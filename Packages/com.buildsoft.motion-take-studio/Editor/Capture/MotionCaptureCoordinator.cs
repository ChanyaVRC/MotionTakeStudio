using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace BuildSoft.MotionTakeStudio.Editor
{
    [DefaultExecutionOrder(32000)]
    internal sealed class MotionCapturePlayerDriver : MonoBehaviour
    {
        private void LateUpdate()
        {
            MotionCaptureCoordinator.PlayerLateUpdate();
        }
    }

    /// <summary>The concrete editor-window bridge for Play Mode VR capture and recovery.</summary>
    [InitializeOnLoad]
    public sealed class MotionCaptureCoordinator :
        IMotionTakeStudioSession,
        IMotionTakeTargetPoseSource,
        IMotionTakeOverlayPoseSource,
        IMotionTakeRawPoseSource,
        IMotionTakeStudioPreviewSession,
        IMotionTakeTrackerRoleSession,
        IMotionTakeValidationSession
    {
        private const float CaptureFrameRate = 60f;
        private const string ArmedKey = "BuildSoft.MotionTakeStudio.Capture.Armed";
        private const string SourceGlobalIdKey = "BuildSoft.MotionTakeStudio.Capture.SourceGlobalId";
        private const string SessionIdKey = "BuildSoft.MotionTakeStudio.Capture.SessionId";
        private const string SourceNameKey = "BuildSoft.MotionTakeStudio.Capture.SourceName";
        private const string ReviewCheckpointPathKey = "BuildSoft.MotionTakeStudio.Capture.ReviewCheckpointPath";
        private const string NdmfApplyOnPlayArmedKey = "BuildSoft.MotionTakeStudio.Capture.NdmfApplyOnPlayArmed";
        private const string ProcessedAvatarConfirmedKey =
            "BuildSoft.MotionTakeStudio.Capture.ProcessedAvatarConfirmed";

        private static readonly MotionCaptureCoordinator Singleton;
        private static readonly IDisposable BridgeRegistration;
        private static Func<string, string, string, string> TakeWriter = MotionTakeAssetWriter.WriteUnique;

        internal sealed class CaptureSamplePlan
        {
            public CaptureSamplePlan(
                IReadOnlyList<double> sampleTimes,
                int droppedSampleCount,
                string warning,
                double nextSampleTime)
            {
                SampleTimes = sampleTimes ?? Array.Empty<double>();
                DroppedSampleCount = droppedSampleCount;
                Warning = warning;
                NextSampleTime = nextSampleTime;
            }

            public IReadOnlyList<double> SampleTimes { get; }
            public int DroppedSampleCount { get; }
            public string Warning { get; }
            public double NextSampleTime { get; }
        }

        private readonly List<MotionTakeValidationIssue> _validationIssues =
            new List<MotionTakeValidationIssue>();
        private readonly List<MotionTakeValidationIssue> _captureIkIssues =
            new List<MotionTakeValidationIssue>();
        private readonly MotionTakeOverlayPoseCache _overlayPoseCache =
            new MotionTakeOverlayPoseCache();

        private GameObject _captureRoot;
        private GameObject _driverRoot;
        private Scene _captureScene;
        private HumanoidAvatarBinding _binding;
        private MotionCaptureRig _captureRig;
        private ITrackerPoseProvider _trackerProvider;
        private CaptureTake _take;
        private MotionTakeAsset _runtimeTake;
        private RecoveryJournal _journal;
        private string _recoveryPath;
        private HumanPose _humanPose;
        private HumanPose _sourceHumanPose;
        private double _recordingStartedRealtime;
        private double _nextSampleTime;
        private bool _animatorWasEnabled;
        private float _animatorSpeed;
        private bool _animatorPaused;
        private MotionEditRecipe _activeRecipe;
        private MotionTakePreviewDriver _previewDriver;
        private MotionTakeOverlayFlags _overlays;
        private MotionTakeSessionPhase _phase;
        private string _statusMessage;
        private int _currentFrame;
        private bool _tearingDown;
        private string _testOwnedSessionId;

        static MotionCaptureCoordinator()
        {
            Singleton = new MotionCaptureCoordinator();
            BridgeRegistration = MotionTakeStudioSessionBridge.Register(Singleton);
            ProcessedAvatarQueue.BindingReady += Singleton.OnBindingReady;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            ProcessedAvatarHooks.Install();

            if (SessionState.GetBool(ArmedKey, false))
            {
                Singleton.SetPhase(
                    MotionTakeSessionPhase.Preparing,
                    "Waiting for Play Mode to create the isolated capture clone.");
            }
            else
            {
                Singleton.SetPhase(MotionTakeSessionPhase.Idle, "Select a Humanoid Animator to begin.");
            }
        }

        private MotionCaptureCoordinator()
        {
            _trackerProvider = new ValveOpenVrTrackerProvider();
        }

        public static MotionCaptureCoordinator Instance => Singleton;

        internal static void SetTakeWriterForTests(Func<string, string, string, string> writer)
        {
            TakeWriter = writer ?? MotionTakeAssetWriter.WriteUnique;
        }

        internal static bool HasUsableCoreTracking(TrackerFrame frame)
        {
            return frame != null &&
                   IsUsableTrackerPose(frame.Find(TrackerRole.Head)) &&
                   IsUsableTrackerPose(frame.Find(TrackerRole.LeftHand)) &&
                   IsUsableTrackerPose(frame.Find(TrackerRole.RightHand));
        }

        internal static bool ShouldStoreResolvedSample(bool rigApplied)
        {
            return rigApplied;
        }

        internal static bool CanReportProcessedAvatarReady(bool ndmfAvailable, bool applyOnPlayEnabled)
        {
            return ndmfAvailable && applyOnPlayEnabled;
        }

        internal static bool CanQueueProcessedAvatar(
            bool processingArmed,
            bool completionConfirmed)
        {
            return processingArmed && completionConfirmed;
        }

        internal static CaptureSamplePlan PlanCaptureSamples(
            double nextSampleTime,
            double elapsed,
            double interval)
        {
            if (double.IsNaN(nextSampleTime) || double.IsInfinity(nextSampleTime) ||
                double.IsNaN(elapsed) || double.IsInfinity(elapsed) ||
                double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0d ||
                elapsed + 1e-8d < nextSampleTime)
            {
                return new CaptureSamplePlan(Array.Empty<double>(), 0, string.Empty, nextSampleTime);
            }

            var dueCount = Math.Max(1L, (long)Math.Floor((elapsed - nextSampleTime + 1e-8d) / interval) + 1L);
            var dropped = dueCount > int.MaxValue ? int.MaxValue : Math.Max(0, (int)dueCount - 1);
            var next = nextSampleTime + dueCount * interval;
            var warning = dropped == 0
                ? string.Empty
                : $"Capture hitch dropped {dropped} historical sample slot(s); the current observation was stored once.";
            return new CaptureSamplePlan(new[] { elapsed }, dropped, warning, next);
        }

        private static bool IsUsableTrackerPose(TrackerPoseSample pose)
        {
            return pose != null && pose.connected && pose.valid &&
                   IsFinite(pose.position.x) && IsFinite(pose.position.y) && IsFinite(pose.position.z) &&
                   IsFinite(pose.rotation.x) && IsFinite(pose.rotation.y) &&
                   IsFinite(pose.rotation.z) && IsFinite(pose.rotation.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public event Action Changed;

        public MotionTakeSessionPhase Phase => _phase;
        public string StatusMessage => _statusMessage;
        public int FrameCount => _take?.frames.Count ?? 0;
        public float FrameRate => CaptureFrameRate;
        public int CurrentFrame => _currentFrame;
        public MotionEditRecipe ActiveRecipe => _activeRecipe;
        public MotionTakePreviewDriver PreviewDriver => _previewDriver;
        public IMotionTakeTargetPoseSource TargetPoseSource => _previewDriver ?? (IMotionTakeTargetPoseSource)this;
        public IMotionTakeOverlayPoseSource OverlayPoseSource => this;
        public IReadOnlyList<MotionTakeValidationIssue> ValidationIssues => _validationIssues;
        public CaptureTake ActiveCapture => _take;
        public MotionTakeAsset ActiveMotionTake => _runtimeTake;
        public ITrackerPoseProvider TrackerProvider => _trackerProvider;
        public string RecoveryPath => _recoveryPath;
        public string TrackerProviderName => _trackerProvider?.DisplayName ?? "None";
        public string TrackerDiagnostic => _trackerProvider?.Diagnostic;
        public IReadOnlyList<TrackedDeviceInfo> TrackedDevices =>
            _trackerProvider?.Devices ?? Array.Empty<TrackedDeviceInfo>();

        public void RefreshTrackedDevices()
        {
            if (_phase == MotionTakeSessionPhase.Recording)
            {
                return;
            }

            var frame = new TrackerFrame();
            var warning = string.Empty;
            _trackerProvider?.TryGetFrame(0d, frame, out warning);
            if (!string.IsNullOrEmpty(warning))
            {
                _statusMessage = warning;
            }

            Changed?.Invoke();
        }

        public void AssignTrackerRole(string deviceId, TrackerRole role)
        {
            if (_phase == MotionTakeSessionPhase.Recording)
            {
                throw new InvalidOperationException("Tracker roles cannot change while recording.");
            }

            _trackerProvider?.AssignRole(deviceId, role);
            RefreshTrackedDevices();
        }

        public void Revalidate()
        {
            if (_phase == MotionTakeSessionPhase.Reviewing)
            {
                _overlayPoseCache.Reset(-1);
                ValidateTake();
                SaveReviewCheckpoint();
                Changed?.Invoke();
            }
        }

        private void SaveReviewCheckpoint()
        {
            if (_take == null || _activeRecipe == null)
            {
                return;
            }

            try
            {
                var path = ReviewRecoveryCheckpoint.Save(_take, _activeRecipe, _currentFrame);
                SessionState.SetString(ReviewCheckpointPathKey, path);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Motion Take Studio could not update the Review checkpoint: " + exception.Message);
            }
        }

        public void SetTrackerProvider(ITrackerPoseProvider provider)
        {
            if (_phase == MotionTakeSessionPhase.Recording)
            {
                throw new InvalidOperationException("The tracker provider cannot change while recording.");
            }

            _trackerProvider?.Dispose();
            _trackerProvider = provider ?? new ValveOpenVrTrackerProvider();
            Changed?.Invoke();
        }

        internal void ArmProcessedAvatarForTests(GameObject processedRoot)
        {
            if (!Application.isPlaying)
            {
                throw new InvalidOperationException(
                    "The processed-avatar test seam can only be armed in Play Mode.");
            }

            if (processedRoot == null)
            {
                throw new ArgumentNullException(nameof(processedRoot));
            }

            var animator = processedRoot.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman || !animator.isHuman)
            {
                throw new ArgumentException(
                    "The processed root must contain an Animator with a valid Humanoid Avatar.",
                    nameof(processedRoot));
            }

            if (_trackerProvider == null || !_trackerProvider.IsAvailable)
            {
                throw new InvalidOperationException(
                    _trackerProvider?.Diagnostic ?? "A usable tracker provider must be injected before arming.");
            }

            TearDownTransientObjects(true);
            DestroyRuntimeTake();
            DestroyRecipe();
            _take = null;
            _validationIssues.Clear();
            _captureIkIssues.Clear();
            _overlayPoseCache.Reset(-1);

            var sessionId = Guid.NewGuid().ToString("N");
            _testOwnedSessionId = sessionId;
            var sourceGlobalId = GlobalObjectId.GetGlobalObjectIdSlow(processedRoot).ToString();
            SessionState.EraseString(ReviewCheckpointPathKey);
            SessionState.SetBool(ArmedKey, true);
            SessionState.SetString(SourceGlobalIdKey, sourceGlobalId);
            SessionState.SetString(SessionIdKey, sessionId);
            SessionState.SetString(SourceNameKey, processedRoot.name);
            SessionState.SetBool(NdmfApplyOnPlayArmedKey, true);
            SessionState.SetBool(ProcessedAvatarConfirmedKey, false);

            _captureScene = SceneManager.CreateScene(
                "Motion Take Studio Test Capture " + sessionId);
            try
            {
                _driverRoot = new GameObject("Motion Take Capture Test Session")
                {
                    hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInBuild
                };
                SceneManager.MoveGameObjectToScene(_driverRoot, _captureScene);
                var marker = _driverRoot.AddComponent<MotionCaptureAvatarMarker>();
                marker.Configure(sessionId, sourceGlobalId);
                _driverRoot.AddComponent<MotionCapturePlayerDriver>();

                processedRoot.transform.SetParent(null, true);
                SceneManager.MoveGameObjectToScene(processedRoot, _captureScene);
                processedRoot.transform.SetParent(_driverRoot.transform, true);
                _captureRoot = processedRoot;

                SetPhase(
                    MotionTakeSessionPhase.Preparing,
                    "Test processed Humanoid is armed; waiting for two stable player frames.");
            }
            catch
            {
                SessionState.SetBool(ArmedKey, false);
                TearDownTransientObjects(true);
                throw;
            }
        }

        internal void ResetForTests()
        {
            var testSessionId = _testOwnedSessionId;
            var journalPath = IsRecoveryFileOwnedByTest(
                    _journal?.Path ?? _recoveryPath,
                    testSessionId)
                ? _journal?.Path ?? _recoveryPath
                : null;
            var checkpointCandidate = SessionState.GetString(ReviewCheckpointPathKey, string.Empty);
            var checkpointPath = IsRecoveryFileOwnedByTest(checkpointCandidate, testSessionId)
                ? checkpointCandidate
                : null;

            TearDownTransientObjects(true);
            DestroyRuntimeTake();
            DestroyRecipe();
            _take = null;
            _validationIssues.Clear();
            _captureIkIssues.Clear();
            _overlayPoseCache.Reset(-1);
            _trackerProvider?.Dispose();
            _trackerProvider = new ValveOpenVrTrackerProvider();
            TakeWriter = MotionTakeAssetWriter.WriteUnique;

            _recoveryPath = null;
            _recordingStartedRealtime = 0d;
            _nextSampleTime = 0d;
            _currentFrame = 0;
            _overlays = MotionTakeOverlayFlags.None;
            _statusMessage = string.Empty;
            _humanPose = default(HumanPose);
            _sourceHumanPose = default(HumanPose);
            _animatorPaused = false;
            _testOwnedSessionId = null;

            SessionState.EraseBool(ArmedKey);
            SessionState.EraseString(SourceGlobalIdKey);
            SessionState.EraseString(SessionIdKey);
            SessionState.EraseString(SourceNameKey);
            SessionState.EraseString(ReviewCheckpointPathKey);
            SessionState.EraseBool(NdmfApplyOnPlayArmedKey);
            SessionState.EraseBool(ProcessedAvatarConfirmedKey);

            DeleteRecoveryFileForTests(journalPath);
            DeleteRecoveryFileForTests(checkpointPath);
            SetPhase(MotionTakeSessionPhase.Idle, "Select a Humanoid Animator to begin.");
        }

        private static void DeleteRecoveryFileForTests(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            var recoveryRoot = Path.GetFullPath(MotionTakeRecovery.RecoveryDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(recoveryRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Delete(candidate);
        }

        internal static bool IsRecoveryFileOwnedByTest(string path, string testSessionId)
        {
            return !string.IsNullOrEmpty(path) &&
                   !string.IsNullOrEmpty(testSessionId) &&
                   Path.GetFileName(path).IndexOf(
                       testSessionId,
                       StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public void PrepareCapture(Animator sourceAvatar)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                throw new InvalidOperationException("Prepare capture from Edit Mode.");
            }

            if (sourceAvatar == null || sourceAvatar.avatar == null ||
                !sourceAvatar.avatar.isValid || !sourceAvatar.avatar.isHuman)
            {
                throw new ArgumentException("Select an Animator with a valid Humanoid Avatar.", nameof(sourceAvatar));
            }

            if (!sourceAvatar.gameObject.scene.IsValid())
            {
                throw new ArgumentException("The capture avatar must be a Scene object, not a prefab asset.",
                    nameof(sourceAvatar));
            }

            if (CaptureConflictDetector.TryFindActiveConflict(out var conflict))
            {
                SetPhase(MotionTakeSessionPhase.Error, conflict);
                return;
            }

            TearDownTransientObjects(false);
            SessionState.EraseString(ReviewCheckpointPathKey);
            SessionState.SetBool(NdmfApplyOnPlayArmedKey, false);
            SessionState.SetBool(ProcessedAvatarConfirmedKey, false);
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(sourceAvatar.gameObject).ToString();
            var sessionId = Guid.NewGuid().ToString("N");
            SessionState.SetBool(ArmedKey, true);
            SessionState.SetString(SourceGlobalIdKey, globalId);
            SessionState.SetString(SessionIdKey, sessionId);
            SessionState.SetString(SourceNameKey, sourceAvatar.gameObject.name);
            CreateEditModeCaptureClone(sourceAvatar.gameObject, sessionId);
            SetPhase(MotionTakeSessionPhase.Preparing, "Entering Play Mode and preparing an additive capture Scene…");
            EditorApplication.isPlaying = true;
        }

        public void BeginRecording()
        {
            if (_phase != MotionTakeSessionPhase.Ready || _binding == null)
            {
                throw new InvalidOperationException("The processed Humanoid clone is not ready.");
            }

            if (CaptureConflictDetector.TryFindActiveConflict(out var conflict))
            {
                SetPhase(MotionTakeSessionPhase.Error, conflict);
                return;
            }

            if (_trackerProvider == null || !_trackerProvider.IsAvailable)
            {
                throw new InvalidOperationException(
                    _trackerProvider?.Diagnostic ?? "No OpenVR tracker provider is available.");
            }

            var readinessFrame = new TrackerFrame();
            if (!_trackerProvider.TryGetFrame(0d, readinessFrame, out var readinessWarning) ||
                !HasUsableCoreTracking(readinessFrame))
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(readinessWarning)
                        ? "Valid Head, Left Hand, and Right Hand tracking is required before recording."
                        : readinessWarning);
            }

            if (!string.IsNullOrEmpty(readinessWarning))
            {
                _statusMessage = readinessWarning;
            }

            RestoreAnimator();
            var sessionId = SessionState.GetString(SessionIdKey, Guid.NewGuid().ToString("N"));
            var sourceName = SessionState.GetString(SourceNameKey, _captureRoot != null ? _captureRoot.name : "Avatar");
            _take = new CaptureTake
            {
                sessionId = sessionId,
                displayName = sourceName + " Take",
                sourceGlobalObjectId = SessionState.GetString(SourceGlobalIdKey, string.Empty),
                sourceName = sourceName,
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                sampleRate = CaptureFrameRate,
                humanScale = Mathf.Max(0.0001f, _binding.Animator.humanScale)
            };
            _validationIssues.Clear();
            _captureIkIssues.Clear();
            _captureRig?.ResetCalibration();
            _currentFrame = 0;
            _recordingStartedRealtime = Time.realtimeSinceStartupAsDouble;
            _nextSampleTime = 0d;
            _journal?.Dispose();
            _journal = new RecoveryJournal(_take);
            _recoveryPath = _journal.Path;
            SetPhase(MotionTakeSessionPhase.Recording, "Recording Humanoid pose and OpenVR tracking at 60 Hz.");
        }

        public void StopAndReview()
        {
            if (_phase != MotionTakeSessionPhase.Recording)
            {
                throw new InvalidOperationException("No recording is active.");
            }

            if (_take == null || _take.frames.Count == 0)
            {
                _journal?.Dispose();
                _journal = null;
                _take = null;
                SetPhase(
                    MotionTakeSessionPhase.Ready,
                    "No frames were captured. Record for at least one player frame before stopping.");
                return;
            }

            var timingWarnings = _take.gapWarnings == null
                ? new List<TrackerGapWarning>()
                : new List<TrackerGapWarning>(_take.gapWarnings);
            _take.gapWarnings = TrackerGapInterpolator.Repair(_take.frames);
            _take.gapWarnings.InsertRange(0, timingWarnings);
            RebuildResolvedPosesAfterGapRepair();
            BuildRuntimeTake();
            _journal?.Complete(_take, Time.realtimeSinceStartupAsDouble);
            _journal?.Dispose();
            _journal = null;
            PauseAnimator();
            CreateRecipe();
            _previewDriver?.Dispose();
            _previewDriver = new MotionTakePreviewDriver();
            _previewDriver.Bind(
                _binding.Animator,
                _runtimeTake,
                _activeRecipe,
                new DelegatingAnimatorPreviewStateGuard(_ => EmptyDisposable.Instance));
            _currentFrame = Mathf.Max(0, FrameCount - 1);
            SaveReviewCheckpoint();
            SetPhase(MotionTakeSessionPhase.Reviewing, "Preparing Review Mode…");
            ScrubToFrame(_currentFrame);
            ValidateTake();

            var warningSuffix = _take.gapWarnings.Count == 0
                ? string.Empty
                : $" {_take.gapWarnings.Count} tracking gap(s) need review.";
            SetPhase(
                MotionTakeSessionPhase.Reviewing,
                $"Review Mode: {FrameCount} frames captured. Animator is paused and will be restored on exit." +
                warningSuffix);
        }

        public void SaveAndExit()
        {
            if (_phase != MotionTakeSessionPhase.Reviewing || _take == null)
            {
                throw new InvalidOperationException("Stop the recording before saving.");
            }

            SetPhase(MotionTakeSessionPhase.Saving, "Saving the durable take source…");
            string assetPath = null;
            try
            {
                assetPath = TakeWriter(
                    "Assets/MotionTakeStudio/Takes",
                    _take.displayName,
                    JsonUtility.ToJson(_take, true));
                PendingCaptureExport.Stage(
                    assetPath,
                    _activeRecipe,
                    _validationIssues,
                    _take,
                    BuildCorrectedFramesForExport());
            }
            catch (Exception exception)
            {
                // The pending payload is the hand-off commit point. If staging did not complete, remove the
                // just-created source so a retry does not silently create duplicate takes.
                if (!string.IsNullOrEmpty(assetPath) &&
                    assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }

                SaveReviewCheckpoint();
                SetPhase(
                    MotionTakeSessionPhase.Reviewing,
                    "Save failed; Review Mode is still active and the operation is retryable. " + exception.Message);
                Debug.LogWarning("Motion Take Studio save remains retryable: " + exception);
                return;
            }

            if (!string.IsNullOrEmpty(_recoveryPath) &&
                !MotionTakeRecovery.Archive(_recoveryPath, out _, out var journalArchiveError))
            {
                Debug.LogWarning("Motion Take Studio kept the completed capture journal: " + journalArchiveError);
            }

            var checkpointPath = SessionState.GetString(ReviewCheckpointPathKey, string.Empty);
            if (!string.IsNullOrEmpty(checkpointPath))
            {
                try
                {
                    ReviewRecoveryCheckpoint.Archive(checkpointPath);
                    SessionState.EraseString(ReviewCheckpointPathKey);
                }
                catch (Exception exception)
                {
                    // The take and pending export are already durable; an ancillary checkpoint archive must not
                    // turn a successful commit into a second retry/export transaction.
                    Debug.LogWarning("Motion Take Studio kept the Review checkpoint: " + exception.Message);
                }
            }

            _statusMessage = "Saved " + assetPath;
            SessionState.SetBool(ArmedKey, false);
            TearDownTransientObjects(true);
            DestroyRuntimeTake();
            DestroyRecipe();
            Changed?.Invoke();
            if (EditorApplication.isPlaying)
            {
                EditorApplication.isPlaying = false;
            }
            else
            {
                SetPhase(MotionTakeSessionPhase.Idle, "Saved " + assetPath);
            }
        }

        public void Cancel()
        {
            SessionState.SetBool(ArmedKey, false);
            TearDownTransientObjects(true);
            _take = null;
            DestroyRuntimeTake();
            _validationIssues.Clear();
            DestroyRecipe();
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.isPlaying = false;
            }

            SetPhase(MotionTakeSessionPhase.Idle, "Capture cancelled. Any started journal remains recoverable.");
        }

        public void ScrubToFrame(int frame)
        {
            if (_take == null || _take.frames.Count == 0 || _binding == null)
            {
                _currentFrame = 0;
                return;
            }

            _currentFrame = Mathf.Clamp(frame, 0, _take.frames.Count - 1);
            _overlayPoseCache.Reset(-1);
            if (_previewDriver != null && _phase == MotionTakeSessionPhase.Reviewing)
            {
                _previewDriver.ApplyFrame(_currentFrame);
                Changed?.Invoke();
                return;
            }

            var sample = _take.frames[_currentFrame];
            _humanPose.bodyPosition = sample.bodyPosition;
            _humanPose.bodyRotation = sample.bodyRotation;
            _humanPose.muscles = sample.muscles == null ? Array.Empty<float>() : (float[])sample.muscles.Clone();
            _binding.PoseHandler.SetHumanPose(ref _humanPose);
            Changed?.Invoke();
        }

        public void SetOverlays(MotionTakeOverlayFlags overlays)
        {
            _overlays = overlays;
            Changed?.Invoke();
        }

        public bool TryGetBaseTargetPose(PoseTarget target, int frame, out MotionTakeTargetPose pose)
        {
            pose = default;
            if (_binding == null || _binding.Animator == null || _take == null || _take.frames.Count == 0)
            {
                return false;
            }

            ScrubToFrame(frame);
            var transform = ResolveTargetTransform(target);
            if (transform == null)
            {
                return false;
            }

            pose = new MotionTakeTargetPose
            {
                AvatarRoot = _binding.Animator.transform,
                WorldPosition = transform.position,
                WorldRotation = transform.rotation,
                HumanScale = Mathf.Max(0.0001f, _binding.Animator.humanScale),
                LimbLength = transform.parent == null
                    ? Mathf.Max(0.0001f, _binding.Animator.humanScale)
                    : Mathf.Max(0.0001f, Vector3.Distance(transform.position, transform.parent.position))
            };
            return true;
        }

        public bool TryGetSolvedTargetPose(
            MotionTakeOverlayFlags stage,
            PoseTarget target,
            int frame,
            out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            if (stage != MotionTakeOverlayFlags.Ik &&
                stage != MotionTakeOverlayFlags.Automatic &&
                stage != MotionTakeOverlayFlags.Manual)
            {
                return false;
            }

            if (!_overlayPoseCache.TryGet(stage, target, frame, out pose) &&
                !BuildOverlayPoseCache(frame))
            {
                return false;
            }

            return _overlayPoseCache.TryGet(stage, target, frame, out pose);
        }

        private bool BuildOverlayPoseCache(int frame)
        {
            if (_previewDriver == null || _binding?.PoseHandler == null ||
                _take == null || frame < 0 || frame >= _take.frames.Count ||
                !_previewDriver.ApplyFrame(frame))
            {
                return false;
            }

            _overlayPoseCache.Reset(frame);
            var found = false;
            foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
            {
                if (_previewDriver.TryGetBaseTargetPose(target, frame, out var automaticPose))
                {
                    _overlayPoseCache.Set(MotionTakeOverlayFlags.Automatic, target, automaticPose);
                    found = true;
                }

                if (_previewDriver.TryGetSolvedTargetPose(target, frame, out var manualPose))
                {
                    _overlayPoseCache.Set(MotionTakeOverlayFlags.Manual, target, manualPose);
                    found = true;
                }
            }

            var captureFrame = _take.frames[frame];
            if (captureFrame.ikMuscles != null &&
                captureFrame.ikMuscles.Length == HumanTrait.MuscleCount)
            {
                var ikPose = new HumanPose
                {
                    bodyPosition = captureFrame.ikBodyPosition,
                    bodyRotation = captureFrame.ikBodyRotation,
                    muscles = (float[])captureFrame.ikMuscles.Clone()
                };
                _binding.PoseHandler.SetHumanPose(ref ikPose);
                foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
                {
                    if (TryCreateCurrentTargetPose(target, frame, out var solvedIkPose))
                    {
                        _overlayPoseCache.Set(MotionTakeOverlayFlags.Ik, target, solvedIkPose);
                    }
                }

                // Stage extraction must not change the authored review pose.
                _previewDriver.ApplyFrame(frame);
            }

            return found;
        }

        private bool TryCreateCurrentTargetPose(
            PoseTarget target,
            int frame,
            out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            var transform = ResolveTargetTransform(target);
            if (transform == null || _binding?.Animator == null)
            {
                return false;
            }

            var limbLength = Mathf.Max(0.0001f, _binding.Animator.humanScale);
            if (_previewDriver != null &&
                _previewDriver.TryGetBaseTargetPose(target, frame, out var basePose))
            {
                limbLength = basePose.LimbLength;
            }

            pose = new MotionTakeTargetPose
            {
                AvatarRoot = _binding.Animator.transform,
                WorldPosition = transform.position,
                WorldRotation = transform.rotation,
                HumanScale = Mathf.Max(0.0001f, _binding.Animator.humanScale),
                LimbLength = limbLength
            };
            return true;
        }

        public bool TryGetRawTargetPose(
            PoseTarget target,
            int frame,
            out Vector3 worldPosition,
            out Quaternion worldRotation)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (_take == null || _captureRig == null || frame < 0 || frame >= _take.frames.Count)
            {
                return false;
            }

            if (!TryMapRawRole(target, out var role))
            {
                return false;
            }

            var sample = _take.frames[frame]?.trackers?.Find(role);
            return _captureRig.TryMapRawPose(sample, out worldPosition, out worldRotation);
        }

        private static bool TryMapRawRole(PoseTarget target, out TrackerRole role)
        {
            switch (target)
            {
                case PoseTarget.Head:
                    role = TrackerRole.Head;
                    return true;
                case PoseTarget.Hips:
                    role = TrackerRole.Waist;
                    return true;
                case PoseTarget.LeftHand:
                    role = TrackerRole.LeftHand;
                    return true;
                case PoseTarget.RightHand:
                    role = TrackerRole.RightHand;
                    return true;
                case PoseTarget.LeftFoot:
                    role = TrackerRole.LeftFoot;
                    return true;
                case PoseTarget.RightFoot:
                    role = TrackerRole.RightFoot;
                    return true;
                case PoseTarget.LeftElbowHint:
                    role = TrackerRole.LeftElbow;
                    return true;
                case PoseTarget.RightElbowHint:
                    role = TrackerRole.RightElbow;
                    return true;
                case PoseTarget.LeftKneeHint:
                    role = TrackerRole.LeftKnee;
                    return true;
                case PoseTarget.RightKneeHint:
                    role = TrackerRole.RightKnee;
                    return true;
                default:
                    role = TrackerRole.Unassigned;
                    return false;
            }
        }

        internal static void PlayerLateUpdate()
        {
            if (Singleton == null)
            {
                return;
            }

            Singleton.TryResumeAfterAssemblyReload();
            ProcessedAvatarQueue.TickPlayerFrame();
            Singleton.SamplePlayerFrame();
        }

        internal static void NotifyProcessedRoot(GameObject root, string source)
        {
            if (Singleton == null || !Singleton.IsExpectedCaptureRoot(root))
            {
                return;
            }

            if (Singleton._phase == MotionTakeSessionPhase.Recording ||
                Singleton._phase == MotionTakeSessionPhase.Reviewing ||
                Singleton._phase == MotionTakeSessionPhase.Saving)
            {
                return;
            }

            SessionState.SetBool(ProcessedAvatarConfirmedKey, true);
            ProcessedAvatarQueue.Enqueue(root, source);
            Singleton.SetPhase(
                MotionTakeSessionPhase.Preparing,
                $"{source} supplied the processed avatar; waiting for two stable player frames.");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySubsystem()
        {
            ProcessedAvatarQueue.Reset();
            ProcessedAvatarHooks.Uninstall();
            ProcessedAvatarHooks.Install();
            Singleton?.ResetPlayReferencesForReload();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    ProcessedAvatarQueue.Reset();
                    Singleton.SetPhase(MotionTakeSessionPhase.Preparing, "Entering Play Mode…");
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    Singleton.BeginPlayCapture();
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    Singleton.TearDownTransientObjects(true);
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    SessionState.SetBool(ArmedKey, false);
                    Singleton.CleanupEditModeCaptureScene();
                    if (PendingCaptureExport.TryFinalize(out var summary, out var exportError))
                    {
                        Singleton.SetPhase(MotionTakeSessionPhase.Idle, summary);
                    }
                    else if (!string.IsNullOrEmpty(exportError))
                    {
                        Singleton.SetPhase(MotionTakeSessionPhase.Error, exportError);
                    }
                    else
                    {
                        Singleton.SetPhase(MotionTakeSessionPhase.Idle, "Select a Humanoid Animator to begin.");
                    }
                    break;
            }
        }

        private static void BeforeAssemblyReload()
        {
            if (Singleton?._phase == MotionTakeSessionPhase.Reviewing ||
                Singleton?._phase == MotionTakeSessionPhase.Saving)
            {
                Singleton.SaveReviewCheckpoint();
            }

            ProcessedAvatarHooks.Uninstall();
            ProcessedAvatarQueue.Reset();
            Singleton?._journal?.Dispose();
            Singleton?._binding?.Dispose();
            Singleton?._trackerProvider?.Dispose();
        }

        private static void OnSceneClosed(Scene scene)
        {
            if (Singleton == null || !Singleton._captureScene.IsValid() ||
                scene.handle != Singleton._captureScene.handle)
            {
                return;
            }

            Singleton.ResetPlayReferencesForReload();
        }

        private void BeginPlayCapture()
        {
            if (!SessionState.GetBool(ArmedKey, false))
            {
                SetPhase(MotionTakeSessionPhase.Idle, "Play Mode was entered without an armed capture.");
                return;
            }

            if (CaptureConflictDetector.TryFindActiveConflict(out var conflict))
            {
                SetPhase(MotionTakeSessionPhase.Error, conflict);
                return;
            }

            if (!TryResolvePreparedCaptureClone())
            {
                Fail("The prepared additive-scene clone could not be restored in Play Mode.");
                return;
            }

            if (_driverRoot.GetComponent<MotionCapturePlayerDriver>() == null)
            {
                _driverRoot.AddComponent<MotionCapturePlayerDriver>();
            }
            SetPhase(
                MotionTakeSessionPhase.Preparing,
                "Additive-scene clone entered Play Mode; waiting for the processed Animator to settle.");
            if (!SessionState.GetBool(NdmfApplyOnPlayArmedKey, false))
            {
                Fail("NDMF Apply on Play was not armed; the unprocessed clone cannot enter capture Ready state.");
                return;
            }

            // The NDMF/VRChat callback must enqueue the exact processed root. Installing an
            // activator proves only that processing was armed, not that processing completed.
            Changed?.Invoke();
        }

        private void TryResumeAfterAssemblyReload()
        {
            if (!Application.isPlaying || !SessionState.GetBool(ArmedKey, false) ||
                _binding != null || _phase != MotionTakeSessionPhase.Preparing)
            {
                return;
            }

            if (!TryResolvePreparedCaptureClone())
            {
                return;
            }

            if (_driverRoot != null && _driverRoot.GetComponent<MotionCapturePlayerDriver>() == null)
            {
                _driverRoot.AddComponent<MotionCapturePlayerDriver>();
            }

            if (!SessionState.GetBool(NdmfApplyOnPlayArmedKey, false))
            {
                Fail("Review reload could not verify the NDMF Apply on Play processing gate.");
                return;
            }

            if (CanQueueProcessedAvatar(
                    SessionState.GetBool(NdmfApplyOnPlayArmedKey, false),
                    SessionState.GetBool(ProcessedAvatarConfirmedKey, false)))
            {
                ProcessedAvatarQueue.Enqueue(_captureRoot, "assembly reload processed avatar rebind");
            }
        }

        private void CreateEditModeCaptureClone(GameObject sourceRoot, string sessionId)
        {
            if (sourceRoot == null)
            {
                throw new ArgumentNullException(nameof(sourceRoot));
            }

            CleanupEditModeCaptureScene();
            var previousActiveScene = SceneManager.GetActiveScene();
            _captureScene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene,
                NewSceneMode.Additive);
            try
            {
                _driverRoot = new GameObject("Motion Take Capture Session")
                {
                    hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInBuild
                };
                SceneManager.MoveGameObjectToScene(_driverRoot, _captureScene);
                var sessionMarker = _driverRoot.AddComponent<MotionCaptureAvatarMarker>();
                sessionMarker.Configure(
                    sessionId,
                    SessionState.GetString(SourceGlobalIdKey, string.Empty));

                _captureRoot = Object.Instantiate(sourceRoot);
                _captureRoot.name = sourceRoot.name + " [Motion Take Capture]";
                _captureRoot.SetActive(false);
                SceneManager.MoveGameObjectToScene(_captureRoot, _captureScene);
                _captureRoot.transform.SetParent(_driverRoot.transform, true);

                var ndmfArmed = ProcessedAvatarHooks.TryInstallNdmfApplyOnPlayActivator(
                        _captureRoot,
                        out var processingWarning);
                SessionState.SetBool(NdmfApplyOnPlayArmedKey, ndmfArmed);
                if (ndmfArmed)
                {
                    _statusMessage = "NDMF Apply on Play is armed on the additive-scene clone.";
                }
                else
                {
                    SessionState.SetBool(ArmedKey, false);
                    throw new InvalidOperationException(
                        string.IsNullOrEmpty(processingWarning)
                            ? "NDMF with Apply on Play enabled is required to create the processed capture clone."
                            : processingWarning);
                }

                _captureRoot.SetActive(true);
            }
            catch
            {
                CleanupEditModeCaptureScene();
                throw;
            }
            finally
            {
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                {
                    SceneManager.SetActiveScene(previousActiveScene);
                }
            }
        }

        private bool TryResolvePreparedCaptureClone()
        {
            if (_driverRoot != null && _captureRoot != null && _captureScene.IsValid())
            {
                return true;
            }

            var expectedSession = SessionState.GetString(SessionIdKey, string.Empty);
            foreach (var marker in Resources.FindObjectsOfTypeAll<MotionCaptureAvatarMarker>())
            {
                if (marker == null || marker.SessionId != expectedSession ||
                    !marker.gameObject.scene.IsValid() || !marker.gameObject.scene.isLoaded)
                {
                    continue;
                }

                var animator = marker.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.isHuman)
                {
                    continue;
                }

                _driverRoot = marker.gameObject;
                _captureScene = marker.gameObject.scene;
                _captureRoot = animator.gameObject;
                return true;
            }

            return false;
        }

        private void CleanupEditModeCaptureScene()
        {
            var scenes = new HashSet<int>();
            var expectedSession = SessionState.GetString(SessionIdKey, string.Empty);
            foreach (var marker in Resources.FindObjectsOfTypeAll<MotionCaptureAvatarMarker>())
            {
                if (marker == null || string.IsNullOrEmpty(expectedSession) ||
                    marker.SessionId != expectedSession)
                {
                    continue;
                }

                var scene = marker.gameObject.scene;
                var matchesKnownScene = !_captureScene.IsValid() || scene.handle == _captureScene.handle;
                if (!scene.IsValid() || !scene.isLoaded || !matchesKnownScene || !scenes.Add(scene.handle))
                {
                    continue;
                }

                if (EditorApplication.isPlaying)
                {
                    SceneManager.UnloadSceneAsync(scene);
                }
                else
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            _captureRoot = null;
            _driverRoot = null;
            _captureScene = default(Scene);
        }

        private void OnBindingReady(HumanoidAvatarBinding binding, string source)
        {
            if (binding == null || !IsExpectedCaptureRoot(binding.Root))
            {
                binding?.Dispose();
                return;
            }

            if (_phase == MotionTakeSessionPhase.Recording ||
                _phase == MotionTakeSessionPhase.Reviewing ||
                _phase == MotionTakeSessionPhase.Saving)
            {
                binding.Dispose();
                return;
            }

            if (!CanQueueProcessedAvatar(
                    SessionState.GetBool(NdmfApplyOnPlayArmedKey, false),
                    SessionState.GetBool(ProcessedAvatarConfirmedKey, false)))
            {
                binding.Dispose();
                Fail("The clone stabilized without an active NDMF Apply on Play processing gate.");
                return;
            }

            _binding?.Dispose();
            _binding = binding;
            _captureRig = new MotionCaptureRig(binding);
            _humanPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            _sourceHumanPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            if (TryRestoreReviewCheckpoint())
            {
                return;
            }

            SetPhase(
                MotionTakeSessionPhase.Ready,
                $"Humanoid ready from {source}. Animator and {binding.Bones.Count} bone references were re-fetched.");
        }

        private bool TryRestoreReviewCheckpoint()
        {
            var checkpointPath = SessionState.GetString(ReviewCheckpointPathKey, string.Empty);
            if (string.IsNullOrEmpty(checkpointPath))
            {
                return false;
            }

            var state = ReviewRecoveryCheckpoint.TryRestore(checkpointPath);
            if (state?.Capture == null || state.Recipe == null)
            {
                return false;
            }

            _take = state.Capture;
            BuildRuntimeTake();
            DestroyRecipe();
            _activeRecipe = state.Recipe;
            _activeRecipe.Initialize(_runtimeTake, _activeRecipe.DisplayName);
            PauseAnimator();
            _previewDriver?.Dispose();
            _previewDriver = new MotionTakePreviewDriver();
            _previewDriver.Bind(
                _binding.Animator,
                _runtimeTake,
                _activeRecipe,
                new DelegatingAnimatorPreviewStateGuard(_ => EmptyDisposable.Instance));
            _currentFrame = Mathf.Clamp(state.CurrentFrame, 0, Mathf.Max(0, FrameCount - 1));
            SetPhase(MotionTakeSessionPhase.Reviewing, "Review Mode restored after assembly reload.");
            ScrubToFrame(_currentFrame);
            ValidateTake();
            SaveReviewCheckpoint();
            return true;
        }

        private void SamplePlayerFrame()
        {
            if (_phase != MotionTakeSessionPhase.Recording || _binding == null || _take == null)
            {
                return;
            }

            if (_binding.Animator == null || _binding.Root == null)
            {
                Fail("The processed capture avatar was replaced or destroyed; recording stopped safely.");
                return;
            }

            var elapsed = Time.realtimeSinceStartupAsDouble - _recordingStartedRealtime;
            var interval = 1d / CaptureFrameRate;
            var plan = PlanCaptureSamples(_nextSampleTime, elapsed, interval);
            _nextSampleTime = plan.NextSampleTime;
            if (plan.SampleTimes.Count == 0)
            {
                return;
            }

            if (plan.DroppedSampleCount > 0)
            {
                _take.gapWarnings.Add(new TrackerGapWarning
                {
                    role = TrackerRole.Unassigned,
                    startTime = Math.Max(0d, elapsed - plan.DroppedSampleCount * interval),
                    duration = plan.DroppedSampleCount * interval,
                    message = plan.Warning
                });
                _statusMessage = plan.Warning;
            }

            var sampleTime = plan.SampleTimes[0];
            _binding.PoseHandler.GetHumanPose(ref _sourceHumanPose);
            var trackerFrame = new TrackerFrame { time = sampleTime };
            if (_trackerProvider != null)
            {
                _trackerProvider.TryGetFrame(sampleTime, trackerFrame, out var providerWarning);
                if (!string.IsNullOrEmpty(providerWarning))
                {
                    _statusMessage = providerWarning;
                }
            }

            _binding.PoseHandler.SetHumanPose(ref _sourceHumanPose);
            var rigApplied = _captureRig != null && _captureRig.Apply(
                trackerFrame,
                _take.frames.Count,
                _captureIkIssues);
            if (!ShouldStoreResolvedSample(rigApplied))
            {
                var unresolved = new HumanoidCaptureFrame
                {
                    time = sampleTime,
                    sourceBodyPosition = _sourceHumanPose.bodyPosition,
                    sourceBodyRotation = _sourceHumanPose.bodyRotation,
                    sourceMuscles = CloneMuscles(_sourceHumanPose.muscles),
                    ikBodyPosition = _sourceHumanPose.bodyPosition,
                    ikBodyRotation = _sourceHumanPose.bodyRotation,
                    ikMuscles = CloneMuscles(_sourceHumanPose.muscles),
                    bodyPosition = _sourceHumanPose.bodyPosition,
                    bodyRotation = _sourceHumanPose.bodyRotation,
                    muscles = CloneMuscles(_sourceHumanPose.muscles),
                    resolved = false,
                    hasFeet = false,
                    trackers = trackerFrame
                };
                AppendCaptureFrame(unresolved);
                _statusMessage = "Waiting for a calibrated, usable Head + Left Hand + Right Hand tracker sample.";
                return;
            }

            _binding.PoseHandler.GetHumanPose(ref _humanPose);
            var frame = new HumanoidCaptureFrame
            {
                time = sampleTime,
                sourceBodyPosition = _sourceHumanPose.bodyPosition,
                sourceBodyRotation = _sourceHumanPose.bodyRotation,
                sourceMuscles = _sourceHumanPose.muscles == null
                    ? Array.Empty<float>()
                    : (float[])_sourceHumanPose.muscles.Clone(),
                bodyPosition = _humanPose.bodyPosition,
                bodyRotation = _humanPose.bodyRotation,
                muscles = _humanPose.muscles == null
                    ? Array.Empty<float>()
                    : (float[])_humanPose.muscles.Clone(),
                resolved = true,
                trackers = trackerFrame
            };
            CaptureFootPositions(frame);
            AppendCaptureFrame(frame);
        }

        private void AppendCaptureFrame(HumanoidCaptureFrame frame)
        {
            _take.frames.Add(frame);
            _journal?.Append(frame, Time.realtimeSinceStartupAsDouble);
            _currentFrame = _take.frames.Count - 1;
            Changed?.Invoke();
        }

        private static float[] CloneMuscles(float[] muscles)
        {
            return muscles == null ? Array.Empty<float>() : (float[])muscles.Clone();
        }

        private void RebuildResolvedPosesAfterGapRepair()
        {
            if (_take == null || _binding?.PoseHandler == null || _captureRig == null)
            {
                return;
            }

            var replayRig = _captureRig.CreateReplayRig();
            var ikReplayRig = _captureRig.CreateIkOnlyReplayRig();
            _captureIkIssues.Clear();
            var sourcePose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            var ikPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            var resolvedPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            var lastResolvedPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            var hasLastResolvedPose = false;
            for (var frameIndex = 0; frameIndex < _take.frames.Count; frameIndex++)
            {
                var frame = _take.frames[frameIndex];
                var sourceMuscles = frame.sourceMuscles != null &&
                                    frame.sourceMuscles.Length == HumanTrait.MuscleCount
                    ? frame.sourceMuscles
                    : frame.muscles;
                sourcePose.bodyPosition = frame.sourceBodyPosition;
                sourcePose.bodyRotation = frame.sourceBodyRotation;
                sourcePose.muscles = sourceMuscles == null
                    ? new float[HumanTrait.MuscleCount]
                    : (float[])sourceMuscles.Clone();

                _binding.PoseHandler.SetHumanPose(ref sourcePose);
                var ikApplied = ikReplayRig.Apply(frame.trackers, frameIndex, null);
                if (ikApplied)
                {
                    _binding.PoseHandler.GetHumanPose(ref ikPose);
                    frame.ikBodyPosition = ikPose.bodyPosition;
                    frame.ikBodyRotation = ikPose.bodyRotation;
                    frame.ikMuscles = CloneMuscles(ikPose.muscles);
                }
                else
                {
                    frame.ikBodyPosition = sourcePose.bodyPosition;
                    frame.ikBodyRotation = sourcePose.bodyRotation;
                    frame.ikMuscles = CloneMuscles(sourcePose.muscles);
                }

                _binding.PoseHandler.SetHumanPose(ref sourcePose);
                var automaticApplied = replayRig.Apply(
                    frame.trackers, frameIndex, _captureIkIssues);
                if (automaticApplied)
                {
                    _binding.PoseHandler.GetHumanPose(ref resolvedPose);
                    frame.bodyPosition = resolvedPose.bodyPosition;
                    frame.bodyRotation = resolvedPose.bodyRotation;
                    frame.muscles = CloneMuscles(resolvedPose.muscles);
                    frame.resolved = true;
                    lastResolvedPose.bodyPosition = resolvedPose.bodyPosition;
                    lastResolvedPose.bodyRotation = resolvedPose.bodyRotation;
                    lastResolvedPose.muscles = CloneMuscles(resolvedPose.muscles);
                    hasLastResolvedPose = true;
                }
                else
                {
                    var fallback = hasLastResolvedPose ? lastResolvedPose : sourcePose;
                    frame.bodyPosition = fallback.bodyPosition;
                    frame.bodyRotation = fallback.bodyRotation;
                    frame.muscles = CloneMuscles(fallback.muscles);
                    frame.resolved = false;
                    _binding.PoseHandler.SetHumanPose(ref fallback);
                }
                CaptureFootPositions(frame);
                // The source pose is only required for this repair pass; do not double the durable take size.
                frame.sourceMuscles = Array.Empty<float>();
            }
        }

        private void CaptureFootPositions(HumanoidCaptureFrame frame)
        {
            if (frame == null || _binding == null ||
                !_binding.TryGetBone(HumanBodyBones.LeftFoot, out var leftFoot) ||
                !_binding.TryGetBone(HumanBodyBones.RightFoot, out var rightFoot))
            {
                if (frame != null)
                {
                    frame.hasFeet = false;
                }

                return;
            }

            frame.hasFeet = true;
            frame.leftFootPosition = leftFoot.position;
            frame.rightFootPosition = rightFoot.position;
        }

        private void PauseAnimator()
        {
            if (_binding?.Animator == null || _animatorPaused)
            {
                return;
            }

            _animatorWasEnabled = _binding.Animator.enabled;
            _animatorSpeed = _binding.Animator.speed;
            _binding.Animator.speed = 0f;
            _binding.Animator.enabled = false;
            _animatorPaused = true;
        }

        private void RestoreAnimator()
        {
            if (!_animatorPaused)
            {
                return;
            }

            if (_binding?.Animator != null)
            {
                _binding.Animator.speed = _animatorSpeed;
                _binding.Animator.enabled = _animatorWasEnabled;
            }

            _animatorPaused = false;
        }

        private void CreateRecipe()
        {
            DestroyRecipe();
            _activeRecipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            _activeRecipe.name = (_take?.displayName ?? "Motion Take") + " Corrections";
            _activeRecipe.hideFlags = HideFlags.HideAndDontSave;
            _activeRecipe.Initialize(_runtimeTake, (_take?.displayName ?? "Motion Take") + " Corrections");
        }

        private void DestroyRecipe()
        {
            if (_activeRecipe != null)
            {
                Object.DestroyImmediate(_activeRecipe);
                _activeRecipe = null;
            }
        }

        private void BuildRuntimeTake()
        {
            DestroyRuntimeTake();
            if (_take == null)
            {
                return;
            }

            _runtimeTake = ScriptableObject.CreateInstance<MotionTakeAsset>();
            _runtimeTake.name = _take.displayName;
            _runtimeTake.hideFlags = HideFlags.HideAndDontSave;
            _runtimeTake.Initialize(
                _take.displayName,
                _take.sessionId,
                _take.sampleRate,
                _take.humanScale,
                _take.sourceGlobalObjectId);
            for (var frameIndex = 0; frameIndex < _take.frames.Count; frameIndex++)
            {
                var source = _take.frames[frameIndex];
                var target = new MotionTakeFrame(
                    frameIndex,
                    source.time,
                    new MotionHumanPoseSample(source.bodyPosition, source.bodyRotation, source.muscles));
                var anyInterpolated = false;
                if (source.trackers?.poses != null)
                {
                    foreach (var pose in source.trackers.poses)
                    {
                        if (pose == null)
                        {
                            continue;
                        }

                        var state = pose.interpolated
                            ? MotionTrackingState.Interpolated
                            : pose.valid
                                ? MotionTrackingState.Valid
                                : pose.connected
                                    ? MotionTrackingState.Lost
                                    : MotionTrackingState.Unavailable;
                        anyInterpolated |= pose.interpolated;
                        target.SetTrackerPose(new MotionTrackerPoseSample(
                            (MotionTrackerRole)(int)pose.role,
                            pose.deviceId,
                            state,
                            pose.position,
                            pose.rotation,
                            pose.velocity,
                            pose.angularVelocity));
                    }
                }

                target.TrackingWasInterpolated = anyInterpolated;
                _runtimeTake.AddOrReplaceFrame(target);
            }
        }

        internal List<HumanoidCaptureFrame> BuildCorrectedFramesForExport()
        {
            var corrected = new List<HumanoidCaptureFrame>();
            if (_take == null || _binding?.PoseHandler == null)
            {
                return corrected;
            }

            var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            for (var frameIndex = 0; frameIndex < _take.frames.Count; frameIndex++)
            {
                if (_previewDriver != null)
                {
                    _previewDriver.ApplyFrame(frameIndex);
                }
                else
                {
                    var source = _take.frames[frameIndex];
                    pose.bodyPosition = source.bodyPosition;
                    pose.bodyRotation = source.bodyRotation;
                    pose.muscles = source.muscles == null
                        ? Array.Empty<float>()
                        : (float[])source.muscles.Clone();
                    _binding.PoseHandler.SetHumanPose(ref pose);
                }

                _binding.PoseHandler.GetHumanPose(ref pose);
                corrected.Add(new HumanoidCaptureFrame
                {
                    time = _take.frames[frameIndex].time,
                    bodyPosition = pose.bodyPosition,
                    bodyRotation = pose.bodyRotation,
                    muscles = pose.muscles == null
                        ? Array.Empty<float>()
                        : (float[])pose.muscles.Clone()
                });
            }

            return corrected;
        }

        private void DestroyRuntimeTake()
        {
            if (_runtimeTake == null)
            {
                return;
            }

            Object.DestroyImmediate(_runtimeTake);
            _runtimeTake = null;
        }

        private IMotionTakeValidationSource BuildCorrectedValidationSource()
        {
            if (_take == null || _binding?.PoseHandler == null || _previewDriver == null)
            {
                return _take == null ? null : new ValidationSource(_take, _captureRig);
            }

            var samples = new List<MotionTakeValidationSample>(_take.frames.Count);
            var restoreFrame = Mathf.Clamp(_currentFrame, 0, Mathf.Max(0, _take.frames.Count - 1));
            var humanPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            try
            {
                for (var frameIndex = 0; frameIndex < _take.frames.Count; frameIndex++)
                {
                    if (!_previewDriver.ApplyFrame(frameIndex))
                    {
                        continue;
                    }

                    _binding.PoseHandler.GetHumanPose(ref humanPose);
                    var hasLeftFoot = _binding.TryGetBone(
                        HumanBodyBones.LeftFoot, out var leftFoot);
                    var hasRightFoot = _binding.TryGetBone(
                        HumanBodyBones.RightFoot, out var rightFoot);
                    var hasFeet = hasLeftFoot && hasRightFoot;
                    samples.Add(new MotionTakeValidationSample
                    {
                        Frame = frameIndex,
                        RootPosition = humanPose.bodyPosition * Mathf.Max(0.0001f, _take.humanScale),
                        RootRotation = humanPose.bodyRotation,
                        Muscles = humanPose.muscles == null
                            ? Array.Empty<float>()
                            : (float[])humanPose.muscles.Clone(),
                        BendDirections = CaptureBendDirections(),
                        IkWarnings = _previewDriver.LastIkWarnings == null
                            ? Array.Empty<string>()
                            : _previewDriver.LastIkWarnings.ToArray(),
                        HasRoot = true,
                        HasFeet = hasFeet,
                        LeftFootPosition = hasFeet ? leftFoot.position : Vector3.zero,
                        RightFootPosition = hasFeet ? rightFoot.position : Vector3.zero,
                        FloorHeight = _captureRig != null ? _captureRig.FloorHeight : 0f,
                        TrackingAvailable = HasUsableCoreTracking(_take.frames[frameIndex].trackers)
                    });
                }
            }
            finally
            {
                if (_take.frames.Count > 0)
                {
                    _previewDriver.ApplyFrame(restoreFrame);
                }
            }

            return new BufferedValidationSource(_take.sampleRate, samples);
        }

        private Vector3[] CaptureBendDirections()
        {
            return new[]
            {
                ResolveBendDirection(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                    HumanBodyBones.LeftHand),
                ResolveBendDirection(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                    HumanBodyBones.RightHand),
                ResolveBendDirection(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
                    HumanBodyBones.LeftFoot),
                ResolveBendDirection(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                    HumanBodyBones.RightFoot)
            };
        }

        private Vector3 ResolveBendDirection(
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones tipBone)
        {
            if (!_binding.TryGetBone(upperBone, out var upper) ||
                !_binding.TryGetBone(lowerBone, out var lower) ||
                !_binding.TryGetBone(tipBone, out var tip))
            {
                return Vector3.zero;
            }

            var axis = tip.position - upper.position;
            var bend = Vector3.ProjectOnPlane(lower.position - upper.position, axis);
            return bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.zero;
        }

        private void ValidateTake()
        {
            _validationIssues.Clear();
            if (_take == null)
            {
                return;
            }

            _validationIssues.AddRange(_captureIkIssues);
            var correctedSource = BuildCorrectedValidationSource();
            if (correctedSource != null)
            {
                _validationIssues.AddRange(MotionTakeValidationEngine.Validate(correctedSource));
            }
            foreach (var gap in _take.gapWarnings)
            {
                var firstFrame = Mathf.Clamp(
                    Mathf.RoundToInt((float)(gap.startTime * CaptureFrameRate)),
                    0,
                    Mathf.Max(0, FrameCount - 1));
                var lastFrame = Mathf.Clamp(
                    Mathf.CeilToInt((float)((gap.startTime + gap.duration) * CaptureFrameRate)),
                    firstFrame,
                    Mathf.Max(firstFrame, FrameCount - 1));
                _validationIssues.Add(new MotionTakeValidationIssue(
                    MotionTakeValidationKind.TrackingGap,
                    MotionTakeValidationSeverity.Warning,
                    firstFrame,
                    gap.message,
                    lastFrame));
            }
        }

        private Transform ResolveTargetTransform(PoseTarget target)
        {
            var name = target.ToString();
            if (name.IndexOf("Root", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return _binding.Animator.transform;
            }

            foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone ||
                    !string.Equals(bone.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return _binding.TryGetBone(bone, out var transform) ? transform : null;
            }

            if (name.IndexOf("Hip", StringComparison.OrdinalIgnoreCase) >= 0 &&
                _binding.TryGetBone(HumanBodyBones.Hips, out var hips))
            {
                return hips;
            }

            return null;
        }

        private bool IsExpectedCaptureRoot(GameObject root)
        {
            if (root == null || _captureRoot == null)
            {
                return false;
            }

            return root == _captureRoot ||
                   root.transform.IsChildOf(_captureRoot.transform) ||
                   _captureRoot.transform.IsChildOf(root.transform) ||
                   HasMatchingRuntimeMarker(root);
        }

        private void AddRuntimeMarker(GameObject root)
        {
            var markerType = FindType("BuildSoft.MotionTakeStudio.MotionCaptureAvatarMarker");
            if (markerType == null || !typeof(Component).IsAssignableFrom(markerType))
            {
                return;
            }

            var marker = root.GetComponent(markerType) ?? root.AddComponent(markerType);
            var configure = markerType.GetMethod("Configure", BindingFlags.Instance | BindingFlags.Public);
            configure?.Invoke(marker, new object[]
            {
                SessionState.GetString(SessionIdKey, string.Empty),
                SessionState.GetString(SourceGlobalIdKey, string.Empty)
            });
        }

        private bool HasMatchingRuntimeMarker(GameObject root)
        {
            var markerType = FindType("BuildSoft.MotionTakeStudio.MotionCaptureAvatarMarker");
            if (markerType == null)
            {
                return false;
            }

            var marker = root.GetComponentInChildren(markerType, true);
            if (marker == null)
            {
                return false;
            }

            var property = markerType.GetProperty("SessionId", BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(marker, null) as string == SessionState.GetString(SessionIdKey, string.Empty);
        }

        private void ResetPlayReferencesForReload()
        {
            _previewDriver?.Dispose();
            _previewDriver = null;
            RestoreAnimator();
            _binding?.Dispose();
            _binding = null;
            _captureRig = null;
            _captureRoot = null;
            _driverRoot = null;
            _captureScene = default;
            _humanPose = default;
            _sourceHumanPose = default;
            _animatorPaused = false;
        }

        private void TearDownTransientObjects(bool closeJournal)
        {
            if (_tearingDown)
            {
                return;
            }

            _tearingDown = true;
            try
            {
                _previewDriver?.Dispose();
                _previewDriver = null;
                RestoreAnimator();
                if (closeJournal)
                {
                    _journal?.Dispose();
                    _journal = null;
                }

                _binding?.Dispose();
                _binding = null;
                _captureRig = null;
                ProcessedAvatarQueue.Reset();
                if (Application.isPlaying)
                {
                    if (_driverRoot != null)
                    {
                        Object.Destroy(_driverRoot);
                    }
                    else if (_captureRoot != null)
                    {
                        Object.Destroy(_captureRoot);
                    }

                    if (_captureScene.IsValid() && _captureScene.isLoaded)
                    {
                        SceneManager.UnloadSceneAsync(_captureScene);
                    }
                }
                else if (_captureScene.IsValid() && _captureScene.isLoaded)
                {
                    EditorSceneManager.CloseScene(_captureScene, true);
                }
                else
                {
                    if (_driverRoot != null)
                    {
                        Object.DestroyImmediate(_driverRoot);
                    }
                    else if (_captureRoot != null)
                    {
                        Object.DestroyImmediate(_captureRoot);
                    }
                }

                _captureRoot = null;
                _driverRoot = null;
                _captureScene = default;
                _humanPose = default;
                _sourceHumanPose = default;
                _animatorPaused = false;
            }
            finally
            {
                _tearingDown = false;
            }
        }

        private void Fail(string message)
        {
            _journal?.Dispose();
            _journal = null;
            SetPhase(MotionTakeSessionPhase.Error, message);
        }

        private void SetPhase(MotionTakeSessionPhase phase, string message)
        {
            _phase = phase;
            _statusMessage = message;
            Changed?.Invoke();
        }

        private static Type FindType(string fullName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(type => type != null);
        }

        private sealed class ValidationSource : IMotionTakeValidationSource
        {
            private readonly CaptureTake _take;
            private readonly MotionCaptureRig _rig;

            public ValidationSource(CaptureTake take, MotionCaptureRig rig)
            {
                _take = take;
                _rig = rig;
            }

            public int FrameCount => _take.frames.Count;
            public float FrameRate => _take.sampleRate;

            public bool TryGetValidationSample(int index, out MotionTakeValidationSample sample)
            {
                sample = default;
                if (index < 0 || index >= _take.frames.Count)
                {
                    return false;
                }

                var frame = _take.frames[index];
                sample = new MotionTakeValidationSample
                {
                    Frame = index,
                    RootPosition = frame.bodyPosition * Mathf.Max(0.0001f, _take.humanScale),
                    RootRotation = frame.bodyRotation,
                    Muscles = frame.muscles,
                    HasRoot = true,
                    HasFeet = frame.hasFeet,
                    LeftFootPosition = frame.leftFootPosition,
                    RightFootPosition = frame.rightFootPosition,
                    FloorHeight = _rig != null ? _rig.FloorHeight : 0f,
                    TrackingAvailable = HasCoreTracking(frame)
                };
                return true;
            }

            private static bool HasCoreTracking(HumanoidCaptureFrame frame)
            {
                if (frame?.trackers == null)
                {
                    return false;
                }

                return IsUsable(frame.trackers.Find(TrackerRole.Head)) &&
                       IsUsable(frame.trackers.Find(TrackerRole.LeftHand)) &&
                       IsUsable(frame.trackers.Find(TrackerRole.RightHand));
            }

            private static bool IsUsable(TrackerPoseSample pose)
            {
                return pose != null && pose.connected && pose.valid;
            }

        }

        private sealed class BufferedValidationSource : IMotionTakeValidationSource
        {
            private readonly IReadOnlyList<MotionTakeValidationSample> _samples;

            public BufferedValidationSource(
                float frameRate,
                IReadOnlyList<MotionTakeValidationSample> samples)
            {
                FrameRate = Mathf.Max(0.001f, frameRate);
                _samples = samples ?? Array.Empty<MotionTakeValidationSample>();
            }

            public int FrameCount => _samples.Count;
            public float FrameRate { get; }

            public bool TryGetValidationSample(int index, out MotionTakeValidationSample sample)
            {
                if (index < 0 || index >= _samples.Count)
                {
                    sample = default(MotionTakeValidationSample);
                    return false;
                }

                sample = _samples[index];
                return true;
            }
        }

        private sealed class EmptyDisposable : IDisposable
        {
            public static readonly EmptyDisposable Instance = new EmptyDisposable();

            public void Dispose()
            {
            }
        }
    }

}

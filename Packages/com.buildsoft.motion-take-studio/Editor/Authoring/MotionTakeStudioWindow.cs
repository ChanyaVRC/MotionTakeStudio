using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public sealed class MotionTakeStudioWindow : EditorWindow
    {
        private const int DefaultInfluenceFrames = 12;

        [SerializeField] private Animator _sourceAvatar;
        [SerializeField] private int _reviewFrame;
        [SerializeField] private PoseTarget _selectedTarget;
        [SerializeField] private int _influenceFrames = DefaultInfluenceFrames;
        [SerializeField] private MotionTakeOverlayFlags _overlays =
            MotionTakeOverlayFlags.Ik | MotionTakeOverlayFlags.Automatic | MotionTakeOverlayFlags.Manual;
        [SerializeField] private Vector2 _scroll;

        private IMotionTakeStudioSession _session;
        private MotionTakeSceneHandleController _sceneHandles;
        private string _operationError;

        [MenuItem("Tools/BuildSoft/Motion Take Studio")]
        public static void Open()
        {
            var window = GetWindow<MotionTakeStudioWindow>();
            window.titleContent = new GUIContent("Motion Take Studio");
            window.minSize = new Vector2(420f, 560f);
            window.Show();
        }

        private void OnEnable()
        {
            _influenceFrames = Mathf.Clamp(
                _influenceFrames <= 0 ? DefaultInfluenceFrames : _influenceFrames,
                1,
                60);
            _sceneHandles = new MotionTakeSceneHandleController(OnAuthoringChanged);
            MotionTakeStudioSessionBridge.CurrentChanged += OnSessionBridgeChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            BindSession(MotionTakeStudioSessionBridge.Current);
        }

        private void OnDisable()
        {
            MotionTakeStudioSessionBridge.CurrentChanged -= OnSessionBridgeChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            BindSession(null);
            _sceneHandles?.Dispose();
            _sceneHandles = null;
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawAvatarSelection();
            EditorGUILayout.Space(6f);
            DrawSessionStatus();
            DrawSessionControls();

            if (_session != null && _session.Phase == MotionTakeSessionPhase.Reviewing)
            {
                EditorGUILayout.Space(10f);
                DrawReviewControls();
                EditorGUILayout.Space(10f);
                DrawAuthoringControls();
                EditorGUILayout.Space(10f);
                DrawValidationIssues();
            }

            if (_session != null &&
                (_session.Phase == MotionTakeSessionPhase.Ready ||
                 _session.Phase == MotionTakeSessionPhase.Recording))
            {
                EditorGUILayout.Space(10f);
                DrawTrackerRoles();
            }

            EditorGUILayout.EndScrollView();
            BindSceneHandles();
        }

        private void DrawAvatarSelection()
        {
            EditorGUILayout.LabelField("Capture Avatar", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(_session != null &&
                                               _session.Phase != MotionTakeSessionPhase.Idle &&
                                               _session.Phase != MotionTakeSessionPhase.Error))
            {
                _sourceAvatar = (Animator)EditorGUILayout.ObjectField(
                    "Humanoid Animator",
                    _sourceAvatar,
                    typeof(Animator),
                    true);
            }

            if (_sourceAvatar != null &&
                (_sourceAvatar.avatar == null || !_sourceAvatar.avatar.isValid || !_sourceAvatar.avatar.isHuman))
            {
                EditorGUILayout.HelpBox("Select an Animator with a valid Humanoid Avatar.", MessageType.Error);
            }
        }

        private void DrawSessionStatus()
        {
            EditorGUILayout.LabelField("Session", EditorStyles.boldLabel);
            if (_session == null)
            {
                EditorGUILayout.HelpBox(
                    "Capture is not connected yet. The capture coordinator registers through " +
                    "MotionTakeStudioSessionBridge.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField("State", ObjectNames.NicifyVariableName(_session.Phase.ToString()));
            var status = string.IsNullOrWhiteSpace(_operationError)
                ? _session.StatusMessage
                : _operationError;
            if (!string.IsNullOrWhiteSpace(status))
            {
                EditorGUILayout.HelpBox(
                    status,
                    string.IsNullOrWhiteSpace(_operationError) ? MessageType.None : MessageType.Error);
            }
        }

        private void DrawSessionControls()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!CanPrepare()))
                {
                    if (GUILayout.Button("Prepare Play Capture"))
                    {
                        InvokeSession(() => _session.PrepareCapture(_sourceAvatar));
                    }
                }

                using (new EditorGUI.DisabledScope(_session == null ||
                                                   _session.Phase != MotionTakeSessionPhase.Ready))
                {
                    if (GUILayout.Button("Record"))
                    {
                        InvokeSession(_session.BeginRecording);
                    }
                }

                using (new EditorGUI.DisabledScope(_session == null ||
                                                   _session.Phase != MotionTakeSessionPhase.Recording))
                {
                    if (GUILayout.Button("Stop & Review"))
                    {
                        InvokeSession(_session.StopAndReview);
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_session == null ||
                                                   _session.Phase != MotionTakeSessionPhase.Reviewing))
                {
                    if (GUILayout.Button("Save & Exit"))
                    {
                        InvokeSession(_session.SaveAndExit);
                    }
                }

                using (new EditorGUI.DisabledScope(_session == null ||
                                                   _session.Phase == MotionTakeSessionPhase.Idle))
                {
                    if (GUILayout.Button("Cancel"))
                    {
                        InvokeSession(_session.Cancel);
                    }
                }
            }
        }

        private void DrawReviewControls()
        {
            EditorGUILayout.LabelField("Review", EditorStyles.boldLabel);
            var maximumFrame = Mathf.Max(0, (_session?.FrameCount ?? 0) - 1);
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                _reviewFrame = EditorGUILayout.IntSlider("Frame", _reviewFrame, 0, maximumFrame);
                if (check.changed)
                {
                    ScrubToFrame(_reviewFrame);
                }
            }

            var frameRate = Mathf.Max(0f, _session?.FrameRate ?? 0f);
            EditorGUILayout.LabelField(
                "Time",
                frameRate > 0f ? $"{_reviewFrame / frameRate:0.000} s" : "—");

            using (var check = new EditorGUI.ChangeCheckScope())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    _overlays = DrawOverlayToggle(_overlays, MotionTakeOverlayFlags.Raw, "Raw");
                    _overlays = DrawOverlayToggle(_overlays, MotionTakeOverlayFlags.Ik, "IK");
                    _overlays = DrawOverlayToggle(_overlays, MotionTakeOverlayFlags.Automatic, "Auto");
                    _overlays = DrawOverlayToggle(_overlays, MotionTakeOverlayFlags.Manual, "Manual");
                }

                if (check.changed && _session != null)
                {
                    InvokeSession(() => _session.SetOverlays(_overlays));
                }
            }
        }

        private void DrawTrackerRoles()
        {
            if (!(_session is IMotionTakeTrackerRoleSession trackerSession))
            {
                return;
            }

            EditorGUILayout.LabelField("OpenVR Tracker Roles", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Provider", trackerSession.TrackerProviderName ?? "Unknown");
            if (!string.IsNullOrWhiteSpace(trackerSession.TrackerDiagnostic))
            {
                EditorGUILayout.HelpBox(trackerSession.TrackerDiagnostic, MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(_session.Phase == MotionTakeSessionPhase.Recording))
            {
                if (GUILayout.Button("Refresh Tracked Devices"))
                {
                    InvokeSession(trackerSession.RefreshTrackedDevices);
                }

                var devices = trackerSession.TrackedDevices;
                if (devices == null || devices.Count == 0)
                {
                    EditorGUILayout.HelpBox(
                        "Start SteamVR, then refresh. For six-point tracking assign Waist, Left Foot, and Right Foot explicitly.",
                        MessageType.None);
                    return;
                }

                foreach (var device in devices)
                {
                    if (device == null)
                    {
                        continue;
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            string.IsNullOrEmpty(device.Id) ? $"Device {device.Index}" : device.Id,
                            GUILayout.MinWidth(160f));
                        var role = (TrackerRole)EditorGUILayout.EnumPopup(device.Role, GUILayout.Width(120f));
                        if (role != device.Role)
                        {
                            var deviceId = device.Id;
                            InvokeSession(() => trackerSession.AssignTrackerRole(deviceId, role));
                        }
                    }
                }
            }
        }

        private void DrawAuthoringControls()
        {
            EditorGUILayout.LabelField("Pose Authoring", EditorStyles.boldLabel);
            _selectedTarget = (PoseTarget)EditorGUILayout.EnumPopup("Target", _selectedTarget);
            _influenceFrames = EditorGUILayout.IntSlider(
                "Influence (frames)",
                _influenceFrames,
                1,
                60);

            var recipe = _session?.ActiveRecipe;
            if (recipe == null)
            {
                EditorGUILayout.HelpBox("No correction recipe is active for this take.", MessageType.Info);
                return;
            }

            var previewWarnings = ResolvePreviewDriver()?.LastIkWarnings;
            if (previewWarnings != null)
            {
                foreach (var warning in previewWarnings)
                {
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Pose Key"))
                {
                    MotionTakeCorrectionAuthoring.AddPoseKey(
                        recipe,
                        ResolvePoseSource(),
                        _selectedTarget,
                        _reviewFrame,
                        _influenceFrames);
                    OnAuthoringChanged();
                }

                using (new EditorGUI.DisabledScope(
                           !MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, _reviewFrame)))
                {
                    if (GUILayout.Button("Reset Target"))
                    {
                        MotionTakeCorrectionAuthoring.ResetTarget(recipe, _selectedTarget, _reviewFrame);
                        OnAuthoringChanged();
                    }

                    if (GUILayout.Button("Delete Key"))
                    {
                        MotionTakeCorrectionAuthoring.DeleteKey(recipe, _reviewFrame);
                        OnAuthoringChanged();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Undo"))
                {
                    Undo.PerformUndo();
                }

                if (GUILayout.Button("Redo"))
                {
                    Undo.PerformRedo();
                }
            }

            EditorGUILayout.HelpBox(
                MotionTakeCorrectionAuthoring.SupportsRotation(_selectedTarget)
                    ? "Use the Scene View position and rotation handles. Corrections are stored relative to the base pose."
                    : "Elbow and knee targets use a position-only hint handle; rotation is intentionally disabled.",
                MessageType.None);
        }

        private void DrawValidationIssues()
        {
            EditorGUILayout.LabelField("Validation", EditorStyles.boldLabel);
            var issues = _session?.ValidationIssues;
            if (issues == null || issues.Count == 0)
            {
                EditorGUILayout.HelpBox("No validation issues.", MessageType.Info);
                return;
            }

            foreach (var issue in issues)
            {
                if (issue == null)
                {
                    continue;
                }

                var icon = issue.Severity == MotionTakeValidationSeverity.Error
                    ? EditorGUIUtility.IconContent("console.erroricon.sml")
                    : EditorGUIUtility.IconContent("console.warnicon.sml");
                var range = issue.EndFrame > issue.Frame
                    ? $"Frames {issue.Frame}–{issue.EndFrame}"
                    : $"Frame {issue.Frame}";
                var label = new GUIContent($"{range}: {issue.Message}", icon.image);
                if (GUILayout.Button(label, EditorStyles.miniButton))
                {
                    ScrubToFrame(issue.Frame);
                }
            }
        }

        private void BindSceneHandles()
        {
            var reviewing = _session != null &&
                            _session.Phase == MotionTakeSessionPhase.Reviewing &&
                            _session.ActiveRecipe != null &&
                            ResolvePoseSource() != null;
            _sceneHandles?.Bind(
                _session?.ActiveRecipe,
                ResolvePoseSource(),
                _session?.OverlayPoseSource,
                _session as IMotionTakeRawPoseSource,
                _selectedTarget,
                _reviewFrame,
                _influenceFrames,
                _overlays,
                reviewing);
        }

        private IMotionTakeTargetPoseSource ResolvePoseSource()
        {
            return _session?.TargetPoseSource ?? ResolvePreviewDriver();
        }

        private MotionTakePreviewDriver ResolvePreviewDriver()
        {
            return (_session as IMotionTakeStudioPreviewSession)?.PreviewDriver;
        }

        private bool CanPrepare()
        {
            if (_session == null || _sourceAvatar == null || _sourceAvatar.avatar == null ||
                !_sourceAvatar.avatar.isValid || !_sourceAvatar.avatar.isHuman)
            {
                return false;
            }

            return _session.Phase == MotionTakeSessionPhase.Idle ||
                   _session.Phase == MotionTakeSessionPhase.Error;
        }

        private void ScrubToFrame(int frame)
        {
            _reviewFrame = Mathf.Clamp(frame, 0, Mathf.Max(0, (_session?.FrameCount ?? 1) - 1));
            if (_session != null)
            {
                InvokeSession(() =>
                {
                    _session.ScrubToFrame(_reviewFrame);
                });
            }

            SceneView.RepaintAll();
        }

        private void OnSessionBridgeChanged()
        {
            BindSession(MotionTakeStudioSessionBridge.Current);
            Repaint();
        }

        private void BindSession(IMotionTakeStudioSession session)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            if (_session != null)
            {
                _session.Changed -= OnSessionChanged;
            }

            _session = session;
            if (_session != null)
            {
                _session.Changed += OnSessionChanged;
                _reviewFrame = Mathf.Clamp(_session.CurrentFrame, 0, Mathf.Max(0, _session.FrameCount - 1));
                InvokeSession(() => _session.SetOverlays(_overlays));
            }
        }

        private void OnSessionChanged()
        {
            if (_session != null)
            {
                _reviewFrame = Mathf.Clamp(_session.CurrentFrame, 0, Mathf.Max(0, _session.FrameCount - 1));
            }

            Repaint();
            SceneView.RepaintAll();
        }

        private void OnAuthoringChanged()
        {
            _operationError = null;
            ScrubToFrame(_reviewFrame);
            if (_session is IMotionTakeValidationSession validationSession)
            {
                InvokeSession(validationSession.Revalidate);
            }
            Repaint();
        }

        private void OnUndoRedo()
        {
            OnAuthoringChanged();
        }

        private void InvokeSession(Action action)
        {
            _operationError = null;
            try
            {
                action?.Invoke();
            }
            catch (Exception exception)
            {
                _operationError = exception.Message;
                Debug.LogException(exception);
            }

            Repaint();
        }

        private static MotionTakeOverlayFlags DrawOverlayToggle(
            MotionTakeOverlayFlags value,
            MotionTakeOverlayFlags flag,
            string label)
        {
            var enabled = (value & flag) != 0;
            enabled = GUILayout.Toggle(enabled, label, EditorStyles.miniButton);
            return enabled ? value | flag : value & ~flag;
        }
    }
}

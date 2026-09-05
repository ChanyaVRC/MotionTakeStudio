using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public sealed class MotionTakeStudioWindow : EditorWindow
    {
        private const int DefaultInfluenceFrames = 12;
        private static readonly string[] WorkflowLabels = { "1  Setup", "2  Capture", "3  Review" };

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
        private MotionTakeStudioStyles _styles;
        private bool _stylesUseDarkTheme;
        private readonly GUIContent _trackerLabel = new GUIContent();

        [MenuItem("Tools/BuildSoft/Motion Take Studio")]
        public static void Open()
        {
            var window = GetWindow<MotionTakeStudioWindow>();
            window.titleContent = new GUIContent(
                "Motion Take Studio",
                EditorGUIUtility.IconContent("Animation.Record").image);
            window.minSize = new Vector2(460f, 620f);
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
            _styles?.Dispose();
            _styles = null;
        }

        private void OnGUI()
        {
            EnsureStyles();
            var presentation = _session == null
                ? MotionTakeStudioPresentation.Disconnected
                : MotionTakeStudioPresentation.ForPhase(_session.Phase);

            EditorGUI.DrawRect(new Rect(Vector2.zero, position.size), _styles.Palette.Window);
            _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            EditorGUILayout.Space(12f);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawHero(presentation);
                    EditorGUILayout.Space(8f);
                    DrawWorkflowRail(presentation.ActiveStep);
                    EditorGUILayout.Space(8f);
                    DrawSessionCard(presentation);

                    if (presentation.ShowTrackers)
                    {
                        EditorGUILayout.Space(8f);
                        DrawTrackerRoles();
                    }

                    if (presentation.ShowReview)
                    {
                        EditorGUILayout.Space(8f);
                        DrawReviewControls();
                        EditorGUILayout.Space(8f);
                        DrawAuthoringControls();
                        EditorGUILayout.Space(8f);
                        DrawValidationIssues();
                    }

                    EditorGUILayout.Space(16f);
                }
                GUILayout.Space(12f);
            }
            EditorGUILayout.EndScrollView();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(12f);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawPrimaryActions(presentation);
                    GUILayout.Space(12f);
                }
                GUILayout.Space(12f);
            }
            BindSceneHandles();
        }

        private void EnsureStyles()
        {
            var darkTheme = EditorGUIUtility.isProSkin;
            if (_styles != null && _stylesUseDarkTheme == darkTheme)
            {
                return;
            }

            _styles?.Dispose();
            _styles = MotionTakeStudioStyles.Create(darkTheme);
            _stylesUseDarkTheme = darkTheme;
        }

        private void DrawHero(MotionTakeStudioPhasePresentation presentation)
        {
            using (new EditorGUILayout.VerticalScope(_styles.Header))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Label("MOTION TAKE STUDIO", _styles.HeaderEyebrow);
                        GUILayout.Label(presentation.Title, _styles.HeaderTitle);
                        GUILayout.Label(presentation.Description, _styles.HeaderDescription);
                    }

                    GUILayout.Space(12f);
                    var statusState = _styles.StatusPill.normal;
                    var previousStatusColor = statusState.textColor;
                    try
                    {
                        statusState.textColor = ResolveStatusColor();
                        GUILayout.Label(
                            $"●  {ResolveStatusLabel()}",
                            _styles.StatusPill,
                        GUILayout.Width(Mathf.Clamp(position.width * 0.25f, 112f, 152f)));
                    }
                    finally
                    {
                        statusState.textColor = previousStatusColor;
                    }
                }
            }
        }

        private void DrawWorkflowRail(int activeStep)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (var index = 0; index < WorkflowLabels.Length; index++)
                {
                    var style = index < activeStep
                        ? _styles.SegmentComplete
                        : index == activeStep
                            ? _styles.SegmentActive
                            : _styles.Segment;
                    GUILayout.Label(WorkflowLabels[index], style, GUILayout.ExpandWidth(true));
                    if (index < WorkflowLabels.Length - 1)
                    {
                        GUILayout.Space(4f);
                    }
                }
            }
        }

        private void DrawSessionCard(MotionTakeStudioPhasePresentation presentation)
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                DrawSectionHeader(
                    "Capture source",
                    "Choose the Humanoid that owns the performance. The source is locked once preparation starts.");

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

                if (_sourceAvatar != null && !HasValidHumanoid(_sourceAvatar))
                {
                    DrawNotice(
                        "This Animator has no valid Humanoid Avatar. Select a Humanoid before preparing capture.",
                        MotionTakeValidationSeverity.Error);
                }

                EditorGUILayout.Space(8f);
                DrawHairline();
                EditorGUILayout.Space(8f);

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("SESSION", _styles.HeaderEyebrow, GUILayout.Width(88f));
                    GUILayout.Label(
                        _session == null
                            ? "Not connected"
                            : ObjectNames.NicifyVariableName(_session.Phase.ToString()),
                        _styles.SectionTitle);
                }

                var status = MotionTakeStudioPresentation.ResolveStatus(
                    _session == null
                        ? "Capture is waiting for the coordinator to register."
                        : _session.StatusMessage,
                    _operationError);
                if (!string.IsNullOrWhiteSpace(status.Message))
                {
                    DrawNotice(
                        status.Message,
                        status.IsError
                            ? MotionTakeValidationSeverity.Error
                            : presentation.IsRecording
                                ? MotionTakeValidationSeverity.Warning
                                : MotionTakeValidationSeverity.Info);
                }
            }
        }

        private void DrawPrimaryActions(MotionTakeStudioPhasePresentation presentation)
        {
            if (presentation.PrimaryAction == MotionTakePrimaryAction.None &&
                !presentation.IsBusy &&
                !presentation.CanCancel)
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(
                        presentation.IsBusy
                            ? "Keep this window open while the operation completes."
                            : "Continue the current workflow when the stage is ready.",
                        _styles.SectionDescription);
                    GUILayout.FlexibleSpace();

                    if (presentation.CanCancel && _session != null)
                    {
                        using (new EditorGUI.DisabledScope(_session.Phase == MotionTakeSessionPhase.Idle))
                        {
                            if (GUILayout.Button("Cancel", _styles.SecondaryButton, GUILayout.Width(96f)))
                            {
                                InvokeSession(_session.Cancel);
                            }
                        }
                    }

                    if (presentation.PrimaryAction != MotionTakePrimaryAction.None || presentation.IsBusy)
                    {
                        using (new EditorGUI.DisabledScope(!CanInvokePrimary(presentation.PrimaryAction)))
                        {
                            var style = presentation.IsRecording
                                ? _styles.RecordingButton
                                : _styles.PrimaryButton;
                            if (GUILayout.Button(
                                    presentation.PrimaryLabel,
                                    style,
                                    GUILayout.MinWidth(156f)))
                            {
                                InvokePrimaryAction(presentation.PrimaryAction);
                            }
                        }
                    }
                }
            }
        }

        private void DrawReviewControls()
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                var maximumFrame = Mathf.Max(0, (_session?.FrameCount ?? 0) - 1);
                var frameRate = Mathf.Max(0f, _session?.FrameRate ?? 0f);
                var time = frameRate > 0f ? _reviewFrame / frameRate : 0f;
                DrawSectionHeader(
                    "Timeline",
                    $"Frame {_reviewFrame + 1} of {maximumFrame + 1}  ·  {time:0.000} s");

                using (var check = new EditorGUI.ChangeCheckScope())
                {
                    _reviewFrame = EditorGUILayout.IntSlider(_reviewFrame, 0, maximumFrame);
                    if (check.changed)
                    {
                        ScrubToFrame(_reviewFrame);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("First", _styles.SecondaryButton)) ScrubToFrame(0);
                    if (GUILayout.Button("Previous", _styles.SecondaryButton)) ScrubToFrame(_reviewFrame - 1);
                    if (GUILayout.Button("Next", _styles.SecondaryButton)) ScrubToFrame(_reviewFrame + 1);
                    if (GUILayout.Button("Last", _styles.SecondaryButton)) ScrubToFrame(maximumFrame);
                }

                EditorGUILayout.Space(8f);
                GUILayout.Label("SOLVE STAGES", _styles.HeaderEyebrow);
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
        }

        private void DrawTrackerRoles()
        {
            if (!(_session is IMotionTakeTrackerRoleSession trackerSession))
            {
                return;
            }

            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                DrawSectionHeader(
                    "Tracker roles",
                    _session.Phase == MotionTakeSessionPhase.Recording
                        ? "Assignments are read-only while recording."
                        : "Map connected devices before starting the take.");

                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        GUILayout.Label("PROVIDER", _styles.HeaderEyebrow);
                        GUILayout.Label(trackerSession.TrackerProviderName ?? "Unknown", _styles.SectionTitle);
                    }
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(_session.Phase == MotionTakeSessionPhase.Recording))
                    {
                        if (GUILayout.Button("Refresh devices", _styles.SecondaryButton, GUILayout.Width(132f)))
                        {
                            InvokeSession(trackerSession.RefreshTrackedDevices);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(trackerSession.TrackerDiagnostic))
                {
                    DrawNotice(trackerSession.TrackerDiagnostic, MotionTakeValidationSeverity.Info);
                }

                var devices = trackerSession.TrackedDevices;
                if (devices == null || devices.Count == 0)
                {
                    DrawNotice(
                        "No tracked devices were found. Start SteamVR, refresh, then assign Waist and both Feet for six-point capture.",
                        MotionTakeValidationSeverity.Info);
                    return;
                }

                EditorGUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(_session.Phase == MotionTakeSessionPhase.Recording))
                {
                    foreach (var device in devices)
                    {
                        if (device == null)
                        {
                            continue;
                        }

                        var rowStyle = device.Connected
                            ? _styles.TrackerRow
                            : _styles.TrackerRowDisconnected;
                        if (position.width < 560f)
                        {
                            using (new EditorGUILayout.VerticalScope(rowStyle))
                            {
                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    GUILayout.Label(
                                        device.Connected ? "●  Connected" : "○  Offline",
                                        _styles.SectionDescription);
                                    GUILayout.FlexibleSpace();
                                    var compactRole = (TrackerRole)EditorGUILayout.EnumPopup(
                                        device.Role,
                                        GUILayout.Width(128f));
                                    AssignTrackerRoleIfChanged(trackerSession, device, compactRole);
                                }
                                GUILayout.Label(
                                    MotionTakeStudioPresentation.PopulateTrackerLabel(
                                        _trackerLabel,
                                        device.Id,
                                        device.Index),
                                    _styles.TrackerId);
                                GUILayout.Label(
                                    string.IsNullOrEmpty(device.DeviceClass) ? "Unknown device class" : device.DeviceClass,
                                    _styles.SectionDescription);
                            }
                        }
                        else
                        {
                            using (new EditorGUILayout.HorizontalScope(rowStyle))
                            {
                                GUILayout.Label(
                                    device.Connected ? "●  Connected" : "○  Offline",
                                    _styles.SectionDescription,
                                    GUILayout.Width(104f));
                                using (new EditorGUILayout.VerticalScope())
                                {
                                    GUILayout.Label(
                                        MotionTakeStudioPresentation.PopulateTrackerLabel(
                                            _trackerLabel,
                                            device.Id,
                                            device.Index),
                                        _styles.TrackerId);
                                    GUILayout.Label(
                                        string.IsNullOrEmpty(device.DeviceClass)
                                            ? "Unknown device class"
                                            : device.DeviceClass,
                                        _styles.SectionDescription);
                                }
                                var role = (TrackerRole)EditorGUILayout.EnumPopup(
                                    device.Role,
                                    GUILayout.Width(128f));
                                AssignTrackerRoleIfChanged(trackerSession, device, role);
                            }
                        }
                    }
                }
            }
        }

        private void DrawAuthoringControls()
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                DrawSectionHeader(
                    "Pose correction",
                    "Choose a target, set its influence, then adjust the Scene View handle.");
                _selectedTarget = (PoseTarget)EditorGUILayout.EnumPopup("Target", _selectedTarget);
                _influenceFrames = EditorGUILayout.IntSlider(
                    "Influence (frames)",
                    _influenceFrames,
                    1,
                    60);

                var recipe = _session?.ActiveRecipe;
                if (recipe == null)
                {
                    DrawNotice("No correction recipe is active for this take.", MotionTakeValidationSeverity.Info);
                    return;
                }

                var previewWarnings = ResolvePreviewDriver()?.LastIkWarnings;
                if (previewWarnings != null)
                {
                    foreach (var warning in previewWarnings)
                    {
                        DrawNotice(warning, MotionTakeValidationSeverity.Warning);
                    }
                }

                EditorGUILayout.Space(8f);
                if (position.width >= 560f)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        DrawAuthoringActions(recipe);
                    }
                }
                else
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        DrawAuthoringActions(recipe);
                    }
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Undo", _styles.SecondaryButton)) Undo.PerformUndo();
                    if (GUILayout.Button("Redo", _styles.SecondaryButton)) Undo.PerformRedo();
                }

                DrawNotice(
                    MotionTakeCorrectionAuthoring.SupportsRotation(_selectedTarget)
                        ? "Scene handles edit position and rotation relative to the base pose."
                        : "Elbow and knee hints are position-only bend guides; rotation stays locked.",
                    MotionTakeValidationSeverity.Info);
            }
        }

        private void DrawValidationIssues()
        {
            using (new EditorGUILayout.VerticalScope(_styles.Card))
            {
                var issues = _session?.ValidationIssues;
                var summary = MotionTakeStudioPresentation.SummarizeValidation(issues);
                var description = summary.Total == 0
                    ? "No issues detected across the corrected take."
                    : $"{summary.Errors} errors  ·  {summary.Warnings} warnings  ·  {summary.Info} info";
                DrawSectionHeader("Validation", description);

                if (issues == null || issues.Count == 0)
                {
                    GUILayout.Label(
                        new GUIContent("Take validation passed", EditorGUIUtility.IconContent("TestPassed").image),
                        _styles.IssueInfo);
                    return;
                }

                foreach (var issue in issues)
                {
                    if (issue == null)
                    {
                        continue;
                    }

                    var iconName = issue.Severity == MotionTakeValidationSeverity.Error
                        ? "console.erroricon.sml"
                        : issue.Severity == MotionTakeValidationSeverity.Warning
                            ? "console.warnicon.sml"
                            : "console.infoicon.sml";
                    var style = issue.Severity == MotionTakeValidationSeverity.Error
                        ? _styles.IssueError
                        : issue.Severity == MotionTakeValidationSeverity.Warning
                            ? _styles.IssueWarning
                            : _styles.IssueInfo;
                    var range = issue.EndFrame > issue.Frame
                        ? $"Frames {issue.Frame}–{issue.EndFrame}"
                        : $"Frame {issue.Frame}";
                    var label = new GUIContent($"{range}  ·  {issue.Message}", EditorGUIUtility.IconContent(iconName).image);
                    if (GUILayout.Button(label, style))
                    {
                        ScrubToFrame(issue.Frame);
                    }
                }
            }
        }

        private void DrawAuthoringActions(MotionEditRecipe recipe)
        {
            if (GUILayout.Button("Add pose key", _styles.SecondaryButton))
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
                if (GUILayout.Button("Reset target", _styles.SecondaryButton))
                {
                    MotionTakeCorrectionAuthoring.ResetTarget(recipe, _selectedTarget, _reviewFrame);
                    OnAuthoringChanged();
                }

                if (GUILayout.Button("Delete key", _styles.SecondaryButton))
                {
                    MotionTakeCorrectionAuthoring.DeleteKey(recipe, _reviewFrame);
                    OnAuthoringChanged();
                }
            }
        }

        private void AssignTrackerRoleIfChanged(
            IMotionTakeTrackerRoleSession trackerSession,
            TrackedDeviceInfo device,
            TrackerRole role)
        {
            if (role == device.Role)
            {
                return;
            }

            var deviceId = device.Id;
            InvokeSession(() => trackerSession.AssignTrackerRole(deviceId, role));
        }

        private void DrawSectionHeader(string title, string description)
        {
            GUILayout.Label(title, _styles.SectionTitle);
            if (!string.IsNullOrWhiteSpace(description))
            {
                GUILayout.Label(description, _styles.SectionDescription);
            }
            EditorGUILayout.Space(8f);
        }

        private void DrawNotice(string message, MotionTakeValidationSeverity severity)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            var iconName = severity == MotionTakeValidationSeverity.Error
                ? "console.erroricon.sml"
                : severity == MotionTakeValidationSeverity.Warning
                    ? "console.warnicon.sml"
                    : "console.infoicon.sml";
            var style = severity == MotionTakeValidationSeverity.Error
                ? _styles.IssueError
                : severity == MotionTakeValidationSeverity.Warning
                    ? _styles.IssueWarning
                    : _styles.IssueInfo;
            GUILayout.Label(new GUIContent(message, EditorGUIUtility.IconContent(iconName).image), style);
        }

        private void DrawHairline()
        {
            var rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, _styles.Palette.Border);
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
            if (_session == null || !HasValidHumanoid(_sourceAvatar))
            {
                return false;
            }

            return _session.Phase == MotionTakeSessionPhase.Idle ||
                   _session.Phase == MotionTakeSessionPhase.Error;
        }

        private bool CanInvokePrimary(MotionTakePrimaryAction action)
        {
            switch (action)
            {
                case MotionTakePrimaryAction.Prepare:
                    return CanPrepare();
                case MotionTakePrimaryAction.Record:
                    return _session != null && _session.Phase == MotionTakeSessionPhase.Ready;
                case MotionTakePrimaryAction.StopAndReview:
                    return _session != null && _session.Phase == MotionTakeSessionPhase.Recording;
                case MotionTakePrimaryAction.SaveAndExit:
                    return _session != null && _session.Phase == MotionTakeSessionPhase.Reviewing;
                default:
                    return false;
            }
        }

        private void InvokePrimaryAction(MotionTakePrimaryAction action)
        {
            switch (action)
            {
                case MotionTakePrimaryAction.Prepare:
                    InvokeSession(() => _session.PrepareCapture(_sourceAvatar));
                    break;
                case MotionTakePrimaryAction.Record:
                    InvokeSession(_session.BeginRecording);
                    break;
                case MotionTakePrimaryAction.StopAndReview:
                    InvokeSession(_session.StopAndReview);
                    break;
                case MotionTakePrimaryAction.SaveAndExit:
                    InvokeSession(_session.SaveAndExit);
                    break;
            }
        }

        private static bool HasValidHumanoid(Animator animator)
        {
            return animator != null &&
                   animator.avatar != null &&
                   animator.avatar.isValid &&
                   animator.avatar.isHuman;
        }

        private string ResolveStatusLabel()
        {
            if (_session == null)
            {
                return "OFFLINE";
            }

            switch (_session.Phase)
            {
                case MotionTakeSessionPhase.Preparing:
                    return "PREPARING";
                case MotionTakeSessionPhase.Ready:
                    return "READY";
                case MotionTakeSessionPhase.Recording:
                    return "RECORDING";
                case MotionTakeSessionPhase.Reviewing:
                    return "REVIEW";
                case MotionTakeSessionPhase.Saving:
                    return "SAVING";
                case MotionTakeSessionPhase.Error:
                    return "ERROR";
                default:
                    return "IDLE";
            }
        }

        private Color ResolveStatusColor()
        {
            if (_session == null)
            {
                return _styles.Palette.MutedText;
            }

            switch (_session.Phase)
            {
                case MotionTakeSessionPhase.Ready:
                case MotionTakeSessionPhase.Reviewing:
                    return _styles.Palette.Success;
                case MotionTakeSessionPhase.Recording:
                    return _styles.Palette.Recording;
                case MotionTakeSessionPhase.Preparing:
                case MotionTakeSessionPhase.Saving:
                    return _styles.Palette.Warning;
                case MotionTakeSessionPhase.Error:
                    return _styles.Palette.Error;
                default:
                    return _styles.Palette.Accent;
            }
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

        private MotionTakeOverlayFlags DrawOverlayToggle(
            MotionTakeOverlayFlags value,
            MotionTakeOverlayFlags flag,
            string label)
        {
            var enabled = (value & flag) != 0;
            enabled = GUILayout.Toggle(
                enabled,
                enabled ? $"●  {label}" : label,
                enabled ? _styles.SegmentActive : _styles.Segment,
                GUILayout.ExpandWidth(true));
            return enabled ? value | flag : value & ~flag;
        }
    }
}

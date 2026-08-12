using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    [Flags]
    public enum MotionTakeOverlayFlags
    {
        None = 0,
        Raw = 1 << 0,
        Ik = 1 << 1,
        Automatic = 1 << 2,
        Manual = 1 << 3
    }

    public enum MotionTakeSessionPhase
    {
        Idle,
        Preparing,
        Ready,
        Recording,
        Reviewing,
        Saving,
        Error
    }

    /// <summary>
    /// Capture/recovery code implements this small editor-facing contract instead of the window
    /// depending on a concrete coordinator. All callbacks are expected on Unity's main thread.
    /// </summary>
    public interface IMotionTakeStudioSession
    {
        event Action Changed;

        MotionTakeSessionPhase Phase { get; }
        string StatusMessage { get; }
        int FrameCount { get; }
        float FrameRate { get; }
        int CurrentFrame { get; }
        MotionEditRecipe ActiveRecipe { get; }
        IMotionTakeTargetPoseSource TargetPoseSource { get; }
        IMotionTakeOverlayPoseSource OverlayPoseSource { get; }
        IReadOnlyList<MotionTakeValidationIssue> ValidationIssues { get; }

        void PrepareCapture(Animator sourceAvatar);
        void BeginRecording();
        void StopAndReview();
        void SaveAndExit();
        void Cancel();
        void ScrubToFrame(int frame);
        void SetOverlays(MotionTakeOverlayFlags overlays);
    }

    /// <summary>Optional extension for coordinators that expose the built-in preview driver.</summary>
    public interface IMotionTakeStudioPreviewSession
    {
        MotionTakePreviewDriver PreviewDriver { get; }
    }

    public struct MotionTakeTargetPose
    {
        public Transform AvatarRoot;
        public Vector3 WorldPosition;
        public Quaternion WorldRotation;
        public float HumanScale;
        public float LimbLength;
    }

    public interface IMotionTakeTargetPoseSource
    {
        bool TryGetBaseTargetPose(PoseTarget target, int frame, out MotionTakeTargetPose pose);
    }

    /// <summary>
    /// Supplies the actual solved pose for a review pipeline stage. The stage must be one of
    /// <see cref="MotionTakeOverlayFlags.Ik"/>, <see cref="MotionTakeOverlayFlags.Automatic"/>,
    /// or <see cref="MotionTakeOverlayFlags.Manual"/>.
    /// </summary>
    public interface IMotionTakeOverlayPoseSource
    {
        bool TryGetSolvedTargetPose(
            MotionTakeOverlayFlags stage,
            PoseTarget target,
            int frame,
            out MotionTakeTargetPose pose);
    }

    public interface IMotionTakeRawPoseSource
    {
        bool TryGetRawTargetPose(
            PoseTarget target,
            int frame,
            out Vector3 worldPosition,
            out Quaternion worldRotation);
    }

    /// <summary>Optional capture-device role assignment UI contract.</summary>
    public interface IMotionTakeTrackerRoleSession
    {
        string TrackerProviderName { get; }
        string TrackerDiagnostic { get; }
        IReadOnlyList<TrackedDeviceInfo> TrackedDevices { get; }

        void RefreshTrackedDevices();
        void AssignTrackerRole(string deviceId, TrackerRole role);
    }

    /// <summary>Optional hook used to refresh validation after a recipe edit.</summary>
    public interface IMotionTakeValidationSession
    {
        void Revalidate();
    }

    /// <summary>
    /// Stable registration point shared by independently implemented capture and authoring layers.
    /// Disposing an old registration never removes a newer replacement.
    /// </summary>
    public static class MotionTakeStudioSessionBridge
    {
        private static IMotionTakeStudioSession _current;

        public static event Action CurrentChanged;

        public static IMotionTakeStudioSession Current => _current;

        public static IDisposable Register(IMotionTakeStudioSession session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _current = session;
            CurrentChanged?.Invoke();
            return new Registration(session);
        }

        private sealed class Registration : IDisposable
        {
            private IMotionTakeStudioSession _session;

            public Registration(IMotionTakeStudioSession session)
            {
                _session = session;
            }

            public void Dispose()
            {
                var session = _session;
                _session = null;
                if (session == null || !ReferenceEquals(_current, session))
                {
                    return;
                }

                _current = null;
                CurrentChanged?.Invoke();
            }
        }
    }
}

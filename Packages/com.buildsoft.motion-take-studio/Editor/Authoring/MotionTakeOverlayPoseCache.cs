using System.Collections.Generic;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Frame-scoped storage for actual solved overlay poses. Keeping separate dictionaries makes
    /// it impossible for IK, automatic cleanup, and manual correction markers to alias one pose.
    /// </summary>
    internal sealed class MotionTakeOverlayPoseCache
    {
        private readonly Dictionary<PoseTarget, MotionTakeTargetPose> _ik =
            new Dictionary<PoseTarget, MotionTakeTargetPose>();
        private readonly Dictionary<PoseTarget, MotionTakeTargetPose> _automatic =
            new Dictionary<PoseTarget, MotionTakeTargetPose>();
        private readonly Dictionary<PoseTarget, MotionTakeTargetPose> _manual =
            new Dictionary<PoseTarget, MotionTakeTargetPose>();
        private int _frame = -1;

        public void Reset(int frame)
        {
            _frame = frame;
            _ik.Clear();
            _automatic.Clear();
            _manual.Clear();
        }

        public void Set(
            MotionTakeOverlayFlags stage,
            PoseTarget target,
            MotionTakeTargetPose pose)
        {
            Resolve(stage)[target] = pose;
        }

        public bool TryGet(
            MotionTakeOverlayFlags stage,
            PoseTarget target,
            int frame,
            out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            return frame == _frame && Resolve(stage).TryGetValue(target, out pose);
        }

        private Dictionary<PoseTarget, MotionTakeTargetPose> Resolve(MotionTakeOverlayFlags stage)
        {
            switch (stage)
            {
                case MotionTakeOverlayFlags.Ik:
                    return _ik;
                case MotionTakeOverlayFlags.Automatic:
                    return _automatic;
                case MotionTakeOverlayFlags.Manual:
                    return _manual;
                default:
                    throw new System.ArgumentOutOfRangeException(
                        nameof(stage), stage, "Select exactly one solved overlay stage.");
            }
        }
    }
}

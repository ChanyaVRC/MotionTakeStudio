namespace BuildSoft.MotionTakeStudio
{
    /// <summary>Editable full-body targets exposed in the Scene view.</summary>
    public enum PoseTarget
    {
        Head,
        Hips,
        LeftHand,
        RightHand,
        LeftFoot,
        RightFoot,
        LeftElbowHint,
        RightElbowHint,
        LeftKneeHint,
        RightKneeHint
    }

    public static class PoseTargetUtility
    {
        public static bool IsHint(this PoseTarget target)
        {
            return target == PoseTarget.LeftElbowHint ||
                   target == PoseTarget.RightElbowHint ||
                   target == PoseTarget.LeftKneeHint ||
                   target == PoseTarget.RightKneeHint;
        }

        public static bool SupportsRotation(this PoseTarget target)
        {
            return !target.IsHint();
        }
    }
}

using System;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>
    /// A target-local correction. Positions are normalized by human scale, or by
    /// limb length for elbow and knee hints. Rotations are local delta rotations.
    /// </summary>
    [Serializable]
    public struct MotionPoseTargetOffset
    {
        [SerializeField] private PoseTarget target;
        [SerializeField] private bool hasPosition;
        [SerializeField] private bool hasRotation;
        [SerializeField] private Vector3 positionOffsetNormalized;
        [SerializeField] private Quaternion rotationOffsetLocal;

        public PoseTarget Target => target;
        public bool HasPosition => hasPosition;
        public bool HasRotation => hasRotation;
        public Vector3 PositionOffsetNormalized => positionOffsetNormalized;
        public Quaternion RotationOffsetLocal => NormalizeSafe(rotationOffsetLocal);

        public Vector3 ResolvePositionOffset(float humanScale, float limbLength)
        {
            var scale = target.IsHint() ? limbLength : humanScale;
            return positionOffsetNormalized * Mathf.Max(0.0001f, Mathf.Abs(scale));
        }

        public static MotionPoseTargetOffset CreatePosition(
            PoseTarget target,
            Vector3 positionOffsetNormalized)
        {
            return Create(target, true, positionOffsetNormalized, false, Quaternion.identity);
        }

        public static MotionPoseTargetOffset CreateRotation(
            PoseTarget target,
            Quaternion rotationOffsetLocal)
        {
            return Create(target, false, Vector3.zero, true, rotationOffsetLocal);
        }

        public static MotionPoseTargetOffset Create(
            PoseTarget target,
            bool hasPosition,
            Vector3 positionOffsetNormalized,
            bool hasRotation,
            Quaternion rotationOffsetLocal)
        {
            return new MotionPoseTargetOffset
            {
                target = target,
                hasPosition = hasPosition,
                hasRotation = hasRotation && target.SupportsRotation(),
                positionOffsetNormalized = hasPosition ? positionOffsetNormalized : Vector3.zero,
                rotationOffsetLocal = hasRotation && target.SupportsRotation()
                    ? NormalizeSafe(rotationOffsetLocal)
                    : Quaternion.identity
            };
        }

        public static MotionPoseTargetOffset FromLocalDeltas(
            PoseTarget target,
            Vector3 localPositionDelta,
            Quaternion localRotationDelta,
            bool includePosition,
            bool includeRotation,
            float humanScale,
            float limbLength)
        {
            var scale = target.IsHint() ? limbLength : humanScale;
            var normalized = includePosition
                ? localPositionDelta / Mathf.Max(0.0001f, Mathf.Abs(scale))
                : Vector3.zero;
            return Create(target, includePosition, normalized, includeRotation, localRotationDelta);
        }

        internal static MotionPoseTargetOffset Lerp(
            MotionPoseTargetOffset from,
            MotionPoseTargetOffset to,
            float amount)
        {
            amount = Mathf.Clamp01(amount);
            var target = from.target;
            var hasPosition = from.hasPosition || to.hasPosition;
            var hasRotation = (from.hasRotation || to.hasRotation) && target.SupportsRotation();
            var fromPosition = from.hasPosition ? from.positionOffsetNormalized : Vector3.zero;
            var toPosition = to.hasPosition ? to.positionOffsetNormalized : Vector3.zero;
            var fromRotation = from.hasRotation ? NormalizeSafe(from.rotationOffsetLocal) : Quaternion.identity;
            var toRotation = to.hasRotation ? NormalizeSafe(to.rotationOffsetLocal) : Quaternion.identity;
            if (Quaternion.Dot(fromRotation, toRotation) < 0f)
            {
                toRotation = Negate(toRotation);
            }

            return Create(
                target,
                hasPosition,
                Vector3.LerpUnclamped(fromPosition, toPosition, amount),
                hasRotation,
                Quaternion.SlerpUnclamped(fromRotation, toRotation, amount));
        }

        internal static MotionPoseTargetOffset Zero(PoseTarget target)
        {
            return Create(target, true, Vector3.zero, target.SupportsRotation(), Quaternion.identity);
        }

        internal static Quaternion NormalizeSafe(Quaternion value)
        {
            var magnitudeSquared = value.x * value.x + value.y * value.y +
                                   value.z * value.z + value.w * value.w;
            if (magnitudeSquared < 1e-12f || float.IsNaN(magnitudeSquared) ||
                float.IsInfinity(magnitudeSquared))
            {
                return Quaternion.identity;
            }

            var inverse = 1f / Mathf.Sqrt(magnitudeSquared);
            return new Quaternion(
                value.x * inverse,
                value.y * inverse,
                value.z * inverse,
                value.w * inverse);
        }

        private static Quaternion Negate(Quaternion value)
        {
            return new Quaternion(-value.x, -value.y, -value.z, -value.w);
        }
    }
}

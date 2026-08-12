using System;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    [Flags]
    public enum TwoBoneIkWarning
    {
        None = 0,
        InvalidInput = 1 << 0,
        TargetBeyondReach = 1 << 1,
        TargetInsideMinimumReach = 1 << 2,
        JointLimitClamped = 1 << 3,
        DegenerateHint = 1 << 4,
        BendContinuityClamped = 1 << 5
    }

    /// <summary>World-space analytic two-bone IK input.</summary>
    public struct TwoBoneIkRequest
    {
        public Vector3 RootPosition;
        public Vector3 JointPosition;
        public Vector3 TipPosition;
        public Vector3 TargetPosition;
        public Vector3 HintPosition;
        public Vector3 PreviousBendDirection;
        public float UpperLength;
        public float LowerLength;
        public float MinimumBendDegrees;
        public float MaximumBendDegrees;
        public float ContinuityThreshold;
        public float MaximumBendDirectionChangeDegrees;

        public static TwoBoneIkRequest Create(
            Vector3 root,
            Vector3 joint,
            Vector3 tip,
            Vector3 target,
            Vector3 hint,
            Vector3 previousBendDirection = default(Vector3))
        {
            return new TwoBoneIkRequest
            {
                RootPosition = root,
                JointPosition = joint,
                TipPosition = tip,
                TargetPosition = target,
                HintPosition = hint,
                PreviousBendDirection = previousBendDirection,
                UpperLength = Vector3.Distance(root, joint),
                LowerLength = Vector3.Distance(joint, tip),
                MinimumBendDegrees = 0f,
                MaximumBendDegrees = 179.5f,
                ContinuityThreshold = 0.002f,
                MaximumBendDirectionChangeDegrees =
                    TwoBoneIkSolver.DefaultMaximumBendDirectionChangeDegrees
            };
        }
    }

    /// <summary>Stable solution. The requested target is never mutated.</summary>
    public struct TwoBoneIkResult
    {
        public bool Succeeded;
        public bool TargetIsReachable;
        public Vector3 JointPosition;
        public Vector3 TipPosition;
        public Vector3 BendDirection;
        public Vector3 ClampedHintPosition;
        public Quaternion UpperRotationDelta;
        public Quaternion LowerRotationDelta;
        public float EndError;
        public TwoBoneIkWarning Warning;

        public bool HasWarning => Warning != TwoBoneIkWarning.None;
    }

    /// <summary>
    /// Deterministic analytic solver used for elbow and knee hint editing. A
    /// reachable target remains exact while the hint only selects the bend plane.
    /// </summary>
    public static class TwoBoneIkSolver
    {
        public const float DefaultMaximumBendDirectionChangeDegrees = 60f;

        private const float Epsilon = 1e-6f;

        public static TwoBoneIkResult Solve(TwoBoneIkRequest request)
        {
            var invalidResult = new TwoBoneIkResult
            {
                Succeeded = false,
                TargetIsReachable = false,
                JointPosition = IsFinite(request.JointPosition) ? request.JointPosition : Vector3.zero,
                TipPosition = IsFinite(request.TipPosition) ? request.TipPosition : Vector3.zero,
                BendDirection = Vector3.up,
                ClampedHintPosition = IsFinite(request.HintPosition) ? request.HintPosition : Vector3.zero,
                UpperRotationDelta = Quaternion.identity,
                LowerRotationDelta = Quaternion.identity,
                EndError = float.PositiveInfinity,
                Warning = TwoBoneIkWarning.InvalidInput
            };

            if (!IsFinite(request.RootPosition) || !IsFinite(request.JointPosition) ||
                !IsFinite(request.TipPosition) || !IsFinite(request.TargetPosition) ||
                !IsFinite(request.HintPosition) || !IsFinite(request.PreviousBendDirection))
            {
                return invalidResult;
            }

            var upperLength = request.UpperLength > Epsilon
                ? request.UpperLength
                : Vector3.Distance(request.RootPosition, request.JointPosition);
            var lowerLength = request.LowerLength > Epsilon
                ? request.LowerLength
                : Vector3.Distance(request.JointPosition, request.TipPosition);
            if (!IsFinite(upperLength) || !IsFinite(lowerLength) ||
                upperLength <= Epsilon || lowerLength <= Epsilon)
            {
                return invalidResult;
            }

            var warnings = TwoBoneIkWarning.None;
            var targetVector = request.TargetPosition - request.RootPosition;
            var targetDistance = targetVector.magnitude;
            var axis = targetDistance > Epsilon
                ? targetVector / targetDistance
                : SafeDirection(request.TipPosition - request.RootPosition, Vector3.forward);

            var minimumReach = Mathf.Abs(upperLength - lowerLength);
            var maximumReach = upperLength + lowerLength;
            var solvedDistance = targetDistance;
            if (solvedDistance > maximumReach)
            {
                solvedDistance = maximumReach;
                warnings |= TwoBoneIkWarning.TargetBeyondReach;
            }
            else if (solvedDistance < minimumReach)
            {
                solvedDistance = minimumReach;
                warnings |= TwoBoneIkWarning.TargetInsideMinimumReach;
            }

            var minimumBend = Mathf.Clamp(request.MinimumBendDegrees, 0f, 179.9f);
            var maximumBend = request.MaximumBendDegrees <= 0f
                ? 179.5f
                : Mathf.Clamp(request.MaximumBendDegrees, minimumBend, 179.9f);
            var denominator = 2f * upperLength * lowerLength;
            var cosine = denominator <= Epsilon
                ? 1f
                : Mathf.Clamp(
                    (solvedDistance * solvedDistance - upperLength * upperLength -
                     lowerLength * lowerLength) / denominator,
                    -1f,
                    1f);
            var bendDegrees = Mathf.Acos(cosine) * Mathf.Rad2Deg;
            var clampedBend = Mathf.Clamp(bendDegrees, minimumBend, maximumBend);
            if (Mathf.Abs(clampedBend - bendDegrees) > 0.001f)
            {
                warnings |= TwoBoneIkWarning.JointLimitClamped;
                var bendRadians = clampedBend * Mathf.Deg2Rad;
                solvedDistance = Mathf.Sqrt(Mathf.Max(
                    0f,
                    upperLength * upperLength + lowerLength * lowerLength +
                    2f * upperLength * lowerLength * Mathf.Cos(bendRadians)));
            }

            var hintVector = request.HintPosition - request.RootPosition;
            var hintProjection = hintVector - axis * Vector3.Dot(hintVector, axis);
            var previousProjection = request.PreviousBendDirection -
                                     axis * Vector3.Dot(request.PreviousBendDirection, axis);
            var threshold = request.ContinuityThreshold > 0f
                ? request.ContinuityThreshold
                : Mathf.Max(0.0005f, maximumReach * 0.001f);
            Vector3 bendDirection;
            if (hintProjection.magnitude <= threshold)
            {
                warnings |= TwoBoneIkWarning.DegenerateHint;
                bendDirection = SafeDirection(previousProjection, FindPerpendicular(axis));
            }
            else
            {
                bendDirection = hintProjection.normalized;
            }

            bendDirection = ApplyBendContinuity(
                axis,
                bendDirection,
                previousProjection,
                request.MaximumBendDirectionChangeDegrees,
                ref warnings);

            var safeDistance = Mathf.Max(Epsilon, solvedDistance);
            var along = (upperLength * upperLength - lowerLength * lowerLength +
                         safeDistance * safeDistance) / (2f * safeDistance);
            along = Mathf.Clamp(along, -upperLength, upperLength);
            var perpendicular = Mathf.Sqrt(Mathf.Max(0f, upperLength * upperLength - along * along));
            var solvedJoint = request.RootPosition + axis * along + bendDirection * perpendicular;
            var solvedTip = request.RootPosition + axis * solvedDistance;

            var currentUpper = request.JointPosition - request.RootPosition;
            var desiredUpper = solvedJoint - request.RootPosition;
            var upperDelta = FromToRotationSafe(currentUpper, desiredUpper);
            var rotatedCurrentLower = upperDelta * (request.TipPosition - request.JointPosition);
            var desiredLower = solvedTip - solvedJoint;
            var lowerDelta = FromToRotationSafe(rotatedCurrentLower, desiredLower);
            var endError = Vector3.Distance(solvedTip, request.TargetPosition);
            var targetReachable = endError <= 0.00001f &&
                                  (warnings & (TwoBoneIkWarning.TargetBeyondReach |
                                               TwoBoneIkWarning.TargetInsideMinimumReach |
                                               TwoBoneIkWarning.JointLimitClamped)) == 0;

            var hintAxial = Vector3.Dot(hintVector, axis);
            var clampedHint = request.RootPosition + axis * hintAxial +
                              bendDirection * Mathf.Max(threshold, hintProjection.magnitude);
            var result = new TwoBoneIkResult
            {
                Succeeded = IsFinite(solvedJoint) && IsFinite(solvedTip),
                TargetIsReachable = targetReachable,
                JointPosition = solvedJoint,
                TipPosition = solvedTip,
                BendDirection = bendDirection,
                ClampedHintPosition = clampedHint,
                UpperRotationDelta = upperDelta,
                LowerRotationDelta = lowerDelta,
                EndError = endError,
                Warning = warnings
            };
            return result.Succeeded ? result : invalidResult;
        }

        private static Vector3 ApplyBendContinuity(
            Vector3 axis,
            Vector3 desired,
            Vector3 previousProjection,
            float requestedMaximumChange,
            ref TwoBoneIkWarning warnings)
        {
            if (previousProjection.sqrMagnitude <= Epsilon)
            {
                return desired;
            }

            var previous = previousProjection.normalized;
            var angle = Vector3.Angle(previous, desired);
            var maximumChange = requestedMaximumChange > 0f
                ? Mathf.Clamp(requestedMaximumChange, 1f, 180f)
                : DefaultMaximumBendDirectionChangeDegrees;
            if (angle <= maximumChange + 0.001f)
            {
                return desired;
            }

            // Both vectors are perpendicular to the root-to-target axis. Rotating
            // around that axis keeps the analytic joint circle intact, so limiting
            // bend-plane motion cannot introduce any end-effector error.
            var cross = Vector3.Cross(previous, desired);
            float turnSign;
            if (cross.sqrMagnitude > 0.000001f)
            {
                turnSign = Mathf.Sign(Vector3.Dot(axis, cross));
            }
            else
            {
                // Near 180 degrees the cross product has no reliable sign. Pick a
                // stable world-relative direction instead of letting floating-point
                // noise alternate the elbow/knee between opposite planes.
                turnSign = CanonicalTurnSign(axis, previous);
            }

            if (Mathf.Abs(turnSign) < 0.5f)
            {
                turnSign = 1f;
            }

            var limited = Quaternion.AngleAxis(turnSign * maximumChange, axis) * previous;
            limited -= axis * Vector3.Dot(limited, axis);
            warnings |= TwoBoneIkWarning.BendContinuityClamped;
            return SafeDirection(limited, previous);
        }

        private static float CanonicalTurnSign(Vector3 axis, Vector3 direction)
        {
            var tangent = Vector3.Cross(axis, direction);
            var reference = FindPerpendicular(axis);
            var alignment = Vector3.Dot(tangent, reference);
            if (Mathf.Abs(alignment) > Epsilon)
            {
                return Mathf.Sign(alignment);
            }

            reference = Vector3.Cross(axis, reference);
            alignment = Vector3.Dot(tangent, reference);
            return Mathf.Abs(alignment) > Epsilon ? Mathf.Sign(alignment) : 1f;
        }

        private static Quaternion FromToRotationSafe(Vector3 from, Vector3 to)
        {
            if (from.sqrMagnitude <= Epsilon || to.sqrMagnitude <= Epsilon)
            {
                return Quaternion.identity;
            }

            return Quaternion.FromToRotation(from, to);
        }

        private static Vector3 SafeDirection(Vector3 value, Vector3 fallback)
        {
            return value.sqrMagnitude > Epsilon ? value.normalized : fallback.normalized;
        }

        private static Vector3 FindPerpendicular(Vector3 axis)
        {
            var reference = Mathf.Abs(Vector3.Dot(axis, Vector3.up)) < 0.9f
                ? Vector3.up
                : Vector3.right;
            return SafeDirection(reference - axis * Vector3.Dot(reference, axis), Vector3.forward);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Applies calibrated OpenVR targets to the processed Humanoid before its
    /// HumanPose is sampled. Filtering/root cleanup/foot locks run here; manual
    /// recipe IK is applied later by MotionTakePreviewDriver.
    /// </summary>
    internal sealed class MotionCaptureRig
    {
        private const float SampleRate = 60f;

        private readonly HumanoidAvatarBinding binding;
        private readonly Dictionary<TrackerRole, Vector3> filteredPositions =
            new Dictionary<TrackerRole, Vector3>();
        private readonly Dictionary<TrackerRole, Quaternion> filteredRotations =
            new Dictionary<TrackerRole, Quaternion>();
        private readonly Dictionary<PoseTarget, Vector3> previousBends =
            new Dictionary<PoseTarget, Vector3>();
        private readonly Dictionary<TrackerRole, Vector3> footLocks =
            new Dictionary<TrackerRole, Vector3>();
        private readonly Dictionary<TrackerRole, Vector3> previousFootPositions =
            new Dictionary<TrackerRole, Vector3>();
        private readonly bool applyAutomaticCorrections;

        private Quaternion trackingToWorldRotation = Quaternion.identity;
        private Vector3 trackingToWorldPosition;
        private Vector3 initialHeadToHipsLocal;
        private float floorHeight;
        private bool calibrated;
        private bool hasLastHips;
        private Vector3 lastHips;

        public MotionCaptureRig(HumanoidAvatarBinding binding)
            : this(binding, true)
        {
        }

        private MotionCaptureRig(HumanoidAvatarBinding binding, bool applyAutomaticCorrections)
        {
            this.binding = binding ?? throw new ArgumentNullException(nameof(binding));
            this.applyAutomaticCorrections = applyAutomaticCorrections;
            floorHeight = ResolveFloorHeight();
        }

        public bool IsCalibrated => calibrated;
        public float FloorHeight => floorHeight;

        public MotionCaptureRig CreateReplayRig()
        {
            return CreateReplayRig(applyAutomaticCorrections);
        }

        /// <summary>
        /// Creates a deterministic replay which stops after tracker-to-Humanoid IK. Filtering,
        /// root jump cleanup, and foot locking are deliberately disabled for the IK-stage overlay.
        /// </summary>
        public MotionCaptureRig CreateIkOnlyReplayRig()
        {
            return CreateReplayRig(false);
        }

        private MotionCaptureRig CreateReplayRig(bool automaticCorrections)
        {
            var replay = new MotionCaptureRig(binding, automaticCorrections)
            {
                trackingToWorldRotation = trackingToWorldRotation,
                trackingToWorldPosition = trackingToWorldPosition,
                initialHeadToHipsLocal = initialHeadToHipsLocal,
                floorHeight = floorHeight,
                calibrated = calibrated
            };
            return replay;
        }

        public bool TryMapRawPose(
            TrackerPoseSample sample,
            out Vector3 worldPosition,
            out Quaternion worldRotation)
        {
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (!calibrated || sample == null || !sample.connected || !sample.valid ||
                !IsFinite(sample.position) || !IsFinite(sample.rotation))
            {
                return false;
            }

            worldPosition = trackingToWorldRotation * sample.position + trackingToWorldPosition;
            worldRotation = trackingToWorldRotation * sample.rotation;
            return true;
        }

        public void ResetCalibration()
        {
            calibrated = false;
            hasLastHips = false;
            filteredPositions.Clear();
            filteredRotations.Clear();
            previousBends.Clear();
            footLocks.Clear();
            previousFootPositions.Clear();
        }

        public bool Apply(
            TrackerFrame frame,
            int frameIndex,
            ICollection<MotionTakeValidationIssue> issues)
        {
            if (frame == null || !EnsureCalibrated(frame))
            {
                return false;
            }

            var headTarget = GetWorldPose(frame, TrackerRole.Head, out var headPosition, out var headRotation);
            var hipsTarget = GetWorldPose(frame, TrackerRole.Waist, out var hipsPosition, out var hipsRotation);
            if (!hipsTarget && headTarget &&
                binding.TryGetBone(HumanBodyBones.Hips, out var inferredHips))
            {
                var headYaw = YawOnly(headRotation);
                hipsPosition = headPosition + headYaw * initialHeadToHipsLocal;
                hipsRotation = YawOnly(inferredHips.rotation);
                hipsTarget = true;
            }

            if (hipsTarget && binding.TryGetBone(HumanBodyBones.Hips, out var hips))
            {
                var maximumRootStep = Mathf.Max(0.05f, binding.Animator.humanScale * 0.15f);
                if (applyAutomaticCorrections && hasLastHips &&
                    Vector3.Distance(lastHips, hipsPosition) > maximumRootStep)
                {
                    var requested = hipsPosition;
                    hipsPosition = lastHips + Vector3.ClampMagnitude(hipsPosition - lastHips, maximumRootStep);
                    issues?.Add(new MotionTakeValidationIssue(
                        MotionTakeValidationKind.RootDiscontinuity,
                        MotionTakeValidationSeverity.Warning,
                        frameIndex,
                        $"Tracked hips jumped {Vector3.Distance(lastHips, requested):0.###} m; root correction clamped the sample."));
                }

                hips.position = hipsPosition;
                hips.rotation = hipsRotation;
                lastHips = hipsPosition;
                hasLastHips = true;
            }

            if (GetWorldPose(frame, TrackerRole.Chest, out _, out var chestRotation) &&
                binding.TryGetBone(HumanBodyBones.Chest, out var chest))
            {
                chest.rotation = Quaternion.Slerp(chest.rotation, chestRotation, 0.75f);
            }

            if (headTarget)
            {
                ApplyHead(headPosition, headRotation);
            }

            ApplyLimb(
                frame,
                frameIndex,
                TrackerRole.LeftHand,
                TrackerRole.LeftElbow,
                PoseTarget.LeftElbowHint,
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                issues);
            ApplyLimb(
                frame,
                frameIndex,
                TrackerRole.RightHand,
                TrackerRole.RightElbow,
                PoseTarget.RightElbowHint,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand,
                issues);
            ApplyLimb(
                frame,
                frameIndex,
                TrackerRole.LeftFoot,
                TrackerRole.LeftKnee,
                PoseTarget.LeftKneeHint,
                HumanBodyBones.LeftUpperLeg,
                HumanBodyBones.LeftLowerLeg,
                HumanBodyBones.LeftFoot,
                issues);
            ApplyLimb(
                frame,
                frameIndex,
                TrackerRole.RightFoot,
                TrackerRole.RightKnee,
                PoseTarget.RightKneeHint,
                HumanBodyBones.RightUpperLeg,
                HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot,
                issues);
            return true;
        }

        private bool EnsureCalibrated(TrackerFrame frame)
        {
            if (calibrated)
            {
                return true;
            }

            if (!TryGetUsable(frame, TrackerRole.Head, out var trackedHead) ||
                !binding.TryGetBone(HumanBodyBones.Head, out var avatarHead) ||
                !binding.TryGetBone(HumanBodyBones.Hips, out var avatarHips))
            {
                return false;
            }

            var trackerYaw = YawOnly(trackedHead.rotation);
            var avatarYaw = YawOnly(avatarHead.rotation);
            trackingToWorldRotation = avatarYaw * Quaternion.Inverse(trackerYaw);
            trackingToWorldPosition = avatarHead.position - trackingToWorldRotation * trackedHead.position;
            initialHeadToHipsLocal = Quaternion.Inverse(avatarYaw) * (avatarHips.position - avatarHead.position);
            calibrated = true;
            return true;
        }

        private void ApplyHead(Vector3 position, Quaternion rotation)
        {
            if (binding.TryGetBone(HumanBodyBones.Chest, out var chest))
            {
                chest.rotation = Quaternion.Slerp(chest.rotation, rotation, 0.12f);
            }

            if (binding.TryGetBone(HumanBodyBones.UpperChest, out var upperChest))
            {
                upperChest.rotation = Quaternion.Slerp(upperChest.rotation, rotation, 0.18f);
            }

            if (binding.TryGetBone(HumanBodyBones.Neck, out var neck))
            {
                neck.rotation = Quaternion.Slerp(neck.rotation, rotation, 0.35f);
            }

            if (binding.TryGetBone(HumanBodyBones.Head, out var head))
            {
                head.position = position;
                head.rotation = rotation;
            }
        }

        private void ApplyLimb(
            TrackerFrame frame,
            int frameIndex,
            TrackerRole tipRole,
            TrackerRole hintRole,
            PoseTarget hintTarget,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones tipBone,
            ICollection<MotionTakeValidationIssue> issues)
        {
            if (!GetWorldPose(frame, tipRole, out var targetPosition, out var targetRotation) ||
                !binding.TryGetBone(upperBone, out var upper) ||
                !binding.TryGetBone(lowerBone, out var lower) ||
                !binding.TryGetBone(tipBone, out var tip))
            {
                return;
            }

            if (applyAutomaticCorrections &&
                (tipRole == TrackerRole.LeftFoot || tipRole == TrackerRole.RightFoot))
            {
                targetPosition = ApplyFootLock(tipRole, targetPosition);
            }

            var hasHint = GetWorldPose(frame, hintRole, out var hintPosition, out _);
            if (!hasHint)
            {
                hintPosition = lower.position;
            }

            previousBends.TryGetValue(hintTarget, out var previousBend);
            var request = TwoBoneIkRequest.Create(
                upper.position,
                lower.position,
                tip.position,
                targetPosition,
                hintPosition,
                previousBend);
            var result = TwoBoneIkSolver.Solve(request);
            if (!result.Succeeded)
            {
                issues?.Add(new MotionTakeValidationIssue(
                    MotionTakeValidationKind.NonFinitePose,
                    MotionTakeValidationSeverity.Error,
                    frameIndex,
                    $"{tipRole} IK input was invalid; the previous limb pose was retained."));
                return;
            }

            upper.rotation = result.UpperRotationDelta * upper.rotation;
            lower.rotation = result.LowerRotationDelta * lower.rotation;
            tip.rotation = targetRotation;
            previousBends[hintTarget] = result.BendDirection;
            if (!result.TargetIsReachable)
            {
                issues?.Add(new MotionTakeValidationIssue(
                    MotionTakeValidationKind.IkUnreachable,
                    MotionTakeValidationSeverity.Warning,
                    frameIndex,
                    $"{tipRole} target was outside the limb reach by {result.EndError * 1000f:0.#} mm; " +
                    "the target was not moved and the limb was clamped."));
            }
        }

        private Vector3 ApplyFootLock(TrackerRole role, Vector3 position)
        {
            if (!previousFootPositions.TryGetValue(role, out var previous))
            {
                previousFootPositions[role] = position;
                return position;
            }

            var speed = Vector3.Distance(previous, position) * SampleRate;
            previousFootPositions[role] = position;
            var nearFloor = position.y <= floorHeight + 0.06f * Mathf.Max(0.5f, binding.Animator.humanScale);
            if (footLocks.TryGetValue(role, out var locked))
            {
                if (speed > 0.18f || !nearFloor)
                {
                    footLocks.Remove(role);
                    return position;
                }

                return new Vector3(locked.x, position.y, locked.z);
            }

            if (speed < 0.06f && nearFloor)
            {
                footLocks[role] = position;
            }

            return position;
        }

        private bool GetWorldPose(
            TrackerFrame frame,
            TrackerRole role,
            out Vector3 position,
            out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            if (!TryGetUsable(frame, role, out var sample))
            {
                return false;
            }

            var worldPosition = trackingToWorldRotation * sample.position + trackingToWorldPosition;
            var worldRotation = trackingToWorldRotation * sample.rotation;
            var alpha = role == TrackerRole.LeftFoot || role == TrackerRole.RightFoot ? 0.55f : 0.7f;
            if (applyAutomaticCorrections &&
                filteredPositions.TryGetValue(role, out var previousPosition))
            {
                worldPosition = Vector3.LerpUnclamped(previousPosition, worldPosition, alpha);
            }

            if (applyAutomaticCorrections &&
                filteredRotations.TryGetValue(role, out var previousRotation))
            {
                if (Quaternion.Dot(previousRotation, worldRotation) < 0f)
                {
                    worldRotation = new Quaternion(
                        -worldRotation.x,
                        -worldRotation.y,
                        -worldRotation.z,
                        -worldRotation.w);
                }

                worldRotation = Quaternion.SlerpUnclamped(previousRotation, worldRotation, alpha);
            }

            if (applyAutomaticCorrections)
            {
                filteredPositions[role] = worldPosition;
                filteredRotations[role] = worldRotation;
            }
            position = worldPosition;
            rotation = worldRotation;
            return true;
        }

        private static bool TryGetUsable(
            TrackerFrame frame,
            TrackerRole role,
            out TrackerPoseSample sample)
        {
            sample = frame?.Find(role);
            return sample != null && sample.connected && sample.valid &&
                   IsFinite(sample.position) && IsFinite(sample.rotation);
        }

        private float ResolveFloorHeight()
        {
            var found = false;
            var floor = binding.Animator.transform.position.y;
            if (binding.TryGetBone(HumanBodyBones.LeftFoot, out var left))
            {
                floor = left.position.y;
                found = true;
            }

            if (binding.TryGetBone(HumanBodyBones.RightFoot, out var right))
            {
                floor = found ? Mathf.Min(floor, right.position.y) : right.position.y;
            }

            return floor;
        }

        private static Quaternion YawOnly(Quaternion rotation)
        {
            var forward = Vector3.ProjectOnPlane(rotation * Vector3.forward, Vector3.up);
            return forward.sqrMagnitude < 1e-8f
                ? Quaternion.identity
                : Quaternion.LookRotation(forward.normalized, Vector3.up);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

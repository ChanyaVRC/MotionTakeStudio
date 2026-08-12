using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Concrete review evaluator: base HumanPose first, then every manual target correction,
    /// with limb hints solved last so their corresponding hand/foot target remains pinned.
    /// </summary>
    public sealed class MotionTakePreviewDriver : IDisposable, IMotionTakeTargetPoseSource
    {
        private readonly Dictionary<PoseTarget, Vector3> _previousBendDirections =
            new Dictionary<PoseTarget, Vector3>();
        private Animator _animator;
        private MotionTakeAsset _take;
        private MotionEditRecipe _recipe;
        private HumanPoseHandler _poseHandler;
        private IDisposable _stateLease;
        private int _currentFrame;
        private readonly Dictionary<PoseTarget, MotionTakeTargetPose> _baseTargetPoses =
            new Dictionary<PoseTarget, MotionTakeTargetPose>();
        private readonly Dictionary<PoseTarget, MotionTakeTargetPose> _solvedTargetPoses =
            new Dictionary<PoseTarget, MotionTakeTargetPose>();
        private readonly Dictionary<HumanBodyBones, Vector3> _baseBonePositions =
            new Dictionary<HumanBodyBones, Vector3>();
        private readonly List<string> _lastIkWarnings = new List<string>();
        private bool _hipsWasCorrected;
        private bool _hasAppliedFrame;

        public Animator Animator => _animator;
        public MotionTakeAsset Take => _take;
        public MotionEditRecipe Recipe => _recipe;
        public int CurrentFrame => _currentFrame;
        public IReadOnlyList<string> LastIkWarnings => _lastIkWarnings;
        public int LastEvaluationSampleCount { get; private set; }

        public void Bind(
            Animator animator,
            MotionTakeAsset take,
            MotionEditRecipe recipe,
            IAnimatorPreviewStateGuard stateGuard = null)
        {
            DisposeBinding();
            if (animator == null)
            {
                throw new ArgumentNullException(nameof(animator));
            }

            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                throw new ArgumentException("Preview requires a valid Humanoid Animator.", nameof(animator));
            }

            _animator = animator;
            _take = take ?? throw new ArgumentNullException(nameof(take));
            _recipe = recipe;
            _poseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
            _stateLease = (stateGuard ?? new DefaultAnimatorPreviewStateGuard()).Acquire(animator);
            _previousBendDirections.Clear();
            _hasAppliedFrame = false;
            LastEvaluationSampleCount = 0;
        }

        public bool ApplyFrame(int frame)
        {
            if (_animator == null || _take == null || _poseHandler == null ||
                !_take.TryGetFrame(Mathf.Max(0, frame), out var takeFrame) ||
                takeFrame?.ResolvedHumanPose == null)
            {
                return false;
            }

            LastEvaluationSampleCount = 0;
            if (_hasAppliedFrame && takeFrame.FrameIndex == _currentFrame + 1)
            {
                ApplyTakeFrame(takeFrame);
                LastEvaluationSampleCount = 1;
                return true;
            }

            // Rebuild the continuity state from a deterministic lookback. This makes a direct
            // Scene-view scrub produce the same pose as the sequential export path.
            _previousBendDirections.Clear();
            var startFrame = 0;
            for (var sampleFrame = startFrame; sampleFrame <= takeFrame.FrameIndex; sampleFrame++)
            {
                if (!_take.TryGetFrame(sampleFrame, out var sample) || sample?.ResolvedHumanPose == null)
                {
                    continue;
                }

                ApplyTakeFrame(sample);
                LastEvaluationSampleCount++;
            }

            return _currentFrame == takeFrame.FrameIndex;
        }

        public bool TryGetBaseTargetPose(PoseTarget target, int frame, out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            if (_animator == null || _take == null || frame != _currentFrame)
            {
                return false;
            }

            return _baseTargetPoses.TryGetValue(target, out pose);
        }

        /// <summary>Returns the actual post-IK pose, including reach and joint-limit clamps.</summary>
        public bool TryGetSolvedTargetPose(PoseTarget target, int frame, out MotionTakeTargetPose pose)
        {
            pose = default(MotionTakeTargetPose);
            if (_animator == null || frame != _currentFrame)
            {
                return false;
            }

            return _solvedTargetPoses.TryGetValue(target, out pose);
        }

        public void Dispose()
        {
            DisposeBinding();
        }

        private void ApplyBodyCorrections()
        {
            ApplyDirectTarget(PoseTarget.Hips, HumanBodyBones.Hips);
            ApplyDirectTarget(PoseTarget.Head, HumanBodyBones.Head);
        }

        private void ApplyTakeFrame(MotionTakeFrame takeFrame)
        {
            _currentFrame = takeFrame.FrameIndex;
            _lastIkWarnings.Clear();
            var humanPose = takeFrame.ResolvedHumanPose.ToHumanPose();
            _poseHandler.SetHumanPose(ref humanPose);
            CacheBaseTargetPoses();
            _hipsWasCorrected = false;
            ApplyBodyCorrections();
            ApplyLimb(PoseTarget.LeftHand, PoseTarget.LeftElbowHint,
                HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
            ApplyLimb(PoseTarget.RightHand, PoseTarget.RightElbowHint,
                HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
            ApplyLimb(PoseTarget.LeftFoot, PoseTarget.LeftKneeHint,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
            ApplyLimb(PoseTarget.RightFoot, PoseTarget.RightKneeHint,
                HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            CacheSolvedTargetPoses();
            _hasAppliedFrame = true;
        }

        private Vector3 RootLocalVectorToWorld(Vector3 localVector)
        {
            return _animator != null ? _animator.transform.rotation * localVector : localVector;
        }

        private void ApplyDirectTarget(PoseTarget target, HumanBodyBones bone)
        {
            var transform = _animator.GetBoneTransform(bone);
            if (transform == null || !TryEvaluate(target, out var offset))
            {
                return;
            }

            if (target == PoseTarget.Head)
            {
                ApplyHeadCorrection(transform, offset);
                return;
            }

            _hipsWasCorrected = HasMeaningfulOffset(offset);

            if (offset.HasPosition)
            {
                transform.position += RootLocalVectorToWorld(
                    offset.ResolvePositionOffset(_take.HumanScale, GetLimbLength(target)));
            }

            if (offset.HasRotation)
            {
                transform.rotation *= offset.RotationOffsetLocal;
            }
        }

        private void ApplyHeadCorrection(Transform head, MotionPoseTargetOffset offset)
        {
            if (!_baseTargetPoses.TryGetValue(PoseTarget.Head, out var basePose))
            {
                return;
            }

            var hasPosition = offset.HasPosition &&
                              offset.PositionOffsetNormalized.sqrMagnitude > 1e-12f;
            var hasRotation = offset.HasRotation &&
                              Quaternion.Angle(
                                  Quaternion.identity,
                                  offset.RotationOffsetLocal) > 0.0001f;
            var desiredRotation = hasRotation
                ? basePose.WorldRotation * offset.RotationOffsetLocal
                : basePose.WorldRotation;
            if (hasRotation)
            {
                var worldDelta = desiredRotation * Quaternion.Inverse(head.rotation);
                ApplyWorldRotationDelta(HumanBodyBones.Chest, worldDelta, 0.12f);
                ApplyWorldRotationDelta(HumanBodyBones.UpperChest, worldDelta, 0.18f);
                ApplyWorldRotationDelta(HumanBodyBones.Neck, worldDelta, 0.35f);
            }

            // Rotation distribution moves the Head in world space. Position is therefore solved
            // afterwards so a combined position+rotation key reaches the authored handle.
            if (hasPosition)
            {
                var targetPosition = basePose.WorldPosition + RootLocalVectorToWorld(
                    offset.ResolvePositionOffset(_take.HumanScale, basePose.LimbLength));
                SolveHeadPosition(head, targetPosition);
            }

            head.rotation = desiredRotation;
        }

        private void SolveHeadPosition(Transform head, Vector3 targetPosition)
        {
            var chain = new[]
            {
                HumanBodyBones.Neck,
                HumanBodyBones.UpperChest,
                HumanBodyBones.Chest,
                HumanBodyBones.Spine
            };
            for (var iteration = 0; iteration < 4; iteration++)
            {
                for (var index = 0; index < chain.Length; index++)
                {
                    var bone = _animator.GetBoneTransform(chain[index]);
                    if (bone == null)
                    {
                        continue;
                    }

                    var toHead = head.position - bone.position;
                    var toTarget = targetPosition - bone.position;
                    if (toHead.sqrMagnitude <= 1e-10f || toTarget.sqrMagnitude <= 1e-10f)
                    {
                        continue;
                    }

                    bone.rotation = Quaternion.FromToRotation(toHead, toTarget) * bone.rotation;
                }

                if (Vector3.Distance(head.position, targetPosition) <= 0.001f)
                {
                    break;
                }
            }
        }

        private void ApplyWorldRotationDelta(HumanBodyBones bone, Quaternion worldDelta, float weight)
        {
            var transform = _animator.GetBoneTransform(bone);
            if (transform != null)
            {
                transform.rotation = Quaternion.Slerp(Quaternion.identity, worldDelta, weight) * transform.rotation;
            }
        }

        private void ApplyLimb(
            PoseTarget tipTarget,
            PoseTarget hintTarget,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones tipBone)
        {
            var upper = _animator.GetBoneTransform(upperBone);
            var lower = _animator.GetBoneTransform(lowerBone);
            var tip = _animator.GetBoneTransform(tipBone);
            if (upper == null || lower == null || tip == null)
            {
                return;
            }

            if (!_baseTargetPoses.TryGetValue(tipTarget, out var baseTipPose) ||
                !_baseTargetPoses.TryGetValue(hintTarget, out var baseHintPose))
            {
                return;
            }

            var targetPosition = baseTipPose.WorldPosition;
            var targetRotation = baseTipPose.WorldRotation;
            var hintPosition = baseHintPose.WorldPosition;
            var hasTip = TryEvaluate(tipTarget, out var tipOffset);
            var hasHint = TryEvaluate(hintTarget, out var hintOffset);
            var limbLength = baseTipPose.LimbLength;

            if (_hipsWasCorrected && (hasTip || hasHint) &&
                _baseBonePositions.TryGetValue(upperBone, out var baseUpperPosition))
            {
                RepositionArmRoot(upperBone, upper, baseUpperPosition);
            }

            if (hasTip && tipOffset.HasPosition)
            {
                targetPosition += RootLocalVectorToWorld(
                    tipOffset.ResolvePositionOffset(_take.HumanScale, limbLength));
            }

            if (hasTip && tipOffset.HasRotation)
            {
                targetRotation *= tipOffset.RotationOffsetLocal;
            }

            if (hasHint && hintOffset.HasPosition)
            {
                hintPosition += RootLocalVectorToWorld(
                    hintOffset.ResolvePositionOffset(_take.HumanScale, limbLength));
            }

            if (hasTip || hasHint || _hipsWasCorrected)
            {
                _previousBendDirections.TryGetValue(hintTarget, out var previousBend);
                var request = TwoBoneIkRequest.Create(
                    upper.position,
                    lower.position,
                    tip.position,
                    targetPosition,
                    hintPosition,
                    previousBend);
                ApplyAvatarJointLimits(ref request, lowerBone);
                var result = TwoBoneIkSolver.Solve(request);
                if (result.Succeeded)
                {
                    upper.rotation = result.UpperRotationDelta * upper.rotation;
                    lower.rotation = result.LowerRotationDelta * lower.rotation;
                    _previousBendDirections[hintTarget] = result.BendDirection;
                    if (result.HasWarning)
                    {
                        _lastIkWarnings.Add(
                            $"{tipTarget}: IK was clamped ({result.Warning}); end error {result.EndError * 1000f:0.0} mm.");
                    }
                }
                else
                {
                    _lastIkWarnings.Add($"{tipTarget}: IK input was invalid; the base limb pose was kept.");
                }
            }

            // The end-effector rotation is an independent target. An IK position/hint edit
            // must never inherit the solver's incidental chain rotation.
            tip.rotation = targetRotation;
        }

        private bool TryEvaluate(PoseTarget target, out MotionPoseTargetOffset offset)
        {
            if (_recipe != null && _recipe.CorrectionTrack != null &&
                _recipe.CorrectionTrack.TryEvaluate(target, _currentFrame, out offset))
            {
                return true;
            }

            offset = default(MotionPoseTargetOffset);
            return false;
        }

        private void RepositionArmRoot(
            HumanBodyBones upperBone,
            Transform upper,
            Vector3 targetPosition)
        {
            HumanBodyBones shoulder;
            switch (upperBone)
            {
                case HumanBodyBones.LeftUpperArm:
                    shoulder = HumanBodyBones.LeftShoulder;
                    break;
                case HumanBodyBones.RightUpperArm:
                    shoulder = HumanBodyBones.RightShoulder;
                    break;
                default:
                    return;
            }

            var chain = new[]
            {
                shoulder,
                HumanBodyBones.UpperChest,
                HumanBodyBones.Chest,
                HumanBodyBones.Spine
            };
            for (var iteration = 0; iteration < 16; iteration++)
            {
                for (var index = 0; index < chain.Length; index++)
                {
                    var pivot = _animator.GetBoneTransform(chain[index]);
                    if (pivot == null)
                    {
                        continue;
                    }

                    var current = upper.position - pivot.position;
                    var desired = targetPosition - pivot.position;
                    if (current.sqrMagnitude <= 1e-10f || desired.sqrMagnitude <= 1e-10f)
                    {
                        continue;
                    }

                    pivot.rotation = Quaternion.FromToRotation(current, desired) * pivot.rotation;
                }

                if (Vector3.Distance(upper.position, targetPosition) <= 0.0005f)
                {
                    break;
                }
            }
        }

        private void ApplyAvatarJointLimits(ref TwoBoneIkRequest request, HumanBodyBones lowerBone)
        {
            var lower = _animator.GetBoneTransform(lowerBone);
            if (lower == null || _animator.avatar == null)
            {
                return;
            }

            var humanBones = _animator.avatar.humanDescription.human;
            for (var index = 0; index < humanBones.Length; index++)
            {
                var humanBone = humanBones[index];
                if (!string.Equals(humanBone.boneName, lower.name, StringComparison.Ordinal))
                {
                    continue;
                }

                var stretchAxis = FindStretchAxis(lowerBone);
                if (stretchAxis < 0)
                {
                    return;
                }

                var minimum = humanBone.limit.min[stretchAxis];
                var maximum = humanBone.limit.max[stretchAxis];
                if (humanBone.limit.useDefaultValues)
                {
                    var muscle = HumanTrait.MuscleFromBone((int)lowerBone, stretchAxis);
                    if (muscle < 0)
                    {
                        return;
                    }

                    minimum = HumanTrait.GetMuscleDefaultMin(muscle);
                    maximum = HumanTrait.GetMuscleDefaultMax(muscle);
                }

                var bendRange = Mathf.Abs(maximum - minimum);
                if (bendRange > 1f)
                {
                    request.MinimumBendDegrees = 0.5f;
                    request.MaximumBendDegrees = Mathf.Clamp(bendRange, 15f, 179.5f);
                }

                return;
            }
        }

        private static bool HasMeaningfulOffset(MotionPoseTargetOffset offset)
        {
            return offset.HasPosition && offset.PositionOffsetNormalized.sqrMagnitude > 1e-12f ||
                   offset.HasRotation &&
                   Quaternion.Angle(Quaternion.identity, offset.RotationOffsetLocal) > 0.0001f;
        }

        private static int FindStretchAxis(HumanBodyBones bone)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var muscle = HumanTrait.MuscleFromBone((int)bone, axis);
                if (muscle >= 0 && muscle < HumanTrait.MuscleName.Length &&
                    HumanTrait.MuscleName[muscle].IndexOf("Stretch", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return axis;
                }
            }

            return -1;
        }

        private bool TryGetTargetTransform(PoseTarget target, out Transform transform)
        {
            transform = null;
            if (_animator == null)
            {
                return false;
            }

            switch (target)
            {
                case PoseTarget.Head:
                    transform = _animator.GetBoneTransform(HumanBodyBones.Head);
                    break;
                case PoseTarget.Hips:
                    transform = _animator.GetBoneTransform(HumanBodyBones.Hips);
                    break;
                case PoseTarget.LeftHand:
                    transform = _animator.GetBoneTransform(HumanBodyBones.LeftHand);
                    break;
                case PoseTarget.RightHand:
                    transform = _animator.GetBoneTransform(HumanBodyBones.RightHand);
                    break;
                case PoseTarget.LeftFoot:
                    transform = _animator.GetBoneTransform(HumanBodyBones.LeftFoot);
                    break;
                case PoseTarget.RightFoot:
                    transform = _animator.GetBoneTransform(HumanBodyBones.RightFoot);
                    break;
                case PoseTarget.LeftElbowHint:
                    transform = _animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                    break;
                case PoseTarget.RightElbowHint:
                    transform = _animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                    break;
                case PoseTarget.LeftKneeHint:
                    transform = _animator.GetBoneTransform(HumanBodyBones.LeftLowerLeg);
                    break;
                case PoseTarget.RightKneeHint:
                    transform = _animator.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                    break;
            }

            return transform != null;
        }

        private void CacheBaseTargetPoses()
        {
            _baseTargetPoses.Clear();
            _baseBonePositions.Clear();
            _lastIkWarnings.Clear();
            _hipsWasCorrected = false;
            foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
            {
                if (!TryGetTargetTransform(target, out var transform))
                {
                    continue;
                }

                _baseTargetPoses[target] = new MotionTakeTargetPose
                {
                    AvatarRoot = _animator.transform,
                    WorldPosition = transform.position,
                    WorldRotation = transform.rotation,
                    HumanScale = Mathf.Max(0.0001f, _take.HumanScale),
                    LimbLength = GetLimbLength(target)
                };
            }

            CacheBaseBonePosition(HumanBodyBones.LeftUpperArm);
            CacheBaseBonePosition(HumanBodyBones.RightUpperArm);
        }

        private void CacheBaseBonePosition(HumanBodyBones bone)
        {
            var transform = _animator.GetBoneTransform(bone);
            if (transform != null)
            {
                _baseBonePositions[bone] = transform.position;
            }
        }

        private void CacheSolvedTargetPoses()
        {
            _solvedTargetPoses.Clear();
            foreach (PoseTarget target in Enum.GetValues(typeof(PoseTarget)))
            {
                if (!TryGetTargetTransform(target, out var transform))
                {
                    continue;
                }

                var limbLength = _baseTargetPoses.TryGetValue(target, out var basePose)
                    ? basePose.LimbLength
                    : GetLimbLength(target);
                _solvedTargetPoses[target] = new MotionTakeTargetPose
                {
                    AvatarRoot = _animator.transform,
                    WorldPosition = transform.position,
                    WorldRotation = transform.rotation,
                    HumanScale = Mathf.Max(0.0001f, _take.HumanScale),
                    LimbLength = limbLength
                };
            }
        }

        private float GetLimbLength(PoseTarget target)
        {
            HumanBodyBones upper;
            HumanBodyBones lower;
            HumanBodyBones tip;
            switch (target)
            {
                case PoseTarget.LeftHand:
                case PoseTarget.LeftElbowHint:
                    upper = HumanBodyBones.LeftUpperArm;
                    lower = HumanBodyBones.LeftLowerArm;
                    tip = HumanBodyBones.LeftHand;
                    break;
                case PoseTarget.RightHand:
                case PoseTarget.RightElbowHint:
                    upper = HumanBodyBones.RightUpperArm;
                    lower = HumanBodyBones.RightLowerArm;
                    tip = HumanBodyBones.RightHand;
                    break;
                case PoseTarget.LeftFoot:
                case PoseTarget.LeftKneeHint:
                    upper = HumanBodyBones.LeftUpperLeg;
                    lower = HumanBodyBones.LeftLowerLeg;
                    tip = HumanBodyBones.LeftFoot;
                    break;
                case PoseTarget.RightFoot:
                case PoseTarget.RightKneeHint:
                    upper = HumanBodyBones.RightUpperLeg;
                    lower = HumanBodyBones.RightLowerLeg;
                    tip = HumanBodyBones.RightFoot;
                    break;
                default:
                    return Mathf.Max(0.0001f, _take != null ? _take.HumanScale : 1f);
            }

            var upperTransform = _animator.GetBoneTransform(upper);
            var lowerTransform = _animator.GetBoneTransform(lower);
            var tipTransform = _animator.GetBoneTransform(tip);
            if (upperTransform == null || lowerTransform == null || tipTransform == null)
            {
                return Mathf.Max(0.0001f, _take.HumanScale);
            }

            return Mathf.Max(
                0.0001f,
                Vector3.Distance(upperTransform.position, lowerTransform.position) +
                Vector3.Distance(lowerTransform.position, tipTransform.position));
        }

        private void DisposeBinding()
        {
            _stateLease?.Dispose();
            _stateLease = null;
            _poseHandler?.Dispose();
            _poseHandler = null;
            _previousBendDirections.Clear();
            _baseTargetPoses.Clear();
            _solvedTargetPoses.Clear();
            _baseBonePositions.Clear();
            _animator = null;
            _take = null;
            _recipe = null;
            _currentFrame = 0;
            _hasAppliedFrame = false;
            LastEvaluationSampleCount = 0;
        }
    }
}

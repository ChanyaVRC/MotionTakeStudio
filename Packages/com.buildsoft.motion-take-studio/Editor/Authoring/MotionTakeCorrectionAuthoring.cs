using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public static class MotionTakeCorrectionAuthoring
    {
        public static bool TryGetEvaluatedTargetPose(
            MotionEditRecipe recipe,
            IMotionTakeTargetPoseSource poseSource,
            PoseTarget target,
            int frame,
            out MotionTakeTargetPose basePose,
            out Vector3 worldPosition,
            out Quaternion worldRotation)
        {
            basePose = default(MotionTakeTargetPose);
            worldPosition = Vector3.zero;
            worldRotation = Quaternion.identity;
            if (poseSource == null || !poseSource.TryGetBaseTargetPose(target, frame, out basePose))
            {
                return false;
            }

            worldPosition = basePose.WorldPosition;
            worldRotation = basePose.WorldRotation;
            if (recipe == null || recipe.CorrectionTrack == null ||
                !recipe.CorrectionTrack.TryEvaluate(target, frame, out var offset))
            {
                return true;
            }

            if (offset.HasPosition)
            {
                var localOffset = offset.ResolvePositionOffset(basePose.HumanScale, basePose.LimbLength);
                worldPosition += basePose.AvatarRoot != null
                    ? basePose.AvatarRoot.rotation * localOffset
                    : localOffset;
            }

            if (offset.HasRotation)
            {
                worldRotation = worldRotation * offset.RotationOffsetLocal;
            }

            return true;
        }

        public static void SetPosition(
            MotionEditRecipe recipe,
            PoseTarget target,
            int frame,
            int influenceFrames,
            MotionTakeTargetPose basePose,
            Vector3 desiredWorldPosition)
        {
            var fallback = EvaluateExistingOffset(recipe, target, frame);
            MutateTarget(recipe, target, frame, influenceFrames, fallback, offset =>
            {
                var worldDelta = desiredWorldPosition - basePose.WorldPosition;
                var localDelta = basePose.AvatarRoot != null
                    ? Quaternion.Inverse(basePose.AvatarRoot.rotation) * worldDelta
                    : worldDelta;
                var normalization = IsLimbHint(target)
                    ? Mathf.Max(0.0001f, basePose.LimbLength)
                    : Mathf.Max(0.0001f, basePose.HumanScale);
                return MotionPoseTargetOffset.Create(
                    target,
                    true,
                    localDelta / normalization,
                    offset.HasRotation,
                    offset.RotationOffsetLocal);
            });
        }

        public static void SetRotation(
            MotionEditRecipe recipe,
            PoseTarget target,
            int frame,
            int influenceFrames,
            MotionTakeTargetPose basePose,
            Quaternion desiredWorldRotation)
        {
            var fallback = EvaluateExistingOffset(recipe, target, frame);
            MutateTarget(recipe, target, frame, influenceFrames, fallback, offset =>
            {
                return MotionPoseTargetOffset.Create(
                    target,
                    offset.HasPosition,
                    offset.PositionOffsetNormalized,
                    true,
                    Normalize(Quaternion.Inverse(basePose.WorldRotation) * desiredWorldRotation));
            });
        }

        public static void AddPoseKey(MotionEditRecipe recipe, int frame, int influenceFrames)
        {
            if (recipe == null)
            {
                return;
            }

            Undo.RecordObject(recipe, "Add Motion Take Pose Key");
            var key = recipe.CorrectionTrack.GetOrCreateKey(frame, ClampInfluence(influenceFrames));
            key.InfluenceFrames = ClampInfluence(influenceFrames);
            recipe.CorrectionTrack.AddOrReplaceKey(key);
            EditorUtility.SetDirty(recipe);
        }

        public static void AddPoseKey(
            MotionEditRecipe recipe,
            IMotionTakeTargetPoseSource poseSource,
            PoseTarget target,
            int frame,
            int influenceFrames)
        {
            if (recipe == null)
            {
                return;
            }

            Undo.RecordObject(recipe, "Add Motion Take Pose Key");
            var evaluated = EvaluateExistingOffset(recipe, target, frame);
            var key = recipe.CorrectionTrack.GetOrCreateKey(frame, ClampInfluence(influenceFrames));
            key.InfluenceFrames = ClampInfluence(influenceFrames);
            if (!key.TryGetTargetOffset(target, out _) &&
                (evaluated.HasPosition || evaluated.HasRotation))
            {
                key.SetTargetOffset(evaluated);
            }
            else if (!key.TryGetTargetOffset(target, out _) && poseSource != null &&
                     poseSource.TryGetBaseTargetPose(target, frame, out _))
            {
                key.SetTargetOffset(MotionPoseTargetOffset.Create(
                    target,
                    true,
                    Vector3.zero,
                    target.SupportsRotation(),
                    Quaternion.identity));
            }

            recipe.CorrectionTrack.AddOrReplaceKey(key);
            EditorUtility.SetDirty(recipe);
        }

        public static void ResetTarget(MotionEditRecipe recipe, PoseTarget target, int frame)
        {
            MutateExistingKey(recipe, frame, "Reset Motion Take Target", key =>
            {
                key.RemoveTargetOffset(target);
                return key.TargetOffsets != null && key.TargetOffsets.Count > 0;
            });
        }

        public static void DeleteKey(MotionEditRecipe recipe, int frame)
        {
            if (recipe == null || recipe.CorrectionTrack == null)
            {
                return;
            }

            Undo.RecordObject(recipe, "Delete Motion Take Pose Key");
            recipe.CorrectionTrack.RemoveKey(frame);
            EditorUtility.SetDirty(recipe);
        }

        public static bool HasKeyAtFrame(MotionEditRecipe recipe, int frame)
        {
            return recipe != null && recipe.CorrectionTrack != null &&
                   recipe.CorrectionTrack.Keys.Any(key => key.Frame == frame);
        }

        public static bool SupportsRotation(PoseTarget target)
        {
            return !IsLimbHint(target);
        }

        public static bool IsLimbHint(PoseTarget target)
        {
            var name = target.ToString();
            return name.IndexOf("Elbow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Knee", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void MutateTarget(
            MotionEditRecipe recipe,
            PoseTarget target,
            int frame,
            int influenceFrames,
            MotionPoseTargetOffset fallback,
            Func<MotionPoseTargetOffset, MotionPoseTargetOffset> mutate)
        {
            if (recipe == null || mutate == null)
            {
                return;
            }

            Undo.RecordObject(recipe, "Edit Motion Take Pose Key");
            var key = recipe.CorrectionTrack.GetOrCreateKey(frame, ClampInfluence(influenceFrames));
            key.InfluenceFrames = ClampInfluence(influenceFrames);
            if (!key.TryGetTargetOffset(target, out var offset))
            {
                offset = fallback;
            }
            key.SetTargetOffset(mutate(offset));
            recipe.CorrectionTrack.AddOrReplaceKey(key);
            EditorUtility.SetDirty(recipe);
        }

        private static MotionPoseTargetOffset EvaluateExistingOffset(
            MotionEditRecipe recipe,
            PoseTarget target,
            int frame)
        {
            return recipe?.CorrectionTrack != null &&
                   recipe.CorrectionTrack.TryEvaluate(target, frame, out var offset)
                ? offset
                : MotionPoseTargetOffset.Create(target, false, Vector3.zero, false, Quaternion.identity);
        }

        private static void MutateExistingKey(
            MotionEditRecipe recipe,
            int frame,
            string undoLabel,
            Func<MotionPoseKey, bool> mutate)
        {
            if (recipe == null || recipe.CorrectionTrack == null)
            {
                return;
            }

            var key = recipe.CorrectionTrack.Keys.FirstOrDefault(candidate => candidate.Frame == frame);
            if (key == null)
            {
                return;
            }

            Undo.RecordObject(recipe, undoLabel);
            if (mutate(key))
            {
                recipe.CorrectionTrack.AddOrReplaceKey(key);
            }
            else
            {
                recipe.CorrectionTrack.RemoveKey(frame);
            }

            EditorUtility.SetDirty(recipe);
        }

        private static int ClampInfluence(int value)
        {
            return Mathf.Clamp(value, 1, 60);
        }

        private static Quaternion Normalize(Quaternion value)
        {
            var magnitude = Mathf.Sqrt(
                value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (magnitude < 0.000001f)
            {
                return Quaternion.identity;
            }

            var inverse = 1f / magnitude;
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }
    }
}

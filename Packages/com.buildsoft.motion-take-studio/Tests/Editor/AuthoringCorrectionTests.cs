using System;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class AuthoringCorrectionTests
    {
        [Test]
        public void PoseTarget_ContainsAllTenAuthoringTargets()
        {
            Assert.That(Enum.GetValues(typeof(PoseTarget)), Has.Length.EqualTo(10));
        }

        [Test]
        public void SetPosition_NormalizesBodyTargetByHumanScale()
        {
            var root = new GameObject("Root");
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null);
                var basePose = new MotionTakeTargetPose
                {
                    AvatarRoot = root.transform,
                    WorldPosition = new Vector3(1f, 2f, 3f),
                    WorldRotation = Quaternion.identity,
                    HumanScale = 2f,
                    LimbLength = 0.5f
                };

                MotionTakeCorrectionAuthoring.SetPosition(
                    recipe,
                    PoseTarget.Head,
                    10,
                    12,
                    basePose,
                    basePose.WorldPosition + new Vector3(0.4f, 0.2f, -0.6f));

                var key = recipe.CorrectionTrack.Keys[0];
                Assert.That(key.TryGetTargetOffset(PoseTarget.Head, out var offset), Is.True);
                Assert.That(offset.PositionOffsetNormalized.x, Is.EqualTo(0.2f).Within(0.0001f));
                Assert.That(offset.PositionOffsetNormalized.y, Is.EqualTo(0.1f).Within(0.0001f));
                Assert.That(offset.PositionOffsetNormalized.z, Is.EqualTo(-0.3f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SetPosition_NormalizesHintByLimbLength()
        {
            var root = new GameObject("Root");
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null);
                var basePose = new MotionTakeTargetPose
                {
                    AvatarRoot = root.transform,
                    WorldPosition = Vector3.zero,
                    WorldRotation = Quaternion.identity,
                    HumanScale = 2f,
                    LimbLength = 0.5f
                };

                MotionTakeCorrectionAuthoring.SetPosition(
                    recipe,
                    PoseTarget.LeftElbowHint,
                    5,
                    12,
                    basePose,
                    new Vector3(0.25f, 0f, 0f));

                var key = recipe.CorrectionTrack.Keys[0];
                Assert.That(key.TryGetTargetOffset(PoseTarget.LeftElbowHint, out var offset), Is.True);
                Assert.That(offset.PositionOffsetNormalized.x, Is.EqualTo(0.5f).Within(0.0001f));
                Assert.That(offset.HasRotation, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AddPoseKey_ClampsInfluenceToSupportedRange()
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null);
                MotionTakeCorrectionAuthoring.AddPoseKey(recipe, 2, 100);
                Assert.That(recipe.CorrectionTrack.Keys[0].InfluenceFrames, Is.EqualTo(60));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void SetPosition_AtInterpolatedFramePreservesRotationChannel()
        {
            var root = new GameObject("Root");
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null);
                var rotation = Quaternion.Euler(0f, 40f, 0f);
                recipe.CorrectionTrack.GetOrCreateKey(0, 12).SetTargetOffset(
                    MotionPoseTargetOffset.CreateRotation(PoseTarget.LeftHand, rotation));
                recipe.CorrectionTrack.GetOrCreateKey(10, 12).SetTargetOffset(
                    MotionPoseTargetOffset.CreateRotation(PoseTarget.LeftHand, rotation));

                var basePose = new MotionTakeTargetPose
                {
                    AvatarRoot = root.transform,
                    WorldPosition = Vector3.zero,
                    WorldRotation = Quaternion.identity,
                    HumanScale = 1f,
                    LimbLength = 1f
                };
                MotionTakeCorrectionAuthoring.SetPosition(
                    recipe,
                    PoseTarget.LeftHand,
                    5,
                    12,
                    basePose,
                    Vector3.up * 0.1f);

                var middle = recipe.CorrectionTrack.Keys[1];
                Assert.That(middle.TryGetTargetOffset(PoseTarget.LeftHand, out var offset), Is.True);
                Assert.That(offset.HasPosition, Is.True);
                Assert.That(offset.HasRotation, Is.True);
                Assert.That(Quaternion.Angle(rotation, offset.RotationOffsetLocal), Is.LessThan(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void AddPoseKey_CapturesEvaluatedSelectedTargetOffset()
        {
            var root = new GameObject("Root");
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null);
                var first = recipe.CorrectionTrack.GetOrCreateKey(0, 12);
                first.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                    PoseTarget.LeftElbowHint,
                    Vector3.up * 0.2f));
                var last = recipe.CorrectionTrack.GetOrCreateKey(10, 12);
                last.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                    PoseTarget.LeftElbowHint,
                    Vector3.up * 0.4f));
                var poseSource = new ConstantPoseSource(root.transform);

                MotionTakeCorrectionAuthoring.AddPoseKey(
                    recipe,
                    poseSource,
                    PoseTarget.LeftElbowHint,
                    5,
                    12);

                var middle = recipe.CorrectionTrack.Keys[1];
                Assert.That(middle.TryGetTargetOffset(PoseTarget.LeftElbowHint, out var offset), Is.True);
                Assert.That(offset.PositionOffsetNormalized.y, Is.EqualTo(0.3f).Within(0.0001f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private sealed class ConstantPoseSource : IMotionTakeTargetPoseSource
        {
            private readonly Transform _root;

            public ConstantPoseSource(Transform root)
            {
                _root = root;
            }

            public bool TryGetBaseTargetPose(PoseTarget target, int frame, out MotionTakeTargetPose pose)
            {
                pose = new MotionTakeTargetPose
                {
                    AvatarRoot = _root,
                    WorldPosition = Vector3.zero,
                    WorldRotation = Quaternion.identity,
                    HumanScale = 1f,
                    LimbLength = 1f
                };
                return true;
            }
        }
    }
}

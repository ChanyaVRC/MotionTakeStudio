using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    public sealed class MotionTakeAuthoringAcceptanceTests
    {
        [TestCase(PoseTarget.Head)]
        [TestCase(PoseTarget.Hips)]
        [TestCase(PoseTarget.LeftHand)]
        [TestCase(PoseTarget.RightHand)]
        [TestCase(PoseTarget.LeftFoot)]
        [TestCase(PoseTarget.RightFoot)]
        public void RotationCapableTarget_PositionAndRotationAuthoring_ReachesSolvedPreview(
            PoseTarget target)
        {
            using (var fixture =
                   new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            {
                var take = fixture.CreateTake(1, 60f);
                var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
                recipe.Initialize(take, "All Target Authoring Acceptance");
                try
                {
                    MotionTakeTargetPose basePose;
                    using (var baseline = new MotionTakePreviewDriver())
                    {
                        baseline.Bind(fixture.Animator, take, null);
                        Assert.That(baseline.ApplyFrame(0), Is.True);
                        Assert.That(baseline.TryGetBaseTargetPose(target, 0, out basePose), Is.True);
                    }

                    var positionDelta = PositionDeltaFor(target, take.HumanScale);
                    var desiredPosition = basePose.WorldPosition + positionDelta;
                    var desiredRotation = basePose.WorldRotation * Quaternion.Euler(3f, -4f, 5f);
                    MotionTakeCorrectionAuthoring.SetPosition(
                        recipe,
                        target,
                        0,
                        MotionPoseKey.DefaultInfluenceFrames,
                        basePose,
                        desiredPosition);
                    MotionTakeCorrectionAuthoring.SetRotation(
                        recipe,
                        target,
                        0,
                        MotionPoseKey.DefaultInfluenceFrames,
                        basePose,
                        desiredRotation);

                    Assert.That(recipe.CorrectionTrack.Keys, Has.Count.EqualTo(1));
                    Assert.That(recipe.CorrectionTrack.Keys[0].TryGetTargetOffset(target, out var offset),
                        Is.True);
                    Assert.That(offset.HasPosition, Is.True);
                    Assert.That(offset.HasRotation, Is.True);
                    Assert.That(offset.PositionOffsetNormalized.sqrMagnitude, Is.GreaterThan(0f));
                    Assert.That(Quaternion.Angle(offset.RotationOffsetLocal, Quaternion.identity),
                        Is.GreaterThan(0.1f));

                    using (var corrected = new MotionTakePreviewDriver())
                    {
                        corrected.Bind(fixture.Animator, take, recipe);
                        Assert.That(corrected.ApplyFrame(0), Is.True);
                        Assert.That(corrected.TryGetSolvedTargetPose(target, 0, out var solved), Is.True);
                        Assert.That(Vector3.Distance(solved.WorldPosition, desiredPosition),
                            Is.LessThanOrEqualTo(PositionToleranceFor(target)),
                            target + " position correction did not reach the solved preview target.");
                        Assert.That(Quaternion.Angle(solved.WorldRotation, desiredRotation),
                            Is.LessThanOrEqualTo(0.25f),
                            target + " rotation correction did not reach the solved preview target.");
                    }
                }
                finally
                {
                    Undo.ClearUndo(recipe);
                    Object.DestroyImmediate(recipe);
                    Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void AddAndDeletePoseKey_UndoRedoRoundTripsRecipeState()
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            recipe.Initialize(null, "Undo Acceptance");
            try
            {
                Undo.IncrementCurrentGroup();
                var addGroup = Undo.GetCurrentGroup();
                MotionTakeCorrectionAuthoring.AddPoseKey(recipe, 12, 12);
                Undo.CollapseUndoOperations(addGroup);
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.True);

                Undo.PerformUndo();
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.False,
                    "Undo must remove the newly authored pose key.");
                Undo.PerformRedo();
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.True,
                    "Redo must restore the authored pose key.");

                Undo.IncrementCurrentGroup();
                var deleteGroup = Undo.GetCurrentGroup();
                MotionTakeCorrectionAuthoring.DeleteKey(recipe, 12);
                Undo.CollapseUndoOperations(deleteGroup);
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.False,
                    "DeleteKey must remove the complete pose key.");

                Undo.PerformUndo();
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.True,
                    "Undo must restore a deleted pose key and its serialized correction track state.");
                Undo.PerformRedo();
                Assert.That(MotionTakeCorrectionAuthoring.HasKeyAtFrame(recipe, 12), Is.False,
                    "Redo must delete the pose key again.");
            }
            finally
            {
                Undo.ClearUndo(recipe);
                Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void InfluenceFrames_AuthoringApiClampsToInclusiveOneThroughSixty()
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            recipe.Initialize(null, "Influence Boundary Acceptance");
            try
            {
                MotionTakeCorrectionAuthoring.AddPoseKey(recipe, 5, 0);
                MotionTakeCorrectionAuthoring.AddPoseKey(recipe, 10, 61);

                Assert.That(recipe.CorrectionTrack.Keys, Has.Count.EqualTo(2));
                Assert.That(recipe.CorrectionTrack.Keys[0].Frame, Is.EqualTo(5));
                Assert.That(recipe.CorrectionTrack.Keys[0].InfluenceFrames,
                    Is.EqualTo(MotionPoseKey.MinimumInfluenceFrames));
                Assert.That(recipe.CorrectionTrack.Keys[1].Frame, Is.EqualTo(10));
                Assert.That(recipe.CorrectionTrack.Keys[1].InfluenceFrames,
                    Is.EqualTo(MotionPoseKey.MaximumInfluenceFrames));
            }
            finally
            {
                Undo.ClearUndo(recipe);
                Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void ResetTarget_RemovesOnlySelectedSideFromSharedPoseKey()
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            recipe.Initialize(null, "Per Target Reset Acceptance");
            try
            {
                var key = recipe.CorrectionTrack.GetOrCreateKey(24, 12);
                key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                    PoseTarget.LeftHand,
                    new Vector3(0.1f, 0f, 0f)));
                key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                    PoseTarget.RightHand,
                    new Vector3(-0.1f, 0f, 0f)));
                recipe.CorrectionTrack.AddOrReplaceKey(key);

                MotionTakeCorrectionAuthoring.ResetTarget(recipe, PoseTarget.LeftHand, 24);

                Assert.That(recipe.CorrectionTrack.Keys, Has.Count.EqualTo(1),
                    "Resetting one side must not delete a key still used by the opposite side.");
                Assert.That(recipe.CorrectionTrack.Keys[0].TryGetTargetOffset(
                    PoseTarget.LeftHand,
                    out _), Is.False);
                Assert.That(recipe.CorrectionTrack.Keys[0].TryGetTargetOffset(
                    PoseTarget.RightHand,
                    out var rightOffset), Is.True);
                Assert.That(rightOffset.PositionOffsetNormalized,
                    Is.EqualTo(new Vector3(-0.1f, 0f, 0f)));
            }
            finally
            {
                Undo.ClearUndo(recipe);
                Object.DestroyImmediate(recipe);
            }
        }

        private static Vector3 PositionDeltaFor(PoseTarget target, float humanScale)
        {
            var scale = Mathf.Max(0.0001f, humanScale);
            switch (target)
            {
                case PoseTarget.Head:
                    // The generated neck/spine starts fully extended vertically. Move laterally
                    // and slightly down so the position target is geometrically reachable without
                    // stretching bones beyond the Humanoid skeleton.
                    return new Vector3(0.006f, -0.004f, 0.004f) * scale;
                case PoseTarget.Hips:
                    return new Vector3(0.008f, 0.006f, -0.004f) * scale;
                case PoseTarget.LeftHand:
                    return new Vector3(0.004f, 0.008f, 0.006f) * scale;
                case PoseTarget.RightHand:
                    return new Vector3(-0.004f, 0.008f, 0.006f) * scale;
                case PoseTarget.LeftFoot:
                    return new Vector3(0.003f, 0.006f, 0.004f) * scale;
                case PoseTarget.RightFoot:
                    return new Vector3(-0.003f, 0.006f, 0.004f) * scale;
                default:
                    return Vector3.zero;
            }
        }

        private static float PositionToleranceFor(PoseTarget target)
        {
            return 0.005f;
        }
    }
}

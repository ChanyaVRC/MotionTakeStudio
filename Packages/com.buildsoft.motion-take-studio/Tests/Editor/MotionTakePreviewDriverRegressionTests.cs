using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class MotionTakePreviewDriverRegressionTests
    {
        private const float PositionTolerance = 0.002f;
        private const float RotationToleranceDegrees = 0.1f;

        [Test]
        public void HipsWithHandAndHintCorrections_StillSolvesCachedBaseTargets()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baseline = fixture.CaptureLeftArm();
                    }

                    var hipsDelta = new Vector3(0.2f, 0.15f, -0.1f);
                    var handDelta = new Vector3(0.12f, 0.08f, 0.04f);
                    var hintDelta = new Vector3(0f, 0.18f, 0.12f);
                    SetPosition(recipe, 0, PoseTarget.Hips, hipsDelta, take.HumanScale);
                    SetPosition(recipe, 0, PoseTarget.LeftHand, handDelta, take.HumanScale);
                    SetPosition(
                        recipe,
                        0,
                        PoseTarget.LeftElbowHint,
                        hintDelta,
                        baseline.LeftArmLength);

                    var expected = TwoBoneIkSolver.Solve(TwoBoneIkRequest.Create(
                        baseline.UpperPosition,
                        baseline.LowerPosition,
                        baseline.TipPosition,
                        baseline.TipPosition + handDelta,
                        baseline.LowerPosition + hintDelta));
                    Assert.That(expected.Succeeded, Is.True, "The regression setup must be reachable.");

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        AssertVector(
                            fixture.Bone(HumanBodyBones.LeftHand).position,
                            expected.TipPosition,
                            0.005f,
                            "Hand target must remain base hand + hand offset when Hips also has a key.");
                        var actualUpper = fixture.Bone(HumanBodyBones.LeftUpperArm).position;
                        var actualLower = fixture.Bone(HumanBodyBones.LeftLowerArm).position;
                        var actualTip = fixture.Bone(HumanBodyBones.LeftHand).position;
                        var axis = (actualTip - actualUpper).normalized;
                        var actualBend = actualLower - actualUpper;
                        actualBend -= axis * Vector3.Dot(actualBend, axis);
                        var desiredHint = baseline.LowerPosition + hintDelta - actualUpper;
                        desiredHint -= axis * Vector3.Dot(desiredHint, axis);
                        Assert.That(actualBend.sqrMagnitude, Is.GreaterThan(1e-8f));
                        Assert.That(desiredHint.sqrMagnitude, Is.GreaterThan(1e-8f));
                        Assert.That(
                            Vector3.Dot(actualBend.normalized, desiredHint.normalized),
                            Is.GreaterThan(0.5f),
                            "The elbow must bend toward the requested hint side; the hint itself is not a joint-position target.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [TestCase(PoseTarget.LeftHand, HumanBodyBones.LeftHand)]
        [TestCase(PoseTarget.LeftFoot, HumanBodyBones.LeftFoot)]
        public void PositionOnlyTipCorrection_PreservesTipWorldRotation(
            PoseTarget target,
            HumanBodyBones tipBone)
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baseline = target == PoseTarget.LeftHand
                            ? fixture.CaptureLeftArm()
                            : fixture.CaptureLeftLeg();
                    }

                    var targetDirection = target == PoseTarget.LeftHand
                        ? new Vector3(-0.35f, 0.55f, 0.35f).normalized
                        : new Vector3(-0.35f, -0.55f, 0.35f).normalized;
                    var desiredTip = baseline.UpperPosition +
                                     targetDirection * baseline.LeftArmLength * 0.72f;
                    var delta = desiredTip - baseline.TipPosition;
                    SetPosition(recipe, 0, target, delta, take.HumanScale);

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        Assert.That(
                            Quaternion.Angle(baseline.TipRotation, fixture.Bone(tipBone).rotation),
                            Is.LessThanOrEqualTo(RotationToleranceDegrees),
                            "A position-only target must not implicitly rotate its Hand/Foot target.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [TestCase(PoseTarget.LeftElbowHint, HumanBodyBones.LeftHand)]
        [TestCase(PoseTarget.LeftKneeHint, HumanBodyBones.LeftFoot)]
        public void HintOnlyCorrection_PreservesTipWorldRotation(
            PoseTarget hintTarget,
            HumanBodyBones tipBone)
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baseline = hintTarget == PoseTarget.LeftElbowHint
                            ? fixture.CaptureLeftArm()
                            : fixture.CaptureLeftLeg();
                    }

                    var hintDelta = hintTarget == PoseTarget.LeftElbowHint
                        ? new Vector3(0f, 0.15f, 0.15f)
                        : new Vector3(0.15f, 0f, 0.15f);
                    SetPosition(recipe, 0, hintTarget, hintDelta, baseline.LeftArmLength);

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        Assert.That(
                            Quaternion.Angle(baseline.TipRotation, fixture.Bone(tipBone).rotation),
                            Is.LessThanOrEqualTo(RotationToleranceDegrees),
                            "A position-only elbow/knee hint must preserve the fixed Hand/Foot rotation.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void HeadPositionOnlyCorrection_PreservesBaseHeadWorldRotation()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    Quaternion baselineRotation;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baselineRotation = fixture.Bone(HumanBodyBones.Head).rotation;
                    }

                    SetPosition(
                        recipe,
                        0,
                        PoseTarget.Head,
                        new Vector3(0.12f, 0.04f, 0.1f),
                        take.HumanScale);

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        Assert.That(
                            Quaternion.Angle(
                                baselineRotation,
                                fixture.Bone(HumanBodyBones.Head).rotation),
                            Is.LessThanOrEqualTo(RotationToleranceDegrees),
                            "Head position CCD must restore the requested/base Head rotation.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void RandomAccessScrub_MatchesSequentialExportEvaluation()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(2, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baseline = fixture.CaptureLeftArm();
                    }

                    var inward = new Vector3(0.12f, 0f, 0f);
                    SetPosition(recipe, 0, PoseTarget.LeftHand, inward, take.HumanScale);
                    SetPosition(recipe, 1, PoseTarget.LeftHand, inward, take.HumanScale);
                    SetPosition(
                        recipe,
                        0,
                        PoseTarget.LeftElbowHint,
                        OppositeBendDelta(baseline, false),
                        baseline.LeftArmLength);
                    SetPosition(
                        recipe,
                        1,
                        PoseTarget.LeftElbowHint,
                        OppositeBendDelta(baseline, true),
                        baseline.LeftArmLength);

                    PoseSnapshot sequential;
                    using (var sequentialDriver = fixture.Bind(take, recipe))
                    {
                        Assert.That(sequentialDriver.ApplyFrame(0), Is.True);
                        Assert.That(sequentialDriver.ApplyFrame(1), Is.True);
                        sequential = fixture.CaptureLeftArm();
                    }

                    PoseSnapshot randomAccess;
                    using (var scrubDriver = fixture.Bind(take, recipe))
                    {
                        Assert.That(scrubDriver.ApplyFrame(1), Is.True);
                        randomAccess = fixture.CaptureLeftArm();
                    }

                    AssertPoseEqual(
                        randomAccess,
                        sequential,
                        "Scrubbing directly to a frame must equal the same frame baked sequentially.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void SequentialEvaluation_DoesNotReplayTheWholeTakeForEveryFrame()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(3, true);
                try
                {
                    using (var driver = fixture.Bind(take, null))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        Assert.That(driver.ApplyFrame(1), Is.True);
                        Assert.That(driver.LastEvaluationSampleCount, Is.EqualTo(1),
                            "Sequential export/validation should evaluate only the newly requested frame.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void DefaultHumanoidLowerArmLimit_ConstrainsSolverBend()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    using (var driver = fixture.Bind(take, recipe))
                    {
                        var request = CreateLimitProbeRequest();
                        request = InvokeApplyAvatarJointLimits(
                            driver,
                            request,
                            HumanBodyBones.LeftLowerArm);

                        var bendAxis = FindStretchAxis(HumanBodyBones.LeftLowerArm);
                        var muscle = HumanTrait.MuscleFromBone(
                            (int)HumanBodyBones.LeftLowerArm,
                            bendAxis);
                        var expectedRange = HumanTrait.GetMuscleDefaultMax(muscle) -
                                            HumanTrait.GetMuscleDefaultMin(muscle);
                        Assert.That(expectedRange, Is.GreaterThan(1f));
                        Assert.That(
                            request.MaximumBendDegrees,
                            Is.EqualTo(expectedRange).Within(0.5f),
                            "useDefaultValues must resolve through HumanTrait instead of leaving 179.5 degrees.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void CustomHumanoidLowerArmLimit_UsesStretchAxisNotLargestAxis()
        {
            var bendAxis = FindStretchAxis(HumanBodyBones.LeftLowerArm);
            var minimum = new Vector3(-160f, -160f, -160f);
            var maximum = new Vector3(160f, 160f, 160f);
            SetAxis(ref minimum, bendAxis, -20f);
            SetAxis(ref maximum, bendAxis, 50f);
            var customLimit = new HumanLimit
            {
                useDefaultValues = false,
                min = minimum,
                max = maximum,
                center = Vector3.zero,
                axisLength = 0.3f
            };

            using (var fixture = new GeneratedHumanoidFixture(customLimit))
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    using (var driver = fixture.Bind(take, recipe))
                    {
                        var request = InvokeApplyAvatarJointLimits(
                            driver,
                            CreateLimitProbeRequest(),
                            HumanBodyBones.LeftLowerArm);
                        Assert.That(
                            request.MaximumBendDegrees,
                            Is.EqualTo(70f).Within(0.5f),
                            "The stretch-axis range is 70 degrees; unrelated twist/swing axes are 320 degrees.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void HintNormalization_IsInvariantUnderNonUnitAvatarRootScale()
        {
            var unitRoot = new GameObject("UnitRoot");
            var scaledRoot = new GameObject("ScaledRoot");
            var unitRecipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            var scaledRecipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                unitRoot.transform.localScale = Vector3.one;
                scaledRoot.transform.localScale = Vector3.one * 2f;
                unitRecipe.Initialize(null);
                scaledRecipe.Initialize(null);
                var localDelta = new Vector3(0.1f, 0.05f, -0.02f);

                var unitPose = new MotionTakeTargetPose
                {
                    AvatarRoot = unitRoot.transform,
                    WorldPosition = unitRoot.transform.TransformPoint(new Vector3(0.3f, 1f, 0f)),
                    WorldRotation = Quaternion.identity,
                    HumanScale = 1f,
                    LimbLength = 1f
                };
                var scaledPose = new MotionTakeTargetPose
                {
                    AvatarRoot = scaledRoot.transform,
                    WorldPosition = scaledRoot.transform.TransformPoint(new Vector3(0.3f, 1f, 0f)),
                    WorldRotation = Quaternion.identity,
                    HumanScale = 2f,
                    LimbLength = 2f
                };

                MotionTakeCorrectionAuthoring.SetPosition(
                    unitRecipe,
                    PoseTarget.LeftElbowHint,
                    0,
                    12,
                    unitPose,
                    unitPose.WorldPosition + unitRoot.transform.TransformVector(localDelta));
                MotionTakeCorrectionAuthoring.SetPosition(
                    scaledRecipe,
                    PoseTarget.LeftElbowHint,
                    0,
                    12,
                    scaledPose,
                    scaledPose.WorldPosition + scaledRoot.transform.TransformVector(localDelta));

                Assert.That(
                    unitRecipe.CorrectionTrack.Keys[0].TryGetTargetOffset(
                        PoseTarget.LeftElbowHint,
                        out var unitOffset),
                    Is.True);
                Assert.That(
                    scaledRecipe.CorrectionTrack.Keys[0].TryGetTargetOffset(
                        PoseTarget.LeftElbowHint,
                        out var scaledOffset),
                    Is.True);
                AssertVector(
                    scaledOffset.PositionOffsetNormalized,
                    unitOffset.PositionOffsetNormalized,
                    0.00001f,
                    "A geometrically identical local hint edit must serialize identically at root scale 1 and 2.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unitRecipe);
                UnityEngine.Object.DestroyImmediate(scaledRecipe);
                UnityEngine.Object.DestroyImmediate(unitRoot);
                UnityEngine.Object.DestroyImmediate(scaledRoot);
            }
        }

        [Test]
        public void FadeBoundary_RestoresExactBasePoseWithoutContinuityResidue()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(2, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(1), Is.True);
                        baseline = fixture.CaptureLeftArm();
                    }

                    var oppositeDelta = OppositeBendDelta(baseline, true);
                    var key = recipe.CorrectionTrack.GetOrCreateKey(0, 1);
                    key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                        PoseTarget.LeftElbowHint,
                        fixture.Root.transform.InverseTransformVector(oppositeDelta) /
                        baseline.LeftArmLength));

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        Assert.That(driver.ApplyFrame(1), Is.True);
                        AssertPoseEqual(
                            fixture.CaptureLeftArm(),
                            baseline,
                            "At key + influence, the correction is exactly zero and must leave no stateful IK residue.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void PreviewDriver_ExposesActualClampedManualPoseForSceneMarker()
        {
            using (var fixture = new GeneratedHumanoidFixture())
            {
                var take = fixture.CreateTake(1, true);
                var recipe = CreateRecipe(take);
                try
                {
                    PoseSnapshot baseline;
                    using (var baselineDriver = fixture.Bind(take, null))
                    {
                        Assert.That(baselineDriver.ApplyFrame(0), Is.True);
                        baseline = fixture.CaptureLeftArm();
                    }

                    SetPosition(
                        recipe,
                        0,
                        PoseTarget.LeftHand,
                        Vector3.left * baseline.LeftArmLength * 3f,
                        take.HumanScale);

                    using (var driver = fixture.Bind(take, recipe))
                    {
                        Assert.That(driver.ApplyFrame(0), Is.True);
                        var actualTip = fixture.Bone(HumanBodyBones.LeftHand).position;
                        var method = typeof(MotionTakePreviewDriver).GetMethod(
                            "TryGetSolvedTargetPose",
                            BindingFlags.Instance | BindingFlags.Public,
                            null,
                            new[]
                            {
                                typeof(PoseTarget),
                                typeof(int),
                                typeof(MotionTakeTargetPose).MakeByRefType()
                            },
                            null);
                        Assert.That(
                            method,
                            Is.Not.Null,
                            "Scene overlays need a solved-pose API; requested base+offset is unreachable here.");

                        var arguments = new object[]
                        {
                            PoseTarget.LeftHand,
                            0,
                            default(MotionTakeTargetPose)
                        };
                        Assert.That((bool)method.Invoke(driver, arguments), Is.True);
                        var solved = (MotionTakeTargetPose)arguments[2];
                        AssertVector(solved.WorldPosition, actualTip, PositionTolerance);

                        Assert.That(
                            MotionTakeCorrectionAuthoring.TryGetEvaluatedTargetPose(
                                recipe,
                                driver,
                                PoseTarget.LeftHand,
                                0,
                                out _,
                                out var requested,
                                out _),
                            Is.True);
                        Assert.That(
                            Vector3.Distance(solved.WorldPosition, requested),
                            Is.GreaterThan(0.1f),
                            "The exposed marker pose must be the clamped solve, not the unreachable request.");
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        private static MotionEditRecipe CreateRecipe(MotionTakeAsset take)
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            recipe.Initialize(take);
            return recipe;
        }

        private static void SetPosition(
            MotionEditRecipe recipe,
            int frame,
            PoseTarget target,
            Vector3 rootLocalDelta,
            float normalizationScale)
        {
            var key = recipe.CorrectionTrack.GetOrCreateKey(frame, 1);
            key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                target,
                rootLocalDelta / Mathf.Max(0.0001f, normalizationScale)));
        }

        private static TwoBoneIkRequest CreateLimitProbeRequest()
        {
            return TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.4f, 0.3f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.3f, 0f, 0f),
                Vector3.up);
        }

        private static TwoBoneIkRequest InvokeApplyAvatarJointLimits(
            MotionTakePreviewDriver driver,
            TwoBoneIkRequest request,
            HumanBodyBones lowerBone)
        {
            var method = typeof(MotionTakePreviewDriver).GetMethod(
                "ApplyAvatarJointLimits",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { request, lowerBone };
            method.Invoke(driver, arguments);
            return (TwoBoneIkRequest)arguments[0];
        }

        private static int FindStretchAxis(HumanBodyBones bone)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                var muscle = HumanTrait.MuscleFromBone((int)bone, axis);
                if (muscle >= 0 && muscle < HumanTrait.MuscleName.Length &&
                    HumanTrait.MuscleName[muscle].IndexOf(
                        "Stretch",
                        StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return axis;
                }
            }

            Assert.Fail($"Could not resolve the stretch axis for {bone}.");
            return 0;
        }

        private static void SetAxis(ref Vector3 value, int axis, float component)
        {
            switch (axis)
            {
                case 0:
                    value.x = component;
                    break;
                case 1:
                    value.y = component;
                    break;
                default:
                    value.z = component;
                    break;
            }
        }

        private static Vector3 OppositeBendDelta(PoseSnapshot pose, bool opposite)
        {
            var axis = (pose.TipPosition - pose.UpperPosition).normalized;
            var bend = pose.LowerPosition - pose.UpperPosition;
            bend -= axis * Vector3.Dot(bend, axis);
            bend = bend.sqrMagnitude > 1e-8f ? bend.normalized : Vector3.up;
            var distance = Mathf.Max(0.1f, pose.LeftArmLength * 0.4f);
            var desiredHint = pose.UpperPosition + (opposite ? -bend : bend) * distance;
            return desiredHint - pose.LowerPosition;
        }

        private static void AssertPoseEqual(PoseSnapshot actual, PoseSnapshot expected, string message)
        {
            AssertVector(actual.UpperPosition, expected.UpperPosition, PositionTolerance, message);
            AssertVector(actual.LowerPosition, expected.LowerPosition, PositionTolerance, message);
            AssertVector(actual.TipPosition, expected.TipPosition, PositionTolerance, message);
            Assert.That(
                Quaternion.Angle(actual.UpperRotation, expected.UpperRotation),
                Is.LessThanOrEqualTo(RotationToleranceDegrees),
                message);
            Assert.That(
                Quaternion.Angle(actual.LowerRotation, expected.LowerRotation),
                Is.LessThanOrEqualTo(RotationToleranceDegrees),
                message);
            Assert.That(
                Quaternion.Angle(actual.TipRotation, expected.TipRotation),
                Is.LessThanOrEqualTo(RotationToleranceDegrees),
                message);
        }

        private static void AssertVector(
            Vector3 actual,
            Vector3 expected,
            float tolerance,
            string message = null)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThanOrEqualTo(tolerance), message);
        }

        private readonly struct PoseSnapshot
        {
            public PoseSnapshot(Transform upper, Transform lower, Transform tip)
            {
                UpperPosition = upper.position;
                LowerPosition = lower.position;
                TipPosition = tip.position;
                UpperRotation = upper.rotation;
                LowerRotation = lower.rotation;
                TipRotation = tip.rotation;
                LeftArmLength = Vector3.Distance(upper.position, lower.position) +
                                Vector3.Distance(lower.position, tip.position);
            }

            public Vector3 UpperPosition { get; }
            public Vector3 LowerPosition { get; }
            public Vector3 TipPosition { get; }
            public Quaternion UpperRotation { get; }
            public Quaternion LowerRotation { get; }
            public Quaternion TipRotation { get; }
            public float LeftArmLength { get; }
        }

        private sealed class GeneratedHumanoidFixture : IDisposable
        {
            private readonly Dictionary<HumanBodyBones, Transform> _bones =
                new Dictionary<HumanBodyBones, Transform>();
            private readonly List<Transform> _skeleton = new List<Transform>();
            private Avatar _avatar;

            public GeneratedHumanoidFixture(HumanLimit? leftLowerArmLimit = null)
            {
                Root = new GameObject("MotionTakeTestAvatar");
                _skeleton.Add(Root.transform);

                var hips = AddBone(HumanBodyBones.Hips, Root.transform, new Vector3(0f, 1f, 0f));
                var spine = AddBone(HumanBodyBones.Spine, hips, new Vector3(0f, 0.2f, 0f));
                var chest = AddBone(HumanBodyBones.Chest, spine, new Vector3(0f, 0.2f, 0f));
                var neck = AddBone(HumanBodyBones.Neck, chest, new Vector3(0f, 0.18f, 0f));
                AddBone(HumanBodyBones.Head, neck, new Vector3(0f, 0.16f, 0f));

                var leftShoulder = AddBone(
                    HumanBodyBones.LeftShoulder,
                    chest,
                    new Vector3(-0.12f, 0.1f, 0f));
                var leftUpperArm = AddBone(
                    HumanBodyBones.LeftUpperArm,
                    leftShoulder,
                    new Vector3(-0.12f, 0f, 0f));
                var leftLowerArm = AddBone(
                    HumanBodyBones.LeftLowerArm,
                    leftUpperArm,
                    new Vector3(-0.34f, 0f, 0f));
                AddBone(HumanBodyBones.LeftHand, leftLowerArm, new Vector3(-0.3f, 0f, 0f));

                var rightShoulder = AddBone(
                    HumanBodyBones.RightShoulder,
                    chest,
                    new Vector3(0.12f, 0.1f, 0f));
                var rightUpperArm = AddBone(
                    HumanBodyBones.RightUpperArm,
                    rightShoulder,
                    new Vector3(0.12f, 0f, 0f));
                var rightLowerArm = AddBone(
                    HumanBodyBones.RightLowerArm,
                    rightUpperArm,
                    new Vector3(0.34f, 0f, 0f));
                AddBone(HumanBodyBones.RightHand, rightLowerArm, new Vector3(0.3f, 0f, 0f));

                var leftUpperLeg = AddBone(
                    HumanBodyBones.LeftUpperLeg,
                    hips,
                    new Vector3(-0.1f, -0.08f, 0f));
                var leftLowerLeg = AddBone(
                    HumanBodyBones.LeftLowerLeg,
                    leftUpperLeg,
                    new Vector3(0f, -0.43f, 0f));
                AddBone(HumanBodyBones.LeftFoot, leftLowerLeg, new Vector3(0f, -0.42f, 0.08f));

                var rightUpperLeg = AddBone(
                    HumanBodyBones.RightUpperLeg,
                    hips,
                    new Vector3(0.1f, -0.08f, 0f));
                var rightLowerLeg = AddBone(
                    HumanBodyBones.RightLowerLeg,
                    rightUpperLeg,
                    new Vector3(0f, -0.43f, 0f));
                AddBone(HumanBodyBones.RightFoot, rightLowerLeg, new Vector3(0f, -0.42f, 0.08f));

                var humanBones = _bones.Select(pair =>
                {
                    var limit = new HumanLimit { useDefaultValues = true };
                    if (pair.Key == HumanBodyBones.LeftLowerArm && leftLowerArmLimit.HasValue)
                    {
                        limit = leftLowerArmLimit.Value;
                    }

                    return new HumanBone
                    {
                        boneName = pair.Value.name,
                        humanName = pair.Key.ToString(),
                        limit = limit
                    };
                }).ToArray();
                var skeletonBones = _skeleton.Select(transform => new SkeletonBone
                {
                    name = transform.name,
                    position = transform.localPosition,
                    rotation = transform.localRotation,
                    scale = transform.localScale
                }).ToArray();
                var description = new HumanDescription
                {
                    human = humanBones,
                    skeleton = skeletonBones,
                    upperArmTwist = 0.5f,
                    lowerArmTwist = 0.5f,
                    upperLegTwist = 0.5f,
                    lowerLegTwist = 0.5f,
                    armStretch = 0.05f,
                    legStretch = 0.05f,
                    feetSpacing = 0f,
                    hasTranslationDoF = false
                };

                _avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
                Assert.That(_avatar, Is.Not.Null);
                Assert.That(_avatar.isValid, Is.True, "Generated test Avatar must be valid.");
                Assert.That(_avatar.isHuman, Is.True, "Generated test Avatar must be Humanoid.");
                Animator = Root.AddComponent<Animator>();
                Animator.avatar = _avatar;
                Animator.applyRootMotion = false;
                Animator.Rebind();
                Animator.Update(0f);
            }

            public GameObject Root { get; }
            public Animator Animator { get; }

            public Transform Bone(HumanBodyBones bone)
            {
                return _bones[bone];
            }

            public MotionTakeAsset CreateTake(int frameCount, bool bendLimbs)
            {
                var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                using (var handler = new HumanPoseHandler(_avatar, Root.transform))
                {
                    handler.GetHumanPose(ref pose);
                    pose.muscles = pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount
                        ? new float[HumanTrait.MuscleCount]
                        : (float[])pose.muscles.Clone();
                    if (bendLimbs)
                    {
                        SetMuscle(pose.muscles, "Left Forearm Stretch", 0.45f);
                        SetMuscle(pose.muscles, "Left Lower Leg Stretch", 0.35f);
                    }

                    handler.SetHumanPose(ref pose);
                    handler.GetHumanPose(ref pose);
                }

                var take = ScriptableObject.CreateInstance<MotionTakeAsset>();
                take.Initialize(
                    "Generated Regression Take",
                    "test-session",
                    60f,
                    Animator.humanScale,
                    string.Empty);
                for (var frame = 0; frame < frameCount; frame++)
                {
                    take.AddOrReplaceFrame(new MotionTakeFrame(
                        frame,
                        frame / 60d,
                        new MotionHumanPoseSample(
                            pose.bodyPosition,
                            pose.bodyRotation,
                            pose.muscles)));
                }

                return take;
            }

            public MotionTakePreviewDriver Bind(MotionTakeAsset take, MotionEditRecipe recipe)
            {
                var driver = new MotionTakePreviewDriver();
                driver.Bind(Animator, take, recipe);
                return driver;
            }

            public PoseSnapshot CaptureLeftArm()
            {
                return new PoseSnapshot(
                    Bone(HumanBodyBones.LeftUpperArm),
                    Bone(HumanBodyBones.LeftLowerArm),
                    Bone(HumanBodyBones.LeftHand));
            }

            public PoseSnapshot CaptureLeftLeg()
            {
                return new PoseSnapshot(
                    Bone(HumanBodyBones.LeftUpperLeg),
                    Bone(HumanBodyBones.LeftLowerLeg),
                    Bone(HumanBodyBones.LeftFoot));
            }

            public void Dispose()
            {
                if (_avatar != null)
                {
                    UnityEngine.Object.DestroyImmediate(_avatar);
                    _avatar = null;
                }

                if (Root != null)
                {
                    UnityEngine.Object.DestroyImmediate(Root);
                }
            }

            private Transform AddBone(
                HumanBodyBones humanBone,
                Transform parent,
                Vector3 localPosition)
            {
                var bone = new GameObject(humanBone.ToString()).transform;
                bone.SetParent(parent, false);
                bone.localPosition = localPosition;
                bone.localRotation = Quaternion.identity;
                bone.localScale = Vector3.one;
                _bones.Add(humanBone, bone);
                _skeleton.Add(bone);
                return bone;
            }

            private static void SetMuscle(float[] muscles, string name, float value)
            {
                var index = Array.IndexOf(HumanTrait.MuscleName, name);
                Assert.That(index, Is.GreaterThanOrEqualTo(0), $"Missing Humanoid muscle {name}.");
                muscles[index] = value;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    public sealed class MotionTakeWorkflowAcceptanceTests
    {
        private const int FrameCount = 17;
        private const int CorrectionFrame = 8;
        private const float FrameRate = 60f;

        private static readonly TrackerRole[] ThreePointRoles =
        {
            TrackerRole.Head,
            TrackerRole.LeftHand,
            TrackerRole.RightHand
        };

        private static readonly TrackerRole[] SixPointRoles =
        {
            TrackerRole.Head,
            TrackerRole.LeftHand,
            TrackerRole.RightHand,
            TrackerRole.Waist,
            TrackerRole.LeftFoot,
            TrackerRole.RightFoot
        };

        private static readonly TrackerRole[] ElevenPointRoles =
        {
            TrackerRole.Head,
            TrackerRole.LeftHand,
            TrackerRole.RightHand,
            TrackerRole.Waist,
            TrackerRole.LeftFoot,
            TrackerRole.RightFoot,
            TrackerRole.Chest,
            TrackerRole.LeftKnee,
            TrackerRole.RightKnee,
            TrackerRole.LeftElbow,
            TrackerRole.RightElbow
        };

        [TestCase(3)]
        [TestCase(6)]
        [TestCase(11)]
        public void SupportedTrackerConfiguration_IsReadyAndProducesCompleteCaptureStages(int pointCount)
        {
            var roles = RolesForPointCount(pointCount);
            using (var fixture = new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            using (var binding = new HumanoidAvatarBinding(fixture.Root, fixture.Animator))
            {
                var trackerFrame = fixture.CreateTrackerFrame(roles, 0d);
                Assert.That(MotionCaptureCoordinator.HasUsableCoreTracking(trackerFrame), Is.True,
                    pointCount + "-point capture must be ready when Head and both Hands are usable.");
                Assert.That(trackerFrame.poses.Count, Is.EqualTo(pointCount));
                Assert.That(trackerFrame.poses.Select(sample => sample.role).Distinct().Count(),
                    Is.EqualTo(pointCount), "Every configured device needs one unique semantic role.");
                CollectionAssert.AreEquivalent(roles, trackerFrame.poses.Select(sample => sample.role));

                var sourcePose = fixture.CaptureHumanPose();
                var ikRig = new MotionCaptureRig(binding).CreateIkOnlyReplayRig();
                Assert.That(ikRig.Apply(trackerFrame, 0, null), Is.True,
                    pointCount + "-point input must resolve an IK pose.");
                var ikPose = fixture.CaptureHumanPose();

                fixture.ApplyHumanPose(sourcePose);
                var automaticRig = new MotionCaptureRig(binding);
                Assert.That(automaticRig.Apply(trackerFrame, 0, null), Is.True,
                    pointCount + "-point input must resolve the automatic correction stage.");
                var automaticPose = fixture.CaptureHumanPose();

                var captured = new HumanoidCaptureFrame
                {
                    time = trackerFrame.time,
                    sourceBodyPosition = sourcePose.bodyPosition,
                    sourceBodyRotation = sourcePose.bodyRotation,
                    sourceMuscles = CloneMuscles(sourcePose),
                    ikBodyPosition = ikPose.bodyPosition,
                    ikBodyRotation = ikPose.bodyRotation,
                    ikMuscles = CloneMuscles(ikPose),
                    bodyPosition = automaticPose.bodyPosition,
                    bodyRotation = automaticPose.bodyRotation,
                    muscles = CloneMuscles(automaticPose),
                    hasFeet = pointCount >= 6,
                    leftFootPosition = fixture.Bone(HumanBodyBones.LeftFoot).position,
                    rightFootPosition = fixture.Bone(HumanBodyBones.RightFoot).position,
                    trackers = trackerFrame
                };
                var take = new CaptureTake
                {
                    sessionId = "acceptance-" + pointCount,
                    displayName = pointCount + " Point Acceptance",
                    sampleRate = FrameRate,
                    humanScale = fixture.HumanScale,
                    frames = new List<HumanoidCaptureFrame> { captured }
                };

                var roundTrip = JsonUtility.FromJson<CaptureTake>(JsonUtility.ToJson(take));
                Assert.That(roundTrip, Is.Not.Null);
                Assert.That(roundTrip.frames, Has.Count.EqualTo(1));
                AssertCaptureFrameShape(roundTrip.frames[0], pointCount, pointCount >= 6);
            }
        }

        [Test]
        public void CorrectedPreviewAndBakedHumanoidClip_ProduceEquivalentPose()
        {
            using (var sourceAvatar =
                   new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            {
                var take = sourceAvatar.CreateTake(FrameCount, FrameRate);
                var recipe = CreateElbowCorrectionRecipe(take);
                AnimationClip correctedClip = null;
                try
                {
                    var expected = EvaluateCorrectedPreview(sourceAvatar, take, recipe);
                    correctedClip = MotionTakeClipBaker.BuildClip(
                        new ArrayClipSource(FrameRate, expected.Select(record => record.Sample).ToArray()),
                        "Corrected Preview Equality");

                    using (var playbackAvatar =
                           new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
                    {
                        for (var frame = 0; frame < expected.Count; frame++)
                        {
                            correctedClip.SampleAnimation(playbackAvatar.Root, frame / FrameRate);
                            var actual = PoseRecord.Capture(
                                playbackAvatar,
                                frame / FrameRate,
                                playbackAvatar.CaptureHumanPose());
                            AssertEquivalentPose(actual, expected[frame], frame);
                        }
                    }
                }
                finally
                {
                    if (correctedClip != null)
                    {
                        UnityEngine.Object.DestroyImmediate(correctedClip);
                    }

                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        [Test]
        public void CorrectedHumanoidClip_RetargetsAcrossTwoBodyProportionsWithoutBreaks()
        {
            using (var sourceAvatar =
                   new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            {
                var take = sourceAvatar.CreateTake(FrameCount, FrameRate);
                var recipe = CreateElbowCorrectionRecipe(take);
                AnimationClip automaticClip = null;
                AnimationClip correctedClip = null;
                try
                {
                    var corrected = EvaluateCorrectedPreview(sourceAvatar, take, recipe);
                    automaticClip = MotionTakeClipBaker.BuildClip(
                        new MotionTakeAssetClipSource(take),
                        "Automatic Retarget Baseline");
                    correctedClip = MotionTakeClipBaker.BuildClip(
                        new ArrayClipSource(FrameRate, corrected.Select(record => record.Sample).ToArray()),
                        "Corrected Retarget Acceptance");

                    var compactScale = ValidateRetarget(
                        HumanoidTestProportions.Compact,
                        automaticClip,
                        correctedClip);
                    var tallScale = ValidateRetarget(
                        HumanoidTestProportions.Tall,
                        automaticClip,
                        correctedClip);
                    Assert.That(Mathf.Abs(tallScale - compactScale), Is.GreaterThan(0.2f),
                        "The retarget acceptance must exercise materially different avatar heights.");
                }
                finally
                {
                    if (automaticClip != null)
                    {
                        UnityEngine.Object.DestroyImmediate(automaticClip);
                    }

                    if (correctedClip != null)
                    {
                        UnityEngine.Object.DestroyImmediate(correctedClip);
                    }

                    UnityEngine.Object.DestroyImmediate(recipe);
                    UnityEngine.Object.DestroyImmediate(take);
                }
            }
        }

        private static float ValidateRetarget(
            HumanoidTestProportions proportions,
            AnimationClip automaticClip,
            AnimationClip correctedClip)
        {
            using (var avatar = new GeneratedHumanoidAcceptanceFixture(proportions))
            {
                automaticClip.SampleAnimation(avatar.Root, CorrectionFrame / FrameRate);
                var automaticHand = avatar.Bone(HumanBodyBones.LeftHand).position;
                var automaticRightHand = avatar.Bone(HumanBodyBones.RightHand).position;
                var automaticLeftFoot = avatar.Bone(HumanBodyBones.LeftFoot).position;
                var automaticRightFoot = avatar.Bone(HumanBodyBones.RightFoot).position;

                correctedClip.SampleAnimation(avatar.Root, CorrectionFrame / FrameRate);
                var correctedHand = avatar.Bone(HumanBodyBones.LeftHand).position;
                var handPinTolerance = Mathf.Max(0.015f, avatar.HumanScale * 0.025f);
                Assert.That(Vector3.Distance(correctedHand, automaticHand),
                    Is.LessThanOrEqualTo(handPinTolerance),
                    "The elbow-hint correction must keep the retargeted Hand effectively pinned.");
                Assert.That(Vector3.Distance(
                        avatar.Bone(HumanBodyBones.RightHand).position,
                        automaticRightHand),
                    Is.LessThanOrEqualTo(0.005f * avatar.HumanScale));
                Assert.That(Vector3.Distance(
                        avatar.Bone(HumanBodyBones.LeftFoot).position,
                        automaticLeftFoot),
                    Is.LessThanOrEqualTo(0.005f * avatar.HumanScale));
                Assert.That(Vector3.Distance(
                        avatar.Bone(HumanBodyBones.RightFoot).position,
                        automaticRightFoot),
                    Is.LessThanOrEqualTo(0.005f * avatar.HumanScale));

                PoseRecord previous = null;
                float? leftArmLength = null;
                float? rightArmLength = null;
                float? leftLegLength = null;
                float? rightLegLength = null;
                for (var frame = 0; frame < FrameCount; frame++)
                {
                    correctedClip.SampleAnimation(avatar.Root, frame / FrameRate);
                    var current = PoseRecord.Capture(
                        avatar,
                        frame / FrameRate,
                        avatar.CaptureHumanPose());
                    AssertFinite(current, frame);

                    var currentLeftArm = LimbLength(
                        avatar,
                        HumanBodyBones.LeftUpperArm,
                        HumanBodyBones.LeftLowerArm,
                        HumanBodyBones.LeftHand);
                    var currentRightArm = LimbLength(
                        avatar,
                        HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightLowerArm,
                        HumanBodyBones.RightHand);
                    var currentLeftLeg = LimbLength(
                        avatar,
                        HumanBodyBones.LeftUpperLeg,
                        HumanBodyBones.LeftLowerLeg,
                        HumanBodyBones.LeftFoot);
                    var currentRightLeg = LimbLength(
                        avatar,
                        HumanBodyBones.RightUpperLeg,
                        HumanBodyBones.RightLowerLeg,
                        HumanBodyBones.RightFoot);
                    if (!leftArmLength.HasValue)
                    {
                        leftArmLength = currentLeftArm;
                        rightArmLength = currentRightArm;
                        leftLegLength = currentLeftLeg;
                        rightLegLength = currentRightLeg;
                    }

                    Assert.That(Mathf.Abs(currentLeftArm - leftArmLength.Value), Is.LessThan(0.002f));
                    Assert.That(Mathf.Abs(currentRightArm - rightArmLength.Value), Is.LessThan(0.002f));
                    Assert.That(Mathf.Abs(currentLeftLeg - leftLegLength.Value), Is.LessThan(0.002f));
                    Assert.That(Mathf.Abs(currentRightLeg - rightLegLength.Value), Is.LessThan(0.002f));

                    if (previous != null)
                    {
                        var rootStep = Vector3.Distance(
                            current.Sample.BodyPosition * avatar.HumanScale,
                            previous.Sample.BodyPosition * avatar.HumanScale);
                        Assert.That(rootStep, Is.LessThan(0.03f),
                            "Retargeted root discontinuity at frame " + frame + ".");
                        Assert.That(Vector3.Distance(current.LeftHand, previous.LeftHand),
                            Is.LessThan(0.12f * avatar.HumanScale));
                        Assert.That(Vector3.Distance(current.RightHand, previous.RightHand),
                            Is.LessThan(0.04f * avatar.HumanScale));
                        Assert.That(Vector3.Distance(current.LeftFoot, previous.LeftFoot),
                            Is.LessThan(0.04f * avatar.HumanScale));
                        Assert.That(Vector3.Distance(current.RightFoot, previous.RightFoot),
                            Is.LessThan(0.04f * avatar.HumanScale));
                    }

                    previous = current;
                }

                return avatar.HumanScale;
            }
        }

        private static IReadOnlyList<PoseRecord> EvaluateCorrectedPreview(
            GeneratedHumanoidAcceptanceFixture avatar,
            MotionTakeAsset take,
            MotionEditRecipe recipe)
        {
            Vector3 baseHand;
            using (var baselineDriver = new MotionTakePreviewDriver())
            {
                baselineDriver.Bind(avatar.Animator, take, null);
                Assert.That(baselineDriver.ApplyFrame(CorrectionFrame), Is.True);
                baseHand = avatar.Bone(HumanBodyBones.LeftHand).position;
            }

            var records = new List<PoseRecord>(FrameCount);
            using (var driver = new MotionTakePreviewDriver())
            {
                driver.Bind(avatar.Animator, take, recipe);
                for (var frame = 0; frame < FrameCount; frame++)
                {
                    Assert.That(driver.ApplyFrame(frame), Is.True);
                    records.Add(PoseRecord.Capture(
                        avatar,
                        frame / FrameRate,
                        avatar.CaptureHumanPose()));
                }
            }

            Assert.That(Vector3.Distance(records[CorrectionFrame].LeftHand, baseHand),
                Is.LessThanOrEqualTo(0.005f),
                "A reachable elbow-hint edit must keep the source preview Hand within 5 mm.");
            return records;
        }

        private static MotionEditRecipe CreateElbowCorrectionRecipe(MotionTakeAsset take)
        {
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            recipe.Initialize(take, "Acceptance Elbow Correction");
            var key = recipe.CorrectionTrack.GetOrCreateKey(CorrectionFrame, 6);
            key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                PoseTarget.LeftElbowHint,
                new Vector3(0f, 0.18f, 0.16f)));
            recipe.CorrectionTrack.AddOrReplaceKey(key);
            return recipe;
        }

        private static void AssertEquivalentPose(PoseRecord actual, PoseRecord expected, int frame)
        {
            Assert.That(Vector3.Distance(actual.Sample.BodyPosition, expected.Sample.BodyPosition),
                Is.LessThanOrEqualTo(0.001f), "Root position mismatch at frame " + frame + ".");
            Assert.That(Quaternion.Angle(actual.Sample.BodyRotation, expected.Sample.BodyRotation),
                Is.LessThanOrEqualTo(0.1f), "Root rotation mismatch at frame " + frame + ".");
            Assert.That(actual.Sample.Muscles, Has.Length.EqualTo(expected.Sample.Muscles.Length));
            for (var muscle = 0; muscle < actual.Sample.Muscles.Length; muscle++)
            {
                Assert.That(Mathf.Abs(actual.Sample.Muscles[muscle] - expected.Sample.Muscles[muscle]),
                    Is.LessThanOrEqualTo(0.003f),
                    "Muscle " + muscle + " mismatch at frame " + frame + ".");
            }

            Assert.That(Vector3.Distance(actual.Head, expected.Head), Is.LessThanOrEqualTo(0.003f));
            Assert.That(Vector3.Distance(actual.LeftHand, expected.LeftHand), Is.LessThanOrEqualTo(0.005f));
            Assert.That(Vector3.Distance(actual.RightHand, expected.RightHand), Is.LessThanOrEqualTo(0.005f));
            Assert.That(Vector3.Distance(actual.LeftFoot, expected.LeftFoot), Is.LessThanOrEqualTo(0.005f));
            Assert.That(Vector3.Distance(actual.RightFoot, expected.RightFoot), Is.LessThanOrEqualTo(0.005f));
        }

        private static void AssertCaptureFrameShape(
            HumanoidCaptureFrame frame,
            int pointCount,
            bool expectsFeet)
        {
            Assert.That(frame.trackers, Is.Not.Null);
            Assert.That(frame.trackers.poses, Has.Count.EqualTo(pointCount));
            Assert.That(frame.sourceMuscles, Has.Length.EqualTo(HumanTrait.MuscleCount));
            Assert.That(frame.ikMuscles, Has.Length.EqualTo(HumanTrait.MuscleCount));
            Assert.That(frame.muscles, Has.Length.EqualTo(HumanTrait.MuscleCount));
            Assert.That(frame.hasFeet, Is.EqualTo(expectsFeet));
            Assert.That(ReferenceEquals(frame.sourceMuscles, frame.ikMuscles), Is.False);
            Assert.That(ReferenceEquals(frame.ikMuscles, frame.muscles), Is.False);
            Assert.That(IsFinite(frame.sourceBodyPosition), Is.True);
            Assert.That(IsFinite(frame.ikBodyPosition), Is.True);
            Assert.That(IsFinite(frame.bodyPosition), Is.True);
            Assert.That(IsFinite(frame.sourceBodyRotation), Is.True);
            Assert.That(IsFinite(frame.ikBodyRotation), Is.True);
            Assert.That(IsFinite(frame.bodyRotation), Is.True);
        }

        private static void AssertFinite(PoseRecord pose, int frame)
        {
            Assert.That(IsFinite(pose.Sample.BodyPosition), Is.True,
                "Non-finite root position at frame " + frame + ".");
            Assert.That(IsFinite(pose.Sample.BodyRotation), Is.True,
                "Non-finite root rotation at frame " + frame + ".");
            for (var muscle = 0; muscle < pose.Sample.Muscles.Length; muscle++)
            {
                Assert.That(IsFinite(pose.Sample.Muscles[muscle]), Is.True,
                    "Non-finite muscle " + muscle + " at frame " + frame + ".");
            }

            Assert.That(IsFinite(pose.Head), Is.True);
            Assert.That(IsFinite(pose.LeftHand), Is.True);
            Assert.That(IsFinite(pose.RightHand), Is.True);
            Assert.That(IsFinite(pose.LeftFoot), Is.True);
            Assert.That(IsFinite(pose.RightFoot), Is.True);
        }

        private static TrackerRole[] RolesForPointCount(int pointCount)
        {
            switch (pointCount)
            {
                case 3:
                    return ThreePointRoles;
                case 6:
                    return SixPointRoles;
                case 11:
                    return ElevenPointRoles;
                default:
                    throw new ArgumentOutOfRangeException(nameof(pointCount), pointCount, null);
            }
        }

        private static float[] CloneMuscles(HumanPose pose)
        {
            return pose.muscles == null
                ? new float[HumanTrait.MuscleCount]
                : (float[])pose.muscles.Clone();
        }

        private static float LimbLength(
            GeneratedHumanoidAcceptanceFixture avatar,
            HumanBodyBones upper,
            HumanBodyBones lower,
            HumanBodyBones tip)
        {
            return Vector3.Distance(avatar.Bone(upper).position, avatar.Bone(lower).position) +
                   Vector3.Distance(avatar.Bone(lower).position, avatar.Bone(tip).position);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) &&
                   IsFinite(value.z) && IsFinite(value.w);
        }

        private sealed class ArrayClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<MotionTakeClipSample> _samples;

            public ArrayClipSource(float frameRate, IReadOnlyList<MotionTakeClipSample> samples)
            {
                FrameRate = frameRate;
                _samples = samples;
            }

            public int SampleCount => _samples.Count;
            public float FrameRate { get; }

            public bool TryGetSample(int index, out MotionTakeClipSample sample)
            {
                if (index < 0 || index >= _samples.Count)
                {
                    sample = default(MotionTakeClipSample);
                    return false;
                }

                sample = _samples[index];
                return true;
            }
        }

        private sealed class PoseRecord
        {
            private PoseRecord()
            {
            }

            public MotionTakeClipSample Sample { get; private set; }
            public Vector3 Head { get; private set; }
            public Vector3 LeftHand { get; private set; }
            public Vector3 RightHand { get; private set; }
            public Vector3 LeftFoot { get; private set; }
            public Vector3 RightFoot { get; private set; }

            public static PoseRecord Capture(
                GeneratedHumanoidAcceptanceFixture avatar,
                float time,
                HumanPose pose)
            {
                return new PoseRecord
                {
                    Sample = new MotionTakeClipSample
                    {
                        TimeSeconds = time,
                        BodyPosition = pose.bodyPosition,
                        BodyRotation = pose.bodyRotation,
                        Muscles = CloneMuscles(pose)
                    },
                    Head = avatar.Bone(HumanBodyBones.Head).position,
                    LeftHand = avatar.Bone(HumanBodyBones.LeftHand).position,
                    RightHand = avatar.Bone(HumanBodyBones.RightHand).position,
                    LeftFoot = avatar.Bone(HumanBodyBones.LeftFoot).position,
                    RightFoot = avatar.Bone(HumanBodyBones.RightFoot).position
                };
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class ValidationClipOverlayRegressionTests
    {
        [TestCase(2f, 0.2f, true,
            TestName = "Validation_RootDiscontinuityUsesMeters_LargeAvatar")]
        [TestCase(0.5f, 0.5f, false,
            TestName = "Validation_RootDiscontinuityUsesMeters_SmallAvatar")]
        public void AssetValidation_RootDiscontinuityThresholdIsInMeters(
            float humanScale,
            float normalizedRootDelta,
            bool expectedIssue)
        {
            var take = ScriptableObject.CreateInstance<MotionTakeAsset>();
            try
            {
                take.Initialize("Scale", "session", 30f, humanScale, string.Empty);
                take.AddOrReplaceFrame(Frame(0, Vector3.zero));
                take.AddOrReplaceFrame(Frame(1, Vector3.right * normalizedRootDelta));

                var issues = MotionTakeValidationEngine.Validate(
                    new MotionTakeAssetValidationSource(take),
                    new MotionTakeValidationSettings
                    {
                        RootDiscontinuityDistance = 0.35f
                    });

                Assert.That(
                    issues.Any(issue => issue.Kind == MotionTakeValidationKind.RootDiscontinuity),
                    Is.EqualTo(expectedIssue),
                    "HumanPose.bodyPosition is normalized by humanScale; validation thresholds are meters.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(take);
            }
        }

        [Test]
        public void Validate_CorrectedSourceChecksEveryFrameAndCarriesPerFrameIkWarnings()
        {
            var warningField = typeof(MotionTakeValidationSample).GetField(
                "IkWarnings",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(warningField, Is.Not.Null,
                "Corrected validation samples must carry the IK warnings for their own frame; " +
                "checking only the preview driver's current-frame warning loses off-screen failures.");

            var warningFrame = ValidationSample(
                4,
                new Vector3(0.5f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f),
                new Vector3(0.5f, 0f, 0f));
            object boxed = warningFrame;
            warningField.SetValue(boxed, new[] { "Right hand target is unreachable." });
            warningFrame = (MotionTakeValidationSample)boxed;

            var source = new ArrayValidationSource(30f,
                ValidationSample(0, Vector3.zero, Vector3.zero, Vector3.zero),
                ValidationSample(1,
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0.5f, -0.1f, 0f),
                    new Vector3(0.5f, 0f, 0f)),
                ValidationSample(2,
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f),
                    float.NaN),
                ValidationSample(3,
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f),
                    new Vector3(0.5f, 0f, 0f)),
                warningFrame);

            var issues = MotionTakeValidationEngine.Validate(source, new MotionTakeValidationSettings
            {
                RootDiscontinuityDistance = 0.25f,
                FloorPenetrationTolerance = 0.01f,
                FootContactHeight = 0.15f,
                FootSlidingSpeed = 0.1f
            });
            var kinds = issues.Select(issue => issue.Kind).ToArray();

            Assert.That(kinds, Does.Contain(MotionTakeValidationKind.NonFinitePose));
            Assert.That(kinds, Does.Contain(MotionTakeValidationKind.RootDiscontinuity));
            Assert.That(kinds, Does.Contain(MotionTakeValidationKind.FloorPenetration));
            Assert.That(kinds, Does.Contain(MotionTakeValidationKind.FootSliding));
            Assert.That(issues.Any(issue =>
                    issue.Kind == MotionTakeValidationKind.IkUnreachable && issue.Frame == 4),
                Is.True,
                "An IK warning outside the current scrub frame must survive full-take validation.");
        }

        [Test]
        public void OverlayPipeline_ExposesStageSpecificSolvedPoseSource()
        {
            var editorAssembly = typeof(MotionCaptureCoordinator).Assembly;
            var sourceType = editorAssembly.GetType(
                "BuildSoft.MotionTakeStudio.Editor.IMotionTakeOverlayPoseSource");
            Assert.That(sourceType, Is.Not.Null,
                "IK, Automatic, and Manual overlays need a stage-aware source of actual solved poses; " +
                "the base target pose cannot represent all three stages.");

            var method = sourceType.GetMethod("TryGetSolvedTargetPose");
            Assert.That(method, Is.Not.Null,
                "The overlay source must resolve a target at a requested frame and stage.");
            var parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(4),
                "A shared overlay source must accept stage, target, frame, and an out pose.");
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(MotionTakeOverlayFlags)));
            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(PoseTarget)));
            Assert.That(parameters[2].ParameterType, Is.EqualTo(typeof(int)));
            Assert.That(parameters[3].IsOut, Is.True);
            Assert.That(
                parameters[3].ParameterType,
                Is.EqualTo(typeof(MotionTakeTargetPose).MakeByRefType()));

            var sessionProperty = typeof(IMotionTakeStudioSession).GetProperty("OverlayPoseSource");
            Assert.That(sessionProperty, Is.Not.Null,
                "Scene handles need the stage-aware solved-pose source through the session contract.");
            Assert.That(sessionProperty.PropertyType, Is.EqualTo(sourceType));
            Assert.That(sourceType.IsAssignableFrom(typeof(MotionCaptureCoordinator)), Is.True,
                "The coordinator must expose the preview pipeline's actual solved stage poses.");
        }

        [Test]
        public void OverlayPoseCache_KeepsIkAutomaticAndManualResultsDistinct()
        {
            var editorAssembly = typeof(MotionCaptureCoordinator).Assembly;
            var cacheType = editorAssembly.GetType(
                "BuildSoft.MotionTakeStudio.Editor.MotionTakeOverlayPoseCache");
            Assert.That(cacheType, Is.Not.Null,
                "Stage overlays need a cache that cannot collapse IK, Automatic, and Manual into one base pose.");

            var cache = Activator.CreateInstance(cacheType);
            var reset = cacheType.GetMethod("Reset");
            var set = cacheType.GetMethod("Set");
            var tryGet = cacheType.GetMethod("TryGet");
            Assert.That(reset, Is.Not.Null);
            Assert.That(set, Is.Not.Null);
            Assert.That(tryGet, Is.Not.Null);

            reset.Invoke(cache, new object[] { 12 });
            var stages = new[]
            {
                MotionTakeOverlayFlags.Ik,
                MotionTakeOverlayFlags.Automatic,
                MotionTakeOverlayFlags.Manual
            };
            for (var index = 0; index < stages.Length; index++)
            {
                var pose = new MotionTakeTargetPose
                {
                    WorldPosition = new Vector3(index + 1f, 0f, 0f),
                    WorldRotation = Quaternion.Euler(0f, index * 10f, 0f)
                };
                set.Invoke(cache, new object[] { stages[index], PoseTarget.LeftHand, pose });
            }

            for (var index = 0; index < stages.Length; index++)
            {
                var arguments = new object[]
                {
                    stages[index], PoseTarget.LeftHand, 12, default(MotionTakeTargetPose)
                };
                Assert.That((bool)tryGet.Invoke(cache, arguments), Is.True);
                var resolved = (MotionTakeTargetPose)arguments[3];
                Assert.That(resolved.WorldPosition.x, Is.EqualTo(index + 1f).Within(0.0001f));
            }
        }

        [Test]
        public void CaptureRig_ExposesAnIkOnlyReplayStage()
        {
            var rigType = typeof(MotionCaptureCoordinator).Assembly.GetType(
                "BuildSoft.MotionTakeStudio.Editor.MotionCaptureRig");
            Assert.That(rigType, Is.Not.Null);
            Assert.That(rigType.GetMethod("CreateIkOnlyReplayRig"), Is.Not.Null,
                "The IK overlay must replay tracker IK before filtering, root cleanup, and foot locking.");
        }

        [Test]
        public void CaptureFrame_PersistsIkStageSeparatelyFromAutomaticStage()
        {
            Assert.That(typeof(HumanoidCaptureFrame).GetField("ikBodyPosition"), Is.Not.Null);
            Assert.That(typeof(HumanoidCaptureFrame).GetField("ikBodyRotation"), Is.Not.Null);
            Assert.That(typeof(HumanoidCaptureFrame).GetField("ikMuscles"), Is.Not.Null,
                "The actual tracker-IK pose must survive gap repair instead of aliasing the automatic pose.");
        }

        [Test]
        public void Coordinator_BuildsAFullCorrectedValidationSource()
        {
            var method = typeof(MotionCaptureCoordinator).GetMethod(
                "BuildCorrectedValidationSource",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null,
                "Validation must replay every manually corrected frame, not append only the current frame warning.");
            Assert.That(method.ReturnType, Is.EqualTo(typeof(IMotionTakeValidationSource)));
        }

        [Test]
        public void BuildClip_UsesMotionPreservingTangents()
        {
            var clip = MotionTakeClipBaker.BuildClip(LinearClipSource(), "Linear");
            try
            {
                var curve = RootXCurve(clip);
                Assert.That(curve.keys[0].outTangent, Is.EqualTo(1f).Within(0.01f));
                Assert.That(curve.keys[1].inTangent, Is.EqualTo(1f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void BuildClip_PlaybackBetweenSamplesMatchesLinearMotion()
        {
            var clip = MotionTakeClipBaker.BuildClip(LinearClipSource(), "Linear");
            try
            {
                var curve = RootXCurve(clip);
                Assert.That(curve.Evaluate(0.25f), Is.EqualTo(0.25f).Within(0.01f));
                Assert.That(curve.Evaluate(0.5f), Is.EqualTo(0.5f).Within(0.01f));
                Assert.That(curve.Evaluate(0.75f), Is.EqualTo(0.75f).Within(0.01f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        private static MotionTakeFrame Frame(int index, Vector3 normalizedBodyPosition)
        {
            return new MotionTakeFrame(
                index,
                index / 30d,
                new MotionHumanPoseSample(
                    normalizedBodyPosition,
                    Quaternion.identity,
                    new float[HumanTrait.MuscleCount]));
        }

        private static MotionTakeValidationSample ValidationSample(
            int frame,
            Vector3 root,
            Vector3 leftFoot,
            Vector3 rightFoot,
            float muscle = 0f)
        {
            return new MotionTakeValidationSample
            {
                Frame = frame,
                RootPosition = root,
                RootRotation = Quaternion.identity,
                LeftFootPosition = leftFoot,
                RightFootPosition = rightFoot,
                FloorHeight = 0f,
                Muscles = new[] { muscle },
                HasRoot = true,
                HasFeet = true,
                TrackingAvailable = true
            };
        }

        private static IMotionTakeClipSource LinearClipSource()
        {
            return new ArrayClipSource(1f,
                ClipSample(0f, 0f),
                ClipSample(1f, 1f));
        }

        private static MotionTakeClipSample ClipSample(float time, float x)
        {
            var muscles = new float[HumanTrait.MuscleCount];
            for (var index = 0; index < muscles.Length; index++)
            {
                muscles[index] = x;
            }

            return new MotionTakeClipSample
            {
                TimeSeconds = time,
                BodyPosition = new Vector3(x, 0f, 0f),
                BodyRotation = Quaternion.identity,
                Muscles = muscles
            };
        }

        private static AnimationCurve RootXCurve(AnimationClip clip)
        {
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), "RootT.x");
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            Assert.That(curve, Is.Not.Null);
            return curve;
        }

        private sealed class ArrayValidationSource : IMotionTakeValidationSource
        {
            private readonly IReadOnlyList<MotionTakeValidationSample> _samples;

            public int FrameCount => _samples.Count;
            public float FrameRate { get; }

            public ArrayValidationSource(float frameRate, params MotionTakeValidationSample[] samples)
            {
                FrameRate = frameRate;
                _samples = samples;
            }

            public bool TryGetValidationSample(int index, out MotionTakeValidationSample sample)
            {
                if (index < 0 || index >= _samples.Count)
                {
                    sample = default(MotionTakeValidationSample);
                    return false;
                }

                sample = _samples[index];
                return true;
            }
        }

        private sealed class ArrayClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<MotionTakeClipSample> _samples;

            public int SampleCount => _samples.Count;
            public float FrameRate { get; }

            public ArrayClipSource(float frameRate, params MotionTakeClipSample[] samples)
            {
                FrameRate = frameRate;
                _samples = samples;
            }

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
    }
}

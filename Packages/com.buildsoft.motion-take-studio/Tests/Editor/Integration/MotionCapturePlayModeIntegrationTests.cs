using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.TestTools;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    public sealed class MotionCapturePlayModeIntegrationTests
    {
        private const float FrameRate = 60f;

        private static readonly TrackerRole[] SixPointRoles =
        {
            TrackerRole.Head,
            TrackerRole.LeftHand,
            TrackerRole.RightHand,
            TrackerRole.Waist,
            TrackerRole.LeftFoot,
            TrackerRole.RightFoot
        };

        private GeneratedHumanoidAcceptanceFixture _sourceAvatar;
        private GeneratedHumanoidAcceptanceFixture _playbackAvatar;
        private AnimationClip _correctedClip;
        private PlayableGraph _playbackGraph;
        private double _captureTime;

        [UnityTest]
        public IEnumerator CaptureReviewElbowCorrectionValidationAndBake_RoundTripsAcrossPlayerFrames()
        {
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying, Is.True);

            var coordinator = MotionCaptureCoordinator.Instance;
            var reset = RequireInstanceSeam("ResetForTests", Type.EmptyTypes);
            var arm = RequireInstanceSeam(
                "ArmCaptureAvatarForTests",
                new[] { typeof(GameObject), typeof(bool) });
            var buildCorrected = RequireInstanceSeam(
                "BuildCorrectedFramesForExport",
                Type.EmptyTypes);

            reset.Invoke(coordinator, null);
            _captureTime = 100d;
            coordinator.SetRealtimeProviderForTests(ReadCaptureTime);
            _sourceAvatar = new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard);
            var reachablePose = _sourceAvatar.CaptureHumanPose();
            SetMuscle(reachablePose.muscles, "Left Forearm Stretch", 0.35f);
            SetMuscle(reachablePose.muscles, "Left Arm Down-Up", 0.1f);
            _sourceAvatar.ApplyHumanPose(reachablePose);
            coordinator.SetTrackerProvider(new SixPointTrackerProvider(_sourceAvatar));
            arm.Invoke(coordinator, new object[] { _sourceAvatar.Root, false });

            for (var frame = 0;
                 frame < 8 && coordinator.Phase != MotionTakeSessionPhase.Ready;
                 frame++)
            {
                yield return null;
            }

            Assert.That(coordinator.Phase, Is.EqualTo(MotionTakeSessionPhase.Ready),
                "A generated Humanoid must stabilize on real player frames without NDMF.");
            coordinator.BeginRecording();
            System.Threading.Thread.Sleep(200);
            yield return null;
            for (var frame = 0; frame < 12 && coordinator.FrameCount < 5; frame++)
            {
                _captureTime += 1d / FrameRate;
                yield return null;
            }

            Assert.That(coordinator.FrameCount, Is.GreaterThanOrEqualTo(3),
                "Recording must be sampled by player LateUpdate, not an edit-mode direct call.");
            coordinator.StopAndReview();
            Assert.That(coordinator.Phase, Is.EqualTo(MotionTakeSessionPhase.Reviewing));

            var correctionFrame = coordinator.FrameCount / 2;
            coordinator.ScrubToFrame(correctionFrame);
            var baseUpperArm = _sourceAvatar.Bone(HumanBodyBones.LeftUpperArm).position;
            Assert.That(coordinator.TargetPoseSource.TryGetBaseTargetPose(
                    PoseTarget.LeftHand,
                    correctionFrame,
                    out var baseHand),
                Is.True);
            Assert.That(coordinator.TargetPoseSource.TryGetBaseTargetPose(
                    PoseTarget.LeftElbowHint,
                    correctionFrame,
                    out var baseElbow),
                Is.True);
            MotionTakeCorrectionAuthoring.SetPosition(
                coordinator.ActiveRecipe,
                PoseTarget.LeftElbowHint,
                correctionFrame,
                12,
                baseElbow,
                baseElbow.WorldPosition + Vector3.up * 0.10f);
            coordinator.Revalidate();
            coordinator.ScrubToFrame(correctionFrame);

            var blockingIssues = coordinator.ValidationIssues.Where(issue =>
                    issue.Severity == MotionTakeValidationSeverity.Error ||
                    issue.Kind == MotionTakeValidationKind.NonFinitePose ||
                    issue.Kind == MotionTakeValidationKind.TrackingGap)
                .ToArray();
            Assert.That(
                blockingIssues,
                Is.Empty,
                "The corrected full take must remain finite and fully tracked. " +
                string.Join(" | ", blockingIssues.Select(issue =>
                    $"{issue.Kind}/{issue.Severity} frame {issue.Frame}-{issue.EndFrame}: {issue.Message}")));
            Assert.That(coordinator.OverlayPoseSource.TryGetSolvedTargetPose(
                    MotionTakeOverlayFlags.Manual,
                    PoseTarget.LeftHand,
                    correctionFrame,
                    out var correctedHand),
                Is.True);
            Assert.That(Vector3.Distance(correctedHand.WorldPosition, baseHand.WorldPosition),
                Is.LessThanOrEqualTo(0.005f),
                "Raising the elbow Hint must keep the authored Hand target pinned within 5 mm.");
            Assert.That(coordinator.OverlayPoseSource.TryGetSolvedTargetPose(
                    MotionTakeOverlayFlags.Manual,
                    PoseTarget.LeftElbowHint,
                    correctionFrame,
                    out var correctedElbow),
                Is.True);
            var armAxis = baseHand.WorldPosition - baseUpperArm;
            var baseBend = Vector3.ProjectOnPlane(
                baseElbow.WorldPosition - baseUpperArm,
                armAxis);
            var requestedBend = Vector3.ProjectOnPlane(
                baseElbow.WorldPosition + Vector3.up * 0.10f - baseUpperArm,
                armAxis);
            var solvedBend = Vector3.ProjectOnPlane(
                correctedElbow.WorldPosition - baseUpperArm,
                armAxis);
            Assert.That(baseBend.sqrMagnitude, Is.GreaterThan(1e-8f));
            Assert.That(requestedBend.sqrMagnitude, Is.GreaterThan(1e-8f));
            Assert.That(solvedBend.sqrMagnitude, Is.GreaterThan(1e-8f));
            Assert.That(Vector3.Angle(baseBend, requestedBend),
                Is.GreaterThan(5f),
                "The fixture must request a meaningfully different elbow bend direction.");
            Assert.That(Vector3.Angle(baseBend, solvedBend),
                Is.GreaterThan(2f),
                "The solved elbow must actually move away from the base bend direction.");
            Assert.That(correctedElbow.WorldPosition.y - baseElbow.WorldPosition.y,
                Is.GreaterThan(0.002f),
                "Raising the Hint by 10 cm must visibly raise the solved elbow.");
            Assert.That(Vector3.Angle(solvedBend, requestedBend),
                Is.LessThan(Vector3.Angle(baseBend, requestedBend)),
                "The solved elbow must move closer to the requested bend direction.");
            Assert.That(Vector3.Dot(requestedBend.normalized, solvedBend.normalized),
                Is.GreaterThan(0.5f),
                "The solved elbow must bend toward the raised Hint, not remain a no-op.");

            var correctedFrames = buildCorrected.Invoke(coordinator, null)
                as IReadOnlyList<HumanoidCaptureFrame>;
            Assert.That(correctedFrames, Is.Not.Null,
                "BuildCorrectedFramesForExport must expose the exact validated review samples.");
            Assert.That(correctedFrames.Count, Is.EqualTo(coordinator.FrameCount));
            _correctedClip = MotionTakeClipBaker.BuildClip(
                new CorrectedFrameClipSource(FrameRate, correctedFrames),
                "Play Mode Corrected Integration");

            _playbackAvatar = new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard);
            _playbackGraph = PlayableGraph.Create("MotionTakeCorrectedPlaybackAcceptance");
            var output = AnimationPlayableOutput.Create(
                _playbackGraph,
                "Corrected Humanoid",
                _playbackAvatar.Animator);
            var clipPlayable = AnimationClipPlayable.Create(_playbackGraph, _correctedClip);
            clipPlayable.SetApplyFootIK(false);
            clipPlayable.SetSpeed(0d);
            output.SetSourcePlayable(clipPlayable);
            _playbackGraph.Play();
            clipPlayable.SetTime(correctedFrames[correctionFrame].time);
            _playbackGraph.Evaluate(0f);
            yield return null;

            var expectedFrame = correctedFrames[correctionFrame];
            _sourceAvatar.ApplyHumanPose(ToHumanPose(expectedFrame));
            var actualPose = _playbackAvatar.CaptureHumanPose();
            Assert.That(Vector3.Distance(actualPose.bodyPosition, expectedFrame.bodyPosition),
                Is.LessThanOrEqualTo(0.01f),
                "Playable Humanoid root reconstruction must stay within 10 mm.");
            Assert.That(Quaternion.Angle(actualPose.bodyRotation, expectedFrame.bodyRotation),
                Is.LessThanOrEqualTo(0.2f));
            Assert.That(Vector3.Distance(
                    _playbackAvatar.Bone(HumanBodyBones.LeftHand).position,
                    _sourceAvatar.Bone(HumanBodyBones.LeftHand).position),
                Is.LessThanOrEqualTo(0.01f),
                "The baked clip must reproduce the corrected preview Hand within 10 mm, " +
                "including Humanoid root reconstruction. The IK target pin is asserted separately at 5 mm.");
        }

        [UnityTest]
        public IEnumerator ArmedOptionalProcessor_WaitsForCompletionBeforeReady()
        {
            yield return new EnterPlayMode();
            Assert.That(Application.isPlaying, Is.True);

            var coordinator = MotionCaptureCoordinator.Instance;
            var reset = RequireInstanceSeam("ResetForTests", Type.EmptyTypes);
            var arm = RequireInstanceSeam(
                "ArmCaptureAvatarForTests",
                new[] { typeof(GameObject), typeof(bool) });
            var resetPlayReferences = RequireInstanceSeam(
                "ResetCaptureReferencesForTests",
                Type.EmptyTypes);

            reset.Invoke(coordinator, null);
            _sourceAvatar = new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard);
            coordinator.SetTrackerProvider(new SixPointTrackerProvider(_sourceAvatar));
            arm.Invoke(coordinator, new object[] { _sourceAvatar.Root, true });
            resetPlayReferences.Invoke(coordinator, null);
            ProcessedAvatarHooks.NotifyProcessingRootDiscovered(
                _sourceAvatar.Root,
                "PlayMode optional processor early root");

            for (var frame = 0; frame < 4; frame++)
            {
                yield return null;
            }

            Assert.That(coordinator.Phase, Is.EqualTo(MotionTakeSessionPhase.Preparing),
                "An armed optional processor must not expose the unprocessed clone as Ready.");
            ProcessedAvatarHooks.NotifyDirectProcessedRoot(
                _sourceAvatar.Root,
                "PlayMode optional processor completion");

            for (var frame = 0;
                 frame < 8 && coordinator.Phase != MotionTakeSessionPhase.Ready;
                 frame++)
            {
                yield return null;
            }

            Assert.That(coordinator.Phase, Is.EqualTo(MotionTakeSessionPhase.Ready),
                "The exact processed root must become Ready after completion and two stable frames.");
        }

        [UnityTearDown]
        public IEnumerator ExitPlayModeEvenWhenTheIntegrationAssertionFails()
        {
            Exception cleanupError = null;
            try
            {
                if (_playbackGraph.IsValid())
                {
                    _playbackGraph.Destroy();
                }

                DestroyPlaySafe(_correctedClip);
                _correctedClip = null;

                var reset = FindInstanceSeam("ResetForTests", Type.EmptyTypes);
                reset?.Invoke(MotionCaptureCoordinator.Instance, null);
                _playbackAvatar?.Dispose();
                _playbackAvatar = null;
                _sourceAvatar?.Dispose();
                _sourceAvatar = null;
            }
            catch (Exception exception)
            {
                cleanupError = exception is TargetInvocationException invocation &&
                               invocation.InnerException != null
                    ? invocation.InnerException
                    : exception;
            }

            yield return null;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                yield return new ExitPlayMode();
            }

            if (cleanupError != null)
            {
                throw cleanupError;
            }
        }

        private static MethodInfo RequireInstanceSeam(string name, Type[] parameters)
        {
            var method = FindInstanceSeam(name, parameters);
            Assert.That(method, Is.Not.Null,
                "MotionCaptureCoordinator requires the test seam " + name + ".");
            Assert.That(method.IsAssembly, Is.True,
                "MotionCaptureCoordinator test seam " + name + " must be internal.");
            return method;
        }

        private static MethodInfo FindInstanceSeam(string name, Type[] parameters)
        {
            return typeof(MotionCaptureCoordinator).GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameters,
                null);
        }

        private static HumanPose ToHumanPose(HumanoidCaptureFrame frame)
        {
            return new HumanPose
            {
                bodyPosition = frame.bodyPosition,
                bodyRotation = frame.bodyRotation,
                muscles = frame.muscles == null
                    ? new float[HumanTrait.MuscleCount]
                    : (float[])frame.muscles.Clone()
            };
        }

        private double ReadCaptureTime()
        {
            return _captureTime;
        }

        private static void SetMuscle(float[] muscles, string name, float value)
        {
            var index = Array.IndexOf(HumanTrait.MuscleName, name);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "Missing Humanoid muscle " + name + ".");
            muscles[index] = value;
        }

        private static void DestroyPlaySafe(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private sealed class SixPointTrackerProvider : ITrackerPoseProvider
        {
            private readonly List<TrackerPoseSample> _samples = new List<TrackerPoseSample>();

            public SixPointTrackerProvider(GeneratedHumanoidAcceptanceFixture avatar)
            {
                var initial = avatar.CreateTrackerFrame(SixPointRoles, 0d);
                foreach (var sample in initial.poses)
                {
                    _samples.Add(sample.Clone());
                }
            }

            public string DisplayName => "Generated 6-point provider";
            public bool IsAvailable => true;
            public string Diagnostic => string.Empty;
            public IReadOnlyList<TrackedDeviceInfo> Devices => Array.Empty<TrackedDeviceInfo>();

            public bool TryGetFrame(double time, TrackerFrame destination, out string warning)
            {
                destination.time = time;
                destination.poses.Clear();
                foreach (var sample in _samples)
                {
                    destination.poses.Add(sample.Clone());
                }

                warning = string.Empty;
                return true;
            }

            public void AssignRole(string deviceId, TrackerRole role)
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class CorrectedFrameClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<HumanoidCaptureFrame> _frames;

            public CorrectedFrameClipSource(
                float frameRate,
                IReadOnlyList<HumanoidCaptureFrame> frames)
            {
                FrameRate = frameRate;
                _frames = frames;
            }

            public int SampleCount => _frames.Count;
            public float FrameRate { get; }

            public bool TryGetSample(int index, out MotionTakeClipSample sample)
            {
                sample = default(MotionTakeClipSample);
                if (index < 0 || index >= _frames.Count)
                {
                    return false;
                }

                var frame = _frames[index];
                sample = new MotionTakeClipSample
                {
                    TimeSeconds = (float)frame.time,
                    BodyPosition = frame.bodyPosition,
                    BodyRotation = frame.bodyRotation,
                    Muscles = frame.muscles
                };
                return true;
            }
        }
    }
}

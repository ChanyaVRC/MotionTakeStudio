using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    /// <summary>
    /// Red contracts for capture lifecycle bugs found during the v0.1.0 review.
    /// These tests deliberately discover the desired internal seams by reflection so
    /// each regression remains an executable failure while production is still unchanged.
    /// </summary>
    public sealed class CaptureLifecycleRegressionTests
    {
        [Test]
        public void RecordingReadiness_RequiresUsableHeadAndBothHands_NotThreeArbitraryDevices()
        {
            var invalidCoreFrame = new TrackerFrame
            {
                poses = new List<TrackerPoseSample>
                {
                    Pose(TrackerRole.Head, "head", false),
                    Pose(TrackerRole.LeftHand, "left", true),
                    Pose(TrackerRole.RightHand, "right", true)
                }
            };
            var bodyOnlyFrame = new TrackerFrame
            {
                poses = new List<TrackerPoseSample>
                {
                    Pose(TrackerRole.Waist, "waist", true),
                    Pose(TrackerRole.LeftFoot, "left-foot", true),
                    Pose(TrackerRole.RightFoot, "right-foot", true)
                }
            };
            var usableCoreFrame = new TrackerFrame
            {
                poses = new List<TrackerPoseSample>
                {
                    Pose(TrackerRole.Head, "head", true),
                    Pose(TrackerRole.LeftHand, "left", true),
                    Pose(TrackerRole.RightHand, "right", true)
                }
            };

            var readiness = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "HasUsableCoreTracking",
                typeof(TrackerFrame));

            Assert.That(InvokeBool(readiness, invalidCoreFrame), Is.False,
                "A connected but invalid HMD pose must block recording.");
            Assert.That(InvokeBool(readiness, bodyOnlyFrame), Is.False,
                "Three generic trackers are not a Head + LeftHand + RightHand capture set.");
            Assert.That(InvokeBool(readiness, usableCoreFrame), Is.True);
        }

        [Test]
        public void CaptureRig_WhenApplyReturnsFalse_DoesNotPermitSourcePoseToBeStoredAsResolvedMotion()
        {
            var admission = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "ShouldStoreResolvedSample",
                typeof(bool));

            Assert.That(InvokeBool(admission, false), Is.False,
                "MotionCaptureRig.Apply(false) means calibration/IK did not produce a tracked pose; " +
                "the current base animation must not be silently appended as captured motion.");
            Assert.That(InvokeBool(admission, true), Is.True);
        }

        [Test]
        public void CatchUpPolicy_DropsHistoricalSlotsAndReportsHitch_InsteadOfDuplicatingCurrentPose()
        {
            var planMethod = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "PlanCaptureSamples",
                typeof(double),
                typeof(double),
                typeof(double));

            var plan = planMethod.Invoke(null, new object[] { 0d, 1d, 1d / 60d });
            Assert.That(plan, Is.Not.Null);

            var planType = plan.GetType();
            var timestamps = ReadSequence<double>(planType, plan, "SampleTimes").ToArray();
            var droppedCount = ReadValue<int>(planType, plan, "DroppedSampleCount");
            var warning = ReadValue<string>(planType, plan, "Warning");

            Assert.That(timestamps, Has.Length.EqualTo(1),
                "One current Player frame supplies only one current HumanPose/OpenVR observation.");
            Assert.That(timestamps[0], Is.EqualTo(1d).Within(1e-9),
                "The current observation must not be back-dated into historical 60 Hz slots.");
            Assert.That(droppedCount, Is.EqualTo(60));
            Assert.That(warning, Does.Contain("hitch").IgnoreCase);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        public void ProcessedAvatarReadiness_RequiresNdmfAndApplyOnPlay(
            bool ndmfAvailable,
            bool applyOnPlayEnabled)
        {
            var gate = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "CanReportProcessedAvatarReady",
                typeof(bool),
                typeof(bool));

            Assert.That(InvokeBool(gate, ndmfAvailable, applyOnPlayEnabled), Is.False,
                "A stable unprocessed clone must never be surfaced as processed Ready.");
        }

        [Test]
        public void ProcessedAvatarReadiness_AllowsNdmfWithApplyOnPlay()
        {
            var gate = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "CanReportProcessedAvatarReady",
                typeof(bool),
                typeof(bool));

            Assert.That(InvokeBool(gate, true, true), Is.True);
        }

        [Test]
        public void ProcessedAvatarQueue_RequiresAProcessorCompletionCallback()
        {
            var gate = RequireStaticMethod(
                typeof(MotionCaptureCoordinator),
                "CanQueueProcessedAvatar",
                typeof(bool),
                typeof(bool));

            Assert.That(InvokeBool(gate, true, false), Is.False,
                "Arming Apply on Play alone must not promote the raw additive clone.");
            Assert.That(InvokeBool(gate, true, true), Is.True);
        }

        [Test]
        public void ManualTrackerRole_RemovesAConflictingRememberedAutomaticRole()
        {
            using (var provider = new ValveOpenVrTrackerProvider())
            {
                var field = typeof(ValveOpenVrTrackerProvider).GetField(
                    "_automaticRoles", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(field, Is.Not.Null);
                var roles = (Dictionary<string, TrackerRole>)field.GetValue(provider);
                roles["tracker-a"] = TrackerRole.Waist;

                provider.AssignRole("tracker-b", TrackerRole.Waist);

                Assert.That(roles.ContainsKey("tracker-a"), Is.False,
                    "A manual serial-to-role assignment must remain unique against provisional mappings.");
            }
        }

        [Test]
        public void OpenVrProvider_CanOwnBackgroundInitializationAndShutdown()
        {
            var type = typeof(ValveOpenVrTrackerProvider);
            Assert.That(type.GetMethod(
                "TryInitializeBackgroundApplication",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Not.Null);
            Assert.That(type.GetField(
                "_ownsOpenVrInitialization",
                BindingFlags.Instance | BindingFlags.NonPublic), Is.Not.Null,
                "The provider must track ownership so a normal Unity session can initialize OpenVR safely.");
        }

        [Test]
        public void TestReset_RecognizesOnlyRecoveryFilesOwnedByItsSession()
        {
            const string testSession = "test-session-123";

            Assert.That(MotionCaptureCoordinator.IsRecoveryFileOwnedByTest(
                    "Recovery/20260813-avatar-test-session-123.jsonl",
                    testSession),
                Is.True);
            Assert.That(MotionCaptureCoordinator.IsRecoveryFileOwnedByTest(
                    "Recovery/test-session-123.review-checkpoint.json",
                    testSession),
                Is.True);
            Assert.That(MotionCaptureCoordinator.IsRecoveryFileOwnedByTest(
                    "Recovery/manual-user-session.review-checkpoint.json",
                    testSession),
                Is.False,
                "A PlayMode test reset must never delete an unrelated user Recovery file.");
            Assert.That(MotionCaptureCoordinator.IsRecoveryFileOwnedByTest(
                    "Recovery/test-session-123.review-checkpoint.json",
                    string.Empty),
                Is.False);
        }

        private static TrackerPoseSample Pose(TrackerRole role, string id, bool valid)
        {
            return new TrackerPoseSample
            {
                role = role,
                deviceId = id,
                connected = true,
                valid = valid,
                position = Vector3.zero,
                rotation = Quaternion.identity
            };
        }

        private static MethodInfo RequireStaticMethod(Type type, string name, params Type[] parameterTypes)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameterTypes,
                null);
            if (method == null)
            {
                Assert.Fail(
                    $"Missing regression seam {type.FullName}.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))}). " +
                    "Implement this contract, then replace/refactor the reflection call if desired.");
            }

            return method;
        }

        private static bool InvokeBool(MethodInfo method, params object[] arguments)
        {
            return (bool)method.Invoke(null, arguments);
        }

        private static IEnumerable<T> ReadSequence<T>(Type type, object instance, string name)
        {
            var value = ReadMember(type, instance, name);
            return value as IEnumerable<T> ?? Array.Empty<T>();
        }

        private static T ReadValue<T>(Type type, object instance, string name)
        {
            return (T)ReadMember(type, instance, name);
        }

        private static object ReadMember(Type type, object instance, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return property.GetValue(instance);
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            Assert.Fail($"Capture sample plan is missing member {name}.");
            return null;
        }
    }
}

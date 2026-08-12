using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    public sealed class CaptureGapResolutionAcceptanceTests
    {
        [Test]
        public void ShortCoreTrackingGap_RepairsPlaceholderThenReplaysIkToResolvedHumanPose()
        {
            var resolvedMember = RequireResolvedMember();
            using (var fixture =
                   new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            using (var binding = new HumanoidAvatarBinding(fixture.Root, fixture.Animator))
            {
                var sourcePose = fixture.CaptureHumanPose();
                var frames = CoreGapFrames(fixture, sourcePose, 1d / 60d, 2d / 60d);
                SetResolved(resolvedMember, frames[0], true);
                SetResolved(resolvedMember, frames[1], false);
                SetResolved(resolvedMember, frames[2], true);

                var warnings = TrackerGapInterpolator.Repair(frames);

                Assert.That(warnings, Is.Empty);
                Assert.That(GetResolved(resolvedMember, frames[1]), Is.False,
                    "Tracker repair alone must not pretend that an unresolved HumanPose is complete.");
                Assert.That(MotionCaptureCoordinator.HasUsableCoreTracking(frames[1].trackers), Is.True);
                foreach (var role in CoreRoles)
                {
                    var repaired = frames[1].trackers.Find(role);
                    Assert.That(repaired, Is.Not.Null);
                    Assert.That(repaired.valid, Is.True);
                    Assert.That(repaired.interpolated, Is.True);
                }

                var replayRig = new MotionCaptureRig(binding).CreateIkOnlyReplayRig();
                fixture.ApplyHumanPose(sourcePose);
                Assert.That(replayRig.Apply(frames[0].trackers, 0, null), Is.True);
                fixture.ApplyHumanPose(sourcePose);
                Assert.That(replayRig.Apply(frames[1].trackers, 1, null), Is.True,
                    "The repaired tracker sample must be replayable through capture IK.");
                var repairedPose = fixture.CaptureHumanPose();
                frames[1].ikBodyPosition = repairedPose.bodyPosition;
                frames[1].ikBodyRotation = repairedPose.bodyRotation;
                frames[1].ikMuscles = (float[])repairedPose.muscles.Clone();
                frames[1].bodyPosition = repairedPose.bodyPosition;
                frames[1].bodyRotation = repairedPose.bodyRotation;
                frames[1].muscles = (float[])repairedPose.muscles.Clone();
                SetResolved(resolvedMember, frames[1], true);

                Assert.That(GetResolved(resolvedMember, frames[1]), Is.True);
                AssertFiniteFrame(frames[1]);
            }
        }

        [Test]
        public void LongCoreTrackingGap_RemainsUnresolvedWarnedAndFinite()
        {
            var resolvedMember = RequireResolvedMember();
            using (var fixture =
                   new GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions.Standard))
            {
                var sourcePose = fixture.CaptureHumanPose();
                var frames = CoreGapFrames(fixture, sourcePose, 7d / 60d, 8d / 60d);
                SetResolved(resolvedMember, frames[0], true);
                SetResolved(resolvedMember, frames[1], false);
                SetResolved(resolvedMember, frames[2], true);

                var warnings = TrackerGapInterpolator.Repair(frames);

                Assert.That(warnings.Select(warning => warning.role).Distinct(),
                    Is.EquivalentTo(CoreRoles));
                Assert.That(warnings.All(warning => warning.duration > 0.1d), Is.True);
                Assert.That(MotionCaptureCoordinator.HasUsableCoreTracking(frames[1].trackers), Is.False);
                Assert.That(GetResolved(resolvedMember, frames[1]), Is.False,
                    "A gap over 100 ms must remain an unresolved placeholder.");
                foreach (var role in CoreRoles)
                {
                    var missing = frames[1].trackers.Find(role);
                    Assert.That(missing.valid, Is.False);
                    Assert.That(missing.interpolated, Is.False);
                }

                AssertFiniteFrame(frames[1]);
            }
        }

        private static readonly TrackerRole[] CoreRoles =
        {
            TrackerRole.Head,
            TrackerRole.LeftHand,
            TrackerRole.RightHand
        };

        private static List<HumanoidCaptureFrame> CoreGapFrames(
            GeneratedHumanoidAcceptanceFixture fixture,
            HumanPose sourcePose,
            double missingTime,
            double endingTime)
        {
            var first = fixture.CreateTrackerFrame(CoreRoles, 0d);
            var missing = fixture.CreateTrackerFrame(CoreRoles, missingTime);
            foreach (var sample in missing.poses)
            {
                sample.valid = false;
            }

            var last = fixture.CreateTrackerFrame(CoreRoles, endingTime);
            foreach (var sample in last.poses)
            {
                sample.position += EndpointDelta(sample.role);
            }

            return new List<HumanoidCaptureFrame>
            {
                Placeholder(0d, first, sourcePose),
                Placeholder(missingTime, missing, sourcePose),
                Placeholder(endingTime, last, sourcePose)
            };
        }

        private static HumanoidCaptureFrame Placeholder(
            double time,
            TrackerFrame trackerFrame,
            HumanPose sourcePose)
        {
            return new HumanoidCaptureFrame
            {
                time = time,
                sourceBodyPosition = sourcePose.bodyPosition,
                sourceBodyRotation = sourcePose.bodyRotation,
                sourceMuscles = (float[])sourcePose.muscles.Clone(),
                ikBodyPosition = sourcePose.bodyPosition,
                ikBodyRotation = sourcePose.bodyRotation,
                ikMuscles = (float[])sourcePose.muscles.Clone(),
                bodyPosition = sourcePose.bodyPosition,
                bodyRotation = sourcePose.bodyRotation,
                muscles = (float[])sourcePose.muscles.Clone(),
                trackers = trackerFrame
            };
        }

        private static Vector3 EndpointDelta(TrackerRole role)
        {
            switch (role)
            {
                case TrackerRole.Head:
                    return new Vector3(0.01f, 0.01f, 0f);
                case TrackerRole.LeftHand:
                    return new Vector3(0f, 0.02f, 0.01f);
                case TrackerRole.RightHand:
                    return new Vector3(0f, -0.01f, 0.02f);
                default:
                    return Vector3.zero;
            }
        }

        private static MemberInfo RequireResolvedMember()
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(HumanoidCaptureFrame);
            var field = type.GetField("resolved", flags);
            if (field != null && field.FieldType == typeof(bool))
            {
                return field;
            }

            var property = type.GetProperty("Resolved", flags);
            if (property != null && property.PropertyType == typeof(bool) && property.CanRead)
            {
                return property;
            }

            Assert.Fail(
                "HumanoidCaptureFrame needs a serialized bool resolved (or readable Resolved property) " +
                "so invalid core samples survive as explicit unresolved placeholders until gap repair/replay.");
            return null;
        }

        private static bool GetResolved(MemberInfo member, HumanoidCaptureFrame frame)
        {
            var field = member as FieldInfo;
            if (field != null)
            {
                return (bool)field.GetValue(frame);
            }

            return (bool)((PropertyInfo)member).GetValue(frame, null);
        }

        private static void SetResolved(
            MemberInfo member,
            HumanoidCaptureFrame frame,
            bool resolved)
        {
            var field = member as FieldInfo;
            if (field != null)
            {
                field.SetValue(frame, resolved);
                return;
            }

            var property = (PropertyInfo)member;
            if (!property.CanWrite)
            {
                Assert.Fail("HumanoidCaptureFrame.Resolved must be writable by the capture/replay pipeline.");
            }

            property.SetValue(frame, resolved, null);
        }

        private static void AssertFiniteFrame(HumanoidCaptureFrame frame)
        {
            Assert.That(IsFinite(frame.sourceBodyPosition), Is.True);
            Assert.That(IsFinite(frame.sourceBodyRotation), Is.True);
            Assert.That(IsFinite(frame.ikBodyPosition), Is.True);
            Assert.That(IsFinite(frame.ikBodyRotation), Is.True);
            Assert.That(IsFinite(frame.bodyPosition), Is.True);
            Assert.That(IsFinite(frame.bodyRotation), Is.True);
            Assert.That(frame.sourceMuscles.All(IsFinite), Is.True);
            Assert.That(frame.ikMuscles.All(IsFinite), Is.True);
            Assert.That(frame.muscles.All(IsFinite), Is.True);
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
    }
}

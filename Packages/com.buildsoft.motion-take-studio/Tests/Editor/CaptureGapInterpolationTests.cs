using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    public sealed class CaptureGapInterpolationTests
    {
        [Test]
        public void Repair_InterpolatesBracketedGapAtOrBelowOneHundredMilliseconds()
        {
            var frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, Pose(true, Vector3.zero)),
                Frame(0.05d, Pose(false, Vector3.zero)),
                Frame(0.1d, Pose(true, Vector3.right))
            };

            var warnings = TrackerGapInterpolator.Repair(frames);

            Assert.That(warnings, Is.Empty);
            var repaired = frames[1].trackers.Find(TrackerRole.Head);
            Assert.That(repaired.valid, Is.True);
            Assert.That(repaired.interpolated, Is.True);
            Assert.That(repaired.position.x, Is.EqualTo(0.5f).Within(0.0001f));
        }

        [Test]
        public void Repair_LeavesLongGapInvalidAndReportsIt()
        {
            var frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, Pose(true, Vector3.zero)),
                Frame(0.1d, Pose(false, Vector3.zero)),
                Frame(0.2d, Pose(true, Vector3.right))
            };

            var warnings = TrackerGapInterpolator.Repair(frames);

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0].duration, Is.EqualTo(0.2d).Within(0.000001d));
            Assert.That(frames[1].trackers.Find(TrackerRole.Head).valid, Is.False);
        }

        [Test]
        public void Repair_ReportsLeadingAndTrailingUnbracketedGaps()
        {
            var frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, Pose(false, Vector3.zero)),
                Frame(1d / 60d, Pose(true, Vector3.zero)),
                Frame(2d / 60d, Pose(false, Vector3.zero))
            };

            var warnings = TrackerGapInterpolator.Repair(frames);

            Assert.That(warnings, Has.Count.EqualTo(2));
            Assert.That(warnings[0].message, Does.Contain("unbracketed"));
            Assert.That(warnings[1].message, Does.Contain("unbracketed"));
        }

        private static HumanoidCaptureFrame Frame(double time, TrackerPoseSample pose)
        {
            return new HumanoidCaptureFrame
            {
                time = time,
                trackers = new TrackerFrame
                {
                    time = time,
                    poses = new List<TrackerPoseSample> { pose }
                }
            };
        }

        private static TrackerPoseSample Pose(bool valid, Vector3 position)
        {
            return new TrackerPoseSample
            {
                role = TrackerRole.Head,
                deviceId = "head",
                connected = true,
                valid = valid,
                position = position,
                rotation = Quaternion.identity
            };
        }
    }
}

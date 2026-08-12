using System.Collections.Generic;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class CaptureGapTests
    {
        [Test]
        public void Repair_InterpolatesBracketedGapAtOneHundredMilliseconds()
        {
            var frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, true, Vector3.zero),
                Frame(0.05d, false, Vector3.zero),
                Frame(0.1d, true, Vector3.right)
            };

            var warnings = TrackerGapInterpolator.Repair(frames, 0.1d);

            Assert.That(warnings, Is.Empty);
            var repaired = frames[1].trackers.Find(TrackerRole.Head);
            Assert.That(repaired.valid, Is.True);
            Assert.That(repaired.interpolated, Is.True);
            Assert.That(repaired.position.x, Is.EqualTo(0.5f).Within(0.0001f));
        }

        [Test]
        public void Repair_LeavesLongGapInvalidAndReportsRange()
        {
            var frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, true, Vector3.zero),
                Frame(0.1d, false, Vector3.zero),
                Frame(0.2d, true, Vector3.right)
            };

            var warnings = TrackerGapInterpolator.Repair(frames, 0.1d);

            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(frames[1].trackers.Find(TrackerRole.Head).valid, Is.False);
            Assert.That(warnings[0].duration, Is.GreaterThan(0.1d));
        }

        [Test]
        public void Repair_ReportsLeadingAndTrailingUnbracketedGaps()
        {
            var leading = new List<HumanoidCaptureFrame>
            {
                Frame(0d, false, Vector3.zero),
                Frame(1d / 60d, true, Vector3.zero)
            };
            var trailing = new List<HumanoidCaptureFrame>
            {
                Frame(0d, true, Vector3.zero),
                Frame(1d / 60d, false, Vector3.zero)
            };

            Assert.That(TrackerGapInterpolator.Repair(leading), Has.Count.EqualTo(1));
            Assert.That(TrackerGapInterpolator.Repair(trailing), Has.Count.EqualTo(1));
        }

        private static HumanoidCaptureFrame Frame(double time, bool valid, Vector3 position)
        {
            return new HumanoidCaptureFrame
            {
                time = time,
                trackers = new TrackerFrame
                {
                    time = time,
                    poses = new List<TrackerPoseSample>
                    {
                        new TrackerPoseSample
                        {
                            role = TrackerRole.Head,
                            connected = true,
                            valid = valid,
                            position = position,
                            rotation = Quaternion.identity
                        }
                    }
                }
            };
        }
    }
}

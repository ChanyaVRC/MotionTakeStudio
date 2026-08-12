using System.Collections.Generic;
using System.Linq;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class ClipValidationTests
    {
        [Test]
        public void Validate_DetectsAllRequiredIssueKinds()
        {
            var source = new ArrayValidationSource(30f,
                Sample(0, Vector3.zero, Vector3.zero, Vector3.zero, true),
                Sample(1, new Vector3(0.5f, 0f, 0f), new Vector3(0f, -0.1f, 0f),
                    new Vector3(0.5f, 0f, 0f), false),
                Sample(2, new Vector3(0.5f, 0f, 0f), Vector3.zero, Vector3.zero, false,
                    float.NaN),
                Sample(3, new Vector3(0.5f, 0f, 0f), Vector3.zero, Vector3.zero, true));

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
            Assert.That(kinds, Does.Contain(MotionTakeValidationKind.TrackingGap));

            var gap = issues.Single(issue => issue.Kind == MotionTakeValidationKind.TrackingGap);
            Assert.That(gap.Frame, Is.EqualTo(1));
            Assert.That(gap.EndFrame, Is.EqualTo(2));
        }

        [Test]
        public void Validate_GroupsUnavailableSamplesIntoOneGap()
        {
            var source = new SparseValidationSource();
            var issues = MotionTakeValidationEngine.Validate(source);

            var gap = issues.Single(issue => issue.Kind == MotionTakeValidationKind.TrackingGap);
            Assert.That(gap.Frame, Is.EqualTo(1));
            Assert.That(gap.EndFrame, Is.EqualTo(3));
        }

        [Test]
        public void Validate_DetectsAOneFrameLimbBendFlip()
        {
            var first = Sample(0, Vector3.zero, Vector3.zero, Vector3.zero, true);
            first.BendDirections = new[] { Vector3.up };
            var flipped = Sample(1, Vector3.zero, Vector3.zero, Vector3.zero, true);
            flipped.BendDirections = new[] { Vector3.down };

            var issues = MotionTakeValidationEngine.Validate(
                new ArrayValidationSource(60f, first, flipped));

            Assert.That(issues.Any(issue =>
                issue.Kind == MotionTakeValidationKind.JointFlip && issue.Frame == 1), Is.True);
        }

        private static MotionTakeValidationSample Sample(
            int frame,
            Vector3 root,
            Vector3 leftFoot,
            Vector3 rightFoot,
            bool tracking,
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
                TrackingAvailable = tracking
            };
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

        private sealed class SparseValidationSource : IMotionTakeValidationSource
        {
            public int FrameCount => 5;
            public float FrameRate => 30f;

            public bool TryGetValidationSample(int index, out MotionTakeValidationSample sample)
            {
                if (index >= 1 && index <= 3)
                {
                    sample = default(MotionTakeValidationSample);
                    return false;
                }

                sample = Sample(index, Vector3.zero, Vector3.zero, Vector3.zero, true);
                return true;
            }
        }
    }
}

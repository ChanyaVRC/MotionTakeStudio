using BuildSoft.MotionTakeStudio;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class CoreCorrectionTests
    {
        [Test]
        public void ElbowHint_RaisesJointWithoutMovingReachableHand()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.4f, 0f, 0.3f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.4f, 0.1f, 0.3f),
                Vector3.forward);

            var baseline = TwoBoneIkSolver.Solve(request);
            request.HintPosition += Vector3.up * 0.1f;
            var raised = TwoBoneIkSolver.Solve(request);

            Assert.That(raised.Succeeded, Is.True);
            Assert.That(raised.EndError, Is.LessThanOrEqualTo(0.005f));
            Assert.That(Vector3.Distance(raised.TipPosition, request.TargetPosition),
                Is.LessThanOrEqualTo(0.005f));
            Assert.That(raised.JointPosition.y, Is.GreaterThan(baseline.JointPosition.y));
        }

        [Test]
        public void UnreachableTarget_IsNotUsedAsSolvedTipAndProducesWarning()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.5f, 0f, 0f),
                Vector3.right,
                Vector3.right * 2f,
                Vector3.up);

            var result = TwoBoneIkSolver.Solve(request);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.TargetIsReachable, Is.False);
            Assert.That(result.Warning.HasFlag(TwoBoneIkWarning.TargetBeyondReach), Is.True);
            Assert.That(result.EndError, Is.GreaterThan(0f));
            AssertFinite(result.JointPosition);
            AssertFinite(result.TipPosition);
        }

        [Test]
        public void DegenerateHint_ReusesPreviousBendAndNeverProducesNaN()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.5f, 0.2f, 0f),
                Vector3.right,
                Vector3.right,
                Vector3.right * 0.5f,
                Vector3.up);

            var result = TwoBoneIkSolver.Solve(request);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Warning.HasFlag(TwoBoneIkWarning.DegenerateHint), Is.True);
            Assert.That(Vector3.Dot(result.BendDirection, Vector3.up), Is.GreaterThan(0.99f));
            AssertFinite(result.JointPosition);
        }

        [Test]
        public void ElbowHint_AxisCrossingLimitsPerFrameBendChangeAndKeepsHandPinned()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.4f, 0.3f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.8f, 0f, 0f),
                Vector3.up,
                Vector3.up);
            var hints = new[]
            {
                new Vector3(0.4f, 0.2f, 0.01f),
                new Vector3(0.4f, 0.01f, 0.0001f),
                new Vector3(0.4f, -0.01f, -0.0001f),
                new Vector3(0.4f, -0.2f, -0.01f)
            };
            var previous = request.PreviousBendDirection;
            var continuityWasApplied = false;

            foreach (var hint in hints)
            {
                request.HintPosition = hint;
                request.PreviousBendDirection = previous;
                var result = TwoBoneIkSolver.Solve(request);

                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.EndError, Is.LessThanOrEqualTo(0.005f));
                Assert.That(
                    Vector3.Angle(previous, result.BendDirection),
                    Is.LessThanOrEqualTo(
                        TwoBoneIkSolver.DefaultMaximumBendDirectionChangeDegrees + 0.01f));
                continuityWasApplied |= result.Warning.HasFlag(
                    TwoBoneIkWarning.BendContinuityClamped);
                AssertFinite(result.JointPosition);
                AssertFinite(result.TipPosition);
                previous = result.BendDirection;
            }

            Assert.That(continuityWasApplied, Is.True);
        }

        [Test]
        public void DeliberateOppositeHint_ConvergesWithoutSingleFrameFlip()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.4f, 0.3f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.8f, 0f, 0f),
                Vector3.down,
                Vector3.up);
            var previous = request.PreviousBendDirection;

            for (var frame = 0; frame < 3; frame++)
            {
                request.PreviousBendDirection = previous;
                var result = TwoBoneIkSolver.Solve(request);

                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.EndError, Is.LessThanOrEqualTo(0.005f));
                Assert.That(
                    Vector3.Angle(previous, result.BendDirection),
                    Is.LessThanOrEqualTo(
                        TwoBoneIkSolver.DefaultMaximumBendDirectionChangeDegrees + 0.01f));
                previous = result.BendDirection;
            }

            Assert.That(Vector3.Dot(previous, Vector3.down), Is.GreaterThan(0.999f),
                "A deliberate opposite hint must remain reachable after the continuity ramp.");
        }

        [Test]
        public void KneeHint_ExtremeAlternatingSequenceNeverFlipsOrProducesNaN()
        {
            var root = new Vector3(0f, 1f, 0f);
            var tip = Vector3.zero;
            var request = TwoBoneIkRequest.Create(
                root,
                new Vector3(0.25f, 0.5f, 0f),
                tip,
                tip,
                root + Vector3.right * 10000f,
                Vector3.right);
            var previous = request.PreviousBendDirection;

            for (var frame = 0; frame < 12; frame++)
            {
                var side = frame % 2 == 0 ? 1f : -1f;
                request.HintPosition = root + Vector3.right * (10000f * side);
                request.PreviousBendDirection = previous;
                var result = TwoBoneIkSolver.Solve(request);

                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.EndError, Is.LessThanOrEqualTo(0.005f));
                Assert.That(
                    Vector3.Angle(previous, result.BendDirection),
                    Is.LessThanOrEqualTo(
                        TwoBoneIkSolver.DefaultMaximumBendDirectionChangeDegrees + 0.01f));
                AssertFinite(result.JointPosition);
                AssertFinite(result.TipPosition);
                AssertFinite(result.UpperRotationDelta);
                AssertFinite(result.LowerRotationDelta);
                previous = result.BendDirection;
            }
        }

        [Test]
        public void NonFiniteHint_ReturnsStableInvalidResultWithoutNaN()
        {
            var request = TwoBoneIkRequest.Create(
                Vector3.zero,
                new Vector3(0.4f, 0.3f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(0.8f, 0f, 0f),
                new Vector3(float.NaN, 0f, 0f),
                Vector3.up);

            var result = TwoBoneIkSolver.Solve(request);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Warning.HasFlag(TwoBoneIkWarning.InvalidInput), Is.True);
            Assert.That(float.IsNaN(result.EndError), Is.False);
            AssertFinite(result.JointPosition);
            AssertFinite(result.TipPosition);
            AssertFinite(result.BendDirection);
            AssertFinite(result.UpperRotationDelta);
            AssertFinite(result.LowerRotationDelta);
        }

        [Test]
        public void Correction_FadesToZeroOutsideTwelveFrames()
        {
            var track = new MotionPoseCorrectionTrack();
            var key = new MotionPoseKey(30, 12);
            key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                PoseTarget.LeftElbowHint,
                new Vector3(0f, 0.1f, 0f)));
            track.AddOrReplaceKey(key);

            Assert.That(track.TryEvaluate(PoseTarget.LeftElbowHint, 17.99f, out _), Is.False);
            Assert.That(track.Evaluate(PoseTarget.LeftElbowHint, 18f)
                .PositionOffsetNormalized.magnitude, Is.LessThanOrEqualTo(0.0005f));
            Assert.That(track.Evaluate(PoseTarget.LeftElbowHint, 30f)
                .PositionOffsetNormalized.y, Is.EqualTo(0.1f).Within(0.00001f));
            Assert.That(track.Evaluate(PoseTarget.LeftElbowHint, 42f)
                .PositionOffsetNormalized.magnitude, Is.LessThanOrEqualTo(0.0005f));
            Assert.That(track.TryEvaluate(PoseTarget.LeftElbowHint, 42.01f, out _), Is.False);
        }

        [Test]
        public void AddOrReplaceKey_ReplacesDuplicateFrame()
        {
            var track = new MotionPoseCorrectionTrack();
            track.AddOrReplaceKey(new MotionPoseKey(10, 3));
            track.AddOrReplaceKey(new MotionPoseKey(10, 24));

            Assert.That(track.Keys, Has.Count.EqualTo(1));
            Assert.That(track.Keys[0].InfluenceFrames, Is.EqualTo(24));
        }

        [Test]
        public void OverlappingKeys_InterpolatePositionAndShortestRotation()
        {
            var track = new MotionPoseCorrectionTrack();
            var left = new MotionPoseKey(10, 12);
            left.SetTargetOffset(MotionPoseTargetOffset.Create(
                PoseTarget.RightHand,
                true,
                Vector3.zero,
                true,
                Quaternion.Euler(0f, 170f, 0f)));
            var right = new MotionPoseKey(20, 12);
            right.SetTargetOffset(MotionPoseTargetOffset.Create(
                PoseTarget.RightHand,
                true,
                Vector3.right,
                true,
                Quaternion.Euler(0f, -170f, 0f)));
            track.AddOrReplaceKey(left);
            track.AddOrReplaceKey(right);

            var middle = track.Evaluate(PoseTarget.RightHand, 15f);
            Assert.That(middle.PositionOffsetNormalized.x, Is.EqualTo(0.5f).Within(0.0001f));
            var forward = middle.RotationOffsetLocal * Vector3.forward;
            Assert.That(Vector3.Dot(forward, Vector3.back), Is.GreaterThan(0.999f));
        }

        [Test]
        public void RemoveKey_LeavesOtherKeysIntact()
        {
            var track = new MotionPoseCorrectionTrack();
            track.AddOrReplaceKey(new MotionPoseKey(3));
            track.AddOrReplaceKey(new MotionPoseKey(7));

            Assert.That(track.RemoveKey(3), Is.True);
            Assert.That(track.Keys, Has.Count.EqualTo(1));
            Assert.That(track.Keys[0].Frame, Is.EqualTo(7));
        }

        private static void AssertFinite(Vector3 value)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False);
        }

        private static void AssertFinite(Quaternion value)
        {
            Assert.That(float.IsNaN(value.x) || float.IsInfinity(value.x), Is.False);
            Assert.That(float.IsNaN(value.y) || float.IsInfinity(value.y), Is.False);
            Assert.That(float.IsNaN(value.z) || float.IsInfinity(value.z), Is.False);
            Assert.That(float.IsNaN(value.w) || float.IsInfinity(value.w), Is.False);
        }
    }
}

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace BuildSoft.MotionTakeStudio.PlayMode.Tests
{
    public sealed class MotionTakeRuntimePlayModeTests
    {
        [UnityTest]
        public IEnumerator TwoBoneIkSolver_ReachesMovingTargetAcrossRealPlayerFramesWithoutFlipping()
        {
            Assert.That(Application.isPlaying, Is.True,
                "This acceptance test must execute in the player loop.");

            var initialFrame = Time.frameCount;
            var root = Vector3.zero;
            var joint = new Vector3(0.5f, 0.5f, 0f);
            var tip = new Vector3(1f, 0f, 0f);
            var previousBend = Vector3.up;

            for (var sample = 0; sample < 4; sample++)
            {
                yield return null;
                Assert.That(Time.frameCount, Is.GreaterThan(initialFrame + sample));

                var target = new Vector3(1f + sample * 0.015f, 0.08f, 0f);
                var request = TwoBoneIkRequest.Create(
                    root,
                    joint,
                    tip,
                    target,
                    new Vector3(0.45f, 0.7f, 0.04f),
                    previousBend);
                request.MaximumBendDirectionChangeDegrees = 25f;

                var result = TwoBoneIkSolver.Solve(request);
                Assert.That(result.Succeeded, Is.True);
                Assert.That(result.TargetIsReachable, Is.True);
                Assert.That(result.EndError, Is.LessThanOrEqualTo(0.005f));
                Assert.That(Vector3.Distance(result.TipPosition, target), Is.LessThanOrEqualTo(0.005f));
                Assert.That(Vector3.Dot(previousBend.normalized, result.BendDirection),
                    Is.GreaterThan(0f), "The elbow bend plane must not flip between player frames.");

                joint = result.JointPosition;
                tip = result.TipPosition;
                previousBend = result.BendDirection;
            }
        }

        [UnityTest]
        public IEnumerator MotionCaptureAvatarMarker_ConfigurePersistsAcrossTheNextPlayerFrame()
        {
            Assert.That(Application.isPlaying, Is.True,
                "This acceptance test must execute in the player loop.");

            var root = new GameObject("MotionTakeMarkerPlayModeTest");
            var marker = root.AddComponent<MotionCaptureAvatarMarker>();
            marker.Configure("play-session", "source-global-id");
            var configuredFrame = Time.frameCount;

            yield return null;

            Assert.That(Time.frameCount, Is.GreaterThan(configuredFrame));
            Assert.That(marker, Is.Not.Null);
            Assert.That(marker.SessionId, Is.EqualTo("play-session"));
            Assert.That(marker.SourceGlobalObjectId, Is.EqualTo("source-global-id"));

            Object.Destroy(root);
            yield return null;
            Assert.That(root == null, Is.True, "The PlayMode fixture must clean up on a player frame.");
        }
    }
}

using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class MotionTakeDataSafetyTests
    {
        [Test]
        public void Muscles_ReturnsDefensiveCopy()
        {
            var sample = new MotionHumanPoseSample(
                Vector3.zero,
                Quaternion.identity,
                new[] { 0.25f, -0.5f });

            var exposed = sample.Muscles;
            exposed[0] = 99f;

            Assert.That(sample.Muscles[0], Is.EqualTo(0.25f));
        }
    }
}

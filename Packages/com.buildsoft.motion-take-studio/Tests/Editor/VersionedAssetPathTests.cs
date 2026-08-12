using NUnit.Framework;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class VersionedAssetPathTests
    {
        [Test]
        public void SanitizeFileName_UsesFallbackForWhitespace()
        {
            Assert.That(
                BuildSoft.MotionTakeStudio.Editor.VersionedAssetPath.SanitizeFileName("  "),
                Is.EqualTo("MotionTake"));
        }

        [Test]
        public void SanitizeFileName_RemovesInvalidCharacters()
        {
            var result = BuildSoft.MotionTakeStudio.Editor.VersionedAssetPath.SanitizeFileName("dance:take");
            Assert.That(result, Does.Not.Contain(":"));
        }
    }
}

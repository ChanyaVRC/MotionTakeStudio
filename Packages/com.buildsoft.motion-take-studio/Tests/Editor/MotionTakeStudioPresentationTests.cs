using System.Collections.Generic;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class MotionTakeStudioPresentationTests
    {
        [TestCase(MotionTakeSessionPhase.Idle, 0, 1, false, false, false, false, false)]
        [TestCase(MotionTakeSessionPhase.Error, 0, 1, false, false, false, false, true)]
        [TestCase(MotionTakeSessionPhase.Preparing, 1, 0, true, false, false, false, true)]
        [TestCase(MotionTakeSessionPhase.Ready, 1, 2, false, false, false, true, true)]
        [TestCase(MotionTakeSessionPhase.Recording, 1, 3, false, true, false, true, true)]
        [TestCase(MotionTakeSessionPhase.Reviewing, 2, 4, false, false, true, false, true)]
        [TestCase(MotionTakeSessionPhase.Saving, 2, 0, true, false, false, false, true)]
        public void PhasePresentation_MapsWorkflowAndPrimaryAction(
            MotionTakeSessionPhase phase,
            int expectedStep,
            int expectedAction,
            bool expectedBusy,
            bool expectedRecording,
            bool expectedReview,
            bool expectedTrackers,
            bool expectedCancel)
        {
            var presentation = MotionTakeStudioPresentation.ForPhase(phase);

            Assert.That(presentation.ActiveStep, Is.EqualTo(expectedStep));
            Assert.That((int)presentation.PrimaryAction, Is.EqualTo(expectedAction));
            Assert.That(presentation.IsBusy, Is.EqualTo(expectedBusy));
            Assert.That(presentation.IsRecording, Is.EqualTo(expectedRecording));
            Assert.That(presentation.ShowReview, Is.EqualTo(expectedReview));
            Assert.That(presentation.ShowTrackers, Is.EqualTo(expectedTrackers));
            Assert.That(presentation.CanCancel, Is.EqualTo(expectedCancel));
            Assert.That(presentation.Title, Is.Not.Empty);
            Assert.That(presentation.Description, Is.Not.Empty);
        }

        [Test]
        public void ResolveStatus_OperationErrorOverridesSessionMessage()
        {
            var status = MotionTakeStudioPresentation.ResolveStatus(
                "Capture is ready.",
                "OpenVR connection failed.");

            Assert.That(status.Message, Is.EqualTo("OpenVR connection failed."));
            Assert.That(status.IsError, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Palette_PreservesReadableTextContrast(bool darkTheme)
        {
            var palette = MotionTakeStudioPalette.Create(darkTheme);

            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.Text, palette.Surface),
                Is.GreaterThanOrEqualTo(4.5f));
            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.MutedText, palette.Surface),
                Is.GreaterThanOrEqualTo(3f));
            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.AccentText, palette.Accent),
                Is.GreaterThanOrEqualTo(4.5f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Palette_StatusColorsRemainReadableOnStatusPill(bool darkTheme)
        {
            var palette = MotionTakeStudioPalette.Create(darkTheme);
            var colors = new[]
            {
                palette.Accent,
                palette.Success,
                palette.Warning,
                palette.Error,
                palette.Recording
            };

            foreach (var color in colors)
            {
                Assert.That(
                    MotionTakeStudioPalette.ContrastRatio(color, palette.SurfaceElevated),
                    Is.GreaterThanOrEqualTo(4.5f));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Palette_ControlBoundariesRemainVisibleAcrossSurfaces(bool darkTheme)
        {
            var palette = MotionTakeStudioPalette.Create(darkTheme);

            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.Border, palette.Window),
                Is.GreaterThanOrEqualTo(3f));
            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.Border, palette.Surface),
                Is.GreaterThanOrEqualTo(3f));
            Assert.That(
                MotionTakeStudioPalette.ContrastRatio(palette.Border, palette.SurfaceElevated),
                Is.GreaterThanOrEqualTo(3f));
        }

        [Test]
        public void ValidationSummary_SeparatesErrorsWarningsAndInfo()
        {
            var issues = new List<MotionTakeValidationIssue>
            {
                Issue(MotionTakeValidationSeverity.Error),
                Issue(MotionTakeValidationSeverity.Warning),
                Issue(MotionTakeValidationSeverity.Warning),
                Issue(MotionTakeValidationSeverity.Info),
                null
            };

            var summary = MotionTakeStudioPresentation.SummarizeValidation(issues);

            Assert.That(summary.Total, Is.EqualTo(4));
            Assert.That(summary.Errors, Is.EqualTo(1));
            Assert.That(summary.Warnings, Is.EqualTo(2));
            Assert.That(summary.Info, Is.EqualTo(1));
            Assert.That(summary.HighestSeverity, Is.EqualTo(MotionTakeValidationSeverity.Error));
        }

        [Test]
        public void PopulateTrackerLabel_ReusesContentAndAddsTooltip()
        {
            var content = new GUIContent();

            var populated = MotionTakeStudioPresentation.PopulateTrackerLabel(content, "tracker-waist", 4);

            Assert.That(populated, Is.SameAs(content));
            Assert.That(populated.text, Is.EqualTo("tracker-waist"));
            Assert.That(populated.tooltip, Is.EqualTo("tracker-waist"));

            populated = MotionTakeStudioPresentation.PopulateTrackerLabel(content, string.Empty, 4);

            Assert.That(populated, Is.SameAs(content));
            Assert.That(populated.text, Is.EqualTo("Device 4"));
            Assert.That(populated.tooltip, Is.EqualTo("Device 4"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Styles_CreateIndependentSemanticRolesWithoutMutatingSources(bool darkTheme)
        {
            var labelBase = new GUIStyle { fontSize = 7 };
            var buttonBase = new GUIStyle { fixedHeight = 19f };
            var boxBase = new GUIStyle { padding = new RectOffset(1, 2, 3, 4) };
            var palette = MotionTakeStudioPalette.Create(darkTheme);

            var styles = MotionTakeStudioStyles.Create(palette, labelBase, buttonBase, boxBase);
            try
            {
                Assert.That(styles.Card, Is.Not.SameAs(styles.Header));
                Assert.That(styles.PrimaryButton, Is.Not.SameAs(styles.SecondaryButton));
                Assert.That(styles.IssueError, Is.Not.SameAs(styles.IssueWarning));
                Assert.That(styles.TrackerRow, Is.Not.SameAs(styles.TrackerRowDisconnected));
                Assert.That(styles.TrackerId.wordWrap, Is.False);
                Assert.That(styles.TrackerId.clipping, Is.EqualTo(TextClipping.Clip));
                Assert.That(styles.Card.normal.background, Is.Not.Null);
                Assert.That(styles.PrimaryButton.normal.background, Is.Not.Null);
                Assert.That(styles.PrimaryButton.focused.background.width, Is.EqualTo(5));
                Assert.That(styles.PrimaryButton.border.left, Is.EqualTo(2));
                Assert.That(styles.PrimaryButton.fixedHeight, Is.GreaterThan(buttonBase.fixedHeight));
                Assert.That(labelBase.fontSize, Is.EqualTo(7));
                Assert.That(buttonBase.fixedHeight, Is.EqualTo(19f));
                Assert.That(boxBase.padding.left, Is.EqualTo(1));
            }
            finally
            {
                styles.Dispose();
            }
        }

        private static MotionTakeValidationIssue Issue(MotionTakeValidationSeverity severity)
        {
            return new MotionTakeValidationIssue(
                MotionTakeValidationKind.TrackingGap,
                severity,
                0,
                severity.ToString());
        }
    }
}

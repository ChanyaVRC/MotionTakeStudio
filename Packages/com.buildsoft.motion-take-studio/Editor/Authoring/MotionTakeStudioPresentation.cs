using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    internal enum MotionTakePrimaryAction
    {
        None,
        Prepare,
        Record,
        StopAndReview,
        SaveAndExit
    }

    internal readonly struct MotionTakeStudioPhasePresentation
    {
        public readonly string Eyebrow;
        public readonly string Title;
        public readonly string Description;
        public readonly string PrimaryLabel;
        public readonly int ActiveStep;
        public readonly MotionTakePrimaryAction PrimaryAction;
        public readonly bool IsBusy;
        public readonly bool IsRecording;
        public readonly bool ShowReview;
        public readonly bool ShowTrackers;
        public readonly bool CanCancel;

        public MotionTakeStudioPhasePresentation(
            string eyebrow,
            string title,
            string description,
            string primaryLabel,
            int activeStep,
            MotionTakePrimaryAction primaryAction,
            bool isBusy = false,
            bool isRecording = false,
            bool showReview = false,
            bool showTrackers = false,
            bool canCancel = true)
        {
            Eyebrow = eyebrow;
            Title = title;
            Description = description;
            PrimaryLabel = primaryLabel;
            ActiveStep = Mathf.Clamp(activeStep, 0, 2);
            PrimaryAction = primaryAction;
            IsBusy = isBusy;
            IsRecording = isRecording;
            ShowReview = showReview;
            ShowTrackers = showTrackers;
            CanCancel = canCancel;
        }
    }

    internal readonly struct MotionTakeStudioStatusPresentation
    {
        public readonly string Message;
        public readonly bool IsError;

        public MotionTakeStudioStatusPresentation(string message, bool isError)
        {
            Message = message ?? string.Empty;
            IsError = isError;
        }
    }

    internal readonly struct MotionTakeValidationSummary
    {
        public readonly int Total;
        public readonly int Errors;
        public readonly int Warnings;
        public readonly int Info;
        public readonly MotionTakeValidationSeverity HighestSeverity;

        public MotionTakeValidationSummary(
            int total,
            int errors,
            int warnings,
            int info,
            MotionTakeValidationSeverity highestSeverity)
        {
            Total = total;
            Errors = errors;
            Warnings = warnings;
            Info = info;
            HighestSeverity = highestSeverity;
        }
    }

    internal static class MotionTakeStudioPresentation
    {
        public static readonly MotionTakeStudioPhasePresentation Disconnected =
            new MotionTakeStudioPhasePresentation(
                "SESSION OFFLINE",
                "Connect the capture coordinator",
                "The editor session will appear here as soon as the capture coordinator registers.",
                string.Empty,
                0,
                MotionTakePrimaryAction.None,
                canCancel: false);

        public static MotionTakeStudioPhasePresentation ForPhase(MotionTakeSessionPhase phase)
        {
            switch (phase)
            {
                case MotionTakeSessionPhase.Preparing:
                    return new MotionTakeStudioPhasePresentation(
                        "PREPARING PLAY MODE",
                        "Building the capture stage",
                        "The temporary Humanoid and tracker bindings are being stabilized.",
                        "Preparing…",
                        1,
                        MotionTakePrimaryAction.None,
                        isBusy: true);

                case MotionTakeSessionPhase.Ready:
                    return new MotionTakeStudioPhasePresentation(
                        "TRACKING READY",
                        "Ready for a new take",
                        "Check tracker roles, strike the opening pose, then start recording.",
                        "Start Recording",
                        1,
                        MotionTakePrimaryAction.Record,
                        showTrackers: true);

                case MotionTakeSessionPhase.Recording:
                    return new MotionTakeStudioPhasePresentation(
                        "LIVE CAPTURE",
                        "Recording motion",
                        "Humanoid pose and tracker observations are being sampled at the take frame rate.",
                        "Stop & Review",
                        1,
                        MotionTakePrimaryAction.StopAndReview,
                        isRecording: true,
                        showTrackers: true);

                case MotionTakeSessionPhase.Reviewing:
                    return new MotionTakeStudioPhasePresentation(
                        "REVIEW MODE",
                        "Shape the final performance",
                        "Scrub the take, compare solve stages, and author focused pose corrections.",
                        "Save Take & Exit",
                        2,
                        MotionTakePrimaryAction.SaveAndExit,
                        showReview: true);

                case MotionTakeSessionPhase.Saving:
                    return new MotionTakeStudioPhasePresentation(
                        "FINALIZING",
                        "Saving the durable take",
                        "Source take, correction recipe, validation report, and animation clips are being committed.",
                        "Saving…",
                        2,
                        MotionTakePrimaryAction.None,
                        isBusy: true);

                case MotionTakeSessionPhase.Error:
                    return new MotionTakeStudioPhasePresentation(
                        "ACTION REQUIRED",
                        "Capture needs attention",
                        "Resolve the status message below, then prepare the capture again.",
                        "Retry Prepare",
                        0,
                        MotionTakePrimaryAction.Prepare);

                default:
                    return new MotionTakeStudioPhasePresentation(
                        "NEW SESSION",
                        "Create a motion take",
                        "Select a valid Humanoid Animator to prepare an isolated Play Mode capture.",
                        "Prepare Capture",
                        0,
                        MotionTakePrimaryAction.Prepare,
                        canCancel: false);
            }
        }

        public static MotionTakeStudioStatusPresentation ResolveStatus(
            string sessionMessage,
            string operationError)
        {
            return string.IsNullOrWhiteSpace(operationError)
                ? new MotionTakeStudioStatusPresentation(sessionMessage, false)
                : new MotionTakeStudioStatusPresentation(operationError, true);
        }

        public static GUIContent PopulateTrackerLabel(GUIContent content, string deviceId, int deviceIndex)
        {
            if (content == null)
            {
                throw new System.ArgumentNullException(nameof(content));
            }

            var label = string.IsNullOrEmpty(deviceId)
                ? $"Device {deviceIndex}"
                : deviceId;
            content.text = label;
            content.tooltip = label;
            content.image = null;
            return content;
        }

        public static MotionTakeValidationSummary SummarizeValidation(
            IReadOnlyList<MotionTakeValidationIssue> issues)
        {
            var total = 0;
            var errors = 0;
            var warnings = 0;
            var info = 0;
            if (issues != null)
            {
                foreach (var issue in issues)
                {
                    if (issue == null)
                    {
                        continue;
                    }

                    total++;
                    switch (issue.Severity)
                    {
                        case MotionTakeValidationSeverity.Error:
                            errors++;
                            break;
                        case MotionTakeValidationSeverity.Warning:
                            warnings++;
                            break;
                        default:
                            info++;
                            break;
                    }
                }
            }

            var highest = errors > 0
                ? MotionTakeValidationSeverity.Error
                : warnings > 0
                    ? MotionTakeValidationSeverity.Warning
                    : MotionTakeValidationSeverity.Info;
            return new MotionTakeValidationSummary(total, errors, warnings, info, highest);
        }
    }

    internal readonly struct MotionTakeStudioPalette
    {
        public readonly Color Window;
        public readonly Color Header;
        public readonly Color Surface;
        public readonly Color SurfaceElevated;
        public readonly Color Border;
        public readonly Color Text;
        public readonly Color MutedText;
        public readonly Color Accent;
        public readonly Color AccentText;
        public readonly Color Success;
        public readonly Color Warning;
        public readonly Color Error;
        public readonly Color Recording;

        private MotionTakeStudioPalette(
            Color window,
            Color header,
            Color surface,
            Color surfaceElevated,
            Color border,
            Color text,
            Color mutedText,
            Color accent,
            Color accentText,
            Color success,
            Color warning,
            Color error,
            Color recording)
        {
            Window = window;
            Header = header;
            Surface = surface;
            SurfaceElevated = surfaceElevated;
            Border = border;
            Text = text;
            MutedText = mutedText;
            Accent = accent;
            AccentText = accentText;
            Success = success;
            Warning = warning;
            Error = error;
            Recording = recording;
        }

        public static MotionTakeStudioPalette Create(bool darkTheme)
        {
            return darkTheme
                ? new MotionTakeStudioPalette(
                    Hex(0x1B1D22),
                    Hex(0x20242B),
                    Hex(0x262930),
                    Hex(0x2D313A),
                    Hex(0x737B8B),
                    Hex(0xF4F6FB),
                    Hex(0xB4BAC8),
                    Hex(0x38BDF8),
                    Hex(0x07131A),
                    Hex(0x55C78A),
                    Hex(0xF2B84B),
                    Hex(0xF57C85),
                    Hex(0xFF7084))
                : new MotionTakeStudioPalette(
                    Hex(0xE9ECF2),
                    Hex(0xF2F5FA),
                    Hex(0xFBFCFE),
                    Hex(0xF7F9FC),
                    Hex(0x808895),
                    Hex(0x20232A),
                    Hex(0x626978),
                    Hex(0x0B65C2),
                    Hex(0xFFFFFF),
                    Hex(0x14733F),
                    Hex(0x9B6500),
                    Hex(0xC23B47),
                    Hex(0xCF2441));
        }

        public static float ContrastRatio(Color first, Color second)
        {
            var firstLuminance = RelativeLuminance(first);
            var secondLuminance = RelativeLuminance(second);
            var lighter = Mathf.Max(firstLuminance, secondLuminance);
            var darker = Mathf.Min(firstLuminance, secondLuminance);
            return (lighter + 0.05f) / (darker + 0.05f);
        }

        private static float RelativeLuminance(Color color)
        {
            return 0.2126f * Linearize(color.r) +
                   0.7152f * Linearize(color.g) +
                   0.0722f * Linearize(color.b);
        }

        private static float Linearize(float component)
        {
            return component <= 0.03928f
                ? component / 12.92f
                : Mathf.Pow((component + 0.055f) / 1.055f, 2.4f);
        }

        private static Color Hex(int rgb)
        {
            return new Color32(
                (byte)((rgb >> 16) & 0xFF),
                (byte)((rgb >> 8) & 0xFF),
                (byte)(rgb & 0xFF),
                0xFF);
        }
    }
}

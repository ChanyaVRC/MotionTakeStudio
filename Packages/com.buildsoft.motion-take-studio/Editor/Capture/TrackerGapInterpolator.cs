using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>Repairs only short, bracketed tracking gaps. Long gaps remain invalid and are reported.</summary>
    public static class TrackerGapInterpolator
    {
        public const double DefaultMaximumGapSeconds = 0.1d;

        public static List<TrackerGapWarning> Repair(
            IList<HumanoidCaptureFrame> frames,
            double maximumGapSeconds = DefaultMaximumGapSeconds)
        {
            if (frames == null)
            {
                throw new ArgumentNullException(nameof(frames));
            }

            if (maximumGapSeconds <= 0d)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumGapSeconds));
            }

            var warnings = new List<TrackerGapWarning>();
            for (var numericRole = (int)TrackerRole.Head;
                 numericRole <= (int)TrackerRole.RightElbow;
                 numericRole++)
            {
                RepairRole(frames, (TrackerRole)numericRole, maximumGapSeconds, warnings);
            }

            return warnings;
        }

        private static void RepairRole(
            IList<HumanoidCaptureFrame> frames,
            TrackerRole role,
            double maximumGapSeconds,
            ICollection<TrackerGapWarning> warnings)
        {
            var roleWasObserved = false;
            for (var observedIndex = 0; observedIndex < frames.Count; observedIndex++)
            {
                if (GetSample(frames[observedIndex], role) == null)
                {
                    continue;
                }

                roleWasObserved = true;
                break;
            }

            if (!roleWasObserved)
            {
                return;
            }

            var previousValid = -1;
            var index = 0;
            while (index < frames.Count)
            {
                var sample = GetSample(frames[index], role);
                if (IsUsable(sample))
                {
                    previousValid = index++;
                    continue;
                }

                var gapStart = index;
                while (index < frames.Count && !IsUsable(GetSample(frames[index], role)))
                {
                    index++;
                }

                var nextValid = index < frames.Count ? index : -1;
                if (previousValid < 0 || nextValid < 0)
                {
                    var cadence = EstimateCadence(frames, gapStart);
                    var lastInvalid = nextValid < 0 ? frames.Count - 1 : nextValid - 1;
                    var unbracketedStartTime = frames[gapStart].time;
                    var unbracketedEndTime = frames[lastInvalid].time + cadence;
                    var unbracketedDuration = Math.Max(cadence, unbracketedEndTime - unbracketedStartTime);
                    warnings.Add(new TrackerGapWarning
                    {
                        role = role,
                        startTime = unbracketedStartTime,
                        duration = unbracketedDuration,
                        message = $"{role} has an unbracketed tracking gap of {unbracketedDuration * 1000d:0} ms; " +
                                  "it cannot be safely interpolated."
                    });
                    continue;
                }

                var startTime = frames[previousValid].time;
                var endTime = frames[nextValid].time;
                var duration = endTime - startTime;
                if (duration > maximumGapSeconds + 1e-6d)
                {
                    warnings.Add(new TrackerGapWarning
                    {
                        role = role,
                        startTime = frames[gapStart].time,
                        duration = duration,
                        message = $"{role} tracking was lost for {duration * 1000d:0} ms; gaps over " +
                                  $"{maximumGapSeconds * 1000d:0} ms were not interpolated."
                    });
                    continue;
                }

                var from = GetSample(frames[previousValid], role);
                var to = GetSample(frames[nextValid], role);
                for (var gapIndex = gapStart; gapIndex < nextValid; gapIndex++)
                {
                    var denominator = endTime - startTime;
                    var amount = denominator <= double.Epsilon
                        ? 0f
                        : (float)((frames[gapIndex].time - startTime) / denominator);
                    var repaired = from.Clone();
                    repaired.valid = true;
                    repaired.connected = from.connected && to.connected;
                    repaired.interpolated = true;
                    repaired.position = Vector3.LerpUnclamped(from.position, to.position, amount);
                    repaired.rotation = Quaternion.SlerpUnclamped(from.rotation, to.rotation, amount);
                    repaired.velocity = Vector3.LerpUnclamped(from.velocity, to.velocity, amount);
                    repaired.angularVelocity = Vector3.LerpUnclamped(
                        from.angularVelocity,
                        to.angularVelocity,
                        amount);
                    ReplaceSample(frames[gapIndex], role, repaired);
                }
            }
        }

        private static double EstimateCadence(IList<HumanoidCaptureFrame> frames, int nearIndex)
        {
            if (nearIndex > 0)
            {
                var delta = frames[nearIndex].time - frames[nearIndex - 1].time;
                if (delta > double.Epsilon)
                {
                    return delta;
                }
            }

            if (nearIndex + 1 < frames.Count)
            {
                var delta = frames[nearIndex + 1].time - frames[nearIndex].time;
                if (delta > double.Epsilon)
                {
                    return delta;
                }
            }

            return 1d / 60d;
        }

        private static TrackerPoseSample GetSample(HumanoidCaptureFrame frame, TrackerRole role)
        {
            return frame?.trackers?.Find(role);
        }

        private static bool IsUsable(TrackerPoseSample sample)
        {
            return sample != null && sample.connected && sample.valid;
        }

        private static void ReplaceSample(HumanoidCaptureFrame frame, TrackerRole role, TrackerPoseSample sample)
        {
            if (frame.trackers == null)
            {
                frame.trackers = new TrackerFrame { time = frame.time };
            }

            for (var index = 0; index < frame.trackers.poses.Count; index++)
            {
                if (frame.trackers.poses[index].role != role)
                {
                    continue;
                }

                frame.trackers.poses[index] = sample;
                return;
            }

            frame.trackers.poses.Add(sample);
        }
    }
}

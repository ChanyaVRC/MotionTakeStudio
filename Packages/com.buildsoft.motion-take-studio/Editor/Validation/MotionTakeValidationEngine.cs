using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public enum MotionTakeValidationKind
    {
        NonFinitePose,
        RootDiscontinuity,
        FloorPenetration,
        FootSliding,
        TrackingGap,
        IkUnreachable,
        JointFlip
    }

    public enum MotionTakeValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    [Serializable]
    public sealed class MotionTakeValidationIssue
    {
        [SerializeField] private MotionTakeValidationKind _kind;
        [SerializeField] private MotionTakeValidationSeverity _severity;
        [SerializeField] private int _frame;
        [SerializeField] private int _endFrame;
        [SerializeField] private string _message;

        public MotionTakeValidationKind Kind => _kind;
        public MotionTakeValidationSeverity Severity => _severity;
        public int Frame => _frame;
        public int EndFrame => _endFrame;
        public string Message => _message;

        public MotionTakeValidationIssue(
            MotionTakeValidationKind kind,
            MotionTakeValidationSeverity severity,
            int frame,
            string message,
            int endFrame = -1)
        {
            _kind = kind;
            _severity = severity;
            _frame = Mathf.Max(0, frame);
            _endFrame = endFrame < 0 ? _frame : Mathf.Max(_frame, endFrame);
            _message = message ?? string.Empty;
        }
    }

    [Serializable]
    public sealed class MotionTakeValidationSettings
    {
        [Min(0.001f)] public float RootDiscontinuityDistance = 0.35f;
        [Min(0f)] public float FloorPenetrationTolerance = 0.015f;
        [Min(0f)] public float FootContactHeight = 0.04f;
        [Min(0.001f)] public float FootSlidingSpeed = 0.2f;
        [Range(90f, 180f)] public float JointFlipAngle = 120f;
    }

    public struct MotionTakeValidationSample
    {
        public int Frame;
        public Vector3 RootPosition;
        public Quaternion RootRotation;
        public Vector3 LeftFootPosition;
        public Vector3 RightFootPosition;
        public float FloorHeight;
        public float[] Muscles;
        public Vector3[] BendDirections;
        public IReadOnlyList<string> IkWarnings;
        public bool HasRoot;
        public bool HasFeet;
        public bool TrackingAvailable;
    }

    public interface IMotionTakeValidationSource
    {
        int FrameCount { get; }
        float FrameRate { get; }
        bool TryGetValidationSample(int index, out MotionTakeValidationSample sample);
    }

    public sealed class MotionTakeAssetValidationSource : IMotionTakeValidationSource
    {
        private readonly MotionTakeAsset _take;
        private readonly Func<MotionTakeFrame, Vector3> _leftFoot;
        private readonly Func<MotionTakeFrame, Vector3> _rightFoot;
        private readonly Func<MotionTakeFrame, float> _floorHeight;

        public int FrameCount => _take != null ? _take.FrameCount : 0;
        public float FrameRate => _take != null ? _take.FrameRate : 60f;

        public MotionTakeAssetValidationSource(
            MotionTakeAsset take,
            Func<MotionTakeFrame, Vector3> leftFoot = null,
            Func<MotionTakeFrame, Vector3> rightFoot = null,
            Func<MotionTakeFrame, float> floorHeight = null)
        {
            _take = take ?? throw new ArgumentNullException(nameof(take));
            _leftFoot = leftFoot;
            _rightFoot = rightFoot;
            _floorHeight = floorHeight;
        }

        public bool TryGetValidationSample(int index, out MotionTakeValidationSample sample)
        {
            sample = default(MotionTakeValidationSample);
            if (index < 0 || index >= _take.Frames.Count)
            {
                return false;
            }

            var frame = _take.Frames[index];
            var pose = frame?.ResolvedHumanPose;
            if (pose == null)
            {
                return false;
            }

            var trackingAvailable = frame.TrackerPoses != null && frame.TrackerPoses.Count > 0;
            if (trackingAvailable)
            {
                for (var tracker = 0; tracker < frame.TrackerPoses.Count; tracker++)
                {
                    if (!frame.TrackerPoses[tracker].IsUsable)
                    {
                        trackingAvailable = false;
                        break;
                    }
                }
            }

            var hasFeet = _leftFoot != null && _rightFoot != null;
            sample = new MotionTakeValidationSample
            {
                Frame = frame.FrameIndex,
                RootPosition = pose.BodyPosition * _take.HumanScale,
                RootRotation = pose.BodyRotation,
                Muscles = pose.Muscles,
                HasRoot = true,
                HasFeet = hasFeet,
                LeftFootPosition = hasFeet ? _leftFoot(frame) : Vector3.zero,
                RightFootPosition = hasFeet ? _rightFoot(frame) : Vector3.zero,
                FloorHeight = _floorHeight != null ? _floorHeight(frame) : 0f,
                TrackingAvailable = trackingAvailable
            };
            return true;
        }
    }

    public static class MotionTakeValidationEngine
    {
        public static IReadOnlyList<MotionTakeValidationIssue> Validate(
            IMotionTakeValidationSource source,
            MotionTakeValidationSettings settings = null)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            settings = settings ?? new MotionTakeValidationSettings();
            var issues = new List<MotionTakeValidationIssue>();
            var frameRate = Mathf.Max(0.001f, source.FrameRate);
            var gapStart = -1;
            var gapEnd = -1;
            var hasPrevious = false;
            var previous = default(MotionTakeValidationSample);

            for (var index = 0; index < source.FrameCount; index++)
            {
                if (!source.TryGetValidationSample(index, out var sample))
                {
                    ExtendTrackingGap(index, ref gapStart, ref gapEnd);
                    hasPrevious = false;
                    continue;
                }

                var frame = sample.Frame >= 0 ? sample.Frame : index;
                if (!sample.TrackingAvailable)
                {
                    ExtendTrackingGap(frame, ref gapStart, ref gapEnd);
                }
                else
                {
                    FlushTrackingGap(issues, ref gapStart, ref gapEnd);
                }

                AppendIkWarnings(issues, sample.IkWarnings, frame);

                if (!IsFinite(sample))
                {
                    issues.Add(new MotionTakeValidationIssue(
                        MotionTakeValidationKind.NonFinitePose,
                        MotionTakeValidationSeverity.Error,
                        frame,
                        "Pose contains NaN or infinity values."));
                    hasPrevious = false;
                    continue;
                }

                if (sample.HasRoot && hasPrevious && previous.HasRoot)
                {
                    var distance = Vector3.Distance(previous.RootPosition, sample.RootPosition);
                    if (distance > settings.RootDiscontinuityDistance)
                    {
                        issues.Add(new MotionTakeValidationIssue(
                            MotionTakeValidationKind.RootDiscontinuity,
                            MotionTakeValidationSeverity.Warning,
                            frame,
                            $"Root moved {distance:0.###} m in one sample."));
                    }
                }

                if (hasPrevious)
                {
                    CheckJointFlip(issues, previous, sample, settings.JointFlipAngle, frame);
                }

                if (sample.HasFeet)
                {
                    CheckFloorPenetration(issues, sample.LeftFootPosition, sample.FloorHeight,
                        settings.FloorPenetrationTolerance, frame, "Left foot");
                    CheckFloorPenetration(issues, sample.RightFootPosition, sample.FloorHeight,
                        settings.FloorPenetrationTolerance, frame, "Right foot");

                    if (hasPrevious && previous.HasFeet)
                    {
                        var frameDelta = Mathf.Max(1, frame - previous.Frame);
                        var elapsed = frameDelta / frameRate;
                        CheckFootSliding(issues, previous.LeftFootPosition, sample.LeftFootPosition,
                            sample.FloorHeight, settings, frame, elapsed, "Left foot");
                        CheckFootSliding(issues, previous.RightFootPosition, sample.RightFootPosition,
                            sample.FloorHeight, settings, frame, elapsed, "Right foot");
                    }
                }

                previous = sample;
                previous.Frame = frame;
                hasPrevious = true;
            }

            FlushTrackingGap(issues, ref gapStart, ref gapEnd);
            return issues;
        }

        private static void AppendIkWarnings(
            ICollection<MotionTakeValidationIssue> issues,
            IReadOnlyList<string> warnings,
            int frame)
        {
            if (warnings == null)
            {
                return;
            }

            for (var index = 0; index < warnings.Count; index++)
            {
                var warning = warnings[index];
                if (string.IsNullOrWhiteSpace(warning))
                {
                    continue;
                }

                issues.Add(new MotionTakeValidationIssue(
                    MotionTakeValidationKind.IkUnreachable,
                    MotionTakeValidationSeverity.Warning,
                    frame,
                    warning.Trim()));
            }
        }

        private static void ExtendTrackingGap(int frame, ref int start, ref int end)
        {
            if (start < 0)
            {
                start = Mathf.Max(0, frame);
            }

            end = Mathf.Max(start, frame);
        }

        private static void FlushTrackingGap(
            ICollection<MotionTakeValidationIssue> issues,
            ref int start,
            ref int end)
        {
            if (start < 0)
            {
                return;
            }

            var count = end - start + 1;
            issues.Add(new MotionTakeValidationIssue(
                MotionTakeValidationKind.TrackingGap,
                MotionTakeValidationSeverity.Warning,
                start,
                count == 1
                    ? "Tracking data is unavailable for one frame."
                    : $"Tracking data is unavailable for {count} frames.",
                end));
            start = -1;
            end = -1;
        }

        private static void CheckFloorPenetration(
            ICollection<MotionTakeValidationIssue> issues,
            Vector3 foot,
            float floor,
            float tolerance,
            int frame,
            string label)
        {
            var depth = floor - foot.y;
            if (depth <= Mathf.Max(0f, tolerance))
            {
                return;
            }

            issues.Add(new MotionTakeValidationIssue(
                MotionTakeValidationKind.FloorPenetration,
                MotionTakeValidationSeverity.Warning,
                frame,
                $"{label} penetrates the floor by {depth:0.###} m."));
        }

        private static void CheckJointFlip(
            ICollection<MotionTakeValidationIssue> issues,
            MotionTakeValidationSample previous,
            MotionTakeValidationSample current,
            float thresholdDegrees,
            int frame)
        {
            if (previous.BendDirections == null || current.BendDirections == null)
            {
                return;
            }

            var count = Mathf.Min(previous.BendDirections.Length, current.BendDirections.Length);
            for (var index = 0; index < count; index++)
            {
                var before = previous.BendDirections[index];
                var after = current.BendDirections[index];
                if (before.sqrMagnitude <= 1e-8f || after.sqrMagnitude <= 1e-8f ||
                    Vector3.Angle(before, after) < Mathf.Clamp(thresholdDegrees, 90f, 180f))
                {
                    continue;
                }

                issues.Add(new MotionTakeValidationIssue(
                    MotionTakeValidationKind.JointFlip,
                    MotionTakeValidationSeverity.Warning,
                    frame,
                    $"Limb {index + 1} bend direction flipped in one sample."));
                return;
            }
        }

        private static void CheckFootSliding(
            ICollection<MotionTakeValidationIssue> issues,
            Vector3 previous,
            Vector3 current,
            float floor,
            MotionTakeValidationSettings settings,
            int frame,
            float elapsed,
            string label)
        {
            var contactHeight = Mathf.Max(0f, settings.FootContactHeight);
            if (Mathf.Abs(previous.y - floor) > contactHeight ||
                Mathf.Abs(current.y - floor) > contactHeight)
            {
                return;
            }

            var previousPlanar = new Vector2(previous.x, previous.z);
            var currentPlanar = new Vector2(current.x, current.z);
            var speed = Vector2.Distance(previousPlanar, currentPlanar) / Mathf.Max(0.0001f, elapsed);
            if (speed <= Mathf.Max(0.001f, settings.FootSlidingSpeed))
            {
                return;
            }

            issues.Add(new MotionTakeValidationIssue(
                MotionTakeValidationKind.FootSliding,
                MotionTakeValidationSeverity.Warning,
                frame,
                $"{label} slides at {speed:0.###} m/s while in floor contact."));
        }

        internal static bool IsFinite(MotionTakeValidationSample sample)
        {
            if (sample.HasRoot && (!IsFinite(sample.RootPosition) || !IsFinite(sample.RootRotation)))
            {
                return false;
            }

            if (sample.HasFeet &&
                (!IsFinite(sample.LeftFootPosition) || !IsFinite(sample.RightFootPosition) ||
                 !IsFinite(sample.FloorHeight)))
            {
                return false;
            }

            if (sample.Muscles == null)
            {
                return AreFinite(sample.BendDirections);
            }

            for (var index = 0; index < sample.Muscles.Length; index++)
            {
                if (!IsFinite(sample.Muscles[index]))
                {
                    return false;
                }
            }

            return AreFinite(sample.BendDirections);
        }

        private static bool AreFinite(Vector3[] values)
        {
            if (values == null)
            {
                return true;
            }

            for (var index = 0; index < values.Length; index++)
            {
                if (!IsFinite(values[index]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Quaternion value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}

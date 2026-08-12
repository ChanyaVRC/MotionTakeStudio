using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    public enum MotionValidationSeverity
    {
        Info,
        Warning,
        Error
    }

    public enum MotionValidationCategory
    {
        TrackingGap,
        FootSliding,
        FloorPenetration,
        RootDiscontinuity,
        NonFinitePose,
        IkUnreachable,
        JointFlip
    }

    [Serializable]
    public sealed class MotionValidationMarker
    {
        [SerializeField] private MotionValidationCategory category;
        [SerializeField] private MotionValidationSeverity severity;
        [SerializeField, Min(0)] private int startFrame;
        [SerializeField, Min(0)] private int endFrame;
        [SerializeField] private PoseTarget target;
        [SerializeField] private bool hasTarget;
        [SerializeField] private string message;

        public MotionValidationCategory Category => category;
        public MotionValidationSeverity Severity => severity;
        public int StartFrame => startFrame;
        public int EndFrame => endFrame;
        public PoseTarget Target => target;
        public bool HasTarget => hasTarget;
        public string Message => message;

        public MotionValidationMarker(
            MotionValidationCategory category,
            MotionValidationSeverity severity,
            int startFrame,
            int endFrame,
            string message,
            PoseTarget? target = null)
        {
            this.category = category;
            this.severity = severity;
            this.startFrame = Mathf.Max(0, startFrame);
            this.endFrame = Mathf.Max(this.startFrame, endFrame);
            this.target = target ?? PoseTarget.Hips;
            hasTarget = target.HasValue;
            this.message = message ?? string.Empty;
        }
    }

    /// <summary>Timeline validation output saved beside a take.</summary>
    [CreateAssetMenu(menuName = "BuildSoft/Motion Take Studio/Validation Report")]
    public sealed class MotionValidationReport : ScriptableObject
    {
        [SerializeField] private MotionTakeAsset sourceTake;
        [SerializeField] private List<MotionValidationMarker> markers =
            new List<MotionValidationMarker>();

        public MotionTakeAsset SourceTake => sourceTake;
        public IReadOnlyList<MotionValidationMarker> Markers => markers;

        public void Initialize(MotionTakeAsset take)
        {
            sourceTake = take;
            if (markers == null)
            {
                markers = new List<MotionValidationMarker>();
            }
        }

        public void Add(MotionValidationMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            if (markers == null)
            {
                markers = new List<MotionValidationMarker>();
            }

            markers.Add(marker);
            markers.Sort((left, right) => left.StartFrame.CompareTo(right.StartFrame));
        }

        public void Clear()
        {
            if (markers == null)
            {
                markers = new List<MotionValidationMarker>();
            }
            else
            {
                markers.Clear();
            }
        }
    }
}

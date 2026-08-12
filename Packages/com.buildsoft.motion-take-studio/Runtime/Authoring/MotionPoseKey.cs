using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>A non-destructive correction key stored at one take frame.</summary>
    [Serializable]
    public sealed class MotionPoseKey
    {
        public const int DefaultInfluenceFrames = 12;
        public const int MinimumInfluenceFrames = 1;
        public const int MaximumInfluenceFrames = 60;

        [SerializeField, Min(0)] private int frame;
        [SerializeField, Range(MinimumInfluenceFrames, MaximumInfluenceFrames)]
        private int influenceFrames = DefaultInfluenceFrames;
        [SerializeField] private List<MotionPoseTargetOffset> targetOffsets =
            new List<MotionPoseTargetOffset>();

        public MotionPoseKey()
        {
        }

        public MotionPoseKey(int frame, int influenceFrames = DefaultInfluenceFrames)
        {
            Frame = frame;
            InfluenceFrames = influenceFrames;
        }

        public int Frame
        {
            get => frame;
            set => frame = Mathf.Max(0, value);
        }

        public int InfluenceFrames
        {
            get => Mathf.Clamp(influenceFrames, MinimumInfluenceFrames, MaximumInfluenceFrames);
            set => influenceFrames = Mathf.Clamp(value, MinimumInfluenceFrames, MaximumInfluenceFrames);
        }

        public IReadOnlyList<MotionPoseTargetOffset> TargetOffsets => targetOffsets;

        public bool TryGetTargetOffset(PoseTarget target, out MotionPoseTargetOffset offset)
        {
            EnsureList();
            for (var index = 0; index < targetOffsets.Count; index++)
            {
                if (targetOffsets[index].Target == target)
                {
                    offset = targetOffsets[index];
                    return true;
                }
            }

            offset = default(MotionPoseTargetOffset);
            return false;
        }

        public void SetTargetOffset(MotionPoseTargetOffset offset)
        {
            EnsureList();
            for (var index = 0; index < targetOffsets.Count; index++)
            {
                if (targetOffsets[index].Target != offset.Target)
                {
                    continue;
                }

                targetOffsets[index] = offset;
                return;
            }

            targetOffsets.Add(offset);
        }

        public bool RemoveTargetOffset(PoseTarget target)
        {
            EnsureList();
            for (var index = targetOffsets.Count - 1; index >= 0; index--)
            {
                if (targetOffsets[index].Target == target)
                {
                    targetOffsets.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        private void EnsureList()
        {
            if (targetOffsets == null)
            {
                targetOffsets = new List<MotionPoseTargetOffset>();
            }
        }
    }
}

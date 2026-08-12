using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>Imported, avatar-independent raw and IK-resolved motion take.</summary>
    public sealed class MotionTakeAsset : ScriptableObject
    {
        [SerializeField] private string takeDisplayName = "Motion Take";
        [SerializeField] private string sessionId;
        [SerializeField, Min(1f)] private float frameRate = 60f;
        [SerializeField, Min(0.0001f)] private float humanScale = 1f;
        [SerializeField] private string sourceAvatarGlobalObjectId;
        [SerializeField] private List<MotionTakeFrame> frames = new List<MotionTakeFrame>();

        public string TakeDisplayName => takeDisplayName;
        public string SessionId => sessionId;
        public float FrameRate => frameRate;
        public float HumanScale => humanScale;
        public string SourceAvatarGlobalObjectId => sourceAvatarGlobalObjectId;
        public IReadOnlyList<MotionTakeFrame> Frames => frames;
        public int FrameCount => frames?.Count ?? 0;
        public float DurationSeconds => FrameCount <= 1 ? 0f :
            (float)frames[FrameCount - 1].TimestampSeconds;

        public void Initialize(
            string displayName,
            string captureSessionId,
            float takeFrameRate,
            float avatarHumanScale,
            string avatarGlobalObjectId)
        {
            takeDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? "Motion Take"
                : displayName.Trim();
            sessionId = captureSessionId ?? string.Empty;
            frameRate = Mathf.Max(1f, takeFrameRate);
            humanScale = Mathf.Max(0.0001f, avatarHumanScale);
            sourceAvatarGlobalObjectId = avatarGlobalObjectId ?? string.Empty;
            if (frames == null)
            {
                frames = new List<MotionTakeFrame>();
            }
        }

        public void ClearFrames()
        {
            if (frames == null)
            {
                frames = new List<MotionTakeFrame>();
            }
            else
            {
                frames.Clear();
            }
        }

        public void AddOrReplaceFrame(MotionTakeFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            if (frames == null)
            {
                frames = new List<MotionTakeFrame>();
            }

            if (frames.Count == 0 ||
                (frames[frames.Count - 1] != null &&
                 frames[frames.Count - 1].FrameIndex < frame.FrameIndex))
            {
                frames.Add(frame);
                return;
            }

            var index = FindFrameIndex(frame.FrameIndex);
            if (index >= 0)
            {
                frames[index] = frame;
                return;
            }

            frames.Insert(~index, frame);
        }

        public bool TryGetFrame(int frameIndex, out MotionTakeFrame frame)
        {
            if (frames != null && frames.Count > 0)
            {
                var index = FindFrameIndex(frameIndex);
                if (index >= 0)
                {
                    frame = frames[index];
                    return frame != null;
                }
            }

            frame = null;
            return false;
        }

        private void SortFrames()
        {
            frames.RemoveAll(frame => frame == null);
            frames.Sort((left, right) => left.FrameIndex.CompareTo(right.FrameIndex));
        }

        private int FindFrameIndex(int frameIndex)
        {
            var low = 0;
            var high = frames.Count - 1;
            while (low <= high)
            {
                var middle = low + ((high - low) >> 1);
                var value = frames[middle]?.FrameIndex ?? int.MinValue;
                if (value == frameIndex)
                {
                    return middle;
                }

                if (value < frameIndex)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return ~low;
        }

        private void OnValidate()
        {
            frameRate = Mathf.Max(1f, frameRate);
            humanScale = Mathf.Max(0.0001f, humanScale);
            if (frames == null)
            {
                frames = new List<MotionTakeFrame>();
            }
            else
            {
                SortFrames();
            }
        }
    }
}

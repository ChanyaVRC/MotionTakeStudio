using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    /// <summary>
    /// Evaluates normalized pose deltas. Neighboring keys blend when their
    /// influence ranges overlap; separated keys each fade to zero.
    /// </summary>
    [Serializable]
    public sealed class MotionPoseCorrectionTrack
    {
        [SerializeField] private List<MotionPoseKey> keys = new List<MotionPoseKey>();

        public IReadOnlyList<MotionPoseKey> Keys
        {
            get
            {
                EnsureSorted();
                return keys;
            }
        }

        public MotionPoseKey GetOrCreateKey(
            int frame,
            int influenceFrames = MotionPoseKey.DefaultInfluenceFrames)
        {
            frame = Mathf.Max(0, frame);
            EnsureSorted();
            for (var index = 0; index < keys.Count; index++)
            {
                if (keys[index].Frame == frame)
                {
                    return keys[index];
                }
            }

            var key = new MotionPoseKey(frame, influenceFrames);
            keys.Add(key);
            EnsureSorted();
            return key;
        }

        public void AddOrReplaceKey(MotionPoseKey key)
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }

            EnsureSorted();
            for (var index = 0; index < keys.Count; index++)
            {
                if (keys[index].Frame == key.Frame)
                {
                    keys[index] = key;
                    EnsureSorted();
                    return;
                }
            }

            keys.Add(key);
            EnsureSorted();
        }

        public bool RemoveKey(int frame)
        {
            EnsureList();
            for (var index = keys.Count - 1; index >= 0; index--)
            {
                if (keys[index] != null && keys[index].Frame == frame)
                {
                    keys.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        public bool TryEvaluate(
            PoseTarget target,
            float frame,
            out MotionPoseTargetOffset offset)
        {
            var matching = GetMatchingKeys(target);
            if (matching.Count == 0)
            {
                offset = default(MotionPoseTargetOffset);
                return false;
            }

            var first = matching[0];
            if (frame <= first.Key.Frame)
            {
                return EvaluateFadeIn(first, target, frame, out offset);
            }

            var last = matching[matching.Count - 1];
            if (frame >= last.Key.Frame)
            {
                return EvaluateFadeOut(last, target, frame, out offset);
            }

            for (var index = 0; index < matching.Count - 1; index++)
            {
                var left = matching[index];
                var right = matching[index + 1];
                if (frame < left.Key.Frame || frame > right.Key.Frame)
                {
                    continue;
                }

                var leftEnd = left.Key.Frame + left.Key.InfluenceFrames;
                var rightStart = right.Key.Frame - right.Key.InfluenceFrames;
                if (leftEnd >= rightStart)
                {
                    var interval = Mathf.Max(1f, right.Key.Frame - left.Key.Frame);
                    var amount = SmoothStep01((frame - left.Key.Frame) / interval);
                    offset = MotionPoseTargetOffset.Lerp(left.Offset, right.Offset, amount);
                    return true;
                }

                if (frame <= leftEnd)
                {
                    var amount = SmoothStep01(
                        (frame - left.Key.Frame) / Mathf.Max(1f, left.Key.InfluenceFrames));
                    offset = MotionPoseTargetOffset.Lerp(
                        left.Offset,
                        MotionPoseTargetOffset.Zero(target),
                        amount);
                    return true;
                }

                if (frame >= rightStart)
                {
                    var amount = SmoothStep01(
                        (frame - rightStart) / Mathf.Max(1f, right.Key.InfluenceFrames));
                    offset = MotionPoseTargetOffset.Lerp(
                        MotionPoseTargetOffset.Zero(target),
                        right.Offset,
                        amount);
                    return true;
                }

                offset = default(MotionPoseTargetOffset);
                return false;
            }

            offset = default(MotionPoseTargetOffset);
            return false;
        }

        public MotionPoseTargetOffset Evaluate(PoseTarget target, float frame)
        {
            return TryEvaluate(target, frame, out var offset)
                ? offset
                : MotionPoseTargetOffset.Zero(target);
        }

        private bool EvaluateFadeIn(
            KeyOffset key,
            PoseTarget target,
            float frame,
            out MotionPoseTargetOffset result)
        {
            var start = key.Key.Frame - key.Key.InfluenceFrames;
            if (frame <= start)
            {
                result = default(MotionPoseTargetOffset);
                return false;
            }

            var amount = SmoothStep01(
                (frame - start) / Mathf.Max(1f, key.Key.InfluenceFrames));
            result = MotionPoseTargetOffset.Lerp(
                MotionPoseTargetOffset.Zero(target),
                key.Offset,
                amount);
            return true;
        }

        private bool EvaluateFadeOut(
            KeyOffset key,
            PoseTarget target,
            float frame,
            out MotionPoseTargetOffset result)
        {
            var end = key.Key.Frame + key.Key.InfluenceFrames;
            if (frame >= end)
            {
                result = default(MotionPoseTargetOffset);
                return false;
            }

            var amount = SmoothStep01(
                (frame - key.Key.Frame) / Mathf.Max(1f, key.Key.InfluenceFrames));
            result = MotionPoseTargetOffset.Lerp(
                key.Offset,
                MotionPoseTargetOffset.Zero(target),
                amount);
            return true;
        }

        private List<KeyOffset> GetMatchingKeys(PoseTarget target)
        {
            EnsureSorted();
            var matching = new List<KeyOffset>();
            for (var index = 0; index < keys.Count; index++)
            {
                var key = keys[index];
                if (key != null && key.TryGetTargetOffset(target, out var offset))
                {
                    matching.Add(new KeyOffset(key, offset));
                }
            }

            return matching;
        }

        private static float SmoothStep01(float value)
        {
            value = Mathf.Clamp01(value);
            return value * value * (3f - 2f * value);
        }

        private void EnsureSorted()
        {
            EnsureList();
            keys.RemoveAll(key => key == null);
            keys.Sort((left, right) => left.Frame.CompareTo(right.Frame));
        }

        private void EnsureList()
        {
            if (keys == null)
            {
                keys = new List<MotionPoseKey>();
            }
        }

        private readonly struct KeyOffset
        {
            public KeyOffset(MotionPoseKey key, MotionPoseTargetOffset offset)
            {
                Key = key;
                Offset = offset;
            }

            public MotionPoseKey Key { get; }
            public MotionPoseTargetOffset Offset { get; }
        }
    }
}

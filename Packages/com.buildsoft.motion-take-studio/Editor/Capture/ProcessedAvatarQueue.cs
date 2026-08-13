using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public sealed class HumanoidAvatarBinding : IDisposable
    {
        private readonly Dictionary<HumanBodyBones, Transform> _bones;

        internal HumanoidAvatarBinding(GameObject root, Animator animator)
        {
            Root = root;
            Animator = animator;
            _bones = new Dictionary<HumanBodyBones, Transform>();
            for (var value = 0; value < (int)HumanBodyBones.LastBone; value++)
            {
                var bone = (HumanBodyBones)value;
                var transform = animator.GetBoneTransform(bone);
                if (transform != null)
                {
                    _bones[bone] = transform;
                }
            }

            PoseHandler = new HumanPoseHandler(animator.avatar, animator.transform);
        }

        public GameObject Root { get; }
        public Animator Animator { get; }
        public HumanPoseHandler PoseHandler { get; private set; }
        public IReadOnlyDictionary<HumanBodyBones, Transform> Bones => _bones;

        public bool TryGetBone(HumanBodyBones bone, out Transform transform)
        {
            return _bones.TryGetValue(bone, out transform) && transform != null;
        }

        public void Dispose()
        {
            PoseHandler?.Dispose();
            PoseHandler = null;
        }
    }

    /// <summary>
    /// Queues capture avatar roots as direct references, then waits for two unchanged player frames before
    /// re-fetching Animator and humanoid bone references. No clone-name lookup is used.
    /// </summary>
    internal static class ProcessedAvatarQueue
    {
        private struct Candidate
        {
            public GameObject Root;
            public string Source;
            public int StableFrames;
            public int LastSignature;
        }

        private static readonly Queue<Candidate> Pending = new Queue<Candidate>();
        private static readonly HashSet<int> SeenRoots = new HashSet<int>();
        private static Candidate? _active;

        public static event Action<HumanoidAvatarBinding, string> BindingReady;

        public static void Enqueue(GameObject root, string source)
        {
            if (root == null)
            {
                return;
            }

            if (!SeenRoots.Add(root.GetInstanceID()))
            {
                return;
            }

            Pending.Enqueue(new Candidate
            {
                Root = root,
                Source = string.IsNullOrEmpty(source) ? "unknown processor" : source,
                StableFrames = 0,
                LastSignature = 0
            });
        }

        public static void TickPlayerFrame()
        {
            if (!_active.HasValue)
            {
                while (Pending.Count > 0)
                {
                    var next = Pending.Dequeue();
                    if (next.Root != null)
                    {
                        _active = next;
                        break;
                    }
                }
            }

            if (!_active.HasValue)
            {
                return;
            }

            var candidate = _active.Value;
            if (candidate.Root == null)
            {
                _active = null;
                return;
            }

            var signature = ComputeHierarchySignature(candidate.Root);
            candidate.StableFrames = signature == candidate.LastSignature
                ? candidate.StableFrames + 1
                : 0;
            candidate.LastSignature = signature;
            _active = candidate;
            if (candidate.StableFrames < 2)
            {
                return;
            }

            var animator = candidate.Root.GetComponentInChildren<Animator>(true);
            if (animator == null || animator.avatar == null || !animator.avatar.isValid || !animator.isHuman)
            {
                candidate.StableFrames = 0;
                _active = candidate;
                return;
            }

            _active = null;
            BindingReady?.Invoke(new HumanoidAvatarBinding(candidate.Root, animator), candidate.Source);
        }

        public static void Reset()
        {
            Pending.Clear();
            SeenRoots.Clear();
            _active = null;
        }

        private static int ComputeHierarchySignature(GameObject root)
        {
            unchecked
            {
                var hash = 17;
                var transforms = root.GetComponentsInChildren<Transform>(true);
                hash = hash * 31 + transforms.Length;
                for (var index = 0; index < transforms.Length; index++)
                {
                    hash = hash * 31 + transforms[index].GetInstanceID();
                    hash = hash * 31 + transforms[index].childCount;
                }

                var animator = root.GetComponentInChildren<Animator>(true);
                hash = hash * 31 + (animator == null ? 0 : animator.GetInstanceID());
                hash = hash * 31 + (animator?.avatar == null ? 0 : animator.avatar.GetInstanceID());
                return hash;
            }
        }
    }
}

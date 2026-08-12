using System;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// A capture coordinator can inject its own lease when it already owns Animator playback state.
    /// The preview driver only holds the returned lease for the duration of its binding.
    /// </summary>
    public interface IAnimatorPreviewStateGuard
    {
        IDisposable Acquire(Animator animator);
    }

    public sealed class DefaultAnimatorPreviewStateGuard : IAnimatorPreviewStateGuard
    {
        public IDisposable Acquire(Animator animator)
        {
            if (animator == null)
            {
                throw new ArgumentNullException(nameof(animator));
            }

            return new Lease(animator);
        }

        private sealed class Lease : IDisposable
        {
            private Animator _animator;
            private readonly bool _enabled;
            private readonly float _speed;
            private readonly RuntimeAnimatorController _controller;

            public Lease(Animator animator)
            {
                _animator = animator;
                _enabled = animator.enabled;
                _speed = animator.speed;
                _controller = animator.runtimeAnimatorController;

                // HumanPoseHandler owns preview evaluation while this lease is held.
                animator.enabled = false;
                animator.speed = 0f;
                animator.runtimeAnimatorController = null;
            }

            public void Dispose()
            {
                var animator = _animator;
                _animator = null;
                if (animator == null)
                {
                    return;
                }

                animator.runtimeAnimatorController = _controller;
                animator.speed = _speed;
                animator.enabled = _enabled;
            }
        }
    }

    public sealed class DelegatingAnimatorPreviewStateGuard : IAnimatorPreviewStateGuard
    {
        private readonly Func<Animator, IDisposable> _acquire;

        public DelegatingAnimatorPreviewStateGuard(Func<Animator, IDisposable> acquire)
        {
            _acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        }

        public IDisposable Acquire(Animator animator)
        {
            return _acquire(animator) ?? EmptyLease.Instance;
        }

        private sealed class EmptyLease : IDisposable
        {
            public static readonly EmptyLease Instance = new EmptyLease();

            public void Dispose()
            {
            }
        }
    }
}

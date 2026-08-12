using System;
using System.Collections.Generic;
using BuildSoft.MotionTakeStudio.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Tests
{
    public sealed class ClipBakerTests
    {
        [Test]
        public void BuildClip_WritesMuscleAndRootCurvesOnAnimator()
        {
            var source = new ArrayClipSource(30f,
                CreateSample(0f, Vector3.zero, Quaternion.identity, 0f),
                CreateSample(1f / 30f, Vector3.one, new Quaternion(0f, 0f, 0f, -1f), 0.5f));

            var clip = MotionTakeClipBaker.BuildClip(source, "Test Take");
            try
            {
                var bindings = AnimationUtility.GetCurveBindings(clip);
                Assert.That(bindings, Has.Length.EqualTo(HumanTrait.MuscleCount + 7));
                Assert.That(Array.Exists(bindings, binding =>
                    binding.path == string.Empty && binding.type == typeof(Animator) &&
                    binding.propertyName == HumanTrait.MuscleName[0]), Is.True);

                var rootWBinding = Array.Find(bindings, binding => binding.propertyName == "RootQ.w");
                var rootWCurve = AnimationUtility.GetEditorCurve(clip, rootWBinding);
                Assert.That(rootWCurve.keys, Has.Length.EqualTo(2));
                Assert.That(rootWCurve.keys[1].value, Is.GreaterThan(0.99f),
                    "Quaternion signs should be made continuous before curve creation.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void BuildClip_RejectsNonFiniteMuscles()
        {
            var sample = CreateSample(0f, Vector3.zero, Quaternion.identity, 0f);
            sample.Muscles[0] = float.NaN;
            var source = new ArrayClipSource(30f, sample);

            Assert.Throws<ArgumentException>(() => MotionTakeClipBaker.BuildClip(source, "Invalid"));
        }

        [Test]
        public void CreateVersionedClips_NeverOverwritesAndManualCopiesCorrected()
        {
            const string folder = "Assets/MotionTakeStudioTests";
            var source = new ArrayClipSource(
                30f,
                CreateSample(0f, Vector3.zero, Quaternion.identity, 0.25f));
            MotionTakeClipBakeResult first = null;
            MotionTakeClipBakeResult second = null;
            try
            {
                first = MotionTakeClipBaker.CreateVersionedClips(folder, "Version Test", source, source);
                second = MotionTakeClipBaker.CreateVersionedClips(folder, "Version Test", source, source);

                Assert.That(first.AutoPath, Does.EndWith("_auto_v01.anim"));
                Assert.That(second.AutoPath, Does.EndWith("_auto_v02.anim"));
                Assert.That(second.CorrectedPath, Is.Not.EqualTo(first.CorrectedPath));
                var corrected = AnimationUtility.GetCurveBindings(first.CorrectedClip);
                var manual = AnimationUtility.GetCurveBindings(first.ManualClip);
                Assert.That(manual, Has.Length.EqualTo(corrected.Length));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static MotionTakeClipSample CreateSample(
            float time,
            Vector3 bodyPosition,
            Quaternion bodyRotation,
            float muscleValue)
        {
            var muscles = new float[HumanTrait.MuscleCount];
            for (var index = 0; index < muscles.Length; index++)
            {
                muscles[index] = muscleValue;
            }

            return new MotionTakeClipSample
            {
                TimeSeconds = time,
                BodyPosition = bodyPosition,
                BodyRotation = bodyRotation,
                Muscles = muscles
            };
        }

        private sealed class ArrayClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<MotionTakeClipSample> _samples;

            public int SampleCount => _samples.Count;
            public float FrameRate { get; }

            public ArrayClipSource(float frameRate, params MotionTakeClipSample[] samples)
            {
                FrameRate = frameRate;
                _samples = samples;
            }

            public bool TryGetSample(int index, out MotionTakeClipSample sample)
            {
                if (index < 0 || index >= _samples.Count)
                {
                    sample = default(MotionTakeClipSample);
                    return false;
                }

                sample = _samples[index];
                return true;
            }
        }
    }
}

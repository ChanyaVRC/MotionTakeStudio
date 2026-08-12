using System;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public struct MotionTakeClipSample
    {
        public float TimeSeconds;
        public Vector3 BodyPosition;
        public Quaternion BodyRotation;
        public float[] Muscles;
    }

    public interface IMotionTakeClipSource
    {
        int SampleCount { get; }
        float FrameRate { get; }
        bool TryGetSample(int index, out MotionTakeClipSample sample);
    }

    public sealed class MotionTakeAssetClipSource : IMotionTakeClipSource
    {
        private readonly MotionTakeAsset _take;

        public int SampleCount => _take != null ? _take.FrameCount : 0;
        public float FrameRate => _take != null ? _take.FrameRate : 60f;

        public MotionTakeAssetClipSource(MotionTakeAsset take)
        {
            _take = take ?? throw new ArgumentNullException(nameof(take));
        }

        public bool TryGetSample(int index, out MotionTakeClipSample sample)
        {
            sample = default(MotionTakeClipSample);
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

            sample = new MotionTakeClipSample
            {
                TimeSeconds = (float)frame.TimestampSeconds,
                BodyPosition = pose.BodyPosition,
                BodyRotation = pose.BodyRotation,
                Muscles = pose.Muscles
            };
            return true;
        }
    }

    public sealed class MotionTakeClipBakeResult
    {
        public AnimationClip AutoClip { get; internal set; }
        public AnimationClip CorrectedClip { get; internal set; }
        public AnimationClip ManualClip { get; internal set; }
        public string AutoPath { get; internal set; }
        public string CorrectedPath { get; internal set; }
        public string ManualPath { get; internal set; }

        public bool OpenManualCopy(out string error)
        {
            return AnimationWindowBridge.Open(ManualClip, out error);
        }
    }

    public static class MotionTakeClipBaker
    {
        private static readonly string[] RootPositionProperties =
        {
            "RootT.x", "RootT.y", "RootT.z"
        };

        private static readonly string[] RootRotationProperties =
        {
            "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w"
        };

        public static AnimationClip BuildClip(IMotionTakeClipSource source, string clipName)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.SampleCount <= 0)
            {
                throw new ArgumentException("At least one HumanPose sample is required.", nameof(source));
            }

            var frameRate = Mathf.Max(1f, source.FrameRate);
            var muscleNames = HumanTrait.MuscleName;
            var muscleCurves = new AnimationCurve[muscleNames.Length];
            for (var muscle = 0; muscle < muscleCurves.Length; muscle++)
            {
                muscleCurves[muscle] = new AnimationCurve();
            }

            var rootPositionCurves = CreateCurves(3);
            var rootRotationCurves = CreateCurves(4);
            var previousRotation = Quaternion.identity;
            var hasPreviousRotation = false;

            for (var index = 0; index < source.SampleCount; index++)
            {
                if (!source.TryGetSample(index, out var sample))
                {
                    throw new InvalidOperationException($"HumanPose sample {index} is unavailable.");
                }

                ValidateSample(sample, muscleNames.Length, index);
                var time = sample.TimeSeconds >= 0f ? sample.TimeSeconds : index / frameRate;
                var rotation = Normalize(sample.BodyRotation);
                if (hasPreviousRotation && Quaternion.Dot(previousRotation, rotation) < 0f)
                {
                    rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                }

                previousRotation = rotation;
                hasPreviousRotation = true;

                for (var muscle = 0; muscle < muscleCurves.Length; muscle++)
                {
                    muscleCurves[muscle].AddKey(time, sample.Muscles[muscle]);
                }

                rootPositionCurves[0].AddKey(time, sample.BodyPosition.x);
                rootPositionCurves[1].AddKey(time, sample.BodyPosition.y);
                rootPositionCurves[2].AddKey(time, sample.BodyPosition.z);
                rootRotationCurves[0].AddKey(time, rotation.x);
                rootRotationCurves[1].AddKey(time, rotation.y);
                rootRotationCurves[2].AddKey(time, rotation.z);
                rootRotationCurves[3].AddKey(time, rotation.w);
            }

            var clip = new AnimationClip
            {
                name = string.IsNullOrWhiteSpace(clipName) ? "MotionTake" : clipName.Trim(),
                frameRate = frameRate
            };

            for (var muscle = 0; muscle < muscleCurves.Length; muscle++)
            {
                SetLinearTangents(muscleCurves[muscle]);
                SetAnimatorCurve(clip, muscleNames[muscle], muscleCurves[muscle]);
            }

            for (var axis = 0; axis < rootPositionCurves.Length; axis++)
            {
                SetLinearTangents(rootPositionCurves[axis]);
                SetAnimatorCurve(clip, RootPositionProperties[axis], rootPositionCurves[axis]);
            }

            for (var component = 0; component < rootRotationCurves.Length; component++)
            {
                SetLinearTangents(rootRotationCurves[component]);
                SetAnimatorCurve(clip, RootRotationProperties[component], rootRotationCurves[component]);
            }

            clip.EnsureQuaternionContinuity();
            return clip;
        }

        public static MotionTakeClipBakeResult CreateVersionedClips(
            string outputFolder,
            string takeName,
            IMotionTakeClipSource automaticSource,
            IMotionTakeClipSource correctedSource)
        {
            if (automaticSource == null)
            {
                throw new ArgumentNullException(nameof(automaticSource));
            }

            correctedSource = correctedSource ?? automaticSource;
            var safeName = VersionedAssetPath.SanitizeFileName(takeName);
            var autoClip = BuildClip(automaticSource, safeName + " Auto");
            var correctedClip = BuildClip(correctedSource, safeName + " Corrected");
            var manualClip = UnityEngine.Object.Instantiate(correctedClip);
            manualClip.name = safeName + " Manual";

            var result = new MotionTakeClipBakeResult
            {
                AutoClip = autoClip,
                CorrectedClip = correctedClip,
                ManualClip = manualClip,
                AutoPath = VersionedAssetPath.Next(outputFolder, safeName, "auto", "anim"),
                CorrectedPath = VersionedAssetPath.Next(outputFolder, safeName, "corrected", "anim"),
                ManualPath = VersionedAssetPath.Next(outputFolder, safeName, "manual", "anim")
            };

            var autoCreated = false;
            var correctedCreated = false;
            var manualCreated = false;
            try
            {
                AssetDatabase.CreateAsset(result.AutoClip, result.AutoPath);
                autoCreated = true;
                AssetDatabase.CreateAsset(result.CorrectedClip, result.CorrectedPath);
                correctedCreated = true;
                AssetDatabase.CreateAsset(result.ManualClip, result.ManualPath);
                manualCreated = true;
                AssetDatabase.SaveAssets();
                return result;
            }
            catch
            {
                if (manualCreated)
                {
                    AssetDatabase.DeleteAsset(result.ManualPath);
                }

                if (correctedCreated)
                {
                    AssetDatabase.DeleteAsset(result.CorrectedPath);
                }

                if (autoCreated)
                {
                    AssetDatabase.DeleteAsset(result.AutoPath);
                }

                DestroyUnpersisted(result.AutoClip);
                DestroyUnpersisted(result.CorrectedClip);
                DestroyUnpersisted(result.ManualClip);
                throw;
            }
        }

        private static AnimationCurve[] CreateCurves(int count)
        {
            var curves = new AnimationCurve[count];
            for (var index = 0; index < count; index++)
            {
                curves[index] = new AnimationCurve();
            }

            return curves;
        }

        private static void SetAnimatorCurve(AnimationClip clip, string property, AnimationCurve curve)
        {
            var binding = EditorCurveBinding.FloatCurve(string.Empty, typeof(Animator), property);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        private static void SetLinearTangents(AnimationCurve curve)
        {
            for (var index = 0; index < curve.length; index++)
            {
                AnimationUtility.SetKeyLeftTangentMode(
                    curve,
                    index,
                    AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(
                    curve,
                    index,
                    AnimationUtility.TangentMode.Linear);
            }
        }

        private static void ValidateSample(MotionTakeClipSample sample, int muscleCount, int index)
        {
            if (sample.Muscles == null || sample.Muscles.Length != muscleCount)
            {
                throw new ArgumentException(
                    $"HumanPose sample {index} must contain exactly {muscleCount} muscles.");
            }

            if (!IsFinite(sample.TimeSeconds) || !IsFinite(sample.BodyPosition) ||
                !IsFinite(sample.BodyRotation))
            {
                throw new ArgumentException($"HumanPose sample {index} contains NaN or infinity values.");
            }

            for (var muscle = 0; muscle < sample.Muscles.Length; muscle++)
            {
                if (!IsFinite(sample.Muscles[muscle]))
                {
                    throw new ArgumentException(
                        $"HumanPose sample {index}, muscle {muscle} contains NaN or infinity.");
                }
            }
        }

        private static Quaternion Normalize(Quaternion value)
        {
            var magnitude = Mathf.Sqrt(
                value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (magnitude < 0.000001f)
            {
                return Quaternion.identity;
            }

            var inverse = 1f / magnitude;
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
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

        private static void DestroyUnpersisted(UnityEngine.Object value)
        {
            if (value != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(value)))
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}

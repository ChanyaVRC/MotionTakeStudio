using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    internal struct HumanoidTestProportions
    {
        public HumanoidTestProportions(
            float torso,
            float arms,
            float legs,
            float shoulderWidth,
            float hipWidth)
        {
            Torso = torso;
            Arms = arms;
            Legs = legs;
            ShoulderWidth = shoulderWidth;
            HipWidth = hipWidth;
        }

        public float Torso { get; }
        public float Arms { get; }
        public float Legs { get; }
        public float ShoulderWidth { get; }
        public float HipWidth { get; }

        public static HumanoidTestProportions Standard =>
            new HumanoidTestProportions(1f, 1f, 1f, 1f, 1f);

        public static HumanoidTestProportions Compact =>
            new HumanoidTestProportions(0.86f, 0.78f, 0.88f, 0.84f, 0.9f);

        public static HumanoidTestProportions Tall =>
            new HumanoidTestProportions(1.14f, 1.22f, 1.28f, 1.12f, 1.08f);
    }

    /// <summary>
    /// Minimal generated Humanoid shared by the workflow acceptance tests. It follows the
    /// proven skeleton used by MotionTakePreviewDriverRegressionTests, with only body
    /// proportion parameters added for retargeting coverage.
    /// </summary>
    internal sealed class GeneratedHumanoidAcceptanceFixture : IDisposable
    {
        private readonly Dictionary<HumanBodyBones, Transform> _bones =
            new Dictionary<HumanBodyBones, Transform>();
        private readonly List<Transform> _skeleton = new List<Transform>();
        private readonly HumanPoseHandler _poseHandler;
        private Avatar _avatar;

        public GeneratedHumanoidAcceptanceFixture(HumanoidTestProportions proportions)
        {
            Root = new GameObject("MotionTakeAcceptanceAvatar");
            _skeleton.Add(Root.transform);

            var hips = AddBone(
                HumanBodyBones.Hips,
                Root.transform,
                new Vector3(0f, 1f * proportions.Legs, 0f));
            var spine = AddBone(
                HumanBodyBones.Spine,
                hips,
                new Vector3(0f, 0.2f * proportions.Torso, 0f));
            var chest = AddBone(
                HumanBodyBones.Chest,
                spine,
                new Vector3(0f, 0.2f * proportions.Torso, 0f));
            var neck = AddBone(
                HumanBodyBones.Neck,
                chest,
                new Vector3(0f, 0.18f * proportions.Torso, 0f));
            AddBone(
                HumanBodyBones.Head,
                neck,
                new Vector3(0f, 0.16f * proportions.Torso, 0f));

            var leftShoulder = AddBone(
                HumanBodyBones.LeftShoulder,
                chest,
                new Vector3(-0.12f * proportions.ShoulderWidth, 0.1f * proportions.Torso, 0f));
            var leftUpperArm = AddBone(
                HumanBodyBones.LeftUpperArm,
                leftShoulder,
                new Vector3(-0.12f * proportions.Arms, 0f, 0f));
            var leftLowerArm = AddBone(
                HumanBodyBones.LeftLowerArm,
                leftUpperArm,
                new Vector3(-0.34f * proportions.Arms, 0f, 0f));
            AddBone(
                HumanBodyBones.LeftHand,
                leftLowerArm,
                new Vector3(-0.3f * proportions.Arms, 0f, 0f));

            var rightShoulder = AddBone(
                HumanBodyBones.RightShoulder,
                chest,
                new Vector3(0.12f * proportions.ShoulderWidth, 0.1f * proportions.Torso, 0f));
            var rightUpperArm = AddBone(
                HumanBodyBones.RightUpperArm,
                rightShoulder,
                new Vector3(0.12f * proportions.Arms, 0f, 0f));
            var rightLowerArm = AddBone(
                HumanBodyBones.RightLowerArm,
                rightUpperArm,
                new Vector3(0.34f * proportions.Arms, 0f, 0f));
            AddBone(
                HumanBodyBones.RightHand,
                rightLowerArm,
                new Vector3(0.3f * proportions.Arms, 0f, 0f));

            var leftUpperLeg = AddBone(
                HumanBodyBones.LeftUpperLeg,
                hips,
                new Vector3(-0.1f * proportions.HipWidth, -0.08f * proportions.Legs, 0f));
            var leftLowerLeg = AddBone(
                HumanBodyBones.LeftLowerLeg,
                leftUpperLeg,
                new Vector3(0f, -0.43f * proportions.Legs, 0f));
            AddBone(
                HumanBodyBones.LeftFoot,
                leftLowerLeg,
                new Vector3(0f, -0.42f * proportions.Legs, 0.08f * proportions.Legs));

            var rightUpperLeg = AddBone(
                HumanBodyBones.RightUpperLeg,
                hips,
                new Vector3(0.1f * proportions.HipWidth, -0.08f * proportions.Legs, 0f));
            var rightLowerLeg = AddBone(
                HumanBodyBones.RightLowerLeg,
                rightUpperLeg,
                new Vector3(0f, -0.43f * proportions.Legs, 0f));
            AddBone(
                HumanBodyBones.RightFoot,
                rightLowerLeg,
                new Vector3(0f, -0.42f * proportions.Legs, 0.08f * proportions.Legs));

            var humanBones = _bones.Select(pair => new HumanBone
            {
                boneName = pair.Value.name,
                humanName = pair.Key.ToString(),
                limit = new HumanLimit { useDefaultValues = true }
            }).ToArray();
            var skeletonBones = _skeleton.Select(transform => new SkeletonBone
            {
                name = transform.name,
                position = transform.localPosition,
                rotation = transform.localRotation,
                scale = transform.localScale
            }).ToArray();
            var description = new HumanDescription
            {
                human = humanBones,
                skeleton = skeletonBones,
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false
            };

            _avatar = AvatarBuilder.BuildHumanAvatar(Root, description);
            Assert.That(_avatar, Is.Not.Null);
            Assert.That(_avatar.isValid, Is.True, "Generated acceptance Avatar must be valid.");
            Assert.That(_avatar.isHuman, Is.True, "Generated acceptance Avatar must be Humanoid.");

            Animator = Root.AddComponent<Animator>();
            Animator.avatar = _avatar;
            Animator.applyRootMotion = false;
            Animator.Rebind();
            Animator.Update(0f);
            _poseHandler = new HumanPoseHandler(_avatar, Root.transform);
        }

        public GameObject Root { get; }
        public Animator Animator { get; }
        public float HumanScale => Animator.humanScale;

        public Transform Bone(HumanBodyBones bone)
        {
            return _bones[bone];
        }

        public HumanPose CaptureHumanPose()
        {
            var pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
            _poseHandler.GetHumanPose(ref pose);
            pose.muscles = pose.muscles == null || pose.muscles.Length != HumanTrait.MuscleCount
                ? new float[HumanTrait.MuscleCount]
                : (float[])pose.muscles.Clone();
            return pose;
        }

        public void ApplyHumanPose(HumanPose pose)
        {
            pose.muscles = pose.muscles == null
                ? new float[HumanTrait.MuscleCount]
                : (float[])pose.muscles.Clone();
            _poseHandler.SetHumanPose(ref pose);
        }

        public MotionTakeAsset CreateTake(int frameCount, float frameRate)
        {
            var pose = CaptureHumanPose();
            SetMuscle(pose.muscles, "Left Forearm Stretch", 0.45f);
            SetMuscle(pose.muscles, "Left Arm Down-Up", 0.12f);
            SetMuscle(pose.muscles, "Left Arm Twist In-Out", 0.08f);
            SetMuscle(pose.muscles, "Left Lower Leg Stretch", 0.3f);
            ApplyHumanPose(pose);
            pose = CaptureHumanPose();

            var take = ScriptableObject.CreateInstance<MotionTakeAsset>();
            take.Initialize(
                "Generated Acceptance Take",
                "acceptance-session",
                frameRate,
                HumanScale,
                string.Empty);
            for (var frame = 0; frame < frameCount; frame++)
            {
                var bodyPosition = pose.bodyPosition + new Vector3(frame * 0.002f, 0f, 0f);
                take.AddOrReplaceFrame(new MotionTakeFrame(
                    frame,
                    frame / (double)frameRate,
                    new MotionHumanPoseSample(
                        bodyPosition,
                        pose.bodyRotation,
                        pose.muscles)));
            }

            return take;
        }

        public TrackerFrame CreateTrackerFrame(IReadOnlyList<TrackerRole> roles, double time)
        {
            var frame = new TrackerFrame { time = time };
            for (var index = 0; index < roles.Count; index++)
            {
                var role = roles[index];
                var bone = Bone(BoneForTracker(role));
                frame.poses.Add(new TrackerPoseSample
                {
                    role = role,
                    deviceId = "acceptance-" + role,
                    deviceClass = role == TrackerRole.Head ? "hmd" : "tracker",
                    deviceIndex = index,
                    connected = true,
                    valid = true,
                    position = bone.position,
                    rotation = bone.rotation
                });
            }

            return frame;
        }

        public void Dispose()
        {
            _poseHandler?.Dispose();
            if (_avatar != null)
            {
                DestroyPlaySafe(_avatar);
                _avatar = null;
            }

            if (Root != null)
            {
                DestroyPlaySafe(Root);
            }
        }

        private static void DestroyPlaySafe(UnityEngine.Object value)
        {
            if (value == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }

        private Transform AddBone(
            HumanBodyBones humanBone,
            Transform parent,
            Vector3 localPosition)
        {
            var bone = new GameObject(humanBone.ToString()).transform;
            bone.SetParent(parent, false);
            bone.localPosition = localPosition;
            bone.localRotation = Quaternion.identity;
            bone.localScale = Vector3.one;
            _bones.Add(humanBone, bone);
            _skeleton.Add(bone);
            return bone;
        }

        private static HumanBodyBones BoneForTracker(TrackerRole role)
        {
            switch (role)
            {
                case TrackerRole.Head:
                    return HumanBodyBones.Head;
                case TrackerRole.LeftHand:
                    return HumanBodyBones.LeftHand;
                case TrackerRole.RightHand:
                    return HumanBodyBones.RightHand;
                case TrackerRole.Waist:
                    return HumanBodyBones.Hips;
                case TrackerRole.Chest:
                    return HumanBodyBones.Chest;
                case TrackerRole.LeftFoot:
                    return HumanBodyBones.LeftFoot;
                case TrackerRole.RightFoot:
                    return HumanBodyBones.RightFoot;
                case TrackerRole.LeftKnee:
                    return HumanBodyBones.LeftLowerLeg;
                case TrackerRole.RightKnee:
                    return HumanBodyBones.RightLowerLeg;
                case TrackerRole.LeftElbow:
                    return HumanBodyBones.LeftLowerArm;
                case TrackerRole.RightElbow:
                    return HumanBodyBones.RightLowerArm;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, null);
            }
        }

        private static void SetMuscle(float[] muscles, string name, float value)
        {
            var index = Array.IndexOf(HumanTrait.MuscleName, name);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), "Missing Humanoid muscle " + name + ".");
            muscles[index] = value;
        }
    }
}

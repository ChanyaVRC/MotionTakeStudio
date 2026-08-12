using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio
{
    public enum MotionTrackerRole
    {
        Unassigned,
        Head,
        LeftHand,
        RightHand,
        Waist,
        Chest,
        LeftFoot,
        RightFoot,
        LeftKnee,
        RightKnee,
        LeftElbow,
        RightElbow
    }

    public enum MotionTrackingState
    {
        Unavailable,
        Valid,
        Interpolated,
        Lost
    }

    [Serializable]
    public struct MotionTrackerPoseSample
    {
        [SerializeField] private MotionTrackerRole role;
        [SerializeField] private string deviceId;
        [SerializeField] private MotionTrackingState state;
        [SerializeField] private Vector3 position;
        [SerializeField] private Quaternion rotation;
        [SerializeField] private Vector3 velocity;
        [SerializeField] private Vector3 angularVelocity;

        public MotionTrackerRole Role => role;
        public string DeviceId => deviceId;
        public MotionTrackingState State => state;
        public Vector3 Position => position;
        public Quaternion Rotation => MotionPoseTargetOffset.NormalizeSafe(rotation);
        public Vector3 Velocity => velocity;
        public Vector3 AngularVelocity => angularVelocity;
        public bool IsUsable => state == MotionTrackingState.Valid || state == MotionTrackingState.Interpolated;

        public MotionTrackerPoseSample(
            MotionTrackerRole role,
            string deviceId,
            MotionTrackingState state,
            Vector3 position,
            Quaternion rotation,
            Vector3 velocity = default(Vector3),
            Vector3 angularVelocity = default(Vector3))
        {
            this.role = role;
            this.deviceId = deviceId ?? string.Empty;
            this.state = state;
            this.position = position;
            this.rotation = MotionPoseTargetOffset.NormalizeSafe(rotation);
            this.velocity = velocity;
            this.angularVelocity = angularVelocity;
        }
    }

    [Serializable]
    public sealed class MotionHumanPoseSample
    {
        [SerializeField] private Vector3 bodyPosition;
        [SerializeField] private Quaternion bodyRotation = Quaternion.identity;
        [SerializeField] private float[] muscles = Array.Empty<float>();

        public Vector3 BodyPosition => bodyPosition;
        public Quaternion BodyRotation => MotionPoseTargetOffset.NormalizeSafe(bodyRotation);
        public float[] Muscles => muscles == null ? Array.Empty<float>() : (float[])muscles.Clone();

        public MotionHumanPoseSample()
        {
        }

        public MotionHumanPoseSample(Vector3 position, Quaternion rotation, float[] muscleValues)
        {
            bodyPosition = position;
            bodyRotation = MotionPoseTargetOffset.NormalizeSafe(rotation);
            muscles = muscleValues == null ? Array.Empty<float>() : (float[])muscleValues.Clone();
        }

        public HumanPose ToHumanPose()
        {
            return new HumanPose
            {
                bodyPosition = bodyPosition,
                bodyRotation = BodyRotation,
                muscles = muscles == null ? Array.Empty<float>() : (float[])muscles.Clone()
            };
        }
    }

    [Serializable]
    public sealed class MotionTakeFrame
    {
        [SerializeField, Min(0)] private int frameIndex;
        [SerializeField, Min(0f)] private double timestampSeconds;
        [SerializeField] private List<MotionTrackerPoseSample> trackerPoses =
            new List<MotionTrackerPoseSample>();
        [SerializeField] private MotionHumanPoseSample resolvedHumanPose = new MotionHumanPoseSample();
        [SerializeField] private bool trackingWasInterpolated;

        public MotionTakeFrame()
        {
        }

        public MotionTakeFrame(
            int frameIndex,
            double timestampSeconds,
            MotionHumanPoseSample resolvedHumanPose)
        {
            this.frameIndex = Mathf.Max(0, frameIndex);
            this.timestampSeconds = Math.Max(0d, timestampSeconds);
            this.resolvedHumanPose = resolvedHumanPose ?? new MotionHumanPoseSample();
        }

        public int FrameIndex => frameIndex;
        public double TimestampSeconds => timestampSeconds;
        public IReadOnlyList<MotionTrackerPoseSample> TrackerPoses => trackerPoses;
        public MotionHumanPoseSample ResolvedHumanPose => resolvedHumanPose;
        public bool TrackingWasInterpolated
        {
            get => trackingWasInterpolated;
            set => trackingWasInterpolated = value;
        }

        public void SetResolvedHumanPose(MotionHumanPoseSample pose)
        {
            resolvedHumanPose = pose ?? new MotionHumanPoseSample();
        }

        public void SetTrackerPose(MotionTrackerPoseSample pose)
        {
            if (trackerPoses == null)
            {
                trackerPoses = new List<MotionTrackerPoseSample>();
            }

            for (var index = 0; index < trackerPoses.Count; index++)
            {
                if (trackerPoses[index].Role == pose.Role)
                {
                    trackerPoses[index] = pose;
                    return;
                }
            }

            trackerPoses.Add(pose);
        }

        public bool TryGetTrackerPose(MotionTrackerRole role, out MotionTrackerPoseSample pose)
        {
            if (trackerPoses != null)
            {
                for (var index = 0; index < trackerPoses.Count; index++)
                {
                    if (trackerPoses[index].Role == role)
                    {
                        pose = trackerPoses[index];
                        return true;
                    }
                }
            }

            pose = default(MotionTrackerPoseSample);
            return false;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    public enum MotionCaptureState
    {
        Idle,
        Armed,
        EnteringPlayMode,
        WaitingForProcessedAvatar,
        Ready,
        Recording,
        Review,
        Error
    }

    public enum TrackerRole
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

    [Serializable]
    public sealed class TrackerPoseSample
    {
        public TrackerRole role;
        public string deviceId;
        public string deviceClass;
        public int deviceIndex;
        public bool connected;
        public bool valid;
        public bool interpolated;
        public Vector3 position;
        public Quaternion rotation = Quaternion.identity;
        public Vector3 velocity;
        public Vector3 angularVelocity;

        public TrackerPoseSample Clone()
        {
            return (TrackerPoseSample)MemberwiseClone();
        }
    }

    [Serializable]
    public sealed class TrackerFrame
    {
        public double time;
        public List<TrackerPoseSample> poses = new List<TrackerPoseSample>();

        public TrackerPoseSample Find(TrackerRole role)
        {
            for (var index = 0; index < poses.Count; index++)
            {
                if (poses[index].role == role)
                {
                    return poses[index];
                }
            }

            return null;
        }
    }

    [Serializable]
    public sealed class HumanoidCaptureFrame
    {
        public double time;
        public Vector3 sourceBodyPosition;
        public Quaternion sourceBodyRotation = Quaternion.identity;
        public float[] sourceMuscles = Array.Empty<float>();
        public Vector3 ikBodyPosition;
        public Quaternion ikBodyRotation = Quaternion.identity;
        public float[] ikMuscles = Array.Empty<float>();
        public Vector3 bodyPosition;
        public Quaternion bodyRotation = Quaternion.identity;
        public float[] muscles = Array.Empty<float>();
        public bool resolved;
        public bool hasFeet;
        public Vector3 leftFootPosition;
        public Vector3 rightFootPosition;
        public TrackerFrame trackers = new TrackerFrame();
    }

    [Serializable]
    public sealed class TrackerGapWarning
    {
        public TrackerRole role;
        public double startTime;
        public double duration;
        public string message;
    }

    [Serializable]
    public sealed class CaptureTake
    {
        public string sessionId;
        public string displayName;
        public string sourceGlobalObjectId;
        public string sourceName;
        public string createdUtc;
        public float sampleRate = 60f;
        public float humanScale = 1f;
        public List<HumanoidCaptureFrame> frames = new List<HumanoidCaptureFrame>();
        public List<TrackerGapWarning> gapWarnings = new List<TrackerGapWarning>();

        public double Duration => frames.Count == 0 ? 0d : frames[frames.Count - 1].time;
    }

    public sealed class TrackedDeviceInfo
    {
        public TrackedDeviceInfo(int index, string id, string deviceClass, TrackerRole role, bool connected)
        {
            Index = index;
            Id = id;
            DeviceClass = deviceClass;
            Role = role;
            Connected = connected;
        }

        public int Index { get; }
        public string Id { get; }
        public string DeviceClass { get; }
        public TrackerRole Role { get; }
        public bool Connected { get; }
    }

    public interface ITrackerPoseProvider : IDisposable
    {
        string DisplayName { get; }
        bool IsAvailable { get; }
        string Diagnostic { get; }
        IReadOnlyList<TrackedDeviceInfo> Devices { get; }

        bool TryGetFrame(double time, TrackerFrame destination, out string warning);
        void AssignRole(string deviceId, TrackerRole role);
    }
}

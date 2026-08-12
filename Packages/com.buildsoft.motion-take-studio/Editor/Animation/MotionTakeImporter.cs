using System;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Imports the durable, value-only capture JSON as a retargetable
    /// MotionTakeAsset. No temporary Play Mode object reference is retained.
    /// </summary>
    [ScriptedImporter(1, "mttake")]
    internal sealed class MotionTakeImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            CaptureTake capture;
            try
            {
                var text = File.ReadAllText(context.assetPath);
                capture = JsonUtility.FromJson<CaptureTake>(text);
            }
            catch (Exception exception)
            {
                context.LogImportError("Could not read Motion Take data: " + exception.Message);
                capture = null;
            }

            if (capture == null)
            {
                capture = new CaptureTake
                {
                    displayName = Path.GetFileNameWithoutExtension(context.assetPath),
                    sampleRate = 60f
                };
            }

            var asset = ScriptableObject.CreateInstance<MotionTakeAsset>();
            asset.name = string.IsNullOrWhiteSpace(capture.displayName)
                ? Path.GetFileNameWithoutExtension(context.assetPath)
                : capture.displayName;
            asset.Initialize(
                asset.name,
                capture.sessionId,
                capture.sampleRate,
                capture.humanScale > 0f ? capture.humanScale : 1f,
                capture.sourceGlobalObjectId);

            if (capture.frames == null)
            {
                capture.frames = new System.Collections.Generic.List<HumanoidCaptureFrame>();
            }

            for (var index = 0; index < capture.frames.Count; index++)
            {
                var source = capture.frames[index];
                if (source == null)
                {
                    continue;
                }

                var pose = new MotionHumanPoseSample(
                    source.bodyPosition,
                    source.bodyRotation,
                    source.muscles);
                var frame = new MotionTakeFrame(index, source.time, pose);
                var trackerFrame = source.trackers;
                if (trackerFrame != null && trackerFrame.poses != null)
                {
                    for (var trackerIndex = 0; trackerIndex < trackerFrame.poses.Count; trackerIndex++)
                    {
                        var tracker = trackerFrame.poses[trackerIndex];
                        if (tracker == null)
                        {
                            continue;
                        }

                        var state = tracker.interpolated
                            ? MotionTrackingState.Interpolated
                            : tracker.valid && tracker.connected
                                ? MotionTrackingState.Valid
                                : tracker.connected
                                    ? MotionTrackingState.Lost
                                    : MotionTrackingState.Unavailable;
                        frame.SetTrackerPose(new MotionTrackerPoseSample(
                            (MotionTrackerRole)(int)tracker.role,
                            tracker.deviceId,
                            state,
                            tracker.position,
                            tracker.rotation,
                            tracker.velocity,
                            tracker.angularVelocity));
                        frame.TrackingWasInterpolated |= tracker.interpolated;
                    }
                }

                asset.AddOrReplaceFrame(frame);
            }

            context.AddObjectToAsset("take", asset);
            context.SetMainObject(asset);
        }
    }
}

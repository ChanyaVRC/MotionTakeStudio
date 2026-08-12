using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>
    /// Carries the small recipe/report payload across the Play Mode boundary, then creates durable assets in
    /// Edit Mode. Frame data stays in the already imported .mttake asset rather than SessionState.
    /// </summary>
    internal static class PendingCaptureExport
    {
        private const string PendingKey = "BuildSoft.MotionTakeStudio.Export.Pending";
        private const string PayloadKey = "BuildSoft.MotionTakeStudio.Export.Payload";
        private const string PayloadPathKey = "BuildSoft.MotionTakeStudio.Export.PayloadPath";
        private const string OutputFolder = "Assets/MotionTakeStudio/Takes";
        private const string ClipFolder = "Assets/MotionTakeStudio/Clips";
        private static string _inMemoryPayloadPath;
        private static string _inMemoryPayloadJson;

        public static void Stage(
            string takeAssetPath,
            MotionEditRecipe recipe,
            IReadOnlyList<MotionTakeValidationIssue> validationIssues,
            CaptureTake capture,
            IReadOnlyList<HumanoidCaptureFrame> correctedFrames)
        {
            if (capture == null)
            {
                throw new ArgumentNullException(nameof(capture));
            }

            var payload = new PendingExportPayload
            {
                takeAssetPath = takeAssetPath,
                sessionId = capture.sessionId,
                sourceGlobalObjectId = capture.sourceGlobalObjectId,
                takeName = capture.displayName,
                frameRate = capture.sampleRate,
                correctionTrack = recipe?.CorrectionTrack ?? new MotionPoseCorrectionTrack(),
                correctedFrames = correctedFrames == null
                    ? new List<HumanoidCaptureFrame>()
                    : new List<HumanoidCaptureFrame>(correctedFrames)
            };
            if (validationIssues != null)
            {
                foreach (var issue in validationIssues)
                {
                    if (issue == null)
                    {
                        continue;
                    }

                    payload.validation.Add(new PendingValidationIssue
                    {
                        kind = issue.Kind,
                        severity = issue.Severity,
                        frame = issue.Frame,
                        endFrame = issue.EndFrame,
                        message = issue.Message
                    });
                }
            }

            var json = JsonUtility.ToJson(payload);
            var payloadPath = WriteRecoveryPayload(payload.sessionId, json);
            _inMemoryPayloadPath = payloadPath;
            _inMemoryPayloadJson = json;
            // Long takes can be tens of megabytes. SessionState only carries the durable file path.
            SessionState.EraseString(PayloadKey);
            SessionState.SetString(PayloadPathKey, payloadPath);
            SessionState.SetBool(PendingKey, true);
        }

        public static bool TryFinalize(out string summary, out string error)
        {
            summary = null;
            error = null;
            var payloadPath = SessionState.GetString(PayloadPathKey, string.Empty);
            var json = SessionState.GetString(PayloadKey, string.Empty);
            if (string.IsNullOrEmpty(json) &&
                string.Equals(payloadPath, _inMemoryPayloadPath, StringComparison.OrdinalIgnoreCase))
            {
                json = _inMemoryPayloadJson;
            }

            try
            {
                if (string.IsNullOrEmpty(json) && !string.IsNullOrEmpty(payloadPath) && File.Exists(payloadPath))
                {
                    json = File.ReadAllText(payloadPath);
                }

                if (string.IsNullOrEmpty(json) && TryFindPendingPayload(out var discoveredPath))
                {
                    payloadPath = discoveredPath;
                    json = File.ReadAllText(payloadPath);
                    SessionState.SetString(PayloadPathKey, payloadPath);
                    SessionState.SetString(PayloadKey, json);
                    SessionState.SetBool(PendingKey, true);
                }
            }
            catch (Exception exception)
            {
                error = "Pending capture export could not read its recovery payload and remains retryable: " +
                        exception.Message;
                return false;
            }

            if (!SessionState.GetBool(PendingKey, false) || string.IsNullOrEmpty(json))
            {
                return false;
            }

            string recipePath = null;
            string reportPath = null;
            MotionTakeClipBakeResult clips = null;
            try
            {
                var payload = JsonUtility.FromJson<PendingExportPayload>(json);
                if (payload == null || string.IsNullOrEmpty(payload.takeAssetPath))
                {
                    throw new InvalidOperationException("The pending export payload is missing its take path.");
                }

                AssetDatabase.ImportAsset(payload.takeAssetPath, ImportAssetOptions.ForceSynchronousImport);
                var take = AssetDatabase.LoadAssetAtPath<MotionTakeAsset>(payload.takeAssetPath);
                if (take == null)
                {
                    throw new InvalidOperationException("The saved .mttake could not be imported as MotionTakeAsset.");
                }

                var safeName = string.IsNullOrWhiteSpace(payload.takeName)
                    ? take.TakeDisplayName
                    : payload.takeName;
                var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
                recipe.Initialize(take, safeName + " Recipe");
                if (payload.correctionTrack != null)
                {
                    foreach (var key in payload.correctionTrack.Keys)
                    {
                        recipe.CorrectionTrack.AddOrReplaceKey(key);
                    }
                }

                recipePath = VersionedAssetPath.Next(OutputFolder, safeName, "recipe", "asset");
                AssetDatabase.CreateAsset(recipe, recipePath);

                var report = ScriptableObject.CreateInstance<MotionValidationReport>();
                report.Initialize(take);
                foreach (var issue in payload.validation)
                {
                    report.Add(new MotionValidationMarker(
                        ConvertCategory(issue.kind),
                        (MotionValidationSeverity)(int)issue.severity,
                        issue.frame,
                        issue.endFrame,
                        issue.message));
                }

                reportPath = VersionedAssetPath.Next(OutputFolder, safeName, "validation", "asset");
                AssetDatabase.CreateAsset(report, reportPath);

                var automaticSource = new MotionTakeAssetClipSource(take);
                IMotionTakeClipSource correctedSource = automaticSource;
                var correctedWasEvaluated = payload.correctedFrames != null &&
                                            payload.correctedFrames.Count == take.FrameCount;
                if (correctedWasEvaluated)
                {
                    correctedSource = new CapturedClipSource(payload.correctedFrames, payload.frameRate);
                }
                else if (TryResolveAnimator(payload.sourceGlobalObjectId, out var sourceAnimator))
                {
                    correctedSource = BuildCorrectedClipSource(sourceAnimator, take, recipe);
                    correctedWasEvaluated = true;
                }

                clips = MotionTakeClipBaker.CreateVersionedClips(
                    ClipFolder,
                    safeName,
                    automaticSource,
                    correctedSource);
                AssetDatabase.SaveAssets();
                // The recovery payload is the transaction commit record. Do not clear pending state until the
                // archive move succeeds, otherwise an interrupted archive leaves no retry handle.
                ArchiveRecoveryPayload(payloadPath);
                SessionState.EraseString(PayloadKey);
                SessionState.EraseString(PayloadPathKey);
                SessionState.SetBool(PendingKey, false);
                _inMemoryPayloadPath = null;
                _inMemoryPayloadJson = null;
                summary = $"Saved take, recipe, validation report, and clips. Corrected clip " +
                          (correctedWasEvaluated
                              ? "was evaluated on the selected Humanoid."
                              : "matches Auto because the source Humanoid could not be resolved after Play Mode.") +
                          $" Manual copy: {clips.ManualPath}";
                return true;
            }
            catch (Exception exception)
            {
                if (clips != null)
                {
                    DeleteAssetIfPresent(clips.ManualPath);
                    DeleteAssetIfPresent(clips.CorrectedPath);
                    DeleteAssetIfPresent(clips.AutoPath);
                }

                if (!string.IsNullOrEmpty(reportPath))
                {
                    AssetDatabase.DeleteAsset(reportPath);
                }

                if (!string.IsNullOrEmpty(recipePath))
                {
                    AssetDatabase.DeleteAsset(recipePath);
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                error = "Pending capture export failed and remains retryable: " + exception.Message;
                Debug.LogWarning(
                    "Motion Take Studio export remains retryable after a commit failure: " +
                    exception.Message);
                return false;
            }
        }

        private static void DeleteAssetIfPresent(string assetPath)
        {
            if (!string.IsNullOrEmpty(assetPath))
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
        }

        private static IMotionTakeClipSource BuildCorrectedClipSource(
            Animator animator,
            MotionTakeAsset take,
            MotionEditRecipe recipe)
        {
            var samples = new List<MotionTakeClipSample>(take.FrameCount);
            using (var preview = new MotionTakePreviewDriver())
            {
                preview.Bind(animator, take, recipe);
                using (var poseHandler = new HumanPoseHandler(animator.avatar, animator.transform))
                {
                    var humanPose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
                    for (var frame = 0; frame < take.FrameCount; frame++)
                    {
                        if (!preview.ApplyFrame(frame))
                        {
                            throw new InvalidOperationException($"Could not evaluate corrected frame {frame}.");
                        }

                        poseHandler.GetHumanPose(ref humanPose);
                        samples.Add(new MotionTakeClipSample
                        {
                            TimeSeconds = take.Frames[frame] == null
                                ? frame / take.FrameRate
                                : (float)take.Frames[frame].TimestampSeconds,
                            BodyPosition = humanPose.bodyPosition,
                            BodyRotation = humanPose.bodyRotation,
                            Muscles = humanPose.muscles == null
                                ? Array.Empty<float>()
                                : (float[])humanPose.muscles.Clone()
                        });
                    }
                }
            }

            return new BufferedClipSource(samples, take.FrameRate);
        }

        private static bool TryResolveAnimator(string globalIdText, out Animator animator)
        {
            animator = null;
            if (string.IsNullOrEmpty(globalIdText) || !GlobalObjectId.TryParse(globalIdText, out var globalId))
            {
                return false;
            }

            var root = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as GameObject;
            animator = root != null
                ? root.GetComponent<Animator>() ?? root.GetComponentInChildren<Animator>(true)
                : null;
            return animator != null && animator.avatar != null && animator.avatar.isValid && animator.isHuman;
        }

        private static MotionValidationCategory ConvertCategory(MotionTakeValidationKind kind)
        {
            switch (kind)
            {
                case MotionTakeValidationKind.TrackingGap:
                    return MotionValidationCategory.TrackingGap;
                case MotionTakeValidationKind.FootSliding:
                    return MotionValidationCategory.FootSliding;
                case MotionTakeValidationKind.FloorPenetration:
                    return MotionValidationCategory.FloorPenetration;
                case MotionTakeValidationKind.RootDiscontinuity:
                    return MotionValidationCategory.RootDiscontinuity;
                case MotionTakeValidationKind.NonFinitePose:
                    return MotionValidationCategory.NonFinitePose;
                case MotionTakeValidationKind.IkUnreachable:
                    return MotionValidationCategory.IkUnreachable;
                case MotionTakeValidationKind.JointFlip:
                    return MotionValidationCategory.JointFlip;
                default:
                    return MotionValidationCategory.NonFinitePose;
            }
        }

        private static string WriteRecoveryPayload(string sessionId, string json)
        {
            Directory.CreateDirectory(MotionTakeRecovery.RecoveryDirectory);
            var safeSession = string.IsNullOrWhiteSpace(sessionId)
                ? Guid.NewGuid().ToString("N")
                : VersionedAssetPath.SanitizeFileName(sessionId);
            var path = Path.Combine(
                MotionTakeRecovery.RecoveryDirectory,
                safeSession + ".pending-export.json");
            if (File.Exists(path))
            {
                path = Path.Combine(
                    MotionTakeRecovery.RecoveryDirectory,
                    safeSession + "-" + Guid.NewGuid().ToString("N") + ".pending-export.json");
            }

            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                           temporary,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read,
                           4096,
                           FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                File.Move(temporary, path);
                return path;
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static bool TryFindPendingPayload(out string path)
        {
            path = null;
            var directory = MotionTakeRecovery.RecoveryDirectory;
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var candidates = Directory.GetFiles(
                directory,
                "*.pending-export.json",
                SearchOption.TopDirectoryOnly);
            if (candidates.Length == 0)
            {
                return false;
            }

            Array.Sort(
                candidates,
                (left, right) => File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
            path = candidates[0];
            return true;
        }

        private static void ArchiveRecoveryPayload(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return;
            }

            var root = Path.GetFullPath(MotionTakeRecovery.RecoveryDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Pending export path escaped the Recovery directory.");
            }

            var completedDirectory = Path.Combine(root, "Completed");
            Directory.CreateDirectory(completedDirectory);
            var destination = Path.Combine(completedDirectory, Path.GetFileName(path));
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    completedDirectory,
                    Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json");
            }

            File.Move(path, destination);
        }

        [Serializable]
        private sealed class PendingExportPayload
        {
            public string takeAssetPath;
            public string sessionId;
            public string sourceGlobalObjectId;
            public string takeName;
            public float frameRate = 60f;
            public MotionPoseCorrectionTrack correctionTrack = new MotionPoseCorrectionTrack();
            public List<PendingValidationIssue> validation = new List<PendingValidationIssue>();
            public List<HumanoidCaptureFrame> correctedFrames = new List<HumanoidCaptureFrame>();
        }

        [Serializable]
        private sealed class PendingValidationIssue
        {
            public MotionTakeValidationKind kind;
            public MotionTakeValidationSeverity severity;
            public int frame;
            public int endFrame;
            public string message;
        }

        private sealed class BufferedClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<MotionTakeClipSample> _samples;

            public BufferedClipSource(IReadOnlyList<MotionTakeClipSample> samples, float frameRate)
            {
                _samples = samples;
                FrameRate = frameRate;
            }

            public int SampleCount => _samples.Count;
            public float FrameRate { get; }

            public bool TryGetSample(int index, out MotionTakeClipSample sample)
            {
                if (index < 0 || index >= _samples.Count)
                {
                    sample = default;
                    return false;
                }

                sample = _samples[index];
                return true;
            }
        }

        private sealed class CapturedClipSource : IMotionTakeClipSource
        {
            private readonly IReadOnlyList<HumanoidCaptureFrame> frames;

            public CapturedClipSource(IReadOnlyList<HumanoidCaptureFrame> frames, float frameRate)
            {
                this.frames = frames;
                FrameRate = Mathf.Max(1f, frameRate);
            }

            public int SampleCount => frames.Count;
            public float FrameRate { get; }

            public bool TryGetSample(int index, out MotionTakeClipSample sample)
            {
                if (index < 0 || index >= frames.Count || frames[index] == null)
                {
                    sample = default(MotionTakeClipSample);
                    return false;
                }

                var frame = frames[index];
                sample = new MotionTakeClipSample
                {
                    TimeSeconds = (float)frame.time,
                    BodyPosition = frame.bodyPosition,
                    BodyRotation = frame.bodyRotation,
                    Muscles = frame.muscles
                };
                return true;
            }
        }
    }
}

using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    internal sealed class ReviewRecoveryState
    {
        public ReviewRecoveryState(CaptureTake capture, MotionEditRecipe recipe, int currentFrame)
        {
            Capture = capture;
            Recipe = recipe;
            CurrentFrame = currentFrame;
        }

        public CaptureTake Capture { get; }
        public MotionEditRecipe Recipe { get; }
        public int CurrentFrame { get; }
    }

    /// <summary>
    /// Crash- and assembly-reload-safe value snapshot for Play Mode review. It intentionally stores no scene object
    /// references; the coordinator rebinds the processed Humanoid after reload.
    /// </summary>
    internal static class ReviewRecoveryCheckpoint
    {
        private const int CurrentFormatVersion = 1;

        public static string Save(CaptureTake capture, MotionEditRecipe recipe)
        {
            return Save(capture, recipe, 0);
        }

        public static string Save(CaptureTake capture, MotionEditRecipe recipe, int currentFrame)
        {
            if (capture == null)
            {
                throw new ArgumentNullException(nameof(capture));
            }

            Directory.CreateDirectory(MotionTakeRecovery.RecoveryDirectory);
            var session = string.IsNullOrWhiteSpace(capture.sessionId)
                ? Guid.NewGuid().ToString("N")
                : VersionedAssetPath.SanitizeFileName(capture.sessionId);
            var path = Path.Combine(
                MotionTakeRecovery.RecoveryDirectory,
                session + ".review-checkpoint.json");
            var payload = new ReviewRecoveryPayload
            {
                formatVersion = CurrentFormatVersion,
                capture = capture,
                recipeDisplayName = recipe?.DisplayName,
                correctionTrack = recipe?.CorrectionTrack ?? new MotionPoseCorrectionTrack(),
                currentFrame = Mathf.Max(0, currentFrame)
            };
            WriteAtomically(path, JsonUtility.ToJson(payload));
            return path;
        }

        public static ReviewRecoveryState TryRestore(string path)
        {
            if (!IsInsideRecoveryDirectory(path) || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var payload = JsonUtility.FromJson<ReviewRecoveryPayload>(File.ReadAllText(path));
                if (payload == null || payload.formatVersion != CurrentFormatVersion || payload.capture == null)
                {
                    return null;
                }

                var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
                recipe.name = string.IsNullOrWhiteSpace(payload.recipeDisplayName)
                    ? (payload.capture.displayName ?? "Motion Take") + " Corrections"
                    : payload.recipeDisplayName;
                recipe.hideFlags = HideFlags.HideAndDontSave;
                recipe.Initialize(null, recipe.name);
                if (payload.correctionTrack != null)
                {
                    foreach (var key in payload.correctionTrack.Keys)
                    {
                        if (key != null)
                        {
                            recipe.CorrectionTrack.AddOrReplaceKey(key);
                        }
                    }
                }

                return new ReviewRecoveryState(payload.capture, recipe, payload.currentFrame);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Motion Take Studio could not restore Review checkpoint: " + exception.Message);
                return null;
            }
        }

        public static void Archive(string path)
        {
            if (!IsInsideRecoveryDirectory(path) || !File.Exists(path))
            {
                return;
            }

            var directory = Path.Combine(MotionTakeRecovery.RecoveryDirectory, "Completed");
            Directory.CreateDirectory(directory);
            var destination = Path.Combine(directory, Path.GetFileName(path));
            if (File.Exists(destination))
            {
                destination = Path.Combine(
                    directory,
                    Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".json");
            }

            File.Move(path, destination);
        }

        private static void WriteAtomically(string path, string json)
        {
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var backup = path + ".bak-" + Guid.NewGuid().ToString("N");
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
                    writer.Write(json ?? "{}");
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    File.Replace(temporary, path, backup, true);
                    File.Delete(backup);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }

                if (File.Exists(backup))
                {
                    File.Delete(backup);
                }
            }
        }

        private static bool IsInsideRecoveryDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var root = Path.GetFullPath(MotionTakeRecovery.RecoveryDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        [Serializable]
        private sealed class ReviewRecoveryPayload
        {
            public int formatVersion;
            public CaptureTake capture;
            public string recipeDisplayName;
            public MotionPoseCorrectionTrack correctionTrack = new MotionPoseCorrectionTrack();
            public int currentFrame;
        }
    }
}

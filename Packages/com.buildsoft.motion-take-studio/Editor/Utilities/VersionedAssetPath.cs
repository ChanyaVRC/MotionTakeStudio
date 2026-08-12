using System;
using System.IO;
using UnityEditor;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>Creates normalized project paths without overwriting an existing asset.</summary>
    internal static class VersionedAssetPath
    {
        public static string EnsureAssetFolder(string assetFolder)
        {
            if (string.IsNullOrWhiteSpace(assetFolder))
            {
                throw new ArgumentException("An Assets-relative output folder is required.", nameof(assetFolder));
            }

            assetFolder = assetFolder.Replace('\\', '/').TrimEnd('/');
            if (assetFolder != "Assets" && !assetFolder.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException("Output folders must be under Assets.", nameof(assetFolder));
            }

            if (AssetDatabase.IsValidFolder(assetFolder))
            {
                return assetFolder;
            }

            var segments = assetFolder.Split('/');
            var current = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var next = current + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, segments[i]);
                }

                current = next;
            }

            return assetFolder;
        }

        public static string Next(string folder, string takeName, string stage, string extension)
        {
            folder = EnsureAssetFolder(folder);
            takeName = SanitizeFileName(takeName);
            extension = extension.TrimStart('.');

            for (var version = 1; version < 10000; version++)
            {
                var candidate = $"{folder}/{takeName}_{stage}_v{version:00}.{extension}";
                if (AssetDatabase.LoadMainAssetAtPath(candidate) == null && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("Could not allocate an unused asset version.");
        }

        public static string Unique(string folder, string fileName, string extension)
        {
            folder = EnsureAssetFolder(folder);
            fileName = SanitizeFileName(fileName);
            var desired = $"{folder}/{fileName}.{extension.TrimStart('.')}";
            return AssetDatabase.GenerateUniqueAssetPath(desired);
        }

        public static string SanitizeFileName(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "MotionTake" : value.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }
    }
}

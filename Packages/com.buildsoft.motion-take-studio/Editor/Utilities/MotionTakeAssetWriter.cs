using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    /// <summary>Atomically creates a unique .mttake source asset.</summary>
    internal static class MotionTakeAssetWriter
    {
        public static string WriteUnique(string folder, string takeName, string json)
        {
            var assetPath = VersionedAssetPath.Unique(folder, takeName, "mttake");
            var projectRoot = Directory.GetParent(Application.dataPath)?.FullName
                ?? throw new InvalidOperationException("Could not resolve the Unity project root.");
            var absolutePath = Path.GetFullPath(Path.Combine(projectRoot, assetPath));
            var absoluteAssets = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
            if (!absolutePath.StartsWith(absoluteAssets, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Take assets must be written under this project's Assets folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)
                ?? throw new InvalidOperationException("Take output folder is invalid."));
            var temporaryPath = absolutePath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporaryPath, json ?? "{}", new UTF8Encoding(false));
                File.Move(temporaryPath, absolutePath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return assetPath;
        }
    }
}

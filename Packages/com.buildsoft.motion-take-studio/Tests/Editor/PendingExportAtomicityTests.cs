using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    /// <summary>Executable red tests for pending-export rollback and retry atomicity.</summary>
    public sealed class PendingExportAtomicityTests
    {
        private const string PendingKey = "BuildSoft.MotionTakeStudio.Export.Pending";
        private const string PayloadKey = "BuildSoft.MotionTakeStudio.Export.Payload";
        private const string PayloadPathKey = "BuildSoft.MotionTakeStudio.Export.PayloadPath";

        private static readonly string[] SharedOutputFolders =
        {
            "Assets/MotionTakeStudio/Clips",
            "Assets/MotionTakeStudio/Takes",
            "Assets/MotionTakeStudio"
        };

        private readonly HashSet<string> cleanupAssets = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> cleanupFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> cleanupFolders = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> cleanupTakeNames = new HashSet<string>(StringComparer.Ordinal);

        [SetUp]
        public void SetUp()
        {
            cleanupAssets.Clear();
            cleanupFiles.Clear();
            cleanupFolders.Clear();
            cleanupTakeNames.Clear();

            foreach (var folder in SharedOutputFolders)
            {
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    cleanupFolders.Add(folder);
                }
            }
        }

        [TearDown]
        public void TearDown()
        {
            SessionState.SetBool(PendingKey, false);
            SessionState.EraseString(PayloadKey);
            SessionState.EraseString(PayloadPathKey);

            foreach (var takeName in cleanupTakeNames)
            {
                RegisterGeneratedAssets(takeName);
            }

            foreach (var assetPath in cleanupAssets.OrderByDescending(path => path.Length))
            {
                AssetDatabase.DeleteAsset(assetPath);
            }

            foreach (var filePath in cleanupFiles)
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }

            AssetDatabase.Refresh();
            DeleteEmptyFoldersCreatedByTest();
            AssetDatabase.Refresh();

            cleanupAssets.Clear();
            cleanupFiles.Clear();
            cleanupFolders.Clear();
            cleanupTakeNames.Clear();
        }

        [Test]
        public void ArchiveFailure_RollsBackEveryClip_AndKeepsPendingPayloadRetryable()
        {
            var scenario = StageScenario();
            bool finalized;
            string error;
            using (new FileStream(scenario.PayloadPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                finalized = PendingCaptureExport.TryFinalize(out _, out error);
            }

            RegisterGeneratedAssets(scenario.TakeName);
            var generatedClips = FindGeneratedClips(scenario.TakeName);
            Assert.That(finalized, Is.False, "The locked payload must force the archive/commit failure path.");
            Assert.That(error, Does.Contain("retryable").IgnoreCase);
            Assert.That(generatedClips, Is.Empty,
                "A failed export transaction must roll back auto, corrected, and manual clips.");
            Assert.That(SessionState.GetBool(PendingKey, false), Is.True,
                "Pending state must remain set until payload archival commits.");
            Assert.That(SessionState.GetString(PayloadPathKey, string.Empty),
                Is.EqualTo(scenario.PayloadPath));
        }

        [Test]
        public void RetryAfterArchiveFailure_CreatesExactlyOneClipSet()
        {
            var scenario = StageScenario();
            using (new FileStream(scenario.PayloadPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.That(PendingCaptureExport.TryFinalize(out _, out _), Is.False);
            }

            Assert.That(PendingCaptureExport.TryFinalize(out _, out var retryError), Is.True, retryError);
            RegisterGeneratedAssets(scenario.TakeName);
            Assert.That(FindGeneratedClips(scenario.TakeName), Has.Length.EqualTo(3),
                "Retry must not leave the first failed attempt's three orphan clips beside the committed set.");

            var foldersCreatedByTest = cleanupFolders.ToArray();
            TearDown();
            foreach (var folder in foldersCreatedByTest)
            {
                Assert.That(AssetDatabase.IsValidFolder(folder), Is.False,
                    $"A clean project must not retain the test-created output folder {folder}.");
            }
        }

        private void DeleteEmptyFoldersCreatedByTest()
        {
            foreach (var folder in cleanupFolders.OrderByDescending(path => path.Length))
            {
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    continue;
                }

                var prefix = folder + "/";
                var hasChildren = AssetDatabase.GetAllAssetPaths()
                    .Any(path => path.StartsWith(prefix, StringComparison.Ordinal));
                if (!hasChildren)
                {
                    AssetDatabase.DeleteAsset(folder);
                }
            }
        }

        private Scenario StageScenario()
        {
            var takeName = "Pending Atomic " + Guid.NewGuid().ToString("N");
            cleanupTakeNames.Add(takeName);
            var capture = new CaptureTake
            {
                sessionId = Guid.NewGuid().ToString("N"),
                displayName = takeName,
                sourceName = "Atomicity Test Avatar",
                createdUtc = DateTime.UtcNow.ToString("O"),
                sampleRate = 60f,
                humanScale = 1f,
                frames = new List<HumanoidCaptureFrame>
                {
                    new HumanoidCaptureFrame
                    {
                        time = 0d,
                        bodyRotation = Quaternion.identity,
                        sourceBodyRotation = Quaternion.identity,
                        muscles = new float[HumanTrait.MuscleCount],
                        sourceMuscles = new float[HumanTrait.MuscleCount]
                    }
                }
            };

            var takePath = MotionTakeAssetWriter.WriteUnique(
                "Assets/MotionTakeStudio/Takes",
                takeName,
                JsonUtility.ToJson(capture, true));
            cleanupAssets.Add(takePath);
            PendingCaptureExport.Stage(
                takePath,
                null,
                Array.Empty<MotionTakeValidationIssue>(),
                capture,
                capture.frames);

            var payloadPath = SessionState.GetString(PayloadPathKey, string.Empty);
            Assert.That(payloadPath, Is.Not.Empty);
            cleanupFiles.Add(payloadPath);
            cleanupFiles.Add(Path.Combine(
                MotionTakeRecovery.RecoveryDirectory,
                "Completed",
                Path.GetFileName(payloadPath)));
            return new Scenario(takeName, payloadPath);
        }

        private void RegisterGeneratedAssets(string takeName)
        {
            foreach (var path in AssetDatabase.GetAllAssetPaths()
                         .Where(path => path.StartsWith("Assets/MotionTakeStudio/", StringComparison.Ordinal) &&
                                        Path.GetFileName(path).StartsWith(takeName, StringComparison.Ordinal)))
            {
                cleanupAssets.Add(path);
            }
        }

        private static string[] FindGeneratedClips(string takeName)
        {
            return AssetDatabase.GetAllAssetPaths()
                .Where(path => path.StartsWith("Assets/MotionTakeStudio/Clips/", StringComparison.Ordinal) &&
                               path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                               Path.GetFileName(path).StartsWith(takeName, StringComparison.Ordinal))
                .ToArray();
        }

        private readonly struct Scenario
        {
            public Scenario(string takeName, string payloadPath)
            {
                TakeName = takeName;
                PayloadPath = payloadPath;
            }

            public string TakeName { get; }
            public string PayloadPath { get; }
        }
    }
}

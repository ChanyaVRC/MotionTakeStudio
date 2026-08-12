using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor.Tests
{
    /// <summary>Red contracts for review/recovery persistence.</summary>
    public sealed class RecoveryPersistenceRegressionTests
    {
        private readonly List<string> cleanupPaths = new List<string>();

        [TearDown]
        public void TearDown()
        {
            const string checkpointKey = "BuildSoft.MotionTakeStudio.Capture.ReviewCheckpointPath";
            var checkpoint = SessionState.GetString(checkpointKey, string.Empty);
            if (!string.IsNullOrEmpty(checkpoint) && File.Exists(checkpoint))
            {
                File.Delete(checkpoint);
            }
            SessionState.EraseString(checkpointKey);

            foreach (var path in cleanupPaths)
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            cleanupPaths.Clear();
        }

        [Test]
        public void ReviewCheckpoint_RoundTripsRecipeBeforeAssemblyReload()
        {
            var take = CreateTake();
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            try
            {
                recipe.Initialize(null, "Reload-safe review");
                var key = recipe.CorrectionTrack.GetOrCreateKey(42, 12);
                key.SetTargetOffset(MotionPoseTargetOffset.CreatePosition(
                    PoseTarget.LeftElbowHint,
                    new Vector3(0f, 0.1f, 0f)));

                var checkpointType = RequireType("BuildSoft.MotionTakeStudio.Editor.ReviewRecoveryCheckpoint");
                var save = RequireStaticMethod(
                    checkpointType,
                    "Save",
                    typeof(CaptureTake),
                    typeof(MotionEditRecipe));
                var restore = RequireStaticMethod(checkpointType, "TryRestore", typeof(string));

                var path = (string)save.Invoke(null, new object[] { take, recipe });
                cleanupPaths.Add(path);
                var restored = restore.Invoke(null, new object[] { path });
                Assert.That(restored, Is.Not.Null);

                var restoredRecipe = ReadMember<MotionEditRecipe>(restored, "Recipe");
                Assert.That(restoredRecipe, Is.Not.Null);
                Assert.That(restoredRecipe.CorrectionTrack.Keys, Has.Count.EqualTo(1));
                Assert.That(restoredRecipe.CorrectionTrack.Keys[0].Frame, Is.EqualTo(42));
                Assert.That(
                    restoredRecipe.CorrectionTrack.Keys[0]
                        .TryGetTargetOffset(PoseTarget.LeftElbowHint, out var restoredOffset),
                    Is.True);
                Assert.That(restoredOffset.PositionOffsetNormalized.y, Is.EqualTo(0.1f).Within(1e-6f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void SaveAndExit_WhenTakeWriteFails_RemainsReviewingAndKeepsRecipeRetryable()
        {
            var coordinator = MotionCaptureCoordinator.Instance;
            var phaseField = RequireField(typeof(MotionCaptureCoordinator), "_phase");
            var takeField = RequireField(typeof(MotionCaptureCoordinator), "_take");
            var recipeField = RequireField(typeof(MotionCaptureCoordinator), "_activeRecipe");
            var previousPhase = phaseField.GetValue(coordinator);
            var previousTake = takeField.GetValue(coordinator);
            var previousRecipe = recipeField.GetValue(coordinator);
            var recipe = ScriptableObject.CreateInstance<MotionEditRecipe>();
            var take = CreateTake();
            take.frames.Add(Frame(0d, true, 0f));

            try
            {
                recipe.Initialize(null, "Retryable recipe");
                recipe.CorrectionTrack.GetOrCreateKey(7).SetTargetOffset(
                    MotionPoseTargetOffset.CreatePosition(PoseTarget.LeftElbowHint, Vector3.up * 0.2f));
                phaseField.SetValue(coordinator, MotionTakeSessionPhase.Reviewing);
                takeField.SetValue(coordinator, take);
                recipeField.SetValue(coordinator, recipe);

                MotionCaptureCoordinator.SetTakeWriterForTests((_, __, ___) =>
                    throw new IOException("synthetic write failure"));

                Assert.DoesNotThrow(coordinator.SaveAndExit);
                Assert.That(coordinator.Phase, Is.EqualTo(MotionTakeSessionPhase.Reviewing));
                Assert.That(coordinator.ActiveCapture, Is.SameAs(take));
                Assert.That(coordinator.ActiveRecipe, Is.SameAs(recipe));
                Assert.That(coordinator.ActiveRecipe.CorrectionTrack.Keys, Has.Count.EqualTo(1));
                Assert.That(coordinator.StatusMessage, Does.Contain("retry").IgnoreCase);
            }
            finally
            {
                MotionCaptureCoordinator.SetTakeWriterForTests(null);
                phaseField.SetValue(coordinator, previousPhase);
                takeField.SetValue(coordinator, previousTake);
                recipeField.SetValue(coordinator, previousRecipe);
                UnityEngine.Object.DestroyImmediate(recipe);
            }
        }

        [Test]
        public void CompletedJournal_LoadsPostRepairFramesAndGapWarnings()
        {
            var take = CreateTake();
            take.frames = new List<HumanoidCaptureFrame>
            {
                Frame(0d, true, 0f),
                Frame(0.05d, false, 0f),
                Frame(0.1d, true, 1f)
            };

            string path;
            using (var journal = new RecoveryJournal(take))
            {
                path = journal.Path;
                cleanupPaths.Add(path);
                foreach (var frame in take.frames)
                {
                    journal.Append(frame, frame.time);
                }

                take.gapWarnings = TrackerGapInterpolator.Repair(take.frames);
                take.gapWarnings.Add(new TrackerGapWarning
                {
                    role = TrackerRole.Head,
                    startTime = 0.05d,
                    duration = 0.05d,
                    message = "synthetic post-repair warning"
                });
                var complete = RequireInstanceMethod(
                    typeof(RecoveryJournal),
                    "Complete",
                    typeof(CaptureTake),
                    typeof(double));
                complete.Invoke(journal, new object[] { take, 0.1d });
            }

            Assert.That(MotionTakeRecovery.TryLoad(path, out var recovered, out var entry), Is.True);
            Assert.That(entry.wasCompleted, Is.True);
            Assert.That(recovered.frames[1].trackers.Find(TrackerRole.Head).valid, Is.True,
                "The completion checkpoint must replace live envelopes with repaired tracker data.");
            Assert.That(recovered.frames[1].trackers.Find(TrackerRole.Head).interpolated, Is.True);
            Assert.That(recovered.frames[1].trackers.Find(TrackerRole.Head).position.x,
                Is.EqualTo(0.5f).Within(1e-6f));
            Assert.That(recovered.gapWarnings.Select(warning => warning.message),
                Does.Contain("synthetic post-repair warning"));
        }

        private static CaptureTake CreateTake()
        {
            return new CaptureTake
            {
                sessionId = Guid.NewGuid().ToString("N"),
                displayName = "Recovery Regression",
                sourceName = "Recovery Avatar",
                sourceGlobalObjectId = string.Empty,
                createdUtc = DateTime.UtcNow.ToString("O"),
                sampleRate = 60f,
                humanScale = 1f
            };
        }

        private static HumanoidCaptureFrame Frame(double time, bool valid, float x)
        {
            return new HumanoidCaptureFrame
            {
                time = time,
                bodyRotation = Quaternion.identity,
                sourceBodyRotation = Quaternion.identity,
                muscles = new float[HumanTrait.MuscleCount],
                sourceMuscles = new float[HumanTrait.MuscleCount],
                trackers = new TrackerFrame
                {
                    time = time,
                    poses = new List<TrackerPoseSample>
                    {
                        new TrackerPoseSample
                        {
                            role = TrackerRole.Head,
                            deviceId = "head",
                            connected = true,
                            valid = valid,
                            position = new Vector3(x, 0f, 0f),
                            rotation = Quaternion.identity
                        }
                    }
                }
            };
        }

        private static Type RequireType(string fullName)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(fullName, false))
                .FirstOrDefault(candidate => candidate != null);
            if (type == null)
            {
                Assert.Fail(
                    $"Missing regression seam {fullName}. Review pose keys need a durable checkpoint before assembly reload.");
            }

            return type;
        }

        private static MethodInfo RequireStaticMethod(Type type, string name, params Type[] parameterTypes)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameterTypes,
                null);
            if (method == null)
            {
                Assert.Fail($"Missing regression seam {type.FullName}.{name}.");
            }

            return method;
        }

        private static MethodInfo RequireInstanceMethod(Type type, string name, params Type[] parameterTypes)
        {
            var method = type.GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                parameterTypes,
                null);
            if (method == null)
            {
                Assert.Fail(
                    $"Missing regression seam {type.FullName}.{name}(CaptureTake, double). " +
                    "Completing a journal must checkpoint repaired frames and gap warnings.");
            }

            return method;
        }

        private static FieldInfo RequireField(Type type, string name)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Assert.Fail($"Missing test field {type.FullName}.{name}.");
            }

            return field;
        }

        private static T ReadMember<T>(object instance, string name)
        {
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                return (T)property.GetValue(instance);
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                return (T)field.GetValue(instance);
            }

            Assert.Fail($"Restored review state is missing {name}.");
            return default(T);
        }
    }
}

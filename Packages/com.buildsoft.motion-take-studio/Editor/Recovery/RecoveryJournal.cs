using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace BuildSoft.MotionTakeStudio.Editor
{
    [Serializable]
    public sealed class RecoveryEntry
    {
        public string path;
        public string sessionId;
        public string sourceName;
        public string createdUtc;
        public bool wasCompleted;
        public int recoveredFrameCount;
        public string warning;
    }

    /// <summary>Reads append-only recovery journals. A truncated final line is ignored after an editor crash.</summary>
    public static class MotionTakeRecovery
    {
        public static string RecoveryDirectory
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
                if (string.IsNullOrEmpty(projectRoot))
                {
                    throw new InvalidOperationException("Could not resolve the Unity project root.");
                }

                return Path.Combine(projectRoot, "Library", "MotionTakeStudio", "Recovery");
            }
        }

        public static IReadOnlyList<RecoveryEntry> FindAll()
        {
            var results = new List<RecoveryEntry>();
            if (!Directory.Exists(RecoveryDirectory))
            {
                return results;
            }

            foreach (var path in Directory.GetFiles(RecoveryDirectory, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                if (TryLoad(path, out var take, out var entry))
                {
                    entry.recoveredFrameCount = take.frames.Count;
                }

                results.Add(entry);
            }

            results.Sort((left, right) => string.CompareOrdinal(right.createdUtc, left.createdUtc));
            return results;
        }

        public static bool TryLoad(string path, out CaptureTake take, out RecoveryEntry entry)
        {
            take = new CaptureTake();
            entry = new RecoveryEntry { path = path };
            if (!IsInsideRecoveryDirectory(path) || !File.Exists(path))
            {
                entry.warning = "The recovery journal does not exist or is outside the recovery directory.";
                return false;
            }

            var foundHeader = false;
            var lineNumber = 0;
            try
            {
                using (var reader = new StreamReader(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        RecoveryEnvelope envelope;
                        try
                        {
                            envelope = JsonUtility.FromJson<RecoveryEnvelope>(line);
                        }
                        catch (ArgumentException)
                        {
                            entry.warning = $"Ignored a truncated recovery record at line {lineNumber}.";
                            break;
                        }

                        if (envelope == null)
                        {
                            continue;
                        }

                        switch (envelope.kind)
                        {
                            case "header" when envelope.header != null:
                                foundHeader = true;
                                take.sessionId = envelope.header.sessionId;
                                take.displayName = envelope.header.displayName;
                                take.sourceGlobalObjectId = envelope.header.sourceGlobalObjectId;
                                take.sourceName = envelope.header.sourceName;
                                take.createdUtc = envelope.header.createdUtc;
                                take.sampleRate = envelope.header.sampleRate;
                                take.humanScale = envelope.header.humanScale > 0f
                                    ? envelope.header.humanScale
                                    : 1f;
                                entry.sessionId = take.sessionId;
                                entry.sourceName = take.sourceName;
                                entry.createdUtc = take.createdUtc;
                                break;
                            case "frame" when envelope.frame != null:
                                take.frames.Add(envelope.frame);
                                break;
                            case "snapshot" when envelope.snapshot != null:
                                take = envelope.snapshot;
                                entry.sessionId = take.sessionId;
                                entry.sourceName = take.sourceName;
                                entry.createdUtc = take.createdUtc;
                                break;
                            case "footer":
                                entry.wasCompleted = true;
                                break;
                        }
                    }
                }

                entry.recoveredFrameCount = take.frames.Count;
                if (!foundHeader)
                {
                    entry.warning = "The recovery journal has no readable header.";
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                entry.warning = "Could not read the recovery journal: " + exception.Message;
                return false;
            }
        }

        public static bool Archive(string path, out string archivedPath, out string error)
        {
            archivedPath = null;
            error = null;
            if (!IsInsideRecoveryDirectory(path) || !File.Exists(path))
            {
                error = "The recovery journal does not exist or is outside the recovery directory.";
                return false;
            }

            try
            {
                var archiveDirectory = Path.Combine(RecoveryDirectory, "Archived");
                Directory.CreateDirectory(archiveDirectory);
                archivedPath = Path.Combine(archiveDirectory, Path.GetFileName(path));
                if (File.Exists(archivedPath))
                {
                    archivedPath = Path.Combine(
                        archiveDirectory,
                        Path.GetFileNameWithoutExtension(path) + "-" + Guid.NewGuid().ToString("N") + ".jsonl");
                }

                File.Move(path, archivedPath);
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return false;
            }
        }

        private static bool IsInsideRecoveryDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var root = Path.GetFullPath(RecoveryDirectory).TrimEnd(Path.DirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(path);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class RecoveryJournal : IDisposable
    {
        private const double FlushIntervalSeconds = 0.5d;

        private FileStream _stream;
        private StreamWriter _writer;
        private double _lastFlushTime;
        private bool _completed;

        public RecoveryJournal(CaptureTake take)
        {
            if (take == null)
            {
                throw new ArgumentNullException(nameof(take));
            }

            Directory.CreateDirectory(MotionTakeRecovery.RecoveryDirectory);
            var safeName = Sanitize(take.sourceName);
            var fileName = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{safeName}-{take.sessionId}.jsonl";
            Path = System.IO.Path.Combine(MotionTakeRecovery.RecoveryDirectory, fileName);
            var temporaryPath = Path + ".tmp";

            var header = new RecoveryEnvelope
            {
                kind = "header",
                header = new RecoveryHeader
                {
                    formatVersion = 1,
                    sessionId = take.sessionId,
                    displayName = take.displayName,
                    sourceGlobalObjectId = take.sourceGlobalObjectId,
                    sourceName = take.sourceName,
                    createdUtc = take.createdUtc,
                    sampleRate = take.sampleRate,
                    humanScale = take.humanScale
                }
            };

            using (var temporaryStream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.Read))
            using (var temporaryWriter = new StreamWriter(temporaryStream))
            {
                temporaryWriter.WriteLine(JsonUtility.ToJson(header));
                temporaryWriter.Flush();
                temporaryStream.Flush(true);
            }

            File.Move(temporaryPath, Path);
            _stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(_stream);
        }

        public string Path { get; }

        public void Append(HumanoidCaptureFrame frame, double realtime)
        {
            if (_writer == null || _completed)
            {
                throw new InvalidOperationException("The recovery journal is closed.");
            }

            _writer.WriteLine(JsonUtility.ToJson(new RecoveryEnvelope { kind = "frame", frame = frame }));
            if (realtime - _lastFlushTime >= FlushIntervalSeconds)
            {
                Flush(realtime);
            }
        }

        public void Complete(int frameCount, double duration, double realtime)
        {
            if (_writer == null || _completed)
            {
                return;
            }

            _writer.WriteLine(JsonUtility.ToJson(new RecoveryEnvelope
            {
                kind = "footer",
                footer = new RecoveryFooter
                {
                    completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    frameCount = frameCount,
                    duration = duration
                }
            }));
            _completed = true;
            Flush(realtime);
        }

        /// <summary>
        /// Completes the journal with the post-repair take. The snapshot is authoritative over the append-only
        /// live frames, so recovered data contains repaired tracker poses and the warnings produced by repair.
        /// </summary>
        public void Complete(CaptureTake take, double realtime)
        {
            if (take == null)
            {
                throw new ArgumentNullException(nameof(take));
            }

            if (_writer == null || _completed)
            {
                return;
            }

            _writer.WriteLine(JsonUtility.ToJson(new RecoveryEnvelope
            {
                kind = "snapshot",
                snapshot = take
            }));
            Complete(take.frames?.Count ?? 0, take.Duration, realtime);
        }

        public void Dispose()
        {
            if (_writer == null)
            {
                return;
            }

            try
            {
                _writer.Flush();
                _stream?.Flush(true);
            }
            finally
            {
                _writer.Dispose();
                _stream?.Dispose();
                _writer = null;
                _stream = null;
            }
        }

        private void Flush(double realtime)
        {
            _writer.Flush();
            _stream.Flush(true);
            _lastFlushTime = realtime;
        }

        private static string Sanitize(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? "avatar" : value;
            foreach (var invalid in System.IO.Path.GetInvalidFileNameChars())
            {
                value = value.Replace(invalid, '_');
            }

            return value;
        }
    }

    [Serializable]
    internal sealed class RecoveryEnvelope
    {
        public string kind;
        public RecoveryHeader header;
        public HumanoidCaptureFrame frame;
        public CaptureTake snapshot;
        public RecoveryFooter footer;
    }

    [Serializable]
    internal sealed class RecoveryHeader
    {
        public int formatVersion;
        public string sessionId;
        public string displayName;
        public string sourceGlobalObjectId;
        public string sourceName;
        public string createdUtc;
        public float sampleRate;
        public float humanScale = 1f;
    }

    [Serializable]
    internal sealed class RecoveryFooter
    {
        public string completedUtc;
        public int frameCount;
        public double duration;
    }
}

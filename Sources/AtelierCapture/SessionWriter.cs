using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtelierCapture;

internal sealed class SessionWriter : IDisposable
{
    internal const long QueueBudget = 64 * 1024 * 1024;
    private readonly BlockingCollection<Entry> queue = new(2048);
    private readonly Source[] sources = { new("Official"), new("Private") };
    private static readonly string[] FileNames = { "api.jsonl.gz", "combat.jsonl.gz", "raw-bodies.jsonl.gz", "diagnostics.jsonl.gz" };
    private static readonly string[] StreamNames = { "api", "combat", "raw", "diagnostics" };
    private readonly string root;
    private readonly object metadata;
    private readonly Func<bool, object> status;
    private readonly Action<string> report;
    private readonly Thread worker;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly string runId = Guid.NewGuid().ToString("N");
    private readonly string date = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
    private long sequence, queuedBytes;
    private int stopping, failed;

    internal SessionWriter(string root, object metadata, Func<bool, object> status, Action<string> report)
    {
        this.root = root;
        this.metadata = metadata;
        this.status = status;
        this.report = report;
        worker = new Thread(WriteLoop) { IsBackground = true, Name = "Atelier capture writer", Priority = ThreadPriority.BelowNormal };
        worker.Start();
        new Thread(() =>
        {
            try { RecoverArchives(); }
            catch (Exception ex) { report($"Capture recovery stopped: {ex.GetType().Name}. Partial files were retained."); }
        }) { IsBackground = true, Name = "Atelier capture recovery", Priority = ThreadPriority.BelowNormal }.Start();
    }

    internal void Emit(bool isPrivate, bool combat, string kind, object? data, long retainedBytes = 0)
    {
        if (Volatile.Read(ref stopping) != 0 || Volatile.Read(ref failed) != 0) return;
        var source = sources[isPrivate ? 1 : 0];
        var stream = combat ? 1 : kind == "raw_body" ? 2 : kind is "request" or "response" ? 0 : 3;
        object record = new
        {
            schema = 4, run_id = runId, sequence = Interlocked.Increment(ref sequence),
            utc = DateTimeOffset.UtcNow, elapsed_ticks = Stopwatch.GetTimestamp() - started,
            thread = Environment.CurrentManagedThreadId, environment = source.Name,
            stream = StreamNames[stream], kind, data
        };
        // Network events are small and supply no graph estimate. Combat is already detached and measured.
        if (retainedBytes == 0)
        {
            var line = JsonSerializer.Serialize(record);
            record = line;
            retainedBytes = line.Length * 2L + 256;
        }
        var size = Math.Max(4096, retainedBytes);
        if (Interlocked.Add(ref queuedBytes, size) > QueueBudget)
        {
            Interlocked.Add(ref queuedBytes, -size);
            Interlocked.Increment(ref source.Dropped);
            return;
        }
        try
        {
            if (queue.TryAdd(new Entry(source, stream, record, size))) return;
        }
        catch (InvalidOperationException) { }
        Interlocked.Add(ref queuedBytes, -size);
        Interlocked.Increment(ref source.Dropped);
    }

    private void WriteLoop()
    {
        try
        {
            var lastFlush = Stopwatch.GetTimestamp();
            while (!queue.IsCompleted)
            {
                if (queue.TryTake(out var entry, 250))
                {
                    EnsureFiles(entry.Source);
                    ReportDrops(entry.Source);
                    entry.Source.Logs![entry.Stream].WriteLine(
                        entry.Record as string ?? JsonSerializer.Serialize(entry.Record));
                    Interlocked.Add(ref queuedBytes, -entry.Bytes);
                    entry.Source.Events++;
                }
                if ((Stopwatch.GetTimestamp() - lastFlush) * 1000.0 / Stopwatch.Frequency >= 1000)
                {
                    Flush();
                    lastFlush = Stopwatch.GetTimestamp();
                }
            }
            foreach (var source in sources.Where(s => s.Logs is not null || s.Dropped != 0))
            {
                EnsureFiles(source);
                ReportDrops(source);
                WriteMetadata(source, new { kind = "capture_status", utc = DateTimeOffset.UtcNow, data = status(source.Name == "Private") });
                WriteMetadata(source, new { kind = "session_end", run_id = runId, utc = DateTimeOffset.UtcNow, events = source.Events });
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failed, 1);
            report($"Capture output stopped: {ex.GetType().Name}. The game continues; this run is incomplete.");
        }
        finally
        {
            foreach (var source in sources)
            {
                var packed = false;
                try
                {
                    if (source.Logs is not null)
                    {
                        Exception? closeError = null;
                        foreach (var log in source.Logs)
                        {
                            try { log?.Dispose(); }
                            catch (Exception ex) { closeError ??= ex; }
                        }
                        if (closeError is not null) throw closeError;
                        SaveCheckpoint(source);
                        packed = CaptureArchive.Pack(source.Folder!, Volatile.Read(ref failed) != 0);
                    }
                }
                catch (Exception ex) { report($"Capture file could not close: {ex.GetType().Name}. This run is incomplete."); }
                finally { source.Lease?.Dispose(); }
                if (packed) CleanPackedFolder(source.Folder!);
            }
            while (queue.TryTake(out _)) { }
        }
    }

    private void EnsureFiles(Source source)
    {
        if (source.Logs is not null) return;
        var directory = Path.Combine(root, source.Name);
        Directory.CreateDirectory(directory);
        for (var suffix = 0; ; suffix++)
        {
            var name = $"capture-{date}" + (suffix == 0 ? "" : $"-{suffix + 1}");
            source.Folder = Path.Combine(directory, name + ".partial");
            if (Directory.Exists(source.Folder) || File.Exists(Path.Combine(directory, name + ".zip"))) continue;
            Directory.CreateDirectory(source.Folder);
            try
            {
                source.Lease = new FileStream(Path.Combine(source.Folder, "session.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                break;
            }
            catch (IOException) when (File.Exists(Path.Combine(source.Folder, "session.lock"))) { }
        }
        source.Logs = new CompressedLog[FileNames.Length];
        for (var i = 0; i < FileNames.Length; i++)
            source.Logs[i] = new CompressedLog(Path.Combine(source.Folder, FileNames[i]));
        WriteMetadata(source, new { kind = "session_start", schema = 4, run_id = runId, utc = DateTimeOffset.UtcNow,
            environment = source.Name, stopwatch_frequency = Stopwatch.Frequency,
            contains_sensitive_raw_data = true, streams = new[] { "api", "combat", "raw", "diagnostics" }, metadata });
        WriteMetadata(source, new { kind = "capture_status", utc = DateTimeOffset.UtcNow, data = status(source.Name == "Private") });
        foreach (var log in source.Logs) log.Flush();
        SaveCheckpoint(source);
    }

    private static void WriteMetadata(Source source, object data) => source.Logs![3].WriteLine(JsonSerializer.Serialize(data));

    private static void ReportDrops(Source source)
    {
        var count = Interlocked.Exchange(ref source.Dropped, 0);
        if (count != 0) WriteMetadata(source, new { kind = "capture_gap", utc = DateTimeOffset.UtcNow, dropped_events = count, reason = "writer_queue_full" });
    }

    private void Flush()
    {
        foreach (var source in sources.Where(s => s.Logs is not null))
        {
            ReportDrops(source);
            foreach (var log in source.Logs!) log.Flush();
            SaveCheckpoint(source);
        }
    }

    private static void SaveCheckpoint(Source source)
    {
        var path = Path.Combine(source.Folder!, "session-state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new
        {
            schema = 1,
            files = source.Logs!.Select((log, i) => new { name = FileNames[i], bytes = log.Bytes, sha256 = log.Sha256 })
        }), new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private void RecoverArchives()
    {
        foreach (var source in sources)
        {
            var directory = Path.Combine(root, source.Name);
            if (!Directory.Exists(directory)) continue;
            foreach (var folder in Directory.EnumerateDirectories(directory, "capture-*.partial"))
            {
                try
                {
                    bool packed;
                    using (new FileStream(Path.Combine(folder, "session.lock"), FileMode.Open, FileAccess.Write, FileShare.None))
                        packed = CaptureArchive.Pack(folder, true);
                    if (packed) CleanPackedFolder(folder);
                }
                catch (IOException) { }
                catch (Exception ex) { report($"Capture recovery skipped: {ex.GetType().Name}. Partial files were retained."); }
            }
        }
    }

    private void CleanPackedFolder(string folder)
    {
        if (!File.Exists(Path.ChangeExtension(folder, ".zip"))) return;
        if (!File.Exists(Path.Combine(folder, "session-state.json"))) return;
        try
        {
            foreach (var name in FileNames.Concat(new[] { "session.lock", "session-state.json", "session-state.json.tmp" })) File.Delete(Path.Combine(folder, name));
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { report("Capture ZIP is complete; temporary files could not be removed."); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopping, 1) != 0) return;
        queue.CompleteAdding();
        if (!worker.Join(TimeSpan.FromSeconds(30)))
            report("Capture writer did not finish within 30 seconds; partial files will be recovered on the next run.");
    }

    private sealed class Source(string name)
    {
        internal readonly string Name = name;
        internal CompressedLog[]? Logs;
        internal FileStream? Lease;
        internal string? Folder;
        internal long Dropped, Events;
    }

    private sealed record Entry(Source Source, int Stream, object Record, long Bytes);

    private sealed class CompressedLog(string path) : IDisposable
    {
        private readonly FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536);
        private readonly MemoryStream block = new();
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private StreamWriter? writer;
        private long characters;
        internal long Bytes { get; private set; }
        internal string Sha256 { get; private set; } = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));

        internal void WriteLine(string line)
        {
            writer ??= new StreamWriter(new GZipStream(block, CompressionLevel.Optimal, leaveOpen: true), new UTF8Encoding(false), 65536);
            writer.WriteLine(line);
            characters += line.Length;
            if (characters >= 4 * 1024 * 1024) Flush();
        }

        internal void Flush()
        {
            if (writer is null) return;
            writer.Dispose();
            writer = null;
            // Append complete gzip members so a normal native exit needs no archive footer.
            file.Write(block.GetBuffer(), 0, checked((int)block.Length));
            file.Flush();
            hash.AppendData(block.GetBuffer(), 0, checked((int)block.Length));
            Bytes = file.Length;
            Sha256 = Convert.ToHexString(hash.GetCurrentHash());
            block.SetLength(0);
            characters = 0;
        }

        public void Dispose()
        {
            try
            {
                if (writer is null && file.Length == 0)
                    writer = new StreamWriter(new GZipStream(block, CompressionLevel.Optimal, true), new UTF8Encoding(false));
                Flush();
            }
            finally { file.Dispose(); block.Dispose(); hash.Dispose(); }
        }
    }
}

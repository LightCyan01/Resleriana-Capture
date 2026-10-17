using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtelierCapture.Archive;
using AtelierCapture.Storage;
using Microsoft.Win32.SafeHandles;

namespace AtelierCapture;

internal sealed partial class SessionWriter : IDisposable
{
    internal const long QueueBudget = 64 * 1024 * 1024;
    internal const int QueueCapacity = 2048;
    private const int OfficialSource = 0,
        PrivateSource = 1,
        ProcessSource = 2;
    private const int ApiStream = 0,
        CombatStream = 1,
        RawStream = 2,
        DiagnosticsStream = 3;
    private static readonly string[] StreamNames = ["api", "combat", "raw", "diagnostics"];
    private readonly BlockingCollection<Batch> queue = new(QueueCapacity);
    private readonly HashSet<Batch> retained = [];
    private readonly HashSet<string> recoveredFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly object admission = new();
    private readonly object copier = new();
    private readonly ThreadLocal<Observation?> observation = new();
    private readonly CancellationTokenSource aborted = new();
    private readonly ManualResetEventSlim completed = new();
    private readonly Source[] sources = [new("Official"), new("Private"), new("Unknown")];
    private readonly string root;
    private readonly object metadata;
    private readonly CapturePrivacy privacy;
    private readonly Func<bool, object> status;
    private readonly Action<string> report;
    private readonly StorageHooks? hooks;
    private readonly Thread worker;
    private readonly long started = Stopwatch.GetTimestamp();
    private readonly string date;
    private long sequence,
        queuedBytes,
        candidateBytes,
        faultCount,
        recoveryPending,
        recoveryFailures,
        oldPackagedSources;
    private int active,
        stopping,
        finalStatusReady,
        firstEnvironment = -1,
        faultCode,
        recoveryCode;
    private string? abortReason,
        faultStage,
        faultType,
        recoveryFolder,
        recoveryError,
        recoveryCandidate;
    private bool recoveryInProgress;
    private long faultSince;

    internal string RunId { get; } = Guid.NewGuid().ToString("N");
    internal bool PrivacyEnabled => privacy.Enabled;

    internal SessionWriter(
        string root,
        object metadata,
        Func<bool, object> status,
        Action<string> report
    )
        : this(root, metadata, status, report, null) { }

    internal SessionWriter(
        string root,
        object metadata,
        Func<bool, object> status,
        Action<string> report,
        StorageHooks? hooks,
        CapturePrivacy? privacy = null
    )
    {
        this.root = Path.GetFullPath(root);
        this.privacy = privacy ?? CapturePrivacy.Disabled;
        this.metadata = this.privacy.SessionMetadata(metadata);
        this.status = status;
        this.report = report;
        this.hooks = hooks;
        date = (hooks?.Timestamp ?? DateTime.Now).ToString(
            "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture
        );
        try
        {
            recoveryPending = InactiveFolders().LongCount();
            oldPackagedSources = OldFolders()
                .LongCount(folder => File.Exists(Path.ChangeExtension(folder, ".zip")));
        }
        catch (Exception ex)
        {
            RecoveryFailure(ex);
            Report(
                $"Old capture inventory is unavailable ({ex.GetType().Name}); existing files remain unchanged."
            );
        }
        worker = new Thread(WriteLoop) { IsBackground = true, Name = "Atelier capture writer" };
        worker.Start();
    }

    // A deferred outer observation is queued without waiting; AfterSave actions report its save.
    internal IDisposable BeginObservation(bool deferred = false)
    {
        if (Environment.CurrentManagedThreadId == worker.ManagedThreadId)
            throw new InvalidOperationException(
                "The capture writer cannot observe native callbacks or recursively emit reports."
            );
        if (observation.Value is { } nested)
        {
            nested.Depth++;
            return new Scope(this, nested);
        }
        lock (admission)
        {
            ThrowIfAborted();
            if (stopping != 0)
                throw new InvalidOperationException("Capture observation admission has closed.");
            active++;
        }
        try
        {
            Monitor.Enter(copier);
            ThrowIfAborted();
            var current = new Observation(Environment.CurrentManagedThreadId)
            {
                Deferred = deferred,
            };
            observation.Value = current;
            return new Scope(this, current);
        }
        catch
        {
            if (Monitor.IsEntered(copier))
                Monitor.Exit(copier);
            FinishObservation();
            throw;
        }
    }

    // Runs on the writer thread after the current observation is durable; never after a failed save.
    internal void AfterSave(Action action) =>
        (
            (
                observation.Value
                ?? throw new InvalidOperationException("Capture save action has no observation.")
            ).OnSaved ??= []
        ).Add(action);

    // Lets shutdown summaries see every deferred save action that already ran.
    internal void WaitForQueued()
    {
        lock (admission)
            while (retained.Count != 0)
            {
                ThrowIfAborted();
                Monitor.Wait(admission);
            }
    }

    internal void Emit(
        bool isPrivate,
        bool combat,
        string kind,
        object? data,
        long retainedBytes = 0,
        string? systemId = null
    )
    {
        using var automatic = observation.Value is null ? BeginObservation() : null;
        var index = isPrivate ? PrivateSource : OfficialSource;
        if (Volatile.Read(ref firstEnvironment) < 0)
            Interlocked.CompareExchange(ref firstEnvironment, index, -1);
        Add(
            index,
            combat ? CombatStream
                : kind == "raw_body" ? RawStream
                : kind is "request" or "response" ? ApiStream
                : DiagnosticsStream,
            kind,
            data,
            retainedBytes,
            systemId ?? (combat ? "Battle" : "Recorder")
        );
    }

    internal void EmitBootstrap(string kind, object? detachedData)
    {
        using var automatic = observation.Value is null ? BeginObservation() : null;
        var known = Volatile.Read(ref firstEnvironment);
        Add(
            known < 0 ? ProcessSource : known,
            DiagnosticsStream,
            kind,
            new
            {
                scope = "process",
                original_environment = "Unknown",
                data = detachedData,
            },
            0,
            "Recorder"
        );
    }

    private void Add(
        int sourceIndex,
        int stream,
        string kind,
        object? data,
        long retainedBytes,
        string systemId
    )
    {
        ThrowIfAborted();
        var current =
            observation.Value
            ?? throw new InvalidOperationException("Capture record has no observation scope.");
        (kind, data) = privacy.Project(kind, data, systemId);
        var source = sources[sourceIndex];
        if (source.InitialStatus is null)
        {
            try
            {
                source.InitialStatus =
                    privacy.Enabled ? privacy.Status
                    : sourceIndex == ProcessSource ? new { scope = "process" }
                    : status(sourceIndex == PrivateSource);
            }
            catch (Exception ex)
            {
                source.InitialStatus = new
                {
                    complete = false,
                    status = "incomplete",
                    error = ex.GetType().Name,
                };
                Report(
                    $"Capture initial managed status failed ({ex.GetType().Name}); available records are still retained."
                );
            }
        }
        var record = new Record(
            5,
            RunId,
            checked(++sequence),
            DateTimeOffset.UtcNow,
            Stopwatch.GetTimestamp() - started,
            current.Thread,
            source.Name,
            StreamNames[stream],
            kind,
            systemId,
            data
        );
        var encoded = new CompressedMember();
        try
        {
            using var counter = new CountOutput(encoded);
            CaptureJson.WriteLine(counter, record);
            var charge = checked(
                Math.Max(retainedBytes, encoded.RetainedCapacity)
                + encoded.RetainedCapacity
                + counter.Bytes
                + 2 * 65536
            );
            current.Bytes = checked(current.Bytes + charge);
            current.Entries.Add(new(sourceIndex, stream, new EncodedRecord(encoded)));
        }
        catch
        {
            encoded.Dispose();
            throw;
        }
    }

    private void EndObservation(Observation current)
    {
        if (
            current.Thread != Environment.CurrentManagedThreadId
            || !ReferenceEquals(observation.Value, current)
        )
            throw new InvalidOperationException(
                "Capture observation scopes must close on their original thread."
            );
        if (--current.Depth != 0)
            return;
        Batch? batch = null;
        var transferred = false;
        try
        {
            observation.Value = null;
            ThrowIfAborted();
            if (current.Entries.Count != 0)
            {
                batch = new Batch(current.Entries.ToArray(), current.Bytes);
                if (
                    current.Entries.Any(entry =>
                        entry.Source != ProcessSource && !sources[entry.Source].Initialized
                    ) && sources[ProcessSource].Initialized
                )
                    batch = new Batch(
                        batch.Entries,
                        checked(
                            batch.Bytes
                            + sources[ProcessSource].Logs[DiagnosticsStream]!.ExtractedBytes * 4
                        )
                    );
                batch.OnSaved = current.OnSaved;
                lock (admission)
                {
                    while (
                        retained.Count >= QueueCapacity
                        || (queuedBytes != 0 && batch.Bytes > QueueBudget - queuedBytes)
                    )
                    {
                        ThrowIfAborted();
                        Monitor.Wait(admission);
                    }
                    ThrowIfAborted();
                    queuedBytes = checked(queuedBytes + batch.Bytes);
                    retained.Add(batch);
                    queue.Add(batch);
                    transferred = true;
                }
            }
        }
        finally
        {
            Monitor.Exit(copier);
            if (!transferred)
            {
                DisposeEncoded(current.Entries);
                FinishObservation();
            }
        }
        if (!transferred)
            return;
        if (current.Deferred)
        {
            FinishObservation();
            return;
        }
        try
        {
            // WaitAsync adds a pool continuation; durable callbacks must wake directly.
            batch!.Saved.Task.Wait(aborted.Token);
            ThrowIfAborted();
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            throw new CaptureAbortedException(abortReason ?? "Capture explicitly aborted.");
        }
        finally
        {
            FinishObservation();
        }
    }

    private void FinishObservation()
    {
        lock (admission)
        {
            active--;
            CompleteAdmission();
        }
    }

    private void CompleteAdmission()
    {
        if (stopping != 0 && active == 0 && finalStatusReady != 0 && !queue.IsAddingCompleted)
            queue.CompleteAdding();
    }

    internal object GetStatus()
    {
        lock (admission)
            return new
            {
                run_id = RunId,
                stopping = stopping != 0,
                aborted = aborted.IsCancellationRequested,
                abort_reason = abortReason,
                completed = completed.IsSet,
                admitted_observations = active,
                retained_batches = retained.Count,
                queued_batches = queue.Count,
                retained_bytes_estimate = queuedBytes,
                candidate_compressed_bytes = Interlocked.Read(ref candidateBytes),
                queue_budget = QueueBudget,
                queue_capacity = QueueCapacity,
                blocked_stage = faultStage,
                error = faultType,
                storage_failures = Interlocked.Read(ref faultCount),
                storage_error_code = faultCode,
                old_recovery_pending = Interlocked.Read(ref recoveryPending),
                old_packaged_sources_retained_unverified = oldPackagedSources,
                old_recovery_in_progress = recoveryInProgress,
                old_recovery_may_delay_callbacks = recoveryInProgress,
                old_recovery_folder = recoveryFolder,
                old_recovery_failures = Interlocked.Read(ref recoveryFailures),
                old_recovery_error = recoveryError,
                old_recovery_error_code = recoveryCode,
                durable_current = !aborted.IsCancellationRequested
                    && active == 0
                    && retained.Count == 0
                    && faultStage is null,
                capture_zip_complete = sources.Any(source =>
                    source.Name != "Unknown" && source.Initialized
                )
                    && sources
                        .Where(source => source.Name != "Unknown" && source.Initialized)
                        .All(source => !source.ArchivePending),
                blocked_ms = faultSince == 0
                    ? 0
                    : (Stopwatch.GetTimestamp() - faultSince) * 1000.0 / Stopwatch.Frequency,
                persistent_diagnostics_available = faultStage is null,
                sources = sources
                    .Select(source => new
                    {
                        environment = source.Name,
                        folder = source.Folder,
                        archive_pending = source.ArchivePending,
                        abort_marker_saved = source.AbortMarkerSaved,
                        working_files_cleared = source.WorkingFilesCleared,
                        working_files_retained_reason = source.RetentionReason,
                        working_cleanup_started = source.CleanupStarted,
                        working_files_removed = source.RemovedFiles,
                        working_files_remaining = source.RemainingFiles,
                        committed_generation = Interlocked.Read(ref source.Generation),
                        committed_batches = Interlocked.Read(ref source.CommittedBatches),
                        committed_events = Interlocked.Read(ref source.Events),
                    })
                    .ToArray(),
            };
    }

    internal void Abort(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Explicit abort needs a reason.", nameof(reason));
        if (privacy.Enabled)
            reason = "explicit_abort_privacy_mode";
        lock (admission)
        {
            if (aborted.IsCancellationRequested || completed.IsSet)
                return;
            abortReason = reason;
            stopping = 1;
            finalStatusReady = 1;
            aborted.Cancel();
            Monitor.PulseAll(admission);
            CompleteAdmission();
        }
        Report(
            "Capture explicitly aborted. Committed files and provisional tails are retained; this run is incomplete."
        );
    }

    private void ThrowIfAborted()
    {
        if (aborted.IsCancellationRequested)
            throw new CaptureAbortedException(abortReason ?? "Capture explicitly aborted.");
    }

    private void WriteLoop()
    {
        try
        {
            using var inactive = InactiveFolders().GetEnumerator();
            var recoveryPassComplete = false;
            while (!queue.IsCompleted)
            {
                if (!queue.TryTake(out var batch, 250, aborted.Token))
                {
                    bool idle;
                    lock (admission)
                        idle = active == 0 && stopping == 0;
                    if (idle && !recoveryPassComplete)
                        recoveryPassComplete = RecoverNextArchive(inactive);
                    continue;
                }
                // Everything already queued shares one commit: one flush per touched file covers it all.
                var group = new List<Batch> { batch };
                while (queue.TryTake(out var next))
                    group.Add(next);
                foreach (
                    var entries in group
                        .SelectMany(item => item.Entries)
                        .GroupBy(entry => entry.Source)
                )
                    CommitRecords(
                        sources[entries.Key],
                        entries.ToArray(),
                        group.Count(item => item.Entries.Any(entry => entry.Source == entries.Key))
                    );
                Retry(
                    "acknowledgement",
                    () => hooks?.Before?.Invoke(StorageAction.Acknowledge, root, sequence)
                );
                foreach (var item in group)
                {
                    foreach (var action in item.OnSaved ?? [])
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception error)
                        {
                            Report(
                                $"Capture save action failed ({error.GetType().Name}); its status counts lag the saved records."
                            );
                        }
                    }
                    Release(item);
                    item.Saved.TrySetResult(true);
                }
            }
            foreach (var source in sources.Where(source => source.Initialized))
            {
                if (source.Name == "Unknown" && source.Frozen)
                    continue;
                CommitRecords(
                    source,
                    [
                        new(
                            0,
                            DiagnosticsStream,
                            new
                            {
                                kind = "capture_status",
                                utc = DateTimeOffset.UtcNow,
                                data = source.FinalStatus ?? source.InitialStatus,
                            }
                        ),
                        new(
                            0,
                            DiagnosticsStream,
                            new
                            {
                                kind = "session_end",
                                run_id = RunId,
                                utc = DateTimeOffset.UtcNow,
                                events = source.Events,
                                shutdown_complete = true,
                            }
                        ),
                    ],
                    0
                );
                source.NormalClosed = true;
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested) { }
        catch (Exception ex)
        {
            try
            {
                Retry(
                    "writer_state",
                    () =>
                        throw new InvalidOperationException(
                            "Capture writer state cannot continue safely.",
                            ex
                        )
                );
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested) { }
        }
        finally
        {
            var packages = new VerifiedCapture?[2];
            foreach (var source in sources)
            {
                if (aborted.IsCancellationRequested && source.Folder is not null)
                    SaveAbortMarker(source);
                foreach (var log in source.Logs)
                {
                    try
                    {
                        log?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Report(
                            $"Capture source close failed ({ex.GetType().Name}); source files were retained."
                        );
                    }
                }
                try
                {
                    CloseCheckpoints(source);
                }
                catch (Exception ex)
                {
                    Report(
                        $"Capture checkpoint close failed ({ex.GetType().Name}); source files were retained."
                    );
                }
                try
                {
                    source.Lease?.Dispose();
                    source.Lease = null;
                }
                catch (Exception ex)
                {
                    Report(
                        $"Capture lease close failed ({ex.GetType().Name}); source files were retained."
                    );
                }
                if (
                    !aborted.IsCancellationRequested
                    && source.Initialized
                    && source.Name != "Unknown"
                )
                {
                    try
                    {
                        var index = source.Name == "Official" ? OfficialSource : PrivateSource;
                        packages[index] = CaptureArchive.PackVerified(source.Folder!, false);
                        source.ArchivePending = packages[index] is null;
                        if (source.ArchivePending)
                            Report(
                                "Capture archive remains pending; all source files were retained."
                            );
                    }
                    catch (Exception ex)
                    {
                        Report(
                            $"Capture archive remains pending ({ex.GetType().Name}); all source files were retained."
                        );
                    }
                }
            }
            try
            {
                CleanupCurrentWorkingFiles(packages);
            }
            finally
            {
                foreach (var package in packages)
                    package?.Dispose();
            }
            if (aborted.IsCancellationRequested)
            {
                lock (admission)
                {
                    foreach (var batch in retained)
                        DisposeEncoded(batch.Entries);
                    retained.Clear();
                    queuedBytes = 0;
                }
                while (queue.TryTake(out _)) { }
            }
            completed.Set();
        }
    }

    private void Release(Batch batch)
    {
        DisposeEncoded(batch.Entries);
        lock (admission)
        {
            if (!retained.Remove(batch))
                throw new InvalidDataException("Capture batch ownership was released twice.");
            queuedBytes -= batch.Bytes;
            Monitor.PulseAll(admission);
        }
    }

    private static void DisposeEncoded(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
            if (entry.Data is EncodedRecord record)
                record.Bytes.Dispose();
    }

    private void CommitRecords(Source source, Entry[] entries, int batches)
    {
        var committedEvents = checked(
            source.Events + entries.LongCount(entry => entry.Data is EncodedRecord)
        );
        Retry("initialize", () => EnsureFiles(source));
        if (source.Name != "Unknown" && !source.Initialized)
            FreezeBootstrap(source);
        CompressedMember?[]? members = null;
        StorageCheckpointPayload? checkpoint = null;
        byte[]? checkpointBytes = null;
        try
        {
            Retry(
                "serialize",
                () =>
                {
                    var prepared = new CompressedMember?[4];
                    try
                    {
                        for (var i = 0; i < prepared.Length; i++)
                        {
                            var streamEntries = entries
                                .Where(entry => entry.Stream == i)
                                .Select(entry => entry.Data)
                                .ToList();
                            if (!source.Initialized && i == DiagnosticsStream)
                            {
                                streamEntries.Insert(
                                    0,
                                    new
                                    {
                                        kind = "capture_status",
                                        utc = DateTimeOffset.UtcNow,
                                        data = source.InitialStatus,
                                    }
                                );
                                streamEntries.Insert(
                                    0,
                                    new
                                    {
                                        kind = "session_start",
                                        schema = 5,
                                        run_id = RunId,
                                        utc = source.Started,
                                        environment = source.Name,
                                        stopwatch_frequency = Stopwatch.Frequency,
                                        contains_sensitive_raw_data = !privacy.Enabled,
                                        streams = StreamNames,
                                        metadata,
                                    }
                                );
                            }
                            if (source.Initialized && streamEntries.Count == 0)
                                continue;
                            prepared[i] = BuildMember(source, i, streamEntries);
                        }
                        members = prepared;
                        Interlocked.Add(
                            ref candidateBytes,
                            prepared.Sum(member => member?.RetainedCapacity ?? 0)
                        );
                    }
                    catch
                    {
                        foreach (var member in prepared)
                            member?.Dispose();
                        throw;
                    }
                }
            );
            Retry(
                "commit",
                () =>
                {
                    try
                    {
                        for (var i = 0; i < members!.Length; i++)
                            if (members[i] is { } member)
                                source.Logs[i]!.AppendAndFlush(member, hooks);
                        checkpoint ??= new(
                            2,
                            checked(source.Generation + 1),
                            RunId,
                            source.Name,
                            source
                                .Logs.Select((log, i) => log!.CandidateStamp(members[i]))
                                .ToArray(),
                            checked(source.CommittedBatches + batches)
                        );
                        checkpointBytes ??= StorageCheckpoint.Encode(checkpoint);
                        WriteCheckpoint(source, checkpoint, checkpointBytes);
                        lock (admission)
                        {
                            for (var i = 0; i < source.Logs.Length; i++)
                                source.Logs[i]!.Commit(checkpoint.Streams[i]);
                            source.Generation = checkpoint.Generation;
                            source.CommittedBatches = checkpoint.CommittedBatches;
                            source.Events = committedEvents;
                            source.CheckpointBytes = checkpointBytes;
                            source.ArchivePending = source.Name != "Unknown";
                            source.Initialized = true;
                        }
                    }
                    catch
                    {
                        foreach (var log in source.Logs)
                            log?.ReopenAfterFailure();
                        CloseCheckpoints(source);
                        throw;
                    }
                }
            );
        }
        finally
        {
            if (members is not null)
            {
                Interlocked.Add(
                    ref candidateBytes,
                    -members.Sum(member => member?.RetainedCapacity ?? 0)
                );
                foreach (var member in members)
                    member?.Dispose();
            }
        }
    }

    private CompressedMember BuildMember(Source source, int stream, List<object> records)
    {
        var member = new CompressedMember();
        try
        {
            using (var gzip = new GZipStream(member, CompressionLevel.Fastest, leaveOpen: true))
            using (var output = new CountOutput(gzip))
            {
                if (!source.Initialized && stream == DiagnosticsStream)
                {
                    foreach (var record in records.Take(2))
                        WriteStoredRecord(output, record);
                    WriteBootstrapMarkers(source, output);
                    foreach (var record in records.Skip(2))
                        WriteStoredRecord(output, record);
                }
                else
                    foreach (var record in records)
                        WriteStoredRecord(output, record);
                member.Records = output.Records;
                member.ExtractedBytes = output.Bytes;
            }
            if (member.Length == 0)
                member.Write([0x1F, 0x8B, 8, 0, 0, 0, 0, 0, 4, 0, 3, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            return member;
        }
        catch
        {
            member.Dispose();
            throw;
        }
    }

    private static void WriteStoredRecord(Stream output, object record)
    {
        if (record is EncodedRecord encoded)
            encoded.Bytes.Visit(0, data => output.Write(data.Span));
        else
            CaptureJson.WriteLine(output, record);
    }

    private void EnsureFiles(Source source)
    {
        if (source.ResourcesReady)
            return;
        if (source.Lease is null)
        {
            var directory = Path.Combine(root, source.Name == "Unknown" ? "Process" : source.Name);
            Directory.CreateDirectory(directory);
            for (var suffix = 0; ; suffix++)
            {
                var name =
                    source.PendingFolderName
                    ?? ($"capture-{date}" + (suffix == 0 ? "" : $"-{checked(suffix + 1)}"));
                var candidate = Path.Combine(directory, name + ".partial");
                var archive = Path.Combine(directory, name + ".zip");
                if (
                    source.PendingFolderName is null
                    && (
                        Directory.Exists(candidate)
                        || File.Exists(candidate)
                        || File.Exists(archive)
                        || Directory.Exists(archive)
                    )
                )
                    continue;
                source.PendingFolderName = name;
                Directory.CreateDirectory(candidate);
                if (
                    File.Exists(archive)
                    || Directory.Exists(archive)
                    || Directory
                        .EnumerateFileSystemEntries(candidate)
                        .Any(path => Path.GetFileName(path) != "session.lock")
                )
                {
                    source.PendingFolderName = null;
                    continue;
                }
                FileStream lease;
                try
                {
                    hooks?.Before?.Invoke(StorageAction.Reserve, candidate, 0);
                    lease = OpenReservationFile(Path.Combine(candidate, "session.lock"));
                }
                catch (IOException) when (File.Exists(Path.Combine(candidate, "session.lock")))
                {
                    source.PendingFolderName = null;
                    continue;
                }
                if (File.Exists(archive) || Directory.Exists(archive))
                {
                    try
                    {
                        var delete = true;
                        if (!DeleteCleanupHandle(lease.SafeFileHandle, 4, ref delete, 1))
                            throw new System.ComponentModel.Win32Exception(
                                System.Runtime.InteropServices.Marshal.GetLastWin32Error()
                            );
                    }
                    finally
                    {
                        lease.Dispose();
                    }
                    source.PendingFolderName = null;
                    Report(
                        "Capture filename was published during reservation; its ZIP and folder are preserved and the next numeric suffix is used."
                    );
                    continue;
                }
                source.Lease = lease;
                source.Folder = candidate;
                source.OwnedFolderName = name;
                source.PendingFolderName = null;
                break;
            }
        }
        var ownedFolder =
            source.Folder
            ?? throw new InvalidDataException("Capture reservation has no owned directory.");
        hooks?.Before?.Invoke(StorageAction.Initialize, ownedFolder, 0);
        for (var i = 0; i < source.Logs.Length; i++)
            source.Logs[i] ??= new DurableLog(
                Path.Combine(ownedFolder, StorageCheckpoint.FileNames[i])
            );
        for (var i = 0; i < 2; i++)
        {
            using var slot = new FileStream(
                Path.Combine(ownedFolder, $"session-state.{i}.json"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read
            );
            slot.Flush(flushToDisk: true);
        }
        source.ResourcesReady = true;
    }

    private void WriteCheckpoint(Source source, StorageCheckpointPayload checkpoint, byte[] bytes)
    {
        var slot = (int)(checkpoint.Generation % 2);
        var path = Path.Combine(source.Folder!, $"session-state.{slot}.json");
        hooks?.Before?.Invoke(StorageAction.CheckpointWrite, path, checkpoint.Generation);
        // Kept open for the session: reopening per commit doubled the checkpoint cost.
        var file = source.Checkpoints[slot] ??= new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            4096
        );
        file.Position = 0;
        file.Write(bytes);
        file.SetLength(bytes.Length);
        hooks?.After?.Invoke(StorageAction.CheckpointWrite, path, checkpoint.Generation);
        hooks?.Before?.Invoke(StorageAction.CheckpointFlush, path, checkpoint.Generation);
        file.Flush(flushToDisk: true);
        hooks?.After?.Invoke(StorageAction.CheckpointFlush, path, checkpoint.Generation);
    }

    private static void CloseCheckpoints(Source source)
    {
        for (var i = 0; i < source.Checkpoints.Length; i++)
        {
            var file = source.Checkpoints[i];
            source.Checkpoints[i] = null;
            file?.Dispose();
        }
    }

    private void FreezeBootstrap(Source target)
    {
        var process = sources[ProcessSource];
        if (!process.Initialized || process.Frozen)
            return;
        CommitRecords(
            process,
            [
                new(
                    ProcessSource,
                    DiagnosticsStream,
                    new
                    {
                        kind = "session_end",
                        run_id = RunId,
                        utc = DateTimeOffset.UtcNow,
                        events = process.Events,
                        shutdown_complete = true,
                    }
                ),
            ],
            0
        );
        process.Frozen = true;
        process.NormalClosed = true;
        process.TransferEnvironment = target.Name;
    }

    private void WriteBootstrapMarkers(Source target, CountOutput output)
    {
        var process = sources[ProcessSource];
        target.BootstrapCopiedBytes = 0;
        target.BootstrapCopiedRecords = 0;
        if (target.Name == "Unknown" || !process.Frozen)
            return;
        var reference = new
        {
            run_id = RunId,
            environment = "Unknown",
            scope = "process",
            checkpoint_sha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(process.CheckpointBytes!)
            ),
            diagnostics_sha256 = process.Logs[DiagnosticsStream]!.Sha256,
            records = process.Logs[DiagnosticsStream]!.Records,
        };
        if (process.TransferEnvironment != target.Name)
        {
            CaptureJson.WriteLine(
                output,
                new
                {
                    kind = "process_bootstrap_reference",
                    utc = DateTimeOffset.UtcNow,
                    data = reference,
                }
            );
            return;
        }
        using var file = new FileStream(
            process.Logs[DiagnosticsStream]!.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            65536
        );
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var prefix = new MemoryStream();
        using (var writer = new Utf8JsonWriter(prefix))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "process_bootstrap");
            writer.WriteString("utc", DateTimeOffset.UtcNow);
            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WritePropertyName("source");
            JsonSerializer.Serialize(writer, reference);
            writer.WritePropertyName("original_record");
            writer.Flush();
        }
        var header = prefix.ToArray();
        var buffer = new byte[65536];
        var inRecord = false;
        int count;
        while ((count = gzip.Read(buffer)) != 0)
        {
            var data = buffer.AsSpan(0, count);
            while (!data.IsEmpty)
            {
                if (!inRecord)
                {
                    output.Write(header);
                    inRecord = true;
                }
                var newline = data.IndexOf((byte)'\n');
                if (newline < 0)
                {
                    output.Write(data);
                    target.BootstrapCopiedBytes = checked(
                        target.BootstrapCopiedBytes + data.Length
                    );
                    break;
                }
                output.Write(data[..newline]);
                target.BootstrapCopiedBytes = checked(target.BootstrapCopiedBytes + newline + 1L);
                target.BootstrapCopiedRecords = checked(target.BootstrapCopiedRecords + 1);
                output.Write("}}\n"u8);
                inRecord = false;
                data = data[(newline + 1)..];
            }
        }
        if (inRecord)
            throw new InvalidDataException("Bootstrap spool ends within a record.");
        if (
            target.BootstrapCopiedBytes != process.Logs[DiagnosticsStream]!.ExtractedBytes
            || target.BootstrapCopiedRecords != process.Logs[DiagnosticsStream]!.Records
        )
            throw new InvalidDataException(
                "Bootstrap copied prefix differs from its durable record/byte totals."
            );
    }

    private void Retry(string stage, Action action)
    {
        var delay = 250;
        while (true)
        {
            ThrowIfAborted();
            try
            {
                action();
                faultStage = null;
                faultType = null;
                faultCode = 0;
                faultSince = 0;
                return;
            }
            catch (Exception ex) when (ex is not CaptureAbortedException)
            {
                Interlocked.Increment(ref faultCount);
                var previous = faultStage;
                faultStage = stage;
                faultType = ex.GetType().Name;
                faultCode = ex.HResult;
                if (faultSince == 0)
                    faultSince = Stopwatch.GetTimestamp();
                if (previous != stage)
                    Report(
                        $"Capture is blocked at {stage} ({ex.GetType().Name}). Records are retained; repair storage or explicitly abort."
                    );
                if (aborted.Token.WaitHandle.WaitOne(delay))
                    ThrowIfAborted();
                delay = Math.Min(5000, delay * 2);
            }
        }
    }

    private void Report(string message)
    {
        try
        {
            report(message);
        }
        catch
        {
            Interlocked.Increment(ref faultCount);
        }
    }

    private void SaveAbortMarker(Source source)
    {
        try
        {
            using var file = new FileStream(
                Path.Combine(source.Folder!, "abort.json"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read
            );
            JsonSerializer.Serialize(
                file,
                new
                {
                    schema = 1,
                    run_id = RunId,
                    environment = source.Name,
                    utc = DateTimeOffset.UtcNow,
                    reason = abortReason,
                    complete = false,
                    last_committed_generation = source.Generation,
                    committed_batches = source.CommittedBatches,
                    committed_events = source.Events,
                    provisional_tail_may_exist = true,
                }
            );
            file.Flush(flushToDisk: true);
            source.AbortMarkerSaved = true;
        }
        catch (Exception ex)
        {
            Report(
                $"Explicit abort could not be saved durably ({ex.GetType().Name}); source files and provisional tails remain."
            );
        }
    }

    private IEnumerable<string> OldFolders()
    {
        foreach (var environment in new[] { "Official", "Private" })
        {
            var directory = Path.Combine(root, environment);
            if (!Directory.Exists(directory))
                continue;
            foreach (var folder in Directory.EnumerateDirectories(directory, "capture-*.partial"))
                if (
                    !sources.Any(source =>
                        string.Equals(source.Folder, folder, StringComparison.OrdinalIgnoreCase)
                    ) && !recoveredFolders.Contains(folder)
                )
                    yield return folder;
        }
    }

    private IEnumerable<string> InactiveFolders() =>
        OldFolders().Where(folder => !File.Exists(Path.ChangeExtension(folder, ".zip")));

    private bool RecoverNextArchive(IEnumerator<string> inactive)
    {
        try
        {
            if (recoveryCandidate is null)
            {
                if (!inactive.MoveNext())
                    return true;
                recoveryCandidate = inactive.Current;
            }
            string folder;
            lock (admission)
            {
                if (active != 0 || stopping != 0 || queue.Count != 0)
                    return false;
                folder = recoveryCandidate;
                recoveryCandidate = null;
                recoveryFolder = folder;
                recoveryInProgress = true;
            }
            RecoverArchive(folder);
            return true;
        }
        catch (Exception ex)
        {
            RecoveryFailure(ex);
            return true;
        }
    }

    private void RecoverArchive(string folder)
    {
        lock (admission)
        {
            recoveryFolder = folder;
            recoveryInProgress = true;
        }
        try
        {
            if (aborted.IsCancellationRequested)
                return;
            if (!CaptureArchive.Pack(folder, true, privacy.Enabled))
                throw new InvalidDataException(
                    "Old capture has no verified packageable checkpoint."
                );
            recoveredFolders.Add(folder);
            Interlocked.Exchange(
                ref recoveryPending,
                Math.Max(0, Interlocked.Read(ref recoveryPending) - 1)
            );
        }
        catch (Exception ex)
        {
            RecoveryFailure(ex);
        }
        finally
        {
            lock (admission)
                recoveryInProgress = false;
        }
    }

    private void RecoveryFailure(Exception error)
    {
        Interlocked.Increment(ref recoveryFailures);
        lock (admission)
        {
            recoveryError = error.GetType().Name;
            recoveryCode = error.HResult;
        }
        Report(
            $"Old capture recovery remains pending ({error.GetType().Name}); source files were retained."
        );
    }

    public void Dispose()
    {
        if (
            Environment.CurrentManagedThreadId == worker.ManagedThreadId
            || observation.Value is not null
        )
            throw new InvalidOperationException(
                "Capture shutdown cannot wait for its own writer or observation."
            );
        var initializeStatus = false;
        lock (admission)
        {
            if (stopping == 0)
            {
                stopping = 1;
                initializeStatus = true;
            }
        }
        if (initializeStatus)
        {
            try
            {
                for (var i = 0; i < 2; i++)
                    sources[i].FinalStatus = privacy.Enabled
                        ? privacy.Status
                        : status(i == PrivateSource);
            }
            catch (Exception ex)
            {
                foreach (var source in sources.Where(source => source.Name != "Unknown"))
                    source.FinalStatus = new
                    {
                        complete = false,
                        status = "incomplete",
                        error = ex.GetType().Name,
                        previous_status = source.InitialStatus,
                    };
                Report(
                    $"Capture final managed status failed ({ex.GetType().Name}); existing status is retained."
                );
            }
            finally
            {
                lock (admission)
                {
                    finalStatusReady = 1;
                    CompleteAdmission();
                }
            }
        }
        if (!aborted.IsCancellationRequested)
        {
            try
            {
                completed.Wait(aborted.Token);
            }
            catch (OperationCanceledException) when (aborted.IsCancellationRequested) { }
        }
    }

    private sealed record Record(
        int schema,
        string run_id,
        long sequence,
        DateTimeOffset utc,
        long elapsed_ticks,
        int thread,
        string environment,
        string stream,
        string kind,
        string system_id,
        object? data
    );

    private sealed record Entry(int Source, int Stream, object Data);

    private sealed record EncodedRecord(CompressedMember Bytes);

    private sealed class Observation(int thread)
    {
        internal readonly int Thread = thread;
        internal int Depth = 1;
        internal long Bytes;
        internal bool Deferred;
        internal List<Action>? OnSaved;
        internal readonly List<Entry> Entries = [];
    }

    private sealed class Scope(SessionWriter owner, Observation current) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            owner.EndObservation(current);
        }
    }

    private sealed class Batch(Entry[] entries, long bytes)
    {
        internal readonly Entry[] Entries = entries;
        internal readonly long Bytes = bytes;
        internal List<Action>? OnSaved;
        internal readonly TaskCompletionSource<bool> Saved = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
    }

    private sealed class Source(string name)
    {
        internal readonly string Name = name;
        internal readonly DurableLog?[] Logs = new DurableLog?[4];
        internal readonly FileStream?[] Checkpoints = new FileStream?[2];
        internal readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
        internal string? Folder,
            OwnedFolderName,
            PendingFolderName,
            TransferEnvironment;
        internal object? InitialStatus,
            FinalStatus;
        internal FileStream? Lease;
        internal bool Initialized,
            Frozen,
            ResourcesReady,
            ArchivePending,
            AbortMarkerSaved;
        internal bool NormalClosed,
            WorkingFilesCleared;
        internal bool CleanupStarted;
        internal int RemovedFiles;
        internal string?[] RemainingFiles = [];
        internal string? RetentionReason;
        internal long Generation,
            CommittedBatches,
            Events;
        internal long BootstrapCopiedBytes,
            BootstrapCopiedRecords;
        internal byte[]? CheckpointBytes;
    }
}

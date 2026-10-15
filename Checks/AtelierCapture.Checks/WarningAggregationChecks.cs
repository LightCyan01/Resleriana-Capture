using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using AtelierCapture.Storage;
using BepInEx.Logging;

namespace AtelierCapture.Checks;

internal static class WarningAggregationChecks
{
    internal static void Run()
    {
        KnownMaterialErrors();
        MaterialErrorEligibility();
        ConcurrentMaterialErrors();
        OutOfOrderCommitCallbacks(knownError: true);
        BootstrapWarningTransfer(knownError: true);
        PartialCopyCommit(knownError: true);
        NestedAcknowledgement(knownError: true);
        ShutdownAcknowledgement(knownError: true);
        FailedAcknowledgement(knownError: true);
        RunPoolAcknowledgementRegression();
        DistinctMessagesAndSeverity();
        ActualLoggerDelivery();
        ConcurrentWarnings();
        OutOfOrderCommitCallbacks();
        BootstrapWarningTransfer();
        PartialCopyCommit();
        NestedAcknowledgement();
        ShutdownAcknowledgement();
        FailedAcknowledgement();
    }

    private static void MaterialErrorEligibility()
    {
        var record = new CaptureDiagnostic(
            "Unity",
            "Error",
            MaterialNre.StandardMessage,
            null,
            null,
            "test",
            DateTimeOffset.UtcNow,
            1,
            "1"
        );
        Require(MaterialNre.IsEligible(record), "Exact original material error is eligible.");
        foreach (
            var different in new[]
            {
                record with
                {
                    source = "DifferentUnity",
                },
                record with
                {
                    severity = "Fatal",
                },
                record with
                {
                    severity = "Error, Warning",
                },
                record with
                {
                    message = record.message.Replace("\r\n", "\n", StringComparison.Ordinal),
                },
                record with
                {
                    message = "OtherException\nOther.FirstFrame\n" + record.message,
                },
                record with
                {
                    exception_type = "System.NullReferenceException",
                },
                record with
                {
                    stack = record.message,
                },
                record with
                {
                    caused_by_event_id = "2",
                },
                record with
                {
                    failed_listener = "OtherListener",
                },
            }
        )
            Require(!MaterialNre.IsEligible(different), "Changed diagnostic metadata stays full.");

        using var fixture = new Fixture();
        using var unity = new ManualLogSource("Unity");
        var longError = new string('e', 32768);
        fixture.Log(longError, LogLevel.Error, unity);
        fixture.Log(longError, LogLevel.Error, unity);
        fixture.Log(MaterialNre.StandardMessage, LogLevel.Error, fixture.Source);
        fixture.Log(MaterialNre.StandardMessage, LogLevel.Error, fixture.Source);
        var exception = new NullReferenceException(MaterialNre.StandardMessage);
        fixture.Bridge.LogEvent(unity, new LogEventArgs(exception, LogLevel.Error, unity));
        fixture.Log("unchanged warning");
        fixture.Stop();
        var ordinary = fixture
            .ReadRows()
            .Where(row => Kind(row) == "runtime_log")
            .Select(Data)
            .ToArray();
        Require(
            ordinary.Length == 6
                && ordinary.All(data =>
                    new[]
                    {
                        "error_id",
                        "error_occurrences",
                        "error_first_utc",
                        "error_last_utc",
                        "error_origin",
                    }.All(name => !data.TryGetProperty(name, out _))
                )
                && ordinary.Count(data => data.GetProperty("message").GetString() == longError)
                    == 2,
            "Unrelated large/managed/different-source errors remain full and gain no nullable error fields."
        );
    }

    private static void ConcurrentMaterialErrors()
    {
        using var fixture = new Fixture();
        using var unity = new ManualLogSource("Unity");
        var events = Enumerable
            .Range(0, 32)
            .Select(_ => new LogEventArgs(MaterialNre.StandardMessage, LogLevel.Error, unity))
            .ToArray();
        Task.WhenAll(events.Select(args => Task.Run(() => fixture.Bridge.LogEvent(unity, args))))
            .WaitAsync(TimeSpan.FromSeconds(10))
            .GetAwaiter()
            .GetResult();
        fixture.Stop();
        var rows = fixture.ReadRows();
        var repeats = rows.Where(row => Kind(row) == "runtime_error_repeat").Select(Data).ToArray();
        Require(
            rows.Count(row => Kind(row) == "runtime_log") == 1
                && repeats.Length == 31
                && repeats
                    .Select(data => data.GetProperty("occurrences").GetInt64())
                    .Order()
                    .SequenceEqual(Enumerable.Range(2, 31).Select(value => (long)value))
                && repeats
                    .Select(data => data.GetProperty("event_id").GetString())
                    .Distinct()
                    .Count() == 31
                && repeats.All(data =>
                    data.GetProperty("thread").GetInt32() > 0
                    && data.GetProperty("phase").GetString() == "startup"
                )
                && events.All(args => !args.SuppressConsole),
            "Concurrent material errors have one full anchor and every durable ordinal without console suppression."
        );
        var aggregation = new WarningAggregation(DiagnosticFamily.Error);
        var first = new CaptureDiagnostic(
            "Unity",
            "Error",
            MaterialNre.StandardMessage,
            null,
            null,
            "test",
            DateTimeOffset.UtcNow,
            1,
            "1"
        );
        try
        {
            aggregation.Stage(
                first,
                "Official",
                (_, _) => throw new IOException("Synthetic emit failure."),
                () => { }
            );
            throw new InvalidOperationException("Synthetic emit must fail.");
        }
        catch (IOException) { }
        Require(
            aggregation.ErrorSummaries().Length == 0,
            "A failed first emission creates no durable error anchor."
        );
        aggregation.Stage(first with { event_id = "2" }, "Official", (_, _) => { }, () => { })();
        Require(
            aggregation.ErrorSummaries().Single().occurrences == 1,
            "Retry after failed emission starts at one saved occurrence."
        );
    }

    private static void KnownMaterialErrors()
    {
        const string prefix =
            "NullReferenceException: Object reference not set to an instance of an object.\r\n"
            + "CBJFMEGGCLP.FOHJOLJKGNP () (at <00000000000000000000000000000000>:0)\n"
            + "CBJFMEGGCLP.IOMNAOKAHCH () (at <00000000000000000000000000000000>:0)\n"
            + "CharacterObject.CallAfterFixPosition () (at <00000000000000000000000000000000>:0)\n";
        const string standard =
            prefix
            + "CharacterLoopManager+NNICKFAANCF.NNCECFDIAAG () (at <00000000000000000000000000000000>:0)\n"
            + "CharacterLoopManager.LateUpdate () (at <00000000000000000000000000000000>:0)\n";
        const string lod =
            prefix
            + "CharaLODControl.UpdateByLODTime () (at <00000000000000000000000000000000>:0)\n"
            + "CharaLODControl.GLOIEADCOHP (System.Boolean BKMOEGJJECI) (at <00000000000000000000000000000000>:0)\n"
            + "CharaLODControl.UpdateInfo (System.Boolean BKMOEGJJECI) (at <00000000000000000000000000000000>:0)\n";
        using var fixture = new Fixture();
        using var unity = new ManualLogSource("Unity");
        var events = new List<LogEventArgs>();
        foreach (var message in new[] { standard, lod })
        {
            events.Add(fixture.Log(message, LogLevel.Error, unity));
            events.Add(fixture.Log(message, LogLevel.Error, unity));
        }
        foreach (
            var (message, severity) in new[]
            {
                ("unrelated original error", LogLevel.Error),
                (standard, LogLevel.Fatal),
                (
                    standard.Replace(
                        "FOHJOLJKGNP",
                        "DifferentFirstFrame",
                        StringComparison.Ordinal
                    ),
                    LogLevel.Error
                ),
            }
        )
        {
            events.Add(fixture.Log(message, severity, unity));
            events.Add(fixture.Log(message, severity, unity));
        }
        fixture.Stop();
        var rows = fixture.ReadRows();
        var anchors = rows.Where(row => Kind(row) == "runtime_log").Select(Data).ToArray();
        var repeats = rows.Where(row => Kind(row) == "runtime_error_repeat").Select(Data).ToArray();
        var summaries = rows.Where(row => Kind(row) == "runtime_error_summary")
            .Select(Data)
            .ToArray();
        Require(
            anchors.Length == 8 && repeats.Length == 2 && summaries.Length == 2,
            $"Known material NREs retain two full anchors plus durable compact occurrences, while changed errors and Fatal remain full (anchors={anchors.Length}, repeats={repeats.Length}, summaries={summaries.Length})."
        );
        Require(
            events.All(args => !args.SuppressConsole),
            "Known and unrelated Error/Fatal records stay console-visible after durable acknowledgement."
        );
        foreach (var message in new[] { standard, lod })
        {
            var anchor = anchors.Single(data =>
                data.GetProperty("severity").GetString() == "Error"
                && data.GetProperty("message").GetString() == message
            );
            var id = anchor.GetProperty("error_id").GetString();
            var repeat = repeats.Single(data => data.GetProperty("error_id").GetString() == id);
            var summary = summaries.Single(data => data.GetProperty("error_id").GetString() == id);
            Require(
                anchor.GetProperty("error_occurrences").GetInt64() == 1
                    && repeat.GetProperty("occurrences").GetInt64() == 2
                    && summary.GetProperty("occurrences").GetInt64() == 2
                    && summary.GetProperty("first_utc").GetDateTimeOffset()
                        == anchor.GetProperty("utc").GetDateTimeOffset()
                    && summary.GetProperty("last_utc").GetDateTimeOffset()
                        == repeat.GetProperty("utc").GetDateTimeOffset()
                    && !repeat.TryGetProperty("message", out _)
                    && !repeat.TryGetProperty("stack", out _)
                    && !repeat.TryGetProperty("delta", out _)
                    && !repeat.TryGetProperty("first_utc", out _)
                    && !repeat.TryGetProperty("last_utc", out _)
                    && repeat.EnumerateObject().Count() == 7,
                "Known error series preserve the exact first stack, every occurrence and its timestamp without duplicate full stacks."
            );
        }
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(fixture.Bridge.Status));
        var errors = status.RootElement.GetProperty("error_aggregation");
        Require(
            errors.GetProperty("patterns_started").GetInt32() == 2
                && errors.GetProperty("occurrences_durable").GetInt64() == 4
                && errors.GetProperty("repeated_occurrences_durable").GetInt64() == 2
                && errors.GetProperty("pending_occurrences").GetInt64() == 0,
            "Known material errors have independent logical occurrence totals."
        );
    }

    private static void DistinctMessagesAndSeverity()
    {
        using var fixture = new Fixture();
        var full = new string('w', 4096);
        var first = fixture.Log(full);
        var repeat = fixture.Log(full);
        fixture.Log("interleaved warning");
        fixture.Log(full);
        fixture.Log(full, LogLevel.Error);
        fixture.Log(full, LogLevel.Error);
        fixture.Log(full, LogLevel.Fatal);
        fixture.Log(full, LogLevel.Fatal);
        using var other = new ManualLogSource("DifferentWarningSource");
        fixture.Log(full, source: other);
        fixture.Origin = "Private";
        fixture.Log(full);
        fixture.Origin = "Unknown";
        fixture.Log(full);
        Require(
            !first.SuppressConsole && repeat.SuppressConsole,
            "Only a durable repeat is suppressed."
        );
        fixture.Stop();
        var rows = fixture.ReadRows();
        var anchors = rows.Where(row => Kind(row) == "runtime_log").ToArray();
        var repeats = rows.Where(row => Kind(row) == "runtime_warning_repeat").ToArray();
        var summaries = rows.Where(row => Kind(row) == "runtime_warning_summary").ToArray();
        Require(
            anchors.Length == 9 && repeats.Length == 2 && summaries.Length == 5,
            $"Distinct messages, sources and origins retain anchors; every Error/Fatal remains separate (anchors={anchors.Length}, repeats={repeats.Length}, summaries={summaries.Length}, rows={rows.Length})."
        );
        Require(
            anchors.Count(row => Data(row).GetProperty("severity").GetString() == "Error") == 2
                && anchors.Count(row => Data(row).GetProperty("severity").GetString() == "Fatal")
                    == 2,
            "Error/Fatal repeats are never aggregated."
        );
        var firstData = anchors
            .Select(Data)
            .Single(data =>
                data.GetProperty("warning_id").ValueKind == JsonValueKind.String
                && data.GetProperty("source").GetString() == fixture.Source.SourceName
                && data.GetProperty("message").GetString() == full
                && data.GetProperty("warning_origin").GetString() == "Official"
            );
        var id = firstData.GetProperty("warning_id").GetString();
        var counts = repeats
            .Select(Data)
            .Where(data => data.GetProperty("warning_id").GetString() == id)
            .Select(data => data.GetProperty("occurrences").GetInt64())
            .ToArray();
        Require(
            counts.SequenceEqual(new long[] { 2, 3 })
                && repeats.All(row =>
                    !Data(row).TryGetProperty("message", out _)
                    && !Data(row).TryGetProperty("stack", out _)
                ),
            "Repeated records preserve ordered counts without duplicating full message or stack."
        );
        var summary = summaries
            .Select(Data)
            .Single(data => data.GetProperty("warning_id").GetString() == id);
        Require(
            summary.GetProperty("occurrences").GetInt64() == 3
                && summary.GetProperty("first_utc").GetDateTimeOffset()
                    == firstData.GetProperty("utc").GetDateTimeOffset()
                && summary.GetProperty("last_utc").GetDateTimeOffset()
                    == repeats
                        .Select(Data)
                        .Last(data => data.GetProperty("warning_id").GetString() == id)
                        .GetProperty("utc")
                        .GetDateTimeOffset(),
            "Final summary preserves exact first/last captured timestamps and cumulative count."
        );
        using var status = JsonDocument.Parse(JsonSerializer.Serialize(fixture.Bridge.Status));
        var warning = status.RootElement.GetProperty("warning_aggregation");
        Require(
            warning.GetProperty("occurrences_durable").GetInt64() == 7
                && warning.GetProperty("repeated_occurrences_durable").GetInt64() == 2
                && warning.GetProperty("pending_occurrences").GetInt64() == 0,
            "Status counts warning occurrences rather than stored full-message records."
        );

        var keyed = new WarningAggregation();
        var records = new List<object>();
        var when = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        var record = new CaptureDiagnostic(
            "Synthetic",
            "Warning",
            "same text",
            null,
            "stack-a",
            "test",
            when,
            1,
            "stack-1"
        );
        keyed.Stage(record, "Official", (_, data) => records.Add(data), () => { })();
        keyed.Stage(
            record with
            {
                stack = "stack-b",
                event_id = "stack-2",
            },
            "Official",
            (_, data) => records.Add(data),
            () => { }
        )();
        Require(
            records.All(value => value is CaptureDiagnostic) && keyed.Summaries().Length == 2,
            "Identical text with a different full stack preserves a distinct warning anchor."
        );
    }

    private static void ConcurrentWarnings()
    {
        using var fixture = new Fixture();
        var started = 0;
        var finished = 0;
        var args = Enumerable
            .Range(0, 64)
            .Select(_ => new LogEventArgs(
                "concurrent identical warning",
                LogLevel.Warning,
                fixture.Source
            ))
            .ToArray();
        var callbacks = Task.WhenAll(
            args.Select(arg =>
                Task.Run(() =>
                {
                    Interlocked.Increment(ref started);
                    try
                    {
                        fixture.Bridge.LogEvent(fixture.Source, arg);
                    }
                    finally
                    {
                        Interlocked.Increment(ref finished);
                    }
                })
            )
        );
        try
        {
            callbacks.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        warning_concurrency_timeout = true,
                        started = Volatile.Read(ref started),
                        finished = Volatile.Read(ref finished),
                        pool = PoolStatus(),
                        writer = fixture.Writer.GetStatus(),
                        bridge = fixture.Bridge.Status,
                    }
                )
            );
            fixture.Writer.Abort(
                "Synthetic warning concurrency timeout; explicit fixture cleanup."
            );
            callbacks.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            throw;
        }
        fixture.Stop();
        var rows = fixture.ReadRows();
        Require(
            rows.Count(row => Kind(row) == "runtime_log") == 1
                && rows.Count(row => Kind(row) == "runtime_warning_repeat") == 63
                && args.Count(arg => arg.SuppressConsole) == 63,
            "Concurrent identical warnings retain one anchor and each acknowledged repeat."
        );
        var counts = rows.Where(row => Kind(row) == "runtime_warning_repeat")
            .Select(row => Data(row).GetProperty("occurrences").GetInt64())
            .Order()
            .ToArray();
        Require(
            counts.SequenceEqual(Enumerable.Range(2, 63).Select(value => (long)value))
                && Data(rows.Single(row => Kind(row) == "runtime_warning_summary"))
                    .GetProperty("occurrences")
                    .GetInt64() == 64,
            "Concurrent callback counts reconstruct exactly without lost increments."
        );
    }

    internal static void RunPoolAcknowledgementChild(string mode, string evidencePath)
    {
        Require(mode is "before" or "after", "Fixed-pool child mode is explicit.");
        ThreadPool.GetMinThreads(out var originalMinimumWorkers, out var minimumIo);
        ThreadPool.GetMaxThreads(out var originalMaximumWorkers, out var maximumIo);
        Require(
            ThreadPool.SetMinThreads(4, minimumIo) && ThreadPool.SetMaxThreads(4, maximumIo),
            "Synthetic child fixes its own worker pool to four threads."
        );
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-pool-child-" + Guid.NewGuid().ToString("N")
        );
        using var ready = new CountdownEvent(4);
        using var release = new ManualResetEventSlim();
        using var acknowledgeEntered = new ManualResetEventSlim();
        using var acknowledgeRelease = new ManualResetEventSlim();
        var callerThreads = new Thread?[4];
        var acknowledgements = 0;
        using var writer = new SessionWriter(
            path,
            new { synthetic = true },
            _ => new { },
            _ => { },
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (
                        action != StorageAction.Acknowledge
                        || Interlocked.Increment(ref acknowledgements) != 1
                    )
                        return;
                    acknowledgeEntered.Set();
                    acknowledgeRelease.Wait();
                },
            }
        );
        var started = 0;
        var finished = 0;
        var abortedCallbacks = 0;
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var timer = Stopwatch.StartNew();
        var samples = new List<object>();
        var callbacks = Enumerable
            .Range(0, 4)
            .Select(index =>
                Task.Run(() =>
                {
                    callerThreads[index] = Thread.CurrentThread;
                    Interlocked.Increment(ref started);
                    ready.Signal();
                    release.Wait();
                    try
                    {
                        writer.Emit(false, false, "pool_child_record", new { index });
                    }
                    catch (CaptureAbortedException)
                    {
                        Interlocked.Increment(ref abortedCallbacks);
                    }
                    catch (Exception error)
                    {
                        failures.Enqueue(error.GetType().FullName!);
                    }
                    finally
                    {
                        Interlocked.Increment(ref finished);
                    }
                })
            )
            .ToArray();
        var observed = false;
        var normalCompletion = false;
        object? archiveProof = null;
        try
        {
            Require(ready.Wait(TimeSpan.FromSeconds(5)), "All four child pool callers start.");
            release.Set();
            Require(
                acknowledgeEntered.Wait(TimeSpan.FromSeconds(5)),
                "First child ACK is held while all four callers queue."
            );
            Require(
                SpinWait.SpinUntil(
                    () =>
                    {
                        var state = JsonSerializer.SerializeToElement(writer.GetStatus());
                        return state.GetProperty("retained_batches").GetInt32() == 4
                            && state.GetProperty("queued_batches").GetInt32() == 3
                            && callerThreads.All(thread =>
                                thread is not null
                                && (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin)
                                    != 0
                            );
                    },
                    TimeSpan.FromSeconds(5)
                ),
                "All four actual caller threads block on retained ACKs before writer release."
            );
            samples.Add(
                new
                {
                    phase = "before_ack_release",
                    elapsed_ms = timer.Elapsed.TotalMilliseconds,
                    started,
                    finished,
                    caller_states = callerThreads
                        .Select(thread => thread!.ThreadState.ToString())
                        .ToArray(),
                    writer = writer.GetStatus(),
                    pool = PoolStatus(),
                }
            );
            acknowledgeRelease.Set();
            timer.Restart();
            while (timer.Elapsed < TimeSpan.FromSeconds(5))
            {
                var state = JsonSerializer.SerializeToElement(writer.GetStatus());
                var committed = state
                    .GetProperty("sources")
                    .EnumerateArray()
                    .Single(source => source.GetProperty("environment").GetString() == "Official")
                    .GetProperty("committed_batches")
                    .GetInt64();
                var snapshot = new
                {
                    elapsed_ms = timer.Elapsed.TotalMilliseconds,
                    started = Volatile.Read(ref started),
                    finished = Volatile.Read(ref finished),
                    committed_batches = committed,
                    retained_batches = state.GetProperty("retained_batches").GetInt32(),
                    queued_batches = state.GetProperty("queued_batches").GetInt32(),
                    storage_failures = state.GetProperty("storage_failures").GetInt64(),
                    pool = PoolStatus(),
                };
                samples.Add(snapshot);
                if (
                    committed == 4
                    && Volatile.Read(ref finished) == 0
                    && snapshot.retained_batches == 0
                    && snapshot.queued_batches == 0
                    && ThreadPool.PendingWorkItemCount >= 4
                )
                {
                    Thread.Sleep(100);
                    observed = Volatile.Read(ref finished) == 0;
                    if (observed)
                        break;
                }
                if (Volatile.Read(ref finished) == 4)
                    break;
                Thread.Sleep(20);
            }
            normalCompletion =
                mode == "after"
                && Volatile.Read(ref finished) == 4
                && Volatile.Read(ref abortedCallbacks) == 0
                && failures.IsEmpty;
        }
        finally
        {
            release.Set();
            acknowledgeRelease.Set();
            if (!normalCompletion)
                writer.Abort("Synthetic fixed-pool acknowledgement diagnosis cleanup.");
            Task.WhenAll(callbacks).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            writer.Dispose();
            Require(
                SpinWait.SpinUntil(
                    () =>
                        JsonSerializer
                            .SerializeToElement(writer.GetStatus())
                            .GetProperty("completed")
                            .GetBoolean(),
                    TimeSpan.FromSeconds(5)
                ),
                "Fixed-pool child writer closes after explicit abort."
            );
            if (normalCompletion)
            {
                var zipPath = Directory
                    .EnumerateFiles(path, "*.zip", SearchOption.AllDirectories)
                    .Single();
                using var zip = ZipFile.OpenRead(zipPath);
                using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                var actual = new List<int>();
                foreach (var stamp in manifest.RootElement.GetProperty("entries").EnumerateArray())
                {
                    using var input = zip.GetEntry(stamp.GetProperty("name").GetString()!)!.Open();
                    using var copy = new MemoryStream();
                    input.CopyTo(copy);
                    var bytes = copy.ToArray();
                    Require(
                        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
                            == stamp.GetProperty("sha256").GetString()
                            && bytes.LongLength == stamp.GetProperty("bytes").GetInt64(),
                        "Fixed-pool normal completion preserves packaged stream integrity."
                    );
                    if (stamp.GetProperty("name").GetString() != "diagnostics.jsonl")
                        continue;
                    using var lines = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
                    long count = 0;
                    while (lines.ReadLine() is { } line)
                    {
                        count++;
                        using var row = JsonDocument.Parse(line);
                        if (row.RootElement.GetProperty("kind").GetString() == "pool_child_record")
                            actual.Add(
                                row.RootElement.GetProperty("data").GetProperty("index").GetInt32()
                            );
                    }
                    Require(
                        count == stamp.GetProperty("records").GetInt64(),
                        "Fixed-pool archive record count matches JSONL."
                    );
                }
                Require(
                    actual.Order().SequenceEqual(new[] { 0, 1, 2, 3 })
                        && manifest.RootElement.GetProperty("shutdown_complete").GetBoolean()
                        && !manifest.RootElement.GetProperty("recovered").GetBoolean(),
                    "All four fixed-pool callbacks finish normally with exactly four archived originals."
                );
                archiveProof = new
                {
                    callbacks = 4,
                    indices_unique = true,
                    streams_sha256_verified = true,
                    shutdown_complete = true,
                    recovered = false,
                };
            }
            var result = new
            {
                mode,
                scope = "Synthetic child-only fixed CLR pool, actual product SessionWriter; no parent pool/runtime changes.",
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                source = typeof(SessionWriter).Assembly.Location,
                product_sha256 = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(typeof(SessionWriter).Assembly.Location)
                    )
                ),
                starved_after_durable_commit = observed,
                normal_completion = normalCompletion,
                archive_proof = archiveProof,
                started,
                finished,
                aborted_callbacks = abortedCallbacks,
                failures = failures.ToArray(),
                elapsed_ms = timer.Elapsed.TotalMilliseconds,
                samples,
                final_writer = writer.GetStatus(),
                final_pool = PoolStatus(),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            File.WriteAllText(
                evidencePath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            );
            Require(
                ThreadPool.SetMaxThreads(originalMaximumWorkers, maximumIo)
                    && ThreadPool.SetMinThreads(originalMinimumWorkers, minimumIo),
                "Disposable child restores only its own original worker pool settings."
            );
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        Require(
            mode == "before"
                ? observed && finished == 4 && abortedCallbacks == 4 && failures.IsEmpty
                : normalCompletion
                    && !observed
                    && finished == 4
                    && abortedCallbacks == 0
                    && failures.IsEmpty,
            mode == "before"
                ? "Fixed-pool old control reproduces queued acknowledgements; abort releases all callers."
                : "Fixed-pool regression requires normal acknowledged progress without pool-continuation starvation."
        );
    }

    private static void RunPoolAcknowledgementRegression()
    {
        var output = Path.Combine(
            Path.GetTempPath(),
            "capture-pool-regression-" + Guid.NewGuid().ToString("N") + ".json"
        );
        var executable =
            Environment.ProcessPath
            ?? throw new InvalidOperationException("Check process path unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (
            Path.GetFileNameWithoutExtension(executable)
                .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        )
            start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
        start.ArgumentList.Add("--warning-pool-child");
        start.ArgumentList.Add("after");
        start.ArgumentList.Add(output);
        using var child =
            Process.Start(start)
            ?? throw new InvalidOperationException("Fixed-pool child did not start.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            Require(
                child.WaitForExit(10000),
                "Fixed-pool regression child exits within unchanged ten-second bound."
            );
            var text = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            Require(child.ExitCode == 0, "Fixed-pool actual writer regression passed: " + text);
            using var result = JsonDocument.Parse(File.ReadAllText(output));
            Require(
                result.RootElement.GetProperty("normal_completion").GetBoolean()
                    && !result.RootElement.GetProperty("starved_after_durable_commit").GetBoolean()
                    && result.RootElement.GetProperty("aborted_callbacks").GetInt32() == 0,
                "Child pool changes remain isolated while actual product acknowledgement progresses normally."
            );
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(5000);
            }
            if (File.Exists(output))
                File.Delete(output);
        }
    }

    private static object PoolStatus()
    {
        ThreadPool.GetAvailableThreads(out var availableWorkers, out var availableIo);
        ThreadPool.GetMinThreads(out var minimumWorkers, out _);
        ThreadPool.GetMaxThreads(out var maximumWorkers, out _);
        return new
        {
            threads = ThreadPool.ThreadCount,
            pending_items = ThreadPool.PendingWorkItemCount,
            completed_items = ThreadPool.CompletedWorkItemCount,
            available_workers = availableWorkers,
            available_io = availableIo,
            minimum_workers = minimumWorkers,
            maximum_workers = maximumWorkers,
        };
    }

    private static void ActualLoggerDelivery()
    {
        using var fixture = new Fixture();
        using var unity = new ManualLogSource("Unity");
        var preceding = BepInEx.Logging.Logger.Listeners.ToArray();
        var received = new List<LogEventArgs>();
        using var normal = new NormalListener(received.Add);
        try
        {
            BepInEx.Logging.Logger.Listeners.Clear();
            BepInEx.Logging.Logger.Listeners.Add(normal);
            BepInEx.Logging.Logger.Listeners.Add(fixture.Bridge);
            BepInEx.Logging.Logger.Sources.Add(fixture.Source);
            BepInEx.Logging.Logger.Sources.Add(unity);
            fixture.Source.LogWarning((object)"actual forwarded warning");
            fixture.Source.LogWarning((object)"actual forwarded warning");
            fixture.Source.LogError((object)"actual forwarded error");
            fixture.Source.LogError((object)"actual forwarded error");
            unity.LogError((object)MaterialNre.StandardMessage);
            unity.LogError((object)MaterialNre.StandardMessage);
            Require(
                received.Count == 6
                    && !received[0].SuppressConsole
                    && received[1].SuppressConsole
                    && !received[2].SuppressConsole
                    && !received[3].SuppressConsole
                    && received.Take(4).All(args => ReferenceEquals(args.Source, fixture.Source))
                    && received
                        .Skip(4)
                        .All(args =>
                            ReferenceEquals(args.Source, unity)
                            && ReferenceEquals(args.Data, MaterialNre.StandardMessage)
                            && args.Level == LogLevel.Error
                            && !args.SuppressConsole
                        ),
                "Actual Bep logger acknowledges repeated warning before ordinary sinks while preserving original source and all errors."
            );
        }
        finally
        {
            BepInEx.Logging.Logger.Sources.Remove(fixture.Source);
            BepInEx.Logging.Logger.Sources.Remove(unity);
            BepInEx.Logging.Logger.Listeners.Clear();
            foreach (var listener in preceding)
                BepInEx.Logging.Logger.Listeners.Add(listener);
        }
    }

    private static void NestedAcknowledgement(bool knownError = false)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var fixture = new Fixture(
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (action != StorageAction.Acknowledge)
                        return;
                    entered.Set();
                    release.Wait();
                },
            }
        );
        using var unity = new ManualLogSource("Unity");
        var source = knownError ? unity : fixture.Source;
        var message = knownError ? MaterialNre.StandardMessage : "nested warning";
        var level = knownError ? LogLevel.Error : LogLevel.Warning;
        var first = new LogEventArgs(message, level, source);
        var repeat = new LogEventArgs(message, level, source);
        Task? task = null;
        try
        {
            task = Task.Run(() =>
                Plugin.Observe(
                    "warning_outer_fixture",
                    () =>
                    {
                        fixture.Bridge.LogEvent(source, first);
                        fixture.Bridge.LogEvent(source, repeat);
                        fixture.Log("different nested warning");
                        Require(
                            !repeat.SuppressConsole,
                            "A nested repeat cannot suppress console before its outer durable acknowledgement."
                        );
                    }
                )
            );
            Require(
                entered.Wait(TimeSpan.FromSeconds(5)),
                "Nested warning batch reaches held ACK."
            );
            using var status = JsonDocument.Parse(JsonSerializer.Serialize(fixture.Bridge.Status));
            Require(
                status
                    .RootElement.GetProperty(
                        knownError ? "error_aggregation" : "warning_aggregation"
                    )
                    .GetProperty("occurrences_durable")
                    .GetInt64() == 0
                    && !repeat.SuppressConsole,
                "Held outer ACK cannot publish durable counts or console suppression prematurely."
            );
            release.Set();
            task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(
                repeat.SuppressConsole == !knownError && !first.SuppressConsole,
                "Nested commit actions run after outer ACK; Error stays console-visible."
            );
        }
        finally
        {
            release.Set();
            task?.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private static void OutOfOrderCommitCallbacks(bool knownError = false)
    {
        var aggregation = new WarningAggregation(
            knownError ? DiagnosticFamily.Error : DiagnosticFamily.Warning
        );
        var firstTime = DateTimeOffset.Parse("2026-10-14T00:00:00Z");
        var first = new CaptureDiagnostic(
            knownError ? "Unity" : "Synthetic",
            knownError ? "Error" : "Warning",
            knownError ? MaterialNre.StandardMessage : "same warning",
            null,
            null,
            "test",
            firstTime,
            1,
            "1"
        );
        var firstCommitted = aggregation.Stage(first, "Official", (_, _) => { }, () => { });
        var repeatTime = firstTime.AddSeconds(knownError ? -1 : 1);
        var repeatCommitted = aggregation.Stage(
            first with
            {
                event_id = "2",
                utc = repeatTime,
            },
            "Official",
            (_, _) => { },
            () => { }
        );
        repeatCommitted();
        firstCommitted();
        firstCommitted();
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize<object>(
                knownError
                    ? aggregation.ErrorSummaries().Single()
                    : aggregation.Summaries().Single()
            )
        );
        var summary = document.RootElement;
        Require(
            summary.GetProperty("occurrences").GetInt64() == 2
                && summary.GetProperty("first_utc").GetDateTimeOffset()
                    == (knownError ? repeatTime : firstTime)
                && summary.GetProperty("last_utc").GetDateTimeOffset()
                    == (knownError ? firstTime : repeatTime),
            "Out-of-order/idempotent commit callbacks retain the latest acknowledged cumulative count."
        );
    }

    private static void BootstrapWarningTransfer(bool knownError = false)
    {
        using var fixture = new Fixture();
        using var unity = new ManualLogSource("Unity");
        var message = knownError ? MaterialNre.StandardMessage : "bootstrap warning";
        var level = knownError ? LogLevel.Error : LogLevel.Warning;
        var source = knownError ? unity : fixture.Source;
        fixture.Origin = "Unknown";
        fixture.Log(message, level, source);
        fixture.Origin = "Official";
        fixture.Log(knownError ? message : "qualified warning", level, source);
        fixture.Origin = "Unknown";
        fixture.Log(message, level, source);
        fixture.Stop();
        var rows = fixture.ReadRows(includeBootstrap: true);
        var anchor = rows.Single(row =>
            Kind(row) == "runtime_log"
            && Data(row).GetProperty(knownError ? "error_origin" : "warning_origin").GetString()
                == "Unknown"
        );
        var repeat = rows.Single(row =>
            Kind(row) == (knownError ? "runtime_error_repeat" : "runtime_warning_repeat")
        );
        var idField = knownError ? "error_id" : "warning_id";
        Require(
            Data(anchor).GetProperty(knownError ? "error_origin" : "warning_origin").GetString()
                == "Unknown"
                && Data(repeat).GetProperty("origin").GetString() == "Unknown"
                && Data(anchor).GetProperty(idField).GetString()
                    == Data(repeat).GetProperty(idField).GetString()
                && Data(repeat).GetProperty("occurrences").GetInt64() == 2,
            "Unknown warning anchor survives bootstrap transfer; later repeats retain the original Unknown scope."
        );
    }

    private static void PartialCopyCommit(bool knownError = false)
    {
        using var fixture = new Fixture();
        Action? committed = null;
        var suppressed = false;
        var when = DateTimeOffset.UtcNow;
        var first = new CaptureDiagnostic(
            knownError ? "Unity" : "Synthetic",
            knownError ? "Error" : "Warning",
            knownError ? MaterialNre.StandardMessage : "partial batch warning",
            null,
            null,
            "test",
            when,
            1,
            "7001"
        );
        Plugin.Observe(
            "partial_warning_first",
            () =>
            {
                committed = Stage(
                    first,
                    "Official",
                    (kind, data) =>
                        fixture.Writer.Emit(false, false, kind, data, systemId: "Recorder"),
                    () => { }
                );
            },
            committed: () => committed?.Invoke()
        );
        committed = null;
        Plugin.Observe(
            "partial_warning_repeat",
            () =>
            {
                committed = Stage(
                    first with
                    {
                        event_id = "7002",
                        utc = when.AddSeconds(1),
                    },
                    "Official",
                    (kind, data) =>
                        fixture.Writer.Emit(false, false, kind, data, systemId: "Recorder"),
                    () => suppressed = true
                );
                throw new InvalidDataException(
                    "Synthetic copy failure after warning record staging."
                );
            },
            committed: () => committed?.Invoke(),
            partialCommitted: () => committed?.Invoke()
        );
        fixture.Stop();
        var rows = fixture.ReadRows();
        Require(
            suppressed == !knownError
                && Data(
                        rows.Single(row =>
                            Kind(row)
                            == (knownError ? "runtime_error_summary" : "runtime_warning_summary")
                        )
                    )
                    .GetProperty("occurrences")
                    .GetInt64() == 2
                && rows.Any(row => Kind(row) == "observer_error"),
            "A successfully staged warning count still commits after a later copy fault and durable batch ACK."
        );

        Action Stage(
            CaptureDiagnostic record,
            string origin,
            Action<string, object> emit,
            Action suppress
        ) =>
            knownError
                ? fixture.Bridge.StageError(record, origin, emit)
                : fixture.Bridge.StageWarning(record, origin, emit, suppress);
    }

    private static void ShutdownAcknowledgement(bool knownError = false)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var acknowledgements = 0;
        using var fixture = new Fixture(
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (
                        action != StorageAction.Acknowledge
                        || Interlocked.Increment(ref acknowledgements) != 2
                    )
                        return;
                    entered.Set();
                    release.Wait();
                },
            }
        );
        using var unity = new ManualLogSource("Unity");
        var source = knownError ? unity : fixture.Source;
        var message = knownError ? MaterialNre.StandardMessage : "warning during close";
        var level = knownError ? LogLevel.Error : LogLevel.Warning;
        var summaryKind = knownError ? "runtime_error_summary" : "runtime_warning_summary";
        fixture.Log(message, level, source);
        var repeat = new LogEventArgs(message, level, source);
        Task? log = null,
            stop = null;
        try
        {
            log = Task.Run(() => fixture.Bridge.LogEvent(source, repeat));
            Require(
                entered.Wait(TimeSpan.FromSeconds(5)),
                "Repeated warning waits at durable ACK."
            );
            stop = Task.Run(() => Plugin.StopDiagnosticCapture(fixture.Bridge));
            Require(
                SpinWait.SpinUntil(
                    () =>
                    {
                        using var json = JsonDocument.Parse(
                            JsonSerializer.Serialize(fixture.Bridge.Status)
                        );
                        return json.RootElement.GetProperty("admission_closed_utc").ValueKind
                            != JsonValueKind.Null;
                    },
                    TimeSpan.FromSeconds(5)
                ),
                "Diagnostic stop seals admission before drain."
            );
            Require(
                !stop.IsCompleted && !repeat.SuppressConsole,
                "Stop waits for admitted repeat ACK."
            );
            var after = fixture.Log(message, level, source);
            Require(
                !after.SuppressConsole,
                "A warning beyond the frontier is not falsely captured/suppressed."
            );
            release.Set();
            Task.WhenAll(log, stop).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            fixture.Stop(alreadyStopped: true);
            var rows = fixture.ReadRows();
            var summary = rows.Single(row => Kind(row) == summaryKind);
            Require(
                Data(summary).GetProperty("occurrences").GetInt64() == 2
                    && Array.FindIndex(rows, row => Kind(row) == summaryKind)
                        < Array.FindIndex(rows, row => Kind(row) == "log_capture_stop")
                    && repeat.SuppressConsole == !knownError,
                "Final warning summary is durable after admitted callbacks and before stop marker."
            );
        }
        finally
        {
            release.Set();
            if (log is not null && stop is not null)
                Task.WhenAll(log, stop).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private static void FailedAcknowledgement(bool knownError = false)
    {
        var fail = false;
        using var blocked = new ManualResetEventSlim();
        using var fixture = new Fixture(
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (!Volatile.Read(ref fail) || action != StorageAction.Acknowledge)
                        return;
                    blocked.Set();
                    throw new IOException("Synthetic repeated-warning acknowledgement failure.");
                },
            }
        );
        using var unity = new ManualLogSource("Unity");
        var source = knownError ? unity : fixture.Source;
        var message = knownError ? MaterialNre.StandardMessage : "warning before abort";
        var level = knownError ? LogLevel.Error : LogLevel.Warning;
        fixture.Log(message, level, source);
        Volatile.Write(ref fail, true);
        var repeat = new LogEventArgs(message, level, source);
        var task = Task.Run(() => fixture.Bridge.LogEvent(source, repeat));
        try
        {
            Require(blocked.Wait(TimeSpan.FromSeconds(5)), "Repeat ACK failure is observed.");
            Require(
                !repeat.SuppressConsole,
                "A failed/unconfirmed repeat never suppresses console."
            );
            fixture.Writer.Abort("Synthetic warning repeat forced abort.");
            task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            using var status = JsonDocument.Parse(JsonSerializer.Serialize(fixture.Bridge.Status));
            var warnings = status.RootElement.GetProperty(
                knownError ? "error_aggregation" : "warning_aggregation"
            );
            Require(
                warnings.GetProperty("occurrences_durable").GetInt64() == 1
                    && warnings.GetProperty("pending_occurrences").GetInt64() == 1
                    && status.RootElement.GetProperty("capture_failures").GetInt64() == 1
                    && !repeat.SuppressConsole,
                "Abort leaves the unacknowledged increment pending and capture explicitly incomplete."
            );
        }
        finally
        {
            fixture.Writer.Abort("Synthetic warning fixture cleanup.");
            task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    private static string Kind(JsonElement row) => row.GetProperty("kind").GetString()!;

    private static JsonElement Data(JsonElement row)
    {
        var data = row.GetProperty("data");
        return data.TryGetProperty("scope", out _) ? data.GetProperty("data") : data;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SessionWriter priorWriter = Plugin.Writer;
        private readonly string root = Path.Combine(
            Path.GetTempPath(),
            "warning-check-" + Guid.NewGuid().ToString("N")
        );
        private readonly List<string> reports = [];
        private bool stopped;
        internal readonly ManualLogSource Source = new("SyntheticWarning");
        internal readonly SessionWriter Writer;
        internal readonly DiagnosticBridge Bridge;
        internal string Origin = "Official";

        internal Fixture(StorageHooks? hooks = null)
        {
            DiagnosticBridge? bridge = null;
            Writer = new SessionWriter(
                root,
                new { synthetic = true },
                _ => new { complete = true },
                message =>
                {
                    lock (reports)
                        reports.Add(message);
                },
                hooks
            );
            Plugin.Writer = Writer;
            bridge = new DiagnosticBridge(
                record =>
                    Plugin.Observe(
                        "warning_diagnostic_fixture",
                        () => Emit("runtime_log", record, Origin),
                        diagnostic: true
                    ),
                new OperationalStatus(),
                (record, suppress) =>
                {
                    Action? commit = null;
                    Plugin.Observe(
                        "warning_fixture",
                        () =>
                        {
                            var origin = Origin;
                            commit = bridge!.StageWarning(
                                record,
                                origin,
                                (kind, data) => Emit(kind, data, origin),
                                suppress
                            );
                        },
                        committed: () => commit?.Invoke(),
                        partialCommitted: () => commit?.Invoke(),
                        diagnostic: true
                    );
                },
                record =>
                {
                    Action? commit = null;
                    Plugin.Observe(
                        "material_error_fixture",
                        () =>
                        {
                            var origin = Origin;
                            commit = bridge!.StageError(
                                record,
                                origin,
                                (kind, data) => Emit(kind, data, origin)
                            );
                        },
                        committed: () => commit?.Invoke(),
                        partialCommitted: () => commit?.Invoke(),
                        diagnostic: true
                    );
                }
            );
            Bridge = bridge;
        }

        private void Emit(string kind, object data, string origin)
        {
            if (origin == "Unknown")
                Writer.EmitBootstrap(kind, data);
            else
                Writer.Emit(origin == "Private", false, kind, data, systemId: "Recorder");
        }

        internal LogEventArgs Log(
            string message,
            LogLevel level = LogLevel.Warning,
            ManualLogSource? source = null
        )
        {
            var args = new LogEventArgs(message, level, source ?? Source);
            Bridge.LogEvent(source ?? Source, args);
            return args;
        }

        internal void Stop(bool alreadyStopped = false)
        {
            if (stopped)
                return;
            if (!alreadyStopped)
                Plugin.StopDiagnosticCapture(Bridge);
            Writer.Dispose();
            using var status = JsonDocument.Parse(JsonSerializer.Serialize(Writer.GetStatus()));
            Require(
                status.RootElement.GetProperty("capture_zip_complete").GetBoolean(),
                "Complete warning fixture publishes a verified ZIP: " + string.Join("; ", reports)
            );
            stopped = true;
        }

        internal JsonElement[] ReadRows(bool includeBootstrap = false)
        {
            var archives = Directory
                .EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
                .ToArray();
            Require(
                archives.Length != 0,
                "Synthetic warning fixture archives successfully: " + string.Join("; ", reports)
            );
            var rows = new List<JsonElement>();
            foreach (var zipPath in archives)
            {
                using var zip = ZipFile.OpenRead(zipPath);
                using var json = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                var stamp = json
                    .RootElement.GetProperty("entries")
                    .EnumerateArray()
                    .Single(entry => entry.GetProperty("name").GetString() == "diagnostics.jsonl");
                using var input = new StreamReader(zip.GetEntry("diagnostics.jsonl")!.Open());
                var current = new List<JsonElement>();
                while (input.ReadLine() is { } line)
                {
                    using var record = JsonDocument.Parse(line);
                    current.Add(record.RootElement.Clone());
                }
                Require(
                    stamp.GetProperty("records").GetInt64() == current.Count,
                    "Packaged warning record counts match actual JSONL."
                );
                var occurrences = current.Sum(SavedWarningOccurrence);
                var warningSummary = json.RootElement.GetProperty("warning_summary");
                Require(
                    occurrences == 0
                        ? warningSummary.ValueKind == JsonValueKind.Null
                        : warningSummary.ValueKind == JsonValueKind.Object
                            && warningSummary.GetProperty("occurrences").GetString()
                                == occurrences.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                    "Archive warning occurrence count is reconstructed from anchors and repeat deltas, separately from JSONL record count."
                );
                var errorOccurrences = current.Sum(row =>
                    SavedOccurrence(row, "error_id", "runtime_error_repeat")
                );
                var errorSummary = json.RootElement.GetProperty("error_summary");
                Require(
                    errorOccurrences == 0
                        ? errorSummary.ValueKind == JsonValueKind.Null
                        : errorSummary.ValueKind == JsonValueKind.Object
                            && errorSummary.GetProperty("occurrences").GetString()
                                == errorOccurrences.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                    "Archive material error count is reconstructed from saved full anchors and repeat ordinals."
                );
                foreach (var row in current)
                {
                    if (Kind(row) == "process_bootstrap")
                    {
                        if (includeBootstrap)
                        {
                            var original = row.GetProperty("data").GetProperty("original_record");
                            if (original.TryGetProperty("data", out _))
                                rows.Add(original.Clone());
                        }
                    }
                    else if (row.TryGetProperty("data", out _))
                        rows.Add(row);
                }
            }
            return rows.ToArray();
        }

        private static long SavedWarningOccurrence(JsonElement row) =>
            SavedOccurrence(row, "warning_id", "runtime_warning_repeat");

        private static long SavedOccurrence(JsonElement row, string idField, string repeatKind)
        {
            if (Kind(row) == "process_bootstrap")
                row = row.GetProperty("data").GetProperty("original_record");
            if (
                Kind(row) != "runtime_log" && Kind(row) != repeatKind
                || !row.TryGetProperty("data", out _)
            )
                return 0;
            var data = Data(row);
            if (Kind(row) == repeatKind)
                return repeatKind == "runtime_error_repeat"
                    ? 1
                    : data.GetProperty("delta").GetInt64();
            return
                Kind(row) == "runtime_log"
                && data.TryGetProperty(idField, out var id)
                && id.ValueKind == JsonValueKind.String
                ? 1
                : 0;
        }

        public void Dispose()
        {
            try
            {
                using var state = JsonDocument.Parse(JsonSerializer.Serialize(Writer.GetStatus()));
                if (state.RootElement.GetProperty("aborted").GetBoolean())
                {
                    Bridge.Stop();
                    Writer.Dispose();
                    Require(
                        SpinWait.SpinUntil(
                            () =>
                            {
                                using var json = JsonDocument.Parse(
                                    JsonSerializer.Serialize(Writer.GetStatus())
                                );
                                return json.RootElement.GetProperty("completed").GetBoolean();
                            },
                            TimeSpan.FromSeconds(5)
                        ),
                        "Aborted warning fixture drains before cleanup."
                    );
                }
                else
                    Stop();
            }
            finally
            {
                Bridge.Dispose();
                Source.Dispose();
                Plugin.Writer = priorWriter;
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class NormalListener(Action<LogEventArgs> receive) : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs args) => receive(args);

        public void Dispose() { }
    }
}

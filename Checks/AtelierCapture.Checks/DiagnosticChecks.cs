using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AtelierCapture.Storage;
using BepInEx.Logging;

namespace AtelierCapture.Checks;

internal static class DiagnosticChecks
{
    internal static void Run()
    {
        CheckBridge();
        WarningAggregationChecks.Run();
        CheckLoggerFanout();
        CheckOriginalLogs();
        CheckCertificates();
        CheckNativeBodyGate();
        CheckControl();
        CheckObservation();
        CheckDurableGate();
        CheckLifecycle();
        CheckNativeLifetime();
        CheckIndependentControl();
        CheckShutdownDiagnostics();
        CheckQuitSubscription();
        NativeObserverHandlerChecks.Run();
    }

    private static void CheckBridge()
    {
        var records = new List<CaptureDiagnostic>();
        DiagnosticBridge? bridge = null;
        using var source = new ManualLogSource("SyntheticUnity");
        var operational = new OperationalStatus(message =>
            bridge!.LogEvent(source, new LogEventArgs(message, LogLevel.Warning, source))
        );
        bridge = new DiagnosticBridge(record => records.Add(record), operational);
        var exception = RaisedException();
        bridge.LogEvent(source, new LogEventArgs(exception, LogLevel.Error, source));
        Require(
            records.Count == 1
                && records[0].severity == "Error"
                && records[0].source == source.SourceName,
            "Original diagnostic source and severity survive."
        );
        Require(
            records[0].message.Contains("Synthetic diagnostic failure", StringComparison.Ordinal)
                && records[0].stack?.Contains(nameof(RaisedException), StringComparison.Ordinal)
                    == true,
            "Original exception stack survives."
        );
        operational.Report("Synthetic writer hold; this status is explicitly unsaved.");
        Require(
            records.Count == 1
                && JsonSerializer
                    .Serialize(operational.Snapshot)
                    .Contains("\"unsaved_operational_messages\":1", StringComparison.Ordinal),
            "Operational logger bypass cannot re-enter capture or self-wait."
        );
        bridge.Dispose();
        bridge.LogEvent(source, new LogEventArgs("after closure", LogLevel.Info, source));
        Require(records.Count == 1, "Detached listener stops callbacks after closure.");

        var recursive = new OperationalStatus();
        DiagnosticBridge? recursiveBridge = null;
        var cycle = new LogEventArgs("recorder-induced same-event cycle", LogLevel.Error, source);
        recursiveBridge = new DiagnosticBridge(
            _ => recursiveBridge!.LogEvent(source, cycle),
            recursive
        );
        recursiveBridge.LogEvent(source, cycle);
        Require(
            JsonSerializer
                .Serialize(recursive.Snapshot)
                .Contains("\"unsaved_operational_messages\":1", StringComparison.Ordinal),
            "Recorder-induced recursion is explicitly counted rather than looping."
        );
        recursiveBridge.Dispose();
        var nestedRecords = new List<CaptureDiagnostic>();
        DiagnosticBridge? nestedBridge = null;
        nestedBridge = new DiagnosticBridge(
            record =>
            {
                nestedRecords.Add(record);
                if (record.message == "outer game event")
                    nestedBridge!.LogEvent(
                        source,
                        new LogEventArgs("nested game event", LogLevel.Warning, source)
                    );
            },
            new OperationalStatus()
        );
        nestedBridge.LogEvent(source, new LogEventArgs("outer game event", LogLevel.Error, source));
        Require(
            nestedRecords.Count == 2 && nestedRecords[1].message == "nested game event",
            "Distinct nested game diagnostics are preserved rather than treated as recursion."
        );
        nestedBridge.Dispose();
        var failedSinkStatus = new OperationalStatus();
        using var failedSinkBridge = new DiagnosticBridge(
            _ => throw new IOException("Synthetic diagnostic persistence failure"),
            failedSinkStatus
        );
        failedSinkBridge.LogListenerFailure(
            new CallbackListener((_, _) => { }),
            RaisedException(),
            cycle
        );
        using (var status = JsonDocument.Parse(JsonSerializer.Serialize(failedSinkBridge.Status)))
            Require(
                status.RootElement.GetProperty("sink_failures").GetInt64() == 1
                    && status.RootElement.GetProperty("capture_failures").GetInt64() == 1
                    && !status.RootElement.GetProperty("complete").GetBoolean(),
                "A sink-failure diagnostic that cannot persist remains explicitly incomplete and counted without recursive logging."
            );

        using var displayEntered = new ManualResetEventSlim();
        using var releaseDisplay = new ManualResetEventSlim();
        using var displayDone = new ManualResetEventSlim();
        var duringDisplay = new ConcurrentQueue<CaptureDiagnostic>();
        DiagnosticBridge? displayBridge = null;
        var displayCalls = 0;
        var heldStatus = new OperationalStatus(message =>
        {
            Interlocked.Increment(ref displayCalls);
            displayEntered.Set();
            releaseDisplay.Wait();
            displayBridge!.LogEvent(source, new LogEventArgs(message, LogLevel.Warning, source));
            displayBridge.LogEvent(
                source,
                new LogEventArgs(
                    "Genuine native error during recorder status display",
                    LogLevel.Error,
                    source
                )
            );
            displayDone.Set();
        });
        displayBridge = new DiagnosticBridge(duringDisplay.Enqueue, heldStatus);
        try
        {
            Task.Run(() => heldStatus.Report("Synthetic blocked status display"))
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            Require(
                displayEntered.Wait(TimeSpan.FromSeconds(5)),
                "Asynchronous status display starts."
            );
            Task.Run(() =>
                {
                    for (var i = 0; i < 1000; i++)
                        heldStatus.Report("Synthetic repeated storage hold");
                })
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            Require(
                displayCalls == 1,
                "A blocked status sink cannot queue unbounded display work or block writer/control reporters."
            );
            Require(
                JsonSerializer
                    .Serialize(heldStatus.Snapshot)
                    .Contains("\"unsaved_operational_messages\":1001", StringComparison.Ordinal),
                "Every operational report remains counted while display is bounded."
            );
            releaseDisplay.Set();
            Require(
                displayDone.Wait(TimeSpan.FromSeconds(5))
                    && duringDisplay.Count == 1
                    && duringDisplay
                        .Single()
                        .message.StartsWith("Genuine native error", StringComparison.Ordinal),
                "Only the recorder's exact message bypasses capture; a genuine nested native error remains admitted."
            );
        }
        finally
        {
            releaseDisplay.Set();
            displayBridge.Dispose();
        }
        var failedDisplay = new OperationalStatus(_ =>
            throw new IOException("Synthetic console sink failure")
        );
        failedDisplay.Report("Synthetic unsaved operational status");
        Require(
            SpinWait.SpinUntil(
                () =>
                {
                    using var status = JsonDocument.Parse(
                        JsonSerializer.Serialize(failedDisplay.Snapshot)
                    );
                    return status.RootElement.GetProperty("operational_display_failures").GetInt64()
                        == 1;
                },
                TimeSpan.FromSeconds(5)
            ),
            "A failed operational display remains explicitly counted without re-entering the logger."
        );
    }

    private static Exception RaisedException()
    {
        try
        {
            throw new InvalidOperationException("Synthetic diagnostic failure");
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void CheckLoggerFanout()
    {
        var prior = Plugin.Writer;
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-log-fanout-" + Guid.NewGuid().ToString("N")
        );
        var listeners = BepInEx.Logging.Logger.Listeners.ToArray();
        using var writer = new SessionWriter(
            path,
            new { synthetic = true },
            _ => new { },
            _ => { }
        );
        using var source = BepInEx.Logging.Logger.CreateLogSource("SyntheticActualBepLogger");
        var records = new List<CaptureDiagnostic>();
        var originalAcknowledged = false;
        var ordinaryDelivered = 0;
        var broken = new CallbackListener(
            (_, _) =>
            {
                Require(
                    originalAcknowledged,
                    "Priority capture acknowledges the original game log before an ordinary sink is invoked."
                );
                throw new IOException("Synthetic disk log sink storage failure.");
            }
        );
        using var bridge = new DiagnosticBridge(
            record =>
            {
                Plugin.Observe(
                    "diagnostic_fanout",
                    () => writer.EmitBootstrap("runtime_log", record)
                );
                records.Add(record);
                if (record.source == source.SourceName)
                    originalAcknowledged = true;
            },
            new OperationalStatus()
        );
        var following = new CallbackListener((_, _) => ordinaryDelivered++);
        try
        {
            Plugin.Writer = writer;
            BepInEx.Logging.Logger.Listeners.Clear();
            BepInEx.Logging.Logger.Listeners.Add(broken);
            BepInEx.Logging.Logger.Listeners.Add(bridge);
            BepInEx.Logging.Logger.Listeners.Add(following);
            source.LogError(
                (object)"Synthetic original game error with a failed earlier disk sink"
            );
            Require(
                originalAcknowledged && ordinaryDelivered == 1 && records.Count == 2,
                "Actual BepInEx fanout preserves the original, diagnoses a failed earlier-registered sink and continues following sinks."
            );
            Require(
                records[1].source == "BepInEx.ListenerFailure"
                    && records[1].caused_by_event_id == records[0].event_id
                    && records[1].failed_listener == typeof(CallbackListener).FullName
                    && records[1]
                        .message.Contains(
                            "Synthetic disk log sink storage failure",
                            StringComparison.Ordinal
                        ),
                "Sink failure retains its exception and exact link to the already acknowledged original."
            );
            Require(
                JsonSerializer
                    .Serialize(bridge.Status)
                    .Contains("\"sink_failures\":1", StringComparison.Ordinal),
                "Isolated log sink failures remain explicitly counted."
            );
        }
        finally
        {
            BepInEx.Logging.Logger.Listeners.Clear();
            foreach (var listener in listeners)
                BepInEx.Logging.Logger.Listeners.Add(listener);
            BepInEx.Logging.Logger.Sources.Remove(source);
            writer.Dispose();
            Plugin.Writer = prior;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private sealed class CallbackListener(Action<object, LogEventArgs> receive) : ILogListener
    {
        public LogLevel LogLevelFilter => LogLevel.All;

        public void LogEvent(object sender, LogEventArgs args) => receive(sender, args);

        public void Dispose() { }
    }

    private static void CheckCertificates()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "capture-certificate-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "runtime.bin");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            using var certificate = JsonDocument.Parse(
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            path = "runtime.bin",
                            sha256 = hash,
                            size = 4,
                        },
                    }
                )
            );
            Require(
                RuntimeCompatibility.VerifyRuntimeFiles(root, certificate.RootElement).Count == 1,
                "Exact runtime certificate has one verified input."
            );
            File.WriteAllBytes(path, [1, 2, 3, 5]);
            Reject(
                () => RuntimeCompatibility.VerifyRuntimeFiles(root, certificate.RootElement),
                "runtime_file_hash"
            );
            Reject(
                () => RuntimeCompatibility.ContainedPath(root, "../outside.bin"),
                "runtime_file_path"
            );
            using var duplicate = JsonDocument.Parse(
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            path = "runtime.bin",
                            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                            size = 4,
                        },
                        new
                        {
                            path = "runtime.bin",
                            sha256 = hash,
                            size = 4,
                        },
                    }
                )
            );
            Reject(
                () => RuntimeCompatibility.VerifyRuntimeFiles(root, duplicate.RootElement),
                "duplicate_runtime_file"
            );
            Directory.CreateDirectory(Path.Combine(root, "BepInEx", "config"));
            var configPath = Path.Combine(root, "BepInEx", "config", "BepInEx.cfg");
            using var configContract = JsonDocument.Parse(
                JsonSerializer.Serialize(
                    new[]
                    {
                        new
                        {
                            path = "BepInEx/config/BepInEx.cfg",
                            section = "IL2CPP",
                            key = "UnityBaseLibrariesSource",
                            value = "2022.3.62.zip",
                        },
                        new
                        {
                            path = "BepInEx/config/BepInEx.cfg",
                            section = "IL2CPP",
                            key = "GlobalMetadataPath",
                            value = "{GameDataPath}/il2cpp_data/Metadata/global-metadata.dat",
                        },
                        new
                        {
                            path = "BepInEx/config/BepInEx.cfg",
                            section = "IL2CPP",
                            key = "IL2CPPInteropAssembliesPath",
                            value = "{BepInEx}",
                        },
                        new
                        {
                            path = "BepInEx/config/AtelierResleriana.Plugin.Localization.cfg",
                            section = "Data",
                            key = "AutoUpdate",
                            value = "false",
                        },
                    }
                )
            );
            File.WriteAllText(
                configPath,
                "[IL2CPP]\nUnityBaseLibrariesSource = 2022.3.62.zip\nGlobalMetadataPath = {GameDataPath}/il2cpp_data/Metadata/global-metadata.dat\nIL2CPPInteropAssembliesPath = {BepInEx}\n[Logging.Disk]\nWriteUnityLog = false\n"
            );
            var localizationPath = Path.Combine(
                root,
                "BepInEx",
                "config",
                "AtelierResleriana.Plugin.Localization.cfg"
            );
            File.WriteAllText(
                localizationPath,
                "[Data]\nAutoUpdate = false\n[General]\nLanguage = en\n"
            );
            var firstConfig = JsonSerializer.Serialize(
                RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement)
            );
            File.AppendAllText(configPath, "[Logging.Console]\nEnabled = true\n");
            Require(
                JsonSerializer.Serialize(
                    RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement)
                ) != firstConfig,
                "Mutable user logging preferences remain accepted and their actual config identity is recorded."
            );
            File.WriteAllText(configPath, "[IL2CPP]\nUnityBaseLibrariesSource = unsupported.zip\n");
            Reject(
                () => RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement),
                "offline_unity_source"
            );
            File.WriteAllText(
                configPath,
                "[IL2CPP]\nUnityBaseLibrariesSource = 2022.3.62.zip\nUnityBaseLibrariesSource = 2022.3.62.zip\n"
            );
            Reject(
                () => RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement),
                "offline_unity_source_missing_or_duplicate"
            );
            File.WriteAllText(
                configPath,
                "[IL2CPP]\nUnityBaseLibrariesSource = 2022.3.62.zip\nGlobalMetadataPath = unsupported\nIL2CPPInteropAssembliesPath = {BepInEx}\n"
            );
            Reject(
                () => RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement),
                "interop_input_configuration"
            );
            File.WriteAllText(
                configPath,
                "[IL2CPP]\nUnityBaseLibrariesSource = 2022.3.62.zip\nGlobalMetadataPath = {GameDataPath}/il2cpp_data/Metadata/global-metadata.dat\nIL2CPPInteropAssembliesPath = {BepInEx}\n"
            );
            File.AppendAllText(localizationPath, "DialogueFontSize = 36\n");
            RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement);
            File.WriteAllText(localizationPath, "[Data]\nAutoUpdate = true\n");
            Reject(
                () => RuntimeCompatibility.VerifyConfiguration(root, configContract.RootElement),
                "localization_data_auto_update"
            );
            var blocked = RuntimeCompatibility.Inspect(root, Path.Combine(root, "absent.json"));
            Require(
                !blocked.ApiVerified && !blocked.ClientVerified,
                "Missing runtime/client proof blocks hook activation."
            );
            Require(
                RuntimeCompatibility.SignatureType(typeof(Dictionary<string, List<int>>))
                    == "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32>>",
                "Native canonical signatures preserve every constructed argument."
            );
            Reject(
                () =>
                    blocked.ValidateMethod(
                        typeof(DiagnosticChecks).GetMethod(
                            nameof(Run),
                            System.Reflection.BindingFlags.NonPublic
                                | System.Reflection.BindingFlags.Static
                        )!,
                        1,
                        "System.Void",
                        [],
                        true
                    ),
                "runtime_not_verified"
            );
            if (OperatingSystem.IsWindows())
            {
                var pointer = Marshal.AllocHGlobal(IntPtr.Size);
                try
                {
                    Marshal.WriteIntPtr(pointer, new IntPtr(0x1234));
                    Require(
                        RuntimeCompatibility.ReadNativePointer(pointer) == new IntPtr(0x1234),
                        "Native method pointer verification reads exactly one accessible pointer in this synthetic process."
                    );
                    Reject(
                        () => RuntimeCompatibility.ReadNativePointer(new IntPtr(1)),
                        "unreadable_native_method_pointer"
                    );
                }
                finally
                {
                    Marshal.FreeHGlobal(pointer);
                }
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void CheckControl()
    {
        var run = Guid.NewGuid().ToString("N");
        var aborted = 0;
        var statusCalls = 0;
        using var control = new CaptureControl(
            run,
            () =>
            {
                statusCalls++;
                return new { detached = true };
            },
            _ => aborted++,
            new OperationalStatus(),
            "capture-check-" + Guid.NewGuid().ToString("N")
        );
        byte[] Request(object request) => JsonSerializer.SerializeToUtf8Bytes(request);
        bool Accepted(byte[] response)
        {
            using var data = JsonDocument.Parse(response);
            return data.RootElement.GetProperty("accepted").GetBoolean();
        }
        Require(
            Accepted(
                control.Handle(
                    Request(
                        new
                        {
                            schema = 1,
                            command = "status",
                            run_id = run,
                        }
                    )
                )
            )
                && statusCalls == 1,
            "Detached status uses the requested run."
        );
        Require(
            !Accepted(
                control.Handle(
                    Request(
                        new
                        {
                            schema = 1,
                            command = "abort",
                            run_id = "wrong",
                            reason = "synthetic",
                        }
                    )
                )
            )
                && aborted == 0,
            "Wrong-run abort cannot wake recorder waiters."
        );
        Require(
            !Accepted(control.Handle("{}"u8))
                && !Accepted(control.Handle(new byte[CaptureControl.RequestLimit + 1])),
            "Malformed and oversized control frames are rejected."
        );
        Require(
            !Accepted(
                control.Handle("{\"schema\":1,\"command\":\"status\",\"command\":\"abort\"}"u8)
            ),
            "Duplicate control fields cannot change the command."
        );
        Require(
            Accepted(
                control.Handle(
                    Request(
                        new
                        {
                            schema = 1,
                            command = "abort",
                            run_id = run,
                            reason = "Synthetic explicit abort",
                        }
                    )
                )
            )
                && aborted == 1,
            "Only explicit matching-run abort reaches the recorder."
        );
        if (!OperatingSystem.IsWindows())
            return;
        using var pipe = new NamedPipeClientStream(
            ".",
            control.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        var bytes = Request(
            new
            {
                schema = 1,
                command = "status",
                run_id = run,
            }
        );
        pipe.Write(BitConverter.GetBytes(bytes.Length));
        pipe.Write(bytes);
        var sizeBytes = new byte[4];
        pipe.ReadExactlyAsync(sizeBytes)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        var size = BitConverter.ToInt32(sizeBytes);
        Require(
            size > 0 && size < CaptureControl.ResponseLimit,
            "Current-user pipe returns a bounded status frame."
        );
        var response = new byte[size];
        pipe.ReadExactlyAsync(response)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        Require(
            Accepted(response) && statusCalls == 2,
            "Actual current-user pipe status works without writer/native state."
        );
        pipe.Dispose();
        using (
            var malformed = new NamedPipeClientStream(
                ".",
                control.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous
            )
        )
        {
            malformed.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            malformed.Write(BitConverter.GetBytes(CaptureControl.RequestLimit + 1));
        }
        using var recovered = JsonDocument.Parse(
            SendControl(
                control,
                new
                {
                    schema = 1,
                    command = "status",
                    run_id = run,
                }
            )
        );
        Require(
            recovered.RootElement.GetProperty("accepted").GetBoolean() && statusCalls == 3,
            "An oversized wire frame closes its connection without disabling later valid status/control."
        );
    }

    private static void CheckObservation()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-observation-" + Guid.NewGuid().ToString("N")
        );
        var previous = Plugin.Writer;
        try
        {
            using var writer = new SessionWriter(
                path,
                new { synthetic = true },
                _ => new { observer_errors = new Dictionary<string, long>() },
                _ => { }
            );
            Plugin.Writer = writer;
            var committed = false;
            Plugin.Observe(
                "outer",
                () =>
                {
                    Plugin.Observe(
                        "nested",
                        () => writer.Emit(false, false, "synthetic", new { preserved = true }),
                        () => committed = true
                    );
                    Require(
                        !committed,
                        "Nested callback cannot advance written flags before the outer durable commit."
                    );
                }
            );
            Require(committed, "Nested written flag advances after outer commit.");
            var laterCommit = false;
            Plugin.Observe(
                "commit_fault_isolation",
                () =>
                {
                    Plugin.Observe(
                        "failing_commit",
                        () => writer.Emit(false, false, "synthetic_commit_fault", new { }),
                        () => throw RaisedException()
                    );
                    Plugin.Observe(
                        "later_commit",
                        () => writer.Emit(false, false, "synthetic_commit_after_fault", new { }),
                        () => laterCommit = true
                    );
                }
            );
            Require(
                laterCommit,
                "An acknowledged state-update failure is diagnosed without skipping other already durable records' commit actions."
            );
            var failedCommitted = false;
            Plugin.Observe(
                "synthetic_read_fault",
                () => throw RaisedException(),
                () => failedCommitted = true
            );
            Require(
                !failedCommitted,
                "Read-failure diagnostics do not certify a successful observer."
            );
            using (
                Network.PushContext(
                    new NetworkContext(
                        "private",
                        "private",
                        null,
                        "/synthetic",
                        "Diagnostics",
                        true,
                        null,
                        "synthetic"
                    )
                )
            )
                Plugin.Observe("private_read_fault", () => throw RaisedException());
            using (
                Network.PushContext(
                    new NetworkContext(
                        "official",
                        "official",
                        null,
                        "/synthetic",
                        "Diagnostics",
                        false,
                        null,
                        "synthetic"
                    )
                )
            )
                Plugin.Observe("official_read_fault", () => throw RaisedException());
            Plugin.Observe("unknown_after_known_read_fault", () => throw RaisedException());
            var partialCommitted = false;
            Plugin.Observe(
                "partial_staging_read_fault",
                () =>
                {
                    writer.Emit(false, false, "synthetic_partial", new { preserved = true });
                    throw RaisedException();
                },
                () =>
                    throw new InvalidOperationException(
                        "Incomplete observer success callback ran."
                    ),
                () => partialCommitted = true
            );
            Require(
                partialCommitted,
                "Successfully staged records advance their partial state only after acknowledgement even if a later read fails."
            );
            writer.Dispose();
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var envelope in SavedDiagnostics(path))
            {
                if (
                    !envelope.TryGetProperty("kind", out var kind)
                    || kind.GetString() != "observer_error"
                )
                    continue;
                var data = envelope.GetProperty("data");
                var process =
                    data.TryGetProperty("scope", out var scope) && scope.GetString() == "process";
                if (process)
                    data = data.GetProperty("data");
                found.Add(
                    data.GetProperty("observer").GetString()!,
                    process ? "process" : envelope.GetProperty("environment").GetString()!
                );
            }
            Require(
                found.GetValueOrDefault("private_read_fault") == "Private"
                    && found.GetValueOrDefault("official_read_fault") == "Official"
                    && found.GetValueOrDefault("unknown_after_known_read_fault") == "process",
                "Diagnostic records retain proven ownership; latest environment never fabricates an unknown owner. Actual: "
                    + JsonSerializer.Serialize(found)
                    + "; writer: "
                    + JsonSerializer.Serialize(writer.GetStatus())
            );
        }
        finally
        {
            Plugin.Writer = previous;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private static void CheckDurableGate()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-diagnostic-gate-" + Guid.NewGuid().ToString("N")
        );
        var previous = Plugin.Writer;
        using var ackEntered = new ManualResetEventSlim();
        using var releaseAck = new ManualResetEventSlim();
        using var secondCopied = new ManualResetEventSlim();
        var ackCount = 0;
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
                        || Interlocked.Increment(ref ackCount) != 1
                    )
                        return;
                    ackEntered.Set();
                    releaseAck.Wait();
                },
            }
        );
        try
        {
            Plugin.Writer = writer;
            var committed = 0;
            var first = Task.Run(() =>
                Plugin.Observe(
                    "durability_gate",
                    () =>
                        Plugin.Observe(
                            "nested_durability_gate",
                            () => writer.Emit(false, false, "synthetic", new { retained = true }),
                            () => Interlocked.Exchange(ref committed, 1)
                        )
                )
            );
            Require(
                ackEntered.Wait(TimeSpan.FromSeconds(5)),
                "Observation reaches durable acknowledgement gate."
            );
            var second = Task.Run(() =>
                Plugin.Observe(
                    "copy_while_first_waits",
                    () =>
                    {
                        writer.Emit(false, false, "synthetic", new { independent = true });
                        secondCopied.Set();
                    }
                )
            );
            Require(
                secondCopied.Wait(TimeSpan.FromSeconds(5))
                    && !first.IsCompleted
                    && Volatile.Read(ref committed) == 0,
                "Durable wait releases copier/observation locks while written flags remain uncommitted."
            );
            releaseAck.Set();
            Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(
                Volatile.Read(ref committed) == 1,
                "Written flag advances only after acknowledgement."
            );
        }
        finally
        {
            releaseAck.Set();
            Plugin.Writer = previous;
            writer.Dispose();
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private static void Reject(Action action, string code)
    {
        try
        {
            action();
        }
        catch (RuntimeCompatibilityException error) when (error.Code == code)
        {
            return;
        }
        throw new InvalidOperationException("Expected compatibility rejection: " + code);
    }

    private static void CheckNativeBodyGate()
    {
        RuntimeCompatibility.RequireUniqueNativeBody(0x9A67A0);
        RuntimeCompatibility.RequireUniqueNativeBody(0x4BBB3D0);
        Reject(
            () => RuntimeCompatibility.RequireUniqueNativeBody(0x8C8F40),
            "shared_native_body_unqualified"
        );
        Reject(
            () => RuntimeCompatibility.RequireUniqueNativeBody(0x8C9060),
            "shared_native_body_unqualified"
        );
        Reject(
            () => RuntimeCompatibility.RequireUniqueNativeBody(1),
            "native_body_inventory_missing"
        );
        using var resource = typeof(Plugin).Assembly.GetManifestResourceStream(
            "AtelierCapture.NativeHookBodies.json"
        )!;
        var inventory = NativeHookBodies.Parse(resource);
        Require(
            inventory[0x8C8F40] == 659 && inventory[0x8C9060] == 135,
            "Shared native setters retain their global alias evidence and cannot be detoured through a correct-looking managed wrapper."
        );
        using var changedClient = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schema = 1,
                    gameassembly_sha256 = new string('0', 64),
                    metadata_sha256 = RuntimeCompatibility.MetadataHash,
                    bodies = new[] { new { rva = 0x9A67A0, aliases = 1 } },
                }
            )
        );
        Reject(() => NativeHookBodies.Parse(changedClient), "native_body_inventory_missing");
        using var duplicate = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    schema = 1,
                    gameassembly_sha256 = RuntimeCompatibility.GameAssemblyHash,
                    metadata_sha256 = RuntimeCompatibility.MetadataHash,
                    bodies = new[]
                    {
                        new { rva = 0x8C9060, aliases = 135 },
                        new { rva = 0x8C9060, aliases = 1 },
                    },
                }
            )
        );
        Reject(() => NativeHookBodies.Parse(duplicate), "native_body_inventory_missing");
    }

    private static void CheckQuitSubscription()
    {
        var calls = 0;
        Action callback = () => calls++;
        var methods = typeof(SyntheticQuitSource);
        var add = methods.GetMethod(nameof(SyntheticQuitSource.Add))!;
        var remove = methods.GetMethod(nameof(SyntheticQuitSource.Remove))!;
        using (var subscription = new UnityQuitSubscription(add, remove, callback, callback))
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SyntheticQuitSource.Quit();
            Require(
                calls == 1,
                "Owned direct quit callback remains registered and rooted until closure."
            );
            subscription.Dispose();
            subscription.Dispose();
            SyntheticQuitSource.Quit();
            Require(
                calls == 1,
                "Owned quit event detaches once and preserves ordinary later engine events."
            );
        }
        var failed = false;
        try
        {
            using var subscription = new UnityQuitSubscription(
                methods.GetMethod(nameof(SyntheticQuitSource.FailAdd))!,
                remove,
                callback,
                callback
            );
        }
        catch (System.Reflection.TargetInvocationException error)
            when (error.InnerException is InvalidOperationException)
        {
            failed = true;
        }
        Require(
            failed,
            "Failed engine callback registration propagates an explicit startup activation failure."
        );
    }

    private static class SyntheticQuitSource
    {
        private static Action? quitting;

        public static void Add(Action callback) => quitting += callback;

        public static void Remove(Action callback) => quitting -= callback;

        public static void Quit() => quitting?.Invoke();

        public static void FailAdd(Action callback) =>
            throw new InvalidOperationException("Synthetic quit registration failure");
    }

    private static void CheckLifecycle()
    {
        var lifetime = new ObservationLifetime();
        using var observed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        var finishes = 0;
        var observation = Task.Run(() =>
        {
            lifetime.Enter();
            observed.Set();
            try
            {
                release.Wait();
            }
            finally
            {
                lifetime.Exit();
            }
        });
        Require(observed.Wait(TimeSpan.FromSeconds(5)), "Synthetic observation is admitted.");
        var close = Task.Run(() =>
            lifetime.Close(() =>
            {
                Interlocked.Increment(ref finishes);
                lifetime.Close(() =>
                    throw new InvalidOperationException("Recursive close ran twice.")
                );
            })
        );
        try
        {
            Require(
                SpinWait.SpinUntil(() => lifetime.IsClosing, TimeSpan.FromSeconds(5)),
                "Close begins draining admitted observations."
            );
            var secondClose = Task.Run(() =>
            {
                secondStarted.Set();
                lifetime.Close(() =>
                    throw new InvalidOperationException("Concurrent close ran twice.")
                );
            });
            Require(
                secondStarted.Wait(TimeSpan.FromSeconds(5))
                    && !close.IsCompleted
                    && !secondClose.IsCompleted
                    && finishes == 0,
                "Every concurrent closer waits for the admitted observation and the single close completion."
            );
            var rejected = false;
            try
            {
                lifetime.Enter();
            }
            catch (InvalidOperationException)
            {
                rejected = true;
            }
            Require(rejected, "Close seals new observation admission.");
            release.Set();
            Task.WhenAll(observation, close, secondClose)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            lifetime.Close(() => throw new InvalidOperationException("Completed close ran twice."));
            Require(
                lifetime.IsClosed && finishes == 1,
                "Drain completes once; repeated and same-thread recursive closure are idempotent."
            );
        }
        finally
        {
            release.Set();
        }
    }

    private static void CheckNativeLifetime()
    {
        var lifetime = new ObservationLifetime();
        using var entered = new ManualResetEventSlim();
        using var finishNative = new ManualResetEventSlim();
        var finished = 0;
        var native = Task.Run(() =>
        {
            using var lease = lifetime.BeginNativeCallback();
            Require(lease is not null, "An open native boundary acquires an invocation lease.");
            entered.Set();
            finishNative.Wait();
            lifetime.Enter();
            lifetime.Exit();
            using var nested = lifetime.BeginNativeCallback();
            Require(
                nested is not null,
                "An admitted native invocation can preserve its downstream observations while shutdown drains."
            );
        });
        try
        {
            Require(entered.Wait(TimeSpan.FromSeconds(5)), "Synthetic native invocation begins.");
            var close = Task.Run(() => lifetime.Close(() => Interlocked.Increment(ref finished)));
            Require(
                SpinWait.SpinUntil(() => lifetime.IsClosing, TimeSpan.FromSeconds(5)),
                "Shutdown begins with an active original native body."
            );
            Require(
                lifetime.BeginNativeCallback() is null
                    && !close.IsCompleted
                    && Volatile.Read(ref finished) == 0,
                "Closed native admission gracefully skips new capture while shutdown retains the original in-flight invocation."
            );
            finishNative.Set();
            Task.WhenAll(native, close).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Require(finished == 1, "Native finalizer release precedes unpatch/closure.");
        }
        finally
        {
            finishNative.Set();
        }

        var recursive = new ObservationLifetime();
        using var closed = new ManualResetEventSlim();
        var callback = recursive.BeginNativeCallback();
        recursive.Close(closed.Set);
        Require(!closed.IsSet, "Close inside its own native callback cannot join itself.");
        callback!.Dispose();
        callback.Dispose();
        Require(
            closed.Wait(TimeSpan.FromSeconds(5)) && recursive.IsClosed,
            "Deferred native callback close completes once after finalizer release."
        );
    }

    private static void CheckOriginalLogs()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-original-log-" + Guid.NewGuid().ToString("N")
        );
        var original = new byte[200_001];
        new Random(7).NextBytes(original);
        File.WriteAllBytes(path, original);
        try
        {
            var chunks = new List<byte[]>();
            string? completed = null;
            OriginalLogSnapshot.Capture(
                path,
                "SyntheticLog",
                (kind, data) =>
                {
                    using var json = JsonDocument.Parse(JsonSerializer.Serialize(data));
                    if (kind == "startup_log_bytes")
                        chunks.Add(json.RootElement.GetProperty("bytes").GetBytesFromBase64());
                    else if (json.RootElement.GetProperty("phase").GetString() == "prefix_complete")
                        completed = json.RootElement.GetProperty("sha256").GetString();
                }
            );
            Require(
                chunks.Count == 4
                    && chunks.SelectMany(chunk => chunk).SequenceEqual(original)
                    && completed == Convert.ToHexString(SHA256.HashData(original)),
                "Available startup log originals survive exact chunked preservation without an arbitrary total-size cutoff."
            );
            var unavailable = false;
            OriginalLogSnapshot.Capture(
                path + ".absent",
                "SyntheticMissingLog",
                (_, data) =>
                {
                    using var json = JsonDocument.Parse(JsonSerializer.Serialize(data));
                    unavailable =
                        json.RootElement.GetProperty("phase").GetString() == "unavailable";
                }
            );
            Require(unavailable, "Missing startup log original is an explicit evidence gap.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void CheckIndependentControl()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-held-control-" + Guid.NewGuid().ToString("N")
        );
        var previous = Plugin.Writer;
        using var copying = new ManualResetEventSlim();
        using var releaseCopy = new ManualResetEventSlim();
        using var failedStorage = new ManualResetEventSlim();
        using var writer = new SessionWriter(
            path,
            new { synthetic = true },
            _ => new { },
            _ => { },
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (action != StorageAction.DataFlush)
                        return;
                    failedStorage.Set();
                    throw new IOException("Synthetic permanent storage failure.");
                },
            }
        );
        try
        {
            Plugin.Writer = writer;
            using var control = new CaptureControl(
                writer.RunId,
                Plugin.CurrentStatus,
                writer.Abort,
                new OperationalStatus(),
                "capture-held-check-" + Guid.NewGuid().ToString("N")
            );
            var observation = Task.Run(() =>
                Plugin.Observe(
                    "held_native_copy",
                    () =>
                    {
                        copying.Set();
                        releaseCopy.Wait();
                        writer.Emit(false, false, "synthetic", new { retained = true });
                    }
                )
            );
            Require(
                copying.Wait(TimeSpan.FromSeconds(5)),
                "Native copier is held for external control verification."
            );
            var duringCopy = Task.Run(() =>
                    SendControl(
                        control,
                        new
                        {
                            schema = 1,
                            command = "status",
                            run_id = writer.RunId,
                        }
                    )
                )
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            using (var response = JsonDocument.Parse(duringCopy))
                Require(
                    response.RootElement.GetProperty("accepted").GetBoolean(),
                    "Current status never waits for the native copier or callback lock."
                );
            releaseCopy.Set();
            Require(
                failedStorage.Wait(TimeSpan.FromSeconds(5)),
                "Synthetic storage holds before acknowledging the native observation."
            );
            Require(
                SpinWait.SpinUntil(
                    () =>
                    {
                        using var status = JsonDocument.Parse(
                            JsonSerializer.Serialize(writer.GetStatus())
                        );
                        return status.RootElement.GetProperty("storage_failures").GetInt64() > 0;
                    },
                    TimeSpan.FromSeconds(5)
                ),
                "Failed storage is published before the external status assertion."
            );
            var duringFailure = SendControl(
                control,
                new
                {
                    schema = 1,
                    command = "status",
                    run_id = writer.RunId,
                }
            );
            using (var response = JsonDocument.Parse(duringFailure))
                Require(
                    response.RootElement.GetProperty("accepted").GetBoolean()
                        && response
                            .RootElement.GetProperty("status")
                            .GetProperty("writer")
                            .GetProperty("storage_failures")
                            .GetInt64() > 0,
                    "External current-run status remains available during persistent failed-volume I/O."
                );
            var aborted = SendControl(
                control,
                new
                {
                    schema = 1,
                    command = "abort",
                    run_id = writer.RunId,
                    reason = "Synthetic explicit external abort",
                }
            );
            using (var response = JsonDocument.Parse(aborted))
                Require(
                    response.RootElement.GetProperty("accepted").GetBoolean(),
                    "Current-user matching-run control explicitly aborts a blocked recorder."
                );
            var incomplete = false;
            try
            {
                observation.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch (CaptureAbortedException)
            {
                incomplete = true;
            }
            Require(
                incomplete,
                "Explicit abort wakes callback waiters with incomplete outcome, never fake success."
            );
        }
        finally
        {
            releaseCopy.Set();
            writer.Abort("Synthetic control check cleanup");
            writer.Dispose();
            Require(
                SpinWait.SpinUntil(
                    () =>
                    {
                        using var status = JsonDocument.Parse(
                            JsonSerializer.Serialize(writer.GetStatus())
                        );
                        return status.RootElement.GetProperty("completed").GetBoolean();
                    },
                    TimeSpan.FromSeconds(5)
                ),
                "Synthetic aborted writer releases its owned files before fixture cleanup."
            );
            Plugin.Writer = previous;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private static byte[] SendControl(CaptureControl control, object request)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (!OperatingSystem.IsWindows())
            return control.Handle(bytes);
        using var pipe = new NamedPipeClientStream(
            ".",
            control.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous
        );
        pipe.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        pipe.Write(BitConverter.GetBytes(bytes.Length));
        pipe.Write(bytes);
        var length = new byte[4];
        pipe.ReadExactlyAsync(length)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        var size = BitConverter.ToInt32(length);
        Require(
            size > 0 && size <= CaptureControl.ResponseLimit,
            "Control response remains bounded."
        );
        var response = new byte[size];
        pipe.ReadExactlyAsync(response)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))
            .GetAwaiter()
            .GetResult();
        return response;
    }

    private static void CheckShutdownDiagnostics()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "capture-shutdown-log-" + Guid.NewGuid().ToString("N")
        );
        var prior = Plugin.Writer;
        var priorListeners = BepInEx.Logging.Logger.Listeners.ToArray();
        using var source = BepInEx.Logging.Logger.CreateLogSource("SyntheticShutdownLogger");
        using var originalActive = new ManualResetEventSlim();
        using var releaseOriginal = new ManualResetEventSlim();
        using var diagnosticAck = new ManualResetEventSlim();
        using var releaseDiagnosticAck = new ManualResetEventSlim();
        using var stopAck = new ManualResetEventSlim();
        using var releaseStopAck = new ManualResetEventSlim();
        var acks = 0;
        var records = new ConcurrentQueue<CaptureDiagnostic>();
        var operational = new OperationalStatus();
        var lifetime = new ObservationLifetime();
        using var writer = new SessionWriter(
            path,
            new { synthetic = true },
            _ => new { },
            _ => { },
            new StorageHooks
            {
                Before = (action, _, _) =>
                {
                    if (action != StorageAction.Acknowledge)
                        return;
                    var next = Interlocked.Increment(ref acks);
                    if (next == 1)
                    {
                        diagnosticAck.Set();
                        releaseDiagnosticAck.Wait();
                    }
                    if (next == 2)
                    {
                        stopAck.Set();
                        releaseStopAck.Wait();
                    }
                },
            }
        );
        using var bridge = new DiagnosticBridge(
            record =>
            {
                lifetime.Enter(diagnostic: true);
                try
                {
                    Plugin.Observe(
                        "shutdown_diagnostic",
                        () => writer.EmitBootstrap("runtime_log", record),
                        diagnostic: true
                    );
                    records.Enqueue(record);
                }
                finally
                {
                    lifetime.Exit();
                }
            },
            operational
        );
        Task? native = null,
            close = null,
            log = null;
        try
        {
            Plugin.Writer = writer;
            BepInEx.Logging.Logger.Listeners.Clear();
            BepInEx.Logging.Logger.Listeners.Add(bridge);
            native = Task.Run(() =>
            {
                using var callback = lifetime.BeginNativeCallback();
                originalActive.Set();
                releaseOriginal.Wait();
            });
            Require(
                originalActive.Wait(TimeSpan.FromSeconds(5)),
                "Original native callback is active before close."
            );
            close = Task.Run(() =>
                lifetime.Close(() =>
                {
                    Plugin.StopDiagnosticCapture(bridge);
                    source.LogWarning((object)"Synthetic unpatch shutdown log after capture stop");
                })
            );
            Require(
                SpinWait.SpinUntil(() => lifetime.IsClosing, TimeSpan.FromSeconds(5)),
                "Close seals native admission while the original callback drains."
            );
            log = Task.Run(() =>
                source.LogError((object)"Synthetic game error while native shutdown drains")
            );
            Require(
                diagnosticAck.Wait(TimeSpan.FromSeconds(5)),
                "Game diagnostic during native shutdown reaches durable acknowledgement rather than closed-admission failure."
            );
            releaseOriginal.Set();
            Require(
                !close.IsCompleted && !log.IsCompleted,
                "Close retains the already admitted diagnostic until its acknowledgement."
            );
            releaseDiagnosticAck.Set();
            Require(
                stopAck.Wait(TimeSpan.FromSeconds(5)),
                "Explicit stop marker is saved after the bridge atomically seals diagnostic admission."
            );
            source.LogError(
                (object)"Synthetic game log after admission frontier while stop marker waits"
            );
            Require(
                records.Count == 1 && !close.IsCompleted,
                "A post-frontier log is outside capture without a persistence-failure warning storm."
            );
            releaseStopAck.Set();
            Task.WhenAll(native, log, close)
                .WaitAsync(TimeSpan.FromSeconds(5))
                .GetAwaiter()
                .GetResult();
            using var bridgeStatus = JsonDocument.Parse(JsonSerializer.Serialize(bridge.Status));
            using var operationalStatus = JsonDocument.Parse(
                JsonSerializer.Serialize(operational.Snapshot)
            );
            Require(
                bridgeStatus.RootElement.GetProperty("capture_failures").GetInt64() == 0
                    && operationalStatus
                        .RootElement.GetProperty("unsaved_operational_messages")
                        .GetInt64() == 0,
                "Closing and unpatch logs never enter closed observation admission or create spurious unsaved diagnostics."
            );
            var boundary = bridgeStatus
                .RootElement.GetProperty("admission_closed_utc")
                .GetDateTimeOffset();
            writer.Dispose();
            DateTimeOffset? savedBoundary = null;
            foreach (var record in SavedDiagnostics(path))
            {
                if (record.GetProperty("kind").GetString() != "log_capture_stop")
                    continue;
                savedBoundary = record
                    .GetProperty("data")
                    .GetProperty("data")
                    .GetProperty("admission_closed_utc")
                    .GetDateTimeOffset();
            }
            Require(
                savedBoundary == boundary,
                "Durable stop marker names the exact atomic admission frontier, not its later acknowledgement time."
            );
        }
        finally
        {
            releaseOriginal.Set();
            releaseDiagnosticAck.Set();
            releaseStopAck.Set();
            if (native is not null && log is not null && close is not null)
                Task.WhenAll(native, log, close)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();
            BepInEx.Logging.Logger.Listeners.Clear();
            foreach (var listener in priorListeners)
                BepInEx.Logging.Logger.Listeners.Add(listener);
            BepInEx.Logging.Logger.Sources.Remove(source);
            writer.Dispose();
            Plugin.Writer = prior;
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }

    private static IEnumerable<JsonElement> SavedDiagnostics(string path)
    {
        var archives = Directory
            .EnumerateFiles(path, "*.zip", SearchOption.AllDirectories)
            .ToArray();
        foreach (var archive in archives)
        {
            using var zip = ZipFile.OpenRead(archive);
            using var reader = new StreamReader(zip.GetEntry("diagnostics.jsonl")!.Open());
            while (reader.ReadLine() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var record = json.RootElement;
                if (record.GetProperty("kind").GetString() == "process_bootstrap")
                    record = record.GetProperty("data").GetProperty("original_record");
                yield return record.Clone();
            }
        }
        if (archives.Length != 0)
            yield break;
        foreach (
            var log in Directory.EnumerateFiles(
                path,
                "diagnostics.jsonl.gz",
                SearchOption.AllDirectories
            )
        )
        {
            using var input = new GZipStream(File.OpenRead(log), CompressionMode.Decompress);
            using var reader = new StreamReader(input);
            while (reader.ReadLine() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                yield return json.RootElement.Clone();
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

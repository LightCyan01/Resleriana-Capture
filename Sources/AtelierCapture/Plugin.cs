using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace AtelierCapture;

[BepInPlugin("atelier.capture", "Atelier Capture", BuildInfo.Version)]
public sealed class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    internal static SessionWriter Writer = null!;
    internal static RuntimeCompatibility Compatibility = null!;
    private static readonly ObservationLifetime Lifetime = new();
    private static readonly ConcurrentDictionary<string, string> HookStatus = new();
    private static readonly ConcurrentDictionary<string, long>[] ObserverErrors =
    [
        new(),
        new(),
        new(),
    ];

    [ThreadStatic]
    private static int observationDepth;

    [ThreadStatic]
    private static List<Action>? afterCommit;
    private static OperationalStatus operational = new();
    private static DiagnosticBridge? diagnostics;
    private static CaptureControl? control;
    private static readonly List<IDisposable> NativeHooks = [];
    private static UnityQuitSubscription? quitSubscription;
    private static int closeRequested;
    private static int unityDiagnosticsAvailable;

    public override void Load()
    {
        Log = base.Log;
        var privacy = CapturePrivacy.Load(
            Path.Combine(Paths.ConfigPath, "AtelierCapture.Privacy.json")
        );
        operational = new OperationalStatus(message => Log.LogWarning(message));
        var pluginPath = typeof(Plugin).Assembly.Location;
        using var assembly = File.OpenRead(pluginPath);
        var metadata = new
        {
            plugin = "atelier.capture",
            version = BuildInfo.Version,
            supported_client = "2.16.0",
            informational_version = typeof(Plugin)
                .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion,
            assembly_sha256 = Convert.ToHexString(SHA256.HashData(assembly)),
            identity_self_reported = true,
            privacy = privacy.Metadata,
            observation_contract = "complete_supported_gameplay_with_explicit_read_failures",
            durability = new
            {
                gameplay_and_network = "durable_before_callback_continuation",
                ordinary_diagnostics = "durable_before_outer_callback_continuation",
                warnings_and_known_material_errors = "queued_before_outer_callback_continuation",
                nested_diagnostics = "share_outer_observation_save_boundary",
                normal_shutdown = "drain_admitted_diagnostics_before_summaries",
                abrupt_termination = "queued_diagnostic_occurrences_can_be_lost",
            },
            plugin_lifecycle = "one_capture_run_per_process_restart_required_after_unload",
            queued_batches = SessionWriter.QueueCapacity,
            retained_memory_budget_bytes = SessionWriter.QueueBudget,
            retained_memory_accounting = "owned_utf8_and_compression_capacity_estimate_with_one_oversized_observation_and_one_active_copier",
        };
        Writer = new SessionWriter(
            Path.Combine(Path.GetDirectoryName(pluginPath)!, "Captures"),
            metadata,
            CaptureStatus,
            operational.Report,
            null,
            privacy
        );
        control = new CaptureControl(Writer.RunId, CurrentStatus, Writer.Abort, operational);
        diagnostics = new DiagnosticBridge(
            record =>
                Observe(
                    "diagnostic",
                    () => EmitDiagnostic("runtime_log", record),
                    diagnostic: true
                ),
            operational,
            (record, suppressConsole) =>
            {
                Action? committed = null;
                Observe(
                    "warning",
                    () =>
                    {
                        var origin = DiagnosticOrigin();
                        committed = diagnostics!.StageWarning(
                            record,
                            origin,
                            (kind, data) => EmitDiagnostic(kind, data, origin),
                            suppressConsole
                        );
                    },
                    committed: () => committed?.Invoke(),
                    partialCommitted: () => committed?.Invoke(),
                    diagnostic: true,
                    deferred: true
                );
            },
            record =>
            {
                Action? committed = null;
                Observe(
                    "known_material_error",
                    () =>
                    {
                        var origin = DiagnosticOrigin();
                        committed = diagnostics!.StageError(
                            record,
                            origin,
                            (kind, data) => EmitDiagnostic(kind, data, origin)
                        );
                    },
                    committed: () => committed?.Invoke(),
                    partialCommitted: () => committed?.Invoke(),
                    diagnostic: true,
                    deferred: true
                );
            }
        );
        BepInEx.Logging.Logger.Listeners.Add(diagnostics);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Writer.EmitBootstrap(
            "process_start",
            new
            {
                process_id = Environment.ProcessId,
                control_pipe = control.PipeName,
                runtime = Environment.Version.ToString(),
                metadata,
            }
        );
        Compatibility = RuntimeCompatibility.Inspect(
            Paths.GameRootPath,
            Path.Combine(Path.GetDirectoryName(pluginPath)!, "capture-runtime.json")
        );
        Writer.EmitBootstrap("runtime_compatibility", Compatibility.Status);
        OriginalLogSnapshot.Capture(
            Path.Combine(Paths.BepInExRootPath, "LogOutput.log"),
            "BepInEx.LogOutput",
            Writer.EmitBootstrap
        );
        OriginalLogSnapshot.Capture(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "LocalLow",
                "KOEI TECMO GAMES CO., LTD_",
                "レスレリアーナのアトリエ",
                "Player.log"
            ),
            "Unity.Player",
            Writer.EmitBootstrap
        );
        Volatile.Write(
            ref unityDiagnosticsAvailable,
            BepInEx.Logging.Logger.Sources.Any(source =>
                source.GetType().FullName == "BepInEx.Unity.IL2CPP.Logging.IL2CPPUnityLogSource"
            )
                ? 1
                : 0
        );
        Writer.EmitBootstrap(
            "diagnostic_listener_coverage",
            new
            {
                bep_source_severity_filter = "all",
                unity_source_available = Volatile.Read(ref unityDiagnosticsAvailable) != 0,
                gap = Volatile.Read(ref unityDiagnosticsAvailable) == 0
                    ? "unity_source_unavailable_user_logging_configuration_preserved"
                    : null,
                startup_originals = "exact_available_file_prefixes_unflushed_or_earlier_console_records_unproven",
            }
        );
        if (!Compatibility.ApiVerified)
        {
            operational.Report(
                "Capture startup blocked by runtime/client compatibility; inspect local status."
            );
            return;
        }
        var startupComplete = true;
        void Install(string owner, Action install)
        {
            try
            {
                install();
            }
            catch (Exception error)
            {
                startupComplete = false;
                ReportFailure("startup_" + owner, error);
                operational.Report(
                    $"Capture {owner} activation is incomplete; inspect local status."
                );
            }
        }
        Install(
            "shutdown",
            () =>
            {
                quitSubscription = UnityQuitSubscription.Attach(Compatibility, OnApplicationQuit);
                Writer.EmitBootstrap(
                    "quit_callback_attached",
                    new { source = "UnityEngine.Application.quitting" }
                );
            }
        );
        Install("network", Network.Install);
        Install("gameplay", Battle.Install);
        diagnostics.Phase = startupComplete ? "recording" : "recording_activation_incomplete";
        Writer.EmitBootstrap(
            "capture_startup_status",
            new
            {
                activation_stages_returned = startupComplete,
                runtime = Compatibility.Status,
                official = CaptureStatus(false),
                @private = CaptureStatus(true),
            }
        );
    }

    public override bool Unload()
    {
        Close("plugin_unload");
        return true;
    }

    private static void OnProcessExit(object? sender, EventArgs args) =>
        Close("managed_process_exit");

    private static void OnApplicationQuit() => Close("unity_application_quitting");

    private static void Close(string trigger)
    {
        if (Interlocked.CompareExchange(ref closeRequested, 1, 0) == 0)
        {
            try
            {
                Writer.EmitBootstrap(
                    "closure_start",
                    new
                    {
                        trigger,
                        utc = DateTimeOffset.UtcNow,
                        phase = "before_admission_drain",
                    }
                );
            }
            catch (Exception error)
            {
                operational.Report($"Capture closure start is unsaved: {error.GetType().Name}.");
            }
        }
        if (observationDepth != 0)
        {
            _ = Task.Run(() => Close(trigger));
            return;
        }
        Lifetime.Close(() =>
        {
            try
            {
                try
                {
                    StopDiagnosticCapture(diagnostics);
                }
                catch (Exception error)
                {
                    ClosingFailure("log_capture_stop", error);
                }
                try
                {
                    quitSubscription?.Dispose();
                }
                catch (Exception error)
                {
                    ClosingFailure("quit_callback_detach", error);
                }
                for (var index = NativeHooks.Count - 1; index >= 0; index--)
                    try
                    {
                        NativeHooks[index].Dispose();
                    }
                    catch (Exception error)
                    {
                        ClosingFailure("native_observer_detach", error);
                    }
                AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
                try
                {
                    try
                    {
                        Network.SaveAssetDownloadSummary();
                    }
                    catch (Exception error)
                    {
                        ClosingFailure("asset_download_summary", error);
                    }
                    Writer?.Dispose();
                }
                finally
                {
                    Network.ReleaseObjects();
                }
            }
            catch (Exception error)
            {
                operational.Report($"Capture closure incomplete: {error.GetType().Name}.");
            }
            finally
            {
                control?.Dispose();
            }
        });
    }

    internal static void StopDiagnosticCapture(DiagnosticBridge? bridge)
    {
        try
        {
            var boundary = bridge?.Stop() ?? DateTimeOffset.UtcNow;
            if (bridge is not null)
            {
                // Deferred warnings count only once saved; summaries must include every queued one.
                Writer.WaitForQueued();
                using (Writer.BeginObservation())
                {
                    foreach (var summary in bridge.WarningSummaries())
                        EmitDiagnostic("runtime_warning_summary", summary, summary.origin);
                    foreach (var summary in bridge.ErrorSummaries())
                        EmitDiagnostic("runtime_error_summary", summary, summary.origin);
                }
            }
            Writer.EmitBootstrap(
                "log_capture_stop",
                new
                {
                    phase = "after_admitted_callbacks_drained_before_unpatch",
                    utc = DateTimeOffset.UtcNow,
                    admission_closed_utc = boundary,
                    coverage = "ordinary_shutdown_logs_after_this_frontier_are_outside_capture",
                    listener = bridge?.Status,
                }
            );
        }
        finally
        {
            if (bridge is not null)
            {
                BepInEx.Logging.Logger.Listeners.Remove(bridge);
                bridge.Dispose();
            }
        }
    }

    private static void ClosingFailure(string stage, Exception error)
    {
        ObserverErrors[2].AddOrUpdate("closure_" + stage, 1, (_, count) => checked(count + 1));
        try
        {
            Writer.EmitBootstrap(
                "closure_error",
                new
                {
                    stage,
                    error = error.GetType().FullName,
                    stack = error.ToString(),
                    completeness = "closure_failed",
                }
            );
        }
        catch (Exception persistenceError)
        {
            operational.Report(
                $"Capture closure diagnostic is unsaved: {persistenceError.GetType().Name}."
            );
        }
    }

    internal static T ReadResource<T>(string name)
    {
        using var stream =
            typeof(Plugin).Assembly.GetManifestResourceStream($"AtelierCapture.{name}")
            ?? throw new FileNotFoundException(name);
        return JsonSerializer.Deserialize<T>(
                stream,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            ) ?? throw new InvalidDataException(name);
    }

    internal static void ObserveNative(
        MethodBase? target,
        Type owner,
        string? prefix,
        string? postfix,
        string? finalizer = null
    )
    {
        if (target is null)
            throw new MissingMethodException("Native observation target");
        Compatibility.RequireValidated(target);
        var identity =
            target.DeclaringType?.FullName
            + "."
            + target.Name
            + "("
            + string.Join(
                ",",
                target
                    .GetParameters()
                    .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
            )
            + ")";
        try
        {
            var handlers = new NativeObserverHandlers(
                target,
                owner,
                prefix,
                postfix,
                finalizer,
                owner == typeof(Network) ? Network.AssetFilter(target) : null
            );
            NativeHooks.Add(
                PassiveNativeHook.Install(
                    target,
                    handlers.Enter,
                    handlers.Returned,
                    handlers.Faulted,
                    handlers.Leave,
                    ReportNativeObserverFailure
                )
            );
            HookStatus[identity] = "installed";
        }
        catch (Exception error)
        {
            if (PassiveNativeHook.IsClosed)
                foreach (var hook in HookStatus)
                    if (hook.Value == "installed")
                        HookStatus[hook.Key] = "inactive_native_activation_rollback";
            HookStatus[identity] = (error.InnerException ?? error).GetType().Name;
            throw;
        }
    }

    internal static void ReportNativeObserverFailure(Exception error)
    {
        try
        {
            ReportFailure("native_observer", error);
        }
        catch (Exception persistenceError)
        {
            operational.Report(
                $"Native observer failure is unsaved: {error.GetType().Name}; persistence: {persistenceError.GetType().Name}."
            );
        }
    }

    internal static void Observe(
        string kind,
        Action copy,
        Action? committed = null,
        Action? partialCommitted = null,
        bool diagnostic = false,
        bool deferred = false
    )
    {
        var outer = observationDepth == 0;
        if (outer)
            Lifetime.Enter(diagnostic);
        observationDepth++;
        deferred &= outer;
        try
        {
            if (outer)
                afterCommit = [];
            using (var batch = Writer.BeginObservation(deferred))
            {
                var copied = true;
                try
                {
                    copy();
                }
                catch (Exception error)
                {
                    copied = false;
                    ReportFailure(kind, error);
                }
                var completed = copied ? committed : partialCommitted;
                if (completed is not null)
                    afterCommit!.Add(completed);
                // Registered before the batch is queued so the writer cannot save it first.
                if (deferred)
                {
                    var saved = afterCommit!;
                    Writer.AfterSave(() => saved.ForEach(action => action()));
                }
            }
            if (outer && !deferred)
                for (var index = 0; index < afterCommit!.Count; index++)
                {
                    try
                    {
                        afterCommit[index]();
                    }
                    catch (Exception error)
                    {
                        ReportFailure("commit_state_" + kind, error);
                    }
                }
        }
        finally
        {
            observationDepth--;
            if (outer)
            {
                afterCommit = null;
                Lifetime.Exit();
            }
        }
    }

    internal static IDisposable? BeginNativeCallback() => Lifetime.BeginNativeCallback();

    private static string DiagnosticOrigin() =>
        Network.CurrentContext?.IsPrivate is { } isPrivate
            ? isPrivate
                ? "Private"
                : "Official"
            : "Unknown";

    private static void EmitDiagnostic(string kind, object record, string? origin = null)
    {
        origin ??= DiagnosticOrigin();
        if (origin == "Unknown")
            Writer.EmitBootstrap(kind, record);
        else
            Writer.Emit(origin == "Private", false, kind, record, systemId: "Recorder");
    }

    internal static void ReportFailure(string kind, Exception error)
    {
        var cause = error.InnerException ?? error;
        var current = Network.CurrentContext?.IsPrivate;
        ObserverErrors[
            current is null ? 2
            : current.Value ? 1
            : 0
        ]
            .AddOrUpdate(kind, 1, (_, count) => checked(count + 1));
        var record = new
        {
            observer = kind,
            error = cause.GetType().FullName,
            stack = cause.ToString(),
            phase = diagnostics?.Phase ?? "startup",
            utc = DateTimeOffset.UtcNow,
            thread = Environment.CurrentManagedThreadId,
            completeness = "read_failed",
        };
        if (observationDepth != 0)
            EmitDiagnostic("observer_error", record);
        else
            Observe("observer_error", () => EmitDiagnostic("observer_error", record));
    }

    private static object CaptureStatus(bool isPrivate) =>
        new
        {
            hooks = HookStatus.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
            native_observers_closed = PassiveNativeHook.IsClosed,
            observer_errors = ObserverErrors[isPrivate ? 1 : 0]
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value),
            bootstrap_observer_errors = ObserverErrors[2]
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value),
            combat = Battle.CaptureStatus(isPrivate),
            network = Network.CaptureStatus(),
            runtime = Compatibility?.Status,
            storage = Writer.GetStatus(),
            operational = operational.Snapshot,
            diagnostics = new
            {
                unity_source_available = Volatile.Read(ref unityDiagnosticsAvailable) != 0,
                prior_console_coverage = "available_original_file_prefix_only",
                listener = diagnostics?.Status,
            },
        };

    internal static object CurrentStatus() =>
        new
        {
            run_id = Writer.RunId,
            phase = Lifetime.IsClosed ? "closed"
            : Lifetime.IsClosing ? "closing"
            : diagnostics?.Phase ?? "startup",
            compatibility = Compatibility?.Status,
            writer = Writer.GetStatus(),
            operational = operational.Snapshot,
            bootstrap_observer_errors = ObserverErrors[2]
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value),
            official = CaptureStatus(false),
            @private = CaptureStatus(true),
        };
}

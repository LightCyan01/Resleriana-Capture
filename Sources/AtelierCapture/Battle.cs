using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace AtelierCapture;

internal static class Battle
{
    [ThreadStatic]
    private static bool observing;

    [ThreadStatic]
    private static Stack<State>? calls;
    private static long nextInvocation;
    private static readonly ConcurrentDictionary<MethodBase, HookSpec> Hooks = new();
    private static readonly ConcurrentDictionary<string, string> Skipped = new(
        StringComparer.Ordinal
    );
    private static readonly ConcurrentDictionary<string, long>[] Observed = [new(), new()];
    private static readonly ConcurrentDictionary<string, long> Incomplete = new(
        StringComparer.Ordinal
    );
    private static readonly ConcurrentDictionary<string, string> SemanticGaps = new(
        StringComparer.Ordinal
    );
    private static HookSpec[] configured = [];
    private static bool readersChecked;

    internal static void Install()
    {
        configured = Plugin
            .ReadResource<HookSpec[]>("BattleHooks.json")
            .Concat(Atelier.Contracts())
            .ToArray();
        foreach (var hook in configured.Where(h => h.AsyncReturn))
            SemanticGaps[hook.System + ":" + hook.Boundary] =
                "async_operation_completion_edge_unqualified";
        foreach (var hook in configured)
            SemanticGaps.TryAdd(
                hook.System + ":" + hook.Boundary + ":stability",
                "native_owner_stability_unqualified"
            );
        if (!Plugin.Compatibility.NativeGameplayVerified)
        {
            foreach (var hook in configured)
                Skipped[hook.System + ":" + hook.Boundary] = "native_runtime_unverified";
            Plugin.Writer.EmitBootstrap("native_activation_incomplete", CaptureStatus(false));
            return;
        }
        try
        {
            Snapshot.Initialize();
            Snapshot.CheckReaders();
            readersChecked = true;
        }
        catch (Exception ex)
        {
            foreach (var hook in configured)
                Skipped[hook.System + ":" + hook.Boundary] = "native_reader_self_check_failed";
            Plugin.ReportFailure("native_readers", ex);
            Plugin.Writer.EmitBootstrap("native_activation_incomplete", CaptureStatus(false));
            return;
        }
        InstallHooks(configured);
        Plugin.Log.LogInfo(
            $"Qualified native capture methods: {Hooks.Count}; unqualified boundaries: {Skipped.Count}."
        );
    }

    internal static void InstallHooks(IEnumerable<HookSpec> hooks)
    {
        foreach (var hook in hooks)
        {
            try
            {
                if (hook.ActivationGap is { Length: > 0 })
                    throw new InvalidDataException(hook.ActivationGap);
                var owner = NativeContract.ResolveType(hook.Type);
                MethodBase target =
                    hook.Method == ".ctor"
                        ? NativeContract.BindConstructor(owner, hook.Rva, hook.Parameters)
                        : NativeContract.Bind(
                            owner,
                            hook.Method,
                            hook.Rva,
                            hook.Returns,
                            hook.Parameters,
                            hook.IsStatic
                        );
                if (!Hooks.TryAdd(target, hook))
                    continue;
                try
                {
                    Plugin.ObserveNative(
                        target,
                        typeof(Battle),
                        target.IsStatic ? nameof(EnterStatic) : nameof(EnterInstance),
                        target is ConstructorInfo || ((MethodInfo)target).ReturnType == typeof(void)
                            ? nameof(Exit)
                            : nameof(ExitResult),
                        nameof(Finalizer)
                    );
                }
                catch
                {
                    Hooks.TryRemove(target, out _);
                    throw;
                }
            }
            catch (Exception ex)
            {
                Skipped[hook.System + ":" + hook.Boundary] =
                    hook.ActivationGap
                    ?? (
                        ex is RuntimeCompatibilityException mismatch
                            ? mismatch.Code
                            : ex.GetType().Name
                    );
                Plugin.ReportFailure("native_hook_" + hook.Role, ex);
            }
        }
    }

    internal static void RecordGap(string boundary, string reason) => Skipped[boundary] = reason;

    internal static object CaptureStatus(bool isPrivate)
    {
        var closed = PassiveNativeHook.IsClosed;
        var bound = Hooks.Values.ToArray();
        return new
        {
            configured = configured.Length,
            installed = closed ? 0 : bound.Length,
            bound_count = bound.Length,
            systems = configured
                .GroupBy(h => h.System)
                .OrderBy(g => g.Key)
                .ToDictionary(
                    g => g.Key,
                    g => new
                    {
                        configured = g.Count(),
                        installed = closed ? 0 : bound.Count(h => h.System == g.Key),
                        bound_count = bound.Count(h => h.System == g.Key),
                        roles = g.Select(h => h.Role).Distinct().OrderBy(r => r).ToArray(),
                    }
                ),
            readers_checked = readersChecked,
            complete = !closed
                && readersChecked
                && Skipped.IsEmpty
                && SemanticGaps.IsEmpty
                && Incomplete.IsEmpty,
            configured_roles = configured
                .Select(h => h.System + ":" + h.Role)
                .Distinct()
                .OrderBy(r => r)
                .ToArray(),
            installed_roles = (closed ? [] : bound)
                .Select(h => h.System + ":" + h.Role)
                .Distinct()
                .OrderBy(r => r)
                .ToArray(),
            skipped_roles = Skipped.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
            incomplete_observations = Incomplete
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value),
            semantic_gaps = SemanticGaps.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
            observed_calls = Observed[isPrivate ? 1 : 0]
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value),
        };
    }

    private static void EnterStatic(
        object[] __args,
        MethodBase __originalMethod,
        out State? __state
    ) => EnterCore(null, __args, __originalMethod, out __state);

    private static void EnterInstance(
        object __instance,
        object[] __args,
        MethodBase __originalMethod,
        out State? __state
    ) => EnterCore(__instance, __args, __originalMethod, out __state);

    private static void EnterCore(
        object? instance,
        object[] args,
        MethodBase method,
        out State? state
    )
    {
        state = null;
        if (observing || !Hooks.TryGetValue(method, out var hook))
            return;
        var lifetime = Plugin.BeginNativeCallback();
        if (lifetime is null)
            return;
        try
        {
            calls ??= new();
            var lineageFailures =
                method is ConstructorInfo
                    ? []
                    : Snapshot.BindOwnerLineage(instance, hook.Boundary, hook.Rva);
            var inputs = args.Append(instance).Select(Network.BindingFor).ToArray();
            var contexts = inputs
                .Select(input => input.Context)
                .Append(Network.CurrentContext)
                .Where(c => c is not null)
                .Cast<NetworkContext>()
                .DistinctBy(c => (c.AttemptId, c.OperationId, c.Route, c.IsPrivate))
                .ToArray();
            var context = contexts.Length == 1 ? contexts[0] : null;
            var environmentProofs = inputs
                .Select(input => input.Environment)
                .Where(p => p is not null)
                .ToArray();
            var origins = environmentProofs
                .Select(p => p!.IsPrivate)
                .Concat(contexts.Select(c => c.IsPrivate))
                .Where(value => value is not null)
                .Distinct()
                .ToArray();
            bool? origin =
                origins.Length == 1
                && environmentProofs.All(p => p?.Ambiguous != true && p?.IsPrivate is not null)
                    ? origins[0]
                    : null;
            state = new(
                instance,
                hook,
                Interlocked.Increment(ref nextInvocation),
                calls.TryPeek(out var parent) ? parent.Id : null,
                Stopwatch.GetTimestamp(),
                context,
                origin,
                environmentProofs
                    .SelectMany(p => p!.QualifiedEdges)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(edge => edge)
                    .ToArray(),
                contexts.Length > 1 ? "native_input_operations_ambiguous"
                    : context is null ? "no_proven_native_operation"
                    : null
            );
            state.CallbackLease = lifetime;
            state.LineageFailures = lineageFailures;
            state.ContextScope = Network.PushContext(context);
            calls.Push(state);
            Record("enter", args, state);
        }
        catch (Exception error)
        {
            if (state is null)
                lifetime.Dispose();
            Incomplete.AddOrUpdate(hook.Boundary, 1, static (_, count) => count + 1);
            Plugin.ReportFailure("native_entry", error);
        }
    }

    private static void Exit(
        object? __instance,
        object[] __args,
        MethodBase __originalMethod,
        State? __state
    )
    {
        if (__state is null)
            return;
        __state.Instance = __instance;
        if (
            __state.Instance is { } instance
            && (
                __originalMethod is ConstructorInfo
                || __state.Hook.Role
                    is "mapped_model_copy"
                        or "selection_model_copy"
                        or "resource_application"
                        or "resource_deletion_application"
            )
        )
        {
            if (
                __state.Context is { } context
                && __state.Hook.Role
                    is not ("resource_application" or "resource_deletion_application")
            )
                Network.BindContext(
                    instance,
                    context,
                    __state.Hook.Boundary + ":initialized_receiver_from_input"
                );
            else
                Network.BindEnvironment(
                    instance,
                    __state.Environment,
                    __state.Hook.Boundary + ":applied_input_environment"
                );
        }
        Record("exit", __args, __state);
    }

    private static void ExitResult(
        object? __instance,
        object[] __args,
        MethodBase __originalMethod,
        State? __state,
        object? __result
    )
    {
        if (__state is not null)
        {
            __state.Instance = __instance;
            Record("exit", __args, __state, __result);
        }
    }

    private static void Finalizer(
        bool __nativeFault,
        bool __snapshotAvailable,
        object? __instance,
        object[] __args,
        MethodBase __originalMethod,
        State? __state
    )
    {
        if (__state is null)
            return;
        try
        {
            if (__nativeFault)
            {
                __state.Instance = __instance;
                Record(
                    "exception",
                    __args,
                    __state,
                    error: __snapshotAvailable
                        ? "native_original_threw"
                        : "native_fault_phase_snapshot_unavailable",
                    phaseSnapshotAvailable: __snapshotAvailable
                );
            }
        }
        finally
        {
            try
            {
                __state.ContextScope?.Dispose();
                if (calls?.TryPeek(out var current) == true && ReferenceEquals(current, __state))
                    calls.Pop();
                else
                    Incomplete.AddOrUpdate("invocation_stack", 1, static (_, count) => count + 1);
            }
            finally
            {
                __state.CallbackLease?.Dispose();
            }
        }
    }

    private static void Record(
        string phase,
        object[] args,
        State state,
        object? result = null,
        string? error = null,
        bool phaseSnapshotAvailable = true
    )
    {
        if (observing)
            return;
        observing = true;
        try
        {
            Plugin.Observe(
                "native_observation",
                () =>
                {
                    var snapshot = new Snapshot(Plugin.Writer.PrivacyEnabled);
                    var started = Stopwatch.GetTimestamp();
                    var instance =
                        !phaseSnapshotAvailable || state.Hook.Method == ".ctor" && phase != "exit"
                            ? null
                            : snapshot.ReadReceiver(
                                state.Instance,
                                state.Hook.Boundary,
                                state.Hook.Rva
                            );
                    var values = args.Select(value => snapshot.Read(value)).ToArray();
                    var isTaskReturn = phase == "exit" && state.Hook.AsyncReturn;
                    var returned = snapshot.Read(result);
                    var context = state.Context;
                    var observationGap = isTaskReturn
                        ? "async_operation_completion_edge_unqualified"
                        : null;
                    var clientStatus = ClientStatusProjection.Applies(
                        state.Hook.Boundary,
                        state.Hook.Rva
                    );
                    var resourceReceiver =
                        !clientStatus
                        && ResourceReceiverProjection.Applies(state.Hook.Boundary, state.Hook.Rva);
                    var data = new
                    {
                        invocation_id = state.Id.ToString(CultureInfo.InvariantCulture),
                        parent_invocation_id = state.ParentId?.ToString(
                            CultureInfo.InvariantCulture
                        ),
                        boundary_id = state.Hook.Boundary,
                        variant_id = state.Hook.Variant,
                        system_id = state.Hook.System,
                        role = state.Hook.Role,
                        phase = isTaskReturn ? "task_return" : phase,
                        request_id = context?.RequestId,
                        operation_id = context?.OperationId,
                        attempt_id = context?.AttemptId,
                        response_id = context?.ResponseId,
                        identity_proof = context?.IdentityProof,
                        origin = state.Environment is null ? "unknown"
                        : state.Environment.Value ? "Private"
                        : "Official",
                        origin_proof = new
                        {
                            qualified_edges = state.EnvironmentEdges,
                            scoped_callback_proof = context?.IdentityProof,
                        },
                        route = context?.Route,
                        declared_route = state.Hook.Route,
                        // Owner stability across the copy is unqualified, so no observation is complete.
                        status = "incomplete",
                        complete = false,
                        failures = snapshot.Failures,
                        correlation_gap = state.CorrelationGap,
                        lineage_failures = state.LineageFailures,
                        observation_gap = observationGap,
                        observation_consistency = "owner_thread_sequential_reads_stability_unqualified",
                        consistency_gap = "native_owner_stability_unqualified",
                        copied_fields_complete = phaseSnapshotAvailable && snapshot.Complete,
                        phase_snapshot_available = phaseSnapshotAvailable,
                        result_contract = phase == "exception" ? "native_fault_no_return_value"
                        : isTaskReturn ? "task_return_only_completion_unqualified"
                        : "synchronous_result",
                        elapsed_ms = (started - state.Started) * 1000.0 / Stopwatch.Frequency,
                        snapshot_ms = (Stopwatch.GetTimestamp() - started)
                            * 1000.0
                            / Stopwatch.Frequency,
                        instance,
                        projection_contract = clientStatus ? ClientStatusProjection.Contract
                        : resourceReceiver ? ResourceReceiverProjection.Contract
                        : NativeProjection.Contract,
                        snapshot_schema = 2,
                        snapshot_contracts = snapshot.Contracts,
                        referenced_owner_state_contract = "identity_only_no_implicit_state_or_definition_reuse",
                        instance_contract = !phaseSnapshotAvailable ? "phase_snapshot_unavailable"
                        : state.Hook.Method == ".ctor" && phase != "exit"
                            ? "uninitialized_constructor_receiver"
                        : clientStatus ? "initialized_owner_native_client_status_read_set"
                        : resourceReceiver ? ResourceReceiverProjection.StateContract
                        : "initialized_owner_gameplay_projection",
                        values,
                        result = returned,
                        nodes = snapshot.Nodes,
                        error,
                        native_original_threw = phase == "exception",
                    };
                    Incomplete.AddOrUpdate(state.Hook.Boundary, 1, static (_, count) => count + 1);
                    var kind = (state.Hook.System == "Battle" ? "combat_" : "native_") + phase;
                    if (state.Environment is { } isPrivate)
                    {
                        if (phase == "enter")
                            Observed[isPrivate ? 1 : 0]
                                .AddOrUpdate(
                                    state.Hook.System + ":" + state.Hook.Role,
                                    1,
                                    static (_, count) => count + 1
                                );
                        Plugin.Writer.Emit(
                            isPrivate,
                            true,
                            kind,
                            data,
                            snapshot.RetainedBytes,
                            state.Hook.System
                        );
                    }
                    else
                        Plugin.Writer.EmitBootstrap("unbound_" + kind, data);
                }
            );
        }
        finally
        {
            observing = false;
        }
    }

    private sealed record State(
        object? Instance,
        HookSpec Hook,
        long Id,
        long? ParentId,
        long Started,
        NetworkContext? Context,
        bool? Environment,
        string[] EnvironmentEdges,
        string? CorrelationGap
    )
    {
        public object? Instance { get; set; } = Instance;
        internal IDisposable? ContextScope;
        internal IDisposable? CallbackLease;
        internal string[] LineageFailures = [];
    }

    internal sealed record HookSpec(
        string Type,
        string Method,
        string Role,
        long Rva,
        string Returns,
        string[] Parameters,
        [property: JsonPropertyName("is_static")] bool IsStatic,
        [property: JsonPropertyName("async_return")] bool AsyncReturn = false,
        string System = "Battle",
        string Boundary = "",
        string Variant = "entry_exit",
        string? Route = null,
        [property: JsonPropertyName("activation_gap")] string? ActivationGap = null
    );
}

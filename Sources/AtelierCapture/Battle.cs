using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using HarmonyLib;

namespace AtelierCapture;

internal static class Battle
{
    [ThreadStatic] private static bool observing;
    private static readonly ConcurrentDictionary<MethodBase, HookSpec> Hooks = new();
    private static readonly ConcurrentDictionary<string, string> Skipped = new();
    private static readonly ConcurrentDictionary<string, long>[] Observed = { new(), new() };
    private static HookSpec[] configured = Array.Empty<HookSpec>();
    private static bool readersChecked;
    private static string? disabledReason;

    internal static void Install(Harmony harmony)
    {
        try
        {
            configured = Plugin.ReadResource<HookSpec[]>("BattleHooks.json");
            if (configured.Length == 0 || configured.Any(h => string.IsNullOrEmpty(h.Type)
                || string.IsNullOrEmpty(h.Method) || string.IsNullOrEmpty(h.Role) || h.Arity < 0))
                throw new InvalidDataException("Invalid battle hook definitions");
            Snapshot.Initialize();
            Snapshot.CheckReaders();
            readersChecked = true;
        }
        catch (Exception ex)
        {
            var cause = ex.InnerException ?? ex;
            disabledReason = cause.GetType().Name;
            foreach (var hook in configured.Where(h => !string.IsNullOrEmpty(h.Role)))
                Skipped[hook.Role] = disabledReason;
            Plugin.ReportFailure("combat_readers", ex);
            Plugin.Log.LogError($"Combat capture disabled: native reader check failed ({disabledReason}). API capture remains enabled. Use the matching plugin and HarmonySupport DLLs from the package.\n{cause}");
            return;
        }
        Plugin.Log.LogInfo("Native collection, nullable allocation, scalar, and tuple capture checks passed.");
        foreach (var hook in configured)
        {
            try
            {
                var type = Plugin.ResolveType(hook.Type);
                var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == hook.Method && m.GetParameters().Length == hook.Arity).ToArray();
                if (methods.Length == 0) throw new MissingMethodException();
                if (methods.Length != 1) throw new AmbiguousMatchException();
                var target = methods[0];
                if (!Hooks.TryAdd(target, hook)) throw new InvalidDataException("Duplicate battle hook");
                try
                {
                    Plugin.Patch(harmony, target, typeof(Battle),
                        target.IsStatic ? nameof(EnterStatic) : nameof(EnterInstance),
                        target.ReturnType == typeof(void) ? nameof(Exit) : nameof(ExitResult), nameof(Finalizer));
                }
                catch { Hooks.TryRemove(target, out _); throw; }
            }
            catch (Exception ex)
            {
                Skipped[hook.Role] = (ex.InnerException ?? ex).GetType().Name;
                Plugin.ReportFailure("combat_hook_" + hook.Role, ex);
            }
        }
        Plugin.Log.LogInfo($"Battle hooks installed: {Hooks.Count}/{configured.Length}.");
    }

    internal static object CaptureStatus(bool isPrivate) => new
    {
        configured = configured.Length, installed = Hooks.Count, readers_checked = readersChecked,
        disabled_reason = disabledReason,
        configured_roles = configured.Select(h => h.Role).OrderBy(r => r).ToArray(),
        installed_roles = Hooks.Values.Select(h => h.Role).OrderBy(r => r).ToArray(),
        skipped_roles = Skipped.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
        observed_calls = Observed[isPrivate ? 1 : 0].OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value)
    };

    private static void EnterStatic(object[] __args, MethodBase __originalMethod, out State? __state) =>
        EnterCore(null, __args, __originalMethod, out __state);

    private static void EnterInstance(object __instance, object[] __args, MethodBase __originalMethod, out State? __state) =>
        EnterCore(__instance, __args, __originalMethod, out __state);

    private static void EnterCore(object? instance, object[] args, MethodBase method, out State? state)
    {
        state = observing ? null : new State(instance, Stopwatch.GetTimestamp(), Network.IsPrivate == true, Network.BattleRequestId);
        if (state is not null) Record("combat_enter", args, method, state);
    }

    private static void Exit(object[] __args, MethodBase __originalMethod, State? __state)
    {
        if (__state is not null) Record("combat_exit", __args, __originalMethod, __state);
    }

    private static void ExitResult(object[] __args, MethodBase __originalMethod, State? __state, object? __result)
    {
        if (__state is not null) Record("combat_exit", __args, __originalMethod, __state, __result);
    }

    private static Exception? Finalizer(Exception? __exception, MethodBase __originalMethod, State? __state)
    {
        if (__exception is not null && __state is not null)
            Record("combat_exception", Array.Empty<object>(), __originalMethod, __state, error: __exception.GetType().Name);
        return __exception;
    }

    private static void Record(string kind, object[] args, MethodBase method, State state, object? result = null, string? error = null)
    {
        if (observing) return;
        observing = true;
        try
        {
            Hooks.TryGetValue(method, out var hook);
            if (kind == "combat_enter" && hook is not null)
                Observed[state.Private ? 1 : 0].AddOrUpdate(hook.Role, 1, static (_, count) => count + 1);
            Plugin.Observe("combat", () =>
            {
                var snapshot = new Snapshot();
                var started = Stopwatch.GetTimestamp();
                var elapsed = (started - state.Started) * 1000.0 / Stopwatch.Frequency;
                var instance = snapshot.Read(state.Instance, shallow: args.Length != 0);
                var values = args.Select(value => snapshot.Read(value)).ToArray();
                var returned = hook?.AsyncReturn == true ? new { omitted = "async_operation_not_awaited" } : snapshot.Read(result);
                var data = new
                {
                    request_id = state.RequestId, role = hook?.Role,
                    method = method.DeclaringType?.FullName + "." + method.Name,
                    elapsed_ms = elapsed,
                    snapshot_ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency,
                    async_return = hook?.AsyncReturn ?? false,
                    instance, values, result = returned,
                    error
                };
                Plugin.Writer.Emit(state.Private, true, kind, data, snapshot.RetainedBytes);
            });
        }
        finally { observing = false; }
    }

    private sealed record State(object? Instance, long Started, bool Private, long RequestId);
    private sealed record HookSpec(string Type, string Method, int Arity, string Role, int Rva,
        [property: JsonPropertyName("async_return")] bool AsyncReturn = false);
}

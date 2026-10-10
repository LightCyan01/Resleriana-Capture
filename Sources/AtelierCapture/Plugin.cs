using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AtelierCapture;

[BepInPlugin("atelier.capture", "Atelier Capture", BuildInfo.Version)]
public sealed class Plugin : BasePlugin
{
    internal static new ManualLogSource Log = null!;
    internal static SessionWriter Writer = null!;
    internal static Privacy Privacy = null!;
    private static readonly HashSet<string> ObserverWarnings = new();
    private static readonly ConcurrentDictionary<string, long> ObserverErrors = new();
    private static readonly ConcurrentDictionary<string, string> HookStatus = new();
    private Harmony? harmony;

    public override void Load()
    {
        Log = base.Log;
        Privacy = new Privacy();
        var root = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!, "Captures");
        using var assembly = File.OpenRead(typeof(Plugin).Assembly.Location);
        using var hash = SHA256.Create();
        Writer = new SessionWriter(root, new
        {
            plugin = "atelier.capture", version = BuildInfo.Version, supported_client = "2.16.0",
            informational_version = typeof(Plugin).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly_sha256 = Convert.ToHexString(hash.ComputeHash(assembly)), identity_self_reported = true,
            limits = new { raw_body_bytes = Network.BodyLimit, queued_bytes = SessionWriter.QueueBudget,
                queued_events = 2048, snapshot_nodes = 12000, snapshot_depth = 10, collection_items = 4096 }
        }, CaptureStatus,
            message => Log.LogWarning(message));
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        harmony = new Harmony("atelier.capture");
        Patch(harmony, AccessTools.Method(ResolveType("UnityEngine.Application"), "Internal_ApplicationQuit"),
            typeof(Plugin), null, nameof(OnApplicationQuit));
        try { Network.Install(harmony); }
        catch (Exception ex) { ReportFailure("network_install", ex); }
        Battle.Install(harmony);
        Log.LogInfo($"Capture output: {root}; one ZIP per environment/run with separate API, combat, raw-body, and diagnostics files.");
    }

    public override bool Unload()
    {
        harmony?.UnpatchSelf();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        Writer?.Dispose();
        return true;
    }

    private static void OnProcessExit(object? sender, EventArgs args) => Writer.Dispose();
    private static void OnApplicationQuit() => Writer.Dispose();

    internal static Type ResolveType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetType(name, false) is { } direct) return direct;
        var shortName = name[(name.LastIndexOf('.') + 1)..];
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetTypes().FirstOrDefault(t => t.Name == shortName || t.FullName == name) is { } found) return found;
            }
            catch (ReflectionTypeLoadException ex)
            {
                if (ex.Types.FirstOrDefault(t => t is not null && (t.Name == shortName || t.FullName == name)) is { } found) return found;
            }
        }
        throw new TypeLoadException(name);
    }

    internal static T ReadResource<T>(string name)
    {
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream($"AtelierCapture.{name}")
            ?? throw new FileNotFoundException(name);
        return JsonSerializer.Deserialize<T>(stream, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException(name);
    }

    internal static void Patch(Harmony harmony, MethodBase? target, Type owner, string? prefix, string? postfix,
        string? finalizer = null, int priority = Priority.Normal)
    {
        if (target is null) throw new MissingMethodException("Patch target");
        HarmonyMethod? method(string? hook)
        {
            if (hook is null) return null;
            var result = new HarmonyMethod(owner.GetMethod(hook, BindingFlags.Static | BindingFlags.NonPublic)!);
            result.priority = priority;
            return result;
        }
        var name = target.DeclaringType?.FullName + "." + target.Name;
        try
        {
            harmony.Patch(target, prefix: method(prefix), postfix: method(postfix), finalizer: method(finalizer));
            HookStatus[name] = "installed";
        }
        catch (Exception ex)
        {
            HookStatus[name] = (ex.InnerException ?? ex).GetType().Name;
            throw;
        }
    }

    internal static void Observe(string kind, Action action)
    {
        try { action(); }
        catch (Exception ex) { ReportFailure(kind, ex); }
    }

    internal static void ReportFailure(string kind, Exception ex)
    {
        var error = (ex.InnerException ?? ex).GetType().Name;
        ObserverErrors.AddOrUpdate(kind, 1, (_, count) => count + 1);
        lock (ObserverWarnings)
            if (ObserverWarnings.Add(kind))
            {
                Log.LogWarning($"{kind} observer failed: {error}; further warnings for this observer are suppressed.");
                if (Network.IsPrivate is { } isPrivate)
                    Writer.Emit(isPrivate, false, "observer_error", new { observer = kind, error });
            }
    }

    private static object CaptureStatus(bool isPrivate) => new
    {
        hooks = HookStatus.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
        observer_errors = ObserverErrors.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value),
        combat = Battle.CaptureStatus(isPrivate)
    };

    internal static JsonNode? ParseJson(string value)
    {
        try { return JsonNode.Parse(value); }
        catch { return new JsonObject { ["omitted"] = "not_json" }; }
    }
}

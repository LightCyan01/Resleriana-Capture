using System.Text.Json;

namespace AtelierCapture.Archive;

internal sealed record NativeCountBoundary(string System, string Boundary, string Variant);

internal static class NativeCountCatalog
{
    internal static readonly NativeCountBoundary[] Boundaries = Read();
    private static readonly IReadOnlyDictionary<string, NativeCountBoundary> ObservedAliases =
        ReadObservedAliases();

    internal static bool TryObservedAlias(string key, out NativeCountBoundary value) =>
        ObservedAliases.TryGetValue(key, out value!);

    private static IReadOnlyDictionary<string, NativeCountBoundary> ReadObservedAliases()
    {
        var result = new Dictionary<string, NativeCountBoundary>(StringComparer.Ordinal);
        foreach (var hook in Plugin.ReadResource<Battle.HookSpec[]>("BattleHooks.json"))
            if (hook.ObservedBoundary is { Length: > 0 } boundary)
                result.Add(
                    Key(hook.System, boundary, hook.Variant),
                    new(hook.System, boundary, hook.Variant)
                );
        return result;
    }

    internal static string Key(string system, string boundary, string variant) =>
        system + "\n" + boundary + "\n" + variant;

    private static NativeCountBoundary[] Read()
    {
        var assembly = typeof(NativeCountCatalog).Assembly;
        var result = new Dictionary<string, NativeCountBoundary>(StringComparer.Ordinal);
        foreach (
            var name in assembly
                .GetManifestResourceNames()
                .Where(name =>
                    name.EndsWith(".BattleHooks.json", StringComparison.Ordinal)
                    || name.EndsWith(".AtelierHooks.json", StringComparison.Ordinal)
                )
        )
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var document = JsonDocument.Parse(stream);
            var hooks =
                document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement
                    : document.RootElement.GetProperty("hooks");
            foreach (var hook in hooks.EnumerateArray())
            {
                var system = hook.TryGetProperty("system", out var owner)
                    ? owner.GetString()!
                    : "Battle";
                var boundary = hook.TryGetProperty("boundary", out var identity)
                    ? identity.GetString()!
                    : hook.GetProperty("type").GetString()
                        + "."
                        + hook.GetProperty("method").GetString();
                var variant = hook.TryGetProperty("variant", out var version)
                    ? version.GetString()!
                    : "entry_exit";
                result.TryAdd(
                    Key(system, boundary, variant),
                    new NativeCountBoundary(system, boundary, variant)
                );
            }
        }
        return result
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .ToArray();
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtelierCapture;

internal static class NativeHookBodies
{
    private static readonly Lazy<IReadOnlyDictionary<long, int>> Inventory = new(ReadInventory);

    internal static void RequireUnique(long rva)
    {
        if (!Inventory.Value.TryGetValue(rva, out var aliases))
            throw new RuntimeCompatibilityException("native_body_inventory_missing");
        if (aliases != 1)
            throw new RuntimeCompatibilityException("shared_native_body_unqualified");
    }

    private static IReadOnlyDictionary<long, int> ReadInventory()
    {
        using var stream =
            typeof(Plugin).Assembly.GetManifestResourceStream(
                "AtelierCapture.NativeHookBodies.json"
            ) ?? throw new RuntimeCompatibilityException("native_body_inventory_missing");
        return Parse(stream);
    }

    internal static IReadOnlyDictionary<long, int> Parse(Stream stream)
    {
        try
        {
            var inventory =
                JsonSerializer.Deserialize<BodyInventory>(stream)
                ?? throw new RuntimeCompatibilityException("native_body_inventory_missing");
            if (
                inventory.Schema != 1
                || !string.Equals(
                    inventory.GameAssemblyHash,
                    RuntimeCompatibility.GameAssemblyHash,
                    StringComparison.OrdinalIgnoreCase
                )
                || !string.Equals(
                    inventory.MetadataHash,
                    RuntimeCompatibility.MetadataHash,
                    StringComparison.OrdinalIgnoreCase
                )
                || inventory.Bodies is null
            )
                throw new RuntimeCompatibilityException("native_body_inventory_missing");
            var bodies = new Dictionary<long, int>();
            foreach (var body in inventory.Bodies)
                if (body.Rva <= 0 || body.Aliases <= 0 || !bodies.TryAdd(body.Rva, body.Aliases))
                    throw new RuntimeCompatibilityException("native_body_inventory_missing");
            return bodies;
        }
        catch (JsonException)
        {
            throw new RuntimeCompatibilityException("native_body_inventory_missing");
        }
    }

    private sealed record BodyInventory(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("gameassembly_sha256")] string? GameAssemblyHash,
        [property: JsonPropertyName("metadata_sha256")] string? MetadataHash,
        [property: JsonPropertyName("bodies")] Body[]? Bodies
    );

    private sealed record Body(
        [property: JsonPropertyName("rva")] long Rva,
        [property: JsonPropertyName("aliases")] int Aliases
    );
}

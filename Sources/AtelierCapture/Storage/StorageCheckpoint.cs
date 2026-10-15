using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtelierCapture.Storage;

internal sealed record StorageStreamStamp(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("records")] long Records,
    [property: JsonPropertyName("extracted_bytes")] long ExtractedBytes
);

internal sealed record StorageCheckpointPayload(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("generation")] long Generation,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("environment")] string Environment,
    [property: JsonPropertyName("streams")] StorageStreamStamp[] Streams,
    [property: JsonPropertyName("committed_batches")] long CommittedBatches
);

internal static class StorageCheckpoint
{
    internal static readonly string[] FileNames =
    [
        "api.jsonl.gz",
        "combat.jsonl.gz",
        "raw-bodies.jsonl.gz",
        "diagnostics.jsonl.gz",
    ];

    internal const int MaximumBytes = 65536;

    internal static byte[] Encode(StorageCheckpointPayload payload)
    {
        Validate(payload);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output))
        {
            json.WriteStartObject();
            using var document = JsonDocument.Parse(canonical);
            foreach (var property in document.RootElement.EnumerateObject())
                property.WriteTo(json);
            json.WriteString("checksum", Convert.ToHexString(SHA256.HashData(canonical)));
            json.WriteEndObject();
        }
        return output.ToArray();
    }

    internal static StorageCheckpointPayload Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Capture checkpoint exceeds its metadata bound.");
        using var document = JsonDocument.Parse(
            bytes.ToArray(),
            new JsonDocumentOptions { MaxDepth = 8 }
        );
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Capture checkpoint must be an object.");
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema",
            "generation",
            "run_id",
            "environment",
            "streams",
            "committed_batches",
            "checksum",
        };
        foreach (var property in root.EnumerateObject())
            if (!expected.Remove(property.Name))
                throw new InvalidDataException(
                    "Capture checkpoint has duplicate or unknown fields."
                );
        if (expected.Count != 0)
            throw new InvalidDataException("Capture checkpoint omits a field.");
        var streams = root.GetProperty("streams");
        if (
            streams.ValueKind != JsonValueKind.Array
            || streams.GetArrayLength() != FileNames.Length
        )
            throw new InvalidDataException("Capture checkpoint must contain all four streams.");
        foreach (var stream in streams.EnumerateArray())
        {
            if (stream.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Capture stream stamp must be an object.");
            var fields = new HashSet<string>(StringComparer.Ordinal)
            {
                "name",
                "bytes",
                "sha256",
                "records",
                "extracted_bytes",
            };
            foreach (var property in stream.EnumerateObject())
                if (!fields.Remove(property.Name))
                    throw new InvalidDataException(
                        "Capture checkpoint has ambiguous stream fields."
                    );
            if (fields.Count != 0)
                throw new InvalidDataException("Capture checkpoint omits a stream field.");
        }
        var payload =
            root.Deserialize<StorageCheckpointPayload>()
            ?? throw new InvalidDataException("Capture checkpoint is empty.");
        Validate(payload);
        var checksum = root.GetProperty("checksum").GetString();
        var actual = Convert.ToHexString(
            SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload))
        );
        if (!string.Equals(checksum, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Capture checkpoint checksum differs.");
        return payload;
    }

    private static void Validate(StorageCheckpointPayload payload)
    {
        if (
            payload.Schema != 2
            || payload.Generation < 1
            || payload.CommittedBatches < 0
            || payload.RunId is null
            || payload.RunId.Length != 32
            || !payload.RunId.All(Uri.IsHexDigit)
            || payload.Environment is not ("Official" or "Private" or "Unknown")
            || payload.Streams is null
            || payload.Streams.Length != FileNames.Length
        )
            throw new InvalidDataException("Capture checkpoint identity is invalid.");
        for (var i = 0; i < FileNames.Length; i++)
        {
            var stream = payload.Streams[i];
            if (
                stream is null
                || stream.Name != FileNames[i]
                || stream.Bytes < 0
                || stream.Records < 0
                || stream.ExtractedBytes < 0
                || stream.Sha256 is null
                || stream.Sha256.Length != 64
                || !stream.Sha256.All(Uri.IsHexDigit)
            )
                throw new InvalidDataException("Capture checkpoint stream is invalid.");
        }
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class JsonCaptureChecks
{
    internal static void Run()
    {
        StringBoundaries();
        const string original =
            "{\"user_id\":42,\"name\":\"original name\",\"nested\":[{\"authToken\":\"original token\",\"hp\":31}],\"null\":null}";
        var node = Parse(original)!;
        using var values = new MemoryStream();
        CaptureJson.Write(values, new { payload = node });
        using var captured = JsonDocument.Parse(values.ToArray());
        var payload = captured.RootElement.GetProperty("payload");
        Assert(payload.GetProperty("user_id").GetInt32() == 42, "Identifier must remain unchanged");
        Assert(
            payload.GetProperty("name").GetString() == "original name",
            "Name must remain unchanged"
        );
        Assert(
            payload.GetProperty("nested")[0].GetProperty("authToken").GetString()
                == "original token",
            "Token must remain unchanged"
        );
        Assert(
            payload.GetProperty("null").ValueKind == JsonValueKind.Null,
            "Null must remain present"
        );

        const int depth = 20000;
        var deep =
            new string('[', depth)
            + "{\"email\":\"original@example.invalid\",\"hp\":73}"
            + new string(']', depth);
        using var output = new MemoryStream();
        CaptureJson.WriteLine(output, new { payload = Parse(deep) });
        using var parsed = JsonDocument.Parse(
            output.ToArray(),
            new JsonDocumentOptions { MaxDepth = int.MaxValue }
        );
        var leaf = parsed.RootElement.GetProperty("payload");
        for (var index = 0; index < depth; index++)
            leaf = leaf[0];
        Assert(
            leaf.GetProperty("email").GetString() == "original@example.invalid"
                && leaf.GetProperty("hp").GetInt32() == 73,
            "Deep original fields must remain unchanged"
        );

        var text = new string('x', 8191) + "\U0001F30D\n\"" + new string('y', 33000000);
        using var large = new MemoryStream();
        CaptureJson.Write(large, new { payload = new JsonObject { ["description"] = text } });
        using var largeDocument = JsonDocument.Parse(large.ToArray());
        Assert(
            largeDocument.RootElement.GetProperty("payload").GetProperty("description").GetString()
                == text,
            "Large JSON string, surrogate and escapes must remain exact"
        );
    }

    private static void StringBoundaries()
    {
        foreach (var length in new[] { 0, 1, 8191, 8192, 8193, 16383, 16384, 16385 })
            ExactString(new string('x', length));
        foreach (var prefix in new[] { 8189, 8190, 8191, 8192 })
        {
            ExactString(new string('x', prefix) + "\U0001F30D");
            ExactString(new string('x', prefix) + "\U0001F30D\n\"\\end");
        }
        foreach (var prefix in new[] { 8191, 8192 })
        {
            ExactString(new string('x', prefix) + "\ud800");
            ExactString(new string('x', prefix) + "\udc00");
        }
        foreach (var length in new[] { 8192, 8193 })
            ExactString(new string('漢', length));
        foreach (
            var value in new[]
            {
                "\"\\\n\r\t\b\f\0<>&",
                "日本語\U0001F30D",
                "\ud800",
                "\udc00",
                "unchanged token and identifier 9223372036854775807",
                "native nonfinite bits 7FC00001 FFC00001 7F800000",
            }
        )
            ExactString(value);
    }

    private static void ExactString(string value)
    {
        var scalar = new { payload = value };
        using var scalarOutput = new MemoryStream();
        CaptureJson.Write(scalarOutput, scalar);
        Assert(
            scalarOutput
                .ToArray()
                .AsSpan()
                .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(scalar)),
            "String converter must preserve the standard escaped UTF8 bytes across its buffered/segmented boundary"
        );
        var node = new { payload = new JsonObject { ["value"] = value } };
        using var nodeOutput = new MemoryStream();
        CaptureJson.Write(nodeOutput, node);
        Assert(
            nodeOutput.ToArray().AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(node)),
            "JsonNode converter must preserve the standard escaped UTF8 bytes across its buffered/segmented boundary"
        );
    }

    private static JsonNode? Parse(string text) =>
        JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = int.MaxValue });

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

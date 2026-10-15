using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static partial class Network
{
    [ThreadStatic]
    private static int derivedParseDepth;

    internal static object Decode(
        byte[] original,
        string inputStage,
        RequestLedger.Attempt attempt,
        bool response,
        bool json,
        bool encrypted,
        bool compressed,
        List<Action> commits
    )
    {
        var bytes = original;
        Wire.Description? wire = null;
        var direction = response ? "response" : "request";
        var stage = inputStage;
        try
        {
            if (encrypted && bytes.Length != 0)
            {
                bytes = Decrypt(bytes);
                stage = "derived_decrypted";
                Preserve(attempt, bytes, direction, stage, commits);
            }
            if (compressed && bytes.Length != 0)
            {
                bytes = ApiBodies.Expand(bytes);
                stage = "derived_decompressed";
                Preserve(attempt, bytes, direction, stage, commits);
            }
            if (json)
            {
                var value =
                    bytes.Length == 0
                        ? new JsonObject()
                        : ParsePayload(new UTF8Encoding(false, true).GetString(bytes));
                return new
                {
                    bytes = bytes.Length,
                    stage,
                    payload = value,
                    source = "derived_json",
                };
            }
            var logicalRoute = attempt.Context.LogicalRoute ?? attempt.Context.Route;
            if (!routes.TryGetValue(logicalRoute, out var route))
            {
                wire = Wire.Describe(bytes, null);
                Gap(attempt.Context, "unknown_route_schema", new { route = logicalRoute });
                return new
                {
                    bytes = bytes.Length,
                    stage,
                    wire,
                    capture_status = "incomplete",
                    reason = "unknown_route_schema",
                };
            }
            var type = route[response ? 1 : 0];
            wire = Wire.Describe(bytes, type);
            if (wire.error is not null)
                throw new InvalidDataException(wire.error);
            if (!Plugin.Compatibility.ClientVerified)
                return new
                {
                    bytes = bytes.Length,
                    stage,
                    protobuf_type = type,
                    wire,
                    capture_status = "incomplete",
                    reason = "native_schema_unverified",
                };
            var parser = Parser(type);
            var nativeBytes = new Il2CppStructArray<byte>(bytes);
            object message;
            derivedParseDepth++;
            try
            {
                message = parser.Parse.Invoke(parser.Parser, new object[] { nativeBytes })!;
            }
            finally
            {
                derivedParseDepth--;
                GC.KeepAlive(nativeBytes);
            }
            try
            {
                var unknown = wire.unknown_count != 0;
                var value = ParsePayload(NativeMessageText(message));
                return new
                {
                    bytes = bytes.Length,
                    stage,
                    protobuf_type = type,
                    unknown_fields_omitted = unknown,
                    wire,
                    payload = value,
                    source = "derived_native_parser",
                };
            }
            finally
            {
                (message as IDisposable)?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Gap(
                attempt.Context,
                "body_decode_failed",
                new
                {
                    error = (ex.InnerException ?? ex).GetType().Name,
                    exception = ex.ToString(),
                    stage,
                }
            );
            return new
            {
                bytes = bytes.Length,
                stage,
                capture_status = "incomplete",
                reason = "body_decode_failed",
                wire,
                error = (ex.InnerException ?? ex).GetType().Name,
                exception = ex.ToString(),
            };
        }
    }

    private static (object Parser, MethodInfo Parse) Parser(string name)
    {
        if (Parsers.TryGetValue(name, out var cached))
            return cached;
        var type = Plugin.ResolveType(name);
        var parser =
            type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException("Native protobuf parser is unavailable.");
        var parse =
            parser.GetType().GetMethod("ParseFrom", new[] { typeof(Il2CppStructArray<byte>) })
            ?? throw new MissingMethodException(
                "Native protobuf parser byte-array overload is unavailable."
            );
        return Parsers[name] = (parser, parse);
    }

    internal static JsonNode? ParsePayload(string json) =>
        JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = int.MaxValue });

    internal static byte[] Decrypt(byte[] frame)
    {
        if (frame.Length < 17 || (frame.Length - 1) % 16 != 0)
            throw new InvalidDataException();
        var seed = Convert.FromHexString("487A9961C947F7D92ED6B79FC0545FEA");
        var prefix = frame[0];
        if (prefix > 127)
            Array.Reverse(seed);
        var shift = prefix & 7;
        var offset = prefix >> 3;
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)(
                (seed[(i + offset) & 15] << shift)
                | (shift == 0 ? 0 : seed[(i + offset + 1) & 15] >> (8 - shift))
            );
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(
            frame.AsSpan(1),
            Convert.FromHexString("65A99B89A634FCA3193C5212E5219378")
        );
    }
}

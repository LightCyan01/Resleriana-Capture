using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static class Network
{
    private sealed record Request(long Id, string Route, bool Private, long Started)
    {
        internal int RawResponseWritten, ResponseWritten, Completed;
    }
    private sealed record RawBody(long request_id, string route, string direction, string stage, byte[] body)
    {
        public int bytes => body.Length;
        public string sha256 => Convert.ToHexString(SHA256.HashData(body));
    }
    internal const int BodyLimit = 32 * 1024 * 1024;
    private static readonly ConcurrentDictionary<IntPtr, Request> Requests = new();
    private static readonly ConcurrentDictionary<string, byte> ApiOrigins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (object Parser, MethodInfo Parse)> Parsers = new();
    private static readonly object ParserGate = new();
    private static readonly string[] SafeHeaders = { "content-type", "content-length", "content-encoding", "x-content-encoding",
        "x-client-version", "x-master-data-version", "x-asset-version", "x-platform", "retry-after", "date" };
    private static Dictionary<string, string[]> routes = null!;
    private static long requestId, battleRequestId;
    private static int environment = -1;
    private static int battleEnvironment = -1;

    internal static bool? IsPrivate => Volatile.Read(ref battleEnvironment) is var battle && battle >= 0
        ? battle == 1
        : Volatile.Read(ref environment) is var e && e >= 0 ? e == 1 : null;
    internal static long BattleRequestId => Interlocked.Read(ref battleRequestId);

    internal static void Install(Harmony harmony)
    {
        routes = Plugin.ReadResource<Dictionary<string, string[]>>("Routes.json");
        foreach (var name in routes.Values.SelectMany(x => x).Distinct())
            _ = Plugin.ResolveType(name);
        var requestType = Plugin.ResolveType("BestHTTP.HTTPRequest");
        Plugin.Patch(harmony, AccessTools.Method(requestType, "Send", Type.EmptyTypes),
            typeof(Network), nameof(Send), null, nameof(SendFailed), Priority.Last);
        Plugin.Patch(harmony, AccessTools.Method(requestType, "Dispose", Type.EmptyTypes),
            typeof(Network), nameof(Completed), null);
        Plugin.Patch(harmony, AccessTools.PropertySetter(requestType, "State"),
            typeof(Network), null, nameof(StateChanged));
        var decoder = Plugin.ResolveType("PMLKMPHGENK").GetMethod("AMIKMFABIFK", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("PMLKMPHGENK.AMIKMFABIFK");
        Plugin.Patch(harmony, decoder, typeof(Network), nameof(Decoding), nameof(Decoded), nameof(DecodeFailed));
    }

    private static bool Select(dynamic request, out string path, out bool isPrivate)
    {
        path = "";
        isPrivate = false;
        if (!Uri.TryCreate((string?)request.Uri?.ToString(), UriKind.Absolute, out var uri)) return false;
        path = uri.AbsolutePath;
        var official = uri.Host.Equals("game.resleriana.jp", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".resleriana.jp", StringComparison.OrdinalIgnoreCase);
        if (routes.ContainsKey(path) || path == "/status") ApiOrigins.TryAdd(uri.GetLeftPart(UriPartial.Authority), 0);
        if (!ApiOrigins.ContainsKey(uri.GetLeftPart(UriPartial.Authority)) && uri.Host != "game.resleriana.jp") return false;
        isPrivate = !official;
        return true;
    }

    private static void Send(dynamic __instance)
    {
        Plugin.Observe("request", () =>
        {
            if (!Select((object)__instance, out string path, out bool isPrivate)) return;
            Volatile.Write(ref environment, isPrivate ? 1 : 0);
            if (Requests.Count >= 1024)
                foreach (var entry in Requests.OrderByDescending(x => Volatile.Read(ref x.Value.Completed))
                    .ThenBy(x => x.Value.Started).Take(512))
                    if (Requests.TryRemove(entry.Key, out var old) && Volatile.Read(ref old.Completed) == 0)
                        Plugin.Writer.Emit(old.Private, false, "request_tracking_gap", new { request_id = old.Id, reason = "inflight_limit" });
            var request = new Request(Interlocked.Increment(ref requestId), path, isPrivate, Stopwatch.GetTimestamp());
            var pointer = (IntPtr)__instance.Pointer;
            if (Requests.TryGetValue(pointer, out var previous) && Volatile.Read(ref previous.Completed) == 0)
                Plugin.Writer.Emit(previous.Private, false, "request_tracking_gap", new { request_id = previous.Id, reason = "request_reused" });
            Requests[pointer] = request;
            var raw = (Il2CppStructArray<byte>?)__instance.RawData;
            if (raw is not null) Preserve(request, raw, "request", "request_entity");
            else Plugin.Writer.Emit(isPrivate, false, "raw_body", new
            {
                request_id = request.Id, route = path, direction = "request", stage = "request_entity",
                omitted = __instance.UploadStream is null ? "request_entity_not_materialized" : "upload_stream_not_read"
            });
            var body = __instance.UploadStream is not null
                ? (object)new { omitted = "upload_stream_not_read" }
                : ReadBody(raw, path, false, path == "/status", encrypted: path != "/status");
            Plugin.Writer.Emit(isPrivate, false, "request", new
            {
                request_id = request.Id, route = path, method = __instance.MethodType.ToString(),
                headers = Headers(name => (string?)__instance.GetFirstHeaderValue(name)), body
            });
        });
    }

    private static void SendFailed(dynamic __instance, Exception? __exception)
    {
        if (__exception is null) return;
        Plugin.Observe("request_exception", () =>
        {
            if (Requests.TryRemove((IntPtr)__instance.Pointer, out var request))
                Plugin.Writer.Emit(request.Private, false, "request_exception", new { request_id = request.Id, error = __exception.GetType().Name });
        });
    }

    private static void Decoding(dynamic __0)
    {
        Plugin.Observe("raw_response", () =>
        {
            if (__0.baseRequest is { } owner && Requests.TryGetValue((IntPtr)((dynamic)owner).Pointer, out var request))
                PreserveResponse(request, (object)__0);
        });
    }

    private static void PreserveResponse(Request request, dynamic response)
    {
        if (Interlocked.Exchange(ref request.RawResponseWritten, 1) == 0)
            Preserve(request, (Il2CppStructArray<byte>?)response.Data, "response", "response_entity");
    }

    private static void Preserve(Request request, Il2CppStructArray<byte>? raw, string direction, string stage)
    {
        var length = raw?.Length ?? 0;
        if (length > BodyLimit)
        {
            Plugin.Writer.Emit(request.Private, false, "raw_body", new
            {
                request_id = request.Id, route = request.Route, direction, stage, bytes = length, omitted = "body_limit"
            });
            return;
        }
        Plugin.Writer.Emit(request.Private, false, "raw_body",
            new RawBody(request.Id, request.Route, direction, stage, Copy(raw)), length + 4096L);
    }

    private static void Completed(dynamic __instance)
    {
        Plugin.Observe("request_complete", () =>
        {
            if (!Requests.TryGetValue((IntPtr)__instance.Pointer, out var request)
                || Interlocked.Exchange(ref request.Completed, 1) != 0) return;
            var response = __instance.Response;
            if (response is not null)
            {
                PreserveResponse(request, (object)response);
                if (Volatile.Read(ref request.ResponseWritten) == 0)
                {
                    var encrypted = ((string?)response.GetFirstHeaderValue("content-type"))?.Contains("application/octet-stream", StringComparison.Ordinal) == true;
                    var compressed = encrypted && response.GetFirstHeaderValue("x-content-encoding") == "gzip";
                    var body = ReadBody((Il2CppStructArray<byte>?)response.Data, request.Route, true,
                        response.StatusCode != 200 || request.Route == "/status" || !encrypted, encrypted, compressed);
                    RecordResponse(request, (int)response.StatusCode, Headers(name => (string?)response.GetFirstHeaderValue(name)), body, "completion_fallback");
                }
            }
            Plugin.Writer.Emit(request.Private, false, "request_complete", new
            {
                request_id = request.Id, route = request.Route,
                state = (int)__instance.State, state_name = __instance.State.ToString(),
                status = response?.StatusCode, response_available = response is not null,
                response_recorded = Volatile.Read(ref request.ResponseWritten) != 0,
                transport_error = __instance.Exception is null ? null : ((object)__instance.Exception).GetType().Name,
                retries = (int)__instance.Retries, max_retries = (int)__instance.MaxRetries,
                headers = response is null ? null : Headers(name => (string?)response.GetFirstHeaderValue(name)),
                elapsed_ms = (Stopwatch.GetTimestamp() - request.Started) * 1000.0 / Stopwatch.Frequency
            });
        });
    }

    private static void StateChanged(dynamic __instance)
    {
        Plugin.Observe("transport_state", () =>
        {
            if (!Requests.TryGetValue((IntPtr)__instance.Pointer, out var request)) return;
            var state = (int)__instance.State;
            Plugin.Writer.Emit(request.Private, false, "transport_state", new { request_id = request.Id, state = state, state_name = __instance.State.ToString() });
        });
    }

    private static void Decoded(dynamic __0, Il2CppStructArray<byte>? __result)
    {
        Plugin.Observe("response", () =>
        {
            if (__0.baseRequest is not { } owner || !Requests.TryGetValue((IntPtr)((dynamic)owner).Pointer, out var request)) return;
            if ((int)__0.StatusCode == 200 && routes.TryGetValue(request.Route, out var schema)
                && schema[1].StartsWith("Blend.ApiSchema.Api.Battle", StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref battleRequestId, request.Id);
                Volatile.Write(ref battleEnvironment, request.Private ? 1 : 0);
            }
            if (Volatile.Read(ref request.ResponseWritten) != 0) return;
            var body = ReadBody(__result, request.Route, true, (int)__0.StatusCode != 200 || request.Route == "/status");
            RecordResponse(request, (int)__0.StatusCode, Headers(name => (string?)__0.GetFirstHeaderValue(name)), body, "client_decoder");
        });
    }

    private static void RecordResponse(Request request, int status, Dictionary<string, string> headers, object body, string source)
    {
        if (Interlocked.Exchange(ref request.ResponseWritten, 1) != 0) return;
        Plugin.Writer.Emit(request.Private, false, "response", new { request_id = request.Id, route = request.Route, status, headers, body, source });
    }

    private static void DecodeFailed(dynamic __0, Exception? __exception)
    {
        if (__exception is null) return;
        Plugin.Observe("response_decode_failed", () =>
        {
            if (__0.baseRequest is { } owner && Requests.TryGetValue((IntPtr)((dynamic)owner).Pointer, out var request))
                Plugin.Writer.Emit(request.Private, false, "response_decode_failed", new
                    { request_id = request.Id, status = __0.StatusCode, error = __exception.GetType().Name });
        });
    }

    private static object ReadBody(Il2CppStructArray<byte>? raw, string path, bool response, bool json, bool encrypted = false, bool compressed = false)
    {
        var length = raw?.Length ?? 0;
        if (length > BodyLimit) return new { bytes = length, omitted = "body_limit" };
        object? wire = null;
        try
        {
            var bytes = Copy(raw);
            if (encrypted && bytes.Length != 0) bytes = Decrypt(bytes);
            if (compressed && bytes.Length != 0)
            {
                using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
                using var expanded = new MemoryStream();
                var block = new byte[65536];
                int count;
                while ((count = gzip.Read(block)) != 0)
                {
                    if (expanded.Length + count > BodyLimit) throw new InvalidDataException("body_limit");
                    expanded.Write(block, 0, count);
                }
                bytes = expanded.ToArray();
            }
            JsonNode? value;
            var unknown = false;
            string? type = null;
            if (json)
                value = bytes.Length == 0 ? new JsonObject() : Privacy.Parse(Encoding.UTF8.GetString(bytes));
            else
            {
                if (!routes.TryGetValue(path, out var route))
                    return new { bytes = length, omitted = "unknown_route_schema", wire = Wire.Describe(bytes, null) };
                type = route[response ? 1 : 0];
                wire = Wire.Describe(bytes, type);
                var parser = Parser(type);
                var data = new Il2CppStructArray<byte>(bytes);
                var message = parser.Parse.Invoke(parser.Parser, new object[] { data })!;
                try
                {
                    unknown = message.GetType().GetProperty("_unknownFields")?.GetValue(message) is not null;
                    value = Privacy.Parse(((Il2CppSystem.Object)message).ToString());
                }
                finally { (message as IDisposable)?.Dispose(); }
            }
            return new { bytes = length, protobuf_type = type, unknown_fields_omitted = unknown, wire, payload = Plugin.Privacy.Clean(value) };
        }
        catch (Exception ex)
        {
            Plugin.ReportFailure("body_decode", ex);
            return new { bytes = length, omitted = "body_decode_failed", wire, error = (ex.InnerException ?? ex).GetType().Name };
        }
    }

    private static (object Parser, MethodInfo Parse) Parser(string name)
    {
        lock (ParserGate)
        {
            if (Parsers.TryGetValue(name, out var cached)) return cached;
            var parser = Plugin.ResolveType(name).GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var parse = parser.GetType().GetMethod("ParseFrom", new[] { typeof(Il2CppStructArray<byte>) })!;
            return Parsers[name] = (parser, parse);
        }
    }

    private static Dictionary<string, string> Headers(Func<string, string?> read)
    {
        var result = new Dictionary<string, string>();
        foreach (var name in SafeHeaders)
        {
            string? value;
            try { value = read(name); }
            catch { continue; }
            if (!string.IsNullOrEmpty(value) && value.Length <= 256) result[name] = value;
        }
        return result;
    }

    private static byte[] Copy(Il2CppStructArray<byte>? source)
    {
        if (source is null || source.Length == 0) return Array.Empty<byte>();
        return source.AsSpan().ToArray();
    }

    internal static byte[] Decrypt(byte[] frame)
    {
        if (frame.Length < 17 || (frame.Length - 1) % 16 != 0) throw new InvalidDataException();
        var seed = Convert.FromHexString("487A9961C947F7D92ED6B79FC0545FEA");
        var prefix = frame[0];
        if (prefix > 127) Array.Reverse(seed);
        var shift = prefix & 7;
        var offset = prefix >> 3;
        var key = new byte[16];
        for (var i = 0; i < key.Length; i++)
            key[i] = (byte)((seed[(i + offset) & 15] << shift)
                | (shift == 0 ? 0 : seed[(i + offset + 1) & 15] >> (8 - shift)));
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(frame.AsSpan(1), Convert.FromHexString("65A99B89A634FCA3193C5212E5219378"));
    }
}

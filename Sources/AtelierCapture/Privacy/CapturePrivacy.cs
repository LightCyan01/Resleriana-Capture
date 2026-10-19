using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AtelierCapture;

internal sealed class CapturePrivacy(bool enabled)
{
    internal static readonly CapturePrivacy Disabled = new(false);
    private static readonly ConcurrentDictionary<
        Type,
        Dictionary<string, PropertyInfo>
    > Properties = new();
    private static readonly Lazy<Dictionary<string, string[]>> Routes = new(() =>
        Plugin.ReadResource<Dictionary<string, string[]>>("Routes.json")
    );

    internal bool Enabled { get; } = enabled;
    internal object Metadata =>
        new
        {
            enabled = Enabled,
            policy_version = 2,
            mode = Enabled ? "filtered_gameplay" : "full_raw",
            account_import_complete = false,
            original_bodies = Enabled ? "omitted" : "preserved_when_observed",
            uncertain_evidence = Enabled ? "omitted" : "preserved_with_gaps",
        };

    internal static CapturePrivacy Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                using var created = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read
                );
                created.Write("{\n  \"enabled\": false\n}\n"u8);
                created.Flush(flushToDisk: true);
                return Disabled;
            }
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 4096)
                throw new InvalidDataException("Privacy configuration exceeds 4096 bytes.");
            using var document = JsonDocument.Parse(
                input,
                new JsonDocumentOptions { AllowDuplicateProperties = false, MaxDepth = 2 }
            );
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("enabled", out var setting)
                || setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            )
                throw new InvalidDataException(
                    "Privacy configuration requires exactly one boolean enabled setting."
                );
            return new CapturePrivacy(setting.GetBoolean());
        }
        catch (Exception error)
            when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException(
                "Privacy configuration is invalid or unreadable; capture was not started."
            );
        }
    }

    internal object SessionMetadata(object metadata) =>
        !Enabled
            ? metadata
            : new
            {
                plugin = "atelier.capture",
                version = BuildInfo.Version,
                supported_client = "2.16.0",
                informational_version = VersionText(Read(metadata, "informational_version")),
                assembly_sha256 = Hash(Read(metadata, "assembly_sha256")),
                identity_self_reported = true,
                observation_contract = "privacy_qualified_gameplay_with_explicit_omissions",
                privacy = Metadata,
                durability = new
                {
                    gameplay_and_network = "durable_before_callback_continuation",
                    ordinary_diagnostics = "durable_before_outer_callback_continuation",
                    warnings_and_known_material_errors = "queued_before_outer_callback_continuation",
                },
            };

    internal object Status =>
        new
        {
            complete = false,
            status = "incomplete",
            omitted = "privacy_mode",
            privacy = Metadata,
        };

    internal (string Kind, object? Data) Project(string kind, object? data, string system)
    {
        if (!Enabled)
            return (kind, data);
        var process = Text(Read(data, "scope")) == "process";
        var value = process ? Read(data, "data") : data;
        object result;
        var outputKind = kind;
        try
        {
            if (kind is "request" or "response")
                result = Api(kind, value);
            else if (
                kind == "asset_download_version"
                && value is AssetDownloadVersion asset
                && AssetDownloads.ValidVersion(asset.version)
            )
                result = asset;
            else if (
                kind == "asset_download_summary"
                && value is AssetDownloadSummary summary
                && summary.versions.All(entry =>
                    AssetDownloads.ValidVersion(entry.version) && entry.observed_send_calls >= 0
                )
            )
                result = summary;
            else if (kind == "raw_body")
                result = new
                {
                    request_id = Id(Read(value, "request_id")),
                    attempt_id = Id(Read(value, "attempt_id")),
                    response_id = Id(Read(value, "response_id")),
                    route = Route(Read(value, "route")),
                    direction = Text(Read(value, "direction")) == "request"
                        ? "request"
                        : "response",
                    stage = Stage(Read(value, "stage")),
                    omitted = "privacy_mode",
                };
            else if (
                kind
                is "combat_enter"
                    or "combat_exit"
                    or "combat_exception"
                    or "native_enter"
                    or "native_exit"
                    or "native_exception"
                    or "unbound_combat_enter"
                    or "unbound_combat_exit"
                    or "unbound_combat_exception"
                    or "unbound_native_enter"
                    or "unbound_native_exit"
                    or "unbound_native_exception"
            )
                result = Native(value, system);
            else if (kind is "request_complete" or "request_exception" or "response_decode_failed")
                result = new
                {
                    request_id = Id(Read(value, "request_id")),
                    attempt_id = Id(Read(value, "attempt_id")),
                    route = Route(Read(value, "route")),
                    omitted = "privacy_mode",
                };
            else
            {
                outputKind = "privacy_omission";
                result = new { omitted = "privacy_mode", capture_status = "incomplete" };
            }
        }
        catch
        {
            // Never serialize the original value or exception as a projection fallback.
            outputKind = kind is "request" or "response" or "raw_body" ? kind : "privacy_omission";
            result = new { omitted = "privacy_projection_failed", capture_status = "incomplete" };
        }
        return (
            outputKind,
            process
                ? new
                {
                    scope = "process",
                    original_environment = "Unknown",
                    data = result,
                }
                : result
        );
    }

    private static object Api(string kind, object? value)
    {
        var route = Route(Read(value, "route"));
        var body = Read(value, "body");
        var type = Text(Read(body, "protobuf_type"));
        JsonNode? payload = null;
        IReadOnlyList<PrivacyPayload.Omission> omissions = [];
        try
        {
            if (
                Routes.Value.TryGetValue(route, out var expected)
                && type == expected[kind == "response" ? 1 : 0]
                && Read(body, "payload") is JsonNode original
            )
                payload = PrivacyPayload.Project(type, original, out _, out omissions);
        }
        catch
        {
            payload = null;
        }
        return new
        {
            request_id = Id(Read(value, "request_id")),
            attempt_id = Id(Read(value, "attempt_id")),
            response_id = Id(Read(value, "response_id")),
            route,
            status = kind == "response"
            && Read(value, "status") is int status
            && status is >= 100 and <= 599
                ? status
                : (int?)null,
            body = new
            {
                protobuf_type = payload is null ? null : type,
                payload,
                source = "privacy_gameplay_projection",
                privacy_omissions = omissions,
                omitted = "privacy_mode",
            },
            omitted = "privacy_mode",
        };
    }

    private static object Native(object? value, string system) =>
        PrivacyNative.Project(value, system);

    internal static bool IsFilteredMetadata(JsonElement? metadata) =>
        metadata is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("privacy", out var privacy)
        && privacy.ValueKind == JsonValueKind.Object
        && privacy.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True
        && privacy.TryGetProperty("policy_version", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var number)
        && number is 1 or 2
        && privacy.TryGetProperty("mode", out var mode)
        && mode.ValueKind == JsonValueKind.String
        && mode.GetString() == "filtered_gameplay";

    internal static object? Read(object? value, string name)
    {
        if (value is null)
            return null;
        if (value is JsonObject json)
            return json[name];
        return Properties
            .GetOrAdd(
                value.GetType(),
                type =>
                    type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Where(property => property.GetIndexParameters().Length == 0)
                        .ToDictionary(property => property.Name, StringComparer.Ordinal)
            )
            .GetValueOrDefault(name)
            ?.GetValue(value);
    }

    private static string? Text(object? value) =>
        value is string text ? text
        : value is JsonValue json && json.TryGetValue<string>(out var item) ? item
        : null;

    private static string? Id(object? value)
    {
        var text = Text(value);
        return
            text is not null
            && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0
            && number.ToString(CultureInfo.InvariantCulture) == text
            ? text
            : null;
    }

    private static string Route(object? value)
    {
        var text = Text(value);
        return text is not null && Routes.Value.ContainsKey(text) ? text : "/privacy/unknown";
    }

    private static string Stage(object? value) =>
        Text(value)
            is "request_entity"
                or "response_entity"
                or "native_request_protobuf"
                or "native_decoder_result"
                or "client_decoder_result"
                or "derived_decrypted"
                or "derived_decompressed"
            ? Text(value)!
            : "unknown";

    private static string? Hash(object? value) =>
        Text(value) is { Length: 64 } text && text.All(Uri.IsHexDigit) ? text : null;

    private static string? VersionText(object? value) =>
        Text(value) is { Length: <= 128 } text
        && text.All(character =>
            char.IsAsciiDigit(character)
            || character is '.' or '+'
            || character is >= 'a' and <= 'f'
        )
            ? text
            : null;
}

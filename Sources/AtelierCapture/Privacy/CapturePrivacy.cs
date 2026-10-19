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
            policy_version = 3,
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

    internal object Status(object? value) =>
        new
        {
            complete = false,
            status = "incomplete",
            omitted = "privacy_mode",
            privacy = Metadata,
            native_observers_closed = Boolean(Read(value, "native_observers_closed")),
            combat = new
            {
                configured = Integer(Read(Read(value, "combat"), "configured")),
                installed = Integer(Read(Read(value, "combat"), "installed")),
                bound_count = Integer(Read(Read(value, "combat"), "bound_count")),
                readers_checked = Boolean(Read(Read(value, "combat"), "readers_checked")),
                complete = Boolean(Read(Read(value, "combat"), "complete")),
            },
            network = new
            {
                active_attempts = Integer(Read(Read(value, "network"), "active_attempts")),
                binding_complete = Boolean(Read(Read(value, "network"), "binding_complete")),
                native_observers_closed = Boolean(
                    Read(Read(value, "network"), "native_observers_closed")
                ),
            },
            runtime = new
            {
                client_verified = Boolean(Read(Read(value, "runtime"), "client_verified")),
                safe_api_runtime_verified = Boolean(
                    Read(Read(value, "runtime"), "safe_api_runtime_verified")
                ),
                native_gameplay_runtime_verified = Boolean(
                    Read(Read(value, "runtime"), "native_gameplay_runtime_verified")
                ),
            },
            storage = new
            {
                aborted = Boolean(Read(Read(value, "storage"), "aborted")),
                storage_failures = Integer(Read(Read(value, "storage"), "storage_failures")),
                storage_error_code = Integer(Read(Read(value, "storage"), "storage_error_code")),
                old_recovery_failures = Integer(
                    Read(Read(value, "storage"), "old_recovery_failures")
                ),
                durable_current = Boolean(Read(Read(value, "storage"), "durable_current")),
                capture_zip_complete = Boolean(
                    Read(Read(value, "storage"), "capture_zip_complete")
                ),
            },
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
            else if (kind is "native_parse_input" or "native_parse_applied")
                result = Parsed(value, kind == "native_parse_input");
            else if (
                kind
                is "unclassified_native_parse_input"
                    or "unclassified_native_parse_applied"
                    or "unclassified_api_descriptor_binding"
            )
                result = new
                {
                    capture_status = "incomplete",
                    data = Project(kind[13..], Read(value, "data"), system).Data,
                };
            else if (kind == "api_descriptor_binding")
                result = new
                {
                    request_id = Id(Read(value, "request_id")),
                    attempt_id = Id(Read(value, "attempt_id")),
                    operation_id = Operation(Read(value, "operation_id")),
                    native_operation_id = Operation(Read(value, "native_operation_id")),
                    descriptor_id = Id(Read(value, "descriptor_id")),
                    request_header_id = Operation(Read(value, "request_header_id")),
                    logical_route = KnownRoute(Read(value, "logical_route")),
                    transport_path = KnownRoute(Read(value, "transport_path")),
                    recorded_route = KnownRoute(Read(value, "recorded_route")),
                    original_descriptor = Boolean(Read(value, "original_descriptor")),
                };
            else if (kind == "capture_status")
                result = Status(value);
            else if (
                kind == "unclassified_api_capture_incomplete"
                && Read(value, "data") is ApiCaptureGap unclassified
            )
                result = new
                {
                    capture_status = "incomplete",
                    data = Project("api_capture_incomplete", unclassified, system).Data,
                };
            else if (
                kind == "api_capture_incomplete"
                && Text(Read(value, "reason")) == "shared_native_body_unqualified"
            )
                result = Network.MaterializationBoundary();
            else if (kind == "api_capture_incomplete" && value is ApiCaptureGap gap)
                result = new
                {
                    gap.capture_status,
                    gap.reason,
                    details = new
                    {
                        request_id = Id(Read(gap.details, "request_id")),
                        attempt_id = Id(Read(gap.details, "attempt_id")),
                        response_id = Id(Read(gap.details, "response_id")),
                        route = Route(Read(gap.details, "route")),
                        stage = Stage(Read(gap.details, "stage")),
                    },
                    omitted = "privacy_mode",
                };
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
            else if (
                kind
                is "request_complete"
                    or "request_exception"
                    or "response_decode_failed"
                    or "request_disposed"
            )
                result = new
                {
                    request_id = Id(Read(value, "request_id")),
                    attempt_id = Id(Read(value, "attempt_id")),
                    operation_id = Operation(Read(value, "operation_id")),
                    route = Route(Read(value, "route")),
                    state = Integer(Read(value, "state")),
                    status = Integer(Read(value, "status")),
                    response_available = Boolean(Read(value, "response_available")),
                    response_recorded = Boolean(Read(value, "response_recorded")),
                    response_pending_commit = Boolean(Read(value, "response_pending_commit")),
                    terminal_already_staged = Boolean(Read(value, "terminal_already_staged")),
                    retries = Integer(Read(value, "retries")),
                    max_retries = Integer(Read(value, "max_retries")),
                    elapsed_ms = NonnegativeNumber(Read(value, "elapsed_ms")),
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
        var derivedJson = Text(Read(body, "source")) == "derived_json";
        var status = Integer(Read(value, "status"));
        var emptyProof =
            Read(body, "framing") is ResponseEntityFraming framing
            && Network.IsVerifiedEmptyEntity(framing);
        JsonNode? payload = null;
        IReadOnlyList<PrivacyPayload.Omission> omissions = [];
        try
        {
            var projectionType =
                route == "/status" && derivedJson ? "capture.json.Status"
                : derivedJson && kind == "response" && status is not (null or 200)
                    ? "capture.json.Error"
                : Routes.Value.TryGetValue(route, out var expected)
                && (derivedJson || type == expected[kind == "response" ? 1 : 0])
                    ? expected[kind == "response" ? 1 : 0]
                : null;
            if (Read(body, "payload") is JsonNode original && projectionType is not null)
                payload = PrivacyPayload.Project(projectionType, original, out _, out omissions);
            else if (!emptyProof)
                omissions = [new("$body", "$payload", "unqualified_body_representation", 1)];
        }
        catch
        {
            payload = null;
            omissions = [new("$body", "$payload", "privacy_projection_failed", 1)];
        }
        return new
        {
            request_id = Id(Read(value, "request_id")),
            attempt_id = Id(Read(value, "attempt_id")),
            response_id = Id(Read(value, "response_id")),
            operation_id = Operation(Read(value, "operation_id")),
            native_operation_id = Operation(Read(value, "native_operation_id")),
            request_header_id = Operation(Read(value, "request_header_id")),
            descriptor_id = Id(Read(value, "descriptor_id")),
            identity_proof = IdentityProof(Read(value, "identity_proof")),
            route,
            logical_route = KnownRoute(Read(value, "logical_route")),
            transport_path = KnownRoute(Read(value, "transport_path")),
            method = Text(Read(value, "method"))
                is "Get"
                    or "Post"
                    or "Put"
                    or "Delete"
                    or "Patch"
                    or "Head"
                    or "Options"
                ? Text(Read(value, "method"))
                : null,
            status = kind == "response" && status is >= 100 and <= 599 ? (int?)status : (int?)null,
            headers = Headers(Read(value, "headers")),
            body = new
            {
                protobuf_type = payload is null ? null : type,
                payload,
                entity_status = emptyProof ? "verified_empty_by_native_framing" : null,
                source = "privacy_gameplay_projection",
                privacy_omissions = omissions,
                omitted = "privacy_mode",
            },
            omitted = "privacy_mode",
        };
    }

    private static object Native(object? value, string system) =>
        PrivacyNative.Project(value, system);

    private static object Parsed(object? value, bool before)
    {
        var type = Text(Read(value, "native_message_type"));
        var slot = before ? "message_before" : "payload";
        IReadOnlyList<PrivacyPayload.Omission> omissions = [];
        var payload =
            type is not null && Read(value, slot) is JsonNode input
                ? PrivacyPayload.Project(type, input, out _, out omissions)
                : null;
        if (payload is null && omissions.Count == 0)
            omissions = [new("$native_message", "$payload", "unavailable_decoded_payload", 1)];
        return new Dictionary<string, object?>
        {
            ["request_id"] = Id(Read(value, "request_id")),
            ["attempt_id"] = Id(Read(value, "attempt_id")),
            ["response_id"] = Id(Read(value, "response_id")),
            ["native_message_type"] = payload is null ? null : type,
            [slot] = payload,
            ["source"] = "privacy_gameplay_projection",
            ["lineage_complete"] = Boolean(Read(value, "lineage_complete")),
            ["original_returned"] = Boolean(Read(value, "original_returned")),
            ["discard_unknown_fields"] = Boolean(Read(value, "discard_unknown_fields")),
            ["correlation_status"] = Text(Read(value, "correlation_status"))
                is "original_input_unbound"
                    or "original_decoder_return_object"
                ? Text(Read(value, "correlation_status"))
                : null,
            ["privacy_omissions"] = omissions,
            ["omitted"] = "privacy_mode",
        };
    }

    internal static bool IsFilteredMetadata(JsonElement? metadata) =>
        metadata is { ValueKind: JsonValueKind.Object } value
        && value.TryGetProperty("privacy", out var privacy)
        && privacy.ValueKind == JsonValueKind.Object
        && privacy.TryGetProperty("enabled", out var enabled)
        && enabled.ValueKind == JsonValueKind.True
        && privacy.TryGetProperty("policy_version", out var version)
        && version.ValueKind == JsonValueKind.Number
        && version.TryGetInt32(out var number)
        && number is 1 or 2 or 3
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
        return text is not null && (text == "/status" || Routes.Value.ContainsKey(text))
            ? text
            : "/privacy/unknown";
    }

    private static object? Headers(object? value)
    {
        if (value is not HeaderSnapshot snapshot)
            return null;
        var groups = snapshot
            .groups.Where(group =>
                group.name?.ToLowerInvariant()
                    is "content-type"
                        or "content-length"
                        or "content-encoding"
                        or "accept"
                        or "accept-encoding"
                        or "cache-control"
                        or "date"
                        or "server"
                        or "x-client-version"
                        or "x-asset-version"
                        or "x-master-data-version"
                        or "x-platform"
                        or "x-server-timestamp"
                        or "x-content-encoding"
                        or "x-request-id"
            )
            .Select(group => new
            {
                group.name,
                group.values,
                group.complete,
            })
            .ToArray();
        return new
        {
            groups,
            complete = snapshot.complete && groups.Length == snapshot.groups.Count,
            omitted_groups = snapshot.groups.Count - groups.Length,
        };
    }

    private static long? Integer(object? value) =>
        value switch
        {
            int number => number,
            long number => number,
            JsonValue json when json.TryGetValue<long>(out var number) => number,
            _ => null,
        };

    private static bool? Boolean(object? value) =>
        value is bool flag ? flag
        : value is JsonValue json && json.TryGetValue<bool>(out var item) ? item
        : null;

    internal static double? NonnegativeNumber(object? value)
    {
        var number =
            value is double real ? real
            : value is JsonValue json && json.TryGetValue<double>(out var item) ? item
            : double.NaN;
        return double.IsFinite(number) && number >= 0 ? number : null;
    }

    internal static string? Operation(object? value) =>
        RequestLedger.CanonicalOperation(Text(value));

    internal static string? KnownRoute(object? value) =>
        Text(value) is { } route && (route == "/status" || Routes.Value.ContainsKey(route))
            ? route
            : null;

    internal static string? IdentityProof(object? value) =>
        Text(value) is "native_descriptor" or "native_request_header" ? Text(value) : null;

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

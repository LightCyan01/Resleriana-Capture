using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtelierCapture.Archive;

internal sealed class ArchiveCounts : IDisposable
{
    internal static readonly string[] Names =
    [
        "api.jsonl",
        "combat.jsonl",
        "raw-bodies.jsonl",
        "diagnostics.jsonl",
    ];
    internal static readonly string[] Streams = ["api", "combat", "raw", "diagnostics"];
    private static readonly HashSet<string> MarkerKinds = new(StringComparer.Ordinal)
    {
        "session_start",
        "session_end",
        "session_recovered",
        "capture_status",
        "capture_gap",
        "session_aborted",
        "process_bootstrap",
        "process_bootstrap_reference",
        "bootstrap_diagnostic",
    };
    private static readonly HashSet<string> Systems = new(StringComparer.Ordinal)
    {
        "Battle",
        "Atelier",
        "Recorder",
        "Service",
        "Unknown",
        "Account",
        "Auth",
        "Character",
        "Quest",
        "Gacha",
        "Mail",
        "Mission",
        "Home",
        "Party",
        "Inventory",
        "Shop",
        "Event",
        "LoginBonus",
        "Expedition",
        "Exploration",
        "Ship",
        "Synthesis",
        "Research",
        "Tutorial",
    };
    private readonly SortedIds identities;
    private readonly WarningCounts warnings,
        runtimeErrors;
    private readonly int contractVersion;
    private readonly Dictionary<string, SystemCount> systems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RouteCount> routes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoundaryCount> boundaries = new(StringComparer.Ordinal);
    private readonly long[] records = new long[4],
        envelopes = new long[4],
        markers = new long[4];
    private readonly Dictionary<string, long> rawStages = new(StringComparer.Ordinal);
    private readonly HashSet<string> reasons = new(StringComparer.Ordinal);
    private string? run,
        environment;
    private int schema;
    private long requests,
        responses,
        completions,
        sendFailures,
        decodeFailures,
        nativeRecords;
    private long identifiedCalls,
        callsReturned,
        callsThrew,
        callsOpen,
        orphanTerminals,
        legacyNative;
    private long attempts,
        pairedResponses,
        terminalAttempts;
    private long operationIds,
        provenOperationAttempts,
        uncorrelatedAttempts;
    private bool allAttemptIds = true;
    private bool finished;
    private BootstrapOwnership? bootstrap;
    private bool bootstrapUnverified;
    internal BootstrapOwnership? BootstrapOwnership => bootstrapUnverified ? null : bootstrap;
    internal object? WarningSummary => warnings.HasRecords ? warnings.Summary() : null;
    internal object? ErrorSummary => runtimeErrors.HasRecords ? runtimeErrors.Summary() : null;
    internal RecordProjection? Start { get; private set; }
    internal RecordProjection? End { get; private set; }
    internal long Dropped { get; private set; }
    internal List<JsonElement> Status { get; } = new();
    internal bool StatusTruncated { get; private set; }
    internal bool Recovered { get; private set; }
    internal readonly Dictionary<string, long> Errors = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, long> ErrorSamples = new(StringComparer.Ordinal);
    internal Dictionary<string, long>? FinalErrors;
    internal bool ErrorsTruncated;

    internal long RecordCount(int stream) => records[stream];

    internal void Incomplete(string reason) => reasons.Add(reason);

    internal ArchiveCounts(string scratchOwner, int contractVersion = 2)
    {
        if (contractVersion is not (1 or 2))
            throw new InvalidDataException("Capture count contract version is unsupported.");
        this.contractVersion = contractVersion;
        identities = new SortedIds(scratchOwner);
        warnings = new WarningCounts(identities);
        runtimeErrors = new WarningCounts(identities, errorFamily: true);
        foreach (var system in Systems.Order(StringComparer.Ordinal))
            systems.Add(system, new SystemCount());
        foreach (var route in RouteCountCatalog.Routes)
        {
            systems.TryAdd(route.Value, new SystemCount());
            routes.Add(route.Key, new RouteCount(route.Value));
        }
        foreach (var boundary in NativeCountCatalog.Boundaries)
            boundaries.Add(
                NativeCountCatalog.Key(boundary.System, boundary.Boundary, boundary.Variant),
                new BoundaryCount(boundary)
            );
    }

    internal void SetIdentity(string runId, string source, int recordSchema)
    {
        if (
            runId.Length != 32
            || !runId.All(Uri.IsHexDigit)
            || source is not ("Official" or "Private")
            || recordSchema is not (4 or 5)
        )
            throw new InvalidDataException("Capture archive identity is unsupported.");
        if (run is not null && (run != runId || environment != source || schema != recordSchema))
            throw new InvalidDataException("Capture archive identity changed.");
        run = runId;
        environment = source;
        schema = recordSchema;
    }

    internal void Add(int stream, RecordProjection record)
    {
        var kind =
            record.Get("kind") ?? throw new InvalidDataException("Capture record has no kind.");
        var marker = MarkerKinds.Contains(kind);
        var hasSequence = record.Root.ContainsKey("sequence");
        if (kind == "session_start")
        {
            if (Start is not null)
                throw new InvalidDataException("Capture has duplicate session starts.");
            SetIdentity(
                record.Get("run_id") ?? "",
                record.Get("environment") ?? "",
                (int)RecordProjection.Integer(record.Get("schema"))
            );
            if (record.Metadata is null)
                throw new InvalidDataException("Capture start has no producer metadata.");
            Start = record;
        }
        if (run is null)
            throw new InvalidDataException("Capture session identity is not known.");
        if (!marker || hasSequence)
        {
            if (
                record.Get("run_id") != run
                || record.Get("environment") != environment
                || record.Get("schema") != schema.ToString(CultureInfo.InvariantCulture)
                || record.Get("stream") != Streams[stream]
            )
                throw new InvalidDataException(
                    "Capture envelope identity differs from its archive."
                );
            var sequence = RecordProjection.Integer(record.Get("sequence"), true);
            if (
                record.RootKinds.GetValueOrDefault("schema") != JsonValueKind.Number
                || record.RootKinds.GetValueOrDefault("sequence") != JsonValueKind.Number
                || record.RootKinds.GetValueOrDefault("thread") != JsonValueKind.Number
                || record.RootKinds.GetValueOrDefault("elapsed_ticks") != JsonValueKind.Number
                || !DateTimeOffset.TryParse(
                    record.Get("utc"),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out _
                )
            )
                throw new InvalidDataException("Capture envelope control types are invalid.");
            RecordProjection.Integer(record.Get("thread"), true);
            RecordProjection.Integer(record.Get("elapsed_ticks"));
            if (
                schema == 5
                && record.RootKinds.GetValueOrDefault("system_id") != JsonValueKind.String
            )
                throw new InvalidDataException(
                    "Capture schema 5 event lacks a typed primary system."
                );
            identities.Add(
                new IdRow(0, sequence.ToString("D19", CultureInfo.InvariantCulture), 0, "")
            );
            envelopes[stream] = checked(envelopes[stream] + 1);
        }
        else
        {
            if (stream != 3)
                throw new InvalidDataException("Capture session marker is outside diagnostics.");
            if (record.Root.GetValueOrDefault("run_id") is { } markerRun && markerRun != run)
                throw new InvalidDataException("Capture marker belongs to another run.");
            if (
                record.Root.GetValueOrDefault("environment") is { } markerEnvironment
                && markerEnvironment != environment
            )
                throw new InvalidDataException("Capture marker belongs to another environment.");
            markers[stream] = checked(markers[stream] + 1);
        }
        records[stream] = checked(records[stream] + 1);
        var declaredSystem =
            record.Root.GetValueOrDefault("system_id")
            ?? record.Data.GetValueOrDefault("system_id");
        if (
            record.Root.GetValueOrDefault("system_id") is { } envelopeSystem
            && record.Data.GetValueOrDefault("system_id") is { } payloadSystem
            && envelopeSystem != payloadSystem
        )
            throw new InvalidDataException("Capture envelope/native system owners disagree.");
        var system =
            marker && !hasSequence ? "Recorder"
            : declaredSystem is not null && systems.ContainsKey(declaredSystem) ? declaredSystem
            : "Unknown";
        var owner = systems[system];
        owner.Records[stream] = checked(owner.Records[stream] + 1);
        if (system == "Unknown")
            reasons.Add("system_owner_unverified");
        if (record.Get("complete") == "false" || record.Get("status") == "incomplete")
            reasons.Add("required_observation_incomplete");
        if (record.HasOmission)
            reasons.Add("explicit_observation_omission");
        if (
            record.Get("capture_status") == "incomplete"
            || record.Get("correlation_gap") is not null
        )
            reasons.Add("causal_observation_incomplete");
        if (record.Get("compatible") == "false")
            reasons.Add("runtime_or_binding_incompatible");

        switch (kind)
        {
            case "process_bootstrap":
            case "process_bootstrap_reference":
                Bootstrap(record, kind == "process_bootstrap");
                if (record.BootstrapOriginal is { } original)
                {
                    Diagnostic(original);
                    switch (original.Get("kind"))
                    {
                        case "unbound_combat_enter":
                        case "unbound_native_enter":
                            UnboundNative(original, 0);
                            break;
                        case "unbound_combat_exit":
                        case "unbound_native_exit":
                            UnboundNative(original, 1);
                            break;
                        case "unbound_combat_exception":
                        case "unbound_native_exception":
                            UnboundNative(original, 2);
                            break;
                    }
                }
                break;
            case "runtime_log":
            case "runtime_warning_repeat":
            case "runtime_warning_summary":
            case "runtime_error_repeat":
            case "runtime_error_summary":
                Diagnostic(record);
                break;
            case "unbound_combat_enter":
            case "unbound_native_enter":
                UnboundNative(record, 0);
                break;
            case "unbound_combat_exit":
            case "unbound_native_exit":
                UnboundNative(record, 1);
                break;
            case "unbound_combat_exception":
            case "unbound_native_exception":
                UnboundNative(record, 2);
                break;
            case "session_end":
                if (End is not null)
                    throw new InvalidDataException("Capture has duplicate session ends.");
                End = record;
                break;
            case "session_recovered":
                if (Recovered)
                    throw new InvalidDataException("Capture has duplicate recovery markers.");
                Recovered = true;
                break;
            case "session_aborted":
                reasons.Add("explicit_abort");
                break;
            case "capture_gap":
                Dropped = checked(Dropped + RecordProjection.Integer(record.Get("dropped_events")));
                reasons.Add("reported_capture_gap");
                break;
            case "capture_status":
                if (Status.Count < 64)
                    Status.Add(record.Control!.Value);
                else
                    StatusTruncated = true;
                if (record.Control is { } control && HasFailure(control))
                    reasons.Add("producer_status_incomplete");
                if (
                    record.Control is { } status
                    && status.TryGetProperty("observer_errors", out var totals)
                )
                {
                    var final = new Dictionary<string, long>(StringComparer.Ordinal);
                    foreach (var value in totals.EnumerateObject())
                    {
                        if (
                            value.Name.Length > 8192
                            || final.Count >= 256
                            || !final.TryAdd(value.Name, ReadCount(value.Value))
                        )
                            throw new InvalidDataException(
                                "Capture observer error totals are invalid."
                            );
                    }
                    FinalErrors = final;
                }
                break;
            case "request":
                RequireStream(stream, 0);
                requests = checked(requests + 1);
                owner.Requests = checked(owner.Requests + 1);
                AddAttempt(record, 0);
                CountRoute(record, false, system);
                if (
                    record.Get("operation_id") is { } operation
                    && record.Get("identity_proof")
                        is "native_descriptor"
                            or "native_request_header"
                )
                {
                    if (
                        !Guid.TryParseExact(operation, "D", out var operationUuid)
                        || operationUuid == Guid.Empty
                    )
                        throw new InvalidDataException("Capture proven operation ID is invalid.");
                    identities.Add(
                        new IdRow(3, operationUuid.ToString("D"), 0, record.Get("route") ?? "")
                    );
                    provenOperationAttempts = checked(provenOperationAttempts + 1);
                }
                else
                    uncorrelatedAttempts = checked(uncorrelatedAttempts + 1);
                break;
            case "response":
                RequireStream(stream, 0);
                responses = checked(responses + 1);
                owner.Responses = checked(owner.Responses + 1);
                AddAttempt(record, 1);
                CountRoute(record, true, system);
                break;
            case "request_complete":
                completions = checked(completions + 1);
                AddAttempt(record, 2);
                break;
            case "request_exception":
                sendFailures = checked(sendFailures + 1);
                AddAttempt(record, 3);
                break;
            case "response_decode_failed":
                decodeFailures = checked(decodeFailures + 1);
                reasons.Add("response_decode_failed");
                break;
            case "api_capture_incomplete":
                reasons.Add("api_capture_incomplete");
                break;
            case "runtime_compatibility":
                if (record.Get("status") != "compatible" && record.Get("compatible") != "true")
                    reasons.Add("runtime_compatibility_unverified");
                break;
            case "request_tracking_gap":
                reasons.Add("request_tracking_gap");
                break;
            case "observer_error":
                reasons.Add("observer_error");
                var observer = record.Get("observer") ?? "unknown";
                var error = record.Get("error") ?? "unknown";
                Sample(Errors, observer);
                Sample(ErrorSamples, observer + ":" + error);
                break;
            case "raw_body":
                RequireStream(stream, 2);
                VerifyBody(record);
                break;
            case "combat_enter":
            case "native_enter":
                RequireStream(stream, 1);
                Native(record, owner, system, 0);
                break;
            case "combat_exit":
            case "native_exit":
                RequireStream(stream, 1);
                Native(record, owner, system, 1);
                break;
            case "combat_exception":
            case "native_exception":
                RequireStream(stream, 1);
                Native(record, owner, system, 2);
                break;
        }
        if (stream == 0 && kind is not ("request" or "response"))
            throw new InvalidDataException("Capture API stream has a non-API record.");
        if (stream == 2 && kind != "raw_body")
            throw new InvalidDataException("Capture raw stream has a non-body record.");
    }

    private void Bootstrap(RecordProjection record, bool transferred)
    {
        var source = transferred ? record.BootstrapSource : record;
        if (source is null || source.Get("checkpoint_sha256") is null)
        {
            bootstrapUnverified = true;
            return;
        }
        var sourceRun = source.Get("run_id");
        var sourceEnvironment = source.Get("environment");
        var checkpointHash = source.Get("checkpoint_sha256")!;
        var diagnosticsHash = source.Get("diagnostics_sha256") ?? "";
        var sourceRecords = RecordProjection.Integer(source.Get("records"));
        if (
            sourceRun != run
            || sourceEnvironment != "Unknown"
            || source.Get("scope") != "process"
            || !IsHash(checkpointHash)
            || !IsHash(diagnosticsHash)
            || transferred && record.BootstrapOriginal is null
        )
            throw new InvalidDataException("Capture bootstrap ownership reference is invalid.");
        if (
            transferred
            && record.BootstrapOriginal is { } original
            && (
                original.Get("run_id") is { } originalRun && originalRun != sourceRun
                || original.Get("environment") is { } originalEnvironment
                    && originalEnvironment != "Unknown"
            )
        )
            throw new InvalidDataException("Capture bootstrap original belongs to another source.");
        var current = new BootstrapOwnership(
            sourceRun!,
            sourceEnvironment,
            checkpointHash,
            diagnosticsHash,
            sourceRecords,
            0,
            0
        );
        if (
            bootstrap is not null
            && current != (bootstrap with { TransferredRecords = 0, ReferenceRecords = 0 })
        )
            throw new InvalidDataException("Capture bootstrap source ownership changed.");
        bootstrap = current with
        {
            TransferredRecords = checked(
                (bootstrap?.TransferredRecords ?? 0) + (transferred ? 1 : 0)
            ),
            ReferenceRecords = checked((bootstrap?.ReferenceRecords ?? 0) + (transferred ? 0 : 1)),
        };
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);

    private void Diagnostic(RecordProjection record)
    {
        warnings.Add(record);
        runtimeErrors.Add(record);
    }

    private void UnboundNative(RecordProjection record, byte phase)
    {
        if (contractVersion == 1)
            return;
        var native = record.Payload;
        if (native is null || native.Get("invocation_id") is null)
        {
            reasons.Add("native_invocation_identity_unavailable");
            return;
        }
        var system = native.Get("system_id") ?? "Unknown";
        if (!systems.ContainsKey(system))
            system = "Unknown";
        reasons.Add("native_origin_unverified");
        Native(native, systems[system], system, phase);
    }

    private void CountRoute(RecordProjection record, bool response, string system)
    {
        var route = record.Get("route");
        if (route is null || !routes.TryGetValue(route, out var counter))
            return;
        if (system != "Unknown" && counter.System != system)
            throw new InvalidDataException("Capture route and primary system inventory disagree.");
        if (response)
            counter.Responses = checked(counter.Responses + 1);
        else
            counter.Requests = checked(counter.Requests + 1);
    }

    private void Native(RecordProjection record, SystemCount owner, string system, byte phase)
    {
        nativeRecords = checked(nativeRecords + 1);
        owner.NativeRecords = checked(owner.NativeRecords + 1);
        var id = record.Get("invocation_id");
        if (id is null)
        {
            legacyNative = checked(legacyNative + 1);
            reasons.Add("native_invocation_identity_unavailable");
            return;
        }
        RecordProjection.Integer(id, true);
        if (
            schema == 5
            && record.DataKinds.GetValueOrDefault("invocation_id") != JsonValueKind.String
        )
            throw new InvalidDataException("Capture native ID is not a schema-5 decimal string.");
        var boundary = record.Get("boundary_id");
        if (boundary is null)
        {
            reasons.Add("native_boundary_identity_unavailable");
            legacyNative = checked(legacyNative + 1);
            return;
        }
        var signature = system + "\n" + boundary + "\n" + record.Get("variant_id");
        if (boundaries.TryGetValue(signature, out var counter))
            counter.Records = checked(counter.Records + 1);
        else
            reasons.Add("native_boundary_inventory_unavailable");
        identities.Add(new IdRow(1, id.PadLeft(19, '0'), phase, signature));
    }

    private void AddAttempt(RecordProjection record, byte phase)
    {
        var requestId = record.Get("request_id");
        RecordProjection.Integer(requestId, true);
        var explicitAttempt = record.Get("attempt_id");
        if (explicitAttempt is null)
            allAttemptIds = false;
        else
            RecordProjection.Integer(explicitAttempt, true);
        if (
            schema == 5
            && (
                record.DataKinds.GetValueOrDefault("request_id") != JsonValueKind.String
                || explicitAttempt is not null
                    && record.DataKinds.GetValueOrDefault("attempt_id") != JsonValueKind.String
            )
        )
            throw new InvalidDataException("Capture request ID is not a schema-5 decimal string.");
        var id =
            requestId!.PadLeft(19, '0') + ":" + (explicitAttempt ?? requestId).PadLeft(19, '0');
        var signature = record.Get("route") ?? "";
        identities.Add(new IdRow(2, id, phase, signature));
        if (phase == 0)
            identities.Add(new IdRow(5, id, 0, BodyOwner(record)));
    }

    private static string BodyOwner(RecordProjection record) =>
        (record.Get("route") ?? "") + "\n" + (record.Get("system_id") ?? "Unknown");

    private static long ReadCount(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
            ? RecordProjection.Integer(value.GetString())
            : RecordProjection.Integer(value.GetRawText());

    private void Sample(Dictionary<string, long> target, string key)
    {
        if (target.TryGetValue(key, out var value))
            target[key] = checked(value + 1);
        else if (target.Count < 128)
            target.Add(key, 1);
        else
            ErrorsTruncated = true;
    }

    private void VerifyBody(RecordProjection record)
    {
        var requestId = record.Get("request_id");
        var attemptId = record.Get("attempt_id");
        if (requestId is not null)
            RecordProjection.Integer(requestId, true);
        if (attemptId is not null)
            RecordProjection.Integer(attemptId, true);
        if (
            schema == 5
            && (
                requestId is not null
                    && record.DataKinds.GetValueOrDefault("request_id") != JsonValueKind.String
                || attemptId is not null
                    && record.DataKinds.GetValueOrDefault("attempt_id") != JsonValueKind.String
            )
        )
            throw new InvalidDataException("Capture raw body ID is not a schema-5 decimal string.");
        if (requestId is null || schema == 5 && attemptId is null)
            reasons.Add("raw_body_request_identity_unavailable");
        else
        {
            if (attemptId is null)
                reasons.Add("raw_body_attempt_identity_unavailable");
            identities.Add(
                new IdRow(
                    5,
                    requestId.PadLeft(19, '0') + ":" + (attemptId ?? requestId).PadLeft(19, '0'),
                    1,
                    BodyOwner(record)
                )
            );
        }
        if (record.Get("route") is null)
            reasons.Add("raw_body_request_route_unverified");
        var direction = record.Get("direction");
        var stage = record.Get("stage");
        if (direction is not ("request" or "response") || stage is null)
            throw new InvalidDataException("Capture raw body association is invalid.");
        var knownStage =
            stage
            is "request_entity"
                or "response_entity"
                or "native_request_protobuf"
                or "native_decoder_result"
                or "client_decoder_result"
                or "derived_decrypted"
                or "derived_decompressed";
        var bucket = direction + ":" + (knownStage ? stage : "unknown");
        rawStages[bucket] = checked(rawStages.GetValueOrDefault(bucket) + 1);
        if (record.Get("omitted") is not null)
        {
            reasons.Add("raw_body_unavailable");
            return;
        }
        if (
            record.BodyBytes is null
            || record.BodyBytes != RecordProjection.Integer(record.Get("bytes"))
            || !string.Equals(
                record.BodyHash,
                record.Get("sha256"),
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new InvalidDataException("Capture raw body bytes/hash differ.");
        if (record.Get("body_id") is { } bodyId)
            identities.Add(
                new IdRow(
                    4,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyId))),
                    0,
                    ""
                )
            );
        else if (schema == 5)
            reasons.Add("raw_body_identity_unavailable");
    }

    internal void Finish()
    {
        if (finished)
            return;
        if (Start is null)
            throw new InvalidDataException("Capture session start is missing.");
        IdRow? previous = null;
        var phases = new int[4];
        string? requestOwner = null;
        foreach (var row in identities.Read())
        {
            if (previous is { } last && (last.Category != row.Category || last.Id != row.Id))
                Reduce(last);
            if (
                previous is { } prior
                && row.Category is not (5 or 6 or 7)
                && prior.Category == row.Category
                && prior.Id == row.Id
                && prior.Signature != row.Signature
                && prior.Signature.Length != 0
                && row.Signature.Length != 0
            )
                throw new InvalidDataException("Capture causal identity has conflicting owners.");
            if (row.Phase >= phases.Length)
                throw new InvalidDataException("Capture identity phase is invalid.");
            if (row.Category == 6)
                warnings.Read(row);
            else if (row.Category == 7)
                runtimeErrors.Read(row);
            else if (row.Category == 5)
            {
                if (row.Phase == 0)
                {
                    if (++phases[0] > 1)
                        throw new InvalidDataException("Capture request owner is duplicated.");
                    requestOwner = row.Signature;
                }
                else if (row.Phase == 1)
                {
                    phases[1] = 1;
                    if (requestOwner is not null)
                    {
                        if (requestOwner.StartsWith('\n') || row.Signature.StartsWith('\n'))
                            reasons.Add("raw_body_request_route_unverified");
                        else if (requestOwner != row.Signature)
                            reasons.Add("raw_body_request_owner_mismatch");
                    }
                }
                else
                    throw new InvalidDataException("Capture body-owner relation phase is invalid.");
            }
            else if (row.Category == 3)
                phases[0] = 1;
            else if (++phases[row.Phase] > 1)
                throw new InvalidDataException("Capture identity phase is duplicated.");
            previous = row;
        }
        if (previous is { } final)
            Reduce(final);
        if (
            End is not null
            && RecordProjection.Integer(End.Get("events")) != envelopes.SumChecked()
        )
            throw new InvalidDataException(
                "Capture session event count differs from actual envelopes."
            );
        if (End is null)
            reasons.Add("normal_shutdown_not_observed");
        if (Recovered)
            reasons.Add("recovered_prefix");
        if (
            bootstrap is { } ownership
            && (
                ownership.TransferredRecords != 0
                    && ownership.TransferredRecords != ownership.Records
                || ownership.ReferenceRecords > 1
                || ownership.TransferredRecords != 0 && ownership.ReferenceRecords != 0
            )
        )
            throw new InvalidDataException(
                "Capture bootstrap marker count differs from its source ownership."
            );
        finished = true;

        void Reduce(IdRow row)
        {
            if (row.Category is 0 or 4)
            {
                if (phases[0] != 1)
                    throw new InvalidDataException("Capture has duplicate record identities.");
            }
            else if (row.Category == 1)
            {
                if (phases.Any(count => count > 1) || phases[1] + phases[2] > 1)
                    throw new InvalidDataException(
                        "Capture native invocation has duplicate/conflicting phases."
                    );
                if (phases[0] == 1)
                {
                    identifiedCalls = checked(identifiedCalls + 1);
                    var system = row.Signature.Split('\n')[0];
                    systems[system].Calls = checked(systems[system].Calls + 1);
                    if (boundaries.TryGetValue(row.Signature, out var boundary))
                        boundary.Calls = checked(boundary.Calls + 1);
                    if (phases[1] == 1)
                        callsReturned = checked(callsReturned + 1);
                    else if (phases[2] == 1)
                        callsThrew = checked(callsThrew + 1);
                    else
                    {
                        callsOpen = checked(callsOpen + 1);
                        reasons.Add("open_native_invocation");
                    }
                }
                else
                {
                    orphanTerminals = checked(orphanTerminals + 1);
                    reasons.Add("native_terminal_without_entry");
                }
            }
            else if (row.Category == 2)
            {
                if (phases.Any(count => count > 1) || phases[2] + phases[3] > 1)
                    throw new InvalidDataException(
                        "Capture request has duplicate/conflicting phases."
                    );
                if (phases[0] == 1)
                {
                    attempts = checked(attempts + 1);
                    if (phases[1] == 1)
                        pairedResponses = checked(pairedResponses + 1);
                    if (phases[2] + phases[3] == 1)
                        terminalAttempts = checked(terminalAttempts + 1);
                    else
                        reasons.Add("open_request_observation");
                }
                else
                    reasons.Add("response_or_terminal_without_request");
            }
            else if (row.Category == 5)
            {
                if (phases[1] == 1 && phases[0] == 0)
                    reasons.Add("raw_body_without_request");
            }
            else if (row.Category == 6)
                warnings.FinishGroup();
            else if (row.Category == 7)
                runtimeErrors.FinishGroup();
            else
                operationIds = checked(operationIds + 1);
            Array.Clear(phases);
            requestOwner = null;
        }
    }

    internal object Summary()
    {
        Finish();
        return new
        {
            version = contractVersion,
            scope = "single_archive",
            basis = "verified_packaged_records",
            run_id = run,
            environment,
            schema,
            units = new
            {
                records = "jsonl_records",
                requests = "api_request_records",
                native_calls = "native_invocations",
            },
            streams = Names
                .Select(
                    (name, i) =>
                        new
                        {
                            name,
                            records = Text(records[i]),
                            envelope_records = Text(envelopes[i]),
                            marker_records = Text(markers[i]),
                        }
                )
                .ToArray(),
            api = new
            {
                request_records = Text(requests),
                response_records = Text(responses),
                response_pairs = Text(pairedResponses),
                request_completion_records = Text(completions),
                send_failure_records = Text(sendFailures),
                decode_failure_records = Text(decodeFailures),
                observed_send_invocations = allAttemptIds && schema == 5 ? Text(attempts) : null,
                observed_send_terminal = allAttemptIds && schema == 5
                    ? Text(terminalAttempts)
                    : null,
                physical_attempts = (string?)null,
                physical_attempts_complete = false,
                distinct_observed_operation_ids = Text(operationIds),
                proven_operation_request_records = Text(provenOperationAttempts),
                uncorrelated_request_records = Text(uncorrelatedAttempts),
                logical_operations = (string?)null,
                retry_attempts = (string?)null,
                identity_reason = "observed_operation_ids_do_not_prove_operation_start_or_internal_retries",
            },
            native = new
            {
                records = Text(nativeRecords),
                calls_started = legacyNative == 0 ? Text(identifiedCalls) : null,
                identified_calls_started = Text(identifiedCalls),
                calls_returned = Text(callsReturned),
                calls_threw = Text(callsThrew),
                calls_open = Text(callsOpen),
                terminal_without_entry = Text(orphanTerminals),
                legacy_records_without_invocation_id = Text(legacyNative),
                gameplay_operations = (string?)null,
                reason = "gameplay_operation_completion_not_proven",
            },
            systems = systems
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new
                {
                    system_id = pair.Key,
                    records_by_stream = Names
                        .Select((name, i) => new { name, records = Text(pair.Value.Records[i]) })
                        .ToArray(),
                    request_records = Text(pair.Value.Requests),
                    response_records = Text(pair.Value.Responses),
                    native_records = Text(pair.Value.NativeRecords),
                    identified_calls_started = Text(pair.Value.Calls),
                })
                .ToArray(),
            raw_bodies = rawStages
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { direction_stage = pair.Key, records = Text(pair.Value) })
                .ToArray(),
            routes = routes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new
                {
                    route_id = pair.Key,
                    system_id = pair.Value.System,
                    request_records = Text(pair.Value.Requests),
                    response_records = Text(pair.Value.Responses),
                })
                .ToArray(),
            boundaries = boundaries
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new
                {
                    system_id = pair.Value.Identity.System,
                    boundary_id = pair.Value.Identity.Boundary,
                    variant_id = pair.Value.Identity.Variant,
                    records = Text(pair.Value.Records),
                    identified_calls_started = Text(pair.Value.Calls),
                    state = pair.Value.Records == 0 ? "not_observed" : "observed",
                })
                .ToArray(),
            completeness = new
            {
                state = reasons.Count == 0 && schema == 5 ? "complete" : "incomplete",
                reasons = reasons.Order(StringComparer.Ordinal).ToArray(),
            },
            coverage = new
            {
                state = "not_certified",
                reason = "recorded_observations_do_not_establish_all_native_gameplay_coverage",
            },
        };
    }

    private static bool HasFailure(JsonElement root)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.TryPop(out var element))
        {
            if (element.ValueKind == JsonValueKind.Object)
                foreach (var property in element.EnumerateObject())
                {
                    if (
                        property.Name is "disabled_reason" or "activation_gap"
                        && property.Value.ValueKind == JsonValueKind.String
                    )
                        return true;
                    if (
                        property.Name is "skipped_roles" or "skipped"
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.EnumerateObject().Any()
                    )
                        return true;
                    if (
                        property.Name is "state" or "status"
                        && property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()
                            is "incomplete"
                                or "disabled"
                                or "mismatch"
                                or "unverified"
                    )
                        return true;
                    if (
                        (property.Name is "complete" or "compatible" or "readers_checked")
                        && property.Value.ValueKind == JsonValueKind.False
                    )
                        return true;
                    if (
                        property.Name == "observer_errors"
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property
                            .Value.EnumerateObject()
                            .Any(value => value.Value.TryGetInt64(out var count) && count > 0)
                    )
                        return true;
                    stack.Push(property.Value);
                }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var item in element.EnumerateArray())
                    stack.Push(item);
        }
        return false;
    }

    internal static string Text(long count) => count.ToString(CultureInfo.InvariantCulture);

    private static void RequireStream(int actual, int expected)
    {
        if (actual != expected)
            throw new InvalidDataException("Capture record kind is in the wrong stream.");
    }

    public void Dispose() => identities.Dispose();

    private sealed class SystemCount
    {
        internal readonly long[] Records = new long[4];
        internal long Requests,
            Responses,
            NativeRecords,
            Calls;
    }

    private sealed class RouteCount(string system)
    {
        internal readonly string System = system;
        internal long Requests,
            Responses;
    }

    private sealed class BoundaryCount(NativeCountBoundary identity)
    {
        internal readonly NativeCountBoundary Identity = identity;
        internal long Records,
            Calls;
    }
}

internal static class CheckedCounts
{
    internal static long SumChecked(this IEnumerable<long> values)
    {
        long sum = 0;
        foreach (var value in values)
            sum = checked(sum + value);
        return sum;
    }
}

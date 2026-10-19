using System.Diagnostics;
using System.Security.Cryptography;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static partial class Network
{
    private static void Send(dynamic __instance, out SendState? __state)
    {
        var lifetime = Plugin.BeginNativeCallback();
        var state = lifetime is null ? null : new SendState(lifetime);
        __state = state;
        if (state is null)
            return;
        if (IsAssetDownload((object)__instance, out var assetVersion))
        {
            state.Excluded = true;
            if (AssetTraffic.ObserveSend(assetVersion))
                Plugin.Writer.EmitBootstrap(
                    "asset_download_version",
                    new AssetDownloadVersion(assetVersion)
                );
            return;
        }
        Observe(
            "request",
            commits =>
            {
                var token = Objects.Token((object)__instance);
                var raw = ReadAvailable(
                    () => (Il2CppStructArray<byte>?)__instance.RawData,
                    "request_raw_body_read_failed"
                );
                var detachedRaw = CopyNullable(raw);
                var uri = ReadAvailable(
                    () => ReadUri((object)__instance),
                    "request_uri_read_failed"
                );
                var environment = RequestLedger.Classify(uri?.Parsed);
                var transportPath = uri?.Parsed?.AbsolutePath ?? "";
                if (uri is not null && uri.Parsed is null)
                    Gap(
                        null,
                        "native_original_uri_not_absolute",
                        new { original_string_preserved = true }
                    );
                var headerId = ReadAvailable(
                    () => (string?)__instance.GetFirstHeaderValue("x-request-id"),
                    "request_operation_read_failed"
                );
                RequestDescriptors.TryGetValue(token, out var descriptor);
                var route = descriptor?.Route ?? transportPath;
                var attempt = Ledger.Begin(
                    token,
                    route,
                    environment,
                    descriptor?.OperationId,
                    descriptor is not null ? "native_descriptor" : null,
                    SystemId(route)
                );
                attempt.Context = attempt.Context with
                {
                    DescriptorId = descriptor?.DescriptorId,
                    LogicalRoute = descriptor?.Route,
                    NativeOperationId = descriptor?.RawOperationId,
                    RequestHeaderId = headerId,
                    TransportPath = transportPath,
                };
                state.Attempt = attempt;
                if (!Plugin.Compatibility.ClientVerified)
                    Gap(attempt.Context, "client_fingerprint_unverified", new { route });
                BindContext((object)__instance, attempt.Context, "BestHTTP.HTTPRequest.Send()");
                using var scope = PushContext(attempt.Context);
                object body;
                if (detachedRaw is not null)
                {
                    Preserve(attempt, detachedRaw, "request", "request_entity", commits);
                    body = Decode(
                        detachedRaw,
                        "request_entity",
                        attempt,
                        false,
                        route == "/status",
                        route != "/status",
                        false,
                        commits
                    );
                }
                else
                {
                    var hasStream =
                        ReadAvailable(
                            () => (object?)__instance.UploadStream,
                            "request_stream_status_read_failed"
                        )
                        is not null;
                    var reason = hasStream
                        ? "upload_stream_not_observed"
                        : "request_entity_not_yet_materialized";
                    body = new { capture_status = "pending_materialization", reason };
                    Emit(
                        attempt.Context,
                        "request_entity_pending",
                        new
                        {
                            request_id = attempt.Context.RequestId,
                            route,
                            reason,
                        }
                    );
                }
                if (descriptor?.Bytes is { } nativeBytes)
                    Preserve(attempt, nativeBytes, "request", "native_request_protobuf", commits);
                Emit(
                    attempt.Context,
                    "request",
                    new
                    {
                        request_id = attempt.Context.RequestId,
                        attempt_id = attempt.Context.AttemptId,
                        operation_id = attempt.Context.OperationId,
                        identity_proof = attempt.Context.IdentityProof,
                        route,
                        logical_route = attempt.Context.LogicalRoute,
                        native_operation_id = attempt.Context.NativeOperationId,
                        descriptor_id = attempt.Context.DescriptorId,
                        request_header_id = headerId,
                        transport_path = transportPath,
                        uri = uri?.OriginalString,
                        uri_source = "original_native_Uri_OriginalString",
                        method = ReadAvailable(
                            () => (string?)__instance.MethodType.ToString(),
                            "request_method_read_failed"
                        ),
                        headers = Headers((object)__instance, false),
                        body,
                    }
                );
            }
        );
        state.SetContext();
    }

    private static void EntityMaterialized(
        object __instance,
        Il2CppStructArray<byte>? __result,
        IDisposable? __state
    )
    {
        if (__state is null or ExcludedRequestScope)
            return;
        Observe(
            "request_entity_materialized",
            commits =>
            {
                var attempt = Ledger.ForObservation(Objects.Token(__instance), CurrentContext);
                if (attempt is null)
                {
                    PreserveOrphan(
                        __result,
                        "request",
                        "request_entity",
                        "entity_materialized_without_send"
                    );
                    return;
                }
                if (__result is null)
                {
                    RecordNullRequestEntity(attempt, commits);
                    return;
                }
                using var scope = PushContext(attempt.Context);
                Preserve(attempt, Copy(__result), "request", "request_entity", commits);
            }
        );
    }

    private static void SendFailed(bool __nativeFault, SendState? __state)
    {
        try
        {
            if (__nativeFault && __state is { Excluded: false })
                Observe(
                    "request_exception",
                    commits =>
                    {
                        var attempt = __state.Attempt;
                        using var scope = PushContext(attempt?.Context);
                        StageNativeSendFault(Ledger, attempt, commits);
                    }
                );
        }
        finally
        {
            __state?.Dispose();
        }
    }

    internal static void StageNativeSendFault(
        RequestLedger ledger,
        RequestLedger.Attempt? attempt,
        List<Action> commits
    )
    {
        if (attempt is null)
        {
            Gap(null, "send_exception_without_attempt", new { error = "native_original_threw" });
            return;
        }
        StageTerminal(
            ledger,
            attempt,
            () =>
                Emit(
                    attempt.Context,
                    "request_exception",
                    new
                    {
                        request_id = attempt.Context.RequestId,
                        attempt_id = attempt.Context.AttemptId,
                        operation_id = attempt.Context.OperationId,
                        route = attempt.Context.Route,
                        error = "native_original_threw",
                        capture_status = "incomplete",
                    }
                ),
            commits
        );
    }

    private static void Completed(object __instance, out IDisposable? __state)
    {
        BeginRequestCallback(__instance, out __state);
        if (__state is null or ExcludedRequestScope)
            return;
        Observe(
            "request_complete",
            commits =>
            {
                var token = Objects.Token(__instance);
                var attempt = Ledger.SingleLifetime(token);
                if (attempt is null)
                {
                    var pending = Ledger.Attempts(token);
                    if (pending.Count != 0)
                    {
                        var sharedResponse = ReadAvailable(
                            () => (object?)((dynamic)__instance).Response,
                            "reused_request_response_read_failed"
                        );
                        if (sharedResponse is not null)
                            PreserveOrphan(
                                (Il2CppStructArray<byte>?)((dynamic)sharedResponse).Data,
                                "response",
                                "response_entity",
                                "reused_request_disposal_response_ambiguous"
                            );
                        foreach (var unresolved in pending)
                        {
                            Gap(
                                unresolved.Context,
                                "request_reused_before_terminal",
                                new { request_id = unresolved.Context.RequestId }
                            );
                            StageTerminal(
                                Ledger,
                                unresolved,
                                () =>
                                    Emit(
                                        unresolved.Context,
                                        "request_complete",
                                        new
                                        {
                                            request_id = unresolved.Context.RequestId,
                                            attempt_id = unresolved.Context.AttemptId,
                                            operation_id = unresolved.Context.OperationId,
                                            route = unresolved.Context.Route,
                                            capture_status = "incomplete",
                                            reason = "shared_native_request_disposed_without_distinct_attempt_terminal",
                                        }
                                    ),
                                commits
                            );
                        }
                        commits.Add(() => ReleaseRequest(__instance, token));
                    }
                    return;
                }
                using var scope = PushContext(attempt.Context);
                if (attempt.Completed || attempt.CompletionPending)
                {
                    Emit(
                        attempt.Context,
                        "request_disposed",
                        new
                        {
                            request_id = attempt.Context.RequestId,
                            attempt_id = attempt.Context.AttemptId,
                            terminal_already_staged = true,
                        }
                    );
                    commits.Add(() => ReleaseRequest(__instance, token));
                    return;
                }
                var request = (dynamic)__instance;
                var response = (object?)request.Response;
                if (response is not null)
                {
                    var bound = FindResponse(response);
                    if (!ReferenceEquals(bound, attempt))
                        Gap(
                            attempt.Context,
                            "completion_response_binding_ambiguous",
                            new { request_id = attempt.Context.RequestId }
                        );
                    else
                    {
                        var emptyProof = PreserveResponse(
                            attempt,
                            response,
                            commits,
                            "before_dispose"
                        );
                        if (!attempt.ResponseWritten && !attempt.ResponsePending)
                        {
                            var nativeResponse = (dynamic)response;
                            var bytes = CopyNullable((Il2CppStructArray<byte>?)nativeResponse.Data);
                            var encrypted =
                                (
                                    (string?)nativeResponse.GetFirstHeaderValue("content-type")
                                )?.Contains("application/octet-stream", StringComparison.Ordinal)
                                == true;
                            var compressed =
                                (string?)nativeResponse.GetFirstHeaderValue("x-content-encoding")
                                == "gzip";
                            var body = bytes is null
                                ? emptyProof is not null
                                    ? EmptyEntityBody(emptyProof)
                                    : (object)
                                        new
                                        {
                                            capture_status = "incomplete",
                                            reason = "response_entity_not_materialized",
                                        }
                                : Decode(
                                    bytes,
                                    "response_entity",
                                    attempt,
                                    true,
                                    (int)nativeResponse.StatusCode != 200
                                        || (attempt.Context.LogicalRoute ?? attempt.Context.Route)
                                            == "/status"
                                        || !encrypted,
                                    encrypted,
                                    compressed,
                                    commits
                                );
                            RecordResponse(attempt, response, body, "completion_fallback", commits);
                        }
                    }
                }
                else
                    Gap(
                        attempt.Context,
                        "request_completed_without_response",
                        new { request_id = attempt.Context.RequestId }
                    );
                if (!attempt.HasStage("request:request_entity"))
                {
                    var proof = ReadAvailable(
                        () => bindings!.ReadRequestEntityFraming((object)__instance, attempt),
                        "request_entity_framing_read_failed"
                    );
                    RecordRequestEntityCompletion(attempt, proof);
                }
                StageTerminal(
                    Ledger,
                    attempt,
                    () =>
                        Emit(
                            attempt.Context,
                            "request_complete",
                            new
                            {
                                request_id = attempt.Context.RequestId,
                                attempt_id = attempt.Context.AttemptId,
                                operation_id = attempt.Context.OperationId,
                                route = attempt.Context.Route,
                                state = (int)request.State,
                                state_name = request.State.ToString(),
                                status = response is null
                                    ? (int?)null
                                    : (int)((dynamic)response).StatusCode,
                                response_available = response is not null,
                                response_recorded = attempt.ResponseWritten
                                    || attempt.ResponsePending,
                                response_pending_commit = attempt.ResponsePending,
                                transport_error = request.Exception is null
                                    ? null
                                    : ((object)request.Exception).GetType().Name,
                                retries = (int)request.Retries,
                                max_retries = (int)request.MaxRetries,
                                elapsed_ms = (Stopwatch.GetTimestamp() - attempt.Started)
                                    * 1000.0
                                    / Stopwatch.Frequency,
                            }
                        ),
                    commits
                );
                commits.Add(() => ReleaseRequest(__instance, token));
            }
        );
    }

    private static void StateChanged(object __instance, IDisposable? __state)
    {
        if (__state is null or ExcludedRequestScope)
            return;
        Observe(
            "transport_state",
            _ =>
            {
                var attempt = Ledger.ForObservation(Objects.Token(__instance), CurrentContext);
                using var scope = PushContext(attempt?.Context);
                var request = (dynamic)__instance;
                Emit(
                    attempt?.Context,
                    "transport_state",
                    new
                    {
                        request_id = attempt?.Context.RequestId,
                        attempt_id = attempt?.Context.AttemptId,
                        state = (int)request.State,
                        state_name = request.State.ToString(),
                        retries = (int)request.Retries,
                    }
                );
            }
        );
    }
}

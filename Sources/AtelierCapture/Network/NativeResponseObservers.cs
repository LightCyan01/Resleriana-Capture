using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static partial class Network
{
    private static void Decoding(object __0, out DecoderState? __state)
    {
        var lifetime = Plugin.BeginNativeCallback();
        var state = lifetime is null ? null : new DecoderState(lifetime);
        __state = state;
        if (state is null)
            return;
        Observe(
            "native_response_decode",
            commits =>
            {
                state.Attempt = FindResponse(__0);
                using var scope = PushContext(state.Attempt?.Context);
                if (state.Attempt is { } attempt)
                    PreserveResponse(attempt, __0, commits, "decoder_entry");
                else
                    PreserveOrphan(
                        (Il2CppStructArray<byte>?)((dynamic)__0).Data,
                        "response",
                        "response_entity",
                        "decoder_response_attempt_unproven"
                    );
            }
        );
        state.EnterContext();
    }

    private static void Decoded(
        object __0,
        Il2CppStructArray<byte>? __result,
        DecoderState? __state
    )
    {
        if (__state is null)
            return;
        Observe(
            "native_response_decoded",
            commits =>
            {
                var attempt = __state.Attempt;
                var bytes = CopyNullable(__result);
                if (attempt is null)
                {
                    PreserveUnclassified(
                        bytes,
                        "response",
                        "native_decoder_result",
                        "decoder_result_attempt_unproven"
                    );
                    return;
                }
                using var scope = PushContext(attempt.Context);
                if (__result is not null)
                    BindContext(
                        __result,
                        attempt.Context,
                        "PMLKMPHGENK.AMIKMFABIFK.original_return"
                    );
                var body = RecordDecodedBody(
                    attempt,
                    bytes,
                    (int)((dynamic)__0).StatusCode,
                    commits
                );
                RecordResponse(attempt, __0, body, "original_native_decoder_return", commits);
            }
        );
    }

    internal static object RecordDecodedBody(
        RequestLedger.Attempt attempt,
        byte[]? bytes,
        int statusCode,
        List<Action> commits
    )
    {
        var body = OriginalDecodedBody(attempt, bytes, statusCode, commits);
        Emit(
            attempt.Context,
            "response_decoder_observation",
            new
            {
                request_id = attempt.Context.RequestId,
                attempt_id = attempt.Context.AttemptId,
                response_id = attempt.Context.ResponseId,
                route = attempt.Context.Route,
                stage = "native_decoder_result",
                body,
                source = "original_native_decoder_return",
                raw_bytes_preserved_separately = bytes is not null,
                api_response_already_staged = attempt.ResponseWritten || attempt.ResponsePending,
            }
        );
        return body;
    }

    internal static object OriginalDecodedBody(
        RequestLedger.Attempt attempt,
        byte[]? bytes,
        int statusCode,
        List<Action> commits
    )
    {
        if (bytes is null)
        {
            Gap(
                attempt.Context,
                "native_decoder_returned_null",
                new { request_id = attempt.Context.RequestId }
            );
            return new
            {
                stage = "native_decoder_result",
                capture_status = "incomplete",
                reason = "native_decoder_returned_null",
            };
        }
        Preserve(attempt, bytes, "response", "native_decoder_result", commits);
        return Decode(
            bytes,
            "native_decoder_result",
            attempt,
            true,
            statusCode != 200
                || (attempt.Context.LogicalRoute ?? attempt.Context.Route) == "/status",
            false,
            false,
            commits
        );
    }

    private static void EndDecoding(DecoderState? __state, bool __nativeFault)
    {
        try
        {
            if (__nativeFault && __state is not null)
                Observe(
                    "native_response_decode_fault",
                    _ =>
                        Gap(
                            __state.Attempt?.Context,
                            "native_original_threw",
                            new { owner = "PMLKMPHGENK.AMIKMFABIFK" }
                        )
                );
        }
        finally
        {
            __state?.Dispose();
        }
    }

    private static void Parsing(
        object __0,
        Il2CppStructArray<byte>? __1,
        bool __2,
        out ParseState? __state
    )
    {
        __state = null;
        if (derivedParseDepth != 0)
            return;
        var lifetime = Plugin.BeginNativeCallback();
        var state = lifetime is null ? null : new ParseState(lifetime);
        __state = state;
        if (state is null)
            return;
        Observe(
            "native_parse_input",
            commits =>
            {
                state.Context = ContextFor(__1);
                using var scope = PushContext(state.Context);
                var bytes = CopyNullable(__1);
                var nativeType = ReadAvailable(
                    () => NativeMessageType(__0),
                    "native_message_class_read_failed"
                );
                Emit(
                    state.Context,
                    "native_parse_input",
                    new
                    {
                        request_id = state.Context?.RequestId,
                        attempt_id = state.Context?.AttemptId,
                        response_id = state.Context?.ResponseId,
                        original_message_type = __0?.GetType().FullName,
                        native_message_type = nativeType,
                        message_before = OriginalMessagePayload(__0, nativeType),
                        message_snapshot_fields = MessageSnapshotFields(nativeType),
                        stage = "native_parse_input",
                        body = bytes,
                        bytes = bytes?.Length,
                        sha256 = bytes is null
                            ? null
                            : Convert.ToHexString(
                                System.Security.Cryptography.SHA256.HashData(bytes)
                            ),
                        discard_unknown_fields = __2,
                        source = "original_native_MergeFrom_argument",
                        correlation_status = state.Context is null
                            ? "original_input_unbound"
                            : "original_decoder_return_object",
                    },
                    bytes?.LongLength + 4096L ?? 0
                );
            }
        );
        state.EnterContext();
    }

    private static void Parsed(object __0, ParseState? __state)
    {
        if (__state is null)
            return;
        Observe(
            "native_parse_applied",
            _ =>
            {
                string[] lineageFailures = [];
                if (__state.Context is { } context)
                {
                    BindContext(
                        __0,
                        context,
                        "Google.Protobuf.MessageExtensions.MergeFrom.original_success"
                    );
                    lineageFailures = Snapshot.BindProtobufChildren(__0);
                }
                var rootGap = NativeLineage.ParsedRootGap(__state.Context, BindingStateFor(__0));
                if (rootGap is not null)
                    lineageFailures = lineageFailures.Append("$lineage_root:" + rootGap).ToArray();
                var nativeType = ReadAvailable(
                    () => NativeMessageType(__0),
                    "native_message_class_read_failed"
                );
                Emit(
                    __state.Context,
                    "native_parse_applied",
                    new
                    {
                        request_id = __state.Context?.RequestId,
                        attempt_id = __state.Context?.AttemptId,
                        response_id = __state.Context?.ResponseId,
                        original_message_type = __0?.GetType().FullName,
                        native_message_type = nativeType,
                        payload = OriginalMessagePayload(__0, nativeType),
                        payload_fields = MessageSnapshotFields(nativeType),
                        source = "original_native_MergeFrom_success",
                        original_returned = true,
                        status = lineageFailures.Length == 0 ? "observed" : "incomplete",
                        lineage_complete = lineageFailures.Length == 0,
                        lineage_failures = lineageFailures,
                        correlation_status = __state.Context is null
                            ? "original_input_unbound"
                            : "original_decoder_return_object",
                    }
                );
            }
        );
    }

    private static object? OriginalMessagePayload(object? message, string? nativeType) =>
        message is null || nativeType is null
            ? null
            : ReadAvailable(
                () =>
                    UsesBinaryMessageSnapshot(nativeType)
                        ? BinaryMessageSnapshot(nativeType!, bindings!.ReadMessageBinary(message))
                        : (object?)ParsePayload(NativeMessageText(message)),
                "native_message_snapshot_read_failed"
            );

    internal static bool UsesBinaryMessageSnapshot(string? type) =>
        type == "Google.Protobuf.Reflection.FileDescriptorProto";

    internal static string MessageSnapshotFields(string? type) =>
        type is null ? "unavailable_native_message_class"
        : UsesBinaryMessageSnapshot(type)
            ? "complete_derived_binary_including_unknown_fields_original_input_retained_separately"
        : "known_fields_only_unknown_wire_fields_retained_in_original_input";

    internal static object BinaryMessageSnapshot(string type, byte[] bytes)
    {
        if (!UsesBinaryMessageSnapshot(type))
            throw new InvalidDataException("Native binary snapshot type is unqualified.");
        return new
        {
            native_message_type = type,
            representation = "derived_native_message_binary",
            body = bytes,
            bytes = bytes.LongLength,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
            source = "native_MessageExtensions_ToByteArray_CalculateSize_WriteTo",
            json_status = "inapplicable_for_self_descriptor_snapshot",
            original_input_preserved_separately = true,
            unknown_fields_preserved = true,
        };
    }

    private static string NativeMessageType(object message)
    {
        var native = (Il2CppObjectBase)message;
        try
        {
            return NativeTypeName(IL2CPP.il2cpp_object_get_class(native.Pointer));
        }
        finally
        {
            GC.KeepAlive(native);
        }
    }

    private static string NativeTypeName(IntPtr nativeClass)
    {
        if (nativeClass == IntPtr.Zero)
            throw new InvalidDataException("Native message class is unavailable.");
        var name =
            IL2CPP.il2cpp_class_get_name_(nativeClass)
            ?? throw new InvalidDataException("Native message class name is unavailable.");
        var owner = IL2CPP.il2cpp_class_get_declaring_type(nativeClass);
        if (owner != IntPtr.Zero)
            return NativeTypeName(owner) + "+" + name;
        var space = IL2CPP.il2cpp_class_get_namespace_(nativeClass);
        return string.IsNullOrEmpty(space) ? name : space + "." + name;
    }

    internal static string NativeMessageText(object message)
    {
        var source = (Il2CppObjectBase)message;
        var view = source.Cast<Il2CppSystem.Object>();
        try
        {
            return view.ToString();
        }
        finally
        {
            GC.KeepAlive(view);
            GC.KeepAlive(source);
        }
    }

    private static void EndParsing(ParseState? __state, bool __nativeFault)
    {
        try
        {
            if (__nativeFault && __state is not null)
                Observe(
                    "native_parse_fault",
                    _ =>
                        Gap(
                            __state.Context,
                            "native_original_threw",
                            new { owner = "Google.Protobuf.MessageExtensions.MergeFrom" }
                        )
                );
        }
        finally
        {
            __state?.Dispose();
        }
    }

    private static void BeginCallback(object __0, object? __1, out IDisposable? __state)
    {
        var lifetime = Plugin.BeginNativeCallback();
        __state = lifetime;
        if (lifetime is null)
            return;
        NetworkContext? context = null;
        Observe(
            "api_callback",
            commits =>
            {
                if (__1 is not null)
                {
                    var attempt = Ledger.BindResponse(Objects.Token(__1), Objects.Token(__0));
                    if (attempt is not null)
                    {
                        BindContext(
                            __1,
                            attempt.Context,
                            "PMLKMPHGENK.INPLGDJFDNN.LCPDHIHNGEP(BestHTTP.HTTPRequest,BestHTTP.HTTPResponse)"
                        );
                        context = attempt.Context;
                        using var scope = PushContext(context);
                        PreserveResponse(attempt, __1, commits, "response_callback");
                    }
                    else
                        Gap(
                            null,
                            "response_attempt_binding_ambiguous",
                            new { owner = "PMLKMPHGENK.INPLGDJFDNN.LCPDHIHNGEP" }
                        );
                }
            }
        );
        __state = new CallbackScope(PushContext(context), lifetime);
    }

    private static void EndCallback(IDisposable? __state, bool __nativeFault) =>
        EndNativeCallback(__state, __nativeFault, bindings!.Callback);

    private sealed class DecoderState(IDisposable lifetime) : IDisposable
    {
        private IDisposable? scope;
        internal RequestLedger.Attempt? Attempt;

        internal void EnterContext() => scope = PushContext(Attempt?.Context);

        public void Dispose()
        {
            scope?.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class ParseState(IDisposable lifetime) : IDisposable
    {
        private IDisposable? scope;
        internal NetworkContext? Context;

        internal void EnterContext() => scope = PushContext(Context);

        public void Dispose()
        {
            scope?.Dispose();
            lifetime.Dispose();
        }
    }
}

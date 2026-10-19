using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace AtelierCapture;

internal static partial class Network
{
    private static readonly object Sync = new();
    private static readonly RequestLedger Ledger = new();
    private static readonly NativeObjectContexts Objects = new();
    private static readonly Dictionary<long, Descriptor> Descriptors = new();
    private static readonly Dictionary<long, Descriptor> RequestDescriptors = new();
    private static readonly Dictionary<string, (object Parser, MethodInfo Parse)> Parsers = new(
        StringComparer.Ordinal
    );
    private static Dictionary<string, string[]> routes = new(StringComparer.Ordinal);
    private static NativeApiBindings? bindings;
    private static bool installed;
    private static NetworkStatus status = new(0, false);
    private static int sweepCountdown = 64;

    [ThreadStatic]
    private static NetworkContext? current;

    internal static NetworkContext? CurrentContext => current;

    internal static NetworkContext? ContextFor(object? value)
    {
        lock (Sync)
            return Objects.Context(value);
    }

    internal static long ObjectIdentity(object value)
    {
        lock (Sync)
            return Objects.Token(value);
    }

    internal static NativeEnvironmentProof? EnvironmentProofFor(object? value)
    {
        lock (Sync)
            return Objects.Environment(value);
    }

    internal static (NetworkContext? Context, NativeEnvironmentProof? Environment) BindingFor(
        object? value
    )
    {
        lock (Sync)
            return Objects.Lookup(value);
    }

    internal static NativeBindingState BindingStateFor(object value)
    {
        lock (Sync)
            return Objects.State(value);
    }

    internal static void InvalidateOperation(object value)
    {
        lock (Sync)
            Objects.InvalidateOperation(value);
    }

    // Gaps write records, which can take the writer's copier lock; never hold Sync across them.
    internal static void BindContext(
        object value,
        NetworkContext context,
        string qualifiedEdge = "native_object_context"
    )
    {
        bool ambiguous;
        lock (Sync)
            ambiguous = Objects.Bind(value, context, qualifiedEdge);
        if (ambiguous)
            ContextAmbiguous(value, context);
    }

    internal static void BindEnvironment(object value, bool? environment, string qualifiedEdge)
    {
        if (string.IsNullOrWhiteSpace(qualifiedEdge))
            throw new InvalidDataException(
                "Native environment proof requires its qualified application edge."
            );
        bool ambiguous;
        lock (Sync)
            ambiguous = Objects.BindEnvironment(value, environment, qualifiedEdge);
        if (ambiguous)
            EnvironmentAmbiguous(value, qualifiedEdge);
    }

    // One lock for the read and the bind, so the parent's binding cannot change in between.
    internal static void InheritEnvironment(object parent, object child, string qualifiedEdge)
    {
        bool ambiguous;
        lock (Sync)
            ambiguous = Objects.InheritEnvironment(parent, child, qualifiedEdge);
        if (ambiguous)
            EnvironmentAmbiguous(child, qualifiedEdge);
    }

    private static void ContextAmbiguous(object value, NetworkContext context) =>
        Gap(
            context,
            "native_object_context_ambiguous",
            new { object_type = value.GetType().FullName }
        );

    private static void EnvironmentAmbiguous(object value, string qualifiedEdge) =>
        Gap(
            null,
            "native_object_environment_ambiguous",
            new { object_type = value.GetType().FullName, qualified_edge = qualifiedEdge }
        );

    internal static IDisposable PushContext(NetworkContext? context) => new ContextScope(context);

    internal static object CaptureStatus()
    {
        var snapshot = Volatile.Read(ref status);
        var closed = PassiveNativeHook.IsClosed;
        return snapshot with
        {
            binding_complete = snapshot.binding_complete && !closed,
            native_observers_closed = closed,
            asset_downloads = AssetTraffic.Snapshot(),
        };
    }

    private static void PublishStatus() =>
        Volatile.Write(ref status, new NetworkStatus(Ledger.ActiveCount, installed));

    internal static void ReleaseObjects()
    {
        lock (Sync)
        {
            Objects.Clear();
            Descriptors.Clear();
            RequestDescriptors.Clear();
            Parsers.Clear();
        }
    }

    internal static void Install()
    {
        routes = Plugin.ReadResource<Dictionary<string, string[]>>("Routes.json");
        bindings = new NativeApiBindings();
        bindings.Install();
        Plugin.Writer.EmitBootstrap("api_capture_incomplete", MaterializationBoundary());
        installed = true;
        PublishStatus();
    }

    internal static object MaterializationBoundary() =>
        new
        {
            complete = false,
            capture_status = "incomplete",
            reason = "shared_native_body_unqualified",
            scope = "assignment_stage_only",
            unsupported = new[]
            {
                new
                {
                    owner = "BestHTTP.HTTPRequest.set_RawData",
                    rva = "0x8C8F40",
                    native_aliases = 659,
                    method_definition_aliases = 617,
                    constructed_generic_aliases = 42,
                    method_definition_assemblies = 43,
                    stage = "native_raw_data_assignment",
                },
                new
                {
                    owner = "BestHTTP.HTTPResponse.set_Data",
                    rva = "0x8C9060",
                    native_aliases = 135,
                    method_definition_aliases = 118,
                    constructed_generic_aliases = 17,
                    method_definition_assemblies = 21,
                    stage = "native_data_assignment",
                },
            },
            preserved = new[]
            {
                "original_immutable_native_descriptor",
                "request_entity_at_Send",
                "original_GetEntityBody_result",
                "original_response_at_callback",
                "original_response_before_Dispose",
                "native_decoder_result_at_original_return",
                "native_parse_input_at_original_MergeFrom",
                "native_parse_applied_at_original_success_known_fields",
            },
            qualification = "unique_consumer_entity_bytes_are_observed_separately_from_unsupported_assignment_history",
        };

    private static void Observe(string kind, Action<List<Action>> copy)
    {
        var committed = new List<Action>();
        Plugin.Observe(
            kind,
            () =>
            {
                lock (Sync)
                {
                    if (--sweepCountdown == 0)
                    {
                        foreach (var token in Objects.Sweep())
                        {
                            Ledger.Release(token);
                            Descriptors.Remove(token);
                            RequestDescriptors.Remove(token);
                        }
                        sweepCountdown = 64;
                    }
                    try
                    {
                        copy(committed);
                    }
                    finally
                    {
                        PublishStatus();
                    }
                }
            },
            Commit,
            Commit
        );
        void Commit()
        {
            lock (Sync)
            {
                try
                {
                    CommitStagedActions(committed);
                }
                finally
                {
                    PublishStatus();
                }
            }
        }
    }

    internal static void CommitStagedActions(IEnumerable<Action> actions)
    {
        List<Exception>? failures = null;
        foreach (var action in actions)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                (failures ??= []).Add(error);
            }
        }
        if (failures is not null)
            throw new AggregateException("Network staged-record state commit failed.", failures);
    }

    private static void BeginNativeCallback(out IDisposable? __state) =>
        __state = Plugin.BeginNativeCallback();

    private static void EndNativeCallback(
        IDisposable? __state,
        bool __nativeFault,
        MethodBase __originalMethod
    )
    {
        try
        {
            if (__nativeFault && __state is not null and not ExcludedRequestScope)
                Observe(
                    "native_api_fault",
                    _ =>
                        Gap(
                            CurrentContext,
                            "native_original_threw",
                            new
                            {
                                owner = __originalMethod.DeclaringType?.FullName,
                                method = __originalMethod.Name,
                                signature = __originalMethod.ToString(),
                            }
                        )
                );
        }
        finally
        {
            __state?.Dispose();
        }
    }

    private static RequestLedger.Attempt? FindResponse(object response)
    {
        var responseToken = Objects.Token(response);
        var existing = Ledger.Response(responseToken);
        if (existing is not null)
            return existing;
        var owner = (object?)((dynamic)response).baseRequest;
        if (owner is null)
            return null;
        var attempt = Ledger.BindResponse(responseToken, Objects.Token(owner));
        if (attempt is not null)
            BindContext(response, attempt.Context, "BestHTTP.HTTPResponse.baseRequest");
        return attempt;
    }

    private static ResponseEntityFraming? PreserveResponse(
        RequestLedger.Attempt attempt,
        object response,
        List<Action> commits,
        string phase
    )
    {
        var raw = (Il2CppStructArray<byte>?)((dynamic)response).Data;
        if (raw is null)
        {
            var framing = ReadAvailable(
                () => bindings!.ReadResponseFraming(response, Headers(response, true)),
                "response_entity_framing_read_failed"
            );
            var verifiedEmpty = framing is not null && IsVerifiedEmptyEntity(framing);
            Emit(
                attempt.Context,
                "response_entity_observation",
                new
                {
                    request_id = attempt.Context.RequestId,
                    attempt_id = attempt.Context.AttemptId,
                    response_id = attempt.Context.ResponseId,
                    stage = "response_entity",
                    phase,
                    original_native_value = (byte[]?)null,
                    entity_status = verifiedEmpty
                        ? "verified_empty_by_native_framing"
                        : "unresolved_null",
                    framing,
                    native_contract = "PMLKMPHGENK.AMIKMFABIFK@0x4BBB3D0:null_Data_to_empty_result",
                }
            );
            if (!verifiedEmpty)
                Gap(
                    attempt.Context,
                    "response_entity_not_materialized",
                    new { request_id = attempt.Context.RequestId, phase }
                );
            return verifiedEmpty ? framing : null;
        }
        Preserve(attempt, Copy(raw), "response", "response_entity", commits);
        return null;
    }

    private static void Preserve(
        RequestLedger.Attempt attempt,
        byte[] bytes,
        string direction,
        string stage,
        List<Action> commits
    )
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var key = direction + ":" + stage + ":" + hash;
        if (!attempt.ReserveStage(key))
            return;
        try
        {
            var detached = ApiBodies.Own(attempt.Context, direction, stage, bytes, hash);
            Emit(attempt.Context, "raw_body", detached, detached.body.LongLength + 4096L);
            commits.Add(() => attempt.CommitStage(key));
        }
        catch
        {
            attempt.CancelStage(key);
            throw;
        }
    }

    private static void PreserveOrphan(
        Il2CppStructArray<byte>? raw,
        string direction,
        string stage,
        string reason
    )
    {
        PreserveUnclassified(CopyNullable(raw), direction, stage, reason);
        Gap(null, reason, new { direction, stage });
    }

    private static void PreserveUnclassified(
        byte[]? bytes,
        string direction,
        string stage,
        string reason
    )
    {
        Plugin.Writer.EmitBootstrap(
            "unclassified_raw_body",
            new
            {
                direction,
                stage,
                body = bytes,
                bytes = bytes?.Length,
                sha256 = bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)),
                capture_status = "incomplete",
                reason,
            }
        );
    }

    private static void RecordResponse(
        RequestLedger.Attempt attempt,
        object response,
        object body,
        string source,
        List<Action> commits
    )
    {
        var native = (dynamic)response;
        StageResponse(
            attempt,
            () =>
                Emit(
                    attempt.Context,
                    "response",
                    new
                    {
                        request_id = attempt.Context.RequestId,
                        attempt_id = attempt.Context.AttemptId,
                        operation_id = attempt.Context.OperationId,
                        response_id = attempt.Context.ResponseId,
                        route = attempt.Context.Route,
                        status = (int)native.StatusCode,
                        headers = Headers(response, true),
                        body,
                        source,
                    }
                ),
            commits
        );
    }

    internal static bool StageResponse(
        RequestLedger.Attempt attempt,
        Action record,
        List<Action> commits
    )
    {
        if (attempt.ResponseWritten || attempt.ResponsePending)
            return false;
        record();
        attempt.ResponsePending = true;
        commits.Add(() =>
        {
            attempt.ResponseWritten = true;
            attempt.ResponsePending = false;
        });
        return true;
    }

    internal static bool StageTerminal(
        RequestLedger ledger,
        RequestLedger.Attempt attempt,
        Action record,
        List<Action> commits
    )
    {
        if (attempt.Completed || attempt.CompletionPending)
            return false;
        record();
        attempt.CompletionPending = true;
        commits.Add(() => ledger.Complete(attempt));
        return true;
    }

    private static void ReleaseRequest(object request, long token)
    {
        Ledger.Release(token);
        RequestDescriptors.Remove(token);
        Objects.Remove(request);
    }

    internal static void Emit(
        NetworkContext? context,
        string kind,
        object data,
        long retainedBytes = 0
    )
    {
        if (context?.IsPrivate is { } environment)
            Plugin.Writer.Emit(environment, false, kind, data, retainedBytes, context.SystemId);
        else
            Plugin.Writer.EmitBootstrap(
                "unclassified_" + kind,
                new
                {
                    capture_status = "incomplete",
                    context,
                    data,
                }
            );
    }

    private static void Gap(NetworkContext? context, string reason, object details)
    {
        Emit(
            context,
            "api_capture_incomplete",
            new
            {
                capture_status = "incomplete",
                reason,
                details,
            }
        );
        Plugin.ReportFailure("api_" + reason, new InvalidDataException(reason));
    }

    private static OriginalUri ReadUri(object request) =>
        ParseOriginalUri(bindings!.ReadOriginalUri(request));

    internal static OriginalUri ParseOriginalUri(string original) =>
        new(original, Uri.TryCreate(original, UriKind.Absolute, out var uri) ? uri : null);

    internal sealed record OriginalUri(string OriginalString, Uri? Parsed);

    private static T? ReadAvailable<T>(Func<T?> read, string reason)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception error)
        {
            Gap(
                current,
                reason,
                new
                {
                    error = (error.InnerException ?? error).GetType().Name,
                    exception = error.ToString(),
                }
            );
            return null;
        }
    }

    private static byte[] Copy(Il2CppStructArray<byte> source)
    {
        try
        {
            return source.AsSpan().ToArray();
        }
        finally
        {
            GC.KeepAlive(source);
        }
    }

    private static byte[]? CopyNullable(Il2CppStructArray<byte>? source) =>
        source is null ? null : Copy(source);

    private static string? SystemId(string route) => Archive.RouteCountCatalog.SystemId(route);

    private sealed record NetworkStatus(
        int active_attempts,
        bool binding_complete,
        bool native_observers_closed = false,
        string origin_policy = "publisher_domain_or_loopback_else_unclassified",
        string correlation = "native_object_and_operation_identity",
        string assignment_stage_status = "unsupported_shared_native_body",
        string entity_stage_status = "observed_at_unique_consumers",
        string decoder_result_stage_status = "observed_at_original_native_return",
        bool complete = false,
        AssetDownloadSummary? asset_downloads = null
    );

    private sealed record Descriptor(
        string? OperationId,
        string Route,
        byte[]? Bytes,
        string? RawOperationId,
        string DescriptorId
    );

    private sealed class SendState(IDisposable lifetime) : IDisposable
    {
        private IDisposable scope = PushContext(null);
        internal RequestLedger.Attempt? Attempt;
        internal bool Excluded;

        internal void SetContext()
        {
            scope.Dispose();
            scope = PushContext(Attempt?.Context);
        }

        public void Dispose()
        {
            scope.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class CallbackScope(IDisposable context, IDisposable lifetime) : IDisposable
    {
        public void Dispose()
        {
            context.Dispose();
            lifetime.Dispose();
        }
    }

    private sealed class ContextScope : IDisposable
    {
        private readonly NetworkContext? previous = current;
        private bool disposed;

        internal ContextScope(NetworkContext? context) => current = context;

        public void Dispose()
        {
            if (!disposed)
            {
                current = previous;
                disposed = true;
            }
        }
    }
}

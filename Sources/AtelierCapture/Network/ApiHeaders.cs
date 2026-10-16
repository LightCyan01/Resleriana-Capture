using NativeValues = Il2CppSystem.Collections.Generic.List<string>;

namespace AtelierCapture;

internal sealed record HeaderGroup(
    string? name,
    string?[]? values,
    bool complete = true,
    string? error = null,
    string? exception = null
);

internal sealed record HeaderSnapshot(
    string source,
    string order,
    IReadOnlyList<HeaderGroup> groups,
    bool complete,
    string? error = null,
    string? exception = null
);

internal static partial class Network
{
    [ThreadStatic]
    private static HeaderSendState? headerSend;

    private static void BeginHeaderSend(object __instance, out HeaderSendState? __state)
    {
        var lifetime = Plugin.BeginNativeCallback();
        var state = lifetime is null ? null : new HeaderSendState(lifetime);
        __state = state;
        if (state is null)
            return;
        Observe(
            "request_headers_begin",
            commits =>
            {
                state.Attempt = Ledger.ForObservation(Objects.Token(__instance), CurrentContext);
                state.Context = state.Attempt?.Context;
                Emit(
                    state.Context,
                    "request_headers_begin",
                    new
                    {
                        request_id = state.Context?.RequestId,
                        attempt_id = state.Context?.AttemptId,
                        source = "original_native_SendHeaders",
                        order = "original_callback_group_and_value_order",
                    }
                );
            }
        );
        state.EnterContext();
    }

    private static void HeaderGroupWriting(string? __0, NativeValues? __1, out IDisposable? __state)
    {
        __state = Plugin.BeginNativeCallback();
        if (__state is null)
            return;
        Observe(
            "request_header_group",
            commits =>
            {
                var sender = headerSend;
                var group = CopyHeaderGroup(__0, __1);
                sender?.Groups.Add(group);
                Emit(
                    sender?.Context,
                    "request_header_group",
                    new
                    {
                        request_id = sender?.Context?.RequestId,
                        attempt_id = sender?.Context?.AttemptId,
                        group_ordinal = sender is null
                            ? null
                            : (++sender.Ordinal).ToString(
                                System.Globalization.CultureInfo.InvariantCulture
                            ),
                        group,
                        source = "original_native_SendHeaders_callback_before_write",
                        order = "original_callback_group_and_value_order",
                    }
                );
                if (!group.complete)
                    Gap(
                        sender?.Context,
                        "serialized_header_values_read_failed",
                        new { error = group.error, exception = group.exception }
                    );
            }
        );
    }

    private static void EndHeaderSend(HeaderSendState? __state, bool __nativeFault)
    {
        try
        {
            if (__state is not null)
                Observe(
                    "request_headers_end",
                    commits =>
                    {
                        Emit(
                            __state.Context,
                            "request_headers_end",
                            new
                            {
                                request_id = __state.Context?.RequestId,
                                attempt_id = __state.Context?.AttemptId,
                                groups_observed = __state.Ordinal.ToString(
                                    System.Globalization.CultureInfo.InvariantCulture
                                ),
                                original_returned = !__nativeFault,
                                error = __nativeFault ? "native_original_threw" : null,
                                source = "original_native_SendHeaders",
                            }
                        );
                        if (__state.Attempt is { } attempt)
                        {
                            var snapshot = new HeaderSnapshot(
                                "original_native_SendHeaders_callbacks",
                                "original_callback_group_and_value_order",
                                __state.Groups.ToArray(),
                                !__nativeFault && __state.Groups.All(group => group.complete),
                                __nativeFault ? "native_original_threw" : null
                            );
                            commits.Add(() => attempt.SerializedRequestHeaders = snapshot);
                        }
                    }
                );
        }
        finally
        {
            __state?.Dispose();
        }
    }

    private sealed class HeaderSendState(IDisposable lifetime) : IDisposable
    {
        private readonly HeaderSendState? previous = headerSend;
        private IDisposable? scope;
        internal NetworkContext? Context;
        internal RequestLedger.Attempt? Attempt;
        internal readonly List<HeaderGroup> Groups = new();
        internal long Ordinal;

        internal void EnterContext()
        {
            headerSend = this;
            scope = PushContext(Context);
        }

        public void Dispose()
        {
            scope?.Dispose();
            headerSend = previous;
            lifetime.Dispose();
        }
    }

    private static HeaderSnapshot Headers(object owner, bool response)
    {
        var groups = new List<HeaderGroup>();
        var source = response
            ? "original_native_response_header_dictionary"
            : "original_native_request_header_dictionary";
        try
        {
            var dictionary = bindings!.ReadHeaders(owner, response);
            if (dictionary is null)
                return new HeaderSnapshot(
                    source,
                    "native_dictionary_iteration_not_wire_order",
                    groups,
                    true
                );
            try
            {
                var enumerator = dictionary.GetEnumerator();
                while (enumerator.MoveNext())
                {
                    var pair = enumerator.Current;
                    var group = CopyHeaderGroup(pair.Key, pair.Value);
                    groups.Add(group);
                    if (!group.complete)
                        Gap(
                            current,
                            "header_values_read_failed",
                            new
                            {
                                source,
                                error = group.error,
                                exception = group.exception,
                            }
                        );
                }
            }
            finally
            {
                GC.KeepAlive(dictionary);
            }
            return new HeaderSnapshot(
                source,
                "native_dictionary_iteration_not_wire_order",
                groups,
                groups.All(group => group.complete)
            );
        }
        catch (Exception error)
        {
            Gap(
                current,
                "header_dictionary_read_failed",
                new
                {
                    source,
                    error = (error.InnerException ?? error).GetType().Name,
                    exception = error.ToString(),
                }
            );
            return new HeaderSnapshot(
                source,
                "native_dictionary_iteration_not_wire_order",
                groups,
                false,
                (error.InnerException ?? error).GetType().Name,
                error.ToString()
            );
        }
    }

    private static HeaderGroup CopyHeaderGroup(string? name, NativeValues? values)
    {
        if (values is null)
            return new HeaderGroup(name, null);
        try
        {
            return CopyHeaderGroup(name, () => values.Count, index => values[index]);
        }
        finally
        {
            GC.KeepAlive(values);
        }
    }

    internal static HeaderGroup CopyHeaderGroup(
        string? name,
        Func<int> count,
        Func<int, string?> read
    )
    {
        var values = new List<string?>();
        try
        {
            var length = count();
            if (length < 0)
                throw new InvalidDataException("Native header count is invalid.");
            for (var index = 0; index < length; index++)
                values.Add(read(index));
            return new HeaderGroup(name, values.ToArray());
        }
        catch (Exception error)
        {
            return new HeaderGroup(
                name,
                values.ToArray(),
                false,
                (error.InnerException ?? error).GetType().Name,
                error.ToString()
            );
        }
    }
}

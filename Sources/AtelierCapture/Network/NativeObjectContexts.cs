using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace AtelierCapture;

internal sealed class NativeObjectContexts
{
    private readonly Dictionary<IntPtr, Binding> bindings = new();
    private readonly List<long> replacedTokens = new();
    private long nextToken;

    internal long Token(object value)
    {
        var pointer = Pointer(value);
        if (bindings.TryGetValue(pointer, out var existing))
        {
            if (existing.Alive)
                return existing.Token;
            replacedTokens.Add(existing.Token);
            existing.Dispose();
            bindings.Remove(pointer);
        }
        var binding = new Binding(++nextToken, pointer);
        bindings.Add(pointer, binding);
        return binding.Token;
    }

    internal NetworkContext? Context(object? value)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            return null;
        return bindings.TryGetValue(native.Pointer, out var binding) && binding.Alive
            ? binding.Context.Current
            : null;
    }

    internal NativeEnvironmentProof? Environment(object? value)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            return null;
        return
            bindings.TryGetValue(native.Pointer, out var binding)
            && binding.Alive
            && binding.Context.Origin.HasEvidence
            ? binding.Context.Origin.Proof
            : null;
    }

    internal NativeBindingState State(object? value)
    {
        if (value is not Il2CppObjectBase native || native.Pointer == IntPtr.Zero)
            return default;
        return bindings.TryGetValue(native.Pointer, out var binding) && binding.Alive
            ? binding.Context.State
            : default;
    }

    internal bool Bind(object value, NetworkContext context, string qualifiedEdge)
    {
        _ = Token(value);
        var binding = bindings[Pointer(value)].Context;
        var wasAmbiguous = binding.Ambiguous;
        binding.Bind(context, qualifiedEdge);
        return !wasAmbiguous && binding.Ambiguous;
    }

    internal bool BindEnvironment(object value, bool? environment, string qualifiedEdge)
    {
        _ = Token(value);
        var origin = bindings[Pointer(value)].Context.Origin;
        var wasAmbiguous = origin.Proof.Ambiguous;
        origin.Bind(environment, qualifiedEdge);
        return !wasAmbiguous && origin.Proof.Ambiguous;
    }

    internal void InvalidateOperation(object value)
    {
        _ = Token(value);
        bindings[Pointer(value)].Context.InvalidateOperation();
    }

    internal void Remove(object value)
    {
        if (bindings.Remove(Pointer(value), out var binding))
            binding.Dispose();
    }

    internal long[] Sweep()
    {
        var retired = new List<long>(replacedTokens);
        replacedTokens.Clear();
        foreach (var pair in bindings.ToArray())
        {
            if (pair.Value.Alive)
                continue;
            retired.Add(pair.Value.Token);
            pair.Value.Dispose();
            bindings.Remove(pair.Key);
        }
        return retired.ToArray();
    }

    internal void Clear()
    {
        foreach (var binding in bindings.Values)
            binding.Dispose();
        bindings.Clear();
        replacedTokens.Clear();
    }

    internal static IntPtr Pointer(object value) =>
        value is Il2CppObjectBase native && native.Pointer != IntPtr.Zero
            ? native.Pointer
            : throw new InvalidDataException("Native API object identity is unavailable.");

    private sealed class Binding(long token, IntPtr pointer) : IDisposable
    {
        private readonly uint handle = checked(
            (uint)IL2CPP.il2cpp_gchandle_new_weakref(pointer, false)
        );
        internal long Token { get; } = token;
        internal ContextBinding Context { get; } = new();
        internal bool Alive => IL2CPP.il2cpp_gchandle_get_target((nint)handle) == pointer;

        public void Dispose() => IL2CPP.il2cpp_gchandle_free((nint)handle);
    }
}

internal sealed class ContextBinding
{
    private NetworkContext? current;
    internal NetworkContext? Current => Origin.Proof.Ambiguous ? null : current;
    internal bool Ambiguous { get; private set; }
    internal EnvironmentBinding Origin { get; } = new();

    internal NativeBindingState State =>
        new(Current, Ambiguous, Origin.Proof.IsPrivate, Origin.Proof.Ambiguous, Origin.HasEvidence);

    internal void InvalidateOperation()
    {
        current = null;
        Ambiguous = true;
    }

    internal void Bind(NetworkContext context, string qualifiedEdge = "native_object_context")
    {
        Origin.Bind(context.IsPrivate, qualifiedEdge);
        if (Ambiguous)
            return;
        if (current is not { } known)
        {
            current = context;
            return;
        }
        if (Conflicts(known, context))
        {
            current = null;
            Ambiguous = true;
        }
        else
            current = known with
            {
                ResponseId = known.ResponseId ?? context.ResponseId,
                DescriptorId = known.DescriptorId ?? context.DescriptorId,
                LogicalRoute = known.LogicalRoute ?? context.LogicalRoute,
                NativeOperationId = known.NativeOperationId ?? context.NativeOperationId,
                RequestHeaderId = known.RequestHeaderId ?? context.RequestHeaderId,
                TransportPath = known.TransportPath ?? context.TransportPath,
            };
    }

    internal static bool Conflicts(NetworkContext known, NetworkContext context) =>
        known.RequestId != context.RequestId
        || known.AttemptId != context.AttemptId
        || known.OperationId != context.OperationId
        || known.Route != context.Route
        || known.IsPrivate != context.IsPrivate
        || known.IdentityProof != context.IdentityProof
        || known.DescriptorId is { } descriptor
            && context.DescriptorId is { } nextDescriptor
            && descriptor != nextDescriptor
        || known.LogicalRoute is { } route
            && context.LogicalRoute is { } nextRoute
            && route != nextRoute
        || known.NativeOperationId is { } operation
            && context.NativeOperationId is { } nextOperation
            && operation != nextOperation
        || known.ResponseId is { } response && context.ResponseId is { } next && response != next;
}

internal readonly record struct NativeBindingState(
    NetworkContext? Context,
    bool OperationAmbiguous,
    bool? Environment,
    bool EnvironmentAmbiguous,
    bool EnvironmentHasEvidence
);

internal sealed record NativeEnvironmentProof(
    bool? IsPrivate,
    bool Ambiguous,
    IReadOnlyList<string> QualifiedEdges
);

internal sealed class EnvironmentBinding
{
    private readonly HashSet<string> edges = new(StringComparer.Ordinal);
    private bool observed;
    internal bool HasEvidence => observed;
    internal NativeEnvironmentProof Proof { get; private set; } =
        new(null, false, Array.Empty<string>());

    internal void Bind(bool? environment, string qualifiedEdge)
    {
        var changed = edges.Add(qualifiedEdge);
        var ambiguous = Proof.Ambiguous || observed && Proof.IsPrivate != environment;
        var value = ambiguous ? null : environment;
        if (changed || !observed || ambiguous != Proof.Ambiguous)
            Proof = new NativeEnvironmentProof(
                value,
                ambiguous,
                edges.Order(StringComparer.Ordinal).ToArray()
            );
        observed = true;
    }
}

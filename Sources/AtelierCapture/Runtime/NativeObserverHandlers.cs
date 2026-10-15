using System.Linq.Expressions;
using System.Reflection;

namespace AtelierCapture;

internal sealed class NativeObserverHandlers
{
    private readonly Action<NativeObservationSnapshot, Invocation>? enter;
    private readonly Action<NativeObservationSnapshot, Invocation>? returned;
    private readonly Action<NativeObservationSnapshot, Invocation>? leave;
    private readonly int argumentCount;

    internal NativeObserverHandlers(
        MethodBase target,
        Type owner,
        string? prefix,
        string? postfix,
        string? finalizer
    )
    {
        argumentCount = target.GetParameters().Length;
        enter = Bind(target, owner, prefix, true);
        returned = Bind(target, owner, postfix, false);
        leave = Bind(target, owner, finalizer, false);
    }

    internal object? Enter(NativeObservation frame)
    {
        var lease = Plugin.BeginNativeCallback();
        if (lease is null)
            return null;
        Invocation invocation;
        try
        {
            invocation = new Invocation(lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        try
        {
            Plugin.Observe(
                "native_entry_observer",
                () =>
                {
                    invocation.Snapshot = frame.Materialize();
                    enter?.Invoke(invocation.Snapshot, invocation);
                }
            );
        }
        catch (Exception error)
        {
            Plugin.ReportNativeObserverFailure(error);
        }
        return invocation;
    }

    internal void Returned(NativeObservation frame, object? state)
    {
        if (state is not Invocation invocation)
            return;
        invocation.Snapshot = null;
        Plugin.Observe(
            "native_return_observer",
            () =>
            {
                invocation.Snapshot = frame.Materialize();
                returned?.Invoke(invocation.Snapshot, invocation);
            }
        );
    }

    internal void Faulted(NativeObservation frame, object? state)
    {
        if (state is not Invocation invocation)
            return;
        invocation.NativeFault = true;
        invocation.Snapshot = null;
        Plugin.Observe("native_fault_observer", () => invocation.Snapshot = frame.Materialize());
    }

    internal void Leave(object? state)
    {
        if (
            state is not Invocation invocation
            || Interlocked.Exchange(ref invocation.Disposed, 1) != 0
        )
            return;
        var invoked = false;
        void Finish()
        {
            invoked = true;
            leave?.Invoke(
                invocation.Snapshot
                    ?? new NativeObservationSnapshot(null, new object[argumentCount], null),
                invocation
            );
        }
        try
        {
            Plugin.Observe("native_observer_leave", Finish);
        }
        finally
        {
            try
            {
                // Abort may close writer admission before owner cleanup can enter it.
                if (!invoked)
                    Finish();
            }
            finally
            {
                invocation.Lease.Dispose();
                invocation.Snapshot = null;
                invocation.OwnerState = null;
            }
        }
    }

    private static Action<NativeObservationSnapshot, Invocation>? Bind(
        MethodBase target,
        Type owner,
        string? name,
        bool prefix
    )
    {
        if (name is null)
            return null;
        var method =
            owner.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(owner.FullName, name);
        if (method.ReturnType != typeof(void))
            throw new InvalidDataException("Native observers must return void.");
        var snapshot = Expression.Parameter(typeof(NativeObservationSnapshot), "snapshot");
        var invocation = Expression.Parameter(typeof(Invocation), "invocation");
        var ownerState = Expression.Field(invocation, nameof(Invocation.OwnerState));
        ParameterExpression? outputState = null;
        var parameters = method.GetParameters();
        var arguments = parameters
            .Select(parameter =>
            {
                var type = parameter.ParameterType;
                if (parameter.Name == "__state" && type.IsByRef)
                {
                    if (!prefix || !parameter.IsOut || outputState is not null)
                        throw new InvalidDataException("Only an entry observer may create state.");
                    outputState = Expression.Variable(type.GetElementType()!, "ownerState");
                    return (Expression)outputState;
                }
                if (type.IsByRef)
                    throw new InvalidDataException(
                        "Native observers cannot replace arguments or results."
                    );
                Expression value = parameter.Name switch
                {
                    "__instance" => Expression.Property(
                        snapshot,
                        nameof(NativeObservationSnapshot.Instance)
                    ),
                    "__args" => Expression.Property(
                        snapshot,
                        nameof(NativeObservationSnapshot.Arguments)
                    ),
                    "__result" => Expression.Property(
                        snapshot,
                        nameof(NativeObservationSnapshot.Result)
                    ),
                    "__originalMethod" => Expression.Constant(target, typeof(MethodBase)),
                    "__state" => ownerState,
                    "__nativeFault" => Expression.Field(invocation, nameof(Invocation.NativeFault)),
                    "__snapshotAvailable" => Expression.NotEqual(
                        Expression.Field(invocation, nameof(Invocation.Snapshot)),
                        Expression.Constant(null, typeof(NativeObservationSnapshot))
                    ),
                    var indexed
                        when indexed is not null
                            && indexed.StartsWith("__", StringComparison.Ordinal)
                            && int.TryParse(indexed.AsSpan(2), out var index)
                            && index >= 0
                            && index < target.GetParameters().Length => Expression.ArrayIndex(
                        Expression.Property(snapshot, nameof(NativeObservationSnapshot.Arguments)),
                        Expression.Constant(index)
                    ),
                    _ => throw new InvalidDataException(
                        "Unsupported native observer parameter: " + parameter.Name
                    ),
                };
                return Expression.Convert(value, type);
            })
            .ToArray();
        Expression body = Expression.Call(method, arguments);
        if (outputState is not null)
            body = Expression.Block(
                [outputState],
                Expression.TryFinally(
                    body,
                    Expression.Block(
                        Expression.Assign(
                            ownerState,
                            Expression.Convert(outputState, typeof(object))
                        ),
                        Expression.Empty()
                    )
                )
            );
        return Expression
            .Lambda<Action<NativeObservationSnapshot, Invocation>>(body, snapshot, invocation)
            .Compile();
    }

    private sealed class Invocation(IDisposable lease)
    {
        internal readonly IDisposable Lease = lease;
        internal NativeObservationSnapshot? Snapshot;
        public object? OwnerState;
        public bool NativeFault;
        internal int Disposed;
    }
}

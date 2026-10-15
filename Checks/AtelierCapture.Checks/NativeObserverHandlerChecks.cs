using System.Reflection;
using System.Runtime.InteropServices;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class NativeObserverHandlerChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const BindingFlags Fields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo Target = typeof(SyntheticOriginal).GetMethod(
        nameof(SyntheticOriginal.Original),
        BindingFlags.Instance | BindingFlags.Public
    )!;
    private static readonly Type Invocation = typeof(NativeObserverHandlers).GetNestedType(
        "Invocation",
        BindingFlags.NonPublic
    )!;
    private static readonly MethodInfo BindMethod = typeof(NativeObserverHandlers).GetMethod(
        "Bind",
        PrivateStatic
    )!;
    private static readonly List<Observed> Observations = [];
    private static int cleanups;
    private static bool failEntry,
        failCleanup;

    internal static void Run()
    {
        CheckCompiledBindings();
        CheckRejectedBindings();
        CheckCleanup(false, false);
        CheckCleanup(true, false);
        CheckCleanup(false, true);
        CheckCleanup(true, true);
        CheckMaterializedPhases();
        CheckFailedPhaseMaterialization(false);
        CheckFailedPhaseMaterialization(true);
        CheckPrimitiveCells();
    }

    private static void CheckCompiledBindings()
    {
        Observations.Clear();
        cleanups = 0;
        failEntry = true;
        var lease = new CountedLease();
        var state = NewInvocation(lease);
        var firstInstance = new SyntheticOriginal();
        var firstArgument = new object();
        var entryArgs = new object[] { 17, firstArgument };
        var entered = Snapshot(firstInstance, entryArgs, null);
        Expect<InvalidOperationException>(() =>
            Bind(nameof(Entry), true).DynamicInvoke(entered, state)
        );
        Require(
            Invocation.GetField("OwnerState", Fields)!.GetValue(state)
                is OwnerState { Token: "assigned-before-throw" },
            "An out-state assigned before a throwing prefix survives the compiled finally assignment."
        );
        var nextInstance = new SyntheticOriginal();
        var nextArgument = new object();
        var result = new object();
        var returnedArgs = new object[] { 29, nextArgument };
        Bind(nameof(Returned), false)
            .DynamicInvoke(Snapshot(nextInstance, returnedArgs, result), state);
        Invocation.GetField("NativeFault", Fields)!.SetValue(state, true);
        Bind(nameof(FinalizeObserver), false)
            .DynamicInvoke(Snapshot(nextInstance, returnedArgs, result), state);
        Require(
            Observations.Count == 3
                && ReferenceEquals(Observations[0].Instance, firstInstance)
                && ReferenceEquals(Observations[1].Instance, nextInstance)
                && ReferenceEquals(Observations[2].Instance, nextInstance)
                && Observations[0].Value == 17
                && Observations[1].Value == 29
                && Observations[2].Value == 29
                && ReferenceEquals(Observations[1].Argument, nextArgument)
                && ReferenceEquals(Observations[1].Result, result)
                && !Observations[1].NativeFault
                && Observations[2].NativeFault,
            "Compiled observers receive each phase's fresh instance, byref input value, arguments and result; native fault remains an input flag."
        );
        Require(
            entryArgs[0].Equals(17)
                && ReferenceEquals(entryArgs[1], firstArgument)
                && returnedArgs[0].Equals(29)
                && ReferenceEquals(returnedArgs[1], nextArgument),
            "Read-only observer binding cannot write through a native ref argument or replace original values."
        );
        Require(
            Observations.All(item => ReferenceEquals(item.Target, Target)) && cleanups == 1,
            "Every observer receives the exact original MethodBase and assigned owner state."
        );
        lease.Dispose();
        failEntry = false;
    }

    private static void CheckPrimitiveCells()
    {
        var cell = Marshal.AllocHGlobal(16);
        var pointerCell = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            var signed = new NativeSnapshotReader(typeof(int), "i4");
            foreach (var value in new[] { 0, -1, int.MinValue, int.MaxValue })
            {
                Marshal.WriteInt32(cell, value);
                Require(
                    signed.Read(cell) is int actual && actual == value,
                    "Int32 cells preserve signed zero, negative and boundary values."
                );
            }
            var enumeration = new NativeSnapshotReader(typeof(SignedNativeFlags), "i8");
            foreach (
                var value in new[]
                {
                    0L,
                    -1L,
                    long.MinValue,
                    long.MaxValue,
                    unchecked((long)0xff00ff00ff00ff00UL),
                }
            )
            {
                Marshal.WriteInt64(cell, value);
                Require(
                    enumeration.Read(cell) is SignedNativeFlags actual && (long)actual == value,
                    "Int64 enum cells preserve negative and unnamed all-bit flag patterns."
                );
            }
            var boolean = new NativeSnapshotReader(typeof(bool), "u1");
            foreach (var value in new byte[] { 0, 1, 0x80, 0xff })
            {
                Marshal.WriteByte(cell, value);
                Require(
                    boolean.Read(cell) is bool actual && actual == (value != 0),
                    "Native booleans read one byte and preserve zero/nonzero semantics."
                );
            }
            var character = new NativeSnapshotReader(typeof(char), "u2");
            foreach (var value in new ushort[] { 0, 0x41, 0xd800, 0xffff })
            {
                Marshal.WriteInt16(cell, unchecked((short)value));
                Require(
                    character.Read(cell) is char actual && actual == value,
                    "Native char cells preserve all sixteen UTF-16 bits including unpaired surrogates."
                );
            }
            var single = new NativeSnapshotReader(typeof(float), "f4");
            foreach (
                var bits in new[]
                {
                    0,
                    int.MinValue,
                    0x3f800000,
                    0x7f800000,
                    0x7fc12345,
                    unchecked((int)0xffc54321),
                }
            )
            {
                Marshal.WriteInt32(cell, bits);
                Require(
                    single.Read(cell) is float actual
                        && BitConverter.SingleToInt32Bits(actual) == bits,
                    "Single cells preserve exact signed-zero, infinity and NaN payload bits."
                );
            }
            var real = new NativeSnapshotReader(typeof(double), "f8");
            foreach (
                var bits in new[]
                {
                    0L,
                    long.MinValue,
                    0x3ff0000000000000L,
                    0x7ff0000000000000L,
                    0x7ff8123456789abcL,
                    unchecked((long)0xfff8fedcba987654UL),
                }
            )
            {
                Marshal.WriteInt64(cell, bits);
                Require(
                    real.Read(cell) is double actual
                        && BitConverter.DoubleToInt64Bits(actual) == bits,
                    "Double cells preserve exact signed-zero, infinity and NaN payload bits."
                );
            }
            var reference = new NativeSnapshotReader(typeof(int).MakeByRefType(), "b4");
            Marshal.WriteInt32(cell, -137);
            Marshal.WriteIntPtr(pointerCell, cell);
            Require(
                reference.Read(pointerCell) is int referred && referred == -137,
                "A byref argument cell follows its pointer exactly once before reading the original signed value."
            );
            Require(
                Marshal.ReadInt32(cell) == -137 && Marshal.ReadIntPtr(pointerCell) == cell,
                "Primitive observation never mutates the original byref cell or pointer."
            );
            Marshal.WriteIntPtr(pointerCell, IntPtr.Zero);
            Require(
                reference.Read(pointerCell) is null,
                "A null native byref target is represented explicitly without dereferencing null."
            );
            var receiver = new NativeSnapshotReader(typeof(int), "i4", receiver: true);
            Require(
                receiver.Read(cell, direct: true) is int direct && direct == -137,
                "A direct receiver cell is read directly even when its reader normally follows an argument pointer."
            );
        }
        finally
        {
            Marshal.FreeHGlobal(pointerCell);
            Marshal.FreeHGlobal(cell);
        }
    }

    private enum SignedNativeFlags : long
    {
        None = 0,
    }

    private static void CheckRejectedBindings()
    {
        foreach (
            var name in new[]
            {
                nameof(UnknownParameter),
                nameof(OutArgument),
                nameof(RefArgument),
                nameof(RefResult),
                nameof(BoolPrefix),
            }
        )
            Expect<InvalidDataException>(() => Bind(name, true));
        Expect<InvalidDataException>(() => Bind(nameof(Entry), false));
        Expect<InvalidDataException>(() => Bind(nameof(RefState), true));
        Expect<InvalidDataException>(() => Bind(nameof(OutsideArgument), false));
    }

    private static void CheckCleanup(bool aborted, bool throwing)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "capture-native-observer-" + Guid.NewGuid().ToString("N")
        );
        var previous = Plugin.Writer;
        var lease = new CountedLease();
        var state = NewInvocation(lease);
        cleanups = 0;
        failCleanup = throwing;
        failEntry = true;
        using var writer = new SessionWriter(
            root,
            new { synthetic = true },
            _ => new { },
            _ => { }
        );
        try
        {
            Plugin.Writer = writer;
            var snapshot = Snapshot(new object(), [31, new object()], new object());
            Expect<InvalidOperationException>(() =>
                Bind(nameof(Entry), true).DynamicInvoke(snapshot, state)
            );
            var ownerState = (OwnerState)
                Invocation.GetField("OwnerState", Fields)!.GetValue(state)!;
            Invocation.GetField("Snapshot", Fields)!.SetValue(state, snapshot);
            Invocation.GetField("NativeFault", Fields)!.SetValue(state, true);
            var handlers = new NativeObserverHandlers(
                Target,
                typeof(NativeObserverHandlerChecks),
                null,
                null,
                nameof(FinalizeObserver)
            );
            if (aborted)
                writer.Abort("Synthetic explicit abort before native cleanup");
            try
            {
                handlers.Leave(state);
            }
            catch (CaptureAbortedException) when (aborted) { }
            catch (InvalidOperationException) when (aborted && throwing) { }
            handlers.Leave(state);
            Require(
                cleanups == 1
                    && lease.Disposed == 1
                    && ownerState.Releases == 1
                    && Invocation.GetField("Snapshot", Fields)!.GetValue(state) is null
                    && Invocation.GetField("OwnerState", Fields)!.GetValue(state) is null,
                "Successful, observer-faulted and aborted writer admission each finalize state once and release the outer native lease."
            );
        }
        finally
        {
            failEntry = false;
            failCleanup = false;
            writer.Dispose();
            if (aborted)
                Require(
                    SpinWait.SpinUntil(
                        () =>
                        {
                            using var status = System.Text.Json.JsonDocument.Parse(
                                System.Text.Json.JsonSerializer.Serialize(writer.GetStatus())
                            );
                            return status.RootElement.GetProperty("completed").GetBoolean();
                        },
                        TimeSpan.FromSeconds(5)
                    ),
                    "Aborted synthetic writer closes before fixture cleanup."
                );
            Plugin.Writer = previous;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void CheckMaterializedPhases()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "capture-native-frame-" + Guid.NewGuid().ToString("N")
        );
        var prior = Plugin.Writer;
        var lifetime = (ObservationLifetime)
            typeof(Plugin)
                .GetField("Lifetime", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
        var materializations = 0;
        Observations.Clear();
        cleanups = 0;
        failEntry = true;
        failCleanup = false;
        NativeObserverHandlers? handlers = null;
        object? state = null;
        using var writer = new SessionWriter(
            root,
            new { synthetic = true },
            _ => new { },
            _ => { }
        );
        try
        {
            Plugin.Writer = writer;
            Require(
                !lifetime.OwnsNativeCallback,
                "Native callback lease starts unowned in the managed fixture."
            );
            handlers = new NativeObserverHandlers(
                Target,
                typeof(NativeObserverHandlerChecks),
                nameof(Entry),
                nameof(Returned),
                nameof(FinalizeObserver)
            );
            var entryInstance = new SyntheticOriginal();
            var entryArgument = new object();
            var frame = Frame(() =>
            {
                materializations++;
                return Snapshot(entryInstance, [41, entryArgument], null);
            });
            Require(
                materializations == 0,
                "Lazy native frame cannot materialize wrappers before observer/writer admission."
            );
            state = handlers.Enter(frame);
            Require(
                state is not null
                    && materializations == 1
                    && lifetime.OwnsNativeCallback
                    && Invocation.GetField("OwnerState", Fields)!.GetValue(state) is OwnerState,
                "Actual entry preserves assigned state and the outer lease after a prefix observer throws."
            );
            var exitInstance = new SyntheticOriginal();
            var exitArgument = new object();
            var returned = new object();
            handlers.Returned(
                Frame(
                    () =>
                    {
                        materializations++;
                        return Snapshot(exitInstance, [43, exitArgument], returned);
                    },
                    NativeObservationPhase.Returned
                ),
                state
            );
            var faultInstance = new SyntheticOriginal();
            var faultArgument = new object();
            var faultResult = new object();
            handlers.Faulted(
                Frame(
                    () =>
                    {
                        materializations++;
                        return Snapshot(faultInstance, [47, faultArgument], faultResult);
                    },
                    NativeObservationPhase.Faulted
                ),
                state
            );
            handlers.Leave(state);
            handlers.Leave(state);
            Require(
                materializations == 3
                    && Observations.Count == 3
                    && ReferenceEquals(Observations[0].Instance, entryInstance)
                    && ReferenceEquals(Observations[1].Instance, exitInstance)
                    && ReferenceEquals(Observations[1].Result, returned)
                    && ReferenceEquals(Observations[2].Instance, faultInstance)
                    && ReferenceEquals(Observations[2].Argument, faultArgument)
                    && ReferenceEquals(Observations[2].Result, faultResult)
                    && Observations[2].Value == 47
                    && Observations[2].NativeFault
                    && cleanups == 1
                    && !lifetime.OwnsNativeCallback,
                "Actual return/fault paths materialize fresh phase state; finalizer receives the native-fault flag and releases the native lease once."
            );
        }
        finally
        {
            failEntry = false;
            if (state is not null)
                handlers!.Leave(state);
            writer.Dispose();
            Plugin.Writer = prior;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static NativeObservation Frame(
        Func<NativeObservationSnapshot> materialize,
        NativeObservationPhase phase = NativeObservationPhase.Entering
    ) => new(Target, phase, materialize);

    private static void CheckFailedPhaseMaterialization(bool nativeFault)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "capture-native-missing-phase-" + Guid.NewGuid().ToString("N")
        );
        var previous = Plugin.Writer;
        var lifetime = (ObservationLifetime)
            typeof(Plugin)
                .GetField("Lifetime", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null)!;
        cleanups = 0;
        failEntry = false;
        missingNativeFault = !nativeFault;
        using var writer = new SessionWriter(
            root,
            new { synthetic = true },
            _ => new { },
            _ => { }
        );
        var handlers = new NativeObserverHandlers(
            Target,
            typeof(NativeObserverHandlerChecks),
            nameof(Entry),
            null,
            nameof(FinalizeMissingSnapshot)
        );
        object? state = null;
        try
        {
            Plugin.Writer = writer;
            var entryInstance = new SyntheticOriginal();
            var entryArgument = new object();
            state = handlers.Enter(
                Frame(() => Snapshot(entryInstance, [59, entryArgument], new object()))
            );
            Require(
                state is not null
                    && Invocation.GetField("Snapshot", Fields)!.GetValue(state) is not null,
                "Entry snapshot exists before the later native phase fails to materialize."
            );
            var owner = (OwnerState)Invocation.GetField("OwnerState", Fields)!.GetValue(state)!;
            var unavailable = Frame(
                () => throw new InvalidDataException("Synthetic native phase read failure"),
                nativeFault ? NativeObservationPhase.Faulted : NativeObservationPhase.Returned
            );
            if (nativeFault)
                handlers.Faulted(unavailable, state);
            else
                handlers.Returned(unavailable, state);
            Require(
                Invocation.GetField("Snapshot", Fields)!.GetValue(state) is null,
                "Failed later-phase materialization clears the earlier snapshot rather than presenting stale entry values as fresh evidence."
            );
            handlers.Leave(state);
            handlers.Leave(state);
            Require(
                cleanups == 1
                    && owner.Releases == 1
                    && missingNativeFault == nativeFault
                    && !lifetime.OwnsNativeCallback,
                "Missing phase snapshot still runs owner cleanup once and releases the outer native invocation lease."
            );
        }
        finally
        {
            if (state is not null)
                handlers.Leave(state);
            writer.Dispose();
            Plugin.Writer = previous;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static void FinalizeMissingSnapshot(
        bool __nativeFault,
        bool __snapshotAvailable,
        object? __instance,
        object?[] __args,
        object? __result,
        OwnerState __state
    )
    {
        cleanups++;
        try
        {
            Require(
                !__snapshotAvailable
                    && __instance is null
                    && __result is null
                    && __args.Length == 2
                    && __args.All(value => value is null),
                "Unavailable phase uses an explicit empty shape of the correct argument count, without stale receiver/argument/result values."
            );
            Require(
                __state.Token == "assigned-before-throw",
                "Unavailable snapshot still retains the actual prefix owner state for cleanup."
            );
            missingNativeFault = __nativeFault;
        }
        finally
        {
            __state.Releases++;
        }
    }

    private static bool missingNativeFault;

    private static Delegate Bind(string name, bool prefix) =>
        (Delegate)
            BindMethod.Invoke(null, [Target, typeof(NativeObserverHandlerChecks), name, prefix])!;

    private static object NewInvocation(IDisposable lease) =>
        Activator.CreateInstance(Invocation, Fields, null, [lease], null)!;

    private static NativeObservationSnapshot Snapshot(
        object? instance,
        object[] arguments,
        object? result
    ) =>
        (NativeObservationSnapshot)
            Activator.CreateInstance(
                typeof(NativeObservationSnapshot),
                [instance, arguments, result]
            )!;

    private sealed class SyntheticOriginal
    {
        public object Original(ref int value, object input) => input;
    }

    private static void Entry(
        object __instance,
        object[] __args,
        int __0,
        object __1,
        MethodBase __originalMethod,
        out OwnerState __state
    )
    {
        __state = new OwnerState("assigned-before-throw");
        Observations.Add(new(__instance, __0, __1, null, false, __originalMethod));
        Require(
            __args[0].Equals(__0) && ReferenceEquals(__args[1], __1),
            "Indexed and array inputs agree."
        );
        if (failEntry)
            throw new InvalidOperationException(
                "Synthetic entry observer failure after assigned state."
            );
    }

    private static void Returned(
        object __instance,
        object[] __args,
        object __result,
        OwnerState __state,
        bool __nativeFault,
        MethodBase __originalMethod
    )
    {
        Require(
            __state.Token == "assigned-before-throw",
            "Returned observer receives entry state."
        );
        Observations.Add(
            new(__instance, (int)__args[0], __args[1], __result, __nativeFault, __originalMethod)
        );
    }

    private static void FinalizeObserver(
        object __instance,
        object[] __args,
        object __result,
        OwnerState __state,
        bool __nativeFault,
        MethodBase __originalMethod
    )
    {
        cleanups++;
        try
        {
            Require(
                __state.Token == "assigned-before-throw",
                "Finalizer receives state even after entry observer failure."
            );
            Observations.Add(
                new(
                    __instance,
                    (int)__args[0],
                    __args[1],
                    __result,
                    __nativeFault,
                    __originalMethod
                )
            );
            if (failCleanup)
                throw new InvalidOperationException("Synthetic cleanup observer failure.");
        }
        finally
        {
            __state.Releases++;
        }
    }

    private static void UnknownParameter(object unknown) { }

    private static void OutsideArgument(object __2) { }

    private static void OutArgument(out int __0) => __0 = 99;

    private static void RefArgument(ref int __0) => __0 = 99;

    private static void RefResult(ref object __result) => __result = new object();

    private static void RefState(ref OwnerState __state) { }

    private static bool BoolPrefix() => false;

    private sealed record OwnerState(string Token)
    {
        internal int Releases;
    }

    private sealed record Observed(
        object? Instance,
        int Value,
        object? Argument,
        object? Result,
        bool NativeFault,
        MethodBase Target
    );

    private sealed class CountedLease : IDisposable
    {
        internal int Disposed;

        public void Dispose() => Disposed++;
    }

    private static void Expect<T>(Action action)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (Exception error) when (Unwrap(error) is T)
        {
            return;
        }
        throw new InvalidOperationException(
            "Expected native observer contract rejection: " + typeof(T).Name
        );
    }

    private static Exception Unwrap(Exception error)
    {
        while (error is TargetInvocationException { InnerException: { } inner })
            error = inner;
        return error;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

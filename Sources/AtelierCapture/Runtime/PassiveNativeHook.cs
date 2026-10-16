using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Runtime;

namespace AtelierCapture;

internal sealed class PassiveNativeHook : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Callback(nint frame);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Find(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string identity,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string shape
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Entry(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Bind(int index, nint source, nint original, nint callback);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SlotAction(int index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Catalog();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int Prepare(nint source, nint target, nint* original);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DobbyAction(nint source);

    private static readonly object Setup = new();
    private static readonly List<PassiveNativeHook> Roots = [];
    private static nint library;
    private static nint dobby;
    private static Find? find;
    private static Entry? entry;
    private static Bind? bind;
    private static SlotAction? close;
    private static SlotAction? restored;
    private static Prepare? prepare;
    private static DobbyAction? commit;
    private static DobbyAction? destroy;
    private static JsonDocument? catalog;
    private static Exception? loadFailure;
    private static int closed;

    [ThreadStatic]
    private static int callbackDepth;
    internal static bool IsClosed => Volatile.Read(ref closed) != 0;
    private readonly MethodBase method;
    private readonly Func<NativeObservation, object?> enterObserver;
    private readonly Action<NativeObservation, object?> returnedObserver;
    private readonly Action<NativeObservation, object?> faultObserver;
    private readonly Action<object?> leaveObserver;
    private readonly Action<Exception> failure;
    private readonly Callback callback;
    private readonly NativeSnapshotReader? receiver;
    private readonly NativeSnapshotReader[] arguments;
    private readonly NativeSnapshotReader? result;
    private readonly bool constructor;
    private readonly int slot;
    private readonly nint source;
    private bool disposed;
    private bool restoredSource;

    private PassiveNativeHook(
        MethodBase method,
        int slot,
        nint source,
        string[] argumentCodes,
        string returnCode,
        Func<NativeObservation, object?> enter,
        Action<NativeObservation, object?> returned,
        Action<NativeObservation, object?> faulted,
        Action<object?> leave,
        Action<Exception> observerFailure
    )
    {
        this.method = method;
        this.slot = slot;
        this.source = source;
        enterObserver = enter;
        returnedObserver = returned;
        faultObserver = faulted;
        leaveObserver = leave;
        failure = observerFailure;
        constructor = method is ConstructorInfo;
        arguments = method
            .GetParameters()
            .Select((p, i) => new NativeSnapshotReader(p.ParameterType, argumentCodes[i]))
            .ToArray();
        receiver = method.IsStatic
            ? null
            : new NativeSnapshotReader(method.DeclaringType!, "p", true);
        result =
            returnCode == "v"
                ? null
                : new NativeSnapshotReader(((MethodInfo)method).ReturnType, returnCode);
        callback = Observe;
    }

    internal static unsafe PassiveNativeHook Install(
        MethodBase target,
        Func<NativeObservation, object?> enter,
        Action<NativeObservation, object?> returned,
        Action<NativeObservation, object?> faulted,
        Action<object?> leave,
        Action<Exception> observerFailure
    )
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Native capture requires Windows x64.");
        if (callbackDepth != 0)
            throw new InvalidOperationException(
                "Native observation cannot install a hook from its callback."
            );
        lock (Setup)
        {
            if (IsClosed)
                throw new InvalidOperationException(
                    "Native capture was closed after activation rollback or shutdown."
                );
            Load();
            var identity =
                target.DeclaringType!.FullName
                + "."
                + target.Name
                + "("
                + string.Join(
                    ",",
                    target
                        .GetParameters()
                        .Select(p => RuntimeCompatibility.SignatureType(p.ParameterType))
                )
                + ")";
            var row = catalog!
                .RootElement.GetProperty("contracts")
                .EnumerateArray()
                .SingleOrDefault(r => r.GetProperty("identity").GetString() == identity);
            if (row.ValueKind == JsonValueKind.Undefined)
                throw new NotSupportedException(
                    "Native observer signature is outside the finite catalog: " + identity
                );
            var receiverKind =
                target.IsStatic ? "static"
                : target.DeclaringType.IsSubclassOf(typeof(Il2CppSystem.ValueType)) ? "value"
                : "reference";
            var returnCode = target is MethodInfo m ? Code(m.ReturnType, true) : "v";
            var argumentCodes = target
                .GetParameters()
                .Select(p => Code(p.ParameterType, false))
                .ToArray();
            var shape = returnCode + ":" + receiverKind + ":" + string.Join(",", argumentCodes);
            if (shape != row.GetProperty("shape").GetString())
                throw new InvalidDataException("Native observer ABI changed: " + identity);
            var index = find!(identity, shape);
            if (index < 0)
                throw new InvalidDataException("Native observer compiled catalog does not match.");
            var field =
                Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target)
                ?? throw new InvalidDataException(
                    "Native observer MethodInfo pointer is unavailable."
                );
            var info = (nint)field.GetValue(null)!;
            var nativeSource = UnityVersionHandler.Wrap((Il2CppMethodInfo*)info).MethodPointer;
            if (nativeSource == 0)
                throw new InvalidDataException("Native observer target pointer is null.");
            if (Roots.Any(h => h.source == nativeSource))
                throw new InvalidDataException("Native observer target is already registered.");
            if (HarmonyLib.Harmony.GetPatchInfo(target) is not null || IsRedirected(nativeSource))
                throw new InvalidDataException(
                    "Native observer target is already redirected or is a jump-only entry."
                );
            var hook = new PassiveNativeHook(
                target,
                index,
                nativeSource,
                argumentCodes,
                returnCode,
                enter,
                returned,
                faulted,
                leave,
                observerFailure
            );
            nint original = 0;
            var prepared = false;
            var published = false;
            try
            {
                Success(prepare!(nativeSource, entry!(index), &original), "native prepare");
                prepared = true;
                if (original == 0)
                    throw new InvalidDataException("Native observer prepared no original gateway.");
                Success(
                    bind!(
                        index,
                        nativeSource,
                        original,
                        Marshal.GetFunctionPointerForDelegate(hook.callback)
                    ),
                    "native bind"
                );
                published = true;
                Roots.Add(hook);
                Success(commit!(nativeSource), "native commit");
                return hook;
            }
            catch
            {
                if (published)
                    hook.Dispose();
                else if (prepared)
                    Success(destroy!(nativeSource), "failed native prepare cleanup");
                throw;
            }
        }
    }

    private static T Export<T>(nint handle, string name)
        where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    private static void Load()
    {
        if (loadFailure is not null)
            throw new InvalidOperationException(
                "Native capture initialization previously failed.",
                loadFailure
            );
        if (library != 0)
            return;
        try
        {
            library = NativeLibrary.Load(
                Path.Combine(
                    Path.GetDirectoryName(typeof(Plugin).Assembly.Location)!,
                    "AtelierCapture.Native.dll"
                )
            );
            dobby = NativeLibrary.Load(Path.Combine(Paths.BepInExRootPath, "core", "dobby.dll"));
            find = Export<Find>(library, "capture_find");
            entry = Export<Entry>(library, "capture_entry");
            bind = Export<Bind>(library, "capture_bind");
            close = Export<SlotAction>(library, "capture_close");
            restored = Export<SlotAction>(library, "capture_restored");
            prepare = Export<Prepare>(dobby, "DobbyPrepare");
            commit = Export<DobbyAction>(dobby, "DobbyCommit");
            destroy = Export<DobbyAction>(dobby, "DobbyDestroy");
            catalog = JsonDocument.Parse(
                Marshal.PtrToStringUTF8(Export<Catalog>(library, "capture_catalog")())!
            );
        }
        catch (Exception error)
        {
            loadFailure = error;
            throw;
        }
    }

    private static string Code(Type type, bool returned)
    {
        if (type.IsByRef)
            return "b" + Code(type.GetElementType()!, false);
        if (type == typeof(void))
            return "v";
        if (type.IsEnum)
            return Code(Enum.GetUnderlyingType(type), returned);
        if (type == typeof(float))
            return "f4";
        if (type == typeof(double))
            return "f8";
        if (type.IsPrimitive)
            return Type.GetTypeCode(type) switch
            {
                TypeCode.Boolean or TypeCode.Byte or TypeCode.SByte => "i1",
                TypeCode.Char or TypeCode.Int16 or TypeCode.UInt16 => "i2",
                TypeCode.Int32 or TypeCode.UInt32 => "i4",
                TypeCode.Int64 or TypeCode.UInt64 => "i8",
                _ => throw new NotSupportedException("Unknown native primitive ABI."),
            };
        if (type == typeof(string))
            return "p";
        if (!type.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
            return typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).IsAssignableFrom(
                type
            )
                ? "p"
                : throw new NotSupportedException("Unknown native argument class.");
        var name = RuntimeCompatibility.SignatureType(type);
        if (!catalog!.RootElement.GetProperty("types").TryGetProperty(name, out var expected))
            throw new NotSupportedException(
                "Native value layout is outside the finite catalog: " + name
            );
        uint alignment = 0;
        var nativeClass = Il2CppClassPointerStore.GetNativeClassPointer(type);
        var size = IL2CPP.il2cpp_class_value_size(nativeClass, ref alignment);
        if (
            size != expected.GetProperty("size").GetInt32()
            || alignment != expected.GetProperty("align").GetUInt32()
        )
            throw new InvalidDataException("Native value layout changed: " + name);
        foreach (var field in expected.GetProperty("fields").EnumerateArray())
        {
            var nativeField = IL2CPP.il2cpp_class_get_field_from_name(
                nativeClass,
                field.GetProperty("name").GetString()!
            );
            if (
                nativeField == 0
                || IL2CPP.il2cpp_field_get_offset(nativeField)
                    != field.GetProperty("offset").GetUInt32() + 2 * IntPtr.Size
            )
                throw new InvalidDataException("Native value field layout changed: " + name);
        }
        return (returned ? "s" : "d") + size;
    }

    private unsafe void Observe(nint pointer)
    {
        callbackDepth++;
        try
        {
            ref var frame = ref Unsafe.AsRef<NativeObservationFrame>((void*)pointer);
            if (
                frame.Size != 64
                || frame.Version != 1
                || frame.Binding != (ulong)slot
                || frame.ArgumentCount != arguments.Length
            )
                throw new InvalidDataException("Native observation frame contract changed.");
            Invocation? invocation = null;
            if (frame.Phase == NativeObservationPhase.Entering)
            {
                invocation = new Invocation();
                var handle = GCHandle.Alloc(invocation);
                frame.Token = GCHandle.ToIntPtr(handle);
            }
            else if (frame.Token != 0)
                invocation = GCHandle.FromIntPtr(frame.Token).Target as Invocation;
            if (invocation is null)
                return;
            if (frame.Phase == NativeObservationPhase.Leaving)
            {
                try
                {
                    leaveObserver(invocation.State);
                }
                finally
                {
                    GCHandle.FromIntPtr(frame.Token).Free();
                    frame.Token = 0;
                    invocation.State = null;
                }
                return;
            }
            var captured = frame;
            var observation = new NativeObservation(
                method,
                frame.Phase,
                () => Materialize(captured)
            );
            try
            {
                switch (frame.Phase)
                {
                    case NativeObservationPhase.Entering:
                        invocation.State = enterObserver(observation);
                        break;
                    case NativeObservationPhase.Returned:
                        returnedObserver(observation, invocation.State);
                        break;
                    case NativeObservationPhase.Faulted:
                        faultObserver(observation, invocation.State);
                        break;
                    default:
                        throw new InvalidDataException("Unknown native observation phase.");
                }
            }
            finally
            {
                observation.Invalidate();
            }
        }
        catch (Exception error)
        {
            Report(error);
        }
        finally
        {
            callbackDepth--;
        }
    }

    private NativeObservationSnapshot Materialize(NativeObservationFrame frame)
    {
        var values = new object?[arguments.Length];
        for (var index = 0; index < values.Length; index++)
            values[index] = arguments[index]
                .Read(Marshal.ReadIntPtr(frame.Arguments, index * IntPtr.Size));
        var instance =
            constructor && frame.Phase != NativeObservationPhase.Returned
                ? null
                : receiver?.Read(frame.Receiver, true);
        var returned =
            frame.Phase == NativeObservationPhase.Returned && result is not null
                ? result.Read(frame.Result)
                : null;
        return new(instance, values, returned);
    }

    private static unsafe bool IsRedirected(nint source)
    {
        var bytes = (byte*)source;
        return bytes[0] == 0xE9
            || bytes[0] == 0xEB
            || bytes[0] == 0xFF && bytes[1] == 0x25
            || bytes[0] == 0x48 && bytes[1] == 0xB8 && bytes[10] == 0xFF && bytes[11] == 0xE0;
    }

    private sealed class Invocation
    {
        internal object? State;
    }

    private void Report(Exception error)
    {
        try
        {
            failure(error);
        }
        catch { }
    }

    private static void Success(int code, string operation)
    {
        if (code != 0)
            throw new InvalidOperationException(operation + " failed with status " + code + ".");
    }

    public void Dispose()
    {
        if (callbackDepth != 0)
            throw new InvalidOperationException(
                "Native observation shutdown must run outside its callback."
            );
        lock (Setup)
        {
            if (disposed)
                return;
            Success(close!(slot), "native close/drain");
            Volatile.Write(ref closed, 1);
            foreach (var hook in Roots)
            {
                if (hook.restoredSource)
                    continue;
                Success(destroy!(hook.source), "native restore");
                hook.restoredSource = true;
            }
            Success(restored!(slot), "native restored publication");
            foreach (var hook in Roots)
                hook.disposed = true;
        }
    }
}

using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.Runtime;

namespace AtelierCapture;

internal enum NativeObservationPhase : uint
{
    Entering,
    Returned,
    Faulted,
    Leaving,
}

internal sealed record NativeObservationSnapshot(
    object? Instance,
    object?[] Arguments,
    object? Result,
    string? NativeAlias = null
);

internal sealed class NativeObservation
{
    private readonly Func<NativeObservationSnapshot> materialize;
    private bool available = true;

    internal NativeObservation(
        MethodBase method,
        NativeObservationPhase phase,
        Func<NativeObservationSnapshot> materialize
    )
    {
        Method = method;
        Phase = phase;
        this.materialize = materialize;
    }

    internal MethodBase Method { get; }
    internal NativeObservationPhase Phase { get; }

    internal NativeObservationSnapshot Materialize() =>
        available
            ? materialize()
            : throw new InvalidOperationException(
                "Native observation frame has returned to its caller."
            );

    internal void Invalidate() => available = false;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeObservationFrame
{
    internal uint Size;
    internal uint Version;
    internal NativeObservationPhase Phase;
    internal uint ArgumentCount;
    internal ulong Binding;
    internal nint Receiver;
    internal nint MethodInfo;
    internal nint Arguments;
    internal nint Result;
    internal nint Token;
}

internal sealed class NativeSnapshotReader
{
    private readonly Type type;
    private readonly bool indirect;
    private readonly int valueSize;
    private readonly nint classPointer;
    private readonly Func<nint, object?>? objectReader;
    private static readonly MethodInfo ReadObjectMethod = typeof(NativeSnapshotReader).GetMethod(
        nameof(ReadObject),
        BindingFlags.Static | BindingFlags.NonPublic
    )!;
    private static Func<nint, nint, nint>? boxNullable;

    internal NativeSnapshotReader(Type type, string code, bool receiver = false)
    {
        this.type = type.IsByRef ? type.GetElementType()! : type;
        indirect =
            receiver
            || code.StartsWith('b')
            || code.StartsWith('d') && int.Parse(code.AsSpan(1)) > 8;
        if (this.type.IsEnum || this.type.IsPrimitive || this.type == typeof(string))
            return;
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(this.type))
            throw new NotSupportedException(
                "Native snapshot type is not a qualified IL2CPP wrapper: " + this.type
            );
        classPointer = Il2CppClassPointerStore.GetNativeClassPointer(this.type);
        if (classPointer == 0)
            throw new InvalidDataException("Native snapshot class pointer is unavailable.");
        objectReader = ReadObjectMethod
            .MakeGenericMethod(this.type)
            .CreateDelegate<Func<nint, object?>>();
        if (this.type.IsSubclassOf(typeof(Il2CppSystem.ValueType)))
        {
            uint align = 0;
            valueSize = IL2CPP.il2cpp_class_value_size(classPointer, ref align);
            if (valueSize <= 0)
                throw new InvalidDataException("Native value snapshot size is invalid.");
        }
    }

    internal unsafe object? Read(nint cell, bool direct = false)
    {
        if (!direct && indirect)
            cell = Marshal.ReadIntPtr(cell);
        if (cell == 0)
            return null;
        if (type.IsEnum)
            return Enum.ToObject(type, ReadPrimitive(Enum.GetUnderlyingType(type), cell));
        if (type.IsPrimitive)
            return ReadPrimitive(type, cell);
        if (type == typeof(string))
            return IL2CPP.Il2CppStringToManaged(direct ? cell : Marshal.ReadIntPtr(cell));
        nint pointer;
        if (valueSize != 0)
        {
            if (
                type.IsConstructedGenericType
                && type.GetGenericTypeDefinition().FullName == "Il2CppSystem.Nullable`1"
            )
            {
                boxNullable ??= AppDomain
                    .CurrentDomain.GetAssemblies()
                    .Select(a =>
                        a.GetType("Il2CppInterop.HarmonySupport.Il2CppDetourMethodPatcher")
                    )
                    .First(t => t is not null)!
                    .GetMethod("BoxNullable", BindingFlags.Static | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<nint, nint, nint>>();
                pointer = boxNullable(classPointer, cell);
            }
            else
                pointer = IL2CPP.il2cpp_value_box(classPointer, cell);
        }
        else
            pointer = direct ? cell : Marshal.ReadIntPtr(cell);
        return pointer == 0 ? null : objectReader!(pointer);
    }

    private static object? ReadObject<T>(nint pointer) => Il2CppObjectPool.Get<T>(pointer);

    private static unsafe object ReadPrimitive(Type type, nint cell) =>
        Type.GetTypeCode(type) switch
        {
            TypeCode.Boolean => *(byte*)cell != 0,
            TypeCode.Byte => *(byte*)cell,
            TypeCode.SByte => *(sbyte*)cell,
            TypeCode.Int16 => *(short*)cell,
            TypeCode.UInt16 => *(ushort*)cell,
            TypeCode.Int32 => *(int*)cell,
            TypeCode.UInt32 => *(uint*)cell,
            TypeCode.Int64 => *(long*)cell,
            TypeCode.UInt64 => *(ulong*)cell,
            TypeCode.Single => *(float*)cell,
            TypeCode.Double => *(double*)cell,
            TypeCode.Char => *(char*)cell,
            _ => throw new NotSupportedException("Unknown native primitive snapshot type."),
        };
}

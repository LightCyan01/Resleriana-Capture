using System;

namespace Il2CppInterop.HarmonySupport;

internal static class NativeValueStorage
{
    internal static bool IsInlineWindows64Value(int size) => size is 1 or 2 or 4 or 8;

    internal static void ValidateNullableLayout(int instanceSize, int valueSize)
    {
        var header = 2 * IntPtr.Size;
        if (
            instanceSize <= header
            || instanceSize > 4096
            || valueSize <= 0
            || valueSize > instanceSize - header
        )
            throw new NotSupportedException(
                "Nullable native instance/payload layout is unsupported"
            );
    }

    internal static unsafe void CopyNullablePayload(
        IntPtr box,
        int instanceSize,
        IntPtr classPtr,
        IntPtr data,
        int valueSize
    )
    {
        ValidateNullableLayout(instanceSize, valueSize);
        if (box == IntPtr.Zero || classPtr == IntPtr.Zero)
            throw new ArgumentException("Nullable storage and class are required");
        var destination = new Span<byte>((void*)box, instanceSize);
        destination.Clear();
        *(IntPtr*)box = classPtr;
        // Value size excludes object alignment padding; copying instance size overreads caller storage.
        if (data != IntPtr.Zero)
            Buffer.MemoryCopy((void*)data, (byte*)box + 2 * IntPtr.Size, valueSize, valueSize);
    }
}

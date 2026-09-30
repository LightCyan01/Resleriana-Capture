using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace BepInEx.Unix;

internal static class UnixStreamHelper
{
    public delegate int dupDelegate(int fd);

    public delegate int fcloseDelegate(IntPtr stream);

    public delegate IntPtr fdopenDelegate(int fd, string mode);

    public delegate int fflushDelegate(IntPtr stream);

    public delegate IntPtr freadDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);

    public delegate int fwriteDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);

    public delegate int isattyDelegate(int fd);

    public static dupDelegate dup;
    public static fdopenDelegate fdopen;
    public static freadDelegate fread;
    public static fwriteDelegate fwrite;
    public static fcloseDelegate fclose;
    public static fflushDelegate fflush;
    public static isattyDelegate isatty;

    static UnixStreamHelper()
    {
        // MonoMod 25 removed the DynDllImport/DynDllMapping machinery, so resolve libc exports manually.
        if (!TryOpenLibc(out var libc))
            return;

        dup = GetExport<dupDelegate>(libc, "dup");
        fdopen = GetExport<fdopenDelegate>(libc, "fdopen");
        fread = GetExport<freadDelegate>(libc, "fread");
        fwrite = GetExport<fwriteDelegate>(libc, "fwrite");
        fclose = GetExport<fcloseDelegate>(libc, "fclose");
        fflush = GetExport<fflushDelegate>(libc, "fflush");
        isatty = GetExport<isattyDelegate>(libc, "isatty");
    }

    private static bool TryOpenLibc(out IntPtr libc)
    {
        if (DynDll.TryOpenLibrary("libc.so.6", out libc))
            return true;
        if (DynDll.TryOpenLibrary("libc", out libc))
            return true;
        return DynDll.TryOpenLibrary("/usr/lib/libSystem.dylib", out libc);
    }

    private static TDelegate GetExport<TDelegate>(IntPtr library, string name) where TDelegate : Delegate
    {
        if (!library.TryGetExport(name, out var ptr))
            return null;

        return (TDelegate) Marshal.GetDelegateForFunctionPointer(ptr, typeof(TDelegate));
    }

    public static Stream CreateDuplicateStream(int fileDescriptor)
    {
        var newFd = dup(fileDescriptor);

        return new UnixStream(newFd, FileAccess.Write);
    }
}

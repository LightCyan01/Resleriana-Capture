using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AtelierCapture;

internal static class GamePath
{
    internal static string Canonical(string path)
    {
        using var handle = Open(
            Path.GetFullPath(path),
            0,
            7,
            IntPtr.Zero,
            3,
            0x02000000,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var text = new StringBuilder(32768);
        var length = FinalPath(handle, text, (uint)text.Capacity, 0);
        if (length == 0 || length >= text.Capacity)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var resolved = text.ToString();
        if (resolved.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
            resolved = "\\\\" + resolved[8..];
        else if (resolved.StartsWith("\\\\?\\", StringComparison.Ordinal))
            resolved = resolved[4..];
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle Open(
        string path,
        uint access,
        uint share,
        IntPtr security,
        uint creation,
        uint flags,
        IntPtr template
    );

    [DllImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern uint FinalPath(
        SafeFileHandle handle,
        StringBuilder path,
        uint size,
        uint flags
    );
}

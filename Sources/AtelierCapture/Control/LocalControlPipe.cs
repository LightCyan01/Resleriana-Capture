using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AtelierCapture;

internal static class LocalControlPipe
{
    private const uint Duplex = 0x03;
    private const uint Overlapped = 0x40000000;
    private const uint FirstInstance = 0x00080000;
    private const uint RejectRemoteClients = 0x08;

    internal static NamedPipeServerStream Create(string name)
    {
        if (!OperatingSystem.IsWindows())
            return new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            );
        using var identity = WindowsIdentity.GetCurrent();
        var user =
            identity.User
            ?? throw new UnauthorizedAccessException(
                "Current-user control identity is unavailable."
            );
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(
            new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow)
        );
        security.SetOwner(user);
        var descriptor = GCHandle.Alloc(
            security.GetSecurityDescriptorBinaryForm(),
            GCHandleType.Pinned
        );
        try
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                Descriptor = descriptor.AddrOfPinnedObject(),
                Inherit = false,
            };
            // Remote rejection is a pipe-mode flag; PipeOptions.CurrentUserOnly alone sets only the ACL.
            var handle = CreateNamedPipe(
                "\\\\.\\pipe\\" + name,
                Duplex | Overlapped | FirstInstance,
                RejectRemoteClients,
                1,
                65536,
                CaptureControl.RequestLimit,
                0,
                ref attributes
            );
            if (handle.IsInvalid)
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                handle.Dispose();
                throw error;
            }
            try
            {
                return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            descriptor.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal IntPtr Descriptor;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Inherit;
    }

    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateNamedPipeW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint instances,
        uint outputSize,
        uint inputSize,
        uint timeout,
        ref SecurityAttributes attributes
    );
}

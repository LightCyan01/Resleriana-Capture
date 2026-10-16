using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;

namespace BepInEx.Unity.IL2CPP.Logging;

public class IL2CPPLogSource : ILogSource
{
    private static readonly object RegistrationGate = new();
    private static readonly IL2CPPLogCallbackDelegate Callback = IL2CPPLogCallback;
    private static IL2CPPLogSource activeSource;

    public IL2CPPLogSource()
        : this(pointer => Il2CppInterop.Runtime.IL2CPP.il2cpp_register_log_callback(pointer)) { }

    internal IL2CPPLogSource(Action<IntPtr> register)
    {
        if (register == null)
            throw new ArgumentNullException(nameof(register));
        lock (RegistrationGate)
        {
            var previous = Volatile.Read(ref activeSource);
            Volatile.Write(ref activeSource, this);
            try
            {
                register(Marshal.GetFunctionPointerForDelegate(Callback));
            }
            catch
            {
                Interlocked.CompareExchange(ref activeSource, previous, this);
                throw;
            }
        }
    }

    public string SourceName { get; } = "IL2CPP";
    public event EventHandler<LogEventArgs> LogEvent;

    public void Dispose() => Interlocked.CompareExchange(ref activeSource, null, this);

    private static void IL2CPPLogCallback(string message)
    {
        // The native callback remains registered; its delegate must live until process exit.
        var source = Volatile.Read(ref activeSource);
        source?.LogEvent?.Invoke(source, new LogEventArgs(message.Trim(), LogLevel.Message, source));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void IL2CPPLogCallbackDelegate([In] [MarshalAs(UnmanagedType.LPStr)] string message);
}

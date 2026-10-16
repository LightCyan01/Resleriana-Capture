using System;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Unity.IL2CPP.Logging;

public class IL2CPPUnityLogSource : ILogSource
{
    private readonly Action<string, string, LogType> callback;
    private readonly Application.LogCallback nativeCallback;
    private readonly object subscription = new();
    private bool disposed;

    public IL2CPPUnityLogSource()
    {
        callback = UnityLogCallback;
        nativeCallback = callback;
        try
        {
            // The threaded event also receives main-thread logs, so subscribe only once.
            Application.add_logMessageReceivedThreaded(nativeCallback);
        }
        catch (Exception registrationError)
        {
            try
            {
                Application.remove_logMessageReceivedThreaded(nativeCallback);
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Unity log callback registration and cleanup failed.",
                    registrationError,
                    cleanupError
                );
            }
            throw;
        }
    }

    public string SourceName { get; } = "Unity";

    public event EventHandler<LogEventArgs> LogEvent;

    public void Dispose()
    {
        try
        {
            lock (subscription)
            {
                if (disposed)
                    return;
                Application.remove_logMessageReceivedThreaded(nativeCallback);
                disposed = true;
            }
        }
        finally
        {
            GC.KeepAlive(callback);
            GC.KeepAlive(nativeCallback);
        }
    }

    public void UnityLogCallback(string logLine, string exception, LogType type)
    {
        var level = type switch
        {
            LogType.Error => LogLevel.Error,
            LogType.Assert => LogLevel.Error,
            LogType.Warning => LogLevel.Warning,
            LogType.Log => LogLevel.Message,
            LogType.Exception => LogLevel.Error,
            _ => LogLevel.Message,
        };
        var message = string.IsNullOrEmpty(exception) ? logLine : logLine + Environment.NewLine + exception;
        try
        {
            LogEvent?.Invoke(this, new LogEventArgs(message, level, this));
        }
        finally
        {
            GC.KeepAlive(callback);
            GC.KeepAlive(nativeCallback);
        }
    }
}

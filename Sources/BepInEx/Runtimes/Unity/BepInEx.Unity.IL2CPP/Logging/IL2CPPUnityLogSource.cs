using System;
using BepInEx.Logging;
using UnityEngine;

namespace BepInEx.Unity.IL2CPP.Logging;

public class IL2CPPUnityLogSource : ILogSource
{
    private readonly Action<string, string, LogType> callback;

    public IL2CPPUnityLogSource()
    {
        callback = UnityLogCallback;
        Application.s_LogCallbackHandler = callback;

        Il2CppInterop.Runtime.IL2CPP.ResolveICall<SetLogCallbackDefinedDelegate>(
            "UnityEngine.Application::SetLogCallbackDefined"
        )(true);
    }

    public string SourceName { get; } = "Unity";

    public event EventHandler<LogEventArgs> LogEvent;

    public void Dispose() { }

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
        LogEvent?.Invoke(this, new LogEventArgs(message, level, this));
        GC.KeepAlive(callback);
    }

    private delegate IntPtr SetLogCallbackDefinedDelegate(bool defined);
}

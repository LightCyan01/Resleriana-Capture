using System;

namespace BepInEx.Logging;

/// <summary>
///     Receives events before display listeners and records their failures without logging recursively.
/// </summary>
public interface IPriorityLogListener : ILogListener
{
    /// <summary>
    ///     Records a filter or output failure from a subsequent listener.
    /// </summary>
    /// <param name="failedListener">The listener whose filter or output failed.</param>
    /// <param name="error">The original listener exception.</param>
    /// <param name="original">The event already delivered to priority listeners.</param>
    void LogListenerFailure(ILogListener failedListener, Exception error, LogEventArgs original);
}

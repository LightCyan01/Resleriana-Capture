using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using BepInEx.Logging;

namespace AtelierCapture;

internal sealed record CaptureDiagnostic(
    string source,
    string severity,
    string message,
    string? exception_type,
    string? stack,
    string phase,
    DateTimeOffset utc,
    int thread,
    string? event_id = null,
    string? caused_by_event_id = null,
    string? failed_listener = null,
    string? warning_id = null,
    long? warning_occurrences = null,
    DateTimeOffset? warning_first_utc = null,
    DateTimeOffset? warning_last_utc = null,
    string? warning_origin = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? error_id = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        long? error_occurrences = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DateTimeOffset? error_first_utc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        DateTimeOffset? error_last_utc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? error_origin = null
);

internal sealed class OperationalStatus(Action<string>? display = null)
{
    private long unsaved;
    private long displayFailures;
    private string latest = "";
    private int displayPending;

    [ThreadStatic]
    private static string? bypassMessage;

    internal static bool IsBypassed(object? message) =>
        bypassMessage is not null && ReferenceEquals(message, bypassMessage);

    internal object Snapshot =>
        new
        {
            unsaved_operational_messages = Interlocked.Read(ref unsaved),
            operational_display_failures = Interlocked.Read(ref displayFailures),
            latest = Volatile.Read(ref latest),
        };

    internal void Report(string message)
    {
        Interlocked.Increment(ref unsaved);
        Volatile.Write(ref latest, message);
        if (display is null || Interlocked.CompareExchange(ref displayPending, 1, 0) != 0)
            return;
        try
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = bypassMessage;
                bypassMessage = Volatile.Read(ref latest);
                try
                {
                    display(bypassMessage);
                }
                catch
                {
                    Interlocked.Increment(ref displayFailures);
                }
                finally
                {
                    bypassMessage = previous;
                    Volatile.Write(ref displayPending, 0);
                }
            });
        }
        catch
        {
            Interlocked.Increment(ref displayFailures);
            Volatile.Write(ref displayPending, 0);
        }
    }
}

internal sealed class DiagnosticBridge(
    Action<CaptureDiagnostic> capture,
    OperationalStatus operational,
    Action<CaptureDiagnostic, Action>? aggregateWarning = null,
    Action<CaptureDiagnostic>? aggregateError = null
) : IPriorityLogListener
{
    private readonly WarningAggregation warnings = new();
    private readonly WarningAggregation errors = new(DiagnosticFamily.Error);
    private readonly ConditionalWeakTable<LogEventArgs, EventIdentity> identities = new();
    private long nextEvent;
    private int disposed;
    private long captureFailures;
    private long recursiveCycles;
    private long sinkFailures;
    private readonly object admission = new();
    private readonly ThreadLocal<int> handlerDepth = new();
    private int active;
    private bool stopping;
    private long stoppedUtcTicks;

    [ThreadStatic]
    private static HashSet<LogEventArgs>? publishing;
    internal string Phase { get; set; } = "startup";
    public LogLevel LogLevelFilter => LogLevel.All;
    internal object Status =>
        new
        {
            capture_failures = Interlocked.Read(ref captureFailures),
            recursive_event_cycles = Interlocked.Read(ref recursiveCycles),
            sink_failures = Interlocked.Read(ref sinkFailures),
            admission_closed_utc = Interlocked.Read(ref stoppedUtcTicks) is var ticks && ticks != 0
                ? (DateTimeOffset?)new DateTimeOffset(ticks, TimeSpan.Zero)
                : null,
            warning_aggregation = warnings.Status,
            error_aggregation = errors.Status,
            complete = Interlocked.Read(ref captureFailures) == 0
                && Interlocked.Read(ref recursiveCycles) == 0,
        };

    public void LogEvent(object sender, LogEventArgs args)
    {
        if (Volatile.Read(ref disposed) != 0 || OperationalStatus.IsBypassed(args.Data))
            return;
        if (!Enter())
            return;
        try
        {
            publishing ??= new(ReferenceEqualityComparer.Instance);
            if (!publishing.Add(args))
            {
                Interlocked.Increment(ref recursiveCycles);
                operational.Report(
                    "Capture diagnostic bridge recursion; nested diagnostic is unsaved."
                );
                return;
            }
            try
            {
                CaptureEvent(args);
            }
            finally
            {
                publishing.Remove(args);
            }
        }
        finally
        {
            Exit();
        }
    }

    private void CaptureEvent(LogEventArgs args)
    {
        try
        {
            var exception = args.Data as Exception;
            var record = new CaptureDiagnostic(
                args.Source.SourceName,
                args.Level.ToString(),
                args.Data?.ToString() ?? "",
                exception?.GetType().FullName,
                exception?.StackTrace,
                Phase,
                DateTimeOffset.UtcNow,
                Environment.CurrentManagedThreadId,
                Identity(args)
            );
            if (args.Level == LogLevel.Warning && aggregateWarning is not null)
                aggregateWarning(record, () => args.SuppressConsole = true);
            else if (
                args.Level == LogLevel.Error
                && aggregateError is not null
                && MaterialNre.IsEligible(record)
            )
                aggregateError(record);
            else
                capture(record);
        }
        catch (Exception error)
        {
            Interlocked.Increment(ref captureFailures);
            operational.Report(
                $"Capture diagnostic persistence failed: {error.GetType().Name}; diagnostic is incomplete."
            );
        }
    }

    public void LogListenerFailure(
        ILogListener failedListener,
        Exception error,
        LogEventArgs original
    )
    {
        if (Volatile.Read(ref disposed) != 0 || OperationalStatus.IsBypassed(original.Data))
            return;
        if (!Enter())
            return;
        try
        {
            CaptureListenerFailure(failedListener, error, original);
        }
        finally
        {
            Exit();
        }
    }

    private void CaptureListenerFailure(
        ILogListener failedListener,
        Exception error,
        LogEventArgs original
    )
    {
        Interlocked.Increment(ref sinkFailures);
        try
        {
            capture(
                new CaptureDiagnostic(
                    "BepInEx.ListenerFailure",
                    "Error",
                    error.ToString(),
                    error.GetType().FullName,
                    error.StackTrace,
                    Phase,
                    DateTimeOffset.UtcNow,
                    Environment.CurrentManagedThreadId,
                    NextIdentity(),
                    Identity(original),
                    failedListener.GetType().FullName
                )
            );
        }
        catch (Exception persistenceError)
        {
            Interlocked.Increment(ref captureFailures);
            operational.Report(
                $"Capture log-sink failure diagnostic is unsaved: {persistenceError.GetType().Name}; diagnostic is incomplete."
            );
        }
    }

    private bool Enter()
    {
        lock (admission)
        {
            if (stopping && handlerDepth.Value == 0)
                return false;
            active++;
            handlerDepth.Value++;
            return true;
        }
    }

    private void Exit()
    {
        lock (admission)
        {
            handlerDepth.Value--;
            active--;
            if (active == 0)
                Monitor.PulseAll(admission);
        }
    }

    internal DateTimeOffset Stop()
    {
        if (handlerDepth.Value != 0)
            throw new InvalidOperationException(
                "A diagnostic listener cannot wait for its own callback."
            );
        lock (admission)
        {
            if (!stopping)
            {
                stoppedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
                stopping = true;
            }
            while (active != 0)
                Monitor.Wait(admission);
            return new DateTimeOffset(stoppedUtcTicks, TimeSpan.Zero);
        }
    }

    private string Identity(LogEventArgs args) =>
        identities.GetValue(args, _ => new EventIdentity(NextIdentity())).Value;

    private string NextIdentity() =>
        Interlocked.Increment(ref nextEvent).ToString(CultureInfo.InvariantCulture);

    private sealed record EventIdentity(string Value);

    internal Action StageWarning(
        CaptureDiagnostic record,
        string origin,
        Action<string, object> emit,
        Action suppressConsole
    ) => warnings.Stage(record, origin, emit, suppressConsole);

    internal CaptureWarningSummary[] WarningSummaries() => warnings.Summaries();

    internal Action StageError(
        CaptureDiagnostic record,
        string origin,
        Action<string, object> emit
    ) => errors.Stage(record, origin, emit, static () => { });

    internal CaptureErrorSummary[] ErrorSummaries() => errors.ErrorSummaries();

    public void Dispose()
    {
        Stop();
        Interlocked.Exchange(ref disposed, 1);
    }
}

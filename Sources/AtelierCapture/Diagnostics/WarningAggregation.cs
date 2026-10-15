namespace AtelierCapture;

internal sealed record CaptureWarningRepeat(
    string warning_id,
    string origin,
    long delta,
    long occurrences,
    DateTimeOffset first_utc,
    DateTimeOffset last_utc,
    DateTimeOffset utc,
    string? event_id,
    int thread,
    string phase
);

internal sealed record CaptureWarningSummary(
    string warning_id,
    string origin,
    long occurrences,
    DateTimeOffset first_utc,
    DateTimeOffset last_utc
);

internal sealed record CaptureErrorRepeat(
    string error_id,
    string origin,
    long occurrences,
    DateTimeOffset utc,
    string? event_id,
    int thread,
    string phase
);

internal sealed record CaptureErrorSummary(
    string error_id,
    string origin,
    long occurrences,
    DateTimeOffset first_utc,
    DateTimeOffset last_utc
);

internal enum DiagnosticFamily
{
    Warning,
    Error,
}

internal sealed class WarningAggregation(DiagnosticFamily family = DiagnosticFamily.Warning)
{
    private readonly object gate = new();
    private readonly Dictionary<Key, State> warnings = new();
    private long staged;
    private long durable;
    private long durablePatterns;
    private WarningStatus status = new(0, 0, 0, 0, 0, true);

    internal object Status => Volatile.Read(ref status);

    internal Action Stage(
        CaptureDiagnostic record,
        string origin,
        Action<string, object> emit,
        Action suppressConsole
    )
    {
        if (
            (
                family == DiagnosticFamily.Warning
                    ? record.severity != "Warning"
                    : !MaterialNre.IsEligible(record)
            ) || origin is not ("Official" or "Private" or "Unknown")
        )
            throw new InvalidDataException(
                "Diagnostic aggregation requires its exact family and origin."
            );
        lock (gate)
        {
            var key = new Key(
                origin,
                record.source,
                record.severity,
                record.message,
                record.exception_type,
                record.stack
            );
            var repeated = warnings.TryGetValue(key, out var state);
            state ??= new State(
                record.event_id
                    ?? throw new InvalidDataException("Diagnostic has no event identity."),
                origin,
                record.utc
            );
            var stagedTotal = checked(staged + 1);
            var count = checked(state.Staged + 1);
            var first = record.utc < state.First ? record.utc : state.First;
            var last = record.utc > state.Last ? record.utc : state.Last;
            if (repeated)
                emit(
                    family == DiagnosticFamily.Warning
                        ? "runtime_warning_repeat"
                        : "runtime_error_repeat",
                    family == DiagnosticFamily.Warning
                        ? new CaptureWarningRepeat(
                            state.Id,
                            origin,
                            1,
                            count,
                            first,
                            last,
                            record.utc,
                            record.event_id,
                            record.thread,
                            record.phase
                        )
                        : new CaptureErrorRepeat(
                            state.Id,
                            origin,
                            count,
                            record.utc,
                            record.event_id,
                            record.thread,
                            record.phase
                        )
                );
            else
                emit(
                    "runtime_log",
                    family == DiagnosticFamily.Warning
                        ? record with
                        {
                            warning_id = state.Id,
                            warning_occurrences = 1,
                            warning_first_utc = first,
                            warning_last_utc = last,
                            warning_origin = origin,
                        }
                        : record with
                        {
                            error_id = state.Id,
                            error_occurrences = 1,
                            error_first_utc = first,
                            error_last_utc = last,
                            error_origin = origin,
                        }
                );
            if (!repeated)
                warnings.Add(key, state);
            state.Staged = count;
            state.First = first;
            state.Last = last;
            staged = stagedTotal;
            PublishStatus();
            return () =>
            {
                lock (gate)
                {
                    if (state.Durable < count)
                    {
                        durable = checked(durable + count - state.Durable);
                        if (state.Durable == 0)
                            durablePatterns = checked(durablePatterns + 1);
                        state.Durable = count;
                        state.DurableFirst = first;
                        state.DurableLast = last;
                        PublishStatus();
                    }
                }
                if (repeated && family == DiagnosticFamily.Warning)
                    suppressConsole();
            };
        }
    }

    internal CaptureWarningSummary[] Summaries() =>
        Summaries(state => new CaptureWarningSummary(
            state.Id,
            state.Origin,
            state.Durable,
            state.DurableFirst,
            state.DurableLast
        ));

    internal CaptureErrorSummary[] ErrorSummaries() =>
        Summaries(state => new CaptureErrorSummary(
            state.Id,
            state.Origin,
            state.Durable,
            state.DurableFirst,
            state.DurableLast
        ));

    private T[] Summaries<T>(Func<State, T> project)
    {
        lock (gate)
            return warnings
                .Values.Where(state => state.Durable != 0)
                .OrderBy(state => state.Id, StringComparer.Ordinal)
                .Select(project)
                .ToArray();
    }

    private void PublishStatus() =>
        Volatile.Write(
            ref status,
            new(
                warnings.Count,
                staged,
                durable,
                durable - durablePatterns,
                staged - durable,
                staged == durable
            )
        );

    private sealed record Key(
        string Origin,
        string Source,
        string Severity,
        string Message,
        string? Exception,
        string? Stack
    );

    private sealed class State(string id, string origin, DateTimeOffset utc)
    {
        internal string Id { get; } = id;
        internal string Origin { get; } = origin;
        internal long Staged;
        internal long Durable;
        internal DateTimeOffset First = utc;
        internal DateTimeOffset Last = utc;
        internal DateTimeOffset DurableFirst;
        internal DateTimeOffset DurableLast;
    }

    private sealed record WarningStatus(
        int patterns_started,
        long occurrences_staged,
        long occurrences_durable,
        long repeated_occurrences_durable,
        long pending_occurrences,
        bool complete
    );
}

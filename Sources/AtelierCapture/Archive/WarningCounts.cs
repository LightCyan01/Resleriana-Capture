using System.Globalization;

namespace AtelierCapture.Archive;

internal sealed class WarningCounts(SortedIds identities, bool errorFamily = false)
{
    private static readonly string[] WarningMetadata =
    [
        "warning_id",
        "warning_occurrences",
        "warning_first_utc",
        "warning_last_utc",
        "warning_origin",
    ];
    private readonly string label = errorFamily ? "error" : "warning";
    private readonly string prefix = errorFamily ? "error_" : "warning_";
    private long anchors,
        repeats,
        summaries,
        occurrences;
    private long? first,
        last;
    private long groupOccurrences,
        groupFirst,
        groupLast;
    private bool groupSummary;
    internal bool HasRecords => anchors != 0 || repeats != 0 || summaries != 0;

    internal void Add(RecordProjection record)
    {
        var kind = record.Get("kind");
        if (
            kind != "runtime_log"
            && kind != "runtime_" + label + "_repeat"
            && kind != "runtime_" + label + "_summary"
        )
            return;
        var payload = record.Payload ?? record;
        var id = payload.Get(prefix + "id");
        if (kind == "runtime_log" && id is null)
        {
            if (
                errorFamily
                && (
                    payload.DataKinds.ContainsKey("error_id")
                    || payload.DataKinds.ContainsKey("error_occurrences")
                    || payload.DataKinds.ContainsKey("error_first_utc")
                    || payload.DataKinds.ContainsKey("error_last_utc")
                    || payload.DataKinds.ContainsKey("error_origin")
                )
            )
                throw new InvalidDataException("Capture error anchor metadata has no identity.");
            return;
        }
        RecordProjection.Integer(id, true);
        if (errorFamily && record.Get("stream") != "diagnostics")
            throw new InvalidDataException("Capture error aggregation is outside diagnostics.");
        if (
            payload.DataKinds.GetValueOrDefault(prefix + "id")
            != System.Text.Json.JsonValueKind.String
        )
            throw new InvalidDataException($"Capture {label} ID is not a decimal string.");
        var anchor = kind == "runtime_log";
        if (
            anchor
            && (
                payload.Get("severity") != (errorFamily ? "Error" : "Warning")
                || errorFamily && !payload.IsEligibleMaterialError
            )
        )
            throw new InvalidDataException($"Capture {label} anchor is not eligible.");
        if (errorFamily)
        {
            foreach (var field in WarningMetadata)
                if (
                    payload.DataKinds.TryGetValue(field, out var type)
                    && type != System.Text.Json.JsonValueKind.Null
                )
                    throw new InvalidDataException(
                        "Capture diagnostic anchor mixes warning/error identities."
                    );
        }
        else if (payload.Get("error_id") is not null)
            throw new InvalidDataException(
                "Capture diagnostic anchor mixes warning/error identities."
            );
        var origin = payload.Get(anchor ? prefix + "origin" : "origin");
        if (origin is not ("Unknown" or "Official" or "Private"))
            throw new InvalidDataException($"Capture {label} origin is invalid.");
        if (
            origin is "Official" or "Private" && record.Get("environment") != origin
            || origin == "Unknown"
                && record.Get("environment") is "Official" or "Private"
                && record.Get("original_environment") != "Unknown"
        )
            throw new InvalidDataException(
                $"Capture {label} origin differs from its recorded scope."
            );
        var count = RecordProjection.Integer(
            payload.Get(anchor ? prefix + "occurrences" : "occurrences"),
            true
        );
        if (errorFamily && kind != "runtime_error_summary")
        {
            var eventId = payload.Data.GetValueOrDefault("event_id");
            RecordProjection.Integer(eventId, true);
            RecordProjection.Integer(payload.Data.GetValueOrDefault("thread"), true);
            if (
                payload.DataKinds.GetValueOrDefault("event_id")
                    != System.Text.Json.JsonValueKind.String
                || payload.DataKinds.GetValueOrDefault("thread")
                    != System.Text.Json.JsonValueKind.Number
                || payload.DataKinds.GetValueOrDefault("phase")
                    != System.Text.Json.JsonValueKind.String
                || string.IsNullOrEmpty(payload.Data.GetValueOrDefault("phase"))
                || anchor && eventId != id
            )
                throw new InvalidDataException("Capture error occurrence metadata is invalid.");
        }
        var compactErrorRepeat = errorFamily && kind == "runtime_error_repeat";
        if (
            compactErrorRepeat
            && (
                payload.DataKinds.ContainsKey("delta")
                || payload.DataKinds.ContainsKey("first_utc")
                || payload.DataKinds.ContainsKey("last_utc")
            )
        )
            throw new InvalidDataException("Capture error repeat includes removed derived fields.");
        var firstUtc = compactErrorRepeat
            ? 0
            : Time(payload.Get(anchor ? prefix + "first_utc" : "first_utc"));
        var lastUtc = compactErrorRepeat
            ? 0
            : Time(payload.Get(anchor ? prefix + "last_utc" : "last_utc"));
        var occurrenceUtc =
            kind == "runtime_" + label + "_summary"
                ? 0
                : Time(payload.Data.GetValueOrDefault("utc"));
        if (
            lastUtc < firstUtc
            || anchor && (count != 1 || firstUtc != occurrenceUtc || lastUtc != occurrenceUtc)
        )
            throw new InvalidDataException($"Capture {label} occurrence/time range is invalid.");
        byte phase;
        if (anchor)
        {
            phase = 0;
            anchors = checked(anchors + 1);
        }
        else if (kind == "runtime_" + label + "_repeat")
        {
            if (
                count < 2
                || !compactErrorRepeat && RecordProjection.Integer(payload.Get("delta"), true) != 1
            )
                throw new InvalidDataException($"Capture {label} repeat delta is invalid.");
            phase = 1;
            repeats = checked(repeats + 1);
        }
        else
        {
            phase = 2;
            summaries = checked(summaries + 1);
        }
        identities.Add(
            new IdRow(
                errorFamily ? (byte)7 : (byte)6,
                origin + ":" + id!.PadLeft(19, '0'),
                phase,
                count.ToString("D19", CultureInfo.InvariantCulture)
                    + "\n"
                    + firstUtc.ToString("D19", CultureInfo.InvariantCulture)
                    + "\n"
                    + lastUtc.ToString("D19", CultureInfo.InvariantCulture)
                    + "\n"
                    + occurrenceUtc.ToString("D19", CultureInfo.InvariantCulture)
            )
        );
    }

    internal void Read(IdRow row)
    {
        var values = row.Signature.Split('\n');
        var count = long.Parse(values[0], CultureInfo.InvariantCulture);
        var start = long.Parse(values[1], CultureInfo.InvariantCulture);
        var end = long.Parse(values[2], CultureInfo.InvariantCulture);
        var occurrenceUtc = long.Parse(values[3], CultureInfo.InvariantCulture);
        if (row.Phase == 0)
        {
            if (groupOccurrences != 0)
                throw new InvalidDataException($"Capture {label} has duplicate anchors.");
            groupOccurrences = count;
            groupFirst = start;
            groupLast = end;
        }
        else
        {
            if (groupOccurrences == 0 || groupSummary)
                throw new InvalidDataException(
                    $"Capture {label} reference/times do not match its anchor."
                );
            if (row.Phase == 1)
            {
                if (count != checked(groupOccurrences + 1))
                    throw new InvalidDataException(
                        $"Capture {label} repeat count is duplicated or missing."
                    );
                var expectedFirst = Math.Min(groupFirst, occurrenceUtc);
                var expectedLast = Math.Max(groupLast, occurrenceUtc);
                if (!errorFamily && (start != expectedFirst || end != expectedLast))
                    throw new InvalidDataException(
                        $"Capture {label} cumulative times differ from saved occurrences."
                    );
                groupOccurrences = count;
                groupFirst = expectedFirst;
                groupLast = expectedLast;
            }
            else if (row.Phase == 2)
            {
                if (count != groupOccurrences || start != groupFirst || end != groupLast)
                    throw new InvalidDataException(
                        $"Capture {label} summary differs from saved occurrences."
                    );
                groupSummary = true;
            }
            else
                throw new InvalidDataException($"Capture {label} phase is invalid.");
        }
    }

    internal void FinishGroup()
    {
        occurrences = checked(occurrences + groupOccurrences);
        first = first is null ? groupFirst : Math.Min(first.Value, groupFirst);
        last = last is null ? groupLast : Math.Max(last.Value, groupLast);
        groupOccurrences = 0;
        groupFirst = groupLast = 0;
        groupSummary = false;
    }

    internal object Summary()
    {
        var summary = new
        {
            anchor_records = ArchiveCounts.Text(anchors),
            repeat_records = ArchiveCounts.Text(repeats),
            summary_records = ArchiveCounts.Text(summaries),
            occurrences = ArchiveCounts.Text(occurrences),
            first_utc = first is { } firstTicks
                ? new DateTimeOffset(firstTicks, TimeSpan.Zero).ToString("O")
                : null,
            last_utc = last is { } lastTicks
                ? new DateTimeOffset(lastTicks, TimeSpan.Zero).ToString("O")
                : null,
            basis = errorFamily
                ? "saved_anchors_and_repeat_ordinals"
                : "saved_anchors_and_repeat_deltas",
            missing_final_summaries = ArchiveCounts.Text(checked(anchors - summaries)),
        };
        if (!errorFamily)
            return summary;
        return new
        {
            scope = "exact_known_unity_material_nre",
            summary.anchor_records,
            summary.repeat_records,
            summary.summary_records,
            summary.occurrences,
            summary.first_utc,
            summary.last_utc,
            summary.basis,
            summary.missing_final_summaries,
        };
    }

    private long Time(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var time
        )
            ? time.UtcTicks
            : throw new InvalidDataException($"Capture {label} timestamp is invalid.");
}

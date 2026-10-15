using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtelierCapture.Archive;

internal sealed class RecordProjection
{
    internal readonly Dictionary<string, string?> Root = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, string?> Data = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonValueKind> RootKinds = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, JsonValueKind> DataKinds = new(StringComparer.Ordinal);
    internal JsonElement? Metadata,
        Control;
    internal long? BodyBytes;
    internal string? BodyHash;
    internal bool HasOmission;
    internal bool MaterialErrorMessage;
    internal RecordProjection? Payload,
        BootstrapSource,
        BootstrapOriginal;

    internal string? Get(string name) =>
        Root.GetValueOrDefault(name) ?? Data.GetValueOrDefault(name);

    internal bool IsEligibleMaterialError =>
        MaterialErrorMessage
        && Get("source") == "Unity"
        && Get("severity") == "Error"
        && DataKinds.GetValueOrDefault("exception_type") == JsonValueKind.Null
        && DataKinds.GetValueOrDefault("stack") == JsonValueKind.Null
        && DataKinds.GetValueOrDefault("caused_by_event_id") == JsonValueKind.Null
        && DataKinds.GetValueOrDefault("failed_listener") == JsonValueKind.Null;

    internal static long Integer(string? value, bool positive = false)
    {
        if (
            value is null
            || value.Length == 0
            || (value.Length > 1 && value[0] == '0')
            || value.Any(c => c is < '0' or > '9')
            || !long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result
            )
            || (positive && result == 0)
        )
            throw new InvalidDataException("Capture integer is not a canonical nonnegative Int64.");
        return result;
    }
}

// The syntax walker retains control metadata, not arbitrary bodies or native graphs.
internal sealed class RecordReader : IDisposable
{
    private const int ControlLimit = 1024 * 1024;
    private static readonly HashSet<string> RootFields = new(StringComparer.Ordinal)
    {
        "schema",
        "run_id",
        "sequence",
        "utc",
        "elapsed_ticks",
        "thread",
        "environment",
        "stream",
        "kind",
        "system_id",
        "events",
        "shutdown_complete",
        "completed_batches",
        "stopwatch_frequency",
        "contains_sensitive_raw_data",
        "dropped_events",
        "reason",
    };
    private static readonly HashSet<string> DataFields = new(StringComparer.Ordinal)
    {
        "request_id",
        "attempt_id",
        "operation_id",
        "identity_proof",
        "invocation_id",
        "parent_invocation_id",
        "boundary_id",
        "variant_id",
        "system_id",
        "route",
        "direction",
        "stage",
        "body_id",
        "bytes",
        "sha256",
        "omitted",
        "status",
        "complete",
        "phase",
        "error",
        "state",
        "retries",
        "response_recorded",
        "capture_status",
        "correlation_gap",
        "reason",
        "compatible",
        "dropped_events",
        "observer",
        "scope",
        "original_environment",
        "run_id",
        "environment",
        "checkpoint_sha256",
        "diagnostics_sha256",
        "records",
        "source",
        "original_record",
        "warning_id",
        "warning_occurrences",
        "warning_first_utc",
        "warning_last_utc",
        "warning_origin",
        "error_id",
        "error_occurrences",
        "error_first_utc",
        "error_last_utc",
        "error_origin",
        "origin",
        "delta",
        "occurrences",
        "first_utc",
        "last_utc",
        "utc",
        "event_id",
        "thread",
        "severity",
    };
    private static readonly HashSet<string> ErrorEligibilityFields = new(StringComparer.Ordinal)
    {
        "message",
        "exception_type",
        "stack",
        "caused_by_event_id",
        "failed_listener",
    };
    private readonly StreamReader reader;
    private StringBuilder? capture;
    private bool captureOverflow;

    internal RecordReader(Stream stream)
    {
        reader = new StreamReader(stream, new UTF8Encoding(false, true), false, 65536, true);
    }

    internal RecordProjection? Read()
    {
        if (reader.Peek() == -1)
            return null;
        var record = new RecordProjection();
        var frames = new List<Frame>();
        StringBuilder? metadata = null,
            control = null;
        var metadataOverflow = false;
        var controlOverflow = false;
        Space(false);
        if (Take() != '{')
            throw new InvalidDataException("Capture JSONL record is not an object.");
        frames.Add(new Frame(true, 0, record));
        while (frames.Count != 0)
        {
            var frame = frames[^1];
            var projection = frame.Projection;
            Space(true);
            if (frame.Object && frame.State is 0 or 4)
            {
                if (reader.Peek() == '}' && frame.State == 0)
                {
                    Take();
                    Finish();
                    continue;
                }
                if (reader.Peek() != '"')
                    throw new InvalidDataException("Capture JSON object key is invalid.");
                frame.Key = String(true, null, true);
                if (frame.Key == "omitted")
                    record.HasOmission = true;
                var significant =
                    frame.Level == 0
                        ? RootFields.Contains(frame.Key ?? "") || frame.Key is "metadata" or "data"
                        : frame.Level == 1
                            && (
                                DataFields.Contains(frame.Key ?? "")
                                || ErrorEligibilityFields.Contains(frame.Key ?? "")
                                || frame.Key == "body"
                            );
                if (significant && !frame.Seen.Add(frame.Key!))
                    throw new InvalidDataException("Capture identity/control field is duplicated.");
                frame.State = 1;
                continue;
            }
            if (frame.Object && frame.State == 1)
            {
                if (Take() != ':')
                    throw new InvalidDataException("Capture JSON object has no colon.");
                frame.State = 2;
                continue;
            }
            if (frame.State == 3)
            {
                var separator = Take();
                if (separator == (frame.Object ? '}' : ']'))
                {
                    Finish();
                    continue;
                }
                if (separator != ',')
                    throw new InvalidDataException("Capture JSON separator is invalid.");
                frame.State = frame.Object ? 4 : 5;
                continue;
            }
            if (!frame.Object && frame.State == 0 && reader.Peek() == ']')
            {
                Take();
                Finish();
                continue;
            }
            var key = frame.Key;
            var selected =
                frame.Level == 0
                    ? RootFields.Contains(key ?? "")
                    : frame.Level == 1 && DataFields.Contains(key ?? "");
            var captureValue =
                frame.Level == 0
                && ReferenceEquals(projection, record)
                && (
                    key == "metadata"
                    || (
                        key == "data"
                        && record.Root.GetValueOrDefault("kind") is null or "capture_status"
                    )
                );
            if (captureValue)
            {
                capture = new StringBuilder();
                captureOverflow = false;
                frame.Capture = key;
            }
            var next = reader.Peek();
            if (next is '{' or '[')
            {
                if (
                    frame.Level == 1
                    && key
                        is "delta"
                            or "first_utc"
                            or "last_utc"
                            or "error_id"
                            or "error_occurrences"
                            or "error_first_utc"
                            or "error_last_utc"
                            or "error_origin"
                            or "warning_id"
                            or "warning_occurrences"
                            or "warning_first_utc"
                            or "warning_last_utc"
                            or "warning_origin"
                )
                    projection.DataKinds.Add(
                        key!,
                        next == '{' ? JsonValueKind.Object : JsonValueKind.Array
                    );
                Take();
                frame.State = 3;
                var childLevel = frame.Level == 0 && key == "data" ? 1 : -1;
                var childProjection = projection;
                if (next == '{' && frame.Level == 1)
                {
                    if (key == "source" && projection.Get("kind") == "process_bootstrap")
                    {
                        childProjection = projection.BootstrapSource ??= new RecordProjection();
                        childLevel = 1;
                    }
                    else if (
                        key == "original_record"
                        && projection.Get("kind") == "process_bootstrap"
                    )
                    {
                        childProjection = projection.BootstrapOriginal ??= new RecordProjection();
                        childLevel = 0;
                    }
                    else if (
                        key == "data"
                        && projection.Get("kind")
                            is null
                                or "runtime_log"
                                or "runtime_warning_repeat"
                                or "runtime_warning_summary"
                                or "runtime_error_repeat"
                                or "runtime_error_summary"
                                or "unbound_combat_enter"
                                or "unbound_combat_exit"
                                or "unbound_combat_exception"
                                or "unbound_native_enter"
                                or "unbound_native_exit"
                                or "unbound_native_exception"
                    )
                    {
                        childProjection = projection.Payload ??= new RecordProjection();
                        childProjection.Root["kind"] = projection.Get("kind");
                        childLevel = 1;
                    }
                }
                frames.Add(new Frame(next == '{', childLevel, childProjection));
                continue;
            }
            string? scalar;
            JsonValueKind scalarKind;
            if (next == '"')
            {
                scalarKind = JsonValueKind.String;
                using var body = frame.Level == 1 && key == "body" ? new BodyDigest() : null;
                var message =
                    frame.Level == 1 && key == "message" ? new MaterialMessageMatch() : null;
                scalar = String(selected, body, match: message);
                if (message is not null)
                    projection.MaterialErrorMessage = message.Complete;
                if (body is not null)
                {
                    (projection.BodyBytes, projection.BodyHash) = body.Finish();
                }
            }
            else if (next is 't' or 'f' or 'n')
            {
                scalarKind =
                    next == 't' ? JsonValueKind.True
                    : next == 'f' ? JsonValueKind.False
                    : JsonValueKind.Null;
                var literal =
                    next == 't' ? "true"
                    : next == 'f' ? "false"
                    : "null";
                foreach (var character in literal)
                    if (Take() != character)
                        throw new InvalidDataException("Capture JSON literal is invalid.");
                scalar = next == 'n' ? null : literal;
            }
            else
            {
                scalarKind = JsonValueKind.Number;
                scalar = Number(selected);
            }
            if (selected)
            {
                var target = frame.Level == 0 ? projection.Root : projection.Data;
                if (!target.TryAdd(key!, scalar))
                    throw new InvalidDataException("Capture identity field is duplicated.");
                (frame.Level == 0 ? projection.RootKinds : projection.DataKinds).Add(
                    key!,
                    scalarKind
                );
            }
            else if (frame.Level == 1 && ErrorEligibilityFields.Contains(key ?? ""))
                projection.DataKinds.Add(key!, scalarKind);
            frame.State = 3;
            EndCapture(frame);
        }
        Space(false);
        if (Take() != '\n')
            throw new InvalidDataException("Capture JSONL has a blank line or incomplete record.");
        if (metadataOverflow)
            throw new InvalidDataException("Capture producer metadata exceeds its control bound.");
        if (metadata is not null)
            record.Metadata = ParseControl(metadata.ToString());
        if (record.Get("kind") == "capture_status")
        {
            if (controlOverflow || control is null)
                throw new InvalidDataException("Capture status exceeds its control bound.");
            record.Control = ParseControl(control.ToString());
        }
        return record;

        void Finish()
        {
            var frame = frames[^1];
            if (frame.Level == 0 && frame.Projection.Payload is { } payload)
                payload.Root["kind"] = frame.Projection.Get("kind");
            frames.RemoveAt(frames.Count - 1);
            if (frames.Count != 0)
                EndCapture(frames[^1]);
        }
        void EndCapture(Frame frame)
        {
            if (frame.Capture is null)
                return;
            if (frame.Capture == "metadata")
            {
                metadata = capture;
                metadataOverflow = captureOverflow;
            }
            else
            {
                control = capture;
                controlOverflow = captureOverflow;
            }
            capture = null;
            frame.Capture = null;
        }
    }

    private static JsonElement ParseControl(string value)
    {
        using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 256 });
        return document.RootElement.Clone();
    }

    private int Take()
    {
        var value = reader.Read();
        if (capture is not null && value != -1)
        {
            if (capture.Length < ControlLimit)
                capture.Append((char)value);
            else
                captureOverflow = true;
        }
        return value;
    }

    private void Space(bool inside)
    {
        while (reader.Peek() is ' ' or '\t' or '\r' || (inside && reader.Peek() == '\n'))
            Take();
    }

    private string? String(
        bool retain,
        BodyDigest? body,
        bool key = false,
        MaterialMessageMatch? match = null
    )
    {
        if (Take() != '"')
            throw new InvalidDataException("Capture JSON string is invalid.");
        var value = retain ? new StringBuilder() : null;
        while (true)
        {
            var character = Take();
            if (character == '"')
                return value?.ToString();
            if (character < 32)
                throw new InvalidDataException(
                    "Capture JSON string is unfinished or contains a control byte."
                );
            if (character == '\\')
            {
                character = Take();
                character = character switch
                {
                    '"' or '\\' or '/' => character,
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => HexCharacter(),
                    _ => throw new InvalidDataException("Capture JSON escape is invalid."),
                };
                if (character is >= 0xD800 and <= 0xDBFF)
                {
                    if (Take() != '\\' || Take() != 'u')
                        throw new InvalidDataException("Capture JSON surrogate is incomplete.");
                    var low = HexCharacter();
                    if (low is < 0xDC00 or > 0xDFFF)
                        throw new InvalidDataException("Capture JSON surrogate pair is invalid.");
                    Add(character);
                    Add(low);
                    continue;
                }
                if (character is >= 0xDC00 and <= 0xDFFF)
                    throw new InvalidDataException("Capture JSON surrogate is unpaired.");
            }
            Add(character);
        }
        void Add(int character)
        {
            body?.Add((char)character);
            match?.Add((char)character);
            if (value is null)
                return;
            if (key && value.Length >= 128)
                return;
            if (value.Length >= 8192)
                throw new InvalidDataException("Capture identity exceeds its control bound.");
            value.Append((char)character);
        }
    }

    private int HexCharacter()
    {
        var result = 0;
        for (var i = 0; i < 4; i++)
        {
            var c = Take();
            var digit =
                c is >= '0' and <= '9' ? c - '0'
                : c is >= 'a' and <= 'f' ? c - 'a' + 10
                : c is >= 'A' and <= 'F' ? c - 'A' + 10
                : -1;
            if (digit < 0)
                throw new InvalidDataException("Capture JSON Unicode escape is invalid.");
            result = (result << 4) | digit;
        }
        return result;
    }

    private string? Number(bool retain)
    {
        var value = retain ? new StringBuilder() : null;
        if (reader.Peek() == '-')
            Digit();
        if (reader.Peek() == '0')
            Digit();
        else
        {
            if (reader.Peek() is < '1' or > '9')
                throw new InvalidDataException("Capture JSON value is invalid.");
            while (reader.Peek() is >= '0' and <= '9')
                Digit();
        }
        if (reader.Peek() == '.')
        {
            Digit();
            if (reader.Peek() is < '0' or > '9')
                throw new InvalidDataException("Capture JSON fraction is invalid.");
            while (reader.Peek() is >= '0' and <= '9')
                Digit();
        }
        if (reader.Peek() is 'e' or 'E')
        {
            Digit();
            if (reader.Peek() is '+' or '-')
                Digit();
            if (reader.Peek() is < '0' or > '9')
                throw new InvalidDataException("Capture JSON exponent is invalid.");
            while (reader.Peek() is >= '0' and <= '9')
                Digit();
        }
        return value?.ToString();
        void Digit()
        {
            var c = Take();
            if (value is null)
                return;
            if (value.Length > 128)
                throw new InvalidDataException("Capture control number exceeds its bound.");
            value.Append((char)c);
        }
    }

    public void Dispose() => reader.Dispose();

    private sealed class Frame(bool isObject, int level, RecordProjection projection)
    {
        internal readonly bool Object = isObject;
        internal readonly int Level = level;
        internal readonly RecordProjection Projection = projection;
        internal int State;
        internal string? Key,
            Capture;
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
    }

    private sealed class MaterialMessageMatch
    {
        private int position;
        private bool standard = true,
            lod = true;

        internal bool Complete =>
            standard && position == MaterialNre.StandardMessage.Length
            || lod && position == MaterialNre.LodMessage.Length;

        internal void Add(char value)
        {
            if (standard)
                standard =
                    position < MaterialNre.StandardMessage.Length
                    && MaterialNre.StandardMessage[position] == value;
            if (lod)
                lod =
                    position < MaterialNre.LodMessage.Length
                    && MaterialNre.LodMessage[position] == value;
            if (standard || lod)
                position++;
        }
    }

    private sealed class BodyDigest : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256
        );
        private readonly char[] group = new char[4];
        private readonly byte[] bytes = new byte[16386];
        private int count,
            decoded;
        private bool padded;
        private long length;

        internal void Add(char value)
        {
            if (
                padded
                || value
                    is not (
                        >= 'A'
                        and <= 'Z'
                        or >= 'a'
                        and <= 'z'
                        or >= '0'
                        and <= '9'
                        or '+'
                        or '/'
                        or '='
                    )
            )
                throw new InvalidDataException("Capture raw body Base64 is invalid.");
            group[count++] = value;
            if (count != 4)
                return;
            if (!Convert.TryFromBase64Chars(group, bytes.AsSpan(decoded, 3), out var written))
                throw new InvalidDataException("Capture raw body Base64 is invalid.");
            decoded += written;
            if (decoded >= 16383)
            {
                hash.AppendData(bytes.AsSpan(0, decoded));
                decoded = 0;
            }
            length = checked(length + written);
            padded = group.Contains('=');
            count = 0;
        }

        internal (long, string) Finish()
        {
            if (count != 0)
                throw new InvalidDataException("Capture raw body Base64 is unfinished.");
            hash.AppendData(bytes.AsSpan(0, decoded));
            return (length, Convert.ToHexString(hash.GetHashAndReset()));
        }

        public void Dispose() => hash.Dispose();
    }
}

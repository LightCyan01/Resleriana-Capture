using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AtelierCapture;

internal static class CaptureArchive
{
    private static readonly string[] Names = { "api.jsonl.gz", "combat.jsonl.gz", "raw-bodies.jsonl.gz", "diagnostics.jsonl.gz" };
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool Pack(string folder, bool recovered)
    {
        var destination = Path.ChangeExtension(folder, ".zip");
        if (File.Exists(destination) || !Names.All(name => File.Exists(Path.Combine(folder, name)))) return false;
        var checkpoints = ReadCheckpoints(folder);
        var summary = ReadDiagnostics(Path.Combine(folder, Names[3]), checkpoints?.GetValueOrDefault(Names[3]));
        var start = summary.Start;
        var schema = start.GetProperty("schema").GetInt32();
        if (schema >= 4 && checkpoints is null) throw new InvalidDataException("Capture checkpoint is missing.");
        var uncertain = checkpoints is null;
        var discarded = checkpoints?.Sum(pair => checked(new FileInfo(Path.Combine(folder, pair.Key)).Length - pair.Value.Bytes)) ?? 0;
        recovered |= uncertain || discarded != 0;
        var entries = new List<object>();
        var temporary = destination + ".partial";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var buffer = new byte[65536];
            foreach (var name in Names)
            {
                var entryName = Path.GetFileNameWithoutExtension(name);
                using var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal).Open();
                using var input = new CheckedInput(Path.Combine(folder, name), checkpoints?.GetValueOrDefault(name));
                using var gzip = new GZipStream(input, CompressionMode.Decompress, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes = 0, records = 0;
                var last = -1;
                int count;
                while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0)
                {
                    Write(buffer.AsSpan(0, count));
                    last = buffer[count - 1];
                }
                input.Verify();
                if (bytes != 0 && last != '\n') throw new InvalidDataException("Capture stream ends inside a record.");
                if (recovered && name == Names[3])
                    Write(Utf8.GetBytes(JsonSerializer.Serialize(new
                    {
                        kind = "session_recovered", schema, run_id = start.GetProperty("run_id").GetString(),
                        utc = DateTimeOffset.UtcNow, shutdown_complete = false,
                        recovery_uncertain = uncertain, discarded_compressed_bytes = discarded
                    }) + "\n"));
                entries.Add(new { name = entryName, sha256 = Convert.ToHexString(hash.GetHashAndReset()), bytes, records });

                void Write(ReadOnlySpan<byte> data)
                {
                    entry.Write(data);
                    hash.AppendData(data);
                    bytes = checked(bytes + data.Length);
                    int newline;
                    while ((newline = data.IndexOf((byte)'\n')) >= 0)
                    {
                        records++;
                        data = data[(newline + 1)..];
                    }
                }
            }
            using var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            JsonSerializer.Serialize(manifest, new
            {
                format = "resleriana-capture", manifest_version = 1, schema,
                run_id = start.GetProperty("run_id").GetString(), environment = start.GetProperty("environment").GetString(),
                started_utc = start.GetProperty("utc"), ended_utc = summary.End?.GetProperty("utc"),
                stopwatch_frequency = start.TryGetProperty("stopwatch_frequency", out var frequency) ? (JsonElement?)frequency : null,
                contains_sensitive_raw_data = start.TryGetProperty("contains_sensitive_raw_data", out var sensitive) ? (bool?)sensitive.GetBoolean() : null,
                metadata = start.GetProperty("metadata"),
                recovered, shutdown_complete = !recovered && summary.End is not null,
                checkpoint_verified = checkpoints is not null, recovery_uncertain = uncertain,
                discarded_compressed_bytes = discarded, dropped_events = summary.Dropped,
                observer_errors = summary.End is not null && summary.FinalErrors is not null ? summary.FinalErrors : summary.Errors,
                observer_error_samples = summary.ErrorSamples,
                observer_errors_truncated = (summary.End is null || summary.FinalErrors is null) && summary.ErrorsTruncated,
                observer_error_samples_truncated = summary.ErrorsTruncated,
                capture_status = summary.Status, capture_status_truncated = summary.StatusTruncated,
                integrity = "sha256", authenticity = "unverified", entries
            });
        }
        File.Move(temporary, destination);
        return true;
    }

    private static Dictionary<string, Checkpoint>? ReadCheckpoints(string folder)
    {
        var path = Path.Combine(folder, "session-state.json");
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Capture checkpoint is too large.");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.GetProperty("schema").GetInt32() != 1) throw new InvalidDataException("Unknown capture checkpoint.");
        var result = new Dictionary<string, Checkpoint>(StringComparer.Ordinal);
        foreach (var value in document.RootElement.GetProperty("files").EnumerateArray())
        {
            var name = value.GetProperty("name").GetString()!;
            var bytes = value.GetProperty("bytes").GetInt64();
            var hash = value.GetProperty("sha256").GetString()!;
            if (!Names.Contains(name) || bytes < 0 || bytes > new FileInfo(Path.Combine(folder, name)).Length
                || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || !result.TryAdd(name, new Checkpoint(bytes, hash)))
                throw new InvalidDataException("Invalid capture checkpoint.");
        }
        if (result.Count != Names.Length) throw new InvalidDataException("Capture checkpoint omits a stream.");
        return result;
    }

    private static Summary ReadDiagnostics(string path, Checkpoint? checkpoint)
    {
        using var input = new CheckedInput(path, checkpoint);
        using var gzip = new GZipStream(input, CompressionMode.Decompress, true);
        using var reader = new StreamReader(gzip, Utf8, false, 65536, true);
        var result = new Summary();
        var line = new StringBuilder();
        int character;
        while ((character = reader.Read()) != -1)
        {
            if (character != '\n')
            {
                if (line.Length >= 262144) throw new InvalidDataException("Capture diagnostic record is too large.");
                line.Append((char)character);
                continue;
            }
            using var document = JsonDocument.Parse(line.ToString(), new JsonDocumentOptions { MaxDepth = 32 });
            line.Clear();
            var record = document.RootElement;
            var data = record.TryGetProperty("data", out var nested) ? nested : record;
            switch (record.GetProperty("kind").GetString())
            {
                case "session_start":
                    if (result.HasStart) throw new InvalidDataException("Capture has multiple session starts.");
                    result.Start = record.Clone();
                    result.HasStart = true;
                    break;
                case "session_end":
                    if (result.End is not null) throw new InvalidDataException("Capture has multiple session ends.");
                    result.End = record.Clone();
                    break;
                case "capture_gap":
                    var dropped = data.GetProperty("dropped_events").GetInt64();
                    if (dropped < 0) throw new InvalidDataException("Invalid dropped event count.");
                    result.Dropped = checked(result.Dropped + dropped);
                    break;
                case "observer_error":
                    var observer = ReadText(data, "observer") ?? ReadText(data, "kind") ?? "unknown";
                    var error = ReadText(data, "error") ?? "unknown";
                    var key = observer + ":" + error;
                    if (result.ErrorSamples.TryGetValue(key, out var previous)) result.ErrorSamples[key] = checked(previous + 1);
                    else if (result.ErrorSamples.Count < 128) result.ErrorSamples.Add(key, 1);
                    else result.ErrorsTruncated = true;
                    if (result.Errors.TryGetValue(observer, out var observed)) result.Errors[observer] = checked(observed + 1);
                    else if (result.Errors.Count < 128) result.Errors.Add(observer, 1);
                    else result.ErrorsTruncated = true;
                    break;
                case "capture_status":
                    if (result.Status.Count < 64) result.Status.Add(data.Clone());
                    else result.StatusTruncated = true;
                    if (data.TryGetProperty("observer_errors", out var totals))
                    {
                        var errors = new Dictionary<string, long>(StringComparer.Ordinal);
                        foreach (var total in totals.EnumerateObject())
                        {
                            var occurrences = total.Value.GetInt64();
                            if (total.Name.Length > 256 || occurrences < 0 || errors.Count >= 128 || !errors.TryAdd(total.Name, occurrences))
                                throw new InvalidDataException("Invalid observer error summary.");
                        }
                        result.FinalErrors = errors;
                    }
                    break;
            }
        }
        input.Verify();
        if (line.Length != 0 || !result.HasStart) throw new InvalidDataException("Capture diagnostics are incomplete.");
        if (result.End is { } end && end.GetProperty("run_id").GetString() != result.Start.GetProperty("run_id").GetString())
            throw new InvalidDataException("Capture session IDs differ.");
        return result;
    }

    private static string? ReadText(JsonElement data, string name)
    {
        if (!data.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        if (text?.Length > 256) throw new InvalidDataException("Capture diagnostic value is too large.");
        return text;
    }

    private sealed record Checkpoint(long Bytes, string Sha256);

    private sealed class Summary
    {
        internal JsonElement Start;
        internal JsonElement? End;
        internal bool HasStart, ErrorsTruncated, StatusTruncated;
        internal long Dropped;
        internal readonly Dictionary<string, long> Errors = new(StringComparer.Ordinal);
        internal readonly Dictionary<string, long> ErrorSamples = new(StringComparer.Ordinal);
        internal Dictionary<string, long>? FinalErrors;
        internal readonly List<JsonElement> Status = new();
    }

    private sealed class CheckedInput : Stream
    {
        private readonly FileStream file;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly Checkpoint? checkpoint;
        private long remaining;

        internal CheckedInput(string path, Checkpoint? checkpoint)
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            this.checkpoint = checkpoint;
            remaining = checkpoint?.Bytes ?? file.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = file.Read(buffer[..(int)Math.Min(buffer.Length, remaining)]);
            if (count == 0 && remaining != 0) throw new InvalidDataException("Capture compressed stream is truncated.");
            remaining -= count;
            hash.AppendData(buffer[..count]);
            return count;
        }

        internal void Verify()
        {
            if (remaining != 0 || (checkpoint is not null
                && !Convert.ToHexString(hash.GetHashAndReset()).Equals(checkpoint.Sha256, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Capture checkpoint does not match the compressed stream.");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { file.Dispose(); hash.Dispose(); }
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

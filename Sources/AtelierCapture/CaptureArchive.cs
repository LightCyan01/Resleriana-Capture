using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtelierCapture.Archive;
using AtelierCapture.Storage;

namespace AtelierCapture;

internal static class CaptureArchive
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int MetadataLimit = 1024 * 1024;

    internal static bool Pack(string folder, bool recovered)
    {
        using var verified = PackVerified(folder, recovered);
        return verified is not null;
    }

    internal static VerifiedCapture? PackVerified(string folder, bool recovered)
    {
        using var lease = new FileStream(
            Path.Combine(folder, "session.lock"),
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None
        );
        return PackLeased(folder, recovered);
    }

    private static VerifiedCapture? PackLeased(string folder, bool recovered)
    {
        var checkpoint = ReadCheckpoint(folder);
        if (checkpoint is null || checkpoint.Environment == "Unknown")
            return null;
        var destination = Path.ChangeExtension(folder, ".zip");
        if (File.Exists(destination))
        {
            var existing = OpenArchive(destination);
            try
            {
                var verified = VerifyCompleted(folder, existing, checkpoint, null);
                return Proof(existing, destination, checkpoint, verified, false, 0);
            }
            catch
            {
                existing.Dispose();
                throw;
            }
        }
        if (!StorageCheckpoint.FileNames.All(name => File.Exists(Path.Combine(folder, name))))
            return null;
        var start = ReadStart(folder, checkpoint);
        var schema = (int)RecordProjection.Integer(start.Get("schema"));
        using var counts = new ArchiveCounts(folder);
        counts.SetIdentity(checkpoint.RunId, checkpoint.Environment, schema);
        var tails = checkpoint
            .Streams.Select(value => new
            {
                name = value.Name,
                bytes = checked(
                    new FileInfo(Path.Combine(folder, value.Name)).Length - value.Bytes
                ),
            })
            .ToArray();
        if (tails.Any(value => value.bytes < 0))
            throw new InvalidDataException("Capture committed prefix is truncated.");
        var retainedTailBytes = tails.Select(value => value.bytes).SumChecked();
        var abort = ReadAbort(folder, checkpoint);
        recovered |= retainedTailBytes != 0 || abort is not null;
        var temporary = destination + ".partial";
        if (File.Exists(temporary))
            temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        var entries = new List<VerifiedArchiveEntry>();
        long sourceBytesExpanded = 0;
        JsonElement countSummary;
        using (
            var file = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                65536
            )
        )
        {
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
            {
                for (var i = 0; i < ArchiveCounts.Names.Length; i++)
                {
                    var stamp = checkpoint.Streams[i];
                    using var target = zip.CreateEntry(
                            ArchiveCounts.Names[i],
                            CompressionLevel.Optimal
                        )
                        .Open();
                    using var input = new GzipPrefix(
                        Path.Combine(folder, stamp.Name),
                        stamp.Bytes,
                        stamp.Sha256
                    );
                    using var compress = new OverlappedWrite(target);
                    using var gzip = new GZipStream(input, CompressionMode.Decompress, true);
                    using var facts = new StreamFacts(gzip, compress);
                    using (var reader = new RecordReader(facts))
                        while (reader.Read() is { } record)
                            counts.Add(i, record);
                    input.Verify();
                    if (
                        facts.Records != counts.RecordCount(i)
                        || stamp.Records is { } committedRecords
                            && (
                                facts.Records != committedRecords
                                || facts.Bytes != stamp.ExtractedBytes
                            )
                    )
                        throw new InvalidDataException(
                            "Capture committed extracted counts differ."
                        );
                    sourceBytesExpanded = checked(sourceBytesExpanded + facts.Bytes);
                    if (i == 3 && (recovered || counts.End is null))
                    {
                        recovered = true;
                        var marker =
                            JsonSerializer.Serialize(
                                new
                                {
                                    kind = "session_recovered",
                                    schema,
                                    run_id = checkpoint.RunId,
                                    environment = checkpoint.Environment,
                                    utc = DateTimeOffset.UtcNow,
                                    shutdown_complete = false,
                                    recovery_uncertain = false,
                                    retained_provisional_compressed_bytes = ArchiveCounts.Text(
                                        retainedTailBytes
                                    ),
                                    discarded_compressed_bytes = 0,
                                    abort,
                                }
                            ) + "\n";
                        var markerBytes = Utf8.GetBytes(marker);
                        using var markerStream = new MemoryStream(markerBytes);
                        using var markerReader = new RecordReader(markerStream);
                        counts.Add(i, markerReader.Read()!);
                        facts.Append(markerBytes);
                    }
                    compress.Complete();
                    entries.Add(
                        new VerifiedArchiveEntry(
                            ArchiveCounts.Names[i],
                            facts.Sha256,
                            facts.Bytes,
                            facts.Records
                        )
                    );
                }
                countSummary = JsonSerializer.SerializeToElement(counts.Summary());
                using var manifest = zip.CreateEntry("manifest.json", CompressionLevel.Optimal)
                    .Open();
                JsonSerializer.Serialize(
                    manifest,
                    new
                    {
                        format = "resleriana-capture",
                        manifest_version = 1,
                        schema,
                        run_id = checkpoint.RunId,
                        environment = checkpoint.Environment,
                        started_utc = start.Get("utc"),
                        ended_utc = counts.End?.Get("utc"),
                        stopwatch_frequency = RecordProjection.Integer(
                            start.Get("stopwatch_frequency"),
                            true
                        ),
                        contains_sensitive_raw_data = start.Get("contains_sensitive_raw_data")
                            == "true",
                        metadata = start.Metadata,
                        recovered,
                        shutdown_complete = !recovered
                            && counts.End is not null
                            && counts.End.Get("shutdown_complete") != "false",
                        checkpoint_verified = true,
                        checkpoint_sha256 = checkpoint.Hash,
                        checkpoint_generation = checkpoint.Generation,
                        committed_batches = checkpoint.CommittedBatches is { } batches
                            ? ArchiveCounts.Text(batches)
                            : null,
                        recovery_uncertain = false,
                        discarded_compressed_bytes = 0,
                        retained_provisional_compressed_bytes = ArchiveCounts.Text(
                            retainedTailBytes
                        ),
                        retained_provisional_tails = tails,
                        abort,
                        dropped_events = counts.Dropped,
                        capture_status = counts.Status,
                        capture_status_truncated = counts.StatusTruncated,
                        observer_errors = counts.End is not null && counts.FinalErrors is not null
                            ? counts.FinalErrors
                            : counts.Errors,
                        observer_error_samples = counts.ErrorSamples,
                        observer_errors_truncated = (
                            counts.End is null || counts.FinalErrors is null
                        ) && counts.ErrorsTruncated,
                        observer_error_samples_truncated = counts.ErrorsTruncated,
                        integrity = "sha256",
                        authenticity = "unverified",
                        count_summary = countSummary,
                        warning_summary = counts.WarningSummary,
                        error_summary = counts.ErrorSummary,
                        entries = entries.Select(entry => new
                        {
                            name = entry.Name,
                            sha256 = entry.Sha256,
                            bytes = entry.Bytes,
                            records = entry.Records,
                        }),
                    }
                );
            }
            file.Flush(true);
        }
        Verification verification;
        string candidateHash;
        using (var candidate = OpenArchive(temporary))
        {
            var fresh =
                !recovered && schema == 5 && checkpoint.Generation is not null
                    ? new FreshFacts(entries.ToArray(), countSummary)
                    : null;
            verification = VerifyCompleted(folder, candidate, checkpoint, fresh);
            candidateHash = ArchiveHash(candidate);
        }
        File.Move(temporary, destination);
        var final = OpenArchive(destination);
        try
        {
            if (
                ArchiveHash(final) != candidateHash
                || ReadCheckpoint(folder)?.Hash != checkpoint.Hash
            )
                throw new InvalidDataException("Capture identity changed during ZIP publication.");
            return Proof(final, destination, checkpoint, verification, true, sourceBytesExpanded);
        }
        catch
        {
            final.Dispose();
            throw;
        }
    }

    private static FileStream OpenArchive(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);

    private static string ArchiveHash(FileStream archive)
    {
        archive.Position = 0;
        return Convert.ToHexString(SHA256.HashData(archive));
    }

    private static VerifiedCapture Proof(
        FileStream archive,
        string destination,
        Checkpoint checkpoint,
        Verification verified,
        bool newlyCreated,
        long buildSourceBytes
    ) =>
        new(
            archive,
            destination,
            checkpoint.RunId,
            checkpoint.Environment,
            checkpoint.Hash,
            checkpoint.Generation,
            checkpoint.Generation is null
                ? []
                : checkpoint
                    .Streams.Select(stamp => new StorageStreamStamp(
                        stamp.Name,
                        stamp.Bytes,
                        stamp.Sha256,
                        stamp.Records!.Value,
                        stamp.ExtractedBytes!.Value
                    ))
                    .ToArray(),
            verified.Entries,
            newlyCreated,
            verified.NormalShutdown,
            verified.Recovered,
            verified.Aborted,
            verified.TailBytes,
            verified.BootstrapOwnership,
            checked(buildSourceBytes + verified.SourceBytesExpanded),
            verified.ZipBytesExpanded
        );

    private static JsonElement? ReadAbort(string folder, Checkpoint checkpoint)
    {
        var path = Path.Combine(folder, "abort.json");
        if (!File.Exists(path))
            return null;
        using var document = JsonDocument.Parse(ReadMetadataFile(path, MetadataLimit));
        var abort = document.RootElement;
        if (
            abort.GetProperty("run_id").GetString() != checkpoint.RunId
            || abort.GetProperty("environment").GetString() != checkpoint.Environment
        )
            throw new InvalidDataException("Capture abort metadata belongs to another session.");
        return abort.Clone();
    }

    private static RecordProjection ReadStart(string folder, Checkpoint checkpoint)
    {
        var stamp = checkpoint.Streams[3];
        using var input = new GzipPrefix(
            Path.Combine(folder, stamp.Name),
            stamp.Bytes,
            stamp.Sha256
        );
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new RecordReader(gzip);
        var start = reader.Read();
        if (
            start is null
            || start.Get("kind") != "session_start"
            || start.Get("run_id") != checkpoint.RunId
            || start.Get("environment") != checkpoint.Environment
        )
            throw new InvalidDataException("Capture session start differs from its commit vector.");
        return start;
    }

    private static Verification VerifyCompleted(
        string folder,
        FileStream archive,
        Checkpoint checkpoint,
        FreshFacts? fresh
    )
    {
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, true);
        ZipLayout.Verify(archive, zip);
        var expectedNames = ArchiveCounts.Names.Append("manifest.json").ToArray();
        if (
            zip.Entries.Count != 5
            || expectedNames.Any(name => zip.Entries.Count(entry => entry.FullName == name) != 1)
        )
            throw new InvalidDataException("Capture ZIP contains duplicate or unexpected entries.");
        var manifestEntry = zip.GetEntry("manifest.json")!;
        using var manifestInput = manifestEntry.Open();
        using var manifestFacts = new StreamFacts(manifestInput);
        using var manifest = JsonDocument.Parse(
            ReadMetadata(manifestFacts, manifestEntry.Length, MetadataLimit)
        );
        if (manifestFacts.Crc32 != manifestEntry.Crc32)
            throw new InvalidDataException("Capture ZIP manifest CRC differs.");
        var data = manifest.RootElement;
        var schema = data.GetProperty("schema").GetInt32();
        if (
            data.GetProperty("format").GetString() != "resleriana-capture"
            || data.GetProperty("manifest_version").GetInt32() != 1
            || schema is not (4 or 5)
            || data.GetProperty("run_id").GetString() != checkpoint.RunId
            || data.GetProperty("environment").GetString() != checkpoint.Environment
            || data.GetProperty("integrity").GetString() != "sha256"
            || data.GetProperty("authenticity").GetString() != "unverified"
            || data.GetProperty("checkpoint_sha256").GetString() != checkpoint.Hash
            || !data.GetProperty("checkpoint_verified").GetBoolean()
            || data.GetProperty("recovery_uncertain").GetBoolean()
        )
            throw new InvalidDataException(
                "Capture ZIP does not match its retained commit vector."
            );
        var entries = data.GetProperty("entries").EnumerateArray().ToArray();
        if (
            entries.Length != 4
            || ArchiveCounts.Names.Any(name =>
                entries.Count(entry => entry.GetProperty("name").GetString() == name) != 1
            )
        )
            throw new InvalidDataException("Capture manifest has duplicate or missing streams.");
        var countContract = data.TryGetProperty("count_summary", out var countClaim)
            ? countClaim.GetProperty("version").GetInt32()
            : 1;
        using var counts = new ArchiveCounts(folder, countContract);
        counts.SetIdentity(checkpoint.RunId, checkpoint.Environment, schema);
        var actualEntries = new VerifiedArchiveEntry[4];
        for (var i = 0; i < ArchiveCounts.Names.Length; i++)
        {
            var name = ArchiveCounts.Names[i];
            var stored = zip.GetEntry(name)!;
            var claim = entries.Single(entry => entry.GetProperty("name").GetString() == name);
            using var input = stored.Open();
            using var facts = new StreamFacts(input);
            using (var reader = new RecordReader(facts))
                while (reader.Read() is { } record)
                    counts.Add(i, record);
            if (
                facts.Bytes != stored.Length
                || facts.Bytes != claim.GetProperty("bytes").GetInt64()
                || facts.Records != claim.GetProperty("records").GetInt64()
                || facts.Records != counts.RecordCount(i)
                || facts.Sha256 != claim.GetProperty("sha256").GetString()
                || facts.Crc32 != stored.Crc32
            )
                throw new InvalidDataException("Capture ZIP extracted stream integrity differs.");
            actualEntries[i] = new VerifiedArchiveEntry(
                name,
                facts.Sha256,
                facts.Bytes,
                facts.Records
            );
            if (fresh is not null && actualEntries[i] != fresh.Entries[i])
                throw new InvalidDataException(
                    "Capture fresh ZIP differs from its verified source stream."
                );
        }
        var recounted = JsonSerializer.SerializeToElement(counts.Summary());
        if (fresh is not null && !JsonElement.DeepEquals(fresh.Summary, recounted))
            throw new InvalidDataException(
                "Capture fresh ZIP summary differs from its source recount."
            );
        if (
            data.TryGetProperty("count_summary", out var summary)
            && !JsonElement.DeepEquals(summary, recounted)
        )
            throw new InvalidDataException(
                "Capture ZIP readable counts differ from independently recounted records."
            );
        if (schema == 5 && !data.TryGetProperty("count_summary", out _))
            throw new InvalidDataException("Capture schema 5 omits its readable counts.");
        var warningSummary = JsonSerializer.SerializeToElement(counts.WarningSummary);
        if (
            data.TryGetProperty("warning_summary", out var savedWarnings)
                ? !JsonElement.DeepEquals(savedWarnings, warningSummary)
                : counts.WarningSummary is not null
        )
            throw new InvalidDataException(
                "Capture warning summary differs from saved diagnostic occurrences."
            );
        var errorSummary = JsonSerializer.SerializeToElement(counts.ErrorSummary);
        if (
            data.TryGetProperty("error_summary", out var savedErrors)
                ? !JsonElement.DeepEquals(savedErrors, errorSummary)
                : counts.ErrorSummary is not null
        )
            throw new InvalidDataException(
                "Capture error summary differs from saved diagnostic occurrences."
            );
        var recovered = data.GetProperty("recovered").GetBoolean();
        var expectedShutdown =
            !recovered && counts.End is not null && counts.End.Get("shutdown_complete") != "false";
        if (
            data.GetProperty("shutdown_complete").GetBoolean() != expectedShutdown
            || counts.Recovered != recovered
            || !JsonElement.DeepEquals(
                data.GetProperty("capture_status"),
                JsonSerializer.SerializeToElement(counts.Status)
            )
            || data.GetProperty("capture_status_truncated").GetBoolean() != counts.StatusTruncated
        )
            throw new InvalidDataException(
                "Capture ZIP completeness/status metadata differs from records."
            );
        long actualTail = 0;
        if (data.TryGetProperty("retained_provisional_compressed_bytes", out var retained))
        {
            var actualTails = checkpoint
                .Streams.Select(stamp => new
                {
                    name = stamp.Name,
                    bytes = checked(
                        new FileInfo(Path.Combine(folder, stamp.Name)).Length - stamp.Bytes
                    ),
                })
                .ToArray();
            actualTail = actualTails.Select(value => value.bytes).SumChecked();
            if (actualTail != RecordProjection.Integer(retained.GetString()))
                throw new InvalidDataException(
                    "Capture ZIP provisional-tail accounting differs from retained originals."
                );
            if (
                !JsonElement.DeepEquals(
                    data.GetProperty("retained_provisional_tails"),
                    JsonSerializer.SerializeToElement(actualTails)
                )
            )
                throw new InvalidDataException(
                    "Capture ZIP per-file provisional-tail accounting differs."
                );
        }
        if (
            counts.Start?.Metadata is not { } metadata
            || !JsonElement.DeepEquals(data.GetProperty("metadata"), metadata)
            || data.GetProperty("started_utc").GetString() != counts.Start.Get("utc")
            || (
                data.GetProperty("ended_utc").ValueKind == JsonValueKind.Null
                    ? null
                    : data.GetProperty("ended_utc").GetString()
            ) != counts.End?.Get("utc")
        )
            throw new InvalidDataException("Capture ZIP producer/session metadata differs.");
        if (
            data.GetProperty("stopwatch_frequency").GetInt64()
                != RecordProjection.Integer(counts.Start.Get("stopwatch_frequency"), true)
            || data.GetProperty("contains_sensitive_raw_data").GetBoolean()
                != (counts.Start.Get("contains_sensitive_raw_data") == "true")
        )
            throw new InvalidDataException("Capture ZIP clock/raw-data metadata differs.");
        if (
            checkpoint.Generation is { } generation
            && data.GetProperty("checkpoint_generation").GetInt64() != generation
        )
            throw new InvalidDataException("Capture ZIP checkpoint generation differs.");
        if (
            checkpoint.CommittedBatches is { } batches
            && data.GetProperty("committed_batches").GetString() != ArchiveCounts.Text(batches)
        )
            throw new InvalidDataException("Capture ZIP committed batch count differs.");
        if (
            data.TryGetProperty("abort", out var recordedAbort)
            && !JsonElement.DeepEquals(
                recordedAbort,
                JsonSerializer.SerializeToElement(ReadAbort(folder, checkpoint))
            )
        )
            throw new InvalidDataException(
                "Capture ZIP abort metadata differs from retained originals."
            );
        var comparedSourceBytes = fresh is null
            ? VerifySources(folder, zip, checkpoint, recovered)
            : 0;
        var zipBytesExpanded = actualEntries.Select(entry => entry.Bytes).SumChecked();
        if (fresh is null)
            zipBytesExpanded = checked(zipBytesExpanded * 2);
        return new Verification(
            actualEntries,
            expectedShutdown && counts.End?.Get("shutdown_complete") == "true",
            recovered,
            ReadAbort(folder, checkpoint) is not null,
            actualTail,
            counts.BootstrapOwnership,
            comparedSourceBytes,
            zipBytesExpanded
        );
    }

    private static long VerifySources(
        string folder,
        ZipArchive zip,
        Checkpoint checkpoint,
        bool recovered
    )
    {
        var buffer = new byte[65536];
        var saved = new byte[65536];
        long sourceBytes = 0;
        for (var i = 0; i < ArchiveCounts.Names.Length; i++)
        {
            var stamp = checkpoint.Streams[i];
            using var compressed = new GzipPrefix(
                Path.Combine(folder, stamp.Name),
                stamp.Bytes,
                stamp.Sha256
            );
            using var gzip = new GZipStream(compressed, CompressionMode.Decompress, true);
            using var facts = new StreamFacts(gzip);
            using var archived = zip.GetEntry(ArchiveCounts.Names[i])!.Open();
            int count;
            while ((count = facts.Read(buffer)) != 0)
            {
                archived.ReadExactly(saved.AsSpan(0, count));
                if (!buffer.AsSpan(0, count).SequenceEqual(saved.AsSpan(0, count)))
                    throw new InvalidDataException(
                        "Capture ZIP differs from its retained original prefix."
                    );
            }
            compressed.Verify();
            sourceBytes = checked(sourceBytes + facts.Bytes);
            if (
                stamp.Records is { } expectedRecords
                && (facts.Records != expectedRecords || facts.Bytes != stamp.ExtractedBytes)
            )
                throw new InvalidDataException(
                    "Capture source prefix differs from its commit counts."
                );
            if (i != 3 || !recovered)
            {
                if (archived.ReadByte() != -1)
                    throw new InvalidDataException("Capture ZIP has unaccounted extra records.");
            }
            else
            {
                using var reader = new RecordReader(archived);
                var marker = reader.Read();
                if (marker?.Get("kind") != "session_recovered" || reader.Read() is not null)
                    throw new InvalidDataException("Capture ZIP recovery suffix differs.");
            }
        }
        return sourceBytes;
    }

    private static Checkpoint? ReadCheckpoint(string folder)
    {
        var candidates = new List<Checkpoint>();
        var anySlot = false;
        foreach (var slot in new[] { "session-state.0.json", "session-state.1.json" })
        {
            var path = Path.Combine(folder, slot);
            if (!File.Exists(path))
                continue;
            anySlot = true;
            try
            {
                var bytes = ReadMetadataFile(path, StorageCheckpoint.MaximumBytes);
                var payload = StorageCheckpoint.Decode(bytes);
                candidates.Add(
                    new Checkpoint(
                        payload.RunId,
                        payload.Environment,
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        payload.Generation,
                        payload.CommittedBatches,
                        payload
                            .Streams.Select(value => new Prefix(
                                value.Name,
                                value.Bytes,
                                value.Sha256,
                                value.Records,
                                value.ExtractedBytes
                            ))
                            .ToArray()
                    )
                );
            }
            catch (Exception exception)
                when (exception
                        is JsonException
                            or InvalidDataException
                            or FormatException
                            or OverflowException
                ) { }
        }
        if (candidates.Count != 0)
        {
            if (
                candidates.Select(value => (value.RunId, value.Environment)).Distinct().Count() != 1
                || candidates
                    .GroupBy(value => value.Generation)
                    .Any(group => group.Select(value => value.Hash).Distinct().Count() != 1)
            )
                throw new InvalidDataException(
                    "Capture commit vectors have conflicting identities/generations."
                );
            return candidates.MaxBy(value => value.Generation);
        }
        if (anySlot)
            throw new InvalidDataException("Capture has no valid durable commit vector.");
        var legacy = Path.Combine(folder, "session-state.json");
        if (!File.Exists(legacy))
            return null;
        var original = ReadMetadataFile(legacy, StorageCheckpoint.MaximumBytes);
        using var document = JsonDocument.Parse(original);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1)
            throw new InvalidDataException("Unknown capture checkpoint schema.");
        var values = root.GetProperty("files")
            .EnumerateArray()
            .Select(value => new Prefix(
                value.GetProperty("name").GetString()!,
                value.GetProperty("bytes").GetInt64(),
                value.GetProperty("sha256").GetString()!,
                null,
                null
            ))
            .ToArray();
        if (
            values.Length != 4
            || StorageCheckpoint.FileNames.Any(name =>
                values.Count(value => value.Name == name) != 1
            )
            || values.Any(value =>
                value.Bytes < 0 || value.Sha256.Length != 64 || !value.Sha256.All(Uri.IsHexDigit)
            )
        )
            throw new InvalidDataException("Legacy capture checkpoint streams are invalid.");
        return new Checkpoint(
            root.GetProperty("run_id").GetString()!,
            root.GetProperty("environment").GetString()!,
            Convert.ToHexString(SHA256.HashData(original)),
            null,
            null,
            StorageCheckpoint
                .FileNames.Select(name => values.Single(value => value.Name == name))
                .ToArray()
        );
    }

    private static byte[] ReadMetadataFile(string path, int limit)
    {
        using var file = File.OpenRead(path);
        return ReadMetadata(file, file.Length, limit);
    }

    private static byte[] ReadMetadata(Stream input, long length, int limit)
    {
        if (length < 0 || length > limit)
            throw new InvalidDataException("Capture control metadata exceeds its bound.");
        var bytes = new byte[(int)length];
        input.ReadExactly(bytes);
        if (input.ReadByte() != -1)
            throw new InvalidDataException("Capture control metadata length differs.");
        return bytes;
    }

    private sealed record Prefix(
        string Name,
        long Bytes,
        string Sha256,
        long? Records,
        long? ExtractedBytes
    );

    private sealed record Checkpoint(
        string RunId,
        string Environment,
        string Hash,
        long? Generation,
        long? CommittedBatches,
        Prefix[] Streams
    );

    private sealed record FreshFacts(VerifiedArchiveEntry[] Entries, JsonElement Summary);

    private sealed record Verification(
        VerifiedArchiveEntry[] Entries,
        bool NormalShutdown,
        bool Recovered,
        bool Aborted,
        long TailBytes,
        BootstrapOwnership? BootstrapOwnership,
        long SourceBytesExpanded,
        long ZipBytesExpanded
    );
}

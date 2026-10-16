using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class StorageChecks
{
    private static readonly object Metadata = new
    {
        plugin = "synthetic-storage-check",
        version = "check",
    };

    internal static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "atelier-storage-checks",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var clock = Stopwatch.StartNew();
        Checkpoints();
        EmptySuffixAllocation(Path.Combine(root, "empty-suffix"));
        ManagedStatusFailure(Path.Combine(root, "status-failure"));
        MultiStream(Path.Combine(root, "multi"));
        PartialAppend(Path.Combine(root, "short-write"));
        AmbiguousCheckpoint(Path.Combine(root, "checkpoint"));
        PermanentFailureAbort(Path.Combine(root, "abort"));
        PrefixConflict(Path.Combine(root, "conflict"));
        BytePressure(Path.Combine(root, "pressure"));
        PressureAbort(Path.Combine(root, "pressure-abort"));
        Bootstrap(Path.Combine(root, "bootstrap"));
        AdmissionShutdown(Path.Combine(root, "shutdown"));
        CrashVectors(Path.Combine(root, "crash"));
        RestartRecovery(Path.Combine(root, "crash", "Acknowledge"));
        ActiveLeaseRecovery(Path.Combine(root, "crash", "after_checkpoint_flush"));
        AdmittedRecoveryShutdown(Path.Combine(root, "crash", "CheckpointWrite"));
        InvalidRecovery(Path.Combine(root, "crash", "DataFlush"));
        ParallelAndMeasurement(Path.Combine(root, "parallel"));
        LargeBody(Path.Combine(root, "large-body"));
        WriterArchiveShutdown(Path.Combine(root, "archive-shutdown"));
        SingleEncoding(Path.Combine(root, "single-encoding"));
        CleanupFailures(Path.Combine(root, "cleanup-failures"));
        BoundedOldRecovery(Path.Combine(root, "bounded-old"));
        FilenameReservations(Path.Combine(root, "filenames"));
        CompactMeasurement(Path.Combine(root, "compact"));
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    storage_checks = "passed",
                    root,
                    elapsed_ms = clock.ElapsedMilliseconds,
                    scope = "synthetic managed writer/checkpoint and real archive handoff; native and hardware qualification separate",
                }
            )
        );
    }

    private static object Status(bool isPrivate) =>
        new { environment = isPrivate ? "Private" : "Official", complete = true };

    private static void EmptySuffixAllocation(string root)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "generated.bin");
        using var file = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None
        );
        using var member = new CompressedMember();
        using (var gzip = new GZipStream(member, CompressionLevel.Fastest, leaveOpen: true))
        {
            var generated = new byte[100 * 1024];
            new Random(104729).NextBytes(generated);
            gzip.Write(generated);
        }
        using var candidate = new MemoryStream();
        member.Visit(0, data => candidate.Write(data.Span));
        var bytes = candidate.ToArray();
        byte[] prefix = [1, 3, 5, 7, 9];
        file.Write(prefix);
        for (var warmup = 0; warmup < 32; warmup++)
            member.VerifySuffix(file, prefix.Length);
        const int observations = 128;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < observations; index++)
        {
            file.Position = 0;
            member.VerifySuffix(file, prefix.Length);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(
            allocated < 65536,
            "An empty suffix must not allocate a comparison block for every fresh append"
        );
        Check(
            file.Length == prefix.Length && file.Position == prefix.Length,
            "Empty suffix with nonzero committed prefix restores the append position"
        );
        Check(ReadFile().SequenceEqual(prefix), "Empty suffix preserves committed prefix bytes");

        SetTail(bytes.AsSpan(0, 13));
        member.VerifySuffix(file, prefix.Length);
        Check(
            file.Position == prefix.Length + 13
                && ReadFile().SequenceEqual(prefix.Concat(bytes.Take(13))),
            "Matching partial suffix remains available for exact resume"
        );
        SetTail(bytes);
        member.VerifySuffix(file, prefix.Length);
        Check(
            file.Position == prefix.Length + bytes.Length
                && ReadFile().SequenceEqual(prefix.Concat(bytes)),
            "Matching complete candidate remains available for ambiguous-flush retry"
        );
        var conflicting = bytes.AsSpan(0, 13).ToArray();
        conflicting[0] ^= 0x80;
        SetTail(conflicting);
        Invalid(() => member.VerifySuffix(file, prefix.Length));
        Check(
            ReadFile().SequenceEqual(prefix.Concat(conflicting)),
            "Conflicting suffix is rejected without overwrite"
        );
        SetTail(bytes);
        file.Position = file.Length;
        file.WriteByte(0xA5);
        Invalid(() => member.VerifySuffix(file, prefix.Length));
        Check(
            file.Length == prefix.Length + bytes.Length + 1,
            "Oversized suffix is rejected without truncation"
        );
        file.SetLength(prefix.Length - 1);
        Invalid(() => member.VerifySuffix(file, prefix.Length));
        Check(
            file.Length == prefix.Length - 1,
            "Short committed prefix is rejected before the empty-tail fast path"
        );
        SetTail([]);
        using var empty = new CompressedMember();
        empty.VerifySuffix(file, prefix.Length);
        Check(
            file.Position == prefix.Length,
            "Empty candidate also restores the exact append position"
        );
        SetTail([0xA5]);
        Invalid(() => empty.VerifySuffix(file, prefix.Length));
        Check(
            ReadFile().SequenceEqual(prefix.Append((byte)0xA5)),
            "Empty candidate rejects a physical tail"
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    zero_suffix_observations = observations,
                    zero_suffix_allocated_bytes = allocated,
                    committed_prefix_bytes = prefix.Length,
                    generated_candidate_bytes = member.Length,
                    scope = "actual member path allocation and retained-suffix preservation; not FPS or physical durability measurement",
                }
            )
        );

        void SetTail(ReadOnlySpan<byte> tail)
        {
            file.Position = 0;
            file.SetLength(0);
            file.Write(prefix);
            file.Write(tail);
            file.Position = 0;
        }

        byte[] ReadFile()
        {
            var position = file.Position;
            var value = new byte[checked((int)file.Length)];
            file.Position = 0;
            file.ReadExactly(value);
            file.Position = position;
            return value;
        }

        static void Invalid(Action action)
        {
            try
            {
                action();
            }
            catch (InvalidDataException)
            {
                return;
            }
            throw new InvalidOperationException("An invalid retained suffix was accepted.");
        }
    }

    internal static void RunZip64()
    {
        RunZip64ReaderChecks();
        const int observations = 129;
        const int paddingLength = 32 * 1024 * 1024;
        const long expectedPaddingBytes = (long)observations * paddingLength;
        const long expectedRecords = observations + 4;
        var root = Path.GetFullPath(
            Path.Combine("work", "checks", "storage-zip64", Guid.NewGuid().ToString("N"))
        );
        var padding = new string('x', paddingLength);
        var process = Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var reports = new ConcurrentQueue<string>();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    zip64_check = "start",
                    root,
                    observations,
                    padding_bytes = expectedPaddingBytes,
                    expected_diagnostic_records = expectedRecords,
                }
            )
        );
        using var writer = new SessionWriter(root, Metadata, Status, reports.Enqueue);
        for (var index = 0; index < observations; index++)
        {
            writer.Emit(
                false,
                false,
                "zip64_storage_check",
                new { index, padding },
                systemId: "Recorder"
            );
            if ((index + 1) % 16 == 0 || index + 1 == observations)
                Console.WriteLine(
                    JsonSerializer.Serialize(
                        new
                        {
                            zip64_check = "durable_progress",
                            observations = index + 1,
                            padding_bytes = (long)(index + 1) * paddingLength,
                            elapsed_ms = clock.ElapsedMilliseconds,
                        }
                    )
                );
        }
        Console.WriteLine("ZIP64_PACKAGING_STARTED");
        var folder = Folder(root, "Official");
        var checkpoint = Directory
            .GetFiles(folder, "session-state.*.json")
            .Select(path => StorageCheckpoint.Decode(ReadShared(path)))
            .MaxBy(value => value.Generation)!;
        var source = checkpoint.Streams[3];
        Check(
            source.Records + 2 == expectedRecords && source.ExtractedBytes > uint.MaxValue,
            "ZIP64 committed counts and extracted diagnostic size exceed the UInt32 boundary"
        );
        writer.Dispose();
        using (var status = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                status.RootElement.GetProperty("capture_zip_complete").GetBoolean(),
                "ZIP64 writer archive completed: " + string.Join(" | ", reports)
            );
        var path = Path.ChangeExtension(folder, ".zip");
        using var zip = ZipFile.OpenRead(path);
        Check(
            zip.Entries.Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)
                .SequenceEqual(
                    new[]
                    {
                        "api.jsonl",
                        "combat.jsonl",
                        "diagnostics.jsonl",
                        "manifest.json",
                        "raw-bodies.jsonl",
                    }
                ),
            "ZIP64 keeps exactly five flat entries"
        );
        var diagnostics = zip.GetEntry("diagnostics.jsonl")!;
        Check(
            diagnostics.Length >= source.ExtractedBytes && diagnostics.Length > uint.MaxValue,
            "ZIP64 public entry length retains every committed expanded byte"
        );
        VerifyZip64Extra(path, diagnostics.Length);
        using var manifestInput = zip.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestInput);
        var manifestEntry = manifest
            .RootElement.GetProperty("entries")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == "diagnostics.jsonl");
        Check(
            manifestEntry.GetProperty("bytes").GetInt64() == diagnostics.Length
                && manifestEntry.GetProperty("records").GetInt64() == expectedRecords,
            "ZIP64 manifest expanded bytes and physical records match precomputed totals"
        );
        var summary = manifest
            .RootElement.GetProperty("count_summary")
            .GetProperty("streams")
            .EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == "diagnostics.jsonl");
        Check(
            summary.GetProperty("records").GetString()
                == expectedRecords.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && summary.GetProperty("envelope_records").GetString()
                    == observations.ToString(System.Globalization.CultureInfo.InvariantCulture)
                && summary.GetProperty("marker_records").GetString() == "4",
            "ZIP64 readable counts reconcile with all 129 observations and four markers"
        );
        Console.WriteLine("ZIP64_INDEPENDENT_STREAM_RECOUNT_STARTED");
        var facts = ReadZip64Facts(diagnostics.Open(), paddingLength, clock);
        Check(
            facts.Bytes == diagnostics.Length
                && facts.Records == expectedRecords
                && facts.Payloads == observations
                && facts.PaddingBytes == expectedPaddingBytes,
            "ZIP64 independently read record and padding totals are exact without a JSON DOM"
        );
        Check(
            facts.Crc == diagnostics.Crc32
                && facts.Hash.Equals(
                    manifestEntry.GetProperty("sha256").GetString(),
                    StringComparison.OrdinalIgnoreCase
                ),
            "ZIP64 independently read CRC32 and SHA256 match central directory and manifest"
        );
        Check(
            !Directory.Exists(folder),
            "ZIP64 verified current working source is cleared after complete package"
        );
        Check(reports.IsEmpty, "ZIP64 completed without storage or archive fault");
        clock.Stop();
        process.Refresh();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    zip64_check = "passed",
                    root,
                    expanded_diagnostic_bytes = facts.Bytes,
                    expected_padding_bytes = expectedPaddingBytes,
                    records = facts.Records,
                    observations = facts.Payloads,
                    crc32 = facts.Crc.ToString(
                        "X8",
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    sha256 = facts.Hash,
                    archive_bytes = new FileInfo(path).Length,
                    source_gzip_bytes = source.Bytes,
                    elapsed_ms = clock.Elapsed.TotalMilliseconds,
                    managed_allocated_bytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    working_set_bytes = process.WorkingSet64,
                    peak_working_set_bytes = process.PeakWorkingSet64,
                    cpu_seconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    disk_bytes = Directory
                        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Sum(item => new FileInfo(item).Length),
                    scope = "optional generated real writer and expanded ZIP64 boundary; native/HDD8GB/one-hour acceptance separate",
                }
            )
        );
    }

    internal static void RunZip64ReaderChecks()
    {
        var known = ReadZip64Facts(
            new MemoryStream("123456789"u8.ToArray()),
            1,
            Stopwatch.StartNew()
        );
        Check(
            known.Crc == 0xCBF43926
                && known.Bytes == 9
                && known.Records == 0
                && known.Payloads == 0,
            "independent ZIP64 stream CRC32 matches the standard known vector"
        );
        const int paddingLength = 131073;
        var fixture =
            new string(' ', 131068)
            + "{\"padding\":\""
            + new string('x', paddingLength)
            + "\"}\n{\"padding\":\""
            + new string('x', paddingLength)
            + "\"}\n";
        var bytes = Encoding.UTF8.GetBytes(fixture);
        var facts = ReadZip64Facts(new MemoryStream(bytes), paddingLength, Stopwatch.StartNew());
        Check(
            facts.Bytes == bytes.LongLength
                && facts.Records == 2
                && facts.Payloads == 2
                && facts.PaddingBytes == paddingLength * 2L
                && facts.Hash == Convert.ToHexString(SHA256.HashData(bytes)),
            "independent ZIP64 stream reader handles split pattern, padding and close boundaries"
        );
        var rejected = false;
        try
        {
            ReadZip64Facts(
                new MemoryStream("{\"padding\":\"xx\"}\n"u8.ToArray()),
                3,
                Stopwatch.StartNew()
            );
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "independent ZIP64 stream reader rejects clipped padding");
    }

    private static void VerifyZip64Extra(string path, long expectedLength)
    {
        using var input = File.OpenRead(path);
        var tail = new byte[(int)Math.Min(65557, input.Length)];
        input.Position = input.Length - tail.Length;
        input.ReadExactly(tail);
        var end = -1;
        for (var offset = tail.Length - 22; offset >= 0; offset--)
            if (
                BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) == 0x06054B50
                && offset + 22 + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20))
                    == tail.Length
            )
            {
                end = offset;
                break;
            }
        Check(end >= 0, "ZIP64 end directory is present");
        long directory = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(end + 16));
        if (directory == uint.MaxValue)
        {
            input.Position = input.Length - tail.Length + end - 20;
            var locator = new byte[20];
            input.ReadExactly(locator);
            Check(
                BinaryPrimitives.ReadUInt32LittleEndian(locator) == 0x07064B50,
                "ZIP64 directory locator"
            );
            input.Position = checked(
                (long)BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8))
            );
            var wide = new byte[56];
            input.ReadExactly(wide);
            Check(
                BinaryPrimitives.ReadUInt32LittleEndian(wide) == 0x06064B50,
                "ZIP64 wide end directory"
            );
            directory = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(wide.AsSpan(48)));
        }
        input.Position = directory;
        var found = false;
        for (var index = 0; index < 5; index++)
        {
            var header = new byte[46];
            input.ReadExactly(header);
            Check(
                BinaryPrimitives.ReadUInt32LittleEndian(header) == 0x02014B50,
                "ZIP64 central header"
            );
            var name = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28))];
            input.ReadExactly(name);
            var extra = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30))];
            input.ReadExactly(extra);
            input.Seek(
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32)),
                SeekOrigin.Current
            );
            if (!name.AsSpan().SequenceEqual("diagnostics.jsonl"u8))
                continue;
            Check(
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)) == uint.MaxValue,
                "ZIP64 diagnostic central uncompressed length uses its required sentinel"
            );
            for (var offset = 0; offset < extra.Length; )
            {
                Check(extra.Length - offset >= 4, "ZIP64 extra header is complete");
                var tag = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset));
                var length = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(offset + 2));
                offset += 4;
                Check(length <= extra.Length - offset, "ZIP64 extra payload is complete");
                if (tag == 1)
                {
                    Check(
                        !found
                            && length >= 8
                            && checked(
                                (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(offset))
                            ) == expectedLength,
                        "ZIP64 extra contains the exact long uncompressed diagnostic length"
                    );
                    found = true;
                }
                offset += length;
            }
        }
        Check(found, "ZIP64 diagnostic extra field is present");
    }

    private static (
        long Bytes,
        long Records,
        long Payloads,
        long PaddingBytes,
        uint Crc,
        string Hash
    ) ReadZip64Facts(Stream input, int paddingLength, Stopwatch clock)
    {
        using (input)
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            ReadOnlySpan<byte> pattern = "\"padding\":\""u8;
            var table = new uint[256];
            for (uint index = 0; index < table.Length; index++)
            {
                var value = index;
                for (var bit = 0; bit < 8; bit++)
                    value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xEDB88320);
                table[index] = value;
            }
            uint crc = uint.MaxValue;
            long bytes = 0,
                records = 0,
                payloads = 0,
                paddingBytes = 0,
                currentPadding = 0,
                nextProgress = 256L * 1024 * 1024;
            var matched = 0;
            var inPadding = false;
            var buffer = new byte[131072];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                var data = buffer.AsSpan(0, count);
                hash.AppendData(data);
                bytes = checked(bytes + count);
                foreach (var value in data)
                {
                    crc = table[(crc ^ value) & 255] ^ (crc >> 8);
                    if (value == (byte)'\n')
                        records++;
                    if (inPadding)
                    {
                        if (value == (byte)'x')
                        {
                            currentPadding++;
                            continue;
                        }
                        Check(
                            value == (byte)'"' && currentPadding == paddingLength,
                            "ZIP64 every generated padding string is exact and uncut"
                        );
                        paddingBytes = checked(paddingBytes + currentPadding);
                        currentPadding = 0;
                        payloads++;
                        inPadding = false;
                        matched = 0;
                    }
                    else
                    {
                        matched =
                            value == pattern[matched] ? matched + 1
                            : value == pattern[0] ? 1
                            : 0;
                        if (matched == pattern.Length)
                        {
                            inPadding = true;
                            matched = 0;
                        }
                    }
                }
                if (bytes >= nextProgress)
                {
                    Console.WriteLine(
                        JsonSerializer.Serialize(
                            new
                            {
                                zip64_check = "independent_recount",
                                expanded_bytes = bytes,
                                payloads,
                                elapsed_ms = clock.ElapsedMilliseconds,
                            }
                        )
                    );
                    nextProgress += 256L * 1024 * 1024;
                }
            }
            Check(
                !inPadding && currentPadding == 0,
                "ZIP64 expanded stream does not end inside generated padding"
            );
            return (
                bytes,
                records,
                payloads,
                paddingBytes,
                ~crc,
                Convert.ToHexString(hash.GetHashAndReset())
            );
        }
    }

    private static void Checkpoints()
    {
        var stamp = new StorageCheckpointPayload(
            2,
            1,
            new string('a', 32),
            "Official",
            StorageCheckpoint
                .FileNames.Select(name => new StorageStreamStamp(
                    name,
                    20,
                    new string('0', 64),
                    0,
                    0
                ))
                .ToArray(),
            1
        );
        var bytes = StorageCheckpoint.Encode(stamp);
        Check(StorageCheckpoint.Decode(bytes).Generation == 1, "checkpoint roundtrip");
        var text = Encoding
            .UTF8.GetString(bytes)
            .Replace("\"generation\":1", "\"generation\":2", StringComparison.Ordinal);
        Reject(
            () => StorageCheckpoint.Decode(Encoding.UTF8.GetBytes(text)),
            "changed checkpoint checksum"
        );
        text = Encoding
            .UTF8.GetString(bytes)
            .Replace("\"schema\":2", "\"schema\":2,\"schema\":2", StringComparison.Ordinal);
        Reject(
            () => StorageCheckpoint.Decode(Encoding.UTF8.GetBytes(text)),
            "duplicate checkpoint key"
        );
        text = Encoding
            .UTF8.GetString(bytes)
            .Replace("api.jsonl.gz", "../evil.gz", StringComparison.Ordinal);
        Reject(
            () => StorageCheckpoint.Decode(Encoding.UTF8.GetBytes(text)),
            "checkpoint path substitution"
        );
    }

    private static void ManagedStatusFailure(string root)
    {
        var reports = new ConcurrentQueue<string>();
        using var writer = new SessionWriter(
            root,
            Metadata,
            _ => throw new InvalidOperationException("Synthetic unavailable managed status"),
            reports.Enqueue
        );
        writer.Emit(
            false,
            false,
            "storage_check",
            new { exact = "available_record", complete = true }
        );
        var folder = Folder(root, "Official");
        writer.Dispose();
        var rows = ZipLines(Path.ChangeExtension(folder, ".zip"), "diagnostics.jsonl")
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Check(
                rows.Any(row =>
                    row.RootElement.GetProperty("kind").GetString() == "storage_check"
                    && row.RootElement.GetProperty("data").GetProperty("exact").GetString()
                        == "available_record"
                ),
                "failed managed status does not discard available detached record"
            );
            Check(
                rows.Where(row =>
                        row.RootElement.GetProperty("kind").GetString() == "capture_status"
                    )
                    .All(row =>
                        !row.RootElement.GetProperty("data").GetProperty("complete").GetBoolean()
                    ),
                "failed initial/final status persists explicit incomplete evidence"
            );
            Check(reports.Count >= 2, "managed status failures remain visible out of band");
        }
        finally
        {
            foreach (var row in rows)
                row.Dispose();
        }
    }

    internal static void RunCrashChild(string root, string boundary)
    {
        var armed = false;
        void Pause(StorageAction action, bool after)
        {
            if (
                !armed
                || !(
                    (
                        boundary == "after_checkpoint_flush"
                        && action == StorageAction.CheckpointFlush
                        && after
                    ) || (boundary == action.ToString() && !after)
                )
            )
                return;
            Console.WriteLine("STORAGE_CRASH_READY");
            Console.Out.Flush();
            new ManualResetEventSlim().Wait();
        }
        var hooks = new StorageHooks
        {
            Before = (action, _, _) => Pause(action, false),
            After = (action, _, _) => Pause(action, true),
        };
        using var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        writer.Emit(false, false, "request", new { request_id = "1", index = 0 });
        armed = true;
        writer.Emit(false, false, "request", new { request_id = "2", index = 1 });
        throw new InvalidOperationException("Crash boundary did not pause the child.");
    }

    private static void CrashVectors(string root)
    {
        foreach (
            var boundary in new[]
            {
                "Append",
                "DataFlush",
                "CheckpointWrite",
                "after_checkpoint_flush",
                "Acknowledge",
            }
        )
        {
            var folderRoot = Path.Combine(root, boundary);
            var executable =
                Environment.ProcessPath
                ?? throw new InvalidOperationException("Check process path unavailable.");
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (
                Path.GetFileNameWithoutExtension(executable)
                    .Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            )
                start.ArgumentList.Add(Environment.GetCommandLineArgs()[0]);
            start.ArgumentList.Add("--storage-crash-child");
            start.ArgumentList.Add(folderRoot);
            start.ArgumentList.Add(boundary);
            using var child =
                Process.Start(start)
                ?? throw new InvalidOperationException("Crash child failed to start.");
            try
            {
                var ready = child
                    .StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(20))
                    .GetAwaiter()
                    .GetResult();
                Check(
                    ready == "STORAGE_CRASH_READY",
                    "disposable child reached exact crash boundary " + boundary
                );
                child.Kill(entireProcessTree: true);
                Check(child.WaitForExit(10000), "crash child exited");
                var folder = Folder(folderRoot, "Official");
                var state = Verify(folder, allowSuffix: true);
                var expected = boundary is "after_checkpoint_flush" or "Acknowledge" ? 2 : 1;
                Check(
                    state.CommittedBatches == expected && state.Streams[0].Records == expected,
                    "crash preserves exact acknowledged/committed vector at " + boundary
                );
                Check(
                    Directory.GetFiles(folder, "*.gz").Length == 4,
                    "crash original sources retained"
                );
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    child.WaitForExit(10000);
                }
            }
        }
    }

    private static void FilenameReservations(string root)
    {
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5);
        const string stem = "capture-2026-01-02_03-04-05";
        var directory = Path.Combine(root, "Official");
        Directory.CreateDirectory(directory);
        var protectedZip = Path.Combine(directory, stem + ".zip");
        File.WriteAllText(protectedZip, "protected-existing-zip");
        var protectedFolder = Path.Combine(directory, stem + "-2.partial");
        Directory.CreateDirectory(protectedFolder);
        File.WriteAllText(
            Path.Combine(protectedFolder, "protected.txt"),
            "protected-existing-folder"
        );
        var hooks = new StorageHooks { Timestamp = timestamp };
        using var first = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        using var second = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        Finish(
            Task.WhenAll(
                Task.Run(() => first.Emit(false, false, "storage_check", new { exact = "first" })),
                Task.Run(() => second.Emit(false, false, "storage_check", new { exact = "second" }))
            )
        );
        first.Dispose();
        second.Dispose();
        var paths = Directory
            .GetFiles(directory, "*.zip")
            .Where(path => path != protectedZip)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Check(
            paths
                .Select(Path.GetFileName)
                .SequenceEqual(new[] { stem + "-3.zip", stem + "-4.zip" }),
            "two same-second writers reserve distinct numeric collision suffixes without RunId filenames"
        );
        var ids = new HashSet<string>();
        foreach (var path in paths)
        {
            using var zip = ZipFile.OpenRead(path);
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            ids.Add(manifest.RootElement.GetProperty("run_id").GetString()!);
            Check(
                !Directory.Exists(Path.ChangeExtension(path, ".partial")),
                "friendly-name working folder is cleaned after verified ZIP"
            );
        }
        Check(
            ids.SetEquals(new[] { first.RunId, second.RunId }),
            "friendly names preserve distinct run identities in metadata"
        );
        Check(
            File.ReadAllText(protectedZip) == "protected-existing-zip"
                && File.ReadAllText(Path.Combine(protectedFolder, "protected.txt"))
                    == "protected-existing-folder",
            "existing ZIP and partial collisions are never overwritten or deleted"
        );

        var retryRoot = Path.Combine(root, "reservation-retry");
        using var failed = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var names = new ConcurrentQueue<string>();
        var retryHooks = new StorageHooks
        {
            Timestamp = timestamp,
            Before = (action, path, _) =>
            {
                if (action != StorageAction.Reserve)
                    return;
                names.Enqueue(path);
                if (!release.IsSet)
                {
                    failed.Set();
                    throw new IOException("Synthetic pre-lock storage failure.");
                }
            },
        };
        using var retry = new SessionWriter(retryRoot, Metadata, Status, _ => { }, retryHooks);
        var emit = Task.Run(() =>
            retry.Emit(false, false, "storage_check", new { exact = "retry" })
        );
        Check(failed.Wait(TimeSpan.FromSeconds(10)), "pre-lock reservation failure reached");
        Check(
            SpinWait.SpinUntil(() => names.Count >= 2, TimeSpan.FromSeconds(10)),
            "pre-lock reservation retries while callback held"
        );
        Check(
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                && Directory.GetDirectories(Path.Combine(retryRoot, "Official")).Length == 1,
            "permanent pre-lock failure holds one pending stem without endless empty folders"
        );
        release.Set();
        Finish(emit);
        retry.Dispose();
        Check(
            File.Exists(Path.Combine(retryRoot, "Official", stem + ".zip")),
            "repaired storage keeps the reserved timestamp stem"
        );

        var initializedRoot = Path.Combine(root, "initialize-retry");
        var initializeNames = new ConcurrentQueue<string>();
        var initAttempts = 0;
        var initializeHooks = new StorageHooks
        {
            Timestamp = timestamp,
            Before = (action, path, _) =>
            {
                if (action != StorageAction.Initialize)
                    return;
                initializeNames.Enqueue(path);
                if (++initAttempts == 1)
                    throw new IOException("Synthetic failure after lock reservation.");
            },
        };
        using var initialize = new SessionWriter(
            initializedRoot,
            Metadata,
            Status,
            _ => { },
            initializeHooks
        );
        initialize.Emit(false, false, "storage_check", new { exact = "same-owner" });
        initialize.Dispose();
        Check(
            initializeNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                && File.Exists(Path.Combine(initializedRoot, "Official", stem + ".zip")),
            "post-lock initialization retries retain the exact owned stem"
        );

        var lateRoot = Path.Combine(root, "late-publication");
        var published = false;
        var lateHooks = new StorageHooks
        {
            Timestamp = timestamp,
            Before = (action, path, _) =>
            {
                if (action != StorageAction.Reserve || published)
                    return;
                File.WriteAllText(
                    Path.ChangeExtension(path, ".zip"),
                    "protected-published-during-reservation"
                );
                published = true;
            },
        };
        using var late = new SessionWriter(lateRoot, Metadata, Status, _ => { }, lateHooks);
        late.Emit(false, false, "storage_check", new { exact = "late-owned" });
        late.Dispose();
        Check(
            File.ReadAllText(Path.Combine(lateRoot, "Official", stem + ".zip"))
                == "protected-published-during-reservation"
                && File.Exists(Path.Combine(lateRoot, "Official", stem + "-2.zip"))
                && Directory
                    .GetFileSystemEntries(Path.Combine(lateRoot, "Official", stem + ".partial"))
                    .Length == 0,
            "post-lock late ZIP publication keeps protected ZIP, removes only exact owned lock, and uses the next suffix"
        );
    }

    private static void RestartRecovery(string root)
    {
        var original = Folder(root, "Official");
        var checkpoint = Verify(original, allowSuffix: true);
        var sourceHashes = RecoverySourceHashes(original);
        var zipPath = Path.ChangeExtension(original, ".zip");
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        writer.Emit(false, false, "storage_check", new { exact = "new-run-only" });
        AwaitRecovery(
            () =>
            {
                using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
                return File.Exists(zipPath)
                    && status.RootElement.GetProperty("old_recovery_pending").GetInt64() == 0
                    && !status.RootElement.GetProperty("old_recovery_in_progress").GetBoolean();
            },
            "restart packages prior crash before new writer disposal"
        );
        using (var status = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                !status.RootElement.GetProperty("completed").GetBoolean()
                    && status.RootElement.GetProperty("old_recovery_pending").GetInt64() == 0,
                "restart recovers while the new capture remains active"
            );
        using (var zip = ZipFile.OpenRead(zipPath))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            Check(
                manifest.RootElement.GetProperty("run_id").GetString() == checkpoint.RunId
                    && manifest.RootElement.GetProperty("recovered").GetBoolean()
                    && !manifest.RootElement.GetProperty("shutdown_complete").GetBoolean(),
                "restart archive retains old identity and incomplete crash classification"
            );
            Check(
                manifest
                    .RootElement.GetProperty("count_summary")
                    .GetProperty("api")
                    .GetProperty("request_records")
                    .GetString() == "2",
                "restart archive contains both exact durable request records"
            );
            using var api = new StreamReader(zip.GetEntry("api.jsonl")!.Open(), Encoding.UTF8);
            var records = new List<string>();
            while (api.ReadLine() is { } line)
                records.Add(line);
            Check(
                records.SequenceEqual(Lines(original, 0)),
                "recovered committed API JSON bytes remain exact"
            );
            using var diagnostics = new StreamReader(
                zip.GetEntry("diagnostics.jsonl")!.Open(),
                Encoding.UTF8
            );
            var lines = new List<string>();
            while (diagnostics.ReadLine() is { } line)
                lines.Add(line);
            Check(
                lines.Count == checkpoint.Streams[3].Records + 1
                    && lines[^1]
                        .Contains("\"kind\":\"session_recovered\"", StringComparison.Ordinal),
                "restart archive adds one explicit recovery marker without inventing a shutdown"
            );
        }
        CheckRecoverySources(original, sourceHashes);
        var current = Directory
            .GetDirectories(Path.Combine(root, "Official"))
            .Single(folder => Verify(folder, allowSuffix: true).RunId == writer.RunId);
        Check(
            !File.Exists(Path.ChangeExtension(current, ".zip"))
                && Verify(current).RunId == writer.RunId,
            "idle recovery never packages or retargets current active sources"
        );
        var oldZipHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath)));
        writer.Dispose();
        Check(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))) == oldZipHash,
            "shutdown does not duplicate or rewrite the already recovered archive"
        );
        CheckRecoverySources(original, sourceHashes);
    }

    private static void ActiveLeaseRecovery(string root)
    {
        var original = Folder(root, "Official");
        var sourceHashes = RecoverySourceHashes(original);
        var zipPath = Path.ChangeExtension(original, ".zip");
        using var lease = new FileStream(
            Path.Combine(original, "session.lock"),
            FileMode.Open,
            FileAccess.Write,
            FileShare.None
        );
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        writer.Emit(false, false, "storage_check", new { exact = "new-run" });
        AwaitRecovery(
            () =>
            {
                using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
                return status.RootElement.GetProperty("old_recovery_failures").GetInt64() >= 1;
            },
            "active old writer lease is reported without blocking new records"
        );
        using (var status = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                status.RootElement.GetProperty("old_recovery_pending").GetInt64() == 1
                    && status.RootElement.GetProperty("old_recovery_error").GetString()
                        == "IOException"
                    && status.RootElement.GetProperty("durable_current").GetBoolean(),
                "active-lease recovery remains independently pending while current capture is durable"
            );
        Check(!File.Exists(zipPath), "active leased source was not packaged");
        CheckRecoverySources(original, sourceHashes);
        writer.Emit(false, false, "storage_check", new { exact = "next-record" });
        lease.Dispose();
        writer.Dispose();
        Check(!File.Exists(zipPath), "quit does not retry an old blocked archive");
        using var restart = new SessionWriter(root, Metadata, Status, _ => { });
        AwaitRecovery(
            () => File.Exists(zipPath),
            "released old lease recovers on the next process startup idle pass"
        );
        CheckRecoverySources(original, sourceHashes);
    }

    private static void AdmittedRecoveryShutdown(string root)
    {
        var original = Folder(root, "Official");
        var sourceHashes = RecoverySourceHashes(original);
        var zipPath = Path.ChangeExtension(original, ".zip");
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        var scope = writer.BeginObservation();
        Check(
            !SpinWait.SpinUntil(() => File.Exists(zipPath), TimeSpan.FromMilliseconds(600)),
            "admitted copying callback takes priority over idle recovery"
        );
        var closing = Task.Run(writer.Dispose);
        AwaitRecovery(
            () =>
            {
                using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
                return status.RootElement.GetProperty("stopping").GetBoolean();
            },
            "recovery close race shuts admission before pending callback finishes"
        );
        Check(
            !closing.IsCompleted,
            "shutdown still awaits admitted observation durable acknowledgement"
        );
        writer.Emit(false, false, "storage_check", new { exact = "admitted-before-close" });
        scope.Dispose();
        Finish(closing);
        Check(
            !File.Exists(zipPath),
            "shutdown leaves deferred old recovery pending without a quit-time full pass"
        );
        using var restart = new SessionWriter(root, Metadata, Status, _ => { });
        AwaitRecovery(
            () => File.Exists(zipPath),
            "deferred old capture recovers during next writer idle startup"
        );
        CheckRecoverySources(original, sourceHashes);
    }

    private static void InvalidRecovery(string root)
    {
        var original = Folder(root, "Official");
        foreach (var slot in Directory.GetFiles(original, "session-state.*.json"))
            File.WriteAllText(slot, "synthetic torn checkpoint");
        var sourceHashes = RecoverySourceHashes(original);
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        writer.Emit(false, false, "storage_check", new { exact = "safe-current" });
        AwaitRecovery(
            () =>
            {
                using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
                return status.RootElement.GetProperty("old_recovery_failures").GetInt64() >= 1;
            },
            "invalid checkpoint stays visible and pending during recording"
        );
        var scope = writer.BeginObservation();
        writer.Emit(false, false, "storage_check", new { exact = "explicitly-aborted" });
        var abort = Task.Run(() =>
            writer.Abort("Synthetic explicit abort with pending old recovery")
        );
        Finish(abort);
        try
        {
            scope.Dispose();
            throw new InvalidOperationException(
                "Aborted recovery observation acknowledged success."
            );
        }
        catch (CaptureAbortedException) { }
        writer.Dispose();
        WaitCompleted(writer);
        using (var status = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                status.RootElement.GetProperty("aborted").GetBoolean()
                    && status.RootElement.GetProperty("old_recovery_pending").GetInt64() == 1
                    && !status.RootElement.GetProperty("durable_current").GetBoolean(),
                "abort status stays responsive outside copy gate without hiding pending old failure"
            );
        Check(
            !File.Exists(Path.ChangeExtension(original, ".zip")),
            "invalid old checkpoint never produces a falsely verified ZIP"
        );
        CheckRecoverySources(original, sourceHashes);
    }

    private static Dictionary<string, string> RecoverySourceHashes(string folder) =>
        Directory
            .GetFiles(folder)
            .Where(path => Path.GetFileName(path) != "session.lock")
            .ToDictionary(
                path => path,
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal
            );

    private static void CheckRecoverySources(string folder, Dictionary<string, string> expected)
    {
        Check(
            RecoverySourceHashes(folder)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(pair => pair.Key, StringComparer.Ordinal)),
            "recovery preserves every original gzip/checkpoint/provisional source byte"
        );
    }

    private static void AwaitRecovery(Func<bool> predicate, string message) =>
        Check(SpinWait.SpinUntil(predicate, TimeSpan.FromSeconds(10)), message);

    private static void MultiStream(string root)
    {
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var flushed = new ConcurrentDictionary<string, byte>();
        var hooks = new StorageHooks
        {
            Before = (action, path, _) =>
            {
                if (action == StorageAction.CheckpointFlush)
                {
                    Check(
                        flushed.Count == 4,
                        "all four initial gzip prefixes flushed before metadata"
                    );
                    reached.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(10)), "checkpoint control released");
                }
            },
            After = (action, path, _) =>
            {
                if (action == StorageAction.DataFlush)
                    flushed.TryAdd(path, 0);
            },
        };

        using var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        var task = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            writer.Emit(
                false,
                false,
                "request",
                new { request_id = "1", value = 17 },
                systemId: "Battle"
            );
            using (writer.BeginObservation())
                writer.Emit(
                    false,
                    false,
                    "raw_body",
                    new { request_id = "1", body = new byte[] { 0, 1, 255 } },
                    systemId: "Battle"
                );
            writer.Emit(
                false,
                true,
                "combat_enter",
                new { invocation_id = "1", value = long.MaxValue },
                systemId: "Battle"
            );
        });
        Check(reached.Wait(TimeSpan.FromSeconds(10)), "durability barrier reached");
        Check(!task.IsCompleted, "callback does not return before checkpoint flush");
        using (var state = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                state.RootElement.GetProperty("retained_batches").GetInt32() == 1,
                "writing batch remains charged"
            );
        release.Set();
        Finish(task);
        var folder = Folder(root, "Official");
        var checkpoint = Verify(folder);
        Check(checkpoint.CommittedBatches == 1, "nested observation is one atomic batch");
        Check(
            checkpoint.Streams[0].Records == 1
                && checkpoint.Streams[1].Records == 1
                && checkpoint.Streams[2].Records == 1,
            "all callback streams committed"
        );
        using var combat = JsonDocument.Parse(Lines(folder, 1).Single());
        Check(
            combat.RootElement.GetProperty("data").GetProperty("value").GetInt64() == long.MaxValue,
            "Int64 retains exact width"
        );
    }

    private static void PartialAppend(string root)
    {
        var writes = 0;
        var hooks = new StorageHooks
        {
            Append = (file, data) =>
            {
                if (Interlocked.Increment(ref writes) == 2)
                {
                    file.Write(data.Span[..Math.Max(1, data.Length / 2)]);
                    file.Flush(true);
                    throw new IOException("Synthetic ambiguous partial write.");
                }
                file.Write(data.Span);
            },
        };
        var body = new byte[200003];
        new Random(174).NextBytes(body);
        using var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        writer.Emit(false, false, "raw_body", new { request_id = "1", body });
        writer.Emit(false, false, "raw_body", new { request_id = "2", body = new byte[] { 1, 2 } });
        var folder = Folder(root, "Official");
        var checkpoint = Verify(folder);
        Check(checkpoint.CommittedBatches == 2, "partial write retry does not double commit");
        var rows = Lines(folder, 2);
        Check(rows.Length == 2, "partial write emits exactly one original record");
        using var first = JsonDocument.Parse(rows[0]);
        Check(
            first
                .RootElement.GetProperty("data")
                .GetProperty("body")
                .GetBytesFromBase64()
                .SequenceEqual(body),
            "resumed suffix exact bytes"
        );
        Check(writes > 4, "partial append fault actually exercised");
    }

    private static void AmbiguousCheckpoint(string root)
    {
        var thrown = 0;
        var hooks = new StorageHooks
        {
            After = (action, _, _) =>
            {
                if (
                    action == StorageAction.CheckpointFlush
                    && Interlocked.Exchange(ref thrown, 1) == 0
                )
                    throw new IOException("Synthetic successful flush with lost completion.");
            },
        };
        using var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        writer.Emit(false, false, "request", new { request_id = "1" });
        writer.Emit(false, false, "request", new { request_id = "2" });
        var folder = Folder(root, "Official");
        Check(
            Verify(folder).CommittedBatches == 2 && Lines(folder, 0).Length == 2,
            "ambiguous flush neither duplicates nor skips"
        );
        Check(
            Directory.GetFiles(folder, "session-state.*.json").Length == 2,
            "alternating slots retained"
        );
    }

    private static void PermanentFailureAbort(string root)
    {
        using var failed = new ManualResetEventSlim();
        var hooks = new StorageHooks
        {
            Before = (action, _, _) =>
            {
                if (action == StorageAction.DataFlush)
                {
                    failed.Set();
                    throw new IOException("Synthetic permanent full disk.");
                }
            },
        };
        var reports = new ConcurrentQueue<string>();
        var writer = new SessionWriter(root, Metadata, Status, reports.Enqueue, hooks);
        var task = Task.Run(() => writer.Emit(false, false, "request", new { request_id = "1" }));
        Check(failed.Wait(TimeSpan.FromSeconds(10)), "permanent fault reached");
        Check(!task.IsCompleted, "permanent output failure holds callback");
        using (var state = JsonSerializer.SerializeToDocument(writer.GetStatus()))
            Check(
                state.RootElement.GetProperty("retained_batches").GetInt32() == 1,
                "failed writer keeps owned batch"
            );
        writer.Abort("Synthetic explicit operator abort");
        try
        {
            Finish(task);
            throw new InvalidOperationException("Abort reported callback success.");
        }
        catch (CaptureAbortedException) { }
        writer.Dispose();
        WaitCompleted(writer);
        Check(
            Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories).Length > 0,
            "abort retains physical gzip data"
        );
        Check(
            Directory.GetFiles(root, "abort.json", SearchOption.AllDirectories).Length == 1,
            "abort reason persisted when storage available"
        );
        Check(reports.Count != 0, "fault status visible out of band");
    }

    private static void BytePressure(string root)
    {
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var secondCopied = new ManualResetEventSlim();
        using var thirdCopied = new ManualResetEventSlim();
        var first = 0;
        var hooks = new StorageHooks
        {
            Before = (action, _, _) =>
            {
                if (
                    action == StorageAction.CheckpointFlush
                    && Interlocked.Exchange(ref first, 1) == 0
                )
                {
                    reached.Set();
                    Check(release.Wait(TimeSpan.FromSeconds(10)), "pressure gate release");
                }
            },
        };
        using var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        var one = Task.Run(() =>
            writer.Emit(
                false,
                true,
                "combat_enter",
                new { index = 1 },
                SessionWriter.QueueBudget + 1
            )
        );
        Check(reached.Wait(TimeSpan.FromSeconds(10)), "oversized record admitted alone");
        var two = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            writer.Emit(false, true, "combat_enter", new { index = 2 }, 4096);
            secondCopied.Set();
        });
        Check(
            secondCopied.Wait(TimeSpan.FromSeconds(10)),
            "one active copier can wait for byte credit"
        );
        var three = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            thirdCopied.Set();
            writer.Emit(false, true, "combat_enter", new { index = 3 }, 4096);
        });
        Check(
            !thirdCopied.Wait(TimeSpan.FromMilliseconds(80)),
            "blocked active copier prevents concurrent detached growth"
        );
        Check(!one.IsCompleted && !two.IsCompleted, "pressure callbacks remain unacknowledged");
        release.Set();
        Finish(Task.WhenAll(one, two, three));
        Check(
            Verify(Folder(root, "Official")).Streams[1].Records == 3,
            "pressure preserves all records"
        );
    }

    private static void PrefixConflict(string root)
    {
        using var reached = new ManualResetEventSlim();
        var attempts = 0;
        var hooks = new StorageHooks
        {
            Append = (file, data) =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    var corrupt = data.ToArray();
                    corrupt[0] ^= 1;
                    file.Write(corrupt);
                    file.Flush(true);
                    reached.Set();
                    throw new IOException("Synthetic conflicting write content.");
                }
                file.Write(data.Span);
            },
        };
        var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        var task = Task.Run(() => writer.Emit(false, false, "request", new { request_id = "1" }));
        Check(reached.Wait(TimeSpan.FromSeconds(10)), "conflicting suffix written");
        var folder = Folder(root, "Official");
        var path = Path.Combine(folder, "api.jsonl.gz");
        byte[] original;
        using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            original = new byte[read.Length];
            read.ReadExactly(original);
        }
        Check(
            !task.Wait(TimeSpan.FromMilliseconds(350)),
            "conflicting suffix holds rather than overwrites"
        );
        writer.Abort("Synthetic conflicting suffix abort");
        try
        {
            Finish(task);
        }
        catch (CaptureAbortedException) { }
        writer.Dispose();
        WaitCompleted(writer);
        Check(
            File.ReadAllBytes(path).SequenceEqual(original),
            "conflicting physical bytes retained"
        );
    }

    private static void PressureAbort(string root)
    {
        using var failed = new ManualResetEventSlim();
        using var copied = new ManualResetEventSlim();
        var hooks = new StorageHooks
        {
            Before = (action, _, _) =>
            {
                if (action == StorageAction.DataFlush)
                {
                    failed.Set();
                    throw new IOException("Synthetic blocked storage during admission.");
                }
            },
        };
        var writer = new SessionWriter(root, Metadata, Status, _ => { }, hooks);
        var one = Task.Run(() =>
            writer.Emit(
                false,
                true,
                "combat_enter",
                new { index = 1 },
                SessionWriter.QueueBudget + 1
            )
        );
        Check(failed.Wait(TimeSpan.FromSeconds(10)), "abort pressure writer is blocked");
        var two = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            writer.Emit(false, true, "combat_enter", new { index = 2 });
            copied.Set();
        });
        Check(copied.Wait(TimeSpan.FromSeconds(10)), "abort pressure second copy owns its graph");
        var three = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            writer.Emit(false, true, "combat_enter", new { index = 3 });
        });
        Check(
            SpinWait.SpinUntil(
                () =>
                {
                    using var state = JsonSerializer.SerializeToDocument(writer.GetStatus());
                    return state.RootElement.GetProperty("admitted_observations").GetInt32() == 3;
                },
                TimeSpan.FromSeconds(10)
            ),
            "abort pressure third observer is admitted before waiting for copy ownership"
        );
        var close = Task.Run(writer.Dispose);
        writer.Abort("Synthetic explicit abort with blocked producers and shutdown");
        foreach (var observer in new[] { one, two, three })
        {
            try
            {
                Finish(observer);
                throw new InvalidOperationException(
                    "Blocked observer returned success after abort."
                );
            }
            catch (CaptureAbortedException) { }
        }
        Finish(close);
        WaitCompleted(writer);
        using var result = JsonSerializer.SerializeToDocument(writer.GetStatus());
        Check(
            result.RootElement.GetProperty("admitted_observations").GetInt32() == 0
                && result.RootElement.GetProperty("retained_batches").GetInt32() == 0
                && result.RootElement.GetProperty("queued_batches").GetInt32() == 0,
            "explicit abort releases only the aborted in-memory ownership and leaves no orphan queue batch"
        );
        Check(
            !result.RootElement.GetProperty("durable_current").GetBoolean(),
            "aborted run never reports current observations durably complete"
        );
        Check(
            Directory.GetFiles(root, "*.gz", SearchOption.AllDirectories).Length > 0,
            "abort pressure original provisional files remain"
        );
    }

    private static void LargeBody(string root)
    {
        var body = new byte[32 * 1024 * 1024 + 5];
        new Random(421).NextBytes(body);
        var expected = SHA256.HashData(body);
        var watch = Stopwatch.StartNew();
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        using (var observation = writer.BeginObservation())
        {
            foreach (var length in new[] { 0, 1, 2, 3, 65535, 65536, 65537 })
            {
                var value = body.AsSpan(0, length).ToArray();
                writer.Emit(false, false, "raw_body", new { length, body = value });
            }
            writer.Emit(
                false,
                false,
                "raw_body",
                new { length = body.Length, body },
                body.Length * 2L
            );
            writer.Emit(
                false,
                false,
                "request",
                new { text = new string('x', 8191) + "😀\n\"tail", request_id = "1" }
            );
        }
        body.AsSpan().Fill(42);
        var folder = Folder(root, "Official");
        var state = Verify(folder);
        Check(
            state.Streams[2].Records == 8,
            "oversized complete body and Base64 variants preserved"
        );
        foreach (var line in Lines(folder, 2))
        {
            using var row = JsonDocument.Parse(line);
            var data = row.RootElement.GetProperty("data");
            var bytes = data.GetProperty("body").GetBytesFromBase64();
            Check(
                bytes.Length == data.GetProperty("length").GetInt32(),
                "segmented Base64 exact declared length"
            );
            if (bytes.Length > 32 * 1024 * 1024)
                Check(
                    SHA256.HashData(bytes).SequenceEqual(expected),
                    "body above former cap remains exact after producer mutation"
                );
        }
        using var textRow = JsonDocument.Parse(Lines(folder, 0).Single());
        Check(
            textRow.RootElement.GetProperty("data").GetProperty("text").GetString()
                == new string('x', 8191) + "😀\n\"tail",
            "segmented UTF16 preserves split surrogate, newline and quote"
        );
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    oversized_raw_body_bytes = body.Length,
                    compressed_raw_bytes = state.Streams[2].Bytes,
                    elapsed_ms = watch.ElapsedMilliseconds,
                }
            )
        );
    }

    private static void Bootstrap(string root)
    {
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        writer.EmitBootstrap(
            "bootstrap_diagnostic",
            new { exact = "line\nquoted\"", complete = false }
        );
        writer.EmitBootstrap(
            "unbound_native_enter",
            new
            {
                system_id = "Battle",
                invocation_id = "9007199254740993",
                complete = false,
            }
        );
        var process = Folder(root, "Process");
        Check(
            Verify(process).Environment == "Unknown",
            "bootstrap is a durable unknown process spool"
        );
        writer.Emit(false, false, "request", new { request_id = "1" });
        writer.Emit(true, false, "request", new { request_id = "2" });
        writer.EmitBootstrap(
            "unbound_native_exit",
            new
            {
                system_id = "Battle",
                invocation_id = "9007199254740993",
                complete = false,
            }
        );
        var official = Lines(Folder(root, "Official"), 3)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        var privateRows = Lines(Folder(root, "Private"), 3)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            var transferred = official
                .Where(row =>
                    row.RootElement.GetProperty("kind").GetString() == "process_bootstrap"
                )
                .ToArray();
            Check(
                transferred.Length == Lines(process, 3).Length,
                "every process record transfers without a guessed environment"
            );
            var original = transferred
                .Select(row => row.RootElement.GetProperty("data").GetProperty("original_record"))
                .Single(row => row.GetProperty("kind").GetString() == "bootstrap_diagnostic");
            Check(
                original.GetProperty("environment").GetString() == "Unknown"
                    && original.GetProperty("system_id").GetString() == "Recorder"
                    && original.GetProperty("data").GetProperty("original_environment").GetString()
                        == "Unknown"
                    && original
                        .GetProperty("data")
                        .GetProperty("data")
                        .GetProperty("exact")
                        .GetString() == "line\nquoted\"",
                "bootstrap identity and contents exact"
            );
            Check(
                transferred
                    .Select(row =>
                        row.RootElement.GetProperty("data")
                            .GetProperty("original_record")
                            .GetRawText()
                    )
                    .SequenceEqual(Lines(process, 3)),
                "transferred bootstrap JSON bytes preserve every original record"
            );
            var unbound = official
                .Single(row =>
                    row.RootElement.GetProperty("kind").GetString() == "unbound_native_exit"
                )
                .RootElement;
            Check(
                unbound.GetProperty("system_id").GetString() == "Recorder"
                    && unbound.GetProperty("data").GetProperty("original_environment").GetString()
                        == "Unknown"
                    && unbound
                        .GetProperty("data")
                        .GetProperty("data")
                        .GetProperty("system_id")
                        .GetString() == "Battle"
                    && unbound
                        .GetProperty("data")
                        .GetProperty("data")
                        .GetProperty("invocation_id")
                        .GetString() == "9007199254740993",
                "unbound gameplay stays unknown under the process owner without losing identity"
            );
            Check(
                privateRows.Count(row =>
                    row.RootElement.GetProperty("kind").GetString() == "process_bootstrap_reference"
                ) == 1,
                "second environment references rather than duplicates process evidence"
            );
            Check(
                !Directory.GetFiles(Path.Combine(root, "Process"), "*.zip").Any(),
                "bootstrap creates no Unknown ZIP"
            );
        }
        finally
        {
            foreach (var row in official.Concat(privateRows))
                row.Dispose();
        }
    }

    private static void AdmissionShutdown(string root)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        var emit = Task.Run(() =>
        {
            using var observation = writer.BeginObservation();
            entered.Set();
            Check(release.Wait(TimeSpan.FromSeconds(10)), "admitted copy released");
            writer.Emit(false, false, "request", new { request_id = "1" });
        });
        Check(entered.Wait(TimeSpan.FromSeconds(10)), "observer admitted before shutdown");
        var close = Task.Run(writer.Dispose);
        Check(
            SpinWait.SpinUntil(
                () =>
                {
                    using var state = JsonSerializer.SerializeToDocument(writer.GetStatus());
                    return state.RootElement.GetProperty("stopping").GetBoolean();
                },
                TimeSpan.FromSeconds(10)
            ),
            "shutdown admission closed"
        );
        var secondClose = Task.Run(writer.Dispose);
        Check(
            !close.IsCompleted && !secondClose.IsCompleted,
            "all shutdown callers await admitted records"
        );
        release.Set();
        Finish(Task.WhenAll(emit, close, secondClose));
        var zipPath = Directory.GetFiles(Path.Combine(root, "Official"), "*.zip").Single();
        Check(ZipLines(zipPath, "api.jsonl").Length == 1, "shutdown commits admitted copy");
        Check(
            ZipLines(zipPath, "diagnostics.jsonl")
                .Any(line => line.Contains("\"kind\":\"session_end\"", StringComparison.Ordinal)),
            "normal shutdown durable end"
        );
    }

    private static void WriterArchiveShutdown(string root)
    {
        var reports = new ConcurrentQueue<string>();
        using var writer = new SessionWriter(root, Metadata, Status, reports.Enqueue);
        writer.EmitBootstrap(
            "bootstrap_diagnostic",
            new { exact = "process-original", complete = true }
        );
        writer.Emit(false, false, "storage_check", new { exact = "official", complete = true });
        writer.Emit(true, false, "storage_check", new { exact = "private", complete = true });
        foreach (var environment in new[] { "Official", "Private" })
        {
            var prefix = Verify(Folder(root, environment));
            Check(
                prefix.Streams.All(stream => stream.Bytes >= 20),
                "initial untouched streams contain complete nonzero empty gzip members"
            );
            Check(
                prefix
                    .Streams.Take(3)
                    .All(stream => stream.Records == 0 && stream.ExtractedBytes == 0),
                "initial empty gzip members preserve zero extracted bytes and records"
            );
        }
        var folders = new[] { "Official", "Private" }.ToDictionary(
            environment => environment,
            environment => Folder(root, environment)
        );
        var originalLines = folders.ToDictionary(pair => pair.Key, pair => Lines(pair.Value, 3));
        writer.Dispose();
        using var state = JsonSerializer.SerializeToDocument(writer.GetStatus());
        Check(
            state.RootElement.GetProperty("capture_zip_complete").GetBoolean(),
            "closed writer hands off its lease and independently verified ZIPs complete; "
                + string.Join(" | ", reports)
        );
        foreach (var environment in new[] { "Official", "Private" })
        {
            var folder = folders[environment];
            var original = originalLines[environment];
            var destination = Path.ChangeExtension(folder, ".zip");
            Check(File.Exists(destination), "normal writer shutdown publishes its environment ZIP");
            using var zip = ZipFile.OpenRead(destination);
            Check(
                zip.Entries.Select(entry => entry.FullName)
                    .Order(StringComparer.Ordinal)
                    .SequenceEqual(
                        new[]
                        {
                            "api.jsonl",
                            "combat.jsonl",
                            "diagnostics.jsonl",
                            "manifest.json",
                            "raw-bodies.jsonl",
                        }
                    ),
                "writer archives preserve the five flat entries"
            );
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            Check(
                manifest.RootElement.GetProperty("shutdown_complete").GetBoolean()
                    && manifest
                        .RootElement.GetProperty("count_summary")
                        .GetProperty("run_id")
                        .GetString() == writer.RunId,
                "writer final archive metadata matches run and successful shutdown"
            );
            using var reader = new StreamReader(
                zip.GetEntry("diagnostics.jsonl")!.Open(),
                Encoding.UTF8,
                true
            );
            var packaged = new List<string>();
            while (reader.ReadLine() is { } line)
                packaged.Add(line);
            Check(
                packaged.Take(original.Length).SequenceEqual(original)
                    && packaged.Count == original.Length + 2,
                "packaged diagnostics match every exact committed source JSON line"
            );
            var summary = manifest
                .RootElement.GetProperty("count_summary")
                .GetProperty("streams")
                .EnumerateArray()
                .Single(stream => stream.GetProperty("name").GetString() == "diagnostics.jsonl");
            Check(
                summary.GetProperty("records").GetString()
                    == packaged.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "manifest physical diagnostic record count reconciles with committed source"
            );
            Check(
                !Directory.Exists(folder),
                "successful new package clears only its verified working source; "
                    + string.Join(" | ", reports)
                    + " "
                    + state.RootElement.GetRawText()
            );
        }
        Check(reports.IsEmpty, "valid writer shutdown has no archive handoff fault");
        Check(
            Directory.GetDirectories(Path.Combine(root, "Process")).Length == 0,
            "verified bootstrap ownership clears its fully transferred process spool after both environments complete"
        );
    }

    private static string[] ZipLines(string path, string entryName)
    {
        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry(entryName)!.Open(), Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static void SingleEncoding(string root)
    {
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        var counted = new ReadOnceValue();
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            "{\"name\":\"original\",\"authToken\":\"original-token\"}"
        )!;
        var body = new byte[] { 0, 1, 2, 255 };
        using (var batch = writer.BeginObservation())
        {
            writer.Emit(
                false,
                false,
                "storage_check",
                new
                {
                    counted,
                    node,
                    body,
                }
            );
            node["name"] = "mutated";
            body.AsSpan().Fill(42);
        }
        var folder = Folder(root, "Official");
        using var row = JsonDocument.Parse(
            Lines(folder, 3)
                .Single(line =>
                    line.Contains("\"kind\":\"storage_check\"", StringComparison.Ordinal)
                )
        );
        var data = row.RootElement.GetProperty("data");
        Check(
            counted.Reads == 1 && data.GetProperty("counted").GetProperty("value").GetInt32() == 73,
            "record payload getters execute exactly once before admission"
        );
        Check(
            data.GetProperty("node").GetProperty("name").GetString() == "original"
                && data.GetProperty("node").GetProperty("authToken").GetString() == "original-token"
                && data.GetProperty("body")
                    .GetBytesFromBase64()
                    .SequenceEqual(new byte[] { 0, 1, 2, 255 }),
            "UTF8 record is immutable against producer mutation before batch disposal"
        );
    }

    private sealed class ReadOnceValue
    {
        internal int Reads;
        public int value
        {
            get
            {
                if (++Reads != 1)
                    throw new InvalidOperationException("Value was serialized twice.");
                return 73;
            }
        }
    }

    private static void CleanupFailures(string root)
    {
        foreach (
            var fault in new[]
            {
                "third-delete",
                "abort-third-delete",
                "unknown-file",
                "process-only",
                "replacement-denied",
                "reparse-before-open",
                "late-tail",
                "late-checkpoint",
            }
        )
        {
            var caseRoot = Path.Combine(root, fault);
            var reports = new ConcurrentQueue<string>();
            SessionWriter? writer = null;
            var attempts = 0;
            var heldZipProtected = false;
            var sourceProtected = false;
            var reparseCreated = false;
            var external = Path.Combine(caseRoot, "protected-external.bin");
            Directory.CreateDirectory(caseRoot);
            File.WriteAllText(external, "protected-external-bytes");
            var hooks = new StorageHooks
            {
                Before = (action, path, _) =>
                {
                    if (
                        action == StorageAction.CleanupOpen
                        && Path.GetFileName(path) == "api.jsonl.gz"
                    )
                    {
                        if (fault == "late-tail")
                        {
                            using var tail = new FileStream(path, FileMode.Append);
                            tail.WriteByte(73);
                        }
                        if (fault == "late-checkpoint")
                        {
                            var slot = Directory
                                .GetFiles(Path.GetDirectoryName(path)!, "session-state.*.json")
                                .MaxBy(value =>
                                    StorageCheckpoint.Decode(File.ReadAllBytes(value)).Generation
                                )!;
                            File.AppendAllText(slot, " ");
                        }
                    }
                    if (
                        action == StorageAction.CleanupOpen
                        && fault == "reparse-before-open"
                        && Path.GetFileName(path) == "api.jsonl.gz"
                    )
                    {
                        File.Move(path, path + ".retained-original");
                        try
                        {
                            File.CreateSymbolicLink(path, external);
                        }
                        catch (IOException)
                        {
                            var target = Path.Combine(caseRoot, "external-directory");
                            Directory.CreateDirectory(target);
                            File.WriteAllText(
                                Path.Combine(target, "protected-target.txt"),
                                "protected-external-bytes"
                            );
                            var start = new ProcessStartInfo("cmd.exe")
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                                RedirectStandardOutput = true,
                                RedirectStandardError = true,
                            };
                            foreach (var argument in new[] { "/c", "mklink", "/J", path, target })
                                start.ArgumentList.Add(argument);
                            using var child = Process.Start(start)!;
                            child.StandardOutput.ReadToEnd();
                            child.StandardError.ReadToEnd();
                            Check(
                                child.WaitForExit(10000) && child.ExitCode == 0,
                                "synthetic reparse junction creation without symlink privilege"
                            );
                        }
                        reparseCreated = true;
                    }
                    if (action != StorageAction.CleanupDelete)
                        return;
                    var attempt = ++attempts;
                    var folder = Path.GetDirectoryName(path)!;
                    if (attempt == 1)
                    {
                        try
                        {
                            using var replace = new FileStream(
                                Path.ChangeExtension(folder, ".zip"),
                                FileMode.Open,
                                FileAccess.Write,
                                FileShare.ReadWrite | FileShare.Delete
                            );
                        }
                        catch (IOException)
                        {
                            heldZipProtected = true;
                        }
                        var file = Path.Combine(folder, "api.jsonl.gz");
                        try
                        {
                            File.Move(file, file + ".replacement");
                        }
                        catch (IOException)
                        {
                            sourceProtected = true;
                        }
                    }
                    if (attempt == 3 && fault == "third-delete")
                        throw new IOException(
                            "Synthetic deletion failure after two exact handles."
                        );
                    if (attempt == 3 && fault == "abort-third-delete")
                        writer!.Abort("Synthetic explicit abort during verified cleanup");
                },
            };
            writer = new SessionWriter(caseRoot, Metadata, Status, reports.Enqueue, hooks);
            if (fault == "process-only")
                writer.EmitBootstrap("storage_check", new { exact = "untransferred-process" });
            else
                writer.Emit(
                    false,
                    false,
                    "storage_check",
                    new { exact = "saved", complete = false }
                );
            var folderPath = Folder(caseRoot, fault == "process-only" ? "Process" : "Official");
            if (fault == "unknown-file")
                File.WriteAllText(
                    Path.Combine(folderPath, "user-protected.txt"),
                    "protected-extra"
                );
            writer.Dispose();
            if (fault == "abort-third-delete")
                WaitCompleted(writer);
            using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
            var source = status
                .RootElement.GetProperty("sources")
                .EnumerateArray()
                .Single(value =>
                    value.GetProperty("environment").GetString()
                    == (fault == "process-only" ? "Unknown" : "Official")
                );
            if (fault == "replacement-denied")
            {
                Check(
                    heldZipProtected && sourceProtected && !Directory.Exists(folderPath),
                    "ZIP and every exact source handle deny replacement during successful cleanup"
                );
                Check(
                    ZipLines(Path.ChangeExtension(folderPath, ".zip"), "diagnostics.jsonl")
                        .Any(line => line.Contains("\"complete\":false", StringComparison.Ordinal)),
                    "physical complete archive may clean working data while preserving incomplete gameplay truth"
                );
            }
            else
            {
                Check(
                    Directory.Exists(folderPath)
                        && !source.GetProperty("working_files_cleared").GetBoolean()
                        && source.GetProperty("working_files_retained_reason").GetString()
                            is not null,
                    "failed/unknown/untransferred cleanup retains exact disposition"
                );
                var actual = Directory
                    .GetFileSystemEntries(folderPath)
                    .Select(Path.GetFileName)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                Check(
                    source
                        .GetProperty("working_files_remaining")
                        .EnumerateArray()
                        .Select(value => value.GetString())
                        .Order(StringComparer.Ordinal)
                        .SequenceEqual(actual),
                    "cleanup status lists exactly the surviving working file names"
                );
                if (fault is "third-delete" or "abort-third-delete")
                    Check(
                        source.GetProperty("working_files_removed").GetInt32() == 2
                            && File.Exists(Path.ChangeExtension(folderPath, ".zip"))
                            && heldZipProtected
                            && sourceProtected,
                        "partial deletion records two removed files and keeps the complete verified ZIP as authority"
                    );
                if (fault == "unknown-file")
                    Check(
                        File.ReadAllText(Path.Combine(folderPath, "user-protected.txt"))
                            == "protected-extra",
                        "unaccounted user file prevents all source cleanup"
                    );
                if (fault == "reparse-before-open")
                    Check(
                        reparseCreated
                            && File.ReadAllText(external) == "protected-external-bytes"
                            && File.Exists(
                                Path.Combine(folderPath, "api.jsonl.gz.retained-original")
                            )
                            && source.GetProperty("working_files_removed").GetInt32() == 0,
                        "reparse replacement between inventory and open never hashes or deletes its outside target; "
                            + source.GetRawText()
                            + " "
                            + string.Join(" | ", reports)
                    );
                if (fault is "late-tail" or "late-checkpoint")
                    Check(
                        source.GetProperty("working_files_removed").GetInt32() == 0
                            && File.Exists(Path.ChangeExtension(folderPath, ".zip")),
                        "post-verification source tail/checkpoint changes prevent every destructive cleanup operation"
                    );
            }
        }
    }

    private static void CompactMeasurement(string root)
    {
        const int total = 256;
        var process = Process.GetCurrentProcess();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var writer = new SessionWriter(root, Metadata, Status, _ => { });
        for (var index = 0; index < total; index++)
            writer.Emit(
                false,
                true,
                "storage_compact_boundary",
                new
                {
                    index,
                    invocation_id = (index + 1).ToString(
                        System.Globalization.CultureInfo.InvariantCulture
                    ),
                    boundary_id = "synthetic",
                    important = new[] { index, index + 1, index + 2 },
                },
                systemId: "Battle"
            );
        var folder = Folder(root, "Official");
        var expandedBeforeClose = Verify(folder).Streams.Sum(stream => stream.ExtractedBytes);
        var quit = Stopwatch.StartNew();
        writer.Dispose();
        quit.Stop();
        clock.Stop();
        var zipPath = Path.ChangeExtension(folder, ".zip");
        using var zip = ZipFile.OpenRead(zipPath);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var expanded = manifest
            .RootElement.GetProperty("entries")
            .EnumerateArray()
            .Sum(entry => entry.GetProperty("bytes").GetInt64());
        Check(
            ZipLines(zipPath, "combat.jsonl").Length == total && !Directory.Exists(folder),
            "compact durable workload has exact count and removes only verified working files"
        );
        process.Refresh();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    compact_records = total,
                    expanded_before_close = expandedBeforeClose,
                    expanded_bytes = expanded,
                    archive_bytes = new FileInfo(zipPath).Length,
                    quit_ms = quit.Elapsed.TotalMilliseconds,
                    elapsed_ms = clock.Elapsed.TotalMilliseconds,
                    managed_allocated_bytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    cpu_seconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    working_set_bytes = process.WorkingSet64,
                    peak_working_set_bytes = process.PeakWorkingSet64,
                    scope = "generated compact writer workload; old live graph volume is evidence only, native game FPS not measured",
                }
            )
        );
    }

    private static void BoundedOldRecovery(string root)
    {
        var official = Path.Combine(root, "Official");
        Directory.CreateDirectory(official);
        var old = Path.Combine(official, "capture-preexisting.partial");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "protected-old-source.txt"), "original-protected");
        var oldZip = Path.ChangeExtension(old, ".zip");
        File.WriteAllText(oldZip, "original-protected-zip-not-reverified");
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        writer.Emit(false, false, "storage_check", new { exact = "new-current" });
        writer.Dispose();
        using var status = JsonSerializer.SerializeToDocument(writer.GetStatus());
        Check(
            status.RootElement.GetProperty("old_packaged_sources_retained_unverified").GetInt64()
                == 1
                && status.RootElement.GetProperty("old_recovery_pending").GetInt64() == 0
                && status.RootElement.GetProperty("old_recovery_failures").GetInt64() == 0,
            "older folders with sibling ZIP are retained and explicitly unverified without repeated shutdown passes"
        );
        Check(
            File.ReadAllText(oldZip) == "original-protected-zip-not-reverified"
                && File.ReadAllText(Path.Combine(old, "protected-old-source.txt"))
                    == "original-protected",
            "new runtime cleanup never modifies protected older source or finished ZIP"
        );
        var unfinished = Path.Combine(root, "unfinished");
        foreach (var name in new[] { "capture-one.partial", "capture-two.partial" })
        {
            var folder = Path.Combine(unfinished, "Official", name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "session-state.0.json"), "invalid checkpoint");
        }
        using var one = new SessionWriter(unfinished, Metadata, Status, _ => { });
        AwaitRecovery(
            () =>
            {
                using var result = JsonSerializer.SerializeToDocument(one.GetStatus());
                return result.RootElement.GetProperty("old_recovery_failures").GetInt64() == 1;
            },
            "one unfinished old folder is attempted during startup idle"
        );
        Check(
            !SpinWait.SpinUntil(
                () =>
                {
                    using var result = JsonSerializer.SerializeToDocument(one.GetStatus());
                    return result.RootElement.GetProperty("old_recovery_failures").GetInt64() > 1;
                },
                TimeSpan.FromMilliseconds(600)
            ),
            "idle recovery is limited to one old candidate per process"
        );
        one.Dispose();
        using var final = JsonSerializer.SerializeToDocument(one.GetStatus());
        Check(
            final.RootElement.GetProperty("old_recovery_pending").GetInt64() == 2
                && final.RootElement.GetProperty("old_recovery_failures").GetInt64() == 1,
            "quit never expands the bounded old recovery attempt or conceals pending candidates"
        );
    }

    private static void ParallelAndMeasurement(string root)
    {
        const int total = 2050;
        var elapsed = new double[total];
        var process = Process.GetCurrentProcess();
        var allocation = GC.GetTotalAllocatedBytes(true);
        var cpu = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        using var writer = new SessionWriter(root, Metadata, Status, _ => { });
        Parallel.For(
            0,
            total,
            new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i =>
            {
                var start = Stopwatch.GetTimestamp();
                writer.Emit(
                    false,
                    true,
                    "combat_enter",
                    new { index = i, payload = new byte[32768] },
                    systemId: "Battle"
                );
                elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
        );
        var folder = Folder(root, "Official");
        Check(Verify(folder).Streams[1].Records == total, "concurrent records exactly once");
        var ids = new HashSet<int>();
        var sequences = new HashSet<long>();
        foreach (var line in Lines(folder, 1))
        {
            using var row = JsonDocument.Parse(line);
            Check(
                ids.Add(row.RootElement.GetProperty("data").GetProperty("index").GetInt32()),
                "no duplicate producer index"
            );
            Check(
                sequences.Add(row.RootElement.GetProperty("sequence").GetInt64()),
                "no duplicate envelope sequence"
            );
        }
        writer.Dispose();
        clock.Stop();
        Array.Sort(elapsed);
        process.Refresh();
        Console.WriteLine(
            JsonSerializer.Serialize(
                new
                {
                    synthetic_records = total,
                    callback_p95_ms = elapsed[(int)(total * .95)],
                    callback_p99_ms = elapsed[(int)(total * .99)],
                    callback_max_ms = elapsed[^1],
                    elapsed_ms = clock.Elapsed.TotalMilliseconds,
                    managed_allocated_bytes = GC.GetTotalAllocatedBytes(true) - allocation,
                    working_set_bytes = process.WorkingSet64,
                    peak_working_set_bytes = process.PeakWorkingSet64,
                    cpu_seconds = (process.TotalProcessorTime - cpu).TotalSeconds,
                    disk_bytes = Directory
                        .EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Sum(path => new FileInfo(path).Length),
                    measurement_boundary = "managed .NET10 synthetic; local current machine, not native/HDD8GB/FPS acceptance",
                }
            )
        );
    }

    private static StorageCheckpointPayload Verify(string folder, bool allowSuffix = false)
    {
        var checkpoints = Directory
            .GetFiles(folder, "session-state.*.json")
            .Select(ReadShared)
            .Where(bytes => bytes.Length != 0)
            .Select(bytes => StorageCheckpoint.Decode(bytes))
            .ToArray();
        var state =
            checkpoints.MaxBy(value => value.Generation)
            ?? throw new InvalidOperationException("No committed checkpoint.");
        foreach (var stamp in state.Streams)
        {
            var path = Path.Combine(folder, stamp.Name);
            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite
            );
            Check(
                allowSuffix ? input.Length >= stamp.Bytes : input.Length == stamp.Bytes,
                "actual compressed length matches committed prefix"
            );
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var scratch = new byte[65536];
            var remaining = stamp.Bytes;
            while (remaining != 0)
            {
                var prefixCount = (int)Math.Min(remaining, scratch.Length);
                input.ReadExactly(scratch.AsSpan(0, prefixCount));
                hash.AppendData(scratch.AsSpan(0, prefixCount));
                remaining -= prefixCount;
            }
            Check(
                Convert.ToHexString(hash.GetHashAndReset()) == stamp.Sha256,
                "independent compressed prefix hash"
            );
            input.Position = 0;
            using var prefix = new PrefixInput(input, stamp.Bytes);
            using var gzip = new GZipStream(prefix, CompressionMode.Decompress);
            var buffer = new byte[65536];
            long bytes = 0,
                records = 0;
            int count;
            while ((count = gzip.Read(buffer)) != 0)
            {
                bytes += count;
                for (var i = 0; i < count; i++)
                    if (buffer[i] == '\n')
                        records++;
            }
            Check(
                bytes == stamp.ExtractedBytes && records == stamp.Records,
                "independent extracted counts"
            );
        }
        return state;
    }

    // The live writer keeps checkpoint slots open, as it does its streams.
    private static byte[] ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static string[] Lines(string folder, int stream)
    {
        using var file = new FileStream(
            Path.Combine(folder, StorageCheckpoint.FileNames[stream]),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite
        );
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, new UTF8Encoding(false, true));
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    private static string Folder(string root, string environment) =>
        Directory.GetDirectories(Path.Combine(root, environment), "*.partial").Single();

    private static void Finish(Task task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

    private static void WaitCompleted(SessionWriter writer)
    {
        Check(
            SpinWait.SpinUntil(
                () =>
                {
                    using var state = JsonSerializer.SerializeToDocument(writer.GetStatus());
                    return state.RootElement.GetProperty("completed").GetBoolean();
                },
                TimeSpan.FromSeconds(10)
            ),
            "worker closes after abort"
        );
    }

    private static void Reject(Action action, string message)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            return;
        }
        throw new InvalidOperationException("Accepted " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("Storage check failed: " + message);
    }

    private sealed class PrefixInput(Stream source, long remaining) : Stream
    {
        public override int Read(Span<byte> buffer)
        {
            var count = source.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            remaining -= count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtelierCapture.Archive;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class ArchiveChecks
{
    private static int assertions;

    internal static void Run()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "synthetic-archive-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(root);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        try
        {
            Normal(root, 5);
            Normal(root, 4);
            Recovery(root);
            Corruption(root);
            ExistingZipControls(root);
            CompleteLargeRecords(root);
            SemanticControls(root);
            SemanticRouteOwners(root);
            RawRequestOwnership(root);
            NativeDecoderStages(root);
            VerifiedProofs(root);
            BootstrapProofs(root);
            WarningAggregation(root);
            ErrorAggregation(root);
            ErrorParserControls();
            BootstrapErrors(root);
            UnboundNativeCounts(root);
            ArchiveExpansionWorkload(root);
            ParserControls();
            OverlappedCompression();
            IdentityLedger(root);
            Console.WriteLine(
                $"ARCHIVE_CHECKS_PASS assertions={assertions} elapsed_ms={started.ElapsedMilliseconds} runtime={Environment.Version} allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated} peak_working_set={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64}"
            );
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void SemanticRouteOwners(string root)
    {
        var starts = new[]
        {
            "/quest/battle/start",
            "/quest/battle/rental_party_start",
            "/quest/battle/solo_raid_battle_start",
            "/quest/battle/total_battle_start",
            "/exploration/battle_start",
            "/gacha/battle_start",
        };
        foreach (var route in starts)
            Require(
                RouteCountCatalog.SystemId(route) == "Battle",
                "BattleStart response semantic owner"
            );
        foreach (
            var route in new[]
            {
                "/party/bulk_update",
                "/rental_party/character_equip",
                "/equipment_preset/equip",
            }
        )
            Require(
                RouteCountCatalog.SystemId(route) == "Parties",
                "formation mutator primary owner"
            );
        foreach (
            var route in new[]
            {
                "/advertising_id/update",
                "/ktid/web_token",
                "/web_session/token",
                "/status",
            }
        )
            Require(RouteCountCatalog.SystemId(route) == "Foundation", "service primary owner");
        Require(
            RouteCountCatalog.SystemId("/user/log_in") == "Accounts",
            "full UserData does not duplicate owners"
        );
        Require(
            RouteCountCatalog.SystemId("/quest/battle/skip") == "Quests",
            "skip reward has no BattleStart contract"
        );
        Require(
            RouteCountCatalog.SystemId("/debug/battle/finish") == "debug",
            "debug not gameplay credit"
        );
        Require(RouteCountCatalog.SystemId("/new/route") is null, "unknown route remains unknown");
        var fixture = new Fixture(root, "battleStartAttackFinish", 5);
        fixture.Lines[0].Clear();
        fixture.Lines[1].Clear();
        fixture.Lines[2].Clear();
        fixture.Lines[3].RemoveAt(1);
        var sequence = 0;
        foreach (
            var route in starts.Concat(
                new[]
                {
                    "/battle/attack",
                    "/battle/finish",
                    "/party/bulk_update",
                    "/user/log_in",
                    "/status",
                    "/quest/battle/skip",
                }
            )
        )
        {
            var id = (sequence / 2 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var kind in new[] { "request", "response" })
                fixture
                    .Lines[0]
                    .Add(
                        JsonSerializer.Serialize(
                            new
                            {
                                schema = 5,
                                run_id = fixture.Run,
                                environment = "Official",
                                sequence = ++sequence,
                                utc = "2026-10-14T00:00:00Z",
                                elapsed_ticks = 0,
                                thread = 1,
                                stream = "api",
                                kind,
                                system_id = RouteCountCatalog.SystemId(route),
                                data = new
                                {
                                    request_id = id,
                                    attempt_id = id,
                                    route,
                                },
                            }
                        )
                    );
        }
        var end = JsonNode.Parse(fixture.Lines[3][^1])!;
        end["events"] = sequence;
        fixture.Lines[3][^1] = end.ToJsonString();
        fixture.Save();
        Require(CaptureArchive.Pack(fixture.Folder, false), "mixed semantic-owner package");
        using var zip = ZipFile.OpenRead(fixture.Zip);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var summary = manifest.RootElement.GetProperty("count_summary");
        var systems = summary.GetProperty("systems").EnumerateArray().ToArray();
        Require(
            systems
                .Single(row => row.GetProperty("system_id").GetString() == "Battle")
                .GetProperty("request_records")
                .GetString() == "8",
            "six starts attack finish attributed Battle"
        );
        Require(
            summary.GetProperty("api").GetProperty("request_records").GetString() == "12",
            "one request per API observation"
        );
        Require(
            systems.Sum(row =>
                long.Parse(
                    row.GetProperty("request_records").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture
                )
            ) == 12,
            "primary system totals do not duplicate resource consumers"
        );
    }

    private static void SemanticControls(string root)
    {
        var fixture = new Fixture(root, "semantic", 5);
        var operation = Guid.NewGuid().ToString("D");
        for (var i = 0; i < 2; i++)
        {
            var record = JsonNode.Parse(fixture.Lines[0][i])!;
            record["data"]!["operation_id"] = operation;
            record["data"]!["identity_proof"] = "native_descriptor";
            fixture.Lines[0][i] = record.ToJsonString();
        }
        for (var i = 0; i < 2; i++)
        {
            var record = JsonNode.Parse(fixture.Lines[1][i])!;
            record["kind"] = i == 0 ? "native_enter" : "native_exception";
            record["system_id"] = "Atelier";
            record["data"]!["system_id"] = "Atelier";
            fixture.Lines[1][i] = record.ToJsonString();
        }
        fixture
            .Lines[3]
            .Insert(
                1,
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "process_bootstrap",
                        utc = "2026-10-14T00:00:00Z",
                        data = new
                        {
                            original_record = new
                            {
                                sequence = 1,
                                kind = "request",
                                environment = "Unknown",
                                run_id = fixture.Run,
                            },
                        },
                    }
                )
            );
        fixture.Save();
        Require(CaptureArchive.Pack(fixture.Folder, false), "typed semantic package");
        using var zip = ZipFile.OpenRead(fixture.Zip);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var summary = manifest.RootElement.GetProperty("count_summary");
        Require(
            summary.GetProperty("api").GetProperty("distinct_observed_operation_ids").GetString()
                == "1",
            "proven observed operation IDs"
        );
        Require(
            summary.GetProperty("native").GetProperty("calls_threw").GetString() == "1",
            "exception terminal one call"
        );
        Require(
            summary.GetProperty("streams")[3].GetProperty("marker_records").GetString() == "3",
            "bootstrap outer marker counted once"
        );
        Require(
            summary.GetProperty("api").GetProperty("request_records").GetString() == "1",
            "nested bootstrap request excluded"
        );
        var other = new Fixture(root, "independent", 5);
        other.Save();
        Require(CaptureArchive.Pack(other.Folder, false), "second independent capture");
        using var second = ZipFile.OpenRead(other.Zip);
        using var secondManifest = JsonDocument.Parse(second.GetEntry("manifest.json")!.Open());
        Require(
            secondManifest
                .RootElement.GetProperty("count_summary")
                .GetProperty("api")
                .GetProperty("request_records")
                .GetString() == "1",
            "no cumulative capture total"
        );
        var incomplete = new Fixture(root, "explicitincomplete", 5);
        incomplete
            .Lines[3]
            .Insert(
                1,
                JsonSerializer.Serialize(
                    new
                    {
                        schema = 5,
                        run_id = incomplete.Run,
                        environment = "Official",
                        sequence = 8,
                        utc = "2026-10-14T00:00:00Z",
                        elapsed_ticks = 0,
                        thread = 1,
                        stream = "diagnostics",
                        system_id = "Recorder",
                        kind = "api_capture_incomplete",
                        data = new
                        {
                            capture_status = "incomplete",
                            reason = "native_schema_unverified",
                        },
                    }
                )
            );
        var end = JsonNode.Parse(incomplete.Lines[3][^1])!;
        end["events"] = 8;
        incomplete.Lines[3][^1] = end.ToJsonString();
        incomplete.Save();
        Require(CaptureArchive.Pack(incomplete.Folder, false), "explicit incomplete preserved");
        using var failedZip = ZipFile.OpenRead(incomplete.Zip);
        using var failedManifest = JsonDocument.Parse(failedZip.GetEntry("manifest.json")!.Open());
        Require(
            failedManifest
                .RootElement.GetProperty("count_summary")
                .GetProperty("completeness")
                .GetProperty("state")
                .GetString() == "incomplete",
            "explicit incomplete independent of final status"
        );
    }

    private static void RawRequestOwnership(string root)
    {
        foreach (
            var test in new[]
            {
                (Name: "matched", Schema: 5, Reason: (string?)null),
                (Name: "request", Schema: 5, Reason: "raw_body_without_request"),
                (Name: "attempt", Schema: 5, Reason: "raw_body_without_request"),
                (Name: "route", Schema: 5, Reason: "raw_body_request_owner_mismatch"),
                (Name: "system", Schema: 5, Reason: "raw_body_request_owner_mismatch"),
                (
                    Name: "unknownRequest",
                    Schema: 5,
                    Reason: "raw_body_request_identity_unavailable"
                ),
                (
                    Name: "unknownAttempt",
                    Schema: 5,
                    Reason: "raw_body_request_identity_unavailable"
                ),
                (Name: "unknownRoute", Schema: 5, Reason: "raw_body_request_route_unverified"),
                (Name: "legacy", Schema: 4, Reason: "raw_body_attempt_identity_unavailable"),
                (Name: "legacyOrphan", Schema: 4, Reason: "raw_body_without_request"),
            }
        )
        {
            var fixture = new Fixture(root, "bodyOwner" + test.Name, test.Schema);
            if (test.Schema == 4)
                foreach (var stream in new[] { 0, 2, 3 })
                    for (var i = 0; i < fixture.Lines[stream].Count; i++)
                    {
                        var record = JsonNode.Parse(fixture.Lines[stream][i])!.AsObject();
                        if (record["data"] is not JsonObject data)
                            continue;
                        data["request_id"] = 1;
                        data.Remove("attempt_id");
                        fixture.Lines[stream][i] = record.ToJsonString();
                    }
            var raw = JsonNode.Parse(fixture.Lines[2][0])!.AsObject();
            var body = raw["data"]!.AsObject();
            switch (test.Name)
            {
                case "request":
                    body["request_id"] = "999";
                    break;
                case "attempt":
                    body["attempt_id"] = "2";
                    break;
                case "route":
                    body["route"] = "/battle/finish";
                    break;
                case "system":
                    raw["system_id"] = "Atelier";
                    break;
                case "unknownRequest":
                    body.Remove("request_id");
                    break;
                case "unknownAttempt":
                    body.Remove("attempt_id");
                    break;
                case "unknownRoute":
                    body.Remove("route");
                    break;
                case "legacyOrphan":
                    body["request_id"] = 999;
                    break;
            }
            fixture.Lines[2][0] = raw.ToJsonString();
            fixture.Save();
            Require(
                CaptureArchive.Pack(fixture.Folder, false),
                "raw owner evidence preserved " + test.Name
            );
            using var zip = ZipFile.OpenRead(fixture.Zip);
            using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
            var summary = manifest.RootElement.GetProperty("count_summary");
            var reasons = summary
                .GetProperty("completeness")
                .GetProperty("reasons")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray();
            if (test.Reason is null)
                Require(
                    !reasons.Any(value => value.StartsWith("raw_body_", StringComparison.Ordinal)),
                    "matched raw request/attempt/route/system joins without incomplete association"
                );
            else
            {
                Require(
                    reasons.Contains(test.Reason),
                    "specific raw association reason " + test.Name
                );
                Require(
                    summary.GetProperty("completeness").GetProperty("state").GetString()
                        == "incomplete",
                    "unverified raw association is incomplete " + test.Name
                );
            }
            if (test.Name == "legacy")
                Require(
                    !reasons.Contains("raw_body_without_request"),
                    "legacy numeric request ID joins saved request"
                );
            Require(
                summary.GetProperty("api").GetProperty("request_records").GetString() == "1",
                "raw references never create requests " + test.Name
            );
            Require(
                summary.GetProperty("api").GetProperty("physical_attempts").ValueKind
                    == JsonValueKind.Null,
                "raw relation does not certify physical attempts " + test.Name
            );
            Require(
                summary.GetProperty("streams")[2].GetProperty("records").GetString() == "2",
                "both raw stages remain counted " + test.Name
            );
            using var reader = new StreamReader(zip.GetEntry("raw-bodies.jsonl")!.Open());
            Require(
                reader.ReadToEnd() == string.Join("\n", fixture.Lines[2]) + "\n",
                "raw ownership failures retain exact available evidence " + test.Name
            );
        }
        foreach (var field in new[] { "request_id", "attempt_id" })
        {
            var fixture = new Fixture(root, "bodyNumeric" + field, 5);
            var raw = JsonNode.Parse(fixture.Lines[2][0])!;
            raw["data"]![field] = 1;
            fixture.Lines[2][0] = raw.ToJsonString();
            fixture.Save();
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                "schema-5 raw ID must be string " + field
            );
            Require(
                File.Exists(Path.Combine(fixture.Folder, "raw-bodies.jsonl.gz")),
                "malformed causal type preserves original " + field
            );
        }
    }

    private static void NativeDecoderStages(string root)
    {
        var fixture = new Fixture(root, "nativeDecoderStages", 5);
        byte[] originalBytes = [0, 255, 128, 10, 0, 127, 254];
        var sequence = 7;
        foreach (var stage in new[] { "native_decoder_result", "client_decoder_result" })
        {
            var raw = JsonNode.Parse(fixture.Lines[2][1])!;
            raw["sequence"] = ++sequence;
            var data = raw["data"]!;
            data["stage"] = stage;
            data["body_id"] = "1:response:" + stage;
            data["body"] = Convert.ToBase64String(originalBytes);
            data["bytes"] = originalBytes.Length;
            data["sha256"] = Hash(originalBytes);
            fixture.Lines[2].Add(raw.ToJsonString());
        }
        var parse = JsonNode.Parse(fixture.Lines[2][^2])!;
        parse["sequence"] = ++sequence;
        parse["stream"] = "diagnostics";
        parse["kind"] = "native_parse_input";
        parse["data"]!["stage"] = "native_parse_input";
        parse["data"]!["source"] = "original_native_MergeFrom_argument";
        parse["data"]!.AsObject().Remove("body_id");
        fixture.Lines[3].Insert(2, parse.ToJsonString());
        var end = JsonNode.Parse(fixture.Lines[3][^1])!;
        end["events"] = sequence;
        fixture.Lines[3][^1] = end.ToJsonString();
        fixture.Save();
        Require(CaptureArchive.Pack(fixture.Folder, false), "native decoder stages package");
        using var zip = ZipFile.OpenRead(fixture.Zip);
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var summary = manifest.RootElement.GetProperty("count_summary");
        var stages = summary.GetProperty("raw_bodies").EnumerateArray().ToArray();
        Require(
            stages.Length == 4,
            "four exact body stage buckets, no diagnostic or unknown bucket"
        );
        foreach (
            var stage in new[]
            {
                "request_entity",
                "response_entity",
                "native_decoder_result",
                "client_decoder_result",
            }
        )
            Require(
                stages
                    .Single(row =>
                        row.GetProperty("direction_stage").GetString()
                        == (stage == "request_entity" ? "request:" : "response:") + stage
                    )
                    .GetProperty("records")
                    .GetString() == "1",
                "exact decoder-compatible stage count " + stage
            );
        foreach (var unit in new[] { "request_records", "response_records", "response_pairs" })
            Require(
                summary.GetProperty("api").GetProperty(unit).GetString() == "1",
                "decoder/parse stages do not add API units " + unit
            );
        Require(
            summary.GetProperty("streams")[2].GetProperty("records").GetString() == "4",
            "only four raw body records"
        );
        Require(
            summary.GetProperty("streams")[3].GetProperty("envelope_records").GetString() == "2",
            "native MergeFrom input is one additional diagnostic envelope"
        );
        Require(
            !summary
                .GetProperty("completeness")
                .GetProperty("reasons")
                .EnumerateArray()
                .Any(reason =>
                    reason.GetString()!.StartsWith("raw_body_", StringComparison.Ordinal)
                ),
            "decoder stages join their actual saved request owner"
        );
        using var rawReader = new StreamReader(zip.GetEntry("raw-bodies.jsonl")!.Open());
        Require(
            rawReader.ReadToEnd() == string.Join("\n", fixture.Lines[2]) + "\n",
            "complete original decoder byte stages retained exactly"
        );
        using var diagnosticReader = new StreamReader(zip.GetEntry("diagnostics.jsonl")!.Open());
        Require(
            diagnosticReader.ReadToEnd() == string.Join("\n", fixture.Lines[3]) + "\n",
            "complete original native MergeFrom input bytes retained as diagnostic"
        );
    }

    private static void CompleteLargeRecords(string root)
    {
        var fixture = new Fixture(root, "large", 5);
        var record = JsonNode.Parse(fixture.Lines[2][0])!;
        var body = new byte[33 * 1024 * 1024 + 7];
        body[^1] = 123;
        record["data"]!["body"] = Convert.ToBase64String(body);
        record["data"]!["bytes"] = body.LongLength;
        record["data"]!["sha256"] = Hash(body);
        fixture.Lines[2][0] = record.ToJsonString();
        fixture.Save();
        Require(
            CaptureArchive.Pack(fixture.Folder, false),
            "complete body larger than legacy32MiB"
        );
        using var zip = ZipFile.OpenRead(fixture.Zip);
        Require(
            zip.GetEntry("raw-bodies.jsonl")!.Length > 44 * 1024 * 1024,
            "large raw line preserved"
        );
        var deep =
            "{\"data\":{\"deep\":" + new string('[', 2048) + "0" + new string(']', 2048) + "}}\n";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(deep));
        using var reader = new RecordReader(input);
        Require(
            reader.Read() is not null && reader.Read() is null,
            "complete deep payload without traversal limit"
        );
    }

    private static void AddDiagnostic(Fixture fixture, string kind, object data, ref long sequence)
    {
        fixture
            .Lines[3]
            .Insert(
                fixture.Lines[3].Count - 1,
                JsonSerializer.Serialize(
                    new
                    {
                        schema = 5,
                        run_id = fixture.Run,
                        environment = "Official",
                        sequence = ++sequence,
                        utc = "2026-10-14T00:00:00Z",
                        elapsed_ticks = 0,
                        thread = 1,
                        stream = "diagnostics",
                        kind,
                        system_id = "Recorder",
                        data,
                    }
                )
            );
        var end = JsonNode.Parse(fixture.Lines[3][^1])!;
        end["events"] = sequence;
        fixture.Lines[3][^1] = end.ToJsonString();
    }

    private static void VerifiedProofs(string root)
    {
        var fixture = new Fixture(root, "freshProof", 5);
        fixture.Save();
        using (var proof = CaptureArchive.PackVerified(fixture.Folder, false)!)
        {
            Require(
                proof is not null
                    && proof.NewlyCreated
                    && proof.NormalShutdown
                    && !proof.Recovered
                    && !proof.Aborted
                    && proof.TailBytes == 0,
                "fresh physical-close proof"
            );
            Require(
                proof!.RunId == fixture.Run
                    && proof.Environment == "Official"
                    && proof.CheckpointGeneration == 1
                    && proof.Streams.SequenceEqual(fixture.Stamps),
                "fresh proof binds exact session/checkpoint/vector"
            );
            Require(
                proof.Entries.Count == 4 && proof.ArchivePath == Path.GetFullPath(fixture.Zip),
                "fresh proof binds exact five-entry archive"
            );
            var expanded = fixture.Stamps.Select(stamp => stamp.ExtractedBytes).SumChecked();
            Require(
                proof.SourceBytesExpanded == expanded && proof.ZipBytesExpanded == expanded,
                "fresh package expands each full source and ZIP stream once"
            );
            Fails(
                () =>
                {
                    using var writer = new FileStream(fixture.Zip, FileMode.Open, FileAccess.Write);
                },
                "proof prevents ZIP write"
            );
            Fails(() => File.Delete(fixture.Zip), "proof prevents ZIP deletion");
            Require(Directory.Exists(fixture.Folder), "archive proof deletes no source folder");
        }
        using (var existing = CaptureArchive.PackVerified(fixture.Folder, false)!)
        {
            Require(
                !existing.NewlyCreated && existing.NormalShutdown,
                "existing path is never a fresh cleanup proof"
            );
            Require(
                existing.SourceBytesExpanded
                    == existing.Entries.Select(entry => entry.Bytes).SumChecked()
                    && existing.ZipBytesExpanded == checked(existing.SourceBytesExpanded * 2),
                "existing ZIP retains independent recount plus full source comparison"
            );
        }
        var legacy = new Fixture(root, "legacyProof", 4);
        legacy.Save();
        using (var proof = CaptureArchive.PackVerified(legacy.Folder, false)!)
            Require(
                proof.Streams.Count == 0 && proof.CheckpointGeneration is null,
                "legacy proof cannot authorize current cleanup"
            );
        var tail = new Fixture(root, "tailProof", 5);
        tail.Save();
        using (
            var output = new FileStream(Path.Combine(tail.Folder, "api.jsonl.gz"), FileMode.Append)
        )
            output.Write([1, 2]);
        using (var proof = CaptureArchive.PackVerified(tail.Folder, false)!)
            Require(
                proof.Recovered && !proof.NormalShutdown && proof.TailBytes == 2,
                "tail disqualifies physical cleanup"
            );
        var aborted = new Fixture(root, "abortProof", 5);
        aborted.Save();
        File.WriteAllText(
            Path.Combine(aborted.Folder, "abort.json"),
            JsonSerializer.Serialize(
                new
                {
                    run_id = aborted.Run,
                    environment = "Official",
                    reason = "synthetic_abort",
                }
            )
        );
        using (var proof = CaptureArchive.PackVerified(aborted.Folder, false)!)
            Require(
                proof.Aborted && !proof.NormalShutdown && proof.Recovered,
                "abort disqualifies physical cleanup"
            );
    }

    private static void WarningAggregation(string root)
    {
        var fixture = new Fixture(root, "warningMany", 5);
        long sequence = 7;
        var first = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var last = first;
        AddDiagnostic(
            fixture,
            "runtime_log",
            new
            {
                warning_id = "9",
                warning_occurrences = 1L,
                warning_first_utc = first,
                warning_last_utc = first,
                warning_origin = "Official",
                severity = "Warning",
                message = "synthetic full warning anchor",
                utc = first,
                event_id = "9",
            },
            ref sequence
        );
        var reference = first;
        for (long occurrence = 2; occurrence <= 9001; occurrence++)
        {
            var utc = reference.AddTicks(occurrence % 2 == 0 ? -occurrence : occurrence);
            first = first < utc ? first : utc;
            last = last > utc ? last : utc;
            AddDiagnostic(
                fixture,
                "runtime_warning_repeat",
                new
                {
                    warning_id = "9",
                    origin = "Official",
                    delta = 1L,
                    occurrences = occurrence,
                    first_utc = first,
                    last_utc = last,
                    utc,
                    event_id = (occurrence + 9).ToString(),
                    thread = 1,
                    phase = "runtime",
                },
                ref sequence
            );
        }
        AddDiagnostic(
            fixture,
            "runtime_warning_summary",
            new
            {
                warning_id = "9",
                origin = "Official",
                occurrences = 9001L,
                first_utc = first,
                last_utc = last,
            },
            ref sequence
        );
        fixture.Save();
        Require(
            CaptureArchive.Pack(fixture.Folder, false),
            "multi-chunk warnings with out-of-order timestamps"
        );
        using (var zip = ZipFile.OpenRead(fixture.Zip))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            var warning = manifest.RootElement.GetProperty("warning_summary");
            Require(
                warning.GetProperty("anchor_records").GetString() == "1"
                    && warning.GetProperty("repeat_records").GetString() == "9000"
                    && warning.GetProperty("summary_records").GetString() == "1"
                    && warning.GetProperty("occurrences").GetString() == "9001",
                "warning logical/physical exact units"
            );
            Require(
                warning.GetProperty("first_utc").GetString() == first.ToString("O")
                    && warning.GetProperty("last_utc").GetString() == last.ToString("O"),
                "warning cumulative min/max exact"
            );
            Require(
                manifest
                    .RootElement.GetProperty("count_summary")
                    .GetProperty("api")
                    .GetProperty("request_records")
                    .GetString() == "1",
                "repeats do not inflate requests"
            );
        }
        foreach (
            var fault in new[]
            {
                "orphan",
                "duplicate",
                "ordinal",
                "timestamp",
                "summary",
                "severity",
                "missingSummary",
            }
        )
        {
            var sample = new Fixture(root, "warning" + fault, 5);
            sequence = 7;
            var utc = reference;
            AddDiagnostic(
                sample,
                "runtime_log",
                new
                {
                    warning_id = "1",
                    warning_occurrences = 1L,
                    warning_first_utc = utc,
                    warning_last_utc = utc,
                    warning_origin = "Official",
                    severity = fault == "severity" ? "Error" : "Warning",
                    utc,
                    event_id = "1",
                    message = "synthetic",
                },
                ref sequence
            );
            AddDiagnostic(
                sample,
                "runtime_warning_repeat",
                new
                {
                    warning_id = fault == "orphan" ? "2" : "1",
                    origin = "Official",
                    delta = 1L,
                    occurrences = fault == "ordinal" ? 3L : 2L,
                    first_utc = fault == "timestamp" ? utc.AddTicks(-1) : utc,
                    last_utc = utc,
                    utc,
                    event_id = "2",
                    thread = 1,
                    phase = "runtime",
                },
                ref sequence
            );
            if (fault == "duplicate")
                AddDiagnostic(
                    sample,
                    "runtime_warning_repeat",
                    new
                    {
                        warning_id = "1",
                        origin = "Official",
                        delta = 1L,
                        occurrences = 2L,
                        first_utc = utc,
                        last_utc = utc,
                        utc,
                        event_id = "3",
                        thread = 1,
                        phase = "runtime",
                    },
                    ref sequence
                );
            if (fault != "missingSummary")
                AddDiagnostic(
                    sample,
                    "runtime_warning_summary",
                    new
                    {
                        warning_id = "1",
                        origin = "Official",
                        occurrences = fault == "summary" ? 3L : 2L,
                        first_utc = utc,
                        last_utc = utc,
                    },
                    ref sequence
                );
            sample.Save();
            if (fault == "missingSummary")
            {
                Require(
                    CaptureArchive.Pack(sample.Folder, false),
                    "missing summary retains exact saved occurrences"
                );
                using var zip = ZipFile.OpenRead(sample.Zip);
                using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                Require(
                    manifest
                        .RootElement.GetProperty("warning_summary")
                        .GetProperty("occurrences")
                        .GetString() == "2"
                        && manifest
                            .RootElement.GetProperty("warning_summary")
                            .GetProperty("missing_final_summaries")
                            .GetString() == "1",
                    "missing final adds neither loss nor occurrences"
                );
            }
            else
                Fails(
                    () => CaptureArchive.Pack(sample.Folder, false),
                    "warning relation negative " + fault
                );
        }
    }

    private static Dictionary<string, object?> ErrorAnchor(
        string id,
        string message,
        DateTimeOffset utc,
        string origin = "Official"
    ) =>
        new(StringComparer.Ordinal)
        {
            ["error_id"] = id,
            ["error_occurrences"] = 1L,
            ["error_first_utc"] = utc,
            ["error_last_utc"] = utc,
            ["error_origin"] = origin,
            ["source"] = "Unity",
            ["severity"] = "Error",
            ["message"] = message,
            ["exception_type"] = null,
            ["stack"] = null,
            ["caused_by_event_id"] = null,
            ["failed_listener"] = null,
            ["utc"] = utc,
            ["event_id"] = id,
            ["thread"] = 1,
            ["phase"] = "runtime",
            ["warning_id"] = null,
            ["warning_occurrences"] = null,
            ["warning_first_utc"] = null,
            ["warning_last_utc"] = null,
            ["warning_origin"] = null,
        };

    private static Dictionary<string, object?> ErrorRepeat(string id, DateTimeOffset utc) =>
        new(StringComparer.Ordinal)
        {
            ["error_id"] = id,
            ["origin"] = "Official",
            ["occurrences"] = 2L,
            ["utc"] = utc,
            ["event_id"] = "2",
            ["thread"] = 1,
            ["phase"] = "runtime",
        };

    private static void ErrorAggregation(string root)
    {
        var fixture = new Fixture(root, "errorMany", 5);
        long sequence = 7;
        var reference = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        var first = reference;
        var last = reference;
        AddDiagnostic(
            fixture,
            "runtime_log",
            ErrorAnchor("9", MaterialNre.StandardMessage, reference),
            ref sequence
        );
        for (long occurrence = 2; occurrence <= 9001; occurrence++)
        {
            var utc = reference.AddTicks(occurrence % 2 == 0 ? -occurrence : occurrence);
            first = first < utc ? first : utc;
            last = last > utc ? last : utc;
            AddDiagnostic(
                fixture,
                "runtime_error_repeat",
                new
                {
                    error_id = "9",
                    origin = "Official",
                    occurrences = occurrence,
                    utc,
                    event_id = (occurrence + 10).ToString(),
                    thread = 1,
                    phase = "runtime",
                },
                ref sequence
            );
        }
        AddDiagnostic(
            fixture,
            "runtime_error_summary",
            new
            {
                error_id = "9",
                origin = "Official",
                occurrences = 9001L,
                first_utc = first,
                last_utc = last,
            },
            ref sequence
        );
        AddDiagnostic(
            fixture,
            "runtime_log",
            ErrorAnchor("10", MaterialNre.LodMessage, reference),
            ref sequence
        );
        AddDiagnostic(
            fixture,
            "runtime_error_summary",
            new
            {
                error_id = "10",
                origin = "Official",
                occurrences = 1L,
                first_utc = reference,
                last_utc = reference,
            },
            ref sequence
        );
        var longText = new string('x', 20000);
        AddDiagnostic(
            fixture,
            "runtime_log",
            new
            {
                source = "Other",
                severity = "Error",
                message = longText,
                stack = longText,
            },
            ref sequence
        );
        AddDiagnostic(
            fixture,
            "runtime_log",
            new
            {
                warning_id = "9",
                warning_occurrences = 1L,
                warning_first_utc = reference,
                warning_last_utc = reference,
                warning_origin = "Official",
                severity = "Warning",
                message = "independent warning",
                utc = reference,
            },
            ref sequence
        );
        fixture.Save();
        Require(
            CaptureArchive.Pack(fixture.Folder, false),
            "exact error families across multiple sort chunks"
        );
        using (var zip = ZipFile.OpenRead(fixture.Zip))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            var errors = manifest.RootElement.GetProperty("error_summary");
            Require(
                errors.GetProperty("anchor_records").GetString() == "2"
                    && errors.GetProperty("repeat_records").GetString() == "9000"
                    && errors.GetProperty("summary_records").GetString() == "2"
                    && errors.GetProperty("occurrences").GetString() == "9002",
                "errors retain exact logical/physical units and both patterns"
            );
            Require(
                errors.GetProperty("first_utc").GetString() == first.ToString("O")
                    && errors.GetProperty("last_utc").GetString() == last.ToString("O"),
                "decreasing and increasing error UTC derive cumulative extrema"
            );
            Require(
                errors.GetProperty("basis").GetString() == "saved_anchors_and_repeat_ordinals"
                    && errors.GetProperty("scope").GetString() == "exact_known_unity_material_nre"
                    && !manifest
                        .RootElement.GetProperty("warning_summary")
                        .TryGetProperty("scope", out _)
                    && manifest
                        .RootElement.GetProperty("warning_summary")
                        .GetProperty("basis")
                        .GetString() == "saved_anchors_and_repeat_deltas",
                "error ordinal basis and legacy warning delta basis remain distinct"
            );
            Require(
                manifest
                    .RootElement.GetProperty("warning_summary")
                    .GetProperty("occurrences")
                    .GetString() == "1"
                    && manifest
                        .RootElement.GetProperty("count_summary")
                        .GetProperty("api")
                        .GetProperty("request_records")
                        .GetString() == "1"
                    && manifest.RootElement.GetProperty("observer_errors").EnumerateObject().Count()
                        == 0,
                "runtime errors do not alter warning/request/observer-error units"
            );
        }
        RewriteManifest(
            fixture.Zip,
            manifest => manifest["error_summary"]!["occurrences"] = "9003"
        );
        Fails(
            () => CaptureArchive.Pack(fixture.Folder, false),
            "readable error summary independently recounted"
        );
        RewriteManifest(fixture.Zip, manifest => manifest.AsObject().Remove("error_summary"));
        Fails(
            () => CaptureArchive.Pack(fixture.Folder, false),
            "new aggregated errors require their readable manifest summary"
        );

        foreach (
            var fault in new[]
            {
                "orphan",
                "duplicate",
                "ordinal",
                "timestamp",
                "utcFormat",
                "summary",
                "summaryFirst",
                "summaryLast",
                "severity",
                "source",
                "message",
                "exception_type",
                "stack",
                "caused_by_event_id",
                "failed_listener",
                "missingNull",
                "crossOrigin",
                "numericId",
                "mixedFamily",
                "missingSummary",
                "partialMetadata",
                "partialWarning",
                "partialWarningContainer",
                "duplicateAnchor",
            }
        )
        {
            var sample = new Fixture(root, "error" + fault, 5);
            sequence = 7;
            var anchor = ErrorAnchor("1", MaterialNre.StandardMessage, reference);
            if (fault is "severity" or "source" or "message")
                anchor[fault] =
                    fault == "severity" ? "Warning"
                    : fault == "source" ? "Other"
                    : MaterialNre.StandardMessage + " ";
            if (fault is "exception_type" or "stack" or "caused_by_event_id" or "failed_listener")
                anchor[fault] = "";
            if (fault == "missingNull")
                anchor.Remove("stack");
            if (fault == "numericId")
                anchor["error_id"] = 1;
            if (fault == "mixedFamily")
                anchor["warning_id"] = "1";
            if (fault == "partialMetadata")
                anchor.Remove("error_id");
            if (fault == "partialWarning")
                anchor["warning_origin"] = "Official";
            if (fault == "partialWarningContainer")
                anchor["warning_origin"] = new[] { "Official" };
            AddDiagnostic(sample, "runtime_log", anchor, ref sequence);
            if (fault == "duplicateAnchor")
                AddDiagnostic(sample, "runtime_log", anchor, ref sequence);
            var repeat = ErrorRepeat(fault == "orphan" ? "2" : "1", reference);
            if (fault == "crossOrigin")
                repeat["origin"] = "Private";
            if (fault == "ordinal")
                repeat["occurrences"] = 3L;
            if (fault == "timestamp")
                repeat["utc"] = reference.AddTicks(-1);
            if (fault == "utcFormat")
                repeat["utc"] = "invalid";
            AddDiagnostic(sample, "runtime_error_repeat", repeat, ref sequence);
            if (fault == "duplicate")
                AddDiagnostic(sample, "runtime_error_repeat", repeat, ref sequence);
            if (fault != "missingSummary")
                AddDiagnostic(
                    sample,
                    "runtime_error_summary",
                    new
                    {
                        error_id = "1",
                        origin = "Official",
                        occurrences = fault == "summary" ? 3L : 2L,
                        first_utc = fault == "summaryFirst" ? reference.AddTicks(-1) : reference,
                        last_utc = fault == "summaryLast" ? reference.AddTicks(1) : reference,
                    },
                    ref sequence
                );
            sample.Save();
            if (fault == "missingSummary")
            {
                Require(
                    CaptureArchive.Pack(sample.Folder, false),
                    "missing final error summary retains saved prefix"
                );
                using var zip = ZipFile.OpenRead(sample.Zip);
                using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                var errors = manifest.RootElement.GetProperty("error_summary");
                Require(
                    errors.GetProperty("occurrences").GetString() == "2"
                        && errors.GetProperty("missing_final_summaries").GetString() == "1",
                    "missing error final invents no occurrences"
                );
            }
            else
                Fails(
                    () => CaptureArchive.Pack(sample.Folder, false),
                    "error relation negative " + fault
                );
        }
        ErrorOccurrenceControls(root, reference);
    }

    private static void ErrorOccurrenceControls(string root, DateTimeOffset utc)
    {
        foreach (var anchorFault in new[] { false, true })
        foreach (var field in new[] { "event_id", "thread", "phase" })
        foreach (var fault in new[] { "missing", "null", "kind", "value" })
        {
            var fixture = new Fixture(root, $"errorMetadata{anchorFault}{field}{fault}", 5);
            var anchor = ErrorAnchor("1", MaterialNre.StandardMessage, utc);
            var repeat = ErrorRepeat("1", utc);
            var target = anchorFault ? anchor : repeat;
            if (fault == "missing")
                target.Remove(field);
            else if (fault == "null")
                target[field] = null;
            else if (fault == "kind")
                target[field] = field == "thread" ? "1" : 1;
            else
                target[field] =
                    field == "event_id" ? "01"
                    : field == "thread" ? 0
                    : "";
            long sequence = 7;
            AddDiagnostic(fixture, "runtime_log", anchor, ref sequence);
            AddDiagnostic(fixture, "runtime_error_repeat", repeat, ref sequence);
            fixture.Save();
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                $"error occurrence metadata {anchorFault}/{field}/{fault}"
            );
        }
        var mismatch = new Fixture(root, "errorAnchorEventMismatch", 5);
        var changed = ErrorAnchor("1", MaterialNre.StandardMessage, utc);
        changed["event_id"] = "2";
        long mismatchSequence = 7;
        AddDiagnostic(mismatch, "runtime_log", changed, ref mismatchSequence);
        mismatch.Save();
        Fails(
            () => CaptureArchive.Pack(mismatch.Folder, false),
            "error anchor event identity differs from pattern identity"
        );
        var variant = 0;
        foreach (var field in new[] { "delta", "first_utc", "last_utc" })
        foreach (var value in new object?[] { null, 1L, new { nested = true }, new[] { 1 } })
        {
            var fixture = new Fixture(root, "errorRemoved" + field + variant++, 5);
            var repeat = ErrorRepeat("1", utc);
            repeat[field] = value;
            long sequence = 7;
            AddDiagnostic(
                fixture,
                "runtime_log",
                ErrorAnchor("1", MaterialNre.StandardMessage, utc),
                ref sequence
            );
            AddDiagnostic(fixture, "runtime_error_repeat", repeat, ref sequence);
            fixture.Save();
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                "removed Error-repeat field is forbidden even null/container: " + field
            );
        }
        foreach (
            var field in new[]
            {
                "error_id",
                "error_occurrences",
                "error_first_utc",
                "error_last_utc",
                "error_origin",
            }
        )
        {
            var fixture = new Fixture(root, "errorPartialContainer" + field, 5);
            long sequence = 7;
            AddDiagnostic(
                fixture,
                "runtime_log",
                new Dictionary<string, object?> { [field] = new { invalid = true } },
                ref sequence
            );
            fixture.Save();
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                "partial Error metadata container is not an ordinary full diagnostic: " + field
            );
        }
    }

    private static void ErrorParserControls()
    {
        var utc = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        foreach (var reverse in new[] { false, true })
        {
            var fields = ErrorAnchor("1", MaterialNre.StandardMessage, utc);
            var ordered = reverse
                ? fields.Reverse().ToDictionary(pair => pair.Key, pair => pair.Value)
                : fields;
            using var input = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new { data = ordered, kind = "runtime_log" }) + "\n"
                )
            );
            using var reader = new RecordReader(input);
            var projection = reader.Read()!;
            Require(
                projection.IsEligibleMaterialError && projection.Get("message") is null,
                "exact error matching does not retain arbitrary message and ignores property order"
            );
            using var wrappedInput = new MemoryStream(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(
                        new
                        {
                            data = new
                            {
                                data = ordered,
                                original_environment = "Unknown",
                                scope = "process",
                            },
                            kind = "runtime_log",
                        }
                    ) + "\n"
                )
            );
            using var wrappedReader = new RecordReader(wrappedInput);
            Require(
                wrappedReader.Read()!.Payload is { IsEligibleMaterialError: true } payload
                    && payload.Get("kind") == "runtime_log",
                "wrapped error matching accepts outer kind after payload"
            );
        }
        foreach (
            var field in new[]
            {
                "error_id",
                "error_occurrences",
                "error_first_utc",
                "error_last_utc",
                "error_origin",
                "source",
                "severity",
                "message",
                "exception_type",
                "stack",
                "caused_by_event_id",
                "failed_listener",
            }
        )
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    kind = "runtime_log",
                    data = ErrorAnchor("1", MaterialNre.StandardMessage, utc),
                }
            );
            var duplicate = "," + JsonSerializer.Serialize(field) + ":null";
            json = json.Insert(json.Length - 2, duplicate) + "\n";
            Fails(
                () =>
                {
                    using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var reader = new RecordReader(input);
                    reader.Read();
                },
                "duplicate error eligibility/control field " + field
            );
        }
        var arbitrary =
            JsonSerializer.Serialize(
                new
                {
                    kind = "runtime_log",
                    data = new
                    {
                        source = "Unity",
                        severity = "Error",
                        message = new string('x', 25000),
                        stack = new string('y', 25000),
                        exception_type = new string('z', 25000),
                        caused_by_event_id = new string('a', 25000),
                        failed_listener = new string('b', 25000),
                    },
                }
            ) + "\n";
        using (var input = new MemoryStream(Encoding.UTF8.GetBytes(arbitrary)))
        using (var reader = new RecordReader(input))
            Require(
                !reader.Read()!.IsEligibleMaterialError && reader.Read() is null,
                "unrelated unbounded diagnostic strings remain valid and unretained"
            );
    }

    private static void BootstrapErrors(string root)
    {
        var utc = new DateTimeOffset(2026, 10, 14, 0, 0, 0, TimeSpan.Zero);
        foreach (var reference in new[] { false, true })
        {
            var fixture = new Fixture(root, "errorBootstrap" + reference, 5);
            var source = new
            {
                run_id = fixture.Run,
                environment = "Unknown",
                scope = "process",
                checkpoint_sha256 = new string('A', 64),
                diagnostics_sha256 = new string('B', 64),
                records = 1,
            };
            var original = new
            {
                kind = "runtime_log",
                schema = 5,
                run_id = fixture.Run,
                environment = "Unknown",
                sequence = 1,
                utc,
                elapsed_ticks = 0,
                thread = 1,
                stream = "diagnostics",
                system_id = "Recorder",
                data = ErrorAnchor("4", MaterialNre.StandardMessage, utc, "Unknown"),
            };
            fixture
                .Lines[3]
                .Insert(
                    1,
                    reference
                        ? JsonSerializer.Serialize(
                            new
                            {
                                kind = "process_bootstrap_reference",
                                utc,
                                data = source,
                            }
                        )
                        : JsonSerializer.Serialize(
                            new
                            {
                                kind = "process_bootstrap",
                                utc,
                                data = new { source, original_record = original },
                            }
                        )
                );
            if (!reference)
            {
                long sequence = 7;
                AddDiagnostic(
                    fixture,
                    "runtime_error_repeat",
                    new
                    {
                        scope = "process",
                        original_environment = "Unknown",
                        data = new
                        {
                            error_id = "4",
                            origin = "Unknown",
                            occurrences = 2L,
                            utc,
                            event_id = "5",
                            thread = 1,
                            phase = "runtime",
                        },
                    },
                    ref sequence
                );
                AddDiagnostic(
                    fixture,
                    "runtime_error_summary",
                    new
                    {
                        scope = "process",
                        original_environment = "Unknown",
                        data = new
                        {
                            error_id = "4",
                            origin = "Unknown",
                            occurrences = 2L,
                            first_utc = utc,
                            last_utc = utc,
                        },
                    },
                    ref sequence
                );
            }
            fixture.Save();
            Require(
                CaptureArchive.Pack(fixture.Folder, false),
                "Unknown error bootstrap " + reference
            );
            using (var zip = ZipFile.OpenRead(fixture.Zip))
            using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
                Require(
                    reference
                        ? manifest.RootElement.GetProperty("error_summary").ValueKind
                            == JsonValueKind.Null
                        : manifest
                            .RootElement.GetProperty("error_summary")
                            .GetProperty("occurrences")
                            .GetString() == "2",
                    "Unknown transfer joins once and reference adds no error occurrences"
                );
            if (!reference)
            {
                using var counts = new ArchiveCounts(fixture.Folder, 1);
                counts.SetIdentity(fixture.Run, "Official", 5);
                for (var stream = 0; stream < fixture.Lines.Length; stream++)
                    foreach (var line in fixture.Lines[stream])
                    {
                        using var input = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
                        using var reader = new RecordReader(input);
                        counts.Add(stream, reader.Read()!);
                    }
                counts.Finish();
                Require(
                    JsonSerializer
                        .SerializeToElement(counts.ErrorSummary)
                        .GetProperty("occurrences")
                        .GetString() == "2",
                    "legacy count contract still validates/recounts new error relationships"
                );
                var bad = new Fixture(root, "errorLegacyOrphan", 5);
                long sequence = 7;
                AddDiagnostic(bad, "runtime_error_repeat", ErrorRepeat("4", utc), ref sequence);
                using var invalid = new ArchiveCounts(bad.Folder, 1);
                invalid.SetIdentity(bad.Run, "Official", 5);
                Fails(
                    () =>
                    {
                        for (var stream = 0; stream < bad.Lines.Length; stream++)
                            foreach (var line in bad.Lines[stream])
                            {
                                using var input = new MemoryStream(
                                    Encoding.UTF8.GetBytes(line + "\n")
                                );
                                using var reader = new RecordReader(input);
                                invalid.Add(stream, reader.Read()!);
                            }
                        invalid.Finish();
                    },
                    "legacy contract cannot bypass orphan error validation"
                );
            }
        }
    }

    private static void BootstrapProofs(string root)
    {
        foreach (var mode in new[] { "transfer", "reference", "wrongCount", "mixedSource" })
        {
            var fixture = new Fixture(root, "bootstrapProof" + mode, 5);
            var utc = "2026-10-14T00:00:00Z";
            var source = new
            {
                run_id = fixture.Run,
                environment = "Unknown",
                scope = "process",
                checkpoint_sha256 = new string('A', 64),
                diagnostics_sha256 = new string('B', 64),
                records = mode == "wrongCount" ? 2 : 1,
            };
            var anchor = new
            {
                schema = 5,
                run_id = fixture.Run,
                environment = "Unknown",
                sequence = 1,
                utc,
                elapsed_ticks = 0,
                thread = 1,
                stream = "diagnostics",
                kind = "runtime_log",
                system_id = "Recorder",
                data = new
                {
                    warning_id = "10",
                    warning_origin = "Unknown",
                    warning_occurrences = 1L,
                    warning_first_utc = utc,
                    warning_last_utc = utc,
                    severity = "Warning",
                    utc,
                    message = "synthetic process warning anchor",
                    event_id = "10",
                },
            };
            var marker =
                mode == "reference"
                    ? JsonSerializer.Serialize(
                        new
                        {
                            kind = "process_bootstrap_reference",
                            utc,
                            data = source,
                        }
                    )
                    : JsonSerializer.Serialize(
                        new
                        {
                            kind = "process_bootstrap",
                            utc,
                            data = new { source, original_record = anchor },
                        }
                    );
            fixture.Lines[3].Insert(1, marker);
            if (mode == "mixedSource")
            {
                var changed = JsonNode.Parse(marker)!;
                changed["data"]!["source"]!["diagnostics_sha256"] = new string('C', 64);
                fixture.Lines[3].Insert(2, changed.ToJsonString());
            }
            if (mode == "transfer")
            {
                long sequence = 7;
                AddDiagnostic(
                    fixture,
                    "runtime_warning_repeat",
                    new
                    {
                        scope = "process",
                        original_environment = "Unknown",
                        data = new
                        {
                            warning_id = "10",
                            origin = "Unknown",
                            delta = 1L,
                            occurrences = 2L,
                            first_utc = utc,
                            last_utc = utc,
                            utc,
                            event_id = "11",
                            phase = "runtime",
                            thread = 1,
                        },
                    },
                    ref sequence
                );
                AddDiagnostic(
                    fixture,
                    "runtime_warning_summary",
                    new
                    {
                        scope = "process",
                        original_environment = "Unknown",
                        data = new
                        {
                            warning_id = "10",
                            origin = "Unknown",
                            occurrences = 2L,
                            first_utc = utc,
                            last_utc = utc,
                        },
                    },
                    ref sequence
                );
            }
            fixture.Save();
            if (mode is "wrongCount" or "mixedSource")
                Fails(
                    () => CaptureArchive.PackVerified(fixture.Folder, false)?.Dispose(),
                    "bootstrap ownership negative " + mode
                );
            else
            {
                using var proof = CaptureArchive.PackVerified(fixture.Folder, false)!;
                Require(
                    proof.BootstrapOwnership is { } ownership
                        && ownership.RunId == fixture.Run
                        && ownership.Environment == "Unknown"
                        && ownership.Records == 1
                        && ownership.TransferredRecords == (mode == "transfer" ? 1 : 0)
                        && ownership.ReferenceRecords == (mode == "reference" ? 1 : 0),
                    "fixed bootstrap source proof " + mode
                );
                using var zip = ZipFile.OpenRead(fixture.Zip);
                using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
                if (mode == "transfer")
                    Require(
                        manifest
                            .RootElement.GetProperty("warning_summary")
                            .GetProperty("occurrences")
                            .GetString() == "2",
                        "transferred Unknown anchor joins later process repeat once"
                    );
                else
                    Require(
                        manifest.RootElement.GetProperty("warning_summary").ValueKind
                            == JsonValueKind.Null,
                        "process reference never duplicates source warning occurrence"
                    );
            }
        }
    }

    private static void UnboundNativeCounts(string root)
    {
        var fixture = new Fixture(root, "unboundNativeV2", 5);
        long sequence = 7;
        foreach (var phase in new[] { "enter", "exit" })
            AddDiagnostic(
                fixture,
                "unbound_combat_" + phase,
                new
                {
                    scope = "process",
                    original_environment = "Unknown",
                    data = new
                    {
                        invocation_id = "2",
                        system_id = "Battle",
                        boundary_id = "attack",
                        variant_id = "normal",
                        complete = true,
                    },
                },
                ref sequence
            );
        foreach (var phase in new[] { "enter", "exit" })
            fixture
                .Lines[3]
                .Insert(
                    1,
                    JsonSerializer.Serialize(
                        new
                        {
                            kind = "process_bootstrap",
                            utc = "2026-10-14T00:00:00Z",
                            data = new
                            {
                                source = new
                                {
                                    run_id = fixture.Run,
                                    environment = "Unknown",
                                    scope = "process",
                                    checkpoint_sha256 = new string('A', 64),
                                    diagnostics_sha256 = new string('B', 64),
                                    records = 2,
                                },
                                original_record = new
                                {
                                    schema = 5,
                                    run_id = fixture.Run,
                                    environment = "Unknown",
                                    sequence = phase == "enter" ? 1 : 2,
                                    utc = "2026-10-14T00:00:00Z",
                                    elapsed_ticks = 0,
                                    thread = 1,
                                    stream = "diagnostics",
                                    kind = "unbound_combat_" + phase,
                                    system_id = "Recorder",
                                    data = new
                                    {
                                        scope = "process",
                                        original_environment = "Unknown",
                                        data = new
                                        {
                                            invocation_id = "3",
                                            system_id = "Battle",
                                            boundary_id = "attack",
                                            variant_id = "normal",
                                            complete = true,
                                        },
                                    },
                                },
                            },
                        }
                    )
                );
        fixture.Save();
        Require(CaptureArchive.Pack(fixture.Folder, false), "unbound native actual IDs retained");
        using (var zip = ZipFile.OpenRead(fixture.Zip))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            var summary = manifest.RootElement.GetProperty("count_summary");
            Require(
                summary.GetProperty("version").GetInt32() == 2
                    && summary
                        .GetProperty("native")
                        .GetProperty("identified_calls_started")
                        .GetString() == "3"
                    && summary.GetProperty("native").GetProperty("records").GetString() == "6",
                "v2 counts direct and transferred unknown-origin native invocations once"
            );
            Require(
                summary.GetProperty("streams")[1].GetProperty("records").GetString() == "2",
                "unbound native remains diagnostic physical stream"
            );
            Require(
                summary
                    .GetProperty("completeness")
                    .GetProperty("reasons")
                    .EnumerateArray()
                    .Any(value => value.GetString() == "native_origin_unverified"),
                "unknown native origin remains explicit"
            );
        }
        var reference = new Fixture(root, "unboundNativeReference", 5);
        reference
            .Lines[3]
            .Insert(
                1,
                JsonSerializer.Serialize(
                    new
                    {
                        kind = "process_bootstrap_reference",
                        utc = "2026-10-14T00:00:00Z",
                        data = new
                        {
                            run_id = reference.Run,
                            environment = "Unknown",
                            scope = "process",
                            checkpoint_sha256 = new string('A', 64),
                            diagnostics_sha256 = new string('B', 64),
                            records = 2,
                        },
                    }
                )
            );
        reference.Save();
        Require(CaptureArchive.Pack(reference.Folder, false), "reference-only archive packages");
        using (var zip = ZipFile.OpenRead(reference.Zip))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
            Require(
                manifest
                    .RootElement.GetProperty("count_summary")
                    .GetProperty("native")
                    .GetProperty("identified_calls_started")
                    .GetString() == "1",
                "reference never duplicates process native calls"
            );
        RewriteManifest(fixture.Zip, manifest => manifest["count_summary"]!["version"] = 1);
        Fails(
            () => CaptureArchive.Pack(fixture.Folder, false),
            "count-contract downgrade cannot relabel v2 counters"
        );
        RewriteManifest(
            fixture.Zip,
            manifest =>
            {
                using var counts = new ArchiveCounts(fixture.Folder, 1);
                counts.SetIdentity(fixture.Run, "Official", 5);
                foreach (var stream in Enumerable.Range(0, 4))
                foreach (var line in fixture.Lines[stream])
                {
                    using var input = new MemoryStream(Encoding.UTF8.GetBytes(line + "\n"));
                    using var reader = new RecordReader(input);
                    counts.Add(stream, reader.Read()!);
                }
                manifest["count_summary"] = JsonSerializer.SerializeToNode(counts.Summary());
                manifest.AsObject().Remove("warning_summary");
            }
        );
        Require(
            CaptureArchive.Pack(fixture.Folder, false),
            "existing schema5 v1 replays exact legacy count semantics"
        );
        RewriteManifest(fixture.Zip, manifest => manifest["count_summary"]!["version"] = 99);
        Fails(
            () => CaptureArchive.Pack(fixture.Folder, false),
            "unsupported count summary version"
        );
    }

    private static void ExistingZipControls(string root)
    {
        var summary = new Fixture(root, "summarytamper", 5);
        summary.Save();
        Require(CaptureArchive.Pack(summary.Folder, false), "summary tamper baseline");
        RewriteManifest(
            summary.Zip,
            value => value["count_summary"]!["api"]!["request_records"] = "999"
        );
        Fails(() => CaptureArchive.Pack(summary.Folder, false), "independent summary recount");
        var source = new Fixture(root, "sourcebinding", 5);
        source.Save();
        Require(CaptureArchive.Pack(source.Folder, false), "source binding baseline");
        var path = Path.Combine(source.Folder, "api.jsonl.gz");
        var bytes = File.ReadAllBytes(path);
        bytes[12] ^= 4;
        File.WriteAllBytes(path, bytes);
        Fails(() => CaptureArchive.Pack(source.Folder, false), "source binding verified again");
        var readOnly = new Fixture(root, "readonly", 5);
        readOnly.Save();
        Require(CaptureArchive.Pack(readOnly.Folder, false), "readonly baseline");
        File.SetAttributes(readOnly.Zip, FileAttributes.ReadOnly);
        try
        {
            Require(CaptureArchive.Pack(readOnly.Folder, false), "readonly existing ZIP");
        }
        finally
        {
            File.SetAttributes(readOnly.Zip, FileAttributes.Normal);
        }
        var zip64 = new Fixture(root, "zip64", 5);
        zip64.Save();
        Require(CaptureArchive.Pack(zip64.Folder, false), "ZIP64 baseline");
        PromoteZip64(zip64.Zip);
        Require(CaptureArchive.Pack(zip64.Folder, false), "ZIP64 central-directory envelope");
        var blank = new Fixture(root, "blank", 5);
        blank.Lines[0].Add("");
        blank.Save();
        Fails(() => CaptureArchive.Pack(blank.Folder, false), "blank JSONL");
        var localName = new Fixture(root, "localname", 5);
        localName.Save();
        Require(CaptureArchive.Pack(localName.Folder, false), "local-name baseline");
        var tampered = File.ReadAllBytes(localName.Zip);
        tampered[30] = (byte)'x';
        File.WriteAllBytes(localName.Zip, tampered);
        Fails(() => CaptureArchive.Pack(localName.Folder, false), "local/central names disagree");
    }

    private static void ArchiveExpansionWorkload(string root)
    {
        var text = new string('x', 8 * 1024 * 1024);
        foreach (var recovered in new[] { false, true })
        {
            var fixture = new Fixture(root, recovered ? "expansionRecovered" : "expansionFresh", 5);
            long sequence = 7;
            AddDiagnostic(fixture, "synthetic_payload", new { exact_payload = text }, ref sequence);
            fixture.Save();
            var extracted = fixture.Stamps.Select(stamp => stamp.ExtractedBytes).SumChecked();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var allocated = GC.GetTotalAllocatedBytes(true);
            using var proof = CaptureArchive.PackVerified(fixture.Folder, recovered)!;
            var elapsed = watch.ElapsedMilliseconds;
            Require(
                proof.SourceBytesExpanded == checked(extracted * (recovered ? 2 : 1)),
                "measured exact full source expansion passes"
            );
            Require(
                proof.ZipBytesExpanded
                    == checked(
                        proof.Entries.Select(entry => entry.Bytes).SumChecked()
                        * (recovered ? 2 : 1)
                    ),
                "measured exact full ZIP expansion passes"
            );
            Console.WriteLine(
                $"ARCHIVE_EXPANSION_WORKLOAD recovered={recovered} elapsed_ms={elapsed} source_bytes_expanded={proof.SourceBytesExpanded} zip_bytes_expanded={proof.ZipBytesExpanded} allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated} gzip_bytes={fixture.Stamps.Select(stamp => stamp.Bytes).SumChecked()} zip_bytes={new FileInfo(fixture.Zip).Length}"
            );
        }
    }

    private static void RewriteManifest(string path, Action<JsonNode> change)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = zip.GetEntry("manifest.json")!;
        JsonNode node;
        using (var input = entry.Open())
            node = JsonNode.Parse(input)!;
        change(node);
        entry.Delete();
        using var output = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        using var json = new Utf8JsonWriter(output);
        node.WriteTo(json);
    }

    private static void PromoteZip64(string path)
    {
        var original = File.ReadAllBytes(path);
        var eocd = original.AsSpan(original.Length - 22);
        Require(BitConverter.ToUInt32(eocd) == 0x06054B50, "ZIP64 positive fixture footer");
        using var output = new MemoryStream();
        output.Write(original.AsSpan(0, original.Length - 22));
        using (var writer = new BinaryWriter(output, Encoding.UTF8, true))
        {
            writer.Write(0x06064B50u);
            writer.Write(44L);
            writer.Write((ushort)45);
            writer.Write((ushort)45);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(5L);
            writer.Write(5L);
            writer.Write((long)BitConverter.ToUInt32(eocd[12..]));
            writer.Write((long)BitConverter.ToUInt32(eocd[16..]));
            writer.Write(0x07064B50u);
            writer.Write(0u);
            writer.Write((long)original.Length - 22);
            writer.Write(1u);
            writer.Write(0x06054B50u);
            writer.Write(0u);
            writer.Write(ushort.MaxValue);
            writer.Write(ushort.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write(uint.MaxValue);
            writer.Write((ushort)0);
        }
        File.WriteAllBytes(path, output.ToArray());
    }

    private static void Normal(string root, int schema)
    {
        var fixture = new Fixture(root, "normal" + schema, schema);
        fixture.Save();
        Require(CaptureArchive.Pack(fixture.Folder, false), "normal package");
        Require(CaptureArchive.Pack(fixture.Folder, false), "existing ZIP verification");
        using var zip = ZipFile.OpenRead(fixture.Zip);
        Require(zip.Entries.Count == 5, "five flat entries");
        using var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open());
        var summary = manifest.RootElement.GetProperty("count_summary");
        Require(
            summary.GetProperty("api").GetProperty("request_records").GetString() == "1",
            "one request"
        );
        Require(
            summary.GetProperty("api").GetProperty("response_records").GetString() == "1",
            "one response"
        );
        Require(
            summary.GetProperty("streams")[0].GetProperty("records").GetString() == "2",
            "two API records"
        );
        Require(
            summary.GetProperty("streams")[2].GetProperty("records").GetString() == "2",
            "raw copies separate"
        );
        Require(
            summary.GetProperty("native").GetProperty("calls_started").ValueKind
                == (schema == 5 ? JsonValueKind.String : JsonValueKind.Null),
            "honest native identity"
        );
        Require(
            summary.GetProperty("api").GetProperty("logical_operations").ValueKind
                == JsonValueKind.Null,
            "unknown operations"
        );
        Require(Directory.Exists(fixture.Folder), "source originals retained");
        Require(
            !Directory.EnumerateDirectories(fixture.Folder, ".archive-scratch-*").Any(),
            "scratch ownership cleaned"
        );
    }

    private static void Recovery(string root)
    {
        var fixture = new Fixture(root, "tail", 5);
        fixture.Save();
        using (
            var lease = new FileStream(
                Path.Combine(fixture.Folder, "session.lock"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None
            )
        )
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                "active writer lease rejects packaging"
            );
        using (
            var tail = new FileStream(Path.Combine(fixture.Folder, "api.jsonl.gz"), FileMode.Append)
        )
            tail.Write([1, 2, 3, 4]);
        Require(CaptureArchive.Pack(fixture.Folder, true), "recover verified prefix");
        using (var zip = ZipFile.OpenRead(fixture.Zip))
        using (var manifest = JsonDocument.Parse(zip.GetEntry("manifest.json")!.Open()))
        {
            Require(
                manifest
                    .RootElement.GetProperty("retained_provisional_compressed_bytes")
                    .GetString() == "4",
                "tail accounted"
            );
            Require(manifest.RootElement.GetProperty("recovered").GetBoolean(), "recovery flag");
            Require(
                !manifest.RootElement.GetProperty("shutdown_complete").GetBoolean(),
                "recovered incomplete"
            );
        }
        Require(
            new FileInfo(Path.Combine(fixture.Folder, "api.jsonl.gz")).Length
                == fixture.Stamps[0].Bytes + 4,
            "tail retained"
        );
        var collision = new Fixture(root, "collision", 5);
        collision.Save();
        var alternate = collision.Payload with
        {
            Streams = collision
                .Stamps.Select(
                    (value, i) => i == 0 ? value with { Records = value.Records + 1 } : value
                )
                .ToArray(),
        };
        File.WriteAllBytes(
            Path.Combine(collision.Folder, "session-state.1.json"),
            StorageCheckpoint.Encode(alternate)
        );
        Fails(() => CaptureArchive.Pack(collision.Folder, false), "same-generation collision");
        var cross = new Fixture(root, "cross", 5);
        cross.Save();
        File.WriteAllBytes(
            Path.Combine(cross.Folder, "session-state.1.json"),
            StorageCheckpoint.Encode(
                cross.Payload with
                {
                    RunId = Guid.NewGuid().ToString("N"),
                    Generation = 2,
                }
            )
        );
        Fails(() => CaptureArchive.Pack(cross.Folder, false), "cross-run checkpoint");
        var partial = new Fixture(root, "competing", 5);
        partial.Save();
        File.WriteAllText(partial.Zip + ".partial", "retained uncertain output");
        Require(
            CaptureArchive.Pack(partial.Folder, false),
            "fresh candidate preserves competing partial"
        );
        Require(
            File.ReadAllText(partial.Zip + ".partial") == "retained uncertain output",
            "partial remains exact"
        );
        var torn = new Fixture(root, "torn", 5);
        torn.Save();
        File.WriteAllText(Path.Combine(torn.Folder, "session-state.1.json"), "{bad");
        Require(CaptureArchive.Pack(torn.Folder, false), "torn alternate slot fallback");
    }

    private static void Corruption(string root)
    {
        foreach (var offset in Enumerable.Range(1, 20))
        {
            var fixture = new Fixture(root, "truncated" + offset, 5);
            fixture.Save();
            var path = Path.Combine(fixture.Folder, "api.jsonl.gz");
            var bytes = File.ReadAllBytes(path)[..^offset];
            File.WriteAllBytes(path, bytes);
            fixture.Stamps[0] = fixture.Stamps[0] with
            {
                Bytes = bytes.Length,
                Sha256 = Hash(bytes),
            };
            fixture.Checkpoint();
            Fails(
                () => CaptureArchive.Pack(fixture.Folder, false),
                "truncated trailer/deflate " + offset
            );
        }
        var garbage = new Fixture(root, "committedGarbage", 5);
        garbage.Save();
        var garbagePath = Path.Combine(garbage.Folder, "api.jsonl.gz");
        var garbageBytes = File.ReadAllBytes(garbagePath)
            .Concat(new byte[] { 1, 2, 3, 4 })
            .ToArray();
        File.WriteAllBytes(garbagePath, garbageBytes);
        garbage.Stamps[0] = garbage.Stamps[0] with
        {
            Bytes = garbageBytes.Length,
            Sha256 = Hash(garbageBytes),
        };
        garbage.Checkpoint();
        Fails(() => CaptureArchive.Pack(garbage.Folder, false), "committed trailing garbage");
        foreach (var trailerOffset in new[] { 1, 5 })
        {
            var fixture = new Fixture(root, "crc" + trailerOffset, 4);
            fixture.Save();
            var path = Path.Combine(fixture.Folder, "api.jsonl.gz");
            var bytes = File.ReadAllBytes(path);
            bytes[^trailerOffset] ^= 0x40;
            File.WriteAllBytes(path, bytes);
            fixture.Stamps[0] = fixture.Stamps[0] with { Sha256 = Hash(bytes) };
            fixture.Checkpoint();
            Fails(() => CaptureArchive.Pack(fixture.Folder, false), "gzip CRC/size");
        }
        var malformed = new Fixture(root, "badjson", 5);
        malformed.Lines[0][0] = "{\"schema\":5,\"bad\":[1,]}";
        malformed.Save();
        Fails(() => CaptureArchive.Pack(malformed.Folder, false), "strict JSON");
        var duplicate = new Fixture(root, "dupid", 5);
        duplicate.Lines[0][1] = duplicate.Lines[0][0];
        duplicate.Save();
        Fails(() => CaptureArchive.Pack(duplicate.Folder, false), "duplicate event ID");
        var wrong = new Fixture(root, "rawhash", 5);
        wrong.Lines[2][0] = wrong
            .Lines[2][0]
            .Replace(Hash([1, 2, 3]), new string('0', 64), StringComparison.Ordinal);
        wrong.Save();
        Fails(() => CaptureArchive.Pack(wrong.Folder, false), "raw digest mismatch");
        var mismatch = new Fixture(root, "scope", 5);
        mismatch.Lines[0][0] = mismatch
            .Lines[0][0]
            .Replace("Official", "Private", StringComparison.Ordinal);
        mismatch.Save();
        Fails(() => CaptureArchive.Pack(mismatch.Folder, false), "cross-environment envelope");
    }

    private static void OverlappedCompression()
    {
        var data = new byte[(5 << 19) + 12345];
        new Random(7).NextBytes(data);
        using var target = new MemoryStream();
        using (var overlapped = new OverlappedWrite(target))
        {
            for (var offset = 0; offset < data.Length; )
            {
                var count = Math.Min(70001, data.Length - offset);
                overlapped.Write(data, offset, count);
                offset += count;
            }
            overlapped.Complete();
        }
        Require(
            target.ToArray().AsSpan().SequenceEqual(data),
            "Overlapped compression keeps every byte in order across partial chunks"
        );
        using var failing = new OverlappedWrite(new FailingWrite());
        var failed = false;
        try
        {
            // The failure surfaces at the next chunk hand-off or at Complete, whichever comes first.
            failing.Write(data);
            failing.Complete();
        }
        catch (IOException)
        {
            failed = true;
        }
        Require(failed, "Overlapped compression surfaces a background write failure");
    }

    private sealed class FailingWrite : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Synthetic background write failure.");
    }

    private static void ParserControls()
    {
        foreach (
            var json in new[]
            {
                "{}\n",
                "{\"data\":{\"n\":-1.1e+9,\"b\":true,\"z\":null,\"s\":\"\\uD83D\\uDE00\"}}\r\n",
            }
        )
        {
            using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
            using var reader = new RecordReader(input);
            Require(reader.Read() is not null && reader.Read() is null, "JSON positive control");
        }
        foreach (
            var json in new[]
            {
                "{}",
                "\n",
                "{\"x\":01}\n",
                "{\"x\":1.}\n",
                "{\"x\":truefalse}\n",
                "{\"x\":\"\\uD800\"}\n",
                "{\"x\":[1,]}\n",
            }
        )
            Fails(
                () =>
                {
                    using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var reader = new RecordReader(input);
                    reader.Read();
                },
                "JSON negative control"
            );
        Require(
            RecordProjection.Integer("9223372036854775807") == long.MaxValue,
            "Int64 exact maximum"
        );
        foreach (var text in new[] { "01", "-1", "+1", "1e3", "9223372036854775808" })
            Fails(() => RecordProjection.Integer(text), "integer canonical/range");
        using var invalid = new MemoryStream([123, 34, 120, 34, 58, 34, 255, 34, 125, 10]);
        Fails(
            () =>
            {
                using var reader = new RecordReader(invalid);
                reader.Read();
            },
            "strict UTF8"
        );
    }

    private static void IdentityLedger(string root)
    {
        using var ledger = new SortedIds(root);
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 70000; i >= 0; i--)
            ledger.Add(new IdRow(1, i.ToString("D19"), 0, "Atelier"));
        long previous = -1;
        var count = 0;
        foreach (var item in ledger.Read())
        {
            var id = long.Parse(item.Id, System.Globalization.CultureInfo.InvariantCulture);
            Require(id > previous, "external sort order");
            previous = id;
            count++;
        }
        Require(count == 70001, "multiple-pass bounded ledger");
        Console.WriteLine(
            $"ARCHIVE_LEDGER_WORKLOAD rows={count} elapsed_ms={watch.ElapsedMilliseconds} allocated_bytes={GC.GetTotalAllocatedBytes(true) - allocated} scratch_files={Directory.EnumerateFiles(root, "*.ids", SearchOption.AllDirectories).Count()} scratch_bytes={Directory.EnumerateFiles(root, "*.ids", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)}"
        );
    }

    private static void Require(bool condition, string test)
    {
        assertions++;
        if (!condition)
            throw new InvalidOperationException("Archive check failed: " + test);
    }

    private static void Fails(Action action, string test)
    {
        try
        {
            action();
        }
        catch (Exception exception)
            when (exception
                    is InvalidDataException
                        or IOException
                        or JsonException
                        or DecoderFallbackException
                        or OverflowException
            )
        {
            assertions++;
            return;
        }
        throw new InvalidOperationException("Archive negative control accepted: " + test);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private sealed class Fixture
    {
        internal readonly string Folder,
            Zip,
            Run = Guid.NewGuid().ToString("N");
        internal readonly int Schema;
        internal readonly List<string>[] Lines = [new(), new(), new(), new()];
        internal StorageStreamStamp[] Stamps = [];
        internal StorageCheckpointPayload Payload => new(2, 1, Run, "Official", Stamps, 1);

        internal Fixture(string root, string name, int schema)
        {
            Schema = schema;
            Folder = Path.Combine(root, name + ".partial");
            Zip = Path.ChangeExtension(Folder, ".zip");
            Directory.CreateDirectory(Folder);
            var sequence = 0;
            string Event(int stream, string kind, object data) =>
                JsonSerializer.Serialize(
                    new
                    {
                        schema,
                        run_id = Run,
                        environment = "Official",
                        sequence = ++sequence,
                        utc = "2026-10-14T00:00:00Z",
                        elapsed_ticks = 0,
                        thread = 1,
                        stream = ArchiveCounts.Streams[stream],
                        kind,
                        system_id = "Battle",
                        data,
                    }
                );
            Lines[0]
                .Add(
                    Event(
                        0,
                        "request",
                        new
                        {
                            request_id = "1",
                            attempt_id = "1",
                            route = "/battle/attack",
                        }
                    )
                );
            Lines[0]
                .Add(
                    Event(
                        0,
                        "response",
                        new
                        {
                            request_id = "1",
                            attempt_id = "1",
                            route = "/battle/attack",
                        }
                    )
                );
            foreach (var phase in new[] { "combat_enter", "combat_exit" })
                Lines[1]
                    .Add(
                        Event(
                            1,
                            phase,
                            schema == 5
                                ? (object)
                                    new
                                    {
                                        invocation_id = "1",
                                        boundary_id = "attack",
                                        variant_id = "normal",
                                        complete = true,
                                    }
                                : new { request_id = 1 }
                        )
                    );
            foreach (var direction in new[] { "request", "response" })
                Lines[2]
                    .Add(
                        Event(
                            2,
                            "raw_body",
                            new
                            {
                                request_id = "1",
                                attempt_id = "1",
                                body_id = "1:" + direction + ":entity",
                                route = "/battle/attack",
                                direction,
                                stage = direction + "_entity",
                                body = new byte[] { 1, 2, 3 },
                                bytes = 3,
                                sha256 = Hash([1, 2, 3]),
                            }
                        )
                    );
            Lines[3]
                .Add(
                    JsonSerializer.Serialize(
                        new
                        {
                            kind = "session_start",
                            schema,
                            run_id = Run,
                            environment = "Official",
                            utc = "2026-10-14T00:00:00Z",
                            stopwatch_frequency = 10000000,
                            contains_sensitive_raw_data = true,
                            metadata = new { plugin = "test", identity_self_reported = true },
                        }
                    )
                );
            Lines[3]
                .Add(
                    Event(
                        3,
                        "request_complete",
                        new
                        {
                            request_id = "1",
                            attempt_id = "1",
                            route = "/battle/attack",
                        }
                    )
                );
            Lines[3]
                .Add(
                    JsonSerializer.Serialize(
                        new
                        {
                            kind = "session_end",
                            run_id = Run,
                            utc = "2026-10-14T00:00:01Z",
                            events = sequence,
                            shutdown_complete = true,
                        }
                    )
                );
        }

        internal void Save()
        {
            Stamps = Lines
                .Select(
                    (lines, index) =>
                    {
                        var raw = Encoding.UTF8.GetBytes(
                            string.Join("\n", lines) + (lines.Count == 0 ? "" : "\n")
                        );
                        using var bytes = new MemoryStream();
                        using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, true))
                            gzip.Write(raw);
                        var compressed = bytes.ToArray();
                        if (compressed.Length == 0)
                            compressed =
                            [
                                31,
                                139,
                                8,
                                0,
                                0,
                                0,
                                0,
                                0,
                                0,
                                3,
                                3,
                                0,
                                0,
                                0,
                                0,
                                0,
                                0,
                                0,
                                0,
                                0,
                            ];
                        File.WriteAllBytes(
                            Path.Combine(Folder, StorageCheckpoint.FileNames[index]),
                            compressed
                        );
                        return new StorageStreamStamp(
                            StorageCheckpoint.FileNames[index],
                            compressed.LongLength,
                            Hash(compressed),
                            lines.Count,
                            raw.LongLength
                        );
                    }
                )
                .ToArray();
            Checkpoint();
        }

        internal void Checkpoint()
        {
            if (Schema == 5)
                File.WriteAllBytes(
                    Path.Combine(Folder, "session-state.0.json"),
                    StorageCheckpoint.Encode(Payload)
                );
            else
                File.WriteAllText(
                    Path.Combine(Folder, "session-state.json"),
                    JsonSerializer.Serialize(
                        new
                        {
                            schema = 1,
                            run_id = Run,
                            environment = "Official",
                            files = Stamps.Select(value => new
                            {
                                name = value.Name,
                                bytes = value.Bytes,
                                sha256 = value.Sha256,
                            }),
                        }
                    )
                );
        }
    }
}

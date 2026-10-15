using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class NetworkChecks
{
    internal static void Run()
    {
        OriginalBodiesAndWire();
        CompletePayloadsAndHeaders();
        NativeMessageAndEntityContracts();
        BodilessRequestContracts();
        NativeBodyQualification();
        AttemptIdentityAndCommit();
        ResponseLifetimeAfterRequestDisposal();
        DescriptorIdentitySemantics();
        ContextScopes();
        StagingFailuresAndTerminalUnion();
        SharedNativeContext();
        ParsedRootAndLeafProofs();
        LineageChecks.Run();
        StatusDuringCopy();
        DurableOriginalBatch();
        NativeSnapshotArchive();
    }

    private static void DescriptorIdentitySemantics()
    {
        const string rawOperation = "synthetic-native-id-without-uuid-normalization";
        var ledger = new RequestLedger();
        var first = ledger.Begin(21, "/status", false, null, "native_descriptor", "Foundation");
        first.Context = first.Context with
        {
            DescriptorId = "31",
            LogicalRoute = "/status",
            NativeOperationId = rawOperation,
            RequestHeaderId = null,
            TransportPath = "/",
        };
        ledger.VerifyDescriptor(21, "31", "/status", rawOperation);
        Check(
            first.Context.Route == "/status"
                && first.Context.TransportPath == "/"
                && first.Context.OperationId is null
                && first.Context.NativeOperationId == rawOperation
                && first.Context.RequestHeaderId is null,
            "logical descriptor route, physical startup path and original operation/header semantics remain distinct"
        );
        var late = ledger.Begin(22, "/", false, null, null, null);
        var recordedContext = late.Context;
        ledger.VerifyDescriptor(22, "32", "/status", rawOperation);
        late.Context = late.Context with
        {
            DescriptorId = "32",
            LogicalRoute = "/status",
            NativeOperationId = rawOperation,
            TransportPath = "/",
            RequestHeaderId = "synthetic-header-unrelated-to-operation",
        };
        Check(
            late.Context.Route == recordedContext.Route
                && late.Context.SystemId == recordedContext.SystemId
                && late.Context.OperationId == recordedContext.OperationId
                && late.Context.LogicalRoute == "/status",
            "late proven descriptor enriches evidence without rewriting recorded route owner or inventing UUID"
        );
        try
        {
            ledger.VerifyDescriptor(22, "33", "/status", rawOperation);
            throw new InvalidOperationException("Conflicting descriptor lifetime was accepted.");
        }
        catch (InvalidDataException) { }
        try
        {
            ledger.VerifyDescriptor(22, "32", "/status", "another-original-native-id");
            throw new InvalidOperationException(
                "Conflicting original descriptor identity was accepted."
            );
        }
        catch (InvalidDataException) { }
        var reused = ledger.Begin(22, "/status", false, null, null, "Foundation");
        Check(
            ledger.Unique(22) is null
                && ReferenceEquals(ledger.ForObservation(22, late.Context), late)
                && ReferenceEquals(ledger.ForObservation(22, reused.Context), reused),
            "reused object cannot bind a new descriptor to an arbitrary latest Send"
        );
        Check(
            ReferenceEquals(ledger.BindResponse(221, 21), first)
                && ledger.BindResponse(222, 22) is null,
            "out-of-order response uses original unique request, not late descriptor or newest reused owner"
        );
    }

    private static void NativeMessageAndEntityContracts()
    {
        const string descriptor = "Google.Protobuf.Reflection.FileDescriptorProto";
        Check(
            Network.UsesBinaryMessageSnapshot(descriptor)
                && !Network.UsesBinaryMessageSnapshot(descriptor + "Nested")
                && !Network.UsesBinaryMessageSnapshot(
                    "Google.Protobuf.Reflection.FileDescriptorSet"
                )
                && !Network.UsesBinaryMessageSnapshot("Blend.ApiSchema.Model.FileDescriptorProto")
                && !Network.UsesBinaryMessageSnapshot(null)
                && Network
                    .MessageSnapshotFields(descriptor)
                    .StartsWith("complete_derived_binary", StringComparison.Ordinal)
                && Network
                    .MessageSnapshotFields("Other")
                    .StartsWith("known_fields_only", StringComparison.Ordinal)
                && Network.MessageSnapshotFields(null) == "unavailable_native_message_class",
            "only the exact native self-descriptor class changes representation; near names cannot broaden the binary path"
        );
        try
        {
            Network.BinaryMessageSnapshot(descriptor + "Nested", []);
            throw new InvalidOperationException("Unqualified binary snapshot type was accepted.");
        }
        catch (InvalidDataException) { }
        var snapshot = typeof(Network).GetMethod(
            "OriginalMessagePayload",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        Check(
            snapshot.Invoke(null, [null, null]) is null,
            "unknown native class cannot fall through to a descriptor-dependent JSON formatter"
        );
        var proof = EmptyEntityProof();
        Check(
            Network.IsVerifiedEmptyEntity(proof),
            "complete qualified native empty framing is recognized"
        );
        foreach (
            var invalid in new[]
            {
                proof with
                {
                    method = "Get",
                },
                proof with
                {
                    status = 500,
                },
                proof with
                {
                    request_streaming = true,
                },
                proof with
                {
                    response_streaming = true,
                },
                proof with
                {
                    transport_error = true,
                },
                proof with
                {
                    headers = proof.headers with { complete = false },
                },
                proof with
                {
                    headers = proof.headers with { groups = [] },
                },
                proof with
                {
                    headers = proof.headers with
                    {
                        groups = [new HeaderGroup("content-length", ["1"])],
                    },
                },
                proof with
                {
                    headers = proof.headers with
                    {
                        groups = [new HeaderGroup("content-length", ["0", "1"])],
                    },
                },
                proof with
                {
                    headers = proof.headers with
                    {
                        groups =
                        [
                            new HeaderGroup("content-length", ["0"]),
                            new HeaderGroup("transfer-encoding", ["chunked"]),
                        ],
                    },
                },
                proof with
                {
                    headers = proof.headers with
                    {
                        groups =
                        [
                            new HeaderGroup("content-length", ["0"]),
                            new HeaderGroup("content-encoding", ["gzip"]),
                        ],
                    },
                },
                proof with
                {
                    headers = proof.headers with
                    {
                        groups =
                        [
                            new HeaderGroup("content-length", ["0"], false, "SyntheticReadFailure"),
                        ],
                    },
                },
            }
        )
        {
            Check(
                !Network.IsVerifiedEmptyEntity(invalid),
                "incomplete, streamed, error or conflicting framing cannot invent an empty entity"
            );
            try
            {
                Network.EmptyEntityBody(invalid);
                throw new InvalidOperationException(
                    "Unqualified null entity was accepted as empty."
                );
            }
            catch (InvalidDataException) { }
        }
        var runtime = new RuntimeCompatibility();
        var synthetic = typeof(NetworkChecks).GetMethod(
            nameof(SyntheticBinarySerializer),
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        try
        {
            runtime.ValidateMethod(synthetic, 0x7E92E00, "System.Byte[]", ["System.Object"], true);
            throw new InvalidOperationException("Unverified runtime admitted a serializer.");
        }
        catch (RuntimeCompatibilityException error)
        {
            Check(
                error.Code == "runtime_not_verified",
                "binary snapshot serialization requires the API runtime certificate"
            );
        }
        typeof(RuntimeCompatibility)
            .GetProperty("ApiVerified", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, true);
        try
        {
            runtime.ValidateMethod(
                synthetic,
                0x7E92E00,
                "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1<System.Byte>",
                ["Google.Protobuf.IMessage"],
                true
            );
            throw new InvalidOperationException("Name/arity-only serializer binding was accepted.");
        }
        catch (RuntimeCompatibilityException error)
        {
            Check(
                error.Code == "native_signature",
                "native byte-array return and IMessage parameter must match exactly"
            );
        }
        try
        {
            runtime.ValidateMethod(synthetic, 0x7E92E00, "System.Byte[]", ["System.Object"], true);
            throw new InvalidOperationException(
                "Managed serializer outside the pinned interop was accepted."
            );
        }
        catch (RuntimeCompatibilityException error)
        {
            Check(
                error.Code == "native_owner_outside_game_interop",
                "a matching managed signature cannot replace the pinned native owner"
            );
        }
    }

    private static byte[] SyntheticBinarySerializer(object message) => [];

    private static void BodilessRequestContracts()
    {
        var proof = new RequestEntityFraming(
            "Get",
            false,
            true,
            new HeaderSnapshot(
                "original_native_SendHeaders_callbacks",
                "original_callback_group_and_value_order",
                [new HeaderGroup("Content-Length", ["0"])],
                true
            )
        );
        Check(
            Network.IsVerifiedBodilessRequest(proof),
            "actual returned null and complete original serialized zero framing prove a bodiless GET without a synthetic array"
        );
        foreach (
            var invalid in new[]
            {
                proof with
                {
                    method = "Post",
                },
                proof with
                {
                    upload_stream_present = true,
                },
                proof with
                {
                    original_entity_returned_null = false,
                },
                proof with
                {
                    serialized_headers = null,
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with { complete = false },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with
                    {
                        error = "native_original_threw",
                    },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with { groups = [] },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with
                    {
                        groups = [new HeaderGroup("Content-Length", ["1"])],
                    },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with
                    {
                        groups = [new HeaderGroup("Content-Length", ["0", "1"])],
                    },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with
                    {
                        groups =
                        [
                            new HeaderGroup("Content-Length", ["0"]),
                            new HeaderGroup("Transfer-Encoding", ["chunked"]),
                        ],
                    },
                },
                proof with
                {
                    serialized_headers = proof.serialized_headers! with
                    {
                        groups =
                        [
                            new HeaderGroup("Content-Length", ["0"], false, "SyntheticReadFailure"),
                        ],
                    },
                },
            }
        )
            Check(
                !Network.IsVerifiedBodilessRequest(invalid),
                "missing/faulted/conflicting/upload framing retains the original request gap"
            );
    }

    private static ResponseEntityFraming EmptyEntityProof() =>
        new(
            "Post",
            200,
            false,
            false,
            false,
            new HeaderSnapshot(
                "synthetic_complete_native_headers",
                "native_dictionary_iteration",
                [
                    new HeaderGroup("content-length", ["0"]),
                    new HeaderGroup("content-type", ["application/octet-stream"]),
                ],
                true
            )
        );

    private static void ResponseLifetimeAfterRequestDisposal()
    {
        var ledger = new RequestLedger();
        var first = ledger.Begin(17, "/status", false, null, null, "Foundation");
        Check(
            ReferenceEquals(ledger.BindResponse(31, 17), first),
            "original response acquires its actual request before disposal"
        );
        ledger.Complete(first);
        Check(
            ledger.BindResponse(32, 17) is null,
            "terminal request cannot acquire an unproved late response"
        );
        ledger.Release(17);
        Check(
            ledger.Unique(17) is null
                && ReferenceEquals(ledger.Response(31), first)
                && ledger.BindResponse(32, 17) is null
                && ledger.ActiveCount == 0,
            "HTTP disposal retires request admission while an already-proven live response retains decoder lineage"
        );
        var next = ledger.Begin(18, "/auth/sign_in", false, null, null, "Accounts");
        Check(
            ledger.Response(33) is null
                && ledger.BindResponse(31, 18) is null
                && ReferenceEquals(ledger.Response(31), first),
            "fresh native lifetime and conflicting owner cannot borrow the old response association"
        );
        foreach (var retiredToken in new long[] { 31 })
            ledger.Release(retiredToken);
        Check(
            ledger.Response(31) is null
                && ledger.ActiveCount == 1
                && ReferenceEquals(ledger.BindResponse(33, 18), next),
            "the same token retirement used by native weak-object sweeping releases response state independently of requests"
        );
        ledger.Release(33);
        Check(
            ledger.Response(33) is null && ReferenceEquals(ledger.Unique(18), next),
            "response retirement cannot release another live request"
        );
        var objects = new NativeObjectContexts();
        Check(
            ReferenceEquals(ledger.BindResponse(34, 18), next),
            "response mapping is live before replacement retirement bookkeeping"
        );
        var replaced =
            (List<long>)
                typeof(NativeObjectContexts)
                    .GetField("replacedTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(objects)!;
        replaced.Add(34);
        foreach (var retiredToken in objects.Sweep())
            ledger.Release(retiredToken);
        Check(
            objects.Sweep().Length == 0 && ledger.Response(34) is null,
            "a token queued by replacement before periodic sweeping is delivered once through the response retirement path"
        );
    }

    private static void NativeBodyQualification()
    {
        foreach (
            var rva in new long[]
            {
                0x9A67A0,
                0x9A6D50,
                0x9A13D0,
                0x9A55D0,
                0x4BBB3D0,
                0x7E93AA0,
                0x4BBE0B0,
                0x8B3D60,
                0x4BBB9D0,
                0x9A5260,
                0x9A72B0,
            }
        )
            RuntimeCompatibility.RequireUniqueNativeBody(rva);
        foreach (var rva in new long[] { 0x8C8F40, 0x8C9060 })
        {
            try
            {
                RuntimeCompatibility.RequireUniqueNativeBody(rva);
                throw new InvalidDataException(
                    "Aliased native setter was qualified for a global detour."
                );
            }
            catch (RuntimeCompatibilityException error)
            {
                Check(
                    error.Code == "shared_native_body_unqualified",
                    "global native detour guard rejects optimized shared setter bodies"
                );
            }
        }
    }

    private static void CompletePayloadsAndHeaders()
    {
        const string escapedUri =
            "HTTPS://GAME.RESLERIANA.JP/battle/start?escaped=%2f%2F&space=%20&literal=+&repeat=one&repeat=two";
        var originalUri = Network.ParseOriginalUri(escapedUri);
        Check(
            originalUri.OriginalString == escapedUri
                && originalUri.Parsed?.AbsolutePath == "/battle/start"
                && RequestLedger.Classify(originalUri.Parsed) == false
                && originalUri.Parsed.ToString() != escapedUri,
            "native OriginalString remains exact while normalized URI is used only for route/origin classification"
        );
        var invalidOriginalUri = Network.ParseOriginalUri("synthetic relative?escaped=%2f");
        Check(
            invalidOriginalUri.OriginalString == "synthetic relative?escaped=%2f"
                && invalidOriginalUri.Parsed is null,
            "unparseable absolute URI retains original string without inventing classification"
        );
        const string json =
            "{\"token\":\"synthetic-token\",\"accountName\":\"Synthetic User\",\"userId\":\"9007199254740993\",\"nested\":{\"sessionKey\":\"synthetic-session\",\"deviceId\":\"synthetic-device\"},\"unknown\":[0,false,null,\"\"]}";
        var value = Network.ParsePayload(json)!;
        using var parsed = JsonDocument.Parse(json);
        Check(
            value["token"]!.GetValue<string>()
                == parsed.RootElement.GetProperty("token").GetString()
                && value["accountName"]!.GetValue<string>() == "Synthetic User"
                && value["userId"]!.GetValue<string>() == "9007199254740993"
                && value["nested"]!["sessionKey"]!.GetValue<string>() == "synthetic-session"
                && value["nested"]!["deviceId"]!.GetValue<string>() == "synthetic-device"
                && value["unknown"]!.AsArray().Count == 4,
            "decoded payload retains all fields without redaction or identifier rewriting"
        );
        var deep = new string('[', 300) + json + new string(']', 300);
        var cursor = Network.ParsePayload(deep);
        for (var depth = 0; depth < 300; depth++)
            cursor = cursor![0];
        Check(
            cursor!["token"]!.GetValue<string>() == "synthetic-token",
            "deep decoded JSON retains original fields beyond old depth limit"
        );
        string?[] originals = { "synthetic-session-one", "synthetic-session-two", "", null };
        var header = Network.CopyHeaderGroup(
            "Set-Cookie",
            () => originals.Length,
            index => originals[index]
        );
        originals[0] = "changed-after-copy";
        Check(
            header.name == "Set-Cookie"
                && header.complete
                && header.values is { Length: 4 }
                && header.values[0] == "synthetic-session-one"
                && header.values[1] == "synthetic-session-two"
                && header.values[2] == ""
                && header.values[3] is null,
            "all duplicate header values retain exact list order and detached content"
        );
        var auth = Network.CopyHeaderGroup(
            "Authorization",
            () => 1,
            _ => "synthetic-authorization"
        );
        Check(
            auth.complete && auth.values![0] == "synthetic-authorization",
            "header capture has no inherited name allowlist"
        );
        var partial = Network.CopyHeaderGroup(
            "X-Synthetic",
            () => 3,
            index =>
                index == 2
                    ? throw new InvalidOperationException(
                        "synthetic header getter failure",
                        new InvalidDataException("synthetic inner header failure")
                    )
                    : index.ToString()
        );
        Check(
            !partial.complete
                && partial.values!.SequenceEqual(new[] { "0", "1" })
                && partial.error == nameof(InvalidDataException)
                && partial.exception!.Contains(
                    "synthetic header getter failure",
                    StringComparison.Ordinal
                )
                && partial.exception.Contains(
                    "synthetic inner header failure",
                    StringComparison.Ordinal
                )
                && partial.exception.Contains(
                    nameof(CompletePayloadsAndHeaders),
                    StringComparison.Ordinal
                ),
            "failed header read retains preceding values, full message, inner cause and actual managed stack"
        );
    }

    private static void OriginalBodiesAndWire()
    {
        var original = new byte[32 * 1024 * 1024 + 17];
        for (var i = 0; i < original.Length; i++)
            original[i] = (byte)(i * 37);
        var expected = Convert.ToHexString(SHA256.HashData(original));
        var context = new NetworkContext(
            "1",
            "1",
            null,
            "/unknown/path",
            "unknown",
            false,
            null,
            null
        );
        var record = ApiBodies.Detach(context, "request", "request_entity", original);
        original[0] ^= 1;
        Check(
            record.bytes > 32 * 1024 * 1024
                && record.sha256 == expected
                && Convert.ToHexString(SHA256.HashData(record.body)) == expected,
            "complete detached large original bytes/hash"
        );
        using var gzip = new MemoryStream();
        using (var encoder = new GZipStream(gzip, CompressionLevel.Fastest, true))
            encoder.Write(record.body);
        var expanded = ApiBodies.Expand(gzip.ToArray());
        Check(
            expanded.Length == record.bytes
                && Convert.ToHexString(SHA256.HashData(expanded)) == expected,
            "decompression beyond previous body limit"
        );
        var malformed = new byte[] { 0x0a, 0x80 };
        var malformedCopy = ApiBodies.Detach(context, "response", "response_entity", malformed);
        var unknownSchemas = new Dictionary<string, Dictionary<int, string>>();
        var invalid = Wire.Describe(malformed, null, unknownSchemas);
        Check(
            invalid.error == "invalid_protobuf_wire" && malformedCopy.body.SequenceEqual(malformed),
            "malformed wire preserves original"
        );
        var many = Enumerable.Repeat(new byte[] { 0x08, 0x01 }, 70000).SelectMany(x => x).ToArray();
        var summary = Wire.Describe(many, null, unknownSchemas);
        Check(
            summary.error is null
                && summary.fields_scanned == 70000
                && summary.unknown_count == 70000
                && summary.unknown_fields.Count == 128
                && summary.summary_limited,
            "all wire fields scanned with bounded auxiliary sample only"
        );
        var nested = new byte[] { 0x08, 0x07 };
        var schemas = new Dictionary<string, Dictionary<int, string>>
        {
            ["test"] = new() { [1] = "test" },
        };
        for (var depth = 0; depth < 300; depth++)
        {
            using var stream = new MemoryStream();
            stream.WriteByte(0x0a);
            WriteVarint(stream, (ulong)nested.Length);
            stream.Write(nested);
            nested = stream.ToArray();
        }
        Check(
            Wire.Describe(nested, "test", schemas).fields_scanned == 301,
            "deep nested protobuf has no old depth cutoff"
        );
        Check(
            Wire.Describe(new byte[] { 0x0b, 0x10, 0x01, 0x0c }, null, unknownSchemas).error
                is null,
            "unknown protobuf group retained and scanned"
        );
        Check(
            Wire.Describe(new byte[] { 0x0b, 0x10, 0x01 }, null, unknownSchemas).error is not null,
            "unterminated protobuf group rejected"
        );
        foreach (var depth in new[] { 50_000, 100_000 })
        {
            var groups = new byte[depth * 2];
            groups.AsSpan(0, depth).Fill(0x0b);
            groups.AsSpan(depth).Fill(0x0c);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var deepGroups = Wire.Describe(groups, null, unknownSchemas);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Check(
                deepGroups.error is null
                    && deepGroups.fields_scanned == depth
                    && deepGroups.unknown_count == depth
                    && deepGroups.unknown_fields.Count == 128
                    && deepGroups.unknown_fields[2].path == "1.1.1",
                "all extremely deep protobuf groups preserve sampled full paths"
            );
            Check(
                allocated < 100L * 1024 * 1024,
                "deep group auxiliary traversal allocates below100MiB without quadratic path strings"
            );
            Console.WriteLine(
                JsonSerializer.Serialize(
                    new
                    {
                        workload = "protobuf_nested_groups",
                        depth,
                        input_bytes = groups.Length,
                        allocated_bytes = allocated,
                        fields_scanned = deepGroups.fields_scanned,
                    }
                )
            );
        }
    }

    private static void AttemptIdentityAndCommit()
    {
        var ledger = new RequestLedger();
        const string operation = "c6066251-3909-4c20-980d-a5d42410c2c0";
        var first = ledger.Begin(
            1,
            "/battle/attack",
            false,
            operation,
            "native_request_header",
            "Battle"
        );
        Check(
            first.ReserveStage("request:request_entity:one")
                && !first.StageWritten("request:request_entity:one"),
            "stage reservation is not durable written acknowledgment"
        );
        first.CommitStage("request:request_entity:one");
        Check(
            first.StageWritten("request:request_entity:one")
                && !first.ReserveStage("request:request_entity:one"),
            "durable commit deduplicates only exact same stage bytes"
        );
        Check(
            first.ReserveStage("request:request_entity:two"),
            "changed materialized bytes remain preserved"
        );
        var second = ledger.Begin(
            2,
            "/battle/attack",
            false,
            operation,
            "native_request_header",
            "Battle"
        );
        Check(
            first.Context.OperationId == second.Context.OperationId
                && first.Context.AttemptId != second.Context.AttemptId,
            "logical native operation differs from physical retry attempt"
        );
        Check(
            ReferenceEquals(ledger.BindResponse(201, 2), second)
                && ReferenceEquals(ledger.BindResponse(101, 1), first),
            "out of order responses bind original native request tokens"
        );
        var reused = ledger.Begin(1, "/mail/list", false, null, null, "Mail");
        Check(
            ledger.Unique(1) is null && ReferenceEquals(ledger.Unique(2), second),
            "materialization on reused request pointer cannot infer latest send ownership"
        );
        Check(
            ReferenceEquals(ledger.Response(101), first)
                && ledger.BindResponse(102, 1) is null
                && reused.Context.OperationId is null,
            "reused live transport does not guess unbound response owner or operation"
        );
        Check(
            ReferenceEquals(ledger.ForObservation(1, first.Context), first)
                && ReferenceEquals(ledger.ForObservation(1, reused.Context), reused)
                && ledger.ForObservation(1, null) is null,
            "only exact original Send scope disambiguates a reused native request"
        );
        Check(ledger.BindResponse(999, 999) is null, "orphan response never invents request");
        for (var i = 3; i <= 1600; i++)
            ledger.Begin(i, "/status", false, null, null, "Foundation");
        Check(
            ledger.ActiveCount > 1024 && ReferenceEquals(ledger.Response(101), first),
            "no active inflight eviction"
        );
        Check(
            RequestLedger.Classify(new Uri("https://game.resleriana.jp/status")) == false
                && RequestLedger.Classify(new Uri("http://127.0.0.1/status")) == true
                && RequestLedger.Classify(new Uri("https://other.invalid/status")) is null
                && RequestLedger.Classify(null) is null,
            "unknown origins never default to official or private"
        );
        Check(
            RequestLedger.CanonicalOperation("same payload") is null
                && RequestLedger.CanonicalOperation(Guid.Empty.ToString("D")) is null,
            "invalid native UUID cannot become operation identity"
        );
        Check(
            !first.ResponseWritten && !first.Completed,
            "response/completion written flags are not inferred from binding"
        );
        var active = ledger.ActiveCount;
        ledger.Complete(first);
        ledger.Complete(first);
        ledger.Release(1);
        Check(
            ledger.ActiveCount == active - 2,
            "terminal and explicit lifetime release update active count once"
        );
        Check(
            AtelierCapture.Archive.RouteCountCatalog.SystemId("/status") == "Foundation"
                && AtelierCapture.Archive.RouteCountCatalog.SystemId("/battle/unrecognized")
                    is null,
            "shared route inventory does not invent arbitrary owner labels"
        );
        var retired = ledger.Begin(2001, "/status", false, null, null, "Foundation");
        var beforeRetirement = ledger.ActiveCount;
        ledger.Release(2001);
        ledger.Complete(retired);
        Check(
            ledger.ActiveCount == beforeRetirement - 1,
            "native retirement before deferred ACK cannot decrement active count twice"
        );
    }

    private static void StagingFailuresAndTerminalUnion()
    {
        var ledger = new RequestLedger();
        var attempt = ledger.Begin(1, "/status", false, null, null, "Foundation");
        var commits = new List<Action>();
        try
        {
            Network.StageResponse(
                attempt,
                () => throw new InvalidDataException("synthetic response field read failure"),
                commits
            );
            throw new InvalidOperationException(
                "Synthetic response staging failure was not raised."
            );
        }
        catch (InvalidDataException) { }
        Check(
            !attempt.ResponsePending && !attempt.ResponseWritten && commits.Count == 0,
            "failed response staging leaves the real original decoder free to record it later"
        );
        try
        {
            Network.StageTerminal(
                ledger,
                attempt,
                () => throw new InvalidDataException("synthetic native getter failure"),
                commits
            );
            throw new InvalidOperationException("Synthetic staging failure was not raised.");
        }
        catch (InvalidDataException) { }
        Check(
            !attempt.CompletionPending && !attempt.Completed && commits.Count == 0,
            "failed terminal staging cannot reserve future completion"
        );
        var terminals = new List<string>();
        Check(
            Network.StageTerminal(
                ledger,
                attempt,
                () => terminals.Add("request_exception"),
                commits
            )
                && attempt.CompletionPending
                && !attempt.Completed,
            "terminal is staged but unwritten before ACK"
        );
        Check(
            !Network.StageTerminal(
                ledger,
                attempt,
                () => terminals.Add("request_complete"),
                commits
            ),
            "dispose after send exception cannot stage a second terminal"
        );
        foreach (var commit in commits)
            commit();
        Check(
            attempt.Completed
                && !attempt.CompletionPending
                && ledger.ActiveCount == 0
                && !Network.StageTerminal(
                    ledger,
                    attempt,
                    () => terminals.Add("request_complete"),
                    commits
                )
                && terminals.SequenceEqual(new[] { "request_exception" }),
            "one durable terminal union survives late dispose"
        );
        Check(
            ReferenceEquals(ledger.SingleLifetime(1), attempt) && ledger.Unique(1) is null,
            "single completed native lifetime is distinguished from ambiguous reused request during disposal"
        );
        const string stage = "request:request_entity:synthetic";
        Check(attempt.ReserveStage(stage), "raw stage begins pending");
        attempt.CancelStage(stage);
        Check(
            attempt.ReserveStage(stage) && !attempt.StageWritten(stage),
            "failed raw staging rolls back for retry"
        );
        var laterCommitted = false;
        try
        {
            Network.CommitStagedActions(
                new Action[]
                {
                    () => throw new InvalidDataException("synthetic state commit failure"),
                    () => laterCommitted = true,
                }
            );
            throw new InvalidOperationException("Commit state fault was not surfaced.");
        }
        catch (AggregateException) { }
        Check(
            laterCommitted,
            "one failed state update cannot skip other acknowledged record state updates"
        );
    }

    private static void SharedNativeContext()
    {
        var first = new NetworkContext("1", "1", null, "/status", "Foundation", false, null, null);
        var identityOnly = new ContextBinding();
        Check(
            !identityOnly.Origin.HasEvidence && !identityOnly.State.EnvironmentHasEvidence,
            "lifetime identity alone does not assert an unknown origin application"
        );
        identityOnly.Origin.Bind(null, "actual-unknown-application");
        Check(
            identityOnly.Origin.HasEvidence
                && identityOnly.State.EnvironmentHasEvidence
                && identityOnly.State.Environment is null
                && identityOnly.Origin.Proof.QualifiedEdges.Count == 1,
            "an actual unknown application remains distinct from identity-only state"
        );
        var binding = new ContextBinding();
        binding.Bind(first);
        var response = first with { ResponseId = "1" };
        binding.Bind(response);
        binding.Bind(first);
        Check(
            binding.Current == response && !binding.Ambiguous,
            "same proven owner can gain a response ID without losing it"
        );
        binding.Bind(first with { RequestId = "2", AttemptId = "2" });
        binding.Bind(first);
        Check(
            binding.Current is null && binding.Ambiguous,
            "shared object never adopts conflicting or latest operation context"
        );
        Check(
            binding.Origin.Proof.IsPrivate == false && !binding.Origin.Proof.Ambiguous,
            "proven per-object environment survives operation ambiguity without borrowing operation IDs"
        );
        var semanticState = binding.State;
        binding.Origin.Bind(false, "additional-same-origin-edge");
        Check(
            binding.State == semanticState,
            "annotation-only edge additions cannot retrigger cyclic lineage traversal"
        );
        var inherited = new ContextBinding();
        inherited.Bind(first);
        inherited.InvalidateOperation();
        inherited.Bind(first);
        Check(
            inherited.Current is null
                && inherited.State.OperationAmbiguous
                && inherited.Origin.Proof.IsPrivate == false
                && !inherited.Origin.Proof.Ambiguous,
            "explicit inherited operation ambiguity stays monotone without losing independently proven origin"
        );
        binding.Origin.Bind(true, "synthetic-qualified-resource-application");
        binding.Origin.Bind(false, "synthetic-qualified-resource-application");
        Check(
            binding.Origin.Proof.IsPrivate is null && binding.Origin.Proof.Ambiguous,
            "conflicting native origins invalidate object environment for its lifetime"
        );
        var origin = new EnvironmentBinding();
        origin.Bind(false, "synthetic-native-constructor");
        origin.Bind(null, "synthetic-unknown-resource-application");
        Check(
            origin.Proof.IsPrivate is null
                && origin.Proof.Ambiguous
                && origin.Proof.QualifiedEdges.Count == 2,
            "unknown origin invalidates previous object proof and retains exact edge evidence"
        );
    }

    private static void ParsedRootAndLeafProofs()
    {
        var expected = new NetworkContext(
            "1",
            "1",
            "2",
            "/quest/battle/start",
            "Battle",
            false,
            "3",
            "exact_original_parse"
        );
        var root = new ContextBinding();
        root.Bind(expected);
        Check(
            NativeLineage.ParsedRootGap(expected, root.State) is null,
            "successful effective parse-root proof is complete before any constructor snapshot"
        );
        root.Bind(expected with { DescriptorId = "7" });
        Check(
            NativeLineage.ParsedRootGap(expected, root.State) is null,
            "nonconflicting root evidence enrichment is not a fabricated operation mismatch"
        );
        Check(
            NativeLineage.ParsedRootGap(null, root.State) == "native_parse_input_unbound"
                && NativeLineage.ParsedRootGap(expected, default)
                    == "native_parse_root_context_unavailable"
                && NativeLineage.ParsedRootGap(expected with { AttemptId = "8" }, root.State)
                    == "native_parse_root_context_mismatch",
            "unbound input, identity-only root and mismatched input cannot claim complete lineage"
        );
        var unknown = new ContextBinding();
        var unknownContext = expected with { IsPrivate = null };
        unknown.Bind(unknownContext);
        Check(
            NativeLineage.ParsedRootGap(unknownContext, unknown.State)
                == "native_parse_root_origin_unknown",
            "a native operation with unknown origin cannot imply complete source lineage"
        );
        foreach (var otherOrigin in new[] { false, true })
        {
            var leaf = new ContextBinding();
            leaf.Bind(expected with { RequestId = "8", AttemptId = "8", IsPrivate = otherOrigin });
            var gap = NativeLineage.Transfer(
                root,
                leaf,
                "stored_leaf",
                value => ((ContextBinding)value).State,
                (value, context, edge) => ((ContextBinding)value).Bind(context, edge),
                (value, origin, edge) => ((ContextBinding)value).Origin.Bind(origin, edge),
                value => ((ContextBinding)value).InvalidateOperation()
            );
            Check(
                gap == "stored_leaf:native_child_operation_ambiguous"
                    && leaf.Current is null
                    && leaf.Ambiguous
                    && leaf.Origin.Proof.IsPrivate == (otherOrigin ? null : false),
                "a conflicting child leaf must report its gap even when it has no children to revisit"
            );
        }
        root.Bind(expected with { RequestId = "8", AttemptId = "8" });
        Check(
            NativeLineage.ParsedRootGap(expected, root.State)
                == "native_parse_root_operation_ambiguous"
                && root.Origin.Proof.IsPrivate == false,
            "a same-origin conflicting leaf root cannot reuse its stale parse-entry context"
        );
        var originConflict = new ContextBinding();
        originConflict.Bind(expected);
        originConflict.Origin.Bind(true, "different_origin_application");
        Check(
            NativeLineage.ParsedRootGap(expected, originConflict.State)
                == "native_parse_root_origin_ambiguous",
            "a genuine root origin conflict cannot be labeled complete lineage"
        );
    }

    private static void StatusDuringCopy()
    {
        var statusField = typeof(Network).GetField(
            "status",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var closedField = typeof(PassiveNativeHook).GetField(
            "closed",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var previousStatus = statusField.GetValue(null)!;
        var previousClosed = closedField.GetValue(null);
        var constructor = previousStatus.GetType().GetConstructors().Single();
        var statusArguments = constructor
            .GetParameters()
            .Select(parameter => parameter.DefaultValue)
            .ToArray();
        statusArguments[0] = 23;
        statusArguments[1] = true;
        var installedStatus = constructor.Invoke(statusArguments);
        var copierGate = typeof(Network)
            .GetField("Sync", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var copier = Task.Run(() =>
        {
            lock (copierGate)
            {
                held.Set();
                release.Wait();
            }
        });
        try
        {
            statusField.SetValue(null, installedStatus);
            closedField.SetValue(null, 0);
            Check(held.Wait(TimeSpan.FromSeconds(5)), "synthetic native copier holds its gate");
            var status = Task.Run(Network.CaptureStatus);
            Check(
                status.Wait(TimeSpan.FromSeconds(5)),
                "status does not acquire stalled native copier lock"
            );
            var openStatus = JsonSerializer.SerializeToElement(status.Result);
            Check(
                openStatus.GetProperty("binding_complete").GetBoolean()
                    && !openStatus.GetProperty("native_observers_closed").GetBoolean()
                    && openStatus.GetProperty("active_attempts").GetInt32() == 23
                    && !openStatus.GetProperty("complete").GetBoolean(),
                "open native bindings retain detached active counts without claiming complete capture"
            );
            closedField.SetValue(null, 1);
            var closedStatusTask = Task.Run(Network.CaptureStatus);
            Check(
                closedStatusTask.Wait(TimeSpan.FromSeconds(5)),
                "closed status remains responsive while the native copier is held"
            );
            var closedStatus = JsonSerializer.SerializeToElement(closedStatusTask.Result);
            Check(
                !closedStatus.GetProperty("binding_complete").GetBoolean()
                    && closedStatus.GetProperty("native_observers_closed").GetBoolean()
                    && closedStatus.GetProperty("active_attempts").GetInt32() == 23
                    && !closedStatus.GetProperty("complete").GetBoolean()
                    && ReferenceEquals(statusField.GetValue(null), installedStatus),
                "global shutdown immediately demotes cached bindings without waiting for another publication"
            );
            Check(
                JsonSerializer
                    .SerializeToElement(status.Result)
                    .GetProperty("binding_complete")
                    .GetBoolean(),
                "earlier detached status remains immutable after global shutdown"
            );
        }
        finally
        {
            release.Set();
            copier.GetAwaiter().GetResult();
            closedField.SetValue(null, previousClosed);
            statusField.SetValue(null, previousStatus);
        }
    }

    private static void ContextScopes()
    {
        Check(Network.CurrentContext is null, "no global latest response context");
        var context = new NetworkContext(
            "7",
            "7",
            null,
            "/battle/start",
            "Battle",
            false,
            "3",
            null
        );
        using (Network.PushContext(context))
        {
            Check(
                ReferenceEquals(Network.CurrentContext, context),
                "proven callback context enters scope"
            );
            using (Network.PushContext(null))
                Check(
                    Network.CurrentContext is null,
                    "unbound nested callback does not inherit unrelated context"
                );
            Check(
                ReferenceEquals(Network.CurrentContext, context),
                "nested callback scope restores owner context"
            );
        }
        Check(Network.CurrentContext is null, "callback scope restores no-context state");
    }

    private static void DurableOriginalBatch()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "atelier-network-checks",
            Guid.NewGuid().ToString("N")
        );
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocked = 0;
        var hooks = new StorageHooks
        {
            Before = (action, _, _) =>
            {
                if (
                    action == StorageAction.Acknowledge
                    && Interlocked.Exchange(ref blocked, 1) == 0
                )
                {
                    reached.Set();
                    Check(
                        release.Wait(TimeSpan.FromSeconds(10)),
                        "release durable acknowledgment control"
                    );
                }
            },
        };
        var reports = new List<string>();
        using var writer = new SessionWriter(
            root,
            new { plugin = "network-synthetic", version = "check" },
            _ => new { complete = true, network = Network.MaterializationBoundary() },
            message => reports.Add(message),
            hooks
        );
        var ledger = new RequestLedger();
        var attempt = ledger.Begin(1, "/status", false, null, null, "Foundation");
        attempt.Context = attempt.Context with
        {
            DescriptorId = "17",
            LogicalRoute = "/status",
            NativeOperationId = "synthetic-original-nonuuid-id",
            RequestHeaderId = null,
            TransportPath = "/",
        };
        var raw = ApiBodies.Detach(
            attempt.Context,
            "response",
            "response_entity",
            Encoding.UTF8.GetBytes("{\"code\":\"synthetic_failure\"}")
        );
        const string stage = "response:response_entity:test";
        Check(attempt.ReserveStage(stage), "durable stage reservation");
        var commit = Task.Run(() =>
        {
            using (writer.BeginObservation())
            {
                writer.EmitBootstrap("api_capture_incomplete", Network.MaterializationBoundary());
                writer.Emit(
                    false,
                    false,
                    "request",
                    new
                    {
                        request_id = "1",
                        attempt_id = "1",
                        route = "/status",
                        logical_route = "/status",
                        transport_path = "/",
                        native_operation_id = "synthetic-original-nonuuid-id",
                        request_header_id = (string?)null,
                    },
                    systemId: "Foundation"
                );
                writer.Emit(false, false, "raw_body", raw, systemId: "Foundation");
                writer.Emit(
                    false,
                    false,
                    "response",
                    new
                    {
                        request_id = "1",
                        attempt_id = "1",
                        route = "/status",
                        status = 409,
                        body = new
                        {
                            capture_status = "incomplete",
                            reason = "synthetic_malformed_body",
                        },
                    },
                    systemId: "Foundation"
                );
                writer.Emit(
                    false,
                    false,
                    "request_complete",
                    new
                    {
                        request_id = "1",
                        attempt_id = "1",
                        route = "/status",
                    },
                    systemId: "Foundation"
                );
            }
            attempt.CommitStage(stage);
            attempt.ResponseWritten = true;
        });
        try
        {
            Check(
                reached.Wait(TimeSpan.FromSeconds(10)),
                "actual writer reached durable acknowledgment"
            );
            Check(
                !commit.IsCompleted && !attempt.ResponseWritten && !attempt.StageWritten(stage),
                "body flags remain unwritten until actual durable batch acknowledgment"
            );
        }
        finally
        {
            release.Set();
        }
        Check(
            commit.Wait(TimeSpan.FromSeconds(10))
                && attempt.ResponseWritten
                && attempt.StageWritten(stage),
            "written flags follow acknowledged complete batch"
        );
        var previousWriter = Plugin.Writer;
        string? lateRequestId = null;
        string? nativeFaultRequestId = null;
        try
        {
            Plugin.Writer = writer;
            var lateStatus = ledger.Begin(3, "/", false, null, null, null);
            lateStatus.Context = lateStatus.Context with
            {
                LogicalRoute = "/status",
                DescriptorId = "23",
                NativeOperationId = "synthetic-status-original-id",
                TransportPath = "/",
                SystemId = "Recorder",
            };
            lateRequestId = lateStatus.Context.RequestId;
            Check(
                ReferenceEquals(ledger.BindResponse(303, 3), lateStatus),
                "fallback response binds to its original request before disposal"
            );
            var lateBytes = Encoding.UTF8.GetBytes(
                "{\"token\":\"synthetic-status-token\",\"result\":true}"
            );
            writer.Emit(
                false,
                false,
                "request",
                new
                {
                    request_id = lateStatus.Context.RequestId,
                    attempt_id = lateStatus.Context.AttemptId,
                    route = "/",
                    transport_path = "/",
                },
                systemId: "Recorder"
            );
            var fallbackCommits = new List<Action>();
            Plugin.Observe(
                "synthetic_completion_before_native_decoder",
                () =>
                {
                    writer.Emit(
                        false,
                        false,
                        "raw_body",
                        ApiBodies.Detach(
                            lateStatus.Context,
                            "response",
                            "response_entity",
                            lateBytes
                        ),
                        systemId: "Recorder"
                    );
                    var decoded = Network.Decode(
                        lateBytes,
                        "response_entity",
                        lateStatus,
                        true,
                        true,
                        false,
                        false,
                        fallbackCommits
                    );
                    Check(
                        Network.StageResponse(
                            lateStatus,
                            () =>
                                writer.Emit(
                                    false,
                                    false,
                                    "response",
                                    new
                                    {
                                        request_id = lateStatus.Context.RequestId,
                                        attempt_id = lateStatus.Context.AttemptId,
                                        route = "/",
                                        logical_route = "/status",
                                        status = 200,
                                        body = decoded,
                                        source = "completion_fallback",
                                    },
                                    systemId: "Recorder"
                                ),
                            fallbackCommits
                        )
                            && !lateStatus.ResponseWritten
                            && lateStatus.ResponsePending,
                        "fallback response stages in the terminal batch without early written acknowledgment"
                    );
                    Network.StageTerminal(
                        ledger,
                        lateStatus,
                        () =>
                            writer.Emit(
                                false,
                                false,
                                "request_complete",
                                new
                                {
                                    request_id = lateStatus.Context.RequestId,
                                    attempt_id = lateStatus.Context.AttemptId,
                                    route = "/",
                                    response_recorded = lateStatus.ResponseWritten
                                        || lateStatus.ResponsePending,
                                    response_pending_commit = lateStatus.ResponsePending,
                                },
                                systemId: "Recorder"
                            ),
                        fallbackCommits
                    );
                },
                () => Network.CommitStagedActions(fallbackCommits)
            );
            ledger.Release(3);
            var decodedAttemptAfterDisposal = ledger.Response(303);
            Check(
                ReferenceEquals(decodedAttemptAfterDisposal, lateStatus)
                    && lateStatus.ResponseWritten
                    && lateStatus.Completed,
                "the original decoder can still use its pre-proven response after the HTTP owner is retired"
            );
            var decodedCommits = new List<Action>();
            Plugin.Observe(
                "synthetic_native_decoder_after_disposal",
                () =>
                {
                    Network.OriginalDecodedBody(
                        decodedAttemptAfterDisposal!,
                        lateBytes,
                        200,
                        decodedCommits
                    );
                    Check(
                        !Network.StageResponse(
                            lateStatus,
                            () =>
                                throw new InvalidOperationException(
                                    "duplicate response after disposal"
                                ),
                            decodedCommits
                        ),
                        "late original decoder retains a separate byte stage without duplicating the fallback API response"
                    );
                },
                () => Network.CommitStagedActions(decodedCommits)
            );
            Check(
                lateStatus.HasStage("response:native_decoder_result"),
                "original native decoder bytes become written only after the actual durable ACK"
            );
            Plugin.Observe(
                "synthetic_unclassified_request",
                () =>
                {
                    Network.Emit(
                        null,
                        "request",
                        new { request_id = "unclassified", route = "/unknown/request" }
                    );
                    Network.Emit(
                        null,
                        "response",
                        new { body = new byte[] { 0, 1, 255 }, status = 503 }
                    );
                }
            );
            var failed = ledger.Begin(2, "/status", false, null, null, "Foundation");
            nativeFaultRequestId = failed.Context.RequestId;
            writer.Emit(
                false,
                false,
                "request",
                new
                {
                    request_id = failed.Context.RequestId,
                    attempt_id = failed.Context.AttemptId,
                    route = "/status",
                },
                systemId: "Foundation"
            );
            var stagedCommits = new List<Action>();
            void Commit()
            {
                foreach (var staged in stagedCommits)
                    staged();
            }
            Plugin.Observe(
                "synthetic_network_late_read_fault",
                () =>
                {
                    Network.StageNativeSendFault(ledger, failed, stagedCommits);
                    throw new InvalidDataException("synthetic field read after successful staging");
                },
                Commit,
                Commit
            );
            Check(
                failed.Completed && !failed.CompletionPending,
                "successfully staged terminal commits after ACK despite a later copy fault"
            );
            Check(
                !Network.StageTerminal(
                    ledger,
                    failed,
                    () => throw new InvalidOperationException("duplicate terminal"),
                    stagedCommits
                ),
                "partial observer failure does not allow dispose to duplicate the durable terminal"
            );
            foreach (var decoderResult in new byte[]?[] { Array.Empty<byte>(), null })
            {
                var decodedAttempt = ledger.Begin(
                    100 + ledger.ActiveCount,
                    "/status",
                    false,
                    null,
                    null,
                    "Foundation"
                );
                writer.Emit(
                    false,
                    false,
                    "request",
                    new
                    {
                        request_id = decodedAttempt.Context.RequestId,
                        attempt_id = decodedAttempt.Context.AttemptId,
                        route = "/status",
                    },
                    systemId: "Foundation"
                );
                var resultCommits = new List<Action>();
                object? decodedBody = null;
                Plugin.Observe(
                    "synthetic_original_decoder_result",
                    () =>
                    {
                        decodedBody = Network.OriginalDecodedBody(
                            decodedAttempt,
                            decoderResult,
                            200,
                            resultCommits
                        );
                        writer.Emit(
                            false,
                            false,
                            "response",
                            new
                            {
                                request_id = decodedAttempt.Context.RequestId,
                                attempt_id = decodedAttempt.Context.AttemptId,
                                route = "/status",
                                status = 200,
                                body = decodedBody,
                            },
                            systemId: "Foundation"
                        );
                        writer.Emit(
                            false,
                            false,
                            "request_complete",
                            new
                            {
                                request_id = decodedAttempt.Context.RequestId,
                                attempt_id = decodedAttempt.Context.AttemptId,
                                route = "/status",
                            },
                            systemId: "Foundation"
                        );
                    },
                    () => Network.CommitStagedActions(resultCommits)
                );
                var bodyElement = JsonSerializer.SerializeToElement(decodedBody);
                Check(
                    decoderResult is null
                        ? bodyElement.GetProperty("reason").GetString()
                            == "native_decoder_returned_null"
                            && !decodedAttempt.HasStage("response:native_decoder_result")
                        : bodyElement.GetProperty("bytes").GetInt32() == 0
                            && decodedAttempt.HasStage("response:native_decoder_result"),
                    "original native decoder distinguishes absent output from an observed empty body"
                );
            }
        }
        finally
        {
            Plugin.Writer = previousWriter;
        }
        writer.Dispose();
        var zipPaths = Directory.GetFiles(Path.Combine(root, "Official"), "*.zip");
        Check(zipPaths.Length == 1, "synthetic archive produced: " + string.Join("; ", reports));
        var zipPath = zipPaths.Single();
        using var archive = ZipFile.OpenRead(zipPath);
        using (var apiReader = new StreamReader(archive.GetEntry("api.jsonl")!.Open()))
        {
            using var firstRequest = JsonDocument.Parse(apiReader.ReadLine()!);
            var original = firstRequest.RootElement.GetProperty("data");
            Check(
                original.GetProperty("route").GetString() == "/status"
                    && original.GetProperty("transport_path").GetString() == "/"
                    && original.GetProperty("native_operation_id").GetString()
                        == "synthetic-original-nonuuid-id"
                    && original.GetProperty("request_header_id").ValueKind == JsonValueKind.Null,
                "actual writer/archive retains logical route versus physical path and unmodified original identifiers"
            );
            var lateStatusDecoded = false;
            var lateResponseCount = 0;
            string? apiLine;
            while ((apiLine = apiReader.ReadLine()) is not null)
            {
                using var api = JsonDocument.Parse(apiLine);
                var value = api.RootElement.GetProperty("data");
                if (
                    api.RootElement.GetProperty("kind").GetString() == "response"
                    && value.GetProperty("request_id").GetString() == lateRequestId
                )
                {
                    lateResponseCount++;
                    lateStatusDecoded =
                        value.GetProperty("route").GetString() == "/"
                        && value.GetProperty("logical_route").GetString() == "/status"
                        && value.GetProperty("body").GetProperty("source").GetString()
                            == "derived_json"
                        && value
                            .GetProperty("body")
                            .GetProperty("payload")
                            .GetProperty("token")
                            .GetString() == "synthetic-status-token";
                }
            }
            Check(
                lateStatusDecoded && lateResponseCount == 1,
                "late proven status logical route controls actual JSON decode while durable API route remains physical"
            );
        }
        using var reader = new StreamReader(archive.GetEntry("raw-bodies.jsonl")!.Open());
        using var record = JsonDocument.Parse(reader.ReadLine()!);
        var data = record.RootElement.GetProperty("data");
        var stored = data.GetProperty("body").GetBytesFromBase64();
        Check(
            stored.SequenceEqual(raw.body)
                && data.GetProperty("sha256").GetString()
                    == Convert.ToHexString(SHA256.HashData(stored))
                && data.GetProperty("logical_route").GetString() == "/status"
                && data.GetProperty("transport_path").GetString() == "/"
                && data.GetProperty("native_operation_id").GetString()
                    == "synthetic-original-nonuuid-id",
            "actual archived non-success original body recomputed independently"
        );
        var originalDecoderBody = false;
        var emptyDecoderBody = false;
        string? rawLine;
        while ((rawLine = reader.ReadLine()) is not null)
        {
            using var rawRecord = JsonDocument.Parse(rawLine);
            var rawData = rawRecord.RootElement.GetProperty("data");
            if (rawData.GetProperty("stage").GetString() != "native_decoder_result")
                continue;
            var originalBytes = rawData.GetProperty("body").GetBytesFromBase64();
            Check(
                rawData.GetProperty("sha256").GetString()
                    == Convert.ToHexString(SHA256.HashData(originalBytes)),
                "original decoder result hash is independently verified from final ZIP bytes"
            );
            if (rawData.GetProperty("request_id").GetString() == lateRequestId)
                originalDecoderBody =
                    Encoding.UTF8.GetString(originalBytes)
                    == "{\"token\":\"synthetic-status-token\",\"result\":true}";
            else if (originalBytes.Length == 0)
                emptyDecoderBody = true;
        }
        Check(
            originalDecoderBody && emptyDecoderBody,
            "final ZIP retains full and empty original decoder results while absent output stays explicitly missing"
        );
        using var diagnosticReader = new StreamReader(
            archive.GetEntry("diagnostics.jsonl")!.Open()
        );
        var diagnosticKinds = new List<string?>();
        var assignmentBoundary = false;
        var nativeFaultRecords = 0;
        var lateCompletionRecordedResponse = false;
        string? line;
        while ((line = diagnosticReader.ReadLine()) is not null)
        {
            using var diagnostic = JsonDocument.Parse(line);
            diagnosticKinds.Add(diagnostic.RootElement.GetProperty("kind").GetString());
            var sourceRecord = diagnostic.RootElement;
            if (sourceRecord.GetProperty("kind").GetString() == "process_bootstrap")
                sourceRecord = sourceRecord.GetProperty("data").GetProperty("original_record");
            if (
                sourceRecord.GetProperty("kind").GetString() == "api_capture_incomplete"
                && sourceRecord.GetProperty("data").TryGetProperty("data", out var boundary)
                && boundary.GetProperty("reason").GetString() == "shared_native_body_unqualified"
            )
            {
                assignmentBoundary =
                    boundary.GetProperty("unsupported").GetArrayLength() == 2
                    && boundary.GetProperty("preserved").GetArrayLength() == 8
                    && boundary
                        .GetProperty("preserved")
                        .EnumerateArray()
                        .Any(stage =>
                            stage.GetString() == "native_decoder_result_at_original_return"
                        )
                    && boundary
                        .GetProperty("preserved")
                        .EnumerateArray()
                        .Any(stage =>
                            stage.GetString() == "native_parse_input_at_original_MergeFrom"
                        )
                    && boundary
                        .GetProperty("preserved")
                        .EnumerateArray()
                        .Any(stage =>
                            stage.GetString()
                            == "native_parse_applied_at_original_success_known_fields"
                        );
            }
            if (sourceRecord.GetProperty("kind").GetString() == "request_exception")
            {
                var fault = sourceRecord.GetProperty("data");
                if (fault.GetProperty("request_id").GetString() == nativeFaultRequestId)
                {
                    Check(
                        fault.GetProperty("error").GetString() == "native_original_threw",
                        "native fault retains the exact qualified outcome label"
                    );
                    nativeFaultRecords++;
                }
            }
            if (sourceRecord.GetProperty("kind").GetString() == "request_complete")
            {
                var completion = sourceRecord.GetProperty("data");
                if (completion.GetProperty("request_id").GetString() == lateRequestId)
                    lateCompletionRecordedResponse =
                        completion.GetProperty("response_recorded").GetBoolean()
                        && completion.GetProperty("response_pending_commit").GetBoolean();
            }
        }
        Check(
            diagnosticKinds.Contains("unclassified_request")
                && diagnosticKinds.Contains("unclassified_response")
                && !diagnosticKinds.Contains("request")
                && !diagnosticKinds.Contains("response"),
            "unknown records after known environment retain typed diagnostics without breaking archive stream validation"
        );
        Check(
            assignmentBoundary,
            "unsafe aliased assignment boundaries remain explicitly unsupported while full entity originals archive"
        );
        Check(
            nativeFaultRecords == 1,
            "native original fault has one durable terminal without a fabricated managed exception type"
        );
        Check(
            lateCompletionRecordedResponse,
            "durable terminal truthfully includes a response staged in its own acknowledged atomic batch"
        );
        using var manifestReader = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestReader);
        Check(
            manifest
                .RootElement.GetProperty("count_summary")
                .GetProperty("completeness")
                .GetProperty("reasons")
                .EnumerateArray()
                .Any(reason => reason.GetString() == "producer_status_incomplete"),
            "unsupported assignment history cannot silently certify a complete archive"
        );
    }

    private static void NativeSnapshotArchive()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "atelier-network-snapshot-checks",
            Guid.NewGuid().ToString("N")
        );
        using var writer = new SessionWriter(
            root,
            new { plugin = "network-snapshot-synthetic" },
            _ => new { complete = true },
            _ => { }
        );
        var attempt = new RequestLedger().Begin(1, "/status", false, null, null, "Foundation");
        var bodiless = new RequestLedger().Begin(2, "/status", false, null, null, "Foundation");
        bodiless.Context = bodiless.Context with { RequestId = "2", AttemptId = "2" };
        var unresolved = new RequestLedger().Begin(3, "/status", false, null, null, "Foundation");
        unresolved.Context = unresolved.Context with { RequestId = "3", AttemptId = "3" };
        const string nativeType = "Google.Protobuf.Reflection.FileDescriptorProto";
        byte[] input = [0x0A, 0x01, 0x78, 0xA0, 0x06, 0x01];
        byte[] before = [0xA0, 0x06, 0x02];
        byte[] after = [0x0A, 0x01, 0x78, 0xA0, 0x06, 0x03];
        var fullMessage = new string('x', 70000) + "-synthetic-full-message-end";
        var previous = Plugin.Writer;
        try
        {
            Plugin.Writer = writer;
            var nullCommits = new List<Action>();
            Plugin.Observe(
                "synthetic_bodiless_GetEntityBody",
                () =>
                {
                    Network.RecordNullRequestEntity(bodiless, nullCommits);
                    Check(
                        !bodiless.RequestEntityReturnedNull,
                        "native null result is not certified before durable ACK"
                    );
                },
                () => Network.CommitStagedActions(nullCommits)
            );
            Check(
                bodiless.RequestEntityReturnedNull,
                "native null result becomes qualified evidence after durable ACK"
            );
            var headerStateType = typeof(Network).GetNestedType(
                "HeaderSendState",
                BindingFlags.NonPublic
            )!;
            var headerState = Activator.CreateInstance(
                headerStateType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                [new MemoryStream()],
                null
            )!;
            headerStateType
                .GetField("Attempt", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(headerState, bodiless);
            headerStateType
                .GetField("Context", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(headerState, bodiless.Context);
            var headerGroups =
                (List<HeaderGroup>)
                    headerStateType
                        .GetField("Groups", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(headerState)!;
            headerGroups.Add(new HeaderGroup("Content-Length", ["0"]));
            typeof(Network)
                .GetMethod("EndHeaderSend", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [headerState, false]);
            Check(
                bodiless.SerializedRequestHeaders is { complete: true }
                    && bodiless.SerializedRequestHeaders.groups[0].values![0] == "0",
                "actual header-end observer stores immutable original groups after successful durable return"
            );
            Plugin.Observe(
                "synthetic_bodiless_request_completion",
                () =>
                    Network.RecordRequestEntityCompletion(
                        bodiless,
                        new RequestEntityFraming(
                            "Get",
                            false,
                            bodiless.RequestEntityReturnedNull,
                            bodiless.SerializedRequestHeaders
                        )
                    )
            );
            Plugin.Observe(
                "synthetic_unresolved_request_completion",
                () =>
                    Network.RecordRequestEntityCompletion(
                        unresolved,
                        new RequestEntityFraming(
                            "Get",
                            true,
                            true,
                            bodiless.SerializedRequestHeaders
                        )
                    )
            );
            writer.Emit(
                false,
                false,
                "request",
                new
                {
                    request_id = attempt.Context.RequestId,
                    attempt_id = attempt.Context.AttemptId,
                    route = "/status",
                },
                systemId: "Foundation"
            );
            using var context = Network.PushContext(attempt.Context);
            Plugin.Observe(
                "synthetic_native_snapshot_preservation",
                () =>
                {
                    Network.Emit(
                        attempt.Context,
                        "native_parse_input",
                        new
                        {
                            native_message_type = nativeType,
                            stage = "native_parse_input",
                            body = input,
                            message_before = Network.BinaryMessageSnapshot(nativeType, before),
                            message_snapshot_fields = Network.MessageSnapshotFields(nativeType),
                            source = "original_native_MergeFrom_argument",
                        }
                    );
                    Network.Emit(
                        attempt.Context,
                        "native_parse_applied",
                        new
                        {
                            native_message_type = nativeType,
                            payload = Network.BinaryMessageSnapshot(nativeType, after),
                            payload_fields = Network.MessageSnapshotFields(nativeType),
                            original_returned = true,
                        }
                    );
                    var read = typeof(Network)
                        .GetMethod("ReadAvailable", BindingFlags.Static | BindingFlags.NonPublic)!
                        .MakeGenericMethod(typeof(object));
                    Check(
                        read.Invoke(
                            null,
                            [
                                new Func<object?>(() =>
                                    throw new InvalidOperationException(
                                        fullMessage,
                                        new InvalidDataException("synthetic retained inner cause")
                                    )
                                ),
                                "synthetic_snapshot_read_failed",
                            ]
                        )
                            is null,
                        "failed optional read preserves the containing callback"
                    );
                    Network.Emit(
                        attempt.Context,
                        "synthetic_available_input_after_failed_read",
                        new { body = input }
                    );
                    var partial = Network.CopyHeaderGroup(
                        "X-Synthetic",
                        () => 2,
                        index =>
                            index == 1
                                ? throw new InvalidOperationException(
                                    "synthetic header failure",
                                    new InvalidDataException("synthetic header inner cause")
                                )
                                : "available-before-failure"
                    );
                    Network.Emit(attempt.Context, "synthetic_partial_header", partial);
                    var malformed = Network.Decode(
                        [0xFF],
                        "response_entity",
                        attempt,
                        true,
                        true,
                        false,
                        false,
                        []
                    );
                    Network.Emit(attempt.Context, "synthetic_failed_decode", malformed);
                }
            );
            var proof = EmptyEntityProof();
            foreach (var phase in new[] { "response_callback", "before_dispose", "decoder_entry" })
                writer.Emit(
                    false,
                    false,
                    "response_entity_observation",
                    new
                    {
                        request_id = attempt.Context.RequestId,
                        phase,
                        original_native_value = (byte[]?)null,
                        framing = proof,
                        entity_status = "verified_empty_by_native_framing",
                    },
                    systemId: "Foundation"
                );
            var fallbackCommits = new List<Action>();
            Plugin.Observe(
                "synthetic_empty_fallback",
                () =>
                {
                    Check(
                        Network.StageResponse(
                            attempt,
                            () =>
                                Network.Emit(
                                    attempt.Context,
                                    "response",
                                    new
                                    {
                                        request_id = attempt.Context.RequestId,
                                        attempt_id = attempt.Context.AttemptId,
                                        route = "/status",
                                        status = 200,
                                        body = Network.EmptyEntityBody(proof),
                                    }
                                ),
                            fallbackCommits
                        ),
                        "null entity fallback stages one API response"
                    );
                },
                () => Network.CommitStagedActions(fallbackCommits)
            );
            var decoderCommits = new List<Action>();
            Plugin.Observe(
                "synthetic_actual_empty_decoder_return",
                () =>
                {
                    Network.RecordDecodedBody(attempt, [], 200, decoderCommits);
                    Check(
                        !Network.StageResponse(
                            attempt,
                            () => throw new InvalidOperationException("duplicate API response"),
                            decoderCommits
                        ),
                        "actual decoder enrichment cannot duplicate an earlier fallback response"
                    );
                },
                () => Network.CommitStagedActions(decoderCommits)
            );
            writer.Emit(
                false,
                false,
                "request_complete",
                new
                {
                    request_id = attempt.Context.RequestId,
                    attempt_id = attempt.Context.AttemptId,
                    route = "/status",
                },
                systemId: "Foundation"
            );
        }
        finally
        {
            Plugin.Writer = previous;
        }
        writer.Dispose();
        using var archive = ZipFile.OpenRead(
            Directory.GetFiles(Path.Combine(root, "Official"), "*.zip").Single()
        );
        var records = new List<JsonElement>();
        using (var diagnostics = new StreamReader(archive.GetEntry("diagnostics.jsonl")!.Open()))
        {
            string? line;
            while ((line = diagnostics.ReadLine()) is not null)
                records.Add(JsonSerializer.Deserialize<JsonElement>(line));
        }
        JsonElement Data(string kind) =>
            records
                .Single(record => record.GetProperty("kind").GetString() == kind)
                .GetProperty("data");
        foreach (
            var item in new[]
            {
                (
                    Kind: "native_parse_input",
                    Field: "message_before",
                    Bytes: before,
                    Label: "message_snapshot_fields"
                ),
                (
                    Kind: "native_parse_applied",
                    Field: "payload",
                    Bytes: after,
                    Label: "payload_fields"
                ),
            }
        )
        {
            var data = Data(item.Kind);
            var snapshot = data.GetProperty(item.Field);
            var stored = snapshot.GetProperty("body").GetBytesFromBase64();
            Check(
                stored.SequenceEqual(item.Bytes)
                    && snapshot.GetProperty("bytes").GetInt64() == stored.Length
                    && snapshot.GetProperty("sha256").GetString()
                        == Convert.ToHexString(SHA256.HashData(stored))
                    && snapshot.GetProperty("representation").GetString()
                        == "derived_native_message_binary"
                    && snapshot.GetProperty("native_message_type").GetString() == nativeType
                    && snapshot.GetProperty("json_status").GetString()
                        == "inapplicable_for_self_descriptor_snapshot"
                    && snapshot.GetProperty("unknown_fields_preserved").GetBoolean()
                    && data.GetProperty(item.Label).GetString()
                        == Network.MessageSnapshotFields(nativeType),
                "ZIP keeps complete before/after derived binary state with independent hashes and truthful field labels"
            );
        }
        Check(
            Data("native_parse_input").GetProperty("body").GetBytesFromBase64().SequenceEqual(input)
                && Data("synthetic_available_input_after_failed_read")
                    .GetProperty("body")
                    .GetBytesFromBase64()
                    .SequenceEqual(input),
            "original MergeFrom argument bytes remain separate from derived state and survive optional snapshot failure"
        );
        var failedRead = records
            .Single(record =>
                record.GetProperty("kind").GetString() == "api_capture_incomplete"
                && record.GetProperty("data").GetProperty("reason").GetString()
                    == "synthetic_snapshot_read_failed"
            )
            .GetProperty("data")
            .GetProperty("details");
        Check(
            failedRead.GetProperty("error").GetString() == nameof(InvalidDataException)
                && failedRead
                    .GetProperty("exception")
                    .GetString()!
                    .Contains(fullMessage, StringComparison.Ordinal)
                && failedRead
                    .GetProperty("exception")
                    .GetString()!
                    .Contains("synthetic retained inner cause", StringComparison.Ordinal)
                && failedRead
                    .GetProperty("exception")
                    .GetString()!
                    .Contains(nameof(NativeSnapshotArchive), StringComparison.Ordinal),
            "persistent failed read retains untruncated message, inner cause and stack"
        );
        var header = Data("synthetic_partial_header");
        var requestNull = records
            .Where(record => record.GetProperty("kind").GetString() == "request_entity_observation")
            .Select(record => record.GetProperty("data"))
            .ToArray();
        Check(
            requestNull.Length == 2
                && requestNull.All(data =>
                    data.GetProperty("original_native_value").ValueKind == JsonValueKind.Null
                )
                && requestNull.Any(data =>
                    data.GetProperty("entity_status").GetString()
                    == "verified_bodiless_by_original_serialized_headers"
                )
                && !records.Any(record =>
                    record.GetProperty("kind").GetString() == "api_capture_incomplete"
                    && record
                        .GetProperty("data")
                        .GetProperty("details")
                        .TryGetProperty("request_id", out var requestId)
                    && requestId.GetString() == "2"
                ),
            "saved bodiless GET has null observation/proof without fake array or false missing-body gap"
        );
        Check(
            records.Any(record =>
                record.GetProperty("kind").GetString() == "api_capture_incomplete"
                && record.GetProperty("data").GetProperty("reason").GetString()
                    == "request_entity_never_materialized"
                && record
                    .GetProperty("data")
                    .GetProperty("details")
                    .GetProperty("request_id")
                    .GetString() == "3"
            ),
            "upload-stream ambiguity remains a real persisted request gap"
        );
        Check(
            !header.GetProperty("complete").GetBoolean()
                && header.GetProperty("values")[0].GetString() == "available-before-failure"
                && header
                    .GetProperty("exception")
                    .GetString()!
                    .Contains("synthetic header inner cause", StringComparison.Ordinal),
            "archive retains partial header data and complete native-read diagnostics"
        );
        var decode = Data("synthetic_failed_decode");
        Check(
            decode.GetProperty("error").GetString() == nameof(DecoderFallbackException)
                && decode
                    .GetProperty("exception")
                    .GetString()!
                    .Contains(nameof(DecoderFallbackException), StringComparison.Ordinal)
                && records.Any(record =>
                    record.GetProperty("kind").GetString() == "api_capture_incomplete"
                    && record.GetProperty("data").GetProperty("reason").GetString()
                        == "body_decode_failed"
                    && record
                        .GetProperty("data")
                        .GetProperty("details")
                        .GetProperty("exception")
                        .GetString() == decode.GetProperty("exception").GetString()
                ),
            "failed decoder return and persistent gap retain the same full exception"
        );
        var phases = records
            .Where(record =>
                record.GetProperty("kind").GetString() == "response_entity_observation"
            )
            .Select(record => record.GetProperty("data"))
            .ToArray();
        Check(
            phases.Length == 3
                && phases.All(data =>
                    data.GetProperty("original_native_value").ValueKind == JsonValueKind.Null
                    && data.GetProperty("framing")
                        .GetProperty("headers")
                        .GetProperty("complete")
                        .GetBoolean()
                )
                && phases.Select(data => data.GetProperty("phase").GetString()).Distinct().Count()
                    == 3,
            "three null observations preserve phase and original native null rather than inventing body arrays"
        );
        var enrichment = Data("response_decoder_observation");
        Check(
            enrichment.GetProperty("api_response_already_staged").GetBoolean()
                && enrichment.GetProperty("body").GetProperty("bytes").GetInt32() == 0
                && enrichment.GetProperty("source").GetString() == "original_native_decoder_return",
            "actual empty decoder return enriches the fallback separately without losing decoded fields"
        );
        using (var api = new StreamReader(archive.GetEntry("api.jsonl")!.Open()))
        {
            var responses = new List<JsonElement>();
            string? line;
            while ((line = api.ReadLine()) is not null)
            {
                var record = JsonSerializer.Deserialize<JsonElement>(line);
                if (record.GetProperty("kind").GetString() == "response")
                    responses.Add(record.GetProperty("data"));
            }
            Check(
                responses.Count == 1
                    && responses[0]
                        .GetProperty("body")
                        .GetProperty("original_native_value")
                        .ValueKind == JsonValueKind.Null
                    && responses[0]
                        .GetProperty("body")
                        .GetProperty("native_decoder_result_recorded_only_at_original_return")
                        .GetBoolean(),
                "saved API fallback is unique and does not claim a future decoder output was already recorded"
            );
        }
        using var raw = new StreamReader(archive.GetEntry("raw-bodies.jsonl")!.Open());
        using var rawRecord = JsonDocument.Parse(raw.ReadLine()!);
        var rawBody = rawRecord.RootElement.GetProperty("data");
        Check(
            rawBody.GetProperty("stage").GetString() == "native_decoder_result"
                && rawBody.GetProperty("body").GetBytesFromBase64().Length == 0
                && rawBody.GetProperty("sha256").GetString()
                    == Convert.ToHexString(SHA256.HashData([]))
                && raw.ReadLine() is null,
            "only the actual returned empty array becomes a raw stage; null entity creates none"
        );
        using var manifest = JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
        Check(
            manifest
                .RootElement.GetProperty("count_summary")
                .GetProperty("api")
                .GetProperty("response_records")
                .GetString() == "1",
            "independent archive recount does not count diagnostic enrichment as another API response"
        );
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value > 127)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidDataException("Network regression: " + name);
    }
}

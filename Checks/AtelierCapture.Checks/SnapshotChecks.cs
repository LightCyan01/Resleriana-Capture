using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Nodes;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class SnapshotChecks
{
    internal static void Run()
    {
        LocalContractsReconstructExactMetadataAndValues();
        StoredValuesKeepEveryBitAndFreshIdentity();
        TimelineMembershipKeepsOrderAndFullArgumentUpgrade();
        IdentityLookupDoesNotBypassDifferentSemanticViews();
        ContractEncodingWorkload();
        OptionalNullAnnotationsPreserveRequiredNullsAndEmptyValues();
        EncryptedDateStorageKeepsRawFields();
        ClientStatusReadSetKeepsInputsFailuresAndFullRootUpgrade();
        ClientStatusReferenceIsCopiedOnceAndItsFailureStaysExplicit();
        RenderExclusionsStayOnExactOwnerFields();
        ResourceReceiversKeepArgumentsWithoutReadingOwnerState();
        GameplayFieldPoliciesUseExactStoredContracts();
    }

    private static void GameplayFieldPoliciesUseExactStoredContracts()
    {
        var references = new[]
        {
            (
                "BattleStateVariables",
                "_ENPKCFFLCFM_k__BackingField",
                "CGGKFDGOFGN",
                "historical_state_not_recopied"
            ),
            (
                "BattleStateVariables",
                "m_actionInfoList",
                "Il2CppSystem.Collections.Generic.List`1<JCDJDPMCAPB>",
                "historical_state_not_recopied"
            ),
            (
                "BattleStateVariables",
                "m_actionSetupInfoList",
                "Il2CppSystem.Collections.Generic.List`1<HMIEBOKCGFE>",
                "historical_state_not_recopied"
            ),
            (
                "NHEIMMIAMJO",
                "EJIDBLHEBNI",
                "BattleUnitTimelineControl",
                "presentation_controller_state_not_observed"
            ),
            (
                "NHEIMMIAMJO",
                "KACMCAGJPLP",
                "BattleUnitDamageControl",
                "presentation_controller_state_not_observed"
            ),
            (
                "NHEIMMIAMJO",
                "DLHMKEBENAD",
                "BattleUnitMoveControl",
                "presentation_controller_state_not_observed"
            ),
            (
                "NHEIMMIAMJO",
                "AJDOKPAONGN",
                "GCNOLOEMKPL",
                "presentation_controller_state_not_observed"
            ),
            (
                "NHEIMMIAMJO",
                "MMELJBOFGCL",
                "GCNOLOEMKPL",
                "presentation_controller_state_not_observed"
            ),
            (
                "NHEIMMIAMJO",
                "ELFKENAPCFJ",
                "GCNOLOEMKPL",
                "presentation_controller_state_not_observed"
            ),
        };
        foreach (var (owner, field, type, contract) in references)
        {
            Require(
                NativeProjection.ReferenceContract(owner, field, type, "Blend.dll") == contract
                    && NativeProjection.ReferenceContract(
                        "Other." + owner,
                        field,
                        type,
                        "Blend.dll"
                    )
                        is null
                    && NativeProjection.ReferenceContract(owner, field + "2", type, "Blend.dll")
                        is null
                    && NativeProjection.ReferenceContract(owner, field, type + "2", "Blend.dll")
                        is null
                    && NativeProjection.ReferenceContract(owner, field, type, "Other.dll") is null,
                "A gameplay reference matched a different owner, field, stored type or module."
            );
        }
        foreach (
            var field in new[] { "DDHFCNJJNGM", "OMJHJKLJGJM", "LFOANAGBMHA", "future_complex" }
        )
            Require(
                NativeProjection.ReferenceContract(
                    "NHEIMMIAMJO",
                    field,
                    "UnknownGameplayModel",
                    "Blend.dll"
                )
                    is null,
                "A condition or unknown complex model became an identity-only omission."
            );

        var sceneFields = new Dictionary<string, string[]>
        {
            ["BattleStateVariables"] = ["PDHCIEFKGCL"],
            ["NHEIMMIAMJO"] =
            [
                "_ACOOGLOBFLJ_k__BackingField",
                "_IKFKOKDHMLD_k__BackingField",
                "_KKFMOCDNELD_k__BackingField",
                "_EPNOCCFKJDK_k__BackingField",
                "NKCLGCOJLEI",
                "OFJELAMLOLC",
                "EHDNBKDIAIB",
                "LOJIMGGCEEI",
                "FHDPNPDBGAK",
                "ICNHFKBCLNB",
                "KGLPBLOPJHB",
                "NGKHMOEABGA",
                "OCIPKHBPJOM",
                "_MNHEFKOOGNP_k__BackingField",
                "_MHHMNDPHOBC_k__BackingField",
            ],
        };
        using var module = Mono.Cecil.ModuleDefinition.CreateModule(
            "Blend.dll",
            Mono.Cecil.ModuleKind.Dll
        );
        module.Assembly.Name.Name = "GameplayFieldProof" + Guid.NewGuid().ToString("N");
        var vector = new Mono.Cecil.TypeDefinition(
            "UnityEngine",
            "Vector3",
            Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
            module.TypeSystem.Object
        );
        module.Types.Add(vector);
        foreach (var (owner, fields) in sceneFields)
        {
            var declaring = new Mono.Cecil.TypeDefinition(
                "",
                owner,
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
                module.TypeSystem.Object
            );
            module.Types.Add(declaring);
            foreach (var name in fields.Append("future_gameplay_position"))
            {
                var property = new Mono.Cecil.PropertyDefinition(
                    name,
                    Mono.Cecil.PropertyAttributes.None,
                    vector
                );
                var getter = new Mono.Cecil.MethodDefinition(
                    "get_" + name,
                    Mono.Cecil.MethodAttributes.Public
                        | Mono.Cecil.MethodAttributes.SpecialName
                        | Mono.Cecil.MethodAttributes.HideBySig,
                    vector
                );
                var il = getter.Body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldstr, "excluded-getter-must-not-run");
                il.Emit(
                    Mono.Cecil.Cil.OpCodes.Newobj,
                    module.ImportReference(
                        typeof(InvalidOperationException).GetConstructor([typeof(string)])!
                    )
                );
                il.Emit(Mono.Cecil.Cil.OpCodes.Throw);
                property.GetMethod = getter;
                declaring.Methods.Add(getter);
                declaring.Properties.Add(property);
            }
            foreach (var name in fields)
                Require(
                    NativeFieldPolicy.SceneExclusion(
                        owner,
                        name,
                        "UnityEngine.Vector3",
                        "Blend.dll"
                    )
                        is not null
                        && NativeFieldPolicy.SceneExclusion(
                            "Other." + owner,
                            name,
                            "UnityEngine.Vector3",
                            "Blend.dll"
                        )
                            is null
                        && NativeFieldPolicy.SceneExclusion(
                            owner,
                            name,
                            "UnityEngine.Quaternion",
                            "Blend.dll"
                        )
                            is null
                        && NativeFieldPolicy.SceneExclusion(
                            owner,
                            name,
                            "UnityEngine.Vector3",
                            "Other.dll"
                        )
                            is null,
                    "A scene exclusion generalized beyond its exact stored field contract."
                );
        }
        using var image = new MemoryStream();
        module.Write(image);
        var assembly = Assembly.Load(image.ToArray());
        var createReader = typeof(Snapshot).GetMethod(
            "CreateFieldReader",
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        foreach (var (owner, fields) in sceneFields)
        {
            var type = assembly.GetType(owner)!;
            Require(
                type.Module.ScopeName == "Blend.dll",
                "Stored-field fixture lost its exact module guard."
            );
            foreach (var name in fields)
            {
                var property = type.GetProperty(name)!;
                Require(
                    NativeFieldPolicy.Exclusion(property)
                        == "render_scene_coordinates_not_gameplay_position",
                    "The actual PropertyInfo policy did not classify a named scene field."
                );
                var reader = createReader.Invoke(null, [property])!;
                Require(
                    reader.GetType().GetProperty("Get")!.GetValue(reader) is null,
                    "An excluded field retained an executable native getter."
                );
            }
            Require(
                NativeFieldPolicy.Exclusion(type.GetProperty("future_gameplay_position")!) is null,
                "An unknown future Vector3 gameplay field was silently excluded."
            );
        }
    }

    private static void ResourceReceiversKeepArgumentsWithoutReadingOwnerState()
    {
        var boundaries = new[]
        {
            ("UserDataManager.SetResources", 0x2A85190L),
            ("UserDataManager.UpdateResources", 0x2A82230L),
            ("UserDataManager.DeleteResourceEntities", 0x2A85070L),
        };
        foreach (var (boundary, rva) in boundaries)
        {
            Require(
                ResourceReceiverProjection.Applies(boundary, rva)
                    && !ResourceReceiverProjection.Applies(boundary, rva + 1)
                    && !ResourceReceiverProjection.Applies(boundary + "X", rva)
                    && Snapshot.BindOwnerLineage(new QueryModel(1), boundary, rva).Length == 0,
                "Resource receiver selection widened or inspected its owner lineage."
            );
            var missing = new Snapshot();
            missing.ReadReceiver(null, boundary, rva);
            Require(
                !missing.Complete && missing.Failures.Single().Field == "$receiver",
                "A null resource receiver was silently accepted."
            );
            var wrong = new Snapshot();
            wrong.ReadReceiver(17, boundary, rva);
            Require(
                !wrong.Complete && wrong.Failures.Count == 1,
                "A CLR scalar was accepted as a resource owner."
            );
        }
        ResourceReceiverProjection.ValidateOwner("UserDataManager", "Blend.dll", 1, 2, 2);
        foreach (
            var invalid in new[]
            {
                ("Other.UserDataManager", "Blend.dll", (nint)1, (nint)2, (nint)2),
                ("UserDataManager", "Other.dll", (nint)1, (nint)2, (nint)2),
                ("UserDataManager", "Blend.dll", (nint)0, (nint)2, (nint)2),
                ("UserDataManager", "Blend.dll", (nint)1, (nint)0, (nint)0),
                ("UserDataManager", "Blend.dll", (nint)1, (nint)2, (nint)3),
            }
        )
        {
            try
            {
                ResourceReceiverProjection.ValidateOwner(
                    invalid.Item1,
                    invalid.Item2,
                    invalid.Item3,
                    invalid.Item4,
                    invalid.Item5
                );
                throw new InvalidOperationException("An invalid resource owner was accepted.");
            }
            catch (InvalidDataException) { }
        }
        try
        {
            ResourceReceiverProjection.Reference(0);
            throw new InvalidOperationException("An unavailable resource lifetime was accepted.");
        }
        catch (InvalidDataException) { }
        var receiver = ResourceReceiverProjection.Reference(7);
        var later = ResourceReceiverProjection.Reference(11);
        Require(
            receiver.lifetime_id != later.lifetime_id
                && receiver.state_contract == ResourceReceiverProjection.StateContract,
            "Resource owner identity was reused or labelled as observed mutable state."
        );
        var full = new QueryModel(7);
        full.Fields["mutable_required_argument_field"] = 29;
        full.Fields["null_required_argument_field"] = null;
        var message = new byte[65_539];
        message[0] = 0x08;
        message[^3] = 0xF8;
        message[^2] = 0x07;
        message[^1] = 0x01;
        var argument = new Metadata("complete.resource.argument", null, null);
        var ownerReads = 0;
        var graph = new DetachedSnapshot(value =>
        {
            if (ReferenceEquals(value, argument))
                return new("complete.resource.argument", value, [], Protobuf: () => message);
            var owner = (QueryModel)value;
            ownerReads++;
            return new(
                "UserDataManager",
                owner,
                owner
                    .Fields.Select(pair => new SnapshotMember(pair.Key, () => pair.Value))
                    .ToArray()
            );
        });
        var copiedReceiver = graph.Read(receiver);
        Require(
            ownerReads == 0 && graph.Nodes.Count == 0,
            "Resource receiver identity traversed or occupied the full argument graph."
        );
        var aliasArgument = graph.Read(full);
        var copiedArgument = graph.Read(argument);
        var nullArgument = graph.Read(null);
        Require(
            graph.Complete
                && graph.Nodes.Count == 2
                && ownerReads == 1
                && Equals(graph.Nodes[0].fields["mutable_required_argument_field"], 29)
                && graph.Nodes[0].fields["null_required_argument_field"] is null
                && graph.Nodes[1].protobuf!.SequenceEqual(message),
            "A receiver alias, full raw argument or required null was pruned with receiver state."
        );
        using var output = new MemoryStream();
        CaptureJson.WriteLine(
            output,
            new
            {
                instance = copiedReceiver,
                instance_contract = ResourceReceiverProjection.StateContract,
                projection_contract = ResourceReceiverProjection.Contract,
                values = new[] { aliasArgument, copiedArgument, nullArgument },
                result = (object?)null,
                snapshot_schema = 2,
                snapshot_contracts = graph.Contracts,
                nodes = graph.Nodes,
                failures = graph.Failures,
            }
        );
        using var serialized = JsonDocument.Parse(output.ToArray());
        var record = serialized.RootElement;
        Require(
            record.GetProperty("instance").GetProperty("state_contract").GetString()
                == ResourceReceiverProjection.StateContract
                && !record.GetProperty("instance").TryGetProperty("reference", out _)
                && record.GetProperty("values")[0].GetProperty("reference").GetInt32() == 1
                && record.GetProperty("values")[2].ValueKind == JsonValueKind.Null
                && record
                    .GetProperty("nodes")[1]
                    .GetProperty("protobuf")
                    .GetBytesFromBase64()
                    .SequenceEqual(message),
            "Persisted resource identity confused full argument references, bytes or null semantics."
        );
    }

    private static void LocalContractsReconstructExactMetadataAndValues()
    {
        var firstExcluded = new[] { "render_parent:excluded" };
        var firstUnavailable = new[] { "required_unknown:unqualified" };
        var values = new[]
        {
            new Metadata("same.type", firstExcluded, firstUnavailable),
            new Metadata("same.type", ["render_parent:excluded"], ["required_unknown:unqualified"]),
            new Metadata("same.type", ["other:excluded"], ["required_unknown:unqualified"]),
            new Metadata("same.type", null, null),
            new Metadata("same.type", [], []),
            new Metadata("other.type", null, null),
        };
        var graph = MetadataGraph();
        var root = graph.Read(values);
        var expected = Expanded(graph);
        firstExcluded[0] = "changed_after_copy";
        firstUnavailable[0] = "changed_after_copy";
        using var compactBytes = new MemoryStream();
        CaptureJson.WriteLine(
            compactBytes,
            new
            {
                root,
                snapshot_schema = 2,
                snapshot_contracts = graph.Contracts,
                nodes = graph.Nodes,
                failures = graph.Failures,
            }
        );
        var compact = JsonNode.Parse(compactBytes.ToArray())!.AsObject();
        var reconstructed = Reconstruct(compact);
        Require(
            JsonNode.DeepEquals(expected, reconstructed),
            "Local contracts changed a value, failure, protobuf, null/empty metadata list or type while reconstructing."
        );
        Require(
            graph.Contracts.Count == 6 && graph.Nodes[1].contract_id == graph.Nodes[2].contract_id,
            "Equal contract lists were repeated or different type/metadata variants were conflated."
        );
        Require(
            !graph.Complete && graph.Failures.Count == values.Length,
            "Contract normalization hid required unavailable fields or dynamic reader failures."
        );
        Require(
            compact["nodes"]!
                .AsArray()
                .All(node =>
                    !node!.AsObject().ContainsKey("type")
                    && !node.AsObject().ContainsKey("excluded_fields")
                    && !node.AsObject().ContainsKey("unavailable_fields")
                ),
            "Per-node static metadata remained repeated in the versioned compact format."
        );
        var missing = compact.DeepClone().AsObject();
        missing["nodes"]![0]!["contract_id"] = 99_999;
        try
        {
            Reconstruct(missing);
            throw new InvalidDataException("An unresolved local contract silently reconstructed.");
        }
        catch (KeyNotFoundException) { }
    }

    private static DetachedSnapshot MetadataGraph() =>
        new(value =>
        {
            if (value is Metadata[] rows)
                return new("metadata.items", value, [], Items: () => rows.Cast<object?>());
            var model = (Metadata)value;
            return new(
                model.Type,
                value,
                [
                    new("zero", () => 0),
                    new("null", () => null),
                    new("flag", () => false),
                    new("nan", () => BitConverter.UInt32BitsToSingle(0x7FC00029)),
                    new("wide", () => ulong.MaxValue),
                    new(
                        "failed",
                        () =>
                            throw new InvalidDataException(
                                "unclipped-read-failure",
                                new ArgumentException("inner-detail")
                            )
                    ),
                ],
                Items: () => new object?[] { 0, null, "semantic text" },
                Protobuf: () => new byte[] { 8, 0, 0xF8, 0x7F, 1 },
                Excluded: model.Excluded,
                Unavailable: model.Unavailable,
                LifetimeId: "17"
            );
        });

    private static JsonArray Expanded(DetachedSnapshot graph)
    {
        var serialized = JsonSerializer.SerializeToNode(graph.Nodes)!.AsArray();
        for (var index = 0; index < serialized.Count; index++)
        {
            var node = serialized[index]!.AsObject();
            RestoreOptionalNulls(node);
            var original = graph.Nodes[index];
            node.Remove("contract_id");
            node["type"] = original.type;
            node["excluded_fields"] = JsonSerializer.SerializeToNode(original.excluded_fields);
            node["unavailable_fields"] = JsonSerializer.SerializeToNode(
                original.unavailable_fields
            );
        }
        return serialized;
    }

    private static JsonArray Reconstruct(JsonObject compact)
    {
        var contracts = compact["snapshot_contracts"]!
            .AsArray()
            .ToDictionary(row => row!["id"]!.GetValue<int>());
        var result = compact["nodes"]!.DeepClone().AsArray();
        foreach (var item in result)
        {
            var node = item!.AsObject();
            RestoreOptionalNulls(node);
            var contract = contracts[node["contract_id"]!.GetValue<int>()]!;
            node.Remove("contract_id");
            node["type"] = contract["type"]!.DeepClone();
            node["excluded_fields"] = contract["excluded_fields"]?.DeepClone();
            node["unavailable_fields"] = contract["unavailable_fields"]?.DeepClone();
        }
        return result;
    }

    private static void RestoreOptionalNulls(JsonObject node)
    {
        foreach (
            var field in new[]
            {
                "items",
                "protobuf",
                "protobuf_sha256",
                "representation",
                "lifetime_id",
                "lineage_failures",
            }
        )
            if (!node.ContainsKey(field))
                node[field] = null;
    }

    private static void OptionalNullAnnotationsPreserveRequiredNullsAndEmptyValues()
    {
        var graph = new DetachedSnapshot(value =>
            new("optional", value, [new("required_absent", () => null)])
        );
        graph.Read(new object());
        using var output = new MemoryStream();
        CaptureJson.WriteLine(
            output,
            new
            {
                snapshot_schema = 2,
                snapshot_contracts = graph.Contracts,
                nodes = graph.Nodes,
            }
        );
        using var json = JsonDocument.Parse(output.ToArray());
        var node = json.RootElement.GetProperty("nodes")[0];
        Require(
            node.GetProperty("fields").GetProperty("required_absent").ValueKind
                == JsonValueKind.Null
                && node.EnumerateObject()
                    .All(property => property.Name is "id" or "contract_id" or "fields"),
            "Optional annotation omission removed a required field null or used a global null policy."
        );
        var present = new DetachedSnapshot(value =>
            new(
                "optional",
                value,
                [new("required_absent", () => null)],
                Items: () => [],
                Protobuf: () => [],
                LifetimeId: "0",
                Lineage: () => []
            )
        );
        present.Read(new object());
        using var nonempty = new MemoryStream();
        CaptureJson.WriteLine(
            nonempty,
            new
            {
                snapshot_schema = 2,
                snapshot_contracts = present.Contracts,
                nodes = present.Nodes,
            }
        );
        using var document = JsonDocument.Parse(nonempty.ToArray());
        var retained = document.RootElement.GetProperty("nodes")[0];
        Require(
            retained.GetProperty("items").GetArrayLength() == 0
                && retained.GetProperty("lineage_failures").GetArrayLength() == 0
                && retained.GetProperty("protobuf").GetString() == ""
                && retained.GetProperty("lifetime_id").GetString() == "0"
                && retained.GetProperty("representation").GetString() == "native_reserialized"
                && retained.GetProperty("protobuf_sha256").GetString()
                    == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])),
            "Optional omission conflated empty/present-zero bytes, collection, lineage or lifetime with null."
        );
        Require(
            JsonNode.DeepEquals(
                Expanded(graph),
                Reconstruct(JsonNode.Parse(output.ToArray())!.AsObject())
            ),
            "Omitted annotations could not reconstruct the historical explicit-null shape."
        );
    }

    private static void EncryptedDateStorageKeepsRawFields()
    {
        var storage = ValueType(
            "_67f24",
            [("e", typeof(long)), ("s", typeof(long)), ("c", typeof(int))]
        );
        var wrapper = ValueType("_ee847", [("m_value", storage)]);
        var bits = Value(storage, ("e", long.MinValue), ("s", long.MaxValue), ("c", int.MinValue));
        var outer = Value(wrapper, ("m_value", bits));
        var graph = new Snapshot();
        graph.Read(outer);
        graph.Read(outer);
        Require(
            graph.Complete
                && graph.Nodes.Count == 4
                && graph.Nodes.All(node => node.lifetime_id is null),
            "Encrypted date storage was coalesced or mistaken for a native pointer."
        );
        foreach (var node in graph.Nodes.Where(node => node.type == "_67f24"))
            Require(
                Equals(node.fields["e"], "-9223372036854775808")
                    && Equals(node.fields["s"], "9223372036854775807")
                    && Equals(node.fields["c"], int.MinValue),
                "Encrypted date raw storage was decoded, rounded or omitted."
            );
        storage.GetField("e")!.SetValue(bits, 9007199254741007L);
        wrapper.GetField("m_value")!.SetValue(outer, bits);
        var after = new Snapshot();
        after.Read(outer);
        Require(
            Equals(
                after.Nodes.Single(node => node.type == "_67f24").fields["e"],
                "9007199254741007"
            )
                && Equals(
                    graph.Nodes.First(node => node.type == "_67f24").fields["e"],
                    "-9223372036854775808"
                ),
            "Encrypted date reused stale fields or changed a prior observation."
        );
        var invalid = new Snapshot();
        invalid.Read(
            Value(
                ValueType("_67f24", [("e", typeof(long)), ("s", typeof(long)), ("c", typeof(long))])
            )
        );
        Require(
            !invalid.Complete && invalid.Failures.Count == 1,
            "Encrypted raw layout mismatch was silently accepted."
        );
    }

    private static void ClientStatusReadSetKeepsInputsFailuresAndFullRootUpgrade()
    {
        Require(
            ClientStatusProjection.Applies(ClientStatusProjection.Boundary, 0x4192D30)
                && !ClientStatusProjection.Applies(ClientStatusProjection.Boundary, 0x4192D31)
                && !ClientStatusProjection.Applies("BattleStateVariables.OtherQuery", 0x4192D30),
            "A read-set optimization activated on an unproved native method."
        );
        var actions = new QueryModel(2);
        actions.Fields["_size"] = 3;
        var setups = new QueryModel(3);
        setups.Fields["_size"] = 0;
        var history = new QueryModel(4);
        history.Fields["_NMGKEONKAHG_k__BackingField"] = 29;
        var owner = new QueryModel(1);
        owner.Fields["m_actionInfoList"] = actions;
        owner.Fields["m_actionSetupInfoList"] = setups;
        owner.Fields["_MHHGMPCBOKI_k__BackingField"] = false;
        owner.Fields["_ENPKCFFLCFM_k__BackingField"] = history;
        owner.Fields["required_other_state"] = 31;
        object? Read(object model, string name)
        {
            if (!((QueryModel)model).Fields.TryGetValue(name, out var value))
                throw new MissingMemberException("required_query_field:" + name);
            if (value is Exception error)
                throw error;
            return value;
        }
        DetachedSnapshot Copy() =>
            new(
                value =>
                    value is QueryView view
                        ? ClientStatusProjection.Source(
                            (view.Owner, "query"),
                            "BattleStateVariables",
                            "1",
                            name => Read(view.Owner, name),
                            Read,
                            model =>
                                NativeProjection.Reference(
                                    "query_child",
                                    ((QueryModel)model).Lifetime
                                ),
                            ["required_other_state:outside_qualified_client_status_read_set"]
                        )
                        : new(
                            "full.owner",
                            value,
                            ((QueryModel)value)
                                .Fields.Select(pair => new SnapshotMember(
                                    pair.Key,
                                    () => pair.Value
                                ))
                                .ToArray()
                        ),
                value => value is QueryView query ? (query.Owner, "query") : value
            );
        var before = Copy();
        before.Read(new QueryView(owner));
        before.Read(owner);
        before.Read(7);
        before.Read(false);
        before.Read(29);
        Require(
            before.Complete
                && before.Nodes[0].fields.Count == 7
                && Equals(before.Nodes[0].fields["m_actionInfoList._size"], 3)
                && Equals(before.Nodes[0].fields["m_actionSetupInfoList._size"], 0)
                && Equals(
                    before.Nodes[0].fields[
                        "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                    ],
                    29
                )
                && Equals(before.Nodes[1].fields["required_other_state"], 31),
            "Query projection lost raw inputs or prevented a full actual argument/root upgrade."
        );
        actions.Fields["_size"] = 7;
        history.Fields["_NMGKEONKAHG_k__BackingField"] = 41;
        var after = Copy();
        after.Read(new QueryView(owner));
        Require(
            Equals(after.Nodes[0].fields["m_actionInfoList._size"], 7)
                && Equals(
                    after.Nodes[0].fields[
                        "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                    ],
                    41
                )
                && Equals(before.Nodes[0].fields["m_actionInfoList._size"], 3),
            "Read-set counts/status were cached or rewritten."
        );
        owner.Fields["m_actionInfoList"] = null;
        owner.Fields["m_actionSetupInfoList"] = null;
        owner.Fields["_ENPKCFFLCFM_k__BackingField"] = null;
        var absent = Copy();
        absent.Read(new QueryView(owner));
        Require(
            absent.Complete
                && absent.Nodes[0].fields["m_actionInfoList._size"] is null
                && absent.Nodes[0].fields["m_actionSetupInfoList._size"] is null
                && absent.Nodes[0].fields[
                    "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                ]
                    is null,
            "A native-null query input was fabricated as empty/zero status."
        );
        owner.Fields["m_actionInfoList"] = actions;
        actions.Fields["_size"] = new InvalidDataException(
            "query-field-failure",
            new ArgumentException("inner")
        );
        var failed = Copy();
        failed.Read(new QueryView(owner));
        Require(
            !failed.Complete
                && failed.Failures.Single().Field == "m_actionInfoList._size"
                && failed
                    .Failures[0]
                    .Details.Contains("query-field-failure", StringComparison.Ordinal)
                && failed.Nodes[0].fields.ContainsKey("_MHHGMPCBOKI_k__BackingField"),
            "Required query failure was suppressed or removed other available input fields."
        );
    }

    private static void RenderExclusionsStayOnExactOwnerFields()
    {
        foreach (
            var pair in new[]
            {
                ("BattleUnitTimelineControl", "m_cameraControl"),
                ("GCNOLOEMKPL", "DPCHGOBNFIA"),
                ("GCNOLOEMKPL", "NCJHHMPPGJJ"),
                ("NHEIMMIAMJO", "_JNNBMJMOMFF_k__BackingField"),
                ("LIAHKJACBDJ", "NOLKEODGADL"),
                ("BattlePresenter", "BDDOIEKEGKD"),
                ("BattlePresenter", "m_playerStatusList"),
                ("LANAGMCAFDC", "BIMAAMLCJPC"),
            }
        )
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new("RenderField" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.RunAndCollect
            );
            var type = assembly
                .DefineDynamicModule("fields")
                .DefineType(pair.Item1, TypeAttributes.Public);
            foreach (var name in new[] { pair.Item2, "required_gameplay_field" })
            {
                var property = type.DefineProperty(
                    name,
                    PropertyAttributes.None,
                    typeof(object),
                    null
                );
                var getter = type.DefineMethod(
                    "get_" + name,
                    MethodAttributes.Public
                        | MethodAttributes.SpecialName
                        | MethodAttributes.HideBySig,
                    typeof(object),
                    Type.EmptyTypes
                );
                var il = getter.GetILGenerator();
                il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ret);
                property.SetGetMethod(getter);
            }
            var built = type.CreateType()!;
            Require(
                NativeFieldPolicy.Exclusion(built.GetProperty(pair.Item2)!) is not null
                    && NativeFieldPolicy.Exclusion(built.GetProperty("required_gameplay_field")!)
                        is null,
                "An exact render exclusion generalized to required owner gameplay fields."
            );
        }
    }

    private static void ClientStatusReferenceIsCopiedOnceAndItsFailureStaysExplicit()
    {
        var first = new QueryModel(17);
        first.Fields["_size"] = 3;
        var replacement = new QueryModel(19);
        replacement.Fields["_size"] = 7;
        var firstHistory = new QueryModel(23);
        firstHistory.Fields["_NMGKEONKAHG_k__BackingField"] = 29;
        var nextHistory = new QueryModel(31);
        nextHistory.Fields["_NMGKEONKAHG_k__BackingField"] = 41;
        var calls = new Dictionary<string, int>();
        var current = 0;
        object? ReadOwner(string name)
        {
            var count = calls.GetValueOrDefault(name) + 1;
            calls[name] = count;
            if (name == "_MHHGMPCBOKI_k__BackingField")
                return false;
            if (name == "_ENPKCFFLCFM_k__BackingField")
                return count == 1 && current == 0 ? firstHistory : nextHistory;
            return count == 1 && current == 0 ? first : replacement;
        }
        DetachedSnapshot Copy(Func<string, object?> readOwner) =>
            new(value =>
                ClientStatusProjection.Source(
                    value,
                    "BattleStateVariables",
                    "1",
                    readOwner,
                    (child, name) => ((QueryModel)child).Fields[name],
                    child => NativeProjection.Reference("child", ((QueryModel)child).Lifetime),
                    []
                )
            );
        var before = Copy(ReadOwner);
        before.Read(new object());
        Require(
            before.Complete
                && calls.Values.All(count => count == 1)
                && ((NativeReference)before.Nodes[0].fields["m_actionInfoList"]!).lifetime_id
                    == "17"
                && Equals(before.Nodes[0].fields["m_actionInfoList._size"], 3)
                && ((NativeReference)before.Nodes[0].fields["m_actionSetupInfoList"]!).lifetime_id
                    == "17"
                && Equals(before.Nodes[0].fields["m_actionSetupInfoList._size"], 3)
                && (
                    (NativeReference)before.Nodes[0].fields["_ENPKCFFLCFM_k__BackingField"]!
                ).lifetime_id == "23"
                && Equals(
                    before.Nodes[0].fields[
                        "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                    ],
                    29
                ),
            "A query paired one owner's identity with a replacement owner's count/status."
        );
        current = 1;
        calls.Clear();
        var after = Copy(ReadOwner);
        after.Read(new object());
        Require(
            after.Complete
                && calls.Values.All(count => count == 1)
                && ((NativeReference)after.Nodes[0].fields["m_actionInfoList"]!).lifetime_id == "19"
                && Equals(after.Nodes[0].fields["m_actionInfoList._size"], 7)
                && Equals(
                    after.Nodes[0].fields[
                        "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                    ],
                    41
                )
                && Equals(before.Nodes[0].fields["m_actionInfoList._size"], 3),
            "A local reference copy leaked cached native values across observations."
        );
        var failure = new InvalidDataException(
            "failed-owner-reference",
            new ArgumentException("inner-owner-detail")
        );
        var failedReads = 0;
        var failed = Copy(name =>
        {
            if (name != "m_actionInfoList")
                return ReadOwner(name);
            failedReads++;
            throw failure;
        });
        failed.Read(new object());
        Require(
            !failed.Complete
                && failedReads == 1
                && failed.Failures.Count == 2
                && failed.Failures.All(error =>
                    error.Details.Contains("failed-owner-reference", StringComparison.Ordinal)
                    && error.Details.Contains("inner-owner-detail", StringComparison.Ordinal)
                )
                && failed
                    .Failures.Select(error => error.Field)
                    .SequenceEqual(new[] { "m_actionInfoList", "m_actionInfoList._size" })
                && Equals(failed.Nodes[0].fields["_MHHGMPCBOKI_k__BackingField"], false)
                && Equals(
                    failed.Nodes[0].fields[
                        "_ENPKCFFLCFM_k__BackingField._NMGKEONKAHG_k__BackingField"
                    ],
                    41
                ),
            "A cached query owner-reference fault was reread, lost full evidence, or prevented other fields."
        );
    }

    private sealed class QueryModel(long lifetime)
    {
        internal long Lifetime { get; } = lifetime;
        internal Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
    }

    private sealed record QueryView(QueryModel Owner);

    private static void StoredValuesKeepEveryBitAndFreshIdentity()
    {
        var vector = ValueType(
            "UnityEngine.Vector3",
            [("x", typeof(float)), ("y", typeof(float)), ("z", typeof(float))]
        );
        var quaternion = ValueType(
            "UnityEngine.Quaternion",
            [("x", typeof(float)), ("y", typeof(float)), ("z", typeof(float)), ("w", typeof(float))]
        );
        var date = ValueType("Il2CppSystem.DateTime", [("_dateData", typeof(ulong))]);
        var negativeZero = Value(vector, ("x", -0.0f), ("y", 1.0f), ("z", 2.0f));
        var positiveZero = Value(vector, ("x", 0.0f), ("y", 1.0f), ("z", 2.0f));
        Require(
            negativeZero.Equals(positiveZero),
            "Signed-zero fixture did not exercise value equality."
        );
        var graph = new Snapshot();
        graph.Read(negativeZero);
        graph.Read(positiveZero);
        graph.Read(negativeZero);
        graph.Read(Value(vector, ("x", BitConverter.UInt32BitsToSingle(0x7FC00029))));
        graph.Read(Value(vector, ("x", BitConverter.UInt32BitsToSingle(0x7FC00031))));
        graph.Read(
            Value(
                quaternion,
                ("x", -0.0f),
                ("y", BitConverter.UInt32BitsToSingle(0x7FC00047)),
                ("z", 3.0f),
                ("w", 0.0f)
            )
        );
        graph.Read(Value(date, ("_dateData", 41UL)));
        graph.Read(Value(date, ("_dateData", 0xC000000000000029UL)));
        Require(
            graph.Complete
                && graph.Nodes.Count == 8
                && graph.Contracts.Count == 3
                && graph.Nodes.All(node => node.lifetime_id is null),
            "CLR value reads coalesced boxes, inferred native lifetimes or became incomplete."
        );
        var expectedBits = new[]
        {
            "80000000",
            "00000000",
            "80000000",
            "7FC00029",
            "7FC00031",
            "80000000",
        };
        for (var index = 0; index < expectedBits.Length; index++)
            Require(
                Bits(graph.Nodes[index], "x") == expectedBits[index],
                "Stored vector/quaternion component bits changed."
            );
        Require(
            Bits(graph.Nodes[5], "y") == "7FC00047"
                && Bits(graph.Nodes[5], "w") == "00000000"
                && Equals(graph.Nodes[6].fields["_dateData"], "41")
                && Equals(graph.Nodes[7].fields["_dateData"], "13835058055282163753"),
            "Quaternion components or DateTime raw kind/DST bits were lost."
        );
        vector.GetField("x")!.SetValue(negativeZero, 7.0f);
        var after = new Snapshot();
        after.Read(negativeZero);
        Require(
            Bits(after.Nodes.Single(), "x") == "40E00000"
                && Bits(graph.Nodes[0], "x") == "80000000",
            "Cached component readers reused native values or mutated an earlier detached observation."
        );
        foreach (
            var bad in new[]
            {
                Value(
                    ValueType(
                        "UnityEngine.Vector3",
                        [
                            ("x", typeof(float)),
                            ("y", typeof(float)),
                            ("z", typeof(float)),
                            ("extra", typeof(int)),
                        ]
                    )
                ),
                Value(
                    ValueType(
                        "UnityEngine.Quaternion",
                        [
                            ("x", typeof(double)),
                            ("y", typeof(float)),
                            ("z", typeof(float)),
                            ("w", typeof(float)),
                        ]
                    )
                ),
                Value(ValueType("unapproved.value", [("x", typeof(float))])),
            }
        )
        {
            var unavailable = new Snapshot();
            unavailable.Read(bad);
            unavailable.Read(29);
            Require(
                !unavailable.Complete
                    && unavailable.Failures.Count == 1
                    && unavailable.Nodes.Count == 0,
                "Unknown/mismatched CLR value layout was silently accepted."
            );
        }
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 5_000; index++)
        {
            vector.GetField("x")!.SetValue(negativeZero, (float)index);
            var sample = new Snapshot();
            sample.Read(negativeZero);
            Require(
                Bits(sample.Nodes.Single(), "x")
                    == BitConverter
                        .SingleToUInt32Bits(index)
                        .ToString("X8", CultureInfo.InvariantCulture),
                "Reused cached value readers returned stale components."
            );
        }
        Console.WriteLine(
            "stored_values_synthetic="
                + JsonSerializer.Serialize(
                    new { observations = 5_000, elapsed_ms = elapsed.Elapsed.TotalMilliseconds }
                )
        );
    }

    private static Type ValueType(string name, (string Name, Type Type)[] fields)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new("SnapshotValueFixture" + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.RunAndCollect
        );
        var builder = assembly
            .DefineDynamicModule("values")
            .DefineType(
                name,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout,
                typeof(ValueType)
            );
        foreach (var field in fields)
            builder.DefineField(field.Name, field.Type, FieldAttributes.Public);
        return builder.CreateType()!;
    }

    private static object Value(Type type, params (string Name, object Value)[] fields)
    {
        var value = Activator.CreateInstance(type)!;
        foreach (var field in fields)
            type.GetField(field.Name)!.SetValue(value, field.Value);
        return value;
    }

    private static string? Bits(SnapshotNode node, string field)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(node.fields[field])
        );
        return document.RootElement.GetProperty("bits").GetString();
    }

    private static void TimelineMembershipKeepsOrderAndFullArgumentUpgrade()
    {
        Require(
            NativeProjection.IdentityCollection("NACHLMPIJDK", "_OEPPMNOAGEB_k__BackingField")
                && NativeProjection.IdentityCollection(
                    "NACHLMPIJDK",
                    "_JPECKMLDDDN_k__BackingField"
                )
                && !NativeProjection.IdentityCollection(
                    "other.owner",
                    "_OEPPMNOAGEB_k__BackingField"
                )
                && !NativeProjection.IdentityCollection("NACHLMPIJDK", "other_required_list"),
            "Timeline identity membership was generalized beyond its two qualified fields."
        );
        var first = new Actor(17, 10);
        var other = new Actor(19, 20);
        var timeline = new Membership { Values = [first, null, other, first] };
        DetachedSnapshot Copy() =>
            new(value =>
                value switch
                {
                    Membership membership => new(
                        "NACHLMPIJDK.ordered_actor_membership",
                        value,
                        [],
                        Items: () =>
                            NativeProjection.ReferenceItems(
                                membership.Values,
                                item =>
                                    NativeProjection.Reference(
                                        "NHEIMMIAMJO",
                                        ((Actor)item).Lifetime
                                    )
                            ),
                        Excluded: ["collection_item_state:native_lifetime_identity_only"]
                    ),
                    Actor actor => new(
                        "NHEIMMIAMJO",
                        value,
                        [new("required_hp", () => actor.Hp)],
                        LifetimeId: actor.Lifetime.ToString(CultureInfo.InvariantCulture)
                    ),
                    _ => throw new InvalidDataException(),
                }
            );
        var before = Copy();
        before.Read(timeline);
        before.Read(other);
        var references = before.Nodes[0].items!;
        Require(
            before.Complete
                && before.Nodes.Count == 2
                && references.Count == 4
                && references[1] is null
                && ((NativeReference)references[0]!).lifetime_id == "17"
                && ((NativeReference)references[2]!).lifetime_id == "19"
                && ((NativeReference)references[3]!).lifetime_id == "17"
                && Equals(before.Nodes[1].fields["required_hp"], 20),
            "Timeline membership lost order/null/duplicates or prevented a later required argument from a full copy."
        );
        other.Hp = 7;
        timeline.Values = [other, first, null];
        var after = Copy();
        after.Read(timeline);
        after.Read(other);
        Require(
            after.Complete
                && after.Nodes[0].items!.Count == 3
                && ((NativeReference)after.Nodes[0].items![0]!).lifetime_id == "19"
                && Equals(after.Nodes[1].fields["required_hp"], 7)
                && Equals(before.Nodes[1].fields["required_hp"], 20),
            "A timeline/actor mutation rewrote detached evidence or reused an earlier owner's state."
        );
        timeline.Values = Enumerable
            .Range(0, 10_000)
            .Select(index => index % 2 == 0 ? (object?)first : null)
            .ToArray();
        var large = Copy();
        large.Read(timeline);
        Require(
            large.Nodes[0].items!.Count == 10_000,
            "Ordered timeline memberships were arbitrarily limited."
        );
        static IEnumerable<object?> Broken(Actor actor)
        {
            yield return actor;
            yield return null;
            throw new InvalidDataException("membership-read-failure");
        }
        timeline.Values = Broken(first);
        var failed = Copy();
        failed.Read(timeline);
        Require(
            !failed.Complete
                && failed.Nodes[0].items!.Count == 2
                && failed.Failures.Single().Field == "items[2]",
            "A failed membership read lost its available prefix or became complete."
        );
    }

    private static void ContractEncodingWorkload()
    {
        var unavailable = Enumerable
            .Range(0, 40)
            .Select(index =>
                "required_computed_field_"
                + index
                + ":computed_native_value_has_no_proven_projection"
            )
            .ToArray();
        var excluded = new[] { "scene_parent:render_scene_object_reference" };
        var graph = new DetachedSnapshot(value =>
        {
            if (value is Metadata[] models)
                return new("models", value, [], Items: () => models.Cast<object?>());
            var model = (Metadata)value;
            return new(
                model.Type,
                model,
                [new("fresh", () => model.Value)],
                Excluded: model.Excluded,
                Unavailable: model.Unavailable
            );
        });
        var models = Enumerable
            .Range(0, 2_481)
            .Select(index => new Metadata("type." + index % 24, excluded, unavailable)
            {
                Value = index,
            })
            .ToArray();
        var timer = Stopwatch.StartNew();
        var root = graph.Read(models);
        var copyMs = timer.Elapsed.TotalMilliseconds;
        var legacy = Expanded(graph);
        using var expanded = new MemoryStream();
        timer.Restart();
        CaptureJson.WriteLine(expanded, new { root, nodes = legacy });
        var expandedMs = timer.Elapsed.TotalMilliseconds;
        using var compact = new MemoryStream();
        timer.Restart();
        CaptureJson.WriteLine(
            compact,
            new
            {
                root,
                snapshot_schema = 2,
                snapshot_contracts = graph.Contracts,
                nodes = graph.Nodes,
            }
        );
        var compactMs = timer.Elapsed.TotalMilliseconds;
        Require(
            JsonNode.DeepEquals(legacy, Reconstruct(JsonNode.Parse(compact.ToArray())!.AsObject()))
                && compact.Length < expanded.Length / 5
                && graph.Contracts.Count == 25,
            "Compact static contracts did not preserve exact data or retained per-node metadata amplification."
        );
        Console.WriteLine(
            "snapshot_contracts_synthetic="
                + JsonSerializer.Serialize(
                    new
                    {
                        nodes = graph.Nodes.Count,
                        contracts = graph.Contracts.Count,
                        expanded_bytes = expanded.Length,
                        compact_bytes = compact.Length,
                        copy_ms = copyMs,
                        expanded_encode_ms = expandedMs,
                        compact_encode_ms = compactMs,
                    }
                )
        );
    }

    private static void IdentityLookupDoesNotBypassDifferentSemanticViews()
    {
        var qualified = new IdentityView(17, "qualified.owner", true, 29);
        var additional = new IdentityView(17, "qualified.alternate", true, 31);
        var unavailable = new IdentityView(17, "unqualified.owner", false, 0);
        var reads = 0;
        var graph = new DetachedSnapshot(
            value =>
            {
                var view = (IdentityView)value;
                reads++;
                if (!view.Qualified)
                    throw new MissingMemberException(
                        "Different generated wrapper is not qualified."
                    );
                return new(view.Type, view.Lifetime, [new("required", () => view.Value)]);
            },
            value => (((IdentityView)value).Lifetime, ((IdentityView)value).Type)
        );
        graph.Read(qualified);
        graph.Read(qualified);
        graph.Read(additional);
        graph.Read(unavailable);
        Require(
            !graph.Complete
                && reads == 3
                && graph.Nodes.Count == 2
                && graph.Failures.Count == 1
                && Equals(graph.Nodes[0].fields["required"], 29)
                && Equals(graph.Nodes[1].fields["required"], 31),
            "Early identity reuse hid another semantic view or bypassed an unsupported wrapper failure."
        );
    }

    private sealed class Metadata(string type, string[]? excluded, string[]? unavailable)
    {
        internal string Type { get; } = type;
        internal string[]? Excluded { get; } = excluded;
        internal string[]? Unavailable { get; } = unavailable;
        internal int Value;
    }

    private sealed class Actor(long lifetime, int hp)
    {
        internal long Lifetime { get; } = lifetime;
        internal int Hp = hp;
    }

    private sealed class Membership
    {
        internal IEnumerable<object?> Values = [];
    }

    private sealed record IdentityView(long Lifetime, string Type, bool Qualified, int Value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using AtelierCapture.Storage;

namespace AtelierCapture.Checks;

internal static class NativeContractChecks
{
    internal static void Run()
    {
        FullGraphHasNoDepthOrItemCutoff();
        FieldFailureKeepsAvailableValues();
        ProtobufFailureKeepsOtherFields();
        CollectionFailureKeepsReadItems();
        ProtobufAndWideScalarsStayExact();
        ExactSignatureRejectsSimilarOverloads();
        EmbeddedBoundaryContractsAreExplicit();
        FreshNativePhasesKeepReceiverArgumentsAndFaults();
        ClosedNativeBridgeDemotesActiveCounts();
        FocusedOwnersPreserveMutableStateAndUpgradeReferences();
        GameplayProjectionKeepsUnknownDomainAndFreshConditions();
        GameplayProjectionPreservesAtelierInputsAndResourceArguments();
        ProtobufOnlySnapshotsBindChildrenAndPreserveFailures();
        ParsedLineageRevisitsChangedProofs();
        FocusedProjectionHandlesDeepConcurrentAndMissingData();
        FocusedProjectionWorkload();
        SnapshotChecks.Run();
        NativeConcreteChecks.Run();
    }

    private static DetachedSnapshot GameGraph(bool focused) =>
        new(value =>
        {
            var model = (GameModel)value;
            var members = new List<SnapshotMember>();
            var excluded = new List<string>();
            foreach (var pair in model.Fields)
            {
                var field = pair.Key;
                var childType =
                    (pair.Value as GameModel)?.Type ?? pair.Value?.GetType().FullName ?? "";
                var reason = focused
                    ? NativeFieldPolicy.SceneExclusion(model.Type, field, childType, model.Module)
                        ?? NativeProjection.Infrastructure(model.Type, field, childType)
                    : null;
                if (reason is not null)
                {
                    excluded.Add(field + ":" + reason);
                    continue;
                }
                members.Add(
                    new(
                        field,
                        () =>
                        {
                            var current = model.Fields[field];
                            if (current is Exception error)
                                throw error;
                            if (focused && current is GameModel child)
                            {
                                var contract = NativeProjection.ReferenceContract(
                                    model.Type,
                                    field,
                                    child.Type,
                                    model.Module
                                );
                                if (
                                    contract is not null
                                    || NativeProjection.ReferenceChild(
                                        model.Type,
                                        field,
                                        child.Type
                                    )
                                )
                                    return NativeProjection.Reference(
                                        child.Type,
                                        child.Lifetime,
                                        contract ?? "native_lifetime_identity_only"
                                    );
                            }
                            return current;
                        }
                    )
                );
            }
            return new(
                model.Type,
                model,
                members,
                model.Items is null ? null : () => model.Items,
                Protobuf: model.Protobuf is null ? null : () => model.Protobuf!,
                Excluded: excluded,
                Unavailable: model.Unavailable,
                LifetimeId: model.Lifetime.ToString(
                    System.Globalization.CultureInfo.InvariantCulture
                )
            );
        });

    private static void FocusedOwnersPreserveMutableStateAndUpgradeReferences()
    {
        var state = new GameModel("BattleStateVariables", 1);
        var unit = new GameModel("NHEIMMIAMJO", 2);
        var condition = new GameModel("BattleUnitConditionControl", 3);
        var conditions = new GameModel("condition_list", 4) { Items = [17, null, 0] };
        unit.Fields["CIHHEGKEOED"] = state;
        unit.Fields["DDHFCNJJNGM"] = condition;
        unit.Fields["_NHNGMNDFPGC_k__BackingField"] = 9007199254741007L;
        condition.Fields["m_selfUnit"] = unit;
        condition.Fields["m_battleStateVariables"] = state;
        condition.Fields["m_conditionList"] = conditions;
        condition.Fields["PJMGGCKGAIL"] = 7;
        state.Fields["_GFNMEDJIHEN_k__BackingField"] = 11;
        var before = GameGraph(true);
        before.Read(unit);
        Require(
            before.Complete && before.Nodes.Count == 3,
            "A local unit copy followed the global state or lost its condition owner."
        );
        var link = (NativeReference)before.Nodes[0].fields["CIHHEGKEOED"]!;
        Require(
            link.lifetime_id == "1"
                && link.type == "BattleStateVariables"
                && link.state_contract == "native_lifetime_identity_only",
            "A typed backlink was relabelled as captured mutable state."
        );
        before.Read(state);
        Require(
            before.Nodes.Count == 4
                && Equals(
                    before.Nodes.Single(n => n.type == state.Type).fields[
                        "_GFNMEDJIHEN_k__BackingField"
                    ],
                    11
                ),
            "A previous backlink prevented a later actual argument/root from receiving a full state snapshot."
        );
        unit.Fields["_NHNGMNDFPGC_k__BackingField"] = -19L;
        condition.Fields["PJMGGCKGAIL"] = 23;
        conditions.Items = [29, 31, null, 0];
        var after = GameGraph(true);
        after.Read(unit);
        Require(
            Equals(before.Nodes[0].fields["_NHNGMNDFPGC_k__BackingField"], "9007199254741007")
                && Equals(after.Nodes[0].fields["_NHNGMNDFPGC_k__BackingField"], "-19")
                && Equals(
                    before.Nodes.Single(n => n.type == condition.Type).fields["PJMGGCKGAIL"],
                    7
                )
                && Equals(
                    after.Nodes.Single(n => n.type == condition.Type).fields["PJMGGCKGAIL"],
                    23
                )
                && before
                    .Nodes.Single(n => n.type == conditions.Type)
                    .items!.SequenceEqual(new object?[] { 17, null, 0 })
                && after
                    .Nodes.Single(n => n.type == conditions.Type)
                    .items!.SequenceEqual(new object?[] { 29, 31, null, 0 }),
            "Pointer/lifetime-equal mutable objects reused stale entry data or changed an already detached phase."
        );
        var reused = NativeProjection.Reference("BattleStateVariables", 101);
        Require(
            reused.lifetime_id != link.lifetime_id,
            "A later native lifetime reused the former generation's reference."
        );
        Require(
            NativeProjection.ReferenceChild(
                "BattleServerControl",
                "m_battleStateVariables",
                "BattleStateVariables"
            ) == false
                && NativeProjection.ReferenceChild(
                    "NHEIMMIAMJO",
                    "CIHHEGKEOED",
                    "BattleStateVariables"
                )
                && !NativeProjection.ReferenceChild(
                    "NativeUnknownGameplay",
                    "required_child",
                    "NativeUnknownGameplayChild"
                ),
            "Empty-argument server mutations lost state, or an unclassified gameplay field became an identity-only omission."
        );
    }

    private static void GameplayProjectionKeepsUnknownDomainAndFreshConditions()
    {
        var state = new GameModel("BattleStateVariables", 1);
        var unit = new GameModel("NHEIMMIAMJO", 2);
        var conditions = new GameModel("BattleUnitConditionControl", 3);
        var stats = new GameModel("BHPJMGDLNNE", 4);
        var future = new GameModel("FutureGameplayModel", 5);
        var history = new GameModel("CGGKFDGOFGN", 6);
        var actions = new GameModel("Il2CppSystem.Collections.Generic.List`1<JCDJDPMCAPB>", 7)
        {
            Items = [17, null, 0, 29],
        };
        var setups = new GameModel("Il2CppSystem.Collections.Generic.List`1<HMIEBOKCGFE>", 8)
        {
            Items = [null, 31],
        };
        var timeline = new GameModel("BattleUnitTimelineControl", 9);
        timeline.Fields["fresh_local_input"] = 41;
        history.Fields["full_actual_input"] = 43;
        state.Fields["_ENPKCFFLCFM_k__BackingField"] = history;
        state.Fields["m_actionInfoList"] = actions;
        state.Fields["m_actionSetupInfoList"] = setups;
        state.Fields["m_allUnitList"] = unit;
        state.Fields["PDHCIEFKGCL"] = new GameModel("UnityEngine.Vector3", 10);
        var lifetime = 20L;
        foreach (
            var name in new[]
            {
                "_MEENINAKKFN_k__BackingField",
                "JEHKCCNGMJA",
                "CAIBMIMOPFI",
                "PELBKEMPEEL",
                "NDMGLBMJOKM",
                "Rewards",
                "_FOBMEFPEIPC_k__BackingField",
                "_OCIPBGHALAL_k__BackingField",
                "_FEJHNLFPJAN_k__BackingField",
                "_LPCCBJOOEBG_k__BackingField",
                "_MIOFPEMOJPA_k__BackingField",
            }
        )
        {
            state.Fields[name] = new GameModel("UnclassifiedRequiredModel", lifetime++)
            {
                Items = [19, null, 0, -23L],
            };
        }
        unit.Fields["EJIDBLHEBNI"] = timeline;
        unit.Fields["DDHFCNJJNGM"] = conditions;
        unit.Fields["_NKADNMPGNCM_k__BackingField"] = stats;
        unit.Fields["_JLENLDFCJKB_k__BackingField"] = stats;
        unit.Fields["OMJHJKLJGJM"] = future;
        unit.Fields["LFOANAGBMHA"] = new GameModel("RequiredConditionModels", lifetime++)
        {
            Items = [0, null, 47],
        };
        unit.Fields["unknown_future_object"] = future;
        unit.Fields["required_nullable"] = null;
        unit.Fields["required_default"] = 0;
        unit.Fields["NKCLGCOJLEI"] = new GameModel("UnityEngine.Vector3", lifetime++);
        conditions.Fields["PJMGGCKGAIL"] = 7;
        conditions.Fields["m_conditionList"] = new GameModel("ConditionInstances", lifetime++)
        {
            Items = [53, null, 0, 59],
        };
        stats.Fields["current_stat"] = 61;
        future.Fields["new_effect_input"] = 67;
        var before = GameGraph(true);
        before.Read(state);
        Require(before.Complete, "The selected domain projection lost a required stored field.");
        Require(
            before.Nodes.All(n => n.type != "UnityEngine.Vector3")
                && before.Nodes.All(n => n.type != history.Type && n.type != timeline.Type),
            "An exact scene exclusion or reference still expanded its unrelated graph."
        );
        var stateNode = before.Nodes.Single(n => n.type == state.Type);
        var unitNode = before.Nodes.Single(n => n.type == unit.Type);
        Require(
            ((NativeReference)stateNode.fields["_ENPKCFFLCFM_k__BackingField"]!).state_contract
                == "historical_state_not_recopied"
                && ((NativeReference)unitNode.fields["EJIDBLHEBNI"]!).state_contract
                    == "presentation_controller_state_not_observed"
                && unitNode.fields["required_nullable"] is null
                && Equals(unitNode.fields["required_default"], 0)
                && before
                    .Nodes.Single(n => n.type == future.Type)
                    .fields.ContainsKey("new_effect_input")
                && before.Nodes.Count(n => n.type == "UnclassifiedRequiredModel") == 11,
            "A reference claimed captured state, or an unknown gameplay input was silently removed."
        );
        before.Read(history);
        before.Read(actions);
        before.Read(setups);
        before.Read(timeline);
        Require(
            before.Nodes.Single(n => n.type == history.Type).fields.ContainsKey("full_actual_input")
                && before
                    .Nodes.Single(n => n.type == actions.Type)
                    .items!.SequenceEqual(actions.Items!)
                && before
                    .Nodes.Single(n => n.type == setups.Type)
                    .items!.SequenceEqual(setups.Items!)
                && before
                    .Nodes.Single(n => n.type == timeline.Type)
                    .fields.ContainsKey("fresh_local_input"),
            "A receiver backlink prevented a complete actual root/argument snapshot."
        );
        conditions.Fields["PJMGGCKGAIL"] = 71;
        stats.Fields["current_stat"] = 73;
        future.Fields["new_effect_input"] = 79;
        var after = GameGraph(true);
        after.Read(state);
        Require(
            Equals(before.Nodes.Single(n => n.type == conditions.Type).fields["PJMGGCKGAIL"], 7)
                && Equals(
                    after.Nodes.Single(n => n.type == conditions.Type).fields["PJMGGCKGAIL"],
                    71
                )
                && Equals(after.Nodes.Single(n => n.type == stats.Type).fields["current_stat"], 73)
                && Equals(
                    after.Nodes.Single(n => n.type == future.Type).fields["new_effect_input"],
                    79
                ),
            "Fresh condition, stat or unclassified object values were replaced by stale references."
        );
        future.Fields["unreadable_new_input"] = new InvalidDataException(
            "required-future-input",
            new MissingMemberException("inner-future-input")
        );
        var failed = GameGraph(true);
        failed.Read(state);
        Require(
            !failed.Complete
                && failed
                    .Failures.Single()
                    .Details.Contains("inner-future-input", StringComparison.Ordinal)
                && failed
                    .Nodes.Single(n => n.type == stats.Type)
                    .fields.ContainsKey("current_stat"),
            "An unknown required field failure lost its evidence or removed other domain values."
        );
    }

    private static void GameplayProjectionPreservesAtelierInputsAndResourceArguments()
    {
        var selection = new GameModel("NAKLKNNBDMC", 1);
        var source = new GameModel("JJPPPEBLGOK", 2);
        var result = new GameModel("OLJAEFPIMBE", 3);
        var research = new GameModel("MIPJCECFKPM", 4);
        var character = new GameModel("MMGMFDEMDLA", 5);
        var order = new GameModel("Il2CppSystem.Collections.Generic.List`1<System.Int32>", 6)
        {
            Items = [17, null, 0, 17, 29],
        };
        selection.Fields["JCFLKHGGACH"] = new GameModel("Blend.UserData.Recipe", 7);
        selection.Fields["NGKGLIBNPCM"] = order;
        selection.Fields["JKBFGCEOACA"] = order;
        selection.Fields["_DIFKGCIAHBK_k__BackingField"] = 2;
        selection.Fields["ODOJELOGOIG"] = 3;
        selection.Fields["GJHHNLBCJDM"] = 5;
        source.Fields["_AJCAELOJDNI_k__BackingField"] = 31;
        source.Fields["_FOPEEJGNMOK_k__BackingField"] = 37;
        source.Fields["_JCNLPCLKHEI_k__BackingField"] = null;
        source.Fields["ELIIPHNLBCO"] = character;
        source.Fields["ILLEMGGPHOI"] = character;
        result.Fields["DPOAOGJOHLM"] = order;
        result.Fields["_NHMJMFJOOJL_k__BackingField"] = 41;
        result.Fields["KMFOLJOJEHB"] = 43;
        research.Fields["_CIKMDCCMAHK_k__BackingField"] = null;
        research.Fields["_BFKNNONLCOF_k__BackingField"] = 47;
        character.Fields["_MGCCHBLDDED_k__BackingField"] = false;
        character.Fields["MEHFGOEBGFG"] = true;
        var nearHistory = new GameModel("CGGKFDGOFGN", 8);
        var nearController = new GameModel("BattleUnitTimelineControl", 9);
        nearHistory.Fields["required_atelier_input"] = 53;
        nearController.Fields["required_atelier_input"] = 59;
        selection.Fields["_ENPKCFFLCFM_k__BackingField"] = nearHistory;
        selection.Fields["EJIDBLHEBNI"] = nearController;
        selection.Fields["NKCLGCOJLEI"] = new GameModel("UnityEngine.Vector3", 10);
        var unknown = new GameModel("FutureAtelierGameplayModel", 11);
        unknown.Fields["new_required_value"] = 61;
        result.Fields["future_result"] = unknown;
        byte[] requestBytes = [0xF0, 0xFF, 0xFF, 0xFF, 0x0F, 0x01];
        byte[] responseBytes = [0xF0, 0xFF, 0xFF, 0xFF, 0x0F, 0x02];
        byte[] resourceBytes = [0xF0, 0xFF, 0xFF, 0xFF, 0x0F, 0x03];
        var request = new GameModel("Blend.ApiSchema.Api.SynthesisExecuteRequest", 12)
        {
            Protobuf = requestBytes,
        };
        var response = new GameModel("Blend.ApiSchema.Api.SynthesisExecuteResponse", 13)
        {
            Protobuf = responseBytes,
        };
        var resources = new GameModel("Blend.ApiSchema.Model.Resources", 14)
        {
            Protobuf = resourceBytes,
        };
        var roots = new[]
        {
            selection,
            source,
            result,
            research,
            character,
            request,
            response,
            resources,
        };
        var baseline = GameGraph(false);
        var before = GameGraph(true);
        foreach (var root in roots)
        {
            baseline.Read(root);
            before.Read(root);
        }
        Require(
            before.Complete
                && JsonSerializer.Serialize(new { baseline.Contracts, baseline.Nodes })
                    == JsonSerializer.Serialize(new { before.Contracts, before.Nodes })
                && before
                    .Nodes.Single(n => n.type == nearHistory.Type)
                    .fields.ContainsKey("required_atelier_input")
                && before
                    .Nodes.Single(n => n.type == nearController.Type)
                    .fields.ContainsKey("required_atelier_input")
                && before.Nodes.Single(n => n.type == source.Type).fields[
                    "_JCNLPCLKHEI_k__BackingField"
                ]
                    is null
                && before
                    .Nodes.Single(n => n.type == order.Type)
                    .items!.SequenceEqual(order.Items!),
            "The Battle-only policy changed Atelier models, near field names, nullable values, aliases or ordered duplicates."
        );
        source.Fields["_JCNLPCLKHEI_k__BackingField"] = 0;
        research.Fields["_BFKNNONLCOF_k__BackingField"] = 67;
        character.Fields["_MGCCHBLDDED_k__BackingField"] = true;
        unknown.Fields["new_required_value"] = 71;
        order.Items = [29, 17, null, 17, 0];
        var after = GameGraph(true);
        foreach (var root in roots)
            after.Read(root);
        Require(
            Equals(
                after.Nodes.Single(n => n.type == source.Type).fields[
                    "_JCNLPCLKHEI_k__BackingField"
                ],
                0
            )
                && Equals(
                    after.Nodes.Single(n => n.type == research.Type).fields[
                        "_BFKNNONLCOF_k__BackingField"
                    ],
                    67
                )
                && Equals(
                    after.Nodes.Single(n => n.type == character.Type).fields[
                        "_MGCCHBLDDED_k__BackingField"
                    ],
                    true
                )
                && Equals(
                    after.Nodes.Single(n => n.type == unknown.Type).fields["new_required_value"],
                    71
                )
                && before
                    .Nodes.Single(n => n.type == order.Type)
                    .items!.SequenceEqual(new object?[] { 17, null, 0, 17, 29 })
                && after.Nodes.Single(n => n.type == order.Type).items!.SequenceEqual(order.Items!)
                && before
                    .Nodes.Single(n => n.type == request.Type)
                    .protobuf!.SequenceEqual(requestBytes)
                && after
                    .Nodes.Single(n => n.type == response.Type)
                    .protobuf!.SequenceEqual(responseBytes)
                && after
                    .Nodes.Single(n => n.type == resources.Type)
                    .protobuf!.SequenceEqual(resourceBytes),
            "Atelier before/after data became stale, lost null/default/order, or changed original typed message bytes."
        );
        using var output = new MemoryStream();
        CaptureJson.WriteLine(
            output,
            new
            {
                snapshot_schema = 2,
                snapshot_contracts = after.Contracts,
                nodes = after.Nodes,
            }
        );
        using var stored = JsonDocument.Parse(output.ToArray());
        var storedProtobufs = stored
            .RootElement.GetProperty("nodes")
            .EnumerateArray()
            .Where(n => n.TryGetProperty("protobuf", out _))
            .Select(n => n.GetProperty("protobuf").GetBytesFromBase64())
            .ToArray();
        Require(
            storedProtobufs.Length == 3
                && storedProtobufs[0].SequenceEqual(requestBytes)
                && storedProtobufs[1].SequenceEqual(responseBytes)
                && storedProtobufs[2].SequenceEqual(resourceBytes),
            "Persisted Atelier request/response/resource unknown protobuf fields were altered."
        );
    }

    private static void ProtobufOnlySnapshotsBindChildrenAndPreserveFailures()
    {
        var response = new GameModel("Blend.ApiSchema.Api.BattleStartResponse", 1);
        var history = new GameModel("Blend.ApiSchema.Model.BattleHistory", 2);
        var setup = new GameModel("Blend.ApiSchema.Model.BattleActionSetup", 3);
        var state = new GameModel("Blend.ApiSchema.Model.BattleState", 4);
        response.Fields["history_"] = history;
        history.Fields["actionSetups_"] = setup;
        setup.Fields["state_"] = state;
        state.Fields["history_cycle"] = history;
        var context = new NetworkContext(
            "1",
            "1",
            "17",
            "/quest/battle/start",
            "Battle",
            false,
            "1",
            "synthetic_original_response"
        );
        var bindings = new Dictionary<GameModel, ContextBinding> { [response] = new() };
        bindings[response].Bind(context);
        var bytes = new byte[] { 8, 0, 0xF8, 0x7F, 1 };
        var serializations = 0;
        var fallbackReads = 0;
        var gaps = new List<string>();
        string[] Bind(object value) =>
            NativeLineage.BindChildren(
                value,
                node => node,
                node =>
                    ((GameModel)node).Fields.Select(p => new SnapshotMember(
                        ((GameModel)node).Type + "." + p.Key,
                        () => p.Value
                    )),
                (parent, child, edge) =>
                {
                    if (!bindings.TryGetValue((GameModel)child, out var target))
                        bindings[(GameModel)child] = target = new();
                    var gap = NativeLineage.Transfer(
                        parent,
                        child,
                        edge,
                        item =>
                            bindings.TryGetValue((GameModel)item, out var bound)
                                ? bound.State
                                : default,
                        (_, proof, sourceEdge) => target.Bind(proof, sourceEdge),
                        (_, environment, sourceEdge) => target.Origin.Bind(environment, sourceEdge),
                        _ => target.InvalidateOperation()
                    );
                    if (gap is not null)
                        gaps.Add(gap);
                },
                item => bindings.TryGetValue((GameModel)item, out var bound) ? bound.State : default
            );
        var graph = new DetachedSnapshot(value =>
            new(
                ((GameModel)value).Type,
                value,
                [],
                Protobuf: () =>
                {
                    Require(
                        bindings.TryGetValue(state, out var bound) && bound.Current == context,
                        "Protobuf-only copying removed the exact response/history/setup/state constructor lineage."
                    );
                    serializations++;
                    return bytes.ToArray();
                },
                ProtobufFallback: () =>
                {
                    fallbackReads++;
                    throw new InvalidOperationException();
                },
                Lineage: () => Bind(value)
            )
        );
        graph.Read(response);
        graph.Read(response);
        Require(
            graph.Complete
                && graph.Nodes.Count == 1
                && graph.Nodes[0].fields.Count == 0
                && graph.Nodes[0].protobuf!.SequenceEqual(bytes)
                && serializations == 1
                && fallbackReads == 0,
            "Protobuf unknown/presence bytes were changed, serialized twice, or expanded into repeated computed fields."
        );
        bindings.Clear();
        bindings[response] = new();
        bindings[response].Bind(context);
        bindings[history] = new();
        bindings[history].Bind(context with { RequestId = "2", AttemptId = "2", IsPrivate = true });
        var ambiguous = new DetachedSnapshot(value =>
            new(
                ((GameModel)value).Type,
                value,
                [],
                Protobuf: () => bytes.ToArray(),
                Lineage: () => Bind(value).Concat(gaps).ToArray()
            )
        );
        ambiguous.Read(response);
        Require(
            !ambiguous.Complete
                && ambiguous.Nodes[0].protobuf!.SequenceEqual(bytes)
                && bindings[history].Origin.Proof.Ambiguous
                && bindings[state].Current is null
                && bindings[state].Origin.Proof.IsPrivate is null
                && gaps.Count > 0,
            "A root origin bridged an already ambiguous nested owner and invented a fresh descendant's origin/operation."
        );
        var failed = new DetachedSnapshot(value =>
            new(
                "native.message",
                value,
                [],
                Protobuf: () => throw new InvalidDataException(),
                ProtobufFallback: () =>
                    [
                        new("available", () => 41),
                        new("missing", () => throw new MissingMemberException()),
                    ]
            )
        );
        failed.Read(response);
        Require(
            !failed.Complete
                && Equals(failed.Nodes[0].fields["available"], 41)
                && failed.Failures.Any(f => f.Field == "$protobuf")
                && failed.Failures.Any(f => f.Field == "missing")
                && failed.Nodes[0].protobuf is null,
            "Failed serialization invented bytes or discarded available required field fallback data."
        );
        var brokenLineage = new DetachedSnapshot(value =>
            new(
                "native.message",
                value,
                [],
                Protobuf: () => bytes.ToArray(),
                Lineage: () =>
                    NativeLineage.BindChildren(
                        value,
                        node => node,
                        _ => [new("history_", () => throw new MissingMemberException())],
                        (_, _, _) => { },
                        _ => default
                    )
            )
        );
        brokenLineage.Read(response);
        Require(
            !brokenLineage.Complete
                && brokenLineage.Nodes[0].protobuf!.SequenceEqual(bytes)
                && brokenLineage
                    .Nodes[0]
                    .lineage_failures!.Single()
                    .Contains("MissingMemberException", StringComparison.Ordinal),
            "Lineage failure lost available bytes or appeared as complete provenance."
        );
        var unknown = new EnvironmentBinding();
        Require(unknown.Proof.IsPrivate is null, "An unbound lineage invented an Official origin.");
        var shared = new ContextBinding();
        shared.Bind(context, "first_exact_owner");
        shared.Bind(
            context with
            {
                RequestId = "2",
                AttemptId = "2",
                OperationId = "19",
            },
            "second_exact_owner"
        );
        Require(
            shared.Current is null && shared.Origin.Proof.IsPrivate == false,
            "Environment-only ownership fabricated a current operation for a reused native owner."
        );
    }

    private static void ParsedLineageRevisitsChangedProofs()
    {
        var first = new NetworkContext(
            "1",
            "1",
            "17",
            "/quest/battle/start",
            "Battle",
            false,
            "1",
            "original_response"
        );
        foreach (var otherOrigin in new[] { false, true })
        {
            var root = new GameModel("response", 1);
            var shared = new GameModel("history", 2);
            var descendant = new GameModel("setup", 3);
            var leaf = new GameModel("state", 4);
            var longPath = new GameModel("long_path", 5);
            var middle = new GameModel("middle", 6);
            var beforeTail = new GameModel("before_tail", 7);
            var tail = new GameModel("conflicting_owner", 8);
            root.Fields["short"] = shared;
            root.Fields["long"] = longPath;
            shared.Fields["setup"] = descendant;
            descendant.Fields["state"] = leaf;
            leaf.Fields["cycle"] = shared;
            longPath.Fields["middle"] = middle;
            middle.Fields["before_tail"] = beforeTail;
            beforeTail.Fields["tail"] = tail;
            tail.Fields["late_history"] = shared;
            var bindings = new Dictionary<GameModel, ContextBinding>
            {
                [root] = new(),
                [tail] = new(),
            };
            bindings[root].Bind(first);
            bindings[tail]
                .Bind(
                    first with
                    {
                        RequestId = "2",
                        AttemptId = "2",
                        OperationId = "18",
                        ResponseId = "2",
                        IsPrivate = otherOrigin,
                    }
                );
            var visits = 0;
            var sawLateConflict = false;
            var gaps = new List<string>();
            ContextBinding Bound(object value)
            {
                var model = (GameModel)value;
                if (!bindings.TryGetValue(model, out var binding))
                    bindings[model] = binding = new();
                return binding;
            }
            var failures = NativeLineage.BindChildren(
                root,
                value => ((GameModel)value).Lifetime,
                value =>
                {
                    visits++;
                    Require(visits <= 32, "Changed-proof lineage did not terminate its cycle.");
                    return ((GameModel)value).Fields.Select(p => new SnapshotMember(
                        p.Key,
                        () => p.Value
                    ));
                },
                (parent, child, edge) =>
                {
                    if (ReferenceEquals(parent, tail) && !sawLateConflict)
                    {
                        Require(
                            Bound(leaf).Current == first,
                            "Conflict fixture did not first publish proof to a visited descendant."
                        );
                        sawLateConflict = true;
                    }
                    var gap = NativeLineage.Transfer(
                        parent,
                        child,
                        edge,
                        value => Bound(value).State,
                        (value, context, source) => Bound(value).Bind(context, source),
                        (value, origin, source) => Bound(value).Origin.Bind(origin, source),
                        value => Bound(value).InvalidateOperation()
                    );
                    if (gap is not null)
                        gaps.Add(gap);
                },
                value => Bound(value).State
            );
            Require(
                failures.Length == 0 && sawLateConflict && gaps.Count > 0,
                "Delayed lineage conflict was skipped or mistaken for a field-read fault."
            );
            foreach (var model in new[] { shared, descendant, leaf })
                Require(
                    Bound(model).Current is null
                        && Bound(model).Ambiguous
                        && Bound(model).Origin.Proof.IsPrivate == (otherOrigin ? null : false)
                        && Bound(model).Origin.Proof.Ambiguous == otherOrigin,
                    "A late conflict retained stale descendant operation/origin proof."
                );
            Require(
                Bound(root).Current == first,
                "A downstream conflict invalidated an unrelated response-root proof."
            );
        }

        var unknown = new ContextBinding();
        var independentlyBound = new ContextBinding();
        independentlyBound.Bind(first);
        NativeLineage.Transfer(
            unknown,
            independentlyBound,
            "identity_only_parent",
            value => ((ContextBinding)value).State,
            (value, context, edge) => ((ContextBinding)value).Bind(context, edge),
            (value, origin, edge) => ((ContextBinding)value).Origin.Bind(origin, edge),
            value => ((ContextBinding)value).InvalidateOperation()
        );
        Require(
            independentlyBound.Current == first && !independentlyBound.Origin.Proof.Ambiguous,
            "An identity-only parent revoked independently proven child ownership."
        );

        var deep = new GameModel("parsed_response", 1);
        var current = deep;
        for (var index = 0; index < 20_000; index++)
        {
            var next = new GameModel("stored_message", index + 2);
            current.Fields["next"] = next;
            current = next;
        }
        current.Fields["cycle"] = deep;
        var failure = new MissingMemberException(
            "required_field",
            new InvalidDataException("inner")
        );
        deep.Fields["broken"] = failure;
        var bindingsDeep = new Dictionary<GameModel, ContextBinding> { [deep] = new() };
        bindingsDeep[deep].Bind(first);
        ContextBinding DeepBound(object value)
        {
            var model = (GameModel)value;
            if (!bindingsDeep.TryGetValue(model, out var binding))
                bindingsDeep[model] = binding = new();
            return binding;
        }
        var reads = 0;
        var readFailures = NativeLineage.BindChildren(
            deep,
            value => ((GameModel)value).Lifetime,
            value =>
                ((GameModel)value).Fields.Select(p => new SnapshotMember(
                    p.Key,
                    () =>
                    {
                        reads++;
                        if (p.Value is Exception error)
                            throw error;
                        return p.Value;
                    }
                )),
            (parent, child, edge) =>
                NativeLineage.Transfer(
                    parent,
                    child,
                    edge,
                    value => DeepBound(value).State,
                    (value, context, source) => DeepBound(value).Bind(context, source),
                    (value, origin, source) => DeepBound(value).Origin.Bind(origin, source),
                    value => DeepBound(value).InvalidateOperation()
                ),
            value => DeepBound(value).State
        );
        Require(
            reads == 20_002
                && bindingsDeep.Count == 20_001
                && DeepBound(current).Current == first
                && readFailures.Length == 1
                && readFailures[0] == "broken:" + failure,
            "Deep parsed-message lineage clipped data, expanded value graphs, lost a full read fault, or cycled."
        );
    }

    private static void FocusedProjectionHandlesDeepConcurrentAndMissingData()
    {
        var root = new GameModel("required.gameplay", 1)
        {
            Unavailable = ["required_unmapped_native_field"],
        };
        root.Fields["available"] = 47;
        root.Fields["absent"] = null;
        var current = root;
        for (var index = 0; index < 20_000; index++)
        {
            var next = new GameModel("required.gameplay", index + 2);
            current.Fields["next"] = next;
            current = next;
        }
        current.Fields["cycle"] = root;
        root.Items = Enumerable.Range(0, 10_000).Select(index => (object?)index).ToArray();
        var graph = GameGraph(true);
        graph.Read(root);
        Require(
            !graph.Complete
                && graph.Nodes.Count == 20_001
                && graph.Nodes[0].items!.Count == 10_000
                && graph.Nodes[0].fields["absent"] is null
                && Equals(graph.Nodes[0].fields["available"], 47),
            "Focused copying clipped a deep/large required model or silently completed an unmapped field."
        );
        var failures = new ConcurrentQueue<Exception>();
        Parallel.For(
            0,
            32,
            index =>
            {
                try
                {
                    var owner = new GameModel("NHEIMMIAMJO", index + 1);
                    owner.Fields["_NHNGMNDFPGC_k__BackingField"] = (long)index;
                    var copy = GameGraph(true);
                    copy.Read(owner);
                    owner.Fields["_NHNGMNDFPGC_k__BackingField"] = -1L;
                    Require(
                        Equals(
                            copy.Nodes[0].fields["_NHNGMNDFPGC_k__BackingField"],
                            index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ),
                        "Concurrent/reordered independent callbacks shared or rewrote detached mutable snapshots."
                    );
                }
                catch (Exception error)
                {
                    failures.Enqueue(error);
                }
            }
        );
        Require(
            failures.IsEmpty,
            "Concurrent independent focused observations lost required state."
        );
        var token = new GameModel("Il2CppSystem.Threading.CancellationToken", 1);
        var source = new GameModel("Il2CppSystem.Threading.CancellationTokenSource", 2);
        token.Fields["_source"] = source;
        var task = new GameModel("Cysharp.Threading.Tasks.UniTask", 3);
        task.Fields["source"] = new GameModel("Cysharp.Threading.Tasks.IUniTaskSource", 4);
        task.Fields["token"] = (short)0;
        var actualValues = GameGraph(true);
        actualValues.Read(token);
        actualValues.Read(task);
        Require(
            actualValues.Complete
                && actualValues.Nodes.Count == 2
                && actualValues.Nodes[0].fields["_source"] is NativeReference
                && actualValues.Nodes[1].fields["source"] is NativeReference
                && Equals(actualValues.Nodes[1].fields["token"], (short)0),
            "Actual CancellationToken/UniTask values vanished with excluded state-machine infrastructure."
        );
    }

    private static void FocusedProjectionWorkload()
    {
        var unit = new GameModel("NHEIMMIAMJO", 1);
        var state = new GameModel("BattleStateVariables", 2);
        var move = new GameModel("BattleMoveControl", 3);
        var grid = new GameModel("GNFAEODJFGH", 4);
        unit.Fields["CIHHEGKEOED"] = state;
        unit.Fields["_NHNGMNDFPGC_k__BackingField"] = 9007199254741007L;
        unit.Fields["_ANBLCJMJBLA_k__BackingField"] = false;
        state.Fields["_KHHJFPDFDEO_k__BackingField"] = move;
        state.Fields["m_playerUnitList"] = unit;
        move.Fields["_AGJJJNHPEIF_k__BackingField"] = grid;
        var cells = new List<object?>();
        for (var index = 0; index < 900; index++)
        {
            var cell = new GameModel("KEMCGNKMCKL", index * 4 + 5);
            var map = new GameModel("grid_map", index * 4 + 6);
            var pair = new GameModel("grid_pair", index * 4 + 7);
            var row = new GameModel("AJKGBHKAEPL", index * 4 + 8);
            cell.Fields["_AHDEJMGNNFK_k__BackingField"] = (float)index;
            cell.Fields["_OKPCHFEAIFA_k__BackingField"] = -(float)index;
            cell.Fields["IIMLOAOPKAE"] = map;
            map.Items = [pair];
            pair.Fields["key"] = index;
            pair.Fields["value"] = row;
            row.Fields["_KJKBHJDLFGH_k__BackingField"] = index;
            row.Fields["synthetic_unrelated_catalog_text"] = new string('x', 4096);
            cells.Add(cell);
        }
        grid.Items = cells.ToArray();
        var measurements = new List<object>();
        foreach (var focused in new[] { false, true })
        {
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var snapshot = GameGraph(focused);
            var root = snapshot.Read(unit);
            var copied = watch.Elapsed.TotalMilliseconds;
            using var json = new MemoryStream();
            CaptureJson.WriteLine(
                json,
                new
                {
                    root,
                    snapshot_schema = 2,
                    snapshot_contracts = snapshot.Contracts,
                    nodes = snapshot.Nodes,
                }
            );
            var encoded = watch.Elapsed.TotalMilliseconds;
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, true))
                gzip.Write(json.GetBuffer().AsSpan(0, checked((int)json.Length)));
            Require(
                Equals(snapshot.Nodes[0].fields["_NHNGMNDFPGC_k__BackingField"], "9007199254741007")
                    && Equals(snapshot.Nodes[0].fields["_ANBLCJMJBLA_k__BackingField"], false),
                "Focused performance savings changed required gameplay reconstruction."
            );
            Require(
                !focused
                    || snapshot.Nodes.All(node =>
                        node.type is not ("KEMCGNKMCKL" or "AJKGBHKAEPL")
                    ),
                "Focused copying still expanded the unrelated movement catalog."
            );
            measurements.Add(
                new
                {
                    focused,
                    nodes = snapshot.Nodes.Count,
                    json_bytes = json.Length,
                    gzip_bytes = compressed.Length,
                    copy_ms = copied,
                    encode_ms = encoded - copied,
                    gzip_ms = watch.Elapsed.TotalMilliseconds - encoded,
                    allocated_bytes = GC.GetAllocatedBytesForCurrentThread() - allocated,
                }
            );
        }
        Console.WriteLine("focused_projection_synthetic=" + JsonSerializer.Serialize(measurements));
    }

    private sealed class GameModel(string type, long lifetime)
    {
        internal string Module = "Blend.dll";
        internal string Type { get; } = type;
        internal long Lifetime { get; } = lifetime;
        internal Dictionary<string, object?> Fields { get; } = new(StringComparer.Ordinal);
        internal object?[]? Items;
        internal byte[]? Protobuf;
        internal string[]? Unavailable;
    }

    private static void ProtobufFailureKeepsOtherFields()
    {
        var graph = new DetachedSnapshot(value =>
            new(
                "native.message",
                value,
                [new("available", () => 31)],
                Protobuf: () => throw new InvalidDataException()
            )
        );
        graph.Read(new Model());
        Require(
            !graph.Complete
                && graph.Failures.Single().Field == "$protobuf"
                && Equals(graph.Nodes[0].fields["available"], 31),
            "Protobuf read failure discarded other available fields."
        );
    }

    private static void CollectionFailureKeepsReadItems()
    {
        static IEnumerable<object?> Values()
        {
            yield return 7;
            yield return null;
            yield return 11;
            throw new InvalidDataException();
        }
        var graph = new DetachedSnapshot(value =>
            new("native.collection", value, [], Items: Values)
        );
        graph.Read(new Model());
        Require(
            !graph.Complete
                && graph.Nodes[0].items!.SequenceEqual(new object?[] { 7, null, 11 })
                && graph.Failures.Single().Field == "items[3]",
            "Enumerator failure discarded the successfully read prefix."
        );
    }

    private static void FullGraphHasNoDepthOrItemCutoff()
    {
        var head = new Model();
        var current = head;
        for (var index = 0; index < 20_000; index++)
            current = current.Next = new();
        current.Next = head;
        head.Items = Enumerable.Range(0, 10_000).Select(x => (object?)x).ToArray();
        var graph = new DetachedSnapshot(value =>
        {
            if (value is object?[] items)
                return new("items", items, [], () => items);
            var model = (Model)value;
            return new(
                "Model",
                model,
                [
                    new("next", () => model.Next),
                    new("items", () => model.Items),
                    new("label", () => "semantic text"),
                ]
            );
        });
        var root = graph.Read(head);
        Require(
            graph.Complete && graph.Nodes.Count == 20_002,
            "Complete deep/cyclic graph was shortened."
        );
        Require(
            graph.Nodes.Single(n => n.type == "items").items!.Count == 10_000,
            "Native-sized item list was shortened."
        );
        using var result = JsonDocument.Parse(
            JsonSerializer.SerializeToUtf8Bytes(
                new
                {
                    root,
                    snapshot_schema = 2,
                    snapshot_contracts = graph.Contracts,
                    nodes = graph.Nodes,
                }
            )
        );
        Require(
            result.RootElement.GetProperty("nodes").GetArrayLength() == 20_002,
            "Flat graph JSON lost nodes."
        );
        Require(graph.Nodes[0].fields["label"] is "semantic text", "Required text was filtered.");
    }

    private static void FieldFailureKeepsAvailableValues()
    {
        var owner = new Model();
        var details = "reader-message-" + new string('x', 100_000) + "-unclipped-tail";
        var graph = new DetachedSnapshot(value =>
            new(
                "Partial",
                value,
                [
                    new("zero", () => 0),
                    new("absent", () => null),
                    new(
                        "unreadable",
                        () =>
                            throw new InvalidOperationException(
                                details,
                                new ArgumentException("inner-reader-detail")
                            )
                    ),
                    new("after", () => 29),
                ]
            )
        );
        graph.Read(owner);
        Require(
            !graph.Complete && graph.Failures.Count == 1 && graph.Failures[0].Field == "unreadable",
            "Field failure was not explicit."
        );
        Require(
            Equals(graph.Nodes[0].fields["zero"], 0)
                && graph.Nodes[0].fields["absent"] is null
                && Equals(graph.Nodes[0].fields["after"], 29),
            "A failed field discarded available values."
        );
        Require(
            graph.Failures[0].Details.Contains(details, StringComparison.Ordinal)
                && graph
                    .Failures[0]
                    .Details.Contains("inner-reader-detail", StringComparison.Ordinal)
                && graph
                    .Failures[0]
                    .Details.Contains(
                        nameof(FieldFailureKeepsAvailableValues),
                        StringComparison.Ordinal
                    ),
            "A reader failure clipped its message, inner exception or stack evidence."
        );
    }

    private static void ProtobufAndWideScalarsStayExact()
    {
        var bytes = new byte[33 * 1024 * 1024];
        bytes[0] = 8;
        bytes[^1] = 127;
        var owner = new Model();
        var graph = new DetachedSnapshot(value =>
            new(
                "native.message",
                value,
                [
                    new("signed", () => long.MinValue),
                    new("unsigned", () => ulong.MaxValue),
                    new("nan", () => BitConverter.UInt32BitsToSingle(0x7FC00029)),
                    new("negative_zero", () => -0.0f),
                ],
                Protobuf: () => bytes
            )
        );
        graph.Read(owner);
        var node = graph.Nodes.Single();
        Require(
            graph.Complete
                && ReferenceEquals(node.protobuf, bytes)
                && node.protobuf?.Length == bytes.Length,
            "Complete serialized message was shortened above the former body limit."
        );
        Require(
            Equals(node.fields["signed"], "-9223372036854775808")
                && Equals(node.fields["unsigned"], "18446744073709551615"),
            "64-bit scalar identity changed."
        );
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(node.fields));
        Require(
            json.RootElement.GetProperty("nan").GetProperty("bits").GetString() == "7FC00029"
                && json.RootElement.GetProperty("negative_zero").GetProperty("bits").GetString()
                    == "80000000",
            "Float bits changed."
        );
    }

    private static void ExactSignatureRejectsSimilarOverloads()
    {
        var method = typeof(NativeContractChecks).GetMethod(
            nameof(Signature),
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        Require(
            NativeContract.Matches(
                method,
                "System.Int64",
                ["System.Int32", "System.String&"],
                true
            ),
            "Exact signature was rejected."
        );
        Require(
            !NativeContract.Matches(
                method,
                "System.Int32",
                ["System.Int32", "System.String&"],
                true
            )
                && !NativeContract.Matches(
                    method,
                    "System.Int64",
                    ["System.Int64", "System.String&"],
                    true
                )
                && !NativeContract.Matches(
                    method,
                    "System.Int64",
                    ["System.Int32", "System.String"],
                    true
                )
                && !NativeContract.Matches(
                    method,
                    "System.Int64",
                    ["System.Int32", "System.String&"],
                    false
                ),
            "Name/arity-compatible but ABI-different signature was accepted."
        );
        var nested = typeof(NativeContractChecks).GetMethod(
            nameof(NestedSignature),
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        Require(
            NativeContract.Matches(
                nested,
                "System.Collections.Generic.Dictionary`2<System.String,System.Int32[]>",
                ["System.Collections.Generic.List`1<System.Int64>&"],
                true
            ),
            "Shared canonical formatter changed generic, array or byref ABI identity."
        );
    }

    private static void EmbeddedBoundaryContractsAreExplicit()
    {
        var battle = Plugin.ReadResource<Battle.HookSpec[]>("BattleHooks.json");
        var atelier = Atelier.Contracts();
        var all = battle.Concat(atelier).ToArray();
        Require(
            battle.Length == 71 && atelier.Length == 148,
            "Native boundary inventory changed without reviewing its exact source contract."
        );
        Require(
            all.All(h =>
                h.Rva > 0
                && h.Type.Length > 0
                && h.Method.Length > 0
                && h.Returns.Length > 0
                && h.Boundary.Length > 0
                && h.Variant.Length > 0
            ),
            "Native hook identity or signature contract is incomplete."
        );
        Require(
            all.Select(h => h.System + ":" + h.Boundary).Distinct(StringComparer.Ordinal).Count()
                == all.Length,
            "Two native boundaries share the same declared identity."
        );
        Require(
            battle.Count(h => h.AsyncReturn) == 6
                && battle.Single(h => h.Role == "damage_flags_merge").ActivationGap
                    == "tiny_native_stub_caller_register_contract_unqualified",
            "Known native activation/completion gap disappeared."
        );
        var types = Plugin.ReadResource<string[]>("SnapshotTypes.json");
        Require(
            types.Length == 211
                && types.Distinct(StringComparer.Ordinal).Count() == types.Length
                && all.Select(h => h.Type).All(t => types.Contains(t, StringComparer.Ordinal)),
            "Native boundary owner is absent from the explicit startup reader inventory."
        );
        using var certificate = Plugin.ReadResource<JsonDocument>("NativeHookBodies.json");
        var entries = certificate.RootElement.GetProperty("bodies").EnumerateArray().ToArray();
        var bodies = entries.ToDictionary(
            entry => entry.GetProperty("rva").GetInt64(),
            entry => entry.GetProperty("aliases").GetInt32()
        );
        Require(
            certificate.RootElement.GetProperty("schema").GetInt32() == 1
                && bodies.Count == entries.Length
                && bodies.Values.All(aliases => aliases > 0),
            "Complete global native-body certificate is absent or changed without qualification."
        );
        Require(
            all.All(h => bodies.ContainsKey(h.Rva)),
            "A gameplay detour lacks global native-body evidence."
        );
        var shared = all.Where(h => bodies[h.Rva] != 1).ToArray();
        Require(
            shared.Length == 3
                && shared.All(h => h.ActivationGap == "shared_native_body_unqualified")
                && bodies[64_433_328] == 2
                && bodies[47_128_560] == 2
                && bodies[47_119_264] == 4,
            "Shared native receiver bodies were admitted by managed signature alone."
        );
        Require(
            all.Count(h => h.ActivationGap is null) == 215
                && bodies[0x8C8F40] == 659
                && bodies[0x8C9060] == 135,
            "Blocked tiny/shared gameplay or API aliases disappeared from the certificate."
        );
    }

    private static long Signature(int value, ref string text) => value + text.Length;

    private static void FreshNativePhasesKeepReceiverArgumentsAndFaults()
    {
        var root = Path.Combine(
            AppContext.BaseDirectory,
            "native-phases-" + Guid.NewGuid().ToString("N")
        );
        var prior = Plugin.Writer;
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var target = typeof(NativeContractChecks).GetMethod(nameof(PhaseSignature), flags)!;
        var hooks =
            (ConcurrentDictionary<MethodBase, Battle.HookSpec>)
                typeof(Battle).GetField("Hooks", flags)!.GetValue(null)!;
        var entry = typeof(Battle).GetMethod("EnterCore", flags)!;
        var returned = typeof(Battle).GetMethod("ExitResult", flags)!;
        var leave = typeof(Battle).GetMethod("Finalizer", flags)!;
        var hook = new Battle.HookSpec(
            typeof(NativeContractChecks).FullName!,
            nameof(PhaseSignature),
            "synthetic_phase",
            1,
            "System.Int32",
            ["System.Int32&"],
            false,
            Boundary: "synthetic.native.phases"
        );
        object Enter(object instance, int value)
        {
            object?[] arguments = [instance, new object[] { value }, target, null];
            entry.Invoke(null, arguments);
            return arguments[3]
                ?? throw new InvalidDataException("Native entry lost its invocation state.");
        }
        try
        {
            using var writer = new SessionWriter(
                root,
                new { synthetic = true },
                _ => new { },
                _ => { }
            );
            Plugin.Writer = writer;
            using (
                Network.PushContext(
                    new(
                        "1",
                        "1",
                        null,
                        "/synthetic/native-phases",
                        "Battle",
                        false,
                        "1",
                        "synthetic_contract_check"
                    )
                )
            )
            {
                hooks[target] = hook;
                var outer = Enter(7, 11);
                var nested = Enter(13, 17);
                returned.Invoke(null, [19, new object[] { 23 }, target, nested, 29]);
                leave.Invoke(null, [false, true, 19, new object[] { 23 }, target, nested]);
                returned.Invoke(null, [31, new object[] { 37 }, target, outer, 41]);
                leave.Invoke(null, [false, true, 31, new object[] { 37 }, target, outer]);
                var faulted = Enter(43, 47);
                leave.Invoke(null, [true, true, 53, new object[] { 59 }, target, faulted]);
                var unavailable = Enter(71, 73);
                leave.Invoke(
                    null,
                    [true, false, null, new object[] { null! }, target, unavailable]
                );
                hooks[target] = hook with
                {
                    Method = ".ctor",
                    Boundary = "synthetic.native.constructor_fault",
                };
                var constructor = Enter("uninitialized_receiver", 61);
                leave.Invoke(
                    null,
                    [true, true, "still_uninitialized", new object[] { 67 }, target, constructor]
                );
                hooks[target] = hook with
                {
                    Method = "UpdateResources",
                    Rva = 0x2A82230,
                    System = "Atelier",
                    Boundary = "UserDataManager.UpdateResources",
                };
                var resource = Enter(null!, 79);
                leave.Invoke(null, [true, false, null, new object[] { null! }, target, resource]);
            }
            writer.Dispose();
            var records = new List<JsonElement>();
            foreach (
                var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.AllDirectories)
            )
            {
                using var archive = ZipFile.OpenRead(path);
                using var source = archive.GetEntry("combat.jsonl")!.Open();
                using var lines = new StreamReader(source);
                while (lines.ReadLine() is { } line)
                {
                    using var json = JsonDocument.Parse(line);
                    var data = json.RootElement.GetProperty("data");
                    if (
                        data.GetProperty("boundary_id")
                            .GetString()!
                            .StartsWith("synthetic.native.", StringComparison.Ordinal)
                        || data.GetProperty("boundary_id").GetString()
                            == "UserDataManager.UpdateResources"
                    )
                        records.Add(data.Clone());
                }
            }
            Require(
                records.Count == 12,
                "Native phase observers lost or duplicated an entry/terminal record: "
                    + records.Count
                    + " of 12."
            );
            var calls = records
                .GroupBy(record => record.GetProperty("invocation_id").GetString()!)
                .ToArray();
            Require(
                calls.Length == 6 && calls.All(call => call.Count() == 2),
                "Native invocation identities did not balance."
            );
            var outerRecords = calls
                .Single(call =>
                    call.Any(record =>
                        record.GetProperty("instance").ValueKind == JsonValueKind.Number
                        && record.GetProperty("instance").GetInt32() == 7
                    )
                )
                .ToArray();
            var outerEntry = outerRecords.Single(record =>
                record.GetProperty("phase").GetString() == "enter"
            );
            var outerExit = outerRecords.Single(record =>
                record.GetProperty("phase").GetString() == "exit"
            );
            Require(
                outerEntry.GetProperty("projection_contract").GetString()
                    == NativeProjection.Contract
                    && outerEntry.GetProperty("instance_contract").GetString()
                        == "initialized_owner_gameplay_projection"
                    && outerEntry.GetProperty("values")[0].GetInt32() == 11
                    && outerExit.GetProperty("instance").GetInt32() == 31
                    && outerExit.GetProperty("values")[0].GetInt32() == 37
                    && outerExit.GetProperty("result").GetInt32() == 41,
                "Return observation reused the entry receiver box or stale byref argument values."
            );
            var nestedEntry = records.Single(record =>
                record.GetProperty("instance").ValueKind == JsonValueKind.Number
                && record.GetProperty("instance").GetInt32() == 13
            );
            Require(
                nestedEntry.GetProperty("parent_invocation_id").GetString()
                    == outerEntry.GetProperty("invocation_id").GetString(),
                "Nested native call lost its proven invocation parent."
            );
            var fault = records.Single(record =>
                record.GetProperty("instance").ValueKind == JsonValueKind.Number
                && record.GetProperty("instance").GetInt32() == 53
            );
            Require(
                fault.GetProperty("native_original_threw").GetBoolean()
                    && fault.GetProperty("phase_snapshot_available").GetBoolean()
                    && fault.GetProperty("error").GetString() == "native_original_threw"
                    && fault.GetProperty("result_contract").GetString()
                        == "native_fault_no_return_value"
                    && fault.GetProperty("values")[0].GetInt32() == 59
                    && fault.GetProperty("result").ValueKind == JsonValueKind.Null,
                "Native fault lost current arguments, invented a return value or fabricated an exception type."
            );
            var missing = records.Single(record =>
                record.GetProperty("error").GetString() == "native_fault_phase_snapshot_unavailable"
                && record.GetProperty("boundary_id").GetString() == "synthetic.native.phases"
            );
            Require(
                missing.GetProperty("native_original_threw").GetBoolean()
                    && !missing.GetProperty("phase_snapshot_available").GetBoolean()
                    && !missing.GetProperty("copied_fields_complete").GetBoolean()
                    && missing.GetProperty("instance_contract").GetString()
                        == "phase_snapshot_unavailable"
                    && missing.GetProperty("instance").ValueKind == JsonValueKind.Null
                    && missing.GetProperty("values")[0].ValueKind == JsonValueKind.Null,
                "Unavailable native fault materialization was relabelled as current complete data."
            );
            Require(
                records
                    .Where(record =>
                        record.GetProperty("boundary_id").GetString()
                        == "synthetic.native.constructor_fault"
                    )
                    .All(record =>
                        record.GetProperty("instance").ValueKind == JsonValueKind.Null
                        && record.GetProperty("instance_contract").GetString()
                            == "uninitialized_constructor_receiver"
                    ),
                "Constructor entry/fault read a receiver whose initialization had not completed."
            );
            var resourceRecords = records
                .Where(record =>
                    record.GetProperty("boundary_id").GetString()
                    == "UserDataManager.UpdateResources"
                )
                .ToArray();
            var resourceEntry = resourceRecords.Single(record =>
                record.GetProperty("phase").GetString() == "enter"
            );
            var resourceFault = resourceRecords.Single(record =>
                record.GetProperty("phase").GetString() == "exception"
            );
            Require(
                resourceEntry.GetProperty("projection_contract").GetString()
                    == ResourceReceiverProjection.Contract
                    && resourceEntry.GetProperty("instance_contract").GetString()
                        == ResourceReceiverProjection.StateContract
                    && !resourceEntry.GetProperty("copied_fields_complete").GetBoolean()
                    && resourceEntry.GetProperty("failures").GetArrayLength() == 1
                    && resourceEntry.GetProperty("values")[0].GetInt32() == 79
                    && resourceFault.GetProperty("instance").ValueKind == JsonValueKind.Null
                    && resourceFault.GetProperty("instance_contract").GetString()
                        == "phase_snapshot_unavailable"
                    && !resourceFault.GetProperty("phase_snapshot_available").GetBoolean()
                    && !resourceFault.GetProperty("copied_fields_complete").GetBoolean()
                    && resourceFault.GetProperty("native_original_threw").GetBoolean(),
                "Resource receiver failure lost available arguments or relabelled unavailable native state as applied."
            );
            Require(
                leave.ReturnType == typeof(void)
                    && leave
                        .GetParameters()
                        .All(parameter => parameter.ParameterType != typeof(Exception)),
                "Native observers regained exception replacement/suppression semantics."
            );
        }
        finally
        {
            hooks.TryRemove(target, out _);
            Plugin.Writer = prior;
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    private static int PhaseSignature(ref int value) => value;

    private static void ClosedNativeBridgeDemotesActiveCounts()
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var closed = typeof(PassiveNativeHook).GetField("closed", flags)!;
        var configured = typeof(Battle).GetField("configured", flags)!;
        var hooks =
            (ConcurrentDictionary<MethodBase, Battle.HookSpec>)
                typeof(Battle).GetField("Hooks", flags)!.GetValue(null)!;
        var battleTarget = typeof(NativeContractChecks).GetMethod(nameof(PhaseSignature), flags)!;
        var atelierTarget = typeof(NativeContractChecks).GetMethod(nameof(Signature), flags)!;
        var priorClosed = (int)closed.GetValue(null)!;
        var priorConfigured = configured.GetValue(null);
        hooks.TryGetValue(battleTarget, out var priorBattle);
        hooks.TryGetValue(atelierTarget, out var priorAtelier);
        var battle = new Battle.HookSpec(
            typeof(NativeContractChecks).FullName!,
            nameof(PhaseSignature),
            "synthetic_status",
            1,
            "System.Int32",
            ["System.Int32&"],
            true,
            Boundary: "synthetic.status.battle"
        );
        var atelier = battle with { System = "Atelier", Boundary = "synthetic.status.atelier" };
        try
        {
            hooks[battleTarget] = battle;
            hooks[atelierTarget] = atelier;
            configured.SetValue(null, new[] { battle, atelier });
            var boundCount = hooks.Count;
            closed.SetValue(null, 0);
            using var open = JsonDocument.Parse(
                JsonSerializer.Serialize(Battle.CaptureStatus(false))
            );
            Require(
                open.RootElement.GetProperty("installed").GetInt32() == boundCount
                    && open.RootElement.GetProperty("bound_count").GetInt32() == boundCount
                    && open.RootElement.GetProperty("systems")
                        .GetProperty("Battle")
                        .GetProperty("installed")
                        .GetInt32() > 0
                    && open.RootElement.GetProperty("systems")
                        .GetProperty("Atelier")
                        .GetProperty("installed")
                        .GetInt32() > 0,
                "Open native bridge lost its active bound inventory."
            );
            closed.SetValue(null, 1);
            using var inactive = JsonDocument.Parse(
                JsonSerializer.Serialize(Battle.CaptureStatus(false))
            );
            Require(
                inactive.RootElement.GetProperty("installed").GetInt32() == 0
                    && inactive.RootElement.GetProperty("bound_count").GetInt32() == boundCount
                    && inactive.RootElement.GetProperty("installed_roles").GetArrayLength() == 0
                    && !inactive.RootElement.GetProperty("complete").GetBoolean(),
                "Closed native bridge still claimed active installed hooks or completeness."
            );
            foreach (var system in inactive.RootElement.GetProperty("systems").EnumerateObject())
                Require(
                    system.Value.GetProperty("installed").GetInt32() == 0
                        && system.Value.GetProperty("bound_count").GetInt32() > 0,
                    "Closed per-system native hooks remained active or lost their bound inventory."
                );
        }
        finally
        {
            closed.SetValue(null, priorClosed);
            configured.SetValue(null, priorConfigured);
            if (priorBattle is null)
                hooks.TryRemove(battleTarget, out _);
            else
                hooks[battleTarget] = priorBattle;
            if (priorAtelier is null)
                hooks.TryRemove(atelierTarget, out _);
            else
                hooks[atelierTarget] = priorAtelier;
        }
    }

    private static Dictionary<string, int[]> NestedSignature(ref List<long> values) => new();

    private static void Require(bool condition, string error)
    {
        if (!condition)
            throw new InvalidDataException(error);
    }

    private sealed class Model
    {
        internal Model? Next;
        internal object?[]? Items;
    }
}

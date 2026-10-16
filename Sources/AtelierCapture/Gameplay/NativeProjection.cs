using System.Globalization;
using System.Reflection;

namespace AtelierCapture;

internal sealed record NativeReference(string type, string lifetime_id, string state_contract);

internal static class NativeProjection
{
    internal const string Contract = "general_gameplay_domain_v2";

    internal static string? ReferenceContract(PropertyInfo property) =>
        ReferenceContract(
            property.DeclaringType!.FullName!,
            property.Name,
            RuntimeCompatibility.SignatureType(property.PropertyType),
            property.DeclaringType.Module.ScopeName
        );

    internal static string? ReferenceContract(
        string owner,
        string field,
        string declaredType,
        string module
    )
    {
        if (module != "Blend.dll")
            return null;
        return (owner, field, declaredType) switch
        {

            ("BattleStateVariables", "_ENPKCFFLCFM_k__BackingField", "CGGKFDGOFGN")
            or
            (
                "BattleStateVariables",
                "m_actionInfoList",
                "Il2CppSystem.Collections.Generic.List`1<JCDJDPMCAPB>"
            )
            or (
                "BattleStateVariables",
                "m_actionSetupInfoList",
                "Il2CppSystem.Collections.Generic.List`1<HMIEBOKCGFE>"
            ) => "historical_state_not_recopied",


            ("NHEIMMIAMJO", "EJIDBLHEBNI", "BattleUnitTimelineControl")
            or
            ("NHEIMMIAMJO", "KACMCAGJPLP", "BattleUnitDamageControl")
            or
            ("NHEIMMIAMJO", "DLHMKEBENAD", "BattleUnitMoveControl")
            or
            ("NHEIMMIAMJO", "AJDOKPAONGN", "GCNOLOEMKPL")
            or
            ("NHEIMMIAMJO", "MMELJBOFGCL", "GCNOLOEMKPL")
            or ("NHEIMMIAMJO", "ELFKENAPCFJ", "GCNOLOEMKPL") =>
                "presentation_controller_state_not_observed",
            _ => null,
        };
    }

    private static readonly HashSet<string> GlobalOwners = new(StringComparer.Ordinal)
    {
        "BattleStateVariables",
        "BattleServerControl",
        "BattleDamageControl",
        "BattleEffectControl",
        "BattlePresenter",
        "BattleMoveControl",
        "BattleTimelineControl",
        "BattleTimelineMoveControl",
        "BattleCommandSkillControl",
        "BattleCommandItemControl",
        "BattleTutorialControl",
        "UserDataManager",
        "ResearchMenuWindow",
        "ContainerToolWindow",
        "BattleToolSelectWindow",
        "CharaLevelUpWindow",
        "CharaRarityUpWindow",
        "CharaGrowResetWindow",
        "CharaDetailMenuWindow",
        "SynthesisResultWindow",
        "SynthesisResultDialog",
        "ShipEnhanceRecipeSelectWindow",
        "SynthesisCombinationRankDisplay",
    };

    // Tuple keys: this runs for every native snapshot child, so no per-call key string.
    private static readonly HashSet<(string Owner, string Field)> UnitBacklinks =
    [
        ("BattleUnitConditionControl", "m_selfUnit"),
        ("BattleUnitDamageControl", "m_selfUnit"),
        ("BattleUnitTimelineControl", "m_selfUnit"),
        ("BattleUnitMoveControl", "m_selfUnit"),
        ("IAKOGAEEOOC", "NONLCDPNJKN"),
        ("NHEIMMIAMJO", "_PKCPKAMAFCH_k__BackingField"),
        ("NACHLMPIJDK", "_KMLNAEHMGPG_k__BackingField"),
    ];

    internal static bool ReferenceChild(string owner, string field, string childType) =>
        UnitBacklinks.Contains((owner, field))
        || childType == "Il2CppSystem.Threading.CancellationTokenSource"
        || childType.StartsWith("Cysharp.Threading.Tasks.IUniTaskSource", StringComparison.Ordinal)
        || GlobalOwners.Contains(childType)
            && !(owner == "BattleServerControl" && field == "m_battleStateVariables");

    internal static NativeReference Reference(
        string type,
        long identity,
        string stateContract = "native_lifetime_identity_only"
    ) => new(type, identity.ToString(CultureInfo.InvariantCulture), stateContract);

    internal static bool IdentityCollection(string owner, string field) =>
        owner == "NACHLMPIJDK"
        && field is "_OEPPMNOAGEB_k__BackingField" or "_JPECKMLDDDN_k__BackingField";

    internal static IEnumerable<object?> ReferenceItems(
        IEnumerable<object?> values,
        Func<object, NativeReference> reference
    )
    {
        foreach (var value in values)
            yield return value is null ? null : reference(value);
    }

    internal static string? Infrastructure(string owner, string field, string fieldType)
    {
        if (owner is "UIAssetLoader" or "AssetLoader" or "WindowBase")
            return "render_window_or_asset_infrastructure";
        if (owner == "UnityEngine.Timeline.Marker" && field == "_parent_k__BackingField")
            return "render_timeline_parent_track";
        if (fieldType is "UnityEngine.GameObject" or "UnityEngine.Transform")
            return "render_scene_object_reference";
        return null;
    }
}

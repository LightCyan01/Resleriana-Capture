using System.Reflection;

namespace AtelierCapture;

internal static class NativeFieldPolicy
{
    private static readonly HashSet<string> UnitSceneFields = new(StringComparer.Ordinal)
    {
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
    };

    internal static string? SceneExclusion(
        string owner,
        string field,
        string declaredType,
        string module
    ) =>
        module == "Blend.dll"
        && declaredType == "UnityEngine.Vector3"
        && (
            owner == "BattleStateVariables" && field == "PDHCIEFKGCL"
            || owner == "NHEIMMIAMJO" && UnitSceneFields.Contains(field)
        )
            ? "render_scene_coordinates_not_gameplay_position"
            : null;

    private static readonly HashSet<string> RenderFields = new(StringComparer.Ordinal)
    {
        "BattleUnitTimelineControl.m_cameraControl",
        "GCNOLOEMKPL.DPCHGOBNFIA",
        "GCNOLOEMKPL.NCJHHMPPGJJ",
        "NHEIMMIAMJO._JNNBMJMOMFF_k__BackingField",
        "LIAHKJACBDJ.NOLKEODGADL",
        "BattlePresenter.BDDOIEKEGKD",
        "BattlePresenter.m_playerStatusList",
        "LANAGMCAFDC.BIMAAMLCJPC",
        "BattleEffectControl.MOEIFHLIBIN",
        "BattlePresenter.m_bossEntry",
        "BattlePresenter.m_startCutIn",
        "BattlePresenter.m_startCutInWaveText",
        "BattleServerControl.HOLGFHEFOCM",
        "BattleStateVariables.AKCKNGJEDBC",
        "BattleStateVariables.BGJOFPBAFPD",
        "BattleStateVariables.FBCJDLGBFMO",
        "BattleStateVariables.MDNFIEIELKG",
        "BattleStateVariables._DMKCMHKCFOA_k__BackingField",
        "BattleStateVariables._IKFKFDMKHKE_k__BackingField",
        "BattleStateVariables._PMDADNLBHOP_k__BackingField",
        "BattleTimelineIcon.m_DeleteTurnAnimator",
        "BattleTimelineIcon.m_activeSkillAnimator",
        "BattleTimelineIcon.m_animator",
        "BattleTimelineIcon.m_attackText",
        "BattleTimelineIcon.m_attackTextObj",
        "BattleTimelineIcon.m_breakAnimator",
        "BattleTimelineIcon.m_changePanelAnimator",
        "BattleTimelineIcon.m_disableObj",
        "BattleTimelineIcon.m_enemyNumObj",
        "BattleTimelineIcon.m_enemyNumText",
        "BattleTimelineIcon.m_enemyStrongAttack",
        "BattleTimelineIcon.m_icon",
        "BattleTimelineIcon.m_nextAnimator",
        "BattleTimelineIcon.m_skillCardAnimator",
        "BattleTimelineIcon.m_stunAnimator",
        "BattleTimelineIcon.m_waitObj",
        "BattleTimelineIcon.m_waitText",
        "BattleTimelineMoveControl.m_endBurstMove",
    };

    private static readonly HashSet<string> OwnerRenderFields = new(StringComparer.Ordinal)
    {
        "AppWindowBase._CanShortcutTransitionFunc_k__BackingField",
        "AppWindowBase._IsBackPrevSceneDirect_k__BackingField",
        "AppWindowBase._IsCheckingText_k__BackingField",
        "AppWindowBase._OnPopupClose_k__BackingField",
        "AppWindowBase.m_firstUnlockObjs",
        "AppWindowBase.m_hasCommonUIParentCache",
        "AppWindowBase.m_hasPopupPrevTime",
        "AppWindowBase.m_headerElements",
        "AppWindowBase.m_isPaidGemDisp",
        "AppWindowBase.m_isPreferredFirst",
        "AppWindowBase.m_isSpareStaminaDispWindow",
        "AppWindowBase.m_itemUIInfoSetList",
        "AppWindowBase.m_soundController",
        "AppWindowBase.m_windowTitleBox",
        "BattleEffectControl.MOEIFHLIBIN",
        "BattleEffectControl._IMJPMEAKGGC_k__BackingField",
        "BattleMoveControl._OCIEPKMCFMF_k__BackingField",
        "BattlePresenter.AGLIKPCCBPO",
        "BattlePresenter.EPNPHFGPCFF",
        "BattlePresenter.HGDKNOFGNKJ",
        "BattlePresenter.KANFFABCCHC",
        "BattlePresenter.MOOCCOHJIFN",
        "BattlePresenter.m_backgroundButton",
        "BattlePresenter.m_battleHeader",
        "BattlePresenter.m_battleTimeline",
        "BattlePresenter.m_beltDialog",
        "BattlePresenter.m_bossEntry",
        "BattlePresenter.m_commandMenu",
        "BattlePresenter.m_damageCanvas",
        "BattlePresenter.m_damageList",
        "BattlePresenter.m_enemyBurst",
        "BattlePresenter.m_enemyPositionHeader",
        "BattlePresenter.m_fieldNameUI",
        "BattlePresenter.m_gimmickGauge",
        "BattlePresenter.m_headerCanvas",
        "BattlePresenter.m_itemBackButton",
        "BattlePresenter.m_itemGaugeButton",
        "BattlePresenter.m_itemView",
        "BattlePresenter.m_mainCanvas",
        "BattlePresenter.m_resultCanvas",
        "BattlePresenter.m_screenEffectCanvas",
        "BattlePresenter.m_shipCommandView",
        "BattlePresenter.m_skillNameCanvas",
        "BattlePresenter.m_skillNameUI",
        "BattlePresenter.m_soloRaidGauge",
        "BattlePresenter.m_startCutIn",
        "BattlePresenter.m_startCutInWaveText",
        "BattlePresenter.m_supportAbilityView",
        "BattlePresenter.m_targetUICanvas",
        "BattlePresenter.m_totalDamgeText",
        "BattlePresenter.m_tutorial",
        "BattleStateVariables.KANFFABCCHC",
        "BattleStateVariables.MDNFIEIELKG",
        "BattleStateVariables._IKFKFDMKHKE_k__BackingField",
        "BattleStateVariables._PMDADNLBHOP_k__BackingField",
        "BattleStateVariables._PPNPKPGFJPE_k__BackingField",
        "BattleTimelineControl.m_baseDirector",
        "BattleTimelineIcon.m_DeleteTurnAnimator",
        "BattleTimelineIcon.m_activeSkillAnimator",
        "BattleTimelineIcon.m_activeSkillSwitch",
        "BattleTimelineIcon.m_animator",
        "BattleTimelineIcon.m_attackText",
        "BattleTimelineIcon.m_attackTextObj",
        "BattleTimelineIcon.m_breakAnimator",
        "BattleTimelineIcon.m_button",
        "BattleTimelineIcon.m_changePanelAnimator",
        "BattleTimelineIcon.m_disableObj",
        "BattleTimelineIcon.m_enemyNumObj",
        "BattleTimelineIcon.m_enemyNumText",
        "BattleTimelineIcon.m_enemyStrongAttack",
        "BattleTimelineIcon.m_icon",
        "BattleTimelineIcon.m_itemIconSwitch",
        "BattleTimelineIcon.m_mainCanvasGroup",
        "BattleTimelineIcon.m_nextAnimator",
        "BattleTimelineIcon.m_skillCardAnimator",
        "BattleTimelineIcon.m_stunAnimator",
        "BattleTimelineIcon.m_waitObj",
        "BattleTimelineIcon.m_waitText",
        "BattleTimelineIconBase.m_rootObj",
        "BattleTimelineMoveControl.PCLHLPLGCJK",
        "BattleToolSelectWindow.m_UISwitch",
        "BattleToolSelectWindow.m_battleToolDetailObj",
        "BattleToolSelectWindow.m_battleToolInfoDisp",
        "BattleToolSelectWindow.m_battleToolList",
        "BattleToolSelectWindow.m_cancelButton",
        "BattleToolSelectWindow.m_decideButton",
        "BattleToolSelectWindow.m_equippedBattleToolIcons",
        "BattleToolSelectWindow.m_noSelectObj",
        "BattleToolSelectWindow.m_shipToolDetailObj",
        "BattleToolSelectWindow.m_shipToolInfoDisp",
        "BattleToolSelectWindow.m_sortFilterControl",
        "BattleToolSelectWindow.m_sortFilterObj",
        "BattleTutorialControl.MBBNGHFDJEK",
        "CharaDetailMenuWindow.m_abilityDisplay",
        "CharaDetailMenuWindow.m_abilitySwitch",
        "CharaDetailMenuWindow.m_abilityWithActiveSkillDisplay",
        "CharaDetailMenuWindow.m_animator",
        "CharaDetailMenuWindow.m_closeButton",
        "CharaDetailMenuWindow.m_infoDisplay",
        "CharaDetailMenuWindow.m_leaderSelectorDisplay",
        "CharaDetailMenuWindow.m_levelLimitEnhanceButton",
        "CharaDetailMenuWindow.m_levelUpButton",
        "CharaDetailMenuWindow.m_maxNotification",
        "CharaDetailMenuWindow.m_rarityUpBadge",
        "CharaDetailMenuWindow.m_rarityUpButton",
        "CharaDetailMenuWindow.m_resetButton",
        "CharaDetailMenuWindow.m_skillDisplay",
        "CharaDetailMenuWindow.m_statusButton",
        "CharaDetailMenuWindow.m_statusDisplay",
        "CharaDetailMenuWindow.m_transformSwitch",
        "CharaGrowResetWindow.m_cancelButton",
        "CharaGrowResetWindow.m_cautionText",
        "CharaGrowResetWindow.m_charaNameText",
        "CharaGrowResetWindow.m_confirmButton",
        "CharaGrowResetWindow.m_costDisplay",
        "CharaGrowResetWindow.m_itemList",
        "CharaGrowResetWindow.m_noReturnItemObj",
        "CharaGrowResetWindow.m_onlyResetLevelCautionText",
        "CharaGrowResetWindow.m_onlyResetLevelCheckBox",
        "CharaLevelUpWindow.IAGJADKFCBH",
        "CharaLevelUpWindow.m_autoSelectButton",
        "CharaLevelUpWindow.m_autoSelectText",
        "CharaLevelUpWindow.m_confirmButton",
        "CharaLevelUpWindow.m_confirmText",
        "CharaLevelUpWindow.m_currentColeText",
        "CharaLevelUpWindow.m_enhanceItemIcons",
        "CharaLevelUpWindow.m_levelupExpGauge",
        "CharaLevelUpWindow.m_previewColeText",
        "CharaRarityUpWindow.IAGJADKFCBH",
        "CharaRarityUpWindow.m_abilityElement",
        "CharaRarityUpWindow.m_afterRarity",
        "CharaRarityUpWindow.m_beforeRarity",
        "CharaRarityUpWindow.m_cancelButton",
        "CharaRarityUpWindow.m_confirmButton",
        "CharaRarityUpWindow.m_costIconList",
        "CharaRarityUpWindow.m_giftBonusElement",
        "CharaRarityUpWindow.m_layoutGroup",
        "CharaRarityUpWindow.m_skillElement",
        "CharaRarityUpWindow.m_statusElement",
        "CharaRarityUpWindow.m_supportAbilityElement",
        "CharaWindowBase.GKPNPGFLBKI",
        "CharaWindowBase.HOLDBBCFCLN",
        "CharaWindowBase._GMAMNACBDJI_k__BackingField",
        "CharaWindowBase.m_charaSwipeArea",
        "CharaWindowBase.m_leftChangeButton",
        "CharaWindowBase.m_rightChangeButton",
        "ContainerToolWindow.m_battleToolButton",
        "ContainerToolWindow.m_battleToolDisplay",
        "ContainerToolWindow.m_battleToolObj",
        "ContainerToolWindow.m_cancelButton",
        "ContainerToolWindow.m_equipmentToolButton1",
        "ContainerToolWindow.m_equipmentToolButton2",
        "ContainerToolWindow.m_equipmentToolButton3",
        "ContainerToolWindow.m_equipmentToolDisplay",
        "ContainerToolWindow.m_equipmentToolObj",
        "ContainerToolWindow.m_noneObj",
        "ContainerToolWindow.m_noneText",
        "ContainerToolWindow.m_okButton",
        "ContainerToolWindow.m_sortFilterControl",
        "ContainerToolWindow.m_toolList",
        "FixedSizeDialogueWindowBase.m_backgroundImage",
        "FixedSizeDialogueWindowBase.m_dialogAnimator",
        "FixedSizeDialogueWindowBase.m_titleText",
        "ResearchMenuWindow.FPPDHLABACF",
        "ResearchMenuWindow.m_achieveSwitch",
        "ResearchMenuWindow.m_animator",
        "ResearchMenuWindow.m_bulkResearchButton",
        "ResearchMenuWindow.m_costDisplay",
        "ResearchMenuWindow.m_effectListButton",
        "ResearchMenuWindow.m_executionButton",
        "ResearchMenuWindow.m_growResearchLock",
        "ResearchMenuWindow.m_researchDatailDisplay",
        "ResearchMenuWindow.m_tabList",
        "ResearchMenuWindow.m_uiBlurObject",
        "ShipEnhanceRecipeSelectWindow.CLKGINOLNEH",
        "ShipEnhanceRecipeSelectWindow.m_emptyObj",
        "ShipEnhanceRecipeSelectWindow.m_executeButton",
        "ShipEnhanceRecipeSelectWindow.m_shipPartDetailDisplay",
        "ShipEnhanceRecipeSelectWindow.m_shipPartsList",
        "ShipEnhanceRecipeSelectWindow.m_shipTabButton",
        "ShipEnhanceRecipeSelectWindow.m_shipToolTabButton",
        "SynthesisCombinationRankDisplay.m_alertObj",
        "SynthesisCombinationRankDisplay.m_applyButton",
        "SynthesisCombinationRankDisplay.m_character1Icon",
        "SynthesisCombinationRankDisplay.m_character2Icon",
        "SynthesisCombinationRankDisplay.m_ingredientIcon",
        "SynthesisCombinationRankDisplay.m_rankBaseSwitch",
        "SynthesisCombinationRankDisplay.m_rankText",
        "SynthesisCombinationRankDisplay.m_rentalObj",
        "SynthesisResultDialog.m_animator",
        "SynthesisResultDialog.m_buttonSwitch",
        "SynthesisResultDialog.m_cancelButton",
        "SynthesisResultDialog.m_easyCancelButton",
        "SynthesisResultDialog.m_itemLayoutGroup",
        "SynthesisResultDialog.m_retryButton",
        "SynthesisResultDialog.m_scrollView",
        "SynthesisResultDialog.m_stimulatorLayoutGroup",
        "SynthesisResultDialog.m_targetLayoutGroup",
        "SynthesisResultDialog.m_toolTypeText",
        "SynthesisResultWindow.m_itemIcons",
        "SynthesisResultWindow.m_itemLayoutGroup",
        "SynthesisResultWindow.m_nextButton",
        "SynthesisResultWindow.m_raritySwitch",
        "SynthesisResultWindow.m_retryButton",
        "SynthesisResultWindow.m_reversulEffectSwitch",
        "VariableSizeDialogueWindowBase._HasRaycastTarget_k__BackingField",
        "VariableSizeDialogueWindowBase._IsCanCheckText_k__BackingField",
        "VariableSizeDialogueWindowBase._IsThreeButton_k__BackingField",
        "VariableSizeDialogueWindowBase.m_dialogAnimator",
        "VariableSizeDialogueWindowBase.m_parts",
    };

    internal static bool IsCollection(Type type) =>
        type.FullName?.StartsWith("Il2CppSystem.Collections.", StringComparison.Ordinal) == true
        && !typeof(Il2CppSystem.ValueType).IsAssignableFrom(type);

    internal static bool IsServiceIdentity(Type type) =>
        type.FullName?.StartsWith("Api.", StringComparison.Ordinal) == true
        && type.FullName.EndsWith("Api", StringComparison.Ordinal);

    internal static void RequireSemanticOwner(Type type)
    {
        for (var parent = type; parent is not null; parent = parent.BaseType)
        {
            if (parent.FullName != "UnityEngine.Component")
                continue;
            if (IsResourceOwner(type.FullName, type.Module.ScopeName))
                return;
            if (
                type.Name
                is "BattleServerControl"
                    or "BattleStateVariables"
                    or "BattleDamageControl"
                    or "BattleEffectControl"
                    or "BattlePresenter"
                    or "BattleTimelineIcon"
                    or "BattleTimelineMoveControl"
                    or "BattleUnitConditionControl"
                    or "BattleUnitDamageControl"
                    or "BattleUnitTimelineControl"
                    or "BattleUnitMoveControl"
                    or "BattleCommandItemControl"
                    or "BattleCommandSkillControl"
                    or "BattleMoveControl"
                    or "BattleTimelineControl"
                    or "BattleTutorialControl"
                    or "ResearchMenuWindow"
                    or "ContainerToolWindow"
                    or "BattleToolSelectWindow"
                    or "CharaLevelUpWindow"
                    or "CharaRarityUpWindow"
                    or "CharaGrowResetWindow"
                    or "CharaDetailMenuWindow"
                    or "SynthesisResultWindow"
                    or "SynthesisResultDialog"
                    or "ShipEnhanceRecipeSelectWindow"
                    or "SynthesisCombinationRankDisplay"
            )
                return;
            throw new MissingMemberException(
                "Required controller has no concrete semantic projection: " + type.FullName
            );
        }
    }

    internal static bool IsResourceOwner(string? name, string? module) =>
        name == "UserDataManager" && module == "Blend.dll";

    internal static string? Exclusion(PropertyInfo property)
    {
        var owner = property.DeclaringType?.FullName ?? "";
        if (
            SceneExclusion(
                owner,
                property.Name,
                property.PropertyType.FullName ?? "",
                property.DeclaringType!.Module.ScopeName
            ) is
            { } scene
        )
            return scene;
        var field = owner + "." + property.Name;
        if (RenderFields.Contains(field) || OwnerRenderFields.Contains(field))
            return "render_or_callback_reference";
        if (
            owner
            is "UnityEngine.Object"
                or "UnityEngine.Component"
                or "UnityEngine.Behaviour"
                or "UnityEngine.MonoBehaviour"
        )
            return "unity_object_infrastructure";
        if (
            owner == "UIAssetLoader"
                && property.Name is "m_loadedPrefabTable" or "m_imageToAddressTable"
            || owner == "AssetLoader" && property.Name == "m_handlesCache"
        )
            return "render_resource_cache";
        var type = property.PropertyType.FullName ?? "";
        if (
            type.StartsWith("Il2CppSystem.Action", StringComparison.Ordinal)
            || type.StartsWith("Il2CppSystem.Func", StringComparison.Ordinal)
        )
            return "callback_implementation";
        if (type == "Il2CppSystem.Threading.CancellationToken")
            return "task_cancellation_handle";
        if (
            property.Name
                is "__t__builder"
                    or "__u__1"
                    or "__u__2"
                    or "__u__3"
                    or "__u__4"
                    or "__u__5"
            && typeof(Il2CppSystem.ValueType).IsAssignableFrom(property.DeclaringType)
            && property.DeclaringType.GetProperty("__1__state") is not null
        )
            return "async_task_infrastructure";
        return null;
    }
}

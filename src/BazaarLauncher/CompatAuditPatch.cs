using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Cards.Enchantments;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal;
using BazaarGameShared.Domain.Cards.Encounter.Pedestal.Behaviors;
using BazaarGameShared.Domain.Cards.Item;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Players;
using BazaarGameShared.Domain.Prerequisites.Conditionals;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Domain.Targeting;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 客户端反编译兼容审计加固补丁：
/// 针对新增/强化事件与断线续玩流程中客户端原生代码的潜在 NRE、KeyNotFoundException、
/// IndexOutOfRangeException 进行前置兜底保护，不改变原版正常表现。
/// </summary>
internal static partial class CompatAuditPatch
{
    private static bool _applied;
    private static int _patchedCount;
    private static int _suppressedSoundMoveCount;
    private static int _suppressedRevealBoundsCount;
    private static int _pedestalFastCompletedCount;
    private static int _rewardDespawnSkippedCount;
    private static int _pvpOpponentChangedFixedCount;
    private static int _pedestalPreviewFallbackCount;
    private static int _pedestalPresentationCleanedCount;
    private static int _encounterPortraitFallbackCount;
    private static int _encounterTooltipFallbackCount;
    private static volatile bool _pedestalCommitRequested;
    private static volatile bool _wasInUpgradePedestal;
    private static volatile bool _wasInAnyPedestal;
    private static MethodInfo _getCardControllerMethod;
    private static MethodInfo _bmShouldLoadPresMethod;
    private static MethodInfo _bmRemovePresMethod;
    private static MethodInfo _bmInvalidateLoadRequestsMethod;
    private static PropertyInfo _bmBoardStatePresProp;
    private static PropertyInfo _bmIsBoardStatePresLoadingProp;
    private static FieldInfo _bmLoadRequestVersionField;
    private static FieldInfo _bmPresRefField;
    private static MethodInfo _bspCleanUpCompleteMethod;
    private static MethodInfo _bspHasFeatureMethod;
    private static FieldInfo _bspCancellationTokenField;
    private static Type _bspType;
    private static readonly List<object> _pedestalFeatureValues = new();
    private static volatile bool _pendingPedestalExitAbort;
    private static volatile bool _pedestalVerifyNonPedestalExit;
    private static int _lastPedestalPresentationId;
    private static SynchronizationContext _unitySyncContext;
    private static int _mainThreadId;
    private static readonly object _pedestalPresLock = new();
    private static readonly HashSet<int> _pedestalPresentationIds = new();
    private static readonly HashSet<int> _forceCleanedPresentationIds = new();
    private static FieldInfo _pedestalAnimCompletedField;
    private static FieldInfo _pedestalTemplateField;
    private static FieldInfo _rewardHideCompleteField;
    private static PropertyInfo _rewardAnimatorProp;
    private static FieldInfo _rewardSoundHandlerField;
    private static PropertyInfo _dataSimPvpOpponentProp;
    private static MethodInfo _pvpOpponentChangedTriggerMethod;
    private static object _pvpOpponentChangedEventObj;
    private static MethodInfo _canCardUpgradeMethod;
    private static MethodInfo _exitUpgradePreviewAllMethod;
    private static MethodInfo _exitUpgradePreviewCardMethod;
    private static MethodInfo _enterUpgradePreviewMethod;
    private static MethodInfo _refreshFullTooltipMethod;
    private static PropertyInfo _cardTooltipControllerProp;
    private static MethodInfo _setDisplayModeMethod;
    private static MethodInfo _getDisplayModeMethod;
    private static ConstructorInfo _cardCondContextCtor;

    // ── #19 父事件肖像资产与本地化兜底缓存（子事件/EncounterStep 立绘与悬浮提示补全）──
    private static readonly object _encounterCacheLock = new();
    private static Guid _cachedParentEncounterTemplateId;
    private static string _cachedParentEncounterArtKey;
    private static object _cachedParentEncounterAsset;
    private static string _cachedParentEncounterTitle;
    private static string _cachedParentEncounterDesc;
    private static Type _encounterControllerType;
    private static Type _encounterAssetDataSoType;
    private static MethodInfo _encSetupEncounterControllerMethod;
    private static PropertyInfo _encCurrentEncounterDataObjectProp;
    private static PropertyInfo _encCardDataProp;
    private static MethodInfo _encInitTooltipMethod;
    private static FieldInfo _encCardTooltipDataField;
    private static FieldInfo _encIsInitializedField;
    private static MethodInfo _encShowCardMethod;
    private static PropertyInfo _encIsCardVisibleProp;
    private static PropertyInfo _encIsLockedToSocketProp;
    private static PropertyInfo _encBoxColliderProp;
    private static PropertyInfo _encColliderEnabledProp;
    private static FieldInfo _encCardVisualsField;
    private static MethodInfo _encServicesGetAssetLoaderMethod;
    private static MethodInfo _encAssetLoaderLoadMethod;
    private static MethodInfo _encDataGetStaticMethod;
    private static MethodInfo _encStaticGetCardByIdMethod;
    private static FieldInfo _bmEncounterAssetSoField;
    private static MethodInfo _bmUpdateEncounterVisMethod;
    private static MethodInfo _tooltipGetLocalizedTextMethod;
    private static MethodInfo _tooltipCtrlRenderPassiveMethod;
    private static FieldInfo _tooltipCtrlQuestDataField;

    private struct PvpOpponentSnapshot
    {
        public bool HadOpponent;
        public string AccountId;
        public string Hero;
    }

    internal static int PatchedCount => _patchedCount;

    internal static void NotifyPedestalCommitted() => _pedestalCommitRequested = true;

    internal static Dictionary<string, int> GetSuppressedStats() => new()
    {
        ["soundOnCardMoved"] = Volatile.Read(ref _suppressedSoundMoveCount),
        ["revealCardContainerBounds"] = Volatile.Read(ref _suppressedRevealBoundsCount),
        ["pedestalFastCompleted"] = Volatile.Read(ref _pedestalFastCompletedCount),
        ["rewardDespawnSkipped"] = Volatile.Read(ref _rewardDespawnSkippedCount),
        ["pvpOpponentChangedFixed"] = Volatile.Read(ref _pvpOpponentChangedFixedCount),
        ["pedestalPreviewFallback"] = Volatile.Read(ref _pedestalPreviewFallbackCount),
        ["pedestalPresentationCleaned"] = Volatile.Read(ref _pedestalPresentationCleanedCount),
        ["encounterPortraitFallback"] = Volatile.Read(ref _encounterPortraitFallbackCount),
        ["encounterTooltipFallback"] = Volatile.Read(ref _encounterTooltipFallbackCount),
    };

    internal static void ResetAppliedFlag()
    {
        _applied = false;
        _gameSimApplied = false;
        _pedestalHintsApplied = false;
        _encounterApplied = false;
        ClearRunScopedCaches("ResetAppliedFlag");
    }

    private static bool _gameSimApplied;
    private static bool _pedestalHintsApplied;
    private static bool _encounterApplied;

    /// <summary>切场景或对局结束时清理跨局动态标签缓存与遭遇资产引用，杜绝内存/显存泄漏（修复 P1-3 / P1-4）。</summary>
    internal static void ClearRunScopedCaches(string reason)
    {
        try
        {
            _enchDynamicSyncedTags.Clear();
            ClearParentEncounterCache(reason);
        }
        catch { }
    }

    internal static void ApplyGameSimGroup(Harmony harmony)
    {
        if (_gameSimApplied || harmony == null) return;
        _gameSimApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        PatchCombatSimHandler(harmony);
        PatchVfxManager(harmony);
        PatchGameSimHandler(harmony);
        PatchCardQuestGroupData(harmony);
        PatchHealthBarRewardController(harmony);
        PatchResumeAndEndOfRunControllers(harmony);
        PatchSoundEventListener(harmony);
        PatchCardRevealAnimationDriver(harmony);
        PatchDataApplySimPvpOpponent(harmony);
    }

    internal static void ApplyPedestalAndHintGroup(Harmony harmony)
    {
        if (_pedestalHintsApplied || harmony == null) return;
        _pedestalHintsApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        PatchPedestalState(harmony);
        PatchRewardController(harmony);
        PatchTargetingAndHints(harmony);
    }

    internal static void ApplyEncounterAndChallengeGroup(Harmony harmony)
    {
        if (_encounterApplied || harmony == null) return;
        _encounterApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        PatchEncounterController(harmony);
        PatchPrizePassChallengesGuard(harmony);
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;
        _patchedCount = 0;

        ApplyGameSimGroup(harmony);
        ApplyPedestalAndHintGroup(harmony);
        ApplyEncounterAndChallengeGroup(harmony);

        BazaarLauncherPlugin.Log?.LogInfo($"CompatAuditPatch 已安全安装 {_patchedCount} 项兼容防护");
    }

}

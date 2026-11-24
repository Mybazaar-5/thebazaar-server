using System;
using System.Collections;
using System.Collections.Concurrent;
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
using UnityEngine.SceneManagement;
using BazaarLauncher.Core;

namespace BazaarLauncher;

/// <summary>
/// 客户端反编译兼容审计加固补丁：
/// 针对新增/强化事件与断线续玩流程中客户端原生代码的潜在 NRE、KeyNotFoundException、
/// IndexOutOfRangeException 进行前置兜底保护，不改变原版正常表现。
/// </summary>
internal static partial class CompatAuditPatch
{
    private static bool _applied;
    private static int _gameSimPatchedCount;
    private static int _pedestalHintsPatchedCount;
    private static int _encounterPatchedCount;
    private static bool _sceneCleanupSubscribed;
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
    private static FieldInfo _bmOpponentBoardAnchorField;
    private static MethodInfo _bspCleanUpCompleteMethod;
    private static MethodInfo _bspHasFeatureMethod;
    private static FieldInfo _bspCancellationTokenField;
    private static MethodInfo _vsCustomEventTriggerMethod;
    private static Type _bspType;
    private static Type _cardSocketCtrlType;
    private static Type _cardCtrlBaseType;
    private static Type _socketEffectCtrlType;
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
    private static FieldInfo _rewardAnimatorField;
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
    // 修复 P1 跨重载炸弹：按参数类型分桶缓存 TooltipExtensions.GetLocalizedText(TLocalizableText / LocalizableText)
    private static readonly ConcurrentDictionary<Type, MethodInfo> _tooltipGetLocalizedTextByArgType = new();
    private static MethodInfo _tooltipCtrlRenderPassiveMethod;
    private static FieldInfo _tooltipCtrlQuestDataField;
    private static int _hour6TransitionUnblockedCount;
    private static int _hour6WatchdogRevealCount;
    // P2-15 (H-1~H-6)：客户端防御性兜底命中低频诊断计数（便于验证服务端修复后是否已归零）
    private static int _hackH3VfxTargetFallbackCount;
    private static int _hackH4TransformShellCount;
    private static int _hackH5OrphanSkillDroppedCount;
    private static long _gameSimVersion;

    private struct PvpOpponentSnapshot
    {
        public bool HadOpponent;
        public string AccountId;
        public string Hero;
        public int Day;
        public int Hour;
        public int MaxHealth;
        public string Name;
    }

    // 记录上次已成功触发 PvpOpponentChanged 时的跨天快照键，避免跨天同 accountId+hero 被误跳
    private static PvpOpponentSnapshot _lastTriggeredPvpSnapshot;

    internal static int PatchedCount
        => Volatile.Read(ref _gameSimPatchedCount)
           + Volatile.Read(ref _pedestalHintsPatchedCount)
           + Volatile.Read(ref _encounterPatchedCount);

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
        ["hour6TransitionUnblocked"] = Volatile.Read(ref _hour6TransitionUnblockedCount),
        ["hour6WatchdogReveal"] = Volatile.Read(ref _hour6WatchdogRevealCount),
        ["hackH1PvpOpponentChanged"] = Volatile.Read(ref _pvpOpponentChangedFixedCount),
        ["hackH2SoundOnCardMoved"] = Volatile.Read(ref _suppressedSoundMoveCount),
        ["hackH3VfxMissingTarget"] = Volatile.Read(ref _hackH3VfxTargetFallbackCount),
        ["hackH4TransformShellUpdate"] = Volatile.Read(ref _hackH4TransformShellCount),
        ["hackH5OrphanSkillOrHour6"] = Volatile.Read(ref _hackH5OrphanSkillDroppedCount)
            + Volatile.Read(ref _hour6TransitionUnblockedCount)
            + Volatile.Read(ref _hour6WatchdogRevealCount),
        ["hackH6EncounterFallback"] = Volatile.Read(ref _encounterPortraitFallbackCount)
            + Volatile.Read(ref _encounterTooltipFallbackCount),
        ["hackH7GoToNextStateFallback"] = CombatPatch.HackH7GoToNextStateFallbackCount,
        ["gameplayMsgFontFixed"] = Volatile.Read(ref _gameplayMsgFontFixedCount),
        ["layoutColliderGuard"] = Volatile.Read(ref _layoutColliderGuardCount),
        ["opponentCardsForceShown"] = Volatile.Read(ref _opponentCardsForceShownCount),
        ["resumeCardsRecovered"] = Volatile.Read(ref _resumeCardsRecoveredCount),
        ["resumeSkillsRecovered"] = Volatile.Read(ref _resumeSkillsRecoveredCount),
    };

    /// <summary>P2-15 (H-1~H-6)：低频记录客户端防御兜底命中诊断日志（第 1、5 次及每 25 次输出），便于验证服务端修完后零触发。</summary>
    private static void RecordCompatHackHit(string hackId, ref int counter, string detail)
    {
        int cnt = Interlocked.Increment(ref counter);
        if (cnt == 1 || cnt == 5 || cnt % 25 == 0)
        {
            BazaarLauncherPlugin.Log?.LogInfo($"[CompatDiag][{hackId}] defensive fallback hit (count={cnt}, detail={detail})");
        }
    }

    // P1-46：拆分为三组独立热启停复位方法，杜绝单模块禁用误清其余两个在运模块的标记
    internal static void ResetGameSimAppliedFlag()
    {
        _gameSimApplied = false;
        _applied = false;
        Interlocked.Exchange(ref _gameSimPatchedCount, 0);
        _lastTriggeredPvpSnapshot = default;
        Interlocked.Increment(ref _gameSimVersion);
        CancelPendingRunScopedWatchdogs();
        TryUnsubscribeSceneCleanupIfIdle();
    }

    internal static void ResetHintEnchantAppliedFlag()
    {
        _pedestalHintsApplied = false;
        _applied = false;
        Interlocked.Exchange(ref _pedestalHintsPatchedCount, 0);
        _enchDynamicSyncedTags.Clear();
        _cardTagSyncStamps.Clear();
        ResetPedestalAndDragRunScopedCaches();
        TryUnsubscribeSceneCleanupIfIdle();
    }

    internal static void ResetEncounterAssetAppliedFlag()
    {
        _encounterApplied = false;
        _applied = false;
        Interlocked.Exchange(ref _encounterPatchedCount, 0);
        ClearParentEncounterCache("ResetEncounterAssetAppliedFlag");
        ClearEndOfRunChallengeGuards();
        TryUnsubscribeSceneCleanupIfIdle();
    }

    internal static void ResetAppliedFlag()
    {
        ResetGameSimAppliedFlag();
        ResetHintEnchantAppliedFlag();
        ResetEncounterAssetAppliedFlag();
        ClearRunScopedCaches("ResetAppliedFlag");
    }

    private static bool _gameSimApplied;
    private static bool _pedestalHintsApplied;
    private static bool _encounterApplied;

    private static void EnsureSceneCleanupLifecycleHooked()
    {
        if (_sceneCleanupSubscribed) return;
        _sceneCleanupSubscribed = true;
        SceneManager.activeSceneChanged += OnCompatActiveSceneChanged;
    }

    private static void TryUnsubscribeSceneCleanupIfIdle()
    {
        if (!_sceneCleanupSubscribed) return;
        if (_gameSimApplied || _pedestalHintsApplied || _encounterApplied) return;
        _sceneCleanupSubscribed = false;
        SceneManager.activeSceneChanged -= OnCompatActiveSceneChanged;
    }

    private static void OnCompatActiveSceneChanged(Scene prev, Scene next)
    {
        ClearRunScopedCaches("ActiveSceneChanged:" + next.name);
    }

    /// <summary>切场景或对局结束/重启时清理跨局动态标签、祭坛/拖拽静态字段与遭遇资产引用，杜绝跨局脏态与内存泄漏（P1-43）。</summary>
    internal static void ClearRunScopedCaches(string reason)
    {
        try
        {
            _enchDynamicSyncedTags.Clear();
            _cardTagSyncStamps.Clear();
            _lastTriggeredPvpSnapshot = default;
            Interlocked.Increment(ref _gameSimVersion);
            CancelPendingRunScopedWatchdogs();
            ResetPedestalAndDragRunScopedCaches();
            ClearParentEncounterCache(reason);
            ClearEndOfRunChallengeGuards();
            ClearCardQuestSubscribedGroups(reason);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch:L279", diagEx); }
    }

    internal static void ApplyGameSimGroup(SafeHarmonyPatcher patcher)
    {
        if (_gameSimApplied || patcher == null) return;
        _gameSimApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;
        EnsureSceneCleanupLifecycleHooked();

        int before = patcher.SucceededCount;
        PatchCombatSimHandler(patcher);
        PatchVfxManager(patcher);
        PatchGameSimHandler(patcher);
        PatchSkillPresentationManager(patcher);
        PatchCardQuestGroupData(patcher);
        PatchHealthBarRewardController(patcher);
        PatchResumeAndEndOfRunControllers(patcher);
        PatchSoundEventListener(patcher);
        PatchCardRevealAnimationDriver(patcher);
        PatchDataApplySimPvpOpponent(patcher);
        PatchTransitionInHour6Guard(patcher);
        PatchUserFeedbackFixes(patcher);
        Interlocked.Exchange(ref _gameSimPatchedCount, Math.Max(0, patcher.SucceededCount - before));
    }

    internal static void ApplyPedestalAndHintGroup(SafeHarmonyPatcher patcher)
    {
        if (_pedestalHintsApplied || patcher == null) return;
        _pedestalHintsApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;
        EnsureSceneCleanupLifecycleHooked();

        int before = patcher.SucceededCount;
        PatchPedestalState(patcher);
        PatchRewardController(patcher);
        PatchTargetingAndHints(patcher);
        Interlocked.Exchange(ref _pedestalHintsPatchedCount, Math.Max(0, patcher.SucceededCount - before));
    }

    internal static void ApplyEncounterAndChallengeGroup(SafeHarmonyPatcher patcher)
    {
        if (_encounterApplied || patcher == null) return;
        _encounterApplied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;
        EnsureSceneCleanupLifecycleHooked();

        int before = patcher.SucceededCount;
        PatchEncounterController(patcher);
        PatchPrizePassChallengesGuard(patcher);
        PatchEndOfRunChallengesGuard(patcher);
        PatchLootPaginatedReroll(patcher);
        Interlocked.Exchange(ref _encounterPatchedCount, Math.Max(0, patcher.SucceededCount - before));
    }

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        ApplyGameSimGroup(patcher);
        ApplyPedestalAndHintGroup(patcher);
        ApplyEncounterAndChallengeGroup(patcher);

        BazaarLauncherPlugin.Log?.LogInfo($"CompatAuditPatch 已安全安装 {PatchedCount} 项兼容防护");
    }

}

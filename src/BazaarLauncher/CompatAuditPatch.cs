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
internal static class CompatAuditPatch
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

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;
        _patchedCount = 0;
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
        PatchPedestalState(harmony);
        PatchRewardController(harmony);
        PatchTargetingAndHints(harmony);
        PatchEncounterController(harmony);
        PatchPrizePassChallengesGuard(harmony);

        BazaarLauncherPlugin.Log?.LogInfo($"CompatAuditPatch applied {_patchedCount} safety guards");
    }

    // ── 1. CombatSimHandler：CardTransformed 防缺 Key & ExtractFrameEffects 兜底 ──

    private static void PatchCombatSimHandler(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.CombatSimHandler");
            if (t == null) return;

            var tryCache = AccessTools.Method(t, "TryCacheTransformedCard",
                new[] { typeof(CombatSimEventCardTransformed), typeof(CombatSimFrame) });
            if (tryCache != null)
            {
                harmony.Patch(tryCache, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(TryCacheTransformedCardPrefix))));
                _patchedCount++;
            }

            var processEvents = AccessTools.Method(t, "ProcessEvents",
                new[] { typeof(ICombatSimEvent), typeof(CombatSimFrame), typeof(List<string>) });
            if (processEvents != null)
            {
                harmony.Patch(processEvents, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(CombatProcessEventsPrefix))));
                _patchedCount++;
            }

            var extractEffects = AccessTools.Method(t, "ExtractFrameEffects",
                new[] { typeof(List<CombatSimFrame>), typeof(List<string>) });
            if (extractEffects != null)
            {
                harmony.Patch(extractEffects, finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(ExtractFrameEffectsFinalizer))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CombatSimHandler: " + ex.Message);
        }
    }

    private static bool TryCacheTransformedCardPrefix(CombatSimEventCardTransformed evt, CombatSimFrame frame)
    {
        if (evt == null || frame == null || evt.TransformedCards == null)
            return false;

        EnsureCombatFrameCardUpdates(evt, frame);
        return true;
    }

    private static bool CombatProcessEventsPrefix(ICombatSimEvent combatSimEvent, CombatSimFrame frame)
    {
        if (combatSimEvent is CombatSimEventCardTransformed evt)
        {
            if (evt.TransformedCards == null || frame == null)
                return false;

            EnsureCombatFrameCardUpdates(evt, frame);
        }
        return true;
    }

    private static void EnsureCombatFrameCardUpdates(CombatSimEventCardTransformed evt, CombatSimFrame frame)
    {
        if (frame.CardUpdates == null) return;

        if (!string.IsNullOrEmpty(evt.OriginalInstanceId))
        {
            var origId = new InstanceId(evt.OriginalInstanceId);
            if (!frame.CardUpdates.ContainsKey(origId))
            {
                frame.CardUpdates[origId] = new CombatSimCardUpdate { CardInstanceId = origId };
            }
        }

        if (evt.TransformedCards == null) return;
        foreach (var tc in evt.TransformedCards)
        {
            if (tc == null || string.IsNullOrEmpty(tc.InstanceId)) continue;
            var newId = new InstanceId(tc.InstanceId);
            if (!frame.CardUpdates.ContainsKey(newId))
            {
                frame.CardUpdates[newId] = new CombatSimCardUpdate { CardInstanceId = newId };
            }
        }
    }

    private static Exception ExtractFrameEffectsFinalizer(Exception __exception, ref long __result)
    {
        if (__exception != null)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch: suppressed ExtractFrameEffects error: " + __exception.Message);
            __result = 0L;
        }
        return null;
    }

    // ── 2. VFXManager.GenerateCombatVFX：属性映射动作 TargetCard 为空防 NRE ──

    private static void PatchVfxManager(Harmony harmony)
    {
        try
        {
            var vfxMgrType = AccessTools.TypeByName("TheBazaar.VFXManager");
            var evtType = AccessTools.TypeByName("TheBazaar.EffectTriggeredEvent");
            if (vfxMgrType == null || evtType == null) return;

            var genMethod = AccessTools.Method(vfxMgrType, "GenerateCombatVFX", new[] { evtType });
            if (genMethod != null)
            {
                harmony.Patch(genMethod, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(GenerateCombatVfxPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.VFXManager: " + ex.Message);
        }
    }

    private static bool GenerateCombatVfxPrefix(object __instance, object eventData)
    {
        try
        {
            if (__instance == null || eventData == null) return false;
            var data = eventData.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance)?.GetValue(eventData);
            if (data == null) return false;

            var vfxShouldPlayProp = data.GetType().GetProperty("VFXShouldPlay", BindingFlags.Public | BindingFlags.Instance);
            if (vfxShouldPlayProp != null && vfxShouldPlayProp.GetValue(data) is bool play && !play)
                return true;

            var vfxSoField = __instance.GetType().GetField("vfxManagerSO", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            var vfxSo = vfxSoField?.GetValue(__instance);
            if (vfxSo == null) return false;

            var actionType = data.GetType().GetProperty("ActionType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(data);
            var targetCardProp = data.GetType().GetProperty("TargetCard", BindingFlags.Public | BindingFlags.Instance);
            var targetCard = targetCardProp?.GetValue(data);

            var isMappedMethod = vfxSo.GetType().GetMethod("IsActionAttributeMapped", BindingFlags.Public | BindingFlags.Instance);
            if (isMappedMethod != null && actionType != null)
            {
                bool isMapped = (bool)isMappedMethod.Invoke(vfxSo, new[] { actionType });
                if (isMapped && targetCard == null)
                {
                    // VFXManager.cs:164 在 IsActionAttributeMapped 为真时直接访问 data.TargetCard.Size；
                    // 若缺少 TargetCard，则回退尝试用 SourceCard 填充 TargetInstanceId，仍为空则跳过特效避免 async void NRE。
                    var srcCard = data.GetType().GetProperty("SourceCard", BindingFlags.Public | BindingFlags.Instance)?.GetValue(data);
                    if (srcCard != null)
                    {
                        var srcIdProp = srcCard.GetType().GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance);
                        var tgtIdProp = data.GetType().GetProperty("TargetInstanceId", BindingFlags.Public | BindingFlags.Instance);
                        if (srcIdProp != null && tgtIdProp != null && tgtIdProp.CanWrite)
                        {
                            tgtIdProp.SetValue(data, srcIdProp.GetValue(srcCard));
                            if (targetCardProp?.GetValue(data) != null) return true;
                        }
                    }

                    BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch: skipped attribute-mapped VFX {actionType} due to null TargetCard");
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("GenerateCombatVfxPrefix: " + ex.Message);
        }
        return true;
    }

    // ── 3. GameSimHandler：Transform 缺 Key 防护 & SkillEquipped 实体判空 ──

    private static void PatchGameSimHandler(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.GameSimHandler");
            if (t == null) return;

            var mTrans = AccessTools.Method(t, "ProcessCardTransformedEvents");
            if (mTrans != null)
            {
                harmony.Patch(mTrans, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessCardTransformedEventsPrefix))));
                _patchedCount++;
            }

            var mRev = AccessTools.Method(t, "ProcessTransformRevertedEvents");
            if (mRev != null)
            {
                harmony.Patch(mRev, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessTransformRevertedEventsPrefix))));
                _patchedCount++;
            }

            var mProc = AccessTools.Method(t, "ProcessEvents", new[] { typeof(IList<IGameSimEvent>) });
            if (mProc != null)
            {
                harmony.Patch(mProc, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(GameSimProcessEventsPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.GameSimHandler: " + ex.Message);
        }
    }

    private static void ProcessCardTransformedEventsPrefix(
        IEnumerable<GameSimEventCardTransformed> events,
        ref Dictionary<string, SimUpdateCard> simUpdates)
    {
        simUpdates ??= new Dictionary<string, SimUpdateCard>();
        if (events == null) return;
        foreach (var ev in events)
        {
            if (ev?.TransformedCards == null) continue;
            foreach (var tc in ev.TransformedCards)
            {
                if (tc == null || string.IsNullOrEmpty(tc.InstanceId)) continue;
                if (!simUpdates.ContainsKey(tc.InstanceId))
                {
                    simUpdates[tc.InstanceId] = new SimUpdateCard { InstanceId = tc.InstanceId };
                }
            }
        }
    }

    private static void ProcessTransformRevertedEventsPrefix(
        IEnumerable<GameSimEventCardTransformReverted> events,
        ref Dictionary<string, SimUpdateCard> simUpdates)
    {
        simUpdates ??= new Dictionary<string, SimUpdateCard>();
        if (events == null) return;
        foreach (var ev in events)
        {
            if (ev?.OriginalCard == null || string.IsNullOrEmpty(ev.OriginalCard.InstanceId)) continue;
            if (!simUpdates.ContainsKey(ev.OriginalCard.InstanceId))
            {
                simUpdates[ev.OriginalCard.InstanceId] = new SimUpdateCard { InstanceId = ev.OriginalCard.InstanceId };
            }
        }
    }

    private static void GameSimProcessEventsPrefix(IList<IGameSimEvent> events)
    {
        if (events == null || events.Count == 0) return;

        try
        {
            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            var entities = dataType?.GetField("Entities", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as IDictionary;
            if (entities == null) return;

            var spawnedInBatch = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < events.Count; i++)
            {
                if (events[i] is GameSimEventCardSpawned sp && !string.IsNullOrEmpty(sp.InstanceId))
                    spawnedInBatch.Add(sp.InstanceId);
                else if (events[i] is GameSimEventCardTransformed tr && tr.TransformedCards != null)
                {
                    foreach (var tc in tr.TransformedCards)
                    {
                        if (!string.IsNullOrEmpty(tc?.InstanceId)) spawnedInBatch.Add(tc.InstanceId);
                    }
                }
            }

            // GameSimHandler.cs:451 在处理 GameSimEventPlayerSkillEquipped 时未判空 TryGetValue 输出，
            // 若遇到孤立无对应实体的事件会直接 NRE 并打断后续 CardDealt 等关键事件。
            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i] is GameSimEventStateTransitioned stEv)
                {
                    if (stEv.ToState is ERunState.Choice or ERunState.Combat or ERunState.PVPCombat or ERunState.EndRunVictory or ERunState.EndRunDefeat
                        || string.IsNullOrEmpty(stEv.CurrentEncounterId))
                    {
                        ClearParentEncounterCache("GameSimTransition:" + stEv.ToState);
                    }
                }
                else if (events[i] is GameSimEventPlayerSkillEquipped eq)
                {
                    string id = eq.InstanceId;
                    if (string.IsNullOrEmpty(id))
                    {
                        events.RemoveAt(i);
                        continue;
                    }
                    bool inEntities = entities.Contains(new InstanceId(id));
                    if (!inEntities && !spawnedInBatch.Contains(id))
                    {
                        BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch: dropped orphan PlayerSkillEquipped ({id}) to prevent GameSimHandler NRE");
                        events.RemoveAt(i);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("GameSimProcessEventsPrefix: " + ex.Message);
        }
    }

    // ── 4. CardQuestGroupData：任务条目越界与实例空安全 ──

    private static void PatchCardQuestGroupData(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.CardQuestGroupData");
            if (t == null) return;

            var onComplete = AccessTools.Method(t, "OnQuestCompleted", new[] { typeof(int) });
            if (onComplete != null)
            {
                harmony.Patch(onComplete, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(OnQuestCompletedPrefix))));
                _patchedCount++;
            }

            foreach (var name in new[]
            {
                "OnCombatSimQuestProgressChanged",
                "OnCombatSimQuestCompleted",
                "OnGameSimQuestProgressChanged",
                "OnGameSimQuestCompleted",
            })
            {
                var m = AccessTools.Method(t, name);
                if (m != null)
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(QuestMessagePrefix))));
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardQuestGroupData: " + ex.Message);
        }
    }

    private static bool OnQuestCompletedPrefix(object __instance, int entryId)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return false;
            var cardInst = __instance.GetType().GetField("_cardInstance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
            var entries = __instance.GetType().GetField("Entries", BindingFlags.Public | BindingFlags.Instance)?.GetValue(__instance) as IList;
            if (cardInst == null || entries == null || entryId < 0 || entryId >= entries.Count)
            {
                BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch: ignored out-of-range QuestCompleted entryId={entryId}");
                return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool QuestMessagePrefix(object __instance, object __0)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __0 == null) return false;
            var cardInst = __instance.GetType().GetField("_cardInstance", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
            return cardInst != null;
        }
        catch
        {
            return false;
        }
    }

    // ── 5. HealthBarRewardController：野怪掉币/经验阈值查表空安全 ──

    private static void PatchHealthBarRewardController(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.HealthBarRewardController");
            if (t == null) return;

            var calc = AccessTools.Method(t, "CalculateRewardReceived", new[] { typeof(float) });
            if (calc != null)
            {
                harmony.Patch(calc, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(CalculateRewardReceivedPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.HealthBarRewardController: " + ex.Message);
        }
    }

    private static bool CalculateRewardReceivedPrefix(ref object __result)
    {
        try
        {
            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            var run = dataType?.GetProperty("Run", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var opp = run?.GetType().GetProperty("Opponent", BindingFlags.Public | BindingFlags.Instance)?.GetValue(run);
            var attrs = opp?.GetType().GetProperty("Attributes", BindingFlags.Public | BindingFlags.Instance)?.GetValue(opp) as IDictionary;
            if (attrs == null || !attrs.Contains(EPlayerAttributeType.HealthMax))
            {
                __result = null;
                return false;
            }
        }
        catch
        {
            __result = null;
            return false;
        }
        return true;
    }

    // ── 6. ResumeVisualController / MainMenuBaseState / EndOfRunRatingController：空 TrackRewards 与 UI 重叠安全兜底 ──

    private static void PatchResumeAndEndOfRunControllers(Harmony harmony)
    {
        try
        {
            var resumeType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.ResumeVisualController");
            var initMethod = resumeType == null ? null : AccessTools.Method(resumeType, "Init");
            if (initMethod != null)
            {
                harmony.Patch(initMethod,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ResumeVisualInitPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ResumeVisualInitFinalizer))));
                _patchedCount++;
            }

            var menuBaseType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.MainMenuBaseState");
            var onEnterMethod = menuBaseType == null ? null : AccessTools.Method(menuBaseType, "OnEnter");
            if (onEnterMethod != null)
            {
                harmony.Patch(onEnterMethod,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterFinalizer))));
                _patchedCount++;
            }

            var refreshMethod = menuBaseType == null ? null : AccessTools.Method(menuBaseType, "Refresh");
            if (refreshMethod != null && !refreshMethod.IsAbstract)
            {
                try
                {
                    harmony.Patch(refreshMethod,
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix))));
                    _patchedCount++;
                }
                catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.MainMenuBaseState.Refresh: " + ex.Message); }
            }

            foreach (var subStateName in new[]
            {
                "TheBazaar.UI.MainMenu.MainMenuUnrankedState",
                "TheBazaar.UI.MainMenu.MainMenuRankedState",
                "TheBazaar.UI.MainMenu.MainMenuTournamentState",
            })
            {
                var subT = AccessTools.TypeByName(subStateName);
                var hm = subT == null ? null : AccessTools.Method(subT, "HandleResumeStateVisuals");
                if (hm != null && !hm.IsAbstract)
                {
                    try
                    {
                        harmony.Patch(hm,
                            postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix))));
                        _patchedCount++;
                    }
                    catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.{subStateName}: " + ex.Message); }
                }
            }

            var runCfgCacheType = AccessTools.TypeByName("TheBazaar.RunConfigurationCache");
            if (runCfgCacheType != null)
            {
                foreach (var rcMethodName in new[] { "Update", "UpdateFromHttpClient" })
                {
                    var rcM = AccessTools.Method(runCfgCacheType, rcMethodName);
                    if (rcM != null && !rcM.IsAbstract)
                    {
                        try
                        {
                            harmony.Patch(rcM,
                                postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(OnRunConfigCacheUpdatedPostfix))));
                            _patchedCount++;
                        }
                        catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.RunConfigurationCache.{rcMethodName}: " + ex.Message); }
                    }
                }
            }

            var winCounterType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentWinCounterController");
            var refreshScore = winCounterType == null ? null : AccessTools.Method(winCounterType, "RefreshScore");
            if (refreshScore != null)
            {
                harmony.Patch(refreshScore,
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer))));
                _patchedCount++;
            }

            var eorType = AccessTools.TypeByName("TheBazaar.UI.EndOfRun.EndOfRunRatingController");
            var cfgRewards = eorType == null ? null : AccessTools.Method(eorType, "ConfigureRewards");
            if (cfgRewards != null)
            {
                harmony.Patch(cfgRewards, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(ConfigureRewardsPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ResumeAndEndOfRun: " + ex.Message);
        }
    }

    private static bool ResumeVisualInitPrefix(object __instance)
    {
        try
        {
            if (__instance == null) return false;
            EnsureTrackRewardsInitialized();

            var t = __instance.GetType();
            var prestigeBar = t.GetField("_prestigeBar", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) as Component;
            var victoryBar = t.GetField("_victoryBar", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance) as Component;
            var trophyImg = t.GetField("_trophyImage", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);

            if (prestigeBar != null) prestigeBar.gameObject.SetActive(false);

            var dataType = AccessTools.TypeByName("TheBazaar.Data");
            bool hasActiveRun = dataType?.GetProperty("HasActiveRun", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool har && har;
            if (!hasActiveRun)
            {
                if (victoryBar != null) victoryBar.gameObject.SetActive(false);
                HideTrophyImage(trophyImg);
                if (__instance is Component selfComp) selfComp.gameObject.SetActive(false);
                return false;
            }

            var cfgType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var runCfg = cfgType?.GetField("RunConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            var runCfgVal = runCfg?.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(runCfg);
            var runType = runCfgVal?.GetType().GetProperty("RunType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(runCfgVal)
                          ?? runCfgVal?.GetType().GetField("RunType", BindingFlags.Public | BindingFlags.Instance)?.GetValue(runCfgVal);
            bool isRanked = runType != null && string.Equals(runType.ToString(), "Ranked", StringComparison.OrdinalIgnoreCase);

            var spritesField = t.GetField(isRanked ? "_trophySpritesRanked" : "_trophySpritesUnranked",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var sprites = spritesField?.GetValue(__instance) as Array;
            if (sprites == null || sprites.Length == 0)
            {
                // 即使缺少奖杯精灵图，也先隐藏白色默认方块并安全初始化胜场进度条（避免 0-0 + 白块残留，Bug #9）
                HideTrophyImage(trophyImg);
                if (victoryBar != null && runCfgVal != null)
                {
                    var vicObj = runCfgVal.GetType().GetField("NumberOfVictories", BindingFlags.Public | BindingFlags.Instance)?.GetValue(runCfgVal)
                                 ?? runCfgVal.GetType().GetProperty("NumberOfVictories", BindingFlags.Public | BindingFlags.Instance)?.GetValue(runCfgVal);
                    uint victories = vicObj is int vi ? (uint)Math.Max(0, vi) : 0u;
                    var vInit = victoryBar.GetType().GetMethod("Init", BindingFlags.Public | BindingFlags.Instance);
                    vInit?.Invoke(victoryBar, new object[] { 10u, victories });
                    victoryBar.gameObject.SetActive(true);
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ResumeVisualInitPrefix: " + ex.Message);
        }
        return true;
    }

    private static Exception ResumeVisualInitFinalizer(object __instance, Exception __exception)
    {
        if (__exception != null)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ResumeVisualController.Init guarded: " + __exception.Message);
            try
            {
                if (__instance != null)
                {
                    var t = __instance.GetType();
                    var trophyImg = t.GetField("_trophyImage", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(__instance);
                    HideTrophyImage(trophyImg);
                    if (__instance is Component c) c.gameObject.SetActive(false);
                }
            }
            catch { }
        }
        return null;
    }

    private static void HideTrophyImage(object trophyImg)
    {
        if (trophyImg == null) return;
        try
        {
            trophyImg.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance)?.SetValue(trophyImg, false);
            if (trophyImg is Component c) c.gameObject.SetActive(false);
        }
        catch { }
    }

    private static void MainMenuBaseStateOnEnterPostfix(object __instance)
    {
        try
        {
            ClearParentEncounterCache("MainMenuBaseState.OnEnter");
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            SyncMainMenuResumeAndPlaymodeVisibility(__instance);
        }
        catch { }
    }

    private static Exception MainMenuBaseStateOnEnterFinalizer(object __instance, Exception __exception)
    {
        try
        {
            if (__exception != null)
                BazaarLauncherPlugin.Log?.LogWarning("MainMenuBaseState.OnEnter guarded: " + __exception.Message);
            if (__instance != null && !(__instance is UnityEngine.Object uo && uo == null))
            {
                SyncMainMenuResumeAndPlaymodeVisibility(__instance);
            }
        }
        catch { }
        return null;
    }

    private static void SyncMainMenuResumeAndPlaymodeVisibility(object mainMenuState)
    {
        try
        {
            if (mainMenuState == null) return;
            var view = mainMenuState.GetType().GetProperty("View", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mainMenuState);
            ApplyMainMenuViewVisibility(view);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("SyncMainMenuResumeAndPlaymodeVisibility: " + ex.Message);
        }
    }

    private static void OnRunConfigCacheUpdatedPostfix() => RefreshActiveMainMenuUiFromData();

    /// <summary>
    /// 当主大厅正在显示时（如放弃对局、deleteactivesave 或 RunConfig 刷新后），同步切换恢复条与三模式入口可见性。
    /// </summary>
    internal static void RefreshActiveMainMenuUiFromData()
    {
        try
        {
            string scene = GameInspector.GetActiveSceneName();
            if (!GameInspector.IsMainMenuSceneName(scene)) return;

            var viewType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.MainMenuBaseView");
            if (viewType == null) return;

            var views = UnityEngine.Object.FindObjectsByType(viewType, FindObjectsSortMode.None);
            if (views == null) return;
            foreach (var v in views)
            {
                if (v is Component comp && comp.gameObject.activeInHierarchy)
                {
                    ApplyMainMenuViewVisibility(v, refreshText: true);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("RefreshActiveMainMenuUiFromData: " + ex.Message);
        }
    }

    private static void ApplyMainMenuViewVisibility(object view, bool refreshText = false)
    {
        if (view == null) return;
        const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var dataType = AccessTools.TypeByName("TheBazaar.Data");
        bool hasActiveRun = dataType?.GetProperty("HasActiveRun", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is bool har && har;

        var viewType = view.GetType();
        viewType.GetMethod("ShowResumeView", anyInst)?.Invoke(view, new object[] { hasActiveRun });
        viewType.GetMethod("ShowPlaymodeOptions", anyInst)?.Invoke(view, new object[] { !hasActiveRun });
        viewType.GetMethod("ShowLoadoutButton", anyInst)?.Invoke(view, new object[] { !hasActiveRun });
        viewType.GetMethod("ShowHeroSelectButton", anyInst)?.Invoke(view, new object[] { !hasActiveRun });

        if (!hasActiveRun)
        {
            var resumeCtrl = viewType.GetField("_resumeStateController", anyInst)?.GetValue(view);
            if (resumeCtrl != null)
            {
                var trophyImg = resumeCtrl.GetType().GetField("_trophyImage", anyInst)?.GetValue(resumeCtrl);
                HideTrophyImage(trophyImg);
                if (resumeCtrl is Component rcComp) rcComp.gameObject.SetActive(false);
            }
        }

        if (refreshText)
        {
            viewType.GetMethod("UpdatePlaymodeSelection", anyInst)?.Invoke(view, null);
            viewType.GetMethod("UpdatePlayButtonText", anyInst)?.Invoke(view, null);
        }
    }

    private static Exception SwallowUiFinalizer(Exception __exception)
    {
        if (__exception != null)
            BazaarLauncherPlugin.Log?.LogWarning("UI controller exception guarded: " + __exception.Message);
        return null;
    }

    private static bool ConfigureRewardsPrefix(object __instance, out int chestRewardCount)
    {
        chestRewardCount = 0;
        try
        {
            EnsureTrackRewardsInitialized();
            var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
            var tr = cacheType?.GetField("TrackRewards", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            bool hasData = tr?.GetType().GetProperty("HasData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tr) is bool b && b;
            if (!hasData)
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("ConfigureRewardsPrefix: " + ex.Message);
            return false;
        }
        return true;
    }

    internal static void EnsureTrackRewardsInitialized()
    {
        var cacheType = AccessTools.TypeByName("TheBazaar.ClientCache");
        var tr = cacheType?.GetField("TrackRewards", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        if (tr == null) return;

        bool hasData = tr.GetType().GetProperty("HasData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tr) is bool b && b;
        var val = tr.GetType().GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tr);

        bool needInit = !hasData || val == null;
        if (!needInit)
        {
            var casual = val.GetType().GetField("CasualTrackRewards", BindingFlags.Public | BindingFlags.Instance)?.GetValue(val) as ICollection;
            var ranked = val.GetType().GetField("RankedTrackRewards", BindingFlags.Public | BindingFlags.Instance)?.GetValue(val) as ICollection;
            if (casual == null || ranked == null || casual.Count == 0 || ranked.Count == 0) needInit = true;
        }

        if (needInit)
        {
            var respType = AccessTools.TypeByName("BazaarGameShared.TempoNet.Models.RunRewardLevelResponse")
                           ?? AccessTools.TypeByName("BazaarGameShared.TempoNet.Responses.RunRewardLevelResponse");
            var curType = AccessTools.TypeByName("BazaarGameShared.TempoNet.Models.CurrencyValue");
            var updateM = tr.GetType().GetMethod("Update", BindingFlags.Public | BindingFlags.Instance);
            if (respType != null && updateM != null)
            {
                int[] thresholds = { 4, 7, 10 };
                var arr = Array.CreateInstance(respType, thresholds.Length);
                var emptyCur = curType != null ? Array.CreateInstance(curType, 0) : null;
                for (int i = 0; i < thresholds.Length; i++)
                {
                    object item = Activator.CreateInstance(respType);
                    respType.GetProperty("WinThreshold")?.SetValue(item, thresholds[i]);
                    respType.GetProperty("UnrankedChestCount")?.SetValue(item, 1);
                    respType.GetProperty("RankedChestCount")?.SetValue(item, i + 1);
                    respType.GetProperty("SubscriptionMultiplier")?.SetValue(item, 2);
                    if (emptyCur != null)
                    {
                        respType.GetProperty("UnrankedCurrencyRewards")?.SetValue(item, emptyCur);
                        respType.GetProperty("RankedCurrencyRewards")?.SetValue(item, emptyCur);
                    }
                    arr.SetValue(item, i);
                }
                updateM.Invoke(tr, new object[] { arr });
            }
        }
    }

    // ── 7. SoundEventListener.OnCardMoved：Controller 尚未生成时防 NRE ─────────

    private static void PatchSoundEventListener(Harmony harmony)
    {
        try
        {
            var soundType = GameInspector.FindTypeSafe("TheBazaar.SoundEventListener");
            if (soundType == null) return;

            var itemCardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");
            var onMoved = itemCardType != null
                ? AccessTools.Method(soundType, "OnCardMoved", new[] { itemCardType })
                : null;
            onMoved ??= AccessTools.Method(soundType, "OnCardMoved");

            if (onMoved != null)
            {
                harmony.Patch(onMoved,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(OnCardMovedPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(OnCardMovedFinalizer))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SoundEventListener: " + ex.Message);
        }
    }

    private static bool OnCardMovedPrefix(object __instance, object itemCard)
    {
        try
        {
            if (__instance == null || itemCard == null)
            {
                Interlocked.Increment(ref _suppressedSoundMoveCount);
                return false;
            }

            var soundMgr = __instance.GetType()
                .GetField("_soundManager", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(__instance);
            if (soundMgr == null || GameInspector.GetMember(soundMgr, "SFXPlayer") == null)
            {
                Interlocked.Increment(ref _suppressedSoundMoveCount);
                return false;
            }

            var iid = GameInspector.GetMember(itemCard, "InstanceId");
            var entities = GameInspector.GetDataEntities();
            var lookup = GameInspector.GetDataLookup();
            if (iid == null || entities == null || lookup == null || !entities.Contains(iid))
            {
                Interlocked.Increment(ref _suppressedSoundMoveCount);
                return false;
            }

            var entity = entities[iid];
            if (entity == null)
            {
                Interlocked.Increment(ref _suppressedSoundMoveCount);
                return false;
            }

            _getCardControllerMethod ??= lookup.GetType().GetMethod(
                "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null) ||
                (ctrl is Component comp && comp.gameObject == null))
            {
                int cnt = Interlocked.Increment(ref _suppressedSoundMoveCount);
                if (cnt <= 3 || cnt % 25 == 0)
                {
                    BazaarLauncherPlugin.Log?.LogInfo(
                        $"CompatAuditPatch: skipped SoundEventListener.OnCardMoved due to missing CardController (count={cnt})");
                }
                return false;
            }
        }
        catch (Exception ex)
        {
            int cnt = Interlocked.Increment(ref _suppressedSoundMoveCount);
            if (cnt <= 3 || cnt % 25 == 0)
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch: OnCardMovedPrefix fallback skip (count={cnt}): {ex.Message}");
            }
            return false;
        }
        return true;
    }

    private static Exception OnCardMovedFinalizer(Exception __exception)
    {
        if (__exception != null)
        {
            int cnt = Interlocked.Increment(ref _suppressedSoundMoveCount);
            if (cnt <= 3 || cnt % 25 == 0)
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch: suppressed SoundEventListener.OnCardMoved {__exception.GetType().Name} (count={cnt}): {__exception.Message}");
            }
        }
        return null;
    }

    // ── 8. BaseCardRevealAnimationDriver.GetCardContainerBounds：空集合防越界 ──

    private static void PatchCardRevealAnimationDriver(Harmony harmony)
    {
        try
        {
            var t = GameInspector.FindTypeSafe("TheBazaar.Game.Cards.Animation.BaseCardRevealAnimationDriver");
            if (t == null) return;

            var m = AccessTools.Method(t, "GetCardContainerBounds", new[] { typeof(List<Bounds>) })
                    ?? AccessTools.Method(t, "GetCardContainerBounds");
            if (m != null)
            {
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(GetCardContainerBoundsPrefix))),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(GetCardContainerBoundsFinalizer))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BaseCardRevealAnimationDriver: " + ex.Message);
        }
    }

    private static bool GetCardContainerBoundsPrefix(List<Bounds> bounds, ref Bounds __result)
    {
        if (bounds == null || bounds.Count == 0)
        {
            int cnt = Interlocked.Increment(ref _suppressedRevealBoundsCount);
            if (cnt <= 3 || cnt % 25 == 0)
            {
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch: skipped empty BaseCardRevealAnimationDriver.GetCardContainerBounds (count={cnt})");
            }
            __result = new Bounds(Vector3.zero, Vector3.zero);
            return false;
        }
        return true;
    }

    private static Exception GetCardContainerBoundsFinalizer(Exception __exception, ref Bounds __result)
    {
        if (__exception != null)
        {
            int cnt = Interlocked.Increment(ref _suppressedRevealBoundsCount);
            if (cnt <= 3 || cnt % 25 == 0)
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch: suppressed BaseCardRevealAnimationDriver.GetCardContainerBounds {__exception.GetType().Name} (count={cnt}): {__exception.Message}");
            }
            __result = new Bounds(Vector3.zero, Vector3.zero);
        }
        return null;
    }

    // ── 9. Data.ApplySimPvpOpponent：首次设置对手或切换英雄时补发 PvpOpponentChanged ──

    private static void PatchDataApplySimPvpOpponent(Harmony harmony)
    {
        try
        {
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType == null) return;

            _dataSimPvpOpponentProp = dataType.GetProperty("SimPvpOpponent", BindingFlags.Public | BindingFlags.Static);
            var applyM = AccessTools.Method(dataType, "ApplySimPvpOpponent");
            if (applyM != null)
            {
                harmony.Patch(applyM,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ApplySimPvpOpponentPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(ApplySimPvpOpponentPostfix))));
                _patchedCount++;
            }

            var eventsType = GameInspector.FindTypeSafe("TheBazaar.Events");
            var evtField = eventsType?.GetField("PvpOpponentChanged", BindingFlags.Public | BindingFlags.Static);
            _pvpOpponentChangedEventObj = evtField?.GetValue(null);
            if (_pvpOpponentChangedEventObj != null)
            {
                _pvpOpponentChangedTriggerMethod = _pvpOpponentChangedEventObj.GetType()
                    .GetMethod("Trigger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.DataApplySimPvpOpponent: " + ex.Message);
        }
    }

    private static string ReadPvpAccountId(object simPvpOpponent)
    {
        if (simPvpOpponent == null) return null;
        var loadout = GameInspector.GetMember(simPvpOpponent, "PlayerLoadout");
        return loadout == null ? null : GameInspector.GetMember(loadout, "accountId") as string;
    }

    private static void ApplySimPvpOpponentPrefix(out PvpOpponentSnapshot __state)
    {
        __state = default;
        try
        {
            var cur = _dataSimPvpOpponentProp?.GetValue(null);
            __state.HadOpponent = cur != null;
            __state.AccountId = ReadPvpAccountId(cur);
            __state.Hero = cur != null ? GameInspector.GetMember(cur, "Hero")?.ToString() : null;
        }
        catch { }
    }

    private static void ApplySimPvpOpponentPostfix(PvpOpponentSnapshot __state)
    {
        try
        {
            var cur = _dataSimPvpOpponentProp?.GetValue(null);
            if (cur == null) return;

            string newAccountId = ReadPvpAccountId(cur);
            string newHero = GameInspector.GetMember(cur, "Hero")?.ToString();

            // 原版 Data.cs:297 仅在前后 accountId 均非空且不相等时触发；此处避免重复触发
            bool nativeTriggered = !string.IsNullOrEmpty(__state.AccountId)
                && !string.IsNullOrEmpty(newAccountId)
                && !string.Equals(__state.AccountId, newAccountId, StringComparison.OrdinalIgnoreCase);
            if (nativeTriggered) return;

            bool firstValid = !__state.HadOpponent && (!string.IsNullOrEmpty(newAccountId) || !string.IsNullOrEmpty(newHero));
            bool becameNonEmptyAccount = string.IsNullOrEmpty(__state.AccountId) && !string.IsNullOrEmpty(newAccountId);
            bool heroChanged = !string.IsNullOrEmpty(newHero)
                && !string.Equals(__state.Hero, newHero, StringComparison.OrdinalIgnoreCase);

            if (!firstValid && !becameNonEmptyAccount && !heroChanged)
                return;

            if (_pvpOpponentChangedEventObj == null || _pvpOpponentChangedTriggerMethod == null)
            {
                var eventsType = GameInspector.FindTypeSafe("TheBazaar.Events");
                _pvpOpponentChangedEventObj ??= eventsType?.GetField("PvpOpponentChanged", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _pvpOpponentChangedTriggerMethod ??= _pvpOpponentChangedEventObj?.GetType()
                    .GetMethod("Trigger", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            if (_pvpOpponentChangedEventObj != null && _pvpOpponentChangedTriggerMethod != null)
            {
                var ps = _pvpOpponentChangedTriggerMethod.GetParameters();
                _pvpOpponentChangedTriggerMethod.Invoke(_pvpOpponentChangedEventObj, ps.Length == 0 ? null : new object[] { null });
                int cnt = Interlocked.Increment(ref _pvpOpponentChangedFixedCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch: triggered missing Events.PvpOpponentChanged (count={cnt}, hero={newHero}, accountId={newAccountId})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ApplySimPvpOpponentPostfix: " + ex.Message);
        }
    }

    // ── 10. PedestalState：升阶/附魔即时刷新 UI、悬停预览双保险与退出恢复 ─────

    private static void PatchPedestalState(Harmony harmony)
    {
        try
        {
            var pedType = GameInspector.FindTypeSafe("TheBazaar.PedestalState");
            if (pedType == null) return;

            _pedestalAnimCompletedField = pedType.GetField("_animationCompleted", BindingFlags.NonPublic | BindingFlags.Instance);
            _pedestalTemplateField = pedType.GetField("_pedestalTemplate", BindingFlags.NonPublic | BindingFlags.Instance);

            var onEnter = AccessTools.Method(pedType, "OnEnter");
            if (onEnter != null)
            {
                harmony.Patch(onEnter, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalOnEnterPostfix))));
                _patchedCount++;
            }

            var mUpd = AccessTools.Method(pedType, "HandleCardUpgradedEvent", new[] { typeof(NetMessageGameSim) });
            if (mUpd != null)
            {
                harmony.Patch(mUpd,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradePrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix))));
                _patchedCount++;
            }

            var mEnch = AccessTools.Method(pedType, "HandleCardEnchantmentEvent", new[] { typeof(NetMessageGameSim) });
            if (mEnch != null)
            {
                harmony.Patch(mEnch,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalEnchantPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PedestalUpgradeOrEnchantPostfix))));
                _patchedCount++;
            }

            // 升级台悬停预览双保险 1：兼容复合 SelectionCriteria (如 Partner Portal - Forja)
            var cardExtType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
            var canPedUpg = cardExtType == null ? null : AccessTools.Method(cardExtType, "CanUseTPedestalBehaviorUpgrade");
            if (canPedUpg != null)
            {
                harmony.Patch(canPedUpg, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(CanUsePedestalUpgradePostfix))));
                _patchedCount++;
            }

            // 升级台悬停预览双保险 2：CurrentEncounterController 未及时就绪或复合条件时兜底
            var tipParentType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.TooltipParentComponent");
            var isUpgPedState = tipParentType == null ? null : AccessTools.Method(tipParentType, "IsUpgradePedestalState");
            if (isUpgPedState != null)
            {
                harmony.Patch(isUpgPedState, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(IsUpgradePedestalStatePostfix))));
                _patchedCount++;
            }

            // 离开 PedestalState 时恢复所有升级预览标记，并即时销毁未提交/已退出的基座中央卷轴预制体
            var bspType = GameInspector.FindTypeSafe("TheBazaar.BoardStatePresentation", "BoardStatePresentation");
            if (bspType != null)
            {
                _bspType = bspType;
                _bspCleanUpCompleteMethod = AccessTools.Method(bspType, "CleanUpComplete", Type.EmptyTypes);
                _bspCancellationTokenField = bspType.GetField("_cancellationToken", BindingFlags.NonPublic | BindingFlags.Instance);
                _bspHasFeatureMethod = AccessTools.Method(bspType, "HasFeature");
                if (_bspHasFeatureMethod != null)
                {
                    var featParams = _bspHasFeatureMethod.GetParameters();
                    if (featParams.Length == 1 && featParams[0].ParameterType.IsEnum)
                    {
                        _pedestalFeatureValues.Clear();
                        foreach (var enumVal in Enum.GetValues(featParams[0].ParameterType))
                        {
                            string enumName = enumVal?.ToString() ?? "";
                            if (enumName.IndexOf("Pedestal", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Artist", StringComparison.OrdinalIgnoreCase) >= 0
                                || enumName.IndexOf("Artisan", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                _pedestalFeatureValues.Add(enumVal);
                            }
                        }
                    }
                }

                var bspInit = AccessTools.Method(bspType, "Initialize");
                if (bspInit != null)
                {
                    harmony.Patch(bspInit, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationInitializePostfix))));
                    _patchedCount++;
                }

                var bspCleanUp = AccessTools.Method(bspType, "CleanUp", Type.EmptyTypes);
                if (bspCleanUp != null)
                {
                    harmony.Patch(bspCleanUp, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationCleanUpPrefix))));
                    _patchedCount++;
                }

                var bspOnDestroy = AccessTools.Method(bspType, "OnDestroy", Type.EmptyTypes);
                if (bspOnDestroy != null)
                {
                    harmony.Patch(bspOnDestroy, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardStatePresentationOnDestroyPostfix))));
                    _patchedCount++;
                }
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _bmShouldLoadPresMethod = AccessTools.Method(bmType, "ShouldLoadBoardStatePresentation", Type.EmptyTypes);
                _bmRemovePresMethod = AccessTools.Method(bmType, "RemoveBoardStatePresentation", Type.EmptyTypes);
                _bmInvalidateLoadRequestsMethod = AccessTools.Method(bmType, "InvalidateBoardStatePresentationLoadRequests", Type.EmptyTypes);
                _bmBoardStatePresProp = bmType.GetProperty("BoardStatePresentation", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmIsBoardStatePresLoadingProp = bmType.GetProperty("IsBoardStatePresentationLoading", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _bmLoadRequestVersionField = bmType.GetField("_boardStatePresentationLoadRequestVersion", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmPresRefField = bmType.GetField("boardStatePresentationReference", BindingFlags.NonPublic | BindingFlags.Instance);

                var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
                if (onStateChanged != null)
                {
                    harmony.Patch(onStateChanged, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(BoardManagerOnStateChangedPostfix))));
                    _patchedCount++;
                }

                var determinePres = AccessTools.Method(bmType, "DetermineBoardStatePresentation", Type.EmptyTypes);
                if (determinePres != null)
                {
                    harmony.Patch(determinePres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(DetermineBoardStatePresentationPostfix))));
                    _patchedCount++;
                }

                var loadPres = AccessTools.Method(bmType, "LoadBoardStatePresentation", Type.EmptyTypes);
                if (loadPres != null)
                {
                    harmony.Patch(loadPres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(LoadBoardStatePresentationPostfix))));
                    _patchedCount++;
                }

                if (_bmRemovePresMethod != null)
                {
                    harmony.Patch(_bmRemovePresMethod, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(RemoveBoardStatePresentationPrefix))));
                    _patchedCount++;
                }

                var reconcilePres = AccessTools.Method(bmType, "ReconcilePresentationAfterStateSyncAsync");
                if (reconcilePres != null)
                {
                    harmony.Patch(reconcilePres, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(ReconcilePresentationAfterStateSyncPostfix))));
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalState: " + ex.Message);
        }
    }

    private static TCardEncounterPedestal ResolveActiveUpgradePedestalTemplate(object encounterCard = null, object pedestalStateObj = null)
    {
        TCardEncounterPedestal pedTpl = null;
        if (encounterCard != null)
        {
            pedTpl = GameInspector.GetMember(encounterCard, "Template") as TCardEncounterPedestal;
        }

        if (pedTpl == null)
        {
            var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
            if (curEncCtrl != null && !(curEncCtrl is UnityEngine.Object uo && uo == null))
            {
                var encCard = GameInspector.GetMember(curEncCtrl, "CardData");
                pedTpl = GameInspector.GetMember(encCard, "Template") as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null && pedestalStateObj != null && _pedestalTemplateField != null)
        {
            pedTpl = _pedestalTemplateField.GetValue(pedestalStateObj) as TCardEncounterPedestal;
        }

        if (pedTpl == null)
        {
            var curState = GameInspector.GetCurrentAppState();
            if (curState != null && curState.GetType().Name == "PedestalState" && _pedestalTemplateField != null)
            {
                pedTpl = _pedestalTemplateField.GetValue(curState) as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null)
        {
            if (GetStaticDataMember("CurrentEncounterId") is Guid encGuid && encGuid != Guid.Empty)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                var getStaticM = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var staticMgr = getStaticM?.Invoke(null, null);
                var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
                pedTpl = getCardM?.Invoke(staticMgr, new object[] { encGuid }) as TCardEncounterPedestal;
            }
        }

        if (pedTpl == null) return null;
        return pedTpl.Behavior is TPedestalBehaviorUpgrade ? pedTpl : null;
    }

    private static object GetStaticDataMember(string memberName)
    {
        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
        if (dataType == null) return null;
        var p = dataType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (p != null) return p.GetValue(null);
        var f = dataType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return f?.GetValue(null);
    }

    private static bool IsUpgradeablePlayerItem(object card)
    {
        if (card == null) return false;
        if (GameInspector.GetMember(card, "Type") is not ECardType cardType || cardType != ECardType.Item)
            return false;

        var run = GameInspector.GetDataRun();
        var player = run != null ? GameInspector.GetMember(run, "Player") : null;
        var owner = GameInspector.GetMember(card, "Owner");
        if (player == null || !ReferenceEquals(owner, player))
            return false;

        var cardExtensionsType = GameInspector.FindTypeSafe("TheBazaar.CardExtensions");
        _canCardUpgradeMethod ??= cardExtensionsType?.GetMethod("CanCardUpgrade", BindingFlags.Public | BindingFlags.Static);
        if (_canCardUpgradeMethod != null)
        {
            return _canCardUpgradeMethod.Invoke(null, new[] { card }) is bool canUpg && canUpg;
        }

        return GameInspector.GetMember(card, "Tier") is ETier tier && tier < ETier.Diamond;
    }

    private static bool EvaluatePedestalSelectionCriteria(TCardEncounterPedestal pedTpl, object card)
    {
        if (pedTpl == null || card == null) return false;
        if (pedTpl.SelectionCriteria == null) return true;

        try
        {
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                if (_cardCondContextCtor == null)
                {
                    var ctors = typeof(CardConditionalContext).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    _cardCondContextCtor = ctors.FirstOrDefault(c =>
                    {
                        var ps = c.GetParameters();
                        return ps.Length == 4 && ps[0].ParameterType.IsInstanceOfType(run);
                    });
                }
                if (_cardCondContextCtor != null && card is ICard icard)
                {
                    var ctx = _cardCondContextCtor.Invoke(new[] { run, null, null, null }) as CardConditionalContext;
                    if (ctx != null)
                    {
                        return pedTpl.SelectionCriteria.IsSatisfiedBy(icard, ctx);
                    }
                }
            }
        }
        catch { }

        // 若条件上下文构造失败，退回到 PedestalState.CanBeUpgraded
        try
        {
            var curState = GameInspector.GetCurrentAppState();
            if (curState != null && curState.GetType().Name == "PedestalState")
            {
                var mCan = curState.GetType().GetMethod("CanBeUpgraded", BindingFlags.Public | BindingFlags.Instance);
                if (mCan != null)
                {
                    return mCan.Invoke(curState, new[] { card }) is bool ok && ok;
                }
            }
        }
        catch { }

        return false;
    }

    private static void CanUsePedestalUpgradePostfix(object card, object cardToSelect, ref bool __result)
    {
        if (__result || cardToSelect == null) return;
        try
        {
            if (!IsUpgradeablePlayerItem(cardToSelect)) return;
            var pedTpl = ResolveActiveUpgradePedestalTemplate(card);
            if (pedTpl == null) return;

            if (EvaluatePedestalSelectionCriteria(pedTpl, cardToSelect))
            {
                __result = true;
            }
        }
        catch { }
    }

    private static void IsUpgradePedestalStatePostfix(object tooltipData, ref bool __result)
    {
        if (__result || tooltipData == null) return;
        try
        {
            var curAppState = GameInspector.GetCurrentAppState();
            if (curAppState == null || curAppState.GetType().Name != "PedestalState")
                return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState || runState != ERunState.Pedestal)
                return;

            var cardInst = GameInspector.GetMember(tooltipData, "CardInstance");
            if (!IsUpgradeablePlayerItem(cardInst)) return;

            var pedTpl = ResolveActiveUpgradePedestalTemplate(null, curAppState);
            if (pedTpl == null) return;

            if (EvaluatePedestalSelectionCriteria(pedTpl, cardInst))
            {
                __result = true;
                Interlocked.Increment(ref _pedestalPreviewFallbackCount);
            }
        }
        catch { }
    }

    private static void PedestalOnEnterPostfix(object __instance)
    {
        _pedestalCommitRequested = false;
        _pendingPedestalExitAbort = false;
        _wasInAnyPedestal = true;
        try
        {
            if (__instance == null) return;

            // 若原生 OnEnter 时 _pedestalTemplate 为空，尝试按 Data.CurrentEncounterId 补全
            if (_pedestalTemplateField != null && _pedestalTemplateField.GetValue(__instance) == null)
            {
                var fallbackTpl = ResolveActiveUpgradePedestalTemplate(null, __instance);
                if (fallbackTpl != null)
                {
                    _pedestalTemplateField.SetValue(__instance, fallbackTpl);
                }
            }

            var pedTpl = _pedestalTemplateField?.GetValue(__instance) as TCardEncounterPedestal
                         ?? ResolveActiveUpgradePedestalTemplate(null, __instance);
            if (pedTpl?.Behavior is not TPedestalBehaviorUpgrade)
            {
                _wasInUpgradePedestal = false;
                return;
            }

            _wasInUpgradePedestal = true;
            RefreshHoveredUpgradePreviewOnEnter(pedTpl);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalOnEnterPostfix: " + ex.Message);
        }
    }

    private static void RefreshHoveredUpgradePreviewOnEnter(TCardEncounterPedestal pedTpl)
    {
        var lookup = GameInspector.GetDataLookup();
        if (lookup == null) return;
        if (GameInspector.GetMember(lookup, "CardControllerDictionary") is not IDictionary dict) return;

        var tipParent = GetStaticDataMember("TooltipParentComponent");
        foreach (DictionaryEntry kv in dict)
        {
            var ctrl = kv.Value;
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
            if (GameInspector.GetMember(ctrl, "IsCursorOverCard") is not bool over || !over) continue;

            var cardData = GameInspector.GetMember(ctrl, "CardData");
            if (!IsUpgradeablePlayerItem(cardData) || !EvaluatePedestalSelectionCriteria(pedTpl, cardData))
                continue;

            _enterUpgradePreviewMethod ??= ctrl.GetType().GetMethod("EnterUpgradePreview", BindingFlags.Public | BindingFlags.Instance);
            _enterUpgradePreviewMethod?.Invoke(ctrl, null);

            var cardTipData = GameInspector.GetMember(ctrl, "_cardTooltipData");
            cardTipData?.GetType().GetProperty("CanFuse", BindingFlags.Public | BindingFlags.Instance)?.SetValue(cardTipData, true);

            if (tipParent != null && !(tipParent is UnityEngine.Object tpuo && tpuo == null))
            {
                _cardTooltipControllerProp ??= tipParent.GetType().GetProperty("CardTooltipController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                var tipCtrl = _cardTooltipControllerProp?.GetValue(tipParent);
                if (tipCtrl != null && !(tipCtrl is UnityEngine.Object tcuo && tcuo == null))
                {
                    var tipData = GameInspector.GetMember(tipCtrl, "CurrentTooltipData");
                    var tipCard = tipData != null ? GameInspector.GetMember(tipData, "CardInstance") : null;
                    if (ReferenceEquals(tipCard, cardData))
                    {
                        tipData?.GetType().GetProperty("CanFuse", BindingFlags.Public | BindingFlags.Instance)?.SetValue(tipData, true);
                        _setDisplayModeMethod ??= tipCtrl.GetType().GetMethod("SetDisplayMode", BindingFlags.Public | BindingFlags.Instance);
                        if (_setDisplayModeMethod != null)
                        {
                            var modeType = _setDisplayModeMethod.GetParameters()[0].ParameterType;
                            _setDisplayModeMethod.Invoke(tipCtrl, new[] { Enum.Parse(modeType, "PedestalUpgradeTooltip") });
                        }
                    }
                }
            }

            _refreshFullTooltipMethod ??= ctrl.GetType().GetMethod("RefreshFullTooltip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _refreshFullTooltipMethod?.Invoke(ctrl, null);
            break;
        }
    }

    private static void BoardManagerOnStateChangedPostfix(object __instance, object obj)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null) || obj == null) return;
            var prev = GameInspector.GetMember(obj, "PreviousState");
            var cur = GameInspector.GetMember(obj, "CurrentState");
            string curStateName = cur?.GetType().Name ?? "";
            if (curStateName is "ChoiceState" or "CombatState" or "PVPCombatState" or "EndRunVictoryState" or "EndRunDefeatState")
            {
                ClearParentEncounterCache("OnStateChanged:" + curStateName);
            }
            bool wasPedestal = prev != null && prev.GetType().Name == "PedestalState";
            bool nowPedestal = cur != null && cur.GetType().Name == "PedestalState";
            bool nowPedestalContext = nowPedestal || IsInPedestalRunState() || IsCurrentEncounterPedestal();

            if (nowPedestalContext)
            {
                _wasInAnyPedestal = true;
                _pedestalVerifyNonPedestalExit = true;
                var curPres = GetBoardStatePresentationObj(__instance);
                if (curPres != null)
                {
                    TrackPedestalPresentation(curPres);
                }
            }

            if ((wasPedestal || _wasInUpgradePedestal) && !nowPedestal)
            {
                _wasInUpgradePedestal = false;
                ExitAllUpgradePreviewsSafely();
            }

            if ((wasPedestal || _wasInAnyPedestal || _pedestalVerifyNonPedestalExit) && !nowPedestalContext)
            {
                _wasInAnyPedestal = false;
                CleanupStalePedestalBoardPresentation(__instance, "OnStateChanged", forceImmediate: true, allowWhileInPedestalState: false);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardManagerOnStateChangedPostfix: " + ex.Message);
        }
    }

    private static void BoardStatePresentationInitializePostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            if (IsInPedestalContext() || HasPedestalFeature(__instance))
            {
                TrackPedestalPresentation(__instance);
            }
        }
        catch { }
    }

    private static void BoardStatePresentationOnDestroyPostfix(object __instance)
    {
        try
        {
            if (__instance is Component comp)
            {
                int id = comp.GetInstanceID();
                lock (_pedestalPresLock)
                {
                    _pedestalPresentationIds.Remove(id);
                    _forceCleanedPresentationIds.Remove(id);
                    if (_lastPedestalPresentationId == id)
                    {
                        _lastPedestalPresentationId = 0;
                    }
                }
            }
        }
        catch { }
    }

    private static bool BoardStatePresentationCleanUpPrefix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null))
            {
                __result = Task.CompletedTask;
                return false;
            }

            // 只要进入销毁流程（CleanUp 仅在移除或替换展示时调用），凡属基座实例/强清实例/基座上下文或已失活对象均立即同步短路销毁，避免连续触发多级基座或切回商店时卡入 10 秒超时
            bool shouldShortCircuit = IsForceCleanedPresentation(__instance)
                || IsTrackedOrPedestalPresentation(__instance)
                || IsInPedestalContext()
                || (__instance is Component c && c.gameObject != null && !c.gameObject.activeInHierarchy);

            if (!shouldShortCircuit)
                return true;

            bool newlyCleaned = ShortCircuitAndDestroyPresentation(__instance);
            if (newlyCleaned)
            {
                Interlocked.Increment(ref _pedestalPresentationCleanedCount);
            }
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.BoardStatePresentationCleanUpPrefix: " + ex.Message);
            return true;
        }
    }

    private static void DetermineBoardStatePresentationPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "DetermineBoardStatePresentation");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.DetermineBoardStatePresentationPostfix: " + ex.Message);
        }
    }

    private static void LoadBoardStatePresentationPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "LoadBoardStatePresentation");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.LoadBoardStatePresentationPostfix: " + ex.Message);
        }
    }

    private static void ReconcilePresentationAfterStateSyncPostfix(object __instance, ref Task __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || __result == null) return;
            __result = WrapBoardPresentationCheckTask(__result, __instance, "ReconcilePresentationAfterStateSync");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ReconcilePresentationAfterStateSyncPostfix: " + ex.Message);
        }
    }

    private static async Task WrapBoardPresentationCheckTask(Task inner, object boardManager, string sourceTag)
    {
        try
        {
            await inner;
        }
        finally
        {
            PostToMainThread(() =>
            {
                try
                {
                    if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                        return;

                    var curPres = GetBoardStatePresentationObj(boardManager);
                    if (IsInPedestalContext())
                    {
                        _wasInAnyPedestal = true;
                        _pedestalVerifyNonPedestalExit = true;
                        if (curPres != null)
                        {
                            TrackPedestalPresentation(curPres);
                        }
                    }
                    else
                    {
                        // 已切换到非基座状态（如 Encounter/Choice/葵仙子商店），二层兜底清理一切残留基座或未替换卷轴
                        CleanupStalePedestalBoardPresentation(boardManager, sourceTag, forceImmediate: false, allowWhileInPedestalState: false);
                    }
                }
                catch (Exception ex)
                {
                    BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapBoardPresentationCheckTask: " + ex.Message);
                }
            });
        }
    }

    private static bool RemoveBoardStatePresentationPrefix(object __instance, ref Task __result)
    {
        if (__instance == null || (__instance is UnityEngine.Object bmUo && bmUo == null)) return true;
        try
        {
            // 关键修复 #12：移除旧补丁的 if (IsInPedestalRunState()) return true 错误放行！
            // 玩家正常用完附魔台/升级台或连续切换多级基座时，客户端调用 RemoveBoardStatePresentation 瞬间往往仍处于 Pedestal 运行态，
            // 若此处放行则会走入原版 10 秒超时死等导致下家商店中央继续挂着蓝色/金色卷轴。
            var curPres = GetBoardStatePresentationObj(__instance);
            bool isLoading = _bmIsBoardStatePresLoadingProp?.GetValue(__instance) is bool b && b;
            bool inPedestalCtx = IsInPedestalContext();
            bool isPedestalPres = curPres != null
                && (IsTrackedOrPedestalPresentation(curPres) || IsForceCleanedPresentation(curPres) || inPedestalCtx);
            bool hasPendingExitAbort = (_pendingPedestalExitAbort || inPedestalCtx) && (curPres != null || isLoading);

            if (!isPedestalPres && !hasPendingExitAbort)
            {
                if (!inPedestalCtx && _pedestalVerifyNonPedestalExit)
                {
                    SweepOrphanPedestalPresentationsInScene(curPres);
                }
                return true;
            }

            // 绝不走原生 await BoardStatePresentation.CleanUp() 10 秒超时分支，直接同步即时销毁并重置全部引用
            CleanupStalePedestalBoardPresentation(__instance, "RemoveBoardStatePresentationPrefix", forceImmediate: true, allowWhileInPedestalState: true);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RemoveBoardStatePresentationPrefix: " + ex.Message);
            return true;
        }
    }

    private static void CleanupStalePedestalBoardPresentation(object boardManager, string reason, bool forceImmediate, bool allowWhileInPedestalState)
    {
        try
        {
            if (boardManager == null || (boardManager is UnityEngine.Object bmUo && bmUo == null))
                return;

            bool inPedestalCtx = IsInPedestalContext();
            if (inPedestalCtx && !allowWhileInPedestalState)
                return;

            var curPres = GetBoardStatePresentationObj(boardManager);
            bool isLoading = _bmIsBoardStatePresLoadingProp?.GetValue(boardManager) is bool b && b;
            bool shouldLoad = _bmShouldLoadPresMethod != null
                && _bmShouldLoadPresMethod.Invoke(boardManager, null) is bool sl && sl;

            bool isPedestalPres = curPres != null
                && (IsTrackedOrPedestalPresentation(curPres) || IsForceCleanedPresentation(curPres));

            // 二层保险：切换到非基座状态后，若新遭遇模板无独立中央 Presentation（如葵仙子等商店），任何尚未销毁的旧 BoardStatePresentation 均按陈旧卷轴立即清理
            bool staleUnreplacedPres = !inPedestalCtx && curPres != null && !shouldLoad && _pedestalVerifyNonPedestalExit;

            // 若当前已合法进入带独立呈现的后续非基座奇遇，且当前持有的是新呈现而非基座卷轴，仅清扫场景孤立基座实例，不破坏当前正常加载
            if (!inPedestalCtx && shouldLoad && !forceImmediate && !isPedestalPres)
            {
                _pendingPedestalExitAbort = false;
                _pedestalVerifyNonPedestalExit = false;
                SweepOrphanPedestalPresentationsInScene(curPres);
                return;
            }

            if (!forceImmediate && !isPedestalPres && !staleUnreplacedPres && !(_pendingPedestalExitAbort && !shouldLoad))
            {
                if (!inPedestalCtx && curPres == null && !isLoading)
                {
                    _pedestalVerifyNonPedestalExit = false;
                }
                SweepOrphanPedestalPresentationsInScene(!inPedestalCtx && shouldLoad ? curPres : null);
                return;
            }

            // 若退出基座时资源仍在异步加载且暂未实例化出展示对象，保留中止标志以便异步回调后兜底收尾
            _pendingPedestalExitAbort = forceImmediate && curPres == null && isLoading;

            if (curPres != null || isLoading)
            {
                // 仅当新奇遇不加载或正在移除旧基座呈现时才作废加载请求，避免误伤新奇遇正在发起的异步加载
                if (!shouldLoad || isPedestalPres || staleUnreplacedPres || forceImmediate)
                {
                    if (_bmInvalidateLoadRequestsMethod != null)
                    {
                        _bmInvalidateLoadRequestsMethod.Invoke(boardManager, null);
                    }
                    else
                    {
                        BumpBoardStatePresentationLoadVersion(boardManager);
                    }
                    _bmPresRefField?.SetValue(boardManager, null);
                    _bmIsBoardStatePresLoadingProp?.SetValue(boardManager, false);
                }

                if (curPres != null)
                {
                    _bmBoardStatePresProp?.SetValue(boardManager, null);
                }

                bool cleaned = curPres != null && ShortCircuitAndDestroyPresentation(curPres);
                if (cleaned || isLoading)
                {
                    int cnt = Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                    BazaarLauncherPlugin.Log?.LogInfo(
                        $"CompatAuditPatch: immediately recycled stale pedestal presentation via {reason} (hasPres={curPres != null}, wasLoading={isLoading}, allowInPedestal={allowWhileInPedestalState}, count={cnt})");
                }
            }

            SweepOrphanPedestalPresentationsInScene(null);
            if (!inPedestalCtx && !isLoading && GetBoardStatePresentationObj(boardManager) == null)
            {
                _pedestalVerifyNonPedestalExit = false;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CleanupStalePedestalBoardPresentation: " + ex.Message);
        }
    }

    private static void SweepOrphanPedestalPresentationsInScene(object validKeepPres)
    {
        try
        {
            if (_bspType == null) return;
            var found = UnityEngine.Object.FindObjectsByType(_bspType, FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (found == null || found.Length == 0) return;

            int keepId = validKeepPres is Component keepComp && keepComp != null ? keepComp.GetInstanceID() : 0;
            foreach (var obj in found)
            {
                if (obj == null || (obj is UnityEngine.Object uo && uo == null)) continue;
                int id = obj is Component comp ? comp.GetInstanceID() : 0;
                if (keepId != 0 && id == keepId) continue;

                bool isKnownPedestal = IsTrackedOrPedestalPresentation(obj) || IsForceCleanedPresentation(obj);
                bool unownedExtraWhileNoKeep = keepId == 0 && !IsInPedestalContext();
                if (isKnownPedestal || unownedExtraWhileNoKeep)
                {
                    if (ShortCircuitAndDestroyPresentation(obj))
                    {
                        Interlocked.Increment(ref _pedestalPresentationCleanedCount);
                    }
                }
            }
        }
        catch { }
    }

    private static bool IsInPedestalContext()
    {
        if (IsInPedestalRunState()) return true;
        var appState = GameInspector.GetCurrentAppState();
        if (appState != null && string.Equals(appState.GetType().Name, "PedestalState", StringComparison.Ordinal))
            return true;
        return IsCurrentEncounterPedestal();
    }

    private static bool IsCurrentEncounterPedestal()
    {
        try
        {
            var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
            if (curEncCtrl != null && !(curEncCtrl is UnityEngine.Object uo && uo == null))
            {
                var encCard = GameInspector.GetMember(curEncCtrl, "CardData");
                if (GameInspector.GetMember(encCard, "Template") is TCardEncounterPedestal)
                    return true;
            }

            if (GetStaticDataMember("CurrentEncounterId") is Guid encGuid && encGuid != Guid.Empty)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                var getStaticM = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                var staticMgr = getStaticM?.Invoke(null, null);
                var getCardM = staticMgr?.GetType().GetMethod("GetCardById", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Guid) }, null);
                if (getCardM?.Invoke(staticMgr, new object[] { encGuid }) is TCardEncounterPedestal)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static bool IsInPedestalRunState()
    {
        var dataState = GameInspector.GetDataCurrentState();
        return GameInspector.GetMember(dataState, "StateName") is ERunState runState && runState == ERunState.Pedestal;
    }

    private static bool HasPedestalFeature(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null)) return false;
        try
        {
            if (_bspHasFeatureMethod != null && _pedestalFeatureValues.Count > 0)
            {
                foreach (var featVal in _pedestalFeatureValues)
                {
                    if (_bspHasFeatureMethod.Invoke(presObj, new[] { featVal }) is bool hasFeat && hasFeat)
                        return true;
                }
            }

            if (presObj is Component comp && comp.gameObject != null)
            {
                string goName = comp.gameObject.name ?? "";
                if (goName.IndexOf("Pedestal", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Enchant", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Upgrade", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Artisan", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Artist", StringComparison.OrdinalIgnoreCase) >= 0
                    || goName.IndexOf("Scroll", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }
        catch
        {
        }
        return false;
    }

    private static void TrackPedestalPresentation(object presObj)
    {
        if (presObj is not Component comp || comp == null) return;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            if (_pedestalPresentationIds.Count > 128) _pedestalPresentationIds.Clear();
            _pedestalPresentationIds.Add(id);
            _lastPedestalPresentationId = id;
        }
    }

    private static bool IsTrackedOrPedestalPresentation(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null)) return false;
        if (presObj is Component comp && comp != null)
        {
            int id = comp.GetInstanceID();
            lock (_pedestalPresLock)
            {
                if (_pedestalPresentationIds.Contains(id) || (_lastPedestalPresentationId != 0 && _lastPedestalPresentationId == id))
                    return true;
            }
        }
        return HasPedestalFeature(presObj);
    }

    private static void PostToMainThread(Action action)
    {
        if (action == null) return;
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            action();
            return;
        }

        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ => action(), null);
            return;
        }

        action();
    }

    private static bool IsForceCleanedPresentation(object presObj)
    {
        if (presObj is not Component comp) return false;
        int id = comp.GetInstanceID();
        lock (_pedestalPresLock)
        {
            return _forceCleanedPresentationIds.Contains(id);
        }
    }

    private static bool ShortCircuitAndDestroyPresentation(object presObj)
    {
        if (presObj == null || (presObj is UnityEngine.Object uo && uo == null))
            return false;

        bool newlyMarked = true;
        if (presObj is Component comp)
        {
            int id = comp.GetInstanceID();
            lock (_pedestalPresLock)
            {
                if (_forceCleanedPresentationIds.Count > 128) _forceCleanedPresentationIds.Clear();
                newlyMarked = _forceCleanedPresentationIds.Add(id);
                _pedestalPresentationIds.Remove(id);
            }
        }

        // 1. 立即置位 CleanupComplete = true 并取消等待令牌，瞬间满足 <CleanUp>b__70_0 条件
        try { _bspCleanUpCompleteMethod?.Invoke(presObj, null); } catch { }
        try
        {
            if (_bspCancellationTokenField?.GetValue(presObj) is CancellationTokenSource cts && !cts.IsCancellationRequested)
            {
                cts.Cancel();
            }
        }
        catch { }

        // 2. 立即失活隐藏并销毁残余基座 GameObject（Unity 将在当帧末触发 OnDestroy 解绑全部事件，视觉当帧消失）
        if (presObj is Component presComp && presComp.gameObject != null)
        {
            if (presComp.gameObject.activeSelf)
            {
                presComp.gameObject.SetActive(false);
            }
            UnityEngine.Object.Destroy(presComp.gameObject);
        }

        return newlyMarked;
    }

    private static object GetBoardStatePresentationObj(object boardManager)
    {
        if (boardManager == null || _bmBoardStatePresProp == null) return null;
        var val = _bmBoardStatePresProp.GetValue(boardManager);
        if (val == null || (val is UnityEngine.Object uo && uo == null)) return null;
        return val;
    }

    private static void BumpBoardStatePresentationLoadVersion(object boardManager)
    {
        if (boardManager == null || _bmLoadRequestVersionField == null) return;
        if (_bmLoadRequestVersionField.GetValue(boardManager) is int curVer)
        {
            _bmLoadRequestVersionField.SetValue(boardManager, curVer + 1);
        }
    }

    private static void ExitAllUpgradePreviewsSafely()
    {
        var tipParent = GetStaticDataMember("TooltipParentComponent");
        if (tipParent != null && !(tipParent is UnityEngine.Object tpuo && tpuo == null))
        {
            _exitUpgradePreviewAllMethod ??= tipParent.GetType().GetMethod("ExitUpgradePreviewOnAllPlayerCards", BindingFlags.Public | BindingFlags.Instance);
            _exitUpgradePreviewAllMethod?.Invoke(tipParent, null);

            _cardTooltipControllerProp ??= tipParent.GetType().GetProperty("CardTooltipController", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            var tipCtrl = _cardTooltipControllerProp?.GetValue(tipParent);
            if (tipCtrl != null && !(tipCtrl is UnityEngine.Object tcuo && tcuo == null))
            {
                _getDisplayModeMethod ??= tipCtrl.GetType().GetMethod("GetDisplayMode", BindingFlags.Public | BindingFlags.Instance);
                var mode = _getDisplayModeMethod?.Invoke(tipCtrl, null);
                if (mode != null && string.Equals(mode.ToString(), "PedestalUpgradeTooltip", StringComparison.Ordinal))
                {
                    _setDisplayModeMethod ??= tipCtrl.GetType().GetMethod("SetDisplayMode", BindingFlags.Public | BindingFlags.Instance);
                    if (_setDisplayModeMethod != null)
                    {
                        var modeType = _setDisplayModeMethod.GetParameters()[0].ParameterType;
                        _setDisplayModeMethod.Invoke(tipCtrl, new[] { Enum.Parse(modeType, "Default") });
                    }
                }
            }
        }

        var lookup = GameInspector.GetDataLookup();
        if (lookup != null && GameInspector.GetMember(lookup, "CardControllerDictionary") is IDictionary dict)
        {
            foreach (DictionaryEntry kv in dict)
            {
                var ctrl = kv.Value;
                if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
                if (GameInspector.GetMember(ctrl, "_isInUpgradePreview") is bool inPrev && inPrev)
                {
                    _exitUpgradePreviewCardMethod ??= ctrl.GetType().GetMethod("ExitUpgradePreview", BindingFlags.Public | BindingFlags.Instance);
                    _exitUpgradePreviewCardMethod?.Invoke(ctrl, null);
                }
            }
        }
    }

    private static void PedestalUpgradePrefix(NetMessageGameSim message, out string __state)
    {
        __state = null;
        try
        {
            __state = message?.Data?.Events?.OfType<GameSimEventCardUpgraded>().FirstOrDefault()?.InstanceId;
        }
        catch { }
    }

    private static void PedestalEnchantPrefix(NetMessageGameSim message, out string __state)
    {
        __state = null;
        try
        {
            __state = message?.Data?.Events?.OfType<GameSimEventCardEnchanted>().FirstOrDefault()?.InstanceId;
        }
        catch { }
    }

    private static void PedestalUpgradeOrEnchantPostfix(object __instance, bool __result, string __state)
    {
        if (!__result) return;

        try
        {
            if (!string.IsNullOrEmpty(__state))
            {
                ForceRefreshItemControllerUi(__state);
            }

            if (CombatPatch.SpeedOverride > 1.5f || _pedestalCommitRequested)
            {
                _pedestalCommitRequested = false;
                if (__instance != null && _pedestalAnimCompletedField != null)
                {
                    _pedestalAnimCompletedField.SetValue(__instance, true);
                    Interlocked.Increment(ref _pedestalFastCompletedCount);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PedestalPostfix: " + ex.Message);
        }
    }

    private static void ForceRefreshItemControllerUi(string instanceId)
    {
        var entities = GameInspector.GetDataEntities();
        var lookup = GameInspector.GetDataLookup();
        if (entities == null || lookup == null) return;

        var iid = new InstanceId(instanceId);
        if (!entities.Contains(iid)) return;
        var entity = entities[iid];
        if (entity == null) return;

        _getCardControllerMethod ??= lookup.GetType().GetMethod(
            "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return;

        var ctrlType = ctrl.GetType();
        for (var t = ctrlType; t != null; t = t.BaseType)
        {
            var blockField = t.GetField("blockVisualUpdates", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (blockField != null)
            {
                blockField.SetValue(ctrl, false);
                break;
            }
        }

        var mFromEvent = ctrlType.GetMethod("UpdateCardFromEvent", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        if (mFromEvent != null)
        {
            mFromEvent.Invoke(ctrl, null);
            return;
        }

        var mUpdate = ctrlType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "UpdateCard");
        if (mUpdate != null)
        {
            var ps = mUpdate.GetParameters();
            if (ps.Length == 0)
                mUpdate.Invoke(ctrl, null);
            else if (ps.Length == 2)
                mUpdate.Invoke(ctrl, new object[] { null, false });
            else if (ps.Length == 1)
                mUpdate.Invoke(ctrl, new object[] { null });
        }
    }

    // ── 10. RewardController.PlayDespawnAnimation：高倍速跳过 10s 退场动画死等 ──

    private static void PatchRewardController(Harmony harmony)
    {
        try
        {
            var rcType = GameInspector.FindTypeSafe("RewardController", "TheBazaar.RewardController");
            if (rcType == null) return;

            _rewardHideCompleteField = rcType.GetField("hideComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            _rewardAnimatorProp = rcType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            _rewardSoundHandlerField = rcType.GetField("soundRewardHandler", BindingFlags.NonPublic | BindingFlags.Instance);

            var mDespawn = AccessTools.Method(rcType, "PlayDespawnAnimation");
            if (mDespawn != null)
            {
                harmony.Patch(mDespawn, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(PlayDespawnAnimationPrefix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RewardController: " + ex.Message);
        }
    }

    private static bool PlayDespawnAnimationPrefix(object __instance, ref Task __result)
    {
        if (CombatPatch.SpeedOverride <= 1.5f || __instance == null)
            return true;

        try
        {
            if (_rewardHideCompleteField?.GetValue(__instance) is bool alreadyHidden && alreadyHidden)
                return true;

            if (__instance is not Component comp || comp.gameObject == null || !comp.gameObject.activeSelf)
                return true;

            var animator = _rewardAnimatorProp?.GetValue(__instance);
            if (animator != null)
            {
                var setBool = animator.GetType().GetMethod("SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string), typeof(bool) }, null);
                setBool?.Invoke(animator, new object[] { "isVisible", false });
            }

            var soundHandler = _rewardSoundHandlerField?.GetValue(__instance);
            if (soundHandler != null)
            {
                var stopLoop = soundHandler.GetType().GetMethod("StopRewardLoop", BindingFlags.Public | BindingFlags.Instance);
                stopLoop?.Invoke(soundHandler, new object[] { comp.gameObject });
            }

            _rewardHideCompleteField?.SetValue(__instance, true);
            Interlocked.Increment(ref _rewardDespawnSkippedCount);
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PlayDespawnAnimationPrefix: " + ex.Message);
            return true;
        }
    }

    // ── 11. 悬停高亮与目标筛选对齐（#11：TTargetCardXMost / HintService / PreviewMove）──

    private static FieldInfo _hintCardField;
    private static PropertyInfo _hintCardProp;
    private static PropertyInfo _cardTagsProp;
    private static PropertyInfo _itemEnchantmentProp;
    private static MethodInfo _itemGetEnchantmentsMethod;
    private static MethodInfo _itemCheckForSellZoneMethod;
    private static PropertyInfo _cardCtrlCardDataProp;
    private static FieldInfo _cardCtrlBoardSectionField;
    private static MethodInfo _cardCtrlShowCardHintMethod;
    private static Type _clientCardType;
    private static readonly Dictionary<string, (HashSet<ECardTag> Tags, HashSet<EHiddenTag> Hidden)> _enchDynamicSyncedTags =
        new(StringComparer.OrdinalIgnoreCase);

    private static void PatchTargetingAndHints(Harmony harmony)
    {
        try
        {
            var xmostGetTargets = AccessTools.Method(
                typeof(TTargetCardXMost),
                nameof(TTargetCardXMost.GetTargets),
                new[]
                {
                    typeof(TargetingContext),
                    typeof(uint?),
                    typeof(List<ITCardConditional>),
                    typeof(HashSet<ECardState>),
                });
            if (xmostGetTargets != null)
            {
                harmony.Patch(xmostGetTargets, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(TargetCardXMostGetTargetsPrefix))));
                _patchedCount++;
            }

            var hintServiceType = AccessTools.TypeByName("BazaarGameClient.Domain.Hints.HintService");
            var listHints = hintServiceType == null ? null : AccessTools.Method(hintServiceType, "ListHints");
            if (listHints != null)
            {
                harmony.Patch(
                    listHints,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(HintServiceListHintsPostfix))));
                _patchedCount++;
            }

            var hintType = AccessTools.TypeByName("BazaarGameClient.Domain.Hints.Hint");
            if (hintType != null)
            {
                _hintCardProp = hintType.GetProperty("Card", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _hintCardField = hintType.GetField("Card", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            _clientCardType = AccessTools.TypeByName("BazaarGameClient.Domain.Models.Cards.Card");
            if (_clientCardType != null)
            {
                _cardTagsProp = _clientCardType.GetProperty("Tags", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            }

            var itemCardType = AccessTools.TypeByName("BazaarGameClient.Domain.Models.Cards.ItemCard");
            if (itemCardType != null)
            {
                _itemEnchantmentProp = itemCardType.GetProperty("Enchantment", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _itemGetEnchantmentsMethod = itemCardType.GetMethod("GetEnchantments", BindingFlags.Public | BindingFlags.Instance);
            }

            var itemCtrlType = GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            var previewMove = itemCtrlType == null ? null : AccessTools.Method(itemCtrlType, "PreviewMove", new[] { typeof(Vector3) });
            if (previewMove != null)
            {
                _itemCheckForSellZoneMethod = itemCtrlType.GetMethod("CheckForSellZone", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                _cardCtrlCardDataProp = itemCtrlType.GetProperty("CardData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                _cardCtrlBoardSectionField = FindFieldInHierarchy(itemCtrlType, "boardSection");
                _cardCtrlShowCardHintMethod = itemCtrlType.GetMethod("ShowCardHint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

                harmony.Patch(previewMove, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CompatAuditPatch), nameof(ItemControllerPreviewMovePostfix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchTargetingAndHints: " + ex.Message);
        }
    }

    private static FieldInfo FindFieldInHierarchy(Type type, string fieldName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
        }
        return null;
    }

    /// <summary>
    /// 修复 #11：严格对齐服务端 TargetResolver（Collect -> 同步附魔标签 -> ExcludeSelf(引用+InstanceId) -> 按槽位升序 -> Conditions 过滤 -> 重排 -> Priority -> LeftMost/RightMost）。
    /// 避免客户端因 Container 槽位顺序滞后、HashSet 打乱、HiddenTags 缺失附魔标签或 null 槽位混入导致悬停高亮偏差。
    /// </summary>
    private static bool TargetCardXMostGetTargetsPrefix(
        TTargetCardXMost __instance,
        TargetingContext ctx,
        uint? NumberOfAllowedTargets,
        List<ITCardConditional> targetingPriorities,
        HashSet<ECardState> cardStateFilter,
        ref List<ICard> __result)
    {
        try
        {
            if (__instance == null || ctx.Run == null)
                return true;

            SyncClientCardTagsAndHiddenTags(ctx.TargetingCard);

            var candidates = CollectXMostCandidates(__instance.TargetSection, ctx, cardStateFilter);
            foreach (var c in candidates)
            {
                SyncClientCardTagsAndHiddenTags(c);
            }

            // 1. 先按引用与 InstanceId 双重严格排除自身（对齐服务端 ExcludeSelf 优先于条件筛选与极值选取）
            if (ctx.TargetingCard != null && __instance.ExcludeSelf)
            {
                var selfId = ctx.TargetingCard.GetInstanceId();
                candidates.RemoveAll(c => c == null || ReferenceEquals(c, ctx.TargetingCard) || c.GetInstanceId() == selfId);
            }

            // 2. 过滤未入格卡牌并按真实槽位升序稳定排序（Hand 优先于 Stash），消除 Container 未同步或 null 槽位抢占最左位置的问题
            candidates = candidates
                .Where(c => c != null && c.LeftSocketId.HasValue)
                .OrderBy(c => c.Section == EInventorySection.Stash ? 1 : 0)
                .ThenBy(c => (int)c.LeftSocketId.Value)
                .ToList();

            // 3. 应用 Conditions 条件筛选后立即重新按槽位升序排列（防止 HashSet 破坏槽位顺序）
            if (__instance.Conditions != null && candidates.Count > 0)
            {
                var condCtx = new CardConditionalContext(ctx.Run, candidates, ctx.TargetingCard, ctx.EventContext);
                var set = new HashSet<ICard>(candidates);
                var filtered = __instance.Conditions.Filter(set, condCtx);
                candidates = filtered == null
                    ? new List<ICard>()
                    : filtered
                        .Where(c => c != null && c.LeftSocketId.HasValue)
                        .OrderBy(c => c.Section == EInventorySection.Stash ? 1 : 0)
                        .ThenBy(c => (int)c.LeftSocketId.Value)
                        .ToList();
            }

            // 4. 若指定了优先级条件列表且允许目标数 > 0，按优先级保留并在同级内继续维持槽位顺序
            if (candidates.Count > 0 && NumberOfAllowedTargets.GetValueOrDefault() > 0 && targetingPriorities != null && targetingPriorities.Count > 0)
            {
                var prioritized = new List<ICard>();
                var seenIds = new HashSet<InstanceId>();
                var fullSet = new HashSet<ICard>(candidates);
                var priCtx = new CardConditionalContext(ctx.Run, candidates, ctx.TargetingCard, ctx.EventContext);

                foreach (var priCond in targetingPriorities)
                {
                    if (priCond == null) continue;
                    var matched = priCond.Filter(fullSet, priCtx);
                    if (matched == null || matched.Count == 0) continue;

                    foreach (var mc in matched
                        .Where(c => c != null && c.LeftSocketId.HasValue)
                        .OrderBy(c => c.Section == EInventorySection.Stash ? 1 : 0)
                        .ThenBy(c => (int)c.LeftSocketId.Value))
                    {
                        if (seenIds.Add(mc.GetInstanceId()))
                        {
                            prioritized.Add(mc);
                        }
                    }
                }

                if (prioritized.Count > 0)
                {
                    candidates = prioritized;
                }
            }

            if (candidates.Count == 0)
            {
                __result = new List<ICard>();
                return false;
            }

            __result = __instance.TargetMode switch
            {
                ETargetCardXMostTargetMode.LeftMostCard => new List<ICard> { candidates[0] },
                ETargetCardXMostTargetMode.RightMostCard => new List<ICard> { candidates[candidates.Count - 1] },
                _ => new List<ICard>(),
            };
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TargetCardXMostGetTargetsPrefix: " + ex.Message);
            return true;
        }
    }

    private static List<ICard> CollectXMostCandidates(ETargetCardXMostTargetSection section, TargetingContext ctx, HashSet<ECardState> cardStateFilter)
    {
        IEnumerable<ICard> raw = section switch
        {
            ETargetCardXMostTargetSection.AbsolutePlayerHand => TargetSystem.GetAbsolutePlayerHand(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerStash => TargetSystem.GetAbsolutePlayerStash(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash => TargetSystem.GetAbsolutePlayerHandAndStash(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllHands => TargetSystem.GetAllHands(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllHandsAndStashes => TargetSystem.GetAllHandsAndStashes(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.AllStashes => TargetSystem.GetAllStashes(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.SelfHand => TargetSystem.GetPlayerHand(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.SelfHandAndStash => TargetSystem.GetPlayerHandAndStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.SelfStash => TargetSystem.GetPlayerStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentHand => TargetSystem.GetOpponentHand(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentHandAndStash => TargetSystem.GetOpponentHandAndStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentStash => TargetSystem.GetOpponentStash(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.AbsolutePlayerSocketEffects => TargetSystem.GetAbsolutePlayerSocketEffects(ctx.Run, cardStateFilter),
            ETargetCardXMostTargetSection.SelfSocketEffects => TargetSystem.GetPlayerSocketEffects(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.OpponentSocketEffects => TargetSystem.GetOpponentSocketEffects(ctx.Run, ctx.TargetingCard?.Owner, cardStateFilter),
            ETargetCardXMostTargetSection.AllSocketEffects => TargetSystem.GetAllSocketEffects(ctx.Run, cardStateFilter),
            _ => Array.Empty<ICard>(),
        };

        var list = new List<ICard>();
        var seen = new HashSet<InstanceId>();
        if (raw != null)
        {
            foreach (var c in raw)
            {
                if (c != null && seen.Add(c.GetInstanceId()))
                {
                    list.Add(c);
                }
            }
        }

        // 若目标为本地玩家的 Hand/Stash，额外结合 Data.Entities 对账，防止客户端拖动/购买后 Container.Sockets 尚未同步导致漏卡或错序
        bool wantHand = section is ETargetCardXMostTargetSection.AbsolutePlayerHand
            or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash
            or ETargetCardXMostTargetSection.SelfHand
            or ETargetCardXMostTargetSection.SelfHandAndStash;
        bool wantStash = section is ETargetCardXMostTargetSection.AbsolutePlayerStash
            or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash
            or ETargetCardXMostTargetSection.SelfStash
            or ETargetCardXMostTargetSection.SelfHandAndStash;

        if (wantHand || wantStash)
        {
            var localPlayer = ctx.Run.GetPlayer();
            bool isAbsolute = section is ETargetCardXMostTargetSection.AbsolutePlayerHand
                or ETargetCardXMostTargetSection.AbsolutePlayerStash
                or ETargetCardXMostTargetSection.AbsolutePlayerHandAndStash;
            var resolvedPlayer = isAbsolute ? localPlayer : (ctx.TargetingCard?.Owner ?? localPlayer);

            if (localPlayer != null && ReferenceEquals(resolvedPlayer, localPlayer))
            {
                var entities = GameInspector.GetDataEntities();
                if (entities != null)
                {
                    // 先踢掉已离场或已不属于该分区的旧 Container 残留项
                    list.RemoveAll(c =>
                        ReferenceEquals(c.Owner, localPlayer)
                        && ((c.Section != EInventorySection.Hand && c.Section != EInventorySection.Stash)
                            || (c.Section == EInventorySection.Hand && !wantHand)
                            || (c.Section == EInventorySection.Stash && !wantStash)));
                    seen.Clear();
                    foreach (var c in list)
                    {
                        seen.Add(c.GetInstanceId());
                    }

                    foreach (DictionaryEntry kv in entities)
                    {
                        if (kv.Value is not ICard ent) continue;
                        if (ent.Type != ECardType.Item || !ReferenceEquals(ent.Owner, localPlayer)) continue;
                        if (!ent.LeftSocketId.HasValue) continue;
                        if (ent.Section == EInventorySection.Hand && !wantHand) continue;
                        if (ent.Section == EInventorySection.Stash && !wantStash) continue;
                        if (ent.Section != EInventorySection.Hand && ent.Section != EInventorySection.Stash) continue;
                        bool stateOk = cardStateFilter != null ? cardStateFilter.Contains(ent.State) : ent.State == ECardState.Alive;
                        if (!stateOk) continue;
                        if (seen.Add(ent.GetInstanceId()))
                        {
                            list.Add(ent);
                        }
                    }
                }
            }
        }

        return list;
    }

    private static void SyncClientCardTagsAndHiddenTags(ICard card)
    {
        if (card == null) return;
        try
        {
            string iid = card.GetInstanceId().ToString();
            var tpl = card.GetTemplate();
            var desiredTags = new HashSet<ECardTag>();
            var desiredHidden = new HashSet<EHiddenTag>();
            var enchAddedTags = new HashSet<ECardTag>();
            var enchAddedHidden = new HashSet<EHiddenTag>();

            if (tpl != null)
            {
                if (tpl.HiddenTags != null)
                {
                    foreach (var ht in tpl.HiddenTags) desiredHidden.Add(ht);
                }
                if (tpl.Tags != null)
                {
                    foreach (var tg in tpl.Tags) desiredTags.Add(tg);
                }
            }

            if (_itemEnchantmentProp != null && _itemGetEnchantmentsMethod != null && _itemEnchantmentProp.DeclaringType != null && _itemEnchantmentProp.DeclaringType.IsInstanceOfType(card))
            {
                var enchObj = _itemEnchantmentProp.GetValue(card);
                if (enchObj is EEnchantmentType enchType)
                {
                    TEnchantment enchDef = null;
                    if (_itemGetEnchantmentsMethod.Invoke(card, null) is IReadOnlyDictionary<EEnchantmentType, TEnchantment> roDict)
                    {
                        roDict.TryGetValue(enchType, out enchDef);
                    }
                    else if (tpl is TCardItem itemTpl && itemTpl.Enchantments != null)
                    {
                        itemTpl.Enchantments.TryGetValue(enchType, out enchDef);
                    }

                    if (enchDef != null)
                    {
                        if (enchDef.HiddenTags != null)
                        {
                            foreach (var ht in enchDef.HiddenTags)
                            {
                                if (!desiredHidden.Contains(ht)) enchAddedHidden.Add(ht);
                                desiredHidden.Add(ht);
                            }
                        }
                        if (enchDef.Tags != null)
                        {
                            foreach (var tg in enchDef.Tags)
                            {
                                if (!desiredTags.Contains(tg)) enchAddedTags.Add(tg);
                                desiredTags.Add(tg);
                            }
                        }
                    }
                }
            }

            // 清理此前因旧附魔同步追加、但当前模板与新附魔均已不再赋予的过期标签（防止黑曜石被护盾覆盖后残留 +Weapon 等）
            var cardTags = _cardTagsProp?.GetValue(card) as HashSet<ECardTag>;
            if (_enchDynamicSyncedTags.TryGetValue(iid, out var prevSynced))
            {
                if (card.HiddenTags != null && prevSynced.Hidden.Count > 0)
                {
                    foreach (var oldHt in prevSynced.Hidden)
                    {
                        if (!desiredHidden.Contains(oldHt)) card.HiddenTags.Remove(oldHt);
                    }
                }
                if (cardTags != null && prevSynced.Tags.Count > 0)
                {
                    foreach (var oldTg in prevSynced.Tags)
                    {
                        if (!desiredTags.Contains(oldTg)) cardTags.Remove(oldTg);
                    }
                }
            }

            if (card.HiddenTags != null)
            {
                foreach (var ht in desiredHidden) card.HiddenTags.Add(ht);
            }
            if (cardTags != null)
            {
                foreach (var tg in desiredTags) cardTags.Add(tg);
            }

            if (enchAddedTags.Count > 0 || enchAddedHidden.Count > 0)
            {
                _enchDynamicSyncedTags[iid] = (enchAddedTags, enchAddedHidden);
            }
            else
            {
                _enchDynamicSyncedTags.Remove(iid);
            }
        }
        catch { }
    }

    private static void HintServiceListHintsPrefix(object me, ref IEnumerable allCards)
    {
        try
        {
            if (me is ICard meCard)
            {
                SyncClientCardTagsAndHiddenTags(meCard);
            }

            if (allCards == null) return;

            var filtered = new List<object>();
            foreach (var obj in allCards)
            {
                if (obj == null) continue;
                if (obj is ICard card)
                {
                    SyncClientCardTagsAndHiddenTags(card);
                    // 仅保留已有归属者（玩家/对手）的存活卡牌或自身，彻底排除无主商店商品误触发 TargetSystem 回落到玩家手牌
                    bool isSelf = ReferenceEquals(obj, me) || (me is ICard mc && card.GetInstanceId() == mc.GetInstanceId());
                    if (!isSelf)
                    {
                        if (card.Owner == null || card.State != ECardState.Alive)
                            continue;
                        if (card.Type == ECardType.Item && card.Section != EInventorySection.Hand && card.Section != EInventorySection.Stash)
                            continue;
                    }
                }
                filtered.Add(obj);
            }

            if (_clientCardType != null)
            {
                var typedList = Activator.CreateInstance(typeof(List<>).MakeGenericType(_clientCardType)) as IList;
                if (typedList != null)
                {
                    foreach (var item in filtered)
                    {
                        if (_clientCardType.IsInstanceOfType(item))
                        {
                            typedList.Add(item);
                        }
                    }
                    allCards = typedList;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.HintServiceListHintsPrefix: " + ex.Message);
        }
    }

    private static void HintServiceListHintsPostfix(object me, ref IEnumerable __result)
    {
        try
        {
            if (__result == null || me is not ICard meCard) return;
            var hintType = AccessTools.TypeByName("BazaarGameClient.Domain.Hints.Hint");
            if (hintType == null) return;

            var filteredList = Activator.CreateInstance(typeof(List<>).MakeGenericType(hintType)) as IList;
            if (filteredList == null) return;

            var selfId = meCard.GetInstanceId();
            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var h in __result)
            {
                if (h == null) continue;
                var targetObj = _hintCardProp?.GetValue(h) ?? _hintCardField?.GetValue(h);
                if (targetObj is ICard targetCard)
                {
                    if (ReferenceEquals(targetObj, me) || targetCard.GetInstanceId() == selfId)
                        continue;

                    string key = targetCard.GetInstanceId() + "|" + (GameInspector.GetMember(h, "Type")?.ToString() ?? "");
                    if (!seenTargets.Add(key))
                        continue;
                }
                filteredList.Add(h);
            }

            __result = filteredList;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.HintServiceListHintsPostfix: " + ex.Message);
        }
    }

    private static void ItemControllerPreviewMovePostfix(object __instance, bool __result)
    {
        try
        {
            if (!__result || __instance == null || (__instance is UnityEngine.Object uo && uo == null))
                return;

            if (_itemCheckForSellZoneMethod?.Invoke(__instance, null) is not bool inSellZone || !inSellZone)
                return;

            var cardData = _cardCtrlCardDataProp?.GetValue(__instance) as ICard;
            var boardSection = _cardCtrlBoardSectionField?.GetValue(__instance);
            if (cardData == null || boardSection == null || _cardCtrlShowCardHintMethod == null)
                return;

            // 拖至出售区悬停时强制以卡牌实际 LeftSocketId 刷新高亮，避免使用相邻空槽位误算
            _cardCtrlShowCardHintMethod.Invoke(__instance, new object[] { cardData.LeftSocketId, boardSection });
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ItemControllerPreviewMovePostfix: " + ex.Message);
        }
    }

    // ── 12. EncounterController 子事件 / EncounterStep 立绘与 Tooltip 补全 (#19) ──

    private static void PatchEncounterController(Harmony harmony)
    {
        try
        {
            _encounterControllerType = GameInspector.FindTypeSafe("EncounterController", "TheBazaar.EncounterController");
            _encounterAssetDataSoType = GameInspector.FindTypeSafe(
                "EncounterAssetDataSO",
                "TheBazaar.Assets.Scripts.ScriptableObjectsScripts.EncounterAssetDataSO");
            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");

            if (_encounterControllerType != null)
            {
                const BindingFlags allInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _encCurrentEncounterDataObjectProp = _encounterControllerType.GetProperty("CurrentEncounterDataObject", allInst);
                _encCardDataProp = FindPropertyInHierarchy(_encounterControllerType, "CardData");
                _encIsCardVisibleProp = FindPropertyInHierarchy(_encounterControllerType, "IsCardVisible");
                _encIsLockedToSocketProp = FindPropertyInHierarchy(_encounterControllerType, "IsLockedToSocket");
                _encBoxColliderProp = FindPropertyInHierarchy(_encounterControllerType, "BoxCollider");
                _encInitTooltipMethod = FindMethodInHierarchy(_encounterControllerType, "InitializeCardTooltipData", Type.EmptyTypes);
                _encCardTooltipDataField = FindFieldInHierarchy(_encounterControllerType, "_cardTooltipData");
                _encIsInitializedField = FindFieldInHierarchy(_encounterControllerType, "_isInitialized");
                _encCardVisualsField = FindFieldInHierarchy(_encounterControllerType, "cardVisuals");
                _encShowCardMethod = AccessTools.Method(_encounterControllerType, "ShowCard", new[] { typeof(bool) });

                if (_encounterAssetDataSoType != null)
                {
                    _encSetupEncounterControllerMethod = AccessTools.Method(
                        _encounterControllerType,
                        "SetupEncounterController",
                        new[] { _encounterAssetDataSoType });
                }
                _encSetupEncounterControllerMethod ??= AccessTools.Method(_encounterControllerType, "SetupEncounterController");

                var setupMethod = cardType != null
                    ? AccessTools.Method(_encounterControllerType, "Setup", new[] { cardType })
                    : AccessTools.Method(_encounterControllerType, "Setup");
                if (setupMethod != null)
                {
                    harmony.Patch(
                        setupMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPrefix))),
                        postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(EncounterSetupPostfix))));
                    _patchedCount++;
                }

                var loadEncMethod = cardType != null
                    ? AccessTools.Method(_encounterControllerType, "LoadEncounterData", new[] { cardType, typeof(ITCard) })
                    : AccessTools.Method(_encounterControllerType, "LoadEncounterData");
                if (loadEncMethod != null && _encounterAssetDataSoType != null)
                {
                    var expectedRet = typeof(Task<>).MakeGenericType(_encounterAssetDataSoType);
                    if (loadEncMethod.ReturnType == expectedRet)
                    {
                        var genericPostfix = typeof(CompatAuditPatch)
                            .GetMethod(nameof(LoadEncounterDataPostfixGeneric), BindingFlags.NonPublic | BindingFlags.Static)
                            ?.MakeGenericMethod(_encounterAssetDataSoType);
                        if (genericPostfix != null)
                        {
                            harmony.Patch(loadEncMethod, postfix: new HarmonyMethod(genericPostfix));
                            _patchedCount++;
                        }
                    }
                }

                if (_encSetupEncounterControllerMethod != null)
                {
                    harmony.Patch(
                        _encSetupEncounterControllerMethod,
                        prefix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(SetupEncounterControllerPrefix))));
                    _patchedCount++;
                }
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _bmEncounterAssetSoField = bmType.GetField("_encounterAssetDataSO", BindingFlags.NonPublic | BindingFlags.Instance);
                _bmUpdateEncounterVisMethod = AccessTools.Method(bmType, "UpdateEncounterVisualization", Type.EmptyTypes);
            }

            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType != null)
            {
                _encDataGetStaticMethod = dataType.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);

                foreach (var resetName in new[] { "ResetStatics", "ResetEncounter" })
                {
                    var rm = AccessTools.Method(dataType, resetName, Type.EmptyTypes);
                    if (rm != null)
                    {
                        harmony.Patch(rm, postfix: new HarmonyMethod(
                            AccessTools.Method(typeof(CompatAuditPatch), nameof(DataResetEncounterCachePostfix))));
                        _patchedCount++;
                    }
                }
            }

            var tipDataType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.CardTooltipData");
            if (tipDataType != null)
            {
                var getTitleMethod = AccessTools.Method(tipDataType, "GetTitle", Type.EmptyTypes);
                if (getTitleMethod != null)
                {
                    harmony.Patch(getTitleMethod, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipGetTitlePostfix))));
                    _patchedCount++;
                }
            }

            var tipHandlerType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipTypeHandler");
            if (tipHandlerType != null)
            {
                var renderPassiveMethod = AccessTools.Method(tipHandlerType, "RenderPassiveEffects");
                if (renderPassiveMethod != null)
                {
                    harmony.Patch(renderPassiveMethod, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CompatAuditPatch), nameof(CardTooltipRenderPassiveEffectsPostfix))));
                    _patchedCount++;
                }
            }

            var tipCtrlType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipController");
            if (tipCtrlType != null)
            {
                _tooltipCtrlRenderPassiveMethod = AccessTools.Method(tipCtrlType, "RenderPassiveEffectTextBlock");
                _tooltipCtrlQuestDataField = FindFieldInHierarchy(tipCtrlType, "_questData");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchEncounterController: " + ex.Message);
        }
    }

    // ── 13. PrizePassChallengesView 空安全保护（消除对局结束回大厅 NRE 与 Uploading Crash Report 卡顿）──

    private static void PatchPrizePassChallengesGuard(Harmony harmony)
    {
        try
        {
            var viewType = GameInspector.FindTypeSafe("TheBazaar.UI.PrizePassChallengesView");
            if (viewType == null) return;

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(PrizePassChallengesSanitizePrefix)));
            var fin = new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer)));

            foreach (var name in new[] { "Awake", "RefreshChallenges", "ShowChallengesProgress", "OnChallengesUpdated" })
            {
                foreach (var m in viewType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name != name) continue;
                    var ps = m.GetParameters();
                    bool hasRespArg = ps.Length > 0
                        && ps[0].ParameterType == typeof(BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse);
                    harmony.Patch(m, prefix: hasRespArg ? prefix : null, finalizer: fin);
                    _patchedCount++;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchPrizePassChallengesGuard: " + ex.Message);
        }
    }

    private static bool PrizePassChallengesSanitizePrefix(ref BazaarGameShared.TempoNet.Responses.PlayerChallengesResponse __0)
    {
        try
        {
            if (__0.dailyChallenges == null || __0.weeklyChallenges == null)
            {
                __0 = __0 with
                {
                    dailyChallenges = __0.dailyChallenges ?? Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>(),
                    weeklyChallenges = __0.weeklyChallenges ?? Array.Empty<BazaarGameShared.TempoNet.Responses.ChallengeProgress>(),
                };
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static PropertyInfo FindPropertyInHierarchy(Type type, string propName)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null) return p;
        }
        return null;
    }

    private static MethodInfo FindMethodInHierarchy(Type type, string methodName, Type[] argTypes)
    {
        for (var t = type; t != null; t = t.BaseType)
        {
            var m = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, argTypes, null);
            if (m != null) return m;
        }
        return null;
    }

    private static bool IsUnityObjectAlive(object obj)
    {
        if (obj == null) return false;
        if (obj is UnityEngine.Object uo) return uo != null;
        return true;
    }

    private static void ClearParentEncounterCache(string reason)
    {
        try
        {
            lock (_encounterCacheLock)
            {
                _cachedParentEncounterTemplateId = Guid.Empty;
                _cachedParentEncounterArtKey = null;
                _cachedParentEncounterAsset = null;
                _cachedParentEncounterTitle = null;
                _cachedParentEncounterDesc = null;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.ClearParentEncounterCache({reason}): {ex.Message}");
        }
    }

    private static bool HasValidArtKey(ITCard tpl)
        => tpl != null && !string.IsNullOrEmpty(tpl.ArtKey) && !string.Equals(tpl.ArtKey, "Invalid", StringComparison.OrdinalIgnoreCase);

    private static string ResolveLocalizedTextValue(object localizableTextObj)
    {
        if (localizableTextObj == null) return null;
        try
        {
            if (_tooltipGetLocalizedTextMethod == null)
            {
                var extType = GameInspector.FindTypeSafe("TheBazaar.Tooltips.TooltipExtensions");
                _tooltipGetLocalizedTextMethod = extType?.GetMethod(
                    "GetLocalizedText",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { localizableTextObj.GetType() },
                    null);
            }
            if (_tooltipGetLocalizedTextMethod != null)
            {
                var res = _tooltipGetLocalizedTextMethod.Invoke(null, new[] { localizableTextObj }) as string;
                if (!string.IsNullOrWhiteSpace(res)) return res;
            }
        }
        catch { }

        return GameInspector.GetMember(localizableTextObj, "Text") as string;
    }

    private static string ResolveTemplateTitleZh(ITCard tpl)
    {
        if (tpl == null) return null;
        try
        {
            var loc = GameInspector.GetMember(tpl, "Localization");
            var titleObj = loc != null ? GameInspector.GetMember(loc, "Title") : null;
            string t = ResolveLocalizedTextValue(titleObj);
            return string.IsNullOrWhiteSpace(t) ? null : t.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveTemplateDescriptionZh(ITCard tpl)
    {
        if (tpl == null) return null;
        try
        {
            var loc = GameInspector.GetMember(tpl, "Localization");
            if (loc == null) return null;

            var descObj = GameInspector.GetMember(loc, "Description");
            string d = ResolveLocalizedTextValue(descObj);
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();

            if (GameInspector.GetMember(loc, "Tooltips") is IEnumerable tips)
            {
                foreach (var tip in tips)
                {
                    if (tip == null) continue;
                    var contentObj = GameInspector.GetMember(tip, "Content");
                    string tipText = ResolveLocalizedTextValue(contentObj);
                    if (!string.IsNullOrWhiteSpace(tipText)) return tipText.Trim();
                }
            }

            var flavorObj = GameInspector.GetMember(loc, "FlavorText");
            string flavor = ResolveLocalizedTextValue(flavorObj);
            if (!string.IsNullOrWhiteSpace(flavor)) return flavor.Trim();
        }
        catch { }
        return null;
    }

    private static ITCard ResolveCardTemplateById(Guid templateId)
    {
        if (templateId == Guid.Empty) return null;
        try
        {
            if (_encDataGetStaticMethod == null)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                _encDataGetStaticMethod = dataType?.GetMethod("GetStatic", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            }
            var staticMgr = _encDataGetStaticMethod?.Invoke(null, null);
            if (staticMgr == null) return null;

            _encStaticGetCardByIdMethod ??= staticMgr.GetType().GetMethod(
                "GetCardById",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Guid) },
                null);
            return _encStaticGetCardByIdMethod?.Invoke(staticMgr, new object[] { templateId }) as ITCard;
        }
        catch
        {
            return null;
        }
    }

    private static void CacheParentEncounterIfRoot(object cardData, ITCard tpl, object encounterAsset)
    {
        if (cardData == null || !IsUnityObjectAlive(encounterAsset)) return;
        try
        {
            if (GameInspector.GetMember(cardData, "Type") is not ECardType cardType)
                return;

            // 仅缓存真正的事件/强化基座根遭遇，绝不把战斗或 PvP 头像误缓存为事件兜底图
            if (cardType != ECardType.EventEncounter && cardType != ECardType.PedestalEncounter)
                return;

            Guid tplId = GameInspector.GetMember(cardData, "TemplateId") is Guid g ? g : Guid.Empty;
            tpl ??= GameInspector.GetMember(cardData, "Template") as ITCard ?? ResolveCardTemplateById(tplId);

            bool ownArtValid = HasValidArtKey(tpl);
            lock (_encounterCacheLock)
            {
                // 若当前只是沿用旧父图的无 ArtKey 子事件，切勿覆盖原有的根父事件模板与描述
                if (!ownArtValid && IsUnityObjectAlive(_cachedParentEncounterAsset))
                    return;

                if (tplId != Guid.Empty) _cachedParentEncounterTemplateId = tplId;
                if (ownArtValid) _cachedParentEncounterArtKey = tpl.ArtKey;
                _cachedParentEncounterAsset = encounterAsset;

                string title = ResolveTemplateTitleZh(tpl);
                if (!string.IsNullOrWhiteSpace(title)) _cachedParentEncounterTitle = title;

                string desc = ResolveTemplateDescriptionZh(tpl);
                if (!string.IsNullOrWhiteSpace(desc)) _cachedParentEncounterDesc = desc;
            }
        }
        catch { }
    }

    private static void DataResetEncounterCachePostfix()
    {
        ClearParentEncounterCache("Data.Reset");
    }

    private static bool EncounterSetupPrefix(object __instance, object cardData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || cardData == null)
            {
                __result = Task.CompletedTask;
                return false;
            }

            if (GameInspector.GetMember(cardData, "Type") is ECardType cardType
                && (cardType == ECardType.PvpEncounter || cardType == ECardType.CombatEncounter))
            {
                ClearParentEncounterCache("EncounterSetup:" + cardType);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EncounterSetupPrefix: " + ex.Message);
        }
        return true;
    }

    private static void EncounterSetupPostfix(object __instance, object cardData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || cardData == null || __result == null) return;
            __result = WrapEncounterSetupTask(__result, __instance, cardData);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.EncounterSetupPostfix: " + ex.Message);
        }
    }

    private static bool SetupEncounterControllerPrefix(object __instance, object encounterData, ref Task __result)
    {
        try
        {
            if (!IsUnityObjectAlive(__instance) || encounterData == null)
            {
                __result = Task.CompletedTask;
                return false;
            }

            var cardData = _encCardDataProp?.GetValue(__instance);
            if (cardData != null)
            {
                if (GameInspector.GetMember(cardData, "Type") is ECardType cardType
                    && cardType == ECardType.PvpEncounter)
                {
                    ClearParentEncounterCache("SetupEncounterController:Pvp");
                    return true;
                }
                CacheParentEncounterIfRoot(cardData, null, encounterData);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SetupEncounterControllerPrefix: " + ex.Message);
        }
        return true;
    }

    private static void LoadEncounterDataPostfixGeneric<TAsset>(
        object cardData,
        object cardTemplate,
        ref Task<TAsset> __result) where TAsset : class
    {
        try
        {
            if (__result == null) return;
            __result = WrapLoadEncounterDataTaskCore(__result, cardData, cardTemplate);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.LoadEncounterDataPostfix: " + ex.Message);
        }
    }

    private static async Task<TAsset> WrapLoadEncounterDataTaskCore<TAsset>(
        Task<TAsset> innerTask,
        object cardData,
        object cardTemplate) where TAsset : class
    {
        TAsset loaded = null;
        try
        {
            if (innerTask != null)
            {
                loaded = await innerTask;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapLoadEncounterData inner: " + ex.Message);
        }

        if (IsUnityObjectAlive(loaded))
        {
            CacheParentEncounterIfRoot(cardData, cardTemplate as ITCard, loaded);
            return loaded;
        }

        try
        {
            if (cardData == null || GameInspector.GetMember(cardData, "Type") is not ECardType cardType)
                return null;

            if (cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter
                && cardType != ECardType.EncounterStep)
            {
                return null;
            }

            var fallback = await ResolveFallbackEncounterAssetAsync(cardData, cardTemplate as ITCard);
            if (fallback is TAsset typedFallback && IsUnityObjectAlive(typedFallback))
            {
                Interlocked.Increment(ref _encounterPortraitFallbackCount);
                return typedFallback;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapLoadEncounterData fallback: " + ex.Message);
        }
        return null;
    }

    private static async Task WrapEncounterSetupTask(Task innerTask, object controller, object cardData)
    {
        try
        {
            if (innerTask != null)
            {
                await innerTask;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapEncounterSetupTask inner: " + ex.Message);
        }

        try
        {
            if (!IsUnityObjectAlive(controller) || cardData == null)
                return;

            // 对象池可能已复用该 EncounterController，若 CardData 已变更则立即退出
            var activeCardData = _encCardDataProp?.GetValue(controller);
            if (!ReferenceEquals(activeCardData, cardData))
                return;

            // 1. 兜底确保非 PvP 遭遇与 EncounterStep 已初始化 Tooltip 数据
            var cardTypeObj = GameInspector.GetMember(cardData, "Type");
            if (cardTypeObj is not ECardType cardType)
                return;

            if (cardType != ECardType.PvpEncounter)
            {
                var tipData = _encCardTooltipDataField?.GetValue(controller);
                if (tipData == null)
                {
                    _encInitTooltipMethod?.Invoke(controller, null);
                    tipData = _encCardTooltipDataField?.GetValue(controller);
                    if (tipData != null && _encIsInitializedField != null)
                    {
                        _encIsInitializedField.SetValue(controller, true);
                    }
                    if (tipData != null)
                    {
                        Interlocked.Increment(ref _encounterTooltipFallbackCount);
                    }
                }
            }

            if (cardType == ECardType.PvpEncounter || cardType == ECardType.CombatEncounter)
                return;

            // 2. 若原生 Setup 因 EncounterStep 跳过加载或子事件无 ArtKey 导致未加载立绘，则自动补全父级 EncounterAssetDataSO
            var currentAsset = _encCurrentEncounterDataObjectProp?.GetValue(controller);
            if (IsUnityObjectAlive(currentAsset))
            {
                CacheParentEncounterIfRoot(cardData, null, currentAsset);
                return;
            }

            Guid tplId = GameInspector.GetMember(cardData, "TemplateId") is Guid g ? g : Guid.Empty;
            var tpl = GameInspector.GetMember(cardData, "Template") as ITCard ?? ResolveCardTemplateById(tplId);
            var fallbackAsset = await ResolveFallbackEncounterAssetAsync(cardData, tpl);
            if (!IsUnityObjectAlive(fallbackAsset))
                return;

            if (!IsUnityObjectAlive(controller))
                return;
            activeCardData = _encCardDataProp?.GetValue(controller);
            if (!ReferenceEquals(activeCardData, cardData))
                return;
            if (IsUnityObjectAlive(_encCurrentEncounterDataObjectProp?.GetValue(controller)))
                return;

            if (_encSetupEncounterControllerMethod != null)
            {
                var setupTask = _encSetupEncounterControllerMethod.Invoke(controller, new[] { fallbackAsset }) as Task;
                if (setupTask != null)
                {
                    await setupTask;
                }
                Interlocked.Increment(ref _encounterPortraitFallbackCount);
            }

            if (!IsUnityObjectAlive(controller))
                return;

            // 3. 确保顶部中央肖像框可见且允许悬停触发 Tooltip
            bool lockedToSocket = _encIsLockedToSocketProp?.GetValue(controller) is bool l && l;
            bool isStorageOpen = GetStaticDataMember("IsStorageOpen") is bool st && st;
            if (lockedToSocket && !isStorageOpen)
            {
                bool isVisible = _encIsCardVisibleProp?.GetValue(controller) is bool vis && vis;
                var visuals = _encCardVisualsField?.GetValue(controller);
                if (!isVisible && IsUnityObjectAlive(visuals))
                {
                    _encShowCardMethod?.Invoke(controller, new object[] { true });
                }
                var boxObj = _encBoxColliderProp?.GetValue(controller);
                if (IsUnityObjectAlive(boxObj))
                {
                    _encColliderEnabledProp ??= boxObj.GetType().GetProperty("enabled", BindingFlags.Public | BindingFlags.Instance);
                    if (_encColliderEnabledProp != null && _encColliderEnabledProp.GetValue(boxObj) is bool colEnabled && !colEnabled)
                    {
                        _encColliderEnabledProp.SetValue(boxObj, true);
                    }
                }

                var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
                if (ReferenceEquals(curEncCtrl, controller))
                {
                    TryUpdateBoardEncounterVisualizationSafe();
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.WrapEncounterSetupTask fallback: " + ex.Message);
        }
    }

    private static async Task<object> ResolveFallbackEncounterAssetAsync(object cardData, ITCard currentTpl)
    {
        object cachedAsset;
        Guid cachedTplId;
        string cachedArtKey;
        lock (_encounterCacheLock)
        {
            cachedAsset = _cachedParentEncounterAsset;
            cachedTplId = _cachedParentEncounterTemplateId;
            cachedArtKey = _cachedParentEncounterArtKey;
        }

        if (IsUnityObjectAlive(cachedAsset))
            return cachedAsset;

        // 1. 从当前存活的 CurrentEncounterController 读取非战斗事件资产
        var curEncCtrl = GetStaticDataMember("CurrentEncounterController");
        if (IsUnityObjectAlive(curEncCtrl))
        {
            var curCard = _encCardDataProp?.GetValue(curEncCtrl);
            if (GameInspector.GetMember(curCard, "Type") is ECardType curType
                && (curType == ECardType.EventEncounter || curType == ECardType.PedestalEncounter))
            {
                var curSo = _encCurrentEncounterDataObjectProp?.GetValue(curEncCtrl);
                if (IsUnityObjectAlive(curSo))
                {
                    CacheParentEncounterIfRoot(curCard, null, curSo);
                    return curSo;
                }
            }
        }

        // 2. 从 BoardManager._encounterAssetDataSO 读取已预载的根遭遇资产
        var bm = GameInspector.GetBoardManagerCached();
        if (IsUnityObjectAlive(bm) && _bmEncounterAssetSoField != null)
        {
            var bmSo = _bmEncounterAssetSoField.GetValue(bm);
            if (IsUnityObjectAlive(bmSo))
            {
                lock (_encounterCacheLock)
                {
                    _cachedParentEncounterAsset = bmSo;
                }
                return bmSo;
            }
        }

        // 3. 根据缓存的父事件 ArtKey / TemplateId 重新通过 AssetLoader 异步加载
        string targetArtKey = cachedArtKey;
        if (string.IsNullOrEmpty(targetArtKey) && cachedTplId != Guid.Empty)
        {
            var parentTpl = ResolveCardTemplateById(cachedTplId);
            if (HasValidArtKey(parentTpl))
            {
                targetArtKey = parentTpl.ArtKey;
            }
        }

        // 4. 若断线重连直接进入子事件而尚未缓存父图，尝试从 Data.Entities 中定位当前在场的根事件卡（非自身 EncounterStep）
        if (string.IsNullOrEmpty(targetArtKey))
        {
            var entities = GameInspector.GetDataEntities();
            if (entities != null)
            {
                foreach (DictionaryEntry kv in entities)
                {
                    var ent = kv.Value;
                    if (ent == null || ReferenceEquals(ent, cardData)) continue;
                    if (GameInspector.GetMember(ent, "Type") is not ECardType entType) continue;
                    if (entType != ECardType.EventEncounter && entType != ECardType.PedestalEncounter) continue;
                    if (GameInspector.GetMember(ent, "Owner") != null) continue;

                    Guid entTplId = GameInspector.GetMember(ent, "TemplateId") is Guid eg ? eg : Guid.Empty;
                    var entTpl = GameInspector.GetMember(ent, "Template") as ITCard ?? ResolveCardTemplateById(entTplId);
                    if (HasValidArtKey(entTpl))
                    {
                        targetArtKey = entTpl.ArtKey;
                        break;
                    }
                }
            }
        }

        // 5. 若当前模板本身是 EventEncounter/PedestalEncounter 且含有效 ArtKey（排除 EncounterStep 的 Reward .png 贴图），尝试直接加载
        if (string.IsNullOrEmpty(targetArtKey)
            && cardData != null
            && GameInspector.GetMember(cardData, "Type") is ECardType selfType
            && (selfType == ECardType.EventEncounter || selfType == ECardType.PedestalEncounter)
            && HasValidArtKey(currentTpl))
        {
            targetArtKey = currentTpl.ArtKey;
        }

        if (string.IsNullOrEmpty(targetArtKey) || string.Equals(targetArtKey, "Invalid", StringComparison.OrdinalIgnoreCase))
            return null;

        var loaded = await LoadEncounterAssetByAddressAsync(targetArtKey);
        if (IsUnityObjectAlive(loaded))
        {
            lock (_encounterCacheLock)
            {
                _cachedParentEncounterArtKey = targetArtKey;
                _cachedParentEncounterAsset = loaded;
            }
            return loaded;
        }
        return null;
    }

    private static async Task<object> LoadEncounterAssetByAddressAsync(string artKey)
    {
        if (string.IsNullOrEmpty(artKey) || _encounterAssetDataSoType == null)
            return null;

        try
        {
            if (_encServicesGetAssetLoaderMethod == null)
            {
                var servicesType = GameInspector.FindTypeSafe("TheBazaar.AppFramework.Services");
                var assetLoaderType = GameInspector.FindTypeSafe("AssetLoader", "TheBazaar.AssetLoader");
                var getGeneric = servicesType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Get" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (getGeneric != null && assetLoaderType != null)
                {
                    _encServicesGetAssetLoaderMethod = getGeneric.MakeGenericMethod(assetLoaderType);
                }
            }

            var assetLoader = _encServicesGetAssetLoaderMethod?.Invoke(null, null);
            if (!IsUnityObjectAlive(assetLoader))
                return null;

            if (_encAssetLoaderLoadMethod == null)
            {
                var loadDef = assetLoader.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m =>
                        m.Name == "LoadAssetAsyncByAddress"
                        && m.IsGenericMethodDefinition
                        && m.GetParameters().Length == 1
                        && m.GetParameters()[0].ParameterType == typeof(string));
                if (loadDef != null)
                {
                    _encAssetLoaderLoadMethod = loadDef.MakeGenericMethod(_encounterAssetDataSoType);
                }
            }

            if (_encAssetLoaderLoadMethod == null)
                return null;

            var taskObj = _encAssetLoaderLoadMethod.Invoke(assetLoader, new object[] { artKey }) as Task;
            if (taskObj == null)
                return null;

            await taskObj;
            var resultProp = taskObj.GetType().GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);
            return resultProp?.GetValue(taskObj);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.LoadEncounterAssetByAddressAsync({artKey}): {ex.Message}");
            return null;
        }
    }

    private static void TryUpdateBoardEncounterVisualizationSafe()
    {
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm) || _bmUpdateEncounterVisMethod == null) return;
            _ = _bmUpdateEncounterVisMethod.Invoke(bm, null);
        }
        catch { }
    }

    private static void CardTooltipGetTitlePostfix(object __instance, ref string __result)
    {
        try
        {
            if (__instance == null || !string.IsNullOrWhiteSpace(__result)) return;

            var cardInst = GameInspector.GetMember(__instance, "CardInstance");
            if (cardInst == null || GameInspector.GetMember(cardInst, "Type") is not ECardType cardType)
                return;
            if (cardType != ECardType.EncounterStep
                && cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter)
            {
                return;
            }

            var tpl = GameInspector.GetMember(__instance, "CardTemplate") as ITCard;
            string title = ResolveTemplateTitleZh(tpl);
            if (string.IsNullOrWhiteSpace(title))
            {
                lock (_encounterCacheLock)
                {
                    title = _cachedParentEncounterTitle;
                }
            }
            if (string.IsNullOrWhiteSpace(title) && tpl != null && !string.IsNullOrWhiteSpace(tpl.InternalName))
            {
                title = tpl.InternalName.Trim();
            }
            if (string.IsNullOrWhiteSpace(title))
            {
                title = "奇遇事件";
            }

            __result = title;
            Interlocked.Increment(ref _encounterTooltipFallbackCount);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardTooltipGetTitlePostfix: " + ex.Message);
        }
    }

    private static void CardTooltipRenderPassiveEffectsPostfix(object controller, object tooltipData)
    {
        try
        {
            if (!IsUnityObjectAlive(controller) || tooltipData == null || _tooltipCtrlRenderPassiveMethod == null)
                return;

            var cardInst = GameInspector.GetMember(tooltipData, "CardInstance");
            if (cardInst == null || GameInspector.GetMember(cardInst, "Type") is not ECardType cardType)
                return;

            // 仅对事件/基座/子步骤生效；战斗怪物的 Description 本身故意为空，由怪物盘面渲染器负责展示
            if (cardType != ECardType.EncounterStep
                && cardType != ECardType.EventEncounter
                && cardType != ECardType.PedestalEncounter)
            {
                return;
            }

            var tpl = GameInspector.GetMember(tooltipData, "CardTemplate") as ITCard;
            if (tpl == null) return;

            var loc = GameInspector.GetMember(tpl, "Localization");
            var descObj = loc != null ? GameInspector.GetMember(loc, "Description") : null;
            string nativeDesc = ResolveLocalizedTextValue(descObj);
            if (!string.IsNullOrWhiteSpace(nativeDesc))
                return;

            string fallbackDesc = ResolveTemplateDescriptionZh(tpl);
            if (string.IsNullOrWhiteSpace(fallbackDesc))
            {
                Guid curTplId = GameInspector.GetMember(cardInst, "TemplateId") is Guid g ? g : Guid.Empty;
                string parentTitle;
                string parentDesc;
                Guid parentTplId;
                lock (_encounterCacheLock)
                {
                    parentTitle = _cachedParentEncounterTitle;
                    parentDesc = _cachedParentEncounterDesc;
                    parentTplId = _cachedParentEncounterTemplateId;
                }

                string curTitle = ResolveTemplateTitleZh(tpl) ?? tpl.InternalName;
                if (!string.IsNullOrWhiteSpace(parentDesc) && (cardType == ECardType.EncounterStep || curTplId != parentTplId))
                {
                    fallbackDesc = (!string.IsNullOrWhiteSpace(parentTitle)
                                    && !string.IsNullOrWhiteSpace(curTitle)
                                    && !string.Equals(parentTitle, curTitle, StringComparison.Ordinal))
                        ? $"{parentTitle}：{parentDesc}"
                        : parentDesc;
                }
                else if (cardType == ECardType.EncounterStep || !HasValidArtKey(tpl))
                {
                    if (!string.IsNullOrWhiteSpace(parentTitle)
                        && !string.IsNullOrWhiteSpace(curTitle)
                        && !string.Equals(parentTitle, curTitle, StringComparison.Ordinal))
                    {
                        fallbackDesc = $"{parentTitle} · {curTitle}";
                    }
                    else if (!string.IsNullOrWhiteSpace(curTitle))
                    {
                        fallbackDesc = $"奇遇阶段：{curTitle}";
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(fallbackDesc))
                return;

            var questData = _tooltipCtrlQuestDataField?.GetValue(controller);
            _tooltipCtrlRenderPassiveMethod.Invoke(controller, new[] { fallbackDesc, questData });
            Interlocked.Increment(ref _encounterTooltipFallbackCount);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CardTooltipRenderPassiveEffectsPostfix: " + ex.Message);
        }
    }
}

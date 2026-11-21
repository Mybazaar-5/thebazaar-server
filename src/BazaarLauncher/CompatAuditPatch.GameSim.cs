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

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
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
        try
        {
            if (evt == null || frame == null || evt.TransformedCards == null)
                return false;

            EnsureCombatFrameCardUpdates(evt, frame);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TryCacheTransformedCardPrefix: " + ex.Message);
        }
        return true;
    }

    private static bool CombatProcessEventsPrefix(ICombatSimEvent combatSimEvent, CombatSimFrame frame)
    {
        try
        {
            if (combatSimEvent is CombatSimEventCardTransformed evt)
            {
                if (evt.TransformedCards == null || frame == null)
                    return false;

                EnsureCombatFrameCardUpdates(evt, frame);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.CombatProcessEventsPrefix: " + ex.Message);
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

    // P1-3 & P2 加固：按运行时真实类型分桶缓存战斗 VFX 反射成员，彻底消除跨类型强转风险与每次触发无缓存反射
    private sealed class VfxEventDataMemberBag
    {
        public PropertyInfo ShouldPlay;
        public PropertyInfo ActionType;
        public PropertyInfo TargetCard;
        public PropertyInfo SourceCard;
        public PropertyInfo TargetInstanceId;
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo> _vfxEventDataPropByType = new();
    private static readonly ConcurrentDictionary<Type, VfxEventDataMemberBag> _vfxDataBagByType = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> _vfxManagerSoFieldByType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _vfxIsActionMappedByType = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> _vfxCardInstanceIdPropByType = new();

    private static bool GenerateCombatVfxPrefix(object __instance, object eventData)
    {
        try
        {
            if (__instance == null || eventData == null) return false;

            // 与 PerfOptimizerModule 联动：高并发 VFX 安全限流
            if (BazaarLauncher.Modules.PerfOptimizerModule.ShouldThrottleCombatVfx())
            {
                return false;
            }

            var evDataProp = _vfxEventDataPropByType.GetOrAdd(
                eventData.GetType(),
                static t => t.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance));
            var data = evDataProp?.GetValue(eventData);
            if (data == null) return false;

            var dataBag = _vfxDataBagByType.GetOrAdd(data.GetType(), static dt => new VfxEventDataMemberBag
            {
                ShouldPlay = dt.GetProperty("VFXShouldPlay", BindingFlags.Public | BindingFlags.Instance),
                ActionType = dt.GetProperty("ActionType", BindingFlags.Public | BindingFlags.Instance),
                TargetCard = dt.GetProperty("TargetCard", BindingFlags.Public | BindingFlags.Instance),
                SourceCard = dt.GetProperty("SourceCard", BindingFlags.Public | BindingFlags.Instance),
                TargetInstanceId = dt.GetProperty("TargetInstanceId", BindingFlags.Public | BindingFlags.Instance),
            });

            if (dataBag.ShouldPlay != null && dataBag.ShouldPlay.GetValue(data) is bool play && !play)
                return true;

            var mgrSoField = _vfxManagerSoFieldByType.GetOrAdd(
                __instance.GetType(),
                static mt => mt.GetField("vfxManagerSO", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public));
            var vfxSo = mgrSoField?.GetValue(__instance);
            if (vfxSo == null) return false;

            var actionType = dataBag.ActionType?.GetValue(data);
            var targetCard = dataBag.TargetCard?.GetValue(data);

            var isMappedMethod = _vfxIsActionMappedByType.GetOrAdd(
                vfxSo.GetType(),
                static st => st.GetMethod("IsActionAttributeMapped", BindingFlags.Public | BindingFlags.Instance));
            if (isMappedMethod != null && actionType != null)
            {
                bool isMapped = (bool)isMappedMethod.Invoke(vfxSo, new[] { actionType });
                if (isMapped && targetCard == null)
                {
                    var srcCard = dataBag.SourceCard?.GetValue(data);
                    if (srcCard != null)
                    {
                        var srcIdProp = _vfxCardInstanceIdPropByType.GetOrAdd(
                            srcCard.GetType(),
                            static ct => ct.GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance));
                        if (srcIdProp != null && dataBag.TargetInstanceId != null && dataBag.TargetInstanceId.CanWrite)
                        {
                            dataBag.TargetInstanceId.SetValue(data, srcIdProp.GetValue(srcCard));
                            if (dataBag.TargetCard?.GetValue(data) != null) return true;
                        }
                    }

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
        try
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
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ProcessCardTransformedEventsPrefix: " + ex.Message);
        }
    }

    private static void ProcessTransformRevertedEventsPrefix(
        IEnumerable<GameSimEventCardTransformReverted> events,
        ref Dictionary<string, SimUpdateCard> simUpdates)
    {
        try
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
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ProcessTransformRevertedEventsPrefix: " + ex.Message);
        }
    }

    private static FieldInfo _cachedDataEntitiesField;

    private static void GameSimProcessEventsPrefix(ref IList<IGameSimEvent> events)
    {
        if (events == null || events.Count == 0) return;

        try
        {
            // 推进 GameSim 版本号，使悬停标签同步与 ShowCardHint 仅在牌局真正变动时刷新
            Interlocked.Increment(ref _gameSimVersion);
            ResetShowCardHintDedupeCache();

            if (_cachedDataEntitiesField == null)
            {
                var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
                _cachedDataEntitiesField = dataType?.GetField("Entities", BindingFlags.Public | BindingFlags.Static);
            }
            var entities = _cachedDataEntitiesField?.GetValue(null) as IDictionary;
            if (entities == null) return;

            var spawnedInBatch = new HashSet<string>(StringComparer.Ordinal);
            bool hasSkillEquipped = false;
            bool shouldArmHour6Watchdog = false;
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev is GameSimEventCardSpawned sp && !string.IsNullOrEmpty(sp.InstanceId))
                {
                    spawnedInBatch.Add(sp.InstanceId);
                }
                else if (ev is GameSimEventCardTransformed tr && tr.TransformedCards != null)
                {
                    foreach (var tc in tr.TransformedCards)
                    {
                        if (!string.IsNullOrEmpty(tc?.InstanceId)) spawnedInBatch.Add(tc.InstanceId);
                    }
                }
                else if (ev is GameSimEventStateTransitioned stEv)
                {
                    if (stEv.ToState is ERunState.Choice or ERunState.TournamentPvpChoice)
                    {
                        shouldArmHour6Watchdog = true;
                    }
                    if (stEv.ToState is ERunState.Choice or ERunState.Combat or ERunState.PVPCombat or ERunState.EndRunVictory or ERunState.EndRunDefeat
                        || string.IsNullOrEmpty(stEv.CurrentEncounterId))
                    {
                        ClearParentEncounterCache("GameSimTransition:" + stEv.ToState);
                        if (stEv.ToState is ERunState.EndRunVictory or ERunState.EndRunDefeat)
                        {
                            _enchDynamicSyncedTags.Clear();
                            _cardTagSyncStamps.Clear();
                            _lastTriggeredPvpSnapshot = default;
                        }
                    }
                    BazaarLauncherPlugin.Host?.EventBus.Publish(new BazaarModSdk.RunStateChangedModEvent(string.Empty, stEv.ToState.ToString()));
                }
                else if (ev is GameSimEventCardDealt)
                {
                    shouldArmHour6Watchdog = true;
                }
                else if (ev is GameSimEventPlayerSkillEquipped)
                {
                    hasSkillEquipped = true;
                }
            }

            if (shouldArmHour6Watchdog)
            {
                ScheduleHour6ChoiceRevealWatchdogIfNeeded();
            }

            if (!hasSkillEquipped) return;

            // P1-4 修复：若上游传入定长数组或只读集合，克隆为 List<IGameSimEvent> 并回写 ref events，杜绝 NotSupportedException
            if (events.IsReadOnly || events is Array)
            {
                events = new List<IGameSimEvent>(events);
            }

            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i] is GameSimEventPlayerSkillEquipped eq)
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

    private static void OnRunConfigCacheUpdatedPostfix()
    {
        try
        {
            RefreshActiveMainMenuUiFromData();
        }
        catch { }
    }

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
            var respType = GameInspector.FindTypeSafe(
                "BazaarGameShared.TempoNet.Responses.RunRewardLevelResponse",
                "BazaarGameShared.TempoNet.Models.RunRewardLevelResponse");
            var curType = GameInspector.FindTypeSafe("BazaarGameShared.TempoNet.Models.CurrencyValue");
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

    // ── 7. SoundEventListener.OnCardMoved：Controller 尚未生成时防 NRE（Top 10：缓存 _soundManager 字段）──

    private static FieldInfo _soundManagerField;

    private static void PatchSoundEventListener(Harmony harmony)
    {
        try
        {
            var soundType = GameInspector.FindTypeSafe("TheBazaar.SoundEventListener");
            if (soundType == null) return;

            _soundManagerField = soundType.GetField("_soundManager", BindingFlags.NonPublic | BindingFlags.Instance);

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

            if (_soundManagerField == null)
            {
                var soundType = GameInspector.FindTypeSafe("TheBazaar.SoundEventListener");
                _soundManagerField = soundType?.GetField("_soundManager", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            var soundMgr = (_soundManagerField != null && _soundManagerField.DeclaringType.IsInstanceOfType(__instance))
                ? _soundManagerField.GetValue(__instance)
                : GameInspector.GetMember(__instance, "_soundManager");
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
        try
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
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.GetCardContainerBoundsPrefix: " + ex.Message);
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

    private static (int Day, int Hour, int OppMaxHealth) ReadRunDayHourAndOppHealth()
    {
        int day = 0;
        int hour = 0;
        int maxHealth = 0;
        try
        {
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                var dayObj = GameInspector.GetMember(run, "Day");
                if (dayObj != null) day = Convert.ToInt32(dayObj);
                var hourObj = GameInspector.GetMember(run, "Hour");
                if (hourObj != null) hour = Convert.ToInt32(hourObj);

                var opp = GameInspector.GetMember(run, "Opponent");
                if (opp != null && GameInspector.GetMember(opp, "Attributes") is IDictionary attrs && attrs.Contains(EPlayerAttributeType.HealthMax))
                {
                    var hm = attrs[EPlayerAttributeType.HealthMax];
                    if (hm != null) maxHealth = Convert.ToInt32(hm);
                }
            }
        }
        catch { }
        return (day, hour, maxHealth);
    }

    private static PvpOpponentSnapshot BuildPvpSnapshot(object simPvpOpponent)
    {
        var (day, hour, maxHealth) = ReadRunDayHourAndOppHealth();
        if (simPvpOpponent == null)
        {
            return new PvpOpponentSnapshot
            {
                HadOpponent = false,
                Day = day,
                Hour = hour,
                MaxHealth = maxHealth,
            };
        }

        return new PvpOpponentSnapshot
        {
            HadOpponent = true,
            AccountId = ReadPvpAccountId(simPvpOpponent),
            Hero = GameInspector.GetMember(simPvpOpponent, "Hero")?.ToString(),
            Name = GameInspector.GetMember(simPvpOpponent, "Name")?.ToString(),
            Day = day,
            Hour = hour,
            MaxHealth = maxHealth,
        };
    }

    private static void ApplySimPvpOpponentPrefix(out PvpOpponentSnapshot __state)
    {
        __state = default;
        try
        {
            var cur = _dataSimPvpOpponentProp?.GetValue(null);
            __state = BuildPvpSnapshot(cur);
        }
        catch { }
    }

    private static void ApplySimPvpOpponentPostfix(PvpOpponentSnapshot __state)
    {
        try
        {
            var cur = _dataSimPvpOpponentProp?.GetValue(null);
            if (cur == null) return;

            var nowSnap = BuildPvpSnapshot(cur);

            // 原版 Data.cs:297 仅在前后 accountId 均非空且不相等时触发；此处记录并避免重复触发
            bool nativeTriggered = !string.IsNullOrEmpty(__state.AccountId)
                && !string.IsNullOrEmpty(nowSnap.AccountId)
                && !string.Equals(__state.AccountId, nowSnap.AccountId, StringComparison.OrdinalIgnoreCase);
            if (nativeTriggered)
            {
                _lastTriggeredPvpSnapshot = nowSnap;
                return;
            }

            // 修复 Hour 6 跨天同 accountId+hero 去重误跳：
            // 引入 (Day, Hour, AccountId, Hero, MaxHealth, Name) 多维比对，确保跨天抽到同 FallbackGhost/锦标赛同对手时必刷新头像与血条！
            var last = _lastTriggeredPvpSnapshot;
            bool firstValid = !__state.HadOpponent && (!string.IsNullOrEmpty(nowSnap.AccountId) || !string.IsNullOrEmpty(nowSnap.Hero));
            bool becameNonEmptyAccount = string.IsNullOrEmpty(__state.AccountId) && !string.IsNullOrEmpty(nowSnap.AccountId);
            bool heroChanged = !string.IsNullOrEmpty(nowSnap.Hero)
                && !string.Equals(__state.Hero, nowSnap.Hero, StringComparison.OrdinalIgnoreCase);
            bool dayChanged = nowSnap.Day > 0 && last.Day != nowSnap.Day;
            bool hourEnteredPvp = nowSnap.Hour > 0 && last.Hour != nowSnap.Hour && nowSnap.Hour >= GetHoursInADaySafe();
            bool healthChanged = nowSnap.MaxHealth > 0 && last.MaxHealth != nowSnap.MaxHealth;
            bool nameChanged = !string.IsNullOrEmpty(nowSnap.Name)
                && !string.Equals(last.Name, nowSnap.Name, StringComparison.Ordinal);

            if (!firstValid && !becameNonEmptyAccount && !heroChanged && !dayChanged && !hourEnteredPvp && !healthChanged && !nameChanged)
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
                _lastTriggeredPvpSnapshot = nowSnap;
                int cnt = Interlocked.Increment(ref _pvpOpponentChangedFixedCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch: triggered missing Events.PvpOpponentChanged (count={cnt}, day={nowSnap.Day}, hour={nowSnap.Hour}, hero={nowSnap.Hero}, accountId={nowSnap.AccountId}, hp={nowSnap.MaxHealth})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ApplySimPvpOpponentPostfix: " + ex.Message);
        }
    }

    // ── 9.5 TransitionInComponent Hour 6 竞态死锁根治（三道防线） ─────────────
    // 根因：进入 Hour 6 Choice 第 1 帧（或 Hour 5->6 LevelUp 隐藏选牌器时）TryUpdateClock() 立即把
    // _waitForNewDayTransition 置为 true；若异步 SpawnCards([pvpCard]) 晚于首帧 TryRevealCards()，
    // 后续每帧 ShouldWaitForNewDayTransition() 因 Hour == HoursInADay (6) 永远返回 true，永久冻结翻牌！

    private static FieldInfo _tiWaitForNewDayField;
    private static PropertyInfo _tiIsProcessingProp;
    private static MethodInfo _tiTryRevealCardsMethod;
    private static PropertyInfo _dataHoursInADayProp;
    private static PropertyInfo _dataNewDayCtrlProp;
    private static PropertyInfo _newDayIsActiveProp;
    private static PropertyInfo _newDayHasPendingProp;
    private static MethodInfo _bmHasCardsToRevealMethod;
    private static int _hour6WatchdogSeq;

    private static void PatchTransitionInHour6Guard(Harmony harmony)
    {
        try
        {
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType != null)
            {
                _dataHoursInADayProp = dataType.GetProperty("HoursInADay", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                _dataNewDayCtrlProp = dataType.GetProperty("NewDayTransitionController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                _bmHasCardsToRevealMethod = AccessTools.Method(bmType, "HasCardsToReveal", Type.EmptyTypes);
            }

            var tiType = GameInspector.FindTypeSafe("TheBazaar.SequenceFramework.VisualSequences.TransitionInComponent");
            if (tiType == null) return;

            _tiWaitForNewDayField = tiType.GetField("_waitForNewDayTransition", BindingFlags.NonPublic | BindingFlags.Instance);
            _tiIsProcessingProp = tiType.GetProperty("IsProcessing", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _tiTryRevealCardsMethod = AccessTools.Method(tiType, "TryRevealCards", Type.EmptyTypes);

            var shouldWaitMethod = AccessTools.Method(tiType, "ShouldWaitForNewDayTransition", Type.EmptyTypes);
            if (shouldWaitMethod != null)
            {
                harmony.Patch(
                    shouldWaitMethod,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(TransitionInShouldWaitForNewDayPostfix))));
                _patchedCount++;
            }

            var tryUpdateClockMethod = AccessTools.Method(tiType, "TryUpdateClock", Type.EmptyTypes);
            if (tryUpdateClockMethod != null)
            {
                harmony.Patch(
                    tryUpdateClockMethod,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CompatAuditPatch), nameof(TransitionInTryUpdateClockPostfix))));
                _patchedCount++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchTransitionInHour6Guard: " + ex.Message);
        }
    }

    private static int GetHoursInADaySafe()
    {
        try
        {
            if (_dataHoursInADayProp != null && _dataHoursInADayProp.GetValue(null) is int h && h > 0)
            {
                return h;
            }
        }
        catch { }
        return 6;
    }

    private static bool IsNewDayTransitionActiveOrPending()
    {
        try
        {
            var ctrl = _dataNewDayCtrlProp?.GetValue(null);
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return false;

            Type ct = ctrl.GetType();
            if (_newDayIsActiveProp == null || (_newDayIsActiveProp.DeclaringType != null && !_newDayIsActiveProp.DeclaringType.IsInstanceOfType(ctrl)))
            {
                _newDayIsActiveProp = ct.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Instance);
            }
            if (_newDayHasPendingProp == null || (_newDayHasPendingProp.DeclaringType != null && !_newDayHasPendingProp.DeclaringType.IsInstanceOfType(ctrl)))
            {
                _newDayHasPendingProp = ct.GetProperty("HasPendingDayChanged", BindingFlags.Public | BindingFlags.Instance);
            }

            if (_newDayIsActiveProp?.GetValue(ctrl) is bool active && active) return true;
            if (_newDayHasPendingProp?.GetValue(ctrl) is bool pending && pending) return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Hour 6 死锁防线 1：在 Hour 6 Choice / TournamentPvpChoice 阶段且不存在真正新一天切场（!IsActive &amp;&amp; !HasPendingDayChanged）时，
    /// 强制解除误设的 _waitForNewDayTransition 锁并返回 false，允许异步发牌后的 PvP 镜像正常翻牌！
    /// </summary>
    private static void TransitionInShouldWaitForNewDayPostfix(object __instance, ref bool __result)
    {
        try
        {
            if (!__result || __instance == null) return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState) return;
            if (runState != ERunState.Choice && runState != ERunState.TournamentPvpChoice) return;

            var run = GameInspector.GetDataRun();
            if (run == null) return;
            object hourObj = GameInspector.GetMember(run, "Hour");
            if (hourObj == null) return;
            int curHour = Convert.ToInt32(hourObj);
            int hoursInDay = GetHoursInADaySafe();
            if (curHour != hoursInDay) return;

            // 若确实正在或即将跨日切场，严格尊重原版等待，绝不提前翻牌
            if (IsNewDayTransitionActiveOrPending()) return;

            if (_tiWaitForNewDayField != null && _tiWaitForNewDayField.DeclaringType.IsInstanceOfType(__instance))
            {
                _tiWaitForNewDayField.SetValue(__instance, false);
            }
            __result = false;

            int cnt = Interlocked.Increment(ref _hour6TransitionUnblockedCount);
            if (cnt <= 5 || cnt % 20 == 0)
            {
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch: unblocked Hour {curHour} Choice TransitionIn deadlock (count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TransitionInShouldWaitForNewDayPostfix: " + ex.Message);
        }
    }

    /// <summary>
    /// Hour 6 死锁防线 2：在 Hour 5-&gt;6 离场升级或 Hour 6 中途升级（LevelUp 状态）时，
    /// 禁止 TryUpdateClock() 提前将 _waitForNewDayTransition 置为 true，彻底根治升级完回 Hour 6 必现空桌！
    /// </summary>
    private static void TransitionInTryUpdateClockPostfix(object __instance)
    {
        try
        {
            if (__instance == null || _tiWaitForNewDayField == null) return;
            if (!_tiWaitForNewDayField.DeclaringType.IsInstanceOfType(__instance)) return;
            if (_tiWaitForNewDayField.GetValue(__instance) is not bool waiting || !waiting) return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState) return;
            if (runState != ERunState.LevelUp && runState != ERunState.Choice && runState != ERunState.TournamentPvpChoice)
                return;

            var run = GameInspector.GetDataRun();
            if (run == null) return;
            object hourObj = GameInspector.GetMember(run, "Hour");
            if (hourObj == null) return;
            if (Convert.ToInt32(hourObj) != GetHoursInADaySafe()) return;

            if (IsNewDayTransitionActiveOrPending()) return;

            _tiWaitForNewDayField.SetValue(__instance, false);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.TransitionInTryUpdateClockPostfix: " + ex.Message);
        }
    }

    /// <summary>
    /// Hour 6 死锁防线 3（1.2s 兜底翻牌看门狗）：进入 Hour 6 Choice 后 1.2s 检查，
    /// 若 Data.Entities 中存在 PvpEncounter 且 BoardManager.HasCardsToReveal() 为真但仍未翻牌，主动触发一次 TryRevealCards()。
    /// </summary>
    private static void ScheduleHour6ChoiceRevealWatchdogIfNeeded()
    {
        try
        {
            int seq = Interlocked.Increment(ref _hour6WatchdogSeq);
            _ = Task.Delay(1200).ContinueWith(_ =>
            {
                if (Volatile.Read(ref _hour6WatchdogSeq) != seq) return;
                BazaarLauncher.Core.MainThreadDispatcher.Enqueue(() => RunHour6ChoiceRevealWatchdogOnMain(seq));
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void RunHour6ChoiceRevealWatchdogOnMain(int expectedSeq)
    {
        try
        {
            if (Volatile.Read(ref _hour6WatchdogSeq) != expectedSeq) return;

            var dataState = GameInspector.GetDataCurrentState();
            if (GameInspector.GetMember(dataState, "StateName") is not ERunState runState) return;
            if (runState != ERunState.Choice && runState != ERunState.TournamentPvpChoice) return;

            var run = GameInspector.GetDataRun();
            if (run == null) return;
            object hourObj = GameInspector.GetMember(run, "Hour");
            if (hourObj == null || Convert.ToInt32(hourObj) != GetHoursInADaySafe()) return;

            if (IsNewDayTransitionActiveOrPending()) return;

            // 确认 Data.Entities 中确实存在 PvpEncounter
            bool hasPvpCard = false;
            var entities = GameInspector.GetDataEntities();
            if (entities != null)
            {
                foreach (DictionaryEntry kv in entities)
                {
                    if (kv.Value is ICard c && c.Type == ECardType.PvpEncounter)
                    {
                        hasPvpCard = true;
                        break;
                    }
                }
            }
            if (!hasPvpCard) return;

            var bm = GameInspector.GetBoardManagerCached();
            if (!IsUnityObjectAlive(bm) || _bmHasCardsToRevealMethod == null) return;
            if (_bmHasCardsToRevealMethod.Invoke(bm, null) is not bool hasToReveal || !hasToReveal) return;

            var ti = GetStaticDataMember("TransitionIn");
            if (!IsUnityObjectAlive(ti)) return;

            if (_tiIsProcessingProp != null && _tiIsProcessingProp.DeclaringType.IsInstanceOfType(ti)
                && _tiIsProcessingProp.GetValue(ti) is bool proc && proc)
            {
                return;
            }

            if (_tiWaitForNewDayField != null && _tiWaitForNewDayField.DeclaringType.IsInstanceOfType(ti))
            {
                _tiWaitForNewDayField.SetValue(ti, false);
            }

            if (_tiTryRevealCardsMethod != null && _tiTryRevealCardsMethod.DeclaringType.IsInstanceOfType(ti))
            {
                _ = _tiTryRevealCardsMethod.Invoke(ti, null);
                int cnt = Interlocked.Increment(ref _hour6WatchdogRevealCount);
                BazaarLauncherPlugin.Log?.LogInfo(
                    $"CompatAuditPatch: Hour 6 watchdog triggered fallback TryRevealCards() for PvpEncounter (count={cnt})");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RunHour6ChoiceRevealWatchdogOnMain: " + ex.Message);
        }
    }

}

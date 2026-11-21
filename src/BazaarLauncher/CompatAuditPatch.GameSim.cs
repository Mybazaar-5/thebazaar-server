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

    // P1-3 修复：缓存战斗 VFX 热路径全部反射成员，彻底消除每次触发 6 次无缓存反射与装箱
    private static PropertyInfo _vfxEventDataProp;
    private static PropertyInfo _vfxShouldPlayProp;
    private static FieldInfo _vfxManagerSoField;
    private static PropertyInfo _vfxActionTypeProp;
    private static PropertyInfo _vfxTargetCardProp;
    private static PropertyInfo _vfxSourceCardProp;
    private static PropertyInfo _vfxTargetInstanceIdProp;
    private static PropertyInfo _vfxCardInstanceIdProp;
    private static MethodInfo _vfxIsActionMappedMethod;

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

            _vfxEventDataProp ??= eventData.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
            var data = _vfxEventDataProp?.GetValue(eventData);
            if (data == null) return false;

            Type dataType = data.GetType();
            _vfxShouldPlayProp ??= dataType.GetProperty("VFXShouldPlay", BindingFlags.Public | BindingFlags.Instance);
            if (_vfxShouldPlayProp != null && _vfxShouldPlayProp.GetValue(data) is bool play && !play)
                return true;

            _vfxManagerSoField ??= __instance.GetType().GetField("vfxManagerSO", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
            var vfxSo = _vfxManagerSoField?.GetValue(__instance);
            if (vfxSo == null) return false;

            _vfxActionTypeProp ??= dataType.GetProperty("ActionType", BindingFlags.Public | BindingFlags.Instance);
            _vfxTargetCardProp ??= dataType.GetProperty("TargetCard", BindingFlags.Public | BindingFlags.Instance);
            var actionType = _vfxActionTypeProp?.GetValue(data);
            var targetCard = _vfxTargetCardProp?.GetValue(data);

            _vfxIsActionMappedMethod ??= vfxSo.GetType().GetMethod("IsActionAttributeMapped", BindingFlags.Public | BindingFlags.Instance);
            if (_vfxIsActionMappedMethod != null && actionType != null)
            {
                bool isMapped = (bool)_vfxIsActionMappedMethod.Invoke(vfxSo, new[] { actionType });
                if (isMapped && targetCard == null)
                {
                    _vfxSourceCardProp ??= dataType.GetProperty("SourceCard", BindingFlags.Public | BindingFlags.Instance);
                    var srcCard = _vfxSourceCardProp?.GetValue(data);
                    if (srcCard != null)
                    {
                        _vfxCardInstanceIdProp ??= srcCard.GetType().GetProperty("InstanceId", BindingFlags.Public | BindingFlags.Instance);
                        _vfxTargetInstanceIdProp ??= dataType.GetProperty("TargetInstanceId", BindingFlags.Public | BindingFlags.Instance);
                        if (_vfxCardInstanceIdProp != null && _vfxTargetInstanceIdProp != null && _vfxTargetInstanceIdProp.CanWrite)
                        {
                            _vfxTargetInstanceIdProp.SetValue(data, _vfxCardInstanceIdProp.GetValue(srcCard));
                            if (_vfxTargetCardProp?.GetValue(data) != null) return true;
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

    private static FieldInfo _cachedDataEntitiesField;

    private static void GameSimProcessEventsPrefix(ref IList<IGameSimEvent> events)
    {
        if (events == null || events.Count == 0) return;

        try
        {
            if (_cachedDataEntitiesField == null)
            {
                var dataType = AccessTools.TypeByName("TheBazaar.Data");
                _cachedDataEntitiesField = dataType?.GetField("Entities", BindingFlags.Public | BindingFlags.Static);
            }
            var entities = _cachedDataEntitiesField?.GetValue(null) as IDictionary;
            if (entities == null) return;

            var spawnedInBatch = new HashSet<string>(StringComparer.Ordinal);
            bool hasSkillEquipped = false;
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
                    if (stEv.ToState is ERunState.Choice or ERunState.Combat or ERunState.PVPCombat or ERunState.EndRunVictory or ERunState.EndRunDefeat
                        || string.IsNullOrEmpty(stEv.CurrentEncounterId))
                    {
                        ClearParentEncounterCache("GameSimTransition:" + stEv.ToState);
                        if (stEv.ToState is ERunState.EndRunVictory or ERunState.EndRunDefeat)
                        {
                            _enchDynamicSyncedTags.Clear();
                        }
                    }
                    BazaarLauncherPlugin.Host?.EventBus.Publish(new BazaarModSdk.RunStateChangedModEvent(string.Empty, stEv.ToState.ToString()));
                }
                else if (ev is GameSimEventPlayerSkillEquipped)
                {
                    hasSkillEquipped = true;
                }
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

}

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
using BazaarLauncher.Core;

namespace BazaarLauncher;

internal static partial class CompatAuditPatch
{
    // ── 1. CombatSimHandler：CardTransformed 防缺 Key & ExtractFrameEffects 兜底 ──

    private static void PatchCombatSimHandler(SafeHarmonyPatcher patcher)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.CombatSimHandler");
            if (t == null) return;

            var tryCache = AccessTools.Method(t, "TryCacheTransformedCard",
                new[] { typeof(CombatSimEventCardTransformed), typeof(CombatSimFrame) });
            if (tryCache != null)
            {
                patcher.TryPatchMethod(tryCache,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TryCacheTransformedCardPrefix)),
                    optional: true,
                    label: "Compat.GameSim.CombatSimHandler.TryCacheTransformedCard");
            }

            var processEvents = AccessTools.Method(t, "ProcessEvents",
                new[] { typeof(ICombatSimEvent), typeof(CombatSimFrame), typeof(List<string>) });
            if (processEvents != null)
            {
                patcher.TryPatchMethod(processEvents,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CombatProcessEventsPrefix)),
                    optional: true,
                    label: "Compat.GameSim.CombatSimHandler.ProcessEvents");
            }

            var extractEffects = AccessTools.Method(t, "ExtractFrameEffects",
                new[] { typeof(List<CombatSimFrame>), typeof(List<string>) });
            if (extractEffects != null)
            {
                patcher.TryPatchMethod(extractEffects,
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(ExtractFrameEffectsFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.CombatSimHandler.ExtractFrameEffects");
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
                RecordCompatHackHit("H-4", ref _hackH4TransformShellCount, "CombatFrame.OriginalCardShell:" + evt.OriginalInstanceId);
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
                RecordCompatHackHit("H-4", ref _hackH4TransformShellCount, "CombatFrame.TransformedCardShell:" + tc.InstanceId);
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

    private static void PatchVfxManager(SafeHarmonyPatcher patcher)
    {
        try
        {
            var vfxMgrType = AccessTools.TypeByName("TheBazaar.VFXManager");
            var evtType = AccessTools.TypeByName("TheBazaar.EffectTriggeredEvent");
            if (vfxMgrType == null || evtType == null) return;

            var genMethod = AccessTools.Method(vfxMgrType, "GenerateCombatVFX", new[] { evtType });
            if (genMethod != null)
            {
                patcher.TryPatchMethod(genMethod,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GenerateCombatVfxPrefix)),
                    optional: true,
                    label: "Compat.GameSim.VFXManager.GenerateCombatVFX");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.VFXManager: " + ex.Message);
        }
    }

    // P1-3 & P2-15 (O-5) 加固：按 CombatActionData.cs 精确字段/属性布局分桶缓存（VFXShouldPlay/ActionType/TargetInstanceId/IsCrit/HealthBefore/HealthAfter/VFXOverrideKey 为 FieldInfo，TargetCard/SourceCard 为 PropertyInfo）
    private sealed class VfxEventDataMemberBag
    {
        public FieldInfo ShouldPlayField;
        public PropertyInfo ShouldPlayProp;
        public FieldInfo ActionTypeField;
        public PropertyInfo ActionTypeProp;
        public FieldInfo TargetInstanceIdField;
        public PropertyInfo TargetInstanceIdProp;
        public FieldInfo IsCritField;
        public PropertyInfo IsCritProp;
        public FieldInfo HealthBeforeField;
        public FieldInfo HealthAfterField;
        public FieldInfo VfxOverrideKeyField;
        public PropertyInfo TargetCard;
        public PropertyInfo SourceCard;
    }

    private static readonly ConcurrentDictionary<Type, PropertyInfo> _vfxEventDataPropByType = new();
    private static readonly ConcurrentDictionary<Type, VfxEventDataMemberBag> _vfxDataBagByType = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo> _vfxManagerSoFieldByType = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> _vfxIsActionMappedByType = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo> _vfxCardInstanceIdPropByType = new();

    private static BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority ClassifyCombatVfxPriority(
        VfxEventDataMemberBag bag, object data, object actionTypeObj)
    {
        // 1. 暴击 / 致死 / 显式自定义覆盖 VFX：Critical（永不节流）
        bool isCrit = (bag.IsCritField?.GetValue(data) ?? bag.IsCritProp?.GetValue(data)) is bool c && c;
        if (isCrit)
            return BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Critical;

        if (bag.HealthBeforeField != null && bag.HealthAfterField != null
            && bag.HealthBeforeField.GetValue(data) is float hpBefore
            && bag.HealthAfterField.GetValue(data) is float hpAfter
            && hpBefore > 0f && hpAfter <= 0f)
        {
            return BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Critical;
        }

        if (bag.VfxOverrideKeyField?.GetValue(data) is string ovKey && !string.IsNullOrEmpty(ovKey))
            return BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Critical;

        if (actionTypeObj == null)
            return BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Minor;

        // 2. 按 TheBazaar.ActionType 官方枚举值分类（见 ActionType.cs）
        int code;
        try { code = Convert.ToInt32(actionTypeObj); }
        catch { return BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Minor; }

        return code switch
        {
            // Critical：摧毁/变形/升级/禁用/任务完成/开战/头像切换（低频重大演出）
            200 or 250 or 900 or 1000 or 1010 or 1400 or 11200
                => BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Critical,

            // Core：主激活战斗效果（伤害/护盾/治疗/灼烧/剧毒/再生/快乐/节奏/冻结/强制触发/最大生命变化/偷金/飞行）
            300 or 400 or 1600 or 1800 or 1900 or 2000 or 2100 or 2300 or 2400
                or 2600 or 3000 or 5000 or 6100 or 6200 or 11300 or 11301 or 11302
                => BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Core,

            // Minor：高频小粒子（光环/属性修改、充能/加速/减速/装填、移除类特效等）优先节流
            _ => BazaarLauncher.Modules.PerfOptimizerModule.CombatVfxPriority.Minor,
        };
    }

    private static bool GenerateCombatVfxPrefix(object __instance, object eventData)
    {
        try
        {
            if (__instance == null || eventData == null) return false;

            var evDataProp = _vfxEventDataPropByType.GetOrAdd(
                eventData.GetType(),
                static t => t.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance));
            var data = evDataProp?.GetValue(eventData);
            if (data == null) return false;

            var dataBag = _vfxDataBagByType.GetOrAdd(data.GetType(), static dt =>
            {
                const BindingFlags anyPub = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                return new VfxEventDataMemberBag
                {
                    ShouldPlayField = dt.GetField("VFXShouldPlay", anyPub),
                    ShouldPlayProp = dt.GetProperty("VFXShouldPlay", anyPub),
                    ActionTypeField = dt.GetField("ActionType", anyPub),
                    ActionTypeProp = dt.GetProperty("ActionType", anyPub),
                    TargetInstanceIdField = dt.GetField("TargetInstanceId", anyPub),
                    TargetInstanceIdProp = dt.GetProperty("TargetInstanceId", anyPub),
                    IsCritField = dt.GetField("IsCrit", anyPub),
                    IsCritProp = dt.GetProperty("IsCrit", anyPub),
                    HealthBeforeField = dt.GetField("HealthBefore", anyPub),
                    HealthAfterField = dt.GetField("HealthAfter", anyPub),
                    VfxOverrideKeyField = dt.GetField("VFXOverrideKey", anyPub),
                    TargetCard = dt.GetProperty("TargetCard", anyPub),
                    SourceCard = dt.GetProperty("SourceCard", anyPub),
                };
            });

            object shouldPlayObj = dataBag.ShouldPlayField != null
                ? dataBag.ShouldPlayField.GetValue(data)
                : dataBag.ShouldPlayProp?.GetValue(data);
            if (shouldPlayObj is bool play && !play)
                return true;

            var actionType = dataBag.ActionTypeField != null
                ? dataBag.ActionTypeField.GetValue(data)
                : dataBag.ActionTypeProp?.GetValue(data);

            // P2-15 (O-5)：分级保留 VFX 限流（Critical 永不丢弃、Core 高预算优先保留、Minor 高频小粒子率先节流）
            var priority = ClassifyCombatVfxPriority(dataBag, data, actionType);
            if (BazaarLauncher.Modules.PerfOptimizerModule.ShouldThrottleCombatVfx(priority))
            {
                return false;
            }

            var mgrSoField = _vfxManagerSoFieldByType.GetOrAdd(
                __instance.GetType(),
                static mt => mt.GetField("vfxManagerSO", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public));
            var vfxSo = mgrSoField?.GetValue(__instance);
            if (vfxSo == null) return false;

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
                        if (srcIdProp != null)
                        {
                            object srcIdVal = srcIdProp.GetValue(srcCard);
                            if (dataBag.TargetInstanceIdField != null)
                            {
                                dataBag.TargetInstanceIdField.SetValue(data, srcIdVal);
                                if (dataBag.TargetCard?.GetValue(data) != null)
                                {
                                    RecordCompatHackHit("H-3", ref _hackH3VfxTargetFallbackCount, "FallbackTargetToSource:" + actionType);
                                    return true;
                                }
                            }
                            else if (dataBag.TargetInstanceIdProp != null && dataBag.TargetInstanceIdProp.CanWrite)
                            {
                                dataBag.TargetInstanceIdProp.SetValue(data, srcIdVal);
                                if (dataBag.TargetCard?.GetValue(data) != null)
                                {
                                    RecordCompatHackHit("H-3", ref _hackH3VfxTargetFallbackCount, "FallbackTargetToSource:" + actionType);
                                    return true;
                                }
                            }
                        }
                    }

                    RecordCompatHackHit("H-3", ref _hackH3VfxTargetFallbackCount, "DroppedMissingTarget:" + actionType);
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

    private static void PatchGameSimHandler(SafeHarmonyPatcher patcher)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.GameSimHandler");
            if (t == null) return;

            var mTrans = AccessTools.Method(t, "ProcessCardTransformedEvents");
            if (mTrans != null)
            {
                patcher.TryPatchMethod(mTrans,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessCardTransformedEventsPrefix)),
                    optional: true,
                    label: "Compat.GameSim.GameSimHandler.ProcessCardTransformedEvents");
            }

            var mRev = AccessTools.Method(t, "ProcessTransformRevertedEvents");
            if (mRev != null)
            {
                patcher.TryPatchMethod(mRev,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ProcessTransformRevertedEventsPrefix)),
                    optional: true,
                    label: "Compat.GameSim.GameSimHandler.ProcessTransformRevertedEvents");
            }

            var mProc = AccessTools.Method(t, "ProcessEvents", new[] { typeof(IList<IGameSimEvent>) });
            if (mProc != null)
            {
                patcher.TryPatchMethod(mProc,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameSimProcessEventsPrefix)),
                    optional: true,
                    label: "Compat.GameSim.GameSimHandler.ProcessEvents");
            }

            // P1-43：同场景重开/重置对局数据时自主触发跨局静态字段清理，不依赖 PerfOptimizerModule
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            var resetRunData = dataType == null ? null : AccessTools.Method(dataType, "ResetRunData", Type.EmptyTypes);
            if (resetRunData != null)
            {
                patcher.TryPatchMethod(resetRunData,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(DataResetRunDataPostfix)),
                    optional: true,
                    label: "Compat.GameSim.Data.ResetRunData");
            }

            // P2-39：原生 GameStateHandler.InitializeSocketEffects 错写 Player.SocketEffects 而非 Player.Socket，
            // 挂载 Postfix 同步清理并写入 Player.Socket，杜绝局中 StateSync 同步后残留脏引用拒放
            var gshType = GameInspector.FindTypeSafe("TheBazaar.GameStateHandler");
            var initSocketEffects = gshType == null ? null : AccessTools.Method(gshType, "InitializeSocketEffects", Type.EmptyTypes);
            if (initSocketEffects != null)
            {
                _socketEffectCardRuntimeType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.SocketEffect");
                patcher.TryPatchMethod(initSocketEffects,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GameStateHandlerInitializeSocketEffectsPostfix)),
                    optional: true,
                    label: "Compat.GameSim.GameStateHandler.InitializeSocketEffects");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.GameSimHandler: " + ex.Message);
        }
    }

    private static Type _socketEffectCardRuntimeType;

    private static void GameStateHandlerInitializeSocketEffectsPostfix()
    {
        try
        {
            var run = GameInspector.GetDataRun() as IRun;
            var player = run?.GetPlayer();
            var entities = GameInspector.GetDataEntities();
            if (player?.Socket?.Container == null || entities == null)
                return;

            var container = player.Socket.Container;
            var sockets = container.Sockets;
            if (sockets != null)
            {
                for (int i = 0; i < sockets.Length; i++)
                {
                    var existing = sockets[i];
                    if (existing != null)
                    {
                        container.Remove(existing);
                    }
                }
            }

            foreach (DictionaryEntry kv in entities)
            {
                var val = kv.Value;
                if (val == null) continue;
                bool isSocketEffect = _socketEffectCardRuntimeType != null
                    ? _socketEffectCardRuntimeType.IsInstanceOfType(val)
                    : (val is ICard c && c.Type == ECardType.SocketEffect);
                if (!isSocketEffect) continue;

                if (val is ICard card && val is ISocketable socketable
                    && ReferenceEquals(card.Owner, player)
                    && card.LeftSocketId.HasValue)
                {
                    container.PlaceOnSocket(socketable, card.LeftSocketId.Value);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.GameStateHandlerInitializeSocketEffectsPostfix: " + ex.Message);
        }
    }

    private static void DataResetRunDataPostfix()
    {
        ClearRunScopedCaches("Data.ResetRunData");
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
                        RecordCompatHackHit("H-4", ref _hackH4TransformShellCount, "GameSim.CardTransformedShell:" + tc.InstanceId);
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
                    RecordCompatHackHit("H-4", ref _hackH4TransformShellCount, "GameSim.TransformRevertedShell:" + ev.OriginalCard.InstanceId);
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
            int preUnlockPlayerHandMask = 0;
            int preUnlockPlayerSocketMask = 0;
            for (int i = 0; i < events.Count; i++)
            {
                var ev = events[i];
                if (ev is GameSimEventSocketsUnlocked su && su.UnlockedSocketsBitmask != 0)
                {
                    // GameSimHandler.ProcessEvents 先执行 ProcessCardSpawnedEvents(:354) 再处理 SocketsUnlocked(:426)，
                    // 此处前置应用同批解锁掩码，防止 Resume 或扩容同帧发卡被 PlaceOnSocket 拒放
                    if (su.Section == EInventorySection.Hand)
                    {
                        preUnlockPlayerHandMask |= su.UnlockedSocketsBitmask;
                        preUnlockPlayerSocketMask |= su.UnlockedSocketsBitmask;
                    }
                }
                else if (ev is GameSimEventCardSpawned sp && !string.IsNullOrEmpty(sp.InstanceId))
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
                            ClearRunScopedCaches("GameSimTransition:" + stEv.ToState);
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

            if (preUnlockPlayerHandMask != 0 || preUnlockPlayerSocketMask != 0)
            {
                var run = GameInspector.GetDataRun();
                var player = run != null ? GameInspector.GetMember(run, "Player") : null;
                if (player != null)
                {
                    EnsurePlayerContainerSocketsUnlocked(player, preUnlockPlayerHandMask, unionOnly: true, socketMaskOverride: preUnlockPlayerSocketMask);
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
                        RecordCompatHackHit("H-5", ref _hackH5OrphanSkillDroppedCount, "OrphanPlayerSkillEquipped:" + id);
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

    // ── 3.5 SkillPresentationManager：重复 TemplateId 防锁盘 + 索引复位 + 贴图备份保护 (P1-44) ──

    private static FieldInfo _spmSkillProxyLookupField;
    private static FieldInfo _spmCurrentSkillEntryIndexField;
    private static PropertyInfo _spmIsUpdatingSkillBoardProp;
    private static MethodInfo _spmIsUpdatingSkillBoardSetter;
    private static FieldInfo _spmIsUpdatingSkillBoardBackingField;
    private static FieldInfo _spmTempBoardTextureField;
    [ThreadStatic] private static Stack<bool> _spmSkipOrAlreadyUpdatingStack;

    private static void PatchSkillPresentationManager(SafeHarmonyPatcher patcher)
    {
        try
        {
            var spmType = GameInspector.FindTypeSafe("TheBazaar.SkillPresentationManager", "SkillPresentationManager");
            if (spmType == null) return;

            _spmSkillProxyLookupField = spmType.GetField("_skillProxyLookup", BindingFlags.NonPublic | BindingFlags.Instance);
            _spmCurrentSkillEntryIndexField = spmType.GetField("_currentSkillEntryIndex", BindingFlags.NonPublic | BindingFlags.Instance);
            _spmIsUpdatingSkillBoardProp = spmType.GetProperty("IsUpdatingSkillBoard", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _spmIsUpdatingSkillBoardSetter = _spmIsUpdatingSkillBoardProp?.GetSetMethod(nonPublic: true);
            _spmIsUpdatingSkillBoardBackingField = spmType.GetField("<IsUpdatingSkillBoard>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
            _spmTempBoardTextureField = spmType.GetField("_tempBoardTexture", BindingFlags.NonPublic | BindingFlags.Instance);

            var cardType = GameInspector.FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
            var addMethod = cardType != null
                ? AccessTools.Method(spmType, "Add", new[] { cardType, typeof(bool) })
                : AccessTools.Method(spmType, "Add");
            if (addMethod != null && typeof(Task).IsAssignableFrom(addMethod.ReturnType))
            {
                patcher.TryPatchMethod(
                    addMethod,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationAddPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationAddFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.SkillPresentationManager.Add");
            }

            if (cardType != null)
            {
                var initMethod = AccessTools.Method(spmType, "Initialize", new[] { typeof(List<>).MakeGenericType(cardType) });
                if (initMethod != null)
                {
                    patcher.TryPatchMethod(
                        initMethod,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationInitializePrefix)),
                        optional: true,
                        label: "Compat.GameSim.SkillPresentationManager.Initialize");
                }
            }

            var removeSkills = AccessTools.Method(spmType, "RemoveSkills", Type.EmptyTypes);
            if (removeSkills != null)
            {
                patcher.TryPatchMethod(
                    removeSkills,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationRemoveSkillsPostfix)),
                    optional: true,
                    label: "Compat.GameSim.SkillPresentationManager.RemoveSkills");
            }

            var setTempTex = AccessTools.Method(spmType, "SetTempBoardTexture", new[] { typeof(Texture2D) });
            if (setTempTex != null)
            {
                patcher.TryPatchMethod(
                    setTempTex,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationSetTempTexturePrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationSetTempTexturePostfix)),
                    optional: true,
                    label: "Compat.GameSim.SkillPresentationManager.SetTempBoardTexture");
            }

            var resetTex = AccessTools.Method(spmType, "ResetBoardTextures", Type.EmptyTypes);
            if (resetTex != null)
            {
                patcher.TryPatchMethod(
                    resetTex,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationResetBoardTexturesPrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationResetBoardTexturesPostfix)),
                    optional: true,
                    label: "Compat.GameSim.SkillPresentationManager.ResetBoardTextures");
            }

            var setEntryTex = AccessTools.Method(spmType, "SetSkillEntryTexture", new[] { typeof(Texture2D) });
            if (setEntryTex != null)
            {
                patcher.TryPatchMethod(
                    setEntryTex,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(SkillPresentationSetSkillEntryTexturePostfix)),
                    optional: true,
                    label: "Compat.GameSim.SkillPresentationManager.SetSkillEntryTexture");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.PatchSkillPresentationManager: " + ex.Message);
        }
    }

    private static bool IsSkillBoardUpdating(object spm)
    {
        if (spm == null || _spmIsUpdatingSkillBoardProp == null) return false;
        try
        {
            return _spmIsUpdatingSkillBoardProp.GetValue(spm) is bool b && b;
        }
        catch { return false; }
    }

    private static void ResetSkillBoardUpdatingFlag(object spm)
    {
        if (spm == null || (spm is UnityEngine.Object uo && uo == null)) return;
        try
        {
            if (!IsSkillBoardUpdating(spm)) return;
            if (_spmIsUpdatingSkillBoardSetter != null)
            {
                _spmIsUpdatingSkillBoardSetter.Invoke(spm, new object[] { false });
            }
            else if (_spmIsUpdatingSkillBoardBackingField != null)
            {
                _spmIsUpdatingSkillBoardBackingField.SetValue(spm, false);
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.ResetSkillBoardUpdatingFlag: " + ex.Message);
        }
    }

    private static bool SkillPresentationAddPrefix(object __instance, object card, ref Task __result)
    {
        bool skipOrAlreadyUpdating = false;
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null) || card == null)
            {
                skipOrAlreadyUpdating = true;
                __result = Task.CompletedTask;
                (_spmSkipOrAlreadyUpdatingStack ??= new Stack<bool>(2)).Push(skipOrAlreadyUpdating);
                return false;
            }

            if (GameInspector.GetMember(card, "TemplateId") is Guid tplId
                && tplId != Guid.Empty
                && _spmSkillProxyLookupField?.GetValue(__instance) is IDictionary lookup
                && lookup.Contains(tplId))
            {
                BazaarLauncherPlugin.Log?.LogWarning(
                    $"CompatAuditPatch[P1-44]: 拦截重复 TemplateId ({tplId}) 进入 SkillPresentationManager.Add，避免 ArgumentException 锁死 IsUpdatingSkillBoard");
                skipOrAlreadyUpdating = true;
                __result = Task.CompletedTask;
                (_spmSkipOrAlreadyUpdatingStack ??= new Stack<bool>(2)).Push(skipOrAlreadyUpdating);
                return false;
            }

            skipOrAlreadyUpdating = IsSkillBoardUpdating(__instance);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SkillPresentationAddPrefix: " + ex.Message);
        }

        (_spmSkipOrAlreadyUpdatingStack ??= new Stack<bool>(2)).Push(skipOrAlreadyUpdating);
        return true;
    }

    private static Exception SkillPresentationAddFinalizer(object __instance, Exception __exception, ref Task __result)
    {
        bool skipOrAlreadyUpdating = false;
        if (_spmSkipOrAlreadyUpdatingStack != null && _spmSkipOrAlreadyUpdatingStack.Count > 0)
        {
            skipOrAlreadyUpdating = _spmSkipOrAlreadyUpdatingStack.Pop();
        }

        if (skipOrAlreadyUpdating)
            return __exception;

        if (__exception != null)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-44]: SkillPresentationManager.Add 同步异常已兜底归零 IsUpdatingSkillBoard: {__exception.Message}");
            ResetSkillBoardUpdatingFlag(__instance);
            __result = Task.CompletedTask;
            return null;
        }

        if (__result == null)
        {
            ResetSkillBoardUpdatingFlag(__instance);
            __result = Task.CompletedTask;
            return null;
        }

        if (__result.IsCompleted)
        {
            if (__result.IsFaulted)
            {
                var innerEx = __result.Exception?.GetBaseException();
                BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-44]: SkillPresentationManager.Add 同步 Fault 已兜底归零 IsUpdatingSkillBoard: {innerEx?.Message}");
                __result = Task.CompletedTask;
            }
            ResetSkillBoardUpdatingFlag(__instance);
            return null;
        }

        __result = WrapSkillPresentationAddTask(__result, __instance);
        return null;
    }

    private static async Task WrapSkillPresentationAddTask(Task inner, object spm)
    {
        try
        {
            await inner;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-44]: SkillPresentationManager.Add 异步异常已拦截并归零 IsUpdatingSkillBoard: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            PostToMainThread(() => ResetSkillBoardUpdatingFlag(spm));
        }
    }

    private static void SkillPresentationInitializePrefix(IList skills)
    {
        try
        {
            if (skills == null || skills.Count <= 1) return;
            var seen = new HashSet<Guid>();
            for (int i = 0; i < skills.Count;)
            {
                var sk = skills[i];
                if (sk == null)
                {
                    skills.RemoveAt(i);
                    continue;
                }
                if (GameInspector.GetMember(sk, "TemplateId") is Guid tplId && tplId != Guid.Empty)
                {
                    if (!seen.Add(tplId))
                    {
                        BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch[P1-44]: SkillPresentationManager.Initialize 剔除重复 TemplateId ({tplId})");
                        skills.RemoveAt(i);
                        continue;
                    }
                }
                i++;
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.SkillPresentationInitializePrefix: " + ex.Message);
        }
    }

    private static void SkillPresentationRemoveSkillsPostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            _spmCurrentSkillEntryIndexField?.SetValue(__instance, 0);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L909", diagEx); }
    }

    private static void SkillPresentationSetTempTexturePrefix(object __instance, out Texture2D __state)
    {
        __state = null;
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            __state = _spmTempBoardTextureField?.GetValue(__instance) as Texture2D;
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L920", diagEx); }
    }

    private static void SkillPresentationSetTempTexturePostfix(object __instance, Texture2D __state)
    {
        try
        {
            // 若调用前已存在原始贴图备份，则恢复原备份，避免连续 SetTempBoardTexture 覆写丢失原板贴图
            if (__state != null && __instance != null && !(__instance is UnityEngine.Object uo && uo == null))
            {
                _spmTempBoardTextureField?.SetValue(__instance, __state);
            }
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L933", diagEx); }
    }

    private static bool SkillPresentationResetBoardTexturesPrefix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return false;
            var backup = _spmTempBoardTextureField?.GetValue(__instance) as Texture2D;
            return backup != null;
        }
        catch
        {
            return true;
        }
    }

    private static void SkillPresentationResetBoardTexturesPostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            _spmTempBoardTextureField?.SetValue(__instance, null);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L957", diagEx); }
    }

    private static void SkillPresentationSetSkillEntryTexturePostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            _spmTempBoardTextureField?.SetValue(__instance, null);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L967", diagEx); }
    }

    // ── 4. CardQuestGroupData：任务条目越界、实例空安全与跨局 _subscribedGroups 泄漏清理 (P2-42) ──

    private static FieldInfo _questSubscribedGroupsField;
    private static MethodInfo _questSubscribedGroupsClearMethod;
    private static FieldInfo[] _cardQuestsDataStaticEventFields;

    internal static void ClearCardQuestSubscribedGroups(string reason)
    {
        try
        {
            if (_questSubscribedGroupsField == null)
            {
                var qType = GameInspector.FindTypeSafe("TheBazaar.CardQuestGroupData", "CardQuestGroupData");
                _questSubscribedGroupsField = qType?.GetField("_subscribedGroups", BindingFlags.NonPublic | BindingFlags.Static);
            }

            var setObj = _questSubscribedGroupsField?.GetValue(null);
            if (setObj != null)
            {
                _questSubscribedGroupsClearMethod ??= setObj.GetType().GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                _questSubscribedGroupsClearMethod?.Invoke(setObj, null);
            }

            // 同步清空 CardQuestsData 的 4 个静态事件（全库仅由 CardQuestGroupData 订阅），彻底释放残留 Card 与委托链
            if (_cardQuestsDataStaticEventFields == null)
            {
                var qdType = GameInspector.FindTypeSafe("TheBazaar.CardQuestsData", "CardQuestsData");
                if (qdType != null)
                {
                    const BindingFlags anyStatic = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
                    var list = new List<FieldInfo>(4);
                    foreach (var evName in new[] { "CombatQuestUpdated", "CombatQuestCompleted", "GameSimQuestUpdated", "GameSimQuestCompleted" })
                    {
                        var f = qdType.GetField(evName, anyStatic);
                        if (f != null && typeof(Delegate).IsAssignableFrom(f.FieldType))
                        {
                            list.Add(f);
                        }
                    }
                    _cardQuestsDataStaticEventFields = list.ToArray();
                }
            }

            if (_cardQuestsDataStaticEventFields != null)
            {
                for (int i = 0; i < _cardQuestsDataStaticEventFields.Length; i++)
                {
                    _cardQuestsDataStaticEventFields[i].SetValue(null, null);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"CompatAuditPatch.ClearCardQuestSubscribedGroups({reason}): {ex.Message}");
        }
    }

    private static void PatchCardQuestGroupData(SafeHarmonyPatcher patcher)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.CardQuestGroupData");
            if (t == null) return;

            var onComplete = AccessTools.Method(t, "OnQuestCompleted", new[] { typeof(int) });
            if (onComplete != null)
            {
                patcher.TryPatchMethod(onComplete,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(OnQuestCompletedPrefix)),
                    optional: true,
                    label: "Compat.GameSim.CardQuestGroupData.OnQuestCompleted");
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
                    patcher.TryPatchMethod(m,
                        prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(QuestMessagePrefix)),
                        optional: true,
                        label: $"Compat.GameSim.CardQuestGroupData.{name}");
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

    private static void PatchHealthBarRewardController(SafeHarmonyPatcher patcher)
    {
        try
        {
            var t = AccessTools.TypeByName("TheBazaar.HealthBarRewardController");
            if (t == null) return;

            var calc = AccessTools.Method(t, "CalculateRewardReceived", new[] { typeof(float) });
            if (calc != null)
            {
                patcher.TryPatchMethod(calc,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(CalculateRewardReceivedPrefix)),
                    optional: true,
                    label: "Compat.GameSim.HealthBarRewardController.CalculateRewardReceived");
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

    private static void PatchResumeAndEndOfRunControllers(SafeHarmonyPatcher patcher)
    {
        try
        {
            var resumeType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.ResumeVisualController");
            var initMethod = resumeType == null ? null : AccessTools.Method(resumeType, "Init");
            if (initMethod != null)
            {
                patcher.TryPatchMethod(initMethod,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ResumeVisualInitPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(ResumeVisualInitFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.ResumeVisualController.Init");
            }

            var menuBaseType = AccessTools.TypeByName("TheBazaar.UI.MainMenu.MainMenuBaseState");
            var onEnterMethod = menuBaseType == null ? null : AccessTools.Method(menuBaseType, "OnEnter");
            if (onEnterMethod != null)
            {
                patcher.TryPatchMethod(onEnterMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.MainMenuBaseState.OnEnter");
            }

            var refreshMethod = menuBaseType == null ? null : AccessTools.Method(menuBaseType, "Refresh");
            if (refreshMethod != null && !refreshMethod.IsAbstract)
            {
                patcher.TryPatchMethod(refreshMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix)),
                    optional: true,
                    label: "Compat.GameSim.MainMenuBaseState.Refresh");
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
                    patcher.TryPatchMethod(hm,
                        postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(MainMenuBaseStateOnEnterPostfix)),
                        optional: true,
                        label: $"Compat.GameSim.{subStateName}.HandleResumeStateVisuals");
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
                        patcher.TryPatchMethod(rcM,
                            postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(OnRunConfigCacheUpdatedPostfix)),
                            optional: true,
                            label: $"Compat.GameSim.RunConfigurationCache.{rcMethodName}");
                    }
                }
            }

            var winCounterType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentWinCounterController");
            var refreshScore = winCounterType == null ? null : AccessTools.Method(winCounterType, "RefreshScore");
            if (refreshScore != null)
            {
                patcher.TryPatchMethod(refreshScore,
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(SwallowUiFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.TournamentWinCounterController.RefreshScore");
            }

            var eorType = AccessTools.TypeByName("TheBazaar.UI.EndOfRun.EndOfRunRatingController");
            var cfgRewards = eorType == null ? null : AccessTools.Method(eorType, "ConfigureRewards");
            if (cfgRewards != null)
            {
                patcher.TryPatchMethod(cfgRewards,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ConfigureRewardsPrefix)),
                    optional: true,
                    label: "Compat.GameSim.EndOfRunRatingController.ConfigureRewards");
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
            catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1316", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1329", diagEx); }
    }

    private static void MainMenuBaseStateOnEnterPostfix(object __instance)
    {
        try
        {
            ClearParentEncounterCache("MainMenuBaseState.OnEnter");
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            SyncMainMenuResumeAndPlaymodeVisibility(__instance);
        }
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1340", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1354", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1378", diagEx); }
    }

    /// <summary>
    /// 当主大厅正在显示时（如放弃对局、deleteactivesave 或 RunConfig 刷新后），同步切换恢复条与三模式入口可见性。
    /// </summary>
    internal static void RefreshActiveMainMenuUiFromData()
    {
        if (BazaarLauncherPlugin.IsStrictBareClientAuditModeActive()) return;
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

    private static void PatchSoundEventListener(SafeHarmonyPatcher patcher)
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
                patcher.TryPatchMethod(onMoved,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(OnCardMovedPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(OnCardMovedFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.SoundEventListener.OnCardMoved");
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
                RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:NullArg");
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
                RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:MissingSoundMgr");
                return false;
            }

            var iid = GameInspector.GetMember(itemCard, "InstanceId");
            var entities = GameInspector.GetDataEntities();
            var lookup = GameInspector.GetDataLookup();
            if (iid == null || entities == null || lookup == null || !entities.Contains(iid))
            {
                RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:EntityMissing");
                return false;
            }

            var entity = entities[iid];
            if (entity == null)
            {
                RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:NullEntity");
                return false;
            }

            _getCardControllerMethod ??= lookup.GetType().GetMethod(
                "GetCardController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var ctrl = _getCardControllerMethod?.Invoke(lookup, new[] { entity });
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null) ||
                (ctrl is Component comp && comp.gameObject == null))
            {
                RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:MissingCardController");
                return false;
            }
        }
        catch (Exception ex)
        {
            RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:PrefixEx:" + ex.GetType().Name);
            return false;
        }
        return true;
    }

    private static Exception OnCardMovedFinalizer(Exception __exception)
    {
        if (__exception != null)
        {
            RecordCompatHackHit("H-2", ref _suppressedSoundMoveCount, "OnCardMoved:Finalizer:" + __exception.GetType().Name);
        }
        return null;
    }

    // ── 8. BaseCardRevealAnimationDriver.GetCardContainerBounds：空集合防越界 ──

    private static void PatchCardRevealAnimationDriver(SafeHarmonyPatcher patcher)
    {
        try
        {
            var t = GameInspector.FindTypeSafe("TheBazaar.Game.Cards.Animation.BaseCardRevealAnimationDriver");
            if (t == null) return;

            var m = AccessTools.Method(t, "GetCardContainerBounds", new[] { typeof(List<Bounds>) })
                    ?? AccessTools.Method(t, "GetCardContainerBounds");
            if (m != null)
            {
                patcher.TryPatchMethod(m,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(GetCardContainerBoundsPrefix)),
                    finalizer: AccessTools.Method(typeof(CompatAuditPatch), nameof(GetCardContainerBoundsFinalizer)),
                    optional: true,
                    label: "Compat.GameSim.BaseCardRevealAnimationDriver.GetCardContainerBounds");
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

    private static void PatchDataApplySimPvpOpponent(SafeHarmonyPatcher patcher)
    {
        try
        {
            var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
            if (dataType == null) return;

            _dataSimPvpOpponentProp = dataType.GetProperty("SimPvpOpponent", BindingFlags.Public | BindingFlags.Static);
            var applyM = AccessTools.Method(dataType, "ApplySimPvpOpponent");
            if (applyM != null)
            {
                patcher.TryPatchMethod(applyM,
                    prefix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ApplySimPvpOpponentPrefix)),
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(ApplySimPvpOpponentPostfix)),
                    optional: true,
                    label: "Compat.GameSim.Data.ApplySimPvpOpponent");
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1747", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1785", diagEx); }
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
                RecordCompatHackHit("H-1", ref _pvpOpponentChangedFixedCount,
                    $"day={nowSnap.Day},hour={nowSnap.Hour},hero={nowSnap.Hero},accountId={nowSnap.AccountId}");
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

    private static void PatchTransitionInHour6Guard(SafeHarmonyPatcher patcher)
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
                patcher.TryPatchMethod(
                    shouldWaitMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TransitionInShouldWaitForNewDayPostfix)),
                    optional: true,
                    label: "Compat.GameSim.TransitionInComponent.ShouldWaitForNewDayTransition");
            }

            var tryUpdateClockMethod = AccessTools.Method(tiType, "TryUpdateClock", Type.EmptyTypes);
            if (tryUpdateClockMethod != null)
            {
                patcher.TryPatchMethod(
                    tryUpdateClockMethod,
                    postfix: AccessTools.Method(typeof(CompatAuditPatch), nameof(TransitionInTryUpdateClockPostfix)),
                    optional: true,
                    label: "Compat.GameSim.TransitionInComponent.TryUpdateClock");
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1920", diagEx); }
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L1944", diagEx); }
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

            RecordCompatHackHit("H-5", ref _hour6TransitionUnblockedCount, $"Hour{curHour}TransitionInDeadlockUnblocked");
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
        catch (Exception diagEx) { global::BazaarLauncher.Core.DiagLog.SwallowOnce("CompatAuditPatch.GameSim:L2035", diagEx); }
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
                RecordCompatHackHit("H-5", ref _hour6WatchdogRevealCount, "Hour6WatchdogFallbackTryRevealCards");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CompatAuditPatch.RunHour6ChoiceRevealWatchdogOnMain: " + ex.Message);
        }
    }

}

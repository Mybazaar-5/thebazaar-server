using System.Collections.Frozen;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;

namespace BazaarCore.Combat;

/// <summary>
/// 确定性战斗引擎：50ms 固定步长（1:1 对齐官方模拟与前端帧率）、250ms 单能力 ICD 触发队列、Burn/Poison/Regen/Sandstorm 完整结算，
/// 既可给 Bot 毫秒级 Headless 演算，也可输出完整帧时间轴给客户端播放 1:1 战斗演出。
/// </summary>
public static class CombatSimulator
{
    public const int TickMs = 50;
    public const int MaxDurationMs = 300_000;
    public const int MaxForceUseDepth = 16;
    public const int MaxTickCascadeFires = 32;
    public const int MaxSameTickCardFires = 2;
    public const int MaxDispatchDepth = 24;
    public const int MaxTickImmediateExecs = 64;

    private static readonly string[] CustomAttrNames =
    [
        "Custom_0", "Custom_1", "Custom_2", "Custom_3", "Custom_4",
        "Custom_5", "Custom_6", "Custom_7", "Custom_8"
    ];

    private readonly record struct TriggerQueueKey(string PlayerId, string CardId, string AbilityId);
    private readonly record struct IndexedAbility(CardInstance Card, AbilityDefinition Ability, int ActiveOrder);

    /// <summary>战斗动态光环依赖位掩码（P0-07）：按实时状态分类精准触发光环脏标记与即时重算，无动态光环对局零额外开销。</summary>
    [Flags]
    private enum CombatAuraDependencyMask
    {
        None       = 0,
        Health     = 1 << 0,
        Shield     = 1 << 1,
        Burn       = 1 << 2,
        Poison     = 1 << 3,
        Regen      = 1 << 4,
        Tempo      = 1 << 5,
        CardStatus = 1 << 6,
        CardAmmo   = 1 << 7
    }

    private sealed class PendingMulticastBatch
    {
        public PlayerState Owner { get; init; } = null!;
        public PlayerState Opponent { get; init; } = null!;
        public CardInstance Card { get; init; } = null!;
        public AbilityDefinition? PrimaryAbility { get; init; }
        public int NextCastNumber { get; set; }
        public int TotalCasts { get; init; }
        public int DelayRemainingMs { get; set; }
        public int LastProcessedTimeMs { get; set; }
    }

    private sealed class QueuedTrigger
    {
        public PlayerState Owner { get; init; } = null!;
        public PlayerState Opponent { get; init; } = null!;
        public CardInstance OwnerCard { get; init; } = null!;
        public AbilityDefinition Ability { get; init; } = null!;
        public CardInstance? SourceCard { get; init; }
        public CardInstance? TargetCard { get; init; }
        public double AttributeDelta { get; init; }
        public bool AllowWhenDestroyed { get; init; }
    }

    private sealed class TimedAttributeExpiry
    {
        public int ExpireTimeMs { get; init; }
        public CardInstance? Card { get; init; }
        public PlayerState? Player { get; init; }
        public string Attribute { get; init; } = string.Empty;
        public string Operation { get; init; } = "Add";
        public double Value { get; init; }
    }

    private sealed class TimedTransformExpiry
    {
        public int ExpireTimeMs { get; init; }
        public PlayerState Owner { get; init; } = null!;
        public CardInstance OriginalCard { get; init; } = null!;
        public List<CardInstance> Replacements { get; init; } = new();
        public string OriginalSection { get; init; } = "Hand";
        public int OriginalSlot { get; init; }
    }

    private sealed class CachedCardSnapshotAttrs
    {
        public int Version = -1;
        public int AmmoMax;
        public int CooldownMaxMs;
        public int DamageAmount;
        public int ShieldApplyAmount;
        public int BurnApplyAmount;
        public int PoisonApplyAmount;
        public int HealAmount;
        public int RegenApplyAmount;
        public int CritChance;
        public int Multicast;
        public int Lifesteal;
        public int SellPrice;
        public bool AuraHeated;
        public bool AuraChilled;
        public IReadOnlyList<string>? Tags;
        public IReadOnlyList<string>? HiddenTags;
        public IReadOnlyList<string>? Heroes;
        public int[]? CustomAttrs;
    }

    /// <summary>
    /// 模拟双方完整战斗；设置 <paramref name="recordTimeline"/> 或 <paramref name="recordEvents"/> 为 false 可显著降低无头批量压测开销。
    /// 将 <paramref name="syncPermanentChanges"/> 设为 true 可在战后自动把永久成长/附魔/升阶/任务/变形/衍生卡及永久玩家属性无损回写至 <paramref name="originalP1"/>。
    /// </summary>
    public static CombatResult Simulate(
        GameDataRepository repo,
        PlayerState originalP1,
        PlayerState originalP2,
        int seed = 42,
        string combatType = "PVP",
        bool recordTimeline = true,
        bool recordEvents = true,
        bool syncPermanentChanges = false)
        => Simulate(
            repo,
            originalP1,
            originalP2,
            seed,
            combatType,
            recordTimeline,
            recordEvents,
            syncPermanentChanges,
            modifiers: null,
            sandstormEnabled: true,
            timelineSampleIntervalMs: TickMs);

    /// <summary>
    /// 扩展战斗模拟重载：支持注入突变词条/无尽/Boss 车轮战 <see cref="CombatModifiers"/>、显式沙暴开关以及自定义时间轴快照采样间隔（如 50ms 对齐 CombatSimConverter）。
    /// </summary>
    public static CombatResult Simulate(
        GameDataRepository repo,
        PlayerState originalP1,
        PlayerState originalP2,
        int seed,
        string combatType,
        bool recordTimeline,
        bool recordEvents,
        bool syncPermanentChanges,
        CombatModifiers? modifiers,
        bool sandstormEnabled = true,
        int timelineSampleIntervalMs = TickMs,
        IReadOnlyDictionary<string, MechanicDefinition>? mechanicDefinitionsOverride = null,
        IReadOnlyList<ICombatPipelineInterceptor>? pipelineInterceptorsOverride = null)
    {
        var rng = new Random(seed);
        var p1 = originalP1.CloneForCombat();
        var p2 = originalP2.CloneForCombat();
        var gm = repo.GameMode;
        CombatMechanicRuntime? mechanicRuntime = null;
        p1.EnsureExclusionSetInitialized(gm);
        p2.EnsureExclusionSetInitialized(gm);
        EnsureCombatBasePlayerEffect(repo, p1);
        EnsureCombatBasePlayerEffect(repo, p2);

        var mod = modifiers ?? CombatModifiers.None;
        bool effectiveSandstormEnabled = sandstormEnabled && mod.SandstormEnabled;
        int effectiveSampleInterval = Math.Max(TickMs, mod.TimelineSampleIntervalMs ?? timelineSampleIntervalMs);

        // 应用开战前双方生命上限修正（含玻璃大炮/泰坦巨躯/无尽高层/Boss 车轮战缩放，防 Int32 溢出）
        if (Math.Abs(mod.HealthMultiplierP1 - 1.0) > 1e-6)
        {
            int scaledHp1 = (int)Math.Clamp(Math.Round(p1.HealthMax * mod.HealthMultiplierP1), 1, EndlessTowerScaler.MaxEnemyHealthCap);
            p1.Attributes["HealthMax"] = scaledHp1;
            p1.CurrentHealth = scaledHp1;
        }
        if (Math.Abs(mod.HealthMultiplierP2 - 1.0) > 1e-6)
        {
            int cap = Math.Max(1, mod.EnemyHealthCap);
            int scaledHp2 = (int)Math.Clamp(Math.Round(p2.HealthMax * mod.HealthMultiplierP2), 1, cap);
            p2.Attributes["HealthMax"] = scaledHp2;
            p2.CurrentHealth = scaledHp2;
        }

        int icdMs = Math.Max(100, gm.CombatSimEffectDelay);
        int effectDelayMs = Math.Max(TickMs, gm.CombatSimEffectDelay > 0 ? gm.CombatSimEffectDelay : 250);

        var events = recordEvents ? new List<CombatEvent>() : new List<CombatEvent>(0);
        var timeline = recordTimeline ? new List<CombatTickState>() : new List<CombatTickState>(0);
        int execChainSeq = 0;
        var frameReadyCards = new List<(PlayerState Owner, PlayerState Opp, CardInstance Card, int PriorityRank, int SideIndex)>(16);
        var snapshotAttrCache = recordTimeline ? new Dictionary<CardInstance, CachedCardSnapshotAttrs>(ReferenceEqualityComparer.Instance) : null;
        double p1DmgTotal = 0, p2DmgTotal = 0;
        double p1ShieldTotal = 0, p2ShieldTotal = 0;

        var icdReadyTime = new Dictionary<TriggerQueueKey, int>();
        var triggerQueues = new Dictionary<TriggerQueueKey, Queue<QueuedTrigger>>();
        var readyBatch = new List<(TriggerQueueKey Key, QueuedTrigger Item, int PrevReadyAt)>();
        // 修复 P0-6：跨卡牌同步触发必须先按 Ability.Priority（Immediate > Highest > High > Medium > Low > Lowest）排序，再按 StartSlot / InstanceId 稳定决胜
        Comparison<(TriggerQueueKey Key, QueuedTrigger Item, int PrevReadyAt)> readyBatchComparison = (a, b) =>
        {
            int sideCmp = (a.Item.Owner == p1 ? 0 : 1).CompareTo(b.Item.Owner == p1 ? 0 : 1);
            if (sideCmp != 0) return sideCmp;
            int prioCmp = GetAbilityPriorityRank(a.Item.Ability.Priority).CompareTo(GetAbilityPriorityRank(b.Item.Ability.Priority));
            if (prioCmp != 0) return prioCmp;
            int slotCmp = a.Item.OwnerCard.StartSlot.CompareTo(b.Item.OwnerCard.StartSlot);
            if (slotCmp != 0) return slotCmp;
            int instCmp = string.CompareOrdinal(a.Item.OwnerCard.InstanceId, b.Item.OwnerCard.InstanceId);
            if (instCmp != 0) return instCmp;
            return string.CompareOrdinal(a.Item.Ability.Id, b.Item.Ability.Id);
        };
        var pendingMulticasts = new List<PendingMulticastBatch>();
        var handSnapshot = new List<CardInstance>(16);
        var timedExpiries = new List<TimedAttributeExpiry>();
        var timedTransformExpiries = new List<TimedTransformExpiry>();
        var temporaryTransforms = new List<CombatTemporaryTransformRecord>();
        var prevAuraTargets = recordEvents
            ? new Dictionary<(string SourceCardId, string AuraId), (HashSet<string> CardIds, HashSet<string> PlayerIds)>()
            : null;
        var activeExec = new ActiveExecutionState();

        var p1TriggerIndex = new Dictionary<string, List<IndexedAbility>>(StringComparer.Ordinal);
        var p2TriggerIndex = new Dictionary<string, List<IndexedAbility>>(StringComparer.Ordinal);
        bool triggerIndexDirty = true;
        int indexedCardTotal = -1;
        bool p1HasQuests = false;
        bool p2HasQuests = false;
        int questDispatchDepth = 0;
        int forceUseDepth = 0;
        int dispatchDepth = 0;
        int tickCascadeFires = 0;
        int tickImmediateExecs = 0;
        int auraRecalcDepth = 0;
        CombatAuraDependencyMask combatAuraDepMask = CombatAuraDependencyMask.None;
        bool auraDirty = false;
        var activeFiringCards = new HashSet<CardInstance>(ReferenceEqualityComparer.Instance);
        var tickCardFireCounts = new Dictionary<CardInstance, int>(ReferenceEqualityComparer.Instance);
        var lastCooldownFireMs = new Dictionary<CardInstance, int>(ReferenceEqualityComparer.Instance);
        RuleExecutionContext p1BaseCtx = null!;
        RuleExecutionContext p2BaseCtx = null!;

        bool sandstormWarned = false;
        bool sandstormActive = false;
        bool sandstormForceStarted = false;
        bool sandstormPendingImmediateTick = false;
        int sandstormWarnMs = Math.Max(0, gm.Sandstorm.CountdownStartTime + mod.SandstormStartOffsetMs);
        int sandstormStartMs = Math.Max(0, gm.Sandstorm.CountdownStartTime + gm.Sandstorm.CountdownDuration + mod.SandstormStartOffsetMs); // 默认 30,000ms
        int sandstormElapsedMs = 0;
        int sandstormCurrentRate = gm.Sandstorm.BaseTickRate; // 250ms
        int sandstormNextDmg = gm.Sandstorm.BaseDamage;       // 1
        int effectiveMinCooldownFloorMs = Math.Clamp(mod.MinCooldownFloorMs, 200, 1000);

        int nextBurnTickMs = 500;
        int nextPoisonTickMs = 1000;
        int nextRegenTickMs = 1000;
        int nextPeriodicFreezeMs = (mod.PeriodicFreezeIntervalMs > 0 && mod.PeriodicFreezeDurationMs > 0)
            ? mod.PeriodicFreezeIntervalMs
            : int.MaxValue;
        int nextSnapshotWindowEndMs = effectiveSampleInterval;
        int p1TempoElapsedMs = 0;
        int p2TempoElapsedMs = 0;
        int timeMs = 0;

        bool p1DeathTriggered = false;
        bool p2DeathTriggered = false;
        int p1Overkill = 0;
        int p2Overkill = 0;

        static void ClampSideAmmo(PlayerState side)
        {
            for (int i = 0; i < side.Hand.Cards.Count; i++)
            {
                var c = side.Hand.Cards[i];
                int maxAmmo = c.EffectiveAmmoMax;
                c.CurrentAmmo = Math.Clamp(c.CurrentAmmo, 0, maxAmmo);
            }
            for (int i = 0; i < side.Stash.Cards.Count; i++)
            {
                var c = side.Stash.Cards[i];
                int maxAmmo = c.EffectiveAmmoMax;
                c.CurrentAmmo = Math.Clamp(c.CurrentAmmo, 0, maxAmmo);
            }
        }

        static void SyncEnragedState(PlayerState side)
        {
            // 修复 P0-03：狂暴态仅写入战斗临时层 CombatAdd（PlayerState.GetAttribute 同时直接派生 EnragedRemainingMs），绝不污染永久 Attributes
            if (side.EnragedRemainingMs > 0)
            {
                side.CombatAdd["Enraged"] = 1.0;
                side.CombatAdd["EnragedDuration"] = side.EnragedRemainingMs;
            }
            else
            {
                side.CombatAdd.Remove("Enraged");
                side.CombatAdd.Remove("EnragedDuration");
            }
        }

        void RefreshCombatAuraDependencyMask()
        {
            combatAuraDepMask = ScanSideAuraDependencies(p1) | ScanSideAuraDependencies(p2);
        }

        void MarkAuraDirty(CombatAuraDependencyMask changedBits)
        {
            if (changedBits != CombatAuraDependencyMask.None && (combatAuraDepMask & changedBits) != 0)
                auraDirty = true;
        }

        void FlushDirtyAurasIfNeeded(bool emitFlyingTriggers = true)
        {
            if (!auraDirty) return;
            auraDirty = false;
            RecalculateCombatAuras(emitFlyingTriggers);
            ClampSideAmmo(p1);
            ClampSideAmmo(p2);
        }

        int GetEffectiveCardCooldownMaxMs(CardInstance card, PlayerState owner)
        {
            double effCd = card.EffectiveCooldownMs;
            if (double.IsNaN(effCd) || double.IsInfinity(effCd)) effCd = 1000;
            double totalModCdr = mod.CooldownReductionPct + (owner == p2 ? mod.CooldownReductionP2Pct : 0.0);
            if (totalModCdr > 1e-6)
                effCd *= Math.Max(0.1, 1.0 - Math.Min(0.85, totalModCdr));
            int floorMs = Math.Clamp(mod.MinCooldownFloorMs, 200, 1000);
            return Math.Max(floorMs, (int)Math.Round(effCd));
        }

        void HandleAuraFlyingTransition(AuraEngine.AuraFlyingTransition tr)
        {
            if (recordEvents)
            {
                Emit(new CombatEvent
                {
                    TimeMs = timeMs,
                    Type = CombatEventType.FlyingChanged,
                    SourcePlayerId = tr.Owner.PlayerId,
                    TargetPlayerId = tr.Owner.PlayerId,
                    SourceCardInstanceId = tr.SourceCard?.InstanceId,
                    TargetCardInstanceId = tr.TargetCard.InstanceId,
                    Value = tr.StartedFlying ? 1 : 0,
                    ActionType = "TAuraActionCardModifyAttribute"
                });
            }
            if (tr.StartedFlying)
            {
                DispatchEvent("TTriggerOnCardStartedFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner);
                DispatchEvent("TTriggerOnCardStartsFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner);
                DispatchEvent("TTriggerOnCardAttributeChanged", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 0, 1);
            }
            else
            {
                DispatchEvent("TTriggerOnCardStoppedFlying", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner);
                DispatchEvent("TTriggerOnCardAttributeChanged", tr.Owner, tr.SourceCard, tr.TargetCard, tr.Owner, "Flying", 1, 0);
            }
        }

        void HandleAuraSnapshot(IReadOnlyList<AuraEngine.AuraEffectTargetsSnapshot> snapshots)
        {
            if (prevAuraTargets == null) return;
            var currentMap = new Dictionary<(string SourceCardId, string AuraId), (HashSet<string> CardIds, HashSet<string> PlayerIds)>(snapshots.Count);
            for (int i = 0; i < snapshots.Count; i++)
            {
                var snap = snapshots[i];
                var key = (snap.SourceCardInstanceId, snap.AuraId);
                if (!currentMap.TryGetValue(key, out var bag))
                {
                    bag = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
                    currentMap[key] = bag;
                }
                for (int c = 0; c < snap.TargetCardInstanceIds.Count; c++)
                    bag.CardIds.Add(snap.TargetCardInstanceIds[c]);
                for (int p = 0; p < snap.TargetPlayerIds.Count; p++)
                    bag.PlayerIds.Add(snap.TargetPlayerIds[p]);
            }

            // 1. 检测消失或目标减少的光环
            List<(string SourceCardId, string AuraId)>? keysToRemove = null;
            foreach (var (key, prevBag) in prevAuraTargets)
            {
                if (!currentMap.TryGetValue(key, out var curBag))
                {
                    if (prevBag.CardIds.Count > 0 || prevBag.PlayerIds.Count > 0)
                    {
                        Emit(new CombatEvent
                        {
                            TimeMs = timeMs,
                            Type = CombatEventType.AuraExecuted,
                            SourceCardInstanceId = key.SourceCardId,
                            TriggerSourceCardInstanceId = activeExec.TriggerSourceCardInstanceId ?? key.SourceCardId,
                            AbilityId = key.AuraId,
                            AuraRemovedCardIds = prevBag.CardIds.Count > 0 ? new List<string>(prevBag.CardIds) : null,
                            AuraRemovedPlayerIds = prevBag.PlayerIds.Count > 0 ? new List<string>(prevBag.PlayerIds) : null
                        });
                    }
                    keysToRemove ??= new List<(string, string)>();
                    keysToRemove.Add(key);
                }
                else
                {
                    List<string>? addedCards = null;
                    List<string>? removedCards = null;
                    List<string>? addedPlayers = null;
                    List<string>? removedPlayers = null;

                    foreach (var cid in curBag.CardIds)
                        if (!prevBag.CardIds.Contains(cid)) (addedCards ??= new List<string>()).Add(cid);
                    foreach (var cid in prevBag.CardIds)
                        if (!curBag.CardIds.Contains(cid)) (removedCards ??= new List<string>()).Add(cid);
                    foreach (var pid in curBag.PlayerIds)
                        if (!prevBag.PlayerIds.Contains(pid)) (addedPlayers ??= new List<string>()).Add(pid);
                    foreach (var pid in prevBag.PlayerIds)
                        if (!curBag.PlayerIds.Contains(pid)) (removedPlayers ??= new List<string>()).Add(pid);

                    if (addedCards != null || removedCards != null || addedPlayers != null || removedPlayers != null)
                    {
                        Emit(new CombatEvent
                        {
                            TimeMs = timeMs,
                            Type = CombatEventType.AuraExecuted,
                            SourceCardInstanceId = key.SourceCardId,
                            TriggerSourceCardInstanceId = activeExec.TriggerSourceCardInstanceId ?? key.SourceCardId,
                            AbilityId = key.AuraId,
                            AuraAppliedCardIds = addedCards,
                            AuraRemovedCardIds = removedCards,
                            AuraAppliedPlayerIds = addedPlayers,
                            AuraRemovedPlayerIds = removedPlayers
                        });
                        prevBag.CardIds.Clear();
                        foreach (var cid in curBag.CardIds) prevBag.CardIds.Add(cid);
                        prevBag.PlayerIds.Clear();
                        foreach (var pid in curBag.PlayerIds) prevBag.PlayerIds.Add(pid);
                    }
                }
            }

            if (keysToRemove != null)
            {
                for (int i = 0; i < keysToRemove.Count; i++)
                    prevAuraTargets.Remove(keysToRemove[i]);
            }

            // 2. 检测全新激活的光环
            foreach (var (key, curBag) in currentMap)
            {
                if (prevAuraTargets.ContainsKey(key)) continue;
                prevAuraTargets[key] = (new HashSet<string>(curBag.CardIds, StringComparer.Ordinal), new HashSet<string>(curBag.PlayerIds, StringComparer.Ordinal));
                if (curBag.CardIds.Count > 0 || curBag.PlayerIds.Count > 0)
                {
                    Emit(new CombatEvent
                    {
                        TimeMs = timeMs,
                        Type = CombatEventType.AuraExecuted,
                        SourceCardInstanceId = key.SourceCardId,
                        TriggerSourceCardInstanceId = activeExec.TriggerSourceCardInstanceId ?? key.SourceCardId,
                        AbilityId = key.AuraId,
                        AuraAppliedCardIds = curBag.CardIds.Count > 0 ? new List<string>(curBag.CardIds) : null,
                        AuraAppliedPlayerIds = curBag.PlayerIds.Count > 0 ? new List<string>(curBag.PlayerIds) : null
                    });
                }
            }
        }

        void RecalculateCombatAuras(bool emitFlyingTriggers = true)
        {
            auraDirty = false;
            if (auraRecalcDepth >= 4) return;
            bool hasPrevTrackedAuras = prevAuraTargets != null && prevAuraTargets.Count > 0;
            if (!AuraEngine.HasAnyAuraSource(p1, p2) && !AuraEngine.HasAnyAppliedAuraModifiers(p1, p2) && !hasPrevTrackedAuras)
                return;
            auraRecalcDepth++;
            try
            {
                AuraEngine.RecalculateAuras(
                    p1,
                    p2,
                    RuleScope.InCombat,
                    p1.Day,
                    p1.Hour,
                    repo,
                    selectionCards: null,
                    onFlyingTransition: emitFlyingTriggers ? HandleAuraFlyingTransition : null,
                    onAuraSnapshot: recordEvents ? HandleAuraSnapshot : null);
            }
            finally
            {
                auraRecalcDepth--;
            }
        }

        RefreshCombatAuraDependencyMask();
        int preAuraHp1 = p1.CurrentHealth;
        int preAuraHp2 = p2.CurrentHealth;
        RecalculateCombatAuras(emitFlyingTriggers: false);
        // 修复 P0-04：首次光环结算后立即将开战当前生命同步至光环后上限（避免 Belt/Pawn Shop 等 HealthMax 光环导致半血开局）
        p1.CurrentHealth = p1.HealthMax;
        p2.CurrentHealth = p2.HealthMax;
        if (p1.CurrentHealth != preAuraHp1 || p2.CurrentHealth != preAuraHp2)
        {
            RecalculateCombatAuras(emitFlyingTriggers: false);
            p1.CurrentHealth = p1.HealthMax;
            p2.CurrentHealth = p2.HealthMax;
        }
        ClampSideAmmo(p1);
        ClampSideAmmo(p2);
        SyncEnragedState(p1);
        SyncEnragedState(p2);

        bool IsSkillCardId(string? cardInstanceId)
        {
            if (string.IsNullOrEmpty(cardInstanceId)) return false;
            foreach (var s in p1.Skills) if (s.InstanceId == cardInstanceId) return true;
            foreach (var s in p2.Skills) if (s.InstanceId == cardInstanceId) return true;
            foreach (var c in p1.Hand.Cards) if (c.InstanceId == cardInstanceId) return c.Template.Type == "Skill";
            foreach (var c in p2.Hand.Cards) if (c.InstanceId == cardInstanceId) return c.Template.Type == "Skill";
            return false;
        }

        void Emit(CombatEvent ev)
        {
            if (!recordEvents) return;
            events.Add(new CombatEvent
            {
                TimeMs = ev.TimeMs,
                Type = ev.Type,
                SourcePlayerId = ev.SourcePlayerId,
                TargetPlayerId = ev.TargetPlayerId,
                SourceCardInstanceId = ev.SourceCardInstanceId,
                TriggerSourceCardInstanceId = ev.TriggerSourceCardInstanceId ?? activeExec.TriggerSourceCardInstanceId,
                TargetCardInstanceId = ev.TargetCardInstanceId,
                Value = ev.Value,
                ShieldAbsorbed = ev.ShieldAbsorbed,
                HpDamage = ev.HpDamage,
                EffectiveHpRestored = ev.EffectiveHpRestored,
                IsCrit = ev.IsCrit,
                Detail = ev.Detail,
                AbilityId = ev.AbilityId ?? activeExec.AbilityId,
                ActionType = ev.ActionType ?? activeExec.ActionType,
                ExecutionContextId = ev.ExecutionContextId ?? activeExec.ExecutionContextId,
                IsReverted = ev.IsReverted,
                IsSkillSource = ev.IsSkillSource || IsSkillCardId(ev.SourceCardInstanceId),
                VfxKeyOverride = ev.VfxKeyOverride,
                AuraAppliedCardIds = ev.AuraAppliedCardIds,
                AuraRemovedCardIds = ev.AuraRemovedCardIds,
                AuraAppliedPlayerIds = ev.AuraAppliedPlayerIds,
                AuraRemovedPlayerIds = ev.AuraRemovedPlayerIds,
                RevertedTransformedInstanceIds = ev.RevertedTransformedInstanceIds,
                Player1Health = p1.CurrentHealth,
                Player1Shield = p1.Shield,
                Player1Burn = p1.Burn,
                Player1Poison = p1.Poison,
                Player2Health = p2.CurrentHealth,
                Player2Shield = p2.Shield,
                Player2Burn = p2.Burn,
                Player2Poison = p2.Poison
            });
        }

        (int ShieldAbsorbed, int HpDamage) ApplyDirectDamage(PlayerState target, int amount, bool bypassShield)
        {
            if (amount <= 0) return (0, 0);
            int prevShield = target.Shield;
            int prevHp = target.CurrentHealth;
            int remaining = amount;
            int absorbed = 0;
            if (!bypassShield && target.Shield > 0)
            {
                absorbed = Math.Min(target.Shield, remaining);
                target.Shield -= absorbed;
                remaining -= absorbed;
            }
            int hpDamage = remaining;
            if (remaining > 0)
            {
                if (remaining > target.CurrentHealth)
                {
                    // 记录同 Tick 内溢出扣血（对齐官方 Health 暂允许负值、帧末 OnDeath 前再归零），确保同帧 Poison+Regen 或伤害+回血按净值结算
                    if (target == p1) p1Overkill += remaining - target.CurrentHealth;
                    else p2Overkill += remaining - target.CurrentHealth;
                    target.CurrentHealth = 0;
                }
                else
                {
                    target.CurrentHealth -= remaining;
                }
            }
            CombatAuraDependencyMask changedMask = CombatAuraDependencyMask.None;
            if (target.Shield < prevShield) changedMask |= CombatAuraDependencyMask.Shield;
            if (target.CurrentHealth < prevHp) changedMask |= CombatAuraDependencyMask.Health;
            if (changedMask != CombatAuraDependencyMask.None)
            {
                MarkAuraDirty(changedMask);
                FlushDirtyAurasIfNeeded();
            }
            if (target.Shield < prevShield)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Shield", prevShield, target.Shield);
            if (target.CurrentHealth < prevHp)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Health", prevHp, target.CurrentHealth);
            return (absorbed, hpDamage);
        }

        int ApplyHealthGain(PlayerState target, int amount)
        {
            if (amount <= 0) return 0;
            ref int overkill = ref (target == p1 ? ref p1Overkill : ref p2Overkill);
            int prevSignedHp = target.CurrentHealth - overkill;
            if (overkill > 0)
            {
                if (amount <= overkill)
                {
                    overkill -= amount;
                    return Math.Max(0, (target.CurrentHealth - overkill) - prevSignedHp);
                }
                amount -= overkill;
                overkill = 0;
            }
            target.CurrentHealth = Math.Min(target.HealthMax, target.CurrentHealth + amount);
            return Math.Max(0, (target.CurrentHealth - overkill) - prevSignedHp);
        }

        /// <summary>返回 true 表示允许继续执行；仅当自定义机制显式调用 CancelHook 时返回 false（自然减免至 0 仍放行原版免疫回调）。</summary>
        bool RunBeforeDamageHook(PlayerState attacker, PlayerState target, CardInstance? srcCard, string damageType, ref int amount)
        {
            if (mechanicRuntime == null || amount <= 0) return true;
            var pipeEv = new CombatPipelineEvent
            {
                HookName = "BeforeDamage",
                TimeMs = timeMs,
                SourcePlayer = attacker,
                TargetPlayer = target,
                SourceCard = srcCard,
                DamageType = damageType,
                Amount = amount
            };
            mechanicRuntime.DispatchPipeline(pipeEv);
            if (pipeEv.Cancelled)
            {
                amount = 0;
                return false;
            }
            if (pipeEv.AmountModified)
                amount = Math.Max(0, (int)Math.Round(pipeEv.Amount));
            return true;
        }

        /// <summary>返回 true 表示允许继续执行；仅当自定义机制显式调用 CancelHook 时返回 false（Radiant 等自然减免至 0 仍放行 PerformedSlow/Freeze）。</summary>
        bool RunBeforeStatusHook(PlayerState actor, PlayerState? targetPlayer, CardInstance? srcCard, CardInstance? targetCard, string statusType, ref int amount)
        {
            if (mechanicRuntime == null || amount <= 0) return true;
            var pipeEv = new CombatPipelineEvent
            {
                HookName = "BeforeStatusApply",
                TimeMs = timeMs,
                SourcePlayer = actor,
                TargetPlayer = targetPlayer,
                SourceCard = srcCard,
                TargetCard = targetCard,
                StatusType = statusType,
                Amount = amount
            };
            mechanicRuntime.DispatchPipeline(pipeEv);
            if (pipeEv.Cancelled)
            {
                amount = 0;
                return false;
            }
            if (pipeEv.AmountModified)
                amount = Math.Max(0, (int)Math.Round(pipeEv.Amount));
            return true;
        }

        void ApplyCardStatusDuration(
            PlayerState actor,
            CardInstance targetCard,
            int ms,
            CardInstance? srcCard,
            string reductionAttr,
            bool checkRadiantAndFlying,
            CombatEventType evType,
            string performTrigger,
            string attrName,
            Func<CardInstance, int> getMs,
            Action<CardInstance, int> setMs)
        {
            if (ms <= 0) return;
            bool isRadiant = checkRadiantAndFlying && AbilityExecutor.IsLegacySyntheticRadiantImmunity(targetCard);
            double pctRed = isRadiant ? 100.0 : Math.Clamp(targetCard.GetAttribute(reductionAttr), 0, 100);
            double flyingMult = (checkRadiantAndFlying && IsCardFlying(targetCard)) ? 0.5 : 1.0;
            int actual = Math.Max(0, (int)Math.Round(ms * (1.0 - pctRed / 100.0) * flyingMult));
            var targetCardOwner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard)) ? p1
                                : ((p2.Hand.Cards.Contains(targetCard) || p2.Skills.Contains(targetCard)) ? p2 : null);
            if (!RunBeforeStatusHook(actor, targetCardOwner, srcCard, targetCard, attrName, ref actual))
                return;
            if (actual > 0)
            {
                int prev = getMs(targetCard);
                int next = prev + actual;
                setMs(targetCard, next);
                MarkAuraDirty(CombatAuraDependencyMask.CardStatus);
                FlushDirtyAurasIfNeeded();
                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = evType, SourceCardInstanceId = srcCard?.InstanceId, TargetCardInstanceId = targetCard.InstanceId, Value = actual });
                // 先派发属性变化触发器，确保 Photo Booth / Beast Call 等自锁存（Custom_8）能在 PerformedHaste 清零前捕获归属
                DispatchEvent("TTriggerOnCardAttributeChanged", actor, srcCard, targetCard, actor, attrName, prev, next);
                DispatchEvent(performTrigger, actor, srcCard, targetCard, actor);
            }
            else
            {
                // 9.1：非零 Haste/Slow/Freeze 被修正降至 0ms 时仍触发对应的 TTriggerOnCardPerformed*
                DispatchEvent(performTrigger, actor, srcCard, targetCard, actor);
            }
        }

        void CompleteReadyCooldowns(PlayerState cardOwner, PlayerState cardOpp, CardInstance targetCard, bool countFirstAsDepth, bool allowIfFrozenThisTick = false)
        {
            if (targetCard.IsDestroyed || targetCard.IsDisabled
                || !targetCard.HasActiveCooldown
                || (!allowIfFrozenThisTick && targetCard.FreezeMs > 0))
            {
                return;
            }
            if (activeFiringCards.Contains(targetCard)) return;
            if (countFirstAsDepth)
            {
                if (forceUseDepth >= MaxForceUseDepth || tickCascadeFires >= MaxTickCascadeFires) return;
                if (tickCardFireCounts.GetValueOrDefault(targetCard, 0) >= MaxSameTickCardFires) return;
            }
            if (lastCooldownFireMs.TryGetValue(targetCard, out int prevCdFireMs) && timeMs - prevCdFireMs < icdMs)
                return;

            int ammoMax = targetCard.EffectiveAmmoMax;
            if (ammoMax > 0 && targetCard.CurrentAmmo <= 0) return;
            int cdMax = GetEffectiveCardCooldownMaxMs(targetCard, cardOwner);
            if (targetCard.CooldownProgressMs < cdMax) return;

            // 修复 P1-54：对齐官方 BazaarCardDealer.cs:4156-4172，满冷却触发后进度直接归零，不向下一轮结转溢出进度
            targetCard.CooldownProgressMs = 0;

            lastCooldownFireMs[targetCard] = timeMs;
            if (countFirstAsDepth)
            {
                tickCascadeFires++;
                tickCardFireCounts[targetCard] = tickCardFireCounts.GetValueOrDefault(targetCard, 0) + 1;
                forceUseDepth++;
            }
            try
            {
                FireCard(cardOwner, cardOpp, targetCard, isForced: false);
            }
            finally
            {
                if (countFirstAsDepth) forceUseDepth--;
            }

            // 对齐 BazaarCardDealer.cs:4156-4172：耗尽弹药后立即清零剩余溢出冷却进度
            int postAmmoMax = targetCard.EffectiveAmmoMax;
            if (postAmmoMax > 0 && targetCard.CurrentAmmo <= 0)
                targetCard.CooldownProgressMs = 0;
        }

        void HandleShieldApplied(PlayerState targetPlayer, int amt, bool isCrit, CardInstance? srcCard)
        {
            if (amt <= 0) return;
            double mult = mod.ShieldAndRegenMultiplier * (targetPlayer == p2 ? mod.OffenseMultiplierP2 : 1.0);
            int finalAmt = Math.Max(0, (int)Math.Clamp(Math.Round(amt * mult), 0, 1_000_000_000));
            if (!RunBeforeStatusHook(targetPlayer, targetPlayer, srcCard, null, "Shield", ref finalAmt)) return;
            if (finalAmt <= 0) return;
            int prevShield = targetPlayer.Shield;
            targetPlayer.Shield += finalAmt;
            MarkAuraDirty(CombatAuraDependencyMask.Shield);
            FlushDirtyAurasIfNeeded();
            if (targetPlayer == p1) p1ShieldTotal += finalAmt; else p2ShieldTotal += finalAmt;
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.ShieldApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = finalAmt, IsCrit = isCrit });
            DispatchEvent("TTriggerOnCardPerformedShield", targetPlayer, srcCard, srcCard, targetPlayer);
            DispatchEvent("TTriggerOnPlayerShielded", targetPlayer, srcCard, srcCard, targetPlayer, "Shield", prevShield, finalAmt);
            if (targetPlayer.Shield != prevShield)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Shield", prevShield, targetPlayer.Shield);
        }

        void HandleHealApplied(PlayerState targetPlayer, int amt, bool isCrit, CardInstance? srcCard)
        {
            if (amt <= 0) return;
            int finalAmt = targetPlayer == p2 && Math.Abs(mod.OffenseMultiplierP2 - 1.0) > 1e-6
                ? Math.Max(0, (int)Math.Clamp(Math.Round(amt * mod.OffenseMultiplierP2), 0, 1_000_000_000))
                : amt;
            if (!RunBeforeStatusHook(targetPlayer, targetPlayer, srcCard, null, "Heal", ref finalAmt)) return;
            if (finalAmt <= 0) return;
            int prevHp = targetPlayer.CurrentHealth;
            int prevBurn = targetPlayer.Burn;
            int prevPoison = targetPlayer.Poison;
            int overkill = targetPlayer == p1 ? p1Overkill : p2Overkill;
            int missing = Math.Max(0, targetPlayer.HealthMax - targetPlayer.CurrentHealth) + overkill;
            // 对齐官方 OverHeal：在满血状态下治疗或本次治疗恰好达到/溢出 HealthMax 均触发 OverHeal
            bool overheal = finalAmt >= missing;
            int effectiveRestored = ApplyHealthGain(targetPlayer, finalAmt);
            // 8.4 官方规则：普通/小额治疗按治疗量的 5%（向下取整，至少 1 点）各自净化 Burn 与 Poison
            int cleanse = Math.Max(1, (int)Math.Floor(finalAmt * 0.05));
            if (targetPlayer.Burn > 0)
                targetPlayer.Burn = Math.Max(0, targetPlayer.Burn - cleanse);
            if (targetPlayer.Poison > 0)
                targetPlayer.Poison = Math.Max(0, targetPlayer.Poison - cleanse);

            CombatAuraDependencyMask healMask = CombatAuraDependencyMask.None;
            if (targetPlayer.CurrentHealth != prevHp) healMask |= CombatAuraDependencyMask.Health;
            if (targetPlayer.Burn != prevBurn) healMask |= CombatAuraDependencyMask.Burn;
            if (targetPlayer.Poison != prevPoison) healMask |= CombatAuraDependencyMask.Poison;
            if (healMask != CombatAuraDependencyMask.None)
            {
                MarkAuraDirty(healMask);
                FlushDirtyAurasIfNeeded();
            }

            if (recordEvents)
            {
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.HealApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = finalAmt, EffectiveHpRestored = effectiveRestored, IsCrit = isCrit });
                // P2-20：治疗净化扣减灼烧/剧毒时补发战报清理明细
                int burnCleaned = prevBurn - targetPlayer.Burn;
                if (burnCleaned > 0)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.BurnRemoved, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = burnCleaned, Detail = "HealCleanse" });
                int poisonCleaned = prevPoison - targetPlayer.Poison;
                if (poisonCleaned > 0)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PoisonRemoved, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = poisonCleaned, Detail = "HealCleanse" });
            }
            DispatchEvent("TTriggerOnCardPerformedHeal", targetPlayer, srcCard, srcCard, targetPlayer);
            if (overheal)
                DispatchEvent("TTriggerOnCardPerformedOverHeal", targetPlayer, srcCard, srcCard, targetPlayer);
            if (targetPlayer.CurrentHealth != prevHp)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Health", prevHp, targetPlayer.CurrentHealth);
            if (targetPlayer.Burn != prevBurn)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Burn", prevBurn, targetPlayer.Burn);
            if (targetPlayer.Poison != prevPoison)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Poison", prevPoison, targetPlayer.Poison);
        }

        RuleExecutionContext CreateBaseContext(PlayerState self, PlayerState opp)
        {
            return new RuleExecutionContext(repo, RuleScope.InCombat, self, opp, rng)
            {
                LocalPlayerId = p1.PlayerId,
                CurrentDay = self.Day,
                CurrentHour = self.Hour,
                CombatType = combatType,
                CombatBonusCritChance = mod.BonusCritChance,
                CombatCritPercentMultiplierOverride = mod.CritPercentMultiplierOverride,
                ActiveExecution = activeExec,
                EmitCombatEvent = recordEvents ? Emit : null,
                OnTimedCardAttributeModifierApplied = (targetCard, attr, op, val, durationMs) =>
                {
                    // 修复 P0-08：不再跳过 Multiply 0.0（由 CombatZeroMultCount 引用计数承载），到期正常无损恢复
                    timedExpiries.Add(new TimedAttributeExpiry
                    {
                        ExpireTimeMs = timeMs + durationMs,
                        Card = targetCard,
                        Attribute = attr,
                        Operation = op,
                        Value = val
                    });
                },
                OnTimedPlayerAttributeModifierApplied = (targetPlayer, attr, op, val, durationMs) =>
                {
                    timedExpiries.Add(new TimedAttributeExpiry
                    {
                        ExpireTimeMs = timeMs + durationMs,
                        Player = targetPlayer,
                        Attribute = attr,
                        Operation = op,
                        Value = val
                    });
                },
                OnCardTransformBatch = (owner, origCard, replacements, isTemporary, timedDurationMs, origSection, origSlot) =>
                {
                    if (!isTemporary || replacements.Count == 0) return;
                    if (timedDurationMs is > 0)
                    {
                        timedTransformExpiries.Add(new TimedTransformExpiry
                        {
                            ExpireTimeMs = timeMs + timedDurationMs.Value,
                            Owner = owner,
                            OriginalCard = origCard,
                            Replacements = new List<CardInstance>(replacements),
                            OriginalSection = origSection,
                            OriginalSlot = origSlot
                        });
                    }
                    else
                    {
                        var ids = new List<string>(replacements.Count);
                        for (int r = 0; r < replacements.Count; r++)
                            ids.Add(replacements[r].InstanceId);
                        temporaryTransforms.Add(new CombatTemporaryTransformRecord
                        {
                            OwnerPlayerId = owner.PlayerId,
                            OriginalCardInstanceId = origCard.InstanceId,
                            OriginalTemplateId = origCard.Template.Id,
                            OriginalCardType = origCard.Template.Type,
                            OriginalSection = origSection,
                            OriginalSlot = origSlot,
                            TransformedInstanceIds = ids
                        });
                    }
                },
                OnDamageApplied = (targetPlayer, dmg, isCrit, srcCard) =>
                {
                    if (dmg <= 0) return;
                    var attacker = targetPlayer == p1 ? p2 : p1;
                    double scaledDmg = dmg * mod.DamageAndDotMultiplier * (attacker == p2 ? mod.OffenseMultiplierP2 : 1.0);
                    // 对齐官方 PercentDamageReduction（0..100%，含 Memento Mori/Invulnerability Potion 100% 免伤）
                    var red = Math.Clamp(targetPlayer.GetAttribute("PercentDamageReduction"), 0, 100);
                    int actual = Math.Max(0, (int)Math.Clamp(Math.Round(scaledDmg * (1.0 - red / 100.0)), 0, 1_000_000_000));
                    if (!RunBeforeDamageHook(attacker, targetPlayer, srcCard, "Direct", ref actual))
                        return;
                    var dmgSplit = ApplyDirectDamage(targetPlayer, actual, bypassShield: false);

                    if (actual > 0 && srcCard != null && srcCard.GetAttribute("Lifesteal") > 0)
                    {
                        int prevAttackerHp = attacker.CurrentHealth;
                        int effectiveRestored = ApplyHealthGain(attacker, actual);
                        int healed = attacker.CurrentHealth - prevAttackerHp;
                        if (healed > 0)
                        {
                            MarkAuraDirty(CombatAuraDependencyMask.Health);
                            FlushDirtyAurasIfNeeded();
                        }
                        if (recordEvents)
                            Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.HealApplied, SourcePlayerId = attacker.PlayerId, TargetPlayerId = attacker.PlayerId, SourceCardInstanceId = srcCard.InstanceId, Value = actual, EffectiveHpRestored = effectiveRestored, Detail = "Lifesteal" });
                        if (healed > 0)
                        {
                            DispatchEvent("TTriggerOnPlayerAttributeChanged", attacker, srcCard, srcCard, attacker, "Health", prevAttackerHp, attacker.CurrentHealth);
                            DispatchEvent("TTriggerOnPlayerLifeStealed", attacker, srcCard, srcCard, attacker, "Health", prevAttackerHp, healed);
                        }
                    }

                    if (targetPlayer == p2) p1DmgTotal += actual; else p2DmgTotal += actual;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.DamageApplied, SourcePlayerId = attacker.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = actual, ShieldAbsorbed = dmgSplit.ShieldAbsorbed, HpDamage = dmgSplit.HpDamage, IsCrit = isCrit });
                    FlushDirtyAurasIfNeeded();
                    DispatchEvent("TTriggerOnCardPerformedDamage", attacker, srcCard, srcCard, targetPlayer);
                    if (actual > 0)
                        DispatchEvent("TTriggerOnPlayerDamaged", attacker, srcCard, srcCard, targetPlayer, null, 0, actual);
                },
                OnShieldApplied = (targetPlayer, amt, srcCard) => HandleShieldApplied(targetPlayer, amt, isCrit: false, srcCard),
                OnShieldAppliedWithCrit = HandleShieldApplied,
                OnBurnApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    var actor = targetPlayer == p1 ? p2 : p1;
                    double mult = mod.DamageAndDotMultiplier * (actor == p2 ? mod.OffenseMultiplierP2 : 1.0);
                    int finalAmt = Math.Max(0, (int)Math.Clamp(Math.Round(amt * mult), 0, 1_000_000_000));
                    if (!RunBeforeStatusHook(actor, targetPlayer, srcCard, null, "Burn", ref finalAmt)) return;
                    if (finalAmt <= 0) return;
                    int prevBurn = targetPlayer.Burn;
                    targetPlayer.Burn += finalAmt;
                    MarkAuraDirty(CombatAuraDependencyMask.Burn);
                    FlushDirtyAurasIfNeeded();
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.BurnApplied, SourcePlayerId = actor.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = finalAmt });
                    DispatchEvent("TTriggerOnCardPerformedBurn", actor, srcCard, srcCard, targetPlayer);
                    if (targetPlayer.Burn != prevBurn)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", actor, srcCard, srcCard, targetPlayer, "Burn", prevBurn, targetPlayer.Burn);
                },
                OnPoisonApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    var actor = targetPlayer == p1 ? p2 : p1;
                    double mult = mod.DamageAndDotMultiplier * (actor == p2 ? mod.OffenseMultiplierP2 : 1.0);
                    int finalAmt = Math.Max(0, (int)Math.Clamp(Math.Round(amt * mult), 0, 1_000_000_000));
                    if (!RunBeforeStatusHook(actor, targetPlayer, srcCard, null, "Poison", ref finalAmt)) return;
                    if (finalAmt <= 0) return;
                    int prevPoison = targetPlayer.Poison;
                    targetPlayer.Poison += finalAmt;
                    MarkAuraDirty(CombatAuraDependencyMask.Poison);
                    FlushDirtyAurasIfNeeded();
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PoisonApplied, SourcePlayerId = actor.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = finalAmt });
                    DispatchEvent("TTriggerOnCardPerformedPoison", actor, srcCard, srcCard, targetPlayer);
                    if (targetPlayer.Poison != prevPoison)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", actor, srcCard, srcCard, targetPlayer, "Poison", prevPoison, targetPlayer.Poison);
                },
                OnHealApplied = (targetPlayer, amt, srcCard) => HandleHealApplied(targetPlayer, amt, isCrit: false, srcCard),
                OnHealAppliedWithCrit = HandleHealApplied,
                OnRegenApplied = (targetPlayer, amt, srcCard, durationTarget) =>
                {
                    if (amt <= 0) return;
                    double mult = mod.ShieldAndRegenMultiplier * (targetPlayer == p2 ? mod.OffenseMultiplierP2 : 1.0);
                    int finalAmt = Math.Max(0, (int)Math.Clamp(Math.Round(amt * mult), 0, 1_000_000_000));
                    if (!RunBeforeStatusHook(targetPlayer, targetPlayer, srcCard, null, "Regen", ref finalAmt)) return;
                    if (finalAmt <= 0) return;
                    double prevRegen = targetPlayer.GetAttribute("HealthRegen");
                    // 修复 P0-03：严格按 ResolveDurationTarget 写入对应持久域（UntilEndOfCombat -> CombatAdd，仅 Duration==null 写 Permanent）
                    targetPlayer.ModifyAttribute("HealthRegen", "Add", finalAmt, durationTarget);
                    double curRegen = targetPlayer.GetAttribute("HealthRegen");
                    MarkAuraDirty(CombatAuraDependencyMask.Regen);
                    FlushDirtyAurasIfNeeded();
                    // 修复 P0-09：切发独立 CombatEventType.RegenApplied，与 HealApplied 彻底解耦
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.RegenApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = finalAmt, Detail = "RegenGain" });
                    DispatchEvent("TTriggerOnCardPerformedRegen", targetPlayer, srcCard, srcCard, targetPlayer);
                    if (Math.Abs(curRegen - prevRegen) > 1e-6)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "HealthRegen", prevRegen, curRegen);
                },
                OnHasteApplied = (targetCard, ms, srcCard) =>
                    ApplyCardStatusDuration(self, targetCard, ms, srcCard, "PercentHasteReduction", checkRadiantAndFlying: false, CombatEventType.HasteApplied, "TTriggerOnCardPerformedHaste", "Haste", static c => c.HasteMs, static (c, v) => c.HasteMs = v),
                OnSlowApplied = (targetCard, ms, srcCard) =>
                    ApplyCardStatusDuration(self, targetCard, ms, srcCard, "PercentSlowReduction", checkRadiantAndFlying: true, CombatEventType.SlowApplied, "TTriggerOnCardPerformedSlow", "Slow", static c => c.SlowMs, static (c, v) => c.SlowMs = v),
                OnFreezeApplied = (targetCard, ms, srcCard) =>
                    ApplyCardStatusDuration(self, targetCard, ms, srcCard, "PercentFreezeReduction", checkRadiantAndFlying: true, CombatEventType.FreezeApplied, "TTriggerOnCardPerformedFreeze", "Freeze", static c => c.FreezeMs, static (c, v) => c.FreezeMs = v),
                OnChargeApplied = (targetCard, ms, srcCard) =>
                {
                    if (!targetCard.HasActiveCooldown || ms <= 0) return;
                    var pctRed = Math.Clamp(targetCard.GetAttribute("PercentChargeReduction"), 0, 90);
                    int actual = Math.Max(0, (int)Math.Round(ms * (1.0 - pctRed / 100.0)));
                    var cardOwner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard)) ? p1
                                  : ((p2.Hand.Cards.Contains(targetCard) || p2.Skills.Contains(targetCard)) ? p2 : null);
                    if (!RunBeforeStatusHook(self, cardOwner, srcCard, targetCard, "Charge", ref actual)) return;
                    if (actual <= 0) return;
                    int cdMax = cardOwner != null ? GetEffectiveCardCooldownMaxMs(targetCard, cardOwner) : GetCardCooldownMaxMs(targetCard);
                    // 修复 P1-54：单次充能严格钳制至 cdMax（对齐官方 Value = Min(ValueMax, Value + amount)），超额充能不溢出至下一周期
                    targetCard.CooldownProgressMs = Math.Min(cdMax, targetCard.CooldownProgressMs + actual);
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.ChargeApplied, SourceCardInstanceId = srcCard?.InstanceId, TargetCardInstanceId = targetCard.InstanceId, Value = actual });
                    DispatchEvent("TTriggerOnCardCharged", self, srcCard, targetCard, self);
                    DispatchEvent("TTriggerOnCardPerformedCharge", self, srcCard, targetCard, self);

                    // 7.5：Charge 推满冷却时在当前 50ms tick 立即触发（受单卡内置冷却与防死循环帧预算约束）
                    if (cardOwner == null) return;
                    CompleteReadyCooldowns(cardOwner, cardOwner == p1 ? p2 : p1, targetCard, countFirstAsDepth: true);
                },
                OnActionCostSpent = (srcCard, attr, amt) =>
                {
                    var costMask = MapAttributeToDependencyMask(attr);
                    if (costMask != CombatAuraDependencyMask.None)
                    {
                        MarkAuraDirty(costMask);
                        FlushDirtyAurasIfNeeded();
                    }
                    if (recordEvents && srcCard != null)
                        Emit(new CombatEvent
                        {
                            TimeMs = timeMs,
                            Type = CombatEventType.ActionCostSpent,
                            SourcePlayerId = self.PlayerId,
                            SourceCardInstanceId = srcCard.InstanceId,
                            Detail = attr,
                            Value = amt
                        });
                },
                OnForceUseRequested = (targetCard, srcCard) =>
                {
                    if (forceUseDepth >= MaxForceUseDepth || tickCascadeFires >= MaxTickCascadeFires || targetCard == null || targetCard.IsDestroyed || targetCard.IsDisabled)
                        return;
                    if (activeFiringCards.Contains(targetCard))
                        return;
                    if (tickCardFireCounts.GetValueOrDefault(targetCard, 0) >= MaxSameTickCardFires)
                        return;
                    // 对齐 BazaarCardDealer.cs:1099：被冻结、无有效冷却或空弹药时禁止强制触发
                    if (targetCard.FreezeMs > 0 || !targetCard.HasActiveCooldown)
                        return;
                    int targetAmmoMax = targetCard.EffectiveAmmoMax;
                    if (targetAmmoMax > 0 && targetCard.CurrentAmmo <= 0)
                        return;
                    var owner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard)) ? p1
                              : ((p2.Hand.Cards.Contains(targetCard) || p2.Skills.Contains(targetCard)) ? p2 : null);
                    if (owner == null) return;
                    var oppP = owner == p1 ? p2 : p1;
                    tickCascadeFires++;
                    tickCardFireCounts[targetCard] = tickCardFireCounts.GetValueOrDefault(targetCard, 0) + 1;
                    forceUseDepth++;
                    try
                    {
                        FireCard(owner, oppP, targetCard, isForced: true);
                    }
                    finally
                    {
                        forceUseDepth--;
                    }
                },
                OnBeginSandstormRequested = () =>
                {
                    if (!sandstormActive)
                    {
                        sandstormForceStarted = true;
                        sandstormActive = true;
                        sandstormPendingImmediateTick = true;
                        sandstormElapsedMs = 0;
                        if (!sandstormWarned)
                        {
                            sandstormWarned = true;
                            if (recordEvents)
                                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormWarning, Detail = "ForcedSandstorm" });
                        }
                        // P2-44：沙暴为全局环境事件，subjectPlayer 规范化为 null 避免对 P2 侧误传 p1
                        DispatchEvent("TTriggerOnSandstorm", p1, null, null, null);
                    }
                },
                OnCardTriggeredEvent = (evName, subjOwner, subjOpp, srcCard) =>
                {
                    if (evName is "TTriggerOnCardTransformed" or "TTriggerOnCardAfterTransform" or "TTriggerOnCardEnchanted" or "TTriggerOnCardUpgraded")
                        triggerIndexDirty = true;
                    if (evName == "TTriggerOnPlayerEnraged")
                    {
                        SyncEnragedState(p1);
                        SyncEnragedState(p2);
                        RecalculateCombatAuras();
                    }
                    DispatchEvent(evName, subjOwner, srcCard, srcCard, subjOwner);
                },
                OnDetailedTriggeredEvent = (evName, actor, src, tgt, subj, attr, prev, cur) =>
                {
                    if (evName is "TTriggerOnCardTransformed" or "TTriggerOnCardAfterTransform" or "TTriggerOnCardEnchanted" or "TTriggerOnCardUpgraded")
                        triggerIndexDirty = true;
                    if (evName == "TTriggerOnPlayerEnraged")
                    {
                        SyncEnragedState(p1);
                        SyncEnragedState(p2);
                        RecalculateCombatAuras();
                    }
                    else if (!string.IsNullOrEmpty(attr)
                        && (evName == "TTriggerOnPlayerAttributeChanged" || evName == "TTriggerOnCardAttributeChanged"))
                    {
                        var depBit = MapAttributeToDependencyMask(attr);
                        if (depBit != CombatAuraDependencyMask.None)
                        {
                            MarkAuraDirty(depBit);
                            FlushDirtyAurasIfNeeded();
                        }
                    }
                    DispatchEvent(evName, actor, src, tgt, subj, attr, prev, cur);
                },
                OnQuestCompleted = completedCard =>
                {
                    triggerIndexDirty = true;
                    if (questDispatchDepth < 4)
                    {
                        questDispatchDepth++;
                        try
                        {
                            var owner = (p1.Hand.Cards.Contains(completedCard) || p1.Stash.Cards.Contains(completedCard) || p1.Skills.Contains(completedCard) || p1.SocketEffects.Contains(completedCard))
                                ? p1
                                : p2;
                            DispatchEvent("TTriggerOnCardQuestCompleted", owner, completedCard, completedCard, owner);
                        }
                        finally
                        {
                            questDispatchDepth--;
                        }
                    }
                },
                OnBeforeCardStateChange = (targetCard, srcCard, changeKind, reason) =>
                {
                    if (mechanicRuntime == null) return true;
                    var targetOwner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard) || p1.Stash.Cards.Contains(targetCard)) ? p1 : p2;
                    var pipeEv = new CombatPipelineEvent
                    {
                        HookName = "BeforeCardStateChange",
                        TimeMs = timeMs,
                        SourcePlayer = self,
                        TargetPlayer = targetOwner,
                        SourceCard = srcCard,
                        TargetCard = targetCard,
                        ChangeKind = changeKind,
                        Reason = reason
                    };
                    mechanicRuntime.DispatchPipeline(pipeEv);
                    return !pipeEv.Cancelled;
                }
            };
        }

        p1BaseCtx = CreateBaseContext(p1, p2);
        p2BaseCtx = CreateBaseContext(p2, p1);

        // V14 开放沙盒机制按需激活（纯原版对局 0 分配、0 性能损耗）
        var activeMechDefs = mechanicDefinitionsOverride ?? ModRuntimeHooks.GetRegisteredMechanics();
        var activeInterceptors = pipelineInterceptorsOverride ?? ModRuntimeHooks.GetPipelineInterceptors();
        if (CombatMechanicRuntime.ShouldActivate(p1, p2, activeMechDefs, activeInterceptors is { Count: > 0 }))
        {
            var frozenDefs = activeMechDefs is FrozenDictionary<string, MechanicDefinition> fd
                ? fd
                : (activeMechDefs != null ? activeMechDefs.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase) : FrozenDictionary<string, MechanicDefinition>.Empty);

            mechanicRuntime = new CombatMechanicRuntime(
                p1,
                p2,
                rng,
                new CombatMechanicHostCallbacks
                {
                    GetTimeMs = () => timeMs,
                    DealDamage = (targetPlayer, srcCard, dmg, bypassShield, vfxKey) =>
                    {
                        if (dmg <= 0) return;
                        var attacker = targetPlayer == p1 ? p2 : p1;
                        int actual = dmg;
                        if (!RunBeforeDamageHook(attacker, targetPlayer, srcCard, "Direct", ref actual)) return;
                        var dmgSplit = ApplyDirectDamage(targetPlayer, actual, bypassShield);
                        if (targetPlayer == p2) p1DmgTotal += actual; else p2DmgTotal += actual;
                        if (recordEvents)
                            Emit(new CombatEvent
                            {
                                TimeMs = timeMs,
                                Type = CombatEventType.DamageApplied,
                                SourcePlayerId = attacker.PlayerId,
                                TargetPlayerId = targetPlayer.PlayerId,
                                SourceCardInstanceId = srcCard?.InstanceId,
                                Value = actual,
                                ShieldAbsorbed = dmgSplit.ShieldAbsorbed,
                                HpDamage = dmgSplit.HpDamage,
                                VfxKeyOverride = vfxKey
                            });
                        DispatchEvent("TTriggerOnCardPerformedDamage", attacker, srcCard, srcCard, targetPlayer);
                        if (actual > 0)
                            DispatchEvent("TTriggerOnPlayerDamaged", attacker, srcCard, srcCard, targetPlayer, null, 0, actual);
                    },
                    ApplyStatus = (status, actorPlayer, srcCard, targetCard, targetPlayer, amt) =>
                    {
                        var ctx = actorPlayer == p1 ? p1BaseCtx : p2BaseCtx;
                        switch (status.ToLowerInvariant())
                        {
                            case "shield": if (targetPlayer != null) ctx.OnShieldApplied?.Invoke(targetPlayer, amt, srcCard); break;
                            case "burn": if (targetPlayer != null) ctx.OnBurnApplied?.Invoke(targetPlayer, amt, srcCard); break;
                            case "poison": if (targetPlayer != null) ctx.OnPoisonApplied?.Invoke(targetPlayer, amt, srcCard); break;
                            case "heal": if (targetPlayer != null) ctx.OnHealApplied?.Invoke(targetPlayer, amt, srcCard); break;
                            case "regen": if (targetPlayer != null) ctx.OnRegenApplied?.Invoke(targetPlayer, amt, srcCard, DurationTarget.Combat); break;
                            case "haste": if (targetCard != null) ctx.OnHasteApplied?.Invoke(targetCard, amt, srcCard); break;
                            case "slow": if (targetCard != null) ctx.OnSlowApplied?.Invoke(targetCard, amt, srcCard); break;
                            case "freeze": if (targetCard != null) ctx.OnFreezeApplied?.Invoke(targetCard, amt, srcCard); break;
                            case "charge": if (targetCard != null) ctx.OnChargeApplied?.Invoke(targetCard, amt, srcCard); break;
                        }
                    },
                    ChangeCardState = (targetCard, srcCard, changeKind, reason, suppressNormalDestroyTriggers, vfxKey) =>
                    {
                        var targetOwner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard) || p1.Stash.Cards.Contains(targetCard)) ? p1 : p2;
                        var srcOwner = srcCard != null && (p1.Hand.Cards.Contains(srcCard) || p1.Skills.Contains(srcCard)) ? p1 : p2;
                        var pipeEv = new CombatPipelineEvent
                        {
                            HookName = "BeforeCardStateChange",
                            TimeMs = timeMs,
                            SourcePlayer = srcOwner,
                            TargetPlayer = targetOwner,
                            SourceCard = srcCard,
                            TargetCard = targetCard,
                            ChangeKind = changeKind,
                            Reason = reason
                        };
                        mechanicRuntime?.DispatchPipeline(pipeEv);
                        if (pipeEv.Cancelled) return;

                        if (changeKind.Equals("Repair", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!targetCard.IsDisabled && !targetCard.IsDestroyed) return;
                            targetCard.IsDisabled = false;
                            targetCard.IsDestroyed = false;
                            DispatchEvent("TTriggerOnCardRepaired", targetOwner, srcCard, targetCard, targetOwner);
                        }
                        else if (changeKind.Equals("Disable", StringComparison.OrdinalIgnoreCase)
                                 || changeKind.Equals("Destroy", StringComparison.OrdinalIgnoreCase))
                        {
                            if (targetCard.IsDisabled || targetCard.IsDestroyed) return;
                            if (!suppressNormalDestroyTriggers)
                                DispatchEvent("TTriggerOnBeforeCardDestroyed", targetOwner, srcCard, targetCard, targetOwner);
                            if (changeKind.Equals("Destroy", StringComparison.OrdinalIgnoreCase))
                                targetCard.IsDestroyed = true;
                            else
                                targetCard.IsDisabled = true;
                            if (recordEvents)
                            {
                                Emit(new CombatEvent
                                {
                                    TimeMs = timeMs,
                                    Type = CombatEventType.CardDestroyed,
                                    SourcePlayerId = srcOwner.PlayerId,
                                    TargetPlayerId = targetOwner.PlayerId,
                                    SourceCardInstanceId = srcCard?.InstanceId,
                                    TargetCardInstanceId = targetCard.InstanceId,
                                    Detail = reason ?? (targetCard.Template.Localization?.Title?.Text ?? targetCard.Template.Id),
                                    VfxKeyOverride = vfxKey
                                });
                            }
                            if (!suppressNormalDestroyTriggers)
                            {
                                if (changeKind.Equals("Disable", StringComparison.OrdinalIgnoreCase))
                                    DispatchEvent("TTriggerOnCardDisabled", targetOwner, srcCard, targetCard, targetOwner);
                                DispatchEvent("TTriggerOnCardPerformedDestruction", srcOwner, srcCard, targetCard, targetOwner);
                            }
                        }
                        RecalculateCombatAuras();
                    },
                    ForceUseCard = (targetCard, srcCard) =>
                    {
                        var ctx = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard)) ? p1BaseCtx : p2BaseCtx;
                        ctx.OnForceUseRequested?.Invoke(targetCard, srcCard);
                    },
                    RecalculateAuras = () => RecalculateCombatAuras(),
                    IsValidVfxKey = key =>
                    {
                        var resolved = ModRuleCatalog.ResolveOfficialVfxKey(key);
                        return !string.IsNullOrWhiteSpace(resolved)
                            && resolved.Length <= 160
                            && resolved.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' or ' ' or '/' or '.');
                    },
                    OnCardCounterSynced = (card, syncAttr, newVal) =>
                    {
                        if (!recordEvents) return;
                        var owner = (p1.Hand.Cards.Contains(card) || p1.Skills.Contains(card) || p1.Stash.Cards.Contains(card)) ? p1 : p2;
                        Emit(new CombatEvent
                        {
                            TimeMs = timeMs,
                            Type = CombatEventType.CardAttributeModified,
                            SourcePlayerId = owner.PlayerId,
                            TargetPlayerId = owner.PlayerId,
                            SourceCardInstanceId = card.InstanceId,
                            TargetCardInstanceId = card.InstanceId,
                            Detail = syncAttr,
                            Value = newVal
                        });
                    }
                },
                frozenDefs,
                activeInterceptors);
        }

        void EnsureTriggerIndex()
        {
            int curTotal = p1.Hand.Cards.Count + p1.Skills.Count + p1.SocketEffects.Count + p1.PlayerEffects.Count + p1.Stash.Cards.Count
                         + p2.Hand.Cards.Count + p2.Skills.Count + p2.SocketEffects.Count + p2.PlayerEffects.Count + p2.Stash.Cards.Count;
            if (!triggerIndexDirty && curTotal == indexedCardTotal) return;

            p1TriggerIndex = BuildSideTriggerIndex(p1);
            p2TriggerIndex = BuildSideTriggerIndex(p2);
            p1HasQuests = HasAnyQuests(p1);
            p2HasQuests = HasAnyQuests(p2);
            RefreshCombatAuraDependencyMask();
            indexedCardTotal = curTotal;
            triggerIndexDirty = false;
        }

        void DispatchSide(
            List<IndexedAbility>? indexedList,
            PlayerState owner,
            PlayerState opp,
            RuleExecutionContext baseCtx,
            string eventType,
            CardInstance? sourceCard,
            CardInstance? targetCard,
            PlayerState? subjectPlayer,
            string? attrChanged,
            double prev,
            double cur)
        {
            if (indexedList == null || indexedList.Count == 0) return;

            baseCtx.TimeMs = timeMs;
            double attrDelta = Math.Abs(cur - prev);
            baseCtx.LastAttributeChangeDelta = attrDelta;

            // TTriggerOnPlayerDied（对齐 BazaarCardDealer.cs:2749,2824 OnDeath 即时结算）与 TTriggerOnBeforeCardDestroyed 不进 250ms ICD 队列：
            // 1. 若同触发器存在对同一 Custom_* 做 Equal 0 / LessThan 限次的兄弟能力（如 Memento Mori / Buster），将自增计数延后到同轮末尾执行；
            // 2. 若同触发器存在对 Custom_* 做 GreaterThan 激活的联动前置条件（如 Grabinator Priority=Immediate 联动），先执行先序递增并实时重估前置条件；
            // 3. 在 TTriggerOnBeforeCardDestroyed 中，将变形自身的 TActionCardTransform* 排在同卡其他亡语动作之后执行，避免提前置 IsDestroyed 截断己方亡语。
            bool executeAllImmediate = eventType is "TTriggerOnPlayerDied" or "TTriggerOnBeforeCardDestroyed";
            List<(CardInstance Card, AbilityDefinition Ability, RuleExecutionContext Ctx)>? localImmediate = null;
            List<(CardInstance Card, AbilityDefinition Ability, RuleExecutionContext Ctx)>? localImmediateTransforms = null;
            List<(CardInstance Card, AbilityDefinition Ability, RuleExecutionContext Ctx)>? localDeferredCounters = null;

            for (int i = 0; i < indexedList.Count; i++)
            {
                var entry = indexedList[i];
                var card = entry.Card;
                if (card.IsDestroyed || card.IsDisabled) continue;
                if (eventType == "TTriggerOnCardFired" && sourceCard != card) continue;

                var ab = entry.Ability;
                var abCtx = baseCtx.WithCards(card, sourceCard, targetCard);
                if (sandstormForceStarted)
                    ValueAndConditionEvaluator.SetSandstormActive(abCtx, true);

                if (executeAllImmediate)
                {
                    if (!AbilityExecutor.MatchesTrigger(ab.Trigger, eventType, card, owner, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur, combatType, "", abCtx))
                        continue;

                    if (IsEndOfEventCounterLimiter(indexedList, card, ab))
                    {
                        if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                            continue;
                        localDeferredCounters ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(2);
                        localDeferredCounters.Add((card, ab, abCtx));
                    }
                    else if (ab.Action?.Type is "TActionCardTransform" or "TActionCardTransformDestroyed")
                    {
                        localImmediateTransforms ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(2);
                        localImmediateTransforms.Add((card, ab, abCtx));
                    }
                    else
                    {
                        if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                            continue;
                        // 先序 Immediate 状态修改（如 Grabinator 0/1/6）立即执行，供后续联动能力（2）实时读取最新 Custom_*
                        FlushDirtyAurasIfNeeded();
                        var prevAb = activeExec.AbilityId;
                        var prevTrigSrc = activeExec.TriggerSourceCardInstanceId;
                        activeExec.AbilityId = ab.Id;
                        activeExec.TriggerSourceCardInstanceId = (sourceCard ?? targetCard)?.InstanceId;
                        try
                        {
                            AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                        }
                        finally
                        {
                            activeExec.AbilityId = prevAb;
                            activeExec.TriggerSourceCardInstanceId = prevTrigSrc;
                        }
                        SyncEnragedState(p1);
                        SyncEnragedState(p2);
                        localImmediate ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(4);
                        localImmediate.Add((card, ab, abCtx));
                    }
                    continue;
                }

                if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                    continue;

                if (eventType == "TTriggerOnCardAttributeChanged"
                    && IsSelfArmedHasteLatchReaction(card, ab)
                    && sourceCard != null
                    && !ReferenceEquals(sourceCard, card))
                    continue;

                if (!AbilityExecutor.MatchesTrigger(ab.Trigger, eventType, card, owner, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur, combatType, "", abCtx))
                    continue;

                if (eventType == "TTriggerOnCardFired" && sourceCard == card)
                {
                    localImmediate ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(4);
                    localImmediate.Add((card, ab, abCtx));
                }
                else if (IsSelfCounterIncrementAbility(ab))
                {
                    // 同卡自增计数（Custom_*）在同轮匹配完成后即时更新，避免占用主效果 250ms ICD 槽位或引起首触发重入
                    localDeferredCounters ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(2);
                    localDeferredCounters.Add((card, ab, abCtx));
                }
                else if (string.Equals(card.Template.Type, "PlayerEffect", StringComparison.OrdinalIgnoreCase)
                    || IsStatGainAbility(ab)
                    || (string.Equals(ab.Priority, "Immediate", StringComparison.OrdinalIgnoreCase) && tickImmediateExecs < MaxTickImmediateExecs))
                {
                    // 系统级 PlayerEffect、数值成长类效果（Stat Gains）与 Priority == "Immediate" 触发豁免 250ms ICD 节流（P1-21：带单帧熔断上限防死循环）
                    if (string.Equals(ab.Priority, "Immediate", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(card.Template.Type, "PlayerEffect", StringComparison.OrdinalIgnoreCase)
                        && !IsStatGainAbility(ab))
                    {
                        tickImmediateExecs++;
                    }
                    localImmediate ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(4);
                    localImmediate.Add((card, ab, abCtx));
                }
                else
                {
                    // 修复 P1-21：单能力粒度 ICD 队列键，避免同卡多触发能力共用 250ms 槽位彼此挤占
                    var key = new TriggerQueueKey(owner.PlayerId, card.InstanceId, ab.Id ?? string.Empty);
                    if (!triggerQueues.TryGetValue(key, out var q))
                    {
                        q = new Queue<QueuedTrigger>();
                        triggerQueues[key] = q;
                    }
                    if (q.Count < 32) // 单槽队列安全上限防止无限积压内存
                    {
                        var queuedSource = eventType == "TTriggerOnCardAttributeChanged"
                            ? (targetCard ?? sourceCard)
                            : sourceCard;
                        var queuedTarget = eventType == "TTriggerOnCardAttributeChanged"
                            ? (targetCard ?? sourceCard)
                            : targetCard;
                        q.Enqueue(new QueuedTrigger
                        {
                            Owner = owner,
                            Opponent = opp,
                            OwnerCard = card,
                            Ability = ab,
                            SourceCard = queuedSource,
                            TargetCard = queuedTarget,
                            AttributeDelta = attrDelta,
                            AllowWhenDestroyed = eventType == "TTriggerOnBeforeCardDestroyed"
                        });
                    }
                }
            }

            if (!executeAllImmediate && localImmediate != null)
            {
                bool ranNonCardFiredImmediate = false;
                for (int i = 0; i < localImmediate.Count; i++)
                {
                    FlushDirtyAurasIfNeeded();
                    var (card, ab, abCtx) = localImmediate[i];
                    if (card.IsDestroyed || card.IsDisabled) continue;
                    if (i > 0 && eventType != "TTriggerOnCardFired"
                        && !ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx))
                    {
                        continue;
                    }
                    var prevAb = activeExec.AbilityId;
                    var prevTrigSrc = activeExec.TriggerSourceCardInstanceId;
                    bool prevInCardFired = activeExec.InCardFiredExecution;
                    activeExec.AbilityId = ab.Id;
                    activeExec.TriggerSourceCardInstanceId = eventType == "TTriggerOnCardFired"
                        ? card.InstanceId
                        : (sourceCard ?? targetCard)?.InstanceId;
                    activeExec.InCardFiredExecution = eventType == "TTriggerOnCardFired"
                        && ReferenceEquals(card, activeExec.CurrentCastCard);
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                        if (eventType != "TTriggerOnCardFired"
                            && (IsStatGainAbility(ab) || string.Equals(ab.Priority, "Immediate", StringComparison.OrdinalIgnoreCase)))
                            ranNonCardFiredImmediate = true;
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                        activeExec.TriggerSourceCardInstanceId = prevTrigSrc;
                        activeExec.InCardFiredExecution = prevInCardFired;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                    // 修复 P0-07：同卡多条 TTriggerOnCardFired 兄弟能力之间若产生状态/属性变动，在下一条能力前即时重算动态光环
                    if (eventType == "TTriggerOnCardFired" && i + 1 < localImmediate.Count)
                        FlushDirtyAurasIfNeeded();
                }
                if (ranNonCardFiredImmediate)
                {
                    RecalculateCombatAuras();
                    ClampSideAmmo(p1);
                    ClampSideAmmo(p2);
                }
            }

            if (localImmediateTransforms != null)
            {
                for (int i = 0; i < localImmediateTransforms.Count; i++)
                {
                    var (card, ab, abCtx) = localImmediateTransforms[i];
                    if (card.IsDestroyed || card.IsDisabled) continue;
                    if (!ValueAndConditionEvaluator.EvaluatePrerequisites(ab.Prerequisites, abCtx)) continue;
                    var prevAb = activeExec.AbilityId;
                    var prevTrigSrc = activeExec.TriggerSourceCardInstanceId;
                    activeExec.AbilityId = ab.Id;
                    activeExec.TriggerSourceCardInstanceId = (sourceCard ?? targetCard)?.InstanceId;
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                        activeExec.TriggerSourceCardInstanceId = prevTrigSrc;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                }
            }

            if (executeAllImmediate && (localImmediate != null || localImmediateTransforms != null))
            {
                RecalculateCombatAuras();
                ClampSideAmmo(p1);
                ClampSideAmmo(p2);
            }

            if (localDeferredCounters != null)
            {
                for (int i = 0; i < localDeferredCounters.Count; i++)
                {
                    var (card, ab, abCtx) = localDeferredCounters[i];
                    if (!executeAllImmediate && (card.IsDestroyed || card.IsDisabled)) continue;
                    var prevAb = activeExec.AbilityId;
                    var prevTrigSrc = activeExec.TriggerSourceCardInstanceId;
                    activeExec.AbilityId = ab.Id;
                    activeExec.TriggerSourceCardInstanceId = (sourceCard ?? targetCard)?.InstanceId;
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                        activeExec.TriggerSourceCardInstanceId = prevTrigSrc;
                    }
                }
            }
        }

        void DispatchEvent(string eventType, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard, PlayerState? subjectPlayer, string? attrChanged = null, double prev = 0, double cur = 0)
        {
            if (dispatchDepth >= MaxDispatchDepth) return;
            bool startedChain = dispatchDepth == 0 && string.IsNullOrEmpty(activeExec.ExecutionContextId);
            string? prevChainId = activeExec.ExecutionContextId;
            if (startedChain)
                activeExec.ExecutionContextId = $"chain_{timeMs}_{++execChainSeq}";
            dispatchDepth++;
            try
            {
            EnsureTriggerIndex();
            p1TriggerIndex.TryGetValue(eventType, out var p1List);
            p2TriggerIndex.TryGetValue(eventType, out var p2List);
            if ((p1List != null && p1List.Count > 0) || (p2List != null && p2List.Count > 0) || p1HasQuests || p2HasQuests)
            {
                DispatchSide(p1List, p1, p2, p1BaseCtx, eventType, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur);
                DispatchSide(p2List, p2, p1, p2BaseCtx, eventType, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur);
            }

            if (mechanicRuntime != null)
            {
                mechanicRuntime.DispatchPipeline(new CombatPipelineEvent
                {
                    HookName = "OnTriggerEvent",
                    TimeMs = timeMs,
                    SourcePlayer = actorPlayer,
                    TargetPlayer = subjectPlayer,
                    SourceCard = sourceCard,
                    TargetCard = targetCard,
                    TriggerEventType = eventType,
                    Amount = Math.Abs(cur - prev),
                    PayloadValue = cur
                });
            }

            if (!p1HasQuests && !p2HasQuests)
                return;

            if ((p1HasQuests || p2HasQuests) && questDispatchDepth < 4)
            {
                double attrDelta = Math.Abs(cur - prev);
                if (p1HasQuests)
                {
                    questDispatchDepth++;
                    try
                    {
                        p1BaseCtx.TimeMs = timeMs;
                        p1BaseCtx.LastAttributeChangeDelta = attrDelta;
                        AbilityExecutor.TryAdvanceQuests(eventType, p1, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur, combatType, "", p1BaseCtx);
                    }
                    finally
                    {
                        questDispatchDepth--;
                    }
                }
                if (p2HasQuests)
                {
                    questDispatchDepth++;
                    try
                    {
                        p2BaseCtx.TimeMs = timeMs;
                        p2BaseCtx.LastAttributeChangeDelta = attrDelta;
                        AbilityExecutor.TryAdvanceQuests(eventType, p2, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur, combatType, "", p2BaseCtx);
                    }
                    finally
                    {
                        questDispatchDepth--;
                    }
                }
            }
            }
            finally
            {
                dispatchDepth--;
                if (startedChain)
                    activeExec.ExecutionContextId = prevChainId;
            }
        }

        void ProcessIcdQueues()
        {
            if (triggerQueues.Count == 0) return;

            // 先快照收集到点触发项，再按确定性顺序排序执行（防止回调 DispatchEvent 向 triggerQueues 插入新 key 引发枚举修改异常并消除字典序漂移）
            for (int pass = 0; pass < 8; pass++)
            {
                readyBatch.Clear();
                foreach (var (key, q) in triggerQueues)
                {
                    while (q.Count > 0 && ((q.Peek().OwnerCard.IsDestroyed || q.Peek().OwnerCard.IsDisabled) && !q.Peek().AllowWhenDestroyed))
                        q.Dequeue();
                    if (q.Count == 0) continue;
                    int readyAt = icdReadyTime.GetValueOrDefault(key, 0);
                    if (timeMs < readyAt) continue;

                    readyBatch.Add((key, q.Dequeue(), readyAt));
                    icdReadyTime[key] = timeMs + icdMs;
                }

                if (readyBatch.Count == 0) break;

                if (readyBatch.Count > 1)
                    readyBatch.Sort(readyBatchComparison);

                for (int i = 0; i < readyBatch.Count; i++)
                {
                    var batchEntry = readyBatch[i];
                    var item = batchEntry.Item;
                    if ((item.OwnerCard.IsDestroyed || item.OwnerCard.IsDisabled) && !item.AllowWhenDestroyed)
                    {
                        icdReadyTime[batchEntry.Key] = batchEntry.PrevReadyAt;
                        continue;
                    }

                    var baseCtx = item.Owner == p1 ? p1BaseCtx : p2BaseCtx;
                    baseCtx.TimeMs = timeMs;
                    baseCtx.LastAttributeChangeDelta = item.AttributeDelta;
                    var abCtx = baseCtx.WithCards(item.OwnerCard, item.SourceCard, item.TargetCard);
                    if (sandstormForceStarted)
                        ValueAndConditionEvaluator.SetSandstormActive(abCtx, true);
                    FlushDirtyAurasIfNeeded();
                    // 修复 P1-21：出队执行前实时重检 Prerequisites，过滤 250ms 等待期内已失效的触发项并退还 ICD
                    // （同时放行已在入队门控消费并被同卡 Immediate 兄弟能力推进/清零的自控 Self.Custom_* 限次计数器与 Haste 锁存条件）
                    if (!EvaluateQueuedPrerequisites(item.OwnerCard, item.Ability.Prerequisites, abCtx))
                    {
                        icdReadyTime[batchEntry.Key] = batchEntry.PrevReadyAt;
                        continue;
                    }
                    var prevAb = activeExec.AbilityId;
                    var prevTrigSrc = activeExec.TriggerSourceCardInstanceId;
                    var prevExecCtxId = activeExec.ExecutionContextId;
                    activeExec.AbilityId = item.Ability.Id;
                    activeExec.TriggerSourceCardInstanceId = (item.SourceCard ?? item.TargetCard)?.InstanceId;
                    if (string.IsNullOrEmpty(activeExec.ExecutionContextId))
                        activeExec.ExecutionContextId = $"chain_{timeMs}_{++execChainSeq}";
                    try
                    {
                        AbilityExecutor.ExecuteAction(item.Ability.Action, item.OwnerCard, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                        activeExec.TriggerSourceCardInstanceId = prevTrigSrc;
                        activeExec.ExecutionContextId = prevExecCtxId;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                    RecalculateCombatAuras();
                    ClampSideAmmo(p1);
                    ClampSideAmmo(p2);
                }
            }
        }

        void ProcessTimedExpiries()
        {
            if (timedExpiries.Count == 0 && timedTransformExpiries.Count == 0) return;
            bool anyExpired = false;
            for (int i = timedExpiries.Count - 1; i >= 0; i--)
            {
                var exp = timedExpiries[i];
                if (timeMs < exp.ExpireTimeMs) continue;
                timedExpiries.RemoveAt(i);
                anyExpired = true;

                if (exp.Card != null)
                {
                    bool hadKeyBefore = exp.Card.HasAnyAttributeKey(exp.Attribute);
                    double prev = exp.Card.GetAttribute(exp.Attribute);
                    exp.Card.RevertCombatAttributeModifier(exp.Attribute, exp.Operation, exp.Value);
                    double cur = exp.Card.GetAttribute(exp.Attribute);
                    bool keyDeleted = hadKeyBefore && !exp.Card.HasAnyAttributeKey(exp.Attribute);
                    if (Math.Abs(cur - prev) > 1e-6 || keyDeleted)
                    {
                        var owner = (p1.Hand.Cards.Contains(exp.Card) || p1.Stash.Cards.Contains(exp.Card) || p1.Skills.Contains(exp.Card) || p1.SocketEffects.Contains(exp.Card))
                            ? p1 : p2;
                        if (recordEvents && Math.Abs(cur - prev) > 1e-6)
                        {
                            Emit(new CombatEvent
                            {
                                TimeMs = timeMs,
                                Type = CombatEventType.CardAttributeModified,
                                SourcePlayerId = owner.PlayerId,
                                TargetPlayerId = owner.PlayerId,
                                TargetCardInstanceId = exp.Card.InstanceId,
                                Value = cur - prev,
                                Detail = exp.Attribute,
                                ActionType = "TCombatDurationExpired"
                            });
                        }
                        if (recordEvents && keyDeleted)
                        {
                            Emit(new CombatEvent
                            {
                                TimeMs = timeMs,
                                Type = CombatEventType.CardAttributeDeleted,
                                SourcePlayerId = owner.PlayerId,
                                TargetPlayerId = owner.PlayerId,
                                TargetCardInstanceId = exp.Card.InstanceId,
                                Detail = exp.Attribute,
                                ActionType = "TCombatDurationExpired"
                            });
                        }
                        if (Math.Abs(cur - prev) > 1e-6)
                            DispatchEvent("TTriggerOnCardAttributeChanged", owner, exp.Card, exp.Card, owner, exp.Attribute, prev, cur);
                    }
                }
                else if (exp.Player != null)
                {
                    double prev = exp.Player.GetAttribute(exp.Attribute);
                    exp.Player.RevertCombatAttributeModifier(exp.Attribute, exp.Operation, exp.Value);
                    if (exp.Attribute.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
                        exp.Player.CurrentHealth = Math.Min(exp.Player.CurrentHealth, exp.Player.HealthMax);
                    double cur = exp.Player.GetAttribute(exp.Attribute);
                    if (Math.Abs(cur - prev) > 1e-6)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", exp.Player, null, null, exp.Player, exp.Attribute, prev, cur);
                }
            }

            // P1-10：战内限时变形到期还原（移除临时产物、恢复原卡并播报 CardTransformReverted）
            for (int i = timedTransformExpiries.Count - 1; i >= 0; i--)
            {
                var texp = timedTransformExpiries[i];
                if (timeMs < texp.ExpireTimeMs) continue;
                timedTransformExpiries.RemoveAt(i);
                anyExpired = true;
                triggerIndexDirty = true;

                var owner = texp.Owner;
                var orig = texp.OriginalCard;
                var revertedIds = new List<string>(texp.Replacements.Count);
                for (int r = 0; r < texp.Replacements.Count; r++)
                {
                    var rep = texp.Replacements[r];
                    revertedIds.Add(rep.InstanceId);
                    rep.IsDestroyed = true;
                    owner.Hand.Remove(rep);
                    owner.Stash.Remove(rep);
                    owner.Skills.Remove(rep);
                    owner.SocketEffects.Remove(rep);
                }

                orig.IsDestroyed = false;
                orig.IsDisabled = false;
                switch (texp.OriginalSection)
                {
                    case "Stash":
                        if (texp.OriginalSlot < 0 || !owner.Stash.TryPlaceWithDisplaceOrSwap(orig, texp.OriginalSlot))
                            owner.Stash.TryAddAuto(orig);
                        break;
                    case "Skill":
                        if (!owner.Skills.Contains(orig)) owner.Skills.Add(orig);
                        break;
                    case "SocketEffect":
                        orig.StartSlot = texp.OriginalSlot;
                        if (!owner.SocketEffects.Contains(orig)) owner.SocketEffects.Add(orig);
                        break;
                    default:
                        if (texp.OriginalSlot < 0 || !owner.Hand.TryPlaceWithDisplaceOrSwap(orig, texp.OriginalSlot))
                            owner.Hand.TryAddAuto(orig);
                        break;
                }

                if (recordEvents)
                {
                    Emit(new CombatEvent
                    {
                        TimeMs = timeMs,
                        Type = CombatEventType.CardTransformReverted,
                        SourcePlayerId = owner.PlayerId,
                        TargetPlayerId = owner.PlayerId,
                        TargetCardInstanceId = orig.InstanceId,
                        Detail = orig.Template.Id,
                        RevertedTransformedInstanceIds = revertedIds,
                        ActionType = "TCombatDurationExpired"
                    });
                }
            }

            if (anyExpired)
            {
                RecalculateCombatAuras();
                ClampSideAmmo(p1);
                ClampSideAmmo(p2);
            }
        }

        void ExecuteCardCast(PlayerState owner, PlayerState opp, CardInstance card, int castNumber, AbilityDefinition? primaryFiredAb)
        {
            if (card.IsDestroyed || card.IsDisabled) return;

            // P2-10：同一单次施放及其同步派发出的所有动作/联动事件共享同一个 ExecutionContextId
            string? prevCastExecCtxId = activeExec.ExecutionContextId;
            if (string.IsNullOrEmpty(activeExec.ExecutionContextId))
                activeExec.ExecutionContextId = $"chain_{timeMs}_{++execChainSeq}";
            try
            {
            FlushDirtyAurasIfNeeded();
            card.TimesFiredThisFight++;
            if (recordEvents)
            {
                Emit(new CombatEvent
                {
                    TimeMs = timeMs,
                    Type = CombatEventType.CardFired,
                    SourcePlayerId = owner.PlayerId,
                    TargetPlayerId = opp.PlayerId,
                    SourceCardInstanceId = card.InstanceId,
                    Value = castNumber,
                    AbilityId = primaryFiredAb?.Id,
                    ActionType = primaryFiredAb?.Action?.Type,
                    IsSkillSource = card.Template.Type == "Skill"
                });
            }

            // 修复 P0-05：对齐官方 BazaarCardDealer.cs:1183-1235，同次 TTriggerOnCardFired 内全部可暴击动作共享 1 次暴击 Roll，
            // 且在整次施放循环结束后仅派发 1 次 TTriggerOnCardCritted（嵌套 ForceUse 独立保存/恢复栈帧）。
            var prevCastCard = activeExec.CurrentCastCard;
            var prevInCardFired = activeExec.InCardFiredExecution;
            var prevCastCritRoll = activeExec.CurrentCastCritRoll;
            var prevCastDidCrit = activeExec.CurrentCastDidCrit;
            activeExec.CurrentCastCard = card;
            activeExec.InCardFiredExecution = false;
            activeExec.CurrentCastCritRoll = null;
            activeExec.CurrentCastDidCrit = false;
            bool thisCastDidCrit = false;
            try
            {
                DispatchEvent("TTriggerOnCardFired", owner, card, card, owner);
                thisCastDidCrit = activeExec.CurrentCastDidCrit;
            }
            finally
            {
                activeExec.CurrentCastCard = prevCastCard;
                activeExec.InCardFiredExecution = prevInCardFired;
                activeExec.CurrentCastCritRoll = prevCastCritRoll;
                activeExec.CurrentCastDidCrit = prevCastDidCrit;
            }
            if (thisCastDidCrit)
                DispatchEvent("TTriggerOnCardCritted", owner, card, card, owner);
            DispatchEvent("TTriggerOnItemUsed", owner, card, card, owner);
            if (mechanicRuntime != null)
            {
                mechanicRuntime.DispatchPipeline(new CombatPipelineEvent
                {
                    HookName = "OnCardFired",
                    TimeMs = timeMs,
                    SourcePlayer = owner,
                    TargetPlayer = opp,
                    SourceCard = card,
                    TargetCard = card,
                    PayloadValue = castNumber
                });
                mechanicRuntime.DispatchPipeline(new CombatPipelineEvent
                {
                    HookName = "AfterCardFire",
                    TimeMs = timeMs,
                    SourcePlayer = owner,
                    TargetPlayer = opp,
                    SourceCard = card,
                    TargetCard = card,
                    PayloadValue = castNumber
                });
            }
            // 若该卡具备 Haste 锁存清理能力但本轮无合法加速目标导致 PerformedHaste 未触发，主动清零 Custom_8 防止后续他源加速串台
            if (HasHasteLatchClearAbility(card) && card.GetAttribute("Custom_8") > 0)
            {
                card.CombatMult.Remove("Custom_8");
                card.CombatAdd.Remove("Custom_8");
                double baseC8 = card.BaseAttributes.GetValueOrDefault("Custom_8", 0)
                              + card.PermanentAdd.GetValueOrDefault("Custom_8", 0)
                              + card.HourAdd.GetValueOrDefault("Custom_8", 0)
                              + card.DayAdd.GetValueOrDefault("Custom_8", 0)
                              + card.AuraAdd.GetValueOrDefault("Custom_8", 0);
                if (Math.Abs(baseC8) > 1e-9)
                    card.CombatAdd["Custom_8"] = -baseC8;
                card.MarkAttributeDirty();
            }
            RecalculateCombatAuras();
            ClampSideAmmo(p1);
            ClampSideAmmo(p2);
            }
            finally
            {
                activeExec.ExecutionContextId = prevCastExecCtxId;
            }
        }

        void ProcessPendingMulticasts()
        {
            if (pendingMulticasts.Count == 0) return;

            for (int i = 0; i < pendingMulticasts.Count; i++)
            {
                var batch = pendingMulticasts[i];
                if (batch.LastProcessedTimeMs >= timeMs) continue;
                batch.LastProcessedTimeMs = timeMs;

                var card = batch.Card;
                if (card.IsDestroyed || card.IsDisabled)
                {
                    pendingMulticasts.RemoveAt(i--);
                    continue;
                }

                // 8.9：冻结期间暂停后续连发计数，解冻后按间隔顺延
                if (card.FreezeMs > 0) continue;

                batch.DelayRemainingMs -= TickMs;
                if (batch.DelayRemainingMs > 0) continue;

                var primaryAb = batch.PrimaryAbility ?? ResolvePrimaryFiredAbility(card);
                ExecuteCardCast(batch.Owner, batch.Opponent, card, batch.NextCastNumber, primaryAb);

                if (batch.NextCastNumber >= batch.TotalCasts || card.IsDestroyed || card.IsDisabled)
                {
                    pendingMulticasts.RemoveAt(i--);
                }
                else
                {
                    batch.NextCastNumber++;
                    batch.DelayRemainingMs = effectDelayMs;
                }
            }
        }

        void FireCard(PlayerState owner, PlayerState opp, CardInstance card, bool isForced)
        {
            if (card.IsDestroyed || card.IsDisabled) return;
            if (!activeFiringCards.Add(card)) return;
            try
            {
                // 修复 P1-9 & P1-11：先验基础可用性与弹药余量，再派发 TTriggerOnBeforeItemUsed，确认未被前置回调销毁/禁用后再扣弹药
                int preAmmoMax = card.EffectiveAmmoMax;
                if (preAmmoMax > 0 && card.CurrentAmmo <= 0) return;

                DispatchEvent("TTriggerOnBeforeItemUsed", owner, card, card, owner);
                if (card.IsDestroyed || card.IsDisabled) return;

                if (mechanicRuntime != null)
                {
                    var beforeFireEv = new CombatPipelineEvent
                    {
                        HookName = "BeforeCardFire",
                        TimeMs = timeMs,
                        SourcePlayer = owner,
                        TargetPlayer = opp,
                        SourceCard = card,
                        TargetCard = card
                    };
                    mechanicRuntime.DispatchPipeline(beforeFireEv);
                    if (beforeFireEv.Cancelled || card.IsDestroyed || card.IsDisabled) return;
                }

                // 先按激活瞬间（扣弹前）的当前状态采样本轮 Multicast（保障 Shuriken 等 Multicast=CurrentAmmo 卡完整连发），
                // 随后扣弹并立即刷新 CardAmmo 动态光环，使 ExecuteCardCast 内依赖空弹药/当前弹药的增伤光环读取扣弹后最新值。
                FlushDirtyAurasIfNeeded();
                int baseMulticast = Math.Max(1, (int)Math.Round(card.GetAttribute("Multicast")));
                int bonusMulticast = (mod.BonusMulticastAll > 0 && card.HasActiveCooldown)
                    ? Math.Min(mod.BonusMulticastAll, mod.MaxBonusMulticastCap)
                    : 0;
                int multicast = Math.Clamp(baseMulticast + bonusMulticast, 1, 10);

                int ammoMax = card.EffectiveAmmoMax;
                if (ammoMax > 0)
                {
                    if (card.CurrentAmmo <= 0) return;
                    int prevAmmo = card.CurrentAmmo;
                    card.CurrentAmmo = Math.Clamp(card.CurrentAmmo - 1, 0, Math.Max(0, ammoMax));
                    // 修复 P0-07：扣弹后立即按掩码刷新依赖弹药/空弹药的动态光环，确保当次 ExecuteCardCast 读到最新值
                    MarkAuraDirty(CombatAuraDependencyMask.CardAmmo);
                    FlushDirtyAurasIfNeeded();
                    DispatchEvent("TTriggerOnCardAttributeChanged", owner, card, card, owner, "Ammo", prevAmmo, card.CurrentAmmo);
                }
                FlushDirtyAurasIfNeeded();

                var primaryFiredAb = ResolvePrimaryFiredAbility(card);

                // 8.9：第 1 发立即施放，第 2..multicast 发按 CombatSimEffectDelay (250ms) 间隔调度
                ExecuteCardCast(owner, opp, card, 1, primaryFiredAb);

                if (multicast > 1 && !card.IsDestroyed && !card.IsDisabled)
                {
                    pendingMulticasts.Add(new PendingMulticastBatch
                    {
                        Owner = owner,
                        Opponent = opp,
                        Card = card,
                        PrimaryAbility = primaryFiredAb,
                        NextCastNumber = 2,
                        TotalCasts = multicast,
                        DelayRemainingMs = effectDelayMs,
                        LastProcessedTimeMs = timeMs
                    });
                }
            }
            finally
            {
                activeFiringCards.Remove(card);
            }
        }

        // 帧末死亡/复活检查：对照官方 BazaarCardDealer.cs:4301-4368 先查 p2 再查 p1，每名玩家至多触发一次 TTriggerOnPlayerDied
        bool CheckEndOfTickDeath()
        {
            int savedP1Overkill = p1Overkill;
            int savedP2Overkill = p2Overkill;
            for (int pass = 0; pass < 2; pass++)
            {
                bool triggeredAny = false;
                if (!p2.IsAlive && !p2DeathTriggered)
                {
                    p2DeathTriggered = true;
                    p2Overkill = 0; // 对齐官方 HealthSetToZeroFromOnDeath：先归零再执行 OnDeath（确保复活按 0 血起算）
                    DispatchEvent("TTriggerOnPlayerDied", p2, null, null, p2);
                    ProcessIcdQueues();
                    triggeredAny = true;
                }
                if (!p1.IsAlive && !p1DeathTriggered)
                {
                    p1DeathTriggered = true;
                    p1Overkill = 0; // 对齐官方 HealthSetToZeroFromOnDeath：先归零再执行 OnDeath
                    DispatchEvent("TTriggerOnPlayerDied", p1, null, null, p1);
                    ProcessIcdQueues();
                    triggeredAny = true;
                }
                if (!triggeredAny) break;
            }
            if (p1.IsAlive) p1Overkill = 0;
            else p1Overkill = Math.Max(p1Overkill, savedP1Overkill); // 未复活则保留真实致命溢出负血供血条呈现（如 -420）
            if (p2.IsAlive) p2Overkill = 0;
            else p2Overkill = Math.Max(p2Overkill, savedP2Overkill);
            return !p1.IsAlive || !p2.IsAlive;
        }

        if (recordEvents)
            Emit(new CombatEvent { TimeMs = 0, Type = CombatEventType.FightStarted, SourcePlayerId = p1.PlayerId, TargetPlayerId = p2.PlayerId });
        DispatchEvent("TTriggerOnFightStarted", p1, null, null, p1);
        mechanicRuntime?.DispatchPipeline(new CombatPipelineEvent
        {
            HookName = "OnFightStart",
            TimeMs = 0,
            SourcePlayer = p1,
            TargetPlayer = p2
        });
        ProcessIcdQueues();
        CheckEndOfTickDeath();

        void ExecuteSandstormTick()
        {
            // P2-44 语义留档：严格对齐官方 BazaarCardDealer.TakeSandstormDamage（bazaarEffectEventContext = null），
            // 沙暴伤害通过 ApplyDirectDamage 仅触发 Shield/Health 属性变更（OnPlayerAttributeLost），不派发 TTriggerOnPlayerDamaged（如 Flamberge）。
            double red1 = Math.Clamp(p1.GetAttribute("PercentDamageReduction"), 0, 100);
            double red2 = Math.Clamp(p2.GetAttribute("PercentDamageReduction"), 0, 100);
            int sandDmg1 = Math.Max(0, (int)Math.Round(sandstormNextDmg * (1.0 - red1 / 100.0)));
            int sandDmg2 = Math.Max(0, (int)Math.Round(sandstormNextDmg * (1.0 - red2 / 100.0)));
            var sandSplit1 = (ShieldAbsorbed: 0, HpDamage: 0);
            var sandSplit2 = (ShieldAbsorbed: 0, HpDamage: 0);
            if (RunBeforeDamageHook(p2, p1, null, "Sandstorm", ref sandDmg1) && sandDmg1 > 0)
                sandSplit1 = ApplyDirectDamage(p1, sandDmg1, bypassShield: false);
            if (RunBeforeDamageHook(p1, p2, null, "Sandstorm", ref sandDmg2) && sandDmg2 > 0)
                sandSplit2 = ApplyDirectDamage(p2, sandDmg2, bypassShield: false);
            FlushDirtyAurasIfNeeded();
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormTick, Value = sandstormNextDmg, ShieldAbsorbed = sandSplit1.ShieldAbsorbed + sandSplit2.ShieldAbsorbed, HpDamage = sandSplit1.HpDamage + sandSplit2.HpDamage });

            // 修复 P1-50（严格对齐官方 BazaarCardDealer.cs:4243-4274 顺序双 if，严禁改为 else）：
            // 先减间隔至 TickRateFloor(150ms)，触底同跳立即按 AdditiveScalar(+2) 递增伤害，确保 t <= 35,000ms 精确 30 跳 / 累计 450 伤。
            if (sandstormCurrentRate > gm.Sandstorm.TickRateFloor)
                sandstormCurrentRate = Math.Max(gm.Sandstorm.TickRateFloor, sandstormCurrentRate - gm.Sandstorm.TickRateDecrement);
            if (sandstormCurrentRate <= gm.Sandstorm.TickRateFloor)
                sandstormNextDmg += gm.Sandstorm.AdditiveScalar;
        }

        // 主循环：50ms 固定步进（1:1 对齐官方 BazaarCardDealer.cs:4061）
        while (p1.IsAlive && p2.IsAlive && timeMs < MaxDurationMs)
        {
            tickCascadeFires = 0;
            tickImmediateExecs = 0;
            mechanicRuntime?.ResetTickBudget();
            if (tickCardFireCounts.Count > 0) tickCardFireCounts.Clear();
            p1Overkill = 0;
            p2Overkill = 0;
            bool p1AliveAtTickStart = p1.IsAlive;
            bool p2AliveAtTickStart = p2.IsAlive;

            // 0. 撤销到期的 TCombatDuration 限时修饰 & 推进开放自定义机制到期延迟回调
            ProcessTimedExpiries();
            mechanicRuntime?.TickDelayedCallbacks(timeMs);

            // 0b. 推进 Multicast 延迟连发（在冻结倒计时扣减前检查，保证冻结帧不推进连发计时）
            ProcessPendingMulticasts();

            // 0c. 推进狂暴持续时间并实时同步 Enraged 状态与光环（在推进物品冷却前结算，确保满额持续时间）
            TickEnraged(p1);
            TickEnraged(p2);

            // 1. 推进双方物品冷却与状态计时，并将同帧双方就绪物品汇总至统一队列排序结算（P2-47：先按主开火能力 Priority，同优先级由 seed+帧号确定性破平）
            frameReadyCards.Clear();
            AdvanceSideCards(p1, p2, sideIndex: 0, frameReadyCards);
            AdvanceSideCards(p2, p1, sideIndex: 1, frameReadyCards);
            if (frameReadyCards.Count > 1)
            {
                bool p1FirstThisTick = IsP1FirstForTick(seed, timeMs);
                frameReadyCards.Sort((a, b) =>
                {
                    int prioCmp = a.PriorityRank.CompareTo(b.PriorityRank);
                    if (prioCmp != 0) return prioCmp;
                    if (a.SideIndex != b.SideIndex)
                        return p1FirstThisTick ? a.SideIndex.CompareTo(b.SideIndex) : b.SideIndex.CompareTo(a.SideIndex);
                    int slotCmp = a.Card.StartSlot.CompareTo(b.Card.StartSlot);
                    return slotCmp != 0 ? slotCmp : string.CompareOrdinal(a.Card.InstanceId, b.Card.InstanceId);
                });
            }
            for (int rIdx = 0; rIdx < frameReadyCards.Count; rIdx++)
            {
                var ready = frameReadyCards[rIdx];
                if (ready.Card.IsDestroyed || ready.Card.IsDisabled) continue;
                int readyAmmoMax = ready.Card.EffectiveAmmoMax;
                if (readyAmmoMax > 0 && ready.Card.CurrentAmmo <= 0)
                {
                    ready.Card.CooldownProgressMs = 0;
                    continue;
                }
                // P2-47：同帧已读条完毕进入统一队列的卡不因同帧先手冻结被截断第 1 发（冻结仍会阻止后续连发与下一轮冷却）
                CompleteReadyCooldowns(ready.Owner, ready.Opp, ready.Card, countFirstAsDepth: false, allowIfFrozenThisTick: true);
            }

            // 2. 处理被动 Tempo 节奏获能周期
            TickPassiveTempo(p1, p1AliveAtTickStart, ref p1TempoElapsedMs);
            TickPassiveTempo(p2, p2AliveAtTickStart, ref p2TempoElapsedMs);

            // 3. 处理 ICD 触发队列
            ProcessIcdQueues();

            // 4. DoT 周期结算（修复 P1-52：严格对齐官方 BazaarCardDealer.cs:4202-4234 顺序 Poison -> Regen -> Burn）
            if (timeMs >= nextPoisonTickMs)
            {
                nextPoisonTickMs += 1000;
                TickPoison(p1);
                TickPoison(p2);
            }
            if (timeMs >= nextRegenTickMs)
            {
                nextRegenTickMs += 1000;
                TickRegen(p1, p1AliveAtTickStart);
                TickRegen(p2, p2AliveAtTickStart);
            }
            if (timeMs >= nextBurnTickMs)
            {
                nextBurnTickMs += 500;
                TickBurn(p1);
                TickBurn(p2);
            }

            // 5b. 突变词条：冰霜荒原（每 PeriodicFreezeIntervalMs 随机冻结双方各 1 件未冻结物品）
            if (timeMs >= nextPeriodicFreezeMs && nextPeriodicFreezeMs != int.MaxValue)
            {
                nextPeriodicFreezeMs += mod.PeriodicFreezeIntervalMs;
                ApplyPeriodicEnvironmentalFreeze(p1);
                ApplyPeriodicEnvironmentalFreeze(p2);
            }

            // 6. Sandstorm（修复 P1-50：严格对齐官方 BazaarCardDealer.cs:4235-4274 sandstormElapsedMs += 50 累加器，启动帧首跳后同帧累加 50ms）
            if (effectiveSandstormEnabled || sandstormForceStarted)
            {
                if (!sandstormWarned && !sandstormForceStarted && timeMs >= sandstormWarnMs)
                {
                    sandstormWarned = true;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormWarning });
                }
                if (!sandstormActive && timeMs >= sandstormStartMs)
                {
                    sandstormActive = true;
                    sandstormElapsedMs = 0;
                    // P2-44：自然启动瞬间仅派发一次 TTriggerOnSandstorm（subjectPlayer 规范为 null 避免对 P2 误传 p1），并在首跳沙暴伤害前先结算对应被动/光环
                    DispatchEvent("TTriggerOnSandstorm", p1, null, null, null);
                    ProcessIcdQueues();
                    ExecuteSandstormTick();
                    sandstormElapsedMs += TickMs;
                }
                else if (sandstormPendingImmediateTick)
                {
                    sandstormPendingImmediateTick = false;
                    sandstormElapsedMs = 0;
                    ExecuteSandstormTick();
                    sandstormElapsedMs += TickMs;
                }
                else if (sandstormActive)
                {
                    sandstormElapsedMs += TickMs;
                    if (sandstormElapsedMs >= sandstormCurrentRate)
                    {
                        ExecuteSandstormTick();
                        sandstormElapsedMs = 0;
                    }
                }
            }

            // 7. 帧末完整死亡与复活/遗言检查：先处理每方至多一次 TTriggerOnPlayerDied 并推进队列，再决定是否结束战斗
            bool anyDeadAfterOnDeath = CheckEndOfTickDeath();

            if (recordTimeline)
            {
                bool shouldCapture = effectiveSampleInterval <= TickMs
                    || timeMs == 0
                    || anyDeadAfterOnDeath
                    || (timeMs + TickMs) > nextSnapshotWindowEndMs;
                if (shouldCapture)
                {
                    timeline.Add(CaptureSnapshot(timeMs, p1, p2, sandstormActive, sandstormNextDmg, gm.StandardPrices, snapshotAttrCache, p1Overkill, p2Overkill, effectiveMinCooldownFloorMs));
                    while (nextSnapshotWindowEndMs <= timeMs + TickMs)
                        nextSnapshotWindowEndMs += effectiveSampleInterval;
                }
            }

            timeMs += TickMs;
            if (anyDeadAfterOnDeath) break;
        }

        // 兜底：若从 MaxDurationMs 超时边界或开战瞬间退出主循环且仍有未触发 OnDeath 的倒地玩家，补发一次（不重复派发）
        if ((!p2.IsAlive && !p2DeathTriggered) || (!p1.IsAlive && !p1DeathTriggered))
        {
            CheckEndOfTickDeath();
        }

        // 决定胜负（P1-55：精准记录同帧双杀 IsSimultaneousDeath 与超时双存 IsTimeout，保持官方异步规则双赢语义）
        bool isSimultaneousDeath = !p1.IsAlive && !p2.IsAlive;
        bool isTimeout = p1.IsAlive && p2.IsAlive;
        bool p1Won;
        bool isDraw = false;
        if (!p2.IsAlive)
        {
            // 官方《The Bazaar》异步对战核心规则：只要敌方倒地（无论玩家同帧是否也同时倒地 / 沙尘暴同归于尽），一律判玩家胜（双赢）！
            p1Won = true;
        }
        else if (!p1.IsAlive)
        {
            p1Won = false;
        }
        else
        {
            // 上限超时兜底：超过 MaxDurationMs 后若双方均扛住沙尘暴存活，判定玩家方胜利（异步对战与锦标赛各自作为 player 均判胜即两边同时胜利），保持 IsDraw=false
            p1Won = true;
        }

        if (recordTimeline)
            timeline.Add(CaptureSnapshot(timeMs, p1, p2, sandstormActive, sandstormNextDmg, gm.StandardPrices, snapshotAttrCache, p1Overkill, p2Overkill, effectiveMinCooldownFloorMs));

        if (recordEvents)
        {
            Emit(new CombatEvent
            {
                TimeMs = timeMs,
                Type = CombatEventType.FightEnded,
                SourcePlayerId = p1Won ? p1.PlayerId : p2.PlayerId,
                TargetPlayerId = p1Won ? p2.PlayerId : p1.PlayerId
            });
        }

        mechanicRuntime?.RunPostCombatCommit(p1Won);

        var result = new CombatResult
        {
            WinnerPlayerId = p1Won ? p1.PlayerId : p2.PlayerId,
            LoserPlayerId = p1Won ? p2.PlayerId : p1.PlayerId,
            Player1Hero = p1.Hero,
            Player2Hero = p2.Hero,
            IsDraw = isDraw,
            IsSimultaneousDeath = isSimultaneousDeath,
            IsTimeout = isTimeout,
            DurationMs = timeMs,
            Player1RemainingHp = p1.CurrentHealth,
            Player2RemainingHp = p2.CurrentHealth,
            Player1TotalDamageDealt = p1DmgTotal,
            Player2TotalDamageDealt = p2DmgTotal,
            Player1TotalShieldGained = p1ShieldTotal,
            Player2TotalShieldGained = p2ShieldTotal,
            Events = events,
            Timeline = timeline,
            TemporaryTransforms = temporaryTransforms,
            PostCombatOps = mechanicRuntime != null ? mechanicRuntime.PostCombatOps : new List<PostCombatMutationOp>(),
            MechanicDiagnostics = mechanicRuntime != null ? mechanicRuntime.Diagnostics : new List<MechanicDiagnostic>(),
            MechanicCircuitBreakerTrips = mechanicRuntime?.TrippedCircuitBreakersCount ?? 0,
            MechanicBudgetViolations = mechanicRuntime?.BudgetViolationsCount ?? 0,
            SimPlayer1 = p1
        };

        if (syncPermanentChanges)
        {
            SyncPermanentChangesBack(p1, originalP1, result);
            AuraEngine.RecalculateAuras(originalP1, null, RuleScope.OutOfCombat, originalP1.Day, originalP1.Hour, repo);
        }
        else
        {
            result.PostCombatPermanentSync = BuildPermanentSyncSummary(p1, originalP1, applyToOriginal: false);
        }

        return result;

        void AdvanceSideCards(
            PlayerState owner,
            PlayerState opp,
            int sideIndex,
            List<(PlayerState Owner, PlayerState Opp, CardInstance Card, int PriorityRank, int SideIndex)> readySink)
        {
            if (owner.Hand.Cards.Count == 0) return;

            // 快照并按 StartSlot/InstanceId 稳定排序，避免变形修改 Hand.Cards 抛异常并保证同帧多卡就绪的左至右确定性顺序
            handSnapshot.Clear();
            handSnapshot.AddRange(owner.Hand.Cards);
            if (handSnapshot.Count > 1)
            {
                handSnapshot.Sort(static (a, b) =>
                {
                    int slotCmp = a.StartSlot.CompareTo(b.StartSlot);
                    return slotCmp != 0 ? slotCmp : string.CompareOrdinal(a.InstanceId, b.InstanceId);
                });
            }

            for (int i = 0; i < handSnapshot.Count; i++)
            {
                var card = handSnapshot[i];
                if (card.IsDestroyed || card.IsDisabled) continue;

                // 修复 P1-53（对齐 BazaarCardDealer.cs:4110-4152 & 4401-4406）：
                // 先对所有存活在场卡独立递减 Freeze/Haste/Slow 三计时器，再跳过无有效冷却的被动/禁用冷却卡
                bool hasted = card.HasteMs > 0;
                bool slowed = card.SlowMs > 0;
                bool frozen = card.FreezeMs > 0;

                bool statusExpired = false;
                if (card.HasteMs > 0)
                {
                    card.HasteMs = Math.Max(0, card.HasteMs - TickMs);
                    if (card.HasteMs == 0) statusExpired = true;
                }
                if (card.SlowMs > 0)
                {
                    card.SlowMs = Math.Max(0, card.SlowMs - TickMs);
                    if (card.SlowMs == 0) statusExpired = true;
                }
                if (card.FreezeMs > 0)
                {
                    card.FreezeMs = Math.Max(0, card.FreezeMs - TickMs);
                    if (card.FreezeMs == 0) statusExpired = true;
                }
                if (statusExpired)
                {
                    MarkAuraDirty(CombatAuraDependencyMask.CardStatus);
                    FlushDirtyAurasIfNeeded();
                }

                if (!card.HasActiveCooldown) continue;

                int ammoMax = card.EffectiveAmmoMax;
                bool outOfAmmo = ammoMax > 0 && card.CurrentAmmo <= 0;

                if (frozen)
                {
                    if (outOfAmmo) card.CooldownProgressMs = 0;
                    continue;
                }

                double speed = 1.0;
                if (hasted && !slowed) speed = 2.0;
                else if (slowed && !hasted) speed = 0.5;

                if (outOfAmmo)
                {
                    card.CooldownProgressMs = 0;
                    continue;
                }

                int cdMax = GetEffectiveCardCooldownMaxMs(card, owner);
                double rawStep = TickMs * speed;
                // 修复 P1-56：单帧推进上限与 MinCooldownFloorMs（200..1000ms）动态对齐，避免 TurboFrenzy(500ms) 被硬编码 1000ms 腰斩
                double minCycleCapStep = TickMs * (cdMax / (double)effectiveMinCooldownFloorMs);
                card.CooldownProgressMs += Math.Max(1, (int)Math.Round(Math.Min(rawStep, minCycleCapStep)));
                if (lastCooldownFireMs.TryGetValue(card, out int prevCdMs) && timeMs - prevCdMs < icdMs)
                {
                    if (card.CooldownProgressMs > cdMax) card.CooldownProgressMs = cdMax;
                    continue;
                }
                if (card.CooldownProgressMs < cdMax) continue;
                int prioRank = GetAbilityPriorityRank(ResolvePrimaryFiredAbility(card)?.Priority);
                readySink.Add((owner, opp, card, prioRank, sideIndex));
            }
        }

        void ApplyPeriodicEnvironmentalFreeze(PlayerState side)
        {
            if (side.Hand.Cards.Count == 0) return;
            var eligible = new List<CardInstance>(side.Hand.Cards.Count);
            for (int i = 0; i < side.Hand.Cards.Count; i++)
            {
                var c = side.Hand.Cards[i];
                if (!c.IsDestroyed && !c.IsDisabled && c.FreezeMs <= 0 && c.HasActiveCooldown)
                    eligible.Add(c);
            }
            if (eligible.Count == 0) return;
            var picked = eligible[rng.Next(eligible.Count)];
            ApplyCardStatusDuration(side, picked, mod.PeriodicFreezeDurationMs, null, "PercentFreezeReduction", checkRadiantAndFlying: true, CombatEventType.FreezeApplied, "TTriggerOnCardPerformedFreeze", "Freeze", static c => c.FreezeMs, static (c, v) => c.FreezeMs = v);
        }

        void TickEnraged(PlayerState p)
        {
            if (p.EnragedRemainingMs <= 0)
            {
                if (p.CombatAdd.ContainsKey("Enraged") || p.CombatAdd.ContainsKey("EnragedDuration"))
                    SyncEnragedState(p);
                return;
            }
            p.EnragedRemainingMs = Math.Max(0, p.EnragedRemainingMs - TickMs);
            SyncEnragedState(p);
            if (p.EnragedRemainingMs == 0)
            {
                RecalculateCombatAuras();
                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PlayerEnrageEnded, TargetPlayerId = p.PlayerId });
                DispatchEvent("TTriggerOnPlayerEnrageEnded", p, null, null, p);
            }
        }

        void TickBurn(PlayerState target)
        {
            if (target.Burn <= 0) return;
            var attacker = target == p1 ? p2 : p1;
            // 8.5：护盾对 Burn 的减免上限受 target.Shield 约束（向上取半确保满盾时与 floor(Burn/2) 一致，同时防止 1 点护盾将 100 点 Burn 减半）
            int mitigation = target.Shield > 0 ? Math.Min(target.Shield, (target.Burn + 1) / 2) : 0;
            int dmg = Math.Max(1, target.Burn - mitigation);
            if (RunBeforeDamageHook(attacker, target, null, "Burn", ref dmg) && dmg > 0)
            {
                var burnSplit = ApplyDirectDamage(target, dmg, bypassShield: false);
                if (target == p2) p1DmgTotal += dmg; else p2DmgTotal += dmg;

                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.BurnTick, TargetPlayerId = target.PlayerId, Value = dmg, ShieldAbsorbed = burnSplit.ShieldAbsorbed, HpDamage = burnSplit.HpDamage });
            }

            int prevBurn = target.Burn;
            int decay = Math.Max(1, (int)Math.Floor(target.Burn * (gm.BurnDecrementByAsPercentage / 100.0)));
            target.Burn = Math.Max(0, target.Burn - decay);
            if (target.Burn != prevBurn)
            {
                MarkAuraDirty(CombatAuraDependencyMask.Burn);
                FlushDirtyAurasIfNeeded();
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Burn", prevBurn, target.Burn);
            }
        }

        void TickPoison(PlayerState target)
        {
            if (target.Poison <= 0) return;
            var attacker = target == p1 ? p2 : p1;
            int dmg = target.Poison;
            if (!RunBeforeDamageHook(attacker, target, null, "Poison", ref dmg) || dmg <= 0) return;
            var poisonSplit = ApplyDirectDamage(target, dmg, bypassShield: true);
            if (target == p2) p1DmgTotal += dmg; else p2DmgTotal += dmg;
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PoisonTick, TargetPlayerId = target.PlayerId, Value = dmg, ShieldAbsorbed = poisonSplit.ShieldAbsorbed, HpDamage = poisonSplit.HpDamage });
            FlushDirtyAurasIfNeeded();
        }

        void TickRegen(PlayerState target, bool wasAliveAtTickStart)
        {
            int regen = (int)Math.Round(target.GetAttribute("HealthRegen"));
            if (regen <= 0 || !wasAliveAtTickStart) return;
            int prevHp = target.CurrentHealth;
            int effectiveRestored = ApplyHealthGain(target, regen);
            if (target.CurrentHealth != prevHp)
            {
                MarkAuraDirty(CombatAuraDependencyMask.Health);
                FlushDirtyAurasIfNeeded();
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Health", prevHp, target.CurrentHealth);
            }
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.RegenTick, TargetPlayerId = target.PlayerId, Value = regen, EffectiveHpRestored = effectiveRestored });
        }

        static int GetEffectiveTempoGainIntervalMs(PlayerState side)
        {
            // 对齐官方 Base Tempo Gain Cooldown Effect：默认 2000ms，应用平摊与百分比缩减；若被 1E15 级负缩减抑制则禁用
            double baseMax = side.GetAttribute("TempoGainCooldownMax");
            if (baseMax <= 0) baseMax = 2000.0;
            double flatRed = side.GetAttribute("FlatTempoGainCooldownReduction");
            double pctRed = Math.Clamp(side.GetAttribute("PercentTempoGainCooldownReduction"), -200.0, 95.0);
            double effective = (baseMax - flatRed) * (1.0 - pctRed / 100.0);
            if (double.IsNaN(effective) || double.IsInfinity(effective) || effective >= 1_000_000.0)
                return int.MaxValue;
            return Math.Max(100, (int)Math.Round(effective));
        }

        void TickPassiveTempo(PlayerState side, bool wasAliveAtTickStart, ref int elapsedMs)
        {
            if (!wasAliveAtTickStart || !side.IsAlive)
            {
                elapsedMs = 0;
                return;
            }
            // P2-46：优先由注入的 Base Tempo Gain Cooldown Effect 光环数据驱动抑制（无 Tempo 卡时 FlatTempoGainCooldownReduction -= 1e15 -> int.MaxValue）；
            // 仅在无该系统卡的轻量合成环境下回退 HasTempoSystemCards 判定。
            if (!HasBaseTempoPlayerEffect(side) && !HasTempoSystemCards(side))
            {
                elapsedMs = 0;
                return;
            }
            int intervalMs = GetEffectiveTempoGainIntervalMs(side);
            if (intervalMs == int.MaxValue)
            {
                elapsedMs = 0;
                return;
            }
            elapsedMs += TickMs;
            if (elapsedMs < intervalMs) return;
            elapsedMs -= intervalMs;
            int prevTempo = side.Tempo;
            side.ModifyAttribute("Tempo", "Add", 1);
            if (side.Tempo != prevTempo)
            {
                MarkAuraDirty(CombatAuraDependencyMask.Tempo);
                FlushDirtyAurasIfNeeded();
                DispatchEvent("TTriggerOnPlayerAttributeChanged", side, null, null, side, "Tempo", prevTempo, side.Tempo);
            }
        }
    }

    /// <summary>
    /// 使用 CombatResult 中保存的模拟态 P1 将战斗期间获得的永久变更无损回写至 <paramref name="originalP1"/>。
    /// </summary>
    public static PostCombatPermanentSyncResult SyncPermanentChangesBack(CombatResult result, PlayerState originalP1)
    {
        if (result.SimPlayer1 != null)
            return SyncPermanentChangesBack(result.SimPlayer1, originalP1, result);

        if (result.PostCombatPermanentSync != null)
            return result.PostCombatPermanentSync;

        var empty = new PostCombatPermanentSyncResult { Applied = true };
        result.PostCombatPermanentSync = empty;
        return empty;
    }

    /// <summary>
    /// 将战斗中产生的永久状态变化无损回写至 <paramref name="originalP1"/>：
    /// 含未销毁源卡的 PermanentAdd/PermanentMult/Enchantment/Tier/ExtraTags/QuestProgress/CompletedQuests/TransformReplacementTemplateId、
    /// 为 P1 永久生成的非临时道具/技能，以及 P1 永久玩家属性增量（Gold/Income/HealthMax/Experience 等）。
    /// </summary>
    public static PostCombatPermanentSyncResult SyncPermanentChangesBack(
        PlayerState simP1,
        PlayerState originalP1,
        CombatResult? result = null)
    {
        if (result?.PostCombatPermanentSync?.Applied == true)
            return result.PostCombatPermanentSync;

        var sync = BuildPermanentSyncSummary(simP1, originalP1, applyToOriginal: true);
        if (result != null)
            result.PostCombatPermanentSync = sync;
        return sync;
    }

    private static PostCombatPermanentSyncResult BuildPermanentSyncSummary(
        PlayerState simP1,
        PlayerState originalP1,
        bool applyToOriginal)
    {
        var sync = new PostCombatPermanentSyncResult { Applied = applyToOriginal };

        var origCardsById = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        foreach (var oc in originalP1.EnumerateAllOwnedCards())
            origCardsById[oc.InstanceId] = oc;

        var simCardsById = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        // 修复 P1-3：支持单卡战斗内永久变形出多产物（1:N 列表），避免后产物覆写吞卡
        var simReplacementsByOrigId = new Dictionary<string, List<(CardInstance Card, bool IsStash, bool IsSkill, bool IsSocketEffect)>>(StringComparer.Ordinal);
        var simNewNonTempCards = new List<(CardInstance Card, bool IsStash, bool IsSkill, bool IsSocketEffect)>();

        void ScanSimList(List<CardInstance> cards, bool isStash, bool isSkill, bool isSocketEffect)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var sc = cards[i];
                simCardsById[sc.InstanceId] = sc;

                if (!string.IsNullOrEmpty(sc.TransformedFromInstanceId) && !sc.IsCombatTemporary)
                {
                    if (!simReplacementsByOrigId.TryGetValue(sc.TransformedFromInstanceId, out var repList))
                    {
                        repList = new List<(CardInstance, bool, bool, bool)>(2);
                        simReplacementsByOrigId[sc.TransformedFromInstanceId] = repList;
                    }
                    repList.Add((sc, isStash, isSkill, isSocketEffect));
                    continue;
                }

                bool existedInOriginal = origCardsById.ContainsKey(sc.InstanceId)
                    || (simP1.CombatSourceCardMap != null && simP1.CombatSourceCardMap.ContainsKey(sc.InstanceId));
                if (!existedInOriginal && !sc.IsCombatTemporary)
                    simNewNonTempCards.Add((sc, isStash, isSkill, isSocketEffect));
            }
        }

        ScanSimList(simP1.Hand.Cards, isStash: false, isSkill: false, isSocketEffect: false);
        ScanSimList(simP1.Stash.Cards, isStash: true, isSkill: false, isSocketEffect: false);
        ScanSimList(simP1.Skills, isStash: false, isSkill: true, isSocketEffect: false);
        ScanSimList(simP1.SocketEffects, isStash: false, isSkill: false, isSocketEffect: true);

        // 1. 回写所有源卡牌的永久变化（含单/多产物永久变形）
        foreach (var (origId, origCard) in origCardsById.ToList())
        {
            if (simReplacementsByOrigId.TryGetValue(origId, out var simReps) && simReps.Count > 0)
            {
                var firstRep = simReps[0].Card;
                var cleanFirst = ClonePermanentCardSnapshot(firstRep);
                cleanFirst.StartSlot = origCard.StartSlot;
                cleanFirst.TransformedFromInstanceId = origId;

                var allCleanReps = new List<CardInstance>(simReps.Count) { cleanFirst };
                var additionalReps = new List<CardInstance>(Math.Max(0, simReps.Count - 1));
                for (int r = 1; r < simReps.Count; r++)
                {
                    var extraClean = ClonePermanentCardSnapshot(simReps[r].Card);
                    extraClean.TransformedFromInstanceId = origId;
                    allCleanReps.Add(extraClean);
                    additionalReps.Add(extraClean);
                }

                sync.PermanentReplacements[origId] = allCleanReps;

                var delta = new CardPermanentSyncDelta
                {
                    InstanceId = origId,
                    SourceCard = origCard,
                    TransformReplacementTemplateId = firstRep.Template.Id,
                    TransformedReplacementCard = cleanFirst,
                    AdditionalTransformedReplacements = additionalReps
                };
                sync.UpdatedCards.Add(delta);

                if (applyToOriginal)
                {
                    bool origWasInStash = originalP1.Stash.Cards.Contains(origCard);
                    origCard.TransformReplacementTemplateId = firstRep.Template.Id;
                    ReplaceCardInOriginalOwner(originalP1, origCard, cleanFirst);

                    for (int r = 1; r < simReps.Count; r++)
                    {
                        var extraClean = allCleanReps[r];
                        var (_, repStash, repSkill, repSocket) = simReps[r];
                        if (repSkill || extraClean.Template.Type == "Skill")
                        {
                            originalP1.Skills.Add(extraClean);
                        }
                        else if (repSocket)
                        {
                            originalP1.SocketEffects.Add(extraClean);
                        }
                        else if (repStash || origWasInStash)
                        {
                            if (!originalP1.Stash.TryAddAuto(extraClean))
                                originalP1.Hand.TryAddAuto(extraClean);
                        }
                        else
                        {
                            if (!originalP1.Hand.TryAddAuto(extraClean))
                                originalP1.Stash.TryAddAuto(extraClean);
                        }
                    }
                }
                continue;
            }

            if (!simCardsById.TryGetValue(origId, out var simCard))
                continue;

            var cardDelta = new CardPermanentSyncDelta
            {
                InstanceId = origId,
                SourceCard = origCard
            };
            bool anyCardChange = false;

            // 1a. PermanentAdd
            var addKeys = new HashSet<string>(origCard.PermanentAdd.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in simCard.PermanentAdd.Keys) addKeys.Add(k);
            foreach (var k in addKeys)
            {
                double sv = simCard.PermanentAdd.GetValueOrDefault(k, 0);
                double ov = origCard.PermanentAdd.GetValueOrDefault(k, 0);
                if (Math.Abs(sv - ov) > 1e-9)
                {
                    cardDelta.PermanentAddChanges[k] = sv - ov;
                    anyCardChange = true;
                    if (applyToOriginal)
                    {
                        if (Math.Abs(sv) < 1e-9) origCard.PermanentAdd.Remove(k);
                        else origCard.PermanentAdd[k] = sv;
                    }
                }
            }

            // 1b. PermanentMult
            var multKeys = new HashSet<string>(origCard.PermanentMult.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in simCard.PermanentMult.Keys) multKeys.Add(k);
            foreach (var k in multKeys)
            {
                double sv = simCard.PermanentMult.GetValueOrDefault(k, 1.0);
                double ov = origCard.PermanentMult.GetValueOrDefault(k, 1.0);
                if (Math.Abs(sv - ov) > 1e-9)
                {
                    cardDelta.PermanentMultChanges[k] = sv;
                    anyCardChange = true;
                    if (applyToOriginal)
                    {
                        if (Math.Abs(sv - 1.0) < 1e-9) origCard.PermanentMult.Remove(k);
                        else origCard.PermanentMult[k] = sv;
                    }
                }
            }

            // 1c. 永久附魔（仅比对与同步 PermanentEnchantment，绝不误把 DayEnchantment/HourEnchantment 当作永久附魔变更抹除）
            if (!string.Equals(simCard.PermanentEnchantment, origCard.PermanentEnchantment, StringComparison.Ordinal))
            {
                anyCardChange = true;
                cardDelta = new CardPermanentSyncDelta
                {
                    InstanceId = cardDelta.InstanceId,
                    SourceCard = cardDelta.SourceCard,
                    PermanentAddChanges = cardDelta.PermanentAddChanges,
                    PermanentMultChanges = cardDelta.PermanentMultChanges,
                    EnchantmentChanged = true,
                    NewEnchantment = simCard.PermanentEnchantment,
                    TierChanged = cardDelta.TierChanged,
                    NewTier = cardDelta.NewTier,
                    AddedExtraTags = cardDelta.AddedExtraTags,
                    UpdatedQuestProgress = cardDelta.UpdatedQuestProgress,
                    NewlyCompletedQuests = cardDelta.NewlyCompletedQuests,
                    TransformReplacementTemplateId = cardDelta.TransformReplacementTemplateId,
                    TransformedReplacementCard = cardDelta.TransformedReplacementCard
                };
                if (applyToOriginal)
                {
                    if (!string.IsNullOrEmpty(simCard.PermanentEnchantment))
                    {
                        origCard.ApplyEnchantment(simCard.PermanentEnchantment, preventOverride: false, isTemporary: false);
                        // 若原卡尚携带 Hour/Day 内层临时附魔，按 Hour ?? Day ?? Permanent 收敛表层显示优先级，与 ResetForCombat 一致
                        var layeredEnch = origCard.HourEnchantment ?? origCard.DayEnchantment ?? origCard.PermanentEnchantment;
                        if (!string.Equals(origCard.Enchantment, layeredEnch, StringComparison.Ordinal))
                            origCard.RemoveEnchantment(DurationTarget.Combat);
                    }
                    else
                    {
                        origCard.RemoveEnchantment(isTemporary: false);
                    }
                }
            }
            else if (applyToOriginal)
            {
                // 战后回写原卡时清理战前已挂载的 UntilEndOfCombat 临时附魔，同时完整保留 HourEnchantment 与 DayEnchantment
                var expectedNonCombatEnch = origCard.HourEnchantment ?? origCard.DayEnchantment ?? origCard.PermanentEnchantment;
                if (!string.Equals(origCard.Enchantment, expectedNonCombatEnch, StringComparison.Ordinal))
                    origCard.RemoveEnchantment(DurationTarget.Combat);
            }

            // 1d. 永久升阶（仅比对与同步 PermanentTier）
            if (simCard.PermanentTier != origCard.PermanentTier)
            {
                anyCardChange = true;
                cardDelta = new CardPermanentSyncDelta
                {
                    InstanceId = cardDelta.InstanceId,
                    SourceCard = cardDelta.SourceCard,
                    PermanentAddChanges = cardDelta.PermanentAddChanges,
                    PermanentMultChanges = cardDelta.PermanentMultChanges,
                    EnchantmentChanged = cardDelta.EnchantmentChanged,
                    NewEnchantment = cardDelta.NewEnchantment,
                    TierChanged = true,
                    NewTier = simCard.PermanentTier,
                    AddedExtraTags = cardDelta.AddedExtraTags,
                    UpdatedQuestProgress = cardDelta.UpdatedQuestProgress,
                    NewlyCompletedQuests = cardDelta.NewlyCompletedQuests,
                    TransformReplacementTemplateId = cardDelta.TransformReplacementTemplateId,
                    TransformedReplacementCard = cardDelta.TransformedReplacementCard
                };
                if (applyToOriginal)
                    origCard.SetTier(simCard.PermanentTier, isTemporary: false);
            }

            // 1e. ExtraTags
            foreach (var tag in simCard.ExtraTags)
            {
                if (!origCard.ExtraTags.Contains(tag))
                {
                    cardDelta.AddedExtraTags.Add(tag);
                    anyCardChange = true;
                    if (applyToOriginal)
                        origCard.ExtraTags.Add(tag);
                }
            }

            // 1f. QuestProgress & CompletedQuests
            foreach (var (qk, qv) in simCard.QuestProgress)
            {
                double ov = origCard.QuestProgress.GetValueOrDefault(qk, 0);
                if (Math.Abs(qv - ov) > 1e-9)
                {
                    cardDelta.UpdatedQuestProgress[qk] = qv;
                    anyCardChange = true;
                    if (applyToOriginal)
                        origCard.QuestProgress[qk] = qv;
                }
            }
            bool questCompletedChanged = false;
            foreach (var cq in simCard.CompletedQuests)
            {
                if (!origCard.CompletedQuests.Contains(cq))
                {
                    cardDelta.NewlyCompletedQuests.Add(cq);
                    anyCardChange = true;
                    questCompletedChanged = true;
                    if (applyToOriginal)
                        origCard.CompletedQuests.Add(cq);
                }
            }
            if (applyToOriginal && questCompletedChanged)
                origCard.RebuildTierAndEnchantmentData();

            if (applyToOriginal && anyCardChange)
                origCard.RefillAmmoToMax();

            // 1g. TransformReplacementTemplateId
            if (!string.IsNullOrEmpty(simCard.TransformReplacementTemplateId) &&
                !string.Equals(simCard.TransformReplacementTemplateId, origCard.TransformReplacementTemplateId, StringComparison.Ordinal))
            {
                anyCardChange = true;
                cardDelta = new CardPermanentSyncDelta
                {
                    InstanceId = cardDelta.InstanceId,
                    SourceCard = cardDelta.SourceCard,
                    PermanentAddChanges = cardDelta.PermanentAddChanges,
                    PermanentMultChanges = cardDelta.PermanentMultChanges,
                    EnchantmentChanged = cardDelta.EnchantmentChanged,
                    NewEnchantment = cardDelta.NewEnchantment,
                    TierChanged = cardDelta.TierChanged,
                    NewTier = cardDelta.NewTier,
                    AddedExtraTags = cardDelta.AddedExtraTags,
                    UpdatedQuestProgress = cardDelta.UpdatedQuestProgress,
                    NewlyCompletedQuests = cardDelta.NewlyCompletedQuests,
                    TransformReplacementTemplateId = simCard.TransformReplacementTemplateId,
                    TransformedReplacementCard = cardDelta.TransformedReplacementCard
                };
                if (applyToOriginal)
                    origCard.TransformReplacementTemplateId = simCard.TransformReplacementTemplateId;
            }

            if (anyCardChange)
                sync.UpdatedCards.Add(cardDelta);
        }

        // 2. 同步为 P1 永久生成的非临时道具/技能
        foreach (var (sc, isStash, isSkill, isSocketEffect) in simNewNonTempCards)
        {
            var cleanCard = ClonePermanentCardSnapshot(sc);
            sync.SpawnedCards.Add(cleanCard);
            if (!applyToOriginal) continue;

            if (isSkill || cleanCard.Template.Type == "Skill")
            {
                originalP1.Skills.Add(cleanCard);
            }
            else if (isSocketEffect)
            {
                originalP1.SocketEffects.Add(cleanCard);
            }
            else if (!isStash)
            {
                if (!originalP1.Hand.TryAddAuto(cleanCard))
                    originalP1.Stash.TryAddAuto(cleanCard);
            }
            else
            {
                // 修复 P1-3：在 Stash 产生的非临时衍生卡优先回写至 Stash，再兜底 Hand
                if (!originalP1.Stash.TryAddAuto(cleanCard))
                    originalP1.Hand.TryAddAuto(cleanCard);
            }
        }

        // 3. 同步 P1 永久玩家属性增量（修复 P0-03：改为显式跨战持久化属性白名单，防止临时 Regen 与战末 Enraged/EnragedDuration 泄漏）
        var baselineAttrs = simP1.CombatSourcePlayerAttributes ?? originalP1.Attributes;
        var playerAttrKeys = new HashSet<string>(simP1.Attributes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in baselineAttrs.Keys) playerAttrKeys.Add(k);

        foreach (var k in playerAttrKeys)
        {
            if (!IsCrossCombatPersistentPlayerAttribute(k)) continue;
            double simVal = simP1.Attributes.GetValueOrDefault(k, 0);
            double baseVal = baselineAttrs.GetValueOrDefault(k, 0);
            double diff = simVal - baseVal;
            if (Math.Abs(diff) <= 1e-9) continue;

            sync.PlayerAttributeDeltas[k] = diff;
            if (!applyToOriginal) continue;

            double prevOrig = originalP1.Attributes.GetValueOrDefault(k, 0);
            double nextOrig = prevOrig + diff;
            if (k.Equals("Gold", StringComparison.OrdinalIgnoreCase)
                || k.Equals("Experience", StringComparison.OrdinalIgnoreCase)
                || k.Equals("Prestige", StringComparison.OrdinalIgnoreCase))
            {
                nextOrig = Math.Max(0, nextOrig);
            }
            else if (k.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            {
                nextOrig = Math.Max(1, nextOrig);
            }

            originalP1.Attributes[k] = nextOrig;
            if (k.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            {
                if (diff > 0)
                    originalP1.CurrentHealth = Math.Min(originalP1.HealthMax, originalP1.CurrentHealth + (int)Math.Round(diff));
                else
                    originalP1.CurrentHealth = Math.Min(originalP1.CurrentHealth, originalP1.HealthMax);
            }
        }

        return sync;
    }

    private static CardInstance ClonePermanentCardSnapshot(CardInstance src)
    {
        var copy = new CardInstance(src.Template, src.PermanentTier, src.PermanentEnchantment, src.InstanceId)
        {
            StartSlot = src.StartSlot,
            TransformReplacementTemplateId = src.TransformReplacementTemplateId,
            TransformedFromInstanceId = src.TransformedFromInstanceId
        };
        foreach (var (k, v) in src.PermanentAdd) copy.PermanentAdd[k] = v;
        foreach (var (k, v) in src.PermanentMult) copy.PermanentMult[k] = v;
        foreach (var (k, v) in src.HourAdd) copy.HourAdd[k] = v;
        foreach (var (k, v) in src.HourMult) copy.HourMult[k] = v;
        foreach (var (k, v) in src.DayAdd) copy.DayAdd[k] = v;
        foreach (var (k, v) in src.DayMult) copy.DayMult[k] = v;
        foreach (var t in src.ExtraTags) copy.ExtraTags.Add(t);
        foreach (var (k, v) in src.QuestProgress) copy.QuestProgress[k] = v;
        foreach (var c in src.CompletedQuests) copy.CompletedQuests.Add(c);
        copy.RebuildTierAndEnchantmentData();
        copy.ResetForCombat(null);
        return copy;
    }

    private static void ReplaceCardInOriginalOwner(PlayerState owner, CardInstance oldCard, CardInstance replacement)
    {
        if (owner.Hand.Cards.Contains(oldCard))
        {
            int slot = oldCard.StartSlot;
            owner.Hand.Remove(oldCard);
            if (slot < 0 || !owner.Hand.TryPlaceOrShift(replacement, slot))
                owner.Hand.TryAddAuto(replacement);
        }
        else if (owner.Stash.Cards.Contains(oldCard))
        {
            int slot = oldCard.StartSlot;
            owner.Stash.Remove(oldCard);
            if (slot < 0 || !owner.Stash.TryPlaceOrShift(replacement, slot))
                owner.Stash.TryAddAuto(replacement);
        }
        else
        {
            int sIdx = owner.Skills.IndexOf(oldCard);
            if (sIdx >= 0) { owner.Skills[sIdx] = replacement; return; }
            int seIdx = owner.SocketEffects.IndexOf(oldCard);
            if (seIdx >= 0) owner.SocketEffects[seIdx] = replacement;
        }
    }

    /// <summary>
    /// 跨战允许回写至局外 PlayerState.Attributes 的显式持久属性白名单（修复 P0-03）：
    /// 临时 HealthRegen 已隔离于 CombatAdd，此处仅在明确 Duration==null 写入 simP1.Attributes 时才产生 HealthRegen 永久差量；
    /// Enraged / EnragedDuration 等战内瞬态属性被彻底拒之门外。
    /// </summary>
    private static bool IsCrossCombatPersistentPlayerAttribute(string key)
        => key.Equals("Gold", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Income", StringComparison.OrdinalIgnoreCase)
        || key.Equals("HealthMax", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Experience", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Prestige", StringComparison.OrdinalIgnoreCase)
        || key.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);

    private static CombatAuraDependencyMask MapAttributeToDependencyMask(string? attr)
    {
        if (string.IsNullOrEmpty(attr)) return CombatAuraDependencyMask.None;
        if (attr.Equals("Health", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Health;
        if (attr.Equals("Shield", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Shield;
        if (attr.Equals("Burn", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Burn;
        if (attr.Equals("Poison", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Poison;
        if (attr.Equals("HealthRegen", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Regen;
        if (attr.Equals("Tempo", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Rage", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Enraged", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("EnragedDuration", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.Tempo;
        if (attr.Equals("Haste", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Slow", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Freeze", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Flying", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Heated", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("Chilled", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.CardStatus;
        if (attr.Equals("Ammo", StringComparison.OrdinalIgnoreCase)
            || attr.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
            return CombatAuraDependencyMask.CardAmmo;
        return CombatAuraDependencyMask.None;
    }

    private static CombatAuraDependencyMask ScanSideAuraDependencies(PlayerState side)
        => ScanAuraListDependencies(side.Hand.Cards)
         | ScanAuraListDependencies(side.Skills)
         | ScanAuraListDependencies(side.SocketEffects)
         | ScanAuraListDependencies(side.PlayerEffects)
         | ScanAuraListDependencies(side.Stash.Cards);

    private static CombatAuraDependencyMask ScanAuraListDependencies(List<CardInstance> cards)
    {
        CombatAuraDependencyMask mask = CombatAuraDependencyMask.None;
        for (int i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (c.IsDestroyed || c.IsDisabled || c.ActiveAuras.Count == 0) continue;
            for (int j = 0; j < c.ActiveAuras.Count; j++)
            {
                var aura = c.ActiveAuras[j];
                if (string.Equals(aura.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (aura.Prerequisites != null)
                {
                    for (int k = 0; k < aura.Prerequisites.Count; k++)
                        mask |= ScanAuraNodeDependencies(aura.Prerequisites[k], 0);
                }
                var act = aura.Action;
                if (act != null)
                {
                    // 仅扫描光环动作的输入依赖（Target/Source/Value/Conditions），避免把被写入的目标属性误标为输入依赖
                    mask |= ScanAuraNodeDependencies(act.Target, 0);
                    mask |= ScanAuraNodeDependencies(act.TargetPlayer, 0);
                    mask |= ScanAuraNodeDependencies(act.Source, 0);
                    mask |= ScanAuraNodeDependencies(act.ReferenceValue, 0);
                    mask |= ScanAuraNodeDependencies(act.GetValueNode(GameDatabaseLoader.JsonOptions), 0);
                    mask |= ScanAuraNodeDependencies(act.GetSingleCondition(GameDatabaseLoader.JsonOptions), 0);
                    foreach (var cond in act.GetConditionList(GameDatabaseLoader.JsonOptions))
                        mask |= ScanAuraNodeDependencies(cond, 0);
                }
            }
        }
        return mask;
    }

    private static CombatAuraDependencyMask ScanAuraNodeDependencies(RuleNode? node, int depth)
    {
        if (node == null || depth > 12) return CombatAuraDependencyMask.None;
        CombatAuraDependencyMask mask = CombatAuraDependencyMask.None;

        mask |= MapAttributeToDependencyMask(node.AttributeType);
        mask |= MapAttributeToDependencyMask(node.Attribute);
        mask |= MapAttributeToDependencyMask(node.AttributeOther);
        mask |= MapAttributeToDependencyMask(node.PlayerAttributeType);
        mask |= MapAttributeToDependencyMask(node.AttributeChanged);

        if (node.Type is "TCardConditionalTag" or "TCardConditionalHiddenTag")
        {
            if (node.Tags != null && node.Tags.Contains("Flying", StringComparer.OrdinalIgnoreCase))
                mask |= CombatAuraDependencyMask.CardStatus;
            if (node.HiddenTags != null && node.HiddenTags.Contains("Flying", StringComparer.OrdinalIgnoreCase))
                mask |= CombatAuraDependencyMask.CardStatus;
        }

        mask |= ScanAuraNodeDependencies(node.Target, depth + 1);
        mask |= ScanAuraNodeDependencies(node.TargetPlayer, depth + 1);
        mask |= ScanAuraNodeDependencies(node.TargetCount, depth + 1);
        mask |= ScanAuraNodeDependencies(node.Source, depth + 1);
        mask |= ScanAuraNodeDependencies(node.Subject, depth + 1);
        mask |= ScanAuraNodeDependencies(node.SubjectOther, depth + 1);
        mask |= ScanAuraNodeDependencies(node.ReferenceValue, depth + 1);
        mask |= ScanAuraNodeDependencies(node.GetValueNode(GameDatabaseLoader.JsonOptions), depth + 1);
        mask |= ScanAuraNodeDependencies(node.GetComparisonValueNode(GameDatabaseLoader.JsonOptions), depth + 1);
        if (node.Modifier?.Value != null)
            mask |= ScanAuraNodeDependencies(node.Modifier.Value, depth + 1);
        mask |= ScanAuraNodeDependencies(node.GetSingleCondition(GameDatabaseLoader.JsonOptions), depth + 1);
        foreach (var sub in node.GetConditionList(GameDatabaseLoader.JsonOptions))
            mask |= ScanAuraNodeDependencies(sub, depth + 1);
        if (node.Actions != null)
        {
            for (int i = 0; i < node.Actions.Count; i++)
                mask |= ScanAuraNodeDependencies(node.Actions[i], depth + 1);
        }
        if (node.Prerequisites != null)
        {
            for (int i = 0; i < node.Prerequisites.Count; i++)
                mask |= ScanAuraNodeDependencies(node.Prerequisites[i], depth + 1);
        }
        return mask;
    }

    private static bool IsCardFlying(CardInstance card)
        => card.IsFlying || card.GetAttribute("Flying") > 0 || card.HasTag("Flying");

    private static int GetCardCooldownMaxMs(CardInstance card)
    {
        double effCd = card.EffectiveCooldownMs;
        return (double.IsNaN(effCd) || double.IsInfinity(effCd))
            ? 1000
            : Math.Max(1000, (int)Math.Round(effCd));
    }

    private static AbilityDefinition? ResolvePrimaryFiredAbility(CardInstance card)
    {
        for (int a = 0; a < card.ActiveAbilities.Count; a++)
        {
            var candAb = card.ActiveAbilities[a];
            if (candAb.Trigger != null && AbilityHasTriggerType(candAb.Trigger, "TTriggerOnCardFired"))
                return candAb;
        }
        return card.ActiveAbilities.FirstOrDefault();
    }

    private static bool AbilityHasTriggerType(RuleNode trigger, string targetType)
    {
        if (string.Equals(trigger.Type, targetType, StringComparison.Ordinal))
            return true;
        if (trigger.Type == "TTriggerOr" && trigger.Triggers != null)
        {
            for (int i = 0; i < trigger.Triggers.Count; i++)
            {
                if (AbilityHasTriggerType(trigger.Triggers[i], targetType))
                    return true;
            }
        }
        return false;
    }

    private static Dictionary<string, List<IndexedAbility>> BuildSideTriggerIndex(PlayerState owner)
    {
        var index = new Dictionary<string, List<IndexedAbility>>(StringComparer.Ordinal);
        var seenTypes = new HashSet<string>(StringComparer.Ordinal);

        void IndexList(List<CardInstance> cards)
        {
            for (int c = 0; c < cards.Count; c++)
            {
                var card = cards[c];
                for (int a = 0; a < card.ActiveAbilities.Count; a++)
                {
                    var ab = card.ActiveAbilities[a];
                    if (ab.Trigger == null || ab.ActiveIn == "StashOnly") continue;
                    if (string.Equals(ab.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)) continue;

                    seenTypes.Clear();
                    CollectTriggerTypes(ab.Trigger, seenTypes);
                    foreach (var triggerType in seenTypes)
                    {
                        if (RuleExecutionContext.HasUnknownRuleTypeObserver && !AbilityExecutor.IsKnownTriggerType(triggerType))
                            RuleExecutionContext.NotifyUnknownRuleType(null, "TTrigger", triggerType);
                        if (!index.TryGetValue(triggerType, out var list))
                        {
                            list = new List<IndexedAbility>(4);
                            index[triggerType] = list;
                        }
                        list.Add(new IndexedAbility(card, ab, a));
                    }
                }
            }
        }

        void IndexStashList(List<CardInstance> stashCards)
        {
            // 修复 P1-20：为 Stash 中带有 HandAndStash / StashOnly 且非 OutOfCombatOnly 的被动触发建立独立战斗索引
            // （严格排除 TTriggerOnCardFired，且绝不动 AuraEngine 战中跳过背包位置光环的隔离边界）
            for (int c = 0; c < stashCards.Count; c++)
            {
                var card = stashCards[c];
                for (int a = 0; a < card.ActiveAbilities.Count; a++)
                {
                    var ab = card.ActiveAbilities[a];
                    if (ab.Trigger == null) continue;
                    if (string.Equals(ab.WorksIn, "OutOfCombatOnly", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(ab.ActiveIn, "HandAndStash", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ab.ActiveIn, "StashOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    seenTypes.Clear();
                    CollectTriggerTypes(ab.Trigger, seenTypes);
                    foreach (var triggerType in seenTypes)
                    {
                        if (string.Equals(triggerType, "TTriggerOnCardFired", StringComparison.Ordinal))
                            continue;
                        if (RuleExecutionContext.HasUnknownRuleTypeObserver && !AbilityExecutor.IsKnownTriggerType(triggerType))
                            RuleExecutionContext.NotifyUnknownRuleType(null, "TTrigger", triggerType);
                        if (!index.TryGetValue(triggerType, out var list))
                        {
                            list = new List<IndexedAbility>(4);
                            index[triggerType] = list;
                        }
                        list.Add(new IndexedAbility(card, ab, a));
                    }
                }
            }
        }

        IndexList(owner.Hand.Cards);
        IndexList(owner.Skills);
        IndexList(owner.SocketEffects);
        IndexList(owner.PlayerEffects);
        IndexStashList(owner.Stash.Cards);

        foreach (var list in index.Values)
        {
            if (list.Count > 1)
            {
                // 修复 P0-6：优先按 Ability.Priority 排定全盘跨卡结算先后，再按 StartSlot / InstanceId / ActiveOrder 稳定排序
                list.Sort(static (a, b) =>
                {
                    int prioCmp = GetAbilityPriorityRank(a.Ability.Priority).CompareTo(GetAbilityPriorityRank(b.Ability.Priority));
                    if (prioCmp != 0) return prioCmp;
                    int slotCmp = a.Card.StartSlot.CompareTo(b.Card.StartSlot);
                    if (slotCmp != 0) return slotCmp;
                    int instCmp = string.CompareOrdinal(a.Card.InstanceId, b.Card.InstanceId);
                    if (instCmp != 0) return instCmp;
                    return a.ActiveOrder.CompareTo(b.ActiveOrder);
                });
            }
        }
        return index;
    }

    private static int GetAbilityPriorityRank(string? priority) => priority switch
    {
        "Immediate" => 0,
        "Highest" => 1,
        "High" => 2,
        "Medium" => 3,
        "Low" => 4,
        "Lowest" => 5,
        _ => 3
    };

    private const string BaseTempoGainCooldownEffectId = "e8478722-1851-4b05-9749-1480352ff710";

    /// <summary>
    /// P2-47：基于本局随机种子与当前帧号生成确定性破平位，消除同步 PvP 同帧就绪时固定 P1 先手的系统偏置。
    /// </summary>
    internal static bool IsP1FirstForTick(int combatSeed, int tickTimeMs)
    {
        uint h = unchecked((uint)combatSeed * 2654435761u ^ (uint)(tickTimeMs / TickMs) * 2246822519u);
        h ^= h >> 16;
        h = unchecked(h * 0x7feb352du);
        h ^= h >> 15;
        h = unchecked(h * 0x846ca68bu);
        h ^= h >> 16;
        return (h & 1u) == 0u;
    }

    private static bool HasBaseTempoPlayerEffect(PlayerState side)
    {
        for (int i = 0; i < side.PlayerEffects.Count; i++)
        {
            if (string.Equals(side.PlayerEffects[i].Template.Id, BaseTempoGainCooldownEffectId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void EnsureCombatBasePlayerEffect(GameDataRepository repo, PlayerState side)
    {
        const string BaseRageEffectId = "4472da8a-26a3-4e10-bd9a-e93c2e22f19c";
        const string EnragePortraitUpdateId = "d2ba43c2-0ba9-4299-b737-7594caa507ea";

        static void EnsurePlayerEffect(GameDataRepository r, PlayerState s, string effectId)
        {
            if (s.Skills.Any(sk => string.Equals(sk.Template.Id, effectId, StringComparison.OrdinalIgnoreCase))
                || s.PlayerEffects.Any(pe => string.Equals(pe.Template.Id, effectId, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }
            var tpl = r.GetCard(effectId);
            if (tpl != null)
                s.PlayerEffects.Add(new CardInstance(tpl, instanceId: $"sys_pe_{s.PlayerId}_{effectId}"));
        }

        EnsurePlayerEffect(repo, side, BaseRageEffectId);
        // P0-11：自动挂载官方 Enrage portrait update（TTriggerOnPlayerEnraged -> PortraitNext / EnrageEnded -> PortraitReset）
        EnsurePlayerEffect(repo, side, EnragePortraitUpdateId);
        // P2-46：自动挂载官方 Base Tempo Gain Cooldown Effect，由 AuraEngine 数据驱动无 Tempo 卡时的被动获能抑制
        EnsurePlayerEffect(repo, side, BaseTempoGainCooldownEffectId);
    }

    private static bool HasHasteLatchClearAbility(CardInstance card)
    {
        for (int i = 0; i < card.ActiveAbilities.Count; i++)
        {
            var ab = card.ActiveAbilities[i];
            if (ab.Trigger?.Type == "TTriggerOnCardPerformedHaste"
                && ab.Action?.Type == "TActionCardModifyAttribute"
                && string.Equals(ab.Action.AttributeType, "Custom_8", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ab.Action.Operation, "Multiply", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSelfArmedHasteLatchReaction(CardInstance card, AbilityDefinition ab)
    {
        if (ab.Trigger?.Type != "TTriggerOnCardAttributeChanged"
            || !string.Equals(ab.Trigger.AttributeChanged, "Haste", StringComparison.OrdinalIgnoreCase)
            || ab.Prerequisites == null
            || !HasHasteLatchClearAbility(card))
        {
            return false;
        }
        for (int i = 0; i < ab.Prerequisites.Count; i++)
        {
            if (NodeReferencesSelfCustom8(ab.Prerequisites[i]))
                return true;
        }
        return false;
    }

    private static bool NodeReferencesSelfCustom8(RuleNode? node)
    {
        if (node == null) return false;
        if (string.Equals(node.Attribute, "Custom_8", StringComparison.OrdinalIgnoreCase)
            || string.Equals(node.AttributeType, "Custom_8", StringComparison.OrdinalIgnoreCase))
            return true;
        if (NodeReferencesSelfCustom8(node.Subject)) return true;
        var singleCond = node.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (NodeReferencesSelfCustom8(singleCond)) return true;
        foreach (var sub in node.GetConditionList(GameDatabaseLoader.JsonOptions))
        {
            if (NodeReferencesSelfCustom8(sub)) return true;
        }
        return false;
    }

    private static bool IsStatGainAbility(AbilityDefinition ab)
        => IsStatGainAction(ab.Action, depth: 0);

    private static bool IsStatGainAction(RuleNode? action, int depth)
    {
        if (action == null || depth > 2) return false;
        if (string.Equals(action.Type, "TActionAnd", StringComparison.Ordinal))
        {
            var subs = action.Actions;
            if (subs == null || subs.Count == 0) return false;
            for (int i = 0; i < subs.Count; i++)
            {
                if (!IsStatGainAction(subs[i], depth + 1)) return false;
            }
            return true;
        }
        if (string.Equals(action.Type, "TActionPlayerModifyAttribute", StringComparison.Ordinal))
        {
            return string.Equals(action.AttributeType, "HealthMax", StringComparison.OrdinalIgnoreCase)
                && string.Equals(action.Operation, "Add", StringComparison.OrdinalIgnoreCase);
        }
        if (!string.Equals(action.Type, "TActionCardModifyAttribute", StringComparison.Ordinal))
            return false;
        string? attr = action.AttributeType;
        if (string.IsNullOrEmpty(attr)) return false;
        // 排除扣减敌方冷却/清零减速冻结等非成长性属性修改（如 Atomic Clock / Base Rage Effect）
        if (string.Equals(action.Target?.TargetSection, "OpponentHand", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(action.Operation, "Multiply", StringComparison.OrdinalIgnoreCase) && attr is "Slow" or "Freeze")
            return false;

        return attr is "DamageAmount" or "ShieldApplyAmount" or "BurnApplyAmount" or "PoisonApplyAmount"
            or "HealAmount" or "RegenApplyAmount" or "CritChance" or "DamageCrit" or "HealCrit"
            or "ShieldCrit" or "BurnCrit" or "PoisonCrit" or "RegenCrit" or "Multicast"
            or "FlatCooldownReduction" or "PercentCooldownReduction" or "SellPrice" or "BuyPrice"
            or "Lifesteal" or "AmmoMax"
            || attr.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateQueuedPrerequisites(CardInstance card, List<RuleNode>? prerequisites, RuleExecutionContext ctx)
    {
        if (prerequisites == null || prerequisites.Count == 0) return true;
        for (int i = 0; i < prerequisites.Count; i++)
        {
            var prereq = prerequisites[i];
            if (ShouldSkipQueuedSelfCustomPrereq(card, prereq))
                continue;
            if (!ValueAndConditionEvaluator.EvaluatePrerequisite(prereq, ctx))
                return false;
        }
        return true;
    }

    private static bool ShouldSkipQueuedSelfCustomPrereq(CardInstance card, RuleNode prereq)
    {
        if (!TryExtractSingleSelfCustomAttr(prereq, out var customAttr))
            return false;
        return CardHasImmediateSelfCustomModifier(card, customAttr);
    }

    private static bool TryExtractSingleSelfCustomAttr(RuleNode node, out string customAttr)
    {
        customAttr = string.Empty;
        if (!string.Equals(node.Type, "TPrerequisiteCardCount", StringComparison.Ordinal))
            return false;
        if (node.Subject == null || !string.Equals(node.Subject.Type, "TTargetCardSelf", StringComparison.Ordinal))
            return false;
        var cond = node.Subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (cond == null || !string.Equals(cond.Type, "TCardConditionalAttribute", StringComparison.Ordinal))
            return false;
        if (string.IsNullOrEmpty(cond.Attribute) || !cond.Attribute.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
            return false;
        customAttr = cond.Attribute;
        return true;
    }

    private static bool CardHasImmediateSelfCustomModifier(CardInstance card, string customAttr)
    {
        for (int i = 0; i < card.ActiveAbilities.Count; i++)
        {
            var ab = card.ActiveAbilities[i];
            var act = ab.Action;
            if (act == null) continue;
            if (!string.Equals(act.Type, "TActionCardModifyAttribute", StringComparison.Ordinal)) continue;
            if (!string.Equals(act.Target?.Type, "TTargetCardSelf", StringComparison.Ordinal)) continue;
            if (!string.Equals(act.AttributeType, customAttr, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(ab.Priority, "Immediate", StringComparison.OrdinalIgnoreCase)
                || (ab.InternalName != null && ab.InternalName.Contains("Counter", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSelfCounterIncrementAbility(AbilityDefinition ab)
        => ab.Action != null
        && string.Equals(ab.Action.Type, "TActionCardModifyAttribute", StringComparison.Ordinal)
        && string.Equals(ab.Action.Target?.Type, "TTargetCardSelf", StringComparison.Ordinal)
        && !string.IsNullOrEmpty(ab.Action.AttributeType)
        && ab.Action.AttributeType.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase)
        && ((ab.InternalName != null && ab.InternalName.Contains("Counter Increment", StringComparison.OrdinalIgnoreCase))
            || string.Equals(ab.Priority, "Immediate", StringComparison.OrdinalIgnoreCase));

    private static bool IsEndOfEventCounterLimiter(List<IndexedAbility> indexedList, CardInstance card, AbilityDefinition ab)
    {
        if (!IsSelfCounterIncrementAbility(ab)) return false;
        if (ab.InternalName != null && ab.InternalName.Contains("Counter Increment", StringComparison.OrdinalIgnoreCase))
            return true;
        string? attr = ab.Action?.AttributeType;
        if (string.IsNullOrEmpty(attr)) return false;
        for (int i = 0; i < indexedList.Count; i++)
        {
            var sibling = indexedList[i];
            if (!ReferenceEquals(sibling.Card, card) || ReferenceEquals(sibling.Ability, ab)) continue;
            if (HasCounterUpperBoundPrerequisite(sibling.Ability.Prerequisites, attr))
                return true;
        }
        return false;
    }

    private static bool HasCounterUpperBoundPrerequisite(List<RuleNode>? prerequisites, string attr)
    {
        if (prerequisites == null || prerequisites.Count == 0) return false;
        for (int i = 0; i < prerequisites.Count; i++)
        {
            if (NodeChecksUpperBoundOnAttr(prerequisites[i], attr))
                return true;
        }
        return false;
    }

    private static bool NodeChecksUpperBoundOnAttr(RuleNode? node, string attr, int depth = 0)
    {
        if (node == null || depth > 8) return false;
        if (string.Equals(node.Attribute, attr, StringComparison.OrdinalIgnoreCase)
            && node.ComparisonOperator is "Equal" or "LessThan" or "LessThanOrEqual")
        {
            return true;
        }
        if (NodeChecksUpperBoundOnAttr(node.Subject, attr, depth + 1)) return true;
        if (NodeChecksUpperBoundOnAttr(node.Source, attr, depth + 1)) return true;
        if (NodeChecksUpperBoundOnAttr(node.Target, attr, depth + 1)) return true;
        var single = node.GetSingleCondition(GameDatabaseLoader.JsonOptions);
        if (NodeChecksUpperBoundOnAttr(single, attr, depth + 1)) return true;
        foreach (var sub in node.GetConditionList(GameDatabaseLoader.JsonOptions))
        {
            if (NodeChecksUpperBoundOnAttr(sub, attr, depth + 1)) return true;
        }
        return false;
    }

    private static void CollectTriggerTypes(RuleNode trigger, HashSet<string> types)
    {
        if (trigger.Type == "TTriggerOr")
        {
            if (trigger.Triggers != null)
            {
                for (int i = 0; i < trigger.Triggers.Count; i++)
                    CollectTriggerTypes(trigger.Triggers[i], types);
            }
            return;
        }
        if (!string.IsNullOrEmpty(trigger.Type))
            types.Add(trigger.Type);
    }

    private static bool HasTempoSystemCards(PlayerState side)
    {
        foreach (var c in side.Hand.Cards)
        {
            if (c.IsDestroyed || c.IsDisabled) continue;
            if (c.Template.HiddenTags.Contains("Tempo") ||
                c.Template.HiddenTags.Contains("TempoReference") ||
                c.GetAttribute("TempoCost") > 0 ||
                (c.Template.Attributes != null && c.Template.Attributes.GetValueOrDefault("TempoCost", 0) > 0))
                return true;
        }
        foreach (var s in side.Skills)
        {
            if (s.IsDestroyed || s.IsDisabled) continue;
            if (s.Template.HiddenTags.Contains("Tempo") ||
                s.Template.HiddenTags.Contains("TempoReference") ||
                s.GetAttribute("TempoCost") > 0 ||
                (s.Template.Attributes != null && s.Template.Attributes.GetValueOrDefault("TempoCost", 0) > 0))
                return true;
        }
        return false;
    }

    private static bool HasAnyQuests(PlayerState owner)
    {
        foreach (var c in owner.Hand.Cards)
            if (c.Template.Quests is { Count: > 0 }) return true;
        foreach (var s in owner.Skills)
            if (s.Template.Quests is { Count: > 0 }) return true;
        foreach (var se in owner.SocketEffects)
            if (se.Template.Quests is { Count: > 0 }) return true;
        foreach (var c in owner.Stash.Cards)
            if (c.Template.Quests is { Count: > 0 }) return true;
        return false;
    }

    private static CombatTickState CaptureSnapshot(
        int timeMs,
        PlayerState p1,
        PlayerState p2,
        bool sandstormActive,
        int nextSandstormDmg,
        StandardPricesSettings prices,
        Dictionary<CardInstance, CachedCardSnapshotAttrs>? attrCache = null,
        int p1Overkill = 0,
        int p2Overkill = 0,
        int minCooldownFloorMs = 1000)
    {
        static int ReadIntAttr(CardInstance c, string attr)
        {
            double v = c.GetAttribute(attr);
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return (int)Math.Round(v);
        }

        static int[]? ReadCustomAttrsSparse(CardInstance c)
        {
            int[]? arr = null;
            for (int ci = 0; ci < CustomAttrNames.Length; ci++)
            {
                string key = CustomAttrNames[ci];
                bool hasKey = (c.BaseAttributes.Count > 0 && c.BaseAttributes.ContainsKey(key))
                           || (c.PermanentAdd.Count > 0 && c.PermanentAdd.ContainsKey(key))
                           || (c.CombatAdd.Count > 0 && c.CombatAdd.ContainsKey(key))
                           || (c.AuraAdd.Count > 0 && c.AuraAdd.ContainsKey(key))
                           || (c.HourAdd.Count > 0 && c.HourAdd.ContainsKey(key))
                           || (c.DayAdd.Count > 0 && c.DayAdd.ContainsKey(key));
                if (!hasKey) continue;
                int v = ReadIntAttr(c, key);
                if (v != 0)
                {
                    arr ??= new int[CustomAttrNames.Length];
                    arr[ci] = v;
                }
            }
            return arr;
        }

        var cards = new List<CardCooldownFrame>(p1.Hand.Cards.Count + p2.Hand.Cards.Count);

        void AppendHandFrames(List<CardInstance> handCards)
        {
            for (int i = 0; i < handCards.Count; i++)
            {
                var c = handCards[i];
                CachedCardSnapshotAttrs cached;
                if (attrCache != null)
                {
                    if (!attrCache.TryGetValue(c, out cached!))
                    {
                        cached = new CachedCardSnapshotAttrs();
                        attrCache[c] = cached;
                    }
                    if (cached.Version != c.AttributeVersion)
                    {
                        cached.Version = c.AttributeVersion;
                        cached.AmmoMax = c.EffectiveAmmoMax;
                        cached.CooldownMaxMs = Math.Max(0, ReadIntAttr(c, "CooldownMax"));
                        cached.DamageAmount = ReadIntAttr(c, "DamageAmount");
                        cached.ShieldApplyAmount = ReadIntAttr(c, "ShieldApplyAmount");
                        cached.BurnApplyAmount = ReadIntAttr(c, "BurnApplyAmount");
                        cached.PoisonApplyAmount = ReadIntAttr(c, "PoisonApplyAmount");
                        cached.HealAmount = ReadIntAttr(c, "HealAmount");
                        cached.RegenApplyAmount = ReadIntAttr(c, "RegenApplyAmount");
                        cached.CritChance = ReadIntAttr(c, "CritChance");
                        cached.Multicast = Math.Max(0, ReadIntAttr(c, "Multicast"));
                        cached.Lifesteal = ReadIntAttr(c, "Lifesteal");
                        cached.SellPrice = c.GetSellPrice(prices);
                        cached.AuraHeated = c.GetAttribute("Heated") > 0;
                        cached.AuraChilled = c.GetAttribute("Chilled") > 0;
                        cached.Tags = c.GetAllTags().ToArray();
                        cached.HiddenTags = c.GetAllHiddenTags().ToArray();
                        cached.Heroes = c.GetAllHeroes().ToArray();
                        cached.CustomAttrs = ReadCustomAttrsSparse(c);
                    }
                }
                else
                {
                    cached = new CachedCardSnapshotAttrs
                    {
                        AmmoMax = c.EffectiveAmmoMax,
                        CooldownMaxMs = Math.Max(0, ReadIntAttr(c, "CooldownMax")),
                        DamageAmount = ReadIntAttr(c, "DamageAmount"),
                        ShieldApplyAmount = ReadIntAttr(c, "ShieldApplyAmount"),
                        BurnApplyAmount = ReadIntAttr(c, "BurnApplyAmount"),
                        PoisonApplyAmount = ReadIntAttr(c, "PoisonApplyAmount"),
                        HealAmount = ReadIntAttr(c, "HealAmount"),
                        RegenApplyAmount = ReadIntAttr(c, "RegenApplyAmount"),
                        CritChance = ReadIntAttr(c, "CritChance"),
                        Multicast = Math.Max(0, ReadIntAttr(c, "Multicast")),
                        Lifesteal = ReadIntAttr(c, "Lifesteal"),
                        SellPrice = c.GetSellPrice(prices),
                        AuraHeated = c.GetAttribute("Heated") > 0,
                        AuraChilled = c.GetAttribute("Chilled") > 0,
                        Tags = c.GetAllTags().ToArray(),
                        HiddenTags = c.GetAllHiddenTags().ToArray(),
                        Heroes = c.GetAllHeroes().ToArray(),
                        CustomAttrs = ReadCustomAttrsSparse(c)
                    };
                }

                double cd = Math.Max(minCooldownFloorMs, cached.CooldownMaxMs > 0 ? cached.CooldownMaxMs : c.EffectiveCooldownMs);
                cards.Add(new CardCooldownFrame
                {
                    InstanceId = c.InstanceId,
                    Progress01 = c.HasActiveCooldown ? (float)Math.Clamp(c.CooldownProgressMs / cd, 0, 1) : 0f,
                    IsHasted = c.HasteMs > 0,
                    IsSlowed = c.SlowMs > 0,
                    IsFrozen = c.FreezeMs > 0,
                    // 修复 P1-6：使用 IsCardFlying 覆盖光环赋予的飞行态
                    IsFlying = IsCardFlying(c),
                    IsDestroyed = c.IsDestroyed || c.IsDisabled,
                    Ammo = c.CurrentAmmo,
                    HasteMs = c.HasteMs,
                    SlowMs = c.SlowMs,
                    FreezeMs = c.FreezeMs,
                    Heated = c.Heated || cached.AuraHeated,
                    Chilled = c.Chilled || cached.AuraChilled,
                    AmmoMax = cached.AmmoMax,
                    CooldownMaxMs = cached.CooldownMaxMs,
                    DamageAmount = cached.DamageAmount,
                    ShieldApplyAmount = cached.ShieldApplyAmount,
                    BurnApplyAmount = cached.BurnApplyAmount,
                    PoisonApplyAmount = cached.PoisonApplyAmount,
                    HealAmount = cached.HealAmount,
                    RegenApplyAmount = cached.RegenApplyAmount,
                    CritChance = cached.CritChance,
                    Multicast = cached.Multicast,
                    Lifesteal = cached.Lifesteal,
                    SellPrice = cached.SellPrice,
                    Counter = c.TimesFiredThisFight,
                    Tags = cached.Tags,
                    HiddenTags = cached.HiddenTags,
                    Heroes = cached.Heroes,
                    CustomAttrs = cached.CustomAttrs
                });
            }
        }

        AppendHandFrames(p1.Hand.Cards);
        AppendHandFrames(p2.Hand.Cards);

        static int ReadPlayerIntAttr(PlayerState p, string attr, int fallback = 0)
        {
            double v = p.GetAttribute(attr);
            if (double.IsNaN(v) || double.IsInfinity(v)) return fallback;
            return (int)Math.Round(v);
        }

        int p1TempoCdMax = ReadPlayerIntAttr(p1, "TempoGainCooldownMax");
        int p2TempoCdMax = ReadPlayerIntAttr(p2, "TempoGainCooldownMax");
        bool p1TempoActive = HasBaseTempoPlayerEffect(p1)
            ? p1.GetAttribute("FlatTempoGainCooldownReduction") > -1_000_000.0
            : HasTempoSystemCards(p1);
        bool p2TempoActive = HasBaseTempoPlayerEffect(p2)
            ? p2.GetAttribute("FlatTempoGainCooldownReduction") > -1_000_000.0
            : HasTempoSystemCards(p2);

        return new CombatTickState
        {
            TimeMs = timeMs,
            P1Hp = p1.CurrentHealth > 0 ? p1.CurrentHealth : -Math.Max(0, p1Overkill),
            P1MaxHp = p1.HealthMax,
            P1Shield = p1.Shield,
            P1Burn = p1.Burn,
            P1Poison = p1.Poison,
            P1Regen = ReadPlayerIntAttr(p1, "HealthRegen"),
            P1Rage = p1.Rage,
            P1RageMax = Math.Max(0, ReadPlayerIntAttr(p1, "RageMax", 100)),
            P1Enraged = p1.IsEnraged,
            P1EnragedDuration = p1.EnragedRemainingMs,
            P1EnragedDurationMax = Math.Max(0, ReadPlayerIntAttr(p1, "EnragedDurationMax", 5000)),
            P1Tempo = p1.Tempo,
            P1TempoGainCooldownMax = p1TempoActive ? (p1TempoCdMax > 0 ? p1TempoCdMax : 2000) : 0,
            P1Joy = Math.Max(0, ReadPlayerIntAttr(p1, "Joy")),
            P1PortraitIndex = p1.PortraitIndex,
            P2Hp = p2.CurrentHealth > 0 ? p2.CurrentHealth : -Math.Max(0, p2Overkill),
            P2MaxHp = p2.HealthMax,
            P2Shield = p2.Shield,
            P2Burn = p2.Burn,
            P2Poison = p2.Poison,
            P2Regen = ReadPlayerIntAttr(p2, "HealthRegen"),
            P2Rage = p2.Rage,
            P2RageMax = Math.Max(0, ReadPlayerIntAttr(p2, "RageMax", 100)),
            P2Enraged = p2.IsEnraged,
            P2EnragedDuration = p2.EnragedRemainingMs,
            P2EnragedDurationMax = Math.Max(0, ReadPlayerIntAttr(p2, "EnragedDurationMax", 5000)),
            P2Tempo = p2.Tempo,
            P2TempoGainCooldownMax = p2TempoActive ? (p2TempoCdMax > 0 ? p2TempoCdMax : 2000) : 0,
            P2Joy = Math.Max(0, ReadPlayerIntAttr(p2, "Joy")),
            P2PortraitIndex = p2.PortraitIndex,
            SandstormActive = sandstormActive,
            SandstormNextDamage = nextSandstormDmg,
            Cards = cards
        };
    }

    /// <summary>
    /// 依据 GameData.db 中的 MonsterTemplate 构建野怪战斗状态（精准还原 0-based 槽位掩码、Hand/Stash 站位、品质与附魔）。
    /// </summary>
    public static PlayerState BuildMonsterPlayerState(GameDataRepository repo, MonsterTemplate monster)
    {
        int hp = (int)Math.Round(monster.Player.Attributes.GetValueOrDefault("HealthMax", 250));
        int level = (int)Math.Round(monster.Player.Attributes.GetValueOrDefault("Level", 1));
        var state = new PlayerState("monster_" + monster.Id, monster.InternalName, HeroNames.Common, Math.Max(10, hp), 20, 0, 10)
        {
            IsMonster = true,
            Level = level
        };
        foreach (var (k, v) in monster.Player.Attributes)
            state.Attributes[k] = v;
        state.Hand.UnlockedMask = monster.Player.Hand.UnlockedSlots > 0 ? monster.Player.Hand.UnlockedSlots : 1023;

        foreach (var itemEntry in monster.Player.Hand.Items)
        {
            var tpl = repo.GetCard(itemEntry.TemplateId);
            if (tpl == null) continue;
            var card = new CardInstance(tpl, TierHelper.ParseTier(itemEntry.Tier), itemEntry.EnchantmentType, itemEntry.InstanceId);
            if (itemEntry.Attributes != null)
            {
                foreach (var (ak, av) in itemEntry.Attributes)
                    card.PermanentAdd[ak] = av - card.BaseAttributes.GetValueOrDefault(ak, 0);
            }
            int desiredSlot = ParseSocketIndex(itemEntry.SocketId);
            if (desiredSlot >= 0 && state.Hand.TryPlaceOrShift(card, desiredSlot))
                continue;
            state.Hand.TryAddAuto(card);
        }

        // 修复 P1-11：解析野怪 Stash 物品（如 PVE_Karnok_D6_001 等带 Stash 装备的野怪，保障依赖 Stash 的技能/光环准确生效）
        if (monster.Player.Stash != null && monster.Player.Stash.Items.Count > 0)
        {
            state.Stash.UnlockedMask = monster.Player.Stash.UnlockedSlots > 0 ? monster.Player.Stash.UnlockedSlots : 1023;
            foreach (var stashEntry in monster.Player.Stash.Items)
            {
                var tpl = repo.GetCard(stashEntry.TemplateId);
                if (tpl == null) continue;
                var card = new CardInstance(tpl, TierHelper.ParseTier(stashEntry.Tier), stashEntry.EnchantmentType, stashEntry.InstanceId);
                if (stashEntry.Attributes != null)
                {
                    foreach (var (ak, av) in stashEntry.Attributes)
                        card.PermanentAdd[ak] = av - card.BaseAttributes.GetValueOrDefault(ak, 0);
                }
                int desiredSlot = ParseSocketIndex(stashEntry.SocketId);
                if (desiredSlot >= 0 && state.Stash.TryPlaceOrShift(card, desiredSlot))
                    continue;
                state.Stash.TryAddAuto(card);
            }
        }

        foreach (var skillEntry in monster.Player.Skills)
        {
            var tpl = repo.GetCard(skillEntry.TemplateId);
            if (tpl == null) continue;
            state.Skills.Add(new CardInstance(tpl, TierHelper.ParseTier(skillEntry.Tier), null, skillEntry.InstanceId));
        }
        foreach (var seEntry in monster.Player.GetEffectiveSocketEffects())
        {
            var tpl = repo.GetCard(seEntry.TemplateId);
            if (tpl == null) continue;
            var seCard = new CardInstance(tpl, TierHelper.ParseTier(seEntry.Tier), seEntry.EnchantmentType, seEntry.InstanceId)
            {
                StartSlot = ParseSocketIndex(seEntry.SocketId)
            };
            state.SocketEffects.Add(seCard);
        }
        state.PrepareForCombat();
        return state;
    }

    private static int ParseSocketIndex(string? socketId)
    {
        if (string.IsNullOrEmpty(socketId)) return -1;
        var parts = socketId.Split('_');
        // 修复 P0-2：GameData.db monsters 表 SocketId 为 0-based（Socket_0..Socket_9），直接返回 idx 而非 idx - 1，根治全 179 只野怪左移撞车
        if (parts.Length >= 2 && int.TryParse(parts[^1], out int idx))
            return Math.Clamp(idx, 0, 9);
        return -1;
    }
}

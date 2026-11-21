using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Combat;

/// <summary>
/// 确定性战斗引擎：20ms 固定步长、250ms ICD 触发队列、Burn/Poison/Regen/Sandstorm 完整结算，
/// 既可给 Bot 毫秒级 Headless 演算，也可输出完整帧时间轴给 Godot 播放 1:1 战斗演出。
/// </summary>
public static class CombatSimulator
{
    public const int TickMs = 20;
    public const int MaxDurationMs = 300_000;
    public const int MaxForceUseDepth = 16;
    public const int MaxTickCascadeFires = 32;
    public const int MaxSameTickCardFires = 2;
    public const int MaxDispatchDepth = 24;

    private readonly record struct TriggerQueueKey(string PlayerId, string CardId);
    private readonly record struct IndexedAbility(CardInstance Card, AbilityDefinition Ability, int ActiveOrder);

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
    {
        var rng = new Random(seed);
        var p1 = originalP1.CloneForCombat();
        var p2 = originalP2.CloneForCombat();
        var gm = repo.GameMode;
        p1.EnsureExclusionSetInitialized(gm);
        p2.EnsureExclusionSetInitialized(gm);
        EnsureCombatBasePlayerEffect(repo, p1);
        EnsureCombatBasePlayerEffect(repo, p2);
        int icdMs = Math.Max(100, gm.CombatSimEffectDelay);
        int effectDelayMs = Math.Max(TickMs, gm.CombatSimEffectDelay > 0 ? gm.CombatSimEffectDelay : 250);

        var events = recordEvents ? new List<CombatEvent>() : new List<CombatEvent>(0);
        var timeline = recordTimeline ? new List<CombatTickState>() : new List<CombatTickState>(0);
        double p1DmgTotal = 0, p2DmgTotal = 0;
        double p1ShieldTotal = 0, p2ShieldTotal = 0;

        var icdReadyTime = new Dictionary<TriggerQueueKey, int>();
        var triggerQueues = new Dictionary<TriggerQueueKey, Queue<QueuedTrigger>>();
        var readyBatch = new List<(TriggerQueueKey Key, QueuedTrigger Item)>();
        Comparison<(TriggerQueueKey Key, QueuedTrigger Item)> readyBatchComparison = (a, b) =>
        {
            int sideCmp = (a.Item.Owner == p1 ? 0 : 1).CompareTo(b.Item.Owner == p1 ? 0 : 1);
            if (sideCmp != 0) return sideCmp;
            int slotCmp = a.Item.OwnerCard.StartSlot.CompareTo(b.Item.OwnerCard.StartSlot);
            if (slotCmp != 0) return slotCmp;
            int instCmp = string.CompareOrdinal(a.Item.OwnerCard.InstanceId, b.Item.OwnerCard.InstanceId);
            if (instCmp != 0) return instCmp;
            int prioCmp = GetAbilityPriorityRank(a.Item.Ability.Priority).CompareTo(GetAbilityPriorityRank(b.Item.Ability.Priority));
            if (prioCmp != 0) return prioCmp;
            return string.CompareOrdinal(a.Item.Ability.Id, b.Item.Ability.Id);
        };
        var pendingMulticasts = new List<PendingMulticastBatch>();
        var handSnapshot = new List<CardInstance>(16);
        var timedExpiries = new List<TimedAttributeExpiry>();
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
        var activeFiringCards = new HashSet<CardInstance>(ReferenceEqualityComparer.Instance);
        var tickCardFireCounts = new Dictionary<CardInstance, int>(ReferenceEqualityComparer.Instance);
        var lastCooldownFireMs = new Dictionary<CardInstance, int>(ReferenceEqualityComparer.Instance);
        RuleExecutionContext p1BaseCtx = null!;
        RuleExecutionContext p2BaseCtx = null!;

        bool sandstormWarned = false;
        bool sandstormActive = false;
        bool sandstormForceStarted = false;
        int sandstormStartMs = gm.Sandstorm.CountdownStartTime + gm.Sandstorm.CountdownDuration; // 默认 30,000ms
        int sandstormNextTickMs = sandstormStartMs;
        int sandstormCurrentRate = gm.Sandstorm.BaseTickRate; // 250ms
        int sandstormNextDmg = gm.Sandstorm.BaseDamage;       // 1

        int nextBurnTickMs = 500;
        int nextPoisonTickMs = 1000;
        int nextRegenTickMs = 1000;
        int p1TempoElapsedMs = 0;
        int p2TempoElapsedMs = 0;
        int timeMs = 0;

        bool p1DeathTriggered = false;
        bool p2DeathTriggered = false;
        int p1Overkill = 0;
        int p2Overkill = 0;

        static void ClampSideAmmo(PlayerState side)
        {
            foreach (var c in side.Hand.Cards)
            {
                int maxAmmo = Math.Max(0, (int)Math.Round(c.GetAttribute("AmmoMax")));
                c.CurrentAmmo = Math.Clamp(c.CurrentAmmo, 0, maxAmmo);
            }
            foreach (var c in side.Stash.Cards)
            {
                int maxAmmo = Math.Max(0, (int)Math.Round(c.GetAttribute("AmmoMax")));
                c.CurrentAmmo = Math.Clamp(c.CurrentAmmo, 0, maxAmmo);
            }
        }

        static void SyncEnragedState(PlayerState side)
        {
            side.Attributes["Enraged"] = side.EnragedRemainingMs > 0 ? 1.0 : 0.0;
            side.Attributes["EnragedDuration"] = side.EnragedRemainingMs;
        }

        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
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
                TargetCardInstanceId = ev.TargetCardInstanceId,
                Value = ev.Value,
                IsCrit = ev.IsCrit,
                Detail = ev.Detail,
                AbilityId = ev.AbilityId ?? activeExec.AbilityId,
                ActionType = ev.ActionType ?? activeExec.ActionType,
                IsSkillSource = ev.IsSkillSource || IsSkillCardId(ev.SourceCardInstanceId),
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

        void ApplyDirectDamage(PlayerState target, int amount, bool bypassShield)
        {
            if (amount <= 0) return;
            int prevShield = target.Shield;
            int prevHp = target.CurrentHealth;
            int remaining = amount;
            if (!bypassShield && target.Shield > 0)
            {
                int absorbed = Math.Min(target.Shield, remaining);
                target.Shield -= absorbed;
                remaining -= absorbed;
            }
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
            if (target.Shield < prevShield)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Shield", prevShield, target.Shield);
            if (target.CurrentHealth < prevHp)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Health", prevHp, target.CurrentHealth);
        }

        void ApplyHealthGain(PlayerState target, int amount)
        {
            if (amount <= 0) return;
            ref int overkill = ref (target == p1 ? ref p1Overkill : ref p2Overkill);
            if (overkill > 0)
            {
                if (amount <= overkill)
                {
                    overkill -= amount;
                    return;
                }
                amount -= overkill;
                overkill = 0;
            }
            target.CurrentHealth = Math.Min(target.HealthMax, target.CurrentHealth + amount);
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
            if (actual > 0)
            {
                int prev = getMs(targetCard);
                int next = prev + actual;
                setMs(targetCard, next);
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

        void CompleteReadyCooldowns(PlayerState cardOwner, PlayerState cardOpp, CardInstance targetCard, bool countFirstAsDepth)
        {
            if (targetCard.IsDestroyed || targetCard.IsDisabled
                || !targetCard.HasActiveCooldown
                || targetCard.FreezeMs > 0)
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

            int ammoMax = (int)Math.Round(targetCard.GetAttribute("AmmoMax"));
            if (ammoMax > 0 && targetCard.CurrentAmmo <= 0) return;
            int cdMax = GetCardCooldownMaxMs(targetCard);
            if (targetCard.CooldownProgressMs < cdMax) return;

            targetCard.CooldownProgressMs -= cdMax;
            if (targetCard.CooldownProgressMs > cdMax)
                targetCard.CooldownProgressMs = cdMax;

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
            int postAmmoMax = (int)Math.Round(targetCard.GetAttribute("AmmoMax"));
            if (postAmmoMax > 0 && targetCard.CurrentAmmo <= 0)
                targetCard.CooldownProgressMs = 0;
        }

        RuleExecutionContext CreateBaseContext(PlayerState self, PlayerState opp)
        {
            return new RuleExecutionContext(repo, RuleScope.InCombat, self, opp, rng)
            {
                LocalPlayerId = p1.PlayerId,
                CurrentDay = self.Day,
                CurrentHour = self.Hour,
                CombatType = combatType,
                ActiveExecution = activeExec,
                EmitCombatEvent = recordEvents ? Emit : null,
                OnTimedCardAttributeModifierApplied = (targetCard, attr, op, val, durationMs) =>
                {
                    if (op == "Multiply" && Math.Abs(val) < 1e-9) return;
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
                    if (op == "Multiply" && Math.Abs(val) < 1e-9) return;
                    timedExpiries.Add(new TimedAttributeExpiry
                    {
                        ExpireTimeMs = timeMs + durationMs,
                        Player = targetPlayer,
                        Attribute = attr,
                        Operation = op,
                        Value = val
                    });
                },
                OnDamageApplied = (targetPlayer, dmg, isCrit, srcCard) =>
                {
                    if (dmg <= 0) return;
                    // 对齐官方 PercentDamageReduction（0..100%，含 Memento Mori/Invulnerability Potion 100% 免伤）
                    var red = Math.Clamp(targetPlayer.GetAttribute("PercentDamageReduction"), 0, 100);
                    int actual = Math.Max(0, (int)Math.Round(dmg * (1.0 - red / 100.0)));
                    ApplyDirectDamage(targetPlayer, actual, bypassShield: false);

                    var attacker = targetPlayer == p1 ? p2 : p1;
                    if (actual > 0 && srcCard != null && srcCard.GetAttribute("Lifesteal") > 0)
                    {
                        int prevAttackerHp = attacker.CurrentHealth;
                        ApplyHealthGain(attacker, actual);
                        int healed = attacker.CurrentHealth - prevAttackerHp;
                        if (recordEvents)
                            Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.HealApplied, SourcePlayerId = attacker.PlayerId, TargetPlayerId = attacker.PlayerId, SourceCardInstanceId = srcCard.InstanceId, Value = actual, Detail = "Lifesteal" });
                        if (healed > 0)
                        {
                            DispatchEvent("TTriggerOnPlayerAttributeChanged", attacker, srcCard, srcCard, attacker, "Health", prevAttackerHp, attacker.CurrentHealth);
                            DispatchEvent("TTriggerOnPlayerLifeStealed", attacker, srcCard, srcCard, attacker, "Health", prevAttackerHp, healed);
                        }
                    }

                    if (targetPlayer == p2) p1DmgTotal += actual; else p2DmgTotal += actual;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.DamageApplied, SourcePlayerId = attacker.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = actual, IsCrit = isCrit });
                    DispatchEvent("TTriggerOnCardPerformedDamage", attacker, srcCard, srcCard, targetPlayer);
                    if (actual > 0)
                        DispatchEvent("TTriggerOnPlayerDamaged", attacker, srcCard, srcCard, targetPlayer, null, 0, actual);
                },
                OnShieldApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    int prevShield = targetPlayer.Shield;
                    targetPlayer.Shield += amt;
                    if (targetPlayer == p1) p1ShieldTotal += amt; else p2ShieldTotal += amt;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.ShieldApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = amt });
                    DispatchEvent("TTriggerOnCardPerformedShield", targetPlayer, srcCard, srcCard, targetPlayer);
                    DispatchEvent("TTriggerOnPlayerShielded", targetPlayer, srcCard, srcCard, targetPlayer, "Shield", prevShield, amt);
                    if (targetPlayer.Shield != prevShield)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Shield", prevShield, targetPlayer.Shield);
                },
                OnBurnApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    int prevBurn = targetPlayer.Burn;
                    targetPlayer.Burn += amt;
                    var actor = targetPlayer == p1 ? p2 : p1;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.BurnApplied, SourcePlayerId = actor.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = amt });
                    DispatchEvent("TTriggerOnCardPerformedBurn", actor, srcCard, srcCard, targetPlayer);
                    if (targetPlayer.Burn != prevBurn)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", actor, srcCard, srcCard, targetPlayer, "Burn", prevBurn, targetPlayer.Burn);
                },
                OnPoisonApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    int prevPoison = targetPlayer.Poison;
                    targetPlayer.Poison += amt;
                    var actor = targetPlayer == p1 ? p2 : p1;
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PoisonApplied, SourcePlayerId = actor.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = amt });
                    DispatchEvent("TTriggerOnCardPerformedPoison", actor, srcCard, srcCard, targetPlayer);
                    if (targetPlayer.Poison != prevPoison)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", actor, srcCard, srcCard, targetPlayer, "Poison", prevPoison, targetPlayer.Poison);
                },
                OnHealApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    int prevHp = targetPlayer.CurrentHealth;
                    int overkill = targetPlayer == p1 ? p1Overkill : p2Overkill;
                    int missing = Math.Max(0, targetPlayer.HealthMax - targetPlayer.CurrentHealth) + overkill;
                    // 对齐官方 OverHeal：在满血状态下治疗或本次治疗恰好达到/溢出 HealthMax 均触发 OverHeal
                    bool overheal = amt >= missing;
                    ApplyHealthGain(targetPlayer, amt);
                    // 8.4 官方规则：普通/小额治疗按治疗量的 5%（向下取整，至少 1 点）各自净化 Burn 与 Poison
                    int cleanse = Math.Max(1, (int)Math.Floor(amt * 0.05));
                    if (targetPlayer.Burn > 0)
                        targetPlayer.Burn = Math.Max(0, targetPlayer.Burn - cleanse);
                    if (targetPlayer.Poison > 0)
                        targetPlayer.Poison = Math.Max(0, targetPlayer.Poison - cleanse);

                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.HealApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = amt });
                    DispatchEvent("TTriggerOnCardPerformedHeal", targetPlayer, srcCard, srcCard, targetPlayer);
                    if (overheal)
                        DispatchEvent("TTriggerOnCardPerformedOverHeal", targetPlayer, srcCard, srcCard, targetPlayer);
                    if (targetPlayer.CurrentHealth != prevHp)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", targetPlayer, srcCard, srcCard, targetPlayer, "Health", prevHp, targetPlayer.CurrentHealth);
                },
                OnRegenApplied = (targetPlayer, amt, srcCard) =>
                {
                    if (amt <= 0) return;
                    targetPlayer.ModifyAttribute("HealthRegen", "Add", amt);
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.HealApplied, SourcePlayerId = targetPlayer.PlayerId, TargetPlayerId = targetPlayer.PlayerId, SourceCardInstanceId = srcCard?.InstanceId, Value = amt, Detail = "RegenGain" });
                    DispatchEvent("TTriggerOnCardPerformedRegen", targetPlayer, srcCard, srcCard, targetPlayer);
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
                    if (actual <= 0) return;
                    int cdMax = GetCardCooldownMaxMs(targetCard);
                    if (lastCooldownFireMs.TryGetValue(targetCard, out int prevCdMs) && timeMs - prevCdMs < icdMs)
                    {
                        targetCard.CooldownProgressMs = Math.Min(cdMax, targetCard.CooldownProgressMs + actual);
                    }
                    else
                    {
                        int overflowCap = Math.Max(cdMax * 2, 2000);
                        targetCard.CooldownProgressMs = Math.Min(overflowCap, targetCard.CooldownProgressMs + actual);
                    }
                    if (recordEvents)
                        Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.ChargeApplied, SourceCardInstanceId = srcCard?.InstanceId, TargetCardInstanceId = targetCard.InstanceId, Value = actual });
                    DispatchEvent("TTriggerOnCardCharged", self, srcCard, targetCard, self);
                    DispatchEvent("TTriggerOnCardPerformedCharge", self, srcCard, targetCard, self);

                    // 7.5：Charge 满冷却时保留溢出并在当前 20ms tick 立即开火（受单卡内置冷却与防死循环帧预算约束）
                    var cardOwner = (p1.Hand.Cards.Contains(targetCard) || p1.Skills.Contains(targetCard)) ? p1
                                  : ((p2.Hand.Cards.Contains(targetCard) || p2.Skills.Contains(targetCard)) ? p2 : null);
                    if (cardOwner == null) return;
                    CompleteReadyCooldowns(cardOwner, cardOwner == p1 ? p2 : p1, targetCard, countFirstAsDepth: true);
                },
                OnActionCostSpent = (srcCard, attr, amt) =>
                {
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
                    int targetAmmoMax = (int)Math.Round(targetCard.GetAttribute("AmmoMax"));
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
                        sandstormNextTickMs = timeMs;
                        if (!sandstormWarned)
                        {
                            sandstormWarned = true;
                            if (recordEvents)
                                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormWarning, Detail = "ForcedSandstorm" });
                        }
                        // 沙暴提前开启时仅在启动瞬间派发一次 TTriggerOnSandstorm
                        DispatchEvent("TTriggerOnSandstorm", p1, null, null, p1);
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
                        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
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
                        AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
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
                }
            };
        }

        p1BaseCtx = CreateBaseContext(p1, p2);
        p2BaseCtx = CreateBaseContext(p2, p1);

        void EnsureTriggerIndex()
        {
            int curTotal = p1.Hand.Cards.Count + p1.Skills.Count + p1.SocketEffects.Count + p1.PlayerEffects.Count + p1.Stash.Cards.Count
                         + p2.Hand.Cards.Count + p2.Skills.Count + p2.SocketEffects.Count + p2.PlayerEffects.Count + p2.Stash.Cards.Count;
            if (!triggerIndexDirty && curTotal == indexedCardTotal) return;

            p1TriggerIndex = BuildSideTriggerIndex(p1);
            p2TriggerIndex = BuildSideTriggerIndex(p2);
            p1HasQuests = HasAnyQuests(p1);
            p2HasQuests = HasAnyQuests(p2);
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
                        var prevAb = activeExec.AbilityId;
                        activeExec.AbilityId = ab.Id;
                        try
                        {
                            AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                        }
                        finally
                        {
                            activeExec.AbilityId = prevAb;
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
                else if (string.Equals(card.Template.Type, "PlayerEffect", StringComparison.OrdinalIgnoreCase) || IsStatGainAbility(ab))
                {
                    // 系统级 PlayerEffect（如 Base Rage Effect 同卡兄弟驱散 Reset Slow + Reset Freeze）与数值成长类效果（Stat Gains）豁免 250ms ICD 节流
                    localImmediate ??= new List<(CardInstance, AbilityDefinition, RuleExecutionContext)>(4);
                    localImmediate.Add((card, ab, abCtx));
                }
                else
                {
                    var key = new TriggerQueueKey(owner.PlayerId, card.InstanceId);
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
                bool ranStatGain = false;
                for (int i = 0; i < localImmediate.Count; i++)
                {
                    var (card, ab, abCtx) = localImmediate[i];
                    if (card.IsDestroyed || card.IsDisabled) continue;
                    var prevAb = activeExec.AbilityId;
                    activeExec.AbilityId = ab.Id;
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                        if (eventType != "TTriggerOnCardFired" && IsStatGainAbility(ab))
                            ranStatGain = true;
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                }
                if (ranStatGain)
                {
                    AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
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
                    activeExec.AbilityId = ab.Id;
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                }
            }

            if (executeAllImmediate && (localImmediate != null || localImmediateTransforms != null))
            {
                AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
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
                    activeExec.AbilityId = ab.Id;
                    try
                    {
                        AbilityExecutor.ExecuteAction(ab.Action, card, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                    }
                }
            }
        }

        void DispatchEvent(string eventType, PlayerState actorPlayer, CardInstance? sourceCard, CardInstance? targetCard, PlayerState? subjectPlayer, string? attrChanged = null, double prev = 0, double cur = 0)
        {
            if (dispatchDepth >= MaxDispatchDepth) return;
            dispatchDepth++;
            try
            {
            EnsureTriggerIndex();
            p1TriggerIndex.TryGetValue(eventType, out var p1List);
            p2TriggerIndex.TryGetValue(eventType, out var p2List);
            if ((p1List == null || p1List.Count == 0) && (p2List == null || p2List.Count == 0) && !p1HasQuests && !p2HasQuests)
                return;

            DispatchSide(p1List, p1, p2, p1BaseCtx, eventType, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur);
            DispatchSide(p2List, p2, p1, p2BaseCtx, eventType, sourceCard, targetCard, subjectPlayer, attrChanged, prev, cur);

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

                    readyBatch.Add((key, q.Dequeue()));
                    icdReadyTime[key] = timeMs + icdMs;
                }

                if (readyBatch.Count == 0) break;

                if (readyBatch.Count > 1)
                    readyBatch.Sort(readyBatchComparison);

                for (int i = 0; i < readyBatch.Count; i++)
                {
                    var item = readyBatch[i].Item;
                    if ((item.OwnerCard.IsDestroyed || item.OwnerCard.IsDisabled) && !item.AllowWhenDestroyed) continue;

                    var baseCtx = item.Owner == p1 ? p1BaseCtx : p2BaseCtx;
                    baseCtx.TimeMs = timeMs;
                    baseCtx.LastAttributeChangeDelta = item.AttributeDelta;
                    var abCtx = baseCtx.WithCards(item.OwnerCard, item.SourceCard, item.TargetCard);
                    if (sandstormForceStarted)
                        ValueAndConditionEvaluator.SetSandstormActive(abCtx, true);
                    var prevAb = activeExec.AbilityId;
                    activeExec.AbilityId = item.Ability.Id;
                    try
                    {
                        AbilityExecutor.ExecuteAction(item.Ability.Action, item.OwnerCard, abCtx);
                    }
                    finally
                    {
                        activeExec.AbilityId = prevAb;
                    }
                    SyncEnragedState(p1);
                    SyncEnragedState(p2);
                    AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
                    ClampSideAmmo(p1);
                    ClampSideAmmo(p2);
                }
            }
        }

        void ProcessTimedExpiries()
        {
            if (timedExpiries.Count == 0) return;
            bool anyExpired = false;
            for (int i = timedExpiries.Count - 1; i >= 0; i--)
            {
                var exp = timedExpiries[i];
                if (timeMs < exp.ExpireTimeMs) continue;
                timedExpiries.RemoveAt(i);
                anyExpired = true;

                if (exp.Card != null)
                {
                    double prev = exp.Card.GetAttribute(exp.Attribute);
                    RevertCombatModifier(exp.Card.CombatAdd, exp.Card.CombatMult, exp.Attribute, exp.Operation, exp.Value);
                    double cur = exp.Card.GetAttribute(exp.Attribute);
                    if (Math.Abs(cur - prev) > 1e-6)
                    {
                        var owner = (p1.Hand.Cards.Contains(exp.Card) || p1.Stash.Cards.Contains(exp.Card) || p1.Skills.Contains(exp.Card) || p1.SocketEffects.Contains(exp.Card))
                            ? p1 : p2;
                        if (recordEvents)
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
                        DispatchEvent("TTriggerOnCardAttributeChanged", owner, exp.Card, exp.Card, owner, exp.Attribute, prev, cur);
                    }
                }
                else if (exp.Player != null)
                {
                    double prev = exp.Player.GetAttribute(exp.Attribute);
                    RevertCombatModifier(exp.Player.CombatAdd, exp.Player.CombatMult, exp.Attribute, exp.Operation, exp.Value);
                    if (exp.Attribute.Equals("HealthMax", StringComparison.OrdinalIgnoreCase))
                        exp.Player.CurrentHealth = Math.Min(exp.Player.CurrentHealth, exp.Player.HealthMax);
                    double cur = exp.Player.GetAttribute(exp.Attribute);
                    if (Math.Abs(cur - prev) > 1e-6)
                        DispatchEvent("TTriggerOnPlayerAttributeChanged", exp.Player, null, null, exp.Player, exp.Attribute, prev, cur);
                }
            }
            if (anyExpired)
            {
                AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
                ClampSideAmmo(p1);
                ClampSideAmmo(p2);
            }
        }

        void ExecuteCardCast(PlayerState owner, PlayerState opp, CardInstance card, int castNumber, AbilityDefinition? primaryFiredAb)
        {
            if (card.IsDestroyed || card.IsDisabled) return;

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
            DispatchEvent("TTriggerOnCardFired", owner, card, card, owner);
            DispatchEvent("TTriggerOnItemUsed", owner, card, card, owner);
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
            }
            AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
            ClampSideAmmo(p1);
            ClampSideAmmo(p2);
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
                // 检查 Ammo（每次冷却或强制激活固定消耗 1 弹药；特殊牌如 Shuriken 由自身 Ability JSON 额外清弹药）
                int ammoMax = (int)Math.Round(card.GetAttribute("AmmoMax"));
                if (ammoMax > 0)
                {
                    if (card.CurrentAmmo <= 0) return;
                    int prevAmmo = card.CurrentAmmo;
                    card.CurrentAmmo = Math.Clamp(card.CurrentAmmo - 1, 0, Math.Max(0, ammoMax));
                    DispatchEvent("TTriggerOnCardAttributeChanged", owner, card, card, owner, "Ammo", prevAmmo, card.CurrentAmmo);
                }

                int multicast = Math.Clamp((int)Math.Round(card.GetAttribute("Multicast")), 1, 10);
                if (multicast < 1) multicast = 1;

                DispatchEvent("TTriggerOnBeforeItemUsed", owner, card, card, owner);
                if (card.IsDestroyed || card.IsDisabled) return;

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
            for (int pass = 0; pass < 2; pass++)
            {
                bool triggeredAny = false;
                if (!p2.IsAlive && !p2DeathTriggered)
                {
                    p2DeathTriggered = true;
                    p2Overkill = 0; // 对齐官方 HealthSetToZeroFromOnDeath：先归零再执行 OnDeath
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
            if (p2.IsAlive) p2Overkill = 0;
            return !p1.IsAlive || !p2.IsAlive;
        }

        if (recordEvents)
            Emit(new CombatEvent { TimeMs = 0, Type = CombatEventType.FightStarted, SourcePlayerId = p1.PlayerId, TargetPlayerId = p2.PlayerId });
        DispatchEvent("TTriggerOnFightStarted", p1, null, null, p1);
        ProcessIcdQueues();
        CheckEndOfTickDeath();

        // 主循环：20ms 步进
        while (p1.IsAlive && p2.IsAlive && timeMs < MaxDurationMs)
        {
            tickCascadeFires = 0;
            if (tickCardFireCounts.Count > 0) tickCardFireCounts.Clear();
            p1Overkill = 0;
            p2Overkill = 0;
            bool p1AliveAtTickStart = p1.IsAlive;
            bool p2AliveAtTickStart = p2.IsAlive;

            // 0. 撤销到期的 TCombatDuration 限时修饰
            ProcessTimedExpiries();

            // 0b. 推进 Multicast 延迟连发（在冻结倒计时扣减前检查，保证冻结帧不推进连发计时）
            ProcessPendingMulticasts();

            // 0c. 推进狂暴持续时间并实时同步 Enraged 状态与光环（在推进物品冷却前结算，确保满额持续时间）
            TickEnraged(p1);
            TickEnraged(p2);

            // 1. 推进物品冷却与状态计时
            AdvanceSideCards(p1, p2, FireCard);
            AdvanceSideCards(p2, p1, FireCard);

            // 2. 处理被动 Tempo 节奏获能周期
            TickPassiveTempo(p1, p1AliveAtTickStart, ref p1TempoElapsedMs);
            TickPassiveTempo(p2, p2AliveAtTickStart, ref p2TempoElapsedMs);

            // 3. 处理 ICD 触发队列
            ProcessIcdQueues();

            // 4. Burn (500ms/跳, 有盾伤害减半，后衰减 3% 至少 1 点)
            if (timeMs >= nextBurnTickMs)
            {
                nextBurnTickMs += 500;
                TickBurn(p1);
                TickBurn(p2);
            }

            // 5. Poison (1000ms/跳, 穿盾直接扣血) & Regen (1000ms/跳)
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

            // 6. Sandstorm (20s 预警, 30s 爆发, 伤害1起每跳+2, 间隔250ms递减10ms至150ms)
            if (!sandstormWarned && !sandstormForceStarted && timeMs >= gm.Sandstorm.CountdownStartTime)
            {
                sandstormWarned = true;
                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormWarning });
            }
            if (!sandstormActive && timeMs >= sandstormStartMs)
            {
                sandstormActive = true;
                sandstormNextTickMs = timeMs;
                // 自然启动瞬间仅派发一次 TTriggerOnSandstorm，并在首跳沙暴伤害前先结算对应被动/光环
                DispatchEvent("TTriggerOnSandstorm", p1, null, null, p1);
                ProcessIcdQueues();
            }
            if (sandstormActive && timeMs >= sandstormNextTickMs)
            {
                double red1 = Math.Clamp(p1.GetAttribute("PercentDamageReduction"), 0, 100);
                double red2 = Math.Clamp(p2.GetAttribute("PercentDamageReduction"), 0, 100);
                int sandDmg1 = Math.Max(0, (int)Math.Round(sandstormNextDmg * (1.0 - red1 / 100.0)));
                int sandDmg2 = Math.Max(0, (int)Math.Round(sandstormNextDmg * (1.0 - red2 / 100.0)));
                ApplyDirectDamage(p1, sandDmg1, bypassShield: false);
                ApplyDirectDamage(p2, sandDmg2, bypassShield: false);
                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.SandstormTick, Value = sandstormNextDmg });
                sandstormNextDmg += gm.Sandstorm.AdditiveScalar;
                sandstormNextTickMs += sandstormCurrentRate;
                sandstormCurrentRate = Math.Max(gm.Sandstorm.TickRateFloor, sandstormCurrentRate - gm.Sandstorm.TickRateDecrement);
            }

            // 7. 帧末完整死亡与复活/遗言检查：先处理每方至多一次 TTriggerOnPlayerDied 并推进队列，再决定是否结束战斗
            bool anyDeadAfterOnDeath = CheckEndOfTickDeath();

            if (recordTimeline)
            {
                timeline.Add(CaptureSnapshot(timeMs, p1, p2, sandstormActive, sandstormNextDmg, gm.StandardPrices));
            }

            timeMs += TickMs;
            if (anyDeadAfterOnDeath) break;
        }

        // 兜底：若从 MaxDurationMs 超时边界或开战瞬间退出主循环且仍有未触发 OnDeath 的倒地玩家，补发一次（不重复派发）
        if ((!p2.IsAlive && !p2DeathTriggered) || (!p1.IsAlive && !p1DeathTriggered))
        {
            CheckEndOfTickDeath();
        }

        // 决定胜负
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
            timeline.Add(CaptureSnapshot(timeMs, p1, p2, sandstormActive, sandstormNextDmg, gm.StandardPrices));

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

        var result = new CombatResult
        {
            WinnerPlayerId = p1Won ? p1.PlayerId : p2.PlayerId,
            LoserPlayerId = p1Won ? p2.PlayerId : p1.PlayerId,
            IsDraw = isDraw,
            DurationMs = timeMs,
            Player1RemainingHp = p1.CurrentHealth,
            Player2RemainingHp = p2.CurrentHealth,
            Player1TotalDamageDealt = p1DmgTotal,
            Player2TotalDamageDealt = p2DmgTotal,
            Player1TotalShieldGained = p1ShieldTotal,
            Player2TotalShieldGained = p2ShieldTotal,
            Events = events,
            Timeline = timeline,
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

        void AdvanceSideCards(PlayerState owner, PlayerState opp, Action<PlayerState, PlayerState, CardInstance, bool> onReady)
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
                if (card.IsDestroyed || card.IsDisabled || !card.HasActiveCooldown) continue;

                int ammoMax = (int)Math.Round(card.GetAttribute("AmmoMax"));
                bool outOfAmmo = ammoMax > 0 && card.CurrentAmmo <= 0;

                // 对齐 BazaarCardDealer.cs:4110-4151 & 4401-4406：Freeze/Haste/Slow 三计时器每帧独立递减，再检查冻结与缺弹阻断
                bool hasted = card.HasteMs > 0;
                bool slowed = card.SlowMs > 0;
                bool frozen = card.FreezeMs > 0;

                if (card.HasteMs > 0) card.HasteMs = Math.Max(0, card.HasteMs - TickMs);
                if (card.SlowMs > 0) card.SlowMs = Math.Max(0, card.SlowMs - TickMs);
                if (card.FreezeMs > 0) card.FreezeMs = Math.Max(0, card.FreezeMs - TickMs);

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

                int cdMax = GetCardCooldownMaxMs(card);
                double rawStep = TickMs * speed;
                double minCycleCapStep = TickMs * (cdMax / 1000.0);
                card.CooldownProgressMs += Math.Max(1, (int)Math.Round(Math.Min(rawStep, minCycleCapStep)));
                if (lastCooldownFireMs.TryGetValue(card, out int prevCdMs) && timeMs - prevCdMs < icdMs)
                {
                    if (card.CooldownProgressMs > cdMax) card.CooldownProgressMs = cdMax;
                    continue;
                }
                if (card.CooldownProgressMs < cdMax) continue;
                CompleteReadyCooldowns(owner, opp, card, countFirstAsDepth: false);
            }
        }

        void TickEnraged(PlayerState p)
        {
            if (p.EnragedRemainingMs <= 0)
            {
                if (p.Attributes.GetValueOrDefault("Enraged", 0) != 0)
                    SyncEnragedState(p);
                return;
            }
            p.EnragedRemainingMs = Math.Max(0, p.EnragedRemainingMs - TickMs);
            SyncEnragedState(p);
            if (p.EnragedRemainingMs == 0)
            {
                AuraEngine.RecalculateAuras(p1, p2, RuleScope.InCombat, p1.Day, p1.Hour, repo);
                if (recordEvents)
                    Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PlayerEnrageEnded, TargetPlayerId = p.PlayerId });
                DispatchEvent("TTriggerOnPlayerEnrageEnded", p, null, null, p);
            }
        }

        void TickBurn(PlayerState target)
        {
            if (target.Burn <= 0) return;
            // 8.5：护盾对 Burn 的减免上限受 target.Shield 约束（向上取半确保满盾时与 floor(Burn/2) 一致，同时防止 1 点护盾将 100 点 Burn 减半）
            int mitigation = target.Shield > 0 ? Math.Min(target.Shield, (target.Burn + 1) / 2) : 0;
            int dmg = Math.Max(1, target.Burn - mitigation);
            ApplyDirectDamage(target, dmg, bypassShield: false);
            if (target == p2) p1DmgTotal += dmg; else p2DmgTotal += dmg;

            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.BurnTick, TargetPlayerId = target.PlayerId, Value = dmg });

            int prevBurn = target.Burn;
            int decay = Math.Max(1, (int)Math.Floor(target.Burn * (gm.BurnDecrementByAsPercentage / 100.0)));
            target.Burn = Math.Max(0, target.Burn - decay);
            if (target.Burn != prevBurn)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Burn", prevBurn, target.Burn);
        }

        void TickPoison(PlayerState target)
        {
            if (target.Poison <= 0) return;
            int dmg = target.Poison;
            ApplyDirectDamage(target, dmg, bypassShield: true);
            if (target == p2) p1DmgTotal += dmg; else p2DmgTotal += dmg;
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.PoisonTick, TargetPlayerId = target.PlayerId, Value = dmg });
        }

        void TickRegen(PlayerState target, bool wasAliveAtTickStart)
        {
            int regen = (int)Math.Round(target.GetAttribute("HealthRegen"));
            if (regen <= 0 || !wasAliveAtTickStart) return;
            int prevHp = target.CurrentHealth;
            ApplyHealthGain(target, regen);
            if (target.CurrentHealth != prevHp)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", target, null, null, target, "Health", prevHp, target.CurrentHealth);
            if (recordEvents)
                Emit(new CombatEvent { TimeMs = timeMs, Type = CombatEventType.RegenTick, TargetPlayerId = target.PlayerId, Value = regen });
        }

        static bool HasTempoSystemCards(PlayerState side)
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
            if (!wasAliveAtTickStart || !side.IsAlive || !HasTempoSystemCards(side))
            {
                elapsedMs = 0;
                return;
            }
            int intervalMs = GetEffectiveTempoGainIntervalMs(side);
            if (intervalMs == int.MaxValue) return;
            elapsedMs += TickMs;
            if (elapsedMs < intervalMs) return;
            elapsedMs -= intervalMs;
            int prevTempo = side.Tempo;
            side.ModifyAttribute("Tempo", "Add", 1);
            if (side.Tempo != prevTempo)
                DispatchEvent("TTriggerOnPlayerAttributeChanged", side, null, null, side, "Tempo", prevTempo, side.Tempo);
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
        var simReplacementsByOrigId = new Dictionary<string, CardInstance>(StringComparer.Ordinal);
        var simNewNonTempCards = new List<(CardInstance Card, bool IsStash, bool IsSkill, bool IsSocketEffect)>();

        void ScanSimList(List<CardInstance> cards, bool isStash, bool isSkill, bool isSocketEffect)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                var sc = cards[i];
                simCardsById[sc.InstanceId] = sc;

                if (!string.IsNullOrEmpty(sc.TransformedFromInstanceId) && !sc.IsCombatTemporary)
                {
                    simReplacementsByOrigId[sc.TransformedFromInstanceId] = sc;
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

        // 1. 回写所有源卡牌的永久变化（含永久变形）
        foreach (var (origId, origCard) in origCardsById.ToList())
        {
            if (simReplacementsByOrigId.TryGetValue(origId, out var simRep))
            {
                var cleanRep = ClonePermanentCardSnapshot(simRep);
                cleanRep.StartSlot = origCard.StartSlot;
                cleanRep.TransformedFromInstanceId = origId;

                var delta = new CardPermanentSyncDelta
                {
                    InstanceId = origId,
                    SourceCard = origCard,
                    TransformReplacementTemplateId = simRep.Template.Id,
                    TransformedReplacementCard = cleanRep
                };
                sync.UpdatedCards.Add(delta);

                if (applyToOriginal)
                {
                    origCard.TransformReplacementTemplateId = simRep.Template.Id;
                    ReplaceCardInOriginalOwner(originalP1, origCard, cleanRep);
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
                if (!originalP1.Hand.TryAddAuto(cleanCard))
                    originalP1.Stash.TryAddAuto(cleanCard);
            }
        }

        // 3. 同步 P1 永久玩家属性增量（Gold, Income, HealthMax, Experience 等）
        var baselineAttrs = simP1.CombatSourcePlayerAttributes ?? originalP1.Attributes;
        var playerAttrKeys = new HashSet<string>(simP1.Attributes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in baselineAttrs.Keys) playerAttrKeys.Add(k);

        foreach (var k in playerAttrKeys)
        {
            if (k.Equals("Health", StringComparison.OrdinalIgnoreCase)) continue;
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

    private static void RevertCombatModifier(
        Dictionary<string, double> addDict,
        Dictionary<string, double> multDict,
        string attr,
        string operation,
        double value)
    {
        switch (operation)
        {
            case "Subtract":
            {
                double next = addDict.GetValueOrDefault(attr, 0) + value;
                if (Math.Abs(next) < 1e-9) addDict.Remove(attr);
                else addDict[attr] = next;
                break;
            }
            case "Multiply":
            {
                if (Math.Abs(value) > 1e-9)
                {
                    double next = multDict.GetValueOrDefault(attr, 1.0) / value;
                    if (Math.Abs(next - 1.0) < 1e-9) multDict.Remove(attr);
                    else multDict[attr] = next;
                }
                break;
            }
            case "AdditiveMultiply":
            {
                double next = multDict.GetValueOrDefault(attr, 1.0) - value;
                if (Math.Abs(next - 1.0) < 1e-9) multDict.Remove(attr);
                else multDict[attr] = next;
                break;
            }
            default: // Add
            {
                double next = addDict.GetValueOrDefault(attr, 0) - value;
                if (Math.Abs(next) < 1e-9) addDict.Remove(attr);
                else addDict[attr] = next;
                break;
            }
        }
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

        IndexList(owner.Hand.Cards);
        IndexList(owner.Skills);
        IndexList(owner.SocketEffects);
        IndexList(owner.PlayerEffects);

        foreach (var list in index.Values)
        {
            if (list.Count > 1)
            {
                list.Sort(static (a, b) =>
                {
                    int slotCmp = a.Card.StartSlot.CompareTo(b.Card.StartSlot);
                    if (slotCmp != 0) return slotCmp;
                    int instCmp = string.CompareOrdinal(a.Card.InstanceId, b.Card.InstanceId);
                    if (instCmp != 0) return instCmp;
                    int prioCmp = GetAbilityPriorityRank(a.Ability.Priority).CompareTo(GetAbilityPriorityRank(b.Ability.Priority));
                    if (prioCmp != 0) return prioCmp;
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

    private static void EnsureCombatBasePlayerEffect(GameDataRepository repo, PlayerState side)
    {
        const string BaseRageEffectId = "4472da8a-26a3-4e10-bd9a-e93c2e22f19c";
        if (side.Skills.Any(s => string.Equals(s.Template.Id, BaseRageEffectId, StringComparison.OrdinalIgnoreCase))
            || side.PlayerEffects.Any(pe => string.Equals(pe.Template.Id, BaseRageEffectId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var baseRageTpl = repo.GetCard(BaseRageEffectId);
        if (baseRageTpl != null)
            side.PlayerEffects.Add(new CardInstance(baseRageTpl));
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

    private static CombatTickState CaptureSnapshot(int timeMs, PlayerState p1, PlayerState p2, bool sandstormActive, int nextSandstormDmg, StandardPricesSettings prices)
    {
        static int ReadIntAttr(CardInstance c, string attr)
        {
            double v = c.GetAttribute(attr);
            if (double.IsNaN(v) || double.IsInfinity(v)) return 0;
            return (int)Math.Round(v);
        }

        var cards = new List<CardCooldownFrame>(p1.Hand.Cards.Count + p2.Hand.Cards.Count);
        foreach (var c in p1.Hand.Cards.Concat(p2.Hand.Cards))
        {
            double cd = Math.Max(1000, c.EffectiveCooldownMs);
            cards.Add(new CardCooldownFrame
            {
                InstanceId = c.InstanceId,
                Progress01 = c.HasActiveCooldown ? (float)Math.Clamp(c.CooldownProgressMs / cd, 0, 1) : 0f,
                IsHasted = c.HasteMs > 0,
                IsSlowed = c.SlowMs > 0,
                IsFrozen = c.FreezeMs > 0,
                IsFlying = c.IsFlying,
                IsDestroyed = c.IsDestroyed,
                Ammo = c.CurrentAmmo,
                HasteMs = c.HasteMs,
                SlowMs = c.SlowMs,
                FreezeMs = c.FreezeMs,
                Heated = c.Heated || c.GetAttribute("Heated") > 0,
                Chilled = c.Chilled || c.GetAttribute("Chilled") > 0,
                AmmoMax = Math.Max(0, ReadIntAttr(c, "AmmoMax")),
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
                Counter = c.TimesFiredThisFight
            });
        }
        return new CombatTickState
        {
            TimeMs = timeMs,
            P1Hp = p1.CurrentHealth,
            P1MaxHp = p1.HealthMax,
            P1Shield = p1.Shield,
            P1Burn = p1.Burn,
            P1Poison = p1.Poison,
            P1Regen = (int)Math.Round(p1.GetAttribute("HealthRegen")),
            P1Rage = p1.Rage,
            P1Enraged = p1.IsEnraged,
            P1Tempo = p1.Tempo,
            P2Hp = p2.CurrentHealth,
            P2MaxHp = p2.HealthMax,
            P2Shield = p2.Shield,
            P2Burn = p2.Burn,
            P2Poison = p2.Poison,
            P2Regen = (int)Math.Round(p2.GetAttribute("HealthRegen")),
            P2Rage = p2.Rage,
            P2Enraged = p2.IsEnraged,
            P2Tempo = p2.Tempo,
            SandstormActive = sandstormActive,
            SandstormNextDamage = nextSandstormDmg,
            Cards = cards
        };
    }

    /// <summary>
    /// 依据 GameData.db 中的 MonsterTemplate 构建野怪战斗状态（精准还原槽位掩码、站位、品质与附魔）。
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

        foreach (var skillEntry in monster.Player.Skills)
        {
            var tpl = repo.GetCard(skillEntry.TemplateId);
            if (tpl == null) continue;
            state.Skills.Add(new CardInstance(tpl, TierHelper.ParseTier(skillEntry.Tier), null, skillEntry.InstanceId));
        }
        foreach (var seEntry in monster.Player.SocketEffects)
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
        if (parts.Length >= 2 && int.TryParse(parts[^1], out int idx))
            return Math.Clamp(idx - 1, 0, 9);
        return -1;
    }
}

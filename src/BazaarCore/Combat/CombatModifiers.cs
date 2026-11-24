using BazaarCore.Engine;
using BazaarCore.Models;

namespace BazaarCore.Combat;

/// <summary>
/// P0-22：通用数据驱动的周期性战斗突变词条效果（由 Mod 自定义突变词条 Rules 解析生成，零单词条 ID 硬编码）。
/// </summary>
public sealed class PeriodicCombatMutatorEffect
{
    public string SourceMutatorId { get; init; } = string.Empty;
    public int TriggerAfterMs { get; init; }
    public int IntervalMs { get; init; } = 1000;
    public int BothApplyBurn { get; init; }
    public int BothApplyPoison { get; init; }
    public int BothApplyShield { get; init; }
    public int BothApplyHeal { get; init; }
    public int BothApplyDamage { get; init; }
    public int AllItemsChargeMsPerTick { get; init; }
}

/// <summary>
/// 战斗期规则修正器：统一承载 14 条肉鸽突变词条、自定义 Mod 突变词条、预设档位敌方强化、无尽爬塔高层缩放与 Boss 车轮战缩放，
/// 并支持按需控制沙暴启停与时间轴快照采样间隔。
/// </summary>
public sealed class CombatModifiers
{
    /// <summary>双方统一冷却缩减百分比（0.0..0.8，如 turbo_frenzy = 0.35）。</summary>
    public double CooldownReductionPct { get; init; }

    /// <summary>主动物品冷却下限（默认 1000ms；极速狂潮词条启用时下探至 500ms）。</summary>
    public int MinCooldownFloorMs { get; init; } = 1000;

    /// <summary>双方伤害/灼烧/剧毒施加量统一倍率（如 glass_cannon = 1.80）。</summary>
    public double DamageAndDotMultiplier { get; init; } = 1.0;

    /// <summary>玩家 1（本地玩家）生命上限倍率。</summary>
    public double HealthMultiplierP1 { get; init; } = 1.0;

    /// <summary>玩家 2（对手/野怪/Boss）生命上限倍率。</summary>
    public double HealthMultiplierP2 { get; init; } = 1.0;

    /// <summary>玩家 2（对手/野怪/Boss）最大血量安全封顶（防 Int32 溢出）。</summary>
    public int EnemyHealthCap { get; init; } = EndlessTowerScaler.MaxEnemyHealthCap;

    /// <summary>玩家 2（对手/野怪/Boss）攻防数值缩放倍率。</summary>
    public double OffenseMultiplierP2 { get; init; } = 1.0;

    /// <summary>玩家 2 额外冷却缩减百分比（如无尽高层敌方急速）。</summary>
    public double CooldownReductionP2Pct { get; init; }

    /// <summary>全员所有主动物品默认额外 Multicast 层数（如 double_trigger = +1，单卡加成封顶 +2）。</summary>
    public int BonusMulticastAll { get; init; }

    /// <summary>单卡额外 Multicast 加成封顶（默认 2）。</summary>
    public int MaxBonusMulticastCap { get; init; } = 2;

    /// <summary>全员基础暴击率加成（如 crit_overload = +50）。</summary>
    public int BonusCritChance { get; init; }

    /// <summary>暴击倍率覆盖百分比（如 crit_overload = 275，null 时使用 GameMode 默认 200%）。</summary>
    public int? CritPercentMultiplierOverride { get; init; }

    /// <summary>护盾与再生效能倍率（如 titan_vitality = 1.50）。</summary>
    public double ShieldAndRegenMultiplier { get; init; } = 1.0;

    /// <summary>沙暴启动时间偏移毫秒（负值提前，如 turbo_frenzy = -5000；正值延后，如 titan_vitality = +10000）。</summary>
    public int SandstormStartOffsetMs { get; init; }

    /// <summary>是否启用沙暴（默认 true；可被具体野怪/模式覆盖）。</summary>
    public bool SandstormEnabled { get; init; } = true;

    /// <summary>周期性环境冰冻间隔毫秒（0 表示不启用；frozen_wasteland = 4000）。</summary>
    public int PeriodicFreezeIntervalMs { get; init; }

    /// <summary>周期性环境冰冻持续毫秒（frozen_wasteland = 1000）。</summary>
    public int PeriodicFreezeDurationMs { get; init; }

    /// <summary>可选时间轴帧快照采样步长（毫秒）；null 时沿用 Simulate 参数默认步长。</summary>
    public int? TimelineSampleIntervalMs { get; init; }

    /// <summary>D1-P2-08：可选战斗最大时长上限覆盖（毫秒；null 时保持默认 300_000ms，可设为 CombatSimulator.OfficialCombatSimMaxTimeMs = 120_000）。</summary>
    public int? MaxDurationMsOverride { get; init; }

    /// <summary>P0-22：通用数据驱动的周期性自定义战斗突变词条列表。</summary>
    public IReadOnlyList<PeriodicCombatMutatorEffect> PeriodicEffects { get; init; } = Array.Empty<PeriodicCombatMutatorEffect>();

    /// <summary>空修正（零额外倍率，对齐纯净官方战斗）。</summary>
    public static CombatModifiers None { get; } = new();

    /// <summary>判断当前是否包含任何非默认战斗修正。</summary>
    public bool HasAnyCombatOverride =>
        Math.Abs(CooldownReductionPct) > 1e-6
        || MinCooldownFloorMs != 1000
        || Math.Abs(DamageAndDotMultiplier - 1.0) > 1e-6
        || Math.Abs(HealthMultiplierP1 - 1.0) > 1e-6
        || Math.Abs(HealthMultiplierP2 - 1.0) > 1e-6
        || Math.Abs(OffenseMultiplierP2 - 1.0) > 1e-6
        || Math.Abs(CooldownReductionP2Pct) > 1e-6
        || BonusMulticastAll > 0
        || BonusCritChance != 0
        || CritPercentMultiplierOverride.HasValue
        || Math.Abs(ShieldAndRegenMultiplier - 1.0) > 1e-6
        || SandstormStartOffsetMs != 0
        || !SandstormEnabled
        || (PeriodicFreezeIntervalMs > 0 && PeriodicFreezeDurationMs > 0)
        || PeriodicEffects.Count > 0;

    /// <summary>
    /// 依据自定义规则集（含预设档位与突变词条）、当前天数、是否 Boss 车轮战及是否 PvE 构造完整战斗修正对象。
    /// </summary>
    public static CombatModifiers FromRulesAndDay(
        CustomRunRuleSet? rules,
        int day,
        bool isBossRush,
        bool isPve)
        => FromRulesAndDay(rules, day, isBossRush, isPve, bossRushWave: isBossRush ? Math.Max(1, day) : 0, isSymmetricHumanPvp: false, endlessGhostSourceDay: null);

    /// <summary>
    /// 依据自定义规则集、当前天数与精确 Boss 车轮战波次构造完整战斗修正对象（兼容历史 5 参签名）。
    /// </summary>
    public static CombatModifiers FromRulesAndDay(
        CustomRunRuleSet? rules,
        int day,
        bool isBossRush,
        bool isPve,
        int bossRushWave)
        => FromRulesAndDay(rules, day, isBossRush, isPve, bossRushWave, isSymmetricHumanPvp: false, endlessGhostSourceDay: null);

    /// <summary>
    /// 依据自定义规则集、当前天数、精确 Boss 车轮战波次、是否真人同步对称 PvP（P0-40）、无尽幽灵源层数（P0-43）及可选单局自定义突变词条（P0-22）构造完整战斗修正对象。
    /// </summary>
    public static CombatModifiers FromRulesAndDay(
        CustomRunRuleSet? rules,
        int day,
        bool isBossRush,
        bool isPve,
        int bossRushWave,
        bool isSymmetricHumanPvp,
        int? endlessGhostSourceDay = null,
        IReadOnlyDictionary<string, MutatorMetadata>? customMutators = null)
    {
        if (rules == null && !isBossRush)
            return None;

        double cdrPct = 0.0;
        int minCdFloor = 1000;
        double dmgDotMult = 1.0;
        double hpP1 = 1.0;
        double hpP2 = 1.0;
        double offP2 = 1.0;
        double cdrP2 = 0.0;
        int bonusMulticast = 0;
        int bonusCrit = 0;
        int? critOverride = null;
        double shieldRegenMult = 1.0;
        int sandstormOffset = 0;
        int freezeInterval = 0;
        int freezeDuration = 0;
        List<PeriodicCombatMutatorEffect>? periodicEffects = null;

        if (rules != null)
        {
            // 1. 预设档位基础敌方倍率（如 ExtremeRoguelike 1.25x）；P0-40 真人同步对称 PvP 严禁单边施加敌方倍率
            if (!isSymmetricHumanPvp && Math.Abs(rules.EnemyStatMultiplier - 1.0) > 1e-6)
            {
                double em = Math.Clamp(rules.EnemyStatMultiplier, 0.25, 10.0);
                hpP2 *= em;
                offP2 *= em;
            }

            // 2. 无尽爬塔 Day 11~99 动态缩放（P0-40 同步真人 PvP 禁止单边缩放；P0-43 异步幽灵按 SourceDay -> TargetDay 相对缩放）
            if (!isSymmetricHumanPvp && rules.SpecialModeId == ESpecialModeId.EndlessTower && day > 10)
            {
                if (!isPve && endlessGhostSourceDay.HasValue)
                {
                    var rel = EndlessTowerScaler.ComputeRelativeScaling(endlessGhostSourceDay.Value, day, rules.EndlessMaxDay);
                    hpP2 *= rel.HealthMultiplier;
                    offP2 *= rel.OffenseMultiplier;
                    cdrP2 = Math.Min(EndlessTowerScaler.MaxCooldownReductionPct, cdrP2 + rel.CooldownReductionPct);
                }
                else
                {
                    var scaling = EndlessTowerScaler.Compute(day, rules.EndlessMaxDay);
                    hpP2 *= scaling.EnemyHealthMultiplier;
                    offP2 *= scaling.EnemyOffenseMultiplier;
                    cdrP2 = Math.Min(EndlessTowerScaler.MaxCooldownReductionPct, cdrP2 + scaling.EnemyCooldownReductionPct);
                }
            }

            // 3. 14 条内置突变词条中的战斗钩子（均为双边对称词条，同步 PvP 中完整保留）
            if (rules.HasMutator(MutatorCatalog.TurboFrenzy))
            {
                cdrPct += 0.35;
                minCdFloor = Math.Min(minCdFloor, 500);
                sandstormOffset -= 5000;
            }
            if (rules.HasMutator(MutatorCatalog.GlassCannon))
            {
                hpP1 *= 0.60;
                hpP2 *= 0.60;
                dmgDotMult *= 1.80;
            }
            if (rules.HasMutator(MutatorCatalog.TitanVitality))
            {
                hpP1 *= 2.50;
                hpP2 *= 2.50;
                shieldRegenMult *= 1.50;
                sandstormOffset += 10000;
            }
            if (rules.HasMutator(MutatorCatalog.DoubleTrigger))
            {
                bonusMulticast = Math.Min(2, bonusMulticast + 1);
            }
            if (rules.HasMutator(MutatorCatalog.CritOverload))
            {
                bonusCrit += 50;
                critOverride = 275;
            }
            if (rules.HasMutator(MutatorCatalog.FrozenWasteland))
            {
                freezeInterval = 4000;
                freezeDuration = 1000;
            }

            // 3b. P0-22：通用数据驱动消费自定义 Mod 突变词条（完全按 Rules/Parameters 标准键驱动，零硬编码词条 ID）
            if (rules.MutatorIds is { Count: > 0 })
            {
                foreach (var mid in rules.MutatorIds)
                {
                    if (string.IsNullOrWhiteSpace(mid) || MutatorCatalog.ById.ContainsKey(mid.Trim()))
                        continue;
                    var meta = MutatorCatalog.TryGet(mid, customMutators);
                    if (meta == null) continue;

                    if (MutatorCatalog.TryReadRuleDouble(meta, "cooldownReductionPct", out double cCdr))
                        cdrPct += Math.Clamp(cCdr, -0.5, 0.75);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "minCooldownFloorMs", out double cFloor) && cFloor > 0)
                        minCdFloor = Math.Min(minCdFloor, Math.Clamp((int)Math.Round(cFloor), 500, 1000));
                    if (MutatorCatalog.TryReadRuleDouble(meta, "healthMultiplier", out double cHp) && cHp > 0)
                    {
                        double clampedHp = Math.Clamp(cHp, 0.1, 10.0);
                        hpP1 *= clampedHp;
                        hpP2 *= clampedHp;
                    }
                    if (!isSymmetricHumanPvp && isPve
                        && MutatorCatalog.TryReadRuleDouble(meta, "monsterHealthMultiplier", out double cMhp) && cMhp > 0)
                    {
                        hpP2 *= Math.Clamp(cMhp, 0.25, 5.0);
                    }
                    if (MutatorCatalog.TryReadRuleDouble(meta, "damageAndDotMultiplier", out double cDmg) && cDmg > 0)
                        dmgDotMult *= Math.Clamp(cDmg, 0.1, 10.0);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "shieldAndRegenMultiplier", out double cSh) && cSh > 0)
                        shieldRegenMult *= Math.Clamp(cSh, 0.1, 10.0);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "bonusMulticast", out double cMc))
                        bonusMulticast = Math.Clamp(bonusMulticast + (int)Math.Round(cMc), 0, 2);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "bonusCritChance", out double cCrit))
                        bonusCrit = Math.Clamp(bonusCrit + (int)Math.Round(cCrit), -100, 200);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "critPercentMultiplier", out double cCritPct) && cCritPct >= 100)
                        critOverride = Math.Clamp((int)Math.Round(cCritPct), 100, 500);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "sandstormOffsetMs", out double cSsOff))
                        sandstormOffset += Math.Clamp((int)Math.Round(cSsOff), -25000, 60000);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "sandstormCountdownDeltaMs", out double cSsDelta))
                        sandstormOffset += Math.Clamp((int)Math.Round(cSsDelta), -25000, 60000);
                    if (MutatorCatalog.TryReadRuleDouble(meta, "freezeIntervalMs", out double cFrInt) && cFrInt >= 500
                        && MutatorCatalog.TryReadRuleDouble(meta, "freezeDurationMs", out double cFrDur) && cFrDur > 0)
                    {
                        freezeInterval = freezeInterval > 0 ? Math.Min(freezeInterval, (int)Math.Round(cFrInt)) : (int)Math.Round(cFrInt);
                        freezeDuration = Math.Max(freezeDuration, Math.Clamp((int)Math.Round(cFrDur), 100, 5000));
                    }

                    // 通用周期战斗效果解析（如 combatPeriodicTriggerAfterMs + combatPeriodicIntervalMs + combatBothApplyBurn/Poison/Shield/Heal/Damage/combatAllItemsChargeMsPerTick）
                    int bothBurn = MutatorCatalog.TryReadRuleDouble(meta, "combatBothApplyBurn", out double bb) ? Math.Clamp((int)Math.Round(bb), 0, 500) : 0;
                    int bothPoison = MutatorCatalog.TryReadRuleDouble(meta, "combatBothApplyPoison", out double bp) ? Math.Clamp((int)Math.Round(bp), 0, 500) : 0;
                    int bothShield = MutatorCatalog.TryReadRuleDouble(meta, "combatBothApplyShield", out double bs) ? Math.Clamp((int)Math.Round(bs), 0, 1000) : 0;
                    int bothHeal = MutatorCatalog.TryReadRuleDouble(meta, "combatBothApplyHeal", out double bh) ? Math.Clamp((int)Math.Round(bh), 0, 1000) : 0;
                    int bothDamage = MutatorCatalog.TryReadRuleDouble(meta, "combatBothApplyDamage", out double bd) ? Math.Clamp((int)Math.Round(bd), 0, 500) : 0;
                    int chargeAllMs = MutatorCatalog.TryReadRuleDouble(meta, "combatAllItemsChargeMsPerTick", out double bc) ? Math.Clamp((int)Math.Round(bc), 0, 5000) : 0;

                    if (bothBurn > 0 || bothPoison > 0 || bothShield > 0 || bothHeal > 0 || bothDamage > 0 || chargeAllMs > 0)
                    {
                        int triggerAfter = MutatorCatalog.TryReadRuleDouble(meta, "combatPeriodicTriggerAfterMs", out double ta)
                            ? Math.Clamp((int)Math.Round(ta), 0, 120000)
                            : 0;
                        int interval = MutatorCatalog.TryReadRuleDouble(meta, "combatPeriodicIntervalMs", out double iv)
                            ? Math.Clamp((int)Math.Round(iv), 250, 60000)
                            : 1000;

                        periodicEffects ??= new List<PeriodicCombatMutatorEffect>();
                        periodicEffects.Add(new PeriodicCombatMutatorEffect
                        {
                            SourceMutatorId = meta.Id,
                            TriggerAfterMs = triggerAfter,
                            IntervalMs = interval,
                            BothApplyBurn = bothBurn,
                            BothApplyPoison = bothPoison,
                            BothApplyShield = bothShield,
                            BothApplyHeal = bothHeal,
                            BothApplyDamage = bothDamage,
                            AllItemsChargeMsPerTick = chargeAllMs
                        });
                    }
                }
            }
        }

        // 4. Boss 车轮战 PvE 波次缩放（同步真人 PvP 双保险跳过）
        bool effectiveBossRush = isBossRush || rules?.SpecialModeId == ESpecialModeId.BossRush;
        if (!isSymmetricHumanPvp && effectiveBossRush && isPve)
        {
            int wave = bossRushWave > 0 ? bossRushWave : Math.Max(1, day);
            var cfg = BossRushCatalog.GetWave(wave);
            hpP2 *= cfg.HealthMultiplier;
            offP2 *= cfg.OffenseMultiplier;
        }

        return new CombatModifiers
        {
            CooldownReductionPct = Math.Clamp(cdrPct, 0.0, 0.80),
            MinCooldownFloorMs = Math.Clamp(minCdFloor, 200, 1000),
            DamageAndDotMultiplier = Math.Clamp(dmgDotMult, 0.1, 20.0),
            HealthMultiplierP1 = Math.Clamp(hpP1, 0.1, 20.0),
            HealthMultiplierP2 = Math.Clamp(hpP2, 0.1, EndlessTowerScaler.MaxHealthMultiplier * 2.0),
            EnemyHealthCap = EndlessTowerScaler.MaxEnemyHealthCap,
            OffenseMultiplierP2 = Math.Clamp(offP2, 0.1, EndlessTowerScaler.MaxOffenseMultiplier * 2.0),
            CooldownReductionP2Pct = Math.Clamp(cdrP2, 0.0, 0.70),
            BonusMulticastAll = Math.Clamp(bonusMulticast, 0, 2),
            BonusCritChance = Math.Clamp(bonusCrit, -100, 200),
            CritPercentMultiplierOverride = critOverride,
            ShieldAndRegenMultiplier = Math.Clamp(shieldRegenMult, 0.1, 10.0),
            SandstormStartOffsetMs = sandstormOffset,
            SandstormEnabled = true,
            PeriodicFreezeIntervalMs = freezeInterval,
            PeriodicFreezeDurationMs = freezeDuration,
            PeriodicEffects = periodicEffects != null ? periodicEffects.ToArray() : Array.Empty<PeriodicCombatMutatorEffect>()
        };
    }
}

using System.Globalization;
using System.Text.Json.Serialization;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Combat;

public enum CombatEventType
{
    FightStarted,
    TickSnapshot,
    CardFired,
    DamageApplied,
    ShieldApplied,
    BurnApplied,
    /// <summary>灼烧层数被移除/净化（P2-20）。</summary>
    BurnRemoved,
    BurnTick,
    PoisonApplied,
    /// <summary>剧毒层数被移除/净化（P2-20）。</summary>
    PoisonRemoved,
    PoisonTick,
    HealApplied,
    /// <summary>施加再生层数（P0-09 与 HealApplied 彻底解耦）。</summary>
    RegenApplied,
    RegenTick,
    HasteApplied,
    SlowApplied,
    FreezeApplied,
    ChargeApplied,
    FlyingChanged,
    CardAttributeModified,
    /// <summary>战内卡牌某非基模动态属性键或被移除附魔的独有属性键彻底消失（P1-11）。</summary>
    CardAttributeDeleted,
    CardDestroyed,
    PlayerEnraged,
    PlayerEnrageEnded,
    PlayerRageApplied,
    PlayerJoyApplied,
    SandstormWarning,
    SandstormTick,
    FightEnded,
    QuestUpdated,
    QuestCompleted,
    CardTransformed,
    /// <summary>战内限时变形到期还原（P1-10）。</summary>
    CardTransformReverted,
    CardEnchanted,
    CardUpgraded,
    CardReloaded,
    PlayerTempoApplied,
    PlayerTempoRemoved,
    ActionCostSpent,
    /// <summary>战内光环目标集增量生效/撤销（P1-09）。</summary>
    AuraExecuted,
    /// <summary>战内玩家属性被修改（如 TActionPlayerModifyAttribute 修改 Gold / Income / HealthMax 等，P1-07）。</summary>
    PlayerAttributeModified
}

/// <summary>
/// 战斗回放事件帧：既供单元测试断言，也直接驱动表现层 1:1 动画、弹道、技能脉冲与特效。
/// </summary>
public sealed class CombatEvent
{
    public int TimeMs { get; init; }
    public CombatEventType Type { get; init; }
    public string? SourcePlayerId { get; init; }
    public string? TargetPlayerId { get; init; }
    public string? SourceCardInstanceId { get; init; }
    /// <summary>引起本次反应/动作执行的真实触发源卡牌实例 ID（P1-08，主动施放时等于自身，被动反应时指向触发它的源卡）。</summary>
    public string? TriggerSourceCardInstanceId { get; init; }
    public string? TargetCardInstanceId { get; init; }
    public double Value { get; init; }
    /// <summary>本次伤害事件中被护盾吸收的精确数值（P0-10 BurnTick / 伤害拆盾权威依据）。</summary>
    public int ShieldAbsorbed { get; init; }
    /// <summary>本次伤害事件中穿透或溢出护盾后作用于生命值的精确伤害（含致命溢出，满足 ShieldAbsorbed + HpDamage == 实际结算伤害）。</summary>
    public int HpDamage { get; init; }
    /// <summary>本次治疗/吸血/再生跳血实际净恢复的生命值（P1-15，满血零回血为 0；为 null 表示旧合成事件，由转换器按实时血量钳制）。</summary>
    public int? EffectiveHpRestored { get; init; }
    public bool IsCrit { get; init; }
    public string? Detail { get; init; }
    public string? AbilityId { get; init; }
    public string? ActionType { get; init; }
    public bool IsSkillSource { get; init; }
    /// <summary>同一触发/施放链共享的执行上下文 ID（P2-10，用于协议层按因果链共享 ExecutionContextId）。</summary>
    public string? ExecutionContextId { get; set; }
    /// <summary>标识本次附魔变更是否为撤回/过期还原（P2-10，透传至 CombatSimEventCardEnchanted.IsReverted）。</summary>
    public bool IsReverted { get; init; }
    /// <summary>可选白名单官方 VFXOverrideKey 覆盖（供自定义机制复用官方弹道/爆炸等视觉演出）。</summary>
    public string? VfxKeyOverride { get; init; }

    // P1-09 光环生效/撤销目标集合（仅 AuraExecuted 事件填充，其他事件保持 null 零分配）
    public IReadOnlyList<string>? AuraAppliedCardIds { get; init; }
    public IReadOnlyList<string>? AuraRemovedCardIds { get; init; }
    public IReadOnlyList<string>? AuraAppliedPlayerIds { get; init; }
    public IReadOnlyList<string>? AuraRemovedPlayerIds { get; init; }

    // P1-10 战内限时变形还原时被移除的临时产物实例 ID 列表（仅 CardTransformReverted 填充）
    public IReadOnlyList<string>? RevertedTransformedInstanceIds { get; init; }

    // D1-P2-09 (#124-1)：每跳仅发 1 条 SandstormTick，同时携带双边真实减伤后拆账字段供转换器过滤 0 伤与 FlipPerspective 互换
    public int? P1SandstormDamage { get; init; }
    public int P1SandstormShieldAbsorbed { get; init; }
    public int P1SandstormHpDamage { get; init; }
    public int? P2SandstormDamage { get; init; }
    public int P2SandstormShieldAbsorbed { get; init; }
    public int P2SandstormHpDamage { get; init; }

    // 供 UI 同步显示的快照（可选）
    public int Player1Health { get; init; }
    public int Player1Shield { get; init; }
    public int Player1Burn { get; init; }
    public int Player1Poison { get; init; }
    public int Player2Health { get; init; }
    public int Player2Shield { get; init; }
    public int Player2Burn { get; init; }
    public int Player2Poison { get; init; }

    /// <summary>
    /// 构造 <see cref="CombatEventType.PlayerAttributeModified"/> 的结构化 <see cref="Detail"/> 载荷（格式：属性名|变化前值|变化后值）。
    /// 复用 <see cref="Detail"/> 原生透传字段，确保经 CombatSimulator.Emit 复制与序列化后完整保留变化前后值。
    /// </summary>
    public static string FormatPlayerAttributeModifiedDetail(string attributeName, double previousValue, double currentValue)
        => string.Concat(
            attributeName,
            "|",
            previousValue.ToString("G17", CultureInfo.InvariantCulture),
            "|",
            currentValue.ToString("G17", CultureInfo.InvariantCulture));

    /// <summary>
    /// 解析 <see cref="CombatEventType.PlayerAttributeModified"/> 的结构化三元组（属性名、变化前值、变化后值）。
    /// </summary>
    public bool TryGetPlayerAttributeModification(out string attributeName, out double previousValue, out double currentValue)
    {
        attributeName = string.Empty;
        previousValue = 0;
        currentValue = 0;
        if (Type != CombatEventType.PlayerAttributeModified || string.IsNullOrWhiteSpace(Detail))
            return false;

        int firstSep = Detail.IndexOf('|');
        if (firstSep <= 0)
            return false;
        int secondSep = Detail.IndexOf('|', firstSep + 1);
        if (secondSep <= firstSep + 1 || secondSep >= Detail.Length - 1)
            return false;

        var attrSpan = Detail.AsSpan(0, firstSep).Trim();
        var prevSpan = Detail.AsSpan(firstSep + 1, secondSep - firstSep - 1);
        var curSpan = Detail.AsSpan(secondSep + 1);
        if (attrSpan.IsEmpty)
            return false;
        if (!double.TryParse(prevSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out previousValue)
            || !double.IsFinite(previousValue))
            return false;
        if (!double.TryParse(curSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out currentValue)
            || !double.IsFinite(currentValue))
            return false;

        attributeName = attrSpan.ToString();
        return true;
    }

    /// <summary>玩家属性修改事件的属性名（兼容「属性名|前值|后值」与纯「属性名」两种 Detail 形态）。</summary>
    [JsonIgnore]
    public string? PlayerAttributeName
    {
        get
        {
            if (Type != CombatEventType.PlayerAttributeModified || string.IsNullOrWhiteSpace(Detail))
                return null;
            int sep = Detail.IndexOf('|');
            var raw = (sep >= 0 ? Detail[..sep] : Detail).Trim();
            return raw.Length > 0 ? raw : null;
        }
    }

    /// <summary>玩家属性修改事件的变化前值（不可解析时返回 null）。</summary>
    [JsonIgnore]
    public double? PlayerAttributePreviousValue
        => TryGetPlayerAttributeModification(out _, out var prev, out _) ? prev : null;

    /// <summary>玩家属性修改事件的变化后值（不可解析时返回 null）。</summary>
    [JsonIgnore]
    public double? PlayerAttributeCurrentValue
        => TryGetPlayerAttributeModification(out _, out _, out var cur) ? cur : null;
}

public sealed class CardCooldownFrame
{
    public string InstanceId { get; init; } = string.Empty;
    public float Progress01 { get; init; }
    public bool IsHasted { get; init; }
    public bool IsSlowed { get; init; }
    public bool IsFrozen { get; init; }
    public bool IsFlying { get; init; }
    public bool IsDestroyed { get; init; }
    public int Ammo { get; init; }
    public int HasteMs { get; init; }
    public int SlowMs { get; init; }
    public int FreezeMs { get; init; }
    public bool Heated { get; init; }
    public bool Chilled { get; init; }
    public int AmmoMax { get; init; }
    public int CooldownMaxMs { get; init; }
    public int DamageAmount { get; init; }
    public int ShieldApplyAmount { get; init; }
    public int BurnApplyAmount { get; init; }
    public int PoisonApplyAmount { get; init; }
    public int HealAmount { get; init; }
    public int RegenApplyAmount { get; init; }
    public int CritChance { get; init; }
    public int Multicast { get; init; }
    public int Lifesteal { get; init; }
    public int SellPrice { get; init; }
    public int Counter { get; init; }
    /// <summary>卡牌实时标签集合（P1-12，按 AttributeVersion 复用不可变快照，仅变化时由转换器下发）。</summary>
    public IReadOnlyList<string>? Tags { get; init; }
    /// <summary>卡牌实时隐藏标签集合（P1-12）。</summary>
    public IReadOnlyList<string>? HiddenTags { get; init; }
    /// <summary>卡牌实时所属英雄集合（P1-12）。</summary>
    public IReadOnlyList<string>? Heroes { get; init; }
    /// <summary>Custom_0..Custom_9 动态属性值快照（按需稀疏分配，原版无自定义属性卡牌保持 null 零开销）。</summary>
    public int[]? CustomAttrs { get; init; }
}

public sealed class CombatTickState
{
    public int TimeMs { get; init; }
    public int P1Hp { get; init; }
    public int P1MaxHp { get; init; }
    public int P1Shield { get; init; }
    public int P1Burn { get; init; }
    public int P1Poison { get; init; }
    public int P1Regen { get; init; }
    public int P1Rage { get; init; }
    public int P1RageMax { get; init; }
    public bool P1Enraged { get; init; }
    public int P1EnragedDuration { get; init; }
    public int P1EnragedDurationMax { get; init; }
    public int P1Tempo { get; init; }
    public int P1TempoGainCooldownMax { get; init; }
    public int P1Joy { get; init; }
    public int P1PercentDamageReduction { get; init; }
    public int P1PortraitIndex { get; init; }

    public int P2Hp { get; init; }
    public int P2MaxHp { get; init; }
    public int P2Shield { get; init; }
    public int P2Burn { get; init; }
    public int P2Poison { get; init; }
    public int P2Regen { get; init; }
    public int P2Rage { get; init; }
    public int P2RageMax { get; init; }
    public bool P2Enraged { get; init; }
    public int P2EnragedDuration { get; init; }
    public int P2EnragedDurationMax { get; init; }
    public int P2Tempo { get; init; }
    public int P2TempoGainCooldownMax { get; init; }
    public int P2Joy { get; init; }
    public int P2PercentDamageReduction { get; init; }
    public int P2PortraitIndex { get; init; }

    public bool SandstormActive { get; init; }
    public int SandstormNextDamage { get; init; }
    public List<CardCooldownFrame> Cards { get; init; } = new();
}

/// <summary>
/// 战斗期间发生的 UntilEndOfCombat 临时变形记录（P1-10），供战后 RunEngine 发射 GameSimEventCardTransformReverted 还原客户端视图。
/// </summary>
public sealed class CombatTemporaryTransformRecord
{
    public string OwnerPlayerId { get; init; } = string.Empty;
    public string OriginalCardInstanceId { get; init; } = string.Empty;
    public string OriginalTemplateId { get; init; } = string.Empty;
    public string OriginalCardType { get; init; } = "Item";
    public string OriginalSection { get; init; } = "Hand";
    public int OriginalSlot { get; init; }
    public List<string> TransformedInstanceIds { get; init; } = new();
}

/// <summary>单张卡牌战后永久同步变更明细。</summary>
public sealed class CardPermanentSyncDelta
{
    public string InstanceId { get; init; } = string.Empty;
    public CardInstance SourceCard { get; init; } = null!;
    public Dictionary<string, double> PermanentAddChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PermanentMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>P0-04：战中写入的 UntilEndOfDay 属性差量。</summary>
    public Dictionary<string, double> DayAddChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> DayMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>P0-04：战中写入的 UntilEndOfHour 属性差量。</summary>
    public Dictionary<string, double> HourAddChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> HourMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? NewEnchantment { get; init; }
    public bool EnchantmentChanged { get; init; }
    public string? NewDayEnchantment { get; init; }
    public bool DayEnchantmentChanged { get; init; }
    public string? NewHourEnchantment { get; init; }
    public bool HourEnchantmentChanged { get; init; }
    public CardTier? NewTier { get; init; }
    public bool TierChanged { get; init; }
    public List<string> AddedExtraTags { get; init; } = new();
    public Dictionary<string, double> UpdatedQuestProgress { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> NewlyCompletedQuests { get; init; } = new();
    public string? TransformReplacementTemplateId { get; init; }
    public CardInstance? TransformedReplacementCard { get; init; }
    /// <summary>多产物永久变形时产生的第 2..N 张附加替换卡（保留第 1 张在 TransformedReplacementCard 以 100% 兼容旧消费端）。</summary>
    public List<CardInstance> AdditionalTransformedReplacements { get; init; } = new();
}

/// <summary>战后永久状态同步结果（供自动或显式 SyncPermanentChangesBack 消费）。</summary>
public sealed class PostCombatPermanentSyncResult
{
    public bool Applied { get; set; }
    public List<CardPermanentSyncDelta> UpdatedCards { get; init; } = new();
    public List<CardInstance> SpawnedCards { get; init; } = new();
    public Dictionary<string, double> PlayerAttributeDeltas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>P0-04：战中玩家层 UntilEndOfDay / UntilEndOfHour 非瞬态属性同步差量。</summary>
    public Dictionary<string, double> PlayerDayAddDeltas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayAdditiveMultDeltas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerDayPureMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourAddDeltas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourAdditiveMultDeltas { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PlayerHourPureMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>按原始卡牌 InstanceId 索引的完整永久变形产物列表（支持一对多永久变形，修复 P1-3）。</summary>
    public Dictionary<string, List<CardInstance>> PermanentReplacements { get; init; } = new(StringComparer.Ordinal);
    public bool HasAnyChanges =>
        UpdatedCards.Count > 0
        || SpawnedCards.Count > 0
        || PlayerAttributeDeltas.Count > 0
        || PlayerDayAddDeltas.Count > 0
        || PlayerDayAdditiveMultDeltas.Count > 0
        || PlayerDayPureMultChanges.Count > 0
        || PlayerHourAddDeltas.Count > 0
        || PlayerHourAdditiveMultDeltas.Count > 0
        || PlayerHourPureMultChanges.Count > 0
        || PermanentReplacements.Count > 0;
}

public sealed class CombatResult
{
    public string WinnerPlayerId { get; init; } = string.Empty;
    public string LoserPlayerId { get; init; } = string.Empty;
    public string Player1Hero { get; init; } = string.Empty;
    public string Player2Hero { get; init; } = string.Empty;
    public bool IsDraw { get; init; }
    /// <summary>双方同帧倒地（同归于尽）标记（P1-55，供同步真人 PvP / 锦标赛公平平局裁决；异步 Ghost PvP 保持官方判玩家胜）。</summary>
    public bool IsSimultaneousDeath { get; init; }
    /// <summary>战斗达到 MaxDurationMs 上限仍双双存活的超时标记（P1-55）。</summary>
    public bool IsTimeout { get; init; }
    public int DurationMs { get; init; }
    public int Player1RemainingHp { get; init; }
    public int Player2RemainingHp { get; init; }
    public double Player1TotalDamageDealt { get; init; }
    public double Player2TotalDamageDealt { get; init; }
    public double Player1TotalShieldGained { get; init; }
    public double Player2TotalShieldGained { get; init; }
    public List<CombatEvent> Events { get; init; } = new();
    public List<CombatTickState> Timeline { get; init; } = new();
    public PostCombatPermanentSyncResult? PostCombatPermanentSync { get; set; }
    /// <summary>P0-40：同步真人 PvP 规范模拟下 canonicalP2 侧专用永久同步结果槽（绝不与 P1 专槽互串早退）。</summary>
    public PostCombatPermanentSyncResult? PostCombatPermanentSyncP2 { get; set; }
    /// <summary>本场战斗中发生的 UntilEndOfCombat 临时变形清单（P1-10，供战后 RunEngine 还原客户端变形视图）。</summary>
    public List<CombatTemporaryTransformRecord> TemporaryTransforms { get; init; } = new();
    /// <summary>V14 自定义机制经由受控门面登记的战后结转操作（由 RunEngine 统一校验并同步给客户端）。</summary>
    public List<BazaarCore.Mods.PostCombatMutationOp> PostCombatOps { get; init; } = new();
    /// <summary>V14 自定义机制运行时安全诊断与熔断日志。</summary>
    public List<BazaarCore.Mods.MechanicDiagnostic> MechanicDiagnostics { get; init; } = new();
    public int MechanicCircuitBreakerTrips { get; init; }
    public int MechanicBudgetViolations { get; init; }
    internal PlayerState? SimPlayer1 { get; init; }
    /// <summary>P0-40：同步真人 PvP 规范模拟下保存的模拟态 P2，用于 canonicalP2 无损同步战后永久/当日/本小时变更。</summary>
    internal PlayerState? SimPlayer2 { get; init; }
}

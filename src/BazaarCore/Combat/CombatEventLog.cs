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
    BurnTick,
    PoisonApplied,
    PoisonTick,
    HealApplied,
    RegenTick,
    HasteApplied,
    SlowApplied,
    FreezeApplied,
    ChargeApplied,
    FlyingChanged,
    CardAttributeModified,
    CardDestroyed,
    PlayerEnraged,
    PlayerEnrageEnded,
    SandstormWarning,
    SandstormTick,
    FightEnded,
    QuestUpdated,
    QuestCompleted,
    CardTransformed,
    CardEnchanted,
    CardUpgraded,
    CardReloaded,
    PlayerTempoApplied,
    ActionCostSpent
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
    public string? TargetCardInstanceId { get; init; }
    public double Value { get; init; }
    public bool IsCrit { get; init; }
    public string? Detail { get; init; }
    public string? AbilityId { get; init; }
    public string? ActionType { get; init; }
    public bool IsSkillSource { get; init; }

    // 供 UI 同步显示的快照（可选）
    public int Player1Health { get; init; }
    public int Player1Shield { get; init; }
    public int Player1Burn { get; init; }
    public int Player1Poison { get; init; }
    public int Player2Health { get; init; }
    public int Player2Shield { get; init; }
    public int Player2Burn { get; init; }
    public int Player2Poison { get; init; }
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
    public bool P1Enraged { get; init; }
    public int P1Tempo { get; init; }

    public int P2Hp { get; init; }
    public int P2MaxHp { get; init; }
    public int P2Shield { get; init; }
    public int P2Burn { get; init; }
    public int P2Poison { get; init; }
    public int P2Regen { get; init; }
    public int P2Rage { get; init; }
    public bool P2Enraged { get; init; }
    public int P2Tempo { get; init; }

    public bool SandstormActive { get; init; }
    public int SandstormNextDamage { get; init; }
    public List<CardCooldownFrame> Cards { get; init; } = new();
}

/// <summary>单张卡牌战后永久同步变更明细。</summary>
public sealed class CardPermanentSyncDelta
{
    public string InstanceId { get; init; } = string.Empty;
    public CardInstance SourceCard { get; init; } = null!;
    public Dictionary<string, double> PermanentAddChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> PermanentMultChanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string? NewEnchantment { get; init; }
    public bool EnchantmentChanged { get; init; }
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
    /// <summary>按原始卡牌 InstanceId 索引的完整永久变形产物列表（支持一对多永久变形，修复 P1-3）。</summary>
    public Dictionary<string, List<CardInstance>> PermanentReplacements { get; init; } = new(StringComparer.Ordinal);
    public bool HasAnyChanges =>
        UpdatedCards.Count > 0 || SpawnedCards.Count > 0 || PlayerAttributeDeltas.Count > 0 || PermanentReplacements.Count > 0;
}

public sealed class CombatResult
{
    public string WinnerPlayerId { get; init; } = string.Empty;
    public string LoserPlayerId { get; init; } = string.Empty;
    public bool IsDraw { get; init; }
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
    internal PlayerState? SimPlayer1 { get; init; }
}

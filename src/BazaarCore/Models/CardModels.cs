using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 统一承载 GameData.db 中 cards 表的全部 9 种卡牌模板。
/// </summary>
public sealed class CardTemplate
{
    [JsonPropertyName("$type")]
    public string TypeDiscriminator { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string? InternalDescription { get; set; }
    public string StartingTier { get; set; } = "Bronze";
    public string Size { get; set; } = "Small";
    public List<string> Heroes { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> HiddenTags { get; set; } = new();
    public string? ArtKey { get; set; }
    public string? SpawningEligibility { get; set; }
    public string? TranslationKey { get; set; }
    public string? AudioKey { get; set; }

    public LocalizationBlock? Localization { get; set; }
    public Dictionary<string, TierData>? Tiers { get; set; }
    public Dictionary<string, EnchantmentData>? Enchantments { get; set; }
    public Dictionary<string, AbilityDefinition> Abilities { get; set; } = new();
    public Dictionary<string, AuraDefinition> Auras { get; set; } = new();
    public List<QuestDefinition>? Quests { get; set; }
    public TransformDefinition? Transform { get; set; }
    public Dictionary<string, double>? Attributes { get; set; }

    // 遭遇卡/事件卡/野怪战/基座专有字段
    public SelectionContextData? SelectionContext { get; set; }
    public RuleNode? SelectionRequirements { get; set; }
    public RuleNode? SelectionCriteria { get; set; }
    public List<NextEncounterOption>? NextEncounterOnSelection { get; set; }
    public string? NewSetIdOnExit { get; set; }
    public bool? IsSingleSpawn { get; set; }
    public bool? IsTakeover { get; set; }
    public bool? ShouldProgressHour { get; set; }
    public bool? IsReselectable { get; set; }
    public double? SpawningChance { get; set; }
    public double? SpawningChanceIncrement { get; set; }
    public int? ExperienceAwardUponSelection { get; set; }

    // 战斗遭遇专有字段
    public RuleNode? CombatantType { get; set; }
    public bool? SandstormEnabled { get; set; }
    public int? RewardCombatGold { get; set; }
    public int? RewardCombatXp { get; set; }
    public CombatRewardData? RewardVictory { get; set; }
    public CombatRewardData? RewardDefeat { get; set; }

    // 基座遭遇专有字段
    public RuleNode? Behavior { get; set; }

    // 音乐盒/插槽效果专有字段
    public System.Text.Json.JsonElement? MusicNote { get; set; }

    public string DisplayTitle => Localization?.Title?.Text ?? InternalName;

    public CardSize CardSize => TierHelper.ParseSize(Size);
    public CardTier DefaultTier => TierHelper.ParseTier(StartingTier);

    [JsonIgnore]
    public bool IsAlwaysSpawnable => string.Equals(SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsGuidOnlySpawnable => string.Equals(SpawningEligibility, "GuidOnly", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsNeverSpawnable => string.Equals(SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase);
}

public sealed class LocalizationBlock
{
    public LocalizedText? Title { get; set; }
    public LocalizedText? Description { get; set; }
    public LocalizedText? FlavorText { get; set; }
    public List<TooltipEntry>? Tooltips { get; set; }
}

public sealed class LocalizedText
{
    public string? Key { get; set; }
    public string? Text { get; set; }
}

public sealed class TooltipEntry
{
    public LocalizedText? Content { get; set; }
    public string? TooltipType { get; set; }
    public RuleNode? TooltipCondition { get; set; }
    public List<RuleNode>? Prerequisites { get; set; }
}

public sealed class TierData
{
    public Dictionary<string, double> Attributes { get; set; } = new();
    public List<string> AbilityIds { get; set; } = new();
    public List<string> AuraIds { get; set; } = new();
    public List<int> TooltipIds { get; set; } = new();
}

public sealed class EnchantmentData
{
    public Dictionary<string, double> Attributes { get; set; } = new();
    public Dictionary<string, AbilityDefinition> Abilities { get; set; } = new();
    public Dictionary<string, AuraDefinition> Auras { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public List<string> HiddenTags { get; set; } = new();
    public LocalizationBlock? Localization { get; set; }
    public bool HasAbilities { get; set; }
    public bool HasAuras { get; set; }
}

public sealed class VfxConfigData
{
    public string? VFXOverrideKey { get; set; }
    public bool VFXShouldPlay { get; set; } = true;
    public bool VFXIsTakeover { get; set; }
}

public sealed class AbilityDefinition
{
    public string Id { get; set; } = string.Empty;
    public RuleNode? Trigger { get; set; }
    public string? ActiveIn { get; set; }
    public string? WorksIn { get; set; }
    public RuleNode? Action { get; set; }
    public List<RuleNode>? Prerequisites { get; set; }
    public string? Priority { get; set; }
    public string? InternalName { get; set; }
    public string? InternalDescription { get; set; }
    public string? TranslationKey { get; set; }
    public VfxConfigData? VFXConfig { get; set; }
}

public sealed class AuraDefinition
{
    public string Id { get; set; } = string.Empty;
    public string? ActiveIn { get; set; }
    public string? WorksIn { get; set; }
    public RuleNode? Action { get; set; }
    public List<RuleNode>? Prerequisites { get; set; }
    public string? InternalName { get; set; }
    public string? InternalDescription { get; set; }
    public string? TranslationKey { get; set; }
}

public sealed class QuestDefinition
{
    public List<RuleNode>? Prerequisites { get; set; }
    public QuestRepeatableData? Repeatable { get; set; }
    public List<QuestEntry> Entries { get; set; } = new();
}

public sealed class QuestRepeatableData
{
    public List<RuleNode>? Prerequisites { get; set; }
}

public sealed class QuestEntry
{
    public string AttributeType { get; set; } = "Quest_1";
    public List<RuleNode>? Prerequisites { get; set; }
    public RuleNode? Trigger { get; set; }
    public double Target { get; set; }
    public QuestUpdate? Update { get; set; }
    public QuestReward? Reward { get; set; }
    /// <summary>任务首次达标时一次性执行的效果能力映射（如 Recycling Truck、Jabalian Totem）。</summary>
    public Dictionary<string, AbilityDefinition>? CompletionEffects { get; set; }
}

public sealed class QuestUpdate
{
    public RuleNode? Value { get; set; }
    public RuleNode? Duration { get; set; }
}

public sealed class QuestReward
{
    public Dictionary<string, TierData>? Tiers { get; set; }
    public Dictionary<string, double>? Attributes { get; set; }
    public Dictionary<string, AbilityDefinition>? Abilities { get; set; }
    public Dictionary<string, AuraDefinition>? Auras { get; set; }
    /// <summary>任务完成后永久授予该卡的显式标签（如 Weapon/Tool/Food 等）。</summary>
    public List<string>? Tags { get; set; }
    /// <summary>任务完成后永久授予该卡的隐藏标签。</summary>
    public List<string>? HiddenTags { get; set; }
    public LocalizationBlock? Localization { get; set; }
}

public sealed class TransformDefinition
{
    public Dictionary<string, AbilityDefinition>? Abilities { get; set; }
    public Dictionary<string, AuraDefinition>? Auras { get; set; }
    public RuleNode? SpawnContext { get; set; }
}

public sealed class SelectionContextData
{
    public RuleNode? SpawnContext { get; set; }
    public SelectionRulesData? Rules { get; set; }
}

public sealed class SelectionRulesData
{
    public bool CanSelectMultiple { get; set; }
    public bool SelectionIsFree { get; set; } = true;
    public bool CanExit { get; set; } = true;
    public RerollRulesData? RerollRules { get; set; }
    public bool WillAutoSellOnExit { get; set; }
    public System.Text.Json.JsonElement? NextEncounterOnExit { get; set; }
    public int? MaxSelections { get; set; }

    /// <summary>返回显式配置或由 CanSelectMultiple 推导出的最大选择次数（无限多选返回 null）。</summary>
    public int? GetEffectiveMaxSelections()
    {
        if (MaxSelections.HasValue)
            return Math.Max(1, MaxSelections.Value);
        return CanSelectMultiple ? null : 1;
    }
}

public sealed class NextEncounterOption
{
    public string Id { get; set; } = string.Empty;
    public double Weight { get; set; } = 1.0;
    public List<RuleNode>? Prerequisites { get; set; }
}

public sealed class RerollRulesData
{
    public int? CostIncrease { get; set; }
    public int? CostMax { get; set; }
    public bool ResetCostOnExit { get; set; }
    public int? StartingCost { get; set; } = 2;
    public int? MaxRerolls { get; set; }
    public int? TotalAllowedRerolls { get; set; }
    public bool? RerollOnPurchase { get; set; }

    /// <summary>解析允许的最大刷新次数；null 表示无限刷新（如 Pearl's Dig Site）。</summary>
    public int? GetEffectiveAllowedRerolls()
    {
        if (TotalAllowedRerolls.HasValue)
            return Math.Max(0, TotalAllowedRerolls.Value);
        if (MaxRerolls.HasValue)
            return Math.Max(0, MaxRerolls.Value);
        return null;
    }

    /// <summary>解析每次刷新费用递增步长（DB 为 null 时按 0 处理）。</summary>
    public int GetEffectiveCostIncrease() => Math.Max(0, CostIncrease ?? 0);

    /// <summary>解析初始刷新费用。</summary>
    public int GetEffectiveStartingCost() => Math.Max(0, StartingCost ?? 2);
}

public sealed class CombatRewardData
{
    public SelectionRulesData? SelectionContextRules { get; set; }
    public RuleNode? SpawnContext { get; set; }
    public int? GoldReward { get; set; }
    public int? ExperienceReward { get; set; }
}

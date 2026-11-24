using System.Text.Json;
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
    /// <summary>
    /// 遭遇/步骤选择前置条件（P2-06：通过自定义 JsonConverter 同时兼容官方 JSON 数组 List&lt;TSelectionRequirement&gt; 与历史单对象格式，保持既有单对象访问无缝兼容）。
    /// </summary>
    [JsonConverter(typeof(SelectionRequirementsRuleNodeConverter))]
    public RuleNode? SelectionRequirements { get; set; }
    public RuleNode? SelectionCriteria { get; set; }
    public List<NextEncounterOption>? NextEncounterOnSelection { get; set; }
    public string? NewSetIdOnExit { get; set; }
    public string? BoardOverride { get; set; }
    public string? Presentation { get; set; }
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

    /// <summary>V14 开放自定义沙盒机制挂载列表（由 Mod 卡牌或补丁声明，纯原版卡恒为 null）。</summary>
    public List<BazaarCore.Mods.CardMechanicAttachment>? CustomMechanics { get; set; }

    /// <summary>Mod 自定义遭遇相对抽签权重（纯原版卡恒为 null；&gt;0 时按倍率作用于白昼遭遇抽签，&lt;=0 转为 GuidOnly）。</summary>
    public double? ModSpawnWeight { get; set; }
    /// <summary>Mod 自定义遭遇最早允许出现天数（纯原版卡恒为 null）。</summary>
    public int? ModMinDay { get; set; }
    /// <summary>Mod 自定义遭遇最晚允许出现天数（纯原版卡恒为 null）。</summary>
    public int? ModMaxDay { get; set; }

    /// <summary>P0-23/P0-25：是否为 Mod 新建自定义卡牌（原版卡即便被 Patch 仍恒为 false）。</summary>
    [JsonIgnore]
    public bool IsCustomModCard { get; set; }

    /// <summary>P0-23：是否为通用跨英雄自定义技能（Type=="Skill" 且 IsCustomModCard 且未限定特定英雄）。</summary>
    [JsonIgnore]
    public bool IsUniversalCustomSkill { get; set; }

    /// <summary>新建自定义卡的归属 Mod ID（原版卡恒为 null）。</summary>
    [JsonIgnore]
    public string? CustomModOrigin { get; set; }

    /// <summary>P0-24：透传自 CustomEventDefinition.Schedule.Hours（null/空表示全时段允许）。</summary>
    [JsonIgnore]
    public List<int>? AllowedHours { get; set; }

    /// <summary>P0-21：透传自自定义野怪遭遇的难度槽位（Bronze / Silver / Gold / Any）。</summary>
    [JsonIgnore]
    public string? ModDifficultySlot { get; set; }

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

public sealed class VfxConfigData : IJsonOnDeserialized
{
    public string? VFXOverrideKey { get; set; }
    public bool VFXShouldPlay { get; set; } = true;
    public bool VFXIsTakeover { get; set; }

    // P0-19：不建同名不同大小写的碰撞属性（避免 PropertyNameCaseInsensitive=true 加载 GameData.db 时抛 InvalidOperationException），
    // 通过 JsonExtensionData + IJsonOnDeserialized 仅在大小写敏感反序列化器下兜底吸收 camelCase 的 vfxOverrideKey / vfxShouldPlay / vfxIsTakeover。
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? CompatExtras { get; set; }

    public void OnDeserialized()
    {
        if (CompatExtras == null || CompatExtras.Count == 0)
            return;

        foreach (var (k, v) in CompatExtras)
        {
            if (string.IsNullOrWhiteSpace(VFXOverrideKey)
                && k.Equals("vfxOverrideKey", StringComparison.OrdinalIgnoreCase)
                && v.ValueKind == JsonValueKind.String)
            {
                VFXOverrideKey = v.GetString();
            }
            else if (k.Equals("vfxShouldPlay", StringComparison.OrdinalIgnoreCase)
                && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                VFXShouldPlay = v.GetBoolean();
            }
            else if (k.Equals("vfxIsTakeover", StringComparison.OrdinalIgnoreCase)
                && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False))
            {
                VFXIsTakeover = v.GetBoolean();
            }
        }

        // 吸收完毕后清空，避免回写序列化时输出冗余 camelCase 字段
        CompatExtras = null;
    }
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
    /// <summary>光环自定义特效配置（P2-01：对齐官方 TCardAura.VFXConfig，透传 VFXOverrideKey）。</summary>
    public VfxConfigData? VFXConfig { get; set; }
}

public sealed class QuestDefinition
{
    public List<RuleNode>? Prerequisites { get; set; }
    public QuestRepeatableData? Repeatable { get; set; }
    /// <summary>D1-P2-01：兼容官方 JSON 布尔字段 IsRepeatable，与旧对象型 Repeatable 统一收敛至 EffectiveRepeatable。</summary>
    [JsonPropertyName("IsRepeatable")]
    public bool? IsRepeatable { get; set; }
    [JsonIgnore]
    public bool EffectiveRepeatable => IsRepeatable ?? (Repeatable != null);
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
    /// <summary>任务条目本地化描述（P2-02：对齐官方 TQuestEntry.Localization，供 TooltipFormatter 优先渲染任务目标文案）。</summary>
    public LocalizationBlock? Localization { get; set; }
    /// <summary>任务条目自定义图标覆盖键（P2-02：对齐官方 TQuestEntry.IconKeyOverride）。</summary>
    public string? IconKeyOverride { get; set; }
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

    /// <summary>返回当前选择规则的独立深拷贝，防止消费方修改污染共享模板缓存。</summary>
    public SelectionRulesData Clone() => new()
    {
        CanSelectMultiple = CanSelectMultiple,
        SelectionIsFree = SelectionIsFree,
        CanExit = CanExit,
        RerollRules = RerollRules?.Clone(),
        WillAutoSellOnExit = WillAutoSellOnExit,
        NextEncounterOnExit = NextEncounterOnExit.HasValue ? NextEncounterOnExit.Value.Clone() : null,
        MaxSelections = MaxSelections
    };
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
    /// <summary>非官方 DB 兼容字段（P2-06：官方 RerollRules 无此字段，仅保留供历史配置/Mod 反序列化兼容）。</summary>
    public bool ResetCostOnExit { get; set; }
    public int? StartingCost { get; set; } = 2;
    /// <summary>非官方 DB 兼容回退字段（P2-06：官方 DB 使用 TotalAllowedRerolls，此处仅作历史/Mod 回退别名）。</summary>
    public int? MaxRerolls { get; set; }
    public int? TotalAllowedRerolls { get; set; }
    /// <summary>非官方 DB 兼容字段（P2-06：官方 RerollRules 无此字段，仅保留供历史配置/Mod 反序列化兼容）。</summary>
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

    /// <summary>返回当前刷新规则的独立拷贝。</summary>
    public RerollRulesData Clone() => new()
    {
        CostIncrease = CostIncrease,
        CostMax = CostMax,
        ResetCostOnExit = ResetCostOnExit,
        StartingCost = StartingCost,
        MaxRerolls = MaxRerolls,
        TotalAllowedRerolls = TotalAllowedRerolls,
        RerollOnPurchase = RerollOnPurchase
    };
}

public sealed class CombatRewardData
{
    public SelectionRulesData? SelectionContextRules { get; set; }
    public RuleNode? SpawnContext { get; set; }
    public int? GoldReward { get; set; }
    public int? ExperienceReward { get; set; }
}

/// <summary>
/// P2-06：兼容反序列化官方数组形式 <c>List&lt;TSelectionRequirement&gt;</c> 与单对象形式 <c>TSelectionRequirement</c>；
/// 遇到多元素 JSON 数组时自动聚合全部前置条件至统一 <c>TSelectionRequirement</c> 节点，并保留原始子项于 <c>CombinedSelectionRequirements</c>。
/// </summary>
public sealed class SelectionRequirementsRuleNodeConverter : JsonConverter<RuleNode?>
{
    public override RuleNode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = JsonSerializer.Deserialize<List<RuleNode>>(ref reader, options);
            if (list == null || list.Count == 0)
                return null;
            if (list.Count == 1)
            {
                list[0].CombinedSelectionRequirements = list;
                return list[0];
            }

            var mergedPrereqs = new List<RuleNode>();
            foreach (var item in list)
            {
                if (item == null) continue;
                if (string.Equals(item.Type, "TSelectionRequirement", StringComparison.Ordinal))
                {
                    if (item.Prerequisites != null)
                        mergedPrereqs.AddRange(item.Prerequisites);
                }
                else
                {
                    mergedPrereqs.Add(item);
                }
            }

            return new RuleNode
            {
                Type = "TSelectionRequirement",
                Prerequisites = mergedPrereqs,
                CombinedSelectionRequirements = list
            };
        }

        return JsonSerializer.Deserialize<RuleNode>(ref reader, options);
    }

    public override void Write(Utf8JsonWriter writer, RuleNode? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }
        if (value.CombinedSelectionRequirements is { Count: > 1 })
        {
            JsonSerializer.Serialize(writer, value.CombinedSelectionRequirements, options);
            return;
        }
        JsonSerializer.Serialize(writer, value, options);
    }
}

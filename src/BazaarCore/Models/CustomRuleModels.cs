using System.Numerics;
using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 卡牌与技能的 18 维流派/构筑倾向位掩码（bits 0..17）。
/// </summary>
[Flags]
public enum ECardArchetype : ulong
{
    None = 0,
    Burn = 1UL << 0,
    Poison = 1UL << 1,
    Shield = 1UL << 2,
    Sustain = 1UL << 3,
    Speed = 1UL << 4,
    Control = 1UL << 5,
    Crit = 1UL << 6,
    Damage = 1UL << 7,
    Ammo = 1UL << 8,
    Flying = 1UL << 9,
    Economy = 1UL << 10,
    Weapon = 1UL << 11,
    Tool = 1UL << 12,
    Tech = 1UL << 13,
    Companion = 1UL << 14,
    AquaticVehicle = 1UL << 15,
    PropertyRelic = 1UL << 16,
    FoodAlchemy = 1UL << 17
}

/// <summary>
/// 单个流派倾向维度的元数据描述。
/// </summary>
public sealed record ArchetypeMetadata(
    int Bit,
    ECardArchetype Flag,
    string Key,
    string NameZh,
    string DescZh);

/// <summary>
/// 18 维流派倾向目录与位掩码辅助工具。
/// </summary>
public static class CardArchetypeCatalog
{
    /// <summary>
    /// 全部有效流派位掩码（bits 0..17）。
    /// </summary>
    public const ulong AllMask = (1UL << 18) - 1UL;

    /// <summary>
    /// 全部 18 个流派倾向定义（按位序 0..17 排列）。
    /// </summary>
    public static IReadOnlyList<ArchetypeMetadata> All { get; } =
    [
        new(0, ECardArchetype.Burn, "burn", "灼烧", "以灼烧叠层与持续燃烧伤害为核心"),
        new(1, ECardArchetype.Poison, "poison", "剧毒", "以剧毒叠层与无视护盾消耗为核心"),
        new(2, ECardArchetype.Shield, "shield", "护盾", "以高额护盾防御与护盾联动输出为核心"),
        new(3, ECardArchetype.Sustain, "sustain", "续航回复", "以生命回复、再生与吸血续航为核心"),
        new(4, ECardArchetype.Speed, "speed", "极速充能", "以急速、冷却缩减与充能循环为核心"),
        new(5, ECardArchetype.Control, "control", "控制干扰", "以减速、冻结与破坏敌方节奏为核心"),
        new(6, ECardArchetype.Crit, "crit", "暴击爆发", "以暴击率叠加与高倍率暴击爆发为核心"),
        new(7, ECardArchetype.Damage, "damage", "直伤压制", "以直接伤害成长与高频输出为核心"),
        new(8, ECardArchetype.Ammo, "ammo", "弹药装填", "以弹药消耗、补充与多连发爆发为核心"),
        new(9, ECardArchetype.Flying, "flying", "空天飞行", "以飞行状态切换与空中协同增益为核心"),
        new(10, ECardArchetype.Economy, "economy", "经济运营", "以金币获取、收益成长与高价值买卖为核心"),
        new(11, ECardArchetype.Weapon, "weapon", "武器体系", "围绕武器牌联动与攻击增益展开构筑"),
        new(12, ECardArchetype.Tool, "tool", "工具工坊", "围绕工具牌触发、改造与辅助协同展开构筑"),
        new(13, ECardArchetype.Tech, "tech", "科技机械", "围绕科技与核心组件高频联动展开构筑"),
        new(14, ECardArchetype.Companion, "companion", "好友伙伴", "围绕好友单位协同与群体触发展开构筑"),
        new(15, ECardArchetype.AquaticVehicle, "aquatic_vehicle", "水生载具", "围绕水生牌与载具跨界协同展开构筑"),
        new(16, ECardArchetype.PropertyRelic, "property_relic", "地产遗物", "围绕大型地产与远古遗物成长体系展开构筑"),
        new(17, ECardArchetype.FoodAlchemy, "food_alchemy", "美食炼金", "围绕料理食材、药水与炼金转化展开构筑")
    ];

    /// <summary>
    /// 统计掩码中已置位的比特数量。
    /// </summary>
    public static int CountBits(ulong mask) => BitOperations.PopCount(mask);

    /// <summary>
    /// 将流派位掩码限制为最多保留最低的 <paramref name="maxBits"/> 个置位比特。
    /// </summary>
    public static ulong ClampMaxBits(ulong mask, int maxBits = 3)
    {
        if (maxBits <= 0 || mask == 0UL)
            return 0UL;

        if (BitOperations.PopCount(mask) <= maxBits)
            return mask;

        ulong clamped = 0UL;
        int kept = 0;
        while (mask != 0UL && kept < maxBits)
        {
            // 提取最低有效置位比特，保留最早/低位倾向
            ulong lowestBit = mask & (~mask + 1UL);
            clamped |= lowestBit;
            mask &= ~lowestBit;
            kept++;
        }

        return clamped;
    }
}

/// <summary>
/// 遭遇权重细粒度分类枚举（共 10 类）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EEncounterWeightCategory
{
    PedestalEnchant,
    PedestalEnchantRandom,
    PedestalUpgrade,
    EventCultAndMystery,
    EventExpeditionAgent,
    EventEconomyStats,
    EventFreeDealTransform,
    MerchantItems,
    MerchantSkillsRare,
    Monsters
}

/// <summary>
/// 遭遇权重分类元数据定义。
/// </summary>
public sealed record EncounterWeightCategoryMetadata(
    EEncounterWeightCategory Category,
    string Key,
    string GroupKey,
    string GroupZh,
    string NameZh,
    double DefaultMult = 1.0);

/// <summary>
/// 10 类遭遇权重目录及中文分组信息。
/// </summary>
public static class EncounterWeightCategoryCatalog
{
    /// <summary>
    /// 全部 10 个细粒度遭遇权重分类元数据。
    /// </summary>
    public static IReadOnlyList<EncounterWeightCategoryMetadata> All { get; } =
    [
        new(EEncounterWeightCategory.PedestalEnchant, "PedestalEnchant", "Pedestal", "祭坛强化", "指定附魔祭坛"),
        new(EEncounterWeightCategory.PedestalEnchantRandom, "PedestalEnchantRandom", "Pedestal", "祭坛强化", "随机附魔祭坛"),
        new(EEncounterWeightCategory.PedestalUpgrade, "PedestalUpgrade", "Pedestal", "祭坛强化", "升阶改造祭坛"),
        new(EEncounterWeightCategory.EventCultAndMystery, "EventCultAndMystery", "Event", "奇遇事件", "邪教与神秘事件"),
        new(EEncounterWeightCategory.EventExpeditionAgent, "EventExpeditionAgent", "Event", "奇遇事件", "探险与特工事件"),
        new(EEncounterWeightCategory.EventEconomyStats, "EventEconomyStats", "Event", "奇遇事件", "经济与属性事件"),
        new(EEncounterWeightCategory.EventFreeDealTransform, "EventFreeDealTransform", "Event", "奇遇事件", "免费交易与变形事件"),
        new(EEncounterWeightCategory.MerchantItems, "MerchantItems", "Merchant", "商店商人", "常规物品商人"),
        new(EEncounterWeightCategory.MerchantSkillsRare, "MerchantSkillsRare", "Merchant", "商店商人", "技能与稀有商人"),
        new(EEncounterWeightCategory.Monsters, "Monsters", "Monsters", "野怪战斗", "PvE 野怪遭遇")
    ];

    /// <summary>
    /// 按枚举分类检索元数据（大小写不敏感字符串键亦可通过 <see cref="ByKey"/> 查询）。
    /// </summary>
    public static IReadOnlyDictionary<EEncounterWeightCategory, EncounterWeightCategoryMetadata> ByCategory { get; } =
        All.ToDictionary(x => x.Category);

    /// <summary>
    /// 按分类键检索元数据（忽略大小写）。
    /// </summary>
    public static IReadOnlyDictionary<string, EncounterWeightCategoryMetadata> ByKey { get; } =
        All.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 开局预设卡牌（物品或技能）规格。
/// </summary>
public sealed class PresetCardSpec
{
    public PresetCardSpec()
    {
    }

    [JsonPropertyName("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "Bronze";

    [JsonPropertyName("enchantment")]
    public string? Enchantment { get; set; }
}

/// <summary>
/// 自定义对局与流派倾向规则配置。
/// </summary>
public sealed class CustomRunRuleSet
{
    [JsonPropertyName("selectedArchetypeMask")]
    public ulong SelectedArchetypeMask { get; set; } = 0;

    [JsonPropertyName("archetypeWeightMultiplier")]
    public double ArchetypeWeightMultiplier { get; set; } = 2.0;

    [JsonPropertyName("archetypeMultiMatchCap")]
    public double ArchetypeMultiMatchCap { get; set; } = 3.0;

    [JsonPropertyName("allowTendencySelection")]
    public bool AllowTendencySelection { get; set; } = true;

    [JsonPropertyName("promptTendencyBeforeRun")]
    public bool PromptTendencyBeforeRun { get; set; } = true;

    [JsonPropertyName("startingGold")]
    public int StartingGold { get; set; } = 15;

    [JsonPropertyName("startingIncome")]
    public int StartingIncome { get; set; } = 5;

    [JsonPropertyName("startingExperience")]
    public int StartingExperience { get; set; } = 3;

    [JsonPropertyName("startingPrestige")]
    public int StartingPrestige { get; set; } = 20;

    [JsonPropertyName("startingHealth")]
    public int StartingHealth { get; set; } = 250;

    [JsonPropertyName("startingItems")]
    public List<PresetCardSpec> StartingItems { get; set; } = new();

    [JsonPropertyName("startingSkills")]
    public List<PresetCardSpec> StartingSkills { get; set; } = new();

    [JsonPropertyName("monsterLootChooseOne")]
    public bool MonsterLootChooseOne { get; set; } = true;

    [JsonPropertyName("encounterCategoryMultipliers")]
    public Dictionary<string, double> EncounterCategoryMultipliers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("bannedEncounterIds")]
    public List<string> BannedEncounterIds { get; set; } = new();

    [JsonPropertyName("playerCap")]
    public int PlayerCap { get; set; } = 8;

    [JsonPropertyName("botNonLethalPrestigeLock")]
    public bool BotNonLethalPrestigeLock { get; set; } = true;

    [JsonPropertyName("humanMatchEvery4Rounds")]
    public bool HumanMatchEvery4Rounds { get; set; } = true;

    [JsonPropertyName("lastHumanStandingMode")]
    public bool LastHumanStandingMode { get; set; } = true;

    /// <summary>
    /// 序列化暴露的强力自定义判定属性：纯 0~3 个流派倾向微调不会脱离标准镜像池。
    /// </summary>
    [JsonPropertyName("isCustomPowerModified")]
    public bool IsCustomPowerModifiedValue => IsCustomPowerModified();

    /// <summary>
    /// 判断规则是否修改了局内数值强度（为 false 时允许保留在标准幽灵/镜像池）。
    /// </summary>
    public bool IsCustomPowerModified()
    {
        bool isStandardStats =
            StartingGold == 15 &&
            StartingIncome == 5 &&
            StartingExperience == 3 &&
            StartingPrestige == 20 &&
            StartingHealth == 250;

        bool noPresetCards =
            (StartingItems == null || StartingItems.Count == 0) &&
            (StartingSkills == null || StartingSkills.Count == 0);

        bool noLootOrBanMods =
            BannedEncounterIds == null || BannedEncounterIds.Count == 0;

        bool multipliersInStandardRange =
            EncounterCategoryMultipliers == null ||
            EncounterCategoryMultipliers.Values.All(v => v >= 0.8 && v <= 1.25);

        return !(isStandardStats && noPresetCards && noLootOrBanMods && multipliersInStandardRange);
    }

    /// <summary>
    /// 返回经边界钳制与深拷贝归一化后的规则副本。
    /// </summary>
    public CustomRunRuleSet CloneNormalized()
    {
        var normalized = new CustomRunRuleSet
        {
            SelectedArchetypeMask = CardArchetypeCatalog.ClampMaxBits(
                SelectedArchetypeMask & CardArchetypeCatalog.AllMask,
                maxBits: 3),
            ArchetypeWeightMultiplier = ClampFinite(ArchetypeWeightMultiplier, 0.0, 10.0, 2.0),
            ArchetypeMultiMatchCap = ClampFinite(ArchetypeMultiMatchCap, 0.0, 10.0, 3.0),
            AllowTendencySelection = AllowTendencySelection,
            PromptTendencyBeforeRun = PromptTendencyBeforeRun,
            StartingGold = Math.Clamp(StartingGold, 0, 999),
            StartingIncome = Math.Clamp(StartingIncome, 0, 200),
            StartingExperience = Math.Clamp(StartingExperience, 0, 500),
            StartingPrestige = Math.Clamp(StartingPrestige, 1, 100),
            StartingHealth = Math.Clamp(StartingHealth, 50, 10000),
            StartingItems = ClonePresetList(StartingItems),
            StartingSkills = ClonePresetList(StartingSkills),
            MonsterLootChooseOne = MonsterLootChooseOne,
            EncounterCategoryMultipliers = CloneMultipliers(EncounterCategoryMultipliers),
            BannedEncounterIds = CloneDistinctIds(BannedEncounterIds),
            PlayerCap = Math.Clamp(PlayerCap, 2, 16),
            BotNonLethalPrestigeLock = BotNonLethalPrestigeLock,
            HumanMatchEvery4Rounds = HumanMatchEvery4Rounds,
            LastHumanStandingMode = LastHumanStandingMode
        };

        return normalized;
    }

    private static double ClampFinite(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return fallback;
        return Math.Clamp(value, min, max);
    }

    private static List<PresetCardSpec> ClonePresetList(List<PresetCardSpec>? source)
    {
        var result = new List<PresetCardSpec>();
        if (source == null)
            return result;

        foreach (var item in source)
        {
            if (item == null)
                continue;

            result.Add(new PresetCardSpec
            {
                TemplateId = (item.TemplateId ?? string.Empty).Trim(),
                Tier = string.IsNullOrWhiteSpace(item.Tier) ? "Bronze" : item.Tier.Trim(),
                Enchantment = string.IsNullOrWhiteSpace(item.Enchantment) ? null : item.Enchantment.Trim()
            });
        }

        return result;
    }

    private static Dictionary<string, double> CloneMultipliers(Dictionary<string, double>? source)
    {
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
            return result;

        foreach (var kv in source)
        {
            if (string.IsNullOrWhiteSpace(kv.Key))
                continue;

            result[kv.Key.Trim()] = ClampFinite(kv.Value, 0.0, 10.0, 1.0);
        }

        return result;
    }

    private static List<string> CloneDistinctIds(List<string>? source)
    {
        var result = new List<string>();
        if (source == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in source)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var trimmed = id.Trim();
            if (seen.Add(trimmed))
                result.Add(trimmed);
        }

        return result;
    }
}

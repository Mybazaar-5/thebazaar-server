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
/// 三套核心预设档位枚举（含 Custom 自定义档）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EGameModePreset
{
    PureOfficial = 0,
    EnhancedFun = 1,
    ExtremeRoguelike = 2,
    Custom = 3
}

/// <summary>
/// 精细玩法模式枚举（标准模式 + 5 大新玩法模式，其中突变词条与每日同种子细分为两个独立模式 ID 以支持独立排行榜与持久化）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ESpecialModeId
{
    Standard = 0,
    EndlessTower = 1,
    CrossHeroMayhem = 2,
    MutatorChallenge = 3,
    DailyChallenge = 4,
    BossRush = 5,
    SandboxLab = 6
}

/// <summary>
/// 顶层玩法模式分组枚举（对齐客户端大厅 UI 6 大入口卡片）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GamePlayMode
{
    Standard = 0,
    EndlessTower = 1,
    CrossHeroMayhem = 2,
    MutatorAndDailyChallenge = 3,
    BossRush = 4,
    SandboxLab = 5
}

/// <summary>
/// 预设档位元数据描述。
/// </summary>
public sealed record PresetMetadata(
    EGameModePreset PresetId,
    string Key,
    string NameZh,
    string DescZh,
    double ScoreMultiplier,
    bool AllowsStandardGhostPool);

/// <summary>
/// 三套预设档位目录与一键工厂（经典官方 / 爽玩增强 / 极限肉鸽）。
/// </summary>
public static class PresetCatalog
{
    public static IReadOnlyList<PresetMetadata> All { get; } =
    [
        new(
            EGameModePreset.PureOfficial,
            "PureOfficial",
            "经典官方",
            "1:1 对齐官方正式服数值与节奏，允许计入标准镜像池与常规榜单",
            1.0,
            AllowsStandardGhostPool: true),
        new(
            EGameModePreset.EnhancedFun,
            "EnhancedFun",
            "爽玩增强",
            "初始资源更充裕（25金/7收/6经/25声/350血），祭坛奇遇 1.4x、白银+ 权重 +15%、野怪战利品选项 +1，严格隔离至娱乐镜像池",
            0.8,
            AllowsStandardGhostPool: false),
        new(
            EGameModePreset.ExtremeRoguelike,
            "ExtremeRoguelike",
            "极限肉鸽",
            "高压高回报（12金/4收/3经/12声/220血），奇遇祭坛 1.6x、黄金+ 权重 +20%、物价 +10%、敌方数值 1.25x、掉落品阶 +1，积分倍率 2.0x",
            2.0,
            AllowsStandardGhostPool: false)
    ];

    public static IReadOnlyDictionary<EGameModePreset, PresetMetadata> ById { get; } =
        All.ToDictionary(x => x.PresetId);

    /// <summary>
    /// 按预设档位与可选特殊玩法模式一键生成归一化规则集。
    /// </summary>
    public static CustomRunRuleSet Create(
        EGameModePreset preset,
        ESpecialModeId mode = ESpecialModeId.Standard)
    {
        var rules = new CustomRunRuleSet
        {
            PresetId = preset,
            SpecialModeId = mode
        };

        switch (preset)
        {
            case EGameModePreset.EnhancedFun:
                rules.StartingGold = 25;
                rules.StartingIncome = 7;
                rules.StartingExperience = 6;
                rules.StartingPrestige = 25;
                rules.StartingHealth = 350;
                rules.MonsterLootChooseOne = true;
                rules.MonsterLootBonusChoices = 1;
                rules.ShopHighTierBonusPct = 15.0;
                rules.EncounterCategoryMultipliers["PedestalEnchant"] = 1.4;
                rules.EncounterCategoryMultipliers["PedestalEnchantRandom"] = 1.4;
                rules.EncounterCategoryMultipliers["PedestalUpgrade"] = 1.4;
                rules.EncounterCategoryMultipliers["EventCultAndMystery"] = 1.4;
                rules.EncounterCategoryMultipliers["EventExpeditionAgent"] = 1.4;
                rules.EncounterCategoryMultipliers["EventEconomyStats"] = 1.4;
                rules.EncounterCategoryMultipliers["EventFreeDealTransform"] = 1.4;
                break;

            case EGameModePreset.ExtremeRoguelike:
                rules.StartingGold = 12;
                rules.StartingIncome = 4;
                rules.StartingExperience = 3;
                rules.StartingPrestige = 12;
                rules.StartingHealth = 220;
                rules.MonsterLootChooseOne = true;
                rules.MonsterLootTierBonus = 1;
                rules.ShopHighTierBonusPct = 20.0;
                rules.ShopPriceMultiplier = 1.10;
                rules.EnemyStatMultiplier = 1.25;
                rules.EncounterCategoryMultipliers["PedestalEnchant"] = 1.6;
                rules.EncounterCategoryMultipliers["PedestalEnchantRandom"] = 1.6;
                rules.EncounterCategoryMultipliers["PedestalUpgrade"] = 1.6;
                rules.EncounterCategoryMultipliers["EventCultAndMystery"] = 1.6;
                rules.EncounterCategoryMultipliers["EventExpeditionAgent"] = 1.6;
                rules.EncounterCategoryMultipliers["EventEconomyStats"] = 1.6;
                rules.EncounterCategoryMultipliers["EventFreeDealTransform"] = 1.6;
                rules.EncounterCategoryMultipliers["Monsters"] = 1.25;
                break;

            case EGameModePreset.PureOfficial:
            default:
                break;
        }

        // 针对特定特殊模式套用默认基础资源或参数
        switch (mode)
        {
            case ESpecialModeId.EndlessTower:
                rules.EndlessMaxDay = 99;
                break;
            case ESpecialModeId.CrossHeroMayhem:
                rules.CrossHeroOwnHeroBias = 0.40;
                break;
            case ESpecialModeId.BossRush:
                rules.BossRushTotalWaves = 10;
                rules.BossRushIncludeMilestonePvp = true;
                break;
            case ESpecialModeId.SandboxLab:
                rules.StartingGold = 999;
                rules.StartingIncome = 100;
                rules.StartingHealth = 1000;
                rules.StartingPrestige = 99;
                rules.SandboxFreeInfiniteReroll = true;
                break;
        }

        return rules.CloneNormalized();
    }

    public static GamePlayMode ToGamePlayMode(ESpecialModeId mode) => mode switch
    {
        ESpecialModeId.EndlessTower => GamePlayMode.EndlessTower,
        ESpecialModeId.CrossHeroMayhem => GamePlayMode.CrossHeroMayhem,
        ESpecialModeId.MutatorChallenge or ESpecialModeId.DailyChallenge => GamePlayMode.MutatorAndDailyChallenge,
        ESpecialModeId.BossRush => GamePlayMode.BossRush,
        ESpecialModeId.SandboxLab => GamePlayMode.SandboxLab,
        _ => GamePlayMode.Standard
    };

    public static ESpecialModeId FromGamePlayMode(GamePlayMode mode, bool isDailyChallenge = false) => mode switch
    {
        GamePlayMode.EndlessTower => ESpecialModeId.EndlessTower,
        GamePlayMode.CrossHeroMayhem => ESpecialModeId.CrossHeroMayhem,
        GamePlayMode.MutatorAndDailyChallenge => isDailyChallenge ? ESpecialModeId.DailyChallenge : ESpecialModeId.MutatorChallenge,
        GamePlayMode.BossRush => ESpecialModeId.BossRush,
        GamePlayMode.SandboxLab => ESpecialModeId.SandboxLab,
        _ => ESpecialModeId.Standard
    };
}

/// <summary>
/// 单个肉鸽突变词条元数据定义（含中文名、量化描述、冲突组、积分倍率、生效钩子与数值参数）。
/// </summary>
public sealed record MutatorMetadata(
    string Id,
    string NameZh,
    string DescZh,
    string? ConflictGroup,
    double ScoreMultiplierDelta,
    bool IsPositive,
    string TargetHook = "CombatSimulator",
    IReadOnlyDictionary<string, double>? Parameters = null);

/// <summary>
/// 全局 14 条肉鸽突变词条权威目录与冲突消解工具。
/// </summary>
public static class MutatorCatalog
{
    public const string TurboFrenzy = "turbo_frenzy";
    public const string AllEnchanted = "all_enchanted";
    public const string DoubleEconomy = "double_economy";
    public const string GlassCannon = "glass_cannon";
    public const string GoldenStart = "golden_start";
    public const string ShopHalfOff = "shop_half_off";
    public const string DoubleTrigger = "double_trigger";
    public const string BloodMarket = "blood_market";
    public const string TitanVitality = "titan_vitality";
    public const string CritOverload = "crit_overload";
    public const string FrozenWasteland = "frozen_wasteland";
    public const string EventCarnival = "event_carnival";
    public const string IronmanNoFates = "ironman_no_fates";
    public const string CrossHeroChaos = "cross_hero_chaos";

    /// <summary>全部 14 条肉鸽突变词条定义。</summary>
    public static IReadOnlyList<MutatorMetadata> All { get; } =
    [
        new(TurboFrenzy, "极速狂潮", "全员物品冷却 -35%（下限 500ms），沙暴提前 5 秒启动", "pace", +0.25, IsPositive: false,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["cooldownReductionPct"] = 0.35, ["minCooldownMs"] = 500, ["sandstormOffsetMs"] = -5000 }),
        new(AllEnchanted, "万物附魔", "商店、战利品、奇遇生成的所有物品 100% 附带合法随机附魔", null, +0.15, IsPositive: true,
            TargetHook: "ShopEngine/SpawnQuery",
            Parameters: new Dictionary<string, double> { ["enchantChance"] = 1.0 }),
        new(DoubleEconomy, "双倍经济", "初始金币、每日收入、战斗与卖卡金币全部 ×2，物价不变", "econ", -0.10, IsPositive: true,
            TargetHook: "RunEngine/ShopEngine",
            Parameters: new Dictionary<string, double> { ["economyMultiplier"] = 2.0 }),
        new(GlassCannon, "玻璃大炮", "双方生命上限 -40%，所有伤害、灼烧与剧毒施加量 +80%", "vitality", +0.35, IsPositive: false,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["healthMultiplier"] = 0.60, ["damageAndDotMultiplier"] = 1.80 }),
        new(GoldenStart, "全金开局", "Day 1 解锁 8 格手牌，自动发放 2 件随机 Gold 物品与 1 个 Gold 技能", "start", -0.15, IsPositive: true,
            TargetHook: "RunEngine.StartRun",
            Parameters: new Dictionary<string, double> { ["unlockedSlots"] = 8, ["goldItemCount"] = 2, ["goldSkillCount"] = 1 }),
        new(ShopHalfOff, "商店全折", "商人买价统一 5 折（最低 1 金），刷新费用 -50%", "econ", -0.10, IsPositive: true,
            TargetHook: "ShopEngine",
            Parameters: new Dictionary<string, double> { ["buyPriceMultiplier"] = 0.50, ["rerollCostMultiplier"] = 0.50 }),
        new(DoubleTrigger, "双倍触发", "全员所有主动物品默认 Multicast +1（单卡额外加成封顶 +2）", null, +0.30, IsPositive: true,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["bonusMulticast"] = 1, ["maxBonusMulticast"] = 2 }),
        new(BloodMarket, "血色黑市", "商店刷新 0 金币，但每次刷新消耗 5% 当前生命上限（下限 50 HP）", "econ", +0.25, IsPositive: false,
            TargetHook: "RunEngine.OnReroll",
            Parameters: new Dictionary<string, double> { ["rerollGoldCost"] = 0, ["rerollHpCostPct"] = 0.05, ["minHpCost"] = 50 }),
        new(TitanVitality, "泰坦巨躯", "双方生命上限 ×2.5，护盾与再生效能 +50%，沙暴延后 10 秒", "vitality", +0.20, IsPositive: true,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["healthMultiplier"] = 2.50, ["shieldAndRegenMultiplier"] = 1.50, ["sandstormOffsetMs"] = 10000 }),
        new(CritOverload, "暴击过载", "全员基础暴击率 +50%，暴击倍率由 200% 提升至 275%", null, +0.25, IsPositive: true,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["bonusCritChance"] = 50, ["critPercentMultiplier"] = 275 }),
        new(FrozenWasteland, "冰霜荒原", "战斗每 4 秒随机冻结双方各 1 件未冻结物品 1.0 秒", null, +0.30, IsPositive: false,
            TargetHook: "CombatSimulator",
            Parameters: new Dictionary<string, double> { ["freezeIntervalMs"] = 4000, ["freezeDurationMs"] = 1000 }),
        new(EventCarnival, "奇遇狂欢", "白昼奇遇事件与附魔/升阶祭坛出现权重 ×2.5", null, +0.15, IsPositive: true,
            TargetHook: "ShopEngine",
            Parameters: new Dictionary<string, double> { ["eventAndPedestalMultiplier"] = 2.5 }),
        new(IronmanNoFates, "铁人无悔", "禁用 Fates 濒死复活、初始声望降为 10", null, +0.60, IsPositive: false,
            TargetHook: "RunEngine",
            Parameters: new Dictionary<string, double> { ["disableFates"] = 1, ["startingPrestige"] = 10 }),
        new(CrossHeroChaos, "跨界异变", "每家常规商店额外混入 1~2 件跨英雄高阶物品", null, +0.20, IsPositive: true,
            TargetHook: "ShopEngine",
            Parameters: new Dictionary<string, double> { ["minExtraCrossHeroItems"] = 1, ["maxExtraCrossHeroItems"] = 2 })
    ];

    /// <summary>按词条 ID 索引（大小写不敏感）。</summary>
    public static IReadOnlyDictionary<string, MutatorMetadata> ById { get; } =
        All.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 清洗、去重、过滤非法词条并按互斥组（ConflictGroup）消解冲突，最多保留 <paramref name="maxCount"/> 条。
    /// </summary>
    public static List<string> NormalizeAndResolveConflicts(IEnumerable<string>? ids, int maxCount = 4)
    {
        var result = new List<string>();
        if (ids == null || maxCount <= 0)
            return result;

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in ids)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var trimmed = raw.Trim();
            if (!ById.TryGetValue(trimmed, out var meta))
                continue;
            if (!seenIds.Add(meta.Id))
                continue;
            if (!string.IsNullOrEmpty(meta.ConflictGroup) && !usedGroups.Add(meta.ConflictGroup))
                continue;

            result.Add(meta.Id);
            if (result.Count >= maxCount)
                break;
        }

        return result;
    }

    /// <summary>
    /// 计算给定预设档位与激活突变词条列表叠加后的最终积分结算倍率（最低钳底 0.25x）。
    /// </summary>
    public static double CalculateTotalScoreMultiplier(EGameModePreset preset, IEnumerable<string>? mutatorIds)
    {
        double baseMult = PresetCatalog.ById.TryGetValue(preset, out var p) ? p.ScoreMultiplier : 1.0;
        if (mutatorIds != null)
        {
            foreach (var id in NormalizeAndResolveConflicts(mutatorIds, maxCount: 14))
            {
                if (ById.TryGetValue(id, out var m))
                    baseMult += m.ScoreMultiplierDelta;
            }
        }
        return Math.Max(0.25, Math.Round(baseMult, 2));
    }
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
/// 自定义对局、预设档位、5 大新玩法模式与流派倾向规则配置（100% 向后兼容旧 JSON）。
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
    public int StartingExperience { get; set; } = 0;

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

    // ===== Wave 2 新增：3 套预设档位 + 5 大新玩法模式 + 14 条突变词条扩展字段（全部带默认值，旧 JSON 100% 兼容） =====

    [JsonPropertyName("presetId")]
    public EGameModePreset PresetId { get; set; } = EGameModePreset.PureOfficial;

    [JsonPropertyName("specialModeId")]
    public ESpecialModeId SpecialModeId { get; set; } = ESpecialModeId.Standard;

    [JsonPropertyName("mutatorIds")]
    public List<string> MutatorIds { get; set; } = new();

    [JsonPropertyName("dailyChallengeDate")]
    public string? DailyChallengeDate { get; set; }

    [JsonPropertyName("fixedRunSeed")]
    public int? FixedRunSeed { get; set; }

    [JsonPropertyName("endlessMaxDay")]
    public int EndlessMaxDay { get; set; } = 99;

    [JsonPropertyName("crossHeroOwnHeroBias")]
    public double CrossHeroOwnHeroBias { get; set; } = 0.40;

    [JsonPropertyName("bossRushTotalWaves")]
    public int BossRushTotalWaves { get; set; } = 10;

    [JsonPropertyName("bossRushIncludeMilestonePvp")]
    public bool BossRushIncludeMilestonePvp { get; set; } = true;

    [JsonPropertyName("sandboxFreeInfiniteReroll")]
    public bool SandboxFreeInfiniteReroll { get; set; } = true;

    [JsonPropertyName("sandboxZeroShopPrice")]
    public bool SandboxZeroShopPrice { get; set; } = false;

    [JsonPropertyName("shopHighTierBonusPct")]
    public double ShopHighTierBonusPct { get; set; } = 0.0;

    [JsonPropertyName("shopPriceMultiplier")]
    public double ShopPriceMultiplier { get; set; } = 1.0;

    [JsonPropertyName("monsterLootBonusChoices")]
    public int MonsterLootBonusChoices { get; set; } = 0;

    [JsonPropertyName("monsterLootTierBonus")]
    public int MonsterLootTierBonus { get; set; } = 0;

    [JsonPropertyName("enemyStatMultiplier")]
    public double EnemyStatMultiplier { get; set; } = 1.0;

    [JsonPropertyName("disableFatesRevive")]
    public bool DisableFatesRevive { get; set; } = false;

    /// <summary>本局或锦标赛房间额外选用的服务器/社区 Mod ID 列表（仅在本局/本房生效，不污染他人标准对局与标准幽灵池）。</summary>
    [JsonPropertyName("selectedModIds")]
    public List<string> SelectedModIds { get; set; } = new();

    [JsonIgnore]
    public GamePlayMode PlayModeCategory => PresetCatalog.ToGamePlayMode(SpecialModeId);

    [JsonIgnore]
    public bool IsSandboxRun => SpecialModeId == ESpecialModeId.SandboxLab;

    [JsonIgnore]
    public bool IsNonStandardRun =>
        SpecialModeId != ESpecialModeId.Standard
        || PresetId != EGameModePreset.PureOfficial
        || (MutatorIds != null && MutatorIds.Count > 0)
        || IsCustomPowerModified();

    [JsonIgnore]
    public bool ShouldIsolateFromStandardGhostPool => IsNonStandardRun;

    [JsonIgnore]
    public bool ShouldSkipGhostPool => IsSandboxRun;

    [JsonIgnore]
    public bool ShouldSkipCareerAndLeaderboard => IsSandboxRun;

    /// <summary>
    /// 序列化暴露的强力自定义判定属性：纯 0~3 个流派倾向微调不会脱离标准镜像池；
    /// 任何非标准模式、非经典官方预设或突变词条均强制为 true。
    /// </summary>
    [JsonPropertyName("isCustomPowerModified")]
    public bool IsCustomPowerModifiedValue => IsCustomPowerModified();

    /// <summary>判断是否激活指定突变词条（大小写不敏感）。</summary>
    public bool HasMutator(string mutatorId)
    {
        if (string.IsNullOrWhiteSpace(mutatorId) || MutatorIds == null || MutatorIds.Count == 0)
            return false;
        for (int i = 0; i < MutatorIds.Count; i++)
        {
            if (string.Equals(MutatorIds[i], mutatorId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 判断规则是否修改了局内数值强度或启用了非标准玩法模式（为 false 时允许保留在标准幽灵/镜像池）。
    /// </summary>
    public bool IsCustomPowerModified()
    {
        bool isStandardMode =
            SpecialModeId == ESpecialModeId.Standard &&
            PresetId == EGameModePreset.PureOfficial &&
            (MutatorIds == null || MutatorIds.Count == 0);

        if (!isStandardMode)
            return true;

        bool isStandardStats =
            StartingGold == 15 &&
            StartingIncome == 5 &&
            StartingExperience == 0 &&
            StartingPrestige == 20 &&
            StartingHealth == 250 &&
            Math.Abs(ShopHighTierBonusPct) < 1e-6 &&
            Math.Abs(ShopPriceMultiplier - 1.0) < 1e-6 &&
            MonsterLootBonusChoices == 0 &&
            MonsterLootTierBonus == 0 &&
            Math.Abs(EnemyStatMultiplier - 1.0) < 1e-6 &&
            !DisableFatesRevive;

        bool noPresetCards =
            (StartingItems == null || StartingItems.Count == 0) &&
            (StartingSkills == null || StartingSkills.Count == 0);

        bool noLootOrBanMods =
            (BannedEncounterIds == null || BannedEncounterIds.Count == 0) &&
            (SelectedModIds == null || SelectedModIds.Count == 0);

        bool multipliersInStandardRange =
            EncounterCategoryMultipliers == null ||
            EncounterCategoryMultipliers.Values.All(v => v >= 0.8 && v <= 1.25);

        return !(isStandardStats && noPresetCards && noLootOrBanMods && multipliersInStandardRange);
    }

    /// <summary>
    /// 解析该对局在 pvp_ghosts 表中的分区键，确保非标准模式与沙盒模式绝不污染标准镜像池。
    /// </summary>
    public string ResolveGhostPoolPartition()
    {
        if (IsSandboxRun)
            return "none";

        return SpecialModeId switch
        {
            ESpecialModeId.EndlessTower => "endless_tower",
            ESpecialModeId.CrossHeroMayhem => "cross_hero",
            ESpecialModeId.MutatorChallenge => "mutator",
            ESpecialModeId.DailyChallenge => "daily",
            ESpecialModeId.BossRush => "boss_rush",
            _ => !IsCustomPowerModified()
                ? "standard"
                : PresetId switch
                {
                    EGameModePreset.EnhancedFun => "enhanced",
                    EGameModePreset.ExtremeRoguelike => "extreme",
                    _ => "custom"
                }
        };
    }

    /// <summary>一键创建指定预设档位与玩法模式的归一化规则集。</summary>
    public static CustomRunRuleSet CreatePreset(EGameModePreset preset, ESpecialModeId mode = ESpecialModeId.Standard)
        => PresetCatalog.Create(preset, mode);

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
            LastHumanStandingMode = LastHumanStandingMode,
            PresetId = Enum.IsDefined(PresetId) ? PresetId : EGameModePreset.PureOfficial,
            SpecialModeId = Enum.IsDefined(SpecialModeId) ? SpecialModeId : ESpecialModeId.Standard,
            MutatorIds = MutatorCatalog.NormalizeAndResolveConflicts(MutatorIds, maxCount: 4),
            DailyChallengeDate = string.IsNullOrWhiteSpace(DailyChallengeDate) ? null : DailyChallengeDate.Trim(),
            FixedRunSeed = FixedRunSeed,
            EndlessMaxDay = Math.Clamp(EndlessMaxDay, 11, 99),
            CrossHeroOwnHeroBias = ClampFinite(CrossHeroOwnHeroBias, 0.0, 1.0, 0.40),
            BossRushTotalWaves = Math.Clamp(BossRushTotalWaves, 1, 30),
            BossRushIncludeMilestonePvp = BossRushIncludeMilestonePvp,
            SandboxFreeInfiniteReroll = SandboxFreeInfiniteReroll,
            SandboxZeroShopPrice = SandboxZeroShopPrice,
            ShopHighTierBonusPct = ClampFinite(ShopHighTierBonusPct, 0.0, 100.0, 0.0),
            ShopPriceMultiplier = ClampFinite(ShopPriceMultiplier, 0.1, 5.0, 1.0),
            MonsterLootBonusChoices = Math.Clamp(MonsterLootBonusChoices, 0, 5),
            MonsterLootTierBonus = Math.Clamp(MonsterLootTierBonus, 0, 3),
            EnemyStatMultiplier = ClampFinite(EnemyStatMultiplier, 0.25, 10.0, 1.0),
            DisableFatesRevive = DisableFatesRevive,
            SelectedModIds = CloneDistinctIds(SelectedModIds, maxCount: 16)
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

    private static List<string> CloneDistinctIds(List<string>? source, int maxCount = 64)
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
            {
                result.Add(trimmed);
                if (result.Count >= maxCount)
                    break;
            }
        }

        return result;
    }
}

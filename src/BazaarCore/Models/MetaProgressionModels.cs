using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 对应 GameData.db 中 challenges 表的每日/每周挑战模板（对齐官方 TChallenge）。
/// </summary>
public sealed class ChallengeTemplate
{
    public string Id { get; set; } = string.Empty;

    /// <summary>挑战周期类型："Daily" 或 "Weekly"（官方 JSON 字段名为 Duration）。</summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>兼容别名：等同于 <see cref="Duration"/>（Daily / Weekly）。</summary>
    [JsonIgnore]
    public string Type => Duration;

    [JsonIgnore]
    public bool IsDaily => string.Equals(Duration, "Daily", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsWeekly => string.Equals(Duration, "Weekly", StringComparison.OrdinalIgnoreCase);

    public string Version { get; set; } = string.Empty;
    public int XpReward { get; set; }
    public List<ChallengeCurrencyReward> CurrencyRewards { get; set; } = new();
    public int CompletionRequirement { get; set; }
    public ChallengeLocalizationData? Localization { get; set; }

    /// <summary>挑战进度触发规则树（加载期由 GameDatabaseLoader 一次性预编译）。</summary>
    public RuleNode? Trigger { get; set; }

    public bool IsActive { get; set; }

    /// <summary>返回解析后的标题文本（优先返回 Localization.Title.Text）。</summary>
    [JsonIgnore]
    public string DisplayTitle => Localization?.Title?.Text ?? Id;

    /// <summary>返回替换 {completionRequirement} 占位符后的描述文本。</summary>
    public string GetFormattedDescription()
    {
        string raw = Localization?.Description?.Text ?? string.Empty;
        return raw.Replace("{completionRequirement}", CompletionRequirement.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>
/// 对应官方 TChallenge.CurrencyRewards 元素（CurrencyReward）。
/// </summary>
public sealed class ChallengeCurrencyReward
{
    public string CurrencyType { get; set; } = string.Empty;
    public int Amount { get; set; }
}

/// <summary>
/// 对应官方 TChallengeLocalization（含 Title 与 Description 本地化键及英文默认文本）。
/// </summary>
public sealed class ChallengeLocalizationData
{
    public LocalizedText? Title { get; set; }
    public LocalizedText? Description { get; set; }
}

/// <summary>
/// 对应 GameData.db 中 collectibles 表的全量藏品模板（8 类数组合计 1,644 条，对齐官方 TCollectible）。
/// </summary>
public sealed class CollectibleTemplate
{
    public string Type { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;

    /// <summary>稀有度：Bronze / Silver / Gold / Diamond / Legendary。</summary>
    public string Rarity { get; set; } = string.Empty;

    /// <summary>是否允许从宝箱奖池开出（严格排除非宝箱限定皮肤/藏品）。</summary>
    public bool IsFoundInChests { get; set; }

    public bool InStore { get; set; } = true;
    public string HeroId { get; set; } = string.Empty;
    public string SeasonId { get; set; } = string.Empty;

    /// <summary>SQLite collectibles 表主键分类名（如 album/bank/board/card_back/card_skin/carpet/hero_skin/stash）。</summary>
    [JsonIgnore]
    public string CategoryKey { get; set; } = string.Empty;

    /// <summary>去除连字符的小写 32 位规范化 ID。</summary>
    [JsonIgnore]
    public string NormalizedId => Guid.TryParse(Id, out var g)
        ? g.ToString("N")
        : Id.Trim().Replace("-", "").ToLowerInvariant();

    /// <summary>是否为卡牌皮肤藏品。</summary>
    [JsonIgnore]
    public bool IsCardSkin => string.Equals(Type, "CardSkin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Type, "card_skin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(CategoryKey, "card_skin", StringComparison.OrdinalIgnoreCase);

    /// <summary>将 Type 规范化为与客户端白名单一致的下划线分类名（如 CardSkin -> card_skin）。</summary>
    public string GetCanonicalCategoryKey()
    {
        if (!string.IsNullOrWhiteSpace(CategoryKey))
            return CategoryKey.Trim().ToLowerInvariant();

        return (Type ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "cardskin" or "card_skin" => "card_skin",
            "heroskin" or "hero_skin" => "hero_skin",
            "cardback" or "card_back" => "card_back",
            "board" => "board",
            "carpet" => "carpet",
            "stash" => "stash",
            "bank" => "bank",
            "album" => "album",
            "toy" => "toy",
            var other => other,
        };
    }
}

/// <summary>
/// 对应 GameData.db 中 seasons 表的赛季配置（21 行，Id 1..21，对齐官方 TSeason）。
/// </summary>
public sealed class SeasonTemplate
{
    [JsonPropertyName("$type")]
    public string? TypeDiscriminator { get; set; }

    public int Id { get; set; }
    public string Version { get; set; } = string.Empty;
    public string InternalName { get; set; } = string.Empty;
    public string TextSeasonalAssetId { get; set; } = string.Empty;
    public string SpriteSeasonalAssetId { get; set; } = string.Empty;
    public string ChestSeasonalAssetId { get; set; } = string.Empty;
}

/// <summary>
/// 对应 GameData.db 中 tooltips 表的词条与排版符号模板（136 条，对齐官方 TTooltipSymbol）。
/// </summary>
public sealed class TooltipSymbolTemplate
{
    public string Id { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public bool Singleton { get; set; }
    public bool PostSymbol { get; set; }
    public bool PostWord { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Keyword { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
}

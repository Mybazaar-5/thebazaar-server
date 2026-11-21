using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 卡牌品质等级（青铜 &lt; 白银 &lt; 黄金 &lt; 钻石 &lt; 传说）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardTier
{
    Bronze = 0,
    Silver = 1,
    Gold = 2,
    Diamond = 3,
    Legendary = 4
}

/// <summary>
/// 卡牌占位尺寸（1/2/3 格）。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CardSize
{
    Small = 1,
    Medium = 2,
    Large = 3
}

/// <summary>
/// 英雄与公共卡池定义。
/// </summary>
public static class HeroNames
{
    public const string Common = "Common";
    public const string Vanessa = "Vanessa";
    public const string Dooley = "Dooley";
    public const string Pygmalien = "Pygmalien";
    public const string Mak = "Mak";
    public const string Karnok = "Karnok";
    public const string Hero8 = "Hero8";
    public const string Jules = "Jules";
    public const string Stelle = "Stelle";

    /// <summary>
    /// 全部 8 位可玩英雄列表。
    /// </summary>
    public static readonly string[] PlayableHeroes =
    [
        Vanessa, Dooley, Pygmalien, Mak, Karnok, Jules, Stelle, Hero8
    ];

    /// <summary>
    /// 获取英雄中文展示名称与特色简介。
    /// </summary>
    public static (string DisplayName, string Title, string Summary) GetHeroProfile(string hero) => hero switch
    {
        Vanessa => ("瓦内莎 (Vanessa)", "海盗船长", "水生、武器、弹药、急速、剧毒与暴击爆发"),
        Dooley => ("杜利 (Dooley)", "机械天才", "核心协同、护盾、灼烧、好友与充能循环"),
        Pygmalien => ("皮格马利翁 (Pygmalien)", "富豪商人", "高生命值、地产增值、经济滚雪球与巨额护盾/治疗"),
        Mak => ("马克 (Mak)", "炼金术士", "药水、遗物、剧毒、再生与变形炼金"),
        Karnok => ("卡诺克 (Karnok)", "狂怒战酋", "怒气值、狂暴状态、高额重击与节奏压制"),
        Jules => ("朱尔斯 (Jules)", "星际大厨", "美食料理、加热/冰镇机制与多元联动"),
        Stelle => ("斯特拉 (Stelle)", "空天工程师", "飞行状态、工坊工具、脉冲与跨界科技"),
        Hero8 => ("双龙 (The Dragons)", "双生乐龙", "节奏值、七音阶音符槽、乐器联动与巨龙爆发"),
        _ => (hero, "冒险者", "通用冒险构筑")
    };
}

/// <summary>
/// 全部 13 种附魔类型常量。
/// </summary>
public static class EnchantmentNames
{
    public static readonly string[] All =
    [
        "Heavy", "Icy", "Turbo", "Shielded", "Restorative", "Toxic",
        "Fiery", "Shiny", "Deadly", "Radiant", "Obsidian", "Golden", "Mossy"
    ];

    public static string GetChineseName(string enchant) => enchant switch
    {
        "Heavy" => "沉重(减速)",
        "Icy" => "冰霜(冻结)",
        "Turbo" => "涡轮(加速)",
        "Shielded" => "护盾",
        "Restorative" => "恢复(治疗)",
        "Toxic" => "剧毒",
        "Fiery" => "烈焰(灼烧)",
        "Shiny" => "闪亮(多重触发)",
        "Deadly" => "致命(暴击)",
        "Radiant" => "圣光(免疫破坏/控)",
        "Obsidian" => "黑曜石(伤害)",
        "Golden" => "黄金(双倍价值)",
        "Mossy" => "苔藓(再生)",
        _ => enchant
    };
}

/// <summary>
/// 卡牌质量与属性辅助工具。
/// </summary>
public static class TierHelper
{
    public static CardTier ParseTier(string? tier, CardTier fallback = CardTier.Bronze)
    {
        return Enum.TryParse<CardTier>(tier, true, out var result) ? result : fallback;
    }

    public static CardSize ParseSize(string? size, CardSize fallback = CardSize.Small)
    {
        return Enum.TryParse<CardSize>(size, true, out var result) ? result : fallback;
    }

    public static int SlotCount(this CardSize size) => (int)size;

    public static string ToChineseName(this CardTier tier) => tier switch
    {
        CardTier.Bronze => "青铜",
        CardTier.Silver => "白银",
        CardTier.Gold => "黄金",
        CardTier.Diamond => "钻石",
        CardTier.Legendary => "传说",
        _ => tier.ToString()
    };

    public static CardTier NextTier(this CardTier tier) => tier switch
    {
        CardTier.Bronze => CardTier.Silver,
        CardTier.Silver => CardTier.Gold,
        CardTier.Gold => CardTier.Diamond,
        _ => tier
    };
}

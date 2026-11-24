using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BazaarLauncher.Modules;

/// <summary>
/// Wave 4C（任务 #11）：PvP VS 对战名牌与段位/头衔中文清洗格式化器（零 Unity 依赖，支持单元测试直测）。
/// 职责边界：
///   1. 仅在中文语言态下生效，英文态 100% 透传原文，保证运行期中英双向无损切换；
///   2. 严格白名单剥离残留的模式/强度桶/段位噪声括号后缀（如 (Normal)/(Ranked)/(Strong)/(MetaBoss)/(Gold) 等，兼容全角括号），
///      绝不误删锦标赛真实玩家的战队标签或合法昵称（如 Alice (TSM)）；
///   3. 完整保护隐私屏蔽符 "******"，并将纯英文英雄名、幽灵占位名、英文段位与头衔统一映射为清爽中文。
/// </summary>
public static class PvpNameplateFormatter
{
    private static readonly Dictionary<string, string> HeroNameZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Vanessa"] = "瓦内莎",
        ["Pygmalien"] = "皮格马利翁",
        ["Dooley"] = "杜利",
        ["Mak"] = "马克",
        ["Stelle"] = "斯黛拉",
        ["Jules"] = "朱尔斯",
        ["Karnok"] = "卡诺克",
        ["The Dragons"] = "双龙",
        ["Hero8"] = "双龙",
    };

    private static readonly Dictionary<string, string> OpponentPlaceholderZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Challenger"] = "挑战者",
        ["Small Boss"] = "挑战者",
        ["Unknown Player"] = "未知玩家",
        ["Opponent assigned on selection"] = "选择后分配对手",
        ["PvP Opponent"] = "PvP 对手",
    };

    private static readonly Dictionary<string, string> RankTokenZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bronze"] = "青铜",
        ["Silver"] = "白银",
        ["Gold"] = "黄金",
        ["Diamond"] = "钻石",
        ["Legend"] = "传奇",
        ["Legendary"] = "传奇",
    };

    private static readonly Dictionary<string, string> TitleAndRankExactZhMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bronze"] = "青铜",
        ["Silver"] = "白银",
        ["Gold"] = "黄金",
        ["Diamond"] = "钻石",
        ["Legend"] = "传奇",
        ["Legendary"] = "传奇",
        ["Apprentice Merchant"] = "见习商人",
        ["Wandering"] = "流浪旅商",
        ["Seasoned"] = "老练商贾",
        ["Gilded"] = "鎏金商豪",
        ["Challenger"] = "挑战者",
        ["Merchant"] = "商人",
        ["Trader"] = "商贩",
        ["Grandmaster"] = "宗师",
        ["Champion"] = "冠军",
        ["Master"] = "大师",
        ["Wandering Challenger"] = "流浪挑战者",
        ["Seasoned Trader"] = "老练商贾",
        ["Gilded Merchant"] = "鎏金商豪",
        ["Diamond Grandmaster"] = "钻石宗师",
        ["Legendary Champion"] = "传奇冠军",
        ["Legendary of the Bazaar"] = "大巴扎传奇",
        ["Bronze Challenger"] = "青铜挑战者",
        ["Silver Trader"] = "白银商贩",
        ["Gold Merchant"] = "黄金商人",
    };

    private static readonly HashSet<string> KnownNoiseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        // 游戏模式后缀
        "Normal",
        "Ranked",
        "Unranked",
        "Casual",
        "Standard",
        "Practice",
        "PvP",
        // 幽灵池强度桶后缀
        "Weak",
        "Strong",
        "MetaBoss",
        "Meta",
        "Easy",
        "Hard",
        "Boss",
        "Default",
        "Fallback",
        "Ghost",
        "Bot",
        // 英文段位噪声后缀
        "Bronze",
        "Silver",
        "Gold",
        "Diamond",
        "Legend",
        "Legendary",
    };

    // 匹配尾部的半角 (...) 或全角 （...） 括号块
    private static readonly Regex TrailingParenRegex = new(
        @"\s*[\(\uFF08]([^\)\uFF09]{1,32})[\)\uFF09]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 匹配 "Bronze V" / "Diamond II" / "Legend 1" / "Legendary #12"
    private static readonly Regex RankWithDivisionRegex = new(
        @"^(Bronze|Silver|Gold|Diamond|Legendary|Legend)\s+([IVX]{1,4}|#?\d{1,5})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 匹配 "<Rank> <HeroName>"（如 "Bronze Vanessa"）
    private static readonly Regex LeadingRankHeroRegex = new(
        @"^(Bronze|Silver|Gold|Diamond|Legendary|Legend)(?:\s+[IVX]{1,4})?\s+(.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 匹配 "<HeroName> - <Rank>" / "<HeroName> · <Rank>"（如 "Vanessa - Diamond"）
    private static readonly Regex TrailingRankDashRegex = new(
        @"^(.+?)\s*[-\u2013\u2014\u00B7|]\s*(Bronze|Silver|Gold|Diamond|Legendary|Legend)(?:\s+[IVX]{1,4})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // 匹配 "<HeroName>镜像" 或 "<HeroName> Ghost"
    private static readonly Regex HeroMirrorSuffixRegex = new(
        @"^([A-Za-z0-9\s]+?)\s*(镜像|Ghost)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>供四层词典低优先兜底层复用的标准中文名牌/段位/头衔词表。</summary>
    public static IReadOnlyDictionary<string, string> BuiltInNameplateChineseEntries { get; } = BuildCombinedDictionary();

    private static Dictionary<string, string> BuildCombinedDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in TitleAndRankExactZhMap)
        {
            map[kv.Key] = kv.Value;
        }
        foreach (var kv in OpponentPlaceholderZhMap)
        {
            if (!map.ContainsKey(kv.Key))
            {
                map[kv.Key] = kv.Value;
            }
        }
        return map;
    }

    /// <summary>判断括号内文本是否属于已知可安全剥离的模式/强度桶/段位噪声词。</summary>
    public static bool IsKnownModeOrBucketNoiseToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;
        string trimmed = token.Trim();
        if (KnownNoiseTokens.Contains(trimmed)) return true;
        if (RankWithDivisionRegex.IsMatch(trimmed)) return true;
        return false;
    }

    /// <summary>
    /// 清洗并格式化 PvP 对手名牌名称（覆盖 SearchOpponentTransition、HeroBannerController、PVPOpponentTooltipData）。
    /// </summary>
    public static string FormatOpponentName(string rawName, bool isChineseLocale, bool isTournament = false)
    {
        if (string.IsNullOrEmpty(rawName) || !isChineseLocale)
        {
            return rawName ?? string.Empty;
        }

        string trimmed = rawName.Trim();
        if (trimmed.Length == 0 || string.Equals(trimmed, "******", StringComparison.Ordinal))
        {
            return rawName;
        }

        // 1. 循环剥离尾部白名单模式/强度桶/段位括号后缀（保留 Alice (TSM) 等非噪声括号）
        string cleaned = StripTrailingKnownNoiseParens(trimmed);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return trimmed;
        }

        // 2. 若形如 "Bronze Vanessa" 或 "Vanessa - Diamond"，仅当剩余主体确为已知英雄/占位名时才剥除段位词，避免误伤真实玩家昵称
        string strippedRankHero = TryExtractKnownHeroOrPlaceholderFromRankWrapped(cleaned);
        if (strippedRankHero != null)
        {
            return strippedRankHero;
        }

        // 3. 精确命中已知英文英雄名或官方英文占位名 -> 映射为统一清爽中文
        if (TryMapHeroOrPlaceholder(cleaned, out string mappedExact))
        {
            return mappedExact;
        }

        // 4. 命中 "<HeroName>镜像" / "<HeroName> Ghost"
        Match mirrorMatch = HeroMirrorSuffixRegex.Match(cleaned);
        if (mirrorMatch.Success)
        {
            string heroPart = mirrorMatch.Groups[1].Value.Trim();
            if (HeroNameZhMap.TryGetValue(heroPart, out string zhHero))
            {
                string suffix = mirrorMatch.Groups[2].Value;
                return string.Equals(suffix, "镜像", StringComparison.Ordinal)
                    ? zhHero + "镜像"
                    : zhHero;
            }
        }

        // 5. 其余均为真实玩家昵称（含锦标赛玩家/带战队标签名称/中文名）：仅保留剥离模式噪声后的干净昵称
        _ = isTournament;
        return cleaned;
    }

    /// <summary>
    /// 格式化 PvP VS 名牌头衔/段位标签（覆盖 HeroBannerController.SetHeroLabel 与段位文本）。
    /// </summary>
    public static string FormatTitleOrRankLabel(string rawLabel, bool isChineseLocale)
    {
        if (!isChineseLocale)
        {
            return rawLabel ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(rawLabel))
        {
            return "见习商人";
        }

        string cleaned = StripTrailingKnownNoiseParens(rawLabel.Trim());
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "见习商人";
        }

        if (TitleAndRankExactZhMap.TryGetValue(cleaned, out string exactZh))
        {
            return exactZh;
        }

        Match divMatch = RankWithDivisionRegex.Match(cleaned);
        if (divMatch.Success)
        {
            string rankEn = divMatch.Groups[1].Value;
            string divPart = divMatch.Groups[2].Value;
            if (RankTokenZhMap.TryGetValue(rankEn, out string rankZh))
            {
                return rankZh + " " + divPart;
            }
        }

        return cleaned;
    }

    private static string StripTrailingKnownNoiseParens(string input)
    {
        string current = input;
        for (int guard = 0; guard < 4; guard++)
        {
            Match m = TrailingParenRegex.Match(current);
            if (!m.Success) break;

            string inside = m.Groups[1].Value;
            if (!IsKnownModeOrBucketNoiseToken(inside)) break;

            string candidate = current.Substring(0, m.Index).TrimEnd();
            if (string.IsNullOrEmpty(candidate)) break;
            current = candidate;
        }
        return current;
    }

    private static string TryExtractKnownHeroOrPlaceholderFromRankWrapped(string text)
    {
        Match lead = LeadingRankHeroRegex.Match(text);
        if (lead.Success)
        {
            string rest = lead.Groups[2].Value.Trim();
            if (TryMapHeroOrPlaceholder(rest, out string zh))
            {
                return zh;
            }
        }

        Match trail = TrailingRankDashRegex.Match(text);
        if (trail.Success)
        {
            string head = trail.Groups[1].Value.Trim();
            if (TryMapHeroOrPlaceholder(head, out string zh))
            {
                return zh;
            }
        }

        return null;
    }

    private static bool TryMapHeroOrPlaceholder(string text, out string zh)
    {
        if (HeroNameZhMap.TryGetValue(text, out zh)) return true;
        if (OpponentPlaceholderZhMap.TryGetValue(text, out zh)) return true;
        zh = null;
        return false;
    }
}

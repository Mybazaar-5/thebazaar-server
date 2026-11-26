using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BazaarServer.Data;

/// <summary>官方白昼常规三槽位与特殊遭遇归属分类（基于 Patch 20.2 + BazaarDB 21.3 万局实测总账验证）。</summary>
public enum EOfficialEncounterSlot
{
    Unknown = 0,
    /// <summary>左槽：常规商人 + 白昼常规技能商（Nufu / Zurphin's Safari），日均 ~400%。</summary>
    LeftMerchant = 1,
    /// <summary>中槽：常规奇遇事件 + 稀有/特殊中槽奇遇，与地图基座共享日均 ~400% 配额。</summary>
    MiddleEvent = 2,
    /// <summary>中槽配额：4 座常规白昼地图基座（B1&amp;B2 / Sterling / Forja / The Artist）。</summary>
    MiddlePedestal = 3,
    /// <summary>右槽：37 组 4 阶免费物品遭遇（Patch 16.0 Right Side Options）。</summary>
    RightFreeItem = 4,
    /// <summary>右槽：10 组免费恢复 / 经济 / 成长保底选项（Regenerative Tincture / Snack Time / Rest / Study 等）。</summary>
    RightUtility = 5,
    /// <summary>远征 / 升级 / Fates 里程碑专属，不进入常规白昼三槽随机池。</summary>
    ExpeditionOrMilestone = 6,
}

/// <summary>
/// 单遭遇官方权威调度规则：天数窗（含封闭截止天/离散天）、小时禁令、英雄差异解锁天、槽位归属与英雄归一化实测日出现率（Day 1–10+）。
/// 数据源：docs/recon/official_events_spawn_registry.json（Patch 20.2 Hotfix 2026-11-20 + Patch 1.0–20.2 官方补丁全编年）。
/// </summary>
public sealed record OfficialEncounterRule(
    string NormalizedKey,
    string CanonicalName,
    EOfficialEncounterSlot Slot,
    int MinDay,
    int MaxDay,
    int[]? AllowedDays,
    int[]? DisallowedHours,
    bool DisallowDay1EarlyHours,
    IReadOnlyDictionary<string, int>? HeroMinDayOverrides,
    bool ExemptFromMaxDayTier,
    double[] DailyOfferedPct,
    string SourceNote);

/// <summary>
/// 《The Bazaar》官方 Patch 20.2 全量事件天数窗、三槽位归属、小时禁令与实测相对权重注册表。
/// 与野怪表 <see cref="OfficialDaySchedule"/> 对称，作为 ShopEngine 与 CardPool 的单一权威数据源。
/// </summary>
public static class OfficialEncounterSchedule
{
    private static readonly Regex SuffixStripRegex = new(
        @"\s*\((?:Bronze|Silver|Gold|Diamond|Legendary|Encounter|Event|Merchant|Hero Merchant|Default Option|Day [^)]+|Level Up|Vanessa|Dooley|Mak|Pygmalien|Stelle|Jules|Karnok|Dragons|Hero8)\)\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PrefixTagRegex = new(@"^\[[^\]]+\]\s*", RegexOptions.Compiled);
    private static readonly Regex NonAlnumRegex = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex ExplicitDayTagRegex = new(
        @"\(Day\s+(\d+)(?:(?:\s*-\s*|\s+to\s+)(\d+)|(\+))?\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Patch 20.2 官方 Day 1 合法放行的 10 位银阶商人 GUID（Curio/Midsworth/Quixel 为 Day 2+，Barkun/Silvia 为 Day 3+，Orion 为 Day 4+）。</summary>
    public static readonly IReadOnlySet<string> Day1AllowedSilverMerchantIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "e3b894b0-a078-4b1d-8793-ac6459a8361a", // Valpak
        "2708055d-a8be-4c2f-a8d6-574e884a254e", // Herma
        "9459a01b-edf5-432b-81cd-1fd2cacfadad", // Shelter Shelby (Merchant)
        "41cdf4ee-7790-4da1-96f8-a46d3391a0d9", // The Tester
        "b1b1f445-e625-4397-b4e2-ee4feeb729fa", // Tinker
        "0ff275f7-60ef-4428-acb6-1a138d03152d", // Eli
        "816e6ba0-8f5f-412e-9756-8e1901dd9d49", // Colt
        "1a21931f-6183-4251-914b-c8f83dc58a30", // Nautica
        "847b1294-649d-4f02-ab04-0ee9f838c462", // Aero
        "a8c54f28-4d97-49fc-8186-0ab412d3577a", // Laurel
    };

    /// <summary>右槽 4 阶免费物品遭遇中，Silver 变体自 Day 1 起（Days 1–5）即可登场的套组集合；其余套组 Silver 自 Day 2 起（Days 2–6）。</summary>
    public static readonly IReadOnlySet<string> SilverDay1RightFreeSuites = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "battlefield",
        "cabinfishing",
        "cinderchase",
        "cupboard",
        "extractextract",
        "furnace",
        "guardlocker",
        "houseparty",
        "lostandfound",
        "medicinecabinet",
        "procuremedkit",
        "scrapsalvage",
        "sharpeningkit",
        "techsalvage",
        "treasurechest",
        "trufflefarming",
        "utilitybox",
    };

    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyfurrycreature"] = "tinyfurrymonster",
        ["docks"] = "thedocks",
        ["lostcrate"] = "thelostcrate",
        ["dfleck"] = "dflek",
        ["dooleysworkshop"] = "dooleyscrib",
        ["relax"] = "rest",
        ["b1andb2"] = "b1b2",
    };

    /// <summary>全量官方遭遇规则字典（键为归一化名称）。</summary>
    public static readonly IReadOnlyDictionary<string, OfficialEncounterRule> RulesByKey = BuildRules();

    private static readonly ConcurrentDictionary<string, string> NormalizedKeyCache = new(StringComparer.Ordinal);

    /// <summary>
    /// 规范化遭遇名称（去除品阶/英雄/天数括号后缀与标点），并解析已知别名（如 Tiny Furry Creature -> tinyfurrymonster、Relax -> rest）。
    /// 结果按原串缓存，避免白昼发牌热路径反复触发正则替换。
    /// </summary>
    public static string NormalizeKey(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return string.Empty;
        return NormalizedKeyCache.GetOrAdd(rawName, ComputeNormalizedKey);
    }

    private static string ComputeNormalizedKey(string rawName)
    {
        string s = rawName.Trim().Replace('\u2019', '\'').Replace('\u00e9', 'e').Replace('\u00e8', 'e');
        s = PrefixTagRegex.Replace(s, string.Empty);
        // 循环剥离多层括号后缀（如 "Kev's Armory (Vanessa)" / "Borrow (Day 1-2)"）
        for (int i = 0; i < 3; i++)
        {
            string next = SuffixStripRegex.Replace(s, " ").Trim();
            if (next.Length == s.Length) break;
            s = next;
        }
        string k = NonAlnumRegex.Replace(s.ToLowerInvariant(), string.Empty);
        return KeyAliases.TryGetValue(k, out var canonical) ? canonical : k;
    }

    /// <summary>按模板查询官方遭遇调度规则；未收录的自定义 Mod 遭遇返回 false（不施加额外封锁）。</summary>
    public static bool TryGetRule(CardPool.CardTemplate? tpl, out OfficialEncounterRule? rule)
    {
        rule = null;
        if (tpl == null) return false;

        // 明确按 GUID 精确映射 4 座地图基座与核心奇遇，避免名称歧义
        if (!string.IsNullOrWhiteSpace(tpl.Id))
        {
            if (tpl.Id.Equals(CardPool.PedestalB1B2Id, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("b1b2", out rule);
            if (tpl.Id.Equals(CardPool.PedestalSterlingId, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("sterling", out rule);
            if (tpl.Id.Equals(CardPool.PedestalForjaId, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("forja", out rule);
            if (tpl.Id.Equals(CardPool.PedestalTheArtistId, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("theartist", out rule);
            if (tpl.Id.Equals(CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("thetravelagent", out rule);
            if (tpl.Id.Equals(CardPool.GrandOpeningEncounterId, StringComparison.OrdinalIgnoreCase))
                return RulesByKey.TryGetValue("grandopening", out rule);
        }

        string key = NormalizeKey(tpl.InternalName);
        if (string.IsNullOrEmpty(key)) return false;
        if (RulesByKey.TryGetValue(key, out rule))
        {
            // Partner Portal 同时存在中槽 EventEncounter 与 8 张 PedestalEncounter 子基座；仅主事件归中槽
            if (key == "partnerportal" && string.Equals(tpl.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
            {
                rule = null;
                return false;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 校验遭遇是否处于 Patch 20.2 官方允许的天数窗与小时窗内。
    /// </summary>
    /// <param name="tpl">候选遭遇模板</param>
    /// <param name="day">当前天数（1-based）</param>
    /// <param name="hour">当前小时（1-based：1, 2, 4, 5 为白昼常规小时）</param>
    /// <param name="hero">当前英雄名（用于 Pol 的 Pyg Day 4+ / 其他 Day 6+ 等英雄差异门控）</param>
    /// <param name="allowAmplifiedTravelAgent">当自定义规则显式拉高 EventExpeditionAgent (&gt;1.0) 时放宽 The Travel Agent 至 Day 2–9</param>
    public static bool IsWithinWindow(
        CardPool.CardTemplate? tpl,
        int day,
        int hour,
        string? hero = null,
        bool allowAmplifiedTravelAgent = false)
    {
        if (tpl == null) return true;

        // 显式分天变体后缀门控（如 "(Day 1 to 2)" / "(Day 3 to 4)" / "(Day 5+)" / "(Day 7-9)" / "(Day 10)"），
        // 严防高天变体（例如 Cache of Riches (Day 5+)）因主规则 MinDay=1 而越界漏到早期天数
        if (!string.IsNullOrWhiteSpace(tpl.InternalName) && tpl.InternalName.Contains("(Day ", StringComparison.OrdinalIgnoreCase))
        {
            var m = ExplicitDayTagRegex.Match(tpl.InternalName);
            if (m.Success)
            {
                int tagMin = int.Parse(m.Groups[1].Value);
                int tagMax = m.Groups[2].Success
                    ? int.Parse(m.Groups[2].Value)
                    : (m.Groups[3].Success || tagMin >= 10 ? int.MaxValue : tagMin);
                if (day < tagMin || day > tagMax)
                    return false;
            }
        }

        if (TryGetRule(tpl, out var rule) && rule != null)
        {
            if (allowAmplifiedTravelAgent && rule.NormalizedKey == "thetravelagent")
            {
                return day is >= 2 and <= 9;
            }

            // 同名多品阶 Always 奇遇（Gunpowder Cache / Moonlit Meadow）按 BazaarDB 实测分阶窗口门控：
            // (Silver) 限 Days 1–5，(Gold)/(Diamond) 限 Day 5+，防止 Day 1–4 越阶刷出高阶变体
            if (rule.NormalizedKey is "gunpowdercache" or "moonlitmeadow" && !string.IsNullOrWhiteSpace(tpl.InternalName))
            {
                if (tpl.InternalName.Contains("(Silver)", StringComparison.OrdinalIgnoreCase) && day > 5)
                    return false;
                if ((tpl.InternalName.Contains("(Gold)", StringComparison.OrdinalIgnoreCase) ||
                     tpl.InternalName.Contains("(Diamond)", StringComparison.OrdinalIgnoreCase)) && day < 5)
                    return false;
            }

            int effectiveMinDay = rule.MinDay;
            if (rule.HeroMinDayOverrides != null)
            {
                if (!string.IsNullOrWhiteSpace(hero) && rule.HeroMinDayOverrides.TryGetValue(hero, out int heroMin))
                {
                    effectiveMinDay = heroMin;
                }
                else if (string.IsNullOrWhiteSpace(hero) && tpl.Heroes is { Length: > 0 } &&
                         tpl.Heroes.Length == 1 && rule.HeroMinDayOverrides.TryGetValue(tpl.Heroes[0], out int tplHeroMin))
                {
                    effectiveMinDay = tplHeroMin;
                }
            }

            if (day < effectiveMinDay || day > rule.MaxDay)
                return false;

            if (rule.AllowedDays is { Length: > 0 } && !Array.Exists(rule.AllowedDays, d => d == day))
                return false;

            if (rule.DisallowDay1EarlyHours && day == 1 && hour <= 2)
                return false;

            if (rule.DisallowedHours is { Length: > 0 } && Array.Exists(rule.DisallowedHours, h => h == hour))
                return false;

            return true;
        }

        // 兜底：未收录在官方规则表中的未知银阶及以上商人（如新加或未映射模板）在 Day 1 仅放行显式白名单
        bool isMerchant = tpl.Tags != null && tpl.Tags.Any(t => string.Equals(t, "Merchant", StringComparison.OrdinalIgnoreCase));
        if (day <= 1 && isMerchant && !string.Equals(tpl.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
        {
            return Day1AllowedSilverMerchantIds.Contains(tpl.Id);
        }

        return true;
    }

    /// <summary>判定遭遇归属哪个官方白昼槽位。</summary>
    public static EOfficialEncounterSlot ClassifySlot(CardPool.CardTemplate? tpl, bool isMerchantFallback)
    {
        if (tpl == null) return EOfficialEncounterSlot.Unknown;
        if (TryGetRule(tpl, out var rule) && rule != null)
            return rule.Slot;
        if (string.Equals(tpl.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
            return EOfficialEncounterSlot.MiddlePedestal;
        if (isMerchantFallback)
            return EOfficialEncounterSlot.LeftMerchant;
        return EOfficialEncounterSlot.MiddleEvent;
    }

    /// <summary>判断是否为 Partner Portal 动态解锁的 8 座英雄升级子基座（Day 2–10+ 入中槽基座池）。</summary>
    public static bool IsPartnerPortalUpgradePedestal(CardPool.CardTemplate? tpl)
        => tpl != null
        && string.Equals(tpl.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(tpl.InternalName)
        && tpl.InternalName.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 计算指定遭遇在指定天数的单小时抽签权重：
    /// 采用 Plackett-Luce 一阶精确 Hazard 变换 <c>w = -ln(1 - min(p, 0.96))</c>，
    /// 使 4 次无放回同日去重抽签的日级出现率逼近 BazaarDB 21.3 万局实测曲线（MAE 0.7–1.4 pp）。
    /// </summary>
    public static double GetEmpiricalDrawWeight(CardPool.CardTemplate? tpl, int day, double fallbackWeight = 1.0)
    {
        if (tpl == null) return fallbackWeight;
        if (IsPartnerPortalUpgradePedestal(tpl))
        {
            // Partner Portal - Forja 子基座按日级参考概率 4.5% 折算 Hazard 权重，避免回退 1.0 挤占 B1&B2 / Forja / The Artist
            return day >= 2 ? -Math.Log(1.0 - 0.045) : 0.0;
        }
        if (!TryGetRule(tpl, out var rule) || rule == null || rule.DailyOfferedPct.Length == 0)
            return fallbackWeight;

        int idx = Math.Clamp(day, 1, rule.DailyOfferedPct.Length) - 1;
        double pct = rule.DailyOfferedPct[idx];
        if (pct <= 0.0)
        {
            // 若因自定义规则放宽（如 amplified Travel Agent）在原 0% 天入池，回退至该卡非零均值
            double sum = 0.0;
            int cnt = 0;
            foreach (double v in rule.DailyOfferedPct)
            {
                if (v > 0.0) { sum += v; cnt++; }
            }
            pct = cnt > 0 ? sum / cnt : 2.0;
        }

        double p = Math.Clamp(pct / 100.0, 0.002, 0.96);
        return -Math.Log(1.0 - p);
    }

    /// <summary>
    /// 根据当前剩余未访问的合法常规地图基座列表，动态折算本小时中槽刷出基座的触发概率：
    /// <c>p_hour = 1 - (1 - sum(DailyOfferedPct[day]))^(1/4)</c>。
    /// 彻底避免 Day 8 抽走 Sterling 后剩余小时仍用 24.8% 高概率强刷 The Artist（使 Artist 精确保持 ~1.8%），
    /// 并允许 Day 2 动态解锁的 Partner Portal - Forja 子基座正常出池。
    /// </summary>
    public static double ComputeDynamicMiddlePedestalHourChance(IReadOnlyList<CardPool.CardTemplate> remainingPedestals, int day)
    {
        if (remainingPedestals == null || remainingPedestals.Count == 0 || day <= 1)
            return 0.0;

        double sumPct = 0.0;
        int idx = Math.Clamp(day, 1, 10) - 1;
        foreach (var ped in remainingPedestals)
        {
            if (TryGetRule(ped, out var rule) && rule != null && rule.Slot == EOfficialEncounterSlot.MiddlePedestal)
            {
                sumPct += Math.Max(0.0, rule.DailyOfferedPct[idx]);
            }
            else if (IsPartnerPortalUpgradePedestal(ped))
            {
                if (day >= 2)
                    sumPct += 4.5;
            }
            else
            {
                // 自定义 Mod 基座默认给 5% 日级参考概率（仅 Day 3+ 常规基座日窗起）
                if (day >= 3)
                    sumPct += 5.0;
            }
        }
        if (sumPct <= 0.0) return 0.0;
        double clampedDaily = Math.Clamp(sumPct / 100.0, 0.0, 0.90);
        return 1.0 - Math.Pow(1.0 - clampedDaily, 0.25);
    }

    /// <summary>判断右槽 4 阶免费物品套组在指定天数是否应优先投射 Silver 变体（供 CardPool.GuidOnlyHourlyEncounterCatalog 使用）。</summary>
    public static bool IsSilverDay1RightFreeSuite(string baseName)
        => SilverDay1RightFreeSuites.Contains(NormalizeKey(baseName));

    /// <summary>
    /// 返回当日匹配的右槽分天经济卡 GUID（Candy Stash / Look for Spare Change / Cache of Riches）。
    /// 这三套右槽高频经济卡在 DB 中为 GuidOnly 且部分被事件引用或使用 "(Day 1 to 2)" 后缀，由此统一按天精准投影进右槽。
    /// </summary>
    public static IReadOnlyList<string> GetRightSlotEconomyIdsForDay(int day)
    {
        string candyId = day switch
        {
            <= 3 => "e6e09e6a-ebe0-46f8-bfbb-716b74f78bc6", // Candy Stash (Day 1-3)
            <= 6 => "33a881fa-66fc-4a98-8744-de38c1d44baf", // Candy Stash (Day 4-6)
            <= 9 => "384bd291-99b1-4736-8cbc-ebdac4d11d63", // Candy Stash (Day 7-9)
            _ => "cbcb84f9-631a-4d85-bed3-cfd2f86fb8d6",    // Candy Stash (Day 10)
        };
        string spareChangeId = day switch
        {
            <= 3 => "cbe0d294-09db-47ea-a7e8-8127605c1173", // Look for Spare Change (Day 1-3)
            <= 6 => "39961216-13c2-4013-8da5-6435a86e0e76", // Look for Spare Change (Day 4-6)
            <= 9 => "ef60b13f-35cb-49ef-bf9c-55847e3ab9ce", // Look for Spare Change (Day 7-9)
            _ => "b3f47a45-98bd-4200-9e94-5ddbc32e60cf",    // Look for Spare Change (Day 10)
        };
        string cacheId = day switch
        {
            <= 2 => "937489c5-22bf-4190-89b1-f396894c85f1", // Cache of Riches (Day 1 to 2)
            <= 4 => "7d832f18-dd4c-4d67-8e4f-573618f7fa38", // Cache of Riches (Day 3 to 4)
            _ => "af188bbf-4e10-42c8-803c-12594f4d80d7",    // Cache of Riches (Day 5+)
        };
        return [candyId, spareChangeId, cacheId];
    }

    private static IReadOnlyDictionary<string, OfficialEncounterRule> BuildRules()
    {
        var d = new Dictionary<string, OfficialEncounterRule>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, string name, EOfficialEncounterSlot slot, int minDay, int maxDay, int[]? allowedDays, int[]? disallowedHours, bool disallowD1Early, IReadOnlyDictionary<string, int>? heroMinDays, bool exemptTier, double[] dailyPct, string note)
            => d[key] = new OfficialEncounterRule(key, name, slot, minDay, maxDay, allowedDays, disallowedHours, disallowD1Early, heroMinDays, exemptTier, dailyPct, note);

        Add("aero", "Aero", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 30.70, 21.60, 19.00, 15.70, 17.80, 11.70, 13.40, 13.40, 14.00, 13.70 }, "BazaarDB 实测 Day 1+");
        Add("aila", "Aila", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 59.68, 50.74, 45.64, 43.60, 45.13, 38.57, 42.20, 42.15, 38.88, 40.62 }, "Bronze 全天");
        Add("aimbot", "Aimbot", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.00, 5.43, 5.67, 4.99, 5.46 }, "Patch 7.0/15.0 Day 6+");
        Add("ande", "Ande", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 60.31, 49.52, 45.63, 43.84, 44.48, 38.44, 42.44, 43.25, 39.97, 40.37 }, "Bronze 全天");
        Add("barkun", "Barkun", EOfficialEncounterSlot.LeftMerchant, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 19.12, 16.34, 16.57, 12.47, 14.01, 14.22, 12.79, 13.21 }, "BazaarDB 实测 Day 3+");
        Add("chronos", "Chronos", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.48, 1.78, 2.01, 1.61, 1.80 }, "BazaarDB 实测 Day 6+ (D5 仅 0.3% Portal 噪点)");
        Add("cobweb", "Cobweb", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.51, 1.85, 1.55, 1.70, 1.80 }, "BazaarDB 实测 Day 6+");
        Add("colt", "Colt", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 30.12, 22.50, 19.46, 16.43, 16.31, 13.63, 13.45, 14.46, 12.62, 12.68 }, "BazaarDB 实测 Day 1+");
        Add("curio", "Curio", EOfficialEncounterSlot.LeftMerchant, 2, 5, null, null, false, null, true, new double[] { 0.00, 22.56, 18.97, 16.59, 16.21, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 9.2 移出 Day 1，主出 Days 2–5");
        Add("dooley", "Dooley", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.88, 1.73, 1.80, 1.70, 1.89 }, "跨英雄钻商 Day 6+");
        Add("eli", "Eli", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 32.12, 21.81, 18.19, 16.00, 16.94, 13.50, 12.94, 13.56, 11.81, 15.25 }, "Patch 7.0 Day 1+");
        Add("flex", "Flex", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.38, 1.58, 1.58, 1.65, 1.77 }, "BazaarDB 实测 Day 6+");
        Add("freiya", "Freiya", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.52, 1.80, 1.87, 1.69, 1.81 }, "BazaarDB 实测 Day 6+");
        Add("gaseo", "Gaseo", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.02, 5.49, 5.71, 5.22, 5.59 }, "BazaarDB 实测 Day 6+");
        Add("gastro", "Gastro", EOfficialEncounterSlot.LeftMerchant, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 23.23, 19.52, 14.35, 15.16, 12.74, 16.77, 12.90, 13.87, 11.61 }, "BazaarDB 实测 Day 2+");
        Add("goldie", "Goldie", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.08, 5.59, 6.04, 5.34, 5.44 }, "BazaarDB 实测 Day 6+");
        Add("hef", "Hef", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.69, 1.76, 1.87, 1.64, 1.95 }, "BazaarDB 实测 Day 6+");
        Add("herma", "Herma", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 36.20, 24.65, 18.26, 16.16, 16.85, 13.03, 14.15, 14.63, 13.65, 13.94 }, "Patch 7.0/9.0 Day 1+");
        Add("jayjay", "Jay Jay", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 58.90, 50.93, 45.21, 44.79, 45.29, 38.50, 41.81, 41.82, 39.39, 40.68 }, "Bronze 全天");
        Add("jules", "Jules", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.52, 1.56, 1.55, 1.60, 1.80 }, "跨英雄钻商 Day 6+");
        Add("karnok", "Karnok", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.61, 2.11, 1.50, 1.52, 1.72 }, "跨英雄钻商 Day 6+");
        Add("kevsarmory", "Kev's Armory", EOfficialEncounterSlot.LeftMerchant, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 16.04, 12.14, 10.96, 11.70, 10.52, 12.90, 11.95, 11.08, 11.57 }, "BazaarDB 实测 Day 2+ (Vanessa 变体 Day 6+ 或通用 Day 2+)");
        Add("kina", "Kina", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 59.46, 49.96, 45.73, 43.80, 43.54, 38.32, 42.39, 42.52, 39.00, 40.30 }, "Bronze 全天");
        Add("knightshade", "Knightshade", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.55, 1.63, 1.51, 1.57, 1.80 }, "BazaarDB 实测 Day 6+");
        Add("laurel", "Laurel", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 35.65, 20.97, 19.52, 15.16, 18.06, 11.94, 15.65, 13.55, 13.55, 12.74 }, "BazaarDB 实测 Day 1+");
        Add("luxe", "Luxe", EOfficialEncounterSlot.LeftMerchant, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.73, 1.58, 1.87 }, "BazaarDB 实测常态 Day 8+");
        Add("mak", "Mak", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.64, 1.71, 1.71, 1.73, 1.83 }, "跨英雄钻商 Day 6+");
        Add("midsworth", "Midsworth", EOfficialEncounterSlot.LeftMerchant, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 22.55, 18.43, 16.30, 15.98, 12.62, 14.48, 13.66, 12.70, 13.63 }, "Patch 7.0 Day 2+");
        Add("mittel", "Mittel", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 59.90, 50.00, 45.89, 43.71, 44.43, 38.62, 41.96, 42.99, 40.06, 40.55 }, "Bronze 全天");
        Add("mrmorland", "Mr. Morland", EOfficialEncounterSlot.LeftMerchant, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 19.53, 13.65, 15.88, 13.65, 15.00, 13.31, 14.19, 13.38 }, "Patch 14.0 Day 3+");
        Add("nautica", "Nautica", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 28.81, 21.67, 19.94, 15.71, 17.68, 12.26, 14.82, 13.93, 11.73, 12.50 }, "Patch 7.0 Day 1+");
        Add("orion", "Orion", EOfficialEncounterSlot.LeftMerchant, 4, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 15.81, 16.75, 13.21, 13.95, 14.02, 13.14, 13.34 }, "BazaarDB 实测 Day 4+");
        Add("pinfeather", "Pinfeather", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 4.83, 5.67, 5.67, 5.06, 5.64 }, "BazaarDB 实测 Day 6+");
        Add("pol", "Pol", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Pygmalien"] = 4 }, true, new double[] { 0.00, 0.00, 0.00, 2.51, 2.24, 11.17, 12.51, 12.29, 10.60, 11.84 }, "Patch 17.0：Pyg Day 4+，其余英雄 Day 6+");
        Add("privatepitchfork", "Private Pitchfork", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.07, 5.68, 5.60, 5.13, 5.79 }, "Patch 16.0 Day 6+");
        Add("prospero", "Prospero", EOfficialEncounterSlot.LeftMerchant, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 17.64, 15.74, 15.47, 14.19, 13.04, 15.47, 12.91, 13.65 }, "Hotfix 13.2 Day 3+");
        Add("pygmalien", "Pygmalien", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.53, 1.95, 1.75, 1.91, 1.88 }, "跨英雄钻商 Day 6+");
        Add("quixel", "Quixel", EOfficialEncounterSlot.LeftMerchant, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 22.77, 18.49, 16.06, 17.11, 12.52, 14.27, 13.61, 13.35, 13.54 }, "Patch 7.0 Day 2+");
        Add("serafina", "Serafina", EOfficialEncounterSlot.LeftMerchant, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.83, 1.82 }, "BazaarDB 实测常态 Day 8+");
        Add("sheltershelby", "Shelter Shelby", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 33.88, 25.29, 19.76, 15.85, 17.02, 12.55, 14.71, 15.16, 13.40, 14.23 }, "Patch 14.0 Day 1+");
        Add("silvia", "Silvia", EOfficialEncounterSlot.LeftMerchant, 3, 5, null, null, false, null, true, new double[] { 0.00, 0.00, 18.66, 16.54, 17.22, 0.00, 0.00, 0.00, 0.00, 0.00 }, "BazaarDB 实测主出 Days 3–5");
        Add("stelle", "Stelle", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.67, 2.00, 1.78, 1.63, 1.80 }, "跨英雄钻商 Day 6+");
        Add("stickybeans", "Stickybeans", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.26, 5.67, 5.65, 5.33, 5.50 }, "Patch 16.0 Day 6+");
        Add("tatiana", "Tatiana", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 4.70, 5.39, 5.55, 5.32, 5.41 }, "BazaarDB 实测 Day 6+");
        Add("theantiquarian", "The Antiquarian", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 4.62, 5.31, 5.67, 5.24, 5.26 }, "Patch 8.0/15.0 Day 6+");
        Add("thedragons", "The Dragons", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.84, 1.94, 1.59, 1.57, 1.79 }, "跨英雄钻商 Day 6+");
        Add("thetester", "The Tester", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 27.74, 20.11, 17.26, 15.60, 15.51, 14.11, 14.09, 13.71, 12.31, 13.91 }, "Patch 16.0 Day 1+ (Dooley/Stelle)");
        Add("tinker", "Tinker", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 34.61, 22.34, 18.77, 16.17, 16.36, 12.34, 14.35, 12.99, 13.31, 14.09 }, "Patch 7.0 Day 1+");
        Add("toksclocks", "Tok's Clocks", EOfficialEncounterSlot.LeftMerchant, 4, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 16.31, 15.14, 9.57, 10.65, 9.77, 8.08, 7.19 }, "BazaarDB 实测 Day 4+，单局唯一");
        Add("uitarcenter", "Uitar Center", EOfficialEncounterSlot.LeftMerchant, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 18.44, 15.10, 15.10, 13.44, 15.00, 15.21, 12.71, 13.75 }, "Patch 20.0 修复为 Day 3+");
        Add("valpak", "Valpak", EOfficialEncounterSlot.LeftMerchant, 1, int.MaxValue, null, null, false, null, true, new double[] { 33.77, 18.24, 18.49, 16.18, 16.80, 12.84, 14.30, 13.90, 12.96, 13.65 }, "Patch 9.2 全天刷新，必带25%折扣");
        Add("vanessa", "Vanessa", EOfficialEncounterSlot.LeftMerchant, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.85, 1.63, 1.68, 1.60, 1.77 }, "跨英雄钻商 Day 6+");
        Add("nufu", "Nufu", EOfficialEncounterSlot.LeftMerchant, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 8.62, 9.89, 6.79, 5.61, 5.98, 6.69, 6.78, 6.89 }, "BazaarDB 实测常规白昼技能商 Day 3+");
        Add("zurphinssafari", "Zurphin's Safari", EOfficialEncounterSlot.LeftMerchant, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 1.92, 1.79, 1.93, 1.96, 1.64, 1.86 }, "BazaarDB 实测常规白昼技能商 Day 5+");
        Add("astrangemushroom", "A Strange Mushroom", EOfficialEncounterSlot.MiddleEvent, 1, 3, null, null, false, null, true, new double[] { 89.67, 78.00, 66.50, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 15.0 Days 1–3");
        Add("advancedtraining", "Advanced Training", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.50, 1.59, 1.85, 1.59, 1.34 }, "Patch 7.0 Day 6+");
        Add("aerodrome", "Aerodrome", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 6.83, 5.46, 5.72, 6.01, 6.81, 6.80 }, "Patch 8.0 Day 5+");
        Add("aldric", "Aldric", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.64, 2.01, 1.78 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("apothecary", "Apothecary", EOfficialEncounterSlot.MiddleEvent, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 8.66, 9.74, 6.55, 5.58, 6.14, 6.41, 7.05, 7.10 }, "Patch 15.0 Day 3+");
        Add("artisandunes", "Artisan Dunes", EOfficialEncounterSlot.MiddleEvent, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 8.65, 10.26, 6.61, 5.88, 5.87, 5.98, 6.60, 7.09 }, "BazaarDB 实测 Day 3+");
        Add("auroradome", "Aurora Dome", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.73, 1.70, 1.64 }, "Patch 16.0 Day 8+，单局唯一");
        Add("bazaarcon", "BazaarCON", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.68, 6.12, 6.47, 7.16, 7.18 }, "BazaarDB 实测 Day 6+");
        Add("bex", "Bex", EOfficialEncounterSlot.MiddleEvent, 2, 5, null, new int[] { 4, 5 }, false, null, true, new double[] { 0.00, 16.94, 8.48, 9.19, 5.74, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 7.0 & 10-10 Hotfix：Days 2–5，单局 1 次，禁 Hour 4–5");
        Add("botul", "Botul", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.73, 2.09, 1.63 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("bountyhunters", "Bounty Hunters", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.79, 1.49, 1.51, 1.51, 1.25 }, "BazaarDB 实测 Day 6+ (Vanessa)");
        Add("burninglibrary", "Burning Library", EOfficialEncounterSlot.MiddleEvent, 4, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.50, 0.50, 0.50, 0.50, 0.42, 0.41, 0.49 }, "BazaarDB 实测 Day 4+ (事件链动态解锁)");
        Add("concerthall", "Concert Hall", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70 }, "Patch 18.0 Day 8+ (Dragons)，单局唯一");
        Add("dflek", "D'flek", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.76, 1.67, 1.50 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("dabora", "Dabora", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.34, 5.57, 5.74, 6.45, 5.76 }, "BazaarDB 实测 Day 6+，单局唯一");
        Add("deadlyduel", "Deadly Duel", EOfficialEncounterSlot.MiddleEvent, 5, 7, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 5.98, 4.95, 6.05, 0.00, 0.00, 0.00 }, "DB 分天三卡 Days 5–7");
        Add("dooleyscrib", "Dooley's Crib", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.56, 2.08, 1.56, 1.56, 1.04 }, "BazaarDB 实测 Day 6+ (Dooley)");
        Add("eatingcontest", "Eating Contest", EOfficialEncounterSlot.MiddleEvent, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 8.64, 10.17, 6.55, 5.59, 5.97, 6.33, 6.75, 6.81 }, "BazaarDB 实测 Day 3+");
        Add("economicseminar", "Economic Seminar", EOfficialEncounterSlot.MiddleEvent, 3, 5, null, null, false, null, true, new double[] { 0.00, 0.00, 23.65, 53.11, 43.51, 0.00, 0.00, 0.00, 0.00, 0.00 }, "BazaarDB 实测 Days 3–5 (Pyg)");
        Add("epicbattle", "Epic Battle", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 5.59, 6.81, 6.54 }, "DB 分天三卡 Day 8+");
        Add("familyreunion", "Family Reunion", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70 }, "BazaarDB 实测 Day 8+ (Stelle)");
        Add("farai", "Farai", EOfficialEncounterSlot.MiddleEvent, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 16.43, 8.83, 9.80, 6.68, 5.49, 6.32, 6.25, 6.85, 7.06 }, "Patch 15.0 Day 2+");
        Add("farmersmarket", "Farmer's Market", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70 }, "Patch 16.0 Day 8+ (Jules)，单局唯一");
        Add("finnsbigbite", "Finn's Big Bite", EOfficialEncounterSlot.MiddleEvent, 1, int.MaxValue, null, null, false, null, true, new double[] { 32.96, 17.03, 8.35, 9.90, 6.63, 5.38, 6.32, 6.29, 6.81, 6.96 }, "BazaarDB 实测 All Days (D1 33%)");
        Add("flambe", "Flambe", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.69, 1.70, 1.71 }, "BazaarDB 实测常态 Day 8+，单局唯一");
        Add("form", "Form", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.07, 1.07, 1.71, 1.67, 1.48 }, "Patch 2.0/实测 Day 6+ (常态 D8+)，单局唯一");
        Add("froghollow", "Frog Hollow", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.68, 1.81, 1.55 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("grandopening", "Grand Opening", EOfficialEncounterSlot.MiddleEvent, 3, 9, null, null, false, null, true, new double[] { 0.00, 0.00, 3.09, 3.45, 2.22, 1.55, 1.71, 1.45, 1.44, 0.00 }, "BazaarDB 实测 Days 3–9");
        Add("grandmaster", "Grandmaster", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 6.61, 5.73, 5.80, 6.47, 6.54, 6.93 }, "BazaarDB 实测 Day 5+");
        Add("gumballmachine", "Gumball Machine", EOfficialEncounterSlot.MiddleEvent, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 28.56, 59.79, 48.34, 44.18, 48.31, 65.77, 73.03, 75.68 }, "BazaarDB 实测 Day 3+");
        Add("gunpowdercache", "Gunpowder Cache", EOfficialEncounterSlot.MiddleEvent, 1, int.MaxValue, null, null, false, null, true, new double[] { 7.70, 6.20, 10.05, 12.75, 15.00, 3.16, 5.18, 6.55, 5.61, 6.11 }, "Patch 15.0 All Days");
        Add("haddy", "Haddy", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.53, 1.83, 1.59 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("investmentpitch", "Investment Pitch", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.58, 2.09, 1.58, 1.58, 1.08 }, "BazaarDB 实测 Day 6+ (Pyg)");
        Add("julescafe", "Jules' Cafe", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 47.96, 45.08, 47.85, 66.04, 73.76, 75.90 }, "BazaarDB 实测 Day 5+");
        Add("jungleruins", "Jungle Ruins", EOfficialEncounterSlot.MiddleEvent, 1, 3, null, null, false, null, true, new double[] { 89.52, 77.98, 66.21, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 15.0 Days 1–3");
        Add("launchtower", "Launch Tower", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70 }, "Patch 16.0 Day 8+ (Stelle)，单局唯一");
        Add("laurelsnightterrors", "Laurel's Night Terrors", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70, 1.70, 1.70 }, "BazaarDB 实测 Day 6+ (Jules)");
        Add("ledger", "Ledger", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70 }, "Patch 17.0 / 实测后期 (Pyg)，单局唯一");
        Add("library", "Library", EOfficialEncounterSlot.MiddleEvent, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 8.88, 9.65, 6.79, 5.11, 5.43, 6.41, 6.66, 6.47 }, "Patch 16.0 Day 3+");
        Add("likit", "Likit", EOfficialEncounterSlot.MiddleEvent, 2, 5, null, null, false, null, true, new double[] { 0.00, 44.25, 25.44, 51.84, 38.75, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 17.0 Days 2–5，单局唯一");
        Add("madmaddie", "Mad Maddie", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.73, 1.77 }, "BazaarDB 实测 Day 8+，单局唯一");
        Add("mandala", "Mandala", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 44.45, 48.59, 65.30, 72.68, 75.69 }, "Patch 5.1 Day 6+");
        Add("monsterranch", "Monster Ranch", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 6.45, 6.72, 7.03 }, "BazaarDB 实测 Day 8+");
        Add("moonlitmeadow", "Moonlit Meadow", EOfficialEncounterSlot.MiddleEvent, 1, int.MaxValue, null, null, false, null, true, new double[] { 9.32, 7.03, 9.55, 12.61, 16.13, 2.93, 6.22, 6.53, 5.86, 5.99 }, "Patch 15.0 All Days");
        Add("mountainpass", "Mountain Pass", EOfficialEncounterSlot.MiddleEvent, 4, 7, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 59.27, 47.69, 43.93, 48.10, 0.00, 0.00, 0.00 }, "Patch 2.5 + 实测 Days 4–7");
        Add("mysteriousportal", "Mysterious Portal", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 5.59, 5.74, 5.91, 6.49, 6.44 }, "BazaarDB 实测 Day 6+");
        Add("mysterybox", "Mystery Box", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.26, 0.81, 0.99 }, "BazaarDB 实测常态 Day 8+");
        Add("partnerportal", "Partner Portal", EOfficialEncounterSlot.MiddleEvent, 2, 5, null, null, false, null, true, new double[] { 0.00, 4.55, 2.49, 2.61, 1.75, 0.00, 0.00, 0.00, 0.00, 0.00 }, "BazaarDB 21.3万局实测 Day 2–5（Day 1 为 0%；Patch 20.0 公告称 Days 1–5）");
        Add("pearlsdigsite", "Pearl's Dig Site", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 6.62, 5.54, 6.26, 6.60, 6.91, 7.10 }, "BazaarDB 实测 Day 5+");
        Add("photobooth", "Photo Booth", EOfficialEncounterSlot.MiddleEvent, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 4.87, 2.55, 2.77, 2.10, 1.54, 2.05, 1.76, 1.83, 1.79 }, "Patch 18.0 Day 3+ / 实测 Day 2+");
        Add("piano", "Piano", EOfficialEncounterSlot.MiddleEvent, 1, int.MaxValue, null, null, false, null, true, new double[] { 27.19, 12.81, 7.81, 10.00, 6.15, 5.42, 6.46, 5.94, 6.98, 7.08 }, "BazaarDB 实测 All Days (Hero8)");
        Add("reflectingpool", "Reflecting Pool", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.56, 1.34, 1.34, 1.34, 1.12 }, "Patch 7.0 Day 6+ (Mak)，单局唯一");
        Add("roboticsfactory", "Robotics Factory", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.81, 1.81, 1.85 }, "Patch 16.0 Day 8+ (Stelle/Dooley)，单局唯一");
        Add("salvageyard", "Salvage Yard", EOfficialEncounterSlot.MiddleEvent, 4, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 2.44, 1.69, 1.48, 1.75, 1.86, 1.70, 1.58 }, "Patch 16.0 Day 4+");
        Add("shroudedfigure", "Shrouded Figure", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 6.71, 6.72, 6.79 }, "BazaarDB 实测常态 Day 8+");
        Add("streetfestival", "Street Festival", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 6.73, 5.77, 6.56, 6.13, 6.99, 7.12 }, "BazaarDB 实测 Day 5+ (Hotfix 13.1 修 D3H1 Bug)");
        Add("thecult", "The Cult", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.46, 0.68, 0.52, 0.57, 0.50 }, "BazaarDB 实测 Day 6+，单局唯一");
        Add("thedocks", "The Docks", EOfficialEncounterSlot.MiddleEvent, 4, 7, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 59.36, 49.36, 44.57, 48.99, 0.00, 0.00, 0.00 }, "Wiki + BazaarDB 实测 Days 4–7");
        Add("thedragonshideout", "The Dragons' Hideout", EOfficialEncounterSlot.MiddleEvent, 6, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 1.70, 1.70, 1.70, 1.70, 1.70 }, "Patch 18.0/19.3 Day 6+，单局唯一");
        Add("thelostcrate", "The Lost Crate", EOfficialEncounterSlot.MiddleEvent, 1, 5, null, null, false, null, true, new double[] { 68.41, 43.80, 27.19, 60.17, 48.30, 0.00, 0.00, 0.00, 0.00, 0.00 }, "BazaarDB 实测 Days 1–5");
        Add("thetravelagent", "The Travel Agent", EOfficialEncounterSlot.MiddleEvent, 2, 5, null, null, false, null, true, new double[] { 0.00, 4.90, 2.61, 2.60, 1.74, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 11.0 Days 2–5，单局唯一");
        Add("tinyfurrymonster", "Tiny Furry Monster", EOfficialEncounterSlot.MiddleEvent, 1, 3, null, null, true, null, true, new double[] { 77.89, 77.00, 66.30, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 15.0 Days 1–3，避开 Day 1 Hour 1–2");
        Add("tomboftheancients", "Tomb of the Ancients", EOfficialEncounterSlot.MiddleEvent, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 2.31, 1.75, 1.31 }, "Patch 16.0 Day 8+ (Mak)，单局唯一");
        Add("wink", "Wink", EOfficialEncounterSlot.MiddleEvent, 5, int.MaxValue, null, new int[] { 4, 5 }, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 2.57, 2.56, 2.86, 2.73, 2.99, 2.89 }, "Patch 7.0 & 10-10 Hotfix：Day 5+，单局唯一，禁 Hour 4–5");
        Add("wishingfountain", "Wishing Fountain", EOfficialEncounterSlot.MiddleEvent, 1, int.MaxValue, null, null, false, null, true, new double[] { 31.93, 12.85, 6.36, 6.88, 4.42, 3.68, 3.70, 4.17, 4.49, 4.04 }, "Patch 16.0 All Days (D1 32%)，单局唯一");
        Add("b1b2", "B1&B2", EOfficialEncounterSlot.MiddlePedestal, 3, 5, new int[] { 3, 5 }, null, false, null, true, new double[] { 0.00, 0.00, 27.66, 0.00, 10.86, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Hotfix 2025-07-09 Days 3–5 取消单局唯一；实测 Day 3/5 出现，Day 4 归 Lv4 升级奖励");
        Add("sterling", "Sterling", EOfficialEncounterSlot.MiddlePedestal, 6, 8, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 44.59, 48.57, 66.25, 0.00, 0.00 }, "Hotfix 2025-07-09 Days 6–8，取消单局唯一");
        Add("forja", "Forja", EOfficialEncounterSlot.MiddlePedestal, 9, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.55, 1.34 }, "Hotfix 2025-07-09 Day 9+，取消单局唯一；亦属 Lv13/16 升级奖励");
        Add("theartist", "The Artist", EOfficialEncounterSlot.MiddlePedestal, 8, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 1.75, 1.49, 1.34 }, "常规白昼地图实测 Day 8+，单局唯一");
        Add("borrow", "Borrow", EOfficialEncounterSlot.RightUtility, 1, 2, null, null, false, null, true, new double[] { 9.09, 5.04, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 0.00 }, "Patch 16.0 Days 1–2 only");
        Add("cacheofriches", "Cache of Riches", EOfficialEncounterSlot.RightUtility, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.84, 15.32, 9.93, 14.06, 3.05, 2.83, 5.49, 4.60, 4.15, 4.63 }, "右槽经济选项 All Days");
        Add("candystash", "Candy Stash", EOfficialEncounterSlot.RightUtility, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.51, 20.45, 67.97, 13.25, 13.29, 17.68, 5.49, 5.00, 4.15, 0.98 }, "右槽分天四档 (1-3/4-6/7-9/10+)");
        Add("hiddenlake", "Hidden Lake", EOfficialEncounterSlot.MiddleEvent, 7, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 0.00, 0.00, 0.00, 47.50, 44.73, 31.16, 14.55 }, "BazaarDB 实测 Day 7+ (Karnok IsTakeover 专属中槽奇遇)");
        Add("investinyourself", "Invest in Yourself", EOfficialEncounterSlot.RightUtility, 1, 6, null, null, false, null, true, new double[] { 8.92, 6.65, 10.61, 13.50, 12.87, 17.56, 0.00, 0.00, 0.00, 0.00 }, "BazaarDB 实测 Days 1–6 (分3档)，Day 7+ 绝迹");
        Add("lookforsparechange", "Look for Spare Change", EOfficialEncounterSlot.RightUtility, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.33, 19.16, 67.86, 12.91, 12.37, 17.56, 5.53, 5.10, 4.00, 0.90 }, "右槽分天四档 (1-3/4-6/7-9/10+)");
        Add("regenerativetincture", "Regenerative Tincture", EOfficialEncounterSlot.RightUtility, 1, int.MaxValue, null, null, false, null, true, new double[] { 9.25, 6.64, 9.79, 13.77, 12.74, 17.61, 66.50, 65.93, 60.15, 62.50 }, "右槽常驻保底支柱 All Days (D7+ 62%–66%)");
        Add("rest", "Rest", EOfficialEncounterSlot.RightUtility, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 10.09, 13.51, 13.16, 17.83, 65.25, 65.70, 59.81, 61.85 }, "右槽常驻保底支柱 Day 3+ (D7+ 60%–66%)");
        Add("snacktime", "Snack Time", EOfficialEncounterSlot.RightUtility, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.50, 6.44, 9.58, 14.14, 12.53, 17.09, 66.48, 66.07, 60.12, 62.24 }, "右槽常驻保底支柱 All Days (D7+ 62%–66%)");
        Add("study", "Study", EOfficialEncounterSlot.RightUtility, 4, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 0.00, 13.45, 13.06, 18.23, 66.14, 65.94, 59.96, 62.37 }, "Patch 2.5 移除 Day 3，右槽常驻保底支柱 Day 4+ (D7+ 60%–66%)");
        Add("abandonedproperty", "Abandoned Property", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 12.77, 20.65, 10.96, 14.38, 13.77, 23.35, 5.88, 4.96, 5.35, 5.81 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("alchemylab", "Alchemy Lab", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 18.62, 24.50, 10.12, 14.37, 12.81, 19.12, 5.06, 4.19, 4.81, 5.00 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("armory", "Armory", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.97, 25.43, 9.63, 13.53, 12.63, 20.20, 5.64, 4.88, 5.30, 5.67 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("battlefield", "Battlefield", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.94, 6.51, 10.29, 13.49, 15.01, 2.81, 5.29, 6.02, 5.12, 5.41 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("blockparty", "Block Party", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.13, 23.26, 9.49, 12.40, 12.00, 19.08, 5.30, 4.34, 4.81, 5.17 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("botanicalgardens", "Botanical Gardens", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 19.02, 22.62, 9.51, 13.05, 11.55, 17.10, 5.67, 4.79, 4.39, 5.46 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("bountifulharvest", "Bountiful Harvest", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 23.23, 28.06, 9.03, 12.58, 11.61, 19.68, 5.00, 5.97, 5.97, 5.97 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("cabinfishing", "Cabin Fishing", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.10, 5.89, 8.45, 12.98, 14.17, 2.20, 5.48, 6.13, 4.76, 5.00 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("cinderchase", "Cinder Chase", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.61, 6.56, 9.76, 13.35, 15.38, 3.20, 5.87, 6.16, 5.38, 5.54 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("cupboard", "Cupboard", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 4.49, 2.97, 4.56, 6.14, 7.15, 3.85, 2.66, 2.41, 2.09, 2.15 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("deepseafishing", "Deep Sea Fishing", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 18.93, 23.39, 9.17, 12.02, 11.67, 19.58, 5.06, 4.11, 5.12, 5.48 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("dinotrapping", "Dino Trapping", EOfficialEncounterSlot.RightFreeItem, 3, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 0.00, 9.55, 11.17, 12.79, 17.27, 6.30, 4.22, 3.64, 5.32 }, "Patch 20.2 Day 3+ (起阶 Silver)");
        Add("equipmentvan", "Equipment Van", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 16.46, 22.71, 8.12, 12.60, 11.77, 16.25, 4.69, 4.27, 4.69, 4.90 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("excavation", "Excavation", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.35, 26.08, 9.59, 14.03, 11.89, 20.55, 5.71, 5.12, 5.25, 5.70 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("extractextract", "Extract Extract", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 7.96, 6.14, 10.34, 13.18, 15.12, 2.54, 5.20, 5.61, 5.05, 5.71 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("freezer", "Freezer", EOfficialEncounterSlot.RightFreeItem, 2, int.MaxValue, null, null, false, null, true, new double[] { 0.00, 6.92, 10.22, 13.16, 12.95, 19.70, 6.46, 5.43, 5.76, 5.70 }, "BazaarDB 实测 Day 2+ (起阶 Silver)");
        Add("furnace", "Furnace", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.54, 5.92, 9.28, 13.18, 15.50, 2.59, 5.00, 5.76, 4.97, 5.50 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("guardlocker", "Guard Locker", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.68, 6.76, 10.56, 13.18, 16.19, 3.14, 5.52, 6.15, 5.52, 5.77 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("hospital", "Hospital", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.74, 26.09, 10.10, 14.67, 12.46, 21.41, 5.14, 4.86, 5.50, 5.64 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("houseparty", "House Party", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.83, 5.96, 9.34, 12.30, 13.89, 2.60, 5.19, 5.70, 4.66, 4.87 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("lostandfound", "Lost and Found", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.61, 7.21, 9.91, 13.77, 15.81, 2.86, 5.56, 6.25, 5.03, 5.64 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("magstorage", "Mag Storage", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 18.52, 21.28, 8.28, 11.36, 11.68, 18.48, 4.68, 4.08, 4.16, 5.52 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("medicinecabinet", "Medicine Cabinet", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 9.36, 7.13, 10.35, 12.75, 16.03, 3.49, 5.23, 5.91, 5.00, 5.79 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("obstaclecourse", "Obstacle Course", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.42, 22.15, 9.85, 13.07, 13.02, 19.19, 5.17, 4.88, 4.76, 5.14 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("procuremedkit", "Procure Medkit", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.74, 6.95, 9.29, 14.20, 15.69, 2.73, 5.17, 6.30, 5.17, 5.80 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("pyre", "Pyre", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.30, 24.04, 9.46, 13.07, 11.24, 19.89, 5.73, 4.69, 5.07, 5.27 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("racetrack", "Racetrack", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 14.60, 16.79, 7.07, 9.39, 8.66, 13.10, 3.52, 3.39, 3.57, 3.81 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("reagentharvesting", "Reagent Harvesting", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.12, 23.38, 8.31, 13.62, 11.44, 18.06, 5.38, 4.75, 5.62, 5.69 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("recyclingcenter", "Recycling Center", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 20.77, 24.92, 9.58, 14.06, 12.94, 20.12, 5.47, 5.28, 5.37, 5.45 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("scrapsalvage", "Scrap Salvage", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 9.06, 6.59, 10.55, 14.46, 15.83, 3.08, 5.88, 7.13, 5.85, 5.97 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("securitycenter", "Security Center", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.32, 25.17, 9.82, 13.75, 12.64, 20.88, 5.19, 4.85, 5.56, 5.81 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("sharpeningkit", "Sharpening Kit", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.78, 6.84, 9.86, 13.31, 15.54, 3.12, 5.32, 6.25, 4.91, 5.82 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("techsalvage", "Tech Salvage", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.52, 6.08, 8.88, 13.48, 13.04, 2.72, 5.12, 5.68, 5.32, 5.40 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("treasurechest", "Treasure Chest", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 9.29, 6.64, 10.29, 13.50, 15.81, 3.08, 5.73, 6.17, 5.17, 5.61 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("trufflefarming", "Truffle Farming", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 11.01, 7.97, 10.68, 14.39, 16.96, 3.58, 7.23, 5.41, 6.96, 6.49 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("utilitybox", "Utility Box", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 8.64, 6.98, 9.79, 13.43, 15.60, 3.04, 5.63, 6.35, 5.11, 5.67 }, "Patch 16.0 右槽四阶免费物品选项");
        Add("workshop", "Workshop", EOfficialEncounterSlot.RightFreeItem, 1, int.MaxValue, null, null, false, null, true, new double[] { 21.88, 25.56, 9.56, 13.55, 12.33, 19.86, 5.81, 4.86, 5.11, 5.75 }, "Patch 16.0 右槽四阶免费物品选项");

        return d;
    }
}

using System.Globalization;
using System.Text;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Engine;

/// <summary>
/// 每日挑战确定性配方规格（东八区 UTC+8 每日 00:00 切换）。
/// </summary>
public sealed record DailyChallengeSpec(
    string DateKey,
    int MasterSeed,
    string FeaturedHero,
    IReadOnlyList<string> MutatorIds,
    DateTimeOffset NextResetUtc,
    CustomRunRuleSet RuleSet);

/// <summary>
/// 每日同种子挑战确定性种子生成器：
/// - 严格使用 UTC+8 日期口径（yyyy-MM-dd）；
/// - 使用 FNV-1a 32-bit 哈希确保跨平台、跨 .NET 进程 100% 确定性；
/// - 通过流标签（encounter/shop/reroll/monster/loot/levelup/pvp_opp/combat）派生独立子种子，彻底杜绝操作顺序引发的 RNG 偏移。
/// </summary>
public static class DailyChallengeSeedGenerator
{
    public static readonly TimeSpan Utc8Offset = TimeSpan.FromHours(8);

    public const string StreamEncounter = "encounter";
    public const string StreamShop = "shop";
    public const string StreamReroll = "reroll";
    public const string StreamMonster = "monster";
    public const string StreamLoot = "loot";
    public const string StreamLevelUp = "levelup";
    public const string StreamPvpOpponent = "pvp_opp";
    public const string StreamCombat = "combat";

    /// <summary>获取东八区（UTC+8）当前日期键（格式 yyyy-MM-dd）。</summary>
    public static string GetTodayDateKeyUtc8(DateTimeOffset? utcNow = null)
    {
        var nowUtc8 = (utcNow ?? DateTimeOffset.UtcNow).ToOffset(Utc8Offset);
        return nowUtc8.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    /// <summary>计算指定 UTC+8 日期的次日零点（换日重置时刻的 UTC 时间）。</summary>
    public static DateTimeOffset GetNextResetUtc(string dateKey)
    {
        if (!DateOnly.TryParseExact(dateKey, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            var today = DateTimeOffset.UtcNow.ToOffset(Utc8Offset);
            date = DateOnly.FromDateTime(today.Date);
        }
        var nextDayLocal = new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue), Utc8Offset);
        return nextDayLocal.ToUniversalTime();
    }

    /// <summary>按确定性 FNV-1a 32-bit 算法计算每日主种子（P0-41：极低概率 0 碰撞兜底为 1，防止绕过 FixedRunSeed != 0 守卫）。</summary>
    public static int ComputeMasterSeed(string dateKey)
    {
        string normalized = string.IsNullOrWhiteSpace(dateKey) ? GetTodayDateKeyUtc8() : dateKey.Trim();
        uint hash = ComputeFnv1a32($"bazaar-daily-v1:{normalized}");
        int signed = unchecked((int)hash);
        return signed == 0 ? 1 : signed;
    }

    /// <summary>
    /// 从主种子按流标签、天数、小时与序号派生互不干扰的确定性子种子。
    /// </summary>
    public static int DeriveSubSeed(int masterSeed, string streamTag, int day, int hour, int index = 0)
    {
        string safeTag = string.IsNullOrWhiteSpace(streamTag) ? "default" : streamTag.Trim().ToLowerInvariant();
        uint hash = ComputeFnv1a32($"{masterSeed}:{safeTag}:d{day}:h{hour}:i{index}");
        return unchecked((int)hash);
    }

    /// <summary>便捷方法：直接由子种子创建独立确定性 Random 实例。</summary>
    public static Random CreateStreamRng(int masterSeed, string streamTag, int day, int hour, int index = 0)
        => new(DeriveSubSeed(masterSeed, streamTag, day, hour, index));

    /// <summary>
    /// 为指定 UTC+8 日期生成确定性每日挑战完整配方（指定推荐英雄 + 2~3 条非冲突词条，保底至少 1 条正面 + 1 条高压挑战词条）。
    /// </summary>
    public static DailyChallengeSpec GenerateSpec(string? dateKey = null, DateTimeOffset? utcNow = null)
    {
        string resolvedDate = string.IsNullOrWhiteSpace(dateKey) ? GetTodayDateKeyUtc8(utcNow) : dateKey.Trim();
        int masterSeed = ComputeMasterSeed(resolvedDate);
        var rng = new Random(masterSeed);

        var heroes = HeroNames.PlayableHeroes;
        string hero = heroes[rng.Next(heroes.Length)];

        var positives = new List<MutatorMetadata>();
        var challenges = new List<MutatorMetadata>();
        foreach (var m in MutatorCatalog.All)
        {
            if (m.IsPositive) positives.Add(m);
            else challenges.Add(m);
        }

        int targetCount = rng.NextDouble() < 0.5 ? 2 : 3;
        var picked = new List<string>(targetCount);
        var usedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryAddFromPool(List<MutatorMetadata> pool)
        {
            if (pool.Count == 0) return;
            int start = rng.Next(pool.Count);
            for (int offset = 0; offset < pool.Count; offset++)
            {
                var cand = pool[(start + offset) % pool.Count];
                if (picked.Contains(cand.Id, StringComparer.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(cand.ConflictGroup) && usedGroups.Contains(cand.ConflictGroup)) continue;
                picked.Add(cand.Id);
                if (!string.IsNullOrEmpty(cand.ConflictGroup)) usedGroups.Add(cand.ConflictGroup);
                return;
            }
        }

        // 保底至少 1 条增益词条 + 1 条高压挑战词条
        TryAddFromPool(positives);
        TryAddFromPool(challenges);
        if (picked.Count < targetCount)
        {
            var allCopy = new List<MutatorMetadata>(MutatorCatalog.All);
            TryAddFromPool(allCopy);
        }

        var normalizedMutators = MutatorCatalog.NormalizeAndResolveConflicts(picked, maxCount: 3);
        var rules = CustomRunRuleSet.CreatePreset(EGameModePreset.PureOfficial, ESpecialModeId.DailyChallenge);
        rules.DailyChallengeDate = resolvedDate;
        rules.FixedRunSeed = masterSeed;
        rules.MutatorIds = normalizedMutators;
        if (rules.HasMutator(MutatorCatalog.IronmanNoFates))
        {
            rules.DisableFatesRevive = true;
            rules.StartingPrestige = 10;
        }

        return new DailyChallengeSpec(
            DateKey: resolvedDate,
            MasterSeed: masterSeed,
            FeaturedHero: hero,
            MutatorIds: normalizedMutators,
            NextResetUtc: GetNextResetUtc(resolvedDate),
            RuleSet: rules.CloneNormalized());
    }

    private static uint ComputeFnv1a32(string text)
    {
        const uint offsetBasis = 2166136261u;
        const uint prime = 16777619u;
        uint hash = offsetBasis;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= prime;
        }
        return hash;
    }
}

/// <summary>
/// 无尽爬塔单层动态缩放计算结果。
/// </summary>
public sealed record EndlessFloorScaling(
    int Day,
    int ExcessDays,
    double EnemyHealthMultiplier,
    int EnemyHealthCap,
    double EnemyOffenseMultiplier,
    double EnemyCooldownReductionPct,
    bool IsMilestoneDay,
    int MilestonePrestigeRestore,
    int MilestoneBonusGold,
    int GuaranteedRandomEnchantCount,
    CardTier MinimumGhostItemTier);

/// <summary>
/// 无尽爬塔模式（Day 11 ~ Day 99）动态缩放与里程碑奖励计算器：
/// 严格防 Int32 溢出（HP 封顶 2,000,000）与 300s 战斗超时。
/// </summary>
public static class EndlessTowerScaler
{
    public const int DefaultMaxDay = 99;
    public const int MaxEnemyHealthCap = 2_000_000;
    public const double MaxHealthMultiplier = 120.0;
    public const double MaxOffenseMultiplier = 35.0;
    public const double MaxCooldownReductionPct = 0.40;

    /// <summary>计算指定天数的无尽缩放参数与里程碑奖励。</summary>
    public static EndlessFloorScaling Compute(int day, int maxDay = DefaultMaxDay)
    {
        int clampedDay = Math.Clamp(day, 1, Math.Clamp(maxDay, 11, DefaultMaxDay));
        int k = Math.Max(0, clampedDay - 10);

        double hpMult = k == 0
            ? 1.0
            : Math.Min(MaxHealthMultiplier, 1.0 + 0.22 * k + 0.018 * k * k);
        double offenseMult = k == 0
            ? 1.0
            : Math.Min(MaxOffenseMultiplier, 1.0 + 0.14 * k + 0.008 * k * k);
        double cdrPct = k == 0
            ? 0.0
            : Math.Min(MaxCooldownReductionPct, (k / 5) * 0.04);

        bool isMilestone = clampedDay >= 15 && clampedDay % 5 == 0;
        int prestigeRestore = isMilestone ? 3 : 0;
        int bonusGold = isMilestone ? 10 + 2 * k : 0;
        int guaranteedEnchants = k / 10;
        CardTier minTier = clampedDay >= 20 ? CardTier.Legendary
                         : clampedDay >= 11 ? CardTier.Diamond
                         : CardTier.Bronze;

        return new EndlessFloorScaling(
            Day: clampedDay,
            ExcessDays: k,
            EnemyHealthMultiplier: Math.Round(hpMult, 4),
            EnemyHealthCap: MaxEnemyHealthCap,
            EnemyOffenseMultiplier: Math.Round(offenseMult, 4),
            EnemyCooldownReductionPct: Math.Round(cdrPct, 4),
            IsMilestoneDay: isMilestone,
            MilestonePrestigeRestore: prestigeRestore,
            MilestoneBonusGold: bonusGold,
            GuaranteedRandomEnchantCount: guaranteedEnchants,
            MinimumGhostItemTier: minTier);
    }

    /// <summary>
    /// P0-43：按无尽幽灵原始录制层数（<paramref name="sourceDay"/>）与目标挑战层数（<paramref name="targetDay"/>）计算相对缩放倍率。
    /// 同层真镜像（<c>sourceDay &gt;= targetDay</c>）恒为 1.0x，低层补位（如 Day 15 -&gt; Day 25）仅乘相对比 <c>Compute(targetDay) / Compute(sourceDay)</c>；
    /// 当 <paramref name="sourceDay"/> &lt;= 10 时平滑退化为 <c>Compute(targetDay)</c>。
    /// </summary>
    public static (double HealthMultiplier, double OffenseMultiplier, double CooldownReductionPct) ComputeRelativeScaling(
        int sourceDay,
        int targetDay,
        int maxDay = DefaultMaxDay)
    {
        if (targetDay <= 10 || sourceDay >= targetDay)
            return (1.0, 1.0, 0.0);

        var targetScale = Compute(targetDay, maxDay);
        var sourceScale = Compute(Math.Max(1, sourceDay), maxDay);
        double hpMult = Math.Clamp(
            targetScale.EnemyHealthMultiplier / Math.Max(1e-6, sourceScale.EnemyHealthMultiplier),
            1.0,
            MaxHealthMultiplier);
        double offMult = Math.Clamp(
            targetScale.EnemyOffenseMultiplier / Math.Max(1e-6, sourceScale.EnemyOffenseMultiplier),
            1.0,
            MaxOffenseMultiplier);
        double cdrPct = Math.Clamp(
            targetScale.EnemyCooldownReductionPct - sourceScale.EnemyCooldownReductionPct,
            0.0,
            MaxCooldownReductionPct);
        return (Math.Round(hpMult, 4), Math.Round(offMult, 4), Math.Round(cdrPct, 4));
    }

    /// <summary>将原始敌方血量安全缩放并钳制在 [1, MaxEnemyHealthCap] 范围内。</summary>
    public static int ScaleEnemyHealth(int baseHealth, int day, int maxDay = DefaultMaxDay)
    {
        var scaling = Compute(day, maxDay);
        double scaled = Math.Round(Math.Max(1, baseHealth) * scaling.EnemyHealthMultiplier);
        if (double.IsNaN(scaled) || double.IsInfinity(scaled))
            return MaxEnemyHealthCap;
        return (int)Math.Clamp(scaled, 1, scaling.EnemyHealthCap);
    }
}

/// <summary>
/// Boss 车轮战单波配置定义。
/// </summary>
public sealed record BossRushWaveConfig(
    int Wave,
    string TierNameZh,
    string DescZh,
    double HealthMultiplier,
    double OffenseMultiplier,
    CardTier MinimumEquipmentTier,
    int BonusReinforceSkills,
    bool IsMilestonePvpWave,
    int VictoryLootChoices,
    int BonusGoldReward,
    int BonusXpReward,
    IReadOnlyList<string> CandidateBossNames);

/// <summary>
/// Boss 车轮战 10 波精选配置表（覆盖 23 只 GameData.db 实存 Boss，支持通关后无缝延续无尽 Boss 波次）。
/// </summary>
public static class BossRushCatalog
{
    public const int StandardTotalWaves = 10;

    private static readonly string[] EarlyTierBosses =
    [
        "Calico", "Hoverbike Hooligan", "Ahexa", "Prince Marianas", "Gorgon Noble", "Flame Juggler"
    ];

    private static readonly string[] MidTierBosses =
    [
        "Loan Shark", "Scovyle", "Capital Punisher", "Infernal Diamond", "Burninator Bot", "Dr. Vortex"
    ];

    private static readonly string[] HighTierBosses =
    [
        "Ferros Khan", "Weapons Platform", "Dooltron", "Boss Harrow", "Awakened Primordial", "Rex Spex"
    ];

    private static readonly string[] FinalTierBosses =
    [
        "Awakened District", "Lord Arken", "Morguloth", "Void Golem", "Bananniboss"
    ];

    /// <summary>标准 10 波 Boss 车轮战完整配置。</summary>
    public static IReadOnlyList<BossRushWaveConfig> Waves { get; } =
    [
        new(1, "初阶试炼 I", "初阶首领热身波，快速建立核心战力", 0.45, 0.75, CardTier.Bronze, 0, false, 2, 8, 6, EarlyTierBosses),
        new(2, "初阶试炼 II", "初阶首领进阶波，考验前期输出节奏", 0.60, 0.85, CardTier.Bronze, 0, false, 2, 8, 6, EarlyTierBosses),
        new(3, "初阶试炼 III", "初阶关底首领，解锁额外强化祭坛", 0.75, 0.95, CardTier.Silver, 0, false, 2, 10, 6, EarlyTierBosses),
        new(4, "中坚对决 I", "中坚高危首领登场，需完善攻防联动", 0.85, 1.00, CardTier.Silver, 0, false, 2, 10, 6, MidTierBosses),
        new(5, "中坚对决 II · 巅峰试炼", "半程关底首领 + 可选巅峰镜像试炼", 0.98, 1.05, CardTier.Gold, 0, true, 2, 12, 8, MidTierBosses),
        new(6, "中坚对决 III", "中坚终段首领，开放高阶强化祭坛", 1.10, 1.12, CardTier.Gold, 0, false, 2, 12, 8, MidTierBosses),
        new(7, "传奇挑战 I", "高难传奇首领，额外随机强化 1 项技能", 1.15, 1.20, CardTier.Gold, 1, false, 2, 14, 8, HighTierBosses),
        new(8, "传奇挑战 II", "传奇首领压制升级，考验爆发与容错", 1.30, 1.30, CardTier.Diamond, 1, false, 2, 14, 8, HighTierBosses),
        new(9, "传奇挑战 III", "终战前哨传奇首领，开放终极强化祭坛", 1.45, 1.40, CardTier.Diamond, 1, false, 2, 16, 10, HighTierBosses),
        new(10, "终极守关 · 巅峰决战", "最终守关传奇 Boss（全装备最低 Diamond）+ 巅峰镜像决战", 1.65, 1.55, CardTier.Diamond, 1, true, 3, 20, 12, FinalTierBosses)
    ];

    /// <summary>按波次（1..N）获取 Boss 配置；超过第 10 波时自动平滑延展为无尽 Boss 波次。</summary>
    public static BossRushWaveConfig GetWave(int wave)
    {
        int w = Math.Max(1, wave);
        if (w <= Waves.Count)
            return Waves[w - 1];

        int extra = w - Waves.Count;
        double hpMult = Math.Min(EndlessTowerScaler.MaxHealthMultiplier, 1.65 + 0.30 * extra + 0.02 * extra * extra);
        double offMult = Math.Min(EndlessTowerScaler.MaxOffenseMultiplier, 1.55 + 0.18 * extra + 0.01 * extra * extra);
        return new BossRushWaveConfig(
            Wave: w,
            TierNameZh: $"无尽首领第 {w} 波",
            DescZh: "通关 10 波后的无尽传奇首领车轮挑战",
            HealthMultiplier: Math.Round(hpMult, 3),
            OffenseMultiplier: Math.Round(offMult, 3),
            MinimumEquipmentTier: CardTier.Legendary,
            BonusReinforceSkills: Math.Min(3, 1 + extra / 3),
            IsMilestonePvpWave: w % 5 == 0,
            VictoryLootChoices: 3,
            BonusGoldReward: 20 + extra * 2,
            BonusXpReward: 12,
            CandidateBossNames: FinalTierBosses);
    }

    /// <summary>
    /// 从权威 <see cref="GameDataRepository"/> 中为指定波次解析真实存在的 Boss 怪物模板列表；
    /// 优先按精选 Boss 名称精准/包含匹配，不足时按高血量真实怪物兜底。
    /// </summary>
    public static IReadOnlyList<MonsterTemplate> ResolveWaveMonsters(int wave, GameDataRepository repo)
    {
        var cfg = GetWave(wave);
        var matched = new List<MonsterTemplate>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var targetName in cfg.CandidateBossNames)
        {
            foreach (var monster in repo.MonstersById.Values)
            {
                if (seenIds.Contains(monster.Id)) continue;
                if (string.Equals(monster.InternalName, targetName, StringComparison.OrdinalIgnoreCase)
                    || monster.InternalName.Contains(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (seenIds.Add(monster.Id))
                        matched.Add(monster);
                }
            }
        }

        if (matched.Count < 3)
        {
            // 按血量降序补足高阶实存野怪，确保 3 选 1 永不空池
            var fallback = repo.MonstersById.Values
                .Where(m => !seenIds.Contains(m.Id)
                            && !m.InternalName.Contains("TEST", StringComparison.OrdinalIgnoreCase)
                            && !m.InternalName.Contains("DUMMY", StringComparison.OrdinalIgnoreCase)
                            && m.Player.Hand.Items.Count > 0)
                .OrderByDescending(m => m.Player.Attributes.GetValueOrDefault("HealthMax", 0))
                .Take(Math.Max(3 - matched.Count, 3));
            foreach (var m in fallback)
            {
                if (seenIds.Add(m.Id))
                    matched.Add(m);
            }
        }

        return matched;
    }
}

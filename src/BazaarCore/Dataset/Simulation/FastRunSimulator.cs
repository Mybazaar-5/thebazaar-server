using System.Runtime.CompilerServices;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarCore.Dataset.Simulation;

/// <summary>
/// 预计算的卡牌静态特征与各品质面板估算缓存，避免局内高频遍历规则树产生 GC 开销。
/// </summary>
public sealed class CardMeta
{
    public bool IsWeapon { get; init; }
    public bool IsBurn { get; init; }
    public bool IsPoison { get; init; }
    public bool IsShield { get; init; }
    public bool IsHeal { get; init; }
    public bool IsRegen { get; init; }
    public bool IsHaste { get; init; }
    public bool IsSlow { get; init; }
    public bool IsFreeze { get; init; }
    public bool IsEconomy { get; init; }
    public bool IsProperty { get; init; }
    public bool IsValueScaler { get; init; }
    public bool IsPureEconomy { get; init; }
    public bool IsFastTrigger { get; init; }
    public bool IsScaler { get; init; }
    public bool HasNeighborEffect { get; init; }
    public bool BuffsLeftNeighbor { get; init; }
    public bool BuffsRightNeighbor { get; init; }
    public bool PrefersLeftMost { get; init; }
    public bool PrefersRightMost { get; init; }
    public bool HasCardSoldTrigger { get; init; }
    public bool HasOutOfCombatTrigger { get; init; }
    public bool HasOutOfCombatAura { get; init; }
    public bool HasQuests { get; init; }
    public IReadOnlyList<string> TriggerTypes { get; init; } = Array.Empty<string>();

    private readonly double[] _powerByTier = new double[5];
    private readonly int[] _buyPriceByTier = new int[5];
    private readonly HashSet<string> _allTags;

    private CardMeta(HashSet<string> allTags)
    {
        _allTags = allTags;
    }

    public bool HasAnyTag(string tag) => _allTags.Contains(tag);

    public double EstimateCombatPower(CardTier tier)
    {
        int idx = Math.Clamp((int)tier, 0, 4);
        return _powerByTier[idx];
    }

    public int GetBuyPrice(CardTemplate tpl, CardTier tier, StandardPricesSettings prices)
    {
        int idx = Math.Clamp((int)tier, 0, 4);
        int custom = _buyPriceByTier[idx];
        if (custom > 0) return custom;
        return tpl.Type == "Skill"
            ? prices.GetSkillBuyPrice(tier)
            : prices.GetItemBuyPrice(tpl.CardSize, tier);
    }

    public static CardMeta Build(CardTemplate tpl)
    {
        var allTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tpl.Tags) allTags.Add(t);
        foreach (var ht in tpl.HiddenTags) allTags.Add(ht);

        bool hasDamage = allTags.Contains("Weapon") || allTags.Contains("Damage");
        bool hasBurn = allTags.Contains("Burn");
        bool hasPoison = allTags.Contains("Poison");
        bool hasShield = allTags.Contains("Shield");
        bool hasHeal = allTags.Contains("Heal");
        bool hasRegen = allTags.Contains("Regen");
        bool hasHaste = allTags.Contains("Haste") || allTags.Contains("Charge");
        bool hasSlow = allTags.Contains("Slow");
        bool hasFreeze = allTags.Contains("Freeze");
        bool isProperty = allTags.Contains("Property");
        bool isEcon = allTags.Contains("EconomyReference") || allTags.Contains("Gold") || allTags.Contains("Income");
        bool isValue = allTags.Contains("Value");

        var triggers = new HashSet<string>(StringComparer.Ordinal);
        bool hasNeighbor = false;
        bool buffsLeft = false;
        bool buffsRight = false;
        bool leftMost = false;
        bool rightMost = false;
        bool isScaler = isValue;

        void InspectTarget(RuleNode? node)
        {
            if (node == null) return;
            if (node.Type == "TTargetCardPositional")
            {
                hasNeighbor = true;
                if (node.TargetMode == "LeftCard") buffsLeft = true;
                else if (node.TargetMode == "RightCard") buffsRight = true;
            }
            else if (node.Type == "TTargetCardXMost")
            {
                if (node.TargetMode == "LeftMostCard") leftMost = true;
                else if (node.TargetMode == "RightMostCard") rightMost = true;
            }
            InspectTarget(node.Target);
            InspectTarget(node.Source);
        }

        void InspectTrigger(RuleNode? trig)
        {
            if (trig == null) return;
            if (!string.IsNullOrEmpty(trig.Type) && trig.Type != "TTriggerOr")
                triggers.Add(trig.Type);
            if (trig.Triggers != null)
            {
                foreach (var sub in trig.Triggers)
                    InspectTrigger(sub);
            }
            InspectTarget(trig.Subject);
            InspectTarget(trig.Source);
        }

        void InspectAction(RuleNode? act)
        {
            if (act == null) return;
            if (act.Type == "TActionCardModifyAttribute")
                isScaler = true;
            InspectTarget(act.Target);
            if (act.Actions != null)
            {
                foreach (var sub in act.Actions)
                    InspectAction(sub);
            }
        }

        bool hasOocAura = false;
        foreach (var ab in tpl.Abilities.Values)
        {
            InspectTrigger(ab.Trigger);
            InspectAction(ab.Action);
        }
        foreach (var au in tpl.Auras.Values)
        {
            InspectTarget(au.Action);
            if (au.Action?.Type == "TAuraActionCardModifyAttribute")
                isScaler = true;
            if (!string.Equals(au.WorksIn, "CombatOnly", StringComparison.OrdinalIgnoreCase))
                hasOocAura = true;
        }
        bool hasQuests = tpl.Quests != null && tpl.Quests.Count > 0;
        if (hasQuests)
        {
            foreach (var q in tpl.Quests!)
            {
                foreach (var entry in q.Entries)
                {
                    InspectTrigger(entry.Trigger);
                    if (entry.Reward?.Abilities != null)
                    {
                        foreach (var rab in entry.Reward.Abilities.Values)
                            InspectTrigger(rab.Trigger);
                    }
                }
            }
        }
        bool hasSoldTrigger = triggers.Contains("TTriggerOnCardSold");
        bool hasOocTrigger = hasSoldTrigger
            || triggers.Contains("TTriggerOnDayStarted")
            || triggers.Contains("TTriggerOnHourStarted")
            || triggers.Contains("TTriggerOnCardPurchased")
            || triggers.Contains("TTriggerOnCardUpgraded")
            || triggers.Contains("TTriggerOnFightEnded")
            || triggers.Contains("TTriggerOnPlayerAttributeChanged")
            || triggers.Contains("TTriggerOnCardAttributeChanged")
            || triggers.Contains("TTriggerOnCardQuestCompleted")
            || triggers.Contains("TTriggerOnCardEnchanted");

        // 预计算每个品质的近似战力与自定义买价
        var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (tpl.Attributes != null)
        {
            foreach (var (k, v) in tpl.Attributes)
                merged[k] = v;
        }

        var powerByTier = new double[5];
        var buyPriceByTier = new int[5];
        double fastestCd = 99999;

        foreach (CardTier t in Enum.GetValues<CardTier>())
        {
            if (tpl.Tiers != null && tpl.Tiers.TryGetValue(t.ToString(), out var td))
            {
                foreach (var (k, v) in td.Attributes)
                    merged[k] = v;
            }

            double cd = merged.GetValueOrDefault("CooldownMax", 0);
            if (cd > 0) fastestCd = Math.Min(fastestCd, cd);
            double cdSec = cd > 0 ? Math.Max(1.0, cd / 1000.0) : 5.0;

            double dmg = merged.GetValueOrDefault("DamageAmount", 0);
            double shd = merged.GetValueOrDefault("ShieldApplyAmount", 0);
            double brn = merged.GetValueOrDefault("BurnApplyAmount", 0);
            double psn = merged.GetValueOrDefault("PoisonApplyAmount", 0);
            double heal = merged.GetValueOrDefault("HealAmount", 0);
            double rgn = merged.GetValueOrDefault("RegenApplyAmount", 0);
            double crit = merged.GetValueOrDefault("CritChance", 0);
            double multi = Math.Max(1.0, merged.GetValueOrDefault("Multicast", 1));
            double haste = merged.GetValueOrDefault("HasteAmount", 0) / 1000.0;
            double slow = merged.GetValueOrDefault("SlowAmount", 0) / 1000.0;
            double freeze = merged.GetValueOrDefault("FreezeAmount", 0) / 1000.0;

            double dps = ((dmg * (1.0 + Math.Clamp(crit, 0, 100) / 100.0)) + shd * 0.85 + brn * 4.5 + psn * 6.0 + heal * 0.65 + rgn * 5.0) * multi / cdSec;
            double utility = (haste * 3.5 + slow * 3.0 + freeze * 5.5) * 4.0 / cdSec;
            double passiveBonus = cd <= 0 ? (6.0 + (int)t * 5.0) : 0.0;

            powerByTier[(int)t] = Math.Max(3.0, dps * 2.4 + utility + passiveBonus);
            buyPriceByTier[(int)t] = (int)Math.Round(merged.GetValueOrDefault("BuyPrice", 0));
        }

        var meta = new CardMeta(allTags)
        {
            IsWeapon = hasDamage,
            IsBurn = hasBurn,
            IsPoison = hasPoison,
            IsShield = hasShield,
            IsHeal = hasHeal,
            IsRegen = hasRegen,
            IsHaste = hasHaste,
            IsSlow = hasSlow,
            IsFreeze = hasFreeze,
            IsEconomy = isEcon,
            IsProperty = isProperty,
            IsValueScaler = isValue,
            IsPureEconomy = (isEcon || isValue || isProperty) && !hasDamage && !hasBurn && !hasPoison && !hasShield,
            IsFastTrigger = fastestCd <= 4200 || hasHaste,
            IsScaler = isScaler,
            HasNeighborEffect = hasNeighbor,
            BuffsLeftNeighbor = buffsLeft,
            BuffsRightNeighbor = buffsRight,
            PrefersLeftMost = leftMost,
            PrefersRightMost = rightMost,
            HasCardSoldTrigger = hasSoldTrigger,
            HasOutOfCombatTrigger = hasOocTrigger,
            HasOutOfCombatAura = hasOocAura,
            HasQuests = hasQuests,
            TriggerTypes = triggers.ToArray()
        };

        Array.Copy(powerByTier, meta._powerByTier, 5);
        Array.Copy(buyPriceByTier, meta._buyPriceByTier, 5);
        return meta;
    }
}

/// <summary>
/// 物品商店定向商人规格定义（通用商 / 标签定向商 / 尺寸定向商）。
/// </summary>
public readonly record struct MerchantSpec(string DisplayName, string? FilterTag, CardSize? FilterSize);

/// <summary>
/// 针对单个 GameDataRepository 预先构建的并发安全只读索引缓存，
/// 彻底避免在 28,000+ 局淘汰赛期间重复扫描全量 3,400+ 张卡牌与 177+ 野怪。
/// </summary>
public sealed class SimulationRepoIndex
{
    public static readonly string[] MerchantFilterTags =
    [
        "Weapon", "Aquatic", "Friend", "Property", "Burn", "Poison", "Shield",
        "Heal", "Regen", "Tool", "Tech", "Food", "Potion", "Relic",
        "Instrument", "Dragon", "Apparel", "Vehicle", "Ammo", "Crit",
        "Haste", "Slow", "Freeze", "Value", "Flying", "Rage"
    ];

    public sealed class HeroCardPool
    {
        public CardTemplate[][] HeroItemsByTier { get; } = new CardTemplate[4][];
        public CardTemplate[][] CommonItemsByTier { get; } = new CardTemplate[4][];
        public CardTemplate[][] SkillsByTier { get; } = new CardTemplate[4][];
        public CardTemplate[][] HeroItemsByTierZh { get; } = new CardTemplate[4][];
        public CardTemplate[][] CommonItemsByTierZh { get; } = new CardTemplate[4][];
        public CardTemplate[][] SkillsByTierZh { get; } = new CardTemplate[4][];

        public Dictionary<string, CardTemplate[][]> ItemsByTag { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CardTemplate[][]> ItemsByTagZh { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CardTemplate[][]> HeroItemsByTag { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, CardTemplate[][]> HeroItemsByTagZh { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<CardSize, CardTemplate[][]> ItemsBySize { get; } = new();
        public Dictionary<CardSize, CardTemplate[][]> ItemsBySizeZh { get; } = new();
        public Dictionary<CardSize, CardTemplate[][]> HeroItemsBySize { get; } = new();
        public Dictionary<CardSize, CardTemplate[][]> HeroItemsBySizeZh { get; } = new();
        public List<string> AvailableTags { get; } = new();
        public List<CardSize> AvailableSizes { get; } = new();
    }

    private static readonly ConditionalWeakTable<GameDataRepository, SimulationRepoIndex> Cache = new();

    private readonly Dictionary<string, CardMeta> _metaByCardId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CardTemplate> _cardsByInternalName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HeroCardPool> _poolsByHero = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, List<MonsterCandidate>> _monstersByLevel = new();
    private readonly List<MonsterCandidate> _allValidMonsters = new();
    private readonly (double B, double S, double G, double D)[] _normalizedTierWeightsByDay;
    private readonly int _maxDefinedTierDay;

    public static SimulationRepoIndex GetOrCreate(GameDataRepository repo)
    {
        return Cache.GetValue(repo, r => new SimulationRepoIndex(r));
    }

    private SimulationRepoIndex(GameDataRepository repo)
    {
        // 1. 预计算全库物品/技能特征与内部名索引
        foreach (var c in repo.AllItems)
        {
            _metaByCardId[c.Id] = CardMeta.Build(c);
            if (!string.IsNullOrEmpty(c.InternalName))
                _cardsByInternalName.TryAdd(c.InternalName, c);
        }
        foreach (var s in repo.AllSkills)
        {
            _metaByCardId[s.Id] = CardMeta.Build(s);
            if (!string.IsNullOrEmpty(s.InternalName))
                _cardsByInternalName.TryAdd(s.InternalName, s);
        }

        // 2. 预构建 8 大英雄的精简分层池及定向商人子池
        var commonItems = repo.AlwaysSpawnableItems
            .Where(c => c.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var commonSkills = repo.AlwaysSpawnableSkills
            .Where(s => s.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var heroItems = repo.AlwaysSpawnableItems
                .Where(c => c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase))
                .ToList();
            var heroSkills = repo.AlwaysSpawnableSkills
                .Where(s => s.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var combinedItems = heroItems.Concat(commonItems).Distinct().ToList();

            var pool = new HeroCardPool();
            for (int t = 0; t < 4; t++)
            {
                var tierName = ((CardTier)t).ToString();
                pool.HeroItemsByTier[t] = heroItems.Where(c => SupportsTier(c, tierName)).ToArray();
                pool.CommonItemsByTier[t] = commonItems.Where(c => SupportsTier(c, tierName)).ToArray();
                pool.SkillsByTier[t] = heroSkills.Concat(commonSkills).Where(s => SupportsTier(s, tierName)).Distinct().ToArray();

                pool.HeroItemsByTierZh[t] = pool.HeroItemsByTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                pool.CommonItemsByTierZh[t] = pool.CommonItemsByTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                pool.SkillsByTierZh[t] = pool.SkillsByTier[t].Where(s => repo.Localization == null || repo.HasOfficialZhTitle(s)).ToArray();
            }

            // 预建 Tag 定向商人子池（区分纯本英雄独占池与含 Common 的兜底池，优先保障英雄流派纯度）
            foreach (var tag in MerchantFilterTags)
            {
                var tagMatched = combinedItems.Where(c => MatchesMerchantTag(c, tag)).ToList();
                if (tagMatched.Count < 3) continue;

                // 为提高英雄本职定向命中率，英雄卡在兜底混合子池内保留 3 倍采样槽位
                var weightedList = new List<CardTemplate>(tagMatched.Count * 2);
                var heroOnlyList = new List<CardTemplate>(tagMatched.Count);
                foreach (var c in tagMatched)
                {
                    weightedList.Add(c);
                    if (c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase))
                    {
                        heroOnlyList.Add(c);
                        weightedList.Add(c);
                        weightedList.Add(c);
                    }
                }

                var byTier = new CardTemplate[4][];
                var byTierZh = new CardTemplate[4][];
                var heroByTier = new CardTemplate[4][];
                var heroByTierZh = new CardTemplate[4][];
                for (int t = 0; t < 4; t++)
                {
                    var tierName = ((CardTier)t).ToString();
                    byTier[t] = weightedList.Where(c => SupportsTier(c, tierName)).ToArray();
                    byTierZh[t] = byTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                    heroByTier[t] = heroOnlyList.Where(c => SupportsTier(c, tierName)).ToArray();
                    heroByTierZh[t] = heroByTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                }
                pool.ItemsByTag[tag] = byTier;
                pool.ItemsByTagZh[tag] = byTierZh;
                pool.HeroItemsByTag[tag] = heroByTier;
                pool.HeroItemsByTagZh[tag] = heroByTierZh;
                pool.AvailableTags.Add(tag);
            }

            // 预建尺寸定向商人子池（Medium / Large，同样区分纯本英雄池与含 Common 兜底池）
            foreach (var size in new[] { CardSize.Medium, CardSize.Large })
            {
                var sizeMatched = combinedItems.Where(c => c.CardSize == size).ToList();
                if (sizeMatched.Count < 3) continue;

                var weightedList = new List<CardTemplate>(sizeMatched.Count * 2);
                var heroOnlyList = new List<CardTemplate>(sizeMatched.Count);
                foreach (var c in sizeMatched)
                {
                    weightedList.Add(c);
                    if (c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase))
                    {
                        heroOnlyList.Add(c);
                        weightedList.Add(c);
                        weightedList.Add(c);
                    }
                }

                var byTier = new CardTemplate[4][];
                var byTierZh = new CardTemplate[4][];
                var heroByTier = new CardTemplate[4][];
                var heroByTierZh = new CardTemplate[4][];
                for (int t = 0; t < 4; t++)
                {
                    var tierName = ((CardTier)t).ToString();
                    byTier[t] = weightedList.Where(c => SupportsTier(c, tierName)).ToArray();
                    byTierZh[t] = byTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                    heroByTier[t] = heroOnlyList.Where(c => SupportsTier(c, tierName)).ToArray();
                    heroByTierZh[t] = heroByTier[t].Where(c => repo.Localization == null || repo.HasOfficialZhTitle(c)).ToArray();
                }
                pool.ItemsBySize[size] = byTier;
                pool.ItemsBySizeZh[size] = byTierZh;
                pool.HeroItemsBySize[size] = heroByTier;
                pool.HeroItemsBySizeZh[size] = heroByTierZh;
                pool.AvailableSizes.Add(size);
            }

            _poolsByHero[hero] = pool;
        }

        // 3. 预构建并归一化每日品质概率表（兼容 Day 12 总和为 0.8 的官方原始配置）
        var rawTable = repo.GameMode.ItemSkillSpawnTierPercantagesByDay;
        int maxDay = 20;
        if (rawTable.Count > 0)
        {
            foreach (var k in rawTable.Keys)
            {
                if (int.TryParse(k, out int d) && d > maxDay)
                    maxDay = d;
            }
        }
        _maxDefinedTierDay = maxDay;
        _normalizedTierWeightsByDay = new (double, double, double, double)[maxDay + 1];

        for (int d = 1; d <= maxDay; d++)
        {
            if (rawTable.TryGetValue(d.ToString(), out var row) && row.Count > 0)
            {
                double b = Math.Max(0, row.GetValueOrDefault("Bronze", 0));
                double s = Math.Max(0, row.GetValueOrDefault("Silver", 0));
                double g = Math.Max(0, row.GetValueOrDefault("Gold", 0));
                double di = Math.Max(0, row.GetValueOrDefault("Diamond", 0));
                double sum = b + s + g + di;
                if (sum > 1e-6)
                {
                    _normalizedTierWeightsByDay[d] = (b / sum, s / sum, g / sum, di / sum);
                    continue;
                }
            }
            _normalizedTierWeightsByDay[d] = GetFallbackTierWeights(d);
        }

        // 4. 预构建合法 PvE 野怪与战斗原型状态
        foreach (var enc in repo.AlwaysSpawnableCombats)
        {
            if (enc.InternalName.Contains("PvP", StringComparison.OrdinalIgnoreCase) ||
                enc.InternalName.Contains("Greenheart", StringComparison.OrdinalIgnoreCase))
                continue;

            var mid = enc.CombatantType?.MonsterTemplateId;
            var monster = repo.GetMonster(mid);
            if (monster == null) continue;

            int lvl = (int)Math.Round(monster.Player.Attributes.GetValueOrDefault("Level", 0));
            int hp = (int)Math.Round(monster.Player.Attributes.GetValueOrDefault("HealthMax", 0));
            if (lvl <= 0 || hp <= 0) continue;

            var monsterState = CombatSimulator.BuildMonsterPlayerState(repo, monster);

            var lootItems = new List<(CardTemplate, CardTier, string?)>();
            foreach (var mi in monster.Player.Hand.Items)
            {
                var tpl = repo.GetCard(mi.TemplateId);
                if (tpl == null || tpl.Type != "Item") continue;
                var tier = ClampSupportedTier(tpl, TierHelper.ParseTier(mi.Tier));
                string? ench = (!string.IsNullOrEmpty(mi.EnchantmentType) &&
                                EnchantmentNames.All.Contains(mi.EnchantmentType, StringComparer.Ordinal) &&
                                tpl.Enchantments != null &&
                                tpl.Enchantments.ContainsKey(mi.EnchantmentType))
                    ? mi.EnchantmentType
                    : null;
                lootItems.Add((tpl, tier, ench));
            }

            var lootSkills = new List<(CardTemplate, CardTier)>();
            foreach (var ms in monster.Player.Skills)
            {
                var tpl = repo.GetCard(ms.TemplateId);
                if (tpl == null || tpl.Type != "Skill") continue;
                var tier = ClampSupportedTier(tpl, TierHelper.ParseTier(ms.Tier));
                lootSkills.Add((tpl, tier));
            }

            var candidate = new MonsterCandidate
            {
                Encounter = enc,
                Monster = monster,
                MonsterState = monsterState,
                Level = lvl,
                HealthMax = hp,
                GoldReward = Math.Max(2, enc.RewardCombatGold ?? (2 + lvl / 4)),
                XpReward = Math.Max(2, enc.RewardCombatXp ?? 3),
                LootItems = lootItems,
                LootSkills = lootSkills
            };

            if (!_monstersByLevel.TryGetValue(lvl, out var list))
            {
                list = new List<MonsterCandidate>();
                _monstersByLevel[lvl] = list;
            }
            list.Add(candidate);
            _allValidMonsters.Add(candidate);
        }

        foreach (var list in _monstersByLevel.Values)
            list.Sort((a, b) => a.HealthMax.CompareTo(b.HealthMax));
    }

    public CardMeta GetMeta(CardTemplate tpl)
    {
        if (_metaByCardId.TryGetValue(tpl.Id, out var meta))
            return meta;
        return CardMeta.Build(tpl);
    }

    public bool TryGetCardByInternalName(string internalName, out CardTemplate? card)
        => _cardsByInternalName.TryGetValue(internalName, out card);

    public HeroCardPool? GetHeroPool(string hero)
        => _poolsByHero.TryGetValue(hero, out var pool) ? pool : null;

    private static bool MatchesMerchantTag(CardTemplate tpl, string tag)
    {
        if (tpl.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return true;
        if (tpl.HiddenTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) return true;
        if (string.Equals(tag, "Crit", StringComparison.OrdinalIgnoreCase) && tpl.HiddenTags.Contains("CritReference", StringComparer.OrdinalIgnoreCase))
            return true;
        if (string.Equals(tag, "Value", StringComparison.OrdinalIgnoreCase) && tpl.HiddenTags.Contains("EconomyReference", StringComparer.OrdinalIgnoreCase))
            return true;
        if (string.Equals(tag, "Weapon", StringComparison.OrdinalIgnoreCase) && tpl.HiddenTags.Contains("Damage", StringComparer.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public CardTier RollShopTier(int day, Random rng)
    {
        int idx = Math.Clamp(day, 1, _maxDefinedTierDay);
        var (b, s, g, _) = _normalizedTierWeightsByDay[idx];
        double r = rng.NextDouble();
        if (r < b) return CardTier.Bronze;
        r -= b;
        if (r < s) return CardTier.Silver;
        r -= s;
        if (r < g) return CardTier.Gold;
        return CardTier.Diamond;
    }

    public List<MonsterCandidate> GetMonsterChoicesForDay(int day, Random rng)
    {
        if (_allValidMonsters.Count == 0) return new List<MonsterCandidate>();

        int targetLvl = Math.Clamp(day, 1, 15);
        List<MonsterCandidate> pool;
        if (_monstersByLevel.TryGetValue(targetLvl, out var exact) && exact.Count > 0)
        {
            pool = exact;
        }
        else
        {
            // 例如 Level 14 在部分补丁中可能为空，自动聚合邻近等级
            pool = new List<MonsterCandidate>();
            for (int delta = 1; delta <= 15 && pool.Count == 0; delta++)
            {
                if (_monstersByLevel.TryGetValue(targetLvl - delta, out var lo)) pool.AddRange(lo);
                if (_monstersByLevel.TryGetValue(targetLvl + delta, out var hi)) pool.AddRange(hi);
            }
            pool.Sort((a, b) => a.HealthMax.CompareTo(b.HealthMax));
        }

        if (pool.Count <= 3) return pool.ToList();

        // 从低/中/高三档各随机挑出 1 位野怪供 Bot 决策
        int third = pool.Count / 3;
        var low = pool[rng.Next(0, Math.Max(1, third))];
        var mid = pool[rng.Next(third, Math.Max(third + 1, third * 2))];
        var high = pool[rng.Next(third * 2, pool.Count)];
        return new List<MonsterCandidate> { low, mid, high };
    }

    public static CardTier ClampSupportedTier(CardTemplate tpl, CardTier target)
    {
        if (tpl.Tiers == null || tpl.Tiers.Count == 0) return target;
        if (tpl.Tiers.ContainsKey(target.ToString())) return target;

        var cur = target;
        while (cur > CardTier.Bronze)
        {
            cur = (CardTier)((int)cur - 1);
            if (tpl.Tiers.ContainsKey(cur.ToString())) return cur;
        }
        foreach (CardTier t in Enum.GetValues<CardTier>())
        {
            if (tpl.Tiers.ContainsKey(t.ToString())) return t;
        }
        return tpl.DefaultTier;
    }

    private static bool SupportsTier(CardTemplate tpl, string tierName)
        => tpl.Tiers == null || tpl.Tiers.Count == 0 || tpl.Tiers.ContainsKey(tierName);

    private static (double B, double S, double G, double D) GetFallbackTierWeights(int day)
    {
        if (day <= 1) return (1.0, 0.0, 0.0, 0.0);
        if (day == 2) return (0.9, 0.1, 0.0, 0.0);
        if (day == 3) return (0.7, 0.3, 0.0, 0.0);
        if (day == 4) return (0.5, 0.5, 0.0, 0.0);
        if (day == 5) return (0.25, 0.75, 0.0, 0.0);
        if (day == 6) return (0.0, 0.95, 0.05, 0.0);
        if (day == 7) return (0.0, 0.80, 0.20, 0.0);
        if (day == 8) return (0.0, 0.60, 0.40, 0.0);
        if (day == 9) return (0.0, 0.35, 0.60, 0.05);
        if (day == 10) return (0.0, 0.20, 0.70, 0.10);
        if (day == 11) return (0.0, 0.25, 0.55, 0.20);
        if (day == 12) return (0.0, 0.25, 0.6875, 0.0625);
        return (0.0, 0.15, 0.60, 0.25);
    }
}

/// <summary>
/// 高速无头 5.0.0 局内发育模拟器，支撑 28,000+ 局多核淘汰赛演化。
/// 完整还原卡槽对称解锁、等级加血、每日品质分布、二合一升阶、Hour 3 PvE 真实野怪实战、附魔台与 Fates 复活机制。
/// </summary>
public sealed class FastRunSimulator : IFastRunSimulator
{
    private readonly ArchetypeBot _bot = new();

    /// <summary>
    /// 初始化高速无头局内发育模拟器实例。
    /// </summary>
    public FastRunSimulator()
    {
    }

    /// <summary>
    /// 初始化一名参赛英雄的 Day 1 开局状态（4 格对称解锁、基础生命、初始金币、声望 20）。
    /// </summary>
    public RunParticipantState CreateParticipant(
        string runId,
        string hero,
        PersonaType persona,
        ArchetypeDefinition archetype,
        int seed,
        GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(archetype);

        // 预热缓存确保并发阶段直接只读命中
        _ = SimulationRepoIndex.GetOrCreate(repo);

        int lv1Bonus = repo.LevelUps.TryGetValue(1, out var lv1) && lv1.HealthIncrease > 0
            ? lv1.HealthIncrease
            : 100;
        int initialHp = 150 + lv1Bonus; // 默认 250（处于 250~300 标准区间）
        int initialPrestige = repo.GameMode.Prestige.PrestigeInitial > 0
            ? repo.GameMode.Prestige.PrestigeInitial
            : 20;

        var ps = new PlayerState(
            playerId: runId,
            displayName: $"{hero}_{persona}_{runId}",
            hero: hero,
            initialHealthMax: initialHp,
            initialPrestige: initialPrestige,
            initialGold: 10,
            unlockedHandSlots: 4)
        {
            IsBot = true,
            Day = 1,
            Hour = 1
        };

        return new RunParticipantState
        {
            RunId = runId,
            Hero = hero,
            Persona = persona,
            Archetype = archetype,
            PlayerState = ps,
            Rng = new Random(seed)
        };
    }

    /// <summary>
    /// 推进单日白天发育（6 小时：商店刷新/购买/升阶、Hour 3 PvE 实战掉落、附魔台与技能商店、升级开槽），返回当晚 PvP 快照。
    /// </summary>
    public PvpBoardSnapshot AdvanceOneDayToPvp(
        RunParticipantState participant,
        int day,
        GameDataRepository repo,
        IArchetypeCatalog catalog,
        FastRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(catalog);

        var opts = options ?? new FastRunOptions();
        bool requireZh = opts.RequireOfficialZhCards && repo.Localization != null;
        var index = SimulationRepoIndex.GetOrCreate(repo);
        var ps = participant.PlayerState;
        var rng = participant.Rng;

        ps.Day = Math.Max(1, day);

        // 1. 每日晨间阶段（Day 1 起手开局奖励；Day 2+ 结算每日收入、局外日初触发与地产/价值永久成长）
        if (ps.Day == 1 && ps.Hand.Cards.Count == 0 && ps.Skills.Count == 0)
        {
            ApplyStartRunChoices(participant, index, repo, requireZh);
        }
        else if (ps.Day > 1)
        {
            ps.Gold += Math.Max(0, ps.Income);
            ArchetypeBot.FireSafeOutOfCombatTriggers(ps, "TTriggerOnDayStarted", null, repo, rng, index);
            ApplyDailyPermanentGrowth(participant, index);
        }

        int xpPerHour = Math.Max(1, repo.GameMode.ExperiencePerHour);
        int daytimeHours = Math.Max(1, Math.Clamp(repo.GameMode.HoursInADay, 5, 6) - 1); // 每日 5 个白天小时，第 6 小时为晚间 PvP

        // 2. 逐小时推进白天发育（Hours 1..5，Hour 3 为固定 PvE 野怪战）
        for (int hour = 1; hour <= daytimeHours; hour++)
        {
            ps.Hour = hour;
            ArchetypeBot.FireSafeOutOfCombatTriggers(ps, "TTriggerOnHourStarted", null, repo, rng, index);
            AddExperience(participant, xpPerHour, repo, index, requireZh);

            if (hour == 3)
            {
                RunHour3PveCombat(participant, repo, index, requireZh);
                continue;
            }

            RunDaytimeHourEncounter(participant, hour, repo, index, requireZh);
        }
        ps.Hour = daytimeHours + 1;

        // 3. FlexPivot 在 Day 3~6 评估是否动态转型至更契合手中核心与野怪跨职业掉落的新蓝图
        if (participant.Persona == PersonaType.FlexPivot && ps.Day >= 3 && ps.Day <= 6)
        {
            _bot.TryPivotArchetype(participant, catalog, index);
        }

        // 3.5 非萌新主动消化 Stash 中的 1 格纯卖出消耗品（Spare Change / 口香糖 / 战利品等）并触发卖出成长
        _bot.DigestStashConsumables(participant, repo, index);

        // 4. 赛前最优子集上阵与智能站位重排（CluelessNewbie/MessyHybrid 内部有专属松散/残缺策略）
        _bot.OptimizeAndPositionBoard(participant, index, repo);

        // 5. 保底检查：非 CluelessNewbie 且手牌为空时兜底购置 1 张青铜物品（CluelessNewbie 若前天全部未买也可能剩 0-1 件，仅当完全空场才补 1 件确保合法交战）
        if (ps.Hand.Cards.Count == 0)
        {
            var fallback = RollItemOffers(participant, 1, 1, index, repo, requireZh, merchant: null);
            if (fallback.Count > 0)
            {
                var card = new CardInstance(fallback[0].Template, fallback[0].Tier);
                ps.Hand.TryAddAuto(card);
            }
        }

        // 导出快照前清理所有局外临时光环加成，防止 AuraAdd/AuraMult 混入基线快照并导致入战双重叠加
        ps.ClearAuraModifiers();
        foreach (var card in ps.Hand.Cards) card.ClearAuraModifiers();
        foreach (var card in ps.Stash.Cards) card.ClearAuraModifiers();
        foreach (var skill in ps.Skills) skill.ClearAuraModifiers();

        var snapshot = BoardSnapshotMapper.ToSnapshot(participant, repo);
        participant.Snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// 结算当晚 PvP 战斗胜负、声望扣除、首次归零触发 Fates 复活及十胜/淘汰/最大天数完结判定。
    /// </summary>
    public void ApplyPvpOutcome(
        RunParticipantState participant,
        PvpBoardSnapshot todaySnapshot,
        PvpMatchOutcome outcome,
        GameDataRepository repo,
        FastRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(todaySnapshot);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(repo);

        var opts = options ?? new FastRunOptions();
        var ps = participant.PlayerState;
        todaySnapshot.MatchOutcome = outcome;

        int winTarget = opts.VictoriesToWin > 0 ? opts.VictoriesToWin : repo.GameMode.VictoriesToWin;
        if (winTarget <= 0) winTarget = 10;

        if (outcome.Won)
        {
            ps.Wins++;
            if (ps.Wins >= winTarget)
            {
                participant.IsVictory = true;
                if (!opts.AllowEndlessAfterVictory)
                    participant.IsFinished = true;
            }
        }
        else
        {
            ps.Losses++;
            int prestigeLoss = Math.Max(1, todaySnapshot.Day);
            int nextPrestige = ps.Prestige - prestigeLoss;

            int fatesThreshold = repo.GameMode.Fates.SpawnAtOrBelowPrestigeValue;
            if (nextPrestige <= fatesThreshold)
            {
                if (!ps.FatesUsed)
                {
                    ps.FatesUsed = true;
                    ps.Prestige = Math.Max(1, repo.GameMode.Fates.AfterFatesPrestigeValue);
                    ApplyFatesBlessing(participant, repo);
                }
                else
                {
                    ps.Prestige = 0;
                    ps.IsEliminated = true;
                    ps.EliminatedDay = todaySnapshot.Day;
                    participant.IsFinished = true;
                }
            }
            else
            {
                ps.Prestige = nextPrestige;
            }
        }

        if (!ps.IsEliminated)
        {
            ArchetypeBot.FireSafeOutOfCombatTriggers(
                ps,
                "TTriggerOnFightEnded",
                null,
                repo,
                participant.Rng,
                SimulationRepoIndex.GetOrCreate(repo),
                combatType: "PVP",
                combatOutcome: outcome.Won ? "Win" : "Lose");
        }

        if (todaySnapshot.Day >= opts.MaxDay)
        {
            participant.IsFinished = true;
        }
    }

    /// <summary>
    /// 汇总单局完整生命周期记录。
    /// </summary>
    public PvpRunRecord BuildRunRecord(RunParticipantState participant, GameDataRepository repo)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(repo);

        var ps = participant.PlayerState;
        int finalDay = participant.Snapshots.Count > 0 ? participant.Snapshots[^1].Day : ps.Day;
        string version = string.IsNullOrWhiteSpace(repo.GameMode.Version) ? "5.0.0" : repo.GameMode.Version;

        return new PvpRunRecord
        {
            RunId = participant.RunId,
            Version = version,
            Source = "synthetic_selfplay",
            Hero = participant.Hero,
            Persona = participant.Persona,
            Archetype = participant.Archetype.Id,
            FinalDay = finalDay,
            FinalWins = ps.Wins,
            FinalLosses = ps.Losses,
            IsVictory = participant.IsVictory || ps.Wins >= 10,
            DayBoards = participant.Snapshots.ToList()
        };
    }

    internal const int MaxEnchantedItemsPerRun = 4;

    private void ApplyStartRunChoices(
        RunParticipantState participant,
        SimulationRepoIndex index,
        GameDataRepository repo,
        bool requireZh)
    {
        var ps = participant.PlayerState;
        var rng = participant.Rng;

        // 起始青铜技能三选一
        var skillOffers = RollSkillOffers(participant, 1, 3, index, repo, requireZh);
        if (skillOffers.Count > 0)
        {
            var pickedSkill = participant.Persona == PersonaType.CluelessNewbie
                ? skillOffers[rng.Next(skillOffers.Count)]
                : skillOffers
                    .OrderByDescending(o => _bot.ScoreSkill(o.Template, o.Tier, participant, index.GetMeta(o.Template)))
                    .First();
            ps.Skills.Add(new CardInstance(pickedSkill.Template, pickedSkill.Tier));
        }

        // 按画像给予开局起步资源：贪经济/崩盘哥选金币收入，萌新盲选，其余从三张起始青铜卡择优
        if (participant.Persona == PersonaType.GreedyBust)
        {
            ps.Gold += 6;
            ps.Attributes["Income"] = ps.Income + 2;
        }
        else if (participant.Persona == PersonaType.GreedyEco)
        {
            ps.Gold += 4;
            ps.Attributes["Income"] = ps.Income + 1;
        }
        else
        {
            var starterOffers = RollItemOffers(participant, 1, 3, index, repo, requireZh, merchant: null);
            if (starterOffers.Count > 0)
            {
                var pickedStarter = participant.Persona == PersonaType.CluelessNewbie
                    ? starterOffers[rng.Next(starterOffers.Count)]
                    : starterOffers
                        .OrderByDescending(o => _bot.ScoreItem(o.Template, o.Tier, participant, index.GetMeta(o.Template)))
                        .First();
                ps.Hand.TryAddAuto(new CardInstance(pickedStarter.Template, pickedStarter.Tier));
            }
        }
    }

    private void RunDaytimeHourEncounter(
        RunParticipantState participant,
        int hour,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        var ps = participant.PlayerState;
        var rng = participant.Rng;
        int day = ps.Day;

        // Hour 5（Day 7 起）按真实稀缺概率出现附魔台机会（Day 7~9: 6%、Day 10~12: 10%、Day 13+: 14%），且单局自然上限 <= 4
        if (hour == 5 && day >= 7 && CountEnchantedItems(ps) < MaxEnchantedItemsPerRun && ps.Hand.Cards.Count > 0)
        {
            double enchantChance = day switch
            {
                <= 9 => 0.06,
                <= 12 => 0.10,
                _ => 0.14
            };

            if (rng.NextDouble() < enchantChance)
            {
                if (participant.Persona == PersonaType.CluelessNewbie && rng.NextDouble() < 0.65)
                {
                    // 萌新有较高概率错过附魔台或随意浪费机会
                }
                else
                {
                    var poolEnchs = PickOfferedEnchantments(participant, ps, rng, count: 2);
                    if (_bot.TryApplyBestEnchantment(participant, index, poolEnchs, allowOverride: false))
                        return;
                }
            }
        }

        // Hour 2 / Hour 5 交替出现技能商店、升阶台、训练强化或定向物品商店
        if (hour == 2 || hour == 5)
        {
            double roll = rng.NextDouble();

            // 技能商人（Day 2 起按周期出现；萌新/杂拼经常跳过技能投资）
            if (day >= 2 && roll < (day % 2 == 1 ? 0.36 : 0.22))
            {
                if (participant.Persona == PersonaType.CluelessNewbie && rng.NextDouble() < 0.55)
                {
                    RunItemShop(participant, repo, index, requireZh);
                    return;
                }
                RunSkillShop(participant, repo, index, requireZh);
                return;
            }

            // 升阶铁砧（B1&B2 / Forja，Day 4 起按真实节奏概率出现，抑制中后期满盘钻石膨胀）
            if (day >= 4 && roll < 0.43 && ps.Hand.Cards.Any(c => c.CanUpgrade()))
            {
                bool skipUpgrade = (participant.Persona == PersonaType.CluelessNewbie && rng.NextDouble() < 0.75)
                                || (participant.Persona == PersonaType.Casual && rng.NextDouble() < 0.50);
                if (!skipUpgrade)
                {
                    int diamondCount = ps.Hand.Cards.Count(c => c.Tier == CardTier.Diamond);
                    CardTier maxAnvilTier = day switch
                    {
                        <= 7 => CardTier.Gold,
                        <= 10 => (diamondCount < 2 && rng.NextDouble() < 0.30) ? CardTier.Diamond : CardTier.Gold,
                        <= 12 => (diamondCount < 3 && rng.NextDouble() < 0.50) ? CardTier.Diamond : CardTier.Gold,
                        _ => CardTier.Diamond
                    };
                    if (UpgradeBestHandCard(participant, index, repo, maxAnvilTier))
                        return;
                }
            }

            // 永久属性训练 / 经济事件
            if (roll < 0.72)
            {
                ApplyTrainingOrEconEvent(participant, index);
                return;
            }
        }

        // 分类定向物品商店（生成 3 位候选商人并按画像择优/盲选进入）
        RunItemShop(participant, repo, index, requireZh);
    }

    private void RunItemShop(
        RunParticipantState participant,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        var ps = participant.PlayerState;
        int day = ps.Day;
        int rerollsUsed = 0;

        var merchants = BuildMerchantCandidates(participant, index);
        var chosenMerchant = _bot.ChooseMerchant(participant, merchants, index);

        int maxShopPages = participant.Persona == PersonaType.HardForcer ? 6 : 5;
        for (int attempt = 0; attempt < maxShopPages; attempt++)
        {
            var offers = RollItemOffers(participant, day, 3, index, repo, requireZh, chosenMerchant);
            if (offers.Count == 0) break;

            // 在同一货架内持续买入所有高性价比物品
            while (offers.Count > 0)
            {
                int bestIdx = _bot.ChooseBestOfferIndex(participant, offers, repo, index);
                if (bestIdx < 0) break;

                var chosen = offers[bestIdx];
                if (_bot.TryExecuteBuyOrMerge(participant, chosen, repo, index))
                    offers.RemoveAt(bestIdx);
                else
                    break;
            }

            int rerollCost = 1 + rerollsUsed;
            if (!_bot.ShouldRerollShop(participant, offers, rerollCost, rerollsUsed, index))
                break;

            if (ps.Gold < rerollCost) break;
            ps.Gold -= rerollCost;
            rerollsUsed++;
        }
    }

    private void RunSkillShop(
        RunParticipantState participant,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        var offers = RollSkillOffers(participant, participant.PlayerState.Day, 3, index, repo, requireZh);
        if (offers.Count == 0) return;

        int bestIdx = _bot.ChooseBestOfferIndex(participant, offers, repo, index);
        if (bestIdx >= 0)
            _bot.TryExecuteBuyOrMerge(participant, offers[bestIdx], repo, index);
    }

    private void RunHour3PveCombat(
        RunParticipantState participant,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        var ps = participant.PlayerState;
        var rng = participant.Rng;
        var candidates = index.GetMonsterChoicesForDay(ps.Day, rng);
        if (candidates.Count == 0) return;

        int pick = _bot.SelectMonsterIndex(participant, candidates, index);
        var chosen = candidates[Math.Clamp(pick, 0, candidates.Count - 1)];

        // 战前快速整理站位以正确触发邻接光环（低分段菜鸟仍按随机/松散站位迎战）
        _bot.ArrangeHandPositions(participant, index);

        int pveSeed = unchecked(ps.Day * 1009 + ps.Wins * 37 + rng.Next());
        var combatResult = CombatSimulator.Simulate(repo, ps, chosen.MonsterState, pveSeed, "PVE", recordTimeline: false, recordEvents: false);
        bool pveWon = string.Equals(combatResult.WinnerPlayerId, ps.PlayerId, StringComparison.Ordinal);

        ArchetypeBot.FireSafeOutOfCombatTriggers(
            ps,
            "TTriggerOnFightEnded",
            null,
            repo,
            rng,
            index,
            combatType: "PVE",
            combatOutcome: pveWon ? "Win" : "Lose");

        if (pveWon)
        {
            ps.Gold += chosen.GoldReward;
            AddExperience(participant, chosen.XpReward, repo, index, requireZh);

            // 按概率掉落野怪棋盘上的跨英雄物品或技能
            if (chosen.LootSkills.Count > 0 && rng.NextDouble() < 0.32)
            {
                var (st, stier) = chosen.LootSkills[rng.Next(chosen.LootSkills.Count)];
                if (!requireZh || repo.HasOfficialZhTitle(st))
                {
                    var dup = ps.Skills.FirstOrDefault(s => string.Equals(s.Template.Id, st.Id, StringComparison.OrdinalIgnoreCase));
                    if (dup != null && dup.Tier == stier && dup.CanUpgrade())
                        dup.TryUpgrade();
                    else if (dup == null)
                        ps.Skills.Add(new CardInstance(st, stier));
                }
            }

            if (chosen.LootItems.Count > 0 && rng.NextDouble() < 0.42)
            {
                var (lt, ltier, lench) = chosen.LootItems[rng.Next(chosen.LootItems.Count)];
                if (!requireZh || repo.HasOfficialZhTitle(lt))
                {
                    // 严格遵守自然附魔上限与前中期稀缺度
                    bool allowDropEnch = !string.IsNullOrEmpty(lench)
                        && CountEnchantedItems(ps) < MaxEnchantedItemsPerRun
                        && (ps.Day >= 7 || rng.NextDouble() < 0.35);

                    bool isHeroOrCommon = lt.Heroes.Contains(participant.Hero, StringComparer.OrdinalIgnoreCase)
                        || lt.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase);
                    double minDropScore = isHeroOrCommon ? 30.0 : 85.0;

                    var dropScore = _bot.ScoreItem(lt, ltier, participant, index.GetMeta(lt));
                    if (dropScore >= minDropScore)
                    {
                        var offer = new ShopOffer(lt, ltier, 0, IsSkill: false);
                        if (_bot.TryExecuteBuyOrMerge(participant, offer, repo, index))
                        {
                            if (allowDropEnch)
                            {
                                var added = ps.Hand.Cards.FirstOrDefault(c => string.Equals(c.Template.Id, lt.Id, StringComparison.OrdinalIgnoreCase) && c.Tier == ltier);
                                if (added != null && string.IsNullOrEmpty(added.Enchantment) && added.Template.Enchantments?.ContainsKey(lench!) == true)
                                {
                                    added.ApplyEnchantment(lench);
                                    ArchetypeBot.FireSafeOutOfCombatTriggers(ps, "TTriggerOnCardEnchanted", added, repo, rng, index);
                                }
                            }
                        }
                        else
                        {
                            ps.Gold += repo.GameMode.StandardPrices.GetItemSellPrice(lt.CardSize, ltier);
                        }
                    }
                    else
                    {
                        ps.Gold += repo.GameMode.StandardPrices.GetItemSellPrice(lt.CardSize, ltier);
                    }
                }
            }
        }
        else
        {
            // 战败安慰经验，野怪不扣声望
            AddExperience(participant, 1, repo, index, requireZh);
        }
    }

    private void AddExperience(
        RunParticipantState participant,
        int xp,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        if (xp <= 0) return;
        var ps = participant.PlayerState;
        int xpPerLevel = Math.Max(1, repo.GameMode.ExperiencePerLevel);
        int maxLevel = Math.Max(1, repo.GameMode.MaxLevel);

        ps.Experience += xp;
        while (ps.Experience >= xpPerLevel && ps.Level < maxLevel)
        {
            ps.Experience -= xpPerLevel;
            int prevLevel = ps.Level;
            ps.Level++;

            int hpGain = repo.LevelUps.TryGetValue(ps.Level, out var lu) && lu.HealthIncrease > 0
                ? lu.HealthIncrease
                : Math.Min(800, 100 + (ps.Level - 1) * 50);

            ps.ModifyAttribute("HealthMax", "Add", hpGain);
            ps.CurrentHealth = ps.HealthMax;

            // 5.0.0 规则：Lv2 -> 6格，Lv3 -> 8格，Lv4+ -> 10格
            int targetSlots = ps.Level switch
            {
                1 => 4,
                2 => 6,
                3 => 8,
                _ => 10
            };
            ps.Hand.UnlockCountCentered(targetSlots);

            ArchetypeBot.FireSafeOutOfCombatTriggers(
                ps,
                "TTriggerOnPlayerAttributeChanged",
                null,
                repo,
                participant.Rng,
                index,
                changedAttr: "Level",
                prevVal: prevLevel,
                curVal: ps.Level);

            ApplyLevelUpReward(participant, repo, index, requireZh);

            // Lv10 里程碑固定 1 次附魔机会（受单局 4 件附魔上限约束）
            if (ps.Level == 10 && CountEnchantedItems(ps) < MaxEnchantedItemsPerRun && ps.Hand.Cards.Count > 0)
            {
                var milestoneEnchs = PickOfferedEnchantments(participant, ps, participant.Rng, count: 2);
                _bot.TryApplyBestEnchantment(participant, index, milestoneEnchs, allowOverride: false);
            }
        }
    }

    private void ApplyLevelUpReward(
        RunParticipantState participant,
        GameDataRepository repo,
        SimulationRepoIndex index,
        bool requireZh)
    {
        var ps = participant.PlayerState;
        var choices = new List<LevelUpRewardKind>
        {
            LevelUpRewardKind.MaxHealthBoost,
            LevelUpRewardKind.GoldAndIncome,
            LevelUpRewardKind.PermanentPowerBuff
        };

        if (ps.Level >= 3) choices.Add(LevelUpRewardKind.SkillChoice);
        if (ps.Level >= 5 && ps.Hand.Cards.Any(c => c.CanUpgrade()))
            choices.Add(LevelUpRewardKind.ItemTierUpgrade);

        // 随机取 3 项做多选一
        while (choices.Count > 3)
            choices.RemoveAt(participant.Rng.Next(choices.Count));

        var picked = _bot.ChooseLevelUpReward(participant, choices);
        switch (picked)
        {
            case LevelUpRewardKind.MaxHealthBoost:
                int extraHp = 50 + ps.Level * 15;
                ps.ModifyAttribute("HealthMax", "Add", extraHp);
                ps.CurrentHealth = ps.HealthMax;
                break;

            case LevelUpRewardKind.GoldAndIncome:
                ps.Gold += 4 + Math.Min(6, ps.Level / 2);
                if (ps.Level % 2 == 0)
                    ps.Attributes["Income"] = ps.Income + 1;
                break;

            case LevelUpRewardKind.PermanentPowerBuff:
                ApplyPermanentBuffToBestCards(participant, index, strength: (1.0 + ps.Level * 0.15) * 0.60);
                break;

            case LevelUpRewardKind.SkillChoice:
                var skOffers = RollSkillOffers(participant, ps.Day, 3, index, repo, requireZh);
                if (skOffers.Count > 0)
                {
                    var bestSk = participant.Persona == PersonaType.CluelessNewbie
                        ? skOffers[participant.Rng.Next(skOffers.Count)]
                        : skOffers
                            .OrderByDescending(o => _bot.ScoreSkill(o.Template, o.Tier, participant, index.GetMeta(o.Template)))
                            .First();
                    var existing = ps.Skills.FirstOrDefault(s =>
                        string.Equals(s.Template.Id, bestSk.Template.Id, StringComparison.OrdinalIgnoreCase) &&
                        s.Tier == bestSk.Tier && s.CanUpgrade());
                    if (existing != null) existing.TryUpgrade();
                    else if (ps.Skills.All(s => !string.Equals(s.Template.Id, bestSk.Template.Id, StringComparison.OrdinalIgnoreCase)))
                        ps.Skills.Add(new CardInstance(bestSk.Template, bestSk.Tier));
                    else
                        ps.Gold += 5;
                }
                break;

            case LevelUpRewardKind.ItemTierUpgrade:
                {
                    int diamondCount = ps.Hand.Cards.Count(c => c.Tier == CardTier.Diamond);
                    CardTier maxLevelUpgradeTier = ps.Level <= 8
                        ? CardTier.Gold
                        : (ps.Level <= 11
                            ? (diamondCount < 2 && participant.Rng.NextDouble() < 0.40 ? CardTier.Diamond : CardTier.Gold)
                            : CardTier.Diamond);
                    if (!UpgradeBestHandCard(participant, index, repo, maxLevelUpgradeTier))
                        ApplyPermanentBuffToBestCards(participant, index, strength: 0.85);
                }
                break;
        }
    }

    private void ApplyDailyPermanentGrowth(RunParticipantState participant, SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        foreach (var card in ps.Hand.Cards)
        {
            var meta = index.GetMeta(card.Template);
            if (meta.IsProperty || meta.IsValueScaler)
            {
                int valGain = Math.Max(1, (1 + (int)card.Tier) / 2);
                card.ModifyAttribute("SellPrice", "Add", valGain, permanent: true);

                if (card.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0)
                    card.ModifyAttribute("DamageAmount", "Add", 1 + (int)card.Tier, permanent: true);
                if (card.BaseAttributes.GetValueOrDefault("ShieldApplyAmount", 0) > 0)
                    card.ModifyAttribute("ShieldApplyAmount", "Add", 2 + (int)card.Tier, permanent: true);
            }
            else if (meta.IsScaler && card.Tier >= CardTier.Silver)
            {
                GrowCardPrimaryAttribute(card, (1.0 + (int)card.Tier * 0.4) * 0.60);
            }
        }
    }

    private void ApplyTrainingOrEconEvent(RunParticipantState participant, SimulationRepoIndex index)
    {
        var ps = participant.PlayerState;
        if (participant.Persona is PersonaType.GreedyEco or PersonaType.GreedyBust || ps.Hand.Cards.Count == 0)
        {
            ps.Gold += 3 + Math.Min(4, ps.Day / 3);
            if (participant.Rng.NextDouble() < 0.55)
                ps.Attributes["Income"] = ps.Income + 1;
            foreach (var c in ps.Hand.Cards)
            {
                if (index.GetMeta(c.Template).IsProperty || index.GetMeta(c.Template).IsValueScaler)
                    c.ModifyAttribute("SellPrice", "Add", 1, permanent: true);
            }
            return;
        }

        if (participant.Persona is not (PersonaType.CluelessNewbie or PersonaType.GreedyBust))
        {
            double hpEventProb = participant.Persona == PersonaType.TempoAggro
                || string.Equals(participant.Hero, HeroNames.Pygmalien, StringComparison.OrdinalIgnoreCase)
                ? 0.45
                : 0.30;
            if (participant.Rng.NextDouble() < hpEventProb)
            {
                int bonusHp = participant.Rng.Next(45, 111) + ps.Day * 8;
                ps.ModifyAttribute("HealthMax", "Add", bonusHp);
                ps.CurrentHealth = ps.HealthMax;
                return;
            }
        }

        ApplyPermanentBuffToBestCards(participant, index, strength: (1.0 + ps.Day * 0.12) * 0.60);
    }

    private void ApplyPermanentBuffToBestCards(RunParticipantState participant, SimulationRepoIndex index, double strength)
    {
        var ps = participant.PlayerState;
        if (ps.Hand.Cards.Count == 0)
        {
            ps.Gold += 4;
            return;
        }

        var sorted = ps.Hand.Cards
            .OrderByDescending(c => _bot.ScoreItem(c.Template, c.Tier, participant, index.GetMeta(c.Template)))
            .Take(2)
            .ToList();

        foreach (var card in sorted)
            GrowCardPrimaryAttribute(card, strength);
    }

    private static void GrowCardPrimaryAttribute(CardInstance card, double scale)
    {
        bool modified = false;
        if (card.BaseAttributes.GetValueOrDefault("DamageAmount", 0) > 0)
        {
            card.ModifyAttribute("DamageAmount", "Add", Math.Max(1, Math.Round(2.4 * scale)), permanent: true);
            modified = true;
        }
        if (card.BaseAttributes.GetValueOrDefault("ShieldApplyAmount", 0) > 0)
        {
            card.ModifyAttribute("ShieldApplyAmount", "Add", Math.Max(2, Math.Round(3.0 * scale)), permanent: true);
            modified = true;
        }
        if (card.BaseAttributes.GetValueOrDefault("BurnApplyAmount", 0) > 0)
        {
            card.ModifyAttribute("BurnApplyAmount", "Add", Math.Max(1, Math.Round(0.9 * scale)), permanent: true);
            modified = true;
        }
        if (card.BaseAttributes.GetValueOrDefault("PoisonApplyAmount", 0) > 0)
        {
            card.ModifyAttribute("PoisonApplyAmount", "Add", Math.Max(1, Math.Round(0.75 * scale)), permanent: true);
            modified = true;
        }
        if (card.BaseAttributes.GetValueOrDefault("HealAmount", 0) > 0)
        {
            card.ModifyAttribute("HealAmount", "Add", Math.Max(2, Math.Round(3.0 * scale)), permanent: true);
            modified = true;
        }
        if (card.BaseAttributes.GetValueOrDefault("RegenApplyAmount", 0) > 0)
        {
            card.ModifyAttribute("RegenApplyAmount", "Add", Math.Max(1, Math.Round(0.75 * scale)), permanent: true);
            modified = true;
        }

        if (!modified && card.BaseAttributes.GetValueOrDefault("CritChance", 0) > 0)
        {
            card.ModifyAttribute("CritChance", "Add", Math.Min(10, Math.Max(1, Math.Round(2.5 * scale))), permanent: true);
        }
    }

    private bool UpgradeBestHandCard(
        RunParticipantState participant,
        SimulationRepoIndex index,
        GameDataRepository repo,
        CardTier maxTargetTier = CardTier.Diamond)
    {
        var candidate = participant.PlayerState.Hand.Cards
            .Where(c => c.CanUpgrade() && (int)c.Tier < (int)maxTargetTier)
            .OrderByDescending(c =>
            {
                double score = _bot.ScoreItem(c.Template, c.Tier, participant, index.GetMeta(c.Template));
                // 优先补齐青铜/白银短板，减少后期满盘钻石中混入单个突兀青铜杂卡的割裂感
                if (c.Tier <= CardTier.Silver && (participant.Archetype.CoreItemNames.Contains(c.Template.InternalName, StringComparer.OrdinalIgnoreCase)
                    || participant.Archetype.SupportItemNames.Contains(c.Template.InternalName, StringComparer.OrdinalIgnoreCase)))
                {
                    score += 18.0;
                }
                return score;
            })
            .FirstOrDefault();

        if (candidate == null) return false;
        if (!candidate.TryUpgrade()) return false;

        ArchetypeBot.FireSafeOutOfCombatTriggers(participant.PlayerState, "TTriggerOnCardUpgraded", candidate, repo, participant.Rng, index);
        return true;
    }

    private void ApplyFatesBlessing(RunParticipantState participant, GameDataRepository repo)
    {
        var index = SimulationRepoIndex.GetOrCreate(repo);
        var ps = participant.PlayerState;
        ps.ModifyAttribute("HealthMax", "Add", 150 + ps.Day * 25);
        ps.CurrentHealth = ps.HealthMax;

        if (CountEnchantedItems(ps) < MaxEnchantedItemsPerRun)
        {
            var fatesEnchs = PickOfferedEnchantments(participant, ps, participant.Rng, count: 2);
            _bot.TryApplyBestEnchantment(participant, index, offeredEnchantments: fatesEnchs, allowOverride: false);
        }
        UpgradeBestHandCard(participant, index, repo);
    }

    internal static int CountEnchantedItems(PlayerState ps)
    {
        int c = 0;
        foreach (var card in ps.Hand.Cards)
            if (!string.IsNullOrEmpty(card.Enchantment)) c++;
        foreach (var card in ps.Stash.Cards)
            if (!string.IsNullOrEmpty(card.Enchantment)) c++;
        return c;
    }

    private static List<string> PickOfferedEnchantments(RunParticipantState participant, PlayerState ps, Random rng, int count)
    {
        var existingOnBoard = new HashSet<string>(
            ps.Hand.Cards.Where(c => !string.IsNullOrEmpty(c.Enchantment)).Select(c => c.Enchantment!),
            StringComparer.OrdinalIgnoreCase);

        // 按蓝图多种偏好与全 13 种附魔加权抽样，避免每盘重复刷出单一黑曜石
        var weightedPool = new List<(string Ench, double Weight)>(EnchantmentNames.All.Length);
        foreach (var ench in EnchantmentNames.All)
        {
            double w = 1.0;
            if (participant.Archetype.PreferredEnchantments.TryGetValue(ench, out var prefW))
                w += prefW * 1.4;
            if (existingOnBoard.Contains(ench))
                w *= 0.25; // 若场上已有同款附魔，降低再次出现比重
            weightedPool.Add((ench, Math.Max(0.15, w)));
        }

        var result = new List<string>(count);
        for (int i = 0; i < count && weightedPool.Count > 0; i++)
        {
            double sum = weightedPool.Sum(x => x.Weight);
            double r = rng.NextDouble() * sum;
            double acc = 0;
            int pickedIdx = weightedPool.Count - 1;
            for (int k = 0; k < weightedPool.Count; k++)
            {
                acc += weightedPool[k].Weight;
                if (r <= acc)
                {
                    pickedIdx = k;
                    break;
                }
            }
            result.Add(weightedPool[pickedIdx].Ench);
            weightedPool.RemoveAt(pickedIdx);
        }
        return result;
    }

    private static List<MerchantSpec> BuildMerchantCandidates(RunParticipantState participant, SimulationRepoIndex index)
    {
        var pool = index.GetHeroPool(participant.Hero);
        var rng = participant.Rng;
        var result = new List<MerchantSpec>(3);

        if (pool == null)
        {
            result.Add(new MerchantSpec("General", null, null));
            return result;
        }

        // 收集流派偏好的 Tag 与核心大件尺寸
        var preferredTags = new List<string>();
        foreach (var t in pool.AvailableTags)
        {
            if (participant.Archetype.PreferredTags.ContainsKey(t) || participant.Archetype.PreferredHiddenTags.ContainsKey(t))
                preferredTags.Add(t);
        }
        foreach (var coreName in participant.Archetype.CoreItemNames)
        {
            if (index.TryGetCardByInternalName(coreName, out var coreTpl) && coreTpl != null)
            {
                foreach (var t in coreTpl.Tags.Concat(coreTpl.HiddenTags))
                {
                    if (pool.ItemsByTag.ContainsKey(t) && !preferredTags.Contains(t, StringComparer.OrdinalIgnoreCase))
                        preferredTags.Add(t);
                }
            }
        }

        // 商人 1：Tag 定向商（约 72% 概率包含一个流派核心相关 Tag 商）
        string? firstTag = null;
        if (pool.AvailableTags.Count > 0)
        {
            firstTag = (preferredTags.Count > 0 && rng.NextDouble() < 0.72)
                ? preferredTags[rng.Next(preferredTags.Count)]
                : pool.AvailableTags[rng.Next(pool.AvailableTags.Count)];
            result.Add(new MerchantSpec($"{firstTag} Merchant", firstTag, null));
        }

        // 商人 2：尺寸定向商（Medium/Large）或第二个不同 Tag 商
        bool hasLargeCore = participant.Archetype.CoreItemNames.Any(n =>
            index.TryGetCardByInternalName(n, out var tpl) && tpl != null && tpl.CardSize >= CardSize.Medium);

        if (pool.AvailableSizes.Count > 0 && rng.NextDouble() < (hasLargeCore ? 0.65 : 0.40))
        {
            CardSize pickedSize = (hasLargeCore && pool.AvailableSizes.Contains(CardSize.Large) && rng.NextDouble() < 0.65)
                ? CardSize.Large
                : pool.AvailableSizes[rng.Next(pool.AvailableSizes.Count)];
            result.Add(new MerchantSpec($"{pickedSize} Merchant", null, pickedSize));
        }
        else if (pool.AvailableTags.Count > 1)
        {
            string secondTag = pool.AvailableTags[rng.Next(pool.AvailableTags.Count)];
            for (int g = 0; g < 6 && string.Equals(secondTag, firstTag, StringComparison.OrdinalIgnoreCase); g++)
                secondTag = pool.AvailableTags[rng.Next(pool.AvailableTags.Count)];
            result.Add(new MerchantSpec($"{secondTag} Merchant", secondTag, null));
        }

        // 商人 3：通用全池商人
        result.Add(new MerchantSpec("General Merchant", null, null));
        return result;
    }

    private static List<ShopOffer> RollItemOffers(
        RunParticipantState participant,
        int day,
        int count,
        SimulationRepoIndex index,
        GameDataRepository repo,
        bool requireZh,
        MerchantSpec? merchant)
    {
        var pool = index.GetHeroPool(participant.Hero);
        if (pool == null) return new List<ShopOffer>();

        var offers = new List<ShopOffer>(count);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rng = participant.Rng;
        var prices = repo.GameMode.StandardPrices;

        for (int i = 0; i < count; i++)
        {
            var tier = index.RollShopTier(day, rng);
            var bucket = PickBucketForMerchant(pool, tier, merchant, requireZh, rng);

            if (bucket.Length == 0 && merchant.HasValue && (merchant.Value.FilterTag != null || merchant.Value.FilterSize != null))
            {
                // 定向子池在特定天桶品质（如 Day 1 青铜 Large）稀缺时，优先尝试该定向子池的邻近品质
                for (int dt = 0; dt < 4 && bucket.Length == 0; dt++)
                {
                    var ft = (CardTier)dt;
                    bucket = PickBucketForMerchant(pool, ft, merchant, requireZh, rng);
                    if (bucket.Length > 0) tier = ft;
                }
            }

            if (bucket.Length == 0)
            {
                bool preferHero = rng.NextDouble() < 0.90;
                bucket = PickItemBucket(pool, tier, preferHero, requireZh);
                if (bucket.Length == 0) bucket = PickItemBucket(pool, tier, !preferHero, requireZh);
                if (bucket.Length == 0)
                {
                    for (int fallbackT = 0; fallbackT < 4 && bucket.Length == 0; fallbackT++)
                    {
                        var ft = (CardTier)fallbackT;
                        bucket = PickItemBucket(pool, ft, true, requireZh);
                        if (bucket.Length == 0) bucket = PickItemBucket(pool, ft, false, requireZh);
                        if (bucket.Length > 0) tier = ft;
                    }
                }
            }

            if (bucket.Length == 0) break;

            CardTemplate? chosen = null;
            for (int guard = 0; guard < 12; guard++)
            {
                var cand = bucket[rng.Next(bucket.Length)];
                if (usedIds.Add(cand.Id))
                {
                    chosen = cand;
                    break;
                }
            }
            chosen ??= bucket[rng.Next(bucket.Length)];

            var actualTier = SimulationRepoIndex.ClampSupportedTier(chosen, tier);
            int price = index.GetMeta(chosen).GetBuyPrice(chosen, actualTier, prices);
            offers.Add(new ShopOffer(chosen, actualTier, price, IsSkill: false));
        }

        return offers;
    }

    private static CardTemplate[] PickBucketForMerchant(
        SimulationRepoIndex.HeroCardPool pool,
        CardTier tier,
        MerchantSpec? merchant,
        bool requireZh,
        Random rng)
    {
        int tIdx = Math.Clamp((int)tier, 0, 3);
        if (merchant.HasValue)
        {
            var m = merchant.Value;
            if (!string.IsNullOrEmpty(m.FilterTag))
            {
                if (rng.NextDouble() < 0.88)
                {
                    var heroMap = requireZh ? pool.HeroItemsByTagZh : pool.HeroItemsByTag;
                    if (heroMap.TryGetValue(m.FilterTag, out var heroByTier) && heroByTier[tIdx].Length > 0)
                        return heroByTier[tIdx];
                }
                var map = requireZh ? pool.ItemsByTagZh : pool.ItemsByTag;
                if (map.TryGetValue(m.FilterTag, out var byTier))
                    return byTier[tIdx];
            }
            if (m.FilterSize.HasValue)
            {
                if (rng.NextDouble() < 0.88)
                {
                    var heroMap = requireZh ? pool.HeroItemsBySizeZh : pool.HeroItemsBySize;
                    if (heroMap.TryGetValue(m.FilterSize.Value, out var heroByTier) && heroByTier[tIdx].Length > 0)
                        return heroByTier[tIdx];
                }
                var map = requireZh ? pool.ItemsBySizeZh : pool.ItemsBySize;
                if (map.TryGetValue(m.FilterSize.Value, out var byTier))
                    return byTier[tIdx];
            }
        }

        bool preferHero = rng.NextDouble() < 0.90;
        var b = PickItemBucket(pool, tier, preferHero, requireZh);
        return b.Length > 0 ? b : PickItemBucket(pool, tier, !preferHero, requireZh);
    }

    private static List<ShopOffer> RollSkillOffers(
        RunParticipantState participant,
        int day,
        int count,
        SimulationRepoIndex index,
        GameDataRepository repo,
        bool requireZh)
    {
        var pool = index.GetHeroPool(participant.Hero);
        if (pool == null) return new List<ShopOffer>();

        var offers = new List<ShopOffer>(count);
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rng = participant.Rng;
        var prices = repo.GameMode.StandardPrices;

        for (int i = 0; i < count; i++)
        {
            var tier = index.RollShopTier(day, rng);
            var bucket = requireZh ? pool.SkillsByTierZh[(int)tier] : pool.SkillsByTier[(int)tier];
            if (bucket.Length == 0)
            {
                for (int fallbackT = 0; fallbackT < 4 && bucket.Length == 0; fallbackT++)
                {
                    bucket = requireZh ? pool.SkillsByTierZh[fallbackT] : pool.SkillsByTier[fallbackT];
                    if (bucket.Length > 0) tier = (CardTier)fallbackT;
                }
            }
            if (bucket.Length == 0) break;

            CardTemplate? chosen = null;
            for (int guard = 0; guard < 12; guard++)
            {
                var cand = bucket[rng.Next(bucket.Length)];
                if (usedIds.Add(cand.Id))
                {
                    chosen = cand;
                    break;
                }
            }
            chosen ??= bucket[rng.Next(bucket.Length)];

            var actualTier = SimulationRepoIndex.ClampSupportedTier(chosen, tier);
            int price = index.GetMeta(chosen).GetBuyPrice(chosen, actualTier, prices);
            offers.Add(new ShopOffer(chosen, actualTier, price, IsSkill: true));
        }

        return offers;
    }

    private static CardTemplate[] PickItemBucket(SimulationRepoIndex.HeroCardPool pool, CardTier tier, bool heroOnly, bool requireZh)
    {
        int idx = Math.Clamp((int)tier, 0, 3);
        if (heroOnly)
            return requireZh ? pool.HeroItemsByTierZh[idx] : pool.HeroItemsByTier[idx];
        return requireZh ? pool.CommonItemsByTierZh[idx] : pool.CommonItemsByTier[idx];
    }
}

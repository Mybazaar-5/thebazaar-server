using BazaarCore.Data;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Models;

namespace BazaarCore.Dataset.Archetypes;

/// <summary>
/// 方案 A+B 扩展流派蓝图构造器：
/// 1. 将 32 套 5.0.0 校验社区十胜参考体系转为可直接参与发育淘汰赛的正式流派蓝图；
/// 2. 基于本地 GameData.db 卡牌标签、触发耦合与光环语义，为全 8 位英雄各挖掘 24 套互异协同双/三核流派（共 192 套）。
/// </summary>
public static class AutoSynergyArchetypeBuilder
{
    private static readonly (string ThemeTag, string ThemeZh, string[] PreferredTags, string[] PreferredHidden, string[] PreferredTriggers, string[] PreferredEnchs)[] ThemeSpecs =
    [
        ("Burn", "灼烧联动", ["Weapon", "Tool", "Food"], ["Burn", "BurnReference", "Haste"], ["TTriggerOnCardPerformedBurn", "TTriggerOnItemUsed"], ["Fiery", "Turbo", "Shiny"]),
        ("Poison", "剧毒叠层", ["Aquatic", "Potion", "Friend"], ["Poison", "PoisonReference", "Haste"], ["TTriggerOnCardPerformedPoison", "TTriggerOnItemUsed"], ["Toxic", "Turbo", "Heavy"]),
        ("Shield", "叠盾反制", ["Apparel", "Property", "Tech"], ["Shield", "ShieldReference", "Damage"], ["TTriggerOnCardPerformedShield", "TTriggerOnCardFired"], ["Shielded", "Restorative", "Obsidian"]),
        ("WeaponCrit", "暴击强攻", ["Weapon", "Tool"], ["Damage", "Crit", "CritReference"], ["TTriggerOnCardCritted", "TTriggerOnCardFired"], ["Deadly", "Obsidian", "Shiny"]),
        ("Ammo", "弹药倾泻", ["Weapon", "Tech", "Tool"], ["Ammo", "AmmoReference", "Damage"], ["TTriggerOnItemUsed", "TTriggerOnCardFired"], ["Deadly", "Turbo", "Obsidian"]),
        ("Haste", "极速充能", ["Aquatic", "Friend", "Tech"], ["Haste", "HasteReference", "Charge"], ["TTriggerOnCardPerformedHaste", "TTriggerOnItemUsed"], ["Turbo", "Shiny", "Heavy"]),
        ("Slow", "减速控场", ["Trap", "Aquatic", "Tool"], ["Slow", "SlowReference", "Burn"], ["TTriggerOnCardPerformedSlow", "TTriggerOnItemUsed"], ["Heavy", "Fiery", "Toxic"]),
        ("Freeze", "冰霜冻结", ["Relic", "Potion", "Aquatic"], ["Freeze", "FreezeReference", "Slow"], ["TTriggerOnCardPerformedFreeze", "TTriggerOnCardFired"], ["Icy", "Turbo", "Heavy"]),
        ("HealRegen", "回复续航", ["Food", "Potion", "Apparel"], ["Heal", "HealReference", "Regen", "RegenReference"], ["TTriggerOnCardPerformedHeal", "TTriggerOnCardPerformedRegen"], ["Restorative", "Shiny", "Shielded"]),
        ("Property", "地产价值", ["Property", "Relic"], ["EconomyReference", "Value", "Income", "Health"], ["TTriggerOnCardSold", "TTriggerOnDayStarted"], ["Golden", "Shielded", "Obsidian"]),
        ("Friend", "伙伴协同", ["Friend", "Aquatic", "Food"], ["Haste", "Poison", "Charge"], ["TTriggerOnCardPerformedHaste", "TTriggerOnItemUsed"], ["Turbo", "Toxic", "Shiny"]),
        ("Flying", "飞行体系", ["Dragon", "Vehicle", "Relic"], ["Flying", "Haste", "Damage"], ["TTriggerOnCardStartedFlying", "TTriggerOnCardFired"], ["Turbo", "Deadly", "Radiant"]),
        ("Tech", "机械充能", ["Tech", "Tool", "Core"], ["Charge", "Shield", "Burn", "Haste"], ["TTriggerOnItemUsed", "TTriggerOnCardFired"], ["Turbo", "Shielded", "Fiery"]),
        ("Potion", "炼金药剂", ["Potion", "Relic"], ["Poison", "Burn", "Heal", "Freeze"], ["TTriggerOnItemUsed", "TTriggerOnCardFired"], ["Toxic", "Fiery", "Icy"]),
        ("Food", "珍馐烹饪", ["Food", "Tool"], ["Burn", "Heal", "Regen", "Haste"], ["TTriggerOnItemUsed", "TTriggerOnCardPerformedBurn"], ["Fiery", "Restorative", "Turbo"]),
        ("Relic", "遗物共鸣", ["Relic", "Instrument"], ["Slow", "Freeze", "Regen", "Shield"], ["TTriggerOnCardFired", "TTriggerOnItemUsed"], ["Radiant", "Icy", "Shielded"]),
        ("MaxHp", "巨血稳健", ["Apparel", "Property", "Food"], ["Health", "Shield", "Regen"], ["TTriggerOnFightStarted", "TTriggerOnCardFired"], ["Restorative", "Shielded", "Obsidian"]),
        ("Cooldown", "高频冷却", ["Instrument", "Tool", "Weapon"], ["Cooldown", "Charge", "Haste"], ["TTriggerOnItemUsed", "TTriggerOnCardFired"], ["Turbo", "Shiny", "Deadly"]),
    ];

    /// <summary>
    /// 将 32 套 5.0.0 社区十胜蓝本转换为可在 FastRunSimulator 中发育进化的正式 ArchetypeDefinition。
    /// </summary>
    public static IReadOnlyList<ArchetypeDefinition> BuildCommunityArchetypes(
        GameDataRepository repo,
        ArchetypeCatalog baseCatalog)
    {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(baseCatalog);

        var itemsByName = BuildValidPoolLookup(repo.AlwaysSpawnableItems, repo);
        var skillsByName = BuildValidPoolLookup(repo.AlwaysSpawnableSkills, repo);
        var validEnchants = new HashSet<string>(EnchantmentNames.All, StringComparer.Ordinal);
        var result = new List<ArchetypeDefinition>();

        foreach (var spec in CommunityTenWinBlueprints.GetAll())
        {
            var baseArch = baseCatalog.GetById(spec.ArchetypeId)
                ?? baseCatalog.GetByHero(spec.Hero).FirstOrDefault();

            var coreItems = new List<string>();
            var supportItems = new List<string>();
            var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in spec.ItemInternalNames)
            {
                if (!IsValidForHero(itemsByName, name, spec.Hero)) continue;
                if (!seenItems.Add(name)) continue;
                if (coreItems.Count < 3)
                    coreItems.Add(name);
                else
                    supportItems.Add(name);
            }

            if (baseArch != null)
            {
                foreach (var name in baseArch.CoreItemNames)
                {
                    if (coreItems.Count >= 3) break;
                    if (!IsValidForHero(itemsByName, name, spec.Hero) || !seenItems.Add(name)) continue;
                    coreItems.Add(name);
                }
                foreach (var name in baseArch.SupportItemNames)
                {
                    if (supportItems.Count >= 6) break;
                    if (!IsValidForHero(itemsByName, name, spec.Hero) || !seenItems.Add(name)) continue;
                    supportItems.Add(name);
                }
            }

            TopUpValidHeroCards(itemsByName, spec.Hero, supportItems, seenItems, minCount: 4);

            var coreSkills = new List<string>();
            var seenSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sname in spec.SkillInternalNames)
            {
                if (IsValidForHero(skillsByName, sname, spec.Hero) && seenSkills.Add(sname))
                    coreSkills.Add(sname);
            }
            if (baseArch != null)
            {
                foreach (var sname in baseArch.CoreSkillNames)
                {
                    if (coreSkills.Count >= 5) break;
                    if (IsValidForHero(skillsByName, sname, spec.Hero) && seenSkills.Add(sname))
                        coreSkills.Add(sname);
                }
            }
            TopUpValidHeroCards(skillsByName, spec.Hero, coreSkills, seenSkills, minCount: 2);

            if (coreItems.Count < 2 || supportItems.Count < 3 || coreSkills.Count < 2)
                continue;

            var prefEnch = new Dictionary<string, double>(StringComparer.Ordinal);
            if (baseArch != null)
            {
                foreach (var (k, v) in baseArch.PreferredEnchantments)
                {
                    if (validEnchants.Contains(k))
                        prefEnch[k] = v;
                }
            }
            foreach (var ench in spec.EnchantedItems.Values)
            {
                if (validEnchants.Contains(ench))
                    prefEnch[ench] = Math.Max(prefEnch.GetValueOrDefault(ench, 0), 3.2);
            }

            result.Add(new ArchetypeDefinition
            {
                Id = $"Comm10W_{spec.BlueprintId}",
                Hero = spec.Hero,
                NameZh = $"十胜·{spec.TitleZh}",
                DescriptionZh = $"{spec.SourceNote}：{spec.ScalingNote}",
                CoreItemNames = coreItems.ToArray(),
                SupportItemNames = supportItems.ToArray(),
                CoreSkillNames = coreSkills.ToArray(),
                PreferredTags = baseArch?.PreferredTags ?? new Dictionary<string, double>(),
                PreferredHiddenTags = baseArch?.PreferredHiddenTags ?? new Dictionary<string, double>(),
                PreferredTriggers = baseArch?.PreferredTriggers ?? new Dictionary<string, double>(),
                PreferredEnchantments = prefEnch,
                PositioningRules = baseArch?.PositioningRules ?? [PositioningRule.CoreCenter, PositioningRule.AdjacentToCore],
                EarlyFocusTags = baseArch?.EarlyFocusTags ?? Array.Empty<string>(),
                EndgamePowerTag = baseArch?.EndgamePowerTag ?? "CommunityTenWin"
            });
        }

        return result;
    }

    /// <summary>
    /// 基于 GameData.db 卡牌特征与触发语义为全 8 位英雄各挖掘指定数量的互异协同蓝图。
    /// </summary>
    public static IReadOnlyList<ArchetypeDefinition> BuildDataDrivenArchetypes(
        GameDataRepository repo,
        IReadOnlyCollection<ArchetypeDefinition> existingArchetypes,
        int perHeroTarget = 24)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var index = SimulationRepoIndex.GetOrCreate(repo);
        var itemsByName = BuildValidPoolLookup(repo.AlwaysSpawnableItems, repo);
        var skillsByName = BuildValidPoolLookup(repo.AlwaysSpawnableSkills, repo);
        var validEnchants = new HashSet<string>(EnchantmentNames.All, StringComparer.Ordinal);

        var existingCorePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arch in existingArchetypes)
        {
            existingIds.Add(arch.Id);
            for (int i = 0; i < arch.CoreItemNames.Count; i++)
            {
                for (int j = i + 1; j < arch.CoreItemNames.Count; j++)
                {
                    existingCorePairs.Add(MakePairKey(arch.Hero, arch.CoreItemNames[i], arch.CoreItemNames[j]));
                }
            }
        }

        var result = new List<ArchetypeDefinition>(HeroNames.PlayableHeroes.Length * perHeroTarget);

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var heroItems = repo.AlwaysSpawnableItems
                .Where(c => c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                    && (repo.Localization == null || repo.HasOfficialZhTitle(c))
                    && !ArchetypeBot.IsNonCombatBoardDeadWeight(c, index))
                .GroupBy(c => c.InternalName, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            var heroAndCommonItems = repo.AlwaysSpawnableItems
                .Where(c => (c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                             || c.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
                    && (repo.Localization == null || repo.HasOfficialZhTitle(c))
                    && !ArchetypeBot.IsNonCombatBoardDeadWeight(c, index))
                .GroupBy(c => c.InternalName, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            var heroAndCommonSkills = repo.AlwaysSpawnableSkills
                .Where(s => (s.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                             || s.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase))
                    && (repo.Localization == null || repo.HasOfficialZhTitle(s)))
                .GroupBy(s => s.InternalName, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            if (heroItems.Count < 4 || heroAndCommonSkills.Count < 2)
                continue;

            int builtForHero = 0;

            foreach (var theme in ThemeSpecs)
            {
                if (builtForHero >= perHeroTarget) break;

                var themedAnchors = heroItems
                    .Select(c => (Card: c, Meta: index.GetMeta(c), Score: ScoreThemeMatch(c, index.GetMeta(c), theme)))
                    .Where(x => x.Score > 1.2)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => (int)x.Card.CardSize)
                    .ThenByDescending(x => x.Meta.EstimateCombatPower(CardTier.Gold))
                    .Take(10)
                    .ToList();

                if (themedAnchors.Count < 2) continue;

                int perThemeAdded = 0;
                for (int i = 0; i < themedAnchors.Count && builtForHero < perHeroTarget && perThemeAdded < 2; i++)
                {
                    for (int j = i + 1; j < themedAnchors.Count && builtForHero < perHeroTarget && perThemeAdded < 2; j++)
                    {
                        var a = themedAnchors[i];
                        var b = themedAnchors[j];
                        string pairKey = MakePairKey(hero, a.Card.InternalName, b.Card.InternalName);
                        if (!existingCorePairs.Add(pairKey))
                            continue;

                        var arch = BuildSynergyDefinition(
                            hero,
                            ++builtForHero,
                            theme,
                            a.Card,
                            b.Card,
                            heroAndCommonItems,
                            heroAndCommonSkills,
                            index,
                            itemsByName,
                            skillsByName,
                            validEnchants,
                            existingIds);

                        if (arch != null)
                        {
                            result.Add(arch);
                            perThemeAdded++;
                        }
                    }
                }
            }

            if (builtForHero < perHeroTarget)
            {
                var topGeneral = heroItems
                    .OrderByDescending(c => (int)c.CardSize * 15 + index.GetMeta(c).EstimateCombatPower(CardTier.Gold))
                    .Take(16)
                    .ToList();

                for (int i = 0; i < topGeneral.Count && builtForHero < perHeroTarget; i++)
                {
                    for (int j = i + 1; j < topGeneral.Count && builtForHero < perHeroTarget; j++)
                    {
                        var c1 = topGeneral[i];
                        var c2 = topGeneral[j];
                        string pairKey = MakePairKey(hero, c1.InternalName, c2.InternalName);
                        if (!existingCorePairs.Add(pairKey))
                            continue;

                        var inferredTheme = PickBestThemeForPair(c1, c2, index);
                        var arch = BuildSynergyDefinition(
                            hero,
                            ++builtForHero,
                            inferredTheme,
                            c1,
                            c2,
                            heroAndCommonItems,
                            heroAndCommonSkills,
                            index,
                            itemsByName,
                            skillsByName,
                            validEnchants,
                            existingIds);

                        if (arch != null)
                            result.Add(arch);
                    }
                }
            }
        }

        return result;
    }

    private static ArchetypeDefinition? BuildSynergyDefinition(
        string hero,
        int serial,
        (string ThemeTag, string ThemeZh, string[] PreferredTags, string[] PreferredHidden, string[] PreferredTriggers, string[] PreferredEnchs) theme,
        CardTemplate core1,
        CardTemplate core2,
        List<CardTemplate> poolItems,
        List<CardTemplate> poolSkills,
        SimulationRepoIndex index,
        Dictionary<string, List<CardTemplate>> itemsByName,
        Dictionary<string, List<CardTemplate>> skillsByName,
        HashSet<string> validEnchants,
        HashSet<string> existingIds)
    {
        var coreItems = new List<string> { core1.InternalName, core2.InternalName };
        var usedItems = new HashSet<string>(coreItems, StringComparer.OrdinalIgnoreCase);

        var rankedPool = poolItems
            .Where(c => !usedItems.Contains(c.InternalName) && IsValidForHero(itemsByName, c.InternalName, hero))
            .Select(c =>
            {
                double s = ScoreThemeMatch(c, index.GetMeta(c), theme);
                if (c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)) s += 2.0;
                return (Card: c, Score: s);
            })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => index.GetMeta(x.Card).EstimateCombatPower(CardTier.Gold))
            .ToList();

        var thirdCore = rankedPool.FirstOrDefault(x => x.Card.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)).Card;
        if (thirdCore != null && usedItems.Add(thirdCore.InternalName))
            coreItems.Add(thirdCore.InternalName);

        var supportItems = new List<string>(6);
        foreach (var entry in rankedPool)
        {
            if (supportItems.Count >= 6) break;
            if (usedItems.Add(entry.Card.InternalName))
                supportItems.Add(entry.Card.InternalName);
        }

        TopUpValidHeroCards(itemsByName, hero, supportItems, usedItems, minCount: 4);

        var rankedSkills = poolSkills
            .Where(s => IsValidForHero(skillsByName, s.InternalName, hero))
            .Select(s =>
            {
                double sc = ScoreThemeMatch(s, index.GetMeta(s), theme);
                if (s.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)) sc += 1.6;
                return (Skill: s, Score: sc);
            })
            .OrderByDescending(x => x.Score)
            .ToList();

        var coreSkills = new List<string>(4);
        var usedSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sk in rankedSkills)
        {
            if (coreSkills.Count >= 4) break;
            if (usedSkills.Add(sk.Skill.InternalName))
                coreSkills.Add(sk.Skill.InternalName);
        }
        TopUpValidHeroCards(skillsByName, hero, coreSkills, usedSkills, minCount: 2);

        if (coreItems.Count < 2 || supportItems.Count < 3 || coreSkills.Count < 2)
            return null;

        string id = $"{hero}_Auto_{serial:00}_{theme.ThemeTag}";
        while (!existingIds.Add(id))
            id += "_X";

        var prefTags = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var t in theme.PreferredTags)
            prefTags[t] = 2.0;
        foreach (var t in core1.Tags.Concat(core2.Tags))
        {
            if (!string.IsNullOrWhiteSpace(t))
                prefTags[t] = Math.Max(prefTags.GetValueOrDefault(t, 0), 2.3);
        }

        var prefHidden = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var ht in theme.PreferredHidden)
            prefHidden[ht] = 2.4;
        foreach (var ht in core1.HiddenTags.Concat(core2.HiddenTags))
        {
            if (!string.IsNullOrWhiteSpace(ht))
                prefHidden[ht] = Math.Max(prefHidden.GetValueOrDefault(ht, 0), 2.1);
        }

        var prefTriggers = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var tr in theme.PreferredTriggers)
            prefTriggers[tr] = 2.0;

        var prefEnch = new Dictionary<string, double>(StringComparer.Ordinal);
        double w = 2.8;
        foreach (var ench in theme.PreferredEnchs)
        {
            if (validEnchants.Contains(ench))
            {
                prefEnch[ench] = w;
                w = Math.Max(1.6, w - 0.4);
            }
        }

        var m1 = index.GetMeta(core1);
        var m2 = index.GetMeta(core2);
        var posRules = new List<PositioningRule> { PositioningRule.CoreCenter };
        if (m1.HasNeighborEffect || m2.HasNeighborEffect)
            posRules.Add(PositioningRule.AdjacentToCore);
        if (m1.PrefersLeftMost || m2.PrefersLeftMost)
            posRules.Add(PositioningRule.LeftMost);
        if (m1.PrefersRightMost || m2.PrefersRightMost)
            posRules.Add(PositioningRule.RightMost);
        if (m1.IsScaler || m2.IsScaler)
            posRules.Add(PositioningRule.FastTriggerLeftOfScaler);
        if (posRules.Count == 1)
            posRules.Add(PositioningRule.AdjacentToCore);

        return new ArchetypeDefinition
        {
            Id = id,
            Hero = hero,
            NameZh = $"{hero}·{theme.ThemeZh}({serial})",
            DescriptionZh = $"数据驱动自动协同流派：围绕 {core1.InternalName} 与 {core2.InternalName} 构建的 {theme.ThemeZh} 构筑。",
            CoreItemNames = coreItems.ToArray(),
            SupportItemNames = supportItems.ToArray(),
            CoreSkillNames = coreSkills.ToArray(),
            PreferredTags = prefTags,
            PreferredHiddenTags = prefHidden,
            PreferredTriggers = prefTriggers,
            PreferredEnchantments = prefEnch,
            PositioningRules = posRules.Distinct().ToArray(),
            EarlyFocusTags = theme.PreferredHidden.Take(3).ToArray(),
            EndgamePowerTag = $"Auto_{theme.ThemeTag}"
        };
    }

    private static (string ThemeTag, string ThemeZh, string[] PreferredTags, string[] PreferredHidden, string[] PreferredTriggers, string[] PreferredEnchs)
        PickBestThemeForPair(CardTemplate c1, CardTemplate c2, SimulationRepoIndex index)
    {
        var m1 = index.GetMeta(c1);
        var m2 = index.GetMeta(c2);
        var best = ThemeSpecs[0];
        double bestScore = -1.0;

        foreach (var theme in ThemeSpecs)
        {
            double s = ScoreThemeMatch(c1, m1, theme) + ScoreThemeMatch(c2, m2, theme);
            if (s > bestScore)
            {
                bestScore = s;
                best = theme;
            }
        }
        return best;
    }

    private static double ScoreThemeMatch(
        CardTemplate tpl,
        CardMeta meta,
        (string ThemeTag, string ThemeZh, string[] PreferredTags, string[] PreferredHidden, string[] PreferredTriggers, string[] PreferredEnchs) theme)
    {
        double score = 0.0;
        foreach (var t in theme.PreferredTags)
        {
            if (tpl.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                score += 1.4;
        }
        foreach (var ht in theme.PreferredHidden)
        {
            if (meta.HasAnyTag(ht))
                score += 2.2;
        }
        foreach (var tr in theme.PreferredTriggers)
        {
            if (meta.TriggerTypes.Contains(tr, StringComparer.Ordinal))
                score += 1.8;
        }
        return score;
    }

    private static Dictionary<string, List<CardTemplate>> BuildValidPoolLookup(
        IEnumerable<CardTemplate> pool,
        GameDataRepository repo)
    {
        var dict = new Dictionary<string, List<CardTemplate>>(StringComparer.Ordinal);
        foreach (var tpl in pool)
        {
            if (string.IsNullOrEmpty(tpl.InternalName)) continue;
            if (repo.Localization != null && !repo.HasOfficialZhTitle(tpl)) continue;
            if (!dict.TryGetValue(tpl.InternalName, out var list))
            {
                list = new List<CardTemplate>();
                dict[tpl.InternalName] = list;
            }
            list.Add(tpl);
        }
        return dict;
    }

    private static bool IsValidForHero(Dictionary<string, List<CardTemplate>> lookup, string internalName, string hero)
    {
        if (!lookup.TryGetValue(internalName, out var templates))
            return false;

        return templates.Any(t =>
            t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) ||
            t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase));
    }

    private static void TopUpValidHeroCards(
        Dictionary<string, List<CardTemplate>> lookup,
        string hero,
        List<string> target,
        HashSet<string> used,
        int minCount)
    {
        if (target.Count >= minCount) return;

        foreach (var (name, list) in lookup)
        {
            if (target.Count >= minCount) break;
            if (used.Contains(name)) continue;
            if (list.Any(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains(HeroNames.Common, StringComparer.OrdinalIgnoreCase)))
            {
                used.Add(name);
                target.Add(name);
            }
        }
    }

    private static string MakePairKey(string hero, string a, string b)
    {
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0
            ? $"{hero}|{a}|{b}"
            : $"{hero}|{b}|{a}";
    }
}

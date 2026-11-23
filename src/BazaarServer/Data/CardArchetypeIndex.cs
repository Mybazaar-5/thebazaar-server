using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarServer.Data;

/// <summary>
/// 18 维流派位掩码索引与 10 类遭遇权重分类索引：
/// 负责基于静态标签（Tags/HiddenTags）、属性（Multicast）与动态效果节点（Abilities/Auras/Quests）
/// 构建全量卡牌流派索引、分英雄合法卡牌计数、遭遇分类及前端大厅目录 DTO。
/// </summary>
public sealed class CardArchetypeIndex
{
    public const string NufuEncounterId = "210003fa-a20d-4ad1-9ada-d5fad5bf9f89";
    public const string TatianaEncounterId = "22bb0004-5131-4588-a567-03ee3c96003b";
    public const string ZurphinsSafariEncounterId = "b81a1c81-0a16-462a-8247-04653354b79d";
    public const string ShroudedFigureEncounterId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    public const string TheCultEncounterId = "bf1594cc-7f65-4236-b95f-ed2f521739de";
    public const string MysteriousPortalEncounterId = "b7acf32d-79f8-4040-810f-4fb44e16682b";
    public const string StatiaEncounterId = "7f2c3a0d-7675-44dd-8667-77b08455eec8";
    public const string AdvancedTrainingEncounterId = "6a5e82cf-969c-432a-863a-1df6e7f56513";
    public const string TheArtistPedestalId = "49717d0f-71de-4291-8af6-d9c41ad3f438";
    public const string TheArtistLevelUpPedestalId = "9b18ad79-901b-46ac-bec1-f447d41ad5ad";
    public const string FuturaEncounterId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";

    /// <summary>需要保证始终出现在英雄统计表中的 9 个标准分类（8 主流英雄 + Common）。</summary>
    public static readonly IReadOnlyList<string> StandardHeroCountKeys =
    [
        HeroNames.Vanessa,
        HeroNames.Pygmalien,
        HeroNames.Dooley,
        HeroNames.Mak,
        HeroNames.Jules,
        HeroNames.Stelle,
        HeroNames.Karnok,
        HeroNames.Hero8,
        HeroNames.Common
    ];

    private readonly Dictionary<string, ulong> _cardMasks;
    private readonly Dictionary<string, EEncounterWeightCategory> _encounterCategories;
    private readonly Dictionary<string, IReadOnlyDictionary<string, int>> _heroCounts;

    private CardArchetypeIndex(
        Dictionary<string, ulong> cardMasks,
        Dictionary<string, EEncounterWeightCategory> encounterCategories,
        Dictionary<string, IReadOnlyDictionary<string, int>> heroCounts)
    {
        _cardMasks = cardMasks;
        _encounterCategories = encounterCategories;
        _heroCounts = heroCounts;
    }

    /// <summary>所有已索引卡牌模板 ID 到 18-bit 流派掩码的映射。</summary>
    public IReadOnlyDictionary<string, ulong> CardMasks => _cardMasks;

    /// <summary>所有已索引遭遇模板 ID 到 10 类权重分类的映射。</summary>
    public IReadOnlyDictionary<string, EEncounterWeightCategory> EncounterCategories => _encounterCategories;

    /// <summary>获取各英雄（含 Common）在 18 维流派上的合法可用卡牌数统计。</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> GetHeroCounts() => _heroCounts;

    /// <summary>查询指定模板 ID 对应的 18-bit 流派位掩码（未命中则返回 0）。</summary>
    public ulong GetArchetypeMask(string? templateId)
        => !string.IsNullOrWhiteSpace(templateId) && _cardMasks.TryGetValue(templateId, out var mask) ? mask : 0UL;

    /// <summary>查询指定遭遇模板 ID 对应的遭遇权重分类。</summary>
    public bool TryGetEncounterCategory(string? encounterId, out EEncounterWeightCategory category)
    {
        if (!string.IsNullOrWhiteSpace(encounterId) && _encounterCategories.TryGetValue(encounterId, out category))
            return true;
        category = default;
        return false;
    }

    /// <summary>
    /// 基于 <see cref="CardPool"/> 与可选 <see cref="GameDataRepository"/> 构建流派与遭遇分类索引。
    /// </summary>
    public static CardArchetypeIndex Build(CardPool pool, GameDataRepository? repo = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var cardMasks = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var encounterCategories = new Dictionary<string, EEncounterWeightCategory>(StringComparer.OrdinalIgnoreCase);

        // 初始化英雄计数表：保证 8 位标准英雄 + Common 必定存在
        var heroCountsMutable = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (string hero in StandardHeroCountKeys)
            heroCountsMutable[hero] = CreateEmptyArchetypeCountMap();

        // 1. 索引所有有效 Item 与 Skill
        foreach (string cardType in new[] { "Item", "Skill" })
        {
            foreach (var poolTpl in pool.OfType(cardType))
            {
                if (string.IsNullOrWhiteSpace(poolTpl.Id)) continue;
                if (string.Equals(poolTpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase)) continue;

                var coreTpl = repo?.GetCard(poolTpl.Id);
                ulong mask = ComputeArchetypeMask(poolTpl, coreTpl);
                cardMasks[poolTpl.Id] = mask;

                if (!IsLegalCatalogCard(pool, repo, poolTpl))
                    continue;

                var cardHeroes = ResolveEffectiveHeroList(poolTpl, coreTpl);
                foreach (string h in cardHeroes)
                {
                    if (!heroCountsMutable.TryGetValue(h, out var map))
                    {
                        map = CreateEmptyArchetypeCountMap();
                        heroCountsMutable[h] = map;
                    }

                    foreach (var meta in CardArchetypeCatalog.All)
                    {
                        if ((mask & (ulong)meta.Flag) != 0UL)
                            map[meta.Key]++;
                    }
                }
            }
        }

        // 补充仅存在于 repo 中但不在 pool 里的模板（例如测试用纯内存合成卡）
        if (repo != null)
        {
            foreach (var coreTpl in repo.CardsById.Values)
            {
                if (coreTpl.Type is not ("Item" or "Skill")) continue;
                if (cardMasks.ContainsKey(coreTpl.Id)) continue;
                cardMasks[coreTpl.Id] = ComputeArchetypeMask(null, coreTpl);
            }
        }

        // 2. 索引所有有效遭遇（PedestalEncounter / EventEncounter / CombatEncounter）
        foreach (string encType in new[] { "PedestalEncounter", "EventEncounter", "CombatEncounter" })
        {
            foreach (var encTpl in pool.OfType(encType))
            {
                if (!pool.IsPlayerSpawnable(encTpl, allowGuidOnly: true)) continue;
                if (IsDebugOrDefunctEncounter(encTpl.InternalName)) continue;

                var coreTpl = repo?.GetCard(encTpl.Id);
                encounterCategories[encTpl.Id] = ClassifyEncounter(encTpl, coreTpl);
            }
        }

        if (repo != null)
        {
            foreach (var coreTpl in repo.CardsById.Values)
            {
                if (coreTpl.Type is not ("PedestalEncounter" or "EventEncounter" or "CombatEncounter")) continue;
                if (encounterCategories.ContainsKey(coreTpl.Id)) continue;
                encounterCategories[coreTpl.Id] = ClassifyEncounter(null, coreTpl);
            }
        }

        var readonlyCounts = new Dictionary<string, IReadOnlyDictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hero, counts) in heroCountsMutable)
            readonlyCounts[hero] = counts;

        return new CardArchetypeIndex(cardMasks, encounterCategories, readonlyCounts);
    }

    /// <summary>
    /// 静态/便捷入口：根据卡池与数据仓库直接构建完整前端目录 DTO。
    /// </summary>
    public static CardTaxonomyCatalogDto BuildCatalogDto(CardPool pool, GameDataRepository? repo = null)
        => Build(pool, repo).ToCatalogDto(pool, repo);

    /// <summary>
    /// 根据已构建索引导出符合大厅契约的 JSON 目录 DTO：
    /// `{ archetypes, encounterCategories, items, skills }`。
    /// </summary>
    public CardTaxonomyCatalogDto ToCatalogDto(CardPool pool, GameDataRepository? repo = null)
    {
        ArgumentNullException.ThrowIfNull(pool);

        var archetypeDtos = new List<ArchetypeCatalogEntryDto>(CardArchetypeCatalog.All.Count);
        foreach (var meta in CardArchetypeCatalog.All)
        {
            var perHero = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string hero in StandardHeroCountKeys)
            {
                int count = _heroCounts.TryGetValue(hero, out var map) && map.TryGetValue(meta.Key, out int c) ? c : 0;
                perHero[hero] = count;
            }
            foreach (var (hero, map) in _heroCounts)
            {
                if (!perHero.ContainsKey(hero) && map.TryGetValue(meta.Key, out int c))
                    perHero[hero] = c;
            }

            archetypeDtos.Add(new ArchetypeCatalogEntryDto(
                meta.Bit,
                meta.Key,
                meta.NameZh,
                meta.DescZh,
                perHero));
        }

        var encounterCategoryDtos = EncounterWeightCategoryCatalog.All
            .Select(c => new EncounterCategoryDto(c.Key, c.GroupZh, c.NameZh, c.DefaultMult))
            .ToList();

        var itemDtos = new List<CatalogCardDto>();
        foreach (var tpl in pool.OfType("Item"))
        {
            if (!IsLegalCatalogCard(pool, repo, tpl)) continue;
            var coreTpl = repo?.GetCard(tpl.Id);
            ulong mask = _cardMasks.TryGetValue(tpl.Id, out var m) ? m : ComputeArchetypeMask(tpl, coreTpl);
            itemDtos.Add(new CatalogCardDto(
                tpl.Id,
                ResolveNameZh(tpl, coreTpl, repo),
                FormatHeroField(ResolveEffectiveHeroList(tpl, coreTpl)),
                string.IsNullOrWhiteSpace(tpl.Size) ? "Medium" : tpl.Size,
                string.IsNullOrWhiteSpace(tpl.StartingTier) ? "Bronze" : tpl.StartingTier,
                mask));
        }

        var skillDtos = new List<CatalogSkillDto>();
        foreach (var tpl in pool.OfType("Skill"))
        {
            if (!IsLegalCatalogCard(pool, repo, tpl)) continue;
            var coreTpl = repo?.GetCard(tpl.Id);
            ulong mask = _cardMasks.TryGetValue(tpl.Id, out var m) ? m : ComputeArchetypeMask(tpl, coreTpl);
            skillDtos.Add(new CatalogSkillDto(
                tpl.Id,
                ResolveNameZh(tpl, coreTpl, repo),
                FormatHeroField(ResolveEffectiveHeroList(tpl, coreTpl)),
                string.IsNullOrWhiteSpace(tpl.StartingTier) ? "Bronze" : tpl.StartingTier,
                mask));
        }

        return new CardTaxonomyCatalogDto(archetypeDtos, encounterCategoryDtos, itemDtos, skillDtos);
    }

    /// <summary>
    /// 综合卡牌的 `Tags`、`HiddenTags`、基础属性（如 `Multicast > 1`）与 `GameDataRepository` 动态效果树，
    /// 计算该卡牌所属的 64-bit <see cref="ECardArchetype"/> 多标签组合掩码。
    /// </summary>
    public static ulong ComputeArchetypeMask(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl = null)
    {
        ECardArchetype mask = ECardArchetype.None;

        if (poolTpl != null)
        {
            foreach (string t in poolTpl.Tags) mask |= MapTagToArchetype(t);
            foreach (string ht in poolTpl.HiddenTags) mask |= MapTagToArchetype(ht);
            foreach (var tierAttrs in poolTpl.TierAttributes.Values)
                mask |= MapAttributeMapToArchetype(tierAttrs);
        }

        if (coreTpl != null)
        {
            foreach (string t in coreTpl.Tags) mask |= MapTagToArchetype(t);
            foreach (string ht in coreTpl.HiddenTags) mask |= MapTagToArchetype(ht);

            if (coreTpl.Attributes != null)
                mask |= MapDoubleAttributeMapToArchetype(coreTpl.Attributes);

            if (coreTpl.Tiers != null)
            {
                foreach (var tierData in coreTpl.Tiers.Values)
                {
                    if (tierData.Attributes != null)
                        mask |= MapDoubleAttributeMapToArchetype(tierData.Attributes);
                }
            }

            foreach (var ab in coreTpl.Abilities.Values)
                mask |= ExtractMaskFromRuleNode(ab.Action);

            foreach (var aura in coreTpl.Auras.Values)
                mask |= ExtractMaskFromRuleNode(aura.Action);

            if (coreTpl.Quests != null)
            {
                foreach (var q in coreTpl.Quests)
                {
                    foreach (var entry in q.Entries)
                    {
                        if (entry.Reward?.Abilities != null)
                        {
                            foreach (var ab in entry.Reward.Abilities.Values)
                                mask |= ExtractMaskFromRuleNode(ab.Action);
                        }
                        if (entry.Reward?.Auras != null)
                        {
                            foreach (var aura in entry.Reward.Auras.Values)
                                mask |= ExtractMaskFromRuleNode(aura.Action);
                        }
                    }
                }
            }
        }

        return (ulong)mask;
    }

    /// <summary>
    /// 将单个遭遇模板准确分类至 10 类 <see cref="EEncounterWeightCategory"/> 之一。
    /// </summary>
    public static EEncounterWeightCategory ClassifyEncounter(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl = null)
    {
        string id = poolTpl?.Id ?? coreTpl?.Id ?? string.Empty;
        string type = poolTpl?.Type ?? coreTpl?.Type ?? string.Empty;
        string name = poolTpl?.InternalName ?? coreTpl?.InternalName ?? string.Empty;

        if (string.Equals(type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
            return EEncounterWeightCategory.Monsters;

        if (string.Equals(type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
            return ClassifyPedestalEncounter(id, name, coreTpl?.Behavior ?? poolTpl?.Behavior);

        if (IsMerchantEncounterTemplate(id, name, poolTpl, coreTpl))
            return IsRareOrSkillMerchant(id, name, poolTpl, coreTpl)
                ? EEncounterWeightCategory.MerchantSkillsRare
                : EEncounterWeightCategory.MerchantItems;

        return ClassifyNonMerchantEvent(id, name, poolTpl, coreTpl);
    }

    private static EEncounterWeightCategory ClassifyPedestalEncounter(
        string id,
        string name,
        RuleNode? behavior)
    {
        string behType = behavior?.Type ?? string.Empty;
        if (string.Equals(behType, "TPedestalBehaviorEnchantRandom", StringComparison.OrdinalIgnoreCase))
            return EEncounterWeightCategory.PedestalEnchantRandom;
        if (string.Equals(behType, "TPedestalBehaviorEnchant", StringComparison.OrdinalIgnoreCase))
            return EEncounterWeightCategory.PedestalEnchant;
        if (string.Equals(behType, "TPedestalBehaviorUpgrade", StringComparison.OrdinalIgnoreCase))
            return EEncounterWeightCategory.PedestalUpgrade;

        if (id.Equals(TheArtistPedestalId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(TheArtistLevelUpPedestalId, StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Artist", StringComparison.OrdinalIgnoreCase) ||
            (name.Contains("Enchant", StringComparison.OrdinalIgnoreCase) && name.Contains("Random", StringComparison.OrdinalIgnoreCase)))
        {
            return EEncounterWeightCategory.PedestalEnchantRandom;
        }

        if (name.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Forja", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Sterling", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("B1&B2", StringComparison.OrdinalIgnoreCase))
        {
            return EEncounterWeightCategory.PedestalUpgrade;
        }

        return EEncounterWeightCategory.PedestalEnchant;
    }

    private static EEncounterWeightCategory ClassifyNonMerchantEvent(
        string id,
        string name,
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl)
    {
        // 1. Expedition / Travel Agent
        if (id.Equals(CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase) ||
            CardPool.IsExpeditionRoomTemplate(id) ||
            ContainsAny(name, "Travel Agent", "Expedition", "Cargo Hold", "Command Nexus",
                "Crystal Growth Chamber", "Temple Reliquary", "Temple Vault", "Ethergy Nexus", "Where am I"))
        {
            return EEncounterWeightCategory.EventExpeditionAgent;
        }

        // 2. Cult & Mystery
        if (id.Equals(ShroudedFigureEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(TheCultEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(MysteriousPortalEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(FuturaEncounterId, StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(name, "Shrouded Figure", "Cult", "Mystery", "Mysterious", "Futura",
                "Crossroads", "Tiny Furry Creature", "Strange Mushroom", "Tomb of the Ancients",
                "Partner Portal", "Monster Ranch", "Thieves Guild", "Burning Library", "BazaarCON"))
        {
            return EEncounterWeightCategory.EventCultAndMystery;
        }

        // 3. Free Deal / Transform
        if (ContainsAny(name, "Salvage", "Dig Site", "The Docks", "Form", "Mandala", "Lost Crate",
                "Haddy", "Wink", "Photo Booth", "Street Festival", "Moonlit Meadow", "Gunpowder Cache",
                "Farai", "Workshop", "Utility Box", "Armory", "Recycling Center", "Lost and Found",
                "Treasure Chest", "Excavation", "Sharpening Kit", "Abandoned Property",
                "Truffle Farming", "Reagent Harvesting", "Bountiful Harvest", "Cupboard",
                "Fishing", "Botanical Gardens", "Alchemy Lab", "Furnace", "Pyre", "Freezer",
                "Mag Storage", "Extract Extract", "Procure Medkit", "Block Party", "House Party",
                "Equipment Van", "Piano", "Sprout", "Reflecting Pool", "Star Chart",
                "Hidden Lake", "Borrow", "Grand Opening", "Transform", "Free Deal"))
        {
            return EEncounterWeightCategory.EventFreeDealTransform;
        }

        // 4. Economy / Stats
        if (ContainsAny(name, "Economic Seminar", "Ledger", "Jungle Ruins", "Wishing Fountain",
                "Gumball Machine", "Spare Change", "Invest", "Cache of Riches", "Dabora",
                "Finn's Big Bite", "Eating Contest", "Apothecary", "Library", "Jules' Cafe",
                "Bex", "Likit", "Aldric", "Artisan Dunes", "Botul", "D'flek", "Flambe",
                "Mad Maddie", "Aurora Dome", "Robotics Factory", "Concert Hall", "Frog Hollow",
                "Farmer's Market", "Launch Tower", "Snack Time", "Candy Stash", "Hospital",
                "Medicine Cabinet", "Rest", "Training", "Obstacle Course", "Racetrack",
                "Guard Locker", "Security Center", "Trophy Hunt", "Deadly Duel", "Epic Battle",
                "Battlefield", "Grandmaster", "Study", "Mountain Pass", "Aerodrome"))
        {
            return EEncounterWeightCategory.EventEconomyStats;
        }

        var rules = coreTpl?.SelectionContext?.Rules ?? poolTpl?.SelectionContext?.Rules;
        return rules is { SelectionIsFree: true }
            ? EEncounterWeightCategory.EventFreeDealTransform
            : EEncounterWeightCategory.EventEconomyStats;
    }

    public static bool IsMerchantEncounterTemplate(
        string id,
        string name,
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl)
    {
        if (id.Equals(NufuEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(ZurphinsSafariEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(StatiaEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(AdvancedTrainingEncounterId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (poolTpl != null && poolTpl.Tags.Any(t => t.Equals("Merchant", StringComparison.OrdinalIgnoreCase)))
            return true;
        if (coreTpl != null && coreTpl.Tags.Any(t => t.Equals("Merchant", StringComparison.OrdinalIgnoreCase)))
            return true;

        var rules = coreTpl?.SelectionContext?.Rules ?? poolTpl?.SelectionContext?.Rules;
        var spawn = coreTpl?.SelectionContext?.SpawnContext ?? poolTpl?.SelectionContext?.SpawnContext;

        // 显式免费事件（如右槽免费物品遭遇 Workshop）且不带 Merchant 标签时，绝不因名称含 "Shop" 子串而误判为商人
        if (rules is { SelectionIsFree: true })
            return false;

        if (ContainsAny(name, "Merchant", "Shop", "Vendor"))
            return true;

        if (rules is { SelectionIsFree: false, RerollRules: not null } && spawn?.Groups is { Count: > 0 })
        {
            return spawn.Groups.Any(g => (g.Filters ?? []).Any(f =>
                f.Type == "TSpawnFilterQuery" &&
                f.RawConstraintsElement.HasValue &&
                (f.RawConstraintsElement.Value.ToString().Contains("\"Item\"", StringComparison.OrdinalIgnoreCase) ||
                 f.RawConstraintsElement.Value.ToString().Contains("\"Skill\"", StringComparison.OrdinalIgnoreCase))));
        }

        return false;
    }

    private static bool IsRareOrSkillMerchant(
        string id,
        string name,
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl)
    {
        if (id.Equals(NufuEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(TatianaEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(ZurphinsSafariEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(StatiaEncounterId, StringComparison.OrdinalIgnoreCase) ||
            id.Equals(AdvancedTrainingEncounterId, StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(name, "Nufu", "Tatiana", "Zurphin", "Statia", "Advanced Training", "Hero Merchant", "Skill"))
        {
            return true;
        }

        string tier = poolTpl?.StartingTier ?? coreTpl?.StartingTier ?? "Bronze";
        if (string.Equals(tier, "Diamond", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tier, "Legendary", StringComparison.OrdinalIgnoreCase))
            return true;

        var spawn = coreTpl?.SelectionContext?.SpawnContext ?? poolTpl?.SelectionContext?.SpawnContext;
        if (spawn?.Groups != null)
        {
            foreach (var grp in spawn.Groups)
            {
                if (grp.Filters == null) continue;
                foreach (var f in grp.Filters)
                {
                    if (f.RawConstraintsElement.HasValue &&
                        f.RawConstraintsElement.Value.ToString().Contains("\"Skill\"", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static ECardArchetype MapTagToArchetype(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
            return ECardArchetype.None;

        string tag = rawTag.Trim();

        if (tag.StartsWith("Burn", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Heated", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Burn;

        if (tag.StartsWith("Poison", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Poison;

        if (tag.StartsWith("Shield", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Shield;

        if (tag.StartsWith("Heal", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Regen", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Health", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Lifesteal", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Sustain;

        if (tag.StartsWith("Haste", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Cooldown", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Tempo", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Charge", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Speed;

        if (tag.StartsWith("Freeze", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Slow", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("ChilledReference", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("AbsorbFreeze", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("AbsorbSlow", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Control;

        if (tag.StartsWith("Crit", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Crit;

        if (tag.StartsWith("Damage", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Rage", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Damage;

        if (tag.StartsWith("Ammo", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Ammo;

        if (tag.StartsWith("Flying", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Flying;

        if (tag.Equals("EconomyReference", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Value", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Gold", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Income", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Quest", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Economy;

        if (tag.Equals("Weapon", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Weapon;

        if (tag.Equals("Tool", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Tool;

        if (tag.Equals("Tech", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("TechReference", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Drone", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Ray", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Core", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Tech;

        if (tag.Equals("Friend", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Toy", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Dragon", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Dinosaur", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Companion;

        if (tag.Equals("Aquatic", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Vehicle", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.AquaticVehicle;

        if (tag.Equals("Property", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Relic", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Apparel", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.PropertyRelic;

        if (tag.Equals("Food", StringComparison.OrdinalIgnoreCase) ||
            tag.StartsWith("Potion", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Reagent", StringComparison.OrdinalIgnoreCase) ||
            tag.Equals("Instrument", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.FoodAlchemy;

        return ECardArchetype.None;
    }

    private static ECardArchetype MapAttributeMapToArchetype(IReadOnlyDictionary<string, int> attrs)
    {
        ECardArchetype mask = ECardArchetype.None;
        foreach (var (key, value) in attrs)
        {
            if (value <= 0) continue;
            mask |= MapAttributeEntryToArchetype(key, value);
        }
        return mask;
    }

    private static ECardArchetype MapDoubleAttributeMapToArchetype(IReadOnlyDictionary<string, double> attrs)
    {
        ECardArchetype mask = ECardArchetype.None;
        foreach (var (key, value) in attrs)
        {
            if (value <= 0) continue;
            mask |= MapAttributeEntryToArchetype(key, value);
        }
        return mask;
    }

    private static ECardArchetype MapAttributeEntryToArchetype(string attrName, double value)
    {
        if (attrName.Equals("Multicast", StringComparison.OrdinalIgnoreCase))
            return value > 1.0 ? ECardArchetype.Ammo : ECardArchetype.None;
        if (attrName.Equals("AmmoMax", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Ammo;
        if (attrName.Equals("BurnApplyAmount", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Burn;
        if (attrName.Equals("PoisonApplyAmount", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Poison;
        if (attrName.Equals("ShieldApplyAmount", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Shield;
        if (attrName.Equals("HealAmount", StringComparison.OrdinalIgnoreCase) ||
            attrName.Equals("RegenApplyAmount", StringComparison.OrdinalIgnoreCase) ||
            attrName.Equals("Lifesteal", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Sustain;
        if (attrName.Equals("HasteAmount", StringComparison.OrdinalIgnoreCase) ||
            attrName.Equals("PercentCooldownReduction", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Speed;
        if (attrName.Equals("FreezeAmount", StringComparison.OrdinalIgnoreCase) ||
            attrName.Equals("SlowAmount", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Control;
        if (attrName.Equals("CritChance", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Crit;
        if (attrName.Equals("DamageAmount", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Damage;
        if (attrName.Equals("Flying", StringComparison.OrdinalIgnoreCase))
            return ECardArchetype.Flying;
        return ECardArchetype.None;
    }

    private static ECardArchetype ExtractMaskFromRuleNode(RuleNode? node, int depth = 0)
    {
        if (node == null || depth > 12) return ECardArchetype.None;

        ECardArchetype mask = node.Type switch
        {
            "TActionPlayerBurnApply" => ECardArchetype.Burn,
            "TActionPlayerPoisonApply" => ECardArchetype.Poison,
            "TActionPlayerShieldApply" => ECardArchetype.Shield,
            "TActionPlayerHeal" or "TActionPlayerReviveHeal" or "TActionPlayerRegenApply" => ECardArchetype.Sustain,
            "TActionCardHaste" or "TActionCardCharge" or "TActionPlayerTempoApply" => ECardArchetype.Speed,
            "TActionCardFreeze" or "TActionCardSlow" => ECardArchetype.Control,
            "TActionPlayerDamage" or "TActionPlayerRageApply" => ECardArchetype.Damage,
            "TActionCardReload" => ECardArchetype.Ammo,
            "TActionCardFlyingStart" or "TActionCardFlyingToggle" => ECardArchetype.Flying,
            _ => ECardArchetype.None
        };

        if (node.Type is "TActionCardModifyAttribute" or "TActionPlayerModifyAttribute")
        {
            string? attr = node.AttributeType ?? node.PlayerAttributeType ?? node.AttributeChanged;
            if (!string.IsNullOrWhiteSpace(attr))
                mask |= MapModifiedAttributeName(attr);
        }

        if (node.Actions != null)
        {
            foreach (var sub in node.Actions)
                mask |= ExtractMaskFromRuleNode(sub, depth + 1);
        }

        return mask;
    }

    private static ECardArchetype MapModifiedAttributeName(string attr) => attr switch
    {
        "BurnApplyAmount" => ECardArchetype.Burn,
        "PoisonApplyAmount" => ECardArchetype.Poison,
        "ShieldApplyAmount" => ECardArchetype.Shield,
        "HealAmount" or "RegenApplyAmount" or "Lifesteal" or "HealthMax" => ECardArchetype.Sustain,
        "HasteAmount" or "CooldownMax" or "PercentCooldownReduction" => ECardArchetype.Speed,
        "FreezeAmount" or "SlowAmount" => ECardArchetype.Control,
        "CritChance" => ECardArchetype.Crit,
        "DamageAmount" => ECardArchetype.Damage,
        "AmmoMax" or "Multicast" => ECardArchetype.Ammo,
        "Gold" or "Income" or "Value" or "SellPrice" => ECardArchetype.Economy,
        _ => ECardArchetype.None
    };

    private static bool IsLegalCatalogCard(CardPool pool, GameDataRepository? repo, CardPool.CardTemplate tpl)
    {
        if (!pool.IsPlayerSpawnable(tpl, allowGuidOnly: false))
            return false;
        if (repo != null && repo.IsExcluded(tpl.Id))
            return false;
        string name = tpl.InternalName ?? string.Empty;
        return !name.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDebugOrDefunctEncounter(string? internalName)
    {
        if (string.IsNullOrEmpty(internalName)) return false;
        return internalName.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase)
            || internalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)
            || internalName.Contains("Tutorial", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ResolveEffectiveHeroList(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl)
    {
        var heroes = poolTpl?.Heroes is { Length: > 0 }
            ? (IReadOnlyList<string>)poolTpl.Heroes
            : (coreTpl?.Heroes is { Count: > 0 } ? coreTpl.Heroes : [HeroNames.Common]);
        return heroes;
    }

    private static string FormatHeroField(IReadOnlyList<string> heroes)
    {
        if (heroes.Count == 0) return HeroNames.Common;
        if (heroes.Count == 1) return heroes[0];
        return string.Join(",", heroes);
    }

    private static string ResolveNameZh(
        CardPool.CardTemplate poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        GameDataRepository? repo)
    {
        if (repo != null && coreTpl != null)
        {
            string localized = repo.GetLocalizedTitle(coreTpl);
            if (!string.IsNullOrWhiteSpace(localized))
                return localized;
        }
        return !string.IsNullOrWhiteSpace(poolTpl.InternalName) ? poolTpl.InternalName : poolTpl.Id;
    }

    private static Dictionary<string, int> CreateEmptyArchetypeCountMap()
    {
        var map = new Dictionary<string, int>(CardArchetypeCatalog.All.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var meta in CardArchetypeCatalog.All)
            map[meta.Key] = 0;
        return map;
    }

    private static bool ContainsAny(string source, params string[] tokens)
    {
        if (string.IsNullOrEmpty(source)) return false;
        foreach (string token in tokens)
        {
            if (source.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>大厅流派倾向与遭遇配置前端目录 DTO。</summary>
public sealed record CardTaxonomyCatalogDto(
    [property: JsonPropertyName("archetypes")] List<ArchetypeCatalogEntryDto> Archetypes,
    [property: JsonPropertyName("encounterCategories")] List<EncounterCategoryDto> EncounterCategories,
    [property: JsonPropertyName("items")] List<CatalogCardDto> Items,
    [property: JsonPropertyName("skills")] List<CatalogSkillDto> Skills);

/// <summary>单个流派维度及各英雄可用卡计数。</summary>
public sealed record ArchetypeCatalogEntryDto(
    [property: JsonPropertyName("bit")] int Bit,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("nameZh")] string NameZh,
    [property: JsonPropertyName("descZh")] string DescZh,
    [property: JsonPropertyName("heroCounts")] Dictionary<string, int> HeroCounts);

/// <summary>单个遭遇权重分类前端元数据。</summary>
public sealed record EncounterCategoryDto(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("groupZh")] string GroupZh,
    [property: JsonPropertyName("nameZh")] string NameZh,
    [property: JsonPropertyName("defaultMult")] double DefaultMult);

/// <summary>前端目录中的物品摘要条目。</summary>
public sealed record CatalogCardDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nameZh")] string NameZh,
    [property: JsonPropertyName("hero")] string Hero,
    [property: JsonPropertyName("size")] string Size,
    [property: JsonPropertyName("startingTier")] string StartingTier,
    [property: JsonPropertyName("archetypeMask")] ulong ArchetypeMask);

/// <summary>前端目录中的技能摘要条目。</summary>
public sealed record CatalogSkillDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nameZh")] string NameZh,
    [property: JsonPropertyName("hero")] string Hero,
    [property: JsonPropertyName("startingTier")] string StartingTier,
    [property: JsonPropertyName("archetypeMask")] ulong ArchetypeMask);

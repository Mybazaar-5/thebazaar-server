using System.Collections.Frozen;
using BazaarCore.Models;

namespace BazaarCore.Data;

/// <summary>
/// 内存中已索引的只读全量游戏数据库，供战斗、构筑与大厅逻辑毫秒级检索；
/// 提供多维只读索引（ItemsByHero / SkillsByHero / LevelUpsByLevel / AlwaysSpawnable*）以及不可变 Mod Overlay 合并副本能力。
/// </summary>
public sealed class GameDataRepository
{
    public string SourceZipPath { get; }
    public string? SourceDataPath { get; }
    public ILocalizationStore? Localization { get; }
    public GameModeData GameMode { get; }
    public IReadOnlyDictionary<string, CardTemplate> CardsById { get; }
    public IReadOnlyDictionary<string, MonsterTemplate> MonstersById { get; }
    public IReadOnlyDictionary<int, LevelUpTemplate> LevelUps { get; }
    /// <summary>按等级索引的升级表只读视图（Wave 2/3 契约别名）。</summary>
    public IReadOnlyDictionary<int, LevelUpTemplate> LevelUpsByLevel => LevelUps;
    public IReadOnlyDictionary<string, string> TooltipGlossary { get; }
    public IReadOnlySet<string> ObservedRuleTypes { get; }
    public IReadOnlySet<string> UnknownRuleTypes { get; }

    public IReadOnlyList<CardTemplate> AllItems { get; }
    public IReadOnlyList<CardTemplate> AllSkills { get; }
    public IReadOnlyList<CardTemplate> AllEncounters { get; }
    public IReadOnlyList<CardTemplate> AllCombatEncounters { get; }
    public IReadOnlyList<CardTemplate> AllPedestals { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableAll { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableItemsAndSkills { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableItems { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableSkills { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableEvents { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableCombats { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnablePedestals { get; }

    /// <summary>按英雄分桶的 Always 物品只读索引（毫秒级无锁检索，供 Wave 3 CardPool 复用）。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CardTemplate>> ItemsByHero { get; }

    /// <summary>按英雄分桶的 Always 技能只读索引。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CardTemplate>> SkillsByHero { get; }

    /// <summary>按卡牌 Type 分桶的只读索引。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<CardTemplate>> CardsByType { get; }

    private readonly FrozenDictionary<string, CardTemplate> _startRunByHero;

    public GameDataRepository(
        string sourceZipPath,
        GameModeData gameMode,
        Dictionary<string, CardTemplate> cards,
        Dictionary<string, MonsterTemplate> monsters,
        Dictionary<int, LevelUpTemplate> levelUps,
        Dictionary<string, string> tooltips,
        HashSet<string> observedRuleTypes,
        HashSet<string> unknownRuleTypes,
        ILocalizationStore? localization = null,
        string? sourceDataPath = null)
    {
        SourceZipPath = sourceZipPath;
        SourceDataPath = string.IsNullOrEmpty(sourceDataPath) ? sourceZipPath : sourceDataPath;
        Localization = localization;
        GameMode = gameMode;
        CardsById = cards;
        MonstersById = monsters;
        LevelUps = levelUps;
        TooltipGlossary = tooltips;
        ObservedRuleTypes = observedRuleTypes;
        UnknownRuleTypes = unknownRuleTypes;

        var allItems = new List<CardTemplate>(1600);
        var allSkills = new List<CardTemplate>(640);
        var allEncounters = new List<CardTemplate>(1400);
        var allCombats = new List<CardTemplate>(200);
        var allPedestals = new List<CardTemplate>(64);
        var alwaysAll = new List<CardTemplate>(2200);
        var alwaysItemsAndSkills = new List<CardTemplate>(1800);
        var alwaysItems = new List<CardTemplate>(1300);
        var alwaysSkills = new List<CardTemplate>(550);
        var alwaysEvents = new List<CardTemplate>(256);
        var alwaysCombats = new List<CardTemplate>(160);
        var alwaysPedestals = new List<CardTemplate>(48);

        var startRunMap = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase);
        var itemsByHeroMut = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase);
        var skillsByHeroMut = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase);
        var cardsByTypeMut = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var card in cards.Values)
        {
            if (!cardsByTypeMut.TryGetValue(card.Type, out var typeBucket))
            {
                typeBucket = new List<CardTemplate>();
                cardsByTypeMut[card.Type] = typeBucket;
            }
            typeBucket.Add(card);

            bool isAlways = string.Equals(card.SpawningEligibility, "Always", StringComparison.Ordinal);
            if (isAlways)
                alwaysAll.Add(card);

            if (card.Type == "Item")
            {
                allItems.Add(card);
                if (isAlways)
                {
                    alwaysItems.Add(card);
                    alwaysItemsAndSkills.Add(card);
                    for (int i = 0; i < card.Heroes.Count; i++)
                    {
                        var h = card.Heroes[i];
                        if (!itemsByHeroMut.TryGetValue(h, out var list))
                        {
                            list = new List<CardTemplate>();
                            itemsByHeroMut[h] = list;
                        }
                        list.Add(card);
                    }
                }
            }
            else if (card.Type == "Skill")
            {
                allSkills.Add(card);
                if (isAlways)
                {
                    alwaysSkills.Add(card);
                    alwaysItemsAndSkills.Add(card);
                    for (int i = 0; i < card.Heroes.Count; i++)
                    {
                        var h = card.Heroes[i];
                        if (!skillsByHeroMut.TryGetValue(h, out var list))
                        {
                            list = new List<CardTemplate>();
                            skillsByHeroMut[h] = list;
                        }
                        list.Add(card);
                    }
                }
            }
            else if (card.Type.Contains("Encounter", StringComparison.Ordinal))
            {
                allEncounters.Add(card);
                if (card.Type == "CombatEncounter")
                {
                    allCombats.Add(card);
                    if (isAlways && card.CombatantType?.MonsterTemplateId != null)
                        alwaysCombats.Add(card);
                }
                else if (card.Type == "PedestalEncounter")
                {
                    allPedestals.Add(card);
                    if (isAlways)
                        alwaysPedestals.Add(card);
                }
                else if (card.Type == "EventEncounter")
                {
                    if (isAlways && !card.InternalName.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase))
                        alwaysEvents.Add(card);
                    if (card.InternalName.Contains("Start Run", StringComparison.OrdinalIgnoreCase))
                    {
                        for (int i = 0; i < card.Heroes.Count; i++)
                        {
                            var h = card.Heroes[i];
                            if (!startRunMap.ContainsKey(h))
                                startRunMap[h] = card;
                        }
                    }
                }
            }
        }

        AllItems = allItems;
        AllSkills = allSkills;
        AllEncounters = allEncounters;
        AllCombatEncounters = allCombats;
        AllPedestals = allPedestals;
        AlwaysSpawnableAll = alwaysAll;
        AlwaysSpawnableItemsAndSkills = alwaysItemsAndSkills;
        AlwaysSpawnableItems = alwaysItems;
        AlwaysSpawnableSkills = alwaysSkills;
        AlwaysSpawnableEvents = alwaysEvents;
        AlwaysSpawnableCombats = alwaysCombats;
        AlwaysSpawnablePedestals = alwaysPedestals;

        _startRunByHero = startRunMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        ItemsByHero = itemsByHeroMut.ToFrozenDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<CardTemplate>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
        SkillsByHero = skillsByHeroMut.ToFrozenDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<CardTemplate>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
        CardsByType = cardsByTypeMut.ToFrozenDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<CardTemplate>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 基于当前只读仓库创建一个叠加自定义卡牌、野怪与本地化存储的新不可变副本（绝不修改原仓库，支持 Mod 热重载与回滚）。
    /// </summary>
    public GameDataRepository CreateWithOverlay(
        IEnumerable<CardTemplate>? additionalCards = null,
        IEnumerable<MonsterTemplate>? additionalMonsters = null,
        ILocalizationStore? localizationOverride = null,
        GameModeData? gameModeOverride = null)
    {
        var mergedCards = new Dictionary<string, CardTemplate>(CardsById, StringComparer.OrdinalIgnoreCase);
        if (additionalCards != null)
        {
            foreach (var card in additionalCards)
            {
                if (card == null || string.IsNullOrWhiteSpace(card.Id)) continue;
                GameDatabaseLoader.PrecompileCardTemplateRules(card);
                mergedCards[card.Id] = card;
            }
        }

        var mergedMonsters = new Dictionary<string, MonsterTemplate>(MonstersById, StringComparer.OrdinalIgnoreCase);
        if (additionalMonsters != null)
        {
            foreach (var m in additionalMonsters)
            {
                if (m == null || string.IsNullOrWhiteSpace(m.Id)) continue;
                mergedMonsters[m.Id] = m;
            }
        }

        return new GameDataRepository(
            SourceZipPath,
            gameModeOverride ?? GameMode,
            mergedCards,
            mergedMonsters,
            new Dictionary<int, LevelUpTemplate>(LevelUps),
            new Dictionary<string, string>(TooltipGlossary, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ObservedRuleTypes, StringComparer.Ordinal),
            new HashSet<string>(UnknownRuleTypes, StringComparer.Ordinal),
            localizationOverride ?? Localization,
            SourceDataPath);
    }

    /// <summary>当前仓库是否已加载有效的官方中文翻译词库。</summary>
    public bool HasLocalizationLoaded => Localization != null && Localization.EntryCount > 0;

    /// <summary>判断指定卡牌模板是否拥有官方简体中文标题。</summary>
    public bool HasOfficialZhTitle(CardTemplate card) => Localization?.HasOfficialTitle(card) ?? false;

    /// <summary>判断指定卡牌模板是否拥有官方简体中文标题（语义同 HasOfficialZhTitle，便于跨模块统一调用）。</summary>
    public bool HasZhTranslation(CardTemplate? card)
        => card != null && (Localization?.HasOfficialTitle(card) ?? false);

    /// <summary>按模板 ID 判断卡牌是否拥有官方简体中文标题。</summary>
    public bool HasZhTranslation(string? cardId)
        => HasZhTranslation(GetCard(cardId));

    /// <summary>判断卡牌模板是否处于游戏模式全局排除集（GameMode.ExclusionSet）中。</summary>
    public bool IsExcluded(string? cardId)
        => !string.IsNullOrWhiteSpace(cardId)
           && GameMode.ExclusionSet != null
           && GameMode.ExclusionSet.Contains(cardId);

    /// <summary>判断是否为单元测试构造的纯内存合成模板（缺少真实本地化键且非 [DEBUG] 卡）。</summary>
    public static bool IsSyntheticTestTemplate(CardTemplate card)
        => card.Localization == null
           && string.IsNullOrEmpty(card.TranslationKey)
           && !card.InternalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase);

    /// <summary>获取卡牌的本地化标题；若未加载词库则回退为模板自带显示标题。</summary>
    public string GetLocalizedTitle(CardTemplate card) => Localization?.GetLocalizedTitle(card) ?? card.DisplayTitle;

    public CardTemplate? GetCard(string? id)
        => string.IsNullOrEmpty(id) ? null : (CardsById.TryGetValue(id, out var card) ? card : null);

    public MonsterTemplate? GetMonster(string? id)
        => string.IsNullOrEmpty(id) ? null : (MonstersById.TryGetValue(id, out var m) ? m : null);

    public CardTemplate? GetStartRunEncounter(string hero)
        => _startRunByHero.TryGetValue(hero, out var card) ? card : null;

    public IReadOnlyList<CardTemplate> GetSpawnableItemsForHero(string hero, bool includeCommon = true)
    {
        if (!includeCommon)
            return ItemsByHero.TryGetValue(hero, out var onlyHero) ? onlyHero : [];

        var result = new List<CardTemplate>();
        if (ItemsByHero.TryGetValue(hero, out var heroItems))
            result.AddRange(heroItems);
        if (ItemsByHero.TryGetValue(HeroNames.Common, out var commonItems))
            result.AddRange(commonItems);
        return result;
    }

    public IReadOnlyList<CardTemplate> GetSpawnableSkillsForHero(string hero, bool includeCommon = true)
    {
        if (!includeCommon)
            return SkillsByHero.TryGetValue(hero, out var onlyHero) ? onlyHero : [];

        var result = new List<CardTemplate>();
        if (SkillsByHero.TryGetValue(hero, out var heroSkills))
            result.AddRange(heroSkills);
        if (SkillsByHero.TryGetValue(HeroNames.Common, out var commonSkills))
            result.AddRange(commonSkills);
        return result;
    }
}

using BazaarCore.Models;

namespace BazaarCore.Data;

/// <summary>
/// 内存中已索引的只读全量游戏数据库，供战斗、构筑与大厅逻辑毫秒级检索。
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
    public IReadOnlyDictionary<string, string> TooltipGlossary { get; }
    public IReadOnlySet<string> ObservedRuleTypes { get; }
    public IReadOnlySet<string> UnknownRuleTypes { get; }

    public IReadOnlyList<CardTemplate> AllItems { get; }
    public IReadOnlyList<CardTemplate> AllSkills { get; }
    public IReadOnlyList<CardTemplate> AllEncounters { get; }
    public IReadOnlyList<CardTemplate> AllCombatEncounters { get; }
    public IReadOnlyList<CardTemplate> AllPedestals { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableItems { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableSkills { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableEvents { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnableCombats { get; }
    public IReadOnlyList<CardTemplate> AlwaysSpawnablePedestals { get; }

    private readonly Dictionary<string, CardTemplate> _startRunByHero = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<CardTemplate>> _itemsByHero = new(StringComparer.OrdinalIgnoreCase);

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

        AllItems = cards.Values.Where(c => c.Type == "Item").ToList();
        AllSkills = cards.Values.Where(c => c.Type == "Skill").ToList();
        AllEncounters = cards.Values.Where(c => c.Type.Contains("Encounter", StringComparison.Ordinal)).ToList();
        AllCombatEncounters = cards.Values.Where(c => c.Type == "CombatEncounter").ToList();
        AllPedestals = cards.Values.Where(c => c.Type == "PedestalEncounter").ToList();

        AlwaysSpawnableItems = AllItems.Where(c => c.SpawningEligibility == "Always").ToList();
        AlwaysSpawnableSkills = AllSkills.Where(c => c.SpawningEligibility == "Always").ToList();
        AlwaysSpawnableEvents = cards.Values
            .Where(c => c.Type == "EventEncounter" && c.SpawningEligibility == "Always" && !c.InternalName.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        AlwaysSpawnableCombats = AllCombatEncounters
            .Where(c => c.SpawningEligibility == "Always" && c.CombatantType?.MonsterTemplateId != null)
            .ToList();
        AlwaysSpawnablePedestals = AllPedestals
            .Where(c => c.SpawningEligibility == "Always")
            .ToList();

        foreach (var card in cards.Values)
        {
            if (card.Type == "EventEncounter" && card.InternalName.Contains("Start Run", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var h in card.Heroes)
                {
                    if (!_startRunByHero.ContainsKey(h))
                        _startRunByHero[h] = card;
                }
            }
            if (card.Type == "Item" && card.SpawningEligibility == "Always")
            {
                foreach (var h in card.Heroes)
                {
                    if (!_itemsByHero.TryGetValue(h, out var list))
                    {
                        list = new List<CardTemplate>();
                        _itemsByHero[h] = list;
                    }
                    list.Add(card);
                }
            }
        }
    }

    /// <summary>
    /// 当前仓库是否已加载有效的官方中文翻译词库。
    /// </summary>
    public bool HasLocalizationLoaded => Localization != null && Localization.EntryCount > 0;

    /// <summary>
    /// 判断指定卡牌模板是否拥有官方简体中文标题。
    /// </summary>
    public bool HasOfficialZhTitle(CardTemplate card) => Localization?.HasOfficialTitle(card) ?? false;

    /// <summary>
    /// 判断指定卡牌模板是否拥有官方简体中文标题（语义同 HasOfficialZhTitle，便于跨模块统一调用）。
    /// </summary>
    public bool HasZhTranslation(CardTemplate? card)
        => card != null && (Localization?.HasOfficialTitle(card) ?? false);

    /// <summary>
    /// 按模板 ID 判断卡牌是否拥有官方简体中文标题。
    /// </summary>
    public bool HasZhTranslation(string? cardId)
        => HasZhTranslation(GetCard(cardId));

    /// <summary>
    /// 判断卡牌模板是否处于游戏模式全局排除集（GameMode.ExclusionSet）中。
    /// </summary>
    public bool IsExcluded(string? cardId)
        => !string.IsNullOrWhiteSpace(cardId)
           && GameMode.ExclusionSet != null
           && GameMode.ExclusionSet.Contains(cardId);

    /// <summary>
    /// 判断是否为单元测试构造的纯内存合成模板（缺少真实本地化键且非 [DEBUG] 卡）。
    /// </summary>
    public static bool IsSyntheticTestTemplate(CardTemplate card)
        => card.Localization == null
           && string.IsNullOrEmpty(card.TranslationKey)
           && !card.InternalName.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 获取卡牌的本地化标题；若未加载词库则回退为模板自带显示标题。
    /// </summary>
    public string GetLocalizedTitle(CardTemplate card) => Localization?.GetLocalizedTitle(card) ?? card.DisplayTitle;

    public CardTemplate? GetCard(string? id)
        => string.IsNullOrEmpty(id) ? null : (CardsById.TryGetValue(id, out var card) ? card : null);

    public MonsterTemplate? GetMonster(string? id)
        => string.IsNullOrEmpty(id) ? null : (MonstersById.TryGetValue(id, out var m) ? m : null);

    public CardTemplate? GetStartRunEncounter(string hero)
        => _startRunByHero.TryGetValue(hero, out var card) ? card : null;

    public IReadOnlyList<CardTemplate> GetSpawnableItemsForHero(string hero, bool includeCommon = true)
    {
        var result = new List<CardTemplate>();
        if (_itemsByHero.TryGetValue(hero, out var heroItems))
            result.AddRange(heroItems);
        if (includeCommon && _itemsByHero.TryGetValue(HeroNames.Common, out var commonItems))
            result.AddRange(commonItems);
        return result;
    }
}

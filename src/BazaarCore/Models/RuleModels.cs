using System.Text.Json;
using System.Text.Json.Serialization;

namespace BazaarCore.Models;

/// <summary>
/// 全局规则节点：完整承载 GameData.db 中 182+ 个 $type 多态规则节点，
/// 所有节点统一保留强类型常用字段及扩展字段，并在反序列化时向审计器登记。
/// </summary>
public sealed class RuleNode
{
    [JsonPropertyName("$type")]
    public string Type { get; set; } = string.Empty;

    // 通用引用/比较/操作字段
    public string? Id { get; set; }
    public List<string>? Ids { get; set; }
    public double? MinValue { get; set; }
    public double? MaxValue { get; set; }
    public double? DefaultValue { get; set; }
    public double? Amount { get; set; }
    [JsonPropertyName("ComparisonValue")]
    public JsonElement? RawComparisonValueElement { get; set; }
    public double? ComparisonAmount { get; set; }
    /// <summary>P2-19：兼容官方 RuleNode 表达式与历史标量数值格式。</summary>
    [JsonPropertyName("CurrentValue")]
    public JsonElement? RawCurrentValueElement { get; set; }
    /// <summary>P2-19：兼容官方 RuleNode 表达式与历史标量数值格式。</summary>
    [JsonPropertyName("PreviousValue")]
    public JsonElement? RawPreviousValueElement { get; set; }
    [JsonIgnore]
    public double? CurrentValue
    {
        get => GetFixedCurrentValue();
        set
        {
            RawCurrentValueElement = value.HasValue ? JsonSerializer.SerializeToElement(value.Value) : null;
            Volatile.Write(ref _cachedCurrentValueNode, null);
        }
    }
    [JsonIgnore]
    public double? PreviousValue
    {
        get => GetFixedPreviousValue();
        set
        {
            RawPreviousValueElement = value.HasValue ? JsonSerializer.SerializeToElement(value.Value) : null;
            Volatile.Write(ref _cachedPreviousValueNode, null);
        }
    }
    /// <summary>P2-06：当 SelectionRequirements 以 JSON 数组提供多条选择前置条件时，保留原始条目列表。</summary>
    [JsonIgnore]
    public List<RuleNode>? CombinedSelectionRequirements { get; set; }
    public double? UpgradeChance { get; set; }
    public int? RandomWeight { get; set; }
    public int? CurrentDay { get; set; }
    public int? CurrentHour { get; set; }
    public int? DurationInMs { get; set; }

    public string? AttributeType { get; set; }
    public string? AttributeChanged { get; set; }
    public string? Attribute { get; set; }
    public string? AttributeOther { get; set; }
    public string? PlayerAttributeType { get; set; }
    public string? Operation { get; set; }
    public string? ComparisonOperator { get; set; }
    public string? Comparison { get; set; }
    public string? Operator { get; set; }
    public string? TargetSection { get; set; }
    public string? TargetMode { get; set; }
    public string? Origin { get; set; }
    public string? DurationType { get; set; }
    public string? ChangeType { get; set; }
    public string? CombatOutcome { get; set; }
    public string? CombatType { get; set; }
    public string? SelectionMethod { get; set; }
    public string? RerollType { get; set; }
    public string? SpawnMode { get; set; }
    public string? CardType { get; set; }
    public string? Enchantment { get; set; }
    public string? UpgradeToTier { get; set; }
    public string? TargetTier { get; set; }
    public string? MonsterTemplateId { get; set; }
    public int? Level { get; set; }
    public int? NewLevel { get; set; }
    public string? OwnerComparison { get; set; }
    public string? TriggerEffectOwner { get; set; }
    public string? EncounterId { get; set; }
    public string? GameMode { get; set; }
    public string? Outcome { get; set; }

    public bool? IsNot { get; set; }
    public bool? ExcludeSelf { get; set; }
    public bool? IncludeOrigin { get; set; }
    public bool? IncludeCommon { get; set; }
    public bool? IsActive { get; set; }
    public bool? Distinct { get; set; }
    public bool? IsSameAsPlayerHero { get; set; }
    public bool? ExcludeFusion { get; set; }
    public bool? PreventOverride { get; set; }
    public bool? InheritEnchantment { get; set; }
    public bool? AllowDuplicates { get; set; }
    public bool? AllowPlayerDuplicates { get; set; }
    public bool? ForceNative { get; set; }
    public bool? IgnoreHero { get; set; }
    public bool? IgnoreTierTable { get; set; }
    public bool? Inherits { get; set; }

    public List<string>? Tags { get; set; }
    public List<string>? FilterTags { get; set; }
    public List<string>? HiddenTags { get; set; }
    public List<string>? Heroes { get; set; }
    public List<string>? Sizes { get; set; }
    public List<string>? Tiers { get; set; }
    public List<string>? Types { get; set; }
    public List<string>? States { get; set; }
    [JsonPropertyName("Enchantments")]
    public JsonElement? RawEnchantmentsElement { get; set; }
    public List<string>? PlayModes { get; set; }
    public List<string>? GameModeIds { get; set; }
    public List<string>? EncounterIds { get; set; }
    public JsonElement? MusicNote { get; set; }

    // 嵌套子节点
    [JsonPropertyName("Value")]
    public JsonElement? RawValueNode { get; set; }

    public RuleNode? ReferenceValue { get; set; }
    public RuleNode? Target { get; set; }
    public RuleNode? TargetPlayer { get; set; }
    public RuleNode? TargetCount { get; set; }
    public RuleNode? Source { get; set; }
    public RuleNode? Subject { get; set; }
    public RuleNode? SubjectOther { get; set; }
    public RuleNode? Duration { get; set; }
    public RuleNode? Cost { get; set; }
    public RuleNode? Limit { get; set; }
    public RuleNode? SpawnContext { get; set; }
    [JsonPropertyName("Constraints")]
    public JsonElement? RawConstraintsElement { get; set; }
    public RuleNode? SelectionContextRules { get; set; }
    public ValueModifierNode? Modifier { get; set; }

    // 集合子节点（保持名称与 JSON 一致）
    [JsonPropertyName("Conditions")]
    public JsonElement? RawConditionsElement { get; set; }

    public List<RuleNode>? Actions { get; set; }
    public List<RuleNode>? Triggers { get; set; }
    public List<RuleNode>? Groups { get; set; }
    public List<RuleNode>? Filters { get; set; }
    public List<RuleNode>? Behaviors { get; set; }
    public List<RuleNode>? Prerequisites { get; set; }
    public int? SpawnCount { get; set; }
    public Dictionary<string, AbilityDefinition>? Abilities { get; set; }
    /// <summary>生成或动作附带的初始属性值映射（如 TActionGameSpawnCards 的 {"Tiny": TFixedValue(1.0)}）。</summary>
    public Dictionary<string, RuleNode?>? Attributes { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    // 线程安全延迟反序列化缓存（避免战斗高频调用重复走 JsonSerializer）
    private static readonly object NullCacheSentinel = new();
    private object? _cachedValueNode;
    private object? _cachedSingleCondition;
    private object? _cachedConditionList;
    private object? _cachedSingleConstraint;
    private object? _cachedConstraintList;
    private object? _cachedComparisonValueNode;
    private object? _cachedCurrentValueNode;
    private object? _cachedPreviousValueNode;
    private List<(string Enchantment, double Weight)>? _cachedEnchantments;

    /// <summary>P2-19：若 CurrentValue 直接为数字或 TFixedValue 节点则返回数值，否则返回 null。</summary>
    public double? GetFixedCurrentValue()
        => TryExtractFixedNumeric(RawCurrentValueElement);

    /// <summary>P2-19：若 PreviousValue 直接为数字或 TFixedValue 节点则返回数值，否则返回 null。</summary>
    public double? GetFixedPreviousValue()
        => TryExtractFixedNumeric(RawPreviousValueElement);

    private static double? TryExtractFixedNumeric(JsonElement? element)
    {
        if (!element.HasValue) return null;
        var el = element.Value;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d))
            return d;
        if (el.ValueKind == JsonValueKind.Object
            && (!el.TryGetProperty("$type", out var tEl)
                || string.Equals(tEl.GetString(), "TFixedValue", StringComparison.OrdinalIgnoreCase))
            && el.TryGetProperty("Value", out var vEl)
            && vEl.ValueKind == JsonValueKind.Number
            && vEl.TryGetDouble(out var fd))
        {
            return fd;
        }
        return null;
    }

    /// <summary>P2-19：当 CurrentValue 为嵌套 RuleNode 对象时反序列化并线程安全缓存返回。</summary>
    public RuleNode? GetCurrentValueNode(JsonSerializerOptions options)
    {
        if (!RawCurrentValueElement.HasValue || RawCurrentValueElement.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedCurrentValueNode);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawCurrentValueElement.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedCurrentValueNode, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedCurrentValueNode);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    /// <summary>P2-19：当 PreviousValue 为嵌套 RuleNode 对象时反序列化并线程安全缓存返回。</summary>
    public RuleNode? GetPreviousValueNode(JsonSerializerOptions options)
    {
        if (!RawPreviousValueElement.HasValue || RawPreviousValueElement.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedPreviousValueNode);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawPreviousValueElement.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedPreviousValueNode, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedPreviousValueNode);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    /// <summary>
    /// 若该节点的 Value 属性是数字（TFixedValue.Value）则直接返回数值；若为嵌套 RuleNode 则解析为 RuleNode。
    /// </summary>
    public double? GetFixedNumericValue()
    {
        if (RawValueNode.HasValue && RawValueNode.Value.ValueKind == JsonValueKind.Number && RawValueNode.Value.TryGetDouble(out var d))
            return d;
        return null;
    }

    /// <summary>
    /// 若 ComparisonValue 直接是数字则返回数值；若为对象表达式则返回 null。
    /// </summary>
    public double? GetFixedComparisonValue()
    {
        if (RawComparisonValueElement.HasValue && RawComparisonValueElement.Value.ValueKind == JsonValueKind.Number && RawComparisonValueElement.Value.TryGetDouble(out var d))
            return d;
        return null;
    }

    /// <summary>
    /// 当节点上的 ComparisonValue 字段是一个嵌套的 RuleNode（如 TFixedValue / TReferenceValue*）时反序列化并缓存返回。
    /// </summary>
    public RuleNode? GetComparisonValueNode(JsonSerializerOptions options)
    {
        if (!RawComparisonValueElement.HasValue || RawComparisonValueElement.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedComparisonValueNode);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawComparisonValueElement.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedComparisonValueNode, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedComparisonValueNode);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    /// <summary>
    /// 当节点上的 Value 字段本身是一个嵌套的 Value 计算节点（例如 TFixedValue / TReferenceValue*）时反序列化并缓存返回。
    /// </summary>
    public RuleNode? GetValueNode(JsonSerializerOptions options)
    {
        if (!RawValueNode.HasValue || RawValueNode.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedValueNode);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawValueNode.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedValueNode, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedValueNode);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    /// <summary>
    /// 统一提取单个条件对象或条件数组（例如 TCardConditionalAnd.Conditions 是数组，而 Target.Conditions 是单对象）。
    /// </summary>
    public RuleNode? GetSingleCondition(JsonSerializerOptions options)
    {
        if (!RawConditionsElement.HasValue || RawConditionsElement.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedSingleCondition);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawConditionsElement.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedSingleCondition, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedSingleCondition);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    public List<RuleNode> GetConditionList(JsonSerializerOptions options)
    {
        if (!RawConditionsElement.HasValue) return [];
        if (RawConditionsElement.Value.ValueKind == JsonValueKind.Array)
        {
            var cached = Volatile.Read(ref _cachedConditionList) as List<RuleNode>;
            if (cached != null) return cached;
            var parsed = RawConditionsElement.Value.Deserialize<List<RuleNode>>(options) ?? [];
            Interlocked.CompareExchange(ref _cachedConditionList, parsed, null);
            return (List<RuleNode>)Volatile.Read(ref _cachedConditionList)!;
        }
        var single = GetSingleCondition(options);
        return single != null ? [single] : [];
    }

    public RuleNode? GetSingleConstraint(JsonSerializerOptions options)
    {
        if (!RawConstraintsElement.HasValue || RawConstraintsElement.Value.ValueKind != JsonValueKind.Object)
            return null;
        var cached = Volatile.Read(ref _cachedSingleConstraint);
        if (cached != null) return ReferenceEquals(cached, NullCacheSentinel) ? null : (RuleNode)cached;
        var parsed = RawConstraintsElement.Value.Deserialize<RuleNode>(options);
        Interlocked.CompareExchange(ref _cachedSingleConstraint, (object?)parsed ?? NullCacheSentinel, null);
        var finalObj = Volatile.Read(ref _cachedSingleConstraint);
        return ReferenceEquals(finalObj, NullCacheSentinel) ? null : (RuleNode?)finalObj;
    }

    public List<RuleNode> GetConstraintList(JsonSerializerOptions options)
    {
        if (!RawConstraintsElement.HasValue) return [];
        if (RawConstraintsElement.Value.ValueKind == JsonValueKind.Array)
        {
            var cached = Volatile.Read(ref _cachedConstraintList) as List<RuleNode>;
            if (cached != null) return cached;
            var parsed = RawConstraintsElement.Value.Deserialize<List<RuleNode>>(options) ?? [];
            Interlocked.CompareExchange(ref _cachedConstraintList, parsed, null);
            return (List<RuleNode>)Volatile.Read(ref _cachedConstraintList)!;
        }
        var single = GetSingleConstraint(options);
        return single != null ? [single] : [];
    }

    public List<(string Enchantment, double Weight)> GetWeightedEnchantments()
    {
        var cached = Volatile.Read(ref _cachedEnchantments);
        if (cached != null) return cached;

        var list = new List<(string, double)>();
        if (!RawEnchantmentsElement.HasValue || RawEnchantmentsElement.Value.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var el in RawEnchantmentsElement.Value.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var name = el.GetString();
                if (!string.IsNullOrEmpty(name)) list.Add((name, 1.0));
            }
            else if (el.ValueKind == JsonValueKind.Object)
            {
                var name = el.TryGetProperty("Enchantment", out var en) ? en.GetString() : null;
                var weight = el.TryGetProperty("Weight", out var wt) && wt.TryGetDouble(out var w) ? w : 1.0;
                if (!string.IsNullOrEmpty(name)) list.Add((name, weight));
            }
        }
        Interlocked.CompareExchange(ref _cachedEnchantments, list, null);
        return Volatile.Read(ref _cachedEnchantments)!;
    }
}

public sealed class ValueModifierNode
{
    public string ModifyMode { get; set; } = "Add";
    public RuleNode? Value { get; set; }
    public bool? ShouldRound { get; set; }
}

/// <summary>
/// 权威登记 GameData.db 与官方 BazaarGameShared.dll 中出现的全部非 TSeason $type 名称，用于自动化测试校验 100% 识别率。
/// </summary>
public static class RuleTypeRegistry
{
    public static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        // Card types (10)
        "TCardItem", "TCardSkill", "TCardEncounterEvent", "TCardEncounterStep",
        "TCardEncounterCombat", "TCardEncounterPedestal", "TCardEncounterPvp",
        "TCardPlayerEffect", "TCardMusicNoteSocketEffect", "TCardSocketEffect",
        // Instance types (4)
        "TCardInstanceItem", "TCardInstancePlayerEffect", "TCardInstanceSkill", "TCardInstanceSocketEffect",
        // GameMode & LevelUp & Combatant (4)
        "TGameMode", "TLevelUp", "TCombatantMonster", "TCombatantPVPGhost",
        // Pedestal behaviors (4)
        "TPedestalBehaviorEnchant", "TPedestalBehaviorEnchantRandom", "TPedestalBehaviorTransform", "TPedestalBehaviorUpgrade",
        // Triggers & Comparators (73)
        "TCardAttributeComparator",
        "TTriggerOnBeforeCardDestroyed", "TTriggerOnBeforeCardSold", "TTriggerOnBeforeCardTransformed",
        "TTriggerOnBeforeFightStarted", "TTriggerOnBeforeItemUsed", "TTriggerOnCardAfterTransform",
        "TTriggerOnCardAttributeChanged", "TTriggerOnCardCharged", "TTriggerOnCardConsumed",
        "TTriggerOnCardCritted", "TTriggerOnCardDestroyed", "TTriggerOnCardDisabled", "TTriggerOnCardEnchanted",
        "TTriggerOnCardFired", "TTriggerOnCardFrozen", "TTriggerOnCardHasted",
        "TTriggerOnCardPerformedBurn", "TTriggerOnCardPerformedCharge", "TTriggerOnCardPerformedDamage",
        "TTriggerOnCardPerformedDestruction", "TTriggerOnCardPerformedFreeze", "TTriggerOnCardPerformedHaste",
        "TTriggerOnCardPerformedHeal", "TTriggerOnCardPerformedOverHeal", "TTriggerOnCardPerformedPoison",
        "TTriggerOnCardPerformedRage", "TTriggerOnCardPerformedRegen", "TTriggerOnCardPerformedReload",
        "TTriggerOnCardPerformedShield", "TTriggerOnCardPerformedSlow", "TTriggerOnCardPerformedTempo",
        "TTriggerOnCardPerformedTransform", "TTriggerOnCardPurchased", "TTriggerOnCardQuestCompleted",
        "TTriggerOnCardReloaded", "TTriggerOnCardRepaired", "TTriggerOnCardSelected", "TTriggerOnCardSlowed",
        "TTriggerOnCardSold", "TTriggerOnCardStartedFlying", "TTriggerOnCardStartsFlying",
        "TTriggerOnCardStoppedFlying", "TTriggerOnCardStopsFlying", "TTriggerOnCardTransformed",
        "TTriggerOnCardUpgraded", "TTriggerOnDayStarted", "TTriggerOnEncounterCardsDealt",
        "TTriggerOnEncounterEntered", "TTriggerOnEncounterExited", "TTriggerOnEncounterSelected",
        "TTriggerOnFightEnded", "TTriggerOnFightStarted", "TTriggerOnHourStarted",
        "TTriggerOnItemUsed", "TTriggerOnPlayerAttributeChanged", "TTriggerOnPlayerAttributePercentChange",
        "TTriggerOnPlayerBurned", "TTriggerOnPlayerDamaged", "TTriggerOnPlayerDied",
        "TTriggerOnPlayerEnrageEnded", "TTriggerOnPlayerEnraged", "TTriggerOnPlayerHealed",
        "TTriggerOnPlayerLeveledUp", "TTriggerOnPlayerLifeStealed", "TTriggerOnPlayerPoisoned",
        "TTriggerOnPlayerRaged", "TTriggerOnPlayerRagedWhileEnraged", "TTriggerOnPlayerShielded",
        "TTriggerOnRunCompleted", "TTriggerOnRunStarted", "TTriggerOnSandstorm", "TTriggerOr",
        // Actions (56)
        "TActionAnd", "TActionCardAddTagsBySource", "TActionCardAddTagsList", "TActionCardAddTagsRandom",
        "TActionCardBeginSandstorm", "TActionCardCharge", "TActionCardChill", "TActionCardDestroy",
        "TActionCardDisable", "TActionCardEnchant", "TActionCardEnchantRandom", "TActionCardEnchantRemove",
        "TActionCardFlyingStart", "TActionCardFlyingStop", "TActionCardFlyingToggle",
        "TActionCardForceUse", "TActionCardFreeze", "TActionCardHaste", "TActionCardHeat",
        "TActionCardModifyAttribute", "TActionCardReload", "TActionCardRemoveTagsBySource",
        "TActionCardRemoveTagsList", "TActionCardRepair", "TActionCardSlow",
        "TActionCardTiny", "TActionCardTransform", "TActionCardTransformDestroyed", "TActionCardUpgrade",
        "TActionCostCardAttribute", "TActionCostPlayerAttribute", "TActionExitReplacementSet",
        "TActionGameAddToExclusionSet", "TActionGameDealCards", "TActionGameRemoveFromExclusionSet",
        "TActionGameReroll", "TActionGameSetNextHourSpawnContext", "TActionGameSpawnCards",
        "TActionPlayerBurnApply", "TActionPlayerBurnRemove", "TActionPlayerDamage",
        "TActionPlayerHeal", "TActionPlayerModifyAttribute", "TActionPlayerPoisonApply",
        "TActionPlayerPoisonRemove", "TActionPlayerPortraitNext", "TActionPlayerPortraitReset",
        "TActionPlayerRageApply", "TActionPlayerRageRemove", "TActionPlayerRegenApply",
        "TActionPlayerRegenRemove", "TActionPlayerReviveHeal", "TActionPlayerShieldApply",
        "TActionPlayerShieldRemove", "TActionPlayerTempoApply", "TActionPlayerTempoRemove",
        // AuraActions (6)
        "TAuraActionCardAddTagsBySource", "TAuraActionCardAddTagsList",
        "TAuraActionCardModifyAttribute", "TAuraActionCardRemoveTagsBySource",
        "TAuraActionCardRemoveTagsList", "TAuraActionPlayerModifyAttribute",
        // Targets (11)
        "TTargetCardOccupying", "TTargetCardPositional", "TTargetCardRandom",
        "TTargetCardSection", "TTargetCardSelf", "TTargetCardTriggerSource",
        "TTargetCardTriggerTarget", "TTargetCardXMost",
        "TTargetPlayer", "TTargetPlayerAbsolute", "TTargetPlayerRelative",
        // Values & Durations (15)
        "TFixedValue", "TRangeValue", "TReferenceValueAttributeChange",
        "TReferenceValueCardAttribute", "TReferenceValueCardAttributeAggregate",
        "TReferenceValueCardAttributeUnscaled", "TReferenceValueCardCount", "TReferenceValueCardHeroCount",
        "TReferenceValueCardTagCount", "TReferenceValuePlayerAttribute",
        "TReferenceValuePlayerAttributeUnscaled", "TReferenceValueUniqueCardCount",
        "TCombatDuration", "TDeterminantDuration", "TVariableDuration",
        // Prerequisites (4)
        "TPrerequisiteCardAttributeComparator", "TPrerequisiteCardCount",
        "TPrerequisitePlayer", "TPrerequisiteRun",
        // Conditionals (38)
        "TCardConditionalAnd", "TCardConditionalAttribute", "TCardConditionalAttributeHighest",
        "TCardConditionalAttributeLowest", "TCardConditionalCanCrit", "TCardConditionalEnchantmentEligible",
        "TCardConditionalHasEnchantment", "TCardConditionalHero", "TCardConditionalHiddenTag", "TCardConditionalId",
        "TCardConditionalOr", "TCardConditionalPlayerHero", "TCardConditionalSize",
        "TCardConditionalSizeComparison", "TCardConditionalSizeLargest", "TCardConditionalSizeSmallest",
        "TCardConditionalState", "TCardConditionalTag", "TCardConditionalTier",
        "TCardConditionalTriggerEffectOwner", "TCardConditionalTriggerSource", "TCardConditionalType",
        "TPlayerConditionalAnd", "TPlayerConditionalAttribute", "TPlayerConditionalAttributeHighest",
        "TPlayerConditionalAttributeLowest", "TPlayerConditionalOr",
        "TRunConditionalAnd", "TRunConditionalCurrentDay", "TRunConditionalCurrentEncounter",
        "TRunConditionalCurrentHour", "TRunConditionalGameMode", "TRunConditionalHasVisitedEncounter",
        "TRunConditionalMusicNoteHasValidSocket", "TRunConditionalOr", "TRunConditionalPlayMode",
        "TRunConditionalPlayerHero", "TRunConditionalSandstorm",
        // Spawn & Constraints (35)
        "TSpawnContextQuery", "TSpawnContextMirror", "TSpawnGroup",
        "TSpawnFilterIdList", "TSpawnFilterQuery", "TSpawnFilterTarget", "TSpawnFilterUpgrade",
        "TSpawnBehaviorAllowDuplicates", "TSpawnBehaviorAllowPlayerDuplicates", "TSpawnBehaviorAvoidAdjacentSocketEffect",
        "TSpawnBehaviorDownShiftTier", "TSpawnBehaviorEnchantment",
        "TSpawnBehaviorExcludePlayerHero", "TSpawnBehaviorForceNative", "TSpawnBehaviorIgnoreHero",
        "TSpawnBehaviorIgnoreSpaceConstraint", "TSpawnBehaviorIgnoreTierTable", "TSpawnBehaviorIncludeCommonAndHero",
        "TSpawnBehaviorInheritEnchantment", "TSpawnBehaviorInheritSize", "TSpawnBehaviorInheritTier",
        "TSpawnBehaviorMonsterLoot", "TSpawnBehaviorTier",
        "ConstraintAnd", "ConstraintOr", "ConstraintCardType", "ConstraintEnchantmentEligible",
        "ConstraintHero", "ConstraintHiddenTag", "ConstraintId", "ConstraintIsOnlyHero",
        "ConstraintSize", "ConstraintTag", "ConstraintTier"
    };
}

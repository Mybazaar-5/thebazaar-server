using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Models;

namespace BazaarCore.Mods;

/// <summary>
/// Mod 内容目录映射（对应 mod.json 中的 contentRoots 字段）。
/// </summary>
public sealed class ModContentRoots
{
    [JsonPropertyName("cards")]
    public string Cards { get; set; } = "cards/*.json";

    [JsonPropertyName("patches")]
    public string Patches { get; set; } = "patches/*.json";

    [JsonPropertyName("events")]
    public string Events { get; set; } = "events/*.json";

    [JsonPropertyName("modes")]
    public string Modes { get; set; } = "modes/*.json";

    [JsonPropertyName("localization")]
    public string Localization { get; set; } = "localization/zh-CN.json";

    [JsonPropertyName("assets")]
    public string Assets { get; set; } = "assets/*.png";

    [JsonPropertyName("artManifest")]
    public string? ArtManifest { get; set; } = "assets/art_manifest.json";
}

/// <summary>
/// 单个 Mod 清单与安全隔离声明（完整对应 mods/&lt;mod-id&gt;/mod.json）。
/// </summary>
public sealed class ModManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    /// <summary>全局唯一 Mod 标识（小写字母/数字/连字符，如 dragon-forge）。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>兼容旧契约别名：等价于 <see cref="Id"/>。</summary>
    [JsonIgnore]
    public string ModId
    {
        get => Id;
        set => Id = value;
    }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>兼容旧契约别名：等价于 <see cref="Name"/>。</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get => Name;
        set => Name = value;
    }

    /// <summary>兼容中文展示名别名 displayNameZh（当 name 为空时自动回填）。</summary>
    [JsonPropertyName("displayNameZh")]
    public string? DisplayNameZh
    {
        get => string.IsNullOrWhiteSpace(Name) ? null : Name;
        set
        {
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(value))
                Name = value.Trim();
        }
    }

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>兼容中文简介别名 descriptionZh（当 description 为空时自动回填）。</summary>
    [JsonPropertyName("descriptionZh")]
    public string? DescriptionZh
    {
        get => string.IsNullOrWhiteSpace(Description) ? null : Description;
        set
        {
            if (string.IsNullOrWhiteSpace(Description) && !string.IsNullOrWhiteSpace(value))
                Description = value.Trim();
        }
    }

    [JsonPropertyName("coreApiVersion")]
    public string CoreApiVersion { get; set; } = "2.0.0";

    [JsonPropertyName("minServerVersion")]
    public string MinServerVersion { get; set; } = "2.0.0";

    [JsonPropertyName("minLauncherVersion")]
    public string MinLauncherVersion { get; set; } = "2.0.0";

    [JsonPropertyName("minGameDataVersion")]
    public string MinGameDataVersion { get; set; } = "5.0.0";

    /// <summary>启用范围：singleplayer-only | multiplayer-safe | cosmetic-only。</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "multiplayer-safe";

    /// <summary>
    /// 是否允许影响标准镜像池（规范默认 false，即包含自定义卡的对局默认强制隔离至自定义幽灵池，绝不污染标准镜像池）。
    /// </summary>
    [JsonPropertyName("affectsGhostPool")]
    public bool AffectsGhostPool { get; set; } = false;

    /// <summary>强制隔离标准幽灵池的显式语义标记（仅 cosmetic-only 且无内容改动时可免隔离）。</summary>
    [JsonIgnore]
    public bool ForcesGhostPoolIsolation =>
        !string.Equals(Scope, "cosmetic-only", StringComparison.OrdinalIgnoreCase) || AffectsGhostPool;

    [JsonPropertyName("dependencies")]
    public List<string> Dependencies { get; set; } = new();

    /// <summary>是否默认启用（配合 mods/ 自动发现时的启停控制）。</summary>
    [JsonPropertyName("enabledByDefault")]
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>同拓扑层加载顺序权重（数值越小越优先加载，默认 100）。</summary>
    [JsonPropertyName("loadOrder")]
    public int LoadOrder { get; set; } = 100;

    [JsonPropertyName("contentRoots")]
    public ModContentRoots ContentRoots { get; set; } = new();
}

/// <summary>
/// 自定义卡图裁剪与尺寸声明。
/// </summary>
public sealed class CustomCardArtRect
{
    [JsonPropertyName("width")]
    public int Width { get; set; } = 512;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 512;

    [JsonPropertyName("cropMode")]
    public string CropMode { get; set; } = "cover";
}

/// <summary>
/// 兼容嵌套形态的自定义卡图声明（customArt: { logicalPath, sha256, width, height, cropMode }）。
/// </summary>
public sealed class CustomCardArtSpec
{
    [JsonPropertyName("logicalPath")]
    public string? LogicalPath { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; } = 512;

    [JsonPropertyName("height")]
    public int Height { get; set; } = 512;

    [JsonPropertyName("cropMode")]
    public string CropMode { get; set; } = "cover";
}

/// <summary>
/// 兼容嵌套形态的自定义事件调度声明（schedule: { minDay, maxDay, weight, hours, oncePerRun, chainOnly }）。
/// </summary>
public sealed class CustomEventSchedule
{
    [JsonPropertyName("minDay")]
    public int? MinDay { get; set; }

    [JsonPropertyName("maxDay")]
    public int? MaxDay { get; set; }

    [JsonPropertyName("weight")]
    public double? Weight { get; set; }

    [JsonPropertyName("hours")]
    public List<int>? Hours { get; set; }

    [JsonPropertyName("oncePerRun")]
    public bool OncePerRun { get; set; }

    [JsonPropertyName("chainOnly")]
    public bool ChainOnly { get; set; }
}

/// <summary>
/// 自定义卡牌 / 技能 / 奇遇事件 / 奇遇步骤 / 野怪战斗定义（对应 mods/&lt;mod-id&gt;/cards/*.json）。
/// 强制要求合法的 <see cref="BaseVisualTemplateId"/>（同 Type 且 Item 同 Size）以彻底防范原版客户端白块与 Addressables 崩溃。
/// </summary>
public sealed class CustomCardDefinition
{
    /// <summary>支持的所有自定义卡牌 Type 白名单。</summary>
    public static readonly IReadOnlySet<string> SupportedCardTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Item", "Skill", "EventEncounter", "EncounterStep", "PedestalEncounter", "CombatEncounter"
    };

    /// <summary>自定义唯一模板 ID（建议 &lt;mod-id&gt;:&lt;card-slug&gt; 或独立 GUID）。</summary>
    [JsonPropertyName("customId")]
    public string CustomId { get; set; } = string.Empty;

    /// <summary>用于网络协议下发与客户端 3D 载体渲染的原版白名单模板 GUID（必须存在于白名单且同 Type、Item 同 Size）。</summary>
    [JsonPropertyName("baseVisualTemplateId")]
    public string BaseVisualTemplateId { get; set; } = string.Empty;

    /// <summary>卡牌类型：Item | Skill | EventEncounter | EncounterStep | PedestalEncounter | CombatEncounter。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Item";

    /// <summary>尺寸：Small | Medium | Large（Item 必检与载体一致）。</summary>
    [JsonPropertyName("size")]
    public string Size { get; set; } = "Medium";

    [JsonPropertyName("startingTier")]
    public string StartingTier { get; set; } = "Bronze";

    [JsonPropertyName("spawningEligibility")]
    public string SpawningEligibility { get; set; } = "Always";

    [JsonPropertyName("heroes")]
    public List<string> Heroes { get; set; } = new();

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("hiddenTags")]
    public List<string> HiddenTags { get; set; } = new();

    [JsonPropertyName("titleZh")]
    public string TitleZh { get; set; } = string.Empty;

    /// <summary>兼容别名 nameZh（例如示例 Mod 的 cards/custom_cards.json）。</summary>
    [JsonPropertyName("nameZh")]
    public string? NameZhAlias
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(TitleZh) && !string.IsNullOrWhiteSpace(value))
                TitleZh = value.Trim();
        }
    }

    [JsonPropertyName("titleEn")]
    public string? TitleEn { get; set; }

    /// <summary>兼容别名 internalName。</summary>
    [JsonPropertyName("internalName")]
    public string? InternalNameAlias
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(TitleEn) && !string.IsNullOrWhiteSpace(value))
                TitleEn = value.Trim();
        }
    }

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("descEn")]
    public string? DescEn { get; set; }

    [JsonPropertyName("activeTooltipsZh")]
    public List<string> ActiveTooltipsZh { get; set; } = new();

    [JsonPropertyName("passiveTooltipsZh")]
    public List<string> PassiveTooltipsZh { get; set; } = new();

    [JsonPropertyName("enchantTooltipsZh")]
    public Dictionary<string, List<string>> EnchantTooltipsZh { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mod 目录内相对贴图路径（如 assets/ember_anvil.png）。</summary>
    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    /// <summary>兼容嵌套对象 customArt（自动回填 CustomArtFile 与 ArtRect）。</summary>
    [JsonPropertyName("customArt")]
    public CustomCardArtSpec? CustomArt
    {
        get => null;
        set
        {
            if (value == null) return;
            if (string.IsNullOrWhiteSpace(CustomArtFile) && !string.IsNullOrWhiteSpace(value.LogicalPath))
                CustomArtFile = value.LogicalPath.Trim();
            if (ArtRect == null && (value.Width > 0 || value.Height > 0))
            {
                ArtRect = new CustomCardArtRect
                {
                    Width = value.Width > 0 ? value.Width : 512,
                    Height = value.Height > 0 ? value.Height : 512,
                    CropMode = string.IsNullOrWhiteSpace(value.CropMode) ? "cover" : value.CropMode
                };
            }
        }
    }

    [JsonPropertyName("artRect")]
    public CustomCardArtRect? ArtRect { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonPropertyName("attributes")]
    public Dictionary<string, double>? Attributes { get; set; }

    [JsonPropertyName("tiers")]
    public Dictionary<string, TierData>? Tiers { get; set; }

    [JsonPropertyName("enchantments")]
    public Dictionary<string, EnchantmentData>? Enchantments { get; set; }

    [JsonPropertyName("abilities")]
    public Dictionary<string, AbilityDefinition> Abilities { get; set; } = new();

    [JsonPropertyName("auras")]
    public Dictionary<string, AuraDefinition> Auras { get; set; } = new();

    [JsonPropertyName("quests")]
    public List<QuestDefinition>? Quests { get; set; }

    [JsonPropertyName("selectionContext")]
    public SelectionContextData? SelectionContext { get; set; }

    [JsonPropertyName("selectionRequirements")]
    public RuleNode? SelectionRequirements { get; set; }

    [JsonPropertyName("selectionCriteria")]
    public RuleNode? SelectionCriteria { get; set; }

    [JsonPropertyName("nextEncounterOnSelection")]
    public List<NextEncounterOption>? NextEncounterOnSelection { get; set; }

    /// <summary>兼容奇遇步骤单目标跳转别名 nextEventId（自动并入 NextEncounterOnSelection 参与环检测）。</summary>
    [JsonPropertyName("nextEventId")]
    public string? NextEventId
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            NextEncounterOnSelection ??= new List<NextEncounterOption>();
            string target = value.Trim();
            if (!NextEncounterOnSelection.Any(x => string.Equals(x.Id, target, StringComparison.OrdinalIgnoreCase)))
                NextEncounterOnSelection.Add(new NextEncounterOption { Id = target, Weight = 1.0 });
        }
    }

    [JsonPropertyName("combatantType")]
    public RuleNode? CombatantType { get; set; }

    [JsonPropertyName("sandstormEnabled")]
    public bool? SandstormEnabled { get; set; }

    [JsonPropertyName("rewardCombatGold")]
    public int? RewardCombatGold { get; set; }

    [JsonPropertyName("rewardCombatXp")]
    public int? RewardCombatXp { get; set; }

    [JsonPropertyName("rewardVictory")]
    public CombatRewardData? RewardVictory { get; set; }

    [JsonPropertyName("rewardDefeat")]
    public CombatRewardData? RewardDefeat { get; set; }

    /// <summary>用于 Gate #1 静态检测非引擎废字段（如奇遇步骤中虚构的 effects / costGold）。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>
    /// 标准化四阶能力与光环绑定：若已声明 <see cref="Tiers"/> 且所有阶均未显式列出 abilityIds/auraIds，
    /// 则自动为各阶补齐卡模板声明的全部 Abilities/Auras Key，彻底根治自定义卡战斗 0 触发。
    /// </summary>
    public bool NormalizeTierBindings()
    {
        if (Tiers == null || Tiers.Count == 0)
            return false;

        bool changed = false;
        if (Abilities.Count > 0 && !Tiers.Values.Any(t => t.AbilityIds.Count > 0))
        {
            var allAbilityKeys = Abilities.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            foreach (var td in Tiers.Values)
            {
                td.AbilityIds.AddRange(allAbilityKeys);
            }
            changed = true;
        }
        if (Auras.Count > 0 && !Tiers.Values.Any(t => t.AuraIds.Count > 0))
        {
            var allAuraKeys = Auras.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            foreach (var td in Tiers.Values)
            {
                td.AuraIds.AddRange(allAuraKeys);
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// 结合已校验的原版载体模板转换为运行时可由 CombatSimulator / SpawnQueryEngine 直接执行的标准 <see cref="CardTemplate"/>。
    /// </summary>
    public CardTemplate ToCardTemplate(CardTemplate baseVisualTemplate, string modId)
    {
        var titleText = !string.IsNullOrWhiteSpace(TitleZh) ? TitleZh : (TitleEn ?? CustomId);
        var descText = !string.IsNullOrWhiteSpace(DescZh) ? DescZh : (DescEn ?? string.Empty);

        var tooltips = new List<TooltipEntry>();
        foreach (var tip in ActiveTooltipsZh)
        {
            tooltips.Add(new TooltipEntry
            {
                TooltipType = "Active",
                Content = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(tip), Text = tip }
            });
        }
        foreach (var tip in PassiveTooltipsZh)
        {
            tooltips.Add(new TooltipEntry
            {
                TooltipType = "Passive",
                Content = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(tip), Text = tip }
            });
        }

        Dictionary<string, TierData>? clonedTiers = null;
        if (Tiers != null)
        {
            bool autoFillAbilities = Abilities.Count > 0 && !Tiers.Values.Any(t => t.AbilityIds.Count > 0);
            bool autoFillAuras = Auras.Count > 0 && !Tiers.Values.Any(t => t.AuraIds.Count > 0);
            var allAbilityKeys = autoFillAbilities ? Abilities.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : null;
            var allAuraKeys = autoFillAuras ? Auras.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : null;

            clonedTiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tk, tv) in Tiers)
            {
                clonedTiers[tk] = new TierData
                {
                    Attributes = new Dictionary<string, double>(tv.Attributes, StringComparer.OrdinalIgnoreCase),
                    AbilityIds = autoFillAbilities ? new List<string>(allAbilityKeys!) : new List<string>(tv.AbilityIds),
                    AuraIds = autoFillAuras ? new List<string>(allAuraKeys!) : new List<string>(tv.AuraIds),
                    TooltipIds = new List<int>(tv.TooltipIds)
                };
            }
        }

        return new CardTemplate
        {
            TypeDiscriminator = MapCardTypeToDiscriminator(Type),
            Id = CustomId,
            Version = $"mod:{modId}",
            Type = Type,
            InternalName = !string.IsNullOrWhiteSpace(TitleEn) ? TitleEn! : titleText,
            InternalDescription = descText,
            StartingTier = StartingTier,
            Size = Size,
            Heroes = Heroes.Count > 0 ? new List<string>(Heroes) : [HeroNames.Common],
            Tags = new List<string>(Tags),
            HiddenTags = new List<string>(HiddenTags),
            ArtKey = baseVisualTemplate.ArtKey,
            AudioKey = baseVisualTemplate.AudioKey,
            SpawningEligibility = SpawningEligibility,
            TranslationKey = OverlayLocalizationStore.ComputeOfficialMd5Hash(titleText),
            Localization = new LocalizationBlock
            {
                Title = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(titleText), Text = titleText },
                Description = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(descText), Text = descText },
                Tooltips = tooltips
            },
            Attributes = Attributes != null ? new Dictionary<string, double>(Attributes, StringComparer.OrdinalIgnoreCase) : null,
            Tiers = clonedTiers,
            Enchantments = Enchantments != null ? new Dictionary<string, EnchantmentData>(Enchantments, StringComparer.OrdinalIgnoreCase) : null,
            Abilities = new Dictionary<string, AbilityDefinition>(Abilities),
            Auras = new Dictionary<string, AuraDefinition>(Auras),
            Quests = Quests != null ? new List<QuestDefinition>(Quests) : null,
            SelectionContext = SelectionContext,
            SelectionRequirements = SelectionRequirements,
            SelectionCriteria = SelectionCriteria,
            NextEncounterOnSelection = NextEncounterOnSelection != null ? new List<NextEncounterOption>(NextEncounterOnSelection) : null,
            CombatantType = CombatantType,
            SandstormEnabled = SandstormEnabled,
            RewardCombatGold = RewardCombatGold,
            RewardCombatXp = RewardCombatXp,
            RewardVictory = RewardVictory,
            RewardDefeat = RewardDefeat
        };
    }

    private static string MapCardTypeToDiscriminator(string cardType) => cardType switch
    {
        "Item" => "TCardItem",
        "Skill" => "TCardSkill",
        "EventEncounter" => "TCardEncounterEvent",
        "EncounterStep" => "TCardEncounterStep",
        "PedestalEncounter" => "TCardEncounterPedestal",
        "CombatEncounter" => "TCardEncounterCombat",
        _ => "TCardItem"
    };
}

/// <summary>
/// 自定义奇遇事件、奇遇步骤与自定义野怪定义（对应 mods/&lt;mod-id&gt;/events/*.json）。
/// </summary>
public sealed class CustomEventDefinition
{
    [JsonPropertyName("eventId")]
    public string EventId { get; set; } = string.Empty;

    /// <summary>兼容别名 customId（例如示例 Mod 的 events/custom_events.json）。</summary>
    [JsonPropertyName("customId")]
    public string? CustomIdAlias
    {
        get => null;
        set
        {
            if (string.IsNullOrWhiteSpace(EventId) && !string.IsNullOrWhiteSpace(value))
                EventId = value.Trim();
        }
    }

    [JsonPropertyName("eventType")]
    public string EventType { get; set; } = "EventEncounter"; // EventEncounter | EncounterStep | PedestalEncounter | CombatEncounter

    /// <summary>兼容别名 type。</summary>
    [JsonPropertyName("type")]
    public string? TypeAlias
    {
        get => null;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                EventType = value.Trim();
        }
    }

    [JsonPropertyName("baseVisualTemplateId")]
    public string BaseVisualTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("titleZh")]
    public string TitleZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("heroes")]
    public List<string> Heroes { get; set; } = new();

    [JsonPropertyName("minDay")]
    public int MinDay { get; set; } = 1;

    [JsonPropertyName("maxDay")]
    public int MaxDay { get; set; } = 99;

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1.0;

    /// <summary>兼容嵌套调度配置 schedule（自动同步 MinDay/MaxDay/Weight）。</summary>
    [JsonPropertyName("schedule")]
    public CustomEventSchedule? Schedule
    {
        get => null;
        set
        {
            if (value == null) return;
            if (value.MinDay.HasValue) MinDay = value.MinDay.Value;
            if (value.MaxDay.HasValue) MaxDay = value.MaxDay.Value;
            if (value.Weight.HasValue) Weight = value.Weight.Value;
        }
    }

    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    /// <summary>该事件直接关联的子步骤卡牌列表（可自动注册为 EncounterStep 模板）。</summary>
    [JsonPropertyName("steps")]
    public List<CustomCardDefinition> Steps { get; set; } = new();

    /// <summary>可选后续遭遇跳转 ID 列表（校验器将做 DFS 环检测防止死循环）。</summary>
    [JsonPropertyName("nextEncounterIds")]
    public List<string> NextEncounterIds { get; set; } = new();

    /// <summary>当事件为 CombatEncounter 时可选内联的自定义野怪模板。</summary>
    [JsonPropertyName("combatMonster")]
    public MonsterTemplate? CombatMonster { get; set; }

    /// <summary>用于 Gate #1 静态检测事件对象上的非引擎废字段。</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>转换为标准事件/战斗遭遇卡牌定义供统一校验与注入仓库。</summary>
    public CustomCardDefinition ToCardDefinition()
    {
        var nextOptions = new List<NextEncounterOption>();
        foreach (var nid in NextEncounterIds)
        {
            if (!string.IsNullOrWhiteSpace(nid))
                nextOptions.Add(new NextEncounterOption { Id = nid.Trim(), Weight = 1.0 });
        }

        return new CustomCardDefinition
        {
            CustomId = EventId,
            BaseVisualTemplateId = BaseVisualTemplateId,
            Type = EventType,
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            Heroes = Heroes.Count > 0 ? new List<string>(Heroes) : [HeroNames.Common],
            TitleZh = TitleZh,
            DescZh = DescZh,
            CustomArtFile = CustomArtFile,
            NextEncounterOnSelection = nextOptions.Count > 0 ? nextOptions : null,
            CombatantType = CombatMonster != null
                ? new RuleNode { Type = "TCombatantMonster", MonsterTemplateId = CombatMonster.Id }
                : null
        };
    }
}

/// <summary>
/// 自定义玩法模式定义（对应 mods/&lt;mod-id&gt;/modes/*.json）。
/// </summary>
public sealed class CustomModeDefinition
{
    [JsonPropertyName("modeId")]
    public string ModeId { get; set; } = string.Empty;

    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("basePreset")]
    public EGameModePreset BasePreset { get; set; } = EGameModePreset.PureOfficial;

    [JsonPropertyName("baseSpecialMode")]
    public ESpecialModeId BaseSpecialMode { get; set; } = ESpecialModeId.Standard;

    [JsonPropertyName("mutatorIds")]
    public List<string> MutatorIds { get; set; } = new();

    [JsonPropertyName("ruleOverrides")]
    public CustomRunRuleSet? RuleOverrides { get; set; }

    [JsonPropertyName("affectsGhostPool")]
    public bool AffectsGhostPool { get; set; } = false;
}

/// <summary>
/// 实例级自定义卡牌视觉绑定（对应服务端侧信道 visual-bindings API，锚定 InstanceId / CustomTemplateId 防客户端白块）。
/// </summary>
public sealed class CustomCardVisualBinding
{
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    [JsonPropertyName("customId")]
    public string CustomTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("baseVisualTemplateId")]
    public string BaseVisualTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("cardType")]
    public string CardType { get; set; } = "Item";

    [JsonPropertyName("size")]
    public string Size { get; set; } = "Medium";

    [JsonPropertyName("titleZh")]
    public string TitleZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("activeTooltipsZh")]
    public List<string> ActiveTooltipsZh { get; set; } = new();

    [JsonPropertyName("passiveTooltipsZh")]
    public List<string> PassiveTooltipsZh { get; set; } = new();

    [JsonPropertyName("enchantTooltipsZh")]
    public Dictionary<string, List<string>> EnchantTooltipsZh { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    [JsonPropertyName("customArtSha256")]
    public string? CustomArtSha256 { get; set; }

    [JsonPropertyName("artRect")]
    public CustomCardArtRect? ArtRect { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    /// <summary>基于模板级绑定派生绑定到特定卡牌 InstanceId 的副本。</summary>
    public CustomCardVisualBinding BindToInstance(string instanceId) => new()
    {
        InstanceId = instanceId,
        CustomTemplateId = CustomTemplateId,
        ModId = ModId,
        BaseVisualTemplateId = BaseVisualTemplateId,
        CardType = CardType,
        Size = Size,
        TitleZh = TitleZh,
        DescZh = DescZh,
        ActiveTooltipsZh = new List<string>(ActiveTooltipsZh),
        PassiveTooltipsZh = new List<string>(PassiveTooltipsZh),
        EnchantTooltipsZh = EnchantTooltipsZh.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase),
        CustomArtFile = CustomArtFile,
        CustomArtSha256 = CustomArtSha256,
        ArtRect = ArtRect != null ? new CustomCardArtRect { Width = ArtRect.Width, Height = ArtRect.Height, CropMode = ArtRect.CropMode } : null,
        Tags = new List<string>(Tags),
        AccentColor = AccentColor
    };
}

/// <summary>
/// Mod 内部客户端视觉资产元数据（仅允许经魔数与尺寸校验的 png/webp 图片，按 SHA-256 内容寻址）。
/// </summary>
public sealed class ModAssetEntry
{
    public string ModId { get; init; } = string.Empty;
    public string LogicalPath { get; init; } = string.Empty;
    public string FullFilePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public long Bytes { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string ContentType { get; init; } = "image/png";
}

/// <summary>
/// 原位魔改原版卡牌补丁契约（V13.1 新增，对应 mods/&lt;mod-id&gt;/patches/*.json）：
/// 仅覆写非 null 的差量字段（四阶数值、冷却、品质、标签、附魔、技能、中文名/描述与卡图），
/// 绝不破坏基础只读库，卸载或回滚 Mod 时自动 100% 复原。
/// </summary>
public sealed class CardPatchDefinition
{
    private static readonly JsonSerializerOptions CloneOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>目标原版卡牌模板 GUID（必须存在于 GameData.db）。</summary>
    [JsonPropertyName("targetTemplateId")]
    public string TargetTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("startingTier")]
    public string? StartingTier { get; set; }

    [JsonPropertyName("spawningEligibility")]
    public string? SpawningEligibility { get; set; }

    [JsonPropertyName("heroes")]
    public List<string>? Heroes { get; set; }

    [JsonPropertyName("addTags")]
    public List<string> AddTags { get; set; } = new();

    [JsonPropertyName("removeTags")]
    public List<string> RemoveTags { get; set; } = new();

    [JsonPropertyName("addHiddenTags")]
    public List<string> AddHiddenTags { get; set; } = new();

    [JsonPropertyName("removeHiddenTags")]
    public List<string> RemoveHiddenTags { get; set; } = new();

    [JsonPropertyName("attributeOverrides")]
    public Dictionary<string, double>? AttributeOverrides { get; set; }

    [JsonPropertyName("tierAttributeOverrides")]
    public Dictionary<string, Dictionary<string, double>>? TierAttributeOverrides { get; set; }

    [JsonPropertyName("enchantmentOverrides")]
    public Dictionary<string, EnchantmentData>? EnchantmentOverrides { get; set; }

    [JsonPropertyName("abilitiesOverride")]
    public Dictionary<string, AbilityDefinition>? AbilitiesOverride { get; set; }

    [JsonPropertyName("aurasOverride")]
    public Dictionary<string, AuraDefinition>? AurasOverride { get; set; }

    [JsonPropertyName("titleZh")]
    public string? TitleZh { get; set; }

    [JsonPropertyName("titleEn")]
    public string? TitleEn { get; set; }

    [JsonPropertyName("descZh")]
    public string? DescZh { get; set; }

    [JsonPropertyName("descEn")]
    public string? DescEn { get; set; }

    [JsonPropertyName("activeTooltipsZh")]
    public List<string>? ActiveTooltipsZh { get; set; }

    [JsonPropertyName("passiveTooltipsZh")]
    public List<string>? PassiveTooltipsZh { get; set; }

    [JsonPropertyName("enchantTooltipsZh")]
    public Dictionary<string, List<string>>? EnchantTooltipsZh { get; set; }

    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    [JsonPropertyName("artRect")]
    public CustomCardArtRect? ArtRect { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>深拷贝原版模板并增量合并补丁差量，产出全新不可变覆写模板（不修改原版实例）。</summary>
    public CardTemplate ApplyToTemplate(CardTemplate sourceCard, string modId)
    {
        // 通过 JSON 往返深拷贝确保 Tier/Ability/Aura/Enchantment 零引用共享污染
        string raw = JsonSerializer.Serialize(sourceCard, CloneOptions);
        var cloned = JsonSerializer.Deserialize<CardTemplate>(raw, CloneOptions)
            ?? throw new InvalidOperationException($"无法深拷贝目标原版卡牌模板 '{sourceCard.Id}'。");

        cloned.Version = $"patch:{modId}";
        if (!string.IsNullOrWhiteSpace(StartingTier))
            cloned.StartingTier = StartingTier.Trim();
        if (!string.IsNullOrWhiteSpace(SpawningEligibility))
            cloned.SpawningEligibility = SpawningEligibility.Trim();
        if (Heroes is { Count: > 0 })
            cloned.Heroes = new List<string>(Heroes);

        if (AddTags.Count > 0 || RemoveTags.Count > 0)
        {
            var tagSet = new List<string>(cloned.Tags);
            foreach (var r in RemoveTags)
                tagSet.RemoveAll(t => string.Equals(t, r, StringComparison.OrdinalIgnoreCase));
            foreach (var a in AddTags)
            {
                if (!string.IsNullOrWhiteSpace(a) && !tagSet.Any(t => string.Equals(t, a, StringComparison.OrdinalIgnoreCase)))
                    tagSet.Add(a.Trim());
            }
            cloned.Tags = tagSet;
        }

        if (AddHiddenTags.Count > 0 || RemoveHiddenTags.Count > 0)
        {
            var hSet = new List<string>(cloned.HiddenTags);
            foreach (var r in RemoveHiddenTags)
                hSet.RemoveAll(t => string.Equals(t, r, StringComparison.OrdinalIgnoreCase));
            foreach (var a in AddHiddenTags)
            {
                if (!string.IsNullOrWhiteSpace(a) && !hSet.Any(t => string.Equals(t, a, StringComparison.OrdinalIgnoreCase)))
                    hSet.Add(a.Trim());
            }
            cloned.HiddenTags = hSet;
        }

        if (AttributeOverrides is { Count: > 0 })
        {
            cloned.Attributes ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in AttributeOverrides)
                cloned.Attributes[k] = v;
        }

        if (TierAttributeOverrides is { Count: > 0 })
        {
            cloned.Tiers ??= new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tierName, attrDelta) in TierAttributeOverrides)
            {
                if (attrDelta == null) continue;
                var existingKey = cloned.Tiers.Keys.FirstOrDefault(k => string.Equals(k, tierName, StringComparison.OrdinalIgnoreCase));
                if (existingKey == null)
                {
                    var newTier = new TierData
                    {
                        Attributes = new Dictionary<string, double>(attrDelta, StringComparer.OrdinalIgnoreCase)
                    };
                    // 继承相邻已有品阶的 AbilityIds / AuraIds / TooltipIds，防止新开阶 0 技能
                    var donor = cloned.Tiers.Values.FirstOrDefault();
                    if (donor != null)
                    {
                        newTier.AbilityIds.AddRange(donor.AbilityIds);
                        newTier.AuraIds.AddRange(donor.AuraIds);
                        newTier.TooltipIds.AddRange(donor.TooltipIds);
                    }
                    cloned.Tiers[tierName] = newTier;
                }
                else
                {
                    var td = cloned.Tiers[existingKey];
                    foreach (var (ak, av) in attrDelta)
                        td.Attributes[ak] = av;
                }
            }
        }

        if (EnchantmentOverrides is { Count: > 0 })
        {
            cloned.Enchantments ??= new Dictionary<string, EnchantmentData>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ek, ev) in EnchantmentOverrides)
            {
                if (ev != null)
                    cloned.Enchantments[ek] = ev;
            }
        }

        if (AbilitiesOverride is { Count: > 0 })
        {
            cloned.Abilities = new Dictionary<string, AbilityDefinition>(AbilitiesOverride);
            if (cloned.Tiers != null && cloned.Tiers.Count > 0)
            {
                var allKeys = cloned.Abilities.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var td in cloned.Tiers.Values)
                {
                    td.AbilityIds.Clear();
                    td.AbilityIds.AddRange(allKeys);
                }
            }
        }

        if (AurasOverride is { Count: > 0 })
        {
            cloned.Auras = new Dictionary<string, AuraDefinition>(AurasOverride);
            if (cloned.Tiers != null && cloned.Tiers.Count > 0)
            {
                var allKeys = cloned.Auras.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                foreach (var td in cloned.Tiers.Values)
                {
                    td.AuraIds.Clear();
                    td.AuraIds.AddRange(allKeys);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(TitleEn))
            cloned.InternalName = TitleEn.Trim();

        if (!string.IsNullOrWhiteSpace(TitleZh) || !string.IsNullOrWhiteSpace(DescZh)
            || ActiveTooltipsZh != null || PassiveTooltipsZh != null)
        {
            cloned.Localization ??= new LocalizationBlock();
            if (!string.IsNullOrWhiteSpace(TitleZh))
            {
                cloned.Localization.Title = new LocalizedText
                {
                    Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(TitleZh),
                    Text = TitleZh
                };
            }
            if (!string.IsNullOrWhiteSpace(DescZh))
            {
                cloned.InternalDescription = DescZh;
                cloned.Localization.Description = new LocalizedText
                {
                    Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(DescZh),
                    Text = DescZh
                };
            }
            if (ActiveTooltipsZh != null || PassiveTooltipsZh != null)
            {
                var tips = new List<TooltipEntry>();
                if (ActiveTooltipsZh != null)
                {
                    foreach (var tip in ActiveTooltipsZh)
                    {
                        tips.Add(new TooltipEntry
                        {
                            TooltipType = "Active",
                            Content = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(tip), Text = tip }
                        });
                    }
                }
                if (PassiveTooltipsZh != null)
                {
                    foreach (var tip in PassiveTooltipsZh)
                    {
                        tips.Add(new TooltipEntry
                        {
                            TooltipType = "Passive",
                            Content = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(tip), Text = tip }
                        });
                    }
                }
                cloned.Localization.Tooltips = tips;
                if (cloned.Tiers != null)
                {
                    foreach (var td in cloned.Tiers.Values)
                        td.TooltipIds.Clear();
                }
            }
        }

        return cloned;
    }

    public bool HasVisualOrTextOverride =>
        !string.IsNullOrWhiteSpace(TitleZh)
        || !string.IsNullOrWhiteSpace(DescZh)
        || !string.IsNullOrWhiteSpace(CustomArtFile)
        || (ActiveTooltipsZh is { Count: > 0 })
        || (PassiveTooltipsZh is { Count: > 0 })
        || (EnchantTooltipsZh is { Count: > 0 });
}

/// <summary>
/// 单个已解析的 Mod 完整内容包。
/// </summary>
public sealed class ModPackage
{
    public required ModManifest Manifest { get; init; }
    public required string RootDirectory { get; init; }
    public List<CustomCardDefinition> Cards { get; } = new();
    public List<CardPatchDefinition> Patches { get; } = new();
    public List<CustomEventDefinition> Events { get; } = new();
    public List<MonsterTemplate> Monsters { get; } = new();
    public List<CustomModeDefinition> Modes { get; } = new();
    public LocalizationOverlayDocument? LocalizationOverlay { get; set; }
    public Dictionary<string, ModAssetEntry> AssetsByLogicalPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>由 assets/art_manifest.json 读取的逻辑路径 -> 预期 SHA-256，用于 Gate #8 完整性比对。</summary>
    public Dictionary<string, string> ExpectedAssetSha256ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CustomCardVisualBinding> VisualBindingsByCustomId { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string PackageHash { get; set; } = string.Empty;
}

/// <summary>校验告警严重度。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModValidationSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>单条 Mod 校验问题记录（含所属闸门编号 1~10 与精准中文修复建议）。</summary>
public sealed record ModValidationIssue(
    ModValidationSeverity Severity,
    string ModId,
    string Code,
    string Message,
    string? TargetId = null,
    int GateId = 0,
    string? FixHintZh = null);

/// <summary>单道防 Bug 铁闸执行结果 DTO。</summary>
public sealed class ModGateResult
{
    [JsonPropertyName("gateId")]
    public int GateId { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    /// <summary>PASS | WARN | FAIL | SKIP。</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "PASS";

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("issues")]
    public List<ModValidationIssue> Issues { get; set; } = new();
}

/// <summary>闸门 #9：50 场真实 CombatSimulator 沙盒压力体检报告 DTO。</summary>
public sealed class ModCombatStressReport
{
    [JsonPropertyName("matchesRun")]
    public int MatchesRun { get; set; }

    [JsonPropertyName("seedBase")]
    public int SeedBase { get; set; }

    [JsonPropertyName("exceptions")]
    public int Exceptions { get; set; }

    [JsonPropertyName("maxDurationHits")]
    public int MaxDurationHits { get; set; }

    [JsonPropertyName("cascadeGuardTrips")]
    public int CascadeGuardTrips { get; set; }

    [JsonPropertyName("numericCorruptions")]
    public int NumericCorruptions { get; set; }

    [JsonPropertyName("slowMatchesOver200ms")]
    public int SlowMatchesOver200ms { get; set; }

    [JsonPropertyName("avgMatchMs")]
    public double AvgMatchMs { get; set; }

    [JsonPropertyName("p95MatchMs")]
    public double P95MatchMs { get; set; }

    [JsonPropertyName("maxMatchMs")]
    public double MaxMatchMs { get; set; }

    [JsonPropertyName("modSideWins")]
    public int ModSideWins { get; set; }

    [JsonPropertyName("modSideWinRatePct")]
    public double ModSideWinRatePct { get; set; }

    [JsonPropertyName("avgFightDurationSec")]
    public double AvgFightDurationSec { get; set; }

    [JsonPropertyName("testedCardIds")]
    public List<string> TestedCardIds { get; set; } = new();

    [JsonPropertyName("balanceHintZh")]
    public string BalanceHintZh { get; set; } = string.Empty;
}

/// <summary>V13.1 工业级 10 道防 Bug 铁闸完整诊断报告 DTO。</summary>
public sealed class ModTenGateValidationReport
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; } = true;

    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("validatedAtUtc")]
    public DateTimeOffset ValidatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("totalDurationMs")]
    public long TotalDurationMs { get; set; }

    [JsonPropertyName("errorCount")]
    public int ErrorCount { get; set; }

    [JsonPropertyName("warningCount")]
    public int WarningCount { get; set; }

    [JsonPropertyName("summaryZh")]
    public string SummaryZh { get; set; } = string.Empty;

    [JsonPropertyName("gates")]
    public List<ModGateResult> Gates { get; set; } = new();

    [JsonPropertyName("combatStressReport")]
    public ModCombatStressReport? CombatStressReport { get; set; }
}

/// <summary>
/// 一次全量 Mod 扫描、校验与加载的详细报告。
/// </summary>
public sealed class ModLoadReport
{
    public DateTimeOffset LoadedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ModsRootDirectory { get; init; } = string.Empty;
    public List<string> LoadedModIdsInOrder { get; } = new();
    public List<string> RejectedModIds { get; } = new();
    public List<ModValidationIssue> Issues { get; } = new();
    public int TotalCustomCards { get; set; }
    public int TotalCardPatches { get; set; }
    public int TotalCustomEvents { get; set; }
    public int TotalCustomMonsters { get; set; }
    public int TotalCustomModes { get; set; }
    public int TotalAssets { get; set; }
    public string ModSetHash { get; set; } = string.Empty;
    public bool ForcesGhostPoolIsolation { get; set; }

    public bool HasErrors => Issues.Any(i => i.Severity == ModValidationSeverity.Error);
    public bool Succeeded => !HasErrors;
}

/// <summary>
/// 供服务端 /api/mods/manifest 序列化下发给 BazaarLauncher 与 BazaarHub 的标准 Mod 同步清单 DTO。
/// </summary>
public sealed class ModManifestDto
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("manifestHash")]
    public string ManifestHash { get; set; } = string.Empty;

    [JsonPropertyName("generatedAtUtc")]
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("forcesGhostPoolIsolation")]
    public bool ForcesGhostPoolIsolation { get; set; }

    [JsonPropertyName("localizationHash")]
    public string LocalizationHash { get; set; } = string.Empty;

    [JsonPropertyName("visualCatalogHash")]
    public string VisualCatalogHash { get; set; } = string.Empty;

    [JsonPropertyName("mods")]
    public List<ModEntryDto> Mods { get; set; } = new();

    [JsonPropertyName("assets")]
    public List<ModAssetDto> Assets { get; set; } = new();
}

/// <summary>单个 Mod 的客户端同步摘要条目。</summary>
public sealed class ModEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    [JsonPropertyName("packageHash")]
    public string PackageHash { get; set; } = string.Empty;

    [JsonPropertyName("customCardCount")]
    public int CustomCardCount { get; set; }

    [JsonPropertyName("customModeCount")]
    public int CustomModeCount { get; set; }

    [JsonPropertyName("assetCount")]
    public int AssetCount { get; set; }
}

/// <summary>单个客户端资产的 SHA-256 增量寻址条目。</summary>
public sealed class ModAssetDto
{
    [JsonPropertyName("modId")]
    public string ModId { get; set; } = string.Empty;

    [JsonPropertyName("logicalPath")]
    public string LogicalPath { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("bytes")]
    public long Bytes { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("contentType")]
    public string ContentType { get; set; } = "image/png";
}

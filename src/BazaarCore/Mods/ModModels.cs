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

    [JsonPropertyName("mechanics")]
    public string Mechanics { get; set; } = "mechanics/*.json";

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

    [JsonPropertyName("difficultySlot")]
    public string? DifficultySlot { get; set; }
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

    [JsonPropertyName("activeTooltipsEn")]
    public List<string>? ActiveTooltipsEn { get; set; }

    [JsonPropertyName("passiveTooltipsEn")]
    public List<string>? PassiveTooltipsEn { get; set; }

    [JsonPropertyName("enchantTooltipsEn")]
    public Dictionary<string, List<string>>? EnchantTooltipsEn { get; set; }

    /// <summary>Mod 目录内相对贴图路径（如 assets/ember_anvil.png）。</summary>
    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    /// <summary>D3-P1-12：可选奇遇/遭遇独立背景图路径（如 assets/event_bg.png）。</summary>
    [JsonPropertyName("customBgArtFile")]
    public string? CustomBgArtFile { get; set; }

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

    /// <summary>P1-16：是否显式禁用默认标准附魔兼容集自动合成（true 时仅保留作者在 enchantments 中手写的附魔）。</summary>
    [JsonPropertyName("disableAutoEnchantments")]
    public bool DisableAutoEnchantments { get; set; }

    /// <summary>P1-16：显式排除的标准附魔名称列表（如 ["Icy", "Toxic"]，大小写不敏感；无论是否自动合成均从有效附魔集中剔除）。</summary>
    [JsonPropertyName("disabledEnchantments")]
    public List<string>? DisabledEnchantments { get; set; }

    [JsonPropertyName("abilities")]
    public Dictionary<string, AbilityDefinition> Abilities { get; set; } = new();

    [JsonPropertyName("auras")]
    public Dictionary<string, AuraDefinition> Auras { get; set; } = new();

    [JsonPropertyName("quests")]
    public List<QuestDefinition>? Quests { get; set; }

    /// <summary>V14 挂载到本卡牌的开放自定义沙盒机制列表。</summary>
    [JsonPropertyName("customMechanics")]
    public List<CardMechanicAttachment>? CustomMechanics { get; set; }

    /// <summary>可选：卡牌级直配实时数值徽章列表（不依赖机制钩子即可展示任意 Custom_0..Custom_9 或原生属性）。</summary>
    [JsonPropertyName("counterDisplays")]
    public List<MechanicCounterDisplay>? CounterDisplays { get; set; }

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

    /// <summary>P1-04：Mod 自定义遭遇场景切板键（原样透传至 CardTemplate.BoardOverride）。</summary>
    [JsonPropertyName("boardOverride")]
    public string? BoardOverride { get; set; }

    /// <summary>P1-04：Mod 自定义遭遇视觉演出键（原样透传至 CardTemplate.Presentation）。</summary>
    [JsonPropertyName("presentation")]
    public string? Presentation { get; set; }

    /// <summary>Mod 自定义遭遇相对抽签权重（仅事件/遭遇使用；&lt;=0 转为 GuidOnly）。</summary>
    [JsonPropertyName("weight")]
    public double? ModSpawnWeight { get; set; }

    /// <summary>Mod 自定义遭遇最早允许出现天数。</summary>
    [JsonPropertyName("minDay")]
    public int? ModMinDay { get; set; }

    /// <summary>Mod 自定义遭遇最晚允许出现天数。</summary>
    [JsonPropertyName("maxDay")]
    public int? ModMaxDay { get; set; }

    /// <summary>P0-24：透传自定义奇遇允许出现的小时列表。</summary>
    [JsonPropertyName("allowedHours")]
    public List<int>? AllowedHours { get; set; }

    /// <summary>P0-24：透传自定义奇遇每局仅出现一次标记。</summary>
    [JsonPropertyName("isSingleSpawn")]
    public bool? IsSingleSpawn { get; set; }

    /// <summary>P0-21：透传自定义野怪遭遇难度槽位（Bronze / Silver / Gold / Any）。</summary>
    [JsonPropertyName("difficultySlot")]
    public string? ModDifficultySlot { get; set; }

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
        if (Tiers != null && Tiers.Count > 0)
        {
            bool autoFillAbilities = Abilities.Count > 0 && !Tiers.Values.Any(t => t.AbilityIds.Count > 0);
            bool autoFillAuras = Auras.Count > 0 && !Tiers.Values.Any(t => t.AuraIds.Count > 0);
            var allAbilityKeys = autoFillAbilities ? Abilities.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : null;
            var allAuraKeys = autoFillAuras ? Auras.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList() : null;

            // D3-P1-01：合并顶层 Attributes 与各阶 Tiers[tier].Attributes 作为完整底表，并从 StartingTier 起补齐至 Legendary
            clonedTiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase);
            var runningAttrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (Attributes != null)
            {
                foreach (var (k, v) in Attributes)
                    runningAttrs[k] = v;
            }
            var lastAbilityIds = allAbilityKeys != null ? new List<string>(allAbilityKeys) : new List<string>();
            var lastAuraIds = allAuraKeys != null ? new List<string>(allAuraKeys) : new List<string>();
            var lastTooltipIds = new List<int>();
            bool started = false;
            string startTierNorm = string.IsNullOrWhiteSpace(StartingTier) ? "Bronze" : StartingTier.Trim();

            foreach (var tierName in new[] { "Bronze", "Silver", "Gold", "Diamond", "Legendary" })
            {
                bool hasExplicit = Tiers.TryGetValue(tierName, out var tv) && tv != null;
                if (hasExplicit || string.Equals(tierName, startTierNorm, StringComparison.OrdinalIgnoreCase))
                    started = true;

                if (hasExplicit)
                {
                    if (tv!.Attributes != null)
                    {
                        foreach (var (k, v) in tv.Attributes)
                            runningAttrs[k] = v;
                    }
                    lastAbilityIds = autoFillAbilities ? new List<string>(allAbilityKeys!) : new List<string>(tv.AbilityIds);
                    lastAuraIds = autoFillAuras ? new List<string>(allAuraKeys!) : new List<string>(tv.AuraIds);
                    lastTooltipIds = new List<int>(tv.TooltipIds);
                }

                if (started)
                {
                    clonedTiers[tierName] = new TierData
                    {
                        Attributes = new Dictionary<string, double>(runningAttrs, StringComparer.OrdinalIgnoreCase),
                        AbilityIds = new List<string>(lastAbilityIds),
                        AuraIds = new List<string>(lastAuraIds),
                        TooltipIds = new List<int>(lastTooltipIds)
                    };
                }
            }
        }

        // P0-23：通用跨英雄自定义技能判定（Type=="Skill" 且未限定特定英雄或包含 Common/Any/All）
        bool isSkill = string.Equals(Type, "Skill", StringComparison.OrdinalIgnoreCase);
        bool isUniversalSkill = isSkill && (Heroes.Count == 0 || Heroes.Any(h =>
            string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)
            || string.Equals(h, "Any", StringComparison.OrdinalIgnoreCase)
            || string.Equals(h, "All", StringComparison.OrdinalIgnoreCase)));
        var resolvedHeroes = Heroes.Count > 0 ? new List<string>(Heroes) : new List<string> { HeroNames.Common };
        if (isUniversalSkill && !resolvedHeroes.Any(h => string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)))
            resolvedHeroes.Add(HeroNames.Common);

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
            Heroes = resolvedHeroes,
            Tags = new List<string>(Tags),
            HiddenTags = new List<string>(HiddenTags),
            ArtKey = baseVisualTemplate.ArtKey,
            AudioKey = baseVisualTemplate.AudioKey,
            SpawningEligibility = SpawningEligibility,
            IsSingleSpawn = IsSingleSpawn,
            TranslationKey = OverlayLocalizationStore.ComputeOfficialMd5Hash(titleText),
            Localization = new LocalizationBlock
            {
                Title = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(titleText), Text = titleText },
                Description = new LocalizedText { Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(descText), Text = descText },
                Tooltips = tooltips
            },
            Attributes = Attributes != null ? new Dictionary<string, double>(Attributes, StringComparer.OrdinalIgnoreCase) : null,
            Tiers = clonedTiers,
            Enchantments = ModDefaultEnchantmentSynthesizer.BuildEffectiveEnchantments(this, clonedTiers),
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
            RewardDefeat = RewardDefeat,
            BoardOverride = string.IsNullOrWhiteSpace(BoardOverride) ? null : BoardOverride.Trim(),
            Presentation = string.IsNullOrWhiteSpace(Presentation) ? null : Presentation.Trim(),
            ModSpawnWeight = ModSpawnWeight.HasValue && double.IsFinite(ModSpawnWeight.Value)
                ? Math.Max(0.0, ModSpawnWeight.Value)
                : null,
            ModMinDay = ModMinDay is >= 1 ? ModMinDay : null,
            ModMaxDay = ModMaxDay is >= 1 ? ModMaxDay : null,
            IsCustomModCard = true,
            IsUniversalCustomSkill = isUniversalSkill,
            CustomModOrigin = modId,
            AllowedHours = AllowedHours is { Count: > 0 } ? new List<int>(AllowedHours) : null,
            ModDifficultySlot = string.IsNullOrWhiteSpace(ModDifficultySlot) ? null : ModDifficultySlot.Trim(),
            CustomMechanics = CustomMechanics is { Count: > 0 }
                ? CustomMechanics.Select(m => new CardMechanicAttachment
                {
                    MechanicId = m.MechanicId,
                    Enabled = m.Enabled,
                    Params = new Dictionary<string, JsonElement>(m.Params, StringComparer.OrdinalIgnoreCase)
                }).ToList()
                : null
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

    [JsonPropertyName("titleEn")]
    public string? TitleEn { get; set; }

    [JsonPropertyName("descZh")]
    public string DescZh { get; set; } = string.Empty;

    [JsonPropertyName("descEn")]
    public string? DescEn { get; set; }

    [JsonPropertyName("heroes")]
    public List<string> Heroes { get; set; } = new();

    [JsonPropertyName("startingTier")]
    public string? StartingTier { get; set; }

    [JsonPropertyName("minDay")]
    public int MinDay { get; set; } = 1;

    [JsonPropertyName("maxDay")]
    public int MaxDay { get; set; } = 99;

    [JsonPropertyName("weight")]
    public double Weight { get; set; } = 1.0;

    /// <summary>P0-24：允许出现的小时列表（透传自 schedule.hours）。</summary>
    [JsonPropertyName("hours")]
    public List<int>? AllowedHours { get; set; }

    /// <summary>P0-24：每局仅出现一次（透传自 schedule.oncePerRun）。</summary>
    [JsonPropertyName("oncePerRun")]
    public bool OncePerRun { get; set; }

    /// <summary>P0-24：仅允许由前序事件链触发（透传自 schedule.chainOnly -> SpawningEligibility="GuidOnly"）。</summary>
    [JsonPropertyName("chainOnly")]
    public bool ChainOnly { get; set; }

    /// <summary>P0-21：自定义野怪遭遇的难度槽位（Bronze / Silver / Gold / Any）。</summary>
    [JsonPropertyName("difficultySlot")]
    public string? DifficultySlot { get; set; }

    /// <summary>兼容嵌套调度配置 schedule（P0-24 完整透传 MinDay/MaxDay/Weight/Hours/OncePerRun/ChainOnly/DifficultySlot）。</summary>
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
            if (value.Hours is { Count: > 0 }) AllowedHours = new List<int>(value.Hours);
            if (value.OncePerRun) OncePerRun = true;
            if (value.ChainOnly) ChainOnly = true;
            if (!string.IsNullOrWhiteSpace(value.DifficultySlot)) DifficultySlot = value.DifficultySlot.Trim();
        }
    }

    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    /// <summary>D3-P1-12：奇遇/遭遇独立背景图路径（如 assets/event_bg.png）。</summary>
    [JsonPropertyName("customBgArtFile")]
    public string? CustomBgArtFile { get; set; }

    [JsonPropertyName("rewardCombatGold")]
    public int? RewardCombatGold { get; set; }

    [JsonPropertyName("rewardCombatXp")]
    public int? RewardCombatXp { get; set; }

    /// <summary>该事件直接关联的子步骤卡牌列表（可自动注册为 EncounterStep 模板）。</summary>
    [JsonPropertyName("steps")]
    public List<CustomCardDefinition> Steps { get; set; } = new();

    /// <summary>可选后续遭遇跳转 ID 列表（校验器将做 DFS 环检测防止死循环）。</summary>
    [JsonPropertyName("nextEncounterIds")]
    public List<string> NextEncounterIds { get; set; } = new();

    /// <summary>当事件为 CombatEncounter 时可选内联的自定义野怪模板。</summary>
    [JsonPropertyName("combatMonster")]
    public MonsterTemplate? CombatMonster { get; set; }

    /// <summary>P1-04：可选自定义切板键（如远征/主题奇遇棋盘覆写）。</summary>
    [JsonPropertyName("boardOverride")]
    public string? BoardOverride { get; set; }

    /// <summary>P1-04：可选自定义遭遇演出键。</summary>
    [JsonPropertyName("presentation")]
    public string? Presentation { get; set; }

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

        // 工坊约定：chainOnly == true 或 weight <= 0 表示“仅允许由上一奇遇跳转进入”，自动转为 GuidOnly 且不进入常规白昼随机池
        bool isGuidOnly = ChainOnly || (double.IsFinite(Weight) && Weight <= 0.0);
        double effectiveWeight = isGuidOnly
            ? 0.0
            : (double.IsFinite(Weight) ? Weight : 1.0);

        string effectiveEventType = CombatMonster != null
            ? "CombatEncounter"
            : (!string.IsNullOrWhiteSpace(EventType) ? EventType.Trim() : "EventEncounter");

        return new CustomCardDefinition
        {
            CustomId = EventId,
            BaseVisualTemplateId = BaseVisualTemplateId,
            Type = effectiveEventType,
            Size = "Medium",
            StartingTier = !string.IsNullOrWhiteSpace(StartingTier) ? StartingTier!.Trim() : "Bronze",
            SpawningEligibility = isGuidOnly ? "GuidOnly" : "Always",
            IsSingleSpawn = OncePerRun ? true : null,
            AllowedHours = AllowedHours is { Count: > 0 } ? new List<int>(AllowedHours) : null,
            ModDifficultySlot = string.IsNullOrWhiteSpace(DifficultySlot) ? null : DifficultySlot.Trim(),
            Heroes = Heroes.Count > 0 ? new List<string>(Heroes) : [HeroNames.Common],
            TitleZh = TitleZh,
            TitleEn = TitleEn,
            DescZh = DescZh,
            DescEn = DescEn,
            CustomArtFile = CustomArtFile,
            CustomBgArtFile = CustomBgArtFile,
            RewardCombatGold = RewardCombatGold,
            RewardCombatXp = RewardCombatXp,
            BoardOverride = string.IsNullOrWhiteSpace(BoardOverride) ? null : BoardOverride.Trim(),
            Presentation = string.IsNullOrWhiteSpace(Presentation) ? null : Presentation.Trim(),
            ModSpawnWeight = effectiveWeight,
            ModMinDay = MinDay > 1 ? MinDay : null,
            ModMaxDay = MaxDay is >= 1 and < 99 ? MaxDay : null,
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
/// P0-22：自定义肉鸽突变词条 JSON 定义 DTO（1:1 映射 mods/&lt;mod-id&gt;/modes/*mutator*.json 及实样 custom_mutators.json）。
/// </summary>
public sealed class CustomMutatorDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    [JsonPropertyName("nameEn")]
    public string? NameEn { get; set; }

    [JsonPropertyName("descriptionZh")]
    public string DescriptionZh { get; set; } = string.Empty;

    [JsonPropertyName("descZh")]
    public string? DescZhAlias
    {
        get => null;
        set { if (string.IsNullOrWhiteSpace(DescriptionZh) && !string.IsNullOrWhiteSpace(value)) DescriptionZh = value.Trim(); }
    }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("conflictGroup")]
    public string? ConflictGroup { get; set; }

    [JsonPropertyName("difficultyDelta")]
    public double DifficultyDelta { get; set; }

    [JsonPropertyName("scoreMultiplierDelta")]
    public double? ScoreMultiplierDelta { get; set; }

    [JsonPropertyName("isPositive")]
    public bool? IsPositive { get; set; }

    [JsonPropertyName("targetHook")]
    public string? TargetHook { get; set; }

    [JsonPropertyName("enabledByDefault")]
    public bool EnabledByDefault { get; set; }

    [JsonPropertyName("rules")]
    public Dictionary<string, JsonElement>? Rules { get; set; }

    public MutatorMetadata ToMetadata(string sourceModId)
    {
        var rulesObj = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var numericParams = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        if (Rules != null)
        {
            foreach (var (k, el) in Rules)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                switch (el.ValueKind)
                {
                    case JsonValueKind.Number when el.TryGetDouble(out double d) && double.IsFinite(d):
                        rulesObj[k] = d;
                        numericParams[k] = d;
                        break;
                    case JsonValueKind.True:
                        rulesObj[k] = true;
                        numericParams[k] = 1.0;
                        break;
                    case JsonValueKind.False:
                        rulesObj[k] = false;
                        numericParams[k] = 0.0;
                        break;
                    case JsonValueKind.String:
                        rulesObj[k] = el.GetString();
                        break;
                    default:
                        rulesObj[k] = el.Clone();
                        break;
                }
            }
        }

        double scoreDelta = ScoreMultiplierDelta ?? (Math.Abs(DifficultyDelta) > 1e-6 ? Math.Round(DifficultyDelta * 0.15, 2) : 0.0);
        bool isPos = IsPositive ?? (DifficultyDelta <= 0);
        string targetHook = !string.IsNullOrWhiteSpace(TargetHook)
            ? TargetHook!.Trim()
            : (rulesObj.Keys.Any(k => k.StartsWith("combat", StringComparison.OrdinalIgnoreCase)
                || k.Contains("sandstorm", StringComparison.OrdinalIgnoreCase)
                || k.Contains("cooldown", StringComparison.OrdinalIgnoreCase))
                ? "CombatSimulator"
                : "RunEngine");

        return new MutatorMetadata(
            Id: Id.Trim(),
            NameZh: !string.IsNullOrWhiteSpace(NameZh) ? NameZh.Trim() : Id.Trim(),
            DescZh: DescriptionZh?.Trim() ?? string.Empty,
            ConflictGroup: string.IsNullOrWhiteSpace(ConflictGroup) ? null : ConflictGroup.Trim(),
            ScoreMultiplierDelta: double.IsFinite(scoreDelta) ? Math.Clamp(scoreDelta, -0.75, 2.0) : 0.0,
            IsPositive: isPos,
            TargetHook: targetHook,
            Parameters: numericParams.Count > 0 ? numericParams : null,
            SourceModId: sourceModId,
            NameEn: string.IsNullOrWhiteSpace(NameEn) ? null : NameEn.Trim(),
            Category: string.IsNullOrWhiteSpace(Category) ? null : Category.Trim(),
            DifficultyDelta: double.IsFinite(DifficultyDelta) ? DifficultyDelta : 0.0,
            EnabledByDefault: EnabledByDefault,
            Rules: rulesObj.Count > 0 ? rulesObj : null);
    }
}

/// <summary>
/// 已按具体卡牌参数实例解析后的通用卡面实时数值徽章元数据（由服务端下发给客户端在卡牌 3D 层级渲染晶球/进度条/宝石/徽章）。
/// </summary>
public sealed class ResolvedCounterDisplay
{
    [JsonPropertyName("labelZh")]
    public string LabelZh { get; set; } = string.Empty;

    [JsonPropertyName("syncAttribute")]
    public string SyncAttribute { get; set; } = "Custom_3";

    [JsonPropertyName("maxValue")]
    public int? MaxValue { get; set; }

    /// <summary>D3-P1-06：可选动态上限属性名。</summary>
    [JsonPropertyName("maxAttr")]
    public string? MaxAttr { get; set; }

    [JsonPropertyName("colorHex")]
    public string? ColorHex { get; set; }

    [JsonPropertyName("hideWhenZero")]
    public bool HideWhenZero { get; set; }

    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("valueType")]
    public string? ValueType { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("unit")]
    public string? Unit { get; set; }

    [JsonPropertyName("position")]
    public string? Position { get; set; }
}

/// <summary>
/// 通用附魔预览属性运算（从附魔自身的 attributes 与作用于自身的 TAuraActionCardModifyAttribute 自动提取，面向任意 Mod 卡通用）。
/// </summary>
public sealed class ResolvedPreviewAttrOp
{
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = string.Empty;

    [JsonPropertyName("operation")]
    public string Operation { get; set; } = "Set";

    [JsonPropertyName("value")]
    public double Value { get; set; }

    [JsonPropertyName("sourceAttribute")]
    public string? SourceAttribute { get; set; }

    /// <summary>D3-P1-02：基于 SourceAttribute 派生时的比例乘数（如 0.5 表示 50%）。</summary>
    [JsonPropertyName("sourceMultiplier")]
    public double? SourceMultiplier { get; set; }

    /// <summary>D3-P1-02：基于 SourceAttribute 派生时的固定加偏移。</summary>
    [JsonPropertyName("sourceAddOffset")]
    public double? SourceAddOffset { get; set; }
}

/// <summary>P0-21：自定义野怪预览结构（供客户端野怪面板 1:1 渲染血量/奖励/手牌/技能）。</summary>
public sealed class CustomMonsterPreviewBinding
{
    [JsonPropertyName("monsterId")]
    public string MonsterId { get; set; } = string.Empty;

    [JsonPropertyName("nameZh")]
    public string NameZh { get; set; } = string.Empty;

    [JsonPropertyName("nameEn")]
    public string? NameEn { get; set; }

    [JsonPropertyName("minDay")]
    public int? MinDay { get; set; }

    [JsonPropertyName("maxDay")]
    public int? MaxDay { get; set; }

    [JsonPropertyName("difficultySlot")]
    public string? DifficultySlot { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("healthMax")]
    public int HealthMax { get; set; }

    [JsonPropertyName("startingTier")]
    public string StartingTier { get; set; } = "Bronze";

    [JsonPropertyName("rewardGold")]
    public int RewardGold { get; set; }

    [JsonPropertyName("rewardXp")]
    public int RewardXp { get; set; }

    [JsonPropertyName("items")]
    public List<CustomMonsterLoadoutItemBinding> Items { get; set; } = new();

    [JsonPropertyName("skills")]
    public List<CustomMonsterLoadoutItemBinding> Skills { get; set; } = new();

    public CustomMonsterPreviewBinding Clone() => new()
    {
        MonsterId = MonsterId,
        NameZh = NameZh,
        NameEn = NameEn,
        MinDay = MinDay,
        MaxDay = MaxDay,
        DifficultySlot = DifficultySlot,
        Level = Level,
        HealthMax = HealthMax,
        StartingTier = StartingTier,
        RewardGold = RewardGold,
        RewardXp = RewardXp,
        Items = Items.Select(i => i.Clone()).ToList(),
        Skills = Skills.Select(s => s.Clone()).ToList()
    };
}

/// <summary>P0-21：自定义野怪单件装备/技能预览条目（含 Size 支撑客户端 SocketId 布局）。</summary>
public sealed class CustomMonsterLoadoutItemBinding
{
    [JsonPropertyName("cardId")]
    public string CardId { get; set; } = string.Empty;

    [JsonPropertyName("wireTemplateId")]
    public string? WireTemplateId { get; set; }

    [JsonPropertyName("size")]
    public string Size { get; set; } = "Small";

    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "Bronze";

    [JsonPropertyName("enchantment")]
    public string? Enchantment { get; set; }

    [JsonPropertyName("titleZh")]
    public string? TitleZh { get; set; }

    [JsonPropertyName("titleEn")]
    public string? TitleEn { get; set; }

    public CustomMonsterLoadoutItemBinding Clone() => new()
    {
        CardId = CardId,
        WireTemplateId = WireTemplateId,
        Size = Size,
        Tier = Tier,
        Enchantment = Enchantment,
        TitleZh = TitleZh,
        TitleEn = TitleEn
    };
}

/// <summary>
/// 实例级自定义卡牌视觉绑定（对应服务端侧信道 visual-bindings API，锚定 InstanceId / CustomTemplateId 防客户端白块）。
/// </summary>
public sealed class CustomCardVisualBinding
{
    public static string ResolveDefaultVisualTarget(string? cardType)
    {
        if (string.Equals(cardType, "Skill", StringComparison.OrdinalIgnoreCase))
            return "SkillIcon";
        if (string.Equals(cardType, "EventEncounter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cardType, "CombatEncounter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cardType, "PedestalEncounter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cardType, "EncounterStep", StringComparison.OrdinalIgnoreCase))
            return "EncounterPortrait";
        return "ItemArt";
    }

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

    /// <summary>五阶基础属性映射（Bronze/Silver/Gold/Diamond/Legendary → 属性表），供客户端局内升阶对比与详情预览精确渲染。</summary>
    [JsonPropertyName("tierAttributes")]
    public Dictionary<string, Dictionary<string, double>>? TierAttributes { get; set; }

    /// <summary>附魔预览属性运算映射（附魔名 → 属性修改列表），供客户端详情预览切换任意附魔时 100% 准确计算面板数值。</summary>
    [JsonPropertyName("enchantmentPreviewOps")]
    public Dictionary<string, List<ResolvedPreviewAttrOp>>? EnchantmentPreviewOps { get; set; }

    /// <summary>通用自定义机制实时计数角标列表（任意叠层/充能/倒计时机制均可声明并自动下发给客户端实时渲染）。</summary>
    [JsonPropertyName("counterDisplays")]
    public List<ResolvedCounterDisplay>? CounterDisplays { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    // ── Wave 2A §0.1 新增 11 个公开契约字段 ───────────────────────────────────

    [JsonPropertyName("isPatchOverride")]
    public bool IsPatchOverride { get; set; }

    [JsonPropertyName("isCustomBorrowShell")]
    public bool IsCustomBorrowShell { get; set; }

    [JsonPropertyName("titleEn")]
    public string? TitleEn { get; set; }

    [JsonPropertyName("descEn")]
    public string? DescEn { get; set; }

    [JsonPropertyName("activeTooltipsEn")]
    public List<string>? ActiveTooltipsEn { get; set; }

    [JsonPropertyName("passiveTooltipsEn")]
    public List<string>? PassiveTooltipsEn { get; set; }

    [JsonPropertyName("enchantTooltipsEn")]
    public Dictionary<string, List<string>>? EnchantTooltipsEn { get; set; }

    [JsonPropertyName("visualTarget")]
    public string VisualTarget { get; set; } = "ItemArt";

    [JsonPropertyName("customBgArtFile")]
    public string? CustomBgArtFile { get; set; }

    [JsonPropertyName("customBgArtSha256")]
    public string? CustomBgArtSha256 { get; set; }

    [JsonPropertyName("monsterPreview")]
    public CustomMonsterPreviewBinding? MonsterPreview { get; set; }

    /// <summary>基于模板级绑定派生绑定到特定卡牌 InstanceId 的独立深拷贝副本（含全部 11 新属性与子对象新字段）。</summary>
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
        AccentColor = AccentColor,
        IsPatchOverride = IsPatchOverride,
        IsCustomBorrowShell = IsCustomBorrowShell,
        TitleEn = TitleEn,
        DescEn = DescEn,
        ActiveTooltipsEn = ActiveTooltipsEn != null ? new List<string>(ActiveTooltipsEn) : null,
        PassiveTooltipsEn = PassiveTooltipsEn != null ? new List<string>(PassiveTooltipsEn) : null,
        EnchantTooltipsEn = EnchantTooltipsEn?.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.OrdinalIgnoreCase),
        VisualTarget = string.IsNullOrWhiteSpace(VisualTarget) ? ResolveDefaultVisualTarget(CardType) : VisualTarget,
        CustomBgArtFile = CustomBgArtFile,
        CustomBgArtSha256 = CustomBgArtSha256,
        MonsterPreview = MonsterPreview?.Clone(),
        TierAttributes = TierAttributes?.ToDictionary(
            kv => kv.Key,
            kv => new Dictionary<string, double>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase),
        EnchantmentPreviewOps = EnchantmentPreviewOps?.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(op => new ResolvedPreviewAttrOp
            {
                Attribute = op.Attribute,
                Operation = op.Operation,
                Value = op.Value,
                SourceAttribute = op.SourceAttribute,
                SourceMultiplier = op.SourceMultiplier,
                SourceAddOffset = op.SourceAddOffset
            }).ToList(),
            StringComparer.OrdinalIgnoreCase),
        CounterDisplays = CounterDisplays?.Select(cd => new ResolvedCounterDisplay
        {
            LabelZh = cd.LabelZh,
            SyncAttribute = cd.SyncAttribute,
            MaxValue = cd.MaxValue,
            MaxAttr = cd.MaxAttr,
            ColorHex = cd.ColorHex,
            HideWhenZero = cd.HideWhenZero,
            Style = cd.Style,
            ValueType = cd.ValueType,
            Format = cd.Format,
            Unit = cd.Unit,
            Position = cd.Position
        }).ToList()
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

    /// <summary>V14 为被魔改的原版卡牌挂载开放自定义沙盒机制列表。</summary>
    [JsonPropertyName("customMechanicsOverride")]
    public List<CardMechanicAttachment>? CustomMechanicsOverride { get; set; }

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

    [JsonPropertyName("activeTooltipsEn")]
    public List<string>? ActiveTooltipsEn { get; set; }

    [JsonPropertyName("passiveTooltipsEn")]
    public List<string>? PassiveTooltipsEn { get; set; }

    [JsonPropertyName("enchantTooltipsEn")]
    public Dictionary<string, List<string>>? EnchantTooltipsEn { get; set; }

    [JsonPropertyName("customArtFile")]
    public string? CustomArtFile { get; set; }

    /// <summary>D3-P1-12：可选覆写奇遇/遭遇独立背景图。</summary>
    [JsonPropertyName("customBgArtFile")]
    public string? CustomBgArtFile { get; set; }

    /// <summary>P0-25：可选覆写被魔改原版卡的实时计数徽章列表。</summary>
    [JsonPropertyName("counterDisplaysOverride")]
    public List<MechanicCounterDisplay>? CounterDisplaysOverride { get; set; }

    [JsonPropertyName("artRect")]
    public CustomCardArtRect? ArtRect { get; set; }

    [JsonPropertyName("accentColor")]
    public string? AccentColor { get; set; }

    /// <summary>P1-04：可选覆写原版遭遇切板键。</summary>
    [JsonPropertyName("boardOverride")]
    public string? BoardOverride { get; set; }

    /// <summary>P1-04：可选覆写原版遭遇演出键。</summary>
    [JsonPropertyName("presentation")]
    public string? Presentation { get; set; }

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
        if (!string.IsNullOrWhiteSpace(BoardOverride))
            cloned.BoardOverride = BoardOverride.Trim();
        if (!string.IsNullOrWhiteSpace(Presentation))
            cloned.Presentation = Presentation.Trim();
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
            {
                cloned.Attributes[k] = v;
                // P0-25：同步覆写各阶已有同名基线键，防止原版卡 Tiers[*].Attributes 静默吞没顶层 attributeOverrides；
                // 若同时提供更精细的 TierAttributeOverrides，将在下方优先覆写指定品阶。
                if (cloned.Tiers != null)
                {
                    foreach (var td in cloned.Tiers.Values)
                    {
                        if (td?.Attributes != null && td.Attributes.ContainsKey(k))
                            td.Attributes[k] = v;
                    }
                }
            }
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

        if (CustomMechanicsOverride != null)
        {
            cloned.CustomMechanics = CustomMechanicsOverride.Count > 0
                ? CustomMechanicsOverride.Select(m => new CardMechanicAttachment
                {
                    MechanicId = m.MechanicId,
                    Enabled = m.Enabled,
                    Params = new Dictionary<string, JsonElement>(m.Params, StringComparer.OrdinalIgnoreCase)
                }).ToList()
                : null;
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
        || !string.IsNullOrWhiteSpace(TitleEn)
        || !string.IsNullOrWhiteSpace(DescZh)
        || !string.IsNullOrWhiteSpace(DescEn)
        || !string.IsNullOrWhiteSpace(CustomArtFile)
        || !string.IsNullOrWhiteSpace(CustomBgArtFile)
        || (ActiveTooltipsZh is { Count: > 0 })
        || (PassiveTooltipsZh is { Count: > 0 })
        || (EnchantTooltipsZh is { Count: > 0 })
        || (ActiveTooltipsEn is { Count: > 0 })
        || (PassiveTooltipsEn is { Count: > 0 })
        || (EnchantTooltipsEn is { Count: > 0 })
        || (CounterDisplaysOverride is { Count: > 0 })
        || !string.IsNullOrWhiteSpace(AccentColor);

    /// <summary>P0-25：只要补丁对目标原版卡存在任意字段改动（数值/阶位/标签/附魔/能力/光环/机制/文本/美术/计数器/切板等）均返回 true。</summary>
    public bool HasAnyMutation =>
        HasVisualOrTextOverride
        || !string.IsNullOrWhiteSpace(StartingTier)
        || !string.IsNullOrWhiteSpace(SpawningEligibility)
        || (Heroes is { Count: > 0 })
        || AddTags.Count > 0
        || RemoveTags.Count > 0
        || AddHiddenTags.Count > 0
        || RemoveHiddenTags.Count > 0
        || (AttributeOverrides is { Count: > 0 })
        || (TierAttributeOverrides is { Count: > 0 })
        || (EnchantmentOverrides is { Count: > 0 })
        || (AbilitiesOverride is { Count: > 0 })
        || (AurasOverride is { Count: > 0 })
        || (CustomMechanicsOverride is { Count: > 0 })
        || ArtRect != null
        || !string.IsNullOrWhiteSpace(BoardOverride)
        || !string.IsNullOrWhiteSpace(Presentation);
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
    /// <summary>P0-22：本 Mod 注册的自定义肉鸽突变词条列表（来自 modes/*mutator*.json 或 modes/*.json 中的 mutators 数组）。</summary>
    public List<MutatorMetadata> CustomMutators { get; } = new();
    /// <summary>V14 本 Mod 注册的开放声明式沙盒自定义机制定义列表（来自 mechanics/*.json）。</summary>
    public List<MechanicDefinition> Mechanics { get; } = new();
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

    [JsonPropertyName("mechanicCircuitBreakerTrips")]
    public int MechanicCircuitBreakerTrips { get; set; }

    [JsonPropertyName("mechanicBudgetViolations")]
    public int MechanicBudgetViolations { get; set; }

    [JsonPropertyName("mechanicDiagnostics")]
    public List<MechanicDiagnostic> MechanicDiagnostics { get; set; } = new();
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
    public int TotalCustomMechanics { get; set; }
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

    [JsonPropertyName("customMechanicCount")]
    public int CustomMechanicCount { get; set; }

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

/// <summary>
/// P1-16（RC-MOD-ENCHANT-ELIGIBILITY）：自定义 Mod 物品通用标准附魔兼容集自动合成器。
/// 当 Mod 作者未在自定义物品中手写全部标准附魔且未显式禁用自动合成时，
/// 依据物品自身基础属性与标签（武器/伤害、护盾、灼烧、剧毒、回复、再生、冷却、暴击、尺寸等）
/// 自动补全 13 种官方标准附魔兼容定义，使其开箱即兼容全部原版定向/随机附魔祭坛。
/// </summary>
public static class ModDefaultEnchantmentSynthesizer
{
    private static readonly JsonSerializerOptions CompactRuleJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>官方 13 种标准附魔名称列表（与 EEnchantmentType 与官方祭坛全量对齐）。</summary>
    public static readonly IReadOnlyList<string> StandardEnchantmentNames =
    [
        "Obsidian", "Shielded", "Fiery", "Toxic", "Restorative", "Mossy",
        "Deadly", "Heavy", "Icy", "Turbo", "Golden", "Radiant", "Shiny"
    ];

    /// <summary>
    /// 依据自定义卡牌定义与已展开的品阶表构造最终有效附魔字典：
    /// 1) 非 Item（Skill/Encounter/Step 等）不自动合成，原样透传；
    /// 2) 优先完整保留作者在 <see cref="CustomCardDefinition.Enchantments"/> 中手写的定义；
    /// 3) <see cref="CustomCardDefinition.DisabledEnchantments"/> 指定的附魔会被严格排除；
    /// 4) 若 <see cref="CustomCardDefinition.DisableAutoEnchantments"/> 为 false，则按属性与标签补全缺失的标准附魔。
    /// </summary>
    public static Dictionary<string, EnchantmentData>? BuildEffectiveEnchantments(
        CustomCardDefinition card,
        Dictionary<string, TierData>? resolvedTiers)
    {
        ArgumentNullException.ThrowIfNull(card);

        if (!string.Equals(card.Type, "Item", StringComparison.OrdinalIgnoreCase))
        {
            return card.Enchantments != null
                ? new Dictionary<string, EnchantmentData>(card.Enchantments, StringComparer.OrdinalIgnoreCase)
                : null;
        }

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (card.DisabledEnchantments is { Count: > 0 })
        {
            for (int i = 0; i < card.DisabledEnchantments.Count; i++)
            {
                string? raw = card.DisabledEnchantments[i];
                if (!string.IsNullOrWhiteSpace(raw))
                    excluded.Add(raw.Trim());
            }
        }

        var result = new Dictionary<string, EnchantmentData>(StringComparer.OrdinalIgnoreCase);
        if (card.Enchantments != null)
        {
            foreach (var (k, v) in card.Enchantments)
            {
                if (string.IsNullOrWhiteSpace(k) || v == null) continue;
                string key = k.Trim();
                if (excluded.Contains(key)) continue;
                result[key] = v;
            }
        }

        if (card.DisableAutoEnchantments)
            return result.Count > 0 ? result : null;

        // 汇总该物品跨顶层与全品阶的基础属性特征
        var maxAttrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        static void MergeMaxAttrs(Dictionary<string, double> dst, Dictionary<string, double>? src)
        {
            if (src == null) return;
            foreach (var (k, v) in src)
            {
                if (string.IsNullOrWhiteSpace(k) || !double.IsFinite(v)) continue;
                if (!dst.TryGetValue(k, out double prev) || v > prev)
                    dst[k] = v;
            }
        }

        MergeMaxAttrs(maxAttrs, card.Attributes);
        var tierSource = resolvedTiers ?? card.Tiers;
        if (tierSource != null)
        {
            foreach (var td in tierSource.Values)
                MergeMaxAttrs(maxAttrs, td?.Attributes);
        }

        bool HasAttrPos(string name) => maxAttrs.TryGetValue(name, out double v) && v > 0;
        bool HasTag(string tag) => card.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        bool HasHiddenTag(string tag) => card.HiddenTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
        bool HasActionType(string actionType) => card.Abilities.Values.Any(ab =>
            ab?.Action != null && ContainsActionType(ab.Action, actionType));

        bool hasFiredTrigger = card.Abilities.Values.Any(ab =>
            ab?.Trigger != null && string.Equals(ab.Trigger.Type, "TTriggerOnCardFired", StringComparison.Ordinal));
        bool hasCooldown = HasAttrPos("CooldownMax");
        bool isActiveItem = hasCooldown || hasFiredTrigger;

        bool hasDamage = HasAttrPos("DamageAmount") || HasTag("Weapon") || HasHiddenTag("Damage") || HasActionType("TActionPlayerDamage");
        bool hasShield = HasAttrPos("ShieldApplyAmount") || HasHiddenTag("Shield") || HasActionType("TActionPlayerShieldApply");
        bool hasBurn = HasAttrPos("BurnApplyAmount") || HasHiddenTag("Burn") || HasActionType("TActionPlayerBurnApply");
        bool hasPoison = HasAttrPos("PoisonApplyAmount") || HasHiddenTag("Poison") || HasActionType("TActionPlayerPoisonApply");
        bool hasHeal = HasAttrPos("HealAmount") || HasHiddenTag("Heal") || HasActionType("TActionPlayerHeal");
        bool hasRegen = HasAttrPos("RegenApplyAmount") || HasHiddenTag("Regen") || HasActionType("TActionPlayerRegenApply");
        bool hasCrit = maxAttrs.ContainsKey("CritChance") || HasHiddenTag("Crit");
        bool canCrit = hasDamage || hasShield || hasBurn || hasPoison || hasHeal || hasRegen || hasCrit;

        // 选取主输出参照属性用于跨系转换（高量级：Damage/Shield/Heal；低量级：Burn/Poison/Regen）
        string? primaryAttr = null;
        bool primaryIsHighMagnitude = true;
        string primaryLabelZh = "主效果";
        if (HasAttrPos("DamageAmount")) { primaryAttr = "DamageAmount"; primaryIsHighMagnitude = true; primaryLabelZh = "伤害"; }
        else if (HasAttrPos("ShieldApplyAmount")) { primaryAttr = "ShieldApplyAmount"; primaryIsHighMagnitude = true; primaryLabelZh = "护盾"; }
        else if (HasAttrPos("HealAmount")) { primaryAttr = "HealAmount"; primaryIsHighMagnitude = true; primaryLabelZh = "治疗"; }
        else if (HasAttrPos("BurnApplyAmount")) { primaryAttr = "BurnApplyAmount"; primaryIsHighMagnitude = false; primaryLabelZh = "灼烧"; }
        else if (HasAttrPos("PoisonApplyAmount")) { primaryAttr = "PoisonApplyAmount"; primaryIsHighMagnitude = false; primaryLabelZh = "剧毒"; }
        else if (HasAttrPos("RegenApplyAmount")) { primaryAttr = "RegenApplyAmount"; primaryIsHighMagnitude = false; primaryLabelZh = "再生"; }

        // 按物品尺寸计算减速/加速/冻结时长（与官方 Small/Medium/Large 附魔尺度对齐）
        var sizeNorm = (card.Size ?? "Medium").Trim();
        double slowHasteMs = sizeNorm.Equals("Small", StringComparison.OrdinalIgnoreCase)
            ? 1000.0
            : (sizeNorm.Equals("Large", StringComparison.OrdinalIgnoreCase) ? 3000.0 : 2000.0);
        double freezeMs = sizeNorm.Equals("Small", StringComparison.OrdinalIgnoreCase)
            ? 500.0
            : (sizeNorm.Equals("Large", StringComparison.OrdinalIgnoreCase) ? 1500.0 : 1000.0);

        void TryAdd(string name, Func<EnchantmentData> factory)
        {
            if (excluded.Contains(name) || result.ContainsKey(name))
                return;
            result[name] = factory();
        }

        // 1. Obsidian（黑曜石）：已有伤害则伤害翻倍，否则主动开火附带伤害
        if (hasDamage)
            TryAdd("Obsidian", () => BuildMultiplySelfAuraEnchant("DamageAmount", 2.0, ["DamageReference"], "此物品的伤害翻倍"));
        else if (isActiveItem)
            TryAdd("Obsidian", () => BuildActiveConversionEnchant(
                "DamageAmount", "TActionPlayerDamage", "Opponent", null, ["Damage"],
                primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: true, fallbackFlat: 10.0,
                primaryLabelZh, "伤害"));

        // 2. Shielded（护盾）：已有护盾则护盾翻倍，否则主动开火附带护盾
        if (hasShield)
            TryAdd("Shielded", () => BuildMultiplySelfAuraEnchant("ShieldApplyAmount", 2.0, ["ShieldReference"], "此物品的护盾翻倍"));
        else if (isActiveItem)
            TryAdd("Shielded", () => BuildActiveConversionEnchant(
                "ShieldApplyAmount", "TActionPlayerShieldApply", "Self", null, ["Shield"],
                primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: true, fallbackFlat: 10.0,
                primaryLabelZh, "护盾"));

        // 3. Fiery（炽焰）：已有灼烧则灼烧翻倍，否则主动开火施加灼烧
        if (hasBurn)
            TryAdd("Fiery", () => BuildMultiplySelfAuraEnchant("BurnApplyAmount", 2.0, ["BurnReference"], "此物品的灼烧翻倍"));
        else if (isActiveItem)
            TryAdd("Fiery", () => BuildActiveConversionEnchant(
                "BurnApplyAmount", "TActionPlayerBurnApply", "Opponent", null, ["Burn"],
                primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: false, fallbackFlat: 2.0,
                primaryLabelZh, "灼烧"));

        // 4. Toxic（剧毒）：已有剧毒则剧毒翻倍，否则主动开火施加剧毒
        if (hasPoison)
            TryAdd("Toxic", () => BuildMultiplySelfAuraEnchant("PoisonApplyAmount", 2.0, ["PoisonReference"], "此物品的剧毒翻倍"));
        else if (isActiveItem)
            TryAdd("Toxic", () => BuildActiveConversionEnchant(
                "PoisonApplyAmount", "TActionPlayerPoisonApply", "Opponent", null, ["Poison"],
                primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: false, fallbackFlat: 2.0,
                primaryLabelZh, "剧毒"));

        // 5. Restorative（回复）：已有治疗则治疗翻倍，否则主动开火附带治疗
        if (hasHeal)
            TryAdd("Restorative", () => BuildMultiplySelfAuraEnchant("HealAmount", 2.0, ["HealReference"], "此物品的治疗翻倍"));
        else if (isActiveItem)
            TryAdd("Restorative", () => BuildActiveConversionEnchant(
                "HealAmount", "TActionPlayerHeal", "Self", null, ["Heal"],
                primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: true, fallbackFlat: 10.0,
                primaryLabelZh, "治疗"));

        // 6. Mossy（苔藓/再生）：已有再生则再生翻倍，否则主动开火获得再生（UntilEndOfCombat）
        if (hasRegen)
            TryAdd("Mossy", () => BuildMultiplySelfAuraEnchant("RegenApplyAmount", 2.0, ["RegenReference"], "此物品的再生翻倍"));
        else if (isActiveItem)
            TryAdd("Mossy", () => BuildActiveConversionEnchant(
                "RegenApplyAmount", "TActionPlayerRegenApply", "Self",
                new RuleNode { Type = "TDeterminantDuration", DurationType = "UntilEndOfCombat" },
                ["Regen"], primaryAttr, primaryIsHighMagnitude, targetIsHighMagnitude: false, fallbackFlat: 2.0,
                primaryLabelZh, "再生"));

        // 7. Deadly（致命）：可暴击物品 +50% 暴击率
        if (canCrit)
            TryAdd("Deadly", () => BuildAddSelfAuraEnchant("CritChance", 50.0, ["Crit"], "此物品获得 +50% 暴击率"));

        // 8. Heavy（沉重）：主动开火减速 1 件敌方有冷却物品
        if (isActiveItem)
            TryAdd("Heavy", () => BuildStatusControlEnchant(
                "TActionCardSlow", "SlowTargets", "SlowAmount", slowHasteMs,
                "OpponentHand", excludeSelf: false, ["Slow"],
                $"使 1 件敌方物品减速 {slowHasteMs / 1000.0:0.#} 秒"));

        // 9. Icy（冰霜）：主动开火冻结 1 件敌方有冷却物品
        if (isActiveItem)
            TryAdd("Icy", () => BuildStatusControlEnchant(
                "TActionCardFreeze", "FreezeTargets", "FreezeAmount", freezeMs,
                "OpponentHand", excludeSelf: false, ["Freeze"],
                $"冻结 1 件敌方物品 {freezeMs / 1000.0:0.#} 秒"));

        // 10. Turbo（涡轮）：主动开火加速 1 件友方其他有冷却物品
        if (isActiveItem)
            TryAdd("Turbo", () => BuildStatusControlEnchant(
                "TActionCardHaste", "HasteTargets", "HasteAmount", slowHasteMs,
                "SelfHand", excludeSelf: true, ["Haste"],
                $"使 1 件其他友方物品加速 {slowHasteMs / 1000.0:0.#} 秒"));

        // 11. Golden（黄金）：全物品通用双倍出售价值
        TryAdd("Golden", () => BuildMultiplySelfAuraEnchant("SellPrice", 2.0, ["EconomyReference"], "此物品的价值翻倍"));

        // 12. Radiant（辉光）：全物品通用免疫冻结、减速与摧毁
        TryAdd("Radiant", () => new EnchantmentData
        {
            Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["PercentSlowReduction"] = 100.0,
                ["PercentFreezeReduction"] = 100.0,
                ["DestroyImmunity"] = 1.0
            },
            Localization = BuildSingleTooltipLoc("Passive", "免疫冻结、减速与摧毁"),
            HasAbilities = false,
            HasAuras = false
        });

        // 13. Shiny（闪亮）：主动开火物品获得 +1 多重施放
        if (isActiveItem)
            TryAdd("Shiny", () => BuildAddSelfAuraEnchant("Multicast", 1.0, [], "此物品获得 +1 多重施放"));

        return result.Count > 0 ? result : null;
    }

    private static bool ContainsActionType(RuleNode node, string actionType, int depth = 0)
    {
        if (depth > 8) return false;
        if (string.Equals(node.Type, actionType, StringComparison.OrdinalIgnoreCase))
            return true;
        if (node.Actions is { Count: > 0 })
        {
            for (int i = 0; i < node.Actions.Count; i++)
                if (ContainsActionType(node.Actions[i], actionType, depth + 1))
                    return true;
        }
        return false;
    }

    private static RuleNode FixedVal(double v) => new()
    {
        Type = "TFixedValue",
        RawValueNode = JsonSerializer.SerializeToElement(v)
    };

    private static JsonElement ToElement(RuleNode node)
        => JsonSerializer.SerializeToElement(node, CompactRuleJsonOptions);

    private static LocalizationBlock BuildSingleTooltipLoc(string tooltipType, string textZh) => new()
    {
        Tooltips =
        [
            new TooltipEntry
            {
                TooltipType = tooltipType,
                Content = new LocalizedText
                {
                    Key = OverlayLocalizationStore.ComputeOfficialMd5Hash(textZh),
                    Text = textZh
                }
            }
        ]
    };

    private static EnchantmentData BuildMultiplySelfAuraEnchant(
        string attr,
        double multiplier,
        List<string> hiddenTags,
        string tooltipZh)
    {
        return new EnchantmentData
        {
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["e1"] = new AuraDefinition
                {
                    Id = "e1",
                    ActiveIn = "HandAndStash",
                    WorksIn = "Anywhere",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = attr,
                        Operation = "Multiply",
                        RawValueNode = ToElement(FixedVal(multiplier)),
                        Target = new RuleNode { Type = "TTargetCardSelf" }
                    }
                }
            },
            HiddenTags = hiddenTags,
            Localization = BuildSingleTooltipLoc("Passive", tooltipZh),
            HasAbilities = false,
            HasAuras = true
        };
    }

    private static EnchantmentData BuildAddSelfAuraEnchant(
        string attr,
        double delta,
        List<string> hiddenTags,
        string tooltipZh)
    {
        return new EnchantmentData
        {
            Auras = new Dictionary<string, AuraDefinition>
            {
                ["e1"] = new AuraDefinition
                {
                    Id = "e1",
                    ActiveIn = "HandAndStash",
                    WorksIn = "Anywhere",
                    Action = new RuleNode
                    {
                        Type = "TAuraActionCardModifyAttribute",
                        AttributeType = attr,
                        Operation = "Add",
                        RawValueNode = ToElement(FixedVal(delta)),
                        Target = new RuleNode { Type = "TTargetCardSelf" }
                    }
                }
            },
            HiddenTags = hiddenTags,
            Localization = BuildSingleTooltipLoc("Passive", tooltipZh),
            HasAbilities = false,
            HasAuras = true
        };
    }

    private static EnchantmentData BuildActiveConversionEnchant(
        string targetAttr,
        string actionType,
        string targetPlayerMode,
        RuleNode? duration,
        List<string> hiddenTags,
        string? primaryAttr,
        bool primaryIsHighMagnitude,
        bool targetIsHighMagnitude,
        double fallbackFlat,
        string primaryLabelZh,
        string targetLabelZh)
    {
        var ench = new EnchantmentData
        {
            // 对齐官方附魔契约与 P1-05 非光环存在性守卫：必须先声明基础附魔属性键，确保 Aura e2 合法写入且不污染原卡
            Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [targetAttr] = primaryAttr != null ? 0.0 : fallbackFlat
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["e1"] = new AbilityDefinition
                {
                    Id = "e1",
                    ActiveIn = "HandOnly",
                    WorksIn = "Anywhere",
                    Priority = "Medium",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = actionType,
                        Duration = duration,
                        Target = new RuleNode
                        {
                            Type = "TTargetPlayerRelative",
                            TargetMode = targetPlayerMode
                        }
                    }
                }
            },
            HiddenTags = hiddenTags,
            HasAbilities = true,
            HasAuras = false
        };

        string tooltipZh;
        if (!string.IsNullOrEmpty(primaryAttr))
        {
            double scale = (primaryIsHighMagnitude, targetIsHighMagnitude) switch
            {
                (true, true) => 1.0,
                (false, false) => 1.0,
                (true, false) => 0.1,
                (false, true) => 10.0
            };

            var refNode = new RuleNode
            {
                Type = "TReferenceValueCardAttribute",
                AttributeType = primaryAttr,
                Target = new RuleNode { Type = "TTargetCardSelf" },
                Modifier = Math.Abs(scale - 1.0) > 1e-9
                    ? new ValueModifierNode
                    {
                        ModifyMode = "Multiply",
                        ShouldRound = true,
                        Value = FixedVal(scale)
                    }
                    : null
            };

            ench.Auras["e2"] = new AuraDefinition
            {
                Id = "e2",
                ActiveIn = "HandAndStash",
                WorksIn = "Anywhere",
                Action = new RuleNode
                {
                    Type = "TAuraActionCardModifyAttribute",
                    AttributeType = targetAttr,
                    Operation = "Add",
                    RawValueNode = ToElement(refNode),
                    Target = new RuleNode { Type = "TTargetCardSelf" }
                }
            };
            ench.HasAuras = true;

            tooltipZh = scale switch
            {
                < 0.5 => $"额外施加等同于此物品{primaryLabelZh} 10% 的{targetLabelZh}",
                > 1.5 => $"额外触发等同于此物品{primaryLabelZh} {scale:0} 倍的{targetLabelZh}",
                _ => $"额外触发等同于此物品{primaryLabelZh}的{targetLabelZh}"
            };
        }
        else
        {
            tooltipZh = $"开火时额外施加 {fallbackFlat:0} 点{targetLabelZh}";
        }

        ench.Localization = BuildSingleTooltipLoc("Active", tooltipZh);
        return ench;
    }

    private static EnchantmentData BuildStatusControlEnchant(
        string actionType,
        string targetsAttr,
        string amountAttr,
        double amountMs,
        string targetSection,
        bool excludeSelf,
        List<string> hiddenTags,
        string tooltipZh)
    {
        var cooldownCond = new RuleNode
        {
            Type = "TCardConditionalAttribute",
            Attribute = "CooldownMax",
            ComparisonOperator = "GreaterThan",
            RawComparisonValueElement = ToElement(FixedVal(0.0))
        };

        return new EnchantmentData
        {
            Attributes = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                [targetsAttr] = 1.0,
                [amountAttr] = amountMs
            },
            Abilities = new Dictionary<string, AbilityDefinition>
            {
                ["e1"] = new AbilityDefinition
                {
                    Id = "e1",
                    ActiveIn = "HandOnly",
                    WorksIn = "Anywhere",
                    Priority = "Medium",
                    Trigger = new RuleNode { Type = "TTriggerOnCardFired" },
                    Action = new RuleNode
                    {
                        Type = actionType,
                        Target = new RuleNode
                        {
                            Type = "TTargetCardRandom",
                            TargetSection = targetSection,
                            ExcludeSelf = excludeSelf,
                            RawConditionsElement = ToElement(cooldownCond)
                        }
                    }
                }
            },
            HiddenTags = hiddenTags,
            Localization = BuildSingleTooltipLoc("Active", tooltipZh),
            HasAbilities = true,
            HasAuras = false
        };
    }
}

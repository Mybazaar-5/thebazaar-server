using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Data;

/// <summary>
/// 从 GameData.db 读取卡牌/野怪/模式数据，并在 Load 时自动接入客户端 Addressables 资源白名单。
///
/// 数据库结构（实测）：
///   cards(Id TEXT PK, Data BLOB)       Data = 明文 UTF-8 JSON
///   monsters / game_modes / level_ups / seasons / challenges / collectibles / tooltips 同构
/// </summary>
public sealed class CardPool
{
    public sealed class CardTemplate
    {
        public string Id { get; init; }
        public string InternalName { get; init; }
        public string Type { get; init; }
        public string[] Heroes { get; init; } = Array.Empty<string>();
        public string Size { get; init; } = "Medium";
        public string StartingTier { get; init; } = "Bronze";
        public string[] Tags { get; init; } = Array.Empty<string>();
        public string[] HiddenTags { get; init; } = Array.Empty<string>();
        public string ArtKey { get; init; } = "";
        public string SpawningEligibility { get; init; } = "Always";
        public string LocalizationTitleKey { get; init; } = "";
        public string TranslationKey { get; init; } = "";
        public bool IsSyntheticTestTemplate
            => string.IsNullOrEmpty(LocalizationTitleKey)
               && string.IsNullOrEmpty(TranslationKey)
               && !(InternalName?.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase) ?? false);
        public Dictionary<string, Dictionary<string, int>> TierAttributes { get; init; } = new();
        public int ExperienceAwardUponSelection { get; init; }
        public bool ShouldProgressHour { get; init; } = true;
        public bool IsReselectable { get; init; }
        public string? NewSetIdOnExit { get; init; }
        public string? BoardOverride { get; init; }
        public string? Presentation { get; init; }

        /// <summary>事件/商店/开局选择上下文（含 SpawnContext 与 Rules）。</summary>
        public SelectionContextData? SelectionContext { get; init; }
        /// <summary>点选后链式跳转候选遭遇。</summary>
        public List<NextEncounterOption> NextEncounterOnSelection { get; init; } = new();
        /// <summary>前置选择条件。</summary>
        public List<RuleNode> SelectionRequirements { get; init; } = new();
        public bool IsSingleSpawn { get; init; }
        public bool IsTakeover { get; init; }
        public double SpawningChance { get; init; }
        public double SpawningChanceIncrement { get; init; }
        /// <summary>基座遭遇行为（TPedestalBehaviorEnchant / Upgrade / EnchantRandom）。</summary>
        public RuleNode? Behavior { get; init; }
        /// <summary>基座遭遇对目标卡牌的准入条件。</summary>
        public RuleNode? SelectionCriteria { get; init; }

        // ── 战斗遭遇专属（TCardEncounterCombat）──────────────────────────────
        /// <summary>`CombatantType.MonsterTemplateId` —— 指向 monsters 表。</summary>
        public string MonsterTemplateId { get; init; }
        /// <summary>`CombatantType.Level` —— 野怪等级缩放。</summary>
        public int MonsterLevel { get; init; }
        public int RewardCombatGold { get; init; }
        public int RewardCombatXp { get; init; }
        public bool SandstormEnabled { get; init; } = true;

        /// <summary>`RewardVictory` / `RewardDefeat` 的基础金币与经验字段（向后兼容保留）。</summary>
        public int RewardVictoryGold { get; init; }
        public int RewardVictoryXp { get; init; }
        public int RewardDefeatGold { get; init; }
        public int RewardDefeatXp { get; init; }

        /// <summary>完整的官方 `RewardVictory` / `RewardDefeat`（包含 SpawnContext、SelectionContextRules 等）。</summary>
        public CombatRewardData? RewardVictory { get; init; }
        public CombatRewardData? RewardDefeat { get; init; }

        // ── Mod 自定义卡牌安全视觉载体元数据 ──────────────────────────────────
        /// <summary>若本模板来自自定义 Mod，指向客户端白名单内同 Type+同 Size 的合法原版视觉载体 GUID。</summary>
        public string? BaseVisualTemplateId { get; init; }
        /// <summary>所属 Mod ID（官方原生卡为 null）。</summary>
        public string? ModId { get; init; }
        /// <summary>Mod 自定义遭遇相对抽签权重（纯原版卡为 null）。</summary>
        public double? ModSpawnWeight { get; init; }
        /// <summary>Mod 自定义遭遇最早允许出现天数（纯原版卡为 null）。</summary>
        public int? ModMinDay { get; init; }
        /// <summary>Mod 自定义遭遇最晚允许出现天数（纯原版卡为 null）。</summary>
        public int? ModMaxDay { get; init; }
        /// <summary>Mod 自定义遭遇允许出现小时列表（null/空表示全白昼时段允许）。</summary>
        public int[]? ModAllowedHours { get; init; }
        /// <summary>P0-21：Mod 自定义野怪遭遇难度槽位（Bronze / Silver / Gold / Any）。</summary>
        public string? ModDifficultySlot { get; init; }
        /// <summary>P0-25：是否为原版卡 Patch 覆盖模板（非借壳新建自定义卡）。</summary>
        public bool IsPatchOverride { get; init; }
        /// <summary>P0-23：是否为不限英雄的通用自定义 Mod 技能。</summary>
        public bool IsUniversalCustomSkill { get; init; }
        /// <summary>W2-A 核心模板显式标注的自定义 Mod 卡标记。</summary>
        public bool CoreIsCustomModCard { get; init; }
        /// <summary>是否为 Mod 自定义卡（P0-25：排除原位 Patch 卡，优先遵循 core.IsCustomModCard 或借壳 BaseVisualTemplateId）。</summary>
        public bool IsCustomModCard => !IsPatchOverride && (CoreIsCustomModCard || !string.IsNullOrWhiteSpace(BaseVisualTemplateId));
    }

    public const string TravelAgentEncounterId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";
    public const string JungleExpeditionTicketId = "77c8ef2d-62c8-4214-993d-648915c93e83";
    public const string CrashSiteExpeditionTicketId = "5bd0a69d-420c-4cd0-93ec-07582c688a5b";
    public const string TinyWorldExpeditionTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";
    public const string JungleExpeditionRoomId = "91287d25-7f63-4826-8c42-06ed1bf83213";
    public const string CrashSiteExpeditionRoomId = "ead0a1ed-703e-445e-ba43-9a7df5d64f54";
    public const string TinyWorldExpeditionRoomId = "42db3205-58d5-4cfe-b537-14091b518ff6";

    // P1-01：官方 GameData.db cards.NewSetIdOnExit 全部 5 个替换遭遇集（Replacement Set）GUID
    public const string ReplacementSetCrashSiteId = "bc2ca2fe-2958-4cff-8c5a-ff30592d2b76";
    public const string ReplacementSetJungleFieryId = "ffc6b072-d9e1-4a1c-b0e7-6d0a9da5c591";
    public const string ReplacementSetJungleToxicId = "fc9adb79-acc7-4021-800c-358853eae91f";
    public const string ReplacementSetJungleObsidianId = "3e44be75-cf74-489f-9c9c-a5a1d64bd172";
    public const string ReplacementSetTinyWorldId = "e40c7e1f-6c6c-4a13-ad28-7bcd0d1af531";

    // P0-15 / P0-16：全局联动 Takeover、特殊 GuidOnly 事件与大件商人 Pol 常量
    public const string StarChartItemId = "5abf3441-11cb-4dbd-8d69-d4829f8df687";
    public const string StarChartTreasureEncounterId = "34c27d0d-2aa2-40a1-8276-791ee7086da1";
    public const string GrandOpeningEncounterId = "39e4aeb6-06f8-45af-a5d1-7e0ca01bd194";
    public const string SproutEncounterId = "5e1ac00b-1abc-44cb-8f9c-6755327580c5";
    public const string CoreInitializationEncounterId = "1596d1df-e943-48b4-bfc6-df2edb9dfe55";
    public const string PolEncounterId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    public const string PolPygmalienEncounterId = "bdf0a9ab-8bc2-4f1b-8f7d-614293ac2db2";

    /// <summary>P0-16：须剔除的 2 张任务残留 GuidOnly 事件（The Financial District - Blacksmith Investment / Furry Return）。</summary>
    public static readonly IReadOnlySet<string> ExcludedQuestRemnantEncounterIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "7676de25-e0eb-4aa2-a33c-0570ddf4777a", // The Financial District - Blacksmith Investment
            "be759478-d828-4bc2-8268-0596f2c731aa", // Furry Return - Large - Pet - Purr
        };

    /// <summary>
    /// 经 Patch 20.2 官方追踪（213,006 局 0 样本）与 Steam 补丁日志多源核验确认的已淘汰/限时活动残留/非白昼系统遭遇 ID 集合：
    /// 禁止进入常规白昼小时池，也禁止经 Aerodrome / Mysterious Portal 等非显式 IdList 动态召唤池漏出。
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedRetiredOrNonHourlyEncounterIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "7676de25-e0eb-4aa2-a33c-0570ddf4777a", // The Financial District - Blacksmith Investment（任务残留）
            "be759478-d828-4bc2-8268-0596f2c731aa", // Furry Return - Large - Pet - Purr（任务残留）
            "a64974ac-1250-41fe-aada-508b22c93ac4", // Bjorn's Gift Exchange（比约恩的礼物交换：Patch 9.2 冬季限时开局赠礼活动，已下架）
            "972e6031-4036-47ef-8e6e-1b8feb53fe47", // Underground Resistance（地下抵抗组织：早期赛季末限时活动 Annexian Subversion，已下架）
            "8c69a3e3-8c82-4049-aa89-1836ccb6ebc4", // Gulch's Dive（古奇潜水：废案/未上线残留卡，Day 1 直塞金阶 Gorgon Noble，0 样本）
            "1596d1df-e943-48b4-bfc6-df2edb9dfe55", // Core Initialization（核心初始化：Dooley Lv3 升级专属，非白昼小时事件）
            "7f2c3a0d-7675-44dd-8667-77b08455eec8", // Statia（丝塔奇娅：早期版本已更名重做为 Futura，0 样本）
            "34c27d0d-2aa2-40a1-8276-791ee7086da1", // Star Chart Treasure（星图宝藏：旧版残留卡，现行 Star Chart 联动在 The Docks，0 样本）
        };

    // 官方 4 座常规白昼地图基座 GUID（V13.1 问题 #2 天数门禁与单局唯一调度）
    public const string PedestalB1B2Id = "2e1e5a86-3ace-4f78-8b73-6641ca59e834";
    public const string PedestalSterlingId = "8b811b0f-15ba-4282-9732-6ddccb26da9f";
    public const string PedestalForjaId = "693af45b-6d85-497f-ba32-b2f2d01b8aa3";
    public const string PedestalTheArtistId = "49717d0f-71de-4291-8af6-d9c41ad3f438";

    /// <summary>
    /// 仅属 Lv10 / Lv19 升级里程碑或 Fates 复活选项的 12 座定向附魔祭坛（严禁在常规小时右槽随机刷出）。
    /// </summary>
    public static readonly IReadOnlySet<string> NonHourlyMilestoneEnchantPedestalIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "04f879e5-94fe-42ae-aa7a-273edbd6d410", // Yetarian Tomb
            "099c544c-6833-407d-8d38-d4d1bb488f72", // Murkwood Bayou
            "181c3701-c473-480a-b113-9befff77474b", // Gravity's Altar
            "22b1eb02-1133-4120-afe3-85f6e9a5aba2", // Plains of Edin
            "3b9b8d2d-37bf-4742-8d9f-4c05c6d2a000", // Celestial Conduit
            "48462c74-567d-4e1e-a441-f0fa62617e47", // Bladeborn Badlands
            "4874450b-1595-4bc2-b9e1-5b03e72a8cf6", // Arcane Abyss
            "8e1fe6b3-d96d-4587-a09d-1bf656d9b364", // Sirocco Steppe
            "943d17eb-e3d9-4349-97cd-b29d50be5197", // Sanguine Valley
            "cce12722-7c66-4030-8a2d-b7b8260a847d", // Guardian's Gorge
            "ce8b4112-39eb-419a-b65e-56f5b7c6aebd", // Mossy Ridge
            "e36bfb52-5c63-4f59-815d-912af7917620", // Burning Caldera
        };

    private static readonly IReadOnlySet<string> NonHourlyMilestoneEnchantPedestalNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Bladeborn Badlands",
            "Burning Caldera",
            "Celestial Conduit",
            "Gravity's Altar",
            "Guardian's Gorge",
            "Mossy Ridge",
            "Murkwood Bayou",
            "Plains of Edin",
            "Sirocco Steppe",
            "Arcane Abyss",
            "Sanguine Valley",
            "Yetarian Tomb",
        };

    /// <summary>
    /// V13.1 问题 #6：内置白名单缺失官方子卡的安全同事件视觉载体回退表：
    /// 服务端完整执行原卡规则与中文标题，下发客户端时借用同事件合法白名单 EncounterStep 视觉载体以实现 0 白块 0 崩溃。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltinVisualFallbackMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // [Finn's Big Bite] Farm Fresh Ingredients -> 复用 [Finn's Big Bite] Compare Big Bites 合法视觉载体
            ["4101c951-4439-4593-89c1-a01046ebbf8e"] = "68d8323d-acef-41fa-90ea-1771e5935b66",
        };

    /// <summary>官方 Expedition 门票模板 ID -> 对应 Expedition 房间遭遇模板 ID 映射表。</summary>
    public static readonly IReadOnlyDictionary<string, string> ExpeditionTicketToRoomMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [JungleExpeditionTicketId] = JungleExpeditionRoomId,
            [CrashSiteExpeditionTicketId] = CrashSiteExpeditionRoomId,
            [TinyWorldExpeditionTicketId] = TinyWorldExpeditionRoomId,
        };

    private static readonly IReadOnlyDictionary<string, string> ExpeditionRoomToTicketMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [JungleExpeditionRoomId] = JungleExpeditionTicketId,
            [CrashSiteExpeditionRoomId] = CrashSiteExpeditionTicketId,
            [TinyWorldExpeditionRoomId] = TinyWorldExpeditionTicketId,
        };

    private sealed class PoolSnapshot
    {
        public required List<CardTemplate> All { get; init; }
        public required Dictionary<string, CardTemplate> ById { get; init; }
        public required Dictionary<string, List<CardTemplate>> ByType { get; init; }
        public required Dictionary<string, string> CustomToBaseVisualId { get; init; }
        public required int AssetValidCount { get; init; }
        public ILocalizationStore? Localization { get; init; }
        public HashSet<string> ReferencedCardIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public GuidOnlyHourlyEncounterCatalog? GuidOnlyCatalog { get; set; }
        public ConcurrentDictionary<string, List<CardTemplate>> HourlyEncountersCache { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, List<CardTemplate>> HeroTakeoverCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private volatile PoolSnapshot _snap = new()
    {
        All = new List<CardTemplate>(),
        ById = new Dictionary<string, CardTemplate>(StringComparer.OrdinalIgnoreCase),
        ByType = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase),
        CustomToBaseVisualId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        AssetValidCount = 0,
    };

    private readonly HashSet<string> _validCardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _zhTranslatedKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认游戏模式 Id（来自 GameData.db 的 game_modes 表，实测值）。</summary>
    public Guid DefaultGameModeId { get; private set; } = Guid.Empty;

    /// <summary>从 GameData.db 的 game_modes.ItemSkillSpawnTierPercantagesByDay 解析的每日品阶权重表。</summary>
    public IReadOnlyList<(double Bronze, double Silver, double Gold, double Diamond)> TierChancesByDay { get; private set; }
        = TierChances.DefaultTable;

    /// <summary>从 GameData.db 的 game_modes.StandardPrices 解析的标准买卖价格表。</summary>
    public StandardPricesSettings StandardPrices { get; private set; } = new();

    /// <summary>构造绑定到当前 GameData.db 权重表的 TierChances 实例。</summary>
    public TierChances CreateTierChances(int day = 1) => new(TierChancesByDay) { CurrentDay = day };

    public int TotalRawCards => _snap.All.Count;
    public int TotalCards => WhitelistEnabled ? _snap.AssetValidCount : _snap.All.Count;
    public IReadOnlyList<CardTemplate> AllItems => _snap.ByType.TryGetValue("Item", out var list) ? list : Array.Empty<CardTemplate>();
    public IReadOnlyList<CardTemplate> AllSkills => _snap.ByType.TryGetValue("Skill", out var list) ? list : Array.Empty<CardTemplate>();
    public string LoadedFrom { get; private set; } = "";
    public bool WhitelistEnabled { get; private set; }
    public string? WhitelistLoadedFrom { get; private set; }
    public bool ZhTranslationLoaded { get; private set; }
    public string? ZhTranslationLoadedFrom { get; private set; }
    public IReadOnlySet<string> ValidCardIds => _validCardIds;

    /// <summary>
    /// 直接基于已单次加载的 <see cref="GameDataRepository"/> 构造只读卡池（彻底消灭启动期重复解析 45MB GameData.db），
    /// 同时兼容 Mod 热叠加和四层外挂中文词典校验。
    /// </summary>
    public static CardPool FromRepository(
        GameDataRepository repo,
        string? dataRoot = null,
        IReadOnlySet<string>? validWhitelistOverride = null,
        IReadOnlyDictionary<string, CustomCardVisualBinding>? visualBindings = null)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var pool = new CardPool();

        string probeBase = !string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(dataRoot, "GameData.db")
            : (repo.SourceDataPath ?? string.Empty);

        if (validWhitelistOverride is { Count: > 0 })
        {
            foreach (var id in validWhitelistOverride)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    pool._validCardIds.Add(id);
            }
            pool.WhitelistEnabled = pool._validCardIds.Count > 0;
            pool.WhitelistLoadedFrom = "<injected>";
        }
        else if (!string.IsNullOrWhiteSpace(probeBase))
        {
            pool.TryLoadAssetWhitelist(probeBase);
        }

        if (repo.HasLocalizationLoaded)
        {
            pool.ZhTranslationLoaded = true;
            pool.ZhTranslationLoadedFrom = repo.Localization?.SourcePath ?? "<repo-localization>";
        }
        else if (!string.IsNullOrWhiteSpace(probeBase))
        {
            pool.TryLoadZhTranslations(probeBase);
        }

        if (Guid.TryParse(repo.GameMode.Id, out var gmGuid))
            pool.DefaultGameModeId = gmGuid;
        if (repo.GameMode.ItemSkillSpawnTierPercantagesByDay is { Count: > 0 })
            pool.TierChancesByDay = TierChances.FromGameModeMap(
                repo.GameMode.ItemSkillSpawnTierPercantagesByDay,
                repo.GameMode.NumberOfDays > 0 ? repo.GameMode.NumberOfDays : 10);
        if (repo.GameMode.StandardPrices != null)
            pool.StandardPrices = repo.GameMode.StandardPrices;

        pool.LoadedFrom = repo.SourceDataPath ?? repo.SourceZipPath ?? "<repository>";
        pool.ApplyRepositoryOverlay(repo, visualBindings);
        Console.WriteLine(
            $"[CardPool] (shared repo) mapped {pool.TotalRawCards} raw cards ({pool.TotalCards} asset-valid, whitelist={pool.WhitelistEnabled})");
        return pool;
    }

    /// <summary>
    /// 基于当前卡池的资产白名单、中文词库与价格/品阶表，在内存中直接派生一个独立叠加指定仓库与 Mod 的新卡池（零 SQLite I/O，用于锦标赛按房隔离加载 Mod）。
    /// </summary>
    public CardPool CreateIsolatedOverlay(
        GameDataRepository activeRepo,
        IReadOnlyDictionary<string, CustomCardVisualBinding>? visualBindings = null)
    {
        ArgumentNullException.ThrowIfNull(activeRepo);
        // D2-P2-01：保留独立仓库的 GameMode 配置（GameModeId / TierChancesByDay / StandardPrices），避免回退至基座旧值
        var gm = activeRepo.GameMode;
        Guid modeId = gm != null && Guid.TryParse(gm.Id, out var parsedGmId) ? parsedGmId : DefaultGameModeId;
        var tierChances = gm?.ItemSkillSpawnTierPercantagesByDay is { Count: > 0 }
            ? TierChances.FromGameModeMap(gm.ItemSkillSpawnTierPercantagesByDay, gm.NumberOfDays > 0 ? gm.NumberOfDays : 10)
            : TierChancesByDay;
        var stdPrices = gm?.StandardPrices ?? StandardPrices;

        var clone = new CardPool
        {
            DefaultGameModeId = modeId,
            TierChancesByDay = tierChances,
            StandardPrices = stdPrices,
            LoadedFrom = activeRepo.SourceDataPath ?? activeRepo.SourceZipPath ?? LoadedFrom,
            WhitelistEnabled = WhitelistEnabled,
            WhitelistLoadedFrom = WhitelistLoadedFrom,
            ZhTranslationLoaded = ZhTranslationLoaded,
            ZhTranslationLoadedFrom = ZhTranslationLoadedFrom,
        };
        foreach (string id in _validCardIds)
            clone._validCardIds.Add(id);
        foreach (string k in _zhTranslatedKeys)
            clone._zhTranslatedKeys.Add(k);
        clone.ApplyRepositoryOverlay(activeRepo, visualBindings);
        return clone;
    }

    /// <summary>
    /// 原子热刷新卡池快照（用于服务端 Mod 热重载/运行时启停，读路径全程无锁无撕裂）。
    /// </summary>
    public void ApplyRepositoryOverlay(
        GameDataRepository activeRepo,
        IReadOnlyDictionary<string, CustomCardVisualBinding>? visualBindings = null)
    {
        ArgumentNullException.ThrowIfNull(activeRepo);

        var all = new List<CardTemplate>(activeRepo.CardsById.Count);
        var byId = new Dictionary<string, CardTemplate>(activeRepo.CardsById.Count, StringComparer.OrdinalIgnoreCase);
        var byType = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase);
        var customMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (visualBindings != null)
        {
            foreach (var (cid, b) in visualBindings)
            {
                // P0-25：原位 Patch 覆盖卡绝不写入借壳映射表 CustomToBaseVisualId
                if (!b.IsPatchOverride && !string.IsNullOrWhiteSpace(cid) && !string.IsNullOrWhiteSpace(b.BaseVisualTemplateId))
                    customMap[cid] = b.BaseVisualTemplateId;
            }
        }

        int validCount = 0;
        foreach (var core in activeRepo.CardsById.Values)
        {
            if (core == null || string.IsNullOrWhiteSpace(core.Id)) continue;
            CustomCardVisualBinding? binding = null;
            visualBindings?.TryGetValue(core.Id, out binding);
            var mapped = MapFromCoreTemplate(core, binding);
            all.Add(mapped);
            byId[mapped.Id] = mapped;
            if (!byType.TryGetValue(mapped.Type, out var list))
            {
                list = new List<CardTemplate>();
                byType[mapped.Type] = list;
            }
            list.Add(mapped);

            if (!WhitelistEnabled
                || _validCardIds.Contains(mapped.Id)
                || (!string.IsNullOrWhiteSpace(mapped.BaseVisualTemplateId) && _validCardIds.Contains(mapped.BaseVisualTemplateId)))
            {
                validCount++;
            }
        }

        if (activeRepo.HasLocalizationLoaded)
        {
            ZhTranslationLoaded = true;
            ZhTranslationLoadedFrom ??= activeRepo.Localization?.SourcePath ?? "<overlay>";
        }

        var referencedIds = CollectRepositoryReferencedCardIds(activeRepo);
        _snap = new PoolSnapshot
        {
            All = all,
            ById = byId,
            ByType = byType,
            CustomToBaseVisualId = customMap,
            AssetValidCount = validCount,
            Localization = activeRepo.Localization,
            ReferencedCardIds = referencedIds,
        };
    }

    /// <summary>解析自定义卡或内置缺失白名单官方子卡对应的合法原版视觉载体 GUID；若本身即为原版 GUID 则原样返回。</summary>
    public string ResolveSafeWireTemplateId(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return string.Empty;
        var snap = _snap;
        if (snap.CustomToBaseVisualId.TryGetValue(templateId, out var baseId) && !string.IsNullOrWhiteSpace(baseId))
            return baseId;
        if (snap.ById.TryGetValue(templateId, out var tpl) && !tpl.IsPatchOverride && !string.IsNullOrWhiteSpace(tpl.BaseVisualTemplateId))
            return tpl.BaseVisualTemplateId!;
        if (WhitelistEnabled && !_validCardIds.Contains(templateId) &&
            BuiltinVisualFallbackMap.TryGetValue(templateId, out var builtinFallback) &&
            _validCardIds.Contains(builtinFallback))
        {
            return builtinFallback;
        }
        return templateId;
    }

    /// <summary>构建包含当前白名单、内置视觉回退子卡与全部已启用 Mod 自定义卡 ID 的合并有效资产集（供 SpawnQueryEngine 放行）。</summary>
    public IReadOnlySet<string>? GetEffectiveValidAssetIds()
    {
        if (!WhitelistEnabled) return null;
        var snap = _snap;
        if (snap.CustomToBaseVisualId.Count == 0 && BuiltinVisualFallbackMap.Count == 0)
            return _validCardIds;

        var merged = new HashSet<string>(_validCardIds, StringComparer.OrdinalIgnoreCase);
        foreach (var (customId, baseId) in snap.CustomToBaseVisualId)
        {
            if (_validCardIds.Contains(baseId))
                merged.Add(customId);
        }
        foreach (var (missingId, fallbackId) in BuiltinVisualFallbackMap)
        {
            if (_validCardIds.Contains(fallbackId))
                merged.Add(missingId);
        }
        return merged;
    }

    private static CardTemplate MapFromCoreTemplate(
        BazaarCore.Models.CardTemplate core,
        CustomCardVisualBinding? binding)
    {
        var tiers = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        // D3-P1-01：始终以顶层 core.Attributes 作为底表，逐阶叠加 core.Tiers[tier].Attributes
        Dictionary<string, int>? baseAttrs = null;
        if (core.Attributes is { Count: > 0 })
        {
            baseAttrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ak, av) in core.Attributes)
                baseAttrs[ak] = (int)Math.Round(av);
        }

        string startTierName = string.IsNullOrWhiteSpace(core.StartingTier) ? "Bronze" : core.StartingTier;
        if (core.Tiers is { Count: > 0 })
        {
            foreach (var (tierName, tierData) in core.Tiers)
            {
                var attrs = baseAttrs != null
                    ? new Dictionary<string, int>(baseAttrs, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (tierData?.Attributes != null)
                {
                    foreach (var (ak, av) in tierData.Attributes)
                        attrs[ak] = (int)Math.Round(av);
                }
                tiers[tierName] = attrs;
            }
        }
        else if (baseAttrs is { Count: > 0 })
        {
            tiers[startTierName] = new Dictionary<string, int>(baseAttrs, StringComparer.OrdinalIgnoreCase);
        }

        // D3-P1-01 & P0-25：对自定义新卡（排除原位 Patch）按 StartingTier -> Diamond 向高阶补齐缺省属性快照，防止中后期商店被 TierAttributes.ContainsKey 误吞
        bool isPatchOverride = binding?.IsPatchOverride ?? false;
        bool isCustomCard = !isPatchOverride && (core.IsCustomModCard || !string.IsNullOrWhiteSpace(binding?.BaseVisualTemplateId));
        if (isCustomCard && tiers.Count > 0)
        {
            string[] standardProgression = ["Bronze", "Silver", "Gold", "Diamond"];
            int startOrder = TierOrder(startTierName);
            Dictionary<string, int>? prevAttrs = tiers.TryGetValue(startTierName, out var st)
                ? st
                : tiers.Values.First();
            foreach (string tierName in standardProgression)
            {
                if (TierOrder(tierName) < startOrder) continue;
                if (tiers.TryGetValue(tierName, out var existing))
                {
                    prevAttrs = existing;
                }
                else
                {
                    var inherited = new Dictionary<string, int>(prevAttrs, StringComparer.OrdinalIgnoreCase);
                    tiers[tierName] = inherited;
                    prevAttrs = inherited;
                }
            }
        }

        var selReqs = new List<RuleNode>();
        if (core.SelectionRequirements != null)
            selReqs.Add(core.SelectionRequirements);

        return new CardTemplate
        {
            Id = core.Id,
            InternalName = core.InternalName ?? string.Empty,
            Type = core.Type ?? string.Empty,
            Heroes = core.Heroes?.ToArray() ?? Array.Empty<string>(),
            Size = string.IsNullOrWhiteSpace(core.Size) ? "Medium" : core.Size,
            StartingTier = string.IsNullOrWhiteSpace(core.StartingTier) ? "Bronze" : core.StartingTier,
            Tags = core.Tags?.ToArray() ?? Array.Empty<string>(),
            HiddenTags = core.HiddenTags?.ToArray() ?? Array.Empty<string>(),
            ArtKey = core.ArtKey ?? string.Empty,
            SpawningEligibility = string.IsNullOrWhiteSpace(core.SpawningEligibility) ? "Always" : core.SpawningEligibility!,
            LocalizationTitleKey = core.Localization?.Title?.Key ?? string.Empty,
            TranslationKey = core.TranslationKey ?? string.Empty,
            TierAttributes = tiers,
            ExperienceAwardUponSelection = core.ExperienceAwardUponSelection ?? 0,
            ShouldProgressHour = core.ShouldProgressHour ?? true,
            IsReselectable = core.IsReselectable ?? false,
            NewSetIdOnExit = core.NewSetIdOnExit,
            BoardOverride = core.BoardOverride,
            Presentation = core.Presentation,
            SelectionContext = core.SelectionContext,
            NextEncounterOnSelection = core.NextEncounterOnSelection != null
                ? new List<NextEncounterOption>(core.NextEncounterOnSelection)
                : new List<NextEncounterOption>(),
            SelectionRequirements = selReqs,
            IsSingleSpawn = core.IsSingleSpawn ?? false,
            IsTakeover = core.IsTakeover ?? false,
            SpawningChance = core.SpawningChance ?? 0.0,
            SpawningChanceIncrement = core.SpawningChanceIncrement ?? 0.0,
            Behavior = core.Behavior,
            SelectionCriteria = core.SelectionCriteria,
            MonsterTemplateId = core.CombatantType?.MonsterTemplateId ?? string.Empty,
            MonsterLevel = core.CombatantType?.Level ?? 0,
            RewardCombatGold = core.RewardCombatGold ?? 0,
            RewardCombatXp = core.RewardCombatXp ?? 0,
            SandstormEnabled = core.SandstormEnabled ?? true,
            RewardVictoryGold = core.RewardVictory?.GoldReward ?? 0,
            RewardVictoryXp = core.RewardVictory?.ExperienceReward ?? 0,
            RewardDefeatGold = core.RewardDefeat?.GoldReward ?? 0,
            RewardDefeatXp = core.RewardDefeat?.ExperienceReward ?? 0,
            RewardVictory = core.RewardVictory,
            RewardDefeat = core.RewardDefeat,
            BaseVisualTemplateId = binding?.BaseVisualTemplateId,
            ModId = binding?.ModId ?? core.CustomModOrigin ?? (core.Version?.StartsWith("mod:", StringComparison.OrdinalIgnoreCase) == true ? core.Version[4..] : null),
            ModSpawnWeight = core.ModSpawnWeight,
            ModMinDay = core.ModMinDay,
            ModMaxDay = core.ModMaxDay,
            ModAllowedHours = core.AllowedHours is { Count: > 0 } ? core.AllowedHours.ToArray() : null,
            ModDifficultySlot = core.ModDifficultySlot,
            IsPatchOverride = isPatchOverride,
            IsUniversalCustomSkill = core.IsUniversalCustomSkill,
            CoreIsCustomModCard = core.IsCustomModCard,
        };
    }

    public static CardPool Load(string dbPath)
    {
        var pool = new CardPool();
        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"[CardPool] NOT FOUND: {dbPath}");
            return pool;
        }

        pool.TryLoadAssetWhitelist(dbPath);
        pool.TryLoadZhTranslations(dbPath);

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // 先读 game_modes：客户端必须拿到真实 GameModeId，同时从权威 DB 同步每日品阶分布与标准价格
        try
        {
            using var gmCmd = conn.CreateCommand();
            gmCmd.CommandText = "SELECT Id, Data FROM game_modes LIMIT 1";
            using var gmReader = gmCmd.ExecuteReader();
            if (gmReader.Read())
            {
                string gmId = gmReader.IsDBNull(0) ? string.Empty : gmReader.GetString(0);
                if (Guid.TryParse(gmId, out var g))
                {
                    pool.DefaultGameModeId = g;
                    Console.WriteLine($"[CardPool] game mode id = {g}");
                }

                if (!gmReader.IsDBNull(1))
                {
                    object rawData = gmReader.GetValue(1);
                    string? gmJson = rawData switch
                    {
                        byte[] bytes => System.Text.Encoding.UTF8.GetString(bytes),
                        string s => s,
                        _ => null,
                    };
                    if (!string.IsNullOrWhiteSpace(gmJson))
                    {
                        var gmData = JsonSerializer.Deserialize<GameModeData>(gmJson, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                        });
                        if (gmData != null)
                        {
                            if (gmData.ItemSkillSpawnTierPercantagesByDay is { Count: > 0 })
                                pool.TierChancesByDay = TierChances.FromGameModeMap(
                                    gmData.ItemSkillSpawnTierPercantagesByDay,
                                    gmData.NumberOfDays > 0 ? gmData.NumberOfDays : 10);
                            if (gmData.StandardPrices != null)
                                pool.StandardPrices = gmData.StandardPrices;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[CardPool] game_modes read failed: " + ex.Message);
        }

        var all = new List<CardTemplate>(3600);
        var byId = new Dictionary<string, CardTemplate>(3600, StringComparer.OrdinalIgnoreCase);
        var byType = new Dictionary<string, List<CardTemplate>>(StringComparer.OrdinalIgnoreCase);
        var referencedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int validCount = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Data FROM cards";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            byte[] blob = (byte[])reader.GetValue(1);
            string json = System.Text.Encoding.UTF8.GetString(blob);
            CollectJsonGuidRefs(json, id, referencedIds);
            var t = Parse(id, json);
            if (t == null) continue;
            all.Add(t);
            byId[t.Id] = t;
            if (!byType.TryGetValue(t.Type, out var list))
            {
                list = new List<CardTemplate>();
                byType[t.Type] = list;
            }
            list.Add(t);
            if (!pool.WhitelistEnabled || pool._validCardIds.Contains(t.Id))
                validCount++;
        }

        // 同步扫描 level_ups 表引用的遭遇/步骤 GUID，防止升级池专用卡被误纳入白昼常规 GuidOnly 目录
        try
        {
            using var luCmd = conn.CreateCommand();
            luCmd.CommandText = "SELECT Data FROM level_ups";
            using var luReader = luCmd.ExecuteReader();
            while (luReader.Read())
            {
                if (luReader.IsDBNull(0)) continue;
                string luJson = luReader.GetValue(0) is byte[] b
                    ? System.Text.Encoding.UTF8.GetString(b)
                    : luReader.GetString(0);
                CollectJsonGuidRefs(luJson, selfId: null, referencedIds);
            }
        }
        catch { }

        pool._snap = new PoolSnapshot
        {
            All = all,
            ById = byId,
            ByType = byType,
            CustomToBaseVisualId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AssetValidCount = validCount,
            ReferencedCardIds = referencedIds,
        };
        pool.LoadedFrom = dbPath;
        Console.WriteLine(
            $"[CardPool] loaded {all.Count} raw cards ({pool.TotalCards} asset-valid, whitelist={pool.WhitelistEnabled}) from {dbPath}");
        foreach (var kv in byType)
        {
            int validCnt = pool.WhitelistEnabled ? kv.Value.Count(c => pool.IsAssetValid(c.Id)) : kv.Value.Count;
            Console.WriteLine($"[CardPool]   {kv.Key,-22} {validCnt}/{kv.Value.Count}");
        }
        return pool;
    }

    private void TryLoadAssetWhitelist(string dbPath)
    {
        string? candidatePath = ResolveWhitelistPath(dbPath);
        if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
        {
            Console.WriteLine("[CardPool] valid_card_ids.json not found; asset whitelist disabled");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(candidatePath));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        _validCardIds.Add(item.GetString()!);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object &&
                     root.TryGetProperty("valid_ids", out var validIdsEl) &&
                     validIdsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in validIdsEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        _validCardIds.Add(item.GetString()!);
                }
            }

            if (_validCardIds.Count > 0)
            {
                WhitelistEnabled = true;
                WhitelistLoadedFrom = candidatePath;
                Console.WriteLine($"[CardPool] loaded {_validCardIds.Count} whitelisted asset card IDs from {candidatePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[CardPool] failed to load valid_card_ids.json: " + ex.Message);
        }
    }

    private static string? ResolveWhitelistPath(string dbPath)
    {
        string? dbDir = !string.IsNullOrWhiteSpace(dbPath) ? Path.GetDirectoryName(Path.GetFullPath(dbPath)) : null;
        var candidates = new[]
        {
            dbDir != null ? Path.Combine(dbDir, "valid_card_ids.json") : null,
            Path.Combine(AppContext.BaseDirectory, "data", "valid_card_ids.json"),
            Path.Combine(Environment.CurrentDirectory, "data", "valid_card_ids.json"),
            Path.Combine(Environment.CurrentDirectory, "dist", "data", "valid_card_ids.json"),
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>判断卡牌 ID 是否具备客户端 Addressables 本地资产（Mod 自定义卡校验其视觉载体是否位于白名单；内置回退子卡仅通过 GetEffectiveValidAssetIds/ResolveSafeWireTemplateId 处理以保持原生白名单纯净）。</summary>
    public bool IsAssetValid(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!WhitelistEnabled) return true;
        if (_validCardIds.Contains(id)) return true;
        var snap = _snap;
        if (snap.CustomToBaseVisualId.TryGetValue(id, out var baseId) && _validCardIds.Contains(baseId))
            return true;
        if (snap.ById.TryGetValue(id, out var tpl) && !string.IsNullOrWhiteSpace(tpl.BaseVisualTemplateId))
            return _validCardIds.Contains(tpl.BaseVisualTemplateId);
        return false;
    }

    private void TryLoadZhTranslations(string dbPath)
    {
        string? candidatePath = ResolveZhTranslationPath(dbPath);
        if (string.IsNullOrEmpty(candidatePath) || !File.Exists(candidatePath))
        {
            Console.WriteLine("[CardPool] zh-CN.bytes not found; zh-CN title filter disabled");
            return;
        }

        try
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = candidatePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            using var conn = new SqliteConnection(connStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT hash, text FROM translation";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                string hash = reader.GetString(0);
                string text = reader.GetString(1);
                if (!string.IsNullOrWhiteSpace(hash) && !string.IsNullOrEmpty(text))
                    _zhTranslatedKeys.Add(hash);
            }

            if (_zhTranslatedKeys.Count > 0)
            {
                ZhTranslationLoaded = true;
                ZhTranslationLoadedFrom = candidatePath;
                Console.WriteLine($"[CardPool] loaded {_zhTranslatedKeys.Count} zh-CN translation keys from {candidatePath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[CardPool] failed to load zh-CN.bytes: " + ex.Message);
        }
    }

    private static string? ResolveZhTranslationPath(string dbPath)
    {
        string? dbDir = !string.IsNullOrWhiteSpace(dbPath) ? Path.GetDirectoryName(Path.GetFullPath(dbPath)) : null;
        var candidates = new[]
        {
            dbDir != null ? Path.Combine(dbDir, "translations", "zh-CN.bytes") : null,
            Path.Combine(AppContext.BaseDirectory, "data", "translations", "zh-CN.bytes"),
            Path.Combine(Environment.CurrentDirectory, "data", "translations", "zh-CN.bytes"),
            Path.Combine(Environment.CurrentDirectory, "dist", "data", "translations", "zh-CN.bytes"),
            SteamPathResolver.ResolveZhCnTranslationPath(),
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
    }

    /// <summary>判断卡牌模板是否拥有官方或外挂/Mod 简体中文标题（未加载词库时回退为 true）。</summary>
    public bool HasZhTranslation(CardTemplate? tpl)
    {
        if (tpl == null) return false;
        if (tpl.IsCustomModCard) return true;
        if (!ZhTranslationLoaded) return true;
        var loc = _snap.Localization;
        if (loc != null)
        {
            if (!string.IsNullOrWhiteSpace(tpl.LocalizationTitleKey) && loc.TryGetText(tpl.LocalizationTitleKey, out _))
                return true;
            if (!string.IsNullOrWhiteSpace(tpl.TranslationKey) && loc.TryGetText(tpl.TranslationKey, out _))
                return true;
            if (loc is OverlayLocalizationStore ov && ov.ExportMergedOverlay().Document.CardOverrides.ContainsKey(tpl.Id))
                return true;
        }
        if (!string.IsNullOrWhiteSpace(tpl.LocalizationTitleKey) && _zhTranslatedKeys.Contains(tpl.LocalizationTitleKey))
            return true;
        if (!string.IsNullOrWhiteSpace(tpl.TranslationKey) && _zhTranslatedKeys.Contains(tpl.TranslationKey))
            return true;
        return false;
    }

    /// <summary>按模板 ID 判断卡牌是否拥有官方简体中文标题。</summary>
    public bool HasZhTranslation(string? id)
    {
        var tpl = Get(id);
        return tpl != null && HasZhTranslation(tpl);
    }

    /// <summary>全局排除误标 Always 的开发调试/新手教程/剧情伏击废卡（D2-P1-11：解除 3 张官方活跃卡与 1 只野怪误封）。</summary>
    private static readonly HashSet<string> ExcludedDevOrStoryTemplateIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1400ea5b-6492-4c41-9bf9-c7016973ee0a", // Greenheart Help Jules Ambush Giant Mosquito
        "c20ccde2-99be-462c-9b32-8a340ee8b017", // Greenheart Help Merchant Help Kyver Drone
        "85a40ccc-9683-4794-8641-65940237da3a", // [Jungle Expedition] Greenheart Guardian Exit
        "62d59e46-31cc-411d-af27-0f3bf82ca57b", // Big Bad Wolf
        "4cdcc6d3-c0e6-4996-a0ea-c3457211f406", // [DEBUG] Transform
    };

    /// <summary>判断模板是否为必须全局屏蔽的开发调试/教程/剧情残留废卡。</summary>
    public static bool IsExcludedDevOrStoryTemplate(CardTemplate? tpl)
    {
        if (tpl == null) return false;
        if (!string.IsNullOrWhiteSpace(tpl.Id) && ExcludedDevOrStoryTemplateIds.Contains(tpl.Id))
            return true;

        string name = tpl.InternalName ?? "";
        if (string.IsNullOrEmpty(name)) return false;

        return name.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tutorial", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Greenheart Help", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>判断物品模板在指定品阶或起步品阶是否显式配置了 BuyPrice &lt;= 0（如 0 元包裹类）。</summary>
    public static bool HasExplicitZeroOrNegativeBuyPrice(CardTemplate? tpl, string? tier = null)
    {
        if (tpl == null) return false;
        if (!string.IsNullOrWhiteSpace(tier) &&
            tpl.TierAttributes.TryGetValue(tier, out var tierAttrs) &&
            tierAttrs.TryGetValue("BuyPrice", out int tierBuy))
        {
            return tierBuy <= 0;
        }
        if (!string.IsNullOrWhiteSpace(tpl.StartingTier) &&
            tpl.TierAttributes.TryGetValue(tpl.StartingTier, out var startAttrs) &&
            startAttrs.TryGetValue("BuyPrice", out int startBuy))
        {
            return startBuy <= 0;
        }
        return false;
    }

    /// <summary>
    /// 统一校验卡牌是否允许进入面向玩家的生成池：
    /// 资产白名单通过 + 非 Never（随机池要求 Always）+ 排除开发/剧情废卡 + 面向玩家卡种已具备简体中文译名。
    /// </summary>
    public bool IsPlayerSpawnable(CardTemplate? tpl, bool allowGuidOnly = false)
    {
        if (tpl == null) return false;
        if (!IsAssetValid(tpl.Id)) return false;
        // D2-P2-02：黑名单（开发/剧情废卡）必须永远优先拦截；SpawningEligibility == "Never" 对全部真实卡与非 EncounterStep 合成卡同样硬拦截
        // （仅测试合成 EncounterStep 在显式 ID 查找 allowGuidOnly 路径下按需放行，与官方库 0 张真实 Never EncounterStep 零冲突）
        if (IsExcludedDevOrStoryTemplate(tpl))
            return false;
        bool isSyntheticEncounterStep = allowGuidOnly
            && tpl.IsSyntheticTestTemplate
            && string.Equals(tpl.Type, "EncounterStep", StringComparison.OrdinalIgnoreCase);
        if (!isSyntheticEncounterStep && string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase))
            return false;
        // 单元测试合成覆盖卡（无官方本地化键且非 DEBUG）仅在显式 ID 查找路径下放行，随机抽卡池依旧严守 Always 与汉化校验
        if (allowGuidOnly && tpl.IsSyntheticTestTemplate)
            return true;
        if (!allowGuidOnly && !string.Equals(tpl.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase))
            return false;
        if (ZhTranslationLoaded
            && tpl.Type is "Item" or "Skill" or "EventEncounter" or "EncounterStep" or "PedestalEncounter"
            && !HasZhTranslation(tpl))
            return false;
        return true;
    }

    /// <summary>按模板 ID 校验卡牌是否允许进入面向玩家的生成池。</summary>
    public bool IsPlayerSpawnableId(string? id, bool allowGuidOnly = false)
        => IsPlayerSpawnable(Get(id), allowGuidOnly);

    private static CardTemplate Parse(string id, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            var tiers = new Dictionary<string, Dictionary<string, int>>();
            if (r.TryGetProperty("Tiers", out var tiersEl) && tiersEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var tier in tiersEl.EnumerateObject())
                {
                    var attrs = new Dictionary<string, int>();
                    if (tier.Value.TryGetProperty("Attributes", out var attrEl) &&
                        attrEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var a in attrEl.EnumerateObject())
                        {
                            if (a.Value.ValueKind == JsonValueKind.Number && a.Value.TryGetInt32(out int v))
                            {
                                attrs[a.Name] = v;
                            }
                        }
                    }
                    tiers[tier.Name] = attrs;
                }
            }

            // 战斗遭遇专属字段
            string monsterTemplateId = null;
            int monsterLevel = 0;
            bool sandstormEnabled = true;
            if (r.TryGetProperty("CombatantType", out var ct) && ct.ValueKind == JsonValueKind.Object)
            {
                monsterTemplateId = Str(ct, "MonsterTemplateId");
                monsterLevel = Int(ct, "Level");
            }
            if (r.TryGetProperty("SandstormEnabled", out var ss) &&
                (ss.ValueKind == JsonValueKind.True || ss.ValueKind == JsonValueKind.False))
            {
                sandstormEnabled = ss.GetBoolean();
            }

            CombatRewardData? rewardVictory = null;
            CombatRewardData? rewardDefeat = null;
            int rvGold = 0, rvXp = 0, rdGold = 0, rdXp = 0;
            if (r.TryGetProperty("RewardVictory", out var rv) && rv.ValueKind == JsonValueKind.Object)
            {
                rvGold = Int(rv, "GoldReward");
                rvXp = Int(rv, "ExperienceReward");
                rewardVictory = DeserializeOptional<CombatRewardData>(rv);
            }
            if (r.TryGetProperty("RewardDefeat", out var rd) && rd.ValueKind == JsonValueKind.Object)
            {
                rdGold = Int(rd, "GoldReward");
                rdXp = Int(rd, "ExperienceReward");
                rewardDefeat = DeserializeOptional<CombatRewardData>(rd);
            }

            SelectionContextData? selectionContext = null;
            if (r.TryGetProperty("SelectionContext", out var scEl) && scEl.ValueKind == JsonValueKind.Object)
            {
                selectionContext = DeserializeOptional<SelectionContextData>(scEl);
            }

            var nextEncounters = new List<NextEncounterOption>();
            if (r.TryGetProperty("NextEncounterOnSelection", out var neEl) && neEl.ValueKind == JsonValueKind.Array)
            {
                nextEncounters = DeserializeOptional<List<NextEncounterOption>>(neEl) ?? new List<NextEncounterOption>();
            }

            var selReqs = new List<RuleNode>();
            if (r.TryGetProperty("SelectionRequirements", out var srEl))
            {
                if (srEl.ValueKind == JsonValueKind.Array)
                    selReqs = DeserializeOptional<List<RuleNode>>(srEl) ?? new List<RuleNode>();
                else if (srEl.ValueKind == JsonValueKind.Object)
                {
                    var single = DeserializeOptional<RuleNode>(srEl);
                    if (single != null) selReqs.Add(single);
                }
            }

            RuleNode? behavior = null;
            if (r.TryGetProperty("Behavior", out var behEl) && behEl.ValueKind == JsonValueKind.Object)
                behavior = DeserializeOptional<RuleNode>(behEl);

            RuleNode? selectionCriteria = null;
            if (r.TryGetProperty("SelectionCriteria", out var critEl) && critEl.ValueKind == JsonValueKind.Object)
                selectionCriteria = DeserializeOptional<RuleNode>(critEl);

            string locTitleKey = "";
            if (r.TryGetProperty("Localization", out var locEl) && locEl.ValueKind == JsonValueKind.Object &&
                locEl.TryGetProperty("Title", out var titleEl) && titleEl.ValueKind == JsonValueKind.Object)
            {
                locTitleKey = Str(titleEl, "Key");
            }

            return new CardTemplate
            {
                Id = id,
                InternalName = Str(r, "InternalName"),
                Type = Str(r, "Type"),
                Heroes = StrArray(r, "Heroes"),
                Size = Str(r, "Size", "Medium"),
                StartingTier = Str(r, "StartingTier", "Bronze"),
                Tags = StrArray(r, "Tags"),
                HiddenTags = StrArray(r, "HiddenTags"),
                ArtKey = Str(r, "ArtKey"),
                SpawningEligibility = Str(r, "SpawningEligibility", "Always"),
                LocalizationTitleKey = locTitleKey,
                TranslationKey = Str(r, "TranslationKey"),
                TierAttributes = tiers,
                ExperienceAwardUponSelection = Int(r, "ExperienceAwardUponSelection"),
                ShouldProgressHour = Bool(r, "ShouldProgressHour", true),
                IsReselectable = Bool(r, "IsReselectable", false),
                NewSetIdOnExit = r.TryGetProperty("NewSetIdOnExit", out var nse) && nse.ValueKind == JsonValueKind.String
                    ? nse.GetString() : null,
                BoardOverride = r.TryGetProperty("BoardOverride", out var bo) && bo.ValueKind == JsonValueKind.String
                    ? bo.GetString() : null,
                Presentation = r.TryGetProperty("Presentation", out var pres) && pres.ValueKind == JsonValueKind.String
                    ? pres.GetString() : null,
                SelectionContext = selectionContext,
                NextEncounterOnSelection = nextEncounters,
                SelectionRequirements = selReqs,
                IsSingleSpawn = Bool(r, "IsSingleSpawn", false),
                IsTakeover = Bool(r, "IsTakeover", false),
                SpawningChance = Double(r, "SpawningChance", 0.0),
                SpawningChanceIncrement = Double(r, "SpawningChanceIncrement", 0.0),
                Behavior = behavior,
                SelectionCriteria = selectionCriteria,

                MonsterTemplateId = monsterTemplateId,
                MonsterLevel = monsterLevel,
                RewardCombatGold = Int(r, "RewardCombatGold"),
                RewardCombatXp = Int(r, "RewardCombatXp"),
                SandstormEnabled = sandstormEnabled,
                RewardVictoryGold = rvGold,
                RewardVictoryXp = rvXp,
                RewardDefeatGold = rdGold,
                RewardDefeatXp = rdXp,
                RewardVictory = rewardVictory,
                RewardDefeat = rewardDefeat,
            };
        }
        catch
        {
            return null;
        }
    }

    private static T? DeserializeOptional<T>(JsonElement el) where T : class
    {
        try
        {
            return el.Deserialize<T>(GameDatabaseLoader.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement r, string name, string fallback = "")
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static int Int(JsonElement r, string name, int fallback = 0)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : fallback;

    private static double Double(JsonElement r, string name, double fallback = 0.0)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : fallback;

    private static bool Bool(JsonElement r, string name, bool fallback = false)
        => r.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    private static string[] StrArray(JsonElement r, string name)
    {
        if (!r.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString());
        }
        return list.ToArray();
    }

    // ── 查询 ────────────────────────────────────────────────────────────────

    /// <summary>按模板 ID 查询（包含系统特殊占位卡如 Hour6PvpCardId；需要校验本地资产时请配合 IsAssetValid）。</summary>
    public CardTemplate Get(string id)
        => !string.IsNullOrWhiteSpace(id) && _snap.ById.TryGetValue(id, out var t) ? t : null;

    /// <summary>仅当模板存在且通过客户端 Addressables 资产白名单校验时返回。</summary>
    public CardTemplate? GetIfAssetValid(string id)
        => IsAssetValid(id) ? Get(id) : null;

    /// <summary>按类型查询卡牌（若启用资产白名单则自动过滤无本地资产卡牌）。</summary>
    public IEnumerable<CardTemplate> OfType(string type)
    {
        if (!_snap.ByType.TryGetValue(type, out var list)) return Enumerable.Empty<CardTemplate>();
        return WhitelistEnabled ? list.Where(t => IsAssetValid(t.Id)) : list;
    }

    /// <summary>
    /// 商店可售物品池：Type=Item，SpawningEligibility=Always，通过资产白名单与中文词库校验。
    /// D2-P1-12：默认排除 136 件纯原版 Common 非商店野怪/Loot 物品（保留通用自定义 Mod 物品），
    /// 仅当 <paramref name="includeCommon"/> 为 true 时才显式放行纯 Common 中立池。
    /// </summary>
    public List<CardTemplate> ShopItems(string hero, bool includeCommon = false)
        => OfType("Item")
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                     || (includeCommon && t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
                     || (t.IsCustomModCard && t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)
                         && !t.Tags.Contains("Loot", StringComparer.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>遭遇池：EventEncounter / CombatEncounter / PedestalEncounter（自动过滤缺失资产/Never/未汉化卡）。</summary>
    public List<CardTemplate> Encounters(string hero, string kind)
        => OfType(kind)
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: true))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// 每小时随机可抽取的常规遭遇池：仅包含 SpawningEligibility=Always、通过资产白名单与中文词库校验、
    /// 且排除 Tutorial / DEFUNCT / [DEBUG] / (Level Up) / (Start Run) / (Start Skill) / Greenheart 的遭遇。
    /// </summary>
    public List<CardTemplate> HourlyEncounters(string hero, string kind)
    {
        var snap = _snap;
        string cacheKey = $"{hero}|{kind}";
        var cached = snap.HourlyEncountersCache.GetOrAdd(cacheKey, _ =>
            OfType(kind)
                .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
                .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
                .Where(t => !IsNonHourlySystemEncounter(t))
                .ToList());
        return new List<CardTemplate>(cached);
    }

    public static bool IsNonHourlySystemEncounter(CardTemplate t)
    {
        if (IsExcludedDevOrStoryTemplate(t))
            return true;

        if (!string.IsNullOrWhiteSpace(t.Id) && ExcludedRetiredOrNonHourlyEncounterIds.Contains(t.Id))
            return true;

        string name = t.InternalName ?? "";
        if (name.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Level Up)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Run", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Skill", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Greenheart", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Trophy Hunt", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // V13.1 问题 #2：彻底剔除 8 个 Partner Portal - Forja 子基座与 12 座 Lv10/Lv19/Fates 定向附魔祭坛，
        // 使常规白昼小时 PedestalEncounter 仅保留官方 4 座地图基座（B1&B2 / Sterling / Forja / The Artist）。
        if (string.Equals(t.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
        {
            if (name.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrWhiteSpace(t.Id) && NonHourlyMilestoneEnchantPedestalIds.Contains(t.Id))
                return true;
            if (NonHourlyMilestoneEnchantPedestalNames.Contains(name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 统一拦截禁止通过非显式 IdList 动态查询（如 Aerodrome / Mysterious Portal / Street Festival）抽出的
    /// 已淘汰、限时活动残留、开发调试、开局/升级专属与远征子节点遭遇。
    /// </summary>
    public static bool IsExcludedFromDynamicEncounterPool(string? id, string? internalName, CardTemplate? tpl = null)
    {
        if (tpl != null && (IsExcludedDevOrStoryTemplate(tpl) || IsNonHourlySystemEncounter(tpl)))
            return true;

        if (!string.IsNullOrWhiteSpace(id))
        {
            if (ExcludedDevOrStoryTemplateIds.Contains(id) ||
                ExcludedRetiredOrNonHourlyEncounterIds.Contains(id) ||
                ExcludedQuestRemnantEncounterIds.Contains(id) ||
                id.Equals(CardArchetypeIndex.FuturaEncounterId, StringComparison.OrdinalIgnoreCase) ||
                id.Equals(SproutEncounterId, StringComparison.OrdinalIgnoreCase) ||
                IsExpeditionRoomTemplate(id) ||
                NonHourlyMilestoneEnchantPedestalIds.Contains(id))
            {
                return true;
            }
        }

        string name = !string.IsNullOrWhiteSpace(internalName) ? internalName : (tpl?.InternalName ?? string.Empty);
        if (string.IsNullOrEmpty(name))
            return false;

        return name.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("[DEBUG]", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Tutorial", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Level Up)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Run", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Skill", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Expedition", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Fates", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Greenheart", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Trophy Hunt", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase)
            || NonHourlyMilestoneEnchantPedestalNames.Contains(name);
    }

    /// <summary>
    /// 技能池：Type=Skill（通过资产白名单与中文词库校验）。
    /// V13.1 问题 #5：官方全职业通用技能已在 Heroes 中显式列全全部 8 英雄，
    /// 而 Heroes==["Common"] 的 124 张活跃技能中 123 张为野怪专属技能；
    /// P0-23：默认隔离原版 Common 野怪技能，但始终放行通用自定义 Mod 技能（IsCustomModCard &amp;&amp; Common 或 IsUniversalCustomSkill）。
    /// </summary>
    public List<CardTemplate> Skills(string hero, bool includeMonsterCommon = false)
        => OfType("Skill")
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)
                     || t.IsUniversalCustomSkill
                     || ((includeMonsterCommon || t.IsCustomModCard) && t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase)))
            .ToList();

    // ── 抽样 ────────────────────────────────────────────────────────────────

    /// <summary>判断模板 ID 是否为官方 Expedition 门票物品。</summary>
    public static bool IsExpeditionTicketTemplate(string? templateId)
        => !string.IsNullOrWhiteSpace(templateId) && ExpeditionTicketToRoomMap.ContainsKey(templateId);

    /// <summary>判断模板 ID 是否为官方 Expedition 房间遭遇。</summary>
    public static bool IsExpeditionRoomTemplate(string? encounterTemplateId)
        => !string.IsNullOrWhiteSpace(encounterTemplateId) && ExpeditionRoomToTicketMap.ContainsKey(encounterTemplateId);

    /// <summary>根据门票模板 ID 映射对应的 Expedition 房间遭遇 ID。</summary>
    public static bool TryResolveExpeditionRoomForTicket(string? ticketTemplateId, out string? roomEncounterId)
    {
        if (!string.IsNullOrWhiteSpace(ticketTemplateId) &&
            ExpeditionTicketToRoomMap.TryGetValue(ticketTemplateId, out var roomId))
        {
            roomEncounterId = roomId;
            return true;
        }
        roomEncounterId = null;
        return false;
    }

    /// <summary>根据 Expedition 房间遭遇 ID 反查所需门票模板 ID。</summary>
    public static bool TryResolveTicketForExpeditionRoom(string? roomEncounterId, out string? ticketTemplateId)
    {
        if (!string.IsNullOrWhiteSpace(roomEncounterId) &&
            ExpeditionRoomToTicketMap.TryGetValue(roomEncounterId, out var ticketId))
        {
            ticketTemplateId = ticketId;
            return true;
        }
        ticketTemplateId = null;
        return false;
    }

    /// <summary>P1-25：返回官方三张探险门票对应的探险开放日（CrashSite=6 / Jungle=9 / TinyWorld=10，非门票返回 0）。</summary>
    public static int GetScheduledDayForExpeditionTicket(string? ticketTemplateId)
    {
        if (string.IsNullOrWhiteSpace(ticketTemplateId)) return 0;
        if (ticketTemplateId.Equals(CrashSiteExpeditionTicketId, StringComparison.OrdinalIgnoreCase)) return 6;
        if (ticketTemplateId.Equals(JungleExpeditionTicketId, StringComparison.OrdinalIgnoreCase)) return 9;
        if (ticketTemplateId.Equals(TinyWorldExpeditionTicketId, StringComparison.OrdinalIgnoreCase)) return 10;
        return 0;
    }

    /// <summary>
    /// P1-25：判断探险门票在给定 (day, hour) 下是否已过期（官方门票仅在对应探险日 Hour 1 开放，
    /// 因此未过期条件严格为 <c>day &lt; scheduledDay || (day == scheduledDay &amp;&amp; hour == 0)</c>）。
    /// </summary>
    public static bool IsExpeditionTicketExpired(string? ticketTemplateId, int day, int hour)
    {
        int scheduledDay = GetScheduledDayForExpeditionTicket(ticketTemplateId);
        if (scheduledDay <= 0) return false;
        return !(day < scheduledDay || (day == scheduledDay && hour == 0));
    }

    /// <summary>P0-15：返回当前英雄全部合法的 GuidOnly + IsTakeover 专属传奇单次奇遇。</summary>
    public List<CardTemplate> GetHeroTakeoverEncounters(string hero)
    {
        var snap = _snap;
        var cached = snap.HeroTakeoverCache.GetOrAdd(hero, h =>
            OfType("EventEncounter")
                .Where(t => t.IsTakeover
                         && string.Equals(t.SpawningEligibility, "GuidOnly", StringComparison.OrdinalIgnoreCase)
                         && !t.Id.Equals(StarChartTreasureEncounterId, StringComparison.OrdinalIgnoreCase)
                         && IsPlayerSpawnable(t, allowGuidOnly: true)
                         && !IsNonHourlySystemEncounter(t)
                         && t.Heroes.Contains(h, StringComparer.OrdinalIgnoreCase)
                         && !t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
                .ToList());
        return new List<CardTemplate>(cached);
    }

    /// <summary>Star Chart Treasure（34c27d0d）经 Patch 20.2 多源核验确认已淘汰（0 样本，现行 Star Chart 联动位于 The Docks），不再主动触发。</summary>
    public CardTemplate? GetStarChartTreasureEncounter() => null;

    /// <summary>P0-16：获取当前卡池快照对应的数据驱动 GuidOnly 白昼奇遇目录（按需构建并随快照缓存）。</summary>
    public GuidOnlyHourlyEncounterCatalog GetGuidOnlyHourlyCatalog()
    {
        var snap = _snap;
        var existing = snap.GuidOnlyCatalog;
        if (existing != null) return existing;

        var built = GuidOnlyHourlyEncounterCatalog.Build(this, snap.All, snap.ReferencedCardIds);
        snap.GuidOnlyCatalog = built;
        return built;
    }

    private static readonly Regex GuidRefRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static void CollectJsonGuidRefs(string? json, string? selfId, HashSet<string> referencedIds)
    {
        if (string.IsNullOrEmpty(json)) return;
        foreach (Match m in GuidRefRegex.Matches(json))
        {
            string g = m.Value;
            if (!string.IsNullOrEmpty(selfId) && g.Equals(selfId, StringComparison.OrdinalIgnoreCase))
                continue;
            referencedIds.Add(g);
        }
    }

    private static HashSet<string> CollectRepositoryReferencedCardIds(GameDataRepository repo)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCardRef(string? candidate, string selfId, GameDataRepository r, HashSet<string> sink)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (candidate.Equals(selfId, StringComparison.OrdinalIgnoreCase)) return;
            // D3-P2-07：除 36 位标准 GUID 外，兼容非 36 位逻辑 ID（如 Mod 自定义卡 ID）只要在 repo.CardsById 登记即纳入引用集
            bool isGuid = candidate.Length == 36 && Guid.TryParse(candidate, out _);
            if (isGuid || r.CardsById.ContainsKey(candidate))
                sink.Add(candidate);
        }

        // D2-P2-01：补扫全库 LevelUps 奖励树与 Monsters 装备/技能/插槽引用
        foreach (var lv in repo.LevelUps.Values)
        {
            if (lv?.Rewards != null)
                CollectRuleNodeGuidRefs(lv.Rewards, string.Empty, repo, refs);
        }

        foreach (var m in repo.MonstersById.Values)
        {
            if (m?.Player == null) continue;
            if (m.Player.Hand?.Items != null)
                foreach (var it in m.Player.Hand.Items) AddCardRef(it.TemplateId, string.Empty, repo, refs);
            if (m.Player.Stash?.Items != null)
                foreach (var it in m.Player.Stash.Items) AddCardRef(it.TemplateId, string.Empty, repo, refs);
            if (m.Player.Skills != null)
                foreach (var sk in m.Player.Skills) AddCardRef(sk.TemplateId, string.Empty, repo, refs);
            foreach (var se in m.Player.GetEffectiveSocketEffects())
                AddCardRef(se.TemplateId, string.Empty, repo, refs);
        }

        foreach (var card in repo.CardsById.Values)
        {
            if (card == null) continue;
            string selfId = card.Id;
            AddCardRef(card.NewSetIdOnExit, selfId, repo, refs);
            if (card.Behavior != null)
                CollectRuleNodeGuidRefs(card.Behavior, selfId, repo, refs);
            if (card.SelectionCriteria != null)
                CollectRuleNodeGuidRefs(card.SelectionCriteria, selfId, repo, refs);
            if (card.SelectionRequirements != null)
                CollectRuleNodeGuidRefs(card.SelectionRequirements, selfId, repo, refs);
            if (card.CombatantType != null)
                CollectRuleNodeGuidRefs(card.CombatantType, selfId, repo, refs);
            if (card.SelectionContext?.SpawnContext != null)
                CollectRuleNodeGuidRefs(card.SelectionContext.SpawnContext, selfId, repo, refs);
            if (card.SelectionContext?.Rules?.NextEncounterOnExit is { } exitElem)
            {
                CollectJsonGuidRefs(exitElem.GetRawText(), selfId, refs);
            }
            if (card.NextEncounterOnSelection != null)
            {
                foreach (var opt in card.NextEncounterOnSelection)
                    AddCardRef(opt.Id, selfId, repo, refs);
            }
            if (card.RewardVictory?.SpawnContext != null)
                CollectRuleNodeGuidRefs(card.RewardVictory.SpawnContext, selfId, repo, refs);
            if (card.RewardDefeat?.SpawnContext != null)
                CollectRuleNodeGuidRefs(card.RewardDefeat.SpawnContext, selfId, repo, refs);
            if (card.Abilities != null)
            {
                foreach (var ab in card.Abilities.Values)
                {
                    CollectRuleNodeGuidRefs(ab.Trigger, selfId, repo, refs);
                    CollectRuleNodeGuidRefs(ab.Action, selfId, repo, refs);
                    if (ab.Prerequisites != null)
                    {
                        foreach (var p in ab.Prerequisites)
                            CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                    }
                }
            }
            if (card.Auras != null)
            {
                foreach (var au in card.Auras.Values)
                {
                    CollectRuleNodeGuidRefs(au.Action, selfId, repo, refs);
                    if (au.Prerequisites != null)
                    {
                        foreach (var p in au.Prerequisites)
                            CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                    }
                }
            }
            if (card.Enchantments != null)
            {
                foreach (var ench in card.Enchantments.Values)
                {
                    if (ench.Abilities != null)
                    {
                        foreach (var ab in ench.Abilities.Values)
                        {
                            CollectRuleNodeGuidRefs(ab.Trigger, selfId, repo, refs);
                            CollectRuleNodeGuidRefs(ab.Action, selfId, repo, refs);
                            if (ab.Prerequisites != null)
                                foreach (var p in ab.Prerequisites) CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                        }
                    }
                    if (ench.Auras != null)
                    {
                        foreach (var au in ench.Auras.Values)
                        {
                            CollectRuleNodeGuidRefs(au.Action, selfId, repo, refs);
                            if (au.Prerequisites != null)
                                foreach (var p in au.Prerequisites) CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                        }
                    }
                }
            }
            if (card.Quests != null)
            {
                foreach (var q in card.Quests)
                {
                    if (q.Prerequisites != null)
                        foreach (var p in q.Prerequisites) CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                    if (q.Entries != null)
                    {
                        foreach (var entry in q.Entries)
                        {
                            CollectRuleNodeGuidRefs(entry.Trigger, selfId, repo, refs);
                            if (entry.Prerequisites != null)
                                foreach (var p in entry.Prerequisites) CollectRuleNodeGuidRefs(p, selfId, repo, refs);
                            if (entry.Reward?.Abilities != null)
                            {
                                foreach (var ab in entry.Reward.Abilities.Values)
                                {
                                    CollectRuleNodeGuidRefs(ab.Trigger, selfId, repo, refs);
                                    CollectRuleNodeGuidRefs(ab.Action, selfId, repo, refs);
                                }
                            }
                            if (entry.Reward?.Auras != null)
                            {
                                foreach (var au in entry.Reward.Auras.Values)
                                    CollectRuleNodeGuidRefs(au.Action, selfId, repo, refs);
                            }
                        }
                    }
                }
            }
            if (card.Transform?.Abilities != null)
            {
                foreach (var ab in card.Transform.Abilities.Values)
                {
                    CollectRuleNodeGuidRefs(ab.Trigger, selfId, repo, refs);
                    CollectRuleNodeGuidRefs(ab.Action, selfId, repo, refs);
                }
            }
        }
        return refs;
    }

    private static void CollectRuleNodeGuidRefs(RuleNode? node, string selfId, GameDataRepository repo, HashSet<string> refs, int depth = 0)
    {
        if (node == null || depth > 24) return;

        void AddIfCardRef(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            if (candidate.Equals(selfId, StringComparison.OrdinalIgnoreCase)) return;
            bool isGuid = candidate.Length == 36 && Guid.TryParse(candidate, out _);
            if (isGuid || repo.CardsById.ContainsKey(candidate))
                refs.Add(candidate);
        }

        AddIfCardRef(node.Id);
        AddIfCardRef(node.EncounterId);
        AddIfCardRef(node.MonsterTemplateId);
        if (node.Ids != null)
        {
            foreach (string id in node.Ids) AddIfCardRef(id);
        }
        if (node.EncounterIds != null)
        {
            foreach (string id in node.EncounterIds) AddIfCardRef(id);
        }

        int nextDepth = depth + 1;
        CollectRuleNodeGuidRefs(node.ReferenceValue, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Target, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.TargetPlayer, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.TargetCount, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Source, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Subject, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.SubjectOther, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Duration, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Cost, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.Limit, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.SpawnContext, selfId, repo, refs, nextDepth);
        CollectRuleNodeGuidRefs(node.SelectionContextRules, selfId, repo, refs, nextDepth);

        if (node.Actions != null)
        {
            foreach (var a in node.Actions) CollectRuleNodeGuidRefs(a, selfId, repo, refs, nextDepth);
        }
        if (node.Triggers != null)
        {
            foreach (var t in node.Triggers) CollectRuleNodeGuidRefs(t, selfId, repo, refs, nextDepth);
        }
        if (node.Abilities != null)
        {
            foreach (var ab in node.Abilities.Values)
            {
                CollectRuleNodeGuidRefs(ab.Trigger, selfId, repo, refs, nextDepth);
                CollectRuleNodeGuidRefs(ab.Action, selfId, repo, refs, nextDepth);
            }
        }
        if (node.Groups != null)
        {
            foreach (var g in node.Groups) CollectRuleNodeGuidRefs(g, selfId, repo, refs, nextDepth);
        }
        if (node.Filters != null)
        {
            foreach (var f in node.Filters) CollectRuleNodeGuidRefs(f, selfId, repo, refs, nextDepth);
        }
        if (node.Prerequisites != null)
        {
            foreach (var p in node.Prerequisites) CollectRuleNodeGuidRefs(p, selfId, repo, refs, nextDepth);
        }
        if (node.Behaviors != null)
        {
            foreach (var b in node.Behaviors) CollectRuleNodeGuidRefs(b, selfId, repo, refs, nextDepth);
        }
        if (node.RawConditionsElement.HasValue)
            CollectJsonGuidRefs(node.RawConditionsElement.Value.GetRawText(), selfId, refs);
        if (node.RawConstraintsElement.HasValue)
            CollectJsonGuidRefs(node.RawConstraintsElement.Value.GetRawText(), selfId, refs);
    }

    /// <summary>返回门票对应的有效 Expedition 房间模板（若缺失资产或未找到则返回 null）。</summary>
    public CardTemplate? GetExpeditionRoomForTicket(string? ticketTemplateId)
        => TryResolveExpeditionRoomForTicket(ticketTemplateId, out var roomId) && roomId != null
            ? GetIfAssetValid(roomId)
            : null;

    /// <summary>返回全部通过白名单校验的 Expedition 门票模板。</summary>
    public IReadOnlyList<CardTemplate> GetExpeditionTicketTemplates()
        => ExpeditionTicketToRoomMap.Keys
            .Select(GetIfAssetValid)
            .Where(t => t != null && IsPlayerSpawnable(t, allowGuidOnly: true))
            .ToList()!;

    /// <summary>返回全部通过白名单校验的 Expedition 房间遭遇模板。</summary>
    public IReadOnlyList<CardTemplate> GetExpeditionRoomTemplates()
        => ExpeditionRoomToTicketMap.Keys
            .Select(GetIfAssetValid)
            .Where(t => t != null && IsPlayerSpawnable(t, allowGuidOnly: true))
            .ToList()!;

    /// <summary>查询遭遇模板在 DB 中配置的原始 SelectionRulesData。</summary>
    public SelectionRulesData? GetEncounterSelectionRules(string? encounterTemplateId)
        => !string.IsNullOrWhiteSpace(encounterTemplateId) ? Get(encounterTemplateId)?.SelectionContext?.Rules : null;

    /// <summary>查询商人/遭遇模板在 DB 中配置的原始 RerollRulesData。</summary>
    public RerollRulesData? GetEncounterRerollRules(string? encounterTemplateId)
        => GetEncounterSelectionRules(encounterTemplateId)?.RerollRules;

    /// <summary>
    /// 按「天数 → 品质概率」抽 n 个不重复物品，并可选按 <paramref name="ownedMaxTiers"/>
    /// 屏蔽玩家已拥有 Diamond 满阶或高于当前掷出商店品阶的重复物品。
    /// </summary>
    public List<(CardTemplate Card, string Tier)> RollShopItems(
        string hero, int day, int count, TierChances chances)
        => RollShopItems(hero, day, count, chances, ownedMaxTiers: null);

    public List<(CardTemplate Card, string Tier)> RollShopItems(
        string hero,
        int day,
        int count,
        TierChances chances,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers)
        => RollShopItems(hero, day, count, chances, ownedMaxTiers, archetypeResolver: null, rules: null);

    public List<(CardTemplate Card, string Tier)> RollShopItems(
        string hero,
        int day,
        int count,
        TierChances chances,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules = null,
        Random? rngOverride = null)
    {
        bool isCrossHero = rules?.SpecialModeId == ESpecialModeId.CrossHeroMayhem;
        List<CardTemplate> ownHeroPool = ShopItems(hero);
        List<CardTemplate> crossHeroPool = isCrossHero
            ? OfType("Item")
                .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
                .Where(t => t.Heroes.Any(h => HeroNames.PlayableHeroes.Contains(h, StringComparer.OrdinalIgnoreCase)))
                .ToList()
            : ownHeroPool;
        // D6-P1-13：拆分互斥的非本英雄池，确保 CrossHeroOwnHeroBias 精确控制本英雄 vs 他英雄抽签比例（与 SpawnQueryEngine:403-420 语义一致）
        List<CardTemplate> otherHeroPool = isCrossHero
            ? crossHeroPool.Where(t => !t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase)).ToList()
            : ownHeroPool;

        if (crossHeroPool.Count == 0 && ownHeroPool.Count == 0)
            return new List<(CardTemplate, string)>();

        Dictionary<string, CardTier>? ownedLookup = null;
        if (ownedMaxTiers is { Count: > 0 })
        {
            ownedLookup = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in ownedMaxTiers)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                if (!ownedLookup.TryGetValue(k, out var existing) || v > existing)
                    ownedLookup[k] = v;
            }
        }

        var rng = rngOverride ?? Random.Shared;
        chances.CurrentDay = day;
        var allowedTiers = chances.GetNonZeroTiersForDay(day);
        if (allowedTiers.Count == 0)
            allowedTiers = ["Bronze"];

        double ownBias = isCrossHero ? Math.Clamp(rules!.CrossHeroOwnHeroBias, 0.0, 1.0) : 1.0;
        double highTierBonus = rules?.ShopHighTierBonusPct ?? 0.0;

        var result = new List<(CardTemplate, string)>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            string rolledTier = chances.Roll(rng, highTierBonus);
            var tryOrder = new List<string> { rolledTier };
            foreach (string t in allowedTiers)
            {
                if (!string.Equals(t, rolledTier, StringComparison.OrdinalIgnoreCase))
                    tryOrder.Add(t);
            }

            // D6-P1-13：跨职业混池模式按 ownBias 在互斥的 ownHeroPool / otherHeroPool 之间精确分流
            List<CardTemplate> activePool;
            List<CardTemplate> fallbackPool;
            if (isCrossHero)
            {
                bool pickOwn = ownHeroPool.Count > 0 && (otherHeroPool.Count == 0 || (ownBias > 0.0 && rng.NextDouble() < ownBias));
                activePool = pickOwn ? ownHeroPool : (otherHeroPool.Count > 0 ? otherHeroPool : ownHeroPool);
                fallbackPool = ReferenceEquals(activePool, ownHeroPool)
                    ? (otherHeroPool.Count > 0 ? otherHeroPool : crossHeroPool)
                    : (ownBias > 0.0 ? crossHeroPool : otherHeroPool);
            }
            else
            {
                activePool = ownHeroPool;
                fallbackPool = ownHeroPool;
            }

            CardTemplate? picked = null;
            string pickedTier = rolledTier;
            foreach (string tier in tryOrder)
            {
                int tierOrder = TierOrder(tier);
                CardTier rolledCoreTier = TierHelper.ParseTier(tier);
                var candidates = activePool
                    .Where(c => !used.Contains(c.Id)
                             && TierOrder(c.StartingTier) <= tierOrder
                             && c.TierAttributes.ContainsKey(tier)
                             && !HasExplicitZeroOrNegativeBuyPrice(c, tier)
                             && IsAllowedByOwnedMaxTiers(c.Id, rolledCoreTier, ownedLookup))
                    .ToList();
                if (candidates.Count == 0 && isCrossHero && !ReferenceEquals(activePool, fallbackPool))
                {
                    candidates = fallbackPool
                        .Where(c => !used.Contains(c.Id)
                                 && TierOrder(c.StartingTier) <= tierOrder
                                 && c.TierAttributes.ContainsKey(tier)
                                 && !HasExplicitZeroOrNegativeBuyPrice(c, tier)
                                 && IsAllowedByOwnedMaxTiers(c.Id, rolledCoreTier, ownedLookup))
                        .ToList();
                }
                if (candidates.Count > 0)
                {
                    picked = PickShopCandidate(candidates, archetypeResolver, rules, rng);
                    pickedTier = tier;
                    break;
                }
            }

            if (picked == null) break;
            used.Add(picked.Id);
            result.Add((picked, pickedTier));
        }
        return result;
    }

    private CardTemplate PickShopCandidate(
        List<CardTemplate> candidates,
        Func<string, ulong>? archetypeResolver,
        CustomRunRuleSet? rules,
        Random? rng = null)
        => SpawnQueryEngine.PickWeightedByArchetype(candidates, c => c.Id, rng ?? Random.Shared, archetypeResolver, rules)!;

    private static bool IsAllowedByOwnedMaxTiers(
        string templateId,
        CardTier rolledShopTier,
        Dictionary<string, CardTier>? ownedLookup)
    {
        if (ownedLookup == null || !ownedLookup.TryGetValue(templateId, out var ownedTier))
            return true;
        if (ownedTier >= CardTier.Diamond)
            return false;
        return ownedTier <= rolledShopTier;
    }

    private static int TierOrder(string? tier) => tier switch
    {
        "Silver" => 1,
        "Gold" => 2,
        "Diamond" => 3,
        "Legendary" => 4,
        _ => 0,
    };

    public List<CardTemplate> RollEncounters(string hero, string kind, int count)
    {
        var pool = Encounters(hero, kind);
        if (pool.Count == 0) return new List<CardTemplate>();
        var rng = Random.Shared;
        var used = new HashSet<string>();
        var result = new List<CardTemplate>();
        for (int i = 0; i < count && used.Count < pool.Count; i++)
        {
            CardTemplate pick;
            int guard = 0;
            do
            {
                pick = pool[rng.Next(pool.Count)];
                guard++;
            } while (used.Contains(pick.Id) && guard < 200);
            if (used.Contains(pick.Id)) continue;
            used.Add(pick.Id);
            result.Add(pick);
        }
        return result;
    }

    public CardTemplate? RollWeightedEncounter(
        IEnumerable<CardTemplate> pool,
        ISet<string>? excludedIds = null,
        ISet<string>? visitedSingleSpawnIds = null,
        Random? rng = null)
        => RollWeightedEncounter(pool, excludedIds, visitedSingleSpawnIds, rng, weightMultiplier: null, bannedIds: null);

    public CardTemplate? RollWeightedEncounter(
        IEnumerable<CardTemplate> pool,
        ISet<string>? excludedIds,
        ISet<string>? visitedSingleSpawnIds,
        Random? rng,
        Func<CardTemplate, double>? weightMultiplier,
        ISet<string>? bannedIds = null)
    {
        var candidates = pool
            .Where(t => excludedIds == null || !excludedIds.Contains(t.Id))
            .Where(t => bannedIds == null || !bannedIds.Contains(t.Id))
            .Where(t => !t.IsSingleSpawn || visitedSingleSpawnIds == null || !visitedSingleSpawnIds.Contains(t.Id))
            .ToList();
        if (candidates.Count == 0) return null;

        rng ??= Random.Shared;
        if (weightMultiplier != null)
        {
            var weighted = new List<(CardTemplate Template, double Weight)>(candidates.Count);
            double sum = 0.0;
            foreach (var t in candidates)
            {
                double mult = weightMultiplier(t);
                if (!double.IsFinite(mult) || mult <= 0.0)
                    continue;
                // P2-26：SpawningChance / SpawningChanceIncrement 专用于 IsTakeover 独立掷骰与局内递增表，
                // 不再误作普通池相对权重（防止 Star Chart Treasure 等 0.1 独立概率卡被降权 10 倍）
                double w = mult;
                if (w > 0.0)
                {
                    weighted.Add((t, w));
                    sum += w;
                }
            }

            if (weighted.Count == 0 || sum <= 0.0)
                return null;

            double r = rng.NextDouble() * sum;
            double a = 0.0;
            foreach (var (t, w) in weighted)
            {
                a += w;
                if (r <= a)
                {
                    excludedIds?.Add(t.Id);
                    return t;
                }
            }
            var fallbackLast = weighted[^1].Template;
            excludedIds?.Add(fallbackLast.Id);
            return fallbackLast;
        }

        // P2-26：普通池内各候选遭遇等权抽取，SpawningChance 仅用于 IsTakeover 独立掷骰
        var picked = candidates[rng.Next(candidates.Count)];
        excludedIds?.Add(picked.Id);
        return picked;
    }

    public CardTemplate RollSkill(string hero)
    {
        var pool = Skills(hero);
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }

    public CardTemplate RollMonster(string hero)
    {
        var pool = OfType("CombatEncounter")
            .Where(t => t.SpawningEligibility != "Never")
            .ToList();
        return pool.Count == 0 ? null : pool[Random.Shared.Next(pool.Count)];
    }
}

/// <summary>
/// 品质概率表。优先从 GameData.db 的 game_modes.ItemSkillSpawnTierPercantagesByDay 加载，
/// 并对齐 Patch 20.2 官服按 NumberOfDays（标准局 10 天）钳制 Day 10+ 稳态（Silver 20% / Gold 70% / Diamond 10%）；
/// 若自定义 Mode 配置了 NumberOfDays &gt; 10 与独立高天品阶表，则通过 clampMaxDay 自动放行读取。
/// </summary>
public sealed class TierChances
{
    // [day] -> (Bronze, Silver, Gold, Diamond) 默认兜底表（对齐 Patch 20.2 BazaarDB /shop-odds Day 1–16 实测）
    internal static readonly (double Bronze, double Silver, double Gold, double Diamond)[] DefaultTable =
    {
        (1.00, 0.00, 0.00, 0.00), // 1
        (0.90, 0.10, 0.00, 0.00), // 2
        (0.70, 0.30, 0.00, 0.00), // 3
        (0.50, 0.50, 0.00, 0.00), // 4
        (0.25, 0.75, 0.00, 0.00), // 5
        (0.00, 0.95, 0.05, 0.00), // 6
        (0.00, 0.80, 0.20, 0.00), // 7
        (0.00, 0.60, 0.40, 0.00), // 8
        (0.00, 0.35, 0.60, 0.05), // 9
        (0.00, 0.20, 0.70, 0.10), // 10（Day 10+ 官服实测稳态：Silver 20% / Gold 70% / Diamond 10%）
        (0.00, 0.20, 0.70, 0.10), // 11
        (0.00, 0.20, 0.70, 0.10), // 12
        (0.00, 0.20, 0.70, 0.10), // 13
        (0.00, 0.20, 0.70, 0.10), // 14
        (0.00, 0.20, 0.70, 0.10), // 15
        (0.00, 0.20, 0.70, 0.10), // 16
        (0.00, 0.20, 0.70, 0.10), // 17
        (0.00, 0.20, 0.70, 0.10), // 18
        (0.00, 0.20, 0.70, 0.10), // 19
        (0.00, 0.20, 0.70, 0.10), // 20
    };

    private readonly IReadOnlyList<(double Bronze, double Silver, double Gold, double Diamond)> _table;

    public TierChances() : this(DefaultTable) { }

    public TierChances(IReadOnlyList<(double Bronze, double Silver, double Gold, double Diamond)>? table)
    {
        _table = table is { Count: > 0 } ? table : DefaultTable;
    }

    /// <summary>
    /// 从 GameData.db 的 game_modes.ItemSkillSpawnTierPercantagesByDay 构建每日权重表。
    /// <paramref name="clampMaxDay"/> 默认 10（对应官方 Base GameMode.NumberOfDays=10，使 Day 11+ 收敛于 Day 10 稳态）；
    /// 自定义 Mode 传入其 NumberOfDays（或 ≤0 表示不钳制）即可读取完整自定义高天品阶表。
    /// </summary>
    public static (double Bronze, double Silver, double Gold, double Diamond)[] FromGameModeMap(
        IReadOnlyDictionary<string, Dictionary<string, double>>? map,
        int clampMaxDay = 10)
    {
        if (map == null || map.Count == 0)
            return DefaultTable;

        int maxDay = DefaultTable.Length;
        foreach (string key in map.Keys)
        {
            if (int.TryParse(key, out int d) && d > maxDay && d <= 50)
                maxDay = d;
        }
        if (clampMaxDay > 0 && clampMaxDay > maxDay)
            maxDay = Math.Min(clampMaxDay, 50);

        var rows = new (double Bronze, double Silver, double Gold, double Diamond)[maxDay];
        for (int day = 1; day <= maxDay; day++)
        {
            int lookupDay = clampMaxDay > 0 ? Math.Min(day, clampMaxDay) : day;
            if (map.TryGetValue(lookupDay.ToString(), out var w) && w != null)
            {
                w.TryGetValue("Bronze", out double b);
                w.TryGetValue("Silver", out double s);
                w.TryGetValue("Gold", out double g);
                w.TryGetValue("Diamond", out double dm);
                b = Math.Max(0, b);
                s = Math.Max(0, s);
                g = Math.Max(0, g);
                dm = Math.Max(0, dm);
                // 防呆保留（P2-30）：若自定义 Mode 设 clampMaxDay>=12 并沿用旧 DB Day 12（总和 0.80），平滑补齐 Diamond=0.25
                double rawSum = b + s + g + dm;
                if (lookupDay == 12
                    && Math.Abs(rawSum - 0.80) < 1e-6
                    && Math.Abs(dm - 0.05) < 1e-6)
                {
                    dm = 0.25;
                }
                rows[day - 1] = (b, s, g, dm);
            }
            else
            {
                int fallbackIdx = Math.Clamp(lookupDay, 1, DefaultTable.Length) - 1;
                rows[day - 1] = DefaultTable[fallbackIdx];
            }
        }
        return rows;
    }

    /// <summary>返回指定天数的归一化前原始品阶权重（Bronze, Silver, Gold, Diamond）。</summary>
    public (double Bronze, double Silver, double Gold, double Diamond) GetWeightsForDay(int day)
    {
        int idx = Math.Clamp(day, 1, _table.Count) - 1;
        return _table[idx];
    }

    public string Roll(Random rng, double highTierBonusPct = 0.0)
    {
        var row = GetWeightsForDay(CurrentDay);
        double b = row.Bronze;
        double s = row.Silver;
        double g = row.Gold;
        double d = row.Diamond;

        if (highTierBonusPct > 0.0)
        {
            double boost = 1.0 + Math.Clamp(highTierBonusPct, 0.0, 100.0) / 100.0;
            s *= boost;
            g *= boost;
            d *= boost;
        }

        double total = b + s + g + d;
        if (total <= 0) return "Bronze";

        double r = rng.NextDouble() * total;
        if (r < b) return "Bronze";
        r -= b;
        if (r < s) return "Silver";
        r -= s;
        if (r < g) return "Gold";
        return "Diamond";
    }

    /// <summary>返回指定天数权重严格大于 0 的允许品阶列表。</summary>
    public List<string> GetNonZeroTiersForDay(int day)
    {
        var row = GetWeightsForDay(day);
        var tiers = new List<string>(4);
        if (row.Bronze > 0) tiers.Add("Bronze");
        if (row.Silver > 0) tiers.Add("Silver");
        if (row.Gold > 0) tiers.Add("Gold");
        if (row.Diamond > 0) tiers.Add("Diamond");
        return tiers;
    }

    public int CurrentDay { get; set; } = 1;
}

/// <summary>P0-16：数据驱动的 GuidOnly 白昼奇遇角色分类。</summary>
public enum GuidOnlyEncounterRole
{
    RegularNonMerchant = 0,
    BonusCombatChallenge = 1,
    RareOrSkillMerchant = 2,
}

/// <summary>
/// P0-16：数据驱动的 GuidOnly 白昼遭遇目录。
/// 自动从 DB 聚合：
/// - 41 套（157 张，含 Workshop (Silver)）按品阶/天数拆分的常规白昼奇遇与额外野怪挑战（Deadly Duel / Epic Battle）；
/// - 独立有效特殊事件（Mystery Box / Aerodrome / Sprout / Study / Borrow / Rest / Snack Time / Regenerative Tincture / Bjorn's Gift Exchange / Gulch's Dive / Underground Resistance 等）；
/// - 稀缺/技能商人分流（Statia 3 金重随技能商 / Advanced Training 8 金重随技能商）；
/// 并自动剔除已有 DB 入度引用的子遭遇、Takeover 奇遇、专用调度事件（Travel Agent / The Cult / Mysterious Portal / Futura / Grand Opening）及 2 张任务残留卡。
/// </summary>
public sealed class GuidOnlyHourlyEncounterCatalog
{
    public sealed record TieredSuite(
        string BaseName,
        GuidOnlyEncounterRole Role,
        IReadOnlyDictionary<string, CardPool.CardTemplate> VariantsByTier);

    public sealed record DayRangeVariant(
        int MinDay,
        int MaxDay,
        CardPool.CardTemplate Card);

    public sealed record DayRangeSuite(
        string BaseName,
        GuidOnlyEncounterRole Role,
        IReadOnlyList<DayRangeVariant> Variants);

    public sealed record StandaloneEntry(
        CardPool.CardTemplate Card,
        GuidOnlyEncounterRole Role,
        int MinDay,
        int MaxDay,
        bool ExemptFromMaxDayTier);

    private static readonly Regex TierSuffixRegex = new(
        @"\s*\((Bronze|Silver|Gold|Diamond|Legendary)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DayRangeSuffixRegex = new(
        @"\s*\(Day\s+(\d+)(?:(?:\s*-\s*|\s+to\s+)(\d+)|(\+))?\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] TierOrder = ["Bronze", "Silver", "Gold", "Diamond", "Legendary"];

    public IReadOnlyList<TieredSuite> TieredSuites { get; }
    public IReadOnlyList<DayRangeSuite> DayRangeSuites { get; }
    public IReadOnlyList<StandaloneEntry> StandaloneEntries { get; }
    private readonly ConcurrentDictionary<(string Hero, int Day), List<(CardPool.CardTemplate Card, GuidOnlyEncounterRole Role, bool ExemptFromMaxDayTier)>> _resolveCache = new();

    public int TotalSuiteCount => TieredSuites.Count + DayRangeSuites.Count;
    public int TotalSuiteCardCount =>
        TieredSuites.Sum(s => s.VariantsByTier.Count) + DayRangeSuites.Sum(s => s.Variants.Count);

    private GuidOnlyHourlyEncounterCatalog(
        List<TieredSuite> tieredSuites,
        List<DayRangeSuite> dayRangeSuites,
        List<StandaloneEntry> standaloneEntries)
    {
        TieredSuites = tieredSuites;
        DayRangeSuites = dayRangeSuites;
        StandaloneEntries = standaloneEntries;
    }

    public static GuidOnlyHourlyEncounterCatalog Build(
        CardPool pool,
        IReadOnlyList<CardPool.CardTemplate> allCards,
        IReadOnlySet<string> referencedIds)
    {
        var tieredBuckets = new Dictionary<string, Dictionary<string, CardPool.CardTemplate>>(StringComparer.OrdinalIgnoreCase);
        var dayRangeBuckets = new Dictionary<string, List<DayRangeVariant>>(StringComparer.OrdinalIgnoreCase);
        var standalones = new List<StandaloneEntry>();

        foreach (var card in allCards)
        {
            if (!string.Equals(card.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(card.SpawningEligibility, "GuidOnly", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!pool.IsPlayerSpawnable(card, allowGuidOnly: true))
                continue;
            if (card.IsTakeover)
                continue;
            // 官方大件商人 Pol / Pol (Pygmalien) 为 GuidOnly 且被 Pol's Package / Ask for Help 引用，
            // 须显式豁免入度去重，使其按 Heroes（大猪 Day 4+ / 其余 6 英雄 Day 6+ / 排除 Hero8）正常进入常规左槽商人池。
            bool isOfficialHourlyPolMerchant =
                card.Id.Equals(CardPool.PolEncounterId, StringComparison.OrdinalIgnoreCase) ||
                card.Id.Equals(CardPool.PolPygmalienEncounterId, StringComparison.OrdinalIgnoreCase);
            if (referencedIds.Contains(card.Id) && !isOfficialHourlyPolMerchant)
                continue;
            if (IsExcludedFromGuidOnlyHourlyCatalog(card))
                continue;

            string name = card.InternalName ?? string.Empty;

            var dayMatch = DayRangeSuffixRegex.Match(name);
            if (dayMatch.Success)
            {
                int minDay = int.Parse(dayMatch.Groups[1].Value);
                int maxDay = dayMatch.Groups[2].Success
                    ? int.Parse(dayMatch.Groups[2].Value)
                    : (dayMatch.Groups[3].Success || minDay >= 10 ? int.MaxValue : minDay);
                string baseName = name[..dayMatch.Index].Trim();
                if (!dayRangeBuckets.TryGetValue(baseName, out var list))
                {
                    list = new List<DayRangeVariant>();
                    dayRangeBuckets[baseName] = list;
                }
                list.Add(new DayRangeVariant(minDay, maxDay, card));
                continue;
            }

            var tierMatch = TierSuffixRegex.Match(name);
            if (tierMatch.Success)
            {
                string tierName = NormalizeTierName(tierMatch.Groups[1].Value);
                string baseName = name[..tierMatch.Index].Trim();
                if (!tieredBuckets.TryGetValue(baseName, out var byTier))
                {
                    byTier = new Dictionary<string, CardPool.CardTemplate>(StringComparer.OrdinalIgnoreCase);
                    tieredBuckets[baseName] = byTier;
                }
                byTier[tierName] = card;
                continue;
            }

            // 独立单卡 GuidOnly 特殊事件分类分流
            var role = ClassifyStandaloneRole(card);
            int minD = isOfficialHourlyPolMerchant
                ? (card.Id.Equals(CardPool.PolPygmalienEncounterId, StringComparison.OrdinalIgnoreCase) ? 4 : 6)
                : (role == GuidOnlyEncounterRole.RareOrSkillMerchant ? 6 : 1);
            bool exempt = isOfficialHourlyPolMerchant;
            standalones.Add(new StandaloneEntry(card, role, minD, int.MaxValue, exempt));
        }

        var tieredSuites = new List<TieredSuite>(tieredBuckets.Count);
        foreach (var (baseName, byTier) in tieredBuckets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (byTier.Count >= 2)
            {
                tieredSuites.Add(new TieredSuite(baseName, GuidOnlyEncounterRole.RegularNonMerchant, byTier));
            }
            else
            {
                var single = byTier.Values.First();
                standalones.Add(new StandaloneEntry(single, ClassifyStandaloneRole(single), 1, int.MaxValue, false));
            }
        }

        var dayRangeSuites = new List<DayRangeSuite>(dayRangeBuckets.Count);
        foreach (var (baseName, variants) in dayRangeBuckets.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            variants.Sort((a, b) => a.MinDay.CompareTo(b.MinDay));
            var role = IsBonusCombatChallengeBaseName(baseName)
                ? GuidOnlyEncounterRole.BonusCombatChallenge
                : GuidOnlyEncounterRole.RegularNonMerchant;
            if (variants.Count >= 2)
            {
                dayRangeSuites.Add(new DayRangeSuite(baseName, role, variants));
            }
            else
            {
                var only = variants[0];
                standalones.Add(new StandaloneEntry(only.Card, role, only.MinDay, only.MaxDay, ExemptFromMaxDayTier: true));
            }
        }

        return new GuidOnlyHourlyEncounterCatalog(tieredSuites, dayRangeSuites, standalones);
    }

    /// <summary>
    /// 按当前英雄与有效天数投影出当日匹配的 GuidOnly 候选遭遇（每套按阶事件至多投影最匹配的 1 个单阶版本，绝不重复堆叠）。
    /// </summary>
    public List<(CardPool.CardTemplate Card, GuidOnlyEncounterRole Role, bool ExemptFromMaxDayTier)> ResolveForHeroAndDay(
        string hero,
        int effectiveDay)
    {
        var key = ((hero ?? string.Empty).ToUpperInvariant(), effectiveDay);
        var cached = _resolveCache.GetOrAdd(key, _ => ComputeResolveForHeroAndDay(hero ?? string.Empty, effectiveDay));
        return new List<(CardPool.CardTemplate, GuidOnlyEncounterRole, bool)>(cached);
    }

    private List<(CardPool.CardTemplate Card, GuidOnlyEncounterRole Role, bool ExemptFromMaxDayTier)> ComputeResolveForHeroAndDay(
        string hero,
        int effectiveDay)
    {
        var results = new List<(CardPool.CardTemplate, GuidOnlyEncounterRole, bool)>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool MatchesHero(CardPool.CardTemplate c, string h)
            => c.Heroes.Contains(h, StringComparer.OrdinalIgnoreCase)
            || c.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase);

        // 1. 按阶拆分常规白昼右槽免费物品遭遇（Patch 16.0 Right Side Options）：
        //    按 BazaarDB Patch 20.2 实测分阶窗口精准投射唯一最匹配品阶版本，并豁免中槽 maxDayTier 误杀。
        foreach (var suite in TieredSuites)
        {
            bool silverStartsDay1 = OfficialEncounterSchedule.IsSilverDay1RightFreeSuite(suite.BaseName);
            int targetTierIdx = silverStartsDay1
                ? effectiveDay switch
                {
                    <= 4 => 1, // Silver (Days 1–5 实测)
                    <= 7 => 2, // Gold   (Days 5–10+ 实测)
                    _ => 3,    // Diamond (Days 8–10+ 实测)
                }
                : effectiveDay switch
                {
                    <= 2 => 0, // Bronze (Days 1–2 实测)
                    <= 5 => 1, // Silver (Days 2–6 实测)
                    <= 8 => 2, // Gold   (Days 6–10+ 实测)
                    _ => 3,    // Diamond (Days 9–10+ 实测)
                };

            CardPool.CardTemplate? chosen = null;
            for (int tIdx = targetTierIdx; tIdx >= 0; tIdx--)
            {
                if (suite.VariantsByTier.TryGetValue(TierOrder[tIdx], out var cand) &&
                    MatchesHero(cand, hero) &&
                    OfficialEncounterSchedule.IsWithinWindow(cand, effectiveDay, hour: 1, hero))
                {
                    chosen = cand;
                    break;
                }
            }
            // 若该套奇遇起阶高于 Bronze（如 Freezer Day 2+ / Dino Trapping Day 3+ 仅含 Silver/Gold/Diamond），
            // 在满足官方 MinDay 窗口的前提下放行最低可用阶
            if (chosen == null)
            {
                for (int tIdx = targetTierIdx + 1; tIdx < TierOrder.Length; tIdx++)
                {
                    if (suite.VariantsByTier.TryGetValue(TierOrder[tIdx], out var cand) &&
                        MatchesHero(cand, hero) &&
                        OfficialEncounterSchedule.IsWithinWindow(cand, effectiveDay, hour: 1, hero))
                    {
                        if (tIdx <= targetTierIdx + 1)
                            chosen = cand;
                        break;
                    }
                }
            }
            if (chosen != null && seenIds.Add(chosen.Id))
            {
                // Patch 16.0：右槽免费物品事件自带品阶且平均高于同天商店，标记 ExemptFromMaxDayTier=true
                results.Add((chosen, suite.Role, true));
            }
        }

        // 2. 分天拆分白昼奇遇（Invest in Yourself / Deadly Duel / Epic Battle）：严格按 (MinDay..MaxDay) 精确匹配
        foreach (var suite in DayRangeSuites)
        {
            foreach (var v in suite.Variants)
            {
                if (effectiveDay >= v.MinDay && effectiveDay <= v.MaxDay && MatchesHero(v.Card, hero))
                {
                    if (seenIds.Add(v.Card.Id))
                    {
                        // 已由 (Day X-Y) 精确门控（如 Day 1-2 银阶 Invest in Yourself、Day 5-7 金阶 Deadly Duel），豁免 maxDayTier 误杀
                        results.Add((v.Card, suite.Role, true));
                    }
                }
            }
        }

        // 3. 独立有效特殊事件（含 Statia / Advanced Training / Borrow (Day 1-2) / Mystery Box / Study / Snack Time / Rest 等）
        foreach (var entry in StandaloneEntries)
        {
            if (effectiveDay < entry.MinDay || effectiveDay > entry.MaxDay)
                continue;
            if (!MatchesHero(entry.Card, hero))
                continue;
            if (seenIds.Add(entry.Card.Id))
            {
                results.Add((entry.Card, entry.Role, entry.ExemptFromMaxDayTier));
            }
        }

        return results;
    }

    private static bool IsExcludedFromGuidOnlyHourlyCatalog(CardPool.CardTemplate card)
    {
        if (CardPool.IsExcludedFromDynamicEncounterPool(card.Id, card.InternalName, card))
            return true;
        if (card.Id.Equals(CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase) ||
            card.Id.Equals(CardArchetypeIndex.TheCultEncounterId, StringComparison.OrdinalIgnoreCase) ||
            card.Id.Equals(CardArchetypeIndex.MysteriousPortalEncounterId, StringComparison.OrdinalIgnoreCase) ||
            card.Id.Equals(CardPool.GrandOpeningEncounterId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static GuidOnlyEncounterRole ClassifyStandaloneRole(CardPool.CardTemplate card)
    {
        if (card.Id.Equals(CardArchetypeIndex.AdvancedTrainingEncounterId, StringComparison.OrdinalIgnoreCase))
        {
            return GuidOnlyEncounterRole.RareOrSkillMerchant;
        }
        return GuidOnlyEncounterRole.RegularNonMerchant;
    }

    private static bool IsBonusCombatChallengeBaseName(string baseName)
        => baseName.Equals("Deadly Duel", StringComparison.OrdinalIgnoreCase)
        || baseName.Equals("Epic Battle", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTierName(string raw)
        => raw.ToLowerInvariant() switch
        {
            "bronze" => "Bronze",
            "silver" => "Silver",
            "gold" => "Gold",
            "diamond" => "Diamond",
            "legendary" => "Legendary",
            _ => raw,
        };
}

using System.Collections.Frozen;
using System.Text.Json;
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
        /// <summary>是否为 Mod 自定义卡。</summary>
        public bool IsCustomModCard => !string.IsNullOrWhiteSpace(BaseVisualTemplateId);
    }

    public const string TravelAgentEncounterId = "002d8f6d-0e36-4921-89ed-3f0f28682d7b";
    public const string JungleExpeditionTicketId = "77c8ef2d-62c8-4214-993d-648915c93e83";
    public const string CrashSiteExpeditionTicketId = "5bd0a69d-420c-4cd0-93ec-07582c688a5b";
    public const string TinyWorldExpeditionTicketId = "24dc340b-bb3d-4c9b-8715-a807c0c06a8c";
    public const string JungleExpeditionRoomId = "91287d25-7f63-4826-8c42-06ed1bf83213";
    public const string CrashSiteExpeditionRoomId = "ead0a1ed-703e-445e-ba43-9a7df5d64f54";
    public const string TinyWorldExpeditionRoomId = "42db3205-58d5-4cfe-b537-14091b518ff6";

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
            pool.TierChancesByDay = TierChances.FromGameModeMap(repo.GameMode.ItemSkillSpawnTierPercantagesByDay);
        if (repo.GameMode.StandardPrices != null)
            pool.StandardPrices = repo.GameMode.StandardPrices;

        pool.LoadedFrom = repo.SourceDataPath ?? repo.SourceZipPath ?? "<repository>";
        pool.ApplyRepositoryOverlay(repo, visualBindings);
        Console.WriteLine(
            $"[CardPool] (shared repo) mapped {pool.TotalRawCards} raw cards ({pool.TotalCards} asset-valid, whitelist={pool.WhitelistEnabled})");
        return pool;
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
                if (!string.IsNullOrWhiteSpace(cid) && !string.IsNullOrWhiteSpace(b.BaseVisualTemplateId))
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

        _snap = new PoolSnapshot
        {
            All = all,
            ById = byId,
            ByType = byType,
            CustomToBaseVisualId = customMap,
            AssetValidCount = validCount,
            Localization = activeRepo.Localization,
        };
    }

    /// <summary>解析自定义卡对应的合法原版视觉载体 GUID；若本身即为原版 GUID 则原样返回。</summary>
    public string ResolveSafeWireTemplateId(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return string.Empty;
        var snap = _snap;
        if (snap.CustomToBaseVisualId.TryGetValue(templateId, out var baseId) && !string.IsNullOrWhiteSpace(baseId))
            return baseId;
        if (snap.ById.TryGetValue(templateId, out var tpl) && !string.IsNullOrWhiteSpace(tpl.BaseVisualTemplateId))
            return tpl.BaseVisualTemplateId!;
        return templateId;
    }

    /// <summary>构建包含当前白名单与全部已启用 Mod 自定义卡 ID 的合并有效资产集（供 SpawnQueryEngine 放行 Mod 卡）。</summary>
    public IReadOnlySet<string>? GetEffectiveValidAssetIds()
    {
        if (!WhitelistEnabled) return null;
        var snap = _snap;
        if (snap.CustomToBaseVisualId.Count == 0)
            return _validCardIds;

        var merged = new HashSet<string>(_validCardIds, StringComparer.OrdinalIgnoreCase);
        foreach (var (customId, baseId) in snap.CustomToBaseVisualId)
        {
            if (_validCardIds.Contains(baseId))
                merged.Add(customId);
        }
        return merged;
    }

    private static CardTemplate MapFromCoreTemplate(
        BazaarCore.Models.CardTemplate core,
        CustomCardVisualBinding? binding)
    {
        var tiers = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        if (core.Tiers != null)
        {
            foreach (var (tierName, tierData) in core.Tiers)
            {
                var attrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (tierData?.Attributes != null)
                {
                    foreach (var (ak, av) in tierData.Attributes)
                        attrs[ak] = (int)Math.Round(av);
                }
                tiers[tierName] = attrs;
            }
        }
        else if (core.Attributes is { Count: > 0 })
        {
            var attrs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ak, av) in core.Attributes)
                attrs[ak] = (int)Math.Round(av);
            tiers[string.IsNullOrWhiteSpace(core.StartingTier) ? "Bronze" : core.StartingTier] = attrs;
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
            ModId = binding?.ModId ?? (core.Version?.StartsWith("mod:", StringComparison.OrdinalIgnoreCase) == true ? core.Version[4..] : null),
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
                                pool.TierChancesByDay = TierChances.FromGameModeMap(gmData.ItemSkillSpawnTierPercantagesByDay);
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
        int validCount = 0;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Data FROM cards";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            byte[] blob = (byte[])reader.GetValue(1);
            string json = System.Text.Encoding.UTF8.GetString(blob);
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

        pool._snap = new PoolSnapshot
        {
            All = all,
            ById = byId,
            ByType = byType,
            CustomToBaseVisualId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            AssetValidCount = validCount,
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

    /// <summary>判断卡牌 ID 是否具备客户端 Addressables 本地资产（Mod 自定义卡校验其 BaseVisualTemplateId 是否位于白名单）。</summary>
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

    /// <summary>全局排除误标 Always 的开发调试/新手教程/剧情伏击废卡。</summary>
    private static readonly HashSet<string> ExcludedDevOrStoryTemplateIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "1400ea5b-6492-4c41-9bf9-c7016973ee0a", // Greenheart Help Jules Ambush Giant Mosquito
        "c20ccde2-99be-462c-9b32-8a340ee8b017", // Greenheart Help Merchant Help Kyver Drone
        "85a40ccc-9683-4794-8641-65940237da3a", // [Jungle Expedition] Greenheart Guardian Exit
        "fd862d85-66cd-431f-a85a-07742cbc2e1b", // Test Subject Alpha (Monster)
        "071cf90c-57a4-40f2-8d79-4c87f0d100d4", // Test Subject Alpha
        "41cdf4ee-7790-4da1-96f8-a46d3391a0d9", // The Tester
        "47704642-ae7a-4775-a26a-e5e5a8cff7bf", // Eating Contest
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
            || name.StartsWith("Greenheart Help", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Test Subject Alpha", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "The Tester", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Eating Contest", StringComparison.OrdinalIgnoreCase);
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
        // 单元测试合成覆盖卡（无官方本地化键且非 DEBUG）仅在显式 ID 查找路径下放行，随机抽卡池依旧严守 Always 与汉化校验
        if (allowGuidOnly && tpl.IsSyntheticTestTemplate)
            return true;
        if (IsExcludedDevOrStoryTemplate(tpl))
            return false;
        if (string.Equals(tpl.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase)) return false;
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
    /// 商店可售物品池：Type=Item，SpawningEligibility=Always，属于该英雄或 Common，且通过资产白名单与中文词库校验。
    /// </summary>
    public List<CardTemplate> ShopItems(string hero)
        => OfType("Item")
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
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
        => OfType(kind)
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
            .Where(t => !IsNonHourlySystemEncounter(t))
            .ToList();

    private static bool IsNonHourlySystemEncounter(CardTemplate t)
    {
        if (IsExcludedDevOrStoryTemplate(t))
            return true;

        string name = t.InternalName ?? "";
        return name.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Level Up)", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Run", StringComparison.OrdinalIgnoreCase)
            || name.Contains("(Start Skill", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Greenheart", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>技能池：Type=Skill（通过资产白名单与中文词库校验）。</summary>
    public List<CardTemplate> Skills(string hero)
        => OfType("Skill")
            .Where(t => IsPlayerSpawnable(t, allowGuidOnly: false))
            .Where(t => t.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) || t.Heroes.Contains("Common", StringComparer.OrdinalIgnoreCase))
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

            // 跨职业混池模式：按 40% 本英雄保底 + 60% 全英雄混池分流抽选
            var activePool = isCrossHero && ownHeroPool.Count > 0 && rng.NextDouble() < ownBias
                ? ownHeroPool
                : (crossHeroPool.Count > 0 ? crossHeroPool : ownHeroPool);

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
                if (candidates.Count == 0 && isCrossHero && !ReferenceEquals(activePool, crossHeroPool))
                {
                    candidates = crossHeroPool
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
                double baseW = t.SpawningChance > 0 ? t.SpawningChance : 1.0;
                double w = baseW * mult;
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

        double totalWeight = candidates.Sum(t => t.SpawningChance > 0 ? t.SpawningChance : 1.0);
        if (totalWeight <= 0)
        {
            var pickedFallback = candidates[rng.Next(candidates.Count)];
            excludedIds?.Add(pickedFallback.Id);
            return pickedFallback;
        }

        double roll = rng.NextDouble() * totalWeight;
        double acc = 0;
        foreach (var t in candidates)
        {
            acc += t.SpawningChance > 0 ? t.SpawningChance : 1.0;
            if (roll <= acc)
            {
                excludedIds?.Add(t.Id);
                return t;
            }
        }
        var last = candidates[^1];
        excludedIds?.Add(last.Id);
        return last;
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
/// 品质概率表。优先从 GameData.db 的 game_modes.ItemSkillSpawnTierPercantagesByDay 加载（Day1..Day20）。
/// </summary>
public sealed class TierChances
{
    // [day] -> (Bronze, Silver, Gold, Diamond) 默认兜底表（仅在未加载 DB 时使用）
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
        (0.00, 0.20, 0.70, 0.10), // 10
        (0.00, 0.25, 0.55, 0.20), // 11
        (0.00, 0.20, 0.55, 0.05), // 12（原始数据 Day12 为 0.2/0.55/0.05，此处按官方值）
        (0.00, 0.10, 0.60, 0.30), // 13
        (0.00, 0.10, 0.60, 0.30), // 14
        (0.00, 0.30, 0.50, 0.20), // 15
        (0.00, 0.10, 0.60, 0.30), // 16
        (0.00, 0.10, 0.60, 0.30), // 17
        (0.00, 0.30, 0.50, 0.20), // 18
        (0.00, 0.10, 0.60, 0.30), // 19
        (0.00, 0.10, 0.60, 0.30), // 20
    };

    private readonly IReadOnlyList<(double Bronze, double Silver, double Gold, double Diamond)> _table;

    public TierChances() : this(DefaultTable) { }

    public TierChances(IReadOnlyList<(double Bronze, double Silver, double Gold, double Diamond)>? table)
    {
        _table = table is { Count: > 0 } ? table : DefaultTable;
    }

    /// <summary>从 GameData.db 的 game_modes.ItemSkillSpawnTierPercantagesByDay 构建每日权重表。</summary>
    public static (double Bronze, double Silver, double Gold, double Diamond)[] FromGameModeMap(
        IReadOnlyDictionary<string, Dictionary<string, double>>? map)
    {
        if (map == null || map.Count == 0)
            return DefaultTable;

        int maxDay = DefaultTable.Length;
        foreach (string key in map.Keys)
        {
            if (int.TryParse(key, out int d) && d > maxDay && d <= 50)
                maxDay = d;
        }

        var rows = new (double Bronze, double Silver, double Gold, double Diamond)[maxDay];
        for (int day = 1; day <= maxDay; day++)
        {
            if (map.TryGetValue(day.ToString(), out var w) && w != null)
            {
                w.TryGetValue("Bronze", out double b);
                w.TryGetValue("Silver", out double s);
                w.TryGetValue("Gold", out double g);
                w.TryGetValue("Diamond", out double dm);
                rows[day - 1] = (Math.Max(0, b), Math.Max(0, s), Math.Max(0, g), Math.Max(0, dm));
            }
            else
            {
                int fallbackIdx = Math.Clamp(day, 1, DefaultTable.Length) - 1;
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

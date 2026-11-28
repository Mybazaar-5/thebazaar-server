using System.Text.Json.Serialization;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarServer.Data;
using BazaarServer.Mods;

namespace BazaarServer.Planner;

/// <summary>
/// 单个依赖 Mod 的版本与哈希描述（记录于剧本与分享蓝图的 ModEnvironmentSpec 中）。
/// </summary>
public sealed class ModRequirementEntry
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public string PackageHash { get; set; } = "";
    public bool IsGameplay { get; set; }
}

/// <summary>
/// 剧本与分享蓝图录制时的完整数据与 Mod 环境快照。
/// </summary>
public sealed class ModEnvironmentSpec
{
    public string GameDataVersion { get; set; } = "";
    public string EffectiveModSetHash { get; set; } = "";
    public List<ModRequirementEntry> Mods { get; set; } = new();
    public string? CustomModeId { get; set; }
}

/// <summary>
/// 玩家自定义每日 PvP 镜像剧本（Day 1..30）。
/// </summary>
public sealed class CustomGhostLineup
{
    public string LineupId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceType { get; set; } = "custom"; // custom | imported | sandbox | faced | run
    public string? SourceCode { get; set; }
    /// <summary>方案配套种子（可空）：分享这个方案时一起带上；「下一局用它」时可一起应用。旧数据没有这个字段 = 没配种子。</summary>
    public string? SeedText { get; set; }
    /// <summary>配套种子录制时的英雄（只用于分享时的日历预览；可空）。</summary>
    public string? SeedHero { get; set; }
    public ModEnvironmentSpec ModEnvironment { get; set; } = new();
    public Dictionary<int, PvpGhostPool.GhostBoard> Days { get; set; } = new(); // 键 1..30
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// 可导出与跨服导入的对局蓝图包（对应短码与离线 BZR1 载荷）。
/// </summary>
public sealed class RunBlueprintPackage
{
    public int SchemaVersion { get; set; } = 1;
    public int SeedAlgoVersion { get; set; } = SeedAlgo.Current;
    public string ExportScope { get; set; } = "events_only"; // events_only | events_and_ghosts
    public string Title { get; set; } = "";
    public string CreatorUsername { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string Hero { get; set; } = "";
    public int MasterSeed { get; set; }
    public string SeedText { get; set; } = "";
    public bool IncludeRules { get; set; }
    public string? SourceSpecialModeId { get; set; }
    public CustomRunRuleSet? RuleSetSnapshot { get; set; }
    public ModEnvironmentSpec ModEnvironment { get; set; } = new();
    public Dictionary<int, PvpGhostPool.GhostBoard>? DayGhosts { get; set; } // events_only 时为 null
}

/// <summary>导入蓝图时可自动开启的本服可用 Mod 条目。</summary>
public sealed class BlueprintAutoEnableMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
}

/// <summary>导入蓝图时本服缺失的 Mod 条目（含受影响的镜像天数）。</summary>
public sealed class BlueprintMissingMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Author { get; set; } = "";
    public List<int> AffectedDays { get; set; } = new();
}

/// <summary>导入蓝图时本服已安装但版本/哈希不一致的 Mod 条目。</summary>
public sealed class BlueprintVersionMismatchMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string ExpectedVersion { get; set; } = "";
}

/// <summary>本服全局额外启用的玩法 Mod 条目。</summary>
public sealed class BlueprintServerExtraMod
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>蓝图导入预览的四类 Mod 环境差异。</summary>
public sealed class BlueprintModDiff
{
    public List<BlueprintAutoEnableMod> AutoEnable { get; set; } = new();
    public List<BlueprintMissingMod> Missing { get; set; } = new();
    public List<BlueprintVersionMismatchMod> VersionMismatch { get; set; } = new();
    public List<BlueprintServerExtraMod> ServerExtra { get; set; } = new();
}

/// <summary>
/// 蓝图导入预览响应模型（与 POST /api/planner/blueprints/preview 契约逐字段对齐，camelCase 输出）。
/// </summary>
public sealed class BlueprintImportPreview
{
    public string CodeKind { get; set; } = "short"; // short | offline | json
    public string Title { get; set; } = "";
    public string CreatorUsername { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string Scope { get; set; } = "events_only"; // events_only | events_and_ghosts
    public string Hero { get; set; } = "";
    public string HeroZh { get; set; } = "";
    public string SeedText { get; set; } = "";
    public List<int> GhostDays { get; set; } = new();
    public bool HasRules { get; set; }
    public string RulesSummaryZh { get; set; } = "";
    public BlueprintModDiff ModDiff { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool HasActiveRun { get; set; }

    [JsonIgnore]
    public int GhostDaysCount => GhostDays?.Count ?? 0;
}

/// <summary>shared_run_blueprints 表完整持久化记录（含 GZip 压缩载荷）。</summary>
public sealed record SharedBlueprintRecord(
    string ShareCode,
    string CreatorAccountId,
    string CreatorUsername,
    string Title,
    string ExportScope,
    string Hero,
    string SeedText,
    int MasterSeed,
    byte[] BlueprintGzip,
    int ImportCount,
    DateTime CreatedAtUtc);

/// <summary>shared_run_blueprints 表轻量摘要记录（不含 BLOB，供 GET blueprints/mine 使用）。</summary>
public sealed record SharedBlueprintSummaryRecord(
    string ShareCode,
    string CreatorAccountId,
    string CreatorUsername,
    string Title,
    string ExportScope,
    string Hero,
    string SeedText,
    int MasterSeed,
    int ImportCount,
    DateTime CreatedAtUtc);

/// <summary>
/// 编排台与种子蓝图共享常量、英雄/品阶/属性目录映射与 Mod 环境构建工具。
/// </summary>
public static class PlannerCatalogConstants
{
    public const int MaxLineups = 50;
    public const int MaxLineupDays = 30;
    public const int MaxSeedTextLength = 48;
    public const int MaxDisplayName = 24;
    public const int MaxLineupName = 40;
    public const int MaxDescription = 200;
    public const int QuantityMax = 1_000_000;
    public const int HealthMin = 1;
    public const int HealthMax = 1_000_000;
    public const int LevelMin = 1;
    public const int LevelMax = 99;
    public const int PrestigeMin = 0;
    public const int PrestigeMax = 100;
    public const int MaxLineupJsonBytes = 256 * 1024; // 一天的真实阵容约 2~5 KB，30 天都排满也放得下
    public const int MaxSharedBlueprintsPerAccount = 100;

    /// <summary>受支持的 8 位英雄有序列表（ID + 简体中文名）。</summary>
    public static IReadOnlyList<(string Id, string NameZh)> SupportedHeroes { get; } = new (string Id, string NameZh)[]
    {
        ("Vanessa", "瓦内莎"),
        ("Pygmalien", "皮格马利翁"),
        ("Dooley", "杜利"),
        ("Mak", "马克"),
        ("Jules", "朱尔斯"),
        ("Stelle", "斯黛拉"),
        ("Karnok", "卡诺克"),
        ("Hero8", "双龙"),
    };

    private static readonly Dictionary<string, (string CanonicalId, string NameZh)> HeroLookup =
        BuildHeroLookup();

    /// <summary>英雄 ID -> 中文名只读字典（大小写不敏感）。</summary>
    public static IReadOnlyDictionary<string, string> HeroNameZhById { get; } =
        BuildHeroNameZhDictionary();

    /// <summary>允许手搓编辑的属性白名单（7 项数量类 + 冷却毫秒；冷却最低 1 秒、被动物品不能加冷却，见 GhostBoardValidator）。</summary>
    public static IReadOnlyList<string> QuantityAttributes { get; } = new[]
    {
        "DamageAmount",
        "ShieldApplyAmount",
        "HealAmount",
        "BurnApplyAmount",
        "PoisonApplyAmount",
        "RegenApplyAmount",
        "CritChance",
        "CooldownMax",
    };

    /// <summary>数量类属性中文标签映射（保留原始 PascalCase 键用于 JSON 契约输出）。</summary>
    public static IReadOnlyDictionary<string, string> AttributeLabelsZh { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DamageAmount"] = "伤害",
            ["ShieldApplyAmount"] = "护盾",
            ["HealAmount"] = "治疗",
            ["BurnApplyAmount"] = "灼烧",
            ["PoisonApplyAmount"] = "剧毒",
            ["RegenApplyAmount"] = "回复",
            ["CritChance"] = "暴击率%",
            ["CooldownMax"] = "冷却(毫秒)",
        };

    /// <summary>数量类属性白名单集合（大小写不敏感）。</summary>
    public static readonly HashSet<string> QuantityAttributeSet =
        new(QuantityAttributes, StringComparer.OrdinalIgnoreCase);

    /// <summary>第 1~4 天基础解锁格子数（第 4 天及以后统一为 10 格）。</summary>
    public static IReadOnlyDictionary<int, int> DayBaseSlots { get; } = new Dictionary<int, int>
    {
        [1] = 4,
        [2] = 6,
        [3] = 8,
        [4] = 10,
    };

    /// <summary>品阶升序列表。</summary>
    public static IReadOnlyList<string> TierOrder { get; } = new[]
    {
        "Bronze",
        "Silver",
        "Gold",
        "Diamond",
        "Legendary",
    };

    /// <summary>品阶有序列表（ID + 简体中文名）。</summary>
    public static IReadOnlyList<(string Id, string NameZh)> SupportedTiers { get; } = new (string Id, string NameZh)[]
    {
        ("Bronze", "青铜"),
        ("Silver", "白银"),
        ("Gold", "黄金"),
        ("Diamond", "钻石"),
        ("Legendary", "传奇"),
    };

    /// <summary>品阶 ID -> 中文名映射（大小写不敏感）。</summary>
    public static IReadOnlyDictionary<string, string> TierNameZhById { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bronze"] = "青铜",
            ["Silver"] = "白银",
            ["Gold"] = "黄金",
            ["Diamond"] = "钻石",
            ["Legendary"] = "传奇",
        };

    /// <summary>品阶 ID -> 升序序号映射（Bronze=0 .. Legendary=4，大小写不敏感）。</summary>
    public static IReadOnlyDictionary<string, int> TierOrderIndex { get; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bronze"] = 0,
            ["Silver"] = 1,
            ["Gold"] = 2,
            ["Diamond"] = 3,
            ["Legendary"] = 4,
        };

    /// <summary>判断英雄 ID 是否属于受支持的 8 位英雄。</summary>
    public static bool IsSupportedHero(string? hero)
        => !string.IsNullOrWhiteSpace(hero) && HeroLookup.ContainsKey(hero.Trim());

    /// <summary>将英雄 ID 归一化为标准 PascalCase 名称；不支持则返回 null。</summary>
    public static string? NormalizeHeroId(string? hero)
    {
        if (string.IsNullOrWhiteSpace(hero)) return null;
        return HeroLookup.TryGetValue(hero.Trim(), out var info) ? info.CanonicalId : null;
    }

    /// <summary>获取英雄简体中文名；未知英雄回退为去空白原串。</summary>
    public static string GetHeroNameZh(string? hero)
    {
        if (string.IsNullOrWhiteSpace(hero)) return "";
        string trimmed = hero.Trim();
        return HeroLookup.TryGetValue(trimmed, out var info) ? info.NameZh : trimmed;
    }

    /// <summary>获取品阶简体中文名；未知品阶回退为去空白原串。</summary>
    public static string GetTierNameZh(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return "";
        string trimmed = tier.Trim();
        return TierNameZhById.TryGetValue(trimmed, out var zh) ? zh : trimmed;
    }

    /// <summary>获取品阶序号（0..4）；未知品阶返回 -1。</summary>
    public static int GetTierIndex(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return -1;
        return TierOrderIndex.TryGetValue(tier.Trim(), out int idx) ? idx : -1;
    }

    /// <summary>
    /// 解析当前游戏数据版本：优先取 <c>repo.GameMode.Version</c>，为空则回退首个非 <c>mod:</c> 前缀的原版卡 <c>CardTemplate.Version</c>，最终兜底 <c>"5.0.0"</c>。
    /// </summary>
    public static string ResolveGameDataVersion(GameDataRepository? repo)
    {
        if (repo != null)
        {
            if (!string.IsNullOrWhiteSpace(repo.GameMode?.Version))
                return repo.GameMode.Version.Trim();

            if (repo.CardsById != null)
            {
                foreach (var card in repo.CardsById.Values)
                {
                    if (card == null || string.IsNullOrWhiteSpace(card.Version))
                        continue;
                    string ver = card.Version.Trim();
                    if (!ver.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
                        return ver;
                }
            }
        }
        return "5.0.0";
    }

    /// <summary>
    /// 按当前选中的 Mod 列表构建 <see cref="ModEnvironmentSpec"/>（记录数据版本、有效玩法 Mod 集哈希及完整已加载包清单）。
    /// </summary>
    public static ModEnvironmentSpec BuildModEnvironmentSpec(
        IReadOnlyList<string>? selectedModIds,
        ServerModHost? modHost,
        GameDataRepository? repo,
        string? customModeId = null)
    {
        var spec = new ModEnvironmentSpec
        {
            GameDataVersion = ResolveGameDataVersion(repo ?? modHost?.ActiveRepository),
            EffectiveModSetHash = ServerModHost.ComputeEffectiveModSetHash(selectedModIds, modHost),
            CustomModeId = string.IsNullOrWhiteSpace(customModeId) ? null : customModeId.Trim(),
        };

        if (modHost != null)
        {
            var effectiveRegistry = modHost.ResolveEffectiveRegistryForMods(selectedModIds);
            foreach (var pkg in effectiveRegistry.LoadedPackages)
            {
                if (pkg?.Manifest == null) continue;
                string id = pkg.Manifest.Id?.Trim() ?? "";
                if (string.IsNullOrEmpty(id)) continue;
                spec.Mods.Add(new ModRequirementEntry
                {
                    ModId = id,
                    Name = string.IsNullOrWhiteSpace(pkg.Manifest.Name) ? id : pkg.Manifest.Name.Trim(),
                    Version = pkg.Manifest.Version?.Trim() ?? "",
                    Author = pkg.Manifest.Author?.Trim() ?? "",
                    PackageHash = pkg.PackageHash ?? "",
                    IsGameplay = ServerModHost.IsGameplayAffectingPackage(pkg),
                });
            }
        }

        return spec;
    }

    private static Dictionary<string, (string CanonicalId, string NameZh)> BuildHeroLookup()
    {
        var map = new Dictionary<string, (string CanonicalId, string NameZh)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, nameZh) in SupportedHeroes)
            map[id] = (id, nameZh);
        return map;
    }

    private static Dictionary<string, string> BuildHeroNameZhDictionary()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, nameZh) in SupportedHeroes)
            map[id] = nameZh;
        return map;
    }
}

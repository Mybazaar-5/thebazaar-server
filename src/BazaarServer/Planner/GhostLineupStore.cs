using System.Globalization;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Mods;
using BazaarServer.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarServer.Planner;

/// <summary>剧本依赖但本服已不存在的 Mod 摘要（契约 0.2）。</summary>
public sealed class LineupMissingModDto
{
    public string ModId { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>自定义每日镜像剧本轻量摘要 DTO（契约 0.2，camelCase 序列化）。</summary>
public class LineupSummaryDto
{
    public string LineupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int[] DayNumbers { get; set; } = Array.Empty<int>();
    public string SourceType { get; set; } = "custom";
    public string? SourceCode { get; set; }
    /// <summary>方案配套种子（null = 没配）。</summary>
    public string? SeedText { get; set; }
    /// <summary>配套种子对应的英雄（可空，仅用于分享时预览日历）。</summary>
    public string? SeedHero { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<LineupMissingModDto> MissingMods { get; set; } = new();
    public List<string> RequiredModIds { get; set; } = new();
    public bool IsSelected { get; set; }
}

/// <summary>自定义每日镜像剧本完整 DTO（契约 0.2，在摘要基础上附带 Days 与 ModEnvironment）。</summary>
public sealed class LineupFullDto : LineupSummaryDto
{
    public Dictionary<int, PvpGhostPool.GhostBoard> Days { get; set; } = new();
    public ModEnvironmentSpec ModEnvironment { get; set; } = new();
}

/// <summary>单天盘面校验失败明细 DTO（对应 400 BOARD_INVALID 的 details 元素）。</summary>
public sealed class BoardErrorDetailDto
{
    public int Day { get; set; }
    public string MessageZh { get; set; } = "";
}

/// <summary>剧本写操作统一结果封装，便于 HTTP 端点直接映射状态码与错误体。</summary>
public sealed class GhostLineupResult<T>
{
    public bool Ok { get; init; }
    public int HttpStatus { get; init; } = 200;
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public List<BoardErrorDetailDto>? Details { get; init; }
    public T? Data { get; init; }
    public List<string> Warnings { get; init; } = new();
    public int? Day { get; init; }

    public static GhostLineupResult<T> Success(T data, List<string>? warnings = null, int? day = null) => new()
    {
        Ok = true,
        HttpStatus = 200,
        Data = data,
        Warnings = warnings ?? new List<string>(),
        Day = day,
    };

    public static GhostLineupResult<T> Failure(
        int httpStatus,
        string errorCode,
        string errorMessage,
        List<BoardErrorDetailDto>? details = null,
        List<string>? warnings = null) => new()
    {
        Ok = false,
        HttpStatus = httpStatus,
        ErrorCode = errorCode,
        ErrorMessage = errorMessage,
        Details = details,
        Warnings = warnings ?? new List<string>(),
    };
}

/// <summary>
/// 玩家自定义每日镜像剧本领域服务：
/// - 统一封装剧本列表、详情、单天校验、整套保存、删除及沙盒/交手盘面捕获；
/// - 基于 <see cref="ServerModHost"/> 与 <see cref="CommunityModStore"/> 判定缺失 Mod 与依赖 Mod；
/// - 删除当前选用剧本时自动将玩家规则回退为 <see cref="EGhostOpponentMode.Random"/>。
/// </summary>
public sealed class GhostLineupStore
{
    private readonly Persistence _db;
    private readonly PlayerRuleStore _ruleStore;
    private readonly GameDataRepository _baseRepo;
    private readonly CardPool _basePool;
    private readonly MonsterPoolStore? _monsterPoolStore;
    private readonly ServerRuntimeSnapshotProvider? _snapshotProvider;
    private readonly List<BazaarServer.Data.MonsterTemplate>? _fallbackMonsters;
    private CardArchetypeIndex? _fallbackArchetypes;
    private readonly object _archetypeInitLock = new();
    private readonly ServerModHost? _modHost;
    private readonly CommunityModStore? _communityModStore;

    private CommunityModStore? EffectiveCommunityStore => _communityModStore ?? _modHost?.CommunityStore;

    /// <summary>DI 主构造函数（全部注入 Singleton 服务，自动跟随 Mod 热重载刷新快照）。</summary>
    [ActivatorUtilitiesConstructor]
    public GhostLineupStore(
        Persistence db,
        PlayerRuleStore ruleStore,
        GameDataRepository baseRepo,
        CardPool basePool,
        MonsterPoolStore? monsterPoolStore = null,
        ServerRuntimeSnapshotProvider? snapshotProvider = null,
        ServerModHost? modHost = null,
        CommunityModStore? communityModStore = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _baseRepo = baseRepo ?? throw new ArgumentNullException(nameof(baseRepo));
        _basePool = basePool ?? throw new ArgumentNullException(nameof(basePool));
        _monsterPoolStore = monsterPoolStore;
        _snapshotProvider = snapshotProvider;
        _modHost = modHost;
        _communityModStore = communityModStore ?? modHost?.CommunityStore;
    }

    /// <summary>便捷测试构造函数（直接传入野怪列表与流派索引）。</summary>
    public GhostLineupStore(
        Persistence db,
        PlayerRuleStore ruleStore,
        GameDataRepository baseRepo,
        CardPool basePool,
        List<BazaarServer.Data.MonsterTemplate>? baseMonsters,
        CardArchetypeIndex? baseArchetypes,
        ServerModHost? modHost = null,
        CommunityModStore? communityModStore = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _ruleStore = ruleStore ?? throw new ArgumentNullException(nameof(ruleStore));
        _baseRepo = baseRepo ?? throw new ArgumentNullException(nameof(baseRepo));
        _basePool = basePool ?? throw new ArgumentNullException(nameof(basePool));
        _fallbackMonsters = baseMonsters;
        _fallbackArchetypes = baseArchetypes;
        _modHost = modHost;
        _communityModStore = communityModStore ?? modHost?.CommunityStore;
    }

    // ── 1. 摘要 / 详情视图构建与缺失 Mod 判定 ─────────────────────────────────

    /// <summary>构建剧本轻量摘要 DTO（计算升序天数、缺失 Mod、依赖 Mod 与当前选中状态）。</summary>
    public LineupSummaryDto BuildSummary(CustomGhostLineup lineup, CustomRunRuleSet? currentRules)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        var dto = new LineupSummaryDto();
        PopulateSummaryFields(dto, lineup, currentRules);
        return dto;
    }

    /// <summary>构建剧本完整 DTO（含按天升序排列的盘面与 Mod 环境快照）。</summary>
    public LineupFullDto BuildFull(CustomGhostLineup lineup, CustomRunRuleSet? currentRules)
    {
        ArgumentNullException.ThrowIfNull(lineup);
        var dto = new LineupFullDto
        {
            ModEnvironment = lineup.ModEnvironment ?? new ModEnvironmentSpec(),
        };
        PopulateSummaryFields(dto, lineup, currentRules);

        var sortedDays = new Dictionary<int, PvpGhostPool.GhostBoard>();
        if (lineup.Days != null)
        {
            foreach (var kv in lineup.Days.OrderBy(k => k.Key))
            {
                if (kv.Key >= 1 && kv.Key <= PlannerCatalogConstants.MaxLineupDays && kv.Value != null)
                    sortedDays[kv.Key] = kv.Value;
            }
        }
        dto.Days = sortedDays;
        return dto;
    }

    // ── 2. 剧本查询、校验、保存、删除与盘面捕获 ────────────────────────────────

    /// <summary>列出指定账号名下全部剧本摘要（按 UpdatedAtUtc 降序）。</summary>
    public List<LineupSummaryDto> ListLineups(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return new List<LineupSummaryDto>();

        var currentRules = _ruleStore.Get(accountId);
        var lineups = _db.ListGhostLineups(accountId);
        var list = new List<LineupSummaryDto>(lineups.Count);
        foreach (var lineup in lineups)
            list.Add(BuildSummary(lineup, currentRules));
        return list;
    }

    /// <summary>按账号与剧本 ID 获取剧本完整详情（跨账号或不存在返回 null）。</summary>
    public LineupFullDto? GetLineup(string accountId, string lineupId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(lineupId))
            return null;

        var lineup = _db.LoadGhostLineup(accountId, lineupId);
        if (lineup == null) return null;

        var currentRules = _ruleStore.Get(accountId);
        return BuildFull(lineup, currentRules);
    }

    /// <summary>
    /// 校验并归一化单天盘面 JSON（不落库）：
    /// 折算 <c>editedAttributes</c> 进 <c>permanentAdd</c> 并剥离、剥除频率类篡改、更新 <c>UnlockedSlotsMask</c> 并保留完整盘面字段。
    /// </summary>
    public GhostBoardValidationResult ValidateBoard(
        int day,
        JsonElement boardElement,
        IReadOnlyList<string>? selectedModIds = null)
    {
        var referencedTemplateIds = ExtractTemplateIdsFromJson(boardElement);
        var (effectiveRepo, effectivePool, explicitAllowed, _) = ResolveValidationEnvironment(selectedModIds, referencedTemplateIds);
        return GhostBoardValidator.ValidateAndNormalize(
            day,
            boardElement,
            effectiveRepo,
            effectivePool,
            _modHost,
            explicitAllowed);
    }

    /// <summary>校验并归一化已反序列化的单天 <see cref="PvpGhostPool.GhostBoard"/>（不落库）。</summary>
    public GhostBoardValidationResult ValidateBoard(
        int day,
        PvpGhostPool.GhostBoard board,
        IReadOnlyList<string>? selectedModIds = null)
    {
        var referencedTemplateIds = ExtractTemplateIdsFromBoard(board);
        var (effectiveRepo, effectivePool, explicitAllowed, _) = ResolveValidationEnvironment(selectedModIds, referencedTemplateIds);
        return GhostBoardValidator.ValidateAndNormalize(
            day,
            board,
            effectiveRepo,
            effectivePool,
            _modHost,
            explicitAllowed);
    }

    /// <summary>
    /// 创建或完整覆盖更新玩家剧本：
    /// - 清洗并校验剧本名（1..40 字）与简介（截断至 200 字）；
    /// - 逐天校验盘面并归一化 <c>Day/SourceDay/TargetDay/BoardId</c>；
    /// - 汇总 <c>selectedModIds</c> 与盘面卡牌实际引用的社区 Mod 写入 <c>ModEnvironment</c>；
    /// - 映射 <c>NOT_FOUND(404)</c>、<c>LINEUP_LIMIT(409)</c>、<c>PAYLOAD_TOO_LARGE(413)</c>。
    /// </summary>
    public GhostLineupResult<LineupFullDto> SaveLineup(
        string accountId,
        string? lineupId,
        string? name,
        string? description,
        IReadOnlyList<string>? selectedModIds,
        Dictionary<string, JsonElement>? days,
        string sourceType = "custom",
        string? sourceCode = null,
        bool seedProvided = false,
        string? seedText = null,
        string? seedHero = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return GhostLineupResult<LineupFullDto>.Failure(401, "AUTH_REQUIRED", "请先登录");

        // 配套种子：请求里带了 seedText 字段才改（空串 = 清掉）；没带就沿用原值（兼容游戏内旧接口）
        string? normalizedSeed = null;
        if (seedProvided && !string.IsNullOrWhiteSpace(seedText))
        {
            if (!TryNormalizeLineupSeed(seedText, out normalizedSeed))
                return GhostLineupResult<LineupFullDto>.Failure(400, "SEED_INVALID", "配套种子格式不对：应该是 S 开头的 8 位码，比如 S3KZ9QW1");
        }

        string cleanedName = AccountStore.SanitizeDisplayText(name?.Trim(), maxLength: PlannerCatalogConstants.MaxLineupName + 64);
        if (cleanedName.Length == 0 || cleanedName.Length > PlannerCatalogConstants.MaxLineupName)
        {
            return GhostLineupResult<LineupFullDto>.Failure(
                400,
                "NAME_INVALID",
                $"方案名要 1–{PlannerCatalogConstants.MaxLineupName} 个字");
        }

        string cleanedDescription = AccountStore.SanitizeDisplayText(description?.Trim(), maxLength: PlannerCatalogConstants.MaxDescription);
        string? trimmedLineupId = string.IsNullOrWhiteSpace(lineupId) ? null : lineupId.Trim();
        bool isUpdate = trimmedLineupId != null;

        CustomGhostLineup? existing = null;
        if (isUpdate)
        {
            existing = _db.LoadGhostLineup(accountId, trimmedLineupId!);
            if (existing == null)
            {
                return GhostLineupResult<LineupFullDto>.Failure(
                    404,
                    "NOT_FOUND",
                    "方案不存在（可能已经在别处删掉了）");
            }
        }

        string finalId = existing != null ? existing.LineupId : $"ln_{Guid.NewGuid():N}";

        // 预先收集全部盘面模板 ID，一次解析出当前编排环境（并识别本服已装但未勾选的社区 Mod 卡）
        var allTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (days != null)
        {
            foreach (var kv in days)
                ExtractTemplateIdsFromJson(kv.Value, allTemplateIds);
        }

        var (effectiveRepo, effectivePool, explicitAllowed, extraReferencedMods) =
            ResolveValidationEnvironment(selectedModIds, allTemplateIds);

        var details = new List<BoardErrorDetailDto>();
        var warnings = new List<string>();
        var normalizedDays = new Dictionary<int, PvpGhostPool.GhostBoard>();
        var boardReferencedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (days != null)
        {
            foreach (var (rawKey, boardEl) in days)
            {
                if (!int.TryParse(rawKey?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)
                    || day < 1
                    || day > PlannerCatalogConstants.MaxLineupDays)
                {
                    int fallbackDay = int.TryParse(rawKey?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 0;
                    details.Add(new BoardErrorDetailDto
                    {
                        Day = fallbackDay,
                        MessageZh = $"第 {rawKey} 天超出 1–{PlannerCatalogConstants.MaxLineupDays} 范围",
                    });
                    continue;
                }

                var vr = GhostBoardValidator.ValidateAndNormalize(
                    day,
                    boardEl,
                    effectiveRepo,
                    effectivePool,
                    _modHost,
                    explicitAllowed);

                foreach (string w in vr.Warnings)
                    warnings.Add($"第 {day} 天：{w}");

                if (!vr.Ok)
                {
                    details.Add(new BoardErrorDetailDto
                    {
                        Day = day,
                        MessageZh = string.Join("；", vr.Errors),
                    });
                    continue;
                }

                var normalizedBoard = NormalizeLineupBoard(vr.Board, day, finalId);
                normalizedDays[day] = normalizedBoard;
                CollectBoardCardModOrigins(normalizedBoard, effectiveRepo, boardReferencedModIds);
            }
        }

        if (details.Count > 0)
        {
            details.Sort((a, b) => a.Day.CompareTo(b.Day));
            return GhostLineupResult<LineupFullDto>.Failure(
                400,
                "BOARD_INVALID",
                "有天的阵容不合法",
                details,
                warnings);
        }

        // 构建 ModEnvironment：合并选中的 Mod、盘面卡牌实际引用的社区 Mod
        var effectiveModIds = MergeModIds(selectedModIds, extraReferencedMods, boardReferencedModIds);
        var modEnv = PlannerCatalogConstants.BuildModEnvironmentSpec(
            effectiveModIds,
            _modHost,
            _baseRepo,
            customModeId: null);

        string resolvedSourceType;
        string? resolvedSourceCode;
        if (existing != null)
        {
            resolvedSourceType = string.IsNullOrWhiteSpace(existing.SourceType) ? "custom" : existing.SourceType;
            resolvedSourceCode = existing.SourceCode;
        }
        else
        {
            resolvedSourceType = string.IsNullOrWhiteSpace(sourceType) ? "custom" : sourceType.Trim().ToLowerInvariant();
            resolvedSourceCode = string.IsNullOrWhiteSpace(sourceCode) ? null : sourceCode.Trim();
        }

        var entity = new CustomGhostLineup
        {
            LineupId = finalId,
            AccountId = accountId,
            Name = cleanedName,
            Description = cleanedDescription,
            SourceType = resolvedSourceType,
            SourceCode = resolvedSourceCode,
            SeedText = seedProvided ? normalizedSeed : existing?.SeedText,
            SeedHero = seedProvided
                ? (normalizedSeed == null ? null : (PlannerCatalogConstants.NormalizeHeroId(seedHero) ?? existing?.SeedHero))
                : existing?.SeedHero,
            ModEnvironment = modEnv,
            Days = normalizedDays,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        var (ok, errorCode, saved) = _db.SaveGhostLineup(
            entity,
            PlannerCatalogConstants.MaxLineups,
            PlannerCatalogConstants.MaxLineupJsonBytes,
            requireExisting: isUpdate);

        if (!ok || saved == null)
            return MapPersistenceSaveFailure<LineupFullDto>(errorCode, warnings);

        var currentRules = _ruleStore.Get(accountId);
        return GhostLineupResult<LineupFullDto>.Success(BuildFull(saved, currentRules), warnings);
    }

    /// <summary>
    /// 删除指定账号名下的剧本；若该剧本正被玩家规则选中，自动重置对手模式为 <see cref="EGhostOpponentMode.Random"/>。
    /// </summary>
    public (bool Found, bool RulesChanged) DeleteLineup(string accountId, string lineupId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(lineupId))
            return (false, false);

        string trimmedId = lineupId.Trim();
        bool deleted = _db.DeleteGhostLineup(accountId, trimmedId);
        if (!deleted)
            return (false, false);

        var rules = _ruleStore.Get(accountId);
        if (string.Equals(rules.SelectedGhostLineupId, trimmedId, StringComparison.OrdinalIgnoreCase))
        {
            rules.GhostOpponentMode = EGhostOpponentMode.Random;
            rules.SelectedGhostLineupId = null;
            _ruleStore.Set(accountId, rules);
            return (true, true);
        }

        return (true, false);
    }

    /// <summary>
    /// 将单块盘面（来自沙盒捕获或已交手对手收藏）校验归一化后写入指定剧本或新建剧本的目标天数。
    /// </summary>
    public GhostLineupResult<LineupSummaryDto> CaptureDayBoard(
        string accountId,
        string? lineupId,
        string? newLineupName,
        int targetDay,
        PvpGhostPool.GhostBoard rawBoard,
        string sourceType,
        IReadOnlyList<string>? selectedModIds = null)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            return GhostLineupResult<LineupSummaryDto>.Failure(401, "AUTH_REQUIRED", "请先登录");

        if (targetDay < 1 || targetDay > PlannerCatalogConstants.MaxLineupDays)
        {
            return GhostLineupResult<LineupSummaryDto>.Failure(
                400,
                "BOARD_INVALID",
                $"目标天数要在 1–{PlannerCatalogConstants.MaxLineupDays} 之间",
                new List<BoardErrorDetailDto>
                {
                    new() { Day = targetDay, MessageZh = $"天数要在 1–{PlannerCatalogConstants.MaxLineupDays} 之间" },
                });
        }

        if (rawBoard == null)
        {
            return GhostLineupResult<LineupSummaryDto>.Failure(
                400,
                "BOARD_INVALID",
                "待捕获的盘面数据不能为空");
        }

        string effectiveSourceType = string.IsNullOrWhiteSpace(sourceType)
            ? "faced"
            : sourceType.Trim().ToLowerInvariant();
        string? trimmedLineupId = string.IsNullOrWhiteSpace(lineupId) ? null : lineupId.Trim();
        bool isUpdate = trimmedLineupId != null;

        CustomGhostLineup? existing = null;
        if (isUpdate)
        {
            existing = _db.LoadGhostLineup(accountId, trimmedLineupId!);
            if (existing == null)
            {
                return GhostLineupResult<LineupSummaryDto>.Failure(
                    404,
                    "NOT_FOUND",
                    "方案不存在（可能已经被删除）");
            }
        }

        string finalId = existing != null ? existing.LineupId : $"ln_{Guid.NewGuid():N}";
        string finalName;
        string finalDescription;
        string finalSourceType;
        string? finalSourceCode;
        Dictionary<int, PvpGhostPool.GhostBoard> days;

        if (existing != null)
        {
            finalName = existing.Name;
            finalDescription = existing.Description ?? "";
            finalSourceType = string.IsNullOrWhiteSpace(existing.SourceType) ? effectiveSourceType : existing.SourceType;
            finalSourceCode = existing.SourceCode;
            days = existing.Days != null
                ? new Dictionary<int, PvpGhostPool.GhostBoard>(existing.Days)
                : new Dictionary<int, PvpGhostPool.GhostBoard>();
        }
        else
        {
            string defaultName = string.Equals(effectiveSourceType, "sandbox", StringComparison.OrdinalIgnoreCase)
                ? "沙盒方案"
                : "收藏的对手";
            string cleanedNewName = AccountStore.SanitizeDisplayText(newLineupName?.Trim(), maxLength: PlannerCatalogConstants.MaxLineupName);
            finalName = string.IsNullOrEmpty(cleanedNewName) ? defaultName : cleanedNewName;
            finalDescription = "";
            finalSourceType = effectiveSourceType;
            finalSourceCode = null;
            days = new Dictionary<int, PvpGhostPool.GhostBoard>();
        }

        // 合并已有剧本 Mod 需求与当前 selectedModIds，解析校验环境
        var existingModIds = existing?.ModEnvironment?.Mods?
            .Where(m => m != null && !string.IsNullOrWhiteSpace(m.ModId))
            .Select(m => m.ModId.Trim())
            .ToList();
        var mergedSelectedModIds = MergeModIds(selectedModIds, existingModIds, null);
        var templateIds = ExtractTemplateIdsFromBoard(rawBoard);
        var (effectiveRepo, effectivePool, explicitAllowed, extraReferencedMods) =
            ResolveValidationEnvironment(mergedSelectedModIds, templateIds);

        var vr = GhostBoardValidator.ValidateAndNormalize(
            targetDay,
            rawBoard,
            effectiveRepo,
            effectivePool,
            _modHost,
            explicitAllowed);

        if (!vr.Ok)
        {
            return GhostLineupResult<LineupSummaryDto>.Failure(
                400,
                "BOARD_INVALID",
                "盘面校验未通过",
                new List<BoardErrorDetailDto>
                {
                    new() { Day = targetDay, MessageZh = string.Join("；", vr.Errors) },
                },
                vr.Warnings);
        }

        var normalizedBoard = NormalizeLineupBoard(vr.Board, targetDay, finalId);
        days[targetDay] = normalizedBoard;

        var boardReferencedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in days.Values)
            CollectBoardCardModOrigins(b, effectiveRepo, boardReferencedModIds);

        var allModIds = MergeModIds(mergedSelectedModIds, extraReferencedMods, boardReferencedModIds);
        var modEnv = PlannerCatalogConstants.BuildModEnvironmentSpec(
            allModIds,
            _modHost,
            _baseRepo,
            customModeId: null);

        // 保留原剧本中已缺失但尚未从其它天移除的 Mod 记录，防止追加一天后丢失“缺 Mod”提示
        if (existing?.ModEnvironment?.Mods is { Count: > 0 })
        {
            var presentInSpec = new HashSet<string>(
                modEnv.Mods.Select(m => m.ModId),
                StringComparer.OrdinalIgnoreCase);
            foreach (var oldMod in existing.ModEnvironment.Mods)
            {
                if (oldMod == null || string.IsNullOrWhiteSpace(oldMod.ModId)) continue;
                if (presentInSpec.Add(oldMod.ModId.Trim()))
                    modEnv.Mods.Add(oldMod);
            }
        }

        var entity = new CustomGhostLineup
        {
            LineupId = finalId,
            AccountId = accountId,
            Name = finalName,
            Description = finalDescription,
            SourceType = finalSourceType,
            SourceCode = finalSourceCode,
            // 往已有方案里加一天时，方案的配套种子要原样保留
            SeedText = existing?.SeedText,
            SeedHero = existing?.SeedHero,
            ModEnvironment = modEnv,
            Days = days,
            CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };

        var (ok, errorCode, saved) = _db.SaveGhostLineup(
            entity,
            PlannerCatalogConstants.MaxLineups,
            PlannerCatalogConstants.MaxLineupJsonBytes,
            requireExisting: isUpdate);

        if (!ok || saved == null)
            return MapPersistenceSaveFailure<LineupSummaryDto>(errorCode, vr.Warnings);

        var currentRules = _ruleStore.Get(accountId);
        return GhostLineupResult<LineupSummaryDto>.Success(
            BuildSummary(saved, currentRules),
            vr.Warnings,
            day: targetDay);
    }

    // ── 内部工具方法 ────────────────────────────────────────────────────────

    private void PopulateSummaryFields(
        LineupSummaryDto target,
        CustomGhostLineup lineup,
        CustomRunRuleSet? currentRules)
    {
        target.LineupId = lineup.LineupId ?? "";
        target.Name = lineup.Name ?? "";
        target.Description = lineup.Description ?? "";
        target.SourceType = string.IsNullOrWhiteSpace(lineup.SourceType) ? "custom" : lineup.SourceType;
        target.SourceCode = string.IsNullOrWhiteSpace(lineup.SourceCode) ? null : lineup.SourceCode;
        target.SeedText = BazaarCore.Engine.DailyChallengeSeedGenerator.NormalizeSeedCode(lineup.SeedText);
        target.SeedHero = PlannerCatalogConstants.NormalizeHeroId(lineup.SeedHero);
        target.UpdatedAtUtc = lineup.UpdatedAtUtc;
        target.DayNumbers = lineup.Days != null
            ? lineup.Days.Keys
                .Where(d => d >= 1 && d <= PlannerCatalogConstants.MaxLineupDays)
                .OrderBy(d => d)
                .ToArray()
            : Array.Empty<int>();

        var serverDefaultIds = CollectServerDefaultModIds();
        var cardReferencedModIds = ResolveBoardModOriginsAcrossInstalledMods(lineup.Days);

        var requiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingList = new List<LineupMissingModDto>();

        if (lineup.ModEnvironment?.Mods is { Count: > 0 })
        {
            foreach (var entry in lineup.ModEnvironment.Mods)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ModId)) continue;
                string modId = entry.ModId.Trim();
                bool isReferencedByBoard = cardReferencedModIds.Contains(modId);

                if (entry.IsGameplay && !serverDefaultIds.Contains(modId))
                    requiredSet.Add(modId);

                if ((entry.IsGameplay || isReferencedByBoard) && !IsModPresentOnServer(modId, serverDefaultIds))
                {
                    if (missingSeen.Add(modId))
                    {
                        missingList.Add(new LineupMissingModDto
                        {
                            ModId = modId,
                            Name = string.IsNullOrWhiteSpace(entry.Name) ? modId : entry.Name.Trim(),
                        });
                    }
                }
            }
        }

        foreach (string modId in cardReferencedModIds)
        {
            if (!serverDefaultIds.Contains(modId))
                requiredSet.Add(modId);
        }

        target.RequiredModIds = requiredSet
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        target.MissingMods = missingList;
        target.IsSelected = currentRules != null
            && currentRules.GhostOpponentMode == EGhostOpponentMode.FixedLineup
            && string.Equals(currentRules.SelectedGhostLineupId, target.LineupId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>方案配套种子：和「下一局的种子」一样只认固定格式的种子码（S + 7 位），返回规范写法。</summary>
    public static bool TryNormalizeLineupSeed(string? raw, out string? display)
    {
        display = null;
        if (!BazaarCore.Engine.DailyChallengeSeedGenerator.TryParseSeedText(raw, out _, out string code))
            return false;
        display = code;
        return true;
    }

    private HashSet<string> CollectServerDefaultModIds()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_modHost == null) return set;
        foreach (var pkg in _modHost.Registry.LoadedPackages)
        {
            if (pkg?.Manifest != null && !string.IsNullOrWhiteSpace(pkg.Manifest.Id))
                set.Add(pkg.Manifest.Id.Trim());
        }
        return set;
    }

    private bool IsModPresentOnServer(string modId, HashSet<string> serverDefaultIds)
    {
        if (string.IsNullOrWhiteSpace(modId)) return false;
        string clean = modId.Trim();
        if (serverDefaultIds.Contains(clean))
            return true;
        var communityStore = EffectiveCommunityStore;
        if (communityStore != null && communityStore.ResolvePackage(clean) != null)
            return true;
        return false;
    }

    private HashSet<string> ResolveBoardModOriginsAcrossInstalledMods(Dictionary<int, PvpGhostPool.GhostBoard>? days)
    {
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (days == null || days.Count == 0) return origins;

        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in days.Values)
            ExtractTemplateIdsFromBoard(board, templateIds);
        if (templateIds.Count == 0) return origins;

        var activeRepo = _modHost?.ActiveRepository ?? _baseRepo;
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tid in templateIds)
        {
            var tpl = activeRepo.GetCard(tid);
            if (tpl != null)
            {
                if (tpl.IsCustomModCard && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin))
                    origins.Add(tpl.CustomModOrigin.Trim());
            }
            else
            {
                unresolved.Add(tid);
            }
        }

        if (unresolved.Count > 0 && EffectiveCommunityStore != null)
        {
            foreach (string modId in FindInstalledCommunityModIdsDefiningCards(unresolved))
                origins.Add(modId);
        }

        return origins;
    }

    private (
        GameDataRepository EffectiveRepo,
        CardPool EffectivePool,
        IReadOnlySet<string>? ExplicitAllowedModIds,
        List<string> ExtraReferencedModIds) ResolveValidationEnvironment(
            IReadOnlyList<string>? selectedModIds,
            IEnumerable<string>? referencedTemplateIds)
    {
        if (_modHost == null)
            return (_baseRepo, _basePool, null, new List<string>());

        var monsters = ResolveBaseMonsters();
        var archetypes = ResolveBaseArchetypes();
        var selectedCtx = _modHost.ResolveRunModContext(selectedModIds, _basePool, monsters, archetypes);
        var explicitAllowed = new HashSet<string>(selectedCtx.ActiveModIds, StringComparer.OrdinalIgnoreCase);

        if (referencedTemplateIds == null)
            return (selectedCtx.Repository, selectedCtx.Pool, explicitAllowed, new List<string>());

        var missingFromSelected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string tid in referencedTemplateIds)
        {
            if (string.IsNullOrWhiteSpace(tid)) continue;
            if (selectedCtx.Repository.GetCard(tid) == null)
                missingFromSelected.Add(tid);
        }

        if (missingFromSelected.Count == 0)
            return (selectedCtx.Repository, selectedCtx.Pool, explicitAllowed, new List<string>());

        var extraModIds = FindInstalledCommunityModIdsDefiningCards(missingFromSelected);
        if (extraModIds.Count == 0)
            return (selectedCtx.Repository, selectedCtx.Pool, explicitAllowed, extraModIds);

        var combinedIds = MergeModIds(selectedModIds, extraModIds, null);
        var combinedCtx = _modHost.ResolveRunModContext(combinedIds, _basePool, monsters, archetypes);
        return (combinedCtx.Repository, combinedCtx.Pool, explicitAllowed, extraModIds);
    }

    private List<string> FindInstalledCommunityModIdsDefiningCards(HashSet<string> missingTemplateIds)
    {
        var foundModIds = new List<string>();
        var communityStore = EffectiveCommunityStore;
        if (missingTemplateIds.Count == 0 || communityStore == null)
            return foundModIds;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var catalog = communityStore.ListCatalog(requesterAccountId: null, isAdmin: true);
        foreach (var item in catalog)
        {
            if (string.IsNullOrWhiteSpace(item.ModId)) continue;
            if (!item.IsInCommunityStore && !item.IsServerDefault) continue;

            ModPackage? pkg = communityStore.FindCommunityPackage(item.ModId)
                ?? (item.IsServerDefault ? _modHost?.FindDiscoveredPackage(item.ModId) : null);
            if (pkg?.Cards is not { Count: > 0 }) continue;

            foreach (var cardDef in pkg.Cards)
            {
                if (cardDef != null && !string.IsNullOrWhiteSpace(cardDef.CustomId) &&
                    missingTemplateIds.Contains(cardDef.CustomId.Trim()))
                {
                    string modId = pkg.Manifest.Id.Trim();
                    if (seen.Add(modId))
                        foundModIds.Add(modId);
                    break;
                }
            }
        }

        return foundModIds;
    }

    private List<BazaarServer.Data.MonsterTemplate> ResolveBaseMonsters()
    {
        if (_monsterPoolStore != null)
            return _monsterPoolStore.SnapshotList();
        return _fallbackMonsters ?? new List<BazaarServer.Data.MonsterTemplate>();
    }

    private CardArchetypeIndex ResolveBaseArchetypes()
    {
        if (_snapshotProvider != null)
            return _snapshotProvider.Current.ArchetypeIndex;
        if (_fallbackArchetypes != null)
            return _fallbackArchetypes;

        lock (_archetypeInitLock)
        {
            _fallbackArchetypes ??= CardArchetypeIndex.Build(_basePool, _baseRepo);
            return _fallbackArchetypes;
        }
    }

    private static PvpGhostPool.GhostBoard NormalizeLineupBoard(
        PvpGhostPool.GhostBoard b,
        int day,
        string lineupId) => new()
    {
        BoardId = string.IsNullOrWhiteSpace(b.BoardId) ? $"lineup_{lineupId}_d{day}" : b.BoardId.Trim(),
        Day = day,
        SourceDay = day,
        TargetDay = day,
        Hero = b.Hero,
        Level = b.Level,
        HealthMax = b.HealthMax,
        Prestige = b.Prestige,
        Wins = b.Wins,
        Losses = b.Losses,
        UnlockedSlotsMask = b.UnlockedSlotsMask,
        PowerBucket = b.PowerBucket,
        EstimatedWinRate = b.EstimatedWinRate,
        Source = b.Source,
        Persona = b.Persona,
        Archetype = b.Archetype,
        IsPlayerGhost = b.IsPlayerGhost,
        IsCustomRule = b.IsCustomRule,
        PoolPartition = b.PoolPartition,
        DisplayName = b.DisplayName,
        Items = b.Items ?? new List<PvpGhostPool.GhostItem>(),
        Skills = b.Skills ?? new List<PvpGhostPool.GhostSkill>(),
        SocketEffects = b.SocketEffects,
    };

    private static void CollectBoardCardModOrigins(
        PvpGhostPool.GhostBoard board,
        GameDataRepository repo,
        HashSet<string> sink)
    {
        if (board.Items != null)
        {
            foreach (var it in board.Items)
            {
                if (it == null || string.IsNullOrWhiteSpace(it.TemplateId)) continue;
                var tpl = repo.GetCard(it.TemplateId);
                if (tpl != null && tpl.IsCustomModCard && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin))
                    sink.Add(tpl.CustomModOrigin.Trim());
            }
        }
        if (board.Skills != null)
        {
            foreach (var sk in board.Skills)
            {
                if (sk == null || string.IsNullOrWhiteSpace(sk.TemplateId)) continue;
                var tpl = repo.GetCard(sk.TemplateId);
                if (tpl != null && tpl.IsCustomModCard && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin))
                    sink.Add(tpl.CustomModOrigin.Trim());
            }
        }
        if (board.SocketEffects != null)
        {
            foreach (var se in board.SocketEffects)
            {
                if (se == null || string.IsNullOrWhiteSpace(se.TemplateId)) continue;
                var tpl = repo.GetCard(se.TemplateId);
                if (tpl != null && tpl.IsCustomModCard && !string.IsNullOrWhiteSpace(tpl.CustomModOrigin))
                    sink.Add(tpl.CustomModOrigin.Trim());
            }
        }
    }

    private static HashSet<string> ExtractTemplateIdsFromJson(JsonElement root, HashSet<string>? target = null)
    {
        var ids = target ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object) return ids;

        CollectArrayTemplateIds(root, "items", ids);
        CollectArrayTemplateIds(root, "skills", ids);
        CollectArrayTemplateIds(root, "socketEffects", ids);
        return ids;
    }

    private static void CollectArrayTemplateIds(JsonElement obj, string propName, HashSet<string> sink)
    {
        if (!TryGetPropertyCI(obj, propName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (TryGetPropertyCI(item, "templateId", out var tidEl) && tidEl.ValueKind == JsonValueKind.String)
            {
                string? tid = tidEl.GetString();
                if (!string.IsNullOrWhiteSpace(tid))
                    sink.Add(tid.Trim());
            }
        }
    }

    private static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static HashSet<string> ExtractTemplateIdsFromBoard(PvpGhostPool.GhostBoard? board, HashSet<string>? target = null)
    {
        var ids = target ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (board == null) return ids;

        if (board.Items != null)
        {
            foreach (var it in board.Items)
            {
                if (it != null && !string.IsNullOrWhiteSpace(it.TemplateId))
                    ids.Add(it.TemplateId.Trim());
            }
        }
        if (board.Skills != null)
        {
            foreach (var sk in board.Skills)
            {
                if (sk != null && !string.IsNullOrWhiteSpace(sk.TemplateId))
                    ids.Add(sk.TemplateId.Trim());
            }
        }
        if (board.SocketEffects != null)
        {
            foreach (var se in board.SocketEffects)
            {
                if (se != null && !string.IsNullOrWhiteSpace(se.TemplateId))
                    ids.Add(se.TemplateId.Trim());
            }
        }
        return ids;
    }

    private static List<string> MergeModIds(
        IEnumerable<string>? a,
        IEnumerable<string>? b,
        IEnumerable<string>? c)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Append(IEnumerable<string>? src)
        {
            if (src == null) return;
            foreach (string raw in src)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string trimmed = raw.Trim();
                if (seen.Add(trimmed))
                    list.Add(trimmed);
            }
        }

        Append(a);
        Append(b);
        Append(c);
        return list;
    }

    private static GhostLineupResult<T> MapPersistenceSaveFailure<T>(string? errorCode, List<string> warnings) =>
        errorCode switch
        {
            "NOT_FOUND" => GhostLineupResult<T>.Failure(
                404,
                "NOT_FOUND",
                "方案不存在（可能已经在别处删掉了）",
                warnings: warnings),
            "LINEUP_LIMIT" => GhostLineupResult<T>.Failure(
                409,
                "LINEUP_LIMIT",
                $"方案最多 {PlannerCatalogConstants.MaxLineups} 套",
                warnings: warnings),
            "PAYLOAD_TOO_LARGE" => GhostLineupResult<T>.Failure(
                413,
                "PAYLOAD_TOO_LARGE",
                "方案数据超过 64 KB 上限，请精简天数或配置后再保存",
                warnings: warnings),
            _ => GhostLineupResult<T>.Failure(
                400,
                string.IsNullOrWhiteSpace(errorCode) ? "SAVE_FAILED" : errorCode,
                "保存方案失败",
                warnings: warnings),
        };
}

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Cryptography;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Mods;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace BazaarServer.Planner;

/// <summary>事件目录单条记录（对应 GET /api/planner/catalog/events 的 items 元素）。</summary>
public sealed class CatalogEventItemDto
{
    public string TemplateId { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string Kind { get; set; } = "event"; // event | merchant | monster | pedestal
    public int MinDay { get; set; } = 1;
    public int MaxDay { get; set; } = 10;
    public bool IsMod { get; set; }
    public string? ModId { get; set; }
}

/// <summary>单种子日历预览请求（POST /api/planner/seed-preview）。</summary>
public sealed class SeedPreviewRequest
{
    public string? SeedText { get; set; }
    public string? Hero { get; set; } = "Vanessa";
    public int MaxDay { get; set; } = 5;
    public List<string>? SelectedModIds { get; set; }
    public bool UseMyRules { get; set; }
}

/// <summary>日历单小时内的单个候选项。</summary>
public sealed class SeedCalendarOptionDto
{
    public string TemplateId { get; set; } = "";
    public string NameZh { get; set; } = "";
    public string Kind { get; set; } = "event"; // event | merchant | monster | pedestal
}

/// <summary>日历单小时（Hour 1..5）的候选列表。</summary>
public sealed class SeedCalendarHourDto
{
    public int Hour { get; set; }
    public List<SeedCalendarOptionDto> Options { get; set; } = new();
}

/// <summary>日历单天（Day 1..N）的 5 小时推演结果。</summary>
public sealed class SeedCalendarDayDto
{
    public int Day { get; set; }
    public List<SeedCalendarHourDto> Hours { get; set; } = new();
}

/// <summary>单种子日历预览响应载荷。</summary>
public sealed class SeedPreviewResponse
{
    public string SeedText { get; set; } = "";
    public int MasterSeed { get; set; }
    public string Hero { get; set; } = "";
    public List<SeedCalendarDayDto> Calendar { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}

/// <summary>单种子日历预览业务结果封装。</summary>
public sealed record SeedPreviewResult(
    bool Ok,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessageZh,
    SeedPreviewResponse? Data);

/// <summary>批量搜种单个目标条件。</summary>
public sealed class SeedFinderConditionRequest
{
    public string? TemplateId { get; set; }
    public int MinDay { get; set; } = 1;
    public int MaxDay { get; set; } = 10;
}

/// <summary>批量搜种任务提交请求（POST /api/planner/seed-finder/jobs）。</summary>
public sealed class SeedFinderJobRequest
{
    public string? Hero { get; set; } = "Vanessa";
    public List<string>? SelectedModIds { get; set; }
    public bool UseMyRules { get; set; }
    public int MaxDay { get; set; } = 10;
    public int? StartSeed { get; set; }
    public List<SeedFinderConditionRequest>? Conditions { get; set; }
}

/// <summary>搜种条件可行性校验失败明细。</summary>
public sealed class SeedFinderImpossibleDetail
{
    public string TemplateId { get; set; } = "";
    public string ReasonZh { get; set; } = "";
}

/// <summary>搜种任务启动结果。</summary>
public sealed record SeedFinderStartResult(
    bool Ok,
    int StatusCode,
    string? ErrorCode,
    string? ErrorMessageZh,
    List<SeedFinderImpossibleDetail>? Details,
    string? JobId);

/// <summary>搜种命中的单个条件出现位置。</summary>
public sealed class SeedFinderMatchedEntry
{
    public string TemplateId { get; set; } = "";
    public string NameZh { get; set; } = "";
    public int Day { get; set; }
    public int Hour { get; set; }
}

/// <summary>搜种命中的单个候选种子记录。</summary>
public sealed class SeedFinderHitDto
{
    public string SeedText { get; set; } = "";
    public int MasterSeed { get; set; }
    public List<SeedFinderMatchedEntry> Matched { get; set; } = new();
}

/// <summary>搜种任务状态查询响应（GET /api/planner/seed-finder/jobs/{id}）。</summary>
public sealed class SeedFinderJobStatusDto
{
    public string JobId { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued | running | done | cancelled | failed
    public int Scanned { get; set; }
    public long ElapsedMs { get; set; }
    public List<SeedFinderHitDto> Hits { get; set; } = new();
    public int NextSeed { get; set; }
    public string MessageZh { get; set; } = "";
}

/// <summary>
/// M3 事件目录、单种子日历预览与后台批量搜种服务：
/// - 推演路径完全复用 ShopEngine.DealHourEncounters + RunState.ApplyNewDayEncounterBookkeeping + RunRandom，
///   与 RunEngine 跑 Day 1..N 不选任何事件的每小时 SelectionSet 逐卡 100% 一致；
/// - 每个候选种子新建独立 RunState / ShopEngine / RunRandom，零 DB 读写、零 SessionStore 访问、零全局可变状态修改；
/// - 后台搜种全局并发上限 2（SemaphoreSlim），排队态 status="queued"，同账号新任务自动取消旧任务，
///   支持早退、命中 20 个 / 60 秒超时 / 手动取消与完成 10 分钟后自动清理。
/// </summary>
public sealed class EventSeedFinderService : IDisposable
{
    public const int MaxSimulationDay = 10;
    public const int MaxConditions = 5;
    public const int DefaultMaxConcurrentJobs = 2;
    public const int DefaultMaxHits = 20;
    public static readonly TimeSpan DefaultMaxJobDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan DefaultJobRetention = TimeSpan.FromMinutes(10);

    /// <summary>计划第 9.4 节规定的单种子日历说明文案。</summary>
    public const string SimulationDisclaimerNoteZh =
        "日历按\"每小时都不进入任何事件\"推演。你实际选了某个事件后，后面的选项可能因为排除规则而变化；自定义模式若在每天开始时改动发牌，预览也可能不准。";

    private static readonly int[] DaytimeHours = [1, 2, 4, 5];

    private sealed class EligibleEventMeta
    {
        public required string TemplateId { get; init; }
        public required string NameZh { get; init; }
        public required string Kind { get; init; }
        public required bool IsMod { get; init; }
        public string? ModId { get; init; }
        public SortedSet<int> EligibleDays { get; } = new();
    }

    private sealed record NormalizedCondition(
        string TemplateId,
        string NameZh,
        int MinDay,
        int MaxDay);

    private sealed class JobState
    {
        public readonly object Sync = new();
        public required string JobId { get; init; }
        public required string AccountId { get; init; }
        public required int StartSeed { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public CancellationTokenSource Cts { get; } = new();

        public string Status { get; private set; } = "queued";
        public int Scanned { get; private set; }
        public long ElapsedMs { get; private set; }
        public int NextSeed { get; private set; }
        public string MessageZh { get; private set; } = "排队中";
        public DateTime? CompletedAtUtc { get; private set; }
        public bool IsTerminal => Status is "done" or "cancelled" or "failed";
        private Stopwatch? _runWatch;
        private readonly List<SeedFinderHitDto> _hits = new();

        public int HitCount
        {
            get
            {
                lock (Sync)
                    return _hits.Count;
            }
        }

        public void InitializeNextSeed(int startSeed)
        {
            lock (Sync)
                NextSeed = startSeed;
        }

        public bool TryMarkRunning(Stopwatch runWatch, CancellationToken ct)
        {
            lock (Sync)
            {
                if (IsTerminal || ct.IsCancellationRequested)
                    return false;
                Status = "running";
                MessageZh = "搜索中";
                _runWatch = runWatch;
                return true;
            }
        }

        public int RecordScanResult(int scannedSeed, int nextSeed, List<SeedFinderMatchedEntry>? matched, long elapsedMs)
        {
            lock (Sync)
            {
                if (IsTerminal)
                    return _hits.Count;

                Scanned++;
                NextSeed = nextSeed;
                ElapsedMs = elapsedMs;
                if (matched != null)
                {
                    _hits.Add(new SeedFinderHitDto
                    {
                        SeedText = DailyChallengeSeedGenerator.FormatSeedCode(scannedSeed),
                        MasterSeed = scannedSeed,
                        Matched = matched,
                    });
                }
                return _hits.Count;
            }
        }

        public void MarkFinished(string terminalStatus, string messageZh, DateTime completedAtUtc, long? finalElapsedMs = null)
        {
            lock (Sync)
            {
                if (finalElapsedMs.HasValue)
                    ElapsedMs = Math.Max(ElapsedMs, finalElapsedMs.Value);
                else if (_runWatch != null)
                    ElapsedMs = Math.Max(ElapsedMs, _runWatch.ElapsedMilliseconds);

                if (IsTerminal)
                    return;

                Status = terminalStatus;
                MessageZh = messageZh;
                CompletedAtUtc = completedAtUtc;
            }
        }

        public void RequestCancel(DateTime nowUtc)
        {
            lock (Sync)
            {
                if (!IsTerminal)
                {
                    if (_runWatch != null)
                        ElapsedMs = Math.Max(ElapsedMs, _runWatch.ElapsedMilliseconds);
                    Status = "cancelled";
                    MessageZh = "已停止";
                    CompletedAtUtc = nowUtc;
                }
            }

            try { Cts.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public SeedFinderJobStatusDto ToSnapshotDto()
        {
            lock (Sync)
            {
                long elapsed = Status == "running" && _runWatch != null
                    ? Math.Max(ElapsedMs, _runWatch.ElapsedMilliseconds)
                    : ElapsedMs;
                var hitsCopy = new List<SeedFinderHitDto>(_hits.Count);
                for (int i = 0; i < _hits.Count; i++)
                {
                    var h = _hits[i];
                    hitsCopy.Add(new SeedFinderHitDto
                    {
                        SeedText = h.SeedText,
                        MasterSeed = h.MasterSeed,
                        Matched = new List<SeedFinderMatchedEntry>(h.Matched),
                    });
                }
                return new SeedFinderJobStatusDto
                {
                    JobId = JobId,
                    Status = Status,
                    Scanned = Scanned,
                    ElapsedMs = elapsed,
                    Hits = hitsCopy,
                    NextSeed = NextSeed,
                    MessageZh = MessageZh,
                };
            }
        }
    }

    private readonly ServerModHost? _modHost;
    private readonly CardPool _basePool;
    private readonly MonsterPoolStore? _monsterPoolStore;
    private readonly ServerRuntimeSnapshotProvider? _runtimeSnapshots;
    private readonly List<BazaarServer.Data.MonsterTemplate>? _fallbackMonsters;
    private readonly CardArchetypeIndex? _fallbackArchetypes;
    private readonly GameDataRepository? _fallbackRepo;

    private readonly SemaphoreSlim _concurrencyGate;
    private readonly TimeSpan _maxJobDuration;
    private readonly int _maxHits;
    private readonly TimeSpan _jobRetention;
    private readonly Func<DateTime> _utcNow;

    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _activeJobIdByAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _accountActiveLock = new();
    private int _disposed;

    /// <summary>当前内存中保留的搜种任务总数（含进行中与未过期历史任务）。</summary>
    public int TrackedJobCount => _jobs.Count;

    [ActivatorUtilitiesConstructor]
    public EventSeedFinderService(
        ServerModHost modHost,
        CardPool basePool,
        MonsterPoolStore monsterPoolStore,
        ServerRuntimeSnapshotProvider runtimeSnapshots)
    {
        _modHost = modHost ?? throw new ArgumentNullException(nameof(modHost));
        _basePool = basePool ?? throw new ArgumentNullException(nameof(basePool));
        _monsterPoolStore = monsterPoolStore ?? throw new ArgumentNullException(nameof(monsterPoolStore));
        _runtimeSnapshots = runtimeSnapshots ?? throw new ArgumentNullException(nameof(runtimeSnapshots));
        _concurrencyGate = new SemaphoreSlim(DefaultMaxConcurrentJobs, DefaultMaxConcurrentJobs);
        _maxJobDuration = DefaultMaxJobDuration;
        _maxHits = DefaultMaxHits;
        _jobRetention = DefaultJobRetention;
        _utcNow = () => DateTime.UtcNow;
    }

    /// <summary>
    /// 供单元测试与独立宿主使用的构造重载：支持注入最大并发度、最大任务时长、最大命中数与保留时长。
    /// </summary>
    public EventSeedFinderService(
        ServerModHost? modHost,
        CardPool basePool,
        List<BazaarServer.Data.MonsterTemplate> baseMonsters,
        CardArchetypeIndex baseArchetypes,
        GameDataRepository? baseRepo = null,
        int maxConcurrentJobs = DefaultMaxConcurrentJobs,
        TimeSpan? maxJobDuration = null,
        int maxHits = DefaultMaxHits,
        TimeSpan? jobRetention = null,
        Func<DateTime>? utcNowProvider = null)
    {
        _modHost = modHost;
        _basePool = basePool ?? throw new ArgumentNullException(nameof(basePool));
        _fallbackMonsters = baseMonsters ?? throw new ArgumentNullException(nameof(baseMonsters));
        _fallbackArchetypes = baseArchetypes ?? throw new ArgumentNullException(nameof(baseArchetypes));
        _fallbackRepo = baseRepo ?? modHost?.ActiveRepository;
        if (_modHost == null && _fallbackRepo == null)
            throw new ArgumentNullException(nameof(baseRepo), "当未提供 ServerModHost 时必须传入 baseRepo。");

        int concurrency = Math.Max(1, maxConcurrentJobs);
        _concurrencyGate = new SemaphoreSlim(concurrency, concurrency);
        _maxJobDuration = maxJobDuration is { Ticks: > 0 } dur ? dur : DefaultMaxJobDuration;
        _maxHits = Math.Max(1, maxHits);
        _jobRetention = jobRetention is { Ticks: > 0 } ret ? ret : DefaultJobRetention;
        _utcNow = utcNowProvider ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// 1. 事件目录（供 GET /api/planner/catalog/events）：
    /// 返回指定英雄与 Mod 环境下在 Day 1..10 内可能出现的全部事件 / 商人 / 祭坛 / 野怪及其准确天数窗。
    /// </summary>
    public List<CatalogEventItemDto> ListCatalogEvents(string? hero, IReadOnlyList<string>? selectedModIds)
    {
        string? normalizedHero = string.IsNullOrWhiteSpace(hero)
            ? "Vanessa"
            : PlannerCatalogConstants.NormalizeHeroId(hero);
        if (normalizedHero == null)
            return new List<CatalogEventItemDto>();

        var modCtx = ResolveModContext(selectedModIds);
        var stdRules = BuildStandardSimulationRules(selectedModIds, modCtx);
        var map = BuildEligibleCatalogMap(normalizedHero, stdRules, modCtx);

        var items = new List<CatalogEventItemDto>(map.Count);
        foreach (var meta in map.Values)
        {
            if (meta.EligibleDays.Count == 0)
                continue;

            items.Add(new CatalogEventItemDto
            {
                TemplateId = meta.TemplateId,
                NameZh = meta.NameZh,
                Kind = meta.Kind,
                MinDay = meta.EligibleDays.Min,
                MaxDay = meta.EligibleDays.Max,
                IsMod = meta.IsMod,
                ModId = meta.ModId,
            });
        }

        items.Sort((a, b) =>
        {
            int k = KindSortOrder(a.Kind).CompareTo(KindSortOrder(b.Kind));
            if (k != 0) return k;
            int d = a.MinDay.CompareTo(b.MinDay);
            if (d != 0) return d;
            int n = StringComparer.Ordinal.Compare(a.NameZh, b.NameZh);
            if (n != 0) return n;
            return StringComparer.OrdinalIgnoreCase.Compare(a.TemplateId, b.TemplateId);
        });

        return items;
    }

    /// <summary>
    /// 2. 单种子日历预览（POST /api/planner/seed-preview）：
    /// 校验种子并按「每小时不进入任何事件」独立推演 Day 1..N（N 钳制 1..10）的 H1~H5 全部候选项。
    /// </summary>
    public SeedPreviewResult PreviewSeed(SeedPreviewRequest? req, CustomRunRuleSet? playerRules = null)
    {
        if (req == null)
        {
            return new SeedPreviewResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: "请求体不能为空。",
                Data: null);
        }

        if (!DailyChallengeSeedGenerator.TryParseSeedText(req.SeedText, out int masterSeed, out string displaySeedText))
        {
            return new SeedPreviewResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "SEED_INVALID",
                ErrorMessageZh: "种子不能为空，请输入有效的种子码或文本。",
                Data: null);
        }

        string? normalizedHero = string.IsNullOrWhiteSpace(req.Hero)
            ? "Vanessa"
            : PlannerCatalogConstants.NormalizeHeroId(req.Hero);
        if (normalizedHero == null || !Enum.TryParse<EHero>(normalizedHero, ignoreCase: true, out var heroEnum))
        {
            return new SeedPreviewResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: $"不支持的英雄：{req.Hero}",
                Data: null);
        }

        int maxDay = Math.Clamp(req.MaxDay <= 0 ? 5 : req.MaxDay, 1, MaxSimulationDay);
        var (modCtx, simRules, usedPlayerRules) = ResolveSimulationEnvironment(req.UseMyRules, req.SelectedModIds, playerRules);

        var calendar = SimulateCalendar(masterSeed, heroEnum, maxDay, simRules, modCtx);
        var notes = new List<string>(2)
        {
            usedPlayerRules
                ? "当前按你的游戏内规则与已选 Mod 环境推演。"
                : "当前按标准规则与所选 Mod 环境推演。",
            SimulationDisclaimerNoteZh,
        };

        return new SeedPreviewResult(
            Ok: true,
            StatusCode: StatusCodes.Status200OK,
            ErrorCode: null,
            ErrorMessageZh: null,
            Data: new SeedPreviewResponse
            {
                SeedText = displaySeedText,
                MasterSeed = masterSeed,
                Hero = normalizedHero,
                Calendar = calendar,
                Notes = notes,
            });
    }

    /// <summary>
    /// 3. 启动后台批量搜种任务（POST /api/planner/seed-finder/jobs）：
    /// 先执行严格可行性校验（不可能条件直接返回 400 CONDITION_IMPOSSIBLE + details），
    /// 通过后取消同账号旧任务并在全局并发门控（默认 2）下异步扫描。
    /// </summary>
    public SeedFinderStartResult StartJob(
        string accountId,
        SeedFinderJobRequest? req,
        CustomRunRuleSet? playerRules = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status503ServiceUnavailable,
                ErrorCode: "SERVICE_STOPPING",
                ErrorMessageZh: "服务正在关闭，暂时无法启动搜种任务。",
                Details: null,
                JobId: null);
        }

        string normAccountId = PlannerSessionStore.NormalizeAccountId(accountId);
        if (string.IsNullOrEmpty(normAccountId))
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status401Unauthorized,
                ErrorCode: "AUTH_REQUIRED",
                ErrorMessageZh: "请先登录后再启动搜种任务。",
                Details: null,
                JobId: null);
        }

        if (req == null)
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: "请求体不能为空。",
                Details: null,
                JobId: null);
        }

        string? normalizedHero = string.IsNullOrWhiteSpace(req.Hero)
            ? "Vanessa"
            : PlannerCatalogConstants.NormalizeHeroId(req.Hero);
        if (normalizedHero == null || !Enum.TryParse<EHero>(normalizedHero, ignoreCase: true, out var heroEnum))
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: $"不支持的英雄：{req.Hero}",
                Details: null,
                JobId: null);
        }

        if (req.Conditions == null || req.Conditions.Count == 0)
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: "请至少设置 1 个搜种条件。",
                Details: null,
                JobId: null);
        }

        if (req.Conditions.Count > MaxConditions)
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "BAD_REQUEST",
                ErrorMessageZh: $"单次最多设置 {MaxConditions} 个搜种条件。",
                Details: null,
                JobId: null);
        }

        int jobMaxDay = Math.Clamp(req.MaxDay <= 0 ? MaxSimulationDay : req.MaxDay, 1, MaxSimulationDay);
        var (modCtx, simRules, _) = ResolveSimulationEnvironment(req.UseMyRules, req.SelectedModIds, playerRules);

        // 可行性校验：逐条验证模板存在、英雄/规则未禁用且天数窗存在交集
        var eligibleMap = BuildEligibleCatalogMap(normalizedHero, simRules, modCtx);
        var impossibleDetails = new List<SeedFinderImpossibleDetail>();
        var normalizedConditions = new List<NormalizedCondition>(req.Conditions.Count);

        foreach (var cond in req.Conditions)
        {
            string rawTplId = cond?.TemplateId?.Trim() ?? string.Empty;
            int condMin = cond?.MinDay ?? 1;
            int condMax = cond?.MaxDay ?? jobMaxDay;

            if (string.IsNullOrEmpty(rawTplId))
            {
                impossibleDetails.Add(new SeedFinderImpossibleDetail
                {
                    TemplateId = rawTplId,
                    ReasonZh = "事件模板 ID 不能为空",
                });
                continue;
            }

            if (condMin > condMax)
            {
                impossibleDetails.Add(new SeedFinderImpossibleDetail
                {
                    TemplateId = rawTplId,
                    ReasonZh = $"条件天数区间无效（最早第 {condMin} 天晚于最晚第 {condMax} 天）",
                });
                continue;
            }

            if (!eligibleMap.TryGetValue(rawTplId, out var meta) || meta.EligibleDays.Count == 0)
            {
                string reason = DiagnoseIneligibleTemplateReason(rawTplId, normalizedHero, jobMaxDay, simRules, modCtx);
                impossibleDetails.Add(new SeedFinderImpossibleDetail
                {
                    TemplateId = rawTplId,
                    ReasonZh = reason,
                });
                continue;
            }

            int effMin = Math.Max(1, condMin);
            int effMax = Math.Min(jobMaxDay, condMax);
            if (effMin > effMax)
            {
                impossibleDetails.Add(new SeedFinderImpossibleDetail
                {
                    TemplateId = rawTplId,
                    ReasonZh = $"条件天数范围（第 {condMin}~{condMax} 天）超出本次搜索上限（第 1~{jobMaxDay} 天）",
                });
                continue;
            }

            bool hasIntersection = false;
            foreach (int d in meta.EligibleDays)
            {
                if (d >= effMin && d <= effMax)
                {
                    hasIntersection = true;
                    break;
                }
            }

            if (!hasIntersection)
            {
                string daysText = FormatDaySetZh(meta.EligibleDays);
                string suffix = meta.EligibleDays.Min > jobMaxDay
                    ? $"（当前搜索上限为第 {jobMaxDay} 天）"
                    : string.Empty;
                impossibleDetails.Add(new SeedFinderImpossibleDetail
                {
                    TemplateId = rawTplId,
                    ReasonZh = $"该事件只在第 {daysText} 天出现{suffix}",
                });
                continue;
            }

            normalizedConditions.Add(new NormalizedCondition(
                TemplateId: meta.TemplateId,
                NameZh: meta.NameZh,
                MinDay: effMin,
                MaxDay: effMax));
        }

        if (impossibleDetails.Count > 0)
        {
            return new SeedFinderStartResult(
                Ok: false,
                StatusCode: StatusCodes.Status400BadRequest,
                ErrorCode: "CONDITION_IMPOSSIBLE",
                ErrorMessageZh: "存在不可能满足的搜种条件，请调整天数或 Mod 环境。",
                Details: impossibleDetails,
                JobId: null);
        }

        int startSeed = req.StartSeed.HasValue
            ? (req.StartSeed.Value == 0 ? 1 : req.StartSeed.Value)
            : RandomNumberGenerator.GetInt32(1, int.MaxValue);

        int effectiveSearchMaxDay = Math.Min(jobMaxDay, normalizedConditions.Max(c => c.MaxDay));
        DateTime now = _utcNow();
        var job = new JobState
        {
            JobId = "job_" + Guid.NewGuid().ToString("N")[..16],
            AccountId = normAccountId,
            StartSeed = startSeed,
            CreatedAtUtc = now,
        };
        job.InitializeNextSeed(startSeed);

        JobState? previousJobToCancel = null;
        lock (_accountActiveLock)
        {
            if (_activeJobIdByAccount.TryGetValue(normAccountId, out string? oldJobId) &&
                !string.IsNullOrEmpty(oldJobId) &&
                _jobs.TryGetValue(oldJobId, out var oldJob))
            {
                previousJobToCancel = oldJob;
            }
            _jobs[job.JobId] = job;
            _activeJobIdByAccount[normAccountId] = job.JobId;
        }

        previousJobToCancel?.RequestCancel(now);

        var condArray = normalizedConditions.ToArray();
        var jobCt = job.Cts.Token;
        _ = Task.Run(() => RunJobAsync(job, jobCt, heroEnum, effectiveSearchMaxDay, condArray, simRules, modCtx));

        return new SeedFinderStartResult(
            Ok: true,
            StatusCode: StatusCodes.Status200OK,
            ErrorCode: null,
            ErrorMessageZh: null,
            Details: null,
            JobId: job.JobId);
    }

    /// <summary>查询本人搜种任务进度与已命中结果（不存在或非本人返回 null，供端点返 404 NOT_FOUND）。</summary>
    public SeedFinderJobStatusDto? GetJob(string accountId, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;

        string normAccountId = PlannerSessionStore.NormalizeAccountId(accountId);
        if (string.IsNullOrEmpty(normAccountId))
            return null;

        if (!_jobs.TryGetValue(jobId.Trim(), out var job))
            return null;

        if (!string.Equals(job.AccountId, normAccountId, StringComparison.OrdinalIgnoreCase))
            return null;

        return job.ToSnapshotDto();
    }

    /// <summary>取消本人搜种任务（不存在或非本人返回 false，供端点返 404 NOT_FOUND）。</summary>
    public bool CancelJob(string accountId, string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;

        string normAccountId = PlannerSessionStore.NormalizeAccountId(accountId);
        if (string.IsNullOrEmpty(normAccountId))
            return false;

        if (!_jobs.TryGetValue(jobId.Trim(), out var job))
            return false;

        if (!string.Equals(job.AccountId, normAccountId, StringComparison.OrdinalIgnoreCase))
            return false;

        job.RequestCancel(_utcNow());
        ClearActiveAccountJobIfMatches(normAccountId, job.JobId);
        return true;
    }

    /// <summary>清理已完成/已取消/已失败超过 10 分钟的历史搜种任务（供 Program.cs 定时维护协程调用）。</summary>
    public int PruneExpiredJobs()
    {
        DateTime cutoff = _utcNow() - _jobRetention;
        int removed = 0;

        foreach (var kv in _jobs)
        {
            var job = kv.Value;
            bool expired;
            lock (job.Sync)
            {
                expired = job.IsTerminal && job.CompletedAtUtc.HasValue && job.CompletedAtUtc.Value <= cutoff;
            }

            if (expired && _jobs.TryRemove(kv.Key, out var removedJob))
            {
                ClearActiveAccountJobIfMatches(removedJob.AccountId, removedJob.JobId);
                try { removedJob.Cts.Dispose(); }
                catch (ObjectDisposedException) { }
                removed++;
            }
        }

        return removed;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        DateTime now = _utcNow();
        foreach (var job in _jobs.Values)
            job.RequestCancel(now);
    }

    // ── 核心后台扫描与单种子推演 ─────────────────────────────────────────────

    private async Task RunJobAsync(
        JobState job,
        CancellationToken ct,
        EHero hero,
        int maxDay,
        NormalizedCondition[] conditions,
        CustomRunRuleSet simRules,
        ServerModHost.RunModContext modCtx)
    {
        bool acquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            var sw = Stopwatch.StartNew();
            if (!job.TryMarkRunning(sw, ct))
            {
                job.MarkFinished("cancelled", "已停止", _utcNow(), 0);
                return;
            }

            long maxDurationMs = Math.Max(1, (long)_maxJobDuration.TotalMilliseconds);
            int seed = job.StartSeed;
            bool timedOut = false;
            var startProfile = BuildSimulationStartProfile(simRules, modCtx);

            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
            {
                if (sw.ElapsedMilliseconds >= maxDurationMs)
                {
                    timedOut = true;
                    break;
                }

                var matched = SimulateAndMatchSeed(seed, hero, maxDay, conditions, startProfile, modCtx);
                int nextSeed = NextNonZeroSeed(seed);
                int hitCount = job.RecordScanResult(seed, nextSeed, matched, sw.ElapsedMilliseconds);
                seed = nextSeed;

                if (hitCount >= _maxHits)
                    break;

                if (sw.ElapsedMilliseconds >= maxDurationMs)
                {
                    timedOut = true;
                    break;
                }
            }

            sw.Stop();
            long totalMs = sw.ElapsedMilliseconds;
            int finalHits = job.HitCount;

            if (ct.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                job.MarkFinished("cancelled", "已停止", _utcNow(), totalMs);
            }
            else if (finalHits >= _maxHits)
            {
                job.MarkFinished("done", $"已找到 {finalHits} 个匹配种子", _utcNow(), totalMs);
            }
            else if (timedOut)
            {
                string msg = finalHits > 0
                    ? $"已达到单轮搜索时长上限，共找到 {finalHits} 个匹配种子"
                    : "已达到单轮搜索时长上限，暂未命中，可点击「继续搜」";
                job.MarkFinished("done", msg, _utcNow(), totalMs);
            }
            else
            {
                job.MarkFinished("done", "搜索结束", _utcNow(), totalMs);
            }
        }
        catch (OperationCanceledException)
        {
            job.MarkFinished("cancelled", "已停止", _utcNow());
        }
        catch (Exception ex)
        {
            job.MarkFinished("failed", $"搜索出错：{ex.Message}", _utcNow());
        }
        finally
        {
            if (acquired)
                _concurrencyGate.Release();
            ClearActiveAccountJobIfMatches(job.AccountId, job.JobId);
        }
    }

    /// <summary>
    /// 推演单个候选种子的 Day 1..maxDay 完整日历（H1~H5，含 H3 野怪）。
    /// 每个种子独立构造 RunState / ShopEngine / RunRandom，不接触任何全局可变状态。
    /// </summary>
    private static List<SeedCalendarDayDto> SimulateCalendar(
        int seed,
        EHero hero,
        int maxDay,
        CustomRunRuleSet simRules,
        ServerModHost.RunModContext modCtx)
    {
        var startProfile = BuildSimulationStartProfile(simRules, modCtx);
        var run = CreateSimulationRunState(seed, hero, startProfile, modCtx.Pool);
        var shop = CreateSimulationShopEngine(run, startProfile.EffectiveRules, modCtx);
        var days = new List<SeedCalendarDayDto>(maxDay);

        for (int day = 1; day <= maxDay; day++)
        {
            run.Day = (uint)day;
            var hourDtos = new List<SeedCalendarHourDto>(5);

            for (uint hour = 1; hour <= 5; hour++)
            {
                run.Hour = hour;
                shop.DealHourEncounters(null, run.VisitedDayEncounterIds, run.CustomRules, modCtx.ArchetypeIndex);

                var options = new List<SeedCalendarOptionDto>(run.SelectionSet.Count);
                foreach (string instId in run.SelectionSet)
                {
                    if (!run.Cards.TryGetValue(instId, out var entry))
                        continue;

                    string tplId = RunState.ResolveCoreTemplateId(entry);
                    var poolTpl = modCtx.Pool.Get(tplId);
                    var coreTpl = modCtx.Repository.GetCard(tplId);
                    string kind = ClassifyOptionKind(entry, poolTpl, coreTpl);
                    string nameZh = ResolveEncounterNameZh(modCtx.Repository, poolTpl, coreTpl, tplId);

                    options.Add(new SeedCalendarOptionDto
                    {
                        TemplateId = tplId,
                        NameZh = nameZh,
                        Kind = kind,
                    });
                }

                hourDtos.Add(new SeedCalendarHourDto
                {
                    Hour = (int)hour,
                    Options = options,
                });
            }

            days.Add(new SeedCalendarDayDto
            {
                Day = day,
                Hours = hourDtos,
            });

            if (day < maxDay)
                run.ApplyNewDayEncounterBookkeeping();
        }

        return days;
    }

    /// <summary>
    /// 推演单个候选种子并按天执行早退判定：
    /// - 所有条件均命中时立即返回匹配列表；
    /// - 任一未命中条件的 MaxDay &lt;= 当前刚结束的 day（即已过最晚天数仍未命中）立即 break 放弃该种子。
    /// </summary>
    private static List<SeedFinderMatchedEntry>? SimulateAndMatchSeed(
        int seed,
        EHero hero,
        int maxDay,
        NormalizedCondition[] conditions,
        SimulationStartProfile startProfile,
        ServerModHost.RunModContext modCtx)
    {
        var run = CreateSimulationRunState(seed, hero, startProfile, modCtx.Pool);
        var shop = CreateSimulationShopEngine(run, startProfile.EffectiveRules, modCtx);

        var matches = new SeedFinderMatchedEntry?[conditions.Length];
        int remaining = conditions.Length;

        for (int day = 1; day <= maxDay; day++)
        {
            run.Day = (uint)day;

            for (uint hour = 1; hour <= 5; hour++)
            {
                run.Hour = hour;
                shop.DealHourEncounters(null, run.VisitedDayEncounterIds, run.CustomRules, modCtx.ArchetypeIndex);

                for (int s = 0; s < run.SelectionSet.Count; s++)
                {
                    if (!run.Cards.TryGetValue(run.SelectionSet[s], out var entry))
                        continue;

                    string tplId = RunState.ResolveCoreTemplateId(entry);
                    for (int ci = 0; ci < conditions.Length; ci++)
                    {
                        if (matches[ci] != null)
                            continue;

                        var cond = conditions[ci];
                        if (day < cond.MinDay || day > cond.MaxDay)
                            continue;

                        if (string.Equals(tplId, cond.TemplateId, StringComparison.OrdinalIgnoreCase))
                        {
                            matches[ci] = new SeedFinderMatchedEntry
                            {
                                TemplateId = tplId,
                                NameZh = cond.NameZh,
                                Day = day,
                                Hour = (int)hour,
                            };
                            remaining--;
                        }
                    }
                }

                if (remaining == 0)
                {
                    var result = new List<SeedFinderMatchedEntry>(conditions.Length);
                    for (int ci = 0; ci < matches.Length; ci++)
                        result.Add(matches[ci]!);
                    return result;
                }
            }

            // 早退检查：本日已推演完毕，若任一尚未命中的条件最晚天数已到（MaxDay <= day），立即放弃该种子
            for (int ci = 0; ci < conditions.Length; ci++)
            {
                if (matches[ci] == null && conditions[ci].MaxDay <= day)
                    return null;
            }

            if (day < maxDay)
                run.ApplyNewDayEncounterBookkeeping();
        }

        return null;
    }

    private sealed class SimulationStartProfile
    {
        public required CustomRunRuleSet EffectiveRules { get; init; }
        public required int StartHp { get; init; }
        public required int StartGold { get; init; }
        public required int StartIncome { get; init; }
        public required int StartPrestige { get; init; }
        public required int StartExp { get; init; }
        public required bool FatesUsed { get; init; }
        public required ushort UnlockedSlots { get; init; }
    }

    /// <summary>
    /// 精确复用 RunEngine.StartRun / ApplyStartingStats / CollectGameModeRunSetup / ApplyCustomModeAndMutatorDeltas
    /// 计算无交互推演的开局属性、模式回退、自定义模式钩子与自定义突变词条开局增量。
    /// 预计算后可在批量搜种循环中复用到每个候选种子，零重复分配。
    /// </summary>
    private static SimulationStartProfile BuildSimulationStartProfile(
        CustomRunRuleSet simRules,
        ServerModHost.RunModContext modCtx)
    {
        var effectiveRules = simRules.CloneNormalized(modCtx.CustomMutatorsById, preserveUnregisteredMutators: false);
        effectiveRules.FixedRunSeed = null;
        effectiveRules.CustomSeedText = null;

        // 1. 解析并执行自定义模式钩子（IGameModeHook.OnRunSetup），确保 ForcedMutatorIds 等在开局前生效
        string? modeId = !string.IsNullOrWhiteSpace(effectiveRules.CustomModeId)
            ? effectiveRules.CustomModeId!.Trim()
            : (effectiveRules.SpecialModeId != ESpecialModeId.Standard ? effectiveRules.SpecialModeId.ToString() : null);

        GameModeRunSetupAdjustment? modeSetupAdj = null;
        if (!string.IsNullOrWhiteSpace(modeId) &&
            modCtx.TryGetGameModeHook(modeId, out var modeHook) &&
            modeHook != null)
        {
            var adj = new GameModeRunSetupAdjustment();
            try
            {
                modeHook.OnRunSetup(adj, effectiveRules);
            }
            catch
            {
                // 推演隔离模式钩子异常，保持安全回退
            }

            if (adj.ForcedMutatorIds.Count > 0)
            {
                var merged = new List<string>(effectiveRules.MutatorIds ?? new List<string>());
                foreach (var mid in adj.ForcedMutatorIds)
                {
                    if (!string.IsNullOrWhiteSpace(mid) && !merged.Contains(mid.Trim(), StringComparer.OrdinalIgnoreCase))
                        merged.Add(mid.Trim());
                }
                effectiveRules.MutatorIds = MutatorCatalog.NormalizeAndResolveConflicts(
                    merged,
                    maxCount: 4,
                    modCtx.CustomMutatorsById);
            }

            if (Math.Abs(adj.ShopPriceMultiplier - 1.0) > 1e-6)
                effectiveRules.ShopPriceMultiplier = Math.Clamp(effectiveRules.ShopPriceMultiplier * adj.ShopPriceMultiplier, 0.2, 5.0);
            if (adj.MaxDaysOverride is > 0)
                effectiveRules.EndlessMaxDay = Math.Clamp(adj.MaxDaysOverride.Value, 11, 99);

            modeSetupAdj = adj;
        }

        // 2. 与 RunEngine.ApplyStartingStats 一致的 GameModeConfig 回退
        var modeCfg = GameModeConfig.FromGameModeData(modCtx.Repository.GameMode);
        int startHp = Math.Max(1, effectiveRules.StartingHealth != GameDefaults.StartingHealth ? effectiveRules.StartingHealth : modeCfg.StartingHealth);
        int startGold = Math.Max(0, effectiveRules.StartingGold != GameDefaults.StartingGold ? effectiveRules.StartingGold : modeCfg.StartingGold);
        int startIncome = Math.Max(0, effectiveRules.StartingIncome != GameDefaults.StartingIncome ? effectiveRules.StartingIncome : modeCfg.StartingIncome);
        int startPrestige = Math.Max(1, effectiveRules.StartingPrestige != GameDefaults.StartingPrestige ? effectiveRules.StartingPrestige : modeCfg.PrestigeInitial);
        int startExp = Math.Max(0, effectiveRules.StartingExperience != GameDefaults.StartingExperience ? effectiveRules.StartingExperience : modeCfg.StartingExperience);

        // 3. 内置词条（DoubleEconomy / IronmanNoFates / GoldenStart）
        bool fatesUsed = effectiveRules.DisableFatesRevive || effectiveRules.HasMutator(MutatorCatalog.IronmanNoFates);
        if (effectiveRules.HasMutator(MutatorCatalog.DoubleEconomy))
        {
            startGold = Math.Min(9999, startGold * 2);
            startIncome = Math.Min(999, startIncome * 2);
        }
        if (effectiveRules.HasMutator(MutatorCatalog.IronmanNoFates))
        {
            startPrestige = Math.Min(startPrestige, 10);
            fatesUsed = true;
        }

        ushort unlockedSlots = (ushort)(effectiveRules.IsSandboxRun
            ? 0b11_1111_1111
            : (effectiveRules.HasMutator(MutatorCatalog.GoldenStart) ? 0b01_1111_1110 : GameDefaults.InitialUnlockedSlotsMask));

        // 4. 叠加自定义模式调节与隔离自定义突变词条的起始数值增量（对齐 RunEngine.ApplyCustomModeAndMutatorDeltas）
        if (modeSetupAdj != null)
        {
            startHp = Math.Clamp(startHp + modeSetupAdj.StartingHealthDelta, 50, 10000);
            startGold = Math.Clamp(startGold + modeSetupAdj.StartingGoldDelta, 0, 9999);
            startIncome = Math.Clamp(startIncome + modeSetupAdj.StartingIncomeDelta, 0, 999);
        }

        var mutSummary = modCtx.ResolveMutatorRuleSummary(effectiveRules);
        startGold = Math.Clamp(startGold + mutSummary.StartingGoldDelta, 0, 9999);
        startIncome = Math.Clamp(startIncome + mutSummary.StartingIncomeDelta, 0, 999);

        return new SimulationStartProfile
        {
            EffectiveRules = effectiveRules,
            StartHp = startHp,
            StartGold = startGold,
            StartIncome = startIncome,
            StartPrestige = startPrestige,
            StartExp = startExp,
            FatesUsed = fatesUsed,
            UnlockedSlots = unlockedSlots,
        };
    }

    private static RunState CreateSimulationRunState(
        int seed,
        EHero hero,
        SimulationStartProfile profile,
        CardPool pool)
    {
        var run = new RunState
        {
            RunId = "sim",
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            CustomRules = profile.EffectiveRules,
            RngSeed = seed,
            MasterSeed = seed,
            SeedText = DailyChallengeSeedGenerator.FormatSeedCode(seed),
            Day = 1,
            Hour = 1,
            FatesUsed = profile.FatesUsed,
            UnlockedSlots = profile.UnlockedSlots,
        };

        run.HandGrid.UnlockedMask = run.UnlockedSlots;
        run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, profile.StartHp);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Health, profile.StartHp);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Gold, profile.StartGold);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Income, profile.StartIncome);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Level, 1);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Experience, profile.StartExp);
        run.SetBasePlayerAttribute(EPlayerAttributeType.Prestige, profile.StartPrestige);

        return run;
    }

    private static PlayerState BuildSimulationQueryPlayer(RunState simRun, string hero, int day, int hour)
    {
        int hpMax = simRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, GameDefaults.StartingHealth);
        int prestige = simRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, GameDefaults.StartingPrestige);
        int gold = simRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, GameDefaults.StartingGold);
        var ps = new PlayerState("query", "query", hero, hpMax, prestige, gold, 10)
        {
            Day = day,
            Hour = hour,
            Level = simRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1),
            Wins = (int)simRun.Victories,
            Losses = (int)simRun.Defeats,
            FatesUsed = simRun.HasVisitedFates,
        };
        if (simRun.ExclusionSetInitialized)
        {
            ps.InitializeExclusionSet(null);
            foreach (string exId in simRun.ExclusionSet)
                ps.ExclusionSet.Add(exId);
        }
        ps.Hand.UnlockedMask = simRun.UnlockedSlots;
        ps.Stash.UnlockedMask = 1023;
        simRun.ApplyLayeredPlayerStateTo(ps);
        return ps;
    }

    private static ShopEngine CreateSimulationShopEngine(
        RunState run,
        CustomRunRuleSet simRules,
        ServerModHost.RunModContext modCtx)
    {
        return new ShopEngine(run, modCtx.Pool, modCtx.Repository, simRules, modCtx.ArchetypeIndex)
        {
            ModRuntimeContext = modCtx.RuntimeContext,
            RunModContext = modCtx,
            Rng = new RunRandom(run),
        };
    }

    // ── Mod 环境与规则解析 ────────────────────────────────────────────────────

    private ServerModHost.RunModContext ResolveModContext(IReadOnlyList<string>? selectedModIds)
    {
        var baseMonsters = _monsterPoolStore != null
            ? _monsterPoolStore.SnapshotList()
            : _fallbackMonsters!;
        var baseArchetypes = _runtimeSnapshots != null
            ? _runtimeSnapshots.Current.ArchetypeIndex
            : _fallbackArchetypes!;

        if (_modHost != null)
            return _modHost.ResolveRunModContext(selectedModIds, _basePool, baseMonsters, baseArchetypes);

        return new ServerModHost.RunModContext(
            ContextKey: "direct",
            Repository: _fallbackRepo!,
            Pool: _basePool,
            Monsters: baseMonsters,
            ArchetypeIndex: baseArchetypes,
            VisualBindingsByCustomId: FrozenDictionary<string, CustomCardVisualBinding>.Empty,
            ActiveModIds: Array.Empty<string>(),
            IsolatedRegistry: null,
            RuntimeContext: null,
            EffectiveModSetHash: string.Empty);
    }

    private (ServerModHost.RunModContext ModCtx, CustomRunRuleSet SimRules, bool UsedPlayerRules) ResolveSimulationEnvironment(
        bool useMyRules,
        IReadOnlyList<string>? requestModIds,
        CustomRunRuleSet? playerRules)
    {
        if (useMyRules && playerRules != null)
        {
            var modCtx = ResolveModContext(playerRules.SelectedModIds);
            var normalized = playerRules.CloneNormalized(modCtx.CustomMutatorsById, preserveUnregisteredMutators: false);
            // 推演种子由候选 seed 直接驱动（RunState.MasterSeed），清除旧固定种子字段避免干扰
            normalized.FixedRunSeed = null;
            normalized.CustomSeedText = null;
            return (modCtx, normalized, true);
        }

        var stdCtx = ResolveModContext(requestModIds);
        var stdRules = BuildStandardSimulationRules(requestModIds, stdCtx);
        return (stdCtx, stdRules, false);
    }

    private static CustomRunRuleSet BuildStandardSimulationRules(
        IReadOnlyList<string>? selectedModIds,
        ServerModHost.RunModContext modCtx)
    {
        var baseRules = new CustomRunRuleSet
        {
            SelectedModIds = selectedModIds != null
                ? selectedModIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string>(),
        };
        return baseRules.CloneNormalized(modCtx.CustomMutatorsById, preserveUnregisteredMutators: false);
    }

    // ── 权威事件目录与天数窗推导（与 ShopEngine.DealHourEncounters 同构） ────

    private static Dictionary<string, EligibleEventMeta> BuildEligibleCatalogMap(
        string hero,
        CustomRunRuleSet rules,
        ServerModHost.RunModContext modCtx)
    {
        var pool = modCtx.Pool;
        var repo = modCtx.Repository;
        var result = new Dictionary<string, EligibleEventMeta>(StringComparer.OrdinalIgnoreCase);

        var heroEnum = Enum.TryParse<EHero>(hero, ignoreCase: true, out var parsedHero)
            ? parsedHero
            : EHero.Vanessa;
        var startProfile = BuildSimulationStartProfile(rules, modCtx);
        var effRules = startProfile.EffectiveRules;

        var exclusionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (repo.GameMode?.ExclusionSet != null)
        {
            foreach (string exId in repo.GameMode.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(exId))
                    exclusionSet.Add(exId);
            }
        }

        // 构造只读模拟态，供 ShopEngine.CheckEncounterRequirements 复验运行期前置条件（SelectionRequirements / TPrerequisiteRun）
        var simRun = CreateSimulationRunState(seed: 1, heroEnum, startProfile, pool);
        foreach (string exId in exclusionSet)
            simRun.ExclusionSet.Add(exId);
        simRun.ExclusionSetInitialized = true;
        var simShop = CreateSimulationShopEngine(simRun, effRules, modCtx);
        var checkRng = new Random(1);

        HashSet<string>? bannedIds = effRules.BannedEncounterIds is { Count: > 0 }
            ? new HashSet<string>(effRules.BannedEncounterIds, StringComparer.OrdinalIgnoreCase)
            : null;
        bool IsBanned(string id) => bannedIds != null && bannedIds.Contains(id);

        double travelAgentMult = GetCategoryMultiplier(EEncounterWeightCategory.EventExpeditionAgent, effRules);
        bool allowAmplifiedTravelAgent = travelAgentMult > 1.0;
        double cultMysteryMult = GetCategoryMultiplier(EEncounterWeightCategory.EventCultAndMystery, effRules);

        void Register(CardPool.CardTemplate tpl, string kind, int day)
        {
            if (tpl == null || string.IsNullOrWhiteSpace(tpl.Id))
                return;
            if (!result.TryGetValue(tpl.Id, out var meta))
            {
                var coreTpl = repo.GetCard(tpl.Id);
                bool isMod = tpl.IsCustomModCard || (coreTpl?.IsCustomModCard == true);
                string? modId = isMod ? (tpl.ModId ?? coreTpl?.CustomModOrigin) : null;
                meta = new EligibleEventMeta
                {
                    TemplateId = tpl.Id,
                    NameZh = ResolveEncounterNameZh(repo, tpl, coreTpl, tpl.Id),
                    Kind = kind,
                    IsMod = isMod,
                    ModId = string.IsNullOrWhiteSpace(modId) ? null : modId,
                };
                result[tpl.Id] = meta;
            }
            meta.EligibleDays.Add(day);
        }

        var rawHourlyEvents = pool.HourlyEncounters(hero, "EventEncounter");
        if (rawHourlyEvents.Count == 0)
            rawHourlyEvents = pool.Encounters(hero, "EventEncounter");
        var guidCatalog = pool.GetGuidOnlyHourlyCatalog();
        var heroTakeovers = pool.GetHeroTakeoverEncounters(hero);
        var rawPedestals = pool.HourlyEncounters(hero, "PedestalEncounter");

        // BossRush 模式下按 MonsterTemplateId 预建合法战斗遭遇索引
        Dictionary<string, CardPool.CardTemplate>? bossRushCombatByMonsterId = null;
        if (effRules.SpecialModeId == ESpecialModeId.BossRush)
        {
            bossRushCombatByMonsterId = new Dictionary<string, CardPool.CardTemplate>(StringComparer.OrdinalIgnoreCase);
            foreach (var ce in pool.OfType("CombatEncounter"))
            {
                if (string.IsNullOrWhiteSpace(ce.MonsterTemplateId)) continue;
                if (!MonsterPool.IsPlayableCombatEncounter(ce, pool, repo, requireAlwaysSpawning: false)) continue;
                if (IsBanned(ce.Id)) continue;
                if (!bossRushCombatByMonsterId.ContainsKey(ce.MonsterTemplateId))
                    bossRushCombatByMonsterId[ce.MonsterTemplateId] = ce;
            }
        }

        List<CardPool.CardTemplate>? playableFallbackMonsters = null;
        List<CardPool.CardTemplate> GetPlayableFallbackMonsters()
        {
            if (playableFallbackMonsters != null)
                return playableFallbackMonsters;
            playableFallbackMonsters = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds);
            if (playableFallbackMonsters.Count == 0 && bannedIds is { Count: > 0 })
                playableFallbackMonsters = MonsterPool.CollectPlayableCombatEncounters(pool, repo, bannedIds: null);
            return playableFallbackMonsters;
        }

        for (int day = 1; day <= MaxSimulationDay; day++)
        {
            ETier maxDayTier = GetMaxEncounterTierForDay(day);
            ETier maxMerchantTier = GetMaxMerchantTierForDay(day);
            PlayerState?[] dayQueryPlayers = new PlayerState?[DaytimeHours.Length];

            bool PassesDaytimeWindowAndPrerequisites(CardPool.CardTemplate tpl, BazaarCore.Models.CardTemplate? coreTpl)
            {
                bool hasPrereq = ShopEngine.HasEncounterRuntimePrerequisites(tpl, coreTpl);
                CardInstance? selfCard = null;
                for (int hi = 0; hi < DaytimeHours.Length; hi++)
                {
                    int h = DaytimeHours[hi];
                    if (!ShopEngine.IsWithinOfficialDayWindow(tpl, day, h, hero, allowAmplifiedTravelAgent))
                        continue;
                    if (!hasPrereq)
                        return true;

                    simRun.Day = (uint)day;
                    simRun.Hour = (uint)h;
                    var qp = dayQueryPlayers[hi] ??= BuildSimulationQueryPlayer(simRun, hero, day, h);
                    selfCard ??= coreTpl != null ? new CardInstance(coreTpl) : null;
                    var checkCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, checkRng)
                    {
                        CurrentDay = day,
                        CurrentHour = h,
                        CurrentEncounterId = tpl.Id,
                        PlayMode = simRun.PlayMode.ToString(),
                        SelfCard = selfCard,
                    };
                    if (simShop.CheckEncounterRequirements(tpl, coreTpl, checkCtx))
                        return true;
                }
                return false;
            }

            // 1. Hour 3 PvE 野怪（严格对齐 OfficialDaySchedule / BossRushCatalog）
            if (effRules.SpecialModeId == ESpecialModeId.BossRush && bossRushCombatByMonsterId != null)
            {
                int matchedWaveCount = 0;
                foreach (var bm in BossRushCatalog.ResolveWaveMonsters(day, repo))
                {
                    if (bossRushCombatByMonsterId.TryGetValue(bm.Id, out var ce))
                    {
                        Register(ce, "monster", day);
                        matchedWaveCount++;
                    }
                }
                if (matchedWaveCount < 3)
                {
                    RegisterStandardDayMonsters(Math.Max(day, 8), day, pool, repo, bannedIds, GetPlayableFallbackMonsters, Register);
                }
            }
            else
            {
                RegisterStandardDayMonsters(day, day, pool, repo, bannedIds, GetPlayableFallbackMonsters, Register);
            }

            // 2. 白昼常规遭遇（Hours 1, 2, 4, 5）
            void TryRegisterDaytimeEvent(CardPool.CardTemplate? tpl, bool tierExemptFromSource)
            {
                if (tpl == null || string.IsNullOrWhiteSpace(tpl.Id)) return;
                if (exclusionSet.Contains(tpl.Id) || IsBanned(tpl.Id)) return;
                if (CardPool.IsExcludedDevOrStoryTemplate(tpl)) return;
                if (string.Equals(tpl.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase)) return;
                if (!MatchesHeroOrCommon(tpl, hero)) return;
                if (GetEncounterMultiplier(tpl, effRules, modCtx) <= 0.0) return;

                var coreTpl = repo.GetCard(tpl.Id);
                if (!PassesDaytimeWindowAndPrerequisites(tpl, coreTpl)) return;

                bool isMerch = CardArchetypeIndex.IsMerchantEncounterTemplate(
                    tpl.Id,
                    tpl.InternalName ?? coreTpl?.InternalName ?? string.Empty,
                    tpl,
                    coreTpl);
                var slot = OfficialEncounterSchedule.ClassifySlot(tpl, isMerch);
                if (slot != EOfficialEncounterSlot.LeftMerchant && !HasPlayableEncounterPayload(tpl, coreTpl))
                    return;

                bool tierExempt = tierExemptFromSource
                    || (OfficialEncounterSchedule.TryGetRule(tpl, out var sr) && sr != null && sr.ExemptFromMaxDayTier);
                if (!tierExempt)
                {
                    ETier cap = slot == EOfficialEncounterSlot.LeftMerchant ? maxMerchantTier : maxDayTier;
                    if (ShopEngine.ParseTier(tpl.StartingTier) > cap)
                        return;
                }

                string kind = (isMerch || slot == EOfficialEncounterSlot.LeftMerchant) ? "merchant" : "event";
                Register(tpl, kind, day);
            }

            void TryRegisterGuidOnlyById(string templateId, bool tierExemptFromSource)
            {
                var tpl = pool.GetIfAssetValid(templateId);
                if (tpl == null || !pool.IsPlayerSpawnable(tpl, allowGuidOnly: true))
                    return;
                TryRegisterDaytimeEvent(tpl, tierExemptFromSource);
            }

            // 2a. Always 白昼池
            for (int i = 0; i < rawHourlyEvents.Count; i++)
                TryRegisterDaytimeEvent(rawHourlyEvents[i], tierExemptFromSource: false);

            // 2b. GuidOnly 白昼遭遇目录（37 套四阶右槽免费物品 + 分天事件 + 独立特殊事件）
            foreach (var (guidCard, _, guidTierExempt) in guidCatalog.ResolveForHeroAndDay(hero, day))
                TryRegisterDaytimeEvent(guidCard, tierExemptFromSource: guidTierExempt);

            // 2c. 右槽 3 套分天经济卡
            foreach (string econId in OfficialEncounterSchedule.GetRightSlotEconomyIdsForDay(day))
                TryRegisterGuidOnlyById(econId, tierExemptFromSource: true);

            // 2d. Grand Opening (Days 3..9)
            if (day is >= 3 and <= 9)
                TryRegisterGuidOnlyById(CardPool.GrandOpeningEncounterId, tierExemptFromSource: true);

            // 2e. Mysterious Portal & The Cult (Day 6+)
            if (day >= 6 && cultMysteryMult > 0.0)
            {
                TryRegisterGuidOnlyById(CardArchetypeIndex.MysteriousPortalEncounterId, tierExemptFromSource: false);
                TryRegisterGuidOnlyById(CardArchetypeIndex.TheCultEncounterId, tierExemptFromSource: true);
            }

            // 2f. The Travel Agent (标准 Days 2..5；放大时 Days 2..9；Day 10 放大且满阶)
            if (travelAgentMult > 0.0 && ((day is >= 2 and <= 5) || (allowAmplifiedTravelAgent && day is >= 2 and <= 9)))
            {
                TryRegisterGuidOnlyById(CardPool.TravelAgentEncounterId, tierExemptFromSource: true);
            }
            else if (day >= 10 && travelAgentMult > 1.0)
            {
                TryRegisterGuidOnlyById(CardPool.TravelAgentEncounterId, tierExemptFromSource: false);
            }

            // 2g. 8 英雄专属 IsTakeover 单次奇遇
            for (int i = 0; i < heroTakeovers.Count; i++)
            {
                var tk = heroTakeovers[i];
                int minUnlock = ShopEngine.ParseTier(tk.StartingTier) <= ETier.Silver ? 3 : 6;
                if (day >= minUnlock)
                    TryRegisterDaytimeEvent(tk, tierExemptFromSource: true);
            }

            // 3. 常规白昼地图基座（PedestalEncounter）：先汇总当日候选集合再整体计算动态基座触发率
            bool guaranteedPedestalHour =
                effRules.SpecialModeId == ESpecialModeId.BossRush ||
                (effRules.SpecialModeId == ESpecialModeId.EndlessTower &&
                 EndlessTowerScaler.Compute(day, effRules.EndlessMaxDay).IsMilestoneDay);
            bool skipRegularPedestal = !guaranteedPedestalHour && allowAmplifiedTravelAgent && travelAgentMult >= 4.0;

            if (guaranteedPedestalHour || (day >= 2 && !skipRegularPedestal))
            {
                var dayPedestals = new List<CardPool.CardTemplate>();
                for (int i = 0; i < rawPedestals.Count; i++)
                {
                    var ped = rawPedestals[i];
                    if (exclusionSet.Contains(ped.Id) || IsBanned(ped.Id)) continue;
                    if (CardPool.IsExcludedDevOrStoryTemplate(ped)) continue;
                    if (GetEncounterMultiplier(ped, effRules, modCtx) <= 0.0) continue;

                    if (!guaranteedPedestalHour)
                    {
                        var corePed = repo.GetCard(ped.Id);
                        if (!PassesDaytimeWindowAndPrerequisites(ped, corePed))
                            continue;
                    }

                    dayPedestals.Add(ped);
                }

                if (dayPedestals.Count > 0)
                {
                    bool canTrigger = guaranteedPedestalHour
                        || OfficialEncounterSchedule.ComputeDynamicMiddlePedestalHourChance(dayPedestals, day) > 0.0;
                    if (canTrigger)
                    {
                        for (int i = 0; i < dayPedestals.Count; i++)
                            Register(dayPedestals[i], "pedestal", day);
                    }
                }
            }
        }

        return result;
    }

    private static void RegisterStandardDayMonsters(
        int scheduleDay,
        int targetDay,
        CardPool pool,
        GameDataRepository repo,
        ISet<string>? bannedIds,
        Func<List<CardPool.CardTemplate>> getPlayableFallback,
        Action<CardPool.CardTemplate, string, int> register)
    {
        var roster = OfficialDaySchedule.GetRosterForDay(scheduleDay);
        IReadOnlyList<string>[] slotLists = [roster.LeftIds, roster.MidIds, roster.RightIds];
        bool anySlotEmpty = false;

        for (int slotIdx = 0; slotIdx < 3; slotIdx++)
        {
            int slotValidCount = 0;
            var curatedIds = slotLists[slotIdx];
            for (int i = 0; i < curatedIds.Count; i++)
            {
                string id = curatedIds[i];
                if (bannedIds != null && bannedIds.Contains(id)) continue;
                var tpl = pool.Get(id);
                if (!MonsterPool.IsPlayableCombatEncounter(tpl, pool, repo, requireAlwaysSpawning: true))
                    continue;
                register(tpl!, "monster", targetDay);
                slotValidCount++;
            }

            var customSlot = OfficialDaySchedule.GetEligibleCustomMonstersForSlot(scheduleDay, slotIdx, pool, repo, bannedIds);
            for (int i = 0; i < customSlot.Count; i++)
            {
                register(customSlot[i], "monster", targetDay);
                slotValidCount++;
            }

            if (slotValidCount == 0)
                anySlotEmpty = true;
        }

        if (anySlotEmpty)
        {
            foreach (var fb in getPlayableFallback())
                register(fb, "monster", targetDay);
        }
    }

    /// <summary>针对不在当期可出现目录内的模板，给出精准简体中文不可行原因。</summary>
    private static string DiagnoseIneligibleTemplateReason(
        string templateId,
        string hero,
        int jobMaxDay,
        CustomRunRuleSet rules,
        ServerModHost.RunModContext modCtx)
    {
        var pool = modCtx.Pool;
        var repo = modCtx.Repository;
        var tpl = pool.Get(templateId);
        var coreTpl = repo.GetCard(templateId);

        if (tpl == null && coreTpl == null)
            return "当前 Mod 环境下不存在该事件模板（可能未开启对应 Mod）";

        if (rules.BannedEncounterIds != null &&
            rules.BannedEncounterIds.Any(id => string.Equals(id, templateId, StringComparison.OrdinalIgnoreCase)))
        {
            return "该事件已被当前规则禁用";
        }

        if (tpl != null && GetEncounterMultiplier(tpl, rules, modCtx) <= 0.0)
            return "该事件在当前规则下的出现权重为 0";

        if (repo.GameMode?.ExclusionSet != null &&
            repo.GameMode.ExclusionSet.Any(id => string.Equals(id, templateId, StringComparison.OrdinalIgnoreCase)))
        {
            return "该事件处于初始排除池，仅能由局内事件链动态解锁，无法在纯事件推演中直接出现";
        }

        string? type = tpl?.Type ?? coreTpl?.Type;
        if (string.Equals(type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
        {
            int? modMin = coreTpl?.ModMinDay ?? tpl?.ModMinDay;
            int? modMax = coreTpl?.ModMaxDay ?? tpl?.ModMaxDay;
            if (modMin.HasValue && modMin.Value > jobMaxDay)
                return $"该野怪最早在第 {modMin.Value} 天出现，超出搜种上限（第 {jobMaxDay} 天）";
            if (modMax.HasValue && modMax.Value < 1)
                return "该野怪的天数配置无效";

            bool inLateRoster =
                OfficialDaySchedule.GetRosterForDay(11).AllIds().Any(id => string.Equals(id, templateId, StringComparison.OrdinalIgnoreCase)) ||
                OfficialDaySchedule.LateGameRoster.AllIds().Any(id => string.Equals(id, templateId, StringComparison.OrdinalIgnoreCase));
            if (inLateRoster)
                return "该野怪仅在第 11 天及以后出现，超出搜种范围（第 1~10 天）";

            return "该野怪不在常规第 1~10 天 Hour 3 野怪池中";
        }

        if (tpl != null && !MatchesHeroOrCommon(tpl, hero))
        {
            return $"该事件不会在英雄「{PlannerCatalogConstants.GetHeroNameZh(hero)}」的对局中出现";
        }

        if (tpl != null)
        {
            if (tpl.ModMinDay.HasValue && tpl.ModMinDay.Value > jobMaxDay)
                return $"该事件最早在第 {tpl.ModMinDay.Value} 天出现，超出搜种上限（第 {jobMaxDay} 天）";

            if (OfficialEncounterSchedule.TryGetRule(tpl, out var rule) && rule != null)
            {
                int effMin = rule.MinDay;
                if (rule.HeroMinDayOverrides != null && rule.HeroMinDayOverrides.TryGetValue(hero, out int hMin))
                    effMin = hMin;
                if (effMin > jobMaxDay)
                    return $"该事件最早在第 {effMin} 天出现，超出搜种上限（第 {jobMaxDay} 天）";
            }

            if (CardPool.IsNonHourlySystemEncounter(tpl) || CardPool.IsExcludedDevOrStoryTemplate(tpl))
                return "该事件为开局/升级/远征或系统专属事件，不会在常规每小时选项中出现";

            // 若模板声明了运行期前置条件（SelectionRequirements / TPrerequisiteRun），
            // 在无交互初始态下全天均无法通过时给出精准中文说明
            if (ShopEngine.HasEncounterRuntimePrerequisites(tpl, coreTpl))
            {
                var heroEnum = Enum.TryParse<EHero>(hero, ignoreCase: true, out var parsedHero)
                    ? parsedHero
                    : EHero.Vanessa;
                var startProfile = BuildSimulationStartProfile(rules, modCtx);
                var simRun = CreateSimulationRunState(seed: 1, heroEnum, startProfile, pool);
                if (repo.GameMode?.ExclusionSet != null)
                {
                    foreach (string exId in repo.GameMode.ExclusionSet)
                    {
                        if (!string.IsNullOrWhiteSpace(exId))
                            simRun.ExclusionSet.Add(exId);
                    }
                }
                simRun.ExclusionSetInitialized = true;
                var simShop = CreateSimulationShopEngine(simRun, startProfile.EffectiveRules, modCtx);
                double travelMult = GetCategoryMultiplier(EEncounterWeightCategory.EventExpeditionAgent, startProfile.EffectiveRules);
                bool allowAmpAgent = travelMult > 1.0;
                var checkRng = new Random(1);
                var selfCard = coreTpl != null ? new CardInstance(coreTpl) : null;

                bool anyWindowPassed = false;
                bool anyPrereqPassed = false;
                for (int d = 1; d <= MaxSimulationDay && !anyPrereqPassed; d++)
                {
                    for (int hi = 0; hi < DaytimeHours.Length; hi++)
                    {
                        int h = DaytimeHours[hi];
                        if (!ShopEngine.IsWithinOfficialDayWindow(tpl, d, h, hero, allowAmpAgent))
                            continue;
                        anyWindowPassed = true;
                        simRun.Day = (uint)d;
                        simRun.Hour = (uint)h;
                        var qp = BuildSimulationQueryPlayer(simRun, hero, d, h);
                        var checkCtx = new RuleExecutionContext(repo, RuleScope.OutOfCombat, qp, qp, checkRng)
                        {
                            CurrentDay = d,
                            CurrentHour = h,
                            CurrentEncounterId = tpl.Id,
                            PlayMode = simRun.PlayMode.ToString(),
                            SelfCard = selfCard,
                        };
                        if (simShop.CheckEncounterRequirements(tpl, coreTpl, checkCtx))
                        {
                            anyPrereqPassed = true;
                            break;
                        }
                    }
                }

                if (anyWindowPassed && !anyPrereqPassed)
                    return "该事件需要局内特定前置条件，在无交互推演中无法出现";
            }
        }

        return "该事件在当前英雄、规则与 Mod 环境下不会在第 1~10 天常规小时中出现";
    }

    // ── 辅助方法 ─────────────────────────────────────────────────────────────

    private void ClearActiveAccountJobIfMatches(string accountId, string jobId)
    {
        lock (_accountActiveLock)
        {
            if (_activeJobIdByAccount.TryGetValue(accountId, out string? currentId) &&
                string.Equals(currentId, jobId, StringComparison.Ordinal))
            {
                _activeJobIdByAccount.Remove(accountId);
            }
        }
    }

    private static int NextNonZeroSeed(int current)
    {
        int next = unchecked(current + 1);
        return next == 0 ? 1 : next;
    }

    private static bool MatchesHeroOrCommon(CardPool.CardTemplate tpl, string hero)
    {
        if (tpl.Heroes == null || tpl.Heroes.Length == 0)
            return true;
        for (int i = 0; i < tpl.Heroes.Length; i++)
        {
            string h = tpl.Heroes[i];
            if (string.Equals(h, hero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(h, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasPlayableEncounterPayload(
        CardPool.CardTemplate tpl,
        BazaarCore.Models.CardTemplate? core)
    {
        if (tpl.SelectionContext?.SpawnContext != null) return true;
        if (tpl.NextEncounterOnSelection is { Count: > 0 }) return true;
        if (tpl.ExperienceAwardUponSelection > 0) return true;
        if (core == null) return false;
        if (core.SelectionContext?.SpawnContext != null) return true;
        if (core.NextEncounterOnSelection is { Count: > 0 }) return true;
        if ((core.ExperienceAwardUponSelection ?? 0) > 0) return true;
        return core.Abilities.Values.Any(a =>
            a.Trigger != null &&
            string.Equals(a.Trigger.Type, "TTriggerOnCardSelected", StringComparison.OrdinalIgnoreCase));
    }

    private static double GetCategoryMultiplier(EEncounterWeightCategory category, CustomRunRuleSet rules)
    {
        double mult = 1.0;
        if (rules.EncounterCategoryMultipliers is { Count: > 0 })
        {
            string key = EncounterWeightCategoryCatalog.ByCategory.TryGetValue(category, out var meta)
                ? meta.Key
                : category.ToString();
            if (rules.EncounterCategoryMultipliers.TryGetValue(key, out double m) && double.IsFinite(m))
                mult = Math.Max(0.0, m);
        }

        if (rules.HasMutator(MutatorCatalog.EventCarnival) && category is
            EEncounterWeightCategory.PedestalEnchant or EEncounterWeightCategory.PedestalEnchantRandom
            or EEncounterWeightCategory.PedestalUpgrade or EEncounterWeightCategory.EventCultAndMystery
            or EEncounterWeightCategory.EventExpeditionAgent or EEncounterWeightCategory.EventEconomyStats
            or EEncounterWeightCategory.EventFreeDealTransform)
        {
            mult *= 2.5;
        }

        return mult;
    }

    private static double GetEncounterMultiplier(
        CardPool.CardTemplate tpl,
        CustomRunRuleSet rules,
        ServerModHost.RunModContext modCtx)
    {
        bool hasMultipliers = (rules.EncounterCategoryMultipliers is { Count: > 0 })
            || rules.HasMutator(MutatorCatalog.EventCarnival);
        double mult = 1.0;
        if (hasMultipliers)
        {
            var cat = modCtx.ArchetypeIndex != null && modCtx.ArchetypeIndex.TryGetEncounterCategory(tpl.Id, out var c)
                ? c
                : CardArchetypeIndex.ClassifyEncounter(tpl, modCtx.Repository.GetCard(tpl.Id));
            mult = GetCategoryMultiplier(cat, rules);
        }

        if (tpl.ModSpawnWeight.HasValue && double.IsFinite(tpl.ModSpawnWeight.Value))
            mult *= Math.Max(0.0, tpl.ModSpawnWeight.Value);

        return mult;
    }

    private static ETier GetMaxEncounterTierForDay(int day) => day switch
    {
        <= 3 => ETier.Silver,
        <= 6 => ETier.Gold,
        _ => ETier.Legendary,
    };

    private static ETier GetMaxMerchantTierForDay(int day) => day switch
    {
        <= 5 => ETier.Silver,
        _ => ETier.Legendary,
    };

    private static string ClassifyOptionKind(
        RunState.CardEntry entry,
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl)
    {
        if (entry.Type == ECardType.CombatEncounter ||
            string.Equals(poolTpl?.Type ?? coreTpl?.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
        {
            return "monster";
        }

        if (entry.Type == ECardType.PedestalEncounter ||
            string.Equals(poolTpl?.Type ?? coreTpl?.Type, "PedestalEncounter", StringComparison.OrdinalIgnoreCase))
        {
            return "pedestal";
        }

        string id = poolTpl?.Id ?? coreTpl?.Id ?? RunState.ResolveCoreTemplateId(entry);
        string name = poolTpl?.InternalName ?? coreTpl?.InternalName ?? string.Empty;
        bool isMerch = CardArchetypeIndex.IsMerchantEncounterTemplate(id, name, poolTpl, coreTpl);
        if (isMerch || (poolTpl != null && OfficialEncounterSchedule.ClassifySlot(poolTpl, isMerch) == EOfficialEncounterSlot.LeftMerchant))
            return "merchant";

        return "event";
    }

    private static string ResolveEncounterNameZh(
        GameDataRepository repo,
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        string fallbackId)
    {
        if (coreTpl != null)
        {
            string? title = repo.Localization?.GetLocalizedTitle(coreTpl);
            if (!string.IsNullOrWhiteSpace(title))
                return title.Trim();
            title = repo.Localization?.GetLocalizedText(coreTpl.Localization?.Title);
            if (!string.IsNullOrWhiteSpace(title))
                return title.Trim();
            if (!string.IsNullOrWhiteSpace(coreTpl.DisplayTitle))
                return coreTpl.DisplayTitle.Trim();
            if (!string.IsNullOrWhiteSpace(coreTpl.InternalName))
                return coreTpl.InternalName.Trim();
        }

        if (poolTpl != null && !string.IsNullOrWhiteSpace(poolTpl.InternalName))
            return poolTpl.InternalName.Trim();

        return fallbackId;
    }

    private static int KindSortOrder(string kind) => kind switch
    {
        "event" => 0,
        "merchant" => 1,
        "pedestal" => 2,
        "monster" => 3,
        _ => 4,
    };

    private static string FormatDaySetZh(SortedSet<int> days)
    {
        if (days.Count == 0) return "";
        if (days.Count == 1) return days.Min.ToString();

        bool contiguous = true;
        int prev = -1;
        foreach (int d in days)
        {
            if (prev >= 0 && d != prev + 1)
            {
                contiguous = false;
                break;
            }
            prev = d;
        }

        return contiguous
            ? $"{days.Min}~{days.Max}"
            : string.Join("、", days);
    }
}

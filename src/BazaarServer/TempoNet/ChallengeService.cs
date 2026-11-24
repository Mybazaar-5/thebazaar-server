using System.Collections.Concurrent;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Storage;

namespace BazaarServer.TempoNet;

/// <summary>
/// 对应官方客户端 BazaarGameShared.TempoNet.Responses.ChallengeProgress 协议项（字段名逐字对齐）。
/// </summary>
public sealed class ChallengeProgressItemDto
{
    public Guid Id { get; set; }
    public int Progress { get; set; }
    public bool Acknowledged { get; set; }
}

/// <summary>
/// 供局内/战后事件推进挑战进度时传入的可选结构化上下文（支持 50 条官方活跃挑战全部 23 种触发器与嵌套条件过滤）。
/// </summary>
public sealed class ChallengeProgressContext
{
    public string? CardType { get; init; }
    public string? CardSize { get; init; }
    public string? CardTier { get; init; }
    public IReadOnlyCollection<string>? Tags { get; init; }
    public IReadOnlyCollection<string>? HiddenTags { get; init; }
    public IReadOnlyDictionary<string, double>? CardAttributes { get; init; }
    public bool? HasEnchantment { get; init; }
    public string? Hero { get; init; }
    public string? CardTemplateId { get; init; }

    public string? AttributeType { get; init; }
    public string? ChangeType { get; init; }
    public string? CombatType { get; init; }
    public string? CombatOutcome { get; init; }
    public string? RunOutcome { get; init; }
    public int? NewLevel { get; init; }
    public double? Amount { get; init; }
    public bool IsFusion { get; init; }

    /// <summary>从权威卡牌模板快速构造挑战匹配上下文（供 RunEngine / ShopEngine 接入）。</summary>
    public static ChallengeProgressContext FromCard(
        CardTemplate card,
        CardTier? tierOverride = null,
        int? buyPriceOverride = null,
        int? multicastOverride = null,
        bool hasEnchantment = false,
        bool isFusion = false)
    {
        ArgumentNullException.ThrowIfNull(card);
        string resolvedTier = (tierOverride ?? card.DefaultTier).ToString();
        var attrs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (card.Tiers != null && card.Tiers.TryGetValue(resolvedTier, out var tierData) && tierData?.Attributes != null)
        {
            foreach (var kv in tierData.Attributes)
                attrs[kv.Key] = kv.Value;
        }
        if (buyPriceOverride.HasValue)
            attrs["BuyPrice"] = buyPriceOverride.Value;
        if (multicastOverride.HasValue)
            attrs["Multicast"] = multicastOverride.Value;

        return new ChallengeProgressContext
        {
            CardTemplateId = card.Id,
            CardType = card.Type,
            CardSize = card.Size,
            CardTier = resolvedTier,
            Tags = card.Tags,
            HiddenTags = card.HiddenTags,
            CardAttributes = attrs,
            HasEnchantment = hasEnchantment,
            Hero = card.Heroes.FirstOrDefault(),
            IsFusion = isFusion,
        };
    }
}

/// <summary>挑战进度推进结果（供服务端调用方与 TempoNet 状态增量同步）。</summary>
public sealed record ChallengeProgressUpdateResult(
    IReadOnlyList<Guid> UpdatedChallengeIds,
    IReadOnlyList<Guid> NewlyCompletedChallengeIds,
    ChallengesDto Snapshot);

/// <summary>挑战领取确认结果（包含实际发放的通行证 XP 与最新赛季总经验）。</summary>
public sealed record ChallengeAcknowledgeResult(
    bool Ok,
    int XpGranted,
    int NewSeasonExperience,
    ChallengesDto Snapshot,
    string? Error);

/// <summary>挑战重随结果。</summary>
public sealed record ChallengeRerollResult(
    bool Ok,
    Guid? ReplacedId,
    Guid? NewChallengeId,
    ChallengesDto Snapshot,
    string? Error);

/// <summary>
/// 每日/每周挑战持久化与规则推进服务（P1-02）：
/// - 驱动自 GameDataRepository.ChallengesById（80 条官方挑战，其中 33 Daily / 17 Weekly 为 IsActive=true）；
/// - 按账号分配并持久化每日 3 条 / 每周 3 条活跃挑战，跨自然日/自然周自动轮换刷新；
/// - 支持每日/每周重随（reroll）、局内/战后触发器规则匹配推进（RecordChallengeProgress）及完成确认发放通行证 XP。
/// </summary>
public sealed class ChallengeService
{
    public const int DailySlotCount = 3;
    public const int WeeklySlotCount = 3;
    public const int DefaultDailyResets = 1;
    public const int DefaultWeeklyResets = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private sealed class PersistedChallengeSlot
    {
        public Guid Id { get; set; }
        public int Progress { get; set; }
        public bool Acknowledged { get; set; }
        public bool RewardGranted { get; set; }
    }

    private sealed class PersistedAccountChallengeState
    {
        public string DailyPeriodKey { get; set; } = string.Empty;
        public DateTime DailyExpiresAtUtc { get; set; }
        public int DailyResetsRemaining { get; set; } = DefaultDailyResets;
        public List<PersistedChallengeSlot> DailyChallenges { get; set; } = new();

        public string WeeklyPeriodKey { get; set; } = string.Empty;
        public DateTime WeeklyExpiresAtUtc { get; set; }
        public int WeeklyResetsRemaining { get; set; } = DefaultWeeklyResets;
        public List<PersistedChallengeSlot> WeeklyChallenges { get; set; } = new();
    }

    private const int AccountLockStripeCount = 64;
    private const int MaxCachedAccounts = 4096;
    private const long CacheSlidingTtlMs = 2L * 60 * 60 * 1000;

    private sealed class CachedChallengeEntry
    {
        public required PersistedAccountChallengeState State { get; init; }
        public long LastAccessTickMs;
    }

    private readonly Persistence _db;
    private readonly GameDataRepository _repo;
    private readonly AccountStore? _accounts;
    private readonly Func<Random> _rngFactory;
    // D8-P2-05：固定 64 条带无符号 FNV-1a 锁 + 有界 4096/2h TTL 挑战缓存，根治长跑内存泄漏
    private readonly object[] _accountLockStripes = Enumerable.Range(0, AccountLockStripeCount).Select(_ => new object()).ToArray();
    private readonly ConcurrentDictionary<Guid, CachedChallengeEntry> _cache = new();

    public ChallengeService(
        Persistence db,
        GameDataRepository repo,
        AccountStore? accounts = null,
        Func<Random>? rngFactory = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _accounts = accounts;
        _rngFactory = rngFactory ?? (() => Random.Shared);
    }

    /// <summary>获取（必要时自动初始化或按周期刷新）指定账号的每日 3 条 + 每周 3 条挑战快照。</summary>
    public ChallengesDto GetChallenges(Guid accountId, DateTime? utcNow = null)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        lock (GetAccountLock(accountId))
        {
            var state = LoadOrRefreshLocked(accountId, now, out bool dirty);
            if (dirty) SaveStateLocked(accountId, state);
            return ToDto(state);
        }
    }

    /// <summary>重随指定每日挑战（消耗 1 次 dailyResets，替换为另一条未持有的活跃每日挑战并持久化）。</summary>
    public ChallengeRerollResult RerollDaily(Guid accountId, Guid challengeId, DateTime? utcNow = null)
        => RerollCore(accountId, challengeId, isDaily: true, utcNow);

    /// <summary>重随指定每周挑战（消耗 1 次 weeklyResets，替换为另一条未持有的活跃每周挑战并持久化）。</summary>
    public ChallengeRerollResult RerollWeekly(Guid accountId, Guid challengeId, DateTime? utcNow = null)
        => RerollCore(accountId, challengeId, isDaily: false, utcNow);

    private ChallengeRerollResult RerollCore(Guid accountId, Guid challengeId, bool isDaily, DateTime? utcNow)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        lock (GetAccountLock(accountId))
        {
            var state = LoadOrRefreshLocked(accountId, now, out bool dirty);
            int resets = isDaily ? state.DailyResetsRemaining : state.WeeklyResetsRemaining;
            var slots = isDaily ? state.DailyChallenges : state.WeeklyChallenges;

            if (resets <= 0)
            {
                if (dirty) SaveStateLocked(accountId, state);
                return new ChallengeRerollResult(false, null, null, ToDto(state), "当前周期重随次数已用完。");
            }

            int idx = slots.FindIndex(s => s.Id == challengeId);
            if (idx < 0)
            {
                if (dirty) SaveStateLocked(accountId, state);
                return new ChallengeRerollResult(false, null, null, ToDto(state), "指定挑战不在当前活跃槽位中。");
            }

            var targetSlot = slots[idx];
            var oldTpl = FindTemplate(targetSlot.Id);
            if (targetSlot.Acknowledged || (oldTpl != null && targetSlot.Progress >= oldTpl.CompletionRequirement))
            {
                if (dirty) SaveStateLocked(accountId, state);
                return new ChallengeRerollResult(false, null, null, ToDto(state), "已完成或已领奖的挑战不可重随。");
            }

            var currentIds = slots.Select(s => s.Id).ToHashSet();
            var pool = isDaily ? _repo.ActiveDailyChallenges : _repo.ActiveWeeklyChallenges;
            var candidates = new List<Guid>();
            for (int i = 0; i < pool.Count; i++)
            {
                if (Guid.TryParse(pool[i].Id, out var g) && !currentIds.Contains(g))
                    candidates.Add(g);
            }

            if (candidates.Count == 0)
            {
                if (dirty) SaveStateLocked(accountId, state);
                return new ChallengeRerollResult(false, null, null, ToDto(state), "没有可供替换的其他活跃挑战。");
            }

            var rng = _rngFactory();
            Guid replacement = candidates[rng.Next(candidates.Count)];
            slots[idx] = new PersistedChallengeSlot
            {
                Id = replacement,
                Progress = 0,
                Acknowledged = false,
                RewardGranted = false,
            };

            if (isDaily) state.DailyResetsRemaining = Math.Max(0, state.DailyResetsRemaining - 1);
            else state.WeeklyResetsRemaining = Math.Max(0, state.WeeklyResetsRemaining - 1);

            SaveStateLocked(accountId, state);
            return new ChallengeRerollResult(true, challengeId, replacement, ToDto(state), null);
        }
    }

    /// <summary>
    /// 确认领取已完成挑战并向当前赛季通行证发放 XpReward（严格幂等：重复调用不重复发奖）。
    /// </summary>
    public ChallengeAcknowledgeResult AcknowledgeChallenge(
        Guid accountId,
        Guid challengeId,
        int currentSeasonId,
        DateTime? utcNow = null)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        int seasonId = currentSeasonId > 0 ? currentSeasonId : 2;
        lock (GetAccountLock(accountId))
        {
            var state = LoadOrRefreshLocked(accountId, now, out bool dirty);
            var slot = state.DailyChallenges.FirstOrDefault(s => s.Id == challengeId)
                       ?? state.WeeklyChallenges.FirstOrDefault(s => s.Id == challengeId);

            if (slot == null)
            {
                if (dirty) SaveStateLocked(accountId, state);
                int curXp = _db.LoadSeasonTrackProgress(accountId, seasonId).Experience;
                return new ChallengeAcknowledgeResult(false, 0, curXp, ToDto(state), "未找到指定的活跃挑战。");
            }

            var tpl = FindTemplate(slot.Id);
            if (tpl == null)
            {
                if (dirty) SaveStateLocked(accountId, state);
                int curXp = _db.LoadSeasonTrackProgress(accountId, seasonId).Experience;
                return new ChallengeAcknowledgeResult(false, 0, curXp, ToDto(state), "挑战模板不存在。");
            }

            if (slot.Progress < tpl.CompletionRequirement)
            {
                if (dirty) SaveStateLocked(accountId, state);
                int curXp = _db.LoadSeasonTrackProgress(accountId, seasonId).Experience;
                return new ChallengeAcknowledgeResult(false, 0, curXp, ToDto(state), "该挑战尚未完成，无法领取奖励。");
            }

            if (slot.Acknowledged)
            {
                if (dirty) SaveStateLocked(accountId, state);
                int curXp = _db.LoadSeasonTrackProgress(accountId, seasonId).Experience;
                return new ChallengeAcknowledgeResult(true, 0, curXp, ToDto(state), null);
            }

            slot.Acknowledged = true;
            int xpGranted = 0;
            int newSeasonXp;
            if (!slot.RewardGranted)
            {
                slot.RewardGranted = true;
                xpGranted = Math.Max(0, tpl.XpReward);
                newSeasonXp = _db.AddSeasonTrackExperience(accountId, seasonId, xpGranted);

                if (tpl.CurrencyRewards is { Count: > 0 } && _accounts != null)
                {
                    var acc = _accounts.FindByAccountId(accountId.ToString("D"));
                    if (acc != null)
                    {
                        foreach (var cr in tpl.CurrencyRewards)
                        {
                            if (cr != null && cr.Amount > 0 &&
                                string.Equals(cr.CurrencyType, "Gem", StringComparison.OrdinalIgnoreCase))
                            {
                                _accounts.AddGems(acc, cr.Amount);
                            }
                        }
                    }
                }
            }
            else
            {
                newSeasonXp = _db.LoadSeasonTrackProgress(accountId, seasonId).Experience;
            }

            SaveStateLocked(accountId, state);
            return new ChallengeAcknowledgeResult(true, xpGranted, newSeasonXp, ToDto(state), null);
        }
    }

    /// <summary>
    /// 按事件触发器类型与结构化上下文推进账号当前活跃挑战进度（供 RunEngine / ShopEngine / CombatSimulator 接入及 TempoNet 调用）。
    /// </summary>
    public ChallengeProgressUpdateResult RecordChallengeProgress(
        Guid accountId,
        string triggerType,
        int delta = 1,
        ChallengeProgressContext? context = null,
        DateTime? utcNow = null)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var updated = new List<Guid>();
        var newlyCompleted = new List<Guid>();

        lock (GetAccountLock(accountId))
        {
            var state = LoadOrRefreshLocked(accountId, now, out bool dirty);
            if (!string.IsNullOrWhiteSpace(triggerType) && delta > 0)
            {
                ApplyProgressToSlots(state.DailyChallenges, triggerType, delta, context, updated, newlyCompleted, ref dirty);
                ApplyProgressToSlots(state.WeeklyChallenges, triggerType, delta, context, updated, newlyCompleted, ref dirty);
            }

            if (dirty) SaveStateLocked(accountId, state);
            return new ChallengeProgressUpdateResult(updated, newlyCompleted, ToDto(state));
        }
    }

    /// <summary>
    /// 按挑战 ID 直接推进当前活跃槽位进度（供 /api/app/challenges/progress 显式增量同步与回归测试使用）。
    /// </summary>
    public ChallengeProgressUpdateResult RecordChallengeProgressById(
        Guid accountId,
        Guid challengeId,
        int delta,
        DateTime? utcNow = null)
    {
        DateTime now = (utcNow ?? DateTime.UtcNow).ToUniversalTime();
        var updated = new List<Guid>();
        var newlyCompleted = new List<Guid>();

        lock (GetAccountLock(accountId))
        {
            var state = LoadOrRefreshLocked(accountId, now, out bool dirty);
            if (challengeId != Guid.Empty && delta > 0)
            {
                var slot = state.DailyChallenges.FirstOrDefault(s => s.Id == challengeId)
                           ?? state.WeeklyChallenges.FirstOrDefault(s => s.Id == challengeId);
                var tpl = slot != null ? FindTemplate(slot.Id) : null;
                if (slot != null && tpl != null && slot.Progress < tpl.CompletionRequirement)
                {
                    int before = slot.Progress;
                    slot.Progress = Math.Min(tpl.CompletionRequirement, slot.Progress + delta);
                    if (slot.Progress != before)
                    {
                        updated.Add(slot.Id);
                        dirty = true;
                        if (slot.Progress >= tpl.CompletionRequirement && before < tpl.CompletionRequirement)
                            newlyCompleted.Add(slot.Id);
                    }
                }
            }

            if (dirty) SaveStateLocked(accountId, state);
            return new ChallengeProgressUpdateResult(updated, newlyCompleted, ToDto(state));
        }
    }

    /// <summary>构造 /api/app/challenges/progress 所需的完整 StateDeltaResponseDto（含挑战快照与赛季通行证进度）。</summary>
    public StateDeltaResponseDto BuildProgressStateDelta(Guid accountId, int currentSeasonId, DateTime? utcNow = null)
    {
        int seasonId = currentSeasonId > 0 ? currentSeasonId : 2;
        var challenges = GetChallenges(accountId, utcNow);
        var prog = _db.LoadSeasonTrackProgress(accountId, seasonId);
        return new StateDeltaResponseDto
        {
            Challenges = challenges,
            SeasonTrackProgression = new SeasonTrackProgressionDto
            {
                accountId = accountId,
                seasonId = seasonId,
                isPaid = prog.IsPaid,
                experience = prog.Experience,
                claimedFreeRewardTiers = prog.ClaimedFreeRewardTiers,
                claimedPaidRewardTiers = prog.ClaimedPaidRewardTiers,
                claimedFreeInfiniteTierCount = prog.ClaimedFreeInfiniteTierCount,
                claimedPaidInfiniteTierCount = prog.ClaimedPaidInfiniteTierCount,
            },
        };
    }

    /// <summary>
    /// 评估某条挑战模板的预编译 Trigger 规则节点是否匹配本次事件与上下文（覆盖官方全部 23 种挑战触发器与嵌套条件）。
    /// </summary>
    public static bool MatchesChallengeTrigger(
        ChallengeTemplate template,
        string triggerType,
        int delta,
        ChallengeProgressContext? context)
    {
        if (template?.Trigger == null || string.IsNullOrWhiteSpace(triggerType))
            return false;

        var trigger = template.Trigger;
        if (!string.Equals(trigger.Type, triggerType, StringComparison.OrdinalIgnoreCase))
            return false;

        // 1. 触发器顶层语义过滤
        switch (trigger.Type)
        {
            case "TTriggerOnPlayerAttributeChanged":
            {
                if (!string.IsNullOrWhiteSpace(trigger.AttributeType))
                {
                    if (context == null || !string.Equals(context.AttributeType, trigger.AttributeType, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                if (!string.IsNullOrWhiteSpace(trigger.ChangeType))
                {
                    if (context == null || !string.Equals(context.ChangeType, trigger.ChangeType, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                break;
            }
            case "TTriggerOnFightEnded":
            {
                if (!string.IsNullOrWhiteSpace(trigger.CombatType))
                {
                    if (context == null || !string.Equals(context.CombatType, trigger.CombatType, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                if (!string.IsNullOrWhiteSpace(trigger.CombatOutcome))
                {
                    if (context == null || !string.Equals(context.CombatOutcome, trigger.CombatOutcome, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                break;
            }
            case "TTriggerOnRunCompleted":
            {
                string? expectedOutcome = trigger.Outcome ?? trigger.CombatOutcome;
                if (!string.IsNullOrWhiteSpace(expectedOutcome))
                {
                    string? actualOutcome = context?.RunOutcome ?? context?.CombatOutcome;
                    if (!string.Equals(actualOutcome, expectedOutcome, StringComparison.OrdinalIgnoreCase))
                        return false;
                }
                break;
            }
            case "TTriggerOnPlayerLeveledUp":
            {
                int? reqLevel = trigger.NewLevel ?? trigger.Level;
                if (reqLevel.HasValue)
                {
                    if (context?.NewLevel == null)
                        return false;
                    string op = string.IsNullOrWhiteSpace(trigger.ComparisonOperator) ? "GreaterThanOrEqual" : trigger.ComparisonOperator!;
                    if (!CompareNumeric(context.NewLevel.Value, reqLevel.Value, op))
                        return false;
                }
                break;
            }
            case "TTriggerOnPlayerDamaged":
            case "TTriggerOnPlayerShielded":
            {
                double actualAmount = context?.Amount ?? delta;
                double? threshold = trigger.ComparisonAmount
                    ?? trigger.GetFixedComparisonValue()
                    ?? trigger.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue();
                if (threshold.HasValue && !string.IsNullOrWhiteSpace(trigger.ComparisonOperator))
                {
                    if (!CompareNumeric(actualAmount, threshold.Value, trigger.ComparisonOperator!))
                        return false;
                }
                break;
            }
            case "TTriggerOnCardUpgraded":
            {
                if (trigger.ExcludeFusion == true && context?.IsFusion == true)
                    return false;
                break;
            }
        }

        // 2. Subject.Conditions 嵌套卡牌条件过滤（兼容单条件对象与条件数组）
        var subject = trigger.Subject;
        if (subject != null)
        {
            var singleCond = subject.GetSingleCondition(GameDatabaseLoader.JsonOptions);
            if (singleCond != null && !EvaluateCardCondition(singleCond, context))
                return false;

            var condList = subject.GetConditionList(GameDatabaseLoader.JsonOptions);
            for (int i = 0; i < condList.Count; i++)
            {
                if (!EvaluateCardCondition(condList[i], context))
                    return false;
            }
        }

        return true;
    }

    private static bool EvaluateCardCondition(RuleNode cond, ChallengeProgressContext? ctx)
    {
        if (cond == null) return true;

        bool rawMatch;
        switch (cond.Type)
        {
            case "TCardConditionalAnd":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                rawMatch = true;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (!EvaluateCardCondition(subs[i], ctx))
                    {
                        rawMatch = false;
                        break;
                    }
                }
                break;
            }
            case "TCardConditionalOr":
            {
                var subs = cond.GetConditionList(GameDatabaseLoader.JsonOptions);
                rawMatch = false;
                for (int i = 0; i < subs.Count; i++)
                {
                    if (EvaluateCardCondition(subs[i], ctx))
                    {
                        rawMatch = true;
                        break;
                    }
                }
                break;
            }
            case "TCardConditionalType":
                rawMatch = !string.IsNullOrWhiteSpace(ctx?.CardType)
                           && string.Equals(ctx!.CardType, cond.CardType, StringComparison.OrdinalIgnoreCase);
                break;

            case "TCardConditionalSize":
                rawMatch = !string.IsNullOrWhiteSpace(ctx?.CardSize)
                           && cond.Sizes != null
                           && cond.Sizes.Contains(ctx!.CardSize!, StringComparer.OrdinalIgnoreCase);
                break;

            case "TCardConditionalTier":
                rawMatch = !string.IsNullOrWhiteSpace(ctx?.CardTier)
                           && cond.Tiers != null
                           && cond.Tiers.Contains(ctx!.CardTier!, StringComparer.OrdinalIgnoreCase);
                break;

            case "TCardConditionalTag":
                rawMatch = MatchTagList(ctx?.Tags, cond.Tags, cond.Operator);
                break;

            case "TCardConditionalHiddenTag":
                rawMatch = MatchTagList(ctx?.HiddenTags, cond.Tags ?? cond.HiddenTags, cond.Operator);
                break;

            case "TCardConditionalHero":
                rawMatch = !string.IsNullOrWhiteSpace(ctx?.Hero)
                           && cond.Heroes != null
                           && cond.Heroes.Contains(ctx!.Hero!, StringComparer.OrdinalIgnoreCase);
                break;

            case "TCardConditionalId":
                rawMatch = !string.IsNullOrWhiteSpace(ctx?.CardTemplateId)
                           && ((cond.Ids != null && cond.Ids.Contains(ctx!.CardTemplateId!, StringComparer.OrdinalIgnoreCase))
                               || string.Equals(ctx!.CardTemplateId, cond.Id, StringComparison.OrdinalIgnoreCase));
                break;

            case "TCardConditionalHasEnchantment":
                rawMatch = ctx?.HasEnchantment == true;
                break;

            case "TCardConditionalAttribute":
            {
                string? attrName = cond.Attribute ?? cond.AttributeType;
                if (string.IsNullOrWhiteSpace(attrName) || ctx?.CardAttributes == null ||
                    !ctx.CardAttributes.TryGetValue(attrName, out double actual))
                {
                    rawMatch = false;
                }
                else
                {
                    double expected = cond.GetFixedComparisonValue()
                        ?? cond.GetComparisonValueNode(GameDatabaseLoader.JsonOptions)?.GetFixedNumericValue()
                        ?? cond.ComparisonAmount
                        ?? 0;
                    string op = string.IsNullOrWhiteSpace(cond.ComparisonOperator) ? "GreaterThanOrEqual" : cond.ComparisonOperator!;
                    rawMatch = CompareNumeric(actual, expected, op);
                }
                break;
            }

            default:
                rawMatch = true;
                break;
        }

        return cond.IsNot == true ? !rawMatch : rawMatch;
    }

    private static bool MatchTagList(IReadOnlyCollection<string>? actualTags, List<string>? requiredTags, string? op)
    {
        if (requiredTags == null || requiredTags.Count == 0) return true;
        if (actualTags == null || actualTags.Count == 0) return false;

        bool requireAll = string.Equals(op, "All", StringComparison.OrdinalIgnoreCase);
        if (requireAll)
        {
            for (int i = 0; i < requiredTags.Count; i++)
            {
                if (!actualTags.Contains(requiredTags[i], StringComparer.OrdinalIgnoreCase))
                    return false;
            }
            return true;
        }

        for (int i = 0; i < requiredTags.Count; i++)
        {
            if (actualTags.Contains(requiredTags[i], StringComparer.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool CompareNumeric(double actual, double expected, string op) => op.Trim() switch
    {
        "Equal" or "Equals" or "==" => Math.Abs(actual - expected) < 1e-6,
        "NotEqual" or "!=" => Math.Abs(actual - expected) >= 1e-6,
        "GreaterThan" or ">" => actual > expected,
        "GreaterThanOrEqual" or ">=" => actual >= expected,
        "LessThan" or "<" => actual < expected,
        "LessThanOrEqual" or "<=" => actual <= expected,
        _ => actual >= expected,
    };

    private void ApplyProgressToSlots(
        List<PersistedChallengeSlot> slots,
        string triggerType,
        int delta,
        ChallengeProgressContext? context,
        List<Guid> updated,
        List<Guid> newlyCompleted,
        ref bool dirty)
    {
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            var tpl = FindTemplate(slot.Id);
            if (tpl == null || slot.Progress >= tpl.CompletionRequirement) continue;

            if (!MatchesChallengeTrigger(tpl, triggerType, delta, context))
                continue;

            int before = slot.Progress;
            slot.Progress = Math.Min(tpl.CompletionRequirement, slot.Progress + delta);
            if (slot.Progress != before)
            {
                updated.Add(slot.Id);
                dirty = true;
                if (slot.Progress >= tpl.CompletionRequirement && before < tpl.CompletionRequirement)
                    newlyCompleted.Add(slot.Id);
            }
        }
    }

    private static uint ComputeFnv1a32(Guid accountId)
    {
        Span<byte> bytes = stackalloc byte[16];
        accountId.TryWriteBytes(bytes);
        uint hash = 2166136261u;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 16777619u;
        }
        return hash;
    }

    private object GetAccountLock(Guid accountId)
        => _accountLockStripes[ComputeFnv1a32(accountId) % (uint)_accountLockStripes.Length];

    /// <summary>D8-P2-05：按 2 小时滑动 TTL 与 4096 上限驱逐陈旧挑战缓存条目。</summary>
    public int EvictStaleCacheEntries()
    {
        long nowMs = Environment.TickCount64;
        int removed = 0;
        foreach (var (aid, entry) in _cache)
        {
            long lastMs = Volatile.Read(ref entry.LastAccessTickMs);
            if (nowMs - lastMs > CacheSlidingTtlMs && _cache.TryRemove(aid, out _))
                removed++;
        }
        if (_cache.Count > MaxCachedAccounts)
        {
            var oldest = _cache
                .OrderBy(kv => Volatile.Read(ref kv.Value.LastAccessTickMs))
                .Take(_cache.Count - (MaxCachedAccounts * 3 / 4))
                .Select(kv => kv.Key)
                .ToList();
            foreach (var aid in oldest)
            {
                if (_cache.TryRemove(aid, out _))
                    removed++;
            }
        }
        return removed;
    }

    private ChallengeTemplate? FindTemplate(Guid id)
        => _repo.GetChallenge(id.ToString("D"));

    private PersistedAccountChallengeState LoadOrRefreshLocked(Guid accountId, DateTime utcNow, out bool dirty)
    {
        dirty = false;
        long nowTick = Environment.TickCount64;
        PersistedAccountChallengeState? state;
        if (_cache.TryGetValue(accountId, out var cachedEntry))
        {
            Volatile.Write(ref cachedEntry.LastAccessTickMs, nowTick);
            state = cachedEntry.State;
        }
        else
        {
            state = null;
            string raw = _db.LoadAccountChallengesJson(accountId);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try { state = JsonSerializer.Deserialize<PersistedAccountChallengeState>(raw, JsonOpts); }
                catch { state = null; }
            }
            if (state == null)
            {
                state = new PersistedAccountChallengeState();
                dirty = true;
            }
            if (_cache.Count >= MaxCachedAccounts)
                EvictStaleCacheEntries();
            _cache[accountId] = new CachedChallengeEntry { State = state, LastAccessTickMs = nowTick };
        }

        string dailyKey = utcNow.ToString("yyyy-MM-dd");
        DateTime dailyExpire = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(1);
        int daysSinceMonday = ((int)utcNow.DayOfWeek + 6) % 7;
        DateTime weekStart = new DateTime(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysSinceMonday);
        string weeklyKey = weekStart.ToString("yyyy-MM-dd");
        DateTime weeklyExpire = weekStart.AddDays(7);

        // 1. 每日周期检查与补齐 3 条
        if (!string.Equals(state.DailyPeriodKey, dailyKey, StringComparison.Ordinal) || state.DailyExpiresAtUtc <= utcNow)
        {
            state.DailyPeriodKey = dailyKey;
            state.DailyExpiresAtUtc = dailyExpire;
            state.DailyResetsRemaining = DefaultDailyResets;
            state.DailyChallenges = PickRandomSlots(_repo.ActiveDailyChallenges, DailySlotCount);
            dirty = true;
        }
        else if (EnsureValidSlotCount(state.DailyChallenges, _repo.ActiveDailyChallenges, DailySlotCount))
        {
            dirty = true;
        }

        // 2. 每周周期检查与补齐 3 条
        if (!string.Equals(state.WeeklyPeriodKey, weeklyKey, StringComparison.Ordinal) || state.WeeklyExpiresAtUtc <= utcNow)
        {
            state.WeeklyPeriodKey = weeklyKey;
            state.WeeklyExpiresAtUtc = weeklyExpire;
            state.WeeklyResetsRemaining = DefaultWeeklyResets;
            state.WeeklyChallenges = PickRandomSlots(_repo.ActiveWeeklyChallenges, WeeklySlotCount);
            dirty = true;
        }
        else if (EnsureValidSlotCount(state.WeeklyChallenges, _repo.ActiveWeeklyChallenges, WeeklySlotCount))
        {
            dirty = true;
        }

        return state;
    }

    private bool EnsureValidSlotCount(
        List<PersistedChallengeSlot> slots,
        IReadOnlyList<ChallengeTemplate> pool,
        int targetCount)
    {
        bool changed = false;
        int removed = slots.RemoveAll(s => s.Id == Guid.Empty || FindTemplate(s.Id) == null);
        if (removed > 0) changed = true;

        if (slots.Count < targetCount && pool.Count > 0)
        {
            var existing = slots.Select(s => s.Id).ToHashSet();
            var avail = new List<Guid>();
            for (int i = 0; i < pool.Count; i++)
            {
                if (Guid.TryParse(pool[i].Id, out var g) && !existing.Contains(g))
                    avail.Add(g);
            }

            var rng = _rngFactory();
            while (slots.Count < targetCount && avail.Count > 0)
            {
                int pickIdx = rng.Next(avail.Count);
                Guid picked = avail[pickIdx];
                avail.RemoveAt(pickIdx);
                slots.Add(new PersistedChallengeSlot { Id = picked, Progress = 0, Acknowledged = false, RewardGranted = false });
                changed = true;
            }
        }
        return changed;
    }

    private List<PersistedChallengeSlot> PickRandomSlots(IReadOnlyList<ChallengeTemplate> pool, int count)
    {
        var result = new List<PersistedChallengeSlot>(count);
        if (pool == null || pool.Count == 0 || count <= 0) return result;

        var candidates = new List<Guid>(pool.Count);
        for (int i = 0; i < pool.Count; i++)
        {
            if (Guid.TryParse(pool[i].Id, out var g))
                candidates.Add(g);
        }

        var rng = _rngFactory();
        int take = Math.Min(count, candidates.Count);
        for (int i = 0; i < take; i++)
        {
            int j = rng.Next(i, candidates.Count);
            (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            result.Add(new PersistedChallengeSlot
            {
                Id = candidates[i],
                Progress = 0,
                Acknowledged = false,
                RewardGranted = false,
            });
        }
        return result;
    }

    private void SaveStateLocked(Guid accountId, PersistedAccountChallengeState state)
    {
        string json = JsonSerializer.Serialize(state, JsonOpts);
        _db.SaveAccountChallengesJson(accountId, json);
    }

    private static ChallengesDto ToDto(PersistedAccountChallengeState state)
    {
        var daily = state.DailyChallenges
            .Select(s => (object)new ChallengeProgressItemDto
            {
                Id = s.Id,
                Progress = s.Progress,
                Acknowledged = s.Acknowledged,
            })
            .ToArray();
        var weekly = state.WeeklyChallenges
            .Select(s => (object)new ChallengeProgressItemDto
            {
                Id = s.Id,
                Progress = s.Progress,
                Acknowledged = s.Acknowledged,
            })
            .ToArray();
        var merged = new object[daily.Length + weekly.Length];
        Array.Copy(daily, 0, merged, 0, daily.Length);
        Array.Copy(weekly, 0, merged, daily.Length, weekly.Length);

        return new ChallengesDto
        {
            challenges = merged,
            dailyChallenges = daily,
            weeklyChallenges = weekly,
            dailyResets = state.DailyResetsRemaining,
            weeklyResets = state.WeeklyResetsRemaining,
            dailyChallengesExpireAt = state.DailyExpiresAtUtc,
            weeklyChallengesExpireAt = state.WeeklyExpiresAtUtc,
        };
    }
}

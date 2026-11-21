using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;

namespace BazaarServer.Game;

/// <summary>
/// 战败按伤比例折算结果（供 RunEngine 与战斗金币/经验跳字展示使用）。
/// </summary>
public readonly record struct MonsterDefeatRewardResult(
    double DamageRatio,
    int GoldAwarded,
    int XpAwarded,
    int BaseCombatGold,
    int BaseCombatXp,
    int BaseDefeatGold,
    int BaseDefeatXp);

/// <summary>
/// 商店 / 遭遇 / 升级 / 战利品 发牌引擎。
///
/// 严格遵循客户端反编译状态机协议：
///   · 候选卡一律设置 Owner = null, Socket = null, Section = null，并通过 GameSimEventCardDealt 下发，
///     且严格保留模板真实 ECardType（Item / Skill / EncounterStep / EventEncounter / PedestalEncounter / CombatEncounter）。
///   · Choice 态专用于选遭遇（CanExit = false）；
///     选中商人或事件后切 Encounter 态（携带真实 CurrentEncounterId 及完整的 SelectionContextRules）。
/// </summary>
public sealed class ShopEngine
{
    public const string Hour6PvpCardId = "f39e9af2-989b-41f8-b212-5333415edd23";
    public const string NufuEncounterId = "210003fa-a20d-4ad1-9ada-d5fad5bf9f89";
    public const float MerchantItemDiscountChance = 0.12f;
    public const int MerchantItemDiscountPercent = 25;
    public const int EmptyFreeDealFallbackGold = 3;
    public const uint UnlimitedRerollsCount = 999;
    public const int MaxMonsterLootSlotsPerPage = 10;
    public const int MaxShelfSlotWidth = 10;

    /// <summary>分页怪物战利品自选模式下的单张候选战利品元数据。</summary>
    public sealed record MonsterLootCandidate(
        CardPool.CardTemplate Template,
        ETier Tier,
        string? Enchantment,
        int SlotCost);

    /// <summary>商人货架单卡 25% 折扣绿标元信息（与 BuyPrice 协同生效，Reroll 时随货架清空重掷）。</summary>
    public sealed record MerchantDiscountInfo(
        string InstanceId,
        string TemplateId,
        int BasePrice,
        int DiscountedPrice,
        int DiscountPercent,
        double PriceMultiplier);

    // 官方各英雄开局房间/包模板 ID（均以白名单验证为终准）
    private static readonly IReadOnlyDictionary<EHero, string> HeroStarterEncounterIds =
        new Dictionary<EHero, string>
        {
            [EHero.Pygmalien] = "5df1c571-e80e-492d-ae4c-78e4fb1e1ee8", // Pygmalien's Loft (Start Skill)
            [EHero.Dooley] = "69bc6d15-487a-43cd-9b7f-d0652853d991",    // Dooley's Crib (Start Run)
            [EHero.Mak] = "001a960b-7ffb-4156-9fc1-1121eb93fb9e",       // Mak's Laboratory (Start Run)
            [EHero.Stelle] = "d6c69fd2-7493-4b55-af18-eab0695cf16a",    // Stelle's Workshop (Start Run)
            [EHero.Jules] = "c4e01347-bcbc-e164-c848-544774f80599",     // Nonna (Jules 专属资产有效技能开局)
            // Vanessa's Quarters (6e33409d / b10b084f) 的 ArtKey (c85a98b4c88018cd07424b5007424b50) 缺失于 catalog.bin，
            // 在 ResolveValidStarterEncounterId 中将被白名单自动拦下并切往 Nufu，同时仍复用 6e33409d 的精选技能列表，避免客户端 InvalidKeyException。
            [EHero.Vanessa] = "6e33409d-d390-4764-969f-f96e96629213",
            [EHero.Karnok] = "de175e34-df34-4581-a13a-46767e1c7c6b",  // Kelsa (Level Up)（Karnok 官方资产有效技能开局）
            [EHero.Hero8] = "d33129a9-5df2-47e5-9b6a-7469743fe5a8",     // The Dragons' Hideout
        };

    private const string DooleyCoreInitializationId = "1596d1df-e943-48b4-bfc6-df2edb9dfe55";
    private const string DooleyCribEncounterId = "2e0acd01-e0b2-4c0d-a7aa-84914c0cbb94";
    private const string VanessaCuratedSkillListId = "6e33409d-d390-4764-969f-f96e96629213";

    private readonly RunState _run;
    private readonly CardPool _pool;
    private readonly GameDataRepository _repo;
    private readonly TierChances _chances;
    private readonly Random _rng = new();
    private readonly Dictionary<string, MerchantDiscountInfo> _activeShelfDiscounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<List<MonsterLootCandidate>> _monsterLootPages = new();
    private CardArchetypeIndex? _cachedArchetypeIndex;
    private int _seq;

    public ShopEngine(RunState run, CardPool pool, GameDataRepository repo)
    {
        _run = run;
        _pool = pool;
        _repo = repo;
        _chances = pool.CreateTierChances();
        RestoreMonsterLootPagesFromRun();
    }

    public ShopEngine(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        CustomRunRuleSet? activeRules,
        CardArchetypeIndex? archetypeIndex = null)
        : this(run, pool, repo)
    {
        ActiveRules = activeRules;
        ArchetypeIndex = archetypeIndex;
    }

    /// <summary>当前生效的自定义规则集（默认 null 表示完全采用官方标准规则）。</summary>
    public CustomRunRuleSet? ActiveRules { get; set; }

    /// <summary>可选外部注入的流派与遭遇分类索引；未设置且规则需要时按需懒构建。</summary>
    public CardArchetypeIndex? ArchetypeIndex { get; set; }

    /// <summary>分页怪物战利品自选模式下当前缓存的全部页数据（未启用分页时为空）。</summary>
    public IReadOnlyList<IReadOnlyList<MonsterLootCandidate>> MonsterLootPages => _monsterLootPages;

    /// <summary>当前展示的怪物战利品页索引（从 0 开始）。</summary>
    public int CurrentMonsterLootPageIndex { get; private set; }

    /// <summary>当前货架上已命中的商人 25% 折扣卡明细（实例 ID -> 折扣记录）。</summary>
    public IReadOnlyDictionary<string, MerchantDiscountInfo> ActiveShelfDiscounts => _activeShelfDiscounts;

    /// <summary>当前遭遇显式配置的 MaxSelections 上限（null 表示未显式限制）。</summary>
    public int? CurrentMaxSelections { get; private set; }

    /// <summary>当前选择上下文的有效可选次数上限（非多选默认 1，多选未设 MaxSelections 时为 null）。</summary>
    public int? EffectiveMaxSelections => CurrentMaxSelections ?? (_run.SelectionRules.CanSelectMultiple ? null : 1);

    public bool TryGetShelfDiscount(string instanceId, out MerchantDiscountInfo? info)
        => _activeShelfDiscounts.TryGetValue(instanceId, out info);

    public bool HasReachedMaxSelections(int selectionsMadeInCurrentContext)
        => EffectiveMaxSelections is int max && selectionsMadeInCurrentContext >= max;

    internal void SetCurrentMaxSelections(int? maxSelections)
        => CurrentMaxSelections = maxSelections.HasValue ? Math.Max(1, maxSelections.Value) : null;

    public string NewId(string prefix) => $"{prefix}_{(_seq++):X4}_{DateTime.UtcNow.Ticks % 100000:X5}";

    /// <summary>清掉所有待选临时卡（SelectionSet 或 Owner == null 的卡）、重置货架折扣及战利品分页。</summary>
    public List<IGameSimEvent> ClearShelf()
        => ClearShelfInternal(clearMonsterLootPages: true);

    private List<IGameSimEvent> ClearShelfInternal(bool clearMonsterLootPages)
    {
        var events = new List<IGameSimEvent>();
        var toRemove = _run.Cards.Values
            .Where(c => c.Owner == null || _run.SelectionSet.Contains(c.InstanceId))
            .Select(c => c.InstanceId)
            .Distinct()
            .ToList();

        foreach (string id in toRemove)
        {
            _run.Cards.Remove(id);
            events.Add(new GameSimEventCardDisposed(id, ECardDisposalReason.Cleared));
        }
        _run.SelectionSet.Clear();
        _activeShelfDiscounts.Clear();
        if (clearMonsterLootPages)
        {
            _monsterLootPages.Clear();
            CurrentMonsterLootPageIndex = 0;
            _run.MonsterLootPages.Clear();
            _run.MonsterLootPageIndex = 0;
        }
        return events;
    }

    private void RestoreMonsterLootPagesFromRun()
    {
        if (_run.MonsterLootPages.Count == 0)
            return;

        _monsterLootPages.Clear();
        foreach (var page in _run.MonsterLootPages)
        {
            var restoredPage = new List<MonsterLootCandidate>();
            foreach (var item in page)
            {
                var tpl = _pool.Get(item.TemplateId);
                if (tpl == null) continue;
                restoredPage.Add(new MonsterLootCandidate(
                    tpl,
                    ParseTier(item.Tier),
                    item.Enchantment,
                    Math.Max(1, item.SlotCost)));
            }
            if (restoredPage.Count > 0)
                _monsterLootPages.Add(restoredPage);
        }

        CurrentMonsterLootPageIndex = _monsterLootPages.Count > 0
            ? Math.Clamp(_run.MonsterLootPageIndex, 0, _monsterLootPages.Count - 1)
            : 0;
    }

    private void SyncMonsterLootPagesToRun()
    {
        _run.MonsterLootPages.Clear();
        foreach (var page in _monsterLootPages)
        {
            _run.MonsterLootPages.Add(page
                .Select(c => new RunState.MonsterLootPageItemState(
                    c.Template.Id,
                    c.Tier.ToString(),
                    c.Enchantment,
                    c.SlotCost))
                .ToList());
        }
        _run.MonsterLootPageIndex = CurrentMonsterLootPageIndex;
    }

    private CardArchetypeIndex GetOrBuildArchetypeIndex()
        => ArchetypeIndex ?? (_cachedArchetypeIndex ??= CardArchetypeIndex.Build(_pool, _repo));

    private Func<string, ulong>? ResolveArchetypeResolver(CustomRunRuleSet? rules, CardArchetypeIndex? explicitIndex = null)
    {
        if (rules == null || rules.SelectedArchetypeMask == 0UL)
            return null;
        var index = explicitIndex ?? GetOrBuildArchetypeIndex();
        return index.GetArchetypeMask;
    }

    /// <summary>
    /// 返回英雄官方开局遭遇模板 ID（严格经过客户端 Addressables 资产白名单校验；
    /// 若该英雄专属房间资产在本地包中缺失或无独立配置，则安全回退至资产完备的 Nufu）。
    /// </summary>
    public static string GetStarterEncounterId(EHero hero)
        => HeroStarterEncounterIds.TryGetValue(hero, out var id) ? id : NufuEncounterId;

    /// <summary>实例级开局遭遇 ID 解析（结合 CardPool 白名单校验确保 0 InvalidKeyException）。</summary>
    public string ResolveValidStarterEncounterId(EHero hero)
    {
        if (HeroStarterEncounterIds.TryGetValue(hero, out var id) &&
            _pool.Get(id) != null &&
            _pool.IsAssetValid(id))
        {
            return id;
        }
        return _pool.IsAssetValid(NufuEncounterId) ? NufuEncounterId : GetStarterEncounterId(hero);
    }

    /// <summary>
    /// 开局起始青铜技能三选一（在各英雄资产完备的官方开局房间中免费选 1 个，经白名单严格过滤）。
    /// 保留原有方法签名以兼容 RunEngine.StartRun 与 cheat restart。
    /// </summary>
    public List<IGameSimEvent> DealStarterSkills(int count = 3)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());
        CurrentMaxSelections = null;

        string starterEncId = ResolveValidStarterEncounterId(_run.Hero);
        _run.State = ERunState.Encounter;
        _run.CurrentEncounterId = starterEncId;
        _run.RerollCost = 0;
        _run.RerollsRemaining = 0;
        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = false,
        };

        string hero = _run.Hero.ToString();
        var usedTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<CardPool.CardTemplate>();
        var effectiveRules = ActiveRules;
        var archetypeResolver = ResolveArchetypeResolver(effectiveRules);

        // 1. 优先尝试从官方开局模板 SpawnContext（例如 Pygmalien's Loft / Vanessa's Quarters 精选技能池）抽取
        string curatedSourceId = _run.Hero == EHero.Vanessa ? VanessaCuratedSkillListId : starterEncId;
        var curatedCore = _repo.GetCard(curatedSourceId);
        if (curatedCore?.SelectionContext?.SpawnContext != null)
        {
            var queryPlayer = BuildDummyQueryPlayer(hero, (int)_run.Day);
            var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, _rng)
            {
                CurrentDay = (int)_run.Day,
                CurrentHour = (int)_run.Hour,
                CurrentEncounterId = starterEncId,
                PlayMode = _run.PlayMode.ToString(),
            };
            var spawned = SpawnQueryEngine.SpawnCards(
                NormalizeSequentialSingleGroupQuery(curatedCore.SelectionContext.SpawnContext),
                ctx,
                count,
                ownedMaxTiers: null,
                validAssetIds: null,
                archetypeResolver,
                effectiveRules);
            var curatedNonBronze = new List<CardPool.CardTemplate>();
            foreach (var inst in spawned)
            {
                if (!string.Equals(inst.Template.Type, "Skill", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_pool.IsPlayerSpawnableId(inst.Template.Id, allowGuidOnly: true)) continue;
                var poolTpl = _pool.Get(inst.Template.Id);
                if (poolTpl == null) continue;
                // 开局 3 选 1 优先精选青铜技能，避免 Kelsa/Nonna 的升级过滤器在 Day 1 直接塞入高阶技能
                if (string.Equals(poolTpl.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
                {
                    if (usedTemplateIds.Add(poolTpl.Id))
                        candidates.Add(poolTpl);
                }
                else
                {
                    curatedNonBronze.Add(poolTpl);
                }
            }
            if (candidates.Count < count && curatedNonBronze.Count > 0)
            {
                // 若精选本身偏向青铜以下补足，仍留到步骤 2 优先从本英雄青铜技能池补满，此处仅作最后兜底
                foreach (var extra in curatedNonBronze)
                {
                    if (!_pool.Skills(hero).Any(s => string.Equals(s.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (candidates.Count < count && usedTemplateIds.Add(extra.Id))
                            candidates.Add(extra);
                    }
                }
            }
        }

        // 2. 不足时从白名单过滤后的英雄青铜技能池（英雄专属优先，再通用技能）补全至 count 张
        if (candidates.Count < count)
        {
            var allSkills = _pool.Skills(hero);
            var heroBronze = allSkills
                .Where(c => c.Heroes.Contains(hero, StringComparer.OrdinalIgnoreCase) &&
                            string.Equals(c.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var anyBronze = allSkills
                .Where(c => string.Equals(c.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var pool in new[] { heroBronze, anyBronze, allSkills })
            {
                while (candidates.Count < count)
                {
                    var pick = PickRandomUnused(pool, usedTemplateIds, archetypeResolver, effectiveRules);
                    if (pick == null) break;
                    candidates.Add(pick);
                }
                if (candidates.Count >= count) break;
            }
        }

        foreach (var card in candidates.Take(count))
        {
            var entry = CreateSkillCandidate(card, ParseTier(card.StartingTier), isFree: true);
            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            events.Add(new GameSimEventCardDealt(entry.InstanceId, card.Id, ECardType.Skill));
        }

        events.Add(new GameSimEventStateTransitioned(ERunState.Encounter, _run.CurrentEncounterId));
        return events;
    }

    /// <summary>
    /// 官方 1.0.13122 Day 1 Hour 1 开局入口：所有英雄直接进入白昼遭遇 3 选 1（Choice 态）。
    /// （Dooley 的 Core Initialization 为 Lv2 升级必出奖励，Dooley's Crib 为高阶遭遇。）
    /// </summary>
    public List<IGameSimEvent> DealInitialState(PlayerState? playerState = null)
        => DealHourEncounters(playerState);

    /// <summary>
    /// 英雄开局发牌入口（对齐官方 1.0.13122：不再强制塞入伪造的 3 技能开局，直接进入 Day 1 Hour 1 Choice）。
    /// 保留原有方法签名以兼容既有调用方。
    /// </summary>
    public List<IGameSimEvent> DealHeroStarterEncounter(bool allowEventPackages = true, int count = 3)
        => DealHourEncounters(playerState: null);

    /// <summary>
    /// 为当前 Day / Hour 在地图 ChoiceState 发遭遇选项：
    ///   · Hour 1, 2, 4, 5 → 3 个白昼遭遇（商人 + 真实事件奇遇 + Day 3+ 混入 Pedestal 基座）
    ///   · Hour 3          → 3 个匹配天数的 PvE 野怪战斗遭遇
    ///   · Hour 6          → 晚间 PvP 遭遇卡
    /// 可选传入 <paramref name="visitedTodayIds"/> 执行同日跨小时去重（若当前合法池耗尽则优雅回退，保持 3 槽布局）。
    /// </summary>
    public List<IGameSimEvent> DealHourEncounters()
        => DealHourEncounters(playerState: null, visitedTodayIds: null);

    public List<IGameSimEvent> DealHourEncounters(PlayerState? playerState, ISet<string>? visitedTodayIds = null)
        => DealHourEncounters(playerState, visitedTodayIds, ActiveRules, ArchetypeIndex);

    public List<IGameSimEvent> DealHourEncounters(
        PlayerState? playerState,
        ISet<string>? visitedTodayIds,
        CustomRunRuleSet? rules,
        CardArchetypeIndex? archetypeIndex = null)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());
        CurrentMaxSelections = null;

        string hero = _run.Hero.ToString();
        int day = (int)_run.Day;
        int maxDays = Math.Max(1, _repo.GameMode.NumberOfDays);
        int effectiveDay = Math.Clamp(day, 1, maxDays);
        uint hour = _run.Hour;
        _chances.CurrentDay = day;

        HashSet<string>? dailyVisited = visitedTodayIds is { Count: > 0 }
            ? new HashSet<string>(visitedTodayIds, StringComparer.OrdinalIgnoreCase)
            : null;

        var effectiveRules = rules ?? ActiveRules;
        HashSet<string>? bannedIds = effectiveRules?.BannedEncounterIds is { Count: > 0 }
            ? new HashSet<string>(effectiveRules.BannedEncounterIds, StringComparer.OrdinalIgnoreCase)
            : null;
        bool IsBanned(string id) => bannedIds != null && bannedIds.Contains(id);

        bool hasMultipliers = effectiveRules?.EncounterCategoryMultipliers is { Count: > 0 };
        var effectiveIndex = hasMultipliers ? (archetypeIndex ?? GetOrBuildArchetypeIndex()) : archetypeIndex;

        double GetCategoryMultiplier(EEncounterWeightCategory category)
        {
            if (!hasMultipliers) return 1.0;
            string key = EncounterWeightCategoryCatalog.ByCategory.TryGetValue(category, out var meta)
                ? meta.Key
                : category.ToString();
            if (effectiveRules!.EncounterCategoryMultipliers.TryGetValue(key, out double m))
                return double.IsFinite(m) ? Math.Max(0.0, m) : 1.0;
            return 1.0;
        }

        double GetEncounterMultiplier(CardPool.CardTemplate tpl)
        {
            if (!hasMultipliers) return 1.0;
            var cat = effectiveIndex != null && effectiveIndex.TryGetEncounterCategory(tpl.Id, out var c)
                ? c
                : CardArchetypeIndex.ClassifyEncounter(tpl, _repo.GetCard(tpl.Id));
            return GetCategoryMultiplier(cat);
        }

        _run.State = ERunState.Choice;
        _run.CurrentEncounterId = null;
        _run.RerollCost = 0;
        _run.RerollsRemaining = 0;
        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = false,
        };

        if (hour == 3)
        {
            // Day 11+ 无尽/加时天数保留真实 day，避免被 NumberOfDays=10 截断导致 Lv12~15 后期野怪永不出场
            var picks = RollDayMatchedMonsters(Math.Max(1, day), 3, dailyVisited, bannedIds);
            foreach (var card in picks)
            {
                var entry = CreateEncounterCandidate(card, ECardType.CombatEncounter, "cbt");
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, card.Id, ECardType.CombatEncounter));
            }
        }
        else if (hour == 6)
        {
            var pvpTpl = _pool.Get(Hour6PvpCardId)
                         ?? _pool.OfType("CombatEncounter").FirstOrDefault(c => c.InternalName.Contains("PvP", StringComparison.OrdinalIgnoreCase));
            if (pvpTpl != null)
            {
                var entry = CreateEncounterCandidate(pvpTpl, ECardType.PvpEncounter, "pvp");
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, pvpTpl.Id, ECardType.PvpEncounter));
            }
        }
        else
        {
            var pickedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queryPlayer = playerState ?? BuildDummyQueryPlayer(hero, effectiveDay);
            EnsureRunExclusionSetInitialized();
            ETier maxDayTier = GetMaxEncounterTierForDay(effectiveDay);

            bool IsExcludedInRun(string encId)
            {
                if (string.IsNullOrWhiteSpace(encId)) return false;
                if (playerState != null && (playerState.ExclusionSetInitialized || playerState.ExclusionSet.Count > 0))
                    return playerState.ExclusionSet.Contains(encId);
                return _run.ExclusionSet.Contains(encId);
            }

            bool IsEligibleEncounter(CardPool.CardTemplate enc)
            {
                if (IsExcludedInRun(enc.Id) || IsBanned(enc.Id)) return false;
                if (effectiveDay <= 1 && string.Equals(enc.Id, CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase))
                    return false;
                if (CardPool.IsExcludedDevOrStoryTemplate(enc) ||
                    string.Equals(enc.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
                    return false;
                var core = _repo.GetCard(enc.Id);
                var checkCtx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, _rng)
                {
                    CurrentDay = effectiveDay,
                    CurrentHour = (int)hour,
                    CurrentEncounterId = enc.Id,
                    PlayMode = _run.PlayMode.ToString(),
                    SelfCard = core != null ? new CardInstance(core) : null,
                };
                return CheckEncounterRequirements(enc, core, checkCtx);
            }

            var rawHourlyEvents = _pool.HourlyEncounters(hero, "EventEncounter");
            if (rawHourlyEvents.Count == 0)
                rawHourlyEvents = _pool.Encounters(hero, "EventEncounter");

            // 支持事件链（如 Burn it Down -> Burning Library、Thieves Guild Medallion -> Thieves Guild）
            // 在运行期动态移除 GameMode.ExclusionSet 后豁免 maxDayTier 解锁入池
            var unlockedExclusionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var tierExemptIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var allHourlyEvents = rawHourlyEvents.Where(IsEligibleEncounter).ToList();
            TryIncludeDynamicallyUnlockedExclusionEvents(
                allHourlyEvents,
                "EventEncounter",
                hero,
                IsExcludedInRun,
                IsEligibleEncounter,
                unlockedExclusionIds);
            foreach (string uId in unlockedExclusionIds)
                tierExemptIds.Add(uId);

            if (allHourlyEvents.Count == 0)
                allHourlyEvents = rawHourlyEvents
                    .Where(e => !IsExcludedInRun(e.Id) &&
                                !IsBanned(e.Id) &&
                                !(effectiveDay <= 1 && string.Equals(e.Id, CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase)) &&
                                !CardPool.IsExcludedDevOrStoryTemplate(e) &&
                                !string.Equals(e.Type, "CombatEncounter", StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // 官方 The Travel Agent 从 Day 2 起至 Day 9 可在非商人事件中出现卖门票（Day 1 禁止，Day 2..9 豁免 maxDayTier）
            double travelAgentMult = GetCategoryMultiplier(EEncounterWeightCategory.EventExpeditionAgent);
            if (effectiveDay is >= 2 and <= 9 && travelAgentMult > 0.0)
            {
                TryIncludeGuidOnlyEvent(
                    allHourlyEvents,
                    CardPool.TravelAgentEncounterId,
                    IsEligibleEncounter);
                if (allHourlyEvents.Any(e => e.Id.Equals(CardPool.TravelAgentEncounterId, StringComparison.OrdinalIgnoreCase)))
                    tierExemptIds.Add(CardPool.TravelAgentEncounterId);
            }
            else if (effectiveDay >= 10)
            {
                TryIncludeAmplifiedGuidOnlyEvent(
                    allHourlyEvents,
                    CardPool.TravelAgentEncounterId,
                    travelAgentMult,
                    maxDayTier,
                    IsEligibleEncounter);
            }

            TryIncludeAmplifiedGuidOnlyEvent(
                allHourlyEvents,
                CardArchetypeIndex.TheCultEncounterId,
                GetCategoryMultiplier(EEncounterWeightCategory.EventCultAndMystery),
                maxDayTier,
                IsEligibleEncounter);

            Func<CardPool.CardTemplate, double>? weightFn = hasMultipliers ? GetEncounterMultiplier : null;
            var nonZeroEvents = hasMultipliers
                ? allHourlyEvents.Where(e => GetEncounterMultiplier(e) > 0.0).ToList()
                : allHourlyEvents;

            var merchants = nonZeroEvents.Where(e => IsMerchantEncounter(e)).ToList();
            var nonMerchants = nonZeroEvents
                .Where(e => !IsMerchantEncounter(e) && HasPlayableEncounterPayload(e))
                .ToList();
            var activeEventPool = nonZeroEvents.Count > 0 ? nonZeroEvents : allHourlyEvents;
            var activeWeightFn = nonZeroEvents.Count > 0 ? weightFn : null;

            // 槽位 1：固定 1 家商人（对齐 BazaarCardDealer MerchantCount == 1；遵循分类倍率，除非自定义规则将全部商人权重置 0.0）
            CardPool.CardTemplate? m1 = null;
            if (merchants.Count > 0)
            {
                m1 = RollDayTieredEncounterWithDailyDedup(merchants, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: true, weightFn, bannedIds, tierExemptIds)
                  ?? RollDayTieredEncounterWithDailyDedup(merchants, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightFn, bannedIds, tierExemptIds);
            }
            else if (!hasMultipliers)
            {
                var allMerchants = allHourlyEvents.Where(e => IsMerchantEncounter(e)).ToList();
                m1 = RollDayTieredEncounterWithDailyDedup(allMerchants, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightMultiplier: null, bannedIds, tierExemptIds);
            }

            if (m1 == null)
            {
                m1 = RollDayTieredEncounterWithDailyDedup(nonMerchants, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: true, weightFn, bannedIds, tierExemptIds)
                  ?? RollDayTieredEncounterWithDailyDedup(activeEventPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, activeWeightFn, bannedIds, tierExemptIds);
            }
            if (m1 != null)
            {
                var entry = CreateEncounterCandidate(m1, ECardType.EventEncounter, "enc");
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, m1.Id, ECardType.EventEncounter));
            }

            // 槽位 2：固定 1 个非商人事件 / 多步事件根节点（若存在未访问的动态解锁 ExclusionSet 事件则高优先抽选）
            var nonMerchantAll = allHourlyEvents.Where(e => !IsMerchantEncounter(e) && HasPlayableEncounterPayload(e)).ToList();
            var nonMerchantFallbackPool = nonMerchantAll.Count > 0 ? nonMerchantAll : allHourlyEvents.Where(e => !IsMerchantEncounter(e)).ToList();
            var effectiveNonMerchantPool = nonMerchants.Count > 0 ? nonMerchants : nonMerchantFallbackPool;
            var prioritizedUnlocked = nonMerchants
                .Where(e => unlockedExclusionIds.Contains(e.Id) &&
                            !_run.VisitedSingleSpawnEncounterIds.Contains(e.Id) &&
                            (dailyVisited == null || !dailyVisited.Contains(e.Id)))
                .ToList();
            CardPool.CardTemplate? ev2 = prioritizedUnlocked.Count > 0
                ? RollDayTieredEncounterWithDailyDedup(prioritizedUnlocked, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: false, weightFn, bannedIds, tierExemptIds)
                : null;
            ev2 ??= RollDayTieredEncounterWithDailyDedup(effectiveNonMerchantPool, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: false, weightFn, bannedIds, tierExemptIds)
                 ?? RollDayTieredEncounterWithDailyDedup(effectiveNonMerchantPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: false, allowDailyFallback: true, weightFn, bannedIds, tierExemptIds)
                 ?? RollDayTieredEncounterWithDailyDedup(nonMerchantFallbackPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightMultiplier: null, bannedIds, tierExemptIds);
            if (ev2 == null && nonMerchantFallbackPool.Count == 0)
            {
                ev2 = RollDayTieredEncounterWithDailyDedup(activeEventPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightMultiplier: null, bannedIds, tierExemptIds);
            }
            if (ev2 != null)
            {
                var entry = CreateEncounterCandidate(ev2, ECardType.EventEncounter, "enc");
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, ev2.Id, ECardType.EventEncounter));
            }

            // 槽位 3（Flex 弹性槽，对齐 BazaarCardDealer FlexEncounterFilter）：
            // 按 Day/Hour 配比优先抽取 Pedestal 基座，其次按 Hero 专属非商人事件 / 中后期稀有三阶（Silver/Gold+）升级事件补全，
            // 始终保持同天去重与全小时严格仅 1 家商人。
            double pedestalChance = effectiveDay switch
            {
                <= 1 => 0.0,
                2 => hour >= 4 ? 0.15 : 0.0,
                <= 6 => hour >= 4 ? 0.40 : 0.30,
                _ => hour >= 4 ? 0.55 : 0.40,
            };
            var rawPedestals = pedestalChance > 0 ? _pool.HourlyEncounters(hero, "PedestalEncounter") : new List<CardPool.CardTemplate>();
            if (pedestalChance > 0 && rawPedestals.Count == 0)
                rawPedestals = _pool.Encounters(hero, "PedestalEncounter");
            var allPedestals = rawPedestals.Where(IsEligibleEncounter).ToList();
            if (pedestalChance > 0)
            {
                TryIncludeDynamicallyUnlockedExclusionEvents(
                    allPedestals,
                    "PedestalEncounter",
                    hero,
                    IsExcludedInRun,
                    IsEligibleEncounter,
                    tierExemptIds);
            }
            var nonZeroPedestals = hasMultipliers
                ? allPedestals.Where(p => GetEncounterMultiplier(p) > 0.0).ToList()
                : allPedestals;

            if (pedestalChance > 0 && nonZeroPedestals.Count > 0)
            {
                double effectivePedChance = pedestalChance;
                if (hasMultipliers)
                {
                    double avgPedMult = nonZeroPedestals.Average(GetEncounterMultiplier);
                    effectivePedChance = Math.Clamp(pedestalChance * avgPedMult, 0.0, 0.90);
                }

                if (_rng.NextDouble() < effectivePedChance)
                {
                    var ped = RollDayTieredEncounterWithDailyDedup(
                        nonZeroPedestals,
                        maxDayTier,
                        pickedIds,
                        _run.VisitedSingleSpawnEncounterIds,
                        dailyVisited,
                        allowExceedDayCap: false,
                        allowDailyFallback: false,
                        weightFn,
                        bannedIds,
                        tierExemptIds);
                    if (ped != null)
                    {
                        var entry = CreateEncounterCandidate(ped, ECardType.PedestalEncounter, "ped");
                        _run.Cards[entry.InstanceId] = entry;
                        _run.SelectionSet.Add(entry.InstanceId);
                        events.Add(new GameSimEventCardDealt(entry.InstanceId, ped.Id, ECardType.PedestalEncounter));
                    }
                }
            }

            if (_run.SelectionSet.Count < 3)
            {
                // 弹性 Flex 优先池：英雄专属非商人事件 + 中后期（Day >= 4 或 Day >= 3 晚间）罕见升级/高阶非商人事件
                var flexPool = BuildFlexNonMerchantPool(effectiveNonMerchantPool, hero, effectiveDay, (int)hour, maxDayTier);
                if (flexPool.Count > 0)
                {
                    double flexChance = effectiveDay >= 4 ? 0.45 : (effectiveDay >= 2 ? 0.30 : 0.25);
                    if (_rng.NextDouble() < flexChance)
                    {
                        var flexEv = RollDayTieredEncounterWithDailyDedup(flexPool, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: false, weightFn, bannedIds, tierExemptIds);
                        if (flexEv != null)
                        {
                            var entry = CreateEncounterCandidate(flexEv, ECardType.EventEncounter, "enc");
                            _run.Cards[entry.InstanceId] = entry;
                            _run.SelectionSet.Add(entry.InstanceId);
                            events.Add(new GameSimEventCardDealt(entry.InstanceId, flexEv.Id, ECardType.EventEncounter));
                        }
                    }
                }
            }

            while (_run.SelectionSet.Count < 3)
            {
                var ev = RollDayTieredEncounterWithDailyDedup(effectiveNonMerchantPool, maxDayTier, pickedIds, _run.VisitedSingleSpawnEncounterIds, dailyVisited, allowExceedDayCap: false, allowDailyFallback: false, weightFn, bannedIds, tierExemptIds)
                      ?? RollDayTieredEncounterWithDailyDedup(effectiveNonMerchantPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: false, allowDailyFallback: true, weightFn, bannedIds, tierExemptIds)
                      ?? RollDayTieredEncounterWithDailyDedup(nonMerchantFallbackPool, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightMultiplier: null, bannedIds, tierExemptIds);
                if (ev == null && nonMerchantFallbackPool.Count == 0)
                {
                    ev = RollDayTieredEncounterWithDailyDedup(allHourlyEvents, maxDayTier, pickedIds, visitedSingleSpawn: null, dailyVisited, allowExceedDayCap: true, allowDailyFallback: true, weightMultiplier: null, bannedIds, tierExemptIds);
                }
                if (ev == null) break;
                var entry = CreateEncounterCandidate(ev, ECardType.EventEncounter, "enc");
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, ev.Id, ECardType.EventEncounter));
            }

            // 第 4 张探险入口卡（Day 6 / 9 / 10 Hour 1）：持有当天对应门票且尚未进入该探险房时，额外下发免费传奇探险入口供自选
            if (hour == 1 &&
                EncounterStepEngine.TryResolveScheduledExpeditionRoomForDay(day, out string? scheduledExpRoomId) &&
                !string.IsNullOrEmpty(scheduledExpRoomId) &&
                !IsBanned(scheduledExpRoomId) &&
                !pickedIds.Contains(scheduledExpRoomId) &&
                !_run.VisitedSingleSpawnEncounterIds.Contains(scheduledExpRoomId) &&
                !(dailyVisited?.Contains(scheduledExpRoomId) ?? false))
            {
                bool hasMatchingTicket = EncounterStepEngine.FindHeldExpeditionTickets(_run)
                    .Any(t => string.Equals(t.RoomEncounterId, scheduledExpRoomId, StringComparison.OrdinalIgnoreCase));
                var expTpl = hasMatchingTicket ? _pool.GetIfAssetValid(scheduledExpRoomId) : null;
                if (expTpl != null)
                {
                    pickedIds.Add(expTpl.Id);
                    var expEntry = CreateEncounterCandidate(expTpl, ECardType.EventEncounter, "exp");
                    expEntry.Attributes[ECardAttributeType.BuyPrice] = 0;
                    _run.Cards[expEntry.InstanceId] = expEntry;
                    _run.SelectionSet.Add(expEntry.InstanceId);
                    events.Add(new GameSimEventCardDealt(expEntry.InstanceId, expTpl.Id, ECardType.EventEncounter));
                }
            }
        }

        events.Add(new GameSimEventStateTransitioned(ERunState.Choice, null));
        return events;
    }

    private void EnsureRunExclusionSetInitialized()
    {
        if (_run.ExclusionSetInitialized) return;
        if (_repo.GameMode?.ExclusionSet != null)
        {
            foreach (string id in _repo.GameMode.ExclusionSet)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    _run.ExclusionSet.Add(id);
            }
        }
        _run.ExclusionSetInitialized = true;
    }

    private void TryIncludeDynamicallyUnlockedExclusionEvents(
        List<CardPool.CardTemplate> targetPool,
        string expectedType,
        string hero,
        Func<string, bool> isExcludedInRun,
        Func<CardPool.CardTemplate, bool> isEligible,
        HashSet<string>? unlockedIds = null)
    {
        var baseExclusions = _repo.GameMode?.ExclusionSet;
        if (baseExclusions == null || baseExclusions.Count == 0) return;

        foreach (string id in baseExclusions)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            // 仅当某模板原本位于 GameMode.ExclusionSet、却在运行期被事件（如 Burn it Down -> Burning Library、
            // Thieves Guild Medallion -> Thieves Guild）移出时才解锁加入，并豁免 maxDayTier 拦截
            if (isExcludedInRun(id)) continue;

            var tpl = _pool.GetIfAssetValid(id);
            if (tpl == null || !_pool.IsPlayerSpawnable(tpl, allowGuidOnly: true)) continue;
            if (!string.Equals(tpl.Type, expectedType, StringComparison.OrdinalIgnoreCase)) continue;
            if (!MatchesHeroOrCommon(tpl, hero)) continue;
            if (tpl.IsSingleSpawn && _run.VisitedSingleSpawnEncounterIds.Contains(tpl.Id)) continue;
            if (!isEligible(tpl)) continue;

            unlockedIds?.Add(tpl.Id);
            if (!targetPool.Any(e => e.Id.Equals(tpl.Id, StringComparison.OrdinalIgnoreCase)))
                targetPool.Add(tpl);
        }
    }

    private static bool MatchesHeroOrCommon(CardPool.CardTemplate tpl, string hero)
    {
        if (tpl.Heroes == null || tpl.Heroes.Length == 0) return true;
        return tpl.Heroes.Any(h =>
            h.Equals(hero, StringComparison.OrdinalIgnoreCase) ||
            h.Equals("Common", StringComparison.OrdinalIgnoreCase));
    }

    private static List<CardPool.CardTemplate> BuildFlexNonMerchantPool(
        IReadOnlyList<CardPool.CardTemplate> nonMerchants,
        string hero,
        int effectiveDay,
        int hour,
        ETier maxDayTier)
    {
        var flex = new List<CardPool.CardTemplate>();
        foreach (var e in nonMerchants)
        {
            bool isHeroExclusive = e.Heroes is { Length: > 0 } &&
                                   e.Heroes.Any(h => h.Equals(hero, StringComparison.OrdinalIgnoreCase)) &&
                                   !e.Heroes.Any(h => h.Equals("Common", StringComparison.OrdinalIgnoreCase));
            var tier = ParseTier(e.StartingTier);
            bool isRareUpgraded = (effectiveDay >= 4 && tier >= ETier.Gold && tier <= maxDayTier) ||
                                  (effectiveDay == 3 && hour >= 4 && tier >= ETier.Silver && tier <= maxDayTier);
            if (isHeroExclusive || isRareUpgraded)
                flex.Add(e);
        }
        return flex;
    }

    private void TryIncludeGuidOnlyEvent(
        List<CardPool.CardTemplate> targetPool,
        string templateId,
        Func<CardPool.CardTemplate, bool> isEligible)
    {
        if (targetPool.Any(e => e.Id.Equals(templateId, StringComparison.OrdinalIgnoreCase))) return;

        var tpl = _pool.GetIfAssetValid(templateId);
        if (tpl == null || !_pool.IsPlayerSpawnable(tpl, allowGuidOnly: true)) return;
        if (tpl.IsSingleSpawn && _run.VisitedSingleSpawnEncounterIds.Contains(tpl.Id)) return;
        if (!isEligible(tpl)) return;

        targetPool.Add(tpl);
    }

    private void TryIncludeAmplifiedGuidOnlyEvent(
        List<CardPool.CardTemplate> targetPool,
        string templateId,
        double multiplier,
        ETier maxDayTier,
        Func<CardPool.CardTemplate, bool> isEligible)
    {
        if (multiplier <= 1.0) return;

        var tpl = _pool.GetIfAssetValid(templateId);
        if (tpl == null) return;
        // 绝不在有效天数解锁门槛前放行高阶 GuidOnly 奇遇（如 Diamond 阶需 Day >= 7）
        if (ParseTier(tpl.StartingTier) > maxDayTier) return;
        TryIncludeGuidOnlyEvent(targetPool, templateId, isEligible);
    }

    private CardPool.CardTemplate? RollDayTieredEncounterWithDailyDedup(
        IReadOnlyList<CardPool.CardTemplate> pool,
        ETier maxDayTier,
        HashSet<string> pickedIds,
        HashSet<string>? visitedSingleSpawn,
        HashSet<string>? dailyVisited,
        bool allowExceedDayCap = false,
        bool allowDailyFallback = true,
        Func<CardPool.CardTemplate, double>? weightMultiplier = null,
        ISet<string>? bannedIds = null,
        ISet<string>? tierExemptIds = null)
    {
        if (pool.Count == 0) return null;

        if (dailyVisited is { Count: > 0 })
        {
            var unvisited = pool.Where(c => !dailyVisited.Contains(c.Id)).ToList();
            if (unvisited.Count > 0)
            {
                var pick = RollDayTieredEncounter(unvisited, maxDayTier, pickedIds, visitedSingleSpawn, allowExceedDayCap: false, weightMultiplier, bannedIds, tierExemptIds);
                if (pick != null) return pick;
            }
            if (!allowDailyFallback) return null;
        }

        return RollDayTieredEncounter(pool, maxDayTier, pickedIds, visitedSingleSpawn, allowExceedDayCap, weightMultiplier, bannedIds, tierExemptIds);
    }

    /// <summary>在 ChoiceState 下发指定的 Day-start Expedition 房间候选项（供持票进入探索时选择）。</summary>
    public List<IGameSimEvent> DealExpeditionRoomChoices(IReadOnlyList<string> roomEncounterIds)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());
        CurrentMaxSelections = 1;

        _run.State = ERunState.Choice;
        _run.CurrentEncounterId = null;
        _run.RerollCost = 0;
        _run.RerollsRemaining = 0;
        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = false,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string roomId in roomEncounterIds)
        {
            if (string.IsNullOrWhiteSpace(roomId) || !seen.Add(roomId)) continue;
            var tpl = _pool.GetIfAssetValid(roomId);
            if (tpl == null) continue;
            var entry = CreateEncounterCandidate(tpl, ECardType.EventEncounter, "exp");
            // 持有门票进入房间时置零费用，绕过 DB 中默认占位高价 BuyPrice=9999999
            entry.Attributes[ECardAttributeType.BuyPrice] = 0;
            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            events.Add(new GameSimEventCardDealt(entry.InstanceId, tpl.Id, ECardType.EventEncounter));
        }

        events.Add(new GameSimEventStateTransitioned(ERunState.Choice, null));
        return events;
    }

    private static ETier GetMaxEncounterTierForDay(int day) => day switch
    {
        <= 1 => ETier.Bronze,
        <= 3 => ETier.Silver,
        <= 6 => ETier.Gold,
        _ => ETier.Legendary,
    };

    private CardPool.CardTemplate? RollDayTieredEncounter(
        IReadOnlyList<CardPool.CardTemplate> pool,
        ETier maxDayTier,
        HashSet<string> pickedIds,
        HashSet<string>? visitedSingleSpawn,
        bool allowExceedDayCap = false,
        Func<CardPool.CardTemplate, double>? weightMultiplier = null,
        ISet<string>? bannedIds = null,
        ISet<string>? tierExemptIds = null)
    {
        if (pool.Count == 0) return null;

        var unpicked = pool
            .Where(c => !pickedIds.Contains(c.Id) &&
                        (bannedIds == null || !bannedIds.Contains(c.Id)) &&
                        (visitedSingleSpawn == null || !visitedSingleSpawn.Contains(c.Id)) &&
                        (weightMultiplier == null || weightMultiplier(c) > 0.0))
            .ToList();
        if (unpicked.Count == 0) return null;

        // 严格只从不高于当日阶级上限（或已豁免天数阶级拦截的动态解锁/TravelAgent）的未抽选候选中抽取；
        // 仅当整池符合上限的候选全部被抽光且显式允许兜底（如极简测试 DB 仅有 2 张青铜遭遇）时，才按最低可用阶补足 3 选空位。
        var available = unpicked
            .Where(e => ParseTier(e.StartingTier) <= maxDayTier ||
                        (tierExemptIds != null && tierExemptIds.Contains(e.Id)))
            .ToList();
        if (available.Count == 0)
        {
            if (!allowExceedDayCap) return null;
            ETier minRemainingTier = unpicked.Min(e => ParseTier(e.StartingTier));
            available = unpicked.Where(e => ParseTier(e.StartingTier) == minRemainingTier).ToList();
        }

        var byTier = available.GroupBy(c => ParseTier(c.StartingTier))
                              .ToDictionary(g => g.Key, g => (IReadOnlyList<CardPool.CardTemplate>)g.ToList());

        var pct = _repo.GameMode.EncounterSpawnTierPercentages;
        var weightedTiers = new List<(ETier Tier, double Weight)>();
        foreach (var (tier, tierCards) in byTier)
        {
            double basePct = (pct.TryGetValue(tier.ToString(), out double raw) && raw > 0) ? raw : 0.05;
            double tierWeight = basePct;
            if (weightMultiplier != null)
            {
                double sumBase = 0.0;
                double sumWeighted = 0.0;
                foreach (var c in tierCards)
                {
                    double baseW = c.SpawningChance > 0 ? c.SpawningChance : 1.0;
                    double mult = Math.Max(0.0, weightMultiplier(c));
                    sumBase += baseW;
                    sumWeighted += baseW * mult;
                }
                tierWeight = sumBase > 0.0 ? basePct * (sumWeighted / sumBase) : basePct;
            }
            if (tierWeight > 0.0)
                weightedTiers.Add((tier, tierWeight));
        }

        double total = weightedTiers.Sum(t => t.Weight);
        if (total > 0)
        {
            double roll = _rng.NextDouble() * total;
            double acc = 0;
            ETier chosenTier = weightedTiers[0].Tier;
            foreach (var (tier, w) in weightedTiers)
            {
                acc += w;
                if (roll < acc)
                {
                    chosenTier = tier;
                    break;
                }
            }

            if (byTier.TryGetValue(chosenTier, out var tierList))
            {
                var pick = _pool.RollWeightedEncounter(tierList, pickedIds, visitedSingleSpawn, _rng, weightMultiplier, bannedIds);
                if (pick != null) return pick;
            }
        }

        return _pool.RollWeightedEncounter(available, pickedIds, visitedSingleSpawn, _rng, weightMultiplier, bannedIds);
    }

    /// <summary>
    /// 进入商人或事件遭遇后（ERunState.Encounter），严格按该遭遇的 SelectionContext（SpawnContext + Rules）
    /// 与英雄/Day/Hour/当前盘面条件抽取真实选项，并完整映射 SelectionContextRules。
    /// 彻底移除给无商品纯随机奇遇塞 3 件普通商店物品的兜底逻辑（仅当确为商店模板且池空时回退同英雄商店池）。
    /// </summary>
    public List<IGameSimEvent> DealEncounterShelf(string encounterTemplateId, bool isReroll = false)
        => DealEncounterShelf(encounterTemplateId, isReroll, playerState: null, ownedMaxTiers: null);

    public List<IGameSimEvent> DealEncounterShelf(
        string encounterTemplateId,
        bool isReroll,
        PlayerState? playerState,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers = null)
        => DealEncounterShelf(encounterTemplateId, isReroll, playerState, ownedMaxTiers, ActiveRules, ArchetypeIndex);

    public List<IGameSimEvent> DealEncounterShelf(
        string encounterTemplateId,
        bool isReroll,
        PlayerState? playerState,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers,
        CustomRunRuleSet? customRules,
        CardArchetypeIndex? archetypeIndex = null,
        bool applySyntheticDiscounts = true)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());

        var coreTpl = _repo.GetCard(encounterTemplateId);
        var poolTpl = _pool.Get(encounterTemplateId);
        bool isMerchant = IsMerchantEncounter(poolTpl, coreTpl);

        var rules = coreTpl?.SelectionContext?.Rules ?? poolTpl?.SelectionContext?.Rules;
        ApplySelectionContextRules(rules, isMerchant, isReroll);

        _run.State = ERunState.Encounter;
        _run.CurrentEncounterId = encounterTemplateId;

        int day = (int)_run.Day;
        _chances.CurrentDay = day;
        string hero = _run.Hero.ToString();

        var effectiveOwnedMaxTiers = ownedMaxTiers != null
            ? new Dictionary<string, CardTier>(ownedMaxTiers, StringComparer.OrdinalIgnoreCase)
            : (isMerchant ? BuildOwnedMaxTiers(_run, playerState) : null);
        if (ownedMaxTiers == null && isMerchant && effectiveOwnedMaxTiers != null)
        {
            foreach (var (skId, skTier) in BuildOwnedSkillMaxTiers(playerState))
            {
                var coreSkTier = ToCoreTier(skTier);
                if (!effectiveOwnedMaxTiers.TryGetValue(skId, out var curTier) || coreSkTier > curTier)
                    effectiveOwnedMaxTiers[skId] = coreSkTier;
            }
        }
        IReadOnlySet<string>? validAssetIds = _pool.WhitelistEnabled ? _pool.ValidCardIds : null;
        var effectiveCustomRules = customRules ?? ActiveRules;
        var archetypeResolver = ResolveArchetypeResolver(effectiveCustomRules, archetypeIndex);

        var dealtList = new List<RunState.CardEntry>();
        var usedTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isPaidContext = !_run.SelectionRules.SelectionIsFree;
        int usedWidth = 0;

        // 1. 按 EventEncounter 的 SelectionRequirements 与 SelectionContext.SpawnContext 抽取真实选项
        var queryPlayer = playerState ?? BuildDummyQueryPlayer(hero, day);
        var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, _rng)
        {
            CurrentDay = day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = encounterTemplateId,
            PlayMode = _run.PlayMode.ToString(),
            SelfCard = coreTpl != null ? new CardInstance(coreTpl) : null,
        };

        var spawnContext = coreTpl?.SelectionContext?.SpawnContext ?? poolTpl?.SelectionContext?.SpawnContext;
        bool allowDuplicates = (_run.SelectionRules.SelectionIsFree && _run.SelectionRules.CanSelectMultiple)
                            || HasAllowDuplicatesBehavior(spawnContext);
        bool allowPlayerDuplicates = HasAllowPlayerDuplicatesBehavior(spawnContext);

        if (spawnContext != null && CheckEncounterRequirements(poolTpl, coreTpl, ctx))
        {
            if (string.Equals(spawnContext.Type, "TSpawnContextMirror", StringComparison.OrdinalIgnoreCase))
            {
                int limit = (int)Math.Round(ValueAndConditionEvaluator.EvaluateValue(spawnContext.Limit, ctx, 3));
                if (limit <= 0) limit = 3;
                var ownedItems = _run.Cards.Values
                    .Where(c => c.Owner == ECombatantId.Player &&
                                c.Type == ECardType.Item &&
                                _pool.IsAssetValid(c.TemplateId.ToString()))
                    .OrderBy(_ => _rng.Next())
                    .ToList();
                foreach (var src in ownedItems)
                {
                    if (dealtList.Count >= limit) break;
                    var srcCore = _repo.GetCard(src.TemplateId.ToString());
                    if (srcCore == null) continue;
                    var copyInst = new CardInstance(srcCore, ToCoreTier(src.Tier), src.Enchantment?.ToString());
                    int slotWidth = GetShelfSlotWidth(copyInst);
                    if (usedWidth + slotWidth > MaxShelfSlotWidth) continue;
                    var entry = CreateEntryFromCoreInstance(copyInst, _run.SelectionRules.SelectionIsFree);
                    if (isPaidContext && entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) <= 0)
                        continue;
                    dealtList.Add(entry);
                    usedWidth += slotWidth;
                }
            }
            else
            {
                // 尊重模板自身 SpawnContext.Limit（如 Limit=1/2/3/4/5/7/8）；
                // 当 Sequential 多分组含带前置条件解锁的特殊选项且总宽度超过 MaxShelfSlotWidth(10) 时，
                // 优先保留命中非空 Prerequisites 条件分组解锁的特殊选项，再补齐常规选项至 10 格满。
                var prioritizedSpawnCtx = PrioritizeSequentialConditionalGroups(spawnContext, ctx);
                var normalizedCtx = NormalizeSequentialSingleGroupQuery(prioritizedSpawnCtx);
                var spawned = SpawnQueryEngine.SpawnCards(
                    normalizedCtx,
                    ctx,
                    defaultCountOverride: null,
                    ownedMaxTiers: effectiveOwnedMaxTiers,
                    validAssetIds: validAssetIds,
                    archetypeResolver,
                    effectiveCustomRules);
                foreach (var inst in spawned)
                {
                    var itemPoolTpl = _pool.Get(inst.Template.Id);
                    if (!_pool.IsPlayerSpawnableId(inst.Template.Id, allowGuidOnly: true)) continue;
                    // 对子遭遇/步骤选项（如 EncounterStep）在发牌阶段即校验天数/局外 Run 前置条件
                    if (inst.Template.Type is "EncounterStep" or "EventEncounter" or "PedestalEncounter" or "CombatEncounter" &&
                        !CheckChildOptionRunPrerequisites(itemPoolTpl, inst.Template, ctx))
                    {
                        continue;
                    }
                    if (isPaidContext && inst.Template.Type is "Item" or "Skill" &&
                        IsZeroBuyPriceOrFreePackage(itemPoolTpl, inst.Template, inst.Tier.ToString()))
                    {
                        continue;
                    }
                    if (isMerchant && !allowPlayerDuplicates &&
                        inst.Template.Type is "Item" or "Skill" &&
                        IsBlockedByOwnedMaxTiers(inst.Template.Id, inst.Tier, effectiveOwnedMaxTiers))
                    {
                        continue;
                    }
                    if (!allowDuplicates && !usedTemplateIds.Add(inst.Template.Id)) continue;

                    int slotWidth = GetShelfSlotWidth(inst);
                    if (usedWidth + slotWidth > MaxShelfSlotWidth) continue;

                    var entry = CreateEntryFromCoreInstance(inst, _run.SelectionRules.SelectionIsFree);
                    if (isPaidContext && entry.Type is ECardType.Item or ECardType.Skill &&
                        entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) <= 0)
                    {
                        continue;
                    }
                    dealtList.Add(entry);
                    usedWidth += slotWidth;
                }
            }
        }

        // 2. 仅当确为商店模板且池空时回退同英雄商店池（绝不再向非商店随机奇遇塞入普通商店物品！）
        if (dealtList.Count == 0 && isMerchant)
        {
            var fallback = _pool.RollShopItems(hero, day, 3, _chances, effectiveOwnedMaxTiers, archetypeResolver, effectiveCustomRules);
            foreach (var (tpl, tierStr) in fallback)
            {
                if (isPaidContext && IsZeroBuyPriceOrFreePackage(tpl, _repo.GetCard(tpl.Id), tierStr))
                    continue;
                if (!usedTemplateIds.Add(tpl.Id)) continue;
                int slotWidth = Math.Clamp((int)ParseSize(tpl.Size), 1, 3);
                if (usedWidth + slotWidth > MaxShelfSlotWidth) continue;
                var entry = CreateItemCandidate(tpl, ParseTier(tierStr), isFree: _run.SelectionRules.SelectionIsFree);
                if (isPaidContext && entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) <= 0)
                    continue;
                dealtList.Add(entry);
                usedWidth += slotWidth;
            }
        }

        // 3. 非免费商人单品 25%-off 绿标折扣掷骰（Reroll 时随 ClearShelf 重新独立掷骰；若交由 DB TTriggerOnEncounterCardsDealt 负责折扣则不叠加掷骰）
        if (applySyntheticDiscounts && isMerchant && !_run.SelectionRules.SelectionIsFree && dealtList.Count > 0)
        {
            ApplyMerchantDiscounts(dealtList);
        }

        // 4. 免费发牌奇遇若因池空未产出任何卡，给予安全金币补偿并确保 CanExit=true 防止软锁
        if (!isMerchant && dealtList.Count == 0 && spawnContext != null && _run.SelectionRules.SelectionIsFree)
        {
            _run.AddPlayerGold(EmptyFreeDealFallbackGold);
            if (!_run.SelectionRules.CanExit)
            {
                var unlockedRules = RunState.CloneRules(_run.SelectionRules);
                unlockedRules.CanExit = true;
                _run.SelectionRules = unlockedRules;
            }
        }
        else if (dealtList.Count == 0 && !_run.SelectionRules.CanExit)
        {
            var unlockedRules = RunState.CloneRules(_run.SelectionRules);
            unlockedRules.CanExit = true;
            _run.SelectionRules = unlockedRules;
        }

        foreach (var entry in dealtList)
        {
            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            events.Add(new GameSimEventCardDealt(entry.InstanceId, entry.TemplateId.ToString(), entry.Type));
        }

        // #5 & #18：免费多选遭遇未显式声明 MaxSelections 时，按本次实际发出的卡牌数量锁定上限，避免拿 1 件就关或拿满未自动结束
        if (!isMerchant &&
            _run.SelectionRules.CanSelectMultiple &&
            _run.SelectionRules.SelectionIsFree &&
            CurrentMaxSelections == null &&
            dealtList.Count > 0)
        {
            CurrentMaxSelections = dealtList.Count;
        }

        if (!isReroll)
        {
            events.Add(new GameSimEventStateTransitioned(ERunState.Encounter, encounterTemplateId));
        }
        return events;
    }

    /// <summary>从 RunState 与可选 PlayerState 汇总玩家当前已持有物品与技能的最高品质（供商店过滤低阶与满阶重复卡使用）。</summary>
    public static Dictionary<string, CardTier> BuildOwnedMaxTiers(RunState run, PlayerState? playerState = null)
    {
        var map = new Dictionary<string, CardTier>(StringComparer.OrdinalIgnoreCase);

        void Record(string? templateId, CardTier tier)
        {
            if (string.IsNullOrWhiteSpace(templateId)) return;
            if (!map.TryGetValue(templateId, out var existing) || tier > existing)
                map[templateId] = tier;
        }

        foreach (var entry in run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player || entry.Type is not (ECardType.Item or ECardType.Skill)) continue;
            if (entry.CoreCard is { IsDestroyed: true }) continue;
            Record(entry.TemplateId.ToString(), ToCoreTier(entry.Tier));
        }

        if (playerState != null)
        {
            foreach (var c in playerState.EnumerateAllOwnedCards())
            {
                if (c.IsDestroyed || c.Template.Type is not ("Item" or "Skill")) continue;
                Record(c.Template.Id, c.Tier);
            }
        }

        return map;
    }

    internal void ClearShelfDiscount(string instanceId)
    {
        if (!string.IsNullOrEmpty(instanceId))
            _activeShelfDiscounts.Remove(instanceId);
    }

    internal void RegisterExternalShelfDiscount(RunState.CardEntry entry, int originalBuyPrice, int discountedBuyPrice)
    {
        if (entry.Type != ECardType.Item || originalBuyPrice <= 0 || discountedBuyPrice <= 0 || discountedBuyPrice >= originalBuyPrice)
            return;

        int discountPct = Math.Clamp((int)Math.Round((1.0 - (double)discountedBuyPrice / originalBuyPrice) * 100.0), 1, 99);
        double mult = Math.Clamp((double)discountedBuyPrice / originalBuyPrice, 0.01, 1.0);
        _activeShelfDiscounts[entry.InstanceId] = new MerchantDiscountInfo(
            entry.InstanceId,
            entry.TemplateId.ToString(),
            originalBuyPrice,
            discountedBuyPrice,
            discountPct,
            mult);
    }

    internal Dictionary<string, ETier> BuildOwnedSkillMaxTiers(PlayerState? playerState = null)
    {
        var map = new Dictionary<string, ETier>(StringComparer.OrdinalIgnoreCase);

        void Record(string? templateId, ETier tier)
        {
            if (string.IsNullOrWhiteSpace(templateId)) return;
            if (!map.TryGetValue(templateId, out var existing) || tier > existing)
                map[templateId] = tier;
        }

        foreach (var entry in _run.Cards.Values)
        {
            if (entry.Owner != ECombatantId.Player || entry.Type != ECardType.Skill) continue;
            if (entry.CoreCard is { IsDestroyed: true }) continue;
            Record(entry.TemplateId.ToString(), entry.Tier);
        }

        if (playerState != null)
        {
            foreach (var c in playerState.Skills)
            {
                if (c.IsDestroyed) continue;
                Record(c.Template.Id, ParseTier(c.Tier.ToString()));
            }
        }

        return map;
    }

    internal static bool CanOfferOwnedSkill(string templateId, ETier offeredTier, IReadOnlyDictionary<string, ETier>? ownedSkills)
    {
        if (ownedSkills == null || !ownedSkills.TryGetValue(templateId, out var ownedTier))
            return true;
        if (ownedTier >= ETier.Diamond)
            return false;
        return offeredTier >= ownedTier;
    }

    /// <summary>判断候选物品/技能是否应因玩家已拥有满阶（Diamond/Legendary）或更高品质而被商店过滤。</summary>
    public static bool IsBlockedByOwnedMaxTiers(
        string? templateId,
        CardTier candidateTier,
        IReadOnlyDictionary<string, CardTier>? ownedMaxTiers)
    {
        if (ownedMaxTiers == null || string.IsNullOrWhiteSpace(templateId)) return false;
        if (!ownedMaxTiers.TryGetValue(templateId, out var ownedTier)) return false;
        if (ownedTier >= CardTier.Diamond) return true;
        return ownedTier > candidateTier;
    }

    /// <summary>计算单件商品享受 25%-off 折扣后的购买价格。</summary>
    public static int ComputeDiscountedBuyPrice(int basePrice, int discountPercent = MerchantItemDiscountPercent)
    {
        if (basePrice <= 0) return 0;
        double multiplier = Math.Clamp(1.0 - discountPercent / 100.0, 0.0, 1.0);
        return Math.Max(1, (int)Math.Round(basePrice * multiplier));
    }

    /// <summary>对非免费商人货架物品逐一按 MerchantItemDiscountChance (12%) 掷骰 25%-off 绿标折扣。</summary>
    public List<MerchantDiscountInfo> ApplyMerchantDiscounts(IEnumerable<RunState.CardEntry> shelfEntries)
    {
        var applied = new List<MerchantDiscountInfo>();
        foreach (var entry in shelfEntries)
        {
            if (entry.Type != ECardType.Item) continue;
            int basePrice = entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
            if (basePrice <= 0) continue;
            if (_rng.NextDouble() >= MerchantItemDiscountChance) continue;

            var info = ApplyMerchantItemDiscount(entry, MerchantItemDiscountPercent);
            if (info != null)
                applied.Add(info);
        }
        return applied;
    }

    /// <summary>对单个货架物品应用指定百分比折扣（更新 BuyPrice、CoreCard 价格修饰与 ActiveShelfDiscounts 标记）。</summary>
    public MerchantDiscountInfo? ApplyMerchantItemDiscount(RunState.CardEntry entry, int discountPercent = MerchantItemDiscountPercent)
    {
        if (entry.Type != ECardType.Item) return null;
        int basePrice = entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
        if (basePrice <= 0) return null;

        double multiplier = Math.Clamp(1.0 - discountPercent / 100.0, 0.0, 1.0);
        int discountedPrice = ComputeDiscountedBuyPrice(basePrice, discountPercent);

        entry.Attributes[ECardAttributeType.BuyPrice] = discountedPrice;
        if (entry.CoreCard != null)
        {
            // 仅写入 HourMult 折扣乘区，避免向未显式配置正基价的卡重复写入 HourAdd["BuyPrice"] 导致同步重算时价格翻倍
            entry.CoreCard.HourMult["BuyPrice"] = multiplier;
        }

        var info = new MerchantDiscountInfo(
            entry.InstanceId,
            entry.TemplateId.ToString(),
            basePrice,
            discountedPrice,
            discountPercent,
            multiplier);
        _activeShelfDiscounts[entry.InstanceId] = info;
        return info;
    }

    /// <summary>将 DB RerollRulesData 规范化为权威 RerollRules 与初始费用/次数。</summary>
    public static (RerollRules? Rules, uint StartingCost, uint RerollsRemaining) ResolveRerollSettings(RerollRulesData? rrData)
    {
        if (rrData == null)
            return (null, 0, 0);

        int? allowed = rrData.GetEffectiveAllowedRerolls();
        int startingCost = rrData.GetEffectiveStartingCost();
        int costIncrease = rrData.GetEffectiveCostIncrease();

        var mapped = new RerollRules
        {
            TotalAllowedRerolls = allowed,
            CostIncrease = costIncrease,
            StartingCost = startingCost,
            CostMax = rrData.CostMax,
        };
        uint rerollsRemaining = allowed.HasValue ? (uint)allowed.Value : UnlimitedRerollsCount;
        return (mapped, (uint)startingCost, rerollsRemaining);
    }

    /// <summary>
    /// 完整映射 SelectionContextRules 到 RunState.SelectionRules，并同步 RerollCost / RerollsRemaining 与 MaxSelections。
    /// </summary>
    public void ApplySelectionContextRules(
        SelectionRulesData? rulesData,
        bool isMerchantFallback = true,
        bool isReroll = false)
    {
        var (mappedReroll, resolvedStartingCost, resolvedRerollsRemaining) = ResolveRerollSettings(rulesData?.RerollRules);

        Guid? nextOnExit = null;
        if (rulesData?.NextEncounterOnExit is JsonElement nextEl &&
            nextEl.ValueKind == JsonValueKind.String &&
            Guid.TryParse(nextEl.GetString(), out var parsedNext))
        {
            nextOnExit = parsedNext;
        }

        if (rulesData != null)
        {
            int? maxSelections = rulesData.MaxSelections.HasValue
                ? Math.Max(1, rulesData.MaxSelections.Value)
                : null;
            bool canSelectMultiple = rulesData.CanSelectMultiple;
            if (maxSelections is int maxSel)
                canSelectMultiple = maxSel > 1;
            else if (!isMerchantFallback && !canSelectMultiple)
                maxSelections = 1;

            CurrentMaxSelections = maxSelections;

            _run.SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = canSelectMultiple,
                SelectionIsFree = rulesData.SelectionIsFree,
                CanExit = rulesData.CanExit,
                RerollRules = mappedReroll,
                WillAutoSellOnExit = rulesData.WillAutoSellOnExit,
                NextEncounterOnExit = nextOnExit,
            };
        }
        else
        {
            CurrentMaxSelections = isMerchantFallback ? null : 1;
            _run.SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = isMerchantFallback,
                SelectionIsFree = !isMerchantFallback,
                CanExit = true,
            };
        }

        if (!isReroll)
        {
            if (rulesData != null)
            {
                if (rulesData.RerollRules != null)
                {
                    _run.RerollCost = resolvedStartingCost;
                    _run.RerollsRemaining = resolvedRerollsRemaining;
                }
                else
                {
                    _run.RerollCost = 0;
                    _run.RerollsRemaining = 0;
                }
            }
            else if (isMerchantFallback)
            {
                _run.RerollCost = 2;
                _run.RerollsRemaining = 3;
            }
            else
            {
                _run.RerollCost = 0;
                _run.RerollsRemaining = 0;
            }
        }
    }

    /// <summary>同步更新当前商店生效的流派倾向位掩码（仅用于开场一次确认锁定，不改变当前货架已生成的卡）。</summary>
    public void UpdateActiveArchetypeMask(ulong normalizedMask)
    {
        ActiveRules ??= new CustomRunRuleSet();
        ActiveRules.SelectedArchetypeMask = normalizedMask;
    }

    /// <summary>向后兼容旧签名：委托至 DealMonsterVictoryLoot，保留野怪专属物品与技能掉落。</summary>
    public List<IGameSimEvent> DealMonsterLoot(BazaarServer.Data.MonsterTemplate? monster)
    {
        var encTpl = !string.IsNullOrEmpty(_run.CurrentEncounterId) ? _pool.Get(_run.CurrentEncounterId) : null;
        return DealMonsterVictoryLoot(encTpl, monster, p1Core: null);
    }

    /// <summary>
    /// 战斗胜利后按官方完整 RewardVictory 逻辑发放战利品：
    ///   · 默认（<c>chooseOne = false</c>）：保留官方随机 1 掉落（优先 RewardVictory.SpawnContext，次选 AllLootCandidates 随机 1 项，保底商店 1 项）；
    ///   · 开启分页自选（<c>chooseOne = true</c>）：委托至 <see cref="PreparePaginatedMonsterLoot"/> 分页展示全部去重战利品并免费 1 选 1。
    /// </summary>
    public List<IGameSimEvent> DealMonsterVictoryLoot(
        CardPool.CardTemplate? combatEncounterTpl,
        BazaarServer.Data.MonsterTemplate? monster,
        PlayerState? p1Core = null)
        => DealMonsterVictoryLoot(combatEncounterTpl, monster, p1Core, chooseOne: ActiveRules?.MonsterLootChooseOne ?? true);

    public List<IGameSimEvent> DealMonsterVictoryLoot(
        CardPool.CardTemplate? combatEncounterTpl,
        BazaarServer.Data.MonsterTemplate? monster,
        PlayerState? p1Core,
        bool chooseOne)
    {
        if (chooseOne)
            return PreparePaginatedMonsterLoot(monster, combatEncounterTpl, p1Core);

        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());

        _run.State = ERunState.Loot;

        var coreCombatTpl = combatEncounterTpl != null ? _repo.GetCard(combatEncounterTpl.Id) : null;
        var rewardVictory = coreCombatTpl?.RewardVictory ?? combatEncounterTpl?.RewardVictory;
        var rules = rewardVictory?.SelectionContextRules;

        ApplySelectionContextRules(
            rules ?? new SelectionRulesData
            {
                CanSelectMultiple = true,
                SelectionIsFree = true,
                CanExit = true,
            },
            isMerchantFallback: false,
            isReroll: false);

        var dealtList = new List<RunState.CardEntry>();
        var usedTemplateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownedSkills = BuildOwnedSkillMaxTiers(p1Core);
        bool allowDuplicates = (_run.SelectionRules.SelectionIsFree && _run.SelectionRules.CanSelectMultiple)
                            || HasAllowDuplicatesBehavior(rewardVictory?.SpawnContext);
        int usedWidth = 0;

        // 1. 若 RewardVictory.SpawnContext 存在，先使用 SpawnQueryEngine 结合野怪上下文候选池执行
        if (rewardVictory?.SpawnContext != null)
        {
            var queryPlayer = p1Core ?? BuildDummyQueryPlayer(_run.Hero.ToString(), (int)_run.Day);
            var monsterState = BuildMonsterLootPlayerState(monster);
            var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, queryPlayer, monsterState, _rng)
            {
                CurrentDay = (int)_run.Day,
                CurrentHour = (int)_run.Hour,
                CurrentEncounterId = combatEncounterTpl?.Id ?? _run.CurrentEncounterId,
                SelfCard = coreCombatTpl != null ? new CardInstance(coreCombatTpl) : null,
            };

            var spawned = SpawnQueryEngine.SpawnCards(NormalizeSequentialSingleGroupQuery(rewardVictory.SpawnContext), ctx, defaultCountOverride: null);
            foreach (var inst in spawned)
            {
                if (!_pool.IsPlayerSpawnableId(inst.Template.Id, allowGuidOnly: true)) continue;
                if (string.Equals(inst.Template.Type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                    !CanOfferOwnedSkill(inst.Template.Id, ParseTier(inst.Tier.ToString()), ownedSkills))
                    continue;
                if (!allowDuplicates && !usedTemplateIds.Add(inst.Template.Id)) continue;
                int slotWidth = GetShelfSlotWidth(inst);
                if (usedWidth + slotWidth > MaxShelfSlotWidth) continue;
                dealtList.Add(CreateEntryFromCoreInstance(inst, _run.SelectionRules.SelectionIsFree));
                usedWidth += slotWidth;
            }
        }

        // 2. 从野怪完整战利品池（专属装备 + 专属圆形 Skill + 背包消耗品）抽取
        if (dealtList.Count == 0 && monster != null)
        {
            var monsterCandidates = new List<(CardPool.CardTemplate Tpl, ETier Tier, string? Enchantment)>();
            foreach (var cand in monster.AllLootCandidates())
            {
                var tpl = _pool.GetIfAssetValid(cand.TemplateId);
                if (tpl == null || !_pool.IsPlayerSpawnable(tpl, allowGuidOnly: true)) continue;
                if (tpl.Type != "Item" && tpl.Type != "Skill") continue;
                var tier = ParseTier(!string.IsNullOrEmpty(cand.Tier) ? cand.Tier : tpl.StartingTier);
                if (tpl.Type == "Skill" && !CanOfferOwnedSkill(tpl.Id, tier, ownedSkills)) continue;
                monsterCandidates.Add((tpl, tier, cand.EnchantmentType));
            }

            if (monsterCandidates.Count > 0)
            {
                var pick = monsterCandidates[_rng.Next(monsterCandidates.Count)];
                RunState.CardEntry entry;
                if (pick.Tpl.Type == "Skill")
                {
                    entry = CreateSkillCandidate(pick.Tpl, pick.Tier, isFree: _run.SelectionRules.SelectionIsFree);
                }
                else
                {
                    entry = CreateItemCandidate(pick.Tpl, pick.Tier, isFree: _run.SelectionRules.SelectionIsFree);
                    ApplyOptionalEnchantment(entry, pick.Enchantment);
                }
                dealtList.Add(entry);
            }
        }

        // 3. 极少数无怪物且无 SpawnContext 的降级情形，按天品质表兜底 1 张物品
        if (dealtList.Count == 0)
        {
            var fallback = _pool.RollShopItems(_run.Hero.ToString(), (int)_run.Day, 1, _chances);
            foreach (var (tpl, tierStr) in fallback)
            {
                dealtList.Add(CreateItemCandidate(tpl, ParseTier(tierStr), isFree: _run.SelectionRules.SelectionIsFree));
            }
        }

        foreach (var entry in dealtList)
        {
            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            events.Add(new GameSimEventCardDealt(entry.InstanceId, entry.TemplateId.ToString(), entry.Type));
        }

        if (_run.SelectionRules.CanSelectMultiple &&
            _run.SelectionRules.SelectionIsFree &&
            CurrentMaxSelections == null &&
            dealtList.Count > 0)
        {
            CurrentMaxSelections = dealtList.Count;
        }

        events.Add(new GameSimEventStateTransitioned(ERunState.Loot, _run.CurrentEncounterId));
        return events;
    }

    /// <summary>
    /// 启用怪物战利品分页自选模式：收集 <paramref name="monster"/> 全部去重且合法的
    /// 物品/技能/背包战利品，按每页 ≤10 地毯格（Small=1, Medium=2, Large=3, Skill=2）确定性分页，
    /// 立即发第 0 页并设置 <c>MaxSelections = 1</c>（多页时设 <c>RerollCost = 0</c> 与 <c>RerollsRemaining = 999</c>）。
    /// </summary>
    public List<IGameSimEvent> PreparePaginatedMonsterLoot(
        BazaarServer.Data.MonsterTemplate? monster,
        CardPool.CardTemplate? combatEncounterTpl = null,
        PlayerState? p1Core = null)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());

        _run.State = ERunState.Loot;
        if (combatEncounterTpl != null && string.IsNullOrEmpty(_run.CurrentEncounterId))
            _run.CurrentEncounterId = combatEncounterTpl.Id;

        var candidates = CollectPaginatedMonsterLootCandidates(monster, p1Core);
        if (candidates.Count == 0)
        {
            var fallback = _pool.RollShopItems(_run.Hero.ToString(), (int)_run.Day, 1, _chances);
            foreach (var (tpl, tierStr) in fallback)
            {
                candidates.Add(new MonsterLootCandidate(
                    tpl,
                    ParseTier(tierStr),
                    null,
                    GetMonsterLootSlotCost(tpl)));
            }
        }

        PackMonsterLootPages(candidates, _monsterLootPages);
        CurrentMonsterLootPageIndex = 0;
        SyncMonsterLootPagesToRun();

        bool hasMultiplePages = _monsterLootPages.Count > 1;
        CurrentMaxSelections = 1;
        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = true,
            RerollRules = hasMultiplePages
                ? new RerollRules
                {
                    TotalAllowedRerolls = (int)UnlimitedRerollsCount,
                    StartingCost = 0,
                    CostIncrease = 0,
                    CostMax = 0,
                }
                : null,
        };
        _run.RerollCost = 0;
        _run.RerollsRemaining = hasMultiplePages ? UnlimitedRerollsCount : 0;

        if (_monsterLootPages.Count > 0)
            DealMonsterLootPage(0, events);

        events.Add(new GameSimEventStateTransitioned(ERunState.Loot, _run.CurrentEncounterId));
        return events;
    }

    /// <summary>
    /// 在多页怪物战利品自选状态下免费循环切换至下一页（<c>(page + 1) % pages.Count</c>）。
    /// </summary>
    public bool TryCycleMonsterLootPage(out List<IGameSimEvent> events)
    {
        if (_monsterLootPages.Count == 0 && _run.MonsterLootPages.Count > 0)
            RestoreMonsterLootPagesFromRun();

        if (_run.State != ERunState.Loot || _monsterLootPages.Count <= 1)
        {
            events = new List<IGameSimEvent>();
            return false;
        }

        events = ClearShelfInternal(clearMonsterLootPages: false);
        CurrentMonsterLootPageIndex = (CurrentMonsterLootPageIndex + 1) % _monsterLootPages.Count;
        _run.MonsterLootPageIndex = CurrentMonsterLootPageIndex;
        CurrentMaxSelections = 1;
        _run.RerollCost = 0;
        _run.RerollsRemaining = UnlimitedRerollsCount;
        DealMonsterLootPage(CurrentMonsterLootPageIndex, events);
        return true;
    }

    private List<MonsterLootCandidate> CollectPaginatedMonsterLootCandidates(
        BazaarServer.Data.MonsterTemplate? monster,
        PlayerState? p1Core = null)
    {
        var result = new List<MonsterLootCandidate>();
        if (monster == null) return result;

        var ownedSkills = BuildOwnedSkillMaxTiers(p1Core);
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cand in monster.AllLootCandidates())
        {
            var tpl = _pool.GetIfAssetValid(cand.TemplateId);
            if (tpl == null || !_pool.IsPlayerSpawnable(tpl, allowGuidOnly: true)) continue;
            if (tpl.Type != "Item" && tpl.Type != "Skill") continue;

            var tier = ParseTier(!string.IsNullOrEmpty(cand.Tier) ? cand.Tier : tpl.StartingTier);
            if (tpl.Type == "Skill" && !CanOfferOwnedSkill(tpl.Id, tier, ownedSkills)) continue;
            if (!seenIds.Add(tpl.Id)) continue;

            int slotCost = GetMonsterLootSlotCost(tpl);
            result.Add(new MonsterLootCandidate(tpl, tier, cand.EnchantmentType, slotCost));
        }

        return result;
    }

    private static int GetMonsterLootSlotCost(CardPool.CardTemplate tpl)
    {
        if (string.Equals(tpl.Type, "Skill", StringComparison.OrdinalIgnoreCase))
            return 2;
        return Math.Max(1, (int)ParseSize(tpl.Size));
    }

    private static void PackMonsterLootPages(
        List<MonsterLootCandidate> candidates,
        List<List<MonsterLootCandidate>> targetPages)
    {
        targetPages.Clear();
        if (candidates.Count == 0) return;

        var currentPage = new List<MonsterLootCandidate>();
        int currentSlots = 0;
        foreach (var cand in candidates)
        {
            int cost = Math.Max(1, cand.SlotCost);
            if (currentPage.Count > 0 && currentSlots + cost > MaxMonsterLootSlotsPerPage)
            {
                targetPages.Add(currentPage);
                currentPage = new List<MonsterLootCandidate>();
                currentSlots = 0;
            }
            currentPage.Add(cand);
            currentSlots += cost;
        }

        if (currentPage.Count > 0)
            targetPages.Add(currentPage);
    }

    private void DealMonsterLootPage(int pageIndex, List<IGameSimEvent> events)
    {
        if (pageIndex < 0 || pageIndex >= _monsterLootPages.Count) return;

        foreach (var cand in _monsterLootPages[pageIndex])
        {
            RunState.CardEntry entry;
            if (cand.Template.Type == "Skill")
            {
                entry = CreateSkillCandidate(cand.Template, cand.Tier, isFree: true);
            }
            else
            {
                entry = CreateItemCandidate(cand.Template, cand.Tier, isFree: true);
                ApplyOptionalEnchantment(entry, cand.Enchantment);
            }

            _run.Cards[entry.InstanceId] = entry;
            _run.SelectionSet.Add(entry.InstanceId);
            events.Add(new GameSimEventCardDealt(entry.InstanceId, entry.TemplateId.ToString(), entry.Type));
        }
    }

    /// <summary>独立上下文调用入口：用于测试或无实例场景直接发放野怪战利品。</summary>
    public static List<IGameSimEvent> DealMonsterVictoryLoot(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        CardPool.CardTemplate? combatEncounterTpl,
        BazaarServer.Data.MonsterTemplate? monster,
        PlayerState? p1Core = null)
        => DealMonsterVictoryLoot(run, pool, repo, combatEncounterTpl, monster, p1Core, chooseOne: run.CustomRules?.MonsterLootChooseOne ?? true);

    public static List<IGameSimEvent> DealMonsterVictoryLoot(
        RunState run,
        CardPool pool,
        GameDataRepository repo,
        CardPool.CardTemplate? combatEncounterTpl,
        BazaarServer.Data.MonsterTemplate? monster,
        PlayerState? p1Core,
        bool chooseOne)
    {
        var engine = new ShopEngine(run, pool, repo);
        return engine.DealMonsterVictoryLoot(combatEncounterTpl, monster, p1Core, chooseOne);
    }

    /// <summary>
    /// 战败按对野怪造成的伤害比例精确折算金币与经验：
    ///   damageRatio = Clamp((monsterMaxHp - monsterRemainingHp) / (double)monsterMaxHp, 0.0, 1.0)
    ///   gold = Floor(rewardCombatGold * damageRatio) + rewardDefeatGold
    ///   xp   = Floor(rewardCombatXp   * damageRatio) + rewardDefeatXp
    /// </summary>
    public static MonsterDefeatRewardResult CalculateMonsterDefeatRewards(
        int monsterMaxHp,
        int monsterRemainingHp,
        int rewardCombatGold,
        int rewardCombatXp,
        int rewardDefeatGold = 0,
        int rewardDefeatXp = 0)
    {
        int safeMaxHp = Math.Max(1, monsterMaxHp);
        int clampedRem = Math.Clamp(monsterRemainingHp, 0, safeMaxHp);
        double damageRatio = Math.Clamp((safeMaxHp - clampedRem) / (double)safeMaxHp, 0.0, 1.0);

        int baseCombatGold = Math.Max(0, rewardCombatGold);
        int baseCombatXp = Math.Max(0, rewardCombatXp);
        int baseDefeatGold = Math.Max(0, rewardDefeatGold);
        int baseDefeatXp = Math.Max(0, rewardDefeatXp);

        int goldAwarded = (int)Math.Floor(baseCombatGold * damageRatio + 1e-9) + baseDefeatGold;
        int xpAwarded = (int)Math.Floor(baseCombatXp * damageRatio + 1e-9) + baseDefeatXp;

        return new MonsterDefeatRewardResult(
            DamageRatio: damageRatio,
            GoldAwarded: goldAwarded,
            XpAwarded: xpAwarded,
            BaseCombatGold: baseCombatGold,
            BaseCombatXp: baseCombatXp,
            BaseDefeatGold: baseDefeatGold,
            BaseDefeatXp: baseDefeatXp);
    }

    /// <summary>从野怪 CombatEncounter 模板直接读取基础奖池并按伤血比例折算战败奖励。</summary>
    public static MonsterDefeatRewardResult CalculateMonsterDefeatRewards(
        int monsterMaxHp,
        int monsterRemainingHp,
        CardPool.CardTemplate? combatEncounterTpl)
    {
        int combatGold = combatEncounterTpl?.RewardCombatGold ?? 3;
        int combatXp = combatEncounterTpl?.RewardCombatXp ?? 2;
        int defeatGold = combatEncounterTpl?.RewardDefeat?.GoldReward ?? combatEncounterTpl?.RewardDefeatGold ?? 0;
        int defeatXp = combatEncounterTpl?.RewardDefeat?.ExperienceReward ?? combatEncounterTpl?.RewardDefeatXp ?? 0;
        return CalculateMonsterDefeatRewards(monsterMaxHp, monsterRemainingHp, combatGold, combatXp, defeatGold, defeatXp);
    }

    /// <summary>升级奖励（从 level_ups 官方 30 级 141 组抽卡，结合真实 PlayerState 评估条件必出与加权组，免费选 1 项）。</summary>
    public List<IGameSimEvent> DealLevelUpChoices(int level, LevelUpTable? levelUps, PlayerState? playerState = null)
    {
        var events = new List<IGameSimEvent>();
        events.AddRange(ClearShelf());

        _run.State = ERunState.LevelUp;
        _run.CurrentEncounterId = null;
        _run.RerollCost = 0;
        _run.RerollsRemaining = 0;
        _run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = true,
        };

        var queryPlayer = playerState ?? BuildDummyQueryPlayer(_run.Hero.ToString(), (int)_run.Day);
        var ctx = new RuleExecutionContext(_repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, _rng)
        {
            CurrentDay = (int)_run.Day,
            CurrentHour = (int)_run.Hour,
            CurrentEncounterId = null,
            PlayMode = _run.PlayMode.ToString(),
        };

        var cardIds = levelUps?.RollRewards(level, ctx, id => _pool.IsPlayerSpawnableId(id, allowGuidOnly: true)) ?? new List<string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in cardIds)
        {
            var tpl = _pool.Get(id);
            if (tpl == null || !_pool.IsPlayerSpawnable(tpl, allowGuidOnly: true) || !used.Add(tpl.Id)) continue;
            var coreTpl = _repo.GetCard(tpl.Id);
            if (coreTpl != null)
            {
                var effectiveCoreTier = SpawnQueryEngine.ClampToSupportedTier(coreTpl, TierHelper.ParseTier(tpl.StartingTier));
                var inst = new CardInstance(coreTpl, effectiveCoreTier);
                var entry = CreateEntryFromCoreInstance(inst, isFree: true);
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, tpl.Id, entry.Type));
            }
            else if (tpl.Type == "Skill")
            {
                var entry = CreateSkillCandidate(tpl, ParseTier(tpl.StartingTier), isFree: true);
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, tpl.Id, ECardType.Skill));
            }
            else if (tpl.Type == "Item")
            {
                var entry = CreateItemCandidate(tpl, ParseTier(tpl.StartingTier), isFree: true);
                _run.Cards[entry.InstanceId] = entry;
                _run.SelectionSet.Add(entry.InstanceId);
                events.Add(new GameSimEventCardDealt(entry.InstanceId, tpl.Id, ECardType.Item));
            }
        }

        // 彻底移除对 Lv10/Lv12 等升级奖励硬塞随机青铜/白银垃圾技能的兜底逻辑（对齐官方 BazaarCardDealer.DealLevelUpRewards）；
        // 仅保留防彻底卡死底线：当整次升级抽卡被过滤到一张不剩（_run.SelectionSet.Count == 0）时，才补 1~3 张同英雄合法技能。
        if (_run.SelectionSet.Count == 0)
        {
            string heroStr = _run.Hero.ToString();
            var ownedSkills = BuildOwnedSkillMaxTiers(playerState);
            var rawAllSkills = _pool.Skills(heroStr);
            var eligibleAllSkills = rawAllSkills
                .Where(s => CanOfferOwnedSkill(s.Id, ParseTier(s.StartingTier), ownedSkills))
                .ToList();
            var allSkills = eligibleAllSkills.Count > 0 ? eligibleAllSkills : rawAllSkills;
            var heroExclusiveSkills = allSkills
                .Where(s => s.Heroes.Contains(heroStr, StringComparer.OrdinalIgnoreCase))
                .ToList();

            var effectiveRules = ActiveRules;
            var archetypeResolver = ResolveArchetypeResolver(effectiveRules);
            foreach (var fallbackPool in new[] { heroExclusiveSkills, allSkills })
            {
                while (_run.SelectionSet.Count < 3)
                {
                    var sk = PickRandomUnused(fallbackPool, used, archetypeResolver, effectiveRules);
                    if (sk == null) break;
                    var entry = CreateSkillCandidate(sk, ParseTier(sk.StartingTier), isFree: true);
                    _run.Cards[entry.InstanceId] = entry;
                    _run.SelectionSet.Add(entry.InstanceId);
                    events.Add(new GameSimEventCardDealt(entry.InstanceId, sk.Id, ECardType.Skill));
                }
                if (_run.SelectionSet.Count > 0) break;
            }
        }

        events.Add(new GameSimEventStateTransitioned(ERunState.LevelUp, null));
        return events;
    }

    // ── 辅助构造器 ──────────────────────────────────────────────────────────

    public RunState.CardEntry CreateEncounterCandidate(CardPool.CardTemplate tpl, ECardType type, string prefix)
    {
        var coreTpl = _repo.GetCard(tpl.Id);
        var entry = new RunState.CardEntry
        {
            InstanceId = NewId(prefix),
            TemplateId = Guid.Parse(tpl.Id),
            Type = type,
            Size = ParseSize(tpl.Size),
            Tier = ParseTier(tpl.StartingTier),
            Owner = null,
            Socket = null,
            Section = null,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, coreTpl);
        return entry;
    }

    private ETier ClampTierForTemplate(CardPool.CardTemplate? poolTpl, BazaarCore.Models.CardTemplate? coreTpl, ETier requestedTier)
    {
        var reqCoreTier = ToCoreTier(requestedTier);
        if (coreTpl != null)
        {
            var clamped = SpawnQueryEngine.ClampToSupportedTier(coreTpl, reqCoreTier);
            return ParseTier(clamped.ToString());
        }
        if (poolTpl == null) return requestedTier;
        if (poolTpl.TierAttributes.Count > 0 && !poolTpl.TierAttributes.ContainsKey(requestedTier.ToString()))
        {
            if (poolTpl.TierAttributes.ContainsKey(poolTpl.StartingTier))
                return ParseTier(poolTpl.StartingTier);
            foreach (ETier candidate in new[] { ETier.Bronze, ETier.Silver, ETier.Gold, ETier.Diamond, ETier.Legendary })
            {
                if (candidate >= requestedTier && poolTpl.TierAttributes.ContainsKey(candidate.ToString()))
                    return candidate;
            }
            return ParseTier(poolTpl.StartingTier);
        }
        return requestedTier < ParseTier(poolTpl.StartingTier) ? ParseTier(poolTpl.StartingTier) : requestedTier;
    }

    public RunState.CardEntry CreateSkillCandidate(CardPool.CardTemplate tpl, ETier tier, bool isFree)
    {
        var coreTpl = _repo.GetCard(tpl.Id);
        ETier effectiveTier = ClampTierForTemplate(tpl, coreTpl, tier);
        var coreTier = ToCoreTier(effectiveTier);
        var coreCard = coreTpl != null ? new CardInstance(coreTpl, coreTier) : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = NewId("skl"),
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Skill,
            Size = ParseSize(tpl.Size),
            Tier = effectiveTier,
            Owner = null,
            Socket = null,
            Section = null,
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, coreTpl);
        if (tpl.TierAttributes.TryGetValue(effectiveTier.ToString(), out var attrs) ||
            tpl.TierAttributes.TryGetValue(tpl.StartingTier, out attrs))
        {
            foreach (var kv in attrs)
                if (Enum.TryParse<ECardAttributeType>(kv.Key, true, out var at))
                    entry.Attributes[at] = kv.Value;
        }

        if (coreCard != null)
        {
            ApplyLocalShelfAuraAndSync(entry, coreCard, isFree);
        }
        else
        {
            var prices = _repo.GameMode.StandardPrices;
            entry.Attributes[ECardAttributeType.BuyPrice] = isFree ? 0 : prices.GetSkillBuyPrice(coreTier);
            entry.Attributes[ECardAttributeType.SellPrice] = prices.GetSkillSellPrice(coreTier);
        }
        return entry;
    }

    public RunState.CardEntry CreateItemCandidate(CardPool.CardTemplate tpl, ETier tier, bool isFree)
    {
        var coreTpl = _repo.GetCard(tpl.Id);
        ECardSize size = ParseSize(tpl.Size);
        ETier effectiveTier = ClampTierForTemplate(tpl, coreTpl, tier);
        var coreTier = ToCoreTier(effectiveTier);
        var coreCard = coreTpl != null ? new CardInstance(coreTpl, coreTier) : null;
        var entry = new RunState.CardEntry
        {
            InstanceId = NewId("itm"),
            TemplateId = Guid.Parse(tpl.Id),
            Type = ECardType.Item,
            Size = size,
            Tier = effectiveTier,
            Owner = null,
            Socket = null,
            Section = null,
            CoreCard = coreCard,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, coreTpl);

        string tierKey = effectiveTier.ToString();
        if (!tpl.TierAttributes.TryGetValue(tierKey, out var attrs))
            tpl.TierAttributes.TryGetValue(tpl.StartingTier, out attrs);
        if (attrs != null)
        {
            foreach (var kv in attrs)
                if (Enum.TryParse<ECardAttributeType>(kv.Key, true, out var at))
                    entry.Attributes[at] = kv.Value;
        }

        if (coreCard != null)
        {
            ApplyLocalShelfAuraAndSync(entry, coreCard, isFree);
        }
        else
        {
            var (buy, sell) = PriceFor(size, effectiveTier);
            if (isFree)
                entry.Attributes[ECardAttributeType.BuyPrice] = 0;
            else if (!entry.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int existingBuy) || existingBuy <= 0)
                entry.Attributes[ECardAttributeType.BuyPrice] = Math.Max(1, buy);
            else
                entry.Attributes[ECardAttributeType.BuyPrice] = Math.Max(1, existingBuy);
            if (!entry.Attributes.TryGetValue(ECardAttributeType.SellPrice, out int existingSell) || existingSell <= 0)
                entry.Attributes[ECardAttributeType.SellPrice] = sell;
        }
        return entry;
    }

    /// <summary>
    /// 根据 BazaarCore CardInstance 创建候选区 CardEntry，
    /// 严格钳制不支持的过低阶级（防止 StartingTier=Gold 的卡以 Bronze 实例化导致 Custom_0 等属性归零），
    /// 并保留模板真实 ECardType（禁止再把 EncounterStep / EventEncounter / PedestalEncounter / CombatEncounter 错标为 Item）。
    /// </summary>
    public RunState.CardEntry CreateEntryFromCoreInstance(CardInstance inst, bool isFree)
    {
        var clampedTier = SpawnQueryEngine.ClampToSupportedTier(inst.Template, inst.Tier);
        if (clampedTier != inst.Tier)
        {
            var rebuilt = new CardInstance(inst.Template, clampedTier, inst.Enchantment)
            {
                StartSlot = inst.StartSlot,
            };
            foreach (var (k, v) in inst.PermanentAdd) rebuilt.PermanentAdd[k] = v;
            foreach (var (k, v) in inst.PermanentMult) rebuilt.PermanentMult[k] = v;
            foreach (var (k, v) in inst.DayAdd) rebuilt.DayAdd[k] = v;
            foreach (var (k, v) in inst.DayMult) rebuilt.DayMult[k] = v;
            foreach (var (k, v) in inst.HourAdd) rebuilt.HourAdd[k] = v;
            foreach (var (k, v) in inst.HourMult) rebuilt.HourMult[k] = v;
            inst = rebuilt;
        }

        var tpl = _pool.Get(inst.Template.Id);
        ECardType cardType = ParseCardType(inst.Template.Type);
        ETier tier = ParseTier(inst.Tier.ToString());
        ECardSize size = cardType == ECardType.Item
            ? ParseSize(inst.EffectiveSize.ToString())
            : ParseSize(inst.Template.Size);

        string prefix = cardType switch
        {
            ECardType.Skill => "skl",
            ECardType.SocketEffect => "skt",
            ECardType.EncounterStep => "stp",
            ECardType.EventEncounter => "enc",
            ECardType.PedestalEncounter => "ped",
            ECardType.CombatEncounter => "cbt",
            ECardType.PvpEncounter => "pvp",
            _ => "itm",
        };

        EEnchantmentType? parsedEnchant = null;
        if (!string.IsNullOrEmpty(inst.Enchantment) &&
            Enum.TryParse<EEnchantmentType>(inst.Enchantment, true, out var e))
        {
            parsedEnchant = e;
        }

        var entry = new RunState.CardEntry
        {
            InstanceId = NewId(prefix),
            TemplateId = Guid.Parse(inst.Template.Id),
            Type = cardType,
            Size = size,
            Tier = tier,
            Enchantment = parsedEnchant,
            Owner = null,
            Socket = null,
            Section = null,
            CoreCard = inst,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, inst.Template);
        ApplyLocalShelfAuraAndSync(entry, inst, isFree);
        return entry;
    }

    private void ApplyOptionalEnchantment(RunState.CardEntry entry, string? enchantmentName)
    {
        if (string.IsNullOrWhiteSpace(enchantmentName)) return;
        if (!Enum.TryParse<EEnchantmentType>(enchantmentName, true, out var ench)) return;

        entry.Enchantment = ench;
        if (entry.CoreCard != null)
        {
            entry.CoreCard.ApplyEnchantment(ench.ToString());
            bool isFree = entry.Attributes.TryGetValue(ECardAttributeType.BuyPrice, out int bp) && bp == 0;
            ApplyLocalShelfAuraAndSync(entry, entry.CoreCard, isFree);
        }
    }

    /// <summary>
    /// P1-1 &amp; P1-2：为商店/战利品/升级货架单卡构建独立临时 PlayerState 并运行局部 OutOfCombat 光环，
    /// 使 Deadly / Shiny / Golden / Obsidian 等光环型附魔加成与翻倍售价直接展现在候选卡面板上，
    /// 同时将 Skill 购买价对齐 StandardPrices.GetSkillBuyPrice（5/10/20/40/50）。
    /// </summary>
    private void ApplyLocalShelfAuraAndSync(RunState.CardEntry entry, CardInstance inst, bool isFree)
    {
        int origSlot = inst.StartSlot;
        if (entry.Type is ECardType.Item or ECardType.Skill or ECardType.SocketEffect)
        {
            var preview = new PlayerState("shelf_preview", "shelf_preview", _run.Hero.ToString(), 250, 20, 0, 10)
            {
                Day = (int)_run.Day,
                Hour = (int)_run.Hour,
            };
            if (entry.Type == ECardType.Skill)
                preview.Skills.Add(inst);
            else if (entry.Type == ECardType.SocketEffect)
                preview.SocketEffects.Add(inst);
            else
                preview.Hand.TryAddAuto(inst);

            AuraEngine.RecalculateAuras(preview, null, RuleScope.OutOfCombat, (int)_run.Day, (int)_run.Hour, _repo);
            inst.StartSlot = origSlot;
        }

        if (entry.Type == ECardType.Item)
            entry.Size = ParseSize(inst.EffectiveSize.ToString());

        var keys = new HashSet<string>(inst.BaseAttributes.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (string k in inst.PermanentAdd.Keys) keys.Add(k);
        foreach (string k in inst.PermanentMult.Keys) keys.Add(k);
        foreach (string k in inst.AuraAdd.Keys) keys.Add(k);
        foreach (string k in inst.AuraMult.Keys) keys.Add(k);

        foreach (string k in keys)
        {
            if (k.Equals("Value", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Enum.TryParse<ECardAttributeType>(k, true, out var at)) continue;
            if (at is ECardAttributeType.BuyPrice or ECardAttributeType.SellPrice) continue;
            int val = at == ECardAttributeType.Flying
                ? Math.Max(inst.IsFlying ? 1 : 0, (int)Math.Round(inst.GetAttribute(k)))
                : (int)Math.Round(inst.GetAttribute(k));
            if (val != 0 || entry.Attributes.ContainsKey(at) || inst.BaseAttributes.ContainsKey(k))
                entry.Attributes[at] = val;
        }
        if (inst.IsFlying)
            entry.Attributes[ECardAttributeType.Flying] = 1;

        var prices = _repo.GameMode.StandardPrices;
        if (entry.Type == ECardType.Item)
        {
            int buy = isFree ? 0 : RunState.ComputeCardBuyPrice(entry, inst, prices);
            entry.Attributes[ECardAttributeType.BuyPrice] = buy;

            bool hasValueBonus = inst.BaseAttributes.ContainsKey("Value")
                              || inst.PermanentAdd.ContainsKey("Value")
                              || inst.PermanentMult.ContainsKey("Value")
                              || inst.AuraAdd.ContainsKey("Value")
                              || inst.AuraMult.ContainsKey("Value");
            int valueBonus = hasValueBonus ? (int)Math.Round(inst.GetAttribute("Value")) : 0;
            entry.Attributes[ECardAttributeType.SellPrice] = Math.Max(0, inst.GetSellPrice(prices) + valueBonus);
        }
        else if (entry.Type == ECardType.Skill)
        {
            entry.Attributes[ECardAttributeType.BuyPrice] = isFree ? 0 : RunState.ComputeCardBuyPrice(entry, inst, prices);
            entry.Attributes[ECardAttributeType.SellPrice] = Math.Max(0, inst.GetSellPrice(prices));
        }
        else
        {
            if (isFree)
            {
                entry.Attributes[ECardAttributeType.BuyPrice] = 0;
            }
            else
            {
                int customBuy = (int)Math.Round(inst.GetAttribute("BuyPrice"));
                if (customBuy > 0)
                    entry.Attributes[ECardAttributeType.BuyPrice] = customBuy;
            }
        }
    }

    public static RuleNode NormalizeSequentialSingleGroupQuery(RuleNode spawnContext)
    {
        if (spawnContext.Groups is not { Count: 1 } groups) return spawnContext;
        if (!string.Equals(spawnContext.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase))
            return spawnContext;
        var grp = groups[0];
        if (grp.Limit != null || spawnContext.Limit == null) return spawnContext;

        var clonedGroup = new RuleNode
        {
            Type = grp.Type,
            Filters = grp.Filters,
            SelectionMethod = grp.SelectionMethod,
            Limit = spawnContext.Limit,
            Prerequisites = grp.Prerequisites,
            RandomWeight = grp.RandomWeight,
            Behaviors = grp.Behaviors,
        };
        return new RuleNode
        {
            Type = spawnContext.Type,
            SelectionMethod = spawnContext.SelectionMethod,
            Limit = spawnContext.Limit,
            Behaviors = spawnContext.Behaviors,
            Groups = new List<RuleNode> { clonedGroup },
        };
    }

    private static bool IsSpecialConditionalGroup(RuleNode grp)
    {
        if (grp.Prerequisites is not { Count: > 0 } prereqs)
            return false;
        // 若仅是“未拥有某卡时的负面兜底（Comparison == Equal && Amount == 0）”，视为常规保底而非解锁特殊选项
        bool onlyNegativeFallback = prereqs.All(p =>
            string.Equals(p.Type, "TPrerequisiteCardCount", StringComparison.Ordinal) &&
            string.Equals(p.Comparison, "Equal", StringComparison.Ordinal) &&
            (p.Amount ?? 0) == 0);
        return !onlyNegativeFallback;
    }

    private static RuleNode PrioritizeSequentialConditionalGroups(RuleNode spawnContext, RuleExecutionContext ctx)
    {
        if (!string.Equals(spawnContext.SelectionMethod, "Sequential", StringComparison.OrdinalIgnoreCase) ||
            spawnContext.Groups is not { Count: > 1 } groups)
        {
            return spawnContext;
        }

        // 若各组存在总格宽超出 10 格（例如 6+ 组 Medium=2 占格）的风险，则把当前命中的特殊条件解锁组前置，
        // 避免如 Shrouded Figure (Limit=7) / Street Festival (Limit=8) 后半段的专属选项（如 Absorb Memories）被 10 格截断
        var specialEligible = new List<RuleNode>();
        var normalEligible = new List<RuleNode>();
        foreach (var grp in groups)
        {
            if (!ValueAndConditionEvaluator.EvaluatePrerequisites(grp.Prerequisites, ctx))
                continue;
            if (IsSpecialConditionalGroup(grp))
                specialEligible.Add(grp);
            else
                normalEligible.Add(grp);
        }

        if (specialEligible.Count == 0)
            return spawnContext;

        var reordered = new List<RuleNode>(specialEligible.Count + normalEligible.Count);
        reordered.AddRange(specialEligible);
        reordered.AddRange(normalEligible);

        return new RuleNode
        {
            Type = spawnContext.Type,
            SelectionMethod = spawnContext.SelectionMethod,
            Limit = spawnContext.Limit,
            Behaviors = spawnContext.Behaviors,
            Groups = reordered,
        };
    }

    internal static bool EvaluateRequirementNode(RuleNode? req, RuleExecutionContext ctx)
    {
        if (req == null) return true;
        if (string.Equals(req.Type, "TSelectionRequirement", StringComparison.Ordinal))
            return req.Prerequisites == null || req.Prerequisites.All(p => EvaluateRequirementNode(p, ctx));
        if (req.Type.StartsWith("TPrerequisite", StringComparison.Ordinal))
            return ValueAndConditionEvaluator.EvaluatePrerequisite(req, ctx);
        return ValueAndConditionEvaluator.EvaluateRunOrPlayerCondition(req, ctx);
    }

    internal static bool CheckChildOptionRunPrerequisites(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        RuleExecutionContext ctx)
    {
        static bool PassesRunOnly(RuleNode? node, RuleExecutionContext runCtx)
        {
            if (node == null) return true;
            if (string.Equals(node.Type, "TSelectionRequirement", StringComparison.Ordinal))
                return node.Prerequisites == null || node.Prerequisites.All(p => PassesRunOnly(p, runCtx));
            if (string.Equals(node.Type, "TPrerequisiteRun", StringComparison.Ordinal))
                return ValueAndConditionEvaluator.EvaluatePrerequisite(node, runCtx);
            return true;
        }

        if (coreTpl?.SelectionRequirements != null && !PassesRunOnly(coreTpl.SelectionRequirements, ctx))
            return false;
        if (poolTpl?.SelectionRequirements is { Count: > 0 })
        {
            foreach (var req in poolTpl.SelectionRequirements)
            {
                if (!PassesRunOnly(req, ctx))
                    return false;
            }
        }
        return true;
    }

    private static bool HasEligibleRunPrerequisitesInSpawnGroups(
        RuleNode? spawnContext,
        RuleExecutionContext ctx)
    {
        if (spawnContext?.Groups is not { Count: > 0 } groups)
            return true;

        bool sawAnyRunPrereq = false;
        foreach (var grp in groups)
        {
            var runPrereqs = grp.Prerequisites?
                .Where(p => string.Equals(p.Type, "TPrerequisiteRun", StringComparison.Ordinal))
                .ToList();
            if (runPrereqs == null || runPrereqs.Count == 0)
                return true;

            sawAnyRunPrereq = true;
            if (runPrereqs.All(p => ValueAndConditionEvaluator.EvaluatePrerequisite(p, ctx)))
                return true;
        }
        return !sawAnyRunPrereq;
    }

    private bool CheckEncounterRequirements(
        CardPool.CardTemplate? poolTpl,
        BazaarCore.Models.CardTemplate? coreTpl,
        RuleExecutionContext ctx)
    {
        if (coreTpl?.SelectionRequirements != null && !EvaluateRequirementNode(coreTpl.SelectionRequirements, ctx))
            return false;
        if (poolTpl?.SelectionRequirements is { Count: > 0 })
        {
            foreach (var req in poolTpl.SelectionRequirements)
            {
                if (!EvaluateRequirementNode(req, ctx))
                    return false;
            }
        }

        var spawnContext = coreTpl?.SelectionContext?.SpawnContext ?? poolTpl?.SelectionContext?.SpawnContext;
        if (!HasEligibleRunPrerequisitesInSpawnGroups(spawnContext, ctx))
            return false;

        return true;
    }

    private bool HasPlayableEncounterPayload(CardPool.CardTemplate tpl)
    {
        if (tpl.SelectionContext?.SpawnContext != null) return true;
        var core = _repo.GetCard(tpl.Id);
        return core?.SelectionContext?.SpawnContext != null
            || (core?.NextEncounterOnSelection is { Count: > 0 });
    }

    /// <summary>计算单张候选卡在货架上占用的格子宽度（Small/Tiny=1, Medium=2, Large=3）。</summary>
    public static int GetShelfSlotWidth(CardInstance inst)
    {
        if (inst == null) return 2;
        if (string.Equals(inst.Template.Type, "Item", StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(inst.SlotWidth, 1, 3);
        return Math.Clamp((int)ParseSize(inst.Template.Size), 1, 3);
    }

    /// <summary>计算单张候选 CardEntry 在货架上占用的格子宽度（Small/Tiny=1, Medium=2, Large=3）。</summary>
    public static int GetShelfSlotWidth(RunState.CardEntry entry)
    {
        if (entry == null) return 2;
        if (entry.CoreCard != null && entry.Type == ECardType.Item)
            return Math.Clamp(entry.CoreCard.SlotWidth, 1, 3);
        return Math.Clamp((int)entry.Size, 1, 3);
    }

    /// <summary>检查 SpawnContext 是否显式声明允许同模板重复生成（如三战利品事件）。</summary>
    public static bool HasAllowDuplicatesBehavior(RuleNode? spawnContext)
        => spawnContext?.Behaviors != null &&
           spawnContext.Behaviors.Any(b =>
               string.Equals(b.Type, "TSpawnBehaviorAllowDuplicates", StringComparison.OrdinalIgnoreCase) &&
               (b.AllowDuplicates ?? true));

    /// <summary>检查 SpawnContext 是否显式允许刷新出玩家已拥有的同模板卡牌。</summary>
    public static bool HasAllowPlayerDuplicatesBehavior(RuleNode? spawnContext)
        => spawnContext?.Behaviors != null &&
           spawnContext.Behaviors.Any(b =>
               string.Equals(b.Type, "TSpawnBehaviorAllowPlayerDuplicates", StringComparison.OrdinalIgnoreCase) &&
               (b.AllowPlayerDuplicates ?? true));

    /// <summary>判断模板在对应品阶下是否为显式 0 元物品/免费包裹（付费商店禁止上架）。</summary>
    public bool IsZeroBuyPriceOrFreePackage(CardPool.CardTemplate? poolTpl, BazaarCore.Models.CardTemplate? coreTpl, string? tier = null)
    {
        string effTier = !string.IsNullOrWhiteSpace(tier)
            ? tier!
            : (poolTpl?.StartingTier ?? coreTpl?.StartingTier ?? "Bronze");

        if (CardPool.HasExplicitZeroOrNegativeBuyPrice(poolTpl, effTier))
            return true;

        if (coreTpl?.Tiers != null)
        {
            if (coreTpl.Tiers.TryGetValue(effTier, out var td) &&
                td.Attributes != null &&
                td.Attributes.TryGetValue("BuyPrice", out double bp) &&
                bp <= 0)
            {
                return true;
            }
            if (!string.IsNullOrWhiteSpace(coreTpl.StartingTier) &&
                coreTpl.Tiers.TryGetValue(coreTpl.StartingTier, out var st) &&
                st.Attributes != null &&
                st.Attributes.TryGetValue("BuyPrice", out double sbp) &&
                sbp <= 0)
            {
                return true;
            }
        }

        bool isPackage = (poolTpl?.HiddenTags.Any(t => t.Equals("Package", StringComparison.OrdinalIgnoreCase)) ?? false)
                      || (poolTpl?.Tags.Any(t => t.Equals("Package", StringComparison.OrdinalIgnoreCase)) ?? false)
                      || (coreTpl?.HiddenTags.Any(t => t.Equals("Package", StringComparison.OrdinalIgnoreCase)) ?? false)
                      || (coreTpl?.Tags.Any(t => t.Equals("Package", StringComparison.OrdinalIgnoreCase)) ?? false);
        return isPackage && (poolTpl == null || CardPool.HasExplicitZeroOrNegativeBuyPrice(poolTpl, effTier));
    }

    private int GetMonsterHealthMax(CardPool.CardTemplate enc)
        => MonsterPool.GetMonsterHealthMax(enc, _repo);

    private static bool IsDevOrTestMonsterName(string? name)
        => MonsterPool.IsExcludedDevOrTestMonsterName(name);

    private bool IsNormalPlayableMonsterEncounter(CardPool.CardTemplate c)
        => MonsterPool.IsPlayableCombatEncounter(c, _pool, _repo, requireAlwaysSpawning: true);

    private List<CardPool.CardTemplate> RollDayMatchedMonsters(
        int day,
        int count,
        ISet<string>? visitedTodayIds = null,
        ISet<string>? bannedIds = null)
        => OfficialDaySchedule.SelectDayMonsters(day, count, _pool, _repo, _rng, visitedTodayIds, bannedIds);

    private bool IsMerchantEncounter(CardPool.CardTemplate? tpl, BazaarCore.Models.CardTemplate? coreTpl = null)
    {
        string id = tpl?.Id ?? coreTpl?.Id ?? string.Empty;
        coreTpl ??= tpl != null ? _repo.GetCard(tpl.Id) : null;
        string name = tpl?.InternalName ?? coreTpl?.InternalName ?? string.Empty;
        return CardArchetypeIndex.IsMerchantEncounterTemplate(id, name, tpl, coreTpl);
    }

    private CardPool.CardTemplate? PickRandomUnused(
        IReadOnlyList<CardPool.CardTemplate> list,
        HashSet<string> used,
        Func<string, ulong>? archetypeResolver = null,
        CustomRunRuleSet? rules = null)
    {
        if (list.Count == 0) return null;
        var avail = list.Where(c => !used.Contains(c.Id)).ToList();
        if (avail.Count == 0) return null;

        var pick = SpawnQueryEngine.PickWeightedByArchetype(avail, c => c.Id, _rng, archetypeResolver, rules)!;
        used.Add(pick.Id);
        return pick;
    }

    private PlayerState BuildDummyQueryPlayer(string hero, int day)
    {
        EnsureRunExclusionSetInitialized();
        int hpMax = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
        int prestige = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20);
        int gold = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 8);
        var ps = new PlayerState("query", "query", hero, hpMax, prestige, gold, 10)
        {
            Day = day,
            Hour = (int)_run.Hour,
            Level = _run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1),
            Wins = (int)_run.Victories,
            Losses = (int)_run.Defeats,
            FatesUsed = _run.HasVisitedFates,
        };
        if (_run.ExclusionSetInitialized)
        {
            ps.InitializeExclusionSet(null);
            foreach (string exId in _run.ExclusionSet)
                ps.ExclusionSet.Add(exId);
        }
        ps.Hand.UnlockedMask = _run.UnlockedSlots;
        ps.Stash.UnlockedMask = 1023;
        foreach (var (ak, av) in _run.PlayerAttributes)
        {
            string key = ak.ToString();
            double baseVal = av;
            double mult = _run.PlayerAuraMult.GetValueOrDefault(key, 1.0);
            if (Math.Abs(mult) > 1e-6 && Math.Abs(mult - 1.0) > 1e-6)
                baseVal /= mult;
            baseVal -= _run.PlayerAuraAdd.GetValueOrDefault(key, 0.0);
            ps.Attributes[key] = baseVal;
        }
        foreach (var (k, v) in _run.PlayerAuraAdd) ps.AuraAdd[k] = v;
        foreach (var (k, v) in _run.PlayerAuraMult) ps.AuraMult[k] = v;

        foreach (var c in _run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player && c.CoreCard != null)
            .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : int.MaxValue))
        {
            var copy = CloneCardForQuery(c.CoreCard!, c.Socket);
            if (c.Type == ECardType.SocketEffect)
            {
                ps.SocketEffects.Add(copy);
            }
            else if (c.Type == ECardType.Skill)
            {
                ps.Skills.Add(copy);
            }
            else if (c.Section == EInventorySection.Stash)
            {
                int slot = c.Socket.HasValue ? (int)c.Socket.Value : 0;
                if (!ps.Stash.TryPlaceOrShift(copy, slot))
                    ps.Stash.TryAddAuto(copy);
            }
            else
            {
                int slot = c.Socket.HasValue ? (int)c.Socket.Value : 3;
                if (!ps.Hand.TryPlaceOrShift(copy, slot))
                    ps.Hand.TryAddAuto(copy);
            }
        }

        AuraEngine.RecalculateAuras(ps, null, RuleScope.OutOfCombat, day, (int)_run.Hour, _repo);
        return ps;
    }

    private static CardInstance CloneCardForQuery(CardInstance src, EContainerSocketId? socket)
    {
        var copy = src.CloneForCombat();
        copy.StartSlot = socket.HasValue ? (int)socket.Value : src.StartSlot;
        foreach (var (k, v) in src.HourAdd) copy.HourAdd[k] = v;
        foreach (var (k, v) in src.HourMult) copy.HourMult[k] = v;
        foreach (var (k, v) in src.DayAdd) copy.DayAdd[k] = v;
        foreach (var (k, v) in src.DayMult) copy.DayMult[k] = v;
        return copy;
    }

    private PlayerState? BuildMonsterLootPlayerState(BazaarServer.Data.MonsterTemplate? monster)
    {
        if (monster == null) return null;
        if (_repo.MonstersById.TryGetValue(monster.Id, out var coreMonster))
            return CombatSimulator.BuildMonsterPlayerState(_repo, coreMonster);

        var ps = new PlayerState("monster_" + monster.Id, monster.InternalName ?? "Monster", "Common", Math.Max(10, monster.HealthMax), monster.Prestige, 0, 10)
        {
            IsMonster = true,
            Level = monster.Level,
        };
        foreach (var it in monster.Items)
        {
            var tpl = _repo.GetCard(it.TemplateId);
            if (tpl != null) ps.Hand.TryAddAuto(new CardInstance(tpl, TierHelper.ParseTier(it.Tier ?? "Bronze"), it.EnchantmentType));
        }
        foreach (var sk in monster.Skills)
        {
            var tpl = _repo.GetCard(sk.TemplateId);
            if (tpl != null) ps.Skills.Add(new CardInstance(tpl, TierHelper.ParseTier(sk.Tier ?? "Bronze")));
        }
        foreach (var st in monster.StashItems)
        {
            var tpl = _repo.GetCard(st.TemplateId);
            if (tpl != null) ps.Stash.TryAddAuto(new CardInstance(tpl, TierHelper.ParseTier(st.Tier ?? "Bronze"), st.EnchantmentType));
        }
        return ps;
    }

    public static ECardType ParseCardType(string? type) => type switch
    {
        "Skill" => ECardType.Skill,
        "CombatEncounter" => ECardType.CombatEncounter,
        "EncounterStep" => ECardType.EncounterStep,
        "EventEncounter" => ECardType.EventEncounter,
        "PedestalEncounter" => ECardType.PedestalEncounter,
        "PvpEncounter" => ECardType.PvpEncounter,
        "SocketEffect" => ECardType.SocketEffect,
        "PlayerEffect" => ECardType.PlayerEffect,
        _ => ECardType.Item,
    };

    public static (int Buy, int Sell) PriceFor(ECardSize size, ETier tier)
    {
        int s = Math.Clamp((int)size - 1, 0, 2);
        int t = Math.Clamp((int)tier, 0, 4);

        int[,] buy =
        {
            { 2, 4, 8, 16, 24 },
            { 4, 8, 16, 32, 48 },
            { 6, 12, 24, 48, 64 },
        };
        int[,] sell =
        {
            { 1, 2, 4, 8, 12 },
            { 2, 4, 8, 16, 24 },
            { 3, 6, 12, 24, 32 },
        };
        return (buy[s, t], sell[s, t]);
    }

    public static ECardSize ParseSize(string s) => s switch
    {
        "Small" => ECardSize.Small,
        "Large" => ECardSize.Large,
        _ => ECardSize.Medium,
    };

    public static ETier ParseTier(string s) => s switch
    {
        "Silver" => ETier.Silver,
        "Gold" => ETier.Gold,
        "Diamond" => ETier.Diamond,
        "Legendary" => ETier.Legendary,
        _ => ETier.Bronze,
    };

    public static CardTier ToCoreTier(ETier t) => t switch
    {
        ETier.Silver => CardTier.Silver,
        ETier.Gold => CardTier.Gold,
        ETier.Diamond => CardTier.Diamond,
        ETier.Legendary => CardTier.Legendary,
        _ => CardTier.Bronze,
    };
}

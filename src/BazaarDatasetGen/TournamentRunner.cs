using System.Diagnostics;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarDatasetGen;

/// <summary>
/// 淘汰赛阶段的完整运行结果汇总。
/// </summary>
public sealed class TournamentResult
{
    public required IReadOnlyList<PvpRunRecord> AllRuns { get; init; }
    public required IReadOnlyList<PvpBoardSnapshot> AllSnapshots { get; init; }
    public int MainTournamentRunCount { get; init; }
    public int SupplementalRunCount { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// 多核并发大逃杀淘汰赛调度器（v2 天梯画像生态版）：
/// 按 8 英雄均分 × 8 种真实天梯占比画像（HardForcer 18% / FlexPivot 15% / TempoAggro 15% /
/// GreedyEco 12% / Casual 14% / MessyHybrid 12% / CluelessNewbie 9% / GreedyBust 5%）
/// 并发推进 Day 1..MaxDay 发育与晚间两两 PvP 实战，并逐日打印自然淘汰漏斗日志。
/// </summary>
public sealed class TournamentRunner
{
    /// <summary>
    /// 天梯生态 100 槽精确配比循环序列：
    /// HardForcer 18%, FlexPivot 15%, TempoAggro 15%, GreedyEco 12%,
    /// Casual 14%, MessyHybrid 12%, CluelessNewbie 9%, GreedyBust 5%。
    /// </summary>
    private static readonly PersonaType[] LadderPersonaCycle100 = BuildInterleavedPersonaCycle(
    [
        (PersonaType.HardForcer, 18),
        (PersonaType.FlexPivot, 15),
        (PersonaType.TempoAggro, 15),
        (PersonaType.GreedyEco, 12),
        (PersonaType.Casual, 14),
        (PersonaType.MessyHybrid, 12),
        (PersonaType.CluelessNewbie, 9),
        (PersonaType.GreedyBust, 5)
    ]);

    /// <summary>
    /// 深水区补齐专用高竞争后期画像循环序列（80% 强竞技后期画像 + 20% Casual/MessyHybrid），
    /// 专用于补足 Day 14/15+ 高难度构筑厚度，不影响主淘汰赛自然漏斗配比。
    /// </summary>
    private static readonly PersonaType[] DeepWaterCompetitiveCycle100 = BuildInterleavedPersonaCycle(
    [
        (PersonaType.HardForcer, 25),
        (PersonaType.FlexPivot, 20),
        (PersonaType.GreedyEco, 20),
        (PersonaType.TempoAggro, 15),
        (PersonaType.Casual, 12),
        (PersonaType.MessyHybrid, 8)
    ]);

    private readonly GameDataRepository _repo;
    private readonly IArchetypeCatalog _catalog;
    private readonly IFastRunSimulator _simulator;
    private readonly IBoardValidator _validator;

    public TournamentRunner(
        GameDataRepository repo,
        IArchetypeCatalog catalog,
        IFastRunSimulator simulator,
        IBoardValidator validator)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// 判断是否属于低分/新手/失误组画像（Casual / MessyHybrid / CluelessNewbie / GreedyBust）。
    /// </summary>
    public static bool IsLowTierPersona(PersonaType persona) => persona is
        PersonaType.Casual or
        PersonaType.MessyHybrid or
        PersonaType.CluelessNewbie or
        PersonaType.GreedyBust;

    /// <summary>
    /// 使用平滑赤字调度（Bresenham 步进）构建按指定权重交错的 100 槽画像序列，
    /// 确保无论全量规模还是小样本测试切片均稳定维持精确占比且不发生随机聚簇。
    /// </summary>
    private static PersonaType[] BuildInterleavedPersonaCycle((PersonaType Persona, int Weight)[] weights)
    {
        var counts = new int[weights.Length];
        var result = new PersonaType[100];
        for (int step = 0; step < 100; step++)
        {
            int bestIdx = 0;
            double bestDeficit = double.MinValue;
            for (int i = 0; i < weights.Length; i++)
            {
                if (counts[i] >= weights[i].Weight) continue;
                double expected = weights[i].Weight * (step + 1) / 100.0;
                double deficit = expected - counts[i];
                if (deficit > bestDeficit)
                {
                    bestDeficit = deficit;
                    bestIdx = i;
                }
            }
            result[step] = weights[bestIdx].Persona;
            counts[bestIdx]++;
        }
        return result;
    }

    /// <summary>
    /// 执行主淘汰赛与深水区补齐流程，产出全部完整 Run 与单日盘面快照。
    /// </summary>
    public TournamentResult Run(DatasetGenOptions options, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var heroes = HeroNames.PlayableHeroes;

        var fastOptions = new FastRunOptions
        {
            MaxDay = options.MaxDay,
            VictoriesToWin = 10,
            AllowEndlessAfterVictory = false,
            RequireOfficialZhCards = options.RequireOfficialZh
        };

        // 1. 构建主淘汰赛参赛名单（8 英雄均分，每英雄内严格按 8 画像天梯生态比例分配）
        var participants = CreateParticipants(
            count: options.TournamentSize,
            idPrefix: "run",
            heroes: heroes,
            baseSeed: options.Seed,
            startIndex: 0);

        log?.Invoke($"[淘汰赛] 主赛初始化完成：{participants.Count} 局（{heroes.Length} 英雄均分，8 画像天梯生态，{options.MaxDegreeOfParallelism} 线程并发）");

        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };

        // 按天桶记录已有实战盘面以备单人轮空或深水区孤儿匹配镜像对手
        var fallbackPoolByDayBucket = new Dictionary<int, List<PvpBoardSnapshot>>();
        for (int b = DayBucketHelper.MinDayBucket; b <= DayBucketHelper.MaxDayBucket; b++)
            fallbackPoolByDayBucket[b] = new List<PvpBoardSnapshot>();

        // 2. 逐日推进主淘汰赛
        RunSeasonDays(participants, fastOptions, options, parallelOpts, fallbackPoolByDayBucket, log, phaseLabel: "主淘汰赛");

        var allRuns = new List<PvpRunRecord>(participants.Count);
        foreach (var p in participants)
        {
            p.IsFinished = true;
            allRuns.Add(_simulator.BuildRunRecord(p, _repo));
        }

        // 3. 统计 15 天桶 × 8 英雄唯一签名覆盖数，按需追加深水区（Day 13~15+ 及任何稀缺格）真实发育批次
        int supplementalCount = 0;
        var sigCoverage = BuildSignatureCoverage(allRuns);
        const int maxSupplementalWaves = 5;

        for (int wave = 1; wave <= maxSupplementalWaves; wave++)
        {
            var deficitByHero = FindHeroDeficits(sigCoverage, heroes, options.MinCandidatesPerDayHeroCell);
            if (deficitByHero.Count == 0)
                break;

            var supSpecs = new List<(string Hero, PersonaType Persona, int Index)>();
            int idxBase = options.TournamentSize + supplementalCount;
            foreach (var (hero, maxDeficit) in deficitByHero)
            {
                int multiplier = options.MinCandidatesPerDayHeroCell >= 40 ? 5 : 3;
                int batchPerHero = Math.Clamp(maxDeficit * multiplier, Math.Min(32, options.MinCandidatesPerDayHeroCell), 1500);
                for (int k = 0; k < batchPerHero; k++)
                {
                    var persona = DeepWaterCompetitiveCycle100[(k + wave * 13) % DeepWaterCompetitiveCycle100.Length];
                    supSpecs.Add((hero, persona, idxBase + supSpecs.Count));
                }
            }

            if (supSpecs.Count == 0)
                break;

            string deepTargetInfo = options.MinCandidatesPerDayHeroCell >= 40
                ? $"{options.MinCandidatesPerDayHeroCell}（Day 14>={Math.Max(options.MinCandidatesPerDayHeroCell, 120)}, Day 15+>={Math.Max(options.MinCandidatesPerDayHeroCell, 240)}）"
                : $"{options.MinCandidatesPerDayHeroCell}";
            log?.Invoke($"[深水区补齐 第 {wave} 轮] 检测到 {deficitByHero.Count} 个英雄在部分天桶候选不足 {deepTargetInfo}，追加 {supSpecs.Count} 局高竞争无尽发育（AllowEndlessAfterVictory=true）");

            var supParticipants = CreateParticipantsFromSpecs(supSpecs, "run_sup", options.Seed ^ (wave * 131071));
            var endlessOptions = new FastRunOptions
            {
                MaxDay = options.MaxDay,
                VictoriesToWin = 10,
                AllowEndlessAfterVictory = true,
                RequireOfficialZhCards = options.RequireOfficialZh
            };

            RunSeasonDays(supParticipants, endlessOptions, options, parallelOpts, fallbackPoolByDayBucket, log, phaseLabel: $"深水补齐#{wave}");

            int addedSigsBefore = CountTotalUniqueSignatures(sigCoverage);
            foreach (var sp in supParticipants)
            {
                sp.IsFinished = true;
                var rec = _simulator.BuildRunRecord(sp, _repo);
                allRuns.Add(rec);
                supplementalCount++;
                MergeRunSignatures(sigCoverage, rec);
            }
            int addedSigsAfter = CountTotalUniqueSignatures(sigCoverage);
            if (addedSigsAfter == addedSigsBefore)
                break;
        }

        // 4. 汇总去重全量盘面快照
        var snapshotMap = new Dictionary<string, PvpBoardSnapshot>(StringComparer.Ordinal);
        foreach (var run in allRuns)
        {
            foreach (var snap in run.DayBoards)
            {
                if (string.IsNullOrEmpty(snap.BoardSignature))
                    snap.BoardSignature = _validator.ComputeSignature(snap);
                snapshotMap[snap.BoardId] = snap;
            }
        }

        sw.Stop();
        log?.Invoke($"[淘汰赛] 完成：总 Run={allRuns.Count}（主赛 {participants.Count} + 深水补齐 {supplementalCount}），总快照={snapshotMap.Count}，耗时 {sw.ElapsedMilliseconds} ms");

        return new TournamentResult
        {
            AllRuns = allRuns,
            AllSnapshots = snapshotMap.Values.ToList(),
            MainTournamentRunCount = participants.Count,
            SupplementalRunCount = supplementalCount,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    private List<RunParticipantState> CreateParticipants(
        int count,
        string idPrefix,
        string[] heroes,
        int baseSeed,
        int startIndex)
    {
        var specs = new List<(string Hero, PersonaType Persona, int Index)>(count);
        for (int i = 0; i < count; i++)
        {
            string hero = heroes[i % heroes.Length];
            int heroSeq = i / heroes.Length;
            PersonaType persona = LadderPersonaCycle100[heroSeq % LadderPersonaCycle100.Length];
            specs.Add((hero, persona, startIndex + i));
        }
        return CreateParticipantsFromSpecs(specs, idPrefix, baseSeed);
    }

    private List<RunParticipantState> CreateParticipantsFromSpecs(
        List<(string Hero, PersonaType Persona, int Index)> specs,
        string idPrefix,
        int baseSeed)
    {
        var list = new List<RunParticipantState>(specs.Count);
        foreach (var (hero, persona, idx) in specs)
        {
            int seed = unchecked(baseSeed * 397 + idx * 7919 + 17);
            var archRng = new Random(seed ^ 0x5A17);
            var archetype = _catalog.PickForHero(hero, persona, archRng);
            string runId = $"{idPrefix}_{idx:000000}";
            var participant = _simulator.CreateParticipant(runId, hero, persona, archetype, seed, _repo);
            list.Add(participant);
        }
        return list;
    }

    private void RunSeasonDays(
        List<RunParticipantState> participants,
        FastRunOptions fastOptions,
        DatasetGenOptions options,
        ParallelOptions parallelOpts,
        Dictionary<int, List<PvpBoardSnapshot>> fallbackPoolByDayBucket,
        Action<string>? log,
        string phaseLabel)
    {
        for (int day = 1; day <= fastOptions.MaxDay; day++)
        {
            var active = participants.Where(p => !p.IsFinished).ToList();
            if (active.Count == 0)
                break;

            // 步骤 A：并发白天发育推进至晚间 PvP 前
            var daySnapshots = new PvpBoardSnapshot[active.Count];
            Parallel.For(0, active.Count, parallelOpts, i =>
            {
                var p = active[i];
                var snap = _simulator.AdvanceOneDayToPvp(p, day, _repo, _catalog, fastOptions);
                if (string.IsNullOrEmpty(snap.BoardSignature))
                    snap.BoardSignature = _validator.ComputeSignature(snap);
                daySnapshots[i] = snap;
            });

            int bucket = DayBucketHelper.GetDayBucket(day);
            var fallbackBucketList = fallbackPoolByDayBucket[bucket];

            // 步骤 B：对齐官方《The Bazaar》异步天梯规则 —— 同天数全池纯随机匹配对手镜像（不按胜场分房保护低分画像）
            var dayRng = new Random(unchecked(options.Seed ^ (day * 10007) ^ phaseLabel.GetHashCode()));
            var order = Enumerable.Range(0, active.Count)
                .Select(i => (Index: i, TieBreaker: dayRng.Next()))
                .OrderBy(x => x.TieBreaker)
                .Select(x => x.Index)
                .ToArray();

            var pairs = new List<(int IdxA, int IdxB, PvpBoardSnapshot? GhostB)>(active.Count / 2 + 1);
            for (int k = 0; k + 1 < order.Length; k += 2)
                pairs.Add((order[k], order[k + 1], null));

            if (order.Length % 2 == 1)
            {
                int soloIdx = order[^1];
                PvpBoardSnapshot ghostSnap;
                if (order.Length > 1)
                    ghostSnap = daySnapshots[order[dayRng.Next(order.Length - 1)]];
                else if (fallbackBucketList.Count > 0)
                    ghostSnap = fallbackBucketList[dayRng.Next(fallbackBucketList.Count)];
                else
                    ghostSnap = daySnapshots[soloIdx];

                pairs.Add((soloIdx, -1, ghostSnap));
            }

            if (fallbackBucketList.Count < 2000)
            {
                int take = Math.Min(daySnapshots.Length, 2000 - fallbackBucketList.Count);
                for (int i = 0; i < take; i++)
                    fallbackBucketList.Add(daySnapshots[i]);
            }

            // 步骤 C：并发执行 CombatSimulator 晚间决斗并应用胜负
            Parallel.For(0, pairs.Count, parallelOpts, pIdx =>
            {
                var (idxA, idxB, ghostB) = pairs[pIdx];
                var snapA = daySnapshots[idxA];
                var snapB = idxB >= 0 ? daySnapshots[idxB] : ghostB!;

                var stateA = _validator.ToPlayerState(snapA, _repo);
                var rawStateB = _validator.ToPlayerState(snapB, _repo);
                var stateB = DayBucketHelper.EnsureDistinctOpponentState(stateA, rawStateB);

                int combatSeed = unchecked(options.Seed * 31 + day * 997 + pIdx * 13);
                var result = DayBucketHelper.SimulateHeadless(_repo, stateA, stateB, combatSeed, "PVP");
                bool aWon = DayBucketHelper.DidPlayer1Win(result, stateA.PlayerId);

                var outcomeA = new PvpMatchOutcome
                {
                    OpponentBoardId = snapB.BoardId,
                    Won = aWon,
                    CombatDurationMs = result.DurationMs,
                    RemainingHp = Math.Max(0, result.Player1RemainingHp),
                    DamageDealt = result.Player1TotalDamageDealt,
                    ShieldGained = result.Player1TotalShieldGained
                };
                _simulator.ApplyPvpOutcome(active[idxA], snapA, outcomeA, _repo, fastOptions);
                snapA.MatchOutcome ??= outcomeA;

                if (idxB >= 0)
                {
                    var outcomeB = new PvpMatchOutcome
                    {
                        OpponentBoardId = snapA.BoardId,
                        Won = !aWon,
                        CombatDurationMs = result.DurationMs,
                        RemainingHp = Math.Max(0, result.Player2RemainingHp),
                        DamageDealt = result.Player2TotalDamageDealt,
                        ShieldGained = result.Player2TotalShieldGained
                    };
                    _simulator.ApplyPvpOutcome(active[idxB], snapB, outcomeB, _repo, fastOptions);
                    snapB.MatchOutcome ??= outcomeB;
                }
            });

            // 每日输出存活局数、低分/失误画像占比与实战对决场次，清晰体现自然淘汰漏斗
            int lowTierCount = 0;
            for (int i = 0; i < daySnapshots.Length; i++)
            {
                if (IsLowTierPersona(daySnapshots[i].Persona))
                    lowTierCount++;
            }
            double lowPct = daySnapshots.Length > 0 ? 100.0 * lowTierCount / daySnapshots.Length : 0.0;
            log?.Invoke($"  [{phaseLabel}] Day {day:00}: 参战 {active.Count,5} 局（低分/失误画像 {lowPct,5:F1}% = {lowTierCount,5}），对决 {pairs.Count,5} 场完成");
        }
    }

    private Dictionary<(int DayBucket, string Hero), HashSet<string>> BuildSignatureCoverage(IEnumerable<PvpRunRecord> runs)
    {
        var map = new Dictionary<(int DayBucket, string Hero), HashSet<string>>();
        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            foreach (var h in HeroNames.PlayableHeroes)
                map[(b, h)] = new HashSet<string>(StringComparer.Ordinal);
        }

        foreach (var run in runs)
            MergeRunSignatures(map, run);

        return map;
    }

    private void MergeRunSignatures(Dictionary<(int DayBucket, string Hero), HashSet<string>> map, PvpRunRecord run)
    {
        foreach (var snap in run.DayBoards)
        {
            int b = DayBucketHelper.GetDayBucket(snap.Day);
            if (!map.TryGetValue((b, snap.Hero), out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                map[(b, snap.Hero)] = set;
            }
            string sig = string.IsNullOrEmpty(snap.BoardSignature) ? _validator.ComputeSignature(snap) : snap.BoardSignature;
            snap.BoardSignature = sig;
            set.Add(sig);
        }
    }

    private static List<(string Hero, int MaxDeficit)> FindHeroDeficits(
        Dictionary<(int DayBucket, string Hero), HashSet<string>> coverage,
        string[] heroes,
        int targetPerCell)
    {
        var result = new List<(string Hero, int MaxDeficit)>();
        foreach (var hero in heroes)
        {
            int maxDeficit = 0;
            foreach (int b in DayBucketHelper.AllDayBuckets)
            {
                int effectiveTarget = targetPerCell;
                if (targetPerCell >= 40)
                {
                    if (b >= 15)
                        effectiveTarget = Math.Max(targetPerCell, 240);
                    else if (b == 14)
                        effectiveTarget = Math.Max(targetPerCell, 120);
                }
                int count = coverage.TryGetValue((b, hero), out var set) ? set.Count : 0;
                if (count < effectiveTarget)
                    maxDeficit = Math.Max(maxDeficit, effectiveTarget - count);
            }
            if (maxDeficit > 0)
                result.Add((hero, maxDeficit));
        }
        return result;
    }

    private static int CountTotalUniqueSignatures(Dictionary<(int DayBucket, string Hero), HashSet<string>> coverage)
    {
        int sum = 0;
        foreach (var set in coverage.Values)
            sum += set.Count;
        return sum;
    }
}

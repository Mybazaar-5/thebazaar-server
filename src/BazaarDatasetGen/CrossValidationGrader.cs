using System.Collections.Concurrent;
using System.Diagnostics;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Models;
using BazaarCore.Runtime;

namespace BazaarDatasetGen;

/// <summary>
/// 交叉定级基准考官条目：统一封装淘汰赛实战合成盘与 Day 10+ 5.0.0 社区 10 胜参考盘。
/// </summary>
public sealed class BenchmarkOpponent
{
    public required string BenchmarkId { get; init; }
    public required string Hero { get; init; }
    public required bool IsCommunityBlueprint { get; init; }
    public PvpBoardSnapshot? Snapshot { get; init; }
    public PlayerState? PreparedState { get; init; }
}

/// <summary>
/// 交叉验证定级阶段的输出结果：包含已定级的全量去重盘面与各天桶基准考官集合（供后续按需补定级）。
/// </summary>
public sealed class CrossValidationResult
{
    public required IReadOnlyList<PvpBoardSnapshot> GradedUniqueBoards { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<PvpBoardSnapshot>> BenchmarksByDayBucket { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<BenchmarkOpponent>> MixedBenchmarksByDayBucket { get; init; }
    public required IReadOnlyDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> RatingCache { get; init; }
    public int TotalMatchesSimulated { get; init; }
    public int CommunityBenchmarkSlotsUsed { get; init; }
    public long ElapsedMs { get; init; }
}

/// <summary>
/// 同天数循环交叉胜率定级器：
/// 按 15 个天桶（Day 1..14 及 Day 15+）分别抽样全英雄分层基准对手，
/// 其中 Day 10+ 考官池按 20%（默认 4/20）比例混入 5.0.0 社区 10 胜参考蓝本（防止水货靠混天数拿 MetaBoss），
/// 通过并发 CombatSimulator 实战计算 EstimatedWinRate 并严格划分四档强度，
/// 针对 15 × 8 × 4 = 480 个网格执行稀缺格自动扩样补强。
/// </summary>
public sealed class CrossValidationGrader
{
    private readonly GameDataRepository _repo;
    private readonly IBoardValidator _validator;

    public CrossValidationGrader(GameDataRepository repo, IBoardValidator validator)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// 对淘汰赛产出的盘面按 15 天桶执行同天交叉胜率定级并回填全量快照。
    /// </summary>
    public CrossValidationResult Grade(
        IReadOnlyList<PvpBoardSnapshot> allSnapshots,
        DatasetGenOptions options,
        Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };

        // 确保全部快照均已计算稳定指纹
        foreach (var snap in allSnapshots)
        {
            if (string.IsNullOrEmpty(snap.BoardSignature))
                snap.BoardSignature = _validator.ComputeSignature(snap);
        }

        // 按天桶聚合并按 (Hero, Signature) 去重（同天桶同英雄同签名只需评估代表样本即可传播结果）
        var snapshotsByBucket = new Dictionary<int, List<PvpBoardSnapshot>>();
        var uniqueByBucketHero = new Dictionary<(int DayBucket, string Hero), List<PvpBoardSnapshot>>();
        var sigGroupsByBucketHero = new Dictionary<(int DayBucket, string Hero, string Signature), List<PvpBoardSnapshot>>();

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            snapshotsByBucket[b] = new List<PvpBoardSnapshot>();
            foreach (var h in HeroNames.PlayableHeroes)
                uniqueByBucketHero[(b, h)] = new List<PvpBoardSnapshot>();
        }

        foreach (var snap in allSnapshots)
        {
            int b = DayBucketHelper.GetDayBucket(snap.Day);
            snapshotsByBucket[b].Add(snap);

            var key = (b, snap.Hero, snap.BoardSignature!);
            if (!sigGroupsByBucketHero.TryGetValue(key, out var group))
            {
                group = new List<PvpBoardSnapshot>();
                sigGroupsByBucketHero[key] = group;
                if (!uniqueByBucketHero.TryGetValue((b, snap.Hero), out var heroList))
                {
                    heroList = new List<PvpBoardSnapshot>();
                    uniqueByBucketHero[(b, snap.Hero)] = heroList;
                }
                heroList.Add(snap);
            }
            group.Add(snap);
        }

        var benchmarksByBucket = new Dictionary<int, IReadOnlyList<PvpBoardSnapshot>>();
        var mixedBenchmarksByBucket = new Dictionary<int, IReadOnlyList<BenchmarkOpponent>>();
        var ratingCache = new ConcurrentDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)>();
        var gradedRepresentatives = new List<PvpBoardSnapshot>();
        int totalMatches = 0;
        int communitySlotsTotal = 0;

        // 每个 (dayBucket, hero, powerBucket) 期望覆盖的最低样本数，确保 480 格满覆盖且支撑均衡池采样
        int targetPerSubCell = Math.Max(4, (int)Math.Ceiling(options.PoolPerDay / 32.0) + 2);
        int initialPerHero = Math.Max(options.MinCandidatesPerDayHeroCell, targetPerSubCell * 3);

        foreach (int dayBucket in DayBucketHelper.AllDayBuckets)
        {
            var uniqueInDay = new List<PvpBoardSnapshot>();
            foreach (var h in HeroNames.PlayableHeroes)
            {
                if (uniqueByBucketHero.TryGetValue((dayBucket, h), out var list))
                    uniqueInDay.AddRange(list);
            }

            if (uniqueInDay.Count == 0)
            {
                benchmarksByBucket[dayBucket] = Array.Empty<PvpBoardSnapshot>();
                mixedBenchmarksByBucket[dayBucket] = Array.Empty<BenchmarkOpponent>();
                continue;
            }

            // 1. 构建当日基准考官池（Day < 10 为全英雄分层合成盘；Day >= 10 按 25% 混入 5.0.0 社区 10 胜参考盘）
            var (mixedBenchmarks, syntheticSnapshots, communitySeats) = BuildDayBenchmarkPool(
                uniqueByBucketHero, dayBucket, options.BenchmarkOpponentsPerDay, options.Seed);
            benchmarksByBucket[dayBucket] = syntheticSnapshots;
            mixedBenchmarksByBucket[dayBucket] = mixedBenchmarks;
            communitySlotsTotal += communitySeats;

            // 2. 为每个英雄准备按启发式强度排序并分层打散的待测候选队列
            var testedSigSet = new HashSet<string>(StringComparer.Ordinal);
            var heroCandidates = new Dictionary<string, List<PvpBoardSnapshot>>(StringComparer.OrdinalIgnoreCase);
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                var raw = uniqueByBucketHero.TryGetValue((dayBucket, hero), out var list) ? list : new List<PvpBoardSnapshot>();
                heroCandidates[hero] = OrderCandidatesStratified(raw);
            }

            var bucketGridCounts = new Dictionary<(string Hero, PowerBucket Bucket), int>();
            foreach (var hero in HeroNames.PlayableHeroes)
                foreach (var pb in DayBucketHelper.AllPowerBuckets)
                    bucketGridCounts[(hero, pb)] = 0;

            // 2A. 首轮基础批：每个英雄抽取 initialPerHero 个分层样本定级，并全量纳入全部 real_community 真值盘面确保精准定级
            var batch = new List<PvpBoardSnapshot>();
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                var list = heroCandidates[hero];
                int take = Math.Min(initialPerHero, list.Count);
                for (int i = 0; i < take; i++)
                {
                    var cand = list[i];
                    if (testedSigSet.Add(cand.BoardSignature!))
                        batch.Add(cand);
                }
                foreach (var cand in list)
                {
                    if (string.Equals(cand.Source, "real_community", StringComparison.OrdinalIgnoreCase)
                        && testedSigSet.Add(cand.BoardSignature!))
                    {
                        batch.Add(cand);
                    }
                }
            }

            totalMatches += EvaluateBatchAndRecord(
                batch, mixedBenchmarks, dayBucket, options, parallelOpts,
                ratingCache, bucketGridCounts, gradedRepresentatives);

            // 2B. 480 格满覆盖自补强：针对任意不足或空缺的 (hero, powerBucket) 从两端/剩余去重池动态扩样
            const int maxExpansionWaves = 4;
            for (int wave = 1; wave <= maxExpansionWaves; wave++)
            {
                var expansionBatch = new List<PvpBoardSnapshot>();
                foreach (var hero in HeroNames.PlayableHeroes)
                {
                    var list = heroCandidates[hero];
                    bool needWeak = bucketGridCounts[(hero, PowerBucket.Weak)] < targetPerSubCell;
                    bool needMeta = bucketGridCounts[(hero, PowerBucket.MetaBoss)] < targetPerSubCell;
                    bool needMid = bucketGridCounts[(hero, PowerBucket.Normal)] < targetPerSubCell
                                || bucketGridCounts[(hero, PowerBucket.Strong)] < targetPerSubCell;

                    if (!needWeak && !needMeta && !needMid)
                        continue;

                    int waveBudget = wave == maxExpansionWaves ? 200 : 80;
                    var picked = PickTargetedExpansion(list, waveBudget, needWeak, needMeta, testedSigSet);
                    expansionBatch.AddRange(picked);
                }

                if (expansionBatch.Count == 0)
                    break;

                totalMatches += EvaluateBatchAndRecord(
                    expansionBatch, mixedBenchmarks, dayBucket, options, parallelOpts,
                    ratingCache, bucketGridCounts, gradedRepresentatives);
            }

            // 2C. 第 5 波零空缺分批早停清扫：只要仍存在任意 0 样本子单元，按每批 64（累计上限 640）定向补测直到达 >= 2 早停
            totalMatches += RescueEmptyHeroGridCells(
                heroCandidates,
                mixedBenchmarks,
                dayBucket,
                options,
                parallelOpts,
                testedSigSet,
                ratingCache,
                bucketGridCounts,
                gradedRepresentatives);

            // 将定级结果同步广播回同天桶中同英雄同签名的所有快照
            foreach (var snap in snapshotsByBucket[dayBucket])
            {
                if (ratingCache.TryGetValue((dayBucket, snap.Hero, snap.BoardSignature!), out var rating))
                {
                    snap.EstimatedWinRate = rating.WinRate;
                    snap.PowerBucket = rating.Bucket;
                }
            }

            string commTag = communitySeats > 0 ? $", 考官={mixedBenchmarks.Count}(含社区10胜:{communitySeats})" : $", 考官={mixedBenchmarks.Count}";
            log?.Invoke($"  [交叉定级] {DayBucketHelper.GetDayLabel(dayBucket),-8}: 去重盘面={uniqueInDay.Count,5}, 已实测={testedSigSet.Count,4}{commTag}, 累计对局={totalMatches,7}");
        }

        sw.Stop();
        log?.Invoke($"[交叉定级] 完成：共实测 {gradedRepresentatives.Count} 个唯一签名，模拟 {totalMatches} 场战斗，高天混入社区10胜考官席位={communitySlotsTotal}，耗时 {sw.ElapsedMilliseconds} ms");

        return new CrossValidationResult
        {
            GradedUniqueBoards = gradedRepresentatives,
            BenchmarksByDayBucket = benchmarksByBucket,
            MixedBenchmarksByDayBucket = mixedBenchmarksByBucket,
            RatingCache = ratingCache,
            TotalMatchesSimulated = totalMatches,
            CommunityBenchmarkSlotsUsed = communitySlotsTotal,
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// 兼容旧签名：对未缓存快照列表执行 8 英雄分层补测（自动按需适配社区 10 胜参考盘）。
    /// </summary>
    public int GradeMissingSnapshots(
        IEnumerable<PvpBoardSnapshot> snapshots,
        IReadOnlyDictionary<int, IReadOnlyList<PvpBoardSnapshot>> benchmarksByDayBucket,
        IDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> ratingCache,
        DatasetGenOptions options,
        Action<string>? log = null)
    {
        var wrapped = new Dictionary<int, IReadOnlyList<BenchmarkOpponent>>(benchmarksByDayBucket.Count);
        foreach (var (bucket, list) in benchmarksByDayBucket)
        {
            var mixed = list.Select(s => new BenchmarkOpponent
            {
                BenchmarkId = s.BoardId,
                Hero = s.Hero,
                IsCommunityBlueprint = false,
                Snapshot = s
            }).ToList();

            // 若传入的是 Day >= 10 且尚无社区考官，自动按 25% 混入当日社区 10 胜参考盘
            if (bucket >= 10 && mixed.Count > 0)
                mixed = MixCommunityIntoExistingPool(mixed, bucket, options.Seed);

            wrapped[bucket] = mixed;
        }
        return GradeMissingSnapshots(snapshots, wrapped, ratingCache, options, log);
    }

    /// <summary>
    /// 对尚未经交叉实战定级的快照执行 4 场全分段分层补测（Weak/Normal/Strong/MetaBoss 各 1 位尽量不同英雄，
    /// Day 10+ 固定含 1 位（25%）社区 10 胜考官，显著加速补测且保持四档精确可达）。
    /// </summary>
    public int GradeMissingSnapshots(
        IEnumerable<PvpBoardSnapshot> snapshots,
        IReadOnlyDictionary<int, IReadOnlyList<BenchmarkOpponent>> mixedBenchmarksByDayBucket,
        IDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> ratingCache,
        DatasetGenOptions options,
        Action<string>? log = null)
    {
        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        var toGradeByBucket = new Dictionary<int, Dictionary<(string Hero, string Signature), PvpBoardSnapshot>>();
        var pendingAll = new List<PvpBoardSnapshot>();

        foreach (var snap in snapshots)
        {
            if (string.IsNullOrEmpty(snap.BoardSignature))
                snap.BoardSignature = _validator.ComputeSignature(snap);

            int b = DayBucketHelper.GetDayBucket(snap.Day);
            if (ratingCache.TryGetValue((b, snap.Hero, snap.BoardSignature), out var cached))
            {
                snap.EstimatedWinRate = cached.WinRate;
                snap.PowerBucket = cached.Bucket;
                continue;
            }

            pendingAll.Add(snap);
            if (!toGradeByBucket.TryGetValue(b, out var map))
            {
                map = new Dictionary<(string Hero, string Signature), PvpBoardSnapshot>();
                toGradeByBucket[b] = map;
            }
            map.TryAdd((snap.Hero, snap.BoardSignature), snap);
        }

        if (pendingAll.Count == 0)
            return 0;

        int matches = 0;
        var dummyCounts = new Dictionary<(string Hero, PowerBucket Bucket), int>();
        var dummyList = new List<PvpBoardSnapshot>();
        var concurrentCache = new ConcurrentDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)>(ratingCache);

        foreach (var (bucket, sigMap) in toGradeByBucket)
        {
            if (!mixedBenchmarksByDayBucket.TryGetValue(bucket, out var fullBenchmarks) || fullBenchmarks.Count == 0)
                continue;

            // 补测提速：从 Weak/Normal/Strong/MetaBoss 四档各挑 1 位尽量不同英雄的考官（Day 10+ 固定含 1 位社区 10 胜考官）
            var fastBenchmarks = SelectFourTierStratifiedBenchmarks(fullBenchmarks, bucket, concurrentCache);

            matches += EvaluateBatchAndRecord(
                sigMap.Values.ToList(),
                fastBenchmarks,
                bucket,
                options,
                parallelOpts,
                concurrentCache,
                dummyCounts,
                dummyList);
        }

        foreach (var kv in concurrentCache)
            ratingCache[kv.Key] = kv.Value;

        foreach (var snap in pendingAll)
        {
            int b = DayBucketHelper.GetDayBucket(snap.Day);
            if (concurrentCache.TryGetValue((b, snap.Hero, snap.BoardSignature!), out var rating))
            {
                snap.EstimatedWinRate = rating.WinRate;
                snap.PowerBucket = rating.Bucket;
            }
        }

        log?.Invoke($"[补充定级] 为入选 Run 与自然池额外补测 {toGradeByBucket.Values.Sum(m => m.Count)} 个未缓存签名，新增实战 {matches} 场");
        return matches;
    }

    private int EvaluateBatchAndRecord(
        List<PvpBoardSnapshot> batch,
        IReadOnlyList<BenchmarkOpponent> benchmarks,
        int dayBucket,
        DatasetGenOptions options,
        ParallelOptions parallelOpts,
        ConcurrentDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> ratingCache,
        Dictionary<(string Hero, PowerBucket Bucket), int> bucketGridCounts,
        List<PvpBoardSnapshot> gradedRepresentatives)
    {
        if (batch.Count == 0 || benchmarks.Count == 0)
            return 0;

        // 每个天桶的基准考官状态提前构建一次（带独立 __bench ID，CombatSimulator 内部 CloneForCombat 只读线程安全）
        var benchStates = new (string BenchmarkId, PlayerState State)[benchmarks.Count];
        for (int bIdx = 0; bIdx < benchmarks.Count; bIdx++)
        {
            var bench = benchmarks[bIdx];
            var rawState = bench.PreparedState ?? _validator.ToPlayerState(bench.Snapshot!, _repo);
            var distinctState = DayBucketHelper.CloneWithPlayerId(rawState, $"{bench.BenchmarkId}__bench_{bIdx}");
            benchStates[bIdx] = (bench.BenchmarkId, distinctState);
        }

        var results = new (double WinRate, PowerBucket Bucket, int MatchesRun)[batch.Count];

        Parallel.For(0, batch.Count, parallelOpts, idx =>
        {
            var cand = batch[idx];
            var stateCand = _validator.ToPlayerState(cand, _repo);
            double wins = 0.0;
            int executed = 0;

            for (int bIdx = 0; bIdx < benchStates.Length; bIdx++)
            {
                var (benchId, stateBench) = benchStates[bIdx];
                if (string.Equals(cand.BoardId, benchId, StringComparison.Ordinal))
                {
                    wins += 0.5;
                    continue;
                }

                int seed = unchecked(options.Seed * 17 + dayBucket * 977 + idx * 131 + bIdx * 19);

                // 交替先手位消除极少数同死回合的 P1 偏置
                if ((bIdx & 1) == 0)
                {
                    var res = DayBucketHelper.SimulateHeadless(_repo, stateCand, stateBench, seed, "PVP");
                    if (DayBucketHelper.DidPlayer1Win(res, stateCand.PlayerId))
                        wins += 1.0;
                }
                else
                {
                    var res = DayBucketHelper.SimulateHeadless(_repo, stateBench, stateCand, seed, "PVP");
                    if (!DayBucketHelper.DidPlayer1Win(res, stateBench.PlayerId))
                        wins += 1.0;
                }
                executed++;
            }

            double winRate = wins / benchStates.Length;
            var pb = DayBucketHelper.ClassifyPowerBucket(winRate);
            results[idx] = (winRate, pb, executed);
        });

        int totalExecuted = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            var cand = batch[i];
            var (wr, pb, executed) = results[i];
            totalExecuted += executed;

            cand.EstimatedWinRate = wr;
            cand.PowerBucket = pb;
            ratingCache[(dayBucket, cand.Hero, cand.BoardSignature!)] = (wr, pb);
            gradedRepresentatives.Add(cand);

            var gridKey = (cand.Hero, pb);
            bucketGridCounts[gridKey] = bucketGridCounts.GetValueOrDefault(gridKey) + 1;
        }

        return totalExecuted;
    }

    private (IReadOnlyList<BenchmarkOpponent> Mixed, IReadOnlyList<PvpBoardSnapshot> SyntheticSnapshots, int CommunitySeats) BuildDayBenchmarkPool(
        Dictionary<(int DayBucket, string Hero), List<PvpBoardSnapshot>> uniqueByBucketHero,
        int dayBucket,
        int targetCount,
        int seed)
    {
        if (dayBucket < 10)
        {
            var synOnly = SelectDayBenchmarks(uniqueByBucketHero, dayBucket, targetCount);
            var mixedSyn = synOnly.Select(s => new BenchmarkOpponent
            {
                BenchmarkId = s.BoardId,
                Hero = s.Hero,
                IsCommunityBlueprint = false,
                Snapshot = s
            }).ToList();
            return (mixedSyn, synOnly, 0);
        }

        // Day >= 10：在总计 targetCount（默认 20）席位中，保留 80%（16 位）全英雄对称合成盘，混入 20%（4 位）5.0.0 社区 10 胜参考盘
        int communitySeats = ComputeCommunitySeatCount(targetCount);
        var communityPicked = PickCommunityBenchmarksForDay(dayBucket, communitySeats, seed);

        int syntheticTarget = Math.Max(1, targetCount - communityPicked.Count);
        var syntheticSnapshots = SelectDayBenchmarks(uniqueByBucketHero, dayBucket, syntheticTarget);

        var mixed = new List<BenchmarkOpponent>(syntheticSnapshots.Count + communityPicked.Count);
        foreach (var s in syntheticSnapshots)
        {
            mixed.Add(new BenchmarkOpponent
            {
                BenchmarkId = s.BoardId,
                Hero = s.Hero,
                IsCommunityBlueprint = false,
                Snapshot = s
            });
        }
        mixed.AddRange(communityPicked);

        return (mixed, syntheticSnapshots, communityPicked.Count);
    }

    private static int ComputeCommunitySeatCount(int desiredTotal)
    {
        if (desiredTotal <= 1)
            return 0;
        int maxAllowed = desiredTotal <= 4 ? desiredTotal - 1 : Math.Max(1, desiredTotal - 4);
        return Math.Clamp((int)Math.Round(desiredTotal * 0.20), 1, maxAllowed);
    }

    private List<BenchmarkOpponent> MixCommunityIntoExistingPool(
        List<BenchmarkOpponent> existingSynthetic,
        int dayBucket,
        int seed)
    {
        int total = existingSynthetic.Count;
        int communitySeats = ComputeCommunitySeatCount(total);
        var communityPicked = PickCommunityBenchmarksForDay(dayBucket, communitySeats, seed);
        if (communityPicked.Count == 0)
            return existingSynthetic;

        int keepSynthetic = Math.Max(1, total - communityPicked.Count);
        var result = new List<BenchmarkOpponent>(keepSynthetic + communityPicked.Count);
        for (int i = 0; i < keepSynthetic && i < existingSynthetic.Count; i++)
        {
            int idx = (int)Math.Floor(i * (existingSynthetic.Count / (double)keepSynthetic));
            result.Add(existingSynthetic[Math.Clamp(idx, 0, existingSynthetic.Count - 1)]);
        }
        result.AddRange(communityPicked);
        return result;
    }

    private List<BenchmarkOpponent> PickCommunityBenchmarksForDay(int dayBucket, int count, int seed)
    {
        if (count <= 0)
            return new List<BenchmarkOpponent>();

        List<PlayerState> refBoards;
        try
        {
            refBoards = CommunityTenWinBlueprints.BuildReferenceBoards(_repo, dayBucket, seed + dayBucket * 31);
        }
        catch
        {
            // 若极简裁剪仓库缺少某些卡牌模板，安全回退为纯合成考官
            return new List<BenchmarkOpponent>();
        }

        if (refBoards.Count == 0)
            return new List<BenchmarkOpponent>();

        var byHero = refBoards
            .GroupBy(b => b.Hero, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var picked = new List<BenchmarkOpponent>(count);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        int heroStride = Math.Max(1, count - 1);
        int startHeroOffset = Math.Max(0, dayBucket - 10) * heroStride;

        for (int k = 0; k < count; k++)
        {
            string hero = HeroNames.PlayableHeroes[(startHeroOffset + k) % HeroNames.PlayableHeroes.Length];
            if (!byHero.TryGetValue(hero, out var heroBoards) || heroBoards.Count == 0)
                continue;

            int specIdx = (dayBucket + k) % heroBoards.Count;
            var board = heroBoards[specIdx];
            if (!usedIds.Add(board.PlayerId))
            {
                board = heroBoards.FirstOrDefault(b => !usedIds.Contains(b.PlayerId)) ?? board;
                usedIds.Add(board.PlayerId);
            }

            board.PrepareForCombat();
            picked.Add(new BenchmarkOpponent
            {
                BenchmarkId = board.PlayerId,
                Hero = board.Hero,
                IsCommunityBlueprint = true,
                PreparedState = board
            });
        }

        return picked;
    }

    /// <summary>
    /// 从当日全量考官池中抽取 4 位跨 Weak / Normal / Strong / MetaBoss 四档分层考官（尽量覆盖不同英雄；
    /// Day 10+ 固定包含 1 位（25%）5.0.0 社区 10 胜参考盘担任 MetaBoss 守门），用于高速 4 场精准补测。
    /// </summary>
    private IReadOnlyList<BenchmarkOpponent> SelectFourTierStratifiedBenchmarks(
        IReadOnlyList<BenchmarkOpponent> fullBenchmarks,
        int dayBucket,
        IDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> ratingCache)
    {
        if (fullBenchmarks.Count <= 4)
            return fullBenchmarks;

        var selected = new List<BenchmarkOpponent>(4);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var usedHeroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var commOpponents = fullBenchmarks.Where(b => b.IsCommunityBlueprint).ToList();
        var synRanked = fullBenchmarks
            .Where(b => !b.IsCommunityBlueprint)
            .Select(b =>
            {
                PowerBucket? knownBucket = null;
                double sortScore = b.Snapshot != null ? ComputeHeuristicStrength(b.Snapshot) : 0.0;
                if (b.Snapshot != null)
                {
                    string sig = string.IsNullOrEmpty(b.Snapshot.BoardSignature)
                        ? _validator.ComputeSignature(b.Snapshot)
                        : b.Snapshot.BoardSignature;
                    if (ratingCache.TryGetValue((dayBucket, b.Hero, sig), out var cached))
                    {
                        knownBucket = cached.Bucket;
                        sortScore = cached.WinRate * 10000.0 + sortScore;
                    }
                }
                return (Opponent: b, KnownBucket: knownBucket, SortScore: sortScore);
            })
            .OrderBy(x => x.SortScore)
            .ToList();

        bool hasCommunitySlot = dayBucket >= 10 && commOpponents.Count > 0;
        if (hasCommunitySlot)
        {
            var commPick = commOpponents[(dayBucket - 10) % commOpponents.Count];
            if (usedIds.Add(commPick.BenchmarkId))
            {
                selected.Add(commPick);
                usedHeroes.Add(commPick.Hero);
            }
        }

        (PowerBucket Tier, double FallbackQuantile)[] targets = hasCommunitySlot
            ? [(PowerBucket.Weak, 0.12), (PowerBucket.Normal, 0.45), (PowerBucket.Strong, 0.85)]
            : [(PowerBucket.Weak, 0.10), (PowerBucket.Normal, 0.38), (PowerBucket.Strong, 0.68), (PowerBucket.MetaBoss, 0.94)];

        foreach (var (tier, quantile) in targets)
        {
            if (selected.Count >= 4 || synRanked.Count == 0)
                break;

            var pick = PickStratifiedSyntheticCandidate(synRanked, tier, quantile, usedIds, usedHeroes);
            if (pick != null && usedIds.Add(pick.BenchmarkId))
            {
                selected.Add(pick);
                usedHeroes.Add(pick.Hero);
            }
        }

        if (selected.Count < 4)
        {
            foreach (var b in fullBenchmarks)
            {
                if (selected.Count >= 4) break;
                if (usedIds.Add(b.BenchmarkId))
                    selected.Add(b);
            }
        }

        return selected;
    }

    private static BenchmarkOpponent? PickStratifiedSyntheticCandidate(
        List<(BenchmarkOpponent Opponent, PowerBucket? KnownBucket, double SortScore)> synRanked,
        PowerBucket targetTier,
        double fallbackQuantile,
        HashSet<string> usedIds,
        HashSet<string> usedHeroes)
    {
        var remaining = synRanked.Where(x => !usedIds.Contains(x.Opponent.BenchmarkId)).ToList();
        if (remaining.Count == 0)
            return null;

        var exactDistinctHero = remaining.Where(x => x.KnownBucket == targetTier && !usedHeroes.Contains(x.Opponent.Hero)).ToList();
        if (exactDistinctHero.Count > 0)
            return exactDistinctHero[exactDistinctHero.Count / 2].Opponent;

        var exactAny = remaining.Where(x => x.KnownBucket == targetTier).ToList();
        if (exactAny.Count > 0)
            return exactAny[exactAny.Count / 2].Opponent;

        var heroPool = remaining.Where(x => !usedHeroes.Contains(x.Opponent.Hero)).ToList();
        var searchPool = heroPool.Count > 0 ? heroPool : remaining;
        int qIdx = Math.Clamp((int)Math.Round((searchPool.Count - 1) * fallbackQuantile), 0, searchPool.Count - 1);
        return searchPool[qIdx].Opponent;
    }

    private static IReadOnlyList<PvpBoardSnapshot> SelectDayBenchmarks(
        Dictionary<(int DayBucket, string Hero), List<PvpBoardSnapshot>> uniqueByBucketHero,
        int dayBucket,
        int targetCount)
    {
        var selected = new List<PvpBoardSnapshot>(targetCount);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        int perHero = Math.Max(1, targetCount / HeroNames.PlayableHeroes.Length);

        double[] quantiles = perHero >= 3
            ? [0.15, 0.45, 0.75]
            : (perHero == 2 ? [0.25, 0.75] : [0.50]);

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            if (selected.Count >= targetCount) break;
            if (!uniqueByBucketHero.TryGetValue((dayBucket, hero), out var list) || list.Count == 0)
                continue;

            var sorted = list.OrderBy(ComputeHeuristicStrength).ToList();
            for (int k = 0; k < perHero && selected.Count < targetCount; k++)
            {
                // 当每英雄仅取 1 席时（如 10 席合成池），交替覆盖低(0.25)/高(0.75)分位
                double q = perHero == 1
                    ? ((selected.Count & 1) == 0 ? 0.25 : 0.75)
                    : quantiles[k % quantiles.Length];
                int idx = Math.Clamp((int)Math.Floor((sorted.Count - 1) * q), 0, sorted.Count - 1);
                var pick = sorted[idx];
                if (!usedIds.Add(pick.BoardId))
                {
                    pick = sorted.FirstOrDefault(x => !usedIds.Contains(x.BoardId)) ?? pick;
                    usedIds.Add(pick.BoardId);
                }
                selected.Add(pick);
            }
        }

        // 补齐剩余名额（例如 targetCount=10 时，前面 8 英雄各取 1 位后补 2 位分层代表）
        if (selected.Count < targetCount)
        {
            var allInDay = HeroNames.PlayableHeroes
                .SelectMany(h => uniqueByBucketHero.TryGetValue((dayBucket, h), out var l) ? l : Enumerable.Empty<PvpBoardSnapshot>())
                .OrderBy(ComputeHeuristicStrength)
                .ToList();

            if (allInDay.Count > 0)
            {
                int need = targetCount - selected.Count;
                for (int i = 0; i < need && selected.Count < targetCount; i++)
                {
                    int idx = Math.Clamp((int)((i + 0.5) * allInDay.Count / Math.Max(1, need)), 0, allInDay.Count - 1);
                    var pick = allInDay[idx];
                    if (usedIds.Add(pick.BoardId))
                        selected.Add(pick);
                    else
                    {
                        var alt = allInDay.FirstOrDefault(x => !usedIds.Contains(x.BoardId));
                        if (alt != null && usedIds.Add(alt.BoardId))
                            selected.Add(alt);
                    }
                }
            }
        }

        return selected;
    }

    private static List<PvpBoardSnapshot> OrderCandidatesStratified(List<PvpBoardSnapshot> raw)
    {
        if (raw.Count <= 2) return new List<PvpBoardSnapshot>(raw);

        var sorted = raw.OrderBy(ComputeHeuristicStrength).ToList();
        var result = new List<PvpBoardSnapshot>(sorted.Count);
        int left = 0;
        int right = sorted.Count - 1;
        int midStep = 0;
        while (left <= right)
        {
            result.Add(sorted[left++]);
            if (left <= right)
                result.Add(sorted[right--]);
            if (left <= right && (midStep++ % 2 == 0))
            {
                int mid = left + (right - left) / 2;
                result.Add(sorted[mid]);
                sorted.RemoveAt(mid);
                right--;
            }
        }
        return result;
    }

    private static List<PvpBoardSnapshot> PickTargetedExpansion(
        List<PvpBoardSnapshot> ordered,
        int budget,
        bool needWeak,
        bool needMeta,
        HashSet<string> testedSigSet)
    {
        var result = new List<PvpBoardSnapshot>(budget);
        var remaining = new List<PvpBoardSnapshot>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            if (!testedSigSet.Contains(s.BoardSignature!))
                remaining.Add(s);
        }

        if (remaining.Count == 0)
            return result;

        if (needWeak && !needMeta)
        {
            foreach (var s in remaining.OrderBy(ComputeHeuristicStrength).Take(budget))
            {
                if (testedSigSet.Add(s.BoardSignature!))
                    result.Add(s);
            }
            return result;
        }

        if (needMeta && !needWeak)
        {
            foreach (var s in remaining.OrderByDescending(ComputeHeuristicStrength).Take(budget))
            {
                if (testedSigSet.Add(s.BoardSignature!))
                    result.Add(s);
            }
            return result;
        }

        foreach (var s in remaining.Take(budget))
        {
            if (testedSigSet.Add(s.BoardSignature!))
                result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// 第 5 波零空缺分批早停清扫：针对前 4 波结束后仍存在 0 样本子单元的英雄，按强弱需求定向分批（每批 64、上限 640）补测，
    /// 一旦该英雄原空缺档位全部达到 &gt;= 2（或达到清扫上限且均已填充）立即早停。
    /// </summary>
    private int RescueEmptyHeroGridCells(
        Dictionary<string, List<PvpBoardSnapshot>> heroCandidates,
        IReadOnlyList<BenchmarkOpponent> mixedBenchmarks,
        int dayBucket,
        DatasetGenOptions options,
        ParallelOptions parallelOpts,
        HashSet<string> testedSigSet,
        ConcurrentDictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)> ratingCache,
        Dictionary<(string Hero, PowerBucket Bucket), int> bucketGridCounts,
        List<PvpBoardSnapshot> gradedRepresentatives)
    {
        const int rescueBatchSize = 64;
        int rescueMaxPerHero = dayBucket >= 14 ? int.MaxValue : 640;
        int extraMatches = 0;

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            var initialEmptyTiers = DayBucketHelper.AllPowerBuckets
                .Where(pb => bucketGridCounts[(hero, pb)] == 0)
                .ToList();
            if (initialEmptyTiers.Count == 0)
                continue;

            var list = heroCandidates[hero];
            int swept = 0;
            while (swept < rescueMaxPerHero)
            {
                if (initialEmptyTiers.All(pb => bucketGridCounts[(hero, pb)] >= 2))
                    break;

                bool needWeakSide = initialEmptyTiers.Any(pb =>
                    (pb == PowerBucket.Weak || pb == PowerBucket.Normal) && bucketGridCounts[(hero, pb)] < 2);
                bool needStrongSide = initialEmptyTiers.Any(pb =>
                    (pb == PowerBucket.Strong || pb == PowerBucket.MetaBoss) && bucketGridCounts[(hero, pb)] < 2);

                int batchBudget = rescueMaxPerHero == int.MaxValue
                    ? rescueBatchSize
                    : Math.Min(rescueBatchSize, rescueMaxPerHero - swept);
                var picked = PickTargetedExpansion(list, batchBudget, needWeakSide, needStrongSide, testedSigSet);
                if (picked.Count == 0)
                    break;

                swept += picked.Count;
                extraMatches += EvaluateBatchAndRecord(
                    picked, mixedBenchmarks, dayBucket, options, parallelOpts,
                    ratingCache, bucketGridCounts, gradedRepresentatives);
            }
        }

        return extraMatches;
    }

    /// <summary>
    /// 用于抽样分层与扩样引导的轻量启发式战力评分（不替代真实实战胜率）。
    /// </summary>
    private static double ComputeHeuristicStrength(PvpBoardSnapshot snap)
    {
        double score = snap.Wins * 12.0 - snap.Losses * 4.0 + snap.HealthMax * 0.015 + snap.Skills.Count * 6.0;
        if (snap.MatchOutcome is { Won: true }) score += 8.0;
        foreach (var item in snap.Items)
        {
            score += ((int)item.Tier + 1) * 5.0 * Math.Max(1, item.Width);
            if (!string.IsNullOrEmpty(item.Enchantment)) score += 10.0;
            score += item.PermanentAttributes.Count * 1.5;
        }
        return score;
    }
}

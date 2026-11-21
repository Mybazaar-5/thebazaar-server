using BazaarCore.Dataset;
using BazaarCore.Models;

namespace BazaarDatasetGen;

/// <summary>
/// 均衡与自然漏斗双池采样结果：包含精选完整 Run、480 格均衡镜像池、真实自然漏斗单日池及关联父 Run 映射。
/// </summary>
public sealed class SamplingResult
{
    public required IReadOnlyList<PvpRunRecord> SelectedRuns { get; init; }
    public required IReadOnlyList<PvpBoardSnapshot> BalancedGhostPool { get; init; }
    public required IReadOnlyDictionary<int, List<PvpBoardSnapshot>> BalancedPoolByDayBucket { get; init; }
    public required IReadOnlyList<PvpBoardSnapshot> NaturalGhostPool { get; init; }
    public required IReadOnlyDictionary<int, List<PvpBoardSnapshot>> NaturalPoolByDayBucket { get; init; }
    public required IReadOnlyDictionary<string, PvpRunRecord> ReferencedRunsById { get; init; }
}

/// <summary>
/// 精选采样器（v2 方案 A）：
/// 1. 从全部已完结 Run 中按 8 英雄均分抽取 12,000 局完整 Run，且在每个英雄内部严格按主淘汰赛真实的
///    (PersonaType, 终局天数段 <=9 / 10~12 / 13+) 比例分层抽样，完整保留早死菜鸟局与自然淘汰分布；
/// 2. 按 15 天桶 × 8 英雄 × 4 强度档（480 格）分层去重采样均衡单日镜像池（默认每日 480 条，共 7,200 条）；
/// 3. 按主淘汰赛各天真实存活率与各画像自然留存比例采样真实自然漏斗单日池（ghost_pool_natural.jsonl，约 14,000~16,000 条）。
/// </summary>
public sealed class BalancedPoolSampler
{
    private readonly IBoardValidator _validator;

    public BalancedPoolSampler(IBoardValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// 执行 12,000 局完整 Run 精选、480 格均衡单日池与真实自然漏斗池采样。
    /// </summary>
    public SamplingResult Sample(
        IReadOnlyList<PvpRunRecord> allRuns,
        DatasetGenOptions options,
        CrossValidationGrader? grader = null,
        CrossValidationResult? gradingResult = null,
        Action<string>? log = null)
    {
        var allRunsById = new Dictionary<string, PvpRunRecord>(allRuns.Count, StringComparer.Ordinal);
        foreach (var run in allRuns)
            allRunsById[run.RunId] = run;

        // 1. 按自然生态 (PersonaType × FinalDayStage) 分层抽取 12,000 局完整 Run（每英雄 1,500 局）
        var selectedRuns = SelectBalancedRuns(allRuns, options.TargetRuns, options.Seed);
        var selectedRunIds = new HashSet<string>(selectedRuns.Select(r => r.RunId), StringComparer.Ordinal);

        // 2. 汇集已有 16 基准实测盘面用于 480 格均衡单日镜像池采样
        var gradedCandidatePool = CollectGradedCandidates(selectedRuns, allRuns, gradingResult);
        var balancedByBucket = SampleBalancedGhostPool(gradedCandidatePool, selectedRunIds, options);
        var balancedList = new List<PvpBoardSnapshot>(options.PoolPerDay * DayBucketHelper.AllDayBuckets.Length);
        foreach (int b in DayBucketHelper.AllDayBuckets)
            balancedList.AddRange(balancedByBucket[b]);

        // 3. 采样真实自然漏斗单日池（保留自然存活曲线与 Day 1~6 高比例低分/失误样本）
        var naturalByBucket = SampleNaturalGhostPool(allRuns, selectedRunIds, options);
        var naturalList = new List<PvpBoardSnapshot>(16_000);
        foreach (int b in DayBucketHelper.AllDayBuckets)
            naturalList.AddRange(naturalByBucket[b]);

        // 4. 对 SelectedRuns 与 NaturalGhostPool 中尚未命中缓存的盘面统一执行高速 8 基准补测（3 倍提速）
        if (grader != null && gradingResult != null)
        {
            var mutableCache = new Dictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)>(
                gradingResult.RatingCache);

            var boardsToVerify = new List<PvpBoardSnapshot>(selectedRuns.Sum(r => r.DayBoards.Count) + naturalList.Count);
            foreach (var r in selectedRuns)
                boardsToVerify.AddRange(r.DayBoards);
            boardsToVerify.AddRange(naturalList);

            grader.GradeMissingSnapshots(boardsToVerify, gradingResult.MixedBenchmarksByDayBucket, mutableCache, options, log);
        }

        // 5. 汇总 SelectedRuns、BalancedGhostPool 与 NaturalGhostPool 涉及的全部父 Run（确保 SQLite 外键与统计自洽）
        var referencedRuns = new Dictionary<string, PvpRunRecord>(StringComparer.Ordinal);
        foreach (var run in selectedRuns)
            referencedRuns[run.RunId] = run;
        foreach (var snap in balancedList)
        {
            if (!string.IsNullOrEmpty(snap.RunId) && allRunsById.TryGetValue(snap.RunId, out var parent))
                referencedRuns.TryAdd(parent.RunId, parent);
        }
        foreach (var snap in naturalList)
        {
            if (!string.IsNullOrEmpty(snap.RunId) && allRunsById.TryGetValue(snap.RunId, out var parent))
                referencedRuns.TryAdd(parent.RunId, parent);
        }

        int totalRunBoards = selectedRuns.Sum(r => r.DayBoards.Count);
        int earlyEliminatedCount = selectedRuns.Count(r => r.FinalDay <= 9);
        log?.Invoke($"[均衡采样] 精选 Run={selectedRuns.Count} 局（快照 {totalRunBoards} 条，<=Day9 早退/淘汰局 {earlyEliminatedCount} 局），均衡池={balancedList.Count} 条，自然漏斗池={naturalList.Count} 条");

        return new SamplingResult
        {
            SelectedRuns = selectedRuns,
            BalancedGhostPool = balancedList,
            BalancedPoolByDayBucket = balancedByBucket,
            NaturalGhostPool = naturalList,
            NaturalPoolByDayBucket = naturalByBucket,
            ReferencedRunsById = referencedRuns
        };
    }

    /// <summary>
    /// 终局天数分段：0=早死/淘汰(<=9), 1=中段(10~12), 2=长程深水(13+)。
    /// </summary>
    private static int GetFinalDayStage(int finalDay) => finalDay <= 9 ? 0 : (finalDay <= 12 ? 1 : 2);

    private List<PvpRunRecord> SelectBalancedRuns(
        IReadOnlyList<PvpRunRecord> allRuns,
        int targetRuns,
        int seed)
    {
        var heroes = HeroNames.PlayableHeroes;
        var personas = Enum.GetValues<PersonaType>();
        var selected = new List<PvpRunRecord>(Math.Min(targetRuns, allRuns.Count));
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);

        int basePerHero = targetRuns / heroes.Length;
        int heroRem = targetRuns % heroes.Length;

        for (int hIdx = 0; hIdx < heroes.Length; hIdx++)
        {
            string hero = heroes[hIdx];
            int heroQuota = basePerHero + (hIdx < heroRem ? 1 : 0);

            var allHeroRuns = allRuns
                .Where(r => string.Equals(r.Hero, hero, StringComparison.OrdinalIgnoreCase)
                    && r.DayBoards.Count > 0
                    && !string.Equals(r.Source, "real_community", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (allHeroRuns.Count <= heroQuota)
            {
                foreach (var r in allHeroRuns)
                {
                    if (selectedIds.Add(r.RunId))
                        selected.Add(r);
                }
                continue;
            }

            // 以主淘汰赛（非 run_sup_ 无尽补充）测量各英雄真实自然的 (Persona, FinalDayStage) 生态分布比例
            var mainHeroRuns = allHeroRuns
                .Where(r => !r.RunId.StartsWith("run_sup_", StringComparison.Ordinal))
                .ToList();
            var proportionBase = mainHeroRuns.Count >= heroQuota ? mainHeroRuns : allHeroRuns;

            // 构建 (Persona, Stage) -> 权重与对应候选池
            var strataKeys = new List<(PersonaType Persona, int Stage)>(personas.Length * 3);
            var strataWeights = new Dictionary<(PersonaType, int), int>();
            var strataPools = new Dictionary<(PersonaType, int), List<PvpRunRecord>>();

            foreach (var p in personas)
            {
                for (int stage = 0; stage < 3; stage++)
                {
                    var key = (p, stage);
                    strataKeys.Add(key);
                    strataWeights[key] = 0;
                    strataPools[key] = new List<PvpRunRecord>();
                }
            }

            foreach (var r in proportionBase)
                strataWeights[(r.Persona, GetFinalDayStage(r.FinalDay))]++;

            // 候选池优先使用主淘汰赛 Run；对于长程(13+)若主赛不足则自动允许使用深水补充 Run
            foreach (var r in mainHeroRuns)
                strataPools[(r.Persona, GetFinalDayStage(r.FinalDay))].Add(r);
            foreach (var r in allHeroRuns)
            {
                if (r.RunId.StartsWith("run_sup_", StringComparison.Ordinal))
                    strataPools[(r.Persona, GetFinalDayStage(r.FinalDay))].Add(r);
            }

            // 按最大余数法（Hamilton 方法）将 heroQuota 严格按自然比例分配给 24 个子层
            var quotas = AllocateProportionalQuotas(strataWeights, heroQuota);
            var heroPicked = new List<PvpRunRecord>(heroQuota);

            for (int sIdx = 0; sIdx < strataKeys.Count; sIdx++)
            {
                var key = strataKeys[sIdx];
                int q = quotas[key];
                if (q <= 0) continue;

                var picked = PickStratumDiverseRuns(strataPools[key], q, seed ^ (hIdx * 131 + sIdx * 17));
                foreach (var r in picked)
                {
                    if (selectedIds.Add(r.RunId))
                        heroPicked.Add(r);
                }
            }

            // 若个别子层候选数略少于配额，从该英雄剩余未选池中按均衡流派补足
            if (heroPicked.Count < heroQuota)
            {
                var leftover = allHeroRuns.Where(r => !selectedIds.Contains(r.RunId)).ToList();
                var topUp = PickStratumDiverseRuns(leftover, heroQuota - heroPicked.Count, seed ^ (hIdx * 997));
                foreach (var r in topUp)
                {
                    if (selectedIds.Add(r.RunId))
                        heroPicked.Add(r);
                }
            }

            selected.AddRange(heroPicked);
        }

        // 若个别英雄总池不足，从全局剩余 Run 补足
        if (selected.Count < targetRuns && selected.Count < allRuns.Count)
        {
            var leftover = allRuns.Where(r => !selectedIds.Contains(r.RunId) && r.DayBoards.Count > 0).ToList();
            foreach (var r in PickStratumDiverseRuns(leftover, targetRuns - selected.Count, seed ^ 0x7777))
            {
                if (selectedIds.Add(r.RunId))
                    selected.Add(r);
            }
        }

        return selected;
    }

    private static List<PvpRunRecord> PickStratumDiverseRuns(List<PvpRunRecord> pool, int quota, int seed)
    {
        if (quota <= 0 || pool.Count == 0) return new List<PvpRunRecord>();
        if (pool.Count <= quota) return new List<PvpRunRecord>(pool);

        // 子层内部不做“长程偏向排序”，而是按确定性哈希打散后跨流派轮转抽取，原汁原味保留层内天数分布（含 Day 5~9 崩盘局）
        var byArch = pool
            .GroupBy(r => r.Archetype, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<PvpRunRecord>(
                g.OrderBy(r => unchecked((r.RunId.GetHashCode() * 397) ^ seed))))
            .ToList();

        var result = new List<PvpRunRecord>(quota);
        int idx = Math.Abs(seed == int.MinValue ? 0 : seed) % Math.Max(1, byArch.Count);
        while (result.Count < quota && byArch.Count > 0)
        {
            idx %= byArch.Count;
            var q = byArch[idx];
            if (q.Count > 0)
            {
                result.Add(q.Dequeue());
                idx++;
            }
            else
            {
                byArch.RemoveAt(idx);
            }
        }
        return result;
    }

    private Dictionary<int, List<PvpBoardSnapshot>> SampleNaturalGhostPool(
        IReadOnlyList<PvpRunRecord> allRuns,
        HashSet<string> selectedRunIds,
        DatasetGenOptions options)
    {
        var result = new Dictionary<int, List<PvpBoardSnapshot>>();
        var mainByBucket = new Dictionary<int, List<PvpBoardSnapshot>>();
        var allByBucket = new Dictionary<int, List<PvpBoardSnapshot>>();

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            mainByBucket[b] = new List<PvpBoardSnapshot>();
            allByBucket[b] = new List<PvpBoardSnapshot>();
        }

        foreach (var run in allRuns)
        {
            if (string.Equals(run.Source, "real_community", StringComparison.OrdinalIgnoreCase))
                continue;
            bool isMain = !run.RunId.StartsWith("run_sup_", StringComparison.Ordinal);
            foreach (var snap in run.DayBoards)
            {
                if (string.IsNullOrEmpty(snap.BoardSignature))
                    snap.BoardSignature = _validator.ComputeSignature(snap);

                int b = DayBucketHelper.GetDayBucket(snap.Day);
                allByBucket[b].Add(snap);
                if (isMain)
                    mainByBucket[b].Add(snap);
            }
        }

        int day1MainCount = Math.Max(1, mainByBucket[1].Count);
        int allDay1Count = Math.Max(1, allByBucket[1].Count);
        int effectiveDay1Ref = mainByBucket[1].Count > 0 ? day1MainCount : allDay1Count;

        // 按规模自适应缩放（全量 24,000 局时 scale=1.0 -> Day 1~6 约 1,250/天，Day 15+ 保底 400/天）
        int earlyTarget = effectiveDay1Ref >= 1000
            ? options.NaturalPoolEarlyPerDay
            : Math.Clamp((int)Math.Round(effectiveDay1Ref * 0.55), 8, options.NaturalPoolEarlyPerDay);
        int lateMin = effectiveDay1Ref >= 1000
            ? options.NaturalPoolLateMinPerDay
            : Math.Max(4, (int)Math.Round(earlyTarget * (options.NaturalPoolLateMinPerDay / (double)Math.Max(1, options.NaturalPoolEarlyPerDay))));

        var usedIdsGlobal = new HashSet<string>(StringComparer.Ordinal);

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var naturalSource = mainByBucket[b].Count > 0 ? mainByBucket[b] : allByBucket[b];
            var allSource = allByBucket[b];

            if (allSource.Count == 0)
            {
                result[b] = new List<PvpBoardSnapshot>();
                continue;
            }

            double survivalRatio = (mainByBucket[b].Count > 0 ? mainByBucket[b].Count : allByBucket[b].Count) / (double)effectiveDay1Ref;
            int dayTarget = b <= 6
                ? earlyTarget
                : Math.Clamp((int)Math.Round(earlyTarget * survivalRatio), lateMin, earlyTarget);

            // 对 Day 15+ 若主赛与补齐汇总唯一盘面仍略少于 dayTarget，尽量取满去重上限
            dayTarget = Math.Min(dayTarget, allSource.Count);

            // 按主赛该天真实存活的 (Hero, PersonaType) 权重比例分配配额
            var weights = new Dictionary<(string Hero, PersonaType Persona), int>();
            var pools = new Dictionary<(string Hero, PersonaType Persona), List<PvpBoardSnapshot>>();

            foreach (var rSnap in naturalSource)
            {
                var k = (rSnap.Hero, rSnap.Persona);
                weights[k] = weights.GetValueOrDefault(k) + 1;
            }

            // 候选池先放主赛、再放精选 Run 所属盘面、再放补齐池
            foreach (var s in allSource
                .OrderByDescending(s => !s.RunId.StartsWith("run_sup_", StringComparison.Ordinal))
                .ThenByDescending(s => selectedRunIds.Contains(s.RunId))
                .ThenBy(s => unchecked(s.BoardId.GetHashCode() ^ options.Seed)))
            {
                var k = (s.Hero, s.Persona);
                if (!pools.TryGetValue(k, out var list))
                {
                    list = new List<PvpBoardSnapshot>();
                    pools[k] = list;
                }
                list.Add(s);
            }

            var quotas = AllocateProportionalQuotas(weights, dayTarget);
            var pickedForDay = new List<PvpBoardSnapshot>(dayTarget);
            var usedSigInDay = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (cellKey, q) in quotas)
            {
                if (q <= 0 || !pools.TryGetValue(cellKey, out var cellPool))
                    continue;

                int added = 0;
                foreach (var cand in cellPool)
                {
                    if (added >= q) break;
                    if (usedIdsGlobal.Contains(cand.BoardId)) continue;
                    string sigKey = cand.Hero + "|" + cand.BoardSignature!;
                    if (!usedSigInDay.Add(sigKey)) continue;

                    usedIdsGlobal.Add(cand.BoardId);
                    pickedForDay.Add(cand);
                    added++;
                }
            }

            // 若因指纹去重导致个别细胞略欠缺，按天桶自然池顺序补齐去重盘面至 dayTarget
            if (pickedForDay.Count < dayTarget)
            {
                foreach (var cand in naturalSource.Concat(allSource))
                {
                    if (pickedForDay.Count >= dayTarget) break;
                    if (usedIdsGlobal.Contains(cand.BoardId)) continue;
                    string sigKey = cand.Hero + "|" + cand.BoardSignature!;
                    if (!usedSigInDay.Add(sigKey)) continue;

                    usedIdsGlobal.Add(cand.BoardId);
                    pickedForDay.Add(cand);
                }
            }

            // 若去重池仍不够，允许从同天剩余不同 BoardId 补齐目标数
            if (pickedForDay.Count < dayTarget)
            {
                foreach (var cand in naturalSource.Concat(allSource))
                {
                    if (pickedForDay.Count >= dayTarget) break;
                    if (!usedIdsGlobal.Add(cand.BoardId)) continue;
                    pickedForDay.Add(cand);
                }
            }

            result[b] = pickedForDay;
        }

        return result;
    }

    private static Dictionary<TKey, int> AllocateProportionalQuotas<TKey>(
        Dictionary<TKey, int> weights,
        int totalQuota) where TKey : notnull
    {
        var result = new Dictionary<TKey, int>(weights.Count);
        long totalWeight = 0;
        foreach (var w in weights.Values)
            totalWeight += Math.Max(0, w);

        if (totalWeight <= 0 || totalQuota <= 0)
        {
            foreach (var k in weights.Keys)
                result[k] = 0;
            return result;
        }

        var remainders = new List<(TKey Key, double Rem)>(weights.Count);
        int assigned = 0;

        foreach (var (key, w) in weights)
        {
            if (w <= 0)
            {
                result[key] = 0;
                continue;
            }
            double exact = totalQuota * (w / (double)totalWeight);
            int floorVal = (int)Math.Floor(exact);
            result[key] = floorVal;
            assigned += floorVal;
            remainders.Add((key, exact - floorVal));
        }

        int leftover = totalQuota - assigned;
        if (leftover > 0)
        {
            foreach (var item in remainders.OrderByDescending(x => x.Rem))
            {
                if (leftover <= 0) break;
                result[item.Key]++;
                leftover--;
            }
        }

        return result;
    }

    private List<PvpBoardSnapshot> CollectGradedCandidates(
        IReadOnlyList<PvpRunRecord> selectedRuns,
        IReadOnlyList<PvpRunRecord> allRuns,
        CrossValidationResult? gradingResult)
    {
        var byBucketHeroSig = new Dictionary<(int DayBucket, string Hero, string Signature), PvpBoardSnapshot>();

        void Consider(PvpBoardSnapshot snap, bool requireInRatingCache)
        {
            if (string.IsNullOrEmpty(snap.BoardSignature))
                snap.BoardSignature = _validator.ComputeSignature(snap);

            int b = DayBucketHelper.GetDayBucket(snap.Day);
            var key = (b, snap.Hero, snap.BoardSignature);

            if (requireInRatingCache && gradingResult != null)
            {
                if (!gradingResult.RatingCache.TryGetValue(key, out var cached))
                    return;
                snap.EstimatedWinRate = cached.WinRate;
                snap.PowerBucket = cached.Bucket;
            }

            byBucketHeroSig.TryAdd(key, snap);
        }

        foreach (var run in selectedRuns)
            foreach (var snap in run.DayBoards)
                Consider(snap, requireInRatingCache: gradingResult != null);

        if (gradingResult != null)
        {
            foreach (var snap in gradingResult.GradedUniqueBoards)
                Consider(snap, requireInRatingCache: true);
        }
        else
        {
            foreach (var run in allRuns)
                foreach (var snap in run.DayBoards)
                    Consider(snap, requireInRatingCache: false);
        }

        return byBucketHeroSig.Values.ToList();
    }

    private static Dictionary<int, List<PvpBoardSnapshot>> SampleBalancedGhostPool(
        List<PvpBoardSnapshot> candidates,
        HashSet<string> selectedRunIds,
        DatasetGenOptions options)
    {
        var result = new Dictionary<int, List<PvpBoardSnapshot>>();
        var heroes = HeroNames.PlayableHeroes;
        var powerBuckets = DayBucketHelper.AllPowerBuckets;

        var byBucket = candidates.GroupBy(s => DayBucketHelper.GetDayBucket(s.Day))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (int dayBucket in DayBucketHelper.AllDayBuckets)
        {
            var pickedForDay = new List<PvpBoardSnapshot>(options.PoolPerDay);
            var usedSignaturesInDay = new HashSet<string>(StringComparer.Ordinal);
            var usedIdsGlobal = new HashSet<string>(StringComparer.Ordinal);

            if (!byBucket.TryGetValue(dayBucket, out var dayList) || dayList.Count == 0)
            {
                result[dayBucket] = pickedForDay;
                continue;
            }

            int basePerHero = options.PoolPerDay / heroes.Length;
            int heroRem = options.PoolPerDay % heroes.Length;

            var byHeroPower = new Dictionary<(string Hero, PowerBucket Power), List<PvpBoardSnapshot>>();
            foreach (var h in heroes)
                foreach (var pb in powerBuckets)
                    byHeroPower[(h, pb)] = new List<PvpBoardSnapshot>();

            foreach (var snap in dayList)
            {
                var key = (Hero: snap.Hero, Power: snap.PowerBucket);
                if (!byHeroPower.TryGetValue(key, out var list))
                {
                    list = new List<PvpBoardSnapshot>();
                    byHeroPower[key] = list;
                }
                list.Add(snap);
            }

            foreach (var list in byHeroPower.Values)
            {
                var ordered = OrderGroupForDiversity(list, selectedRunIds);
                list.Clear();
                list.AddRange(ordered);
            }

            for (int hIdx = 0; hIdx < heroes.Length; hIdx++)
            {
                string hero = heroes[hIdx];
                int heroTarget = basePerHero + (hIdx < heroRem ? 1 : 0);
                var pickedForHero = new List<PvpBoardSnapshot>(heroTarget);

                int basePerPower = heroTarget / powerBuckets.Length;
                int powerRem = heroTarget % powerBuckets.Length;
                PowerBucket[] remOrder = [PowerBucket.Normal, PowerBucket.Strong, PowerBucket.Weak, PowerBucket.MetaBoss];

                var powerQuotas = new Dictionary<PowerBucket, int>();
                foreach (var pb in powerBuckets) powerQuotas[pb] = basePerPower;
                for (int r = 0; r < powerRem; r++)
                    powerQuotas[remOrder[r % remOrder.Length]]++;

                foreach (var pb in powerBuckets)
                {
                    var pool = byHeroPower[(hero, pb)];
                    PickWithDiversity(pool, powerQuotas[pb], 0.85, pickedForHero, usedSignaturesInDay, usedIdsGlobal);
                }

                foreach (var pb in powerBuckets)
                {
                    int already = pickedForHero.Count(s => s.PowerBucket == pb);
                    int missing = powerQuotas[pb] - already;
                    if (missing <= 0) continue;

                    var pool = byHeroPower[(hero, pb)];
                    PickWithDiversity(pool, missing, 0.96, pickedForHero, usedSignaturesInDay, usedIdsGlobal);

                    already = pickedForHero.Count(s => s.PowerBucket == pb);
                    missing = powerQuotas[pb] - already;
                    if (missing > 0)
                        PickWithDiversity(pool, missing, 1.01, pickedForHero, usedSignaturesInDay, usedIdsGlobal);
                }

                if (pickedForHero.Count < heroTarget)
                {
                    var heroSurplus = powerBuckets
                        .SelectMany(pb => byHeroPower[(hero, pb)])
                        .ToList();
                    PickWithDiversity(heroSurplus, heroTarget - pickedForHero.Count, 0.88, pickedForHero, usedSignaturesInDay, usedIdsGlobal);
                    if (pickedForHero.Count < heroTarget)
                        PickWithDiversity(heroSurplus, heroTarget - pickedForHero.Count, 1.01, pickedForHero, usedSignaturesInDay, usedIdsGlobal);
                }

                pickedForDay.AddRange(pickedForHero);
            }

            if (pickedForDay.Count < options.PoolPerDay)
            {
                var daySurplus = OrderGroupForDiversity(dayList, selectedRunIds);
                PickWithDiversity(daySurplus, options.PoolPerDay - pickedForDay.Count, 0.90, pickedForDay, usedSignaturesInDay, usedIdsGlobal);
                if (pickedForDay.Count < options.PoolPerDay)
                    PickWithDiversity(daySurplus, options.PoolPerDay - pickedForDay.Count, 1.01, pickedForDay, usedSignaturesInDay, usedIdsGlobal);
            }

            result[dayBucket] = pickedForDay;
        }

        return result;
    }

    private static List<PvpBoardSnapshot> OrderGroupForDiversity(List<PvpBoardSnapshot> raw, HashSet<string> selectedRunIds)
    {
        if (raw.Count <= 1) return new List<PvpBoardSnapshot>(raw);

        var groups = raw
            .GroupBy(s => s.Archetype, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<PvpBoardSnapshot>(
                g.OrderByDescending(s => string.Equals(s.Source, "real_community", StringComparison.OrdinalIgnoreCase))
                 .ThenByDescending(s => selectedRunIds.Contains(s.RunId))
                 .ThenByDescending(s => s.Items.Count)
                 .ThenBy(s => s.BoardId, StringComparer.Ordinal)))
            .ToList();

        var ordered = new List<PvpBoardSnapshot>(raw.Count);
        int idx = 0;
        while (groups.Count > 0)
        {
            idx %= groups.Count;
            var q = groups[idx];
            if (q.Count > 0)
            {
                ordered.Add(q.Dequeue());
                idx++;
            }
            else
            {
                groups.RemoveAt(idx);
            }
        }
        return ordered;
    }

    private static void PickWithDiversity(
        List<PvpBoardSnapshot> candidates,
        int needed,
        double maxSimilarity,
        List<PvpBoardSnapshot> targetList,
        HashSet<string> usedSignaturesInDay,
        HashSet<string> usedIdsGlobal)
    {
        if (needed <= 0) return;
        int added = 0;
        foreach (var cand in candidates)
        {
            if (added >= needed) break;
            if (usedIdsGlobal.Contains(cand.BoardId)) continue;
            if (usedSignaturesInDay.Contains(cand.Hero + "|" + cand.BoardSignature!)) continue;

            if (maxSimilarity <= 1.0)
            {
                bool tooSimilar = false;
                for (int i = 0; i < targetList.Count; i++)
                {
                    if (DayBucketHelper.ComputeItemSimilarity(cand, targetList[i]) >= maxSimilarity)
                    {
                        tooSimilar = true;
                        break;
                    }
                }
                if (tooSimilar) continue;
            }

            usedIdsGlobal.Add(cand.BoardId);
            usedSignaturesInDay.Add(cand.Hero + "|" + cand.BoardSignature!);
            targetList.Add(cand);
            added++;
        }
    }
}

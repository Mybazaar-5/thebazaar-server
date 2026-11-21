using System.Diagnostics;
using System.Numerics;
using System.Text;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Models;

namespace BazaarDatasetGen;

/// <summary>
/// 报告生成与全量体检结果汇总。
/// </summary>
public sealed class DatasetReportResult
{
    public required string ReportPath { get; init; }
    public int TotalValidatedBoards { get; init; }
    public int ValidationErrors { get; init; }
    public int MissingZhNames { get; init; }
    public int ReplayMatchesCount { get; init; }
    public int ReplayExceptions { get; init; }
    public int ReplayTimeouts { get; init; }
}

/// <summary>
/// 简体中文数据集统计与合法性体检报告生成器（v2 方案 A 版）：
/// 包含规模统计、15×8 英雄覆盖矩阵、15×4 强度分布与各档实测平均胜率、
/// Day 1 → Day 15+ 成长曲线（含平均附魔件数/物品数/核心流派命中率）、
/// 8 画像自然存活占比漏斗、Day 1/5/10/14 代表性 MetaBoss 与 CluelessNewbie/Weak 实录展示，
/// 以及全量 BoardValidator 校验与 1,000+ 场 CombatSimulator 回放体检。
/// </summary>
public sealed class DatasetReportGenerator
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly GameDataRepository _repo;
    private readonly IBoardValidator _validator;
    private readonly IArchetypeCatalog? _catalog;

    public DatasetReportGenerator(GameDataRepository repo, IBoardValidator validator, IArchetypeCatalog? catalog = null)
    {
        _repo = repo ?? throw new ArgumentNullException(nameof(repo));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _catalog = catalog;
    }

    /// <summary>
    /// 执行全量校验与 1,000+ 场随机战斗抽样回放，并生成 dataset_report.md。
    /// </summary>
    public DatasetReportResult Generate(
        SamplingResult sampling,
        TournamentResult? tournament,
        CrossValidationResult? crossValidation,
        DatasetGenOptions options,
        int replayTargetMatches = 1200,
        Action<string>? log = null)
    {
        Directory.CreateDirectory(options.OutputDir);
        string reportPath = Path.Combine(options.OutputDir, "dataset_report.md");

        var allUniqueBoards = new Dictionary<string, PvpBoardSnapshot>(StringComparer.Ordinal);
        foreach (var r in sampling.SelectedRuns)
            foreach (var b in r.DayBoards)
                allUniqueBoards[b.BoardId] = b;
        foreach (var b in sampling.BalancedGhostPool)
            allUniqueBoards.TryAdd(b.BoardId, b);
        foreach (var b in sampling.NaturalGhostPool)
            allUniqueBoards.TryAdd(b.BoardId, b);

        var uniqueList = allUniqueBoards.Values.ToList();

        // 1. 全量 BoardValidator 合法性与官方中文名完整性审计
        int validationErrCount = 0;
        int missingZhCount = 0;
        var sampleErrors = new List<string>();

        foreach (var snap in uniqueList)
        {
            var res = _validator.Validate(snap, _repo, options.RequireOfficialZh);
            if (!res.IsValid)
            {
                validationErrCount++;
                if (sampleErrors.Count < 10)
                    sampleErrors.Add($"{snap.BoardId}: {string.Join("; ", res.Errors)}");
            }

            foreach (var item in snap.Items)
                if (string.IsNullOrWhiteSpace(item.ZhName)) missingZhCount++;
            foreach (var skill in snap.Skills)
                if (string.IsNullOrWhiteSpace(skill.ZhName)) missingZhCount++;
        }

        // 2. 抽样 >= 1,000 场直接送入 CombatSimulator 完整战斗回放体检
        var replayResult = RunCombatReplayAudit(sampling.BalancedGhostPool, uniqueList, options, replayTargetMatches);
        log?.Invoke($"[体检报告] 全量校验 {uniqueList.Count} 条（错误={validationErrCount}，缺中文={missingZhCount}），抽样回放 {replayResult.SimulatedCount} 场（异常={replayResult.ExceptionCount}，超时={replayResult.TimeoutCount}）");

        // 3. 拼装简体中文 Markdown 报告
        var sb = new StringBuilder();
        sb.AppendLine("# 《The Bazaar》PvP 逐日阵容数据集质量与统计报告 (v2)");
        sb.AppendLine();
        sb.AppendLine($"- **生成时间**：`{DateTime.Now:yyyy-MM-dd HH:mm:ss}`");
        sb.AppendLine($"- **游戏数据源**：`{_repo.SourceDataPath ?? _repo.SourceZipPath}`（卡牌 {_repo.CardsById.Count} / 野怪 {_repo.MonstersById.Count} / 官方中文词条 {_repo.Localization?.EntryCount ?? 0}）");
        sb.AppendLine($"- **全局随机种子**：`{options.Seed}`");
        sb.AppendLine();

        sb.AppendLine("## 一、数据集总规模汇总");
        sb.AppendLine();
        sb.AppendLine("| 指标项 | 实测数值 |");
        sb.AppendLine("| --- | ---: |");
        if (tournament != null)
        {
            sb.AppendLine($"| 主淘汰赛规模（Run 数） | {tournament.MainTournamentRunCount:N0} |");
            sb.AppendLine($"| 深水区补齐追加 Run 数 | {tournament.SupplementalRunCount:N0} |");
            sb.AppendLine($"| 淘汰赛总生成快照数 | {tournament.AllSnapshots.Count:N0} |");
            sb.AppendLine($"| 淘汰赛阶段耗时 | {tournament.ElapsedMs:N0} ms |");
        }
        if (crossValidation != null)
        {
            sb.AppendLine($"| 交叉定级去重签名数 | {crossValidation.GradedUniqueBoards.Count:N0} |");
            sb.AppendLine($"| 交叉定级累计战斗场次 | {crossValidation.TotalMatchesSimulated:N0} |");
            sb.AppendLine($"| Day 10+ 混入社区 10 胜考官席位 | {crossValidation.CommunityBenchmarkSlotsUsed:N0} |");
            sb.AppendLine($"| 交叉定级阶段耗时 | {crossValidation.ElapsedMs:N0} ms |");
        }
        int selectedSnapshotsTotal = sampling.SelectedRuns.Sum(r => r.DayBoards.Count);
        int earlyRunsCount = sampling.SelectedRuns.Count(r => r.FinalDay <= 9);
        sb.AppendLine($"| `runs_5000.jsonl` 精选 Run 数 | {sampling.SelectedRuns.Count:N0} |");
        sb.AppendLine($"| 其中早死/早退局（FinalDay <= 9） | {earlyRunsCount:N0} ({(sampling.SelectedRuns.Count > 0 ? earlyRunsCount / (double)sampling.SelectedRuns.Count : 0):P1}) |");
        sb.AppendLine($"| `runs_5000.jsonl` 含逐日快照总数 | {selectedSnapshotsTotal:N0} |");
        sb.AppendLine($"| `ghost_pool_balanced.jsonl` 480格均衡镜像池 | {sampling.BalancedGhostPool.Count:N0} |");
        sb.AppendLine($"| `ghost_pool_natural.jsonl` 自然漏斗单日池 | {sampling.NaturalGhostPool.Count:N0} |");
        sb.AppendLine($"| SQLite 写入去重盘面总数 | {uniqueList.Count:N0} |");
        sb.AppendLine();

        // 4. 15 × 8 均衡池英雄覆盖表
        sb.AppendLine("## 二、均衡镜像池 15 天桶 × 8 英雄覆盖分布");
        sb.AppendLine();
        sb.Append("| 天桶 |");
        foreach (var h in HeroNames.PlayableHeroes)
            sb.Append($" {h} |");
        sb.AppendLine(" 合计 |");

        sb.Append("| --- |");
        foreach (var _ in HeroNames.PlayableHeroes)
            sb.Append(" ---: |");
        sb.AppendLine(" ---: |");

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var list = sampling.BalancedPoolByDayBucket.TryGetValue(b, out var l) ? l : new List<PvpBoardSnapshot>();
            sb.Append($"| {DayBucketHelper.GetDayLabel(b)} |");
            foreach (var h in HeroNames.PlayableHeroes)
            {
                int cnt = list.Count(s => string.Equals(s.Hero, h, StringComparison.OrdinalIgnoreCase));
                sb.Append($" {cnt} |");
            }
            sb.AppendLine($" **{list.Count}** |");
        }
        sb.AppendLine();

        // 5. 15 × 4 强度档分布与各档实测平均胜率
        sb.AppendLine("## 三、强度分档（15 × 4）分布与各档实测平均胜率");
        sb.AppendLine();
        sb.AppendLine("### 1. 均衡池按天桶 × 强度档分布（并核查 15×8×4 = 480 子单元空缺数）");
        sb.AppendLine();
        int emptySubCells = 0;
        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var list = sampling.BalancedPoolByDayBucket.TryGetValue(b, out var l) ? l : new List<PvpBoardSnapshot>();
            foreach (var h in HeroNames.PlayableHeroes)
                foreach (var pb in DayBucketHelper.AllPowerBuckets)
                    if (!list.Any(s => string.Equals(s.Hero, h, StringComparison.OrdinalIgnoreCase) && s.PowerBucket == pb))
                        emptySubCells++;
        }
        sb.AppendLine($"- **480 网格（15 天 × 8 英雄 × 4 强度）空缺格数**：`{emptySubCells}` / 480");
        sb.AppendLine();

        sb.AppendLine("| 天桶 | Weak (&lt;0.25) | Normal ([0.25,0.60)) | Strong ([0.60,0.88)) | MetaBoss (&gt;=0.88) | 合计 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var list = sampling.BalancedPoolByDayBucket.TryGetValue(b, out var l) ? l : new List<PvpBoardSnapshot>();
            sb.Append($"| {DayBucketHelper.GetDayLabel(b)} |");
            foreach (var pb in DayBucketHelper.AllPowerBuckets)
                sb.Append($" {list.Count(s => s.PowerBucket == pb)} |");
            sb.AppendLine($" {list.Count} |");
        }
        sb.AppendLine();

        sb.AppendLine("### 2. 四档强度全局实测平均胜率分层");
        sb.AppendLine();
        sb.AppendLine("| 强度档位 | 均衡池样本数 | 全量去重样本数 | 实测平均交叉胜率 |");
        sb.AppendLine("| --- | ---: | ---: | ---: |");
        foreach (var pb in DayBucketHelper.AllPowerBuckets)
        {
            var poolMatches = sampling.BalancedGhostPool.Where(s => s.PowerBucket == pb).ToList();
            var allMatches = uniqueList.Where(s => s.PowerBucket == pb).ToList();
            double avgWr = allMatches.Count > 0 ? allMatches.Average(s => s.EstimatedWinRate) : 0.0;
            sb.AppendLine($"| `{pb}` | {poolMatches.Count:N0} | {allMatches.Count:N0} | {avgWr:P2} |");
        }
        sb.AppendLine();

        // 6. Day 1 -> Day 15+ 发育与战力成长曲线（含平均附魔件数、平均物品件数与核心流派命中率）
        var curveSource = selectedSnapshotsTotal > 0
            ? sampling.SelectedRuns.SelectMany(r => r.DayBoards).ToList()
            : uniqueList;

        sb.AppendLine("## 四、Day 1 → Day 15+ 发育与战力成长曲线");
        sb.AppendLine();
        sb.AppendLine("| 天桶 | 盘面数 | 平均血量上限 | 平均等级 | 平均解锁格数 | 平均物品件数 | 平均附魔件数 | 核心流派命中率 | Silver+ 占比 | Gold+ 占比 | 物品附魔率 | 场均伤害 | 平均时长 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var daySnaps = curveSource.Where(s => DayBucketHelper.GetDayBucket(s.Day) == b).ToList();
            if (daySnaps.Count == 0)
            {
                sb.AppendLine($"| {DayBucketHelper.GetDayLabel(b)} | 0 | - | - | - | - | - | - | - | - | - | - | - |");
                continue;
            }

            double avgHp = daySnaps.Average(s => s.HealthMax);
            double avgLvl = daySnaps.Average(s => s.Level);
            double avgUnlocked = daySnaps.Average(s => BitOperations.PopCount((uint)(s.UnlockedSlotsMask & 0x3FF)));
            double avgItemsPerBoard = daySnaps.Average(s => s.Items.Count);
            double avgEnchantsPerBoard = daySnaps.Average(s => s.Items.Count(it => !string.IsNullOrEmpty(it.Enchantment)));
            double coreHitRate = ComputeCoreHitRate(daySnaps);

            long totalItems = 0, silverPlusItems = 0, goldPlusItems = 0, enchantedItems = 0;
            foreach (var s in daySnaps)
            {
                foreach (var it in s.Items)
                {
                    totalItems++;
                    if (it.Tier >= CardTier.Silver) silverPlusItems++;
                    if (it.Tier >= CardTier.Gold) goldPlusItems++;
                    if (!string.IsNullOrEmpty(it.Enchantment)) enchantedItems++;
                }
            }

            double silverPlusPct = totalItems > 0 ? (double)silverPlusItems / totalItems : 0.0;
            double goldPlusPct = totalItems > 0 ? (double)goldPlusItems / totalItems : 0.0;
            double enchantPct = totalItems > 0 ? (double)enchantedItems / totalItems : 0.0;

            var withOutcome = daySnaps.Where(s => s.MatchOutcome != null).ToList();
            double avgDmg = withOutcome.Count > 0 ? withOutcome.Average(s => s.MatchOutcome!.DamageDealt) : 0.0;
            double avgDurSec = withOutcome.Count > 0 ? withOutcome.Average(s => s.MatchOutcome!.CombatDurationMs) / 1000.0 : 0.0;

            sb.AppendLine($"| {DayBucketHelper.GetDayLabel(b)} | {daySnaps.Count:N0} | {avgHp:F1} | {avgLvl:F2} | {avgUnlocked:F2} | {avgItemsPerBoard:F2} | {avgEnchantsPerBoard:F2} | {coreHitRate:P1} | {silverPlusPct:P1} | {goldPlusPct:P1} | {enchantPct:P1} | {avgDmg:F1} | {avgDurSec:F2}s |");
        }
        sb.AppendLine();

        // 7. 8 种画像从 Day 1 → Day 15+ 自然存活占比漏斗表
        AppendPersonaSurvivalFunnelSection(sb, tournament, sampling, curveSource);

        // 8. Day 1 / Day 5 / Day 10 / Day 14 代表性 MetaBoss 与 CluelessNewbie/Weak 实录展示
        AppendRepresentativeSamplesSection(sb, uniqueList);

        // 9. 局内/局外成长与数值收敛统计
        AppendPermanentGrowthAndConvergenceSection(sb, curveSource);

        // 10. 5.0.0 社区 10 胜参考蓝本清单与高天对标
        AppendCommunityTenWinAlignmentSection(sb, uniqueList);

        // 11. 引擎合法性与千场实战回放体检
        sb.AppendLine("## 九、合法性全量校验与千场战斗引擎回放体检");
        sb.AppendLine();
        sb.AppendLine($"- **`IBoardValidator` 全量校验盘面数**：`{uniqueList.Count:N0}`");
        sb.AppendLine($"- **合法性校验失败数**：`{validationErrCount}`");
        sb.AppendLine($"- **缺失官方中文名条目数**：`{missingZhCount}`");
        sb.AppendLine($"- **`CombatSimulator` 抽样完整对局数**：`{replayResult.SimulatedCount:N0}`");
        sb.AppendLine($"- **战斗模拟异常数**：`{replayResult.ExceptionCount}`");
        sb.AppendLine($"- **战斗超时/疑似卡死数（>= 120s 或抛出异常）**：`{replayResult.TimeoutCount}`");
        sb.AppendLine($"- **抽样回放平均战斗时长**：`{replayResult.AvgDurationMs:F0} ms`（总回放耗时 `{replayResult.ElapsedMs:N0} ms`）");

        if (sampleErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### 校验异常样本明细（前 10 条）");
            foreach (var err in sampleErrors)
                sb.AppendLine($"- {err}");
        }

        File.WriteAllText(reportPath, sb.ToString(), Utf8NoBom);

        return new DatasetReportResult
        {
            ReportPath = reportPath,
            TotalValidatedBoards = uniqueList.Count,
            ValidationErrors = validationErrCount,
            MissingZhNames = missingZhCount,
            ReplayMatchesCount = replayResult.SimulatedCount,
            ReplayExceptions = replayResult.ExceptionCount,
            ReplayTimeouts = replayResult.TimeoutCount
        };
    }

    private double ComputeCoreHitRate(List<PvpBoardSnapshot> daySnaps)
    {
        if (_catalog == null || daySnaps.Count == 0)
            return 0.0;

        int hits = 0;
        foreach (var s in daySnaps)
        {
            var arch = _catalog.GetById(s.Archetype);
            if (arch == null || arch.CoreItemNames.Count == 0) continue;
            bool hasCore = s.Items.Any(it => arch.CoreItemNames.Contains(it.InternalName, StringComparer.OrdinalIgnoreCase));
            if (hasCore)
                hits++;
        }
        return (double)hits / daySnaps.Count;
    }

    private static void AppendPersonaSurvivalFunnelSection(
        StringBuilder sb,
        TournamentResult? tournament,
        SamplingResult sampling,
        List<PvpBoardSnapshot> fallbackCurveSource)
    {
        sb.AppendLine("## 五、8 种画像从 Day 1 → Day 15+ 自然存活占比漏斗");
        sb.AppendLine();

        // 优先基于主淘汰赛（排除 run_sup_ 无尽补充）展现真实淘汰漏斗；若未传入则回退至自然漏斗池
        List<PvpBoardSnapshot> funnelSource;
        string sourceDesc;
        if (tournament != null)
        {
            var mainOnly = tournament.AllSnapshots
                .Where(s => !s.RunId.StartsWith("run_sup_", StringComparison.Ordinal))
                .ToList();
            funnelSource = mainOnly.Count > 0 ? mainOnly : tournament.AllSnapshots.ToList();
            sourceDesc = "主淘汰赛自然存活轨迹（不含深水无尽补齐）";
        }
        else if (sampling.NaturalGhostPool.Count > 0)
        {
            funnelSource = sampling.NaturalGhostPool.ToList();
            sourceDesc = "自然漏斗单日池 (`ghost_pool_natural.jsonl`)";
        }
        else
        {
            funnelSource = fallbackCurveSource;
            sourceDesc = "精选 Run 盘面集合";
        }

        sb.AppendLine($"- **统计口径**：{sourceDesc}");
        sb.AppendLine();

        var personas = Enum.GetValues<PersonaType>();
        sb.Append("| 天桶 | 存活样本数 | 低分/失误组占比 |");
        foreach (var p in personas)
            sb.Append($" {p} |");
        sb.AppendLine();

        sb.Append("| --- | ---: | ---: |");
        foreach (var _ in personas)
            sb.Append(" ---: |");
        sb.AppendLine();

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var list = funnelSource.Where(s => DayBucketHelper.GetDayBucket(s.Day) == b).ToList();
            if (list.Count == 0)
            {
                sb.Append($"| {DayBucketHelper.GetDayLabel(b)} | 0 | - |");
                foreach (var _ in personas) sb.Append(" - |");
                sb.AppendLine();
                continue;
            }

            int lowCnt = list.Count(s => TournamentRunner.IsLowTierPersona(s.Persona));
            double lowPct = (double)lowCnt / list.Count;
            sb.Append($"| {DayBucketHelper.GetDayLabel(b)} | {list.Count:N0} | {lowPct:P1} |");
            foreach (var p in personas)
            {
                double pct = list.Count(s => s.Persona == p) / (double)list.Count;
                sb.Append($" {pct:P1} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
    }

    private static void AppendRepresentativeSamplesSection(StringBuilder sb, List<PvpBoardSnapshot> uniqueList)
    {
        sb.AppendLine("## 六、代表性盘面实录对比（MetaBoss vs CluelessNewbie/Weak）");
        sb.AppendLine();

        int[] sampleDays = [1, 5, 10, 14];
        foreach (int targetDay in sampleDays)
        {
            var dayPool = uniqueList.Where(s => s.Day == targetDay).ToList();
            if (dayPool.Count == 0) continue;

            var boss = dayPool
                .Where(s => s.PowerBucket == PowerBucket.MetaBoss)
                .OrderByDescending(s => s.EstimatedWinRate)
                .ThenByDescending(s => s.Items.Count)
                .FirstOrDefault()
                ?? dayPool.OrderByDescending(s => s.EstimatedWinRate).First();

            var newbie = dayPool
                .Where(s => s.Persona == PersonaType.CluelessNewbie && s.PowerBucket == PowerBucket.Weak)
                .OrderBy(s => s.EstimatedWinRate)
                .FirstOrDefault()
                ?? dayPool.Where(s => s.Persona == PersonaType.CluelessNewbie).OrderBy(s => s.EstimatedWinRate).FirstOrDefault()
                ?? dayPool.Where(s => s.PowerBucket == PowerBucket.Weak).OrderBy(s => s.EstimatedWinRate).FirstOrDefault()
                ?? dayPool.OrderBy(s => s.EstimatedWinRate).First();

            sb.AppendLine($"### Day {targetDay} 实录对照");
            sb.AppendLine(FormatSampleLine("天胡强阵 (MetaBoss)", boss));
            sb.AppendLine(FormatSampleLine("萌新/弱阵 (CluelessNewbie/Weak)", newbie));
            sb.AppendLine();
        }
    }

    private static string FormatSampleLine(string label, PvpBoardSnapshot s)
    {
        var itemsText = s.Items.Count == 0
            ? "(空场)"
            : string.Join("、", s.Items.OrderBy(i => i.Slot).Select(i =>
            {
                string ench = string.IsNullOrEmpty(i.Enchantment) ? string.Empty : $"/{EnchantmentNames.GetChineseName(i.Enchantment)}";
                return $"[{i.Slot}..{i.Slot + i.Width - 1}]{i.ZhName}({i.Tier.ToChineseName()}{ench})";
            }));
        string skillsText = s.Skills.Count == 0
            ? "无"
            : string.Join("、", s.Skills.Select(sk => $"{sk.ZhName}({sk.Tier.ToChineseName()})"));

        return $"- **{label}**：`{s.BoardId}` | 英雄=`{s.Hero}` | 画像=`{s.Persona}` | 流派=`{s.Archetype}` | 强度=`{s.PowerBucket}` ({s.EstimatedWinRate:P1}) | Lv.{s.Level} HP={s.HealthMax} ({s.Wins}胜{s.Losses}负)\n"
             + $"  - 物品：{itemsText}\n"
             + $"  - 技能：{skillsText}";
    }

    private static void AppendPermanentGrowthAndConvergenceSection(StringBuilder sb, List<PvpBoardSnapshot> curveSource)
    {
        sb.AppendLine("## 七、局内/局外成长与数值收敛统计");
        sb.AppendLine();

        var day8Plus = curveSource.Where(s => s.Day >= 8).ToList();
        if (day8Plus.Count > 0)
        {
            int grownBoardsD8Plus = day8Plus.Count(s => s.Items.Any(it => it.PermanentAttributes.Count > 0));
            int totalItemsD8Plus = day8Plus.Sum(s => s.Items.Count);
            int grownItemsD8Plus = day8Plus.Sum(s => s.Items.Count(it => it.PermanentAttributes.Count > 0));
            double avgEnchD8Plus = day8Plus.Average(s => s.Items.Count(it => !string.IsNullOrEmpty(it.Enchantment)));
            sb.AppendLine($"- **Day 8+ 成长覆盖总览**（样本 {day8Plus.Count:N0}）：含非空 `attribute_mods` 盘面占比 `{(double)grownBoardsD8Plus / day8Plus.Count:P1}`（{grownBoardsD8Plus:N0}/{day8Plus.Count:N0}），含 `attribute_mods` 物品占比 `{(totalItemsD8Plus > 0 ? (double)grownItemsD8Plus / totalItemsD8Plus : 0):P1}`，平均每盘附魔件数 `{avgEnchD8Plus:F2}`");
            sb.AppendLine();
        }

        sb.AppendLine("| 天桶 | 盘面数 | 平均 `health_max` | 含非空 `attribute_mods` 盘面占比 | 含 `attribute_mods` 物品占比 | 平均每盘附魔件数 |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");

        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var list = curveSource.Where(s => DayBucketHelper.GetDayBucket(s.Day) == b).ToList();
            if (list.Count == 0)
            {
                sb.AppendLine($"| {DayBucketHelper.GetDayLabel(b)} | 0 | - | - | - | - |");
                continue;
            }

            double avgHp = list.Average(s => s.HealthMax);
            int boardsWithMods = list.Count(s => s.Items.Any(it => it.PermanentAttributes.Count > 0));
            long totalItems = 0;
            long itemsWithMods = 0;
            long enchTotal = 0;

            foreach (var s in list)
            {
                foreach (var it in s.Items)
                {
                    totalItems++;
                    if (it.PermanentAttributes.Count > 0) itemsWithMods++;
                    if (!string.IsNullOrEmpty(it.Enchantment)) enchTotal++;
                }
            }

            double boardModsPct = (double)boardsWithMods / list.Count;
            double itemModsPct = totalItems > 0 ? (double)itemsWithMods / totalItems : 0.0;
            double avgEnchPerBoard = (double)enchTotal / list.Count;

            sb.AppendLine($"| {DayBucketHelper.GetDayLabel(b)} | {list.Count:N0} | {avgHp:F1} | {boardModsPct:P1} | {itemModsPct:P1} | {avgEnchPerBoard:F2} |");
        }
        sb.AppendLine();
    }

    private static void AppendCommunityTenWinAlignmentSection(StringBuilder sb, List<PvpBoardSnapshot> uniqueList)
    {
        sb.AppendLine("## 八、5.0.0 社区 10 胜参考蓝本清单与高天对标");
        sb.AppendLine();

        var specs = CommunityTenWinBlueprints.GetAll();
        var specsByHero = specs
            .GroupBy(s => s.Hero, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var highDayTop = uniqueList
            .Where(s => s.Day >= 10 && s.PowerBucket is PowerBucket.Strong or PowerBucket.MetaBoss)
            .ToList();

        int exactArchHits = 0;
        int coreComboHits = 0;

        foreach (var s in highDayTop)
        {
            if (!specsByHero.TryGetValue(s.Hero, out var heroSpecs)) continue;
            if (heroSpecs.Any(sp => string.Equals(sp.ArchetypeId, s.Archetype, StringComparison.OrdinalIgnoreCase)))
                exactArchHits++;
            if (heroSpecs.Any(sp => s.Items.Count(it => sp.ItemInternalNames.Contains(it.InternalName, StringComparer.OrdinalIgnoreCase)) >= 2))
                coreComboHits++;
        }

        sb.AppendLine($"- **社区 10 胜参考蓝本总数**：`{specs.Count}` 套（覆盖 {specsByHero.Count} 位英雄）");
        if (highDayTop.Count > 0)
        {
            sb.AppendLine($"- **高天（Day 10+）`Strong` / `MetaBoss` 盘面总数**：`{highDayTop.Count:N0}`");
            sb.AppendLine($"- **同流派精确命中（ArchetypeId 一致）**：`{exactArchHits:N0}` / `{highDayTop.Count:N0}`（`{(double)exactArchHits / highDayTop.Count:P1}`）");
            sb.AppendLine($"- **核心组件协同命中（同英雄任意 10 胜蓝本命中 >= 2 件）**：`{coreComboHits:N0}` / `{highDayTop.Count:N0}`（`{(double)coreComboHits / highDayTop.Count:P1}`）");
        }
        sb.AppendLine();

        if (highDayTop.Count > 0)
        {
            sb.AppendLine("### 1. 分英雄高天强势盘面对标统计");
            sb.AppendLine();
            sb.AppendLine("| 英雄 | 蓝本数 | Day 10+ Strong/MetaBoss 数 | 同流派命中数 | 核心组件(>=2件)命中数 | 核心命中率 |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                int specCount = specsByHero.TryGetValue(hero, out var hSpecs) ? hSpecs.Count : 0;
                var hBoards = highDayTop.Where(s => string.Equals(s.Hero, hero, StringComparison.OrdinalIgnoreCase)).ToList();
                int hArch = 0, hCombo = 0;
                if (hSpecs != null)
                {
                    foreach (var s in hBoards)
                    {
                        if (hSpecs.Any(sp => string.Equals(sp.ArchetypeId, s.Archetype, StringComparison.OrdinalIgnoreCase)))
                            hArch++;
                        if (hSpecs.Any(sp => s.Items.Count(it => sp.ItemInternalNames.Contains(it.InternalName, StringComparer.OrdinalIgnoreCase)) >= 2))
                            hCombo++;
                    }
                }
                double rate = hBoards.Count > 0 ? (double)hCombo / hBoards.Count : 0.0;
                sb.AppendLine($"| {hero} | {specCount} | {hBoards.Count:N0} | {hArch:N0} | {hCombo:N0} | {rate:P1} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### 2. 全 8 英雄 32 套 5.0.0 社区 10 胜参考蓝本清单");
        sb.AppendLine();
        sb.AppendLine("| BlueprintId | 英雄 | 蓝本名称 | 关联流派 ID | 成长与协同机制概要 (`ScalingNote`) |");
        sb.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var sp in specs)
        {
            string noteClean = sp.ScalingNote.Replace("|", "\\|");
            sb.AppendLine($"| `{sp.BlueprintId}` | {sp.Hero} | {sp.TitleZh} | `{sp.ArchetypeId}` | {noteClean} |");
        }
        sb.AppendLine();
    }

    private (int SimulatedCount, int ExceptionCount, int TimeoutCount, double AvgDurationMs, long ElapsedMs) RunCombatReplayAudit(
        IReadOnlyList<PvpBoardSnapshot> balancedPool,
        IReadOnlyList<PvpBoardSnapshot> fallbackList,
        DatasetGenOptions options,
        int targetMatches)
    {
        var source = balancedPool.Count >= 2 ? balancedPool : fallbackList;
        if (source.Count < 2 || targetMatches <= 0)
            return (0, 0, 0, 0, 0);

        var sw = Stopwatch.StartNew();
        var byBucket = source.GroupBy(b => DayBucketHelper.GetDayBucket(b.Day))
            .Where(g => g.Count() >= 2)
            .Select(g => g.ToList())
            .ToList();

        var rng = new Random(options.Seed ^ 0x4E91);
        var pairs = new List<(PvpBoardSnapshot A, PvpBoardSnapshot B, int Seed)>(targetMatches);
        for (int i = 0; i < targetMatches; i++)
        {
            var bucketList = byBucket.Count > 0 ? byBucket[i % byBucket.Count] : source.ToList();
            var a = bucketList[rng.Next(bucketList.Count)];
            var b = bucketList[rng.Next(bucketList.Count)];
            if (ReferenceEquals(a, b) && bucketList.Count > 1)
                b = bucketList[(bucketList.IndexOf(a) + 1) % bucketList.Count];
            pairs.Add((a, b, unchecked(options.Seed + i * 97)));
        }

        int exceptions = 0;
        int timeouts = 0;
        long durationSum = 0;

        var parallelOpts = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism };
        Parallel.ForEach(pairs, parallelOpts, pair =>
        {
            try
            {
                var p1 = _validator.ToPlayerState(pair.A, _repo);
                var p2 = DayBucketHelper.EnsureDistinctOpponentState(p1, _validator.ToPlayerState(pair.B, _repo));
                var res = DayBucketHelper.SimulateHeadless(_repo, p1, p2, pair.Seed, "PVP");
                Interlocked.Add(ref durationSum, res.DurationMs);
                if (res.DurationMs >= 120_000 && res.IsDraw)
                    Interlocked.Increment(ref timeouts);
            }
            catch
            {
                Interlocked.Increment(ref exceptions);
            }
        });

        sw.Stop();
        double avgDur = pairs.Count > 0 ? (double)durationSum / pairs.Count : 0.0;
        return (pairs.Count, exceptions, timeouts, avgDur, sw.ElapsedMilliseconds);
    }
}

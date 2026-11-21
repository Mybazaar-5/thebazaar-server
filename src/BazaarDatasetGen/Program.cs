using System.Diagnostics;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Models;

namespace BazaarDatasetGen;

/// <summary>
/// PvP 数据集全量生成管线与外部真实社区数据合并工具命令行入口。
/// 阶段 2 已直接静态装配 ArchetypeCatalog、FastRunSimulator 与 BoardValidator。
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        var options = DatasetGenOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(DatasetGenOptions.GetHelpText());
            return 0;
        }

        var repo = GameDatabaseLoader.Load(options.DataPath, options.TranslationPath);

        // 1. 外部真实社区数据导入模式 (--import-real <path>)
        if (!string.IsNullOrWhiteSpace(options.ImportRealPath))
        {
            var boardValidator = new BoardValidator();
            var importer = new RealDataImporter(repo, boardValidator);
            var importRes = importer.Import(options.ImportRealPath, options.OutputDir, options.RequireOfficialZh, Console.WriteLine);
            Console.WriteLine($"导入完成：读取 {importRes.TotalBoardsRead}，新增 Run {importRes.ImportedRuns}，新增盘面 {importRes.ImportedBoards}，重复跳过 {importRes.SkippedDuplicateBoards}，失败 {importRes.FailedValidationCount}");
            return importRes.FailedValidationCount > 0 && importRes.ImportedBoards == 0 ? 1 : 0;
        }

        // 2. 全量合成与四层真值融合生成管线模式
        CompassDataIntegrator.CompassDistilledBundle? compassBundle = null;
        IArchetypeCatalog catalog;
        if (!string.IsNullOrWhiteSpace(options.CompassDataPath))
        {
            compassBundle = CompassDataIntegrator.LoadBundle(options.CompassDataPath);
            CompassDataIntegrator.RegisterAvailability(compassBundle);
            catalog = CompassDataIntegrator.CreateCompassFusedCatalog(repo, compassBundle);
            Console.WriteLine($"[罗盘融合] 已加载蒸馏真值（天梯/标杆={compassBundle.Anchors.Count}, 真棋盘={compassBundle.Boards.Count}, 构筑={compassBundle.Builds.Count}），融合流派目录共 {catalog.All.Count} 套（覆盖 {HeroNames.PlayableHeroes.Length} 英雄）");
        }
        else
        {
            catalog = ArchetypeCatalog.CreateExpanded(repo);
            Console.WriteLine($"[流派目录] 已加载扩展蓝图共 {catalog.All.Count} 套（覆盖 {HeroNames.PlayableHeroes.Length} 英雄）");
        }

        var simulator = new FastRunSimulator();
        var validator = new BoardValidator();

        try
        {
            RunPipeline(options, repo, catalog, simulator, validator, Console.WriteLine, compassBundle: compassBundle);
        }
        finally
        {
            CompassAvailabilityRegistry.Clear();
        }
        return 0;
    }

    /// <summary>
    /// 执行从并发淘汰赛 -> 罗盘/社区真值水合合并 -> 同天交叉定级 -> 均衡采样 -> 多格式导出 -> 体检报告的全套生成管线。
    /// </summary>
    public static (TournamentResult Tournament, CrossValidationResult Grading, SamplingResult Sampling, DatasetReportResult Report) RunPipeline(
        DatasetGenOptions options,
        GameDataRepository repo,
        IArchetypeCatalog catalog,
        IFastRunSimulator simulator,
        IBoardValidator validator,
        Action<string>? log = null,
        int replayTargetMatches = 1200,
        CompassDataIntegrator.CompassDistilledBundle? compassBundle = null)
    {
        var totalSw = Stopwatch.StartNew();
        log?.Invoke($"[管线启动] 淘汰赛={options.TournamentSize}, 目标Run={options.TargetRuns}, 每日均衡池={options.PoolPerDay}, 最大天数={options.MaxDay}, 线程={options.MaxDegreeOfParallelism}, 输出={options.OutputDir}");

        // 阶段 A：多核并发大逃杀淘汰赛与深水区补齐
        var tournamentRunner = new TournamentRunner(repo, catalog, simulator, validator);
        var tournament = tournamentRunner.Run(options, log);

        // 阶段 A+B 融合：注入罗盘四层水合盘（优先）或社区十胜真值盘参与交叉定级与均衡池采样
        IReadOnlyList<PvpBoardSnapshot> gradingInputs = tournament.AllSnapshots;
        IReadOnlyList<PvpRunRecord> samplingRunsInput = tournament.AllRuns;
        if (compassBundle != null)
        {
            int limit = options.TournamentSize < 500 ? 64 : 0;
            var (commRuns, commSnaps) = CompassDataIntegrator.BuildHydratedRealPool(
                repo, validator, compassBundle, options.Seed, options.RequireOfficialZh, limit);
            if (commSnaps.Count > 0)
            {
                gradingInputs = tournament.AllSnapshots.Concat(commSnaps).ToList();
                samplingRunsInput = tournament.AllRuns.Concat(commRuns).ToList();
                log?.Invoke($"[四层融合] 已注入罗盘+社区水合真值轨迹 {commRuns.Count} 条 / 盘面 {commSnaps.Count} 张送入实战定级");
            }
        }
        else if (catalog is ArchetypeCatalog && options.TournamentSize >= 1000)
        {
            var community = CommunityGhostHydrator.BuildCommunitySnapshots(repo, validator, options.Seed, options.RequireOfficialZh);
            if (community.CommunitySnapshots.Count > 0)
            {
                gradingInputs = tournament.AllSnapshots.Concat(community.CommunitySnapshots).ToList();
                samplingRunsInput = tournament.AllRuns.Concat(community.CommunityRuns).ToList();
                log?.Invoke($"[双源融合] 已水合社区十胜真值轨迹 {community.CommunityRuns.Count} 条 / 盘面 {community.CommunitySnapshots.Count} 张送入实战定级");
            }
        }

        // 阶段 B：15 天桶同天 20 基准循环交叉定级与满覆盖扩样
        var grader = new CrossValidationGrader(repo, validator);
        var grading = grader.Grade(gradingInputs, options, log);

        // 阶段 C：精选完整 Run 与均衡单日镜像池采样
        var sampler = new BalancedPoolSampler(validator);
        var sampling = sampler.Sample(samplingRunsInput, options, grader, grading, log);

        // 阶段 D：多格式导出（JSONL + 分 Day JSON + SQLite 4 表与索引）
        var exporter = new DatasetExporter(validator);
        exporter.Export(sampling, options, log);

        // 阶段 E：全量合法性校验、千场 CombatSimulator 回放体检与简体中文报告输出
        var reportGen = new DatasetReportGenerator(repo, validator, catalog);
        var report = reportGen.Generate(sampling, tournament, grading, options, replayTargetMatches: replayTargetMatches, log: log);

        totalSw.Stop();
        log?.Invoke($"[管线完成] 总耗时 {totalSw.Elapsed.TotalSeconds:F2}s，报告已写入 {report.ReportPath}");
        return (tournament, grading, sampling, report);
    }
}

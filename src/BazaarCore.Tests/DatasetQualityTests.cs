using System.Numerics;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Dataset;
using BazaarCore.Dataset.Archetypes;
using BazaarCore.Dataset.Simulation;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarDatasetGen;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// 工作线 D 质量校验套件：
/// 1. 使用轻量契约 Stub 验证淘汰赛、交叉定级、480 格均衡采样、多格式导出、SQLite 4 表索引、体检报告与 --import-real 幂等并池；
/// 2. 在环境变量 BAZAAR_VERIFY_OUTPUT=1 时对生产输出目录执行第五节全量自动化验收。
/// </summary>
public sealed class DatasetQualityTests
{
    private readonly ITestOutputHelper _output;

    public DatasetQualityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    [Fact]
    public void ShouldParseCliOptionsProperly()
    {
        var defaults = DatasetGenOptions.Parse(Array.Empty<string>());
        Assert.Equal(24_000, defaults.TournamentSize);
        Assert.Equal(12_000, defaults.TargetRuns);
        Assert.Equal(480, defaults.PoolPerDay);
        Assert.Equal(1_250, defaults.NaturalPoolEarlyPerDay);
        Assert.Equal(400, defaults.NaturalPoolLateMinPerDay);
        Assert.Equal(17, defaults.MaxDay);
        Assert.Equal(20, defaults.BenchmarkOpponentsPerDay);
        Assert.True(defaults.RequireOfficialZh);
        Assert.Null(defaults.ImportRealPath);

        var custom = DatasetGenOptions.Parse(
        [
            "--tournament-size", "1200",
            "--runs", "8000",
            "--pool-per-day", "64",
            "--max-day", "16",
            "--benchmarks", "16",
            "--threads", "4",
            "--seed", "99",
            "--out", @"e:\tmp\out",
            "--import-real", @"e:\tmp\real.jsonl",
            "--allow-non-zh"
        ]);
        Assert.Equal(1200, custom.TournamentSize);
        Assert.Equal(8000, custom.TargetRuns);
        Assert.Equal(64, custom.PoolPerDay);
        Assert.Equal(16, custom.MaxDay);
        Assert.Equal(16, custom.BenchmarkOpponentsPerDay);
        Assert.Equal(4, custom.MaxDegreeOfParallelism);
        Assert.Equal(99, custom.Seed);
        Assert.Equal(@"e:\tmp\out", custom.OutputDir);
        Assert.Equal(@"e:\tmp\real.jsonl", custom.ImportRealPath);
        Assert.False(custom.RequireOfficialZh);

        var legacyAlias = DatasetGenOptions.Parse(["--target-runs", "400"]);
        Assert.Equal(400, legacyAlias.TargetRuns);
    }

    [Fact]
    public void ShouldClassifyPowerBucketsStrictlyByThresholds()
    {
        Assert.Equal(PowerBucket.Weak, DayBucketHelper.ClassifyPowerBucket(0.0));
        Assert.Equal(PowerBucket.Weak, DayBucketHelper.ClassifyPowerBucket(0.2499));
        Assert.Equal(PowerBucket.Normal, DayBucketHelper.ClassifyPowerBucket(0.25));
        Assert.Equal(PowerBucket.Normal, DayBucketHelper.ClassifyPowerBucket(0.5999));
        Assert.Equal(PowerBucket.Strong, DayBucketHelper.ClassifyPowerBucket(0.60));
        Assert.Equal(PowerBucket.Strong, DayBucketHelper.ClassifyPowerBucket(0.8799));
        Assert.Equal(PowerBucket.MetaBoss, DayBucketHelper.ClassifyPowerBucket(0.88));
        Assert.Equal(PowerBucket.MetaBoss, DayBucketHelper.ClassifyPowerBucket(1.0));
    }

    [Fact]
    public void ShouldRunEndToEndMiniPipelineWithStubsAndExportAllFormats()
    {
        var repo = Repo.Value;
        var catalog = new StubArchetypeCatalog();
        var validator = new StubBoardValidator();
        var simulator = new StubFastRunSimulator(validator);

        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_ws_d_mini_" + Guid.NewGuid().ToString("N"));
        try
        {
            var opts = new DatasetGenOptions
            {
                TournamentSize = 64,           // 8 英雄 × 8 局
                TargetRuns = 40,               // 8 英雄 × 5 局
                PoolPerDay = 32,               // 每日 32 条，15 天共 480 条
                MaxDay = 15,
                MinCandidatesPerDayHeroCell = 2,
                BenchmarkOpponentsPerDay = 8,
                MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
                Seed = 12345,
                OutputDir = tempDir,
                RequireOfficialZh = false
            };

            var (tournament, grading, sampling, report) = Program.RunPipeline(
                opts, repo, catalog, simulator, validator, log: msg => _output.WriteLine(msg), replayTargetMatches: 120);

            Assert.True(tournament.AllRuns.Count >= 64);
            Assert.Equal(40, sampling.SelectedRuns.Count);
            Assert.Equal(0, report.ValidationErrors);
            Assert.Equal(0, report.MissingZhNames);
            Assert.Equal(0, report.ReplayExceptions);
            Assert.Equal(0, report.ReplayTimeouts);
            Assert.True(report.ReplayMatchesCount >= 100);

            // 1. runs_5000.jsonl 校验（40 行，含真实中文且未转义为 \uXXXX）
            string runsPath = Path.Combine(tempDir, "runs_5000.jsonl");
            Assert.True(File.Exists(runsPath));
            var runLines = File.ReadAllLines(runsPath);
            Assert.Equal(40, runLines.Length);
            Assert.Contains(runLines[0], ch => ch >= '\u4e00' && ch <= '\u9fff');
            Assert.DoesNotContain(@"\u", runLines[0], StringComparison.OrdinalIgnoreCase);

            var parsedRuns = runLines
                .Select(l => JsonSerializer.Deserialize<PvpRunRecord>(l, PvpDatasetJson.PvpJsonOptions)!)
                .ToList();
            Assert.All(parsedRuns, r => Assert.True(r.DayBoards.Count > 0));
            foreach (var h in HeroNames.PlayableHeroes)
                Assert.Equal(5, parsedRuns.Count(r => r.Hero == h));

            // 2. ghost_pool_balanced.jsonl 校验
            string ghostPath = Path.Combine(tempDir, "ghost_pool_balanced.jsonl");
            Assert.True(File.Exists(ghostPath));
            var ghostLines = File.ReadAllLines(ghostPath);
            Assert.True(ghostLines.Length >= 240);
            var ghostBoards = ghostLines
                .Select(l => JsonSerializer.Deserialize<PvpBoardSnapshot>(l, PvpDatasetJson.PvpJsonOptions)!)
                .ToList();
            Assert.Equal(ghostBoards.Count, ghostBoards.Select(b => b.BoardId).Distinct().Count());

            // 2B. ghost_pool_natural.jsonl 自然漏斗池校验
            string naturalPath = Path.Combine(tempDir, "ghost_pool_natural.jsonl");
            Assert.True(File.Exists(naturalPath));
            var naturalBoards = File.ReadAllLines(naturalPath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<PvpBoardSnapshot>(l, PvpDatasetJson.PvpJsonOptions)!)
                .ToList();
            Assert.True(naturalBoards.Count > 0);
            Assert.Equal(naturalBoards.Count, naturalBoards.Select(b => b.BoardId).Distinct().Count());

            // 3. by_day/day_01.json .. day_15_plus.json 全部 15 个文件就位
            string byDayDir = Path.Combine(tempDir, "by_day");
            int byDaySum = 0;
            foreach (int b in DayBucketHelper.AllDayBuckets)
            {
                string f = Path.Combine(byDayDir, DayBucketHelper.GetDayFileName(b));
                Assert.True(File.Exists(f), $"缺少分天文件 {f}");
                var dayArr = JsonSerializer.Deserialize<List<PvpBoardSnapshot>>(
                    File.ReadAllText(f), PvpDatasetJson.PvpJsonOptions)!;
                byDaySum += dayArr.Count;
            }
            Assert.Equal(ghostBoards.Count, byDaySum);

            // 4. bazaar_pvp_dataset.db Schema、索引与数据校验
            string dbPath = Path.Combine(tempDir, "bazaar_pvp_dataset.db");
            Assert.True(File.Exists(dbPath));
            VerifySqliteSchemaAndCounts(
                dbPath,
                minRuns: 40,
                minBoards: sampling.SelectedRuns.Sum(r => r.DayBoards.Count),
                expectedBalancedCount: ghostBoards.Count,
                expectedNaturalCount: naturalBoards.Count);

            Assert.True(grading.CommunityBenchmarkSlotsUsed > 0, "Day 10+ 应混入 5.0.0 社区 10 胜考官席位");

            // 5. dataset_report.md 简体中文报告校验（含 v2 新增漏斗表、附魔均值、成长统计与社区 10 胜对标）
            Assert.True(File.Exists(report.ReportPath));
            string md = File.ReadAllText(report.ReportPath);
            Assert.Contains("《The Bazaar》PvP 逐日阵容数据集质量与统计报告", md);
            Assert.Contains("Day 1 → Day 15+", md);
            Assert.Contains("480 网格", md);
            Assert.Contains("8 种画像从 Day 1 → Day 15+ 自然存活占比漏斗", md);
            Assert.Contains("平均附魔件数", md);
            Assert.Contains("代表性盘面实录对比", md);
            Assert.Contains("局内/局外成长与数值收敛统计", md);
            Assert.Contains("5.0.0 社区 10 胜参考蓝本清单与高天对标", md);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ShouldMixCommunityTenWinBlueprintsIntoDay10PlusBenchmarks()
    {
        var repo = Repo.Value;
        var validator = new BoardValidator();
        var grader = new CrossValidationGrader(repo, validator);

        var boards = new List<PvpBoardSnapshot>();
        int[] testDays = [5, 10, 14];
        foreach (int day in testDays)
        {
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                var pool = repo.GetSpawnableItemsForHero(hero, includeCommon: true)
                    .Where(c => c.CardSize == CardSize.Small && c.Attributes != null)
                    .Take(6)
                    .ToList();
                if (pool.Count == 0)
                    pool = repo.AlwaysSpawnableItems.Where(c => c.CardSize == CardSize.Small).Take(6).ToList();

                for (int copy = 0; copy < 3; copy++)
                {
                    var tpl = pool[copy % pool.Count];
                    var tier = day >= 10 ? CardTier.Gold : CardTier.Bronze;
                    if (tpl.Tiers != null && tpl.Tiers.Count > 0 && !tpl.Tiers.ContainsKey(tier.ToString()))
                        tier = tpl.DefaultTier;

                    var snap = new PvpBoardSnapshot
                    {
                        BoardId = $"mix_d{day:00}_{hero}_{copy}",
                        RunId = $"mix_run_{hero}_{copy}",
                        Hero = hero,
                        Day = day,
                        Level = day + 1,
                        HealthMax = 300 + day * 350 + copy * 150,
                        Prestige = 15,
                        Wins = Math.Min(9, day - 1),
                        Losses = copy,
                        UnlockedSlotsMask = 1023,
                        Items =
                        [
                            new PvpItemSnapshot
                            {
                                Slot = 0,
                                Width = tpl.CardSize.SlotCount(),
                                TemplateId = tpl.Id,
                                InternalName = tpl.InternalName,
                                ZhName = repo.GetLocalizedTitle(tpl),
                                Tier = tier,
                                PermanentAttributes = copy > 0 ? new Dictionary<string, double> { ["DamageAmount"] = 30 + day * 5 } : new()
                            }
                        ]
                    };
                    snap.BoardSignature = validator.ComputeSignature(snap) + $"_c{copy}";
                    boards.Add(snap);
                }
            }
        }

        var opts = new DatasetGenOptions
        {
            BenchmarkOpponentsPerDay = 20,
            PoolPerDay = 32,
            MinCandidatesPerDayHeroCell = 2,
            MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount),
            Seed = 20260501
        };

        var grading = grader.Grade(boards, opts, msg => _output.WriteLine(msg));

        // Day 5 不应混入社区 10 胜参考盘；Day 10 与 Day 14 应精准包含 20%（4 / 20）社区 10 胜考官
        Assert.Equal(0, grading.MixedBenchmarksByDayBucket[5].Count(b => b.IsCommunityBlueprint));

        var d10Examiners = grading.MixedBenchmarksByDayBucket[10];
        var d14Examiners = grading.MixedBenchmarksByDayBucket[14];
        Assert.Equal(20, d10Examiners.Count);
        Assert.Equal(20, d14Examiners.Count);

        int d10Comm = d10Examiners.Count(b => b.IsCommunityBlueprint);
        int d14Comm = d14Examiners.Count(b => b.IsCommunityBlueprint);
        Assert.Equal(4, d10Comm);
        Assert.Equal(4, d14Comm);
        Assert.True(grading.CommunityBenchmarkSlotsUsed >= 8);

        // 验证 GradeMissingSnapshots 4 场全分段分层补测在 Day 10+ 同样稳定运行并固定含 1 位社区考官
        var extraItem = repo.AlwaysSpawnableItems.First(c => c.CardSize == CardSize.Small);
        var uncachedD14 = new PvpBoardSnapshot
        {
            BoardId = "uncached_d14_test",
            RunId = "uncached_run",
            Hero = HeroNames.Vanessa,
            Day = 14,
            Level = 15,
            HealthMax = 4500,
            UnlockedSlotsMask = 1023,
            Items =
            [
                new PvpItemSnapshot
                {
                    Slot = 4,
                    Width = 1,
                    TemplateId = extraItem.Id,
                    InternalName = extraItem.InternalName,
                    ZhName = repo.GetLocalizedTitle(extraItem),
                    Tier = extraItem.DefaultTier
                }
            ]
        };
        uncachedD14.BoardSignature = "uncached_sig_d14_" + Guid.NewGuid().ToString("N");
        var cacheCopy = new Dictionary<(int DayBucket, string Hero, string Signature), (double WinRate, PowerBucket Bucket)>(grading.RatingCache);
        int extraMatches = grader.GradeMissingSnapshots([uncachedD14], grading.MixedBenchmarksByDayBucket, cacheCopy, opts);
        Assert.Equal(4, extraMatches);
        Assert.InRange(uncachedD14.EstimatedWinRate, 0.0, 1.0);
    }

    [Fact]
    public void ShouldSampleAll480GridCellsWithoutDuplicatesWhenPoolProvidesCoverage()
    {
        var repo = Repo.Value;
        var validator = new StubBoardValidator();
        var item = repo.AlwaysSpawnableItems.First(i => i.CardSize == CardSize.Small);
        var skill = repo.AlwaysSpawnableSkills.First();

        // 构造覆盖 15 天桶 × 8 英雄 × 4 强度档 × 2 副本的合成测试全集（共 960 条不重复签名盘面）
        var runs = new List<PvpRunRecord>();
        var personas = Enum.GetValues<PersonaType>();
        int runCounter = 0;

        foreach (var hero in HeroNames.PlayableHeroes)
        {
            for (int copy = 0; copy < 12; copy++)
            {
                var isEarlyBust = copy < 4;
                var run = new PvpRunRecord
                {
                    RunId = $"syn_run_{runCounter++:0000}",
                    Hero = hero,
                    Persona = personas[copy % personas.Length],
                    Archetype = $"{hero}_Arch_{copy % 3}",
                    FinalDay = isEarlyBust ? 8 : 16,
                    FinalWins = isEarlyBust ? 3 : 10,
                    FinalLosses = isEarlyBust ? 4 : copy % 3,
                    IsVictory = !isEarlyBust
                };

                for (int day = 1; day <= 15; day++)
                {
                    var pb = DayBucketHelper.AllPowerBuckets[(copy + day) % 4];
                    double wr = pb switch
                    {
                        PowerBucket.Weak => 0.12,
                        PowerBucket.Normal => 0.42,
                        PowerBucket.Strong => 0.75,
                        _ => 0.94
                    };

                    var snap = new PvpBoardSnapshot
                    {
                        BoardId = $"b_{run.RunId}_d{day:00}",
                        RunId = run.RunId,
                        Hero = hero,
                        Persona = run.Persona,
                        Archetype = run.Archetype,
                        Day = day == 15 && copy % 2 == 1 ? 16 : day, // 验证 15+ 合并桶
                        Level = day + 1,
                        HealthMax = 250 + day * 150,
                        Prestige = 20 - (copy % 5),
                        Wins = Math.Min(10, day - 1),
                        Losses = copy % 3,
                        UnlockedSlotsMask = 1023,
                        PowerBucket = pb,
                        EstimatedWinRate = wr,
                        Items =
                        [
                            new PvpItemSnapshot
                            {
                                Slot = 0,
                                Width = 1,
                                TemplateId = item.Id,
                                InternalName = item.InternalName,
                                ZhName = "测试物品",
                                Tier = (CardTier)(copy % 4),
                                Enchantment = copy % 2 == 0 ? "Fiery" : "Deadly",
                                PermanentAttributes = new Dictionary<string, double>
                                {
                                    ["DamageAmount"] = 10 + copy * 5 + day * 2
                                }
                            }
                        ],
                        Skills =
                        [
                            new PvpSkillSnapshot
                            {
                                TemplateId = skill.Id,
                                InternalName = skill.InternalName,
                                ZhName = "测试技能",
                                Tier = CardTier.Bronze
                            }
                        ]
                    };
                    // 故意让 PermanentAttributes 或 Slot 微调产生唯一签名
                    snap.Items[0].Slot = copy % 8;
                    snap.BoardSignature = $"{item.Id}:{(CardTier)(copy % 4)}:{snap.Items[0].Enchantment}:{snap.Items[0].Slot}:d{day}:c{copy}";
                    run.DayBoards.Add(snap);
                }

                runs.Add(run);
            }
        }

        var sampler = new BalancedPoolSampler(validator);
        var opts = new DatasetGenOptions
        {
            TargetRuns = 40, // 每英雄 5 局
            PoolPerDay = 96, // 构造池共 8 英雄 × 12 副本 = 96 条/天，每强度 3 条
            Seed = 777
        };

        var sampling = sampler.Sample(runs, opts);
        Assert.Equal(40, sampling.SelectedRuns.Count);
        Assert.True(sampling.SelectedRuns.Any(r => r.FinalDay <= 9), "比例分层抽样应保留早死/早退局");
        foreach (var hero in HeroNames.PlayableHeroes)
            Assert.Equal(5, sampling.SelectedRuns.Count(r => r.Hero == hero));

        Assert.Equal(15 * 96, sampling.BalancedGhostPool.Count);
        Assert.True(sampling.NaturalGhostPool.Count > 0);

        // 严格核查 15 × 8 × 4 = 480 子单元无一空缺且单桶内无重复签名
        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var bucketList = sampling.BalancedPoolByDayBucket[b];
            Assert.Equal(96, bucketList.Count);
            Assert.Equal(bucketList.Count, bucketList.Select(s => s.Hero + "|" + s.BoardSignature).Distinct().Count());

            foreach (var h in HeroNames.PlayableHeroes)
                foreach (var pb in DayBucketHelper.AllPowerBuckets)
                    Assert.True(
                        bucketList.Any(s => s.Hero == h && s.PowerBucket == pb),
                        $"子单元 ({b}, {h}, {pb}) 不应为空");
        }
    }

    [Fact]
    public void ShouldImportRealCommunityDataIdempotentlyWithDualSourceMerge()
    {
        var repo = Repo.Value;
        var validator = new StubBoardValidator();
        var item = repo.AlwaysSpawnableItems.First(i => i.CardSize == CardSize.Small);
        var skill = repo.AlwaysSpawnableSkills.First();

        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_ws_d_import_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // 1. 先导出 1 条 synthetic_selfplay 数据到数据库，模拟双源并池起点
            var synRun = new PvpRunRecord
            {
                RunId = "syn_run_01",
                Source = "synthetic_selfplay",
                Hero = HeroNames.Vanessa,
                Persona = PersonaType.TempoAggro,
                Archetype = "Vanessa_Test",
                FinalDay = 3,
                FinalWins = 2,
                FinalLosses = 1,
                IsVictory = false,
                DayBoards =
                [
                    new PvpBoardSnapshot
                    {
                        BoardId = "syn_board_01",
                        RunId = "syn_run_01",
                        Source = "synthetic_selfplay",
                        Hero = HeroNames.Vanessa,
                        Day = 1,
                        Level = 1,
                        HealthMax = 250,
                        UnlockedSlotsMask = 1023,
                        Items =
                        [
                            new PvpItemSnapshot
                            {
                                Slot = 0, Width = 1, TemplateId = item.Id, InternalName = item.InternalName,
                                ZhName = "合成物品", Tier = CardTier.Bronze
                            }
                        ]
                    }
                ]
            };
            var sampling = new SamplingResult
            {
                SelectedRuns = [synRun],
                BalancedGhostPool = [synRun.DayBoards[0]],
                BalancedPoolByDayBucket = DayBucketHelper.AllDayBuckets.ToDictionary(
                    b => b, b => b == 1 ? new List<PvpBoardSnapshot> { synRun.DayBoards[0] } : new List<PvpBoardSnapshot>()),
                NaturalGhostPool = [synRun.DayBoards[0]],
                NaturalPoolByDayBucket = DayBucketHelper.AllDayBuckets.ToDictionary(
                    b => b, b => b == 1 ? new List<PvpBoardSnapshot> { synRun.DayBoards[0] } : new List<PvpBoardSnapshot>()),
                ReferencedRunsById = new Dictionary<string, PvpRunRecord> { [synRun.RunId] = synRun }
            };
            new DatasetExporter(validator).Export(sampling, new DatasetGenOptions { OutputDir = tempDir });

            // 2. 构造外部真实社区 JSONL（含 1 条合法 Snapshot 刻意不写 Source/ZhName，1 条合法 Run，1 条槽位重叠的非法盘面）
            string jsonlPath = Path.Combine(tempDir, "community_dump.jsonl");
            var validSnap = new PvpBoardSnapshot
            {
                BoardId = "comm_b_001",
                RunId = "comm_r_001",
                Source = "third_party_scraper", // 导入器必须强制覆写为 real_community
                Hero = HeroNames.Dooley,
                Day = 5,
                Level = 6,
                HealthMax = 1100,
                UnlockedSlotsMask = 1023,
                EstimatedWinRate = 0.72,
                PowerBucket = PowerBucket.Strong,
                Items =
                [
                    new PvpItemSnapshot
                    {
                        Slot = 2, Width = 1, TemplateId = item.Id, InternalName = item.InternalName,
                        ZhName = "", Tier = CardTier.Gold
                    }
                ],
                Skills =
                [
                    new PvpSkillSnapshot
                    {
                        TemplateId = skill.Id, InternalName = skill.InternalName,
                        ZhName = "", Tier = CardTier.Silver
                    }
                ]
            };

            var validRun = new PvpRunRecord
            {
                RunId = "comm_r_002",
                Source = "raw_upload",
                Hero = HeroNames.Pygmalien,
                Persona = PersonaType.GreedyEco,
                Archetype = "Pyg_Eco",
                FinalDay = 10,
                FinalWins = 10,
                FinalLosses = 0,
                IsVictory = true,
                DayBoards =
                [
                    new PvpBoardSnapshot
                    {
                        BoardId = "comm_b_002",
                        RunId = "comm_r_002",
                        Hero = HeroNames.Pygmalien,
                        Day = 10,
                        Level = 11,
                        HealthMax = 2800,
                        UnlockedSlotsMask = 1023,
                        EstimatedWinRate = 0.91,
                        PowerBucket = PowerBucket.MetaBoss,
                        Items =
                        [
                            new PvpItemSnapshot
                            {
                                Slot = 0, Width = 1, TemplateId = item.Id, InternalName = item.InternalName,
                                ZhName = "手填名称", Tier = CardTier.Diamond
                            }
                        ]
                    }
                ]
            };

            var invalidOverlapSnap = new PvpBoardSnapshot
            {
                BoardId = "comm_b_bad",
                RunId = "comm_r_bad",
                Hero = HeroNames.Mak,
                Day = 2,
                Level = 2,
                HealthMax = 400,
                UnlockedSlotsMask = 1023,
                Items =
                [
                    new PvpItemSnapshot { Slot = 0, Width = 1, TemplateId = item.Id, InternalName = item.InternalName, ZhName = "A", Tier = CardTier.Bronze },
                    new PvpItemSnapshot { Slot = 0, Width = 1, TemplateId = item.Id, InternalName = item.InternalName, ZhName = "B", Tier = CardTier.Bronze }
                ]
            };

            File.WriteAllLines(jsonlPath,
            [
                JsonSerializer.Serialize(validSnap, PvpDatasetJson.PvpJsonOptions),
                JsonSerializer.Serialize(validRun, PvpDatasetJson.PvpJsonOptions),
                JsonSerializer.Serialize(invalidOverlapSnap, PvpDatasetJson.PvpJsonOptions)
            ]);

            var importer = new RealDataImporter(repo, validator);

            // 第一次导入
            var res1 = importer.Import(jsonlPath, tempDir, requireOfficialZh: false);
            Assert.Equal(3, res1.TotalBoardsRead);
            Assert.Equal(2, res1.ImportedBoards);
            Assert.Equal(2, res1.ImportedRuns);
            Assert.Equal(0, res1.SkippedDuplicateBoards);
            Assert.Equal(1, res1.FailedValidationCount);

            // 第二次幂等导入相同文件：不得新增或重复插入子表记录
            var res2 = importer.Import(jsonlPath, tempDir, requireOfficialZh: false);
            Assert.Equal(0, res2.ImportedBoards);
            Assert.Equal(0, res2.ImportedRuns);
            Assert.Equal(2, res2.SkippedDuplicateBoards);
            Assert.Equal(1, res2.FailedValidationCount);

            // 验证 SQLite 中双源共存与幂等唯一性
            string dbPath = Path.Combine(tempDir, "bazaar_pvp_dataset.db");
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            conn.Open();

            Assert.Equal(1L, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM day_boards WHERE source = 'synthetic_selfplay'"));
            Assert.Equal(2L, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM day_boards WHERE source = 'real_community'"));
            Assert.Equal(2L, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM runs WHERE source = 'real_community'"));
            Assert.Equal(1L, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM board_items WHERE board_id = 'comm_b_001'"));
            Assert.Equal(1L, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM board_skills WHERE board_id = 'comm_b_001'"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// 全量生产数据集自动化验收测试（仅在设置环境变量 BAZAAR_VERIFY_OUTPUT=1 时激活），
    /// 严格检验 docs/PVP_DATASET_PLAN.md 第五节全部 4 大验收标准。
    /// </summary>
    [Fact]
    public void VerifyFullProductionDatasetOutput()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BAZAAR_VERIFY_OUTPUT"), "1", StringComparison.Ordinal))
            return;

        string outputDir = Environment.GetEnvironmentVariable("BAZAAR_OUTPUT_DIR")
            ?? Path.Combine(TestInfrastructure.TestDataLocator.ResolveRepoRoot(), "output", "pvp_dataset");
        Assert.True(Directory.Exists(outputDir), $"生产输出目录不存在: {outputDir}");

        var repo = Repo.Value;
        var validatorType = Type.GetType("BazaarCore.Dataset.Simulation.BoardValidator, BazaarCore");
        Assert.NotNull(validatorType);
        var validator = (IBoardValidator)Activator.CreateInstance(validatorType!)!;

        // 1. 规模达标：runs >= 10,000（v2 扩容 12,000）、总快照 >= 90,000、均衡池 >= 5,000、自然漏斗池 >= 10,000
        string runsPath = Path.Combine(outputDir, "runs_5000.jsonl");
        string ghostPath = Path.Combine(outputDir, "ghost_pool_balanced.jsonl");
        string naturalPath = Path.Combine(outputDir, "ghost_pool_natural.jsonl");
        string dbPath = Path.Combine(outputDir, "bazaar_pvp_dataset.db");
        string reportPath = Path.Combine(outputDir, "dataset_report.md");

        Assert.True(File.Exists(runsPath));
        Assert.True(File.Exists(ghostPath));
        Assert.True(File.Exists(naturalPath));
        Assert.True(File.Exists(dbPath));
        Assert.True(File.Exists(reportPath));

        var runs = File.ReadLines(runsPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<PvpRunRecord>(l, PvpDatasetJson.PvpJsonOptions)!)
            .ToList();
        Assert.True(runs.Count >= 10_000, $"runs_5000.jsonl 应 >= 10000 局，实际: {runs.Count}");
        Assert.True(runs.Count(r => r.FinalDay <= 9) > 0, "runs_5000.jsonl 必须保留 FinalDay <= 9 的早死菜鸟局");

        var runBoards = runs.SelectMany(r => r.DayBoards).ToList();
        Assert.True(runBoards.Count >= 90_000, $"总单日快照应 >= 90000 条，实际: {runBoards.Count}");

        var ghostPool = File.ReadLines(ghostPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<PvpBoardSnapshot>(l, PvpDatasetJson.PvpJsonOptions)!)
            .ToList();
        Assert.True(ghostPool.Count >= 5000, $"ghost_pool_balanced.jsonl 应 >= 5000 条，实际: {ghostPool.Count}");

        var naturalPool = File.ReadLines(naturalPath)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonSerializer.Deserialize<PvpBoardSnapshot>(l, PvpDatasetJson.PvpJsonOptions)!)
            .ToList();
        Assert.True(naturalPool.Count >= 10_000, $"ghost_pool_natural.jsonl 应 >= 10000 条，实际: {naturalPool.Count}");

        // 验证自然漏斗池早期低分/失误画像占比显著高于后期
        var earlyNat = naturalPool.Where(b => b.Day <= 5).ToList();
        var lateNat = naturalPool.Where(b => b.Day >= 13).ToList();
        Assert.NotEmpty(earlyNat);
        Assert.NotEmpty(lateNat);
        double earlyLowPct = earlyNat.Count(b => TournamentRunner.IsLowTierPersona(b.Persona)) / (double)earlyNat.Count;
        double lateLowPct = lateNat.Count(b => TournamentRunner.IsLowTierPersona(b.Persona)) / (double)lateNat.Count;
        Assert.True(earlyLowPct > lateLowPct, $"自然漏斗池早期低分画像占比({earlyLowPct:P1})应高于后期({lateLowPct:P1})");

        // 分 Day JSON 15 个文件完整核查
        foreach (int b in DayBucketHelper.AllDayBuckets)
            Assert.True(File.Exists(Path.Combine(outputDir, "by_day", DayBucketHelper.GetDayFileName(b))));

        // SQLite 4 表 4 索引核查
        VerifySqliteSchemaAndCounts(
            dbPath,
            minRuns: 10_000,
            minBoards: 90_000,
            expectedBalancedCount: ghostPool.Count,
            expectedNaturalCount: naturalPool.Count);

        // 2. 覆盖均匀：Day 1~15+ 每天均衡池 >= 300，8 英雄全覆盖且 15×8×4 = 480 子单元无一空缺
        foreach (int b in DayBucketHelper.AllDayBuckets)
        {
            var dayList = ghostPool.Where(s => DayBucketHelper.GetDayBucket(s.Day) == b).ToList();
            Assert.True(dayList.Count >= 300, $"{DayBucketHelper.GetDayLabel(b)} 均衡池应 >= 300，实际: {dayList.Count}");
            foreach (var hero in HeroNames.PlayableHeroes)
            {
                foreach (var pb in DayBucketHelper.AllPowerBuckets)
                {
                    Assert.True(
                        dayList.Any(s => string.Equals(s.Hero, hero, StringComparison.OrdinalIgnoreCase) && s.PowerBucket == pb),
                        $"均衡池网格 ({DayBucketHelper.GetDayLabel(b)}, {hero}, {pb}) 存在空缺");
                }
            }
        }

        // 3. 100% 合法可回放：全量 BoardValidator 0 错 0 缺中文，随机 1,000 场 CombatSimulator 0 异常 0 卡死
        var allToCheck = runBoards
            .Concat(ghostPool)
            .Concat(naturalPool)
            .GroupBy(b => b.BoardId)
            .Select(g => g.First())
            .ToList();
        foreach (var snap in allToCheck)
        {
            var vr = validator.Validate(snap, repo, requireOfficialZh: true);
            Assert.True(vr.IsValid, $"盘面 {snap.BoardId} 校验失败: {string.Join("; ", vr.Errors)}");
            foreach (var it in snap.Items)
                Assert.False(string.IsNullOrWhiteSpace(it.ZhName), $"盘面 {snap.BoardId} 物品 {it.InternalName} 缺少中文名");
            foreach (var sk in snap.Skills)
                Assert.False(string.IsNullOrWhiteSpace(sk.ZhName), $"盘面 {snap.BoardId} 技能 {sk.InternalName} 缺少中文名");
        }

        var rng = new Random(2026);
        var byBucket = ghostPool.GroupBy(b => DayBucketHelper.GetDayBucket(b.Day)).ToDictionary(g => g.Key, g => g.ToList());
        for (int i = 0; i < 1000; i++)
        {
            int b = DayBucketHelper.AllDayBuckets[i % DayBucketHelper.AllDayBuckets.Length];
            var list = byBucket[b];
            var a = list[rng.Next(list.Count)];
            var opp = list[rng.Next(list.Count)];
            var p1 = validator.ToPlayerState(a, repo);
            var p2 = DayBucketHelper.EnsureDistinctOpponentState(p1, validator.ToPlayerState(opp, repo));
            var res = DayBucketHelper.SimulateHeadless(repo, p1, p2, 10000 + i, "PVP");
            Assert.True(res.DurationMs <= CombatSimulator.MaxDurationMs);
            Assert.False(res.IsDraw, $"场次 {i} 不应超时卡死为平局");
        }

        // 4. 曲线真实：Day 1 -> Day 15+ 成长趋势与四档胜率显著拉开
        var d1 = runBoards.Where(b => b.Day == 1).ToList();
        var d15 = runBoards.Where(b => b.Day >= 15).ToList();
        Assert.NotEmpty(d1);
        Assert.NotEmpty(d15);

        Assert.True(d15.Average(b => b.HealthMax) > d1.Average(b => b.HealthMax) * 2.5);
        double HighTierRatio(List<PvpBoardSnapshot> list)
        {
            var items = list.SelectMany(b => b.Items).ToList();
            return items.Count == 0 ? 0 : items.Count(it => it.Tier >= CardTier.Silver) / (double)items.Count;
        }
        double EnchantRatio(List<PvpBoardSnapshot> list)
        {
            var items = list.SelectMany(b => b.Items).ToList();
            return items.Count == 0 ? 0 : items.Count(it => !string.IsNullOrEmpty(it.Enchantment)) / (double)items.Count;
        }
        Assert.True(HighTierRatio(d15) > HighTierRatio(d1));
        Assert.True(EnchantRatio(d15) > EnchantRatio(d1));
        Assert.True(d15.Where(b => b.MatchOutcome != null).Average(b => b.MatchOutcome!.DamageDealt)
                  > d1.Where(b => b.MatchOutcome != null).Average(b => b.MatchOutcome!.DamageDealt));

        // 验证 v2 附魔稀缺度校准：Day 10~15 平均单盘附魔件数 <= 4.0
        var d10To15 = runBoards.Where(b => b.Day is >= 10 and <= 15).ToList();
        Assert.NotEmpty(d10To15);
        double avgEnchantsD10To15 = d10To15.Average(b => b.Items.Count(it => !string.IsNullOrEmpty(it.Enchantment)));
        Assert.True(avgEnchantsD10To15 <= 4.0, $"Day 10~15 平均单盘附魔件数应 <= 4.0，实际: {avgEnchantsD10To15:F2}");

        // 验证局内/局外永久成长闭环：Day 8~15+ 存在相当比例 (>= 30%) 含非空 attribute_mods（PermanentAttributes）的成长盘面
        var d8To15Plus = runBoards.Where(b => b.Day >= 8).ToList();
        Assert.NotEmpty(d8To15Plus);
        double permGrowthBoardRatio = d8To15Plus.Count(b => b.Items.Any(it => it.PermanentAttributes.Count > 0)) / (double)d8To15Plus.Count;
        Assert.True(permGrowthBoardRatio >= 0.30, $"Day 8~15+ 含非空 attribute_mods 盘面占比应 >= 30%，实际: {permGrowthBoardRatio:P1}");

        string reportMd = File.ReadAllText(reportPath);
        Assert.Contains("局内/局外成长与数值收敛统计", reportMd);
        Assert.Contains("5.0.0 社区 10 胜参考蓝本清单与高天对标", reportMd);

        double avgWeak = allToCheck.Where(b => b.PowerBucket == PowerBucket.Weak).Average(b => b.EstimatedWinRate);
        double avgNormal = allToCheck.Where(b => b.PowerBucket == PowerBucket.Normal).Average(b => b.EstimatedWinRate);
        double avgStrong = allToCheck.Where(b => b.PowerBucket == PowerBucket.Strong).Average(b => b.EstimatedWinRate);
        double avgMeta = allToCheck.Where(b => b.PowerBucket == PowerBucket.MetaBoss).Average(b => b.EstimatedWinRate);
        Assert.True(avgWeak < avgNormal && avgNormal < avgStrong && avgStrong < avgMeta);
        Assert.True(avgMeta - avgWeak >= 0.65);
    }

    private static void VerifySqliteSchemaAndCounts(
        string dbPath,
        int minRuns,
        int minBoards,
        int expectedBalancedCount,
        int? expectedNaturalCount = null)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        conn.Open();

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        Assert.Contains("runs", tables);
        Assert.Contains("day_boards", tables);
        Assert.Contains("board_items", tables);
        Assert.Contains("board_skills", tables);

        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index';";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(0));
        }
        Assert.Contains("idx_boards_day_hero_power", indexes);
        Assert.Contains("idx_boards_run", indexes);
        Assert.Contains("idx_items_board", indexes);
        Assert.Contains("idx_skills_board", indexes);

        Assert.True(ExecuteScalarLong(conn, "SELECT COUNT(*) FROM runs") >= minRuns);
        Assert.True(ExecuteScalarLong(conn, "SELECT COUNT(*) FROM day_boards") >= minBoards);
        Assert.True(ExecuteScalarLong(conn, "SELECT COUNT(*) FROM board_items") > 0);
        Assert.True(ExecuteScalarLong(conn, "SELECT COUNT(*) FROM board_skills") > 0, "多天模拟数据集应包含非空 board_skills 技能记录");
        Assert.Equal(expectedBalancedCount, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM day_boards WHERE is_balanced_pool = 1"));
        if (expectedNaturalCount.HasValue)
            Assert.Equal(expectedNaturalCount.Value, ExecuteScalarLong(conn, "SELECT COUNT(*) FROM day_boards WHERE is_natural_pool = 1"));
    }

    private static long ExecuteScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    #region 阶段 1 测试用轻量契约 Stub

    private sealed class StubArchetypeCatalog : IArchetypeCatalog
    {
        private readonly List<ArchetypeDefinition> _all;

        public StubArchetypeCatalog()
        {
            _all = HeroNames.PlayableHeroes
                .Select(h => new ArchetypeDefinition
                {
                    Id = $"{h}_Stub_Core",
                    Hero = h,
                    NameZh = $"{h}测试流派",
                    DescriptionZh = "单元测试 Stub 流派"
                })
                .ToList();
        }

        public IReadOnlyList<ArchetypeDefinition> All => _all;
        public IReadOnlyList<ArchetypeDefinition> GetByHero(string hero)
            => _all.Where(a => string.Equals(a.Hero, hero, StringComparison.OrdinalIgnoreCase)).ToList();
        public ArchetypeDefinition? GetById(string id)
            => _all.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        public ArchetypeDefinition PickForHero(string hero, PersonaType persona, Random rng)
            => GetByHero(hero)[0];
    }

    private sealed class StubBoardValidator : IBoardValidator
    {
        public BoardValidationResult Validate(PvpBoardSnapshot snapshot, GameDataRepository repo, bool requireOfficialZh = true)
        {
            var errors = new List<string>();
            if (!HeroNames.PlayableHeroes.Contains(snapshot.Hero, StringComparer.OrdinalIgnoreCase))
                errors.Add($"非法英雄: {snapshot.Hero}");
            if (snapshot.Day < 1 || snapshot.Level < 1 || snapshot.HealthMax <= 0)
                errors.Add("基础数值非法");
            if (snapshot.UnlockedSlotsMask is <= 0 or > 1023)
                errors.Add("UnlockedSlotsMask 非法");

            var occupied = new bool[10];
            foreach (var item in snapshot.Items)
            {
                var tpl = repo.GetCard(item.TemplateId);
                if (tpl == null || tpl.Type != "Item")
                {
                    errors.Add($"无效物品模板: {item.TemplateId}");
                    continue;
                }
                if (item.Width != tpl.CardSize.SlotCount() || item.Slot < 0 || item.Slot + item.Width > 10)
                {
                    errors.Add($"越界或宽度不匹配: {item.InternalName}");
                    continue;
                }
                for (int s = item.Slot; s < item.Slot + item.Width; s++)
                {
                    if ((snapshot.UnlockedSlotsMask & (1 << s)) == 0)
                        errors.Add($"槽位 {s} 未解锁");
                    if (occupied[s])
                        errors.Add($"槽位 {s} 重叠");
                    occupied[s] = true;
                }
                if (string.IsNullOrWhiteSpace(item.ZhName))
                    errors.Add($"缺少中文名: {item.InternalName}");
                if (requireOfficialZh && repo.Localization != null && !repo.HasOfficialZhTitle(tpl))
                    errors.Add($"缺少官方中文词条: {item.InternalName}");
            }

            foreach (var sk in snapshot.Skills)
            {
                var tpl = repo.GetCard(sk.TemplateId);
                if (tpl == null || tpl.Type != "Skill")
                    errors.Add($"无效技能模板: {sk.TemplateId}");
                if (string.IsNullOrWhiteSpace(sk.ZhName))
                    errors.Add($"技能缺少中文名: {sk.InternalName}");
            }

            return new BoardValidationResult { IsValid = errors.Count == 0, Errors = errors };
        }

        public PlayerState ToPlayerState(PvpBoardSnapshot snapshot, GameDataRepository repo)
        {
            var state = new PlayerState(snapshot.BoardId, snapshot.Hero, snapshot.Hero, snapshot.HealthMax, snapshot.Prestige, 0, 10)
            {
                Day = snapshot.Day,
                Level = snapshot.Level,
                Wins = snapshot.Wins,
                Losses = snapshot.Losses
            };
            state.Hand.UnlockedMask = snapshot.UnlockedSlotsMask;
            foreach (var item in snapshot.Items)
            {
                var tpl = repo.GetCard(item.TemplateId)!;
                var card = new CardInstance(tpl, item.Tier, item.Enchantment, $"{snapshot.BoardId}_i_{item.Slot}");
                foreach (var (k, v) in item.PermanentAttributes)
                    card.PermanentAdd[k] = v - card.BaseAttributes.GetValueOrDefault(k, 0);
                state.Hand.TryPlaceOrShift(card, item.Slot);
            }
            foreach (var sk in snapshot.Skills)
            {
                var tpl = repo.GetCard(sk.TemplateId)!;
                state.Skills.Add(new CardInstance(tpl, sk.Tier, null, $"{snapshot.BoardId}_s_{state.Skills.Count}"));
            }
            state.PrepareForCombat();
            return state;
        }

        public string ComputeSignature(PvpBoardSnapshot snapshot)
        {
            var items = snapshot.Items
                .OrderBy(i => i.Slot)
                .Select(i => $"{i.Slot}:{i.TemplateId}:{i.Tier}:{i.Enchantment}:{i.PermanentAttributes.GetValueOrDefault("DamageAmount", 0):F0}");
            var skills = snapshot.Skills
                .OrderBy(s => s.TemplateId)
                .Select(s => $"{s.TemplateId}:{s.Tier}");
            return $"{snapshot.Hero}|{string.Join(",", items)}|{string.Join(",", skills)}";
        }
    }

    private sealed class StubFastRunSimulator : IFastRunSimulator
    {
        private readonly IBoardValidator _validator;
        private readonly Dictionary<string, List<CardTemplate>> _combatItemsByHero = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<CardTemplate> _skills;

        public StubFastRunSimulator(IBoardValidator validator)
        {
            _validator = validator;
            var repo = Repo.Value;
            static bool HasAttr(CardTemplate c, string attr) =>
                (c.Attributes != null && c.Attributes.ContainsKey(attr))
                || (c.Tiers != null && c.Tiers.Values.Any(t => t.Attributes.ContainsKey(attr)));

            foreach (var hero in HeroNames.PlayableHeroes)
            {
                var pool = repo.GetSpawnableItemsForHero(hero, includeCommon: true)
                    .Where(c => c.CardSize == CardSize.Small
                             && HasAttr(c, "CooldownMax")
                             && HasAttr(c, "DamageAmount"))
                    .Take(16)
                    .ToList();
                if (pool.Count < 4)
                    pool = repo.AlwaysSpawnableItems.Where(c => c.CardSize == CardSize.Small && HasAttr(c, "DamageAmount")).Take(16).ToList();
                if (pool.Count == 0)
                    pool = repo.AlwaysSpawnableItems.Where(c => c.CardSize == CardSize.Small).Take(16).ToList();
                _combatItemsByHero[hero] = pool;
            }
            _skills = repo.AlwaysSpawnableSkills.Take(16).ToList();
        }

        public RunParticipantState CreateParticipant(
            string runId, string hero, PersonaType persona, ArchetypeDefinition archetype, int seed, GameDataRepository repo)
        {
            var ps = new PlayerState(runId, hero, hero, initialHealthMax: 250, initialPrestige: 20, initialGold: 10, unlockedHandSlots: 4)
            {
                Day = 1,
                Level = 1
            };
            return new RunParticipantState
            {
                RunId = runId,
                Hero = hero,
                Persona = persona,
                Archetype = archetype,
                PlayerState = ps,
                Rng = new Random(seed)
            };
        }

        public PvpBoardSnapshot AdvanceOneDayToPvp(
            RunParticipantState participant, int day, GameDataRepository repo, IArchetypeCatalog catalog, FastRunOptions? options = null)
        {
            var ps = participant.PlayerState;
            ps.Day = day;
            ps.Level = Math.Min(16, 1 + day);
            ps.Attributes["HealthMax"] = 250 + (day - 1) * 180;
            ps.Hand.UnlockedMask = 0;
            ps.Hand.UnlockCountCentered(day == 1 ? 4 : day == 2 ? 6 : day <= 4 ? 8 : 10);

            var pool = _combatItemsByHero[participant.Hero];
            // 随天数逐步扩充手牌数量，保持各局之间存在显著战力差异以自然拉出四档胜率
            int targetCards = Math.Min(ps.Hand.UnlockedCount, 1 + day / 2 + (participant.Persona == PersonaType.Casual ? 0 : 1));
            while (ps.Hand.Cards.Count < targetCards)
            {
                var tpl = pool[participant.Rng.Next(pool.Count)];
                var card = new CardInstance(tpl);
                if (!ps.Hand.TryAddAuto(card))
                    break;
            }

            // 随天数与画像提升品质、附魔与永久成长属性
            foreach (var c in ps.Hand.Cards)
            {
                if (day >= 4 && participant.Rng.NextDouble() < 0.35 && c.CanUpgrade())
                    c.TryUpgrade();
                if (day >= 6 && string.IsNullOrEmpty(c.Enchantment) && c.Template.Enchantments is { Count: > 0 } && participant.Rng.NextDouble() < 0.25)
                {
                    var enchKey = c.Template.Enchantments.Keys.First();
                    c.ApplyEnchantment(enchKey);
                }
                if (c.BaseAttributes.ContainsKey("DamageAmount"))
                    c.PermanentAdd["DamageAmount"] = c.PermanentAdd.GetValueOrDefault("DamageAmount") + 5 + participant.Rng.Next(0, 12);
            }

            if (day % 3 == 1 && ps.Skills.Count < 3 && _skills.Count > 0)
            {
                var sk = _skills[participant.Rng.Next(_skills.Count)];
                ps.Skills.Add(new CardInstance(sk));
            }

            var snap = new PvpBoardSnapshot
            {
                BoardId = $"syn_d{day:00}_{participant.Hero.ToLowerInvariant()}_{participant.RunId}",
                RunId = participant.RunId,
                Persona = participant.Persona,
                Archetype = participant.Archetype.Id,
                Day = day,
                Hero = participant.Hero,
                Level = ps.Level,
                HealthMax = ps.HealthMax,
                Prestige = ps.Prestige,
                Wins = ps.Wins,
                Losses = ps.Losses,
                UnlockedSlotsMask = ps.Hand.UnlockedMask,
                Items = ps.Hand.Cards.OrderBy(c => c.StartSlot).Select(c =>
                {
                    var perm = new Dictionary<string, double>();
                    foreach (var (k, addVal) in c.PermanentAdd)
                    {
                        if (Math.Abs(addVal) > 1e-6)
                            perm[k] = c.GetAttributeUnscaled(k);
                    }
                    return new PvpItemSnapshot
                    {
                        Slot = c.StartSlot,
                        Width = c.SlotWidth,
                        TemplateId = c.Template.Id,
                        InternalName = c.Template.InternalName,
                        ZhName = repo.HasOfficialZhTitle(c.Template) ? repo.GetLocalizedTitle(c.Template) : $"测试_{c.Template.InternalName}",
                        Tier = c.Tier,
                        Enchantment = c.Enchantment,
                        PermanentAttributes = perm
                    };
                }).ToList(),
                Skills = ps.Skills.Select(s => new PvpSkillSnapshot
                {
                    TemplateId = s.Template.Id,
                    InternalName = s.Template.InternalName,
                    ZhName = repo.HasOfficialZhTitle(s.Template) ? repo.GetLocalizedTitle(s.Template) : $"测试技能_{s.Template.InternalName}",
                    Tier = s.Tier
                }).ToList()
            };
            snap.BoardSignature = _validator.ComputeSignature(snap);
            participant.Snapshots.Add(snap);
            return snap;
        }

        public void ApplyPvpOutcome(
            RunParticipantState participant, PvpBoardSnapshot todaySnapshot, PvpMatchOutcome outcome, GameDataRepository repo, FastRunOptions? options = null)
        {
            todaySnapshot.MatchOutcome = outcome;
            var ps = participant.PlayerState;
            if (outcome.Won)
            {
                ps.Wins++;
                if (ps.Wins >= (options?.VictoriesToWin ?? 10) && !(options?.AllowEndlessAfterVictory ?? false))
                {
                    participant.IsVictory = true;
                    participant.IsFinished = true;
                }
            }
            else
            {
                ps.Losses++;
                ps.Prestige = Math.Max(0, ps.Prestige - todaySnapshot.Day);
                if (ps.Prestige <= 0)
                {
                    if (!ps.FatesUsed)
                    {
                        ps.FatesUsed = true;
                        ps.Prestige = 1;
                    }
                    else
                    {
                        participant.IsFinished = true;
                    }
                }
            }

            if (todaySnapshot.Day >= (options?.MaxDay ?? 17))
                participant.IsFinished = true;
        }

        public PvpRunRecord BuildRunRecord(RunParticipantState participant, GameDataRepository repo)
        {
            var last = participant.Snapshots.LastOrDefault();
            return new PvpRunRecord
            {
                RunId = participant.RunId,
                Hero = participant.Hero,
                Persona = participant.Persona,
                Archetype = participant.Archetype.Id,
                FinalDay = last?.Day ?? participant.PlayerState.Day,
                FinalWins = participant.PlayerState.Wins,
                FinalLosses = participant.PlayerState.Losses,
                IsVictory = participant.IsVictory || participant.PlayerState.Wins >= 10,
                DayBoards = participant.Snapshots.ToList()
            };
        }
    }

    #endregion
}

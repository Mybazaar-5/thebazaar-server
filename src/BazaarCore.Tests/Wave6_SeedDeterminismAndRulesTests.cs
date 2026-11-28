using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Planner;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 6 测试组 A（种子确定性、随机流收口、旧客户端与规则兼容、锦标赛边界及插件序列化契约）：
/// 覆盖《SEED_MODE_AND_GHOST_PLANNER_PLAN.md》测试清单第 2、3、4、5、6、7、8、9、15、16 项。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave6_SeedDeterminismAndRulesTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _launcherFx;

    private static readonly JsonSerializerOptions GoldenJsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public Wave6_SeedDeterminismAndRulesTests(Wave2CLauncherAssemblyFixture launcherFx)
    {
        _launcherFx = launcherFx ?? throw new ArgumentNullException(nameof(launcherFx));
    }

    private static string NewTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 11, 48)]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // 忽略临时目录清理竞争异常，避免掩盖主断言结果
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #2：金样（3 个固定种子 × 2 个英雄，Day 1~3 每小时事件模板 ID 列表）
    // ════════════════════════════════════════════════════════════════════════

    private sealed class GoldenCalendarDocument
    {
        public int SeedAlgoVersion { get; set; }
        public List<GoldenCalendarCase> Cases { get; set; } = new();
    }

    private sealed class GoldenCalendarCase
    {
        public string SeedText { get; set; } = "";
        public int MasterSeed { get; set; }
        public string Hero { get; set; } = "";
        public List<GoldenCalendarDay> Days { get; set; } = new();
    }

    private sealed class GoldenCalendarDay
    {
        public int Day { get; set; }
        public List<GoldenCalendarHour> Hours { get; set; } = new();
    }

    private sealed class GoldenCalendarHour
    {
        public int Hour { get; set; }
        public List<string> TemplateIds { get; set; } = new();
    }

    [Fact]
    public void Test02_SeedCalendarGolden_ThreeSeedsTwoHeroesDay1To3MatchesGoldenFile()
    {
        var fx = SharedGameDataFixture.Shared;
        string[] seeds = ["S00031N1", "S00KAA88", "S32J35TF"]; // 主种子 100001 / 20261128 / -987654321
        string[] heroes = ["Vanessa", "Pygmalien"];

        using var finder = new EventSeedFinderService(
            modHost: null,
            basePool: fx.Pool,
            baseMonsters: fx.CreateMonstersCopy(),
            baseArchetypes: fx.ArchetypeIndex,
            baseRepo: fx.Repo);

        var actual = new GoldenCalendarDocument
        {
            SeedAlgoVersion = SeedAlgo.Current,
        };

        foreach (string seed in seeds)
        {
            foreach (string hero in heroes)
            {
                var res = finder.PreviewSeed(new SeedPreviewRequest
                {
                    SeedText = seed,
                    Hero = hero,
                    MaxDay = 3,
                    UseMyRules = false,
                });

                Assert.True(res.Ok, $"PreviewSeed 失败（seed={seed}, hero={hero}）：{res.ErrorCode} {res.ErrorMessageZh}");
                Assert.NotNull(res.Data);
                Assert.Equal(3, res.Data!.Calendar.Count);

                var caseEntry = new GoldenCalendarCase
                {
                    SeedText = res.Data.SeedText,
                    MasterSeed = res.Data.MasterSeed,
                    Hero = res.Data.Hero,
                };

                foreach (var dayDto in res.Data.Calendar)
                {
                    Assert.Equal(5, dayDto.Hours.Count);
                    var goldenDay = new GoldenCalendarDay { Day = dayDto.Day };
                    foreach (var hourDto in dayDto.Hours)
                    {
                        Assert.NotEmpty(hourDto.Options);
                        goldenDay.Hours.Add(new GoldenCalendarHour
                        {
                            Hour = hourDto.Hour,
                            TemplateIds = hourDto.Options.Select(o => o.TemplateId).ToList(),
                        });
                    }
                    caseEntry.Days.Add(goldenDay);
                }

                actual.Cases.Add(caseEntry);
            }
        }

        string goldenPath = Path.Combine(fx.RepoRoot, "src", "BazaarCore.Tests", "Golden", "seed_calendar_v1.json");
        bool updateGolden = string.Equals(
            Environment.GetEnvironmentVariable("BAZAAR_UPDATE_GOLDEN"),
            "1",
            StringComparison.Ordinal);

        if (updateGolden)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
            string json = System.Text.Json.JsonSerializer.Serialize(actual, GoldenJsonOpts);
            File.WriteAllText(goldenPath, json + Environment.NewLine, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Assert.True(File.Exists(goldenPath));
            return;
        }

        Assert.True(
            File.Exists(goldenPath),
            $"金样文件不存在：{goldenPath}。首次生成请设置环境变量 BAZAAR_UPDATE_GOLDEN=1 后运行本测试。");

        string goldenJson = File.ReadAllText(goldenPath, Encoding.UTF8);
        var expected = System.Text.Json.JsonSerializer.Deserialize<GoldenCalendarDocument>(goldenJson, GoldenJsonOpts);
        Assert.NotNull(expected);
        Assert.Equal(SeedAlgo.Current, expected!.SeedAlgoVersion);
        Assert.Equal(expected.Cases.Count, actual.Cases.Count);

        for (int c = 0; c < expected.Cases.Count; c++)
        {
            var expCase = expected.Cases[c];
            var actCase = actual.Cases[c];
            Assert.Equal(expCase.SeedText, actCase.SeedText);
            Assert.Equal(expCase.MasterSeed, actCase.MasterSeed);
            Assert.Equal(expCase.Hero, actCase.Hero);
            Assert.Equal(expCase.Days.Count, actCase.Days.Count);

            for (int d = 0; d < expCase.Days.Count; d++)
            {
                var expDay = expCase.Days[d];
                var actDay = actCase.Days[d];
                Assert.Equal(expDay.Day, actDay.Day);
                Assert.Equal(expDay.Hours.Count, actDay.Hours.Count);

                for (int h = 0; h < expDay.Hours.Count; h++)
                {
                    var expHour = expDay.Hours[h];
                    var actHour = actDay.Hours[h];
                    Assert.Equal(expHour.Hour, actHour.Hour);
                    Assert.Equal(expHour.TemplateIds, actHour.TemplateIds);
                }
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #3：中断等价（同种子、同脚本跑两遍，第二遍频繁序列化重建 RunEngine）
    // ════════════════════════════════════════════════════════════════════════

    private sealed record ScriptStepSnapshot(
        int Step,
        string ActionLabel,
        uint Day,
        uint Hour,
        ERunState State,
        string CurrentEncounterId,
        string SelectionTemplatesJoined);

    private sealed record OwnedCardProjection(
        string Section,
        string Socket,
        string TemplateId,
        ETier Tier,
        string Enchantment);

    private sealed record FinalRunStats(
        uint Day,
        uint Hour,
        ERunState State,
        int Gold,
        int Health,
        int HealthMax,
        int Level,
        int Experience,
        int Prestige);

    private sealed record ScriptRunTrace(
        List<ScriptStepSnapshot> History,
        List<OwnedCardProjection> OwnedCards,
        FinalRunStats Stats,
        int RebuildCount);

    [Fact]
    public void Test03_InterruptionEquivalence_SameSeedAndScriptProducesIdenticalStateAcrossSnapshots()
    {
        var fx = SharedGameDataFixture.Shared;
        const string seedText = "S3WAVE60";

        ScriptRunTrace ExecuteScript(bool interruptEveryTwoCommands)
        {
            var rules = new CustomRunRuleSet
            {
                CustomSeedText = seedText,
            }.CloneNormalized();

            var run = new RunState
            {
                RunId = interruptEveryTwoCommands ? "w6_inter_b" : "w6_inter_a",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                CustomRules = rules,
            };

            RunEngine BuildEngine(RunState targetRun) => new(
                targetRun,
                fx.Pool,
                fx.CreateMonstersCopy(),
                fx.LevelUps,
                fx.Mode,
                fx.Repo,
                fx.Ghosts,
                fx.ArchetypeIndex);

            var engine = BuildEngine(run);
            engine.StartRun();

            var history = new List<ScriptStepSnapshot>();
            int stepCounter = 0;
            int cmdsSinceRebuild = 0;
            int rebuildCount = 0;

            void Record(string label)
            {
                string selectionTemplates = string.Join(
                    ",",
                    run.SelectionSet.Select(id =>
                        run.Cards.TryGetValue(id, out var ce)
                            ? RunState.ResolveCoreTemplateId(ce)
                            : id));
                history.Add(new ScriptStepSnapshot(
                    Step: stepCounter++,
                    ActionLabel: label,
                    Day: run.Day,
                    Hour: run.Hour,
                    State: run.State,
                    CurrentEncounterId: run.CurrentEncounterId ?? string.Empty,
                    SelectionTemplatesJoined: selectionTemplates));
            }

            void RebuildFromSnapshot()
            {
                string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: engine.StartSelectionDone);
                run = RunSnapshotSerializer.Deserialize(snapJson, fx.Repo);
                engine = BuildEngine(run);
                rebuildCount++;
                cmdsSinceRebuild = 0;
            }

            Record("StartRun");
            if (interruptEveryTwoCommands)
            {
                // 开局后立即验证一次无损持久化重建
                RebuildFromSnapshot();
            }

            int actionsInCurrentEncounter = 0;
            bool rerolledInCurrentEncounter = false;
            string? trackedEncounterKey = null;

            for (int iter = 0; iter < 60; iter++)
            {
                if (run.State is ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
                    break;

                // 仅推进 Day 1 白天（Hour 1..5，含 Hour 3 野怪战与战利品）；进入 Hour 6 PvP 门前停止，避免走 Random.Shared 的随机镜像池
                if (run.State == ERunState.Choice && (run.Day > 1 || run.Hour >= (uint)fx.Mode.HoursInADay))
                    break;

                string currentEncKey = $"{run.Day}:{run.Hour}:{run.State}:{run.CurrentEncounterId}";
                if (!string.Equals(trackedEncounterKey, currentEncKey, StringComparison.Ordinal))
                {
                    trackedEncounterKey = currentEncKey;
                    actionsInCurrentEncounter = 0;
                    rerolledInCurrentEncounter = false;
                }

                (INetCommand Cmd, string Label) PickNextCommand()
                {
                    if (run.State == ERunState.Choice)
                    {
                        if (run.SelectionSet.Count > 0)
                            return (new SelectEncounterCommand { InstanceId = new InstanceId(run.SelectionSet[0]) }, "Choice.Select[0]");
                        return (new ExitCurrentStateCommand(), "Choice.ExitFallback");
                    }

                    if (run.State == ERunState.Encounter)
                    {
                        int gold = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
                        if (!rerolledInCurrentEncounter &&
                            run.RerollsRemaining > 0 &&
                            gold >= (int)run.RerollCost &&
                            run.SelectionSet.Count > 0 &&
                            run.SelectionSet.All(id => run.Cards.TryGetValue(id, out var ce) && ce.Type is ECardType.Item or ECardType.Skill))
                        {
                            rerolledInCurrentEncounter = true;
                            return (new RerollCommand(), "Encounter.Reroll");
                        }

                        if (actionsInCurrentEncounter < 2 && run.SelectionSet.Count > 0)
                        {
                            for (int i = 0; i < run.SelectionSet.Count; i++)
                            {
                                string cid = run.SelectionSet[i];
                                if (!run.Cards.TryGetValue(cid, out var ce))
                                    continue;

                                if (ce.Type == ECardType.Item)
                                {
                                    int price = ce.IsFreeCandidate
                                        ? 0
                                        : run.ActiveShelfDiscounts.TryGetValue(cid, out int disc)
                                            ? disc
                                            : ce.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
                                    if (price <= gold)
                                    {
                                        actionsInCurrentEncounter++;
                                        return (new SelectItemCommand { InstanceId = new InstanceId(cid) }, $"Encounter.SelectItem[{i}]");
                                    }
                                }
                                else if (ce.Type == ECardType.Skill)
                                {
                                    int price = ce.IsFreeCandidate
                                        ? 0
                                        : run.ActiveShelfDiscounts.TryGetValue(cid, out int disc)
                                            ? disc
                                            : ce.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
                                    if (price <= gold)
                                    {
                                        actionsInCurrentEncounter++;
                                        return (new SelectSkillCommand { InstanceId = new InstanceId(cid) }, $"Encounter.SelectSkill[{i}]");
                                    }
                                }
                                else if (ce.Type is ECardType.EventEncounter or ECardType.EncounterStep)
                                {
                                    actionsInCurrentEncounter++;
                                    return (new SelectEncounterCommand { InstanceId = new InstanceId(cid) }, $"Encounter.SelectEncounter[{i}]");
                                }
                            }
                        }

                        return (new ExitCurrentStateCommand(), "Encounter.Exit");
                    }

                    if (run.State == ERunState.Pedestal)
                    {
                        var handCard = run.Cards.Values
                            .Where(c => c.Owner == ECombatantId.Player && c.Section == EInventorySection.Hand)
                            .OrderBy(c => c.Socket.HasValue ? (int)c.Socket.Value : 99)
                            .ThenBy(c => RunState.ResolveCoreTemplateId(c), StringComparer.Ordinal)
                            .FirstOrDefault();
                        if (handCard != null && actionsInCurrentEncounter == 0)
                        {
                            actionsInCurrentEncounter++;
                            return (new CommitToPedestalCommand { InstanceId = new InstanceId(handCard.InstanceId) }, "Pedestal.CommitHand[0]");
                        }
                        return (new ExitCurrentStateCommand(), "Pedestal.Exit");
                    }

                    if (run.State is ERunState.Loot or ERunState.LevelUp)
                    {
                        if (run.SelectionSet.Count > 0 && run.Cards.TryGetValue(run.SelectionSet[0], out var first))
                        {
                            return first.Type switch
                            {
                                ECardType.Item => (new SelectItemCommand { InstanceId = new InstanceId(first.InstanceId) }, $"{run.State}.SelectItem[0]"),
                                ECardType.Skill => (new SelectSkillCommand { InstanceId = new InstanceId(first.InstanceId) }, $"{run.State}.SelectSkill[0]"),
                                _ => (new SelectEncounterCommand { InstanceId = new InstanceId(first.InstanceId) }, $"{run.State}.SelectEncounter[0]"),
                            };
                        }
                        return (new ExitCurrentStateCommand(), $"{run.State}.Exit");
                    }

                    return (new ExitCurrentStateCommand(), $"{run.State}.Exit");
                }

                var (cmd, label) = PickNextCommand();
                var outcome = engine.Handle(cmd);
                Record(outcome.Error == null ? label : label + ":ERR");
                cmdsSinceRebuild++;

                if (interruptEveryTwoCommands && cmdsSinceRebuild >= 2)
                    RebuildFromSnapshot();

                if (outcome.Error != null && cmd is not ExitCurrentStateCommand)
                {
                    var fallback = engine.Handle(new ExitCurrentStateCommand());
                    Record(fallback.Error == null ? "Fallback.Exit" : "Fallback.Exit:ERR");
                    cmdsSinceRebuild++;
                    if (interruptEveryTwoCommands && cmdsSinceRebuild >= 2)
                        RebuildFromSnapshot();
                }
            }

            var ownedCards = run.Cards.Values
                .Where(c => c.Owner == ECombatantId.Player)
                .Select(c => new OwnedCardProjection(
                    Section: c.Section?.ToString() ?? "None",
                    Socket: c.Socket?.ToString() ?? "None",
                    TemplateId: RunState.ResolveCoreTemplateId(c),
                    Tier: c.Tier,
                    Enchantment: c.Enchantment?.ToString() ?? "None"))
                .OrderBy(c => c.Section, StringComparer.Ordinal)
                .ThenBy(c => c.Socket, StringComparer.Ordinal)
                .ThenBy(c => c.TemplateId, StringComparer.Ordinal)
                .ThenBy(c => c.Tier)
                .ThenBy(c => c.Enchantment, StringComparer.Ordinal)
                .ToList();

            var stats = new FinalRunStats(
                Day: run.Day,
                Hour: run.Hour,
                State: run.State,
                Gold: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0),
                Health: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Health, 0),
                HealthMax: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 0),
                Level: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 0),
                Experience: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0),
                Prestige: run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0));

            return new ScriptRunTrace(history, ownedCards, stats, rebuildCount);
        }

        var continuous = ExecuteScript(interruptEveryTwoCommands: false);
        var interrupted = ExecuteScript(interruptEveryTwoCommands: true);

        Assert.True(interrupted.RebuildCount >= 3, $"应至少触发 3 次中断序列化重建，实际={interrupted.RebuildCount}");
        Assert.True(
            continuous.Stats.Day > 1 || continuous.Stats.Hour >= (uint)fx.Mode.HoursInADay,
            $"脚本应推进至 Day 1 Hour 6 门前（覆盖野怪战与多个小时），实际停留 Day={continuous.Stats.Day} Hour={continuous.Stats.Hour} State={continuous.Stats.State}");

        Assert.Equal(continuous.History, interrupted.History);
        Assert.Equal(continuous.OwnedCards, interrupted.OwnedCards);
        Assert.Equal(continuous.Stats, interrupted.Stats);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #4：同一小时内两次进入同一链式商店 / 祭坛 / 奇遇重抽 → 两次结果不同
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test04_SameHourRepeatStreamAccess_ProducesDifferentResults()
    {
        var fx = SharedGameDataFixture.Shared;

        // 1) 同一小时内两次进入同一商人商店（模拟链式事件再次进入同商店，不推进小时、不改 DailyRerollSequence）
        var merchantCandidates = fx.Pool.HourlyEncounters("Vanessa", "EventEncounter")
            .Where(e => CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, fx.Repo.GetCard(e.Id)))
            .ToList();
        Assert.NotEmpty(merchantCandidates);

        bool verifiedDifferentShopShelf = false;
        bool verifiedDifferentRerollShelf = false;

        foreach (var merchantTpl in merchantCandidates.Take(20))
        {
            var run = new RunState
            {
                RunId = "w6_same_hour_shop",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = 3,
                Hour = 2,
                MasterSeed = 20261128,
                RngSeed = 20261128,
                DailyRerollSequence = 0,
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);

            shop.DealEncounterShelf(merchantTpl.Id, isReroll: false);
            var shelf1 = run.SelectionSet
                .Select(id => $"{run.Cards[id].TemplateId}:{run.Cards[id].Tier}")
                .ToList();
            int occAfterFirstShop = run.StreamOccurrences.GetValueOrDefault($"{DailyChallengeSeedGenerator.StreamShop}|0", 0);

            shop.DealEncounterShelf(merchantTpl.Id, isReroll: false);
            var shelf2 = run.SelectionSet
                .Select(id => $"{run.Cards[id].TemplateId}:{run.Cards[id].Tier}")
                .ToList();
            int occAfterSecondShop = run.StreamOccurrences.GetValueOrDefault($"{DailyChallengeSeedGenerator.StreamShop}|0", 0);

            Assert.Equal(1, occAfterFirstShop);
            Assert.Equal(2, occAfterSecondShop);

            if (shelf1.Count >= 2 && shelf2.Count >= 2 && !shelf1.SequenceEqual(shelf2))
                verifiedDifferentShopShelf = true;

            // 同一小时内不递增 DailyRerollSequence 的两次商店重抽（isReroll: true）
            shop.DealEncounterShelf(merchantTpl.Id, isReroll: true);
            var reroll1 = run.SelectionSet
                .Select(id => $"{run.Cards[id].TemplateId}:{run.Cards[id].Tier}")
                .ToList();
            int occAfterFirstReroll = run.StreamOccurrences.GetValueOrDefault($"{DailyChallengeSeedGenerator.StreamReroll}|0", 0);

            shop.DealEncounterShelf(merchantTpl.Id, isReroll: true);
            var reroll2 = run.SelectionSet
                .Select(id => $"{run.Cards[id].TemplateId}:{run.Cards[id].Tier}")
                .ToList();
            int occAfterSecondReroll = run.StreamOccurrences.GetValueOrDefault($"{DailyChallengeSeedGenerator.StreamReroll}|0", 0);

            Assert.Equal(1, occAfterFirstReroll);
            Assert.Equal(2, occAfterSecondReroll);

            if (reroll1.Count >= 2 && reroll2.Count >= 2 && !reroll1.SequenceEqual(reroll2))
                verifiedDifferentRerollShelf = true;

            if (verifiedDifferentShopShelf && verifiedDifferentRerollShelf)
                break;
        }

        Assert.True(verifiedDifferentShopShelf, "同一小时内两次进入同一商人商店必须产出不同货架结果。");
        Assert.True(verifiedDifferentRerollShelf, "同一小时内两次重抽同一商店必须产出不同货架结果。");

        // 2) 同一小时内两次进入祭坛随机附魔流（StreamPedestalEnchant）与链式奇遇步进流（StreamEncounterStep）→ 两次结果不同
        var streamRun = new RunState
        {
            RunId = "w6_same_hour_streams",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 4,
            Hour = 4,
            MasterSeed = 99887766,
            RngSeed = 99887766,
        };
        var rr = new RunRandom(streamRun);

        static int[] Sample(Random? rng)
        {
            Assert.NotNull(rng);
            var arr = new int[8];
            for (int i = 0; i < arr.Length; i++) arr[i] = rng!.Next();
            return arr;
        }

        var pedestal1 = Sample(rr.Stream(DailyChallengeSeedGenerator.StreamPedestalEnchant));
        var pedestal2 = Sample(rr.Stream(DailyChallengeSeedGenerator.StreamPedestalEnchant));
        Assert.Equal(2, streamRun.StreamOccurrences[$"{DailyChallengeSeedGenerator.StreamPedestalEnchant}|0"]);
        Assert.NotEqual(pedestal1, pedestal2);

        var step1 = Sample(rr.Stream(DailyChallengeSeedGenerator.StreamEncounterStep));
        var step2 = Sample(rr.Stream(DailyChallengeSeedGenerator.StreamEncounterStep));
        Assert.Equal(2, streamRun.StreamOccurrences[$"{DailyChallengeSeedGenerator.StreamEncounterStep}|0"]);
        Assert.NotEqual(step1, step2);

        // 3) 跨小时后 StreamOccurrenceStamp 自动重置，同一标签从第 0 次重新计数
        streamRun.Hour = 5;
        _ = rr.Stream(DailyChallengeSeedGenerator.StreamPedestalEnchant);
        Assert.Equal(405, streamRun.StreamOccurrenceStamp);
        Assert.Equal(1, streamRun.StreamOccurrences[$"{DailyChallengeSeedGenerator.StreamPedestalEnchant}|0"]);
        Assert.False(streamRun.StreamOccurrences.ContainsKey($"{DailyChallengeSeedGenerator.StreamEncounterStep}|0"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #5：每日挑战首次出现的随机流与旧公式一致（直接比对 DeriveSubSeed）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test05_DailyChallengeFirstOccurrence_MatchesLegacyDeriveSubSeedFormula()
    {
        var spec = DailyChallengeSeedGenerator.GenerateSpec("2026-11-28");
        int masterSeed = spec.MasterSeed;

        string[] tags =
        [
            DailyChallengeSeedGenerator.StreamEncounter,
            DailyChallengeSeedGenerator.StreamShop,
            DailyChallengeSeedGenerator.StreamReroll,
            DailyChallengeSeedGenerator.StreamMonster,
            DailyChallengeSeedGenerator.StreamLoot,
            DailyChallengeSeedGenerator.StreamLevelUp,
            DailyChallengeSeedGenerator.StreamEncounterStep,
            DailyChallengeSeedGenerator.StreamPedestalEnchant,
        ];

        (uint Day, uint Hour, int Index)[] probes =
        [
            (1u, 1u, 0),
            (3u, 3u, 0),
            (5u, 2u, 4),
            (10u, 5u, 2),
        ];

        foreach (var (day, hour, index) in probes)
        {
            var run = new RunState
            {
                RunId = $"w6_daily_{day}_{hour}_{index}",
                GameModeId = SharedGameDataFixture.Shared.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = day,
                Hour = hour,
                MasterSeed = masterSeed,
                RngSeed = masterSeed,
                CustomRules = spec.RuleSet.CloneNormalized(),
            };
            var rr = new RunRandom(run);

            foreach (string tag in tags)
            {
                // 第 0 次调用：与旧公式 DeriveSubSeed(masterSeed, tag, day, hour, index) 100% 逐项相等
                var actualFirst = rr.Stream(tag, index);
                Assert.NotNull(actualFirst);

                int legacySeed0 = DailyChallengeSeedGenerator.DeriveSubSeed(masterSeed, tag, (int)day, (int)hour, index);
                var expectedFirst = new Random(legacySeed0);

                var actualNums0 = new int[10];
                var expectedNums0 = new int[10];
                for (int i = 0; i < 10; i++)
                {
                    actualNums0[i] = actualFirst!.Next();
                    expectedNums0[i] = expectedFirst.Next();
                }
                Assert.Equal(expectedNums0, actualNums0);

                // 同小时第 1 次重复调用：追加 "#1"，与 DeriveSubSeed(masterSeed, tag + "#1", ...) 一致且不同于首次
                var actualSecond = rr.Stream(tag, index);
                Assert.NotNull(actualSecond);

                int legacySeed1 = DailyChallengeSeedGenerator.DeriveSubSeed(masterSeed, $"{tag}#1", (int)day, (int)hour, index);
                var expectedSecond = new Random(legacySeed1);

                var actualNums1 = new int[10];
                var expectedNums1 = new int[10];
                for (int i = 0; i < 10; i++)
                {
                    actualNums1[i] = actualSecond!.Next();
                    expectedNums1[i] = expectedSecond.Next();
                }
                Assert.Equal(expectedNums1, actualNums1);
                Assert.NotEqual(actualNums0, actualNums1);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #6：种子码往返 10 万随机 int + 全角/半角等价 + 极端边界不崩
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test06_SeedCodeRoundTrip_100kRandomIntsFullWidthAndBoundaryTexts()
    {
        // 1) 10 万个随机 int（含正负数及极端边界）往返全部精确还原
        var rng = new Random(20261128);
        int[] edgeInts = [1, -1, 42, -42, 100001, int.MaxValue, int.MinValue, int.MinValue + 1, int.MaxValue - 1];

        foreach (int v in edgeInts)
        {
            string code = DailyChallengeSeedGenerator.FormatSeedCode(v);
            Assert.Equal(8, code.Length);
            Assert.StartsWith("S", code, StringComparison.Ordinal);

            bool ok = DailyChallengeSeedGenerator.TryParseSeedText(code, out int parsed, out string display);
            Assert.True(ok);
            Assert.Equal(v, parsed);
            Assert.Equal(code, display);
        }

        byte[] buf = new byte[4];
        for (int i = 0; i < 100_000; i++)
        {
            rng.NextBytes(buf);
            int val = BitConverter.ToInt32(buf, 0);
            if (val == 0) val = 1; // 种子 0 视为未设置，规范化会兜底到 1

            string code = DailyChallengeSeedGenerator.FormatSeedCode(val);
            bool ok = DailyChallengeSeedGenerator.TryParseSeedText(code, out int parsed, out string display);
            Assert.True(ok, $"第 {i} 个随机种子 {val}（code={code}）解析失败");
            Assert.Equal(val, parsed);
            Assert.Equal(code, display);
        }

        // 2) 只认固定格式的种子码：数字、文字、超长文本一律不认
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("123456", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("１２３４５６", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("-987654", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("0", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText(int.MinValue.ToString(CultureInfo.InvariantCulture), out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("我的幸运种子", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText(new string('A', 80), out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("大巴扎测试种子_BazaarSeed_１２３４５６_" + new string('星', 60), out _, out _));

        // 大小写、空格、O/0、I/L/1 笔误自动纠正成规范写法
        string anyCode = DailyChallengeSeedGenerator.FormatSeedCode(123456789);
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText(" " + anyCode.ToLowerInvariant() + " ", out int lowerParsed, out string lowerDisp));
        Assert.Equal(123456789, lowerParsed);
        Assert.Equal(anyCode, lowerDisp);
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText("SO0000L1", out _, out string fixDisp));
        Assert.Equal("S0000011", fixDisp);

        string sampleCode = DailyChallengeSeedGenerator.FormatSeedCode(-135792468);
        string fullWidthCode = string.Concat(sampleCode.Select(ch => (char)(ch + 0xFEE0)));
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText(fullWidthCode, out int fwDecoded, out string fwDisp));
        Assert.Equal(-135792468, fwDecoded);
        Assert.Equal(sampleCode, fwDisp);

        // 3) 边界输入："0"、int.MinValue、空文本、48 字以上超长文本全部安全不崩
        string zeroCode = DailyChallengeSeedGenerator.FormatSeedCode(0);
        Assert.Equal(8, zeroCode.Length);
        Assert.True(DailyChallengeSeedGenerator.TryParseSeedText(zeroCode, out int zeroCodeParsed, out _));
        Assert.NotEqual(0, zeroCodeParsed);

        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText(null, out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("", out _, out _));
        Assert.False(DailyChallengeSeedGenerator.TryParseSeedText("   \t\r\n　 ", out _, out _));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #7：老客户端 PUT 不带新字段保留新字段；改非零 fixedRunSeed 清空文本种子；切预设/切模式保留新字段
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test07_LegacyPutAndPresetModeEndpoints_PreserveSeedAndLineupFields()
    {
        var fx = SharedGameDataFixture.Shared;
        string dir = NewTempDir("bz_w6_t7");
        string dbPath = Path.Combine(dir, "test.db");
        string stateDir = Path.Combine(dir, "state");
        string emptyModsDir = Path.Combine(dir, "mods");
        string emptyGhostDir = Path.Combine(dir, "ghosts");
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(emptyModsDir);
        Directory.CreateDirectory(emptyGhostDir);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var modHost = new ServerModHost(fx.Repo, fx.DataDir, stateDir, fx.CreateValidCardIdsCopy(), modsRootOverride: emptyModsDir);
        var communityMods = new CommunityModStore(modHost, stateDir);
        var ruleStore = new PlayerRuleStore(db, modHost);
        var sessions = new SessionStore();
        var isolatedPool = fx.CreateIsolatedCardPool();
        var monsterPoolStore = new MonsterPoolStore(fx.CreateMonstersCopy(), modHost.ActiveRepository);
        var runtimeSnapshots = new ServerRuntimeSnapshotProvider(modHost, isolatedPool);
        var ghostPool = PvpGhostPool.Load(emptyGhostDir);

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(modHost);
        builder.Services.AddSingleton(communityMods);
        builder.Services.AddSingleton(ruleStore);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton<GameDataRepository>(modHost.BaseRepository);
        builder.Services.AddSingleton(isolatedPool);
        builder.Services.AddSingleton(monsterPoolStore);
        builder.Services.AddSingleton(runtimeSnapshots);
        builder.Services.AddSingleton(ghostPool);
        builder.Services.AddSingleton<PlannerSessionStore>();
        builder.Services.AddSingleton<GhostLineupStore>();
        builder.Services.AddSingleton<RunBlueprintService>();
        builder.Services.AddSingleton<EventSeedFinderService>();

        await using var app = builder.Build();

        try
        {
            CustomRuleEndpoints.Map(app, ruleStore, accounts, () => new { }, sessions, db);
            app.MapPlannerEndpoints();
            await app.StartAsync();

            string baseUrl = app.Urls.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

            var acc = accounts.GetOrCreateByName("w6_legacy_compat");
            string accountId = acc.AccountId.ToString("D");
            string bearer = accounts.IssuePersistentTokens(acc, "game", "w6_dev_01").AccessToken;

            // 预存一套归属该账号的合法剧本，供 apply-rules 校验存在性
            var saveLineup = db.SaveGhostLineup(new CustomGhostLineup
            {
                LineupId = "ln_w6_compat_01",
                AccountId = accountId,
                Name = "Wave6 兼容测试剧本",
                SourceType = "custom",
            });
            Assert.True(saveLineup.Ok, saveLineup.ErrorCode);

            // 1) 通过网页编排台 apply-rules 写入自定义种子 + 固定剧本模式 + 剧本 ID
            using (var applyReq = new HttpRequestMessage(HttpMethod.Post, "/api/planner/apply-rules"))
            {
                applyReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                applyReq.Content = new StringContent(
                    """{"customSeedText":"S1W6KEEP","ghostOpponentMode":"FixedLineup","selectedGhostLineupId":"ln_w6_compat_01"}""",
                    Encoding.UTF8,
                    "application/json");
                using var applyResp = await client.SendAsync(applyReq);
                string applyBody = await applyResp.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, applyResp.StatusCode);
                Assert.Contains("S1W6KEEP", applyBody, StringComparison.Ordinal);
            }

            var afterApply = ruleStore.Get(accountId);
            Assert.Equal("S1W6KEEP", afterApply.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterApply.GhostOpponentMode);
            Assert.Equal("ln_w6_compat_01", afterApply.SelectedGhostLineupId);

            // 1b) 种子只认固定格式的码：随手填的文字种子必须被拒（400 SEED_INVALID），原种子保持不变
            using (var badSeedReq = new HttpRequestMessage(HttpMethod.Post, "/api/planner/apply-rules"))
            {
                badSeedReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                badSeedReq.Content = new StringContent("""{"customSeedText":"我的幸运种子"}""", Encoding.UTF8, "application/json");
                using var badSeedResp = await client.SendAsync(badSeedReq);
                Assert.Equal(HttpStatusCode.BadRequest, badSeedResp.StatusCode);
                Assert.Contains("SEED_INVALID", await badSeedResp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
            Assert.Equal("S1W6KEEP", ruleStore.Get(accountId).CustomSeedText);

            // 2) 老客户端 PUT /custom-rules/me 不带三个新字段 → 新字段全部完整保留
            using (var putReq = new HttpRequestMessage(HttpMethod.Put, "/custom-rules/me"))
            {
                putReq.Headers.Add("X-Username", "w6_legacy_compat");
                putReq.Content = new StringContent(
                    """{"presetId":"PureOfficial","specialModeId":"Standard","startingGold":24,"monsterLootChooseOne":true}""",
                    Encoding.UTF8,
                    "application/json");
                using var putResp = await client.SendAsync(putReq);
                Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
            }

            var afterLegacyPut = ruleStore.Get(accountId);
            Assert.Equal("S1W6KEEP", afterLegacyPut.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterLegacyPut.GhostOpponentMode);
            Assert.Equal("ln_w6_compat_01", afterLegacyPut.SelectedGhostLineupId);
            Assert.Equal(24, afterLegacyPut.StartingGold);

            // 3) 老客户端 PUT 不带 customSeedText，但改了与库里不同的非零 fixedRunSeed → 清空文本种子、旧整数种子生效，剧本字段继续保留
            using (var legacySeedReq = new HttpRequestMessage(HttpMethod.Put, "/custom-rules/me"))
            {
                legacySeedReq.Headers.Add("X-Username", "w6_legacy_compat");
                legacySeedReq.Content = new StringContent(
                    """{"presetId":"PureOfficial","specialModeId":"Standard","fixedRunSeed":20260707,"monsterLootChooseOne":true}""",
                    Encoding.UTF8,
                    "application/json");
                using var legacySeedResp = await client.SendAsync(legacySeedReq);
                Assert.Equal(HttpStatusCode.OK, legacySeedResp.StatusCode);
            }

            var afterLegacyIntSeed = ruleStore.Get(accountId);
            Assert.Null(afterLegacyIntSeed.CustomSeedText);
            Assert.Equal(20260707, afterLegacyIntSeed.FixedRunSeed);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterLegacyIntSeed.GhostOpponentMode);
            Assert.Equal("ln_w6_compat_01", afterLegacyIntSeed.SelectedGhostLineupId);

            // 4) 重新设回文本种子与固定剧本，随后切换预设档位和玩法模式 → 三个新字段均完整保留
            using (var applyReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/planner/apply-rules"))
            {
                applyReq2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                applyReq2.Content = new StringContent(
                    """{"customSeedText":"S2W6PRES","ghostOpponentMode":"FixedLineup","selectedGhostLineupId":"ln_w6_compat_01"}""",
                    Encoding.UTF8,
                    "application/json");
                using var applyResp2 = await client.SendAsync(applyReq2);
                Assert.Equal(HttpStatusCode.OK, applyResp2.StatusCode);
            }

            using (var presetReq = new HttpRequestMessage(HttpMethod.Post, "/custom-rules/me/preset/EnhancedFun"))
            {
                presetReq.Headers.Add("X-Username", "w6_legacy_compat");
                presetReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var presetResp = await client.SendAsync(presetReq);
                Assert.Equal(HttpStatusCode.OK, presetResp.StatusCode);
            }
            var afterPreset = ruleStore.Get(accountId);
            Assert.Equal(EGameModePreset.EnhancedFun, afterPreset.PresetId);
            Assert.Equal("S2W6PRES", afterPreset.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterPreset.GhostOpponentMode);
            Assert.Equal("ln_w6_compat_01", afterPreset.SelectedGhostLineupId);

            using (var modeReq = new HttpRequestMessage(HttpMethod.Post, "/custom-rules/me/mode/EndlessTower"))
            {
                modeReq.Headers.Add("X-Username", "w6_legacy_compat");
                modeReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var modeResp = await client.SendAsync(modeReq);
                Assert.Equal(HttpStatusCode.OK, modeResp.StatusCode);
            }
            var afterMode = ruleStore.Get(accountId);
            Assert.Equal(ESpecialModeId.EndlessTower, afterMode.SpecialModeId);
            Assert.Equal("S2W6PRES", afterMode.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterMode.GhostOpponentMode);
            Assert.Equal("ln_w6_compat_01", afterMode.SelectedGhostLineupId);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            db.Dispose();
            CleanupDir(dir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #8：PlayerRuleStore 保存后有效 Mod 词条还在，未知词条仍被去掉
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test08_PlayerRuleStore_PreservesValidModMutatorAndStripsUnknown()
    {
        var fx = SharedGameDataFixture.Shared;
        string sampleModDir = Path.Combine(fx.ModsRoot, "bazaar-chronicles-sample");
        Assert.True(Directory.Exists(sampleModDir), $"未找到仓库实样 Mod 目录：{sampleModDir}");

        string dir = NewTempDir("bz_w6_t8");
        string dbPath = Path.Combine(dir, "test.db");
        string stateDir = Path.Combine(dir, "state");
        Directory.CreateDirectory(stateDir);

        try
        {
            using var db = new Persistence(dbPath);
            var modHost = new ServerModHost(
                fx.Repo,
                fx.DataDir,
                stateDir,
                fx.CreateValidCardIdsCopy(),
                modsRootOverride: fx.ModsRoot);

            // 即使服务端未全局启用该 Mod，只要玩家单局 SelectedModIds 勾选了它，其有效 Mod 词条也必须被保留
            modHost.ToggleMod("bazaar-chronicles-sample", enabled: false);

            const string validModMutatorId = "bazaar-chronicles-sample:mutator_starport_boom";
            const string unknownMutatorId = "wave6_unknown_garbage_mutator_999";
            const string accountId = "w6-mod-mutator-account";

            var store1 = new PlayerRuleStore(db, modHost);
            var saved = store1.Set(accountId, new CustomRunRuleSet
            {
                SpecialModeId = ESpecialModeId.MutatorChallenge,
                SelectedModIds = new List<string> { "bazaar-chronicles-sample" },
                MutatorIds = new List<string>
                {
                    validModMutatorId,
                    MutatorCatalog.TurboFrenzy,
                    unknownMutatorId,
                },
            });

            Assert.Contains(validModMutatorId, saved.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(MutatorCatalog.TurboFrenzy, saved.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(unknownMutatorId, saved.MutatorIds, StringComparer.OrdinalIgnoreCase);

            // 冷启动重建 PlayerRuleStore 从 SQLite 重新读取，结果必须一致
            var store2 = new PlayerRuleStore(db, modHost);
            var reloaded = store2.Get(accountId);
            Assert.Contains(validModMutatorId, reloaded.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(MutatorCatalog.TurboFrenzy, reloaded.MutatorIds, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(unknownMutatorId, reloaded.MutatorIds, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #9：旧快照恢复后新生成的实例 ID 不与已有 ID 重复
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test09_LegacySnapshotRestore_DerivesNextInstanceSeqAndPreventsIdCollision()
    {
        var fx = SharedGameDataFixture.Shared;

        var sampleItem = fx.Pool.ShopItems("Vanessa").First();
        var sampleSkill = fx.Pool.Skills("Vanessa").First();

        var run = new RunState
        {
            RunId = "w6_legacy_seq",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 2,
            Hour = 2,
            MasterSeed = 20261128,
            RngSeed = 20261128,
        };

        var existingIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "itm_001a_1700000001",
            "itm_00fe_1700000002",
            "skl_0105_1700000003", // 最大十六进制序号 0x0105 = 261
        };

        run.Cards["itm_001a_1700000001"] = new RunState.CardEntry
        {
            InstanceId = "itm_001a_1700000001",
            TemplateId = Guid.Parse(sampleItem.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(sampleItem.Size),
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
        };
        run.Cards["itm_00fe_1700000002"] = new RunState.CardEntry
        {
            InstanceId = "itm_00fe_1700000002",
            TemplateId = Guid.Parse(sampleItem.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(sampleItem.Size),
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
        };
        run.Cards["skl_0105_1700000003"] = new RunState.CardEntry
        {
            InstanceId = "skl_0105_1700000003",
            TemplateId = Guid.Parse(sampleSkill.Id),
            Type = ECardType.Skill,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = null,
            Socket = EContainerSocketId.Socket_0,
        };

        // 构造模拟旧版快照（NextInstanceSeq == 0）
        var dto = RunSnapshotSerializer.SerializeToDto(run, startSelectionDone: true);
        dto.NextInstanceSeq = 0;
        string legacyJson = System.Text.Json.JsonSerializer.Serialize(dto);

        var restored = RunSnapshotSerializer.Deserialize(legacyJson, fx.Repo);
        Assert.Equal(0x0106, restored.NextInstanceSeq);

        var shop = new ShopEngine(restored, fx.Pool, fx.Repo);
        var generatedIds = new HashSet<string>(StringComparer.Ordinal);
        int seqStart = restored.NextInstanceSeq;

        for (uint h = 1; h <= 5; h++)
        {
            restored.Hour = h;
            shop.DealHourEncounters();
            foreach (string id in restored.SelectionSet)
            {
                Assert.DoesNotContain(id, existingIds);
                Assert.True(generatedIds.Add(id), $"检测到重复生成的实例 ID：{id}");
            }
        }

        var merchant = fx.Pool.HourlyEncounters("Vanessa", "EventEncounter")
            .First(e => CardArchetypeIndex.IsMerchantEncounterTemplate(e.Id, e.InternalName, e, fx.Repo.GetCard(e.Id)));
        restored.Hour = 2;
        shop.DealEncounterShelf(merchant.Id);
        foreach (string id in restored.SelectionSet)
        {
            Assert.DoesNotContain(id, existingIds);
            Assert.True(generatedIds.Add(id), $"商人货架检测到重复生成的实例 ID：{id}");
        }

        Assert.True(generatedIds.Count >= 10, $"应至少生成 10 个新实例 ID，实际={generatedIds.Count}");
        Assert.True(restored.NextInstanceSeq > seqStart, "NextInstanceSeq 应随新卡牌生成单调递增");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #15：锦标赛大厅规则不含三个新字段
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Test15_TournamentLobbyRules_AlwaysStripsThreeNewSeedAndLineupFields()
    {
        string dir = NewTempDir("bz_w6_t15");
        string dbPath = Path.Combine(dir, "test.db");

        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var lobbies = new LobbyStore(db);
            var host = accounts.GetOrCreateByName("w6_tourney_host");

            // 1) 创建大厅时传入三个新字段 → 必须全部清空
            var dirtyCreateRules = new CustomRunRuleSet
            {
                CustomSeedText = "S1T0RNEY",
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = "ln_forbidden_in_tournament",
                StartingGold = 15,
            };

            var lobby = lobbies.Create(
                host,
                name: "Wave6 锦标赛边界测试房",
                playMode: "Tournament",
                playerCap: 4,
                isPublic: false,
                rules: dirtyCreateRules);

            Assert.Null(lobby.Rules.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.Random, lobby.Rules.GhostOpponentMode);
            Assert.Null(lobby.Rules.SelectedGhostLineupId);
            Assert.Equal(15, lobby.Rules.StartingGold);

            // 2) UpdateRules 再次尝试写入三个新字段 → 依然全部清空
            var dirtyUpdateRules = new CustomRunRuleSet
            {
                CustomSeedText = "S1TRY1NJ",
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = "ln_inject_again",
                StartingGold = 20,
            };
            lobbies.UpdateRules(lobby, dirtyUpdateRules);

            Assert.Null(lobby.Rules.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.Random, lobby.Rules.GhostOpponentMode);
            Assert.Null(lobby.Rules.SelectedGhostLineupId);
            Assert.Equal(20, lobby.Rules.StartingGold);

            // 3) 不含 CustomSeedText 的传统锦标赛 FixedRunSeed 仍正常保留，且冷启动从 SQLite 恢复后三新字段仍为空
            lobbies.UpdateRules(lobby, new CustomRunRuleSet
            {
                FixedRunSeed = 20269999,
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = "ln_still_stripped",
            });

            Assert.Equal(20269999, lobby.Rules.FixedRunSeed);
            Assert.Null(lobby.Rules.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.Random, lobby.Rules.GhostOpponentMode);
            Assert.Null(lobby.Rules.SelectedGhostLineupId);

            var reloadedStore = new LobbyStore(db);
            var reloadedLobby = reloadedStore.Get(lobby.LobbyId);
            Assert.NotNull(reloadedLobby);
            Assert.Equal(20269999, reloadedLobby!.Rules.FixedRunSeed);
            Assert.Null(reloadedLobby.Rules.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.Random, reloadedLobby.Rules.GhostOpponentMode);
            Assert.Null(reloadedLobby.Rules.SelectedGhostLineupId);
        }
        finally
        {
            CleanupDir(dir);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 测试 #16：插件约定（LauncherAssembly 反射 BazaarLauncher.CustomRunRuleSet）
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Test16_LauncherCustomRunRuleSetContract_OmitsNewFieldsOnSerializeAndParsesBothFormats()
    {
        var ruleType = _launcherFx.LauncherAssembly.GetType("BazaarLauncher.CustomRunRuleSet", throwOnError: true)!;

        var instance = Activator.CreateInstance(ruleType);
        Assert.NotNull(instance);

        void SetProp(string name, object? value)
        {
            var prop = ruleType.GetProperty(name);
            Assert.NotNull(prop);
            prop!.SetValue(instance, value);
        }

        object? GetProp(object obj, string name)
        {
            var prop = ruleType.GetProperty(name);
            Assert.NotNull(prop);
            return prop!.GetValue(obj);
        }

        SetProp("CustomModeId", "wave6_custom_mode");
        SetProp("CustomSeedText", "SHOULD-NOT-SERIALIZE");
        SetProp("GhostOpponentMode", "FixedLineup");
        SetProp("SelectedGhostLineupId", "ln_should_not_serialize");
        SetProp("StartingGold", 18);

        // 1) JsonConvert.SerializeObject 不含三个新字段，但包含 customModeId
        string serialized = JsonConvert.SerializeObject(instance);
        var jObj = JObject.Parse(serialized);

        Assert.Null(jObj.Property("customSeedText", StringComparison.OrdinalIgnoreCase));
        Assert.Null(jObj.Property("ghostOpponentMode", StringComparison.OrdinalIgnoreCase));
        Assert.Null(jObj.Property("selectedGhostLineupId", StringComparison.OrdinalIgnoreCase));

        var customModeProp = jObj.Property("customModeId", StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(customModeProp);
        Assert.Equal("wave6_custom_mode", customModeProp!.Value.Value<string>());

        // 2) 反序列化兼容数字（0/1）、字符串（"Random"/"FixedLineup"）与缺省值
        (string Json, string ExpectedMode)[] cases =
        [
            ("""{"ghostOpponentMode":1}""", "FixedLineup"),
            ("""{"ghostOpponentMode":"FixedLineup"}""", "FixedLineup"),
            ("""{"ghostOpponentMode":0}""", "Random"),
            ("""{"ghostOpponentMode":"Random"}""", "Random"),
            ("""{}""", "Random"),
        ];

        foreach (var (json, expectedMode) in cases)
        {
            var parsed = JsonConvert.DeserializeObject(json, ruleType);
            Assert.NotNull(parsed);
            Assert.Equal(expectedMode, GetProp(parsed!, "GhostOpponentMode") as string);
        }

        // 3) 联动验证第 7 条：直接用插件序列化产出的真实载荷 PUT /custom-rules/me，服务端网页已设置的种子与剧本绝不被清空
        string dir = NewTempDir("bz_w6_t16");
        string dbPath = Path.Combine(dir, "test.db");
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var ruleStore = new PlayerRuleStore(db);
        var sessions = new SessionStore();

        await using var app = builder.Build();
        try
        {
            CustomRuleEndpoints.Map(app, ruleStore, accounts, () => new { }, sessions, db);
            await app.StartAsync();

            var acc = accounts.GetOrCreateByName("w6_plugin_save_user");
            string accountId = acc.AccountId.ToString("D");

            // 模拟玩家在网页编排台先设好了种子与剧本
            ruleStore.Set(accountId, new CustomRunRuleSet
            {
                CustomSeedText = "S2WEB026",
                GhostOpponentMode = EGhostOpponentMode.FixedLineup,
                SelectedGhostLineupId = "ln_web_selected_01",
            });

            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var putReq = new HttpRequestMessage(HttpMethod.Put, "/custom-rules/me");
            putReq.Headers.Add("X-Username", "w6_plugin_save_user");
            putReq.Content = new StringContent(serialized, Encoding.UTF8, "application/json");
            using var putResp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

            var afterPluginSave = ruleStore.Get(accountId);
            Assert.Equal("S2WEB026", afterPluginSave.CustomSeedText);
            Assert.Equal(EGhostOpponentMode.FixedLineup, afterPluginSave.GhostOpponentMode);
            Assert.Equal("ln_web_selected_01", afterPluginSave.SelectedGhostLineupId);
            Assert.Equal(18, afterPluginSave.StartingGold);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            db.Dispose();
            CleanupDir(dir);
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V10 专项回归测试：
/// 覆盖 #9 升级表 targetNewLevel -> dbId(oldLevel) 1~30 级对位与随机附魔台无错截断、
/// #5/#18 Grab the Loot / Mystery Bundle 等全拿多选免费事件与三选一单选事件对比、
/// #7/#10/#14 官方 6 小时制（H1/2/4/5 = 1商+2发育，H3 = 左铜/中银/右金三梯度且剔除测试怪）、
/// 以及 #19 PvpGhostPool 全天数血量等级门卫（保留 MaxBoardsPerDayBucket = 2500）。
/// </summary>
public sealed class V10ScheduleLevelMonstersTests
{
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string GrabTheLootStepId = "87d3d519-00e8-4208-ba8d-c79f424b9e7a";
    private const string JungleRuinsGrabLootStepId = "e73e7956-1875-4f40-b5f2-d3c4ed975751";
    private const string HaddyMysteryBundleStepId = "ed15d9f2-b77e-4038-a540-84f381e0cb08";
    private const string BurnTrainingStepId = "4a1560bc-dcc8-43e1-b080-8cbfafb84eb2";
    private const string PoisonTrainingStepId = "1289bed0-75d7-434d-892d-e5da6018490a";

    private sealed class Fx
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
    }

    private static readonly Lazy<Fx> RealFixture = new(() =>
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++, dir = Directory.GetParent(dir)?.FullName)
        {
            string candidate = Path.Combine(dir, "dist", "data");
            if (File.Exists(Path.Combine(candidate, "GameData.db")))
            {
                dir = candidate;
                break;
            }
        }
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "GameData.db")))
            dir = @"e:\thebazaar\dist\data";

        string dbPath = Path.Combine(dir, "GameData.db");
        return new Fx
        {
            DataDir = dir,
            DbPath = dbPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, null),
            Ghosts = PvpGhostPool.Load(dir),
        };
    });

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        CustomRunRuleSet? rules = null,
        int seed = 1001)
    {
        var run = new RunState
        {
            RunId = "v10_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        engine.StartRun();
        return (run, engine);
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string encounterId,
        ECardType fallbackType = ECardType.EventEncounter)
    {
        var tpl = pool.Get(encounterId);
        string instId = "inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : fallbackType,
            Size = tpl != null ? ShopEngine.ParseSize(tpl.Size) : ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
            Owner = null,
            Section = null,
            Socket = null,
        };
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static string BuildZeroXpFreeEventJson(string id, string name)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterEvent",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "EventEncounter",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            ExperienceAwardUponSelection = 0,
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = Array.Empty<object>(),
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = 0.0 },
                },
                Rules = new
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                    WillAutoSellOnExit = false,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildWeaponJson(string id, string name, int dmg)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "Item",
            StartingTier = "Bronze",
            Size = "Small",
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = new[] { "Damage" },
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["CooldownMax"] = 1000,
                        ["DamageAmount"] = dmg,
                        ["Multicast"] = 1,
                        ["BuyPrice"] = 2,
                        ["SellPrice"] = 1,
                    },
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { @__type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        @__type = "TActionPlayerDamage",
                        ReferenceValue = new
                        {
                            @__type = "TReferenceValueCardAttribute",
                            AttributeType = "DamageAmount",
                            Target = new { @__type = "TTargetCardSelf", Conditions = (object?)null },
                            DefaultValue = 0.0,
                            Modifier = (object?)null,
                        },
                        Target = new { @__type = "TTargetPlayerRelative", TargetMode = "Opponent", Conditions = (object?)null },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private sealed class OverlayContext : IDisposable
    {
        private readonly string _dir;
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayContext(IEnumerable<(string Id, string Json)> customCards)
        {
            var fx = RealFixture.Value;
            _dir = Path.Combine(Path.GetTempPath(), "bazaar_v10_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            string dbPath = Path.Combine(_dir, "GameData.db");
            File.Copy(fx.DbPath, dbPath, overwrite: true);

            var wl = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string srcWlPath = Path.Combine(fx.DataDir, "valid_card_ids.json");
            if (File.Exists(srcWlPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(srcWlPath));
                var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement
                    : doc.RootElement.GetProperty("valid_ids");
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        wl.Add(el.GetString()!);
            }

            using (var conn = new SqliteConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                foreach (var (id, json) in customCards)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO cards (Id, Data) VALUES ($id, $data)";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$data", System.Text.Encoding.UTF8.GetBytes(json));
                    cmd.ExecuteNonQuery();
                    wl.Add(id);
                }
            }

            if (wl.Count > 0)
                File.WriteAllText(Path.Combine(_dir, "valid_card_ids.json"), JsonSerializer.Serialize(wl.ToList()));

            Pool = CardPool.Load(dbPath);
            Monsters = MonsterPool.Load(dbPath);
            LevelUps = LevelUpTable.Load(dbPath);
            Mode = GameModeConfig.Load(dbPath);
            Repo = GameDatabaseLoader.Load(dbPath, null);
            Ghosts = fx.Ghosts;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch { }
        }
    }

    // ── 1. #9 修复 level_ups 1~30 级错位 1 级 + 附魔台分组不截断 + 瓦内莎真实奖励 ──

    [Fact]
    public void Test01_LevelUps_TargetNewLevelMapsToDbIdMinusOne_HealthAndRewardsMatchOfficialTable()
    {
        var fx = RealFixture.Value;

        // 1a. 映射与血量阶梯：targetNewLevel=2 -> DB Id=1(+100HP)，targetNewLevel=10 -> DB Id=9(+600HP)，targetNewLevel=11 -> DB Id=10(+700HP)，Lv12..30+ -> +800HP
        Assert.Equal(1, fx.LevelUps.MapTargetLevelToDbId(2));
        Assert.Equal(9, fx.LevelUps.MapTargetLevelToDbId(10));
        Assert.Equal(30, fx.LevelUps.MapTargetLevelToDbId(31));

        int[] expectedHpByTargetLevel =
        [
            0,   // 0
            100, // 1 -> clamp dbId 1
            100, // 2 (Lv1->Lv2, Id=1)
            150, // 3 (Lv2->Lv3, Id=2)
            200, // 4 (Lv3->Lv4, Id=3)
            250, // 5 (Lv4->Lv5, Id=4)
            300, // 6 (Lv5->Lv6, Id=5)
            350, // 7 (Lv6->Lv7, Id=6)
            450, // 8 (Lv7->Lv8, Id=7)
            500, // 9 (Lv8->Lv9, Id=8)
            600, // 10 (Lv9->Lv10, Id=9)
            700, // 11 (Lv10->Lv11, Id=10)
            800, // 12 (Lv11->Lv12, Id=11)
        ];
        for (int lvl = 0; lvl < expectedHpByTargetLevel.Length; lvl++)
        {
            Assert.Equal(expectedHpByTargetLevel[lvl], fx.LevelUps.HealthIncreaseFor(lvl));
        }
        for (int lvl = 13; lvl <= 35; lvl++)
        {
            Assert.Equal(800, fx.LevelUps.HealthIncreaseFor(lvl));
        }

        // 1b. Lv1->Lv2 (targetNewLevel = 2) 读取 DB Id=1：瓦内莎标准状态应产出 Shiny!(e286d583) + Pip(e2fc8bd4)，绝不能出现 Get Bronze item 或随机垃圾青铜技能
        var vanessaLv2 = new PlayerState("p", "Vanessa", "Vanessa", 350, 20, 10, 10) { Level = 2, Day = 1 };
        var ctxLv2 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, vanessaLv2, vanessaLv2, new Random(101));
        var lv2Rewards = fx.LevelUps.RollRewards(2, ctxLv2, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.Equal(2, lv2Rewards.Count);
        Assert.Contains(lv2Rewards, id => id.StartsWith("e286d583", StringComparison.OrdinalIgnoreCase)); // Shiny!
        Assert.Contains(lv2Rewards, id => id.StartsWith("e2fc8bd4", StringComparison.OrdinalIgnoreCase)); // Pip
        Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(2, ctxLv2));

        // DealLevelUpChoices(2, ...) 同等只发出 Shiny! 与 Pip，不会硬塞多余垃圾技能
        var runLv2 = new RunState { RunId = "v10_lv2_deal", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 1, RngSeed = 102 };
        runLv2.PlayerAttributes[EPlayerAttributeType.Level] = 2;
        var shopLv2 = new ShopEngine(runLv2, fx.Pool, fx.Repo);
        shopLv2.DealLevelUpChoices(2, fx.LevelUps, vanessaLv2);
        Assert.Equal(2, runLv2.SelectionSet.Count);
        var lv2DealtTpls = runLv2.SelectionSet.Select(id => runLv2.Cards[id].TemplateId.ToString()).ToList();
        Assert.Contains(lv2DealtTpls, id => id.StartsWith("e286d583", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lv2DealtTpls, id => id.StartsWith("e2fc8bd4", StringComparison.OrdinalIgnoreCase));

        // 1c. Lv2->Lv3 (targetNewLevel = 3) 读取 DB Id=2：
        //     - 瓦内莎获得 Get Bronze item + Get Bronze Loot + 2 项瓦内莎合法分支（共 4 项）
        //     - Dooley 获得 Core Initialization (1596d1df)
        //     - Jules 获得 Chef Brolin (7a4f9be7)
        //     - Hero8 获得 Add a Random Note
        var vanessaLv3 = new PlayerState("p", "Vanessa", "Vanessa", 500, 20, 10, 10) { Level = 3, Day = 2 };
        var ctxVanLv3 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, vanessaLv3, vanessaLv3, new Random(201));
        Assert.True(fx.LevelUps.IsStandardMultiChoiceLevel(3, ctxVanLv3));
        var vanLv3Rewards = fx.LevelUps.RollRewards(3, ctxVanLv3, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.Equal(4, vanLv3Rewards.Count);
        Assert.Contains(vanLv3Rewards, id => id.StartsWith("1453be13", StringComparison.OrdinalIgnoreCase)); // Get Bronze item
        Assert.Contains(vanLv3Rewards, id => id.StartsWith("146907bc", StringComparison.OrdinalIgnoreCase)); // Get Bronze Loot

        var dooleyLv3 = new PlayerState("p", "Dooley", "Dooley", 500, 20, 10, 10) { Level = 3, Day = 2 };
        var ctxDooleyLv3 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, dooleyLv3, dooleyLv3, new Random(202));
        Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(3, ctxDooleyLv3));
        var dooleyLv3Rewards = fx.LevelUps.RollRewards(3, ctxDooleyLv3, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.Contains(dooleyLv3Rewards, id => id.StartsWith("1596d1df", StringComparison.OrdinalIgnoreCase)); // Core Initialization

        var julesLv3 = new PlayerState("p", "Jules", "Jules", 500, 20, 10, 10) { Level = 3, Day = 2 };
        var ctxJulesLv3 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, julesLv3, julesLv3, new Random(203));
        Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(3, ctxJulesLv3));
        var julesLv3Rewards = fx.LevelUps.RollRewards(3, ctxJulesLv3, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.Contains(julesLv3Rewards, id => id.StartsWith("7a4f9be7", StringComparison.OrdinalIgnoreCase)); // Chef Brolin

        var hero8Lv3 = new PlayerState("p", "Hero8", "Hero8", 500, 20, 10, 10) { Level = 3, Day = 2 };
        var ctxHero8Lv3 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, hero8Lv3, hero8Lv3, new Random(204));
        Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(3, ctxHero8Lv3));
        var hero8Lv3Rewards = fx.LevelUps.RollRewards(3, ctxHero8Lv3, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
        Assert.NotEmpty(hero8Lv3Rewards);
        Assert.Contains(hero8Lv3Rewards, id => fx.Repo.GetCard(id)?.InternalName.Contains("Add a Random Note", StringComparison.OrdinalIgnoreCase) == true);

        // 1d. Lv9->Lv10 (targetNewLevel = 10 -> DB Id=9) 与 Lv18->Lv19 (targetNewLevel = 19 -> DB Id=18)：
        //     随机附魔祭坛（2 选 1），修复 Group.Limit==null 截断后，2 个槽位应都能独立抽中主权重组（W=199 钻石附魔台），
        //     绝大多数次数出现“两张皆为钻石附魔祭坛”，绝不会每次第 2 槽必出传说祭坛！
        string[] legendaryPedestals = ["04f879e5", "8dead654", "f1878b7e"];
        foreach (int targetLvl in new[] { 10, 19 })
        {
            int bothDiamondCount = 0;
            for (int trial = 0; trial < 40; trial++)
            {
                var p = new PlayerState("p", "Vanessa", "Vanessa", 2000, 20, 10, 10) { Level = targetLvl, Day = 8 };
                var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, p, p, new Random(9000 + targetLvl * 100 + trial));
                Assert.False(fx.LevelUps.IsStandardMultiChoiceLevel(targetLvl, ctx));
                var picks = fx.LevelUps.RollRewards(targetLvl, ctx, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
                Assert.Equal(2, picks.Count);
                Assert.NotEqual(picks[0], picks[1]);
                Assert.All(picks, id => Assert.Equal("PedestalEncounter", fx.Repo.GetCard(id)?.Type));
                if (picks.All(id => !legendaryPedestals.Any(leg => id.StartsWith(leg, StringComparison.OrdinalIgnoreCase))))
                    bothDiamondCount++;
            }
            Assert.True(bothDiamondCount >= 32, $"targetNewLevel={targetLvl} 修复后 40 次抽样应绝大多数（>=32次）为双钻石附魔台，实际={bothDiamondCount}");
        }

        // 1e. Lv10->Lv11 (targetNewLevel = 11 -> DB Id=10)：瓦内莎必须只看到自身合法技能导师（包括 Old Zane 845d2051 候选，不含他英雄专属 Mr. Tuskari 等）
        var vanessaLv11 = new PlayerState("p", "Vanessa", "Vanessa", 2600, 20, 10, 10) { Level = 11, Day = 9 };
        var forbiddenOtherHeroMentors = new[] { "35e74c18", "77cd5ccd", "054e937d", "3e0f0266", "386dd351", "de175e34", "c4e01347" };
        for (int trial = 0; trial < 20; trial++)
        {
            var ctx11 = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, vanessaLv11, vanessaLv11, new Random(1100 + trial));
            var picks11 = fx.LevelUps.RollRewards(11, ctx11, id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
            Assert.InRange(picks11.Count, 3, 4);
            Assert.DoesNotContain(picks11, id => forbiddenOtherHeroMentors.Any(f => id.StartsWith(f, StringComparison.OrdinalIgnoreCase)));
        }
    }

    [Fact]
    public void Test01b_RunEngine_EndToEndDay1Level1To2_Adds100HpAndDealsShinyAndPip()
    {
        var fx = RealFixture.Value;
        using var ov = new OverlayContext(new[]
        {
            ("a0000001-0001-4001-8001-000000000001", BuildZeroXpFreeEventJson("a0000001-0001-4001-8001-000000000001", "Zero XP Event")),
            ("a0000001-0001-4001-8001-000000000002", BuildWeaponJson("a0000001-0001-4001-8001-000000000002", "V10 Strong Sword", dmg: 80)),
        });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 303);
        int baseHp = run.PlayerAttributes[EPlayerAttributeType.HealthMax];

        var wpnTpl = ov.Repo.GetCard("a0000001-0001-4001-8001-000000000002")!;
        var wpnCore = new CardInstance(wpnTpl, CardTier.Bronze, null, "v10_hero_wpn");
        Assert.True(run.HandGrid.TryPlaceOrShift(wpnCore, 3));
        run.Cards[wpnCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = wpnCore.InstanceId,
            TemplateId = Guid.Parse(wpnTpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = wpnCore,
        };

        // Day 1 Hour 1..4 推进至 Hour 5 时因每小时固定 +1 XP（#4）已达 9 XP，触发 Lv1 -> Lv2（+100 HP，弹出 Shiny! 与 Pip）
        var pol1 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol1.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        var zeroEv = InjectChoiceEncounter(run, ov.Pool, "a0000001-0001-4001-8001-000000000001");
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(zeroEv.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        var pol4 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol4.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // 验证真实 Lv1 -> Lv2 升级（Hour 4 商店退出攒满 8 XP 立刻进入 LevelUp）：血量精确 +100（而非旧错位 +150），弹窗选项为 Shiny! 与 Pip
        Assert.Equal(4u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(baseHp + 100, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(2, run.SelectionSet.Count);
        var lvlChoiceTpls = run.SelectionSet.Select(id => run.Cards[id].TemplateId.ToString()).ToList();
        Assert.Contains(lvlChoiceTpls, id => id.StartsWith("e286d583", StringComparison.OrdinalIgnoreCase)); // Shiny!
        Assert.Contains(lvlChoiceTpls, id => id.StartsWith("e2fc8bd4", StringComparison.OrdinalIgnoreCase)); // Pip

        // 完成 Lv2 升级弹层后推进至 Hour 5 Choice，再打完 Hour 5，稳停 Hour 6 Choice 进入 Day 1 PvP
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(5u, run.Hour);

        var pol5 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol5.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);

        var pvpCard = run.Cards[Assert.Single(run.SelectionSet)];
        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(pvpOut.Error);
    }

    // ── 2. #5 & #18 全拿多选免费事件（Grab the Loot / Mystery Bundle）可拿满 3 件 vs 单选事件限拿 1 件 ──

    [Theory]
    [InlineData(JungleRuinsGrabLootStepId, 3)]
    [InlineData(GrabTheLootStepId, 5)]
    [InlineData(HaddyMysteryBundleStepId, 3)]
    public void Test02_MultiPickFreeLootEvents_AllowTakingAllItems_AndAutoCloseAfterLast(string lootStepId, int expectedDealtCount)
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 518);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["slots"] }).Error);

        run.Day = 4;
        run.State = ERunState.Encounter;
        run.CurrentEncounterId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = false,
            SelectionIsFree = true,
            CanExit = true,
        };

        var stepEntry = InjectChoiceEncounter(run, fx.Pool, lootStepId, ECardType.EncounterStep);
        var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepEntry.InstanceId) });
        Assert.Null(stepOut.Error);

        Assert.Equal(ERunState.Encounter, run.State);
        Assert.True(run.SelectionRules.SelectionIsFree);
        Assert.True(run.SelectionRules.CanSelectMultiple);
        Assert.Equal(expectedDealtCount, run.SelectionSet.Count);

        var dealtIds = run.SelectionSet.ToList();
        int initialOwned = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player);

        // 依次拿取每一件，直到最后一件拿完前都必须留在 Encounter 界面；拿满后自动结算退出回到 Choice
        for (int i = 0; i < dealtIds.Count; i++)
        {
            var pickOut = engine.Handle(new SelectItemCommand
            {
                InstanceId = new InstanceId(dealtIds[i]),
                Section = EInventorySection.Hand,
            });
            if (pickOut.Error != null)
            {
                pickOut = engine.Handle(new SelectItemCommand
                {
                    InstanceId = new InstanceId(dealtIds[i]),
                    Section = EInventorySection.Stash,
                });
            }
            Assert.Null(pickOut.Error);

            if (i < dealtIds.Count - 1)
            {
                Assert.Equal(ERunState.Encounter, run.State);
                Assert.Equal(dealtIds.Count - i - 1, run.SelectionSet.Count);
            }
            else
            {
                Assert.Equal(ERunState.Choice, run.State);
            }
        }

        int finalOwned = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player);
        Assert.Equal(initialOwned + expectedDealtCount, finalOwned);

        // 同类全拿事件中，拿满前若玩家主动点击退出，也应允许正常退场
        var (exitRun, exitEngine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 519);
        Assert.Null(exitEngine.Handle(new CheatCommand { Args = ["slots"] }).Error);
        exitRun.Day = 4;
        exitRun.State = ERunState.Encounter;
        exitRun.CurrentEncounterId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
        var step2 = InjectChoiceEncounter(exitRun, fx.Pool, lootStepId, ECardType.EncounterStep);
        Assert.Null(exitEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step2.InstanceId) }).Error);
        Assert.Equal(expectedDealtCount, exitRun.SelectionSet.Count);
        Assert.Null(exitEngine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(exitRun.SelectionSet[0]),
            Section = EInventorySection.Hand,
        }).Error);
        Assert.Equal(ERunState.Encounter, exitRun.State);
        Assert.Null(exitEngine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.Choice, exitRun.State);
    }

    [Theory]
    [InlineData(BurnTrainingStepId)]
    [InlineData(PoisonTrainingStepId)]
    public void Test02b_SinglePickFreeChoiceEvents_StillExitAfterFirstPick(string singlePickStepId)
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 520);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["slots"] }).Error);

        run.Day = 4;
        run.State = ERunState.Encounter;
        run.CurrentEncounterId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
        // 即使上一跳误留了 CanSelectMultiple = true，单选免费事件步仍必须重置为单选并拿 1 件即关
        run.SelectionRules = new TSelectionContextRules
        {
            CanSelectMultiple = true,
            SelectionIsFree = true,
            CanExit = true,
        };

        var stepEntry = InjectChoiceEncounter(run, fx.Pool, singlePickStepId, ECardType.EncounterStep);
        var stepOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepEntry.InstanceId) });
        Assert.Null(stepOut.Error);

        Assert.Equal(ERunState.Encounter, run.State);
        Assert.False(run.SelectionRules.CanSelectMultiple);
        Assert.True(run.SelectionSet.Count >= 2);

        string firstId = run.SelectionSet[0];
        string secondId = run.SelectionSet[1];
        Assert.Null(engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(firstId),
            Section = EInventorySection.Hand,
        }).Error);

        Assert.NotEqual(ERunState.Encounter, run.State);
        Assert.NotNull(engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(secondId),
            Section = EInventorySection.Hand,
        }).Error);
    }

    // ── 3. #7、#10、#14 官方 6 小时制与 Hour 3 左铜/中银/右金三难度野怪梯度 ──

    [Fact]
    public void Test03_OfficialSixHourSchedule_And_ProgressiveThreeTierMonsters()
    {
        var fx = RealFixture.Value;

        // 3a. Day 1..10 的 Hour 1, 2, 4, 5：严格 3 槽 = 1 商人 + 2 非战斗非商人遭遇（无野怪、无双商人）
        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak })
        {
            for (uint day = 1; day <= 10; day++)
            {
                var visitedToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (uint hour in new[] { 1u, 2u, 4u, 5u })
                {
                    var run = new RunState
                    {
                        RunId = $"v10_h_{hero}_{day}_{hour}",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = hero,
                        Day = day,
                        Hour = hour,
                        State = ERunState.Choice,
                        RngSeed = (int)(day * 100 + hour),
                    };

                    var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                    shop.DealHourEncounters(
                        new PlayerState("p", "P", hero.ToString(), 500, 20, 20, 10)
                        {
                            Day = (int)day,
                            Hour = (int)hour,
                        },
                        visitedToday,
                        rules: null);

                    Assert.Equal(3, run.SelectionSet.Count);
                    int merchantCount = 0;
                    foreach (string instId in run.SelectionSet)
                    {
                        var entry = run.Cards[instId];
                        Assert.NotEqual(ECardType.CombatEncounter, entry.Type);
                        Assert.NotEqual(ECardType.PvpEncounter, entry.Type);

                        string tplId = entry.TemplateId.ToString();
                        visitedToday.Add(tplId);
                        var tpl = fx.Pool.Get(tplId);
                        var core = fx.Repo.GetCard(tplId);
                        string name = tpl?.InternalName ?? core?.InternalName ?? "";
                        if (CardArchetypeIndex.IsMerchantEncounterTemplate(tplId, name, tpl, core))
                            merchantCount++;
                    }
                    Assert.Equal(1, merchantCount);
                }

                // Hour 6：专属 PvP 对战槽
                var h6Run = new RunState
                {
                    RunId = $"v10_h6_{hero}_{day}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = hero,
                    Day = day,
                    Hour = 6,
                    State = ERunState.Choice,
                    RngSeed = (int)(day * 100 + 6),
                };
                new ShopEngine(h6Run, fx.Pool, fx.Repo).DealHourEncounters();
                var pvpOnly = h6Run.Cards[Assert.Single(h6Run.SelectionSet)];
                Assert.Equal(ECardType.PvpEncounter, pvpOnly.Type);
            }
        }

        // 3b. 严禁测试怪与模板怪污染 Hour 3 野怪池
        string[] forbiddenNames =
        [
            "Training Dummy", "QA Test", "Sparring Partner", "Monster_Battle_Player_Template",
            "Skill Board Test", "Art Test", "Design Testing", "LOADOUT TEMPLATE",
            "Greenheart", "Test Subject Alpha", "Big Bad Wolf", "Tutorial",
        ];

        int GetHp(RunState run, string instId)
        {
            var tpl = fx.Pool.Get(run.Cards[instId].TemplateId.ToString())!;
            var mon = fx.Repo.MonstersById[tpl.MonsterTemplateId];
            return (int)Math.Round(mon.Player!.Attributes["HealthMax"]);
        }

        int GetLv(RunState run, string instId)
            => fx.Pool.Get(run.Cards[instId].TemplateId.ToString())!.MonsterLevel;

        void AssertNoForbiddenMonster(RunState run)
        {
            foreach (string id in run.SelectionSet)
            {
                var tpl = fx.Pool.Get(run.Cards[id].TemplateId.ToString())!;
                var mon = fx.Repo.MonstersById[tpl.MonsterTemplateId];
                int hp = GetHp(run, id);
                Assert.InRange(hp, 50, 9999);
                foreach (string bad in forbiddenNames)
                {
                    Assert.DoesNotContain(bad, tpl.InternalName, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(bad, mon.InternalName, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        // 3c. Day 1 Hour 3（30 次抽样）：左=Lv1(75~100HP)，中=Lv1~2(100~200HP)，右=Lv2(200~300HP)
        for (int i = 0; i < 30; i++)
        {
            var run = new RunState { RunId = $"v10_d1_h3_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 1, Hour = 3, RngSeed = 7000 + i };
            new ShopEngine(run, fx.Pool, fx.Repo).DealHourEncounters();
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.Equal(3, run.SelectionSet.Select(id => run.Cards[id].TemplateId).Distinct().Count());
            AssertNoForbiddenMonster(run);

            int leftLv = GetLv(run, run.SelectionSet[0]);
            int midLv = GetLv(run, run.SelectionSet[1]);
            int rightLv = GetLv(run, run.SelectionSet[2]);
            int leftHp = GetHp(run, run.SelectionSet[0]);
            int midHp = GetHp(run, run.SelectionSet[1]);
            int rightHp = GetHp(run, run.SelectionSet[2]);

            Assert.Equal(1, leftLv);
            Assert.InRange(leftHp, 75, 100);
            Assert.InRange(midLv, 1, 2);
            Assert.InRange(midHp, 100, 200);
            Assert.Equal(2, rightLv);
            Assert.InRange(rightHp, 200, 300);
            Assert.True(leftHp <= midHp && midHp <= rightHp && rightHp > leftHp, $"Day 1 血量梯度应递增: [{leftHp}, {midHp}, {rightHp}]");
        }

        // 3d. Day 2 Hour 3（30 次抽样）：左=Lv1~2(<=200HP)，中=Lv2~3(200~350HP)，右=Lv3~4(350~600HP)
        for (int i = 0; i < 30; i++)
        {
            var run = new RunState { RunId = $"v10_d2_h3_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 2, Hour = 3, RngSeed = 8000 + i };
            new ShopEngine(run, fx.Pool, fx.Repo).DealHourEncounters();
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.Equal(3, run.SelectionSet.Select(id => run.Cards[id].TemplateId).Distinct().Count());
            AssertNoForbiddenMonster(run);

            int leftLv = GetLv(run, run.SelectionSet[0]);
            int midLv = GetLv(run, run.SelectionSet[1]);
            int rightLv = GetLv(run, run.SelectionSet[2]);
            int leftHp = GetHp(run, run.SelectionSet[0]);
            int midHp = GetHp(run, run.SelectionSet[1]);
            int rightHp = GetHp(run, run.SelectionSet[2]);

            Assert.InRange(leftLv, 1, 2);
            Assert.True(leftHp <= 200, $"Day 2 左槽 HP={leftHp} 超出 <=200 上限");
            Assert.InRange(midLv, 2, 3);
            Assert.InRange(midHp, 200, 350);
            Assert.InRange(rightLv, 3, 4);
            Assert.InRange(rightHp, 350, 600);
            Assert.True(leftHp <= midHp && midHp <= rightHp && rightHp > leftHp, $"Day 2 血量梯度应递增: [{leftHp}, {midHp}, {rightHp}]");
        }

        // 3e. Day 3..15 Hour 3：左<=中<=右严格按天数递增（Day 13 右槽在无 Lv14 时顺延 Lv15），且跨天血量抬升
        double avgMidHpDay3 = 0, avgMidHpDay7 = 0, avgMidHpDay12 = 0;
        foreach (int day in new[] { 3, 5, 7, 10, 12, 13, 15 })
        {
            double midSum = 0;
            for (int i = 0; i < 15; i++)
            {
                var run = new RunState { RunId = $"v10_d{day}_h3_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = (uint)day, Hour = 3, RngSeed = 9000 + day * 50 + i };
                new ShopEngine(run, fx.Pool, fx.Repo).DealHourEncounters();
                Assert.Equal(3, run.SelectionSet.Count);
                Assert.Equal(3, run.SelectionSet.Select(id => run.Cards[id].TemplateId).Distinct().Count());
                AssertNoForbiddenMonster(run);

                int l = GetLv(run, run.SelectionSet[0]);
                int m = GetLv(run, run.SelectionSet[1]);
                int r = GetLv(run, run.SelectionSet[2]);
                Assert.True(l <= m && m <= r, $"Day {day} 野怪等级应递增: [{l}, {m}, {r}]");
                if (day <= 12)
                {
                    Assert.Equal(day - 1, l);
                    Assert.Equal(day, m);
                    Assert.Equal(day + 1, r);
                }
                midSum += GetHp(run, run.SelectionSet[1]);
            }
            if (day == 3) avgMidHpDay3 = midSum / 15.0;
            if (day == 7) avgMidHpDay7 = midSum / 15.0;
            if (day == 12) avgMidHpDay12 = midSum / 15.0;
        }
        Assert.True(avgMidHpDay3 < avgMidHpDay7 && avgMidHpDay7 < avgMidHpDay12,
            $"野怪中槽血量应随天数递增: D3={avgMidHpDay3:F0} < D7={avgMidHpDay7:F0} < D12={avgMidHpDay12:F0}");
    }

    // ── 4. #19 PvpGhostPool 全天数合理血量与等级上限过滤（保留 MaxBoardsPerDayBucket = 2500） ──

    [Fact]
    public void Test04_PvpGhostPool_IsSaneGhostForDay_RejectsDirtyDay4AndEnforcesAllDayCaps()
    {
        var fx = RealFixture.Value;
        Assert.Equal(2500, PvpGhostPool.MaxBoardsPerDayBucket);

        var cutlassItem = new PvpGhostPool.GhostItem
        {
            Slot = 3,
            Width = 2,
            TemplateId = CutlassId,
            InternalName = "Cutlass",
            Tier = "Bronze",
        };

        // 4a. Day 4 却 3065 血这种典型历史 Bug 脏镜像必须被精准拒收；正常 Day 4 镜像应通过
        var dirtyDay4 = new PvpGhostPool.GhostBoard
        {
            BoardId = "dirty_d4_3065",
            Day = 4,
            Hero = "Vanessa",
            Level = 6,
            HealthMax = 3065,
            Items = new List<PvpGhostPool.GhostItem> { cutlassItem },
        };
        Assert.False(PvpGhostPool.IsSaneGhostForDay(dirtyDay4, 4));

        var dirtyDay4HighHpOnly = new PvpGhostPool.GhostBoard
        {
            BoardId = "dirty_d4_hp_only",
            Day = 4,
            Hero = "Vanessa",
            Level = 4,
            HealthMax = 3065,
            Items = new List<PvpGhostPool.GhostItem> { cutlassItem },
        };
        Assert.False(PvpGhostPool.IsSaneGhostForDay(dirtyDay4HighHpOnly, 4));

        var dirtyDay1 = new PvpGhostPool.GhostBoard
        {
            BoardId = "dirty_d1_620",
            Day = 1,
            Hero = "Vanessa",
            Level = 2,
            HealthMax = 620,
            Items = new List<PvpGhostPool.GhostItem> { cutlassItem },
        };
        Assert.False(PvpGhostPool.IsSaneGhostForDay(dirtyDay1, 1));

        var saneDay4 = new PvpGhostPool.GhostBoard
        {
            BoardId = "sane_d4",
            Day = 4,
            Hero = "Vanessa",
            Level = 4,
            HealthMax = 800,
            Items = new List<PvpGhostPool.GhostItem> { cutlassItem },
        };
        Assert.True(PvpGhostPool.IsSaneGhostForDay(saneDay4, 4));

        // 4b. 实池 Day 1..15 抽样全部严格满足当日 IsSaneGhostForDay 上限
        var rng = new Random(2026);
        for (int day = 1; day <= 15; day++)
        {
            for (int i = 0; i < 20; i++)
            {
                var opp = fx.Ghosts.PickOpponent(day, playerWins: Math.Max(0, day - 2), playerLosses: 1, rng, fx.Repo);
                Assert.True(PvpGhostPool.IsSaneGhostForDay(opp, day),
                    $"Day {day} PickOpponent 不应返回越界镜像: {opp.BoardId} Lv{opp.Level} HP={opp.HealthMax}");
            }
        }

        // 4c. 隔离库：写入脏 Day 4(3065HP) 与正常 Day 4(800HP) 到 SQLite，验证 LoadFromDb 与 RecordPlayerGhost 及 PickOpponent 彻底拒收脏镜像
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_v10_ghost_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string jsonlPath = Path.Combine(tempDir, "ghost_pool_balanced.jsonl");
            File.WriteAllLines(jsonlPath, new[]
            {
                JsonSerializer.Serialize(new
                {
                    boardId = "dirty_jsonl_d4",
                    day = 4,
                    hero = "Vanessa",
                    level = 8,
                    healthMax = 3065,
                    prestige = 20,
                    wins = 3,
                    losses = 0,
                    unlockedSlotsMask = 120,
                    powerBucket = "Strong",
                    estimatedWinRate = 0.8,
                    items = new[] { new { slot = 3, width = 2, templateId = CutlassId, internalName = "Cutlass", zhName = "Cutlass", tier = "Bronze" } },
                    skills = Array.Empty<object>(),
                }),
            });

            var pool = PvpGhostPool.Load(tempDir);
            Assert.Equal(0, pool.Count);

            var db = new Persistence(Path.Combine(tempDir, "ghosts.db"));
            string dirtyPayload = JsonSerializer.Serialize(new
            {
                boardId = "dirty_sqlite_d4",
                powerBucket = "Strong",
                estimatedWinRate = 0.8,
                isCustomRule = false,
                items = new[] { new { slot = 3, width = 2, templateId = CutlassId, internalName = "Cutlass", zhName = "Cutlass", tier = "Bronze" } },
                skills = Array.Empty<object>(),
            });
            string cleanPayload = JsonSerializer.Serialize(new
            {
                boardId = "clean_sqlite_d4",
                powerBucket = "Normal",
                estimatedWinRate = 0.5,
                isCustomRule = false,
                items = new[] { new { slot = 3, width = 2, templateId = CutlassId, internalName = "Cutlass", zhName = "Cutlass", tier = "Bronze" } },
                skills = Array.Empty<object>(),
            });

            db.InsertPvpGhost(new Persistence.PvpGhostRow(0, 4, "Vanessa", 2, 1, 3065, 20, 5, 120, dirtyPayload, DateTime.UtcNow, false));
            db.InsertPvpGhost(new Persistence.PvpGhostRow(0, 4, "Vanessa", 2, 1, 800, 20, 4, 120, cleanPayload, DateTime.UtcNow, false));

            int loaded = pool.LoadFromDb(db);
            Assert.Equal(1, loaded);

            for (int i = 0; i < 15; i++)
            {
                var picked = pool.PickOpponent(4, 2, 1, rng, fx.Repo);
                Assert.True(PvpGhostPool.IsSaneGhostForDay(picked, 4));
                Assert.NotEqual("dirty_sqlite_d4", picked.BoardId);
                Assert.NotEqual(3065, picked.HealthMax);
            }

            // RecordPlayerGhost 对异常 Day 4 却 3065HP 局也应拒绝入库
            var cheatRun = new RunState
            {
                RunId = "cheat_d4_run",
                Hero = EHero.Vanessa,
                Day = 4,
                Hour = 6,
                UnlockedSlots = 120,
            };
            cheatRun.PlayerAttributes[EPlayerAttributeType.HealthMax] = 3065;
            cheatRun.PlayerAttributes[EPlayerAttributeType.Level] = 4;
            var cutlassTpl = fx.Repo.GetCard(CutlassId)!;
            var inst = new CardInstance(cutlassTpl, CardTier.Bronze, null, "itm_c1") { StartSlot = 3 };
            cheatRun.Cards[inst.InstanceId] = new RunState.CardEntry
            {
                InstanceId = inst.InstanceId,
                TemplateId = Guid.Parse(CutlassId),
                Type = ECardType.Item,
                Size = ECardSize.Medium,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Hand,
                Socket = EContainerSocketId.Socket_3,
                CoreCard = inst,
            };
            Assert.Null(pool.RecordPlayerGhost(cheatRun, db));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
        }
    }
}

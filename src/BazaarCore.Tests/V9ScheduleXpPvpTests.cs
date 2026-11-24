using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V9 Stage B2 综合回归测试：
/// 覆盖 StartingExperience=0 与经验单算闸门（#10/#26/#29）、Lv10/Lv12 升级奖励与移除垃圾技能兜底（#23/#24/#31）、
/// 每小时 3 槽布局 Merchant&lt;=1 与废卡屏蔽（#11/#17）、货架 &lt;=10 格/三战利品足额/0 元过滤/Unsellable/WillAutoSellOnExit（#4/#7/#18/#19b/#32a）、
/// 分天野怪与 Day 1 福利局（#5/#17/#30）、以及 Hour 6 PvP 一致化绑定与镜像清洗（#8/#9/#20/#25/#35）。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V9ScheduleXpPvpTests
{
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string RedGumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
    private const string StudyDefaultEventId = "ce38f4a2-f156-4082-b8ce-e8956c71fc0d";
    private const string AldricEventId = "78cf8b25-5812-4b9a-a9ff-9694c86e7a90";
    private const string ExploitAldricStepId = "29bb1d38-882b-48bb-be8a-d69dca75fd3f";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string JungleRuinsGrabLootStepId = "e73e7956-1875-4f40-b5f2-d3c4ed975751";
    private const string HaddyMysteryBundleStepId = "ed15d9f2-b77e-4038-a540-84f381e0cb08";
    private const string FaraiEventId = "412fb3ec-88de-40b4-a475-10e583189d8a";
    private const string TinkersPackageId = "00c67a4d-eea4-4297-b797-3071893c2a1c";
    private const string DepletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";

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
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new Fx
        {
            DataDir = shared.DataDir,
            DbPath = shared.DbPath,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Repo = shared.Repo,
            Ghosts = shared.Ghosts,
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
        int seed = 101)
    {
        var run = new RunState
        {
            RunId = "v9_b2_" + Guid.NewGuid().ToString("N")[..8],
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

    // ── 1. 开局 StartingExperience=3 / StartingHealth=300、残缺 JSON 防御及默认规则保留 ──

    [Fact]
    public void Test01_StartingExperienceZero_CustomOverridePreserved_AndDbAuthorityCheck()
    {
        var fx = RealFixture.Value;
        Assert.Equal(3, fx.Mode.StartingExperience);
        Assert.Equal(3, new GameModeConfig().StartingExperience);
        Assert.Equal(3, new GameModeData().StartingExperience);
        Assert.Equal(300, fx.Mode.StartingHealth);

        var (stdRun, _) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        Assert.Equal(3, stdRun.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(300, stdRun.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(1, stdRun.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.False(stdRun.GainedHourXpThisHour);
        Assert.False(stdRun.IsCustomRuleRun);

        // 自定义 StartingExperience 覆盖能力完整保留
        var customRules = new CustomRunRuleSet { StartingExperience = 5 };
        var (customRun, _) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, rules: customRules);
        Assert.Equal(5, customRun.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(1, customRun.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.True(customRun.IsCustomRuleRun);

        // targetNewLevel=2 -> DB Id=1(+100HP)，targetNewLevel=10 -> DB Id=9(+600HP)，targetNewLevel=11 -> DB Id=10(+700HP)，Lv12..35 恒为 +800HP
        Assert.Equal(100, fx.LevelUps.HealthIncreaseFor(2));
        Assert.Equal(600, fx.LevelUps.HealthIncreaseFor(10));
        Assert.Equal(700, fx.LevelUps.HealthIncreaseFor(11));
        for (int lvl = 12; lvl <= 35; lvl++)
            Assert.Equal(800, fx.LevelUps.HealthIncreaseFor(lvl));
        var emptyLevelUps = new LevelUpTable();
        Assert.Equal(100, emptyLevelUps.HealthIncreaseFor(2));
        Assert.Equal(600, emptyLevelUps.HealthIncreaseFor(10));
        Assert.Equal(700, emptyLevelUps.HealthIncreaseFor(11));
        Assert.Equal(800, emptyLevelUps.HealthIncreaseFor(24));

        // 即使被传入 dist/gamemodes.json 的错误 Legendary=64，也能校准为 DB 权威值 24/48
        var corruptedPrices = new StandardPricesSettings
        {
            ItemBuyPrices = new Dictionary<string, Dictionary<string, int>>
            {
                ["Small"] = new() { ["Legendary"] = 64 },
                ["Medium"] = new() { ["Legendary"] = 64 },
                ["Large"] = new() { ["Legendary"] = 64 },
            },
            ItemSellPrices = new Dictionary<string, Dictionary<string, int>>
            {
                ["Small"] = new() { ["Legendary"] = 32 },
                ["Medium"] = new() { ["Legendary"] = 32 },
                ["Large"] = new() { ["Legendary"] = 32 },
            },
        };
        Assert.Equal(24, corruptedPrices.GetItemBuyPrice(CardSize.Small, CardTier.Legendary));
        Assert.Equal(48, corruptedPrices.GetItemBuyPrice(CardSize.Medium, CardTier.Legendary));
        Assert.Equal(64, corruptedPrices.GetItemBuyPrice(CardSize.Large, CardTier.Legendary));
        Assert.Equal(12, corruptedPrices.GetItemSellPrice(CardSize.Small, CardTier.Legendary));
        Assert.Equal(24, corruptedPrices.GetItemSellPrice(CardSize.Medium, CardTier.Legendary));
        Assert.Equal(32, corruptedPrices.GetItemSellPrice(CardSize.Large, CardTier.Legendary));
    }

    // ── 2. 根治经验双算、Study/Aldric 去重与 Day 1 Lv1 -> Hour 6 PvP 升 Lv2 ──

    [Fact]
    public void Test02_NoDoubleXpOnShopOrSteps_And_Day1FullRunReachesLevel2AfterHour6Pvp()
    {
        var fx = RealFixture.Value;

        // 2a. 完整 Day 1 体验：0 XP 起步 -> H1(商店+1) -> H2(无经验事件+1) -> H3(Lv1野怪+3) -> H4(商店+1) -> H5(商店+1) -> H6 PvP前 7/8 Lv1 -> H6 PvP(+3) -> Lv2 2/8 XP
        using var ov = new OverlayContext(new[]
        {
            ("11111111-9001-4001-8001-111111111101", BuildZeroXpFreeEventJson("11111111-9001-4001-8001-111111111101", "Zero XP Event")),
            ("11111111-9001-4001-8001-111111111102", BuildWeaponJson("11111111-9001-4001-8001-111111111102", "Strong Starter Sword", dmg: 80)),
        });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 202);
        // 放入一把足够击败 Day 1 Lv1 野怪的强力武器
        var weaponTpl = ov.Repo.GetCard("11111111-9001-4001-8001-111111111102")!;
        var weaponCore = new CardInstance(weaponTpl, CardTier.Bronze, null, "hero_wpn");
        Assert.True(run.HandGrid.TryPlaceOrShift(weaponCore, 3));
        run.Cards[weaponCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = weaponCore.InstanceId,
            TemplateId = Guid.Parse(weaponTpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = weaponCore,
        };

        // P0-1 官方单份经验规则（Patch 20.6 初始 3 XP / 300 HP：3 -> H1(+1)=4 -> H2(+1)=5 -> H3野怪(+3)=8 升 Lv2 0/8 400HP -> H4(+1)=1 -> H5(+1)=2 -> H6 PvP 跨天(+1) -> Lv2 3/8 XP）
        // Hour 1：进入 Pol（ExperienceAwardUponSelection = 1）并直接离开 → 仅 +1 XP（3+1=4，绝不双算为 5）
        var pol1 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol1.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(4, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.False(run.GainedHourXpThisHour);
        Assert.False(run.GainedAdvanceHourXpThisHour);

        // Hour 2：进入 0 XP 免费事件并直接离开 → 由 AdvanceHourOrDay 兜底发放 1 点小时推进经验 → 累计 5 XP
        var zeroEv = InjectChoiceEncounter(run, ov.Pool, "11111111-9001-4001-8001-111111111101");
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(zeroEv.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(3u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 3：选打真实 Day 1 Lv1 野怪 Fanged Inglet（RewardCombatXp = 3）→ 5+3=8 触发 Lv2 升级（300 -> 400 HP），完成后推进至 Hour 4（战斗小时不再额外叠 +1）
        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var pveOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(pveOut.Error);
        Assert.Equal(ERunState.Loot, run.State);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        while (run.State is ERunState.LevelUp or ERunState.Encounter)
        {
            if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
            {
                var pick = run.Cards[run.SelectionSet[0]];
                bool isEnc = pick.Type is ECardType.EncounterStep or ECardType.EventEncounter
                    or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter;
                INetCommand cmd = isEnc
                    ? new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) }
                    : new SelectItemCommand { InstanceId = new InstanceId(pick.InstanceId) };
                Assert.Null(engine.Handle(cmd).Error);
            }
            else break;
        }
        Assert.Equal(4u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 4：进入 Pol (+1 XP) 并离开 → Lv2 1/8 XP
        var pol4 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol4.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(5u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 5：进入 Pol (+1 XP) 并离开 → Hour 6 PvP 前已稳居 Lv2（2/8 XP, 400+ HP，对齐 Patch 20.6 官服实机！）
        var pol5 = InjectChoiceEncounter(run, ov.Pool, PolShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pol5.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // Hour 6 PvP 战斗本身 0 XP，跨入 Day 2 Hour 1 发放 +1 小时经验 → Lv2 3/8 XP
        var pvpCard = run.Cards[Assert.Single(run.SelectionSet)];
        Assert.Equal(ECardType.PvpEncounter, pvpCard.Type);
        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(pvpOut.Error);
        while (run.State is ERunState.LevelUp or ERunState.Encounter)
        {
            if (run.State == ERunState.Encounter && run.SelectionRules.CanExit)
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
            else if (run.SelectionSet.Count > 0)
            {
                var pick = run.Cards[run.SelectionSet[0]];
                bool isEnc = pick.Type is ECardType.EncounterStep or ECardType.EventEncounter
                    or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter;
                INetCommand cmd = isEnc
                    ? new SelectEncounterCommand { InstanceId = new InstanceId(pick.InstanceId) }
                    : new SelectItemCommand { InstanceId = new InstanceId(pick.InstanceId) };
                Assert.Null(engine.Handle(cmd).Error);
            }
            else break;
        }
        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(3, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // 2b. P0-1 / P0-16 单份经验与直效规范落地校验（Wave 4A #1 裸端防软锁）：Study (xp=2, 无 SpawnContext) 选中先落地 Encounter(CanExit=true)，退出后推进至 Hour==2 Choice 且 XP==3+2=5；Aldric (xp=1) + Exploit (xp=1 同小时去重) = 3+1=4
        var (studyRun, studyEngine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        var studyOpt = InjectChoiceEncounter(studyRun, fx.Pool, StudyDefaultEventId);
        Assert.Null(studyEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(studyOpt.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, studyRun.State);
        Assert.True(studyRun.SelectionRules.CanExit);
        Assert.Null(studyEngine.Handle(new ExitCurrentStateCommand()).Error);
        Assert.Equal(ERunState.Choice, studyRun.State);
        Assert.Equal(2u, studyRun.Hour);
        Assert.Equal(5, studyRun.PlayerAttributes[EPlayerAttributeType.Experience]);

        var (aldricRun, aldricEngine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        var aldricOpt = InjectChoiceEncounter(aldricRun, fx.Pool, AldricEventId);
        Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aldricOpt.InstanceId) }).Error);
        var exploitStep = aldricRun.SelectionSet
            .Select(id => aldricRun.Cards[id])
            .FirstOrDefault(c => c.TemplateId == Guid.Parse(ExploitAldricStepId));
        if (exploitStep != null)
        {
            Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(exploitStep.InstanceId) }).Error);
            int guard = 0;
            while (aldricRun.State == ERunState.Encounter && guard++ < 5)
            {
                if (aldricRun.SelectionRules.CanExit)
                    Assert.Null(aldricEngine.Handle(new ExitCurrentStateCommand()).Error);
                else if (aldricRun.SelectionSet.Count > 0)
                    Assert.Null(aldricEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(aldricRun.SelectionSet[0]) }).Error);
                else
                    break;
            }
            Assert.Equal(4, aldricRun.PlayerAttributes[EPlayerAttributeType.Experience]);
        }

        // 2c. 快照序列化往返保留 GainedHourXpThisHour
        aldricRun.GainedHourXpThisHour = true;
        string snapJson = RunSnapshotSerializer.Serialize(aldricRun);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snapJson, fx.Repo, out var restored, out _));
        Assert.True(restored!.GainedHourXpThisHour);
    }

    // ── 3. 修复 Lv10 / Lv12 升级奖励，移除垃圾青铜技能兜底 ──

    [Fact]
    public void Test03_DealLevelUpChoices_Lv10AndLv12MatchOfficialPool_WithoutBronzeSkillPadding()
    {
        var fx = RealFixture.Value;
        var bronzeSkillIds = fx.Pool.OfType("Skill")
            .Where(s => string.Equals(s.StartingTier, "Bronze", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 预先提取官方 Lv10 / Lv12 level_ups 允许的全部候选 ID 集合
        HashSet<string> CollectOfficialLevelRewardIds(int targetLevel)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var conn = new SqliteConnection($"Data Source={fx.DbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Data FROM level_ups WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", targetLevel);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                using var doc = JsonDocument.Parse((byte[])r.GetValue(0));
                if (doc.RootElement.TryGetProperty("Rewards", out var rewards) &&
                    rewards.TryGetProperty("Groups", out var groups) &&
                    groups.ValueKind == JsonValueKind.Array)
                {
                    foreach (var grp in groups.EnumerateArray())
                    {
                        if (!grp.TryGetProperty("Filters", out var filters) || filters.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (var f in filters.EnumerateArray())
                        {
                            if (f.TryGetProperty("Ids", out var idArr) && idArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var el in idArr.EnumerateArray())
                                {
                                    string? s = el.GetString();
                                    if (!string.IsNullOrWhiteSpace(s)) ids.Add(s);
                                }
                            }
                        }
                    }
                }
            }
            return ids;
        }

        var officialLv10Ids = CollectOfficialLevelRewardIds(10);
        var officialLv12Ids = CollectOfficialLevelRewardIds(12);

        // V10 targetNewLevel->DB Id=targetNewLevel-1 契约：
        // targetNewLevel=10(DB Id=9) / 19(DB Id=18) 为附魔祭坛（1..2 项），11(DB Id=10) 为 3 项，13(DB Id=12) 为 2 项
        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak })
        {
            foreach (int lvl in new[] { 10, 11, 13, 19 })
            {
                var run = new RunState
                {
                    RunId = $"v9_lv_{hero}_{lvl}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = hero,
                    Day = (uint)lvl,
                };
                var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                var player = new PlayerState("p", "P", hero.ToString(), 1500, 20, 20, 10) { Level = lvl, Day = lvl };
                shop.DealLevelUpChoices(lvl, fx.LevelUps, player);

                Assert.Equal(ERunState.LevelUp, run.State);
                Assert.NotEmpty(run.SelectionSet);
                if (lvl is 10 or 19)
                {
                    Assert.InRange(run.SelectionSet.Count, 1, 2);
                }
                else if (lvl == 11)
                {
                    // 空棋盘无 Core 且无 Rage/Heated/Chilled 时，官方 DB Id=10 (Lv10->Lv11) 仅 3 组无前置条件（G1/G2 + 本英雄导师），绝不塞青铜技能凑数
                    Assert.Equal(3, run.SelectionSet.Count);
                    Assert.All(run.SelectionSet, id =>
                    {
                        string tplId = run.Cards[id].TemplateId.ToString();
                        Assert.Contains(tplId, officialLv10Ids);
                        Assert.DoesNotContain(tplId, bronzeSkillIds);
                    });
                }
                else if (lvl == 13)
                {
                    // 空棋盘时官方 DB Id=12 (Lv12->Lv13) 仅 2 组无前置条件（Forja + Orlin），绝不塞青铜技能凑 3~4 张
                    Assert.Equal(2, run.SelectionSet.Count);
                    Assert.All(run.SelectionSet, id =>
                    {
                        string tplId = run.Cards[id].TemplateId.ToString();
                        Assert.Contains(tplId, officialLv12Ids);
                        Assert.DoesNotContain(tplId, bronzeSkillIds);
                    });
                }
            }
        }

        // 额外验证：当棋盘拥有 Rage 物品时，targetNewLevel=11 (DB Id=10) / 13 (DB Id=12) 满足 Inspired 条件组后正常升至 4 项 / 3 项，且依然不含任何青铜垃圾技能
        const string rageItemId = "02859d88-e334-45a9-8483-597d744cb7c7";
        var rageTpl = fx.Repo.GetCard(rageItemId);
        if (rageTpl != null)
        {
            foreach (int lvl in new[] { 11, 13 })
            {
                var condRun = new RunState
                {
                    RunId = $"v9_lv_cond_{lvl}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    Day = (uint)lvl,
                };
                var condShop = new ShopEngine(condRun, fx.Pool, fx.Repo);
                var condPlayer = new PlayerState("p", "P", "Vanessa", 1500, 20, 20, 10) { Level = lvl, Day = lvl };
                var rageInst = new CardInstance(rageTpl, CardTier.Bronze, null, "itm_rage_cond");
                Assert.True(condPlayer.Hand.TryPlaceOrShift(rageInst, 0));

                condShop.DealLevelUpChoices(lvl, fx.LevelUps, condPlayer);
                Assert.Equal(lvl == 11 ? 4 : 3, condRun.SelectionSet.Count);
                var targetOfficialIds = lvl == 11 ? officialLv10Ids : officialLv12Ids;
                Assert.All(condRun.SelectionSet, id =>
                {
                    string tplId = condRun.Cards[id].TemplateId.ToString();
                    Assert.Contains(tplId, targetOfficialIds);
                    Assert.DoesNotContain(tplId, bronzeSkillIds);
                });
            }
        }
    }

    // ── 4. 每小时 3 槽布局 Merchant<=1 与测试/剧情废卡全局屏蔽 ──

    [Fact]
    public void Test04_HourlyEncounters_AtMostOneMerchant_And_ExcludesAllDevStoryTemplates()
    {
        var fx = RealFixture.Value;
        // D2-P1-11（#84 授权适配）：保留真实废卡黑名单，解除 3 张官方活跃卡与 1 只野怪的误封
        string[] bannedIds =
        [
            "1400ea5b-6492-4c41-9bf9-c7016973ee0a", // Greenheart Help Jules Ambush Giant Mosquito
            "c20ccde2-99be-462c-9b32-8a340ee8b017", // Greenheart Help Merchant Help Kyver Drone
            "85a40ccc-9683-4794-8641-65940237da3a", // [Jungle Expedition] Greenheart Guardian Exit
            "62d59e46-31cc-411d-af27-0f3bf82ca57b", // Big Bad Wolf
        ];

        // 全局静态池校验
        foreach (string badId in bannedIds)
        {
            Assert.False(fx.Pool.IsPlayerSpawnableId(badId, allowGuidOnly: true), $"废卡 {badId} 不得通过 IsPlayerSpawnable");
            Assert.False(fx.Pool.IsPlayerSpawnableId(badId, allowGuidOnly: false));
        }

        // D2-P1-11 正面断言：官方活跃卡必须可生成
        Assert.True(fx.Pool.IsPlayerSpawnableId("071cf90c-57a4-40f2-8d79-4c87f0d100d4", allowGuidOnly: false)); // Test Subject Alpha
        Assert.True(fx.Pool.IsPlayerSpawnableId("41cdf4ee-7790-4da1-96f8-a46d3391a0d9", allowGuidOnly: false)); // The Tester
        Assert.True(fx.Pool.IsPlayerSpawnableId("47704642-ae7a-4775-a26a-e5e5a8cff7bf", allowGuidOnly: false)); // Eating Contest

        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak })
        {
            Assert.DoesNotContain(fx.Pool.ShopItems(hero.ToString()), c => CardPool.IsExcludedDevOrStoryTemplate(c));
            Assert.DoesNotContain(fx.Pool.HourlyEncounters(hero.ToString(), "EventEncounter"), c => CardPool.IsExcludedDevOrStoryTemplate(c));
        }

        // Day 1..10 × Hour 1,2,4,5：每次 3 槽，且商人数量严格 <= 1
        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley })
        {
            for (uint day = 1; day <= 10; day++)
            {
                foreach (uint hour in new[] { 1u, 2u, 4u, 5u })
                {
                    var run = new RunState
                    {
                        RunId = $"v9_h_{hero}_{day}_{hour}",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = hero,
                        Day = day,
                        Hour = hour,
                        State = ERunState.Choice,
                    };
                    var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                    shop.DealHourEncounters(new PlayerState("p", "P", hero.ToString(), 500, 20, 20, 10)
                    {
                        Day = (int)day,
                        Hour = (int)hour,
                    });

                    Assert.Equal(3, run.SelectionSet.Count);
                    int merchantCount = 0;
                    foreach (string instId in run.SelectionSet)
                    {
                        var entry = run.Cards[instId];
                        string tplId = entry.TemplateId.ToString();
                        Assert.DoesNotContain(tplId, bannedIds, StringComparer.OrdinalIgnoreCase);

                        var tpl = fx.Pool.Get(tplId);
                        var core = fx.Repo.GetCard(tplId);
                        string name = tpl?.InternalName ?? core?.InternalName ?? "";
                        Assert.DoesNotContain("Tutorial", name, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("[DEBUG]", name, StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("Greenheart", name, StringComparison.OrdinalIgnoreCase);

                        if (CardArchetypeIndex.IsMerchantEncounterTemplate(tplId, name, tpl, core))
                            merchantCount++;
                    }

                    // V13.1 官方常规 3 槽布局支持 1 家或双商人（左+中不同商人），绝不为 0 也绝不 3 槽全商人
                    Assert.InRange(merchantCount, 1, 2);
                }
            }
        }
    }

    // ── 5. 货架 <=10 格、三战利品足额给 3 件、普通商店禁出 0 元包裹 ──

    [Fact]
    public void Test05_ShelfWidthCap10_ThreeLootFull3Items_And_PaidShopFiltersZeroPricePackages()
    {
        var fx = RealFixture.Value;

        // 5a. 构造 4 个 Large(3格) + 2 个 Small(1格) 混合商店，总宽如果全发超过 10 格，必须被限为 <= 10 格且尽量保留更窄卡
        string[] largeIds =
        [
            "22222222-9005-4005-8005-000000000001",
            "22222222-9005-4005-8005-000000000002",
            "22222222-9005-4005-8005-000000000003",
            "22222222-9005-4005-8005-000000000004",
        ];
        string[] smallIds =
        [
            "22222222-9005-4005-8005-000000000011",
            "22222222-9005-4005-8005-000000000012",
        ];

        const string wideShopId = "22222222-9005-4005-8005-222222222201";
        const string pkgShopId = "22222222-9005-4005-8005-222222222202";
        var wideItems = largeIds.Concat(smallIds).ToArray();

        var customCards = new List<(string Id, string Json)>
        {
            (wideShopId, BuildMerchantShopJson(wideShopId, "Wide Test Merchant", wideItems, selectionIsFree: false, perItemSequentialGroups: true)),
            (pkgShopId, BuildMerchantShopJson(pkgShopId, "Pkg Test Merchant", new[] { TinkersPackageId, CutlassId, RedGumballId }, selectionIsFree: false)),
        };
        foreach (string lid in largeIds)
            customCards.Add((lid, BuildWeaponJson(lid, "Test Large " + lid[^2..], 5, size: "Large", spawningEligibility: "Always")));
        foreach (string sid in smallIds)
            customCards.Add((sid, BuildWeaponJson(sid, "Test Small " + sid[^2..], 2, size: "Small", spawningEligibility: "Always")));

        using var ov = new OverlayContext(customCards);

        var (run, _) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var shop = new ShopEngine(run, ov.Pool, ov.Repo);
        shop.DealEncounterShelf(wideShopId);

        int totalWidth = run.SelectionSet.Sum(id => ShopEngine.GetShelfSlotWidth(run.Cards[id]));
        Assert.True(totalWidth <= 10, $"货架总宽应 <= 10，实际={totalWidth}");
        Assert.Equal(10, totalWidth); // 3+3+3 + 1 = 10（跳过第4张Large后吸入1格Small）

        // 5b. 付费商店过滤 0 元包裹，且上架商品 BuyPrice >= 1；免费事件 Farai 仍可发出 0 元包裹
        shop.DealEncounterShelf(pkgShopId);
        Assert.NotEmpty(run.SelectionSet);
        Assert.DoesNotContain(run.SelectionSet, id => run.Cards[id].TemplateId == Guid.Parse(TinkersPackageId));
        Assert.All(run.SelectionSet, id => Assert.True(run.Cards[id].Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0) >= 1));

        // 验证 Farai 免费事件（SelectionIsFree = true）仍正常允许 0 元包裹
        run.Day = 5;
        shop.DealEncounterShelf(FaraiEventId, isReroll: false, playerState: new PlayerState("p", "P", "Vanessa", 500, 20, 20, 10) { Day = 5, Hour = 1 });
        Assert.True(run.SelectionRules.SelectionIsFree);
        Assert.NotEmpty(run.SelectionSet);

        // 5c. 三战利品足额给 3 件（[Jungle Ruins] Grab the Loot / Haddy - Mystery Bundle）
        foreach (string lootStepId in new[] { JungleRuinsGrabLootStepId, HaddyMysteryBundleStepId })
        {
            var (lootRun, lootEngine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 77);
            lootRun.Day = 4;
            lootRun.State = ERunState.Encounter;
            lootRun.CurrentEncounterId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
            lootRun.SelectionRules = new TSelectionContextRules
            {
                CanSelectMultiple = false,
                SelectionIsFree = true,
                CanExit = true,
            };
            var stepOpt = InjectChoiceEncounter(lootRun, fx.Pool, lootStepId, ECardType.EncounterStep);
            var stepOut = lootEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepOpt.InstanceId) });
            Assert.Null(stepOut.Error);
            Assert.Equal(3, lootRun.SelectionSet.Count);
            Assert.True(lootRun.SelectionRules.SelectionIsFree);
            Assert.True(lootRun.SelectionRules.CanSelectMultiple);
        }
    }

    // ── 6. Unsellable 拦截 + 附魔/0元可售 + WillAutoSellOnExit 仅售未拿取货架卡 ──

    [Fact]
    public void Test06_UnsellableBlocked_NormalAndEnchantedSold_And_WillAutoSellOnExit()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["slots"] }).Error);

        // 6a. Unsellable 物品（Depleted Device 021639c0，Medium 占 2 格）严禁出售
        var unsellTpl = fx.Repo.GetCard(DepletedDeviceId)!;
        var unsellCore = new CardInstance(unsellTpl, CardTier.Bronze, null, "itm_unsellable");
        Assert.True(run.HandGrid.TryPlaceOrShift(unsellCore, 3));
        run.Cards[unsellCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = unsellCore.InstanceId,
            TemplateId = Guid.Parse(DepletedDeviceId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = unsellCore,
        };
        var unsellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(unsellCore.InstanceId) });
        Assert.NotNull(unsellOut.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, unsellOut.Error!.ErrorType);
        Assert.True(run.Cards.ContainsKey(unsellCore.InstanceId));

        // 6b. 附魔 Icy 战利品物品 & 0 金可售物品 100% 允许出售
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;
        var icyCore = new CardInstance(cutlassTpl, CardTier.Silver, "Icy", "itm_icy_loot");
        Assert.True(run.HandGrid.TryPlaceOrShift(icyCore, 5));
        run.Cards[icyCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = icyCore.InstanceId,
            TemplateId = Guid.Parse(CutlassId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Silver,
            Enchantment = EEnchantmentType.Icy,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_5,
            CoreCard = icyCore,
        };
        var icySellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(icyCore.InstanceId) });
        Assert.Null(icySellOut.Error);
        Assert.False(run.Cards.ContainsKey(icyCore.InstanceId));

        // 6c. WillAutoSellOnExit：离开 Encounter 时只把货架残留未拿取卡折算 SellPrice 加给金币，绝不误卖玩家刚买走的手牌卡
        const string autoSellEncId = "33333333-9006-4006-8006-333333333301";
        using var ov = new OverlayContext(new[]
        {
            (autoSellEncId, BuildMerchantShopJson(autoSellEncId, "AutoSell Shelf", new[] { CutlassId, RedGumballId }, selectionIsFree: true, willAutoSellOnExit: true)),
        });
        var (autoRun, autoEngine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var encOpt = InjectChoiceEncounter(autoRun, ov.Pool, autoSellEncId);
        Assert.Null(autoEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encOpt.InstanceId) }).Error);
        Assert.True(autoRun.SelectionRules.WillAutoSellOnExit);
        Assert.Equal(2, autoRun.SelectionSet.Count);

        // 玩家挑选拿走第 1 件（Red Gumball），把第 2 件（Cutlass，Medium Bronze SellPrice=2）留在货架上
        var gumballShelf = autoRun.SelectionSet.Select(id => autoRun.Cards[id]).Single(c => c.TemplateId == Guid.Parse(RedGumballId));
        var cutlassShelf = autoRun.SelectionSet.Select(id => autoRun.Cards[id]).Single(c => c.TemplateId == Guid.Parse(CutlassId));
        int expectedAutoSellGold = cutlassShelf.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 2);
        Assert.True(expectedAutoSellGold > 0);

        Assert.Null(autoEngine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(gumballShelf.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3 },
        }).Error);

        int goldBeforeExit = autoRun.PlayerAttributes[EPlayerAttributeType.Gold];
        Assert.Null(autoEngine.Handle(new ExitCurrentStateCommand()).Error);

        Assert.True(autoRun.Cards.ContainsKey(gumballShelf.InstanceId), "玩家已拿入手牌的物品绝不能被 WillAutoSellOnExit 误卖");
        Assert.Equal(ECombatantId.Player, autoRun.Cards[gumballShelf.InstanceId].Owner);
        Assert.Equal(goldBeforeExit + expectedAutoSellGold, autoRun.PlayerAttributes[EPlayerAttributeType.Gold]);
    }

    // ── 7. 官方分天野怪池与 Day 1 福利局 ──

    [Fact]
    public void Test07_RollDayMatchedMonsters_Day1EasyOnly_Day2Below300_Day3PlusStrictWindow()
    {
        var fx = RealFixture.Value;
        string[] devForbiddenNames = ["Big Bad Wolf", "Greenheart", "Test Subject Alpha", "Training Dummy", "QA Test", "Sparring Partner", "Monster_Battle_Player_Template"];

        // Day 1（V13.1 官方表）：80 次抽取严格为 2 只不重复 Lv1 Bronze (75~100HP) + 1 只 Lv2 Silver (75~100HP，含 Viper 75HP)，严禁 Lv3+/假怪/伏击怪
        for (int i = 0; i < 80; i++)
        {
            var run = new RunState { RunId = $"d1_m_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 1, Hour = 3, RngSeed = 1000 + i };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            shop.DealHourEncounters();
            Assert.Equal(3, run.SelectionSet.Count);
            var slots = run.SelectionSet.Select(id =>
            {
                var tpl = fx.Pool.Get(run.Cards[id].TemplateId.ToString())!;
                var mon = fx.Repo.MonstersById[tpl.MonsterTemplateId];
                int hp = (int)Math.Round(mon.Player!.Attributes["HealthMax"]);
                var tier = MonsterPool.GetMonsterTier(tpl);
                foreach (string bad in devForbiddenNames)
                    Assert.DoesNotContain(bad, tpl.InternalName, StringComparison.OrdinalIgnoreCase);
                return (tpl.MonsterLevel, hp, tier, tpl.InternalName);
            }).ToList();

            Assert.Equal(1, slots[0].MonsterLevel);
            Assert.Equal(CardTier.Bronze, slots[0].tier);
            Assert.InRange(slots[0].hp, 75, 100);
            Assert.Equal(1, slots[1].MonsterLevel);
            Assert.Equal(CardTier.Bronze, slots[1].tier);
            Assert.InRange(slots[1].hp, 75, 100);
            Assert.Equal(2, slots[2].MonsterLevel);
            Assert.Equal(CardTier.Silver, slots[2].tier);
            Assert.InRange(slots[2].hp, 75, 100);
        }

        // Day 2（V13.1 官方公式 Lv(D)/Lv(D+1)/Lv(D+2)）：左=Lv2 Bronze (200~300HP)、中=Lv3 Silver (300~350HP)、右=Lv4 Gold+ (450~600HP)，严禁假怪
        for (int i = 0; i < 80; i++)
        {
            var run = new RunState { RunId = $"d2_m_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 2, Hour = 3, RngSeed = 2000 + i };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            shop.DealHourEncounters();
            Assert.Equal(3, run.SelectionSet.Count);
            var slots = run.SelectionSet.Select(id =>
            {
                var tpl = fx.Pool.Get(run.Cards[id].TemplateId.ToString())!;
                var mon = fx.Repo.MonstersById[tpl.MonsterTemplateId];
                int hp = (int)Math.Round(mon.Player!.Attributes["HealthMax"]);
                var tier = MonsterPool.GetMonsterTier(tpl);
                foreach (string bad in devForbiddenNames)
                    Assert.DoesNotContain(bad, tpl.InternalName, StringComparison.OrdinalIgnoreCase);
                return (tpl.MonsterLevel, hp, tier, tpl.InternalName);
            }).ToList();

            Assert.Equal(2, slots[0].MonsterLevel);
            Assert.Equal(CardTier.Bronze, slots[0].tier);
            Assert.InRange(slots[0].hp, 200, 300);
            Assert.Equal(3, slots[1].MonsterLevel);
            Assert.Equal(CardTier.Silver, slots[1].tier);
            Assert.InRange(slots[1].hp, 300, 350);
            Assert.Equal(4, slots[2].MonsterLevel);
            Assert.True(slots[2].tier >= CardTier.Gold, $"Day 2 右槽应为 Gold+，实际 {slots[2].tier}");
            Assert.InRange(slots[2].hp, 450, 600);
        }

        // Day 4 / 7 / 10（V13.1 官方公式）：左=Lv(D) Bronze / 中=Lv(D+1) Silver / 右=Lv(D+2) Gold+
        foreach (int day in new[] { 4, 7, 10 })
        {
            for (int i = 0; i < 30; i++)
            {
                var run = new RunState { RunId = $"d{day}_m_{i}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = (uint)day, Hour = 3, RngSeed = 3000 + day * 100 + i };
                var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                shop.DealHourEncounters();
                Assert.Equal(3, run.SelectionSet.Count);
                var lvs = run.SelectionSet.Select(id => fx.Pool.Get(run.Cards[id].TemplateId.ToString())!.MonsterLevel).ToList();
                Assert.Equal(day, lvs[0]);
                Assert.Equal(day + 1, lvs[1]);
                Assert.Equal(day + 2, lvs[2]);
            }
        }
    }

    // ── 8. Hour 6 Choice 锁定 PendingPvpGhost 与选前/进战/断点恢复 100% 一致 ──

    [Fact]
    public void Test08_Hour6PvpOpponentLockedInChoice_ConsistentAcrossResumeAndCombat()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, seed: 808);
        engine.Handle(new CheatCommand { Args = ["hour", "6"] });
        engine.Handle(new CheatCommand { Args = ["deal"] });

        Assert.Equal(ERunState.Choice, run.State);
        Assert.NotNull(run.PendingPvpGhost);
        Assert.NotNull(run.CurrentPvpOpponent);
        Assert.False(string.IsNullOrWhiteSpace(run.CurrentPvpOpponent.PlayerLoadout?.accountId));

        string lockedBoardId = run.PendingPvpGhost!.BoardId;
        EHero lockedHero = run.CurrentPvpOpponent.Hero;
        string lockedName = run.CurrentPvpOpponent.Name;
        string lockedAccountId = run.CurrentPvpOpponent.PlayerLoadout!.accountId;

        // 快照持久化与断点恢复到 Hour 6 Choice 必须依然是同一对手
        string snap = RunSnapshotSerializer.Serialize(run);
        Assert.True(RunSnapshotSerializer.TryDeserialize(snap, fx.Repo, out var resumedRun, out _));
        var resumedEngine = new RunEngine(resumedRun!, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        Assert.NotNull(resumedRun!.PendingPvpGhost);
        Assert.Equal(lockedBoardId, resumedRun.PendingPvpGhost!.BoardId);
        Assert.NotNull(resumedRun.CurrentPvpOpponent);
        Assert.Equal(lockedHero, resumedRun.CurrentPvpOpponent!.Hero);
        Assert.Equal(lockedName, resumedRun.CurrentPvpOpponent.Name);
        Assert.Equal(lockedAccountId, resumedRun.CurrentPvpOpponent.PlayerLoadout?.accountId);

        // 点击 Hour 6 PvP 进战，CombatSpawnSim 下发的对手信息必须与 Choice 阶段 100% 一致
        var pvpCard = resumedRun.Cards[Assert.Single(resumedRun.SelectionSet)];
        Assert.Equal(ECardType.PvpEncounter, pvpCard.Type);
        var combatOut = resumedEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.CombatSpawnSim);
        Assert.NotNull(combatOut.CombatSpawnSim!.CurrentState);

        var spawnOpp = combatOut.CombatSpawnSim.CurrentState!.PvpOpponent;
        Assert.NotNull(spawnOpp);
        Assert.Equal(lockedHero, spawnOpp!.Hero);
        Assert.Equal(lockedName, spawnOpp.Name);
        Assert.Equal(lockedAccountId, spawnOpp.PlayerLoadout?.accountId);

        var oppInitEvt = combatOut.CombatSpawnSim.Events
            .OfType<GameSimEventPlayerInitialized>()
            .Last(e => e.CombatantId == ECombatantId.Opponent);
        Assert.Equal(lockedHero, oppInitEvt.Hero);

        // 战后清空 PendingPvpGhost 与 CurrentPvpOpponent
        Assert.Null(resumedRun.PendingPvpGhost);
        Assert.Null(resumedRun.CurrentPvpOpponent);
    }

    // ── 9. PvpGhostPool MaxBoardsPerDayBucket=2500、Day 1/4+ 清洗与 1023 满格掩码 ──

    [Fact]
    public void Test09_PvpGhostPool_2500Bucket_Day1AndDay4PlusCleaning_And_Mask1023()
    {
        var fx = RealFixture.Value;
        Assert.Equal(2500, PvpGhostPool.MaxBoardsPerDayBucket);

        var rng = new Random(909);

        // 9a. Day 1 实池抽样 100 次：100% Level <= 2 && HealthMax <= 550（允许当天正常升级到 Lv2 的玩家镜像），且均属于含 Hero8 在内的 8 位正式英雄
        bool sawHero8 = false;
        for (int i = 0; i < 100; i++)
        {
            var g = fx.Ghosts.PickOpponent(day: 1, playerWins: 0, playerLosses: 0, rng, fx.Repo);
            Assert.True(g.Level <= 2 && g.HealthMax <= 550, $"Day 1 镜像异常: {g.BoardId} Lv{g.Level} HP={g.HealthMax}");
            Assert.Contains(g.Hero, HeroNames.PlayableHeroes, StringComparer.OrdinalIgnoreCase);
            if (string.Equals(g.Hero, HeroNames.Hero8, StringComparison.OrdinalIgnoreCase))
                sawHero8 = true;
        }

        // 9b. Day 5~8 实池抽样 120 次：均属于 8 位正式英雄，手牌占格 >= 8（优先 10 格满编）且无同模板 >= 3 的异常重复
        for (int day = 5; day <= 8; day++)
        {
            for (int i = 0; i < 30; i++)
            {
                var g = fx.Ghosts.PickOpponent(day, playerWins: day - 2, playerLosses: 1, rng, fx.Repo);
                Assert.Contains(g.Hero, HeroNames.PlayableHeroes, StringComparer.OrdinalIgnoreCase);
                if (string.Equals(g.Hero, HeroNames.Hero8, StringComparison.OrdinalIgnoreCase))
                    sawHero8 = true;
                int totalWidth = g.Items.Sum(it => Math.Clamp(it.Width, 1, 3));
                Assert.True(totalWidth >= 8, $"Day {day} 镜像 {g.BoardId} 总占格={totalWidth} < 8");
                var maxDup = g.Items.GroupBy(it => it.TemplateId, StringComparer.OrdinalIgnoreCase).Max(grp => grp.Count());
                Assert.True(maxDup <= 2, $"Day {day} 镜像 {g.BoardId} 出现异常重复模板计数={maxDup}");
            }
        }
        Assert.True(sawHero8, "PvpGhostPool 应已激活并可抽样到第 8 英雄双龙 (Hero8) 镜像");

        // 9c. ToPlayerState 即使旧快照 UnlockedSlotsMask 仅 120(4格)，也强制用 1023 满格摆放全部随身物品
        var smallIds = fx.Pool.OfType("Item")
            .Where(c => fx.Pool.IsPlayerSpawnable(c, false) && c.Size == "Small" && fx.Repo.GetCard(c.Id) != null)
            .Select(c => c.Id)
            .Take(8)
            .ToList();
        Assert.Equal(8, smallIds.Count);

        var ghostWithLowMask = new PvpGhostPool.GhostBoard
        {
            BoardId = "mask_test_d5",
            Day = 5,
            Hero = "Vanessa",
            Level = 5,
            HealthMax = 1500,
            UnlockedSlotsMask = 120,
            Items = smallIds.Select((id, idx) => new PvpGhostPool.GhostItem
            {
                Slot = idx,
                Width = 1,
                TemplateId = id,
                Tier = "Silver",
            }).ToList(),
        };
        var ps = PvpGhostPool.ToPlayerState(ghostWithLowMask, fx.Repo);
        Assert.Equal(1023, ps.Hand.UnlockedMask);
        Assert.Equal(8, ps.Hand.Cards.Count);
    }

    // ── 10. H1 热修：严禁占位 BuyPrice=0 或折扣/光环同步将付费商店商品价格覆盖为 0 ──

    [Fact]
    public void Test10_PaidMerchantItems_NeverOverwriteBuyPriceZero_AcrossDiscountAndCoreSync()
    {
        var fx = RealFixture.Value;
        var prices = fx.Repo.GameMode.StandardPrices;

        // 10a. 官方实库 5 张关键卡在非免费货架：无折扣 / 25%-off / 占位 BuyPrice=0 / 光环折扣下均严格 >= 1 且精准对齐官方价
        (string Id, string Name, CardTier Tier, int ExpectedStd, int ExpectedDisc)[] targets =
        [
            ("5e17cfc3-25c0-4a74-8a33-07ee972e6f6e", "Concealed Dagger", CardTier.Bronze, 2, 2),
            ("1ff59248-bf6c-49e1-9c45-59989df19693", "Dragon Whelp", CardTier.Silver, 4, 3),
            ("53099361-599f-4c1a-ad4a-d2fbb491b756", "Langxian", CardTier.Bronze, 4, 3),
            ("d5e2cff2-31b5-48eb-86ff-9543ea0c6d40", "Knee Brace", CardTier.Bronze, 2, 2),
            ("edd857da-ac11-4640-bc1a-217ef8385f72", "Sharkclaws", CardTier.Silver, 8, 6),
        ];

        var run = new RunState
        {
            RunId = "v9_h1_buyprice",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 3,
            Hour = 1,
        };
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);

        foreach (var (id, name, tier, expStd, expDisc) in targets)
        {
            var tpl = fx.Repo.GetCard(id);
            Assert.NotNull(tpl);

            // (1) 普通不打折 + 模拟占位 BaseAttributes["BuyPrice"] = 0 + SyncAllEntriesFromCoreCardsAndDiff
            var plainCore = new CardInstance(tpl!, tier);
            plainCore.BaseAttributes["BuyPrice"] = 0;
            var plainEntry = shop.CreateEntryFromCoreInstance(plainCore, isFree: false);
            Assert.Equal(expStd, plainEntry.Attributes[ECardAttributeType.BuyPrice]);
            run.SyncAllEntriesFromCoreCardsAndDiff(prices, [plainEntry]);
            Assert.Equal(expStd, plainEntry.Attributes[ECardAttributeType.BuyPrice]);
            Assert.True(plainEntry.Attributes[ECardAttributeType.BuyPrice] >= 1, $"{name} ({tier}) 不打折买价不得为 0");

            // (2) 25%-off 绿标折扣 + SyncAllEntriesFromCoreCardsAndDiff（不翻倍也不变 0）
            var discCore = new CardInstance(tpl!, tier);
            discCore.BaseAttributes["BuyPrice"] = 0;
            var discEntry = shop.CreateEntryFromCoreInstance(discCore, isFree: false);
            var discInfo = shop.ApplyMerchantItemDiscount(discEntry, ShopEngine.MerchantItemDiscountPercent);
            Assert.NotNull(discInfo);
            Assert.Equal(expStd, discInfo!.BasePrice);
            Assert.Equal(expDisc, discInfo.DiscountedPrice);
            Assert.Equal(expDisc, discEntry.Attributes[ECardAttributeType.BuyPrice]);

            run.SyncAllEntriesFromCoreCardsAndDiff(prices, [discEntry]);
            Assert.Equal(expDisc, discEntry.Attributes[ECardAttributeType.BuyPrice]);
            Assert.Equal(expDisc, RunState.ComputeCardBuyPrice(discEntry, discCore, prices));
            Assert.True(discEntry.Attributes[ECardAttributeType.BuyPrice] >= 1, $"{name} ({tier}) 折后买价不得为 0");

            // (3) 外部局外光环/技能施加 BuyPrice 乘区或减费（如 Vanessa's Fence *0.75 或 Merchant Club -10）时不得被归零或负数
            var auraCore = new CardInstance(tpl!, tier);
            auraCore.BaseAttributes["BuyPrice"] = 0;
            var auraEntry = shop.CreateEntryFromCoreInstance(auraCore, isFree: false);
            auraCore.AuraMult["BuyPrice"] = 0.75;
            run.SyncAllEntriesFromCoreCardsAndDiff(prices, [auraEntry]);
            Assert.Equal(expDisc, auraEntry.Attributes[ECardAttributeType.BuyPrice]);

            auraCore.PermanentAdd["BuyPrice"] = -99;
            run.SyncAllEntriesFromCoreCardsAndDiff(prices, [auraEntry]);
            Assert.Equal(1, auraEntry.Attributes[ECardAttributeType.BuyPrice]);
        }

        // 10b. 验证 GameData.db 中自带 "BuyPrice": 0 的条目（如 Cookies）在非免费同步时不归零、免费上下文保持 0
        var cookiesTpl = fx.Repo.GetCard("367c2ad9-94b3-4f01-a130-a673922244ef")!;
        var paidCookiesCore = new CardInstance(cookiesTpl, CardTier.Bronze);
        Assert.Equal(0, paidCookiesCore.BaseAttributes.GetValueOrDefault("BuyPrice", -1));
        var paidCookies = shop.CreateEntryFromCoreInstance(paidCookiesCore, isFree: false);
        Assert.Equal(2, paidCookies.Attributes[ECardAttributeType.BuyPrice]);
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [paidCookies]);
        Assert.Equal(2, paidCookies.Attributes[ECardAttributeType.BuyPrice]);
        Assert.NotNull(shop.ApplyMerchantItemDiscount(paidCookies, 25));
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [paidCookies]);
        Assert.Equal(2, paidCookies.Attributes[ECardAttributeType.BuyPrice]);

        // 免费候选卡即使挂载 0.75 乘区光环或减价修饰，仍应严格保持 0 元免费
        var freeCore = new CardInstance(fx.Repo.GetCard(targets[1].Id)!, CardTier.Silver);
        var freeEntry = shop.CreateEntryFromCoreInstance(freeCore, isFree: true);
        Assert.Equal(0, freeEntry.Attributes[ECardAttributeType.BuyPrice]);
        freeCore.AuraMult["BuyPrice"] = 0.75;
        freeCore.PermanentAdd["BuyPrice"] = -1;
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [freeEntry]);
        Assert.Equal(0, freeEntry.Attributes[ECardAttributeType.BuyPrice]);

        // 非免费 Skill 携带占位 BuyPrice=0 时亦不得归零，受折扣与下限钳制均 >= 1
        var skillTpl = fx.Repo.GetCard("f422485c-b23a-4c3f-9ad4-9d22718dc204")!; // Artillery Spotter (Bronze)
        var skillCore = new CardInstance(skillTpl, CardTier.Bronze);
        skillCore.BaseAttributes["BuyPrice"] = 0;
        var skillEntry = shop.CreateEntryFromCoreInstance(skillCore, isFree: false);
        Assert.Equal(5, skillEntry.Attributes[ECardAttributeType.BuyPrice]);
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [skillEntry]);
        Assert.Equal(5, skillEntry.Attributes[ECardAttributeType.BuyPrice]);
        skillCore.AuraMult["BuyPrice"] = 0.75;
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [skillEntry]);
        Assert.Equal(4, skillEntry.Attributes[ECardAttributeType.BuyPrice]);
        skillCore.PermanentAdd["BuyPrice"] = -99;
        run.SyncAllEntriesFromCoreCardsAndDiff(prices, [skillEntry]);
        Assert.Equal(1, skillEntry.Attributes[ECardAttributeType.BuyPrice]);

        // 10c. 端到端 RunEngine 付费商人流程：进入商店 -> DealEncounterShelf + TriggerOnEncounterEnteredAndDealt -> 全货架 BuyPrice >= 1 且购入扣对应金币
        const string fiveItemsShopId = "44444444-9010-4010-8010-444444444401";
        using var ov = new OverlayContext(
        [
            (fiveItemsShopId, BuildMerchantShopJson(fiveItemsShopId, "H1 Five Items Merchant", targets.Select(t => t.Id).ToArray(), selectionIsFree: false, perItemSequentialGroups: true)),
        ]);
        var (engineRun, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 303);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["gold", "100"] }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = ["slots"] }).Error);

        var shopOpt = InjectChoiceEncounter(engineRun, ov.Pool, fiveItemsShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, engineRun.State);
        Assert.Equal(5, engineRun.SelectionSet.Count);

        foreach (string instId in engineRun.SelectionSet)
        {
            var entry = engineRun.Cards[instId];
            var core = entry.CoreCard;
            Assert.NotNull(core);
            int actualBuy = entry.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, 0);
            int stdBuy = ov.Repo.GameMode.StandardPrices.GetItemBuyPrice(core!.Template.CardSize, core.Tier);
            int expectedBuy = core.HourMult.TryGetValue("BuyPrice", out double hm) && hm < 0.999
                ? ShopEngine.ComputeDiscountedBuyPrice(stdBuy, ShopEngine.MerchantItemDiscountPercent)
                : stdBuy;

            Assert.True(actualBuy >= 1, $"货架商品 {entry.TemplateId} ({entry.Tier}) 买价异常={actualBuy}");
            Assert.Equal(expectedBuy, actualBuy);
        }

        // 购买货架首件物品，校验精确扣除 >= 1 的金币
        var firstShelf = engineRun.Cards[engineRun.SelectionSet[0]];
        int buyCost = firstShelf.Attributes[ECardAttributeType.BuyPrice];
        int goldBeforeBuy = engineRun.PlayerAttributes[EPlayerAttributeType.Gold];
        var buyOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(firstShelf.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_3],
        });
        Assert.Null(buyOut.Error);
        Assert.Equal(goldBeforeBuy - buyCost, engineRun.PlayerAttributes[EPlayerAttributeType.Gold]);
    }

    // ── 本地测试 JSON 构造辅助 ──

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

    private static string BuildWeaponJson(string id, string name, int dmg, string size = "Small", string spawningEligibility = "Never")
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardItem",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "Item",
            StartingTier = "Bronze",
            Size = size,
            Heroes = new[] { "Common" },
            Tags = new[] { "Weapon" },
            HiddenTags = new[] { "Damage" },
            SpawningEligibility = spawningEligibility,
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

    private static string BuildMerchantShopJson(
        string id,
        string name,
        string[] itemIds,
        bool selectionIsFree,
        bool willAutoSellOnExit = false,
        bool perItemSequentialGroups = false)
    {
        object[] groups = perItemSequentialGroups
            ? itemIds.Select(iid => (object)new
            {
                @__type = "TSpawnGroup",
                Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = new[] { iid } } },
                SelectionMethod = "Sequential",
                Limit = new { @__type = "TFixedValue", Value = 1.0 },
                RandomWeight = 0,
            }).ToArray()
            : new object[]
            {
                new
                {
                    @__type = "TSpawnGroup",
                    Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = itemIds } },
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                    RandomWeight = 100,
                },
            };

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
            Tags = new[] { "Merchant" },
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = groups,
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                },
                Rules = new
                {
                    CanSelectMultiple = true,
                    SelectionIsFree = selectionIsFree,
                    CanExit = true,
                    WillAutoSellOnExit = willAutoSellOnExit,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private sealed class OverlayContext : IDisposable
    {
        private readonly TestInfrastructure.SharedTestOverlay _inner;
        public CardPool Pool => _inner.Pool;
        public List<BazaarServer.Data.MonsterTemplate> Monsters => _inner.Monsters;
        public LevelUpTable LevelUps => _inner.LevelUps;
        public GameModeConfig Mode => _inner.Mode;
        public GameDataRepository Repo => _inner.Repo;
        public PvpGhostPool Ghosts => _inner.Ghosts;

        public OverlayContext(IEnumerable<(string Id, string Json)> customCards)
        {
            _inner = TestInfrastructure.SharedGameDataFixture.Shared.CreateMemoryOverlay(customCards: customCards);
        }

        public void Dispose() => _inner.Dispose();
    }
}

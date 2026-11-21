using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V13.1 官方 Wiki 权威一致性对齐、Hour 6 服务端绝对保底与 MoveItem 轻量增量回包综合验收测试套件。
/// 覆盖：
///   1. 问题 #1：Day 1 Hour 1→6 全天经验总账严格 <= 8 XP（Hour 6 PvP 战斗本身 0 XP，仅跨日 +1 小时 XP）；
///   2. 问题 #2：常规小时 3 槽布局（Day 1 开放 Silver 非死循环、支持双商人、0 误刷 12 座 Lv10 附魔台与 8 座 Partner Portal - Forja、4 基座天数窗与单局唯一）；
///   3. 问题 #3：Day 1~12+ 每日野怪严格 3 只不重复，Day 1 含 2 Lv1 Bronze + 1 Lv2 Silver，覆盖全部 136 只官方可实战野怪（含原漏 9 只）；
///   4. 问题 #5：8 大英雄技能商店与 30 级升级池抽样 1,000 次验证 0 张野怪专属技能、0 跨职业污染，同时保留 Zurphin's Safari 显式 Common 与修复 Grandmaster；
///   5. 问题 #6：Tiny Furry Monster 0~5 标签基础项永不被挤掉，Shrouded Figure/Street Festival 不被 10 格截断，Wishing Fountain D11+ 满 3 项，Finn/Flambe/Farai 及全库 52 活跃非商人事件 0 异常；
///   6. Hour 6 保底：空池合成保底、Resume 自愈、中途升级压栈出栈生成全新 InstanceId、PickOpponent 异常兜底；
///   7. MoveItem 轻量增量回包：BuildDeltaGameSim 仅打包脏卡（体积大幅缩减）且准确覆盖光环变化卡。
/// </summary>
public sealed class V13_1_WikiParityAndHour6Tests
{
    private const string TinyFurryCreatureId = "54b6cf27-ab0f-4a57-bb0a-83c08de10d5a";
    private const string PetItStepId = "668a64e9-48d9-4ea2-ae9c-9526ab131ea7";
    private const string ScareItStepId = "f66494a8-c9e4-439d-b762-149d903c23aa";
    private const string ShroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    private const string StreetFestivalId = "2ee65e2b-c679-41b6-812d-5dd59a5cb27a";
    private const string WishingFountainId = "3212ade4-8e23-49cd-b9b5-f729280ba0b5";
    private const string FinnsBigBiteId = "0623cedd-f24b-4117-a6b8-5fc41064a00d";
    private const string FarmFreshIngredientsStepId = "4101c951-4439-4593-89c1-a01046ebbf8e";
    private const string FlambeId = "f4347956-4eaf-4665-aa1b-addf8423588b";
    private const string FaraiId = "412fb3ec-88de-40b4-a475-10e583189d8a";
    private const string ZurphinsSafariId = "b81a1c81-0a16-462a-8247-04653354b79d";
    private const string GrandmasterId = "12fb92f0-b76c-4493-85eb-47a6d18bdf5f";
    private const string NufuId = ShopEngine.NufuEncounterId;

    private sealed class RealDataFx
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public CardArchetypeIndex ArchetypeIndex { get; init; } = null!;
    }

    private static readonly Lazy<RealDataFx> Fx = new(() =>
    {
        string? resolvedDir = null;
        string? probe = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(probe); i++, probe = Directory.GetParent(probe)?.FullName)
        {
            string candidate = Path.Combine(probe, "dist", "data");
            string dbFile = Path.Combine(candidate, "GameData.db");
            if (File.Exists(dbFile) && new FileInfo(dbFile).Length > 1024 * 1024)
            {
                resolvedDir = candidate;
                break;
            }
        }
        if (string.IsNullOrEmpty(resolvedDir))
        {
            string mainDist = @"e:\thebazaar\dist\data";
            if (File.Exists(Path.Combine(mainDist, "GameData.db")) && new FileInfo(Path.Combine(mainDist, "GameData.db")).Length > 1024 * 1024)
                resolvedDir = mainDist;
        }
        if (string.IsNullOrEmpty(resolvedDir))
        {
            string cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "LocalLow", "Tempo Storm", "The Bazaar", "prod", "cache");
            if (File.Exists(Path.Combine(cacheDir, "GameData.db")))
                resolvedDir = cacheDir;
        }

        Assert.False(string.IsNullOrEmpty(resolvedDir), "未找到有效 GameData.db，无法执行 V13.1 权威一致性验收测试。");
        string dbPath = Path.Combine(resolvedDir!, "GameData.db");
        var repo = GameDatabaseLoader.Load(dbPath, null);
        var pool = CardPool.FromRepository(repo, resolvedDir);
        var monsters = MonsterPool.FromRepository(repo);
        var levelUps = LevelUpTable.FromRepository(repo);
        var mode = GameModeConfig.FromRepository(repo);
        var ghosts = PvpGhostPool.Load(resolvedDir!);
        var archIdx = CardArchetypeIndex.Build(pool, repo);

        return new RealDataFx
        {
            DataDir = resolvedDir!,
            DbPath = dbPath,
            Pool = pool,
            Monsters = monsters,
            LevelUps = levelUps,
            Mode = mode,
            Repo = repo,
            Ghosts = ghosts,
            ArchetypeIndex = archIdx,
        };
    });

    private static (RunState Run, RunEngine Engine) CreateStartedRun(
        EHero hero = EHero.Vanessa,
        CustomRunRuleSet? rules = null,
        int seed = 2026131,
        PvpGhostPool? ghostsOverride = null)
    {
        var fx = Fx.Value;
        var run = new RunState
        {
            RunId = "v131_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(
            run,
            fx.Pool,
            fx.Monsters,
            fx.LevelUps,
            fx.Mode,
            fx.Repo,
            ghostsOverride ?? fx.Ghosts,
            fx.ArchetypeIndex);
        engine.StartRun();
        return (run, engine);
    }

    private static int TotalAccumulatedXp(RunState run, GameModeConfig mode)
    {
        int lv = Math.Max(1, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1));
        int xp = Math.Max(0, run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0));
        return (lv - 1) * Math.Max(1, mode.ExperiencePerLevel) + xp;
    }

    private static bool IsMerchant(string id, CardPool.CardTemplate? poolTpl, BazaarCore.Models.CardTemplate? coreTpl)
        => CardArchetypeIndex.IsMerchantEncounterTemplate(id, poolTpl?.InternalName ?? coreTpl?.InternalName ?? "", poolTpl, coreTpl);

    private static void PassOneNonCombatHourViaMerchant(RunState run, RunEngine engine)
    {
        var fx = Fx.Value;
        Assert.Equal(ERunState.Choice, run.State);
        // 优先选择商人遭遇（保证无额外 XP 奇遇干扰总账测试）
        string? merchantInstId = run.SelectionSet.FirstOrDefault(id =>
            run.Cards.TryGetValue(id, out var ce) &&
            IsMerchant(ce.TemplateId.ToString(), fx.Pool.Get(ce.TemplateId.ToString()), fx.Repo.GetCard(ce.TemplateId.ToString())));
        Assert.False(string.IsNullOrEmpty(merchantInstId), $"Day {run.Day} Hour {run.Hour} 应至少包含 1 家商人遭遇");

        var enter = engine.Handle(new SelectEncounterCommand { InstanceId = new(merchantInstId!) });
        Assert.Null(enter.Error);
        Assert.Equal(ERunState.Encounter, run.State);

        var exit = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exit.Error);
    }

    // ── ① 问题 #1：完整 Day 1 Hour 1→6 全天经验总账严格 <= 8 XP（PvP 0 战斗 XP）──

    [Fact]
    public void Issue1_Day1FullHour1To6_XpLedgerShouldBeExact7BeforePvpAnd8AfterDayTransition()
    {
        var fx = Fx.Value;
        var (run, engine) = CreateStartedRun(EHero.Vanessa, seed: 13101);

        // 开局初始经验为 0（而非旧哨兵 3）
        Assert.Equal(0, new CustomRunRuleSet().StartingExperience);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // 给玩家塞一件强力高阶武器保证 Hour 3 必胜青铜怪拿到完整 3 XP（不给额外 XP 的普通武器 Cutlass）
        var cutlassTpl = fx.Repo.GetCard("1b955b8d-0ea6-4437-935c-d588be5898c1");
        Assert.NotNull(cutlassTpl);
        var ci = new CardInstance(cutlassTpl!, CardTier.Gold);
        run.HandGrid.TryPlaceOrShift(ci, 3);
        var ceWeapon = new RunState.CardEntry
        {
            InstanceId = "test_cutlass_xp",
            TemplateId = Guid.Parse(cutlassTpl!.Id),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = (EContainerSocketId)ci.StartSlot,
            CoreCard = ci,
        };
        run.Cards[ceWeapon.InstanceId] = ceWeapon;

        // Hour 1 -> 2 (+1 XP)
        PassOneNonCombatHourViaMerchant(run, engine);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(1, TotalAccumulatedXp(run, fx.Mode));

        // Hour 2 -> 3 (+1 XP, 累计 2 XP)
        PassOneNonCombatHourViaMerchant(run, engine);
        Assert.Equal(3u, run.Hour);
        Assert.Equal(2, TotalAccumulatedXp(run, fx.Mode));

        // Hour 3：选左槽青铜野怪开打（胜得 +3 XP，不叠加小时 +1 XP -> 累计 5 XP）
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(3, run.SelectionSet.Count);
        string monsterId = run.SelectionSet[0];
        var pveOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new(monsterId) });
        Assert.Null(pveOutcome.Error);
        Assert.NotNull(pveOutcome.Combat);
        Assert.Equal(ECombatantId.Player, pveOutcome.Combat!.Winner);

        if (run.State == ERunState.Loot)
        {
            var exitLoot = engine.Handle(new ExitCurrentStateCommand());
            Assert.Null(exitLoot.Error);
        }
        Assert.Equal(4u, run.Hour);
        Assert.Equal(5, TotalAccumulatedXp(run, fx.Mode));

        // Hour 4 -> 5 (+1 XP, 累计 6 XP)
        PassOneNonCombatHourViaMerchant(run, engine);
        Assert.Equal(5u, run.Hour);
        Assert.Equal(6, TotalAccumulatedXp(run, fx.Mode));

        // Hour 5 -> 6 (+1 XP, 进入 Hour 6 PvP 前精确 7/8 XP，尚未升 2 级！)
        PassOneNonCombatHourViaMerchant(run, engine);
        Assert.Equal(1u, run.Day);
        Assert.Equal(6u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(7, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(7, TotalAccumulatedXp(run, fx.Mode));

        // Hour 6 PvP：战斗本身 0 XP，跨日推进至 Day 2 Hour 1 给 +1 小时 XP -> 全天精确 8 XP（升 Lv2 余 0 XP，绝非 10 XP！）
        string pvpCardId = run.SelectionSet.Single();
        var pvpOutcome = engine.Handle(new SelectEncounterCommand { InstanceId = new(pvpCardId) });
        Assert.Null(pvpOutcome.Error);
        Assert.NotNull(pvpOutcome.CombatSpawnSim);
        Assert.NotNull(pvpOutcome.CombatDespawnSim);

        // PvP 跨日正好 7 + 1 = 8 XP -> 升 Lv2 触发挂起 LevelUp，总累计经验严格 == 8 XP
        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(8, TotalAccumulatedXp(run, fx.Mode));
    }

    [Fact]
    public void Issue1_CustomStartingExperience3_ShouldActuallyTakeEffect()
    {
        var customRules = new CustomRunRuleSet { StartingExperience = 3 };
        var (run, _) = CreateStartedRun(EHero.Vanessa, rules: customRules, seed: 13102);
        Assert.Equal(3, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.True(customRules.IsCustomPowerModified());
    }

    // ── ② 问题 #2：常规小时 3 槽布局、0 误刷附魔台、Day 1 非死循环、支持双商人、基座天数门禁 ──

    [Fact]
    public void Issue2_Hourly3SlotLayout_ZeroMilestoneEnchantPedestals_Day1HasSilverAndDoubleMerchants()
    {
        var fx = Fx.Value;

        // 1. 校验 CardPool.HourlyEncounters 彻底清除 12 座附魔祭坛与 8 个 Partner Portal - Forja
        foreach (string hero in HeroNames.PlayableHeroes)
        {
            var hourlyPeds = fx.Pool.HourlyEncounters(hero, "PedestalEncounter");
            Assert.NotEmpty(hourlyPeds);
            foreach (var ped in hourlyPeds)
            {
                Assert.DoesNotContain(ped.Id, CardPool.NonHourlyMilestoneEnchantPedestalIds);
                Assert.False(
                    ped.InternalName.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase),
                    $"常规小时基座池不应包含 Partner Portal 子基座: {ped.InternalName}");
            }
            // 仅保留官方 4 座地图基座（B1&B2 / Sterling / Forja / The Artist）
            Assert.True(hourlyPeds.Count <= 4, $"常规小时 PedestalEncounter 应 <= 4 座，实际 {hourlyPeds.Count}");
        }

        // 2. 跨 Day 1..10 × Hour 1/2/4/5 共 400 轮发牌统计
        var day1MerchantTiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var day1DistinctEvents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int doubleMerchantHours = 0;
        int totalRegularHours = 0;
        int pedestalAppearances = 0;

        for (int trial = 0; trial < 10; trial++)
        {
            var run = new RunState
            {
                RunId = $"v131_slot_{trial}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 5000 + trial * 37,
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo, run.CustomRules, fx.ArchetypeIndex);

            for (uint d = 1; d <= 10; d++)
            {
                run.Day = d;
                run.VisitedDayEncounterIds.Clear();
                foreach (uint h in new uint[] { 1, 2, 4, 5 })
                {
                    run.Hour = h;
                    shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);
                    totalRegularHours++;

                    Assert.Equal(3, run.SelectionSet.Count);
                    int merchantsInHour = 0;

                    foreach (string instId in run.SelectionSet)
                    {
                        var ce = run.Cards[instId];
                        string tplId = ce.TemplateId.ToString();
                        var tpl = fx.Pool.Get(tplId);
                        Assert.NotNull(tpl);

                        // 绝对不允许误刷 12 座 Lv10/Fates 定向附魔台或 Partner Portal - Forja
                        Assert.DoesNotContain(tplId, CardPool.NonHourlyMilestoneEnchantPedestalIds);
                        Assert.False(tpl!.InternalName.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase));

                        // 校验单卡天数窗与单局唯一门禁
                        Assert.True(ShopEngine.IsWithinOfficialDayWindow(tpl, (int)d, (int)h),
                            $"Day {d} Hour {h} 违规刷出窗口外遭遇: {tpl.InternalName}");

                        if (ce.Type == ECardType.PedestalEncounter)
                            pedestalAppearances++;

                        if (IsMerchant(tplId, tpl, fx.Repo.GetCard(tplId)))
                        {
                            merchantsInHour++;
                            if (d == 1)
                                day1MerchantTiers.Add(tpl.StartingTier);
                        }
                        else if (d == 1)
                        {
                            day1DistinctEvents.Add(tpl.Id);
                        }
                    }

                    if (merchantsInHour >= 2)
                        doubleMerchantHours++;
                }
            }
        }

        // Day 1 必须能刷出 Silver 商人，且非商人事件池远超旧版 3 青铜事件死循环
        Assert.Contains("Silver", day1MerchantTiers);
        Assert.True(day1DistinctEvents.Count >= 6, $"Day 1 非商人事件去重数应 >= 6（告别 3 青铜死循环），实际: {day1DistinctEvents.Count}");
        // 必须支持官方双商人布局
        Assert.True(doubleMerchantHours > 0, "常规小时应支持双商人布局（左+中两家不同商人）");
        // 标准模式下基座出现率应控制在合理区间（不再是 30%~55% 满屏附魔台）
        double pedRate = (double)pedestalAppearances / totalRegularHours;
        Assert.True(pedRate <= 0.25, $"标准模式地图基座出现率应 <= 25%，实际: {pedRate:P1}");
    }

    // ── ③ 问题 #3：Day 1~12+ 136 只野怪官方公式与 Day 1 Silver Lv2 验收 ──

    [Fact]
    public void Issue3_MonstersDay1To12_All136CuratedAndExactLevelTierFormula()
    {
        var fx = Fx.Value;

        // 1. 全表覆盖全部 136 只官方可实战野怪（含原漏 9 只），零调试怪/空手牌/失效白名单
        var allScheduledIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var roster in OfficialDaySchedule.CuratedDayRosters.Values)
        {
            foreach (string id in roster.AllIds())
                allScheduledIds.Add(id);
        }
        foreach (string id in OfficialDaySchedule.LateGameRoster.AllIds())
            allScheduledIds.Add(id);

        Assert.Equal(136, allScheduledIds.Count);
        foreach (string id in allScheduledIds)
        {
            var tpl = fx.Pool.Get(id);
            Assert.True(MonsterPool.IsPlayableCombatEncounter(tpl, fx.Pool, fx.Repo, requireAlwaysSpawning: true),
                $"精选野怪 {id} ({tpl?.InternalName}) 未通过实战合法性校验");
        }

        // 核验全部原漏 9 只官方正式怪均已归位
        string[] restoredMissing9 =
        [
            "b79845a7-f806-4e7a-9011-d98ebd22a323", // Viper (Day 1 Right Silver)
            "f6962ffe-efdf-4da2-9803-6671f42d9811", // Happy Camper (Day 1 Right Silver)
            "035ace94-61e5-4343-8383-83044ea4e08e", // Haunted Kimono (Day 1 Right Silver)
            "6c84f0a7-4669-4eeb-bb0b-a0dce69e6a29", // Drone Operator (Day 5 Right Gold 850HP)
            "f9e17ce4-c82d-44c2-8406-ad33277a3b1c", // Sergeant Suds (Day 6 Right Gold 1200HP)
            "69af3106-78fd-4a74-b150-ec41442cdbba", // Lich (Day 8 Right Legendary 1800HP)
            "74173ad7-8fea-442d-a7ec-ba0b604629c9", // Bloodreef Captain (Bronze) (Day 10 Left)
            "b988bbe5-35cf-445e-bea5-ec54b8daf54a", // Aleppa Tantius (Day 9 Right Diamond)
            "265d1068-e90d-49c4-8013-ed2e856ead8f", // Property Baron (Day 11 Right Diamond)
        ];
        foreach (string id in restoredMissing9)
            Assert.Contains(id, allScheduledIds);

        // 2. Day 1 抽样 100 次：严格 3 只不重复，2 只 Lv1 Bronze + 1 只 Lv2 Silver（且低血 Viper 75HP 可正常出场）
        var rng = new Random(13103);
        bool sawViper = false;
        for (int i = 0; i < 100; i++)
        {
            var d1 = OfficialDaySchedule.SelectDayMonsters(1, 3, fx.Pool, fx.Repo, rng);
            Assert.Equal(3, d1.Count);
            Assert.Equal(3, d1.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(CardTier.Bronze, MonsterPool.GetMonsterTier(d1[0]));
            Assert.Equal(1, MonsterPool.GetEffectiveMonsterLevel(d1[0], fx.Repo));
            Assert.Equal(CardTier.Bronze, MonsterPool.GetMonsterTier(d1[1]));
            Assert.Equal(1, MonsterPool.GetEffectiveMonsterLevel(d1[1], fx.Repo));
            Assert.Equal(CardTier.Silver, MonsterPool.GetMonsterTier(d1[2]));
            Assert.Equal(2, MonsterPool.GetEffectiveMonsterLevel(d1[2], fx.Repo));
            if (d1[2].Id.Equals("b79845a7-f806-4e7a-9011-d98ebd22a323", StringComparison.OrdinalIgnoreCase))
                sawViper = true;
        }
        Assert.True(sawViper, "Day 1 右槽 75HP 低血白银怪 Viper 必须正常出场（不得被伪血量过滤误杀）");

        // 3. Day 2..10 严格校验公式：左=Lv(D) Bronze / 中=Lv(D+1) Silver / 右=Lv(D+2) Gold+
        bool sawLich = false;
        bool sawDroneOp = false;
        for (int day = 2; day <= 10; day++)
        {
            for (int i = 0; i < 50; i++)
            {
                var picks = OfficialDaySchedule.SelectDayMonsters(day, 3, fx.Pool, fx.Repo, rng);
                Assert.Equal(3, picks.Count);
                Assert.Equal(3, picks.Select(m => m.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());

                Assert.Equal(CardTier.Bronze, MonsterPool.GetMonsterTier(picks[0]));
                Assert.Equal(day, MonsterPool.GetEffectiveMonsterLevel(picks[0], fx.Repo));

                Assert.Equal(CardTier.Silver, MonsterPool.GetMonsterTier(picks[1]));
                Assert.Equal(day + 1, MonsterPool.GetEffectiveMonsterLevel(picks[1], fx.Repo));

                Assert.True(MonsterPool.GetMonsterTier(picks[2]) >= CardTier.Gold,
                    $"Day {day} 右槽应为 Gold+，实际: {picks[2].InternalName} ({picks[2].StartingTier})");
                Assert.Equal(day + 2, MonsterPool.GetEffectiveMonsterLevel(picks[2], fx.Repo));

                if (day == 5 && picks[2].Id.Equals("6c84f0a7-4669-4eeb-bb0b-a0dce69e6a29", StringComparison.OrdinalIgnoreCase))
                    sawDroneOp = true;
                if (day == 8 && picks[2].Id.Equals("69af3106-78fd-4a74-b150-ec41442cdbba", StringComparison.OrdinalIgnoreCase))
                    sawLich = true;
            }
        }
        Assert.True(sawDroneOp, "Day 5 低血高阶怪 Drone Operator (850HP) 必须正常出场");
        Assert.True(sawLich, "Day 8 低血传奇怪 Lich (1800HP) 必须正常出场");
    }

    // ── ④ 问题 #5：8 英雄技能商店 + 30 级升级池抽样 1,000 次 0 野怪技能、0 跨职业污染 ──

    [Fact]
    public void Issue5_SkillPoolsAnd30LevelUps_ZeroMonsterSkillOrCrossHeroPollutionAcross1000Samples()
    {
        var fx = Fx.Value;

        var monsterOnlyCommonSkills = fx.Repo.AllSkills
            .Where(s => s.Heroes.Count > 0 &&
                        s.Heroes.All(h => h.Equals(HeroNames.Common, StringComparison.OrdinalIgnoreCase)))
            .Select(s => s.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.True(monsterOnlyCommonSkills.Count >= 100, $"预期 DB 含 >= 100 张 Common 野怪技能，实际: {monsterOnlyCommonSkills.Count}");

        // 1. 验证 CardPool.Skills(hero) 默认 0 野怪技能、0 跨职业污染
        foreach (string hero in HeroNames.PlayableHeroes)
        {
            var skills = fx.Pool.Skills(hero);
            Assert.True(skills.Count >= 80, $"英雄 {hero} 合法技能池应 >= 80，实际: {skills.Count}");
            foreach (var sk in skills)
            {
                Assert.DoesNotContain(sk.Id, monsterOnlyCommonSkills);
                Assert.Contains(hero, sk.Heroes, StringComparer.OrdinalIgnoreCase);
            }
        }

        // 2. 8 英雄 × Nufu 技能商店累计抽样 >= 1,000 次 + 30 级升级池累计抽样 >= 1,000 次
        var rng = new Random(13105);
        int shopSkillSamples = 0;
        int levelUpSkillSamples = 0;
        var nufuCore = fx.Repo.GetCard(NufuId);
        Assert.NotNull(nufuCore?.SelectionContext?.SpawnContext);

        foreach (string hero in HeroNames.PlayableHeroes)
        {
            var ps = new PlayerState("p_" + hero, hero, hero, 250, 20, 0, 10);
            for (int iter = 0; iter < 130; iter++)
            {
                var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, ps, ps, rng)
                {
                    CurrentDay = (iter % 10) + 1,
                    CurrentHour = 1,
                    CurrentEncounterId = NufuId,
                    SelfCard = new CardInstance(nufuCore!),
                };
                var drawn = SpawnQueryEngine.SpawnCards(
                    nufuCore!.SelectionContext!.SpawnContext,
                    ctx,
                    validAssetIds: fx.Pool.GetEffectiveValidAssetIds());
                foreach (var inst in drawn)
                {
                    if (!inst.Template.Type.Equals("Skill", StringComparison.OrdinalIgnoreCase)) continue;
                    shopSkillSamples++;
                    Assert.DoesNotContain(inst.Template.Id, monsterOnlyCommonSkills);
                    Assert.Contains(hero, inst.Template.Heroes, StringComparer.OrdinalIgnoreCase);
                }
            }

            // 覆盖 Lv2..Lv30 全部等级升级奖励抽样（展开升级技能导师 EventEncounter / EncounterStep 实际发出的技能卡！）
            var validAssets = fx.Pool.GetEffectiveValidAssetIds();
            for (int round = 0; round < 6; round++)
            {
                for (int targetLv = 2; targetLv <= 30; targetLv++)
                {
                    var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, ps, ps, rng)
                    {
                        CurrentDay = Math.Min(10, targetLv),
                        CurrentHour = 1,
                    };
                    var rewards = fx.LevelUps.RollRewardsWithTier(
                        targetLv,
                        ctx,
                        id => fx.Pool.IsPlayerSpawnableId(id, allowGuidOnly: true));
                    foreach (var (tplId, _) in rewards)
                    {
                        var coreTpl = fx.Repo.GetCard(tplId);
                        if (coreTpl == null) continue;

                        if (coreTpl.Type.Equals("Skill", StringComparison.OrdinalIgnoreCase))
                        {
                            levelUpSkillSamples++;
                            Assert.DoesNotContain(coreTpl.Id, monsterOnlyCommonSkills);
                            Assert.Contains(hero, coreTpl.Heroes, StringComparer.OrdinalIgnoreCase);
                            continue;
                        }

                        // 展开升级导师事件 / 步骤（如 Pip / Argenta / Orlin / Adira / Bjorn / Malafang / Ryukon 等）
                        void CheckSpawnContextSkills(RuleNode? sc, BazaarCore.Models.CardTemplate hostTpl)
                        {
                            if (sc == null) return;
                            var subCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, ps, ps, rng)
                            {
                                CurrentDay = Math.Min(10, targetLv),
                                CurrentHour = 1,
                                CurrentEncounterId = hostTpl.Id,
                                SelfCard = new CardInstance(hostTpl),
                            };
                            foreach (var skInst in SpawnQueryEngine.SpawnCards(sc, subCtx, validAssetIds: validAssets))
                            {
                                if (!skInst.Template.Type.Equals("Skill", StringComparison.OrdinalIgnoreCase)) continue;
                                levelUpSkillSamples++;
                                Assert.DoesNotContain(skInst.Template.Id, monsterOnlyCommonSkills);
                                Assert.Contains(hero, skInst.Template.Heroes, StringComparer.OrdinalIgnoreCase);
                            }
                        }

                        CheckSpawnContextSkills(coreTpl.SelectionContext?.SpawnContext, coreTpl);
                        if (coreTpl.Abilities != null)
                        {
                            foreach (var ab in coreTpl.Abilities.Values)
                            {
                                if (ab.Action?.Type == "TActionGameDealCards" && ab.Action.SpawnContext != null)
                                    CheckSpawnContextSkills(ab.Action.SpawnContext, coreTpl);
                                if (ab.Action?.Type == "TActionAnd" && ab.Action.Actions != null)
                                {
                                    foreach (var subAct in ab.Action.Actions)
                                    {
                                        if (subAct.Type == "TActionGameDealCards" && subAct.SpawnContext != null)
                                            CheckSpawnContextSkills(subAct.SpawnContext, coreTpl);
                                    }
                                }
                            }
                        }
                        if (coreTpl.NextEncounterOnSelection != null)
                        {
                            foreach (var nxt in coreTpl.NextEncounterOnSelection)
                            {
                                var nextTpl = !string.IsNullOrEmpty(nxt.Id) ? fx.Repo.GetCard(nxt.Id) : null;
                                if (nextTpl != null)
                                    CheckSpawnContextSkills(nextTpl.SelectionContext?.SpawnContext, nextTpl);
                            }
                        }
                    }
                }
            }
        }

        Assert.True(shopSkillSamples >= 1000, $"Nufu 技能商店抽样应 >= 1,000 次，实际: {shopSkillSamples}");
        Assert.True(levelUpSkillSamples >= 1000, $"30 级升级导师技能抽样应 >= 1,000 次，实际: {levelUpSkillSamples}");

        // 3. 验证显式声明 ConstraintHero:["Common"] 的 Zurphin's Safari 仍可正常产出 Common 野怪技能
        var zurphinCore = fx.Repo.GetCard(ZurphinsSafariId);
        Assert.NotNull(zurphinCore?.SelectionContext?.SpawnContext);
        var vanessaPs = new PlayerState("p_v", "Vanessa", "Vanessa", 250, 20, 0, 10);
        var zCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, vanessaPs, vanessaPs, new Random(777))
        {
            CurrentDay = 8,
            CurrentHour = 1,
            CurrentEncounterId = ZurphinsSafariId,
            SelfCard = new CardInstance(zurphinCore!),
        };
        var zurphinDrawn = SpawnQueryEngine.SpawnCards(
            zurphinCore!.SelectionContext!.SpawnContext,
            zCtx,
            validAssetIds: fx.Pool.GetEffectiveValidAssetIds());
        Assert.NotEmpty(zurphinDrawn);
        Assert.All(zurphinDrawn, c => Assert.Contains(c.Template.Id, monsterOnlyCommonSkills));

        // 4. 验证 Grandmaster 在玩家仅拥有 1 个可升级技能时不会在 G2 跌落抽跨职业/野怪新技能
        var gmCore = fx.Repo.GetCard(GrandmasterId);
        if (gmCore?.SelectionContext?.SpawnContext != null)
        {
            var singleSkillTpl = fx.Pool.Skills("Vanessa")
                .Select(s => fx.Repo.GetCard(s.Id))
                .First(c => c != null && c.DefaultTier == CardTier.Bronze)!;
            var gmPs = new PlayerState("p_gm", "Vanessa", "Vanessa", 250, 20, 0, 10);
            gmPs.Skills.Add(new CardInstance(singleSkillTpl, CardTier.Bronze));
            var gmCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, gmPs, gmPs, new Random(888))
            {
                CurrentDay = 6,
                CurrentHour = 1,
                CurrentEncounterId = GrandmasterId,
                SelfCard = new CardInstance(gmCore),
            };
            var gmDrawn = SpawnQueryEngine.SpawnCards(
                gmCore.SelectionContext.SpawnContext,
                gmCtx,
                validAssetIds: fx.Pool.GetEffectiveValidAssetIds());
            // 绝不能跌落抽出未拥有的跨职业或野怪技能
            foreach (var c in gmDrawn)
            {
                if (c.Template.Type.Equals("Skill", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal(singleSkillTpl.Id, c.Template.Id);
                }
            }
        }
    }

    // ── ⑤ 问题 #6：Tiny Furry Monster + 13 高危事件 + 全库 52 活跃非商人事件扫描 ──

    [Fact]
    public void Issue6_TinyFurryMonster_BaseOptionsNeverCrowdedOut_AndAll52ActiveEventsValid()
    {
        var fx = Fx.Value;

        // 1. Tiny Furry Monster：0 标签出 2 个基础项；同时带齐 5 种标签时仍严格保留 Pet It + Scare It 基础 2 项且总计满 4 项
        var run0 = new RunState { RunId = "tf_0", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 2, Hour = 1, RngSeed = 101 };
        var shop0 = new ShopEngine(run0, fx.Pool, fx.Repo);
        var psEmpty = new PlayerState("p0", "Vanessa", "Vanessa", 250, 20, 0, 10);
        shop0.DealEncounterShelf(TinyFurryCreatureId, isReroll: false, playerState: psEmpty);
        var ids0 = run0.SelectionSet.Select(id => run0.Cards[id].TemplateId.ToString()).ToList();
        Assert.Equal(2, ids0.Count);
        Assert.Contains(PetItStepId, ids0, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ScareItStepId, ids0, StringComparer.OrdinalIgnoreCase);

        var run5 = new RunState { RunId = "tf_5", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 2, Hour = 1, RngSeed = 102 };
        var shop5 = new ShopEngine(run5, fx.Pool, fx.Repo);
        var ps5Tags = new PlayerState("p5", "Vanessa", "Vanessa", 250, 20, 0, 10);
        foreach (string tag in new[] { "Trap", "Instrument", "Friend", "Food", "Toy" })
        {
            var tagItem = fx.Repo.AllItems.FirstOrDefault(it =>
                it.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) && it.CardSize == CardSize.Small);
            if (tagItem != null)
                ps5Tags.Stash.TryAddAuto(new CardInstance(tagItem));
        }
        shop5.DealEncounterShelf(TinyFurryCreatureId, isReroll: false, playerState: ps5Tags);
        var ids5 = run5.SelectionSet.Select(id => run5.Cards[id].TemplateId.ToString()).ToList();
        Assert.Equal(4, ids5.Count);
        Assert.Contains(PetItStepId, ids5, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(ScareItStepId, ids5, StringComparer.OrdinalIgnoreCase);

        // 2. Wishing Fountain 在无尽 Day 11 / Day 15 仍出 3 个完整许愿选项
        foreach (uint endlessDay in new uint[] { 11u, 15u })
        {
            var runWf = new RunState { RunId = "wf_" + endlessDay, GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = endlessDay, Hour = 1, RngSeed = 103 };
            var shopWf = new ShopEngine(runWf, fx.Pool, fx.Repo);
            shopWf.DealEncounterShelf(WishingFountainId, isReroll: false, playerState: psEmpty);
            Assert.Equal(3, runWf.SelectionSet.Count);
        }

        // 3. Street Festival (Lim=8) / Shrouded Figure (Lim=7) 不被 10 格 Item 宽度截断
        var sfRun = new RunState { RunId = "sf_1", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 6, Hour = 1, RngSeed = 104 };
        var sfShop = new ShopEngine(sfRun, fx.Pool, fx.Repo);
        var sfPs = new PlayerState("p_sf", "Vanessa", "Vanessa", 250, 20, 0, 10);
        // 塞入能触发多个 Street Festival 特殊选项的物品（Food / Tech / Potion 等）
        foreach (string tag in new[] { "Food", "Tech", "Potion", "Tool" })
        {
            var item = fx.Repo.AllItems.FirstOrDefault(it => it.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) && it.CardSize == CardSize.Small);
            if (item != null) sfPs.Stash.TryAddAuto(new CardInstance(item));
        }
        sfShop.DealEncounterShelf(StreetFestivalId, isReroll: false, playerState: sfPs);
        Assert.True(sfRun.SelectionSet.Count >= 3, $"Street Festival 应至少产出 >= 3 个合法选项，实际: {sfRun.SelectionSet.Count}");

        // 验证 Shrouded Figure 与 Finn's Big Bite 视觉回退（原生 IsAssetValid 保持白名单纯净，有效集与电线回退放行 4101c951）
        var shroudedCore = fx.Repo.GetCard(ShroudedFigureId);
        Assert.NotNull(shroudedCore);
        Assert.False(fx.Pool.IsAssetValid(FarmFreshIngredientsStepId), "原生 IsAssetValid 应严格保持白名单纯净");
        Assert.Contains(FarmFreshIngredientsStepId, fx.Pool.GetEffectiveValidAssetIds()!);
        Assert.Equal("68d8323d-acef-41fa-90ea-1771e5935b66", fx.Pool.ResolveSafeWireTemplateId(FarmFreshIngredientsStepId));

        // 4. Flambe 跳过悬空 G3 零报错并完整出 4 项
        var flRun = new RunState { RunId = "fl_1", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 6, Hour = 1, RngSeed = 105 };
        var flShop = new ShopEngine(flRun, fx.Pool, fx.Repo);
        flShop.DealEncounterShelf(FlambeId, isReroll: false, playerState: psEmpty);
        Assert.Equal(4, flRun.SelectionSet.Count);

        // 5. Farai 放行 29 张白名单 Package 子卡，正常满架发牌不空架
        var faRun = new RunState { RunId = "fa_1", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 6, Hour = 1, RngSeed = 106 };
        var faShop = new ShopEngine(faRun, fx.Pool, fx.Repo);
        faShop.DealEncounterShelf(FaraiId, isReroll: false, playerState: psEmpty);
        Assert.True(faRun.SelectionSet.Count >= 3, $"Farai 应正常发满合法包裹，实际: {faRun.SelectionSet.Count}");

        // 6. 全库 52 个活跃非商人 EventEncounter 零异常扫描
        var allActiveNonMerchantEvents = fx.Pool.OfType("EventEncounter")
            .Where(e => fx.Pool.IsPlayerSpawnable(e, allowGuidOnly: false)
                     && !IsMerchant(e.Id, e, fx.Repo.GetCard(e.Id))
                     && !e.InternalName.Contains("(Level Up)", StringComparison.OrdinalIgnoreCase)
                     && !e.InternalName.Contains("(Start ", StringComparison.OrdinalIgnoreCase)
                     && !e.InternalName.Contains("DEFUNCT", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(allActiveNonMerchantEvents.Count >= 40, $"活跃非商人事件数应 >= 40，实际: {allActiveNonMerchantEvents.Count}");

        foreach (var ev in allActiveNonMerchantEvents)
        {
            string hero = ev.Heroes.FirstOrDefault(h => HeroNames.PlayableHeroes.Contains(h, StringComparer.OrdinalIgnoreCase)) ?? "Vanessa";
            var parsedHero = Enum.TryParse<EHero>(hero, true, out var eh) ? eh : EHero.Vanessa;
            var scanRun = new RunState
            {
                RunId = "scan_" + ev.Id[..6],
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = parsedHero,
                Day = 6,
                Hour = 1,
                RngSeed = 2026,
            };
            var scanShop = new ShopEngine(scanRun, fx.Pool, fx.Repo);
            var scanPs = new PlayerState("p_scan", hero, hero, 250, 20, 0, 10);
            scanShop.DealEncounterShelf(ev.Id, isReroll: false, playerState: scanPs);
            // 必须保证：要么发出了合法选项，要么设置了 CanExit=true 绝不制造不可退出死局
            Assert.True(scanRun.SelectionSet.Count > 0 || scanRun.SelectionRules.CanExit,
                $"活跃事件 {ev.InternalName} ({ev.Id}) 产生了 0 选项且不可退出死局！");
        }
    }

    // ── ⑥ Hour 6 中途升级压栈恢复新 InstanceId + Resume 自愈 + 空抽保底 ──

    [Fact]
    public void Hour6Guarantees_EmptyPoolFallback_ResumeHeal_AndMidHour6LevelUpGeneratesFreshInstanceId()
    {
        var fx = Fx.Value;

        // 1. 空 CardPool 下 Hour 6 仍合成保底 PvpEncounter 永不空选
        var emptyPool = new CardPool();
        var emptyRun = new RunState
        {
            RunId = "h6_empty",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 1,
            Hour = 6,
            RngSeed = 301,
        };
        var emptyShop = new ShopEngine(emptyRun, emptyPool, fx.Repo);
        emptyShop.DealHourEncounters();
        Assert.Single(emptyRun.SelectionSet);
        var syntheticPvp = emptyRun.Cards[emptyRun.SelectionSet[0]];
        Assert.Equal(ECardType.PvpEncounter, syntheticPvp.Type);
        Assert.Equal(Guid.Parse(ShopEngine.Hour6PvpCardId), syntheticPvp.TemplateId);

        // 2. Resume 自愈：存档停留在 Hour 6 Choice 但 SelectionSet 为空 → 构造 RunEngine 即自动自愈出 PvpEncounter 且锁定对手
        var brokenRun = new RunState
        {
            RunId = "h6_broken_resume",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 2,
            Hour = 6,
            State = ERunState.Choice,
            RngSeed = 302,
        };
        brokenRun.SelectionSet.Clear();
        var emptyGhosts = new PvpGhostPool();
        _ = new RunEngine(brokenRun, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, emptyGhosts);
        Assert.Single(brokenRun.SelectionSet);
        Assert.Equal(ECardType.PvpEncounter, brokenRun.Cards[brokenRun.SelectionSet[0]].Type);
        Assert.NotNull(brokenRun.CurrentPvpOpponent);
        Assert.NotNull(brokenRun.PendingPvpGhost);

        // 3. Hour 6 桌面中途升级（卖掉给经验的物品 -> 压栈进入 LevelUp -> 退出/完成升级 -> 出栈恢复 Hour 6 Choice）：
        //    必须生成全新 InstanceId 根治客户端 DisposedCards 竞态吞卡！
        var (run, engine) = CreateStartedRun(EHero.Vanessa, seed: 303);
        run.Hour = 6;
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 7;
        var healShop = new ShopEngine(run, fx.Pool, fx.Repo);
        healShop.DealHourEncounters();
        Assert.Single(run.SelectionSet);
        string oldPvpInstanceId = run.SelectionSet[0];
        Assert.Equal(ECardType.PvpEncounter, run.Cards[oldPvpInstanceId].Type);

        // 在玩家背包放入官方经典卖掉给经验道具 Vial of Blood (08bb5055)，真实还原 Hour 6 桌面卖卡触发中途升级！
        var xpItemTpl = fx.Repo.GetCard("08bb5055-bd8d-4f86-bff6-24f8cb47f509");
        Assert.NotNull(xpItemTpl);
        var xpCore = new CardInstance(xpItemTpl!, CardTier.Bronze);
        if (xpCore.GetAttribute("Custom_0") <= 0)
            xpCore.PermanentAdd["Custom_0"] = 2;
        run.StashGrid.TryPlaceOrShift(xpCore, 0);
        var xpEntry = new RunState.CardEntry
        {
            InstanceId = "hour6_xp_sell_item",
            TemplateId = Guid.Parse(xpItemTpl!.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Stash,
            Socket = EContainerSocketId.Socket_0,
            CoreCard = xpCore,
        };
        xpEntry.Attributes[ECardAttributeType.Custom_0] = (int)Math.Max(2, xpCore.GetAttribute("Custom_0"));
        run.Cards[xpEntry.InstanceId] = xpEntry;

        var sellOutcome = engine.Handle(new SellCardCommand { InstanceId = new(xpEntry.InstanceId) });
        Assert.Null(sellOutcome.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.NotEmpty(run.SelectionSet);

        // 退出升级后，出栈恢复 Hour 6 Choice：必须生成全新 InstanceId 的 PvpEncounter 且锁定对手镜像
        var exitLv = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitLv.Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Hour);
        Assert.Single(run.SelectionSet);
        string newPvpInstanceId = run.SelectionSet[0];
        Assert.Equal(ECardType.PvpEncounter, run.Cards[newPvpInstanceId].Type);
        Assert.NotEqual(oldPvpInstanceId, newPvpInstanceId);
        Assert.NotNull(run.CurrentPvpOpponent);
    }

    // ── ⑦ MoveItem 轻量增量回包（BuildDeltaGameSim）验证 ──

    [Fact]
    public void MoveItemAcceleration_BuildDeltaGameSim_OnlySerializesDirtyAndMovedCards()
    {
        var fx = Fx.Value;
        var (run, engine) = CreateStartedRun(EHero.Vanessa, seed: 13107);

        // 往手牌和储藏箱共放 8 件 Small 物品（总计含 3 张遭遇卡 >= 11 张卡）
        var smallItems = fx.Repo.GetSpawnableItemsForHero("Vanessa")
            .Where(it => it.CardSize == CardSize.Small && (it.Auras == null || it.Auras.Count == 0))
            .Take(8)
            .ToList();
        Assert.True(smallItems.Count >= 6);

        var stashIds = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var tpl = smallItems[i];
            var ci = new CardInstance(tpl, CardTier.Bronze);
            bool inHand = i < 3;
            int slot = inHand ? (3 + i) : i;
            if (inHand) run.HandGrid.TryPlaceOrShift(ci, slot);
            else run.StashGrid.TryPlaceOrShift(ci, slot);

            var entry = new RunState.CardEntry
            {
                InstanceId = $"mv_item_{i}",
                TemplateId = Guid.Parse(tpl.Id),
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = inHand ? EInventorySection.Hand : EInventorySection.Stash,
                Socket = (EContainerSocketId)ci.StartSlot,
                CoreCard = ci,
            };
            run.Cards[entry.InstanceId] = entry;
            if (!inHand) stashIds.Add(entry.InstanceId);
        }

        // 执行纯储藏箱内部挪动（走快路径）
        var moveOutcome = engine.Handle(new MoveItemCommand
        {
            InstanceId = new(stashIds[0]),
            Section = EInventorySection.Stash,
            TargetSockets = [EContainerSocketId.Socket_8],
        });
        Assert.Null(moveOutcome.Error);
        Assert.Contains(stashIds[0], moveOutcome.DirtyCardIds);

        var fullSim = run.BuildFullGameSim(moveOutcome.Events);
        var deltaSim = run.BuildDeltaGameSim(moveOutcome.Events, moveOutcome.DirtyCardIds);

        Assert.True(fullSim.Cards.Count >= 9, $"全盘快照应包含 >= 9 张卡，实际: {fullSim.Cards.Count}");
        Assert.True(deltaSim.Cards.Count <= 2, $"单次储藏箱换位增量 GameSim 应仅含 1~2 张脏卡，实际: {deltaSim.Cards.Count}");
        Assert.True(deltaSim.Cards.ContainsKey(stashIds[0]));
        Assert.Equal(EContainerSocketId.Socket_8, run.Cards[stashIds[0]].Socket);
    }

    [Fact]
    public void Issue1_Hour6PvpDefeat_AlsoGrantsZeroCombatXpAndOnlyOneHourXpOnDayTransition()
    {
        var fx = Fx.Value;
        var (run, engine) = CreateStartedRun(EHero.Vanessa, seed: 13109);

        // 玩家空手牌直接把 Hour 1..5 推进至 Day 1 Hour 6（此时精确 4~5 XP，不打赢 Hour 3 也完全可以精确核账）
        run.Day = 1;
        run.Hour = 6;
        run.PlayerAttributes[EPlayerAttributeType.Level] = 1;
        run.PlayerAttributes[EPlayerAttributeType.Experience] = 6; // 故意留 6 XP，便于观察战败后是否恰好 6 + 1 = 7 XP（绝非 6 + 3 = 9 XP！）
        run.GainedHourXpThisHour = false;
        run.GainedAdvanceHourXpThisHour = false;
        var h6Shop = new ShopEngine(run, fx.Pool, fx.Repo);
        h6Shop.DealHourEncounters();

        string pvpCardId = run.SelectionSet.Single();
        var pvpRes = engine.Handle(new SelectEncounterCommand { InstanceId = new(pvpCardId) });
        Assert.Null(pvpRes.Error);
        Assert.NotNull(pvpRes.Combat);
        Assert.Equal(ECombatantId.Opponent, pvpRes.Combat!.Winner); // 空手牌必败于 Day 1 镜像

        // 无论 PvP 胜负，PvP 战斗本身 0 XP，仅跨日推进至 Day 2 Hour 1 给 +1 小时 XP -> 精确 7 XP
        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(7, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(7, TotalAccumulatedXp(run, fx.Mode));
    }

    [Fact]
    public void MoveItemAcceleration_HandMoveWithAura_DeltaGameSimCapturesAuraAffectedNeighbor()
    {
        var fx = Fx.Value;
        var (run, engine) = CreateStartedRun(EHero.Vanessa, seed: 13110);

        // 找一张带相邻光环（如给相邻物品加伤）的 Small/Medium 物品和一张普通武器 + 3 张无关储藏箱卡
        var auraTpl = fx.Repo.AllItems.FirstOrDefault(it =>
            it.CardSize == CardSize.Small &&
            it.Auras is { Count: > 0 } &&
            it.Auras.Values.Any(a => a.Action?.Target?.TargetMode is "Adjacent" or "RightOf" or "LeftOf"));
        var weaponTpl = fx.Repo.GetCard("1b955b8d-0ea6-4437-935c-d588be5898c1"); // Cutlass (Medium)
        Assert.NotNull(weaponTpl);

        var fillerItems = fx.Repo.GetSpawnableItemsForHero("Vanessa")
            .Where(it => it.CardSize == CardSize.Small && (it.Auras == null || it.Auras.Count == 0))
            .Take(4)
            .ToList();
        Assert.True(fillerItems.Count >= 4);

        // 储藏箱塞 4 张无关卡
        for (int i = 0; i < 4; i++)
        {
            var ci = new CardInstance(fillerItems[i], CardTier.Bronze);
            run.StashGrid.TryPlaceOrShift(ci, i);
            run.Cards[$"unrelated_stash_{i}"] = new RunState.CardEntry
            {
                InstanceId = $"unrelated_stash_{i}",
                TemplateId = Guid.Parse(fillerItems[i].Id),
                Type = ECardType.Item,
                Size = ECardSize.Small,
                Tier = ETier.Bronze,
                Owner = ECombatantId.Player,
                Section = EInventorySection.Stash,
                Socket = (EContainerSocketId)ci.StartSlot,
                CoreCard = ci,
            };
        }

        // 手牌放武器（Socket_3..4）+ 光环卡（Socket_6，最初不相邻）
        var wCi = new CardInstance(weaponTpl!, CardTier.Bronze);
        run.HandGrid.TryPlaceOrShift(wCi, 3);
        run.Cards["hand_weapon"] = new RunState.CardEntry
        {
            InstanceId = "hand_weapon",
            TemplateId = Guid.Parse(weaponTpl!.Id),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = wCi,
        };

        var moverTpl = auraTpl ?? fillerItems[0];
        var aCi = new CardInstance(moverTpl, CardTier.Bronze);
        run.HandGrid.TryPlaceOrShift(aCi, 6);
        run.Cards["hand_aura_mover"] = new RunState.CardEntry
        {
            InstanceId = "hand_aura_mover",
            TemplateId = Guid.Parse(moverTpl.Id),
            Type = ECardType.Item,
            Size = ECardSize.Small,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_6,
            CoreCard = aCi,
        };

        // 模拟真实对局前置状态：已存在的卡牌在移动前均已完成基础属性同步
        run.SyncAllEntriesFromCoreCardsAndDiff(fx.Repo.GameMode.StandardPrices);

        // 把光环卡从 Socket_6 挪到 Socket_5（紧贴 hand_weapon 右侧）
        var moveRes = engine.Handle(new MoveItemCommand
        {
            InstanceId = new("hand_aura_mover"),
            Section = EInventorySection.Hand,
            TargetSockets = [EContainerSocketId.Socket_5],
        });
        Assert.Null(moveRes.Error);

        var deltaSim = run.BuildDeltaGameSim(moveRes.Events, moveRes.DirtyCardIds);
        Assert.True(deltaSim.Cards.ContainsKey("hand_aura_mover"));
        // 4 张无关储藏箱卡一律不出现在增量回包中
        for (int i = 0; i < 4; i++)
            Assert.False(deltaSim.Cards.ContainsKey($"unrelated_stash_{i}"));
    }
}

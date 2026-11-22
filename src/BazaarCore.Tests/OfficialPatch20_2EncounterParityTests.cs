using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Data;
using BazaarServer.Game;
using Xunit;
using Xunit.Abstractions;

namespace BazaarCore.Tests;

/// <summary>
/// Patch 20.2（2026-11-20）全量官方事件天数窗、小时禁令、三槽位归属与 BazaarDB 21.3 万局实测权重综合验收测试。
/// 对应落盘档案：docs/recon/OFFICIAL_EVENTS_SPAWN_ATLAS.md &amp; official_events_spawn_registry.json。
/// </summary>
public sealed class OfficialPatch20_2EncounterParityTests
{
    private readonly ITestOutputHelper _output;

    public OfficialPatch20_2EncounterParityTests(ITestOutputHelper output) => _output = output;

    private sealed class RealFx
    {
        public CardPool Pool { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public CardArchetypeIndex ArchetypeIndex { get; init; } = null!;
    }

    private static readonly Lazy<RealFx> Fx = new(() =>
    {
        string? dataDir = null;
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir != null; i++)
        {
            string cand = Path.Combine(dir, "dist", "data");
            if (File.Exists(Path.Combine(cand, "GameData.db")))
            {
                dataDir = cand;
                break;
            }
            dir = Directory.GetParent(dir)?.FullName!;
        }
        if (dataDir == null && File.Exists(@"e:\thebazaar\dist\data\GameData.db"))
            dataDir = @"e:\thebazaar\dist\data";
        Assert.NotNull(dataDir);

        string dbPath = Path.Combine(dataDir!, "GameData.db");
        var repo = GameDatabaseLoader.Load(dbPath, null);
        var pool = CardPool.FromRepository(repo, dataDir);
        var idx = CardArchetypeIndex.Build(pool, repo);
        return new RealFx { Pool = pool, Repo = repo, ArchetypeIndex = idx };
    });

    private static CardPool.CardTemplate FindByName(CardPool pool, string internalName, string? type = null)
    {
        var list = type != null
            ? pool.OfType(type)
            : pool.OfType("EventEncounter").Concat(pool.OfType("PedestalEncounter"));
        var exact = list.FirstOrDefault(c =>
            !string.Equals(c.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.InternalName, internalName, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;
        var normKey = OfficialEncounterSchedule.NormalizeKey(internalName);
        var byNorm = list.FirstOrDefault(c =>
            !string.Equals(c.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase) &&
            OfficialEncounterSchedule.NormalizeKey(c.InternalName) == normKey);
        Assert.True(byNorm != null, $"未在 GameData.db 中找到遭遇: {internalName}");
        return byNorm!;
    }

    [Fact]
    public void ClosedDayWindows_HourBans_HeroOverrides_And_Pedestals_MatchPatch20_2()
    {
        var pool = Fx.Value.Pool;

        // 1. 全部 17 个核心封闭天数窗（既有最早天、又有截止天）
        (string Name, int[] AllowedSampleDays, int[] ForbiddenSampleDays)[] closedWindows =
        [
            ("Borrow (Day 1-2)",          [1, 2],       [3, 4, 6]),
            ("A Strange Mushroom",        [1, 2, 3],    [4, 5, 8]),
            ("Jungle Ruins",              [1, 2, 3],    [4, 6, 10]),
            ("Tiny Furry Creature",       [1, 2, 3],    [4, 5, 9]),
            ("The Lost Crate",            [1, 3, 5],    [6, 7, 10]),
            ("Partner Portal",            [2, 3, 5],    [1, 6, 8, 10]),
            ("Curio",                     [2, 3, 5],    [1, 6, 8]),
            ("Bex",                       [2, 3, 5],    [1, 6, 9]),
            ("Likit",                     [2, 3, 5],    [1, 6, 8]),
            ("The Travel Agent",          [2, 4, 5],    [1, 6, 9]),
            ("Silvia",                    [3, 4, 5],    [1, 2, 6, 9]),
            ("Economic Seminar",          [3, 4, 5],    [1, 2, 6, 8]),
            ("B1&B2",                     [3, 5],       [1, 2, 4, 6, 8]),
            ("The Docks",                 [4, 5, 7],    [1, 3, 8, 10]),
            ("Mountain Pass",             [4, 6, 7],    [1, 3, 8, 10]),
            ("Sterling",                  [6, 7, 8],    [1, 3, 5, 9, 10]),
            ("Grand Opening",             [3, 6, 9],    [1, 2, 10]),
        ];

        foreach (var (name, allowed, forbidden) in closedWindows)
        {
            string? type = name is "B1&B2" or "Sterling" ? "PedestalEncounter" : "EventEncounter";
            var tpl = FindByName(pool, name, type);
            foreach (int d in allowed)
            {
                int safeHour = name == "Tiny Furry Creature" && d == 1 ? 4 : 1;
                Assert.True(ShopEngine.IsWithinOfficialDayWindow(tpl, d, safeHour), $"{name} 应在 Day {d} Hour {safeHour} 允许出现");
            }
            foreach (int d in forbidden)
            {
                Assert.False(ShopEngine.IsWithinOfficialDayWindow(tpl, d, 4), $"{name} 不应在 Day {d} 出现");
            }
        }

        // 2. 其余 2 座常规地图基座（Forja Day 9+ / The Artist Day 8+）
        var forja = pool.Get(CardPool.PedestalForjaId)!;
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(forja, 4, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(forja, 8, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(forja, 9, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(forja, 12, 1));

        var artist = pool.Get(CardPool.PedestalTheArtistId)!;
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(artist, 6, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(artist, 7, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(artist, 8, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(artist, 10, 1));

        // 3. 官方小时禁令：Bex & Wink 禁用 Hour 4–5；Tiny Furry Creature 禁用 Day 1 Hour 1–2
        var bex = FindByName(pool, "Bex", "EventEncounter");
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(bex, 3, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(bex, 3, 2));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(bex, 3, 4));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(bex, 3, 5));

        var wink = FindByName(pool, "Wink", "EventEncounter");
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(wink, 4, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(wink, 5, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(wink, 5, 2));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(wink, 5, 4));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(wink, 5, 5));

        var tiny = FindByName(pool, "Tiny Furry Creature", "EventEncounter");
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(tiny, 1, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(tiny, 1, 2));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(tiny, 1, 4));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(tiny, 2, 1));

        // 4. 英雄差异解锁：Pol 对 Pygmalien 自 Day 4+ 解锁，其余英雄 Day 6+
        var pol = FindByName(pool, "Pol", "EventEncounter");
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(pol, 3, 1, HeroNames.Pygmalien));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(pol, 4, 1, HeroNames.Pygmalien));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(pol, 4, 1, HeroNames.Vanessa));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(pol, 5, 1, HeroNames.Vanessa));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(pol, 6, 1, HeroNames.Vanessa));

        // 5. Patch 20.2 Day 1 银商放行核验（10 名 Day 1 银商放行，Curio/Midsworth/Quixel/Barkun/Silvia/Orion 禁 Day 1）
        foreach (string allowedName in new[] { "Valpak", "Herma", "Shelter Shelby (Merchant)", "The Tester", "Tinker", "Eli", "Colt", "Nautica", "Aero", "Laurel" })
        {
            var m = FindByName(pool, allowedName, "EventEncounter");
            Assert.True(ShopEngine.IsWithinOfficialDayWindow(m, 1, 1), $"{allowedName} 应在 Day 1 放行");
        }
        foreach (string blockedName in new[] { "Curio", "Midsworth", "Quixel", "Barkun", "Silvia", "Orion" })
        {
            var m = FindByName(pool, blockedName, "EventEncounter");
            Assert.False(ShopEngine.IsWithinOfficialDayWindow(m, 1, 1), $"{blockedName} 不应在 Day 1 出现");
        }

        // 6. Patch 15.0/16.0/20.0 延迟解锁核验
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Farai", "EventEncounter"), 2, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Farai", "EventEncounter"), 1, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Uitar Center", "EventEncounter"), 3, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Uitar Center", "EventEncounter"), 2, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Wishing Fountain", "EventEncounter"), 1, 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Study (Default Option)", "EventEncounter"), 3, 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(FindByName(pool, "Study (Default Option)", "EventEncounter"), 4, 1));
    }

    [Fact]
    public void ThreeSlotPartitioning_And_ZeroWindowLeaks_AcrossAll8Heroes_Days1To10()
    {
        var fx = Fx.Value;
        var heroes = new[]
        {
            EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak,
            EHero.Stelle, EHero.Jules, EHero.Karnok, EHero.Hero8
        };

        foreach (var hero in heroes)
        {
            var run = new RunState
            {
                RunId = $"p202_8h_{hero}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = hero,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 202611 + (int)hero * 97,
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

                    Assert.Equal(3, run.SelectionSet.Count);
                    var dealt = run.SelectionSet.Select(id => run.Cards[id]).ToList();
                    var tpls = dealt.Select(c => fx.Pool.Get(c.TemplateId.ToString())!).ToList();

                    // 所有 3 个槽位均 100% 满足官方天数/小时窗且不含 12 座里程碑附魔台 / Partner Portal 子基座
                    foreach (var tpl in tpls)
                    {
                        Assert.DoesNotContain(tpl.Id, CardPool.NonHourlyMilestoneEnchantPedestalIds);
                        Assert.False(tpl.InternalName.StartsWith("Partner Portal - Forja", StringComparison.OrdinalIgnoreCase));
                        Assert.True(
                            ShopEngine.IsWithinOfficialDayWindow(tpl, (int)d, (int)h, hero.ToString()),
                            $"Hero={hero} Day {d} Hour {h} 越窗泄露: {tpl.InternalName} ({tpl.Id})");
                    }

                    // 严格三槽分区核验：Slot 0 = LeftMerchant, Slot 1 = MiddleEvent/MiddlePedestal, Slot 2 = RightFreeItem/RightUtility
                    var s0 = OfficialEncounterSchedule.ClassifySlot(tpls[0], CardArchetypeIndex.IsMerchantEncounterTemplate(tpls[0].Id, tpls[0].InternalName, tpls[0], fx.Repo.GetCard(tpls[0].Id)));
                    var s1 = OfficialEncounterSchedule.ClassifySlot(tpls[1], CardArchetypeIndex.IsMerchantEncounterTemplate(tpls[1].Id, tpls[1].InternalName, tpls[1], fx.Repo.GetCard(tpls[1].Id)));
                    var s2 = OfficialEncounterSchedule.ClassifySlot(tpls[2], CardArchetypeIndex.IsMerchantEncounterTemplate(tpls[2].Id, tpls[2].InternalName, tpls[2], fx.Repo.GetCard(tpls[2].Id)));

                    Assert.Equal(EOfficialEncounterSlot.LeftMerchant, s0);
                    Assert.True(s1 is EOfficialEncounterSlot.MiddleEvent or EOfficialEncounterSlot.MiddlePedestal,
                        $"Hero={hero} Day {d} Hour {h} 中槽类型异常: {tpls[1].InternalName} -> {s1}");
                    Assert.True(s2 is EOfficialEncounterSlot.RightFreeItem or EOfficialEncounterSlot.RightUtility,
                        $"Hero={hero} Day {d} Hour {h} 右槽类型异常: {tpls[2].InternalName} -> {s2}");

                    // 模拟记录当日全部出现过的模板防重复（近似测试单局演进）
                    if (ShopEngine.IsEffectiveSingleSpawnEncounter(tpls[1]))
                        run.VisitedSingleSpawnEncounterIds.Add(tpls[1].Id);
                    run.VisitedDayEncounterIds.Add(tpls[0].Id);
                    run.VisitedDayEncounterIds.Add(tpls[1].Id);
                    run.VisitedDayEncounterIds.Add(tpls[2].Id);
                }
            }
        }
    }

    [Fact]
    public void MonteCarlo_600Runs_EmpiricalDistribution_MatchesBazaarDB_Patch20_2()
    {
        var fx = Fx.Value;
        const int totalRuns = 600;
        var heroes = new[]
        {
            EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak,
            EHero.Stelle, EHero.Jules, EHero.Karnok, EHero.Hero8
        };

        // day (1..10) -> normalizedKey -> count of runs where seen on that day
        var seenByDay = new Dictionary<int, Dictionary<string, int>>();
        var slotSumsByDay = new Dictionary<int, int[]>(); // [0]=left, [1]=midEv, [2]=midPed, [3]=right
        for (int d = 1; d <= 10; d++)
        {
            seenByDay[d] = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            slotSumsByDay[d] = new int[4];
        }

        for (int r = 0; r < totalRuns; r++)
        {
            var hero = heroes[r % heroes.Length];
            var run = new RunState
            {
                RunId = $"mc_{r}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = hero,
                PlayMode = EPlayMode.Unranked,
                RngSeed = 900001 + r * 131,
                CustomRules = new CustomRunRuleSet(),
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo, run.CustomRules, fx.ArchetypeIndex);

            for (int d = 1; d <= 10; d++)
            {
                run.Day = (uint)d;
                run.VisitedDayEncounterIds.Clear();
                var seenTodayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (uint h in new uint[] { 1, 2, 4, 5 })
                {
                    run.Hour = h;
                    shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);

                    foreach (string instId in run.SelectionSet)
                    {
                        var ce = run.Cards[instId];
                        var tpl = fx.Pool.Get(ce.TemplateId.ToString())!;
                        string nk = OfficialEncounterSchedule.NormalizeKey(tpl.InternalName);
                        var slot = OfficialEncounterSchedule.ClassifySlot(tpl, CardArchetypeIndex.IsMerchantEncounterTemplate(tpl.Id, tpl.InternalName, tpl, fx.Repo.GetCard(tpl.Id)));

                        if (slot == EOfficialEncounterSlot.LeftMerchant) slotSumsByDay[d][0]++;
                        else if (slot == EOfficialEncounterSlot.MiddlePedestal) slotSumsByDay[d][2]++;
                        else if (slot is EOfficialEncounterSlot.RightFreeItem or EOfficialEncounterSlot.RightUtility) slotSumsByDay[d][3]++;
                        else slotSumsByDay[d][1]++;

                        if (seenTodayKeys.Add(nk))
                        {
                            seenByDay[d][nk] = seenByDay[d].GetValueOrDefault(nk) + 1;
                        }
                        // 同日去重模拟（避免同小时未选事件次小时反复狂刷）
                        run.VisitedDayEncounterIds.Add(tpl.Id);
                    }
                }
            }
        }

        double Pct(int day, string key) => 100.0 * seenByDay[day].GetValueOrDefault(key) / totalRuns;

        _output.WriteLine("=== 600-Run Monte Carlo Parity Check vs BazaarDB Patch 20.2 ===");
        for (int d = 1; d <= 10; d++)
        {
            double l = 100.0 * slotSumsByDay[d][0] / totalRuns;
            double me = 100.0 * slotSumsByDay[d][1] / totalRuns;
            double mp = 100.0 * slotSumsByDay[d][2] / totalRuns;
            double rt = 100.0 * slotSumsByDay[d][3] / totalRuns;
            _output.WriteLine($"Day {d,2}: Left={l:F1}% | Mid={me + mp:F1}% (Ev={me:F1}% Ped={mp:F1}%) | Right={rt:F1}%");
        }
        _output.WriteLine($"D1 Aila={Pct(1, "aila"):F1}% (BDB 59.7%) | Valpak={Pct(1, "valpak"):F1}% (BDB 33.8%) | Mushroom={Pct(1, "astrangemushroom"):F1}% (BDB 89.7%) | Wishing={Pct(1, "wishingfountain"):F1}% (BDB 31.9%)");
        _output.WriteLine($"D3 B1&B2={Pct(3, "b1b2"):F1}% (BDB 27.7%) | Curio={Pct(3, "curio"):F1}% (BDB 19.0%) | Silvia={Pct(3, "silvia"):F1}% (BDB 18.7%)");
        _output.WriteLine($"D4 Docks={Pct(4, "thedocks"):F1}% (BDB 59.4%) | MtnPass={Pct(4, "mountainpass"):F1}% (BDB 59.3%) | B1&B2={Pct(4, "b1b2"):F1}% (BDB 0%)");
        _output.WriteLine($"D6 Sterling={Pct(6, "sterling"):F1}% (BDB 44.6%) | Mandala={Pct(6, "mandala"):F1}% (BDB 44.5%) | Curio={Pct(6, "curio"):F1}% (BDB 0%)");
        _output.WriteLine($"D8 Sterling={Pct(8, "sterling"):F1}% (BDB 66.2%) | Artist={Pct(8, "theartist"):F1}% (BDB 1.8%) | RegTinc={Pct(8, "regenerativetincture"):F1}% (BDB 65.9%) | Study={Pct(8, "study"):F1}% (BDB 65.9%)");
        _output.WriteLine($"D9 Forja={Pct(9, "forja"):F1}% (BDB 1.6%) | Sterling={Pct(9, "sterling"):F1}% (BDB 0%)");

        // 核心分布断言（精确验证三槽平衡、封闭窗清零与关键曲线趋势）
        Assert.InRange(100.0 * slotSumsByDay[1][0] / totalRuns, 395.0, 405.0);
        Assert.InRange(100.0 * (slotSumsByDay[3][1] + slotSumsByDay[3][2]) / totalRuns, 395.0, 405.0);
        Assert.InRange(100.0 * slotSumsByDay[8][3] / totalRuns, 395.0, 405.0);

        // Day 1 锚点：无基座、无 Curio/Midsworth、高频青铜商 & 青铜奇遇 & Wishing Fountain 正常出现
        Assert.Equal(0, seenByDay[1].GetValueOrDefault("curio"));
        Assert.Equal(0, seenByDay[1].GetValueOrDefault("midsworth"));
        Assert.Equal(0, slotSumsByDay[1][2]);
        Assert.InRange(Pct(1, "aila"), 45.0, 75.0);
        Assert.InRange(Pct(1, "astrangemushroom"), 65.0, 98.0);
        Assert.InRange(Pct(1, "wishingfountain"), 15.0, 50.0);

        // Day 3–4 锚点：B1&B2 仅 Day 3/5 出现、Day 4 清零且 Docks/Mountain Pass 强势上线
        Assert.InRange(Pct(3, "b1b2"), 15.0, 40.0);
        Assert.Equal(0, seenByDay[4].GetValueOrDefault("b1b2"));
        Assert.Equal(0, seenByDay[4].GetValueOrDefault("astrangemushroom"));
        Assert.InRange(Pct(4, "thedocks"), 35.0, 80.0);
        Assert.InRange(Pct(4, "mountainpass"), 35.0, 80.0);

        // Day 6–9 锚点：Sterling D6–8 强势上线并在 D9 归零，Forja D9 解锁；右槽 D8 四大保底柱石飙升
        Assert.InRange(Pct(6, "sterling"), 30.0, 60.0);
        Assert.InRange(Pct(8, "sterling"), 48.0, 80.0);
        Assert.Equal(0, seenByDay[9].GetValueOrDefault("sterling"));
        Assert.Equal(0, seenByDay[8].GetValueOrDefault("forja"));
        Assert.True(seenByDay[9].GetValueOrDefault("forja") + seenByDay[10].GetValueOrDefault("forja") > 0, "Forja 应在 Day 9–10 合法出现");
        Assert.Equal(0, seenByDay[6].GetValueOrDefault("curio"));
        Assert.Equal(0, seenByDay[6].GetValueOrDefault("thelostcrate"));
        Assert.Equal(0, seenByDay[7].GetValueOrDefault("investinyourself"));
        Assert.InRange(Pct(8, "regenerativetincture"), 45.0, 85.0);
        Assert.InRange(Pct(8, "study"), 45.0, 85.0);
    }

    [Fact]
    public void ShopTierOdds_Days1To16_MatchBazaarDB_Patch20_2_And_AllowCustomModeOverride()
    {
        var repo = Fx.Value.Repo;
        var pool = Fx.Value.Pool;
        var chances = pool.CreateTierChances();

        // 1. Patch 20.2 Day 1–16 官方品阶曲线：Day 1–9 递增 + Day 10–16 稳态 (0 / 0.20 / 0.70 / 0.10)
        (int Day, double B, double S, double G, double D)[] expected =
        [
            (1,  1.00, 0.00, 0.00, 0.00),
            (2,  0.90, 0.10, 0.00, 0.00),
            (3,  0.70, 0.30, 0.00, 0.00),
            (4,  0.50, 0.50, 0.00, 0.00),
            (5,  0.25, 0.75, 0.00, 0.00),
            (6,  0.00, 0.95, 0.05, 0.00),
            (7,  0.00, 0.80, 0.20, 0.00),
            (8,  0.00, 0.60, 0.40, 0.00),
            (9,  0.00, 0.35, 0.60, 0.05),
            (10, 0.00, 0.20, 0.70, 0.10),
            (11, 0.00, 0.20, 0.70, 0.10),
            (12, 0.00, 0.20, 0.70, 0.10),
            (13, 0.00, 0.20, 0.70, 0.10),
            (14, 0.00, 0.20, 0.70, 0.10),
            (15, 0.00, 0.20, 0.70, 0.10),
            (16, 0.00, 0.20, 0.70, 0.10),
        ];

        foreach (var (day, b, s, g, d) in expected)
        {
            var tc = chances.GetWeightsForDay(day);
            Assert.Equal(b, tc.Bronze, 6);
            Assert.Equal(s, tc.Silver, 6);
            Assert.Equal(g, tc.Gold, 6);
            Assert.Equal(d, tc.Diamond, 6);

            var sq = SpawnQueryEngine.GetDayTierWeights(repo.GameMode, day);
            Assert.Equal(b, sq.Bronze, 6);
            Assert.Equal(s, sq.Silver, 6);
            Assert.Equal(g, sq.Gold, 6);
            Assert.Equal(d, sq.Diamond, 6);
        }

        // 2. 自定义 Mode 显式扩天（NumberOfDays=15）并自定义 Day 11–15 品阶表时不被 Day 10 强行覆盖
        var customMode = new GameModeData
        {
            Id = "custom-mode-15d",
            NumberOfDays = 15,
            ItemSkillSpawnTierPercantagesByDay = new Dictionary<string, Dictionary<string, double>>
            {
                ["10"] = new() { ["Bronze"] = 0.0, ["Silver"] = 0.20, ["Gold"] = 0.70, ["Diamond"] = 0.10 },
                ["15"] = new() { ["Bronze"] = 0.0, ["Silver"] = 0.00, ["Gold"] = 0.40, ["Diamond"] = 0.60 },
            },
        };
        var customSq15 = SpawnQueryEngine.GetDayTierWeights(customMode, 15);
        Assert.Equal(0.40, customSq15.Gold, 6);
        Assert.Equal(0.60, customSq15.Diamond, 6);

        var customTc = new TierChances(TierChances.FromGameModeMap(customMode.ItemSkillSpawnTierPercantagesByDay, customMode.NumberOfDays));
        var customTc15 = customTc.GetWeightsForDay(15);
        Assert.Equal(0.40, customTc15.Gold, 6);
        Assert.Equal(0.60, customTc15.Diamond, 6);

        // 3. Mod 显式 ModMinDay/ModMaxDay 优先于官方静态天数窗（可收窄亦可放宽）
        var modCurioExpanded = new CardPool.CardTemplate
        {
            Id = "mod-test:curio_all_days",
            InternalName = "Curio",
            Type = "EventEncounter",
            StartingTier = "Silver",
            Tags = ["Merchant"],
            ModMinDay = 1,
            ModMaxDay = 10,
        };
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(modCurioExpanded, day: 1, hour: 1));
        Assert.True(ShopEngine.IsWithinOfficialDayWindow(modCurioExpanded, day: 8, hour: 1));
        Assert.False(ShopEngine.IsWithinOfficialDayWindow(modCurioExpanded, day: 11, hour: 1));
    }
}

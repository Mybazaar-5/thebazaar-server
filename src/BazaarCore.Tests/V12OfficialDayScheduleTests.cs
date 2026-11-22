using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V12 官方每日怪物调度（Day 1..10+ 精选三槽名册 &amp; Hour 1/2/4/5 三槽对齐 &amp; 同日去重 &amp; 动态 ExclusionSet）
/// 与服务端 #19（父级遭遇头像 ParentEncounterTemplateId 跨子步同步、挂起恢复、新旧快照前后向兼容及战前零残留清理）综合测试套件。
/// </summary>
public sealed class V12OfficialDayScheduleTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string BayonetId = "6146cacd-202c-489f-88c0-f77ef3e0fda6";
    private const string TheDocksId = "11287697-dd45-4edc-9539-780a26001043";
    private const string WorkAsMaintenanceStepId = "0398b2d1-010d-43e6-9cf5-b2b6eabe8cf3";
    private const string WorkAsSecurityStepId = "38056994-a881-4f78-a91a-aeb1bc7d7f61";
    private const string LibraryId = "48a25657-baf6-440d-9085-bd7aeda1638a";
    private const string BurnItDownStepId = "38ec2202-644e-42d5-8d6b-7476e7df9f42";
    private const string BurningLibraryId = "bea25390-0ec6-45fd-be00-d07649d7586a";
    private const string ShroudedFigureId = "818fcd1d-4cfd-4629-b83f-8d5e753dd8f4";
    private const string TravelAgentId = CardPool.TravelAgentEncounterId;

    // Day 4 合法小型青铜灼烧物品（满足 Burn it Down 至少 3 件 Burn 物品的前置需求）
    private const string PopSnappersId = "13f852ba-1d3d-4c1a-b6db-79072e0b19d0";
    private const string SteamLadleId = "194572c8-39b0-4dea-92d5-2c97f8d59554";
    private const string BlowTorchId = "302d710c-335d-4fd4-af3c-34789e55acd3";

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
        public HashSet<string> ValidCardIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<Fx> RealFixture = new(() =>
    {
        string? dataDir = Environment.GetEnvironmentVariable("BAZAAR_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
        {
            string? probe = AppContext.BaseDirectory;
            for (int i = 0; i < 10 && !string.IsNullOrEmpty(probe); i++, probe = Directory.GetParent(probe)?.FullName)
            {
                string candidate = Path.Combine(probe, "dist", "data");
                if (File.Exists(Path.Combine(candidate, "GameData.db")))
                {
                    dataDir = candidate;
                    break;
                }
            }
        }
        if (string.IsNullOrWhiteSpace(dataDir) || !File.Exists(Path.Combine(dataDir, "GameData.db")))
            dataDir = @"e:\thebazaar\dist\data";

        string dbPath = Path.Combine(dataDir, "GameData.db");
        var validIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string wlPath = Path.Combine(dataDir, "valid_card_ids.json");
        if (File.Exists(wlPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(wlPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("valid_ids", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    string? id = el.GetString();
                    if (!string.IsNullOrWhiteSpace(id)) validIds.Add(id);
                }
            }
        }

        return new Fx
        {
            DataDir = dataDir,
            DbPath = dbPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, null),
            Ghosts = PvpGhostPool.Load(dataDir),
            ValidCardIds = validIds,
        };
    });

    /// <summary>独立复制 GameData.db + 白名单写入合成遭遇/子步模板的临时测试夹具，绝不污染真实资产。</summary>
    private sealed class OverlayFixture : IDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayFixture(params (string Id, string Json)[] customCards)
        {
            var real = RealFixture.Value;
            TempDir = Path.Combine(Path.GetTempPath(), "bazaar_v12_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            DbPath = Path.Combine(TempDir, "GameData.db");
            File.Copy(real.DbPath, DbPath, overwrite: true);

            var wl = new HashSet<string>(real.ValidCardIds, StringComparer.OrdinalIgnoreCase);
            using (var conn = new SqliteConnection($"Data Source={DbPath}"))
            {
                conn.Open();
                foreach (var (id, json) in customCards)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "INSERT OR REPLACE INTO cards (Id, Data) VALUES ($id, $data)";
                    cmd.Parameters.AddWithValue("$id", id);
                    cmd.Parameters.AddWithValue("$data", Encoding.UTF8.GetBytes(json));
                    cmd.ExecuteNonQuery();
                    wl.Add(id);
                }
            }

            File.WriteAllText(
                Path.Combine(TempDir, "valid_card_ids.json"),
                JsonSerializer.Serialize(new { valid_ids = wl.ToArray() }));

            Pool = CardPool.Load(DbPath);
            Monsters = real.Monsters;
            LevelUps = real.LevelUps;
            Mode = real.Mode;
            Repo = GameDatabaseLoader.Load(DbPath, null);
            Ghosts = real.Ghosts;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(TempDir))
            {
                try { Directory.Delete(TempDir, recursive: true); } catch { }
            }
        }
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        CustomRunRuleSet? rules = null,
        int seed = 1201)
    {
        var run = new RunState
        {
            RunId = "v12_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId != Guid.Empty ? pool.DefaultGameModeId : Guid.NewGuid(),
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            RngSeed = seed,
            CustomRules = rules ?? new CustomRunRuleSet(),
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
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
        run.State = ERunState.Choice;
        run.CurrentEncounterId = null;
        run.SelectionSet.Clear();
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        GameDataRepository repo,
        string templateId,
        EInventorySection section,
        int slot,
        ETier tier = ETier.Bronze)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, $"owned_{Guid.NewGuid():N}")
        {
            StartSlot = slot,
        };
        var grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        grid.UnlockedMask = section == EInventorySection.Stash ? 1023 : run.UnlockedSlots;
        Assert.True(grid.TryPlaceOrShift(core, slot), $"向 {section}:{slot} 添加物品 {templateId} 失败");

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)slot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, null, coreTpl);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    // ── 1. 官方精选怪表（Day 1..13+）静态全量校验 ─────────────────────────────

    [Fact(DisplayName = "V12_01_OfficialDaySchedule_AllCuratedGuids_ExistAssetValidPlayableWithNonEmptyBoard")]
    public void V12_01_OfficialDaySchedule_AllCuratedGuids_ExistAssetValidPlayableWithNonEmptyBoard()
    {
        var fx = RealFixture.Value;
        Assert.NotEmpty(fx.ValidCardIds);

        for (int day = 1; day <= 12; day++)
        {
            Assert.True(OfficialDaySchedule.CuratedDayRosters.ContainsKey(day), $"缺少 Day {day} 官方精选怪表");
        }

        var rosters = OfficialDaySchedule.CuratedDayRosters.Values.Append(OfficialDaySchedule.LateGameRoster).ToList();
        foreach (var roster in rosters)
        {
            Assert.NotEmpty(roster.LeftIds);
            Assert.NotEmpty(roster.MidIds);
            Assert.NotEmpty(roster.RightIds);

            void AssertSlotIds(EMonsterSlotTier slot, IReadOnlyList<string> ids)
            {
                var (minHp, maxHp) = OfficialDaySchedule.GetRecommendedHealthBand(roster.Day, slot);
                foreach (string id in ids)
                {
                    var tpl = fx.Pool.Get(id);
                    Assert.NotNull(tpl);
                    Assert.Equal("CombatEncounter", tpl!.Type, StringComparer.OrdinalIgnoreCase);
                    Assert.Equal("Always", tpl.SpawningEligibility, StringComparer.OrdinalIgnoreCase);
                    Assert.True(fx.Pool.IsAssetValid(id), $"精选怪物 {id} ({tpl.InternalName}) 未通过 IsAssetValid");
                    Assert.Contains(id, fx.ValidCardIds);
                    Assert.False(MonsterPool.IsExcludedDevOrTestMonsterName(tpl.InternalName), $"出现调试怪名称: {tpl.InternalName}");

                    Assert.True(MonsterPool.IsPlayableCombatEncounter(tpl, fx.Pool, fx.Repo, requireAlwaysSpawning: true),
                        $"Day {roster.Day} {slot} 怪物 {tpl.InternalName} ({id}) 未通过 IsPlayableCombatEncounter");

                    Assert.True(fx.Repo.MonstersById.TryGetValue(tpl.MonsterTemplateId!, out var monster));
                    Assert.NotEmpty(monster!.Player.Hand.Items);
                    int hp = MonsterPool.GetMonsterHealthMax(tpl, fx.Repo);
                    Assert.InRange(hp, 31, 9999);
                    Assert.InRange(hp, minHp, maxHp);
                }
            }

            AssertSlotIds(EMonsterSlotTier.LeftBronze, roster.LeftIds);
            AssertSlotIds(EMonsterSlotTier.MidSilver, roster.MidIds);
            AssertSlotIds(EMonsterSlotTier.RightGold, roster.RightIds);
        }
    }

    // ── 2. Day 1..14 Hour 3 发怪、Day 1 初始武器可胜性、以及全禁用时的 MonsterPool 回退 ──

    [Fact(DisplayName = "V12_02_Hour3Deals_Day1To14_MonotonicAssetValid_Day1Winnable_AndFallback")]
    public void V12_02_Hour3Deals_Day1To14_MonotonicAssetValid_Day1Winnable_AndFallback()
    {
        var fx = RealFixture.Value;

        for (int day = 1; day <= 14; day++)
        {
            for (int seed = 0; seed < 5; seed++)
            {
                var run = new RunState
                {
                    RunId = $"v12_d{day}_{seed}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    PlayMode = EPlayMode.Ranked,
                    Day = (uint)day,
                    Hour = 3,
                    RngSeed = 4000 + day * 37 + seed,
                };
                var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                var events = shop.DealHourEncounters();
                Assert.NotEmpty(events);
                Assert.Equal(3, run.SelectionSet.Count);

                var dealtTemplates = run.SelectionSet
                    .Select(id => run.Cards[id])
                    .ToList();

                // 3 只严格去重，100% 为资产合法 CombatEncounter 且非调试怪
                Assert.Equal(3, dealtTemplates.Select(c => c.TemplateId).Distinct().Count());
                var metas = dealtTemplates.Select(c =>
                {
                    Assert.Equal(ECardType.CombatEncounter, c.Type);
                    string tid = c.TemplateId.ToString();
                    var tpl = fx.Pool.Get(tid)!;
                    Assert.True(fx.Pool.IsAssetValid(tid));
                    Assert.Contains(tid, fx.ValidCardIds);
                    Assert.True(MonsterPool.IsPlayableCombatEncounter(tpl, fx.Pool, fx.Repo));

                    int hp = MonsterPool.GetMonsterHealthMax(tpl, fx.Repo);
                    int lv = MonsterPool.GetEffectiveMonsterLevel(tpl, fx.Repo);
                    var tier = MonsterPool.GetMonsterTier(tpl);
                    return (Tpl: tpl, Hp: hp, Lv: lv, Tier: tier);
                }).ToList();

                // V13.1 官方公式：左 <= 中 <= 右 品阶稀有度与等级单调递增（不再强制伪血量单调，保护 Viper 75HP / Drone Operator 850HP / Lich 1800HP 等官方低血高阶怪）
                Assert.True(metas[0].Tier <= metas[1].Tier && metas[1].Tier <= metas[2].Tier,
                    $"Day {day} 品阶倒挂: {metas[0].Tpl.InternalName}({metas[0].Tier}), {metas[1].Tpl.InternalName}({metas[1].Tier}), {metas[2].Tpl.InternalName}({metas[2].Tier})");
                Assert.True(metas[0].Lv <= metas[1].Lv && metas[1].Lv <= metas[2].Lv,
                    $"Day {day} Lv 倒挂: {metas[0].Lv}, {metas[1].Lv}, {metas[2].Lv}");
                Assert.All(metas, m => Assert.InRange(m.Hp, 75, 9999));

                if (day == 1)
                {
                    Assert.Equal(1, metas[0].Lv);
                    Assert.Equal(1, metas[1].Lv);
                    Assert.Equal(2, metas[2].Lv);
                }
                else if (day is >= 2 and <= 10)
                {
                    Assert.Equal(day, metas[0].Lv);
                    Assert.Equal(day + 1, metas[1].Lv);
                    Assert.Equal(day + 2, metas[2].Lv);
                }
                else if (day == 11)
                {
                    Assert.Equal(11, metas[0].Lv);
                    Assert.Equal(12, metas[1].Lv);
                    Assert.Equal(13, metas[2].Lv);
                }
                else
                {
                    Assert.Equal(12, metas[0].Lv);
                    Assert.Equal(13, metas[1].Lv);
                    Assert.Equal(15, metas[2].Lv);
                }
            }
        }

        // Day 1 Left & Mid 全部精选怪均应被起手青铜 Cutlass + Bayonet 稳定击败
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;
        var bayonetTpl = fx.Repo.GetCard(BayonetId)!;
        var day1Roster = OfficialDaySchedule.GetRosterForDay(1);
        foreach (string mobId in day1Roster.LeftIds.Concat(day1Roster.MidIds).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mobTpl = fx.Pool.Get(mobId)!;
            var monster = fx.Monsters.First(m => string.Equals(m.Id, mobTpl.MonsterTemplateId, StringComparison.OrdinalIgnoreCase));

            var p1 = new PlayerState("p1", "Vanessa", "Vanessa", 250, 20, 0, 10) { Level = 1 };
            p1.Hand.UnlockedMask = 0b00_0111_1000;
            p1.Hand.TryPlaceOrShift(new CardInstance(cutlassTpl, CardTier.Bronze, null, "d1_cutlass"), 3);
            p1.Hand.TryPlaceOrShift(new CardInstance(bayonetTpl, CardTier.Bronze, null, "d1_bayonet"), 5);
            p1.PrepareForCombat();

            var p2 = new PlayerState("p2", monster.InternalName, "Common", monster.HealthMax, monster.Prestige, 0, 10)
            {
                Level = Math.Max(1, mobTpl.MonsterLevel),
            };
            p2.Hand.UnlockedMask = 1023;
            foreach (var mi in monster.Items)
            {
                var mit = fx.Repo.GetCard(mi.TemplateId);
                if (mit == null) continue;
                int slot = int.TryParse(mi.SocketId?.Replace("Socket_", ""), out int s) ? s : 0;
                var ci = new CardInstance(mit, TierHelper.ParseTier(mi.Tier ?? "Bronze"), mi.EnchantmentType);
                if (!p2.Hand.TryPlaceOrShift(ci, slot)) p2.Hand.TryAddAuto(ci);
            }
            foreach (var sk in monster.Skills)
            {
                var st = fx.Repo.GetCard(sk.TemplateId);
                if (st != null) p2.Skills.Add(new CardInstance(st, TierHelper.ParseTier(sk.Tier ?? "Bronze")));
            }
            p2.PrepareForCombat();

            var simRes = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 77, combatType: "PVE");
            Assert.True(simRes.WinnerPlayerId == "p1",
                $"Day 1 初始武器应稳定击败 {monster.InternalName} (HP={monster.HealthMax}), 实际胜者={simRes.WinnerPlayerId}");
        }

        // 回退测试：禁用 Day 4 全部精选 GUID，循环多组确定性种子验证必须回退到 MonsterPool 同级合法怪，
        // 且依然产出 3 只不重复、非禁用怪，并满足官方「品阶升序 + 等级升序 + 合法血量区间」（不再要求伪血量单调）
        var bannedDay4 = OfficialDaySchedule.GetRosterForDay(4).AllIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var banRules = new CustomRunRuleSet
        {
            BannedEncounterIds = bannedDay4.ToList(),
        }.CloneNormalized();
        int[] fallbackSeeds = [120401, 120402, 120403, 120404, 120405, 120406, 120407, 120408];
        foreach (int fbSeed in fallbackSeeds)
        {
            var fallbackRun = new RunState
            {
                RunId = $"v12_fallback_d4_{fbSeed}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                PlayMode = EPlayMode.Ranked,
                Day = 4,
                Hour = 3,
                RngSeed = fbSeed,
                CustomRules = banRules,
            };
            var fallbackShop = new ShopEngine(fallbackRun, fx.Pool, fx.Repo, banRules);
            fallbackShop.DealHourEncounters();

            Assert.Equal(3, fallbackRun.SelectionSet.Count);
            var fbEntries = fallbackRun.SelectionSet.Select(id => fallbackRun.Cards[id]).ToList();
            Assert.Equal(3, fbEntries.Select(e => e.TemplateId).Distinct().Count());
            foreach (var e in fbEntries)
            {
                string tid = e.TemplateId.ToString();
                Assert.DoesNotContain(tid, bannedDay4);
                Assert.True(MonsterPool.IsPlayableCombatEncounter(fx.Pool.Get(tid), fx.Pool, fx.Repo));
            }
            int[] fbHps = fbEntries.Select(e => MonsterPool.GetMonsterHealthMax(fx.Pool.Get(e.TemplateId.ToString()), fx.Repo)).ToArray();
            int[] fbLvs = fbEntries.Select(e => MonsterPool.GetEffectiveMonsterLevel(fx.Pool.Get(e.TemplateId.ToString()), fx.Repo)).ToArray();
            var fbTiers = fbEntries.Select(e => MonsterPool.GetMonsterTier(fx.Pool.Get(e.TemplateId.ToString()))).ToArray();
            Assert.True(fbTiers[0] <= fbTiers[1] && fbTiers[1] <= fbTiers[2],
                $"Day 4 fallback(seed={fbSeed}) 品阶倒挂: {fbTiers[0]}, {fbTiers[1]}, {fbTiers[2]}");
            Assert.True(fbLvs[0] <= fbLvs[1] && fbLvs[1] <= fbLvs[2],
                $"Day 4 fallback(seed={fbSeed}) Lv 倒挂: {fbLvs[0]}, {fbLvs[1]}, {fbLvs[2]}");
            Assert.All(fbHps, hp => Assert.InRange(hp, 75, 9999));
        }
    }

    // ── 3. Hour 1/2/4/5 三槽对齐、100 天同天不重复、TRunConditionalCurrentDay 与动态 ExclusionSet ──

    [Fact(DisplayName = "V12_03_Hour1245Breakdown_100DaysSameDayDedup_CurrentDayGating_AndDynamicExclusionSet")]
    public void V12_03_Hour1245Breakdown_100DaysSameDayDedup_CurrentDayGating_AndDynamicExclusionSet()
    {
        var fx = RealFixture.Value;
        EHero[] heroes = [EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak];
        int[] encounterHours = [1, 2, 4, 5];

        // 连续模拟 100 天（每名英雄轮换 25 天）验证 Hour 1,2,4,5 遭遇结构与同日不重复
        for (int simDay = 0; simDay < 100; simDay++)
        {
            int day = (simDay % 10) + 1;
            var hero = heroes[simDay % heroes.Length];
            var run = new RunState
            {
                RunId = $"v12_sim100_{simDay}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = hero,
                PlayMode = EPlayMode.Ranked,
                Day = (uint)day,
                Hour = 1,
                RngSeed = 9000 + simDay,
            };
            run.PlayerAttributes[EPlayerAttributeType.Level] = Math.Max(1, day);
            run.PlayerAttributes[EPlayerAttributeType.Gold] = 20;
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);

            var selectedNonMerchantsToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedMerchantsToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (int hr in encounterHours)
            {
                run.Hour = (uint)hr;
                shop.DealHourEncounters(playerState: null, visitedTodayIds: run.VisitedDayEncounterIds);

                Assert.Equal(3, run.SelectionSet.Count);
                var cards = run.SelectionSet.Select(id => run.Cards[id]).ToList();
                Assert.Equal(3, cards.Select(c => c.TemplateId).Distinct().Count());

                int merchantCount = 0;
                RunState.CardEntry? pickedMerchant = null;
                RunState.CardEntry? pickedEvent = null;
                int unvisitedNonMerchantInDeal = 0;

                foreach (var c in cards)
                {
                    Assert.True(c.Type is ECardType.EventEncounter or ECardType.PedestalEncounter,
                        $"Day {day} Hour {hr} 不应发出战斗卡: {c.Type}");

                    string tid = c.TemplateId.ToString();
                    var tpl = fx.Pool.Get(tid);
                    Assert.True(fx.Pool.IsAssetValid(tid));

                    bool isMerchant = CardArchetypeIndex.IsMerchantEncounterTemplate(tid, tpl?.InternalName ?? "", tpl, fx.Repo.GetCard(tid));
                    if (isMerchant)
                    {
                        merchantCount++;
                        pickedMerchant ??= c;
                        Assert.DoesNotContain(tid, selectedMerchantsToday);
                    }
                    else
                    {
                        pickedEvent ??= c;
                        if (!selectedNonMerchantsToday.Contains(tid))
                            unvisitedNonMerchantInDeal++;
                        // Day >= 2 解锁白银池后同天非商人池远超 8 槽，必须全程严格零重复；
                        // Day 1 全库通用青铜非商人仅 3 张，仅允许全池耗尽后的槽位兜底，但在未耗尽前必有未访问新卡优先出现
                        if (day >= 2)
                            Assert.DoesNotContain(tid, selectedNonMerchantsToday);
                    }

                    // 验证天数阶级门槛（V13.1：Day 1 Hour 1 无基座，Hour 2/4/5 仅允许 B1&B2 基座；严禁 Shrouded Figure / The Travel Agent 提前泄露）
                    if (day == 1)
                    {
                        if (hr == 1)
                            Assert.NotEqual(ECardType.PedestalEncounter, c.Type);
                        else if (c.Type == ECardType.PedestalEncounter)
                            Assert.Equal(CardPool.PedestalB1B2Id, tid, StringComparer.OrdinalIgnoreCase);
                        Assert.NotEqual(ShroudedFigureId, tid, StringComparer.OrdinalIgnoreCase);
                        Assert.NotEqual(TravelAgentId, tid, StringComparer.OrdinalIgnoreCase);
                    }
                }

                // Day 2..9 允许 The Travel Agent 合法入池且豁免 maxDayTier
                if (day is >= 2 and <= 9 && hr == 1)
                {
                    var taCheckRun = new RunState
                    {
                        RunId = $"v12_ta_d{day}",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = EHero.Vanessa,
                        PlayMode = EPlayMode.Ranked,
                        Day = (uint)day,
                        Hour = 1,
                        RngSeed = 7000 + day,
                    };
                    var taRules = new CustomRunRuleSet
                    {
                        EncounterCategoryMultipliers = new(StringComparer.OrdinalIgnoreCase)
                        {
                            [nameof(EEncounterWeightCategory.EventEconomyStats)] = 0.0,
                            [nameof(EEncounterWeightCategory.EventFreeDealTransform)] = 0.0,
                            [nameof(EEncounterWeightCategory.EventCultAndMystery)] = 0.0,
                            [nameof(EEncounterWeightCategory.EventExpeditionAgent)] = 8.0,
                        }
                    };
                    new ShopEngine(taCheckRun, fx.Pool, fx.Repo, taRules).DealHourEncounters();
                    Assert.Contains(
                        taCheckRun.SelectionSet.Select(id => taCheckRun.Cards[id].TemplateId.ToString()),
                        tid => string.Equals(tid, TravelAgentId, StringComparison.OrdinalIgnoreCase));
                }

                // V13.1 官方常规 3 槽布局：1..2 家商人 + 1..2 非商人发育（支持双商人，至少保留 1 处非商人遭遇）
                Assert.InRange(merchantCount, 1, 2);
                Assert.NotNull(pickedEvent);
                if (selectedNonMerchantsToday.Count < 3)
                    Assert.True(unvisitedNonMerchantInDeal >= 1, $"Day {day} Hour {hr} 未耗尽青铜池时不应跳过未访问非商人遭遇");

                // 模拟玩家在当小时选中其中 1 张遭遇（轮流模拟选商人/选事件）并记录进当日已访问集
                var toVisit = (hr % 2 == 1 ? pickedEvent : (pickedMerchant ?? pickedEvent))!;
                string visitedId = toVisit.TemplateId.ToString();
                if (toVisit == pickedMerchant)
                    selectedMerchantsToday.Add(visitedId);
                else
                    selectedNonMerchantsToday.Add(visitedId);
                run.VisitedDayEncounterIds.Add(visitedId);
            }
        }

        // ── 验证 Burn it Down 在 RunEngine 执行后动态排除 Library 并解锁 Burning Library ──
        var (exclRun, exclEngine) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, EHero.Vanessa, seed: 555);
        exclRun.Day = 4;
        exclRun.Hour = 1;
        exclRun.PlayerAttributes[EPlayerAttributeType.Level] = 4;
        AddOwnedItem(exclRun, fx.Repo, PopSnappersId, EInventorySection.Hand, 3);
        AddOwnedItem(exclRun, fx.Repo, SteamLadleId, EInventorySection.Hand, 4);
        AddOwnedItem(exclRun, fx.Repo, BlowTorchId, EInventorySection.Hand, 5);

        // 确认初始状态 Burning Library 在 ExclusionSet 内、Library 不在其中
        var preShop = new ShopEngine(exclRun, fx.Pool, fx.Repo);
        preShop.DealHourEncounters();
        Assert.Contains(BurningLibraryId, exclRun.ExclusionSet);
        Assert.DoesNotContain(LibraryId, exclRun.ExclusionSet);

        // 进入 Library 并将 Burn it Down 注入货架执行（验证 Burn >= 3 前置条件与动态 ExclusionSet 更新）
        var libEntry = InjectChoiceEncounter(exclRun, fx.Pool, LibraryId);
        var enterLibOut = exclEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(libEntry.InstanceId) });
        Assert.Null(enterLibOut.Error);

        var burnStepTpl = fx.Pool.Get(BurnItDownStepId)!;
        var burnCand = preShop.CreateEncounterCandidate(burnStepTpl, ECardType.EncounterStep, "step");
        exclRun.Cards[burnCand.InstanceId] = burnCand;
        exclRun.SelectionSet.Clear();
        exclRun.SelectionSet.Add(burnCand.InstanceId);

        var execBurnOut = exclEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(burnCand.InstanceId) });
        Assert.Null(execBurnOut.Error);

        Assert.Contains(LibraryId, exclRun.ExclusionSet);
        Assert.DoesNotContain(BurningLibraryId, exclRun.ExclusionSet);

        // 快照往返序列化后 ExclusionSet 动态状态保持不丢且不被默认 GameMode 覆盖
        string exclJson = RunSnapshotSerializer.Serialize(exclRun);
        var exclRestored = RunSnapshotSerializer.Deserialize(exclJson, fx.Repo);
        Assert.NotNull(exclRestored);
        Assert.True(exclRestored!.ExclusionSetInitialized);
        Assert.Contains(LibraryId, exclRestored.ExclusionSet);
        Assert.DoesNotContain(BurningLibraryId, exclRestored.ExclusionSet);

        // 后续在 Day 4 多次抽选 Hour 遭遇，Library 永远不会再刷出，而 Burning Library 在常规池中可合法候选
        var postShop = new ShopEngine(exclRestored, fx.Pool, fx.Repo);
        for (int i = 0; i < 20; i++)
        {
            exclRestored.Day = 4;
            exclRestored.Hour = 1;
            postShop.DealHourEncounters();
            Assert.DoesNotContain(exclRestored.SelectionSet, id =>
                string.Equals(exclRestored.Cards[id].TemplateId.ToString(), LibraryId, StringComparison.OrdinalIgnoreCase));
        }

        // ── 验证 TRunConditionalCurrentDay 天数门控 ──
        const string gatedDay4Id = "91200001-0001-4001-8001-000000000001";
        string validZhKey = fx.Pool.Get(TheDocksId)!.TranslationKey;
        string gatedJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = gatedDay4Id,
            InternalName = "V12 Day4 Conditional Event",
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            SpawningChance = 999999.0,
            TranslationKey = validZhKey,
            SelectionRequirements = new
            {
                __type = "TPrerequisiteRun",
                Conditions = new
                {
                    __type = "TRunConditionalCurrentDay",
                    CurrentDay = 4,
                    ComparisonOperator = "GreaterThanOrEqual",
                },
            },
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = 1.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { WorkAsSecurityStepId } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture((gatedDay4Id, gatedJson));
        for (int d = 1; d <= 3; d++)
        {
            for (int s = 0; s < 10; s++)
            {
                var r = new RunState { RunId = $"g_d{d}_{s}", GameModeId = ov.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = (uint)d, Hour = 1, RngSeed = 100 + d * 20 + s };
                new ShopEngine(r, ov.Pool, ov.Repo).DealHourEncounters();
                Assert.DoesNotContain(r.SelectionSet, id => r.Cards[id].TemplateId.ToString().Equals(gatedDay4Id, StringComparison.OrdinalIgnoreCase));
            }
        }
        bool unlockedOnDay4 = false;
        for (int s = 0; s < 30 && !unlockedOnDay4; s++)
        {
            var r4 = new RunState { RunId = $"g_d4_{s}", GameModeId = ov.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 4, Hour = 1, RngSeed = 200 + s };
            new ShopEngine(r4, ov.Pool, ov.Repo).DealHourEncounters();
            unlockedOnDay4 = r4.SelectionSet.Any(id => r4.Cards[id].TemplateId.ToString().Equals(gatedDay4Id, StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(unlockedOnDay4, "Day 4 起满足 TRunConditionalCurrentDay >= 4 后应允许抽中条件事件");
    }

    // ── 4. 服务端 #19：多步奇遇保留 ParentEncounterTemplateId / Socket_5 肖像、挂起恢复与战前零残留 ──

    [Fact(DisplayName = "V12_04_ParentEncounterPortrait_MultiStepPreservation_ResumeCompatibility_AndPreCombatCleanup")]
    public void V12_04_ParentEncounterPortrait_MultiStepPreservation_ResumeCompatibility_AndPreCombatCleanup()
    {
        var fx = RealFixture.Value;

        // ── (A) 真实数据库多步事件：The Docks -> Work as Maintenace (原地 TActionGameDealCards 子牌刷新) ──
        var (realRun, realEngine) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, EHero.Vanessa, seed: 2026);

        var docksChoice = InjectChoiceEncounter(realRun, fx.Pool, TheDocksId);
        var enterDocksOut = realEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(docksChoice.InstanceId) });
        Assert.Null(enterDocksOut.Error);
        Assert.Equal(ERunState.Encounter, realRun.State);
        Assert.Equal(TheDocksId, realRun.ParentEncounterTemplateId, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(realRun.ActiveEncounterCardInstanceId));

        string originalActiveId = realRun.ActiveEncounterCardInstanceId!;
        Assert.True(realRun.Cards.TryGetValue(originalActiveId, out var activeDocks));
        Assert.Equal(Guid.Parse(TheDocksId), activeDocks!.TemplateId);
        Assert.Equal(ECardType.EventEncounter, activeDocks.Type);
        Assert.Equal(EContainerSocketId.Socket_5, activeDocks.Socket);

        // 注入/选中 Work as Maintenace（会调用 EncounterStepEngine -> ShopEngine.ClearShelf + DealCards）
        var maintTpl = fx.Pool.Get(WorkAsMaintenanceStepId)!;
        var maintShop = new ShopEngine(realRun, fx.Pool, fx.Repo);
        var maintCand = maintShop.CreateEncounterCandidate(maintTpl, ECardType.EncounterStep, "step");
        realRun.Cards[maintCand.InstanceId] = maintCand;
        realRun.SelectionSet.Add(maintCand.InstanceId);

        var stepOut = realEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(maintCand.InstanceId) });
        Assert.Null(stepOut.Error);
        Assert.Equal(ERunState.Encounter, realRun.State);
        Assert.Equal(TheDocksId, realRun.ParentEncounterTemplateId, ignoreCase: true);
        // ActiveEncounterCard 在 ExecuteStep ClearShelf 期间绝不能被 CardDisposed 销毁
        Assert.DoesNotContain(stepOut.Events, e =>
            e is GameSimEventCardDisposed disp && string.Equals(disp.InstanceId, originalActiveId, StringComparison.Ordinal));
        Assert.Equal(originalActiveId, realRun.ActiveEncounterCardInstanceId);
        Assert.True(realRun.Cards.TryGetValue(originalActiveId, out var stillActive));
        Assert.Equal(Guid.Parse(TheDocksId), stillActive!.TemplateId);
        Assert.Equal(ECardType.EventEncounter, stillActive.Type);
        Assert.Equal(EContainerSocketId.Socket_5, stillActive.Socket);

        // 退出后，ActiveEncounterCard 与 ParentEncounterTemplateId 彻底清理（切至下一小时 Choice，无任何 SelectionSet 外残留卡）
        var exitDocksOut = realEngine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitDocksOut.Error);
        Assert.Equal(ERunState.Choice, realRun.State);
        Assert.Null(realRun.ParentEncounterTemplateId);
        Assert.Null(realRun.ActiveEncounterCardInstanceId);
        Assert.False(realRun.Cards.ContainsKey(originalActiveId));
        Assert.DoesNotContain(realRun.Cards.Values, c =>
            c.Type == ECardType.EncounterStep ||
            (!realRun.SelectionSet.Contains(c.InstanceId) &&
             c.Type is ECardType.EventEncounter or ECardType.PedestalEncounter));

        // ── (B) 多步 NextEncounterOnSelection：EventEncounter -> Child EncounterStep -> Resume / Legacy / Exit ──
        const string rootEventId = "91200002-0001-4001-8001-000000000001";
        const string branchStep1Id = "91200002-0001-4001-8001-000000000002";
        const string childStepNodeId = "91200002-0001-4001-8001-000000000003";
        const string terminalStep2Id = "91200002-0001-4001-8001-000000000004";

        string rootEventJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = rootEventId,
            InternalName = "V12 MultiStep Root Event",
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = 1.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { branchStep1Id } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string branchStep1Json = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterStep",
            Id = branchStep1Id,
            InternalName = "V12 Step 1 Jump To Child Step",
            Type = "EncounterStep",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            ShouldProgressHour = false,
            NextEncounterOnSelection = new[]
            {
                new { Id = childStepNodeId, Weight = 1.0 },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string childStepNodeJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterStep",
            Id = childStepNodeId,
            InternalName = "V12 Child EncounterStep Container",
            Type = "EncounterStep",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = 1.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { terminalStep2Id } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string terminalStep2Json = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterStep",
            Id = terminalStep2Id,
            InternalName = "V12 Terminal Step Reward Gold",
            Type = "EncounterStep",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            ShouldProgressHour = false,
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { __type = "TTriggerOnCardSelected" },
                    Action = new
                    {
                        __type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Value = new { __type = "TFixedValue", Value = 5.0 },
                        Target = new { __type = "TTargetPlayerAbsolute", TargetMode = "Player" },
                    },
                    ActiveIn = "HandOnly",
                    WorksIn = "Anywhere",
                    Priority = "Immediate",
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture(
            (rootEventId, rootEventJson),
            (branchStep1Id, branchStep1Json),
            (childStepNodeId, childStepNodeJson),
            (terminalStep2Id, terminalStep2Json));

        var (chainRun, chainEngine) = CreateStartedEngine(
            ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Vanessa, seed: 3030);

        var rootChoice = InjectChoiceEncounter(chainRun, ov.Pool, rootEventId);
        var enterRootOut = chainEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(rootChoice.InstanceId) });
        Assert.Null(enterRootOut.Error);
        Assert.Equal(rootEventId, chainRun.ParentEncounterTemplateId, ignoreCase: true);
        string rootPortraitInstId = chainRun.ActiveEncounterCardInstanceId!;

        // 选中 Step 1 -> NextEncounterOnSelection 跳入子级 EncounterStep (childStepNodeId)
        string step1InstId = Assert.Single(chainRun.SelectionSet);
        var jumpChildOut = chainEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step1InstId) });
        Assert.Null(jumpChildOut.Error);

        // 服务端内部识别当前真实节点为 childStepNodeId，但 ParentEncounterTemplateId 与 Socket_5 始终保留根父级 EventEncounter
        Assert.Equal(ERunState.Encounter, chainRun.State);
        Assert.Equal(childStepNodeId, chainRun.CurrentEncounterId, ignoreCase: true);
        Assert.Equal(rootEventId, chainRun.ParentEncounterTemplateId, ignoreCase: true);
        Assert.Equal(rootPortraitInstId, chainRun.ActiveEncounterCardInstanceId);
        Assert.True(chainRun.Cards.TryGetValue(rootPortraitInstId, out var childPortraitCard));
        Assert.Equal(Guid.Parse(rootEventId), childPortraitCard!.TemplateId);
        Assert.Equal(ECardType.EventEncounter, childPortraitCard.Type);
        Assert.Equal(EContainerSocketId.Socket_5, childPortraitCard.Socket);

        // 验证发送给客户端的状态切换事件 CurrentEncounterId 对齐根父级 ID（防止客户端 BoardManager 找不到 Portrait 而清屏）
        var childTransitions = jumpChildOut.Events.OfType<GameSimEventStateTransitioned>()
            .Where(t => t.ToState == ERunState.Encounter)
            .ToList();
        Assert.NotEmpty(childTransitions);
        Assert.Equal(rootEventId, childTransitions.Last().CurrentEncounterId, ignoreCase: true);

        // 新快照往返序列化 + BuildResumeEvents 验证重连后依旧完整保留 ParentEncounterTemplateId 与顶栏头像
        string chainJson = RunSnapshotSerializer.Serialize(chainRun);
        var resumedChainRun = RunSnapshotSerializer.Deserialize(chainJson, ov.Repo)!;
        Assert.Equal(childStepNodeId, resumedChainRun.CurrentEncounterId, ignoreCase: true);
        Assert.Equal(rootEventId, resumedChainRun.ParentEncounterTemplateId, ignoreCase: true);
        Assert.Equal(rootPortraitInstId, resumedChainRun.ActiveEncounterCardInstanceId);
        var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(resumedChainRun);
        var resumeTransition = Assert.Single(resumeEvents.OfType<GameSimEventStateTransitioned>());
        Assert.Equal(ERunState.Encounter, resumeTransition.ToState);
        Assert.Equal(rootEventId, resumeTransition.CurrentEncounterId, ignoreCase: true);

        // 向后兼容测试：剥离旧快照中可能不存在的 ParentEncounterTemplateId 字段，验证反序列化无报错且能从 ActiveEncounterCard 回填
        string legacyJson = chainJson.Replace($"\"ParentEncounterTemplateId\":\"{rootEventId}\",", "");
        var legacyResumed = RunSnapshotSerializer.Deserialize(legacyJson, ov.Repo)!;
        Assert.Equal(rootEventId, legacyResumed.ParentEncounterTemplateId, ignoreCase: true);

        // SuspendedStates（LevelUp 中断压栈 → 快照序列化 → 选完升级弹出恢复）保留子步 ParentEncounterTemplateId 与 Socket_5 头像
        var suspendedStepSnap = new SuspendedStateSnapshot
        {
            State = resumedChainRun.State,
            CurrentEncounterId = resumedChainRun.CurrentEncounterId,
            ParentEncounterTemplateId = resumedChainRun.ParentEncounterTemplateId,
            ActiveEncounterCardInstanceId = resumedChainRun.ActiveEncounterCardInstanceId,
            ActiveEncounterCard = resumedChainRun.Cards[rootPortraitInstId],
            SelectionSet = new List<string>(resumedChainRun.SelectionSet),
            SelectionRules = RunState.CloneRules(resumedChainRun.SelectionRules),
            RerollCost = resumedChainRun.RerollCost,
            RerollsRemaining = resumedChainRun.RerollsRemaining,
            SelectionsMadeInCurrentContext = resumedChainRun.SelectionsMadeInCurrentContext,
        };
        foreach (string sid in resumedChainRun.SelectionSet)
            suspendedStepSnap.SuspendedCards.Add(resumedChainRun.Cards[sid]);
        resumedChainRun.SuspendedStates.Push(suspendedStepSnap);
        resumedChainRun.PendingLevelUps = 1;
        resumedChainRun.State = ERunState.LevelUp;
        resumedChainRun.CurrentEncounterId = null;
        resumedChainRun.ParentEncounterTemplateId = null;
        resumedChainRun.ActiveEncounterCardInstanceId = null;
        resumedChainRun.Cards.Remove(rootPortraitInstId);
        resumedChainRun.SelectionSet.Clear();

        string suspendedJson = RunSnapshotSerializer.Serialize(resumedChainRun);
        var restoredLevelUpRun = RunSnapshotSerializer.Deserialize(suspendedJson, ov.Repo)!;
        var levelUpEngine = new RunEngine(restoredLevelUpRun, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        Assert.Equal(ERunState.LevelUp, restoredLevelUpRun.State);
        Assert.NotEmpty(restoredLevelUpRun.SelectionSet);
        string lvlChoiceId = restoredLevelUpRun.SelectionSet.FirstOrDefault(sid =>
        {
            var ct = ov.Repo.GetCard(restoredLevelUpRun.Cards[sid].TemplateId.ToString());
            return ct?.NextEncounterOnSelection is not { Count: > 0 } &&
                   !string.Equals(ct?.Type, "EventEncounter", StringComparison.OrdinalIgnoreCase);
        }) ?? restoredLevelUpRun.SelectionSet.First();

        var finishLevelUpOut = levelUpEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(lvlChoiceId) });
        if (finishLevelUpOut.Error != null)
            finishLevelUpOut = levelUpEngine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lvlChoiceId) });
        Assert.Null(finishLevelUpOut.Error);

        // 若所选升级选项本身开启了子奇遇（如 Pip / 技能传授），继续收尾直至 SuspendedStates 弹出恢复父奇遇子步
        int safety = 0;
        while ((restoredLevelUpRun.SuspendedStates.Count > 0 ||
                !string.Equals(restoredLevelUpRun.CurrentEncounterId, childStepNodeId, StringComparison.OrdinalIgnoreCase)) &&
               safety++ < 4)
        {
            if (restoredLevelUpRun.SelectionRules.CanExit || restoredLevelUpRun.SelectionSet.Count == 0)
            {
                finishLevelUpOut = levelUpEngine.Handle(new ExitCurrentStateCommand());
            }
            else
            {
                string nextChoiceId = restoredLevelUpRun.SelectionSet.First();
                finishLevelUpOut = levelUpEngine.Handle(new SelectItemCommand { InstanceId = new InstanceId(nextChoiceId) });
            }
            Assert.Null(finishLevelUpOut.Error);
        }

        // 消费完 LevelUp 弹出恢复至子 EncounterStep 后，ParentEncounterTemplateId、ActiveEncounterCard(Socket_5) 与客户端过渡事件全部还原
        Assert.Equal(ERunState.Encounter, restoredLevelUpRun.State);
        Assert.Equal(childStepNodeId, restoredLevelUpRun.CurrentEncounterId, ignoreCase: true);
        Assert.Equal(rootEventId, restoredLevelUpRun.ParentEncounterTemplateId, ignoreCase: true);
        Assert.False(string.IsNullOrEmpty(restoredLevelUpRun.ActiveEncounterCardInstanceId));
        var restoredActive = restoredLevelUpRun.Cards[restoredLevelUpRun.ActiveEncounterCardInstanceId!];
        Assert.Equal(Guid.Parse(rootEventId), restoredActive.TemplateId);
        Assert.Equal(ECardType.EventEncounter, restoredActive.Type);
        Assert.Equal(EContainerSocketId.Socket_5, restoredActive.Socket);
        var popTransition = finishLevelUpOut.Events.OfType<GameSimEventStateTransitioned>()
            .LastOrDefault(t => t.ToState == ERunState.Encounter);
        Assert.NotNull(popTransition);
        Assert.Equal(rootEventId, popTransition!.CurrentEncounterId, ignoreCase: true);

        // 用恢复后的 Engine 继续完成终点 Step 2，并检验彻底退场与零残留
        string step2InstId = Assert.Single(restoredLevelUpRun.SelectionSet);
        var finishChainOut = levelUpEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step2InstId) });
        Assert.Null(finishChainOut.Error);
        Assert.Null(restoredLevelUpRun.ParentEncounterTemplateId);
        Assert.Null(restoredLevelUpRun.ActiveEncounterCardInstanceId);
        Assert.DoesNotContain(restoredLevelUpRun.Cards.Values, c =>
            c.Type == ECardType.EncounterStep ||
            (!restoredLevelUpRun.SelectionSet.Contains(c.InstanceId) &&
             c.Type is ECardType.EventEncounter or ECardType.PedestalEncounter));

        // ── (C) 进入根事件后切入战斗，确认进战前清理无任何残留事件卡或手牌/背包格子重叠 ──
        var combatStarterRules = new CustomRunRuleSet
        {
            StartingItems =
            [
                new PresetCardSpec { TemplateId = CutlassId, Tier = "Bronze" },
                new PresetCardSpec { TemplateId = BayonetId, Tier = "Bronze" },
            ],
        };
        var (combatRun, combatEngine) = CreateStartedEngine(
            fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, EHero.Vanessa,
            rules: combatStarterRules, seed: 4040);

        var docksBeforeCombat = InjectChoiceEncounter(combatRun, fx.Pool, TheDocksId);
        Assert.Null(combatEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(docksBeforeCombat.InstanceId) }).Error);
        Assert.Equal(TheDocksId, combatRun.ParentEncounterTemplateId, ignoreCase: true);
        Assert.Null(combatEngine.Handle(new ExitCurrentStateCommand()).Error);

        // 切至 Day 1 Hour 3 并选第一只 PvE 怪物进战（配备起手武器确保稳定击败 Day 1 Left 怪并停留在战后 Loot 态检验清理结果）
        combatRun.Day = 1;
        combatRun.Hour = 3;
        var h3Shop = new ShopEngine(combatRun, fx.Pool, fx.Repo);
        combatRun.State = ERunState.Choice;
        h3Shop.DealHourEncounters();
        string mobId = combatRun.SelectionSet.First();
        var fightOut = combatEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobId) });
        Assert.Null(fightOut.Error);
        Assert.Null(combatRun.ParentEncounterTemplateId);
        Assert.NotNull(fightOut.CombatSpawnSim);
        Assert.False(fightOut.CombatSpawnSim!.Cards.ContainsKey(docksBeforeCombat.InstanceId));
        Assert.Equal(ERunState.Loot, combatRun.State);
        Assert.DoesNotContain(combatRun.Cards.Values, c =>
            c.Type is ECardType.EventEncounter or ECardType.EncounterStep or ECardType.PedestalEncounter);

        Assert.True(combatRun.HandGrid.ValidateInvariants(out string handErr), handErr);
        Assert.True(combatRun.StashGrid.ValidateInvariants(out string stashErr), stashErr);
    }
}

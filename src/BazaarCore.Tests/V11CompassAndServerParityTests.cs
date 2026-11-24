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
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V11 罗盘校准、8 英雄全通与服务端 C/D/E 协议终验测试套件。
/// </summary>
[Collection("ShopEngineStaticState")]
public sealed class V11CompassAndServerParityTests
{
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string RedGumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
    private const string DuctTapeId = "0966f887-5aaf-44a5-90fa-ecb194270513";
    private const string DepletedDeviceId = "021639c0-4a9c-49d8-8254-6376994823e2";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string CoconutCrabCombatId = "9744d386-0c5c-414b-8065-0244aa539604";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string KarnokKelsaStarterId = "de175e34-df34-4581-a13a-46767e1c7c6b";

    private sealed class Fx
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public string CompassPath { get; init; } = "";
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
        var shared = TestInfrastructure.SharedGameDataFixture.Shared;
        return new Fx
        {
            DataDir = shared.DataDir,
            DbPath = shared.DbPath,
            CompassPath = shared.CompassPath,
            Pool = shared.Pool,
            Monsters = shared.CreateMonstersCopy(),
            LevelUps = shared.LevelUps,
            Mode = shared.Mode,
            Repo = shared.Repo,
            Ghosts = shared.Ghosts,
            ValidCardIds = shared.CreateValidCardIdsCopy(),
        };
    });

    /// <summary>
    /// 零拷贝内存叠加测试卡/遭遇模板，完全隔离且不污染共享底盘、不触发 Steam 回退。
    /// </summary>
    private sealed class OverlayFixture : IDisposable
    {
        private readonly TestInfrastructure.SharedTestOverlay _inner;
        public string TempDir => _inner.TempDir;
        public string DbPath => TestInfrastructure.SharedGameDataFixture.Shared.DbPath;
        public CardPool Pool => _inner.Pool;
        public List<BazaarServer.Data.MonsterTemplate> Monsters => _inner.Monsters;
        public LevelUpTable LevelUps => _inner.LevelUps;
        public GameModeConfig Mode => _inner.Mode;
        public GameDataRepository Repo => _inner.Repo;
        public PvpGhostPool Ghosts => _inner.Ghosts;

        public OverlayFixture(params (string Id, string Json)[] customCards)
        {
            _inner = TestInfrastructure.SharedGameDataFixture.Shared.CreateMemoryOverlay(customCards: customCards);
        }

        public void Dispose() => _inner.Dispose();
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        Fx fx,
        EHero hero = EHero.Vanessa,
        int seed = 2026)
        => CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, hero, seed);

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<BazaarServer.Data.MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa,
        int seed = 2026)
    {
        var run = new RunState
        {
            RunId = "v11_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId != Guid.Empty ? pool.DefaultGameModeId : Guid.NewGuid(),
            Hero = hero,
            PlayMode = EPlayMode.Ranked,
            RngSeed = seed,
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        GameDataRepository repo,
        CardPool pool,
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
        Assert.True(grid.TryPlaceOrShift(core, slot), $"测试插入卡牌 {templateId} 到 {section}:{slot} 失败");

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = section,
            Socket = (EContainerSocketId)core.StartSlot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, pool.Get(templateId), coreTpl);
        run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string templateId,
        ECardType cardType = ECardType.EventEncounter)
    {
        foreach (string oldId in run.SelectionSet.ToList())
        {
            run.SelectionSet.Remove(oldId);
            run.Cards.Remove(oldId);
        }
        var tpl = pool.Get(templateId);
        var entry = new RunState.CardEntry
        {
            InstanceId = $"enc_{Guid.NewGuid():N}",
            TemplateId = Guid.Parse(templateId),
            Type = cardType,
            Size = ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
            Owner = null,
            Section = null,
            Socket = null,
        };
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static string BuildShopJson(string id, string name, string[] itemIds, bool willAutoSellOnExit = false)
    {
        return JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = id,
            InternalName = name,
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = (double)itemIds.Length },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[]
                            {
                                new { __type = "TSpawnFilterIdList", Ids = itemIds },
                            },
                        },
                    },
                },
                Rules = new
                {
                    CanSelectMultiple = true,
                    SelectionIsFree = false,
                    CanExit = true,
                    WillAutoSellOnExit = willAutoSellOnExit,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    // ── 1. GameData.db 权威数据源：品阶概率（每日 2,000 次掷骰）与标准价格 ───────────────

    [Fact]
    public void V11_01_CardPool_LoadsAuthoritativeGameModeTierChancesAndPrices_FromGameDataDb()
    {
        var fx = RealFixture.Value;
        Assert.NotEqual(Guid.Empty, fx.Pool.DefaultGameModeId);

        // 1a. 验证 DB 标准价格未被旧 JSON 覆盖（Legendary 小/中/大 = 24/48/64；技能 = 5/10/20/40/50）
        Assert.Equal(24, fx.Pool.StandardPrices.GetItemBuyPrice(CardSize.Small, CardTier.Legendary));
        Assert.Equal(48, fx.Pool.StandardPrices.GetItemBuyPrice(CardSize.Medium, CardTier.Legendary));
        Assert.Equal(64, fx.Pool.StandardPrices.GetItemBuyPrice(CardSize.Large, CardTier.Legendary));
        Assert.Equal(12, fx.Pool.StandardPrices.GetItemSellPrice(CardSize.Small, CardTier.Legendary));
        Assert.Equal(24, fx.Pool.StandardPrices.GetItemSellPrice(CardSize.Medium, CardTier.Legendary));
        Assert.Equal(32, fx.Pool.StandardPrices.GetItemSellPrice(CardSize.Large, CardTier.Legendary));
        Assert.Equal(5, fx.Pool.StandardPrices.GetSkillBuyPrice(CardTier.Bronze));
        Assert.Equal(50, fx.Pool.StandardPrices.GetSkillBuyPrice(CardTier.Legendary));
        Assert.Equal(25, fx.Pool.StandardPrices.GetSkillSellPrice(CardTier.Legendary));

        // 1b. 从 GameData.db 读取官方 ItemSkillSpawnTierPercantagesByDay，与 CreateTierChances 每日 2,000 次抽样统计对标
        using var conn = new SqliteConnection($"Data Source={fx.DbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Data FROM game_modes LIMIT 1";
        byte[] blob = (byte[])cmd.ExecuteScalar()!;
        var gm = JsonSerializer.Deserialize<GameModeData>(Encoding.UTF8.GetString(blob), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        var chances = fx.Pool.CreateTierChances();
        var rng = new Random(1101);
        const int rollsPerDay = 2000;

        for (int day = 1; day <= 10; day++)
        {
            var expected = gm.ItemSkillSpawnTierPercantagesByDay[day.ToString()];
            double sum = expected.GetValueOrDefault("Bronze") + expected.GetValueOrDefault("Silver")
                       + expected.GetValueOrDefault("Gold") + expected.GetValueOrDefault("Diamond");

            chances.CurrentDay = day;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rollsPerDay; i++)
            {
                string tier = chances.Roll(rng);
                counts[tier] = counts.GetValueOrDefault(tier) + 1;
            }

            foreach (string tier in new[] { "Bronze", "Silver", "Gold", "Diamond" })
            {
                double expP = expected.GetValueOrDefault(tier, 0.0) / sum;
                double obsP = counts.GetValueOrDefault(tier, 0) / (double)rollsPerDay;
                if (expP == 0.0)
                {
                    Assert.Equal(0, counts.GetValueOrDefault(tier, 0));
                }
                else
                {
                    Assert.True(Math.Abs(obsP - expP) <= 0.05,
                        $"Day {day} {tier} 抽样概率偏移过大: expected={expP:P2}, observed={obsP:P2}");
                }
            }
        }
    }

    // ── 2. 罗盘校准 & PvpGhostPool Day 1..15 边界 / 槽位填充 / 500 抽样英雄一致性 ────────

    [Fact]
    public void V11_02_CompassCalibration_And_PvpGhostPool_BoundsSlotFillHeroParity()
    {
        var fx = RealFixture.Value;
        Assert.Equal(2500, PvpGhostPool.MaxBoardsPerDayBucket);
        Assert.True(File.Exists(fx.CompassPath), $"未找到 compass_distilled.json: {fx.CompassPath}");

        // 2a. 解析 compass_distilled.json 天梯校准曲线并验证其落在 PvpGhostPool.GetSaneBoundsForDay 范围内
        using (var doc = JsonDocument.Parse(File.ReadAllText(fx.CompassPath)))
        {
            var cal = doc.RootElement.GetProperty("calibration");
            foreach (int calDay in new[] { 4, 7, 10 })
            {
                var (minHp, maxHp, _) = PvpGhostPool.GetSaneBoundsForDay(calDay);
                var arr = cal.GetProperty(calDay.ToString());
                foreach (var entry in arr.EnumerateArray())
                {
                    int hp = entry.GetProperty("healthMax").GetInt32();
                    Assert.InRange(hp, minHp, maxHp);
                }
            }
        }

        // 验证 GetSaneBoundsForDay 在 Day 1..15 单调递增
        int prevMin = 0, prevMax = 0;
        for (int d = 1; d <= 15; d++)
        {
            var (minHp, maxHp, _) = PvpGhostPool.GetSaneBoundsForDay(d);
            Assert.True(minHp < maxHp);
            Assert.True(minHp > prevMin && maxHp > prevMax, $"Day {d} 上下界应单调递增");
            prevMin = minHp;
            prevMax = maxHp;
        }

        // 2b. 跨 Day 1..15 抽样 500 份对手镜像，校验血量界限、英雄严格一致与 0 丢卡
        var rng = new Random(1102);
        int samples = 0;
        for (int i = 0; i < 500; i++)
        {
            int day = (i % 15) + 1;
            var ghost = fx.Ghosts.PickOpponent(day, playerWins: Math.Max(0, day - 2), playerLosses: 1, rng, fx.Repo);
            Assert.True(PvpGhostPool.IsSaneGhostForDay(ghost, day),
                $"Day {day} 抽取到超界镜像 {ghost.BoardId}: HP={ghost.HealthMax}, Lv={ghost.Level}");

            EHero previewHero = PvpGhostPool.ParseHero(ghost.Hero);
            PlayerState ps = PvpGhostPool.ToPlayerState(ghost, fx.Repo);
            EHero combatHero = PvpGhostPool.ParseHero(ps.Hero);
            Assert.Equal(previewHero, combatHero);

            // 验证不丢卡且 Day >= 5 解锁掩码为完整 10 格 0x3FF
            int validTemplateItems = ghost.Items.Count(it => fx.Repo.GetCard(it.TemplateId) != null);
            Assert.Equal(validTemplateItems, ps.Hand.Cards.Count);
            if (day >= 5)
                Assert.Equal(1023, ps.Hand.UnlockedMask);
            samples++;
        }
        Assert.Equal(500, samples);

        // 2c. 全 8 位英雄（含 Karnok 与 Hero8）预览解析与 ToPlayerState 转换完全一致
        string sampleItemId = CutlassId;
        foreach (EHero h in new[]
                 {
                     EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak,
                     EHero.Jules, EHero.Stelle, EHero.Karnok, EHero.Hero8,
                 })
        {
            var synthetic = new PvpGhostPool.GhostBoard
            {
                BoardId = "v11_hero_" + h,
                Day = 5,
                Hero = h.ToString(),
                Level = 5,
                HealthMax = 1400,
                UnlockedSlotsMask = 120,
                Items = new List<PvpGhostPool.GhostItem>
                {
                    new() { Slot = 0, Width = 2, TemplateId = sampleItemId, Tier = "Silver" },
                    new() { Slot = 8, Width = 2, TemplateId = sampleItemId, Tier = "Gold" },
                },
            };
            Assert.Equal(h, PvpGhostPool.ParseHero(synthetic.Hero));
            var ps = PvpGhostPool.ToPlayerState(synthetic, fx.Repo);
            Assert.Equal(h, PvpGhostPool.ParseHero(ps.Hero));
            Assert.Equal(1023, ps.Hand.UnlockedMask);
            Assert.Equal(2, ps.Hand.Cards.Count);
        }
    }

    // ── 3. Day 1..10 Hour 3 野怪分级与首日起始武器可胜性 ──────────────────────────────────

    [Fact]
    public void V11_03_MonsterDayByHourGating_And_Day1StarterWeaponWinnability()
    {
        var fx = RealFixture.Value;

        for (int day = 1; day <= 10; day++)
        {
            var run = new RunState
            {
                RunId = $"v11_mob_d{day}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = EHero.Vanessa,
                Day = (uint)day,
                Hour = 3,
                RngSeed = 3000 + day,
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            shop.DealHourEncounters();

            Assert.Equal(3, run.SelectionSet.Count);
            var dealt = run.SelectionSet.Select(id => run.Cards[id]).ToList();
            Assert.All(dealt, e => Assert.Equal(ECardType.CombatEncounter, e.Type));

            var metas = dealt.Select(e =>
            {
                var tpl = fx.Pool.Get(e.TemplateId.ToString())!;
                var m = fx.Monsters.First(x => string.Equals(x.Id, tpl.MonsterTemplateId, StringComparison.OrdinalIgnoreCase));
                Assert.False(MonsterPool.IsExcludedDevOrTestMonsterName(tpl.InternalName));
                Assert.False(MonsterPool.IsExcludedDevOrTestMonsterName(m.InternalName));
                Assert.InRange(m.HealthMax, 75, 9999);
                return (Tpl: tpl, Hp: m.HealthMax, Lv: tpl.MonsterLevel, Tier: MonsterPool.GetMonsterTier(tpl));
            }).ToList();

            // V13.1 官方规则按品阶与等级左<=中<=右递增（允许低血高阶官方怪如 Day 1 Viper 75HP / Day 5 Drone Operator 850HP / Day 8 Lich 1800HP）
            Assert.True(metas[0].Tier <= metas[1].Tier && metas[1].Tier <= metas[2].Tier,
                $"Day {day} 野怪品阶应呈左<=中<=右梯度: {metas[0].Tier}, {metas[1].Tier}, {metas[2].Tier}");
            Assert.True(metas[0].Lv <= metas[1].Lv && metas[1].Lv <= metas[2].Lv,
                $"Day {day} 野怪等级应呈左<=中<=右梯度: {metas[0].Lv}, {metas[1].Lv}, {metas[2].Lv}");

            if (day == 1)
            {
                Assert.Equal(1, metas[0].Lv);
                Assert.InRange(metas[0].Hp, 75, 100);
                Assert.Equal(1, metas[1].Lv);
                Assert.InRange(metas[1].Hp, 75, 100);
                Assert.Equal(2, metas[2].Lv);
                Assert.InRange(metas[2].Hp, 75, 100);
            }
            else if (day == 2)
            {
                Assert.Equal(2, metas[0].Lv);
                Assert.InRange(metas[0].Hp, 200, 300);
                Assert.Equal(3, metas[1].Lv);
                Assert.InRange(metas[1].Hp, 300, 350);
                Assert.Equal(4, metas[2].Lv);
                Assert.InRange(metas[2].Hp, 450, 600);
            }
            else if (day == 3)
            {
                Assert.Equal(3, metas[0].Lv);
                Assert.InRange(metas[0].Hp, 300, 350);
                Assert.Equal(4, metas[1].Lv);
                Assert.InRange(metas[1].Hp, 450, 600);
                Assert.Equal(5, metas[2].Lv);
                Assert.InRange(metas[2].Hp, 650, 700);
            }
            else
            {
                Assert.Equal(day, metas[0].Lv);
                Assert.Equal(day + 1, metas[1].Lv);
                Assert.Equal(day + 2, metas[2].Lv);
            }
        }

        // Day 1 Hour 3 起始武器组合战斗力验证：青铜 Cutlass + Bayonet（3/4 已解锁格、6/15 初始金币）确定性击败全部 Lv1 (75~150HP) 野怪
        const string bayonetId = "6146cacd-202c-489f-88c0-f77ef3e0fda6";
        var cutlassTpl = fx.Repo.GetCard(CutlassId)!;
        var bayonetTpl = fx.Repo.GetCard(bayonetId)!;
        var lv1Monsters = fx.Pool.OfType("CombatEncounter")
            .Where(c => c.MonsterLevel == 1 &&
                        string.Equals(c.SpawningEligibility, "Always", StringComparison.OrdinalIgnoreCase) &&
                        !CardPool.IsExcludedDevOrStoryTemplate(c) &&
                        !MonsterPool.IsExcludedDevOrTestMonsterName(c.InternalName))
            .Select(c => (Tpl: c, Monster: fx.Monsters.FirstOrDefault(m => string.Equals(m.Id, c.MonsterTemplateId, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Monster != null && !MonsterPool.IsExcludedDevOrTestMonsterName(x.Monster.InternalName) && x.Monster.HealthMax is >= 75 and <= 150)
            .ToList();
        Assert.NotEmpty(lv1Monsters);

        foreach (var (mobTpl, monster) in lv1Monsters)
        {
            var m = monster!;
            var p1 = new PlayerState("p1", "Vanessa", "Vanessa", 250, 20, 0, 10) { Level = 1 };
            p1.Hand.UnlockedMask = 0b00_0111_1000;
            p1.Hand.TryPlaceOrShift(new CardInstance(cutlassTpl, CardTier.Bronze, null, "starter_cutlass"), 3);
            p1.Hand.TryPlaceOrShift(new CardInstance(bayonetTpl, CardTier.Bronze, null, "starter_bayonet"), 5);
            p1.PrepareForCombat();

            var p2 = new PlayerState("p2", m.InternalName, "Common", m.HealthMax, m.Prestige, 0, 10) { Level = Math.Max(1, mobTpl.MonsterLevel) };
            p2.Hand.UnlockedMask = 1023;
            foreach (var mi in m.Items)
            {
                var mit = fx.Repo.GetCard(mi.TemplateId);
                if (mit == null) continue;
                int slot = int.TryParse(mi.SocketId?.Replace("Socket_", ""), out int s) ? s : 0;
                var ci = new CardInstance(mit, TierHelper.ParseTier(mi.Tier ?? "Bronze"), mi.EnchantmentType);
                if (!p2.Hand.TryPlaceOrShift(ci, slot)) p2.Hand.TryAddAuto(ci);
            }
            foreach (var sk in m.Skills)
            {
                var st = fx.Repo.GetCard(sk.TemplateId);
                if (st != null) p2.Skills.Add(new CardInstance(st, TierHelper.ParseTier(sk.Tier ?? "Bronze")));
            }
            p2.PrepareForCombat();

            var res = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 42, combatType: "PVE");
            Assert.True(res.WinnerPlayerId == "p1",
                $"Bronze Cutlass+Bayonet 应击败 Day 1 野怪 {m.InternalName} (Lv={mobTpl.MonsterLevel}, HP={m.HealthMax}), 实际胜者={res.WinnerPlayerId}, P1剩余HP={res.Player1RemainingHp}, P2剩余HP={res.Player2RemainingHp}, 时长={res.DurationMs}ms");
        }
    }

    // ── 4. 全 8 英雄开局发牌 + TPrerequisiteRun / TRunConditionalCurrentDay 天数门控 ───

    [Fact]
    public void V11_04_All8HeroStarters_And_EncounterDayPrerequisites()
    {
        var fx = RealFixture.Value;
        Assert.Equal(KarnokKelsaStarterId, ShopEngine.GetStarterEncounterId(EHero.Karnok));

        foreach (EHero hero in new[]
                 {
                     EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak,
                     EHero.Jules, EHero.Stelle, EHero.Karnok, EHero.Hero8,
                 })
        {
            var run = new RunState
            {
                RunId = $"v11_s_{hero}",
                GameModeId = fx.Pool.DefaultGameModeId,
                Hero = hero,
                Day = 1,
                Hour = 1,
                RngSeed = 400 + (int)hero,
            };
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            string resolvedId = shop.ResolveValidStarterEncounterId(hero);
            Assert.True(fx.Pool.IsAssetValid(resolvedId), $"英雄 {hero} 开局遭遇 {resolvedId} 未通过白名单");

            var skillEvs = shop.DealStarterSkills(3);
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.Contains(skillEvs, e => e is GameSimEventCardDealt);
            foreach (string cid in run.SelectionSet)
            {
                var entry = run.Cards[cid];
                Assert.Equal(ECardType.Skill, entry.Type);
                Assert.True(fx.Pool.IsAssetValid(entry.TemplateId.ToString()));
            }

            // 同时验证 Day 1 Hour 1 正式白昼开局 Choice 对 8 位英雄均发满 3 张有效遭遇卡
            var (startRun, _) = CreateStartedEngine(fx, hero, 500 + (int)hero);
            Assert.Equal(3, startRun.SelectionSet.Count);
            Assert.All(startRun.SelectionSet, id => Assert.True(fx.Pool.IsAssetValid(startRun.Cards[id].TemplateId.ToString())));
        }

        // 验证 TPrerequisiteRun(TRunConditionalCurrentDay) 对 Hour 遭遇及子 EncounterStep 选项过滤
        const string lateEventId = "91100001-0001-4001-8001-000000000001";
        const string parentStepEventId = "91100001-0001-4001-8001-000000000002";
        const string earlyStepId = "91100001-0001-4001-8001-000000000003";
        const string lateStepId = "91100001-0001-4001-8001-000000000004";

        string lateEventJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = lateEventId,
            InternalName = "V11 Late Day4 Event",
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            SpawningChance = 999999.0,
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
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { earlyStepId } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string MakeStepJson(string sid, string name, int reqDay, string op) => JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterStep",
            Id = sid,
            InternalName = name,
            Type = "EncounterStep",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            SelectionRequirements = new
            {
                __type = "TPrerequisiteRun",
                Conditions = new
                {
                    __type = "TRunConditionalCurrentDay",
                    CurrentDay = reqDay,
                    ComparisonOperator = op,
                },
            },
            Abilities = new Dictionary<string, object>(),
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string parentJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = parentStepEventId,
            InternalName = "V11 Parent Step Event",
            Type = "EventEncounter",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Always",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    __type = "TSpawnContextQuery",
                    SelectionMethod = "Sequential",
                    Limit = new { __type = "TFixedValue", Value = 2.0 },
                    Groups = new[]
                    {
                        new
                        {
                            __type = "TSpawnGroup",
                            SelectionMethod = "Sequential",
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { earlyStepId, lateStepId } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture(
            (lateEventId, lateEventJson),
            (earlyStepId, MakeStepJson(earlyStepId, "Early Step <= D3", 3, "LessThanOrEqual")),
            (lateStepId, MakeStepJson(lateStepId, "Late Step >= D4", 4, "GreaterThanOrEqual")),
            (parentStepEventId, parentJson));

        var day2Run = new RunState { RunId = "v11_d2_pre", GameModeId = ov.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = 2, Hour = 1, RngSeed = 11 };
        var shopD2 = new ShopEngine(day2Run, ov.Pool, ov.Repo);
        for (int k = 0; k < 5; k++)
        {
            shopD2.DealHourEncounters();
            Assert.DoesNotContain(day2Run.SelectionSet, id => day2Run.Cards[id].TemplateId == Guid.Parse(lateEventId));
        }

        shopD2.DealEncounterShelf(parentStepEventId, isReroll: false);
        Assert.Contains(day2Run.SelectionSet, id => day2Run.Cards[id].TemplateId == Guid.Parse(earlyStepId));
        Assert.DoesNotContain(day2Run.SelectionSet, id => day2Run.Cards[id].TemplateId == Guid.Parse(lateStepId));

        day2Run.Day = 4;
        shopD2.DealEncounterShelf(parentStepEventId, isReroll: false);
        Assert.Contains(day2Run.SelectionSet, id => day2Run.Cards[id].TemplateId == Guid.Parse(lateStepId));
        Assert.DoesNotContain(day2Run.SelectionSet, id => day2Run.Cards[id].TemplateId == Guid.Parse(earlyStepId));
    }

    // ── 5. C-18 / C-32 / D-28 / C-16：Unsellable、WillAutoSellOnExit、手牌满拒购与全挤位同步 ──

    [Fact]
    public void V11_05_OnSellUnsellable_WillAutoSellOnExit_OnBuyHandFull_And_MoveOwnedCardDisplacementSync()
    {
        const string shopNormalId = "91100002-0001-4001-8001-000000000001";
        const string shopAutoSellId = "91100002-0001-4001-8001-000000000002";
        const string autoSellItemId = "91100002-0001-4001-8001-000000000003";
        const string attrUnsellableId = "91100002-0001-4001-8001-000000000004";
        const string spawnStepId = "91100002-0001-4001-8001-000000000005";
        const string spawnEventId = "91100002-0001-4001-8001-000000000006";

        string CustomItemJson(string id, string name, string[] tags, Dictionary<string, int> attrs) => JsonSerializer.Serialize(new
        {
            __type = "TCardItem",
            Id = id,
            InternalName = name,
            Type = "Item",
            Heroes = new[] { "Common" },
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            Tags = tags,
            HiddenTags = Array.Empty<string>(),
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = attrs,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string spawnStepJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterStep",
            Id = spawnStepId,
            InternalName = "V11 Free Spawn Step",
            Type = "EncounterStep",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "GuidOnly",
            Abilities = new Dictionary<string, object>
            {
                ["a0"] = new
                {
                    Id = "a0",
                    Trigger = new { __type = "TTriggerOnCardSelected" },
                    Action = new
                    {
                        __type = "TActionGameSpawnCards",
                        Target = new { __type = "TTargetPlayerRelative", TargetMode = "Self" },
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
                                    Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { CutlassId } } },
                                },
                            },
                        },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        string spawnEventJson = JsonSerializer.Serialize(new
        {
            __type = "TCardEncounterEvent",
            Id = spawnEventId,
            InternalName = "V11 Free Spawn Event",
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
                            Filters = new[] { new { __type = "TSpawnFilterIdList", Ids = new[] { spawnStepId } } },
                        },
                    },
                },
                Rules = new { CanSelectMultiple = false, SelectionIsFree = true, CanExit = true },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture(
            (shopNormalId, BuildShopJson(shopNormalId, "V11 Normal Shop", new[] { CutlassId, RedGumballId })),
            (shopAutoSellId, BuildShopJson(shopAutoSellId, "V11 AutoSell Shop", new[] { RedGumballId, CutlassId }, willAutoSellOnExit: true)),
            (autoSellItemId, CustomItemJson(autoSellItemId, "V11 AutoSell Item", new[] { "WillAutoSellOnExit" }, new Dictionary<string, int> { ["BuyPrice"] = 2, ["SellPrice"] = 4 })),
            (attrUnsellableId, CustomItemJson(attrUnsellableId, "V11 Attr Unsellable", Array.Empty<string>(), new Dictionary<string, int> { ["Unsellable"] = 1, ["SellPrice"] = 3 })),
            (spawnStepId, spawnStepJson),
            (spawnEventId, spawnEventJson));

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        run.PlayerAttributes[EPlayerAttributeType.Gold] = 50;

        // C-18: Depleted Device(宽2占3,4) 与 Unsellable=1 属性卡(slot 5) 必须拒绝出售、不涨金币且返回回弹同步信息
        var depleted = AddOwnedItem(run, ov.Repo, ov.Pool, DepletedDeviceId, EInventorySection.Hand, slot: 3);
        var attrUnsell = AddOwnedItem(run, ov.Repo, ov.Pool, attrUnsellableId, EInventorySection.Hand, slot: 5);
        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];

        foreach (var target in new[] { depleted, attrUnsell })
        {
            var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(target.InstanceId) });
            Assert.NotNull(sellOut.Error);
            Assert.Equal(EResponseErrorType.NotAllowed, sellOut.Error!.ErrorType);
            Assert.True(run.Cards.ContainsKey(target.InstanceId));
            Assert.Contains(target.InstanceId, sellOut.DirtyCardIds);
            Assert.Contains(sellOut.Events.OfType<GameSimEventCardMoved>(), mv => mv.InstanceId == target.InstanceId && mv.ToSocket == target.Socket);
        }
        Assert.Equal(goldBefore, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        // D-28: Day 1 手牌 4 格(3,4,5,6)填满、储藏箱全空时：指定 Hand 或省略 Section 买卡必须干净拒购且 Stash 保持为空；仅显式 Stash 成功
        var filler1 = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 6);
        Assert.Equal(0, run.HandGrid.CountFreeUnlockedSlots());
        Assert.Empty(run.StashGrid.Cards);

        var shopChoice = InjectChoiceEncounter(run, ov.Pool, shopNormalId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopChoice.InstanceId) }).Error);

        var shelfCutlass = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(CutlassId));
        var shelfGumball = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(RedGumballId));

        int goldPreBuy = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var rejectDefault = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(shelfGumball.InstanceId), Section = null });
        Assert.NotNull(rejectDefault.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, rejectDefault.Error!.ErrorType);
        Assert.Equal(goldPreBuy, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Contains(shelfGumball.InstanceId, run.SelectionSet);
        Assert.Empty(run.StashGrid.Cards);

        var rejectHand = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(shelfCutlass.InstanceId), Section = EInventorySection.Hand });
        Assert.NotNull(rejectHand.Error);
        Assert.Equal(goldPreBuy, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Empty(run.StashGrid.Cards);

        var explicitStashBuy = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(shelfGumball.InstanceId), Section = EInventorySection.Stash });
        Assert.Null(explicitStashBuy.Error);
        Assert.Equal(EInventorySection.Stash, shelfGumball.Section);
        Assert.Contains(run.StashGrid.Cards, c => ReferenceEquals(c, shelfGumball.CoreCard));

        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);

        // D-28 补充：免费 TActionGameSpawnCards 在手牌满时仍可正常溢出到 Stash
        var spChoice = InjectChoiceEncounter(run, ov.Pool, spawnEventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(spChoice.InstanceId) }).Error);
        var spStep = run.SelectionSet.Select(id => run.Cards[id]).Single();
        var spOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(spStep.InstanceId) });
        Assert.Null(spOut.Error);
        Assert.True(run.StashGrid.Cards.Count >= 2, "免费 TActionGameSpawnCards 在手牌满时应溢出进 Stash");

        // C-16: 开放全部格子并测试 MoveOwnedCard 挤位相邻卡牌的全量 GameSimEventCardMoved 与 DirtyCardIds 同步
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        // 清理出简洁场景测试 1 格卡挤入 2 格相邻卡位置
        foreach (var c in run.Cards.Values.Where(x => x.Owner == ECombatantId.Player && x.Type == ECardType.Item).ToList())
        {
            run.HandGrid.Remove(c.CoreCard!);
            run.StashGrid.Remove(c.CoreCard!);
            run.Cards.Remove(c.InstanceId);
        }
        var mover = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Hand, slot: 0);
        var neighbor = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Hand, slot: 4);
        var neighbor2 = AddOwnedItem(run, ov.Repo, ov.Pool, DuctTapeId, EInventorySection.Hand, slot: 5);

        // 放 1 张 Cutlass(宽 2) 挤占 neighbor(4) 与 neighbor2(5)
        var wideCard = AddOwnedItem(run, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 8);
        var moveOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(wideCard.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_4 },
        });
        Assert.Null(moveOut.Error);
        Assert.Contains(wideCard.InstanceId, moveOut.DirtyCardIds);
        var movedEvents = moveOut.Events.OfType<GameSimEventCardMoved>().ToList();
        Assert.Contains(movedEvents, m => m.InstanceId == wideCard.InstanceId);

        // 验证至少挤动了原本占在 4/5 的邻卡，且所有变动邻卡均带 DirtyCardIds + GameSimEventCardMoved 且与 CoreCard 完全对齐
        var displaced = new[] { neighbor, neighbor2 }.Where(x => x.Socket != (x == neighbor ? EContainerSocketId.Socket_4 : EContainerSocketId.Socket_5)).ToList();
        Assert.NotEmpty(displaced);
        foreach (var d in displaced)
        {
            Assert.Contains(d.InstanceId, moveOut.DirtyCardIds);
            Assert.Contains(movedEvents, m => m.InstanceId == d.InstanceId && m.ToInventory == d.Section && m.ToSocket == d.Socket);
            Assert.Equal((int)d.Socket!.Value, d.CoreCard!.StartSlot);
        }
        Assert.True(run.HandGrid.ValidateInvariants(out string hErr), hErr);

        // C-32: WillAutoSellOnExit（货架未购卡 + 显式标有 WillAutoSellOnExit 的持有卡离开 Encounter 时自动卖出，普通手牌不受波及）
        foreach (var c in run.Cards.Values.Where(x => x.Owner == ECombatantId.Player && x.Type == ECardType.Item).ToList())
        {
            run.HandGrid.Remove(c.CoreCard!);
            run.StashGrid.Remove(c.CoreCard!);
            run.Cards.Remove(c.InstanceId);
        }
        var flaggedOwned = AddOwnedItem(run, ov.Repo, ov.Pool, autoSellItemId, EInventorySection.Hand, slot: 1);
        var autoEnc = InjectChoiceEncounter(run, ov.Pool, shopAutoSellId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(autoEnc.InstanceId) }).Error);

        // 从货架买下 Cutlass（普通商品，不应在离开时误卖）而留下 RedGumball 在货架
        var buyNormal = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(CutlassId));
        var leaveShelfGumball = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(RedGumballId));
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(buyNormal.InstanceId), Section = EInventorySection.Hand }).Error);

        int goldPreExit = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int expectedSell = leaveShelfGumball.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 1)
                         + flaggedOwned.Attributes.GetValueOrDefault(ECardAttributeType.SellPrice, 4);

        var exitAutoOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitAutoOut.Error);
        Assert.Equal(goldPreExit + expectedSell, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Contains(exitAutoOut.Events.OfType<GameSimEventCardSold>(), cs => cs.InstanceId == leaveShelfGumball.InstanceId);
        Assert.Contains(exitAutoOut.Events.OfType<GameSimEventCardSold>(), cs => cs.InstanceId == flaggedOwned.InstanceId);
        Assert.False(run.Cards.ContainsKey(flaggedOwned.InstanceId));
        Assert.True(run.Cards.ContainsKey(buyNormal.InstanceId), "普通商店购入物品离开后严禁被误卖");
    }

    // ── 6. E-6 / E-27：战末 CombatSim 收尾零值/弹药重置与战后首帧强制恢复 ───────────────────

    [Fact]
    public void V11_06_PostCombatVfxAndAmmoCleanup_ZeroesStatusStopsFlyingAndRefillsAmmo()
    {
        const string ammoWeaponId = "91100003-0001-4001-8001-000000000001";
        const string selfFreezeFlyId = "91100003-0001-4001-8001-000000000002";

        string ammoWeaponJson = JsonSerializer.Serialize(new
        {
            __type = "TCardItem",
            Id = ammoWeaponId,
            Version = "5.0.0",
            InternalName = "V11 Ammo Cannon",
            Type = "Item",
            Heroes = new[] { "Common" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tags = new[] { "Weapon" },
            HiddenTags = new[] { "Damage", "Ammo" },
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["CooldownMax"] = 600,
                        ["DamageAmount"] = 60,
                        ["AmmoMax"] = 2,
                        ["BuyPrice"] = 4,
                        ["SellPrice"] = 2,
                    },
                    AbilityIds = new[] { "fire" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["fire"] = new
                {
                    Id = "fire",
                    Trigger = new { __type = "TTriggerOnCardFired" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        __type = "TActionPlayerDamage",
                        Target = new { __type = "TTargetPlayerRelative", TargetMode = "Opponent" },
                        Value = new { __type = "TReferenceValueCardAttribute", AttributeType = "DamageAmount", Target = new { __type = "TTargetCardSelf" } },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        // 开战即挂长期 Flying + Freeze + Slow + Haste，保证胜利击杀瞬间状态仍在激活中，验证末帧清理与战后归零
        string statusGadgetJson = JsonSerializer.Serialize(new
        {
            __type = "TCardItem",
            Id = selfFreezeFlyId,
            Version = "5.0.0",
            InternalName = "V11 Long Flight Status Gadget",
            Type = "Item",
            Heroes = new[] { "Common" },
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int> { ["BuyPrice"] = 2, ["SellPrice"] = 1 },
                    AbilityIds = new[] { "fly", "frz", "slw", "hst" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["fly"] = new
                {
                    Id = "fly",
                    Trigger = new { __type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        __type = "TActionCardFlyingStart",
                        Target = new { __type = "TTargetCardSelf" },
                    },
                },
                ["frz"] = new
                {
                    Id = "frz",
                    Trigger = new { __type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        __type = "TActionCardFreeze",
                        Target = new { __type = "TTargetCardSelf" },
                        Value = new { __type = "TFixedValue", Value = 10000.0 },
                    },
                },
                ["slw"] = new
                {
                    Id = "slw",
                    Trigger = new { __type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        __type = "TActionCardSlow",
                        Target = new { __type = "TTargetCardSelf" },
                        Value = new { __type = "TFixedValue", Value = 10000.0 },
                    },
                },
                ["hst"] = new
                {
                    Id = "hst",
                    Trigger = new { __type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    WorksIn = "CombatOnly",
                    Action = new
                    {
                        __type = "TActionCardHaste",
                        Target = new { __type = "TTargetCardSelf" },
                        Value = new { __type = "TFixedValue", Value = 10000.0 },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture(
            (ammoWeaponId, ammoWeaponJson),
            (selfFreezeFlyId, statusGadgetJson));

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var weapon = AddOwnedItem(run, ov.Repo, ov.Pool, ammoWeaponId, EInventorySection.Hand, slot: 0);
        var gadget = AddOwnedItem(run, ov.Repo, ov.Pool, selfFreezeFlyId, EInventorySection.Hand, slot: 2);
        Assert.Equal(2, weapon.CoreCard!.CurrentAmmo);

        // 对阵 Fanged Inglet (100 HP)：AmmoMax=2 的 60 伤武器恰好 2 枪耗尽弹药 (120 dmg) 并击杀胜利
        var mobOpt = InjectChoiceEncounter(run, ov.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);
        Assert.Equal(ECombatantId.Player, combatOut.Combat!.Winner);

        // 1. 验证 CombatSim 末帧显式收尾清零 Freeze/Slow/Haste/Flying 并回满 Ammo
        var lastFrame = combatOut.Combat.Frames[^1];
        Assert.True(lastFrame.CardUpdates.TryGetValue(new InstanceId(gadget.InstanceId), out var gadgetLastUpdate));
        Assert.Equal(0, gadgetLastUpdate!.Attributes[ECardAttributeType.Freeze].CurrentValue);
        Assert.Equal(0, gadgetLastUpdate.Attributes[ECardAttributeType.Slow].CurrentValue);
        Assert.Equal(0, gadgetLastUpdate.Attributes[ECardAttributeType.Haste].CurrentValue);
        Assert.Equal(0, gadgetLastUpdate.Attributes[ECardAttributeType.Flying].CurrentValue);

        if (lastFrame.CardUpdates.TryGetValue(new InstanceId(weapon.InstanceId), out var weaponLastUpdate) &&
            weaponLastUpdate.Attributes.TryGetValue(ECardAttributeType.Ammo, out var lastAmmoDelta))
        {
            Assert.Equal(2, lastAmmoDelta.CurrentValue);
        }

        // 2. 验证战后 RunState 与 CombatDespawnSim 首帧恢复 Ammo=EffectiveAmmoMax(2) 及全部瞬态状态=0
        Assert.Equal(2, weapon.CoreCard.CurrentAmmo);
        Assert.Equal(2, weapon.Attributes[ECardAttributeType.Ammo]);
        var despawnWeapon = combatOut.CombatDespawnSim!.Cards[weapon.InstanceId];
        Assert.Equal(2, despawnWeapon.Attributes[ECardAttributeType.Ammo].Value);

        Assert.Equal(0, gadget.CoreCard!.FreezeMs);
        Assert.Equal(0, gadget.CoreCard.SlowMs);
        Assert.Equal(0, gadget.CoreCard.HasteMs);
        Assert.False(gadget.CoreCard.IsFlying);
        var despawnGadget = combatOut.CombatDespawnSim.Cards[gadget.InstanceId];
        Assert.Equal(0, despawnGadget.Attributes[ECardAttributeType.Freeze].Value);
        Assert.Equal(0, despawnGadget.Attributes[ECardAttributeType.Slow].Value);
        Assert.Equal(0, despawnGadget.Attributes[ECardAttributeType.Haste].Value);
        Assert.Equal(0, despawnGadget.Attributes[ECardAttributeType.Flying].Value);
    }

    // =========================================================================
    // 7. Wave 2 跨分支集成回归：Stickybeans 纯净跨英雄池 & RunEngine 升级派发联动
    // =========================================================================

    [Fact]
    public void Wave2_Stickybeans_ExcludePlayerHero_NeverSpawnsPureCommonOrOwnHero_WhileCurioStillAllowsCommon()
    {
        var fx = RealFixture.Value;
        const string stickybeansId = "d0276b47-be8a-4bbc-ab55-9f92b352480a";
        const string curioId = "c6cffe25-8e94-4a7b-a821-3e3c7093333b";
        const string crashSiteDestroyId = "d89ef0c8-5992-4f04-89dc-96b2a50073c4";

        EHero[] allHeroes =
        [
            EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Jules,
            EHero.Stelle, EHero.Mak, EHero.Karnok, EHero.Hero8
        ];

        // 1. Stickybeans (TSpawnBehaviorExcludePlayerHero) 全 8 英雄 40 次发牌：
        //    必须全部产自其他可玩英雄（非玩家自身，且绝不漏出纯 Common 怪物物品或 Loot）
        int totalStickyItems = 0;
        for (int i = 0; i < 40; i++)
        {
            var hero = allHeroes[i % allHeroes.Length];
            var (run, _) = CreateStartedEngine(fx, hero, seed: 5100 + i);
            run.Day = (uint)(2 + (i % 7));
            var shop = new ShopEngine(run, fx.Pool, fx.Repo);
            shop.DealEncounterShelf(stickybeansId, isReroll: false);
            Assert.NotEmpty(run.SelectionSet);
            foreach (string id in run.SelectionSet)
            {
                var entry = run.Cards[id];
                if (entry.Type != ECardType.Item) continue;
                totalStickyItems++;
                var tpl = fx.Repo.GetCard(entry.TemplateId.ToString())!;
                Assert.DoesNotContain(hero.ToString(), tpl.Heroes, StringComparer.OrdinalIgnoreCase);
                Assert.False(
                    tpl.Heroes.All(h => string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase)),
                    $"Stickybeans 不应刷出纯 Common 怪物物品: {tpl.InternalName}");
                Assert.Contains(
                    tpl.Heroes,
                    h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain("Loot", tpl.Tags, StringComparer.OrdinalIgnoreCase);
            }
        }
        Assert.True(totalStickyItems >= 80, $"Stickybeans 40 次刷店产出过少: {totalStickyItems}");

        // 2. Curio（显式 ConstraintHero=["Common"]）仍正常产出 Common 物品
        var (curioRun, _) = CreateStartedEngine(fx, EHero.Vanessa, seed: 7100);
        curioRun.Day = 3;
        var curioShop = new ShopEngine(curioRun, fx.Pool, fx.Repo);
        curioShop.DealEncounterShelf(curioId, isReroll: false);
        Assert.NotEmpty(curioRun.SelectionSet);
        Assert.All(curioRun.SelectionSet, id =>
        {
            var tpl = fx.Repo.GetCard(curioRun.Cards[id].TemplateId.ToString())!;
            Assert.Contains(HeroNames.Common, tpl.Heroes, StringComparer.OrdinalIgnoreCase);
        });

        // 3. Crash Site Destroy It (ExcludePlayerHero: 1 Tech from another Hero + 5 Loot items)
        var destroyTpl = fx.Repo.GetCard(crashSiteDestroyId)!;
        var pProbe = new PlayerState("p_crash", "Vanessa", HeroNames.Vanessa, 500, 20, 10, 10) { Day = 4 };
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, pProbe, null, new Random(8801))
        {
            CurrentDay = 4,
            CurrentEncounterId = crashSiteDestroyId,
            SelfCard = new CardInstance(destroyTpl),
        };
        var crashSpawned = SpawnQueryEngine.SpawnCards(destroyTpl.Abilities!["0"].Action!.SpawnContext!, ctx, validAssetIds: fx.ValidCardIds);
        Assert.Equal(6, crashSpawned.Count);
        Assert.Contains("Tech", crashSpawned[0].Template.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(HeroNames.Vanessa, crashSpawned[0].Template.Heroes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(crashSpawned[0].Template.Heroes, h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase));
        for (int k = 1; k < crashSpawned.Count; k++)
            Assert.Contains("Loot", crashSpawned[k].Template.Tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wave2_RunEngine_LevelUp_DispatchesOnPlayerLeveledUpAndLevelAttrChanged_AndSyncsDiffs()
    {
        const string synthLvlSkillId = "aaaaaaaa-2222-4444-8888-eeeeeeee0101";
        const string scavengerSkillId = "ee6059b6-f960-4ede-a65d-ad97cf4393b7";
        const string moneyTreeId = "49895243-8b5d-407f-9c8c-efdd05b3a34a";

        string lvlSkillJson = JsonSerializer.Serialize(new
        {
            __type = "TCardSkill",
            Id = synthLvlSkillId,
            Version = "5.0.0",
            InternalName = "V11 LevelUp Bonus Gold",
            Type = "Skill",
            Heroes = new[] { "Vanessa" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int>(),
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new
                    {
                        __type = "TTriggerOnPlayerLeveledUp",
                        Level = 5,
                        ComparisonOperator = "LessThanOrEqual",
                    },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        __type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Operation = "Add",
                        Value = new { __type = "TFixedValue", Value = 7.0 },
                        Target = new { __type = "TTargetPlayerRelative", TargetMode = "Self" },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture((synthLvlSkillId, lvlSkillJson));
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Vanessa, seed: 9201);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 1. 装备合成 TTriggerOnPlayerLeveledUp 技能、真实 Scavenger 技能（TTriggerOnPlayerAttributeChanged: Level）与真实 Money Tree 物品
        foreach (string skId in new[] { synthLvlSkillId, scavengerSkillId })
        {
            var skTpl = ov.Repo.GetCard(skId)!;
            var skCore = new CardInstance(skTpl, ShopEngine.ToCoreTier(ETier.Silver), null, $"sk_{skId[..8]}");
            var skEntry = new RunState.CardEntry
            {
                InstanceId = skCore.InstanceId,
                TemplateId = Guid.Parse(skId),
                Type = ECardType.Skill,
                Size = ECardSize.Medium,
                Tier = ETier.Silver,
                Owner = ECombatantId.Player,
                CoreCard = skCore,
            };
            RunState.PopulateMetaFromTemplate(skEntry, ov.Pool.Get(skId), skTpl);
            run.Cards[skEntry.InstanceId] = skEntry;
        }
        var moneyTree = AddOwnedItem(run, ov.Repo, ov.Pool, moneyTreeId, EInventorySection.Hand, slot: 0, tier: ETier.Bronze);
        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int itemCountBefore = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);

        // 2. 通过 CheatCommand 加经验触发升级（L1 -> L2）：
        //    - TTriggerOnPlayerLeveledUp (+7 Gold)
        //    - Scavenger (TTriggerOnPlayerAttributeChanged: Level) 产出 1 张来自其他可玩英雄的小型物品（非 Vanessa、非纯 Common）
        //    - Money Tree (TTriggerOnPlayerAttributeChanged: Level) 产出 1 张 Spare Change
        var xpToLevel = Math.Max(1, ov.Mode.ExperiencePerLevel);
        var outRes = engine.Handle(new CheatCommand { Args = new List<string> { "exp", xpToLevel.ToString() } });
        Assert.Null(outRes.Error);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(goldBefore + 7, run.PlayerAttributes[EPlayerAttributeType.Gold]);

        int itemCountAfter = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item);
        Assert.Equal(itemCountBefore + 2, itemCountAfter);
        Assert.Contains(outRes.Events, e => e is GameSimEventCardSpawned);

        var spawnedItems = run.Cards.Values
            .Where(c => c.Owner == ECombatantId.Player &&
                        c.Type == ECardType.Item &&
                        c.InstanceId != moneyTree.InstanceId)
            .ToList();
        Assert.Equal(2, spawnedItems.Count);
        Assert.Contains(spawnedItems, it =>
            string.Equals(ov.Repo.GetCard(it.TemplateId.ToString())?.InternalName, "Spare Change", StringComparison.OrdinalIgnoreCase));
        var scavengerItem = Assert.Single(spawnedItems, it =>
            !string.Equals(ov.Repo.GetCard(it.TemplateId.ToString())?.InternalName, "Spare Change", StringComparison.OrdinalIgnoreCase));
        var spawnedTpl = ov.Repo.GetCard(scavengerItem.TemplateId.ToString())!;
        Assert.Equal("Small", spawnedTpl.Size);
        Assert.DoesNotContain(HeroNames.Vanessa, spawnedTpl.Heroes, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(spawnedTpl.Heroes, h => !string.Equals(h, HeroNames.Common, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Gate3_BoardGrid_FullOrImpossibleFit_And_FishingNetOnDayStarted_FastFailUnder100ms()
    {
        var fx = RealFixture.Value;
        var fishingNetTpl = fx.Repo.GetCard("63e5a09e-ad7c-44ee-ba7d-d1b515ae8e9e")!;
        var smallTemplates = fx.Repo.CardsById.Values
            .Where(c => string.Equals(c.Type, "Item", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Size, "Small", StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();
        Assert.True(smallTemplates.Count >= 18);

        // 1. 满 10 格 BoardGrid：TryAddAuto / TryPlaceWithDisplaceOrSwap 应 O(1) 快速失败，避免回溯爆炸
        var fullGrid = new BoardGrid(10) { UnlockedMask = 1023 };
        for (int i = 0; i < 10; i++)
        {
            Assert.True(fullGrid.TryPlaceOrShift(new CardInstance(smallTemplates[i], CardTier.Bronze, null, $"g_{i}"), i));
        }
        var extraSmall = new CardInstance(smallTemplates[10], CardTier.Bronze, null, "extra_small");
        var swGrid = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(fullGrid.TryAddAuto(extraSmall));
        Assert.False(fullGrid.TryPlaceWithDisplaceOrSwap(extraSmall, 0));

        // 局部碎片无 2 连空位 + 中型卡不可容纳情形同样必须瞬时返回 false
        var tightGrid = new BoardGrid(10) { UnlockedMask = 1023 };
        for (int i = 0; i < 8; i++)
            Assert.True(tightGrid.TryPlaceOrShift(new CardInstance(smallTemplates[i], CardTier.Bronze, null, $"t_{i}"), i));
        var extraLarge = new CardInstance(fishingNetTpl, CardTier.Bronze, null, "net_extra");
        Assert.True(tightGrid.TryAddAuto(extraLarge)); // 占满最后 2 格 -> 共 10 格满
        Assert.False(tightGrid.TryAddAuto(new CardInstance(smallTemplates[11], CardTier.Bronze, null, "overflow_11")));
        swGrid.Stop();
        Assert.True(swGrid.ElapsedMilliseconds < 100, $"BoardGrid 满盘失败判定耗时过长: {swGrid.ElapsedMilliseconds}ms");

        // 2. 还原 Gate 3 卡顿场景：Hand 放 1 张 Fishing Net(Medium) + 8 张 Small，Stash 放 10 张 Small，触发 OnDayStarted 产小鱼卡
        var player = new PlayerState("p_full", "Vanessa", HeroNames.Vanessa, 2000, 20, 50, 10)
        {
            Day = 4,
            Hour = 1,
        };
        var fishNet = new CardInstance(fishingNetTpl, CardTier.Bronze, null, "hand_fishing_net") { StartSlot = 0 };
        player.Hand.UnlockedMask = 1023;
        player.Stash.UnlockedMask = 1023;
        player.Hand.Cards.Add(fishNet);
        for (int i = 0; i < 8; i++)
            player.Hand.Cards.Add(new CardInstance(smallTemplates[i], CardTier.Bronze, null, $"h_s_{i}") { StartSlot = 2 + i });
        for (int i = 0; i < 10; i++)
            player.Stash.Cards.Add(new CardInstance(smallTemplates[8 + i], CardTier.Bronze, null, $"s_s_{i}") { StartSlot = i });

        var swDay = System.Diagnostics.Stopwatch.StartNew();
        OutOfCombatController.OnDayStarted(player, fx.Repo, new Random(1));
        swDay.Stop();
        Assert.True(swDay.ElapsedMilliseconds < 250, $"满盘 FishingNet OnDayStarted 耗时过长: {swDay.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void Gate3_Persistence_IsSaneGhostForDay_AlignsWithPvpGhostPoolAndAcceptsDay2UpTo900()
    {
        for (int day = 1; day <= 20; day++)
        {
            Assert.Equal(
                PvpGhostPool.GetSaneBoundsForDay(day).MaxHp,
                BazaarServer.Storage.Persistence.MaxSaneGhostHealthForDay(day));
        }

        // Day 2 合法最高血量上限为 900（901 及以上应拒绝）
        Assert.True(BazaarServer.Storage.Persistence.IsSaneGhostForDay(2, 746));
        Assert.True(BazaarServer.Storage.Persistence.IsSaneGhostForDay(2, 900));
        Assert.False(BazaarServer.Storage.Persistence.IsSaneGhostForDay(2, 901));

        // 保留既有 Day 1 / Day 4 脏数据阈值不回归
        Assert.False(BazaarServer.Storage.Persistence.IsSaneGhostForDay(1, 610));
        Assert.True(BazaarServer.Storage.Persistence.IsSaneGhostForDay(4, 1500));
        Assert.False(BazaarServer.Storage.Persistence.IsSaneGhostForDay(4, 3065));
    }

    [Fact]
    public void Gate3_PostCombatAmmoSync_PendingForceSyncAndRebuildAndEnsureCoreCard_MatchAmmoMax()
    {
        const string synthAmmoSkillId = "aaaaaaaa-3333-4444-8888-eeeeeeee0202";
        const string revolverId = "92e7ab18-a035-43c7-a53c-965aeecbe357";

        string ammoSkillJson = JsonSerializer.Serialize(new
        {
            __type = "TCardSkill",
            Id = synthAmmoSkillId,
            Version = "5.0.0",
            InternalName = "V11 LevelUp Plus AmmoMax",
            Type = "Skill",
            Heroes = new[] { "Vanessa" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int>(),
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new
                    {
                        __type = "TTriggerOnPlayerLeveledUp",
                        Level = 5,
                        ComparisonOperator = "LessThanOrEqual",
                    },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        __type = "TActionCardModifyAttribute",
                        AttributeType = "AmmoMax",
                        Operation = "Add",
                        Value = new { __type = "TFixedValue", Value = 2.0 },
                        Target = new { __type = "TTargetCardSection", TargetSection = "SelfHand" },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture((synthAmmoSkillId, ammoSkillJson));
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Vanessa, seed: 9301);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var skTpl = ov.Repo.GetCard(synthAmmoSkillId)!;
        var skCore = new CardInstance(skTpl, ShopEngine.ToCoreTier(ETier.Bronze), null, "sk_ammo_lvl");
        var skEntry = new RunState.CardEntry
        {
            InstanceId = skCore.InstanceId,
            TemplateId = Guid.Parse(synthAmmoSkillId),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = skCore,
        };
        RunState.PopulateMetaFromTemplate(skEntry, ov.Pool.Get(synthAmmoSkillId), skTpl);
        run.Cards[skEntry.InstanceId] = skEntry;

        var revolver = AddOwnedItem(run, ov.Repo, ov.Pool, revolverId, EInventorySection.Hand, slot: 0, tier: ETier.Bronze);
        int baseAmmoMax = revolver.Attributes[ECardAttributeType.AmmoMax];
        Assert.True(baseAmmoMax > 0);

        // 模拟战后 ClearPostCombatTransientStatusAndSync 在升级/跨天之前先写入旧 PendingForceSyncAttributes[Ammo]
        revolver.PendingForceSyncAttributes[ECardAttributeType.Ammo] = baseAmmoMax;

        // 升级触发 OnPlayerLeveledUp -> 为 Revolver 永久增加 +2 AmmoMax 并补满 CurrentAmmo
        var xpToLevel = Math.Max(1, ov.Mode.ExperiencePerLevel);
        var lvlOut = engine.Handle(new CheatCommand { Args = new List<string> { "exp", xpToLevel.ToString() } });
        Assert.Null(lvlOut.Error);

        int expectedAmmoMax = baseAmmoMax + 2;
        Assert.Equal(expectedAmmoMax, revolver.Attributes[ECardAttributeType.AmmoMax]);
        Assert.Equal(expectedAmmoMax, revolver.Attributes[ECardAttributeType.Ammo]);
        Assert.Equal(expectedAmmoMax, revolver.PendingForceSyncAttributes[ECardAttributeType.Ammo]);

        var sim = RunSnapshotSerializer.ToSimCard(revolver, clearPendingForceSync: true);
        Assert.Equal(expectedAmmoMax, sim.Attributes[ECardAttributeType.Ammo].Value);

        // 校验快照往返 RebuildCoreCard 与清除 CoreCard 后通过 MoveItemCommand 触发 EnsureCoreCard 均保持满弹药且不污染 PermanentAdd["Ammo"]
        var dto = RunSnapshotSerializer.SerializeToDto(run);
        var restored = RunSnapshotSerializer.DeserializeFromDto(dto, ov.Repo);
        var restoredRev = restored.Cards[revolver.InstanceId];
        Assert.NotNull(restoredRev.CoreCard);
        Assert.Equal(expectedAmmoMax, restoredRev.CoreCard!.EffectiveAmmoMax);
        Assert.Equal(expectedAmmoMax, restoredRev.CoreCard.CurrentAmmo);
        Assert.False(restoredRev.CoreCard.PermanentAdd.ContainsKey("Ammo"));

        revolver.CoreCard = null;
        revolver.Attributes[ECardAttributeType.Ammo] = 0;
        var mvOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(revolver.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_1 },
        });
        Assert.Null(mvOut.Error);
        Assert.NotNull(revolver.CoreCard);
        Assert.Equal(expectedAmmoMax, revolver.CoreCard!.EffectiveAmmoMax);
        Assert.Equal(expectedAmmoMax, revolver.CoreCard.CurrentAmmo);
        Assert.Equal(expectedAmmoMax, revolver.Attributes[ECardAttributeType.Ammo]);
        Assert.False(revolver.CoreCard.PermanentAdd.ContainsKey("Ammo"));
    }

    [Fact]
    public void Gate3_SellCard_CompletingCardSpawningQuest_DoesNotThrowCollectionModified()
    {
        const string synthQuestSpawnerId = "aaaaaaaa-4444-4444-8888-eeeeeeee0303";
        const string spawnTargetId = CutlassId;

        string questSpawnerJson = JsonSerializer.Serialize(new
        {
            __type = "TCardItem",
            Id = synthQuestSpawnerId,
            Version = "5.0.0",
            InternalName = "V11 Quest Card Spawner",
            Type = "Item",
            Heroes = new[] { "Vanessa" },
            Size = "Small",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int> { ["SellPrice"] = 2 },
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new
                    {
                        __type = "TTriggerOnCardQuestCompleted",
                        Subject = new { __type = "TTargetCardSelf" },
                    },
                    ActiveIn = "HandAndStash",
                    Action = new
                    {
                        __type = "TActionGameSpawnCards",
                        TargetPlayer = new { __type = "TTargetPlayerAbsolute", TargetMode = "Player" },
                        SpawnContext = new
                        {
                            __type = "TSpawnContextQuery",
                            Groups = new[]
                            {
                                new
                                {
                                    __type = "TSpawnGroup",
                                    Filters = new object[]
                                    {
                                        new { __type = "TSpawnFilterIdList", Ids = new[] { spawnTargetId } },
                                    },
                                    SelectionMethod = "Random",
                                    Limit = new { MinValue = 1, MaxValue = 1 },
                                },
                            },
                            Limit = new { MinValue = 1, MaxValue = 1 },
                        },
                    },
                },
            },
            Auras = new { },
            Quests = new[]
            {
                new
                {
                    Prerequisites = Array.Empty<object>(),
                    Entries = new[]
                    {
                        new
                        {
                            AttributeType = "Quest_1",
                            Trigger = new
                            {
                                __type = "TTriggerOnCardSold",
                                Subject = new
                                {
                                    __type = "TTargetCardSection",
                                    TargetSection = "SelfHandAndStash",
                                    ExcludeSelf = true,
                                },
                            },
                            Target = 1,
                            Update = new
                            {
                                Value = new { __type = "TFixedValue", Value = 1.0 },
                            },
                        },
                    },
                },
            },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture((synthQuestSpawnerId, questSpawnerJson));
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Vanessa, seed: 9401);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var questItem = AddOwnedItem(run, ov.Repo, ov.Pool, synthQuestSpawnerId, EInventorySection.Hand, slot: 0, tier: ETier.Bronze);
        var soldItem = AddOwnedItem(run, ov.Repo, ov.Pool, CutlassId, EInventorySection.Stash, slot: 0, tier: ETier.Bronze);
        Assert.NotNull(questItem);

        // 出售另一张卡牌使任务在 TryAdvanceQuests 遍历中立即完成，并通过 OnQuestCompleted 衍生 Cutlass 到手牌（不得抛出 Collection was modified）
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(soldItem.InstanceId) });
        Assert.Null(sellOut.Error);
        Assert.False(run.Cards.ContainsKey(soldItem.InstanceId));
        Assert.Contains(run.Cards.Values, c =>
            c.Owner == ECombatantId.Player &&
            c.TemplateId == Guid.Parse(spawnTargetId) &&
            c.InstanceId != soldItem.InstanceId);
        Assert.Contains("Quest_1", questItem.CoreCard!.CompletedQuests);
    }

    [Fact]
    public void Gate3_PvpGhostPool_PickOpponent_FollowsTwoLayerMedianBandDistributionAndLateDayCaps()
    {
        var fx = RealFixture.Value;

        // 第二层：Day 1..4 仍严格拦截超界脏镜像，中后期合法高血量放行
        Assert.False(PvpGhostPool.IsSaneGhostForDay(new PvpGhostPool.GhostBoard { BoardId = "d1_bad", Day = 1, Level = 1, HealthMax = 620, Items = { new() { TemplateId = CutlassId } } }, 1));
        Assert.False(PvpGhostPool.IsSaneGhostForDay(new PvpGhostPool.GhostBoard { BoardId = "d4_bad", Day = 4, Level = 4, HealthMax = 3065, Items = { new() { TemplateId = CutlassId } } }, 4));
        Assert.True(PvpGhostPool.IsSaneGhostForDay(new PvpGhostPool.GhostBoard { BoardId = "d7_ok", Day = 7, Level = 10, HealthMax = 9500, Items = { new() { TemplateId = CutlassId } } }, 7));
        Assert.True(PvpGhostPool.IsSaneGhostForDay(new PvpGhostPool.GhostBoard { BoardId = "d10_ok", Day = 10, Level = 18, HealthMax = 24000, Items = { new() { TemplateId = CutlassId } } }, 10));
        Assert.True(PvpGhostPool.IsSaneGhostForDay(new PvpGhostPool.GhostBoard { BoardId = "d14_ok", Day = 14, Level = 26, HealthMax = 55000, Items = { new() { TemplateId = CutlassId } } }, 14));

        // 第一层：注入大量高压超高尾真人镜像，验证 PickOpponent 仍将正常频段维持 >= 95%、超高尾 <= 5%
        var pool = PvpGhostPool.Load(fx.DataDir);
        var tempDir = Directory.CreateTempSubdirectory("v11_ghost_dist_");
        try
        {
            var db = new BazaarServer.Storage.Persistence(Path.Combine(tempDir.FullName, "dist_test.db"));
            var fiveMediums = fx.Repo.CardsById.Values
                .Where(c => c.Type == "Item" && c.CardSize == BazaarCore.Models.CardSize.Medium && c.SpawningEligibility == "Always")
                .Take(5)
                .ToList();
            Assert.Equal(5, fiveMediums.Count);

            void InjectPlayerGhost(int day, int level, int healthMax, int wins, string idSuffix)
            {
                var r = new RunState
                {
                    RunId = $"run_dist_{day}_{idSuffix}",
                    GameModeId = fx.Pool.DefaultGameModeId,
                    Hero = EHero.Jules,
                    Day = (uint)day,
                    Hour = 6,
                    Victories = (uint)wins,
                    Defeats = 1,
                    UnlockedSlots = 1023,
                };
                r.PlayerAttributes[EPlayerAttributeType.HealthMax] = healthMax;
                r.PlayerAttributes[EPlayerAttributeType.Level] = level;
                r.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
                for (int k = 0; k < fiveMediums.Count; k++)
                {
                    var tpl = fiveMediums[k];
                    r.Cards[$"g_itm_{k}"] = new RunState.CardEntry
                    {
                        InstanceId = $"g_itm_{k}",
                        TemplateId = Guid.Parse(tpl.Id),
                        Type = ECardType.Item,
                        Size = ECardSize.Medium,
                        Tier = ETier.Gold,
                        Owner = ECombatantId.Player,
                        Section = EInventorySection.Hand,
                        Socket = (EContainerSocketId)(k * 2),
                    };
                }
                Assert.NotNull(pool.RecordPlayerGhost(r, db));
            }

            // Day 7 (median=2200, 1.8x=3960, 2.2x=4840) 注入 25 条超高尾 + 5 条正常带
            for (int i = 0; i < 25; i++) InjectPlayerGhost(7, 10, 6200 + i * 100, 5, $"tail_{i}");
            for (int i = 0; i < 5; i++) InjectPlayerGhost(7, 8, 2200 + i * 150, 5, $"norm_{i}");

            // Day 10 (median=4400, 1.8x=7920, 2.2x=9680) 注入 25 条超高尾 + 5 条正常带
            for (int i = 0; i < 25; i++) InjectPlayerGhost(10, 17, 12000 + i * 350, 7, $"tail_{i}");
            for (int i = 0; i < 5; i++) InjectPlayerGhost(10, 13, 4400 + i * 250, 7, $"norm_{i}");

            var rng = new Random(20261121);
            int d7Normal = 0, d7Tail = 0;
            for (int i = 0; i < 1000; i++)
            {
                var opp = pool.PickOpponent(7, playerWins: 5, playerLosses: 1, rng, fx.Repo);
                if (opp.HealthMax <= 4000) d7Normal++;
                if (opp.HealthMax > (int)Math.Round(PvpGhostPool.GetExpectedMedianHealthForDay(7) * 2.20)) d7Tail++;
            }
            Assert.True(d7Normal >= 900, $"Day 7 <= 4000 命中率过低: {d7Normal}/1000");
            Assert.True(d7Tail <= 50, $"Day 7 >2.2x 中位数超高尾过高: {d7Tail}/1000");

            int d10Normal = 0, d10Tail = 0;
            for (int i = 0; i < 1000; i++)
            {
                var opp = pool.PickOpponent(10, playerWins: 7, playerLosses: 1, rng, fx.Repo);
                if (opp.HealthMax <= 8500) d10Normal++;
                if (opp.HealthMax > (int)Math.Round(PvpGhostPool.GetExpectedMedianHealthForDay(10) * 2.20)) d10Tail++;
            }
            Assert.True(d10Normal >= 900, $"Day 10 <= 8500 命中率过低: {d10Normal}/1000");
            Assert.True(d10Tail <= 50, $"Day 10 >2.2x 中位数超高尾过高: {d10Tail}/1000");
        }
        finally
        {
            try { tempDir.Delete(recursive: true); } catch { }
        }
    }

    [Fact]
    public void Gate3_LargeAppetites_DeactivatesOnHand6ItemsDuringLevelUp_ComputesExactHealthMax()
    {
        const string largeAppetitesId = "19c967e5-f4c0-4c11-bb96-aa039369600a";
        const string synthLvlSpawnId = "aaaaaaaa-5555-4444-8888-eeeeeeee0404";

        var fx = RealFixture.Value;
        var smallItemIds = fx.Repo.CardsById.Values
            .Where(c => c.Type == "Item" && c.CardSize == BazaarCore.Models.CardSize.Small && c.SpawningEligibility == "Always")
            .Select(c => c.Id)
            .Take(6)
            .ToList();
        Assert.Equal(6, smallItemIds.Count);

        string lvlSpawnSkillJson = JsonSerializer.Serialize(new
        {
            __type = "TCardSkill",
            Id = synthLvlSpawnId,
            Version = "5.0.0",
            InternalName = "V11 LevelUp Spawn 6th Item",
            Type = "Skill",
            Heroes = new[] { "Dooley" },
            Size = "Medium",
            StartingTier = "Bronze",
            SpawningEligibility = "Never",
            Tiers = new
            {
                Bronze = new
                {
                    Attributes = new Dictionary<string, int>(),
                    AbilityIds = new[] { "0" },
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { __type = "TTriggerOnPlayerLeveledUp" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        __type = "TActionGameSpawnCards",
                        TargetPlayer = new { __type = "TTargetPlayerAbsolute", TargetMode = "Player" },
                        SpawnContext = new
                        {
                            __type = "TSpawnContextQuery",
                            Groups = new[]
                            {
                                new
                                {
                                    __type = "TSpawnGroup",
                                    Filters = new object[]
                                    {
                                        new { __type = "TSpawnFilterIdList", Ids = new[] { smallItemIds[5] } },
                                    },
                                    SelectionMethod = "Random",
                                    Limit = new { MinValue = 1, MaxValue = 1 },
                                },
                            },
                            Limit = new { MinValue = 1, MaxValue = 1 },
                        },
                    },
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");

        using var ov = new OverlayFixture((synthLvlSpawnId, lvlSpawnSkillJson));
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, EHero.Dooley, seed: 9501);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 赋予 Gold Large Appetites (+1000 MaxHP 当手牌 <= 5)，并在 Hand 放置 5 件小物品使光环激活
        var laTpl = ov.Repo.GetCard(largeAppetitesId)!;
        var laCore = new CardInstance(laTpl, BazaarCore.Models.CardTier.Gold, null, "sk_large_appetites");
        var laEntry = new RunState.CardEntry
        {
            InstanceId = laCore.InstanceId,
            TemplateId = Guid.Parse(largeAppetitesId),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Gold,
            Owner = ECombatantId.Player,
            CoreCard = laCore,
        };
        RunState.PopulateMetaFromTemplate(laEntry, ov.Pool.Get(largeAppetitesId), laTpl);
        run.Cards[laEntry.InstanceId] = laEntry;

        for (int i = 0; i < 5; i++)
            AddOwnedItem(run, ov.Repo, ov.Pool, smallItemIds[i], EInventorySection.Hand, slot: i, tier: ETier.Bronze);

        // 移动第 0 张牌到空槽位 6 以刷新一次局外光环，确认 Large Appetites Gold (+1000) 已生效
        var firstItem = run.Cards.Values.First(c => c.Type == ECardType.Item && c.Socket == EContainerSocketId.Socket_0);
        engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(firstItem.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_6 },
        });
        int hpWithAura = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        Assert.Equal(300 + 1000, hpWithAura);

        // 加上升级衍生第 6 张手牌的技能，设定当前等级=13，升至 14 级 (+800 MaxHP) 同时手牌增至 6 张导致 -1000 光环关闭 => 净变化正好 -200
        var spTpl = ov.Repo.GetCard(synthLvlSpawnId)!;
        var spCore = new CardInstance(spTpl, BazaarCore.Models.CardTier.Bronze, null, "sk_lvl_spawn6");
        run.Cards[spCore.InstanceId] = new RunState.CardEntry
        {
            InstanceId = spCore.InstanceId,
            TemplateId = Guid.Parse(synthLvlSpawnId),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = spCore,
        };
        run.PlayerAttributes[EPlayerAttributeType.Level] = 13;
        int lvl14Inc = ov.LevelUps.HealthIncreaseFor(14);
        int xpToLevel = Math.Max(1, ov.Mode.ExperiencePerLevel);
        var lvlOut = engine.Handle(new CheatCommand { Args = new List<string> { "exp", xpToLevel.ToString() } });
        Assert.Null(lvlOut.Error);

        int handItemsAfter = run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.Section == EInventorySection.Hand);
        Assert.Equal(6, handItemsAfter);
        int hpAfter = run.PlayerAttributes[EPlayerAttributeType.HealthMax];
        Assert.Equal(hpWithAura + lvl14Inc - 1000, hpAfter);

        // 将第 6 件物品移入 Stash (Hand 回到 5 件) 后，Large Appetites (+1000) 应无损重新激活
        var sixthItem = run.Cards.Values.First(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item && c.TemplateId == Guid.Parse(smallItemIds[5]));
        var moveOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(sixthItem.InstanceId),
            Section = EInventorySection.Stash,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_0 },
        });
        Assert.Null(moveOut.Error);
        Assert.Equal(hpWithAura + lvl14Inc, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
    }

    [Fact]
    public void Gate3_RunEngine_Day25Hour6Pvp_SafetyCapEndsAtDay25WithExactWlInvariant()
    {
        var fx = RealFixture.Value;

        // 构造一个包含 1 名其他存活真人的 2 人锦标赛房，使 LastHumanStandingMode 抑制 10 胜提前结束
        var acc0 = new BazaarServer.Accounts.AccountStore.Account { AccountId = Guid.NewGuid(), Username = "H0", IdentityKey = "h0", AccessToken = "t0", RefreshToken = "r0" };
        var acc1 = new BazaarServer.Accounts.AccountStore.Account { AccountId = Guid.NewGuid(), Username = "H1", IdentityKey = "h1", AccessToken = "t1", RefreshToken = "r1" };
        var lobbies = new BazaarServer.Tournament.LobbyStore();
        var host = lobbies.Create(acc0, "D25Test", "Tournament", 2, isPublic: false, rules: new CustomRunRuleSet { LastHumanStandingMode = true });
        Assert.NotNull(lobbies.Join(host.Code, acc1));
        lobbies.StartCountdown(host.LobbyId, Guid.NewGuid());
        var m1 = host.Members.First(m => m.AccountId == acc1.AccountId.ToString("N"));
        m1.CurrentDay = 25;
        m1.CurrentHour = 6;
        m1.IsReadyAtHour6Pvp = true;

        var run = new RunState
        {
            RunId = "v11_d25_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId != Guid.Empty ? fx.Pool.DefaultGameModeId : Guid.NewGuid(),
            Hero = EHero.Vanessa,
            PlayMode = EPlayMode.Tournament,
            TournamentSessionId = host.TournamentSessionId,
            AccountId = acc0.AccountId.ToString("N"),
            CustomRules = host.Rules,
            RngSeed = 9601,
        };
        var trnEngine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts, lobbies: lobbies);
        Assert.Null(trnEngine.StartRun().Error);
        trnEngine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        AddOwnedItem(run, fx.Repo, fx.Pool, CutlassId, EInventorySection.Hand, slot: 0, tier: ETier.Bronze);

        // 模拟 Day 25 Hour 6 PvP 前状态（V+L=24, Prestige=20），并确保进入当日 Hour 6 PvP 遭遇
        run.Day = 25;
        run.Hour = 6;
        run.Victories = 14;
        run.Defeats = 10;
        run.PlayerAttributes[EPlayerAttributeType.Prestige] = 20;
        run.PlayerAttributes[EPlayerAttributeType.HealthMax] = 2000;
        run.PlayerAttributes[EPlayerAttributeType.Health] = 2000;
        run.State = ERunState.Choice;

        var encCard = InjectChoiceEncounter(run, fx.Pool, ShopEngine.Hour6PvpCardId, ECardType.PvpEncounter);
        var pvpOut = trnEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(encCard.InstanceId) });
        Assert.Null(pvpOut.Error);

        // 应直接于 Day 25 Hour 6 终局且 V+L == 25 == Day，不再多推进到 Day 26 Hour 3
        Assert.Equal(25u, run.Day);
        Assert.Equal(25u, run.Victories + run.Defeats);
        Assert.True(run.State is ERunState.EndRunDefeat or ERunState.EndRunVictory);
    }
}

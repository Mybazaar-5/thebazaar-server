using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BazaarCore.Combat;
using BazaarCore.Data;
using BazaarCore.Engine;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Effect;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarGameShared.Infra.Messages.Shared;
using MessagePack;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V6 全量用户反馈 BUG 回归测试套件（覆盖 Bug #1 ~ #17 及 Bug #14 保留项）。
/// </summary>
public sealed class V6UserReportedBugsTests
{
    private const string ImprovisedBurnSkillId = "1e8293e9-c60f-464a-9d44-21e766978edc";
    private const string SalvageYardEncounterId = "a79a4688-752d-4b4f-90b3-c92b3a4d1a92";
    private const string SwoleSkillId = "c08396cd-ffc6-4b3f-86f8-18c053bee76f";
    private const string JungleRuinsEncounterId = "1e511488-af8e-4852-a46a-0f27a2b07aac";
    private const string HuntForReagentsStepId = "9a2dc8e3-e313-46c0-8e07-9552a5f8b32c";
    private const string DuctTapeItemId = "0966f887-5aaf-44a5-90fa-ecb194270513";
    private const string CutlassItemId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string SwashBuckleItemId = "69688b74-fd2b-42a2-a018-2b720a13f95a";
    private const string RocketDroneItemId = "560450e3-8700-4ef9-b7c1-d3ae9937b55e";
    private const string EagleTalismanLootId = "0022c409-c839-41e8-8022-65a407457dfe";
    private const string BoarriorMonsterId = "6d939dd0-fd9b-46b8-8861-cccb818757da";
    private const string BanannibalMonsterId = "7e8b7521-af5e-4c81-93c1-694e823bc5de";
    private const string OverwhelmedStudentMonsterId = "77a31f8e-77b1-431f-aab8-22bbe5c3826b";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";

    private sealed class RealFixture
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public string ZhPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public Dictionary<string, string> RawCardsJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawMonstersJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawGameModesJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<RealFixture> Fixture = new(LoadFixture);

    private static string ResolveDataDir()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "dist", "data");
            if (File.Exists(Path.Combine(candidate, "GameData.db")))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        const string fallback = @"e:\thebazaar\dist\data";
        if (File.Exists(Path.Combine(fallback, "GameData.db")))
            return fallback;

        throw new FileNotFoundException("未找到 dist/data/GameData.db");
    }

    private static RealFixture LoadFixture()
    {
        string dataDir = ResolveDataDir();
        string dbPath = Path.Combine(dataDir, "GameData.db");
        string zhPath = Path.Combine(dataDir, "translations", "zh-CN.bytes");

        var rawCards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawMonsters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            ReadBlobTable(conn, "cards", rawCards);
            ReadBlobTable(conn, "monsters", rawMonsters);
            ReadBlobTable(conn, "game_modes", rawModes);
        }

        return new RealFixture
        {
            DataDir = dataDir,
            DbPath = dbPath,
            ZhPath = zhPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, File.Exists(zhPath) ? zhPath : null),
            Ghosts = PvpGhostPool.Load(dataDir),
            RawCardsJson = rawCards,
            RawMonstersJson = rawMonsters,
            RawGameModesJson = rawModes,
        };
    }

    private static void ReadBlobTable(SqliteConnection conn, string table, Dictionary<string, string> dest)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Data FROM {table}";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string id = reader.GetString(0);
            string json = reader.GetValue(1) is byte[] bytes
                ? Encoding.UTF8.GetString(bytes)
                : reader.GetString(1);
            dest[id] = json;
        }
    }

    private sealed class OverlayEnv : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _tempDbPath;
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayEnv(params (string Id, string Json)[] customCards)
        {
            var fx = Fixture.Value;
            _tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_v6_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            _tempDbPath = Path.Combine(_tempDir, "GameData.db");
            File.Copy(fx.DbPath, _tempDbPath, overwrite: true);

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

            using (var conn = new SqliteConnection($"Data Source={_tempDbPath}"))
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

            File.WriteAllText(Path.Combine(_tempDir, "valid_card_ids.json"), JsonSerializer.Serialize(wl.ToList()));

            Pool = CardPool.Load(_tempDbPath);
            Monsters = MonsterPool.Load(_tempDbPath);
            LevelUps = LevelUpTable.Load(_tempDbPath);
            Mode = GameModeConfig.Load(_tempDbPath);
            Repo = GameDatabaseLoader.Load(_tempDbPath, File.Exists(fx.ZhPath) ? fx.ZhPath : null);
            Ghosts = fx.Ghosts;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        EHero hero = EHero.Vanessa,
        EPlayMode playMode = EPlayMode.Unranked,
        RealFixture? fxOverride = null)
    {
        var fx = fxOverride ?? Fixture.Value;
        var run = new RunState
        {
            RunId = "v6_test_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = playMode,
        };
        var engine = new RunEngine(run, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        var startOut = engine.StartRun();
        Assert.Null(startOut.Error);
        return (run, engine);
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string encTemplateId,
        ECardType cardType = ECardType.EventEncounter)
    {
        var tpl = pool.Get(encTemplateId) ?? throw new InvalidOperationException($"Encounter template not found: {encTemplateId}");
        var entry = new RunState.CardEntry
        {
            InstanceId = "enc_inj_" + Guid.NewGuid().ToString("N")[..6],
            TemplateId = Guid.Parse(tpl.Id),
            Type = cardType,
            Size = ShopEngine.ParseSize(tpl.Size),
            Tier = ShopEngine.ParseTier(tpl.StartingTier),
            Owner = null,
        };
        RunState.PopulateMetaFromTemplate(entry, tpl, null);
        run.Cards[entry.InstanceId] = entry;
        run.SelectionSet.Add(entry.InstanceId);
        return entry;
    }

    private static RunState.CardEntry AddOwnedHandItem(
        RunState run,
        GameDataRepository repo,
        CardPool pool,
        string templateId,
        int slot,
        ETier? tierOverride = null)
    {
        var coreTpl = repo.GetCard(templateId) ?? throw new InvalidOperationException($"Core template not found: {templateId}");
        var poolTpl = pool.Get(templateId);
        ETier tier = tierOverride ?? ShopEngine.ParseTier(coreTpl.StartingTier);
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "itm_v6_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        Assert.True(run.HandGrid.TryPlaceOrShift(core, slot), $"无法把 {templateId} 放入手牌槽位 {slot}");

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(core.Template.Id),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(core.Template.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = (EContainerSocketId)core.StartSlot,
            CoreCard = core,
        };
        RunState.PopulateMetaFromTemplate(entry, poolTpl, core.Template);
        run.SyncEntryFromCoreCardAndDiff(entry, repo.GameMode.StandardPrices, emitAuraRefreshEvent: false);
        var (buy, sell) = ShopEngine.PriceFor(entry.Size, entry.Tier);
        if (!entry.Attributes.ContainsKey(ECardAttributeType.BuyPrice))
            entry.Attributes[ECardAttributeType.BuyPrice] = buy;
        if (!entry.Attributes.ContainsKey(ECardAttributeType.SellPrice))
            entry.Attributes[ECardAttributeType.SellPrice] = sell;
        run.Cards[entry.InstanceId] = entry;
        return entry;
    }

    // ── Bug #1：全英雄 Day 1 Hour 1 直接进入 Choice，多阶/高起步阶技能夹紧不归零 ────

    [Fact]
    public void Bug01_StartRunAllHeroes_EnterDay1Hour1Choice_AndSkillClampsAttributes()
    {
        var fx = Fixture.Value;
        var heroes = new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak, EHero.Stelle, EHero.Jules };

        foreach (var hero in heroes)
        {
            var (run, engine) = CreateStartedEngine(hero);
            Assert.Equal(1u, run.Day);
            Assert.Equal(1u, run.Hour);
            Assert.Equal(ERunState.Choice, run.State);
            Assert.True(engine.StartSelectionDone);
            Assert.Equal(3, run.SelectionSet.Count);
            Assert.All(run.SelectionSet, id =>
            {
                var e = run.Cards[id];
                Assert.Equal(ECardType.EventEncounter, e.Type);
                Assert.Equal(ETier.Bronze, e.Tier);
            });
            Assert.DoesNotContain(run.Cards.Values, c => c.Owner == ECombatantId.Player && c.Type == ECardType.Skill);

            // cheat restart 同样必须回到 Day 1 Hour 1 Choice 且 StartSelectionDone=true
            var restartOut = engine.Handle(new CheatCommand { Args = ["restart"] });
            Assert.Null(restartOut.Error);
            Assert.Equal(ERunState.Choice, run.State);
            Assert.True(engine.StartSelectionDone);
            Assert.Equal(3, run.SelectionSet.Count);
        }

        // 临时灼烧 1e8293e9（仅支持 Gold/Diamond）：请求 Bronze 时必须夹紧至 Gold，Custom_0 > 0
        var burnTpl = fx.Repo.GetCard(ImprovisedBurnSkillId)!;
        Assert.Equal(CardTier.Gold, SpawnQueryEngine.ClampToSupportedTier(burnTpl, CardTier.Bronze));

        var poolTpl = fx.Pool.Get(ImprovisedBurnSkillId)!;
        var testRun = new RunState
        {
            RunId = "v6_skill_clamp",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
        };
        var shop = new ShopEngine(testRun, fx.Pool, fx.Repo);
        var skEntry = shop.CreateSkillCandidate(poolTpl, ETier.Bronze, isFree: true);
        Assert.Equal(ETier.Gold, skEntry.Tier);
        Assert.True(skEntry.Attributes.GetValueOrDefault(ECardAttributeType.Custom_0, 0) > 0,
            "临时灼烧 1e8293e9 夹紧后 Custom_0 应 > 0");
    }

    // ── Bug #2：Day 1 商店与遭遇仅出青铜品质，绝不泄露 Gold/Diamond 或 Salvage Yard ──

    [Fact]
    public void Bug02_Day1ShopAndEncounters_ShouldBe100PercentBronze_NoSalvageYardLeak()
    {
        var fx = Fixture.Value;
        var chances = new TierChances { CurrentDay = 1 };

        // Day 1 RollShopItems（60 轮 * 3 件 = 180 次抽卡）必须 100% Bronze
        foreach (var hero in new[] { "Vanessa", "Pygmalien", "Dooley" })
        {
            for (int i = 0; i < 25; i++)
            {
                var rolled = fx.Pool.RollShopItems(hero, day: 1, count: 3, chances);
                Assert.NotEmpty(rolled);
                foreach (var (tpl, tierStr) in rolled)
                {
                    Assert.Equal("Bronze", tierStr);
                    Assert.Equal("Bronze", tpl.StartingTier, ignoreCase: true);
                }
            }
        }

        // Day 1 DealHourEncounters（Hour 1,2,4,5 x 多英雄）必须 100% Bronze，且绝不出现 Salvage Yard
        foreach (var hero in new[] { EHero.Vanessa, EHero.Pygmalien, EHero.Dooley, EHero.Mak })
        {
            foreach (uint hour in new[] { 1u, 2u, 4u, 5u })
            {
                for (int r = 0; r < 10; r++)
                {
                    var run = new RunState
                    {
                        RunId = $"d1_h{hour}_{r}",
                        GameModeId = fx.Pool.DefaultGameModeId,
                        Hero = hero,
                        Day = 1,
                        Hour = hour,
                        State = ERunState.Choice,
                    };
                    var shop = new ShopEngine(run, fx.Pool, fx.Repo);
                    shop.DealHourEncounters(new PlayerState("p", "P", hero.ToString(), 250, 20, 8, 10) { Day = 1, Hour = (int)hour });

                    Assert.NotEmpty(run.SelectionSet);
                    foreach (string id in run.SelectionSet)
                    {
                        var e = run.Cards[id];
                        Assert.Equal(ETier.Bronze, e.Tier);
                        Assert.NotEqual(Guid.Parse(SalvageYardEncounterId), e.TemplateId);
                    }
                }
            }
        }
    }

    // ── Bug #3 & #12 & #16：进场经验延迟到离场结算、禁止同批多状态融合、标准升级 3 选卡与空升级自愈 ──

    [Fact]
    public void Bug03_12_16_DeferredEncounterXp_NoFusedLevelUp_NonEmptyAndResumeSelfHeal()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);

        // 1. 距离升级仅差 1 XP：进入 ExperienceAwardUponSelection=1 的 Salvage Yard，必须留在 Encounter 且货架完整
        int xpNeeded = fx.Mode.ExperiencePerLevel;
        Assert.True(xpNeeded >= 2);
        run.PlayerAttributes[EPlayerAttributeType.Experience] = xpNeeded - 1;

        var syOpt = InjectChoiceEncounter(run, fx.Pool, SalvageYardEncounterId);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(syOpt.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.False(run.PendingLevelUp);
        Assert.Equal(1, run.PendingEncounterExitXp);
        Assert.NotEmpty(run.SelectionSet);
        Assert.True(enterOut.Events.Count(e => e is GameSimEventStateTransitioned) <= 1);

        // 离开 Salvage Yard 时结算延迟经验并正常进入 LevelUp（3 选项非空）
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(2, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PendingEncounterExitXp);
        Assert.True(run.SelectionSet.Count >= 2, $"LevelUp 候选数应 >= 2，实际={run.SelectionSet.Count}");
        Assert.True(exitOut.Events.Count(e => e is GameSimEventStateTransitioned) <= 1);

        // 2. 在多步奇遇中，Step 触发 DealCards（Replace）+ 给足升级经验时，绝不把 LevelUp 融进同一事件批
        const string dealXpStepId = "99999999-6666-4666-8666-666666666601";
        const string dealXpEncId = "99999999-6666-4666-8666-666666666602";
        string stepJson = $$"""
        {
          "$type": "TCardEncounterStep",
          "Id": "{{dealXpStepId}}",
          "Type": "EncounterStep",
          "InternalName": "V6 DealAndXp Step",
          "StartingTier": "Bronze",
          "Size": "Medium",
          "Heroes": ["Common"],
          "SpawningEligibility": "Always",
          "ShouldProgressHour": true,
          "ExperienceAwardUponSelection": 10,
          "Abilities": {
            "0": {
              "Id": "0",
              "Trigger": { "$type": "TTriggerOnCardSelected" },
              "ActiveIn": "HandOnly",
              "WorksIn": "Anywhere",
              "Action": {
                "$type": "TActionGameDealCards",
                "SpawnMode": "Replace",
                "SpawnContext": {
                  "$type": "TSpawnContextQuery",
                  "SelectionMethod": "Random",
                  "Limit": { "$type": "TFixedValue", "Value": 3.0 },
                  "Groups": [{
                    "$type": "TSpawnGroup",
                    "SelectionMethod": "Random",
                    "Filters": [{
                      "$type": "TSpawnFilterQuery",
                      "Constraints": { "$type": "ConstraintCardType", "Types": ["Item"], "IsNot": false }
                    }]
                  }]
                }
              }
            }
          }
        }
        """;
        string encJson = $$"""
        {
          "$type": "TCardEncounterEvent",
          "Id": "{{dealXpEncId}}",
          "Type": "EventEncounter",
          "InternalName": "V6 MultiStep Event",
          "StartingTier": "Bronze",
          "Size": "Medium",
          "Heroes": ["Common"],
          "SpawningEligibility": "Always",
          "SelectionContext": {
            "SpawnContext": {
              "$type": "TSpawnContextQuery",
              "SelectionMethod": "Sequential",
              "Groups": [{
                "$type": "TSpawnGroup",
                "SelectionMethod": "Sequential",
                "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{dealXpStepId}}"] }]
              }]
            },
            "Rules": { "CanSelectMultiple": false, "SelectionIsFree": true, "CanExit": true }
          }
        }
        """;

        using var ov = new OverlayEnv((dealXpStepId, stepJson), (dealXpEncId, encJson));
        var ovRun = new RunState
        {
            RunId = "v6_no_fuse",
            GameModeId = ov.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
        };
        var ovEngine = new RunEngine(ovRun, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        ovEngine.StartRun();

        var customOpt = InjectChoiceEncounter(ovRun, ov.Pool, dealXpEncId);
        Assert.Null(ovEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(customOpt.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, ovRun.State);
        var stepCand = ovRun.Cards[Assert.Single(ovRun.SelectionSet)];

        var stepOut = ovEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepCand.InstanceId) });
        Assert.Null(stepOut.Error);
        Assert.Equal(ERunState.Encounter, ovRun.State);
        Assert.True(ovRun.PendingLevelUp);
        Assert.Equal(3, ovRun.SelectionSet.Count);
        Assert.True(stepOut.Events.Count(e => e is GameSimEventStateTransitioned) <= 1,
            "多步发牌同批不得融合进入 LevelUp");

        // 3. 标准升级多选覆盖（对齐 GameData.db targetNewLevel->DB Id=lvl-1 真值：空棋盘 Lv2(Id1)=2 / Lv4(Id3)=2 / Lv5(Id4)=1 / Lv7(Id6)=1 / Lv10(Id9)=2，其余 >= 3，绝不再强塞无关青铜技能凑数）
        foreach (int lvl in new[] { 2, 3, 4, 5, 6, 7, 8, 9, 10 })
        {
            var lvRun = new RunState { RunId = $"lvl_{lvl}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = (uint)lvl };
            var lvShop = new ShopEngine(lvRun, fx.Pool, fx.Repo);
            lvShop.DealLevelUpChoices(lvl, fx.LevelUps, new PlayerState("p", "P", "Vanessa", 250, 20, 8, 10) { Level = lvl, Day = lvl });
            Assert.Equal(ERunState.LevelUp, lvRun.State);
            int expectedMin = lvl switch
            {
                2 => 2,
                4 => 2,
                5 => 1,
                7 => 1,
                10 => 2,
                _ => 3,
            };
            Assert.True(lvRun.SelectionSet.Count >= expectedMin,
                $"Lv{lvl} 升级选项数应 >= {expectedMin}，实际={lvRun.SelectionSet.Count}");
            Assert.All(lvRun.SelectionSet, id => Assert.True(fx.Pool.IsPlayerSpawnableId(lvRun.Cards[id].TemplateId.ToString(), allowGuidOnly: true)));
        }

        // 3b. 当棋盘持有符合条件的 Rage + HeatedReference + ChilledReference 物品时，Lv2/Lv4/Lv5（对应 DB Id=1/3/4）均自然产出 >= 3 个官方真实选项
        var rageTpl = fx.Repo.GetCard("02d7a406-a6eb-4b46-8975-1f69b0fde428")!;
        var heatedTpl = fx.Repo.GetCard("13081c31-f8b4-400d-bedc-8f311b08153b")!;
        var chilledTpl = fx.Repo.GetCard("1d19c55c-bfe7-469c-9a98-5c54ab8f5319")!;
        foreach (int lvl in new[] { 2, 4, 5 })
        {
            var condRun = new RunState { RunId = $"lvl_cond_{lvl}", GameModeId = fx.Pool.DefaultGameModeId, Hero = EHero.Vanessa, Day = (uint)lvl };
            var condShop = new ShopEngine(condRun, fx.Pool, fx.Repo);
            var condPlayer = new PlayerState("p", "P", "Vanessa", 250, 20, 8, 10) { Level = lvl, Day = lvl };
            condPlayer.Hand.UnlockedMask = 1023;
            Assert.True(condPlayer.Hand.TryPlaceOrShift(new CardInstance(rageTpl, CardTier.Bronze, null, "v6_rage"), 0));
            Assert.True(condPlayer.Hand.TryPlaceOrShift(new CardInstance(heatedTpl, CardTier.Bronze, null, "v6_heat"), 2));
            Assert.True(condPlayer.Hand.TryPlaceOrShift(new CardInstance(chilledTpl, CardTier.Bronze, null, "v6_chill"), 4));

            condShop.DealLevelUpChoices(lvl, fx.LevelUps, condPlayer);
            Assert.Equal(ERunState.LevelUp, condRun.State);
            Assert.True(condRun.SelectionSet.Count >= 3,
                $"带 Rage/Heated/Chilled 棋盘的 Lv{lvl} 升级选项数应 >= 3，实际={condRun.SelectionSet.Count}");
            Assert.All(condRun.SelectionSet, id => Assert.True(fx.Pool.IsPlayerSpawnableId(condRun.Cards[id].TemplateId.ToString(), allowGuidOnly: true)));
        }

        // 4. 存档恢复时处于 LevelUp 但 SelectionSet 为空 → 构造 RunEngine 自动重发自愈
        var brokenRun = new RunState
        {
            RunId = "v6_broken_lvl",
            GameModeId = fx.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            State = ERunState.LevelUp,
        };
        brokenRun.PlayerAttributes[EPlayerAttributeType.Level] = 2;
        brokenRun.SelectionSet.Clear();
        _ = new RunEngine(brokenRun, fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);
        Assert.True(brokenRun.SelectionSet.Count >= 2, "空货架 LevelUp 恢复时应自动自愈填充选项");
    }

    // ── Bug #4：非商人 TActionGameDealCards 免费发 3 件只能领 1 件（CanSelectMultiple=false）──

    [Fact]
    public void Bug04_NonMerchantDealCards_EnforcesSinglePickAndExitsAfterOneItem()
    {
        const string freeDealStepId = "99999999-4444-4444-8444-444444444401";
        const string freeDealEncId = "99999999-4444-4444-8444-444444444402";
        string stepJson = $$"""
        {
          "$type": "TCardEncounterStep",
          "Id": "{{freeDealStepId}}",
          "Type": "EncounterStep",
          "InternalName": "V6 Free 3 Items Pick 1",
          "StartingTier": "Bronze",
          "Size": "Medium",
          "Heroes": ["Common"],
          "SpawningEligibility": "Always",
          "ShouldProgressHour": true,
          "Abilities": {
            "0": {
              "Id": "0",
              "Trigger": { "$type": "TTriggerOnCardSelected" },
              "ActiveIn": "HandOnly",
              "WorksIn": "Anywhere",
              "Action": {
                "$type": "TActionGameDealCards",
                "SpawnMode": "Replace",
                "SpawnContext": {
                  "$type": "TSpawnContextQuery",
                  "SelectionMethod": "Random",
                  "Limit": { "$type": "TFixedValue", "Value": 3.0 },
                  "Groups": [{
                    "$type": "TSpawnGroup",
                    "SelectionMethod": "Random",
                    "Filters": [{
                      "$type": "TSpawnFilterQuery",
                      "Constraints": { "$type": "ConstraintCardType", "Types": ["Item"], "IsNot": false }
                    }]
                  }]
                }
              }
            }
          }
        }
        """;
        string encJson = $$"""
        {
          "$type": "TCardEncounterEvent",
          "Id": "{{freeDealEncId}}",
          "Type": "EventEncounter",
          "InternalName": "V6 Free Pick Event",
          "StartingTier": "Bronze",
          "Size": "Medium",
          "Heroes": ["Common"],
          "SpawningEligibility": "Always",
          "SelectionContext": {
            "SpawnContext": {
              "$type": "TSpawnContextQuery",
              "SelectionMethod": "Sequential",
              "Groups": [{
                "$type": "TSpawnGroup",
                "SelectionMethod": "Sequential",
                "Filters": [{ "$type": "TSpawnFilterIdList", "Ids": ["{{freeDealStepId}}"] }]
              }]
            },
            "Rules": { "CanSelectMultiple": false, "SelectionIsFree": true, "CanExit": true }
          }
        }
        """;

        using var ov = new OverlayEnv((freeDealStepId, stepJson), (freeDealEncId, encJson));
        var run = new RunState { RunId = "v6_bug4", GameModeId = ov.Pool.DefaultGameModeId, Hero = EHero.Vanessa };
        var engine = new RunEngine(run, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.StartRun();

        var opt = InjectChoiceEncounter(run, ov.Pool, freeDealEncId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt.InstanceId) }).Error);
        var stepCard = run.Cards[Assert.Single(run.SelectionSet)];

        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(stepCard.InstanceId) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(3, run.SelectionSet.Count);
        Assert.False(run.SelectionRules.CanSelectMultiple);
        Assert.True(run.SelectionRules.SelectionIsFree);

        string pickedId = run.SelectionSet[0];
        string secondId = run.SelectionSet[1];
        var pickOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(pickedId),
            Section = EInventorySection.Hand,
        });
        Assert.Null(pickOut.Error);

        // 仅能拿到 1 件物品，并立刻退出 Encounter 回到 Choice（绝不能留在货架继续白嫖第 2、3 件）
        Assert.Equal(1, run.Cards.Values.Count(c => c.Owner == ECombatantId.Player && c.Type == ECardType.Item));
        Assert.NotEqual(ERunState.Encounter, run.State);
        Assert.NotNull(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(secondId), Section = EInventorySection.Hand }).Error);
    }

    // ── Bug #5：CombatSimConverter 冷却条从 CooldownMax 倒数至 0，开火帧发回 CooldownMax 触发 Reset 特效 ──

    [Fact]
    public void Bug05_CombatSimConverter_CooldownCountsDownAndEmitsResetFrame()
    {
        var fx = Fixture.Value;
        var cutlassTpl = fx.Repo.GetCard(CutlassItemId)!;

        var p1 = new PlayerState("player", "P1", "Vanessa", 500, 20, 10, 10);
        var p2 = new PlayerState("opp", "P2", "Vanessa", 500, 20, 10, 10);
        var p1Wep = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p1_cutlass") { StartSlot = 3 };
        var p2Wep = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p2_cutlass") { StartSlot = 3 };
        p1.Hand.TryAddAuto(p1Wep);
        p2.Hand.TryAddAuto(p2Wep);

        var res = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 42, combatType: "PVP", recordTimeline: true, recordEvents: true);
        var p1Entry = new RunState.CardEntry { InstanceId = "p1_cutlass", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Player, CoreCard = p1Wep };
        var p2Entry = new RunState.CardEntry { InstanceId = "p2_cutlass", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Opponent, CoreCard = p2Wep };
        p1Entry.Attributes[ECardAttributeType.CooldownMax] = (int)p1Wep.GetAttribute("CooldownMax");
        p2Entry.Attributes[ECardAttributeType.CooldownMax] = (int)p2Wep.GetAttribute("CooldownMax");

        var sim = CombatSimConverter.ToCombatSim(
            res,
            fx.Repo,
            new Dictionary<string, CardInstance> { ["p1_cutlass"] = p1Wep },
            new Dictionary<string, CardInstance> { ["p2_cutlass"] = p2Wep },
            new[] { p1Entry },
            new[] { p2Entry });

        var p1Id = new InstanceId("p1_cutlass");
        var cdUpdates = new List<(int FrameIdx, int Prev, int Cur, int CdMax, bool Fired)>();
        int curCdMax = p1Entry.Attributes[ECardAttributeType.CooldownMax];
        for (int i = 0; i < sim.Frames.Count; i++)
        {
            var f = sim.Frames[i];
            if (f.CardUpdates.TryGetValue(p1Id, out var cu))
            {
                if (cu.Attributes.TryGetValue(ECardAttributeType.CooldownMax, out var cm))
                    curCdMax = cm.CurrentValue;
                if (cu.Attributes.TryGetValue(ECardAttributeType.Cooldown, out var cd))
                {
                    bool fired = f.Events.OfType<CombatSimEventEffectExecuted>().Any(e => e.Source?.Value == "p1_cutlass");
                    cdUpdates.Add((i, cd.PreviousValue, cd.CurrentValue, curCdMax, fired));
                }
            }
        }

        Assert.True(cdUpdates.Count >= 10);
        // 第 0 帧后到首次开火前应严格递减（从 CooldownMax 倒数趋近 0）
        var preFire = cdUpdates.TakeWhile(u => !u.Fired).ToList();
        Assert.True(preFire.Count >= 5);
        Assert.True(preFire.Last().Cur < preFire.First().Cur, "Cooldown 应从 CooldownMax 向下倒数");

        // 开火重置帧必须发回 Cooldown == CooldownMax
        var firedFrames = cdUpdates.Where(u => u.Fired).ToList();
        Assert.NotEmpty(firedFrames);
        Assert.All(firedFrames, ff => Assert.Equal(ff.CdMax, ff.Cur));

        // 开火后下一帧继续向下倒数
        int firstFireListIdx = cdUpdates.FindIndex(u => u.Fired);
        Assert.True(firstFireListIdx + 1 < cdUpdates.Count);
        Assert.True(cdUpdates[firstFireListIdx + 1].Cur < cdUpdates[firstFireListIdx].Cur);
    }

    // ── Bug #6：Duct Tape 次级护盾与 Boarrior/Banannibal/Overwhelmed Student 护盾/血量同步 ──

    [Fact]
    public void Bug06_PassiveSecondaryShieldAndMonsters_EmitShieldEffectAndSyncHealth()
    {
        var fx = Fixture.Value;

        // 6a. Duct Tape（0966f887）紧邻 Cutlass（1b955b8d）：Cutlass 每次开火触发 Duct Tape 邻居护盾，应产出 PlayerShieldApply EffectExecuted
        var ductTpl = fx.Repo.GetCard(DuctTapeItemId)!;
        var cutlassTpl = fx.Repo.GetCard(CutlassItemId)!;
        var p1 = new PlayerState("player", "P1", "Vanessa", 400, 20, 10, 10);
        var p2 = new PlayerState("opp", "P2", "Vanessa", 400, 20, 10, 10);
        var p1Cutlass = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p1_cut") { StartSlot = 2 };
        var p1Duct = new CardInstance(ductTpl, CardTier.Silver, null, "p1_duct") { StartSlot = 4 };
        var p2Cutlass = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p2_cut") { StartSlot = 3 };
        p1.Hand.TryAddAuto(p1Cutlass);
        p1.Hand.TryAddAuto(p1Duct);
        p2.Hand.TryAddAuto(p2Cutlass);

        var dtRes = CombatSimulator.Simulate(fx.Repo, p1, p2, seed: 7, combatType: "PVP", recordTimeline: true, recordEvents: true);
        var p1Entries = new[]
        {
            new RunState.CardEntry { InstanceId = "p1_cut", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Player, CoreCard = p1Cutlass },
            new RunState.CardEntry { InstanceId = "p1_duct", TemplateId = Guid.Parse(DuctTapeItemId), Type = ECardType.Item, Owner = ECombatantId.Player, CoreCard = p1Duct },
        };
        var p2Entries = new[]
        {
            new RunState.CardEntry { InstanceId = "p2_cut", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Opponent, CoreCard = p2Cutlass },
        };
        var dtSim = CombatSimConverter.ToCombatSim(
            dtRes, fx.Repo,
            new Dictionary<string, CardInstance> { ["p1_cut"] = p1Cutlass, ["p1_duct"] = p1Duct },
            new Dictionary<string, CardInstance> { ["p2_cut"] = p2Cutlass },
            p1Entries, p2Entries);

        var ductShieldExecs = dtSim.Frames
            .SelectMany(f => f.Events.OfType<CombatSimEventEffectExecuted>())
            .Where(e => e.Source?.Value == "p1_duct" && e.ActionType == EActionCommandType.PlayerShieldApply)
            .ToList();
        Assert.NotEmpty(ductShieldExecs);

        // 校验逐帧 Health 一致性：Player 与 Opponent 每次 HealthAdjustments 都不得把 Shield 吸收误差混入 Health
        VerifyFrameHealthConsistency(dtSim, p1.HealthMax, p2.HealthMax);

        // 6b. 官方带盾野怪 Boarrior / Banannibal / Overwhelmed Student
        foreach (var (mId, expectShieldExec) in new[]
        {
            (BoarriorMonsterId, true),
            (OverwhelmedStudentMonsterId, true),
            (BanannibalMonsterId, false),
        })
        {
            Assert.True(fx.Repo.MonstersById.TryGetValue(mId, out var coreMob), $"Monster missing: {mId}");
            var mp1 = new PlayerState("player", "P1", "Vanessa", 350, 20, 10, 10);
            mp1.Hand.TryAddAuto(new CardInstance(cutlassTpl, CardTier.Bronze, null, "mp1_cut") { StartSlot = 3 });
            var mp2 = CombatSimulator.BuildMonsterPlayerState(fx.Repo, coreMob);

            var mRes = CombatSimulator.Simulate(fx.Repo, mp1, mp2, seed: 11, combatType: "PVE", recordTimeline: true, recordEvents: true);
            var mP1Entries = mp1.Hand.Cards.Select(c => new RunState.CardEntry { InstanceId = c.InstanceId, TemplateId = Guid.Parse(c.Template.Id), Type = ECardType.Item, Owner = ECombatantId.Player, CoreCard = c }).ToList();
            var mP2Entries = mp2.Hand.Cards.Select(c => new RunState.CardEntry { InstanceId = c.InstanceId, TemplateId = Guid.Parse(c.Template.Id), Type = ECardType.Item, Owner = ECombatantId.Opponent, CoreCard = c }).ToList();
            var mSim = CombatSimConverter.ToCombatSim(
                mRes, fx.Repo,
                mp1.Hand.Cards.ToDictionary(c => c.InstanceId),
                mp2.Hand.Cards.ToDictionary(c => c.InstanceId),
                mP1Entries, mP2Entries);

            if (expectShieldExec)
            {
                Assert.Contains(mSim.Frames.SelectMany(f => f.Events.OfType<CombatSimEventEffectExecuted>()),
                    e => e.ActionType == EActionCommandType.PlayerShieldApply);
            }
            VerifyFrameHealthConsistency(mSim, mp1.HealthMax, mp2.HealthMax);
        }
    }

    private static void VerifyFrameHealthConsistency(CombatSim sim, int initP1Hp, int initP2Hp)
    {
        int p1Hp = initP1Hp;
        int p2Hp = initP2Hp;
        foreach (var frame in sim.Frames)
        {
            ApplyUpdate(frame.PlayerUpdates, ref p1Hp);
            ApplyUpdate(frame.OpponentUpdates, ref p2Hp);
            Assert.True(p1Hp >= 0 && p2Hp >= 0);
        }

        static void ApplyUpdate(CombatSimPlayerUpdate? upd, ref int hp)
        {
            if (upd == null) return;
            foreach (var adj in upd.HealthAdjustments)
            {
                if (adj.AttributeChanged == EPlayerHealthChangeType.Health)
                    hp += adj.Amount;
            }
            if (upd.Attributes.TryGetValue(EPlayerAttributeType.Health, out var attr))
            {
                hp = attr.CurrentValue;
            }
        }
    }

    // ── Bug #7：Day 1 Hour 6 PvP 产出有效的 Opponent PlayerInitialized 与 CardSpawned ──

    [Fact]
    public void Bug07_Day1Hour6Pvp_ProducesValidOpponentPlayerInitializedAndSpawnedCards()
    {
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);
        engine.Handle(new CheatCommand { Args = ["hour", "6"] });
        engine.Handle(new CheatCommand { Args = ["deal"] });
        Assert.Equal(ERunState.Choice, run.State);
        var pvpCard = run.Cards[Assert.Single(run.SelectionSet)];
        Assert.Equal(ECardType.PvpEncounter, pvpCard.Type);

        var pvpOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpCard.InstanceId) });
        Assert.Null(pvpOut.Error);
        Assert.NotNull(pvpOut.CombatSpawnSim);
        Assert.NotNull(pvpOut.Combat);

        var oppInit = pvpOut.CombatSpawnSim!.Events.OfType<GameSimEventPlayerInitialized>()
            .FirstOrDefault(e => e.CombatantId == ECombatantId.Opponent);
        Assert.NotNull(oppInit);
        var oppInfo = pvpOut.CombatSpawnSim.CurrentState?.PvpOpponent;
        Assert.NotNull(oppInfo);
        Assert.False(string.IsNullOrWhiteSpace(oppInfo!.PlayerLoadout?.accountId));

        var oppCards = pvpOut.CombatSpawnSim.Events.OfType<GameSimEventCardSpawned>()
            .Where(e => pvpOut.CombatSpawnSim.Cards.TryGetValue(e.InstanceId, out var c) && c.Placement?.Owner == ECombatantId.Opponent)
            .ToList();
        Assert.NotEmpty(oppCards);
    }

    // ── Bug #8：BoardGrid / TargetResolver 变宽邻接/空隙/Stash/销毁隔离 + MoveOwnedCard 实时刷新光环 ──

    [Fact]
    public void Bug08_PositionalKeywordsAndMoveOwnedCardAuraSync()
    {
        var fx = Fixture.Value;
        var smallTpl = new CardTemplate { Id = "pos_s", InternalName = "PosSmall", Type = "Item", Size = "Small", StartingTier = "Bronze" };
        var medTpl = new CardTemplate { Id = "pos_m", InternalName = "PosMed", Type = "Item", Size = "Medium", StartingTier = "Bronze" };
        var largeTpl = new CardTemplate { Id = "pos_l", InternalName = "PosLarge", Type = "Item", Size = "Large", StartingTier = "Bronze" };

        var player = new PlayerState("p", "P", "Vanessa", 250, 20, 10, 10);
        player.Hand.UnlockedMask = 0x3FF;
        player.Stash.UnlockedMask = 0x3FF;

        // 布局：Hand 中 [0]:Small, [1..2]:Medium, [3]:空隙!, [4..6]:Large, [7]:Small(Destroyed), [8]:Small
        var s0 = new CardInstance(smallTpl, CardTier.Bronze, null, "s0");
        var m1 = new CardInstance(medTpl, CardTier.Bronze, null, "m1");
        var l4 = new CardInstance(largeTpl, CardTier.Bronze, null, "l4");
        var d7 = new CardInstance(smallTpl, CardTier.Bronze, null, "d7") { IsDestroyed = true };
        var s8 = new CardInstance(smallTpl, CardTier.Bronze, null, "s8");
        var stashItem = new CardInstance(smallTpl, CardTier.Bronze, null, "st0");
        var unplaced = new CardInstance(smallTpl, CardTier.Bronze, null, "unp") { StartSlot = -1 };

        Assert.True(player.Hand.TryPlaceOrShift(s0, 0));
        Assert.True(player.Hand.TryPlaceOrShift(m1, 1));
        Assert.True(player.Hand.TryPlaceOrShift(l4, 4));
        Assert.True(player.Hand.TryPlaceOrShift(d7, 7));
        Assert.True(player.Hand.TryPlaceOrShift(s8, 8));
        Assert.True(player.Stash.TryPlaceOrShift(stashItem, 0));

        // 严格邻接：m1(1..2) 左邻是 s0(0)，右邻是空槽(3) 故 RightNeighbor 为 null（绝不跨空槽跳到 l4）
        Assert.Same(s0, player.Hand.GetLeftNeighbor(m1));
        Assert.Null(player.Hand.GetRightNeighbor(m1));
        Assert.Null(player.Hand.GetLeftNeighbor(l4));
        Assert.Null(player.Hand.GetRightNeighbor(l4)); // d7 已销毁故跳过/不可见
        Assert.Empty(player.Hand.GetAllRightCards(unplaced));
        Assert.Empty(player.Hand.GetAllLeftCards(unplaced));

        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, player, null, new Random(1)) { SelfCard = l4 };
        Assert.Equal(new[] { "s0", "m1" }, TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardPositional", Origin = "Self", TargetMode = "AllLeftCards" }, ctx).Select(c => c.InstanceId));
        Assert.Equal(new[] { "s8" }, TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardPositional", Origin = "Self", TargetMode = "AllRightCards" }, ctx).Select(c => c.InstanceId));
        Assert.Equal(new[] { "s0" }, TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardXMost", TargetMode = "LeftMostCard", TargetSection = "SelfHand" }, ctx).Select(c => c.InstanceId));
        Assert.Equal(new[] { "s8" }, TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardXMost", TargetMode = "RightMostCard", TargetSection = "SelfHand" }, ctx).Select(c => c.InstanceId));

        // Stash 卡片独立解析，绝不混入手牌邻接
        var stashCtx = ctx.WithCards(stashItem);
        Assert.Empty(TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardPositional", Origin = "Self", TargetMode = "Neighbor" }, stashCtx));
        Assert.Single(TargetResolver.ResolveCards(new RuleNode { Type = "TTargetCardSection", TargetSection = "SelfStash" }, stashCtx));

        // RunEngine.MoveOwnedCard：Swash Buckle 移动到 Cutlass 相邻槽位即时更新 DamageAmount 光环
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);
        engine.Handle(new CheatCommand { Args = ["slots"] });
        var cutlass = AddOwnedHandItem(run, fx.Repo, fx.Pool, CutlassItemId, slot: 0, ETier.Bronze);
        var swash = AddOwnedHandItem(run, fx.Repo, fx.Pool, SwashBuckleItemId, slot: 6, ETier.Gold);
        Assert.Equal(10, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));

        var moveNear = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(swash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_2 },
        });
        Assert.Null(moveNear.Error);
        Assert.True(cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0) > 10);

        var moveAway = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(swash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_6 },
        });
        Assert.Null(moveAway.Error);
        Assert.Equal(10, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
    }

    // ── Bug #9 & #17：放弃清理 / RunRewardLevels 非空 / 锦标赛全链路（HTTP 真实自托管） ──

    [Fact]
    public async Task Bug09_17_AbandonCleanup_RunRewardLevels_AndTournamentLifecycle()
    {
        var fx = Fixture.Value;
        string stateDir = Path.Combine(Path.GetTempPath(), $"bazaar_http_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        string dbPath = Path.Combine(stateDir, "bazaar.db");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var sessions = new SessionStore();
        var lobbies = new LobbyStore();

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(fx.Pool);
        builder.Services.AddSingleton(fx.Monsters);
        builder.Services.AddSingleton(fx.LevelUps);
        builder.Services.AddSingleton(fx.Mode);
        builder.Services.AddSingleton(fx.Ghosts);
        builder.Services.AddSingleton(fx.Repo);

        await using var app = builder.Build();
        TempoNetEndpoints.Map(app);
        GameServerEndpoints.Map(app);
        TournamentEndpoints.Map(app, lobbies);

        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        try
        {
            // 登录测试账号
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "v6tester" });
            loginResp.EnsureSuccessStatusCode();
            using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
            string token = loginDoc.RootElement.GetProperty("AccessToken").GetString()!;
            http.DefaultRequestHeaders.Remove("Authorization");
            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

            // 1. Bug #9：/api/RunRewardLevels 与 /api/Bootstrap/app 的 runRewardLevels 非空
            using var rrlDoc = JsonDocument.Parse(await http.GetStringAsync("/api/RunRewardLevels"));
            Assert.Equal(JsonValueKind.Array, rrlDoc.RootElement.ValueKind);
            Assert.Equal(3, rrlDoc.RootElement.GetArrayLength());

            using var bootDoc = JsonDocument.Parse(await http.GetStringAsync("/api/Bootstrap/app"));
            Assert.True(bootDoc.RootElement.TryGetProperty("runRewardLevels", out var bootRrl));
            Assert.Equal(JsonValueKind.Array, bootRrl.ValueKind);
            Assert.Equal(3, bootRrl.GetArrayLength());

            // 2. Bug #17：单人匹配队列 PUT /queue -> GET /queue -> GET /membership -> /sessions/current/status
            var putQueueResp = await http.PutAsync("/queue", new StringContent("{}", Encoding.UTF8, "application/json"));
            putQueueResp.EnsureSuccessStatusCode();
            using var qDoc = JsonDocument.Parse(await http.GetStringAsync("/queue"));
            Assert.Equal("lobby", qDoc.RootElement.GetProperty("State").GetString());
            Assert.Equal(8, qDoc.RootElement.GetProperty("QueuedCount").GetInt32());

            using var memDoc = JsonDocument.Parse(await http.GetStringAsync("/membership"));
            Assert.Equal("session", memDoc.RootElement.GetProperty("State").GetString());
            string? tSessionIdStr = memDoc.RootElement.GetProperty("SessionId").GetString();
            Assert.True(Guid.TryParse(tSessionIdStr, out var tSessionId));

            // 通过 /sessions 建立正式锦标赛 Run
            var initCmd = new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
                TournamentSessionId = tSessionId,
            };
            var initReq = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(initCmd, Codec.Options));
            initReq.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var sessResp = await http.PostAsync("/sessions", initReq);
            sessResp.EnsureSuccessStatusCode();
            string sid = sessResp.Headers.GetValues("sid").First();

            // Hour 1..5 -> DayFlow, CanStartPvp=false, TotalPlayers=8, AssignedOpponent* 非空
            using (var stDoc = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("DayFlow", stDoc.RootElement.GetProperty("Phase").GetString());
                Assert.Equal(8, stDoc.RootElement.GetProperty("TotalPlayers").GetInt32());
                Assert.False(stDoc.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.False(string.IsNullOrWhiteSpace(stDoc.RootElement.GetProperty("AssignedOpponentAccountId").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(stDoc.RootElement.GetProperty("AssignedOpponentUsername").GetString()));
            }

            // 切至 Hour 6（PvP Ready）→ Phase="PvpReady", CanStartPvp=true
            var handle = SessionRegistry.Get(sid)!;
            handle.Engine.Handle(new CheatCommand { Args = ["hour", "6"] });
            handle.Engine.Handle(new CheatCommand { Args = ["deal"] });
            using (var pvpStDoc = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("PvpReady", pvpStDoc.RootElement.GetProperty("Phase").GetString());
                Assert.True(pvpStDoc.RootElement.GetProperty("CanStartPvp").GetBoolean());
                Assert.True(pvpStDoc.RootElement.GetProperty("PlayerReadyForPvp").GetBoolean());
            }

            // 3. Bug #9：暂停（DELETE /sessions）保留 ActiveRun 以便断点续玩；放弃（AbandonRunCommand / deleteactivesave）彻底清除 ActiveRun
            var delSessMsg = new HttpRequestMessage(HttpMethod.Delete, "/sessions");
            delSessMsg.Headers.Add("sid", sid);
            (await http.SendAsync(delSessMsg)).EnsureSuccessStatusCode();
            using (var profAfterPause = JsonDocument.Parse(await http.GetStringAsync("/api/PlayerProfiles/me")))
            {
                Assert.NotEqual(JsonValueKind.Null, profAfterPause.RootElement.GetProperty("ActiveRun").ValueKind);
            }

            // 重新建房 + 倒计时（覆盖 POST /lobbies -> PUT /countdown 8 人补齐）并建立新局后验证 AbandonRunCommand 与 deleteactivesave
            var createLobbyResp = await http.PostAsJsonAsync("/lobbies", new { name = "SoloTest", playMode = "Tournament", playerCap = 8, isPublic = false });
            createLobbyResp.EnsureSuccessStatusCode();
            using var lobbyDoc = JsonDocument.Parse(await createLobbyResp.Content.ReadAsStringAsync());
            string lobbyId = lobbyDoc.RootElement.GetProperty("LobbyId").GetString()!;
            Assert.True(lobbyDoc.RootElement.GetProperty("CanStart").GetBoolean());

            var cdResp = await http.PutAsync($"/lobbies/{lobbyId}/countdown", new StringContent("{}", Encoding.UTF8, "application/json"));
            cdResp.EnsureSuccessStatusCode();
            using var lobbyAfterCd = JsonDocument.Parse(await http.GetStringAsync($"/lobbies/{lobbyId}"));
            Assert.Equal("started", lobbyAfterCd.RootElement.GetProperty("State").GetString());
            Assert.Equal(8, lobbyAfterCd.RootElement.GetProperty("Members").GetArrayLength());
            Guid soloTournamentSessionId = Guid.Parse(lobbyAfterCd.RootElement.GetProperty("SessionId").GetString()!);

            var initCmd2 = new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
                TournamentSessionId = soloTournamentSessionId,
            };
            var initReq2 = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(initCmd2, Codec.Options));
            initReq2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var sessResp2 = await http.PostAsync("/sessions", initReq2);
            sessResp2.EnsureSuccessStatusCode();
            string sid2 = sessResp2.Headers.GetValues("sid").First();

            // 发送 AbandonRunCommand 后，/sessions/current/status 应立即反映 RunEnded，且 Profile.ActiveRun 必须为 null
            var abandonReq = new HttpRequestMessage(HttpMethod.Post, "/commands")
            {
                Content = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(new AbandonRunCommand(), Codec.Options))
            };
            abandonReq.Headers.Add("sid", sid2);
            abandonReq.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            (await http.SendAsync(abandonReq)).EnsureSuccessStatusCode();

            using (var endStDoc = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("RunEnded", endStDoc.RootElement.GetProperty("Phase").GetString());
                Assert.True(endStDoc.RootElement.GetProperty("EndRun").GetBoolean());
                Assert.True(endStDoc.RootElement.GetProperty("IsEliminated").GetBoolean());
            }
            using (var profAfterAbandon = JsonDocument.Parse(await http.GetStringAsync("/api/PlayerProfiles/me")))
            {
                Assert.Equal(JsonValueKind.Null, profAfterAbandon.RootElement.GetProperty("ActiveRun").ValueKind);
            }

            // 验证 DELETE /api/game/deleteactivesave 清理所有残余存档
            var delSaveResp = await http.DeleteAsync("/api/game/deleteactivesave");
            delSaveResp.EnsureSuccessStatusCode();
            using var postRunDoc = JsonDocument.Parse(await http.GetStringAsync("/api/Bootstrap/post-run"));
            Assert.Equal(JsonValueKind.Null, postRunDoc.RootElement.GetProperty("profile").GetProperty("ActiveRun").ValueKind);
        }
        finally
        {
            await app.StopAsync();
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Bug17_PriorNonTournamentAndOldTournamentDefeats_DoNotEliminateNewTournamentLobby()
    {
        var fx = Fixture.Value;
        string stateDir = Path.Combine(Path.GetTempPath(), $"bazaar_tourney_iso_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDir);
        string dbPath = Path.Combine(stateDir, "bazaar.db");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var sessions = new SessionStore();
        var lobbies = new LobbyStore();

        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(fx.Pool);
        builder.Services.AddSingleton(fx.Monsters);
        builder.Services.AddSingleton(fx.LevelUps);
        builder.Services.AddSingleton(fx.Mode);
        builder.Services.AddSingleton(fx.Ghosts);
        builder.Services.AddSingleton(fx.Repo);

        await using var app = builder.Build();
        TempoNetEndpoints.Map(app);
        GameServerEndpoints.Map(app);
        TournamentEndpoints.Map(app, lobbies);

        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>().Features
            .Get<IServerAddressesFeature>()!.Addresses.First();

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        try
        {
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "v6tourney_iso" });
            loginResp.EnsureSuccessStatusCode();
            using var loginDoc = JsonDocument.Parse(await loginResp.Content.ReadAsStringAsync());
            string token = loginDoc.RootElement.GetProperty("AccessToken").GetString()!;
            http.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);

            // 1. 先打一局普通模式并投降，使 SessionStore 中残留该账号的 EndRunDefeat（非锦标赛）会话
            var unrankedInit = new InitializeRunCommand
            {
                PlayMode = EPlayMode.Unranked,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
            };
            var unrankedReq = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(unrankedInit, Codec.Options));
            unrankedReq.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var unrankedSess = await http.PostAsync("/sessions", unrankedReq);
            unrankedSess.EnsureSuccessStatusCode();
            string unrankedSid = unrankedSess.Headers.GetValues("sid").First();

            var abandonReq1 = new HttpRequestMessage(HttpMethod.Post, "/commands")
            {
                Content = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(new AbandonRunCommand(), Codec.Options))
            };
            abandonReq1.Headers.Add("sid", unrankedSid);
            abandonReq1.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            (await http.SendAsync(abandonReq1)).EnsureSuccessStatusCode();

            // 2. 创建自定义锦标赛房间并开赛：在建立新锦标赛 /sessions 之前查询 /sessions/current/status 与 /membership，不得被旧普通局误判淘汰
            var createLobbyResp = await http.PostAsJsonAsync("/lobbies", new { name = "IsoLobby1", playMode = "Tournament", playerCap = 8, isPublic = false });
            createLobbyResp.EnsureSuccessStatusCode();
            using var lobbyDoc1 = JsonDocument.Parse(await createLobbyResp.Content.ReadAsStringAsync());
            string lobbyId1 = lobbyDoc1.RootElement.GetProperty("LobbyId").GetString()!;

            // 等待房态阶段必须为 WaitingRoom 且不继承淘汰
            using (var waitDoc = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("WaitingRoom", waitDoc.RootElement.GetProperty("Phase").GetString());
                Assert.False(waitDoc.RootElement.GetProperty("IsEliminated").GetBoolean());
                Assert.False(waitDoc.RootElement.GetProperty("EndRun").GetBoolean());
            }

            var cdResp1 = await http.PutAsync($"/lobbies/{lobbyId1}/countdown", new StringContent("{}", Encoding.UTF8, "application/json"));
            cdResp1.EnsureSuccessStatusCode();
            using var cdDoc1 = JsonDocument.Parse(await cdResp1.Content.ReadAsStringAsync());
            Guid tid1 = Guid.Parse(cdDoc1.RootElement.GetProperty("tournamentSessionId").GetString()!);

            using (var startSt = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("RunStarting", startSt.RootElement.GetProperty("Phase").GetString());
                Assert.False(startSt.RootElement.GetProperty("IsEliminated").GetBoolean());
                Assert.False(startSt.RootElement.GetProperty("EndRun").GetBoolean());
            }
            using (var memDoc1 = JsonDocument.Parse(await http.GetStringAsync("/membership")))
            {
                Assert.Equal("session", memDoc1.RootElement.GetProperty("State").GetString());
            }

            // 3. 在 Lobby1 中开局并投降（此时该锦标赛局确实为 EndRunDefeat）
            var tInit1 = new InitializeRunCommand
            {
                PlayMode = EPlayMode.Tournament,
                SelectedHero = EHero.Vanessa,
                GameModeId = fx.Pool.DefaultGameModeId,
                TournamentSessionId = tid1,
            };
            var tReq1 = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(tInit1, Codec.Options));
            tReq1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            var tSess1 = await http.PostAsync("/sessions", tReq1);
            tSess1.EnsureSuccessStatusCode();
            string tSid1 = tSess1.Headers.GetValues("sid").First();

            var abandonReq2 = new HttpRequestMessage(HttpMethod.Post, "/commands")
            {
                Content = new ByteArrayContent(MessagePackSerializer.Serialize<INetCommand>(new AbandonRunCommand(), Codec.Options))
            };
            abandonReq2.Headers.Add("sid", tSid1);
            abandonReq2.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            (await http.SendAsync(abandonReq2)).EnsureSuccessStatusCode();

            using (var endedSt1 = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("RunEnded", endedSt1.RootElement.GetProperty("Phase").GetString());
                Assert.True(endedSt1.RootElement.GetProperty("IsEliminated").GetBoolean());
            }

            // 4. 退房后再通过 PUT /queue 秒开新一局锦标赛 T2，不得继承 T1 的淘汰态
            (await http.DeleteAsync("/membership")).EnsureSuccessStatusCode();
            var qResp = await http.PutAsync("/queue", new StringContent("{}", Encoding.UTF8, "application/json"));
            qResp.EnsureSuccessStatusCode();

            using (var startSt2 = JsonDocument.Parse(await http.GetStringAsync("/sessions/current/status")))
            {
                Assert.Equal("RunStarting", startSt2.RootElement.GetProperty("Phase").GetString());
                Assert.False(startSt2.RootElement.GetProperty("IsEliminated").GetBoolean());
                Assert.False(startSt2.RootElement.GetProperty("EndRun").GetBoolean());
            }
            using (var memDoc2 = JsonDocument.Parse(await http.GetStringAsync("/membership")))
            {
                Assert.Equal("session", memDoc2.RootElement.GetProperty("State").GetString());
            }
        }
        finally
        {
            await app.StopAsync();
            try { Directory.Delete(stateDir, recursive: true); } catch { }
        }
    }

    // ── Bug #10：Swole (c08396cd) / Never / 缺 zh-CN 翻译卡从 CardPool / SpawnQueryEngine / LevelUpTable 全量剔除 ──

    [Fact]
    public void Bug10_SwoleNeverAndMissingZhCn_ExcludedFromCardPoolSpawnQueryAndLevelUp()
    {
        var fx = Fixture.Value;
        Assert.True(fx.Repo.HasLocalizationLoaded);
        Assert.False(fx.Pool.IsPlayerSpawnableId(SwoleSkillId, allowGuidOnly: true));
        Assert.DoesNotContain(fx.Pool.Skills("Pygmalien"), c => c.Id == SwoleSkillId);

        // 所有可通过 CardPool 玩家生成池出现的卡，均不可是 SpawningEligibility=Never，且必须具备 zh-CN 翻译
        foreach (var hero in new[] { "Vanessa", "Pygmalien", "Dooley", "Mak", "Stelle", "Jules" })
        {
            foreach (var c in fx.Pool.ShopItems(hero).Concat(fx.Pool.Skills(hero)))
            {
                Assert.False(string.Equals(c.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase),
                    $"Card {c.Id} ({c.InternalName}) has SpawningEligibility=Never");
                var core = fx.Repo.GetCard(c.Id);
                Assert.True(fx.Repo.HasZhTranslation(core), $"Card {c.Id} missing zh-CN translation");
            }
        }

        // SpawnQueryEngine 广域查询绝不返回 Swole / Never / 无 zh-CN 卡
        var queryPlayer = new PlayerState("p", "P", "Pygmalien", 250, 20, 20, 10) { Day = 5, Hour = 1, Level = 5 };
        var broadSkillQuery = JsonSerializer.Deserialize<RuleNode>(
            """
            {
              "$type": "TSpawnContextQuery",
              "SelectionMethod": "Random",
              "Limit": { "$type": "TFixedValue", "Value": 25.0 },
              "Groups": [{
                "$type": "TSpawnGroup",
                "SelectionMethod": "Random",
                "Filters": [{
                  "$type": "TSpawnFilterQuery",
                  "Constraints": { "$type": "ConstraintCardType", "Types": ["Skill"], "IsNot": false }
                }]
              }]
            }
            """,
            GameDatabaseLoader.JsonOptions)!;
        var ctx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, new Random(123)) { CurrentDay = 5, CurrentHour = 1 };
        var spawnedSkills = SpawnQueryEngine.SpawnCards(broadSkillQuery, ctx);
        Assert.DoesNotContain(spawnedSkills, s => s.Template.Id == SwoleSkillId);
        Assert.All(spawnedSkills, s =>
        {
            Assert.False(string.Equals(s.Template.SpawningEligibility, "Never", StringComparison.OrdinalIgnoreCase));
            Assert.True(fx.Repo.HasZhTranslation(s.Template));
        });

        // LevelUpTable 全等级抽取绝不出现 Swole / Never / 缺 zh-CN
        for (int lvl = 2; lvl <= 10; lvl++)
        {
            var lvCtx = new RuleExecutionContext(fx.Repo, RuleScope.OutOfCombat, queryPlayer, queryPlayer, new Random(lvl * 31)) { CurrentDay = lvl, CurrentHour = 1 };
            var picks = fx.LevelUps.RollRewards(lvl, lvCtx);
            Assert.DoesNotContain(picks, id => id == SwoleSkillId);
        }
    }

    // ── Bug #11：Salvage Yard (a79a4688) 卖非 Loot 卡每次只生 1 张 Loot，卖 Loot 卡生 0 张 ──

    [Fact]
    public void Bug11_SalvageYard_SellsSpawnExactlyOneLootPerNonLootSaleAndZeroForLoot()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);
        engine.Handle(new CheatCommand { Args = ["slots"] });

        var drone1 = AddOwnedHandItem(run, fx.Repo, fx.Pool, RocketDroneItemId, slot: 0, ETier.Bronze);
        var drone2 = AddOwnedHandItem(run, fx.Repo, fx.Pool, RocketDroneItemId, slot: 1, ETier.Bronze);
        var lootItem = AddOwnedHandItem(run, fx.Repo, fx.Pool, EagleTalismanLootId, slot: 2, ETier.Bronze);

        var syOpt = InjectChoiceEncounter(run, fx.Pool, SalvageYardEncounterId);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(syOpt.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.NotNull(run.CurrentEncounterCoreCard);

        // 第 1 次卖非 Loot（Rocket Drone）：精确只生成 1 张 Loot（绝不是 4 张！）
        var sell1 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(drone1.InstanceId) });
        Assert.Null(sell1.Error);
        var spawned1 = sell1.Events.OfType<GameSimEventCardSpawned>().ToList();
        Assert.Single(spawned1);
        Assert.Contains(ECardTag.Loot, run.Cards[spawned1[0].InstanceId].Tags);

        // 第 2 次卖非 Loot（Rocket Drone）：重置 Custom_0 后再次精确只生成 1 张 Loot
        var sell2 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(drone2.InstanceId) });
        Assert.Null(sell2.Error);
        var spawned2 = sell2.Events.OfType<GameSimEventCardSpawned>().ToList();
        Assert.Single(spawned2);
        Assert.Contains(ECardTag.Loot, run.Cards[spawned2[0].InstanceId].Tags);

        // 第 3 次卖 Loot 卡（Eagle Talisman）：绝不产新 Loot（0 张）
        var sell3 = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(lootItem.InstanceId) });
        Assert.Null(sell3.Error);
        Assert.Empty(sell3.Events.OfType<GameSimEventCardSpawned>());
    }

    // ── Bug #13：非融合 Skill 选择保留原 InstanceId，不销毁，发射 CardPurchased + CardMoved + PlayerSkillEquipped ──

    [Fact]
    public void Bug13_EquipCandidateSkill_NonFusionPreservesInstanceIdAndEmitsEquipEvents()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);

        // 直接给玩家进入一个包含技能货架的 LevelUp 态
        var shop = new ShopEngine(run, fx.Pool, fx.Repo);
        shop.DealStarterSkills(count: 3);
        Assert.Equal(ERunState.Encounter, run.State);
        string chosenSkillId = run.SelectionSet.First();

        var pickOut = engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(chosenSkillId) });
        Assert.Null(pickOut.Error);

        Assert.True(run.Cards.TryGetValue(chosenSkillId, out var ownedSkill));
        Assert.Equal(ECombatantId.Player, ownedSkill.Owner);
        Assert.DoesNotContain(pickOut.Events.OfType<GameSimEventCardDisposed>(), e => e.InstanceId == chosenSkillId);
        Assert.Contains(pickOut.Events.OfType<GameSimEventCardPurchased>(), e => e.InstanceId == chosenSkillId);
        Assert.Contains(pickOut.Events.OfType<GameSimEventCardMoved>(), e => e.InstanceId == chosenSkillId);
        Assert.Contains(pickOut.Events.OfType<GameSimEventPlayerSkillEquipped>(), e => e.InstanceId == chosenSkillId);
    }

    // ── Bug #14 保留项：野怪 PvE 胜利进入 Loot 并保持 CanExit=true 直到显式 ExitCurrentStateCommand ──

    [Fact]
    public void Bug14_MonsterVictory_StaysInLootUntilExplicitExitCurrentStateCommand()
    {
        var fx = Fixture.Value;
        var (run, engine) = CreateStartedEngine(EHero.Vanessa);
        engine.Handle(new CheatCommand { Args = ["slots"] });
        AddOwnedHandItem(run, fx.Repo, fx.Pool, CutlassItemId, slot: 3, ETier.Gold);

        var mobOpt = InjectChoiceEncounter(run, fx.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });
        Assert.Null(combatOut.Error);
        Assert.Equal(ECombatantId.Player, combatOut.Combat!.Winner);
        Assert.Equal(ERunState.Loot, run.State);
        Assert.True(run.SelectionRules.CanExit);
        Assert.NotEmpty(run.SelectionSet);

        // 必须由玩家显式发送 ExitCurrentStateCommand 才退出 Loot
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        Assert.NotEqual(ERunState.Loot, run.State);
    }

    // ── Bug #15：Jungle Ruins 英雄限制过滤 —— Vanessa 绝不见 Mak 专属步骤 9a2dc8e3，Mak 可见 ──

    [Fact]
    public void Bug15_JungleRuins_FiltersMakOnlyStepForVanessa_AndAllowsForMak()
    {
        var fx = Fixture.Value;

        // Vanessa 进入 Jungle Ruins 20 次均不得出现 Mak 专属步骤 9a2dc8e3
        for (int i = 0; i < 20; i++)
        {
            var (vRun, vEngine) = CreateStartedEngine(EHero.Vanessa);
            var jrOpt = InjectChoiceEncounter(vRun, fx.Pool, JungleRuinsEncounterId);
            var enterOut = vEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(jrOpt.InstanceId) });
            Assert.Null(enterOut.Error);
            Assert.Equal(ERunState.Encounter, vRun.State);
            Assert.NotEmpty(vRun.SelectionSet);
            Assert.DoesNotContain(vRun.SelectionSet, id =>
                vRun.Cards[id].TemplateId.ToString().Equals(HuntForReagentsStepId, StringComparison.OrdinalIgnoreCase));
        }

        // Mak 进入 Jungle Ruins 时可见 Hunt for Reagents (9a2dc8e3)
        bool makSawReagents = false;
        for (int i = 0; i < 10 && !makSawReagents; i++)
        {
            var (mRun, mEngine) = CreateStartedEngine(EHero.Mak);
            var jrOpt = InjectChoiceEncounter(mRun, fx.Pool, JungleRuinsEncounterId);
            var enterOut = mEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(jrOpt.InstanceId) });
            Assert.Null(enterOut.Error);
            makSawReagents = mRun.SelectionSet.Any(id =>
                mRun.Cards[id].TemplateId.ToString().Equals(HuntForReagentsStepId, StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(makSawReagents, "Mak 进入 Jungle Ruins 应能看到专属步骤 9a2dc8e3 (Hunt for Reagents)");
    }

    // ── Bug #16：超 2 分钟沙尘暴战斗的 CombatSimConverter 帧流不断流验证（>2400 帧） ──

    [Fact]
    public void Bug16_CombatSimConverter_PreservesFramesBeyond120Seconds()
    {
        var fx = Fixture.Value;
        var cutlassTpl = fx.Repo.GetCard(CutlassItemId)!;
        var p1 = new PlayerState("player", "P1", "Vanessa", 500, 20, 10, 10);
        var p2 = new PlayerState("opp", "P2", "Vanessa", 500, 20, 10, 10);
        var p1Wep = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p1_cutlass") { StartSlot = 3 };
        var p2Wep = new CardInstance(cutlassTpl, CardTier.Bronze, null, "p2_cutlass") { StartSlot = 3 };

        var p1Entry = new RunState.CardEntry { InstanceId = "p1_cutlass", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Player, CoreCard = p1Wep };
        var p2Entry = new RunState.CardEntry { InstanceId = "p2_cutlass", TemplateId = Guid.Parse(CutlassItemId), Type = ECardType.Item, Owner = ECombatantId.Opponent, CoreCard = p2Wep };

        var syntheticLongResult = new CombatResult
        {
            WinnerPlayerId = "player",
            LoserPlayerId = "opp",
            IsDraw = false,
            DurationMs = 150_000,
            Player1RemainingHp = 200,
            Player2RemainingHp = 0,
            Timeline = new List<CombatTickState>
            {
                new() { TimeMs = 0, P1Hp = 500, P1MaxHp = 500, P2Hp = 500, P2MaxHp = 500 },
                new() { TimeMs = 150_000, P1Hp = 200, P1MaxHp = 500, P2Hp = 0, P2MaxHp = 500 }
            }
        };

        var sim = CombatSimConverter.ToCombatSim(
            syntheticLongResult,
            fx.Repo,
            new Dictionary<string, CardInstance> { ["p1_cutlass"] = p1Wep },
            new Dictionary<string, CardInstance> { ["p2_cutlass"] = p2Wep },
            new[] { p1Entry },
            new[] { p2Entry });

        Assert.True(sim.Frames.Count > 2400, $"150s 长战斗必须生成超过 2400 帧而不截断，实际 Frames={sim.Frames.Count}");
        Assert.Equal(ECombatantId.Player, sim.Winner);
        Assert.False(sim.IsDraw);
    }
}

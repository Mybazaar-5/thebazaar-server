using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
using BazaarGameShared.TempoNet.Responses;
using BazaarServer.Data;
using BazaarServer.Game;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// V7 Official Parity 全链路集成测试：
/// 覆盖 Wave 1 四大模块 + Wave 2 RunEngine 跨模块钩子的真实端到端行为。
/// </summary>
public sealed class V7IntegrationTests
{
    private const string SwashBuckleId = "69688b74-fd2b-42a2-a018-2b720a13f95a";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string RedGumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
    private const string ChocolateBarId = "1a3d4cde-333c-4936-b147-263d73f6d200";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string RegalBladeId = "6ada601b-88a5-4eaf-8813-805bd51c7f9b";
    private const string FatesTemplateId = "aef5e7d8-ea0b-4e7e-8f3d-6e7ba492fa10";

    private sealed class GameFixture
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public Dictionary<string, string> RawCardsJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawMonstersJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawGameModesJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<int, string> RawLevelUpsJson { get; init; } = new();
        public HashSet<string> ValidCardIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly Lazy<GameFixture> RealFixture = new(LoadRealFixture);

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

    private static GameFixture LoadRealFixture()
    {
        string dataDir = ResolveDataDir();
        string dbPath = Path.Combine(dataDir, "GameData.db");

        var rawCards = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawMonsters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawLevels = new Dictionary<int, string>();

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            ReadBlobTable(conn, "cards", rawCards);
            ReadBlobTable(conn, "monsters", rawMonsters);
            ReadBlobTable(conn, "game_modes", rawModes);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, Data FROM level_ups";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rawLevels[r.GetInt32(0)] = Encoding.UTF8.GetString((byte[])r.GetValue(1));
        }

        var validIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string wlPath = Path.Combine(dataDir, "valid_card_ids.json");
        if (File.Exists(wlPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(wlPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        validIds.Add(el.GetString()!);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                     doc.RootElement.TryGetProperty("valid_ids", out var arr) &&
                     arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(el.GetString()))
                        validIds.Add(el.GetString()!);
            }
        }

        return new GameFixture
        {
            DataDir = dataDir,
            DbPath = dbPath,
            Pool = CardPool.Load(dbPath),
            Monsters = MonsterPool.Load(dbPath),
            LevelUps = LevelUpTable.Load(dbPath),
            Mode = GameModeConfig.Load(dbPath),
            Repo = GameDatabaseLoader.Load(dbPath, null),
            Ghosts = PvpGhostPool.Load(dataDir),
            RawCardsJson = rawCards,
            RawMonstersJson = rawMonsters,
            RawGameModesJson = rawModes,
            RawLevelUpsJson = rawLevels,
            ValidCardIds = validIds,
        };
    }

    private static void ReadBlobTable(SqliteConnection conn, string table, Dictionary<string, string> dst)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Data FROM {table}";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            dst[r.GetString(0)] = Encoding.UTF8.GetString((byte[])r.GetValue(1));
    }

    private sealed class OverlayContext : IDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public CardPool Pool { get; }
        public List<BazaarServer.Data.MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayContext(
            IEnumerable<(string Id, string Json)> customCards,
            IEnumerable<(int Level, string Json)>? customLevelUps = null)
        {
            var real = RealFixture.Value;
            TempDir = Path.Combine(Path.GetTempPath(), "bazaar_v7_int_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDir);
            DbPath = Path.Combine(TempDir, "GameData.db");

            var wl = new HashSet<string>(real.ValidCardIds, StringComparer.OrdinalIgnoreCase);

            using (var conn = new SqliteConnection($"Data Source={DbPath}"))
            {
                conn.Open();
                using var create = conn.CreateCommand();
                create.CommandText = """
                    CREATE TABLE cards (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE challenges (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE collectibles (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE game_modes (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE level_ups (Id INTEGER NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE monsters (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE seasons (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    CREATE TABLE tooltips (Id TEXT NOT NULL PRIMARY KEY, Data BLOB NOT NULL) STRICT, WITHOUT ROWID;
                    """;
                create.ExecuteNonQuery();

                using var tx = conn.BeginTransaction();
                foreach (var kv in real.RawGameModesJson)
                    InsertTextRow(conn, tx, "game_modes", kv.Key, kv.Value);
                foreach (var kv in real.RawMonstersJson)
                    InsertTextRow(conn, tx, "monsters", kv.Key, kv.Value);
                foreach (var kv in real.RawLevelUpsJson)
                    InsertIntRow(conn, tx, "level_ups", kv.Key, kv.Value);

                var seedCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SwashBuckleId, CutlassId, RedGumballId, ChocolateBarId,
                    PolShopId, RegalBladeId, FatesTemplateId, ShopEngine.Hour6PvpCardId,
                    CardPool.JungleExpeditionTicketId, CardPool.CrashSiteExpeditionTicketId, CardPool.TinyWorldExpeditionTicketId,
                    CardPool.JungleExpeditionRoomId, CardPool.CrashSiteExpeditionRoomId, CardPool.TinyWorldExpeditionRoomId,
                    "99ac6ab3-0b14-44c3-b7f9-21e008291b85",
                    "c0f1cad2-8561-475a-9e08-a88e8bd62d04",
                    "cfaef293-1d93-4be9-8cf1-0baf3eee6dbb",
                    "c01612a6-574c-4b17-8b2b-68b92f2c07c5",
                    "b594f459-30db-442f-aa19-62804e466e43",
                    "23380916-567d-4a87-b393-4ca5c49b74ba",
                    "cee40d90-51c7-4983-8616-e5fe51161025",
                    "bf424d49-9d37-431e-8686-426004f181be",
                    "dd328ec0-7452-43d8-9d85-559b70947a91",
                    "021639c0-4a9c-49d8-8254-6376994823e2",
                };
                foreach (var sk in real.Pool.Skills("Vanessa").Take(10))
                    seedCardIds.Add(sk.Id);
                foreach (var ev in real.Pool.HourlyEncounters("Vanessa", "EventEncounter").Take(12))
                    seedCardIds.Add(ev.Id);
                foreach (var it in real.Pool.ShopItems("Vanessa").Where(c => c.Size == "Large").Take(12))
                    seedCardIds.Add(it.Id);
                foreach (var it in real.Pool.ShopItems("Vanessa").Take(10))
                    seedCardIds.Add(it.Id);
                foreach (var cbt in real.Pool.OfType("CombatEncounter").Take(10))
                    seedCardIds.Add(cbt.Id);

                foreach (string sid in seedCardIds)
                {
                    if (real.RawCardsJson.TryGetValue(sid, out string? raw))
                        InsertTextRow(conn, tx, "cards", sid, raw);
                }

                foreach (var (id, json) in customCards)
                {
                    InsertTextRow(conn, tx, "cards", id, json);
                    wl.Add(id);
                }

                if (customLevelUps != null)
                {
                    foreach (var (lvl, json) in customLevelUps)
                        InsertIntRow(conn, tx, "level_ups", lvl, json);
                }

                tx.Commit();
            }

            File.WriteAllText(Path.Combine(TempDir, "valid_card_ids.json"), JsonSerializer.Serialize(wl.ToList()));

            Pool = CardPool.Load(DbPath);
            Monsters = MonsterPool.Load(DbPath);
            LevelUps = customLevelUps != null ? LevelUpTable.Load(DbPath) : real.LevelUps;
            Mode = GameModeConfig.Load(DbPath);
            Repo = GameDatabaseLoader.Load(DbPath, null);
            Ghosts = real.Ghosts;
        }

        private static void InsertTextRow(SqliteConnection conn, SqliteTransaction tx, string table, string id, string json)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT OR REPLACE INTO {table}(Id, Data) VALUES (@id, @data)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@data", Encoding.UTF8.GetBytes(json));
            cmd.ExecuteNonQuery();
        }

        private static void InsertIntRow(SqliteConnection conn, SqliteTransaction tx, string table, int id, string json)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"INSERT OR REPLACE INTO {table}(Id, Data) VALUES (@id, @data)";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@data", Encoding.UTF8.GetBytes(json));
            cmd.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (Directory.Exists(TempDir))
                    Directory.Delete(TempDir, recursive: true);
            }
            catch
            {
                // 忽略临时清理异常
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
        int? seed = null)
    {
        var run = new RunState
        {
            RunId = "v7_int_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
            RngSeed = seed ?? 0,
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        engine.StartRun();
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedItem(
        RunState run,
        GameDataRepository repo,
        CardPool pool,
        string templateId,
        EInventorySection section,
        int slot,
        ETier? tierOverride = null)
    {
        var coreTpl = repo.GetCard(templateId)!;
        var poolTpl = pool.Get(templateId);
        ETier tier = tierOverride ?? ShopEngine.ParseTier(coreTpl.StartingTier);
        var core = new CardInstance(coreTpl, ShopEngine.ToCoreTier(tier), null, "own_" + Guid.NewGuid().ToString("N")[..8])
        {
            StartSlot = slot,
        };
        BoardGrid grid = section == EInventorySection.Stash ? run.StashGrid : run.HandGrid;
        Assert.True(grid.TryPlaceWithDisplaceOrSwap(core, slot), $"无法放置 {templateId} 到 {section} 槽位 {slot}");

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
        RunState.PopulateMetaFromTemplate(entry, poolTpl, coreTpl);
        run.Cards[entry.InstanceId] = entry;
        run.SyncAllEntriesFromCoreCardsAndDiff(repo.GameMode.StandardPrices, new[] { entry });
        return entry;
    }

    private static RunState.CardEntry InjectChoiceEncounter(
        RunState run,
        CardPool pool,
        string encounterTemplateId,
        ECardType cardType = ECardType.EventEncounter)
    {
        var tpl = pool.Get(encounterTemplateId);
        string instId = "enc_inj_" + Guid.NewGuid().ToString("N")[..8];
        var entry = new RunState.CardEntry
        {
            InstanceId = instId,
            TemplateId = Guid.Parse(encounterTemplateId),
            Type = tpl != null ? ShopEngine.ParseCardType(tpl.Type) : cardType,
            Size = ECardSize.Medium,
            Tier = tpl != null ? ShopEngine.ParseTier(tpl.StartingTier) : ETier.Bronze,
            Owner = null,
            Section = null,
            Socket = null,
        };
        run.Cards[instId] = entry;
        run.SelectionSet.Add(instId);
        return entry;
    }

    // ── 1. 默认开局属性 (15 Gold / 5 Income / 0 XP / 20 Prestige / 250 HP) ──

    [Fact]
    public void V7_01_StartRunAndCheatRestart_ApplyOfficialDefaultStats_WithoutFalseLevelUp()
    {
        var fx = RealFixture.Value;
        Assert.Equal(15, fx.Mode.StartingGold);
        Assert.Equal(5, fx.Mode.StartingIncome);
        Assert.Equal(0, fx.Mode.StartingExperience);
        Assert.Equal(20, fx.Mode.PrestigeInitial);
        Assert.Equal(250, fx.Mode.StartingHealth);

        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        Assert.Equal(15, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(20, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(250, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(250, run.PlayerAttributes[EPlayerAttributeType.Health]);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PendingLevelUps);
        Assert.False(run.PendingLevelUp);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(1u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(3, run.SelectionSet.Count);

        // 修改运行状态后执行 restart 作弊指令，必须恢复官方默认值并清空去重集合
        run.VisitedDayEncounterIds.Add(PolShopId);
        engine.Handle(new CheatCommand { Args = new List<string> { "gold", "99" } });
        engine.Handle(new CheatCommand { Args = new List<string> { "day", "4" } });
        var restartOut = engine.Handle(new CheatCommand { Args = new List<string> { "restart" } });
        Assert.Null(restartOut.Error);
        Assert.True(restartOut.ForceStateSync);

        Assert.Equal(15, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(5, run.PlayerAttributes[EPlayerAttributeType.Income]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(20, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(250, run.PlayerAttributes[EPlayerAttributeType.HealthMax]);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PendingLevelUps);
        Assert.Empty(run.VisitedDayEncounterIds);
        Assert.Equal(1u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(ERunState.Choice, run.State);
    }

    // ── 2. BoardGrid.TryPlaceWithDisplaceOrSwap + 邻近卡牌 Socket & Move 事件同步 ──

    [Fact]
    public void V7_02_BoardGrid_TryPlaceWithDisplaceOrSwap_SyncsNeighborSocketsAndEmitsMoveEvents()
    {
        const string shopId = "70700001-0001-4001-8001-000000000001";
        string shopJson = BuildShopJson(shopId, "V7 Displace Shop", new[] { ChocolateBarId, CutlassId }, canSelectMultiple: true, selectionIsFree: false);

        using var ov = new OverlayContext(new[] { (shopId, shopJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        engine.Handle(new CheatCommand { Args = new List<string> { "gold", "100" } });

        var gumball = AddOwnedItem(run, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Hand, slot: 0);
        var cutlass = AddOwnedItem(run, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 1);
        var swash = AddOwnedItem(run, ov.Repo, ov.Pool, SwashBuckleId, EInventorySection.Hand, slot: 3);

        // 2a. 同网格换位：把 Swash Buckle(3..4) 拖到 Cutlass(1..2) 的位置，Cutlass 必须顺滑换入 3..4 且同步 Socket 与 Move 事件
        var moveOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(swash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_1 },
        });
        Assert.Null(moveOut.Error);
        Assert.Equal(EContainerSocketId.Socket_1, swash.Socket);
        Assert.Equal(1, swash.CoreCard!.StartSlot);
        Assert.Equal(EContainerSocketId.Socket_3, cutlass.Socket);
        Assert.Equal(3, cutlass.CoreCard!.StartSlot);
        Assert.Equal(EContainerSocketId.Socket_0, gumball.Socket);

        Assert.Contains(moveOut.Events, e =>
            e is GameSimEventCardMoved m &&
            m.InstanceId == swash.InstanceId &&
            m.ToSocket == EContainerSocketId.Socket_1);
        Assert.Contains(moveOut.Events, e =>
            e is GameSimEventCardMoved m &&
            m.InstanceId == cutlass.InstanceId &&
            m.ToSocket == EContainerSocketId.Socket_3);

        var simAfterMove = run.BuildFullGameSim(moveOut.Events);
        // P0-18：本帧已有 GameSimEventCardMoved 的现存卡省略 Placement（交由客户端 ProcessCardMovedEvents 驱动观战移位动画），无事件全量快照保留最终 Placement
        Assert.Null(simAfterMove.Cards[cutlass.InstanceId].Placement);
        var snapAfterMove = run.BuildFullGameSim();
        Assert.Equal(EContainerSocketId.Socket_3, snapAfterMove.Cards[cutlass.InstanceId].Placement!.Socket);

        // 2b. 购买新卡直接落入已占用槽位 (Socket_1)：挤开 Swash Buckle 与 Cutlass，所有网格内卡牌 Socket 必须与 CoreCard.StartSlot 1:1 匹配
        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) }).Error);

        var chocoShelf = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(ChocolateBarId));
        var buyOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(chocoShelf.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_1 },
        });
        Assert.Null(buyOut.Error);
        Assert.Equal(EContainerSocketId.Socket_1, chocoShelf.Socket);
        Assert.Equal(1, chocoShelf.CoreCard!.StartSlot);

        foreach (var gridCard in run.HandGrid.Cards)
        {
            var entry = run.Cards.Values.Single(x => ReferenceEquals(x.CoreCard, gridCard));
            Assert.Equal((EContainerSocketId)gridCard.StartSlot, entry.Socket);
        }

        Assert.NotEqual(EContainerSocketId.Socket_1, swash.Socket);
        Assert.Contains(buyOut.Events, e =>
            e is GameSimEventCardMoved m &&
            m.InstanceId == swash.InstanceId &&
            m.ToSocket == swash.Socket);
        Assert.Contains(buyOut.Events, e =>
            e is GameSimEventCardMoved m &&
            m.InstanceId == cutlass.InstanceId &&
            m.ToSocket == cutlass.Socket);
    }

    // ── 3. 遭遇内经验延迟升级 + 多级连续 PendingLevelUps 顺次结算 ──

    [Fact]
    public void V7_03_InEncounterXpDeferral_AndSequentialMultiLevelPendingLevelUps()
    {
        const string xpItemId = "70700002-0001-4002-8002-000000000001";
        const string shopId = "70700002-0001-4002-8002-000000000002";
        const string lv2StepId = "70700002-0001-4002-8002-000000000003";
        const string lv3StepId = "70700002-0001-4002-8002-000000000004";

        // 起始 XP=0，卖出 +16 XP -> 总计 16 -> 连升 Lv2、Lv3（余 0 XP），PendingLevelUps = 2
        string xpItemJson = BuildOnSellAttrItemJson(xpItemId, "Double Level Gem", buyPrice: 2, sellPrice: 1, "Experience", 16);
        string shopJson = BuildShopJson(shopId, "Multi-Level Deferral Shop", new[] { CutlassId, xpItemId }, canSelectMultiple: true, selectionIsFree: false);
        string lv2StepJson = BuildGoldStepJson(lv2StepId, "Lv2 Reward +5g", goldGain: 5, xpGain: 0, shouldProgressHour: true);
        string lv3StepJson = BuildGoldStepJson(lv3StepId, "Lv3 Reward +7g", goldGain: 7, xpGain: 0, shouldProgressHour: true);
        // V10 targetNewLevel->DB Id=targetNewLevel-1 契约：Lv1->Lv2 读 DB Id=1，Lv2->Lv3 读 DB Id=2
        string lv2Json = BuildSingleStepLevelUpJson(1, healthIncrease: 100, new[] { lv2StepId });
        string lv3Json = BuildSingleStepLevelUpJson(2, healthIncrease: 150, new[] { lv3StepId });

        using var ov = new OverlayContext(
            new[] { (xpItemId, xpItemJson), (shopId, shopJson), (lv2StepId, lv2StepJson), (lv3StepId, lv3StepJson) },
            customLevelUps: new[] { (1, lv2Json), (2, lv3Json) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "gold", "50" } });

        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) }).Error);

        var xpShelf = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(xpItemId));
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(xpShelf.InstanceId), Section = EInventorySection.Hand }).Error);

        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(xpShelf.InstanceId) });
        Assert.Null(sellOut.Error);

        // 遭遇内禁止打断：必须留在 Encounter，累计 PendingLevelUps=2，货架保留
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(shopId, run.CurrentEncounterId);
        Assert.Equal(3, run.PlayerAttributes[EPlayerAttributeType.Level]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(2, run.PendingLevelUps);
        Assert.Single(run.SelectionSet);

        int goldBeforeLevels = run.PlayerAttributes[EPlayerAttributeType.Gold];

        // 离开商店 -> 顺次进入 Lv2 LevelUp (剩余 PendingLevelUps=1)
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(1, run.PendingLevelUps);
        var lv2StepCard = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(lv2StepId));

        // 领完 Lv2 -> 自动接续 Lv3 LevelUp (剩余 PendingLevelUps=0)
        var lv2Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lv2StepCard.InstanceId) });
        Assert.Null(lv2Out.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Equal(0, run.PendingLevelUps);
        var lv3StepCard = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(lv3StepId));

        // 领完 Lv3 -> 干净推进到 Hour 2 Choice，两次升级奖励均已兑现
        var lv3Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lv3StepCard.InstanceId) });
        Assert.Null(lv3Out.Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(goldBeforeLevels + 5 + 7, run.PlayerAttributes[EPlayerAttributeType.Gold]);
    }

    // ── 4. 首次 Prestige <= 0 触发 Fates (1 Prestige 复活)，第二次致命败北直接 EndRunDefeat ──

    [Fact]
    public void V7_04_FatesLastChance_FirstFatalPvpSavesAtOnePrestige_SecondFatalPvpEndsInDefeat()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        // Day 10、0 装备、Prestige=5：保证在 Day 10 PvP 战败且单次扣 10 威望归零触发 Fates
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "win", "9" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "day", "10" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "prestige", "5" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "6" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);

        var pvpOpt1 = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.Type == ECardType.PvpEncounter);
        var fatal1Out = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpOpt1.InstanceId) });
        Assert.Null(fatal1Out.Error);
        Assert.NotNull(fatal1Out.CombatDespawnSim);

        Assert.True(run.FatesUsed);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(FatesTemplateId, run.CurrentEncounterId);
        Assert.Equal(3, run.SelectionSet.Count);
        Assert.Contains(fatal1Out.CombatDespawnSim!.Events, e => e is GameSimEventPlayerPrestigeChanged);
        Assert.Contains(fatal1Out.CombatDespawnSim.Events, e =>
            e is GameSimEventStateTransitioned st &&
            st.ToState == ERunState.Encounter &&
            st.CurrentEncounterId == FatesTemplateId);

        // 退出 Fates 遭遇后（若有升级则顺手退出/选择）推进到次日并再次触发致命 PvP 战败
        var exitFatesOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitFatesOut.Error);
        while (run.State == ERunState.LevelUp)
        {
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }

        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(1, run.PlayerAttributes[EPlayerAttributeType.Prestige]);

        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", "6" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "deal" } }).Error);
        var pvpOpt2 = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.Type == ECardType.PvpEncounter);
        var fatal2Out = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpOpt2.InstanceId) });
        Assert.Null(fatal2Out.Error);
        Assert.NotNull(fatal2Out.CombatDespawnSim);

        Assert.True(run.FatesUsed);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Prestige]);
        Assert.Equal(ERunState.EndRunDefeat, run.State);
        Assert.Contains(fatal2Out.CombatDespawnSim!.Events, e =>
            e is GameSimEventStateTransitioned st && st.ToState == ERunState.EndRunDefeat);
        Assert.Contains(fatal2Out.CombatDespawnSim.Events, e => e is GameSimEventRunCompleted);
    }

    // ── 5. 商人 25% 折扣准确扣款 + 同名低阶/Diamond 过滤 + 跨小时去重 + Quest 购买计数 ──

    [Fact]
    public void V7_05_MerchantDiscount_DuplicateTierFilter_DailyDedup_AndQuestPurchaseCounting()
    {
        const string dupShopId = "70700003-0001-4003-8003-000000000001";
        const string freeWeaponEventId = "70700003-0001-4003-8003-000000000002";
        const string freeWeaponId = "70700003-0001-4003-8003-000000000003";
        const string fuseCutlassShopId = "70700003-0001-4003-8003-000000000004";

        string dupShopJson = BuildShopJson(dupShopId, "Owned Tier Filter Shop", new[] { CutlassId, RedGumballId, ChocolateBarId }, canSelectMultiple: true, selectionIsFree: false, rerollStartCost: 0, totalRerolls: 20);
        string freeWeaponJson = BuildSimpleWeaponJson(freeWeaponId, "Free Test Dagger", buyPrice: 4);
        string freeEventJson = BuildShopJson(freeWeaponEventId, "Free Weapon Event", new[] { freeWeaponId }, canSelectMultiple: false, selectionIsFree: true);
        string fuseShopJson = BuildShopJson(fuseCutlassShopId, "Cutlass Fuse Shop", new[] { CutlassId }, canSelectMultiple: true, selectionIsFree: false);

        using var ov = new OverlayContext(new[]
        {
            (dupShopId, dupShopJson),
            (freeWeaponId, freeWeaponJson),
            (freeWeaponEventId, freeEventJson),
            (fuseCutlassShopId, fuseShopJson),
        });

        // 5a. 商人 25% 折扣绿标价同步客户端并在 OnSelectItem 精确按折后价扣款
        var (discRun, discEngine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 42);
        discEngine.Handle(new CheatCommand { Args = new List<string> { "gold", "200" } });
        discEngine.Handle(new CheatCommand { Args = new List<string> { "day", "5" } });
        var polOpt = InjectChoiceEncounter(discRun, ov.Pool, PolShopId);
        Assert.Null(discEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(polOpt.InstanceId) }).Error);

        var shopField = typeof(RunEngine).GetField("_shop", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var shopEngine = (ShopEngine)shopField.GetValue(discEngine)!;

        // 重掷直到 Pol 货架命中 25%-off 折扣（Pol 单次发 3 件 Large 物品，50 轮内未命中概率 < 10^-8）
        for (int attempt = 0; attempt < 50 && shopEngine.ActiveShelfDiscounts.Count == 0; attempt++)
        {
            discRun.RerollsRemaining = 5;
            discRun.RerollCost = 0;
            Assert.Null(discEngine.Handle(new RerollCommand()).Error);
        }

        Assert.NotEmpty(shopEngine.ActiveShelfDiscounts);
        var discountInfo = shopEngine.ActiveShelfDiscounts.Values.First();
        var discountedEntry = discRun.Cards[discountInfo.InstanceId];
        Assert.Equal(25, discountInfo.DiscountPercent);
        Assert.Equal(ShopEngine.ComputeDiscountedBuyPrice(discountInfo.BasePrice), discountInfo.DiscountedPrice);
        Assert.Equal(discountInfo.DiscountedPrice, discountedEntry.Attributes[ECardAttributeType.BuyPrice]);

        var simBeforeBuy = discRun.BuildFullGameSim();
        Assert.Equal(discountInfo.DiscountedPrice, simBeforeBuy.Cards[discountInfo.InstanceId].Attributes[ECardAttributeType.BuyPrice].Value);

        int goldBeforeDiscountBuy = discRun.PlayerAttributes[EPlayerAttributeType.Gold];
        var buyDiscountOut = discEngine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(discountInfo.InstanceId),
            Section = EInventorySection.Stash,
        });
        Assert.Null(buyDiscountOut.Error);
        Assert.Equal(goldBeforeDiscountBuy - discountInfo.DiscountedPrice, discRun.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Contains(buyDiscountOut.Events, e =>
            e is GameSimEventCardPurchased p &&
            p.InstanceId == discountInfo.InstanceId &&
            p.BuyPrice == (uint)discountInfo.DiscountedPrice);

        // 5b. GetOwnedMaxTiers 传入 DealEncounterShelf 与 OnReroll：拒绝 Diamond 重复与低阶同名卡
        var (dupRun, dupEngine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 7);
        dupEngine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        AddOwnedItem(dupRun, ov.Repo, ov.Pool, CutlassId, EInventorySection.Stash, slot: 0, ETier.Diamond);
        AddOwnedItem(dupRun, ov.Repo, ov.Pool, RedGumballId, EInventorySection.Stash, slot: 2, ETier.Gold);

        var dupShopOpt = InjectChoiceEncounter(dupRun, ov.Pool, dupShopId);
        Assert.Null(dupEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(dupShopOpt.InstanceId) }).Error);

        for (int r = 0; r < 6; r++)
        {
            var shelfTpls = dupRun.SelectionSet.Select(id => dupRun.Cards[id].TemplateId.ToString()).ToList();
            Assert.DoesNotContain(CutlassId, shelfTpls);
            Assert.DoesNotContain(RedGumballId, shelfTpls);
            Assert.Contains(ChocolateBarId, shelfTpls);
            if (r < 5)
                Assert.Null(dupEngine.Handle(new RerollCommand()).Error);
        }

        // 5c. 每日跨小时去重 VisitedDayEncounterIds：选中即登记，跨天清零
        Assert.Contains(dupShopId, dupRun.VisitedDayEncounterIds);

        // 5d. Quest 购买计数：免费发牌不计买，付费二合一融合计为购买 +1
        var (qRun, qEngine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 9);
        qEngine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        qEngine.Handle(new CheatCommand { Args = new List<string> { "gold", "100" } });
        var regalBlade = AddOwnedItem(qRun, ov.Repo, ov.Pool, RegalBladeId, EInventorySection.Hand, slot: 0, ETier.Bronze);
        var bronzeCutlass = AddOwnedItem(qRun, ov.Repo, ov.Pool, CutlassId, EInventorySection.Hand, slot: 2, ETier.Bronze);

        // 免费选择武器 -> Quest_1 必须保持 0
        var freeOpt = InjectChoiceEncounter(qRun, ov.Pool, freeWeaponEventId);
        Assert.Null(qEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(freeOpt.InstanceId) }).Error);
        string freeWeapInst = qRun.SelectionSet.Single();
        Assert.Null(qEngine.Handle(new SelectItemCommand { InstanceId = new InstanceId(freeWeapInst), Section = EInventorySection.Stash }).Error);
        Assert.Equal(0, regalBlade.CoreCard!.QuestProgress.GetValueOrDefault("Quest_1", 0));

        // 付费购买同名同阶 Cutlass 二合一升阶 -> Quest_1 必须推进至 1
        var fuseOpt = InjectChoiceEncounter(qRun, ov.Pool, fuseCutlassShopId);
        Assert.Null(qEngine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(fuseOpt.InstanceId) }).Error);
        string paidCutlassInst = qRun.SelectionSet.Single();
        var fuseBuyOut = qEngine.Handle(new SelectItemCommand { InstanceId = new InstanceId(paidCutlassInst) });
        Assert.Null(fuseBuyOut.Error);
        Assert.Equal(ETier.Silver, bronzeCutlass.Tier);
        Assert.Equal(1, regalBlade.CoreCard.QuestProgress.GetValueOrDefault("Quest_1", 0));
    }

    // ── 6. Day-Start 远征门票流转（单票直入 / 多票选房 + 消耗对应门票 + Hour 1 干净恢复） ──

    [Fact]
    public void V7_06_DayStartExpedition_SingleAndMultiTicketFlow_ResumesHour1Cleanly()
    {
        const string fastShopId = "70700004-0001-4004-8004-000000000001";
        string fastShopJson = BuildShopJson(fastShopId, "Fast Hour Shop", new[] { RedGumballId }, canSelectMultiple: true, selectionIsFree: false);

        using var ov = new OverlayContext(new[] { (fastShopId, fastShopJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts, seed: 11);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        // 6a. Day 5 Hour 6 持 Crash Site + Jungle 门票并设为恰好跨天触发 Lv2 升级：
        //     跨入 Day 6 Hour 1 时不再抢跳进探险房，而是先发放 1 次 Income + 消费 LevelUp，
        //     随后回到 Day 6 Hour 1 Choice，在 3 张常规卡旁下发第 4 张免费 Crash Site 探险入口卡供玩家自选！
        var crashTicket1 = AddOwnedItem(run, ov.Repo, ov.Pool, CardPool.CrashSiteExpeditionTicketId, EInventorySection.Stash, slot: 0);
        var jungleTicketReserve = AddOwnedItem(run, ov.Repo, ov.Pool, CardPool.JungleExpeditionTicketId, EInventorySection.Hand, slot: 0);
        int xpNeededForDay6Lv2 = Math.Max(1, ov.Mode.ExperiencePerLevel - run.PlayerAttributes[EPlayerAttributeType.Experience] - Math.Max(1, ov.Mode.ExperiencePerHour));
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "exp", xpNeededForDay6Lv2.ToString() } }).Error);
        run.Defeats = 4;
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "win", "1" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "day", "5" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", ov.Mode.HoursInADay.ToString() } }).Error);

        int goldBeforeDay6 = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int income = run.PlayerAttributes[EPlayerAttributeType.Income];

        // 通过进入并退出 Day 5 Hour 6 临时商店触发 AdvanceHourOrDay(newDay=true) -> Day 6 Hour 1
        var opt5H6 = InjectChoiceEncounter(run, ov.Pool, fastShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt5H6.InstanceId) }).Error);
        var crossDay6Out = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(crossDay6Out.Error);

        // 先消费跨天挂起的 LevelUp（Income 仅发 1 次，且门票尚未消耗）
        Assert.Equal(6u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(goldBeforeDay6 + income, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.True(run.Cards.ContainsKey(crashTicket1.InstanceId));
        Assert.True(run.Cards.ContainsKey(jungleTicketReserve.InstanceId));
        Assert.Equal(ERunState.LevelUp, run.State);

        int goldBeforeLvlPick = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var lvlCand = run.SelectionSet.Select(id => run.Cards[id]).FirstOrDefault(c => c.Type == ECardType.Skill)
                   ?? run.Cards[run.SelectionSet.First()];
        INetCommand lvlCmd = lvlCand.Type switch
        {
            ECardType.Skill => new SelectSkillCommand { InstanceId = new InstanceId(lvlCand.InstanceId) },
            ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.EncounterStep
                => new SelectEncounterCommand { InstanceId = new InstanceId(lvlCand.InstanceId) },
            _ => new SelectItemCommand { InstanceId = new InstanceId(lvlCand.InstanceId), Section = EInventorySection.Hand },
        };
        var lvlPickOut = engine.Handle(lvlCmd);
        Assert.Null(lvlPickOut.Error);
        Assert.DoesNotContain(lvlPickOut.Events, e => e is GameSimEventPlayerIncomeGained);
        while (run.State is ERunState.Encounter or ERunState.LevelUp)
        {
            var exitLvlSubOut = engine.Handle(new ExitCurrentStateCommand());
            Assert.Null(exitLvlSubOut.Error);
            Assert.DoesNotContain(exitLvlSubOut.Events, e => e is GameSimEventPlayerIncomeGained);
        }

        // LevelUp 完成后恢复 Day 6 Hour 1 Choice：常规 3 张 + 第 4 张免费 CrashSite 入口卡（不重复发 Income）
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(4, run.SelectionSet.Count);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Gold] >= goldBeforeLvlPick);

        var crashRoomChoice = run.SelectionSet
            .Select(id => run.Cards[id])
            .Single(c => string.Equals(c.TemplateId.ToString(), CardPool.CrashSiteExpeditionRoomId, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, crashRoomChoice.Attributes.GetValueOrDefault(ECardAttributeType.BuyPrice, -1));

        // 选择第 4 张 Crash Site 卡进入房间：仅消耗 CrashSite 门票，保留 Jungle 门票
        var enterCrashOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(crashRoomChoice.InstanceId) });
        Assert.Null(enterCrashOut.Error);
        Assert.False(run.Cards.ContainsKey(crashTicket1.InstanceId));
        Assert.Contains(enterCrashOut.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == crashTicket1.InstanceId);
        Assert.True(run.Cards.ContainsKey(jungleTicketReserve.InstanceId));
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(CardPool.CrashSiteExpeditionRoomId, run.CurrentEncounterId);

        // 完成 Crash Site 内步骤 -> 推进到 Day 6 Hour 2
        string crashStepId = run.SelectionSet.First();
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(crashStepId) }).Error);
        while (run.State is ERunState.Encounter or ERunState.LevelUp)
        {
            if (run.State == ERunState.LevelUp)
                Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(run.SelectionSet.First()) }).Error);
            else
                Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(6u, run.Day);
        Assert.Equal(2u, run.Hour);

        // 6b. Day 8 Hour 6 持 Jungle + TinyWorld 双票跨日到 Day 9 Hour 1：
        //     仅第 4 张下发 Temple/Jungle 入口卡；若玩家改选常规商店跳过，门票完整保留到后续日期，
        //     直至于 Day 10 Hour 1 正常下发第 4 张 Tiny World 入口卡并自选消耗 Tiny World 门票
        var tinyTicket = AddOwnedItem(run, ov.Repo, ov.Pool, CardPool.TinyWorldExpeditionTicketId, EInventorySection.Stash, slot: 2);
        run.Defeats = Math.Max(run.Defeats, 8u - run.Victories);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "day", "8" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", ov.Mode.HoursInADay.ToString() } }).Error);
        var optDay8H6 = InjectChoiceEncounter(run, ov.Pool, fastShopId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(optDay8H6.InstanceId) }).Error);
        Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        while (run.State == ERunState.LevelUp)
        {
            var lc = run.Cards[run.SelectionSet.First()];
            if (lc.Type == ECardType.Skill)
                Assert.Null(engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(lc.InstanceId) }).Error);
            else
                Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lc.InstanceId) }).Error);
        }
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(9u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(4, run.SelectionSet.Count);
        Assert.Contains(run.SelectionSet, id => string.Equals(run.Cards[id].TemplateId.ToString(), CardPool.JungleExpeditionRoomId, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(run.SelectionSet, id => string.Equals(run.Cards[id].TemplateId.ToString(), CardPool.TinyWorldExpeditionRoomId, StringComparison.OrdinalIgnoreCase));

        // 跳过第 4 张 Jungle 入口卡（改选第 1 张常规遭遇再退场）-> 两张门票均不销毁
        var regularPickId = run.SelectionSet.First(id => !string.Equals(run.Cards[id].TemplateId.ToString(), CardPool.JungleExpeditionRoomId, StringComparison.OrdinalIgnoreCase));
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(regularPickId) }).Error);
        Assert.True(run.Cards.ContainsKey(jungleTicketReserve.InstanceId));
        Assert.True(run.Cards.ContainsKey(tinyTicket.InstanceId));

        // 跳到 Day 9 Hour 6 跨日进入 Day 10 Hour 1 -> 下发第 4 张 TinyWorld 入口卡并点击进入
        run.Defeats = Math.Max(run.Defeats, 9u - run.Victories);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "day", "9" } }).Error);
        Assert.Null(engine.Handle(new CheatCommand { Args = new List<string> { "hour", ov.Mode.HoursInADay.ToString() } }).Error);
        if (run.State == ERunState.Encounter)
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        while (run.State == ERunState.LevelUp)
        {
            var lc = run.Cards[run.SelectionSet.First()];
            if (lc.Type == ECardType.Skill)
                Assert.Null(engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(lc.InstanceId) }).Error);
            else
                Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lc.InstanceId) }).Error);
        }
        if (run.Day == 9)
        {
            var opt9H6 = InjectChoiceEncounter(run, ov.Pool, fastShopId);
            Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt9H6.InstanceId) }).Error);
            Assert.Null(engine.Handle(new ExitCurrentStateCommand()).Error);
        }
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(10u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(4, run.SelectionSet.Count);
        var tinyRoomChoice = run.SelectionSet
            .Select(id => run.Cards[id])
            .Single(c => string.Equals(c.TemplateId.ToString(), CardPool.TinyWorldExpeditionRoomId, StringComparison.OrdinalIgnoreCase));
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(tinyRoomChoice.InstanceId) }).Error);
        Assert.False(run.Cards.ContainsKey(tinyTicket.InstanceId));
        Assert.True(run.Cards.ContainsKey(jungleTicketReserve.InstanceId));
        Assert.Equal(CardPool.TinyWorldExpeditionRoomId, run.CurrentEncounterId);
    }

    // ── 7. MaxSelections 上限重置、计数、自动清架与干净退场 ──

    [Fact]
    public void V7_07_MaxSelections_CapEnforcement_StepDealtNewCardsResetAndAutoClearExit()
    {
        const string rootEventId = "70700005-0001-4005-8005-000000000001";
        const string dealStepId = "70700005-0001-4005-8005-000000000002";

        string rootJson = BuildStepEventJson(rootEventId, "MaxSelections Step Event", new[] { dealStepId });
        string stepJson = BuildDealCardsStepWithMaxSelectionsJson(
            dealStepId,
            "Deal 3 Pick 2 Step",
            new[] { CutlassId, RedGumballId, ChocolateBarId },
            maxSelections: 2);

        using var ov = new OverlayContext(new[] { (rootEventId, rootJson), (dealStepId, stepJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });

        var evOpt = InjectChoiceEncounter(run, ov.Pool, rootEventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(evOpt.InstanceId) }).Error);
        Assert.Equal(0, run.SelectionsMadeInCurrentContext);

        // 执行 EncounterStep 发 3 张替代卡 (MaxSelections=2)：DealtNewCards 必须把计数重置为 0
        string stepInstId = run.SelectionSet.Single();
        var dealOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(stepInstId) });
        Assert.Null(dealOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(0, run.SelectionsMadeInCurrentContext);
        Assert.Equal(3, run.SelectionSet.Count);

        // 第 1 次选择：仍停留在 Encounter，剩余 2 张候选卡
        string pick1 = run.SelectionSet[0];
        var pick1Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(pick1), Section = EInventorySection.Hand });
        Assert.Null(pick1Out.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(1, run.SelectionsMadeInCurrentContext);
        Assert.Equal(2, run.SelectionSet.Count);

        // 第 2 次选择：达到 MaxSelections=2 上限，自动清理剩余第 3 张免费卡并安全退场至 Hour 2 Choice
        string pick2 = run.SelectionSet[0];
        string unpicked = run.SelectionSet[1];
        var pick2Out = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(pick2), Section = EInventorySection.Stash });
        Assert.Null(pick2Out.Error);

        Assert.False(run.Cards.ContainsKey(unpicked));
        Assert.DoesNotContain(unpicked, run.SelectionSet);
        Assert.Contains(pick2Out.Events, e =>
            e is GameSimEventCardDisposed d &&
            d.InstanceId == unpicked &&
            d.Reason == ECardDisposalReason.Cleared);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(3, run.SelectionSet.Count);
        Assert.All(run.SelectionSet, id => Assert.NotEqual(ECardType.Item, run.Cards[id].Type));

        // 额外验证达到上限状态若未离场时，HasReachedMaxSelections 拦截额外选卡指令
        var evOpt2 = InjectChoiceEncounter(run, ov.Pool, rootEventId);
        Assert.Null(engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(evOpt2.InstanceId) }).Error);
        Assert.Null(engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(run.SelectionSet.Single()) }).Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(3, run.SelectionSet.Count);
        run.SelectionsMadeInCurrentContext = 2; // 已达 MaxSelections=2 上限
        var blockedOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(run.SelectionSet[0]), Section = EInventorySection.Hand });
        Assert.NotNull(blockedOut.Error);
        Assert.Equal(EResponseErrorType.NotAllowed, blockedOut.Error!.ErrorType);
    }

    // ── 测试 JSON 构造辅助 ──

    private static string BuildShopJson(
        string id,
        string name,
        string[] itemIds,
        bool canSelectMultiple,
        bool selectionIsFree,
        int rerollStartCost = 2,
        int totalRerolls = 3)
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
            SpawningEligibility = "Always",
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = new[]
                    {
                        new
                        {
                            @__type = "TSpawnGroup",
                            Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = itemIds } },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                            Prerequisites = (object?)null,
                            RandomWeight = 100,
                            Behaviors = (object?)null,
                        },
                    },
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                    Behaviors = (object?)null,
                },
                Rules = new
                {
                    CanSelectMultiple = canSelectMultiple,
                    SelectionIsFree = selectionIsFree,
                    CanExit = true,
                    RerollRules = new
                    {
                        TotalAllowedRerolls = totalRerolls,
                        CostIncrease = 0,
                        StartingCost = rerollStartCost,
                        CostMax = 10,
                    },
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildSimpleWeaponJson(string id, string name, int buyPrice)
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
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["CooldownMax"] = 3000,
                        ["DamageAmount"] = 10,
                        ["Multicast"] = 1,
                        ["BuyPrice"] = buyPrice,
                        ["SellPrice"] = Math.Max(1, buyPrice / 2),
                    },
                    AbilityIds = Array.Empty<string>(),
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildOnSellAttrItemJson(string id, string name, int buyPrice, int sellPrice, string playerAttr, int amount)
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
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Always",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["BuyPrice"] = buyPrice,
                        ["SellPrice"] = sellPrice,
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
                    Trigger = new
                    {
                        @__type = "TTriggerOnCardSold",
                        Subject = new { @__type = "TTargetCardSelf", Conditions = (object?)null },
                    },
                    ActiveIn = "HandAndStash",
                    Action = new
                    {
                        @__type = "TActionPlayerModifyAttribute",
                        AttributeType = playerAttr,
                        Value = new { @__type = "TFixedValue", Value = (double)amount },
                        Operation = "Add",
                        Duration = (object?)null,
                        Target = new { @__type = "TTargetPlayerAbsolute", TargetMode = "Player", Conditions = (object?)null },
                        Cost = (object?)null,
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildGoldStepJson(string id, string name, int goldGain, int xpGain, bool shouldProgressHour)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterStep",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "EncounterStep",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            IsReselectable = false,
            ShouldProgressHour = shouldProgressHour,
            ExperienceAwardUponSelection = xpGain,
            NextEncounterOnSelection = (object?)null,
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { @__type = "TTriggerOnCardSelected" },
                    ActiveIn = "HandAndStash",
                    Action = new
                    {
                        @__type = "TActionPlayerModifyAttribute",
                        AttributeType = "Gold",
                        Value = new { @__type = "TFixedValue", Value = (double)goldGain },
                        Operation = "Add",
                        Duration = (object?)null,
                        Target = new { @__type = "TTargetPlayerAbsolute", TargetMode = "Player", Conditions = (object?)null },
                        Cost = (object?)null,
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildSingleStepLevelUpJson(int level, int healthIncrease, string[] stepIds)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TLevelUp",
            Level = level,
            HealthIncrease = healthIncrease,
            Version = "5.0.0",
            Rewards = new
            {
                @__type = "TSpawnContextQuery",
                Groups = new[]
                {
                    new
                    {
                        @__type = "TSpawnGroup",
                        Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = stepIds } },
                        SelectionMethod = "Random",
                        Limit = new { @__type = "TFixedValue", Value = 1.0 },
                        RandomWeight = 100,
                        Behaviors = new[] { new { @__type = "TSpawnBehaviorIgnoreTierTable" } },
                        Prerequisites = (object?)null,
                    },
                },
            },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildStepEventJson(string id, string name, string[] stepIds)
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
            SelectionContext = new
            {
                SpawnContext = new
                {
                    @__type = "TSpawnContextQuery",
                    Groups = new[]
                    {
                        new
                        {
                            @__type = "TSpawnGroup",
                            Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = stepIds } },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = (double)stepIds.Length },
                            Prerequisites = (object?)null,
                            RandomWeight = 100,
                            Behaviors = (object?)null,
                        },
                    },
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = (double)stepIds.Length },
                    Behaviors = (object?)null,
                },
                Rules = new
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = false,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildDealCardsStepWithMaxSelectionsJson(
        string id,
        string name,
        string[] itemIds,
        int maxSelections)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterStep",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "EncounterStep",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            IsReselectable = false,
            ShouldProgressHour = true,
            ExperienceAwardUponSelection = 0,
            NextEncounterOnSelection = (object?)null,
            Abilities = new Dictionary<string, object>
            {
                ["0"] = new
                {
                    Id = "0",
                    Trigger = new { @__type = "TTriggerOnCardSelected" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        @__type = "TActionGameDealCards",
                        SpawnMode = "Replace",
                        SpawnContext = new
                        {
                            @__type = "TSpawnContextQuery",
                            Groups = new[]
                            {
                                new
                                {
                                    @__type = "TSpawnGroup",
                                    Filters = new[] { new { @__type = "TSpawnFilterIdList", Ids = itemIds } },
                                    SelectionMethod = "Sequential",
                                    Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                                    Prerequisites = (object?)null,
                                    RandomWeight = 100,
                                    Behaviors = (object?)null,
                                },
                            },
                            SelectionMethod = "Sequential",
                            Limit = new { @__type = "TFixedValue", Value = (double)itemIds.Length },
                            Behaviors = (object?)null,
                        },
                        SelectionContextRules = new
                        {
                            MaxSelections = maxSelections,
                            CanSelectMultiple = true,
                            SelectionIsFree = true,
                            CanExit = true,
                        },
                        Cost = (object?)null,
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }
}

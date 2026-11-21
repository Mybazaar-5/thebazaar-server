using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2.5 RunEngine 全链路集成测试（真实指令驱动 + 挂起状态栈 + 多步奇遇 + 战后永久成长回写 + 序列化往返恢复）。
/// </summary>
public sealed class V4RunOrchestratorIntegrationTests
{
    private const string SwashBuckleId = "69688b74-fd2b-42a2-a018-2b720a13f95a";
    private const string CutlassId = "1b955b8d-0ea6-4437-935c-d588be5898c1";
    private const string LangxianId = "53099361-599f-4c1a-ad4a-d2fbb491b756";
    private const string RedGumballId = "6d6199b4-82a4-441d-9329-f4164737ac6b";
    private const string ChocolateBarId = "1a3d4cde-333c-4936-b147-263d73f6d200";
    private const string PolShopId = "065f6793-8607-4f02-abfe-db6afb221ab2";
    private const string FangedIngletCombatId = "1ea147f3-5712-4333-b347-8fd65d08f009";
    private const string TrainingDummyCombatId = "068d7ed2-3716-4bab-9240-9d7ff63c6fa3";

    private sealed class GameFixture
    {
        public string DataDir { get; init; } = "";
        public string DbPath { get; init; } = "";
        public CardPool Pool { get; init; } = null!;
        public List<MonsterTemplate> Monsters { get; init; } = null!;
        public LevelUpTable LevelUps { get; init; } = null!;
        public GameModeConfig Mode { get; init; } = null!;
        public GameDataRepository Repo { get; init; } = null!;
        public PvpGhostPool Ghosts { get; init; } = null!;
        public Dictionary<string, string> RawCardsJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawMonstersJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> RawGameModesJson { get; init; } = new(StringComparer.OrdinalIgnoreCase);
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

        using (var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            conn.Open();
            ReadBlobTable(conn, "cards", rawCards);
            ReadBlobTable(conn, "monsters", rawMonsters);
            ReadBlobTable(conn, "game_modes", rawModes);
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
            ValidCardIds = validIds,
        };
    }

    private static void ReadBlobTable(SqliteConnection conn, string table, Dictionary<string, string> dst)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT Id, Data FROM {table}";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            dst[r.GetString(0)] = Encoding.UTF8.GetString((byte[])r.GetValue(1));
        }
    }

    private sealed class OverlayContext : IDisposable
    {
        public string TempDir { get; }
        public string DbPath { get; }
        public CardPool Pool { get; }
        public List<MonsterTemplate> Monsters { get; }
        public LevelUpTable LevelUps { get; }
        public GameModeConfig Mode { get; }
        public GameDataRepository Repo { get; }
        public PvpGhostPool Ghosts { get; }

        public OverlayContext(
            IEnumerable<(string Id, string Json)> customCards,
            IEnumerable<(string Id, string Json)>? customMonsters = null,
            IEnumerable<(int Level, string Json)>? customLevelUps = null)
        {
            var real = RealFixture.Value;
            TempDir = Path.Combine(Path.GetTempPath(), "bazaar_orch_test_" + Guid.NewGuid().ToString("N"));
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

                // 复制测试所需的真实代表卡牌与怪物身上引用的装备/技能模板
                var seedCardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    SwashBuckleId, CutlassId, LangxianId, RedGumballId, ChocolateBarId,
                    PolShopId, FangedIngletCombatId, TrainingDummyCombatId,
                    "fb41fdef-d83b-4674-8cb3-9a224aa8f84a",
                    "7cbc8056-482b-4ccf-887a-ddb2702c856c",
                    ShopEngine.Hour6PvpCardId,
                };
                foreach (var m in real.Monsters)
                {
                    if (string.Equals(m.Id, "d7391818-e677-4c98-994d-71e25ed2d41c", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.Id, "29549218-4198-44f1-8ebc-e118dd693ddb", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var it in m.AllLootCandidates())
                            if (!string.IsNullOrWhiteSpace(it.TemplateId))
                                seedCardIds.Add(it.TemplateId);
                    }
                }
                foreach (var sk in real.Pool.Skills("Vanessa").Take(8))
                    seedCardIds.Add(sk.Id);
                foreach (var ev in real.Pool.HourlyEncounters("Vanessa", "EventEncounter").Take(10))
                    seedCardIds.Add(ev.Id);
                foreach (var it in real.Pool.ShopItems("Vanessa").Take(8))
                    seedCardIds.Add(it.Id);

                foreach (string id in seedCardIds)
                {
                    if (real.RawCardsJson.TryGetValue(id, out string? raw))
                        InsertTextRow(conn, tx, "cards", id, raw);
                }

                foreach (var (id, json) in customCards)
                {
                    InsertTextRow(conn, tx, "cards", id, json);
                    wl.Add(id);
                }

                if (customMonsters != null)
                {
                    foreach (var (id, json) in customMonsters)
                        InsertTextRow(conn, tx, "monsters", id, json);
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
                // 测试临时目录清理失败不影响结果
            }
        }
    }

    private static (RunState Run, RunEngine Engine) CreateStartedEngine(
        CardPool pool,
        List<MonsterTemplate> monsters,
        LevelUpTable levelUps,
        GameModeConfig mode,
        GameDataRepository repo,
        PvpGhostPool ghosts,
        EHero hero = EHero.Vanessa)
    {
        var run = new RunState
        {
            RunId = "test_run_" + Guid.NewGuid().ToString("N")[..8],
            GameModeId = pool.DefaultGameModeId,
            Hero = hero,
            PlayMode = EPlayMode.Unranked,
        };
        var engine = new RunEngine(run, pool, monsters, levelUps, mode, repo, ghosts);
        engine.StartRun();
        engine.Handle(new CheatCommand { Args = new List<string> { "skipstart" } });
        engine.Handle(new CheatCommand { Args = new List<string> { "slots" } });
        engine.Handle(new CheatCommand { Args = new List<string> { "gold", "100" } });
        return (run, engine);
    }

    private static RunState.CardEntry AddOwnedHandItem(
        RunState run,
        GameDataRepository repo,
        CardPool pool,
        string templateId,
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
        Assert.True(run.HandGrid.TryPlaceOrShift(core, slot), $"无法放置测试卡牌 {templateId} 到 slot {slot}");

        var entry = new RunState.CardEntry
        {
            InstanceId = core.InstanceId,
            TemplateId = Guid.Parse(templateId),
            Type = ECardType.Item,
            Size = ShopEngine.ParseSize(coreTpl.Size),
            Tier = tier,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = (EContainerSocketId)slot,
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

    // ── (a) 商店期间 MoveItem 触发 OnCardMoved 与 OutOfCombat 光环重算及 Attributes 回写 ──

    [Fact]
    public void ShopMoveItem_RecalculatesOutOfCombatAura_UpdatesEntryAttributesAndEmitsEvents()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        // 先放两张互不相邻的真卡：Cutlass(0..1) 与 Swash Buckle(6..7)
        var cutlass = AddOwnedHandItem(run, fx.Repo, fx.Pool, CutlassId, slot: 0, ETier.Bronze);
        var swash = AddOwnedHandItem(run, fx.Repo, fx.Pool, SwashBuckleId, slot: 6, ETier.Gold);

        Assert.Equal(10, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
        Assert.Equal(0, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.CritChance, 0));

        // 进入真实商店 Pol，确保在 Encounter 状态期间摆盘
        var polOpt = InjectChoiceEncounter(run, fx.Pool, PolShopId, ECardType.EventEncounter);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(polOpt.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);

        // 把 Swash Buckle 拖到 Cutlass 右侧紧邻位 (slot 2..3)
        var moveOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(swash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_2 },
        });
        Assert.Null(moveOut.Error);

        // 验证 Cutlass 获得 +20 CritChance 与 +20 DamageAmount (10 -> 30) 且回写到 RunState.Cards
        Assert.Equal(20, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.CritChance, 0));
        Assert.Equal(30, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
        Assert.Contains(moveOut.Events, e => e is GameSimEventCardMoved);
        Assert.Contains(moveOut.Events, e => e is GameSimEventEffectAuraExecuted);

        // 拖回远离位 (slot 6..7)，光环自动撤除，DamageAmount 回到基础 10
        var moveAwayOut = engine.Handle(new MoveItemCommand
        {
            InstanceId = new InstanceId(swash.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_6 },
        });
        Assert.Null(moveAwayOut.Error);
        Assert.Equal(0, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.CritChance, 0));
        Assert.Equal(10, cutlass.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));
    }

    // ── (b-1) 商店卖出经验道具触发升级 → 压栈现存货架进入 LevelUp → 选奖后精确弹出恢复原商店 ──

    [Fact]
    public void ShopSellXpConsumable_TriggersLevelUp_PushesSuspendedShopAndRestoresExactShelf()
    {
        string xpItemId = "11111111-1111-4111-8111-111111111101";
        string shopId = "11111111-1111-4111-8111-111111111102";
        string lvlStepId = "11111111-1111-4111-8111-111111111103";

        string xpItemJson = BuildCustomXpOnSellItemJson(xpItemId, "Test XP Gem", buyPrice: 2, sellPrice: 1, xpGain: 8);
        string shopJson = BuildFixedShopJson(shopId, "Deterministic XP Shop", new[] { CutlassId, RedGumballId, xpItemId });
        string lvlStepJson = BuildGoldStepJson(lvlStepId, "Level Up +5 Gold", goldGain: 5, xpGain: 0, shouldProgressHour: true);
        string lvl2Json = BuildSingleStepLevelUpJson(2, healthIncrease: 100, stepIds: new[] { lvlStepId });

        using var ov = new OverlayContext(
            new[] { (xpItemId, xpItemJson), (shopId, shopJson), (lvlStepId, lvlStepJson) },
            customLevelUps: new[] { (2, lvl2Json) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId, ECardType.EventEncounter);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(shopId, run.CurrentEncounterId);
        Assert.Equal(3, run.SelectionSet.Count);

        uint dayBefore = run.Day;
        uint hourBefore = run.Hour;

        var xpShelfEntry = run.SelectionSet
            .Select(id => run.Cards[id])
            .Single(c => c.TemplateId == Guid.Parse(xpItemId));
        var remainingShelfIds = run.SelectionSet
            .Where(id => id != xpShelfEntry.InstanceId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(2, remainingShelfIds.Count);

        // V9-B2：默认官方起始经验为 0（StartingExperience=0）
        Assert.Equal(ov.Mode.StartingExperience, run.PlayerAttributes[EPlayerAttributeType.Experience]);
        Assert.Equal(0, run.PlayerAttributes[EPlayerAttributeType.Experience]);

        // 买入经验道具，留在同一商店中
        var buyOut = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(xpShelfEntry.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3 },
        });
        Assert.Null(buyOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(2, run.SelectionSet.Count);

        // V7-1.5：在商店内卖出经验道具触发升级经验时只累计 PendingLevelUps，绝不在 Encounter 内抢跳打断
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(xpShelfEntry.InstanceId) });
        Assert.Null(sellOut.Error);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(shopId, run.CurrentEncounterId);
        Assert.True(run.PendingLevelUp);
        Assert.Equal(1, run.PendingLevelUps);
        Assert.Empty(run.SuspendedStates);
        Assert.Equal(remainingShelfIds, run.SelectionSet.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.DoesNotContain(sellOut.Events, e => e is GameSimEventInterruptStateEntered);

        // 仍在同一商店内可继续购买原货架上的 Cutlass，待退出商店后才统一进入 LevelUp
        var cutlassShelf = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(CutlassId));
        var buyCutlass = engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(cutlassShelf.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_4 },
        });
        Assert.Null(buyCutlass.Error);
        Assert.Equal(ECombatantId.Player, cutlassShelf.Owner);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.True(run.PendingLevelUp);

        // 退出商店后才触发延迟结算的 LevelUp
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.False(run.PendingLevelUp);
        Assert.Equal(dayBefore, run.Day);
        Assert.Equal(hourBefore, run.Hour);

        // 选完 LevelUp 奖励，正常推进到下一小时 Choice
        string rewardId = run.SelectionSet.First();
        var lvlOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(rewardId) });
        Assert.Null(lvlOut.Error);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(dayBefore, run.Day);
        Assert.Equal(hourBefore + 1, run.Hour);
    }

    // ── (b-2) 推进小时时恰好升级 → 压栈 Choice 进入 LevelUp → 选完恢复到同一新小时 Choice ──

    [Fact]
    public void AdvanceHour_WithLevelUpExperience_PushesChoiceAndRestoresSameHourChoice()
    {
        string shopId = "22222222-2222-4222-8222-222222222201";
        string lvlStepId = "22222222-2222-4222-8222-222222222202";

        string shopJson = BuildFixedShopJson(shopId, "Simple Shop", new[] { CutlassId });
        string lvlStepJson = BuildGoldStepJson(lvlStepId, "Level Up Reward", goldGain: 3, xpGain: 0, shouldProgressHour: true);
        string lvl2Json = BuildSingleStepLevelUpJson(2, healthIncrease: 100, stepIds: new[] { lvlStepId });

        using var ov = new OverlayContext(
            new[] { (shopId, shopJson), (lvlStepId, lvlStepJson) },
            customLevelUps: new[] { (2, lvl2Json) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId, ECardType.EventEncounter);
        engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) });
        Assert.Equal(1u, run.Hour);

        // V9-B2：默认开局起始经验为 0（StartingExperience=0），扣减当前经验后再补足至差 1 小时升级
        int currentXp = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Experience, 0);
        Assert.Equal(ov.Mode.StartingExperience, currentXp);
        Assert.Equal(0, currentXp);
        int almostLvlUp = Math.Max(0, ov.Mode.ExperiencePerLevel - currentXp - Math.Max(1, ov.Mode.ExperiencePerHour));
        engine.Handle(new CheatCommand { Args = new List<string> { "exp", almostLvlUp.ToString() } });

        // 离开商店推进至 Hour 2 (+1 XP 升级)，应把 Hour 2 Choice 压栈后进入 LevelUp
        var exitOut = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitOut.Error);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(ERunState.LevelUp, run.State);
        Assert.Single(run.SuspendedStates);
        Assert.Equal(ERunState.Choice, run.SuspendedStates.Peek().State);

        // 选完 LevelUp 奖励，应留在 Hour 2 Choice（绝不额外推进成 Hour 3）
        string lvlOptId = run.SelectionSet.First();
        var pickOut = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(lvlOptId) });
        Assert.Null(pickOut.Error);
        Assert.Empty(run.SuspendedStates);
        Assert.Equal(ERunState.Choice, run.State);
        Assert.Equal(2u, run.Hour);
        Assert.Equal(3, run.SelectionSet.Count);
    }

    // ── (c) 多步奇遇（EncounterStep → 子奇遇 → EncounterStep → 触发野怪战斗）全串联执行 ──

    [Fact]
    public void MultiStepEncounterStep_ChainedSubEventAndCombatEncounter_ExecutesFullCombatPipeline()
    {
        string rootEventId = "33333333-3333-4333-8333-333333333301";
        string step1Id = "33333333-3333-4333-8333-333333333302";
        string subEventId = "33333333-3333-4333-8333-333333333303";
        string step2Id = "33333333-3333-4333-8333-333333333304";

        string rootJson = BuildStepEventJson(rootEventId, "Root MultiStep Event", new[] { step1Id }, selectionIsFree: false);
        string step1Json = BuildChainedStepJson(step1Id, "Pay 2 Gold & Enter SubEvent", buyPrice: 2, goldGain: 0, nextEncounterId: subEventId);
        string subJson = BuildStepEventJson(subEventId, "Sub Event Before Ambush", new[] { step2Id });
        string step2Json = BuildChainedStepJson(step2Id, "Ambush Fanged Inglet", buyPrice: 0, goldGain: 0, nextEncounterId: FangedIngletCombatId);

        using var ov = new OverlayContext(new[]
        {
            (rootEventId, rootJson),
            (step1Id, step1Json),
            (subEventId, subJson),
            (step2Id, step2Json),
        });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        AddOwnedHandItem(run, ov.Repo, ov.Pool, CutlassId, slot: 3, ETier.Bronze);
        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];

        // 1. 进入首层奇遇
        var rootOpt = InjectChoiceEncounter(run, ov.Pool, rootEventId, ECardType.EventEncounter);
        var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(rootOpt.InstanceId) });
        Assert.Null(enterOut.Error);
        Assert.Equal(rootEventId, run.CurrentEncounterId);
        Assert.Single(run.SelectionSet);

        // 2. 通过 SelectItem 点选第 1 步 EncounterStep → 扣 2 金币并链式跳转到子奇遇 subEventId
        string step1Inst = run.SelectionSet.Single();
        var stepOut1 = engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(step1Inst) });
        Assert.Null(stepOut1.Error);
        Assert.Equal(goldBefore - 2, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(subEventId, run.CurrentEncounterId);
        Assert.Single(run.SelectionSet);
        Assert.Equal(Guid.Parse(step2Id), run.Cards[run.SelectionSet.Single()].TemplateId);

        // 3. 通过 SelectEncounter 点选第 2 步 EncounterStep → 直接串联进入真实三段式战斗序列
        string step2Inst = run.SelectionSet.Single();
        var stepOut2 = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(step2Inst) });
        Assert.Null(stepOut2.Error);
        Assert.NotNull(stepOut2.CombatSpawnSim);
        Assert.NotNull(stepOut2.Combat);
        Assert.NotNull(stepOut2.CombatDespawnSim);
        Assert.Equal(ECombatantId.Player, stepOut2.Combat!.Winner);
        Assert.True(stepOut2.Combat.Frames.Count > 1);
    }

    // ── (d-1) 真实 PvE 胜利后 OnFightEnded(opponent) 让 Langxian 永久涨攻击并分发战利品 ──

    [Fact]
    public void RealCombatSequence_LangxianPvEVictory_SyncsOnFightEndedAndDealsMonsterLoot()
    {
        var fx = RealFixture.Value;
        var (run, engine) = CreateStartedEngine(fx.Pool, fx.Monsters, fx.LevelUps, fx.Mode, fx.Repo, fx.Ghosts);

        var langxian = AddOwnedHandItem(run, fx.Repo, fx.Pool, LangxianId, slot: 3, ETier.Bronze);
        Assert.Equal(40, langxian.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0));

        // 真实 Day 1 野怪遭遇 Fanged Inglet（含 RewardVictory 真实掉落池）
        var mobOpt = InjectChoiceEncounter(run, fx.Pool, FangedIngletCombatId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });

        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.CombatSpawnSim);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);
        Assert.Equal(ECombatantId.Player, combatOut.Combat!.Winner);

        // Langxian OnFightEnded(Win, opponent != null) 必须触发并将 Custom_1 (+1 -> Aura +40 Damage) 写回 CoreCard、RunState.Cards 与 CombatDespawnSim
        Assert.True(langxian.CoreCard!.PermanentAdd.GetValueOrDefault("Custom_1", 0) >= 1,
            "Langxian 战后应保留 PermanentAdd[\"Custom_1\"] >= 1 永久增益");
        Assert.True(langxian.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0) > 40,
            $"Langxian 面板 DamageAmount 应 > 40，实际={langxian.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0)}");
        Assert.Contains(combatOut.CombatDespawnSim!.Events, e => e is GameSimEventEffectAuraExecuted);
        Assert.Contains(combatOut.CombatDespawnSim.Events, e => e is GameSimEventCardDealt);
        Assert.Equal(ERunState.Loot, run.State);
    }

    // ── (d-2) 战中永久属性成长 SyncPermanentChangesBack 与 PvE 战败按伤血比例折算金币/经验 ──

    [Fact]
    public void RealCombatSequence_InCombatPermanentGrowthAndPvEDefeat_SyncsPermAndPartialRewards()
    {
        string permWeaponId = "44444444-4444-4444-8444-444444444401";
        string bossWeaponId = "44444444-4444-4444-8444-444444444402";
        string bossMonsterId = "44444444-4444-4444-8444-444444444403";
        string bossCombatEncId = "44444444-4444-4444-8444-444444444404";

        // 我方武器：1s 冷却，基础 60 伤，每次开火给自身永久 +20 DamageAmount
        string permWeaponJson = BuildPermScalingWeaponJson(permWeaponId, "Perm Blade", cooldownMs: 1000, baseDmg: 60, permIncOnFire: 20);
        // Boss 武器：1.2s 冷却，基础 140 伤，两发即可击杀 250 HP 玩家（保证玩家先开火两次造成伤血并积累战中永久成长，再战败）
        string bossWeaponJson = BuildPermScalingWeaponJson(bossWeaponId, "Boss Crusher", cooldownMs: 1200, baseDmg: 140, permIncOnFire: 0);
        string bossMonsterJson = BuildBossMonsterJson(bossMonsterId, "Test Iron Colossus", healthMax: 450, level: 10, bossWeaponId);
        string bossEncJson = BuildCombatEncounterJson(bossCombatEncId, "Test Colossus Encounter", bossMonsterId, level: 10, rewardGold: 10, rewardXp: 6);

        using var ov = new OverlayContext(
            new[]
            {
                (permWeaponId, permWeaponJson),
                (bossWeaponId, bossWeaponJson),
                (bossCombatEncId, bossEncJson),
            },
            customMonsters: new[] { (bossMonsterId, bossMonsterJson) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var permBlade = AddOwnedHandItem(run, ov.Repo, ov.Pool, permWeaponId, slot: 3, ETier.Bronze);
        int goldBefore = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int xpBefore = run.PlayerAttributes[EPlayerAttributeType.Experience];

        var bossOpt = InjectChoiceEncounter(run, ov.Pool, bossCombatEncId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(bossOpt.InstanceId) });

        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);
        Assert.Equal(ECombatantId.Opponent, combatOut.Combat!.Winner);

        // 战中开火产生的 PermanentAdd 必须回写到 RunState.Cards 与 CombatDespawnSim
        Assert.True(permBlade.CoreCard!.PermanentAdd.GetValueOrDefault("DamageAmount", 0) >= 20,
            "战中开火带来的 PermanentAdd 应由 SyncPermanentChangesBack 回写");
        Assert.True(permBlade.Attributes.GetValueOrDefault(ECardAttributeType.DamageAmount, 0) >= 80);
        Assert.Contains(combatOut.CombatDespawnSim!.Events, e => e is GameSimEventEffectAuraExecuted);

        // PvE 战败仍按伤血比例结算部分金币与经验并传入 CombatSim
        Assert.NotEmpty(combatOut.Combat.OpponentHealthThresholdsForGold);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Gold] > goldBefore);
        Assert.True(run.PlayerAttributes[EPlayerAttributeType.Experience] > xpBefore);
        Assert.NotEqual(ERunState.Loot, run.State);
    }

    // ── (e) SuspendedStates（含 SuspendedCards）快照序列化往返与 Resume 后退出 LevelUp 精确还原商店 ──

    [Fact]
    public void SuspendedStatesAndCards_SerializerRoundtrip_AndResumeRestoresShopIntact()
    {
        string xpItemId = "55555555-5555-4555-8555-555555555501";
        string shopId = "55555555-5555-4555-8555-555555555502";
        string lvlStepId = "55555555-5555-4555-8555-555555555503";

        string xpItemJson = BuildCustomXpOnSellItemJson(xpItemId, "Resume XP Gem", buyPrice: 2, sellPrice: 1, xpGain: 8);
        string shopJson = BuildFixedShopJson(shopId, "Resume Shop", new[] { CutlassId, RedGumballId, xpItemId });
        string lvlStepJson = BuildGoldStepJson(lvlStepId, "Resume Level Reward", goldGain: 4, xpGain: 0, shouldProgressHour: true);
        string lvl2Json = BuildSingleStepLevelUpJson(2, healthIncrease: 100, stepIds: new[] { lvlStepId });

        using var ov = new OverlayContext(
            new[] { (xpItemId, xpItemJson), (shopId, shopJson), (lvlStepId, lvlStepJson) },
            customLevelUps: new[] { (2, lvl2Json) });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var shopOpt = InjectChoiceEncounter(run, ov.Pool, shopId, ECardType.EventEncounter);
        engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(shopOpt.InstanceId) });

        // 刷新一次商店，使 RerollCost / RerollsRemaining 与初始默认值不同，检验无损往返
        var rerollOut = engine.Handle(new RerollCommand());
        Assert.Null(rerollOut.Error);
        uint expectedRerollCost = run.RerollCost;
        uint expectedRerollsRemaining = run.RerollsRemaining;

        var xpShelfEntry = run.SelectionSet.Select(id => run.Cards[id]).Single(c => c.TemplateId == Guid.Parse(xpItemId));
        var expectedRemainingShelfIds = run.SelectionSet
            .Where(id => id != xpShelfEntry.InstanceId)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        engine.Handle(new SelectItemCommand
        {
            InstanceId = new InstanceId(xpShelfEntry.InstanceId),
            Section = EInventorySection.Hand,
            TargetSockets = new List<EContainerSocketId> { EContainerSocketId.Socket_3 },
        });
        engine.Handle(new SellCardCommand { InstanceId = new InstanceId(xpShelfEntry.InstanceId) });
        // V7-1.5：商店内卖卡只累计 PendingLevelUps，保持商店状态与剩余货架，待退出后结算 LevelUp
        Assert.Equal(ERunState.Encounter, run.State);
        Assert.Equal(1, run.PendingLevelUps);
        Assert.Equal(expectedRemainingShelfIds, run.SelectionSet.OrderBy(x => x, StringComparer.Ordinal).ToList());

        // 1) 校验延迟升级在商店状态下的快照序列化往返与剩余货架恢复
        string deferredJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: engine.StartSelectionDone, pendingLevelUp: run.PendingLevelUp);
        bool deferredOk = RunSnapshotSerializer.TryDeserialize(deferredJson, ov.Repo, out var restoredDeferredRun, out var deferredDto);
        Assert.True(deferredOk);
        Assert.NotNull(restoredDeferredRun);
        Assert.NotNull(deferredDto);
        Assert.Equal(ERunState.Encounter, restoredDeferredRun!.State);
        Assert.Equal(shopId, restoredDeferredRun.CurrentEncounterId);
        Assert.Equal(1, restoredDeferredRun.PendingLevelUps);
        Assert.Equal(expectedRerollCost, restoredDeferredRun.RerollCost);
        Assert.Equal(expectedRerollsRemaining, restoredDeferredRun.RerollsRemaining);
        Assert.Equal(expectedRemainingShelfIds, restoredDeferredRun.SelectionSet.OrderBy(x => x, StringComparer.Ordinal).ToList());

        var savedShelfCards = expectedRemainingShelfIds.Select(id => restoredDeferredRun.Cards[id]).ToList();
        var resumedDeferredEngine = new RunEngine(restoredDeferredRun, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        var exitShopOut = resumedDeferredEngine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitShopOut.Error);
        Assert.Equal(ERunState.LevelUp, restoredDeferredRun.State);
        Assert.False(restoredDeferredRun.PendingLevelUp);

        // 2) 校验 SuspendedStates（含 SuspendedCards）快照序列化往返与退出 LevelUp 精确还原商店现场
        restoredDeferredRun.SuspendedStates.Push(new SuspendedStateSnapshot
        {
            State = ERunState.Encounter,
            CurrentEncounterId = shopId,
            SelectionSet = new List<string>(expectedRemainingShelfIds),
            SelectionRules = RunState.CloneRules(restoredDeferredRun.SelectionRules),
            RerollCost = expectedRerollCost,
            RerollsRemaining = expectedRerollsRemaining,
        });
        foreach (var ce in savedShelfCards)
            restoredDeferredRun.SuspendedStates.Peek().SuspendedCards.Add(ce);

        string json = RunSnapshotSerializer.Serialize(restoredDeferredRun, startSelectionDone: resumedDeferredEngine.StartSelectionDone, pendingLevelUp: restoredDeferredRun.PendingLevelUp);
        bool ok = RunSnapshotSerializer.TryDeserialize(json, ov.Repo, out var restoredRun, out var dto);
        Assert.True(ok);
        Assert.NotNull(restoredRun);
        Assert.NotNull(dto);
        Assert.Equal(ERunState.LevelUp, restoredRun!.State);
        Assert.Single(restoredRun.SuspendedStates);

        var restoredTop = restoredRun.SuspendedStates.Peek();
        Assert.Equal(ERunState.Encounter, restoredTop.State);
        Assert.Equal(shopId, restoredTop.CurrentEncounterId);
        Assert.Equal(expectedRerollCost, restoredTop.RerollCost);
        Assert.Equal(expectedRerollsRemaining, restoredTop.RerollsRemaining);
        Assert.Equal(2, restoredTop.SuspendedCards.Count);
        Assert.Equal(
            expectedRemainingShelfIds,
            restoredTop.SuspendedCards.Select(c => c.InstanceId).OrderBy(x => x, StringComparer.Ordinal).ToList());

        // 校验 BuildResumeEvents 恢复事件流完整性
        var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(restoredRun);
        Assert.Contains(resumeEvents, e => e is GameSimEventStateTransitioned st && st.ToState == ERunState.LevelUp);
        Assert.Contains(resumeEvents, e => e is GameSimEventCardDealt);

        // 用恢复出的 RunState 构建新 RunEngine 并完成 LevelUp 选择，确认商店货架与 Reroll 状态精确还原
        var resumedEngine = new RunEngine(restoredRun, ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        Assert.True(RunSnapshotSerializer.InferStartSelectionDone(restoredRun));
        typeof(RunEngine).GetProperty(nameof(RunEngine.StartSelectionDone))!.SetValue(resumedEngine, true);

        string resumedLvlRewardId = restoredRun.SelectionSet.First();
        var afterLvlOut = resumedEngine.Handle(new SelectItemCommand { InstanceId = new InstanceId(resumedLvlRewardId) });
        Assert.Null(afterLvlOut.Error);
        Assert.Empty(restoredRun.SuspendedStates);
        Assert.Equal(ERunState.Encounter, restoredRun.State);
        Assert.Equal(shopId, restoredRun.CurrentEncounterId);
        Assert.Equal(expectedRerollCost, restoredRun.RerollCost);
        Assert.Equal(expectedRerollsRemaining, restoredRun.RerollsRemaining);
        Assert.Equal(
            expectedRemainingShelfIds,
            restoredRun.SelectionSet.OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.All(expectedRemainingShelfIds, id => Assert.True(restoredRun.Cards.ContainsKey(id)));
        Assert.Contains(afterLvlOut.Events, e => e is GameSimEventInterruptStateExited ixe && ixe.ExitedState == ERunState.LevelUp && ixe.IsInterrupt);
    }

    // ── 卖卡 OnSell 先触发后摘除 + 跨小时/跨天临时修饰清理与收入发放 ──

    [Fact]
    public void SellCard_FiresOnSellBeforeRemoval_AndAdvanceHourClearsHourDayTempModifiers()
    {
        string goldSellItemId = "66666666-6666-4666-8666-666666666601";
        string shopId = "66666666-6666-4666-8666-666666666602";

        string sellGoldJson = BuildCustomGoldOnSellItemJson(goldSellItemId, "Golden Idol", buyPrice: 2, sellPrice: 2, bonusGoldOnSell: 5);
        string shopJson = BuildSingleChoiceStepShopJson(shopId, "Fast Exit Event");

        using var ov = new OverlayContext(new[] { (goldSellItemId, sellGoldJson), (shopId, shopJson) });
        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);

        var idol = AddOwnedHandItem(run, ov.Repo, ov.Pool, goldSellItemId, slot: 3, ETier.Bronze);
        var cutlass = AddOwnedHandItem(run, ov.Repo, ov.Pool, CutlassId, slot: 4, ETier.Bronze);
        cutlass.CoreCard!.PermanentAdd["DamageAmount"] = 10;
        cutlass.CoreCard.HourAdd["DamageAmount"] = 7;
        cutlass.CoreCard.DayAdd["DamageAmount"] = 11;
        run.SyncAllEntriesFromCoreCardsAndDiff(ov.Repo.GameMode.StandardPrices, new[] { cutlass });
        Assert.Equal(38, cutlass.Attributes[ECardAttributeType.DamageAmount]);

        int goldBeforeSell = run.PlayerAttributes[EPlayerAttributeType.Gold];
        var sellOut = engine.Handle(new SellCardCommand { InstanceId = new InstanceId(idol.InstanceId) });
        Assert.Null(sellOut.Error);
        // 卖卡基础售价 2 + OnSell 额外金币 5 = +7，且已从 Cards/HandGrid 摘除
        Assert.Equal(goldBeforeSell + 7, run.PlayerAttributes[EPlayerAttributeType.Gold]);
        Assert.False(run.Cards.ContainsKey(idol.InstanceId));
        Assert.DoesNotContain(sellOut.Events, _ => run.HandGrid.Cards.Any(c => c.InstanceId == idol.InstanceId));
        Assert.Contains(sellOut.Events, e => e is GameSimEventCardDisposed d && d.InstanceId == idol.InstanceId);

        // 进入一个单步商店并退出，从 Hour 1 → Hour 2：HourAdd(7) 必须清零，DayAdd(11)+Permanent(10) 保留
        var opt1 = InjectChoiceEncounter(run, ov.Pool, shopId, ECardType.EventEncounter);
        engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt1.InstanceId) });
        var exitHour1 = engine.Handle(new ExitCurrentStateCommand());
        Assert.Null(exitHour1.Error);
        Assert.Equal(2u, run.Hour);
        Assert.False(cutlass.CoreCard.HourAdd.ContainsKey("DamageAmount"));
        Assert.Equal(11, cutlass.CoreCard.DayAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(31, cutlass.Attributes[ECardAttributeType.DamageAmount]);

        // 继续推进到跨天 Day 2 Hour 1：Hour 2~5 走商店退出，Hour 6 必须正常结算 Day 1 PvP 后跨天
        int goldBeforeDayRoll = run.PlayerAttributes[EPlayerAttributeType.Gold];
        int income = run.PlayerAttributes[EPlayerAttributeType.Income];
        RunEngine.Outcome? crossDayOut = null;
        int guard = 0;
        while (run.Day == 1 && guard++ < Math.Max(12, ov.Mode.HoursInADay * 2))
        {
            if (run.State == ERunState.Choice)
            {
                if (run.Hour == (uint)ov.Mode.HoursInADay)
                {
                    var pvpOpt = run.SelectionSet
                        .Select(id => run.Cards.GetValueOrDefault(id))
                        .FirstOrDefault(c => c != null && c.Type == ECardType.PvpEncounter)
                        ?? InjectChoiceEncounter(run, ov.Pool, ShopEngine.Hour6PvpCardId, ECardType.PvpEncounter);
                    crossDayOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(pvpOpt.InstanceId) });
                    Assert.Null(crossDayOut.Error);
                }
                else
                {
                    var opt = InjectChoiceEncounter(run, ov.Pool, shopId, ECardType.EventEncounter);
                    var enterOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(opt.InstanceId) });
                    Assert.Null(enterOut.Error);
                    crossDayOut = engine.Handle(new ExitCurrentStateCommand());
                    Assert.Null(crossDayOut.Error);
                }
            }
            else if (run.State is ERunState.LevelUp or ERunState.Loot && !run.SelectionRules.CanExit && run.SelectionSet.Count > 0)
            {
                var cand = run.Cards[run.SelectionSet[0]];
                crossDayOut = cand.Type switch
                {
                    ECardType.Skill => engine.Handle(new SelectSkillCommand { InstanceId = new InstanceId(cand.InstanceId) }),
                    ECardType.EncounterStep or ECardType.EventEncounter or ECardType.PedestalEncounter or ECardType.CombatEncounter or ECardType.PvpEncounter
                        => engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(cand.InstanceId) }),
                    _ => engine.Handle(new SelectItemCommand { InstanceId = new InstanceId(cand.InstanceId), Section = EInventorySection.Hand }),
                };
                Assert.Null(crossDayOut.Error);
            }
            else
            {
                crossDayOut = engine.Handle(new ExitCurrentStateCommand());
                Assert.Null(crossDayOut.Error);
            }
        }

        Assert.Equal(2u, run.Day);
        Assert.Equal(1u, run.Hour);
        Assert.Equal(1u, run.Victories + run.Defeats);
        Assert.NotNull(crossDayOut);
        Assert.False(cutlass.CoreCard.DayAdd.ContainsKey("DamageAmount"));
        Assert.Equal(10, cutlass.CoreCard.PermanentAdd.GetValueOrDefault("DamageAmount", 0));
        Assert.Equal(20, cutlass.Attributes[ECardAttributeType.DamageAmount]);
        // V7-1.1 起始经验为 3，Hour 5->6 升级已在 PvP 前消化，跨天事件可随 CombatDespawnSim.Events 或 Events 派发
        var crossDayEvents = crossDayOut!.CombatDespawnSim?.Events ?? crossDayOut.Events;
        Assert.Contains(crossDayEvents, e => e is GameSimEventRunDayChanged dc && dc.NewDay == 2);
        if (income > 0)
        {
            Assert.Contains(crossDayEvents, e => e is GameSimEventPlayerIncomeGained inc && inc.Amount == (uint)income);
            Assert.True(run.PlayerAttributes[EPlayerAttributeType.Gold] >= goldBeforeDayRoll + income);
        }
    }

    // ── (e) PvP 镜像与野怪重复模板/重复前缀/重复 InstanceId 唯一键热修回归测试 ──

    [Fact]
    public void PvpGhostPool_ToPlayerState_DuplicateSkillsAndSameSlotItems_GeneratesUniqueInstanceIds()
    {
        const string dupSkillIdA = "0becce00-1111-4111-8111-111111111101";
        const string dupSkillIdB = "0becce00-2222-4222-8222-222222222202";
        const string shortItemId = "ab12";

        using var ov = new OverlayContext(new[]
        {
            (dupSkillIdA, BuildCustomSkillJson(dupSkillIdA, "Dup Skill A", startShield: 10)),
            (dupSkillIdB, BuildCustomSkillJson(dupSkillIdB, "Dup Skill B", startShield: 12)),
            (shortItemId, BuildPermScalingWeaponJson(shortItemId, "Short Id Blade", cooldownMs: 2000, baseDmg: 15, permIncOnFire: 0)),
        });

        var ghost = new PvpGhostPool.GhostBoard
        {
            BoardId = "player_run_dup_test_d2",
            Day = 2,
            Hero = "Vanessa",
            Level = 3,
            HealthMax = 400,
            Prestige = 20,
            Wins = 1,
            Losses = 0,
            UnlockedSlotsMask = 1023,
            IsPlayerGhost = true,
            Items = new List<PvpGhostPool.GhostItem>
            {
                new()
                {
                    Slot = 3,
                    Width = 2,
                    TemplateId = CutlassId,
                    Tier = "Bronze",
                    PermanentAttributes = new Dictionary<string, int> { ["DamageAmount"] = 17 },
                },
                new()
                {
                    Slot = 3,
                    Width = 2,
                    TemplateId = CutlassId,
                    Tier = "Silver",
                    PermanentAttributes = new Dictionary<string, int> { ["DamageAmount"] = 33 },
                },
                new()
                {
                    Slot = 3,
                    Width = 1,
                    TemplateId = shortItemId,
                    Tier = "Bronze",
                },
            },
            Skills = new List<PvpGhostPool.GhostSkill>
            {
                new() { TemplateId = dupSkillIdA, Tier = "Bronze" },
                new() { TemplateId = dupSkillIdA, Tier = "Bronze" },
                new() { TemplateId = dupSkillIdB, Tier = "Bronze" },
            },
        };

        var ps = PvpGhostPool.ToPlayerState(ghost, ov.Repo);

        Assert.Equal(3, ps.Hand.Cards.Count);
        Assert.Equal(3, ps.Skills.Count);

        var itemIds = ps.Hand.Cards.Select(c => c.InstanceId).ToList();
        var skillIds = ps.Skills.Select(s => s.InstanceId).ToList();
        var allIds = itemIds.Concat(skillIds).ToList();

        Assert.All(allIds, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(6, allIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(itemIds, id => id.StartsWith("opp_0_3_", StringComparison.Ordinal));
        Assert.Contains(itemIds, id => id.StartsWith("opp_1_3_", StringComparison.Ordinal));
        Assert.Contains(itemIds, id => id == "opp_2_3_ab12");
        Assert.Equal("oppskl_0_0becce", skillIds[0]);
        Assert.Equal("oppskl_1_0becce", skillIds[1]);
        Assert.Equal("oppskl_2_0becce", skillIds[2]);

        Assert.Contains(ps.Hand.Cards, c => c.Template.Id == CutlassId && Math.Abs(c.GetAttribute("DamageAmount") - 17) < 0.01);
        Assert.Contains(ps.Hand.Cards, c => c.Template.Id == CutlassId && Math.Abs(c.GetAttribute("DamageAmount") - 33) < 0.01);
    }

    [Fact]
    public void RunRealCombatSequence_PvpGhostWithDuplicateSkillsAndSameSlotItems_CompletesWithoutException()
    {
        const string dupSkillIdA = "0becce00-3333-4333-8333-333333333301";
        const string dupSkillIdB = "0becce00-4444-4444-8444-444444444402";

        using var ov = new OverlayContext(new[]
        {
            (dupSkillIdA, BuildCustomSkillJson(dupSkillIdA, "Prefix 0becce Skill A", startShield: 15)),
            (dupSkillIdB, BuildCustomSkillJson(dupSkillIdB, "Prefix 0becce Skill B", startShield: 20)),
        });

        var db = new Persistence(Path.Combine(ov.TempDir, "pvp_hotfix.db"));
        var writerPool = new PvpGhostPool();

        // 构造带 2 个同槽同模板物品 + 2 个同模板技能 + 1 个同 6 位前缀技能的真人镜像并写入 SQLite
        var oppRun = new RunState
        {
            RunId = "run_cf3d8205ee1a",
            GameModeId = ov.Pool.DefaultGameModeId,
            Hero = EHero.Vanessa,
            Day = 1,
            Hour = 6,
            UnlockedSlots = 1023,
        };
        var cutlassTpl = ov.Repo.GetCard(CutlassId)!;
        var skillTplA = ov.Repo.GetCard(dupSkillIdA)!;
        var skillTplB = ov.Repo.GetCard(dupSkillIdB)!;

        oppRun.Cards["opp_src_itm_1"] = new RunState.CardEntry
        {
            InstanceId = "opp_src_itm_1",
            TemplateId = Guid.Parse(CutlassId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = new CardInstance(cutlassTpl, BazaarCore.Models.CardTier.Bronze, null, "opp_src_itm_1"),
        };
        oppRun.Cards["opp_src_itm_2"] = new RunState.CardEntry
        {
            InstanceId = "opp_src_itm_2",
            TemplateId = Guid.Parse(CutlassId),
            Type = ECardType.Item,
            Size = ECardSize.Medium,
            Tier = ETier.Silver,
            Owner = ECombatantId.Player,
            Section = EInventorySection.Hand,
            Socket = EContainerSocketId.Socket_3,
            CoreCard = new CardInstance(cutlassTpl, BazaarCore.Models.CardTier.Silver, null, "opp_src_itm_2"),
        };
        oppRun.Cards["opp_src_skl_1"] = new RunState.CardEntry
        {
            InstanceId = "opp_src_skl_1",
            TemplateId = Guid.Parse(dupSkillIdA),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = new CardInstance(skillTplA, BazaarCore.Models.CardTier.Bronze, null, "opp_src_skl_1"),
        };
        oppRun.Cards["opp_src_skl_2"] = new RunState.CardEntry
        {
            InstanceId = "opp_src_skl_2",
            TemplateId = Guid.Parse(dupSkillIdA),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = new CardInstance(skillTplA, BazaarCore.Models.CardTier.Bronze, null, "opp_src_skl_2"),
        };
        oppRun.Cards["opp_src_skl_3"] = new RunState.CardEntry
        {
            InstanceId = "opp_src_skl_3",
            TemplateId = Guid.Parse(dupSkillIdB),
            Type = ECardType.Skill,
            Size = ECardSize.Medium,
            Tier = ETier.Bronze,
            Owner = ECombatantId.Player,
            CoreCard = new CardInstance(skillTplB, BazaarCore.Models.CardTier.Bronze, null, "opp_src_skl_3"),
        };

        var recorded = writerPool.RecordPlayerGhost(oppRun, db);
        Assert.NotNull(recorded);
        Assert.Equal(2, recorded!.Items.Count);
        Assert.Equal(3, recorded.Skills.Count);

        // 从 SQLite 重新加载进独立 PvP 镜像池，确保 100% 命中该重复技能/物品真人镜像
        var pvpPool = new PvpGhostPool();
        Assert.Equal(1, pvpPool.LoadFromDb(db));

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, pvpPool);
        AddOwnedHandItem(run, ov.Repo, ov.Pool, CutlassId, slot: 3, ETier.Gold);

        var pvpOut = engine.Handle(new CheatCommand { Args = new List<string> { "pvp" } });
        Assert.Null(pvpOut.Error);
        Assert.NotNull(pvpOut.CombatSpawnSim);
        Assert.NotNull(pvpOut.Combat);
        Assert.NotNull(pvpOut.CombatDespawnSim);
        Assert.Equal(ERunState.PVPCombat, pvpOut.CombatSpawnSim!.CurrentState!.StateName);
        Assert.True(pvpOut.Combat!.Frames.Count > 1);

        var spawnedOppIds = pvpOut.CombatSpawnSim.Events
            .OfType<GameSimEventCardSpawned>()
            .Where(e => pvpOut.CombatSpawnSim.Cards.TryGetValue(e.InstanceId, out var c) && c.Placement?.Owner == ECombatantId.Opponent)
            .Select(e => e.InstanceId)
            .ToList();
        Assert.Equal(5, spawnedOppIds.Count);
        Assert.Equal(5, spawnedOppIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, pvpOut.CombatSpawnSim.Cards.Values.Count(c => c.Placement?.Owner == ECombatantId.Opponent));

        var disposedIds = pvpOut.CombatDespawnSim!.Events
            .OfType<GameSimEventCardDisposed>()
            .Select(e => e.InstanceId)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(spawnedOppIds, id => Assert.Contains(id, disposedIds));
    }

    [Fact]
    public void RunRealCombatSequence_MonsterWithDuplicateExplicitInstanceIds_SafelyDeduplicatesAndCompletes()
    {
        const string monWeaponId = "77777777-7777-4777-8777-777777777701";
        const string monSkillId = "77777777-7777-4777-8777-777777777702";
        const string monsterId = "77777777-7777-4777-8777-777777777703";
        const string combatEncId = "77777777-7777-4777-8777-777777777704";

        using var ov = new OverlayContext(
            new[]
            {
                (monWeaponId, BuildPermScalingWeaponJson(monWeaponId, "Dup Monster Blade", cooldownMs: 1500, baseDmg: 25, permIncOnFire: 0)),
                (monSkillId, BuildCustomSkillJson(monSkillId, "Dup Monster Shield Skill", startShield: 10)),
                (combatEncId, BuildCombatEncounterJson(combatEncId, "Dup Id Monster Combat", monsterId, level: 2, rewardGold: 4, rewardXp: 2)),
            },
            customMonsters: new[]
            {
                (monsterId, BuildMonsterWithDuplicateIdsJson(monsterId, "Duplicate Key Chimera", monWeaponId, monSkillId)),
            });

        var (run, engine) = CreateStartedEngine(ov.Pool, ov.Monsters, ov.LevelUps, ov.Mode, ov.Repo, ov.Ghosts);
        AddOwnedHandItem(run, ov.Repo, ov.Pool, CutlassId, slot: 3, ETier.Gold);

        var mobOpt = InjectChoiceEncounter(run, ov.Pool, combatEncId, ECardType.CombatEncounter);
        var combatOut = engine.Handle(new SelectEncounterCommand { InstanceId = new InstanceId(mobOpt.InstanceId) });

        Assert.Null(combatOut.Error);
        Assert.NotNull(combatOut.CombatSpawnSim);
        Assert.NotNull(combatOut.Combat);
        Assert.NotNull(combatOut.CombatDespawnSim);
        Assert.Equal(ERunState.Combat, combatOut.CombatSpawnSim!.CurrentState!.StateName);
        Assert.True(combatOut.Combat!.Frames.Count > 1);

        var spawnedOppIds = combatOut.CombatSpawnSim.Events
            .OfType<GameSimEventCardSpawned>()
            .Where(e => combatOut.CombatSpawnSim.Cards.TryGetValue(e.InstanceId, out var c) && c.Placement?.Owner == ECombatantId.Opponent)
            .Select(e => e.InstanceId)
            .ToList();
        Assert.Equal(4, spawnedOppIds.Count);
        Assert.Equal(4, spawnedOppIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(4, combatOut.CombatSpawnSim.Cards.Values.Count(c => c.Placement?.Owner == ECombatantId.Opponent));
    }

    // ── JSON 测试夹具构造器 ──

    private static string BuildCustomXpOnSellItemJson(string id, string name, int buyPrice, int sellPrice, int xpGain)
        => BuildCustomOnSellItemJson(id, name, buyPrice, sellPrice, "Experience", xpGain);

    private static string BuildCustomGoldOnSellItemJson(string id, string name, int buyPrice, int sellPrice, int bonusGoldOnSell)
        => BuildCustomOnSellItemJson(id, name, buyPrice, sellPrice, "Gold", bonusGoldOnSell);

    private static string BuildCustomOnSellItemJson(string id, string name, int buyPrice, int sellPrice, string playerAttr, int amount)
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

    private static string BuildFixedShopJson(string id, string name, string[] itemIds)
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
                            Filters = new[]
                            {
                                new { @__type = "TSpawnFilterIdList", Ids = itemIds },
                            },
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
                    CanSelectMultiple = true,
                    SelectionIsFree = false,
                    CanExit = true,
                    RerollRules = new
                    {
                        TotalAllowedRerolls = 3,
                        CostIncrease = 1,
                        StartingCost = 2,
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

    private static string BuildSingleChoiceStepShopJson(string id, string name)
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
                    Groups = Array.Empty<object>(),
                    SelectionMethod = "Sequential",
                    Limit = new { @__type = "TFixedValue", Value = 0.0 },
                    Behaviors = (object?)null,
                },
                Rules = new
                {
                    CanSelectMultiple = false,
                    SelectionIsFree = true,
                    CanExit = true,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
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

    private static string BuildStepEventJson(string id, string name, string[] stepIds, bool selectionIsFree = true)
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
                    SelectionIsFree = selectionIsFree,
                    CanExit = false,
                    WillAutoSellOnExit = false,
                    NextEncounterOnExit = (string?)null,
                },
            },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildChainedStepJson(string id, string name, int buyPrice, int goldGain, string nextEncounterId)
    {
        var abilities = new Dictionary<string, object>();
        if (goldGain != 0)
        {
            abilities["0"] = new
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
            };
        }

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
            Attributes = buyPrice > 0 ? new Dictionary<string, int> { ["BuyPrice"] = buyPrice, ["SellPrice"] = 0 } : null,
            NextEncounterOnSelection = new[]
            {
                new
                {
                    Id = nextEncounterId,
                    Weight = 1.0,
                    Prerequisites = (object?)null,
                },
            },
            Abilities = abilities,
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildPermScalingWeaponJson(string id, string name, int cooldownMs, int baseDmg, int permIncOnFire)
    {
        var abilities = new Dictionary<string, object>
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
        };
        if (permIncOnFire > 0)
        {
            abilities["1"] = new
            {
                Id = "1",
                Trigger = new { @__type = "TTriggerOnCardFired" },
                ActiveIn = "HandOnly",
                Action = new
                {
                    @__type = "TActionCardModifyAttribute",
                    AttributeType = "DamageAmount",
                    Operation = "Add",
                    Value = new { @__type = "TFixedValue", Value = (double)permIncOnFire },
                    Duration = (object?)null,
                    TargetCount = (object?)null,
                    Target = new { @__type = "TTargetCardSelf", Conditions = (object?)null },
                },
                Prerequisites = (object?)null,
            };
        }

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
                        ["CooldownMax"] = cooldownMs,
                        ["DamageAmount"] = baseDmg,
                        ["Multicast"] = 1,
                        ["BuyPrice"] = 4,
                        ["SellPrice"] = 2,
                    },
                    AbilityIds = abilities.Keys.ToArray(),
                    AuraIds = Array.Empty<string>(),
                },
            },
            Abilities = abilities,
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildBossMonsterJson(string id, string name, int healthMax, int level, string weaponId)
    {
        return JsonSerializer.Serialize(new
        {
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Player = new
            {
                Attributes = new Dictionary<string, int>
                {
                    ["Prestige"] = 50,
                    ["HealthMax"] = healthMax,
                    ["Level"] = level,
                },
                Hand = new
                {
                    UnlockedSlots = 120,
                    Items = new[]
                    {
                        new
                        {
                            @__type = "TCardInstanceItem",
                            EnchantmentType = (string?)null,
                            SocketId = "Socket_3",
                            TemplateId = weaponId,
                            TemplateVersion = "5.0.0",
                            InstanceId = (string?)null,
                            Tier = "Bronze",
                            Attributes = new { },
                        },
                    },
                },
                Skills = Array.Empty<object>(),
                Stash = new { Items = Array.Empty<object>() },
                SocketEffects = Array.Empty<object>(),
            },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildCombatEncounterJson(
        string id,
        string name,
        string monsterTemplateId,
        int level,
        int rewardGold,
        int rewardXp)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardEncounterCombat",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "CombatEncounter",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = Array.Empty<string>(),
            SpawningEligibility = "Never",
            CombatantType = new
            {
                @__type = "TCombatantMonster",
                MonsterTemplateId = monsterTemplateId,
                Level = level,
            },
            RewardCombatGold = rewardGold,
            RewardCombatXp = rewardXp,
            RewardVictory = new { GoldReward = 2, ExperienceReward = 1 },
            RewardDefeat = new { GoldReward = 0, ExperienceReward = 0 },
            Abilities = new { },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildCustomSkillJson(string id, string name, int startShield)
    {
        return JsonSerializer.Serialize(new
        {
            @__type = "TCardSkill",
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Type = "Skill",
            StartingTier = "Bronze",
            Size = "Medium",
            Heroes = new[] { "Common" },
            Tags = Array.Empty<string>(),
            HiddenTags = new[] { "Shield" },
            SpawningEligibility = "Never",
            Tiers = new Dictionary<string, object>
            {
                ["Bronze"] = new
                {
                    Attributes = new Dictionary<string, int>
                    {
                        ["ShieldApplyAmount"] = startShield,
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
                    Trigger = new { @__type = "TTriggerOnFightStarted" },
                    ActiveIn = "HandOnly",
                    Action = new
                    {
                        @__type = "TActionPlayerShieldApply",
                        ReferenceValue = new { @__type = "TFixedValue", Value = (double)startShield },
                        Target = new { @__type = "TTargetPlayerRelative", TargetMode = "Self", Conditions = (object?)null },
                    },
                    Prerequisites = (object?)null,
                },
            },
            Auras = new { },
        }).Replace("\"__type\"", "\"$type\"");
    }

    private static string BuildMonsterWithDuplicateIdsJson(string id, string name, string weaponId, string skillId)
    {
        return JsonSerializer.Serialize(new
        {
            Id = id,
            Version = "5.0.0",
            InternalName = name,
            Player = new
            {
                Attributes = new Dictionary<string, int>
                {
                    ["Prestige"] = 50,
                    ["HealthMax"] = 220,
                    ["Level"] = 2,
                },
                Hand = new
                {
                    UnlockedSlots = 1023,
                    Items = new[]
                    {
                        new
                        {
                            @__type = "TCardInstanceItem",
                            EnchantmentType = (string?)null,
                            SocketId = "Socket_3",
                            TemplateId = weaponId,
                            TemplateVersion = "5.0.0",
                            InstanceId = "dup_mon_itm",
                            Tier = "Bronze",
                            Attributes = new { },
                        },
                        new
                        {
                            @__type = "TCardInstanceItem",
                            EnchantmentType = (string?)null,
                            SocketId = "Socket_4",
                            TemplateId = weaponId,
                            TemplateVersion = "5.0.0",
                            InstanceId = "dup_mon_itm",
                            Tier = "Bronze",
                            Attributes = new { },
                        },
                    },
                },
                Skills = new[]
                {
                    new
                    {
                        @__type = "TCardInstanceSkill",
                        TemplateId = skillId,
                        TemplateVersion = "5.0.0",
                        InstanceId = "dup_mon_skl",
                        Tier = "Bronze",
                        Attributes = new { },
                    },
                    new
                    {
                        @__type = "TCardInstanceSkill",
                        TemplateId = skillId,
                        TemplateVersion = "5.0.0",
                        InstanceId = "dup_mon_skl",
                        Tier = "Bronze",
                        Attributes = new { },
                    },
                },
                Stash = new { Items = Array.Empty<object>() },
                SocketEffects = Array.Empty<object>(),
            },
        }).Replace("\"__type\"", "\"$type\"");
    }
}

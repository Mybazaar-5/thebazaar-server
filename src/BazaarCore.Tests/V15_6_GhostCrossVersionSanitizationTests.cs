using System.Text.Json;
using BazaarCore.Data;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Storage;
using Microsoft.Data.Sqlite;

namespace BazaarCore.Tests;

/// <summary>
/// 模块 A（v15.6）跨版本读时清洗、平铺任务顺序统一、空指纹保守兼容与 Migration #7 综合单元测试。
/// 覆盖方案 8.2 模块 A 全部验收条目：
/// - Blank Slate / Waystones 任务保留且零双倍叠算；
/// - 旧版 Sword Cane / Coral 不倒扣 + 无法反推的孤立输出属性被守卫丢弃；
/// - 空指纹 "" 同版本分层无损还原且绝不回写数据库；
/// - old_hash 分层跨版本精准清洗 + CooldownMax 减 CD 豁免保留；
/// - Source="custom" / "sandbox" 负值完全豁免；
/// - Persistence v6 -> v7 幂等迁移、旧空值行读写与单一权威列（不写进 payload JSON）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class V15_6_GhostCrossVersionSanitizationTests
{
    private static readonly Lazy<GameDataRepository> Repo = new(() => TestInfrastructure.SharedGameDataFixture.Shared.Repo);

    private static CardTemplate FindItemByName(GameDataRepository repo, string internalName)
        => repo.CardsById.Values.First(c =>
            c.Type == "Item" && string.Equals(c.InternalName, internalName, StringComparison.OrdinalIgnoreCase));

    private static string CreateTempDbPath(string tag)
    {
        string dir = Path.Combine(Path.GetTempPath(), "bazaar_v15_6_ghost_data_tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{tag}_{Guid.NewGuid():N}.db");
    }

    private static void TryDeleteTempDb(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(dbPath)) File.Delete(dbPath); } catch { }
        try { if (File.Exists(dbPath + "-wal")) File.Delete(dbPath + "-wal"); } catch { }
        try { if (File.Exists(dbPath + "-shm")) File.Delete(dbPath + "-shm"); } catch { }
    }

    [Fact]
    public void BlankSlate_WithCompletedQuests_PreservesQuestRewards_WithoutDoubleCounting()
    {
        var repo = Repo.Value;
        var blankSlate = FindItemByName(repo, "Blank Slate");

        // Gold 阶 Blank Slate 的 Quest_1..Quest_5 奖励属性为 Poison=18, Regen=18, Burn=18, Slow=1000, Freeze=1000
        var board = new PvpGhostPool.GhostBoard
        {
            BoardId = "test_blank_slate_anchor",
            Day = 12,
            Hero = "Mak",
            Level = 12,
            HealthMax = 4000,
            Source = "real_compass_anchor",
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    Slot = 0,
                    Width = 2,
                    TemplateId = blankSlate.Id,
                    InternalName = blankSlate.InternalName,
                    Tier = "Gold",
                    CompletedQuests = ["Quest_1", "Quest_2", "Quest_3", "Quest_4", "Quest_5"],
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        ["BurnApplyAmount"] = 24,
                        ["PoisonApplyAmount"] = 20,
                        ["RegenApplyAmount"] = 18,
                        ["Multicast"] = 1,
                    },
                },
            ],
        };

        var collector = new PvpGhostPool.GhostSanitizationAuditCollector();
        var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "", auditCollector: collector);
        var card = Assert.Single(ps.Hand.Cards);

        // 全部 5 个任务完成态保留，且不可重复任务进度已补满 Target
        for (int i = 1; i <= 5; i++)
        {
            string qk = $"Quest_{i}";
            Assert.Contains(qk, card.CompletedQuests);
            Assert.True(card.QuestProgress.GetValueOrDefault(qk, 0) >= 1, $"{qk} 进度应补满 Target");
        }

        // 任务奖励能力已激活，且平铺终值不会与任务奖励双倍叠算
        Assert.Equal(18.0, card.BaseAttributes["BurnApplyAmount"], precision: 3);
        Assert.Equal(6.0, card.PermanentAdd["BurnApplyAmount"], precision: 3);
        Assert.Equal(24.0, card.GetAttribute("BurnApplyAmount"), precision: 3);

        Assert.Equal(18.0, card.BaseAttributes["PoisonApplyAmount"], precision: 3);
        Assert.Equal(2.0, card.PermanentAdd["PoisonApplyAmount"], precision: 3);
        Assert.Equal(20.0, card.GetAttribute("PoisonApplyAmount"), precision: 3);

        Assert.Equal(18.0, card.BaseAttributes["RegenApplyAmount"], precision: 3);
        Assert.False(card.PermanentAdd.ContainsKey("RegenApplyAmount"));
        Assert.Equal(18.0, card.GetAttribute("RegenApplyAmount"), precision: 3);

        // 冗余 Multicast=1 已被清洗
        Assert.False(card.PermanentAdd.ContainsKey("Multicast"));
        Assert.True(collector.BuildSummary().CountsByReason.GetValueOrDefault("redundant_multicast") >= 1);
    }

    [Fact]
    public void Waystones_WithInferredQuests_ActivatesAllThreeRewards_WithoutDoubleCounting()
    {
        var repo = Repo.Value;
        var waystones = FindItemByName(repo, "Waystones");

        // 模拟 Waystones 经反推补全 Quest_1/2/3 后携带 Damage=66, Heal=110, Shield=60
        var board = new PvpGhostPool.GhostBoard
        {
            BoardId = "test_waystones_q123",
            Day = 10,
            Hero = "Karnok",
            Level = 10,
            HealthMax = 3500,
            Source = "real_compass_anchor",
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    Slot = 0,
                    Width = 2,
                    TemplateId = waystones.Id,
                    InternalName = waystones.InternalName,
                    Tier = "Gold",
                    CompletedQuests = ["Quest_1", "Quest_2", "Quest_3"],
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        ["DamageAmount"] = 66,
                        ["HealAmount"] = 110,
                        ["ShieldApplyAmount"] = 60,
                        ["Multicast"] = 1,
                    },
                },
            ],
        };

        var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "");
        var card = Assert.Single(ps.Hand.Cards);

        // q1/q2/q3 三个奖励能力均进入 ActiveAbilities
        var rewardAbilityIds = waystones.Quests!
            .SelectMany(g => g.Entries)
            .Where(e => e.AttributeType is "Quest_1" or "Quest_2" or "Quest_3")
            .SelectMany(e => e.Reward?.Abilities?.Keys ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var activeIds = card.ActiveAbilities.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(rewardAbilityIds);
        foreach (string rid in rewardAbilityIds)
            Assert.Contains(rid, activeIds);

        // 三个任务奖励基准各为 50，平铺差值分别为 +16/+60/+10，绝不双倍叠算
        Assert.Equal(66.0, card.GetAttribute("DamageAmount"), precision: 3);
        Assert.Equal(110.0, card.GetAttribute("HealAmount"), precision: 3);
        Assert.Equal(60.0, card.GetAttribute("ShieldApplyAmount"), precision: 3);
        Assert.Equal(16.0, card.PermanentAdd["DamageAmount"], precision: 3);
        Assert.Equal(60.0, card.PermanentAdd["HealAmount"], precision: 3);
        Assert.Equal(10.0, card.PermanentAdd["ShieldApplyAmount"], precision: 3);
        Assert.False(card.PermanentAdd.ContainsKey("Multicast"));
    }

    [Fact]
    public void LegacyAnchor_OldSwordCane_Coral_AndUninferableOrphanOutput_AreSanitized()
    {
        var repo = Repo.Value;
        var swordCane = FindItemByName(repo, "Sword Cane");
        var coral = FindItemByName(repo, "Coral");
        var katana = FindItemByName(repo, "Katana");

        var board = new PvpGhostPool.GhostBoard
        {
            BoardId = "test_legacy_nerf_and_orphan",
            Day = 12,
            Hero = "Vanessa",
            Level = 12,
            HealthMax = 4200,
            Source = "real_compass_anchor",
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    Slot = 0,
                    Width = 2,
                    TemplateId = swordCane.Id,
                    Tier = "Gold",
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        // 旧版低数值：现版 Gold 基础为 80/6/6/6
                        ["DamageAmount"] = 30,
                        ["RegenApplyAmount"] = 3,
                        ["BurnApplyAmount"] = 3,
                        ["PoisonApplyAmount"] = 3,
                        ["Multicast"] = 1,
                    },
                },
                new PvpGhostPool.GhostItem
                {
                    Slot = 2,
                    Width = 1,
                    TemplateId = coral.Id,
                    Tier = "Gold",
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        // 旧版 HealAmount=0 vs 现版 30
                        ["HealAmount"] = 0,
                        ["Lifesteal"] = 0,
                    },
                },
                new PvpGhostPool.GhostItem
                {
                    Slot = 3,
                    Width = 2,
                    TemplateId = katana.Id,
                    Tier = "Gold",
                    PermanentAttributes = new Dictionary<string, int>
                    {
                        // 无法反推的孤立输出属性（Katana 无 Poison/Burn/Quest）
                        ["PoisonApplyAmount"] = 99,
                        ["BurnApplyAmount"] = 45,
                        // 合法正向成长与减 CD 必须保留
                        ["DamageAmount"] = 95,
                        ["CooldownMax"] = 1500,
                    },
                },
            ],
        };

        var collector = new PvpGhostPool.GhostSanitizationAuditCollector();
        var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "", auditCollector: collector);

        var caneCard = ps.Hand.Cards.First(c => c.Template.Id == swordCane.Id);
        Assert.Equal(80.0, caneCard.GetAttribute("DamageAmount"), precision: 3);
        Assert.Equal(6.0, caneCard.GetAttribute("RegenApplyAmount"), precision: 3);
        Assert.Equal(6.0, caneCard.GetAttribute("BurnApplyAmount"), precision: 3);
        Assert.Equal(6.0, caneCard.GetAttribute("PoisonApplyAmount"), precision: 3);
        Assert.False(caneCard.PermanentAdd.ContainsKey("DamageAmount"));
        Assert.False(caneCard.PermanentAdd.ContainsKey("Multicast"));

        var coralCard = ps.Hand.Cards.First(c => c.Template.Id == coral.Id);
        Assert.Equal(30.0, coralCard.GetAttribute("HealAmount"), precision: 3);
        Assert.False(coralCard.PermanentAdd.ContainsKey("HealAmount"));
        Assert.False(coralCard.PermanentAdd.ContainsKey("Lifesteal"));

        var katanaCard = ps.Hand.Cards.First(c => c.Template.Id == katana.Id);
        Assert.False(katanaCard.PermanentAdd.ContainsKey("PoisonApplyAmount"));
        Assert.False(katanaCard.PermanentAdd.ContainsKey("BurnApplyAmount"));
        Assert.Equal(95.0, katanaCard.GetAttribute("DamageAmount"), precision: 3);
        Assert.Equal(1500.0, katanaCard.GetAttribute("CooldownMax"), precision: 3);

        var summary = collector.BuildSummary();
        Assert.Equal(5, summary.CountsByReason.GetValueOrDefault("negative_output_delta"));
        Assert.Equal(2, summary.CountsByReason.GetValueOrDefault("orphan_output_attribute"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("redundant_multicast"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("non_positive_lifesteal"));
    }

    [Fact]
    public void LegacyEmptyHash_SameVersionLayeredPlayerGhost_IsLosslessAndNeverRewritesDatabase()
    {
        var repo = Repo.Value;
        var katana = FindItemByName(repo, "Katana");
        string dbPath = CreateTempDbPath("empty_hash_no_writeback");
        string prevActiveHash = PvpGhostPool.ActiveGameDataHash;
        try
        {
            using var db = new Persistence(dbPath);
            // 构造 Migration #7 前存量行：GameDataHash = ""，合法同版本分层正成长 + 减 CD
            string boardJson = JsonSerializer.Serialize(new
            {
                BoardId = "player_legacy_empty_hash_d5",
                PowerBucket = "Strong",
                EstimatedWinRate = 0.72,
                IsCustomRule = false,
                PoolPartition = "standard",
                Items = new[]
                {
                    new PvpGhostPool.GhostItem
                    {
                        Slot = 0,
                        Width = 2,
                        TemplateId = katana.Id,
                        InternalName = katana.InternalName,
                        Tier = "Gold",
                        Enchantment = "Fiery",
                        PermanentAttributes = new Dictionary<string, int>
                        {
                            ["DamageAmount"] = 120,
                            ["CooldownMax"] = 1600,
                        },
                        PermanentAdd = new Dictionary<string, double>
                        {
                            ["DamageAmount"] = 20,
                            ["CooldownMax"] = -400,
                        },
                        PermanentMult = new Dictionary<string, double>
                        {
                            ["DamageAmount"] = 1.5,
                        },
                    },
                },
                Skills = Array.Empty<PvpGhostPool.GhostSkill>(),
            });

            long insertedId = db.InsertPvpGhost(new Persistence.PvpGhostRow(
                Id: 0,
                Day: 5,
                Hero: "Vanessa",
                Wins: 4,
                Losses: 0,
                HealthMax: 1200,
                Prestige: 20,
                Level: 5,
                UnlockedSlots: 1023,
                BoardJson: boardJson,
                CreatedAt: DateTime.UtcNow,
                IsCustomRule: false,
                ModeId: "standard",
                PoolPartition: "standard",
                GameDataHash: ""));
            Assert.True(insertedId > 0);

            var pool = new PvpGhostPool();
            pool.SetCurrentGameDataHash("0123456789abcdef");
            Assert.Equal(1, pool.LoadFromDb(db, includeCustomRule: false));

            var loadedRow = Assert.Single(db.LoadPvpGhosts(10));
            Assert.Equal("", loadedRow.GameDataHash);
            string rawJsonBefore = loadedRow.BoardJson;

            var matched = pool.PickOpponent(5, 4, 0, new Random(42), repo);
            Assert.Equal("player_legacy_empty_hash_d5", matched.BoardId);
            Assert.Equal("", matched.GameDataHash);

            // 对比清洗后（currentHash 非空）与同版本不清洗（currentHash == ""）战斗属性 100% 一致
            var psSanitized = PvpGhostPool.ToPlayerState(matched, repo, currentGameDataHash: "0123456789abcdef");
            var psRaw = PvpGhostPool.ToPlayerState(matched, repo, currentGameDataHash: "");

            var cardSan = Assert.Single(psSanitized.Hand.Cards);
            var cardRaw = Assert.Single(psRaw.Hand.Cards);
            Assert.Equal(cardRaw.GetAttribute("DamageAmount"), cardSan.GetAttribute("DamageAmount"), precision: 3);
            Assert.Equal(cardRaw.GetAttribute("CooldownMax"), cardSan.GetAttribute("CooldownMax"), precision: 3);
            Assert.Equal(cardRaw.Enchantment, cardSan.Enchantment);

            // 强断言：数据库原始 JSON 与空指纹列绝不被回写改动
            var rowAfter = Assert.Single(db.LoadPvpGhosts(10));
            Assert.Equal(rawJsonBefore, rowAfter.BoardJson);
            Assert.Equal("", rowAfter.GameDataHash);
        }
        finally
        {
            new PvpGhostPool().SetCurrentGameDataHash(prevActiveHash);
            TryDeleteTempDb(dbPath);
        }
    }

    [Fact]
    public void CrossVersionLayeredPlayerGhost_SanitizesInvalidFields_PreservesCooldownReduction()
    {
        var repo = Repo.Value;
        var katana = FindItemByName(repo, "Katana");
        var coral = FindItemByName(repo, "Coral");

        var board = new PvpGhostPool.GhostBoard
        {
            BoardId = "player_old_hash_layered",
            Day = 8,
            Hero = "Vanessa",
            Level = 8,
            HealthMax = 2400,
            IsPlayerGhost = true,
            GameDataHash = "old_hash_0000001",
            Items =
            [
                new PvpGhostPool.GhostItem
                {
                    Slot = 0,
                    Width = 2,
                    TemplateId = katana.Id,
                    Tier = "Gold", // base DamageAmount=60, CooldownMax=2000
                    Enchantment = "LegacyDeletedEnchantment_XYZ",
                    CompletedQuests = ["Quest_Removed_99"],
                    QuestProgress = new Dictionary<string, double> { ["Quest_Removed_99"] = 5 },
                    PermanentAdd = new Dictionary<string, double>
                    {
                        ["LegacyRemovedAttr_XYZ"] = 12,
                        ["PoisonApplyAmount"] = 25,   // Katana 无毒属性 -> 丢弃
                        ["DamageAmount"] = -35,       // 拉低至 25 < 60 -> 丢弃
                        ["CooldownMax"] = -600,       // 减 CD 明确豁免 -> 保留
                    },
                    PermanentMult = new Dictionary<string, double>
                    {
                        ["PoisonApplyAmount"] = 2.0,  // 孤立输出属性倍率 -> 丢弃
                    },
                },
                new PvpGhostPool.GhostItem
                {
                    Slot = 2,
                    Width = 1,
                    TemplateId = coral.Id,
                    Tier = "Gold", // base HealAmount=30
                    PermanentAdd = new Dictionary<string, double>
                    {
                        ["HealAmount"] = 15,          // 正向成长 -> 保留
                    },
                    PermanentMult = new Dictionary<string, double>
                    {
                        ["HealAmount"] = 0.5,         // (30+15)*0.5=22.5 < 30 -> 丢弃低倍率
                    },
                },
            ],
        };

        var collector = new PvpGhostPool.GhostSanitizationAuditCollector();
        var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "new_hash_0000002", auditCollector: collector);

        var katanaCard = ps.Hand.Cards.First(c => c.Template.Id == katana.Id);
        Assert.Null(katanaCard.Enchantment);
        Assert.Empty(katanaCard.CompletedQuests);
        Assert.Empty(katanaCard.QuestProgress);
        Assert.False(katanaCard.PermanentAdd.ContainsKey("LegacyRemovedAttr_XYZ"));
        Assert.False(katanaCard.PermanentAdd.ContainsKey("PoisonApplyAmount"));
        Assert.False(katanaCard.PermanentMult.ContainsKey("PoisonApplyAmount"));
        Assert.False(katanaCard.PermanentAdd.ContainsKey("DamageAmount"));
        Assert.Equal(60.0, katanaCard.GetAttribute("DamageAmount"), precision: 3);
        Assert.Equal(-600.0, katanaCard.PermanentAdd["CooldownMax"], precision: 3);
        Assert.Equal(1400.0, katanaCard.GetAttribute("CooldownMax"), precision: 3);

        var coralCard = ps.Hand.Cards.First(c => c.Template.Id == coral.Id);
        Assert.Equal(15.0, coralCard.PermanentAdd["HealAmount"], precision: 3);
        Assert.False(coralCard.PermanentMult.ContainsKey("HealAmount"));
        Assert.Equal(45.0, coralCard.GetAttribute("HealAmount"), precision: 3);

        var summary = collector.BuildSummary();
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("invalid_enchantment"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("invalid_completed_quest"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("invalid_quest_progress"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("invalid_attribute_key"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("orphan_output_attribute"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("negative_output_delta"));
        Assert.Equal(1, summary.CountsByReason.GetValueOrDefault("negative_output_mult"));
    }

    [Fact]
    public void CustomAndSandboxBoards_AreStrictlyExempt_FromCrossVersionSanitization()
    {
        var repo = Repo.Value;
        var katana = FindItemByName(repo, "Katana");

        foreach (string exemptSource in new[] { "custom", "sandbox" })
        {
            var board = new PvpGhostPool.GhostBoard
            {
                BoardId = $"exempt_{exemptSource}_board",
                Day = 8,
                Hero = "Vanessa",
                Level = 8,
                HealthMax = 2500,
                Source = exemptSource,
                IsPlayerGhost = true,
                GameDataHash = "old_hash_should_be_ignored",
                Items =
                [
                    new PvpGhostPool.GhostItem
                    {
                        Slot = 0,
                        Width = 2,
                        TemplateId = katana.Id,
                        Tier = "Diamond", // base DamageAmount=120, CooldownMax=2000
                        PermanentAdd = new Dictionary<string, double>
                        {
                            ["DamageAmount"] = -50,
                            ["CooldownMax"] = -2000,
                        },
                    },
                ],
            };

            var collector = new PvpGhostPool.GhostSanitizationAuditCollector();
            var ps = PvpGhostPool.ToPlayerState(board, repo, currentGameDataHash: "new_hash_99999999", auditCollector: collector);
            var card = Assert.Single(ps.Hand.Cards);

            Assert.Equal(-50.0, card.PermanentAdd["DamageAmount"], precision: 3);
            Assert.Equal(-2000.0, card.PermanentAdd["CooldownMax"], precision: 3);
            Assert.Equal(70.0, card.GetAttribute("DamageAmount"), precision: 3);
            Assert.Equal(0, collector.BuildSummary().TotalCleanedEntries);
        }
    }

    [Fact]
    public void Persistence_MigrationV6ToV7_IsIdempotent_And_StoresGameDataHashInColumnOnly()
    {
        var repo = Repo.Value;
        var katana = FindItemByName(repo, "Katana");
        string dbPath = CreateTempDbPath("migration_v6_to_v7");
        string prevActiveHash = PvpGhostPool.ActiveGameDataHash;
        try
        {
            // 1. 构造纯 v6 老库（pvp_ghosts 不含 game_data_hash 列，schema_migrations 停在 6，且已有 1 条存量行）
            using (var rawConn = new SqliteConnection($"Data Source={dbPath};Pooling=False"))
            {
                rawConn.Open();
                using var cmd = rawConn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_migrations (
                        version INTEGER PRIMARY KEY,
                        name TEXT NOT NULL,
                        applied_at TEXT NOT NULL
                    );
                    INSERT INTO schema_migrations (version, name, applied_at) VALUES
                        (1, 'legacy_core_columns_and_terminal_backfill', '2026-01-01T00:00:00Z'),
                        (2, 'wave3a_incremental_columns_for_legacy_tables', '2026-01-01T00:00:00Z'),
                        (3, 'wave3a_multiplayer_social_replay_leaderboard_tables', '2026-01-01T00:00:00Z'),
                        (4, 'wave4a_account_challenges_and_season_progress', '2026-01-01T00:00:00Z'),
                        (5, 'wave2b_replay_visual_bindings_json', '2026-01-01T00:00:00Z'),
                        (6, 'v6_seed_lineup_blueprint', '2026-01-01T00:00:00Z');

                    CREATE TABLE pvp_ghosts (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        day INTEGER NOT NULL,
                        hero TEXT NOT NULL,
                        wins INTEGER NOT NULL,
                        losses INTEGER NOT NULL,
                        health_max INTEGER NOT NULL,
                        prestige INTEGER NOT NULL,
                        level INTEGER NOT NULL,
                        unlocked_slots INTEGER NOT NULL,
                        board_json TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        is_custom_rule INTEGER NOT NULL DEFAULT 0,
                        mode_id TEXT NOT NULL DEFAULT 'standard',
                        pool_partition TEXT NOT NULL DEFAULT 'standard'
                    );
                    INSERT INTO pvp_ghosts
                        (day, hero, wins, losses, health_max, prestige, level, unlocked_slots, board_json, created_at, is_custom_rule, mode_id, pool_partition)
                    VALUES
                        (3, 'Vanessa', 2, 0, 750, 20, 3, 510, '{"BoardId":"legacy_v6_row","Items":[],"Skills":[]}', '2026-01-01T00:00:00Z', 0, 'standard', 'standard');
                    """;
                cmd.ExecuteNonQuery();
            }

            // 2. 首次打开 Persistence：触发 Migration #7
            using (var db1 = new Persistence(dbPath))
            {
                var migrations = db1.GetAppliedMigrations();
                Assert.Contains(migrations, m => m.Version == 7 && m.Name == "v7_pvp_ghosts_gamedata_hash");

                var legacyRows = db1.LoadPvpGhosts(10);
                var v6Row = Assert.Single(legacyRows);
                Assert.Equal("", v6Row.GameDataHash);
            }

            // 3. 第二次打开 Persistence：验证严格幂等，且可写入与读取新指纹行
            using (var db2 = new Persistence(dbPath))
            {
                var migrations = db2.GetAppliedMigrations();
                Assert.Single(migrations, m => m.Version == 7);

                var pool = new PvpGhostPool();
                pool.SetCurrentGameDataHash("a1b2c3d4e5f60708");

                var run = new RunState
                {
                    RunId = "v7_record_run",
                    GameModeId = TestInfrastructure.SharedGameDataFixture.Shared.Pool.DefaultGameModeId,
                    Hero = EHero.Vanessa,
                    Day = 4,
                    Hour = 6,
                };
                run.SetBasePlayerAttribute(EPlayerAttributeType.HealthMax, 950);
                run.SetBasePlayerAttribute(EPlayerAttributeType.Health, 950);
                run.SetBasePlayerAttribute(EPlayerAttributeType.Level, 4);
                var coreCard = new CardInstance(katana, CardTier.Silver, null, "itm_v7_1") { StartSlot = 0 };
                run.Cards[coreCard.InstanceId] = new RunState.CardEntry
                {
                    InstanceId = coreCard.InstanceId,
                    TemplateId = Guid.Parse(katana.Id),
                    Type = ECardType.Item,
                    Size = ECardSize.Medium,
                    Tier = ETier.Silver,
                    Owner = ECombatantId.Player,
                    Section = EInventorySection.Hand,
                    Socket = EContainerSocketId.Socket_0,
                    CoreCard = coreCard,
                };

                var recorded = pool.RecordPlayerGhost(run, db2);
                Assert.NotNull(recorded);
                Assert.Equal("a1b2c3d4e5f60708", recorded!.GameDataHash);

                var allRows = db2.LoadPvpGhosts(20);
                Assert.Equal(2, allRows.Count);
                var newRow = Assert.Single(allRows, r => r.Day == 4);
                Assert.Equal("a1b2c3d4e5f60708", newRow.GameDataHash);

                // 单一权威列强断言：GameDataHash 绝不写入 board_json payload
                Assert.DoesNotContain("gameDataHash", newRow.BoardJson, StringComparison.OrdinalIgnoreCase);

                // 重载到新池后内存 GhostBoard.GameDataHash 精准还原
                var reloaded = new PvpGhostPool();
                reloaded.SetCurrentGameDataHash("a1b2c3d4e5f60708");
                reloaded.LoadFromDb(db2, includeCustomRule: false);
                var picked = reloaded.PickOpponent(4, 0, 0, new Random(1), repo);
                Assert.Equal("a1b2c3d4e5f60708", picked.GameDataHash);
            }
        }
        finally
        {
            new PvpGhostPool().SetCurrentGameDataHash(prevActiveHash);
            TryDeleteTempDb(dbPath);
        }
    }
}

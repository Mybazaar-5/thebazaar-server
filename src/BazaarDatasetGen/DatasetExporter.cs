using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BazaarCore.Dataset;
using Microsoft.Data.Sqlite;

namespace BazaarDatasetGen;

/// <summary>
/// 多格式数据集导出器：
/// 将采样结果同步写入 runs_5000.jsonl、ghost_pool_balanced.jsonl、by_day/day_*.json 以及 SQLite bazaar_pvp_dataset.db。
/// </summary>
public sealed class DatasetExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IBoardValidator _validator;

    public DatasetExporter(IBoardValidator validator)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
    }

    /// <summary>
    /// 导出全部 4 类结构化数据产物（JSONL、分天 JSON 与 SQLite 数据库）。
    /// </summary>
    public void Export(SamplingResult sampling, DatasetGenOptions options, Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(options.OutputDir);

        string runsPath = Path.Combine(options.OutputDir, "runs_5000.jsonl");
        string ghostPoolPath = Path.Combine(options.OutputDir, "ghost_pool_balanced.jsonl");
        string naturalPoolPath = Path.Combine(options.OutputDir, "ghost_pool_natural.jsonl");
        string byDayDir = Path.Combine(options.OutputDir, "by_day");
        string dbPath = Path.Combine(options.OutputDir, "bazaar_pvp_dataset.db");

        // 1. 导出 runs_5000.jsonl（v2 扩容为 12,000 局完整轨迹）
        WriteJsonLines(runsPath, sampling.SelectedRuns);

        // 2. 导出 ghost_pool_balanced.jsonl（15×8×4 全均衡单日镜像池）
        WriteJsonLines(ghostPoolPath, sampling.BalancedGhostPool);

        // 3. 导出 ghost_pool_natural.jsonl（真实天梯淘汰漏斗单日池）
        WriteJsonLines(naturalPoolPath, sampling.NaturalGhostPool);

        // 4. 导出 by_day/day_01.json ~ day_14.json & day_15_plus.json
        Directory.CreateDirectory(byDayDir);
        foreach (int dayBucket in DayBucketHelper.AllDayBuckets)
        {
            string dayFile = Path.Combine(byDayDir, DayBucketHelper.GetDayFileName(dayBucket));
            var list = sampling.BalancedPoolByDayBucket.TryGetValue(dayBucket, out var bucketList)
                ? bucketList
                : new List<PvpBoardSnapshot>();
            string json = JsonSerializer.Serialize(list, PvpDatasetJson.PvpJsonOptions);
            File.WriteAllText(dayFile, json, Utf8NoBom);
        }

        // 4. 导出 bazaar_pvp_dataset.db（写入前清理旧文件，事务高速插入并标记均衡池属性）
        ExportSqlite(dbPath, sampling);

        sw.Stop();
        log?.Invoke($"[导出] 完成多格式文件写入至 {options.OutputDir}（耗时 {sw.ElapsedMilliseconds} ms）");
    }

    private static void WriteJsonLines<T>(string path, IEnumerable<T> items)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
        using var writer = new StreamWriter(fs, Utf8NoBom, 65536);
        foreach (var item in items)
        {
            string line = JsonSerializer.Serialize(item, PvpDatasetJson.PvpJsonOptions);
            writer.WriteLine(line);
        }
    }

    /// <summary>
    /// 构建并初始化 SQLite 四张表与四条核心索引（与 PVP_DATASET_PLAN.md 第四节完全对齐）。
    /// </summary>
    public static void EnsureDatabaseSchema(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS runs (
    run_id TEXT PRIMARY KEY,
    source TEXT NOT NULL,
    hero TEXT NOT NULL,
    persona TEXT NOT NULL,
    archetype TEXT NOT NULL,
    final_day INTEGER NOT NULL,
    final_wins INTEGER NOT NULL,
    final_losses INTEGER NOT NULL,
    is_victory INTEGER NOT NULL,
    version TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS day_boards (
    board_id TEXT PRIMARY KEY,
    run_id TEXT NOT NULL,
    source TEXT NOT NULL,
    day INTEGER NOT NULL,
    hero TEXT NOT NULL,
    level INTEGER NOT NULL,
    health_max INTEGER NOT NULL,
    prestige INTEGER NOT NULL,
    wins INTEGER NOT NULL,
    losses INTEGER NOT NULL,
    unlocked_slots_mask INTEGER NOT NULL,
    archetype TEXT NOT NULL,
    power_bucket TEXT NOT NULL,
    estimated_win_rate REAL NOT NULL,
    won_pvp INTEGER,
    opponent_board_id TEXT,
    board_signature TEXT NOT NULL,
    persona TEXT NOT NULL DEFAULT '',
    combat_duration_ms INTEGER,
    remaining_hp INTEGER,
    damage_dealt REAL,
    shield_gained REAL,
    is_balanced_pool INTEGER NOT NULL DEFAULT 0,
    is_natural_pool INTEGER NOT NULL DEFAULT 0,
    version TEXT NOT NULL DEFAULT '5.0.0'
);

CREATE TABLE IF NOT EXISTS board_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    board_id TEXT NOT NULL,
    slot INTEGER NOT NULL,
    width INTEGER NOT NULL,
    template_id TEXT NOT NULL,
    internal_name TEXT NOT NULL,
    zh_name TEXT NOT NULL,
    tier TEXT NOT NULL,
    enchantment TEXT,
    dynamic_attrs_json TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS board_skills (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    board_id TEXT NOT NULL,
    template_id TEXT NOT NULL,
    internal_name TEXT NOT NULL,
    zh_name TEXT NOT NULL,
    tier TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_boards_day_hero_power ON day_boards (day, hero, power_bucket);
CREATE INDEX IF NOT EXISTS idx_boards_run ON day_boards (run_id, day);
CREATE INDEX IF NOT EXISTS idx_items_board ON board_items (board_id);
CREATE INDEX IF NOT EXISTS idx_skills_board ON board_skills (board_id);
";
        cmd.ExecuteNonQuery();

        // 若读取旧版本预存库，兼容补齐 is_natural_pool 列
        EnsureColumnExists(conn, "day_boards", "is_natural_pool", "INTEGER NOT NULL DEFAULT 0");
    }

    private static void EnsureColumnExists(SqliteConnection conn, string tableName, string columnName, string columnDef)
    {
        bool exists = false;
        using (var infoCmd = conn.CreateCommand())
        {
            infoCmd.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (!exists)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDef};";
            alter.ExecuteNonQuery();
        }
    }

    private void ExportSqlite(string dbPath, SamplingResult sampling)
    {
        DeleteIfExists(dbPath);
        DeleteIfExists(dbPath + "-wal");
        DeleteIfExists(dbPath + "-shm");

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY;";
            pragma.ExecuteNonQuery();
        }

        EnsureDatabaseSchema(conn);

        // 统计属于均衡镜像池及自然漏斗池的 BoardId，并在内存中按 BoardId 去重以避免主键冲突
        var balancedBoardIds = new HashSet<string>(sampling.BalancedGhostPool.Select(b => b.BoardId), StringComparer.Ordinal);
        var naturalBoardIds = new HashSet<string>(sampling.NaturalGhostPool.Select(b => b.BoardId), StringComparer.Ordinal);
        var uniqueBoards = new Dictionary<string, PvpBoardSnapshot>(StringComparer.Ordinal);

        foreach (var run in sampling.SelectedRuns)
        {
            foreach (var board in run.DayBoards)
                uniqueBoards[board.BoardId] = board;
        }
        foreach (var board in sampling.BalancedGhostPool)
        {
            uniqueBoards.TryAdd(board.BoardId, board);
        }
        foreach (var board in sampling.NaturalGhostPool)
        {
            uniqueBoards.TryAdd(board.BoardId, board);
        }

        using var tx = conn.BeginTransaction();

        InsertRunsBatch(conn, tx, sampling.ReferencedRunsById.Values);
        InsertBoardsAndChildrenBatch(conn, tx, uniqueBoards.Values, balancedBoardIds, _validator, naturalBoardIds);

        tx.Commit();

        // 将 WAL 检查点刷盘并关闭连接，确保单文件自包含且不锁文件
        using var walFlush = conn.CreateCommand();
        walFlush.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
        walFlush.ExecuteNonQuery();
    }

    internal static void InsertRunsBatch(SqliteConnection conn, SqliteTransaction tx, IEnumerable<PvpRunRecord> runs)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
INSERT OR IGNORE INTO runs (
    run_id, source, hero, persona, archetype, final_day, final_wins, final_losses, is_victory, version
) VALUES (
    $run_id, $source, $hero, $persona, $archetype, $final_day, $final_wins, $final_losses, $is_victory, $version
);";
        var pRunId = cmd.Parameters.Add("$run_id", SqliteType.Text);
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pHero = cmd.Parameters.Add("$hero", SqliteType.Text);
        var pPersona = cmd.Parameters.Add("$persona", SqliteType.Text);
        var pArch = cmd.Parameters.Add("$archetype", SqliteType.Text);
        var pFinalDay = cmd.Parameters.Add("$final_day", SqliteType.Integer);
        var pFinalWins = cmd.Parameters.Add("$final_wins", SqliteType.Integer);
        var pFinalLosses = cmd.Parameters.Add("$final_losses", SqliteType.Integer);
        var pIsVictory = cmd.Parameters.Add("$is_victory", SqliteType.Integer);
        var pVersion = cmd.Parameters.Add("$version", SqliteType.Text);
        cmd.Prepare();

        foreach (var run in runs)
        {
            pRunId.Value = run.RunId;
            pSource.Value = run.Source;
            pHero.Value = run.Hero;
            pPersona.Value = run.Persona.ToString();
            pArch.Value = run.Archetype;
            pFinalDay.Value = run.FinalDay;
            pFinalWins.Value = run.FinalWins;
            pFinalLosses.Value = run.FinalLosses;
            pIsVictory.Value = run.IsVictory ? 1 : 0;
            pVersion.Value = run.Version;
            cmd.ExecuteNonQuery();
        }
    }

    internal static void InsertBoardsAndChildrenBatch(
        SqliteConnection conn,
        SqliteTransaction tx,
        IEnumerable<PvpBoardSnapshot> boards,
        IReadOnlySet<string> balancedBoardIds,
        IBoardValidator validator,
        IReadOnlySet<string>? naturalBoardIds = null)
    {
        using var boardCmd = conn.CreateCommand();
        boardCmd.Transaction = tx;
        boardCmd.CommandText = @"
INSERT OR IGNORE INTO day_boards (
    board_id, run_id, source, day, hero, level, health_max, prestige, wins, losses,
    unlocked_slots_mask, archetype, power_bucket, estimated_win_rate, won_pvp, opponent_board_id,
    board_signature, persona, combat_duration_ms, remaining_hp, damage_dealt, shield_gained, is_balanced_pool, is_natural_pool, version
) VALUES (
    $board_id, $run_id, $source, $day, $hero, $level, $health_max, $prestige, $wins, $losses,
    $unlocked_slots_mask, $archetype, $power_bucket, $estimated_win_rate, $won_pvp, $opponent_board_id,
    $board_signature, $persona, $combat_duration_ms, $remaining_hp, $damage_dealt, $shield_gained, $is_balanced_pool, $is_natural_pool, $version
);";
        var pbId = boardCmd.Parameters.Add("$board_id", SqliteType.Text);
        var pbRunId = boardCmd.Parameters.Add("$run_id", SqliteType.Text);
        var pbSource = boardCmd.Parameters.Add("$source", SqliteType.Text);
        var pbDay = boardCmd.Parameters.Add("$day", SqliteType.Integer);
        var pbHero = boardCmd.Parameters.Add("$hero", SqliteType.Text);
        var pbLevel = boardCmd.Parameters.Add("$level", SqliteType.Integer);
        var pbHp = boardCmd.Parameters.Add("$health_max", SqliteType.Integer);
        var pbPrestige = boardCmd.Parameters.Add("$prestige", SqliteType.Integer);
        var pbWins = boardCmd.Parameters.Add("$wins", SqliteType.Integer);
        var pbLosses = boardCmd.Parameters.Add("$losses", SqliteType.Integer);
        var pbMask = boardCmd.Parameters.Add("$unlocked_slots_mask", SqliteType.Integer);
        var pbArch = boardCmd.Parameters.Add("$archetype", SqliteType.Text);
        var pbPower = boardCmd.Parameters.Add("$power_bucket", SqliteType.Text);
        var pbWr = boardCmd.Parameters.Add("$estimated_win_rate", SqliteType.Real);
        var pbWon = boardCmd.Parameters.Add("$won_pvp", SqliteType.Integer);
        var pbOpp = boardCmd.Parameters.Add("$opponent_board_id", SqliteType.Text);
        var pbSig = boardCmd.Parameters.Add("$board_signature", SqliteType.Text);
        var pbPersona = boardCmd.Parameters.Add("$persona", SqliteType.Text);
        var pbDur = boardCmd.Parameters.Add("$combat_duration_ms", SqliteType.Integer);
        var pbRemHp = boardCmd.Parameters.Add("$remaining_hp", SqliteType.Integer);
        var pbDmg = boardCmd.Parameters.Add("$damage_dealt", SqliteType.Real);
        var pbShd = boardCmd.Parameters.Add("$shield_gained", SqliteType.Real);
        var pbBalanced = boardCmd.Parameters.Add("$is_balanced_pool", SqliteType.Integer);
        var pbNatural = boardCmd.Parameters.Add("$is_natural_pool", SqliteType.Integer);
        var pbVersion = boardCmd.Parameters.Add("$version", SqliteType.Text);
        boardCmd.Prepare();

        using var itemCmd = conn.CreateCommand();
        itemCmd.Transaction = tx;
        itemCmd.CommandText = @"
INSERT INTO board_items (
    board_id, slot, width, template_id, internal_name, zh_name, tier, enchantment, dynamic_attrs_json
) VALUES (
    $board_id, $slot, $width, $template_id, $internal_name, $zh_name, $tier, $enchantment, $dynamic_attrs_json
);";
        var piBoard = itemCmd.Parameters.Add("$board_id", SqliteType.Text);
        var piSlot = itemCmd.Parameters.Add("$slot", SqliteType.Integer);
        var piWidth = itemCmd.Parameters.Add("$width", SqliteType.Integer);
        var piTpl = itemCmd.Parameters.Add("$template_id", SqliteType.Text);
        var piName = itemCmd.Parameters.Add("$internal_name", SqliteType.Text);
        var piZh = itemCmd.Parameters.Add("$zh_name", SqliteType.Text);
        var piTier = itemCmd.Parameters.Add("$tier", SqliteType.Text);
        var piEnch = itemCmd.Parameters.Add("$enchantment", SqliteType.Text);
        var piAttrs = itemCmd.Parameters.Add("$dynamic_attrs_json", SqliteType.Text);
        itemCmd.Prepare();

        using var skillCmd = conn.CreateCommand();
        skillCmd.Transaction = tx;
        skillCmd.CommandText = @"
INSERT INTO board_skills (
    board_id, template_id, internal_name, zh_name, tier
) VALUES (
    $board_id, $template_id, $internal_name, $zh_name, $tier
);";
        var psBoard = skillCmd.Parameters.Add("$board_id", SqliteType.Text);
        var psTpl = skillCmd.Parameters.Add("$template_id", SqliteType.Text);
        var psName = skillCmd.Parameters.Add("$internal_name", SqliteType.Text);
        var psZh = skillCmd.Parameters.Add("$zh_name", SqliteType.Text);
        var psTier = skillCmd.Parameters.Add("$tier", SqliteType.Text);
        skillCmd.Prepare();

        foreach (var b in boards)
        {
            string sig = string.IsNullOrEmpty(b.BoardSignature) ? validator.ComputeSignature(b) : b.BoardSignature;
            b.BoardSignature = sig;

            pbId.Value = b.BoardId;
            pbRunId.Value = b.RunId;
            pbSource.Value = b.Source;
            pbDay.Value = b.Day;
            pbHero.Value = b.Hero;
            pbLevel.Value = b.Level;
            pbHp.Value = b.HealthMax;
            pbPrestige.Value = b.Prestige;
            pbWins.Value = b.Wins;
            pbLosses.Value = b.Losses;
            pbMask.Value = b.UnlockedSlotsMask;
            pbArch.Value = b.Archetype;
            pbPower.Value = b.PowerBucket.ToString();
            pbWr.Value = b.EstimatedWinRate;
            pbWon.Value = b.MatchOutcome != null ? (b.MatchOutcome.Won ? 1 : 0) : DBNull.Value;
            pbOpp.Value = b.MatchOutcome != null ? b.MatchOutcome.OpponentBoardId : DBNull.Value;
            pbSig.Value = sig;
            pbPersona.Value = b.Persona.ToString();
            pbDur.Value = b.MatchOutcome != null ? b.MatchOutcome.CombatDurationMs : DBNull.Value;
            pbRemHp.Value = b.MatchOutcome != null ? b.MatchOutcome.RemainingHp : DBNull.Value;
            pbDmg.Value = b.MatchOutcome != null ? b.MatchOutcome.DamageDealt : DBNull.Value;
            pbShd.Value = b.MatchOutcome != null ? b.MatchOutcome.ShieldGained : DBNull.Value;
            pbBalanced.Value = balancedBoardIds.Contains(b.BoardId) ? 1 : 0;
            pbNatural.Value = naturalBoardIds != null && naturalBoardIds.Contains(b.BoardId) ? 1 : 0;
            pbVersion.Value = b.Version;

            int affected = boardCmd.ExecuteNonQuery();
            if (affected == 0)
                continue; // 已存在相同主键时跳过子表插入，避免重复行

            foreach (var item in b.Items)
            {
                piBoard.Value = b.BoardId;
                piSlot.Value = item.Slot;
                piWidth.Value = item.Width;
                piTpl.Value = item.TemplateId;
                piName.Value = item.InternalName;
                piZh.Value = item.ZhName;
                piTier.Value = item.Tier.ToString();
                piEnch.Value = string.IsNullOrEmpty(item.Enchantment) ? DBNull.Value : item.Enchantment;
                piAttrs.Value = item.PermanentAttributes.Count == 0
                    ? "{}"
                    : JsonSerializer.Serialize(item.PermanentAttributes, PvpDatasetJson.PvpJsonOptions);
                itemCmd.ExecuteNonQuery();
            }

            foreach (var skill in b.Skills)
            {
                psBoard.Value = b.BoardId;
                psTpl.Value = skill.TemplateId;
                psName.Value = skill.InternalName;
                psZh.Value = skill.ZhName;
                psTier.Value = skill.Tier.ToString();
                skillCmd.ExecuteNonQuery();
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}

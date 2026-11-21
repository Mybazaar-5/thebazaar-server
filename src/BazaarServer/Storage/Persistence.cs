using Microsoft.Data.Sqlite;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Game;

namespace BazaarServer.Storage;

/// <summary>
/// 持久化：账号、生涯战绩、完整单局快照与真人 PvP 镜像池存 SQLite，服务端重启不丢。
/// 支持从旧版 V3 SQLite 数据库通过 PRAGMA table_info + ALTER TABLE 自动平滑迁移。
/// 内置按 RunId 聚合的 1.5s 防抖后台保存队列，降低高频换位写盘开销。
/// </summary>
public sealed class Persistence : IDisposable
{
    public const int DefaultSaveDebounceMs = 1500;

    private sealed record PendingRunSave(RunRow Row, long Generation, long DueTickMs);

    private readonly string _connString;
    private readonly object _lock = new();
    private readonly object _debounceSync = new();
    private readonly Dictionary<string, PendingRunSave> _pendingSaves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPersistedGen = new(StringComparer.Ordinal);
    private readonly Timer _debounceTimer;
    private long _debounceGeneration;
    private long _completedRunWriteCount;
    private int _disposed;

    public string DbPath { get; }

    /// <summary>当前防抖队列中尚未落盘的待处理 Run 数量。</summary>
    public int PendingDebounceCount
    {
        get
        {
            lock (_debounceSync) return _pendingSaves.Count;
        }
    }

    /// <summary>累计已完成 SQLite 写入的对局快照次数（用于并发压测与防抖合并验证）。</summary>
    public long CompletedRunWriteCount => Volatile.Read(ref _completedRunWriteCount);

    public Persistence(string dbPath)
    {
        DbPath = dbPath;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath) ?? ".");
        _connString = $"Data Source={dbPath}";
        Initialize();
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = """
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA busy_timeout=5000;
            """;
        pragma.ExecuteNonQuery();
        return conn;
    }

    private void Initialize()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                wal.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS accounts (
                        account_id      TEXT PRIMARY KEY,
                        username        TEXT NOT NULL,
                        identity_key    TEXT NOT NULL,
                        runs_completed  INTEGER NOT NULL DEFAULT 0,
                        victories       INTEGER NOT NULL DEFAULT 0,
                        defeats         INTEGER NOT NULL DEFAULT 0,
                        best_day        INTEGER NOT NULL DEFAULT 0,
                        created_at      TEXT NOT NULL,
                        last_seen       TEXT NOT NULL,
                        career_json     TEXT NOT NULL DEFAULT '',
                        custom_rules_json TEXT NOT NULL DEFAULT ''
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_identity ON accounts(identity_key);
                    CREATE INDEX IF NOT EXISTS idx_accounts_username ON accounts(username);

                    CREATE TABLE IF NOT EXISTS account_custom_rules (
                        account_id        TEXT PRIMARY KEY,
                        custom_rules_json TEXT NOT NULL DEFAULT '',
                        updated_at        TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS runs (
                        run_id          TEXT PRIMARY KEY,
                        account_id      TEXT NOT NULL,
                        hero            INTEGER NOT NULL,
                        play_mode       INTEGER NOT NULL,
                        game_mode_id    TEXT NOT NULL,
                        day             INTEGER NOT NULL,
                        hour            INTEGER NOT NULL,
                        victories       INTEGER NOT NULL,
                        defeats         INTEGER NOT NULL,
                        prestige        INTEGER NOT NULL,
                        gold            INTEGER NOT NULL,
                        income          INTEGER NOT NULL,
                        state           INTEGER NOT NULL,
                        saved_at        TEXT NOT NULL,
                        snapshot        TEXT,
                        is_ended        INTEGER NOT NULL DEFAULT 0,
                        ended_reason    TEXT NOT NULL DEFAULT '',
                        career_recorded INTEGER NOT NULL DEFAULT 0,
                        started_at      TEXT NOT NULL DEFAULT '',
                        is_custom_rule  INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_runs_account ON runs(account_id);

                    CREATE TABLE IF NOT EXISTS pvp_ghosts (
                        id              INTEGER PRIMARY KEY AUTOINCREMENT,
                        day             INTEGER NOT NULL,
                        hero            TEXT NOT NULL,
                        wins            INTEGER NOT NULL,
                        losses          INTEGER NOT NULL,
                        health_max      INTEGER NOT NULL,
                        prestige        INTEGER NOT NULL,
                        level           INTEGER NOT NULL,
                        unlocked_slots  INTEGER NOT NULL,
                        board_json      TEXT NOT NULL,
                        created_at      TEXT NOT NULL,
                        is_custom_rule  INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_pvp_ghosts_day ON pvp_ghosts(day);
                    """;
                cmd.ExecuteNonQuery();
            }

            // 兼容旧版 SQLite 数据库：按需自动 ALTER TABLE 补齐新增列
            EnsureColumn(conn, "accounts", "career_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "accounts", "custom_rules_json", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "runs", "is_ended", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "runs", "ended_reason", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "runs", "career_recorded", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "runs", "started_at", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "runs", "is_custom_rule", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "pvp_ghosts", "is_custom_rule", "INTEGER NOT NULL DEFAULT 0");

            // 为自定义规则幽灵过滤索引延后建表（确保旧库 ALTER TABLE 补列后再创建）
            using (var idx = conn.CreateCommand())
            {
                idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_pvp_ghosts_custom_day ON pvp_ghosts(is_custom_rule, day);";
                idx.ExecuteNonQuery();
            }

            // 回填旧数据中已处于终局状态但未标记 is_ended 的行
            using (var fix = conn.CreateCommand())
            {
                fix.CommandText = """
                    UPDATE runs
                    SET is_ended = 1,
                        ended_reason = CASE
                            WHEN ended_reason != '' THEN ended_reason
                            WHEN state = 4 THEN 'victory'
                            WHEN state = 3 THEN 'defeat'
                            ELSE 'shutdown'
                        END
                    WHERE is_ended = 0 AND state IN (3, 4, 10);
                    """;
                fix.ExecuteNonQuery();
            }
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string definition)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({table});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        if (!existing.Contains(column))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    public static bool IsTerminalState(ERunState state)
        => state is ERunState.EndRunDefeat or ERunState.EndRunVictory or ERunState.Shutdown;

    // ── 账号 ────────────────────────────────────────────────────────────────

    public sealed record AccountRow(
        Guid AccountId, string Username, string IdentityKey,
        int RunsCompleted, int Victories, int Defeats, uint BestDay,
        DateTime CreatedAt, DateTime LastSeen, string CareerJson = "");

    public AccountRow LoadAccount(string identityKey)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, username, identity_key, runs_completed, victories,
                       defeats, best_day, created_at, last_seen, career_json
                FROM accounts WHERE identity_key = $k LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$k", identityKey);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadAccountRow(r);
        }
    }

    public void SaveAccount(AccountRow a)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO accounts (account_id, username, identity_key, runs_completed,
                                      victories, defeats, best_day, created_at, last_seen, career_json, custom_rules_json)
                VALUES ($id, $u, $k, $rc, $w, $l, $bd, $ca, $ls, $cj,
                        COALESCE((SELECT custom_rules_json FROM account_custom_rules WHERE account_id = $id), ''))
                ON CONFLICT(account_id) DO UPDATE SET
                    username = excluded.username,
                    runs_completed = excluded.runs_completed,
                    victories = excluded.victories,
                    defeats = excluded.defeats,
                    best_day = excluded.best_day,
                    last_seen = excluded.last_seen,
                    career_json = CASE
                        WHEN excluded.career_json != '' THEN excluded.career_json
                        ELSE accounts.career_json
                    END;
                DELETE FROM account_custom_rules WHERE account_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", a.AccountId.ToString("D"));
            cmd.Parameters.AddWithValue("$u", a.Username);
            cmd.Parameters.AddWithValue("$k", a.IdentityKey);
            cmd.Parameters.AddWithValue("$rc", a.RunsCompleted);
            cmd.Parameters.AddWithValue("$w", a.Victories);
            cmd.Parameters.AddWithValue("$l", a.Defeats);
            cmd.Parameters.AddWithValue("$bd", (int)a.BestDay);
            cmd.Parameters.AddWithValue("$ca", a.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ls", a.LastSeen.ToString("O"));
            cmd.Parameters.AddWithValue("$cj", a.CareerJson ?? "");
            cmd.ExecuteNonQuery();
        }
    }

    private static string NormalizeAccountKey(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return "";
        return Guid.TryParse(accountId, out var g) ? g.ToString("D") : accountId.Trim();
    }

    /// <summary>线程安全保存账号自定义规则 JSON（优先落入 accounts.custom_rules_json，未建号时回退落盘待迁移表）。</summary>
    public void SaveAccountCustomRules(Guid accountId, string? json)
        => SaveAccountCustomRules(accountId.ToString("D"), json);

    /// <summary>线程安全保存账号自定义规则 JSON。</summary>
    public void SaveAccountCustomRules(string accountId, string? json)
    {
        string key = NormalizeAccountKey(accountId);
        if (string.IsNullOrEmpty(key)) return;
        string payload = json ?? "";

        lock (_lock)
        {
            using var conn = OpenConnection();
            int updated;
            using (var upd = conn.CreateCommand())
            {
                upd.CommandText = "UPDATE accounts SET custom_rules_json = $cr WHERE account_id = $id;";
                upd.Parameters.AddWithValue("$cr", payload);
                upd.Parameters.AddWithValue("$id", key);
                updated = upd.ExecuteNonQuery();
            }

            if (updated > 0)
            {
                using var del = conn.CreateCommand();
                del.CommandText = "DELETE FROM account_custom_rules WHERE account_id = $id;";
                del.Parameters.AddWithValue("$id", key);
                del.ExecuteNonQuery();
                return;
            }

            using var ups = conn.CreateCommand();
            ups.CommandText = """
                INSERT INTO account_custom_rules (account_id, custom_rules_json, updated_at)
                VALUES ($id, $cr, $ua)
                ON CONFLICT(account_id) DO UPDATE SET
                    custom_rules_json = excluded.custom_rules_json,
                    updated_at = excluded.updated_at;
                """;
            ups.Parameters.AddWithValue("$id", key);
            ups.Parameters.AddWithValue("$cr", payload);
            ups.Parameters.AddWithValue("$ua", DateTime.UtcNow.ToString("O"));
            ups.ExecuteNonQuery();
        }
    }

    /// <summary>线程安全读取账号自定义规则 JSON（未配置时返回空字符串）。</summary>
    public string LoadAccountCustomRules(Guid accountId)
        => LoadAccountCustomRules(accountId.ToString("D"));

    /// <summary>线程安全读取账号自定义规则 JSON（未配置时返回空字符串）。</summary>
    public string LoadAccountCustomRules(string accountId)
    {
        string key = NormalizeAccountKey(accountId);
        if (string.IsNullOrEmpty(key)) return "";

        lock (_lock)
        {
            using var conn = OpenConnection();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT custom_rules_json FROM accounts WHERE account_id = $id LIMIT 1;";
                cmd.Parameters.AddWithValue("$id", key);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                    return r.IsDBNull(0) ? "" : r.GetString(0);
            }

            using (var fb = conn.CreateCommand())
            {
                fb.CommandText = "SELECT custom_rules_json FROM account_custom_rules WHERE account_id = $id LIMIT 1;";
                fb.Parameters.AddWithValue("$id", key);
                object? obj = fb.ExecuteScalar();
                return obj is string s ? s : "";
            }
        }
    }

    public List<AccountRow> LoadAllAccounts()
    {
        var list = new List<AccountRow>();
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, username, identity_key, runs_completed, victories,
                       defeats, best_day, created_at, last_seen, career_json
                FROM accounts ORDER BY last_seen DESC
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadAccountRow(r));
            }
        }
        return list;
    }

    private static AccountRow ReadAccountRow(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3),
        r.GetInt32(4),
        r.GetInt32(5),
        (uint)r.GetInt32(6),
        DateTime.Parse(r.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        DateTime.Parse(r.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(9) ? "" : r.GetString(9));

    // ── 对局 ────────────────────────────────────────────────────────────────

    public sealed record RunRow(
        string RunId, Guid AccountId, EHero Hero, EPlayMode PlayMode, Guid GameModeId,
        uint Day, uint Hour, uint Victories, uint Defeats,
        int Prestige, int Gold, int Income, ERunState State,
        DateTime SavedAt, string Snapshot,
        bool IsEnded = false, string EndedReason = "", bool CareerRecorded = false,
        bool IsCustomRule = false);

    public void SaveRun(RunRow run)
        => SaveRunCore(run, assignNewGen: true, explicitGeneration: 0);

    public void SaveRun(RunRow run, bool isCustomRule)
        => SaveRunCore(run with { IsCustomRule = isCustomRule }, assignNewGen: true, explicitGeneration: 0);

    /// <summary>同步立即保存对局快照，同时取消同一 RunId 挂起的防抖后台任务。</summary>
    public void SaveRunSnapshot(RunState run, string accountIdOrSnapshot, bool isCustomRule = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        var row = BuildRunRowFromState(run, accountIdOrSnapshot, explicitSnapshotJson: null, isCustomRule);
        SaveRunCore(row, assignNewGen: true, explicitGeneration: 0);
    }

    /// <summary>同步立即保存对局快照（显式传入账号与快照 JSON），同时取消挂起防抖。</summary>
    public void SaveRunSnapshot(RunState run, string accountId, string snapshotJson, bool isCustomRule = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        var row = BuildRunRowFromState(run, accountId, snapshotJson, isCustomRule);
        SaveRunCore(row, assignNewGen: true, explicitGeneration: 0);
    }

    /// <summary>同步立即保存对局并取消该 RunId 挂起防抖。</summary>
    public void FlushRunImmediate(RunState run, string accountIdOrSnapshot, bool isCustomRule = false)
        => SaveRunSnapshot(run, accountIdOrSnapshot, isCustomRule);

    /// <summary>立即取消指定 RunId 的防抖计时并同步落盘其最新待保存快照。</summary>
    public void FlushRunImmediate(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;

        PendingRunSave? entry;
        lock (_debounceSync)
        {
            if (!_pendingSaves.Remove(runId, out entry))
                return;
            RescheduleTimerLocked();
        }

        WriteBatch(new[] { entry });
    }

    /// <summary>按 RunId 在 1500ms 窗口内合并高频写入（如 MoveItem 拖拽），仅在超时后异步落盘最新一次。</summary>
    public void SaveRunSnapshotDebounced(RunState run, string accountIdOrSnapshot, bool isCustomRule = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        var row = BuildRunRowFromState(run, accountIdOrSnapshot, explicitSnapshotJson: null, isCustomRule);

        lock (_debounceSync)
        {
            if (_disposed != 0)
            {
                // 已处于释放流程时直接同步落盘，避免丢失最后一次变更
                SaveRunCore(row, assignNewGen: true, explicitGeneration: 0);
                return;
            }

            long gen = ++_debounceGeneration;
            long dueMs = Environment.TickCount64 + DefaultSaveDebounceMs;
            _pendingSaves[row.RunId] = new PendingRunSave(row, gen, dueMs);
            RescheduleTimerLocked();
        }
    }

    /// <summary>优雅停服或测试断言前冲刷所有挂起的防抖对局快照。</summary>
    public void FlushAllPending()
    {
        List<PendingRunSave> batch;
        lock (_debounceSync)
        {
            if (_pendingSaves.Count == 0)
            {
                if (_disposed == 0)
                    _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            batch = new List<PendingRunSave>(_pendingSaves.Values);
            _pendingSaves.Clear();
            if (_disposed == 0)
                _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        WriteBatch(batch);
    }

    private void OnDebounceTimerElapsed(object? _)
    {
        List<PendingRunSave>? dueBatch = null;
        lock (_debounceSync)
        {
            if (_disposed != 0 || _pendingSaves.Count == 0)
                return;

            long now = Environment.TickCount64;
            foreach (var kv in _pendingSaves)
            {
                if (kv.Value.DueTickMs <= now)
                {
                    dueBatch ??= new List<PendingRunSave>();
                    dueBatch.Add(kv.Value);
                }
            }

            if (dueBatch != null)
            {
                foreach (var item in dueBatch)
                    _pendingSaves.Remove(item.Row.RunId);
            }

            RescheduleTimerLocked();
        }

        if (dueBatch is { Count: > 0 })
            WriteBatch(dueBatch);
    }

    private void RescheduleTimerLocked()
    {
        if (_disposed != 0) return;
        if (_pendingSaves.Count == 0)
        {
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            return;
        }

        long earliest = long.MaxValue;
        foreach (var entry in _pendingSaves.Values)
        {
            if (entry.DueTickMs < earliest)
                earliest = entry.DueTickMs;
        }

        long delay = Math.Max(10L, earliest - Environment.TickCount64);
        _debounceTimer.Change(delay, Timeout.Infinite);
    }

    private void SaveRunCore(RunRow run, bool assignNewGen, long explicitGeneration)
    {
        long gen = explicitGeneration;
        if (assignNewGen)
        {
            lock (_debounceSync)
            {
                _pendingSaves.Remove(run.RunId);
                gen = ++_debounceGeneration;
                RescheduleTimerLocked();
            }
        }

        WriteBatch(new[] { new PendingRunSave(run, gen, 0) });
    }

    private void WriteBatch(IReadOnlyList<PendingRunSave> batch)
    {
        if (batch.Count == 0) return;

        lock (_lock)
        {
            using var conn = OpenConnection();
            using var tx = conn.BeginTransaction();
            foreach (var item in batch)
            {
                var run = item.Row;
                if (_lastPersistedGen.TryGetValue(run.RunId, out long lastGen) && item.Generation < lastGen)
                    continue;

                ExecuteSaveRunCommand(conn, tx, run);
                _lastPersistedGen[run.RunId] = item.Generation;
                Interlocked.Increment(ref _completedRunWriteCount);
            }
            tx.Commit();
        }
    }

    private static void ExecuteSaveRunCommand(SqliteConnection conn, SqliteTransaction? tx, RunRow run)
    {
        bool ended = run.IsEnded || IsTerminalState(run.State);
        string reason = !string.IsNullOrEmpty(run.EndedReason)
            ? run.EndedReason
            : run.State switch
            {
                ERunState.EndRunVictory => "victory",
                ERunState.EndRunDefeat => "defeat",
                ERunState.Shutdown => "shutdown",
                _ => "",
            };

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runs (run_id, account_id, hero, play_mode, game_mode_id,
                              day, hour, victories, defeats, prestige, gold, income,
                              state, saved_at, snapshot, is_ended, ended_reason, career_recorded, started_at, is_custom_rule)
            VALUES ($rid, $aid, $h, $pm, $gm, $d, $hr, $w, $l, $p, $g, $inc, $st, $sa, $snap, $ie, $er, $cr, $sa, $icr)
            ON CONFLICT(run_id) DO UPDATE SET
                day = excluded.day, hour = excluded.hour,
                victories = excluded.victories, defeats = excluded.defeats,
                prestige = excluded.prestige, gold = excluded.gold,
                income = excluded.income, state = excluded.state,
                saved_at = excluded.saved_at, snapshot = excluded.snapshot,
                is_ended = excluded.is_ended,
                ended_reason = excluded.ended_reason,
                is_custom_rule = excluded.is_custom_rule,
                career_recorded = CASE
                    WHEN excluded.is_ended = 0 THEN 0
                    ELSE MAX(runs.career_recorded, excluded.career_recorded)
                END
            """;
        cmd.Parameters.AddWithValue("$rid", run.RunId);
        cmd.Parameters.AddWithValue("$aid", run.AccountId.ToString("D"));
        cmd.Parameters.AddWithValue("$h", (int)run.Hero);
        cmd.Parameters.AddWithValue("$pm", (int)run.PlayMode);
        cmd.Parameters.AddWithValue("$gm", run.GameModeId.ToString("D"));
        cmd.Parameters.AddWithValue("$d", (int)run.Day);
        cmd.Parameters.AddWithValue("$hr", (int)run.Hour);
        cmd.Parameters.AddWithValue("$w", (int)run.Victories);
        cmd.Parameters.AddWithValue("$l", (int)run.Defeats);
        cmd.Parameters.AddWithValue("$p", run.Prestige);
        cmd.Parameters.AddWithValue("$g", run.Gold);
        cmd.Parameters.AddWithValue("$inc", run.Income);
        cmd.Parameters.AddWithValue("$st", (int)run.State);
        cmd.Parameters.AddWithValue("$sa", run.SavedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$snap", run.Snapshot ?? "");
        cmd.Parameters.AddWithValue("$ie", ended ? 1 : 0);
        cmd.Parameters.AddWithValue("$er", reason);
        cmd.Parameters.AddWithValue("$cr", run.CareerRecorded ? 1 : 0);
        cmd.Parameters.AddWithValue("$icr", run.IsCustomRule ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static RunRow BuildRunRowFromState(
        RunState run,
        string accountIdOrSnapshot,
        string? explicitSnapshotJson,
        bool isCustomRule)
    {
        bool secondArgIsSnapshot = explicitSnapshotJson == null &&
            !string.IsNullOrWhiteSpace(accountIdOrSnapshot) &&
            accountIdOrSnapshot.TrimStart().StartsWith('{');

        string snapshotJson = explicitSnapshotJson
            ?? (secondArgIsSnapshot
                ? accountIdOrSnapshot
                : RunSnapshotSerializer.Serialize(
                    run,
                    startSelectionDone: RunSnapshotSerializer.InferStartSelectionDone(run),
                    pendingLevelUp: run.PendingLevelUp));

        Guid accountGuid = !secondArgIsSnapshot && Guid.TryParse(accountIdOrSnapshot, out var parsed)
            ? parsed
            : Guid.Empty;

        return new RunRow(
            run.RunId,
            accountGuid,
            run.Hero,
            run.PlayMode,
            run.GameModeId,
            run.Day,
            run.Hour,
            run.Victories,
            run.Defeats,
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20),
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0),
            run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0),
            run.State,
            DateTime.UtcNow,
            snapshotJson,
            IsTerminalState(run.State),
            IsCustomRule: isCustomRule);
    }

    public RunRow LoadRun(string runId)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                       victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                       is_ended, ended_reason, career_recorded, is_custom_rule
                FROM runs WHERE run_id = $rid LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadRunRow(r);
        }
    }

    /// <summary>
    /// 查询指定账号最近一次未结束且含完整 JSON 快照的活跃对局。
    /// 排除 EndRunDefeat(3)、EndRunVictory(4)、Shutdown(10) 与 is_ended=1 记录。
    /// </summary>
    public RunRow LoadLatestActiveRun(Guid accountId, bool? isCustomRule = null)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                       victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                       is_ended, ended_reason, career_recorded, is_custom_rule
                FROM runs
                WHERE account_id = $aid
                  AND is_ended = 0
                  AND state NOT IN (3, 4, 10)
                  AND snapshot IS NOT NULL
                  AND TRIM(snapshot) LIKE '{%'
                  AND ($icr IS NULL OR is_custom_rule = $icr)
                ORDER BY saved_at DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$aid", accountId.ToString("D"));
            cmd.Parameters.AddWithValue("$icr", isCustomRule.HasValue ? (isCustomRule.Value ? 1 : 0) : DBNull.Value);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadRunRow(r);
        }
    }

    /// <summary>标记指定对局已结束（胜利/战败/投降）。</summary>
    public void MarkRunEnded(string runId, ERunState endState, string reason)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE runs
                SET state = $st, is_ended = 1, ended_reason = $er, saved_at = $sa
                WHERE run_id = $rid
                """;
            cmd.Parameters.AddWithValue("$st", (int)endState);
            cmd.Parameters.AddWithValue("$er", reason ?? "ended");
            cmd.Parameters.AddWithValue("$sa", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$rid", runId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>将账号名下所有未完结对局标记为投降结束，并返回最新一局（若有）。</summary>
    public RunRow ConcedeActiveRuns(Guid accountId)
    {
        lock (_lock)
        {
            using var conn = OpenConnection();

            RunRow latest = null;
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = """
                    SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                           victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                           is_ended, ended_reason, career_recorded, is_custom_rule
                    FROM runs
                    WHERE account_id = $aid AND is_ended = 0 AND state NOT IN (3, 4, 10)
                    ORDER BY saved_at DESC LIMIT 1
                    """;
                sel.Parameters.AddWithValue("$aid", accountId.ToString("D"));
                using var r = sel.ExecuteReader();
                if (r.Read()) latest = ReadRunRow(r);
            }

            using (var upd = conn.CreateCommand())
            {
                upd.CommandText = """
                    UPDATE runs
                    SET state = 3, is_ended = 1, ended_reason = 'conceded', saved_at = $sa
                    WHERE account_id = $aid AND is_ended = 0 AND state NOT IN (3, 4, 10)
                    """;
                upd.Parameters.AddWithValue("$aid", accountId.ToString("D"));
                upd.Parameters.AddWithValue("$sa", DateTime.UtcNow.ToString("O"));
                upd.ExecuteNonQuery();
            }

            return latest;
        }
    }

    /// <summary>幂等抢占某局生涯结算标记，返回 true 表示首次结算、false 表示已结算过。</summary>
    public bool TryMarkCareerRecorded(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return false;
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE runs
                SET career_recorded = 1
                WHERE run_id = $rid AND career_recorded = 0
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public DateTime? GetRunStartedAt(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT started_at FROM runs WHERE run_id = $rid LIMIT 1";
            cmd.Parameters.AddWithValue("$rid", runId);
            object obj = cmd.ExecuteScalar();
            if (obj is string s && !string.IsNullOrWhiteSpace(s) &&
                DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }
            return null;
        }
    }

    private static RunRow ReadRunRow(SqliteDataReader r) => new(
        r.GetString(0),
        Guid.Parse(r.GetString(1)),
        (EHero)r.GetInt32(2),
        (EPlayMode)r.GetInt32(3),
        Guid.Parse(r.GetString(4)),
        (uint)r.GetInt32(5),
        (uint)r.GetInt32(6),
        (uint)r.GetInt32(7),
        (uint)r.GetInt32(8),
        r.GetInt32(9),
        r.GetInt32(10),
        r.GetInt32(11),
        (ERunState)r.GetInt32(12),
        DateTime.Parse(r.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind),
        r.IsDBNull(14) ? "" : r.GetString(14),
        !r.IsDBNull(15) && r.GetInt32(15) != 0,
        r.IsDBNull(16) ? "" : r.GetString(16),
        !r.IsDBNull(17) && r.GetInt32(17) != 0,
        r.FieldCount > 18 && !r.IsDBNull(18) && r.GetInt32(18) != 0);

    // ── 真人 PvP 幽灵镜像池 ─────────────────────────────────────────────────

    public sealed record PvpGhostRow(
        long Id, int Day, string Hero, int Wins, int Losses,
        int HealthMax, int Prestige, int Level, int UnlockedSlots,
        string BoardJson, DateTime CreatedAt, bool IsCustomRule = false);

    /// <summary>返回指定天数对应的标准镜像合理生命上限（直接与 PvpGhostPool.GetSaneBoundsForDay 对齐）。</summary>
    public static int MaxSaneGhostHealthForDay(int day)
        => BazaarServer.Data.PvpGhostPool.GetSaneBoundsForDay(Math.Max(1, day)).MaxHp;

    /// <summary>校验单条标准规则 PvP 镜像的 Day 与 HealthMax 是否处于合理区间。</summary>
    public static bool IsSaneGhostForDay(int day, int healthMax)
        => day >= 1 && day <= 30 && healthMax >= 50 && healthMax <= MaxSaneGhostHealthForDay(day);

    public long InsertPvpGhost(PvpGhostRow row)
        => InsertPvpGhost(row, row.IsCustomRule);

    public long InsertPvpGhost(PvpGhostRow row, bool isCustomRule)
    {
        if (!isCustomRule && !IsSaneGhostForDay(row.Day, row.HealthMax))
            return 0;

        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO pvp_ghosts (day, hero, wins, losses, health_max, prestige, level,
                                        unlocked_slots, board_json, created_at, is_custom_rule)
                VALUES ($d, $h, $w, $l, $hm, $p, $lvl, $us, $bj, $ca, $icr);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$d", row.Day);
            cmd.Parameters.AddWithValue("$h", row.Hero ?? "Vanessa");
            cmd.Parameters.AddWithValue("$w", row.Wins);
            cmd.Parameters.AddWithValue("$l", row.Losses);
            cmd.Parameters.AddWithValue("$hm", row.HealthMax);
            cmd.Parameters.AddWithValue("$p", row.Prestige);
            cmd.Parameters.AddWithValue("$lvl", row.Level);
            cmd.Parameters.AddWithValue("$us", row.UnlockedSlots);
            cmd.Parameters.AddWithValue("$bj", row.BoardJson ?? "{}");
            cmd.Parameters.AddWithValue("$ca", row.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$icr", isCustomRule ? 1 : 0);
            return Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    /// <summary>清理 pvp_ghosts 表中超出按天合理血量上限的旧脏标准镜像（不动 accounts 与自定义镜像）。</summary>
    public int PurgeCorruptedPvpGhosts()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            var badIds = new List<long>();
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT id, day, health_max FROM pvp_ghosts WHERE is_custom_rule = 0;";
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    int day = r.GetInt32(1);
                    int hpMax = r.GetInt32(2);
                    if (!IsSaneGhostForDay(day, hpMax))
                        badIds.Add(id);
                }
            }

            if (badIds.Count == 0)
                return 0;

            using var tx = conn.BeginTransaction();
            int deleted = 0;
            foreach (long id in badIds)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM pvp_ghosts WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                deleted += del.ExecuteNonQuery();
            }
            tx.Commit();
            if (deleted > 0)
                Console.WriteLine($"[Persistence] purged {deleted} corrupted standard ghost board(s) from pvp_ghosts");
            return deleted;
        }
    }

    /// <summary>加载 PvP 幽灵快照（默认仅加载标准规则幽灵 WHERE is_custom_rule = 0，并自动过滤异常超血旧脏镜像）。</summary>
    public List<PvpGhostRow> LoadPvpGhosts(int limit = 2000, bool includeCustomRule = false)
    {
        var list = new List<PvpGhostRow>();
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            string filter = includeCustomRule ? "" : "WHERE is_custom_rule = 0";
            cmd.CommandText = $"""
                SELECT id, day, hero, wins, losses, health_max, prestige, level,
                       unlocked_slots, board_json, created_at, is_custom_rule
                FROM pvp_ghosts
                {filter}
                ORDER BY id DESC
                LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$lim", Math.Max(1, limit));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int day = r.GetInt32(1);
                int healthMax = r.GetInt32(5);
                bool rowIsCustom = !r.IsDBNull(11) && r.GetInt32(11) != 0;
                if (!rowIsCustom && !IsSaneGhostForDay(day, healthMax))
                    continue;

                list.Add(new PvpGhostRow(
                    r.GetInt64(0),
                    day,
                    r.GetString(2),
                    r.GetInt32(3),
                    r.GetInt32(4),
                    healthMax,
                    r.GetInt32(6),
                    r.GetInt32(7),
                    r.GetInt32(8),
                    r.GetString(9),
                    DateTime.Parse(r.GetString(10), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    rowIsCustom));
            }
        }
        return list;
    }

    public int CountPvpGhosts()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pvp_ghosts";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int CountAccounts()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM accounts";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public int CountRuns()
    {
        lock (_lock)
        {
            using var conn = OpenConnection();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM runs";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public void Dispose()
    {
        List<PendingRunSave> remaining;
        lock (_debounceSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            remaining = new List<PendingRunSave>(_pendingSaves.Values);
            _pendingSaves.Clear();
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (remaining.Count > 0)
        {
            try { WriteBatch(remaining); } catch { }
        }

        using var waitHandle = new ManualResetEvent(false);
        if (_debounceTimer.Dispose(waitHandle))
            waitHandle.WaitOne(2000);
    }
}

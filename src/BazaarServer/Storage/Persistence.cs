using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Game;

namespace BazaarServer.Storage;

/// <summary>
/// SQLite 持久化总引擎（向后兼容旧版数据库，支持幂等 schema_migrations 自动升级）：
/// - 写通道：持久化长驻单写连接 + 串行写锁 + WAL 模式 + 1.5s 对局防抖合批写盘 + 优雅停机全量冲刷；
/// - 读通道：并发只读连接池（不阻塞写锁，大幅提升 100-CCU 读吞吐）；
/// - 扩展能力：保留 accounts / account_custom_rules / runs / pvp_ghosts 零破坏平滑补列，
///   自动创建 8 张多人联机/社交/回放/排行榜新表，并提供定期存储空间治理。
/// </summary>
public sealed partial class Persistence : IDisposable
{
    public const int DefaultSaveDebounceMs = 1500;
    public const int MaxReplayBlobBytes = 512 * 1024; // 单条 GZip 战斗回放上限 512 KB

    private sealed record PendingRunSave(RunRow Row, long Generation, long DueTickMs);

    private readonly string _writeConnString;
    private readonly string _readConnString;
    private readonly object _writeLock = new();
    private readonly object _debounceSync = new();
    private readonly Dictionary<string, PendingRunSave> _pendingSaves = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastPersistedGen = new(StringComparer.Ordinal);
    private readonly List<Action> _flushCallbacks = new();
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

        // 写连接复用标准池化连接串（默认 Pooling=True / ReadWriteCreate），确保对已有旧库立即切换 WAL 无锁冲突
        _writeConnString = $"Data Source={dbPath}";
        var readBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Default,
            Pooling = true,
            DefaultTimeout = 5,
        };
        _readConnString = readBuilder.ToString();

        Initialize();
        _debounceTimer = new Timer(OnDebounceTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>注册停机/全量冲刷时的附加回调（例如账号生涯防抖队列）。</summary>
    internal void RegisterFlushCallback(Action callback)
    {
        if (callback == null) return;
        lock (_debounceSync)
        {
            _flushCallbacks.Add(callback);
        }
    }

    /// <summary>
    /// 从池化连接获取写连接（由连接池持久化复用原生 sqlite3 句柄，且 ForeignKeys / DefaultTimeout 由连接串原生生效，
    /// 无需每请求重复执行 3 条 PRAGMA；using 结束后归还连接池，保证停服与 ClearAllPools 零文件锁残留）。
    /// </summary>
    private SqliteConnection OpenWriteConnection()
    {
        var conn = new SqliteConnection(_writeConnString);
        conn.Open();
        return conn;
    }

    /// <summary>打开一条池化只读连接，用于高并发无阻塞读取。</summary>
    private SqliteConnection OpenReadConnection()
    {
        var conn = new SqliteConnection(_readConnString);
        conn.Open();
        return conn;
    }

    /// <summary>执行带事务保护的写操作，统一串行化至单写通道。</summary>
    internal T ExecuteWrite<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        lock (_writeLock)
        {
            using var conn = OpenWriteConnection();
            using var tx = conn.BeginTransaction();
            try
            {
                T result = action(conn, tx);
                tx.Commit();
                return result;
            }
            catch
            {
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }

    /// <summary>执行带事务保护的无返回值写操作。</summary>
    internal void ExecuteWrite(Action<SqliteConnection, SqliteTransaction> action)
    {
        ExecuteWrite<int>((conn, tx) =>
        {
            action(conn, tx);
            return 0;
        });
    }

    /// <summary>执行无写锁竞争的并发只读查询。</summary>
    internal T ExecuteRead<T>(Func<SqliteConnection, T> action)
    {
        using var conn = OpenReadConnection();
        return action(conn);
    }

    private void Initialize()
    {
        lock (_writeLock)
        {
            using var conn = OpenWriteConnection();
            using (var wal = conn.CreateCommand())
            {
                wal.CommandText = "PRAGMA journal_mode=WAL;";
                _ = wal.ExecuteScalar();
            }
            using (var pragmas = conn.CreateCommand())
            {
                pragmas.CommandText = """
                    PRAGMA synchronous=NORMAL;
                    PRAGMA temp_store=MEMORY;
                    PRAGMA busy_timeout=5000;
                    PRAGMA foreign_keys=ON;
                    """;
                pragmas.ExecuteNonQuery();
            }

            // 1. 既有基础 4 张核心表（完全兼容旧版结构，不删除、不改名任何历史列）
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS schema_migrations (
                        version     INTEGER PRIMARY KEY,
                        name        TEXT NOT NULL,
                        applied_at  TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS accounts (
                        account_id        TEXT PRIMARY KEY,
                        username          TEXT NOT NULL,
                        identity_key      TEXT NOT NULL,
                        runs_completed    INTEGER NOT NULL DEFAULT 0,
                        victories         INTEGER NOT NULL DEFAULT 0,
                        defeats           INTEGER NOT NULL DEFAULT 0,
                        best_day          INTEGER NOT NULL DEFAULT 0,
                        created_at        TEXT NOT NULL,
                        last_seen         TEXT NOT NULL,
                        career_json       TEXT NOT NULL DEFAULT '',
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

            // 2. 幂等版本迁移（始终执行判缺补列以确保旧库与半迁移库均 100% 完整）
            ApplyMigration(conn, 1, "legacy_core_columns_and_terminal_backfill", c =>
            {
                EnsureColumn(c, "accounts", "career_json", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "accounts", "custom_rules_json", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "is_ended", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "runs", "ended_reason", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "career_recorded", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "runs", "started_at", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "is_custom_rule", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "pvp_ghosts", "is_custom_rule", "INTEGER NOT NULL DEFAULT 0");

                ExecuteNonQuery(c, "CREATE INDEX IF NOT EXISTS idx_pvp_ghosts_custom_day ON pvp_ghosts(is_custom_rule, day);");
                ExecuteNonQuery(c, """
                    UPDATE runs
                    SET is_ended = 1,
                        ended_reason = CASE
                            WHEN ended_reason != '' THEN ended_reason
                            WHEN state = 4 THEN 'victory'
                            WHEN state = 3 THEN 'defeat'
                            ELSE 'shutdown'
                        END
                    WHERE is_ended = 0 AND state IN (3, 4, 10);
                    """);
            });

            ApplyMigration(conn, 2, "wave3a_incremental_columns_for_legacy_tables", c =>
            {
                // accounts 增量列
                EnsureColumn(c, "accounts", "auth_secret_hash", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "accounts", "role", "TEXT NOT NULL DEFAULT 'player'");
                EnsureColumn(c, "accounts", "last_ping_ms", "INTEGER NOT NULL DEFAULT 0");

                // runs 增量列
                EnsureColumn(c, "runs", "tournament_session_id", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "mode_id", "TEXT NOT NULL DEFAULT 'standard'");
                EnsureColumn(c, "runs", "preset_id", "TEXT NOT NULL DEFAULT 'PureOfficial'");
                EnsureColumn(c, "runs", "mutators_json", "TEXT NOT NULL DEFAULT '[]'");
                EnsureColumn(c, "runs", "daily_seed_date", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "max_floor", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "runs", "score", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "runs", "final_rank", "INTEGER NOT NULL DEFAULT 0");
                EnsureColumn(c, "runs", "rules_json", "TEXT NOT NULL DEFAULT ''");
                EnsureColumn(c, "runs", "is_tainted", "INTEGER NOT NULL DEFAULT 0");

                ExecuteNonQuery(c, """
                    CREATE INDEX IF NOT EXISTS idx_runs_acc_active
                        ON runs(account_id, is_ended, saved_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_runs_tournament
                        ON runs(tournament_session_id, account_id);
                    CREATE INDEX IF NOT EXISTS idx_runs_mode_daily
                        ON runs(mode_id, daily_seed_date, score DESC);
                    """);

                // pvp_ghosts 增量列（保护标准镜像池分区）
                EnsureColumn(c, "pvp_ghosts", "mode_id", "TEXT NOT NULL DEFAULT 'standard'");
                EnsureColumn(c, "pvp_ghosts", "pool_partition", "TEXT NOT NULL DEFAULT 'standard'");

                // 回填历史上标记为 is_custom_rule = 1 但尚未分区的幽灵镜像
                ExecuteNonQuery(c, """
                    UPDATE pvp_ghosts
                    SET pool_partition = 'custom'
                    WHERE is_custom_rule = 1 AND (pool_partition = '' OR pool_partition = 'standard');
                    CREATE INDEX IF NOT EXISTS idx_pvp_ghosts_part_day
                        ON pvp_ghosts(pool_partition, is_custom_rule, day);
                    """);
            });

            ApplyMigration(conn, 3, "wave3a_multiplayer_social_replay_leaderboard_tables", c =>
            {
                ExecuteNonQuery(c, """
                    CREATE TABLE IF NOT EXISTS account_tokens (
                        token_hash   TEXT PRIMARY KEY,
                        account_id   TEXT NOT NULL,
                        token_type   TEXT NOT NULL,
                        client_kind  TEXT NOT NULL DEFAULT 'game',
                        device_id    TEXT NOT NULL DEFAULT '',
                        created_at   TEXT NOT NULL,
                        expires_at   TEXT NOT NULL,
                        revoked_at   TEXT NOT NULL DEFAULT ''
                    );
                    CREATE INDEX IF NOT EXISTS idx_tokens_account ON account_tokens(account_id, token_type);
                    CREATE INDEX IF NOT EXISTS idx_tokens_expires ON account_tokens(expires_at);

                    CREATE TABLE IF NOT EXISTS tournament_lobbies (
                        lobby_id                       TEXT PRIMARY KEY,
                        code                           TEXT NOT NULL UNIQUE,
                        name                           TEXT NOT NULL,
                        kind                           TEXT NOT NULL DEFAULT 'custom',
                        play_mode                      TEXT NOT NULL DEFAULT 'Tournament',
                        mode_id                        TEXT NOT NULL DEFAULT 'standard',
                        preset_id                      TEXT NOT NULL DEFAULT 'PureOfficial',
                        mutators_json                  TEXT NOT NULL DEFAULT '[]',
                        host_account_id                TEXT NOT NULL,
                        host_username                  TEXT NOT NULL,
                        player_cap                     INTEGER NOT NULL DEFAULT 8,
                        is_public                      INTEGER NOT NULL DEFAULT 1,
                        has_password                   INTEGER NOT NULL DEFAULT 0,
                        password_hash                  TEXT NOT NULL DEFAULT '',
                        is_invite_only                 INTEGER NOT NULL DEFAULT 0,
                        invite_token_hash              TEXT NOT NULL DEFAULT '',
                        exclude_host_from_gameplay     INTEGER NOT NULL DEFAULT 0,
                        state                          TEXT NOT NULL DEFAULT 'waiting',
                        tournament_session_id          TEXT NOT NULL DEFAULT '',
                        round_countdown_seconds        INTEGER NOT NULL DEFAULT 120,
                        accel_round_countdown_seconds  INTEGER NOT NULL DEFAULT 30,
                        rules_json                     TEXT NOT NULL DEFAULT '{}',
                        banned_encounter_ids_json      TEXT NOT NULL DEFAULT '[]',
                        round_barriers_json            TEXT NOT NULL DEFAULT '{}',
                        matchmaker_state_json          TEXT NOT NULL DEFAULT '{}',
                        created_at                     TEXT NOT NULL,
                        started_at                     TEXT NOT NULL DEFAULT '',
                        expires_at                     TEXT NOT NULL,
                        ended_at                       TEXT NOT NULL DEFAULT '',
                        updated_at                     TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_lobbies_state_public ON tournament_lobbies(state, is_public, created_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_lobbies_session_id   ON tournament_lobbies(tournament_session_id);
                    CREATE INDEX IF NOT EXISTS idx_lobbies_expires      ON tournament_lobbies(expires_at);

                    CREATE TABLE IF NOT EXISTS tournament_lobby_members (
                        lobby_id             TEXT NOT NULL,
                        account_id           TEXT NOT NULL,
                        username             TEXT NOT NULL,
                        hero                 TEXT NOT NULL DEFAULT 'Vanessa',
                        is_ai                INTEGER NOT NULL DEFAULT 0,
                        member_state         TEXT NOT NULL DEFAULT 'accepted',
                        connection_state     TEXT NOT NULL DEFAULT 'online',
                        ai_takeover_since    TEXT NOT NULL DEFAULT '',
                        missed_pvp_days      INTEGER NOT NULL DEFAULT 0,
                        current_day          INTEGER NOT NULL DEFAULT 1,
                        current_hour         INTEGER NOT NULL DEFAULT 1,
                        is_ready_h6_pvp      INTEGER NOT NULL DEFAULT 0,
                        has_finished_run     INTEGER NOT NULL DEFAULT 0,
                        is_champion          INTEGER NOT NULL DEFAULT 0,
                        is_eliminated        INTEGER NOT NULL DEFAULT 0,
                        is_conceded          INTEGER NOT NULL DEFAULT 0,
                        final_place          INTEGER NOT NULL DEFAULT 0,
                        victories            INTEGER NOT NULL DEFAULT 0,
                        defeats              INTEGER NOT NULL DEFAULT 0,
                        prestige             INTEGER NOT NULL DEFAULT 20,
                        final_day            INTEGER NOT NULL DEFAULT 1,
                        removal_reason       TEXT NOT NULL DEFAULT '',
                        joined_at            TEXT NOT NULL,
                        last_seen_at         TEXT NOT NULL,
                        PRIMARY KEY (lobby_id, account_id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_lobby_members_account ON tournament_lobby_members(account_id, member_state);

                    CREATE TABLE IF NOT EXISTS tournament_matchups (
                        lobby_id               TEXT NOT NULL,
                        day                    INTEGER NOT NULL,
                        pair_key               TEXT NOT NULL,
                        p1_account_id          TEXT NOT NULL,
                        p1_username            TEXT NOT NULL,
                        p1_is_bot              INTEGER NOT NULL DEFAULT 0,
                        p2_account_id          TEXT NOT NULL,
                        p2_username            TEXT NOT NULL,
                        p2_is_bot              INTEGER NOT NULL DEFAULT 0,
                        is_synthetic_sparring  INTEGER NOT NULL DEFAULT 0,
                        battle_seed            INTEGER NOT NULL,
                        p1_locked_board_json   TEXT NOT NULL DEFAULT '',
                        p2_locked_board_json   TEXT NOT NULL DEFAULT '',
                        winner_account_id      TEXT NOT NULL DEFAULT '',
                        loser_account_id       TEXT NOT NULL DEFAULT '',
                        loser_prestige_damage  INTEGER NOT NULL DEFAULT 0,
                        is_auto_resolved       INTEGER NOT NULL DEFAULT 0,
                        replay_id              TEXT NOT NULL DEFAULT '',
                        resolved_at            TEXT NOT NULL DEFAULT '',
                        PRIMARY KEY (lobby_id, day, pair_key)
                    );
                    CREATE INDEX IF NOT EXISTS idx_matchups_replay ON tournament_matchups(replay_id);

                    CREATE TABLE IF NOT EXISTS mode_leaderboards (
                        id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                        board_key          TEXT NOT NULL,
                        season_id          INTEGER NOT NULL DEFAULT 2,
                        mode_id            TEXT NOT NULL,
                        account_id         TEXT NOT NULL,
                        username           TEXT NOT NULL,
                        hero               TEXT NOT NULL,
                        run_id             TEXT NOT NULL,
                        score              INTEGER NOT NULL DEFAULT 0,
                        primary_metric     INTEGER NOT NULL DEFAULT 0,
                        secondary_metric   INTEGER NOT NULL DEFAULT 0,
                        elapsed_seconds    INTEGER NOT NULL DEFAULT 0,
                        day_reached        INTEGER NOT NULL DEFAULT 1,
                        victories          INTEGER NOT NULL DEFAULT 0,
                        defeats            INTEGER NOT NULL DEFAULT 0,
                        prestige           INTEGER NOT NULL DEFAULT 0,
                        is_first_attempt   INTEGER NOT NULL DEFAULT 1,
                        mutators_json      TEXT NOT NULL DEFAULT '[]',
                        summary_board_json TEXT NOT NULL DEFAULT '{}',
                        achieved_at        TEXT NOT NULL
                    );
                    CREATE UNIQUE INDEX IF NOT EXISTS idx_lb_unique_run ON mode_leaderboards(board_key, run_id);
                    CREATE INDEX IF NOT EXISTS idx_lb_rank_query
                        ON mode_leaderboards(board_key, score DESC, primary_metric DESC, secondary_metric DESC, elapsed_seconds ASC, achieved_at ASC);
                    CREATE INDEX IF NOT EXISTS idx_lb_account ON mode_leaderboards(account_id, board_key);

                    CREATE TABLE IF NOT EXISTS match_history_and_replays (
                        replay_id             TEXT PRIMARY KEY,
                        run_id                TEXT NOT NULL,
                        lobby_id              TEXT NOT NULL DEFAULT '',
                        tournament_session_id TEXT NOT NULL DEFAULT '',
                        mode_id               TEXT NOT NULL DEFAULT 'standard',
                        combat_type           TEXT NOT NULL,
                        day                   INTEGER NOT NULL,
                        hour                  INTEGER NOT NULL,
                        battle_seed           INTEGER NOT NULL,
                        p1_account_id         TEXT NOT NULL,
                        p1_username           TEXT NOT NULL,
                        p1_hero               TEXT NOT NULL,
                        p2_account_id         TEXT NOT NULL DEFAULT '',
                        p2_username           TEXT NOT NULL,
                        p2_hero               TEXT NOT NULL,
                        winner_side           TEXT NOT NULL,
                        duration_ms           INTEGER NOT NULL,
                        p1_remaining_hp       INTEGER NOT NULL,
                        p2_remaining_hp       INTEGER NOT NULL,
                        p1_damage_dealt       INTEGER NOT NULL DEFAULT 0,
                        p2_damage_dealt       INTEGER NOT NULL DEFAULT 0,
                        p1_board_json         TEXT NOT NULL,
                        p2_board_json         TEXT NOT NULL,
                        timeline_summary_json TEXT NOT NULL DEFAULT '{}',
                        combat_sim_gzip       BLOB,
                        created_at            TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_replays_p1      ON match_history_and_replays(p1_account_id, created_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_replays_p2      ON match_history_and_replays(p2_account_id, created_at DESC);
                    CREATE INDEX IF NOT EXISTS idx_replays_run     ON match_history_and_replays(run_id, day, hour);
                    CREATE INDEX IF NOT EXISTS idx_replays_lobby   ON match_history_and_replays(lobby_id, day);
                    CREATE INDEX IF NOT EXISTS idx_replays_created ON match_history_and_replays(created_at);

                    CREATE TABLE IF NOT EXISTS chat_messages (
                        id           INTEGER PRIMARY KEY AUTOINCREMENT,
                        channel_type TEXT NOT NULL,
                        channel_id   TEXT NOT NULL DEFAULT '',
                        msg_type     TEXT NOT NULL DEFAULT 'chat',
                        sender_id    TEXT NOT NULL DEFAULT '',
                        sender_name  TEXT NOT NULL DEFAULT '系统',
                        content      TEXT NOT NULL,
                        meta_json    TEXT NOT NULL DEFAULT '{}',
                        created_at   TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_chat_channel_id ON chat_messages(channel_type, channel_id, id DESC);
                    CREATE INDEX IF NOT EXISTS idx_chat_created    ON chat_messages(created_at);
                    """);
            });
        }
    }

    private static void ApplyMigration(SqliteConnection conn, int version, string name, Action<SqliteConnection> step)
    {
        bool alreadyApplied;
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = $v;";
            check.Parameters.AddWithValue("$v", version);
            alreadyApplied = Convert.ToInt32(check.ExecuteScalar()) > 0;
        }

        // 所有迁移步骤均严格幂等（CREATE IF NOT EXISTS / EnsureColumn），即便重放也安全
        step(conn);

        if (!alreadyApplied)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT OR IGNORE INTO schema_migrations (version, name, applied_at)
                VALUES ($v, $n, $at);
                """;
            ins.Parameters.AddWithValue("$v", version);
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }
    }

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
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

    /// <summary>查询已应用的数据库迁移版本列表（用于健康自检与审计验证）。</summary>
    public List<(int Version, string Name, DateTime AppliedAt)> GetAppliedMigrations()
    {
        return ExecuteRead(conn =>
        {
            var list = new List<(int, string, DateTime)>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version, name, applied_at FROM schema_migrations ORDER BY version ASC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add((
                    r.GetInt32(0),
                    r.GetString(1),
                    ParseUtc(r.GetString(2))));
            }
            return list;
        });
    }

    // ── 账号 ────────────────────────────────────────────────────────────────

    public sealed record AccountRow(
        Guid AccountId, string Username, string IdentityKey,
        int RunsCompleted, int Victories, int Defeats, uint BestDay,
        DateTime CreatedAt, DateTime LastSeen, string CareerJson = "",
        string AuthSecretHash = "", string Role = "player", int LastPingMs = 0);

    public AccountRow? LoadAccount(string identityKey)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, username, identity_key, runs_completed, victories,
                       defeats, best_day, created_at, last_seen, career_json,
                       auth_secret_hash, role, last_ping_ms
                FROM accounts WHERE identity_key = $k LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$k", identityKey);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAccountRow(r) : null;
        });
    }

    public AccountRow? LoadAccountById(Guid accountId)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, username, identity_key, runs_completed, victories,
                       defeats, best_day, created_at, last_seen, career_json,
                       auth_secret_hash, role, last_ping_ms
                FROM accounts WHERE account_id = $id LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$id", accountId.ToString("D"));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadAccountRow(r) : null;
        });
    }

    public void SaveAccount(AccountRow a)
    {
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO accounts (account_id, username, identity_key, runs_completed,
                                      victories, defeats, best_day, created_at, last_seen, career_json,
                                      custom_rules_json, auth_secret_hash, role, last_ping_ms)
                VALUES ($id, $u, $k, $rc, $w, $l, $bd, $ca, $ls, $cj,
                        COALESCE((SELECT custom_rules_json FROM account_custom_rules WHERE account_id = $id), ''),
                        $ash, $role, $ping)
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
                    END,
                    auth_secret_hash = CASE
                        WHEN excluded.auth_secret_hash != '' THEN excluded.auth_secret_hash
                        ELSE accounts.auth_secret_hash
                    END,
                    role = CASE
                        WHEN excluded.role != '' THEN excluded.role
                        ELSE accounts.role
                    END,
                    last_ping_ms = CASE
                        WHEN excluded.last_ping_ms > 0 THEN excluded.last_ping_ms
                        ELSE accounts.last_ping_ms
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
            cmd.Parameters.AddWithValue("$ash", a.AuthSecretHash ?? "");
            cmd.Parameters.AddWithValue("$role", string.IsNullOrWhiteSpace(a.Role) ? "player" : a.Role);
            cmd.Parameters.AddWithValue("$ping", Math.Max(0, a.LastPingMs));
            cmd.ExecuteNonQuery();
        });
    }

    public void UpdateAccountSecretHash(Guid accountId, string secretHash)
    {
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE accounts SET auth_secret_hash = $h WHERE account_id = $id;";
            cmd.Parameters.AddWithValue("$h", secretHash ?? "");
            cmd.Parameters.AddWithValue("$id", accountId.ToString("D"));
            cmd.ExecuteNonQuery();
        });
    }

    public void UpdateAccountLastPing(Guid accountId, int pingMs)
    {
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE accounts SET last_ping_ms = $p, last_seen = $ls WHERE account_id = $id;";
            cmd.Parameters.AddWithValue("$p", Math.Clamp(pingMs, 0, 60000));
            cmd.Parameters.AddWithValue("$ls", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("$id", accountId.ToString("D"));
            cmd.ExecuteNonQuery();
        });
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

        ExecuteWrite((conn, tx) =>
        {
            int updated;
            using (var upd = conn.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE accounts SET custom_rules_json = $cr WHERE account_id = $id;";
                upd.Parameters.AddWithValue("$cr", payload);
                upd.Parameters.AddWithValue("$id", key);
                updated = upd.ExecuteNonQuery();
            }

            if (updated > 0)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM account_custom_rules WHERE account_id = $id;";
                del.Parameters.AddWithValue("$id", key);
                del.ExecuteNonQuery();
                return;
            }

            using var ups = conn.CreateCommand();
            ups.Transaction = tx;
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
        });
    }

    /// <summary>线程安全读取账号自定义规则 JSON（未配置时返回空字符串）。</summary>
    public string LoadAccountCustomRules(Guid accountId)
        => LoadAccountCustomRules(accountId.ToString("D"));

    /// <summary>线程安全读取账号自定义规则 JSON（未配置时返回空字符串）。</summary>
    public string LoadAccountCustomRules(string accountId)
    {
        string key = NormalizeAccountKey(accountId);
        if (string.IsNullOrEmpty(key)) return "";

        return ExecuteRead(conn =>
        {
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
        });
    }

    public List<AccountRow> LoadAllAccounts()
    {
        return ExecuteRead(conn =>
        {
            var list = new List<AccountRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT account_id, username, identity_key, runs_completed, victories,
                       defeats, best_day, created_at, last_seen, career_json,
                       auth_secret_hash, role, last_ping_ms
                FROM accounts ORDER BY last_seen DESC
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(ReadAccountRow(r));
            }
            return list;
        });
    }

    private static AccountRow ReadAccountRow(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)),
        r.GetString(1),
        r.GetString(2),
        r.GetInt32(3),
        r.GetInt32(4),
        r.GetInt32(5),
        (uint)r.GetInt32(6),
        ParseUtc(r.GetString(7)),
        ParseUtc(r.GetString(8)),
        r.IsDBNull(9) ? "" : r.GetString(9),
        r.FieldCount > 10 && !r.IsDBNull(10) ? r.GetString(10) : "",
        r.FieldCount > 11 && !r.IsDBNull(11) ? r.GetString(11) : "player",
        r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetInt32(12) : 0);

    // ── 对局 ────────────────────────────────────────────────────────────────

    public sealed record RunRow(
        string RunId, Guid AccountId, EHero Hero, EPlayMode PlayMode, Guid GameModeId,
        uint Day, uint Hour, uint Victories, uint Defeats,
        int Prestige, int Gold, int Income, ERunState State,
        DateTime SavedAt, string Snapshot,
        bool IsEnded = false, string EndedReason = "", bool CareerRecorded = false,
        bool IsCustomRule = false,
        string TournamentSessionId = "",
        string ModeId = "standard",
        string PresetId = "PureOfficial",
        string MutatorsJson = "[]",
        string DailySeedDate = "",
        int MaxFloor = 0,
        int Score = 0,
        int FinalRank = 0,
        string RulesJson = "",
        bool IsTainted = false);

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
                SaveRunCore(row, assignNewGen: true, explicitGeneration: 0);
                return;
            }

            long gen = ++_debounceGeneration;
            long dueMs = Environment.TickCount64 + DefaultSaveDebounceMs;
            _pendingSaves[row.RunId] = new PendingRunSave(row, gen, dueMs);
            RescheduleTimerLocked();
        }
    }

    /// <summary>优雅停服或测试断言前冲刷所有挂起的防抖对局快照及已注册的防抖缓存。</summary>
    public void FlushAllPending()
    {
        List<PendingRunSave> batch;
        List<Action> callbacks;
        lock (_debounceSync)
        {
            batch = _pendingSaves.Count > 0 ? new List<PendingRunSave>(_pendingSaves.Values) : new List<PendingRunSave>();
            _pendingSaves.Clear();
            callbacks = new List<Action>(_flushCallbacks);
            if (_disposed == 0)
                _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (batch.Count > 0)
            WriteBatch(batch);

        foreach (var cb in callbacks)
        {
            try { cb(); } catch { }
        }
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

        ExecuteWrite((conn, tx) =>
        {
            foreach (var item in batch)
            {
                var run = item.Row;
                if (_lastPersistedGen.TryGetValue(run.RunId, out long lastGen) && item.Generation < lastGen)
                    continue;

                ExecuteSaveRunCommand(conn, tx, run);
                _lastPersistedGen[run.RunId] = item.Generation;
                Interlocked.Increment(ref _completedRunWriteCount);
            }
        });
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

        string aidText = run.AccountId.ToString("D");

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO runs (run_id, account_id, hero, play_mode, game_mode_id,
                              day, hour, victories, defeats, prestige, gold, income,
                              state, saved_at, snapshot, is_ended, ended_reason, career_recorded, started_at, is_custom_rule,
                              tournament_session_id, mode_id, preset_id, mutators_json, daily_seed_date,
                              max_floor, score, final_rank, rules_json, is_tainted)
            VALUES ($rid, $aid, $h, $pm, $gm, $d, $hr, $w, $l, $p, $g, $inc, $st, $sa, $snap, $ie, $er, $cr, $sa, $icr,
                    $tsid, $mid, $pid, $mjson, $dsd, $mf, $sc, $fr, $rjson, $tnt)
            ON CONFLICT(run_id) DO UPDATE SET
                account_id = CASE
                    WHEN excluded.account_id != '00000000-0000-0000-0000-000000000000' THEN excluded.account_id
                    ELSE runs.account_id
                END,
                day = excluded.day, hour = excluded.hour,
                victories = excluded.victories, defeats = excluded.defeats,
                prestige = excluded.prestige, gold = excluded.gold,
                income = excluded.income, state = excluded.state,
                saved_at = excluded.saved_at, snapshot = excluded.snapshot,
                is_ended = excluded.is_ended,
                ended_reason = excluded.ended_reason,
                is_custom_rule = excluded.is_custom_rule,
                tournament_session_id = CASE
                    WHEN excluded.tournament_session_id != '' THEN excluded.tournament_session_id
                    ELSE runs.tournament_session_id
                END,
                mode_id = excluded.mode_id,
                preset_id = excluded.preset_id,
                mutators_json = excluded.mutators_json,
                daily_seed_date = CASE
                    WHEN excluded.daily_seed_date != '' THEN excluded.daily_seed_date
                    ELSE runs.daily_seed_date
                END,
                max_floor = MAX(runs.max_floor, excluded.max_floor),
                score = MAX(runs.score, excluded.score),
                final_rank = CASE WHEN excluded.final_rank > 0 THEN excluded.final_rank ELSE runs.final_rank END,
                rules_json = CASE WHEN excluded.rules_json != '' THEN excluded.rules_json ELSE runs.rules_json END,
                is_tainted = MAX(runs.is_tainted, excluded.is_tainted),
                career_recorded = CASE
                    WHEN excluded.is_ended = 0 THEN 0
                    ELSE MAX(runs.career_recorded, excluded.career_recorded)
                END
            """;
        cmd.Parameters.AddWithValue("$rid", run.RunId);
        cmd.Parameters.AddWithValue("$aid", aidText);
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
        cmd.Parameters.AddWithValue("$tsid", run.TournamentSessionId ?? "");
        cmd.Parameters.AddWithValue("$mid", string.IsNullOrWhiteSpace(run.ModeId) ? "standard" : run.ModeId);
        cmd.Parameters.AddWithValue("$pid", string.IsNullOrWhiteSpace(run.PresetId) ? "PureOfficial" : run.PresetId);
        cmd.Parameters.AddWithValue("$mjson", string.IsNullOrWhiteSpace(run.MutatorsJson) ? "[]" : run.MutatorsJson);
        cmd.Parameters.AddWithValue("$dsd", run.DailySeedDate ?? "");
        cmd.Parameters.AddWithValue("$mf", Math.Max(run.MaxFloor, (int)run.Day));
        cmd.Parameters.AddWithValue("$sc", run.Score);
        cmd.Parameters.AddWithValue("$fr", run.FinalRank);
        cmd.Parameters.AddWithValue("$rjson", run.RulesJson ?? "");
        cmd.Parameters.AddWithValue("$tnt", run.IsTainted ? 1 : 0);
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

        // 修复 MP-P1-08：严禁在 3 参快照重载时把 account_id 误写为 Guid.Empty，回退取 run.AccountId
        Guid accountGuid = Guid.Empty;
        if (!secondArgIsSnapshot && Guid.TryParse(accountIdOrSnapshot, out var parsedFromArg))
        {
            accountGuid = parsedFromArg;
        }
        else if (!string.IsNullOrWhiteSpace(run.AccountId) && Guid.TryParse(run.AccountId, out var parsedFromRun))
        {
            accountGuid = parsedFromRun;
        }

        var rules = run.CustomRules ?? new CustomRunRuleSet();
        string modeId = ResolveModeKey(rules.SpecialModeId);
        string presetId = rules.PresetId.ToString();
        string mutatorsJson = rules.MutatorIds is { Count: > 0 }
            ? JsonSerializer.Serialize(rules.MutatorIds)
            : "[]";
        string rulesJson = JsonSerializer.Serialize(rules);

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
            IsCustomRule: isCustomRule || rules.IsCustomPowerModified(),
            TournamentSessionId: run.TournamentSessionId.HasValue && run.TournamentSessionId.Value != Guid.Empty
                ? run.TournamentSessionId.Value.ToString("D")
                : "",
            ModeId: modeId,
            PresetId: presetId,
            MutatorsJson: mutatorsJson,
            DailySeedDate: rules.DailyChallengeDate ?? "",
            MaxFloor: (int)run.Day,
            Score: 0,
            FinalRank: 0,
            RulesJson: rulesJson,
            IsTainted: false);
    }

    public static string ResolveModeKey(ESpecialModeId mode) => mode switch
    {
        ESpecialModeId.EndlessTower => "endless_tower",
        ESpecialModeId.CrossHeroMayhem => "cross_hero",
        ESpecialModeId.MutatorChallenge => "mutator",
        ESpecialModeId.DailyChallenge => "daily",
        ESpecialModeId.BossRush => "boss_rush",
        ESpecialModeId.SandboxLab => "sandbox",
        _ => "standard",
    };

    public RunRow? LoadRun(string runId)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                       victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                       is_ended, ended_reason, career_recorded, is_custom_rule,
                       tournament_session_id, mode_id, preset_id, mutators_json, daily_seed_date,
                       max_floor, score, final_rank, rules_json, is_tainted
                FROM runs WHERE run_id = $rid LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRunRow(r) : null;
        });
    }

    /// <summary>
    /// 查询指定账号最近一次未结束且含完整 JSON 快照的活跃对局。
    /// 排除 EndRunDefeat(3)、EndRunVictory(4)、Shutdown(10) 与 is_ended=1 记录。
    /// </summary>
    public RunRow? LoadLatestActiveRun(Guid accountId, bool? isCustomRule = null)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                       victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                       is_ended, ended_reason, career_recorded, is_custom_rule,
                       tournament_session_id, mode_id, preset_id, mutators_json, daily_seed_date,
                       max_floor, score, final_rank, rules_json, is_tainted
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
            return r.Read() ? ReadRunRow(r) : null;
        });
    }

    /// <summary>按锦标赛场次与账号加载未完结快照（用于掉线托管或防串局重连）。</summary>
    public RunRow? LoadLatestTournamentRun(Guid accountId, Guid tournamentSessionId)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                       victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                       is_ended, ended_reason, career_recorded, is_custom_rule,
                       tournament_session_id, mode_id, preset_id, mutators_json, daily_seed_date,
                       max_floor, score, final_rank, rules_json, is_tainted
                FROM runs
                WHERE account_id = $aid
                  AND tournament_session_id = $tsid
                  AND snapshot IS NOT NULL
                  AND TRIM(snapshot) LIKE '{%'
                ORDER BY saved_at DESC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$aid", accountId.ToString("D"));
            cmd.Parameters.AddWithValue("$tsid", tournamentSessionId.ToString("D"));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadRunRow(r) : null;
        });
    }

    /// <summary>标记指定对局已结束（胜利/战败/投降）。</summary>
    public void MarkRunEnded(string runId, ERunState endState, string reason)
    {
        if (string.IsNullOrWhiteSpace(runId)) return;
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
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
        });
    }

    /// <summary>将账号名下所有未完结对局标记为投降结束，并返回最新一局（若有）。</summary>
    public RunRow? ConcedeActiveRuns(Guid accountId)
    {
        return ExecuteWrite((conn, tx) =>
        {
            RunRow? latest = null;
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT run_id, account_id, hero, play_mode, game_mode_id, day, hour,
                           victories, defeats, prestige, gold, income, state, saved_at, snapshot,
                           is_ended, ended_reason, career_recorded, is_custom_rule,
                           tournament_session_id, mode_id, preset_id, mutators_json, daily_seed_date,
                           max_floor, score, final_rank, rules_json, is_tainted
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
                upd.Transaction = tx;
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
        });
    }

    /// <summary>幂等抢占某局生涯结算标记，返回 true 表示首次结算、false 表示已结算过。</summary>
    public bool TryMarkCareerRecorded(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return false;
        return ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE runs
                SET career_recorded = 1
                WHERE run_id = $rid AND career_recorded = 0
                """;
            cmd.Parameters.AddWithValue("$rid", runId);
            return cmd.ExecuteNonQuery() > 0;
        });
    }

    public DateTime? GetRunStartedAt(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId)) return null;
        return ExecuteRead<DateTime?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT started_at FROM runs WHERE run_id = $rid LIMIT 1";
            cmd.Parameters.AddWithValue("$rid", runId);
            object? obj = cmd.ExecuteScalar();
            if (obj is string s && !string.IsNullOrWhiteSpace(s) &&
                DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt))
            {
                return dt;
            }
            return null;
        });
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
        ParseUtc(r.GetString(13)),
        r.IsDBNull(14) ? "" : r.GetString(14),
        !r.IsDBNull(15) && r.GetInt32(15) != 0,
        r.IsDBNull(16) ? "" : r.GetString(16),
        !r.IsDBNull(17) && r.GetInt32(17) != 0,
        r.FieldCount > 18 && !r.IsDBNull(18) && r.GetInt32(18) != 0,
        r.FieldCount > 19 && !r.IsDBNull(19) ? r.GetString(19) : "",
        r.FieldCount > 20 && !r.IsDBNull(20) ? r.GetString(20) : "standard",
        r.FieldCount > 21 && !r.IsDBNull(21) ? r.GetString(21) : "PureOfficial",
        r.FieldCount > 22 && !r.IsDBNull(22) ? r.GetString(22) : "[]",
        r.FieldCount > 23 && !r.IsDBNull(23) ? r.GetString(23) : "",
        r.FieldCount > 24 && !r.IsDBNull(24) ? r.GetInt32(24) : 0,
        r.FieldCount > 25 && !r.IsDBNull(25) ? r.GetInt32(25) : 0,
        r.FieldCount > 26 && !r.IsDBNull(26) ? r.GetInt32(26) : 0,
        r.FieldCount > 27 && !r.IsDBNull(27) ? r.GetString(27) : "",
        r.FieldCount > 28 && !r.IsDBNull(28) && r.GetInt32(28) != 0);

    // ── 真人 PvP 幽灵镜像池（含 pool_partition 绝对隔离保护）─────────────────

    public sealed record PvpGhostRow(
        long Id, int Day, string Hero, int Wins, int Losses,
        int HealthMax, int Prestige, int Level, int UnlockedSlots,
        string BoardJson, DateTime CreatedAt, bool IsCustomRule = false,
        string ModeId = "standard", string PoolPartition = "standard");

    /// <summary>返回指定天数对应的标准镜像合理生命上限（直接与 PvpGhostPool.GetSaneBoundsForDay 对齐）。</summary>
    public static int MaxSaneGhostHealthForDay(int day)
        => BazaarServer.Data.PvpGhostPool.GetSaneBoundsForDay(Math.Max(1, day)).MaxHp;

    /// <summary>校验单条标准规则 PvP 镜像的 Day 与 HealthMax 是否处于合理区间。</summary>
    public static bool IsSaneGhostForDay(int day, int healthMax)
        => day >= 1 && day <= 30 && healthMax >= 50 && healthMax <= MaxSaneGhostHealthForDay(day);

    public long InsertPvpGhost(PvpGhostRow row)
        => InsertPvpGhost(row, row.IsCustomRule, row.PoolPartition, row.ModeId);

    public long InsertPvpGhost(PvpGhostRow row, bool isCustomRule)
        => InsertPvpGhost(row, isCustomRule, isCustomRule ? (string.IsNullOrWhiteSpace(row.PoolPartition) || row.PoolPartition == "standard" ? "custom" : row.PoolPartition) : "standard", row.ModeId);

    public long InsertPvpGhost(PvpGhostRow row, bool isCustomRule, string? poolPartition, string? modeId = null)
    {
        string partition = string.IsNullOrWhiteSpace(poolPartition)
            ? (isCustomRule ? "custom" : "standard")
            : poolPartition.Trim().ToLowerInvariant();
        if (partition == "none" || partition == "sandbox")
            return 0;

        bool effectiveCustom = isCustomRule || !string.Equals(partition, "standard", StringComparison.OrdinalIgnoreCase);
        if (!effectiveCustom && !IsSaneGhostForDay(row.Day, row.HealthMax))
            return 0;

        string effectiveMode = string.IsNullOrWhiteSpace(modeId) ? (effectiveCustom ? partition : "standard") : modeId.Trim();

        return ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO pvp_ghosts (day, hero, wins, losses, health_max, prestige, level,
                                        unlocked_slots, board_json, created_at, is_custom_rule, mode_id, pool_partition)
                VALUES ($d, $h, $w, $l, $hm, $p, $lvl, $us, $bj, $ca, $icr, $mid, $part);
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
            cmd.Parameters.AddWithValue("$icr", effectiveCustom ? 1 : 0);
            cmd.Parameters.AddWithValue("$mid", effectiveMode);
            cmd.Parameters.AddWithValue("$part", partition);
            return Convert.ToInt64(cmd.ExecuteScalar());
        });
    }

    /// <summary>清理 pvp_ghosts 表中超出按天合理血量上限的旧脏标准镜像（不动 accounts 与自定义镜像）。</summary>
    public int PurgeCorruptedPvpGhosts()
    {
        return ExecuteWrite((conn, tx) =>
        {
            var badIds = new List<long>();
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
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

            int deleted = 0;
            foreach (long id in badIds)
            {
                using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM pvp_ghosts WHERE id = $id;";
                del.Parameters.AddWithValue("$id", id);
                deleted += del.ExecuteNonQuery();
            }
            if (deleted > 0)
                Console.WriteLine($"[Persistence] 已清理 {deleted} 条超血异常标准幽灵镜像");
            return deleted;
        });
    }

    /// <summary>加载 PvP 幽灵快照（默认仅加载标准规则标准分区幽灵，并自动过滤异常超血旧脏镜像）。</summary>
    public List<PvpGhostRow> LoadPvpGhosts(int limit = 2000, bool includeCustomRule = false, string? poolPartition = null)
    {
        return ExecuteRead(conn =>
        {
            var list = new List<PvpGhostRow>();
            using var cmd = conn.CreateCommand();
            string filter;
            if (!string.IsNullOrWhiteSpace(poolPartition))
            {
                filter = "WHERE pool_partition = $part";
                cmd.Parameters.AddWithValue("$part", poolPartition.Trim().ToLowerInvariant());
            }
            else if (includeCustomRule)
            {
                filter = "";
            }
            else
            {
                filter = "WHERE is_custom_rule = 0 AND (pool_partition = 'standard' OR pool_partition = '')";
            }

            cmd.CommandText = $"""
                SELECT id, day, hero, wins, losses, health_max, prestige, level,
                       unlocked_slots, board_json, created_at, is_custom_rule, mode_id, pool_partition
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
                string rowMode = r.FieldCount > 12 && !r.IsDBNull(12) ? r.GetString(12) : "standard";
                string rowPart = r.FieldCount > 13 && !r.IsDBNull(13) && !string.IsNullOrWhiteSpace(r.GetString(13))
                    ? r.GetString(13)
                    : (rowIsCustom ? "custom" : "standard");

                if (!rowIsCustom && string.Equals(rowPart, "standard", StringComparison.OrdinalIgnoreCase) && !IsSaneGhostForDay(day, healthMax))
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
                    ParseUtc(r.GetString(10)),
                    rowIsCustom,
                    rowMode,
                    rowPart));
            }
            return list;
        });
    }

    public int CountPvpGhosts()
        => ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pvp_ghosts";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    public int CountAccounts()
        => ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM accounts";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    public int CountRuns()
        => ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM runs";
            return Convert.ToInt32(cmd.ExecuteScalar());
        });

    // ── 存储空间治理（过期令牌/回放二进制/超限聊天/归档大厅定期裁剪）─────────

    public sealed record StorageMaintenanceReport(
        int DeletedExpiredTokens,
        int TrimmedReplayBlobs,
        int DeletedOldChatMessages,
        int PurgedArchivedLobbies);

    /// <summary>
    /// 执行定期存储空间治理：
    /// 1. 清理过期超 7 天或已吊销超 1 天的 account_tokens；
    /// 2. 清理超过 30 天或超出 50,000 条上限的旧回放 combat_sim_gzip BLOB（保留轻量摘要 JSON）；
    /// 3. 单频道保留最近 500 条聊天，全服/系统播报保留最近 2,000 条；
    /// 4. 清理结束/取消超过 14 天的历史锦标赛大厅及附属对阵数据。
    /// </summary>
    public StorageMaintenanceReport RunStorageMaintenance(
        DateTime? nowUtc = null,
        int replayBlobRetentionDays = 30,
        int maxReplayBlobs = 50000,
        int maxLobbyChatPerChannel = 500,
        int maxGlobalChatMessages = 2000,
        int archivedLobbyRetentionDays = 14)
    {
        DateTime now = nowUtc ?? DateTime.UtcNow;
        string tokenCutoff = now.AddDays(-7).ToString("O");
        string revokedCutoff = now.AddDays(-1).ToString("O");
        string replayCutoff = now.AddDays(-Math.Max(1, replayBlobRetentionDays)).ToString("O");
        string lobbyCutoff = now.AddDays(-Math.Max(1, archivedLobbyRetentionDays)).ToString("O");

        return ExecuteWrite((conn, tx) =>
        {
            int deletedTokens;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    DELETE FROM account_tokens
                    WHERE expires_at < $expCut
                       OR (revoked_at != '' AND revoked_at < $revCut);
                    """;
                cmd.Parameters.AddWithValue("$expCut", tokenCutoff);
                cmd.Parameters.AddWithValue("$revCut", revokedCutoff);
                deletedTokens = cmd.ExecuteNonQuery();
            }

            int trimmedBlobs = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE match_history_and_replays
                    SET combat_sim_gzip = NULL
                    WHERE combat_sim_gzip IS NOT NULL AND created_at < $cut;
                    """;
                cmd.Parameters.AddWithValue("$cut", replayCutoff);
                trimmedBlobs += cmd.ExecuteNonQuery();
            }

            // 若仍超过总条数上限，则按最旧时间裁剪多余 BLOB
            using (var cntCmd = conn.CreateCommand())
            {
                cntCmd.Transaction = tx;
                cntCmd.CommandText = "SELECT COUNT(*) FROM match_history_and_replays WHERE combat_sim_gzip IS NOT NULL;";
                int blobCount = Convert.ToInt32(cntCmd.ExecuteScalar());
                int excess = blobCount - Math.Max(100, maxReplayBlobs);
                if (excess > 0)
                {
                    using var trimCmd = conn.CreateCommand();
                    trimCmd.Transaction = tx;
                    trimCmd.CommandText = """
                        UPDATE match_history_and_replays
                        SET combat_sim_gzip = NULL
                        WHERE replay_id IN (
                            SELECT replay_id FROM match_history_and_replays
                            WHERE combat_sim_gzip IS NOT NULL
                            ORDER BY created_at ASC
                            LIMIT $lim
                        );
                        """;
                    trimCmd.Parameters.AddWithValue("$lim", excess);
                    trimmedBlobs += trimCmd.ExecuteNonQuery();
                }
            }

            // 裁剪聊天消息：全服/系统频道保留最近 maxGlobalChatMessages 条，其余单频道保留 maxLobbyChatPerChannel 条
            int deletedChat = 0;
            var channels = new List<(string Type, string Id)>();
            using (var chCmd = conn.CreateCommand())
            {
                chCmd.Transaction = tx;
                chCmd.CommandText = "SELECT DISTINCT channel_type, channel_id FROM chat_messages;";
                using var r = chCmd.ExecuteReader();
                while (r.Read())
                    channels.Add((r.GetString(0), r.GetString(1)));
            }

            foreach (var (cType, cId) in channels)
            {
                int keep = (cType == "global" || cType == "system")
                    ? Math.Max(100, maxGlobalChatMessages)
                    : Math.Max(50, maxLobbyChatPerChannel);
                using var delChat = conn.CreateCommand();
                delChat.Transaction = tx;
                delChat.CommandText = """
                    DELETE FROM chat_messages
                    WHERE channel_type = $ct AND channel_id = $cid
                      AND id NOT IN (
                          SELECT id FROM chat_messages
                          WHERE channel_type = $ct AND channel_id = $cid
                          ORDER BY id DESC
                          LIMIT $keep
                      );
                    """;
                delChat.Parameters.AddWithValue("$ct", cType);
                delChat.Parameters.AddWithValue("$cid", cId);
                delChat.Parameters.AddWithValue("$keep", keep);
                deletedChat += delChat.ExecuteNonQuery();
            }

            // 清理超过保留期的已完结/已取消历史大厅
            var staleLobbyIds = new List<string>();
            using (var selLob = conn.CreateCommand())
            {
                selLob.Transaction = tx;
                selLob.CommandText = """
                    SELECT lobby_id FROM tournament_lobbies
                    WHERE (state IN ('finished', 'cancelled') AND updated_at < $cut)
                       OR (expires_at < $cut);
                    """;
                selLob.Parameters.AddWithValue("$cut", lobbyCutoff);
                using var r = selLob.ExecuteReader();
                while (r.Read())
                    staleLobbyIds.Add(r.GetString(0));
            }

            foreach (string lid in staleLobbyIds)
            {
                foreach (string tbl in new[] { "tournament_matchups", "tournament_lobby_members", "tournament_lobbies" })
                {
                    using var delLob = conn.CreateCommand();
                    delLob.Transaction = tx;
                    delLob.CommandText = $"DELETE FROM {tbl} WHERE lobby_id = $lid;";
                    delLob.Parameters.AddWithValue("$lid", lid);
                    delLob.ExecuteNonQuery();
                }
            }

            return new StorageMaintenanceReport(deletedTokens, trimmedBlobs, deletedChat, staleLobbyIds.Count);
        });
    }

    private static DateTime ParseUtc(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) &&
            DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
        {
            return dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        }
        return DateTime.UtcNow;
    }

    public void Dispose()
    {
        List<PendingRunSave> remaining;
        List<Action> callbacks;
        lock (_debounceSync)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            remaining = new List<PendingRunSave>(_pendingSaves.Values);
            _pendingSaves.Clear();
            callbacks = new List<Action>(_flushCallbacks);
            _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (remaining.Count > 0)
        {
            try { WriteBatch(remaining); } catch { }
        }

        foreach (var cb in callbacks)
        {
            try { cb(); } catch { }
        }

        using var waitHandle = new ManualResetEvent(false);
        if (_debounceTimer.Dispose(waitHandle))
            waitHandle.WaitOne(2000);

        lock (_writeLock)
        {
            try
            {
                using var conn = OpenWriteConnection();
                using var ck = conn.CreateCommand();
                ck.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                ck.ExecuteNonQuery();
            }
            catch { }
        }
    }
}

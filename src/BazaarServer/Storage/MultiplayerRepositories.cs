using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;

namespace BazaarServer.Storage;

/// <summary>
/// 多人联机扩展仓储（8 张新表对应的强类型行记录与 CRUD）：
/// - account_tokens（多端持久化令牌）
/// - tournament_lobbies / tournament_lobby_members / tournament_matchups（锦标赛持久化与对称裁决）
/// - mode_leaderboards（多模式与每日挑战排行榜）
/// - match_history_and_replays（历史战报与 GZip 压缩回放）
/// - chat_messages（房间/观战/全服聊天与系统播报）
/// </summary>
public sealed partial class Persistence
{
    // ── 1. 多端会话令牌表 (account_tokens) ──────────────────────────────────

    public sealed record AccountTokenRow(
        string TokenHash,
        Guid AccountId,
        string TokenType,
        string ClientKind,
        string DeviceId,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        DateTime? RevokedAt);

    public void UpsertAccountToken(AccountTokenRow row)
    {
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO account_tokens (token_hash, account_id, token_type, client_kind, device_id, created_at, expires_at, revoked_at)
                VALUES ($th, $aid, $tt, $ck, $did, $ca, $ea, $ra)
                ON CONFLICT(token_hash) DO UPDATE SET
                    expires_at = excluded.expires_at,
                    revoked_at = excluded.revoked_at;
                """;
            cmd.Parameters.AddWithValue("$th", row.TokenHash);
            cmd.Parameters.AddWithValue("$aid", row.AccountId.ToString("D"));
            cmd.Parameters.AddWithValue("$tt", row.TokenType);
            cmd.Parameters.AddWithValue("$ck", string.IsNullOrWhiteSpace(row.ClientKind) ? "game" : row.ClientKind);
            cmd.Parameters.AddWithValue("$did", row.DeviceId ?? "");
            cmd.Parameters.AddWithValue("$ca", row.CreatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ea", row.ExpiresAt.ToString("O"));
            cmd.Parameters.AddWithValue("$ra", row.RevokedAt.HasValue ? row.RevokedAt.Value.ToString("O") : "");
            cmd.ExecuteNonQuery();
        });
    }

    public AccountTokenRow? LoadAccountTokenByHash(string tokenHash)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) return null;
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT token_hash, account_id, token_type, client_kind, device_id, created_at, expires_at, revoked_at
                FROM account_tokens
                WHERE token_hash = $th LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$th", tokenHash);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadTokenRow(r) : null;
        });
    }

    public List<AccountTokenRow> LoadActiveAccountTokens(DateTime? nowUtc = null)
    {
        string nowIso = (nowUtc ?? DateTime.UtcNow).ToString("O");
        return ExecuteRead(conn =>
        {
            var list = new List<AccountTokenRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT token_hash, account_id, token_type, client_kind, device_id, created_at, expires_at, revoked_at
                FROM account_tokens
                WHERE revoked_at = '' AND expires_at > $now
                ORDER BY created_at DESC;
                """;
            cmd.Parameters.AddWithValue("$now", nowIso);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadTokenRow(r));
            return list;
        });
    }

    public void RevokeAccountToken(string tokenHash, DateTime? nowUtc = null)
    {
        if (string.IsNullOrWhiteSpace(tokenHash)) return;
        string nowIso = (nowUtc ?? DateTime.UtcNow).ToString("O");
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE account_tokens SET revoked_at = $now WHERE token_hash = $th AND revoked_at = '';";
            cmd.Parameters.AddWithValue("$now", nowIso);
            cmd.Parameters.AddWithValue("$th", tokenHash);
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// 限制单个账号指定类型令牌的活跃数量上限（默认 5 枚多端并发不互踢），仅淘汰超出上限的最旧令牌。
    /// 返回被淘汰的令牌哈希列表供内存缓存同步摘除。
    /// </summary>
    public List<string> TrimExcessAccountTokens(Guid accountId, string tokenType, int maxActive = 5, DateTime? nowUtc = null)
    {
        string nowIso = (nowUtc ?? DateTime.UtcNow).ToString("O");
        return ExecuteWrite((conn, tx) =>
        {
            var toRevoke = new List<string>();
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = """
                    SELECT token_hash FROM account_tokens
                    WHERE account_id = $aid AND token_type = $tt AND revoked_at = '' AND expires_at > $now
                    ORDER BY created_at DESC
                    LIMIT -1 OFFSET $keep;
                    """;
                sel.Parameters.AddWithValue("$aid", accountId.ToString("D"));
                sel.Parameters.AddWithValue("$tt", tokenType);
                sel.Parameters.AddWithValue("$now", nowIso);
                sel.Parameters.AddWithValue("$keep", Math.Max(1, maxActive));
                using var r = sel.ExecuteReader();
                while (r.Read()) toRevoke.Add(r.GetString(0));
            }

            foreach (string hash in toRevoke)
            {
                using var upd = conn.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE account_tokens SET revoked_at = $now WHERE token_hash = $th;";
                upd.Parameters.AddWithValue("$now", nowIso);
                upd.Parameters.AddWithValue("$th", hash);
                upd.ExecuteNonQuery();
            }

            return toRevoke;
        });
    }

    private static AccountTokenRow ReadTokenRow(SqliteDataReader r) => new(
        r.GetString(0),
        Guid.Parse(r.GetString(1)),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        ParseUtc(r.GetString(5)),
        ParseUtc(r.GetString(6)),
        string.IsNullOrWhiteSpace(r.GetString(7)) ? null : ParseUtc(r.GetString(7)));

    // ── 2. 锦标赛大厅、成员与对称对阵表 ──────────────────────────────────────

    public sealed record TournamentLobbyRow(
        string LobbyId,
        string Code,
        string Name,
        string Kind,
        string PlayMode,
        string ModeId,
        string PresetId,
        string MutatorsJson,
        string HostAccountId,
        string HostUsername,
        int PlayerCap,
        bool IsPublic,
        bool HasPassword,
        string PasswordHash,
        bool IsInviteOnly,
        string InviteTokenHash,
        bool ExcludeHostFromGameplay,
        string State,
        string TournamentSessionId,
        int RoundCountdownSeconds,
        int AccelRoundCountdownSeconds,
        string RulesJson,
        string BannedEncounterIdsJson,
        string RoundBarriersJson,
        string MatchmakerStateJson,
        DateTime CreatedAt,
        DateTime? StartedAt,
        DateTime ExpiresAt,
        DateTime? EndedAt,
        DateTime UpdatedAt);

    public sealed record TournamentLobbyMemberRow(
        string LobbyId,
        string AccountId,
        string Username,
        string Hero,
        bool IsAi,
        string MemberState,
        string ConnectionState,
        DateTime? AiTakeoverSince,
        int MissedPvpDays,
        uint CurrentDay,
        uint CurrentHour,
        bool IsReadyH6Pvp,
        bool HasFinishedRun,
        bool IsChampion,
        bool IsEliminated,
        bool IsConceded,
        int FinalPlace,
        int Victories,
        int Defeats,
        int Prestige,
        uint FinalDay,
        string RemovalReason,
        DateTime JoinedAt,
        DateTime LastSeenAt);

    public sealed record TournamentMatchupRow(
        string LobbyId,
        uint Day,
        string PairKey,
        string P1AccountId,
        string P1Username,
        bool P1IsBot,
        string P2AccountId,
        string P2Username,
        bool P2IsBot,
        bool IsSyntheticSparring,
        int BattleSeed,
        string P1LockedBoardJson,
        string P2LockedBoardJson,
        string WinnerAccountId,
        string LoserAccountId,
        int LoserPrestigeDamage,
        bool IsAutoResolved,
        string ReplayId,
        DateTime? ResolvedAt);

    /// <summary>原子保存整个锦标赛大厅及全部当前成员状态快照（重启可 100% 还原）。</summary>
    public void SaveTournamentLobbyWithMembers(TournamentLobbyRow lobby, IReadOnlyList<TournamentLobbyMemberRow> members)
    {
        ExecuteWrite((conn, tx) =>
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tournament_lobbies (
                        lobby_id, code, name, kind, play_mode, mode_id, preset_id, mutators_json,
                        host_account_id, host_username, player_cap, is_public,
                        has_password, password_hash, is_invite_only, invite_token_hash,
                        exclude_host_from_gameplay, state, tournament_session_id,
                        round_countdown_seconds, accel_round_countdown_seconds,
                        rules_json, banned_encounter_ids_json, round_barriers_json, matchmaker_state_json,
                        created_at, started_at, expires_at, ended_at, updated_at
                    ) VALUES (
                        $lid, $code, $name, $kind, $pm, $mid, $pid, $mut,
                        $haid, $huser, $cap, $pub,
                        $hpw, $pwh, $inv, $invh,
                        $exh, $st, $tsid,
                        $rcs, $arcs,
                        $rjson, $bjson, $rbjson, $mmjson,
                        $ca, $sa, $ea, $enda, $ua
                    )
                    ON CONFLICT(lobby_id) DO UPDATE SET
                        name = excluded.name,
                        kind = excluded.kind,
                        play_mode = excluded.play_mode,
                        mode_id = excluded.mode_id,
                        preset_id = excluded.preset_id,
                        mutators_json = excluded.mutators_json,
                        host_account_id = excluded.host_account_id,
                        host_username = excluded.host_username,
                        player_cap = excluded.player_cap,
                        is_public = excluded.is_public,
                        has_password = excluded.has_password,
                        password_hash = excluded.password_hash,
                        is_invite_only = excluded.is_invite_only,
                        invite_token_hash = excluded.invite_token_hash,
                        state = excluded.state,
                        tournament_session_id = excluded.tournament_session_id,
                        round_countdown_seconds = excluded.round_countdown_seconds,
                        accel_round_countdown_seconds = excluded.accel_round_countdown_seconds,
                        rules_json = excluded.rules_json,
                        banned_encounter_ids_json = excluded.banned_encounter_ids_json,
                        round_barriers_json = excluded.round_barriers_json,
                        matchmaker_state_json = excluded.matchmaker_state_json,
                        started_at = excluded.started_at,
                        expires_at = excluded.expires_at,
                        ended_at = excluded.ended_at,
                        updated_at = excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("$lid", lobby.LobbyId);
                cmd.Parameters.AddWithValue("$code", lobby.Code);
                cmd.Parameters.AddWithValue("$name", lobby.Name);
                cmd.Parameters.AddWithValue("$kind", lobby.Kind);
                cmd.Parameters.AddWithValue("$pm", lobby.PlayMode);
                cmd.Parameters.AddWithValue("$mid", lobby.ModeId);
                cmd.Parameters.AddWithValue("$pid", lobby.PresetId);
                cmd.Parameters.AddWithValue("$mut", lobby.MutatorsJson ?? "[]");
                cmd.Parameters.AddWithValue("$haid", lobby.HostAccountId);
                cmd.Parameters.AddWithValue("$huser", lobby.HostUsername);
                cmd.Parameters.AddWithValue("$cap", lobby.PlayerCap);
                cmd.Parameters.AddWithValue("$pub", lobby.IsPublic ? 1 : 0);
                cmd.Parameters.AddWithValue("$hpw", lobby.HasPassword ? 1 : 0);
                cmd.Parameters.AddWithValue("$pwh", lobby.PasswordHash ?? "");
                cmd.Parameters.AddWithValue("$inv", lobby.IsInviteOnly ? 1 : 0);
                cmd.Parameters.AddWithValue("$invh", lobby.InviteTokenHash ?? "");
                cmd.Parameters.AddWithValue("$exh", lobby.ExcludeHostFromGameplay ? 1 : 0);
                cmd.Parameters.AddWithValue("$st", lobby.State);
                cmd.Parameters.AddWithValue("$tsid", lobby.TournamentSessionId ?? "");
                cmd.Parameters.AddWithValue("$rcs", lobby.RoundCountdownSeconds);
                cmd.Parameters.AddWithValue("$arcs", lobby.AccelRoundCountdownSeconds);
                cmd.Parameters.AddWithValue("$rjson", lobby.RulesJson ?? "{}");
                cmd.Parameters.AddWithValue("$bjson", lobby.BannedEncounterIdsJson ?? "[]");
                cmd.Parameters.AddWithValue("$rbjson", lobby.RoundBarriersJson ?? "{}");
                cmd.Parameters.AddWithValue("$mmjson", lobby.MatchmakerStateJson ?? "{}");
                cmd.Parameters.AddWithValue("$ca", lobby.CreatedAt.ToString("O"));
                cmd.Parameters.AddWithValue("$sa", lobby.StartedAt.HasValue ? lobby.StartedAt.Value.ToString("O") : "");
                cmd.Parameters.AddWithValue("$ea", lobby.ExpiresAt.ToString("O"));
                cmd.Parameters.AddWithValue("$enda", lobby.EndedAt.HasValue ? lobby.EndedAt.Value.ToString("O") : "");
                cmd.Parameters.AddWithValue("$ua", lobby.UpdatedAt.ToString("O"));
                cmd.ExecuteNonQuery();
            }

            // 同步成员表：先删除不在当前成员列表中的旧记录，再逐条 Upsert
            var activeIds = new HashSet<string>(members.Select(m => m.AccountId), StringComparer.OrdinalIgnoreCase);
            var existingIds = new List<string>();
            using (var sel = conn.CreateCommand())
            {
                sel.Transaction = tx;
                sel.CommandText = "SELECT account_id FROM tournament_lobby_members WHERE lobby_id = $lid;";
                sel.Parameters.AddWithValue("$lid", lobby.LobbyId);
                using var r = sel.ExecuteReader();
                while (r.Read()) existingIds.Add(r.GetString(0));
            }

            foreach (string oldAid in existingIds)
            {
                if (!activeIds.Contains(oldAid))
                {
                    using var del = conn.CreateCommand();
                    del.Transaction = tx;
                    del.CommandText = "DELETE FROM tournament_lobby_members WHERE lobby_id = $lid AND account_id = $aid;";
                    del.Parameters.AddWithValue("$lid", lobby.LobbyId);
                    del.Parameters.AddWithValue("$aid", oldAid);
                    del.ExecuteNonQuery();
                }
            }

            foreach (var m in members)
            {
                using var up = conn.CreateCommand();
                up.Transaction = tx;
                up.CommandText = """
                    INSERT INTO tournament_lobby_members (
                        lobby_id, account_id, username, hero, is_ai,
                        member_state, connection_state, ai_takeover_since, missed_pvp_days,
                        current_day, current_hour, is_ready_h6_pvp, has_finished_run,
                        is_champion, is_eliminated, is_conceded, final_place,
                        victories, defeats, prestige, final_day, removal_reason, joined_at, last_seen_at
                    ) VALUES (
                        $lid, $aid, $u, $h, $ai,
                        $ms, $cs, $ats, $mpd,
                        $cd, $ch, $rh6, $hfr,
                        $ic, $ie, $ico, $fp,
                        $v, $d, $p, $fd, $rr, $ja, $lsa
                    )
                    ON CONFLICT(lobby_id, account_id) DO UPDATE SET
                        username = excluded.username,
                        hero = excluded.hero,
                        member_state = excluded.member_state,
                        connection_state = excluded.connection_state,
                        ai_takeover_since = excluded.ai_takeover_since,
                        missed_pvp_days = excluded.missed_pvp_days,
                        current_day = excluded.current_day,
                        current_hour = excluded.current_hour,
                        is_ready_h6_pvp = excluded.is_ready_h6_pvp,
                        has_finished_run = excluded.has_finished_run,
                        is_champion = excluded.is_champion,
                        is_eliminated = excluded.is_eliminated,
                        is_conceded = excluded.is_conceded,
                        final_place = excluded.final_place,
                        victories = excluded.victories,
                        defeats = excluded.defeats,
                        prestige = excluded.prestige,
                        final_day = excluded.final_day,
                        removal_reason = excluded.removal_reason,
                        last_seen_at = excluded.last_seen_at;
                    """;
                up.Parameters.AddWithValue("$lid", m.LobbyId);
                up.Parameters.AddWithValue("$aid", m.AccountId);
                up.Parameters.AddWithValue("$u", m.Username);
                up.Parameters.AddWithValue("$h", m.Hero);
                up.Parameters.AddWithValue("$ai", m.IsAi ? 1 : 0);
                up.Parameters.AddWithValue("$ms", m.MemberState);
                up.Parameters.AddWithValue("$cs", m.ConnectionState);
                up.Parameters.AddWithValue("$ats", m.AiTakeoverSince.HasValue ? m.AiTakeoverSince.Value.ToString("O") : "");
                up.Parameters.AddWithValue("$mpd", m.MissedPvpDays);
                up.Parameters.AddWithValue("$cd", (int)m.CurrentDay);
                up.Parameters.AddWithValue("$ch", (int)m.CurrentHour);
                up.Parameters.AddWithValue("$rh6", m.IsReadyH6Pvp ? 1 : 0);
                up.Parameters.AddWithValue("$hfr", m.HasFinishedRun ? 1 : 0);
                up.Parameters.AddWithValue("$ic", m.IsChampion ? 1 : 0);
                up.Parameters.AddWithValue("$ie", m.IsEliminated ? 1 : 0);
                up.Parameters.AddWithValue("$ico", m.IsConceded ? 1 : 0);
                up.Parameters.AddWithValue("$fp", m.FinalPlace);
                up.Parameters.AddWithValue("$v", m.Victories);
                up.Parameters.AddWithValue("$d", m.Defeats);
                up.Parameters.AddWithValue("$p", m.Prestige);
                up.Parameters.AddWithValue("$fd", (int)m.FinalDay);
                up.Parameters.AddWithValue("$rr", m.RemovalReason ?? "");
                up.Parameters.AddWithValue("$ja", m.JoinedAt.ToString("O"));
                up.Parameters.AddWithValue("$lsa", m.LastSeenAt.ToString("O"));
                up.ExecuteNonQuery();
            }
        });
    }

    /// <summary>加载全部未取消、未过期的活跃锦标赛大厅与成员快照（用于服务重启热恢复）。</summary>
    public List<(TournamentLobbyRow Lobby, List<TournamentLobbyMemberRow> Members)> LoadActiveTournamentLobbies(DateTime? nowUtc = null)
    {
        string nowIso = (nowUtc ?? DateTime.UtcNow).ToString("O");
        return ExecuteRead(conn =>
        {
            var lobbies = new List<TournamentLobbyRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT lobby_id, code, name, kind, play_mode, mode_id, preset_id, mutators_json,
                           host_account_id, host_username, player_cap, is_public,
                           has_password, password_hash, is_invite_only, invite_token_hash,
                           exclude_host_from_gameplay, state, tournament_session_id,
                           round_countdown_seconds, accel_round_countdown_seconds,
                           rules_json, banned_encounter_ids_json, round_barriers_json, matchmaker_state_json,
                           created_at, started_at, expires_at, ended_at, updated_at
                    FROM tournament_lobbies
                    WHERE state IN ('waiting', 'countdown', 'started')
                      AND expires_at > $now
                    ORDER BY created_at DESC;
                    """;
                cmd.Parameters.AddWithValue("$now", nowIso);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    lobbies.Add(new TournamentLobbyRow(
                        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                        r.GetString(5), r.GetString(6), r.GetString(7),
                        r.GetString(8), r.GetString(9), r.GetInt32(10), r.GetInt32(11) != 0,
                        r.GetInt32(12) != 0, r.GetString(13), r.GetInt32(14) != 0, r.GetString(15),
                        r.GetInt32(16) != 0, r.GetString(17), r.GetString(18),
                        r.GetInt32(19), r.GetInt32(20),
                        r.GetString(21), r.GetString(22), r.GetString(23), r.GetString(24),
                        ParseUtc(r.GetString(25)),
                        string.IsNullOrWhiteSpace(r.GetString(26)) ? null : ParseUtc(r.GetString(26)),
                        ParseUtc(r.GetString(27)),
                        string.IsNullOrWhiteSpace(r.GetString(28)) ? null : ParseUtc(r.GetString(28)),
                        ParseUtc(r.GetString(29))));
                }
            }

            var result = new List<(TournamentLobbyRow, List<TournamentLobbyMemberRow>)>(lobbies.Count);
            foreach (var l in lobbies)
            {
                var members = new List<TournamentLobbyMemberRow>();
                using var mCmd = conn.CreateCommand();
                mCmd.CommandText = """
                    SELECT lobby_id, account_id, username, hero, is_ai,
                           member_state, connection_state, ai_takeover_since, missed_pvp_days,
                           current_day, current_hour, is_ready_h6_pvp, has_finished_run,
                           is_champion, is_eliminated, is_conceded, final_place,
                           victories, defeats, prestige, final_day, removal_reason, joined_at, last_seen_at
                    FROM tournament_lobby_members
                    WHERE lobby_id = $lid
                    ORDER BY joined_at ASC;
                    """;
                mCmd.Parameters.AddWithValue("$lid", l.LobbyId);
                using var mr = mCmd.ExecuteReader();
                while (mr.Read())
                {
                    members.Add(new TournamentLobbyMemberRow(
                        mr.GetString(0), mr.GetString(1), mr.GetString(2), mr.GetString(3), mr.GetInt32(4) != 0,
                        mr.GetString(5), mr.GetString(6),
                        string.IsNullOrWhiteSpace(mr.GetString(7)) ? null : ParseUtc(mr.GetString(7)),
                        mr.GetInt32(8),
                        (uint)Math.Max(1, mr.GetInt32(9)), (uint)Math.Max(1, mr.GetInt32(10)),
                        mr.GetInt32(11) != 0, mr.GetInt32(12) != 0,
                        mr.GetInt32(13) != 0, mr.GetInt32(14) != 0, mr.GetInt32(15) != 0, mr.GetInt32(16),
                        mr.GetInt32(17), mr.GetInt32(18), mr.GetInt32(19), (uint)Math.Max(1, mr.GetInt32(20)),
                        mr.GetString(21), ParseUtc(mr.GetString(22)), ParseUtc(mr.GetString(23))));
                }
                result.Add((l, members));
            }
            return result;
        });
    }

    public void UpsertTournamentMatchup(TournamentMatchupRow row)
    {
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO tournament_matchups (
                    lobby_id, day, pair_key,
                    p1_account_id, p1_username, p1_is_bot,
                    p2_account_id, p2_username, p2_is_bot,
                    is_synthetic_sparring, battle_seed,
                    p1_locked_board_json, p2_locked_board_json,
                    winner_account_id, loser_account_id, loser_prestige_damage,
                    is_auto_resolved, replay_id, resolved_at
                ) VALUES (
                    $lid, $day, $pk,
                    $p1id, $p1u, $p1b,
                    $p2id, $p2u, $p2b,
                    $syn, $seed,
                    $p1lb, $p2lb,
                    $win, $lose, $dmg,
                    $auto, $rep, $res
                )
                ON CONFLICT(lobby_id, day, pair_key) DO UPDATE SET
                    p1_locked_board_json = CASE WHEN excluded.p1_locked_board_json != '' THEN excluded.p1_locked_board_json ELSE tournament_matchups.p1_locked_board_json END,
                    p2_locked_board_json = CASE WHEN excluded.p2_locked_board_json != '' THEN excluded.p2_locked_board_json ELSE tournament_matchups.p2_locked_board_json END,
                    winner_account_id = CASE WHEN tournament_matchups.winner_account_id != '' THEN tournament_matchups.winner_account_id ELSE excluded.winner_account_id END,
                    loser_account_id = CASE WHEN tournament_matchups.loser_account_id != '' THEN tournament_matchups.loser_account_id ELSE excluded.loser_account_id END,
                    loser_prestige_damage = CASE WHEN tournament_matchups.winner_account_id != '' THEN tournament_matchups.loser_prestige_damage ELSE excluded.loser_prestige_damage END,
                    is_auto_resolved = CASE WHEN tournament_matchups.winner_account_id != '' THEN tournament_matchups.is_auto_resolved ELSE excluded.is_auto_resolved END,
                    replay_id = CASE WHEN tournament_matchups.replay_id != '' THEN tournament_matchups.replay_id ELSE excluded.replay_id END,
                    resolved_at = CASE WHEN tournament_matchups.resolved_at != '' THEN tournament_matchups.resolved_at ELSE excluded.resolved_at END;
                """;
            cmd.Parameters.AddWithValue("$lid", row.LobbyId);
            cmd.Parameters.AddWithValue("$day", (int)row.Day);
            cmd.Parameters.AddWithValue("$pk", row.PairKey);
            cmd.Parameters.AddWithValue("$p1id", row.P1AccountId);
            cmd.Parameters.AddWithValue("$p1u", row.P1Username);
            cmd.Parameters.AddWithValue("$p1b", row.P1IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("$p2id", row.P2AccountId);
            cmd.Parameters.AddWithValue("$p2u", row.P2Username);
            cmd.Parameters.AddWithValue("$p2b", row.P2IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("$syn", row.IsSyntheticSparring ? 1 : 0);
            cmd.Parameters.AddWithValue("$seed", row.BattleSeed);
            cmd.Parameters.AddWithValue("$p1lb", row.P1LockedBoardJson ?? "");
            cmd.Parameters.AddWithValue("$p2lb", row.P2LockedBoardJson ?? "");
            cmd.Parameters.AddWithValue("$win", row.WinnerAccountId ?? "");
            cmd.Parameters.AddWithValue("$lose", row.LoserAccountId ?? "");
            cmd.Parameters.AddWithValue("$dmg", row.LoserPrestigeDamage);
            cmd.Parameters.AddWithValue("$auto", row.IsAutoResolved ? 1 : 0);
            cmd.Parameters.AddWithValue("$rep", row.ReplayId ?? "");
            cmd.Parameters.AddWithValue("$res", row.ResolvedAt.HasValue ? row.ResolvedAt.Value.ToString("O") : "");
            cmd.ExecuteNonQuery();
        });
    }

    public TournamentMatchupRow? LoadTournamentMatchup(string lobbyId, uint day, string pairKey)
    {
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT lobby_id, day, pair_key, p1_account_id, p1_username, p1_is_bot,
                       p2_account_id, p2_username, p2_is_bot, is_synthetic_sparring, battle_seed,
                       p1_locked_board_json, p2_locked_board_json,
                       winner_account_id, loser_account_id, loser_prestige_damage,
                       is_auto_resolved, replay_id, resolved_at
                FROM tournament_matchups
                WHERE lobby_id = $lid AND day = $day AND pair_key = $pk LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$lid", lobbyId);
            cmd.Parameters.AddWithValue("$day", (int)day);
            cmd.Parameters.AddWithValue("$pk", pairKey);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadMatchupRow(r) : null;
        });
    }

    public List<TournamentMatchupRow> LoadTournamentMatchupsByLobby(string lobbyId)
    {
        return ExecuteRead(conn =>
        {
            var list = new List<TournamentMatchupRow>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT lobby_id, day, pair_key, p1_account_id, p1_username, p1_is_bot,
                       p2_account_id, p2_username, p2_is_bot, is_synthetic_sparring, battle_seed,
                       p1_locked_board_json, p2_locked_board_json,
                       winner_account_id, loser_account_id, loser_prestige_damage,
                       is_auto_resolved, replay_id, resolved_at
                FROM tournament_matchups
                WHERE lobby_id = $lid
                ORDER BY day ASC, pair_key ASC;
                """;
            cmd.Parameters.AddWithValue("$lid", lobbyId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadMatchupRow(r));
            return list;
        });
    }

    private static TournamentMatchupRow ReadMatchupRow(SqliteDataReader r) => new(
        r.GetString(0),
        (uint)r.GetInt32(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetInt32(5) != 0,
        r.GetString(6),
        r.GetString(7),
        r.GetInt32(8) != 0,
        r.GetInt32(9) != 0,
        r.GetInt32(10),
        r.GetString(11),
        r.GetString(12),
        r.GetString(13),
        r.GetString(14),
        r.GetInt32(15),
        r.GetInt32(16) != 0,
        r.GetString(17),
        string.IsNullOrWhiteSpace(r.GetString(18)) ? null : ParseUtc(r.GetString(18)));

    // ── 3. 多模式排行榜表 (mode_leaderboards) ────────────────────────────────

    public sealed record LeaderboardEntryRow(
        long Id,
        string BoardKey,
        int SeasonId,
        string ModeId,
        string AccountId,
        string Username,
        string Hero,
        string RunId,
        int Score,
        int PrimaryMetric,
        int SecondaryMetric,
        int ElapsedSeconds,
        int DayReached,
        int Victories,
        int Defeats,
        int Prestige,
        bool IsFirstAttempt,
        string MutatorsJson,
        string SummaryBoardJson,
        DateTime AchievedAt);

    public void UpsertLeaderboardEntry(LeaderboardEntryRow row)
    {
        ExecuteWrite((conn, tx) =>
        {
            // 若为每日挑战，检查该账号今天是否已有过一次完赛记录
            bool isFirst = row.IsFirstAttempt;
            if (isFirst && row.BoardKey.StartsWith("daily:", StringComparison.OrdinalIgnoreCase))
            {
                using var chk = conn.CreateCommand();
                chk.Transaction = tx;
                chk.CommandText = "SELECT COUNT(*) FROM mode_leaderboards WHERE board_key = $bk AND account_id = $aid AND run_id != $rid;";
                chk.Parameters.AddWithValue("$bk", row.BoardKey);
                chk.Parameters.AddWithValue("$aid", row.AccountId);
                chk.Parameters.AddWithValue("$rid", row.RunId);
                if (Convert.ToInt32(chk.ExecuteScalar()) > 0)
                    isFirst = false;
            }

            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO mode_leaderboards (
                    board_key, season_id, mode_id, account_id, username, hero, run_id,
                    score, primary_metric, secondary_metric, elapsed_seconds,
                    day_reached, victories, defeats, prestige, is_first_attempt,
                    mutators_json, summary_board_json, achieved_at
                ) VALUES (
                    $bk, $sid, $mid, $aid, $u, $h, $rid,
                    $sc, $pm, $sm, $es,
                    $dr, $v, $d, $p, $ifa,
                    $mj, $sbj, $at
                )
                ON CONFLICT(board_key, run_id) DO UPDATE SET
                    score = MAX(mode_leaderboards.score, excluded.score),
                    primary_metric = MAX(mode_leaderboards.primary_metric, excluded.primary_metric),
                    secondary_metric = MAX(mode_leaderboards.secondary_metric, excluded.secondary_metric),
                    day_reached = MAX(mode_leaderboards.day_reached, excluded.day_reached),
                    victories = MAX(mode_leaderboards.victories, excluded.victories),
                    defeats = excluded.defeats,
                    prestige = excluded.prestige,
                    summary_board_json = CASE WHEN excluded.summary_board_json != '{}' THEN excluded.summary_board_json ELSE mode_leaderboards.summary_board_json END;
                """;
            cmd.Parameters.AddWithValue("$bk", row.BoardKey);
            cmd.Parameters.AddWithValue("$sid", row.SeasonId);
            cmd.Parameters.AddWithValue("$mid", row.ModeId);
            cmd.Parameters.AddWithValue("$aid", row.AccountId);
            cmd.Parameters.AddWithValue("$u", row.Username);
            cmd.Parameters.AddWithValue("$h", row.Hero);
            cmd.Parameters.AddWithValue("$rid", row.RunId);
            cmd.Parameters.AddWithValue("$sc", row.Score);
            cmd.Parameters.AddWithValue("$pm", row.PrimaryMetric);
            cmd.Parameters.AddWithValue("$sm", row.SecondaryMetric);
            cmd.Parameters.AddWithValue("$es", Math.Max(0, row.ElapsedSeconds));
            cmd.Parameters.AddWithValue("$dr", row.DayReached);
            cmd.Parameters.AddWithValue("$v", row.Victories);
            cmd.Parameters.AddWithValue("$d", row.Defeats);
            cmd.Parameters.AddWithValue("$p", row.Prestige);
            cmd.Parameters.AddWithValue("$ifa", isFirst ? 1 : 0);
            cmd.Parameters.AddWithValue("$mj", string.IsNullOrWhiteSpace(row.MutatorsJson) ? "[]" : row.MutatorsJson);
            cmd.Parameters.AddWithValue("$sbj", string.IsNullOrWhiteSpace(row.SummaryBoardJson) ? "{}" : row.SummaryBoardJson);
            cmd.Parameters.AddWithValue("$at", row.AchievedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }

    public (List<LeaderboardEntryRow> Items, int Total) QueryLeaderboard(
        string boardKey,
        string? heroFilter = null,
        bool firstAttemptOnly = false,
        int page = 1,
        int limit = 50)
    {
        int safePage = Math.Max(1, page);
        int safeLimit = Math.Clamp(limit, 1, 100);
        int offset = (safePage - 1) * safeLimit;

        return ExecuteRead(conn =>
        {
            string where = "WHERE board_key = $bk";
            if (!string.IsNullOrWhiteSpace(heroFilter))
                where += " AND LOWER(hero) = LOWER($hero)";
            if (firstAttemptOnly)
                where += " AND is_first_attempt = 1";

            int total;
            using (var cnt = conn.CreateCommand())
            {
                cnt.CommandText = $"SELECT COUNT(*) FROM mode_leaderboards {where};";
                cnt.Parameters.AddWithValue("$bk", boardKey);
                if (!string.IsNullOrWhiteSpace(heroFilter))
                    cnt.Parameters.AddWithValue("$hero", heroFilter.Trim());
                total = Convert.ToInt32(cnt.ExecuteScalar());
            }

            var items = new List<LeaderboardEntryRow>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT id, board_key, season_id, mode_id, account_id, username, hero, run_id,
                           score, primary_metric, secondary_metric, elapsed_seconds,
                           day_reached, victories, defeats, prestige, is_first_attempt,
                           mutators_json, summary_board_json, achieved_at
                    FROM mode_leaderboards
                    {where}
                    ORDER BY score DESC, primary_metric DESC, secondary_metric DESC, elapsed_seconds ASC, achieved_at ASC
                    LIMIT $lim OFFSET $off;
                    """;
                cmd.Parameters.AddWithValue("$bk", boardKey);
                if (!string.IsNullOrWhiteSpace(heroFilter))
                    cmd.Parameters.AddWithValue("$hero", heroFilter.Trim());
                cmd.Parameters.AddWithValue("$lim", safeLimit);
                cmd.Parameters.AddWithValue("$off", offset);
                using var r = cmd.ExecuteReader();
                while (r.Read()) items.Add(ReadLeaderboardRow(r));
            }
            return (items, total);
        });
    }

    public (LeaderboardEntryRow? Best, int Rank) QueryAccountLeaderboardBest(
        string boardKey,
        string accountId,
        string? heroFilter = null,
        bool firstAttemptOnly = false)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return (null, 0);
        return ExecuteRead<(LeaderboardEntryRow?, int)>(conn =>
        {
            string extraWhere = "";
            if (!string.IsNullOrWhiteSpace(heroFilter))
                extraWhere += " AND LOWER(hero) = LOWER($hero)";
            if (firstAttemptOnly)
                extraWhere += " AND is_first_attempt = 1";

            LeaderboardEntryRow? best = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    SELECT id, board_key, season_id, mode_id, account_id, username, hero, run_id,
                           score, primary_metric, secondary_metric, elapsed_seconds,
                           day_reached, victories, defeats, prestige, is_first_attempt,
                           mutators_json, summary_board_json, achieved_at
                    FROM mode_leaderboards
                    WHERE board_key = $bk AND LOWER(account_id) = LOWER($aid){extraWhere}
                    ORDER BY score DESC, primary_metric DESC, secondary_metric DESC, elapsed_seconds ASC, achieved_at ASC
                    LIMIT 1;
                    """;
                cmd.Parameters.AddWithValue("$bk", boardKey);
                cmd.Parameters.AddWithValue("$aid", accountId);
                if (!string.IsNullOrWhiteSpace(heroFilter))
                    cmd.Parameters.AddWithValue("$hero", heroFilter.Trim());
                using var r = cmd.ExecuteReader();
                if (r.Read()) best = ReadLeaderboardRow(r);
            }

            if (best == null) return (null, 0);

            using var rankCmd = conn.CreateCommand();
            rankCmd.CommandText = $"""
                SELECT COUNT(*) + 1
                FROM mode_leaderboards
                WHERE board_key = $bk{extraWhere}
                  AND (
                      score > $sc
                      OR (score = $sc AND primary_metric > $pm)
                      OR (score = $sc AND primary_metric = $pm AND secondary_metric > $sm)
                      OR (score = $sc AND primary_metric = $pm AND secondary_metric = $sm AND elapsed_seconds < $es)
                      OR (score = $sc AND primary_metric = $pm AND secondary_metric = $sm AND elapsed_seconds = $es AND achieved_at < $at)
                  );
                """;
            rankCmd.Parameters.AddWithValue("$bk", boardKey);
            if (!string.IsNullOrWhiteSpace(heroFilter))
                rankCmd.Parameters.AddWithValue("$hero", heroFilter.Trim());
            rankCmd.Parameters.AddWithValue("$sc", best.Score);
            rankCmd.Parameters.AddWithValue("$pm", best.PrimaryMetric);
            rankCmd.Parameters.AddWithValue("$sm", best.SecondaryMetric);
            rankCmd.Parameters.AddWithValue("$es", best.ElapsedSeconds);
            rankCmd.Parameters.AddWithValue("$at", best.AchievedAt.ToString("O"));
            int rank = Convert.ToInt32(rankCmd.ExecuteScalar());
            return (best, rank);
        });
    }

    private static LeaderboardEntryRow ReadLeaderboardRow(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetInt32(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        r.GetInt32(8),
        r.GetInt32(9),
        r.GetInt32(10),
        r.GetInt32(11),
        r.GetInt32(12),
        r.GetInt32(13),
        r.GetInt32(14),
        r.GetInt32(15),
        r.GetInt32(16) != 0,
        r.GetString(17),
        r.GetString(18),
        ParseUtc(r.GetString(19)));

    // ── 4. 历史战报与压缩战斗回放表 (match_history_and_replays) ──────────────

    public sealed record ReplayRow(
        string ReplayId,
        string RunId,
        string LobbyId,
        string TournamentSessionId,
        string ModeId,
        string CombatType,
        int Day,
        int Hour,
        int BattleSeed,
        string P1AccountId,
        string P1Username,
        string P1Hero,
        string P2AccountId,
        string P2Username,
        string P2Hero,
        string WinnerSide,
        int DurationMs,
        int P1RemainingHp,
        int P2RemainingHp,
        int P1DamageDealt,
        int P2DamageDealt,
        string P1BoardJson,
        string P2BoardJson,
        string TimelineSummaryJson,
        bool HasCombatSimBlob,
        DateTime CreatedAt);

    public static byte[]? CompressReplayPayload(byte[]? rawMsgPack)
    {
        if (rawMsgPack == null || rawMsgPack.Length == 0) return null;
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(rawMsgPack, 0, rawMsgPack.Length);
        }
        byte[] compressed = ms.ToArray();
        return compressed.Length <= MaxReplayBlobBytes ? compressed : null;
    }

    public static byte[]? DecompressReplayPayload(byte[]? compressed)
    {
        if (compressed == null || compressed.Length == 0) return null;
        using var input = new MemoryStream(compressed);
        using var gz = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16384];
        int total = 0;
        const int maxDecompressedBytes = 8 * 1024 * 1024;
        while (true)
        {
            int read = gz.Read(buffer, 0, buffer.Length);
            if (read <= 0) break;
            total += read;
            if (total > maxDecompressedBytes)
                throw new InvalidDataException("战斗回放解压体积超过安全上限。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void SaveReplay(ReplayRow row, byte[]? rawCombatSimMsgPack = null)
    {
        byte[]? gzBlob = CompressReplayPayload(rawCombatSimMsgPack);
        ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO match_history_and_replays (
                    replay_id, run_id, lobby_id, tournament_session_id, mode_id, combat_type,
                    day, hour, battle_seed,
                    p1_account_id, p1_username, p1_hero,
                    p2_account_id, p2_username, p2_hero,
                    winner_side, duration_ms, p1_remaining_hp, p2_remaining_hp,
                    p1_damage_dealt, p2_damage_dealt,
                    p1_board_json, p2_board_json, timeline_summary_json, combat_sim_gzip, created_at
                ) VALUES (
                    $rep, $run, $lid, $tsid, $mid, $ct,
                    $d, $h, $seed,
                    $p1id, $p1u, $p1h,
                    $p2id, $p2u, $p2h,
                    $ws, $dur, $p1hp, $p2hp,
                    $p1dmg, $p2dmg,
                    $p1b, $p2b, $tsum, $blob, $ca
                )
                ON CONFLICT(replay_id) DO UPDATE SET
                    timeline_summary_json = excluded.timeline_summary_json,
                    combat_sim_gzip = COALESCE(excluded.combat_sim_gzip, match_history_and_replays.combat_sim_gzip);
                """;
            cmd.Parameters.AddWithValue("$rep", row.ReplayId);
            cmd.Parameters.AddWithValue("$run", row.RunId ?? "");
            cmd.Parameters.AddWithValue("$lid", row.LobbyId ?? "");
            cmd.Parameters.AddWithValue("$tsid", row.TournamentSessionId ?? "");
            cmd.Parameters.AddWithValue("$mid", string.IsNullOrWhiteSpace(row.ModeId) ? "standard" : row.ModeId);
            cmd.Parameters.AddWithValue("$ct", string.IsNullOrWhiteSpace(row.CombatType) ? "PVP" : row.CombatType);
            cmd.Parameters.AddWithValue("$d", row.Day);
            cmd.Parameters.AddWithValue("$h", row.Hour);
            cmd.Parameters.AddWithValue("$seed", row.BattleSeed);
            cmd.Parameters.AddWithValue("$p1id", row.P1AccountId ?? "");
            cmd.Parameters.AddWithValue("$p1u", row.P1Username ?? "");
            cmd.Parameters.AddWithValue("$p1h", row.P1Hero ?? "Vanessa");
            cmd.Parameters.AddWithValue("$p2id", row.P2AccountId ?? "");
            cmd.Parameters.AddWithValue("$p2u", row.P2Username ?? "");
            cmd.Parameters.AddWithValue("$p2h", row.P2Hero ?? "Vanessa");
            cmd.Parameters.AddWithValue("$ws", row.WinnerSide ?? "Player1");
            cmd.Parameters.AddWithValue("$dur", row.DurationMs);
            cmd.Parameters.AddWithValue("$p1hp", row.P1RemainingHp);
            cmd.Parameters.AddWithValue("$p2hp", row.P2RemainingHp);
            cmd.Parameters.AddWithValue("$p1dmg", row.P1DamageDealt);
            cmd.Parameters.AddWithValue("$p2dmg", row.P2DamageDealt);
            cmd.Parameters.AddWithValue("$p1b", string.IsNullOrWhiteSpace(row.P1BoardJson) ? "{}" : row.P1BoardJson);
            cmd.Parameters.AddWithValue("$p2b", string.IsNullOrWhiteSpace(row.P2BoardJson) ? "{}" : row.P2BoardJson);
            cmd.Parameters.AddWithValue("$tsum", string.IsNullOrWhiteSpace(row.TimelineSummaryJson) ? "{}" : row.TimelineSummaryJson);
            cmd.Parameters.AddWithValue("$blob", gzBlob != null ? gzBlob : DBNull.Value);
            cmd.Parameters.AddWithValue("$ca", row.CreatedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        });
    }

    public List<ReplayRow> QueryReplaysByAccount(string accountId, string? modeId = null, string? cursorCreatedAt = null, int limit = 20)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        return ExecuteRead(conn =>
        {
            var list = new List<ReplayRow>();
            using var cmd = conn.CreateCommand();
            string where = "WHERE (LOWER(p1_account_id) = LOWER($aid) OR LOWER(p2_account_id) = LOWER($aid))";
            cmd.Parameters.AddWithValue("$aid", accountId);
            if (!string.IsNullOrWhiteSpace(modeId))
            {
                where += " AND LOWER(mode_id) = LOWER($mid)";
                cmd.Parameters.AddWithValue("$mid", modeId.Trim());
            }
            if (!string.IsNullOrWhiteSpace(cursorCreatedAt))
            {
                where += " AND created_at < $cur";
                cmd.Parameters.AddWithValue("$cur", cursorCreatedAt.Trim());
            }
            cmd.CommandText = $"""
                SELECT replay_id, run_id, lobby_id, tournament_session_id, mode_id, combat_type,
                       day, hour, battle_seed,
                       p1_account_id, p1_username, p1_hero,
                       p2_account_id, p2_username, p2_hero,
                       winner_side, duration_ms, p1_remaining_hp, p2_remaining_hp,
                       p1_damage_dealt, p2_damage_dealt,
                       p1_board_json, p2_board_json, timeline_summary_json,
                       CASE WHEN combat_sim_gzip IS NOT NULL THEN 1 ELSE 0 END,
                       created_at
                FROM match_history_and_replays
                {where}
                ORDER BY created_at DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", safeLimit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadReplayRow(r));
            return list;
        });
    }

    public List<ReplayRow> QueryReplaysByLobby(string lobbyId, int? day = null, int limit = 50)
    {
        int safeLimit = Math.Clamp(limit, 1, 200);
        return ExecuteRead(conn =>
        {
            var list = new List<ReplayRow>();
            using var cmd = conn.CreateCommand();
            string where = "WHERE lobby_id = $lid";
            cmd.Parameters.AddWithValue("$lid", lobbyId);
            if (day.HasValue && day.Value > 0)
            {
                where += " AND day = $day";
                cmd.Parameters.AddWithValue("$day", day.Value);
            }
            cmd.CommandText = $"""
                SELECT replay_id, run_id, lobby_id, tournament_session_id, mode_id, combat_type,
                       day, hour, battle_seed,
                       p1_account_id, p1_username, p1_hero,
                       p2_account_id, p2_username, p2_hero,
                       winner_side, duration_ms, p1_remaining_hp, p2_remaining_hp,
                       p1_damage_dealt, p2_damage_dealt,
                       p1_board_json, p2_board_json, timeline_summary_json,
                       CASE WHEN combat_sim_gzip IS NOT NULL THEN 1 ELSE 0 END,
                       created_at
                FROM match_history_and_replays
                {where}
                ORDER BY day DESC, created_at DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", safeLimit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadReplayRow(r));
            return list;
        });
    }

    public List<ReplayRow> QueryRecentReplays(string? modeId = null, int limit = 20)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        return ExecuteRead(conn =>
        {
            var list = new List<ReplayRow>();
            using var cmd = conn.CreateCommand();
            string where = string.IsNullOrWhiteSpace(modeId) ? "" : "WHERE LOWER(mode_id) = LOWER($mid)";
            if (!string.IsNullOrWhiteSpace(modeId))
                cmd.Parameters.AddWithValue("$mid", modeId.Trim());
            cmd.CommandText = $"""
                SELECT replay_id, run_id, lobby_id, tournament_session_id, mode_id, combat_type,
                       day, hour, battle_seed,
                       p1_account_id, p1_username, p1_hero,
                       p2_account_id, p2_username, p2_hero,
                       winner_side, duration_ms, p1_remaining_hp, p2_remaining_hp,
                       p1_damage_dealt, p2_damage_dealt,
                       p1_board_json, p2_board_json, timeline_summary_json,
                       CASE WHEN combat_sim_gzip IS NOT NULL THEN 1 ELSE 0 END,
                       created_at
                FROM match_history_and_replays
                {where}
                ORDER BY created_at DESC
                LIMIT $lim;
                """;
            cmd.Parameters.AddWithValue("$lim", safeLimit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadReplayRow(r));
            return list;
        });
    }

    public ReplayRow? LoadReplay(string replayId)
    {
        if (string.IsNullOrWhiteSpace(replayId)) return null;
        return ExecuteRead(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT replay_id, run_id, lobby_id, tournament_session_id, mode_id, combat_type,
                       day, hour, battle_seed,
                       p1_account_id, p1_username, p1_hero,
                       p2_account_id, p2_username, p2_hero,
                       winner_side, duration_ms, p1_remaining_hp, p2_remaining_hp,
                       p1_damage_dealt, p2_damage_dealt,
                       p1_board_json, p2_board_json, timeline_summary_json,
                       CASE WHEN combat_sim_gzip IS NOT NULL THEN 1 ELSE 0 END,
                       created_at
                FROM match_history_and_replays
                WHERE replay_id = $rid LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$rid", replayId);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadReplayRow(r) : null;
        });
    }

    public byte[]? LoadReplayMsgPack(string replayId)
    {
        if (string.IsNullOrWhiteSpace(replayId)) return null;
        byte[]? compressed = ExecuteRead<byte[]?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT combat_sim_gzip FROM match_history_and_replays WHERE replay_id = $rid LIMIT 1;";
            cmd.Parameters.AddWithValue("$rid", replayId);
            object? obj = cmd.ExecuteScalar();
            return obj is byte[] bytes ? bytes : null;
        });
        return DecompressReplayPayload(compressed);
    }

    public string? FindLatestReplayIdForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        return ExecuteRead<string?>(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT replay_id FROM match_history_and_replays
                WHERE LOWER(p1_account_id) = LOWER($aid) OR LOWER(p2_account_id) = LOWER($aid)
                ORDER BY created_at DESC LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$aid", accountId);
            return cmd.ExecuteScalar() as string;
        });
    }

    private static ReplayRow ReadReplayRow(SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetInt32(6),
        r.GetInt32(7),
        r.GetInt32(8),
        r.GetString(9),
        r.GetString(10),
        r.GetString(11),
        r.GetString(12),
        r.GetString(13),
        r.GetString(14),
        r.GetString(15),
        r.GetInt32(16),
        r.GetInt32(17),
        r.GetInt32(18),
        r.GetInt32(19),
        r.GetInt32(20),
        r.GetString(21),
        r.GetString(22),
        r.GetString(23),
        r.GetInt32(24) != 0,
        ParseUtc(r.GetString(25)));

    // ── 5. 聊天与系统播报表 (chat_messages) ─────────────────────────────────

    public sealed record ChatMessageRow(
        long Id,
        string ChannelType,
        string ChannelId,
        string MsgType,
        string SenderId,
        string SenderName,
        string Content,
        string MetaJson,
        DateTime CreatedAt);

    public ChatMessageRow InsertChatMessage(
        string channelType,
        string channelId,
        string msgType,
        string senderId,
        string senderName,
        string content,
        string? metaJson = null,
        DateTime? createdAt = null)
    {
        DateTime now = createdAt ?? DateTime.UtcNow;
        string ct = string.IsNullOrWhiteSpace(channelType) ? "global" : channelType.Trim().ToLowerInvariant();
        string cid = string.IsNullOrWhiteSpace(channelId) ? "global" : channelId.Trim();
        string mt = string.IsNullOrWhiteSpace(msgType) ? "chat" : msgType.Trim();
        string mj = string.IsNullOrWhiteSpace(metaJson) ? "{}" : metaJson;

        long id = ExecuteWrite((conn, tx) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO chat_messages (channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at)
                VALUES ($ct, $cid, $mt, $sid, $sn, $c, $mj, $ca);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ct", ct);
            cmd.Parameters.AddWithValue("$cid", cid);
            cmd.Parameters.AddWithValue("$mt", mt);
            cmd.Parameters.AddWithValue("$sid", senderId ?? "");
            cmd.Parameters.AddWithValue("$sn", string.IsNullOrWhiteSpace(senderName) ? "系统" : senderName);
            cmd.Parameters.AddWithValue("$c", content ?? "");
            cmd.Parameters.AddWithValue("$mj", mj);
            cmd.Parameters.AddWithValue("$ca", now.ToString("O"));
            return Convert.ToInt64(cmd.ExecuteScalar());
        });

        return new ChatMessageRow(id, ct, cid, mt, senderId ?? "", string.IsNullOrWhiteSpace(senderName) ? "系统" : senderName, content ?? "", mj, now);
    }

    public List<ChatMessageRow> QueryChatMessages(string channelType, string channelId, long sinceId = 0, int limit = 50)
    {
        int safeLimit = Math.Clamp(limit, 1, 200);
        string ct = string.IsNullOrWhiteSpace(channelType) ? "global" : channelType.Trim().ToLowerInvariant();
        string cid = string.IsNullOrWhiteSpace(channelId) ? "global" : channelId.Trim();

        return ExecuteRead(conn =>
        {
            var list = new List<ChatMessageRow>();
            using var cmd = conn.CreateCommand();
            if (sinceId > 0)
            {
                cmd.CommandText = """
                    SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                    FROM chat_messages
                    WHERE channel_type = $ct AND channel_id = $cid AND id > $since
                    ORDER BY id ASC
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$since", sinceId);
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                    FROM (
                        SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                        FROM chat_messages
                        WHERE channel_type = $ct AND channel_id = $cid
                        ORDER BY id DESC
                        LIMIT $lim
                    ) ORDER BY id ASC;
                    """;
            }
            cmd.Parameters.AddWithValue("$ct", ct);
            cmd.Parameters.AddWithValue("$cid", cid);
            cmd.Parameters.AddWithValue("$lim", safeLimit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadChatRow(r));
            return list;
        });
    }

    public List<ChatMessageRow> QuerySystemAnnouncements(long sinceId = 0, int limit = 30)
    {
        int safeLimit = Math.Clamp(limit, 1, 100);
        return ExecuteRead(conn =>
        {
            var list = new List<ChatMessageRow>();
            using var cmd = conn.CreateCommand();
            if (sinceId > 0)
            {
                cmd.CommandText = """
                    SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                    FROM chat_messages
                    WHERE (channel_type = 'system' OR msg_type LIKE 'system_%') AND id > $since
                    ORDER BY id ASC
                    LIMIT $lim;
                    """;
                cmd.Parameters.AddWithValue("$since", sinceId);
            }
            else
            {
                cmd.CommandText = """
                    SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                    FROM (
                        SELECT id, channel_type, channel_id, msg_type, sender_id, sender_name, content, meta_json, created_at
                        FROM chat_messages
                        WHERE channel_type = 'system' OR msg_type LIKE 'system_%'
                        ORDER BY id DESC
                        LIMIT $lim
                    ) ORDER BY id ASC;
                    """;
            }
            cmd.Parameters.AddWithValue("$lim", safeLimit);
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadChatRow(r));
            return list;
        });
    }

    private static ChatMessageRow ReadChatRow(SqliteDataReader r) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.GetString(2),
        r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        ParseUtc(r.GetString(8)));
}

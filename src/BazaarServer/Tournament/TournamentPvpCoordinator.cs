using System.Collections.Concurrent;
using System.Text.Json;
using BazaarServer.Endpoints;
using BazaarServer.Storage;

namespace BazaarServer.Tournament;

/// <summary>
/// 真人同步 PvP 唯一对称裁决协调器（实现 ITournamentPvpCoordinator）：
/// - 按 (tournamentSessionId, day, accountId) 锁定不可变 Hour 6 盘面快照，100% 消除跨天脏盘面与无锁脏读；
/// - 按 (tournamentSessionId, day, CanonicalPairKey(A,B)) 加细粒度锁并双向落盘至 SQLite tournament_matchups；
/// - 首次提交裁决即成唯一事实，后到的对手直接命中并返回严格对称结果与对应视角 CombatSim，100% 杜绝双胜/双负。
/// </summary>
public sealed class TournamentPvpCoordinator : ITournamentPvpCoordinator
{
    private readonly Persistence _db;
    private readonly LobbyStore _lobbies;

    private readonly ConcurrentDictionary<(Guid Tsid, uint Day, string AccountId), LockedPvpBoard> _lockedBoards = new();
    private readonly ConcurrentDictionary<(Guid Tsid, uint Day, string PairKey), SymmetricHumanPvpOutcome> _resolvedMatches = new();
    private readonly ConcurrentDictionary<(Guid Tsid, uint Day, string PairKey), object> _pairLocks = new();

    public TournamentPvpCoordinator(Persistence db, LobbyStore lobbies)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _lobbies = lobbies ?? throw new ArgumentNullException(nameof(lobbies));
    }

    public void LockPlayerBoardForDay(Guid tournamentSessionId, uint day, LockedPvpBoard board)
    {
        if (tournamentSessionId == Guid.Empty || board == null || string.IsNullOrWhiteSpace(board.AccountId))
            return;

        uint safeDay = day == 0 ? 1u : day;
        string aid = NormalizeAccountKey(board.AccountId);
        var normalized = board with { AccountId = aid, Day = safeDay };

        // 仅允许同一天的首次冻结或覆盖同一天快照（绝不跨天混写）
        _lockedBoards[(tournamentSessionId, safeDay, aid)] = normalized;

        // 尝试持久化至 tournament_matchups 对应槽位（若该天已冻结配对）
        var lobby = _lobbies.FindByTournamentSessionId(tournamentSessionId);
        if (lobby != null)
        {
            var assignment = lobby.Matchmaker.GetAssignment(aid, safeDay);
            if (assignment != null)
            {
                string pairKey = TournamentMatchmaker.CanonicalPairKey(aid, assignment.OpponentAccountId);
                string p1 = CanonicalFirst(aid, assignment.OpponentAccountId);
                string p2 = CanonicalSecond(aid, assignment.OpponentAccountId);
                bool selfIsP1 = string.Equals(aid, p1, StringComparison.OrdinalIgnoreCase);
                string lockedJson = JsonSerializer.Serialize(normalized);

                var existing = _db.LoadTournamentMatchup(lobby.LobbyId, safeDay, pairKey);
                string p1Name = selfIsP1 ? normalized.Username : (existing?.P1Username ?? assignment.OpponentUsername);
                string p2Name = !selfIsP1 ? normalized.Username : (existing?.P2Username ?? assignment.OpponentUsername);
                bool p1IsBot = selfIsP1 ? false : (existing?.P1IsBot ?? assignment.OpponentIsBot);
                bool p2IsBot = !selfIsP1 ? false : (existing?.P2IsBot ?? assignment.OpponentIsBot);

                _db.UpsertTournamentMatchup(new Persistence.TournamentMatchupRow(
                    LobbyId: lobby.LobbyId,
                    Day: safeDay,
                    PairKey: pairKey,
                    P1AccountId: p1,
                    P1Username: p1Name,
                    P1IsBot: p1IsBot,
                    P2AccountId: p2,
                    P2Username: p2Name,
                    P2IsBot: p2IsBot,
                    IsSyntheticSparring: assignment.OpponentIsSyntheticSparringBot,
                    BattleSeed: assignment.BattleSeed,
                    P1LockedBoardJson: selfIsP1 ? lockedJson : (existing?.P1LockedBoardJson ?? ""),
                    P2LockedBoardJson: !selfIsP1 ? lockedJson : (existing?.P2LockedBoardJson ?? ""),
                    WinnerAccountId: existing?.WinnerAccountId ?? "",
                    LoserAccountId: existing?.LoserAccountId ?? "",
                    LoserPrestigeDamage: existing?.LoserPrestigeDamage ?? 0,
                    IsAutoResolved: existing?.IsAutoResolved ?? false,
                    ReplayId: existing?.ReplayId ?? "",
                    ResolvedAt: existing?.ResolvedAt));
            }
        }
    }

    public LockedPvpBoard? TryGetLockedBoard(Guid tournamentSessionId, uint day, string accountId)
    {
        if (tournamentSessionId == Guid.Empty || string.IsNullOrWhiteSpace(accountId))
            return null;

        uint safeDay = day == 0 ? 1u : day;
        string aid = NormalizeAccountKey(accountId);

        if (_lockedBoards.TryGetValue((tournamentSessionId, safeDay, aid), out var cached))
            return cached;

        // 内存未命中（例如刚经历服务重启）：按精确 day 从 SQLite tournament_matchups 恢复
        string lobbyId = ResolveLobbyId(tournamentSessionId);
        var lobby = _lobbies.FindByTournamentSessionId(tournamentSessionId);
        var assignment = lobby?.Matchmaker.GetAssignment(aid, safeDay);
        if (assignment != null)
        {
            string pairKey = TournamentMatchmaker.CanonicalPairKey(aid, assignment.OpponentAccountId);
            var row = _db.LoadTournamentMatchup(lobbyId, safeDay, pairKey);
            if (row != null)
            {
                string json = string.Equals(row.P1AccountId, aid, StringComparison.OrdinalIgnoreCase)
                    ? row.P1LockedBoardJson
                    : row.P2LockedBoardJson;
                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        var restored = JsonSerializer.Deserialize<LockedPvpBoard>(json);
                        if (restored != null && restored.Day == safeDay)
                        {
                            _lockedBoards[(tournamentSessionId, safeDay, aid)] = restored;
                            return restored;
                        }
                    }
                    catch { }
                }
            }
        }

        return null;
    }

    public SymmetricHumanPvpOutcome? TryGetResolvedMatch(Guid tournamentSessionId, uint day, string accountA, string accountB)
    {
        if (tournamentSessionId == Guid.Empty || string.IsNullOrWhiteSpace(accountA) || string.IsNullOrWhiteSpace(accountB))
            return null;

        uint safeDay = day == 0 ? 1u : day;
        string pairKey = TournamentMatchmaker.CanonicalPairKey(NormalizeAccountKey(accountA), NormalizeAccountKey(accountB));
        var cacheKey = (tournamentSessionId, safeDay, pairKey);

        if (_resolvedMatches.TryGetValue(cacheKey, out var cached))
            return cached;

        string lobbyId = ResolveLobbyId(tournamentSessionId);
        var row = _db.LoadTournamentMatchup(lobbyId, safeDay, pairKey);
        if (row != null && !string.IsNullOrWhiteSpace(row.WinnerAccountId))
        {
            var restored = new SymmetricHumanPvpOutcome(
                WinnerAccountId: row.WinnerAccountId,
                LoserAccountId: row.LoserAccountId,
                BattleSeed: row.BattleSeed,
                LoserPrestigeDamage: row.LoserPrestigeDamage,
                ReplayId: row.ReplayId,
                CachedCombatSimForP1: null,
                CachedCombatSimForP2: null);
            _resolvedMatches[cacheKey] = restored;
            return restored;
        }

        return null;
    }

    public SymmetricHumanPvpOutcome RecordOrGetResolvedMatch(
        Guid tournamentSessionId,
        uint day,
        string accountA,
        string accountB,
        SymmetricHumanPvpOutcome proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        if (tournamentSessionId == Guid.Empty)
            return proposed;

        uint safeDay = day == 0 ? 1u : day;
        string normA = NormalizeAccountKey(accountA);
        string normB = NormalizeAccountKey(accountB);
        string pairKey = TournamentMatchmaker.CanonicalPairKey(normA, normB);
        var cacheKey = (tournamentSessionId, safeDay, pairKey);

        object pairLock = _pairLocks.GetOrAdd(cacheKey, _ => new object());
        lock (pairLock)
        {
            if (_resolvedMatches.TryGetValue(cacheKey, out var existing))
                return existing;

            string lobbyId = ResolveLobbyId(tournamentSessionId);
            var existingRow = _db.LoadTournamentMatchup(lobbyId, safeDay, pairKey);
            if (existingRow != null && !string.IsNullOrWhiteSpace(existingRow.WinnerAccountId))
            {
                var dbOutcome = new SymmetricHumanPvpOutcome(
                    WinnerAccountId: existingRow.WinnerAccountId,
                    LoserAccountId: existingRow.LoserAccountId,
                    BattleSeed: existingRow.BattleSeed,
                    LoserPrestigeDamage: existingRow.LoserPrestigeDamage,
                    ReplayId: existingRow.ReplayId,
                    CachedCombatSimForP1: proposed.CachedCombatSimForP1,
                    CachedCombatSimForP2: proposed.CachedCombatSimForP2);
                _resolvedMatches[cacheKey] = dbOutcome;
                return dbOutcome;
            }

            string normWinner = NormalizeAccountKey(proposed.WinnerAccountId);
            string normLoser = string.Equals(normWinner, normA, StringComparison.OrdinalIgnoreCase) ? normB : normA;
            var canonicalOutcome = proposed with
            {
                WinnerAccountId = normWinner,
                LoserAccountId = normLoser,
                LoserPrestigeDamage = Math.Max(0, proposed.LoserPrestigeDamage),
                ReplayId = proposed.ReplayId ?? "",
            };

            string p1 = CanonicalFirst(normA, normB);
            string p2 = CanonicalSecond(normA, normB);
            var lobby = _lobbies.FindByTournamentSessionId(tournamentSessionId);
            var p1Member = lobby != null ? _lobbies.FindMember(lobby, p1) : null;
            var p2Member = lobby != null ? _lobbies.FindMember(lobby, p2) : null;

            string p1BoardJson = existingRow?.P1LockedBoardJson ?? "";
            if (string.IsNullOrWhiteSpace(p1BoardJson) && _lockedBoards.TryGetValue((tournamentSessionId, safeDay, p1), out var b1))
                p1BoardJson = JsonSerializer.Serialize(b1);
            string p2BoardJson = existingRow?.P2LockedBoardJson ?? "";
            if (string.IsNullOrWhiteSpace(p2BoardJson) && _lockedBoards.TryGetValue((tournamentSessionId, safeDay, p2), out var b2))
                p2BoardJson = JsonSerializer.Serialize(b2);

            _db.UpsertTournamentMatchup(new Persistence.TournamentMatchupRow(
                LobbyId: lobbyId,
                Day: safeDay,
                PairKey: pairKey,
                P1AccountId: p1,
                P1Username: p1Member?.Username ?? existingRow?.P1Username ?? p1,
                P1IsBot: p1Member?.IsAi ?? existingRow?.P1IsBot ?? false,
                P2AccountId: p2,
                P2Username: p2Member?.Username ?? existingRow?.P2Username ?? p2,
                P2IsBot: p2Member?.IsAi ?? existingRow?.P2IsBot ?? false,
                IsSyntheticSparring: existingRow?.IsSyntheticSparring ?? false,
                BattleSeed: canonicalOutcome.BattleSeed,
                P1LockedBoardJson: p1BoardJson,
                P2LockedBoardJson: p2BoardJson,
                WinnerAccountId: canonicalOutcome.WinnerAccountId,
                LoserAccountId: canonicalOutcome.LoserAccountId,
                LoserPrestigeDamage: canonicalOutcome.LoserPrestigeDamage,
                IsAutoResolved: false,
                ReplayId: canonicalOutcome.ReplayId,
                ResolvedAt: DateTime.UtcNow));

            lobby?.Matchmaker.RecordHumanMatchResolution(
                safeDay, p1, p2, canonicalOutcome.WinnerAccountId, canonicalOutcome.LoserPrestigeDamage, canonicalOutcome.BattleSeed);

            _resolvedMatches[cacheKey] = canonicalOutcome;
            return canonicalOutcome;
        }
    }

    /// <summary>清理已完结或已归档锦标赛的内存锁定与裁决缓存，防止长跑泄漏。</summary>
    public void PurgeForSession(Guid tournamentSessionId)
    {
        if (tournamentSessionId == Guid.Empty) return;
        foreach (var k in _lockedBoards.Keys.Where(x => x.Tsid == tournamentSessionId).ToList())
            _lockedBoards.TryRemove(k, out _);
        foreach (var k in _resolvedMatches.Keys.Where(x => x.Tsid == tournamentSessionId).ToList())
            _resolvedMatches.TryRemove(k, out _);
        foreach (var k in _pairLocks.Keys.Where(x => x.Tsid == tournamentSessionId).ToList())
            _pairLocks.TryRemove(k, out _);
    }

    private string ResolveLobbyId(Guid tournamentSessionId)
        => _lobbies.FindByTournamentSessionId(tournamentSessionId)?.LobbyId ?? tournamentSessionId.ToString("N");

    private static string NormalizeAccountKey(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        return Guid.TryParse(accountId, out var g) ? g.ToString("N") : accountId.Trim().Replace("-", "").ToLowerInvariant();
    }

    private static string CanonicalFirst(string a, string b)
        => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? a : b;

    private static string CanonicalSecond(string a, string b)
        => string.Compare(a, b, StringComparison.OrdinalIgnoreCase) <= 0 ? b : a;
}

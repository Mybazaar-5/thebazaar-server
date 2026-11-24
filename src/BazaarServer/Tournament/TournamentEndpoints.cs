using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BazaarCore.Models;
using BazaarCore.Runtime;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Tournament;

/// <summary>
/// 持久化锦标赛大厅、成员状态机、屏障看门狗核心与对称匹配协调存储：
/// - 全部关键状态变更同步持久化至 SQLite (tournament_lobbies / tournament_lobby_members / tournament_matchups)，重启自动热恢复；
/// - 修复 8 项多人联机 P0 缺陷：越权踢人/开赛、纯内存重启丢失、掉线卡死屏障、跨天脏对阵、观战缺失 Players、满房/已开赛假成功等；
/// - 内置房间级隔离通知 + 可选挂载 SseEventHub 与 ISocialAndReplaySink。
/// </summary>
public sealed class LobbyStore
{
    public const int DisconnectGraceSeconds = 45;
    public const int TakeoverBarrierGraceSeconds = 30;
    public const int TakeoverTotalDisconnectSeconds = 90;
    public const int MaxMissedTakeoverDays = 3;

    private static readonly JsonSerializerOptions StorageJsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private static readonly string[] AiBotNames =
    {
        "CrimsonVanessa",
        "ClockworkDooley",
        "GildedPygmalien",
        "ChefJules",
        "AlchemistMak",
        "SkyshipStelle",
        "ShadowKarnok",
        "TempestBot",
        "TempestBot-9",
        "ChronoBot-10",
        "ArcaneBot-11",
        "VanguardBot-12",
        "MirageBot-13",
        "PulseBot-14",
        "EclipseBot-15",
        "AegisBot-16",
    };

    private static readonly string[] HeroCycle =
    {
        "Vanessa", "Dooley", "Pygmalien", "Jules", "Mak", "Stelle", "Karnok", "Hero8",
    };

    private readonly Persistence? _db;
    private readonly ConcurrentDictionary<string, Lobby> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _codeToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _memberToLobby = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemovalNotice> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);

    // 房间级本地版本等待器（用于未装配 SseEventHub 的单元测试与回退场景）
    private readonly ConcurrentDictionary<string, long> _lobbyVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<TaskCompletionSource<bool>>> _lobbyWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _waitersLock = new();
    private List<TaskCompletionSource<bool>> _globalWaiters = new();
    private long _changeVersion;
    private Func<DateTime> _utcNowProvider = static () => DateTime.UtcNow;
    private Func<long> _monotonicMsProvider = static () => Environment.TickCount64;

    private SseEventHub? _sseHub;
    private ISocialAndReplaySink? _socialSink;

    public LobbyStore(Persistence? db = null)
    {
        _db = db;
        if (_db != null)
        {
            LoadActiveFromDb();
        }
    }

    /// <summary>挂载运行期 SSE 中枢与社交播报写入器（由 MultiplayerEndpointModule 在启动时绑定）。</summary>
    public void AttachRuntimeHooks(SseEventHub? sseHub, ISocialAndReplaySink? socialSink)
    {
        _sseHub = sseHub;
        _socialSink = socialSink;
    }

    public SseEventHub? SseHub => _sseHub;
    public long ChangeVersion => Volatile.Read(ref _changeVersion);
    internal DateTime UtcNow => _utcNowProvider();
    /// <summary>D6-P2-04：单调时钟毫秒计数（默认 Environment.TickCount64，免疫系统墙钟 NTP 回拨；测试注入时钟时自动同步映射）。</summary>
    internal long MonotonicNowMs => _monotonicMsProvider();

    public void SetClockForTesting(Func<DateTime>? utcNowProvider)
    {
        if (utcNowProvider == null)
        {
            _utcNowProvider = static () => DateTime.UtcNow;
            _monotonicMsProvider = static () => Environment.TickCount64;
        }
        else
        {
            _utcNowProvider = utcNowProvider;
            _monotonicMsProvider = () => (long)(utcNowProvider().ToUniversalTime() - DateTime.UnixEpoch).TotalMilliseconds;
        }
    }

    public sealed record RemovalNotice(string LobbyId, string Code, string Reason);

    public sealed record JoinLobbyResult(bool Ok, string? ErrorCode, string? MessageZh, Lobby? Lobby);

    public sealed record OperationResult(bool Ok, int StatusCode, string? ErrorCode, string? MessageZh);

    public sealed class RoundBarrierState
    {
        public uint Day { get; init; }
        public DateTime? FirstReadyAtUtc { get; set; }
        public DateTime? DeadlineAtUtc { get; set; }
        /// <summary>D6-P2-04：单调时钟就绪基准（毫秒），用于不受墙钟回拨影响的超时与宽限期判定。</summary>
        [JsonIgnore]
        public long? FirstReadyMonotonicMs { get; set; }
        /// <summary>D6-P2-04：单调时钟截止点（毫秒），从持久化恢复时按 remainingMs 钳制初始化。</summary>
        [JsonIgnore]
        public long? DeadlineMonotonicMs { get; set; }
        public bool IsAccelerated { get; set; }
        /// <summary>D6-P1-07：全员按时到达 Hour 6 正常解锁标记（与真实超时解锁 TimedOutUnlocked 严格分离）。</summary>
        public bool AllReadyUnlocked { get; set; }
        public bool TimedOutUnlocked { get; set; }
    }

    public sealed record PvpBarrierDecision(
        bool CanStartPvp,
        bool WaitingForOtherPlayers,
        DateTime? DeadlineAt,
        int SurvivingHumans,
        int ReadyHumans,
        int NotReadyHumans,
        bool IsAccelerated,
        bool TimedOutUnlocked,
        int RemainingSeconds,
        string? BlockReason);

    public sealed class Lobby
    {
        public string LobbyId { get; init; } = "";
        public string Code { get; init; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "custom"; // "custom" | "matchmaking" | "assigned"
        public string PlayMode { get; set; } = "Tournament";
        public string ModeId { get; set; } = "standard";
        public string PresetId { get; set; } = "PureOfficial";
        public string HostAccountId { get; set; } = "";
        public string HostUsername { get; set; } = "";
        public int PlayerCap { get; set; } = 8;
        public bool IsPublic { get; set; } = true;
        public bool HasPassword { get; set; }
        public string PasswordHash { get; set; } = "";
        public bool IsInviteOnly { get; set; }
        public string InviteTokenHash { get; set; } = "";
        public DateTime? InviteTokenExpiresAtUtc { get; set; }
        public bool ExcludeHostFromGameplay { get; init; }
        public bool HasStarted { get; set; }
        public bool IsCancelled { get; set; }
        public bool CountdownStarted { get; set; }
        public Guid? TournamentSessionId { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(6);
        public DateTime? EndedAt { get; set; }
        public CustomRunRuleSet Rules { get; set; } = new();
        public int RoundCountdownSeconds { get; set; } = 120;
        public int AcceleratedRoundCountdownSeconds { get; set; } = 30;
        public List<string> BannedEncounterIds { get; set; } = new();
        public TournamentMatchmaker Matchmaker { get; init; } = new();
        public Dictionary<uint, RoundBarrierState> RoundBarriers { get; } = new();
        public readonly List<Member> Members = new();
        internal uint HighestAiSimulatedDay { get; set; }
    }

    public sealed class Member
    {
        public string AccountId { get; init; } = "";
        public string Username { get; init; } = "";
        public string Hero { get; set; } = "Vanessa";
        public bool IsAi { get; init; }
        public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        // 掉线与 AI 托管状态机 ("online" | "disconnected" | "ai_takeover")
        public string ConnectionState { get; set; } = "online";
        public DateTime? AiTakeoverSinceUtc { get; set; }
        public int MissedPvpDays { get; set; }

        // 实时回合同步进度
        public uint CurrentDay { get; set; } = 1;
        public uint CurrentHour { get; set; } = 1;
        public bool IsReadyAtHour6Pvp { get; set; }
        /// <summary>P0-42 / P0-16：语义纯粹的已完成 PvP（真实对阵或托管代打）最高天数，绝不被 run.Day 或 FinalDay 覆写。</summary>
        public uint LastCompletedPvpDay { get; set; }

        // 终局与 AI 真实战绩状态
        public bool HasFinishedRun { get; set; }
        public bool FinalIsChampion { get; set; }
        public bool FinalIsEliminated { get; set; }
        public bool FinalIsConceded { get; set; }
        public int FinalPlace { get; set; }
        public int FinalVictories { get; set; }
        public int FinalDefeats { get; set; }
        public int FinalPrestige { get; set; } = 20;
        public uint FinalDay { get; set; } = 1;
        public string RemovalReason { get; set; } = "";
    }

    // ── 通知与房间级隔离 SSE 唤醒 ──────────────────────────────────────────

    public void NotifyChanged() => NotifyLobbyChanged(null, publicListChanged: true);

    public void NotifyLobbyChanged(string? lobbyId, bool publicListChanged = false)
    {
        Interlocked.Increment(ref _changeVersion);

        if (!string.IsNullOrWhiteSpace(lobbyId))
        {
            _lobbyVersions.AddOrUpdate(lobbyId, 1, (_, old) => old + 1);
            _sseHub?.PublishLobbyUpdate(lobbyId, alsoPublicList: publicListChanged);
        }
        else if (publicListChanged)
        {
            _sseHub?.PublishLobbyUpdate("", alsoPublicList: true);
        }

        List<TaskCompletionSource<bool>>? toWakeGlobal = null;
        List<TaskCompletionSource<bool>>? toWakeLobby = null;
        lock (_waitersLock)
        {
            if (_globalWaiters.Count > 0)
            {
                toWakeGlobal = _globalWaiters;
                _globalWaiters = new List<TaskCompletionSource<bool>>();
            }
            if (!string.IsNullOrWhiteSpace(lobbyId) && _lobbyWaiters.TryRemove(lobbyId, out var lw) && lw.Count > 0)
            {
                toWakeLobby = lw;
            }
        }
        if (toWakeGlobal != null)
            foreach (var tcs in toWakeGlobal) tcs.TrySetResult(true);
        if (toWakeLobby != null)
            foreach (var tcs in toWakeLobby) tcs.TrySetResult(true);
    }

    public long GetLobbyChangeVersion(string lobbyId)
        => _lobbyVersions.TryGetValue(lobbyId, out long v) ? v : 0;

    public async Task<bool> WaitForChangeAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        if (Volatile.Read(ref _changeVersion) != seenVersion)
            return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            if (Volatile.Read(ref _changeVersion) != seenVersion)
                return true;
            _globalWaiters.Add(tcs);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                tcs);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_waitersLock)
            {
                _globalWaiters.Remove(tcs);
            }
        }
    }

    public async Task<bool> WaitForLobbyChangeAsync(string lobbyId, long seenLobbyVersion, TimeSpan timeout, CancellationToken ct)
    {
        if (GetLobbyChangeVersion(lobbyId) != seenLobbyVersion)
            return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            if (GetLobbyChangeVersion(lobbyId) != seenLobbyVersion)
                return true;
            var list = _lobbyWaiters.GetOrAdd(lobbyId, _ => new List<TaskCompletionSource<bool>>());
            list.Add(tcs);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            using var reg = timeoutCts.Token.Register(
                static state => ((TaskCompletionSource<bool>)state!).TrySetResult(false),
                tcs);
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_waitersLock)
            {
                if (_lobbyWaiters.TryGetValue(lobbyId, out var list))
                {
                    list.Remove(tcs);
                    if (list.Count == 0) _lobbyWaiters.TryRemove(lobbyId, out _);
                }
            }
        }
    }

    // ── SQLite 持久化与重启热恢复 ──────────────────────────────────────────

    /// <summary>服务端启动时从 SQLite 热恢复所有活跃锦标赛大厅、成员、屏障与匹配器状态。</summary>
    public int LoadActiveFromDb()
    {
        if (_db == null) return 0;
        int restored = 0;
        try
        {
            var records = _db.LoadActiveTournamentLobbies(UtcNow);
            foreach (var (row, memberRows) in records)
            {
                if (_byId.ContainsKey(row.LobbyId)) continue;

                CustomRunRuleSet rules;
                try { rules = JsonSerializer.Deserialize<CustomRunRuleSet>(row.RulesJson, StorageJsonOpts)?.CloneNormalized() ?? new CustomRunRuleSet(); }
                catch { rules = new CustomRunRuleSet(); }

                List<string> banned;
                try { banned = JsonSerializer.Deserialize<List<string>>(row.BannedEncounterIdsJson, StorageJsonOpts) ?? new List<string>(); }
                catch { banned = new List<string>(); }

                var matchmaker = new TournamentMatchmaker(row.LobbyId);
                matchmaker.ImportStateJson(row.MatchmakerStateJson);

                Guid? tsid = Guid.TryParse(row.TournamentSessionId, out var parsedGuid) && parsedGuid != Guid.Empty
                    ? parsedGuid
                    : null;

                var lobby = new Lobby
                {
                    LobbyId = row.LobbyId,
                    Code = row.Code,
                    Name = row.Name,
                    Kind = row.Kind,
                    PlayMode = row.PlayMode,
                    ModeId = string.IsNullOrWhiteSpace(row.ModeId) ? Persistence.ResolveModeKey(rules.SpecialModeId) : row.ModeId,
                    PresetId = string.IsNullOrWhiteSpace(row.PresetId) ? rules.PresetId.ToString() : row.PresetId,
                    HostAccountId = row.HostAccountId,
                    HostUsername = row.HostUsername,
                    PlayerCap = Math.Clamp(row.PlayerCap, 2, 16),
                    IsPublic = row.IsPublic,
                    HasPassword = row.HasPassword,
                    PasswordHash = row.PasswordHash,
                    IsInviteOnly = row.IsInviteOnly,
                    InviteTokenHash = row.InviteTokenHash,
                    InviteTokenExpiresAtUtc = row.InviteTokenExpiresAtUtc,
                    ExcludeHostFromGameplay = row.ExcludeHostFromGameplay,
                    HasStarted = row.State is "started" or "finished",
                    CountdownStarted = row.State is "countdown" or "started" or "finished",
                    IsCancelled = row.State == "cancelled",
                    TournamentSessionId = tsid,
                    CreatedAt = row.CreatedAt,
                    StartedAt = row.StartedAt,
                    ExpiresAt = row.ExpiresAt,
                    EndedAt = row.EndedAt,
                    Rules = rules,
                    RoundCountdownSeconds = row.RoundCountdownSeconds,
                    AcceleratedRoundCountdownSeconds = row.AccelRoundCountdownSeconds,
                    BannedEncounterIds = banned,
                    Matchmaker = matchmaker,
                };

                // 恢复 RoundBarriers（D6-P2-04：同步按剩余时间初始化单调时钟截止点，<= 0 钳制为 MonotonicNowMs 立即到期）
                try
                {
                    var barriers = JsonSerializer.Deserialize<Dictionary<uint, RoundBarrierState>>(row.RoundBarriersJson, StorageJsonOpts);
                    if (barriers != null)
                    {
                        DateTime restoreNowUtc = UtcNow;
                        long restoreMonoMs = MonotonicNowMs;
                        foreach (var kv in barriers)
                        {
                            var br = kv.Value;
                            if (br.FirstReadyAtUtc.HasValue)
                            {
                                long elapsedMs = Math.Max(0L, (long)(restoreNowUtc - br.FirstReadyAtUtc.Value).TotalMilliseconds);
                                br.FirstReadyMonotonicMs = restoreMonoMs - elapsedMs;
                            }
                            if (br.DeadlineAtUtc.HasValue)
                            {
                                long remMs = Math.Max(0L, (long)(br.DeadlineAtUtc.Value - restoreNowUtc).TotalMilliseconds);
                                br.DeadlineMonotonicMs = restoreMonoMs + remMs;
                            }
                            lobby.RoundBarriers[kv.Key] = br;
                        }
                    }
                }
                catch { }

                // 恢复成员
                foreach (var mr in memberRows)
                {
                    var m = new Member
                    {
                        AccountId = mr.AccountId,
                        Username = mr.Username,
                        Hero = mr.Hero,
                        IsAi = mr.IsAi,
                        JoinedAt = mr.JoinedAt,
                        LastSeenAt = mr.LastSeenAt,
                        ConnectionState = string.IsNullOrWhiteSpace(mr.ConnectionState) ? "online" : mr.ConnectionState,
                        AiTakeoverSinceUtc = mr.AiTakeoverSince,
                        MissedPvpDays = mr.MissedPvpDays,
                        CurrentDay = mr.CurrentDay,
                        CurrentHour = mr.CurrentHour,
                        IsReadyAtHour6Pvp = mr.IsReadyH6Pvp,
                        HasFinishedRun = mr.HasFinishedRun,
                        FinalIsChampion = mr.IsChampion,
                        FinalIsEliminated = mr.IsEliminated,
                        FinalIsConceded = mr.IsConceded,
                        FinalPlace = mr.FinalPlace,
                        FinalVictories = mr.Victories,
                        FinalDefeats = mr.Defeats,
                        FinalPrestige = mr.Prestige,
                        FinalDay = mr.FinalDay,
                        RemovalReason = mr.RemovalReason,
                    };
                    lobby.Members.Add(m);
                    if (!m.IsAi && !m.HasFinishedRun && !lobby.IsCancelled)
                    {
                        _memberToLobby[m.AccountId] = lobby.LobbyId;
                    }
                }

                // 恢复历史日冻结对阵表
                var matchups = _db.LoadTournamentMatchupsByLobby(lobby.LobbyId);
                foreach (var dayGroup in matchups.GroupBy(x => x.Day))
                {
                    var dayAssignments = new List<MatchupAssignment>();
                    var dayResolutions = new List<HumanMatchResolution>();
                    bool nonLethal = lobby.Rules.BotNonLethalPrestigeLock;
                    foreach (var mu in dayGroup)
                    {
                        dayAssignments.Add(new MatchupAssignment(
                            mu.Day, mu.P1AccountId, mu.P2AccountId, mu.P2Username,
                            mu.P2IsBot, mu.IsSyntheticSparring || (mu.P2IsBot && nonLethal), mu.IsSyntheticSparring, mu.BattleSeed));
                        dayAssignments.Add(new MatchupAssignment(
                            mu.Day, mu.P2AccountId, mu.P1AccountId, mu.P1Username,
                            mu.P1IsBot, mu.IsSyntheticSparring || (mu.P1IsBot && nonLethal), mu.IsSyntheticSparring, mu.BattleSeed));
                        if (!string.IsNullOrWhiteSpace(mu.WinnerAccountId))
                        {
                            dayResolutions.Add(new HumanMatchResolution(
                                mu.Day, mu.PairKey, mu.WinnerAccountId, mu.LoserAccountId,
                                mu.BattleSeed, mu.LoserPrestigeDamage, mu.ResolvedAt ?? DateTime.UtcNow));
                        }
                    }
                    lobby.Matchmaker.RestoreDayAssignments(dayGroup.Key, dayAssignments, dayResolutions);
                }

                _byId[lobby.LobbyId] = lobby;
                _codeToId[lobby.Code] = lobby.LobbyId;
                restored++;
            }

            if (restored > 0)
                Console.WriteLine($"[LobbyStore] 已从 SQLite 恢复 {restored} 个活跃锦标赛大厅");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[LobbyStore] 从 SQLite 恢复大厅失败: " + ex.Message);
        }
        return restored;
    }

    private void PersistLobby(Lobby lobby)
    {
        if (_db == null || lobby == null) return;
        try
        {
            Persistence.TournamentLobbyRow lobbyRow;
            List<Persistence.TournamentLobbyMemberRow> memberRows;
            DateTime now = UtcNow;

            lock (lobby.Members)
            {
                bool allHumansDone = lobby.HasStarted && lobby.Members.Where(m => !m.IsAi).All(m => m.HasFinishedRun);
                string state = lobby.IsCancelled
                    ? "cancelled"
                    : allHumansDone ? "finished"
                    : lobby.HasStarted ? "started"
                    : lobby.CountdownStarted ? "countdown" : "waiting";

                if (state is "finished" or "cancelled" && !lobby.EndedAt.HasValue)
                    lobby.EndedAt = now;

                string rulesJson = JsonSerializer.Serialize(lobby.Rules ?? new CustomRunRuleSet(), StorageJsonOpts);
                string bannedJson = JsonSerializer.Serialize(lobby.BannedEncounterIds ?? new List<string>(), StorageJsonOpts);
                string mutatorsJson = JsonSerializer.Serialize(lobby.Rules?.MutatorIds ?? new List<string>(), StorageJsonOpts);
                string barriersJson = JsonSerializer.Serialize(lobby.RoundBarriers, StorageJsonOpts);
                string mmJson = lobby.Matchmaker.ExportStateJson();

                lobbyRow = new Persistence.TournamentLobbyRow(
                    LobbyId: lobby.LobbyId,
                    Code: lobby.Code,
                    Name: lobby.Name,
                    Kind: lobby.Kind,
                    PlayMode: lobby.PlayMode,
                    ModeId: lobby.ModeId,
                    PresetId: lobby.PresetId,
                    MutatorsJson: mutatorsJson,
                    HostAccountId: lobby.HostAccountId,
                    HostUsername: lobby.HostUsername,
                    PlayerCap: lobby.PlayerCap,
                    IsPublic: lobby.IsPublic,
                    HasPassword: lobby.HasPassword,
                    PasswordHash: lobby.PasswordHash,
                    IsInviteOnly: lobby.IsInviteOnly,
                    InviteTokenHash: lobby.InviteTokenHash,
                    ExcludeHostFromGameplay: lobby.ExcludeHostFromGameplay,
                    State: state,
                    TournamentSessionId: lobby.TournamentSessionId?.ToString("D") ?? "",
                    RoundCountdownSeconds: lobby.RoundCountdownSeconds,
                    AccelRoundCountdownSeconds: lobby.AcceleratedRoundCountdownSeconds,
                    RulesJson: rulesJson,
                    BannedEncounterIdsJson: bannedJson,
                    RoundBarriersJson: barriersJson,
                    MatchmakerStateJson: mmJson,
                    CreatedAt: lobby.CreatedAt,
                    StartedAt: lobby.StartedAt,
                    ExpiresAt: lobby.ExpiresAt,
                    EndedAt: lobby.EndedAt,
                    UpdatedAt: now,
                    InviteTokenExpiresAtUtc: lobby.InviteTokenExpiresAtUtc);

                memberRows = lobby.Members.Select(m => new Persistence.TournamentLobbyMemberRow(
                    LobbyId: lobby.LobbyId,
                    AccountId: m.AccountId,
                    Username: m.Username,
                    Hero: m.Hero,
                    IsAi: m.IsAi,
                    MemberState: "accepted",
                    ConnectionState: m.ConnectionState,
                    AiTakeoverSince: m.AiTakeoverSinceUtc,
                    MissedPvpDays: m.MissedPvpDays,
                    CurrentDay: m.CurrentDay,
                    CurrentHour: m.CurrentHour,
                    IsReadyH6Pvp: m.IsReadyAtHour6Pvp,
                    HasFinishedRun: m.HasFinishedRun,
                    IsChampion: m.FinalIsChampion,
                    IsEliminated: m.FinalIsEliminated,
                    IsConceded: m.FinalIsConceded,
                    FinalPlace: m.FinalPlace,
                    Victories: m.FinalVictories,
                    Defeats: m.FinalDefeats,
                    Prestige: m.FinalPrestige,
                    FinalDay: m.FinalDay,
                    RemovalReason: m.RemovalReason,
                    JoinedAt: m.JoinedAt,
                    LastSeenAt: m.LastSeenAt)).ToList();
            }

            _db.SaveTournamentLobbyWithMembers(lobbyRow, memberRows);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LobbyStore] 持久化大厅 {lobby.LobbyId} 失败: {ex.Message}");
        }
    }

    private void PersistFrozenDayMatchups(Lobby lobby, uint day)
    {
        if (_db == null) return;
        var assigned = lobby.Matchmaker.TryGetAssignedDaySnapshot(day);
        if (assigned == null || assigned.Count == 0) return;

        var savedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Member> byId;
        lock (lobby.Members)
        {
            byId = lobby.Members.ToDictionary(m => m.AccountId, StringComparer.OrdinalIgnoreCase);
        }

        foreach (var a in assigned.Values)
        {
            string pairKey = TournamentMatchmaker.CanonicalPairKey(a.AccountId, a.OpponentAccountId);
            if (!savedPairs.Add(pairKey)) continue;

            bool selfFirst = string.Compare(a.AccountId, a.OpponentAccountId, StringComparison.OrdinalIgnoreCase) <= 0;
            string p1Id = selfFirst ? a.AccountId : a.OpponentAccountId;
            string p2Id = selfFirst ? a.OpponentAccountId : a.AccountId;
            byId.TryGetValue(p1Id, out var p1m);
            byId.TryGetValue(p2Id, out var p2m);

            string p1Name = p1m?.Username ?? (selfFirst ? p1Id : a.OpponentUsername);
            string p2Name = p2m?.Username ?? (selfFirst ? a.OpponentUsername : p2Id);
            bool p1Bot = p1m?.IsAi ?? (!selfFirst && a.OpponentIsBot);
            bool p2Bot = p2m?.IsAi ?? (selfFirst && a.OpponentIsBot);

            try
            {
                _db.UpsertTournamentMatchup(new Persistence.TournamentMatchupRow(
                    LobbyId: lobby.LobbyId,
                    Day: day,
                    PairKey: pairKey,
                    P1AccountId: p1Id,
                    P1Username: p1Name,
                    P1IsBot: p1Bot,
                    P2AccountId: p2Id,
                    P2Username: p2Name,
                    P2IsBot: p2Bot,
                    IsSyntheticSparring: a.OpponentIsSyntheticSparringBot,
                    BattleSeed: a.BattleSeed,
                    P1LockedBoardJson: "",
                    P2LockedBoardJson: "",
                    WinnerAccountId: "",
                    LoserAccountId: "",
                    LoserPrestigeDamage: 0,
                    IsAutoResolved: false,
                    ReplayId: "",
                    ResolvedAt: null));
            }
            catch { }
        }
    }

    // ── 大厅全生命周期操作 ───────────────────────────────────────────────────

    public Lobby Create(
        AccountStore.Account host,
        string? name,
        string? playMode,
        int playerCap,
        bool isPublic,
        CustomRunRuleSet? rules = null,
        int? roundCountdownSeconds = null,
        int? acceleratedRoundCountdownSeconds = null,
        IEnumerable<string>? bannedEncounterIds = null,
        string? password = null,
        bool isInviteOnly = false,
        string? modeId = null,
        string? presetId = null)
    {
        string hostAid = host.AccountId.ToString("N");
        CleanupPreviousMembership(hostAid);

        DateTime now = UtcNow;
        string lobbyId = Guid.NewGuid().ToString("N")[..12];
        string cleanName = AccountStore.SanitizeDisplayText(name, 48);
        if (string.IsNullOrWhiteSpace(cleanName))
            cleanName = host.Username + "的锦标赛房间";

        string trimmedPwd = (password ?? "").Trim();
        bool hasPwd = trimmedPwd.Length > 0;
        string pwdHash = hasPwd ? AccountStore.HashSecret(trimmedPwd) : "";

        var lobby = new Lobby
        {
            LobbyId = lobbyId,
            Code = NewCode(),
            Name = cleanName,
            Kind = "custom",
            PlayMode = string.IsNullOrWhiteSpace(playMode) ? "Tournament" : playMode.Trim(),
            HostAccountId = hostAid,
            HostUsername = host.Username,
            PlayerCap = Math.Clamp(playerCap <= 0 ? 8 : playerCap, 2, 16),
            IsPublic = isPublic,
            HasPassword = hasPwd,
            PasswordHash = pwdHash,
            IsInviteOnly = isInviteOnly,
            CreatedAt = now,
            ExpiresAt = now.AddHours(6),
            Matchmaker = new TournamentMatchmaker(lobbyId),
        };
        lobby.Members.Add(new Member
        {
            AccountId = hostAid,
            Username = host.Username,
            Hero = "Vanessa",
            JoinedAt = now,
            LastSeenAt = now,
        });

        ApplyNormalizedRulesLocked(
            lobby,
            rules,
            playerCapOverride: playerCap > 0 ? playerCap : null,
            roundCountdownSeconds: roundCountdownSeconds,
            acceleratedRoundCountdownSeconds: acceleratedRoundCountdownSeconds,
            bannedEncounterIds: bannedEncounterIds,
            allowUpdatePlayerCap: true,
            modeIdOverride: modeId,
            presetIdOverride: presetId);

        _byId[lobby.LobbyId] = lobby;
        _codeToId[lobby.Code] = lobby.LobbyId;
        _memberToLobby[hostAid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(hostAid, out _);

        PersistLobby(lobby);
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
        return lobby;
    }

    public void UpdateRules(
        Lobby lobby,
        CustomRunRuleSet incomingRules,
        int? roundCountdownSeconds = null,
        int? acceleratedRoundCountdownSeconds = null,
        IEnumerable<string>? bannedEncounterIds = null,
        string? modeId = null,
        string? presetId = null)
    {
        lock (lobby.Members)
        {
            ApplyNormalizedRulesLocked(
                lobby,
                incomingRules,
                playerCapOverride: null,
                roundCountdownSeconds: roundCountdownSeconds,
                acceleratedRoundCountdownSeconds: acceleratedRoundCountdownSeconds,
                bannedEncounterIds: bannedEncounterIds,
                allowUpdatePlayerCap: !lobby.HasStarted,
                modeIdOverride: modeId,
                presetIdOverride: presetId);
        }
        PersistLobby(lobby);
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
    }

    public (bool Ok, string? Error) ManageBots(Lobby lobby, string action)
    {
        string act = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (act is not ("add" or "fill" or "remove" or "clear"))
            return (false, "不支持的机器人操作指令（仅支持 add/fill/remove/clear）。");

        lock (lobby.Members)
        {
            if (lobby.HasStarted || lobby.CountdownStarted)
                return (false, "房间已开赛，无法再增删 AI 选手。");

            switch (act)
            {
                case "add":
                    if (lobby.Members.Count < lobby.PlayerCap)
                        TryAddNextAiMember(lobby, UtcNow);
                    break;
                case "fill":
                    while (lobby.Members.Count < lobby.PlayerCap)
                    {
                        if (!TryAddNextAiMember(lobby, UtcNow)) break;
                    }
                    break;
                case "remove":
                    int lastAi = lobby.Members.FindLastIndex(m => m.IsAi);
                    if (lastAi >= 0) lobby.Members.RemoveAt(lastAi);
                    break;
                case "clear":
                    lobby.Members.RemoveAll(m => m.IsAi);
                    break;
            }
        }

        PersistLobby(lobby);
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
        return (true, null);
    }

    /// <summary>生成或刷新带时效的房间邀请令牌（仅房主可调用）。</summary>
    public (string InviteToken, DateTime ExpiresAtUtc) GenerateInviteToken(Lobby lobby, TimeSpan? ttl = null)
    {
        DateTime expires = UtcNow.Add(ttl ?? TimeSpan.FromHours(2));
        string token = "inv_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        string hash = AccountStore.Fingerprint(token);
        lock (lobby.Members)
        {
            lobby.InviteTokenHash = hash;
            lobby.InviteTokenExpiresAtUtc = expires;
        }
        PersistLobby(lobby);
        return (token, expires);
    }

    /// <summary>手动移交房主权限给房间内其他真人成员。</summary>
    public OperationResult TransferHost(Lobby lobby, string callerAccountId, string targetAccountId)
    {
        string normCaller = NormalizeAccountKey(callerAccountId);
        string normTarget = NormalizeAccountKey(targetAccountId);
        string newHostName;

        lock (lobby.Members)
        {
            if (lobby.IsCancelled)
                return new OperationResult(false, 404, "LOBBY_NOT_FOUND", "房间不存在或已解散。");
            if (!lobby.HostAccountId.Equals(normCaller, StringComparison.OrdinalIgnoreCase))
                return new OperationResult(false, 409, "NOT_HOST", "仅房主可以移交房主权限。");
            if (normCaller.Equals(normTarget, StringComparison.OrdinalIgnoreCase))
                return new OperationResult(false, 409, "ALREADY_HOST", "你当前已经是房主。");

            var target = lobby.Members.Find(m => !m.IsAi && m.AccountId.Equals(normTarget, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return new OperationResult(false, 409, "TARGET_NOT_IN_LOBBY", "目标玩家不在房间中或为 AI 选手，无法移交房主。");

            lobby.HostAccountId = target.AccountId;
            lobby.HostUsername = target.Username;
            newHostName = target.Username;
        }

        PersistLobby(lobby);
        _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_host_transfer", $"👑 房主权限已移交给 {newHostName}。", new { lobbyId = lobby.LobbyId, newHost = newHostName });
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
        return new OperationResult(true, 200, null, null);
    }

    public Lobby EnqueueMatchmaking(AccountStore.Account player)
    {
        string aid = player.AccountId.ToString("N");
        var existing = GetByMember(aid);
        if (existing != null)
        {
            var m = FindMember(existing, aid);
            if (m != null && !m.HasFinishedRun && !existing.IsCancelled && existing.Kind == "matchmaking")
            {
                if (!existing.HasStarted)
                {
                    lock (existing.Members) FillAiCompetitors(existing, 8, UtcNow);
                    StartCountdown(existing.LobbyId, Guid.NewGuid());
                }
                return existing;
            }
            CleanupPreviousMembership(aid);
        }

        DateTime now = UtcNow;
        string lobbyId = Guid.NewGuid().ToString("N")[..12];
        var lobby = new Lobby
        {
            LobbyId = lobbyId,
            Code = NewCode(),
            Name = "快速匹配锦标赛",
            Kind = "matchmaking",
            PlayMode = "Tournament",
            ModeId = "standard",
            PresetId = "PureOfficial",
            HostAccountId = aid,
            HostUsername = player.Username,
            PlayerCap = 8,
            IsPublic = false,
            HasStarted = true,
            CountdownStarted = true,
            TournamentSessionId = Guid.NewGuid(),
            CreatedAt = now,
            StartedAt = now,
            ExpiresAt = now.AddHours(6),
            Matchmaker = new TournamentMatchmaker(lobbyId),
        };
        lobby.Members.Add(new Member
        {
            AccountId = aid,
            Username = player.Username,
            Hero = "Vanessa",
            JoinedAt = now,
            LastSeenAt = now,
        });
        FillAiCompetitors(lobby, 8, now);
        EnsureDayMatchups(lobby, 1u, lobby.Members);

        _byId[lobby.LobbyId] = lobby;
        _codeToId[lobby.Code] = lobby.LobbyId;
        _memberToLobby[aid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(aid, out _);

        PersistLobby(lobby);
        PersistFrozenDayMatchups(lobby, 1u);
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: false);
        return lobby;
    }

    public Lobby? Get(string? lobbyId)
        => !string.IsNullOrWhiteSpace(lobbyId) && _byId.TryGetValue(lobbyId, out var l) ? l : null;

    public Lobby? GetByCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && _codeToId.TryGetValue(code, out var id) ? Get(id) : null;

    public Lobby? GetByMember(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return null;
        string norm = NormalizeAccountKey(accountId);
        if (_memberToLobby.TryGetValue(accountId, out var id) || (!string.IsNullOrEmpty(norm) && _memberToLobby.TryGetValue(norm, out id)))
            return Get(id);
        return null;
    }

    public RemovalNotice? PopRemovalNotice(string accountId)
        => _pendingRemovals.TryRemove(NormalizeAccountKey(accountId), out var notice) ? notice : null;

    /// <summary>向后兼容的加房重载：仅在成功入房时返回 Lobby，满房/已开赛/密码错误一律返回 null。</summary>
    public Lobby? Join(string code, AccountStore.Account acc)
        => JoinWithResult(code, acc).Lobby;

    /// <summary>强类型加房：严格校验存在性、未开赛、密码/邀请码与满员（满员且开赛前存在 AI 占位时自动腾出 1 位给真人）。</summary>
    public JoinLobbyResult JoinWithResult(string code, AccountStore.Account acc, string? password = null, string? inviteToken = null)
    {
        string aid = acc.AccountId.ToString("N");
        var lobby = GetByCode(code) ?? Get(code);
        if (lobby == null || lobby.IsCancelled)
            return new JoinLobbyResult(false, "LOBBY_NOT_FOUND", "房间不存在或已解散。", null);

        DateTime now = UtcNow;
        bool alreadyMember;
        bool needPasswordCheck = false;
        string expectedPasswordHash = string.Empty;
        lock (lobby.Members)
        {
            if (lobby.IsCancelled)
                return new JoinLobbyResult(false, "LOBBY_NOT_FOUND", "房间不存在或已解散。", null);

            alreadyMember = lobby.Members.Exists(m => m.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase));
            if (alreadyMember)
            {
                var self = lobby.Members.Find(m => m.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))!;
                self.LastSeenAt = now;
                if (!self.HasFinishedRun && !string.Equals(self.ConnectionState, "left", StringComparison.OrdinalIgnoreCase))
                {
                    self.ConnectionState = "online";
                    self.AiTakeoverSinceUtc = null;
                }
            }
            else
            {
                // 修复 MP-P0-08：严禁加入已开赛或倒计时已锁定的房间
                if (lobby.HasStarted || lobby.CountdownStarted)
                    return new JoinLobbyResult(false, "LOBBY_ALREADY_STARTED", "该房间已经开赛，无法中途加入，您可以通过观战进入查看。", null);

                // 校验邀请令牌（有效邀请码可豁免仅邀请限制与密码限制）
                bool validInvite = false;
                if (!string.IsNullOrWhiteSpace(inviteToken) && !string.IsNullOrWhiteSpace(lobby.InviteTokenHash))
                {
                    bool notExpired = !lobby.InviteTokenExpiresAtUtc.HasValue || lobby.InviteTokenExpiresAtUtc.Value > now;
                    if (notExpired && string.Equals(AccountStore.Fingerprint(inviteToken.Trim()), lobby.InviteTokenHash, StringComparison.OrdinalIgnoreCase))
                        validInvite = true;
                }

                if (lobby.IsInviteOnly && !validInvite)
                    return new JoinLobbyResult(false, "INVITE_ONLY", "该房间为仅限邀请房间，需要有效邀请链接或邀请码才能加入。", null);

                if (lobby.HasPassword && !validInvite)
                {
                    if (string.IsNullOrWhiteSpace(password))
                        return new JoinLobbyResult(false, "PASSWORD_REQUIRED", "该房间已设置密码，请输入房间密码后再加入。", null);
                    // 将高开销 PBKDF2 密码验算移至 lock (lobby.Members) 之外，防止暴力猜密阻塞全房操作
                    needPasswordCheck = true;
                    expectedPasswordHash = lobby.PasswordHash;
                }

                // 预检满房（此处不提前移除 AI，待锁外密码核验通过后再原子腾位）
                if (lobby.Members.Count >= lobby.PlayerCap && !lobby.Members.Exists(m => m.IsAi))
                    return new JoinLobbyResult(false, "LOBBY_FULL", "房间人数已满，无法加入。", null);
            }
        }

        if (needPasswordCheck && !AccountStore.VerifySecretHash(password!.Trim(), expectedPasswordHash))
            return new JoinLobbyResult(false, "INVALID_PASSWORD", "房间密码不正确，请核对后重试。", null);

        var prev = GetByMember(aid);
        if (prev != null && !prev.LobbyId.Equals(lobby.LobbyId, StringComparison.OrdinalIgnoreCase))
        {
            CleanupPreviousMembership(aid);
        }

        if (!alreadyMember)
        {
            lock (lobby.Members)
            {
                if (lobby.IsCancelled)
                    return new JoinLobbyResult(false, "LOBBY_NOT_FOUND", "房间不存在或已解散。", null);
                if (lobby.HasStarted || lobby.CountdownStarted)
                    return new JoinLobbyResult(false, "LOBBY_ALREADY_STARTED", "该房间已经开赛，无法加入。", null);

                if (!lobby.Members.Exists(m => m.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase)))
                {
                    if (lobby.Members.Count >= lobby.PlayerCap)
                    {
                        int lastAi = lobby.Members.FindLastIndex(m => m.IsAi);
                        if (lastAi >= 0) lobby.Members.RemoveAt(lastAi);
                        else return new JoinLobbyResult(false, "LOBBY_FULL", "房间人数已满，无法加入。", null);
                    }

                    lobby.Members.Add(new Member
                    {
                        AccountId = aid,
                        Username = acc.Username,
                        Hero = HeroCycle[lobby.Members.Count % HeroCycle.Length],
                        JoinedAt = now,
                        LastSeenAt = now,
                    });
                }
            }
        }

        _memberToLobby[aid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(aid, out _);
        PersistLobby(lobby);
        if (!alreadyMember)
        {
            _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_join", $"🎮 {acc.Username} 加入了房间。", new { lobbyId = lobby.LobbyId, username = acc.Username });
        }
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
        return new JoinLobbyResult(true, null, null, lobby);
    }

    public void Leave(string accountId, string? reason = null)
    {
        string normAid = NormalizeAccountKey(accountId);
        var lobby = GetByMember(normAid);
        if (lobby == null) return;

        string? removedName = null;
        string? newHostName = null;
        bool wasPublic = lobby.IsPublic;
        bool lobbyCancelled = false;
        lock (lobby.Members)
        {
            var removed = lobby.Members.Find(m => m.AccountId.Equals(normAid, StringComparison.OrdinalIgnoreCase));
            removedName = removed?.Username;

            if (!lobby.HasStarted)
            {
                // 开赛前允许物理移除成员
                lobby.Members.RemoveAll(m => m.AccountId.Equals(normAid, StringComparison.OrdinalIgnoreCase));

                int humanLeft = lobby.Members.Count(m => !m.IsAi);
                if (humanLeft == 0)
                {
                    lobby.IsCancelled = true;
                    lobby.EndedAt = UtcNow;
                    lobbyCancelled = true;
                    _byId.TryRemove(lobby.LobbyId, out _);
                    _codeToId.TryRemove(lobby.Code, out _);
                }
                else if (lobby.HostAccountId.Equals(normAid, StringComparison.OrdinalIgnoreCase))
                {
                    var successor = lobby.Members
                        .Where(m => !m.IsAi)
                        .OrderBy(m => m.ConnectionState == "online" ? 0 : 1)
                        .ThenBy(m => m.JoinedAt)
                        .FirstOrDefault();
                    if (successor != null)
                    {
                        lobby.HostAccountId = successor.AccountId;
                        lobby.HostUsername = successor.Username;
                        newHostName = successor.Username;
                    }
                }
            }
            else
            {
                // D6-P1-07：开赛后严禁物理删除成员，改为软退赛保留席位与完整对阵/排名历史
                if (removed != null)
                {
                    removed.ConnectionState = "left";
                    removed.AiTakeoverSinceUtc = null;
                    if (!removed.HasFinishedRun)
                    {
                        removed.HasFinishedRun = true;
                        removed.FinalIsConceded = true;
                        removed.FinalIsEliminated = true;
                        removed.FinalPrestige = 0;
                    }
                    removed.RemovalReason = string.IsNullOrWhiteSpace(reason) ? "left" : reason.Trim();
                }

                int remainingHumans = lobby.Members.Count(m =>
                    !m.IsAi && !string.Equals(m.ConnectionState, "left", StringComparison.OrdinalIgnoreCase));
                if (remainingHumans == 0)
                {
                    lobby.EndedAt ??= UtcNow;
                }
                else if (lobby.HostAccountId.Equals(normAid, StringComparison.OrdinalIgnoreCase))
                {
                    var successor = lobby.Members
                        .Where(m => !m.IsAi && !string.Equals(m.ConnectionState, "left", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(m => m.ConnectionState == "online" ? 0 : 1)
                        .ThenBy(m => m.JoinedAt)
                        .FirstOrDefault();
                    if (successor != null)
                    {
                        lobby.HostAccountId = successor.AccountId;
                        lobby.HostUsername = successor.Username;
                        newHostName = successor.Username;
                    }
                }
            }
        }

        _memberToLobby.TryRemove(normAid, out _);
        if (!string.IsNullOrEmpty(reason))
        {
            _pendingRemovals[normAid] = new RemovalNotice(lobby.LobbyId, lobby.Code, reason);
        }

        PersistLobby(lobby);
        if (!string.IsNullOrWhiteSpace(removedName) && !lobbyCancelled)
        {
            string msg = reason == "kicked" ? $"🚪 {removedName} 已被房主移出房间。" : $"👋 {removedName} 离开了房间。";
            _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_leave", msg, new { lobbyId = lobby.LobbyId, username = removedName, reason = reason ?? "left" });
        }
        if (!string.IsNullOrWhiteSpace(newHostName))
        {
            _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_host_transfer", $"👑 原房主已离开，房主自动移交给 {newHostName}。", new { lobbyId = lobby.LobbyId, newHost = newHostName });
        }
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: wasPublic);
    }

    /// <summary>带权限校验的踢人操作（修复 MP-P0-01 + MP-P1-11 + D6-P1-07 开赛后禁物理删成员）。</summary>
    public OperationResult KickMember(Lobby lobby, string callerAccountId, string targetAccountId)
    {
        string normCaller = NormalizeAccountKey(callerAccountId);
        string normTarget = NormalizeAccountKey(targetAccountId);

        bool targetIsAi;
        string targetName;
        lock (lobby.Members)
        {
            if (lobby.IsCancelled)
                return new OperationResult(false, 404, "LOBBY_NOT_FOUND", "房间不存在或已解散。");
            if (!lobby.HostAccountId.Equals(normCaller, StringComparison.OrdinalIgnoreCase))
                return new OperationResult(false, 409, "NOT_HOST", "仅房主可以移出房间成员。");
            if (normCaller.Equals(normTarget, StringComparison.OrdinalIgnoreCase))
                return new OperationResult(false, 409, "CANNOT_KICK_SELF", "不能将自己移出房间，如需离开请点击退出房间。");

            var target = lobby.Members.Find(m => m.AccountId.Equals(normTarget, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return new OperationResult(false, 404, "MEMBER_NOT_FOUND", "该成员不在当前房间中。");

            if (lobby.HasStarted)
            {
                // D6-P1-07：开赛后禁止物理移除 AI 或已完赛成员；仅允许对掉线/托管中的未完赛真人做软退赛淘汰
                if (target.IsAi || target.HasFinishedRun)
                    return new OperationResult(false, 409, "CANNOT_KICK_AFTER_START", "对局已经开始，无法删除 AI 或已完赛成员以保护完整对阵历史。");
                if (target.ConnectionState == "online")
                    return new OperationResult(false, 409, "CANNOT_KICK_ACTIVE_PLAYER", "对局进行中，无法踢出在线参赛玩家。");
            }

            targetIsAi = target.IsAi;
            targetName = target.Username;

            if (targetIsAi && !lobby.HasStarted)
            {
                lobby.Members.Remove(target);
            }
        }

        if (targetIsAi)
        {
            PersistLobby(lobby);
            NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
            return new OperationResult(true, 200, null, $"已移出 AI 选手 {targetName}。");
        }

        Leave(normTarget, reason: "kicked");
        return new OperationResult(true, 200, null, $"已将 {targetName} 移出房间。");
    }

    /// <summary>记录成员心跳；若成员先前处于掉线/AI 托管状态，立即无缝交还控制权并广播恢复。</summary>
    public void Touch(string accountId)
    {
        string normAid = NormalizeAccountKey(accountId);
        var lobby = GetByMember(normAid);
        if (lobby == null) return;

        bool restoredFromTakeover = false;
        string restoredName = "";
        lock (lobby.Members)
        {
            var m = lobby.Members.Find(x => x.AccountId.Equals(normAid, StringComparison.OrdinalIgnoreCase));
            if (m != null)
            {
                m.LastSeenAt = UtcNow;
                if (!m.HasFinishedRun &&
                    !string.Equals(m.ConnectionState, "left", StringComparison.OrdinalIgnoreCase) &&
                    m.ConnectionState != "online")
                {
                    restoredFromTakeover = m.ConnectionState == "ai_takeover";
                    m.ConnectionState = "online";
                    m.AiTakeoverSinceUtc = null;
                    restoredName = m.Username;
                }
            }
        }

        if (restoredFromTakeover)
        {
            PersistLobby(lobby);
            PublishTakeoverEvent(lobby.LobbyId, normAid, restoredName, "online", "reconnected");
            _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_takeover", $"✅ 玩家 {restoredName} 已重连归队，解除 AI 托管。", new { lobbyId = lobby.LobbyId, accountId = normAid, state = "online" });
            NotifyLobbyChanged(lobby.LobbyId);
        }
    }

    public void StartCountdown(string lobbyId, Guid sessionId)
    {
        var lobby = Get(lobbyId);
        if (lobby == null) return;
        DateTime now = UtcNow;
        List<Member> snapshot;
        lock (lobby.Members)
        {
            if (lobby.IsCancelled) return;
            int targetCount = Math.Max(lobby.Members.Count, Math.Clamp(lobby.PlayerCap, 2, 16));
            FillAiCompetitors(lobby, targetCount, now);
            lobby.CountdownStarted = true;
            lobby.HasStarted = true;
            lobby.StartedAt ??= now;
            lobby.TournamentSessionId ??= sessionId;
            foreach (var m in lobby.Members)
            {
                if (m.IsAi)
                {
                    m.FinalPrestige = Math.Max(1, lobby.Rules.StartingPrestige);
                    m.FinalVictories = 0;
                    m.FinalDefeats = 0;
                    m.FinalIsEliminated = false;
                    m.CurrentDay = 1;
                }
            }
            snapshot = lobby.Members.ToList();
        }
        EnsureDayMatchups(lobby, 1u, snapshot);
        PersistLobby(lobby);
        PersistFrozenDayMatchups(lobby, 1u);
        NotifyLobbyChanged(lobby.LobbyId, publicListChanged: lobby.IsPublic);
    }

    public Member? FindMember(Lobby lobby, string accountId)
    {
        string normalized = NormalizeAccountKey(accountId);
        lock (lobby.Members)
        {
            return lobby.Members.Find(m =>
                m.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && m.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public Lobby? FindByTournamentSessionId(Guid? tournamentSessionId)
    {
        if (!tournamentSessionId.HasValue || tournamentSessionId.Value == Guid.Empty)
            return null;
        return _byId.Values.FirstOrDefault(l =>
            !l.IsCancelled &&
            l.TournamentSessionId.HasValue &&
            l.TournamentSessionId.Value == tournamentSessionId.Value);
    }

    public Lobby? FindLobbyForRun(Guid? tournamentSessionId, string? accountId)
    {
        var byTid = FindByTournamentSessionId(tournamentSessionId);
        if (byTid != null) return byTid;

        if (!string.IsNullOrWhiteSpace(accountId))
        {
            string normalized = NormalizeAccountKey(accountId);
            var byMem = GetByMember(accountId) ?? (!string.IsNullOrEmpty(normalized) ? GetByMember(normalized) : null);
            if (byMem != null && !byMem.IsCancelled && byMem.HasStarted &&
                (!tournamentSessionId.HasValue || tournamentSessionId.Value == Guid.Empty || byMem.TournamentSessionId == tournamentSessionId))
            {
                return byMem;
            }
        }
        return null;
    }

    /// <summary>记录真人与 AI 交手的真实战果，保证 AI 榜单声望/胜负与真实对局完全同步（修复 MP-P0-05）。</summary>
    public void RecordAiMatchResult(Lobby lobby, string aiAccountId, bool aiWon, uint day)
    {
        if (lobby == null || string.IsNullOrWhiteSpace(aiAccountId)) return;
        string normAi = NormalizeAccountKey(aiAccountId);
        bool updated = false;
        lock (lobby.Members)
        {
            var ai = lobby.Members.Find(m => m.IsAi && m.AccountId.Equals(normAi, StringComparison.OrdinalIgnoreCase));
            if (ai == null || ai.CurrentDay > day) return;

            if (aiWon)
            {
                ai.FinalVictories++;
            }
            else
            {
                ai.FinalDefeats++;
                int dmg = Math.Max(1, (int)day);
                ai.FinalPrestige = lobby.Rules.BotNonLethalPrestigeLock
                    ? Math.Max(1, ai.FinalPrestige - dmg)
                    : Math.Max(0, ai.FinalPrestige - dmg);
                if (!lobby.Rules.BotNonLethalPrestigeLock && ai.FinalPrestige <= 0)
                {
                    ai.FinalIsEliminated = true;
                    ai.HasFinishedRun = true;
                }
            }
            ai.CurrentDay = Math.Max(ai.CurrentDay, day + 1);
            ai.FinalDay = Math.Max(ai.FinalDay, day);
            updated = true;
        }
        if (updated)
        {
            PersistLobby(lobby);
            NotifyLobbyChanged(lobby.LobbyId);
        }
    }

    /// <summary>
    /// 推进第 1..upToDay 天中尚未结算过战绩的 AI 成员内部互战结果（确定性且单调不回退）。
    /// </summary>
    internal void AdvanceAiStandingsUpToDayLocked(Lobby lobby, uint upToDay)
    {
        if (upToDay <= 1) return;
        uint targetFinishedDay = upToDay - 1;
        bool nonLethal = lobby.Rules.BotNonLethalPrestigeLock;

        int aiIdx = 0;
        foreach (var m in lobby.Members)
        {
            if (!m.IsAi) continue;
            int idx = aiIdx++;
            if (m.FinalIsEliminated) continue;

            // 从该 AI 尚未结算的下一天推进到 targetFinishedDay
            uint startDay = Math.Max(1u, m.FinalDay == 1 && m.FinalVictories == 0 && m.FinalDefeats == 0 ? 1u : m.FinalDay + 1u);
            for (uint d = startDay; d <= targetFinishedDay; d++)
            {
                if (m.FinalIsEliminated) break;
                bool wonRound = (((int)d * 3 + idx) % 5) < 3;
                if (wonRound)
                {
                    m.FinalVictories++;
                }
                else
                {
                    m.FinalDefeats++;
                    m.FinalPrestige = nonLethal
                        ? Math.Max(1, m.FinalPrestige - (int)d)
                        : Math.Max(0, m.FinalPrestige - (int)d);
                    if (!nonLethal && m.FinalPrestige <= 0)
                    {
                        m.FinalIsEliminated = true;
                        m.HasFinishedRun = true;
                    }
                }
                m.FinalDay = d;
                m.CurrentDay = d + 1;
            }
        }
        if (targetFinishedDay > lobby.HighestAiSimulatedDay)
            lobby.HighestAiSimulatedDay = targetFinishedDay;
    }

    /// <summary>
    /// 判断第 <paramref name="day"/> 天是否已满足正式冻结对阵表的条件（修复 MP-P0-05 过早锁表）：
    /// Day 1 随时可冻结；Day D (>1) 仅在所有存活非托管真人均已离开 Day D-1（进入 Day D），
    /// 或该天已经冻结过时允许冻结。
    /// </summary>
    internal bool CanFreezeMatchupsForDayLocked(Lobby lobby, uint day, SessionStore? sessions)
    {
        if (day <= 1 || lobby.Matchmaker.HasAssignedDay(day))
            return true;

        foreach (var m in lobby.Members)
        {
            if (m.IsAi) continue;
            if (m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion) continue;
            if (m.ConnectionState == "ai_takeover") continue;

            uint mDay = m.CurrentDay;
            if (sessions != null)
            {
                var mem = sessions.GetByAccount(m.AccountId);
                var snap = mem != null ? SessionRegistry.Get(mem.Sid)?.Run.PublicSummary : null;
                if (snap != null && (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId))
                {
                    if (snap.IsTerminal) continue;
                    mDay = Math.Max(mDay, snap.Day);
                }
            }

            if (mDay < day)
                return false;
        }
        return true;
    }

    public MatchupAssignment? EnsureAndGetAssignment(
        Lobby lobby,
        uint day,
        string accountId,
        SessionStore? sessions = null)
    {
        if (lobby == null || string.IsNullOrWhiteSpace(accountId))
            return null;

        List<MatchupParticipant> participants;
        CustomRunRuleSet rules;
        string canonicalSelfId = accountId;
        bool wasFrozenBefore = lobby.Matchmaker.HasAssignedDay(day);

        lock (lobby.Members)
        {
            AdvanceAiStandingsUpToDayLocked(lobby, day);
            rules = lobby.Rules;
            string normalized = NormalizeAccountKey(accountId);
            var selfMember = lobby.Members.Find(m =>
                m.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && m.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
            if (selfMember != null)
                canonicalSelfId = selfMember.AccountId;

            participants = new List<MatchupParticipant>(lobby.Members.Count);
            foreach (var m in lobby.Members)
            {
                bool elim = m.FinalIsEliminated;
                if (!m.IsAi && !elim && sessions != null)
                {
                    var mem = sessions.GetByAccount(m.AccountId);
                    var snap = mem != null ? SessionRegistry.Get(mem.Sid)?.Run.PublicSummary : null;
                    if (snap != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId))
                    {
                        elim = snap.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                    }
                }
                participants.Add(new MatchupParticipant(m.AccountId, m.Username, m.IsAi, elim));
            }
        }

        // 进入实际开战点（EnsureAndGetAssignment 由 RunEngine 在 Hour 6 开战时调用）必须正式冻结当日配对
        lobby.Matchmaker.AssignRoundMatchups(day, participants, rules);
        if (!wasFrozenBefore)
        {
            PersistFrozenDayMatchups(lobby, day);
            PersistLobby(lobby);
        }
        return lobby.Matchmaker.GetAssignment(canonicalSelfId, day);
    }

    public (int TotalHumans, int SurvivingHumans) CountHumanStanding(
        Lobby lobby,
        string? currentAccountId = null,
        RunState? currentRun = null,
        SessionStore? sessions = null)
    {
        int totalHumans = 0;
        int survivingHumans = 0;
        string normalizedSelf = NormalizeAccountKey(currentAccountId ?? "");

        lock (lobby.Members)
        {
            foreach (var m in lobby.Members)
            {
                if (m.IsAi) continue;
                totalHumans++;

                bool isSelf = !string.IsNullOrWhiteSpace(currentAccountId) &&
                              (m.AccountId.Equals(currentAccountId, StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrEmpty(normalizedSelf) && m.AccountId.Equals(normalizedSelf, StringComparison.OrdinalIgnoreCase)));

                bool eliminated = m.FinalIsEliminated;
                if (isSelf && currentRun != null)
                {
                    eliminated = eliminated || currentRun.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                }
                else if (!eliminated && sessions != null)
                {
                    var mem = sessions.GetByAccount(m.AccountId);
                    var snap = mem != null ? SessionRegistry.Get(mem.Sid)?.Run.PublicSummary : null;
                    if (snap != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId) &&
                        snap.State is ERunState.EndRunDefeat or ERunState.Shutdown)
                    {
                        eliminated = true;
                    }
                }

                if (!eliminated)
                    survivingHumans++;
            }
        }

        return (totalHumans, survivingHumans);
    }

    /// <summary>P0-42 / P0-15：短持 L5a 采集的成员只读权威快照，供 /sessions 重连对齐与终局阻断安全消费（严守 L1 -> L5a 锁序）。</summary>
    public sealed record MemberAuthoritySnapshot(
        string AccountId,
        uint CurrentDay,
        uint CurrentHour,
        uint LastCompletedPvpDay,
        int FinalVictories,
        int FinalDefeats,
        int FinalPrestige,
        bool HasFinishedRun,
        bool FinalIsChampion,
        bool FinalIsEliminated,
        bool FinalIsConceded,
        string RemovalReason);

    public MemberAuthoritySnapshot? TryGetMemberSnapshot(Lobby? lobby, string? accountId)
    {
        if (lobby == null || string.IsNullOrWhiteSpace(accountId))
            return null;
        string normalized = NormalizeAccountKey(accountId);
        lock (lobby.Members)
        {
            var m = lobby.Members.Find(x =>
                x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && x.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
            if (m == null) return null;
            return new MemberAuthoritySnapshot(
                m.AccountId,
                m.CurrentDay,
                m.CurrentHour,
                m.LastCompletedPvpDay,
                m.FinalVictories,
                m.FinalDefeats,
                m.FinalPrestige,
                m.HasFinishedRun,
                m.FinalIsChampion,
                m.FinalIsEliminated,
                m.FinalIsConceded,
                m.RemovalReason ?? "");
        }
    }

    public void RecordRunProgress(Lobby lobby, string accountId, RunState run)
    {
        if (lobby == null || run == null || string.IsNullOrWhiteSpace(accountId))
            return;

        // 持 Gate 写侧或单测路径先刷新一次不可变公开摘要，再走统一的不可变快照更新逻辑
        var snap = run.PublishPublicSummary();
        RecordRunProgressSnapshot(lobby, accountId, snap);
    }

    /// <summary>D8-P1-02：基于不可变 RunPublicSummarySnapshot 同步成员对局进度，彻底消除无锁跨线程读可变 RunState。</summary>
    public void RecordRunProgressSnapshot(Lobby lobby, string accountId, RunPublicSummarySnapshot snap)
    {
        if (lobby == null || snap == null || string.IsNullOrWhiteSpace(accountId))
            return;

        string normalized = NormalizeAccountKey(accountId);
        bool changed = false;
        bool restoredFromTakeover = false;
        bool newlyChampion = false;
        bool newlyEliminated = false;
        string memberName = "";
        int finalWins = 0;

        lock (lobby.Members)
        {
            var m = lobby.Members.Find(x =>
                x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && x.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
            if (m != null)
            {
                DateTime now = UtcNow;
                bool isTerminal = snap.IsTerminal;

                // P0-42 守卫 1（终态不可逆）：已完赛/已 AFK 淘汰成员绝不允许被陈旧非终态 RunState 覆写复活
                if (m.HasFinishedRun)
                    return;

                // P0-42 守卫 2（语义纯粹的单调不倒流）：仅拦截 snap.Day < m.CurrentDay 或同天未包含已结算托管 PvP 的旧快照；
                // 严禁用混义 m.FinalDay 对比 LastCompletedPvpDay，确保曾托管 1 天的玩家重连正常玩 Day 2 Hour 1..6 100% 放行！
                bool isStaleDay = snap.Day < m.CurrentDay;
                bool isStaleSameDayPvp = snap.Day == m.CurrentDay
                    && !isTerminal
                    && m.LastCompletedPvpDay > 0
                    && snap.LastCompletedPvpDay < m.LastCompletedPvpDay;

                if (isStaleDay || isStaleSameDayPvp)
                {
                    if (m.ConnectionState == "ai_takeover")
                        restoredFromTakeover = true;
                    if (m.ConnectionState != "online")
                        changed = true;
                    m.LastSeenAt = now;
                    m.ConnectionState = "online";
                    m.AiTakeoverSinceUtc = null;
                    memberName = m.Username;
                }
                else
                {
                    bool readyH6 = !isTerminal && snap.IsReadyAtHour6Pvp;
                    int prestige = snap.Prestige;
                    string heroName = (snap.Hero == EHero.Common ? EHero.Vanessa : snap.Hero).ToString();
                    uint nextLastCompletedPvpDay = Math.Max(m.LastCompletedPvpDay, snap.LastCompletedPvpDay);

                    if (m.CurrentDay != snap.Day ||
                        m.CurrentHour != snap.Hour ||
                        m.IsReadyAtHour6Pvp != readyH6 ||
                        m.LastCompletedPvpDay != nextLastCompletedPvpDay ||
                        m.FinalVictories != (int)snap.Victories ||
                        m.FinalDefeats != (int)snap.Defeats ||
                        m.FinalPrestige != prestige ||
                        m.FinalDay != snap.Day ||
                        m.Hero != heroName ||
                        m.ConnectionState != "online" ||
                        (isTerminal && !m.HasFinishedRun))
                    {
                        changed = true;
                    }

                    if (m.ConnectionState == "ai_takeover")
                        restoredFromTakeover = true;

                    m.LastSeenAt = now;
                    m.ConnectionState = "online";
                    m.AiTakeoverSinceUtc = null;
                    m.Hero = heroName;
                    m.CurrentDay = snap.Day;
                    m.CurrentHour = snap.Hour;
                    m.IsReadyAtHour6Pvp = readyH6;
                    m.LastCompletedPvpDay = nextLastCompletedPvpDay;
                    m.FinalVictories = (int)snap.Victories;
                    m.FinalDefeats = (int)snap.Defeats;
                    m.FinalPrestige = prestige;
                    m.FinalDay = snap.Day;
                    memberName = m.Username;
                    finalWins = (int)snap.Victories;

                    if (isTerminal && !m.HasFinishedRun)
                    {
                        m.HasFinishedRun = true;
                        m.FinalIsChampion = snap.State == ERunState.EndRunVictory;
                        m.FinalIsEliminated = snap.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                        newlyChampion = m.FinalIsChampion;
                        newlyEliminated = m.FinalIsEliminated;
                    }
                }
            }
        }

        if (changed)
        {
            PersistLobby(lobby);
            if (restoredFromTakeover)
            {
                PublishTakeoverEvent(lobby.LobbyId, normalized, memberName, "online", "reconnected");
                _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_takeover", $"✅ 玩家 {memberName} 已重连并接回对局控制权。", new { lobbyId = lobby.LobbyId, accountId = normalized, state = "online" });
            }
            if (newlyChampion)
            {
                _socialSink?.PublishSystemBroadcast(null, "system_champion", $"🏆 {memberName} 在【{lobby.Name}】中以 {finalWins} 胜夺得最终冠军！", new { lobbyId = lobby.LobbyId, watchCode = lobby.Code, username = memberName, wins = finalWins, day = snap.Day });
            }
            else if (newlyEliminated)
            {
                _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_eliminated", $"💀 {memberName} 在第 {snap.Day} 天被淘汰出局。", new { lobbyId = lobby.LobbyId, username = memberName, day = snap.Day });
            }
            NotifyLobbyChanged(lobby.LobbyId);
        }
    }

    /// <summary>P0-16：权威判定该局当日 PvP 是否已完成（兼容历史未填 LastCompletedPvpDay 的单测/旧快照，根治 BossRush/同步平局误判）。</summary>
    public static bool HasCompletedPvpForDay(RunState run)
    {
        if (run == null) return false;
        if (run.LastCompletedPvpDay >= run.Day) return true;
        if (run.LastCompletedPvpDay == 0 &&
            (run.CustomRules?.SpecialModeId ?? ESpecialModeId.Standard) != ESpecialModeId.BossRush &&
            (run.Victories + run.Defeats) >= run.Day)
        {
            return true;
        }
        return false;
    }

    public static bool ComputeIsAtOrPastHour6Pvp(RunState run)
    {
        if (run == null || Persistence.IsTerminalState(run.State))
            return false;
        return run.State == ERunState.TournamentPvpChoice
            || run.State == ERunState.TournamentWaitingRoom
            || run.State == ERunState.PVPCombat
            || (run.Hour >= 6 && HasCompletedPvpForDay(run))
            || (run.State == ERunState.Choice && run.Hour >= 6);
    }

    public PvpBarrierDecision CheckPvpBarrier(
        Guid? tournamentSessionId,
        string? accountId,
        uint day,
        RunState? currentRun = null,
        SessionStore? sessions = null,
        bool startOrAdvanceCountdown = true)
    {
        var lobby = FindLobbyForRun(tournamentSessionId, accountId);
        if (lobby == null)
        {
            return new PvpBarrierDecision(true, false, null, 1, 1, 0, false, false, 0, null);
        }
        return EvaluateRoundBarrier(lobby, day, accountId, currentRun, sessions, startOrAdvanceCountdown);
    }

    public PvpBarrierDecision EvaluateRoundBarrier(
        Lobby lobby,
        uint day,
        string? selfAccountId = null,
        RunState? selfRun = null,
        SessionStore? sessions = null,
        bool startOrAdvanceCountdown = true)
    {
        if (lobby == null)
            return new PvpBarrierDecision(true, false, null, 1, 1, 0, false, false, 0, null);

        if (day == 0) day = 1;
        string normalizedSelf = NormalizeAccountKey(selfAccountId ?? "");
        bool shouldNotify = false;
        bool shouldPersist = false;
        List<string> waitingUsernames = new();
        PvpBarrierDecision decision;

        lock (lobby.Members)
        {
            int survivingHumans = 0;
            int readyHumans = 0;
            int notReadyHumans = 0;

            foreach (var m in lobby.Members)
            {
                if (m.IsAi) continue;

                bool isSelf = !string.IsNullOrWhiteSpace(selfAccountId) &&
                              (m.AccountId.Equals(selfAccountId, StringComparison.OrdinalIgnoreCase) ||
                               (!string.IsNullOrEmpty(normalizedSelf) && m.AccountId.Equals(normalizedSelf, StringComparison.OrdinalIgnoreCase)));

                // D8-P1-02：他人状态严格读取不可变 volatile RunPublicSummarySnapshot，彻底消灭无锁跨线程读可变 RunState 与 (Day, Hour) 撕裂态
                RunPublicSummarySnapshot? liveSnap = null;
                if (isSelf && selfRun != null)
                {
                    selfRun.PublishPublicSummary();
                    liveSnap = selfRun.PublicSummary;
                }
                else if (sessions != null)
                {
                    var mem = sessions.GetByAccount(m.AccountId);
                    var candidateSnap = mem != null ? SessionRegistry.Get(mem.Sid)?.Run.PublicSummary : null;
                    if (candidateSnap != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId))
                    {
                        liveSnap = candidateSnap;
                    }
                }

                if (liveSnap != null)
                {
                    bool term = liveSnap.IsTerminal;
                    bool h6Ready = !term && liveSnap.IsReadyAtHour6Pvp;
                    if (m.CurrentDay != liveSnap.Day || m.CurrentHour != liveSnap.Hour || m.IsReadyAtHour6Pvp != h6Ready)
                    {
                        m.CurrentDay = liveSnap.Day;
                        m.CurrentHour = liveSnap.Hour;
                        m.IsReadyAtHour6Pvp = h6Ready;
                    }
                    if (term && !m.HasFinishedRun)
                    {
                        m.HasFinishedRun = true;
                        m.FinalIsChampion = liveSnap.State == ERunState.EndRunVictory;
                        m.FinalIsEliminated = liveSnap.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                    }
                }

                bool elimOrDone = m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion
                                  || (liveSnap != null && liveSnap.IsTerminal);
                if (elimOrDone)
                    continue;

                // 修复 MP-P0-03：已切换为 AI 托管接管的掉线玩家立即移出屏障阻塞人数，绝不卡死全房！
                if (string.Equals(m.ConnectionState, "ai_takeover", StringComparison.OrdinalIgnoreCase))
                    continue;

                survivingHumans++;
                bool reachedDayHour6 = m.CurrentDay > day || (m.CurrentDay == day && m.IsReadyAtHour6Pvp);
                if (reachedDayHour6)
                {
                    readyHumans++;
                }
                else
                {
                    notReadyHumans++;
                    waitingUsernames.Add(m.Username);
                }
            }

            if (survivingHumans <= 1)
            {
                decision = new PvpBarrierDecision(true, false, null, survivingHumans, readyHumans, notReadyHumans, false, false, 0, null);
            }
            else if (notReadyHumans == 0)
            {
                // D6-P1-07：全员按时就位仅置 AllReadyUnlocked = true，绝不误置 TimedOutUnlocked = true（避免客户端误发 ForcedTransition）
                if (lobby.RoundBarriers.TryGetValue(day, out var doneBarrier) && !doneBarrier.AllReadyUnlocked)
                {
                    doneBarrier.AllReadyUnlocked = true;
                    shouldNotify = true;
                    shouldPersist = true;
                }
                decision = new PvpBarrierDecision(true, false, null, survivingHumans, readyHumans, 0, false, false, 0, null);
            }
            else
            {
                lobby.RoundBarriers.TryGetValue(day, out var barrier);
                DateTime now = UtcNow;
                long monoNow = MonotonicNowMs;

                if (barrier is { AllReadyUnlocked: true } or { TimedOutUnlocked: true })
                {
                    bool isTimedOut = barrier.TimedOutUnlocked;
                    decision = new PvpBarrierDecision(
                        true, false, barrier.DeadlineAtUtc,
                        survivingHumans, readyHumans, notReadyHumans,
                        barrier.IsAccelerated, isTimedOut, 0, null);
                }
                else if (readyHumans == 0)
                {
                    decision = new PvpBarrierDecision(
                        false, false, barrier?.DeadlineAtUtc,
                        survivingHumans, 0, notReadyHumans,
                        barrier?.IsAccelerated ?? false, false, 0,
                        $"需等待同房真人玩家到达第 {day} 天第 6 小时");
                }
                else
                {
                    if (startOrAdvanceCountdown)
                    {
                        if (barrier == null)
                        {
                            barrier = new RoundBarrierState { Day = day };
                            lobby.RoundBarriers[day] = barrier;
                        }
                        if (!barrier.FirstReadyAtUtc.HasValue)
                        {
                            int cdSec = Math.Max(0, lobby.RoundCountdownSeconds);
                            barrier.FirstReadyAtUtc = now;
                            barrier.FirstReadyMonotonicMs = monoNow;
                            barrier.DeadlineAtUtc = now.AddSeconds(cdSec);
                            barrier.DeadlineMonotonicMs = monoNow + cdSec * 1000L;
                            shouldNotify = true;
                            shouldPersist = true;
                        }
                        else if (!barrier.DeadlineMonotonicMs.HasValue && barrier.DeadlineAtUtc.HasValue)
                        {
                            long remMs = Math.Max(0L, (long)(barrier.DeadlineAtUtc.Value - now).TotalMilliseconds);
                            barrier.DeadlineMonotonicMs = monoNow + remMs;
                            barrier.FirstReadyMonotonicMs ??= monoNow - Math.Max(0L, (long)(now - barrier.FirstReadyAtUtc.Value).TotalMilliseconds);
                        }
                        if (notReadyHumans == 1 && !barrier.IsAccelerated)
                        {
                            barrier.IsAccelerated = true;
                            int accelSec = Math.Max(0, lobby.AcceleratedRoundCountdownSeconds);
                            long accelMono = monoNow + accelSec * 1000L;
                            DateTime accelDeadline = now.AddSeconds(accelSec);
                            if (!barrier.DeadlineMonotonicMs.HasValue || accelMono < barrier.DeadlineMonotonicMs.Value)
                            {
                                barrier.DeadlineMonotonicMs = accelMono;
                                barrier.DeadlineAtUtc = accelDeadline;
                            }
                            shouldNotify = true;
                            shouldPersist = true;
                        }
                        if (barrier.DeadlineMonotonicMs.HasValue
                            ? monoNow >= barrier.DeadlineMonotonicMs.Value
                            : (barrier.DeadlineAtUtc.HasValue && now >= barrier.DeadlineAtUtc.Value))
                        {
                            barrier.TimedOutUnlocked = true;
                            shouldNotify = true;
                            shouldPersist = true;
                        }
                    }

                    if (barrier is { TimedOutUnlocked: true })
                    {
                        decision = new PvpBarrierDecision(
                            true, false, barrier.DeadlineAtUtc,
                            survivingHumans, readyHumans, notReadyHumans,
                            barrier.IsAccelerated, true, 0, null);
                    }
                    else
                    {
                        DateTime? dl = barrier?.DeadlineAtUtc;
                        int remSec;
                        if (barrier?.DeadlineMonotonicMs.HasValue == true)
                        {
                            double remS = (barrier.DeadlineMonotonicMs.Value - monoNow) / 1000.0;
                            remSec = Math.Max(1, (int)Math.Ceiling(remS));
                        }
                        else if (dl.HasValue)
                        {
                            remSec = Math.Max(1, (int)Math.Ceiling((dl.Value - now).TotalSeconds));
                        }
                        else
                        {
                            remSec = Math.Max(1, notReadyHumans == 1 ? lobby.AcceleratedRoundCountdownSeconds : lobby.RoundCountdownSeconds);
                        }
                        decision = new PvpBarrierDecision(
                            false, true, dl,
                            survivingHumans, readyHumans, notReadyHumans,
                            barrier?.IsAccelerated ?? false, false, remSec,
                            $"正在等待其他真人玩家完成第 {day} 天准备（未就位 {notReadyHumans} 人，剩余 {remSec} 秒）");
                    }
                }
            }
        }

        if (shouldPersist)
            PersistLobby(lobby);
        if (shouldNotify)
        {
            PublishBarrierEvent(lobby.LobbyId, day, decision, waitingUsernames);
            NotifyLobbyChanged(lobby.LobbyId);
        }

        return decision;
    }

    // ── 后台看门狗（1s 屏障主动唤醒 + 掉线 AI 托管接管 + 过期房归档）──────────

    private sealed record HumanWatchdogSnapshot(
        string AccountId,
        uint CurrentDay,
        bool IsReadyAtHour6Pvp,
        string ConnectionState,
        bool HasFinishedRun,
        bool FinalIsEliminated);

    private sealed record TakeoverStepPlan(
        string AccountId,
        string Username,
        uint SimDay,
        int NextMissedPvpDays,
        Guid? TournamentSessionId,
        bool BarrierTimedOut);

    private sealed record TakeoverStepResult(
        string AccountId,
        uint SimDay,
        bool WonByTakeover,
        bool IsDraw,
        bool Deferred);

    private sealed record TakeoverRunSyncAction(
        string AccountId,
        Guid TournamentSessionId,
        uint SimDay,
        int FinalVictories,
        int FinalDefeats,
        int FinalPrestige,
        bool IsEliminated,
        string RemovalReason);

    /// <summary>
    /// 1 秒级后台看门狗单次扫描（支持测试中手工触发）：
    /// 1. 驱动掉线状态机 online -> disconnected (45s) -> ai_takeover (阻塞屏障30s或累计90s)；
    /// 2. 采用四阶段无跨锁拆分（阶段1收集单日计划 -> 阶段2无锁协调 PvP 裁决与在线真人防截胡门控 -> 阶段3原子复核应用 -> 阶段4彻底释放 L5a 后按 L1->L2->L6/L7 同步推进/终结掉线 RunState 与 SQLite），严格服从 11 锁全局 DAG；
    /// 3. 主动扫描并解锁已达 DeadlineAtUtc 的所有 RoundBarriers，推送房间级 SSE 唤醒等待客户端。
    /// </summary>
    public void RunWatchdogTick(SessionStore? sessions = null, ITournamentPvpCoordinator? pvpCoordinator = null)
    {
        DateTime now = UtcNow;
        long monoNow = MonotonicNowMs;
        foreach (var lobby in _byId.Values)
        {
            if (lobby.IsCancelled || !lobby.HasStarted)
                continue;

            bool lobbyDirty = false;
            var takeoverEvents = new List<(string AccountId, string Username, string State, string Reason)>();
            var afkEliminations = new List<(string AccountId, string Username, uint Day)>();
            var daysToCheckBarrier = new HashSet<uint>();
            var runSyncActions = new List<TakeoverRunSyncAction>();

            // 阶段 0：短持 lobby.Members，仅推进掉线状态机与收集待检查屏障
            lock (lobby.Members)
            {
                uint maxOnlineDay = 1;
                foreach (var m in lobby.Members)
                {
                    if (!m.IsAi && !m.HasFinishedRun && !m.FinalIsEliminated && m.ConnectionState == "online")
                    {
                        if (m.CurrentDay > maxOnlineDay) maxOnlineDay = m.CurrentDay;
                    }
                }

                // 收集当前尚未解锁的屏障天（全员就位 AllReadyUnlocked 或超时 TimedOutUnlocked 均视为已解锁）
                foreach (var kv in lobby.RoundBarriers)
                {
                    if (!kv.Value.AllReadyUnlocked && !kv.Value.TimedOutUnlocked)
                        daysToCheckBarrier.Add(kv.Key);
                }
                if (maxOnlineDay >= 1) daysToCheckBarrier.Add(maxOnlineDay);

                foreach (var m in lobby.Members)
                {
                    if (m.IsAi || m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion)
                        continue;

                    DateTime lastAct = m.LastSeenAt;
                    if (sessions != null)
                    {
                        var sess = sessions.GetByAccount(m.AccountId);
                        if (sess != null && sess.LastCommandAt > lastAct)
                            lastAct = sess.LastCommandAt;
                    }

                    double offlineSec = (now - lastAct).TotalSeconds;
                    if (m.ConnectionState == "online" && offlineSec >= DisconnectGraceSeconds)
                    {
                        m.ConnectionState = "disconnected";
                        lobbyDirty = true;
                        takeoverEvents.Add((m.AccountId, m.Username, "disconnected", "heartbeat_timeout"));
                    }

                    if (m.ConnectionState == "disconnected")
                    {
                        bool blockingActiveBarrier = lobby.RoundBarriers.Values.Any(b =>
                            !b.AllReadyUnlocked &&
                            !b.TimedOutUnlocked &&
                            (b.FirstReadyMonotonicMs.HasValue
                                ? (monoNow - b.FirstReadyMonotonicMs.Value) >= TakeoverBarrierGraceSeconds * 1000L
                                : (b.FirstReadyAtUtc.HasValue && (now - b.FirstReadyAtUtc.Value).TotalSeconds >= TakeoverBarrierGraceSeconds)) &&
                            (m.CurrentDay < b.Day || (m.CurrentDay == b.Day && !m.IsReadyAtHour6Pvp)));

                        if (blockingActiveBarrier || offlineSec >= TakeoverTotalDisconnectSeconds)
                        {
                            m.ConnectionState = "ai_takeover";
                            m.AiTakeoverSinceUtc = now;
                            lobbyDirty = true;
                            takeoverEvents.Add((m.AccountId, m.Username, "ai_takeover", blockingActiveBarrier ? "barrier_timeout" : "disconnect_timeout"));
                        }
                    }
                }
            }

            // 阶段 1 -> 2 -> 3：逐日无跨锁推进托管滞后天数（每次仅采集 1 天，避免第 1 天已淘汰却提前替第 2 天向 pvpCoordinator 写入负场）
            int maxAdvanceSteps = MaxMissedTakeoverDays + 1;
            for (int step = 0; step < maxAdvanceSteps; step++)
            {
                var takeoverPlans = new List<TakeoverStepPlan>();
                var humanSnaps = new Dictionary<string, HumanWatchdogSnapshot>(StringComparer.OrdinalIgnoreCase);
                lock (lobby.Members)
                {
                    if (lobby.IsCancelled)
                        break;

                    uint maxOnlineDay = 1;
                    foreach (var m in lobby.Members)
                    {
                        if (!m.IsAi)
                        {
                            humanSnaps[m.AccountId] = new HumanWatchdogSnapshot(
                                m.AccountId,
                                m.CurrentDay,
                                m.IsReadyAtHour6Pvp,
                                m.ConnectionState,
                                m.HasFinishedRun,
                                m.FinalIsEliminated);
                        }
                        if (!m.IsAi && !m.HasFinishedRun && !m.FinalIsEliminated && m.ConnectionState == "online")
                        {
                            if (m.CurrentDay > maxOnlineDay) maxOnlineDay = m.CurrentDay;
                        }
                    }

                    foreach (var m in lobby.Members)
                    {
                        if (m.IsAi || m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion)
                            continue;
                        if (m.ConnectionState == "ai_takeover" && m.CurrentDay < maxOnlineDay)
                        {
                            bool barrierTimedOut = lobby.RoundBarriers.TryGetValue(m.CurrentDay, out var br) && br.TimedOutUnlocked;
                            takeoverPlans.Add(new TakeoverStepPlan(
                                AccountId: m.AccountId,
                                Username: m.Username,
                                SimDay: m.CurrentDay,
                                NextMissedPvpDays: m.MissedPvpDays + 1,
                                TournamentSessionId: lobby.TournamentSessionId,
                                BarrierTimedOut: barrierTimedOut));
                        }
                    }
                }

                if (takeoverPlans.Count == 0)
                    break;

                // 阶段 2（完全无锁）：对称裁决查询/落盘，绝不持有 lobby.Members，且严格保护在线游玩真人不被第三方领跑提前判负截胡（P0-42）
                var takeoverResults = new List<TakeoverStepResult>(takeoverPlans.Count);
                foreach (var plan in takeoverPlans)
                {
                    bool wonByTakeover = false;
                    bool isDraw = false;
                    bool deferred = false;
                    if (plan.TournamentSessionId.HasValue && pvpCoordinator != null)
                    {
                        var assign = lobby.Matchmaker.GetAssignment(plan.AccountId, plan.SimDay);
                        if (assign != null && !assign.OpponentIsBot)
                        {
                            var resolved = pvpCoordinator.TryGetResolvedMatch(plan.TournamentSessionId.Value, plan.SimDay, plan.AccountId, assign.OpponentAccountId);
                            if (resolved != null)
                            {
                                isDraw = string.Equals(resolved.WinnerAccountId, "__draw__", StringComparison.OrdinalIgnoreCase);
                                wonByTakeover = !isDraw && string.Equals(resolved.WinnerAccountId, plan.AccountId, StringComparison.OrdinalIgnoreCase);
                            }
                            else
                            {
                                // P0-42：仅当真人对手已就位/越天/已锁板、对手自身也托管或终局、或本轮屏障已正式超时解锁时才允许落盘缺席判负
                                bool oppReadyOrPast = false;
                                bool oppOfflineOrFinished = false;
                                if (humanSnaps.TryGetValue(assign.OpponentAccountId, out var oppSnap))
                                {
                                    oppReadyOrPast = oppSnap.CurrentDay > plan.SimDay
                                        || (oppSnap.CurrentDay == plan.SimDay && oppSnap.IsReadyAtHour6Pvp);
                                    oppOfflineOrFinished = oppSnap.ConnectionState == "ai_takeover"
                                        || oppSnap.HasFinishedRun
                                        || oppSnap.FinalIsEliminated;
                                }
                                bool oppHasLockedBoard = pvpCoordinator.TryGetLockedBoard(
                                    plan.TournamentSessionId.Value, plan.SimDay, assign.OpponentAccountId) != null;

                                if (oppReadyOrPast || oppHasLockedBoard || oppOfflineOrFinished || plan.BarrierTimedOut)
                                {
                                    // 无人先结算时记录保守判负（避免掉线托管白嫖胜利）；若对手并发抢先提交则服从权威结果
                                    var committed = pvpCoordinator.RecordOrGetResolvedMatch(
                                        plan.TournamentSessionId.Value,
                                        plan.SimDay,
                                        plan.AccountId,
                                        assign.OpponentAccountId,
                                        new SymmetricHumanPvpOutcome(
                                            WinnerAccountId: assign.OpponentAccountId,
                                            LoserAccountId: plan.AccountId,
                                            BattleSeed: assign.BattleSeed,
                                            LoserPrestigeDamage: Math.Max(1, (int)plan.SimDay),
                                            ReplayId: "",
                                            CachedCombatSimForP1: null,
                                            CachedCombatSimForP2: null));
                                    isDraw = string.Equals(committed.WinnerAccountId, "__draw__", StringComparison.OrdinalIgnoreCase);
                                    wonByTakeover = !isDraw && string.Equals(committed.WinnerAccountId, plan.AccountId, StringComparison.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    // 真人对手仍在当日 Hour 1..5 正常在线游玩：暂缓托管结算，绝不提前截胡！
                                    deferred = true;
                                }
                            }
                        }
                        else
                        {
                            // 对阵 AI 托管采用确定性中等胜率
                            wonByTakeover = ((plan.SimDay + plan.NextMissedPvpDays) % 2) == 0;
                        }
                    }
                    takeoverResults.Add(new TakeoverStepResult(plan.AccountId, plan.SimDay, wonByTakeover, isDraw, deferred));
                }

                // 阶段 3（短持 lobby.Members）：原子复核成员仍处于 ai_takeover 且当前天数未变更后应用进度，并收集阶段 4 RunState 同步动作
                bool anyApplied = false;
                lock (lobby.Members)
                {
                    if (lobby.IsCancelled)
                        break;

                    foreach (var res in takeoverResults)
                    {
                        if (res.Deferred)
                            continue;

                        var m = lobby.Members.Find(x => x.AccountId.Equals(res.AccountId, StringComparison.OrdinalIgnoreCase));
                        if (m == null || m.IsAi || m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion)
                            continue;
                        if (m.ConnectionState != "ai_takeover" || m.CurrentDay != res.SimDay)
                            continue;

                        m.MissedPvpDays++;
                        if (res.IsDraw)
                        {
                            // 平局不增胜负不扣声望
                        }
                        else if (res.WonByTakeover)
                        {
                            m.FinalVictories++;
                        }
                        else
                        {
                            m.FinalDefeats++;
                            m.FinalPrestige = Math.Max(0, m.FinalPrestige - Math.Max(1, (int)res.SimDay));
                        }

                        m.LastCompletedPvpDay = Math.Max(m.LastCompletedPvpDay, res.SimDay);
                        m.CurrentDay = res.SimDay + 1;
                        m.CurrentHour = 1;
                        m.IsReadyAtHour6Pvp = false;
                        m.FinalDay = res.SimDay;
                        lobbyDirty = true;
                        anyApplied = true;

                        bool newlyEliminated = false;
                        if (m.FinalPrestige <= 0 || m.MissedPvpDays >= MaxMissedTakeoverDays)
                        {
                            m.FinalIsEliminated = true;
                            m.HasFinishedRun = true;
                            m.RemovalReason = "afk";
                            newlyEliminated = true;
                            afkEliminations.Add((m.AccountId, m.Username, res.SimDay));
                        }

                        if (lobby.TournamentSessionId.HasValue)
                        {
                            runSyncActions.Add(new TakeoverRunSyncAction(
                                AccountId: m.AccountId,
                                TournamentSessionId: lobby.TournamentSessionId.Value,
                                SimDay: res.SimDay,
                                FinalVictories: m.FinalVictories,
                                FinalDefeats: m.FinalDefeats,
                                FinalPrestige: m.FinalPrestige,
                                IsEliminated: newlyEliminated || m.FinalIsEliminated,
                                RemovalReason: newlyEliminated ? "afk_eliminated" : (m.RemovalReason ?? "")));
                        }
                    }
                }

                if (!anyApplied)
                    break;
            }

            if (lobbyDirty)
            {
                PersistLobby(lobby);
                foreach (var ev in takeoverEvents)
                {
                    PublishTakeoverEvent(lobby.LobbyId, ev.AccountId, ev.Username, ev.State, ev.Reason);
                    if (ev.State == "ai_takeover")
                    {
                        _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_takeover", $"🤖 玩家 {ev.Username} 掉线超时，已由系统 AI 托管接管，不再阻塞全房回合。", new { lobbyId = lobby.LobbyId, accountId = ev.AccountId, state = "ai_takeover" });
                    }
                }
                foreach (var elim in afkEliminations)
                {
                    _socialSink?.PublishSystemBroadcast(lobby.LobbyId, "system_eliminated", $"⌛ 玩家 {elim.Username} 连续托管缺席或声望归零，已在第 {elim.Day} 天判定淘汰。", new { lobbyId = lobby.LobbyId, accountId = elim.AccountId, day = elim.Day, reason = "afk" });
                }
                NotifyLobbyChanged(lobby.LobbyId);
            }

            // 阶段 4（完全在 L5a 锁外执行！）：按严格全局正序 L1 (AccountLock) -> L2 (Session.Gate) -> L6/L7 (Persistence)
            // 同步推进或终结掉线玩家的 RunState 与 SQLite，并清理僵尸内存会话（P0-42）
            if (runSyncActions.Count > 0)
            {
                // 同账号多次步进合并为最新终态动作，避免冗余重复落盘
                var latestByAccount = new Dictionary<string, TakeoverRunSyncAction>(StringComparer.OrdinalIgnoreCase);
                foreach (var act in runSyncActions)
                {
                    if (!latestByAccount.TryGetValue(act.AccountId, out var prev) ||
                        act.IsEliminated ||
                        act.SimDay >= prev.SimDay)
                    {
                        latestByAccount[act.AccountId] = act;
                    }
                }
                foreach (var action in latestByAccount.Values)
                {
                    ApplyTakeoverRunSyncOutOfLobbyLock(action, sessions);
                }
            }

            foreach (uint d in daysToCheckBarrier)
            {
                EvaluateRoundBarrier(lobby, d, null, null, sessions, startOrAdvanceCountdown: true);
            }
        }
    }

    /// <summary>
    /// P0-42 阶段 4：彻底在 L5a 锁外按严格正序 L1 (AccountSessionLock, 200ms TryWait) -> L2 (Session.Gate, 200ms TryWait) -> L6/L7 (Persistence)
    /// 同步推进或终结掉线玩家内存 RunState 与 SQLite 快照，并分离僵尸会话防止空 SelectionSet 内存态残留。
    /// </summary>
    private void ApplyTakeoverRunSyncOutOfLobbyLock(TakeoverRunSyncAction action, SessionStore? sessions)
    {
        var accountLock = GameServerEndpoints.GetAccountSessionLock(action.AccountId);
        if (!accountLock.Wait(TimeSpan.FromMilliseconds(200)))
            return;
        try
        {
            bool handledInMemory = false;
            var mem = sessions?.GetByAccount(action.AccountId);
            if (mem != null && mem.TournamentSessionId == action.TournamentSessionId)
            {
                if (mem.Gate.Wait(TimeSpan.FromMilliseconds(200)))
                {
                    try
                    {
                        var handle = SessionRegistry.Get(mem.Sid);
                        if (handle != null && handle.Run.TournamentSessionId == action.TournamentSessionId)
                        {
                            var run = handle.Run;
                            // 首行强制 TOCTOU 二次复核：若玩家已在间隙主动投降/阵亡/夺冠，绝不覆写已终局态！
                            if (Persistence.IsTerminalState(run.State))
                            {
                                handledInMemory = true;
                            }
                            else if (action.IsEliminated)
                            {
                                run.State = ERunState.EndRunDefeat;
                                run.Day = Math.Max(run.Day, action.SimDay);
                                run.Victories = Math.Max(run.Victories, (uint)Math.Max(0, action.FinalVictories));
                                run.Defeats = Math.Max(run.Defeats, (uint)Math.Max(0, action.FinalDefeats));
                                run.LastCompletedPvpDay = Math.Max(run.LastCompletedPvpDay, action.SimDay);
                                run.PlayerAttributes[EPlayerAttributeType.Prestige] = 0;
                                run.PublishPublicSummary();

                                // 严格落盘序（兼顾 W1-A SQL 终态守卫）：先写含 EndRunDefeat 的完整终局快照，再 MarkRunEnded
                                if (_db != null)
                                {
                                    try
                                    {
                                        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: run.StartSelectionDone);
                                        _db.SaveRunSnapshot(run, action.AccountId, snapJson, isCustomRule: run.IsCustomRuleRun);
                                        _db.MarkRunEnded(run.RunId, ERunState.EndRunDefeat, "afk_eliminated");
                                    }
                                    catch { }
                                }
                                SessionRegistry.Detach(mem.Sid);
                                GameServerEndpoints.DetachRidCache(mem.Sid);
                                sessions!.Remove(mem.Sid);
                                handledInMemory = true;
                            }
                            else if (run.Day <= action.SimDay)
                            {
                                // 仅当内存会话仍停留在 <= action.SimDay 时推进（防止 TOCTOU 清空已抢先进入 Day+1 的在线玩家盘面！）
                                run.Day = action.SimDay + 1;
                                run.Hour = 1;
                                run.State = ERunState.Choice;
                                run.CurrentEncounterId = null;
                                run.ActiveEncounterCardInstanceId = null;
                                run.SelectionSet.Clear();
                                run.SuspendedStates.Clear();
                                run.Victories = Math.Max(run.Victories, (uint)Math.Max(0, action.FinalVictories));
                                run.Defeats = Math.Max(run.Defeats, (uint)Math.Max(0, action.FinalDefeats));
                                run.LastCompletedPvpDay = Math.Max(run.LastCompletedPvpDay, action.SimDay);
                                int curPrestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, action.FinalPrestige);
                                run.PlayerAttributes[EPlayerAttributeType.Prestige] = Math.Max(0, Math.Min(curPrestige, action.FinalPrestige));
                                run.PublishPublicSummary();

                                if (_db != null)
                                {
                                    try
                                    {
                                        string snapJson = RunSnapshotSerializer.Serialize(run, startSelectionDone: run.StartSelectionDone);
                                        _db.SaveRunSnapshot(run, action.AccountId, snapJson, isCustomRule: run.IsCustomRuleRun);
                                    }
                                    catch { }
                                }
                                // 分离旧内存会话，令重连走 /sessions 自愈补发 Day N Hour 1 完整候选，绝不留空 SelectionSet 僵尸会话
                                SessionRegistry.Detach(mem.Sid);
                                GameServerEndpoints.DetachRidCache(mem.Sid);
                                sessions!.Remove(mem.Sid);
                                handledInMemory = true;
                            }
                            else
                            {
                                // 玩家已在线推进至 >= SimDay + 1，尊重在线状态不干扰
                                handledInMemory = true;
                            }
                        }
                    }
                    finally
                    {
                        mem.Gate.Release();
                    }
                }
            }

            // 内存无活跃会话（如服务曾重启或先前已分离）：针对淘汰动作仍在持 L1 内兜底更新 SQLite 终局标志
            if (!handledInMemory && action.IsEliminated && _db != null && Guid.TryParse(action.AccountId, out var accGuid))
            {
                try
                {
                    var row = _db.LoadLatestTournamentRun(accGuid, action.TournamentSessionId);
                    if (row != null && !row.IsEnded)
                    {
                        _db.MarkRunEnded(row.RunId, ERunState.EndRunDefeat, "afk_eliminated");
                    }
                }
                catch { }
            }
        }
        finally
        {
            accountLock.Release();
        }
    }

    /// <summary>归档并清理过期或全员人类完赛超 30 分钟的房间，根治内存泄漏（MP-P0-02）。</summary>
    public int EvictExpiredAndFinishedLobbies(TournamentPvpCoordinator? pvpCoordinator = null)
    {
        DateTime now = UtcNow;
        int evicted = 0;

        foreach (var lobby in _byId.Values.ToArray())
        {
            bool shouldEvict = false;
            lock (lobby.Members)
            {
                bool allHumansDone = lobby.HasStarted && lobby.Members.Where(m => !m.IsAi).All(m => m.HasFinishedRun);
                if (allHumansDone && !lobby.EndedAt.HasValue)
                    lobby.EndedAt = now;

                if (lobby.IsCancelled || lobby.ExpiresAt <= now)
                {
                    shouldEvict = true;
                    if (!lobby.HasStarted) lobby.IsCancelled = true;
                    lobby.EndedAt ??= now;
                }
                else if (allHumansDone && lobby.EndedAt.HasValue && (now - lobby.EndedAt.Value) >= TimeSpan.FromMinutes(30))
                {
                    shouldEvict = true;
                }
            }

            if (!shouldEvict) continue;

            PersistLobby(lobby);
            if (_byId.TryRemove(lobby.LobbyId, out _))
            {
                _codeToId.TryRemove(lobby.Code, out _);
                lock (lobby.Members)
                {
                    foreach (var m in lobby.Members)
                    {
                        if (!m.IsAi)
                            _memberToLobby.TryRemove(new KeyValuePair<string, string>(m.AccountId, lobby.LobbyId));
                    }
                }
                if (lobby.TournamentSessionId.HasValue)
                    pvpCoordinator?.PurgeForSession(lobby.TournamentSessionId.Value);
                evicted++;
            }
        }

        if (evicted > 0)
            NotifyLobbyChanged(null, publicListChanged: true);
        return evicted;
    }

    private void PublishBarrierEvent(string lobbyId, uint day, PvpBarrierDecision decision, List<string> waitingFor)
    {
        if (_sseHub == null) return;
        string json = JsonSerializer.Serialize(new
        {
            lobbyId,
            day,
            survivingOnline = decision.SurvivingHumans,
            readyHumans = decision.ReadyHumans,
            waitingFor,
            isAccelerated = decision.IsAccelerated,
            remainingSeconds = decision.RemainingSeconds,
            timedOutUnlocked = decision.TimedOutUnlocked,
            deadlineAt = decision.DeadlineAt,
        });
        _sseHub.PublishSocialEvent(lobbyId, "barrier", json);
    }

    private void PublishTakeoverEvent(string lobbyId, string accountId, string username, string state, string reason)
    {
        if (_sseHub == null) return;
        string json = JsonSerializer.Serialize(new
        {
            lobbyId,
            accountId,
            username,
            state,
            reason,
            updatedAt = UtcNow,
        });
        _sseHub.PublishSocialEvent(lobbyId, "takeover", json);
    }

    public static string NormalizeAccountKey(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        return Guid.TryParse(accountId, out var g) ? g.ToString("N") : accountId.Trim();
    }

    public IEnumerable<Lobby> PublicLobbies(bool includeStartedSpectatable = false)
        => _byId.Values.Where(l =>
            l.IsPublic &&
            !l.IsCancelled &&
            l.ExpiresAt > UtcNow &&
            (!l.HasStarted || includeStartedSpectatable));

    public IEnumerable<Lobby> All => _byId.Values;

    public int ActiveLobbyCount => _byId.Values.Count(l => !l.IsCancelled && l.ExpiresAt > UtcNow);

    private void CleanupPreviousMembership(string accountId)
    {
        if (_memberToLobby.TryGetValue(accountId, out var oldId))
        {
            var old = Get(oldId);
            if (old != null)
            {
                bool shouldLeave;
                lock (old.Members)
                {
                    var mem = old.Members.Find(m => m.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
                    shouldLeave = mem == null || mem.HasFinishedRun || !old.HasStarted || old.Members.Count(m => !m.IsAi) <= 1;
                }
                if (shouldLeave)
                {
                    Leave(accountId);
                    return;
                }
            }
            _memberToLobby.TryRemove(accountId, out _);
        }
    }

    private static void ApplyNormalizedRulesLocked(
        Lobby lobby,
        CustomRunRuleSet? incomingRules,
        int? playerCapOverride,
        int? roundCountdownSeconds,
        int? acceleratedRoundCountdownSeconds,
        IEnumerable<string>? bannedEncounterIds,
        bool allowUpdatePlayerCap,
        string? modeIdOverride = null,
        string? presetIdOverride = null)
    {
        var normalized = (incomingRules ?? lobby.Rules ?? new CustomRunRuleSet()).CloneNormalized();

        if (!string.IsNullOrWhiteSpace(presetIdOverride) &&
            Enum.TryParse<EGameModePreset>(presetIdOverride.Trim(), ignoreCase: true, out var parsedPreset))
        {
            normalized.PresetId = parsedPreset;
        }
        if (!string.IsNullOrWhiteSpace(modeIdOverride))
        {
            normalized.SpecialModeId = ParseSpecialModeId(modeIdOverride.Trim(), normalized.SpecialModeId);
        }

        if (allowUpdatePlayerCap)
        {
            int desiredCap = Math.Clamp(playerCapOverride ?? normalized.PlayerCap, 2, 16);
            while (lobby.Members.Count > desiredCap)
            {
                int lastAi = lobby.Members.FindLastIndex(m => m.IsAi);
                if (lastAi < 0) break;
                lobby.Members.RemoveAt(lastAi);
            }
            lobby.PlayerCap = Math.Max(desiredCap, Math.Clamp(lobby.Members.Count, 2, 16));
        }
        normalized.PlayerCap = lobby.PlayerCap;

        if (bannedEncounterIds != null)
        {
            var merged = new List<string>(normalized.BannedEncounterIds);
            merged.AddRange(bannedEncounterIds);
            normalized.BannedEncounterIds = merged;
            normalized = normalized.CloneNormalized();
        }
        lobby.BannedEncounterIds = new List<string>(normalized.BannedEncounterIds);

        if (roundCountdownSeconds.HasValue)
            lobby.RoundCountdownSeconds = Math.Clamp(roundCountdownSeconds.Value, 0, 3600);
        if (acceleratedRoundCountdownSeconds.HasValue)
            lobby.AcceleratedRoundCountdownSeconds = Math.Clamp(acceleratedRoundCountdownSeconds.Value, 0, 3600);

        normalized.SelectedArchetypeMask = 0UL;
        lobby.Rules = normalized;
        lobby.ModeId = Persistence.ResolveModeKey(normalized.SpecialModeId);
        lobby.PresetId = normalized.PresetId.ToString();
    }

    public static ESpecialModeId ParseSpecialModeId(string raw, ESpecialModeId fallback = ESpecialModeId.Standard)
        => raw.Trim().ToLowerInvariant() switch
        {
            "standard" => ESpecialModeId.Standard,
            "endless" or "endless_tower" or "endlesstower" => ESpecialModeId.EndlessTower,
            "cross_hero" or "crossheromayhem" => ESpecialModeId.CrossHeroMayhem,
            "mutator" or "mutator_challenge" or "mutatorchallenge" => ESpecialModeId.MutatorChallenge,
            "daily" or "daily_challenge" or "dailychallenge" => ESpecialModeId.DailyChallenge,
            "boss_rush" or "bossrush" => ESpecialModeId.BossRush,
            "sandbox" or "sandbox_lab" or "sandboxlab" => ESpecialModeId.SandboxLab,
            _ => Enum.TryParse<ESpecialModeId>(raw, ignoreCase: true, out var parsed) ? parsed : fallback,
        };

    private static void EnsureDayMatchups(Lobby lobby, uint day, IEnumerable<Member> members)
    {
        var participants = members
            .Where(m => !m.FinalIsEliminated)
            .Select(m => new MatchupParticipant(m.AccountId, m.Username, m.IsAi, m.FinalIsEliminated))
            .ToList();
        lobby.Matchmaker.AssignRoundMatchups(day, participants, lobby.Rules);
    }

    private static void FillAiCompetitors(Lobby lobby, int targetCount, DateTime now)
    {
        while (lobby.Members.Count < targetCount)
        {
            if (!TryAddNextAiMember(lobby, now)) break;
        }
    }

    private static bool TryAddNextAiMember(Lobby lobby, DateTime now)
    {
        for (int idx = 0; idx < 64; idx++)
        {
            var candidate = CreateAiMember(lobby, idx, now);
            if (!lobby.Members.Exists(m =>
                    m.AccountId.Equals(candidate.AccountId, StringComparison.OrdinalIgnoreCase) ||
                    m.Username.Equals(candidate.Username, StringComparison.OrdinalIgnoreCase)))
            {
                lobby.Members.Add(candidate);
                return true;
            }
        }
        return false;
    }

    private static Member CreateAiMember(Lobby lobby, int idx, DateTime now)
    {
        string baseName = AiBotNames[idx % AiBotNames.Length];
        string username = idx < AiBotNames.Length ? baseName : $"{baseName}-{idx + 1}";
        string aiId = DeterministicGuidHex($"bazaar-ai:{lobby.LobbyId}:{idx}");
        return new Member
        {
            AccountId = aiId,
            Username = username,
            Hero = HeroCycle[(idx + 1) % HeroCycle.Length],
            IsAi = true,
            JoinedAt = now,
            LastSeenAt = now,
            FinalPrestige = Math.Max(1, lobby.Rules?.StartingPrestige ?? 20),
        };
    }

    private static string DeterministicGuidHex(string seed)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash).ToString("N");
    }

    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = RandomNumberGenerator.GetBytes(6);
        var sb = new StringBuilder(6);
        foreach (byte b in rng) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}

public static class TournamentEndpoints
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };
    private static BazaarServer.Mods.CommunityModStore? _communityModStore;

    public static void Map(WebApplication app, LobbyStore lobbies)
    {
        _communityModStore = app.Services.GetService<BazaarServer.Mods.CommunityModStore>();
        // ── 建房（支持密码房、仅限邀请、预设档位、新模式与突变词条）───────────
        app.MapPost("/lobbies", async (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            var acc = auth.Account!;

            string raw = await ReadBoundedBodyAsync(ctx, 64 * 1024);

            string? name = null;
            string playMode = "Tournament";
            int? explicitCap = null;
            int? rulesPlayerCap = null;
            bool isPublic = true;
            bool isInviteOnly = false;
            string? password = null;
            string? modeId = null;
            string? presetId = null;
            int? roundCountdownSeconds = null;
            int? acceleratedRoundCountdownSeconds = null;
            List<string>? bannedEncounterIds = null;
            List<string>? mutatorIds = null;
            List<string>? selectedModIds = null;
            CustomRunRuleSet? parsedRules = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var r = doc.RootElement;
                if (TryGetStringCaseInsensitive(r, "name", out var n)) name = n;
                if (TryGetStringCaseInsensitive(r, "playMode", out var pm) && !string.IsNullOrWhiteSpace(pm)) playMode = pm!;
                if (TryGetIntCaseInsensitive(r, "playerCap", out int c)) explicitCap = c;
                if (TryGetBoolCaseInsensitive(r, "isPublic", out bool ip)) isPublic = ip;
                if (TryGetBoolCaseInsensitive(r, "isInviteOnly", out bool inv)) isInviteOnly = inv;
                if (TryGetStringCaseInsensitive(r, "password", out var pwd)) password = pwd;
                if (TryGetStringCaseInsensitive(r, "modeId", out var mid)) modeId = mid;
                if (TryGetStringCaseInsensitive(r, "presetId", out var pid)) presetId = pid;
                if (TryGetIntCaseInsensitive(r, "roundCountdownSeconds", out int rcs)) roundCountdownSeconds = rcs;
                if (TryGetIntCaseInsensitive(r, "acceleratedRoundCountdownSeconds", out int arcs)) acceleratedRoundCountdownSeconds = arcs;
                if (TryGetStringArrayCaseInsensitive(r, "bannedEncounterIds", out var banned)) bannedEncounterIds = banned;
                if (TryGetStringArrayCaseInsensitive(r, "mutatorIds", out var muts)) mutatorIds = muts;
                if (TryGetStringArrayCaseInsensitive(r, "selectedModIds", out var smods)) selectedModIds = smods;
                if (TryGetPropertyCaseInsensitive(r, "rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Object)
                {
                    parsedRules = JsonSerializer.Deserialize<CustomRunRuleSet>(rulesEl.GetRawText(), CaseInsensitiveJson);
                    if (TryGetIntCaseInsensitive(rulesEl, "playerCap", out int rc))
                        rulesPlayerCap = rc;
                }
            }
            catch
            {
                return Results.Json(new { error = "建房请求体 JSON 格式不正确。", code = "INVALID_BODY" }, Json, statusCode: 400);
            }

            if (!string.IsNullOrEmpty(password) && (password.Trim().Length < 4 || password.Trim().Length > 32))
                return Results.Json(new { error = "房间密码长度必须在 4 到 32 位之间。", code = "INVALID_PASSWORD_LENGTH" }, Json, statusCode: 400);

            if (parsedRules == null && (!string.IsNullOrWhiteSpace(presetId) || !string.IsNullOrWhiteSpace(modeId) || mutatorIds is { Count: > 0 } || selectedModIds is { Count: > 0 }))
            {
                var pEnum = !string.IsNullOrWhiteSpace(presetId) && Enum.TryParse<EGameModePreset>(presetId.Trim(), true, out var pe)
                    ? pe : EGameModePreset.PureOfficial;
                var mEnum = !string.IsNullOrWhiteSpace(modeId) ? LobbyStore.ParseSpecialModeId(modeId) : ESpecialModeId.Standard;
                parsedRules = PresetCatalog.Create(pEnum, mEnum);
            }
            if (parsedRules != null && mutatorIds is { Count: > 0 })
            {
                parsedRules.MutatorIds = MutatorCatalog.NormalizeAndResolveConflicts(mutatorIds);
            }
            if (parsedRules != null && selectedModIds is { Count: > 0 })
            {
                parsedRules.SelectedModIds = selectedModIds;
            }

            // 修复 16 人爆房：未携带房主自定义 rules.PlayerCap 时，无视原版客户端硬编码发来的 playerCap=16，统一默认 8 人
            int effectiveCap = rulesPlayerCap
                ?? (parsedRules != null
                    ? (explicitCap.HasValue && explicitCap.Value != 16 ? explicitCap.Value : parsedRules.PlayerCap)
                    : 8);

            var lobby = lobbies.Create(
                acc,
                name,
                playMode,
                effectiveCap,
                isPublic,
                parsedRules,
                roundCountdownSeconds,
                acceleratedRoundCountdownSeconds,
                bannedEncounterIds,
                password,
                isInviteOnly,
                modeId,
                presetId);
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 房主修改房间规则（非房主返回 409 中文提示，防原版误踢回主菜单）────
        app.MapPut("/lobbies/{id}/rules", async (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string aid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "仅房主可以修改房间规则。", code = "NOT_HOST" }, Json, statusCode: 409);
            if (lobby.HasStarted)
                return Results.Json(new { error = "对局已开始，无法再修改房间规则。", code = "LOBBY_ALREADY_STARTED" }, Json, statusCode: 409);

            string raw = await ReadBoundedBodyAsync(ctx, 64 * 1024);
            CustomRunRuleSet? incoming = null;
            int? roundCountdownSeconds = null;
            int? acceleratedRoundCountdownSeconds = null;
            List<string>? bannedEncounterIds = null;
            List<string>? topSelectedModIds = null;
            string? modeId = null;
            string? presetId = null;

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var r = doc.RootElement;
                if (TryGetIntCaseInsensitive(r, "roundCountdownSeconds", out int rcs)) roundCountdownSeconds = rcs;
                if (TryGetIntCaseInsensitive(r, "acceleratedRoundCountdownSeconds", out int arcs)) acceleratedRoundCountdownSeconds = arcs;
                if (TryGetStringArrayCaseInsensitive(r, "bannedEncounterIds", out var banned)) bannedEncounterIds = banned;
                if (TryGetStringArrayCaseInsensitive(r, "selectedModIds", out var smods)) topSelectedModIds = smods;
                if (TryGetStringCaseInsensitive(r, "modeId", out var mid)) modeId = mid;
                if (TryGetStringCaseInsensitive(r, "presetId", out var pid)) presetId = pid;

                string rulesJson = TryGetPropertyCaseInsensitive(r, "rules", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
                    ? wrapped.GetRawText()
                    : r.GetRawText();
                incoming = JsonSerializer.Deserialize<CustomRunRuleSet>(rulesJson, CaseInsensitiveJson);
                if (incoming != null && topSelectedModIds != null && (incoming.SelectedModIds == null || incoming.SelectedModIds.Count == 0))
                    incoming.SelectedModIds = topSelectedModIds;
            }
            catch
            {
                return Results.Json(new { error = "规则配置 JSON 格式不正确。", code = "INVALID_RULES" }, Json, statusCode: 400);
            }

            lobbies.UpdateRules(
                lobby,
                incoming ?? new CustomRunRuleSet(),
                roundCountdownSeconds,
                acceleratedRoundCountdownSeconds,
                bannedEncounterIds,
                modeId,
                presetId);
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 房主在开赛前管理 AI 陪练（非房主返回 409 中文提示）────────────────
        app.MapPost("/lobbies/{id}/bots", async (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string aid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "仅房主可以管理房间内的 AI 选手。", code = "NOT_HOST" }, Json, statusCode: 409);

            string raw = await ReadBoundedBodyAsync(ctx, 8 * 1024);
            string? action = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                TryGetStringCaseInsensitive(doc.RootElement, "action", out action);
            }
            catch { }

            var (ok, error) = lobbies.ManageBots(lobby, action ?? string.Empty);
            if (!ok)
            {
                int code = error != null && error.Contains("已开赛") ? 409 : 400;
                return Results.Json(new { error = error ?? "AI 操作失败。", code = code == 409 ? "LOBBY_ALREADY_STARTED" : "INVALID_ACTION" }, Json, statusCode: code);
            }
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 房主生成邀请码与移交房主 ─────────────────────────────────────────
        app.MapPost("/lobbies/{id}/invite", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(auth.Account!.AccountId.ToString("N"), StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "仅房主可以生成房间邀请链接。", code = "NOT_HOST" }, Json, statusCode: 409);

            var (token, expiresAt) = lobbies.GenerateInviteToken(lobby);
            return Results.Json(new
            {
                lobbyId = lobby.LobbyId,
                code = lobby.Code,
                inviteToken = token,
                joinUrl = $"bazaar://join/{lobby.Code}?t={token}",
                expiresAt,
            }, Json);
        });

        app.MapPost("/lobbies/{id}/transfer-host", async (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);

            string raw = await ReadBoundedBodyAsync(ctx, 4 * 1024);
            string? targetId = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                TryGetStringCaseInsensitive(doc.RootElement, "targetAccountId", out targetId);
            }
            catch { }

            if (string.IsNullOrWhiteSpace(targetId))
                return Results.Json(new { error = "请指定要移交房主的目标成员账号 ID。", code = "INVALID_TARGET" }, Json, statusCode: 400);

            var res = lobbies.TransferHost(lobby, auth.Account!.AccountId.ToString("N"), targetId);
            if (!res.Ok)
                return Results.Json(new { error = res.MessageZh, code = res.ErrorCode }, Json, statusCode: res.StatusCode);

            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 公开房列表与多维度筛选（100% 兼容原版 GET /lobbies?limit=50）──────
        app.MapGet("/lobbies", (HttpContext ctx) =>
        {
            int limit = 50;
            if (int.TryParse(ctx.Request.Query["limit"], out int l)) limit = Math.Clamp(l, 1, 200);
            string q = ctx.Request.Query["q"].ToString().Trim();
            string modeId = ctx.Request.Query["modeId"].ToString().Trim();
            string presetId = ctx.Request.Query["presetId"].ToString().Trim();
            string hasPwdFilter = ctx.Request.Query["hasPassword"].ToString().Trim().ToLowerInvariant();
            bool hideFull = string.Equals(ctx.Request.Query["hideFull"], "true", StringComparison.OrdinalIgnoreCase);
            bool includeStarted = string.Equals(ctx.Request.Query["includeStartedSpectatable"], "true", StringComparison.OrdinalIgnoreCase);
            string sort = ctx.Request.Query["sort"].ToString().Trim().ToLowerInvariant();

            var candidates = lobbies.PublicLobbies(includeStarted).Select(l => SnapshotPublicRow(l, lobbies.SseHub));

            if (!string.IsNullOrEmpty(q))
            {
                candidates = candidates.Where(x =>
                    x.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.HostUsername.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.Code.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrEmpty(modeId) && !modeId.Equals("all", StringComparison.OrdinalIgnoreCase))
                candidates = candidates.Where(x => x.ModeId.Equals(modeId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(presetId) && !presetId.Equals("all", StringComparison.OrdinalIgnoreCase))
                candidates = candidates.Where(x => x.PresetId.Equals(presetId, StringComparison.OrdinalIgnoreCase));
            if (hasPwdFilter == "true")
                candidates = candidates.Where(x => x.HasPassword);
            else if (hasPwdFilter == "false")
                candidates = candidates.Where(x => !x.HasPassword);
            if (hideFull)
                candidates = candidates.Where(x => x.HumanCount < x.PlayerCap);

            candidates = sort switch
            {
                "players_desc" => candidates.OrderByDescending(x => x.JoinedCount).ThenByDescending(x => x.CreatedAt),
                "human_desc" => candidates.OrderByDescending(x => x.HumanCount).ThenByDescending(x => x.CreatedAt),
                _ => candidates.OrderByDescending(x => x.CreatedAt),
            };

            return Results.Json(candidates.Take(limit).ToArray(), Json);
        });

        app.MapGet("/lobbies/{id}", (string id) =>
        {
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            return lobby == null || lobby.IsCancelled
                ? Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404)
                : Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 按码加入（修复 MP-P0-08：校验已开赛/密码/邀请码/满员，绝不返回 200 假成功）──
        app.MapPost("/lobbies/{code}/members", async (string code, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string? password = null;
            string? inviteToken = ctx.Request.Query["t"].ToString();
            string raw = await ReadBoundedBodyAsync(ctx, 4 * 1024);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (TryGetStringCaseInsensitive(doc.RootElement, "password", out var p)) password = p;
                    if (TryGetStringCaseInsensitive(doc.RootElement, "inviteToken", out var t) && !string.IsNullOrWhiteSpace(t)) inviteToken = t;
                }
                catch { }
            }

            var res = lobbies.JoinWithResult(code, auth.Account!, password, inviteToken);
            if (!res.Ok || res.Lobby == null)
            {
                int status = res.ErrorCode == "LOBBY_NOT_FOUND" ? 404 : 409;
                return Results.Json(new { error = res.MessageZh ?? "加入房间失败。", code = res.ErrorCode ?? "JOIN_FAILED" }, Json, statusCode: status);
            }

            return Results.Json(new { LobbyId = res.Lobby.LobbyId, Code = res.Lobby.Code }, Json);
        });

        // ── 我所在的房（严格对齐 TournamentHttpClient.CoordinatorMembership）──
        app.MapGet("/membership", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            if (auth.HasExplicitFailure) return AccountAuthResolver.ErrorResult(auth, Json);
            if (auth.Account == null)
                return Results.Json(new { State = "none", LobbyId = (string?)null, SessionId = (string?)null, Removal = (object?)null }, Json);

            string aid = auth.Account.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(aid);
            var member = lobby != null ? lobbies.FindMember(lobby, aid) : null;

            if (lobby == null || lobby.IsCancelled || member == null || member.HasFinishedRun)
            {
                var removal = lobbies.PopRemovalNotice(aid);
                return Results.Json(new
                {
                    State = "none",
                    LobbyId = (string?)null,
                    SessionId = (string?)null,
                    Removal = removal == null ? null : new
                    {
                        LobbyId = removal.LobbyId,
                        Code = removal.Code,
                        Reason = removal.Reason,
                    },
                }, Json);
            }

            return Results.Json(new
            {
                State = lobby.HasStarted ? "session" : "lobby",
                LobbyId = lobby.LobbyId,
                SessionId = lobby.TournamentSessionId?.ToString("D"),
                Removal = (object?)null,
            }, Json);
        });

        // 退房
        app.MapDelete("/membership", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Leave(auth.Account!.AccountId.ToString("N"));
            return Results.NoContent();
        });
        app.MapDelete("/lobbies/{id}/membership", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Leave(auth.Account!.AccountId.ToString("N"));
            return Results.NoContent();
        });

        // 踢人（根治 MP-P0-01：严禁匿名踢人，校验房主身份与业务边界，非房主返回 409 中文提示）
        app.MapDelete("/members/{accountId}", (string accountId, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string callerAid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(callerAid);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "您当前不在任何锦标赛房间中。", code = "NOT_IN_LOBBY" }, Json, statusCode: 404);

            var res = lobbies.KickMember(lobby, callerAid, accountId);
            if (!res.Ok)
                return Results.Json(new { error = res.MessageZh, code = res.ErrorCode }, Json, statusCode: res.StatusCode);

            return Results.Json(new { ok = true, message = res.MessageZh }, Json);
        });
        app.MapDelete("/lobbies/{id}/members/{accountId}", (string id, string accountId, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);

            var res = lobbies.KickMember(lobby, auth.Account!.AccountId.ToString("N"), accountId);
            if (!res.Ok)
                return Results.Json(new { error = res.MessageZh, code = res.ErrorCode }, Json, statusCode: res.StatusCode);

            return Results.Json(new { ok = true, message = res.MessageZh }, Json);
        });

        // ── 心跳 / 倒计时开局（根治 MP-P0-01：仅房主可启动倒计时，非房主返 409 中文提示）──
        app.MapPut("/lobbies/{id}/presence", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Touch(auth.Account!.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPut("/presence", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Touch(auth.Account!.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPut("/countdown", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string callerAid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(callerAid);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "您当前不在任何锦标赛房间中。", code = "NOT_IN_LOBBY" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(callerAid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "仅房主可以启动开赛倒计时。", code = "NOT_HOST" }, Json, statusCode: 409);
            if (lobby.HasStarted)
                return Results.Json(new { error = "房间已经开始，无需重复开启倒计时。", code = "LOBBY_ALREADY_STARTED" }, Json, statusCode: 409);

            lobbies.StartCountdown(lobby.LobbyId, Guid.NewGuid());
            return Results.Json(new { ok = true, tournamentSessionId = lobby.TournamentSessionId }, Json);
        });
        app.MapPut("/lobbies/{id}/countdown", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string callerAid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id) ?? lobbies.GetByMember(callerAid);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(callerAid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "仅房主可以启动开赛倒计时。", code = "NOT_HOST" }, Json, statusCode: 409);
            if (lobby.HasStarted)
                return Results.Json(new { error = "房间已经开始，无需重复开启倒计时。", code = "LOBBY_ALREADY_STARTED" }, Json, statusCode: 409);

            lobbies.StartCountdown(lobby.LobbyId, Guid.NewGuid());
            return Results.Json(new { ok = true, tournamentSessionId = lobby.TournamentSessionId }, Json);
        });

        // ── 匹配队列（加锁读取 lobby.Members 修复 MP-P1-01）──────────────────
        app.MapGet("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            if (auth.HasExplicitFailure) return AccountAuthResolver.ErrorResult(auth, Json);
            if (auth.Account == null)
                return Results.Json(new { State = "idle", QueuedCount = 0, MaxPlayers = 8 }, Json);

            string aid = auth.Account.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(aid);
            if (lobby != null && lobby.Kind == "matchmaking" && !lobby.IsCancelled)
            {
                lock (lobby.Members)
                {
                    var mem = lobby.Members.Find(m => m.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase));
                    if (mem != null && !mem.HasFinishedRun)
                    {
                        return Results.Json(new
                        {
                            State = lobby.HasStarted ? "lobby" : "queued",
                            QueuedCount = lobby.Members.Count,
                            MaxPlayers = lobby.PlayerCap,
                        }, Json);
                    }
                }
            }
            return Results.Json(new { State = "idle", QueuedCount = 0, MaxPlayers = 8 }, Json);
        });
        app.MapPut("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            var lobby = lobbies.EnqueueMatchmaking(auth.Account!);
            int count;
            lock (lobby.Members) count = lobby.Members.Count;
            return Results.Json(new
            {
                State = "lobby",
                QueuedCount = count,
                MaxPlayers = lobby.PlayerCap,
                TournamentSessionId = lobby.TournamentSessionId,
            }, Json);
        });
        app.MapDelete("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);

            string aid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(aid);
            if (lobby != null && lobby.Kind == "matchmaking")
            {
                lobbies.Leave(aid);
            }
            return Results.Json(new { ok = true }, Json);
        });

        // ── 接受 / 拒绝分配房 ───────────────────────────────────────────────
        app.MapPut("/acceptance", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Touch(auth.Account!.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPut("/lobbies/{id}/acceptance", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Touch(auth.Account!.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapDelete("/acceptance", (HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Leave(auth.Account!.AccountId.ToString("N"));
            return Results.NoContent();
        });
        app.MapDelete("/lobbies/{id}/acceptance", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            lobbies.Leave(auth.Account!.AccountId.ToString("N"));
            return Results.NoContent();
        });

        // ── 锦标赛状态 ───────────────────────────────────────────────────────
        app.MapGet("/sessions/current/status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            return Results.Json(StatusFor(auth.Account!, lobbies, sessions), Json);
        });
        app.MapGet("/sessions/current/gameplay-status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            return Results.Json(StatusFor(auth.Account!, lobbies, sessions), Json);
        });
        app.MapGet("/tournament/status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, Json);
            return Results.Json(StatusFor(auth.Account!, lobbies, sessions), Json);
        });

        // ── 观战名单与详情（修复 MP-P0-06：GET /spectate 必须包含完整 Players[] 名单）──
        app.MapGet("/spectate", (SessionStore sessions) =>
        {
            var live = lobbies.All
                .Where(l => l.HasStarted && !l.IsCancelled)
                .Select(l => BuildSpectateSessionDto(l, lobbies, sessions))
                .ToArray();
            return Results.Json(live, Json);
        });
        app.MapGet("/spectate/{code}", (string code, SessionStore sessions) =>
        {
            var lobby = lobbies.GetByCode(code) ?? lobbies.Get(code);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "未找到对应的可观战锦标赛房间。", code = "SPECTATE_NOT_FOUND" }, Json, statusCode: 404);

            return Results.Json(BuildSpectateSessionDto(lobby, lobbies, sessions), Json);
        });

        // ── 房间级隔离 SSE 事件流（根治全局风暴 MP-P1-02，含断连释放与保活）──
        app.MapGet("/events", async (HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            string? lobbyId = ctx.Request.Query["lobbyId"].ToString();
            string ownerKey = auth.Account != null
                ? auth.Account.AccountId.ToString("N")
                : $"anon:{ctx.Connection.RemoteIpAddress}";
            if (string.IsNullOrWhiteSpace(lobbyId) && auth.Account != null)
            {
                lobbyId = lobbies.GetByMember(auth.Account.AccountId.ToString("N"))?.LobbyId;
            }
            await StreamLobbySseAsync(ctx, lobbies, lobbyId, spectateLobbyId: null, ownerKey, ct);
        });
        app.MapGet("/spectate/{code}/events", async (string code, HttpContext ctx, AccountStore accounts, CancellationToken ct) =>
        {
            var lobby = lobbies.GetByCode(code) ?? lobbies.Get(code);
            if (lobby == null || lobby.IsCancelled)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "未找到观战房间。", code = "SPECTATE_NOT_FOUND" }, ct);
                return;
            }
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            string ownerKey = auth.Account != null
                ? auth.Account.AccountId.ToString("N")
                : $"anon:{ctx.Connection.RemoteIpAddress}";
            await StreamLobbySseAsync(ctx, lobbies, lobby.LobbyId, spectateLobbyId: lobby.LobbyId, ownerKey, ct);
        });
    }

    private static async Task StreamLobbySseAsync(
        HttpContext ctx,
        LobbyStore lobbies,
        string? lobbyId,
        string? spectateLobbyId,
        string ownerKey,
        CancellationToken ct)
    {
        var hub = lobbies.SseHub;
        if (hub != null)
        {
            string topic = !string.IsNullOrWhiteSpace(lobbyId)
                ? SseEventHub.LobbyTopic(lobbyId)
                : SseEventHub.PublicLobbiesTopic;
            using var sub = hub.TrySubscribe(topic, ownerKey, spectateLobbyId);
            if (sub == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "当前实时事件连接数已达上限，请关闭多余窗口后重试。", code = "SSE_LIMIT_EXCEEDED" }, ct);
                return;
            }

            SseEventHub.PrepareSseResponseHeaders(ctx.Response);
            // 首帧发送注释行 : connected，兼容原版 ReadServerSentEvents(notifyOnConnect:true) 避免双拉取风暴
            if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": connected\n\n", ct))
                return;

            while (!ct.IsCancellationRequested)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delayCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    var frame = await sub.Reader.ReadAsync(delayCts.Token).ConfigureAwait(false);
                    if (!await SseEventHub.TryWriteFrameAsync(ctx.Response, frame, ct))
                        break;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": keepalive\n\n", ct))
                        break;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or System.Threading.Channels.ChannelClosedException)
                {
                    break;
                }
            }
            return;
        }

        // 回退路径（未注册 SseEventHub 时仍使用房间级版本隔离 + 优雅断连保护）
        SseEventHub.PrepareSseResponseHeaders(ctx.Response);
        long lastVer = !string.IsNullOrWhiteSpace(lobbyId)
            ? lobbies.GetLobbyChangeVersion(lobbyId)
            : lobbies.ChangeVersion;

        if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": connected\n\n", ct))
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool changed = !string.IsNullOrWhiteSpace(lobbyId)
                    ? await lobbies.WaitForLobbyChangeAsync(lobbyId, lastVer, TimeSpan.FromSeconds(20), ct)
                    : await lobbies.WaitForChangeAsync(lastVer, TimeSpan.FromSeconds(20), ct);
                if (ct.IsCancellationRequested) break;

                long curVer = !string.IsNullOrWhiteSpace(lobbyId)
                    ? lobbies.GetLobbyChangeVersion(lobbyId)
                    : lobbies.ChangeVersion;
                if (changed || curVer != lastVer)
                {
                    lastVer = curVer;
                    if (!await SseEventHub.TryWriteRawAsync(ctx.Response, "data: update\n\n", ct))
                        break;
                }
                else
                {
                    if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": keepalive\n\n", ct))
                        break;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    // ── DTO 构造与实时状态快照 ──────────────────────────────────────────────

    private static object BuildSpectateSessionDto(LobbyStore.Lobby lobby, LobbyStore lobbies, SessionStore sessions)
    {
        var players = BuildPlayersSnapshot(lobby, null, null, 1, sessions, lobbies);
        uint curDay = players.Length > 0 ? players.Max(p => p.CurrentDay) : 1u;
        int activeCount = players.Count(p => !p.IsEliminated);
        var barrier = lobby.HasStarted
            ? lobbies.EvaluateRoundBarrier(lobby, curDay, null, null, sessions, startOrAdvanceCountdown: false)
            : new LobbyStore.PvpBarrierDecision(true, false, null, activeCount, activeCount, 0, false, false, 0, null);

        int aiAlive;
        lock (lobby.Members)
            aiAlive = lobby.Members.Count(m => m.IsAi && !m.FinalIsEliminated);

        int readyPlayers = barrier.SurvivingHumans >= 2
            ? Math.Max(0, barrier.ReadyHumans + aiAlive)
            : activeCount;

        return new
        {
            WatchCode = lobby.Code,
            TournamentSessionId = lobby.TournamentSessionId ?? Guid.Empty,
            Name = lobby.Name,
            PlayMode = lobby.PlayMode,
            ModeId = lobby.ModeId,
            PresetId = lobby.PresetId,
            Source = lobby.Kind,
            Phase = activeCount <= 1 && lobby.HasStarted ? "RunEnded" : (lobby.HasStarted ? "DayFlow" : "WaitingRoom"),
            CurrentDay = curDay,
            TotalPlayers = players.Length,
            ActivePlayers = activeCount,
            ReadyPlayers = readyPlayers,
            DeadlineAt = barrier.SurvivingHumans >= 2 ? barrier.DeadlineAt : null,
            StartedAt = lobby.StartedAt ?? lobby.CreatedAt,
            SpectatorCount = lobbies.SseHub?.GetSpectatorCount(lobby.LobbyId) ?? 0,
            Players = players,
        };
    }

    private sealed record PublicLobbyRowDto(
        string LobbyId,
        string Code,
        string Name,
        string HostAccountId,
        string HostUsername,
        int JoinedCount,
        int PlayerCap,
        int MaxPlayers,
        DateTime CreatedAt,
        DateTime ExpiresAt,
        string State,
        bool HasPassword,
        bool IsInviteOnly,
        string ModeId,
        string PresetId,
        string[] MutatorIds,
        bool IsCustomPowerModified,
        int HumanCount,
        int BotCount,
        int RoundCountdownSeconds,
        bool Spectatable,
        int SpectatorCount);

    private static PublicLobbyRowDto SnapshotPublicRow(LobbyStore.Lobby lobby, SseEventHub? sseHub)
    {
        int joinedCount, humanCount, botCount, playerCap, roundCd;
        string[] mutators;
        bool isCustomPower;
        string state, modeId, presetId, hostAid, hostUser;
        bool hasPwd, isInv, spectatable;

        lock (lobby.Members)
        {
            joinedCount = lobby.Members.Count;
            humanCount = lobby.Members.Count(m => !m.IsAi);
            botCount = joinedCount - humanCount;
            playerCap = lobby.PlayerCap;
            roundCd = lobby.RoundCountdownSeconds;
            mutators = lobby.Rules?.MutatorIds?.ToArray() ?? Array.Empty<string>();
            isCustomPower = lobby.Rules?.IsCustomPowerModified() ?? false;
            state = lobby.IsCancelled ? "cancelled" : (lobby.HasStarted ? "started" : "waiting");
            modeId = lobby.ModeId;
            presetId = lobby.PresetId;
            hostAid = lobby.HostAccountId;
            hostUser = lobby.HostUsername;
            hasPwd = lobby.HasPassword;
            isInv = lobby.IsInviteOnly;
            spectatable = lobby.HasStarted && !lobby.IsCancelled;
        }

        return new PublicLobbyRowDto(
            LobbyId: lobby.LobbyId,
            Code: lobby.Code,
            Name: lobby.Name,
            HostAccountId: hostAid,
            HostUsername: hostUser,
            JoinedCount: joinedCount,
            PlayerCap: playerCap,
            MaxPlayers: playerCap,
            CreatedAt: lobby.CreatedAt,
            ExpiresAt: lobby.ExpiresAt,
            State: state,
            HasPassword: hasPwd,
            IsInviteOnly: isInv,
            ModeId: modeId,
            PresetId: presetId,
            MutatorIds: mutators,
            IsCustomPowerModified: isCustomPower,
            HumanCount: humanCount,
            BotCount: botCount,
            RoundCountdownSeconds: roundCd,
            Spectatable: spectatable,
            SpectatorCount: sseHub?.GetSpectatorCount(lobby.LobbyId) ?? 0);
    }

    private static object CoordinatorLobbyDto(LobbyStore.Lobby lobby)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now.AddHours(6);
        List<LobbyStore.Member> snapshot;
        CustomRunRuleSet rulesSnapshot;
        string[] bannedSnapshot;
        int playerCap, roundCountdown, accelCountdown;
        string hostAid, hostName, modeId, presetId;
        bool hasPwd, isInv;
        lock (lobby.Members)
        {
            snapshot = lobby.Members.ToList();
            rulesSnapshot = lobby.Rules;
            bannedSnapshot = lobby.BannedEncounterIds.ToArray();
            playerCap = lobby.PlayerCap;
            roundCountdown = lobby.RoundCountdownSeconds;
            accelCountdown = lobby.AcceleratedRoundCountdownSeconds;
            hostAid = lobby.HostAccountId;
            hostName = lobby.HostUsername;
            modeId = lobby.ModeId;
            presetId = lobby.PresetId;
            hasPwd = lobby.HasPassword;
            isInv = lobby.IsInviteOnly;
        }

        return new
        {
            LobbyId = lobby.LobbyId,
            SessionId = lobby.TournamentSessionId?.ToString("D"),
            Kind = lobby.Kind,
            State = lobby.IsCancelled ? "cancelled" : (lobby.HasStarted ? "started" : "waiting"),
            Code = lobby.Code,
            HostAccountId = hostAid,
            HostUsername = hostName,
            PlayerCap = playerCap,
            MaxPlayers = playerCap,
            Name = lobby.Name,
            ModeId = modeId,
            PresetId = presetId,
            HasPassword = hasPwd,
            IsInviteOnly = isInv,
            ExcludeHostFromGameplay = lobby.ExcludeHostFromGameplay,
            IsPublic = lobby.IsPublic,
            ExpiresAt = lobby.ExpiresAt,
            ServerTime = now,
            CanStart = !lobby.HasStarted && !lobby.IsCancelled && snapshot.Count >= 1,
            RoundCountdownSeconds = roundCountdown,
            AcceleratedRoundCountdownSeconds = accelCountdown,
            BannedEncounterIds = bannedSnapshot,
            SelectedModIds = (rulesSnapshot.SelectedModIds ?? new List<string>()).ToArray(),
            RequiredMods = _communityModStore?.DescribeRequiredMods(rulesSnapshot.SelectedModIds)
                ?? (rulesSnapshot.SelectedModIds ?? new List<string>())
                    .Select(id => new BazaarServer.Mods.RequiredModSummary(
                        id, id, "-", "-", string.Empty, 0, 0, 0, false,
                        $"/api/community/mods/{Uri.EscapeDataString(id)}/download"))
                    .ToList(),
            Rules = rulesSnapshot,
            Members = snapshot.Select(m => new
            {
                AccountId = m.AccountId,
                Username = m.Username,
                Hero = m.Hero,
                IsAi = m.IsAi,
                State = "accepted",
                ConnectionState = m.ConnectionState,
                AiTakeoverRounds = m.MissedPvpDays,
                PresenceExpiresAt = expires,
                AcceptedAt = (DateTime?)m.JoinedAt,
                LastHeartbeatAt = (DateTime?)m.LastSeenAt,
            }).ToArray(),
        };
    }

    /// <summary>D8-P1-02：跨线程零锁读取指定账号锦标赛对局的不可变公开摘要快照，严禁无锁直接读取他人 RunState。</summary>
    private static RunPublicSummarySnapshot? FindAccountPublicSummary(
        string accountIdHex,
        SessionStore sessions,
        Guid? expectedTournamentSessionId = null)
    {
        var mem = sessions.GetByAccount(accountIdHex);
        if (mem == null) return null;

        if (expectedTournamentSessionId.HasValue && expectedTournamentSessionId.Value != Guid.Empty)
        {
            if (!mem.TournamentSessionId.HasValue || mem.TournamentSessionId.Value != expectedTournamentSessionId.Value)
                return null;
        }
        else if (mem.PlayMode != EPlayMode.Tournament && !mem.TournamentSessionId.HasValue)
        {
            return null;
        }

        return SessionRegistry.Get(mem.Sid)?.Run.PublicSummary;
    }

    public static object StatusFor(AccountStore.Account acc, LobbyStore lobbies, SessionStore sessions)
    {
        string aid = acc.AccountId.ToString("N");
        var lobby = lobbies.GetByMember(aid);
        Guid? expectedTid = lobby?.TournamentSessionId;
        var runSnap = (lobby != null && (!lobby.HasStarted || !expectedTid.HasValue))
            ? null
            : FindAccountPublicSummary(aid, sessions, expectedTid);
        var selfMember = lobby != null ? lobbies.FindMember(lobby, aid) : null;

        bool started = lobby?.HasStarted ?? (runSnap != null);
        var activeMem = sessions.GetByAccount(aid);
        Guid sessionId = lobby?.TournamentSessionId
                         ?? (activeMem != null && (activeMem.PlayMode == EPlayMode.Tournament || activeMem.TournamentSessionId.HasValue)
                             ? activeMem.TournamentSessionId
                             : null)
                         ?? Guid.Empty;

        uint currentDay = runSnap?.Day ?? selfMember?.FinalDay ?? 1u;
        bool isChampion = false;
        bool isEliminated = false;
        bool isConceded = false;
        bool isRunEnded = false;

        // P0-42：当 selfMember 已标记权威终局（含 AFK 淘汰），一律终态优先，绝不因残留内存 run 误报未淘汰
        if (selfMember != null && selfMember.HasFinishedRun)
        {
            isChampion = selfMember.FinalIsChampion;
            isEliminated = selfMember.FinalIsEliminated;
            isConceded = selfMember.FinalIsConceded;
            isRunEnded = true;
        }
        else if (runSnap != null)
        {
            isChampion = runSnap.State == ERunState.EndRunVictory;
            isEliminated = runSnap.State == ERunState.EndRunDefeat;
            isRunEnded = runSnap.IsTerminal;
            if (isRunEnded && selfMember != null && !selfMember.HasFinishedRun && lobby != null)
            {
                lobbies.RecordRunProgressSnapshot(lobby, aid, runSnap);
            }
        }

        bool snapHasCompletedPvpForDay = runSnap != null && (
            runSnap.LastCompletedPvpDay >= runSnap.Day ||
            (runSnap.LastCompletedPvpDay == 0 &&
             runSnap.RulesSnapshot.SpecialModeId != ESpecialModeId.BossRush &&
             (runSnap.Victories + runSnap.Defeats) >= runSnap.Day));
        bool isPvpReady = !isRunEnded && runSnap != null &&
            (runSnap.State == ERunState.TournamentPvpChoice ||
             runSnap.State == ERunState.TournamentWaitingRoom ||
             (runSnap.State == ERunState.Choice && runSnap.Hour >= 6 && !snapHasCompletedPvpForDay));

        if (lobby != null && runSnap != null)
        {
            lobbies.RecordRunProgressSnapshot(lobby, aid, runSnap);
        }

        // D8-P1-02：StatusFor 处于无 Session.Gate 的只读 HTTP 路径，传 selfRun: null 令屏障评估同样经 sessions 读取不可变 PublicSummary
        var barrierDecision = (lobby != null && started && !isRunEnded)
            ? lobbies.EvaluateRoundBarrier(lobby, currentDay, aid, selfRun: null, sessions, startOrAdvanceCountdown: true)
            : new LobbyStore.PvpBarrierDecision(true, false, null, 1, isPvpReady ? 1 : 0, 0, false, false, 0, null);

        bool canStartPvp = isPvpReady && barrierDecision.CanStartPvp;
        bool waitingForOthers = isPvpReady && barrierDecision.WaitingForOtherPlayers;
        DateTime? deadlineAt = barrierDecision.SurvivingHumans >= 2 ? barrierDecision.DeadlineAt : null;

        string phase;
        if (!started)
            phase = "WaitingRoom";
        else if (isRunEnded)
            phase = "RunEnded";
        else if (isPvpReady)
            phase = "PvpReady";
        else if (runSnap == null)
            phase = "RunStarting";
        else
            phase = "DayFlow";

        var players = BuildPlayersSnapshot(lobby, acc, runSnap, currentDay, sessions, lobbies);
        var selfSnapshot = players.FirstOrDefault(p => p.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase));

        string assignedOppId = selfSnapshot?.AssignedOpponentAccountId ?? "";
        string assignedOppName = selfSnapshot?.AssignedOpponentUsername ?? "";
        if (string.IsNullOrWhiteSpace(assignedOppId))
        {
            var fallbackOpp = players
                .Where(p => !p.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.IsEliminated ? 1 : 0)
                .FirstOrDefault();
            assignedOppId = fallbackOpp?.AccountId ?? "";
            assignedOppName = fallbackOpp?.Username ?? "锦标赛挑战者";
        }

        int? place = isChampion
            ? 1
            : (isEliminated ? (selfSnapshot?.Place ?? Math.Max(2, players.Count(p => !p.IsEliminated) + 1)) : selfSnapshot?.Place);

        int activePlayers = Math.Max(1, players.Count(p => !p.IsEliminated));
        int aiAlive = 0;
        if (lobby != null)
        {
            lock (lobby.Members)
            {
                aiAlive = lobby.Members.Count(m => m.IsAi && !m.FinalIsEliminated);
            }
        }
        int readyPlayers = barrierDecision.SurvivingHumans >= 2
            ? Math.Max(0, barrierDecision.ReadyHumans + aiAlive)
            : activePlayers;

        return new
        {
            TournamentSessionId = sessionId,
            Phase = phase,
            CurrentDay = currentDay,
            TotalPlayers = Math.Max(1, players.Length),
            ActivePlayers = activePlayers,
            ReadyPlayers = readyPlayers,
            DeadlineAt = deadlineAt,
            PlayerReadyForPvp = isPvpReady,
            ForcedTransition = barrierDecision.TimedOutUnlocked,
            CombatSubmitted = runSnap != null && runSnap.State == ERunState.PVPCombat,
            CanStartPvp = canStartPvp,
            WaitingForOtherPlayers = waitingForOthers,
            IsEliminated = isEliminated,
            IsChampion = isChampion,
            Place = place,
            Concede = isConceded,
            EndRun = isRunEnded,
            AssignedOpponentAccountId = assignedOppId,
            AssignedOpponentUsername = string.IsNullOrWhiteSpace(assignedOppName) ? "锦标赛挑战者" : assignedOppName,
            WaitingForHumanDayStart = false,
            Players = players,
            ExitReason = isChampion ? "champion" : (isEliminated ? "eliminated" : ""),
        };
    }

    private sealed class ObservedPlayerRow
    {
        public string AccountId { get; set; } = "";
        public string Username { get; set; } = "";
        public string TitlePrefix { get; set; } = "";
        public string Hero { get; set; } = "Vanessa";
        public uint CurrentDay { get; set; } = 1;
        public uint CurrentHour { get; set; } = 1;
        public int Prestige { get; set; } = GameDefaults.StartingPrestige;
        public bool HasVisitedFates { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public string Phase { get; set; } = "DayFlow";
        public bool ReadyForPvp { get; set; }
        public bool ForcedTransition { get; set; }
        public bool CombatSubmitted { get; set; }
        public bool CanStartPvp { get; set; }
        public string AssignedOpponentAccountId { get; set; } = "";
        public string AssignedOpponentUsername { get; set; } = "";
        public bool IsEliminated { get; set; }
        public bool IsConceded { get; set; }
        public bool IsChampion { get; set; }
        public int? Place { get; set; }
        public bool CanObserve { get; set; }
        public string ConnectionState { get; set; } = "online";
        public int AiTakeoverRounds { get; set; }
    }

    private static ObservedPlayerRow[] BuildPlayersSnapshot(
        LobbyStore.Lobby? lobby,
        AccountStore.Account? selfAcc,
        RunPublicSummarySnapshot? selfSnap,
        uint currentDay,
        SessionStore sessions,
        LobbyStore? store = null)
    {
        var list = new List<ObservedPlayerRow>();
        string? selfAid = selfAcc?.AccountId.ToString("N");

        if (lobby == null)
        {
            if (selfAcc != null)
            {
                list.Add(BuildHumanRow(selfAcc.AccountId.ToString("N"), selfAcc.Username, selfSnap, null, currentDay));
            }
            return list.ToArray();
        }

        List<LobbyStore.Member> members;
        CustomRunRuleSet rules;
        bool canFreezeDay;
        lock (lobby.Members)
        {
            store?.AdvanceAiStandingsUpToDayLocked(lobby, currentDay);
            canFreezeDay = store == null || store.CanFreezeMatchupsForDayLocked(lobby, currentDay, sessions);
            members = lobby.Members.ToList();
            rules = lobby.Rules;
        }

        var isAiById = new Dictionary<string, bool>(members.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var m in members)
        {
            isAiById[m.AccountId] = m.IsAi;
            if (!m.IsAi)
            {
                var snap = (selfAid != null && m.AccountId.Equals(selfAid, StringComparison.OrdinalIgnoreCase))
                    ? selfSnap
                    : ((lobby.HasStarted && lobby.TournamentSessionId.HasValue)
                        ? FindAccountPublicSummary(m.AccountId, sessions, lobby.TournamentSessionId)
                        : null);
                list.Add(BuildHumanRow(m.AccountId, m.Username, snap, m, currentDay));
            }
            else
            {
                list.Add(BuildAiRow(m, currentDay));
            }
        }

        if (lobby.HasStarted && list.Count > 0)
        {
            var participants = list
                .Select(p => new MatchupParticipant(
                    p.AccountId,
                    p.Username,
                    isAiById.GetValueOrDefault(p.AccountId, false),
                    p.IsEliminated))
                .ToList();

            // 修复 MP-P0-05：未满足全员就位条件时绝不提前冻结未来天配对表，仅使用只读预览！
            var assignments = canFreezeDay
                ? lobby.Matchmaker.AssignRoundMatchups(currentDay, participants, rules)
                : lobby.Matchmaker.PreviewRoundMatchups(currentDay, participants, rules);

            foreach (var row in list)
            {
                if (assignments.TryGetValue(row.AccountId, out var a))
                {
                    row.AssignedOpponentAccountId = a.OpponentAccountId;
                    row.AssignedOpponentUsername = a.OpponentUsername;
                }
            }
        }

        var ranked = list
            .OrderBy(p => p.IsEliminated ? 1 : 0)
            .ThenByDescending(p => p.Prestige)
            .ThenByDescending(p => p.Wins)
            .ThenBy(p => p.Losses)
            .ToList();
        for (int i = 0; i < ranked.Count; i++)
        {
            ranked[i].Place = i + 1;
        }

        return ranked.ToArray();
    }

    private static ObservedPlayerRow BuildHumanRow(
        string accountId,
        string username,
        RunPublicSummarySnapshot? snap,
        LobbyStore.Member? m,
        uint fallbackDay)
    {
        if (m != null && m.HasFinishedRun)
        {
            return new ObservedPlayerRow
            {
                AccountId = accountId,
                Username = username,
                Hero = m.Hero,
                CurrentDay = m.FinalDay,
                CurrentHour = m.CurrentHour,
                Prestige = m.FinalPrestige,
                Wins = m.FinalVictories,
                Losses = m.FinalDefeats,
                Phase = "RunEnded",
                IsChampion = m.FinalIsChampion,
                IsEliminated = m.FinalIsEliminated,
                IsConceded = m.FinalIsConceded,
                CanObserve = false,
                ConnectionState = m.ConnectionState,
                AiTakeoverRounds = m.MissedPvpDays,
            };
        }
        if (snap != null)
        {
            bool champ = snap.State == ERunState.EndRunVictory;
            bool elim = snap.State == ERunState.EndRunDefeat;
            bool terminal = snap.IsTerminal;
            bool readyPvp = !terminal && snap.IsReadyAtHour6Pvp;
            return new ObservedPlayerRow
            {
                AccountId = accountId,
                Username = username,
                Hero = (snap.Hero == EHero.Common ? EHero.Vanessa : snap.Hero).ToString(),
                CurrentDay = snap.Day,
                CurrentHour = snap.Hour,
                Prestige = snap.Prestige,
                HasVisitedFates = snap.HasVisitedFates,
                Wins = (int)snap.Victories,
                Losses = (int)snap.Defeats,
                Phase = terminal ? "RunEnded" : (readyPvp ? "PvpReady" : "DayFlow"),
                ReadyForPvp = readyPvp,
                CombatSubmitted = snap.State == ERunState.PVPCombat,
                IsChampion = champ,
                IsEliminated = elim,
                IsConceded = m?.FinalIsConceded ?? false,
                CanObserve = !terminal,
                ConnectionState = m?.ConnectionState ?? "online",
                AiTakeoverRounds = m?.MissedPvpDays ?? 0,
            };
        }
        return new ObservedPlayerRow
        {
            AccountId = accountId,
            Username = username,
            Hero = m?.Hero ?? "Vanessa",
            CurrentDay = m?.FinalDay ?? fallbackDay,
            CurrentHour = m?.CurrentHour ?? 1,
            Prestige = m?.FinalPrestige ?? GameDefaults.StartingPrestige,
            Wins = m?.FinalVictories ?? 0,
            Losses = m?.FinalDefeats ?? 0,
            Phase = m?.HasFinishedRun == true ? "RunEnded" : "DayFlow",
            IsChampion = m?.FinalIsChampion ?? false,
            IsEliminated = m?.FinalIsEliminated ?? false,
            IsConceded = m?.FinalIsConceded ?? false,
            CanObserve = false,
            ConnectionState = m?.ConnectionState ?? "online",
            AiTakeoverRounds = m?.MissedPvpDays ?? 0,
        };
    }

    private static ObservedPlayerRow BuildAiRow(LobbyStore.Member m, uint currentDay)
    {
        return new ObservedPlayerRow
        {
            AccountId = m.AccountId,
            Username = m.Username,
            Hero = m.Hero,
            CurrentDay = Math.Max(currentDay, m.CurrentDay),
            CurrentHour = 6,
            Prestige = m.FinalPrestige,
            Wins = m.FinalVictories,
            Losses = m.FinalDefeats,
            Phase = m.FinalIsEliminated ? "RunEnded" : "DayFlow",
            IsEliminated = m.FinalIsEliminated,
            CanObserve = false,
            ConnectionState = "online",
            AiTakeoverRounds = 0,
        };
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.Body == null) return string.Empty;
        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer, 0, Math.Min(buffer.Length, maxBytes - total + 1));
            if (read <= 0) break;
            total += read;
            if (total > maxBytes) return string.Empty;
            ms.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetStringArrayCaseInsensitive(JsonElement el, string name, out List<string> values)
    {
        values = new List<string>();
        if (!TryGetPropertyCaseInsensitive(el, name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    values.Add(s.Trim());
            }
        }
        return true;
    }

    private static bool TryGetStringCaseInsensitive(JsonElement el, string name, out string? value)
    {
        value = null;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
            {
                value = p.Value.GetString();
                return true;
            }
        }
        return false;
    }

    private static bool TryGetIntCaseInsensitive(JsonElement el, string name, out int value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && p.Value.TryGetInt32(out int v))
            {
                value = v;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetBoolCaseInsensitive(JsonElement el, string name, out bool value)
    {
        value = false;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = p.Value.GetBoolean();
                return true;
            }
        }
        return false;
    }
}

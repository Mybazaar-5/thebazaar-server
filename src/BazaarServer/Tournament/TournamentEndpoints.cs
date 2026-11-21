using System.Collections.Concurrent;
using System.Text.Json;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Tournament;

/// <summary>
/// 锦标赛 / 房间 / 匹配 / 观战协调器（自建版，完整对齐客户端 TournamentHttpClient 协议）。
///
/// 支持端点：
///   POST /lobbies                         建房（CoordinatorLobby）
///   GET  /lobbies?limit=N                 公开房列表（TournamentPublicLobbyDto[]）
///   GET  /lobbies/{id}                    房间详情（CoordinatorLobby）
///   POST /lobbies/{code}/members          按码加入
///   GET  /membership                      当前所在成员关系（CoordinatorMembership）
///   DELETE /membership & /lobbies/{id}/membership         退房
///   DELETE /members/{aid} & /lobbies/{id}/members/{aid}   踢人
///   PUT  /lobbies/{id}/presence & /presence               心跳
///   PUT  /lobbies/{id}/countdown & /countdown             开始倒计时并补齐 8 人 AI 选手
///   PUT  /lobbies/{id}/acceptance & /acceptance           接受/拒绝指定房
///   GET/PUT/DELETE /queue                 单人秒匹配 8 人 AI 锦标赛
///   GET  /sessions/current/status
///   GET  /sessions/current/gameplay-status
///   GET  /tournament/status               实时同步玩家 RunState 进度的锦标赛状态
///   GET  /events                          SSE（发送 data: update 驱动客户端实时刷新）
///   GET  /spectate & /spectate/{code}     观战名单与数据
/// </summary>
public sealed class LobbyStore
{
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

    private readonly ConcurrentDictionary<string, Lobby> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _codeToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _memberToLobby = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RemovalNotice> _pendingRemovals = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _waitersLock = new();
    private List<TaskCompletionSource<bool>> _waiters = new();
    private long _changeVersion;
    private Func<DateTime> _utcNowProvider = static () => DateTime.UtcNow;

    public long ChangeVersion => Volatile.Read(ref _changeVersion);

    /// <summary>当前 UTC 时间（支持测试注入可控时钟验证倒计时与超时解锁）。</summary>
    internal DateTime UtcNow => _utcNowProvider();

    /// <summary>测试辅助：覆写锦标赛时钟源（传 null 恢复系统 UTC 时钟）。</summary>
    public void SetClockForTesting(Func<DateTime>? utcNowProvider)
        => _utcNowProvider = utcNowProvider ?? (static () => DateTime.UtcNow);

    public sealed record RemovalNotice(string LobbyId, string Code, string Reason);

    public sealed class RoundBarrierState
    {
        public uint Day { get; init; }
        public DateTime? FirstReadyAtUtc { get; set; }
        public DateTime? DeadlineAtUtc { get; set; }
        public bool IsAccelerated { get; set; }
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
        public string HostAccountId { get; init; } = "";
        public string HostUsername { get; init; } = "";
        public int PlayerCap { get; set; } = 8;
        public bool IsPublic { get; init; }
        public bool ExcludeHostFromGameplay { get; init; }
        public bool HasStarted { get; set; }
        public bool IsCancelled { get; set; }
        public bool CountdownStarted { get; set; }
        public Guid? TournamentSessionId { get; set; }
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(6);
        public CustomRunRuleSet Rules { get; set; } = new();
        public int RoundCountdownSeconds { get; set; } = 120;
        public int AcceleratedRoundCountdownSeconds { get; set; } = 30;
        public List<string> BannedEncounterIds { get; set; } = new();
        public TournamentMatchmaker Matchmaker { get; init; } = new();
        public Dictionary<uint, RoundBarrierState> RoundBarriers { get; } = new();
        public readonly List<Member> Members = new();
    }

    public sealed class Member
    {
        public string AccountId { get; init; } = "";
        public string Username { get; init; } = "";
        public string Hero { get; set; } = "Vanessa";
        public bool IsAi { get; init; }
        public DateTime JoinedAt { get; init; } = DateTime.UtcNow;
        public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

        // 实时回合同步进度（用于多真人 Day D Hour 6 PvP 同步屏障）
        public uint CurrentDay { get; set; } = 1;
        public uint CurrentHour { get; set; } = 1;
        public bool IsReadyAtHour6Pvp { get; set; }

        // 结束快照：一旦人类玩家完成/放弃当前锦标赛对局，保留终局榜单数据供 EndOfRun 展示，同时从 /membership 摘除防自动重开
        public bool HasFinishedRun { get; set; }
        public bool FinalIsChampion { get; set; }
        public bool FinalIsEliminated { get; set; }
        public int FinalVictories { get; set; }
        public int FinalDefeats { get; set; }
        public int FinalPrestige { get; set; } = 20;
        public uint FinalDay { get; set; } = 1;
    }

    public void NotifyChanged()
    {
        Interlocked.Increment(ref _changeVersion);
        List<TaskCompletionSource<bool>>? toWake = null;
        lock (_waitersLock)
        {
            if (_waiters.Count > 0)
            {
                toWake = _waiters;
                _waiters = new List<TaskCompletionSource<bool>>();
            }
        }
        if (toWake != null)
        {
            foreach (var tcs in toWake)
            {
                tcs.TrySetResult(true);
            }
        }
    }

    /// <summary>
    /// 异步挂起直到发生房间变更（NotifyChanged）或超过保活超时时间，避免轮询空耗 CPU。
    /// </summary>
    public async Task<bool> WaitForChangeAsync(long seenVersion, TimeSpan timeout, CancellationToken ct)
    {
        if (Volatile.Read(ref _changeVersion) != seenVersion)
            return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waitersLock)
        {
            if (Volatile.Read(ref _changeVersion) != seenVersion)
                return true;
            _waiters.Add(tcs);
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
                _waiters.Remove(tcs);
            }
        }
    }

    public Lobby Create(
        AccountStore.Account host,
        string? name,
        string? playMode,
        int playerCap,
        bool isPublic,
        CustomRunRuleSet? rules = null,
        int? roundCountdownSeconds = null,
        int? acceleratedRoundCountdownSeconds = null,
        IEnumerable<string>? bannedEncounterIds = null)
    {
        string hostAid = host.AccountId.ToString("N");
        CleanupPreviousMembership(hostAid);

        string lobbyId = Guid.NewGuid().ToString("N")[..12];
        var lobby = new Lobby
        {
            LobbyId = lobbyId,
            Code = NewCode(),
            Name = string.IsNullOrWhiteSpace(name) ? host.Username + "'s Tournament" : name.Trim(),
            Kind = "custom",
            PlayMode = string.IsNullOrWhiteSpace(playMode) ? "Tournament" : playMode!,
            HostAccountId = hostAid,
            HostUsername = host.Username,
            PlayerCap = Math.Clamp(playerCap <= 0 ? 8 : playerCap, 2, 16),
            IsPublic = isPublic,
            Matchmaker = new TournamentMatchmaker(lobbyId),
        };
        lobby.Members.Add(new Member
        {
            AccountId = hostAid,
            Username = host.Username,
            Hero = "Vanessa",
        });

        ApplyNormalizedRulesLocked(
            lobby,
            rules,
            playerCapOverride: playerCap > 0 ? playerCap : null,
            roundCountdownSeconds: roundCountdownSeconds,
            acceleratedRoundCountdownSeconds: acceleratedRoundCountdownSeconds,
            bannedEncounterIds: bannedEncounterIds,
            allowUpdatePlayerCap: true);

        _byId[lobby.LobbyId] = lobby;
        _codeToId[lobby.Code] = lobby.LobbyId;
        _memberToLobby[hostAid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(hostAid, out _);
        NotifyChanged();
        return lobby;
    }

    /// <summary>
    /// 仅房主更新房间规则，归一化并按需同步开赛前人数上限。
    /// </summary>
    public void UpdateRules(
        Lobby lobby,
        CustomRunRuleSet incomingRules,
        int? roundCountdownSeconds = null,
        int? acceleratedRoundCountdownSeconds = null,
        IEnumerable<string>? bannedEncounterIds = null)
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
                allowUpdatePlayerCap: !lobby.HasStarted);
        }
        NotifyChanged();
    }

    /// <summary>
    /// 房主在开赛前管理房间内的 AI 陪练机器人（add / fill / remove / clear），不自动触发开赛倒计时。
    /// </summary>
    public (bool Ok, string? Error) ManageBots(Lobby lobby, string action)
    {
        string act = (action ?? string.Empty).Trim().ToLowerInvariant();
        if (act is not ("add" or "fill" or "remove" or "clear"))
            return (false, "invalid bot action");

        lock (lobby.Members)
        {
            if (lobby.HasStarted || lobby.CountdownStarted)
                return (false, "lobby already started");

            switch (act)
            {
                case "add":
                    if (lobby.Members.Count < lobby.PlayerCap)
                        TryAddNextAiMember(lobby);
                    break;
                case "fill":
                    while (lobby.Members.Count < lobby.PlayerCap)
                    {
                        if (!TryAddNextAiMember(lobby)) break;
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

        NotifyChanged();
        return (true, null);
    }

    /// <summary>
    /// 单人/多人快速匹配队列：立即创建（或加入）含 7 名 AI 对手的 8 人锦标赛对局并启动。
    /// </summary>
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
                    FillAiCompetitors(existing, 8);
                    StartCountdown(existing.LobbyId, Guid.NewGuid());
                }
                return existing;
            }
            CleanupPreviousMembership(aid);
        }

        string lobbyId = Guid.NewGuid().ToString("N")[..12];
        var lobby = new Lobby
        {
            LobbyId = lobbyId,
            Code = NewCode(),
            Name = "Ranked Matchmaking Tournament",
            Kind = "matchmaking",
            PlayMode = "Tournament",
            HostAccountId = aid,
            HostUsername = player.Username,
            PlayerCap = 8,
            IsPublic = false,
            HasStarted = true,
            CountdownStarted = true,
            TournamentSessionId = Guid.NewGuid(),
            Matchmaker = new TournamentMatchmaker(lobbyId),
        };
        lobby.Members.Add(new Member
        {
            AccountId = aid,
            Username = player.Username,
            Hero = "Vanessa",
        });
        FillAiCompetitors(lobby, 8);
        EnsureDayMatchups(lobby, 1u, lobby.Members);

        _byId[lobby.LobbyId] = lobby;
        _codeToId[lobby.Code] = lobby.LobbyId;
        _memberToLobby[aid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(aid, out _);
        NotifyChanged();
        return lobby;
    }

    public Lobby? Get(string? lobbyId)
        => !string.IsNullOrWhiteSpace(lobbyId) && _byId.TryGetValue(lobbyId, out var l) ? l : null;

    public Lobby? GetByCode(string? code)
        => !string.IsNullOrWhiteSpace(code) && _codeToId.TryGetValue(code, out var id) ? Get(id) : null;

    public Lobby? GetByMember(string? accountId)
        => !string.IsNullOrWhiteSpace(accountId) && _memberToLobby.TryGetValue(accountId, out var id) ? Get(id) : null;

    public RemovalNotice? PopRemovalNotice(string accountId)
        => _pendingRemovals.TryRemove(accountId, out var notice) ? notice : null;

    public Lobby? Join(string code, AccountStore.Account acc)
    {
        string aid = acc.AccountId.ToString("N");
        var lobby = GetByCode(code) ?? Get(code);
        if (lobby == null || lobby.IsCancelled) return null;

        var prev = GetByMember(aid);
        if (prev != null && prev.LobbyId != lobby.LobbyId)
        {
            CleanupPreviousMembership(aid);
        }

        lock (lobby.Members)
        {
            if (lobby.Members.Exists(m => m.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase)))
            {
                _memberToLobby[aid] = lobby.LobbyId;
                return lobby;
            }
            if (lobby.Members.Count >= lobby.PlayerCap) return lobby;
            lobby.Members.Add(new Member
            {
                AccountId = aid,
                Username = acc.Username,
                Hero = HeroCycle[lobby.Members.Count % HeroCycle.Length],
            });
        }
        _memberToLobby[aid] = lobby.LobbyId;
        _pendingRemovals.TryRemove(aid, out _);
        NotifyChanged();
        return lobby;
    }

    public void Leave(string accountId, string? reason = null)
    {
        var lobby = GetByMember(accountId);
        if (lobby == null) return;
        lock (lobby.Members)
        {
            lobby.Members.RemoveAll(m => m.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            int humanLeft = lobby.Members.Count(m => !m.IsAi);
            if (humanLeft == 0)
            {
                lobby.IsCancelled = true;
                _byId.TryRemove(lobby.LobbyId, out _);
                _codeToId.TryRemove(lobby.Code, out _);
            }
        }
        _memberToLobby.TryRemove(accountId, out _);
        if (!string.IsNullOrEmpty(reason))
        {
            _pendingRemovals[accountId] = new RemovalNotice(lobby.LobbyId, lobby.Code, reason);
        }
        NotifyChanged();
    }

    public void Touch(string accountId)
    {
        var lobby = GetByMember(accountId);
        if (lobby == null) return;
        lock (lobby.Members)
        {
            var m = lobby.Members.Find(x => x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase));
            if (m != null) m.LastSeenAt = DateTime.UtcNow;
        }
    }

    public void StartCountdown(string lobbyId, Guid sessionId)
    {
        var lobby = Get(lobbyId);
        if (lobby == null) return;
        List<Member> snapshot;
        lock (lobby.Members)
        {
            int targetCount = Math.Max(lobby.Members.Count, Math.Clamp(lobby.PlayerCap, 2, 16));
            FillAiCompetitors(lobby, targetCount);
            lobby.CountdownStarted = true;
            lobby.HasStarted = true;
            lobby.TournamentSessionId ??= sessionId;
            snapshot = lobby.Members.ToList();
        }
        EnsureDayMatchups(lobby, 1u, snapshot);
        NotifyChanged();
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

        lock (lobby.Members)
        {
            rules = lobby.Rules;
            string normalized = NormalizeAccountKey(accountId);
            var selfMember = lobby.Members.Find(m =>
                m.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && m.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
            if (selfMember != null)
                canonicalSelfId = selfMember.AccountId;

            participants = new List<MatchupParticipant>(lobby.Members.Count);
            int aiIdx = 0;
            foreach (var m in lobby.Members)
            {
                bool elim = m.FinalIsEliminated;
                if (!m.IsAi && !elim && sessions != null)
                {
                    var mem = sessions.GetByAccount(m.AccountId);
                    var run = mem != null ? SessionRegistry.Get(mem.Sid)?.Run : null;
                    if (run != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId))
                    {
                        elim = run.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                    }
                }
                else if (m.IsAi)
                {
                    if (!rules.BotNonLethalPrestigeLock)
                    {
                        int roundsPlayed = Math.Max(0, (int)day - 1);
                        int prestige = 20;
                        for (int d = 1; d <= roundsPlayed; d++)
                        {
                            if (((d * 3 + aiIdx) % 5) >= 3)
                                prestige = Math.Max(0, prestige - d);
                        }
                        elim = prestige <= 0;
                    }
                    aiIdx++;
                }

                participants.Add(new MatchupParticipant(m.AccountId, m.Username, m.IsAi, elim));
            }
        }

        lobby.Matchmaker.AssignRoundMatchups(day, participants, rules);
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
                    var r = mem != null ? SessionRegistry.Get(mem.Sid)?.Run : null;
                    if (r != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId) &&
                        r.State is ERunState.EndRunDefeat or ERunState.Shutdown)
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

    public void RecordRunProgress(Lobby lobby, string accountId, RunState run)
    {
        if (lobby == null || run == null || string.IsNullOrWhiteSpace(accountId))
            return;

        string normalized = NormalizeAccountKey(accountId);
        bool changed = false;
        lock (lobby.Members)
        {
            var m = lobby.Members.Find(x =>
                x.AccountId.Equals(accountId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrEmpty(normalized) && x.AccountId.Equals(normalized, StringComparison.OrdinalIgnoreCase)));
            if (m != null)
            {
                DateTime now = UtcNow;
                bool isTerminal = Persistence.IsTerminalState(run.State);
                bool readyH6 = !isTerminal && ComputeIsAtOrPastHour6Pvp(run);
                int prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);

                if (m.CurrentDay != run.Day ||
                    m.CurrentHour != run.Hour ||
                    m.IsReadyAtHour6Pvp != readyH6 ||
                    m.FinalVictories != (int)run.Victories ||
                    m.FinalDefeats != (int)run.Defeats ||
                    m.FinalPrestige != prestige ||
                    m.FinalDay != run.Day ||
                    (isTerminal && !m.HasFinishedRun))
                {
                    changed = true;
                }

                m.LastSeenAt = now;
                m.CurrentDay = run.Day;
                m.CurrentHour = run.Hour;
                m.IsReadyAtHour6Pvp = readyH6;
                m.FinalVictories = (int)run.Victories;
                m.FinalDefeats = (int)run.Defeats;
                m.FinalPrestige = prestige;
                m.FinalDay = run.Day;
                if (isTerminal)
                {
                    m.HasFinishedRun = true;
                    m.FinalIsChampion = run.State == ERunState.EndRunVictory;
                    m.FinalIsEliminated = run.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                }
            }
        }
        if (changed)
            NotifyChanged();
    }

    /// <summary>判断对局是否已到达或正在结算当天 Hour 6 PvP。</summary>
    public static bool ComputeIsAtOrPastHour6Pvp(RunState run)
    {
        if (run == null || Persistence.IsTerminalState(run.State))
            return false;
        return run.State == ERunState.TournamentPvpChoice
            || run.State == ERunState.TournamentWaitingRoom
            || run.State == ERunState.PVPCombat
            || (run.Hour >= 6 && (run.Victories + run.Defeats) >= run.Day)
            || (run.State == ERunState.Choice && run.Hour >= 6);
    }

    /// <summary>
    /// 检查多真人锦标赛第 <paramref name="day"/> 天 Hour 6 回合同步屏障：
    ///   · 仅剩 1 名存活真人时立即可开战（CanStartPvp = true）；
    ///   · 存活真人 >= 2 时，须全部存活真人到达 Day D Hour 6（或倒计时归零超时解锁）方可进 PvP；
    ///   · 仅剩最后 1 名存活真人未就位时自动切换加速倒计时 AcceleratedRoundCountdownSeconds 并推送 SSE。
    /// </summary>
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

                RunState? liveRun = isSelf ? selfRun : null;
                if (liveRun == null && sessions != null)
                {
                    var mem = sessions.GetByAccount(m.AccountId);
                    var candidate = mem != null ? SessionRegistry.Get(mem.Sid)?.Run : null;
                    if (candidate != null &&
                        (!lobby.TournamentSessionId.HasValue || mem!.TournamentSessionId == lobby.TournamentSessionId))
                    {
                        liveRun = candidate;
                    }
                }

                if (liveRun != null)
                {
                    bool term = Persistence.IsTerminalState(liveRun.State);
                    bool h6Ready = !term && ComputeIsAtOrPastHour6Pvp(liveRun);
                    if (m.CurrentDay != liveRun.Day || m.CurrentHour != liveRun.Hour || m.IsReadyAtHour6Pvp != h6Ready)
                    {
                        m.CurrentDay = liveRun.Day;
                        m.CurrentHour = liveRun.Hour;
                        m.IsReadyAtHour6Pvp = h6Ready;
                    }
                    if (term && !m.HasFinishedRun)
                    {
                        m.HasFinishedRun = true;
                        m.FinalIsChampion = liveRun.State == ERunState.EndRunVictory;
                        m.FinalIsEliminated = liveRun.State is ERunState.EndRunDefeat or ERunState.Shutdown;
                    }
                }

                bool elimOrDone = m.HasFinishedRun || m.FinalIsEliminated || m.FinalIsChampion
                                  || (liveRun != null && Persistence.IsTerminalState(liveRun.State));
                if (elimOrDone)
                    continue;

                survivingHumans++;
                bool reachedDayHour6 = m.CurrentDay > day || (m.CurrentDay == day && m.IsReadyAtHour6Pvp);
                if (reachedDayHour6)
                    readyHumans++;
                else
                    notReadyHumans++;
            }

            if (survivingHumans <= 1)
            {
                decision = new PvpBarrierDecision(true, false, null, survivingHumans, readyHumans, notReadyHumans, false, false, 0, null);
            }
            else if (notReadyHumans == 0)
            {
                if (lobby.RoundBarriers.TryGetValue(day, out var doneBarrier) && !doneBarrier.TimedOutUnlocked)
                {
                    doneBarrier.TimedOutUnlocked = true;
                    shouldNotify = true;
                }
                decision = new PvpBarrierDecision(true, false, null, survivingHumans, readyHumans, 0, false, false, 0, null);
            }
            else
            {
                lobby.RoundBarriers.TryGetValue(day, out var barrier);
                DateTime now = UtcNow;

                if (barrier is { TimedOutUnlocked: true })
                {
                    decision = new PvpBarrierDecision(
                        true, false, barrier.DeadlineAtUtc,
                        survivingHumans, readyHumans, notReadyHumans,
                        barrier.IsAccelerated, true, 0, null);
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
                            barrier.FirstReadyAtUtc = now;
                            barrier.DeadlineAtUtc = now.AddSeconds(Math.Max(0, lobby.RoundCountdownSeconds));
                            shouldNotify = true;
                        }
                        if (notReadyHumans == 1 && !barrier.IsAccelerated)
                        {
                            barrier.IsAccelerated = true;
                            DateTime accelDeadline = now.AddSeconds(Math.Max(0, lobby.AcceleratedRoundCountdownSeconds));
                            if (!barrier.DeadlineAtUtc.HasValue || accelDeadline < barrier.DeadlineAtUtc.Value)
                                barrier.DeadlineAtUtc = accelDeadline;
                            shouldNotify = true;
                        }
                        if (barrier.DeadlineAtUtc.HasValue && now >= barrier.DeadlineAtUtc.Value)
                        {
                            barrier.TimedOutUnlocked = true;
                            shouldNotify = true;
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
                        int remSec = dl.HasValue
                            ? Math.Max(1, (int)Math.Ceiling((dl.Value - now).TotalSeconds))
                            : Math.Max(1, notReadyHumans == 1 ? lobby.AcceleratedRoundCountdownSeconds : lobby.RoundCountdownSeconds);
                        decision = new PvpBarrierDecision(
                            false, true, dl,
                            survivingHumans, readyHumans, notReadyHumans,
                            barrier?.IsAccelerated ?? false, false, remSec,
                            $"正在等待其他真人玩家完成第 {day} 天准备（未就位 {notReadyHumans} 人，剩余 {remSec} 秒）");
                    }
                }
            }
        }

        if (shouldNotify)
            NotifyChanged();

        return decision;
    }

    private static string NormalizeAccountKey(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return string.Empty;
        return Guid.TryParse(accountId, out var g) ? g.ToString("N") : accountId.Trim();
    }

    public IEnumerable<Lobby> PublicLobbies()
        => _byId.Values.Where(l => l.IsPublic && !l.HasStarted && !l.IsCancelled);

    public IEnumerable<Lobby> All => _byId.Values;

    private void CleanupPreviousMembership(string accountId)
    {
        if (_memberToLobby.TryGetValue(accountId, out var oldId))
        {
            var old = Get(oldId);
            if (old != null)
            {
                var mem = FindMember(old, accountId);
                // 仅在不打断他人或已完结时直接退房
                if (mem == null || mem.HasFinishedRun || !old.HasStarted || old.Members.Count(m => !m.IsAi) <= 1)
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
        bool allowUpdatePlayerCap)
    {
        var normalized = (incomingRules ?? lobby.Rules ?? new CustomRunRuleSet()).CloneNormalized();

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

        // 房主自身流派倾向不写入房间公共规则，开局后由全员在 Day 1 Hour 1 各自独立选定并锁定
        normalized.SelectedArchetypeMask = 0UL;
        lobby.Rules = normalized;
    }

    private static void EnsureDayMatchups(Lobby lobby, uint day, IEnumerable<Member> members)
    {
        var participants = members
            .Where(m => !m.FinalIsEliminated)
            .Select(m => new MatchupParticipant(m.AccountId, m.Username, m.IsAi, m.FinalIsEliminated))
            .ToList();
        lobby.Matchmaker.AssignRoundMatchups(day, participants, lobby.Rules);
    }

    private static void FillAiCompetitors(Lobby lobby, int targetCount)
    {
        while (lobby.Members.Count < targetCount)
        {
            if (!TryAddNextAiMember(lobby)) break;
        }
    }

    private static bool TryAddNextAiMember(Lobby lobby)
    {
        for (int idx = 0; idx < 64; idx++)
        {
            var candidate = CreateAiMember(lobby, idx);
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

    private static Member CreateAiMember(Lobby lobby, int idx)
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
        };
    }

    private static string DeterministicGuidHex(string seed)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new Guid(hash).ToString("N");
    }

    private static string NewCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = System.Security.Cryptography.RandomNumberGenerator.GetBytes(6);
        var sb = new System.Text.StringBuilder(6);
        foreach (byte b in rng) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }
}

public static class TournamentEndpoints
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = null };
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    public static void Map(WebApplication app, LobbyStore lobbies)
    {
        // ── 建房 ─────────────────────────────────────────────────────────────
        app.MapPost("/lobbies", async (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();

            string? name = null;
            string playMode = "Tournament";
            int? explicitCap = null;
            int? rulesPlayerCap = null;
            bool isPublic = true;
            int? roundCountdownSeconds = null;
            int? acceleratedRoundCountdownSeconds = null;
            List<string>? bannedEncounterIds = null;
            CustomRunRuleSet? parsedRules = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var r = doc.RootElement;
                if (TryGetStringCaseInsensitive(r, "name", out var n)) name = n;
                if (TryGetStringCaseInsensitive(r, "playMode", out var pm) && !string.IsNullOrWhiteSpace(pm)) playMode = pm!;
                if (TryGetIntCaseInsensitive(r, "playerCap", out int c)) explicitCap = c;
                if (TryGetBoolCaseInsensitive(r, "isPublic", out bool ip)) isPublic = ip;
                if (TryGetIntCaseInsensitive(r, "roundCountdownSeconds", out int rcs)) roundCountdownSeconds = rcs;
                if (TryGetIntCaseInsensitive(r, "acceleratedRoundCountdownSeconds", out int arcs)) acceleratedRoundCountdownSeconds = arcs;
                if (TryGetStringArrayCaseInsensitive(r, "bannedEncounterIds", out var banned)) bannedEncounterIds = banned;
                if (TryGetPropertyCaseInsensitive(r, "rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Object)
                {
                    parsedRules = JsonSerializer.Deserialize<CustomRunRuleSet>(rulesEl.GetRawText(), CaseInsensitiveJson);
                    if (TryGetIntCaseInsensitive(rulesEl, "playerCap", out int rc))
                        rulesPlayerCap = rc;
                }
            }
            catch { }

            // #13 修复 16 人爆房：未携带房主自定义 rules.PlayerCap 时，无视原版客户端硬编码发来的 playerCap=16，统一默认 8 人
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
                bannedEncounterIds);
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 房主修改房间规则（开赛前同步 PlayerCap）──────────────────────────
        app.MapPut("/lobbies/{id}/rules", async (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string aid = acc.AccountId.ToString("N");
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "lobby not found" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "only host can update rules" }, Json, statusCode: 403);

            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            CustomRunRuleSet? incoming = null;
            int? roundCountdownSeconds = null;
            int? acceleratedRoundCountdownSeconds = null;
            List<string>? bannedEncounterIds = null;

            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
                var r = doc.RootElement;
                if (TryGetIntCaseInsensitive(r, "roundCountdownSeconds", out int rcs)) roundCountdownSeconds = rcs;
                if (TryGetIntCaseInsensitive(r, "acceleratedRoundCountdownSeconds", out int arcs)) acceleratedRoundCountdownSeconds = arcs;
                if (TryGetStringArrayCaseInsensitive(r, "bannedEncounterIds", out var banned)) bannedEncounterIds = banned;

                string rulesJson = TryGetPropertyCaseInsensitive(r, "rules", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object
                    ? wrapped.GetRawText()
                    : r.GetRawText();
                incoming = JsonSerializer.Deserialize<CustomRunRuleSet>(rulesJson, CaseInsensitiveJson);
            }
            catch
            {
                return Results.Json(new { error = "invalid rules payload" }, Json, statusCode: 400);
            }

            lobbies.UpdateRules(
                lobby,
                incoming ?? new CustomRunRuleSet(),
                roundCountdownSeconds,
                acceleratedRoundCountdownSeconds,
                bannedEncounterIds);
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 房主在开赛前管理 AI 陪练（add / fill / remove / clear）───────────
        app.MapPost("/lobbies/{id}/bots", async (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string aid = acc.AccountId.ToString("N");
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "lobby not found" }, Json, statusCode: 404);
            if (!lobby.HostAccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { error = "only host can manage bots" }, Json, statusCode: 403);

            string raw = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
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
                int code = error == "lobby already started" ? 409 : 400;
                return Results.Json(new { error }, Json, statusCode: code);
            }
            return Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 公开房列表 ───────────────────────────────────────────────────────
        app.MapGet("/lobbies", (HttpContext ctx) =>
        {
            int limit = 50;
            if (int.TryParse(ctx.Request.Query["limit"], out int l)) limit = Math.Clamp(l, 1, 200);
            var list = lobbies.PublicLobbies().Take(limit).Select(PublicDto).ToArray();
            return Results.Json(list, Json);
        });

        app.MapGet("/lobbies/{id}", (string id) =>
        {
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            return lobby == null || lobby.IsCancelled
                ? Results.Json(new { error = "lobby not found" }, Json, statusCode: 404)
                : Results.Json(CoordinatorLobbyDto(lobby), Json);
        });

        // ── 按码加入 ─────────────────────────────────────────────────────────
        app.MapPost("/lobbies/{code}/members", (string code, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.Join(code, acc);
            if (lobby == null) return Results.Json(new { error = "invalid code" }, Json, statusCode: 404);
            return Results.Json(new { LobbyId = lobby.LobbyId, Code = lobby.Code }, Json);
        });

        // ── 我所在的房（严格对齐 TournamentHttpClient.CoordinatorMembership）────
        app.MapGet("/membership", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            string aid = acc.AccountId.ToString("N");
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

        // 退房（支持客户端 /lobbies/{id}/membership 与短路径 /membership）
        app.MapDelete("/membership", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Leave(acc.AccountId.ToString("N"));
            return Results.NoContent();
        });
        app.MapDelete("/lobbies/{id}/membership", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Leave(acc.AccountId.ToString("N"));
            return Results.NoContent();
        });

        // 踢人（支持 /lobbies/{id}/members/{accountId} 与 /members/{accountId}）
        app.MapDelete("/members/{accountId}", (string accountId) =>
        {
            lobbies.Leave(accountId, reason: "kicked");
            return Results.Json(new { ok = true }, Json);
        });
        app.MapDelete("/lobbies/{id}/members/{accountId}", (string id, string accountId) =>
        {
            lobbies.Leave(accountId, reason: "kicked");
            return Results.Json(new { ok = true }, Json);
        });

        // ── 心跳 / 倒计时开局 ───────────────────────────────────────────────
        app.MapPut("/lobbies/{id}/presence", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Touch(acc.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPut("/presence", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Touch(acc.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });

        app.MapPut("/countdown", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.GetByMember(acc.AccountId.ToString("N"));
            if (lobby == null) return Results.Json(new { error = "not in lobby" }, Json, statusCode: 404);
            lobbies.StartCountdown(lobby.LobbyId, Guid.NewGuid());
            return Results.Json(new { ok = true, tournamentSessionId = lobby.TournamentSessionId }, Json);
        });
        app.MapPut("/lobbies/{id}/countdown", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.Get(id) ?? lobbies.GetByMember(acc.AccountId.ToString("N"));
            if (lobby == null) return Results.Json(new { error = "lobby not found" }, Json, statusCode: 404);
            lobbies.StartCountdown(lobby.LobbyId, Guid.NewGuid());
            return Results.Json(new { ok = true, tournamentSessionId = lobby.TournamentSessionId }, Json);
        });

        // ── 匹配队列（单人秒排 8 人 AI 锦标赛）───────────────────────────────
        app.MapGet("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.GetByMember(acc.AccountId.ToString("N"));
            if (lobby != null && lobby.Kind == "matchmaking" && !lobby.IsCancelled)
            {
                var mem = lobbies.FindMember(lobby, acc.AccountId.ToString("N"));
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
            return Results.Json(new { State = "idle", QueuedCount = 0, MaxPlayers = 8 }, Json);
        });
        app.MapPut("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.EnqueueMatchmaking(acc);
            return Results.Json(new
            {
                State = "lobby",
                QueuedCount = lobby.Members.Count,
                MaxPlayers = lobby.PlayerCap,
                TournamentSessionId = lobby.TournamentSessionId,
            }, Json);
        });
        app.MapDelete("/queue", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            var lobby = lobbies.GetByMember(acc.AccountId.ToString("N"));
            if (lobby != null && lobby.Kind == "matchmaking")
            {
                lobbies.Leave(acc.AccountId.ToString("N"));
            }
            return Results.Json(new { ok = true }, Json);
        });

        // ── 接受 / 拒绝分配房 ───────────────────────────────────────────────
        app.MapPut("/acceptance", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Touch(acc.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapPut("/lobbies/{id}/acceptance", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Touch(acc.AccountId.ToString("N"));
            return Results.Json(new { ok = true }, Json);
        });
        app.MapDelete("/acceptance", (HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Leave(acc.AccountId.ToString("N"));
            return Results.NoContent();
        });
        app.MapDelete("/lobbies/{id}/acceptance", (string id, HttpContext ctx, AccountStore accounts) =>
        {
            var acc = Resolve(ctx, accounts);
            lobbies.Leave(acc.AccountId.ToString("N"));
            return Results.NoContent();
        });

        // ── 锦标赛状态（实时同步玩家 RunState 的 Day / Hour / V / D / Prestige / PvP）──
        app.MapGet("/sessions/current/status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(StatusFor(acc, lobbies, sessions), Json);
        });
        app.MapGet("/sessions/current/gameplay-status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(StatusFor(acc, lobbies, sessions), Json);
        });
        app.MapGet("/tournament/status", (HttpContext ctx, AccountStore accounts, SessionStore sessions) =>
        {
            var acc = Resolve(ctx, accounts);
            return Results.Json(StatusFor(acc, lobbies, sessions), Json);
        });

        // ── 观战 ─────────────────────────────────────────────────────────────
        app.MapGet("/spectate", () =>
        {
            var live = lobbies.All
                .Where(l => l.HasStarted && !l.IsCancelled)
                .Select(l => new
                {
                    WatchCode = l.Code,
                    TournamentSessionId = l.TournamentSessionId ?? Guid.Empty,
                    Name = l.Name,
                    PlayMode = l.PlayMode,
                    TotalPlayers = l.Members.Count,
                    ActivePlayers = l.Members.Count(m => !m.FinalIsEliminated),
                })
                .ToArray();
            return Results.Json(live, Json);
        });
        app.MapGet("/spectate/{code}", (string code, SessionStore sessions) =>
        {
            var lobby = lobbies.GetByCode(code) ?? lobbies.Get(code);
            if (lobby == null) return Results.Json(new { error = "not found" }, Json, statusCode: 404);
            var players = BuildPlayersSnapshot(lobby, null, null, 1, sessions);
            uint curDay = players.Length > 0 ? players.Max(p => p.CurrentDay) : 1u;
            int activeCount = players.Count(p => !p.IsEliminated);
            return Results.Json(new
            {
                WatchCode = lobby.Code,
                TournamentSessionId = lobby.TournamentSessionId ?? Guid.Empty,
                Name = lobby.Name,
                PlayMode = lobby.PlayMode,
                Source = lobby.Kind,
                Phase = activeCount <= 1 && lobby.HasStarted ? "RunEnded" : (lobby.HasStarted ? "DayFlow" : "WaitingRoom"),
                CurrentDay = curDay,
                TotalPlayers = players.Length,
                ActivePlayers = activeCount,
                ReadyPlayers = activeCount,
                DeadlineAt = (DateTime?)null,
                StartedAt = lobby.CreatedAt,
                Players = players,
            }, Json);
        });

        // ── SSE 事件流（发送真实 data: update 包，唤醒客户端 ReadServerSentEventData）──
        app.MapGet("/events", async (HttpContext ctx, CancellationToken ct) =>
        {
            await StreamSseUpdatesAsync(ctx, lobbies, ct);
        });
        app.MapGet("/spectate/{code}/events", async (string code, HttpContext ctx, CancellationToken ct) =>
        {
            await StreamSseUpdatesAsync(ctx, lobbies, ct);
        });
    }

    private static async Task StreamSseUpdatesAsync(HttpContext ctx, LobbyStore lobbies, CancellationToken ct)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.Headers["Connection"] = "keep-alive";

        long lastVer = lobbies.ChangeVersion;
        await ctx.Response.WriteAsync("data: connected\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool changed = await lobbies.WaitForChangeAsync(lastVer, TimeSpan.FromSeconds(25), ct);
                if (ct.IsCancellationRequested) break;

                long curVer = lobbies.ChangeVersion;
                if (changed || curVer != lastVer)
                {
                    lastVer = curVer;
                    await ctx.Response.WriteAsync("data: update\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
                else
                {
                    await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static AccountStore.Account Resolve(HttpContext ctx, AccountStore accounts)
    {
        var acc = accounts.FindByAccessToken(ctx.Request.Headers["Authorization"].ToString());
        if (acc != null) return acc;
        acc = accounts.FindByAccountId(ctx.Request.Headers["X-Account-Id"].ToString());
        if (acc != null) return acc;
        acc = accounts.FindByAccountId(ctx.Request.Headers["aid"].ToString());
        if (acc != null) return acc;
        string xUser = ctx.Request.Headers["X-Username"].ToString();
        if (!string.IsNullOrWhiteSpace(xUser)) return accounts.GetOrCreateByName(xUser);
        string uid = ctx.Request.Headers["uid"].ToString();
        return accounts.GetOrCreateByName(string.IsNullOrWhiteSpace(uid) ? "guest" : uid);
    }

    private static object CoordinatorLobbyDto(LobbyStore.Lobby lobby)
    {
        DateTime now = DateTime.UtcNow;
        DateTime expires = now.AddHours(6);
        List<LobbyStore.Member> snapshot;
        CustomRunRuleSet rulesSnapshot;
        string[] bannedSnapshot;
        int playerCap;
        int roundCountdown;
        int accelCountdown;
        lock (lobby.Members)
        {
            snapshot = lobby.Members.ToList();
            rulesSnapshot = lobby.Rules;
            bannedSnapshot = lobby.BannedEncounterIds.ToArray();
            playerCap = lobby.PlayerCap;
            roundCountdown = lobby.RoundCountdownSeconds;
            accelCountdown = lobby.AcceleratedRoundCountdownSeconds;
        }

        return new
        {
            LobbyId = lobby.LobbyId,
            SessionId = lobby.TournamentSessionId?.ToString("D"),
            Kind = lobby.Kind,
            State = lobby.IsCancelled ? "cancelled" : (lobby.HasStarted ? "started" : "waiting"),
            Code = lobby.Code,
            HostAccountId = lobby.HostAccountId,
            PlayerCap = playerCap,
            MaxPlayers = playerCap,
            Name = lobby.Name,
            ExcludeHostFromGameplay = lobby.ExcludeHostFromGameplay,
            IsPublic = lobby.IsPublic,
            ExpiresAt = lobby.ExpiresAt,
            ServerTime = now,
            CanStart = !lobby.HasStarted && !lobby.IsCancelled && snapshot.Count >= 1,
            RoundCountdownSeconds = roundCountdown,
            AcceleratedRoundCountdownSeconds = accelCountdown,
            BannedEncounterIds = bannedSnapshot,
            Rules = rulesSnapshot,
            Members = snapshot.Select(m => new
            {
                AccountId = m.AccountId,
                Username = m.Username,
                Hero = m.Hero,
                IsAi = m.IsAi,
                State = "accepted",
                PresenceExpiresAt = expires,
                AcceptedAt = (DateTime?)m.JoinedAt,
                LastHeartbeatAt = (DateTime?)m.LastSeenAt,
            }).ToArray(),
        };
    }

    private static object PublicDto(LobbyStore.Lobby lobby) => new
    {
        LobbyId = lobby.LobbyId,
        Code = lobby.Code,
        Name = lobby.Name,
        HostAccountId = lobby.HostAccountId,
        HostUsername = lobby.HostUsername,
        JoinedCount = lobby.Members.Count,
        PlayerCap = lobby.PlayerCap,
        MaxPlayers = lobby.PlayerCap,
        CreatedAt = lobby.CreatedAt,
        ExpiresAt = lobby.ExpiresAt,
    };

    private static RunState? FindAccountActiveOrRecentRun(
        string accountIdHex,
        SessionStore sessions,
        Guid? expectedTournamentSessionId = null)
    {
        var mem = sessions.GetByAccount(accountIdHex);
        if (mem == null) return null;

        // 严格隔离历史非锦标赛对局与其它场次的锦标赛对局，避免新建锦标赛房开赛前继承旧终局淘汰态
        if (expectedTournamentSessionId.HasValue && expectedTournamentSessionId.Value != Guid.Empty)
        {
            if (!mem.TournamentSessionId.HasValue || mem.TournamentSessionId.Value != expectedTournamentSessionId.Value)
                return null;
        }
        else if (mem.PlayMode != EPlayMode.Tournament && !mem.TournamentSessionId.HasValue)
        {
            return null;
        }

        return SessionRegistry.Get(mem.Sid)?.Run;
    }

    /// <summary>
    /// 依据当前玩家真实 RunState 与房间成员，生成严格对齐 ETournamentPhase 的锦标赛实时状态。
    /// </summary>
    private static object StatusFor(AccountStore.Account acc, LobbyStore lobbies, SessionStore sessions)
    {
        string aid = acc.AccountId.ToString("N");
        var lobby = lobbies.GetByMember(aid);
        Guid? expectedTid = lobby?.TournamentSessionId;
        var run = (lobby != null && (!lobby.HasStarted || !expectedTid.HasValue))
            ? null
            : FindAccountActiveOrRecentRun(aid, sessions, expectedTid);
        var selfMember = lobby != null ? lobbies.FindMember(lobby, aid) : null;

        bool started = lobby?.HasStarted ?? (run != null);
        var activeMem = sessions.GetByAccount(aid);
        Guid sessionId = lobby?.TournamentSessionId
                         ?? (activeMem != null && (activeMem.PlayMode == EPlayMode.Tournament || activeMem.TournamentSessionId.HasValue)
                             ? activeMem.TournamentSessionId
                             : null)
                         ?? Guid.Empty;

        uint currentDay = run?.Day ?? selfMember?.FinalDay ?? 1u;
        bool isChampion = false;
        bool isEliminated = false;
        bool isConceded = false;
        bool isRunEnded = false;

        if (run != null)
        {
            isChampion = run.State == ERunState.EndRunVictory;
            isEliminated = run.State == ERunState.EndRunDefeat;
            isRunEnded = Persistence.IsTerminalState(run.State);
            if (isRunEnded && selfMember != null && !selfMember.HasFinishedRun)
            {
                selfMember.HasFinishedRun = true;
                selfMember.FinalIsChampion = isChampion;
                selfMember.FinalIsEliminated = isEliminated;
                selfMember.FinalVictories = (int)run.Victories;
                selfMember.FinalDefeats = (int)run.Defeats;
                selfMember.FinalPrestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 0);
                selfMember.FinalDay = run.Day;
                lobbies.NotifyChanged();
            }
        }
        else if (selfMember != null && selfMember.HasFinishedRun)
        {
            isChampion = selfMember.FinalIsChampion;
            isEliminated = selfMember.FinalIsEliminated;
            isRunEnded = true;
        }

        // 仅当真正处于第 6 小时待打本日 PvP（或处于 TournamentPvpChoice/WaitingRoom）时才暴露 PvpReady
        bool isPvpReady = !isRunEnded && run != null &&
            (run.State == ERunState.TournamentPvpChoice ||
             run.State == ERunState.TournamentWaitingRoom ||
             (run.State == ERunState.Choice && run.Hour >= 6 && (run.Victories + run.Defeats) < run.Day));

        if (lobby != null && run != null)
        {
            lobbies.RecordRunProgress(lobby, aid, run);
        }

        var barrierDecision = (lobby != null && started && !isRunEnded)
            ? lobbies.EvaluateRoundBarrier(lobby, currentDay, aid, run, sessions, startOrAdvanceCountdown: true)
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
        else if (run == null)
            phase = "RunStarting";
        else
            phase = "DayFlow";

        var players = BuildPlayersSnapshot(lobby, acc, run, currentDay, sessions);
        var selfSnapshot = players.FirstOrDefault(p => p.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase));

        // 优先取 TournamentMatchmaker 为当前天分配的对称真实对手，未开赛或空房时回退兼容占位
        string assignedOppId = selfSnapshot?.AssignedOpponentAccountId ?? "";
        string assignedOppName = selfSnapshot?.AssignedOpponentUsername ?? "";
        if (string.IsNullOrWhiteSpace(assignedOppId))
        {
            var fallbackOpp = players
                .Where(p => !p.AccountId.Equals(aid, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.IsEliminated ? 1 : 0)
                .FirstOrDefault();
            assignedOppId = fallbackOpp?.AccountId ?? "";
            assignedOppName = fallbackOpp?.Username ?? "Tournament Challenger";
        }

        int? place = isChampion
            ? 1
            : (isEliminated ? (selfSnapshot?.Place ?? Math.Max(2, players.Count(p => !p.IsEliminated) + 1)) : selfSnapshot?.Place);

        int activePlayers = Math.Max(1, players.Count(p => !p.IsEliminated));
        int readyPlayers = barrierDecision.SurvivingHumans >= 2
            ? Math.Max(0, barrierDecision.ReadyHumans + players.Count(p => !p.IsEliminated && (lobby?.Members.Any(m => m.IsAi && m.AccountId.Equals(p.AccountId, StringComparison.OrdinalIgnoreCase)) ?? false)))
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
            CombatSubmitted = run != null && run.State == ERunState.PVPCombat,
            CanStartPvp = canStartPvp,
            WaitingForOtherPlayers = waitingForOthers,
            IsEliminated = isEliminated,
            IsChampion = isChampion,
            Place = place,
            Concede = isConceded,
            EndRun = isRunEnded,
            AssignedOpponentAccountId = assignedOppId,
            AssignedOpponentUsername = string.IsNullOrWhiteSpace(assignedOppName) ? "Tournament Challenger" : assignedOppName,
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
        public int Prestige { get; set; } = 20;
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
    }

    private static ObservedPlayerRow[] BuildPlayersSnapshot(
        LobbyStore.Lobby? lobby,
        AccountStore.Account? selfAcc,
        RunState? selfRun,
        uint currentDay,
        SessionStore sessions)
    {
        var list = new List<ObservedPlayerRow>();
        string? selfAid = selfAcc?.AccountId.ToString("N");

        if (lobby == null)
        {
            if (selfAcc != null)
            {
                list.Add(BuildHumanRow(selfAcc.AccountId.ToString("N"), selfAcc.Username, selfRun, null, currentDay));
            }
            return list.ToArray();
        }

        List<LobbyStore.Member> members;
        CustomRunRuleSet rules;
        lock (lobby.Members)
        {
            members = lobby.Members.ToList();
            rules = lobby.Rules;
        }

        bool nonLethalBots = rules.BotNonLethalPrestigeLock;
        var isAiById = new Dictionary<string, bool>(members.Count, StringComparer.OrdinalIgnoreCase);

        int aiIdx = 0;
        foreach (var m in members)
        {
            isAiById[m.AccountId] = m.IsAi;
            if (!m.IsAi)
            {
                var r = (selfAid != null && m.AccountId.Equals(selfAid, StringComparison.OrdinalIgnoreCase))
                    ? selfRun
                    : ((lobby.HasStarted && lobby.TournamentSessionId.HasValue)
                        ? FindAccountActiveOrRecentRun(m.AccountId, sessions, lobby.TournamentSessionId)
                        : null);
                list.Add(BuildHumanRow(m.AccountId, m.Username, r, m, currentDay));
            }
            else
            {
                list.Add(BuildAiRow(m, currentDay, aiIdx++, nonLethalBots));
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
            var assignments = lobby.Matchmaker.AssignRoundMatchups(currentDay, participants, rules);
            foreach (var row in list)
            {
                if (assignments.TryGetValue(row.AccountId, out var a))
                {
                    row.AssignedOpponentAccountId = a.OpponentAccountId;
                    row.AssignedOpponentUsername = a.OpponentUsername;
                }
            }
        }

        // 计算实时排名 Place：活跃名次按 Prestige/Wins 排前，被淘汰的按坚持天数/胜场排后
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
        RunState? r,
        LobbyStore.Member? m,
        uint fallbackDay)
    {
        if (r != null)
        {
            bool champ = r.State == ERunState.EndRunVictory;
            bool elim = r.State == ERunState.EndRunDefeat;
            return new ObservedPlayerRow
            {
                AccountId = accountId,
                Username = username,
                Hero = (r.Hero == EHero.Common ? EHero.Vanessa : r.Hero).ToString(),
                CurrentDay = r.Day,
                CurrentHour = r.Hour,
                Prestige = r.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20),
                HasVisitedFates = r.HasVisitedFates,
                Wins = (int)r.Victories,
                Losses = (int)r.Defeats,
                Phase = Persistence.IsTerminalState(r.State) ? "RunEnded" : "DayFlow",
                IsChampion = champ,
                IsEliminated = elim,
                CanObserve = !Persistence.IsTerminalState(r.State),
            };
        }
        return new ObservedPlayerRow
        {
            AccountId = accountId,
            Username = username,
            Hero = m?.Hero ?? "Vanessa",
            CurrentDay = m?.FinalDay ?? fallbackDay,
            CurrentHour = 1,
            Prestige = m?.FinalPrestige ?? 20,
            Wins = m?.FinalVictories ?? 0,
            Losses = m?.FinalDefeats ?? 0,
            Phase = m?.HasFinishedRun == true ? "RunEnded" : "DayFlow",
            IsChampion = m?.FinalIsChampion ?? false,
            IsEliminated = m?.FinalIsEliminated ?? false,
            CanObserve = false,
        };
    }

    private static ObservedPlayerRow BuildAiRow(
        LobbyStore.Member m,
        uint currentDay,
        int index,
        bool botNonLethalPrestigeLock)
    {
        int roundsPlayed = Math.Max(0, (int)currentDay - 1);
        int wins = 0;
        int losses = 0;
        int prestige = 20;
        for (int d = 1; d <= roundsPlayed; d++)
        {
            bool wonRound = ((d * 3 + index) % 5) < 3;
            if (wonRound) wins++;
            else
            {
                losses++;
                prestige = botNonLethalPrestigeLock
                    ? Math.Max(1, prestige - d)
                    : Math.Max(0, prestige - d);
            }
        }
        bool eliminated = !botNonLethalPrestigeLock && prestige <= 0;
        return new ObservedPlayerRow
        {
            AccountId = m.AccountId,
            Username = m.Username,
            Hero = m.Hero,
            CurrentDay = currentDay,
            CurrentHour = 6,
            Prestige = prestige,
            Wins = wins,
            Losses = losses,
            Phase = eliminated ? "RunEnded" : "DayFlow",
            IsEliminated = eliminated,
            CanObserve = false,
        };
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

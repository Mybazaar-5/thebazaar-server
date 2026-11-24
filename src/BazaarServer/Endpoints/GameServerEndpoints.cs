using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarCore.Models;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Commands;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.TempoNet;

namespace BazaarServer.Endpoints;

public static class GameServerEndpoints
{
    /// <summary>P0-21：按 runId 维度跨 sid 保留最近 8 条幂等命令指纹 (reqRid, commandSha256, responsePayload)。</summary>
    public const int MaxRidCacheEntriesPerRun = 8;
    private const int MaxRidCacheRuns = 1024;
    private const long OrphanRunRidCacheExpireMs = 60L * 60 * 1000;

    private sealed class CachedCommandResponse
    {
        public required byte[] CommandSha256 { get; init; }
        public required byte[] Payload { get; init; }
        public required long ResponseRid { get; init; }
    }

    private sealed class RunRidCache
    {
        private readonly object _sync = new();
        private readonly Dictionary<long, CachedCommandResponse> _entries = new();
        private readonly Queue<long> _order = new();
        public long LastAccessTick { get; private set; } = Environment.TickCount64;

        public bool TryGetMatch(long reqRid, ReadOnlySpan<byte> commandSha256, out CachedCommandResponse? cached)
        {
            lock (_sync)
            {
                LastAccessTick = Environment.TickCount64;
                if (_entries.TryGetValue(reqRid, out var entry) &&
                    CryptographicOperations.FixedTimeEquals(entry.CommandSha256, commandSha256))
                {
                    cached = entry;
                    return true;
                }
                cached = null;
                return false;
            }
        }

        public void Store(long reqRid, byte[] commandSha256, byte[] payload, long responseRid)
        {
            lock (_sync)
            {
                LastAccessTick = Environment.TickCount64;
                if (!_entries.ContainsKey(reqRid))
                {
                    while (_order.Count >= MaxRidCacheEntriesPerRun)
                    {
                        long oldest = _order.Dequeue();
                        _entries.Remove(oldest);
                    }
                    _order.Enqueue(reqRid);
                }
                _entries[reqRid] = new CachedCommandResponse
                {
                    CommandSha256 = commandSha256,
                    Payload = payload,
                    ResponseRid = responseRid,
                };
            }
        }
    }

    // P0-21：幂等指纹缓存按 runId 维度存储以跨 sid 存活；另维护 sid -> runId 索引与 ReestablishSession 后首包强同步标记
    private static readonly ConcurrentDictionary<string, RunRidCache> RunRidCaches = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> SidToRunId = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> PendingPostResumeSyncSids = new(StringComparer.Ordinal);
    // P1-37 & D8-P2-05：固定 128 条带账号会话互斥锁（无符号 32 位 FNV-1a 映射，严禁 Math.Abs(GetHashCode())，杜绝长跑无界字典泄漏）
    private const int AccountSessionLockStripeCount = 128;
    private static readonly SemaphoreSlim[] AccountSessionLockStripes = Enumerable.Range(0, AccountSessionLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly PropertyInfo? StartSelectionDoneProp =
        typeof(RunEngine).GetProperty(nameof(RunEngine.StartSelectionDone), BindingFlags.Public | BindingFlags.Instance);

    private static uint ComputeFnv1a32(string value)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < value.Length; i++)
        {
            char ch = char.ToLowerInvariant(value[i]);
            hash ^= (byte)(ch & 0xFF);
            hash *= 16777619u;
            if (ch > 0xFF)
            {
                hash ^= (byte)(ch >> 8);
                hash *= 16777619u;
            }
        }
        return hash;
    }

    /// <summary>P1-37 &amp; D8-P2-05：获取指定账号的会话生命周期条带互斥锁（加锁顺序恒为 AccountLock → Session.Gate）。</summary>
    public static SemaphoreSlim GetAccountSessionLock(string accountIdKey)
    {
        string norm = string.IsNullOrWhiteSpace(accountIdKey) ? "_anonymous" : accountIdKey.Trim();
        uint stripe = ComputeFnv1a32(norm) % (uint)AccountSessionLockStripes.Length;
        return AccountSessionLockStripes[stripe];
    }

    /// <summary>
    /// 移除指定会话 sid 的绑定与首包标记，但保留 runId 维度的幂等缓存以支撑 410 / ReestablishSession 跨 sid 重放。
    /// </summary>
    public static void DetachRidCache(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return;
        SidToRunId.TryRemove(sid, out _);
        PendingPostResumeSyncSids.TryRemove(sid, out _);
    }

    /// <summary>显式释放指定对局 runId 的幂等缓存（用于投降/终局销毁或测试清理）。</summary>
    public static void DetachRunRidCache(string? runId)
    {
        if (!string.IsNullOrEmpty(runId))
            RunRidCaches.TryRemove(runId, out _);
    }

    /// <summary>标记某会话刚刚完成 /sessions 恢复（ReestablishSession），其首次命令拒单需强制附带 GameStateSync。</summary>
    public static void MarkSessionReestablished(string sid, string runId)
    {
        if (!string.IsNullOrEmpty(sid))
        {
            if (!string.IsNullOrEmpty(runId))
                SidToRunId[sid] = runId;
            PendingPostResumeSyncSids[sid] = 1;
        }
    }

    /// <summary>消费并清除某会话的 ReestablishSession 后首次命令标记。</summary>
    public static bool ConsumeSessionReestablished(string sid)
        => !string.IsNullOrEmpty(sid) && PendingPostResumeSyncSids.TryRemove(sid, out _);

    /// <summary>按 runId 查询 (reqRid, commandSha256) 幂等命中记录（跨 sid 生效）。</summary>
    public static bool TryReplayIdempotentCommand(
        string runId,
        long reqRid,
        ReadOnlySpan<byte> commandSha256,
        out byte[]? payload,
        out long responseRid)
    {
        if (!string.IsNullOrEmpty(runId) &&
            RunRidCaches.TryGetValue(runId, out var cache) &&
            cache.TryGetMatch(reqRid, commandSha256, out var cached) &&
            cached != null)
        {
            payload = cached.Payload;
            responseRid = cached.ResponseRid;
            return true;
        }
        payload = null;
        responseRid = 0;
        return false;
    }

    /// <summary>按 runId 记录最近 8 条命令响应的幂等指纹。</summary>
    public static void RecordIdempotentResponse(
        string runId,
        long reqRid,
        byte[] commandSha256,
        byte[] payload,
        long responseRid)
    {
        if (string.IsNullOrEmpty(runId)) return;
        var cache = RunRidCaches.GetOrAdd(runId, _ => new RunRidCache());
        cache.Store(reqRid, commandSha256, payload, responseRid);
        EnforceRidCacheCapacity();
    }

    /// <summary>按活跃会话存在性批量清理孤儿 sid 映射及长期失活的 runId 幂等缓存。</summary>
    public static int PurgeOrphanRidCaches(Func<string, bool> exists)
    {
        int removed = 0;
        var activeRunIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in SidToRunId)
        {
            if (!exists(kv.Key))
            {
                if (SidToRunId.TryRemove(kv.Key, out _))
                    removed++;
                PendingPostResumeSyncSids.TryRemove(kv.Key, out _);
            }
            else if (!string.IsNullOrEmpty(kv.Value))
            {
                activeRunIds.Add(kv.Value);
            }
        }
        foreach (string sid in PendingPostResumeSyncSids.Keys)
        {
            if (!exists(sid))
                PendingPostResumeSyncSids.TryRemove(sid, out _);
        }

        long now = Environment.TickCount64;
        foreach (var kv in RunRidCaches)
        {
            if (!activeRunIds.Contains(kv.Key) &&
                now - kv.Value.LastAccessTick >= OrphanRunRidCacheExpireMs &&
                RunRidCaches.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }
        return removed;
    }

    private static void EnforceRidCacheCapacity()
    {
        if (RunRidCaches.Count <= MaxRidCacheRuns) return;
        int over = RunRidCaches.Count - (MaxRidCacheRuns * 3 / 4);
        var victims = RunRidCaches
            .OrderBy(kv => kv.Value.LastAccessTick)
            .Take(Math.Max(1, over))
            .Select(kv => kv.Key)
            .ToList();
        foreach (string runId in victims)
            RunRidCaches.TryRemove(runId, out _);
    }

    /// <summary>
    /// P0-17 &amp; P0-21：判断命令拒单是否需要在响应末尾附加 NetMessageGameStateSync 强制对齐客户端乐观盘面。
    /// 战斗态（Combat/PVPCombat）与终局态（EndRun*/Shutdown）绝不附加 GameStateSync。
    /// </summary>
    public static bool ShouldAttachStateSyncOnCommandError(
        INetCommand cmd,
        ERunState currentState,
        bool isFirstAfterReestablish = false)
    {
        if (currentState is ERunState.Combat or ERunState.PVPCombat
            or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown)
        {
            return false;
        }

        if (isFirstAfterReestablish)
            return true;

        return cmd is SelectItemCommand
            or MoveItemCommand
            or SellCardCommand
            or SelectSkillCommand
            or CommitToPedestalCommand
            or SelectEncounterCommand
            or ExitCurrentStateCommand
            or RerollCommand;
    }

    /// <summary>
    /// 统一构造 /commands 下行消息：
    /// - 拒单时对可触发乐观变更的命令或 ReestablishSession 首包过期拒单包装 [Error, GameStateSync]（GameStateSync 居尾且后无 GameSim）；
    /// - 正常响应透传 ExtraSimCards（P0-19 技能融合源卡）；
    /// - 降级落位（AppendStateSyncAtTail）返回 [GameSim, GameStateSync]（GameStateSync 居尾不吞 GameSim）。
    /// </summary>
    public static INetMessage BuildCommandReply(
        INetCommand cmd,
        RunState run,
        RunEngine.Outcome outcome,
        ERunState stateBefore,
        bool isOwnedSelectMove,
        bool isFirstAfterReestablish,
        out bool isPureBoardMove,
        out bool isCombatSequence)
    {
        isCombatSequence = false;

        if (outcome.Error != null)
        {
            isPureBoardMove = false;
            if (ShouldAttachStateSyncOnCommandError(cmd, run.State, isFirstAfterReestablish))
            {
                return new NetMessageAggregate(new List<INetMessage>
                {
                    outcome.Error,
                    new NetMessageGameStateSync(run.BuildSnapshot()),
                });
            }
            return outcome.Error;
        }

        if (outcome.CombatSpawnSim != null && outcome.Combat != null && outcome.CombatDespawnSim != null)
        {
            isPureBoardMove = false;
            isCombatSequence = true;
            return new NetMessageAggregate(new List<INetMessage>
            {
                new NetMessageGameSim(outcome.CombatSpawnSim),
                new NetMessageCombatSim(outcome.Combat),
                new NetMessageGameSim(outcome.CombatDespawnSim),
            });
        }

        bool hasStructuralEvent = outcome.Events.Any(e =>
            e is GameSimEventCardSpawned
            or GameSimEventCardDisposed
            or GameSimEventCardDealt
            or GameSimEventCardTransformed
            or GameSimEventCardTransformReverted
            or GameSimEventBoardOverridden
            or GameSimEventStateTransitioned
            or GameSimEventSocketsUnlocked
            or GameSimEventPlayerSkillEquipped);

        isPureBoardMove = (cmd is MoveItemCommand || isOwnedSelectMove) &&
            run.State == stateBefore &&
            !Persistence.IsTerminalState(run.State) &&
            outcome.CombatSpawnSim == null &&
            outcome.Combat == null &&
            !outcome.ForceStateSync &&
            !outcome.AppendStateSyncAtTail &&
            !hasStructuralEvent;

        if (isPureBoardMove)
        {
            // V13.1 拖拽加速（Top 7）：MoveItem 返回增量 GameSim（仅打包被移动/挤位/光环变更卡），削减 80%+ 包体与客户端刷新开销
            return new NetMessageAggregate(new List<INetMessage>
            {
                new NetMessageGameSim(run.BuildDeltaGameSim(outcome.Events, outcome.DirtyCardIds)),
            });
        }

        if (outcome.PreSplitGameSim != null && outcome.SecondaryGameSimEvents is { Count: > 0 })
        {
            isPureBoardMove = false;
            // P2-32：存在 Pedestal NextEncounterOnExit 跳往 Encounter / Pedestal 时，拆分为两条独立 NetMessageGameSim，
            // 避免客户端 PedestalState 在同一条 GameSim 中吞并 StateTransitioned 并强清 CurrentEncounterId。
            var splitList = new List<INetMessage>
            {
                new NetMessageGameSim(outcome.PreSplitGameSim),
                new NetMessageGameSim(run.BuildFullGameSim(outcome.SecondaryGameSimEvents)),
            };
            if (outcome.Combat != null) splitList.Add(new NetMessageCombatSim(outcome.Combat));
            if ((outcome.ForceStateSync || outcome.AppendStateSyncAtTail) &&
                run.State is not (ERunState.Combat or ERunState.PVPCombat or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
            {
                splitList.Add(new NetMessageGameStateSync(run.BuildSnapshot()));
            }
            return new NetMessageAggregate(splitList);
        }

        var extraCards = outcome.ExtraSimCards.Count > 0 ? outcome.ExtraSimCards : null;
        var list = new List<INetMessage>
        {
            new NetMessageGameSim(run.BuildFullGameSim(outcome.Events, extraCards)),
        };
        if (outcome.Combat != null) list.Add(new NetMessageCombatSim(outcome.Combat));
        if ((outcome.ForceStateSync || outcome.AppendStateSyncAtTail) &&
            run.State is not (ERunState.Combat or ERunState.PVPCombat or ERunState.EndRunVictory or ERunState.EndRunDefeat or ERunState.Shutdown))
        {
            // P1-26：与官方客户端 NetMessageAggregate 串行顺序一致，GameStateSync 必须位于 GameSim 之后，
            // 避免先全量覆盖再播增量事件导致二次触发，同时对 ForceStateSync / AppendStateSyncAtTail 自动去重
            list.Add(new NetMessageGameStateSync(run.BuildSnapshot()));
        }
        return new NetMessageAggregate(list);
    }

    public static void Map(WebApplication app)
    {
        // D2-P2-04：不在启动期闭包捕获 CardArchetypeIndex，每次 /sessions 按需经 RequestServices 获取最新热重载快照
        var playerRules = app.Services.GetService<PlayerRuleStore>();
        var lobbies = app.Services.GetService<BazaarServer.Tournament.LobbyStore>();
        var diSessions = app.Services.GetService<SessionStore>();
        var pvpCoordinator = app.Services.GetService<ITournamentPvpCoordinator>();
        var socialSink = app.Services.GetService<ISocialAndReplaySink>();
        var spectateRecorder = app.Services.GetService<ISpectateFrameRecorder>();
        var sseHub = app.Services.GetService<BazaarServer.Tournament.SseEventHub>();
        var modHost = app.Services.GetService<ServerModHost>();

        // 自动协同注入 Persistence，在服务启动映射路由时先清理历史超血旧脏标准镜像，再热加载 SQLite 真人 PvP 幽灵镜像池
        try
        {
            var startupDb = app.Services.GetRequiredService<Persistence>();
            var startupGhosts = app.Services.GetRequiredService<PvpGhostPool>();
            startupDb.PurgeCorruptedPvpGhosts();
            startupGhosts.LoadFromDb(startupDb);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[GameServerEndpoints] PvpGhostPool.LoadFromDb failed: " + ex.Message);
        }

        // 进程优雅退出时冲刷 Persistence 防抖队列，保证零状态丢失
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                var stopDb = app.Services.GetRequiredService<Persistence>();
                stopDb.FlushAllPending();
            }
            catch { }
        });

        // ── /sessions ────────────────────────────────────────────────────────
        app.MapPost("/sessions", async (
            HttpContext ctx,
            SessionStore sessions,
            AccountStore accounts,
            Persistence db,
            CardPool pool,
            List<BazaarServer.Data.MonsterTemplate> monsters,
            LevelUpTable levelUps,
            GameModeConfig mode,
            BazaarCore.Data.GameDataRepository fallbackRepo,
            PvpGhostPool ghosts,
            ILogger<Program> log) =>
        {
            byte[] body = await ReadAll(ctx);
            INetCommand cmd;
            try { cmd = Codec.ReadCommand(body); }
            catch (Exception ex)
            {
                log.LogError(ex, "/sessions: 解码命令失败 ({n} bytes)", body.Length);
                ctx.Response.StatusCode = 400;
                return;
            }

            if (cmd is not InitializeRunCommand init)
            {
                log.LogWarning("/sessions: 预期 InitializeRunCommand，实际收到 {t}", cmd.GetType().Name);
                ctx.Response.StatusCode = 400;
                return;
            }

            var account = ResolveAccount(ctx, accounts);
            string accountIdKey = account.AccountId.ToString("N");
            var archetypeIndex = ctx.RequestServices.GetService<CardArchetypeIndex>();
            var (desiredRules, hasAuthoritativeLobby) = ResolveDesiredRulesForAccount(accountIdKey, init, playerRules, lobbies);
            var runModCtx = modHost?.ResolveRunModContext(desiredRules.SelectedModIds, pool, monsters, archetypeIndex);
            var activeRepo = runModCtx?.Repository ?? modHost?.ActiveRepository ?? fallbackRepo;
            var runPool = runModCtx?.Pool ?? pool;
            var runMonsters = runModCtx?.Monsters ?? monsters;
            var runArchetypeIndex = runModCtx?.ArchetypeIndex ?? archetypeIndex;
            var challengeService = app.Services.GetService<ChallengeService>() ?? TempoNetEndpoints.SharedChallengeService;

            // P1-37：账号级互斥锁串行化 /sessions 与 /runs/active/concede（加锁顺序恒为 AccountLock → Session.Gate）
            var accountLock = GetAccountSessionLock(accountIdKey);
            await accountLock.WaitAsync(ctx.RequestAborted);
            try
            {
                bool initIsTourney = init.PlayMode == EPlayMode.Tournament
                    || (init.TournamentSessionId.HasValue && init.TournamentSessionId.Value != Guid.Empty);
                Guid tourneyTid = init.TournamentSessionId ?? Guid.Empty;

                // P0-15 / P0-42：锦标赛请求先短持 L5a 采集只读成员权威快照（严格 L1 -> L5a 锁序）
                var tourneyLobby = (initIsTourney && tourneyTid != Guid.Empty)
                    ? lobbies?.FindLobbyForRun(tourneyTid, accountIdKey)
                    : null;
                var tourneyMember = tourneyLobby != null
                    ? lobbies!.TryGetMemberSnapshot(tourneyLobby, accountIdKey)
                    : null;
                bool memberFinishedTourney = tourneyMember is { HasFinishedRun: true };
                bool tournamentRunAlreadyFinished = memberFinishedTourney;
                Persistence.RunRow? endedTourneyRow = null;

                // 1. 优先检查内存中或 SQLite 是否存在该账号的未完结活跃对局（Resume Run / 410 重连恢复，严格防串局）
                RunState? resumedRun = null;
                bool? resumedStarterDone = null;
                ServerModHost.RunModContext? resumedModCtx = null;

                var existingSession = sessions.GetByAccount(accountIdKey);
                if (existingSession != null)
                {
                    await existingSession.Gate.WaitAsync(ctx.RequestAborted);
                    try
                    {
                        var existingHandle = SessionRegistry.Get(existingSession.Sid);
                        if (existingHandle != null)
                        {
                            // 新会话创建/恢复前先冲刷该局挂起防抖写入任务，保证内存与 SQLite 完全同步
                            try { db.FlushRunImmediate(existingHandle.Run.RunId); } catch { }
                            bool sameTourneyAsMem = initIsTourney && tourneyTid != Guid.Empty
                                && existingHandle.Run.TournamentSessionId == tourneyTid;

                            if (sameTourneyAsMem && memberFinishedTourney)
                            {
                                // 该锦标赛成员已被 AFK 淘汰/完赛：严禁从内存热会话复活，幂等落盘终局并标记阻断
                                tournamentRunAlreadyFinished = true;
                                try
                                {
                                    var endState = tourneyMember!.FinalIsChampion ? ERunState.EndRunVictory : ERunState.EndRunDefeat;
                                    string reason = string.IsNullOrWhiteSpace(tourneyMember.RemovalReason) ? "afk_eliminated" : tourneyMember.RemovalReason;
                                    db.MarkRunEnded(existingHandle.Run.RunId, endState, reason);
                                }
                                catch { }
                                DetachRunRidCache(existingHandle.Run.RunId);
                            }
                            else if (!Persistence.IsTerminalState(existingHandle.Run.State) &&
                                CanResumeRunForInit(existingHandle.Run, init, desiredRules, hasAuthoritativeLobby))
                            {
                                resumedRun = existingHandle.Run;
                                resumedStarterDone = existingHandle.Engine.StartSelectionDone;
                            }
                            else if (Persistence.IsTerminalState(existingHandle.Run.State))
                            {
                                if (sameTourneyAsMem)
                                    tournamentRunAlreadyFinished = true;
                                DetachRunRidCache(existingHandle.Run.RunId);
                            }
                        }
                        SessionRegistry.Detach(existingSession.Sid);
                        DetachRidCache(existingSession.Sid);
                        if (tournamentRunAlreadyFinished)
                            sessions.Remove(existingSession.Sid);
                    }
                    finally
                    {
                        existingSession.Gate.Release();
                    }
                }

                if (resumedRun == null)
                {
                    // P0-15：锦标赛强制路由 LoadLatestTournamentRun，绝不落入 LoadLatestActiveRun 拉错单人局！
                    var savedRow = (initIsTourney && tourneyTid != Guid.Empty)
                        ? db.LoadLatestTournamentRun(account.AccountId, tourneyTid)
                        : db.LoadLatestActiveRun(account.AccountId);

                    if (initIsTourney && tourneyTid != Guid.Empty && savedRow != null)
                    {
                        bool dbRowIsEnded = savedRow.IsEnded || Persistence.IsTerminalState(savedRow.State);
                        if (dbRowIsEnded || memberFinishedTourney)
                        {
                            tournamentRunAlreadyFinished = true;
                            endedTourneyRow = savedRow;
                            if (!savedRow.IsEnded)
                            {
                                try
                                {
                                    var endState = tourneyMember?.FinalIsChampion == true ? ERunState.EndRunVictory : ERunState.EndRunDefeat;
                                    string reason = string.IsNullOrWhiteSpace(tourneyMember?.RemovalReason) ? "afk_eliminated" : tourneyMember!.RemovalReason;
                                    db.MarkRunEnded(savedRow.RunId, endState, reason);
                                }
                                catch { }
                            }
                            savedRow = null;
                        }
                    }

                    // P0-15 两阶段反序列化：阶段 1 轻量只读头解析校验 -> 阶段 2 按快照自身 SelectedModIds 解析权威 Mod 上下文后再重建 CoreCard！
                    if (savedRow != null &&
                        RunSnapshotSerializer.TryReadHeader(savedRow.Snapshot, savedRow.RulesJson, out var header) &&
                        header != null &&
                        !Persistence.IsTerminalState(header.State) &&
                        CanResumeHeaderForInit(header, init, desiredRules, hasAuthoritativeLobby))
                    {
                        resumedModCtx = modHost?.ResolveRunModContext(
                            header.CustomRules?.SelectedModIds, pool, monsters, archetypeIndex);
                        var resumedRepo = resumedModCtx?.Repository ?? modHost?.ActiveRepository ?? fallbackRepo;

                        if (RunSnapshotSerializer.TryDeserialize(
                                savedRow.Snapshot,
                                resumedRepo,
                                out var restored,
                                out var snapDto,
                                extraMutators: resumedModCtx?.CustomMutatorsById ?? modHost?.Registry.RuntimeContext.EffectiveCustomMutatorsById) &&
                            restored != null &&
                            !Persistence.IsTerminalState(restored.State) &&
                            CanResumeRunForInit(restored, init, desiredRules, hasAuthoritativeLobby))
                        {
                            resumedRun = restored;
                            resumedStarterDone = snapDto?.StartSelectionDone ?? header.StartSelectionDone;
                        }
                    }
                }

                // P0-15 × P0-42：在开启全新对局前硬拦截同场已终结/已 AFK 淘汰锦标赛成员，返回 HTTP 409 Conflict 绝不开 Day 1 僵尸局！
                if (resumedRun == null && initIsTourney && tourneyTid != Guid.Empty && tournamentRunAlreadyFinished)
                {
                    bool isChampion = tourneyMember?.FinalIsChampion ?? (endedTourneyRow?.State == ERunState.EndRunVictory);
                    bool isEliminated = tourneyMember?.FinalIsEliminated ?? !isChampion;
                    string reason = !string.IsNullOrWhiteSpace(tourneyMember?.RemovalReason)
                        ? tourneyMember!.RemovalReason
                        : (!string.IsNullOrWhiteSpace(endedTourneyRow?.EndedReason) ? endedTourneyRow!.EndedReason! : (isChampion ? "champion" : "ended"));

                    log.LogInformation(
                        "/sessions [BLOCKED_FINISHED_TOURNAMENT]: account={acc} tid={tid} reason={reason} elim={elim} champ={champ}",
                        account.Username, tourneyTid, reason, isEliminated, isChampion);

                    ctx.Response.StatusCode = StatusCodes.Status409Conflict;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    await ctx.Response.WriteAsync(JsonSerializer.Serialize(new
                    {
                        error = "tournament_run_finished",
                        tournamentSessionId = tourneyTid,
                        reason,
                        isEliminated,
                        isChampion,
                    }, JsonOpts), ctx.RequestAborted);
                    return;
                }

                var session = sessions.Create(accountIdKey, account.Username, init);

                if (resumedRun != null)
                {
                    if (string.IsNullOrWhiteSpace(resumedRun.AccountId))
                        resumedRun.AccountId = accountIdKey;
                    if (string.IsNullOrWhiteSpace(resumedRun.Username))
                        resumedRun.Username = account.Username;
                    if (!resumedRun.TournamentSessionId.HasValue && init.TournamentSessionId.HasValue)
                        resumedRun.TournamentSessionId = init.TournamentSessionId;

                    // P0-42 重连端二次保险：若锦标赛大厅托管已将成员进度向前推进，先对齐 resumedRun 再构造 RunEngine 防止倒流
                    if (tourneyMember != null && tourneyMember.CurrentDay > resumedRun.Day && !Persistence.IsTerminalState(resumedRun.State))
                    {
                        resumedRun.Day = tourneyMember.CurrentDay;
                        resumedRun.Hour = 1;
                        resumedRun.State = ERunState.Choice;
                        resumedRun.CurrentEncounterId = null;
                        resumedRun.ActiveEncounterCardInstanceId = null;
                        resumedRun.SelectionSet.Clear();
                        resumedRun.SuspendedStates.Clear();
                        resumedRun.Victories = Math.Max(resumedRun.Victories, (uint)Math.Max(0, tourneyMember.FinalVictories));
                        resumedRun.Defeats = Math.Max(resumedRun.Defeats, (uint)Math.Max(0, tourneyMember.FinalDefeats));
                        uint authCompletedDay = tourneyMember.LastCompletedPvpDay > 0
                            ? tourneyMember.LastCompletedPvpDay
                            : (tourneyMember.CurrentDay > 1 ? tourneyMember.CurrentDay - 1 : 0u);
                        resumedRun.LastCompletedPvpDay = Math.Max(resumedRun.LastCompletedPvpDay, authCompletedDay);
                        int curPrestige = resumedRun.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, tourneyMember.FinalPrestige);
                        resumedRun.PlayerAttributes[EPlayerAttributeType.Prestige] = Math.Max(0, Math.Min(curPrestige, tourneyMember.FinalPrestige));
                    }

                    bool starterDone = resumedStarterDone ?? resumedRun.StartSelectionDone;
                    resumedRun.StartSelectionDone = starterDone;

                    resumedModCtx ??= modHost?.ResolveRunModContext(resumedRun.CustomRules?.SelectedModIds, pool, monsters, archetypeIndex);
                    var resumedEngine = new RunEngine(
                        resumedRun,
                        resumedModCtx?.Pool ?? runPool,
                        resumedModCtx?.Monsters ?? runMonsters,
                        levelUps, mode,
                        resumedModCtx?.Repository ?? activeRepo,
                        ghosts,
                        resumedModCtx?.ArchetypeIndex ?? runArchetypeIndex,
                        playerRules, lobbies, diSessions ?? sessions,
                        pvpCoordinator, socialSink, accounts, challengeService, db,
                        resumedModCtx?.RuntimeContext ?? modHost?.Registry.RuntimeContext,
                        modHost: modHost,
                        runVisualBindings: resumedModCtx?.VisualBindingsByCustomId,
                        runModContext: resumedModCtx);
                    if (starterDone)
                    {
                        StartSelectionDoneProp?.SetValue(resumedEngine, true);
                    }

                    resumedRun.PublishPublicSummary();
                    SessionRegistry.Attach(session.Sid, resumedRun, resumedEngine);
                    MarkSessionReestablished(session.Sid, resumedRun.RunId);
                    try { SaveSnapshotImmediate(db, resumedRun, session, resumedEngine); } catch { }
                    try
                    {
                        var tLobby = lobbies?.FindLobbyForRun(resumedRun.TournamentSessionId, accountIdKey);
                        if (tLobby != null)
                            lobbies!.RecordRunProgress(tLobby, accountIdKey, resumedRun);
                    }
                    catch { }

                    // D2-P1-04：紧前下发的 NetMessageGameStateSync 已携权威 Board = resumedRun.CurrentBoardId，跳过重复 BoardOverridden 避免撞客户端锁
                    var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(resumedRun, stateSyncAlreadyCarriesBoard: true);
                    var resumeMsgs = new List<INetMessage>
                    {
                        resumedRun.BuildRunInitialized(),
                        new NetMessageGameStateSync(resumedRun.BuildSnapshot()),
                        new NetMessageGameSim(resumedRun.BuildFullGameSim(resumeEvents)),
                    };

                    log.LogInformation(
                        "/sessions [RESUME]: sid={sid} runId={runId} account={acc} hero={hero} mode={spMode} day={day} hour={hour} state={state} cards={cards}",
                        session.Sid, resumedRun.RunId, account.Username, resumedRun.Hero,
                        resumedRun.CustomRules.SpecialModeId, resumedRun.Day, resumedRun.Hour, resumedRun.State, resumedRun.Cards.Count);

                    byte[] resumePayload = await WriteMessageAndReturnPayload(
                        ctx, new NetMessageAggregate(resumeMsgs), session, resumedRun, modHost);
                    try
                    {
                        var resumeSseHub = ctx.RequestServices?.GetService<BazaarServer.Tournament.SseEventHub>();
                        if (ShouldRecordSpectate(spectateRecorder, resumeSseHub, accountIdKey))
                        {
                            spectateRecorder!.RecordBootstrapSnapshot(
                                accountIdKey, resumedRun.RunId, resumedRun.TournamentSessionId, session.RequestId, resumePayload);
                        }
                    }
                    catch { }
                    return;
                }

                // 2. 无可复用的活跃对局 → 开启全新对局
                log.LogInformation("/sessions [NEW]: sid={sid} account={acc} hero={hero} mode={mode} special={spMode} tournament={tid}",
                    session.Sid, account.Username, init.SelectedHero, init.PlayMode, desiredRules.SpecialModeId, init.TournamentSessionId);

                var run = new RunState
                {
                    RunId = session.RunId,
                    GameModeId = init.GameModeId ?? pool.DefaultGameModeId,
                    Hero = init.SelectedHero,
                    PlayMode = init.PlayMode,
                    AccountId = accountIdKey,
                    Username = account.Username,
                    TournamentSessionId = init.TournamentSessionId,
                    CustomRules = desiredRules.CloneNormalized(runModCtx?.CustomMutatorsById ?? modHost?.Registry.RuntimeContext.EffectiveCustomMutatorsById),
                };
                var engine = new RunEngine(
                    run, runPool, runMonsters, levelUps, mode, activeRepo, ghosts,
                    runArchetypeIndex, playerRules, lobbies, diSessions ?? sessions,
                    pvpCoordinator, socialSink, accounts, challengeService, db,
                    runModCtx?.RuntimeContext ?? modHost?.Registry.RuntimeContext,
                    modHost: modHost,
                    runVisualBindings: runModCtx?.VisualBindingsByCustomId,
                    runModContext: runModCtx);

                // 开局：发起始技能三选一 / Day 1 Hour 1 遭遇
                var startOutcome = engine.StartRun();

                // 首次响应：RunInitialized + 全量快照 + 首个 GameSim（含开局事件）
                var msgs = new List<INetMessage>
                {
                    run.BuildRunInitialized(),
                    new NetMessageGameStateSync(run.BuildSnapshot()),
                    new NetMessageGameSim(run.BuildFullGameSim(startOutcome.Events)),
                };

                SessionRegistry.Attach(session.Sid, run, engine);
                SidToRunId[session.Sid] = run.RunId;
                try { SaveSnapshotImmediate(db, run, session, engine); } catch { }
                try
                {
                    var tLobby = lobbies?.FindLobbyForRun(run.TournamentSessionId, accountIdKey);
                    if (tLobby != null)
                        lobbies!.RecordRunProgress(tLobby, accountIdKey, run);
                }
                catch { }

                byte[] startPayload = await WriteMessageAndReturnPayload(
                    ctx, new NetMessageAggregate(msgs), session, run, modHost);
                try
                {
                    var startSseHub = ctx.RequestServices?.GetService<BazaarServer.Tournament.SseEventHub>();
                    if (ShouldRecordSpectate(spectateRecorder, startSseHub, accountIdKey))
                    {
                        spectateRecorder!.RecordBootstrapSnapshot(
                            accountIdKey, run.RunId, run.TournamentSessionId, session.RequestId, startPayload);
                    }
                }
                catch { }
            }
            finally
            {
                accountLock.Release();
            }
        });

        // ── /commands ────────────────────────────────────────────────────────
        app.MapPost("/commands", async (
            HttpContext ctx,
            SessionStore sessions,
            AccountStore accounts,
            Persistence db,
            PvpGhostPool ghosts,
            ILogger<Program> log) =>
        {
            string sid = ctx.Request.Headers["sid"].ToString();
            var session = sessions.Get(sid);
            if (session == null)
            {
                ctx.Response.StatusCode = 410; // Gone → 客户端会用最后一次 InitializeRunCommand 重建会话
                return;
            }

            byte[] body = await ReadAll(ctx);
            INetCommand cmd;
            try { cmd = Codec.ReadCommand(body); }
            catch (Exception ex)
            {
                log.LogError(ex, "/commands: 解码失败 sid={sid} ({n} bytes)", sid, body.Length);
                ctx.Response.StatusCode = 400;
                return;
            }

            string rawRid = ctx.Request.Headers["rid"].ToString();
            bool hasReqRid = long.TryParse(rawRid, out long reqRid) && reqRid >= 0;
            byte[] commandSha256 = SHA256.HashData(body);

            await session.Gate.WaitAsync(ctx.RequestAborted);
            try
            {
                var handle = SessionRegistry.Get(sid);
                if (handle == null)
                {
                    ctx.Response.StatusCode = 410;
                    return;
                }

                string runId = handle.Run.RunId;
                SidToRunId[sid] = runId;
                bool isFirstAfterReestablish = ConsumeSessionReestablished(sid);

                if (hasReqRid &&
                    TryReplayIdempotentCommand(runId, reqRid, commandSha256, out var cachedPayload, out long cachedRespRid) &&
                    cachedPayload != null)
                {
                    session.LastCommandAt = DateTime.UtcNow;
                    session.RequestId = Math.Max(session.RequestId, cachedRespRid);
                    log.LogInformation("/commands [RID-REPLAY]: sid={sid} runId={runId} reqRid={reqRid} respRid={respRid} cmd={cmd}",
                        sid, runId, reqRid, cachedRespRid, cmd.GetType().Name);
                    await WriteSerializedPayload(ctx, cachedPayload, session.Sid, cachedRespRid, handle.Run, modHost);
                    return;
                }

                long prevRid = session.RequestId;
                // P2-35：强制单调递增 session.RequestId，防止乱序旧 reqRid 导致 rid 回退
                long rid = hasReqRid
                    ? Math.Max(session.RequestId + 1, reqRid + 1)
                    : session.RequestId + 1;
                session.RequestId = rid;
                session.LastCommandAt = DateTime.UtcNow;

                int goldBefore = handle.Run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
                var stateBefore = handle.Run.State;
                bool wasTerminalBefore = Persistence.IsTerminalState(handle.Run.State);
                bool isOwnedSelectMove = cmd is SelectItemCommand selCmd &&
                    !string.IsNullOrEmpty(selCmd.InstanceId.Value) &&
                    handle.Run.Cards.TryGetValue(selCmd.InstanceId.Value, out var targetEntry) &&
                    targetEntry.Owner == ECombatantId.Player;

                RunEngine.Outcome outcome;
                if (cmd is CheatCommand cheatCmd && IsHttpCheatBlocked(handle.Run, cheatCmd))
                {
                    outcome = new RunEngine.Outcome
                    {
                        Error = new NetMessageError(
                            $"cmd_{rid}_cheat_blocked",
                            EResponseErrorType.NotAllowed,
                            "当前模式（锦标赛/每日挑战或服务器配置）已禁用调试指令"),
                    };
                }
                else if (cmd is AbandonRunCommand && wasTerminalBefore)
                {
                    outcome = new RunEngine.Outcome
                    {
                        Error = new NetMessageError(
                            $"cmd_{rid}_abandon_terminal",
                            EResponseErrorType.NotAllowed,
                            $"对局已处于终局状态（{handle.Run.State}），无法重复放弃"),
                    };
                }
                else
                {
                    outcome = handle.Engine.Handle(cmd);
                }

                // 仅保留最近 32 条轻量命令摘要，彻底消除全量 GameSim JSON 序列化造成的 LOH 与内存泄漏
                AppendBoundedCommandLog(session, rid, cmd, handle.Run, outcome.Error == null);

                if (outcome.Error == null)
                {
                    var acc = accounts.FindByAccountId(session.AccountId) ?? ResolveAccount(ctx, accounts);
                    bool isSandbox = handle.Run.IsSandboxRun || handle.Run.CustomRules.ShouldSkipGhostPool;

                    // Hour 6 PvP 战斗发生时，通过 P0-30 单一统一入口解析分区写入 SQLite pvp_ghosts（严格执行沙盒/自定义/Mod 隔离，绝不污染标准池）
                    if (!isSandbox &&
                        outcome.CombatSpawnSim != null &&
                        outcome.CombatSpawnSim.CurrentState?.StateName == ERunState.PVPCombat)
                    {
                        bool hasTrueCustomCard = PvpGhostPool.HasTrueCustomCard(handle.Run);
                        string partition = PvpGhostPool.ResolveGhostPoolPartitionLocal(
                            handle.Run.CustomRules,
                            hasTrueCustomCard,
                            modHost);
                        bool isNonStandardPartition = !string.Equals(partition, PvpGhostPool.StandardPartition, StringComparison.OrdinalIgnoreCase);

                        int curHpMax = handle.Run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, BazaarCore.Runtime.GameDefaults.StartingHealth);
                        if (!string.Equals(partition, "none", StringComparison.OrdinalIgnoreCase) &&
                            (handle.Run.IsCustomRuleRun || isNonStandardPartition || Persistence.IsSaneGhostForDay((int)handle.Run.Day, curHpMax)))
                        {
                            try
                            {
                                ghosts.RecordPlayerGhost(
                                    handle.Run,
                                    db,
                                    isCustomRule: handle.Run.IsCustomRuleRun || isNonStandardPartition,
                                    poolPartition: partition);
                            }
                            catch { }
                        }
                    }

                    // 实时同步锦标赛房间成员进度（Day / Hour / Hour6 PvP 就位标记）并推送 SSE 驱动同步屏障
                    try
                    {
                        var syncLobby = lobbies?.FindLobbyForRun(handle.Run.TournamentSessionId, session.AccountId);
                        if (syncLobby != null)
                            lobbies!.RecordRunProgress(syncLobby, session.AccountId, handle.Run);
                    }
                    catch { }

                    // 局内生涯统计增量（严格遵守沙盒三禁隔离：SandboxLab 不计生涯统计）
                    if (!handle.Run.IsSandboxRun && !handle.Run.CustomRules.ShouldSkipCareerAndLeaderboard)
                    {
                        try { RecordInRunCareerDeltas(accounts, acc, cmd, handle.Run, outcome, goldBefore); } catch { }
                    }
                }

                // 兜底消费沙盒外部修改挂起的同步事件与全量同步标记（若玩家在播战斗跳过 sandbox_sync，下一次任意命令自动合并下发）
                if (outcome.Error == null &&
                    (handle.Run.PendingExternalSyncEvents.Count > 0 || handle.Run.PendingExternalForceStateSync))
                {
                    bool extForce = outcome.ForceStateSync;
                    handle.Run.ConsumePendingExternalSync(outcome.Events, ref extForce);
                    if (extForce)
                        outcome.ForceStateSync = true;
                }

                INetMessage reply = BuildCommandReply(
                    cmd,
                    handle.Run,
                    outcome,
                    stateBefore,
                    isOwnedSelectMove,
                    isFirstAfterReestablish,
                    out bool isPureBoardMove,
                    out bool isCombatSequence);

                log.LogInformation("/commands: sid={sid} rid={rid} cmd={cmd} ok={ok} state={state} hour={hour} day={day}",
                    sid, rid, cmd.GetType().Name, outcome.Error == null,
                    handle.Run.State, handle.Run.Hour, handle.Run.Day);

                if (isPureBoardMove)
                {
                    // 高频拖拽换位走 Persistence 1500ms 内存防抖合批，零磁盘写盘阻塞且完整兼容 PendingDebounceCount 与停服自动冲刷
                    handle.Run.SnapshotDirty = true;
                    try { db.SaveRunSnapshotDebounced(handle.Run, session.AccountId, isCustomRule: handle.Run.IsCustomRuleRun); } catch { }
                }
                else
                {
                    handle.Run.SnapshotDirty = false;
                    try { SaveSnapshotImmediate(db, handle.Run, session, handle.Engine); } catch { }
                }

                // 终局或局内放弃（EndRunVictory / EndRunDefeat / Shutdown）：仅首次从非终局跃迁时标记终局并结算生涯
                bool reachedTerminal = outcome.Error == null && !wasTerminalBefore && Persistence.IsTerminalState(handle.Run.State);
                if (reachedTerminal)
                {
                    try
                    {
                        bool isConceded = cmd is AbandonRunCommand;
                        string endReason = isConceded
                            ? "conceded"
                            : (handle.Run.State == ERunState.EndRunVictory ? "victory" : "defeat");
                        db.MarkRunEnded(handle.Run.RunId, handle.Run.State, endReason);

                        // 沙盒实验室三禁隔离：不计入生涯胜负
                        if (!handle.Run.IsSandboxRun && !handle.Run.CustomRules.ShouldSkipCareerAndLeaderboard)
                        {
                            var acc = accounts.FindByAccountId(session.AccountId) ?? ResolveAccount(ctx, accounts);
                            int elapsed = ComputeElapsedSeconds(db, handle.Run.RunId, session.CreatedAt);
                            accounts.RecordRunCompleted(acc, handle.Run, isConceded: isConceded, elapsedSeconds: elapsed);
                        }
                        session.CommandLog.Clear();

                        var tLobby = lobbies?.FindLobbyForRun(handle.Run.TournamentSessionId, session.AccountId);
                        if (tLobby != null)
                            lobbies!.RecordRunProgress(tLobby, session.AccountId, handle.Run);
                    }
                    catch { }
                }

                byte[] replyPayload = SerializeAndRecordResponse(session, runId, reqRid, hasReqRid, commandSha256, reply);

                // 先将响应刷给客户端，再做（有观战者时的）异步观战录制，消除回包前观战序列化阻塞！
                await WriteSerializedPayload(ctx, replyPayload, session.Sid, session.RequestId, handle.Run, modHost);

                // P0-44 精准观战门控：仅当目标账号存在活跃 SSE 棋盘订阅者或有效 HTTP 观战租约时才执行录制，彻底消除无观战者 LOH 浪费
                if (outcome.Error == null && spectateRecorder != null)
                {
                    if (ShouldRecordSpectate(spectateRecorder, sseHub, session.AccountId))
                    {
                        string recAccountId = session.AccountId;
                        string recRunId = handle.Run.RunId;
                        Guid? recTourneyId = handle.Run.TournamentSessionId;
                        long recFromRid = hasReqRid ? reqRid : prevRid;
                        long recRid = session.RequestId;
                        bool recCombat = isCombatSequence;
                        ThreadPool.UnsafeQueueUserWorkItem(_ =>
                        {
                            try
                            {
                                spectateRecorder.RecordCommandDelta(
                                    recAccountId,
                                    recRunId,
                                    recTourneyId,
                                    recFromRid,
                                    recRid,
                                    replyPayload,
                                    recCombat);
                            }
                            catch { }
                        }, state: null);
                    }
                }
            }
            finally
            {
                session.Gate.Release();
            }
        });

        static bool ShouldRecordSpectate(ISpectateFrameRecorder? recorder, BazaarServer.Tournament.SseEventHub? hub, string accountId)
            => recorder is SpectateFrameStore store
                ? store.ShouldRecordForAccount(accountId)
                : (hub != null && hub.HasBoardSubscribers(accountId));

        // ── DELETE /sessions ─────────────────────────────────────────────────
        app.MapDelete("/sessions", async (HttpContext ctx, SessionStore sessions, Persistence db) =>
        {
            string sid = ctx.Request.Headers["sid"].ToString();
            var session = sessions.Get(sid);
            if (session != null)
            {
                await session.Gate.WaitAsync(ctx.RequestAborted);
                try
                {
                    var handle = SessionRegistry.Get(sid);
                    if (handle != null)
                    {
                        if (!Persistence.IsTerminalState(handle.Run.State))
                        {
                            try { SaveSnapshotImmediate(db, handle.Run, session, handle.Engine); } catch { }
                        }
                        else
                        {
                            handle.Run.SnapshotDirty = false;
                            try { db.FlushRunImmediate(handle.Run.RunId); } catch { }
                        }
                    }
                    session.CommandLog.Clear();
                    sessions.Remove(sid);
                    SessionRegistry.Detach(sid);
                    DetachRidCache(sid);
                    try { spectateRecorder?.ClearForAccount(session.AccountId); } catch { }
                }
                finally
                {
                    session.Gate.Release();
                }
            }
            else
            {
                sessions.Remove(sid);
                SessionRegistry.Detach(sid);
                DetachRidCache(sid);
            }
            ctx.Response.StatusCode = 200;
        });

        // ── 当前局摘要（主菜单会拉）──────────────────────────────────────────
        app.MapGet("/runs/active/summary", (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            Persistence db,
            GameModeConfig mode) =>
        {
            var acc = ResolveAccount(ctx, accounts);

            // 1. 优先查内存活跃对局（D8-P1-02：读取不可变 volatile RunPublicSummarySnapshot；D6-P1-17：返回真实 StartingPrestige）
            var memSession = sessions.GetByAccount(acc.AccountId.ToString("N"));
            if (memSession != null)
            {
                var handle = SessionRegistry.Get(memSession.Sid);
                if (handle != null)
                {
                    var summary = handle.Run.PublicSummary;
                    if (!summary.IsTerminal)
                    {
                        var (initPrestige, maxPrestige) = ResolveEffectivePrestigeBounds(summary.RulesSnapshot, mode, summary.Prestige);
                        return Results.Json(new
                        {
                            HasActiveRun = true,
                            RunId = (Guid?)ToGuidOrDeterministic(summary.RunId),
                            PlayMode = summary.PlayMode.ToString(),
                            SpecialModeId = summary.SpecialModeIdName,
                            Hero = summary.Hero.ToString(),
                            Day = summary.Day,
                            Hour = summary.Hour,
                            CurrentPrestige = summary.Prestige,
                            InitialPrestige = initPrestige,
                            MaxPrestige = maxPrestige,
                            Victories = summary.Victories,
                            Defeats = summary.Defeats,
                        }, JsonOpts);
                    }
                }
            }

            // 2. 查 SQLite 未完结对局（P2-35 & D6-P1-17：解析真实 StartingPrestige 与 SpecialModeId）
            var row = db.LoadLatestActiveRun(acc.AccountId);
            if (row != null)
            {
                CustomRunRuleSet? rowRules = TryDeserializeRunRowRules(row);
                var (initPrestige, maxPrestige) = ResolveEffectivePrestigeBounds(rowRules, mode, row.Prestige);
                return Results.Json(new
                {
                    HasActiveRun = true,
                    RunId = (Guid?)ToGuidOrDeterministic(row.RunId),
                    PlayMode = row.PlayMode.ToString(),
                    SpecialModeId = ResolveRunRowSpecialModeId(row),
                    Hero = row.Hero.ToString(),
                    Day = row.Day,
                    Hour = row.Hour,
                    CurrentPrestige = row.Prestige,
                    InitialPrestige = initPrestige,
                    MaxPrestige = maxPrestige,
                    Victories = row.Victories,
                    Defeats = row.Defeats,
                }, JsonOpts);
            }

            return Results.Json(new
            {
                HasActiveRun = false,
                RunId = (Guid?)null,
                PlayMode = (string?)null,
                SpecialModeId = (string?)null,
                Hero = (string?)null,
                Day = 0u,
                Hour = 0u,
                CurrentPrestige = 0,
                InitialPrestige = mode.PrestigeInitial,
                MaxPrestige = mode.PrestigeMax,
                Victories = 0u,
                Defeats = 0u,
            }, JsonOpts);
        });

        // ── 主动投降当前活跃对局 ─────────────────────────────────────────────
        app.MapPost("/runs/active/concede", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            Persistence db,
            ILogger<Program> log) =>
        {
            var acc = ResolveAccount(ctx, accounts);
            string accountIdKey = acc.AccountId.ToString("N");
            string concededRunId = "";

            // P1-37：与 /sessions 共享同账号互斥锁（AccountLock → Session.Gate），杜绝并发建局/投降竞态
            var accountLock = GetAccountSessionLock(accountIdKey);
            await accountLock.WaitAsync(ctx.RequestAborted);
            try
            {
                // 投降前加会话锁并冲刷挂起的异步写盘任务，避免与 /commands 或后台定时器竞争
                var memSession = sessions.GetByAccount(accountIdKey);
                if (memSession != null)
                {
                    await memSession.Gate.WaitAsync(ctx.RequestAborted);
                    try
                    {
                        var handle = SessionRegistry.Get(memSession.Sid);
                        if (handle != null)
                        {
                            concededRunId = handle.Run.RunId;
                            try { db.FlushRunImmediate(concededRunId); } catch { }
                            if (!Persistence.IsTerminalState(handle.Run.State))
                            {
                                handle.Run.State = ERunState.EndRunDefeat;
                                handle.Run.PublishPublicSummary();
                                try
                                {
                                    SaveSnapshotImmediate(db, handle.Run, memSession, handle.Engine);
                                    db.MarkRunEnded(handle.Run.RunId, ERunState.EndRunDefeat, "conceded");
                                    if (!handle.Run.IsSandboxRun && !handle.Run.CustomRules.ShouldSkipCareerAndLeaderboard)
                                    {
                                        int elapsed = ComputeElapsedSeconds(db, handle.Run.RunId, memSession.CreatedAt);
                                        accounts.RecordRunCompleted(acc, handle.Run, isConceded: true, elapsedSeconds: elapsed);
                                    }
                                }
                                catch { }
                            }
                            var tLobby = lobbies?.FindLobbyForRun(handle.Run.TournamentSessionId, accountIdKey);
                            if (tLobby != null)
                                lobbies!.RecordRunProgress(tLobby, accountIdKey, handle.Run);
                        }
                        memSession.CommandLog.Clear();
                        sessions.Remove(memSession.Sid);
                        SessionRegistry.Detach(memSession.Sid);
                        DetachRidCache(memSession.Sid);
                        DetachRunRidCache(concededRunId);
                        try { spectateRecorder?.ClearForAccount(accountIdKey); } catch { }
                    }
                    finally
                    {
                        memSession.Gate.Release();
                    }
                }
                else if (db.PendingDebounceCount > 0)
                {
                    try { db.FlushAllPending(); } catch { }
                }

                var dbConceded = db.ConcedeActiveRuns(acc.AccountId);
                if (dbConceded != null)
                {
                    if (string.IsNullOrEmpty(concededRunId)) concededRunId = dbConceded.RunId;
                    bool skipDbCareer = IsSnapshotSandboxRun(dbConceded.Snapshot);
                    if (!skipDbCareer)
                    {
                        try
                        {
                            int elapsed = ComputeElapsedSeconds(db, dbConceded.RunId, dbConceded.SavedAt);
                            accounts.RecordRunCompleted(
                                acc,
                                dbConceded.RunId,
                                dbConceded.Hero,
                                dbConceded.PlayMode,
                                dbConceded.Day,
                                dbConceded.Victories,
                                dbConceded.Defeats,
                                dbConceded.Prestige,
                                ERunState.EndRunDefeat,
                                isConceded: true,
                                elapsedSeconds: elapsed);
                        }
                        catch { }
                    }
                }

                log.LogInformation("/runs/active/concede: account={acc} runId={rid}", acc.Username, concededRunId);
                return Results.Json(new { AccountId = acc.AccountId.ToString("D"), RunId = concededRunId }, JsonOpts);
            }
            finally
            {
                accountLock.Release();
            }
        });

        // ── 开发用：自动驾驶（官方也有这个端点）─────────────────────────────
        app.MapPost("/dev/autopilot/next", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 204;
        });
    }

    /// <summary>
    /// 断线重连防串局校验（P0-15）：
    /// - 锦标赛请求绝不恢复普通对局，且必须同 TournamentSessionId；
    /// - 非锦标赛请求绝不恢复未完结的锦标赛对局；
    /// - 玩家在大厅切换了 SpecialModeId 或规范化 SelectedModIds 后，单人局不强行续玩旧档；
    /// - 锦标赛局仅在命中权威内存大厅时校验 lobby.Rules.SelectedModIds（重启恢复窗口不受个人单人厅 Mod 勾选干扰）；
    /// - 每日挑战跨日后不再续玩昨日对局。
    /// </summary>
    private static bool CanResumeRunForInit(
        RunState candidate,
        InitializeRunCommand init,
        CustomRunRuleSet desiredRules,
        bool hasAuthoritativeLobby = false)
        => CanResumeCoreForInit(
            candidate.PlayMode,
            candidate.TournamentSessionId,
            candidate.CustomRules,
            init,
            desiredRules,
            hasAuthoritativeLobby);

    private static bool CanResumeHeaderForInit(
        RunSnapshotSerializer.RunSnapshotHeader header,
        InitializeRunCommand init,
        CustomRunRuleSet desiredRules,
        bool hasAuthoritativeLobby = false)
        => CanResumeCoreForInit(
            header.PlayMode,
            header.TournamentSessionId,
            header.CustomRules,
            init,
            desiredRules,
            hasAuthoritativeLobby);

    private static bool CanResumeCoreForInit(
        EPlayMode candPlayMode,
        Guid? candTourneyId,
        CustomRunRuleSet? candRules,
        InitializeRunCommand init,
        CustomRunRuleSet desiredRules,
        bool hasAuthoritativeLobby)
    {
        bool initIsTourney = init.PlayMode == EPlayMode.Tournament
            || (init.TournamentSessionId.HasValue && init.TournamentSessionId.Value != Guid.Empty);
        bool candIsTourney = candPlayMode == EPlayMode.Tournament
            || (candTourneyId.HasValue && candTourneyId.Value != Guid.Empty);

        if (initIsTourney != candIsTourney)
            return false;

        if (initIsTourney &&
            init.TournamentSessionId.HasValue &&
            init.TournamentSessionId.Value != Guid.Empty &&
            candTourneyId.HasValue &&
            candTourneyId.Value != Guid.Empty &&
            init.TournamentSessionId.Value != candTourneyId.Value)
        {
            return false;
        }

        if (!initIsTourney)
        {
            var candMode = candRules?.SpecialModeId ?? ESpecialModeId.Standard;
            if (candMode != desiredRules.SpecialModeId)
                return false;

            if (candMode == ESpecialModeId.DailyChallenge)
            {
                string today = BazaarCore.Engine.DailyChallengeSeedGenerator.GetTodayDateKeyUtc8();
                string? candDate = candRules?.DailyChallengeDate;
                if (!string.IsNullOrWhiteSpace(candDate) &&
                    !string.Equals(candDate, today, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            if (!RunSnapshotSerializer.AreModSetsEqual(candRules?.SelectedModIds, desiredRules.SelectedModIds))
                return false;
        }
        else if (hasAuthoritativeLobby)
        {
            // 锦标赛仅在命中权威内存大厅时校验大厅 Mod 集，避免重启恢复窗口回退个人 playerRules 导致误拒合法锦标赛重连
            if (!RunSnapshotSerializer.AreModSetsEqual(candRules?.SelectedModIds, desiredRules.SelectedModIds))
                return false;
        }

        return true;
    }

    private static (CustomRunRuleSet Rules, bool HasAuthoritativeLobby) ResolveDesiredRulesForAccount(
        string accountIdKey,
        InitializeRunCommand init,
        PlayerRuleStore? playerRules,
        BazaarServer.Tournament.LobbyStore? lobbies)
    {
        var lobby = lobbies?.FindLobbyForRun(init.TournamentSessionId, accountIdKey);
        if (lobby != null)
            return (lobby.Rules.CloneNormalized(), true);

        if (playerRules != null)
            return (playerRules.Get(accountIdKey).CloneNormalized(), false);

        return (new CustomRunRuleSet(), false);
    }

    /// <summary>D6-P1-17：结构化 JSON 判定快照是否属于 SandboxLab（彻底消除昵称/卡名包含 "SandboxLab" 误杀生涯记账）。</summary>
    public static bool IsSnapshotSandboxRun(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(snapshotJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!prop.Name.Equals("CustomRules", StringComparison.OrdinalIgnoreCase)) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) return false;
                foreach (var rp in prop.Value.EnumerateObject())
                {
                    if (!rp.Name.Equals("SpecialModeId", StringComparison.OrdinalIgnoreCase)) continue;
                    if (rp.Value.ValueKind == JsonValueKind.String)
                    {
                        string? s = rp.Value.GetString();
                        return string.Equals(s, nameof(ESpecialModeId.SandboxLab), StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s, "sandbox_lab", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s, "sandbox", StringComparison.OrdinalIgnoreCase);
                    }
                    if (rp.Value.ValueKind == JsonValueKind.Number && rp.Value.TryGetInt32(out int num))
                    {
                        return num == (int)ESpecialModeId.SandboxLab;
                    }
                }
                return false;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>D6-P1-17：按对局真实 CustomRules 与 GameModeConfig 计算权威初始与最大声望（含 IronmanNoFates 截断）。</summary>
    public static (int InitialPrestige, int MaxPrestige) ResolveEffectivePrestigeBounds(
        CustomRunRuleSet? rules,
        GameModeConfig mode,
        int currentPrestige = 0)
    {
        int baseInit = mode?.PrestigeInitial > 0 ? mode.PrestigeInitial : BazaarCore.Runtime.GameDefaults.StartingPrestige;
        int initPrestige = rules != null && rules.StartingPrestige != BazaarCore.Runtime.GameDefaults.StartingPrestige
            ? Math.Max(1, rules.StartingPrestige)
            : baseInit;
        if (rules != null && rules.HasMutator(MutatorCatalog.IronmanNoFates))
            initPrestige = Math.Min(initPrestige, 10);
        int baseMax = mode?.PrestigeMax > 0 ? mode.PrestigeMax : BazaarCore.Runtime.GameDefaults.StartingPrestige;
        int maxPrestige = Math.Max(baseMax, Math.Max(initPrestige, currentPrestige));
        return (initPrestige, maxPrestige);
    }

    private static CustomRunRuleSet? TryDeserializeRunRowRules(Persistence.RunRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.RulesJson))
        {
            try
            {
                var r = JsonSerializer.Deserialize<CustomRunRuleSet>(row.RulesJson, JsonOpts);
                if (r != null) return r.CloneNormalized();
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(row.Snapshot))
        {
            try
            {
                using var doc = JsonDocument.Parse(row.Snapshot);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Name.Equals("CustomRules", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object)
                        {
                            var r = JsonSerializer.Deserialize<CustomRunRuleSet>(p.Value.GetRawText(), JsonOpts);
                            if (r != null) return r.CloneNormalized();
                        }
                    }
                }
            }
            catch { }
        }
        return null;
    }

    private static void AppendBoundedCommandLog(
        Session session, long rid, INetCommand cmd, RunState run, bool ok)
    {
        session.AddCommandLog(
            $"{rid}|{cmd.GetType().Name}|state={run.State}|d={run.Day}h={run.Hour}|ok={ok}");
    }

    private static void SaveSnapshotImmediate(Persistence db, RunState run, Session session, RunEngine? engine = null)
    {
        bool startDone = engine?.StartSelectionDone ?? run.StartSelectionDone;
        string snapshotJson = RunSnapshotSerializer.Serialize(
            run,
            startSelectionDone: startDone,
            pendingLevelUp: run.PendingLevelUp);
        db.SaveRunSnapshot(run, session.AccountId, snapshotJson, isCustomRule: run.IsCustomRuleRun);
    }

    private static void RecordInRunCareerDeltas(
        AccountStore accounts,
        AccountStore.Account acc,
        INetCommand cmd,
        RunState run,
        RunEngine.Outcome outcome,
        int goldBefore)
    {
        int goldAfter = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0);
        int coinsSpent = cmd is SelectItemCommand or SelectSkillCommand or RerollCommand
            ? Math.Max(0, goldBefore - goldAfter)
            : 0;

        int itemsSold = cmd is SellCardCommand ? 1 : 0;
        int eventsCompleted = cmd is SelectEncounterCommand && outcome.CombatSpawnSim == null ? 1 : 0;
        int monsterVictories = 0;
        int monsterDefeats = 0;
        long damageDealt = 0;

        if (outcome.CombatSpawnSim != null && outcome.Combat != null)
        {
            bool isPvp = outcome.CombatSpawnSim.CurrentState?.StateName == ERunState.PVPCombat;
            if (!isPvp)
            {
                if (outcome.Combat.Winner == ECombatantId.Player) monsterVictories = 1;
                else monsterDefeats = 1;
            }

            if (outcome.Combat.Frames != null)
            {
                foreach (var frame in outcome.Combat.Frames)
                {
                    if (frame.OpponentUpdates?.Attributes != null &&
                        frame.OpponentUpdates.Attributes.TryGetValue(EPlayerAttributeType.Health, out var hpUpd) &&
                        hpUpd.PreviousValue > hpUpd.CurrentValue)
                    {
                        damageDealt += hpUpd.PreviousValue - hpUpd.CurrentValue;
                    }
                }
            }
        }

        if (coinsSpent > 0 || itemsSold > 0 || eventsCompleted > 0 ||
            monsterVictories > 0 || monsterDefeats > 0 || damageDealt > 0)
        {
            accounts.RecordInRunProgress(
                acc,
                run.Hero,
                monsterVictoriesDelta: monsterVictories,
                monsterDefeatsDelta: monsterDefeats,
                itemsSoldDelta: itemsSold,
                coinsSpentDelta: coinsSpent,
                eventsCompletedDelta: eventsCompleted,
                damageDealtDelta: damageDealt);
        }
    }

    private static int ComputeElapsedSeconds(Persistence db, string runId, DateTime fallbackStartUtc)
    {
        DateTime start = db.GetRunStartedAt(runId) ?? fallbackStartUtc;
        return Math.Max(0, (int)Math.Round((DateTime.UtcNow - start).TotalSeconds));
    }

    private static Guid ToGuidOrDeterministic(string rawId)
    {
        if (Guid.TryParse(rawId, out var g)) return g;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("run:" + (rawId ?? "")));
        byte[] bytes = new byte[16];
        Array.Copy(hash, bytes, 16);
        return new Guid(bytes);
    }

    private static AccountStore.Account ResolveAccount(HttpContext ctx, AccountStore accounts)
    {
        string token = ctx.Request.Headers["Authorization"].ToString();
        var acc = accounts.FindByAccessToken(token);
        if (acc != null) return acc;

        string aid = ctx.Request.Headers["aid"].ToString();
        acc = accounts.FindByAccountId(aid);
        if (acc != null) return acc;

        string uid = ctx.Request.Headers["uid"].ToString();
        return accounts.GetOrCreateByName(string.IsNullOrWhiteSpace(uid) ? "guest" : uid);
    }

    private static async Task<byte[]> ReadAll(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// D6-P1-11：全模式作弊门禁严格 6 级优先级：
    /// 1) 沙盒 sandbox_sync 零副作用同步永远放行；
    /// 2) BAZAAR_DISABLE_CHEATS == "1" 运维硬禁用高于一切逃生变量；
    /// 3) SandboxLab 放行；
    /// 4) BAZAAR_ALLOW_CHEATS_ALL_MODES == "1" 全模式开发逃生；
    /// 5) Tournament + BAZAAR_ALLOW_TOURNAMENT_CHEATS == "1" 兼容旧逃生；
    /// 6) 其余全部 8 大正式/上榜模式默认严格拦截。
    /// </summary>
    public static bool IsHttpCheatBlocked(RunState run, CheatCommand? cmd = null)
    {
        // 1. 沙盒对局的 sandbox_sync 是零副作用状态同步通道，不受通用调试开关限制
        if (run.IsSandboxRun &&
            cmd?.Args is { Count: > 0 } &&
            string.Equals(cmd.Args[0], "sandbox_sync", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // 2. 运维硬禁用开关最高优先（高于全部开发逃生变量）
        if (string.Equals(Environment.GetEnvironmentVariable("BAZAAR_DISABLE_CHEATS"), "1", StringComparison.Ordinal))
            return true;
        // 3. 沙盒实验室放行常规调试指令
        if (run.IsSandboxRun)
            return false;
        // 4. 全模式开发逃生变量
        if (string.Equals(Environment.GetEnvironmentVariable("BAZAAR_ALLOW_CHEATS_ALL_MODES"), "1", StringComparison.Ordinal))
            return false;
        // 5. 锦标赛旧逃生变量兼容
        if (run.PlayMode == EPlayMode.Tournament &&
            string.Equals(Environment.GetEnvironmentVariable("BAZAAR_ALLOW_TOURNAMENT_CHEATS"), "1", StringComparison.Ordinal))
        {
            return false;
        }
        // 6. 默认拦截全部正式与上榜模式（Standard / Ranked / DailyChallenge / EndlessTower / BossRush / CrossHeroMayhem / MutatorChallenge / Tournament）
        return true;
    }

    /// <summary>D3-P1-12（合流胶水 #5）：统一委托 ServerModHost.ComputeEffectiveModSetHash（含递归依赖闭包与 cosmetic 过滤）。</summary>
    public static string ComputeEffectiveModSetHashLocal(IReadOnlyList<string>? selectedModIds, ServerModHost? modHost)
        => ServerModHost.ComputeEffectiveModSetHash(selectedModIds, modHost);

    private static byte[] SerializeAndRecordResponse(
        Session session,
        string runId,
        long reqRid,
        bool hasReqRid,
        byte[] commandSha256,
        INetMessage message)
    {
        byte[] payload = Codec.WriteMessage(message);
        if (DebugDump.Enabled)
        {
            DebugDump.Write(session.Sid, session.RequestId, payload, message);
        }
        if (hasReqRid)
        {
            RecordIdempotentResponse(runId, reqRid, commandSha256, payload, session.RequestId);
        }
        return payload;
    }

    private static async Task WriteSerializedPayload(
        HttpContext ctx,
        byte[] payload,
        string sid,
        long responseRid,
        RunState? run,
        ServerModHost? modHost)
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/msgpack";
        ctx.Response.Headers["sid"] = sid;
        ctx.Response.Headers["rid"] = responseRid.ToString();
        if (run != null)
            ctx.Response.Headers["X-Visual-Bindings-Rev"] = run.VisualBindingsRevision.ToString();
        string effectiveModHash = ComputeEffectiveModSetHashLocal(run?.CustomRules?.SelectedModIds, modHost);
        if (!string.IsNullOrEmpty(effectiveModHash))
            ctx.Response.Headers["X-Mod-Set-Hash"] = effectiveModHash;
        ctx.Response.ContentLength = payload.Length;
        await ctx.Response.Body.WriteAsync(payload, 0, payload.Length);
    }

    private static async Task<byte[]> WriteMessageAndReturnPayload(
        HttpContext ctx,
        INetMessage message,
        Session session,
        RunState? run,
        ServerModHost? modHost)
    {
        byte[] payload = Codec.WriteMessage(message);

        if (DebugDump.Enabled)
        {
            DebugDump.Write(session.Sid, session.RequestId, payload, message);
        }

        await WriteSerializedPayload(ctx, payload, session.Sid, session.RequestId, run, modHost);
        return payload;
    }

    /// <summary>P2-11：限流错误统一填充官方客户端 NetErrorHandler 识别的 MessageId="RateLimited"。</summary>
    public static NetMessageError CreateRateLimitedError(string message = "Too many requests.")
        => new("RateLimited", EResponseErrorType.NotAllowed, message);

    /// <summary>P2-35 &amp; D6-P1-17：从 SQLite RunRow（优先 RulesJson，次之 ModeId）解析活跃局摘要所需的 SpecialModeId 名称。</summary>
    public static string ResolveRunRowSpecialModeId(Persistence.RunRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.RulesJson))
        {
            try
            {
                var rules = JsonSerializer.Deserialize<CustomRunRuleSet>(row.RulesJson, JsonOpts);
                if (rules != null)
                    return rules.SpecialModeId.ToString();
            }
            catch
            {
                // 忽略旧格式 JSON 解析失败，回退至 ModeId 映射
            }
        }

        if (!string.IsNullOrWhiteSpace(row.ModeId))
        {
            if (Enum.TryParse<ESpecialModeId>(row.ModeId, ignoreCase: true, out var parsed))
                return parsed.ToString();

            return row.ModeId.Trim().ToLowerInvariant() switch
            {
                "boss_rush" or "bossrush" => nameof(ESpecialModeId.BossRush),
                "sandbox" or "sandbox_lab" or "sandboxlab" => nameof(ESpecialModeId.SandboxLab),
                "endless" or "endless_tower" or "endlesstower" => nameof(ESpecialModeId.EndlessTower),
                "daily" or "daily_challenge" or "dailychallenge" => nameof(ESpecialModeId.DailyChallenge),
                "cross_hero" or "cross_hero_mayhem" or "crossheromayhem" => nameof(ESpecialModeId.CrossHeroMayhem),
                "mutator" or "mutator_challenge" or "mutatorchallenge" => nameof(ESpecialModeId.MutatorChallenge),
                _ => nameof(ESpecialModeId.Standard),
            };
        }

        return nameof(ESpecialModeId.Standard);
    }
}

public sealed class TournamentStatusDto
{
    public Guid TournamentSessionId { get; set; }
    public string Phase { get; set; } = "DayFlow";
    public uint CurrentDay { get; set; } = 1;
    public int TotalPlayers { get; set; } = 1;
    public int ActivePlayers { get; set; } = 1;
    public int ReadyPlayers { get; set; }
    public DateTime? DeadlineAt { get; set; }
    public bool PlayerReadyForPvp { get; set; }
    public bool ForcedTransition { get; set; }
    public bool CombatSubmitted { get; set; }
    public bool CanStartPvp { get; set; }
    public bool WaitingForOtherPlayers { get; set; }
    public bool IsEliminated { get; set; }
    public bool IsChampion { get; set; }
    public int? Place { get; set; }
    public bool Concede { get; set; }
    public bool EndRun { get; set; }
    public string AssignedOpponentAccountId { get; set; } = "";
    public string AssignedOpponentUsername { get; set; } = "";
    public bool WaitingForHumanDayStart { get; set; }
    public object[] Players { get; set; } = Array.Empty<object>();
    public string ExitReason { get; set; } = "";

    public static TournamentStatusDto Default() => new();
}

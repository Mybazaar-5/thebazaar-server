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
using BazaarServer.Accounts;
using BazaarServer.Data;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Protocol;
using BazaarServer.Storage;

namespace BazaarServer.Endpoints;

public static class GameServerEndpoints
{
    private const int MaxRidCacheEntriesPerSession = 64;
    private const int MaxRidCacheSessions = 1024;

    private sealed class CachedCommandResponse
    {
        public required byte[] CommandSha256 { get; init; }
        public required byte[] Payload { get; init; }
        public required long ResponseRid { get; init; }
    }

    private sealed class SessionRidCache
    {
        private readonly Dictionary<long, CachedCommandResponse> _entries = new();
        private readonly Queue<long> _order = new();
        public long LastAccessTick { get; private set; } = Environment.TickCount64;

        public bool TryGetMatch(long reqRid, ReadOnlySpan<byte> commandSha256, out CachedCommandResponse? cached)
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

        public void Store(long reqRid, byte[] commandSha256, byte[] payload, long responseRid)
        {
            LastAccessTick = Environment.TickCount64;
            if (!_entries.ContainsKey(reqRid))
            {
                while (_order.Count >= MaxRidCacheEntriesPerSession)
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

    private static readonly ConcurrentDictionary<string, SessionRidCache> RidCaches = new(StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly PropertyInfo? StartSelectionDoneProp =
        typeof(RunEngine).GetProperty(nameof(RunEngine.StartSelectionDone), BindingFlags.Public | BindingFlags.Instance);

    /// <summary>移除指定会话的幂等响应缓存，防止长跑内存泄漏。</summary>
    public static void DetachRidCache(string sid)
    {
        if (!string.IsNullOrEmpty(sid))
            RidCaches.TryRemove(sid, out _);
    }

    /// <summary>按活跃会话存在性批量清理孤儿 RidCache 条目。</summary>
    public static int PurgeOrphanRidCaches(Func<string, bool> exists)
    {
        int removed = 0;
        foreach (string sid in RidCaches.Keys)
        {
            if (!exists(sid) && RidCaches.TryRemove(sid, out _))
                removed++;
        }
        return removed;
    }

    private static void EnforceRidCacheCapacity()
    {
        if (RidCaches.Count <= MaxRidCacheSessions) return;
        int over = RidCaches.Count - (MaxRidCacheSessions * 3 / 4);
        var victims = RidCaches
            .OrderBy(kv => kv.Value.LastAccessTick)
            .Take(Math.Max(1, over))
            .Select(kv => kv.Key)
            .ToList();
        foreach (string sid in victims)
            RidCaches.TryRemove(sid, out _);
    }

    public static void Map(WebApplication app)
    {
        var archetypeIndex = app.Services.GetService<CardArchetypeIndex>();
        var playerRules = app.Services.GetService<PlayerRuleStore>();
        var lobbies = app.Services.GetService<BazaarServer.Tournament.LobbyStore>();
        var diSessions = app.Services.GetService<SessionStore>();
        var pvpCoordinator = app.Services.GetService<ITournamentPvpCoordinator>();
        var socialSink = app.Services.GetService<ISocialAndReplaySink>();
        var spectateRecorder = app.Services.GetService<ISpectateFrameRecorder>();
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

        // 进程优雅退出时冲刷所有待落盘的异步防抖写入任务，防止换位状态丢失
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

            var activeRepo = modHost?.ActiveRepository ?? fallbackRepo;
            var account = ResolveAccount(ctx, accounts);
            string accountIdKey = account.AccountId.ToString("N");
            var desiredRules = ResolveDesiredRulesForAccount(accountIdKey, init, playerRules, lobbies);

            // 1. 优先检查内存中或 SQLite 是否存在该账号的未完结活跃对局（Resume Run / 410 重连恢复，严格防串局）
            RunState? resumedRun = null;
            bool? resumedStarterDone = null;

            var existingSession = sessions.GetByAccount(accountIdKey);
            if (existingSession != null)
            {
                await existingSession.Gate.WaitAsync(ctx.RequestAborted);
                try
                {
                    var existingHandle = SessionRegistry.Get(existingSession.Sid);
                    if (existingHandle != null)
                    {
                        // 新会话创建/恢复前先冲刷该局挂起的防抖写入任务，保证内存与 SQLite 完全同步
                        try { db.FlushRunImmediate(existingHandle.Run.RunId); } catch { }
                        if (!Persistence.IsTerminalState(existingHandle.Run.State) &&
                            CanResumeRunForInit(existingHandle.Run, init, desiredRules))
                        {
                            resumedRun = existingHandle.Run;
                            resumedStarterDone = existingHandle.Engine.StartSelectionDone;
                        }
                    }
                    SessionRegistry.Detach(existingSession.Sid);
                    DetachRidCache(existingSession.Sid);
                }
                finally
                {
                    existingSession.Gate.Release();
                }
            }

            if (resumedRun == null)
            {
                var savedRow = db.LoadLatestActiveRun(account.AccountId);
                if (savedRow != null &&
                    RunSnapshotSerializer.TryDeserialize(savedRow.Snapshot, activeRepo, out var restored, out var snapDto) &&
                    restored != null &&
                    !Persistence.IsTerminalState(restored.State) &&
                    CanResumeRunForInit(restored, init, desiredRules))
                {
                    resumedRun = restored;
                    resumedStarterDone = snapDto?.StartSelectionDone;
                }
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

                var resumedEngine = new RunEngine(
                    resumedRun, pool, monsters, levelUps, mode, activeRepo, ghosts,
                    archetypeIndex, playerRules, lobbies, diSessions ?? sessions,
                    pvpCoordinator, socialSink);
                bool starterDone = resumedStarterDone ?? RunSnapshotSerializer.InferStartSelectionDone(resumedRun);
                if (starterDone)
                {
                    StartSelectionDoneProp?.SetValue(resumedEngine, true);
                }

                SessionRegistry.Attach(session.Sid, resumedRun, resumedEngine);
                try { SaveSnapshotImmediate(db, resumedRun, session, resumedEngine); } catch { }
                try
                {
                    var tLobby = lobbies?.FindLobbyForRun(resumedRun.TournamentSessionId, accountIdKey);
                    if (tLobby != null)
                        lobbies!.RecordRunProgress(tLobby, accountIdKey, resumedRun);
                }
                catch { }

                var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(resumedRun);
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
                    spectateRecorder?.RecordBootstrapSnapshot(
                        accountIdKey, resumedRun.RunId, resumedRun.TournamentSessionId, session.RequestId, resumePayload);
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
                CustomRules = desiredRules.CloneNormalized(),
            };
            var engine = new RunEngine(
                run, pool, monsters, levelUps, mode, activeRepo, ghosts,
                archetypeIndex, playerRules, lobbies, diSessions ?? sessions,
                pvpCoordinator, socialSink);

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
                spectateRecorder?.RecordBootstrapSnapshot(
                    accountIdKey, run.RunId, run.TournamentSessionId, session.RequestId, startPayload);
            }
            catch { }
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

                if (hasReqRid &&
                    RidCaches.TryGetValue(sid, out var ridCache) &&
                    ridCache.TryGetMatch(reqRid, commandSha256, out var cachedResp) &&
                    cachedResp != null)
                {
                    session.LastCommandAt = DateTime.UtcNow;
                    log.LogInformation("/commands [RID-REPLAY]: sid={sid} reqRid={reqRid} respRid={respRid} cmd={cmd}",
                        sid, reqRid, cachedResp.ResponseRid, cmd.GetType().Name);
                    await WriteSerializedPayload(ctx, cachedResp.Payload, session.Sid, cachedResp.ResponseRid, handle.Run, modHost);
                    return;
                }

                long prevRid = session.RequestId;
                long rid = hasReqRid ? reqRid + 1 : session.RequestId + 1;
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
                if (cmd is CheatCommand && IsHttpCheatBlocked(handle.Run))
                {
                    outcome = new RunEngine.Outcome
                    {
                        Error = new NetMessageError(
                            null,
                            EResponseErrorType.NotAllowed,
                            "当前模式（锦标赛/每日挑战或服务器配置）已禁用调试指令"),
                    };
                }
                else if (cmd is AbandonRunCommand && wasTerminalBefore)
                {
                    outcome = new RunEngine.Outcome
                    {
                        Error = new NetMessageError(
                            null,
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

                    // Hour 6 PvP 战斗发生时，按分区写入 SQLite pvp_ghosts 并热加载进对应分区池（严格执行沙盒三禁隔离，绝不污染标准池）
                    if (!isSandbox &&
                        outcome.CombatSpawnSim != null &&
                        outcome.CombatSpawnSim.CurrentState?.StateName == ERunState.PVPCombat)
                    {
                        string partition = handle.Run.CustomRules.ResolveGhostPoolPartition();
                        int curHpMax = handle.Run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250);
                        if (partition != "none" &&
                            (handle.Run.IsCustomRuleRun || Persistence.IsSaneGhostForDay((int)handle.Run.Day, curHpMax)))
                        {
                            try
                            {
                                ghosts.RecordPlayerGhost(
                                    handle.Run,
                                    db,
                                    isCustomRule: handle.Run.IsCustomRuleRun,
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

                INetMessage reply;
                bool isCombatSequence = false;
                if (outcome.Error != null)
                {
                    reply = outcome.Error;
                }
                else if (outcome.CombatSpawnSim != null && outcome.Combat != null && outcome.CombatDespawnSim != null)
                {
                    isCombatSequence = true;
                    reply = new NetMessageAggregate(new List<INetMessage>
                    {
                        new NetMessageGameSim(outcome.CombatSpawnSim),
                        new NetMessageCombatSim(outcome.Combat),
                        new NetMessageGameSim(outcome.CombatDespawnSim),
                    });
                }
                else
                {
                    var list = new List<INetMessage>();
                    if (outcome.ForceStateSync) list.Add(new NetMessageGameStateSync(handle.Run.BuildSnapshot()));
                    list.Add(new NetMessageGameSim(handle.Run.BuildFullGameSim(outcome.Events)));
                    if (outcome.Combat != null) list.Add(new NetMessageCombatSim(outcome.Combat));
                    reply = new NetMessageAggregate(list);
                }

                log.LogInformation("/commands: sid={sid} rid={rid} cmd={cmd} ok={ok} state={state} hour={hour} day={day}",
                    sid, rid, cmd.GetType().Name, outcome.Error == null,
                    handle.Run.State, handle.Run.Hour, handle.Run.Day);

                bool isPureBoardMove = (cmd is MoveItemCommand || isOwnedSelectMove) &&
                    outcome.Error == null &&
                    handle.Run.State == stateBefore &&
                    !Persistence.IsTerminalState(handle.Run.State) &&
                    outcome.CombatSpawnSim == null;

                if (isPureBoardMove)
                {
                    try { db.SaveRunSnapshotDebounced(handle.Run, session.AccountId, isCustomRule: handle.Run.IsCustomRuleRun); } catch { }
                }
                else
                {
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

                byte[] replyPayload = SerializeAndRecordResponse(session, reqRid, hasReqRid, commandSha256, reply);

                // 录制观战增量帧（仅对成功命令记录，供观战 SSE/轮询拉取）
                if (outcome.Error == null && spectateRecorder != null)
                {
                    try
                    {
                        spectateRecorder.RecordCommandDelta(
                            session.AccountId,
                            handle.Run.RunId,
                            handle.Run.TournamentSessionId,
                            hasReqRid ? reqRid : prevRid,
                            session.RequestId,
                            replyPayload,
                            isCombatSequence);
                    }
                    catch { }
                }

                await WriteSerializedPayload(ctx, replyPayload, session.Sid, session.RequestId, handle.Run, modHost);
            }
            finally
            {
                session.Gate.Release();
            }
        });

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
                        try { SaveSnapshotImmediate(db, handle.Run, session, handle.Engine); } catch { }
                        try { db.FlushRunImmediate(handle.Run.RunId); } catch { }
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

            // 1. 优先查内存活跃对局
            var memSession = sessions.GetByAccount(acc.AccountId.ToString("N"));
            if (memSession != null)
            {
                var handle = SessionRegistry.Get(memSession.Sid);
                if (handle != null && !Persistence.IsTerminalState(handle.Run.State))
                {
                    var r = handle.Run;
                    return Results.Json(new
                    {
                        HasActiveRun = true,
                        RunId = (Guid?)ToGuidOrDeterministic(r.RunId),
                        PlayMode = r.PlayMode.ToString(),
                        SpecialModeId = r.CustomRules.SpecialModeId.ToString(),
                        Hero = r.Hero.ToString(),
                        Day = r.Day,
                        Hour = r.Hour,
                        CurrentPrestige = r.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, mode.PrestigeInitial),
                        InitialPrestige = mode.PrestigeInitial,
                        MaxPrestige = mode.PrestigeMax,
                        Victories = r.Victories,
                        Defeats = r.Defeats,
                    }, JsonOpts);
                }
            }

            // 2. 查 SQLite 未完结对局
            var row = db.LoadLatestActiveRun(acc.AccountId);
            if (row != null)
            {
                return Results.Json(new
                {
                    HasActiveRun = true,
                    RunId = (Guid?)ToGuidOrDeterministic(row.RunId),
                    PlayMode = row.PlayMode.ToString(),
                    Hero = row.Hero.ToString(),
                    Day = row.Day,
                    Hour = row.Hour,
                    CurrentPrestige = row.Prestige,
                    InitialPrestige = mode.PrestigeInitial,
                    MaxPrestige = mode.PrestigeMax,
                    Victories = row.Victories,
                    Defeats = row.Defeats,
                }, JsonOpts);
            }

            return Results.Json(new
            {
                HasActiveRun = false,
                RunId = (Guid?)null,
                PlayMode = (string?)null,
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
        });

        // ── 开发用：自动驾驶（官方也有这个端点）─────────────────────────────
        app.MapPost("/dev/autopilot/next", (HttpContext ctx) =>
        {
            ctx.Response.StatusCode = 204;
        });
    }

    /// <summary>
    /// 断线重连防串局校验：
    /// - 锦标赛请求绝不恢复普通对局，且必须同 TournamentSessionId；
    /// - 非锦标赛请求绝不恢复未完结的锦标赛对局；
    /// - 玩家在大厅切换了 SpecialModeId（如切入沙盒/Boss车轮战/无尽/每日挑战）后，不强行续玩旧模式存档；
    /// - 每日挑战跨日后不再续玩昨日对局。
    /// </summary>
    private static bool CanResumeRunForInit(RunState candidate, InitializeRunCommand init, CustomRunRuleSet desiredRules)
    {
        bool initIsTourney = init.PlayMode == EPlayMode.Tournament
            || (init.TournamentSessionId.HasValue && init.TournamentSessionId.Value != Guid.Empty);
        bool candIsTourney = candidate.PlayMode == EPlayMode.Tournament
            || (candidate.TournamentSessionId.HasValue && candidate.TournamentSessionId.Value != Guid.Empty);

        if (initIsTourney != candIsTourney)
            return false;

        if (initIsTourney &&
            init.TournamentSessionId.HasValue &&
            init.TournamentSessionId.Value != Guid.Empty &&
            candidate.TournamentSessionId.HasValue &&
            candidate.TournamentSessionId.Value != Guid.Empty &&
            init.TournamentSessionId.Value != candidate.TournamentSessionId.Value)
        {
            return false;
        }

        if (!initIsTourney)
        {
            var candMode = candidate.CustomRules?.SpecialModeId ?? ESpecialModeId.Standard;
            if (candMode != desiredRules.SpecialModeId)
                return false;

            if (candMode == ESpecialModeId.DailyChallenge)
            {
                string today = BazaarCore.Engine.DailyChallengeSeedGenerator.GetTodayDateKeyUtc8();
                string? candDate = candidate.CustomRules?.DailyChallengeDate;
                if (!string.IsNullOrWhiteSpace(candDate) &&
                    !string.Equals(candDate, today, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static CustomRunRuleSet ResolveDesiredRulesForAccount(
        string accountIdKey,
        InitializeRunCommand init,
        PlayerRuleStore? playerRules,
        BazaarServer.Tournament.LobbyStore? lobbies)
    {
        var lobby = lobbies?.FindLobbyForRun(init.TournamentSessionId, accountIdKey);
        if (lobby != null)
            return lobby.Rules.CloneNormalized();

        if (playerRules != null)
            return playerRules.Get(accountIdKey).CloneNormalized();

        return new CustomRunRuleSet();
    }

    private static bool IsSnapshotSandboxRun(string? snapshotJson)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson)) return false;
        return snapshotJson.Contains("\"SandboxLab\"", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendBoundedCommandLog(
        Session session, long rid, INetCommand cmd, RunState run, bool ok)
    {
        session.AddCommandLog(
            $"{rid}|{cmd.GetType().Name}|state={run.State}|d={run.Day}h={run.Hour}|ok={ok}");
    }

    private static void SaveSnapshotImmediate(Persistence db, RunState run, Session session, RunEngine? engine = null)
    {
        bool startDone = engine?.StartSelectionDone ?? RunSnapshotSerializer.InferStartSelectionDone(run);
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

    private static bool IsHttpCheatBlocked(RunState run)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("BAZAAR_DISABLE_CHEATS"), "1", StringComparison.Ordinal))
            return true;
        if (run.CustomRules?.SpecialModeId == ESpecialModeId.DailyChallenge)
            return true;
        if (run.PlayMode == EPlayMode.Tournament &&
            !string.Equals(Environment.GetEnvironmentVariable("BAZAAR_ALLOW_TOURNAMENT_CHEATS"), "1", StringComparison.Ordinal))
        {
            return true;
        }
        return false;
    }

    private static byte[] SerializeAndRecordResponse(
        Session session,
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
            var cache = RidCaches.GetOrAdd(session.Sid, _ => new SessionRidCache());
            cache.Store(reqRid, commandSha256, payload, session.RequestId);
            EnforceRidCacheCapacity();
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
        if (modHost != null && !string.IsNullOrEmpty(modHost.ModSetHash))
            ctx.Response.Headers["X-Mod-Set-Hash"] = modHost.ModSetHash;
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

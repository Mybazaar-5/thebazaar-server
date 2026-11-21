using System.Collections.Concurrent;
using System.Text.Json;
using BazaarCore.Data;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Protocol;
using BazaarServer.Storage;
using BazaarServer.Tournament;

namespace BazaarServer.Endpoints;

/// <summary>
/// 双轨观战帧录制器（实现 ISpectateFrameRecorder）：
/// - 为每位活跃玩家保存最近全量 Bootstrap 快照 + 最多 32 步增量 MessagePack 环形缓冲；
/// - 收到新指令增量帧时零重复序列化直接通过 SseEventHub 广播给所有实时棋盘观战订阅者。
/// </summary>
public sealed class SpectateFrameStore : ISpectateFrameRecorder
{
    public const int MaxDeltaRingSize = 32;

    public sealed record DeltaFrame(
        long FromRequestId,
        long RequestId,
        byte[] MsgPackPayload,
        bool IsCombatSequence,
        DateTime RecordedAtUtc);

    public sealed record BoardQueryResult(
        bool Found,
        bool NotModified,
        long RequestId,
        byte[]? Payload,
        string? ErrorZh = null);

    private sealed class AccountBuffer
    {
        public readonly object Sync = new();
        public string RunId = "";
        public Guid? TournamentSessionId;
        public long BootstrapRequestId;
        public byte[]? BootstrapPayload;
        public long LatestRequestId;
        public DateTime UpdatedAtUtc = DateTime.UtcNow;
        public readonly List<DeltaFrame> Deltas = new(MaxDeltaRingSize);
    }

    private readonly ConcurrentDictionary<string, AccountBuffer> _buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SseEventHub _sseHub;

    public SpectateFrameStore(SseEventHub sseHub)
    {
        _sseHub = sseHub ?? throw new ArgumentNullException(nameof(sseHub));
    }

    public void RecordBootstrapSnapshot(
        string accountId,
        string runId,
        Guid? tournamentSessionId,
        long requestId,
        byte[] msgPackPayload)
    {
        if (string.IsNullOrWhiteSpace(accountId) || msgPackPayload == null || msgPackPayload.Length == 0)
            return;

        string key = NormalizeKey(accountId);
        var buf = _buffers.GetOrAdd(key, _ => new AccountBuffer());
        long rid = Math.Max(1, requestId);

        lock (buf.Sync)
        {
            buf.RunId = runId ?? "";
            buf.TournamentSessionId = tournamentSessionId;
            buf.BootstrapRequestId = rid;
            buf.BootstrapPayload = msgPackPayload;
            buf.LatestRequestId = Math.Max(buf.LatestRequestId, rid);
            buf.UpdatedAtUtc = DateTime.UtcNow;
            buf.Deltas.Clear();
        }

        BroadcastBoardFrame(key, fromRequestId: 0, requestId: rid, msgPackPayload);
    }

    public void RecordCommandDelta(
        string accountId,
        string runId,
        Guid? tournamentSessionId,
        long fromRequestId,
        long requestId,
        byte[] msgPackPayload,
        bool isCombatSequence)
    {
        if (string.IsNullOrWhiteSpace(accountId) || msgPackPayload == null || msgPackPayload.Length == 0)
            return;

        string key = NormalizeKey(accountId);
        var buf = _buffers.GetOrAdd(key, _ => new AccountBuffer());
        long rid = Math.Max(1, requestId);
        long fromRid = Math.Max(0, fromRequestId);
        var frame = new DeltaFrame(fromRid, rid, msgPackPayload, isCombatSequence, DateTime.UtcNow);

        lock (buf.Sync)
        {
            if (!string.Equals(buf.RunId, runId, StringComparison.Ordinal))
            {
                buf.RunId = runId ?? "";
                buf.TournamentSessionId = tournamentSessionId;
                buf.Deltas.Clear();
            }

            buf.Deltas.Add(frame);
            if (buf.Deltas.Count > MaxDeltaRingSize)
                buf.Deltas.RemoveAt(0);
            if (rid > buf.LatestRequestId)
                buf.LatestRequestId = rid;
            buf.UpdatedAtUtc = frame.RecordedAtUtc;
        }

        BroadcastBoardFrame(key, fromRid, rid, msgPackPayload);
    }

    public void ClearForAccount(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId)) return;
        _buffers.TryRemove(NormalizeKey(accountId), out _);
    }

    /// <summary>获取自 sinceRequestId 之后的待补发增量帧列表（供 SSE 建连瞬间补帧）。</summary>
    public List<DeltaFrame> GetPendingFramesSince(string accountId, long sinceRequestId)
    {
        string key = NormalizeKey(accountId);
        if (!_buffers.TryGetValue(key, out var buf))
            return new List<DeltaFrame>();

        lock (buf.Sync)
        {
            if (sinceRequestId <= 0)
            {
                if (buf.BootstrapPayload != null && buf.Deltas.Count == 0)
                    return new List<DeltaFrame> { new(0, buf.BootstrapRequestId, buf.BootstrapPayload, false, buf.UpdatedAtUtc) };
                return new List<DeltaFrame>(buf.Deltas);
            }

            return buf.Deltas.Where(d => d.RequestId > sinceRequestId).ToList();
        }
    }

    /// <summary>
    /// 按原版客户端观战协议返回目标玩家棋盘数据（Bootstrap 或增量，或无变更 304）。
    /// 若录制缓冲中尚无数据（或游标滑出窗口），直接实时锁定目标会话拍取原版 Bootstrap 全量快照。
    /// </summary>
    public async Task<BoardQueryResult> GetBoardResponseAsync(
        string targetAccountId,
        long sinceRequestId,
        SessionStore sessions,
        CancellationToken ct)
    {
        string key = NormalizeKey(targetAccountId);
        var session = sessions.GetByAccount(key);
        var handle = session != null ? SessionRegistry.Get(session.Sid) : null;
        long liveRid = session != null ? Volatile.Read(ref session.RequestId) : 0;

        if (_buffers.TryGetValue(key, out var buf))
        {
            byte[]? bootstrap = null;
            long bootRid = 0;
            long latestRid = 0;
            List<DeltaFrame>? deltas = null;
            bool windowCoversSince = false;

            lock (buf.Sync)
            {
                latestRid = Math.Max(buf.LatestRequestId, liveRid);
                bootRid = buf.BootstrapRequestId;
                bootstrap = buf.BootstrapPayload;

                if (sinceRequestId > 0)
                {
                    if (latestRid <= sinceRequestId)
                        return new BoardQueryResult(true, NotModified: true, latestRid, null);

                    if (buf.Deltas.Count > 0 && buf.Deltas[0].FromRequestId <= sinceRequestId)
                    {
                        deltas = buf.Deltas.Where(d => d.RequestId > sinceRequestId).ToList();
                        windowCoversSince = deltas.Count > 0;
                    }
                }
            }

            if (sinceRequestId > 0 && windowCoversSince && deltas is { Count: > 0 })
            {
                long endRid = deltas[^1].RequestId;
                if (deltas.Count == 1)
                    return new BoardQueryResult(true, NotModified: false, endRid, deltas[0].MsgPackPayload);

                byte[]? merged = TryMergeDeltaPayloads(deltas);
                if (merged != null)
                    return new BoardQueryResult(true, NotModified: false, endRid, merged);
            }

            // 首次 Bootstrap 或游标已滑出 32 步窗口：优先返回实时现场快照，次选缓存 Bootstrap
            if (session != null && handle != null)
            {
                var liveSnap = await CaptureLiveBootstrapAsync(session, handle, ct).ConfigureAwait(false);
                if (liveSnap != null)
                    return liveSnap;
            }

            if (bootstrap != null)
                return new BoardQueryResult(true, NotModified: false, Math.Max(bootRid, latestRid), bootstrap);
        }

        if (session == null || handle == null)
            return new BoardQueryResult(false, NotModified: false, 0, null, "目标玩家当前没有可观战的活跃对局。");

        if (sinceRequestId > 0 && liveRid > 0 && liveRid <= sinceRequestId)
            return new BoardQueryResult(true, NotModified: true, liveRid, null);

        var fresh = await CaptureLiveBootstrapAsync(session, handle, ct).ConfigureAwait(false);
        return fresh ?? new BoardQueryResult(false, NotModified: false, 0, null, "无法捕获目标玩家当前盘面快照。");
    }

    private static async Task<BoardQueryResult?> CaptureLiveBootstrapAsync(
        Session session,
        SessionRegistry.Handle handle,
        CancellationToken ct)
    {
        bool acquired = false;
        try
        {
            acquired = await session.Gate.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            if (!acquired) return null;

            var run = handle.Run;
            if (run == null) return null;

            long rid = Math.Max(1, Volatile.Read(ref session.RequestId));
            var resumeEvents = RunSnapshotSerializer.BuildResumeEvents(run);
            var aggregate = new NetMessageAggregate(new List<INetMessage>
            {
                run.BuildRunInitialized(),
                new NetMessageGameStateSync(run.BuildSnapshot()),
                new NetMessageGameSim(run.BuildFullGameSim(resumeEvents)),
            });
            byte[] payload = Codec.WriteMessage(aggregate);
            return new BoardQueryResult(true, NotModified: false, rid, payload);
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
        finally
        {
            if (acquired)
            {
                try { session.Gate.Release(); } catch { }
            }
        }
    }

    private static byte[]? TryMergeDeltaPayloads(List<DeltaFrame> deltas)
    {
        try
        {
            var flattened = new List<INetMessage>();
            foreach (var d in deltas)
            {
                var msg = Codec.ReadMessage(d.MsgPackPayload);
                if (msg is NetMessageAggregate agg && agg.Messages != null)
                    flattened.AddRange(agg.Messages);
                else
                    flattened.Add(msg);
            }
            if (flattened.Count == 0) return null;
            return Codec.WriteMessage(new NetMessageAggregate(flattened));
        }
        catch
        {
            return null;
        }
    }

    private void BroadcastBoardFrame(string accountKey, long fromRequestId, long requestId, byte[] msgPackPayload)
    {
        // 严格对齐原版 TournamentSpectateStreamUpdateDto：RequestId / FromRequestId / MessagePayload (Base64)
        string json = JsonSerializer.Serialize(new
        {
            RequestId = requestId,
            FromRequestId = fromRequestId,
            MessagePayload = Convert.ToBase64String(msgPackPayload),
        });
        _sseHub.PublishBoardUpdate(accountKey, json);
    }

    private static string NormalizeKey(string accountId)
        => Guid.TryParse(accountId, out var g) ? g.ToString("N") : accountId.Trim().Replace("-", "").ToLowerInvariant();
}

/// <summary>
/// 双轨观战与历史战斗回放 HTTP + SSE 端点：
/// - 轨道 A（原版游戏客户端 MessagePack + X-Request-Id 协议）：
///     GET /sessions/current/players/{targetAccountId}/board[?since={rid}]
///     GET /tournament/spectate/{watchCode}/players/{targetAccountId}/board[?since={rid}]
///     GET /tournament/spectate/{watchCode}/players/{targetAccountId}/board/events[?since={rid}]
/// - 轨道 B（嵌入式 UI & 启动器结构化 JSON 实时盘面与回放系统）：
///     GET /api/spectate/{code}/players/{targetAccountId}/board-dto
///     GET /api/replays & GET /api/replays/me
///     GET /api/lobbies/{code}/replays
///     GET /api/replays/{id} & GET /api/replays/{id}/msgpack
/// </summary>
public static class SpectateEndpoints
{
    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void Map(WebApplication app)
    {
        // ── 轨道 A-1：局内观战同房对手棋盘（TournamentBoardObserverHttpClient）──
        app.MapGet("/sessions/current/players/{targetAccountId}/board", async (
            string targetAccountId,
            HttpContext ctx,
            AccountStore accounts,
            LobbyStore lobbies,
            SessionStore sessions,
            SpectateFrameStore frameStore,
            CancellationToken ct) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, CamelJson);

            string callerAid = auth.Account!.AccountId.ToString("N");
            var lobby = lobbies.GetByMember(callerAid);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "您当前不在锦标赛对局中，无法观战同房对手。", code = "NOT_IN_TOURNAMENT" }, CamelJson, statusCode: 404);

            string normTarget = LobbyStore.NormalizeAccountKey(targetAccountId);
            if (lobbies.FindMember(lobby, normTarget) == null)
                return Results.Json(new { error = "目标玩家不属于您所在的锦标赛房间。", code = "TARGET_NOT_IN_LOBBY" }, CamelJson, statusCode: 404);

            long since = ParseSince(ctx);
            return await WriteBoardResultAsync(ctx, frameStore, normTarget, since, sessions, ct);
        });

        // ── 轨道 A-2：大厅/淘汰观战指定玩家棋盘（TournamentSpectateHttpClient）──
        app.MapGet("/tournament/spectate/{watchCode}/players/{targetAccountId}/board", async (
            string watchCode,
            string targetAccountId,
            HttpContext ctx,
            LobbyStore lobbies,
            SessionStore sessions,
            SpectateFrameStore frameStore,
            CancellationToken ct) =>
        {
            var lobby = lobbies.GetByCode(watchCode) ?? lobbies.Get(watchCode);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "未找到对应的观战房间。", code = "SPECTATE_NOT_FOUND" }, CamelJson, statusCode: 404);

            string normTarget = LobbyStore.NormalizeAccountKey(targetAccountId);
            if (lobbies.FindMember(lobby, normTarget) == null)
                return Results.Json(new { error = "目标玩家不在该锦标赛房间中。", code = "TARGET_NOT_IN_LOBBY" }, CamelJson, statusCode: 404);

            long since = ParseSince(ctx);
            return await WriteBoardResultAsync(ctx, frameStore, normTarget, since, sessions, ct);
        });

        // ── 轨道 A-3：实时棋盘增量 SSE 流（TournamentSpectateStreamUpdateDto）──
        app.MapGet("/tournament/spectate/{watchCode}/players/{targetAccountId}/board/events", async (
            string watchCode,
            string targetAccountId,
            HttpContext ctx,
            AccountStore accounts,
            LobbyStore lobbies,
            SpectateFrameStore frameStore,
            SseEventHub sseHub,
            CancellationToken ct) =>
        {
            var lobby = lobbies.GetByCode(watchCode) ?? lobbies.Get(watchCode);
            if (lobby == null || lobby.IsCancelled)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "未找到对应的观战房间。", code = "SPECTATE_NOT_FOUND" }, ct);
                return;
            }

            string normTarget = LobbyStore.NormalizeAccountKey(targetAccountId);
            if (lobbies.FindMember(lobby, normTarget) == null)
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new { error = "目标玩家不在该锦标赛房间中。", code = "TARGET_NOT_IN_LOBBY" }, ct);
                return;
            }

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            string ownerKey = auth.Account != null
                ? auth.Account.AccountId.ToString("N")
                : $"anon:{ctx.Connection.RemoteIpAddress}";

            using var sub = sseHub.TrySubscribe(SseEventHub.BoardTopic(normTarget), ownerKey, spectateLobbyId: lobby.LobbyId);
            if (sub == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "当前观战连接数已达上限，请稍后再试。", code = "SSE_LIMIT_EXCEEDED" }, ct);
                return;
            }

            SseEventHub.PrepareSseResponseHeaders(ctx.Response);
            if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": connected\n\n", ct))
                return;

            // 若客户端携带 since 游标，立即补发错过的增量帧
            long since = ParseSince(ctx);
            long lastSentRid = since;
            var backlog = frameStore.GetPendingFramesSince(normTarget, since);
            foreach (var f in backlog)
            {
                string json = JsonSerializer.Serialize(new
                {
                    RequestId = f.RequestId,
                    FromRequestId = f.FromRequestId,
                    MessagePayload = Convert.ToBase64String(f.MsgPackPayload),
                });
                if (!await SseEventHub.TryWriteRawAsync(ctx.Response, $"data: {json}\n\n", ct))
                    return;
                if (f.RequestId > lastSentRid) lastSentRid = f.RequestId;
            }

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
        });

        // ── 轨道 B-1：扩展 UI / 启动器结构化 JSON 实时盘面 DTO ─────────────────
        app.MapGet("/api/spectate/{code}/players/{targetAccountId}/board-dto", async (
            string code,
            string targetAccountId,
            LobbyStore lobbies,
            SessionStore sessions,
            Persistence db,
            GameDataRepository coreRepo,
            CancellationToken ct) =>
        {
            var lobby = lobbies.GetByCode(code) ?? lobbies.Get(code);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "未找到对应的观战房间。", code = "SPECTATE_NOT_FOUND" }, CamelJson, statusCode: 404);

            string normTarget = LobbyStore.NormalizeAccountKey(targetAccountId);
            var member = lobbies.FindMember(lobby, normTarget);
            if (member == null)
                return Results.Json(new { error = "目标玩家不在该房间中。", code = "TARGET_NOT_IN_LOBBY" }, CamelJson, statusCode: 404);

            var session = sessions.GetByAccount(normTarget);
            var handle = session != null ? SessionRegistry.Get(session.Sid) : null;
            string? lastReplayId = db.FindLatestReplayIdForAccount(normTarget);

            if (session == null || handle == null || handle.Run == null)
            {
                return Results.Json(new
                {
                    watchCode = lobby.Code,
                    accountId = member.AccountId,
                    username = member.Username,
                    hero = member.Hero,
                    day = member.CurrentDay,
                    hour = member.CurrentHour,
                    state = member.HasFinishedRun ? "Ended" : "Offline",
                    connectionState = member.ConnectionState,
                    stats = new
                    {
                        healthMax = 0,
                        prestige = member.FinalPrestige,
                        gold = 0,
                        income = 0,
                        level = 1,
                        victories = member.FinalVictories,
                        defeats = member.FinalDefeats,
                    },
                    unlockedSlotsMask = 0,
                    handItems = Array.Empty<object>(),
                    stashItems = Array.Empty<object>(),
                    skills = Array.Empty<object>(),
                    lastReplayId,
                    updatedAtUtc = member.LastSeenAt,
                }, CamelJson);
            }

            bool acquired = false;
            try
            {
                acquired = await session.Gate.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (!acquired)
                    return Results.Json(new { error = "目标玩家对局正在结算中，请稍后重试。", code = "BUSY" }, CamelJson, statusCode: 503);

                var run = handle.Run;
                var handItems = new List<object>();
                var stashItems = new List<object>();
                var skills = new List<object>();

                foreach (var c in run.Cards.Values.OrderBy(x => (int?)x.Socket ?? 99))
                {
                    if (c.Owner != ECombatantId.Player) continue;
                    string tplId = c.TemplateId.ToString("D");
                    var tpl = coreRepo.GetCard(tplId);
                    string nameZh = tpl != null ? coreRepo.GetLocalizedTitle(tpl) : tplId;
                    var attrs = c.Attributes.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
                    int cooldownMs = c.Attributes.GetValueOrDefault(ECardAttributeType.CooldownMax, 0);

                    if (c.Type == ECardType.Skill)
                    {
                        skills.Add(new
                        {
                            instanceId = c.InstanceId,
                            templateId = tplId,
                            nameZh,
                            tier = c.Tier.ToString(),
                            attributes = attrs,
                            tags = c.Tags.Select(t => t.ToString()).ToArray(),
                        });
                    }
                    else if (c.Type == ECardType.Item)
                    {
                        var itemDto = new
                        {
                            instanceId = c.InstanceId,
                            templateId = tplId,
                            nameZh,
                            size = c.Size.ToString(),
                            socket = c.Socket.HasValue ? (int)c.Socket.Value : -1,
                            tier = c.Tier.ToString(),
                            enchantment = c.Enchantment?.ToString(),
                            cooldownMs,
                            attributes = attrs,
                            tags = c.Tags.Select(t => t.ToString()).ToArray(),
                        };
                        if (c.Section == EInventorySection.Stash)
                            stashItems.Add(itemDto);
                        else
                            handItems.Add(itemDto);
                    }
                }

                return Results.Json(new
                {
                    watchCode = lobby.Code,
                    accountId = member.AccountId,
                    username = member.Username,
                    hero = (run.Hero == EHero.Common ? EHero.Vanessa : run.Hero).ToString(),
                    day = run.Day,
                    hour = run.Hour,
                    state = run.State.ToString(),
                    connectionState = member.ConnectionState,
                    stats = new
                    {
                        healthMax = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.HealthMax, 250),
                        prestige = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Prestige, 20),
                        gold = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Gold, 0),
                        income = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Income, 0),
                        level = run.PlayerAttributes.GetValueOrDefault(EPlayerAttributeType.Level, 1),
                        victories = run.Victories,
                        defeats = run.Defeats,
                    },
                    unlockedSlotsMask = (int)run.UnlockedSlots,
                    handItems,
                    stashItems,
                    skills,
                    lastReplayId,
                    updatedAtUtc = DateTime.UtcNow,
                }, CamelJson);
            }
            catch (ObjectDisposedException)
            {
                return Results.Json(new { error = "目标玩家对局会话已结束。", code = "SESSION_ENDED" }, CamelJson, statusCode: 404);
            }
            finally
            {
                if (acquired)
                {
                    try { session.Gate.Release(); } catch { }
                }
            }
        });

        // ── 轨道 B-2：历史战报与战斗回放 REST 接口 ─────────────────────────────
        app.MapGet("/api/replays", (HttpContext ctx, AccountStore accounts, Persistence db) =>
        {
            string modeId = ctx.Request.Query["modeId"].ToString().Trim();
            string cursor = ctx.Request.Query["cursor"].ToString().Trim();
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 100) : 20;
            bool mine = !string.Equals(ctx.Request.Query["mine"], "false", StringComparison.OrdinalIgnoreCase);

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            if (auth.HasExplicitFailure) return AccountAuthResolver.ErrorResult(auth, CamelJson);

            List<Persistence.ReplayRow> rows = (mine && auth.Account != null)
                ? db.QueryReplaysByAccount(auth.Account.AccountId.ToString("N"), modeId, cursor, limit)
                : db.QueryRecentReplays(modeId, limit);

            string? nextCursor = rows.Count >= limit ? rows[^1].CreatedAt.ToString("O") : null;
            return Results.Json(new
            {
                items = rows.Select(ToReplayListItemDto).ToArray(),
                nextCursor,
            }, CamelJson);
        });

        app.MapGet("/api/replays/me", (HttpContext ctx, AccountStore accounts, Persistence db) =>
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
            if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, CamelJson);

            string modeId = ctx.Request.Query["modeId"].ToString().Trim();
            string cursor = ctx.Request.Query["cursor"].ToString().Trim();
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 100) : 20;

            var rows = db.QueryReplaysByAccount(auth.Account!.AccountId.ToString("N"), modeId, cursor, limit);
            string? nextCursor = rows.Count >= limit ? rows[^1].CreatedAt.ToString("O") : null;
            return Results.Json(new
            {
                items = rows.Select(ToReplayListItemDto).ToArray(),
                nextCursor,
            }, CamelJson);
        });

        app.MapGet("/api/lobbies/{code}/replays", (string code, HttpContext ctx, LobbyStore lobbies, Persistence db) =>
        {
            var lobby = lobbies.GetByCode(code) ?? lobbies.Get(code);
            string lobbyId = lobby?.LobbyId ?? code;
            int? day = int.TryParse(ctx.Request.Query["day"], out int d) && d > 0 ? d : null;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 200) : 50;

            var rows = db.QueryReplaysByLobby(lobbyId, day, limit);
            return Results.Json(new
            {
                lobbyId,
                code = lobby?.Code ?? code,
                items = rows.Select(ToReplayListItemDto).ToArray(),
            }, CamelJson);
        });

        app.MapGet("/api/replays/{id}", (string id, Persistence db) =>
        {
            var row = db.LoadReplay(id);
            if (row == null)
                return Results.Json(new { error = "未找到对应的历史战报记录。", code = "REPLAY_NOT_FOUND" }, CamelJson, statusCode: 404);

            return Results.Json(new
            {
                replayId = row.ReplayId,
                runId = row.RunId,
                lobbyId = row.LobbyId,
                tournamentSessionId = row.TournamentSessionId,
                modeId = row.ModeId,
                combatType = row.CombatType,
                day = row.Day,
                hour = row.Hour,
                battleSeed = row.BattleSeed,
                p1AccountId = row.P1AccountId,
                p1Username = row.P1Username,
                p1Hero = row.P1Hero,
                p2AccountId = row.P2AccountId,
                p2Username = row.P2Username,
                p2Hero = row.P2Hero,
                winnerSide = row.WinnerSide,
                durationMs = row.DurationMs,
                p1RemainingHp = row.P1RemainingHp,
                p2RemainingHp = row.P2RemainingHp,
                p1DamageDealt = row.P1DamageDealt,
                p2DamageDealt = row.P2DamageDealt,
                p1Board = TryParseJsonElement(row.P1BoardJson),
                p2Board = TryParseJsonElement(row.P2BoardJson),
                timelineSummary = TryParseJsonElement(row.TimelineSummaryJson),
                hasMsgPackReplay = row.HasCombatSimBlob,
                createdAt = row.CreatedAt,
            }, CamelJson);
        });

        app.MapGet("/api/replays/{id}/msgpack", (string id, HttpContext ctx, Persistence db) =>
        {
            byte[]? payload;
            try
            {
                payload = db.LoadReplayMsgPack(id);
            }
            catch (InvalidDataException ex)
            {
                return Results.Json(new { error = ex.Message, code = "REPLAY_CORRUPTED" }, CamelJson, statusCode: 500);
            }

            if (payload == null || payload.Length == 0)
                return Results.Json(new { error = "该战报不包含可重播的二进制战斗帧，或已超过存储保留期被归档。", code = "REPLAY_BLOB_UNAVAILABLE" }, CamelJson, statusCode: 404);

            ctx.Response.Headers["Cache-Control"] = "public, max-age=86400";
            return Results.Bytes(payload, "application/msgpack");
        });
    }

    private static async Task<IResult> WriteBoardResultAsync(
        HttpContext ctx,
        SpectateFrameStore frameStore,
        string normTarget,
        long since,
        SessionStore sessions,
        CancellationToken ct)
    {
        var result = await frameStore.GetBoardResponseAsync(normTarget, since, sessions, ct).ConfigureAwait(false);
        if (!result.Found)
            return Results.Json(new { error = result.ErrorZh ?? "目标玩家当前不可观战。", code = "BOARD_UNAVAILABLE" }, CamelJson, statusCode: 404);

        ctx.Response.Headers["X-Request-Id"] = result.RequestId.ToString();
        ctx.Response.Headers["Cache-Control"] = "no-store";

        if (result.NotModified)
            return Results.StatusCode(StatusCodes.Status304NotModified);

        if (result.Payload == null || result.Payload.Length == 0)
            return Results.Json(new { error = "目标盘面数据暂时为空。", code = "EMPTY_BOARD" }, CamelJson, statusCode: 502);

        return Results.Bytes(result.Payload, "application/msgpack");
    }

    private static long ParseSince(HttpContext ctx)
        => long.TryParse(ctx.Request.Query["since"], out long v) && v > 0 ? v : 0L;

    private static object ToReplayListItemDto(Persistence.ReplayRow r) => new
    {
        replayId = r.ReplayId,
        runId = r.RunId,
        lobbyId = r.LobbyId,
        modeId = r.ModeId,
        combatType = r.CombatType,
        day = r.Day,
        hour = r.Hour,
        p1AccountId = r.P1AccountId,
        p1Username = r.P1Username,
        p1Hero = r.P1Hero,
        p2AccountId = r.P2AccountId,
        p2Username = r.P2Username,
        p2Hero = r.P2Hero,
        winnerSide = r.WinnerSide,
        durationMs = r.DurationMs,
        p1RemainingHp = r.P1RemainingHp,
        p2RemainingHp = r.P2RemainingHp,
        p1DamageDealt = r.P1DamageDealt,
        p2DamageDealt = r.P2DamageDealt,
        hasMsgPackReplay = r.HasCombatSimBlob,
        createdAt = r.CreatedAt,
    };

    private static object TryParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new { };
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return new { raw = json };
        }
    }
}

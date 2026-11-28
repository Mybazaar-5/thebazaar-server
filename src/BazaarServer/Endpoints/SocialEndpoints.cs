using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;

namespace BazaarServer.Endpoints;

/// <summary>
/// 战报回放、多模式排行榜与全服/房间系统播报统一写入器（实现 ISocialAndReplaySink）。
/// </summary>
public sealed class SocialAndReplayService : ISocialAndReplaySink
{
    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Persistence _db;
    private readonly SseEventHub _sseHub;

    public SocialAndReplayService(Persistence db, SseEventHub sseHub)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _sseHub = sseHub ?? throw new ArgumentNullException(nameof(sseHub));
    }

    public string SaveCombatReplay(CombatReplayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string replayId = string.IsNullOrWhiteSpace(record.ReplayId)
            ? $"rep_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid().ToString("N")[..8]}"
            : record.ReplayId.Trim();

        string p1Board = "{}";
        string p2Board = "{}";
        string modeId = "standard";
        int hour = 6;
        int p1RemHp = 0, p2RemHp = 0, p1Dmg = 0, p2Dmg = 0;

        if (!string.IsNullOrWhiteSpace(record.SummaryJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(record.SummaryJson);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetProp(root, "p1Board", out var b1) || TryGetProp(root, "playerItems", out b1)) p1Board = b1.GetRawText();
                    if (TryGetProp(root, "p2Board", out var b2) || TryGetProp(root, "opponentItems", out b2)) p2Board = b2.GetRawText();
                    if ((TryGetProp(root, "modeId", out var m) || TryGetProp(root, "mode", out m)) && m.ValueKind == JsonValueKind.String) modeId = m.GetString() ?? "standard";
                    if (TryGetProp(root, "hour", out var hr) && hr.TryGetInt32(out int hv)) hour = hv;
                    if ((TryGetProp(root, "p1RemainingHp", out var h1) || TryGetProp(root, "playerRemHp", out h1)) && h1.TryGetInt32(out int v1)) p1RemHp = v1;
                    if ((TryGetProp(root, "p2RemainingHp", out var h2) || TryGetProp(root, "opponentRemHp", out h2)) && h2.TryGetInt32(out int v2)) p2RemHp = v2;
                    if (TryGetProp(root, "p1DamageDealt", out var d1) && d1.TryGetInt32(out int vd1)) p1Dmg = vd1;
                    if (TryGetProp(root, "p2DamageDealt", out var d2) && d2.TryGetInt32(out int vd2)) p2Dmg = vd2;
                }
            }
            catch { }
        }

        string p2Aid = string.IsNullOrWhiteSpace(record.OpponentAccountId) ? "" : NormalizeKey(record.OpponentAccountId);
        string winnerSide = record.IsDraw ? "Draw" : (record.PlayerWon ? "Player1" : "Player2");

        _db.SaveReplay(new Persistence.ReplayRow(
            ReplayId: replayId,
            RunId: record.RunId ?? "",
            LobbyId: record.LobbyId ?? "",
            TournamentSessionId: record.TournamentSessionId?.Trim() ?? "",
            ModeId: modeId,
            CombatType: string.IsNullOrWhiteSpace(record.CombatType) ? "PVP" : record.CombatType,
            Day: (int)Math.Max(1u, record.Day),
            Hour: hour,
            BattleSeed: record.Seed,
            P1AccountId: NormalizeKey(record.PlayerAccountId),
            P1Username: record.PlayerUsername ?? "玩家",
            P1Hero: string.IsNullOrWhiteSpace(record.PlayerHero) ? "Vanessa" : record.PlayerHero,
            P2AccountId: p2Aid,
            P2Username: string.IsNullOrWhiteSpace(record.OpponentName) ? "对手" : record.OpponentName,
            P2Hero: string.IsNullOrWhiteSpace(record.OpponentHero) ? "Vanessa" : record.OpponentHero,
            WinnerSide: winnerSide,
            DurationMs: Math.Max(0, record.DurationMs),
            P1RemainingHp: p1RemHp,
            P2RemainingHp: p2RemHp,
            P1DamageDealt: p1Dmg,
            P2DamageDealt: p2Dmg,
            P1BoardJson: p1Board,
            P2BoardJson: p2Board,
            TimelineSummaryJson: string.IsNullOrWhiteSpace(record.SummaryJson) ? "{}" : record.SummaryJson,
            HasCombatSimBlob: record.CombatSimMsgPack is { Length: > 0 },
            CreatedAt: DateTime.UtcNow,
            VisualBindingsJson: record.VisualBindingsJson ?? ""), record.CombatSimMsgPack);

        return replayId;
    }

    public void RecordRunLeaderboard(RunLeaderboardSubmission submission)
    {
        if (submission == null) return;
        string modeKey = (submission.ModeKey ?? "standard").Trim().ToLowerInvariant();
        if (modeKey is "sandbox" or "sandbox_lab" or "sandboxlab")
            return; // 沙盒模式绝不上榜

        string aid = NormalizeKey(submission.AccountId);
        string hero = string.IsNullOrWhiteSpace(submission.Hero) ? "Vanessa" : submission.Hero.Trim();
        // D6-P1-11：使用确定性 runKey（优先真 RunId，兜底确定性键），彻底废除 DateTime.UtcNow.Ticks 重复刷榜
        string runKey = !string.IsNullOrWhiteSpace(submission.RunId)
            ? submission.RunId.Trim()
            : $"lb_{modeKey}_{aid}_{submission.DayReached}_{submission.Score}";
        string rawSeasonOrDate = (submission.SeasonOrDateKey ?? "").Trim();
        int primaryBoardScore = Math.Max(0, submission.Score);
        int primaryMetric = modeKey is "endless" or "endless_tower"
            ? submission.DayReached
            : submission.Victories;
        DateTime now = DateTime.UtcNow;

        var boardKeys = new List<string>();
        switch (modeKey)
        {
            case "endless":
            case "endless_tower":
                boardKeys.Add("endless:all");
                boardKeys.Add($"endless:hero:{hero}");
                break;
            case "daily":
            case "daily_challenge":
                string dKey = (string.IsNullOrWhiteSpace(rawSeasonOrDate) || rawSeasonOrDate.Equals("all", StringComparison.OrdinalIgnoreCase))
                    ? LeaderboardEndpoints.GetChinaTodayDateKey()
                    : rawSeasonOrDate;
                boardKeys.Add($"daily:{dKey}");
                break;
            case "boss_rush":
                boardKeys.Add("boss_rush:all");
                break;
            case "mutator":
            case "mutator_challenge":
                boardKeys.Add("mutator:all");
                break;
            case "cross_hero":
                boardKeys.Add("cross_hero:all");
                break;
            case "tournament":
                string tSeason = (string.IsNullOrWhiteSpace(rawSeasonOrDate) ||
                                  rawSeasonOrDate.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                  rawSeasonOrDate.Equals("S1", StringComparison.OrdinalIgnoreCase))
                    ? "s2"
                    : rawSeasonOrDate.ToLowerInvariant();
                boardKeys.Add($"tournament:{tSeason}");
                break;
            default:
                string rSeason = (string.IsNullOrWhiteSpace(rawSeasonOrDate) ||
                                  rawSeasonOrDate.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                                  rawSeasonOrDate.Equals("S1", StringComparison.OrdinalIgnoreCase))
                    ? "2"
                    : rawSeasonOrDate;
                boardKeys.Add($"season:{rSeason}:ranked");
                break;
        }

        foreach (string bk in boardKeys)
        {
            _db.UpsertLeaderboardEntry(new Persistence.LeaderboardEntryRow(
                Id: 0,
                BoardKey: bk,
                SeasonId: 2,
                ModeId: modeKey,
                AccountId: aid,
                Username: string.IsNullOrWhiteSpace(submission.Username) ? "玩家" : submission.Username,
                Hero: hero,
                RunId: runKey,
                Score: primaryBoardScore,
                PrimaryMetric: primaryMetric,
                SecondaryMetric: submission.Victories,
                ElapsedSeconds: Math.Max(0, submission.ElapsedSeconds),
                DayReached: submission.DayReached,
                Victories: submission.Victories,
                Defeats: submission.Defeats,
                Prestige: Math.Max(0, submission.Prestige),
                IsFirstAttempt: submission.IsFirstAttempt ?? true,
                MutatorsJson: string.IsNullOrWhiteSpace(submission.MutatorsJson) ? "[]" : submission.MutatorsJson,
                SummaryBoardJson: string.IsNullOrWhiteSpace(submission.FinalBoardSummaryJson) ? "{}" : submission.FinalBoardSummaryJson,
                AchievedAt: now));
        }

        // 自动触发高光里程碑全服系统战况播报
        if ((modeKey is "endless" or "endless_tower") && submission.DayReached >= 20)
        {
            PublishSystemBroadcast(
                null,
                "system_endless_record",
                $"🏔️ {submission.Username}（{hero}）在无尽爬塔中勇闯至第 {submission.DayReached} 层（积分 {primaryBoardScore}）！",
                new { username = submission.Username, hero, floor = submission.DayReached, score = primaryBoardScore });
        }
        else if (modeKey == "boss_rush" && submission.Victories >= 10)
        {
            PublishSystemBroadcast(
                null,
                "system_boss_rush_clear",
                $"⚔️ {submission.Username}（{hero}）成功通关 Boss 车轮战全部波次（积分 {primaryBoardScore}）！",
                new { username = submission.Username, hero, score = primaryBoardScore });
        }
        else if (submission.Victories >= 10 && modeKey is "standard" or "ranked" or "cross_hero" or "mutator" or "daily")
        {
            PublishSystemBroadcast(
                null,
                "system_ten_win",
                $"🌟 {submission.Username}（{hero}）斩获十胜完美通关！",
                new { username = submission.Username, hero, modeId = modeKey, score = primaryBoardScore });
        }
    }

    public void PublishSystemBroadcast(string? lobbyId, string msgType, string contentZh, object? meta = null)
    {
        string clean = AccountStore.SanitizeDisplayText(contentZh, 280);
        if (string.IsNullOrWhiteSpace(clean)) return;

        string channelType = string.IsNullOrWhiteSpace(lobbyId) ? "system" : "lobby";
        string channelId = string.IsNullOrWhiteSpace(lobbyId) ? "global" : lobbyId.Trim();
        string metaJson = meta != null ? JsonSerializer.Serialize(meta, CamelJson) : "{}";

        var row = _db.InsertChatMessage(
            channelType: channelType,
            channelId: channelId,
            msgType: string.IsNullOrWhiteSpace(msgType) ? "system_notice" : msgType,
            senderId: "",
            senderName: "系统",
            content: clean,
            metaJson: metaJson);

        string payload = JsonSerializer.Serialize(new
        {
            id = row.Id,
            channelType = row.ChannelType,
            channelId = row.ChannelId,
            msgType = row.MsgType,
            senderName = row.SenderName,
            content = row.Content,
            meta = meta ?? new { },
            createdAt = row.CreatedAt,
        }, CamelJson);

        _sseHub.PublishSocialEvent(string.IsNullOrWhiteSpace(lobbyId) ? "global" : lobbyId, "broadcast", payload, row.Id);
    }

    private static bool TryGetProp(JsonElement el, string name, out JsonElement val)
    {
        foreach (var p in el.EnumerateObject())
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                val = p.Value;
                return true;
            }
        }
        val = default;
        return false;
    }

    private static string NormalizeKey(string? id)
        => string.IsNullOrWhiteSpace(id) ? "" : (Guid.TryParse(id, out var g) ? g.ToString("N") : id.Trim().Replace("-", "").ToLowerInvariant());
}

/// <summary>
/// 社交聊天、系统战况播报、富社交 SSE 与轻量 /api/ping 测速端点：
/// - GET/POST /api/social/chat（世界 / 房间 / 观战频道，防刷屏限流 5 条/10s，落盘 chat_messages）
/// - GET/POST /api/lobbies/{id}/chat（房间频道便捷路径）
/// - GET /api/social/announcements（全服近期高光战况播报）
/// - GET /api/social/events（富社交命名 SSE，支持 Last-Event-ID 断线补发）
/// - GET /api/ping（零锁、零 DB 查询超低延迟 RTT 探针与服况统计）
/// </summary>
public static class SocialEndpoints
{
    public const int MaxChatContentLength = 200;
    public const int RateLimitMaxMessages = 5;
    public const int RateLimitWindowMs = 10_000;
    private const int MaxRateBucketEntries = 4096;

    private static readonly string DynamicServerVersion = ResolveServerVersion();

    private static string ResolveServerVersion()
    {
        var raw = typeof(SocialEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SocialEndpoints).Assembly.GetName().Version?.ToString(3)
            ?? "15.5.0";
        int plusIdx = raw.IndexOf('+');
        return plusIdx > 0 ? raw[..plusIdx] : raw;
    }

    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ConcurrentDictionary<Guid, Queue<long>> ChatRateBuckets = new();

    /// <summary>D8-P2-05：清理已过窗口期的空聊天限流桶，供定期维护任务与高水位溢出时复用。</summary>
    public static int PurgeStaleRateBuckets()
    {
        long now = Environment.TickCount64;
        int removed = 0;
        foreach (var (aid, q) in ChatRateBuckets)
        {
            bool empty;
            lock (q)
            {
                while (q.Count > 0 && now - q.Peek() > RateLimitWindowMs)
                    q.Dequeue();
                empty = q.Count == 0;
            }
            if (empty && ChatRateBuckets.TryRemove(aid, out _))
                removed++;
        }
        return removed;
    }

    public static void Map(WebApplication app)
    {
        // ── 轻量测速与服况探针（无鉴权、无锁、零 SQLite 查询，P99 < 1ms）──────
        app.MapGet("/api/ping", (HttpContext ctx, AccountStore accounts, SessionStore sessions, LobbyStore lobbies) =>
        {
            long serverTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long.TryParse(ctx.Request.Query["ts"], out long clientTs);

            // 可选记录玩家客户端测得的 RTT（仅内存更新，不触碰数据库锁）
            if (int.TryParse(ctx.Request.Query["rtt"], out int rttMs) && rttMs >= 0)
            {
                var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
                if (auth.Account != null)
                    accounts.TouchActivity(auth.Account, rttMs);
            }

            return Results.Json(new
            {
                ok = true,
                clientTs,
                serverTs,
                uptimeSec = Environment.TickCount64 / 1000,
                onlinePlayers = Math.Max(sessions.Count, accounts.CountOnlinePlayers()),
                activeRuns = sessions.Count,
                activeLobbies = lobbies.ActiveLobbyCount,
                serverVersion = DynamicServerVersion,
                protocolVersion = 2,
            }, CamelJson);
        });

        // ── 通用聊天拉取（世界/房间/观战频道，私密/密码/邀请/缺失房强制成员鉴权）──
        app.MapGet("/api/social/chat", (HttpContext ctx, AccountStore accounts, LobbyStore lobbies, Persistence db) =>
        {
            string channel = ctx.Request.Query["channel"].ToString().Trim().ToLowerInvariant();
            string lobbyIdOrCode = ctx.Request.Query["lobbyId"].ToString().Trim();
            long sinceId = long.TryParse(ctx.Request.Query["sinceId"], out long s) && s > 0 ? s : 0;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 200) : 50;

            bool explicitLobbyChannel = channel is "lobby" or "spectate";
            if (explicitLobbyChannel && string.IsNullOrWhiteSpace(lobbyIdOrCode))
            {
                return Results.Json(new
                {
                    error = "查询房间或观战聊天记录时必须指定 lobbyId。",
                    code = "LOBBY_ID_REQUIRED",
                }, CamelJson, statusCode: 400);
            }

            if (explicitLobbyChannel || (!string.IsNullOrWhiteSpace(lobbyIdOrCode) && !lobbyIdOrCode.Equals("global", StringComparison.OrdinalIgnoreCase)))
            {
                var denied = TryAuthorizeLobbyChannel(ctx, lobbyIdOrCode, isWrite: false, accounts, lobbies, out var lobby, out _);
                if (denied != null)
                    return denied.ToResult(CamelJson);

                string channelType = channel == "spectate" ? "spectate" : "lobby";
                string channelId = lobby!.LobbyId;
                var lobbyRows = db.QueryChatMessages(channelType, channelId, sinceId, limit);
                return Results.Json(new
                {
                    channelType,
                    channelId,
                    items = lobbyRows.Select(ToChatDto).ToArray(),
                }, CamelJson);
            }

            var rows = db.QueryChatMessages("global", "global", sinceId, limit);
            return Results.Json(new
            {
                channelType = "global",
                channelId = "global",
                items = rows.Select(ToChatDto).ToArray(),
            }, CamelJson);
        });

        // ── 通用聊天发送（强制鉴权 + 令牌桶限流 + TMP/BiDi 清洗 + 落盘 + SSE）──
        app.MapPost("/api/social/chat", async (HttpContext ctx, AccountStore accounts, LobbyStore lobbies, Persistence db, SseEventHub sseHub) =>
        {
            return await HandlePostChatAsync(ctx, explicitLobbyId: null, accounts, lobbies, db, sseHub);
        });

        // ── 房间专属聊天路由别名（私密/密码/邀请/缺失房强制成员鉴权）─────────
        app.MapGet("/api/lobbies/{id}/chat", (string id, HttpContext ctx, AccountStore accounts, LobbyStore lobbies, Persistence db) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.Json(new
                {
                    error = "查询房间聊天记录时必须指定房间 ID。",
                    code = "LOBBY_ID_REQUIRED",
                }, CamelJson, statusCode: 400);
            }

            var denied = TryAuthorizeLobbyChannel(ctx, id, isWrite: false, accounts, lobbies, out var lobby, out _);
            if (denied != null)
                return denied.ToResult(CamelJson);

            string channelId = lobby!.LobbyId;
            string channel = ctx.Request.Query["channel"].ToString().Trim().ToLowerInvariant();
            string channelType = channel == "spectate" ? "spectate" : "lobby";
            long sinceId = long.TryParse(ctx.Request.Query["sinceId"], out long s) && s > 0 ? s : 0;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 200) : 50;

            var rows = db.QueryChatMessages(channelType, channelId, sinceId, limit);
            return Results.Json(new
            {
                channelType,
                channelId,
                items = rows.Select(ToChatDto).ToArray(),
            }, CamelJson);
        });

        app.MapPost("/api/lobbies/{id}/chat", async (string id, HttpContext ctx, AccountStore accounts, LobbyStore lobbies, Persistence db, SseEventHub sseHub) =>
        {
            return await HandlePostChatAsync(ctx, explicitLobbyId: id, accounts, lobbies, db, sseHub);
        });

        // ── 全服高光战况系统播报历史 ─────────────────────────────────────────
        app.MapGet("/api/social/announcements", (HttpContext ctx, Persistence db) =>
        {
            long sinceId = long.TryParse(ctx.Request.Query["sinceId"], out long s) && s > 0 ? s : 0;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 100) : 30;
            var rows = db.QuerySystemAnnouncements(sinceId, limit);
            return Results.Json(new
            {
                items = rows.Select(ToChatDto).ToArray(),
            }, CamelJson);
        });

        // ── 富社交命名 SSE 流（chat / broadcast / barrier / takeover）──────────
        app.MapGet("/api/social/events", async (
            HttpContext ctx,
            AccountStore accounts,
            LobbyStore lobbies,
            Persistence db,
            SseEventHub sseHub,
            CancellationToken ct) =>
        {
            string scope = ctx.Request.Query["scope"].ToString().Trim().ToLowerInvariant();
            string lobbyParam = ctx.Request.Query["lobbyId"].ToString().Trim();
            long sinceId = 0;
            if (long.TryParse(ctx.Request.Headers["Last-Event-ID"].ToString(), out long lastId) && lastId > 0)
                sinceId = lastId;
            else if (long.TryParse(ctx.Request.Query["sinceId"], out long qSince) && qSince > 0)
                sinceId = qSince;

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false, allowLegacyUsernameFallback: false);
            string ownerKey = auth.Account != null
                ? auth.Account.AccountId.ToString("N")
                : $"anon:{ctx.Connection.RemoteIpAddress}";

            string topic;
            string? resolvedLobbyId = null;
            if (!string.IsNullOrWhiteSpace(lobbyParam))
            {
                // 显式指定 lobbyId 时严禁回退 lobbies.GetByMember(...)，防止错误/伪造房号静默串房
                var denied = TryAuthorizeLobbyChannel(ctx, lobbyParam, isWrite: false, accounts, lobbies, out var lobby, out var caller);
                if (denied != null)
                {
                    await denied.WriteAsync(ctx, ct);
                    return;
                }
                if (caller != null)
                    ownerKey = caller.AccountId.ToString("N");
                resolvedLobbyId = lobby!.LobbyId;
                topic = SseEventHub.SocialLobbyTopic(lobby.LobbyId);
            }
            else if (scope == "lobby")
            {
                // 仅在未指定 lobbyId 且 scope=lobby 时定位已认证本人的当前房间
                if (!auth.IsAuthenticated || auth.Account == null)
                {
                    var unauth = new ChannelAuthDenial(401, "AUTH_REQUIRED", "订阅当前房间社交事件流前请先登录账号。");
                    await unauth.WriteAsync(ctx, ct);
                    return;
                }

                var myLobby = lobbies.GetByMember(auth.Account.AccountId.ToString("N"));
                if (myLobby == null || myLobby.IsCancelled)
                {
                    var notFound = new ChannelAuthDenial(404, "LOBBY_NOT_FOUND", "您当前未加入任何活跃房间。");
                    await notFound.WriteAsync(ctx, ct);
                    return;
                }

                var denied = TryAuthorizeLobbyChannel(ctx, myLobby.LobbyId, isWrite: false, accounts, lobbies, out var lobby, out _);
                if (denied != null)
                {
                    await denied.WriteAsync(ctx, ct);
                    return;
                }

                resolvedLobbyId = lobby!.LobbyId;
                topic = SseEventHub.SocialLobbyTopic(lobby.LobbyId);
            }
            else
            {
                topic = SseEventHub.SocialGlobalTopic;
            }

            using var sub = sseHub.TrySubscribe(topic, ownerKey);
            if (sub == null)
            {
                ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await ctx.Response.WriteAsJsonAsync(new { error = "社交实时流连接数已达上限，请关闭多余窗口后重试。", code = "SSE_LIMIT_EXCEEDED" }, ct);
                return;
            }

            SseEventHub.PrepareSseResponseHeaders(ctx.Response);
            if (!await SseEventHub.TryWriteRawAsync(ctx.Response, ": connected\n\n", ct))
                return;

            // 若携带 Last-Event-ID / sinceId，从 SQLite 补发断线期间遗漏的聊天与播报
            if (sinceId > 0)
            {
                var missed = resolvedLobbyId != null
                    ? db.QueryChatMessages("lobby", resolvedLobbyId, sinceId, 50)
                    : db.QueryChatMessages("global", "global", sinceId, 50)
                        .Concat(db.QuerySystemAnnouncements(sinceId, 30))
                        .OrderBy(x => x.Id)
                        .Take(80)
                        .ToList();

                foreach (var row in missed)
                {
                    string evName = row.MsgType.StartsWith("system_", StringComparison.OrdinalIgnoreCase) ? "broadcast" : "chat";
                    string json = JsonSerializer.Serialize(ToChatDto(row), CamelJson);
                    var frame = new SseEventHub.SseFrame(evName, json, row.Id.ToString());
                    if (!await SseEventHub.TryWriteFrameAsync(ctx.Response, frame, ct))
                        return;
                }
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
    }

    private sealed record ChannelAuthDenial(int StatusCode, string Code, string Error)
    {
        public IResult ToResult(JsonSerializerOptions jsonOpts)
            => Results.Json(new { error = Error, code = Code }, jsonOpts, statusCode: StatusCode);

        public async Task WriteAsync(HttpContext ctx, CancellationToken ct)
        {
            ctx.Response.StatusCode = StatusCode;
            await ctx.Response.WriteAsJsonAsync(new { error = Error, code = Code }, ct);
        }
    }

    /// <summary>
    /// 统一房间聊天/SSE 频道鉴权守卫（D6-P1-05）：
    /// - 写操作：强制 Bearer 登录且必须为活跃房间成员（非成员返 403 LOBBY_CHAT_FORBIDDEN，缺失/取消返 404）；
    /// - 读操作：公开无密非邀请活跃房允许只读；私密/密码/邀请房强制 Bearer 登录（禁用 X-Username 降级）且必须为成员；
    /// - 不存在或已取消房间：强制 Bearer 登录（匿名返 401，已登录返 404），阻断历史私密房 lobbyId 匿名窃听。
    /// </summary>
    private static ChannelAuthDenial? TryAuthorizeLobbyChannel(
        HttpContext ctx,
        string? lobbyIdOrCode,
        bool isWrite,
        AccountStore accounts,
        LobbyStore lobbies,
        out LobbyStore.Lobby? lobby,
        out AccountStore.Account? caller)
    {
        caller = null;
        string cleanId = (lobbyIdOrCode ?? string.Empty).Trim();
        lobby = string.IsNullOrEmpty(cleanId) ? null : (lobbies.Get(cleanId) ?? lobbies.GetByCode(cleanId));

        bool isRestricted = lobby == null
            || lobby.IsCancelled
            || !lobby.IsPublic
            || lobby.HasPassword
            || lobby.IsInviteOnly;

        if (isWrite)
        {
            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true, allowLegacyUsernameFallback: false);
            if (!auth.IsAuthenticated || auth.Account == null)
                return new ChannelAuthDenial(auth.FailureStatusCode > 0 ? auth.FailureStatusCode : 401, auth.ErrorCode ?? "AUTH_REQUIRED", auth.ErrorMessageZh ?? "请先登录账号后再发言。");

            caller = auth.Account;
            if (lobby == null || lobby.IsCancelled)
                return new ChannelAuthDenial(404, "LOBBY_NOT_FOUND", "目标聊天房间不存在或已解散。");

            if (lobbies.FindMember(lobby, caller.AccountId.ToString("N")) == null)
                return new ChannelAuthDenial(403, "LOBBY_CHAT_FORBIDDEN", "您不是该房间的成员，无权在此房间发言。");

            return null;
        }

        // 只读公开无密非邀请活跃房放行（兼容大厅公开观战）
        if (!isRestricted)
        {
            var optAuth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false, allowLegacyUsernameFallback: false);
            caller = optAuth.IsAuthenticated ? optAuth.Account : null;
            return null;
        }

        // 私密/密码/邀请房或不存在/已归档房：一律先强制 Bearer 登录
        var strictAuth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true, allowLegacyUsernameFallback: false);
        if (!strictAuth.IsAuthenticated || strictAuth.Account == null)
            return new ChannelAuthDenial(strictAuth.FailureStatusCode > 0 ? strictAuth.FailureStatusCode : 401, strictAuth.ErrorCode ?? "AUTH_REQUIRED", strictAuth.ErrorMessageZh ?? "访问受限房间聊天频道前请先登录账号。");

        caller = strictAuth.Account;
        if (lobby == null || lobby.IsCancelled)
            return new ChannelAuthDenial(404, "LOBBY_NOT_FOUND", "目标房间不存在或已解散。");

        if (lobbies.FindMember(lobby, caller.AccountId.ToString("N")) == null)
            return new ChannelAuthDenial(403, "LOBBY_CHAT_FORBIDDEN", "该房间为受限房间，仅房间成员可查看聊天与实时事件流。");

        return null;
    }

    private static async Task<IResult> HandlePostChatAsync(
        HttpContext ctx,
        string? explicitLobbyId,
        AccountStore accounts,
        LobbyStore lobbies,
        Persistence db,
        SseEventHub sseHub)
    {
        var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true, allowLegacyUsernameFallback: false);
        if (!auth.IsAuthenticated) return AccountAuthResolver.ErrorResult(auth, CamelJson);
        var acc = auth.Account!;

        if (!CheckChatRateLimit(acc.AccountId))
        {
            return Results.Json(new
            {
                error = "发言过于频繁（每 10 秒最多发送 5 条消息），请稍后再试。",
                code = "CHAT_RATE_LIMITED",
            }, CamelJson, statusCode: StatusCodes.Status429TooManyRequests);
        }

        string raw = await ReadBoundedBodyAsync(ctx, 4 * 1024);
        string? content = null;
        string? channel = null;
        string? bodyLobbyId = explicitLobbyId;

        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in root.EnumerateObject())
                {
                    if (p.Name.Equals("content", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        content = p.Value.GetString();
                    else if (p.Name.Equals("channel", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                        channel = p.Value.GetString();
                    else if (string.IsNullOrWhiteSpace(bodyLobbyId) &&
                             (p.Name.Equals("lobbyId", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("code", StringComparison.OrdinalIgnoreCase)) &&
                             p.Value.ValueKind == JsonValueKind.String)
                        bodyLobbyId = p.Value.GetString();
                }
            }
        }
        catch
        {
            return Results.Json(new { error = "聊天消息 JSON 格式不正确。", code = "INVALID_BODY" }, CamelJson, statusCode: 400);
        }

        string clean = AccountStore.SanitizeDisplayText(content, MaxChatContentLength);
        if (string.IsNullOrWhiteSpace(clean))
            return Results.Json(new { error = "消息内容不能为空（且不允许仅包含非法标签字符）。", code = "EMPTY_CONTENT" }, CamelJson, statusCode: 400);

        string chNorm = (channel ?? "").Trim().ToLowerInvariant();
        bool explicitLobbyChannel = chNorm is "lobby" or "spectate";
        if (explicitLobbyChannel && string.IsNullOrWhiteSpace(bodyLobbyId))
        {
            return Results.Json(new
            {
                error = "向房间或观战频道发送消息时必须指定 lobbyId。",
                code = "LOBBY_ID_REQUIRED",
            }, CamelJson, statusCode: 400);
        }

        string channelType;
        string channelId;
        if (explicitLobbyChannel || (!string.IsNullOrWhiteSpace(bodyLobbyId) && !bodyLobbyId.Equals("global", StringComparison.OrdinalIgnoreCase)))
        {
            var denied = TryAuthorizeLobbyChannel(ctx, bodyLobbyId, isWrite: true, accounts, lobbies, out var lobby, out _);
            if (denied != null)
                return denied.ToResult(CamelJson);

            channelType = chNorm == "spectate" ? "spectate" : "lobby";
            channelId = lobby!.LobbyId;
        }
        else
        {
            channelType = "global";
            channelId = "global";
        }

        var row = db.InsertChatMessage(
            channelType: channelType,
            channelId: channelId,
            msgType: "chat",
            senderId: acc.AccountId.ToString("N"),
            senderName: acc.Username,
            content: clean,
            metaJson: "{}");

        var dto = ToChatDto(row);
        string payload = JsonSerializer.Serialize(dto, CamelJson);
        sseHub.PublishSocialEvent(channelType == "global" ? "global" : channelId, "chat", payload, row.Id);

        return Results.Json(dto, CamelJson);
    }

    private static bool CheckChatRateLimit(Guid accountId)
    {
        if (ChatRateBuckets.Count >= MaxRateBucketEntries && !ChatRateBuckets.ContainsKey(accountId))
            PurgeStaleRateBuckets();
        long now = Environment.TickCount64;
        var q = ChatRateBuckets.GetOrAdd(accountId, _ => new Queue<long>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > RateLimitWindowMs)
                q.Dequeue();
            if (q.Count >= RateLimitMaxMessages)
                return false;
            q.Enqueue(now);
            return true;
        }
    }

    private static object ToChatDto(Persistence.ChatMessageRow r) => new
    {
        id = r.Id,
        channelType = r.ChannelType,
        channelId = r.ChannelId,
        msgType = r.MsgType,
        senderId = r.SenderId,
        senderName = r.SenderName,
        content = r.Content,
        meta = TryParseMeta(r.MetaJson),
        createdAt = r.CreatedAt,
    };

    private static object TryParseMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new { };
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch { return new { }; }
    }

    private static async Task<string> ReadBoundedBodyAsync(HttpContext ctx, int maxBytes)
    {
        if (ctx.Request.Body == null) return string.Empty;
        using var ms = new MemoryStream();
        byte[] buffer = new byte[2048];
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
}

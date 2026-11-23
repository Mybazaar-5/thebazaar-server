using System.Collections.Concurrent;
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
                    if (TryGetProp(root, "p1Board", out var b1)) p1Board = b1.GetRawText();
                    if (TryGetProp(root, "p2Board", out var b2)) p2Board = b2.GetRawText();
                    if (TryGetProp(root, "modeId", out var m) && m.ValueKind == JsonValueKind.String) modeId = m.GetString() ?? "standard";
                    if (TryGetProp(root, "hour", out var hr) && hr.TryGetInt32(out int hv)) hour = hv;
                    if (TryGetProp(root, "p1RemainingHp", out var h1) && h1.TryGetInt32(out int v1)) p1RemHp = v1;
                    if (TryGetProp(root, "p2RemainingHp", out var h2) && h2.TryGetInt32(out int v2)) p2RemHp = v2;
                    if (TryGetProp(root, "p1DamageDealt", out var d1) && d1.TryGetInt32(out int vd1)) p1Dmg = vd1;
                    if (TryGetProp(root, "p2DamageDealt", out var d2) && d2.TryGetInt32(out int vd2)) p2Dmg = vd2;
                }
            }
            catch { }
        }

        _db.SaveReplay(new Persistence.ReplayRow(
            ReplayId: replayId,
            RunId: record.RunId ?? "",
            LobbyId: record.LobbyId ?? "",
            TournamentSessionId: "",
            ModeId: modeId,
            CombatType: string.IsNullOrWhiteSpace(record.CombatType) ? "PVP" : record.CombatType,
            Day: (int)Math.Max(1u, record.Day),
            Hour: hour,
            BattleSeed: record.Seed,
            P1AccountId: NormalizeKey(record.PlayerAccountId),
            P1Username: record.PlayerUsername ?? "玩家",
            P1Hero: string.IsNullOrWhiteSpace(record.PlayerHero) ? "Vanessa" : record.PlayerHero,
            P2AccountId: "",
            P2Username: string.IsNullOrWhiteSpace(record.OpponentName) ? "对手" : record.OpponentName,
            P2Hero: string.IsNullOrWhiteSpace(record.OpponentHero) ? "Vanessa" : record.OpponentHero,
            WinnerSide: record.PlayerWon ? "Player1" : "Player2",
            DurationMs: Math.Max(0, record.DurationMs),
            P1RemainingHp: p1RemHp,
            P2RemainingHp: p2RemHp,
            P1DamageDealt: p1Dmg,
            P2DamageDealt: p2Dmg,
            P1BoardJson: p1Board,
            P2BoardJson: p2Board,
            TimelineSummaryJson: string.IsNullOrWhiteSpace(record.SummaryJson) ? "{}" : record.SummaryJson,
            HasCombatSimBlob: record.CombatSimMsgPack is { Length: > 0 },
            CreatedAt: DateTime.UtcNow), record.CombatSimMsgPack);

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
        string runKey = $"lb_{modeKey}_{aid}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        string seasonOrDate = string.IsNullOrWhiteSpace(submission.SeasonOrDateKey) ? "all" : submission.SeasonOrDateKey.Trim();
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
                boardKeys.Add($"daily:{(seasonOrDate == "all" ? LeaderboardEndpoints.GetChinaTodayDateKey() : seasonOrDate)}");
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
                boardKeys.Add($"tournament:{(seasonOrDate == "all" ? "s2" : seasonOrDate)}");
                break;
            default:
                boardKeys.Add($"season:{(seasonOrDate == "all" ? "2" : seasonOrDate)}:ranked");
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
                ElapsedSeconds: 0,
                DayReached: submission.DayReached,
                Victories: submission.Victories,
                Defeats: submission.Defeats,
                Prestige: 0,
                IsFirstAttempt: true,
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

    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly ConcurrentDictionary<Guid, Queue<long>> ChatRateBuckets = new();

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
                serverVersion = "15.4.0",
                protocolVersion = 2,
            }, CamelJson);
        });

        // ── 通用聊天拉取（世界/房间/观战频道）────────────────────────────────
        app.MapGet("/api/social/chat", (HttpContext ctx, LobbyStore lobbies, Persistence db) =>
        {
            string channel = ctx.Request.Query["channel"].ToString().Trim().ToLowerInvariant();
            string lobbyIdOrCode = ctx.Request.Query["lobbyId"].ToString().Trim();
            long sinceId = long.TryParse(ctx.Request.Query["sinceId"], out long s) && s > 0 ? s : 0;
            int limit = int.TryParse(ctx.Request.Query["limit"], out int l) ? Math.Clamp(l, 1, 200) : 50;

            var (channelType, channelId) = ResolveChannel(channel, lobbyIdOrCode, lobbies);
            var rows = db.QueryChatMessages(channelType, channelId, sinceId, limit);
            return Results.Json(new
            {
                channelType,
                channelId,
                items = rows.Select(ToChatDto).ToArray(),
            }, CamelJson);
        });

        // ── 通用聊天发送（强制鉴权 + 令牌桶限流 + TMP/BiDi 清洗 + 落盘 + SSE）──
        app.MapPost("/api/social/chat", async (HttpContext ctx, AccountStore accounts, LobbyStore lobbies, Persistence db, SseEventHub sseHub) =>
        {
            return await HandlePostChatAsync(ctx, explicitLobbyId: null, accounts, lobbies, db, sseHub);
        });

        // ── 房间专属聊天路由别名 ─────────────────────────────────────────────
        app.MapGet("/api/lobbies/{id}/chat", (string id, HttpContext ctx, LobbyStore lobbies, Persistence db) =>
        {
            var lobby = lobbies.Get(id) ?? lobbies.GetByCode(id);
            string channelId = lobby?.LobbyId ?? id.Trim();
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

            var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: false);
            string ownerKey = auth.Account != null
                ? auth.Account.AccountId.ToString("N")
                : $"anon:{ctx.Connection.RemoteIpAddress}";

            string topic;
            string? resolvedLobbyId = null;
            if (scope == "lobby" || !string.IsNullOrWhiteSpace(lobbyParam))
            {
                var lobby = lobbies.Get(lobbyParam) ?? lobbies.GetByCode(lobbyParam)
                    ?? (auth.Account != null ? lobbies.GetByMember(auth.Account.AccountId.ToString("N")) : null);
                if (lobby == null)
                {
                    ctx.Response.StatusCode = 404;
                    await ctx.Response.WriteAsJsonAsync(new { error = "未找到指定的房间社交频道。", code = "LOBBY_NOT_FOUND" }, ct);
                    return;
                }
                resolvedLobbyId = lobby.LobbyId;
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

    private static async Task<IResult> HandlePostChatAsync(
        HttpContext ctx,
        string? explicitLobbyId,
        AccountStore accounts,
        LobbyStore lobbies,
        Persistence db,
        SseEventHub sseHub)
    {
        var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
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

        var (channelType, channelId) = ResolveChannel(channel, bodyLobbyId, lobbies);
        if (channelType is "lobby" or "spectate")
        {
            var lobby = lobbies.Get(channelId) ?? lobbies.GetByCode(channelId);
            if (lobby == null || lobby.IsCancelled)
                return Results.Json(new { error = "目标聊天房间不存在或已解散。", code = "LOBBY_NOT_FOUND" }, CamelJson, statusCode: 404);
            channelId = lobby.LobbyId;
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

    private static (string ChannelType, string ChannelId) ResolveChannel(string? channel, string? lobbyIdOrCode, LobbyStore lobbies)
    {
        string ch = (channel ?? "").Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(lobbyIdOrCode) && !lobbyIdOrCode.Equals("global", StringComparison.OrdinalIgnoreCase))
        {
            var lobby = lobbies.Get(lobbyIdOrCode) ?? lobbies.GetByCode(lobbyIdOrCode);
            string resolvedId = lobby?.LobbyId ?? lobbyIdOrCode.Trim();
            string resolvedType = ch == "spectate" ? "spectate" : "lobby";
            return (resolvedType, resolvedId);
        }
        return ("global", "global");
    }

    private static bool CheckChatRateLimit(Guid accountId)
    {
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

using System.Text.Json;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Storage;

namespace BazaarServer.Endpoints;

/// <summary>
/// 玩家自定义局内规则与流派目录 HTTP 端点。
/// 通过委托注入目录提供者，与 Wave 2 索引构建解耦。
/// </summary>
public static class CustomRuleEndpoints
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    private sealed class TendencyUpdateDto
    {
        public ulong SelectedArchetypeMask { get; set; }
    }

    private sealed class LockOpeningTendencyDto
    {
        public ulong SelectedArchetypeMask { get; set; }
        public string? RunId { get; set; }
    }

    public static void Map(
        WebApplication app,
        PlayerRuleStore store,
        AccountStore accounts,
        Func<object> catalogProvider,
        SessionStore? sessions = null,
        Persistence? persistence = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(accounts);

        app.MapGet("/custom-rules/catalog", () =>
        {
            object catalog = catalogProvider?.Invoke() ?? new { };
            return Results.Json(catalog, JsonOpts);
        });

        app.MapGet("/custom-rules/me", (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            return Results.Json(store.Get(accountId), JsonOpts);
        });

        app.MapPut("/custom-rules/me", async (HttpContext ctx) =>
        {
            CustomRunRuleSet? incoming;
            try
            {
                incoming = await JsonSerializer.DeserializeAsync<CustomRunRuleSet>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload for CustomRunRuleSet." });
            }

            if (incoming == null)
                return Results.BadRequest(new { error = "Request body cannot be null." });

            string accountId = ResolveAccountId(ctx, accounts);
            var updated = store.Set(accountId, incoming);
            return Results.Json(updated, JsonOpts);
        });

        app.MapPut("/custom-rules/me/tendency", async (HttpContext ctx) =>
        {
            TendencyUpdateDto? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<TendencyUpdateDto>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload for tendency." });
            }

            if (req == null)
                return Results.BadRequest(new { error = "Request body cannot be null." });

            string accountId = ResolveAccountId(ctx, accounts);
            var current = store.Get(accountId);
            current.SelectedArchetypeMask = req.SelectedArchetypeMask;
            var updated = store.Set(accountId, current);
            return Results.Json(updated, JsonOpts);
        });

        app.MapPost("/custom-rules/me/reset", (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            var reset = store.Reset(accountId);
            return Results.Json(reset, JsonOpts);
        });

        app.MapGet("/custom-rules/me/active-run-tendency", async (HttpContext ctx) =>
        {
            string accountId = ResolveAccountId(ctx, accounts);
            var session = FindSessionForAccount(sessions, accountId);
            if (session == null)
            {
                return Results.Json(new
                {
                    hasActiveRun = false,
                    allowTendencySelection = false,
                    tendencyLocked = true,
                    canLockNow = false,
                    selectedArchetypeMask = 0UL,
                }, JsonOpts);
            }

            try
            {
                await session.Gate.WaitAsync(ctx.RequestAborted);
            }
            catch (ObjectDisposedException)
            {
                return Results.Json(new
                {
                    hasActiveRun = false,
                    allowTendencySelection = false,
                    tendencyLocked = true,
                    canLockNow = false,
                    selectedArchetypeMask = 0UL,
                }, JsonOpts);
            }

            try
            {
                var handle = SessionRegistry.Get(session.Sid);
                if (handle == null || Persistence.IsTerminalState(handle.Run.State))
                {
                    return Results.Json(new
                    {
                        hasActiveRun = false,
                        allowTendencySelection = false,
                        tendencyLocked = true,
                        canLockNow = false,
                        selectedArchetypeMask = 0UL,
                    }, JsonOpts);
                }

                bool canLock = handle.Engine.CanLockOpeningTendency;
                return Results.Json(new
                {
                    hasActiveRun = true,
                    runId = handle.Run.RunId,
                    playMode = handle.Run.PlayMode.ToString(),
                    hero = handle.Run.Hero.ToString(),
                    day = handle.Run.Day,
                    hour = handle.Run.Hour,
                    state = handle.Run.State.ToString(),
                    allowTendencySelection = handle.Run.CustomRules?.AllowTendencySelection ?? true,
                    tendencyLocked = !canLock,
                    canLockNow = canLock,
                    selectedArchetypeMask = handle.Run.CustomRules?.SelectedArchetypeMask ?? 0UL,
                }, JsonOpts);
            }
            finally
            {
                try { session.Gate.Release(); } catch (ObjectDisposedException) { }
            }
        });

        app.MapPost("/custom-rules/me/lock-opening-tendency", async (HttpContext ctx) =>
        {
            LockOpeningTendencyDto? req;
            try
            {
                req = await JsonSerializer.DeserializeAsync<LockOpeningTendencyDto>(
                    ctx.Request.Body,
                    JsonOpts,
                    ctx.RequestAborted);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON payload." });
            }

            if (req == null)
                return Results.BadRequest(new { error = "Request body cannot be null." });

            string accountId = ResolveAccountId(ctx, accounts);
            var session = FindSessionForAccount(sessions, accountId);
            if (session == null)
            {
                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    error = "当前没有进行中的对局，请进入对局开场后再锁定倾向。",
                }, JsonOpts, statusCode: 404);
            }

            try
            {
                await session.Gate.WaitAsync(ctx.RequestAborted);
            }
            catch (ObjectDisposedException)
            {
                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    error = "会话已关闭。",
                }, JsonOpts, statusCode: 409);
            }

            try
            {
                var handle = SessionRegistry.Get(session.Sid);
                if (handle == null)
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        error = "当前会话未挂载运行态。",
                    }, JsonOpts, statusCode: 404);
                }

                if (!string.IsNullOrWhiteSpace(req.RunId) &&
                    !string.Equals(req.RunId.Trim(), handle.Run.RunId, StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        runId = handle.Run.RunId,
                        selectedArchetypeMask = handle.Run.CustomRules?.SelectedArchetypeMask ?? 0UL,
                        error = "当前活跃对局编号不匹配。",
                    }, JsonOpts, statusCode: 409);
                }

                bool ok = handle.Engine.TryLockOpeningTendency(
                    req.SelectedArchetypeMask,
                    out ulong lockedMask,
                    out string? error);

                if (!ok)
                {
                    return Results.Json(new
                    {
                        locked = true,
                        canLockNow = false,
                        runId = handle.Run.RunId,
                        selectedArchetypeMask = lockedMask,
                        error = error ?? "本局流派倾向已锁定，严禁对局途中修改。",
                    }, JsonOpts, statusCode: 409);
                }

                if (persistence != null)
                {
                    try
                    {
                        string snapJson = RunSnapshotSerializer.Serialize(
                            handle.Run,
                            startSelectionDone: handle.Engine.StartSelectionDone,
                            pendingLevelUp: handle.Run.PendingLevelUp);
                        persistence.SaveRunSnapshot(
                            handle.Run,
                            session.AccountId,
                            snapJson,
                            isCustomRule: handle.Run.IsCustomRuleRun);
                    }
                    catch { }
                }

                var currentPreset = store.Get(accountId);
                currentPreset.SelectedArchetypeMask = lockedMask;
                store.Set(accountId, currentPreset);

                return Results.Json(new
                {
                    locked = true,
                    canLockNow = false,
                    runId = handle.Run.RunId,
                    selectedArchetypeMask = lockedMask,
                }, JsonOpts);
            }
            finally
            {
                try { session.Gate.Release(); } catch (ObjectDisposedException) { }
            }
        });
    }

    private static Session? FindSessionForAccount(SessionStore? sessions, string accountId)
    {
        if (sessions == null || string.IsNullOrWhiteSpace(accountId))
            return null;

        if (Guid.TryParse(accountId, out var parsed))
        {
            return sessions.GetByAccount(parsed.ToString("N"))
                ?? sessions.GetByAccount(parsed.ToString("D"));
        }
        return sessions.GetByAccount(accountId);
    }

    /// <summary>
    /// 按 Bearer Token -> X-Account-Id (aid) -> X-Username (uid) -> guest 顺序解析账号标识。
    /// </summary>
    public static string ResolveAccountId(HttpContext ctx, AccountStore accounts)
    {
        string auth = ctx.Request.Headers["Authorization"].ToString();
        var byToken = accounts.FindByAccessToken(auth);
        if (byToken != null)
            return byToken.AccountId.ToString("D");

        string rawAccountId = ctx.Request.Headers["X-Account-Id"].ToString().Trim();
        if (string.IsNullOrEmpty(rawAccountId))
            rawAccountId = ctx.Request.Headers["aid"].ToString().Trim();

        if (!string.IsNullOrEmpty(rawAccountId))
        {
            var byId = accounts.FindByAccountId(rawAccountId);
            if (byId != null)
                return byId.AccountId.ToString("D");
        }

        string rawUsername = ctx.Request.Headers["X-Username"].ToString().Trim();
        if (string.IsNullOrEmpty(rawUsername))
            rawUsername = ctx.Request.Headers["uid"].ToString().Trim();

        if (!string.IsNullOrEmpty(rawUsername))
        {
            var byName = accounts.FindByUsername(rawUsername) ?? accounts.GetOrCreateByName(rawUsername);
            return byName.AccountId.ToString("D");
        }

        if (!string.IsNullOrEmpty(rawAccountId))
            return Guid.TryParse(rawAccountId, out var g) ? g.ToString("D") : rawAccountId;

        return accounts.GetOrCreateByName("guest").AccountId.ToString("D");
    }
}

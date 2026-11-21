using System.Text;
using System.Text.Json;
using BazaarCore.Models;
using BazaarServer.Accounts;
using BazaarServer.Game;

namespace BazaarServer.Endpoints;

/// <summary>
/// 沙盒构筑实验室 REST 端点（严格鉴权 + 沙盒模式门禁 + 通过 DI 调用 ISandboxEngineBridge）：
/// - POST /api/sandbox/grant-card
/// - POST /api/sandbox/modify-card
/// - POST /api/sandbox/set-stats
/// - POST /api/sandbox/test-combat
/// - POST /api/sandbox/preset-board
/// 任何非 SandboxLab 模式的活跃对局请求一律返回 409 SANDBOX_ONLY，绝不触碰 RunEngine 内部细节。
/// </summary>
public static class SandboxEndpoints
{
    private const int MaxSandboxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static void Map(WebApplication app)
    {
        app.MapPost("/api/sandbox/grant-card", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ISandboxEngineBridge bridge,
            CancellationToken ct) =>
        {
            var gate = ValidateSandboxAccess(ctx, accounts, sessions);
            if (!gate.Ok) return gate.ErrorResult!;

            var (reqOk, req, parseErr) = await TryReadJsonAsync<SandboxGrantCardRequest>(ctx, ct);
            if (!reqOk || req == null || string.IsNullOrWhiteSpace(req.TemplateId))
                return Results.Json(new { ok = false, error = parseErr ?? "请提供有效的卡牌模板 ID（templateId）。", code = "INVALID_REQUEST" }, CamelJson, statusCode: 400);

            var result = await bridge.GrantCardAsync(gate.AccountId!, req, ct).ConfigureAwait(false);
            return ToOpHttpResult(result);
        });

        app.MapPost("/api/sandbox/modify-card", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ISandboxEngineBridge bridge,
            CancellationToken ct) =>
        {
            var gate = ValidateSandboxAccess(ctx, accounts, sessions);
            if (!gate.Ok) return gate.ErrorResult!;

            var (reqOk, req, parseErr) = await TryReadJsonAsync<SandboxModifyCardRequest>(ctx, ct);
            if (!reqOk || req == null || string.IsNullOrWhiteSpace(req.InstanceId))
                return Results.Json(new { ok = false, error = parseErr ?? "请提供要修改的卡牌实例 ID（instanceId）。", code = "INVALID_REQUEST" }, CamelJson, statusCode: 400);

            var result = await bridge.ModifyCardAsync(gate.AccountId!, req, ct).ConfigureAwait(false);
            return ToOpHttpResult(result);
        });

        app.MapPost("/api/sandbox/set-stats", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ISandboxEngineBridge bridge,
            CancellationToken ct) =>
        {
            var gate = ValidateSandboxAccess(ctx, accounts, sessions);
            if (!gate.Ok) return gate.ErrorResult!;

            var (reqOk, req, parseErr) = await TryReadJsonAsync<SandboxSetStatsRequest>(ctx, ct);
            if (!reqOk || req == null)
                return Results.Json(new { ok = false, error = parseErr ?? "属性调整参数格式不正确。", code = "INVALID_REQUEST" }, CamelJson, statusCode: 400);

            var result = await bridge.SetStatsAsync(gate.AccountId!, req, ct).ConfigureAwait(false);
            return ToOpHttpResult(result);
        });

        app.MapPost("/api/sandbox/test-combat", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ISandboxEngineBridge bridge,
            CancellationToken ct) =>
        {
            var gate = ValidateSandboxAccess(ctx, accounts, sessions);
            if (!gate.Ok) return gate.ErrorResult!;

            var (reqOk, req, parseErr) = await TryReadJsonAsync<SandboxTestCombatRequest>(ctx, ct);
            if (!reqOk)
                return Results.Json(new { ok = false, error = parseErr ?? "模拟战斗请求格式不正确。", code = "INVALID_REQUEST" }, CamelJson, statusCode: 400);

            req ??= new SandboxTestCombatRequest();
            string src = (req.OpponentSource ?? "self_mirror").Trim().ToLowerInvariant();
            if (src is not ("self_mirror" or "mirror_self" or "monster" or "ghost"))
                return Results.Json(new { ok = false, error = "不支持的模拟对手类型，仅支持 self_mirror、monster 或 ghost。", code = "INVALID_OPPONENT_SOURCE" }, CamelJson, statusCode: 400);

            var normalizedReq = req with { OpponentSource = src == "mirror_self" ? "self_mirror" : src };
            var result = await bridge.RunTestCombatAsync(gate.AccountId!, normalizedReq, ct).ConfigureAwait(false);
            if (!result.Ok)
                return Results.Json(new { ok = false, error = result.MessageZh, messageZh = result.MessageZh, code = "SANDBOX_COMBAT_FAILED" }, CamelJson, statusCode: 409);

            return Results.Json(result, CamelJson);
        });

        app.MapPost("/api/sandbox/preset-board", async (
            HttpContext ctx,
            AccountStore accounts,
            SessionStore sessions,
            ISandboxEngineBridge bridge,
            CancellationToken ct) =>
        {
            var gate = ValidateSandboxAccess(ctx, accounts, sessions);
            if (!gate.Ok) return gate.ErrorResult!;

            var (reqOk, req, parseErr) = await TryReadJsonAsync<SandboxPresetBoardRequest>(ctx, ct);
            if (!reqOk || req == null)
                return Results.Json(new { ok = false, error = parseErr ?? "预设盘面导入参数格式不正确。", code = "INVALID_REQUEST" }, CamelJson, statusCode: 400);

            var result = await bridge.ApplyPresetBoardAsync(gate.AccountId!, req, ct).ConfigureAwait(false);
            return ToOpHttpResult(result);
        });
    }

    private sealed record SandboxGateResult(bool Ok, string? AccountId, IResult? ErrorResult);

    private static SandboxGateResult ValidateSandboxAccess(
        HttpContext ctx,
        AccountStore accounts,
        SessionStore sessions)
    {
        var auth = AccountAuthResolver.Resolve(ctx, accounts, requireAuth: true);
        if (!auth.IsAuthenticated)
            return new SandboxGateResult(false, null, AccountAuthResolver.ErrorResult(auth, CamelJson));

        string aid = auth.Account!.AccountId.ToString("N");
        var session = sessions.GetByAccount(aid);
        var handle = session != null ? SessionRegistry.Get(session.Sid) : null;
        if (handle == null || handle.Run == null)
        {
            return new SandboxGateResult(
                false,
                null,
                Results.Json(new
                {
                    ok = false,
                    error = "当前没有进行中的沙盒对局，请先开启沙盒构筑实验室对局。",
                    code = "SANDBOX_ONLY",
                }, CamelJson, statusCode: StatusCodes.Status409Conflict));
        }

        var rules = handle.Run.CustomRules;
        if (rules == null || rules.SpecialModeId != ESpecialModeId.SandboxLab)
        {
            return new SandboxGateResult(
                false,
                null,
                Results.Json(new
                {
                    ok = false,
                    error = "仅沙盒构筑实验室（SandboxLab）模式允许调用此接口，常规与竞技模式已严禁此操作。",
                    code = "SANDBOX_ONLY",
                }, CamelJson, statusCode: StatusCodes.Status409Conflict));
        }

        return new SandboxGateResult(true, aid, null);
    }

    private static IResult ToOpHttpResult(SandboxOpResult result)
    {
        if (!result.Ok)
        {
            return Results.Json(new
            {
                ok = false,
                error = result.MessageZh,
                messageZh = result.MessageZh,
                data = result.Data,
                code = "SANDBOX_OP_REJECTED",
            }, CamelJson, statusCode: StatusCodes.Status409Conflict);
        }

        return Results.Json(new
        {
            ok = true,
            messageZh = result.MessageZh,
            forceSyncRequired = true,
            data = result.Data,
        }, CamelJson);
    }

    private static async Task<(bool Ok, T? Value, string? ErrorZh)> TryReadJsonAsync<T>(HttpContext ctx, CancellationToken ct)
    {
        if (ctx.Request.Body == null)
            return (true, default, null);

        using var ms = new MemoryStream();
        byte[] buffer = new byte[4096];
        int total = 0;
        while (true)
        {
            int read = await ctx.Request.Body.ReadAsync(buffer, 0, Math.Min(buffer.Length, MaxSandboxBodyBytes - total + 1), ct).ConfigureAwait(false);
            if (read <= 0) break;
            total += read;
            if (total > MaxSandboxBodyBytes)
                return (false, default, $"请求体超出大小上限（最大 {MaxSandboxBodyBytes / 1024} KB）。");
            ms.Write(buffer, 0, read);
        }

        string raw = Encoding.UTF8.GetString(ms.ToArray());
        if (string.IsNullOrWhiteSpace(raw))
            return (true, default, null);

        try
        {
            var value = JsonSerializer.Deserialize<T>(raw, CamelJson);
            return (true, value, null);
        }
        catch
        {
            return (false, default, "请求 JSON 格式不合法，请检查字段类型。");
        }
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Mods;
using BazaarServer.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 5B 客户端网络同步、304 ETag 缓存、规则 HUD 与 Wave 2 补丁签名专项回归测试：
///   1. P2-02（RC-CLIENT-ETAG-IF-NONE-MATCH-MISSING）：
///      - NameAuth.GetJsonAsync / SendJsonAsync 可选 ifNoneMatch、ETag 响应头解析与 HTTP 304 NotModified 早退（不破坏既有调用方）；
///      - ModContentSyncClient 对 /api/mods/manifest、/api/mods/active-bindings、/api/localization/overlay 按端点缓存 ETag 并命中 304；
///      - ModContentSyncClient.NotifyUnboundInstanceSeen 按当前 revision 记忆已确认“原版壳卡”实例，消除每 250ms 重复全量轮询，并在 revision 变更或回主菜单时失效。
///   2. P1-15（RC-HUD-COLD-RESTART-RULES-NEVER-FETCHED，客户端侧对齐 #11 服务端契约）：
///      - NameAuth.ApplyAuthHeaders 同步附带 X-Bazaar-Username-Utf8（Uri.EscapeDataString）并与服务端 CustomRuleEndpoints.ResolveAccountId 精确闭环，不回退 guest；
///      - NameAuth.AuthSucceededEvent / AuthGeneration / AuthSucceeded 广播接线；
///      - CustomGameplayUI.ResolveInRunDisplayRules / SummarizeInRunDisplayRules 在活跃局优先展示服务端下发的权威 _activeRunRules（含 isCustomRuleRun 兜底），不误读大厅可变 _normalRules。
///   3. Wave 2 客户端 Harmony 补丁零签名失配核验：
///      - 通过 Wave2CLauncherAssemblyFixture + SafeHarmonyPatcher.ValidateHookMethod 校验 NameAuth、CombatPatch（P1-02/P2-04）与 CompatAuditPatch.Wave5Stability（P1-14/P2-06/P2-07/P2-08/P2-09/P2-11/P3-05）全部补丁签名零失配，并核验 GameInspector/GameDriver 关键修复入口。
/// </summary>
public sealed class Wave5B_ClientSyncAndStabilityTests : IClassFixture<Wave2CLauncherAssemblyFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _fx;

    public Wave5B_ClientSyncAndStabilityTests(Wave2CLauncherAssemblyFixture fx) => _fx = fx;

    // ── 通用反射与测试环境辅助 ─────────────────────────────────────────────────

    private sealed class LauncherServerScope : IDisposable
    {
        private readonly FieldInfo _serverBaseField;
        private readonly object? _oldServerBase;
        private readonly FieldInfo _userField;
        private readonly object? _oldUser;
        private readonly FieldInfo _aidField;
        private readonly object? _oldAid;
        private readonly FieldInfo _tokenField;
        private readonly object? _oldToken;
        private readonly FieldInfo _ignoreCacheField;
        private readonly object? _oldIgnoreCache;
        private readonly string _tempDir;

        public LauncherServerScope(Assembly launcherAsm, string serverBaseUrl, string username)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"wave5b_sync_cfg_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_tempDir);
            string tempCfgPath = Path.Combine(_tempDir, "test_launcher.cfg");

            Type pluginType = launcherAsm.GetType("BazaarLauncher.BazaarLauncherPlugin", throwOnError: true)!;
            _serverBaseField = pluginType.GetField("ServerBase", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            Assert.NotNull(_serverBaseField);
            _oldServerBase = _serverBaseField.GetValue(null);

            Assembly bepAsm = _serverBaseField.FieldType.Assembly;
            Type configFileType = bepAsm.GetType("BepInEx.Configuration.ConfigFile", throwOnError: true)!;
            object cfgFile = Activator.CreateInstance(configFileType, new object[] { tempCfgPath, false })!;
            MethodInfo bindStringMethod = configFileType
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Single(m => m.Name == "Bind"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 4
                    && m.GetParameters()[0].ParameterType == typeof(string)
                    && m.GetParameters()[3].ParameterType == typeof(string))
                .MakeGenericMethod(typeof(string));

            object boundEntry = bindStringMethod.Invoke(cfgFile, new object[] { "General", "ServerBase", serverBaseUrl, "wave5b-test" })!;
            _serverBaseField.SetValue(null, boundEntry);

            Type nameAuthType = launcherAsm.GetType("BazaarLauncher.NameAuth", throwOnError: true)!;
            _userField = nameAuthType.GetField("_currentUsername", BindingFlags.NonPublic | BindingFlags.Static)!;
            _aidField = nameAuthType.GetField("_currentAccountId", BindingFlags.NonPublic | BindingFlags.Static)!;
            _tokenField = nameAuthType.GetField("_currentAccessToken", BindingFlags.NonPublic | BindingFlags.Static)!;
            _ignoreCacheField = nameAuthType.GetField("_ignoreGameCachedAuthUntilRelogin", BindingFlags.NonPublic | BindingFlags.Static)!;

            _oldUser = _userField.GetValue(null);
            _oldAid = _aidField.GetValue(null);
            _oldToken = _tokenField.GetValue(null);
            _oldIgnoreCache = _ignoreCacheField.GetValue(null);

            _userField.SetValue(null, username);
            _aidField.SetValue(null, string.Empty);
            _tokenField.SetValue(null, string.Empty);
            _ignoreCacheField.SetValue(null, false);
        }

        public void Dispose()
        {
            try { _serverBaseField.SetValue(null, _oldServerBase); } catch { }
            try { _userField.SetValue(null, _oldUser); } catch { }
            try { _aidField.SetValue(null, _oldAid); } catch { }
            try { _tokenField.SetValue(null, _oldToken); } catch { }
            try { _ignoreCacheField.SetValue(null, _oldIgnoreCache); } catch { }
            try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private static async Task<object> InvokeTaskWithResultAsync(MethodInfo method, object? instance, object?[] args)
    {
        var task = (Task)method.Invoke(instance, args)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }

    private bool InvokeValidateHookMethod(MethodBase target, MethodInfo hook, string role, out string error)
    {
        Type patcherType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.SafeHarmonyPatcher", throwOnError: true)!;
        MethodInfo? validateM = patcherType.GetMethod("ValidateHookMethod", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(validateM);

        object?[] args = { target, hook, role, null };
        bool ok = (bool)validateM!.Invoke(null, args)!;
        error = args[3]?.ToString() ?? string.Empty;
        return ok;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 4000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // ── 1. P2-02：NameAuth 条件请求 If-None-Match / ETag / 304 NotModified ─────

    [Fact]
    public async Task P2_02_NameAuth_GetJsonAsync_SendsIfNoneMatch_AndParsesEtagAnd304NotModified()
    {
        string currentEtag = "\"probe-v1-a1b2c3\"";
        string? lastIfNoneMatch = null;
        string? lastUtf8UserHeader = null;
        string? lastLegacyUserHeader = null;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.MapGet("/test/etag-probe", (HttpContext ctx) =>
        {
            lastIfNoneMatch = ctx.Request.Headers[HeaderNames.IfNoneMatch].ToString();
            lastUtf8UserHeader = ctx.Request.Headers[CustomRuleEndpoints.UsernameUtf8Header].ToString();
            lastLegacyUserHeader = ctx.Request.Headers["X-Username"].ToString();

            ctx.Response.Headers[HeaderNames.ETag] = currentEtag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";

            if (ModEndpoints.MatchesIfNoneMatchHeader(lastIfNoneMatch, currentEtag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.Json(new { ok = true, version = currentEtag });
        });

        await app.StartAsync();
        string baseUrl = app.Urls.First();

        using var scope = new LauncherServerScope(_fx.LauncherAssembly, baseUrl, "破风");

        Type nameAuthType = _fx.LauncherAssembly.GetType("BazaarLauncher.NameAuth", throwOnError: true)!;
        MethodInfo? getJsonM = nameAuthType.GetMethod("GetJsonAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(getJsonM);

        // 校验第 3 参数为可选 string ifNoneMatch = null，确保不破坏既有 1 参/2 参调用方
        ParameterInfo[] ps = getJsonM!.GetParameters();
        Assert.Equal(3, ps.Length);
        Assert.Equal(typeof(string), ps[2].ParameterType);
        Assert.True(ps[2].IsOptional);
        Assert.Null(ps[2].DefaultValue);

        // 第 1 次：不传 If-None-Match -> HTTP 200 + ETag + 同步附带 UTF-8 用户名编码头
        object resp1 = await InvokeTaskWithResultAsync(getJsonM, null, new object?[] { "/test/etag-probe", CancellationToken.None, null });
        Type respType = resp1.GetType();
        Assert.True((bool)respType.GetProperty("Ok")!.GetValue(resp1)!);
        Assert.False((bool)respType.GetProperty("NotModified")!.GetValue(resp1)!);
        Assert.Equal(200, (int)respType.GetProperty("StatusCode")!.GetValue(resp1)!);
        Assert.Equal(currentEtag, (string)respType.GetProperty("ETag")!.GetValue(resp1)!);
        Assert.Contains("probe-v1-a1b2c3", (string)respType.GetProperty("Body")!.GetValue(resp1)!, StringComparison.Ordinal);
        Assert.Equal(string.Empty, (string)respType.GetProperty("Error")!.GetValue(resp1)!);
        Assert.Equal(Uri.EscapeDataString("破风"), lastUtf8UserHeader);
        Assert.Equal(Uri.EscapeDataString("破风"), lastLegacyUserHeader);

        // 第 2 次：携带上次 ETag -> 服务端命中 304 Not Modified，零正文零报错
        object resp2 = await InvokeTaskWithResultAsync(getJsonM, null, new object?[] { "/test/etag-probe", CancellationToken.None, currentEtag });
        Assert.False((bool)respType.GetProperty("Ok")!.GetValue(resp2)!);
        Assert.True((bool)respType.GetProperty("NotModified")!.GetValue(resp2)!);
        Assert.Equal(304, (int)respType.GetProperty("StatusCode")!.GetValue(resp2)!);
        Assert.Equal(currentEtag, (string)respType.GetProperty("ETag")!.GetValue(resp2)!);
        Assert.Equal(string.Empty, (string)respType.GetProperty("Body")!.GetValue(resp2)!);
        Assert.Equal(string.Empty, (string)respType.GetProperty("Error")!.GetValue(resp2)!);
        Assert.Equal(currentEtag, lastIfNoneMatch);

        // 第 3 次：服务端推进 ETag，客户端携带旧 ETag -> 命中 200 OK 并返回新 ETag
        currentEtag = "\"probe-v2-d4e5f6\"";
        object resp3 = await InvokeTaskWithResultAsync(getJsonM, null, new object?[] { "/test/etag-probe", CancellationToken.None, "\"probe-v1-a1b2c3\"" });
        Assert.True((bool)respType.GetProperty("Ok")!.GetValue(resp3)!);
        Assert.False((bool)respType.GetProperty("NotModified")!.GetValue(resp3)!);
        Assert.Equal(200, (int)respType.GetProperty("StatusCode")!.GetValue(resp3)!);
        Assert.Equal(currentEtag, (string)respType.GetProperty("ETag")!.GetValue(resp3)!);
    }

    // ── 2. P2-02：ModContentSyncClient 全端点 304 缓存与原版壳卡 revision 记忆 ─

    [Fact]
    public async Task P2_02_ModContentSyncClient_CachesEndpointEtags_Hits304_AndMemorizesVanillaDonorByRevision()
    {
        const string donorTemplateGuid = "11111111-2222-3333-4444-555555555555";
        int activeRev = 1;
        string overlayEtag = "\"ov-sha-v1\"";
        string manifestEtag = "\"mf-hash-v1\"";

        int overlay200 = 0, overlay304 = 0;
        int manifest200 = 0, manifest304 = 0;
        int active200 = 0, active304 = 0;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();

        app.MapGet("/api/localization/overlay", (HttpContext ctx) =>
        {
            ctx.Response.Headers[HeaderNames.ETag] = overlayEtag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            if (ModEndpoints.MatchesIfNoneMatchHeader(ctx.Request.Headers[HeaderNames.IfNoneMatch].ToString(), overlayEtag))
            {
                Interlocked.Increment(ref overlay304);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            Interlocked.Increment(ref overlay200);
            return Results.Json(new { locale = "zh-CN", entries = new Dictionary<string, string> { ["k1"] = "星港" } });
        });

        app.MapGet("/api/mods/manifest", (HttpContext ctx) =>
        {
            ctx.Response.Headers[HeaderNames.ETag] = manifestEtag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            if (ModEndpoints.MatchesIfNoneMatchHeader(ctx.Request.Headers[HeaderNames.IfNoneMatch].ToString(), manifestEtag))
            {
                Interlocked.Increment(ref manifest304);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            Interlocked.Increment(ref manifest200);
            return Results.Json(new
            {
                modSetHash = "modset-v1",
                manifestHash = "mf-hash-v1",
                mods = new[]
                {
                    new { id = "bazaar-chronicles-sample", name = "星港编年史", version = "1.0.0", enabled = true, cardCount = 1 }
                },
                assets = Array.Empty<object>(),
                visualBindings = new[]
                {
                    new
                    {
                        modId = "bazaar-chronicles-sample",
                        customId = "bazaar-chronicles-sample:aether_bulwark_lantern",
                        baseVisualTemplateId = donorTemplateGuid,
                        cardType = "Item",
                        size = "Medium",
                        nameZh = "以太壁垒提灯",
                        isCustomBorrowShell = true
                    }
                }
            });
        });

        app.MapGet("/api/mods/active-bindings", (HttpContext ctx) =>
        {
            int curRev = Volatile.Read(ref activeRev);
            string etag = ModEndpoints.ComputeActiveBindingsEtag("run_wave5b_01", curRev, "modset-v1");
            ctx.Response.Headers["X-Visual-Bindings-Rev"] = curRev.ToString();
            ctx.Response.Headers[HeaderNames.ETag] = etag;
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";

            if (ModEndpoints.MatchesIfNoneMatchHeader(ctx.Request.Headers[HeaderNames.IfNoneMatch].ToString(), etag))
            {
                Interlocked.Increment(ref active304);
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            Interlocked.Increment(ref active200);
            return Results.Json(new
            {
                ok = true,
                runId = "run_wave5b_01",
                revision = curRev,
                modSetHash = "modset-v1",
                bindingCount = 1,
                bindings = new Dictionary<string, object>
                {
                    ["itm_mod_lantern_01"] = new
                    {
                        instanceId = "itm_mod_lantern_01",
                        modId = "bazaar-chronicles-sample",
                        customId = "bazaar-chronicles-sample:aether_bulwark_lantern",
                        baseVisualTemplateId = donorTemplateGuid,
                        cardType = "Item",
                        nameZh = "以太壁垒提灯",
                        isCustomBorrowShell = true
                    }
                },
                templateCatalog = Array.Empty<object>()
            });
        });

        await app.StartAsync();
        string baseUrl = app.Urls.First();

        using var scope = new LauncherServerScope(_fx.LauncherAssembly, baseUrl, "破风");

        Type syncType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.ModContentSyncClient", throwOnError: true)!;
        object syncInst = syncType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

        MethodInfo syncAllM = syncType.GetMethod("SyncAllAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo syncActiveM = syncType.GetMethod("SyncActiveBindingsAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo notifyUnboundM = syncType.GetMethod("NotifyUnboundInstanceSeen", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo notifyRevM = syncType.GetMethod("NotifyVisualBindingsRevObserved", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo returnMenuM = syncType.GetMethod("OnReturnedToMainMenu", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        FieldInfo autoTickField = syncType.GetField("_lastAutoBindingsTickMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo revTickField = syncType.GetField("_lastRevSyncTickMs", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo inFlightField = syncType.GetField("_activeBindingsInFlight", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo checkedVanillaField = syncType.GetField("_checkedVanillaInstances", BindingFlags.NonPublic | BindingFlags.Instance)!;

        try
        {
            returnMenuM.Invoke(syncInst, null);

            // 1. 首次全量同步：3 个端点均拉取 200 并缓存各自 ETag
            bool ok1 = (bool)await InvokeTaskWithResultAsync(syncAllM, syncInst, new object?[] { false, CancellationToken.None });
            Assert.True(ok1);
            Assert.Equal(1, Volatile.Read(ref overlay200));
            Assert.Equal(1, Volatile.Read(ref manifest200));
            Assert.Equal(1, Volatile.Read(ref active200));
            Assert.Equal(0, Volatile.Read(ref overlay304));
            Assert.Equal(0, Volatile.Read(ref manifest304));
            Assert.Equal(0, Volatile.Read(ref active304));

            // 2. 再次全量同步（服务端无变化）：3 个端点全部携带 If-None-Match 命中 304 Not Modified，200 次数零增长
            bool ok2 = (bool)await InvokeTaskWithResultAsync(syncAllM, syncInst, new object?[] { false, CancellationToken.None });
            Assert.True(ok2);
            Assert.Equal(1, Volatile.Read(ref overlay200));
            Assert.Equal(1, Volatile.Read(ref manifest200));
            Assert.Equal(1, Volatile.Read(ref active200));
            Assert.Equal(1, Volatile.Read(ref overlay304));
            Assert.Equal(1, Volatile.Read(ref manifest304));
            Assert.Equal(1, Volatile.Read(ref active304));

            // 3. 见到原版借壳卡 itm_vanilla_globe_01：首次向服务端核实（命中 304），随后记入当前 revision 的原版壳卡集合
            autoTickField.SetValue(syncInst, 0L);
            notifyUnboundM.Invoke(syncInst, new object[] { "itm_vanilla_globe_01", donorTemplateGuid });
            await WaitUntilAsync(() => Volatile.Read(ref active304) >= 2 && (int)inFlightField.GetValue(syncInst)! == 0);

            Assert.Equal(2, Volatile.Read(ref active304));
            Assert.Equal(1, Volatile.Read(ref active200));
            var checkedSet = (HashSet<string>)checkedVanillaField.GetValue(syncInst)!;
            Assert.Contains("itm_vanilla_globe_01", checkedSet);

            // 同 revision 下反复见到同一张原版壳卡 10 次（即使绕过 250ms 防抖窗口）：必须 0 次新增 HTTP 请求
            for (int i = 0; i < 10; i++)
            {
                autoTickField.SetValue(syncInst, 0L);
                notifyUnboundM.Invoke(syncInst, new object[] { "itm_vanilla_globe_01", donorTemplateGuid });
            }
            await Task.Delay(120);
            Assert.Equal(2, Volatile.Read(ref active304));
            Assert.Equal(1, Volatile.Read(ref active200));

            // 4. 服务端 revision 推进至 2：NotifyVisualBindingsRevObserved 失效旧原版壳卡记忆，并触发 200 拉取新 revision
            Volatile.Write(ref activeRev, 2);
            revTickField.SetValue(syncInst, 0L);
            notifyRevM.Invoke(syncInst, new object[] { "2" });
            await WaitUntilAsync(() => Volatile.Read(ref active200) >= 2 && (int)inFlightField.GetValue(syncInst)! == 0);

            Assert.Equal(2, Volatile.Read(ref active200));
            Assert.DoesNotContain("itm_vanilla_globe_01", checkedSet);

            // revision=2 稳态下再同步一次，应立即命中新 ETag 的 304
            await (Task)syncActiveM.Invoke(syncInst, new object?[] { null, CancellationToken.None, null, null })!;
            Assert.Equal(3, Volatile.Read(ref active304));
            Assert.Equal(2, Volatile.Read(ref active200));

            // 5. 返回主菜单清空活跃绑定 ETag：下一次进局拉取绝不误用旧局 304，必须拉 200 重建
            returnMenuM.Invoke(syncInst, null);
            await (Task)syncActiveM.Invoke(syncInst, new object?[] { null, CancellationToken.None, null, null })!;
            Assert.Equal(3, Volatile.Read(ref active200));
        }
        finally
        {
            returnMenuM.Invoke(syncInst, null);
        }
    }

    // ── 3. P1-15：NameAuth UTF-8 中文用户名头与鉴权成功通知闭环 ─────────────────

    [Fact]
    public void P1_15_NameAuth_ApplyAuthHeaders_ResolvesChineseAccountWithoutGuestFallback_AndPublishesAuthEvent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"wave5b_p115_db_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            using var db = new Persistence(dbPath);
            var accounts = new AccountStore(db);
            var pofeng = accounts.GetOrCreateByName("破风");
            var guest = accounts.GetOrCreateByName("guest");

            using var scope = new LauncherServerScope(_fx.LauncherAssembly, "http://127.0.0.1:8080", "破风");

            Type nameAuthType = _fx.LauncherAssembly.GetType("BazaarLauncher.NameAuth", throwOnError: true)!;
            MethodInfo applyHeadersM = nameAuthType.GetMethod("ApplyAuthHeaders", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            FieldInfo utf8HeaderConst = nameAuthType.GetField("UsernameUtf8Header", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

            // 1. 常量与服务端 #11 契约 100% 一致
            Assert.Equal(CustomRuleEndpoints.UsernameUtf8Header, (string)utf8HeaderConst.GetValue(null)!);

            // 2. 模拟冷重启登录前（无 Bearer Token / 无 X-Account-Id）的请求头构造
            using var req = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/custom-rules/me");
            applyHeadersM.Invoke(null, new object[] { req });

            Assert.True(req.Headers.TryGetValues(CustomRuleEndpoints.UsernameUtf8Header, out var utf8Vals));
            string utf8HeaderVal = Assert.Single(utf8Vals!);
            Assert.Equal(Uri.EscapeDataString("破风"), utf8HeaderVal);
            Assert.All(utf8HeaderVal, ch => Assert.True(ch <= 0x7F, "X-Bazaar-Username-Utf8 必须为纯 ASCII 安全头值"));

            // 3. 将客户端实际发出的请求头原样注入服务端 DefaultHttpContext，校验服务端精确解析到「破风」而非 guest
            var httpCtx = new DefaultHttpContext();
            foreach (KeyValuePair<string, IEnumerable<string>> h in req.Headers)
            {
                httpCtx.Request.Headers[h.Key] = string.Join(",", h.Value);
            }

            Assert.Equal("破风", CustomRuleEndpoints.ResolveUsernameFromHeaders(httpCtx));
            string resolvedAccountId = CustomRuleEndpoints.ResolveAccountId(httpCtx, accounts);
            Assert.Equal(pofeng.AccountId.ToString("D"), resolvedAccountId);
            Assert.NotEqual(guest.AccountId.ToString("D"), resolvedAccountId);

            // 4. 校验 NameAuth.AuthSucceededEvent、AuthGeneration 递增与静态事件 + ModEventBus 完整收发
            Type authEvtType = nameAuthType.GetNestedType("AuthSucceededEvent", BindingFlags.NonPublic | BindingFlags.Public)!;
            Assert.NotNull(authEvtType);
            Assert.True(authEvtType.IsValueType, "AuthSucceededEvent 须为值类型结构体以供 ModEventBus 零装箱发布");

            PropertyInfo authGenProp = nameAuthType.GetProperty("AuthGeneration", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            EventInfo authEvent = nameAuthType.GetEvent("AuthSucceeded", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;
            MethodInfo notifyAuthM = nameAuthType.GetMethod("NotifyAuthSucceeded", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(authGenProp);
            Assert.NotNull(authEvent);
            Assert.NotNull(notifyAuthM);

            int genBefore = (int)authGenProp.GetValue(null)!;
            int callbackHits = 0;
            string? receivedUser = null;
            int receivedGen = 0;

            // 通过表达式动态构造 Action<AuthSucceededEvent> 订阅 NameAuth.AuthSucceeded 与独立 ModEventBus
            Type actionOfEvtType = typeof(Action<>).MakeGenericType(authEvtType);
            var param = System.Linq.Expressions.Expression.Parameter(authEvtType, "e");
            MethodInfo recordHitM = typeof(Wave5B_ClientSyncAndStabilityTests)
                .GetMethod(nameof(RecordAuthHitHelper), BindingFlags.NonPublic | BindingFlags.Static)!;

            var hitBox = new AuthHitRecorder();
            var handlerDelegate = System.Linq.Expressions.Expression.Lambda(
                actionOfEvtType,
                System.Linq.Expressions.Expression.Call(
                    recordHitM,
                    System.Linq.Expressions.Expression.Constant(hitBox),
                    System.Linq.Expressions.Expression.Convert(param, typeof(object))),
                param).Compile();

            MethodInfo addAuthHandlerM = authEvent.GetAddMethod(nonPublic: true)!;
            MethodInfo removeAuthHandlerM = authEvent.GetRemoveMethod(nonPublic: true)!;
            Assert.NotNull(addAuthHandlerM);
            Assert.NotNull(removeAuthHandlerM);

            addAuthHandlerM.Invoke(null, new object[] { handlerDelegate });
            try
            {
                notifyAuthM.Invoke(null, new object?[] { pofeng.AccountId.ToString("N"), "破风" });
                int genAfter = (int)authGenProp.GetValue(null)!;
                Assert.Equal(genBefore + 1, genAfter);

                callbackHits = hitBox.Count;
                receivedUser = hitBox.LastUsername;
                receivedGen = hitBox.LastGeneration;
                Assert.Equal(1, callbackHits);
                Assert.Equal("破风", receivedUser);
                Assert.Equal(genAfter, receivedGen);
            }
            finally
            {
                removeAuthHandlerM.Invoke(null, new object[] { handlerDelegate });
            }

            // 同步校验独立 ModEventBus 可无异常订阅并投递 NameAuth.AuthSucceededEvent
            Type busType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.ModEventBus", throwOnError: true)!;
            object bus = Activator.CreateInstance(busType)!;
            MethodInfo subM = busType.GetMethod("Subscribe", BindingFlags.Public | BindingFlags.Instance)!.MakeGenericMethod(authEvtType);
            MethodInfo pubM = busType.GetMethod("Publish", BindingFlags.Public | BindingFlags.Instance)!.MakeGenericMethod(authEvtType);

            var busHitBox = new AuthHitRecorder();
            var busHandler = System.Linq.Expressions.Expression.Lambda(
                actionOfEvtType,
                System.Linq.Expressions.Expression.Call(
                    recordHitM,
                    System.Linq.Expressions.Expression.Constant(busHitBox),
                    System.Linq.Expressions.Expression.Convert(param, typeof(object))),
                param).Compile();

            using var sub = (IDisposable)subM.Invoke(bus, new object[] { busHandler, 0 })!;
            object evtInstance = Activator.CreateInstance(authEvtType, new object[] { pofeng.AccountId.ToString("N"), "破风", 99 })!;
            pubM.Invoke(bus, new object[] { evtInstance });
            Assert.Equal(1, busHitBox.Count);
            Assert.Equal("破风", busHitBox.LastUsername);
            Assert.Equal(99, busHitBox.LastGeneration);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class AuthHitRecorder
    {
        public int Count;
        public string? LastUsername;
        public int LastGeneration;
    }

    private static void RecordAuthHitHelper(AuthHitRecorder recorder, object evtBoxed)
    {
        recorder.Count++;
        Type t = evtBoxed.GetType();
        recorder.LastUsername = (string?)t.GetField("Username")?.GetValue(evtBoxed);
        recorder.LastGeneration = (int)(t.GetField("Generation")?.GetValue(evtBoxed) ?? 0);
    }

    // ── 4. P1-15：CustomGameplayUI 局内 HUD 权威规则 _activeRunRules 优先校验 ────

    [Fact]
    public void P1_15_CustomGameplayUI_ResolveInRunDisplayRules_PrioritizesAuthoritativeActiveRunRulesOverLobbyNormalRules()
    {
        Type uiType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomGameplayUI", throwOnError: true)!;
        Type rulesType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomRunRuleSet", throwOnError: true)!;

        // 跳过 MonoBehaviour 原生构造，纯 .NET 实例化验证 ResolveInRunDisplayRules / SummarizeInRunDisplayRules / TryDeserializeRules
        object ui = RuntimeHelpers.GetUninitializedObject(uiType);

        FieldInfo normalRulesF = uiType.GetField("_normalRules", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo lobbyRulesF = uiType.GetField("_lobbyRules", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo activeRunRulesF = uiType.GetField("_activeRunRules", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo activeRunIsCustomF = uiType.GetField("_activeRunIsCustomRuleRun", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo allowTendencyF = uiType.GetField("_activeRunAllowTendency", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo lockedTendencyF = uiType.GetField("_activeRunTendencyLocked", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo lockedMaskF = uiType.GetField("_activeRunLockedMask", BindingFlags.NonPublic | BindingFlags.Instance)!;

        MethodInfo tryDeserM = uiType.GetMethod("TryDeserializeRules", BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo resolveInRunM = uiType.GetMethod("ResolveInRunDisplayRules", BindingFlags.NonPublic | BindingFlags.Instance)!;
        MethodInfo summarizeInRunM = uiType.GetMethod("SummarizeInRunDisplayRules", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.NotNull(activeRunRulesF);
        Assert.NotNull(activeRunIsCustomF);
        Assert.NotNull(tryDeserM);
        Assert.NotNull(resolveInRunM);
        Assert.NotNull(summarizeInRunM);

        // 核验冷重启鉴权后重同步与防乱序成员全部在位
        Assert.NotNull(uiType.GetMethod("OnNameAuthSucceeded", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(uiType.GetMethod("HandleAuthReadySync", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(uiType.GetMethod("ForceRefreshActiveRunRulesAfterAuth", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(uiType.GetField("_rulesFetchSeq", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(uiType.GetField("_activeRunFetchSeq", BindingFlags.NonPublic | BindingFlags.Instance));

        // 场景 A：模拟冷重启后大厅 _normalRules 退化为官方默认（0 项），但服务端 /active-run-tendency 下发了 SQLite 权威活跃局规则（2+ 项自定义）
        object vanillaNormal = Activator.CreateInstance(rulesType)!;
        object vanillaLobby = Activator.CreateInstance(rulesType)!;
        normalRulesF.SetValue(ui, vanillaNormal);
        lobbyRulesF.SetValue(ui, vanillaLobby);
        allowTendencyF.SetValue(ui, true);
        lockedTendencyF.SetValue(ui, true);
        lockedMaskF.SetValue(ui, 0UL);

        string serverActiveRunPayloadJson = JsonConvert.SerializeObject(new
        {
            hasActiveRun = true,
            runId = "run_281612cbbcd3",
            isCustomRuleRun = true,
            allowTendencySelection = true,
            tendencyLocked = true,
            selectedArchetypeMask = 0UL,
            customRules = new
            {
                presetId = "Custom",
                specialModeId = "Standard",
                startingGold = 50,
                startingHealth = 500,
                startingIncome = 5,
                startingExperience = 3,
                startingPrestige = 20,
                monsterLootChooseOne = true,
                startingItems = new[]
                {
                    new { templateId = "bazaar-chronicles-sample:starport_compass_blade", tier = "Bronze" },
                    new { templateId = "clockwork-forge:z_sword_custom_d83alo", tier = "Bronze" }
                },
                selectedModIds = new[] { "bazaar-chronicles-sample", "clockwork-forge" },
                isCustomPowerModified = true
            }
        });

        var payloadObj = JObject.Parse(serverActiveRunPayloadJson);
        object? parsedActiveRules = tryDeserM.Invoke(null, new object[] { payloadObj["customRules"]!.ToString() });
        Assert.NotNull(parsedActiveRules);

        activeRunRulesF.SetValue(ui, parsedActiveRules);
        activeRunIsCustomF.SetValue(ui, (bool)payloadObj["isCustomRuleRun"]!);

        object displayed1 = resolveInRunM.Invoke(ui, new object[] { false })!;
        Assert.Equal(50, rulesType.GetProperty("StartingGold")!.GetValue(displayed1));
        Assert.Equal(500, rulesType.GetProperty("StartingHealth")!.GetValue(displayed1));

        object summary1 = summarizeInRunM.Invoke(ui, new object[] { false })!;
        Type sumType = summary1.GetType();
        Assert.True((bool)sumType.GetField("IsCustomActive")!.GetValue(summary1)!);
        Assert.True((int)sumType.GetField("ActiveCount")!.GetValue(summary1)! >= 2);
        string summaryText1 = (string)sumType.GetField("SummaryText")!.GetValue(summary1)!;
        Assert.Contains("开局预设", summaryText1, StringComparison.Ordinal);
        Assert.Contains("固定发牌×2", summaryText1, StringComparison.Ordinal);

        // 场景 B：若活跃局仅开启了本局 Mod 列表（其余参数为官方默认）且服务端标明 isCustomRuleRun=true，HUD 仍点亮自定义规则不退回灰色标准
        string modOnlyRulesJson = JsonConvert.SerializeObject(new
        {
            presetId = "PureOfficial",
            specialModeId = "Standard",
            startingGold = 15,
            startingHealth = 300,
            startingIncome = 5,
            startingExperience = 3,
            startingPrestige = 20,
            monsterLootChooseOne = true,
            selectedModIds = new[] { "bazaar-chronicles-sample" },
            isCustomPowerModified = true
        });
        object? parsedModOnly = tryDeserM.Invoke(null, new object[] { modOnlyRulesJson });
        Assert.NotNull(parsedModOnly);
        activeRunRulesF.SetValue(ui, parsedModOnly);
        activeRunIsCustomF.SetValue(ui, true);

        object summary2 = summarizeInRunM.Invoke(ui, new object[] { false })!;
        Assert.True((bool)sumType.GetField("IsCustomActive")!.GetValue(summary2)!);
        Assert.Equal(1, (int)sumType.GetField("ActiveCount")!.GetValue(summary2)!);
        Assert.Contains("本局 Mod×1", (string)sumType.GetField("SummaryText")!.GetValue(summary2)!, StringComparison.Ordinal);

        // 场景 C：无活跃局清空 _activeRunRules 后，平滑回退大厅 _normalRules 且仍尊重锁定流派倾向掩码
        activeRunRulesF.SetValue(ui, null);
        activeRunIsCustomF.SetValue(ui, false);
        rulesType.GetProperty("StartingGold")!.SetValue(vanillaNormal, 25);
        lockedTendencyF.SetValue(ui, true);
        lockedMaskF.SetValue(ui, 0b101UL);

        object displayed3 = resolveInRunM.Invoke(ui, new object[] { false })!;
        Assert.Equal(25, rulesType.GetProperty("StartingGold")!.GetValue(displayed3));
        Assert.Equal(0b101UL, (ulong)rulesType.GetProperty("SelectedArchetypeMask")!.GetValue(displayed3)!);

        allowTendencyF.SetValue(ui, false);
        object displayed4 = resolveInRunM.Invoke(ui, new object[] { false })!;
        Assert.Equal(0UL, (ulong)rulesType.GetProperty("SelectedArchetypeMask")!.GetValue(displayed4)!);
    }

    // ── 5. Wave 2 全量客户端 Harmony 补丁零签名失配校验（基于 LauncherAssemblyFixture） ──

    private sealed class MockWave2CardController { }
    private sealed class MockAuthResult { }

    private sealed class MockWave2OfficialTargets
    {
        // NameAuth
        public Task<MockAuthResult> AuthenticateAsync() => Task.FromResult(new MockAuthResult());
        public bool IsAuthenticated => true;
        public bool RequiresEmailVerification => false;

        // CombatPatch (P1-02 / P2-04)
        public Task SpawnCombatCards(ECombatantId combatantId, bool skipAnimation = false) => Task.CompletedTask;
        public void ApplyCardCombatAttributes() { }
        public void Exit() { }
        public void ForceExitReplayState() { }
        public Task OnExit() => Task.CompletedTask;

        // CompatAuditPatch.Wave5Stability (P1-14 / P2-06 / P2-07 / P2-08 / P2-09 / P2-11 / P3-05)
        public Task WaitForCardDealVFX(List<MockWave2CardController> controllers) => Task.CompletedTask;
        public bool FromBothSections(List<MockWave2CardController> cardControllers) => false;
        public Task PlayRevealSequence() => Task.CompletedTask;
        public Task PlaySkippedRevealSequence() => Task.CompletedTask;
        public Task Play(List<MockWave2CardController> itemControllers, bool useRawAnimation, bool skipAnimation) => Task.CompletedTask;
        public void PlayCarpetUnroll(string targetState, int cueDelay) { }
        public void Reset() { }
        public void ShowBanner() { }
        public Task UnlockPlayerSockets() => Task.CompletedTask;
        public Task UnlockSocketsBitmask(SocketedContainer unlockedSockets, bool animate) => Task.CompletedTask;
        public Task UpdateUnrollState(int a, int b, int c) => Task.CompletedTask;
        public Task AnimateCarpetToUnlocks(int a, int b) => Task.CompletedTask;
        public void AnimateCarpetsToUnlockInstant(int a, int b) { }
        public void HandleOnLevelUpVisuals(bool flag) { }
        public void Show() { }
        public Task HandleNewDayTransition() => Task.CompletedTask;
        public void RefreshPropManager() { }
        public void OnSpawnComplete(string id) { }
        public void UpdatePriceTag(ERunState state) { }
        public void OnEnter() { }
        public void HandleCardMoved(InstanceId instanceId, bool movedLocally) { }
        public void OnStateChanged(object obj) { }
        public void ClearPortrait() { }
        public void MoveNext() { }
    }

    [Fact]
    public void Wave2_AllClientHarmonyPatches_HaveZeroSignatureMismatch_AndAllReferencedCallbacksExist()
    {
        const BindingFlags staticAny = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        Type mockType = typeof(MockWave2OfficialTargets);

        Type nameAuthType = _fx.LauncherAssembly.GetType("BazaarLauncher.NameAuth", throwOnError: true)!;
        Type combatPatchType = _fx.LauncherAssembly.GetType("BazaarLauncher.CombatPatch", throwOnError: true)!;
        Type compatType = _fx.LauncherAssembly.GetType("BazaarLauncher.CompatAuditPatch", throwOnError: true)!;
        Type uiType = _fx.LauncherAssembly.GetType("BazaarLauncher.CustomGameplayUI", throwOnError: true)!;
        Type inspectorType = _fx.LauncherAssembly.GetType("BazaarLauncher.GameInspector", throwOnError: true)!;
        Type driverType = _fx.LauncherAssembly.GetType("BazaarLauncher.GameDriver", throwOnError: true)!;

        // 1. 逐一通过 SafeHarmonyPatcher.ValidateHookMethod 强校验 Wave 2 全部关键补丁签名（含返回类型、ref __result 与字段引用）
        var checks = new List<(string Label, MethodBase Target, MethodInfo Hook, string Role)>
        {
            // NameAuth
            ("NameAuth.AuthenticateAsync",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.AuthenticateAsync))!,
                nameAuthType.GetMethod("AuthenticatePrefix", staticAny)!, "Prefix"),
            ("NameAuth.IsAuthenticated",
                mockType.GetProperty(nameof(MockWave2OfficialTargets.IsAuthenticated))!.GetGetMethod()!,
                nameAuthType.GetMethod("ReturnTrue", staticAny)!, "Prefix"),
            ("NameAuth.RequiresEmailVerification",
                mockType.GetProperty(nameof(MockWave2OfficialTargets.RequiresEmailVerification))!.GetGetMethod()!,
                nameAuthType.GetMethod("ReturnFalse", staticAny)!, "Prefix"),

            // CombatPatch (P1-02 / P2-04)
            ("CombatPatch.ReplaySpawnCombatCards.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.SpawnCombatCards))!,
                combatPatchType.GetMethod("ReplaySpawnCombatCardsPrefix", staticAny)!, "Prefix"),
            ("CombatPatch.ReplaySpawnCombatCards.Postfix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.SpawnCombatCards))!,
                combatPatchType.GetMethod("ReplaySpawnCombatCardsPostfix", staticAny)!, "Postfix"),
            ("CombatPatch.ReplayApplyCardCombatAttributes.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.ApplyCardCombatAttributes))!,
                combatPatchType.GetMethod("ReplayApplyCardCombatAttributesPrefix", staticAny)!, "Prefix"),
            ("CombatPatch.ReplayExit.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Exit))!,
                combatPatchType.GetMethod("ReplayExitPrefix", staticAny)!, "Prefix"),
            ("CombatPatch.ReplayExit.Postfix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Exit))!,
                combatPatchType.GetMethod("ReplayExitPostfix", staticAny)!, "Postfix"),
            ("CombatPatch.ForceExitReplayState.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.ForceExitReplayState))!,
                combatPatchType.GetMethod("ForceExitReplayStatePrefix", staticAny)!, "Prefix"),
            ("CombatPatch.ForceExitReplayState.Postfix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.ForceExitReplayState))!,
                combatPatchType.GetMethod("ForceExitReplayStatePostfix", staticAny)!, "Postfix"),
            ("CombatPatch.ReplayOnExitVisualCleanup.Postfix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.OnExit))!,
                combatPatchType.GetMethod("ReplayOnExitVisualCleanupPostfix", staticAny)!, "Postfix"),

            // CompatAuditPatch.Wave5Stability (P1-14 / P2-06 / P2-07 / P2-08 / P2-09 / P2-11 / P3-05)
            ("Wave5.WaitForCardDealVFX",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.WaitForCardDealVFX))!,
                compatType.GetMethod("Wave5WaitForCardDealVfxPrefixGeneric", staticAny)!.MakeGenericMethod(typeof(MockWave2CardController)), "Prefix"),
            ("Wave5.FromBothSections",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.FromBothSections))!,
                compatType.GetMethod("Wave5FromBothSectionsPrefixGeneric", staticAny)!.MakeGenericMethod(typeof(MockWave2CardController)), "Prefix"),
            ("Wave5.ItemBoardPlayReveal.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.PlayRevealSequence))!,
                compatType.GetMethod("Wave5ItemBoardPlayRevealPrefix", staticAny)!, "Prefix"),
            ("Wave5.ItemBoardPlayReveal.Finalizer",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.PlayRevealSequence))!,
                compatType.GetMethod("Wave5ItemBoardPlayRevealFinalizer", staticAny)!, "Finalizer"),
            ("Wave5.ItemBoardPlaySkippedReveal",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.PlaySkippedRevealSequence))!,
                compatType.GetMethod("Wave5ItemBoardPlaySkippedRevealPrefix", staticAny)!, "Prefix"),
            ("Wave5.RevealDriverPlay.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Play))!,
                compatType.GetMethod("Wave5RevealDriverPlayPrefixGeneric", staticAny)!.MakeGenericMethod(typeof(MockWave2CardController)), "Prefix"),
            ("Wave5.RevealDriverPlay.Finalizer",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Play))!,
                compatType.GetMethod("Wave5RevealDriverPlayFinalizer", staticAny)!, "Finalizer"),
            ("Wave5.SoundCarpetPlayUnroll",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.PlayCarpetUnroll))!,
                compatType.GetMethod("Wave5SoundCarpetPlayUnrollPrefix", staticAny)!, "Prefix"),
            ("Wave5.EndBattleDialogReset",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Reset))!,
                compatType.GetMethod("Wave5EndBattleDialogResetPrefix", staticAny)!, "Prefix"),
            ("Wave5.EndBattleDialogShowBanner",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.ShowBanner))!,
                compatType.GetMethod("Wave5EndBattleDialogShowBannerPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetUnlockPlayerSockets.Prefix",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.UnlockPlayerSockets))!,
                compatType.GetMethod("Wave5CarpetUnlockPlayerSocketsPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetUnlockPlayerSockets.Finalizer",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.UnlockPlayerSockets))!,
                compatType.GetMethod("Wave5TaskUnloadNreFinalizer", staticAny)!, "Finalizer"),
            ("Wave5.CarpetUnlockBitmask",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.UnlockSocketsBitmask))!,
                compatType.GetMethod("Wave5CarpetUnlockBitmaskPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetUpdateUnrollState",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.UpdateUnrollState))!,
                compatType.GetMethod("Wave5CarpetAnimatorAliveTaskPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetAnimateToUnlocks",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.AnimateCarpetToUnlocks))!,
                compatType.GetMethod("Wave5CarpetAnimateToUnlocksPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetAnimateInstant",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.AnimateCarpetsToUnlockInstant))!,
                compatType.GetMethod("Wave5CarpetAnimatorAliveVoidPrefix", staticAny)!, "Prefix"),
            ("Wave5.CarpetHandleLevelUp",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.HandleOnLevelUpVisuals))!,
                compatType.GetMethod("Wave5RequireAliveAndRunVoidPrefix", staticAny)!, "Prefix"),
            ("Wave5.ClearBoardShow",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.Show))!,
                compatType.GetMethod("Wave5ClearBoardShowPrefix", staticAny)!, "Prefix"),
            ("Wave5.ClearBoardNewDay",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.HandleNewDayTransition))!,
                compatType.GetMethod("Wave5RequireAliveAndRunTaskPrefix", staticAny)!, "Prefix"),
            ("Wave5.HeroBoardRefreshPropManager",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.RefreshPropManager))!,
                compatType.GetMethod("Wave5HeroBoardRefreshPropManagerPrefix", staticAny)!, "Prefix"),
            ("Wave5.RewardOnSpawnComplete",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.OnSpawnComplete))!,
                compatType.GetMethod("Wave5RewardOnSpawnCompletePrefix", staticAny)!, "Prefix"),
            ("Wave5.CardControllerUpdatePriceTag",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.UpdatePriceTag))!,
                compatType.GetMethod("Wave5CardControllerUpdatePriceTagPrefix", staticAny)!, "Prefix"),
            ("Wave5.CombatStateOnEnter",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.OnEnter))!,
                compatType.GetMethod("Wave5CombatStateOnEnterPrefix", staticAny)!, "Prefix"),
            ("Wave5.HandleCardMoved",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.HandleCardMoved))!,
                compatType.GetMethod("Wave5HandleCardMovedPostfix", staticAny)!, "Postfix"),
            ("Wave5.HandVisibilityOnStateChanged",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.OnStateChanged))!,
                compatType.GetMethod("Wave5HandVisibilityOnStateChangedPostfix", staticAny)!, "Postfix"),
            ("Wave5.EncounterPortraitOnStateChanged",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.OnStateChanged))!,
                compatType.GetMethod("Wave5EncounterStatePortraitOnStateChangedPostfix", staticAny)!, "Postfix"),
            ("Wave5.ClearPortrait",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.ClearPortrait))!,
                compatType.GetMethod("Wave5ClearPortraitPostfix", staticAny)!, "Postfix"),
            ("Wave5.AsyncVoidUnloadNreFinalizer",
                mockType.GetMethod(nameof(MockWave2OfficialTargets.MoveNext))!,
                compatType.GetMethod("Wave5AsyncVoidUnloadNreFinalizer", staticAny)!, "Finalizer"),
        };

        foreach (var (label, target, hook, role) in checks)
        {
            Assert.True(
                InvokeValidateHookMethod(target, hook, role, out string err),
                $"Wave 2 补丁签名预检失败 [{label}]：{err}");
        }

        // 额外核验 TransformManager.StartTransformVFX 泛型补丁方法存在且带合法泛型参数
        MethodInfo? startVfxPrefix = compatType.GetMethod("Wave5StartTransformVfxPrefixGeneric", staticAny);
        MethodInfo? startVfxPostfix = compatType.GetMethod("Wave5StartTransformVfxPostfixGeneric", staticAny);
        Assert.NotNull(startVfxPrefix);
        Assert.NotNull(startVfxPostfix);
        Assert.True(startVfxPrefix!.IsGenericMethodDefinition);
        Assert.True(startVfxPostfix!.IsGenericMethodDefinition);

        // 2. 源码交叉扫描：确认 Wave 2 涉及的所有客户端补丁文件中 AccessTools.Method(typeof(X), nameof(Y)) 100% 在真实程序集中可解析
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        var fileToType = new (string RelPath, Type OwnerType)[]
        {
            ("src/BazaarLauncher/NameAuth.cs", nameAuthType),
            ("src/BazaarLauncher/CustomGameplayUI.cs", uiType),
            ("src/BazaarLauncher/CombatPatch.cs", combatPatchType),
            ("src/BazaarLauncher/CompatAuditPatch.PedestalAndHints.cs", compatType),
            ("src/BazaarLauncher/CompatAuditPatch.GameSim.cs", compatType),
            ("src/BazaarLauncher/CompatAuditPatch.UserFeedbackFixes.cs", compatType),
            ("src/BazaarLauncher/CompatAuditPatch.Wave5Stability.cs", compatType),
        };

        int totalVerifiedHookRefs = 0;
        foreach (var (relPath, ownerType) in fileToType)
        {
            string fullPath = Path.Combine(repoRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"未找到源文件：{fullPath}");
            string src = File.ReadAllText(fullPath);

            string pattern = $@"AccessTools\.Method\(typeof\({Regex.Escape(ownerType.Name)}\),\s*nameof\(([A-Za-z0-9_]+)\)\)";
            MethodInfo[] staticMethods = ownerType.GetMethods(staticAny);
            foreach (Match m in Regex.Matches(src, pattern))
            {
                string hookName = m.Groups[1].Value;
                bool exists = staticMethods.Any(sm => string.Equals(sm.Name, hookName, StringComparison.Ordinal));
                Assert.True(exists, $"补丁回调 {ownerType.Name}.{hookName}（引用于 {relPath}）在真实程序集中不存在");
                totalVerifiedHookRefs++;
            }
        }
        Assert.True(totalVerifiedHookRefs >= 50, $"预期验证至少 50 个 Wave 2 补丁回调引用，实际仅 {totalVerifiedHookRefs}");

        // 3. 核验 GameInspector & GameDriver 的 Wave 2 关键入口与防回归契约（P2-10 / P2-11 / P2-12 / P3-04）
        Assert.NotNull(inspectorType.GetMethod("IsClientBusy", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static));
        Assert.NotNull(driverType.GetMethod("WaitForClientIdleAsync", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static));
        string driverSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "GameDriver.cs"));
        Assert.Contains("retryable_busy", driverSrc, StringComparison.Ordinal);
        Assert.Contains("EnterUpgradePreview", driverSrc, StringComparison.Ordinal);
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using BazaarCore.Data;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.TempoNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Phase 1 安卓自建服通用服务端兼容专项测试：
/// 覆盖邮箱 @ 前缀账号互通、SHA-256 双模口令校验、三段式 JWT AccessToken、
/// 移动端缺失路由、ActiveRun 双字段（PlayMode + Type）、路径前缀剥离与稳定 SHA-256 ETag/304。
/// </summary>
public sealed class AndroidCompatServerTests
{
    private static async Task<(WebApplication App, HttpClient Http, string TempDir, AccountStore Accounts, Persistence Db)> StartTestServerAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "bazaar_android_compat_" + Guid.NewGuid().ToString("N"));
        string dataDir = Path.Combine(tempDir, "data");
        string transDir = Path.Combine(dataDir, "translations");
        Directory.CreateDirectory(transDir);

        File.WriteAllBytes(Path.Combine(dataDir, "GameData.db.zip"), Encoding.UTF8.GetBytes("FAKE_GAMEDATA_ZIP_PAYLOAD_V1"));
        File.WriteAllBytes(Path.Combine(transDir, "zh-CN.bytes"), Encoding.UTF8.GetBytes("FAKE_ZH_CN_TRANSLATION_V1"));
        File.WriteAllText(Path.Combine(dataDir, "maintenance.json"),
            """
            {
              "status": {
                "systems": { "temponet": { "isAvailable": true } },
                "versions": {},
                "locales": [
                  { "id": "de-DE", "displayName": "Deutsch" },
                  { "id": "zh-CN", "displayName": "简体中文" },
                  { "id": "ko-KR", "displayName": "한국어" }
                ]
              }
            }
            """);

        string dbPath = Path.Combine(tempDir, "state.db");
        var persistence = new Persistence(dbPath);
        var accounts = new AccountStore(persistence);
        var sessions = new SessionStore();

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(persistence);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);

        var app = builder.Build();
        BazaarStaticDataPipeline.UseBazaarRoutePrefixStrip(app);
        app.UseRouting();

        TempoNetEndpoints.Map(app);
        BazaarStaticDataPipeline.UseBazaarStaticDataStableEtag(app, dataDir);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(dataDir),
            ServeUnknownFileTypes = true,
            DefaultContentType = "application/octet-stream",
        });
        app.MapGet("/healthz", () => Results.Json(new { ok = true }));
        app.MapMethods("/maintenance.json", new[] { HttpMethods.Get, HttpMethods.Head }, (HttpContext ctx) => BazaarStaticDataPipeline.ServeMaintenanceJsonAsync(ctx, dataDir));

        await app.StartAsync();
        string baseUrl = app.Urls.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (app, http, tempDir, accounts, persistence);
    }

    private static async Task CleanupAsync(WebApplication app, HttpClient http, string tempDir, AccountStore accounts, Persistence db)
    {
        http.Dispose();
        try { await app.StopAsync(); } catch { }
        try { await app.DisposeAsync(); } catch { }
        try { accounts.Dispose(); } catch { }
        try { db.Dispose(); } catch { }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private static string Sha256Hex(string raw)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();

    [Fact]
    public async Task EmailPrefixLogin_SharesAccountWithPc_AndIssuesValidMobileJwt()
    {
        var (app, http, tempDir, accounts, db) = await StartTestServerAsync();
        try
        {
            // 1. PC 端先用用户名 "Alice" 免密建号登录
            var pcResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "Alice" });
            pcResp.EnsureSuccessStatusCode();
            var pcToken = JsonNode.Parse(await pcResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();

            var pcSummaryReq = new HttpRequestMessage(HttpMethod.Get, "/api/Accounts/me/summary");
            pcSummaryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pcToken);
            var pcSummary = JsonNode.Parse(await (await http.SendAsync(pcSummaryReq)).Content.ReadAsStringAsync())!;
            string pcAccountId = pcSummary["AccountId"]!.GetValue<string>();

            // 2. 安卓端用 Email="alice@bazaar.local" + 前端 SHA-256 密码登录
            var mobileResp = await http.PostAsJsonAsync("/api/auth/login/user", new
            {
                Email = "alice@bazaar.local",
                Password = Sha256Hex("any-mobile-password"),
            });
            mobileResp.EnsureSuccessStatusCode();
            var mobileLoginJson = JsonNode.Parse(await mobileResp.Content.ReadAsStringAsync())!;
            string mobileToken = mobileLoginJson["AccessToken"]!.GetValue<string>();

            // 3. 校验三段式 JWT 结构与 MobileAuthService.AccountState 所需载荷字段
            string[] jwtParts = mobileToken.Split('.');
            Assert.Equal(3, jwtParts.Length);
            string padded = jwtParts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
            var payloadObj = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(padded)))!;
            Assert.False(string.IsNullOrEmpty(payloadObj["email"]?.GetValue<string>()));
            Assert.Equal("true", payloadObj["native_verified"]?.GetValue<string>());

            // 4. 校验与 PC 端完全为同一个 AccountId
            var mobileSummaryReq = new HttpRequestMessage(HttpMethod.Get, "/api/Accounts/me/summary");
            mobileSummaryReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mobileToken);
            var mobileSummary = JsonNode.Parse(await (await http.SendAsync(mobileSummaryReq)).Content.ReadAsStringAsync())!;
            Assert.Equal(pcAccountId, mobileSummary["AccountId"]!.GetValue<string>());
            Assert.Equal("Alice", mobileSummary["Username"]!.GetValue<string>());

            // 5. 安卓无口令直登不得锁死 PC 免密登录
            var pcRetryResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "Alice" });
            Assert.Equal(HttpStatusCode.OK, pcRetryResp.StatusCode);

            // 6. 设置口令保护后，明文与移动端 SHA-256 均可校验通过，错误口令返回 401
            var setPwdReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
            {
                Content = JsonContent.Create(new { newPassword = "MyStrongPassword123" }),
            };
            setPwdReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pcToken);
            (await http.SendAsync(setPwdReq)).EnsureSuccessStatusCode();

            var pcPlainOk = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "Alice", password = "MyStrongPassword123" });
            Assert.Equal(HttpStatusCode.OK, pcPlainOk.StatusCode);

            var mobileShaOk = await http.PostAsJsonAsync("/api/auth/login/user", new
            {
                Email = "alice@bazaar.local",
                Password = Sha256Hex("MyStrongPassword123"),
            });
            Assert.Equal(HttpStatusCode.OK, mobileShaOk.StatusCode);

            var mobileWrongPwd = await http.PostAsJsonAsync("/api/auth/login/user", new
            {
                Email = "alice@bazaar.local",
                Password = Sha256Hex("WrongPassword999"),
            });
            Assert.Equal(HttpStatusCode.Unauthorized, mobileWrongPwd.StatusCode);
        }
        finally
        {
            await CleanupAsync(app, http, tempDir, accounts, db);
        }
    }

    [Fact]
    public async Task MobileAuthRoutes_CheckUsername_ExternalAndRegisterEmailAlias_WorkCorrectly()
    {
        var (app, http, tempDir, accounts, db) = await StartTestServerAsync();
        try
        {
            // 1. POST /api/accounts/check-username
            var okCheck = await http.PostAsJsonAsync("/api/accounts/check-username", new { Username = "NewMobileHero" });
            Assert.Equal(HttpStatusCode.OK, okCheck.StatusCode);

            var emptyCheck = await http.PostAsJsonAsync("/api/accounts/check-username", new { Username = "   " });
            Assert.Equal(HttpStatusCode.BadRequest, emptyCheck.StatusCode);

            // 2. GET /api/accounts/check-username 保持向后兼容
            var getCheck = await http.GetAsync("/api/accounts/check-username?username=NewMobileHero");
            Assert.Equal(HttpStatusCode.OK, getCheck.StatusCode);

            // 3. POST /api/auth/external 默认返回 404 + TN-20013
            var extResp = await http.PostAsJsonAsync("/api/auth/external", new { provider = "PlayGamesServices", deviceId = "dev-001" });
            Assert.Equal(HttpStatusCode.NotFound, extResp.StatusCode);
            var extJson = JsonNode.Parse(await extResp.Content.ReadAsStringAsync())!;
            Assert.Equal("TN-20013", extJson["errorCode"]!.GetValue<string>());

            // 4. POST /api/auth/register 绑定不同邮箱别名后，后续用邮箱登录仍能定位原账号
            var regResp = await http.PostAsJsonAsync("/api/auth/register", new
            {
                Username = "DragonRider",
                Email = "player999@example.com",
                Password = Sha256Hex("secretpass"),
            });
            regResp.EnsureSuccessStatusCode();
            string regToken = JsonNode.Parse(await regResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();

            // 5. 已认证状态下 POST /api/auth/external/link 返回 200
            var linkReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/external/link")
            {
                Content = JsonContent.Create(new { provider = "PlayGamesServices", deviceId = "dev-001" }),
            };
            linkReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", regToken);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(linkReq)).StatusCode);

            // 6. 使用注册邮箱登录应返回 DragonRider 账号
            var relogin = await http.PostAsJsonAsync("/api/auth/login/user", new
            {
                Email = "player999@example.com",
                Password = Sha256Hex("secretpass"),
            });
            relogin.EnsureSuccessStatusCode();
            string reloginToken = JsonNode.Parse(await relogin.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            var sumReq = new HttpRequestMessage(HttpMethod.Get, "/api/Accounts/me/summary");
            sumReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", reloginToken);
            var sumJson = JsonNode.Parse(await (await http.SendAsync(sumReq)).Content.ReadAsStringAsync())!;
            Assert.Equal("DragonRider", sumJson["Username"]!.GetValue<string>());

            // 7. 邮箱校验状态始终为 true，且移动端全英雄解锁与宝石商品接口返回标准结构
            var verifyResp = await http.GetStringAsync("/api/accounts/me/email-verification-status");
            Assert.Equal("true", verifyResp.Trim());

            var heroUnlockJson = JsonNode.Parse(await http.GetStringAsync("/api/purchase/mobile/hero-unlock"))!;
            Assert.True(heroUnlockJson["HasAllHeroes"]!.GetValue<bool>());
            Assert.True(heroUnlockJson["OwnedHeroIds"]!.AsArray().Count >= 7);

            var gemsJson = JsonNode.Parse(await http.GetStringAsync("/api/purchase/mobile/gems"))!;
            Assert.Equal(JsonValueKind.Array, gemsJson.GetValueKind());
        }
        finally
        {
            await CleanupAsync(app, http, tempDir, accounts, db);
        }
    }

    [Fact]
    public async Task ActiveRunDto_OutputsBothPlayModeAndType_ForAndroidResumeCompatibility()
    {
        var (app, http, tempDir, accounts, db) = await StartTestServerAsync();
        try
        {
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "ResumeTester" });
            loginResp.EnsureSuccessStatusCode();
            string token = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            var acc = accounts.FindByUsername("ResumeTester")!;

            db.SaveRun(new Persistence.RunRow(
                Guid.NewGuid().ToString("N"),
                acc.AccountId,
                EHero.Vanessa,
                EPlayMode.Ranked,
                Guid.NewGuid(),
                3, 4, 2, 1, 18, 10, 4,
                ERunState.Choice,
                DateTime.UtcNow,
                "{}"));

            var profReq = new HttpRequestMessage(HttpMethod.Get, "/api/PlayerProfiles/me");
            profReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var profJson = JsonNode.Parse(await (await http.SendAsync(profReq)).Content.ReadAsStringAsync())!;
            var activeRun = profJson["ActiveRun"]!;
            Assert.NotNull(activeRun);
            Assert.Equal((int)EPlayMode.Ranked, activeRun["PlayMode"]!.GetValue<int>());
            Assert.Equal((int)EPlayMode.Ranked, activeRun["Type"]!.GetValue<int>());
        }
        finally
        {
            await CleanupAsync(app, http, tempDir, accounts, db);
        }
    }

    [Fact]
    public async Task PrefixStrip_StableSha256Etag304_AndZhCnOnlyMaintenance_WorkEndToEnd()
    {
        var (app, http, tempDir, accounts, db) = await StartTestServerAsync();
        try
        {
            // 1. /bz-enter 与 /bz-server 前缀剥离
            var timeResp = await http.GetAsync("/bz-enter/api/time");
            Assert.Equal(HttpStatusCode.OK, timeResp.StatusCode);

            var healthResp = await http.GetAsync("/bz-server/healthz");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);

            // 2. /bz-static-data/GameData.db.zip 返回稳定 SHA-256 ETag，且携带 If-None-Match 命中 304 (0 字节)
            string expectedZipEtag = $"\"{Sha256Hex("FAKE_GAMEDATA_ZIP_PAYLOAD_V1")}\"";
            var zipResp = await http.GetAsync("/bz-static-data/GameData.db.zip");
            Assert.Equal(HttpStatusCode.OK, zipResp.StatusCode);
            Assert.Equal(expectedZipEtag, zipResp.Headers.ETag?.ToString());

            var zip304Req = new HttpRequestMessage(HttpMethod.Get, "/bz-static-data/GameData.db.zip");
            zip304Req.Headers.TryAddWithoutValidation("If-None-Match", expectedZipEtag);
            var zip304Resp = await http.SendAsync(zip304Req);
            Assert.Equal(HttpStatusCode.NotModified, zip304Resp.StatusCode);
            Assert.Empty(await zip304Resp.Content.ReadAsByteArrayAsync());

            // 3. /translations/zh-CN.bytes 同样命中稳定 SHA-256 ETag 与 304
            string expectedZhEtag = $"\"{Sha256Hex("FAKE_ZH_CN_TRANSLATION_V1")}\"";
            var zhResp = await http.GetAsync("/bz-static-data/translations/zh-CN.bytes");
            Assert.Equal(HttpStatusCode.OK, zhResp.StatusCode);
            Assert.Equal(expectedZhEtag, zhResp.Headers.ETag?.ToString());

            var zh304Req = new HttpRequestMessage(HttpMethod.Get, "/translations/zh-CN.bytes");
            zh304Req.Headers.TryAddWithoutValidation("If-None-Match", expectedZhEtag);
            var zh304Resp = await http.SendAsync(zh304Req);
            Assert.Equal(HttpStatusCode.NotModified, zh304Resp.StatusCode);
            Assert.Empty(await zh304Resp.Content.ReadAsByteArrayAsync());

            // 4. /maintenance.json 仅保留 zh-CN 且支持 ETag/304
            var maintResp = await http.GetAsync("/bz-static-data/maintenance.json");
            Assert.Equal(HttpStatusCode.OK, maintResp.StatusCode);
            string maintEtag = maintResp.Headers.ETag!.ToString();
            var maintJson = JsonNode.Parse(await maintResp.Content.ReadAsStringAsync())!;
            var locales = maintJson["status"]!["locales"]!.AsArray();
            Assert.Single(locales);
            Assert.Equal("zh-CN", locales[0]!["id"]!.GetValue<string>());

            var maint304Req = new HttpRequestMessage(HttpMethod.Get, "/maintenance.json");
            maint304Req.Headers.TryAddWithoutValidation("If-None-Match", maintEtag);
            var maint304Resp = await http.SendAsync(maint304Req);
            Assert.Equal(HttpStatusCode.NotModified, maint304Resp.StatusCode);

            // 5. HEAD /maintenance.json 必须返回与 GET 完全一致的 SHA-256 ETag（绝不穿透至 StaticFiles）且支持 304
            var maintHeadReq = new HttpRequestMessage(HttpMethod.Head, "/bz-static-data/maintenance.json");
            var maintHeadResp = await http.SendAsync(maintHeadReq);
            Assert.Equal(HttpStatusCode.OK, maintHeadResp.StatusCode);
            Assert.Equal(maintEtag, maintHeadResp.Headers.ETag?.ToString());
            Assert.Empty(await maintHeadResp.Content.ReadAsByteArrayAsync());

            var maintHead304Req = new HttpRequestMessage(HttpMethod.Head, "/maintenance.json");
            maintHead304Req.Headers.TryAddWithoutValidation("If-None-Match", maintEtag);
            var maintHead304Resp = await http.SendAsync(maintHead304Req);
            Assert.Equal(HttpStatusCode.NotModified, maintHead304Resp.StatusCode);
        }
        finally
        {
            await CleanupAsync(app, http, tempDir, accounts, db);
        }
    }

    [Fact]
    public async Task ExternalAutoLogin_RespectsPasswordProtection_AndIssuesSingleTokenPair()
    {
        var (app, http, tempDir, accounts, db) = await StartTestServerAsync();
        string? prevEnv = Environment.GetEnvironmentVariable("BAZAAR_ALLOW_MOBILE_DEVICE_AUTOLOGIN");
        try
        {
            // 先创建受口令保护的账号 "ShieldedUser"
            var loginResp = await http.PostAsJsonAsync("/api/auth/login/user", new { username = "ShieldedUser" });
            loginResp.EnsureSuccessStatusCode();
            string token = JsonNode.Parse(await loginResp.Content.ReadAsStringAsync())!["AccessToken"]!.GetValue<string>();
            var setPwdReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
            {
                Content = JsonContent.Create(new { newPassword = "TopSecret999" }),
            };
            setPwdReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await http.SendAsync(setPwdReq)).EnsureSuccessStatusCode();

            // 验证 SetOrUpdateSecret 对带前后空格的原口令也能正确校验（与登录行为一致）
            var updatePwdReq = new HttpRequestMessage(HttpMethod.Post, "/api/auth/password")
            {
                Content = JsonContent.Create(new { currentPassword = "  TopSecret999  ", newPassword = "NewSecret888" }),
            };
            updatePwdReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(updatePwdReq)).StatusCode);

            Environment.SetEnvironmentVariable("BAZAAR_ALLOW_MOBILE_DEVICE_AUTOLOGIN", "1");

            // 1. 外部自动登录尝试命中已设口令账号时必须返回 401，禁止越权绕过口令保护
            var blockedExt = await http.PostAsJsonAsync("/api/auth/external", new { username = "ShieldedUser", deviceId = "dev-x" });
            Assert.Equal(HttpStatusCode.Unauthorized, blockedExt.StatusCode);

            // 2. 外部自动登录无口令保护的新账号成功，且只签发 1 对持久化令牌（不重复签发浪费配额）
            var okExt = await http.PostAsJsonAsync("/api/auth/external", new { username = "FreshMobileUser", deviceId = "dev-y" });
            Assert.Equal(HttpStatusCode.OK, okExt.StatusCode);
            var freshAcc = accounts.FindByUsername("FreshMobileUser");
            Assert.NotNull(freshAcc);
            var activeAccessTokens = db.LoadActiveAccountTokens()
                .Where(t => t.AccountId == freshAcc!.AccountId && t.TokenType == "access")
                .ToList();
            Assert.Single(activeAccessTokens);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_ALLOW_MOBILE_DEVICE_AUTOLOGIN", prevEnv);
            await CleanupAsync(app, http, tempDir, accounts, db);
        }
    }
}

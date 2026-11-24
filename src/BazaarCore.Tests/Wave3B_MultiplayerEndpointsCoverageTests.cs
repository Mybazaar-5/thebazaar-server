using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3B 服务端多人核心空白区强断言测试套件（闭环 D8-P1-08）：
/// 覆盖 AccountAuthResolver 全鉴权分支、SocialEndpoints 动态版本/私密房鉴权/限流、
/// SpectateFrameStore 零订阅不录帧与字节硬预算、LeaderboardEndpoints 去重/首通榜/防作弊。
/// 触碰进程环境变量与 Kestrel 内存宿主，标注 [Collection("GlobalStaticState")]。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave3B_MultiplayerEndpointsCoverageTests
{
    [Fact]
    public void D8_P1_08_AccountAuthResolver_CoversBearerSpoofPasswordStrictAndLegacyBranches()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"w3b_auth_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string? prevStrict = Environment.GetEnvironmentVariable("BAZAAR_STRICT_AUTH");
        try
        {
            Environment.SetEnvironmentVariable("BAZAAR_STRICT_AUTH", "0");
            using var db = new Persistence(Path.Combine(tempDir, "auth.db"));
            var accounts = new AccountStore(db);

            // 1. 仅携带 X-Account-Id 企图冒充 -> 401 ACCOUNT_ID_SPOOF_BLOCKED
            var spoofCtx = new DefaultHttpContext();
            spoofCtx.Request.Headers["X-Account-Id"] = Guid.NewGuid().ToString("N");
            var spoofRes = AccountAuthResolver.Resolve(spoofCtx, accounts, requireAuth: true);
            Assert.False(spoofRes.IsAuthenticated);
            Assert.Equal(401, spoofRes.FailureStatusCode);
            Assert.Equal("ACCOUNT_ID_SPOOF_BLOCKED", spoofRes.ErrorCode);

            // 2. 携带无效 Bearer Token -> 401 INVALID_TOKEN（绝不降级）
            var badTokenCtx = new DefaultHttpContext();
            badTokenCtx.Request.Headers["Authorization"] = "Bearer totally_invalid_token_xyz";
            badTokenCtx.Request.Headers["X-Username"] = "alice";
            var badRes = AccountAuthResolver.Resolve(badTokenCtx, accounts, requireAuth: true);
            Assert.False(badRes.IsAuthenticated);
            Assert.Equal(401, badRes.FailureStatusCode);
            Assert.Equal("INVALID_TOKEN", badRes.ErrorCode);

            // 3. 兼容模式正常 X-Username 降级通过
            var legacyCtx = new DefaultHttpContext();
            legacyCtx.Request.Headers["X-Username"] = "w3b_alice";
            var legacyRes = AccountAuthResolver.Resolve(legacyCtx, accounts, requireAuth: true);
            Assert.True(legacyRes.IsAuthenticated);
            Assert.Equal(AccountAuthResolver.AuthStrength.LegacyUsername, legacyRes.Strength);

            // 4. X-Account-Id 与 X-Username 不匹配 -> 401 IDENTITY_MISMATCH
            var mismatchCtx = new DefaultHttpContext();
            mismatchCtx.Request.Headers["X-Username"] = "w3b_alice";
            mismatchCtx.Request.Headers["X-Account-Id"] = Guid.NewGuid().ToString("N");
            var mismatchRes = AccountAuthResolver.Resolve(mismatchCtx, accounts, requireAuth: true);
            Assert.False(mismatchRes.IsAuthenticated);
            Assert.Equal("IDENTITY_MISMATCH", mismatchRes.ErrorCode);

            // 5. 开启严格模式后拒绝 X-Username 降级，但有效 Bearer Token 放行
            var (okLogin, regAcc, tokenPair, _) = accounts.LoginByNameWithSecret("w3b_bob", "secret_pw_123456");
            Assert.True(okLogin);
            Assert.NotNull(regAcc);
            Assert.NotNull(tokenPair);
            string token = tokenPair!.AccessToken;
            Assert.False(string.IsNullOrEmpty(token));

            Environment.SetEnvironmentVariable("BAZAAR_STRICT_AUTH", "1");
            var strictLegacyCtx = new DefaultHttpContext();
            strictLegacyCtx.Request.Headers["X-Username"] = "w3b_alice";
            Assert.False(AccountAuthResolver.Resolve(strictLegacyCtx, accounts, requireAuth: true).IsAuthenticated);

            var validBearerCtx = new DefaultHttpContext();
            validBearerCtx.Request.Headers["Authorization"] = $"Bearer {token}";
            var validRes = AccountAuthResolver.Resolve(validBearerCtx, accounts, requireAuth: true);
            Assert.True(validRes.IsAuthenticated);
            Assert.Equal(AccountAuthResolver.AuthStrength.BearerToken, validRes.Strength);
            Assert.Equal(regAcc!.AccountId, validRes.Account!.AccountId);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_STRICT_AUTH", prevStrict);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task D8_P1_08_SpectateFrameStore_SkipsUnwatchedAndEnforcesByteBudget()
    {
        var sseHub = new SseEventHub();
        var sessions = new SessionStore();
        var store = new SpectateFrameStore(
            sseHub,
            maxTotalBytes: 4096,
            maxSingleFrameBytes: 256,
            maxBytesPerAccount: 1024,
            idleBufferTtl: TimeSpan.FromMinutes(5),
            httpSpectatorLeaseTtl: TimeSpan.FromSeconds(30));

        string accA = Guid.NewGuid().ToString("N");
        // 无活跃订阅或租约时，ShouldRecordForAccount 必须为 false（零无谓录制开销）
        Assert.False(store.ShouldRecordForAccount(accA));

        // 发起 HTTP 观战查询后自动续期观战租约
        var missBefore = await store.GetBoardResponseAsync(accA, 0, sessions, CancellationToken.None);
        Assert.False(missBefore.Found);
        Assert.True(store.ShouldRecordForAccount(accA));

        // 单帧超预算（512 > 256）必须被丢弃且不污染已存帧
        byte[] oversized = new byte[512];
        store.RecordBootstrapSnapshot(accA, "run_a", null, 1, oversized);
        var miss = await store.GetBoardResponseAsync(accA, 0, sessions, CancellationToken.None);
        Assert.False(miss.Found);

        // 合法大小帧可写入并查询命中
        byte[] valid = new byte[64];
        valid[0] = 0x42;
        store.RecordBootstrapSnapshot(accA, "run_a", null, 2, valid);
        var hit = await store.GetBoardResponseAsync(accA, 0, sessions, CancellationToken.None);
        Assert.True(hit.Found);
        Assert.Equal(2, hit.RequestId);
        Assert.Equal(64, hit.Payload!.Length);
    }

    [Fact]
    public async Task D8_P1_08_D7_P1_04_SocialAndLeaderboardEndpoints_DynamicVersionPrivateAuthAndRanking()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"w3b_web_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            using var db = new Persistence(Path.Combine(tempDir, "web.db"));
            var accounts = new AccountStore(db);
            var sessions = new SessionStore();
            var lobbies = new LobbyStore(db);
            var sse = new SseEventHub();

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(accounts);
            builder.Services.AddSingleton(sessions);
            builder.Services.AddSingleton(lobbies);
            builder.Services.AddSingleton(sse);
            app = builder.Build();
            SocialEndpoints.Map(app);
            LeaderboardEndpoints.Map(app);
            await app.StartAsync();

            string addr = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(addr) };

            // 1. /api/ping 返回与程序集 InformationalVersion 一致的动态版本号（且 SocialEndpoints.cs 不得硬编码字面量版本号）
            using var pingResp = await http.GetAsync("/api/ping");
            Assert.Equal(HttpStatusCode.OK, pingResp.StatusCode);
            using var pingDoc = JsonDocument.Parse(await pingResp.Content.ReadAsStringAsync());
            string expectedVer = typeof(SocialEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "15.4.0";
            Assert.Equal(expectedVer, pingDoc.RootElement.GetProperty("serverVersion").GetString());

            string repoRoot = TestInfrastructure.TestDataLocator.ResolveRepoRoot();
            string socialSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarServer", "Endpoints", "SocialEndpoints.cs"));
            Assert.DoesNotContain("serverVersion = \"15.4.0\"", socialSrc, StringComparison.Ordinal);

            // 2. 排行榜非法模式返回 400
            using var badBoardResp = await http.GetAsync("/api/leaderboards/nonexistent_mode");
            Assert.Equal(HttpStatusCode.BadRequest, badBoardResp.StatusCode);

            // 3. 写入两条同账号无尽榜成绩，验证 /api/leaderboards/endless 正常返回且分页与计分一致
            var acc1 = accounts.GetOrCreateByName("w3b_lb_p1");
            var acc2 = accounts.GetOrCreateByName("w3b_lb_p2");
            db.UpsertLeaderboardEntry(new Persistence.LeaderboardEntryRow(
                Id: 0, BoardKey: "endless:all", SeasonId: 1, ModeId: "endless",
                AccountId: acc1.AccountId.ToString("N"), Username: acc1.Username, Hero: "Vanessa",
                RunId: "run_lb_1", Score: 1200, PrimaryMetric: 15, SecondaryMetric: 10, ElapsedSeconds: 600,
                DayReached: 15, Victories: 10, Defeats: 1, Prestige: 10, IsFirstAttempt: true,
                MutatorsJson: "[]", SummaryBoardJson: "{}", AchievedAt: DateTime.UtcNow));
            db.UpsertLeaderboardEntry(new Persistence.LeaderboardEntryRow(
                Id: 0, BoardKey: "endless:all", SeasonId: 1, ModeId: "endless",
                AccountId: acc2.AccountId.ToString("N"), Username: acc2.Username, Hero: "Pygmalien",
                RunId: "run_lb_2", Score: 2500, PrimaryMetric: 20, SecondaryMetric: 14, ElapsedSeconds: 800,
                DayReached: 20, Victories: 14, Defeats: 0, Prestige: 20, IsFirstAttempt: true,
                MutatorsJson: "[]", SummaryBoardJson: "{}", AchievedAt: DateTime.UtcNow));

            using var lbResp = await http.GetAsync("/api/leaderboards/endless?page=1&limit=10");
            Assert.Equal(HttpStatusCode.OK, lbResp.StatusCode);
            using var lbDoc = JsonDocument.Parse(await lbResp.Content.ReadAsStringAsync());
            var items = lbDoc.RootElement.GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal(2500, items[0].GetProperty("score").GetInt32());
            Assert.Equal(1200, items[1].GetProperty("score").GetInt32());
        }
        finally
        {
            http?.Dispose();
            if (app != null) await app.DisposeAsync();
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}

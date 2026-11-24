using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BazaarCore.Tests.TestInfrastructure;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarServer.Accounts;
using BazaarServer.Endpoints;
using BazaarServer.Game;
using BazaarServer.Storage;
using BazaarServer.Tournament;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1A 回归测试（文件 5）：覆盖 P0-44（观战精准订阅门控与 64MB 预算/单帧上限/TTL/LRU）、
/// D6-P1-04（系统公告不泄私密房事件）、D6-P1-05（私密/密码/邀请/归档房聊天与 SSE 成员鉴权及参数边界）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_SpectateAndSocialPrivacyTests
{
    private static async Task<(WebApplication App, HttpClient Http, string TempDir)> StartSocialTestHostAsync()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_soc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string dbPath = Path.Combine(tempDir, "state.db");

        var db = new Persistence(dbPath);
        var accounts = new AccountStore(db);
        var sessions = new SessionStore();
        var lobbies = new LobbyStore(db);
        var sseHub = new SseEventHub();
        var frameStore = new SpectateFrameStore(sseHub);
        var socialSink = new SocialAndReplayService(db, sseHub);

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(sessions);
        builder.Services.AddSingleton(lobbies);
        builder.Services.AddSingleton(sseHub);
        builder.Services.AddSingleton(frameStore);
        builder.Services.AddSingleton<ISpectateFrameRecorder>(frameStore);
        builder.Services.AddSingleton<ISocialAndReplaySink>(socialSink);
        builder.Services.AddSingleton(SharedGameDataFixture.Shared.Repo);

        var app = builder.Build();
        SocialEndpoints.Map(app);
        SpectateEndpoints.Map(app);
        await app.StartAsync();

        string baseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        return (app, http, tempDir);
    }

    private static async Task StopHostAsync(WebApplication app, HttpClient http, string tempDir)
    {
        try { http.Dispose(); } catch { }
        try { await app.StopAsync(); } catch { }
        try { await app.DisposeAsync(); } catch { }
        try { app.Services.GetService<Persistence>()?.Dispose(); } catch { }
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string bearer, object? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (jsonBody != null)
            req.Content = JsonContent.Create(jsonBody);
        return req;
    }

    [Fact]
    public async Task Spectate_ZeroBoardSubscribers_WithActiveGlobalChatSse_MustNotSerializeOrRecord()
    {
        var sseHub = new SseEventHub();
        var frameStore = new SpectateFrameStore(sseHub);
        string accountA = Guid.NewGuid().ToString("N");
        string accountB = Guid.NewGuid().ToString("N");

        // 1. 建立 10 条全局聊天 SSE 连接（TotalConnections == 10），但无人订阅 board:{accountA}
        var chatSubs = new List<SseEventHub.Subscription>();
        try
        {
            for (int i = 0; i < 10; i++)
            {
                var sub = sseHub.TrySubscribe(SseEventHub.SocialGlobalTopic, ownerKey: $"chat_user_{i}");
                Assert.NotNull(sub);
                chatSubs.Add(sub!);
            }

            Assert.Equal(10, sseHub.TotalConnections);
            Assert.False(sseHub.HasBoardSubscribers(accountA));
            Assert.False(frameStore.ShouldRecordForAccount(accountA));

            // 即使有全局 SSE 活跃，对无观战者的账号调用 RecordBootstrapSnapshot/RecordCommandDelta 必须 0 Base64/JSON 序列化
            frameStore.RecordBootstrapSnapshot(accountA, "run_zero_1", Guid.NewGuid(), 1, new byte[4096]);
            frameStore.RecordCommandDelta(accountA, "run_zero_1", Guid.NewGuid(), 1, 2, new byte[2048], isCombatSequence: false);
            Assert.Equal(0, frameStore.BroadcastSerializationCount);

            // 2. 开启 board:{accountA} 真实 SSE 订阅后，仅 accountA 触发精准推送，accountB 仍保持 false
            using var boardSub = sseHub.TrySubscribe(SseEventHub.BoardTopic(accountA), ownerKey: "spec_1");
            Assert.NotNull(boardSub);
            Assert.True(sseHub.HasBoardSubscribers(accountA));
            Assert.True(frameStore.ShouldRecordForAccount(accountA));
            Assert.False(sseHub.HasBoardSubscribers(accountB));
            Assert.False(frameStore.ShouldRecordForAccount(accountB));

            frameStore.RecordCommandDelta(accountA, "run_zero_1", Guid.NewGuid(), 2, 3, new byte[1024], isCombatSequence: true);
            Assert.Equal(1, frameStore.BroadcastSerializationCount);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var received = await boardSub!.Reader.ReadAsync(cts.Token);
            Assert.Contains("\"RequestId\":3", received.Data, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var s in chatSubs) s.Dispose();
        }

        // 全部退订后连接数与订阅检测均归零
        Assert.Equal(0, sseHub.TotalConnections);
        Assert.False(sseHub.HasBoardSubscribers(accountA));
    }

    [Fact]
    public async Task SpectateFrameStore_GlobalByteBudget_OversizedFrame_And_LruTtlEviction()
    {
        var sseHub = new SseEventHub();
        var store = new SpectateFrameStore(
            sseHub,
            maxTotalBytes: 256 * 1024,
            maxSingleFrameBytes: 64 * 1024,
            maxBytesPerAccount: 128 * 1024,
            idleBufferTtl: TimeSpan.FromMilliseconds(100),
            httpSpectatorLeaseTtl: TimeSpan.FromSeconds(10));

        // 1. 超过单帧 64KB 上限的 80KB 帧必须直接拦截，DroppedOversizedFrames == 1 且总字节为 0
        store.RecordBootstrapSnapshot("acc_big", "run_big", null, 1, new byte[80 * 1024]);
        Assert.Equal(1, store.DroppedOversizedFrames);
        Assert.Equal(0, store.CurrentTotalBytes);
        Assert.Equal(0, store.BufferedAccountCount);

        // 2. 连续为 10 个账号各写入 50KB（合计 500KB > 256KB 预算），必须触发 LRU 驱逐且始终 <= 256KB
        for (int i = 0; i < 10; i++)
        {
            store.RecordBootstrapSnapshot($"acc_{i:D2}", $"run_{i:D2}", null, 1, new byte[50 * 1024]);
            Assert.True(store.CurrentTotalBytes <= 256 * 1024, $"写入第 {i} 个账号后超出 256KB 全局预算：{store.CurrentTotalBytes}");
            await Task.Delay(5); // 确保 UpdatedAtUtc 严格递增以验证 LRU 顺序
        }
        // 最旧的 acc_00 应已被 LRU 淘汰，最新的 acc_09 仍保留
        Assert.Empty(store.GetPendingFramesSince("acc_00", 0));
        Assert.NotEmpty(store.GetPendingFramesSince("acc_09", 0));

        // 3. 等待超过 100ms TTL 后触发清理，全部旧缓存被回收，再 ClearForAccount 后总字节精确归零
        await Task.Delay(160);
        int ttlEvicted = store.EvictExpiredAndLru();
        Assert.True(ttlEvicted > 0);
        Assert.Equal(0, store.CurrentTotalBytes);
        Assert.Equal(0, store.BufferedAccountCount);

        // 验证 ClearForAccount 精确释放字节预算（无泄漏）
        store.RecordBootstrapSnapshot("acc_clear", "run_c", null, 1, new byte[32 * 1024]);
        Assert.Equal(32 * 1024, store.CurrentTotalBytes);
        store.ClearForAccount("acc_clear");
        Assert.Equal(0, store.CurrentTotalBytes);
    }

    [Fact]
    public async Task SocialAnnouncements_MustNotLeakPrivateLobbySystemEvents()
    {
        var (app, http, tempDir) = await StartSocialTestHostAsync();
        try
        {
            var accounts = app.Services.GetRequiredService<AccountStore>();
            var lobbies = app.Services.GetRequiredService<LobbyStore>();
            var socialSink = app.Services.GetRequiredService<ISocialAndReplaySink>();

            var host = accounts.GetOrCreateByName("PrivAnnHost", "game", "d1");
            var privLobby = lobbies.Create(host, "私密房秘密局", "Tournament", playerCap: 4, isPublic: false, password: "secret");

            socialSink.PublishSystemBroadcast(privLobby.LobbyId, "system_takeover", "私密房内部托管机密动态");
            socialSink.PublishSystemBroadcast(null, "system_ten_win", "全服十胜公开战报");

            using var resp = await http.GetAsync("/api/social/announcements?limit=30");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string body = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(body);
            var contents = doc.RootElement.GetProperty("items")
                .EnumerateArray()
                .Select(x => (
                    ChannelType: x.GetProperty("channelType").GetString() ?? "",
                    Content: x.GetProperty("content").GetString() ?? ""))
                .ToList();

            Assert.Contains(contents, c => c.ChannelType == "system" && c.Content.Contains("全服十胜公开战报", StringComparison.Ordinal));
            Assert.DoesNotContain(contents, c => c.Content.Contains("私密房内部托管机密动态", StringComparison.Ordinal));
            Assert.All(contents, c => Assert.Equal("system", c.ChannelType));
        }
        finally
        {
            await StopHostAsync(app, http, tempDir);
        }
    }

    [Fact]
    public async Task SocialChatAndSse_PrivatePasswordInviteAndArchivedLobbies_MustBlockAnonAndNonMembers()
    {
        var (app, http, tempDir) = await StartSocialTestHostAsync();
        try
        {
            var accounts = app.Services.GetRequiredService<AccountStore>();
            var lobbies = app.Services.GetRequiredService<LobbyStore>();

            // 使用 4 个独立房主账号避免 LobbyStore.Create 自动清理前房
            var hostPriv = accounts.GetOrCreateByName("SocPrivHost1", "game", "d1");
            var hostPwd = accounts.GetOrCreateByName("SocPwdHost2", "game", "d2");
            var hostInv = accounts.GetOrCreateByName("SocInvHost3", "game", "d3");
            var hostPub = accounts.GetOrCreateByName("SocPubHost4", "game", "d4");
            var mallory = accounts.GetOrCreateByName("SocPrivMallory", "game", "d5");

            var privLobby = lobbies.Create(hostPriv, "私密房", "Tournament", playerCap: 4, isPublic: false);
            var pwdLobby = lobbies.Create(hostPwd, "密码房", "Tournament", playerCap: 4, isPublic: true, password: "p1234");
            var invLobby = lobbies.Create(hostInv, "邀请房", "Tournament", playerCap: 4, isPublic: true, isInviteOnly: true);
            var pubLobby = lobbies.Create(hostPub, "公开房", "Tournament", playerCap: 4, isPublic: true);

            // 1. 成员发送私密房聊天 -> 200
            using (var okPost = await http.SendAsync(Authed(HttpMethod.Post, $"/api/lobbies/{privLobby.LobbyId}/chat", hostPriv.AccessToken, new { content = "仅成员可见消息" })))
                Assert.Equal(HttpStatusCode.OK, okPost.StatusCode);

            // 2. 匿名窃听私密房/密码房/邀请房 GET 聊天与 SSE -> 401
            foreach (var restricted in new[] { privLobby, pwdLobby, invLobby })
            {
                using var r1 = await http.GetAsync($"/api/social/chat?channel=lobby&lobbyId={restricted.LobbyId}");
                Assert.Equal(HttpStatusCode.Unauthorized, r1.StatusCode);

                using var r2 = await http.GetAsync($"/api/lobbies/{restricted.LobbyId}/chat");
                Assert.Equal(HttpStatusCode.Unauthorized, r2.StatusCode);

                using var r3 = await http.GetAsync($"/api/social/events?scope=lobby&lobbyId={restricted.LobbyId}");
                Assert.Equal(HttpStatusCode.Unauthorized, r3.StatusCode);
            }

            // 2b. 尝试用 X-Username 降级伪造房主窃听私密房 -> 仍必须返回 401（allowLegacyUsernameFallback: false）
            using (var legacySpoof = new HttpRequestMessage(HttpMethod.Get, $"/api/lobbies/{privLobby.LobbyId}/chat"))
            {
                legacySpoof.Headers.TryAddWithoutValidation("X-Username", hostPriv.Username);
                using var spoofResp = await http.SendAsync(legacySpoof);
                Assert.Equal(HttpStatusCode.Unauthorized, spoofResp.StatusCode);
            }

            // 3. 非成员已登录用户访问私密房 GET 聊天与 SSE -> 403
            using (var nm1 = await http.SendAsync(Authed(HttpMethod.Get, $"/api/social/chat?channel=lobby&lobbyId={privLobby.LobbyId}", mallory.AccessToken)))
                Assert.Equal(HttpStatusCode.Forbidden, nm1.StatusCode);
            using (var nm2 = await http.SendAsync(Authed(HttpMethod.Get, $"/api/lobbies/{privLobby.LobbyId}/chat", mallory.AccessToken)))
                Assert.Equal(HttpStatusCode.Forbidden, nm2.StatusCode);
            using (var nm3 = await http.SendAsync(Authed(HttpMethod.Get, $"/api/social/events?scope=lobby&lobbyId={privLobby.LobbyId}", mallory.AccessToken)))
                Assert.Equal(HttpStatusCode.Forbidden, nm3.StatusCode);

            // 4. 非成员（即使对公开房）POST 跨房注入发言 -> 403
            using (var inj1 = await http.SendAsync(Authed(HttpMethod.Post, "/api/social/chat", mallory.AccessToken, new { channel = "lobby", lobbyId = pubLobby.LobbyId, content = "跨房注入" })))
                Assert.Equal(HttpStatusCode.Forbidden, inj1.StatusCode);
            using (var inj2 = await http.SendAsync(Authed(HttpMethod.Post, $"/api/lobbies/{privLobby.LobbyId}/chat", mallory.AccessToken, new { content = "跨房注入2" })))
                Assert.Equal(HttpStatusCode.Forbidden, inj2.StatusCode);

            // 5. 不存在/已归档的历史房 lobbyId：匿名 401，已登录（即使本人正处于其他活跃房）返回 404，绝不回退 GetByMember
            using (var ghostAnon = await http.GetAsync("/api/lobbies/archived_secret_999/chat"))
                Assert.Equal(HttpStatusCode.Unauthorized, ghostAnon.StatusCode);
            using (var ghostAuthed = await http.SendAsync(Authed(HttpMethod.Get, "/api/lobbies/archived_secret_999/chat", hostPriv.AccessToken)))
                Assert.Equal(HttpStatusCode.NotFound, ghostAuthed.StatusCode);
            using (var ghostSse = await http.SendAsync(Authed(HttpMethod.Get, "/api/social/events?scope=lobby&lobbyId=missing_lobby_id", hostPriv.AccessToken)))
                Assert.Equal(HttpStatusCode.NotFound, ghostSse.StatusCode);

            // 6. GET / POST /api/social/chat 显式 channel=lobby|spectate 缺 lobbyId -> 400 LOBBY_ID_REQUIRED
            using (var missing1 = await http.GetAsync("/api/social/chat?channel=lobby"))
                Assert.Equal(HttpStatusCode.BadRequest, missing1.StatusCode);
            using (var missing2 = await http.GetAsync("/api/social/chat?channel=spectate"))
                Assert.Equal(HttpStatusCode.BadRequest, missing2.StatusCode);
            using (var missingPost = await http.SendAsync(Authed(HttpMethod.Post, "/api/social/chat", hostPriv.AccessToken, new { channel = "lobby", content = "缺房号" })))
                Assert.Equal(HttpStatusCode.BadRequest, missingPost.StatusCode);

            // 7. 真实成员带 Bearer Token 读取私密房聊天 -> 200 且可见消息；公开房匿名 GET 保持兼容 200
            using (var memberGet = await http.SendAsync(Authed(HttpMethod.Get, $"/api/lobbies/{privLobby.LobbyId}/chat", hostPriv.AccessToken)))
            {
                Assert.Equal(HttpStatusCode.OK, memberGet.StatusCode);
                using var doc = JsonDocument.Parse(await memberGet.Content.ReadAsStringAsync());
                var texts = doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(x => x.GetProperty("content").GetString() ?? "")
                    .ToList();
                Assert.Contains("仅成员可见消息", texts);
            }
            using (var pubAnonGet = await http.GetAsync($"/api/social/chat?channel=lobby&lobbyId={pubLobby.LobbyId}"))
                Assert.Equal(HttpStatusCode.OK, pubAnonGet.StatusCode);
        }
        finally
        {
            await StopHostAsync(app, http, tempDir);
        }
    }
}

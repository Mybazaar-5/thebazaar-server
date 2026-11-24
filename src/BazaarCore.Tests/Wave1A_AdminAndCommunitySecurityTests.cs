using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using BazaarServer.Accounts;
using BazaarServer.Game;
using BazaarServer.Mods;
using BazaarServer.Storage;
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
/// Wave 1A 回归测试（文件 3）：覆盖 P0-36（全服统一 AdminAccessGuard、反代头防伪造、/api/mods/* 鉴权、readonly/token-only 模式）
/// 与 P0-37（社区 Mod 归属防伪造、匿名高熵 Secret 覆盖更新保留不变量、report+validation 双字段响应、10MB 流式截断、并发无死锁与 TOCTOU）。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_AdminAndCommunitySecurityTests
{
    private sealed class SecHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required string TempRoot { get; init; }
        public required AccountStore Accounts { get; init; }
        public required ServerModHost ModHost { get; init; }
        public required CommunityModStore CommunityStore { get; init; }

        public async ValueTask DisposeAsync()
        {
            try { Http.Dispose(); } catch { }
            try { await App.StopAsync(); } catch { }
            try { await App.DisposeAsync(); } catch { }
            try { App.Services.GetService<Persistence>()?.Dispose(); } catch { }
            SqliteConnection.ClearAllPools();
            ModRuntimeHooks.ClearAll();
            try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); } catch { }
        }
    }

    private static async Task<SecHost> StartSecurityHostAsync(Func<string, string?>? envProvider = null)
    {
        var fx = SharedGameDataFixture.Shared;
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_sec_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        var db = new Persistence(Path.Combine(stateDir, "state.db"));
        var accounts = new AccountStore(db);
        var modHost = new ServerModHost(fx.Repo, fx.DataDir, stateDir, fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
        var communityStore = new CommunityModStore(modHost, stateDir);
        var validator = new WorkshopValidatorService(modHost);
        var guard = envProvider != null ? new AdminAccessGuard(envProvider) : new AdminAccessGuard(_ => null);

        var builder = WebApplication.CreateBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton(accounts);
        builder.Services.AddSingleton(new SessionStore());
        builder.Services.AddSingleton(modHost);
        builder.Services.AddSingleton(communityStore);
        builder.Services.AddSingleton(validator);
        builder.Services.AddSingleton(guard);

        var app = builder.Build();
        ModEndpoints.Map(app);
        WorkshopEndpoints.Map(app);
        CommunityModEndpoints.Map(app);
        await app.StartAsync();

        string baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(20) };

        return new SecHost
        {
            App = app,
            Http = http,
            TempRoot = tempRoot,
            Accounts = accounts,
            ModHost = modHost,
            CommunityStore = communityStore
        };
    }

    private static byte[] BuildValidModZip(string modId, string version = "1.0.0")
    {
        string srcDir = Path.Combine(SharedGameDataFixture.Shared.RepoRoot, "mods", "clockwork-forge");
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;
                var entry = archive.CreateEntry($"{modId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8)
                        .Replace("clockwork-forge", modId, StringComparison.OrdinalIgnoreCase);
                    if (rel.Equals("mod.json", StringComparison.OrdinalIgnoreCase) && version != "1.0.0")
                        text = text.Replace("\"1.0.0\"", $"\"{version}\"", StringComparison.Ordinal);
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    es.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    using var fs = File.OpenRead(file);
                    fs.CopyTo(es);
                }
            }
        }
        return ms.ToArray();
    }

    private static MultipartFormDataContent MakeZipForm(byte[] zipBytes, string modId)
    {
        var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(zipBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", $"{modId}.bazaarmod.zip");
        return form;
    }

    [Fact]
    public async Task AdminAccessGuard_SpoofedLoopbackXff_MustReturn403AcrossAllAdminEndpoints()
    {
        const string secretToken = "SuperSecretToken_Wave1A_987654";

        // 1. 默认 auto 模式 + 配置管理员口令：任何伪造回环代理头必须全部 403，Query ?token= 也必须 403，仅正确 X-Workshop-Token 头放行
        await using (var h = await StartSecurityHostAsync(k => k == "BAZAAR_WORKSHOP_TOKEN" ? secretToken : null))
        {
            var spoofHeaders = new (string Name, string Value)[]
            {
                ("X-Forwarded-For", "127.0.0.1"),
                ("X-Forwarded-For", "127.0.0.1, 8.8.8.8"), // zero-leak:allow-ip
                ("X-Real-IP", "127.0.0.1"),
                ("Forwarded", "for=127.0.0.1")
            };

            foreach (var (hn, hv) in spoofHeaders)
            {
                // POST /api/mods/reload -> 403
                using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/mods/reload"))
                {
                    req.Headers.TryAddWithoutValidation(hn, hv);
                    using var resp = await h.Http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                }
                // POST /api/mods/{id}/toggle -> 403
                using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/mods/nonexistent/toggle") { Content = JsonContent.Create(new { enabled = false }) })
                {
                    req.Headers.TryAddWithoutValidation(hn, hv);
                    using var resp = await h.Http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                }
                // POST /api/workshop/mods -> 403
                using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/workshop/mods") { Content = JsonContent.Create(new { modId = "spoof-x" }) })
                {
                    req.Headers.TryAddWithoutValidation(hn, hv);
                    using var resp = await h.Http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                }
                // POST /api/community/mods/{id}/server-default -> 403
                using (var req = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/spoof-x/server-default") { Content = JsonContent.Create(new { enabled = true }) })
                {
                    req.Headers.TryAddWithoutValidation(hn, hv);
                    using var resp = await h.Http.SendAsync(req);
                    Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                }
            }

            // Query ?token= 在社区管理端必须返回 403（已废除 Query 传密防日志泄漏）
            using (var qReq = new HttpRequestMessage(HttpMethod.Post, $"/api/community/mods/any-mod/server-default?token={secretToken}"))
            {
                qReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.5");
                qReq.Content = JsonContent.Create(new { enabled = true });
                using var qResp = await h.Http.SendAsync(qReq);
                Assert.Equal(HttpStatusCode.Forbidden, qResp.StatusCode);
            }

            // 携带正确 X-Workshop-Token 请求头即使带 X-Forwarded-For 也放行（200）
            using (var okReq = new HttpRequestMessage(HttpMethod.Post, "/api/mods/reload"))
            {
                okReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.5");
                okReq.Headers.TryAddWithoutValidation("X-Workshop-Token", secretToken);
                using var okResp = await h.Http.SendAsync(okReq);
                Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            }
        }

        // 2. BAZAAR_WORKSHOP_MODE=readonly 下，即便本机直连 + 正确 Token 亦一律返回 403
        await using (var ro = await StartSecurityHostAsync(k => k switch
        {
            "BAZAAR_WORKSHOP_MODE" => "readonly",
            "BAZAAR_WORKSHOP_TOKEN" => secretToken,
            _ => null
        }))
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/mods/reload");
            req.Headers.TryAddWithoutValidation("X-Workshop-Token", secretToken);
            using var resp = await ro.Http.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

            using var commReq = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/any/server-default") { Content = JsonContent.Create(new { enabled = true }) };
            commReq.Headers.TryAddWithoutValidation("X-Workshop-Token", secretToken);
            using var commResp = await ro.Http.SendAsync(commReq);
            Assert.Equal(HttpStatusCode.Forbidden, commResp.StatusCode);
        }

        // 3. BAZAAR_WORKSHOP_MODE=token-only 下，无代理头本机直连无 Token 亦必须返回 403
        await using (var to = await StartSecurityHostAsync(k => k switch
        {
            "BAZAAR_WORKSHOP_MODE" => "token-only",
            "BAZAAR_WORKSHOP_TOKEN" => secretToken,
            _ => null
        }))
        {
            using var noTok = await to.Http.PostAsync("/api/mods/reload", content: null);
            Assert.Equal(HttpStatusCode.Forbidden, noTok.StatusCode);

            using var withTok = new HttpRequestMessage(HttpMethod.Post, "/api/mods/reload");
            withTok.Headers.TryAddWithoutValidation("X-Workshop-Token", secretToken);
            using var tokResp = await to.Http.SendAsync(withTok);
            Assert.Equal(HttpStatusCode.OK, tokResp.StatusCode);
        }
    }

    [Fact]
    public async Task CommunityMod_SpoofedPlayerName_CannotDeleteOrOverwriteOthersMod()
    {
        await using var h = await StartSecurityHostAsync();
        var alice = h.Accounts.GetOrCreateByName("RealOwnerAlice", "game", "dev1");
        const string modId = "sec-mod-a";
        byte[] zip = BuildValidModZip(modId);

        // 1. 玩家 A 凭真实 Bearer Token 上传
        using (var upReq = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            upReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.11");
            upReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.AccessToken);
            upReq.Content = MakeZipForm(zip, modId);
            using var upResp = await h.Http.SendAsync(upReq);
            Assert.Equal(HttpStatusCode.OK, upResp.StatusCode);
        }

        // 2. 攻击者无 Token 仅伪造 X-Player-Name 尝试删除 -> 403
        using (var delSpoof = new HttpRequestMessage(HttpMethod.Delete, $"/api/community/mods/{modId}"))
        {
            delSpoof.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
            delSpoof.Headers.TryAddWithoutValidation("X-Player-Name", alice.Username);
            using var resp = await h.Http.SendAsync(delSpoof);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 3. 攻击者无 Token 仅伪造 X-Player-Name 尝试覆盖上传 -> 403
        using (var ovSpoof = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            ovSpoof.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
            ovSpoof.Headers.TryAddWithoutValidation("X-Player-Name", alice.Username);
            ovSpoof.Content = MakeZipForm(BuildValidModZip(modId, "2.0.0"), modId);
            using var resp = await h.Http.SendAsync(ovSpoof);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 4. 攻击者用 X-Username 降级头尝试删除 -> 401/403
        using (var uSpoof = new HttpRequestMessage(HttpMethod.Delete, $"/api/community/mods/{modId}"))
        {
            uSpoof.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.99");
            uSpoof.Headers.TryAddWithoutValidation("X-Username", alice.Username);
            using var resp = await h.Http.SendAsync(uSpoof);
            Assert.True(resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        }

        // 5. 玩家 A 凭真实 Bearer Token 删除 -> 200
        using (var okDel = new HttpRequestMessage(HttpMethod.Delete, $"/api/community/mods/{modId}"))
        {
            okDel.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.11");
            okDel.Headers.Authorization = new AuthenticationHeaderValue("Bearer", alice.AccessToken);
            using var resp = await h.Http.SendAsync(okDel);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CommunityMod_AnonymousUploaderSecret_RequiredForDeleteAndOverwrite()
    {
        await using var h = await StartSecurityHostAsync();
        const string modId = "anon-secret-mod";
        const string zhAuthor = "匿名工匠";
        string issuedSecret;

        // 1. 匿名用户携带中文 X-Player-Name 上传 -> 200，返回高熵 uploaderSecret，且 mod 对象不暴露 uploaderSecretHash
        using (var up = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            up.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            up.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(zhAuthor));
            up.Content = MakeZipForm(BuildValidModZip(modId, "1.0.0"), modId);
            using var resp = await h.Http.SendAsync(up);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            string body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            issuedSecret = doc.RootElement.GetProperty("uploaderSecret").GetString() ?? "";
            Assert.True(issuedSecret.Length >= 32, "匿名上传应签发高熵 uploaderSecret");
            Assert.False(doc.RootElement.GetProperty("mod").TryGetProperty("uploaderSecretHash", out _), "API 绝不可泄露 uploaderSecretHash");
        }

        // 2. 仅同名 X-Player-Name 查列表 canDelete == false，尝试覆盖与删除均 403
        using (var listNoSec = new HttpRequestMessage(HttpMethod.Get, "/api/community/mods"))
        {
            listNoSec.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            listNoSec.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(zhAuthor));
            using var resp = await h.Http.SendAsync(listNoSec);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var item = doc.RootElement.GetProperty("mods").EnumerateArray().Single(m => m.GetProperty("modId").GetString() == modId);
            Assert.False(item.GetProperty("canDelete").GetBoolean());
        }
        using (var badDel = new HttpRequestMessage(HttpMethod.Delete, $"/api/community/mods/{modId}"))
        {
            badDel.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            badDel.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(zhAuthor));
            badDel.Headers.TryAddWithoutValidation("X-Uploader-Secret", "wrong_secret_value");
            using var resp = await h.Http.SendAsync(badDel);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 3. 携带正确 X-Uploader-Secret 覆盖更新 v1.1.0 -> 200，不重发 uploaderSecret，且原 Secret 哈希完整继承不洗白！
        using (var ovOk = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            ovOk.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            ovOk.Headers.TryAddWithoutValidation("X-Player-Name", Uri.EscapeDataString(zhAuthor));
            ovOk.Headers.TryAddWithoutValidation("X-Uploader-Secret", issuedSecret);
            ovOk.Content = MakeZipForm(BuildValidModZip(modId, "1.1.0"), modId);
            using var resp = await h.Http.SendAsync(ovOk);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.False(doc.RootElement.TryGetProperty("uploaderSecret", out var secProp) && secProp.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(secProp.GetString()));
            Assert.Equal("1.1.0", doc.RootElement.GetProperty("mod").GetProperty("version").GetString());
        }

        // 4. 覆盖更新后凭同一原 Secret 查列表 canDelete == true，并可成功删除返回 200
        using (var listWithSec = new HttpRequestMessage(HttpMethod.Get, "/api/community/mods"))
        {
            listWithSec.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            listWithSec.Headers.TryAddWithoutValidation("X-Uploader-Secret", issuedSecret);
            using var resp = await h.Http.SendAsync(listWithSec);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var item = doc.RootElement.GetProperty("mods").EnumerateArray().Single(m => m.GetProperty("modId").GetString() == modId);
            Assert.True(item.GetProperty("canDelete").GetBoolean(), "覆盖更新后原 X-Uploader-Secret 必须继续保有所有权");
        }
        using (var okDel = new HttpRequestMessage(HttpMethod.Delete, $"/api/community/mods/{modId}"))
        {
            okDel.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.20");
            okDel.Headers.TryAddWithoutValidation("X-Uploader-Secret", issuedSecret);
            using var resp = await h.Http.SendAsync(okDel);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CommunityMod_ValidationFailure_ReturnsBothReportAndValidation_AndRejectsOver10MbRawBody()
    {
        await using var h = await StartSecurityHostAsync();

        // 1. 构造含致命错误（CooldownMax=3 < 1000ms）的合法 ZIP，断言 422 同时携带 report 与 validation
        var fx = SharedGameDataFixture.Shared;
        string carrierId = fx.Repo.CardsById.Values.First(c => c.Type == "Item" && c.Size == "Small").Id;
        byte[] badModZip;
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var mEntry = archive.CreateEntry("bad-cd-mod/mod.json");
                using (var s = mEntry.Open())
                {
                    s.Write(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"id\":\"bad-cd-mod\",\"name\":\"坏冷却包\",\"version\":\"1.0.0\",\"author\":\"t\",\"scope\":\"multiplayer-safe\"}"));
                }
                var cEntry = archive.CreateEntry("bad-cd-mod/cards/custom_cards.json");
                using (var s = cEntry.Open())
                {
                    string badCardJson = $$"""
                        {
                          "schemaVersion": 1,
                          "cards": [
                            {
                              "customId": "bad-cd-mod:broken_cd",
                              "baseVisualTemplateId": "{{carrierId}}",
                              "type": "Item",
                              "size": "Small",
                              "startingTier": "Bronze",
                              "heroes": ["Common"],
                              "titleZh": "坏冷却短刃",
                              "descZh": "故意把冷却写成 3 秒而非 3000 毫秒",
                              "tiers": {
                                "Bronze": { "attributes": { "BuyPrice": 2, "SellPrice": 1, "CooldownMax": 3, "DamageAmount": 10 }, "abilityIds": [] }
                              },
                              "abilities": {}
                            }
                          ]
                        }
                        """;
                    s.Write(Encoding.UTF8.GetBytes(badCardJson));
                }
            }
            badModZip = ms.ToArray();
        }

        using (var failReq = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            failReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.30");
            failReq.Headers.TryAddWithoutValidation("X-Player-Name", "BadUploader");
            failReq.Content = MakeZipForm(badModZip, "bad-cd-mod");
            using var resp = await h.Http.SendAsync(failReq);
            Assert.Equal((HttpStatusCode)422, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.TryGetProperty("report", out var reportProp));
            Assert.True(doc.RootElement.TryGetProperty("validation", out var validationProp));
            Assert.True(reportProp.GetProperty("errorCount").GetInt32() > 0);
            Assert.True(validationProp.GetProperty("errorCount").GetInt32() > 0);
        }

        // 2. 发送 11 MB 原始二进制流 -> 必须流式截断并返回 HTTP 413 Payload Too Large
        byte[] overLimit = new byte[11 * 1024 * 1024];
        using (var bigReq = new HttpRequestMessage(HttpMethod.Post, "/api/community/mods/upload"))
        {
            bigReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.30");
            bigReq.Headers.TryAddWithoutValidation("X-Player-Name", "BigUploader");
            bigReq.Content = new ByteArrayContent(overLimit);
            bigReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var resp = await h.Http.SendAsync(bigReq);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        }
    }

    [Fact]
    public async Task CommunityModStore_And_ServerModHost_ConcurrentUploadAndResolve_ZeroDeadlock()
    {
        await using var h = await StartSecurityHostAsync();
        var fx = SharedGameDataFixture.Shared;
        byte[] zipA = BuildValidModZip("race-mod-a");
        byte[] zipB = BuildValidModZip("race-mod-b");

        // 先让 acc_owner 抢先占下 race-mod-a
        var seedUp = await h.CommunityStore.UploadZipAsync(zipA, "acc_owner", "Owner", isAdmin: false);
        Assert.True(seedUp.Ok, seedUp.Error);

        // 并发测试：上传/删除（含非法抢占 TOCTOU 检测）与 ServerModHost.ResolveRunModContext 交叉执行，限时 15s 必须 0 死锁完成
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pool = fx.CreateIsolatedCardPool();
        var monsters = fx.CreateMonstersCopy();

        var resolvers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 60; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                var ctx = h.ModHost.ResolveRunModContext(["race-mod-a", "race-mod-b"], pool, monsters, fx.ArchetypeIndex);
                Assert.NotNull(ctx.Repository);
            }
        }, cts.Token)).ToArray();

        // 非拥有者抢占覆盖 race-mod-a 必被拦截 403；并发上传/删除 race-mod-b 与解析交叉无 AB-BA 死锁
        int forbiddenCount = 0;
        var mutators = new Task[]
        {
            Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    var hijack = await h.CommunityStore.UploadZipAsync(zipA, "acc_attacker", "Attacker", isAdmin: false, cts.Token);
                    Assert.False(hijack.Ok);
                    Assert.Equal(403, hijack.StatusCode);
                    Interlocked.Increment(ref forbiddenCount);
                }
            }, cts.Token),
            Task.Run(async () =>
            {
                for (int i = 0; i < 2; i++)
                {
                    var upB = await h.CommunityStore.UploadZipAsync(zipB, "acc_b_owner", "OwnerB", isAdmin: true, cts.Token);
                    Assert.True(upB.Ok, upB.Error);
                    var delB = h.CommunityStore.DeleteCommunityMod("race-mod-b", "acc_b_owner", isAdmin: true);
                    Assert.True(delB.Ok, delB.Error);
                }
            }, cts.Token)
        };

        var allTask = Task.WhenAll(resolvers.Concat(mutators));
        var completed = await Task.WhenAny(allTask, Task.Delay(TimeSpan.FromSeconds(15)));
        Assert.Same(allTask, completed);
        await allTask;
        Assert.Equal(3, forbiddenCount);
    }
}

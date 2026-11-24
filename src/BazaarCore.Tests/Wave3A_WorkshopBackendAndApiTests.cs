using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BazaarCore.Models;
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
/// Wave 3A 工坊服务端、安全与社区端点集成测试（D5-P1-05/07/08/09、D5-P2-05、P0-37）：
/// 涉及 WebApplication / AdminAccessGuard / ModRuntimeHooks 全局静态钩子，归属 GlobalStaticState 串行集合。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave3A_WorkshopBackendAndApiTests
{
    private const string KukriTemplateId = "03b1fd16-6b0d-4847-a76f-6c586f7c5958";

    private sealed class TestHost : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required HttpClient Http { get; init; }
        public required string TempRoot { get; init; }
        public required string ModsDir { get; init; }
        public required ServerModHost ModHost { get; init; }
        public required WorkshopValidatorService Validator { get; init; }
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

    private static async Task<TestHost> StartHostAsync(
        Func<string, string?>? envProvider = null,
        bool copyClockworkSample = false)
    {
        var fx = SharedGameDataFixture.Shared;
        string tempRoot = Path.Combine(Path.GetTempPath(), $"bazaar_w3a_api_{Guid.NewGuid():N}");
        string modsDir = Path.Combine(tempRoot, "mods");
        string stateDir = Path.Combine(tempRoot, "state");
        Directory.CreateDirectory(modsDir);
        Directory.CreateDirectory(stateDir);

        if (copyClockworkSample)
        {
            string src = Path.Combine(fx.RepoRoot, "mods", "clockwork-forge");
            CopyDirectory(src, Path.Combine(modsDir, "clockwork-forge"));
        }

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
        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };

        return new TestHost
        {
            App = app,
            Http = http,
            TempRoot = tempRoot,
            ModsDir = modsDir,
            ModHost = modHost,
            Validator = validator,
            CommunityStore = communityStore
        };
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, f);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(f, target, overwrite: true);
        }
    }

    private static byte[] BuildValidPng64x64()
    {
        const int w = 64, h = 64;
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        static uint Crc32(byte[] a, byte[] b)
        {
            uint crc = 0xFFFFFFFFu;
            void Feed(byte[] buf)
            {
                foreach (byte x in buf)
                {
                    crc ^= x;
                    for (int k = 0; k < 8; k++)
                        crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : (crc >> 1);
                }
            }
            Feed(a);
            Feed(b);
            return crc ^ 0xFFFFFFFFu;
        }

        void WriteChunk(string type, byte[] data)
        {
            byte[] t = Encoding.ASCII.GetBytes(type);
            ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(data.Length)));
            ms.Write(t);
            ms.Write(data);
            ms.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int)Crc32(t, data))));
        }

        using (var ihdr = new MemoryStream())
        {
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(w)));
            ihdr.Write(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(h)));
            ihdr.Write(new byte[] { 8, 2, 0, 0, 0 });
            WriteChunk("IHDR", ihdr.ToArray());
        }

        byte[] raw = new byte[h * (1 + w * 3)];
        using (var idat = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(raw, 0, raw.Length);
            WriteChunk("IDAT", idat.ToArray());
        }
        WriteChunk("IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    /// <summary>生成最小合法 64×64 JPEG（含 SOI + APP0 + SOF0 64×64 + SOS + EOI，供 Scheme A 魔数与尺寸检验）。</summary>
    private static byte[] BuildValidJpeg64x64()
    {
        return new byte[]
        {
            0xFF, 0xD8,                                                 // SOI
            0xFF, 0xE0, 0x00, 0x10,                                     // APP0 len=16
            0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00,             // JFIF\0 v1.1
            0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11,                                     // SOF0 len=17
            0x08,                                                       // precision 8
            0x00, 0x40,                                                 // height = 64
            0x00, 0x40,                                                 // width  = 64
            0x03,                                                       // 3 components
            0x01, 0x11, 0x00,
            0x02, 0x11, 0x01,
            0x03, 0x11, 0x01,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, // SOS
            0x00,                                                       // dummy entropy byte
            0xFF, 0xD9                                                  // EOI
        };
    }

    private static CustomCardDefinition BuildHealthyWeapon(string modId, string slug, string? artFile = null)
    {
        return new CustomCardDefinition
        {
            CustomId = $"{modId}:{slug}",
            TitleZh = "测试锋刃",
            TitleEn = "Test Blade",
            Type = "Item",
            Size = "Small",
            StartingTier = "Bronze",
            Heroes = ["Vanessa"],
            Tags = ["Weapon"],
            BaseVisualTemplateId = KukriTemplateId,
            CustomArtFile = artFile,
            Tiers = new Dictionary<string, TierData>(StringComparer.OrdinalIgnoreCase)
            {
                ["Bronze"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 10, ["BuyPrice"] = 2, ["SellPrice"] = 1 } },
                ["Silver"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 20, ["BuyPrice"] = 4, ["SellPrice"] = 2 } },
                ["Gold"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 40, ["BuyPrice"] = 8, ["SellPrice"] = 4 } },
                ["Diamond"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 80, ["BuyPrice"] = 16, ["SellPrice"] = 8 } },
                ["Legendary"] = new TierData { Attributes = new Dictionary<string, double> { ["CooldownMax"] = 4000, ["DamageAmount"] = 120, ["BuyPrice"] = 24, ["SellPrice"] = 12 } }
            },
            ActiveTooltipsZh = ["造成 {attr.DamageAmount} 点伤害。"]
        };
    }

    private static byte[] BuildValidModZipFromClockwork(string newModId)
    {
        string srcDir = Path.Combine(SharedGameDataFixture.Shared.RepoRoot, "mods", "clockwork-forge");
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;
                var entry = archive.CreateEntry($"{newModId}/{rel}", CompressionLevel.Optimal);
                using var es = entry.Open();
                if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    string text = File.ReadAllText(file, Encoding.UTF8)
                        .Replace("clockwork-forge", newModId, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task WorkshopHtmlMiddleware_HeadZeroBytes_AssetPrefix_AndDynamicVersionAndVfxCount()
    {
        await using var host = await StartHostAsync();

        // 1. GET /workshop, /workshop/, /workshop/index.html 均完成 {{ASSET_PREFIX}} 注入且无模板泄漏
        var rNoSlash = await host.Http.GetAsync("/workshop");
        Assert.Equal(HttpStatusCode.OK, rNoSlash.StatusCode);
        string bodyNoSlash = await rNoSlash.Content.ReadAsStringAsync();
        Assert.DoesNotContain("{{ASSET_PREFIX}}", bodyNoSlash);
        Assert.Contains("./workshop/styles.css", bodyNoSlash);

        var rSlash = await host.Http.GetAsync("/workshop/");
        Assert.Equal(HttpStatusCode.OK, rSlash.StatusCode);
        string bodySlash = await rSlash.Content.ReadAsStringAsync();
        Assert.DoesNotContain("{{ASSET_PREFIX}}", bodySlash);
        Assert.Contains("./styles.css", bodySlash);

        var rIndex = await host.Http.GetAsync("/workshop/index.html");
        Assert.Equal(HttpStatusCode.OK, rIndex.StatusCode);
        string bodyIndex = await rIndex.Content.ReadAsStringAsync();
        Assert.DoesNotContain("{{ASSET_PREFIX}}", bodyIndex);
        Assert.Contains("./styles.css", bodyIndex);

        // 2. HEAD 全部返回 200 且响应体严格为 0 字节、携带准确 Content-Length
        foreach (string path in new[] { "/workshop", "/workshop/", "/workshop/index.html" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, path);
            var resp = await host.Http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            byte[] headBytes = await resp.Content.ReadAsByteArrayAsync();
            Assert.Empty(headBytes);
            Assert.True(resp.Content.Headers.ContentLength > 1000, $"HEAD {path} 应返回真实 Content-Length");
        }

        // 3. /api/workshop/meta 与 /api/mods/manifest 均动态读取版本（绝非硬编码 "2.0.0"）且 VFX 数为 133
        var meta = await host.Http.GetFromJsonAsync<JsonElement>("/api/workshop/meta");
        string wsVer = meta.GetProperty("serverVersion").GetString() ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(wsVer));
        Assert.NotEqual("2.0.0", wsVer);
        Assert.Equal(133, meta.GetProperty("vfxKeyCount").GetInt32());

        var manifest = await host.Http.GetFromJsonAsync<JsonElement>("/api/mods/manifest");
        string modsVer = manifest.GetProperty("serverVersion").GetString() ?? string.Empty;
        Assert.Equal(wsVer, modsVer);
    }

    [Fact]
    public async Task Assets_SchemeA_PngAndJpeg_RejectWebp_FakePng_AndPathTraversal()
    {
        await using var host = await StartHostAsync();
        var ws = new WorkshopModWorkspace
        {
            Manifest = new ModManifest { Id = "w3a-assets", Name = "资产测试", Version = "1.0.0", Scope = "multiplayer-safe" },
            Cards = [BuildHealthyWeapon("w3a-assets", "blade_1")]
        };
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsJsonAsync("/api/workshop/mods", ws)).StatusCode);

        // 1. 合法 PNG -> 200，.png，预览 Content-Type=image/png
        using (var form = new MultipartFormDataContent())
        {
            var fc = new ByteArrayContent(BuildValidPng64x64());
            fc.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(fc, "file", "art_png.png");
            form.Add(new StringContent("art_png"), "logicalName");
            var up = await host.Http.PostAsync("/api/workshop/mods/w3a-assets/assets", form);
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
            var json = await up.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("assets/art_png.png", json.GetProperty("logicalPath").GetString());
            Assert.Equal("image/png", json.GetProperty("contentType").GetString());
        }
        var getPng = await host.Http.GetAsync("/api/workshop/mods/w3a-assets/assets/assets/art_png.png");
        Assert.Equal(HttpStatusCode.OK, getPng.StatusCode);
        Assert.Equal("image/png", getPng.Content.Headers.ContentType?.MediaType);

        // 2. 合法 JPEG -> 200，.jpg，预览 Content-Type=image/jpeg
        using (var form = new MultipartFormDataContent())
        {
            var fc = new ByteArrayContent(BuildValidJpeg64x64());
            fc.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(fc, "file", "art_jpg.jpeg");
            form.Add(new StringContent("art_jpg"), "logicalName");
            var up = await host.Http.PostAsync("/api/workshop/mods/w3a-assets/assets", form);
            Assert.Equal(HttpStatusCode.OK, up.StatusCode);
            var json = await up.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("assets/art_jpg.jpg", json.GetProperty("logicalPath").GetString());
            Assert.Equal("image/jpeg", json.GetProperty("contentType").GetString());
        }
        var getJpg = await host.Http.GetAsync("/api/workshop/mods/w3a-assets/assets/assets/art_jpg.jpg");
        Assert.Equal(HttpStatusCode.OK, getJpg.StatusCode);
        Assert.Equal("image/jpeg", getJpg.Content.Headers.ContentType?.MediaType);

        // 3. WebP 必须被拒（400）
        using (var form = new MultipartFormDataContent())
        {
            byte[] webp = Encoding.ASCII.GetBytes("RIFF\x20\x00\x00\x00WEBPVP8X01234567890123456789");
            form.Add(new ByteArrayContent(webp), "file", "bad.webp");
            var bad = await host.Http.PostAsync("/api/workshop/mods/w3a-assets/assets", form);
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        // 4. TryResolveDraftAssetFile 严限 assets/ 与真实魔数（拒绝假 .png、mod.json、../ 穿越）
        string fakePngPath = Path.Combine(host.ModsDir, "w3a-assets", "assets", "fake.png");
        File.WriteAllText(fakePngPath, "NOT_A_PNG_FILE_PAYLOAD", Encoding.UTF8);

        Assert.False(host.Validator.TryResolveDraftAssetFile("w3a-assets", "assets/fake.png", out _, out _));
        Assert.False(host.Validator.TryResolveDraftAssetFile("w3a-assets", "mod.json", out _, out _));
        Assert.False(host.Validator.TryResolveDraftAssetFile("w3a-assets", "../mod.json", out _, out _));
        Assert.True(host.Validator.TryResolveDraftAssetFile("w3a-assets", "assets/art_png.png", out _, out string mimePng));
        Assert.Equal("image/png", mimePng);
        Assert.True(host.Validator.TryResolveDraftAssetFile("w3a-assets", "assets/art_jpg.jpg", out _, out string mimeJpg));
        Assert.Equal("image/jpeg", mimeJpg);

        var modJsonResp = await host.Http.GetAsync("/api/workshop/mods/w3a-assets/assets/mod.json");
        Assert.NotEqual(HttpStatusCode.OK, modJsonResp.StatusCode);
    }

    [Fact]
    public async Task SaveWorkspaceAtomic_PreservesCustomRoots_UnmanagedFiles_AndConcurrentSavesSafe()
    {
        await using var host = await StartHostAsync();
        const string modId = "w3a-atomic-mod";

        var initialWs = new WorkshopModWorkspace
        {
            Manifest = new ModManifest
            {
                Id = modId,
                Name = "原子切换测试",
                Version = "1.0.0",
                Scope = "multiplayer-safe",
                ContentRoots = new ModContentRoots
                {
                    Cards = "cards/*.json",
                    Assets = string.Empty
                }
            },
            Cards = [BuildHealthyWeapon(modId, "blade_init")]
        };

        var (ok1, err1, _, _) = host.Validator.SaveWorkspaceAtomic(initialWs);
        Assert.True(ok1, err1);

        // 校验保留自定义通配 cards/*.json 路径、自动补齐 assets/*、以及未托管 README.md 跨保存保留
        string modDir = Path.Combine(host.ModsDir, modId);
        Assert.True(File.Exists(Path.Combine(modDir, "cards", "custom_cards.json")));
        File.WriteAllText(Path.Combine(modDir, "README.md"), "Keep Me Across Atomic Swap", Encoding.UTF8);

        var savedManifest = JsonSerializer.Deserialize<ModManifest>(
            File.ReadAllText(Path.Combine(modDir, "mod.json"), Encoding.UTF8),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(savedManifest);
        Assert.Equal("cards/*.json", savedManifest!.ContentRoots.Cards);
        Assert.Equal("assets/*", savedManifest.ContentRoots.Assets);

        // 6 并发同时保存同一个 modId：无异常、无死锁、无 .tmp_/.bak_ 残留、README.md 完整保留
        var tasks = Enumerable.Range(1, 6).Select(i => Task.Run(() =>
        {
            var concurrentWs = new WorkshopModWorkspace
            {
                Manifest = new ModManifest
                {
                    Id = modId,
                    Name = $"并发版本-{i}",
                    Version = $"1.0.{i}",
                    Scope = "multiplayer-safe",
                    ContentRoots = new ModContentRoots { Cards = "cards/*.json" }
                },
                Cards = [BuildHealthyWeapon(modId, "blade_init")]
            };
            return host.Validator.SaveWorkspaceAtomic(concurrentWs);
        })).ToArray();

        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal("Keep Me Across Atomic Swap", File.ReadAllText(Path.Combine(modDir, "README.md"), Encoding.UTF8));

        var leftoverDirs = Directory.GetDirectories(host.ModsDir)
            .Select(Path.GetFileName)
            .Where(n => n != null && (n.StartsWith(".tmp_", StringComparison.Ordinal) || n.StartsWith(".bak_", StringComparison.Ordinal)))
            .ToList();
        Assert.Empty(leftoverDirs);
    }

    [Fact]
    public async Task Validation_MutatorInvalidJson_Gate1Error_AndMechanicStressGate4Normalization()
    {
        await using var host = await StartHostAsync();
        const string modId = "w3a-bad-mutator";

        // 1. 畸形 Mutators JSON 不应 500 崩溃，而应转化为 Gate #1 MUTATOR_JSON_INVALID Error
        using var badMutatorDoc = JsonDocument.Parse("\"not_a_mutator_object\"");
        var ws = new WorkshopModWorkspace
        {
            Manifest = new ModManifest { Id = modId, Name = "畸形变体测试", Version = "1.0.0", Scope = "multiplayer-safe" },
            Cards = [BuildHealthyWeapon(modId, "blade_ok")],
            Mutators = [badMutatorDoc.RootElement.Clone()]
        };
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsJsonAsync("/api/workshop/mods", ws)).StatusCode);

        var valResp = await host.Http.PostAsJsonAsync($"/api/workshop/mods/{modId}/validate", new { matches = 10, seed = 101 });
        Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);
        var valJson = await valResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(valJson.GetProperty("passed").GetBoolean());
        var issues = valJson.GetProperty("gates").EnumerateArray()
            .SelectMany(g => g.GetProperty("issues").EnumerateArray())
            .ToList();
        Assert.Contains(issues, i =>
            i.GetProperty("code").GetString() == "MUTATOR_JSON_ERROR"
            && i.GetProperty("gateId").GetInt32() == 1);

        // 1b. 内存草稿直校验（不落盘）同样捕获 JsonException 转化为 Gate #1 MUTATOR_JSON_INVALID Error
        var draftValResp = await host.Http.PostAsJsonAsync($"/api/workshop/mods/{modId}/validate", new { draft = ws, matches = 10, seed = 101 });
        Assert.Equal(HttpStatusCode.OK, draftValResp.StatusCode);
        var draftValJson = await draftValResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(draftValJson.GetProperty("passed").GetBoolean());
        var draftIssues = draftValJson.GetProperty("gates").EnumerateArray()
            .SelectMany(g => g.GetProperty("issues").EnumerateArray())
            .ToList();
        Assert.Contains(draftIssues, i =>
            i.GetProperty("code").GetString() == "MUTATOR_JSON_INVALID"
            && i.GetProperty("gateId").GetInt32() == 1);

        // 2. 机制静态校验失败时所有 staticIssues 均归一化为 Gate #4
        var badMech = new MechanicDefinition
        {
            Id = "w3a-bad-mutator:bad_mech",
            NameZh = "坏机制",
            Hooks = [new MechanicHookDefinition { Id = "h1", Hook = "NotARealHook", Steps = [new MechanicStep { Op = "NotARealOp" }] }]
        };
        var stressResp = await host.Http.PostAsJsonAsync("/api/workshop/mechanics/stress-test", new { mechanic = badMech, matches = 10, seed = 7 });
        Assert.Equal(HttpStatusCode.OK, stressResp.StatusCode);
        var stressJson = await stressResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(stressJson.GetProperty("passed").GetBoolean());
        Assert.Equal("static", stressJson.GetProperty("stage").GetString());
        var staticIssues = stressJson.GetProperty("staticIssues").EnumerateArray().ToList();
        Assert.NotEmpty(staticIssues);
        Assert.All(staticIssues, i => Assert.Equal(4, i.GetProperty("gateId").GetInt32()));
    }

    [Fact]
    public async Task ExportAndImport_ErrorPackageBlockedWith422_AllowDraftExportAndStrictImportReject()
    {
        await using var host = await StartHostAsync();
        const string badId = "w3a-broken-cd";

        var badCard = BuildHealthyWeapon(badId, "broken_blade");
        badCard.Tiers!["Bronze"].Attributes["CooldownMax"] = 3; // 致命错误：秒当毫秒
        var ws = new WorkshopModWorkspace
        {
            Manifest = new ModManifest { Id = badId, Name = "带病包导出拦截", Version = "1.0.0", Scope = "multiplayer-safe" },
            Cards = [badCard]
        };
        Assert.Equal(HttpStatusCode.OK, (await host.Http.PostAsJsonAsync("/api/workshop/mods", ws)).StatusCode);

        // 1. 默认导出 Error 包必须返回 HTTP 422
        var defaultExport = await host.Http.GetAsync($"/api/workshop/mods/{badId}/export");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, defaultExport.StatusCode);
        var errJson = await defaultExport.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(errJson.GetProperty("ok").GetBoolean());
        Assert.True(errJson.TryGetProperty("validation", out _));

        // 2. 显式 ?allowDraft=true 允许导出草稿包，且 validation_stamp.json 标注 isDraftExport=true
        var draftExport = await host.Http.GetAsync($"/api/workshop/mods/{badId}/export?allowDraft=true");
        Assert.Equal(HttpStatusCode.OK, draftExport.StatusCode);
        byte[] draftZip = await draftExport.Content.ReadAsByteArrayAsync();
        Assert.True(draftZip.Length > 100);

        using (var za = new ZipArchive(new MemoryStream(draftZip), ZipArchiveMode.Read))
        {
            var stampEntry = za.Entries.FirstOrDefault(e => e.FullName.EndsWith("validation_stamp.json", StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(stampEntry);
            using var sr = new StreamReader(stampEntry!.Open(), Encoding.UTF8);
            var stampJson = JsonDocument.Parse(sr.ReadToEnd()).RootElement;
            Assert.True(stampJson.GetProperty("isDraftExport").GetBoolean());
            Assert.False(stampJson.GetProperty("validationPassed").GetBoolean());
        }

        // 3. 把该带病草稿包重新导入（即使传 allowDraftWithWarnings=true）也必须返回 HTTP 422 阻断！
        using var impForm = new MultipartFormDataContent();
        var fc = new ByteArrayContent(draftZip);
        fc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        impForm.Add(fc, "file", $"{badId}.bazaarmod.zip");
        var impResp = await host.Http.PostAsync("/api/workshop/mods/import?allowDraftWithWarnings=true", impForm);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, impResp.StatusCode);
    }

    [Fact]
    public async Task MechanicCounts_CommunityUploadP037_DownloadSanitize_AndReadOnlyMode()
    {
        // 1. 先在正常可写模式下验证 mechanicCount 全链路、P0-37 Secret/Report 闭环与文件名清洗
        await using (var host = await StartHostAsync(copyClockworkSample: true))
        {
            // 工坊列表与服务端 Bundle 均上报真实 mechanicCount
            var wsMods = await host.Http.GetFromJsonAsync<JsonElement>("/api/workshop/mods");
            var cfEntry = wsMods.GetProperty("mods").EnumerateArray()
                .First(m => m.GetProperty("id").GetString() == "clockwork-forge");
            Assert.True(cfEntry.GetProperty("mechanicCount").GetInt32() >= 1);
            Assert.True(cfEntry.GetProperty("customMechanicCount").GetInt32() >= 1);

            var bundle = await host.Http.GetFromJsonAsync<JsonElement>("/api/mods/bundle/clockwork-forge");
            Assert.True(bundle.GetProperty("customMechanicCount").GetInt32() >= 1);
            Assert.True(bundle.GetProperty("mechanics").GetArrayLength() >= 1);

            // 社区上传合法带机制 Mod -> 返回 uploaderSecret + X-Uploader-Secret 头 + mechanicCount >= 1
            byte[] validZip = BuildValidModZipFromClockwork("w3a-comm-mech");
            using (var form = new MultipartFormDataContent())
            {
                var fc = new ByteArrayContent(validZip);
                fc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                form.Add(fc, "file", "w3a-comm-mech.bazaarmod.zip");
                var upResp = await host.Http.PostAsync("/api/community/mods/upload", form);
                Assert.Equal(HttpStatusCode.OK, upResp.StatusCode);
                Assert.True(upResp.Headers.TryGetValues("X-Uploader-Secret", out var headerSecrets));
                string headerSecret = headerSecrets.First();
                var upJson = await upResp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(headerSecret, upJson.GetProperty("uploaderSecret").GetString());
                Assert.True(upJson.GetProperty("mod").GetProperty("mechanicCount").GetInt32() >= 1);
            }

            // 社区列表与详情均返回 mechanicCount 及 mechanics 摘要
            var commList = await host.Http.GetFromJsonAsync<JsonElement>("/api/community/mods");
            var commItem = commList.GetProperty("mods").EnumerateArray()
                .First(m => m.GetProperty("modId").GetString() == "w3a-comm-mech");
            Assert.True(commItem.GetProperty("mechanicCount").GetInt32() >= 1);
            Assert.True(commItem.GetProperty("customMechanicCount").GetInt32() >= 1);

            var commDetail = await host.Http.GetFromJsonAsync<JsonElement>("/api/community/mods/w3a-comm-mech");
            Assert.True(commDetail.GetProperty("mod").GetProperty("mechanicCount").GetInt32() >= 1);
            Assert.True(commDetail.GetProperty("customMechanicCount").GetInt32() >= 1);
            Assert.True(commDetail.GetProperty("mechanics").GetArrayLength() >= 1);

            // 下载端点返回合法 Content-Disposition，且反射校验 SanitizeDownloadFileName 清洗危险字符
            var dlResp = await host.Http.GetAsync("/api/community/mods/w3a-comm-mech/download");
            Assert.Equal(HttpStatusCode.OK, dlResp.StatusCode);
            string cdHeader = dlResp.Content.Headers.ContentDisposition?.FileName ?? string.Empty;
            Assert.Contains("w3a-comm-mech", cdHeader);

            MethodInfo? sanitizeMethod = typeof(CommunityModEndpoints).GetMethod(
                "SanitizeDownloadFileName",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(sanitizeMethod);
            string cleaned = (string)sanitizeMethod!.Invoke(null, new object?[] { "evil\r\n\"../x\\y.zip", "safe-mod" })!;
            Assert.DoesNotContain("\r", cleaned);
            Assert.DoesNotContain("\n", cleaned);
            Assert.DoesNotContain("\"", cleaned);
            Assert.DoesNotContain("/", cleaned);
            Assert.DoesNotContain("\\", cleaned);
            Assert.EndsWith(".bazaarmod.zip", cleaned, StringComparison.OrdinalIgnoreCase);

            // P0-37 校验闭环：社区上传带病包同时返回 validation 与 report 字段
            using var badZipMs = new MemoryStream();
            using (var za = new ZipArchive(badZipMs, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
            {
                var e = za.CreateEntry("bad-comm/mod.json");
                using (var sw = new StreamWriter(e.Open(), Encoding.UTF8))
                    sw.Write("{\"id\":\"bad-comm\",\"name\":\"Bad\",\"version\":\"1.0.0\"}");
                var c = za.CreateEntry("bad-comm/cards/custom_cards.json");
                using (var sw2 = new StreamWriter(c.Open(), Encoding.UTF8))
                    sw2.Write("{\"cards\":[{\"customId\":\"wrong-ns:x\",\"titleZh\":\"坏卡\",\"type\":\"Item\",\"size\":\"Small\",\"startingTier\":\"Bronze\",\"baseVisualTemplateId\":\"00000000-0000-0000-0000-000000000000\"}]}");
            }
            using (var badForm = new MultipartFormDataContent())
            {
                var fc = new ByteArrayContent(badZipMs.ToArray());
                fc.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                badForm.Add(fc, "file", "bad-comm.bazaarmod.zip");
                var badUp = await host.Http.PostAsync("/api/community/mods/upload", badForm);
                Assert.Equal(HttpStatusCode.UnprocessableEntity, badUp.StatusCode);
                var badJson = await badUp.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(badJson.TryGetProperty("validation", out _));
                Assert.True(badJson.TryGetProperty("report", out _));
            }
        }

        // 2. BAZAAR_WORKSHOP_MODE=readonly 下，Workshop 写入与 CommunityModEndpoints 管理端点均返回 403
        await using (var roHost = await StartHostAsync(
            envProvider: k => k == "BAZAAR_WORKSHOP_MODE" ? "readonly" : null,
            copyClockworkSample: true))
        {
            var roMeta = await roHost.Http.GetFromJsonAsync<JsonElement>("/api/workshop/meta");
            Assert.False(roMeta.GetProperty("canWrite").GetBoolean());

            var ws = new WorkshopModWorkspace
            {
                Manifest = new ModManifest { Id = "ro-blocked", Name = "只读测试", Version = "1.0.0", Scope = "multiplayer-safe" }
            };
            var wsWrite = await roHost.Http.PostAsJsonAsync("/api/workshop/mods", ws);
            Assert.Equal(HttpStatusCode.Forbidden, wsWrite.StatusCode);

            var commAdmin = await roHost.Http.PostAsJsonAsync("/api/community/mods/clockwork-forge/server-default", new { enabled = true });
            Assert.Equal(HttpStatusCode.Forbidden, commAdmin.StatusCode);
        }
    }
}

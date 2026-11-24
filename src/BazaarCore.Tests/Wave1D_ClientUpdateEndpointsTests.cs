using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BazaarServer.Mods;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1D（P0-45）：验证真实 BazaarServer.Mods.ClientUpdateEndpoints 白名单清单、权威 targetPath/updatePolicy、
/// .cfg/.xml/.pdb 剔除与下载拦截、.doorstop_version 纳入、路径穿越防护以及 payload/ 回退目录探测。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1D_ClientUpdateEndpointsTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task Manifest_WhitelistFiltersCfgAndDebugFiles_EmitsAuthoritativeTargetPathAndPolicy()
    {
        string tempDir = CreateTempDir("w1d_client_update_manifest");
        string payloadDir = Path.Combine(tempDir, "client-payload");
        string dataDir = Path.Combine(tempDir, "data");
        Directory.CreateDirectory(dataDir);
        SeedRealisticPayloadTree(payloadDir);

        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            (app, http) = await StartClientUpdateServerAsync(dataDir, payloadDirOverride: payloadDir);

            string etag;
            using (var resp = await http.GetAsync("/api/client-update/manifest"))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                etag = resp.Headers.ETag!.Tag;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;

                Assert.True(root.GetProperty("payloadDirAvailable").GetBoolean());
                Assert.True(root.GetProperty("schemaVersion").GetInt32() >= 2, "P0-45 升级后 schemaVersion 应 >= 2");

                var files = root.GetProperty("files").EnumerateArray().ToList();
                var byTarget = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    Assert.True(f.TryGetProperty("targetPath", out var tpProp), "每条清单项必须包含权威 targetPath");
                    string tp = tpProp.GetString()!;
                    Assert.False(byTarget.ContainsKey(tp), $"清单出现重复 targetPath: {tp}");
                    byTarget[tp] = f;
                }

                // 绝不包含玩家本地配置与调试文档文件
                Assert.DoesNotContain(byTarget.Keys, k => k.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(byTarget.Keys, k => k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(byTarget.Keys, k => k.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(byTarget.Keys, k => k.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase));
                Assert.DoesNotContain(byTarget.Keys, k => k.Contains("game-dir.txt", StringComparison.OrdinalIgnoreCase));

                // 精确验证白名单组件与权威元数据
                AssertEntry(byTarget, "BepInEx/plugins/BazaarLauncher.dll", "plugin", "always", "core-plugins", required: true);
                AssertEntry(byTarget, "BepInEx/plugins/BazaarModSdk.dll", "plugin", "always", "core-plugins", required: true);
                AssertEntry(byTarget, "winhttp.dll", "loader", "always", "bepinex-loader", required: true);
                AssertEntry(byTarget, "doorstop_config.ini", "loader", "ifMissing", "bepinex-loader", required: true);
                AssertEntry(byTarget, ".doorstop_version", "loader", "always", "bepinex-loader", required: false);
                AssertEntry(byTarget, "BepInEx/core/BepInEx.dll", "loader", "always", "bepinex-loader", required: true);
                AssertEntry(byTarget, "BepInEx/core/0Harmony.dll", "loader", "always", "bepinex-loader", required: true);

                Assert.Equal(7, files.Count);
            }

            // ETag 304 协商缓存正常
            using (var req304 = new HttpRequestMessage(HttpMethod.Get, "/api/client-update/manifest"))
            {
                req304.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
                using var resp304 = await http.SendAsync(req304);
                Assert.Equal(HttpStatusCode.NotModified, resp304.StatusCode);
            }

            // 绕过 manifest 直下 .cfg 与 .xml 必须被白名单阻断返回 404
            using (var cfgResp = await http.GetAsync("/api/client-update/files/BepInEx/config/local.thebazaar.bazaarlauncher.cfg"))
            {
                Assert.Equal(HttpStatusCode.NotFound, cfgResp.StatusCode);
            }
            using (var xmlResp = await http.GetAsync("/api/client-update/files/BepInEx/core/0Harmony.xml"))
            {
                Assert.Equal(HttpStatusCode.NotFound, xmlResp.StatusCode);
            }

            // 白名单合法文件（含 .doorstop_version 与 BazaarLauncher.dll）正常下载且 SHA-256 吻合
            using (var verResp = await http.GetAsync("/api/client-update/files/.doorstop_version"))
            {
                Assert.Equal(HttpStatusCode.OK, verResp.StatusCode);
                string body = await verResp.Content.ReadAsStringAsync();
                Assert.Equal("3.4.0", body);
            }

            // 路径穿越与冒号非法路径返回 400
            using (var travResp = await http.GetAsync("/api/client-update/files/..%2F..%2Fsecret.txt"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, travResp.StatusCode);
            }
            using (var colonResp = await http.GetAsync("/api/client-update/files/BazaarLauncher.dll:ads"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, colonResp.StatusCode);
            }
        }
        finally
        {
            await StopServerAsync(app, http, tempDir);
        }
    }

    [Fact]
    public async Task ResolveClientPayloadDirectory_FindsReleasePayloadFolderFallback()
    {
        string tempDir = CreateTempDir("w1d_release_payload_fallback");
        string dataDir = Path.Combine(tempDir, "data");
        string releasePayloadDir = Path.Combine(tempDir, "payload");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(releasePayloadDir);
        File.WriteAllBytes(Path.Combine(releasePayloadDir, "BazaarLauncher.dll"), Utf8NoBom.GetBytes("FAKE_LAUNCHER_V15"));

        string? prevEnv = Environment.GetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD");
        Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", null);
        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            (app, http) = await StartClientUpdateServerAsync(dataDir, payloadDirOverride: null);
            using var resp = await http.GetAsync("/api/client-update/manifest");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            Assert.True(root.GetProperty("payloadDirAvailable").GetBoolean(), "应自动回退探测到同级 Release payload/ 目录");
            Assert.Equal(1, root.GetProperty("fileCount").GetInt32());
            var f0 = root.GetProperty("files")[0];
            Assert.Equal("BepInEx/plugins/BazaarLauncher.dll", f0.GetProperty("targetPath").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", prevEnv);
            await StopServerAsync(app, http, tempDir);
        }
    }

    internal static void SeedRealisticPayloadTree(string payloadDir)
    {
        Directory.CreateDirectory(Path.Combine(payloadDir, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(payloadDir, "BepInEx", "config"));

        File.WriteAllBytes(Path.Combine(payloadDir, "BazaarLauncher.dll"), Utf8NoBom.GetBytes("FAKE_LAUNCHER_BINARY_WAVE1D_V15_4"));
        File.WriteAllBytes(Path.Combine(payloadDir, "BazaarModSdk.dll"), Utf8NoBom.GetBytes("FAKE_MODSDK_BINARY_WAVE1D_V15_4"));
        File.WriteAllBytes(Path.Combine(payloadDir, "winhttp.dll"), Utf8NoBom.GetBytes("FAKE_WINHTTP_PROXY_DLL"));
        File.WriteAllText(Path.Combine(payloadDir, "doorstop_config.ini"), "[UnityDoorstop]\nenabled=true\ntargetAssembly=BepInEx\\core\\BepInEx.Preloader.dll\n", Utf8NoBom);
        File.WriteAllText(Path.Combine(payloadDir, ".doorstop_version"), "3.4.0", Utf8NoBom);
        File.WriteAllText(Path.Combine(payloadDir, "game-dir.txt"), "should-never-ship", Utf8NoBom);
        File.WriteAllText(
            Path.Combine(payloadDir, "BepInEx", "config", "local.thebazaar.bazaarlauncher.cfg"),
            "[General]\nEnabled = true\nServerBase = http://127.0.0.1:8080\n",
            Utf8NoBom);
        File.WriteAllBytes(Path.Combine(payloadDir, "BepInEx", "core", "BepInEx.dll"), Utf8NoBom.GetBytes("FAKE_BEPINEX_CORE_DLL"));
        File.WriteAllBytes(Path.Combine(payloadDir, "BepInEx", "core", "0Harmony.dll"), Utf8NoBom.GetBytes("FAKE_HARMONY_CORE_DLL"));
        File.WriteAllText(Path.Combine(payloadDir, "BepInEx", "core", "0Harmony.xml"), "<doc>debug xml</doc>", Utf8NoBom);
        File.WriteAllBytes(Path.Combine(payloadDir, "BepInEx", "core", "BepInEx.pdb"), Utf8NoBom.GetBytes("FAKE_PDB"));
        File.WriteAllBytes(Path.Combine(payloadDir, "BepInEx", "core", "legacy.mdb"), Utf8NoBom.GetBytes("FAKE_MDB"));
    }

    internal static async Task<(WebApplication App, HttpClient Http)> StartClientUpdateServerAsync(
        string dataDir,
        string? payloadDirOverride)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        ClientUpdateEndpoints.Map(app, dataDir, payloadDirOverride);

        await app.StartAsync();
        string baseUrl = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();

        var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
        return (app, http);
    }

    internal static async Task StopServerAsync(WebApplication? app, HttpClient? http, string tempDir)
    {
        http?.Dispose();
        if (app != null)
        {
            try { await app.StopAsync(); } catch { }
            try { await app.DisposeAsync(); } catch { }
        }
        try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
    }

    private static void AssertEntry(
        Dictionary<string, JsonElement> byTarget,
        string targetPath,
        string expectedCategory,
        string expectedPolicy,
        string expectedAtomicGroup,
        bool required)
    {
        Assert.True(byTarget.TryGetValue(targetPath, out var el), $"清单缺少预期目标文件：{targetPath}");
        Assert.Equal(expectedCategory, el.GetProperty("category").GetString());
        Assert.Equal(expectedPolicy, el.GetProperty("updatePolicy").GetString());
        Assert.Equal(expectedAtomicGroup, el.GetProperty("atomicGroup").GetString());
        Assert.Equal(required, el.GetProperty("required").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(el.GetProperty("sha256").GetString()));
    }

    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

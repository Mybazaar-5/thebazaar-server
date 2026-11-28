using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using BazaarCore.Tests.TestInfrastructure;
using BazaarServer.Mods;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2C 模块四独占测试（ClientUpdateEndpoints 动态版本/SemVer 契约 + BazaarHub 12 项 --self-test + CAS 路径对齐）。
/// 触碰进程环境变量与 Kestrel 内存宿主，严格遵循 Wave 0 集合约定标注 [Collection("GlobalStaticState")]。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave2C_HubAndEndpointsTests : IClassFixture<BazaarHubExecutableFixture>
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly BazaarHubExecutableFixture _fx;

    public Wave2C_HubAndEndpointsTests(BazaarHubExecutableFixture fx) => _fx = fx;

    [Fact]
    public async Task D7_P1_01_05_ClientUpdateEndpoints_EmitsUnifiedVersionMinRequiredAndRealFileVersions()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"w2c_cu_{Guid.NewGuid():N}");
        string payloadDir = Path.Combine(tempDir, "payload");
        string dataDir = Path.Combine(tempDir, "data");
        Directory.CreateDirectory(payloadDir);
        Directory.CreateDirectory(dataDir);

        // 拷贝真实编译出的 BazaarCore.dll 作为含 PE FileVersion 的样本 DLL，验证 fileVersion 非空且为真实 PE 版本
        string realDllSource = typeof(ClientUpdateEndpoints).Assembly.Location;
        File.Copy(realDllSource, Path.Combine(payloadDir, "BazaarLauncher.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(payloadDir, "winhttp.dll"), "stub-loader", Utf8NoBom);

        string? prevPayload = Environment.GetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD");
        string? prevMinClient = Environment.GetEnvironmentVariable("BAZAAR_MIN_CLIENT_VERSION");
        string? prevMinHub = Environment.GetEnvironmentVariable("BAZAAR_MIN_HUB_VERSION");

        WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", payloadDir);
            Environment.SetEnvironmentVariable("BAZAAR_MIN_CLIENT_VERSION", "15.0.0");
            Environment.SetEnvironmentVariable("BAZAAR_MIN_HUB_VERSION", "15.6.0");

            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            app = builder.Build();
            ClientUpdateEndpoints.Map(app, dataDir);
            await app.StartAsync();

            string addr = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            http = new HttpClient { BaseAddress = new Uri(addr) };

            using var resp = await http.GetAsync("/api/client-update/manifest");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;

            string serverVer = root.GetProperty("serverVersion").GetString()!;
            Assert.Equal("15.6.0", serverVer);
            Assert.DoesNotContain("+", serverVer);
            Assert.Equal("15.6.0", root.GetProperty("hubVersion").GetString());
            Assert.Equal("15.0.0", root.GetProperty("minRequiredVersion").GetString());
            Assert.Equal("15.6.0", root.GetProperty("minHubVersion").GetString());

            var files = root.GetProperty("files").EnumerateArray().ToList();
            var launcherEntry = files.First(f =>
                string.Equals(f.GetProperty("targetPath").GetString(), "BepInEx/plugins/BazaarLauncher.dll", StringComparison.OrdinalIgnoreCase));
            string fileVer = launcherEntry.GetProperty("fileVersion").GetString()!;
            Assert.StartsWith("15.6.0", fileVer, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("BAZAAR_CLIENT_PAYLOAD", prevPayload);
            Environment.SetEnvironmentVariable("BAZAAR_MIN_CLIENT_VERSION", prevMinClient);
            Environment.SetEnvironmentVariable("BAZAAR_MIN_HUB_VERSION", prevMinHub);
            http?.Dispose();
            if (app != null)
            {
                try { await app.StopAsync(); } catch { }
                try { await app.DisposeAsync(); } catch { }
            }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void D7_P1_02_03_05_07_BazaarHub_SelfTestRunner_AllTwelveCasesPass()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (exitCode, stdout, stderr) = _fx.RunHubCli("--self-test", timeoutMs: 60000);
        Assert.True(exitCode == 0, $"BazaarHub --self-test 失败 (ExitCode={exitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        int passCount = stdout.Split('\n').Count(line => line.TrimStart().StartsWith("[PASS]", StringComparison.Ordinal));
        Assert.True(passCount >= 12, $"预期至少通过 12 项自检用例（含 D7-P1-02/03/05/07），实际 {passCount} 项：\n{stdout}");
        Assert.Contains("10.", stdout, StringComparison.Ordinal);
        Assert.Contains("11.", stdout, StringComparison.Ordinal);
        Assert.Contains("12.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void D7_P1_05_SemVerComparer_HandlesFourPartPreReleaseAndMetadata()
    {
        if (!OperatingSystem.IsWindows()) return;

        string hubDll = Path.ChangeExtension(_fx.HubExePath, ".dll");
        Assert.True(File.Exists(hubDll), $"未找到 BazaarHub.dll：{hubDll}");

        var alc = new AssemblyLoadContext($"Wave2C_Hub_{Guid.NewGuid():N}", isCollectible: true);
        try
        {
            using var ms = new MemoryStream(File.ReadAllBytes(hubDll));
            Assembly hubAsm = alc.LoadFromStream(ms);
            Type cmpType = hubAsm.GetType("BazaarHub.Services.SemVerComparer", throwOnError: true)!;
            MethodInfo compare = cmpType.GetMethod("Compare", BindingFlags.Public | BindingFlags.Static)!;

            Assert.Equal(0, (int)compare.Invoke(null, new object?[] { "15.4.0", "15.4.0.0" })!); // FileVersion 四段等价
            Assert.Equal(0, (int)compare.Invoke(null, new object?[] { "v15.4.0+abc123", "15.4.0" })!);
            Assert.True((int)compare.Invoke(null, new object?[] { "15.4.0-beta.1", "15.4.0" })! < 0);
            Assert.True((int)compare.Invoke(null, new object?[] { "15.5.0", "15.4.9" })! > 0);
            Assert.True((int)compare.Invoke(null, new object?[] { "15.4.1", "15.4.0" })! > 0);
        }
        finally
        {
            try { alc.Unload(); } catch { }
        }
    }
}

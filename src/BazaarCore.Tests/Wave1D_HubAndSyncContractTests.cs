using System.Diagnostics;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// 隔离防锁 Fixture：优先复用当前 worktree 已编译的最新 BazaarHub.exe；
/// 若缺失或源码更新于产物，则在独立临时目录以 -p:BaseIntermediateOutputPath 隔离编译，绝不锁死工作树 obj/。
/// </summary>
public sealed class BazaarHubExecutableFixture : IDisposable
{
    private readonly string? _tempBuildRoot;

    public string HubExePath { get; }

    public BazaarHubExecutableFixture()
    {
        if (!OperatingSystem.IsWindows())
        {
            HubExePath = string.Empty;
            return;
        }

        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string hubDir = Path.Combine(repoRoot, "src", "BazaarHub");
        string csprojPath = Path.Combine(hubDir, "BazaarHub.csproj");
        Assert.True(File.Exists(csprojPath), $"未找到 BazaarHub.csproj：{csprojPath}");

        string? reusableExe = TryFindFreshBuiltExe(hubDir);
        if (reusableExe != null)
        {
            HubExePath = reusableExe;
            return;
        }

        _tempBuildRoot = Path.Combine(Path.GetTempPath(), $"w1d_hub_build_{Guid.NewGuid():N}");
        string tempObj = Path.Combine(_tempBuildRoot, "obj");
        string tempOut = Path.Combine(_tempBuildRoot, "out");
        Directory.CreateDirectory(tempObj);
        Directory.CreateDirectory(tempOut);

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(csprojPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add($"-p:BaseIntermediateOutputPath={tempObj.Replace('\\', '/')}/");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(tempOut);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dotnet build BazaarHub");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"隔离构建 BazaarHub 失败（ExitCode={proc.ExitCode}）：\n{stdout}\n{stderr}");
        }

        HubExePath = Path.Combine(tempOut, "BazaarHub.exe");
        Assert.True(File.Exists(HubExePath), $"未找到隔离编译产物：{HubExePath}");
    }

    private static string? TryFindFreshBuiltExe(string hubDir)
    {
        DateTime latestSourceUtc = Directory
            .EnumerateFiles(hubDir, "*", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        string[] candidates =
        {
            Path.Combine(hubDir, "bin", "Release", "net8.0-windows", "BazaarHub.exe"),
            Path.Combine(hubDir, "bin", "Debug", "net8.0-windows", "BazaarHub.exe"),
        };

        string? best = null;
        DateTime bestTime = DateTime.MinValue;
        foreach (string c in candidates)
        {
            if (!File.Exists(c)) continue;
            DateTime t = File.GetLastWriteTimeUtc(c);
            if (t >= latestSourceUtc && t > bestTime)
            {
                best = c;
                bestTime = t;
            }
        }
        return best;
    }

    public (int ExitCode, string StdOut, string StdErr) RunHubCli(string arguments, int timeoutMs = 45000)
    {
        var psi = new ProcessStartInfo(HubExePath, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 BazaarHub.exe");
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"BazaarHub CLI 超时（{timeoutMs}ms）：{arguments}");
        }
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    public void Dispose()
    {
        if (!string.IsNullOrWhiteSpace(_tempBuildRoot))
        {
            try { if (Directory.Exists(_tempBuildRoot)) Directory.Delete(_tempBuildRoot, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Wave 1D（P0-34 / P0-45 / P0-46 / P0-47）：跨进程真实驱动 BazaarHub.exe 无头自检（9 项）
/// 与真实 Kestrel ClientUpdateEndpoints 端到端落盘契约联调测试。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1D_HubAndSyncContractTests : IClassFixture<BazaarHubExecutableFixture>
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly BazaarHubExecutableFixture _fx;

    public Wave1D_HubAndSyncContractTests(BazaarHubExecutableFixture fx) => _fx = fx;

    [Fact]
    public void BazaarHub_SelfTestRunner_AllNineCasesPass()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (exitCode, stdout, stderr) = _fx.RunHubCli("--self-test");
        Assert.True(exitCode == 0, $"BazaarHub --self-test 失败 (ExitCode={exitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

        int passCount = stdout.Split('\n').Count(line => line.TrimStart().StartsWith("[PASS]", StringComparison.Ordinal));
        Assert.True(passCount >= 9, $"预期至少通过 9 项真实回归用例，实际仅 {passCount} 项：\n{stdout}");
        Assert.Contains("9.", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BazaarHub_UpdateSyncService_AgainstRealClientUpdateEndpoints_PreservesCfgAndRoutesLoaders()
    {
        if (!OperatingSystem.IsWindows()) return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"w1d_e2e_sync_{Guid.NewGuid():N}");
        string payloadDir = Path.Combine(tempDir, "client-payload");
        string dataDir = Path.Combine(tempDir, "data");
        string gameDir = Path.Combine(tempDir, "game");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));
        Wave1D_ClientUpdateEndpointsTests.SeedRealisticPayloadTree(payloadDir);

        // 预先写入玩家自定义配置，断言同步后逐字节不可被洗白
        string userCfgPath = Path.Combine(gameDir, "BepInEx", "config", "local.thebazaar.bazaarlauncher.cfg");
        const string userCustomCfg =
            "## Player Custom Config\n[General]\nEnabled = true\nServerBase = http://10.20.30.40:9090\n\n[Performance]\nMaxForegroundFps = 144\n";
        File.WriteAllText(userCfgPath, userCustomCfg, Utf8NoBom);
        byte[] expectedCfgBytes = File.ReadAllBytes(userCfgPath);

        Microsoft.AspNetCore.Builder.WebApplication? app = null;
        HttpClient? http = null;
        try
        {
            (app, http) = await Wave1D_ClientUpdateEndpointsTests.StartClientUpdateServerAsync(dataDir, payloadDirOverride: payloadDir);
            string serverUrl = http.BaseAddress!.ToString().TrimEnd('/');

            var (exitCode, stdout, stderr) = _fx.RunHubCli($"--verify-client-update-sync \"{serverUrl}\" \"{gameDir}\"");
            Assert.True(exitCode == 0, $"跨进程真实联调失败 (ExitCode={exitCode}):\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");

            // 1. 玩家本地 .cfg 逐字节完整保留
            Assert.Equal(expectedCfgBytes, File.ReadAllBytes(userCfgPath));

            // 2. 根目录引导文件精准落盘游戏根目录，绝不污染 BepInEx\plugins\
            Assert.True(File.Exists(Path.Combine(gameDir, "winhttp.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "doorstop_config.ini")));
            Assert.True(File.Exists(Path.Combine(gameDir, ".doorstop_version")));
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "winhttp.dll")));
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "doorstop_config.ini")));

            // 3. 双核心插件精准落盘 BepInEx\plugins\，核心库落盘 BepInEx\core\
            Assert.True(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarLauncher.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarModSdk.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll")));
            Assert.True(File.Exists(Path.Combine(gameDir, "BepInEx", "core", "0Harmony.dll")));

            // 4. 调试文档文件未落盘，无残留 .tmp 或暂存目录
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "core", "0Harmony.xml")));
            Assert.False(File.Exists(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.pdb")));
            Assert.Empty(Directory.EnumerateFiles(gameDir, "*.tmp*", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(gameDir, "_BazaarHubStaging")));
        }
        finally
        {
            await Wave1D_ClientUpdateEndpointsTests.StopServerAsync(app, http, tempDir);
        }
    }
}

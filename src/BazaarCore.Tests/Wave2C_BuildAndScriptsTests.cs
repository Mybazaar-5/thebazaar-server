using System.Diagnostics;
using System.Reflection;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 2C 工程与脚本独占测试（D7-P1-01 Directory.Build.props、D7-P1-06 start-standalone.ps1、
/// D7-P1-08 start-local-server.ps1 / deploy-linux.sh / sync-github-public.ps1 增量防误删与零泄露）。
/// </summary>
public sealed class Wave2C_BuildAndScriptsTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public void D7_P1_01_DirectoryBuildProps_ExistsAndUnifiesCoreAssemblyInformationalVersions()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string propsPath = Path.Combine(repoRoot, "Directory.Build.props");
        Assert.True(File.Exists(propsPath), $"缺少根级 Directory.Build.props：{propsPath}");

        string propsXml = File.ReadAllText(propsPath, Encoding.UTF8);
        Assert.Contains("<Version>15.6.0</Version>", propsXml, StringComparison.Ordinal);
        Assert.Contains("<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>", propsXml, StringComparison.Ordinal);

        // 校验当前已编译加载的 BazaarCore 与 BazaarServer 程序集 InformationalVersion 均为精确 15.6.0（无 +gitsha）
        string? coreVer = typeof(BazaarCore.Models.CustomRunRuleSet).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        string? srvVer = typeof(BazaarServer.Mods.ClientUpdateEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Assert.Equal("15.6.0", coreVer);
        Assert.Equal("15.6.0", srvVer);

        // BazaarHub.csproj 与 BazaarModSdk.csproj 不应保留重复 <Version>
        Assert.DoesNotContain("<Version>", File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarHub", "BazaarHub.csproj")), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<Version>", File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarModSdk", "BazaarModSdk.csproj")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void D7_P1_06_StartStandaloneScript_DeploysModSdkAndPreservesExistingCfg()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "tools", "start-standalone.ps1");
        Assert.True(File.Exists(scriptPath));

        string content = File.ReadAllText(scriptPath, Encoding.UTF8);
        Assert.Contains("BazaarModSdk.dll", content, StringComparison.Ordinal);
        Assert.Contains("BAZAAR_BIND", content, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", content, StringComparison.Ordinal);

        if (!OperatingSystem.IsWindows()) return;

        // 在隔离沙盒中真实调用 Ensure-ClientPluginInstalled 函数，验证同时部署 Launcher+ModSdk 且跳过已有 .cfg
        string sandbox = Path.Combine(Path.GetTempPath(), $"w2c_standalone_{Guid.NewGuid():N}");
        try
        {
            string fakeRepo = Path.Combine(sandbox, "repo");
            string payload = Path.Combine(fakeRepo, "dist", "client", "payload");
            string gameDir = Path.Combine(sandbox, "game");
            Directory.CreateDirectory(Path.Combine(payload, "BepInEx", "core"));
            Directory.CreateDirectory(Path.Combine(payload, "BepInEx", "config"));
            Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));
            Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));

            File.WriteAllText(Path.Combine(payload, "winhttp.dll"), "NEW_WINHTTP", Utf8NoBom);
            File.WriteAllText(Path.Combine(payload, "doorstop_config.ini"), "NEW_DOORSTOP", Utf8NoBom);
            File.WriteAllText(Path.Combine(payload, "BazaarLauncher.dll"), "NEW_LAUNCHER", Utf8NoBom);
            File.WriteAllText(Path.Combine(payload, "BazaarModSdk.dll"), "NEW_MODSDK", Utf8NoBom);
            File.WriteAllText(Path.Combine(payload, "BepInEx", "core", "BepInEx.dll"), "NEW_CORE", Utf8NoBom);
            File.WriteAllText(Path.Combine(payload, "BepInEx", "config", "local.thebazaar.bazaarlauncher.cfg"), "OVERWRITE_EVIL", Utf8NoBom);

            // 模拟玩家已有配置与已装 BazaarLauncher.dll 但缺失 BazaarModSdk.dll
            string existingCfg = Path.Combine(gameDir, "BepInEx", "config", "local.thebazaar.bazaarlauncher.cfg");
            File.WriteAllText(existingCfg, "PLAYER_CUSTOM_CFG_DO_NOT_TOUCH", Utf8NoBom);
            File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "OLD_WINHTTP", Utf8NoBom);
            File.WriteAllText(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarLauncher.dll"), "OLD_LAUNCHER", Utf8NoBom);

            string psCode = $@"
$ast = [System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''")}', [ref]$null, [ref]$null)
$fn = $ast.Find({{ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Ensure-ClientPluginInstalled' }}, $true)
if ($null -eq $fn) {{ throw 'Ensure-ClientPluginInstalled not found' }}
. ([scriptblock]::Create($fn.Extent.Text))
Ensure-ClientPluginInstalled -TargetGameDir '{gameDir.Replace("'", "''")}' -RepoRoot '{fakeRepo.Replace("'", "''")}'
";
            var (exitCode, stdout, stderr) = RunPowerShell(psCode);
            Assert.True(exitCode == 0, $"Ensure-ClientPluginInstalled 执行失败：{stdout}\n{stderr}");

            Assert.True(File.Exists(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarModSdk.dll")), "应自动补齐部署 BazaarModSdk.dll");
            Assert.Equal("NEW_MODSDK", File.ReadAllText(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarModSdk.dll")));
            Assert.Equal("PLAYER_CUSTOM_CFG_DO_NOT_TOUCH", File.ReadAllText(existingCfg));
        }
        finally
        {
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void D7_P1_01_08_LocalServerAndDeployLinuxAndPublicSyncScripts_MeetPortabilityAndSafetyGates()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string forbiddenWorkspace = string.Concat("e:", "\\", "the", "bazaar");

        // 1. start-local-server.ps1 零本机工作区硬编码
        string localSrvPath = Path.Combine(repoRoot, "tools", "start-local-server.ps1");
        string localSrv = File.ReadAllText(localSrvPath, Encoding.UTF8);
        Assert.DoesNotContain(forbiddenWorkspace, localSrv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$PSScriptRoot", localSrv, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BAZAAR_BIND", localSrv, StringComparison.Ordinal);

        // 2. tools/deploy-linux.sh 权威源本存在且支持 payload / BAZAAR_CLIENT_PAYLOAD / BAZAAR_BIND / 公网安全提示
        string deployShPath = Path.Combine(repoRoot, "tools", "deploy-linux.sh");
        Assert.True(File.Exists(deployShPath), $"缺少主仓权威 deploy-linux.sh：{deployShPath}");
        string deploySh = File.ReadAllText(deployShPath, Encoding.UTF8);
        Assert.Contains("BAZAAR_CLIENT_PAYLOAD", deploySh, StringComparison.Ordinal);
        Assert.Contains("BAZAAR_BIND", deploySh, StringComparison.Ordinal);
        Assert.Contains("BAZAAR_WORKSHOP_TOKEN", deploySh, StringComparison.Ordinal);
        Assert.Contains("payload", deploySh, StringComparison.Ordinal);

        // 3. sync-github-public.ps1 增量受控前缀同步（保留 LICENSE/.github 元文件）且同步 Directory.Build.props & deploy-linux.sh
        string syncPath = Path.Combine(repoRoot, "tools", "sync-github-public.ps1");
        string syncText = File.ReadAllText(syncPath, Encoding.UTF8);
        Assert.Contains("Directory.Build.props", syncText, StringComparison.Ordinal);
        Assert.Contains("deploy-linux.sh", syncText, StringComparison.Ordinal);
        Assert.Contains("managedSyncPrefixes", syncText, StringComparison.OrdinalIgnoreCase);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPowerShell(string script)
    {
        string exe = File.Exists(@"C:\Program Files\PowerShell\7\pwsh.exe") ? @"C:\Program Files\PowerShell\7\pwsh.exe" : "powershell.exe";
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 PowerShell");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(20000);
        return (proc.ExitCode, stdout, stderr);
    }
}

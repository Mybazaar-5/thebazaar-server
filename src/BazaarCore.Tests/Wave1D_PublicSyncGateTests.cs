using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1D（P0-48）：真实调用 tools/sync-github-public.ps1 -ScanOnlyTarget 验证：
/// 1. 本测试源码自身零静态敏感明文（闭环 #22-3）；
/// 2. .bat 独立行 ::PS_BEGIN:: Base64 解码与防第 5 行字面量误杀（闭环 #22-2）；
/// 3. >5MB 文件 2MB 滑窗跨块边界流式扫描（裸文件与 ZIP 内）；
/// 4. 泛化私钥/Token/公网 IP/盘符规则拦截与 RFC1918/回环/RFC5737/版本号零误报；
/// 5. 版权产物门禁（含反斜杠路径归一化，闭环 #23-3）。
/// </summary>
public sealed class Wave1D_PublicSyncGateTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 运行期动态构造攻击样本，绝不在源代码中静态出现任何触发零泄露门禁的明文模式
    private static string BuildSamplePrivateIp() => string.Join('.', "62", "234", "34", "130");
    private static string BuildSamplePublicIp() => string.Join('.', "8", "8", "4", "4");
    private static string BuildSamplePrivateKeyHeader() => string.Concat("-----", "BEGIN OPENSSH ", "PRIVATE KEY", "-----");
    private static string BuildSampleGhToken() => string.Concat("ghp", "_", new string('A', 24));
    private static string BuildSampleWorkspacePath() => string.Concat("E:", "\\", "the", "bazaar", "\\", "secret.txt");
    private static string BuildSampleUserPemPath() => string.Concat("C:", "\\", "Users", "\\", "alice", "\\", "prod", ".", "pem");
    private static string BuildSampleCompassKeyword() => "\u5df4\u624e" + "\u7f57\u76d8";

    [Fact]
    public void SelfAudit_Wave1DPublicSyncGateTestsSourceFile_PassesZeroLeakScan()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string selfPath = Path.Combine(repoRoot, "src", "BazaarCore.Tests", "Wave1D_PublicSyncGateTests.cs");
        Assert.True(File.Exists(selfPath), $"未找到本测试文件：{selfPath}");

        var (exitCode, stdout, stderr) = RunPublicSyncScan(selfPath, checkCopyright: false);
        Assert.True(exitCode == 0, $"测试源码自身必须通过零泄露自检 (ExitCode={exitCode})：\n{stdout}\n{stderr}");
    }

    [Fact]
    public void ZeroLeakScan_BatBase64_MatchesStandaloneMarkerLine_BlocksHiddenLeak_AllowsCleanBat()
    {
        string sandbox = CreateSandbox("w1d_gate_bat");
        try
        {
            string batHeader =
                "@echo off\r\n" +
                "setlocal\r\n" +
                "set \"SELF_BAT=%~f0\"\r\n" +
                "set \"BAT_DIR=%~dp0\"\r\n" +
                "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"$raw=[System.IO.File]::ReadAllLines($env:SELF_BAT,[System.Text.Encoding]::ASCII); $idx=[Array]::IndexOf($raw,'::PS_BEGIN::'); $b64=($raw[($idx+1)..($raw.Length-1)] -join ''); $code=[System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($b64)); & ([scriptblock]::Create($code)) @args\" -- %*\r\n" +
                "exit /b %ERRORLEVEL%\r\n" +
                "::PS_BEGIN::\r\n";

            // 1. 真实格式洁净 .bat（含第 5 行字面量 + 第 7 行独立标记 + 洁净 Base64）必须退出码 0 放行
            string cleanPs = "Write-Host 'Starting local server on http://127.0.0.1:8080'\n";
            string cleanBatPath = Path.Combine(sandbox, "clean_launcher.bat");
            File.WriteAllText(cleanBatPath, batHeader + Convert.ToBase64String(Utf8NoBom.GetBytes(cleanPs)) + "\r\n", Encoding.ASCII);

            var cleanRes = RunPublicSyncScan(cleanBatPath, checkCopyright: false);
            Assert.True(cleanRes.ExitCode == 0, $"洁净真实格式 .bat 不应误杀：\n{cleanRes.StdOut}\n{cleanRes.StdErr}");

            // 2. 同格式但 Base64 内藏私人 IP 必须非 0 拦截
            string leakedPs = $"$url = 'http://{BuildSamplePrivateIp()}:8080/bz'\n";
            string leakedBatPath = Path.Combine(sandbox, "leaked_installer.bat");
            File.WriteAllText(leakedBatPath, batHeader + Convert.ToBase64String(Utf8NoBom.GetBytes(leakedPs)) + "\r\n", Encoding.ASCII);

            var leakRes = RunPublicSyncScan(leakedBatPath, checkCopyright: false);
            Assert.NotEqual(0, leakRes.ExitCode);
            Assert.Contains("[ZERO-LEAK-HIT]", leakRes.StdErr, StringComparison.Ordinal);

            // 3. 独立 ::PS_BEGIN:: 后接损坏 Base64 必须 fail-closed 拦截
            string brokenBatPath = Path.Combine(sandbox, "broken_base64.bat");
            File.WriteAllText(brokenBatPath, batHeader + "!!!NOT_VALID_BASE64!!!\r\n", Encoding.ASCII);

            var brokenRes = RunPublicSyncScan(brokenBatPath, checkCopyright: false);
            Assert.NotEqual(0, brokenRes.ExitCode);
            Assert.Contains("[BAT-BASE64-INVALID]", brokenRes.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CleanupDir(sandbox);
        }
    }

    [Fact]
    public void ZeroLeakScan_StreamsOver5MB_AndCatchesSecretAcross2MBChunkBoundary()
    {
        string sandbox = CreateSandbox("w1d_gate_bigfile");
        try
        {
            string bigFile = Path.Combine(sandbox, "large_dump.txt");
            const int totalSize = 6 * 1024 * 1024 + 256 * 1024; // 6.25 MB > 5 MB
            byte[] payload = new byte[totalSize];
            Array.Fill(payload, (byte)'A');

            // 精确跨 2MB 块边界写入私钥头，检验 4KB 重叠滑窗零边界漏报
            byte[] secretBytes = Encoding.ASCII.GetBytes(BuildSamplePrivateKeyHeader());
            int boundaryOffset = 2 * 1024 * 1024 - 10;
            Buffer.BlockCopy(secretBytes, 0, payload, boundaryOffset, secretBytes.Length);
            File.WriteAllBytes(bigFile, payload);

            var rawRes = RunPublicSyncScan(bigFile, checkCopyright: false);
            Assert.NotEqual(0, rawRes.ExitCode);
            Assert.Contains("[ZERO-LEAK-HIT]", rawRes.StdErr, StringComparison.Ordinal);

            // 打包进 ZIP 后再扫同样不能跳过 >5MB 条目
            string bigZip = Path.Combine(sandbox, "large_bundle.zip");
            using (var za = ZipFile.Open(bigZip, ZipArchiveMode.Create))
            {
                za.CreateEntryFromFile(bigFile, "data/large_dump.txt", CompressionLevel.Fastest);
            }

            var zipRes = RunPublicSyncScan(bigZip, checkCopyright: false);
            Assert.NotEqual(0, zipRes.ExitCode);
            Assert.Contains("[ZERO-LEAK-HIT]", zipRes.StdErr, StringComparison.Ordinal);
        }
        finally
        {
            CleanupDir(sandbox);
        }
    }

    [Fact]
    public void ZeroLeakScan_GeneralizedRules_BlockSecretsAndPublicIp_AllowLoopbackAndRfc5737()
    {
        string sandbox = CreateSandbox("w1d_gate_general");
        try
        {
            // 洁净样本：合法回环、RFC1918、RFC5737 文档段、0.0.0.0 与版本号全部零误报
            string cleanFile = Path.Combine(sandbox, "clean_sample.cs");
            File.WriteAllText(
                cleanFile,
                "using System.Reflection;\n" +
                "[assembly: AssemblyVersion(\"15.4.0.0\")]\n" +
                "[assembly: AssemblyFileVersion(\"1.0.0.0\")]\n" +
                "// Local: http://127.0.0.1:8080, LAN: 10.0.0.8 / 192.168.1.5 / 172.16.0.12\n" +
                "// RFC5737 Docs: http://203.0.113.10:8080, 198.51.100.2, 192.0.2.1, Bind: 0.0.0.0\n",
                Utf8NoBom);
            var cleanRes = RunPublicSyncScan(cleanFile, checkCopyright: false);
            Assert.True(cleanRes.ExitCode == 0, $"合法地址与版本号不应误报：\n{cleanRes.StdOut}\n{cleanRes.StdErr}");

            // 5 类泛化泄露样本必须 100% 拦截
            (string Name, string Content)[] badSamples =
            {
                ("token.txt", $"token = \"{BuildSampleGhToken()}\"\n"),
                ("pubip.txt", $"remote = \"http://{BuildSamplePublicIp()}:8080\"\n"),
                ("ws.txt", $"repo = \"{BuildSampleWorkspacePath()}\"\n"),
                ("pem.txt", $"key = \"{BuildSampleUserPemPath()}\"\n"),
                ("compass.txt", $"source = \"{BuildSampleCompassKeyword()}\"\n"),
            };

            foreach (var (name, content) in badSamples)
            {
                string f = Path.Combine(sandbox, name);
                File.WriteAllText(f, content, Utf8NoBom);
                var res = RunPublicSyncScan(f, checkCopyright: false);
                Assert.NotEqual(0, res.ExitCode);
                Assert.Contains("[ZERO-LEAK-HIT]", res.StdErr, StringComparison.Ordinal);
            }
        }
        finally
        {
            CleanupDir(sandbox);
        }
    }

    [Fact]
    public void CopyrightGate_BlocksVanillaDllsGameDataDbReconAndBackslashBypass_AllowsCleanBundle()
    {
        string sandbox = CreateSandbox("w1d_gate_copyright");
        try
        {
            string[] forbiddenEntries =
            {
                "server/BazaarGameShared.dll",
                @"server\BazaarBattleService.dll",
                "data/GameData.db",
                "docs/recon/src_runtime/Foo.cs",
                "data/translations/zh-CN.bytes",
                "BazaarHub/hub-presets.json",
            };

            foreach (string entryName in forbiddenEntries)
            {
                string badZip = Path.Combine(sandbox, $"bad_{Guid.NewGuid():N}.zip");
                using (var za = ZipFile.Open(badZip, ZipArchiveMode.Create))
                {
                    var e = za.CreateEntry(entryName);
                    using var s = e.Open();
                    byte[] b = Utf8NoBom.GetBytes("stub");
                    s.Write(b, 0, b.Length);
                }

                var res = RunPublicSyncScan(badZip, checkCopyright: true);
                Assert.NotEqual(0, res.ExitCode);
                Assert.Contains("[COPYRIGHT-GATE-HIT]", res.StdErr, StringComparison.Ordinal);
            }

            // 洁净自研 Release 包（含 ghost_pool_balanced.jsonl + LOCAL_ASSET_EXTRACTION.md）必须放行
            string goodZip = Path.Combine(sandbox, "clean_release.zip");
            using (var za = ZipFile.Open(goodZip, ZipArchiveMode.Create))
            {
                void AddText(string name, string text)
                {
                    var e = za.CreateEntry(name);
                    using var s = e.Open();
                    byte[] b = Utf8NoBom.GetBytes(text);
                    s.Write(b, 0, b.Length);
                }

                AddText("README.md", "# Clean Release (PTR)\n");
                AddText("LOCAL_ASSET_EXTRACTION.md", "Copy from your local Steam PTR installation.\n");
                AddText("data/ghost_pool_balanced.jsonl", "{\"day\":1,\"hero\":\"Vanessa\"}\n");
                AddText("data/translations/custom_zh_overlay.json", "{\"schemaVersion\":1}\n");
                AddText("payload/BazaarLauncher.dll", "FAKE_OPEN_SOURCE_PLUGIN");
            }

            var goodRes = RunPublicSyncScan(goodZip, checkCopyright: true);
            Assert.True(goodRes.ExitCode == 0, $"洁净自研包应通过版权门禁：\n{goodRes.StdOut}\n{goodRes.StdErr}");
        }
        finally
        {
            CleanupDir(sandbox);
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) RunPublicSyncScan(string targetPath, bool checkCopyright)
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string scriptPath = Path.Combine(repoRoot, "tools", "sync-github-public.ps1");
        Assert.True(File.Exists(scriptPath), $"未找到脚本：{scriptPath}");

        var psi = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = repoRoot,
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
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-ScanOnlyTarget");
        psi.ArgumentList.Add(targetPath);
        if (checkCopyright)
        {
            psi.ArgumentList.Add("-CheckCopyright");
        }

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 pwsh 执行零泄露扫描");
        Task<string> outTask = proc.StandardOutput.ReadToEndAsync();
        Task<string> errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(45000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("sync-github-public.ps1 -ScanOnlyTarget 执行超时");
        }
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CreateSandbox(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

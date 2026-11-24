using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BazaarCore.Tests.TestInfrastructure;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3A Web 工坊前端静态/运行时安全回归 + Python SDK CLI (`tools/mod_sdk_cli.py`) 端到端测试：
/// 覆盖 P0-19、P0-37 前端闭环、D5-P1-05/06/10、D5-P2-01~06；
/// 全程使用独立临时目录，无全局静态状态写入，可安全并发运行。
/// </summary>
public sealed class Wave3A_WorkshopFrontendAndCliTests
{
    private static string RepoRoot => SharedGameDataFixture.Shared.RepoRoot;
    private static string WorkshopRoot => Path.Combine(RepoRoot, "src", "BazaarServer", "wwwroot", "workshop");
    private static string CliScript => Path.Combine(RepoRoot, "tools", "mod_sdk_cli.py");

    private static (int ExitCode, string StdOut, string StdErr) RunProcess(string fileName, string arguments, string workingDir, int timeoutMs = 45000)
    {
        using var proc = new Process();
        proc.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        proc.StartInfo.Environment["PYTHONUTF8"] = "1";
        proc.StartInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.StandardInput.Close(); // 即时 EOF，严防挂起
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"子进程超时 ({timeoutMs}ms): {fileName} {arguments}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        }
        proc.WaitForExit();
        return (proc.ExitCode, stdout.ToString(), stderr.ToString());
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

    private static byte[] BuildValidJpeg64x64() => new byte[]
    {
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
        0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x40, 0x00, 0x40, 0x03, 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x00,
        0xFF, 0xD9
    };

    [Fact]
    public void WorkshopFrontend_SecurityHardening_VfxCompat_V154Ui_AndNodeSyntaxCheck()
    {
        string html = File.ReadAllText(Path.Combine(WorkshopRoot, "index.html"), Encoding.UTF8);
        string css = File.ReadAllText(Path.Combine(WorkshopRoot, "styles.css"), Encoding.UTF8);
        string appJs = File.ReadAllText(Path.Combine(WorkshopRoot, "app.js"), Encoding.UTF8);
        string studioJs = File.ReadAllText(Path.Combine(WorkshopRoot, "studio-v14.js"), Encoding.UTF8);

        // 1. 安全加固：零私服 IP 泄漏、管理员 Token 迁 sessionStorage 且清理历史 localStorage、DOM-XSS 防护
        Assert.DoesNotContain("<YOUR_SERVER_IP>", appJs);
        Assert.DoesNotContain("<YOUR_SERVER_IP>", studioJs);
        Assert.DoesNotContain("<YOUR_SERVER_IP>", html);
        Assert.DoesNotContain("localStorage.setItem('bazaar_workshop_token'", appJs);
        Assert.DoesNotContain("localStorage.setItem(\"bazaar_workshop_token\"", appJs);
        Assert.Contains("sessionStorage.getItem('bazaar_workshop_token')", appJs);
        Assert.Contains("sessionStorage.setItem('bazaar_workshop_token'", appJs);
        Assert.Contains("localStorage.removeItem('bazaar_workshop_token')", appJs);
        Assert.Contains("safeNumAttr", studioJs);

        // 2. P0-19 VFX 键名 camelCase 写入与历史大小写兼容读取
        Assert.Contains("vfxOverrideKey", studioJs);
        Assert.Contains("vFXOverrideKey", studioJs);
        Assert.Contains("VFXOverrideKey", studioJs);
        Assert.DoesNotContain("vfxConfig: { vFXOverrideKey:", studioJs);

        // 3. P0-37 前端闭环：校验面板兼容 validation || report + X-Uploader-Secret 保存与展示
        Assert.Contains("validation || ", appJs);
        Assert.Contains("X-Uploader-Secret", appJs);
        Assert.Contains("communityUploaderSecret", html);
        Assert.Contains("communitySecretBanner", html);

        // 4. V15.4 完整契约 UI：5 阶 Legendary、Custom_0..8、10 大通用管线钩子、4 种事件类型 + evHours -> ev.hours、4 类扩展工作室
        Assert.Contains("Legendary", html);
        Assert.Contains("Custom_8", html);
        Assert.Contains("10 大通用", html);
        Assert.DoesNotContain("6 大钩子", html);
        Assert.DoesNotContain("6 大通用", html);
        foreach (string evType in new[] { "EventEncounter", "PedestalEncounter", "EncounterStep", "CombatEncounter" })
            Assert.Contains(evType, html);
        Assert.Contains("id=\"evHours\"", html);
        Assert.Contains("id=\"extraContentStudio\"", html);
        Assert.Contains("id=\"abAuraActiveIn\"", html);
        Assert.Contains("ev.hours", appJs);
        Assert.Contains("grantIfMissing", studioJs);
        Assert.Contains("allowDraft=true", appJs);
        Assert.Contains(".tier-Legendary", css);
        Assert.Contains(".bc-counter-badges", css);

        // 5. 若安装 node，则执行真机语法检查与 safeNumAttr XSS 回归测试
        try
        {
            var (vCode, _, _) = RunProcess("node", "--version", RepoRoot, 10000);
            if (vCode == 0)
            {
                var (c1, _, e1) = RunProcess("node", $"--check \"{Path.Combine(WorkshopRoot, "app.js")}\"", RepoRoot, 15000);
                Assert.True(c1 == 0, $"app.js 语法错误：{e1}");
                var (c2, _, e2) = RunProcess("node", $"--check \"{Path.Combine(WorkshopRoot, "studio-v14.js")}\"", RepoRoot, 15000);
                Assert.True(c2 == 0, $"studio-v14.js 语法错误：{e2}");

                // 验证 safeNumAttr 对恶意属性注入 payload 返回空串且保留合法数字
                const string testExpr = "const fs=require('fs');const s=fs.readFileSync(process.argv[1],'utf8');" +
                    "const m=s.match(/function safeNumAttr\\(([^)]*)\\)\\s*\\{([\\s\\S]*?)\\n\\s*\\}/);" +
                    "if(!m) throw new Error('missing safeNumAttr');" +
                    "const fn=new Function(m[1].trim(), m[2]);" +
                    "if(fn('1\" onfocus=\"alert(1)') !== '') throw new Error('XSS not stripped');" +
                    "if(fn(42) !== '42') throw new Error('valid int failed');" +
                    "if(fn(-3.5) !== '-3.5') throw new Error('valid float failed');" +
                    "console.log('XSS_GUARD_OK');";
                var (c3, o3, e3) = RunProcess("node", $"-e \"{testExpr.Replace("\"", "\\\"")}\" \"{Path.Combine(WorkshopRoot, "app.js")}\"", RepoRoot, 15000);
                Assert.True(c3 == 0 && o3.Contains("XSS_GUARD_OK"), $"safeNumAttr 运行时断言失败: code={c3}\nOUT={o3}\nERR={e3}");
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 环境无 node 时跳过 JS 引擎执行，仍保留全部静态契约断言
        }
    }

    [Fact]
    public void ModSdkCli_NewCloneAndStatic8Gates_CoverageAndPngJpegGuard()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"bazaar_w3a_cli_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        try
        {
            // 1. new 命令生成 V15.4 完整目录结构（含 mechanics/custom_mechanics.json 与 assets/*）
            var (cNew, oNew, eNew) = RunProcess("python", $"\"{CliScript}\" new cli-forge --name \"CLI工坊测试\" --mods-dir \"{tmp}\"", RepoRoot);
            Assert.True(cNew == 0, $"cli new 失败:\nOUT={oNew}\nERR={eNew}");

            string modDir = Path.Combine(tmp, "cli-forge");
            Assert.True(File.Exists(Path.Combine(modDir, "mod.json")));
            Assert.True(File.Exists(Path.Combine(modDir, "mechanics", "custom_mechanics.json")));

            var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "mod.json"), Encoding.UTF8)).RootElement;
            Assert.Equal("mechanics/custom_mechanics.json", manifest.GetProperty("contentRoots").GetProperty("mechanics").GetString());
            Assert.Equal("assets/*", manifest.GetProperty("contentRoots").GetProperty("assets").GetString());

            // 2. clone-card 保留 enchantments 与 5 阶框架
            var (cClone, oClone, eClone) = RunProcess("python", $"\"{CliScript}\" clone-card \"Kukri\" --mod cli-forge --as cloned_kukri --mods-dir \"{tmp}\"", RepoRoot);
            Assert.True(cClone == 0, $"cli clone-card 失败:\nOUT={oClone}\nERR={eClone}");

            var cardsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modDir, "cards", "custom_cards.json"), Encoding.UTF8)).RootElement;
            var clonedCard = cardsDoc.GetProperty("cards").EnumerateArray()
                .First(c => c.GetProperty("customId").GetString() == "cli-forge:cloned_kukri");
            Assert.True(clonedCard.TryGetProperty("enchantments", out var enchElem) && enchElem.EnumerateObject().Any(),
                "clone-card 应保留原版卡的 enchantments");

            // 3. 写入合法 PNG + JPEG 后，先校验缺失资产、WebP、非法机制、minDay>maxDay、空事件步骤均被静态 8 闸门拦截
            Directory.CreateDirectory(Path.Combine(modDir, "assets"));
            File.WriteAllBytes(Path.Combine(modDir, "assets", "bad.webp"), Encoding.ASCII.GetBytes("RIFF\x20\x00\x00\x00WEBPVP8X01234567890123456789"));

            File.WriteAllText(Path.Combine(modDir, "mechanics", "custom_mechanics.json"), """
            {
              "schemaVersion": 1,
              "modId": "cli-forge",
              "mechanics": [
                {
                  "id": "cli-forge:bad_mech",
                  "nameZh": "坏机制",
                  "counterDisplay": { "labelZh": "槽", "syncAttribute": "Custom_9" },
                  "hooks": [
                    { "id": "h1", "hook": "BogusHook", "scope": "BogusScope", "steps": [ { "op": "BogusOp" } ] }
                  ]
                }
              ]
            }
            """, Encoding.UTF8);

            File.WriteAllText(Path.Combine(modDir, "events", "custom_events.json"), """
            {
              "schemaVersion": 1,
              "modId": "cli-forge",
              "events": [
                {
                  "eventId": "cli-forge:bad_event",
                  "titleZh": "坏事件",
                  "type": "EventEncounter",
                  "minDay": 9,
                  "maxDay": 2,
                  "steps": []
                }
              ]
            }
            """, Encoding.UTF8);

            // 给克隆卡挂一个不存在的 customArtFile 以验证 found_assets 死代码已激活
            string cardsRaw = File.ReadAllText(Path.Combine(modDir, "cards", "custom_cards.json"), Encoding.UTF8);
            var cardObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(cardsRaw)!;
            var cardList = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(cardObj["cards"].GetRawText())!;
            var clonedEntry = cardList.First(c => c.TryGetValue("customId", out var cid) && cid?.ToString() == "cli-forge:cloned_kukri");
            clonedEntry["customArtFile"] = "assets/does_not_exist.png";
            File.WriteAllText(Path.Combine(modDir, "cards", "custom_cards.json"),
                JsonSerializer.Serialize(new { schemaVersion = 1, modId = "cli-forge", cards = cardList }), Encoding.UTF8);

            var (cBad, oBad, eBad) = RunProcess("python", $"\"{CliScript}\" validate \"{modDir}\" --static-only --json", RepoRoot);
            Assert.NotEqual(0, cBad);
            using var badReport = JsonDocument.Parse(oBad);
            var issueCodes = badReport.RootElement.GetProperty("gates").EnumerateArray()
                .SelectMany(g => g.GetProperty("issues").EnumerateArray())
                .Select(i => i.GetProperty("code").GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("MISSING_ASSET_FILE", issueCodes);
            Assert.Contains("ASSET_FORMAT_UNSUPPORTED_FOR_UNITY", issueCodes);
            Assert.Contains("MECHANIC_UNKNOWN_HOOK", issueCodes);
            Assert.Contains("MECHANIC_UNKNOWN_SCOPE", issueCodes);
            Assert.Contains("MECHANIC_UNKNOWN_OP", issueCodes);
            Assert.Contains("MECHANIC_INVALID_COUNTER_ATTR", issueCodes);
            Assert.Contains("INVALID_EVENT_DAY_RANGE", issueCodes);
            Assert.Contains("EVENT_EMPTY_STEPS", issueCodes);

            // 4. 修复为合法 PNG + JPEG + 合法机制与事件 -> 静态校验应 0 Error 通过
            File.Delete(Path.Combine(modDir, "assets", "bad.webp"));
            File.WriteAllBytes(Path.Combine(modDir, "assets", "ok_art.png"), BuildValidPng64x64());
            File.WriteAllBytes(Path.Combine(modDir, "assets", "ok_bg.jpg"), BuildValidJpeg64x64());
            clonedEntry["customArtFile"] = "assets/ok_art.png";
            clonedEntry["customBgArtFile"] = "assets/ok_bg.jpg";
            File.WriteAllText(Path.Combine(modDir, "cards", "custom_cards.json"),
                JsonSerializer.Serialize(new { schemaVersion = 1, modId = "cli-forge", cards = cardList }), Encoding.UTF8);
            File.WriteAllText(Path.Combine(modDir, "mechanics", "custom_mechanics.json"), """
            {
              "schemaVersion": 1,
              "modId": "cli-forge",
              "mechanics": [
                {
                  "id": "cli-forge:good_mech",
                  "nameZh": "好机制",
                  "counterDisplay": { "labelZh": "充能", "syncAttribute": "Custom_8" },
                  "hooks": [
                    { "id": "h1", "hook": "OnFightStart", "scope": "SelfCard", "steps": [ { "op": "ModifyCounter", "key": "charge", "operation": "Set", "value": { "const": 0 }, "syncAttribute": "Custom_8" } ] }
                  ]
                }
              ]
            }
            """, Encoding.UTF8);
            File.WriteAllText(Path.Combine(modDir, "events", "custom_events.json"), """
            {
              "schemaVersion": 1,
              "modId": "cli-forge",
              "events": []
            }
            """, Encoding.UTF8);

            var (cGood, oGood, eGood) = RunProcess("python", $"\"{CliScript}\" validate \"{modDir}\" --static-only --json", RepoRoot);
            Assert.True(cGood == 0, $"修复后静态校验应通过:\nOUT={oGood}\nERR={eGood}");
            using var goodReport = JsonDocument.Parse(oGood);
            Assert.True(goodReport.RootElement.GetProperty("passed").GetBoolean());
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ModSdkCli_Unpack_HardenedAgainstZipSlip_Backslash_Drive_Symlink_Dll_ZipBomb_AndAtomicSuccess()
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"bazaar_w3a_unpack_{Guid.NewGuid():N}");
        string outDir = Path.Combine(tmp, "out");
        Directory.CreateDirectory(outDir);
        try
        {
            static string WriteMaliciousZip(string path, Action<ZipArchive> populate)
            {
                using var fs = File.Create(path);
                using var za = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);
                populate(za);
                return path;
            }

            // 1. Zip Slip ../escape.txt -> exit code 4
            string z1 = WriteMaliciousZip(Path.Combine(tmp, "z1_slip.zip"), z =>
            {
                var e = z.CreateEntry("../escape.txt");
                using var sw = new StreamWriter(e.Open()); sw.Write("pwn");
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z1}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 2. Windows 盘符路径 C:/evil.txt -> exit code 4
            string z2 = WriteMaliciousZip(Path.Combine(tmp, "z2_drive.zip"), z =>
            {
                var e = z.CreateEntry("C:/evil.txt");
                using var sw = new StreamWriter(e.Open()); sw.Write("pwn");
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z2}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 3. 反斜杠穿越（Linux 上易绕过） -> exit code 4
            string z3 = WriteMaliciousZip(Path.Combine(tmp, "z3_backslash.zip"), z =>
            {
                var e = z.CreateEntry(@"foo\..\..\evil.txt");
                using var sw = new StreamWriter(e.Open()); sw.Write("pwn");
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z3}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 4. 禁止扩展名 .dll -> exit code 4
            string z4 = WriteMaliciousZip(Path.Combine(tmp, "z4_dll.zip"), z =>
            {
                var e = z.CreateEntry("badmod/payload.dll");
                using var sw = new StreamWriter(e.Open()); sw.Write("MZ");
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z4}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 5. ZIP 符号链接条目 -> exit code 4
            string z5 = WriteMaliciousZip(Path.Combine(tmp, "z5_symlink.zip"), z =>
            {
                var e = z.CreateEntry("badmod/link.json");
                e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); // POSIX S_IFLNK (0120777)
                using var sw = new StreamWriter(e.Open()); sw.Write("/etc/passwd");
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z5}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 6. Zip Bomb（压缩全零数据总解压体积 > 64 MiB）-> exit code 4
            string z6 = WriteMaliciousZip(Path.Combine(tmp, "z6_bomb.zip"), z =>
            {
                byte[] chunk = new byte[1024 * 1024]; // 1 MiB zeros（极高压缩比）
                for (int i = 0; i < 66; i++) // 66 MiB > 64 MiB 上限
                {
                    var e = z.CreateEntry($"bomb/part_{i}.txt", CompressionLevel.SmallestSize);
                    using var es = e.Open();
                    es.Write(chunk, 0, chunk.Length);
                }
            });
            Assert.Equal(4, RunProcess("python", $"\"{CliScript}\" unpack \"{z6}\" --into \"{outDir}\" --allow-static-only", RepoRoot).ExitCode);

            // 恶意包拦截后 outDir 应 0 污染
            Assert.Empty(Directory.GetFileSystemEntries(outDir));

            // 7. 正常合法包 pack + unpack 原子落盘成功且无 .tmp_/.bak_ 残留
            string sampleMod = Path.Combine(RepoRoot, "mods", "clockwork-forge");
            string packedZip = Path.Combine(tmp, "clockwork-forge.bazaarmod.zip");
            var (cPack, oPack, ePack) = RunProcess("python", $"\"{CliScript}\" pack \"{sampleMod}\" --out \"{packedZip}\"", RepoRoot);
            Assert.True(cPack == 0, $"cli pack 失败:\nOUT={oPack}\nERR={ePack}");

            var (cUnpack, oUnpack, eUnpack) = RunProcess("python", $"\"{CliScript}\" unpack \"{packedZip}\" --into \"{outDir}\" --allow-static-only", RepoRoot);
            Assert.True(cUnpack == 0, $"cli unpack 失败:\nOUT={oUnpack}\nERR={eUnpack}");
            Assert.True(File.Exists(Path.Combine(outDir, "clockwork-forge", "mod.json")));

            var leftover = Directory.GetDirectories(outDir)
                .Select(Path.GetFileName)
                .Where(n => n != null && (n.StartsWith(".tmp_", StringComparison.Ordinal) || n.StartsWith(".bak_", StringComparison.Ordinal)))
                .ToList();
            Assert.Empty(leftover);
        }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); } catch { }
        }
    }
}

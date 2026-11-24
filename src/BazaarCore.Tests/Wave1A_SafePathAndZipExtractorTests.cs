using System.IO.Compression;
using System.Text;
using BazaarCore.Mods;
using BazaarCore.Tests.TestInfrastructure;
using BazaarServer.Mods;
using Xunit;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 1A 回归测试（文件 2）：覆盖 P0-34 共享 SafePathGuard / SafeZipExtractor、
/// Windows 反斜杠目录条目跨平台解压、多条目伪造头 Zip Bomb 防护、Zip Slip / 符号链接 / Windows 保留设备名与前缀绕过防护。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1A_SafePathAndZipExtractorTests
{
    private static string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"bazaar_w1a_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanupDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }

    /// <summary>克隆仓库内合法 clockwork-forge 并强制用 Windows 反斜杠目录/文件路径打包。</summary>
    private static byte[] BuildBackslashModZip(string newModId)
    {
        string srcDir = Path.Combine(SharedGameDataFixture.Shared.RepoRoot, "mods", "clockwork-forge");
        Assert.True(Directory.Exists(srcDir), "缺少示例 Mod 目录 mods/clockwork-forge");

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            // 显式写入带反斜杠结尾的 Windows 风格目录条目（修复前在 Linux 上未命中 EndsWith('/') 直接 File.Create 抛异常）
            archive.CreateEntry($"{newModId}\\");
            archive.CreateEntry($"{newModId}\\cards\\");
            archive.CreateEntry($"{newModId}\\assets\\");

            foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(srcDir, file).Replace('/', '\\');
                if (rel.StartsWith(".", StringComparison.Ordinal)) continue;

                var entry = archive.CreateEntry($"{newModId}\\{rel}", CompressionLevel.Optimal);
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
    public async Task SafeZipExtractor_WindowsBackslashDirectoryEntries_ShouldExtractCleanlyCrossPlatform()
    {
        string tempRoot = CreateTempDir("bslash");
        try
        {
            const string modId = "w1a-backslash-mod";
            byte[] zipBytes = BuildBackslashModZip(modId);

            // 1. SafeZipExtractor 直接解压验证
            string directOut = Path.Combine(tempRoot, "direct");
            var res = SafeZipExtractor.ExtractToDirectory(
                zipBytes,
                directOut,
                new SafeZipExtractOptions(MaxEntries: 128, MaxTotalUncompressedBytes: 32L * 1024 * 1024, MaxSingleFileBytes: 16L * 1024 * 1024));
            Assert.True(res.Ok, $"反斜杠目录条目应安全解压，实际错误：{res.ErrorZh}");
            Assert.True(File.Exists(Path.Combine(directOut, modId, "mod.json")));

            // 2. CommunityModStore 上传验证
            var fx = SharedGameDataFixture.Shared;
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(stateDir);
            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var store = new CommunityModStore(host, stateDir);

            var up = await store.UploadZipAsync(zipBytes, "acc_bslash", "BSlashUser", isAdmin: false);
            Assert.True(up.Ok, $"CommunityModStore 应接收 Windows 反斜杠目录条目 ZIP：{up.Error}");

            // 3. WorkshopValidatorService 导入验证
            var validator = new WorkshopValidatorService(host);
            var imp = await validator.ImportZipAsync(zipBytes, allowDraftWithWarnings: false);
            Assert.True(imp.Ok, $"WorkshopValidatorService 应接收 Windows 反斜杠目录条目 ZIP：{imp.Error}");
        }
        finally
        {
            CleanupDir(tempRoot);
        }
    }

    [Fact]
    public async Task SafeZipExtractor_MultiEntryZipBombWithForgedHeader_ShouldAbortAtTotalLimit()
    {
        string tempRoot = CreateTempDir("zipbomb");
        try
        {
            // 构造 20 个高压缩比全零文件（每个 2 MB，合计 40 MB > 32 MB 上限；压缩后仅几十 KB 绕过 10 MB 压缩体积检查）
            byte[] twoMbZeros = new byte[2 * 1024 * 1024];
            byte[] bombZip;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        var e = archive.CreateEntry($"bomb/part_{i:D2}.json", CompressionLevel.SmallestSize);
                        using var s = e.Open();
                        s.Write(twoMbZeros, 0, twoMbZeros.Length);
                    }
                }
                bombZip = ms.ToArray();
            }
            Assert.True(bombZip.Length < 512 * 1024, "高压缩比 Zip Bomb 压缩后应远小于 512KB");

            // 1. SafeZipExtractor 真实流跨条目累计配额拦截（32 MB 上限）
            string outDir = Path.Combine(tempRoot, "out");
            var res = SafeZipExtractor.ExtractToDirectory(
                bombZip,
                outDir,
                new SafeZipExtractOptions(
                    MaxEntries: 64,
                    MaxTotalUncompressedBytes: CommunityModStore.MaxCommunityUncompressedBytes,
                    MaxSingleFileBytes: CommunityModStore.MaxCommunityUncompressedBytes));
            Assert.False(res.Ok);
            Assert.Contains("32", res.ErrorZh ?? string.Empty);

            // 2. 即使单文件头伪造或被限制在 2MB 总限内，跨条目流配额绝不重置
            string tightDir = Path.Combine(tempRoot, "tight");
            var tightRes = SafeZipExtractor.ExtractToDirectory(
                bombZip,
                tightDir,
                new SafeZipExtractOptions(MaxEntries: 64, MaxTotalUncompressedBytes: 3L * 1024 * 1024, MaxSingleFileBytes: 2L * 1024 * 1024));
            Assert.False(tightRes.Ok);

            // 3. CommunityModStore 拒绝且临时目录零残留
            var fx = SharedGameDataFixture.Shared;
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(stateDir);
            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var store = new CommunityModStore(host, stateDir);

            var up = await store.UploadZipAsync(bombZip, "acc_bomb", "Bomber", isAdmin: false);
            Assert.False(up.Ok);
            Assert.Equal(400, up.StatusCode);
            Assert.Empty(Directory.GetDirectories(store.RootDirectory, ".upload_*"));
        }
        finally
        {
            CleanupDir(tempRoot);
        }
    }

    [Fact]
    public void SafeZipExtractor_ZipSlip_Symlink_And_DeviceNames_MustBeRejected()
    {
        string tempRoot = CreateTempDir("malicious_zip");
        try
        {
            var opts = new SafeZipExtractOptions(
                MaxEntries: 32,
                MaxTotalUncompressedBytes: 1024 * 1024,
                MaxSingleFileBytes: 512 * 1024,
                ForbiddenExtensions: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".dll", ".exe", ".ps1" },
                RejectSymlinks: true);

            string[] badEntries =
            [
                "../evil.json",
                "a/../../evil.json",
                "C:/evil.json",
                "../evil_dir\\",
                "CON.json",
                "a/NUL",
                "a/LPT1.txt",
                "evil.dll",
                "dir/file.png:zone.identifier"
            ];

            foreach (string entryName in badEntries)
            {
                byte[] zip = BuildSingleEntryZip(entryName, "{}"u8.ToArray());
                string outDir = Path.Combine(tempRoot, "case_" + Guid.NewGuid().ToString("N")[..8]);
                var res = SafeZipExtractor.ExtractToDirectory(zip, outDir, opts);
                Assert.False(res.Ok, $"恶意条目 '{entryName}' 必须被 SafeZipExtractor 拒绝");
            }

            // Unix symlink 外部属性 (0xA000 << 16) 必须拒绝
            byte[] symlinkZip;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    var e = archive.CreateEntry("mod.json");
                    e.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                    using var s = e.Open();
                    s.Write("/etc/passwd"u8);
                }
                symlinkZip = ms.ToArray();
            }
            var symRes = SafeZipExtractor.ExtractToDirectory(symlinkZip, Path.Combine(tempRoot, "sym"), opts);
            Assert.False(symRes.Ok);

            // 大小写冲突重复条目必须拒绝
            byte[] dupZip;
            using (var ms = new MemoryStream())
            {
                using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    var e1 = archive.CreateEntry("mod.json");
                    using (var s1 = e1.Open()) s1.Write("{}"u8);
                    var e2 = archive.CreateEntry("MOD.JSON");
                    using (var s2 = e2.Open()) s2.Write("{}"u8);
                }
                dupZip = ms.ToArray();
            }
            var dupRes = SafeZipExtractor.ExtractToDirectory(dupZip, Path.Combine(tempRoot, "dup"), opts);
            Assert.False(dupRes.Ok);

            // 确保根目录外无任何 evil.json 逃逸
            Assert.False(File.Exists(Path.Combine(tempRoot, "evil.json")));
        }
        finally
        {
            CleanupDir(tempRoot);
        }
    }

    [Fact]
    public void SafePathGuard_SiblingPrefixBypass_And_Sha256Whitelist()
    {
        string tempRoot = CreateTempDir("pathguard");
        try
        {
            string baseRoot = Path.Combine(tempRoot, "root");
            string evilSibling = Path.Combine(tempRoot, "root_evil");
            Directory.CreateDirectory(baseRoot);
            Directory.CreateDirectory(evilSibling);
            File.WriteAllText(Path.Combine(evilSibling, "x.png"), "evil");

            // 兄弟目录前缀绕过测试：/tmp/.../root vs ../root_evil/x.png
            Assert.False(SafePathGuard.TryResolveUnderRoot(baseRoot, "../root_evil/x.png", out _));
            Assert.False(SafePathGuard.TryResolveUnderRoot(baseRoot, "..\\root_evil\\x.png", out _));
            Assert.True(SafePathGuard.TryResolveUnderRoot(baseRoot, "assets/icon.png", out string okFull));
            Assert.StartsWith(Path.GetFullPath(baseRoot) + Path.DirectorySeparatorChar, okFull, SafePathGuard.OsPathComparison);

            // 非法 modId 与 sha256 白名单测试
            Assert.False(SafePathGuard.IsValidModId("../plugins"));
            Assert.False(SafePathGuard.IsValidModId("a/b"));
            Assert.False(SafePathGuard.IsValidModId(""));
            Assert.False(SafePathGuard.IsValidModId(new string('a', 65)));
            Assert.True(SafePathGuard.IsValidModId("clockwork-forge"));

            Assert.False(SafePathGuard.IsValidSha256Hex(".."));
            Assert.False(SafePathGuard.IsValidSha256Hex("abc123"));
            Assert.False(SafePathGuard.IsValidSha256Hex(new string('g', 64)));
            Assert.True(SafePathGuard.IsValidSha256Hex(new string('a', 64)));

            // WorkshopValidatorService.TryResolveDraftAssetFile 必须拒绝读取 mod.json，只允许 assets/ 下图片
            var fx = SharedGameDataFixture.Shared;
            string modsDir = Path.Combine(tempRoot, "mods");
            string stateDir = Path.Combine(tempRoot, "state");
            string sampleModDir = Path.Combine(modsDir, "asset-guard-mod");
            Directory.CreateDirectory(Path.Combine(sampleModDir, "assets"));
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(sampleModDir, "mod.json"), "{\"id\":\"asset-guard-mod\"}");
            File.WriteAllBytes(Path.Combine(sampleModDir, "assets", "card.png"), [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(Path.Combine(sampleModDir, "assets", "art_manifest.json"), "{}");

            var host = new ServerModHost(fx.Repo, fx.DataDir, stateDir, fx.CreateValidCardIdsCopy(), modsRootOverride: modsDir);
            var validator = new WorkshopValidatorService(host);

            Assert.False(validator.TryResolveDraftAssetFile("asset-guard-mod", "mod.json", out _, out _));
            Assert.False(validator.TryResolveDraftAssetFile("asset-guard-mod", "assets/art_manifest.json", out _, out _));
            Assert.False(validator.TryResolveDraftAssetFile("asset-guard-mod", "../evil.png", out _, out _));
            Assert.True(validator.TryResolveDraftAssetFile("asset-guard-mod", "assets/card.png", out string resolvedPng, out string ct));
            Assert.Equal("image/png", ct);
            Assert.True(File.Exists(resolvedPng));
        }
        finally
        {
            CleanupDir(tempRoot);
        }
    }

    private static byte[] BuildSingleEntryZip(string entryName, byte[] payload)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            var e = archive.CreateEntry(entryName);
            using var s = e.Open();
            if (!entryName.EndsWith('\\') && !entryName.EndsWith('/'))
                s.Write(payload, 0, payload.Length);
        }
        return ms.ToArray();
    }
}

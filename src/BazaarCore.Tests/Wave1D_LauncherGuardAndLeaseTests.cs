using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using BazaarCore.Tests.TestInfrastructure;

namespace BazaarCore.Tests;

/// <summary>
/// 隔离防锁 Fixture：优先复用当前 worktree 已编译的最新 BazaarLauncher.dll（0ms 零额外构建）；
/// 若产物缺失或早于最新源码，则在独立临时目录以 -p:BaseIntermediateOutputPath 隔离构建，绝不污染或锁死工作树 obj/。
/// </summary>
public sealed class LauncherAssemblyFixture : IDisposable
{
    private readonly string? _tempBuildRoot;
    private readonly AssemblyLoadContext _alc;

    public Assembly LauncherAssembly { get; }
    public string LauncherDllPath { get; }

    public LauncherAssemblyFixture()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();
        string mainRepoRoot = TestDataLocator.ResolveMainRepoRoot();
        string launcherDir = Path.Combine(repoRoot, "src", "BazaarLauncher");
        string csprojPath = Path.Combine(launcherDir, "BazaarLauncher.csproj");
        Assert.True(File.Exists(csprojPath), $"未找到 BazaarLauncher.csproj：{csprojPath}");

        string? reusableDll = TryFindFreshBuiltDll(launcherDir);
        if (reusableDll != null)
        {
            LauncherDllPath = reusableDll;
        }
        else
        {
            _tempBuildRoot = Path.Combine(Path.GetTempPath(), $"w1d_launcher_build_{Guid.NewGuid():N}");
            string tempObj = Path.Combine(_tempBuildRoot, "obj");
            string tempOut = Path.Combine(_tempBuildRoot, "out");
            Directory.CreateDirectory(tempObj);
            Directory.CreateDirectory(tempOut);

            string? managedDir = ResolveManagedDir(repoRoot, mainRepoRoot);
            string? bepinexCore = ResolveBepInExCoreDir(repoRoot, mainRepoRoot);

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
            if (!string.IsNullOrWhiteSpace(managedDir))
                psi.ArgumentList.Add($"-p:ManagedDir={managedDir}");
            if (!string.IsNullOrWhiteSpace(bepinexCore))
                psi.ArgumentList.Add($"-p:BepInExCore={bepinexCore}");

            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 dotnet build BazaarLauncher");
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"隔离构建 BazaarLauncher 失败（ExitCode={proc.ExitCode}）：\n{stdout}\n{stderr}");
            }

            LauncherDllPath = Path.Combine(tempOut, "BazaarLauncher.dll");
            Assert.True(File.Exists(LauncherDllPath), $"未找到隔离编译产物：{LauncherDllPath}");
        }

        _alc = new AssemblyLoadContext($"Wave1D_Launcher_{Guid.NewGuid():N}", isCollectible: true);
        // 读入内存流加载，避免锁定磁盘 DLL 文件影响后续构建
        byte[] dllBytes = File.ReadAllBytes(LauncherDllPath);
        using var ms = new MemoryStream(dllBytes);
        LauncherAssembly = _alc.LoadFromStream(ms);
    }

    private static string? TryFindFreshBuiltDll(string launcherDir)
    {
        DateTime latestSourceUtc = Directory
            .EnumerateFiles(launcherDir, "*", SearchOption.AllDirectories)
            .Where(p =>
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                (p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();

        string[] candidates =
        {
            Path.Combine(launcherDir, "bin", "Release", "netstandard2.1", "BazaarLauncher.dll"),
            Path.Combine(launcherDir, "bin", "Debug", "netstandard2.1", "BazaarLauncher.dll"),
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

    private static string? ResolveManagedDir(string repoRoot, string mainRepoRoot)
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_MANAGED_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        string[] candidates =
        {
            Path.Combine(repoRoot, "ext", "Managed"),
            Path.Combine(mainRepoRoot, "ext", "Managed"),
            @"<THE_BAZAAR_GAME_DIR>\TheBazaar_Data\Managed",
            @"C:\Program Files (x86)\Steam\steamapps\common\The Bazaar\TheBazaar_Data\Managed",
            @"<THE_BAZAAR_GAME_DIR>\TheBazaar_Data\Managed",
        };
        foreach (string c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "UnityEngine.CoreModule.dll")))
                return c;
        }
        return null;
    }

    private static string? ResolveBepInExCoreDir(string repoRoot, string mainRepoRoot)
    {
        string? env = Environment.GetEnvironmentVariable("BAZAAR_BEPINEX_CORE");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        string[] candidates =
        {
            Path.Combine(repoRoot, "ext", "bepinex", "extracted", "BepInEx", "core"),
            Path.Combine(mainRepoRoot, "ext", "bepinex", "extracted", "BepInEx", "core"),
        };
        foreach (string c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "BepInEx.dll")))
                return c;
        }
        return null;
    }

    public void Dispose()
    {
        try { _alc.Unload(); } catch { }
        if (!string.IsNullOrWhiteSpace(_tempBuildRoot))
        {
            try { if (Directory.Exists(_tempBuildRoot)) Directory.Delete(_tempBuildRoot, recursive: true); } catch { }
        }
    }
}

/// <summary>
/// Wave 1D（P0-34 &amp; P0-35）：通过 AssemblyLoadContext 直接加载并验证真实 BazaarLauncher.dll 中的
/// 顶层零 Unity 依赖生产类型 SafePathAndZipGuard 与 TextureRefPool/TextureLease/TextureLeaseSlot。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave1D_LauncherGuardAndLeaseTests : IClassFixture<LauncherAssemblyFixture>
{
    private readonly LauncherAssemblyFixture _fx;

    public Wave1D_LauncherGuardAndLeaseTests(LauncherAssemblyFixture fx) => _fx = fx;

    [Fact]
    public void SafePathAndZipGuard_ModIdAndSha256_RejectsTraversalAndReservedNames()
    {
        Type? guardType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.SafePathAndZipGuard");
        Assert.NotNull(guardType);

        MethodInfo isValidModId = guardType!.GetMethod("IsValidModId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(isValidModId);

        string[] badModIds =
        {
            "", "   ", "../../plugins", "../config", @"C:\Windows", "CON", "con.json", "aux", "NUL.txt", "COM1",
            "bad..id", "trailing.", "trailing ", "a/b", @"a\b", "mod:ads", "-starts-dash", ".starts-dot",
            new string('a', 65)
        };
        foreach (string bad in badModIds)
        {
            bool ok = (bool)isValidModId.Invoke(null, new object?[] { bad })!;
            Assert.False(ok, $"非法 modId 应被拒绝：{bad}");
        }

        string[] goodModIds = { "bazaar-chronicles-sample", "mod_v1.2", "A", "hero99.pack-01" };
        foreach (string good in goodModIds)
        {
            bool ok = (bool)isValidModId.Invoke(null, new object?[] { good })!;
            Assert.True(ok, $"合法 modId 应被放行：{good}");
        }

        MethodInfo tryNormSha = guardType.GetMethod("TryNormalizeSha256Hex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(tryNormSha);

        string[] badShas =
        {
            "", "../../../../secret", new string('a', 63), new string('a', 65),
            new string('g', 64), "0123456789abcdef../0123456789abcdef0123456789abcdef0123456789abc"
        };
        foreach (string bad in badShas)
        {
            object?[] invokeArgs = { bad, null };
            bool ok = (bool)tryNormSha.Invoke(null, invokeArgs)!;
            Assert.False(ok, $"非法 sha256 应被拒绝：{bad}");
        }

        string validUpper = "SHA256:" + new string('A', 64);
        object?[] goodArgs = { validUpper, null };
        Assert.True((bool)tryNormSha.Invoke(null, goodArgs)!);
        Assert.Equal(new string('a', 64), goodArgs[1]);
    }

    [Fact]
    public void SafePathAndZipGuard_ExtractModZipSafely_WindowsBackslash_BlocksZipSlipExecAndZipBomb()
    {
        Type? guardType = _fx.LauncherAssembly.GetType("BazaarLauncher.Core.SafePathAndZipGuard");
        Assert.NotNull(guardType);

        MethodInfo extractMethod = guardType!.GetMethod(
            "ExtractModZipSafely",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.NotNull(extractMethod);

        string sandbox = Path.Combine(Path.GetTempPath(), $"w1d_launcher_zip_{Guid.NewGuid():N}");
        string modsRoot = Path.Combine(sandbox, "BepInEx", "mods");
        string pluginsRoot = Path.Combine(sandbox, "BepInEx", "plugins");
        Directory.CreateDirectory(modsRoot);
        Directory.CreateDirectory(pluginsRoot);
        string sentinelFile = Path.Combine(pluginsRoot, "sentinel.txt");
        File.WriteAllText(sentinelFile, "MUST_SURVIVE");

        try
        {
            // 1. 合法包：含 Windows 反斜杠目录条目 + 大写前缀 MY-MOD\ + 合法双点文件名 icon..v2.png
            byte[] validZip = BuildZip(
                (@"my-mod\assets\", null),
                (@"MY-MOD\mod.json", Encoding.UTF8.GetBytes("{\"id\":\"my-mod\"}")),
                (@"MY-MOD\assets\icon..v2.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }));

            string extractedDir = (string)InvokeExtract(extractMethod, validZip, modsRoot, "my-mod")!;
            Assert.True(File.Exists(Path.Combine(extractedDir, "mod.json")));
            Assert.True(File.Exists(Path.Combine(extractedDir, "assets", "icon..v2.png")));

            // 2. 恶意 modId 路径穿越绝不删外层 plugins 目录
            var exTrav = Assert.Throws<TargetInvocationException>(() =>
                InvokeExtract(extractMethod, validZip, modsRoot, "../plugins"));
            Assert.IsType<InvalidOperationException>(exTrav.InnerException);
            Assert.True(File.Exists(sentinelFile), "路径穿越拦截失败，外部 plugins 哨兵文件被破坏！");

            // 3. 夹带 .dll / .ps1 必须 Fail-Closed 整包拒收（不静默跳过继续落盘）
            byte[] execZip = BuildZip(
                ("evil-mod/mod.json", Encoding.UTF8.GetBytes("{\"id\":\"evil-mod\"}")),
                ("evil-mod/payload.dll", Encoding.UTF8.GetBytes("MZ_FAKE")));
            var exExec = Assert.Throws<TargetInvocationException>(() =>
                InvokeExtract(extractMethod, execZip, modsRoot, "evil-mod"));
            Assert.IsType<InvalidDataException>(exExec.InnerException);
            Assert.False(Directory.Exists(Path.Combine(modsRoot, "evil-mod")));

            // 4. 空 ZIP（仅目录、0 有效文件）必须拒收
            byte[] emptyZip = BuildZip((@"empty-mod\assets\", null));
            var exEmpty = Assert.Throws<TargetInvocationException>(() =>
                InvokeExtract(extractMethod, emptyZip, modsRoot, "empty-mod"));
            Assert.IsType<InvalidDataException>(exEmpty.InnerException);

            // 5. 超过 500 条目必须拒收
            var manyEntries = Enumerable.Range(0, 501)
                .Select(i => ($"many-mod/f_{i}.txt", (byte[]?)Encoding.UTF8.GetBytes("x")))
                .ToArray();
            byte[] tooManyZip = BuildZip(manyEntries);
            var exMany = Assert.Throws<TargetInvocationException>(() =>
                InvokeExtract(extractMethod, tooManyZip, modsRoot, "many-mod"));
            Assert.IsType<InvalidDataException>(exMany.InnerException);

            // 6. 跨条目真实解压流 Zip Bomb（5 × 15MB = 75MB > 64MB，压缩后仅几十 KB）
            byte[] zero15Mb = new byte[15 * 1024 * 1024];
            byte[] bombZip = BuildZip(
                ("bomb-mod/part1.dat", zero15Mb),
                ("bomb-mod/part2.dat", zero15Mb),
                ("bomb-mod/part3.dat", zero15Mb),
                ("bomb-mod/part4.dat", zero15Mb),
                ("bomb-mod/part5.dat", zero15Mb));
            var exBomb = Assert.Throws<TargetInvocationException>(() =>
                InvokeExtract(extractMethod, bombZip, modsRoot, "bomb-mod"));
            Assert.IsType<InvalidDataException>(exBomb.InnerException);
            Assert.False(Directory.Exists(Path.Combine(modsRoot, "bomb-mod")));
            Assert.Empty(Directory.GetDirectories(modsRoot, ".tmp_*"));
        }
        finally
        {
            try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TextureLease_Rebind100Times_DrainAllGenerationGuard_AndLruEvict()
    {
        // 验证为 BazaarLauncher.Modules 顶层类型（不嵌套于引用 Unity 的 CardTextureCache 内）
        Type? poolOpenType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.TextureRefPool`1");
        Type? slotOpenType = _fx.LauncherAssembly.GetType("BazaarLauncher.Modules.TextureLeaseSlot`1");
        Assert.NotNull(poolOpenType);
        Assert.NotNull(slotOpenType);

        Type poolType = poolOpenType!.MakeGenericType(typeof(string));
        Type slotType = slotOpenType!.MakeGenericType(typeof(string));

        object pool = Activator.CreateInstance(poolType)!;
        object slot = Activator.CreateInstance(slotType)!;

        PropertyInfo genProp = poolType.GetProperty("Generation")!;
        PropertyInfo countProp = poolType.GetProperty("Count")!;
        PropertyInfo bytesProp = poolType.GetProperty("TotalEstimatedBytes")!;
        MethodInfo getRefCount = poolType.GetMethod("GetRefCount")!;
        MethodInfo tryAcquire = poolType.GetMethod("TryAcquireExisting")!;
        MethodInfo registerOrAcquire = poolType.GetMethod("RegisterOrAcquire")!;
        MethodInfo evictLru = poolType.GetMethod("EvictLru")!;
        MethodInfo drainAll = poolType.GetMethod("DrainAll")!;
        MethodInfo slotTake = slotType.GetMethod("Take")!;
        MethodInfo slotClear = slotType.GetMethod("Clear")!;

        long gen0 = (long)genProp.GetValue(pool)!;

        // 首次注册条目 A
        object?[] regA = { gen0, "tex-key-a", "ASSET_A", 4096L, null, null };
        registerOrAcquire.Invoke(pool, regA);
        Assert.NotNull(regA[4]);
        Assert.Null(regA[5]);

        object?[] takeArgs = { regA[4] };
        slotTake.Invoke(slot, takeArgs);
        Assert.Null(takeArgs[0]); // 所有权转移后调用方局部变量已清零
        Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-key-a" })!);

        // 连续 100 次同条目 Acquire + 重绑（精确复现原 P0-35 VRAM RefCount 锁死漏洞）
        for (int i = 0; i < 100; i++)
        {
            object?[] acq = { "tex-key-a", null, 0L };
            Assert.True((bool)tryAcquire.Invoke(pool, acq)!);
            object?[] rebind = { acq[1] };
            slotTake.Invoke(slot, rebind);
            Assert.Null(rebind[0]);
            Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-key-a" })!);
        }

        // 并发竞态注册同 key：应返回已有条目新租约并交回冗余资产供锁外销毁
        object?[] raceReg = { gen0, "tex-key-a", "ASSET_A_DUPLICATE", 4096L, null, null };
        registerOrAcquire.Invoke(pool, raceReg);
        Assert.Equal("ASSET_A_DUPLICATE", raceReg[5]);
        Assert.Equal(2, (int)getRefCount.Invoke(pool, new object[] { "tex-key-a" })!);

        // 重复 Dispose 10 次必须 100% 幂等，绝不下溢
        var raceLease = (IDisposable)raceReg[4]!;
        for (int i = 0; i < 10; i++) raceLease.Dispose();
        Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-key-a" })!);

        // 换绑新条目 B，旧条目 A 计数必须精确归 0
        object?[] regB = { gen0, "tex-key-b", "ASSET_B", 8192L, null, null };
        registerOrAcquire.Invoke(pool, regB);
        object?[] takeB = { regB[4] };
        slotTake.Invoke(slot, takeB);
        Assert.Equal(0, (int)getRefCount.Invoke(pool, new object[] { "tex-key-a" })!);
        Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-key-b" })!);

        // Clear 后两者计数均归 0，EvictLru(0) 必须 100% 驱逐全部显存估算字节
        slotClear.Invoke(slot, null);
        Assert.Equal(0, (int)getRefCount.Invoke(pool, new object[] { "tex-key-b" })!);

        var evicted = new List<string>();
        evictLru.Invoke(pool, new object[] { 0L, evicted });
        Assert.Equal(2, evicted.Count);
        Assert.Equal(0, (int)countProp.GetValue(pool)!);
        Assert.Equal(0L, (long)bytesProp.GetValue(pool)!);

        // 跨世代卸载防漏（闭环 #23-1）：DrainAll 递增 _generation 后，旧世代 RegisterOrAcquire 必须被拒收并交回资产销毁
        object?[] regOld = { gen0, "tex-gen", "ASSET_OLD", 2048L, null, null };
        registerOrAcquire.Invoke(pool, regOld);
        var oldLease = (IDisposable)regOld[4]!;

        var drained = new List<string>();
        drainAll.Invoke(pool, new object[] { drained });
        Assert.Single(drained);
        long gen1 = (long)genProp.GetValue(pool)!;
        Assert.True(gen1 > gen0);

        object?[] staleReg = { gen0, "tex-stale", "ASSET_STALE_DECODED", 2048L, null, null };
        registerOrAcquire.Invoke(pool, staleReg);
        Assert.Null(staleReg[4]); // 拒绝入池
        Assert.Equal("ASSET_STALE_DECODED", staleReg[5]); // 原样交回锁外销毁
        Assert.Equal(0, (int)countProp.GetValue(pool)!);

        // 在新世代注册同 key 后，释放旧世代残留租约绝不扣减新世代计数、不下溢为负
        object?[] regNew = { gen1, "tex-gen", "ASSET_NEW", 2048L, null, null };
        registerOrAcquire.Invoke(pool, regNew);
        Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-gen" })!);
        oldLease.Dispose();
        Assert.Equal(1, (int)getRefCount.Invoke(pool, new object[] { "tex-gen" })!);
        ((IDisposable)regNew[4]!).Dispose();
        Assert.Equal(0, (int)getRefCount.Invoke(pool, new object[] { "tex-gen" })!);
    }

    private static object? InvokeExtract(MethodInfo m, byte[] zipBytes, string allowedRoot, string modId)
    {
        var ps = m.GetParameters();
        object?[] args = new object?[ps.Length];
        args[0] = zipBytes;
        args[1] = allowedRoot;
        args[2] = modId;
        for (int i = 3; i < ps.Length; i++)
        {
            args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : Type.Missing;
        }
        return m.Invoke(null, args);
    }

    private static byte[] BuildZip(params (string Name, byte[]? Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
        {
            foreach (var (name, content) in entries)
            {
                var e = za.CreateEntry(name, CompressionLevel.Optimal);
                if (content != null)
                {
                    using var s = e.Open();
                    s.Write(content, 0, content.Length);
                }
            }
        }
        return ms.ToArray();
    }
}

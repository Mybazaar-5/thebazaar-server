using System.Diagnostics;
using System.Reflection;
using BazaarCore.Tests.TestInfrastructure;
using BazaarModSdk;

namespace BazaarCore.Tests;

/// <summary>
/// Wave 3B 客户端 / Hub / ModSdk / 工程卫生跨 TFM 强断言测试套件（闭环 #124-4 与 #128 备忘）：
/// 1. BazaarModSdk 经 ProjectReference 直接强类型测试；
/// 2. BazaarLauncher 仅对 6 个零 Unity 纯 .NET 内核调用 asm.GetType("<FullName>", throwOnError: true)（严禁 asm.GetTypes()）；
/// 3. BazaarHub 经 BazaarHubExecutableFixture 真实运行 --self-test 核验；
/// 4. 工程卫生：Bazaar.sln 9 工程清单、NoWarn、Launcher 默认地址与 sync-github-public.ps1 脱敏验证。
/// </summary>
[Collection("GlobalStaticState")]
public sealed class Wave3B_LauncherHubAndSdkCoverageTests
    : IClassFixture<Wave2CLauncherAssemblyFixture>, IClassFixture<BazaarHubExecutableFixture>
{
    private readonly Wave2CLauncherAssemblyFixture _launcherFx;
    private readonly BazaarHubExecutableFixture _hubFx;

    public Wave3B_LauncherHubAndSdkCoverageTests(
        Wave2CLauncherAssemblyFixture launcherFx,
        BazaarHubExecutableFixture hubFx)
    {
        _launcherFx = launcherFx;
        _hubFx = hubFx;
    }

    [Fact]
    public void D8_P1_08_BazaarModSdk_StrongTypedMetadataAndManifestRoundTrip()
    {
        Assert.False(string.IsNullOrWhiteSpace(BazaarModSdkVersion.Current));

        var attr = new ModMetadataAttribute("community.w3b-sample", "测试扩展", "2.1.0")
        {
            Author = "Tester",
            Description = "SDK 契约直测",
            Dependencies = new[] { "core.base" },
            SupportsHotReload = false,
        };
        var manifest = attr.ToManifest("/tmp/mod");
        Assert.Equal("community.w3b-sample", manifest.Id);
        Assert.Equal("测试扩展", manifest.Name);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Equal("Tester", manifest.Author);
        Assert.Equal(BazaarModSdkVersion.Current, manifest.MinSdkVersion);
        Assert.Single(manifest.Dependencies);
        Assert.False(manifest.SupportsHotReload);
        Assert.Equal("/tmp/mod", manifest.RootDirectory);
        Assert.Equal(ModLifecycleState.Unloaded, default);
    }

    [Fact]
    public void D3_P2_06_Launcher_TextureInFlightCoalescer_CoalescesConcurrentRequests()
    {
        // 严格遵守 #128 备忘：仅使用 GetType("<FullName>", throwOnError: true)，绝不调用 asm.GetTypes()
        Type openType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.Modules.TextureInFlightCoalescer`1", throwOnError: true)!;
        Type closedType = openType.MakeGenericType(typeof(string));
        object coalescer = Activator.CreateInstance(closedType)!;

        MethodInfo acquireMethod = closedType.GetMethod("TryBeginOrJoin", BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo completeMethod = closedType.GetMethod("Complete", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(acquireMethod);
        Assert.NotNull(completeMethod);

        var received = new List<string>();
        bool firstIsLoader = (bool)acquireMethod.Invoke(coalescer, new object[] { "sha_abc", (Action<string>)(v => received.Add("w1:" + v)) })!;
        bool secondIsLoader = (bool)acquireMethod.Invoke(coalescer, new object[] { "sha_abc", (Action<string>)(v => received.Add("w2:" + v)) })!;

        Assert.True(firstIsLoader, "首个请求必须成为唯一读盘加载者");
        Assert.False(secondIsLoader, "同键并发第二请求必须合并挂接，避免重复读盘");

        int factoryCalls = 0;
        completeMethod.Invoke(coalescer, new object[] { "sha_abc", (Func<string>)(() => $"lease_{++factoryCalls}") });
        Assert.Equal(2, received.Count);
        Assert.Equal(2, factoryCalls);
        Assert.Contains("w1:lease_1", received);
        Assert.Contains("w2:lease_2", received);

        // 异常/失败兜底 Complete(null) 后同 key 必须自动解锁，可再次加载；Clear() 清空等待表不留死锁
        bool retryLoader = (bool)acquireMethod.Invoke(coalescer, new object[] { "sha_abc", (Action<string>)(v => received.Add("fail:" + (v ?? "null"))) })!;
        Assert.True(retryLoader);
        completeMethod.Invoke(coalescer, new object[] { "sha_abc", (Func<string?>)(() => null) });
        Assert.Contains("fail:null", received);

        bool afterFailLoader = (bool)acquireMethod.Invoke(coalescer, new object[] { "sha_abc", (Action<string>)(_ => { }) })!;
        Assert.True(afterFailLoader);
        MethodInfo clearMethod = closedType.GetMethod("Clear", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(clearMethod);
        clearMethod.Invoke(coalescer, null);
        bool afterClearLoader = (bool)acquireMethod.Invoke(coalescer, new object[] { "sha_abc", (Action<string>)(_ => { }) })!;
        Assert.True(afterClearLoader);
    }

    [Fact]
    public void D4_P2_1_Launcher_HarmonyOverloadSelector_IsDeterministicAcrossOverloads()
    {
        Type selectorType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.Core.HarmonyOverloadSelector", throwOnError: true)!;
        MethodInfo selectMethod = selectorType.GetMethod("SelectDeterministic", BindingFlags.Public | BindingFlags.Static)!;
        Assert.NotNull(selectMethod);

        var candidates = typeof(string).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == nameof(string.IndexOf))
            .ToArray();
        var reversed = candidates.Reverse().ToArray();

        var pick1 = selectMethod.Invoke(null, new object?[] { candidates }) as MethodInfo;
        var pick2 = selectMethod.Invoke(null, new object?[] { reversed }) as MethodInfo;
        Assert.NotNull(pick1);
        Assert.Same(pick1, pick2);
    }

    [Fact]
    public void D4_P2_4_Launcher_CloudGuardUrlFilterCore_BlocksTelemetryAndAllowsLocal()
    {
        Type filterType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.CloudGuardUrlFilterCore", throwOnError: true)!;
        MethodInfo shouldBlock = filterType.GetMethod("ShouldBlockUrl", BindingFlags.Public | BindingFlags.Static)!;
        Assert.NotNull(shouldBlock);

        Assert.False((bool)shouldBlock.Invoke(null, new object?[] { "http://127.0.0.1:8080/api/ping", "http://127.0.0.1:8080" })!);
        Assert.True((bool)shouldBlock.Invoke(null, new object?[] { "https://telemetry.playthebazaar.com/v1/events", "http://127.0.0.1:8080" })!);
        Assert.True((bool)shouldBlock.Invoke(null, new object?[] { "https://api.sentry.io/api/1/envelope/", "http://127.0.0.1:8080" })!);
    }

    [Fact]
    public void D4_P2_6_Launcher_DiagLogThrottleCore_ThrottlesRepeatedTagsAndCounts()
    {
        Type throttleType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.Core.DiagLogThrottleCore", throwOnError: true)!;
        object throttle = Activator.CreateInstance(throttleType, new object[] { 60_000L })!;
        MethodInfo record = throttleType.GetMethod("ShouldEmit", BindingFlags.Public | BindingFlags.Instance)!;
        PropertyInfo totalProp = throttleType.GetProperty("TotalSwallowedCount", BindingFlags.Public | BindingFlags.Instance)!;
        Assert.NotNull(record);
        Assert.NotNull(totalProp);

        Assert.True((bool)record.Invoke(throttle, new object[] { "tag.a", 1000L })!);
        Assert.False((bool)record.Invoke(throttle, new object[] { "tag.a", 5000L })!);
        Assert.True((bool)record.Invoke(throttle, new object[] { "tag.a", 62_000L })!);
        Assert.Equal(3L, (long)totalProp.GetValue(throttle)!);
    }

    [Fact]
    public void D4_P2_7_Launcher_NoSteamTicketFallbackCore_CreatesZeroedStructWithoutDefaultCtor()
    {
        Type fallbackType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.NoSteamTicketFallbackCore", throwOnError: true)!;
        MethodInfo createMethod = fallbackType.GetMethod("TryCreateFallbackValue", BindingFlags.Public | BindingFlags.Static)!;
        Assert.NotNull(createMethod);

        object?[] args = { typeof(Guid), null };
        bool ok = (bool)createMethod.Invoke(null, args)!;
        Assert.True(ok);
        Assert.IsType<Guid>(args[1]);
    }

    [Fact]
    public void D6_P2_07_Launcher_LobbyMemberRulesSummaryFormatter_IncludesWave23BannedAndMods()
    {
        Type fmtType = _launcherFx.LauncherAssembly.GetType(
            "BazaarLauncher.LobbyMemberRulesSummaryFormatter", throwOnError: true)!;
        MethodInfo formatMethod = fmtType.GetMethod("FormatSummary", BindingFlags.Public | BindingFlags.Static)!;
        Assert.NotNull(formatMethod);

        string summary = (string)formatMethod.Invoke(null, new object?[]
        {
            "BossRush",
            "ExtremeRoguelike",
            new[] { "turbo_frenzy" },
            new[] { "enc_ban_1", "enc_ban_2" },
            new[] { ("mod_alpha", "已同步"), ("mod_beta", "待同步") },
            2.0,
            1.5,
        })!;

        Assert.Contains("BossRush", summary, StringComparison.Ordinal);
        Assert.Contains("turbo_frenzy", summary, StringComparison.Ordinal);
        Assert.Contains("2", summary, StringComparison.Ordinal);
        Assert.Contains("mod_alpha", summary, StringComparison.Ordinal);
        Assert.Contains("mod_beta", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void D7_P2_02_05_BazaarHub_SelfTest_PassesPrivacyRedactionAndAtomicFileWriter()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.True(File.Exists(_hubFx.HubExePath));

        var psi = new ProcessStartInfo(_hubFx.HubExePath, "--self-test")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)!;
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30_000);

        Assert.True(proc.ExitCode == 0, $"BazaarHub --self-test 失败 (ExitCode={proc.ExitCode}):\n{stdout}\n{stderr}");
        Assert.Contains("PrivacyRedaction", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AtomicFileWriter", stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void D7_P2_03_D4_P2_6_D8_P2_02_EngineeringAndSanitizationContractsHold()
    {
        string repoRoot = TestDataLocator.ResolveRepoRoot();

        // 1. Bazaar.sln 必须收录全部 9 个正式工程
        string sln = File.ReadAllText(Path.Combine(repoRoot, "Bazaar.sln"));
        string[] requiredProjects =
        {
            "BazaarCore.csproj",
            "BazaarServer.csproj",
            "BazaarModSdk.csproj",
            "BazaarLauncher.csproj",
            "BazaarHub.csproj",
            "BazaarCli.csproj",
            "BazaarDatasetGen.csproj",
            "BazaarProbe.csproj",
            "BazaarCore.Tests.csproj",
        };
        foreach (string proj in requiredProjects)
            Assert.Contains(proj, sln, StringComparison.OrdinalIgnoreCase);

        // 2. BazaarServer.csproj 不得压制 CS0219
        string serverCsproj = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarServer", "BazaarServer.csproj"));
        Assert.DoesNotContain("CS0219", serverCsproj, StringComparison.OrdinalIgnoreCase);

        // 3. BazaarLauncherPlugin.cs 必须默认绑定 127.0.0.1:8080 且不得硬编码任何非回环公网 IPv4
        string pluginSrc = File.ReadAllText(Path.Combine(repoRoot, "src", "BazaarLauncher", "BazaarLauncherPlugin.cs"));
        Assert.Contains("http://127.0.0.1:8080", pluginSrc, StringComparison.Ordinal);
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(pluginSrc, @"(?<![0-9.])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?![0-9.])"))
        {
            int a = int.Parse(m.Groups[1].Value);
            int b = int.Parse(m.Groups[2].Value);
            int c = int.Parse(m.Groups[3].Value);
            bool allowed = a == 0 || a == 127 || a == 10
                || (a == 172 && b >= 16 && b <= 31)
                || (a == 192 && b == 168)
                || (a == 192 && b == 0 && c == 2)
                || (a == 198 && b == 51 && c == 100)
                || (a == 203 && b == 0 && c == 113);
            Assert.True(allowed, $"BazaarLauncherPlugin.cs 残留非回环公网 IPv4: {m.Value}");
        }

        // 4. sync-github-public.ps1 Scrub-Text 必须覆盖独立 "自建云服务器" 与 "PresetRemote"
        string syncScript = File.ReadAllText(Path.Combine(repoRoot, "tools", "sync-github-public.ps1"));
        Assert.Contains("自建云服务器", syncScript, StringComparison.Ordinal);
        Assert.Contains("Get-ChildItem -Directory", syncScript, StringComparison.Ordinal);
    }
}

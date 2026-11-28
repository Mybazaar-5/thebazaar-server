using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 无头自动化自检运行器（供 BazaarHub.exe --self-test 与 --verify-client-update-sync 调用，不创建任何 GUI 窗口）。
/// 在隔离的临时沙盒目录内逐项验证 9 项核心服务与 P0-34/45/46/47 契约回归，全部通过返回 0，任一失败返回 1。
/// </summary>
public static class SelfTestRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<int> RunAsync()
    {
        string sandboxRoot = Path.Combine(Path.GetTempPath(), $"BazaarHub_SelfTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxRoot);

        int passed = 0;
        int failed = 0;

        Console.WriteLine("==================================================================");
        Console.WriteLine("  BazaarHub 无头自动化自检 (--self-test · 14 项契约回归)");
        Console.WriteLine($"  沙盒目录：{sandboxRoot}");
        Console.WriteLine("==================================================================");

        async Task RunCase(string title, Func<Task> body)
        {
            try
            {
                await body();
                passed++;
                Console.WriteLine($"[PASS] {title}");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"[FAIL] {title}: {ex.Message}");
            }
        }

        try
        {
            await RunCase("1. GameLocatorService 目录体检与 Steam VDF 解析", () => TestGameLocatorAsync(sandboxRoot));
            await RunCase("2. LauncherConfigService 无损读写 (.cfg) + 无 BOM player-name.txt + 防撞号昵称", () => TestLauncherConfigAsync(sandboxRoot));
            await RunCase("3. UpdateSyncService SHA-256 增量同步 + 原子替换 + 防篡改拦截 + 断网优雅降级", () => TestUpdateSyncAsync(sandboxRoot));
            await RunCase("4. PerformanceTunerService 首次备份 → 5项调优 → 不覆盖原备份 → 逐字节还原 round-trip", () => TestPerformanceTunerRoundTripAsync(sandboxRoot));
            await RunCase("5. DiagnosticsExportService 诊断包 ZIP 打包完整性", () => TestDiagnosticsExportAsync(sandboxRoot));
            await RunCase("6. [P0-45] UpdateSyncService 新旧服务端 .cfg 硬保护 + 引导根目录路由 + 过滤调试文档", () => TestClientUpdatePolicyAndRoutingAsync(sandboxRoot));
            await RunCase("7. [P0-46] HubPresetLoader 外置预设防回环碰撞 + InferServerMode 8080/9090 端口边界", () => TestHubPresetsAndServerModeInferenceAsync(sandboxRoot));
            await RunCase("8. [P0-47] 进程锁/只读属性预检 + 首次建目录 + 两阶段原子事务回滚 + LaunchSyncGate", () => TestAtomicTransactionAndLaunchGateAsync(sandboxRoot));
            await RunCase("9. [P0-34] CommunityModClient 路径穿越拦截 + Windows反斜杠Zip + 危险扩展名拒收 + Zip Bomb", () => TestCommunityModSecurityGuardsAsync(sandboxRoot));
            await RunCase("10. [D7-P1-05/P0-28] SemVer MinRequired 阻断 + Hub 升级提示 + 防旧服降级覆盖高版 DLL + WebP 优雅跳过", () => TestSemVerGatesAndDowngradeProtectionAsync(sandboxRoot));
            await RunCase("11. [D7-P1-03] 单机端口占用非 BazaarServer 冲突阻断 + /healthz JSON 指纹 + Job Object 生命周期", () => TestStandaloneHostHealthzAndJobObjectAsync());
            await RunCase("12. [D7-P1-01/02] 统一版本号动态读取 + CAS 资产池共享路径契约校验", () => TestUnifiedVersionAndCasPoolAsync(sandboxRoot));
            await RunCase("13. [D7-P2-02] PrivacyRedaction 诊断导出隐私脱敏（MachineName/UserName/Token/路径/URL凭证）", () => TestDiagnosticsPrivacyRedactionAsync(sandboxRoot));
            await RunCase("14. [D7-P2-05] AtomicFileWriter 唯一随机临时文件名 + 异常自动清理 + 原子落盘", () => TestAtomicFileWriterAsync(sandboxRoot));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandboxRoot))
                {
                    ClearReadOnlyAttributes(sandboxRoot);
                    Directory.Delete(sandboxRoot, recursive: true);
                }
            }
            catch
            {
                // 忽略临时目录清理占用
            }
        }

        Console.WriteLine("------------------------------------------------------------------");
        Console.WriteLine($"自检完成：通过 {passed} 项，失败 {failed} 项。");
        Console.WriteLine("==================================================================");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 无头端到端联调：对接真实运行中的服务端执行一次增量热更新，核验成功无核心失败后退出。
    /// </summary>
    public static async Task<int> VerifyClientUpdateSyncAsync(string serverUrl, string gameDir)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(gameDir))
        {
            Console.Error.WriteLine("[FAIL] 缺少有效 serverUrl 或 gameDir。");
            return 2;
        }

        using var sync = new UpdateSyncService();
        UpdateSyncReport report = await sync.SyncAsync(
            gameDir,
            serverUrl,
            applyDownloads: true,
            progress: null,
            log: Console.WriteLine,
            isGameRunningProbe: () => false,
            beforeCommitMoveHook: null);

        foreach (SyncItemStatus item in report.Items)
        {
            Console.WriteLine($"[ITEM] [{item.Action}] {item.TargetRelativePath} :: {item.Detail}");
        }

        if (!report.Success || report.HasCorePluginFailure || report.BlockedByRunningGame)
        {
            Console.Error.WriteLine($"[FAIL] 客户端更新联调未通过：{report.SummaryMessage}");
            foreach (string err in report.Errors)
            {
                Console.Error.WriteLine($"  - {err}");
            }
            return 1;
        }

        Console.WriteLine($"[PASS] 客户端更新联调通过：{report.SummaryMessage}");
        return 0;
    }

    private static Task TestGameLocatorAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case1_game");
        Directory.CreateDirectory(Path.Combine(gameDir, "TheBazaar_Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "core"));
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "plugins"));

        File.WriteAllText(Path.Combine(gameDir, "TheBazaar.exe"), "stub", Utf8NoBom);
        File.WriteAllText(Path.Combine(gameDir, "TheBazaar_Data", "Managed", "Assembly-CSharp.dll"), "stub", Utf8NoBom);
        File.WriteAllText(Path.Combine(gameDir, "winhttp.dll"), "stub", Utf8NoBom);
        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "core", "BepInEx.dll"), "stub", Utf8NoBom);
        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarLauncher.dll"), "stub", Utf8NoBom);

        var locator = new GameLocatorService();
        // D7-P1-07 反向门禁 1：缺 doorstop_config.ini 与 BazaarModSdk.dll 时必须判定未就绪
        GameInstallInfo missingBoth = locator.InspectGameDirectory(gameDir, "沙盒模拟");
        Assert(missingBoth.IsValidGameInstall, "应识别有效游戏本体");
        Assert(!missingBoth.HasDoorstopConfig && !missingBoth.HasBazaarModSdk, "缺失 doorstop_config.ini 与 BazaarModSdk.dll 标志应为 false");
        Assert(!missingBoth.IsBepInExReady, "缺 doorstop_config.ini/BazaarModSdk.dll 时 IsBepInExReady 必须为 false");

        // D7-P1-07 反向门禁 2：仅有 doorstop_config.ini 但缺 BazaarModSdk.dll 仍为未就绪
        File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), "[General]\nenabled=true\n", Utf8NoBom);
        GameInstallInfo missingSdk = locator.InspectGameDirectory(gameDir, "沙盒模拟");
        Assert(missingSdk.HasDoorstopConfig && !missingSdk.HasBazaarModSdk, "仅缺 BazaarModSdk.dll 标志校验异常");
        Assert(!missingSdk.IsBepInExReady, "缺 BazaarModSdk.dll 时 IsBepInExReady 必须为 false");

        // 补齐 BazaarModSdk.dll 后全部就绪
        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "plugins", "BazaarModSdk.dll"), "stub", Utf8NoBom);
        GameInstallInfo info = locator.InspectGameDirectory(gameDir, "沙盒模拟");
        Assert(info.IsValidGameInstall && info.HasDoorstopConfig && info.HasBazaarModSdk && info.IsBepInExReady,
            "补齐 doorstop_config.ini 与 BazaarModSdk.dll 后应识别 BepInEx 与插件全部就绪");

        const string sampleVdf = """
"libraryfolders"
{
	"0"
	{
		"path"		"C:\\Program Files (x86)\\Steam"
	}
	"1"
	{
		"path"		"D:\\SteamLibrary"
	}
}
""";
        List<string> libs = GameLocatorService.ParseLibraryFoldersVdfContent(sampleVdf);
        Assert(libs.Count == 2, $"VDF 应解析出 2 个库路径，实际为 {libs.Count}");
        Assert(libs.Contains(@"D:\SteamLibrary", StringComparer.OrdinalIgnoreCase), "VDF 应包含 D:\\SteamLibrary");

        return Task.CompletedTask;
    }

    private static Task TestLauncherConfigAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case2_cfg");
        string cfgPath = LauncherConfigService.GetLauncherConfigPath(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);

        const string sampleRemoteUrl = "http://203.0.113.10:8080";
        string originalCfg = $"""
## Settings file was created by plugin BazaarLauncher v{LauncherConstants.HubVersion}
## 重要自定义头部注释：绝不能丢失！

[CustomGameplay]
# 未知段落必须完整保留
MigratedV12_1Defaults = true
SecretModFlag = keep_me_exact_42

[General]

## 总开关注释
# Setting type: Boolean
Enabled = true

## 自建服基址注释
ServerBase = http://203.0.113.10:8080

## 玩家名注释
PlayerName = OldCfgName

BypassSteam = true
UnlockEverything = true

[UnknownFutureSection]
# 未来版本新增的未知字段
CustomRuleJsonHash = abcdef123456
""";
        File.WriteAllText(cfgPath, originalCfg, Utf8NoBom);

        var preset = new HubPresetConfig(
            hasPresetRemote: true,
            presetRemoteUrl: sampleRemoteUrl,
            presetRemoteName: "示例远程自建服",
            presetRemoteShortName: "远程服",
            presetRemoteSubtitle: "203.0.113.10:8080");
        var svc = new LauncherConfigService();

        // 防撞号昵称校验
        string autoName = LauncherConfigService.GenerateCollisionSafePlayerName("WIN-DEV01", "Administrator");
        Assert(autoName == "WIN-DEV01_Administrator", $"防撞号昵称格式异常：{autoName}");
        string fallbackFromGeneric = LauncherConfigService.ResolveOrGeneratePlayerName("Player");
        Assert(fallbackFromGeneric.Contains('_'), $"泛用名 Player 应自动转为 机器名_用户名，实际：{fallbackFromGeneric}");

        // 切换 1：本地单机 + 中文昵称
        LauncherConfigSnapshot snapLocal = svc.ApplyServerAndPlayerSettings(
            gameDir,
            ServerMode.LocalStandalone,
            null,
            "星港大巴扎_测试员");
        Assert(snapLocal.Enabled, "单机模式 Enabled 应为 true");
        Assert(snapLocal.ServerBase == LauncherConstants.LocalStandaloneUrl, "单机模式 ServerBase 应为 127.0.0.1:8080");
        Assert(snapLocal.EffectivePlayerName == "星港大巴扎_测试员", $"中文昵称读写异常：{snapLocal.EffectivePlayerName}");

        // 校验 player-name.txt 严格无 BOM UTF-8
        byte[] nameBytes = File.ReadAllBytes(LauncherConfigService.GetPlayerNameFilePath(gameDir));
        Assert(!(nameBytes.Length >= 3 && nameBytes[0] == 0xEF && nameBytes[1] == 0xBB && nameBytes[2] == 0xBF),
            "player-name.txt 不得包含 UTF-8 BOM");
        Assert(Encoding.UTF8.GetString(nameBytes) == "星港大巴扎_测试员", "player-name.txt 内容不匹配");

        // 切换 2：自定义服 -> 纯净官方原版 -> 预设远程服
        LauncherConfigSnapshot snapCustom = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.CustomServer, "10.0.0.8:9090/", "星港大巴扎_测试员");
        Assert(snapCustom.ServerBase == "http://10.0.0.8:9090", $"自定义地址规范化失败：{snapCustom.ServerBase}");

        LauncherConfigSnapshot snapOfficial = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.PureOfficial, null, "星港大巴扎_测试员");
        Assert(!snapOfficial.Enabled, "纯净官方原版 Enabled 应为 false");
        Assert(snapOfficial.InferServerMode(preset) == ServerMode.PureOfficial, "模式推断应为 PureOfficial");

        LauncherConfigSnapshot snapPreset = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.PresetRemote, null, "星港大巴扎_测试员", preset: preset);
        Assert(snapPreset.Enabled && snapPreset.ServerBase == sampleRemoteUrl, "切回外置预设远程服失败");
        Assert(snapPreset.InferServerMode(preset) == ServerMode.PresetRemote, "外置预设远程服模式推断失败");

        // 校验所有原始注释与未知段落 100% 保留
        string finalRaw = File.ReadAllText(cfgPath, Encoding.UTF8);
        Assert(finalRaw.Contains("## 重要自定义头部注释：绝不能丢失！", StringComparison.Ordinal), "头部注释丢失");
        Assert(finalRaw.Contains("[CustomGameplay]", StringComparison.Ordinal), "未知段落 CustomGameplay 丢失");
        Assert(finalRaw.Contains("SecretModFlag = keep_me_exact_42", StringComparison.Ordinal), "未知段落键值丢失");
        Assert(finalRaw.Contains("[UnknownFutureSection]", StringComparison.Ordinal), "未知段落 UnknownFutureSection 丢失");
        Assert(finalRaw.Contains("CustomRuleJsonHash = abcdef123456", StringComparison.Ordinal), "未来字段丢失");
        Assert(finalRaw.Contains("## 总开关注释", StringComparison.Ordinal), "字段注释丢失");

        return Task.CompletedTask;
    }

    private static async Task TestUpdateSyncAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case3_sync");
        Directory.CreateDirectory(gameDir);

        byte[] pluginBytes = Utf8NoBom.GetBytes("FAKE_BAZAAR_LAUNCHER_DLL_V2_1_0_PAYLOAD");
        string pluginSha = UpdateSyncService.ComputeBytesSha256(pluginBytes);

        byte[] modPngBytes = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03, 0x04 };
        string modPngSha = UpdateSyncService.ComputeBytesSha256(modPngBytes);

        byte[] overlayBytes = Utf8NoBom.GetBytes("{\"schemaVersion\":1,\"revision\":\"2026.11.21-test\",\"entryCount\":2,\"uiExact\":{\"global\":{\"Test\":\"测试\"}}}");
        string overlaySha = UpdateSyncService.ComputeBytesSha256(overlayBytes);

        string tamperedSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        bool serveTamperedAsset = false;

        await RunMockHttpServerAsync(async (path, ctx) =>
        {
            if (path == "/healthz")
            {
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{\"ok\":true,\"uptime\":123}"));
            }
            else if (path == "/api/ping")
            {
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{\"ok\":true,\"onlinePlayers\":7,\"serverVersion\":\"13.0.0\"}"));
            }
            else if (path == "/api/client-update/manifest")
            {
                var manifestObj = new
                {
                    schemaVersion = 2,
                    channel = "stable",
                    launcherVersion = "2.1.0",
                    localizationVersion = "2026.11.21-test",
                    files = new object[]
                    {
                        new
                        {
                            id = "bazaar-launcher",
                            name = "BazaarLauncher.dll",
                            category = "plugin",
                            version = "2.1.0",
                            targetPath = "BepInEx/plugins/BazaarLauncher.dll",
                            updatePolicy = "always",
                            atomicGroup = "core-plugins",
                            downloadUrl = "/downloads/BazaarLauncher.dll",
                            sha256 = pluginSha,
                            sizeBytes = pluginBytes.Length
                        }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifestObj)));
            }
            else if (path == "/downloads/BazaarLauncher.dll")
            {
                await WriteResponseAsync(ctx, 200, "application/octet-stream", pluginBytes);
            }
            else if (path == "/api/mods/manifest")
            {
                var assets = new List<object>
                {
                    new
                    {
                        modId = "bazaar-chronicles-sample",
                        logicalPath = "assets/ember_anvil.png",
                        sha256 = modPngSha,
                        bytes = modPngBytes.Length,
                        width = 512,
                        height = 512
                    }
                };
                if (serveTamperedAsset)
                {
                    assets.Add(new
                    {
                        modId = "bazaar-chronicles-sample",
                        logicalPath = "assets/corrupted.png",
                        sha256 = tamperedSha,
                        bytes = 16,
                        width = 64,
                        height = 64
                    });
                }

                var modsObj = new
                {
                    manifestHash = "mhash-01",
                    mods = new object[]
                    {
                        new
                        {
                            id = "bazaar-chronicles-sample",
                            displayNameZh = "星港奇物志",
                            version = "1.0.0",
                            scope = "multiplayer-safe",
                            assets
                        }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(modsObj)));
            }
            else if (path == $"/api/mods/assets/{modPngSha}")
            {
                await WriteResponseAsync(ctx, 200, "image/png", modPngBytes);
            }
            else if (path == $"/api/mods/assets/{tamperedSha}")
            {
                await WriteResponseAsync(ctx, 200, "image/png", Utf8NoBom.GetBytes("TAMPERED_WRONG_BYTES"));
            }
            else if (path == "/api/localization/overlay")
            {
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", overlayBytes);
            }
            else
            {
                await WriteResponseAsync(ctx, 404, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{\"error\":\"not found\"}"));
            }
        }, async baseUrl =>
        {
            using var probe = new ServerProbeService();
            ServerProbeResult probeRes = await probe.ProbeAsync(baseUrl);
            Assert(probeRes.IsOnline && probeRes.HealthzOk && probeRes.PingEndpointSupported, "模拟服探针应全部通过");
            Assert(probeRes.OnlinePlayers == 7, "在线人数解析应为 7");

            using var sync = new UpdateSyncService();

            // 第一次全量增量同步（应下载 3 项：DLL + 汉化 + Mod PNG）
            UpdateSyncReport first = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(first.Success, $"首次同步应成功：{first.SummaryMessage}");
            Assert(first.DownloadedCount == 3, $"首次应下载 3 项，实际 {first.DownloadedCount}");
            Assert(first.SkippedUpToDateCount == 0, $"首次跳过应为 0，实际 {first.SkippedUpToDateCount}");

            string localPlugin = Path.Combine(gameDir, @"BepInEx\plugins\BazaarLauncher.dll");
            string localOverlay = Path.Combine(gameDir, LauncherConstants.ZhOverlayRelativePath);
            string localAsset = Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "assets", modPngSha + ".png");

            Assert(UpdateSyncService.ComputeFileSha256(localPlugin) == pluginSha, "同步后的插件 SHA-256 不一致");
            Assert(UpdateSyncService.ComputeFileSha256(localOverlay) == overlaySha, "同步后的汉化词典 SHA-256 不一致");
            Assert(UpdateSyncService.ComputeFileSha256(localAsset) == modPngSha, "同步后的 Mod PNG SHA-256 不一致");
            Assert(!Directory.EnumerateFiles(gameDir, "*.tmp*", SearchOption.AllDirectories).Any(), "同步完成后不应残留 .tmp 文件");

            // 第二次同步（应全部命中 SHA-256 缓存，0 下载、3 跳过）
            UpdateSyncReport second = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(second.Success, "二次增量同步应成功");
            Assert(second.DownloadedCount == 0, $"二次应下载 0 项，实际 {second.DownloadedCount}");
            Assert(second.SkippedUpToDateCount == 3, $"二次应跳过 3 项，实际 {second.SkippedUpToDateCount}");

            // 第三次同步：注入篡改资产，验证 SHA-256 防护拒绝写入且清理临时文件
            serveTamperedAsset = true;
            UpdateSyncReport third = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(!third.Success && third.FailedCount == 1, "篡改资产必须被 SHA-256 校验拦截并记为失败");
            string corruptedTarget = Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "assets", tamperedSha + ".png");
            Assert(!File.Exists(corruptedTarget), "被篡改资产绝不应落盘");
            Assert(!Directory.EnumerateFiles(gameDir, "*.tmp*", SearchOption.AllDirectories).Any(), "被篡改资产的 .tmp 必须自动清理");

            // 第四次同步：断网不可达端口优雅降级
            int closedPort = GetFreeTcpPort();
            using var fastHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var offlineSync = new UpdateSyncService(fastHttp);
            UpdateSyncReport offline = await offlineSync.SyncAsync(gameDir, $"http://127.0.0.1:{closedPort}", applyDownloads: true, null, null, () => false, null);
            Assert(offline.Success && offline.DegradedOffline, "断网不可达时应返回优雅降级状态而非崩退");
        });
    }

    private static async Task TestPerformanceTunerRoundTripAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case4_perf");
        Directory.CreateDirectory(Path.Combine(gameDir, @"TheBazaar_Data\StreamingAssets\aa"));
        Directory.CreateDirectory(Path.Combine(gameDir, @"BepInEx\config"));

        string bootPath = Path.Combine(gameDir, LauncherConstants.BootConfigRelativePath);
        string aaPath = Path.Combine(gameDir, LauncherConstants.AddressablesSettingsRelativePath);
        string bepPath = Path.Combine(gameDir, LauncherConstants.BepInExCfgRelativePath);
        string launcherPath = Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath);

        string origBootText =
            "gfx-enable-gfx-jobs=1\r\n" +
            "gfx-threading-mode=6\r\n" +
            "wait-for-native-debugger=0\r\n" +
            "hdr-display-enabled=0\r\n" +
            "gc-max-time-slice=3\r\n" +
            "single-instance=\r\n" +
            "build-guid=cf8293a098d641d5b969fe646236cb04\r\n";

        string origAaText =
            "{\"m_buildTarget\":\"StandaloneWindows64\",\"m_LogResourceManagerExceptions\":true,\"m_DisableCatalogUpdateOnStart\":false,\"m_AddressablesVersion\":\"2.10.3\"}";

        string origBepText =
            "[Logging]\r\n## 原版 Unity 日志监听注释\r\nUnityLogListening = true\r\n\r\n" +
            "[Logging.Console]\r\nEnabled = true\r\n\r\n" +
            "[Logging.Disk]\r\nEnabled = true\r\nLogLevels = Fatal, Error, Warning, Message, Info\r\n";

        string origLauncherText =
            "## 原版 BazaarLauncher 头部注释\r\n[General]\r\nEnabled = true\r\nServerBase = http://127.0.0.1:8080\r\n\r\n" +
            "[Performance]\r\nMaxForegroundFps = 60\r\nBackgroundFps = 20\r\nLazyLoadCosmetics = true\r\n";

        File.WriteAllText(bootPath, origBootText, Utf8NoBom);
        File.WriteAllText(aaPath, origAaText, Utf8NoBom);
        File.WriteAllText(bepPath, origBepText, Utf8NoBom);
        File.WriteAllText(launcherPath, origLauncherText, Utf8NoBom);

        byte[] origBootBytes = File.ReadAllBytes(bootPath);
        byte[] origAaBytes = File.ReadAllBytes(aaPath);
        byte[] origBepBytes = File.ReadAllBytes(bepPath);
        byte[] origLauncherBytes = File.ReadAllBytes(launcherPath);

        var tuner = new PerformanceTunerService();
        PerformanceTuningOptions opt1 = PerformanceTuningOptions.CreateExtremeLowSpec();
        opt1.MaxForegroundFps = 90;
        opt1.BackgroundFps = 15;
        opt1.ReduceHighConcurrencyVfx = true;

        PerformanceOperationResult applyRes = await tuner.ApplyAsync(gameDir, opt1);
        Assert(applyRes.Success, $"首次性能调优应成功：{applyRes.Summary}");
        Assert(applyRes.BackedUpFiles.Count == 4, $"应首次备份 4 份原版文件，实际 {applyRes.BackedUpFiles.Count}");

        string tunedBoot = File.ReadAllText(bootPath, Encoding.UTF8);
        Assert(tunedBoot.Contains("gfx-enable-native-gfx-jobs=1", StringComparison.Ordinal), "boot.config 缺少 native-gfx-jobs");
        Assert(tunedBoot.Contains("gc-max-time-slice=2", StringComparison.Ordinal), "boot.config 缺少 gc-max-time-slice=2");
        Assert(tunedBoot.Contains("job-worker-count=", StringComparison.Ordinal), "boot.config 缺少 job-worker-count");
        Assert(tunedBoot.Contains("build-guid=cf8293a098d641d5b969fe646236cb04", StringComparison.Ordinal), "boot.config 丢失原版 build-guid");

        using (JsonDocument aaDoc = JsonDocument.Parse(File.ReadAllText(aaPath, Encoding.UTF8)))
        {
            Assert(aaDoc.RootElement.GetProperty("m_DisableCatalogUpdateOnStart").GetBoolean(), "aa settings 未禁用启动 Catalog 更新");
            Assert(!aaDoc.RootElement.GetProperty("m_LogResourceManagerExceptions").GetBoolean(), "aa settings 未关闭资源异常日志");
        }

        IniDocument tunedBep = IniDocument.LoadOrEmpty(bepPath);
        Assert(tunedBep.GetValue("Logging.Console", "Enabled") == "false", "BepInEx.cfg 控制台应关闭");
        Assert(tunedBep.GetValue("Logging.Disk", "LogLevels") == "Fatal, Error", "BepInEx.cfg 日志级别应为 Fatal, Error");
        Assert(tunedBep.GetValue("Logging", "UnityLogListening") == "false", "BepInEx.cfg UnityLogListening 应关闭");
        Assert(File.ReadAllText(bepPath, Encoding.UTF8).Contains("## 原版 Unity 日志监听注释", StringComparison.Ordinal), "BepInEx.cfg 注释丢失");

        IniDocument tunedLauncher = IniDocument.LoadOrEmpty(launcherPath);
        Assert(tunedLauncher.GetInt("Performance", "MaxForegroundFps", 0) == 90, "MaxForegroundFps 应为 90");
        Assert(tunedLauncher.GetInt("Performance", "BackgroundFps", 0) == 15, "BackgroundFps 应为 15");
        Assert(tunedLauncher.GetBool("Performance", "ReduceHighConcurrencyVfx", false), "ReduceHighConcurrencyVfx 应为 true");

        string argsPreview = PerformanceTunerService.BuildFullLaunchArgsPreview(opt1);
        Assert(argsPreview.Contains("-high", StringComparison.Ordinal) &&
               argsPreview.Contains("-nolog", StringComparison.Ordinal) &&
               argsPreview.Contains("-window-mode borderless", StringComparison.Ordinal),
            $"启动参数组合异常：{argsPreview}");

        string bootBackupPath = Path.Combine(PerformanceTunerService.GetOriginalBackupDir(gameDir), LauncherConstants.BootConfigRelativePath);
        string backupShaBefore = UpdateSyncService.ComputeFileSha256(bootBackupPath);

        opt1.GcMaxTimeSliceMs = 4;
        PerformanceOperationResult applyRes2 = await tuner.ApplyAsync(gameDir, opt1);
        Assert(applyRes2.Success && applyRes2.BackedUpFiles.Count == 0, "二次调优不得重复覆盖原版备份");
        Assert(UpdateSyncService.ComputeFileSha256(bootBackupPath) == backupShaBefore, "原版备份哈希被非法篡改");

        PerformanceOperationResult restoreOnly = await tuner.RestoreOriginalFilesOnlyAsync(gameDir);
        Assert(restoreOnly.Success && restoreOnly.RestoredFiles.Count == 4, "应还原全部 4 份原始文件");
        Assert(File.ReadAllBytes(bootPath).SequenceEqual(origBootBytes), "boot.config 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(aaPath).SequenceEqual(origAaBytes), "aa/settings.json 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(bepPath).SequenceEqual(origBepBytes), "BepInEx.cfg 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(launcherPath).SequenceEqual(origLauncherBytes), "local.thebazaar.bazaarlauncher.cfg 还原后与原版字节不一致");

        PerformanceOperationResult pureRes = await tuner.RestorePureOfficialAsync(gameDir, disableBazaarLauncher: true);
        Assert(pureRes.Success, "RestorePureOfficialAsync 应成功");
        Assert(!IniDocument.LoadOrEmpty(launcherPath).GetBool("General", "Enabled", true), "纯净官方原版后 Enabled 应为 false");
    }

    private static async Task TestDiagnosticsExportAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case5_diag");
        string unityDir = Path.Combine(sandboxRoot, "case5_unity_logs");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));
        Directory.CreateDirectory(unityDir);

        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "LogOutput.log"), "[Info] sample bepinex log", Utf8NoBom);
        File.WriteAllText(Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath), "[General]\nEnabled = true\n", Utf8NoBom);
        File.WriteAllText(Path.Combine(unityDir, "Player.log"), "Initialize engine version: 6000.3.27f2", Utf8NoBom);

        string zipPath = Path.Combine(sandboxRoot, "diag_test.zip");
        var exporter = new DiagnosticsExportService();
        await exporter.ExportZipAsync(
            zipPath,
            gameDir,
            Array.Empty<string>(),
            new[] { "[测试] 启动器诊断日志行 1", "[测试] 启动器诊断日志行 2" },
            unityLogDirOverride: unityDir);

        Assert(File.Exists(zipPath), "诊断 ZIP 未生成");
        using var zip = ZipFile.OpenRead(zipPath);
        var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert(names.Contains("system-info.json"), "ZIP 缺少 system-info.json");
        Assert(names.Contains("network-probe.json"), "ZIP 缺少 network-probe.json");
        Assert(names.Contains("hub-session.log"), "ZIP 缺少 hub-session.log");
        Assert(names.Contains("logs/BepInEx_LogOutput.log"), "ZIP 缺少 BepInEx 日志");
        Assert(names.Contains("logs/Unity_Player.log"), "ZIP 缺少 Unity Player.log");
        Assert(names.Contains("config/local.thebazaar.bazaarlauncher.cfg"), "ZIP 缺少客户端配置");
    }

    /// <summary>
    /// Case 6 (P0-45)：同时验证模拟旧服务端与新服务端下的 .cfg 客户端硬保护、引导根目录精准路由、ifMissing 保留与调试文档过滤。
    /// </summary>
    private static async Task TestClientUpdatePolicyAndRoutingAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case6_p045");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));

        string userCfgPath = Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath);
        byte[] origCfgBytes = Utf8NoBom.GetBytes("[General]\nEnabled = true\nServerBase = http://10.10.10.10:9090\nPlayerName = MyHero\n");
        File.WriteAllBytes(userCfgPath, origCfgBytes);

        byte[] serverEvilCfg = Utf8NoBom.GetBytes("[General]\nServerBase = http://198.51.100.99:9999\n");
        byte[] winhttpBytes = Utf8NoBom.GetBytes("WINHTTP_LOADER_V1");
        byte[] doorstopIniBytes = Utf8NoBom.GetBytes("[General]\nenabled=true\n");
        byte[] doorstopVerBytes = Utf8NoBom.GetBytes("3.4.0");
        byte[] launcherBytes = Utf8NoBom.GetBytes("LAUNCHER_DLL_V1");
        byte[] sdkBytes = Utf8NoBom.GetBytes("MODSDK_DLL_V1");
        byte[] xmlDocBytes = Utf8NoBom.GetBytes("<doc>0Harmony</doc>");

        bool useNewSchema = false;

        await RunMockHttpServerAsync(async (path, ctx) =>
        {
            if (path == "/api/client-update/manifest")
            {
                object manifest = useNewSchema
                    ? new
                    {
                        schemaVersion = 2,
                        files = new object[]
                        {
                            new { name = "doorstop_config.ini", category = "loader", targetPath = "doorstop_config.ini", updatePolicy = "ifMissing", atomicGroup = "bepinex-loader", downloadUrl = "/f/doorstop_config.ini", sha256 = UpdateSyncService.ComputeBytesSha256(Utf8NoBom.GetBytes("[General]\nchanged=true\n")), bytes = 22L },
                            new { name = "BazaarLauncher.dll", category = "plugin", targetPath = "BepInEx/plugins/BazaarLauncher.dll", updatePolicy = "always", atomicGroup = "core-plugins", downloadUrl = "/f/BazaarLauncher.dll", sha256 = UpdateSyncService.ComputeBytesSha256(launcherBytes), bytes = (long)launcherBytes.Length }
                        }
                    }
                    : new
                    {
                        // 模拟旧服务端：无 targetPath / 无 updatePolicy，直接下发 .cfg + 扁平 winhttp.dll + 0Harmony.xml
                        files = new object[]
                        {
                            new { name = "local.thebazaar.bazaarlauncher.cfg", relativePath = "BepInEx/config/local.thebazaar.bazaarlauncher.cfg", downloadUrl = "/f/cfg", sha256 = UpdateSyncService.ComputeBytesSha256(serverEvilCfg), bytes = (long)serverEvilCfg.Length },
                            new { name = "winhttp.dll", relativePath = "winhttp.dll", downloadUrl = "/f/winhttp.dll", sha256 = UpdateSyncService.ComputeBytesSha256(winhttpBytes), bytes = (long)winhttpBytes.Length },
                            new { name = "doorstop_config.ini", relativePath = "doorstop_config.ini", downloadUrl = "/f/doorstop_config.ini", sha256 = UpdateSyncService.ComputeBytesSha256(doorstopIniBytes), bytes = (long)doorstopIniBytes.Length },
                            new { name = ".doorstop_version", relativePath = ".doorstop_version", downloadUrl = "/f/.doorstop_version", sha256 = UpdateSyncService.ComputeBytesSha256(doorstopVerBytes), bytes = (long)doorstopVerBytes.Length },
                            new { name = "BazaarLauncher.dll", relativePath = "BazaarLauncher.dll", downloadUrl = "/f/BazaarLauncher.dll", sha256 = UpdateSyncService.ComputeBytesSha256(launcherBytes), bytes = (long)launcherBytes.Length },
                            new { name = "BazaarModSdk.dll", relativePath = "BazaarModSdk.dll", downloadUrl = "/f/BazaarModSdk.dll", sha256 = UpdateSyncService.ComputeBytesSha256(sdkBytes), bytes = (long)sdkBytes.Length },
                            new { name = "0Harmony.xml", relativePath = "BepInEx/core/0Harmony.xml", downloadUrl = "/f/0Harmony.xml", sha256 = UpdateSyncService.ComputeBytesSha256(xmlDocBytes), bytes = (long)xmlDocBytes.Length }
                        }
                    };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest)));
            }
            else if (path == "/f/cfg") await WriteResponseAsync(ctx, 200, "application/octet-stream", serverEvilCfg);
            else if (path == "/f/winhttp.dll") await WriteResponseAsync(ctx, 200, "application/octet-stream", winhttpBytes);
            else if (path == "/f/doorstop_config.ini") await WriteResponseAsync(ctx, 200, "application/octet-stream", doorstopIniBytes);
            else if (path == "/f/.doorstop_version") await WriteResponseAsync(ctx, 200, "application/octet-stream", doorstopVerBytes);
            else if (path == "/f/BazaarLauncher.dll") await WriteResponseAsync(ctx, 200, "application/octet-stream", launcherBytes);
            else if (path == "/f/BazaarModSdk.dll") await WriteResponseAsync(ctx, 200, "application/octet-stream", sdkBytes);
            else if (path == "/f/0Harmony.xml") await WriteResponseAsync(ctx, 200, "application/octet-stream", xmlDocBytes);
            else await WriteResponseAsync(ctx, 404, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{}"));
        }, async baseUrl =>
        {
            using var sync = new UpdateSyncService();

            // 1. 对接旧服务端
            UpdateSyncReport legacyRep = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(legacyRep.Success, $"兼容旧服务端同步应成功：{legacyRep.SummaryMessage}");
            Assert(File.ReadAllBytes(userCfgPath).SequenceEqual(origCfgBytes), "玩家本地 .cfg 绝对不可被旧服务端洗白");
            Assert(legacyRep.Items.Any(i => i.TargetRelativePath.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) && i.Action == "已保护"),
                "被保护的 .cfg 必须标记为 '已保护'");

            Assert(File.Exists(Path.Combine(gameDir, "winhttp.dll")), "winhttp.dll 必须落盘游戏根目录");
            Assert(File.Exists(Path.Combine(gameDir, "doorstop_config.ini")), "doorstop_config.ini 必须落盘游戏根目录");
            Assert(File.Exists(Path.Combine(gameDir, ".doorstop_version")), ".doorstop_version 必须落盘游戏根目录");
            Assert(!File.Exists(Path.Combine(gameDir, @"BepInEx\plugins\winhttp.dll")), "winhttp.dll 严禁错写进 BepInEx\\plugins");
            Assert(File.Exists(Path.Combine(gameDir, @"BepInEx\plugins\BazaarLauncher.dll")), "BazaarLauncher.dll 必须落盘 BepInEx\\plugins");
            Assert(File.Exists(Path.Combine(gameDir, @"BepInEx\plugins\BazaarModSdk.dll")), "BazaarModSdk.dll 必须落盘 BepInEx\\plugins");
            Assert(!File.Exists(Path.Combine(gameDir, @"BepInEx\core\0Harmony.xml")), "调试文档 0Harmony.xml 必须被客户端过滤忽略");

            // 2. 对接新服务端：验证 doorstop_config.ini 的 ifMissing 策略不覆盖本地自定义内容
            string customDoorstop = "[General]\nenabled=true\n# player-custom-doorstop\n";
            File.WriteAllText(Path.Combine(gameDir, "doorstop_config.ini"), customDoorstop, Utf8NoBom);
            useNewSchema = true;
            UpdateSyncReport v2Rep = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(v2Rep.Success, $"新服务端 ifMissing 同步应成功：{v2Rep.SummaryMessage}");
            Assert(File.ReadAllText(Path.Combine(gameDir, "doorstop_config.ini"), Encoding.UTF8) == customDoorstop, "ifMissing 不得覆写本地已有 doorstop_config.ini");
            Assert(v2Rep.Items.Any(i => i.TargetRelativePath.Equals("doorstop_config.ini", StringComparison.OrdinalIgnoreCase) && i.Action == "已保留"),
                "已有文件命中 ifMissing 必须标记为 '已保留'");
        });
    }

    /// <summary>
    /// Case 7 (P0-46)：验证外置 hub-presets.json 防回环碰撞硬不变量与 InferServerMode 8080/9090 端口边界。
    /// </summary>
    private static Task TestHubPresetsAndServerModeInferenceAsync(string sandboxRoot)
    {
        // 1. 开源默认无预设：8080 回环归 LocalStandalone，非 8080 端口或含子路径归 CustomServer
        var emptyPreset = HubPresetConfig.Empty;
        Assert(!emptyPreset.HasPresetRemote && emptyPreset.DefaultMode == ServerMode.LocalStandalone, "无预设默认模式应为 LocalStandalone");

        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://127.0.0.1:8080" }.InferServerMode(emptyPreset) == ServerMode.LocalStandalone, "127.0.0.1:8080 应判 LocalStandalone");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://localhost:8080/" }.InferServerMode(emptyPreset) == ServerMode.LocalStandalone, "localhost:8080 应判 LocalStandalone");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://[::1]:8080" }.InferServerMode(emptyPreset) == ServerMode.LocalStandalone, "[::1]:8080 应判 LocalStandalone");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://127.0.0.1:9090" }.InferServerMode(emptyPreset) == ServerMode.CustomServer, "127.0.0.1:9090 必须归入 CustomServer，不可误抢");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://localhost:8080/sub" }.InferServerMode(emptyPreset) == ServerMode.CustomServer, "带子路径的回环服应归入 CustomServer");
        Assert(new LauncherConfigSnapshot { Enabled = false, ServerBase = "http://127.0.0.1:8080" }.InferServerMode(emptyPreset) == ServerMode.PureOfficial, "Enabled=false 应判 PureOfficial");

        // 2. 回环 URL 预设文件必须强制拒收，彻底根除 PresetRemoteUrl == LocalStandaloneUrl 碰撞
        string badPresetPath = Path.Combine(sandboxRoot, "bad_loopback_presets.json");
        File.WriteAllText(badPresetPath, "{\"defaultRemote\":{\"name\":\"Bad\",\"url\":\"http://127.0.0.1:8080\"}}", Utf8NoBom);
        HubPresetConfig badLoaded = HubPresetLoader.Load(badPresetPath);
        Assert(!badLoaded.HasPresetRemote, "指向 127.0.0.1 的 hub-presets.json 必须被强制拒收");

        File.WriteAllText(badPresetPath, "{\"defaultRemote\":{\"name\":\"Bad2\",\"url\":\"http://localhost:9000\"}}", Utf8NoBom);
        Assert(!HubPresetLoader.Load(badPresetPath).HasPresetRemote, "指向 localhost 的 hub-presets.json 必须被强制拒收");

        // 3. 合法远程预设文件精确加载并区分三模式
        string goodPresetPath = Path.Combine(sandboxRoot, "good_remote_presets.json");
        File.WriteAllText(goodPresetPath, "{\"defaultRemote\":{\"name\":\"群友私服\",\"shortName\":\"私服\",\"url\":\"http://203.0.113.10:8080/\"}}", Utf8NoBom);
        HubPresetConfig goodLoaded = HubPresetLoader.Load(goodPresetPath);
        Assert(goodLoaded.HasPresetRemote && goodLoaded.PresetRemoteUrl == "http://203.0.113.10:8080", "合法远程预设应规范加载");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://203.0.113.10:8080" }.InferServerMode(goodLoaded) == ServerMode.PresetRemote, "命中远程预设 URL 应判 PresetRemote");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://127.0.0.1:8080" }.InferServerMode(goodLoaded) == ServerMode.LocalStandalone, "有预设时 127.0.0.1:8080 仍须优先判 LocalStandalone");
        Assert(new LauncherConfigSnapshot { Enabled = true, ServerBase = "http://10.0.0.5:8080" }.InferServerMode(goodLoaded) == ServerMode.CustomServer, "其他地址应判 CustomServer");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Case 8 (P0-47)：验证进程锁拦截/已最新放行、ReadOnly 属性区分、首次建目录、Staging 半失败零污染、Commit 遇锁逆序回滚与非核心失败 WarnNonCoreFailure。
    /// </summary>
    private static async Task TestAtomicTransactionAndLaunchGateAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case8_p047");
        Directory.CreateDirectory(gameDir); // 故意不先建 BepInEx\plugins，测试首次安装自动建父目录

        byte[] launcherV1 = Utf8NoBom.GetBytes("LAUNCHER_V1_ORIGINAL");
        byte[] sdkV1 = Utf8NoBom.GetBytes("MODSDK_V1_ORIGINAL");
        byte[] launcherV2 = Utf8NoBom.GetBytes("LAUNCHER_V2_UPGRADED");
        byte[] sdkV2 = Utf8NoBom.GetBytes("MODSDK_V2_UPGRADED");
        string badAssetSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        bool serveV2 = false;
        bool corruptSdkV2 = false;
        bool includeBadModAsset = false;

        await RunMockHttpServerAsync(async (path, ctx) =>
        {
            byte[] curLauncher = serveV2 ? launcherV2 : launcherV1;
            byte[] curSdk = serveV2 ? sdkV2 : sdkV1;

            if (path == "/api/client-update/manifest")
            {
                var manifest = new
                {
                    schemaVersion = 2,
                    files = new object[]
                    {
                        new { name = "BazaarLauncher.dll", category = "plugin", targetPath = "BepInEx/plugins/BazaarLauncher.dll", updatePolicy = "always", atomicGroup = "core-plugins", downloadUrl = "/f/launcher", sha256 = UpdateSyncService.ComputeBytesSha256(curLauncher), bytes = (long)curLauncher.Length },
                        new { name = "BazaarModSdk.dll", category = "plugin", targetPath = "BepInEx/plugins/BazaarModSdk.dll", updatePolicy = "always", atomicGroup = "core-plugins", downloadUrl = "/f/sdk", sha256 = UpdateSyncService.ComputeBytesSha256(curSdk), bytes = (long)curSdk.Length }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest)));
            }
            else if (path == "/f/launcher")
            {
                await WriteResponseAsync(ctx, 200, "application/octet-stream", curLauncher);
            }
            else if (path == "/f/sdk")
            {
                byte[] body = (serveV2 && corruptSdkV2) ? Utf8NoBom.GetBytes("CORRUPTED_SDK_BYTES") : curSdk;
                await WriteResponseAsync(ctx, 200, "application/octet-stream", body);
            }
            else if (path == "/api/mods/manifest" && includeBadModAsset)
            {
                var mods = new
                {
                    manifestHash = "bad-asset-test",
                    assets = new object[]
                    {
                        new { modId = "sample", logicalPath = "assets/bad.png", sha256 = badAssetSha, bytes = 10L }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(mods)));
            }
            else if (path == $"/api/mods/assets/{badAssetSha}")
            {
                await WriteResponseAsync(ctx, 200, "image/png", Utf8NoBom.GetBytes("WRONG_PNG_BYTES"));
            }
            else
            {
                await WriteResponseAsync(ctx, 404, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{}"));
            }
        }, async baseUrl =>
        {
            using var sync = new UpdateSyncService();
            string launcherPath = Path.Combine(gameDir, @"BepInEx\plugins\BazaarLauncher.dll");
            string sdkPath = Path.Combine(gameDir, @"BepInEx\plugins\BazaarModSdk.dll");
            string stagingRoot = Path.Combine(gameDir, LauncherConstants.StagingRootFolderName);

            // 8a. 游戏运行中 + 待更新 → 硬拦截 0 字节写入
            UpdateSyncReport blocked = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => true, beforeCommitMoveHook: null);
            Assert(blocked.BlockedByRunningGame && blocked.HasCorePluginFailure && !blocked.Success, "游戏运行中必须拦截核心更新");
            Assert(LaunchSyncGate.Evaluate(blocked) == LaunchGateAction.BlockRunningGame, "门禁应返回 BlockRunningGame");
            Assert(!File.Exists(launcherPath) && !File.Exists(sdkPath), "拦截后不得有任何文件落盘");

            // 8b. 首次安装（缺失 BepInEx\plugins 目录）→ 自动创建父目录并原子提交 V1 双 DLL
            UpdateSyncReport firstInstall = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => false, beforeCommitMoveHook: null);
            Assert(firstInstall.Success && firstInstall.DownloadedCount == 2, $"首次自动建目录原子提交 V1 应成功：{firstInstall.SummaryMessage}");
            Assert(File.ReadAllBytes(launcherPath).SequenceEqual(launcherV1) && File.ReadAllBytes(sdkPath).SequenceEqual(sdkV1), "V1 双 DLL 内容校验失败");
            Assert(!Directory.Exists(stagingRoot), "提交完成后 _BazaarHubStaging 必须自动清理");

            // 8c. 全部已是最新 + 游戏运行中 → 无需写盘，不应误拦
            UpdateSyncReport runningUpToDate = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => true, beforeCommitMoveHook: null);
            Assert(runningUpToDate.Success && !runningUpToDate.BlockedByRunningGame, "全部已是最新时即使游戏运行也不应误拦");
            Assert(LaunchSyncGate.Evaluate(runningUpToDate) == LaunchGateAction.AllowLaunch, "门禁应返回 AllowLaunch");

            // 切换到 V2 升级测试
            serveV2 = true;

            // 8d. 只读属性区分检测（绝不误报进程占用）
            File.SetAttributes(launcherPath, File.GetAttributes(launcherPath) | FileAttributes.ReadOnly);
            try
            {
                UpdateSyncReport roRep = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => false, beforeCommitMoveHook: null);
                Assert(!roRep.Success && roRep.HasCorePluginFailure, "只读文件应触发预检拦截");
                Assert(roRep.Errors.Any(e => e.Contains("只读属性", StringComparison.Ordinal)),
                    $"只读文件必须明确提示'只读属性'，实际错误：{string.Join(" | ", roRep.Errors)}");
                Assert(LaunchSyncGate.Evaluate(roRep) == LaunchGateAction.BlockCoreFailure, "只读预检失败门禁应返回 BlockCoreFailure");
            }
            finally
            {
                File.SetAttributes(launcherPath, File.GetAttributes(launcherPath) & ~FileAttributes.ReadOnly);
            }

            // 8e. Staging 阶段第 2 个 DLL 校验失败 → 整组放弃，工作目录 0 字节触碰
            corruptSdkV2 = true;
            UpdateSyncReport stageFail = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => false, beforeCommitMoveHook: null);
            Assert(!stageFail.Success && stageFail.HasCorePluginFailure, "Staging 失败应标记核心失败");
            Assert(stageFail.RolledBackAtomicGroups.Contains("core-plugins"), "Staging 失败应记录回滚组 core-plugins");
            Assert(File.ReadAllBytes(launcherPath).SequenceEqual(launcherV1) && File.ReadAllBytes(sdkPath).SequenceEqual(sdkV1), "Staging 失败不得修改任何 V1 文件");
            Assert(!Directory.Exists(stagingRoot), "Staging 失败后必须清理暂存目录");
            Assert(LaunchSyncGate.Evaluate(stageFail) == LaunchGateAction.BlockCoreFailure, "门禁应返回 BlockCoreFailure");

            // 8f. Commit 阶段第 2 个 DLL 迁移突遇 IO 锁 → 自动逆序回滚第 1 个 DLL，双 DLL 维持 V1 原子一致
            corruptSdkV2 = false;
            UpdateSyncReport commitFail = await sync.SyncAsync(
                gameDir,
                baseUrl,
                applyDownloads: true,
                null,
                null,
                isGameRunningProbe: () => false,
                beforeCommitMoveHook: rel =>
                {
                    if (rel.EndsWith("BazaarModSdk.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException("模拟提交第 2 个 DLL 时文件突被杀毒软件锁定");
                    }
                });
            Assert(!commitFail.Success && commitFail.HasCorePluginFailure, "Commit 中断应标记核心失败");
            Assert(commitFail.RolledBackAtomicGroups.Contains("core-plugins"), "Commit 中断应记录回滚组 core-plugins");
            Assert(File.ReadAllBytes(launcherPath).SequenceEqual(launcherV1), "Commit 中断后第 1 个 DLL 必须自动逆序回滚回 V1");
            Assert(File.ReadAllBytes(sdkPath).SequenceEqual(sdkV1), "Commit 中断后第 2 个 DLL 必须维持 V1");
            Assert(!Directory.Exists(stagingRoot), "Commit 回滚后必须清理 _BazaarHubStaging");
            Assert(LaunchSyncGate.Evaluate(commitFail) == LaunchGateAction.BlockCoreFailure, "Commit 回滚后门禁应返回 BlockCoreFailure");

            // 8g. 正常完成 V2 升级后，引入非核心 Mod PNG 失败 → WarnNonCoreFailure（不阻断为核心失败）
            UpdateSyncReport v2Ok = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => false, beforeCommitMoveHook: null);
            Assert(v2Ok.Success && File.ReadAllBytes(launcherPath).SequenceEqual(launcherV2) && File.ReadAllBytes(sdkPath).SequenceEqual(sdkV2), "V2 原子升级应全部成功");

            includeBadModAsset = true;
            UpdateSyncReport nonCoreFail = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, isGameRunningProbe: () => false, beforeCommitMoveHook: null);
            Assert(!nonCoreFail.Success && nonCoreFail.FailedCount == 1 && !nonCoreFail.HasCorePluginFailure && !nonCoreFail.BlockedByRunningGame,
                "仅非核心 Mod 资产失败不得置 HasCorePluginFailure");
            Assert(LaunchSyncGate.Evaluate(nonCoreFail) == LaunchGateAction.WarnNonCoreFailure, "仅非核心失败门禁应返回 WarnNonCoreFailure");
        });
    }

    /// <summary>
    /// Case 9 (P0-34)：验证 CommunityModClient + SafePathAndZipGuard 拦截路径穿越、放行 Windows 反斜杠 Zip、整包拒收危险扩展名与 75MB Zip Bomb。
    /// </summary>
    private static async Task TestCommunityModSecurityGuardsAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case9_p034");
        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);
        string sentinelFile = Path.Combine(pluginsDir, "sentinel.dll");
        File.WriteAllText(sentinelFile, "DO_NOT_DELETE_ME", Utf8NoBom);

        byte[] goodWinSlashZip = BuildZipBytes(za =>
        {
            za.CreateEntry(@"MY-MOD\");
            za.CreateEntry(@"MY-MOD\assets\");
            AddZipTextEntry(za, @"MY-MOD\mod.json", "{\"id\":\"my-mod\",\"version\":\"1.0.0\"}");
            AddZipTextEntry(za, @"MY-MOD\assets\icon..v2.png", "PNG_BYTES");
        });

        byte[] dangerousDllZip = BuildZipBytes(za =>
        {
            AddZipTextEntry(za, "bad-dll-mod/mod.json", "{\"id\":\"bad-dll-mod\"}");
            AddZipTextEntry(za, "bad-dll-mod/evil.dll", "MZ_PAYLOAD");
        });

        byte[] zipBombBytes = BuildZipBytes(za =>
        {
            AddZipTextEntry(za, "bomb-mod/mod.json", "{\"id\":\"bomb-mod\"}");
            // 跨 3 个条目合计 75MB 全零压缩包（压缩后仅几十 KB，触发 64MB 跨条目总解压上限）
            byte[] zeros = new byte[1024 * 1024];
            for (int f = 0; f < 3; f++)
            {
                var entry = za.CreateEntry($"bomb-mod/assets/z{f}.png", CompressionLevel.SmallestSize);
                using var s = entry.Open();
                for (int mb = 0; mb < 25; mb++)
                {
                    s.Write(zeros, 0, zeros.Length);
                }
            }
        });

        await RunMockHttpServerAsync(async (path, ctx) =>
        {
            if (path.EndsWith("/download", StringComparison.OrdinalIgnoreCase))
            {
                if (path.Contains("/my-mod/", StringComparison.OrdinalIgnoreCase))
                    await WriteResponseAsync(ctx, 200, "application/zip", goodWinSlashZip);
                else if (path.Contains("/bad-dll-mod/", StringComparison.OrdinalIgnoreCase))
                    await WriteResponseAsync(ctx, 200, "application/zip", dangerousDllZip);
                else if (path.Contains("/bomb-mod/", StringComparison.OrdinalIgnoreCase))
                    await WriteResponseAsync(ctx, 200, "application/zip", zipBombBytes);
                else
                    await WriteResponseAsync(ctx, 200, "application/zip", goodWinSlashZip);
            }
            else
            {
                await WriteResponseAsync(ctx, 404, "application/json", Utf8NoBom.GetBytes("{}"));
            }
        }, async baseUrl =>
        {
            using var client = new CommunityModClient();

            // 9a. 路径穿越 modId = "../../plugins" 必须被拦截且哨兵文件完好无损
            var travRes = await client.DownloadToLocalAsync(baseUrl, "../../plugins", gameDir);
            Assert(!travRes.Ok, "路径穿越 modId 必须被拒收");
            Assert(File.Exists(sentinelFile) && File.ReadAllText(sentinelFile, Encoding.UTF8) == "DO_NOT_DELETE_ME",
                "路径穿越攻击绝不能删除或篡改 BepInEx\\plugins 哨兵文件");

            // 9b. 含 Windows 反斜杠目录条目 + 合法双点文件名 icon..v2.png 的 Zip 必须成功落盘且无临时残留
            string archiveDir = Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "downloads");
            var okRes = await client.DownloadToLocalAsync(baseUrl, "my-mod", gameDir);
            Assert(okRes.Ok, $"Windows 反斜杠目录合法 Mod 应成功解压：{okRes.Message}");
            Assert(File.Exists(Path.Combine(gameDir, "BepInEx", "mods", "my-mod", "mod.json")), "my-mod/mod.json 未落盘");
            Assert(File.Exists(Path.Combine(gameDir, "BepInEx", "mods", "my-mod", "assets", "icon..v2.png")), "icon..v2.png 未落盘");
            Assert(File.Exists(Path.Combine(archiveDir, "my-mod.bazaarmod.zip")), "归档 .bazaarmod.zip 未生成");

            // 9c. 夹带 .dll 的恶意包必须 Fail-Closed 整包拒收且零残留
            var dllRes = await client.DownloadToLocalAsync(baseUrl, "bad-dll-mod", gameDir);
            Assert(!dllRes.Ok, "夹带 .dll 的 Mod 包必须整包拒收");
            Assert(!Directory.Exists(Path.Combine(gameDir, "BepInEx", "mods", "bad-dll-mod")), "被拒收恶意包不得创建目标目录");
            Assert(!File.Exists(Path.Combine(archiveDir, "bad-dll-mod.bazaarmod.zip")), "被拒收恶意包不得保留归档 ZIP");

            // 9d. 75MB 跨条目 Zip Bomb 必须流式拦截拒收且零残留
            var bombRes = await client.DownloadToLocalAsync(baseUrl, "bomb-mod", gameDir);
            Assert(!bombRes.Ok, "75MB 跨条目 Zip Bomb 必须被拦截拒收");
            Assert(!Directory.Exists(Path.Combine(gameDir, "BepInEx", "mods", "bomb-mod")), "Zip Bomb 不得创建目标目录");
            Assert(!File.Exists(Path.Combine(archiveDir, "bomb-mod.bazaarmod.zip")), "Zip Bomb 不得保留归档 ZIP");
            Assert(!Directory.EnumerateFileSystemEntries(Path.Combine(gameDir, "BepInEx", "mods"), "*.tmp*").Any(), "严禁残留 .tmp 临时文件或目录");
        });
    }

    private static async Task TestSemVerGatesAndDowngradeProtectionAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case10_semver");
        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);

        // 复制本启动器真实 PE DLL（带 FileVersion 15.5.0.0）作为“高版本本地插件”，验证旧服（14.0.0）不可将其降级覆写
        string selfDll = typeof(SelfTestRunner).Assembly.Location;
        Assert(File.Exists(selfDll), $"无法定位当前 BazaarHub.dll：{selfDll}");
        string localLauncherPath = Path.Combine(pluginsDir, "BazaarLauncher.dll");
        File.Copy(selfDll, localLauncherPath, overwrite: true);
        byte[] origHighVerBytes = File.ReadAllBytes(localLauncherPath);

        byte[] oldServerDllBytes = Utf8NoBom.GetBytes("OLD_SERVER_LAUNCHER_14_0_0");
        string oldServerSha = UpdateSyncService.ComputeBytesSha256(oldServerDllBytes);
        string webpSha = "1111111111111111111111111111111111111111111111111111111111111111";

        bool requireHighMinVer = false;

        await RunMockHttpServerAsync(async (path, ctx) =>
        {
            if (path == "/api/client-update/manifest")
            {
                object manifest = new
                {
                    schemaVersion = 2,
                    serverVersion = requireHighMinVer ? "16.1.0" : "14.0.0",
                    launcherVersion = "14.0.0",
                    hubVersion = requireHighMinVer ? "16.1.0" : "14.0.0",
                    minRequiredVersion = requireHighMinVer ? "16.0.0" : "14.0.0",
                    minHubVersion = requireHighMinVer ? "16.0.0" : "",
                    files = new object[]
                    {
                        new
                        {
                            name = "BazaarLauncher.dll",
                            category = "plugin",
                            version = "14.0.0",
                            fileVersion = "14.0.0",
                            targetPath = "BepInEx/plugins/BazaarLauncher.dll",
                            updatePolicy = "always",
                            atomicGroup = "core-plugins",
                            downloadUrl = "/f/old-launcher.dll",
                            sha256 = oldServerSha,
                            bytes = (long)oldServerDllBytes.Length
                        }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifest)));
            }
            else if (path == "/f/old-launcher.dll")
            {
                await WriteResponseAsync(ctx, 200, "application/octet-stream", oldServerDllBytes);
            }
            else if (path == "/api/mods/manifest")
            {
                var mods = new
                {
                    manifestHash = "m-webp",
                    mods = new object[]
                    {
                        new
                        {
                            id = "webp-sample",
                            name = "WebP测试",
                            assets = new object[]
                            {
                                new { modId = "webp-sample", logicalPath = "assets/unsupported.webp", sha256 = webpSha, bytes = 64 }
                            }
                        }
                    }
                };
                await WriteResponseAsync(ctx, 200, "application/json; charset=utf-8", Utf8NoBom.GetBytes(JsonSerializer.Serialize(mods)));
            }
            else
            {
                await WriteResponseAsync(ctx, 404, "application/json; charset=utf-8", Utf8NoBom.GetBytes("{}"));
            }
        }, async baseUrl =>
        {
            using var sync = new UpdateSyncService();

            // 1. 连接旧服：本地高版本 15.5.0 绝不可被旧服 14.0.0 降级覆写，且 .webp 资产被优雅跳过
            UpdateSyncReport repOld = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true, null, null, () => false, null);
            Assert(repOld.Success, $"连接旧服防降级同步应成功：{repOld.SummaryMessage}");
            Assert(repOld.DowngradeProtectedCount == 1, $"应触发 1 次防降级保护，实际 {repOld.DowngradeProtectedCount}");
            Assert(File.ReadAllBytes(localLauncherPath).SequenceEqual(origHighVerBytes), "高版本本地插件被旧服非法降级覆写");
            Assert(repOld.Items.Any(i => i.Action == "已保留（本地更新）"), "明细应标明 '已保留（本地更新）'");
            Assert(repOld.Warnings.Any(w => w.Contains("WebP", StringComparison.OrdinalIgnoreCase)), "应记录 WebP 不支持告警");
            Assert(!File.Exists(Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "assets", webpSha + ".webp")), "WebP 不得落盘");

            // 2. 连接要求更高 MinRequiredVersion(16.0.0) & MinHubVersion(16.0.0) 的新服：必须触发硬阻断与 Hub 升级提示
            requireHighMinVer = true;
            UpdateSyncReport repHigh = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: false, null, null, () => false, null);
            Assert(repHigh.BlockedByMinVersion, "低于 MinRequiredVersion 时 BlockedByMinVersion 必须为 true");
            Assert(LaunchSyncGate.Evaluate(repHigh) == LaunchGateAction.BlockCoreFailure, "低于 MinRequiredVersion 时 LaunchSyncGate 必须阻断启动");
            Assert(repHigh.HubUpgradeRequired && repHigh.HubUpgradeRecommended && repHigh.RecommendedHubVersion == "16.0.0",
                "低于 MinHubVersion 时应标记 HubUpgradeRequired 并提示 16.0.0");
        });
    }

    private static async Task TestStandaloneHostHealthzAndJobObjectAsync()
    {
        // 1. /healthz 专属 JSON 指纹校验
        Assert(StandaloneServerHost.IsBazaarHealthzPayload("""{"ok":true,"loadedCards":1200,"ghostBoards":50}"""),
            "真实 BazaarServer /healthz 指纹应通过校验");
        Assert(!StandaloneServerHost.IsBazaarHealthzPayload("""{"ok":true}"""),
            "仅含 ok:true 的通用第三方 /healthz 必须被指纹校验拒绝");
        Assert(!StandaloneServerHost.IsBazaarHealthzPayload("<html><body>200 OK</body></html>"),
            "非 JSON 响应必须被拒绝");

        // 2. 模拟端口被第三方非 BazaarServer 程序占用：EnsureStartedAsync 必须明确阻断而非误复用
        int occupiedPort = GetFreeTcpPort();
        using var alienListener = new HttpListener();
        alienListener.Prefixes.Add($"http://127.0.0.1:{occupiedPort}/");
        alienListener.Start();
        using var cts = new CancellationTokenSource();
        Task alienLoop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var ctx = await alienListener.GetContextAsync();
                    await WriteResponseAsync(ctx, 200, "application/json", Utf8NoBom.GetBytes("""{"status":"other-web-service"}"""));
                }
                catch { break; }
            }
        });
        try
        {
            using var host = new StandaloneServerHost();
            var (ready, msg) = await host.EnsureStartedAsync(occupiedPort);
            Assert(!ready, "第三方占端口时 EnsureStartedAsync 绝不可误报就绪");
            Assert(msg.Contains("占用", StringComparison.Ordinal), $"冲突提示应明确指出端口占用：{msg}");
        }
        finally
        {
            cts.Cancel();
            alienListener.Stop();
            try { await alienLoop; } catch { }
        }

        // 3. Windows Job Object 创建与释放生命周期校验
        Assert(StandaloneServerHost.VerifyJobObjectLifecycle(), "Windows Job Object 生命周期校验失败");
    }

    private static Task TestUnifiedVersionAndCasPoolAsync(string sandboxRoot)
    {
        Assert(LauncherConstants.HubVersion == "15.5.0", $"HubVersion 应动态读取根级 Directory.Build.props 为 15.5.0，实际：{LauncherConstants.HubVersion}");
        string casDir = Path.Combine(sandboxRoot, LauncherConstants.ModCacheRelativeDir, "assets");
        string normalized = casDir.Replace('/', '\\');
        Assert(normalized.EndsWith(@"BepInEx\config\BazaarModCache\assets", StringComparison.OrdinalIgnoreCase),
            $"CAS 共享资产目录必须对齐 BepInEx\\config\\BazaarModCache\\assets，实际：{normalized}");
        return Task.CompletedTask;
    }

    private static async Task TestDiagnosticsPrivacyRedactionAsync(string sandboxRoot)
    {
        string gameDir = Path.Combine(sandboxRoot, "case13_privacy");
        string unityDir = Path.Combine(sandboxRoot, "case13_unity");
        Directory.CreateDirectory(Path.Combine(gameDir, "BepInEx", "config"));
        Directory.CreateDirectory(unityDir);

        // 运行期动态构造样本路径，避免静态源码字面量命中开源镜像防泄漏正则
        string sampleUserPath = "C:" + Path.DirectorySeparatorChar + "Users" + Path.DirectorySeparatorChar + "SampleUser" + Path.DirectorySeparatorChar + "Documents" + Path.DirectorySeparatorChar + "secret.log";
        string sampleUserPrefix = "C:" + Path.DirectorySeparatorChar + "Users" + Path.DirectorySeparatorChar + "SampleUser";
        string sampleLog =
            $"[Info] Host={Environment.MachineName} User={Environment.UserName}\n" +
            $"Path={sampleUserPath}\n" +
            "Authorization: Bearer secret_jwt_token_987654\n" +
            "GameDriverToken = gd_tok_secret_12345\n" +
            "Url=https://alice:SuperSecretPwd@example.invalid/api\n";

        string sanitizedDirect = DiagnosticsExportService.SanitizeDiagnosticText(sampleLog);
        Assert(!sanitizedDirect.Contains(sampleUserPrefix, StringComparison.OrdinalIgnoreCase), "用户目录绝对路径必须脱敏");
        Assert(!sanitizedDirect.Contains("secret_jwt_token_987654", StringComparison.Ordinal), "Bearer Token 必须脱敏");
        Assert(!sanitizedDirect.Contains("gd_tok_secret_12345", StringComparison.Ordinal), "GameDriverToken 必须脱敏");
        Assert(!sanitizedDirect.Contains("SuperSecretPwd", StringComparison.Ordinal), "URL 内嵌密码必须脱敏");
        Assert(sanitizedDirect.Contains("<REDACTED_TOKEN>", StringComparison.Ordinal), "Token 脱敏占位符缺失");
        Assert(sanitizedDirect.Contains("<REDACTED_CRED>", StringComparison.Ordinal), "URL 凭证脱敏占位符缺失");
        Assert(sanitizedDirect.Contains("<REDACTED_USER>", StringComparison.Ordinal), "用户名脱敏占位符缺失");

        File.WriteAllText(Path.Combine(gameDir, "BepInEx", "LogOutput.log"), sampleLog, Utf8NoBom);
        File.WriteAllText(
            Path.Combine(gameDir, LauncherConstants.LauncherCfgRelativePath),
            "[General]\nEnabled = true\nGameDriverToken = gd_cfg_secret_777\n",
            Utf8NoBom);
        File.WriteAllText(Path.Combine(unityDir, "Player.log"), sampleLog, Utf8NoBom);

        string zipPath = Path.Combine(sandboxRoot, "diag_privacy.zip");
        var exporter = new DiagnosticsExportService();
        await exporter.ExportZipAsync(
            zipPath,
            gameDir,
            Array.Empty<string>(),
            new[] { sampleLog },
            unityLogDirOverride: unityDir);

        Assert(File.Exists(zipPath), "脱敏诊断 ZIP 未生成");
        Assert(!Directory.EnumerateFiles(sandboxRoot, "*.tmp*", SearchOption.TopDirectoryOnly).Any(), "导出 ZIP 后不得残留 .tmp 文件");

        using var zip = ZipFile.OpenRead(zipPath);
        static string ReadEntry(ZipArchive archive, string name)
        {
            var entry = archive.GetEntry(name);
            Assert(entry != null, $"诊断包缺少条目：{name}");
            using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        string sysInfo = ReadEntry(zip, "system-info.json");
        Assert(sysInfo.Contains("<REDACTED_HOST>", StringComparison.Ordinal), "system-info.json 必须脱敏 MachineName");
        Assert(sysInfo.Contains("<REDACTED_USER>", StringComparison.Ordinal), "system-info.json 必须脱敏 UserName");
        Assert(!sysInfo.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase), "system-info.json 不得泄露真实 MachineName");

        string cfgEntry = ReadEntry(zip, "config/local.thebazaar.bazaarlauncher.cfg");
        Assert(!cfgEntry.Contains("gd_cfg_secret_777", StringComparison.Ordinal), "配置内 GameDriverToken 必须脱敏");

        string logEntry = ReadEntry(zip, "logs/BepInEx_LogOutput.log");
        Assert(!logEntry.Contains("secret_jwt_token_987654", StringComparison.Ordinal), "BepInEx 日志 Bearer Token 必须脱敏");
        Assert(!logEntry.Contains("SuperSecretPwd", StringComparison.Ordinal), "BepInEx 日志 URL 凭证必须脱敏");
    }

    private static Task TestAtomicFileWriterAsync(string sandboxRoot)
    {
        string dir = Path.Combine(sandboxRoot, "case14_atomic");
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "state.json");

        string t1 = AtomicFileWriter.BuildUniqueTempPath(target);
        string t2 = AtomicFileWriter.BuildUniqueTempPath(target);
        Assert(!string.Equals(t1, t2, StringComparison.OrdinalIgnoreCase), "两次生成的随机临时路径必须唯一且不碰撞");
        Assert(t1.Contains($".{Environment.ProcessId}.", StringComparison.Ordinal) && t1.EndsWith(".tmp", StringComparison.Ordinal),
            $"随机临时路径格式异常：{t1}");

        AtomicFileWriter.WriteAllTextAtomic(target, "{\"v\":1}", Utf8NoBom);
        Assert(File.ReadAllText(target, Encoding.UTF8) == "{\"v\":1}", "WriteAllTextAtomic 落盘内容不符");
        Assert(!Directory.EnumerateFiles(dir, "*.tmp*").Any(), "WriteAllTextAtomic 不得残留 .tmp 文件");

        bool threw = false;
        try
        {
            AtomicFileWriter.WriteWithTempFile(target, tmp =>
            {
                File.WriteAllText(tmp, "PARTIAL_CORRUPT_WRITE", Utf8NoBom);
                throw new IOException("模拟写入中途失败");
            });
        }
        catch (IOException)
        {
            threw = true;
        }
        Assert(threw, "WriteWithTempFile 回调异常必须向上抛出");
        Assert(File.ReadAllText(target, Encoding.UTF8) == "{\"v\":1}", "异常中断不得破坏原目标文件");
        Assert(!Directory.EnumerateFiles(dir, "*.tmp*").Any(), "异常中断必须清理随机 .tmp 残留");

        string copyTarget = Path.Combine(dir, "state_copy.json");
        AtomicFileWriter.CopyFileAtomic(target, copyTarget);
        Assert(File.ReadAllText(copyTarget, Encoding.UTF8) == "{\"v\":1}", "CopyFileAtomic 内容不符");
        Assert(!Directory.EnumerateFiles(dir, "*.tmp*").Any(), "CopyFileAtomic 不得残留 .tmp 文件");

        return Task.CompletedTask;
    }

    private static byte[] BuildZipBytes(Action<ZipArchive> populate)
    {
        using var ms = new MemoryStream();
        using (var za = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true, Utf8NoBom))
        {
            populate(za);
        }
        return ms.ToArray();
    }

    private static void AddZipTextEntry(ZipArchive za, string entryName, string content)
    {
        var e = za.CreateEntry(entryName, CompressionLevel.Fastest);
        using var s = e.Open();
        byte[] bytes = Utf8NoBom.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static async Task RunMockHttpServerAsync(
        Func<string, HttpListenerContext, Task> handler,
        Func<string, Task> testBody)
    {
        int port = GetFreeTcpPort();
        string prefix = $"http://127.0.0.1:{port}/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var cts = new CancellationTokenSource();
        Task loop = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await listener.GetContextAsync();
                }
                catch
                {
                    break;
                }

                try
                {
                    string path = ctx.Request.Url?.AbsolutePath ?? "/";
                    await handler(path, ctx);
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        });

        try
        {
            await testBody($"http://127.0.0.1:{port}");
        }
        finally
        {
            cts.Cancel();
            listener.Stop();
            try { await loop; } catch { }
        }
    }

    private static async Task WriteResponseAsync(HttpListenerContext ctx, int statusCode, string contentType, byte[] body)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = body.Length;
        await ctx.Response.OutputStream.WriteAsync(body);
        ctx.Response.Close();
    }

    private static void ClearReadOnlyAttributes(string dir)
    {
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try
            {
                FileAttributes a = File.GetAttributes(f);
                if ((a & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(f, a & ~FileAttributes.ReadOnly);
                }
            }
            catch { }
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

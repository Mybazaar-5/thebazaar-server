using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BazaarHub.Models;

namespace BazaarHub.Services;

/// <summary>
/// 无头自动化自检运行器（供 BazaarHub.exe --self-test 调用，不创建任何 GUI 窗口）。
/// 在隔离的临时沙盒目录内逐项验证全部核心服务与文件往返一致性，全部通过返回 0，任一失败返回 1。
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
        Console.WriteLine("  BazaarHub 无头自动化自检 (--self-test)");
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
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandboxRoot))
                {
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
        GameInstallInfo info = locator.InspectGameDirectory(gameDir, "沙盒模拟");
        Assert(info.IsValidGameInstall, "应识别有效游戏本体");
        Assert(info.IsBepInExReady, "应识别 BepInEx 与插件均已就绪");

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

        const string originalCfg = """
## Settings file was created by plugin BazaarLauncher v2.0.0
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
ServerBase = http://127.0.0.1:8080

## 玩家名注释
PlayerName = OldCfgName

BypassSteam = true
UnlockEverything = true

[UnknownFutureSection]
# 未来版本新增的未知字段
CustomRuleJsonHash = abcdef123456
""";
        File.WriteAllText(cfgPath, originalCfg, Utf8NoBom);

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

        // 切换 2：自定义服 -> 纯净官方原版 -> 远程自建服
        LauncherConfigSnapshot snapCustom = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.CustomServer, "10.0.0.8:9090/", "星港大巴扎_测试员");
        Assert(snapCustom.ServerBase == "http://10.0.0.8:9090", $"自定义地址规范化失败：{snapCustom.ServerBase}");

        LauncherConfigSnapshot snapOfficial = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.PureOfficial, null, "星港大巴扎_测试员");
        Assert(!snapOfficial.Enabled, "纯净官方原版 Enabled 应为 false");
        Assert(snapOfficial.InferServerMode() == ServerMode.PureOfficial, "模式推断应为 PureOfficial");

        LauncherConfigSnapshot snapCloud = svc.ApplyServerAndPlayerSettings(
            gameDir, ServerMode.CloudTencent, null, "星港大巴扎_测试员");
        Assert(snapCloud.Enabled && snapCloud.ServerBase == LauncherConstants.CloudTencentUrl, "切回远程自建服失败");

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

        int port = GetFreeTcpPort();
        string prefix = $"http://127.0.0.1:{port}/";

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        using var serverCts = new CancellationTokenSource();
        Task serverLoop = Task.Run(async () =>
        {
            while (!serverCts.IsCancellationRequested)
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

                string path = ctx.Request.Url?.AbsolutePath ?? "/";
                byte[] respBytes;
                string contentType = "application/json; charset=utf-8";
                int status = 200;

                if (path == "/healthz")
                {
                    respBytes = Utf8NoBom.GetBytes("{\"ok\":true,\"uptime\":123}");
                }
                else if (path == "/api/ping")
                {
                    respBytes = Utf8NoBom.GetBytes("{\"ok\":true,\"onlinePlayers\":7,\"serverVersion\":\"13.0.0\"}");
                }
                else if (path == "/api/client-update/manifest")
                {
                    var manifestObj = new
                    {
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
                                relativeTargetPath = @"BepInEx\plugins\BazaarLauncher.dll",
                                downloadUrl = "/downloads/BazaarLauncher.dll",
                                sha256 = pluginSha,
                                sizeBytes = pluginBytes.Length
                            }
                        }
                    };
                    respBytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(manifestObj));
                }
                else if (path == "/downloads/BazaarLauncher.dll")
                {
                    contentType = "application/octet-stream";
                    respBytes = pluginBytes;
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
                    respBytes = Utf8NoBom.GetBytes(JsonSerializer.Serialize(modsObj));
                }
                else if (path == $"/api/mods/assets/{modPngSha}")
                {
                    contentType = "image/png";
                    respBytes = modPngBytes;
                }
                else if (path == $"/api/mods/assets/{tamperedSha}")
                {
                    contentType = "image/png";
                    respBytes = Utf8NoBom.GetBytes("TAMPERED_WRONG_BYTES");
                }
                else if (path == "/api/localization/overlay")
                {
                    respBytes = overlayBytes;
                }
                else
                {
                    status = 404;
                    respBytes = Utf8NoBom.GetBytes("{\"error\":\"not found\"}");
                }

                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = respBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(respBytes);
                ctx.Response.Close();
            }
        });

        try
        {
            string baseUrl = $"http://127.0.0.1:{port}";

            // 验证探针
            using var probe = new ServerProbeService();
            ServerProbeResult probeRes = await probe.ProbeAsync(baseUrl);
            Assert(probeRes.IsOnline && probeRes.HealthzOk && probeRes.PingEndpointSupported, "模拟服探针应全部通过");
            Assert(probeRes.OnlinePlayers == 7, "在线人数解析应为 7");

            using var sync = new UpdateSyncService();

            // 第一次全量增量同步（应下载 3 项：DLL + 汉化 + Mod PNG）
            UpdateSyncReport first = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true);
            Assert(first.Success, $"首次同步应成功：{first.SummaryMessage}");
            Assert(first.DownloadedCount == 3, $"首次应下载 3 项，实际 {first.DownloadedCount}");
            Assert(first.SkippedUpToDateCount == 0, $"首次跳过应为 0，实际 {first.SkippedUpToDateCount}");

            string localPlugin = Path.Combine(gameDir, @"BepInEx\plugins\BazaarLauncher.dll");
            string localOverlay = Path.Combine(gameDir, LauncherConstants.ZhOverlayRelativePath);
            string localAsset = Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "assets", modPngSha + ".png");

            Assert(UpdateSyncService.ComputeFileSha256(localPlugin) == pluginSha, "同步后的插件 SHA-256 不一致");
            Assert(UpdateSyncService.ComputeFileSha256(localOverlay) == overlaySha, "同步后的汉化词典 SHA-256 不一致");
            Assert(UpdateSyncService.ComputeFileSha256(localAsset) == modPngSha, "同步后的 Mod PNG SHA-256 不一致");
            Assert(!Directory.EnumerateFiles(gameDir, "*.tmp", SearchOption.AllDirectories).Any(), "同步完成后不应残留 .tmp 文件");

            // 第二次同步（应全部命中 SHA-256 缓存，0 下载、3 跳过）
            UpdateSyncReport second = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true);
            Assert(second.Success, "二次增量同步应成功");
            Assert(second.DownloadedCount == 0, $"二次应下载 0 项，实际 {second.DownloadedCount}");
            Assert(second.SkippedUpToDateCount == 3, $"二次应跳过 3 项，实际 {second.SkippedUpToDateCount}");

            // 第三次同步：注入篡改资产，验证 SHA-256 防护拒绝写入且清理 .tmp
            serveTamperedAsset = true;
            UpdateSyncReport third = await sync.SyncAsync(gameDir, baseUrl, applyDownloads: true);
            Assert(!third.Success && third.FailedCount == 1, "篡改资产必须被 SHA-256 校验拦截并记为失败");
            string corruptedTarget = Path.Combine(gameDir, LauncherConstants.ModCacheRelativeDir, "assets", tamperedSha + ".png");
            Assert(!File.Exists(corruptedTarget), "被篡改资产绝不应落盘");
            Assert(!File.Exists(corruptedTarget + ".tmp"), "被篡改资产的 .tmp 必须自动清理");

            // 第四次同步：断网不可达端口优雅降级
            int closedPort = GetFreeTcpPort();
            using var fastHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var offlineSync = new UpdateSyncService(fastHttp);
            UpdateSyncReport offline = await offlineSync.SyncAsync(gameDir, $"http://127.0.0.1:{closedPort}", applyDownloads: true);
            Assert(offline.Success && offline.DegradedOffline, "断网不可达时应返回优雅降级状态而非崩退");
        }
        finally
        {
            serverCts.Cancel();
            listener.Stop();
            try { await serverLoop; } catch { }
        }
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

        // 校验调优后内容
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

        // 二次调优：验证首次原始备份绝不被覆写
        string bootBackupPath = Path.Combine(PerformanceTunerService.GetOriginalBackupDir(gameDir), LauncherConstants.BootConfigRelativePath);
        string backupShaBefore = UpdateSyncService.ComputeFileSha256(bootBackupPath);

        opt1.GcMaxTimeSliceMs = 4;
        PerformanceOperationResult applyRes2 = await tuner.ApplyAsync(gameDir, opt1);
        Assert(applyRes2.Success && applyRes2.BackedUpFiles.Count == 0, "二次调优不得重复覆盖原版备份");
        Assert(UpdateSyncService.ComputeFileSha256(bootBackupPath) == backupShaBefore, "原版备份哈希被非法篡改");

        // 逐字节还原 round-trip 校验
        PerformanceOperationResult restoreOnly = await tuner.RestoreOriginalFilesOnlyAsync(gameDir);
        Assert(restoreOnly.Success && restoreOnly.RestoredFiles.Count == 4, "应还原全部 4 份原始文件");
        Assert(File.ReadAllBytes(bootPath).SequenceEqual(origBootBytes), "boot.config 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(aaPath).SequenceEqual(origAaBytes), "aa/settings.json 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(bepPath).SequenceEqual(origBepBytes), "BepInEx.cfg 还原后与原版字节不一致");
        Assert(File.ReadAllBytes(launcherPath).SequenceEqual(origLauncherBytes), "local.thebazaar.bazaarlauncher.cfg 还原后与原版字节不一致");

        // 再测试一键还原纯净官方原版（停用插件 Enabled=false）
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

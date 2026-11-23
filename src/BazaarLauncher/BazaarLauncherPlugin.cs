using System;
using BazaarLauncher.Core;
using BazaarLauncher.Modules;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 《The Bazaar》BazaarModKit 客户端模块化宿主入口（重构修复 P0-2 / P0-6 / P2-5）：
///   - 保持全部既有 BepInEx 配置键与默认 ServerBase 兼容；
///   - 通过 ModuleHost 崩溃隔离沙箱逐一装载 14 个内置模块（每模块独立 SafeHarmonyPatcher + 熔断保护 + 耗时统计）；
///   - 自动扫描 BepInEx/plugins/BazaarMods/*.dll 与 BepInEx/mods/ 热加载外部第三方 IBazaarClientMod 扩展；
///   - OnDestroy 统一回收全部 Harmony 补丁、HTTP 取消令牌与运行时宿主对象，零僵尸补丁泄漏。
/// </summary>
[BepInPlugin(Guid, "BazaarLauncher", "15.4.0")]
public sealed class BazaarLauncherPlugin : BaseUnityPlugin
{
    public const string Guid = "local.thebazaar.bazaarlauncher";

    internal static ManualLogSource Log;
    internal static ModuleHost Host { get; private set; }

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<string> ServerBase;
    internal static ConfigEntry<bool> AcceptServerMods;
    internal static ConfigEntry<string> PlayerName;
    internal static ConfigEntry<bool> BypassSteam;
    internal static ConfigEntry<bool> UnlockEverything;
    internal static ConfigEntry<bool> DumpTraffic;
    internal static ConfigEntry<string> DumpDir;
    internal static ConfigEntry<bool> FastForwardFirstFight;
    internal static ConfigEntry<bool> FastForwardUserExplicitlySet;
    internal static ConfigEntry<int> MaxForegroundFps;
    internal static ConfigEntry<int> MenuFpsLimit;
    internal static ConfigEntry<int> BackgroundFps;
    internal static ConfigEntry<bool> LazyLoadCosmetics;
    internal static ConfigEntry<bool> MigratedV12_1Defaults;

    // 诊断与安全配置（默认安全关闭，彻底修复 P0-2 / P1-5）
    internal static ConfigEntry<bool> EnableGameDriver;
    internal static ConfigEntry<int> GameDriverPort;
    internal static ConfigEntry<bool> RequireGameDriverToken;
    internal static ConfigEntry<string> GameDriverToken;
    internal static ConfigEntry<bool> EnableCombatAudit;

    private void Awake()
    {
        Log = Logger;

        BindAllConfigEntries();

        if (!Enabled.Value)
        {
            Log.LogInfo("BazaarLauncher 已通过配置关闭，将完全按官方原版行为运行。");
            return;
        }

        HttpClientHub.ResetForStartup();
        MainThreadDispatcher.EnsureInstalled();

        Host = new ModuleHost();

        // 1. 优先实例化并注入核心宿主服务（UI 嵌入、卡图覆盖、四层本地化、性能遥测），供所有模块与第三方 Mod 使用
        var perfModule = new PerfOptimizerModule();
        var visualModule = new CustomContentVisualModule();
        var l10nModule = new LocalizationOverlayModule();
        var nativeUiModule = new NativeMenuEmbedModule();

        Host.Telemetry = perfModule;
        Host.VisualRegistry = visualModule;
        Host.LocalizationRegistry = l10nModule;
        Host.UiHost = nativeUiModule;

        // 2. 按职责顺序在独立崩溃隔离沙箱中加载全部 14 个内置模块（任一模块异常绝不拖垮其他模块）
        Host.RegisterAndLoad(new NetworkRedirectModule(), enabledOverride: true);
        Host.RegisterAndLoad(new OfflineAuthModule(), enabledOverride: true);
        Host.RegisterAndLoad(l10nModule);
        Host.RegisterAndLoad(perfModule);
        Host.RegisterAndLoad(visualModule);
        Host.RegisterAndLoad(nativeUiModule);
        Host.RegisterAndLoad(new CollectionUnlockModule(), enabledOverride: UnlockEverything.Value);
        Host.RegisterAndLoad(new CombatSyncModule());
        Host.RegisterAndLoad(new CompatGameSimModule());
        Host.RegisterAndLoad(new CompatHintEnchantModule());
        Host.RegisterAndLoad(new CompatEncounterAssetModule());
        Host.RegisterAndLoad(new WorkshopUiModule());
        Host.RegisterAndLoad(new DiagInspectorModule());
        Host.RegisterAndLoad(new DiagGameDriverModule(), enabledOverride: GameDriver.ShouldAutoEnable());
        Host.RegisterAndLoad(new DiagTrafficDumpModule(), enabledOverride: DumpTraffic.Value);

        // 3. 扫描并热加载 BepInEx/plugins/BazaarMods/*.dll 与 BepInEx/mods/ 下的第三方扩展 Mod
        int extCount = Host.DiscoverAndLoadExternalMods();

        // 4. 异步预拉取服务端 Mod 清单、PNG 资产缓存与四层中文词典
        ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: false);

        Log.LogInfo(
            $"BazaarLauncher 15.4.0 (BazaarModKit) 启动就绪 -> {ServerBase.Value} | 玩家={PlayerIdentity.ResolveName()} | 外部扩展={extCount}");
    }

    private void OnDestroy()
    {
        try { Host?.ShutdownAll(); } catch { }
        try { HttpClientHub.Shutdown(); } catch { }
        try { MainThreadDispatcher.Shutdown(); } catch { }
        Host = null;
    }

    private void BindAllConfigEntries()
    {
        Enabled = Config.Bind("General", "Enabled", true,
            "总开关。false = 完全按原版行为运行（连官方服）。");
        ServerBase = Config.Bind("General", "ServerBase", "http://127.0.0.1:8080",
            "自建服基址。单机默认 http://127.0.0.1:8080；远程自建服请改为 http://<你的服务器IP>:8080");
        AcceptServerMods = Config.Bind("General", "AcceptServerMods", true,
            "是否允许接收当前服务器下发的 Mod 贴图与文案（关闭后不加载服务器下发的 Mod 资源）。");
        PlayerName = Config.Bind("General", "PlayerName", "",
            "玩家名（账号名）。留空则读游戏目录 player-name.txt，再退回系统用户名。");
        BypassSteam = Config.Bind("General", "BypassSteam", true,
            "摘掉 Steam 依赖：不需要 Steam 客户端与正版票据。");
        UnlockEverything = Config.Bind("General", "UnlockEverything", true,
            "解锁全部英雄 / 皮肤 / 藏品 / 赛季奖励。");

        FastForwardFirstFight = Config.Bind("Combat", "FastForwardFirstFight", true,
            "首场战斗（Day 1）默认开启 3x 加速；在游戏选项中手动关闭后将同步记忆。");
        FastForwardUserExplicitlySet = Config.Bind("Combat", "FastForwardUserExplicitlySet", false,
            "内部标记：玩家是否已在游戏选项中显式修改过首战加速开关。");

        MaxForegroundFps = Config.Bind("Performance", "MaxForegroundFps", 60,
            "前台游玩帧率上限（建议 60 省电稳帧；支持 60/90/120，0 = 不额外限制跟随游戏原版）。");
        MenuFpsLimit = Config.Bind("Performance", "MenuFpsLimit", 60,
            "主大厅界面省电帧率上限（0 = 跟随前台上限）。");
        BackgroundFps = Config.Bind("Performance", "BackgroundFps", 20,
            "游戏窗口失去焦点切到后台时的降频帧率（默认 20 FPS 释放 GPU，0 = 不降频）。");
        LazyLoadCosmetics = Config.Bind("Performance", "LazyLoadCosmetics", true,
            "按需懒加载藏品：启动时仅加载当前穿戴装扮，跳过未穿戴 3D 藏品全量预加载，打开藏品柜时按需加载。");
        MigratedV12_1Defaults = Config.Bind("CustomGameplay", "MigratedV12_1Defaults", false,
            "内部标记：是否已完成 V12.1 野怪多选一与开局倾向默认生效迁移。");

        EnableGameDriver = Config.Bind("Diagnostics", "EnableGameDriver", false,
            "是否开启本地 HTTP 自动化驱动控制面（默认关闭保障生产安全；设置环境变量 BAZAAR_DRIVER_ENABLE=1 亦可临时启用）。");
        GameDriverPort = Config.Bind("Diagnostics", "GameDriverPort", 27182,
            "本地 HTTP 自动化驱动监听端口（仅绑定 127.0.0.1）。");
        RequireGameDriverToken = Config.Bind("Diagnostics", "RequireGameDriverToken", false,
            "启用驱动服务时是否强制随机生成并校验 X-Bazaar-Token（写入 BepInEx/config/bazaar_driver.token）。");
        GameDriverToken = Config.Bind("Diagnostics", "GameDriverToken", "",
            "可选固定驱动访问 Token；非空时所有本地驱动请求必须携带 X-Bazaar-Token 或 Authorization: Bearer。");
        EnableCombatAudit = Config.Bind("Diagnostics", "EnableCombatAudit", false,
            "是否记录每场战斗全帧触发明细审计（默认关闭以消除生产进战 GC 开销）。");

        DumpTraffic = Config.Bind("Debug", "DumpTraffic", false,
            "把 /sessions、/commands 的原始字节落盘（差分测试用）。");
        DumpDir = Config.Bind("Debug", "DumpDir", "",
            "抓包输出目录。");
    }

    // ── 运行时切换服务器地址与接收开关 ────────────────────────────────────────

    internal static bool TrySwitchServerBase(string rawUrl, out string normalizedUrl, out string error)
    {
        normalizedUrl = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "服务器地址不能为空";
            return false;
        }
        string trimmed = rawUrl.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            error = "服务器地址格式不正确（示例：http://127.0.0.1:8080）";
            return false;
        }

        normalizedUrl = trimmed;
        if (ServerBase != null)
        {
            ServerBase.Value = normalizedUrl;
            try { ServerBase.ConfigFile?.Save(); } catch { }
        }
        if (Enabled != null && !Enabled.Value)
        {
            Enabled.Value = true;
            try { Enabled.ConfigFile?.Save(); } catch { }
        }

        // 完整热切服事务：1) 重设游戏原生 TheBazaar.Config 五大端点；2) 清空旧服 JWT/账号缓存并触发新服鉴权；
        // 3) 清空旧服 Mod 与 server_overlay 并拉取新服清单；4) 重置 CustomGameplayUI 远端状态与 SSE 订阅。
        try { ApplyUrls(GameInspector.FindTypeSafe("TheBazaar.Config")); } catch { }
        try { NameAuth.OnServerChanged(); } catch { }
        ModContentSyncClient.Instance.OnServerChanged();
        MainThreadDispatcher.Enqueue(() =>
        {
            try { CustomGameplayUI.Instance?.OnServerBaseSwitched(); } catch { }
        });
        return true;
    }

    internal static void SetAcceptServerMods(bool accept)
    {
        if (AcceptServerMods != null)
        {
            AcceptServerMods.Value = accept;
            try { AcceptServerMods.ConfigFile?.Save(); } catch { }
        }
        if (!accept)
        {
            ModContentSyncClient.Instance.ClearSyncedServerMods();
        }
        else
        {
            ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: false);
        }
    }

    // ── URL 覆盖（由 Core.NetworkRedirectModule 沙箱化调用）───────────────────

    internal static void ApplyServerUrlPatches(SafeHarmonyPatcher patcher)
    {
        var t = AccessTools.TypeByName("TheBazaar.Config");
        if (t == null)
        {
            Log?.LogError("未找到 TheBazaar.Config，游戏结构可能已变更。");
            return;
        }

        patcher.TryPatch(
            t,
            "SetupCommandArgs",
            postfix: AccessTools.Method(typeof(BazaarLauncherPlugin), nameof(AfterSetupCommandArgs)),
            label: "TheBazaar.Config.SetupCommandArgs");

        ApplyUrls(t);
    }

    private static void AfterSetupCommandArgs()
    {
        try
        {
            ApplyUrls(GameInspector.FindTypeSafe("TheBazaar.Config"));
        }
        catch (Exception ex)
        {
            Log?.LogWarning("[BazaarLauncher] AfterSetupCommandArgs 异常已拦截: " + ex.Message);
        }
    }

    private static void ApplyUrls(Type t)
    {
        if (t == null) return;
        string baseUrl = (ServerBase?.Value ?? string.Empty).TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl)) return;

        foreach (var prop in new[] { "NetURL", "SocketURL", "TournamentURL", "DataURL", "MaintenanceDataURL" })
        {
            SetStatic(t, prop, baseUrl);
        }
        Log?.LogInfo($"服务器端点已重定向 -> {baseUrl}");
    }

    private static void SetStatic(Type t, string prop, string value)
    {
        var p = AccessTools.Property(t, prop);
        if (p != null && p.CanWrite) { p.SetValue(null, value); return; }
        AccessTools.Field(t, "<" + prop + ">k__BackingField")?.SetValue(null, value);
    }
}

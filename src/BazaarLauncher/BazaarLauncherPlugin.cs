using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 把原版客户端改造成「单文件分发包即可玩」的 BepInEx 插件。
///
/// 做四件事：
///   1. 摘掉 Steam：不用 Steam 客户端、不用正版票据
///   2. 用户名即账号：输入名字就能进，服务端按名字建号
///   3. 指向自建服：Config.{NetURL, SocketURL, TournamentURL, DataURL, MaintenanceDataURL}
///   4. 全解锁：所有英雄 / 皮肤 / 藏品 / 赛季奖励全部视为已拥有
///
/// 配置文件：BepInEx/config/bazaarlauncher.cfg
/// </summary>
[BepInPlugin(Guid, "BazaarLauncher", "2.0.0")]
public sealed class BazaarLauncherPlugin : BaseUnityPlugin
{
    public const string Guid = "local.thebazaar.bazaarlauncher";

    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<string> ServerBase;
    internal static ConfigEntry<string> PlayerName;
    internal static ConfigEntry<bool> BypassSteam;
    internal static ConfigEntry<bool> UnlockEverything;
    internal static ConfigEntry<bool> DumpTraffic;
    internal static ConfigEntry<string> DumpDir;
    internal static ConfigEntry<bool> FastForwardFirstFight;
    internal static ConfigEntry<bool> FastForwardUserExplicitlySet;
    internal static ConfigEntry<int> MaxForegroundFps;
    internal static ConfigEntry<int> BackgroundFps;
    internal static ConfigEntry<bool> LazyLoadCosmetics;
    internal static ConfigEntry<bool> MigratedV12_1Defaults;

    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;

        Enabled = Config.Bind("General", "Enabled", true,
            "总开关。false = 完全按原版行为运行（连官方服）。");
        ServerBase = Config.Bind("General", "ServerBase", "http://127.0.0.1:8080",
            "自建服基址。单机默认 http://127.0.0.1:8080；远程自建服请改为 http://<你的服务器IP>:8080");
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
        BackgroundFps = Config.Bind("Performance", "BackgroundFps", 20,
            "游戏窗口失去焦点切到后台时的降频帧率（默认 20 FPS 释放 GPU，0 = 不降频）。");
        LazyLoadCosmetics = Config.Bind("Performance", "LazyLoadCosmetics", true,
            "按需懒加载藏品：启动时仅加载当前穿戴装扮，跳过 396 套未穿戴 3D 藏品全量预加载，打开藏品柜时按需加载。");
        MigratedV12_1Defaults = Config.Bind("CustomGameplay", "MigratedV12_1Defaults", false,
            "内部标记：是否已完成 V12.1 野怪多选一与开局倾向默认生效迁移。");

        DumpTraffic = Config.Bind("Debug", "DumpTraffic", false,
            "把 /sessions、/commands 的原始字节落盘（差分测试用）。");
        DumpDir = Config.Bind("Debug", "DumpDir", "",
            "抓包输出目录。");

        if (!Enabled.Value)
        {
            Log.LogInfo("Disabled by config; original behavior preserved.");
            return;
        }

        _harmony = new Harmony(Guid);

        CloudGuard.Apply(_harmony);
        PatchConfig();
        if (BypassSteam.Value) NoSteam.Apply(_harmony);
        NameAuth.Apply(_harmony);
        if (UnlockEverything.Value) Unlocker.Apply(_harmony);
        if (DumpTraffic.Value) PatchTraffic();
        CombatPatch.Apply(_harmony);
        CompatAuditPatch.Apply(_harmony);
        TournamentLocalizationPatch.Apply(_harmony);
        FrameRateGuard.EnsureInstalled(_harmony);
        GameDriver.EnsureInstalled();
        CustomGameplayUI.EnsureInstalled();

        Log.LogInfo($"BazaarLauncher 2.0.0 ready -> {ServerBase.Value} (player={PlayerIdentity.ResolveName()})");
    }

    private void OnDestroy() => _harmony?.UnpatchSelf();

    // ── URL 覆盖 ────────────────────────────────────────────────────────────
    private void PatchConfig()
    {
        var t = AccessTools.TypeByName("TheBazaar.Config");
        if (t == null)
        {
            Log.LogError("TheBazaar.Config not found — game layout changed?");
            return;
        }

        var setup = AccessTools.Method(t, "SetupCommandArgs");
        if (setup != null)
        {
            _harmony.Patch(setup, postfix: new HarmonyMethod(
                AccessTools.Method(typeof(BazaarLauncherPlugin), nameof(AfterSetupCommandArgs))));
            Log.LogInfo("Patched TheBazaar.Config.SetupCommandArgs");
        }
        ApplyUrls(t);
    }

    private static void AfterSetupCommandArgs()
        => ApplyUrls(AccessTools.TypeByName("TheBazaar.Config"));

    private static void ApplyUrls(Type t)
    {
        string baseUrl = ServerBase.Value.TrimEnd('/');
        foreach (var prop in new[] { "NetURL", "SocketURL", "TournamentURL", "DataURL", "MaintenanceDataURL" })
        {
            SetStatic(t, prop, baseUrl);
        }
        Log.LogInfo($"URLs overridden -> {baseUrl}");
    }

    private static void SetStatic(Type t, string prop, string value)
    {
        var p = AccessTools.Property(t, prop);
        if (p != null && p.CanWrite) { p.SetValue(null, value); return; }
        AccessTools.Field(t, "<" + prop + ">k__BackingField")?.SetValue(null, value);
    }

    // ── 流量落盘（HttpClient 层，非泛型可 patch）────────────────────────────
    private void PatchTraffic() => TrafficDump.Apply(_harmony);
}





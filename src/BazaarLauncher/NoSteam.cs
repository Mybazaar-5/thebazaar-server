using System;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 摘掉 Steam 依赖。
///
/// 原版启动链：
///   AppLoader → GameInstance.InitializeSteam() → SteamModule.Initialize()
///                → SteamAPI.Init() → GetAuthTicketForWebApi() → 票据
///   AppLoader → InitializeAuthService() → SteamAuthService.TryAutoLoginAsync()
///                → GetSteamTicket() → POST /api/auth/login/silent
///
/// 这里：
///   · SteamModule.Initialize 整体短路，直接置 Initialized / IsAuthenticated = true
///   · GetSteamTicket 返回按玩家名派生的稳定伪票据
///   · 其余 Steam API 调用全部安全桩
///
/// 注意：所有 prefix 补丁都必须返回一个「已完成的任务」，否则 AppLoader 的
/// DependsOn 链会把 Auth Service 判为 "Skipped due to failed dependency"。
/// </summary>
internal static class NoSteam
{
    private static bool _applied;

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;

        var steamModule = AccessTools.TypeByName("TheBazaar.Steam.SteamModule");
        if (steamModule == null)
        {
            BazaarLauncherPlugin.Log.LogError("TheBazaar.Steam.SteamModule not found — cannot bypass Steam.");
            return;
        }

        PatchTaskMethod(harmony, steamModule, "Initialize", nameof(SkipInitialize));
        PatchTaskMethod(harmony, steamModule, "RegisterWithUpdater", nameof(NoOpTask));

        var shutdown = AccessTools.Method(steamModule, "Shutdown");
        if (shutdown != null)
        {
            harmony.Patch(shutdown, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(SkipShutdown))));
        }

        var tick = AccessTools.Method(steamModule, "Tick");
        if (tick != null)
        {
            harmony.Patch(tick, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid))));
        }

        var getTicket = AccessTools.Method(steamModule, "GetSteamTicket");
        if (getTicket != null)
        {
            harmony.Patch(getTicket, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(FakeTicket))));
            BazaarLauncherPlugin.Log.LogInfo("Patched SteamModule.GetSteamTicket (fake ticket)");
        }

        // DLC 服务：不再轮询 SteamApps
        var dlc = AccessTools.TypeByName("TheBazaar.Steam.SteamDLCService");
        if (dlc != null)
        {
            var dlcInit = AccessTools.Method(dlc, "Initialize");
            if (dlcInit != null)
            {
                harmony.Patch(dlcInit, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid))));
                BazaarLauncherPlugin.Log.LogInfo("Patched SteamDLCService.Initialize (no-op)");
            }

            var isInstalled = AccessTools.Method(dlc, "IsInstalled");
            if (isInstalled != null)
            {
                harmony.Patch(isInstalled, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(NoSteam), nameof(ReturnTrue))));
            }
        }

        PatchSteamStatics(harmony);
    }

    private static void PatchTaskMethod(Harmony harmony, Type type, string methodName, string patchName)
    {
        var m = AccessTools.Method(type, methodName);
        if (m == null)
        {
            BazaarLauncherPlugin.Log.LogWarning($"{type.Name}.{methodName} not found");
            return;
        }
        harmony.Patch(m, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(NoSteam), patchName)));
        BazaarLauncherPlugin.Log.LogInfo($"Patched {type.Name}.{methodName}");
    }

    private static void PatchSteamStatics(Harmony harmony)
    {
        var utils = AccessTools.TypeByName("Steamworks.SteamUtils");
        if (utils != null)
        {
            foreach (var name in new[] { "IsSteamRunningOnSteamDeck", "IsOverlayEnabled", "IsSteamInBigPictureMode" })
            {
                var m = AccessTools.Method(utils, name);
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(NoSteam), nameof(ReturnFalse))));
            }
            BazaarLauncherPlugin.Log.LogInfo("Patched SteamUtils queries");
        }

        var apps = AccessTools.TypeByName("Steamworks.SteamApps");
        if (apps != null)
        {
            foreach (var name in new[] { "BIsDlcInstalled", "BIsSubscribedApp" })
            {
                var m = AccessTools.Method(apps, name);
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(NoSteam), nameof(ReturnTrue))));
            }
            var dlcCount = AccessTools.Method(apps, "GetDLCCount");
            if (dlcCount != null) harmony.Patch(dlcCount, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(ZeroInt))));
        }

        var friends = AccessTools.TypeByName("Steamworks.SteamFriends");
        if (friends != null)
        {
            foreach (var name in new[] { "ActivateGameOverlay", "ActivateGameOverlayToStore",
                                          "ActivateGameOverlayToWebPage", "ActivateGameOverlayToUser" })
            {
                var m = AccessTools.Method(friends, name);
                if (m != null) harmony.Patch(m, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid))));
            }
        }

        var client = AccessTools.TypeByName("Steamworks.SteamClient");
        if (client != null)
        {
            var hook = AccessTools.Method(client, "SetWarningMessageHook");
            if (hook != null) harmony.Patch(hook, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid))));
        }

        var steamUser = AccessTools.TypeByName("Steamworks.SteamUser");
        if (steamUser != null)
        {
            var getSteamId = AccessTools.Method(steamUser, "GetSteamID");
            if (getSteamId != null) harmony.Patch(getSteamId, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(NoSteam), nameof(FakeSteamId))));
        }
    }

    // ── 补丁体 ──────────────────────────────────────────────────────────────

    /// <summary>SteamModule.Initialize：不碰 SteamAPI，直接置状态并返回已完成任务。</summary>
    private static bool SkipInitialize(object __instance, ref Task __result)
    {
        try
        {
            SetBacking(__instance.GetType(), __instance, "Initialized", true);
            SetBacking(__instance.GetType(), __instance, "IsAuthenticated", true);
            BazaarLauncherPlugin.Log.LogInfo("Steam bypassed: Initialized=true, IsAuthenticated=true");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogError("SkipInitialize: " + ex);
        }
        __result = Task.CompletedTask;   // 关键：必须给一个已完成的 Task
        return false;
    }

    private static bool NoOpTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool SkipShutdown(object __instance)
    {
        try { SetBacking(__instance.GetType(), __instance, "Initialized", false); }
        catch { }
        return false;
    }

    /// <summary>返回按玩家名派生的稳定伪票据。</summary>
    private static bool FakeTicket(ref object __result)
    {
        try
        {
            string name = PlayerIdentity.ResolveName();
            string ticket = PlayerIdentity.FakeSteamTicket(name);
            var ticketType = AccessTools.TypeByName("TheBazaar.Steam.SteamModule+SteamTicket");
            __result = ticketType != null
                ? Activator.CreateInstance(ticketType, new object[] { ticket })
                : null;
            BazaarLauncherPlugin.Log.LogInfo(
                $"Fake Steam ticket for '{name}': {ticket.Substring(0, Math.Min(24, ticket.Length))}…");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogError("FakeTicket: " + ex);
        }
        return false;
    }

    private static bool FakeSteamId(ref object __result)
    {
        try
        {
            var csteamId = AccessTools.TypeByName("Steamworks.CSteamID");
            if (csteamId != null)
            {
                __result = Activator.CreateInstance(csteamId,
                    new object[] { PlayerIdentity.StableSteamId() });
            }
        }
        catch { }
        return false;
    }

    private static bool NoOpVoid() => false;
    private static bool ReturnFalse(ref bool __result) { __result = false; return false; }
    private static bool ReturnTrue(ref bool __result) { __result = true; return false; }
    private static bool ZeroInt(ref int __result) { __result = 0; return false; }

    private static void SetBacking(Type t, object instance, string propName, object value)
    {
        var f = AccessTools.Field(t, "<" + propName + ">k__BackingField");
        if (f != null) { f.SetValue(instance, value); return; }
        var p = AccessTools.Property(t, propName);
        if (p != null && p.CanWrite) p.SetValue(instance, value);
    }
}

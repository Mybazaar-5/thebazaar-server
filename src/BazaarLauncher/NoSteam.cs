using System;
using System.Reflection;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using HarmonyLib;

namespace BazaarLauncher;

/// <summary>
/// 摘掉 Steam 依赖（修复 P1-6 值类型 CSteamID 拆箱 NRE 风险与热重载状态清理）。
/// </summary>
internal static class NoSteam
{
    private static bool _applied;
    private static Type _cachedCSteamIdType;
    private static object _cachedDefaultCSteamId;
    private static Type _cachedSteamTicketType;

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        var steamModule = AccessTools.TypeByName("TheBazaar.Steam.SteamModule");
        if (steamModule == null)
        {
            BazaarLauncherPlugin.Log?.LogError("NoSteam: 未找到 TheBazaar.Steam.SteamModule，无法绕过 Steam");
            return;
        }

        _cachedSteamTicketType = AccessTools.TypeByName("TheBazaar.Steam.SteamModule+SteamTicket");
        _cachedCSteamIdType = AccessTools.TypeByName("Steamworks.CSteamID");
        if (_cachedCSteamIdType != null && _cachedCSteamIdType.IsValueType)
        {
            try
            {
                _cachedDefaultCSteamId = Activator.CreateInstance(_cachedCSteamIdType, new object[] { PlayerIdentity.StableSteamId() })
                    ?? Activator.CreateInstance(_cachedCSteamIdType);
            }
            catch
            {
                try { _cachedDefaultCSteamId = Activator.CreateInstance(_cachedCSteamIdType); } catch { }
            }
        }

        patcher.TryPatch(steamModule, "Initialize", prefix: AccessTools.Method(typeof(NoSteam), nameof(SkipInitialize)));
        patcher.TryPatch(steamModule, "RegisterWithUpdater", prefix: AccessTools.Method(typeof(NoSteam), nameof(NoOpTask)));
        patcher.TryPatch(steamModule, "Shutdown", prefix: AccessTools.Method(typeof(NoSteam), nameof(SkipShutdown)));
        patcher.TryPatch(steamModule, "Tick", prefix: AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid)));
        patcher.TryPatch(steamModule, "GetSteamTicket", prefix: AccessTools.Method(typeof(NoSteam), nameof(FakeTicket)));

        var dlc = AccessTools.TypeByName("TheBazaar.Steam.SteamDLCService");
        if (dlc != null)
        {
            patcher.TryPatch(dlc, "Initialize", prefix: AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid)), optional: true);
            patcher.TryPatch(dlc, "IsInstalled", prefix: AccessTools.Method(typeof(NoSteam), nameof(ReturnTrue)), optional: true);
        }

        PatchSteamStatics(patcher);
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        Apply(new SafeHarmonyPatcher("nosteam.legacy"));
    }

    private static void PatchSteamStatics(SafeHarmonyPatcher patcher)
    {
        var utils = AccessTools.TypeByName("Steamworks.SteamUtils");
        if (utils != null)
        {
            foreach (var name in new[] { "IsSteamRunningOnSteamDeck", "IsOverlayEnabled", "IsSteamInBigPictureMode" })
            {
                patcher.TryPatch(utils, name, prefix: AccessTools.Method(typeof(NoSteam), nameof(ReturnFalse)), optional: true);
            }
        }

        var apps = AccessTools.TypeByName("Steamworks.SteamApps");
        if (apps != null)
        {
            foreach (var name in new[] { "BIsDlcInstalled", "BIsSubscribedApp" })
            {
                patcher.TryPatch(apps, name, prefix: AccessTools.Method(typeof(NoSteam), nameof(ReturnTrue)), optional: true);
            }
            patcher.TryPatch(apps, "GetDLCCount", prefix: AccessTools.Method(typeof(NoSteam), nameof(ZeroInt)), optional: true);
        }

        var friends = AccessTools.TypeByName("Steamworks.SteamFriends");
        if (friends != null)
        {
            foreach (var name in new[]
            {
                "ActivateGameOverlay",
                "ActivateGameOverlayToStore",
                "ActivateGameOverlayToWebPage",
                "ActivateGameOverlayToUser",
            })
            {
                patcher.TryPatch(friends, name, prefix: AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid)), optional: true);
            }
        }

        var client = AccessTools.TypeByName("Steamworks.SteamClient");
        if (client != null)
        {
            patcher.TryPatch(client, "SetWarningMessageHook", prefix: AccessTools.Method(typeof(NoSteam), nameof(NoOpVoid)), optional: true);
        }

        // 仅在能够确保产出合法值类型 CSteamID 时才挂载 GetSteamID 补丁，杜绝 P1-6 拆箱 NRE
        var steamUser = AccessTools.TypeByName("Steamworks.SteamUser");
        if (steamUser != null && _cachedDefaultCSteamId != null)
        {
            patcher.TryPatch(steamUser, "GetSteamID", Type.EmptyTypes, prefix: AccessTools.Method(typeof(NoSteam), nameof(FakeSteamId)), optional: true);
        }
    }

    private static bool SkipInitialize(object __instance, ref Task __result)
    {
        try
        {
            SetBacking(__instance.GetType(), __instance, "Initialized", true);
            SetBacking(__instance.GetType(), __instance, "IsAuthenticated", true);
            BazaarLauncherPlugin.Log?.LogInfo("NoSteam: 已完成 Steam 离线旁路初始化");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogError("NoSteam SkipInitialize 失败: " + ex);
        }
        __result = Task.CompletedTask;
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

    private static bool FakeTicket(ref object __result)
    {
        try
        {
            string name = PlayerIdentity.ResolveName();
            string ticket = PlayerIdentity.FakeSteamTicket(name);
            _cachedSteamTicketType ??= AccessTools.TypeByName("TheBazaar.Steam.SteamModule+SteamTicket");
            __result = _cachedSteamTicketType != null
                ? Activator.CreateInstance(_cachedSteamTicketType, new object[] { ticket })
                : null;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogError("NoSteam FakeTicket 失败: " + ex);
        }
        return false;
    }

    private static bool FakeSteamId(ref object __result)
    {
        try
        {
            if (_cachedCSteamIdType != null)
            {
                object instance = Activator.CreateInstance(_cachedCSteamIdType, new object[] { PlayerIdentity.StableSteamId() });
                if (instance != null)
                {
                    __result = instance;
                    return false;
                }
            }
        }
        catch { }

        // 绝不将 null 写回值类型 __result，回退到预检缓存的合法值类型实例
        __result = _cachedDefaultCSteamId;
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

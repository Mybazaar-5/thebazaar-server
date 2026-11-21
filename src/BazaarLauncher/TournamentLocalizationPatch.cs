using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 锦标赛与奇遇禁用弹窗的简体中文补丁（Safe Harmony Postfix）。
///
/// 覆盖目标：
///   - EncounterBanPopupController（全部类型 / 商人 / 奇遇事件 / 野怪战斗 / 强化基座 / 复制禁用码 / 粘贴禁用码 / 最近禁用）
///   - TournamentHostSettingsView（创建房主参数界面文本）
///   - TournamentLobbyView（解散房间 / 离开房间 / 开始锦标赛 / 复制房间码等）
///   - 锦标赛大厅相关 LocalizableText 与子视图文本兜底
///
/// 所有反射查找均做空值防护并在缺失时仅记录一次日志，绝不抛异常阻断游戏流程。
/// </summary>
internal static class TournamentLocalizationPatch
{
    private static bool _applied;
    private static readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string> ExactEnglishToZh = new(StringComparer.OrdinalIgnoreCase)
    {
        // EncounterBanPopupController
        ["All Types"] = "全部类型",
        ["Merchants"] = "商人",
        ["Events"] = "奇遇事件",
        ["Monsters"] = "野怪战斗",
        ["Pedestals"] = "强化基座",
        ["Copy Code"] = "复制禁用码",
        ["Copied!"] = "已复制!",
        ["Paste Code"] = "粘贴禁用码",
        ["Pasted!"] = "已粘贴!",
        ["No Code"] = "无禁用码",
        ["Recent Bans"] = "最近禁用",
        ["Ban Encounters"] = "禁用奇遇",
        ["Banned Encounters"] = "已禁用奇遇",
        ["Encounter Bans"] = "奇遇禁用设置",
        ["Search Encounters..."] = "搜索奇遇...",
        ["Search..."] = "搜索...",
        ["Filter..."] = "筛选...",
        ["Clear All"] = "清空全部",
        ["Reset"] = "重置",

        // 第 8 英雄双龙展示名兜底汉化
        ["The Dragons"] = "双龙",
        ["Hero8"] = "双龙",
        ["The Dragons' Hideout"] = "双龙的藏身处",

        // TournamentHostSettingsView
        ["Host Settings"] = "房主建房设置",
        ["Tournament Settings"] = "锦标赛设置",
        ["Base Settings"] = "基础设置",
        ["Basic Settings"] = "基础设置",
        ["Basic"] = "基础设置",
        ["Advanced Settings"] = "高级设置",
        ["Advanced"] = "高级设置",
        ["Create"] = "创建房间",
        ["Create Lobby"] = "创建房间",
        ["Create Tournament"] = "创建锦标赛",
        ["Limit Players"] = "限制房间人数",
        ["Limit Player Cap"] = "限制房间人数",
        ["Limit Size"] = "限制人数",
        ["Player Cap"] = "人数上限",
        ["Max Players"] = "最大人数",
        ["Start on Full"] = "满员自动开始",
        ["Start When Full"] = "满员自动开始",
        ["List Publicly"] = "公开展示房间",
        ["Public Lobby"] = "公开房间",
        ["Display Code"] = "显示房间码",
        ["Show Code"] = "显示房间码",
        ["Total Countdown"] = "总倒计时(秒)",
        ["Round Countdown"] = "回合倒计时(秒)",
        ["PvP Countdown"] = "PvP倒计时(秒)",
        ["Edit Bans"] = "设置禁用",
        ["Ban List"] = "禁用列表",

        // TournamentLobbyView & 兄弟界面
        ["Leave Lobby"] = "离开房间",
        ["Begin"] = "开始比赛",
        ["Begin Tournament"] = "开始锦标赛",
        ["Start Tournament"] = "开始锦标赛",
        ["Start"] = "开始",
        ["Lobby Code"] = "房间码",
        ["Tournament Code"] = "锦标赛房间码",
        ["Room Code"] = "房间码",
        ["Enter Code"] = "输入房间码",
        ["Enter Tournament Code"] = "输入锦标赛房间码",
        ["Tournament Lobby"] = "锦标赛大厅",
        ["Participants"] = "参赛选手",
        ["Players"] = "参赛玩家",
        ["Waiting for host..."] = "等待房主开始...",
        ["Waiting for host to start..."] = "等待房主开始比赛...",
        ["Join"] = "加入房间",
        ["Join Lobby"] = "加入房间",
        ["Host"] = "创建房间",
        ["Host Lobby"] = "创建房间",
        ["Browse"] = "浏览房间",
        ["Browse Lobbies"] = "浏览公开房间",
        ["Queue"] = "快速匹配",
        ["Open Lobbies"] = "公开房间",
        ["Live Tournaments"] = "进行中锦标赛",
        ["Watch"] = "观战",
        ["Loading..."] = "加载中...",
        ["Starting..."] = "即将开始...",
        ["No public lobbies right now. Host one, or join with a code."] = "当前暂无公开房间，可自行创建或输入房间码加入。",
        ["No live tournaments to watch right now."] = "当前暂无可观战的锦标赛。",
        ["Play begins when {0} players join!"] = "满 {0} 位选手后自动开赛！",
        ["{0} (Host Only)"] = "{0}（仅房主）",
        ["{0}'s Tournament"] = "{0} 的锦标赛",
        ["Return to Lobby"] = "返回大厅",
        ["Removed from Lobby"] = "已被移出房间",
        ["The host removed you from the lobby."] = "房主已将你移出当前房间。",
        ["You were removed from this lobby by the host and can't rejoin."] = "你已被房主移出该房间，无法重新加入。",
        ["This lobby can't be joined right now."] = "当前无法加入该房间。",
        ["Enter a tournament code to join."] = "请输入要加入的房间码。",
        ["The tournament lobby code is invalid."] = "锦标赛房间码无效。",
        ["The tournament lobby has already started."] = "该锦标赛已经开始。",
        ["The tournament lobby is already full."] = "该锦标赛房间已满。",
        ["The player is not in the tournament lobby."] = "玩家不在该锦标赛房间中。",
        ["Only the lobby host can start the tournament lobby."] = "只有房主可以开始锦标赛。",
        ["At least two players are required to start a tournament lobby."] = "至少需要 2 名选手才能开始锦标赛。",
        ["Failed to join tournament lobby."] = "加入锦标赛房间失败。",
        ["Back"] = "返回",
        ["Close"] = "关闭",
        ["OK"] = "确定",
        ["Ok"] = "确定",
    };

    private static readonly Dictionary<string, string> HostSettingsUiOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cancel"] = "取消",
    };

    private static readonly Dictionary<string, string> BanPopupUiOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cancel"] = "取消",
    };

    private static readonly Dictionary<string, string> QueueUiOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Leave"] = "退出匹配",
        ["Cancel"] = "取消匹配",
    };

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        _applied = true;

        try
        {
            PatchLocalizableText(harmony);
            PatchEncounterBanPopup(harmony);
            PatchTournamentHostSettingsView(harmony);
            PatchTournamentLobbyView(harmony);
            PatchOtherTournamentViews(harmony);
            BazaarLauncherPlugin.Log?.LogInfo("TournamentLocalizationPatch applied");
        }
        catch (Exception ex)
        {
            LogOnce("apply_root", "TournamentLocalizationPatch.Apply failed: " + ex.Message);
        }
    }

    // ── 1. LocalizableText.GetLocalizedText 全局白名单翻译 ────────────────────

    private static void PatchLocalizableText(Harmony harmony)
    {
        var t = AccessTools.TypeByName("TheBazaar.Utilities.LocalizableText");
        if (t == null)
        {
            LogOnce("missing:LocalizableText", "LocalizableText type not found");
            return;
        }
        SafePatchPostfix(harmony, t, "GetLocalizedText", nameof(LocalizableTextPostfix));
    }

    private static void LocalizableTextPostfix(ref string __result)
    {
        if (string.IsNullOrWhiteSpace(__result)) return;
        string trimmed = __result.Trim();
        if (ExactEnglishToZh.TryGetValue(trimmed, out string zh))
        {
            __result = zh;
        }
    }

    // ── 2. EncounterBanPopupController ────────────────────────────────────────

    private static void PatchEncounterBanPopup(Harmony harmony)
    {
        var t = AccessTools.TypeByName("TheBazaar.UI.EncounterBanPopupController");
        if (t == null)
        {
            LogOnce("missing:EncounterBanPopupController", "EncounterBanPopupController not found");
            return;
        }

        SafePatchPostfix(harmony, t, "Awake", nameof(BanPopupRefreshPostfix));
        SafePatchPostfix(harmony, t, "OnEnable", nameof(BanPopupRefreshPostfix));
        SafePatchPostfix(harmony, t, "Show", nameof(BanPopupRefreshPostfix));
        SafePatchPostfix(harmony, t, "ResetCodeLabels", nameof(BanPopupCodeLabelsPostfix));
        SafePatchPostfix(harmony, t, "FlashCodeLabel", nameof(BanPopupCodeLabelsPostfix));
        SafePatchPostfix(harmony, t, "RefreshRecentBans", nameof(BanPopupRecentBansPostfix));
    }

    private static void BanPopupRefreshPostfix(object __instance)
    {
        if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            TranslateBanPopupLabels(__instance);
            BanPopupCodeLabelsPostfix(__instance);
            BanPopupRecentBansPostfix(__instance);
            if (__instance is Component comp && comp != null)
            {
                TranslateChildTexts(comp, BanPopupUiOverrides);
            }
        }
        catch (Exception ex)
        {
            LogOnce("BanPopupRefreshPostfix", "BanPopupRefreshPostfix error: " + ex.Message);
        }
    }

    private static void TranslateBanPopupLabels(object popup)
    {
        string[] zhCategories = { "全部类型", "商人", "奇遇事件", "野怪战斗", "强化基座" };

        var catDropdown = GetFieldOrProp<TMP_Dropdown>(popup, "_categoryDropdown");
        if (catDropdown != null && catDropdown.options != null)
        {
            for (int i = 0; i < catDropdown.options.Count && i < zhCategories.Length; i++)
            {
                catDropdown.options[i].text = zhCategories[i];
            }
            catDropdown.RefreshShownValue();
        }

        var filterInput = GetFieldOrProp<TMP_InputField>(popup, "_encounterFilter");
        if (filterInput != null && filterInput.placeholder is TMP_Text ph)
        {
            if (string.IsNullOrWhiteSpace(ph.text) || IsAsciiOnly(ph.text))
            {
                ph.text = "搜索奇遇名称或ID...";
            }
        }
    }

    private static void BanPopupCodeLabelsPostfix(object __instance)
    {
        if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            TranslateTmpField(__instance, "_copyCodeLabel");
            TranslateTmpField(__instance, "_pasteCodeLabel");
        }
        catch (Exception ex)
        {
            LogOnce("BanPopupCodeLabelsPostfix", "BanPopupCodeLabelsPostfix error: " + ex.Message);
        }
    }

    private static void BanPopupRecentBansPostfix(object __instance)
    {
        if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            var recent = GetFieldOrProp<TMP_Dropdown>(__instance, "_recentBansDropdown");
            if (recent != null && recent.options != null && recent.options.Count > 0)
            {
                string opt0 = recent.options[0].text;
                if (string.IsNullOrEmpty(opt0) || string.Equals(opt0.Trim(), "Recent Bans", StringComparison.OrdinalIgnoreCase))
                {
                    recent.options[0].text = "最近禁用";
                    recent.RefreshShownValue();
                }
            }
        }
        catch (Exception ex)
        {
            LogOnce("BanPopupRecentBansPostfix", "BanPopupRecentBansPostfix error: " + ex.Message);
        }
    }

    // ── 3. TournamentHostSettingsView ─────────────────────────────────────────

    private static void PatchTournamentHostSettingsView(Harmony harmony)
    {
        var t = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentHostSettingsView");
        if (t == null)
        {
            LogOnce("missing:TournamentHostSettingsView", "TournamentHostSettingsView not found");
            return;
        }

        SafePatchPostfix(harmony, t, "OnEnable", nameof(HostSettingsRefreshPostfix));
        SafePatchPostfix(harmony, t, "OnLocaleChanged", nameof(HostSettingsRefreshPostfix));
        SafePatchPostfix(harmony, t, "SetInteractable", nameof(HostSettingsRefreshPostfix));
        SafePatchPostfix(harmony, t, "OnSizeLimited", nameof(HostSettingsRefreshPostfix));
        SafePatchPostfix(harmony, t, "DrawBannedEncounters", nameof(HostSettingsRefreshPostfix));
    }

    private static void HostSettingsRefreshPostfix(object __instance)
    {
        if (__instance is not Component comp || comp == null) return;
        try
        {
            TranslateChildTexts(comp, HostSettingsUiOverrides);
        }
        catch (Exception ex)
        {
            LogOnce("HostSettingsRefreshPostfix", "HostSettingsRefreshPostfix error: " + ex.Message);
        }
    }

    // ── 4. TournamentLobbyView ────────────────────────────────────────────────

    private static void PatchTournamentLobbyView(Harmony harmony)
    {
        var t = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentLobbyView");
        if (t == null)
        {
            LogOnce("missing:TournamentLobbyView", "TournamentLobbyView not found");
            return;
        }

        SafePatchPostfix(harmony, t, "SetIsHost", nameof(LobbyViewSetIsHostPostfix));
        SafePatchPostfix(harmony, t, "SetTournamentInfo", nameof(LobbyViewSetInfoPostfix));
    }

    private static void LobbyViewSetIsHostPostfix(object __instance, bool isHost)
    {
        if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            var leaveBtn = GetFieldOrProp<object>(__instance, "_leaveButton");
            if (leaveBtn != null)
            {
                var btnText = GetFieldOrProp<TMP_Text>(leaveBtn, "ButtonText");
                if (btnText != null)
                {
                    btnText.text = isHost ? "解散房间" : "离开房间";
                }
            }

            if (__instance is Component comp && comp != null)
            {
                var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Cancel"] = isHost ? "解散房间" : "离开房间",
                    ["Leave Lobby"] = "离开房间",
                    ["Leave"] = isHost ? "解散房间" : "离开房间",
                };
                TranslateChildTexts(comp, overrides);
            }
        }
        catch (Exception ex)
        {
            LogOnce("LobbyViewSetIsHostPostfix", "LobbyViewSetIsHostPostfix error: " + ex.Message);
        }
    }

    private static void LobbyViewSetInfoPostfix(object __instance)
    {
        if (__instance is not Component comp || comp == null) return;
        try
        {
            bool isHost = GetFieldOrProp<bool>(__instance, "_isHost");
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cancel"] = isHost ? "解散房间" : "离开房间",
                ["Leave Lobby"] = "离开房间",
            };
            TranslateChildTexts(comp, overrides);
        }
        catch (Exception ex)
        {
            LogOnce("LobbyViewSetInfoPostfix", "LobbyViewSetInfoPostfix error: " + ex.Message);
        }
    }

    // ── 5. 其余锦标赛辅助界面（空值安全兜底） ──────────────────────────────────

    private static void PatchOtherTournamentViews(Harmony harmony)
    {
        var hostOrJoin = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentHostOrJoinView");
        if (hostOrJoin != null)
        {
            SafePatchPostfix(harmony, hostOrJoin, "OnEnable", nameof(GenericViewTranslatePostfix));
        }

        var browser = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentLobbyBrowserView");
        if (browser != null)
        {
            SafePatchPostfix(harmony, browser, "OnEnable", nameof(GenericViewTranslatePostfix));
            SafePatchPostfix(harmony, browser, "SetTab", nameof(GenericViewTranslatePostfix));
            SafePatchPostfix(harmony, browser, "ShowLoading", nameof(GenericViewTranslatePostfix));
            SafePatchPostfix(harmony, browser, "ShowResults", nameof(GenericViewTranslatePostfix));
        }

        var queue = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentQueueView");
        if (queue != null)
        {
            SafePatchPostfix(harmony, queue, "OnEnable", nameof(QueueViewTranslatePostfix));
            SafePatchPostfix(harmony, queue, "SetProgress", nameof(QueueViewTranslatePostfix));
            SafePatchPostfix(harmony, queue, "SetStarting", nameof(QueueViewTranslatePostfix));
        }
    }

    private static void GenericViewTranslatePostfix(object __instance)
    {
        if (__instance is not Component comp || comp == null) return;
        try { TranslateChildTexts(comp, null); }
        catch { }
    }

    private static void QueueViewTranslatePostfix(object __instance)
    {
        if (__instance is not Component comp || comp == null) return;
        try { TranslateChildTexts(comp, QueueUiOverrides); }
        catch { }
    }

    // ── 工具函数（严格空值保护与单次日志） ────────────────────────────────────

    private static void SafePatchPostfix(
        Harmony harmony, Type targetType, string methodName, string postfixMethodName, Type[] parameterTypes = null)
    {
        if (targetType == null) return;
        try
        {
            MethodInfo target = parameterTypes == null
                ? AccessTools.Method(targetType, methodName)
                : AccessTools.Method(targetType, methodName, parameterTypes);

            if (target == null)
            {
                LogOnce($"missing_method:{targetType.FullName}.{methodName}",
                    $"TournamentLocalizationPatch: method not found {targetType.FullName}.{methodName}");
                return;
            }

            MethodInfo postfix = AccessTools.Method(typeof(TournamentLocalizationPatch), postfixMethodName);
            if (postfix == null)
            {
                LogOnce($"missing_postfix:{postfixMethodName}",
                    $"TournamentLocalizationPatch: postfix helper missing {postfixMethodName}");
                return;
            }

            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
        }
        catch (Exception ex)
        {
            LogOnce($"patch_fail:{targetType.FullName}.{methodName}",
                $"TournamentLocalizationPatch: failed patching {targetType.FullName}.{methodName}: {ex.Message}");
        }
    }

    private static void TranslateTmpField(object instance, string fieldName)
    {
        var tmp = GetFieldOrProp<TMP_Text>(instance, fieldName);
        if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) return;
        if (TryTranslateString(tmp.text, null, out string zh))
        {
            tmp.text = zh;
        }
    }

    private static void TranslateChildTexts(Component root, IReadOnlyDictionary<string, string> overrides)
    {
        if (root == null) return;
        var texts = root.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        if (texts == null) return;

        foreach (var tmp in texts)
        {
            if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) continue;
            if (TryTranslateString(tmp.text, overrides, out string zh) && !string.Equals(tmp.text, zh, StringComparison.Ordinal))
            {
                tmp.text = zh;
            }
        }
    }

    private static bool TryTranslateString(string raw, IReadOnlyDictionary<string, string> overrides, out string translated)
    {
        string trimmed = (raw ?? "").Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            translated = raw;
            return false;
        }

        if (overrides != null && overrides.TryGetValue(trimmed, out translated))
        {
            return true;
        }
        if (ExactEnglishToZh.TryGetValue(trimmed, out translated))
        {
            return true;
        }

        // 处理带冒号的表单标签（如 "Player Cap:" -> "人数上限:"）
        if (trimmed.EndsWith(":", StringComparison.Ordinal))
        {
            string stem = trimmed.Substring(0, trimmed.Length - 1).Trim();
            if (overrides != null && overrides.TryGetValue(stem, out string zhStem))
            {
                translated = zhStem + "：";
                return true;
            }
            if (ExactEnglishToZh.TryGetValue(stem, out string zhStem2))
            {
                translated = zhStem2 + "：";
                return true;
            }
        }

        translated = raw;
        return false;
    }

    private static T GetFieldOrProp<T>(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name)) return default;
        try
        {
            Type t = obj.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo f = t.GetField(name, flags);
            if (f != null)
            {
                object val = f.GetValue(obj);
                if (val is T tv) return tv;
                return default;
            }
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.CanRead)
            {
                object val = p.GetValue(obj);
                if (val is T tv) return tv;
            }
        }
        catch (Exception ex)
        {
            LogOnce($"reflect:{obj.GetType().Name}.{name}", $"TournamentLocalizationPatch reflection {name}: {ex.Message}");
        }
        return default;
    }

    private static bool IsAsciiOnly(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        foreach (char c in s)
        {
            if (c > 127) return false;
        }
        return true;
    }

    private static void LogOnce(string key, string msg)
    {
        if (_loggedOnce.Add(key))
        {
            BazaarLauncherPlugin.Log?.LogWarning(msg);
        }
    }
}

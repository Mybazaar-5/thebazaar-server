using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using BazaarLauncher.Core;
using BazaarLauncher.Modules;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// 锦标赛、奇遇禁用与高频原生视图硬编码英文兜底补丁（已升级并接入 LocalizationOverlayModule 四层词典，修复 P1-9）：
///   1. 严格校验当前游戏语言（非中文模式不强行篡改英文界面）；
///   2. 缓存所有反射 FieldInfo/PropertyInfo 与静态上下文词典，彻底消除热路径反射与 Dictionary 重复分配；
///   3. 优先查询 LocalizationOverlayModule 四层外挂词典，回退内置兜底词典。
/// </summary>
internal static class TournamentLocalizationPatch
{
    private static bool _applied;
    private static readonly HashSet<string> _loggedOnce = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(Type, string), MemberInfo> _memberCache = new();

    private static readonly Dictionary<string, string> ExactEnglishToZh = new(StringComparer.OrdinalIgnoreCase)
    {
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

        ["The Dragons"] = "双龙",
        ["Hero8"] = "双龙",
        ["The Dragons' Hideout"] = "双龙的藏身处",

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
        ["Spectate"] = "观战",
        ["End Run"] = "结束对局",
        ["Eliminated"] = "已淘汰",
        ["Pending"] = "等待中",
        ["Started"] = "进行中",
        ["Equip"] = "装备",
        ["Unequip"] = "卸下",
        ["View in Collection"] = "在藏品中查看",
        ["Apprentice Merchant"] = "见习商人",
        ["Max Rage"] = "最大怒气",
        ["Combat will begin shortly."] = "战斗即将开始。",
        ["Connection Issue"] = "连接异常",
        ["Server"] = "服务器",
        ["Loading..."] = "加载中...",
        ["Loading preview..."] = "正在加载预览...",
        ["Loading previews..."] = "正在加载预览...",
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

    private static readonly Dictionary<string, string> LobbyHostOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cancel"] = "解散房间",
        ["Leave Lobby"] = "解散房间",
        ["Leave"] = "解散房间",
    };

    private static readonly Dictionary<string, string> LobbyGuestOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Cancel"] = "离开房间",
        ["Leave Lobby"] = "离开房间",
        ["Leave"] = "离开房间",
    };

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (_applied || patcher == null) return;
        _applied = true;

        try
        {
            // 注册基础内置兜底表到四层词典低优先层（让外挂 custom_zh_overlay.json 与 Mod 词典优先覆盖）
            LocalizationOverlayModule.Instance?.RegisterExactOverrides("zh-CN", ExactEnglishToZh, null, priority: 20);

            PatchEncounterBanPopup(patcher);
            PatchTournamentHostSettingsView(patcher);
            PatchTournamentLobbyView(patcher);
            PatchOtherTournamentViews(patcher);
            BazaarLauncherPlugin.Log?.LogInfo("TournamentLocalizationPatch（四层词典联动版）已加载");
        }
        catch (Exception ex)
        {
            LogOnce("apply_root", "TournamentLocalizationPatch.Apply 异常: " + ex.Message);
        }
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied || harmony == null) return;
        Apply(new SafeHarmonyPatcher("tournament.l10n.legacy"));
    }

    private static void PatchEncounterBanPopup(SafeHarmonyPatcher patcher)
    {
        const string typeName = "TheBazaar.UI.EncounterBanPopupController";
        var refresh = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(BanPopupRefreshPostfix));
        var codes = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(BanPopupCodeLabelsPostfix));
        var recent = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(BanPopupRecentBansPostfix));

        patcher.TryPatch(typeName, "Awake", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "OnEnable", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "Show", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "ResetCodeLabels", postfix: codes, optional: true);
        patcher.TryPatch(typeName, "FlashCodeLabel", postfix: codes, optional: true);
        patcher.TryPatch(typeName, "RefreshRecentBans", postfix: recent, optional: true);
    }

    private static void PatchTournamentHostSettingsView(SafeHarmonyPatcher patcher)
    {
        const string typeName = "TheBazaar.UI.Tournaments.TournamentHostSettingsView";
        var refresh = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(HostSettingsRefreshPostfix));

        patcher.TryPatch(typeName, "OnEnable", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "OnLocaleChanged", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "SetInteractable", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "OnSizeLimited", postfix: refresh, optional: true);
        patcher.TryPatch(typeName, "DrawBannedEncounters", postfix: refresh, optional: true);
    }

    private static void PatchTournamentLobbyView(SafeHarmonyPatcher patcher)
    {
        const string typeName = "TheBazaar.UI.Tournaments.TournamentLobbyView";
        patcher.TryPatch(typeName, "SetIsHost", postfix: AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(LobbyViewSetIsHostPostfix)), optional: true);
        patcher.TryPatch(typeName, "SetTournamentInfo", postfix: AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(LobbyViewSetInfoPostfix)), optional: true);
    }

    private static void PatchOtherTournamentViews(SafeHarmonyPatcher patcher)
    {
        var generic = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(GenericViewTranslatePostfix));
        var queue = AccessTools.Method(typeof(TournamentLocalizationPatch), nameof(QueueViewTranslatePostfix));

        patcher.TryPatch("TheBazaar.UI.Tournaments.TournamentHostOrJoinView", "OnEnable", postfix: generic, optional: true);

        const string browser = "TheBazaar.UI.Tournaments.TournamentLobbyBrowserView";
        patcher.TryPatch(browser, "OnEnable", postfix: generic, optional: true);
        patcher.TryPatch(browser, "SetTab", postfix: generic, optional: true);
        patcher.TryPatch(browser, "ShowLoading", postfix: generic, optional: true);
        patcher.TryPatch(browser, "ShowResults", postfix: generic, optional: true);

        const string queueType = "TheBazaar.UI.Tournaments.TournamentQueueView";
        patcher.TryPatch(queueType, "OnEnable", postfix: queue, optional: true);
        patcher.TryPatch(queueType, "SetProgress", postfix: queue, optional: true);
        patcher.TryPatch(queueType, "SetStarting", postfix: queue, optional: true);
    }

    private static bool IsChineseActive()
        => LocalizationOverlayModule.IsChineseLocale(LocalizationOverlayModule.DetectCurrentGameLocale());

    private static void BanPopupRefreshPostfix(object __instance)
    {
        if (!IsChineseActive() || __instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            TranslateBanPopupLabels(__instance);
            BanPopupCodeLabelsPostfix(__instance);
            BanPopupRecentBansPostfix(__instance);
            if (__instance is Component comp && comp != null)
            {
                TranslateChildTexts(comp, BanPopupUiOverrides, "EncounterBanPopupController");
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
        if (!IsChineseActive() || __instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
        try
        {
            TranslateTmpField(__instance, "_copyCodeLabel", "EncounterBanPopupController");
            TranslateTmpField(__instance, "_pasteCodeLabel", "EncounterBanPopupController");
        }
        catch (Exception ex)
        {
            LogOnce("BanPopupCodeLabelsPostfix", "BanPopupCodeLabelsPostfix error: " + ex.Message);
        }
    }

    private static void BanPopupRecentBansPostfix(object __instance)
    {
        if (!IsChineseActive() || __instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
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

    private static void HostSettingsRefreshPostfix(object __instance)
    {
        if (!IsChineseActive() || __instance is not Component comp || comp == null) return;
        try
        {
            TranslateChildTexts(comp, HostSettingsUiOverrides, "TournamentHostSettingsView");
        }
        catch (Exception ex)
        {
            LogOnce("HostSettingsRefreshPostfix", "HostSettingsRefreshPostfix error: " + ex.Message);
        }
    }

    private static void LobbyViewSetIsHostPostfix(object __instance, bool isHost)
    {
        if (!IsChineseActive() || __instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
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
                TranslateChildTexts(
                    comp,
                    isHost ? LobbyHostOverrides : LobbyGuestOverrides,
                    isHost ? "TournamentLobbyView.Host" : "TournamentLobbyView.Guest");
            }
        }
        catch (Exception ex)
        {
            LogOnce("LobbyViewSetIsHostPostfix", "LobbyViewSetIsHostPostfix error: " + ex.Message);
        }
    }

    private static void LobbyViewSetInfoPostfix(object __instance)
    {
        if (!IsChineseActive() || __instance is not Component comp || comp == null) return;
        try
        {
            bool isHost = GetFieldOrProp<bool>(__instance, "_isHost");
            TranslateChildTexts(
                comp,
                isHost ? LobbyHostOverrides : LobbyGuestOverrides,
                isHost ? "TournamentLobbyView.Host" : "TournamentLobbyView.Guest");
        }
        catch (Exception ex)
        {
            LogOnce("LobbyViewSetInfoPostfix", "LobbyViewSetInfoPostfix error: " + ex.Message);
        }
    }

    private static void GenericViewTranslatePostfix(object __instance)
    {
        if (!IsChineseActive() || __instance is not Component comp || comp == null) return;
        try { TranslateChildTexts(comp, null, __instance.GetType().Name); }
        catch { }
    }

    private static void QueueViewTranslatePostfix(object __instance)
    {
        if (!IsChineseActive() || __instance is not Component comp || comp == null) return;
        try { TranslateChildTexts(comp, QueueUiOverrides, "TournamentQueueView"); }
        catch { }
    }

    private static void TranslateTmpField(object instance, string fieldName, string contextKey)
    {
        var tmp = GetFieldOrProp<TMP_Text>(instance, fieldName);
        if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) return;
        if (TryTranslateString(tmp.text, null, contextKey, out string zh) && !string.Equals(tmp.text, zh, StringComparison.Ordinal))
        {
            tmp.text = zh;
        }
    }

    private static void TranslateChildTexts(Component root, IReadOnlyDictionary<string, string> overrides, string contextKey)
    {
        if (root == null) return;
        var texts = root.GetComponentsInChildren<TMP_Text>(includeInactive: true);
        if (texts == null) return;

        for (int i = 0; i < texts.Length; i++)
        {
            var tmp = texts[i];
            if (tmp == null || string.IsNullOrWhiteSpace(tmp.text)) continue;
            if (TryTranslateString(tmp.text, overrides, contextKey, out string zh) && !string.Equals(tmp.text, zh, StringComparison.Ordinal))
            {
                tmp.text = zh;
            }
        }
    }

    internal static bool TryTranslateString(
        string raw,
        IReadOnlyDictionary<string, string> overrides,
        string contextKey,
        out string translated)
    {
        if (string.IsNullOrEmpty(raw))
        {
            translated = raw;
            return false;
        }

        string lookup = (char.IsWhiteSpace(raw[0]) || char.IsWhiteSpace(raw[raw.Length - 1])) ? raw.Trim() : raw;
        if (string.IsNullOrEmpty(lookup))
        {
            translated = raw;
            return false;
        }

        // 1. 优先上下文显式局部覆盖
        if (overrides != null && overrides.TryGetValue(lookup, out translated))
        {
            return true;
        }

        // 2. 查四层词典引擎（包含 Mod 词典 / 外挂 custom_zh_overlay.json / 上下文 / 正则）
        if (LocalizationOverlayModule.Instance != null
            && LocalizationOverlayModule.Instance.TryResolveText("zh-CN", null, lookup, contextKey, out translated))
        {
            return true;
        }

        // 3. 本地兜底字典
        if (ExactEnglishToZh.TryGetValue(lookup, out translated))
        {
            return true;
        }

        if (lookup.Length > 1 && lookup[lookup.Length - 1] == ':')
        {
            string stem = lookup.Substring(0, lookup.Length - 1).TrimEnd();
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
            var member = _memberCache.GetOrAdd((t, name), static key =>
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MemberInfo f = key.Item1.GetField(key.Item2, flags);
                if (f != null) return f;
                return key.Item1.GetProperty(key.Item2, flags);
            });

            object val = member switch
            {
                FieldInfo fi => fi.GetValue(obj),
                PropertyInfo pi when pi.CanRead => pi.GetValue(obj),
                _ => null,
            };
            return val is T tv ? tv : default;
        }
        catch (Exception ex)
        {
            LogOnce($"reflect:{obj.GetType().Name}.{name}", $"TournamentLocalizationPatch reflection {name}: {ex.Message}");
            return default;
        }
    }

    private static bool IsAsciiOnly(string s)
    {
        if (string.IsNullOrEmpty(s)) return true;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] > 127) return false;
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

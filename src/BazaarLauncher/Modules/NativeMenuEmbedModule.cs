using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// 原生游戏界面深度嵌入宿主（实现 INativeUiHost，彻底告别割裂浮窗！同时 100% 保留 F8 兜底）：
///   锚点一：主大厅模式选择区（PlaymodeSelectionViewComponent / MainMenuBaseView / MainMenuController）
///           —— 原生深色鎏金风格的 7 大玩法模式/预设一键直达栏：
///           「经典官方」「爽玩增强」「无尽爬塔」「跨职业混池」「肉鸽词条/每日挑战」「Boss 车轮战」「沙盒实验室」
///           自动记住上次选择并同步服务端 /custom-rules/me/mode/{id} 或 /preset/{id}；
///   锚点二：全局左侧原生导航抽屉（NavigationMenuController + SceneSelectOptionContainer 哨兵 SceneID 9001..9003）
///           —— 自然融入「自建工坊 &amp; Mod 中心」「多人社交与观战」「性能监控与诊断」；
///   锚点三：局内 ESC 暂停菜单（FightMenuDialog.Start / UpdateButtonsState）与原生设置页（OptionsDialogController.Awake）
///           —— 克隆原版按钮预制体自然嵌入上述三大入口；
///   锚点四：支持第三方 Mod 通过 INativeUiHost 动态注册导航项、模式项、ESC 按钮、工坊扩展页签与原生 Toast 通知。
/// </summary>
internal sealed class NativeMenuEmbedModule : IBuiltInPatchModule, INativeUiHost
{
    internal static NativeMenuEmbedModule Instance { get; private set; }

    internal const int SentinelSceneWorkshop = 9001;
    internal const int SentinelSceneSocial = 9002;
    internal const int SentinelScenePerf = 9003;

    public ModManifest Manifest { get; } = new()
    {
        Id = "bazaar.ui.native_embed",
        Name = "原生界面深度嵌入宿主",
        Version = "2.1.0",
        Author = "BazaarModKit",
        Description = "在主大厅模式区、左侧导航抽屉、局内 ESC 菜单与原生设置页深度嵌入 7 模式直达、工坊中心、多人社交与性能诊断入口。",
        IsBuiltIn = true,
        DefaultEnabled = true,
        Category = "UI",
        SupportsHotReload = true,
    };

    public SafeHarmonyPatcher Patcher { get; private set; }

    private readonly object _sync = new();
    private readonly List<NativeNavEntryDescriptor> _navEntries = new();
    private readonly List<NativePlaymodeOptionDescriptor> _playmodeOptions = new();
    private readonly List<NativeMenuButtonDescriptor> _escapeButtons = new();
    private readonly List<NativeOptionsSectionDescriptor> _optionsSections = new();
    private readonly List<NativeWorkshopTabDescriptor> _workshopTabs = new();
    private int _nextDynamicSentinelId = 9100;

    // 主大厅原生嵌入模式选择器与克隆按钮运行时引用（P2-41：热卸载时全量回收）
    private GameObject _embeddedLobbyModeBarGo;
    private readonly List<GameObject> _clonedButtons = new();
    private readonly Dictionary<string, (Image Bg, Image Border, TextMeshProUGUI Label)> _lobbyModeChips = new(StringComparer.OrdinalIgnoreCase);
    private TextMeshProUGUI _lobbyModeHintText;
    private string _selectedQuickModeId = "classic";
    private IModContext _ctx;

    // 反射缓存
    private static FieldInfo _navDataField;
    private static Type _sceneIdEnumType;
    private static Type _traversableSceneType;
    private static FieldInfo _fightOptionsBtnField;
    private static FieldInfo _fightMenuDialogRootField;
    private static MethodInfo _fightHideDialogsMethod;
    private static FieldInfo _optionsGameplayBtnField;

    internal string SelectedQuickModeId => _selectedQuickModeId;

    public void OnLoad(IModContext context)
    {
        _ctx = context;
        Instance = this;
        _selectedQuickModeId = context?.Config?.GetString("lastQuickModeId", "classic") ?? "classic";
        RegisterBuiltInEntries();
    }

    public void OnEnable()
    {
        Patcher = new SafeHarmonyPatcher(Manifest.Id);
        InstallNativeUiAnchorPatches(Patcher);
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[NativeMenuEmbed] 原生 UI 4 组锚点嵌入已激活 (成功={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        Patcher?.UnpatchAll();
        MainThreadDispatcher.Enqueue(() =>
        {
            if (_embeddedLobbyModeBarGo != null)
            {
                UnityEngine.Object.Destroy(_embeddedLobbyModeBarGo);
                _embeddedLobbyModeBarGo = null;
                _lobbyModeChips.Clear();
            }

            // P2-41：热卸载时销毁全部注入到 FightMenuDialog / OptionsDialogController 的克隆按钮，杜绝菜单留僵尸按钮
            for (int i = 0; i < _clonedButtons.Count; i++)
            {
                var btnGo = _clonedButtons[i];
                if (btnGo != null)
                {
                    UnityEngine.Object.Destroy(btnGo);
                }
            }
            _clonedButtons.Clear();
        });
    }

    private void TrackClonedButton(GameObject clonedGo)
    {
        if (clonedGo == null) return;
        for (int i = _clonedButtons.Count - 1; i >= 0; i--)
        {
            if (_clonedButtons[i] == null)
            {
                _clonedButtons.RemoveAt(i);
            }
        }
        _clonedButtons.Add(clonedGo);
    }

    private static void StripNativeButtonCustomComponents(GameObject clonedGo)
    {
        if (clonedGo == null) return;
        var customBtnType = GameInspector.FindTypeSafe("ButtonCustom", "TheBazaar.ButtonCustom");
        if (customBtnType == null) return;

        var comps = clonedGo.GetComponentsInChildren(customBtnType, includeInactive: true);
        if (comps == null || comps.Length == 0) return;
        for (int i = 0; i < comps.Length; i++)
        {
            if (comps[i] != null)
            {
                UnityEngine.Object.Destroy(comps[i]);
            }
        }
    }

    public void OnUnload()
    {
        OnDisable();
        if (Instance == this) Instance = null;
    }

    // ── 内置 7 大模式直达 + 3 大原生导航/ESC/设置页入口注册 ──────────────────

    private void RegisterBuiltInEntries()
    {
        // 主大厅 7 大模式与预设直达项
        var builtInModes = new[]
        {
            new NativePlaymodeOptionDescriptor { Id = "classic", Title = "经典官方", Subtitle = "1:1 原版平衡", ModeId = "Standard", PresetId = "PureOfficial", Order = 10 },
            new NativePlaymodeOptionDescriptor { Id = "enhanced", Title = "爽玩增强", Subtitle = "高资源快成型", ModeId = "Standard", PresetId = "EnhancedFun", Order = 20 },
            new NativePlaymodeOptionDescriptor { Id = "endless_tower", Title = "无尽爬塔", Subtitle = "突破10胜冲99层", ModeId = "EndlessTower", PresetId = "PureOfficial", Order = 30 },
            new NativePlaymodeOptionDescriptor { Id = "cross_hero", Title = "跨职业混池", Subtitle = "8英雄大乱斗", ModeId = "CrossHeroMayhem", PresetId = "PureOfficial", Order = 40 },
            new NativePlaymodeOptionDescriptor { Id = "mutator_daily", Title = "肉鸽词条/每日", Subtitle = "14词条与同种子", ModeId = "DailyChallenge", PresetId = "ExtremeRoguelike", Order = 50 },
            new NativePlaymodeOptionDescriptor { Id = "boss_rush", Title = "Boss车轮战", Subtitle = "10波传奇连战", ModeId = "BossRush", PresetId = "PureOfficial", Order = 60 },
            new NativePlaymodeOptionDescriptor { Id = "sandbox", Title = "沙盒实验室", Subtitle = "无限刷新自由测", ModeId = "SandboxLab", PresetId = "PureOfficial", Order = 70 },
        };

        foreach (var opt in builtInModes)
        {
            var captured = opt;
            captured.OnSelected = () => SelectQuickMode(captured);
            RegisterPlaymodeOption(captured);
        }

        // 全局左侧导航抽屉 3 大原生入口（哨兵 SceneID 9001..9003）
        RegisterNavDrawerEntry(new NativeNavEntryDescriptor
        {
            Id = "nav.workshop",
            Title = "自建工坊 & Mod 中心",
            Tooltip = "配置玩法规则、管理 Mod 与自定义卡牌、启用沙盒控制台",
            SentinelSceneId = SentinelSceneWorkshop,
            Order = 10,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Workshop),
        });

        RegisterNavDrawerEntry(new NativeNavEntryDescriptor
        {
            Id = "nav.social",
            Title = "多人社交与观战",
            Tooltip = "公开/密码房浏览、房间聊天、系统播报、实时观战、历史回放与排行榜",
            SentinelSceneId = SentinelSceneSocial,
            Order = 20,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Social),
        });

        RegisterNavDrawerEntry(new NativeNavEntryDescriptor
        {
            Id = "nav.perf",
            Title = "性能监控与诊断",
            Tooltip = "实时 FPS/1%Low/内存/Ping 读数、10 项性能优化开关与模块健康诊断",
            SentinelSceneId = SentinelScenePerf,
            Order = 30,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Performance),
        });

        // 局内 ESC 菜单 3 大原生快捷按钮
        RegisterEscapeMenuButton(new NativeMenuButtonDescriptor
        {
            Id = "esc.workshop",
            Label = "自建工坊 & Mod 中心",
            Order = 10,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Workshop),
        });

        RegisterEscapeMenuButton(new NativeMenuButtonDescriptor
        {
            Id = "esc.social",
            Label = "多人社交与观战",
            Order = 20,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Social),
        });

        RegisterEscapeMenuButton(new NativeMenuButtonDescriptor
        {
            Id = "esc.perf",
            Label = "性能监控与诊断",
            Order = 30,
            OnClick = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Performance),
        });

        // 原生设置页分栏入口
        RegisterOptionsSection(new NativeOptionsSectionDescriptor
        {
            Id = "opt.perf_modkit",
            Title = "自建服与极致性能",
            Order = 10,
            OnOpen = () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Performance),
        });
    }

    internal void SelectQuickMode(NativePlaymodeOptionDescriptor opt, bool syncServer = true)
    {
        if (opt == null) return;
        _selectedQuickModeId = opt.Id;
        _ctx?.Config?.SetString("lastQuickModeId", opt.Id);
        _ctx?.Config?.Save();

        RefreshLobbyModeBarVisuals();

        if (syncServer)
        {
            CustomGameplayUI.Instance?.ApplyQuickModeFromNativeLobby(opt.Id, opt.ModeId, opt.PresetId, opt.Title);
        }
    }

    internal void SyncSelectedQuickModeFromRules(string modeId, string presetId)
    {
        string targetId = MapRulesToQuickModeId(modeId, presetId);
        if (!string.Equals(_selectedQuickModeId, targetId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedQuickModeId = targetId;
            _ctx?.Config?.SetString("lastQuickModeId", targetId);
            _ctx?.Config?.Save();
            RefreshLobbyModeBarVisuals();
        }
    }

    private static string MapRulesToQuickModeId(string modeId, string presetId)
    {
        if (string.Equals(modeId, "EndlessTower", StringComparison.OrdinalIgnoreCase)) return "endless_tower";
        if (string.Equals(modeId, "CrossHeroMayhem", StringComparison.OrdinalIgnoreCase)) return "cross_hero";
        if (string.Equals(modeId, "MutatorChallenge", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modeId, "DailyChallenge", StringComparison.OrdinalIgnoreCase)) return "mutator_daily";
        if (string.Equals(modeId, "BossRush", StringComparison.OrdinalIgnoreCase)) return "boss_rush";
        if (string.Equals(modeId, "SandboxLab", StringComparison.OrdinalIgnoreCase)) return "sandbox";
        if (string.Equals(presetId, "EnhancedFun", StringComparison.OrdinalIgnoreCase)) return "enhanced";
        return "classic";
    }

    private static void OpenWorkspacePanelTab(CustomGameplayUI.UiTab tab)
    {
        MainThreadDispatcher.Enqueue(() =>
        {
            CustomGameplayUI.Instance?.OpenPanelOnTab(tab);
        });
    }

    // ── INativeUiHost 接口实现 ───────────────────────────────────────────────

    public IDisposable RegisterNavDrawerEntry(NativeNavEntryDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (_sync)
        {
            if (descriptor.SentinelSceneId < 9001)
            {
                descriptor.SentinelSceneId = _nextDynamicSentinelId++;
            }
            _navEntries.RemoveAll(x => string.Equals(x.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            _navEntries.Add(descriptor);
            _navEntries.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return new CallbackDisposable(() => { lock (_sync) { _navEntries.Remove(descriptor); } });
    }

    public IDisposable RegisterPlaymodeOption(NativePlaymodeOptionDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (_sync)
        {
            _playmodeOptions.RemoveAll(x => string.Equals(x.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            _playmodeOptions.Add(descriptor);
            _playmodeOptions.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return new CallbackDisposable(() => { lock (_sync) { _playmodeOptions.Remove(descriptor); } });
    }

    public IDisposable RegisterEscapeMenuButton(NativeMenuButtonDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (_sync)
        {
            _escapeButtons.RemoveAll(x => string.Equals(x.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            _escapeButtons.Add(descriptor);
            _escapeButtons.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return new CallbackDisposable(() => { lock (_sync) { _escapeButtons.Remove(descriptor); } });
    }

    public IDisposable RegisterOptionsSection(NativeOptionsSectionDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (_sync)
        {
            _optionsSections.RemoveAll(x => string.Equals(x.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            _optionsSections.Add(descriptor);
            _optionsSections.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return new CallbackDisposable(() => { lock (_sync) { _optionsSections.Remove(descriptor); } });
    }

    public IDisposable RegisterWorkshopTab(NativeWorkshopTabDescriptor descriptor)
    {
        if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
        lock (_sync)
        {
            _workshopTabs.RemoveAll(x => string.Equals(x.Id, descriptor.Id, StringComparison.OrdinalIgnoreCase));
            _workshopTabs.Add(descriptor);
            _workshopTabs.Sort((a, b) => a.Order.CompareTo(b.Order));
        }
        return new CallbackDisposable(() => { lock (_sync) { _workshopTabs.Remove(descriptor); } });
    }

    public void OpenNativePopup(string popupId, Action<object> buildContent = null)
    {
        MainThreadDispatcher.Enqueue(() =>
        {
            if (string.Equals(popupId, "social", StringComparison.OrdinalIgnoreCase))
            {
                CustomGameplayUI.Instance?.OpenPanelOnTab(CustomGameplayUI.UiTab.Social);
            }
            else if (string.Equals(popupId, "performance", StringComparison.OrdinalIgnoreCase)
                  || string.Equals(popupId, "perf", StringComparison.OrdinalIgnoreCase))
            {
                CustomGameplayUI.Instance?.OpenPanelOnTab(CustomGameplayUI.UiTab.Performance);
            }
            else
            {
                CustomGameplayUI.Instance?.OpenPanelOnTab(CustomGameplayUI.UiTab.Workshop);
                if (buildContent != null && CustomGameplayUI.Instance != null)
                {
                    CustomGameplayUI.Instance.InvokeExternalCustomPopupBuilder(buildContent);
                }
            }
        });
    }

    public void CloseNativePopup(string popupId)
    {
        MainThreadDispatcher.Enqueue(() => CustomGameplayUI.Instance?.ClosePanel());
    }

    public void ShowToast(string message, bool isError = false)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        MainThreadDispatcher.Enqueue(() => CustomGameplayUI.Instance?.ShowToastBanner(message, isError));
    }

    public IReadOnlyList<NativeNavEntryDescriptor> GetRegisteredNavEntries()
    {
        lock (_sync) return _navEntries.ToArray();
    }

    public IReadOnlyList<NativePlaymodeOptionDescriptor> GetRegisteredPlaymodeOptions()
    {
        lock (_sync) return _playmodeOptions.ToArray();
    }

    public IReadOnlyList<NativeMenuButtonDescriptor> GetRegisteredEscapeMenuButtons()
    {
        lock (_sync) return _escapeButtons.ToArray();
    }

    public IReadOnlyList<NativeWorkshopTabDescriptor> GetRegisteredWorkshopTabs()
    {
        lock (_sync) return _workshopTabs.ToArray();
    }

    // ── 安装 4 组原生 UI 锚点 Harmony 钩子 ────────────────────────────────────

    private static void InstallNativeUiAnchorPatches(SafeHarmonyPatcher patcher)
    {
        // 锚点一：主大厅模式选择区 (PlaymodeSelectionViewComponent.Initialize)
        patcher.TryPatch(
            "TheBazaar.UI.MainMenu.PlaymodeSelectionViewComponent",
            "Initialize",
            postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(PlaymodeSelectionInitializePostfix)),
            optional: true,
            label: "NativeUi.PlaymodeSelectionViewComponent.Initialize");

        patcher.TryPatch(
            "TheBazaar.UI.MainMenu.MainMenuBaseView",
            "OnEnable",
            postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(MainMenuBaseViewOnEnablePostfix)),
            optional: true,
            label: "NativeUi.MainMenuBaseView.OnEnable");

        // 锚点二：全局左侧导航抽屉 (NavigationMenuController.PopulateData & SceneSelectOptionContainer.HandleSceneChange)
        var navType = GameInspector.FindTypeSafe("TheBazaar.UI.Menu.NavigationMenuController");
        if (navType != null)
        {
            _navDataField = AccessTools.Field(navType, "_data");
            patcher.TryPatch(
                navType,
                "PopulateData",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(NavigationMenuPopulateDataPostfix)),
                optional: true,
                label: "NativeUi.NavigationMenuController.PopulateData");
        }

        // 优先使用真实全局命名空间类型名并静默查找，彻底清零 HarmonyX 反射告警 #6
        _sceneIdEnumType = GameInspector.FindTypeSafe("TheBazaar.SceneID", "TheBazaar.UI.Menu.SceneID");
        _traversableSceneType = GameInspector.FindTypeSafe(
            "TraversableScenesSO+TraversableScene",
            "TheBazaar.UI.Menu.TraversableScenesSO+TraversableScene");

        patcher.TryPatch(
            "TheBazaar.UI.Menu.SceneSelectOptionContainer",
            "HandleSceneChange",
            prefix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(SceneSelectHandleSceneChangePrefix)),
            optional: true,
            label: "NativeUi.SceneSelectOptionContainer.HandleSceneChange");

        // 锚点三：局内 ESC 暂停菜单 (FightMenuDialog.Start / UpdateButtonsState)
        // 严禁触碰 SettingButton（它是常驻右上角的原生齿轮图标本体！），仅读取弹窗内部的 optionsButton 与 fightMenuDialog 容器
        var fightMenuType = GameInspector.FindTypeSafe("FightMenuDialog");
        if (fightMenuType != null)
        {
            _fightOptionsBtnField = AccessTools.Field(fightMenuType, "optionsButton");
            _fightMenuDialogRootField = AccessTools.Field(fightMenuType, "fightMenuDialog");
            _fightHideDialogsMethod = AccessTools.Method(fightMenuType, "HideDialogs", Type.EmptyTypes);
            patcher.TryPatch(
                fightMenuType,
                "Start",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(FightMenuDialogStartPostfix)),
                optional: true,
                label: "NativeUi.FightMenuDialog.Start");
            patcher.TryPatch(
                fightMenuType,
                "UpdateButtonsState",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(FightMenuDialogStartPostfix)),
                optional: true,
                label: "NativeUi.FightMenuDialog.UpdateButtonsState");
        }

        // 锚点四：原生设置页 (OptionsDialogController.Awake)
        var optType = GameInspector.FindTypeSafe("OptionsDialogController");
        if (optType != null)
        {
            _optionsGameplayBtnField = AccessTools.Field(optType, "_gameplayButton");
            patcher.TryPatch(
                optType,
                "Awake",
                Type.EmptyTypes,
                postfix: AccessTools.Method(typeof(NativeMenuEmbedModule), nameof(OptionsDialogAwakePostfix)),
                optional: true,
                label: "NativeUi.OptionsDialogController.Awake");
        }
    }

    // ── 锚点一实现：主大厅 7 大模式与预设原生直达选择器 ───────────────────────

    private static void PlaymodeSelectionInitializePostfix(Component __instance)
    {
        try
        {
            if (__instance == null || Instance == null) return;
            MainThreadDispatcher.Enqueue(() => Instance?.MountLobbyModeSelector(__instance));
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NativeMenuEmbed] PlaymodeSelectionInitializePostfix 异常已拦截: " + ex.Message);
        }
    }

    private static void MainMenuBaseViewOnEnablePostfix(Component __instance)
    {
        if (__instance == null || Instance == null) return;
        try
        {
            var f = AccessTools.Field(__instance.GetType(), "_playmodeSelectionView");
            if (f?.GetValue(__instance) is Component comp && comp != null)
            {
                MainThreadDispatcher.Enqueue(() => Instance?.MountLobbyModeSelector(comp));
            }
        }
        catch { }
    }

    private void MountLobbyModeSelector(Component playmodeAnchor)
    {
        if (playmodeAnchor == null) return;
        try
        {
            Transform parent = playmodeAnchor.transform.parent != null ? playmodeAnchor.transform.parent : playmodeAnchor.transform;
            if (parent == null) return;

            Transform existing = parent.Find("BazaarNativeLobbyModeBar");
            if (existing != null)
            {
                _embeddedLobbyModeBarGo = existing.gameObject;
                RefreshLobbyModeBarVisuals();
                return;
            }

            // 采集原版 Playmode 区域的 TMP 字体与材质，确保视觉 100% 统一
            TMP_Text sampleTmp = playmodeAnchor.GetComponentInChildren<TMP_Text>(true);
            TMP_FontAsset nativeFont = sampleTmp != null ? sampleTmp.font : CustomGameplayUI.Instance?.GetActiveUiFont();

            var barGo = new GameObject("BazaarNativeLobbyModeBar", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            barGo.transform.SetParent(parent, worldPositionStays: false);
            _embeddedLobbyModeBarGo = barGo;

            var rt = barGo.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 0f);
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(1f, 0f);
            rt.anchoredPosition = new Vector2(-24f, 8f);
            rt.sizeDelta = new Vector2(940f, 76f);

            var bgImg = barGo.GetComponent<Image>();
            bgImg.color = new Color(0.07f, 0.06f, 0.10f, 0.90f);

            var outline = barGo.AddComponent<Outline>();
            outline.effectColor = new Color(0.83f, 0.69f, 0.22f, 0.72f);
            outline.effectDistance = new Vector2(1.2f, -1.2f);

            var vlg = barGo.GetComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 10, 6, 6);
            vlg.spacing = 4f;
            vlg.childAlignment = TextAnchor.MiddleCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            // 顶栏标题与当前选中说明 + 一键打开工坊入口
            var topRow = new GameObject("HeaderRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            topRow.transform.SetParent(barGo.transform, false);
            var topLe = topRow.AddComponent<LayoutElement>();
            topLe.preferredHeight = 22f;
            var topHlg = topRow.GetComponent<HorizontalLayoutGroup>();
            topHlg.spacing = 8f;
            topHlg.childControlWidth = true;
            topHlg.childControlHeight = true;
            topHlg.childForceExpandWidth = false;

            var hintGo = new GameObject("ModeHint", typeof(RectTransform));
            hintGo.transform.SetParent(topRow.transform, false);
            var hintLe = hintGo.AddComponent<LayoutElement>();
            hintLe.flexibleWidth = 1f;
            _lobbyModeHintText = hintGo.AddComponent<TextMeshProUGUI>();
            if (nativeFont != null) _lobbyModeHintText.font = nativeFont;
            _lobbyModeHintText.fontSize = 14f;
            _lobbyModeHintText.color = NativeUguiFactory.ColGold;
            _lobbyModeHintText.alignment = TextAlignmentOptions.MidlineLeft;
            _lobbyModeHintText.raycastTarget = false;

            CreateCompactNativeActionBtn(topRow.transform, nativeFont, "工坊 & Mod", () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Workshop), 108f);
            CreateCompactNativeActionBtn(topRow.transform, nativeFont, "社交 / 观战", () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Social), 108f);
            CreateCompactNativeActionBtn(topRow.transform, nativeFont, "性能 / 诊断", () => OpenWorkspacePanelTab(CustomGameplayUI.UiTab.Performance), 108f);

            // 7 大模式横向原生按钮排
            var chipRow = new GameObject("ModeChipsRow", typeof(RectTransform), typeof(HorizontalLayoutGroup));
            chipRow.transform.SetParent(barGo.transform, false);
            var chipLe = chipRow.AddComponent<LayoutElement>();
            chipLe.preferredHeight = 36f;
            var chipHlg = chipRow.GetComponent<HorizontalLayoutGroup>();
            chipHlg.spacing = 6f;
            chipHlg.childControlWidth = true;
            chipHlg.childControlHeight = true;
            chipHlg.childForceExpandWidth = true;
            chipHlg.childForceExpandHeight = true;

            _lobbyModeChips.Clear();
            var options = GetRegisteredPlaymodeOptions();
            foreach (var opt in options)
            {
                var captured = opt;
                var chipGo = new GameObject("ModeChip_" + captured.Id, typeof(RectTransform), typeof(Image), typeof(Button));
                chipGo.transform.SetParent(chipRow.transform, false);

                var chipImg = chipGo.GetComponent<Image>();
                var borderGo = new GameObject("Border", typeof(RectTransform), typeof(Image));
                borderGo.transform.SetParent(chipGo.transform, false);
                var bRt = borderGo.GetComponent<RectTransform>();
                bRt.anchorMin = Vector2.zero;
                bRt.anchorMax = Vector2.one;
                bRt.offsetMin = Vector2.zero;
                bRt.offsetMax = Vector2.zero;
                var bImg = borderGo.GetComponent<Image>();
                bImg.raycastTarget = false;

                var lblGo = new GameObject("Label", typeof(RectTransform));
                lblGo.transform.SetParent(chipGo.transform, false);
                var lRt = lblGo.GetComponent<RectTransform>();
                lRt.anchorMin = Vector2.zero;
                lRt.anchorMax = Vector2.one;
                lRt.offsetMin = new Vector2(4f, 2f);
                lRt.offsetMax = new Vector2(-4f, -2f);
                var lbl = lblGo.AddComponent<TextMeshProUGUI>();
                if (nativeFont != null) lbl.font = nativeFont;
                lbl.text = captured.Title;
                lbl.fontSize = 14f;
                lbl.alignment = TextAlignmentOptions.Center;
                lbl.raycastTarget = false;
                lbl.textWrappingMode = TextWrappingModes.NoWrap;
                lbl.overflowMode = TextOverflowModes.Ellipsis;

                var btn = chipGo.GetComponent<Button>();
                btn.onClick.AddListener(() =>
                {
                    captured.OnSelected?.Invoke();
                });

                _lobbyModeChips[captured.Id] = (chipImg, bImg, lbl);
            }

            RefreshLobbyModeBarVisuals();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[NativeMenuEmbed] 挂载主大厅 7 模式直达栏异常: " + ex.Message);
        }
    }

    private static void CreateCompactNativeActionBtn(Transform parent, TMP_FontAsset font, string label, Action onClick, float width)
    {
        var go = new GameObject("QuickBtn_" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var le = go.AddComponent<LayoutElement>();
        le.preferredWidth = width;
        le.preferredHeight = 22f;

        var img = go.GetComponent<Image>();
        img.color = new Color(0.24f, 0.19f, 0.10f, 0.95f);

        var txtGo = new GameObject("Txt", typeof(RectTransform));
        txtGo.transform.SetParent(go.transform, false);
        var rt = txtGo.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        var tmp = txtGo.AddComponent<TextMeshProUGUI>();
        if (font != null) tmp.font = font;
        tmp.text = label;
        tmp.fontSize = 12f;
        tmp.color = NativeUguiFactory.ColGold;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;

        go.GetComponent<Button>().onClick.AddListener(() => onClick?.Invoke());
    }

    internal void RefreshLobbyModeBarVisuals()
    {
        if (_embeddedLobbyModeBarGo == null) return;
        var options = GetRegisteredPlaymodeOptions();
        NativePlaymodeOptionDescriptor active = options.FirstOrDefault(x => string.Equals(x.Id, _selectedQuickModeId, StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault();

        if (_lobbyModeHintText != null && active != null)
        {
            _lobbyModeHintText.text = $"玩法直达：{active.Title}（{active.Subtitle}）· 点击下方即刻切换并自动记忆";
        }

        foreach (var kv in _lobbyModeChips)
        {
            bool selected = string.Equals(kv.Key, _selectedQuickModeId, StringComparison.OrdinalIgnoreCase);
            if (kv.Value.Bg != null)
            {
                kv.Value.Bg.color = selected
                    ? new Color(0.46f, 0.34f, 0.09f, 0.98f)
                    : new Color(0.15f, 0.13f, 0.20f, 0.92f);
            }
            if (kv.Value.Border != null)
            {
                kv.Value.Border.color = selected
                    ? new Color(0.95f, 0.80f, 0.30f, 0.25f)
                    : new Color(0.38f, 0.34f, 0.45f, 0.12f);
            }
            if (kv.Value.Label != null)
            {
                kv.Value.Label.color = selected ? Color.white : NativeUguiFactory.ColCream;
                kv.Value.Label.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
            }
        }
    }

    // ── 锚点二实现：左侧原生导航抽屉注入（哨兵 SceneID 9001..9003） ─────────

    private static void NavigationMenuPopulateDataPostfix(object __instance)
    {
        if (__instance == null || Instance == null || _navDataField == null) return;
        try
        {
            if (_navDataField.GetValue(__instance) is not IList dataList || dataList.Count == 0) return;

            object templateData = dataList[0];
            if (templateData == null) return;
            Type dataItemType = templateData.GetType();
            var sceneProp = dataItemType.GetProperty("Scene", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var sceneField = dataItemType.GetField("Scene", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? dataItemType.GetField("_scene", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            var entries = Instance.GetRegisteredNavEntries();
            if (entries.Count == 0 || _sceneIdEnumType == null) return;

            // 收集已有哨兵 ID 防重复注入
            var existingIds = new HashSet<int>();
            foreach (var item in dataList)
            {
                if (item == null) continue;
                object sceneObj = sceneProp?.GetValue(item) ?? sceneField?.GetValue(item);
                if (sceneObj != null)
                {
                    object sidVal = sceneObj.GetType().GetField("sceneID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(sceneObj)
                        ?? sceneObj.GetType().GetProperty("SceneID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(sceneObj);
                    if (sidVal != null) existingIds.Add(Convert.ToInt32(sidVal));
                }
            }

            foreach (var entry in entries)
            {
                if (existingIds.Contains(entry.SentinelSceneId)) continue;
                object clonedItem = TryCreateSentinelNavData(dataItemType, templateData, entry);
                if (clonedItem != null)
                {
                    dataList.Add(clonedItem);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogDebug("[NativeMenuEmbed] 左侧抽屉注入回退静默跳过: " + ex.Message);
        }
    }

    private static object TryCreateSentinelNavData(Type dataItemType, object templateData, NativeNavEntryDescriptor entry)
    {
        try
        {
            if (_traversableSceneType == null || _sceneIdEnumType == null) return null;
            object sceneObj = Activator.CreateInstance(_traversableSceneType);
            object sentinelEnum = Enum.ToObject(_sceneIdEnumType, entry.SentinelSceneId);

            SetFieldOrProp(sceneObj, "sceneID", sentinelEnum);
            SetFieldOrProp(sceneObj, "SceneID", sentinelEnum);
            SetFieldOrProp(sceneObj, "displayName", entry.Title);
            SetFieldOrProp(sceneObj, "DisplayName", entry.Title);
            SetFieldOrProp(sceneObj, "title", entry.Title);
            SetFieldOrProp(sceneObj, "Title", entry.Title);

            // 尝试使用带 TraversableScene 参数的构造器，否则浅拷贝模板再替换 Scene
            var ctor = dataItemType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType.IsAssignableFrom(_traversableSceneType));

            if (ctor != null)
            {
                return ctor.Invoke(new[] { sceneObj });
            }

            var cloneMethod = typeof(object).GetMethod("MemberwiseClone", BindingFlags.NonPublic | BindingFlags.Instance);
            object copy = cloneMethod?.Invoke(templateData, null);
            if (copy != null)
            {
                SetFieldOrProp(copy, "Scene", sceneObj);
                SetFieldOrProp(copy, "_scene", sceneObj);
                SetFieldOrProp(copy, "Title", entry.Title);
                return copy;
            }
        }
        catch { }
        return null;
    }

    private static bool SceneSelectHandleSceneChangePrefix(object __0, ref Task __result)
    {
        try
        {
            if (__0 == null || Instance == null) return true;
            int id = Convert.ToInt32(__0);
            if (id < 9001) return true;

            var entries = Instance.GetRegisteredNavEntries();
            var matched = entries.FirstOrDefault(e => e.SentinelSceneId == id);
            if (matched != null)
            {
                matched.OnClick?.Invoke();
                __result = Task.CompletedTask;
                return false;
            }
        }
        catch { }
        return true;
    }

    // ── 锚点三实现：局内 ESC 菜单（FightMenuDialog）原版按钮克隆注入 ──────────

    private static void FightMenuDialogStartPostfix(Component __instance)
    {
        if (__instance == null || Instance == null) return;
        try
        {
            // 仅允许在暂停菜单弹窗内部（fightMenuDialog）且存在 LayoutGroup 的按钮列表中安全追加，
            // 绝不触碰右上角常驻齿轮（SettingButton），也绝不在无排版容器的固定坐标上原地克隆覆盖原生按钮。
            GameObject dialogRoot = _fightMenuDialogRootField?.GetValue(__instance) as GameObject;
            object rawBtn = _fightOptionsBtnField?.GetValue(__instance);
            Button templateBtn = (rawBtn as Button)
                ?? (rawBtn as Component)?.GetComponentInChildren<Button>(true);
            if (dialogRoot == null || templateBtn == null || templateBtn.transform.parent == null) return;
            if (!templateBtn.transform.IsChildOf(dialogRoot.transform)) return;

            Transform container = templateBtn.transform.parent;
            if (container.GetComponent<LayoutGroup>() == null)
            {
                return;
            }

            var buttons = Instance.GetRegisteredEscapeMenuButtons();
            foreach (var desc in buttons)
            {
                if (desc.IsVisible != null && !desc.IsVisible()) continue;

                string goName = "BazaarEscBtn_" + desc.Id;
                Transform existing = container.Find(goName);
                if (existing != null) continue;

                GameObject cloned = UnityEngine.Object.Instantiate(templateBtn.gameObject, container, worldPositionStays: false);
                cloned.name = goName;
                cloned.SetActive(true);
                Instance.TrackClonedButton(cloned);

                // 移除克隆体上的 ButtonCustom，避免其 Start() 额外绑定导致重复或误触原生逻辑
                StripNativeButtonCustomComponents(cloned);

                // 剥离原版持久化监听并绑定新回调；关闭弹窗必须调用原生 HideDialogs()，绝不能停用 __instance.gameObject（否则会注销 ESC 监听！）
                Button btn = cloned.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick = new Button.ButtonClickedEvent();
                    var captured = desc;
                    var dialogInst = __instance;
                    btn.onClick.AddListener(() =>
                    {
                        try
                        {
                            if (_fightHideDialogsMethod != null && dialogInst != null)
                            {
                                _fightHideDialogsMethod.Invoke(dialogInst, null);
                            }
                            else if (dialogRoot != null)
                            {
                                dialogRoot.SetActive(false);
                            }
                        }
                        catch { }
                        captured.OnClick?.Invoke();
                    });
                }

                // 替换克隆按钮上的文字（自动继承原版 TMP 字体、材质与鎏金描边）
                var tmp = cloned.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                {
                    tmp.text = desc.Label;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogDebug("[NativeMenuEmbed] FightMenuDialog 注入异常: " + ex.Message);
        }
    }

    // ── 锚点四实现：原生设置页（OptionsDialogController）分栏入口注入 ─────────

    private static void OptionsDialogAwakePostfix(Component __instance)
    {
        if (__instance == null || Instance == null) return;
        try
        {
            // 仅在 _gameplayButton 明确存在且父节点具备 LayoutGroup 时才追加分栏入口，
            // 严禁在无自动排版的固定布局上原地克隆覆盖原生「玩法设置 / 关闭 / 退出」按钮。
            Button gameplayBtn = _optionsGameplayBtnField?.GetValue(__instance) as Button;
            if (gameplayBtn == null || gameplayBtn.transform.parent == null) return;

            Transform container = gameplayBtn.transform.parent;
            if (container.GetComponent<LayoutGroup>() == null)
            {
                return;
            }

            foreach (var sec in Instance._optionsSections)
            {
                string goName = "BazaarOptSec_" + sec.Id;
                if (container.Find(goName) != null) continue;

                GameObject cloned = UnityEngine.Object.Instantiate(gameplayBtn.gameObject, container, worldPositionStays: false);
                cloned.name = goName;
                cloned.SetActive(true);
                Instance.TrackClonedButton(cloned);

                // P2-41：剥离克隆自 _gameplayButton 的 ButtonCustom 组件，防止点击自定义分栏误触原生按钮样式/回调
                StripNativeButtonCustomComponents(cloned);

                Button btn = cloned.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick = new Button.ButtonClickedEvent();
                    var captured = sec;
                    btn.onClick.AddListener(() => captured.OnOpen?.Invoke());
                }

                var tmp = cloned.GetComponentInChildren<TMP_Text>(true);
                if (tmp != null)
                {
                    tmp.text = sec.Title;
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogDebug("[NativeMenuEmbed] OptionsDialogController 注入异常: " + ex.Message);
        }
    }

    private static void SetFieldOrProp(object target, string name, object value)
    {
        if (target == null) return;
        Type t = target.GetType();
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var f = t.GetField(name, flags);
        if (f != null && (value == null || f.FieldType.IsInstanceOfType(value)))
        {
            f.SetValue(target, value);
            return;
        }
        var p = t.GetProperty(name, flags);
        if (p != null && p.CanWrite && (value == null || p.PropertyType.IsInstanceOfType(value)))
        {
            p.SetValue(target, value);
        }
    }

    private sealed class CallbackDisposable : IDisposable
    {
        private Action _act;
        public CallbackDisposable(Action act) => _act = act;
        public void Dispose() { _act?.Invoke(); _act = null; }
    }
}

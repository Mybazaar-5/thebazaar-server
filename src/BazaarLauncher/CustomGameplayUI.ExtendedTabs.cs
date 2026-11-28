using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using BazaarLauncher.Modules;
using BazaarModSdk;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// CustomGameplayUI 三大原生级扩展页签与原生 Toast/模式直达同步实现：
///   1. 「自建工坊 &amp; Mod 中心」：服务端已同步 Mod 列表（含《大巴扎编年史》示例包）、一键启停与热重载、
///      全部自定义卡牌/技能/事件目录、14 个本地模块与外部 DLL 热启停/热重载、以及沙盒快捷控制台（仅沙盒局激活）；
///   2. 「多人社交与观战」：公开房/密码房浏览与一键建房/密码加房、房间/世界聊天与系统战况播报（对接 /api/social/* 与 SSE）、
///      实时观战入口与历史战报回放列表（/spectate、/api/replays/me）、模式排行榜查看（/api/leaderboards/*）；
///   3. 「性能与诊断」：实时 FPS / 1% Low / 帧时间 ms / Mono 堆 MB / /api/ping 延迟 ms 读数、三档一键预设、
///      10 项运行时性能优化独立实时开关、模块健康与熔断状态列表及一键导出诊断报告。
/// </summary>
internal sealed partial class CustomGameplayUI
{
    // ── 新增 3 个原生级页签按钮与根容器 ───────────────────────────────────────
    private NativeUguiFactory.ButtonHandle _tabWorkshopBtn;
    private NativeUguiFactory.ButtonHandle _tabSocialBtn;
    private NativeUguiFactory.ButtonHandle _tabPerformanceBtn;

    private GameObject _workshopTabRoot;
    private GameObject _socialTabRoot;
    private GameObject _performanceTabRoot;

    // ── 原生轻提示 Toast 横幅 ──────────────────────────────────────────────────
    private GameObject _toastGo;
    private Image _toastBg;
    private TextMeshProUGUI _toastText;
    private float _toastExpireAt;

    // ── 工坊 & Mod 中心控件引用 ────────────────────────────────────────────────
    private TextMeshProUGUI _workshopSyncSummaryText;
    private Transform _serverModsListContainer;
    private Transform _customContentListContainer;
    private Transform _localModulesListContainer;
    private GameObject _sandboxSectionGo;
    private TMP_InputField _sandboxCardIdInput;
    private TMP_InputField _sandboxGoldInput;
    private TMP_InputField _sandboxHpInput;
    private TextMeshProUGUI _sandboxResultText;
    private Transform _externalModTabsContainer;

    // ── 多人社交与观战控件引用 ──────────────────────────────────────────────────
    private TMP_InputField _socialRoomNameInput;
    private TMP_InputField _socialRoomPwdInput;
    private TMP_InputField _socialJoinCodeInput;
    private TMP_InputField _socialJoinPwdInput;
    private Transform _socialLobbiesContainer;
    private Transform _socialChatMessagesContainer;
    private TMP_InputField _socialChatInput;
    private Transform _socialSpectateContainer;
    private Transform _socialReplaysContainer;
    private Transform _socialLeaderboardContainer;
    private string _activeLeaderboardKey = "endless";
    private string _activeChatChannel = "lobby";
    private sealed class SocialSseHandle
    {
        public CancellationTokenSource Cts { get; }
        private int _disposed;

        public SocialSseHandle(CancellationTokenSource cts)
        {
            Cts = cts ?? throw new ArgumentNullException(nameof(cts));
        }

        public bool IsCancellationRequested
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return true;
                try { return Cts.IsCancellationRequested; }
                catch (ObjectDisposedException) { return true; }
            }
        }

        public void CancelAndDisposeOnce()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Cts.Cancel(); } catch { }
            try { Cts.Dispose(); } catch { }
        }
    }

    private SocialSseHandle _socialSseHandle;
    private string _subscribedSseKey = string.Empty;
    private long _lastSocialEventId;
    private readonly List<(long Id, string Channel, string Sender, string Content)> _socialMessages = new();

    // ── 性能监控与诊断控件引用 ──────────────────────────────────────────────────
    private TextMeshProUGUI _perfLiveHeaderText;
    private TextMeshProUGUI _perfLiveDetailText;
    private Transform _perfModulesHealthContainer;
    private readonly List<Action> _perfToggleRefreshers = new();

    // ── 对外导航与主大厅 7 模式直达同步接口 ───────────────────────────────────

    internal void OpenPanelOnTab(UiTab tab)
    {
        EnsureModalBuilt();
        _activeTab = tab;
        OpenPanel();
        SwitchTab(tab);
    }

    internal void InvokeExternalCustomPopupBuilder(Action<object> buildContent)
    {
        if (buildContent == null || _externalModTabsContainer == null) return;
        try
        {
            ClearChildren(_externalModTabsContainer);
            buildContent(_externalModTabsContainer as RectTransform);
        }
        catch (Exception ex)
        {
            SetStatus("第三方 Mod 界面构建异常：" + ex.Message, isError: true);
        }
    }

    internal void ShowToastBanner(string message, bool isError = false, float seconds = 3.6f)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        EnsureHudBuilt();
        if (_toastGo == null && _safeViewportRoot != null)
        {
            BuildToastBanner(_safeViewportRoot);
        }
        if (_toastGo == null) return;

        _toastBg.color = isError ? new Color(0.42f, 0.10f, 0.12f, 0.94f) : new Color(0.08f, 0.22f, 0.16f, 0.94f);
        _toastText.color = isError ? NativeUguiFactory.ColErrorText : NativeUguiFactory.ColGold;
        _toastText.text = message;
        _toastGo.SetActive(true);
        _toastExpireAt = Time.unscaledTime + Mathf.Clamp(seconds, 1f, 15f);
        if (_hudCanvas != null) _hudCanvas.enabled = true;
    }

    private void TickToastBanner(float now)
    {
        if (_toastGo != null && _toastGo.activeSelf && now >= _toastExpireAt)
        {
            _toastGo.SetActive(false);
            MarkHudDirty();
        }
    }

    private void BuildToastBanner(Transform parent)
    {
        _toastGo = _ui.CreateBox("BazaarToastBanner", parent, new Color(0.08f, 0.22f, 0.16f, 0.94f), rounded: true, raycastTarget: false);
        var rt = _toastGo.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 1f);
        rt.anchorMax = new Vector2(0.5f, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, -66f);
        rt.sizeDelta = new Vector2(600f, 34f);

        // P1-49：附加独立排序子 Canvas（sortingOrder=560 > _modalCanvas=500，无 GraphicRaycaster），确保弹窗打开时 Toast 仍清晰可见且不阻挡点击
        var toastCanvas = _toastGo.AddComponent<Canvas>();
        toastCanvas.overrideSorting = true;
        toastCanvas.sortingOrder = 560;

        _toastBg = _toastGo.GetComponent<Image>();
        _toastText = _ui.CreateText("ToastText", _toastGo.transform, string.Empty, 14f, NativeUguiFactory.ColGold, FontStyles.Bold, TextAlignmentOptions.Center, wrap: false);
        NativeUguiFactory.StretchFill(_toastText.rectTransform);
        _toastText.rectTransform.offsetMin = new Vector2(12f, 4f);
        _toastText.rectTransform.offsetMax = new Vector2(-12f, -4f);
        _toastGo.SetActive(false);
    }

    /// <summary>
    /// 响应主大厅 7 大模式与预设原生直达选择，同步服务端 /custom-rules/me/mode/{id} 或 /preset/{id} 并回退 /custom-rules/me。
    /// </summary>
    internal void ApplyQuickModeFromNativeLobby(string quickId, string modeId, string presetId, string titleZh)
    {
        _normalRules.EnsureCollections();
        _normalRules.ApplyLocalPresetDefaults(presetId, modeId);
        _normalBuf.SyncFromRules(_normalRules);

        // D4-P1-4：进入 Task.Run 前在主线程先深拷贝不可变规则快照，禁止后台直接访问可变 _normalRules
        var rulesSnapshot = _normalRules.Clone();
        _rulesVersion++;
        MarkHudDirty();
        SetStatus($"正在同步玩法模式「{titleZh}」...", isError: false);
        ShowToastBanner($"已切换至【{titleZh}】，正在同步至服务端...");

        _ = Task.Run(async () =>
        {
            NameAuth.HttpJsonResponse resp;
            if (!string.Equals(modeId, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                resp = await NameAuth.PostJsonAsync($"/custom-rules/me/mode/{Uri.EscapeDataString(modeId)}", new { presetId, modeId }).ConfigureAwait(false);
            }
            else
            {
                resp = await NameAuth.PostJsonAsync($"/custom-rules/me/preset/{Uri.EscapeDataString(presetId)}", new { presetId, modeId }).ConfigureAwait(false);
            }

            // 向后兼容旧服务端：若快捷端点不存在则回退 PUT /custom-rules/me（使用主线程不可变快照）
            if (!resp.Ok && (resp.StatusCode == 404 || resp.StatusCode == 405))
            {
                resp = await NameAuth.PutJsonAsync("/custom-rules/me", rulesSnapshot).ConfigureAwait(false);
            }

            _pendingUpdates.Enqueue(() =>
            {
                if (resp.Ok)
                {
                    var srv = TryDeserializeRules(resp.Body);
                    if (srv != null)
                    {
                        _normalRules = srv;
                        _normalRules.NormalizeBounds();
                        _normalBuf.SyncFromRules(_normalRules);
                    }
                    SetStatus($"已激活玩法「{titleZh}」并同步服务端（开局即生效）", isError: false);
                    ShowToastBanner($"玩法已生效：【{titleZh}】");
                }
                else
                {
                    SetStatus($"模式「{titleZh}」本地已切，服务端同步提示：{Truncate(resp.Error, 52)}", isError: true);
                    ShowToastBanner($"服务端同步提示：{Truncate(resp.Error, 48)}", isError: true);
                }

                NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                BazaarLauncherPlugin.Host?.EventBus.Publish(
                    new ActiveRulesUpdatedModEvent(_rulesVersion, _normalRules.SpecialModeId, _normalRules.PresetId, false));
                MarkHudDirty();
                if (_isOpen) RefreshModalContentsIfOpen();
            });
        });
    }

    // ── 构建三大全新原生级页签（由 BuildModalPanel 调用） ──────────────────────

    private void BuildExtendedTabs(Transform contentRoot)
    {
        BuildWorkshopAndModCenterTab(contentRoot);
        BuildSocialAndSpectateTab(contentRoot);
        BuildPerformanceAndDiagnosticsTab(contentRoot);
    }

    private void UpdateExtendedTabsVisibility()
    {
        if (_workshopTabRoot != null) _workshopTabRoot.SetActive(_activeTab == UiTab.Workshop);
        if (_socialTabRoot != null) _socialTabRoot.SetActive(_activeTab == UiTab.Social);
        if (_performanceTabRoot != null) _performanceTabRoot.SetActive(_activeTab == UiTab.Performance);

        if (_tabWorkshopBtn != null) StyleTab(_tabWorkshopBtn, _activeTab == UiTab.Workshop);
        if (_tabSocialBtn != null) StyleTab(_tabSocialBtn, _activeTab == UiTab.Social);
        if (_tabPerformanceBtn != null) StyleTab(_tabPerformanceBtn, _activeTab == UiTab.Performance);

        if (_activeTab == UiTab.Workshop) RefreshWorkshopTabUi();
        else if (_activeTab == UiTab.Social) RefreshSocialTabData();
        else if (_activeTab == UiTab.Performance) RefreshPerformanceTabFull();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 页签 3：自建工坊 & Mod 中心（含沙盒实验室快捷控制台）
    // ═════════════════════════════════════════════════════════════════════════

    private void BuildWorkshopAndModCenterTab(Transform parent)
    {
        _workshopTabRoot = _ui.CreateVerticalGroup("WorkshopTabRoot", parent, spacing: 10f);

        // 0. 服务器切换、接收开关、社区 Mod 下载与本地 Mod 上传
        BuildServerAndCommunitySections(_workshopTabRoot.transform);

        // 1. 顶部 7 模式快速切换卡 + Mod 同步控制栏
        var syncCard = _ui.CreateCard("WorkshopSyncCard", _workshopTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 8f);
        _ui.CreateText("Title", syncCard.transform, "自建工坊 & Mod 扩展中心", 16f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _workshopSyncSummaryText = _ui.CreateText("SyncSummary", syncCard.transform, "同步状态：准备就绪", 13f, NativeUguiFactory.ColCream);

        var actionRow = _ui.CreateHorizontalRow("SyncActions", syncCard.transform, spacing: 8f);
        var btnSyncNow = _ui.CreateButton("BtnSyncMods", actionRow.transform, "立即同步 Mod / PNG / 词典", 13f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                SetStatus("正在从服务端同步 Mod 清单、PNG 卡图与四层中文词典...", isError: false);
                ModContentSyncClient.Instance.TriggerFullSyncInBackground(forceDownload: true);
                RefreshWorkshopTabUi();
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnSyncNow.Root, minW: 210f, prefW: 225f, minH: 32f);

        var btnServerReload = _ui.CreateButton("BtnServerReload", actionRow.transform, "请求服务端热重载 Mod", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                _ = Task.Run(async () =>
                {
                    var resp = await NameAuth.PostJsonAsync("/api/mods/reload", new { }).ConfigureAwait(false);
                    if (resp.Ok)
                    {
                        await ModContentSyncClient.Instance.SyncAllAsync(forceRefresh: true, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                    }
                    _pendingUpdates.Enqueue(() =>
                    {
                        SetStatus(resp.Ok ? "服务端 Mod 与四层词典热重载成功" : "服务端热重载失败：" + resp.Error, !resp.Ok);
                        RefreshWorkshopTabUi();
                    });
                });
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnServerReload.Root, minW: 178f, prefW: 185f, minH: 32f);

        var btnExtReload = _ui.CreateButton("BtnExtReload", actionRow.transform, "热重载本地外部 Mod DLL", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                int n = BazaarLauncherPlugin.Host?.ReloadExternalMods() ?? 0;
                SetStatus($"已完成本地外部 Mod 影子热重载，当前共加载 {n} 个外部扩展", isError: false);
                RefreshWorkshopTabUi();
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnExtReload.Root, minW: 185f, prefW: 195f, minH: 32f);

        // 2. 服务端已同步 Mod 列表（含《大巴扎编年史》示例包）
        var serverModsCard = _ui.CreateCard("ServerModsCard", _workshopTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        _ui.CreateText("Title", serverModsCard.transform, "服务端已同步 Mod 扩展包（支持运行时启停）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _serverModsListContainer = _ui.CreateVerticalGroup("ServerModsList", serverModsCard.transform, spacing: 5f).transform;

        // 3. 全部已同步自定义卡牌 / 技能 / 事件预览目录
        var contentCard = _ui.CreateCard("CustomContentCard", _workshopTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        _ui.CreateText("Title", contentCard.transform, "自定义卡牌 / 技能 / 奇遇视觉与文案绑定目录", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _customContentListContainer = _ui.CreateVerticalGroup("CustomContentList", contentCard.transform, spacing: 4f).transform;

        // 4. 沙盒实验室快捷控制台（仅在沙盒局激活，非沙盒局清晰标灰提示）
        _sandboxSectionGo = _ui.CreateCard("SandboxConsoleCard", _workshopTabRoot.transform, NativeUguiFactory.ColAmberBg, NativeUguiFactory.ColGold, padH: 14, padV: 10, spacing: 8f);
        _ui.CreateText("Title", _sandboxSectionGo.transform, "沙盒实验室快捷控制台（仅沙盒局激活 · 严格三禁隔离不污染榜单/镜像）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);

        var sbRow1 = _ui.CreateHorizontalRow("SandboxGrantRow", _sandboxSectionGo.transform, spacing: 8f);
        _sandboxCardIdInput = _ui.CreateInputField("SandboxCardId", sbRow1.transform, "输入卡牌 ID、短 ID 或中文名...", "", fontSize: 13.5f, charLimit: 80);
        NativeUguiFactory.SetLayout(_sandboxCardIdInput.gameObject, minW: 240f, flexW: 1f, minH: 30f, prefH: 30f);

        var btnGrantCard = _ui.CreateButton("BtnGrantCard", sbRow1.transform, "发到手牌 (黄金)", 13f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                string cid = _sandboxCardIdInput?.text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(cid))
                {
                    SetStatus("请先输入要发放的卡牌 ID、短 ID 或中文名", true);
                    ShowToastBanner("请先输入要发放的卡牌 ID、短 ID 或中文名", isError: true);
                    return;
                }
                SendSandboxApi("/api/sandbox/grant-card", new { templateId = cid, tier = "Gold", section = "Hand" }, "已发放测试卡牌");
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnGrantCard.Root, minW: 140f, prefW: 150f, minH: 30f);

        var sbRow2 = _ui.CreateHorizontalRow("SandboxStatsRow", _sandboxSectionGo.transform, spacing: 8f);
        _sandboxGoldInput = _ui.CreateInputField("SandboxGold", sbRow2.transform, "金币", "999", fontSize: 13.5f, charLimit: 6, contentType: TMP_InputField.ContentType.IntegerNumber);
        NativeUguiFactory.SetLayout(_sandboxGoldInput.gameObject, minW: 84f, prefW: 90f, minH: 30f, prefH: 30f);
        _sandboxHpInput = _ui.CreateInputField("SandboxHp", sbRow2.transform, "最大生命", "2000", fontSize: 13.5f, charLimit: 6, contentType: TMP_InputField.ContentType.IntegerNumber);
        NativeUguiFactory.SetLayout(_sandboxHpInput.gameObject, minW: 94f, prefW: 100f, minH: 30f, prefH: 30f);

        var btnApplyStats = _ui.CreateButton("BtnSandboxStats", sbRow2.transform, "应用数值并全解锁手牌槽", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                int gold = int.TryParse(_sandboxGoldInput?.text, out int g) ? g : 999;
                int hp = int.TryParse(_sandboxHpInput?.text, out int h) ? h : 2000;
                SendSandboxApi("/api/sandbox/set-stats", new { gold, healthMax = hp, prestige = 99, unlockAllSlots = true }, "沙盒数值已生效");
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnApplyStats.Root, minW: 185f, prefW: 195f, minH: 30f);

        var btnMirrorTest = _ui.CreateButton("BtnSandboxMirror", sbRow2.transform, "自镜像测试战斗", 13f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
            {
                SendSandboxApi("/api/sandbox/test-combat", new { opponentSource = "self_mirror" }, "沙盒自镜像战斗推演完成");
            }, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnMirrorTest.Root, minW: 140f, prefW: 150f, minH: 30f);

        // v4：把沙盒里搭好的盘面存成对手剧本的某一天
        var sbRow3 = _ui.CreateHorizontalRow("SandboxLineupRow", _sandboxSectionGo.transform, spacing: 8f);
        var btnToLineup = _ui.CreateButton("BtnSandboxToLineup", sbRow3.transform, "把当前盘面存成方案某天的对手", 13f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, SpOpenCaptureSandboxPopup, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnToLineup.Root, minW: 230f, prefW: 240f, minH: 30f);
        var lineupTip = _ui.CreateText("Tip", sbRow3.transform, "在沙盒里搭好一套阵容存进方案，正常对局就能在那一天遇到它。", 12f, NativeUguiFactory.ColMuted);
        NativeUguiFactory.SetLayout(lineupTip.gameObject, flexW: 1f, minH: 30f);

        _sandboxResultText = _ui.CreateText("SandboxResult", _sandboxSectionGo.transform, "提示：请先在主大厅切换至「沙盒实验室」模式进入对局后使用。", 12.5f, NativeUguiFactory.ColCream);

        // 5. 本地 14 个内置模块 + 第三方扩展开关列表
        var localModsCard = _ui.CreateCard("LocalModulesCard", _workshopTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        _ui.CreateText("Title", localModsCard.transform, "本地 BazaarModKit 模块与外部扩展启停控制", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _localModulesListContainer = _ui.CreateVerticalGroup("LocalModulesList", localModsCard.transform, spacing: 4f).transform;

        // 6. 第三方 Mod 扩展自定义 UI 挂载区
        var extContainer = _ui.CreateCard("ExternalModTabsCard", _workshopTabRoot.transform, NativeUguiFactory.ColSubCardBg, NativeUguiFactory.ColGoldDim, padH: 12, padV: 8, spacing: 6f);
        _ui.CreateText("Title", extContainer.transform, "第三方 Mod 扩展自定义工作区（INativeUiHost.RegisterWorkshopTab）", 13.5f, NativeUguiFactory.ColMuted);
        _externalModTabsContainer = _ui.CreateVerticalGroup("ExtTabsHost", extContainer.transform, spacing: 4f).transform;

        _workshopTabRoot.SetActive(false);
    }

    private void SendSandboxApi(string endpoint, object payload, string okMsg)
    {
        _ = Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync(endpoint, payload).ConfigureAwait(false);
            JObject obj = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(resp.Body))
                    obj = JObject.Parse(resp.Body);
            }
            catch { }

            bool forceSyncRequired = resp.Ok && ((bool?)obj?["forceSyncRequired"] ?? false);
            string runId = (string)obj?["data"]?["runId"];
            string msgZh = (string)obj?["messageZh"];
            string errZh = (string)obj?["error"] ?? msgZh ?? resp.Error;

            // 先同步最新的实例视觉绑定，确保新生成的 Mod 卡实例能立即套上自定义 PNG 与中文文案
            if (forceSyncRequired)
            {
                try
                {
                    await ModContentSyncClient.Instance
                        .SyncActiveBindingsAsync(runId, HttpClientHub.ShutdownToken)
                        .ConfigureAwait(false);
                }
                catch { }
            }

            _pendingUpdates.Enqueue(() =>
            {
                if (resp.Ok)
                {
                    string displayMsg = !string.IsNullOrWhiteSpace(msgZh) ? msgZh : okMsg;
                    if (forceSyncRequired)
                    {
                        bool inRun = GameInspector.IsActiveInRunState(
                            GameInspector.GetActiveSceneName(),
                            GameInspector.GetAppStateName());

                        if (!inRun)
                        {
                            displayMsg += "（当前未处于局内，进局后会自动同步）";
                        }
                        else if (IsInActiveCombatPlayback())
                        {
                            displayMsg += "（正在播战斗，战后或下次操作会自动同步到棋盘）";
                        }
                        else if (TrySendSandboxSyncCheatOnMainThread())
                        {
                            StartCoroutine(ReapplySandboxVisualsDelayedCoroutine());
                        }
                        else
                        {
                            displayMsg += "（已写入对局，下次任意操作将自动同步到棋盘）";
                        }
                    }

                    if (_sandboxResultText != null) _sandboxResultText.text = displayMsg;
                    SetStatus(displayMsg, false);
                    ShowToastBanner(Truncate(displayMsg, 64), isError: false);
                }
                else
                {
                    string displayErr = !string.IsNullOrWhiteSpace(errZh) ? errZh : "沙盒请求失败";
                    if (_sandboxResultText != null) _sandboxResultText.text = $"沙盒提示：{displayErr}";
                    SetStatus("沙盒调用未生效：" + Truncate(displayErr, 72), true);
                    ShowToastBanner(Truncate(displayErr, 64), isError: true);
                }
            });
        });
    }

    private static bool TrySendSandboxSyncCheatOnMainThread()
    {
        try
        {
            var cmd = GameInspector.GetCmdCached();
            if (cmd == null) return false;
            var sendCheat = cmd.GetType().GetMethod("SendCheat");
            if (sendCheat == null) return false;
            sendCheat.Invoke(cmd, new object[] { new List<string> { "sandbox_sync" } });
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[SandboxSync] 发送 sandbox_sync 失败：{ex.Message}");
            return false;
        }
    }

    private IEnumerator ReapplySandboxVisualsDelayedCoroutine()
    {
        CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
        yield return new WaitForSecondsRealtime(0.35f);
        CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
        yield return new WaitForSecondsRealtime(0.65f);
        CustomContentVisualModule.Instance?.ReapplyActiveVisualsOnMainThread();
    }

    private void RefreshWorkshopTabUi()
    {
        if (_workshopTabRoot == null) return;

        RefreshServerAndCommunityUi();

        var sync = ModContentSyncClient.Instance;
        int boundVisuals = CustomContentVisualModule.Instance?.GetRegisteredBindingCount() ?? 0;
        int l10nEntries = LocalizationOverlayModule.Instance?.OverlayEntryCount ?? 0;
        if (_workshopSyncSummaryText != null)
        {
            _workshopSyncSummaryText.text =
                $"{sync.LastSyncSummary} | 生效卡图覆盖: {boundVisuals} 条 | 外挂四层词典: {l10nEntries} 条（rev={LocalizationOverlayModule.Instance?.OverlayRevision ?? "-"}）";
        }

        // 刷新服务端 Mod 列表
        if (_serverModsListContainer != null)
        {
            ClearChildren(_serverModsListContainer);
            var serverMods = sync.GetServerModsSnapshot();
            if (serverMods.Count == 0)
            {
                _ui.CreateText("Empty", _serverModsListContainer, "暂未检测到服务端 Mod 扩展包（可点击上方「立即同步」或确认服务端已启用 mods/ 目录）。", 13f, NativeUguiFactory.ColMuted);
            }
            else
            {
                foreach (var m in serverMods)
                {
                    var captured = m;
                    var row = _ui.CreateHorizontalRow("SrvMod_" + captured.Id, _serverModsListContainer, spacing: 8f);
                    string badge = captured.Enabled ? "[已启用]" : "[已停用]";
                    var info = _ui.CreateText(
                        "Info",
                        row.transform,
                        $"{badge} {captured.Name} v{captured.Version}（{captured.Id}）· 卡牌 {captured.CustomCardCount} / 事件 {captured.CustomEventCount} / 贴图 {captured.AssetCount}",
                        13f,
                        captured.Enabled ? NativeUguiFactory.ColEmeraldText : NativeUguiFactory.ColMuted,
                        wrap: true);
                    NativeUguiFactory.SetLayout(info.gameObject, flexW: 1f, minH: 26f);

                    var toggleBtn = _ui.CreateButton("Toggle", row.transform, captured.Enabled ? "停用" : "启用", 12.5f,
                        captured.Enabled ? NativeUguiFactory.ColDangerBg : NativeUguiFactory.ColGoldBtn,
                        NativeUguiFactory.ColBtnHover, Color.white, () =>
                        {
                            _ = Task.Run(async () =>
                            {
                                var resp = await NameAuth.PostJsonAsync(
                                    $"/api/mods/{Uri.EscapeDataString(captured.Id)}/toggle",
                                    new { enabled = !captured.Enabled }).ConfigureAwait(false);
                                if (resp.Ok)
                                {
                                    await ModContentSyncClient.Instance.SyncAllAsync(false, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                                }
                                _pendingUpdates.Enqueue(() =>
                                {
                                    SetStatus(resp.Ok ? $"已切换 Mod「{captured.Name}」状态" : "切换 Mod 状态失败：" + resp.Error, !resp.Ok);
                                    RefreshWorkshopTabUi();
                                });
                            });
                        }, padH: 8, padV: 2);
                    NativeUguiFactory.SetLayout(toggleBtn.Root, minW: 72f, prefW: 76f, minH: 26f);
                }
            }
        }

        // 刷新自定义内容目录（最多展示前 24 项避免滚动卡顿）
        if (_customContentListContainer != null)
        {
            ClearChildren(_customContentListContainer);
            var contents = sync.GetCustomContentsSnapshot();
            if (contents.Count == 0)
            {
                _ui.CreateText("Empty", _customContentListContainer, "当前无自定义卡牌/技能/事件绑定条目。", 12.5f, NativeUguiFactory.ColMuted);
            }
            else
            {
                int show = Math.Min(contents.Count, 24);
                for (int i = 0; i < show; i++)
                {
                    var captured = contents[i];
                    string artMark = string.IsNullOrEmpty(captured.LocalArtPath) ? "原版底座图" : "自定义PNG已缓存";
                    var itemRow = _ui.CreateHorizontalRow("Item_" + i, _customContentListContainer, spacing: 6f);
                    var itemLbl = _ui.CreateText(
                        "Lbl",
                        itemRow.transform,
                        $"• [{captured.CardType}/{captured.Size}] {captured.NameZh}（{captured.CustomId}）— {artMark} {Truncate(captured.DescZh, 32)}",
                        12.5f,
                        NativeUguiFactory.ColCream);
                    NativeUguiFactory.SetLayout(itemLbl.gameObject, flexW: 1f, minH: 24f);

                    bool canGrant = string.Equals(captured.CardType, "Item", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(captured.CardType, "Skill", StringComparison.OrdinalIgnoreCase);
                    if (canGrant)
                    {
                        var fillBtn = _ui.CreateButton("BtnFill", itemRow.transform, "填入沙盒", 11.5f,
                            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                            {
                                if (_sandboxCardIdInput != null) _sandboxCardIdInput.text = captured.CustomId;
                                ShowToastBanner($"已填入沙盒输入框：{captured.NameZh}");
                            }, NativeUguiFactory.ColGoldDim, padH: 6, padV: 2);
                        NativeUguiFactory.SetLayout(fillBtn.Root, minW: 68f, prefW: 72f, minH: 24f);

                        var grantBtn = _ui.CreateButton("BtnGrant", itemRow.transform, "发到手牌", 11.5f,
                            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
                            {
                                if (_sandboxCardIdInput != null) _sandboxCardIdInput.text = captured.CustomId;
                                SendSandboxApi("/api/sandbox/grant-card",
                                    new { templateId = captured.CustomId, tier = "Gold", section = "Hand" },
                                    $"已发放 {captured.NameZh}");
                            }, NativeUguiFactory.ColGold, padH: 6, padV: 2);
                        NativeUguiFactory.SetLayout(grantBtn.Root, minW: 68f, prefW: 72f, minH: 24f);
                    }
                }
                if (contents.Count > show)
                {
                    _ui.CreateText("More", _customContentListContainer, $"...共 {contents.Count} 项自定义内容", 12f, NativeUguiFactory.ColMuted);
                }
            }
        }

        // 沙盒控制台仅在沙盒局激活交互提示
        bool isSandboxActive = string.Equals(_normalRules?.SpecialModeId, "SandboxLab", StringComparison.OrdinalIgnoreCase)
            && GameInspector.IsActiveInRunState(GameInspector.GetActiveSceneName(), GameInspector.GetAppStateName());
        if (_sandboxResultText != null && !isSandboxActive)
        {
            _sandboxResultText.text = "当前非沙盒对局（沙盒发卡/调数值仅在「沙盒实验室」对局中激活，确保标准对局与榜单绝对纯净）。";
        }

        // 刷新本地模块开关表
        if (_localModulesListContainer != null)
        {
            ClearChildren(_localModulesListContainer);
            var snaps = BazaarLauncherPlugin.Host?.GetHealthSnapshots() ?? Array.Empty<ModHealthSnapshot>();
            foreach (var s in snaps)
            {
                var captured = s;
                var row = _ui.CreateHorizontalRow("Mod_" + captured.ModId, _localModulesListContainer, spacing: 6f);
                string stateZh = FormatLifecycleStateZh(captured.State);
                Color col = captured.State == ModLifecycleState.Enabled ? NativeUguiFactory.ColEmeraldText
                    : captured.State == ModLifecycleState.Quarantined ? NativeUguiFactory.ColErrorText
                    : captured.State == ModLifecycleState.Degraded ? NativeUguiFactory.ColGold
                    : NativeUguiFactory.ColMuted;

                var lbl = _ui.CreateText(
                    "Lbl",
                    row.transform,
                    $"[{captured.Category}] {captured.Name} — {stateZh}（耗时 {captured.LoadDurationMs:0.0}ms，补丁 {captured.PatchSucceeded}/{captured.PatchAttempted}）",
                    12.5f,
                    col);
                NativeUguiFactory.SetLayout(lbl.gameObject, flexW: 1f, minH: 24f);

                if (captured.SupportsHotReload)
                {
                    var btn = _ui.CreateButton("Btn", row.transform, captured.Enabled ? "关闭" : "开启", 12f,
                        captured.Enabled ? NativeUguiFactory.ColBtnNormal : NativeUguiFactory.ColGoldBtn,
                        NativeUguiFactory.ColBtnHover, Color.white, () =>
                        {
                            BazaarLauncherPlugin.Host?.SetModuleEnabled(captured.ModId, !captured.Enabled);
                            RefreshWorkshopTabUi();
                        }, padH: 6, padV: 2);
                    NativeUguiFactory.SetLayout(btn.Root, minW: 62f, prefW: 64f, minH: 24f);
                }
            }
        }

        // 渲染第三方 Mod 注册的扩展工坊页签
        if (_externalModTabsContainer != null)
        {
            var extTabs = NativeMenuEmbedModule.Instance?.GetRegisteredWorkshopTabs();
            if (extTabs != null && extTabs.Count > 0)
            {
                ClearChildren(_externalModTabsContainer);
                foreach (var t in extTabs)
                {
                    var box = _ui.CreateVerticalGroup("ExtTab_" + t.Id, _externalModTabsContainer, spacing: 4f);
                    _ui.CreateText("Title", box.transform, t.Title, 13.5f, NativeUguiFactory.ColGold, FontStyles.Bold);
                    try { t.BuildBody?.Invoke(box.GetComponent<RectTransform>()); t.OnRefresh?.Invoke(); }
                    catch (Exception ex) { _ui.CreateText("Err", box.transform, "渲染异常：" + ex.Message, 12f, NativeUguiFactory.ColErrorText); }
                }
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 页签 4：多人社交与观战（房间浏览、密码建房/加房、实时聊天与播报、观战回放、排行榜）
    // ═════════════════════════════════════════════════════════════════════════

    private void BuildSocialAndSpectateTab(Transform parent)
    {
        _socialTabRoot = _ui.CreateVerticalGroup("SocialTabRoot", parent, spacing: 10f);

        // 1. 公开房 / 密码房快速创建与加入
        var roomCard = _ui.CreateCard("SocialRoomsCard", _socialTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 8f);
        var roomHeader = _ui.CreateHorizontalRow("Header", roomCard.transform, spacing: 8f);
        var roomTitle = _ui.CreateText("Title", roomHeader.transform, "联机公开房 / 密码房浏览器", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(roomTitle.gameObject, flexW: 1f, minH: 26f);

        var btnRefreshSocial = _ui.CreateButton("BtnRefreshSocial", roomHeader.transform, "刷新全部社交数据", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, RefreshSocialTabData, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnRefreshSocial.Root, minW: 148f, prefW: 156f, minH: 28f);

        var createRow = _ui.CreateHorizontalRow("CreateRow", roomCard.transform, spacing: 6f);
        _socialRoomNameInput = _ui.CreateInputField("RoomName", createRow.transform, "房间名称（例如 周末混池赛）", "", fontSize: 13.5f, charLimit: 32);
        NativeUguiFactory.SetLayout(_socialRoomNameInput.gameObject, minW: 200f, flexW: 1f, minH: 28f, prefH: 28f);
        _socialRoomPwdInput = _ui.CreateInputField("RoomPwd", createRow.transform, "可选密码", "", fontSize: 13.5f, charLimit: 32);
        NativeUguiFactory.SetLayout(_socialRoomPwdInput.gameObject, minW: 120f, prefW: 130f, minH: 28f, prefH: 28f);
        var btnCreateRoom = _ui.CreateButton("BtnCreate", createRow.transform, "创建房间", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, CreateSocialLobbyAsync, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnCreateRoom.Root, minW: 96f, prefW: 102f, minH: 28f);

        var joinRow = _ui.CreateHorizontalRow("JoinRow", roomCard.transform, spacing: 6f);
        _socialJoinCodeInput = _ui.CreateInputField("JoinCode", joinRow.transform, "输入 6 位房间码", "", fontSize: 13.5f, charLimit: 16);
        NativeUguiFactory.SetLayout(_socialJoinCodeInput.gameObject, minW: 160f, prefW: 180f, minH: 28f, prefH: 28f);
        _socialJoinPwdInput = _ui.CreateInputField("JoinPwd", joinRow.transform, "房间密码（无则留空）", "", fontSize: 13.5f, charLimit: 32);
        NativeUguiFactory.SetLayout(_socialJoinPwdInput.gameObject, minW: 150f, flexW: 1f, minH: 28f, prefH: 28f);
        var btnJoinRoom = _ui.CreateButton("BtnJoin", joinRow.transform, "密码加入", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                JoinSocialLobbyAsync(_socialJoinCodeInput?.text?.Trim(), _socialJoinPwdInput?.text?.Trim());
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btnJoinRoom.Root, minW: 96f, prefW: 102f, minH: 28f);

        _socialLobbiesContainer = _ui.CreateVerticalGroup("LobbiesList", roomCard.transform, spacing: 4f).transform;

        // 2. 实时房间/世界聊天与全服战况播报（对接 /api/social/chat、/api/lobbies/{id}/chat 与 SSE）
        var chatCard = _ui.CreateCard("SocialChatCard", _socialTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        var chatHdr = _ui.CreateHorizontalRow("ChatHdr", chatCard.transform, spacing: 8f);
        var chatTitle = _ui.CreateText("Title", chatHdr.transform, "实时聊天与系统战况播报（支持 SSE 推送）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(chatTitle.gameObject, flexW: 1f, minH: 26f);

        var btnChLobby = _ui.CreateButton("BtnChLobby", chatHdr.transform, "房间频道", 12f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                _activeChatChannel = "lobby";
                EnsureSocialSseSubscribed();
                RefreshSocialChatAsync();
            });
        NativeUguiFactory.SetLayout(btnChLobby.Root, minW: 82f, prefW: 86f, minH: 26f);

        var btnChGlobal = _ui.CreateButton("BtnChGlobal", chatHdr.transform, "世界/播报", 12f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                _activeChatChannel = "global";
                EnsureSocialSseSubscribed();
                RefreshSocialChatAsync();
            });
        NativeUguiFactory.SetLayout(btnChGlobal.Root, minW: 86f, prefW: 92f, minH: 26f);

        _socialChatMessagesContainer = _ui.CreateVerticalGroup("ChatMessages", chatCard.transform, spacing: 3f).transform;

        var sendRow = _ui.CreateHorizontalRow("SendRow", chatCard.transform, spacing: 6f);
        _socialChatInput = _ui.CreateInputField("ChatInput", sendRow.transform, "输入聊天消息（最多 200 字，自动防富文本注入）...", "", fontSize: 13.5f, charLimit: 200);
        NativeUguiFactory.SetLayout(_socialChatInput.gameObject, minW: 320f, flexW: 1f, minH: 30f, prefH: 30f);
        var btnSendChat = _ui.CreateButton("BtnSendChat", sendRow.transform, "发送", 13f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, SendSocialChatMessageAsync, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnSendChat.Root, minW: 84f, prefW: 88f, minH: 30f);

        // 3. 实时观战入口与历史战报回放
        var specCard = _ui.CreateCard("SocialSpectateCard", _socialTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        _ui.CreateText("Title", specCard.transform, "实时观战对局列表 & 我的历史战报回放", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _socialSpectateContainer = _ui.CreateVerticalGroup("SpectateList", specCard.transform, spacing: 4f).transform;
        _socialReplaysContainer = _ui.CreateVerticalGroup("ReplaysList", specCard.transform, spacing: 4f).transform;

        // 4. 模式排行榜（D6-P1-11：完整补齐全部 7 榜：无尽/Boss车轮/每日/排位/肉鸽词条/跨职业/锦标赛）
        var lbCard = _ui.CreateCard("SocialLeaderboardCard", _socialTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 6f);
        var lbTitle = _ui.CreateText("Title", lbCard.transform, "玩法模式排行榜（7 大全模式榜单）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        NativeUguiFactory.SetLayout(lbTitle.gameObject, minH: 24f);

        var lbRow1 = _ui.CreateHorizontalRow("LbRow1", lbCard.transform, spacing: 6f);
        var lbRow2 = _ui.CreateHorizontalRow("LbRow2", lbCard.transform, spacing: 6f);
        for (int bi = 0; bi < LeaderboardBoards.Length; bi++)
        {
            string k = LeaderboardBoards[bi].Key;
            string label = LeaderboardBoards[bi].LabelZh;
            Transform rowParent = bi < 4 ? lbRow1.transform : lbRow2.transform;
            var b = _ui.CreateButton("Lb_" + k, rowParent, label, 12f,
                NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                {
                    _activeLeaderboardKey = k;
                    RefreshLeaderboardAsync();
                });
            NativeUguiFactory.SetLayout(b.Root, minW: 100f, flexW: 1f, minH: 26f);
        }
        _socialLeaderboardContainer = _ui.CreateVerticalGroup("LbList", lbCard.transform, spacing: 4f).transform;

        _socialTabRoot.SetActive(false);
    }

    private void RefreshSocialTabData()
    {
        EnsureSocialSseSubscribed();
        RefreshPublicLobbiesAsync();
        RefreshSocialChatAsync();
        RefreshSpectateAndReplaysAsync();
        RefreshLeaderboardAsync();
    }

    private void CreateSocialLobbyAsync()
    {
        string roomName = string.IsNullOrWhiteSpace(_socialRoomNameInput?.text)
            ? $"{NameAuth.CurrentUsername} 的自建房间"
            : _socialRoomNameInput.text.Trim();
        string pwd = _socialRoomPwdInput?.text?.Trim() ?? string.Empty;

        // D4-P1-4：在主线程捕获不可变规则快照，禁止在 Task.Run 后台读取可变 _normalRules / _lobbyRules
        int capSnap = Math.Clamp(_lobbyRules?.PlayerCap ?? 8, 2, 16);
        string modeSnap = _normalRules?.SpecialModeId ?? "Standard";
        string presetSnap = _normalRules?.PresetId ?? "PureOfficial";
        var mutatorsSnap = _normalRules?.MutatorIds != null ? new List<string>(_normalRules.MutatorIds) : new List<string>();
        var modsSnap = _lobbyRules?.SelectedModIds != null ? new List<string>(_lobbyRules.SelectedModIds) : new List<string>();

        _ = Task.Run(async () =>
        {
            var payload = new
            {
                name = roomName,
                password = string.IsNullOrEmpty(pwd) ? null : pwd,
                isPublic = true,
                playerCap = capSnap,
                modeId = modeSnap,
                presetId = presetSnap,
                mutatorIds = mutatorsSnap,
                selectedModIds = modsSnap,
            };
            var resp = await NameAuth.PostJsonAsync("/lobbies", payload).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                SetStatus(resp.Ok ? $"已创建联机房间「{roomName}」" : "创建房间失败：" + resp.Error, !resp.Ok);
                if (resp.Ok) RefreshPublicLobbiesAsync();
            });
        });
    }

    private void JoinSocialLobbyAsync(string code, string password)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            SetStatus("请输入房间码", true);
            return;
        }

        _ = Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync(
                $"/lobbies/{Uri.EscapeDataString(code)}/members",
                new { password = string.IsNullOrEmpty(password) ? null : password }).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                SetStatus(resp.Ok ? $"已加入房间 {code}" : "加入房间失败：" + resp.Error, !resp.Ok);
                if (resp.Ok)
                {
                    RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false);
                    RefreshPublicLobbiesAsync();
                }
            });
        });
    }

    private void RefreshPublicLobbiesAsync()
    {
        _ = Task.Run(async () =>
        {
            var resp = await NameAuth.GetJsonAsync("/lobbies?limit=30&includeStartedSpectatable=true").ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                if (_socialLobbiesContainer == null) return;
                ClearChildren(_socialLobbiesContainer);
                if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
                {
                    _ui.CreateText("Empty", _socialLobbiesContainer, "暂未获取到公开房间列表。", 12.5f, NativeUguiFactory.ColMuted);
                    return;
                }
                try
                {
                    var arr = JArray.Parse(resp.Body);
                    if (arr.Count == 0)
                    {
                        _ui.CreateText("Empty", _socialLobbiesContainer, "当前暂无公开房间，可在上方一键创建。", 12.5f, NativeUguiFactory.ColMuted);
                        return;
                    }
                    int shown = Math.Min(arr.Count, 12);
                    for (int i = 0; i < shown; i++)
                    {
                        if (arr[i] is not JObject lo) continue;
                        string code = (string)(lo["Code"] ?? lo["code"]) ?? "";
                        string name = (string)(lo["Name"] ?? lo["name"]) ?? code;
                        string host = (string)(lo["HostUsername"] ?? lo["hostUsername"]) ?? "-";
                        int joined = (int?)(lo["JoinedCount"] ?? lo["joinedCount"]) ?? 0;
                        int cap = (int?)(lo["MaxPlayers"] ?? lo["PlayerCap"] ?? lo["playerCap"]) ?? 8;
                        bool hasPwd = (bool?)(lo["HasPassword"] ?? lo["hasPassword"]) ?? false;
                        string mode = GetModeDisplayZh((string)(lo["ModeId"] ?? lo["modeId"]), (string)(lo["PresetId"] ?? lo["presetId"]));

                        var row = _ui.CreateHorizontalRow("Lobby_" + code, _socialLobbiesContainer, spacing: 6f);
                        string pwdBadge = hasPwd ? "[密码]" : "[公开]";
                        var txt = _ui.CreateText("Txt", row.transform,
                            $"{pwdBadge} {name}（房码 {code}）· 房主 {host} · {joined}/{cap}人 · {mode}",
                            12.5f, NativeUguiFactory.ColCream);
                        NativeUguiFactory.SetLayout(txt.gameObject, flexW: 1f, minH: 24f);

                        string c = code;
                        var btn = _ui.CreateButton("Join", row.transform, "加入", 12f,
                            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColGold, () =>
                            {
                                JoinSocialLobbyAsync(c, _socialJoinPwdInput?.text?.Trim());
                            }, padH: 8, padV: 2);
                        NativeUguiFactory.SetLayout(btn.Root, minW: 56f, prefW: 60f, minH: 24f);
                    }
                }
                catch { }
            });
        });
    }

    private void RefreshSocialChatAsync()
    {
        string targetChannel = _activeChatChannel;
        string lobbyId = _currentLobby?.LobbyId ?? string.Empty;
        bool isLobbyChannel = string.Equals(targetChannel, "lobby", StringComparison.OrdinalIgnoreCase);

        if (isLobbyChannel && string.IsNullOrEmpty(lobbyId))
        {
            _socialMessages.Clear();
            RenderSocialChatLines();
            return;
        }

        _ = Task.Run(async () =>
        {
            var parsed = new List<(long Id, string Channel, string Sender, string Content)>();
            var seenIds = new HashSet<long>();

            void CollectFromJson(string body, string fallbackChannel)
            {
                if (string.IsNullOrWhiteSpace(body)) return;
                try
                {
                    var token = JToken.Parse(body);
                    JArray arr = token as JArray ?? token["messages"] as JArray ?? token["items"] as JArray;
                    if (arr == null) return;
                    foreach (var item in arr)
                    {
                        if (item is not JObject mo) continue;
                        long id = (long?)(mo["id"] ?? mo["Id"]) ?? 0L;
                        if (id > 0 && !seenIds.Add(id)) continue;
                        string ch = (string)(mo["channelType"] ?? mo["channel"]) ?? fallbackChannel;
                        string sender = (string)(mo["senderName"] ?? mo["sender"]) ?? "系统";
                        string content = (string)(mo["content"] ?? mo["message"]) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(content)) continue;
                        parsed.Add((id, ch, sender, content));
                    }
                }
                catch { }
            }

            if (isLobbyChannel)
            {
                var resp = await NameAuth.GetJsonAsync($"/api/lobbies/{Uri.EscapeDataString(lobbyId)}/chat?limit=25").ConfigureAwait(false);
                if (resp.Ok) CollectFromJson(resp.Body, "lobby");
            }
            else
            {
                // 世界频道同时拉取玩家世界聊天与系统播报，按消息 ID 去重并按时间序合并，避免只拉公告导致玩家发言刷新消失
                var chatTask = NameAuth.GetJsonAsync("/api/social/chat?channel=global&limit=25");
                var annTask = NameAuth.GetJsonAsync("/api/social/announcements?limit=15");
                await Task.WhenAll(chatTask, annTask).ConfigureAwait(false);
                if (chatTask.Result.Ok) CollectFromJson(chatTask.Result.Body, "global");
                if (annTask.Result.Ok) CollectFromJson(annTask.Result.Body, "system");
            }

            parsed.Sort((a, b) => a.Id.CompareTo(b.Id));
            if (parsed.Count > 40)
            {
                parsed.RemoveRange(0, parsed.Count - 40);
            }

            _pendingUpdates.Enqueue(() =>
            {
                // 若拉取期间玩家已切换频道，丢弃过期结果防止串频
                if (!string.Equals(_activeChatChannel, targetChannel, StringComparison.OrdinalIgnoreCase))
                    return;

                _socialMessages.Clear();
                long maxId = Interlocked.Read(ref _lastSocialEventId);
                for (int i = 0; i < parsed.Count; i++)
                {
                    _socialMessages.Add(parsed[i]);
                    if (parsed[i].Id > maxId) maxId = parsed[i].Id;
                }
                if (maxId > 0) Interlocked.Exchange(ref _lastSocialEventId, maxId);
                RenderSocialChatLines();
            });
        });
    }

    private void UpsertSocialMessageOnMain(long id, string channel, string sender, string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;
        if (id > 0)
        {
            for (int i = 0; i < _socialMessages.Count; i++)
            {
                if (_socialMessages[i].Id == id) return;
            }
            long curMax = Interlocked.Read(ref _lastSocialEventId);
            if (id > curMax) Interlocked.Exchange(ref _lastSocialEventId, id);
        }

        _socialMessages.Add((id, channel ?? _activeChatChannel, string.IsNullOrWhiteSpace(sender) ? "系统" : sender, content));
        if (_socialMessages.Count > 40) _socialMessages.RemoveAt(0);
    }

    private void SendSocialChatMessageAsync()
    {
        string raw = _socialChatInput?.text?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        string targetChannel = _activeChatChannel;
        string lobbyId = _currentLobby?.LobbyId ?? string.Empty;
        bool isLobbyChannel = string.Equals(targetChannel, "lobby", StringComparison.OrdinalIgnoreCase);
        if (isLobbyChannel && string.IsNullOrEmpty(lobbyId))
        {
            SetStatus("当前未加入锦标赛房间，无法在房间频道发言（可切换至「世界/播报」频道）。", true);
            return;
        }

        if (raw.Length > 200) raw = raw.Substring(0, 200);
        // 客户端二次剥离尖括号防 Unity TMP 富文本标签注入
        string clean = raw.Replace("<", "＜").Replace(">", "＞");
        if (_socialChatInput != null) _socialChatInput.text = string.Empty;

        _ = Task.Run(async () =>
        {
            string path = isLobbyChannel
                ? $"/api/lobbies/{Uri.EscapeDataString(lobbyId)}/chat"
                : "/api/social/chat";

            var resp = await NameAuth.PostJsonAsync(path, new { content = clean, channel = targetChannel }).ConfigureAwait(false);
            long msgId = 0L;
            string respChannel = targetChannel;
            string respSender = NameAuth.CurrentUsername;
            string respContent = clean;

            if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
            {
                try
                {
                    var jo = JObject.Parse(resp.Body);
                    msgId = (long?)(jo["id"] ?? jo["Id"]) ?? 0L;
                    respChannel = (string)(jo["channelType"] ?? jo["channel"]) ?? targetChannel;
                    respSender = (string)(jo["senderName"] ?? jo["sender"]) ?? respSender;
                    respContent = (string)(jo["content"] ?? jo["message"]) ?? clean;
                }
                catch { }
            }

            _pendingUpdates.Enqueue(() =>
            {
                if (resp.Ok)
                {
                    if (string.Equals(_activeChatChannel, targetChannel, StringComparison.OrdinalIgnoreCase))
                    {
                        UpsertSocialMessageOnMain(msgId, respChannel, respSender, respContent);
                        RenderSocialChatLines();
                    }
                }
                else
                {
                    SetStatus("发送消息失败：" + resp.Error, true);
                }
            });
        });
    }

    private void RenderSocialChatLines()
    {
        if (_socialChatMessagesContainer == null) return;
        ClearChildren(_socialChatMessagesContainer);
        if (_socialMessages.Count == 0)
        {
            string emptyHint = string.Equals(_activeChatChannel, "lobby", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(_currentLobby?.LobbyId)
                ? "当前未加入锦标赛房间，加入或创建房间后可开启房间频道聊天。"
                : "暂无聊天或系统播报消息。";
            _ui.CreateText("Empty", _socialChatMessagesContainer, emptyHint, 12.5f, NativeUguiFactory.ColMuted);
            return;
        }
        int start = Math.Max(0, _socialMessages.Count - 12);
        for (int i = start; i < _socialMessages.Count; i++)
        {
            var m = _socialMessages[i];
            bool isSys = string.Equals(m.Sender, "系统", StringComparison.Ordinal) || m.Channel == "system";
            _ui.CreateText(
                "Msg_" + i,
                _socialChatMessagesContainer,
                $"[{m.Sender}] {m.Content}",
                12.5f,
                isSys ? NativeUguiFactory.ColGold : NativeUguiFactory.ColCream);
        }
    }

    private void StopSocialSse()
    {
        var prev = Interlocked.Exchange(ref _socialSseHandle, null);
        prev?.CancelAndDisposeOnce();
        _subscribedSseKey = string.Empty;
    }

    private string ComputeDesiredSseTopic(out string sseUrl)
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        string lobbyId = _currentLobby?.LobbyId ?? string.Empty;
        bool useLobbyTopic = string.Equals(_activeChatChannel, "lobby", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(lobbyId);
        if (useLobbyTopic)
        {
            sseUrl = $"{baseUrl}/api/social/events?scope=lobby&lobbyId={Uri.EscapeDataString(lobbyId)}";
            return "lobby:" + lobbyId;
        }
        sseUrl = $"{baseUrl}/api/social/events?scope=global";
        return "global";
    }

    private void SyncSocialSseForLobbyOrPanelState()
    {
        bool shouldKeepAlive = (_isOpen && _activeTab == UiTab.Social)
            || IsActiveRunTournamentMode()
            || IsTournamentUiContext();
        if (!shouldKeepAlive)
        {
            StopSocialSse();
            return;
        }

        string desiredKey = ComputeDesiredSseTopic(out _);
        var current = Volatile.Read(ref _socialSseHandle);
        if (current == null || current.IsCancellationRequested
            || !string.Equals(_subscribedSseKey, desiredKey, StringComparison.Ordinal))
        {
            EnsureSocialSseSubscribed();
        }
    }

    private void EnsureSocialSseSubscribed()
    {
        string baseUrl = NameAuth.GetServerBaseUrl();
        if (string.IsNullOrWhiteSpace(baseUrl)) return;

        string desiredKey = ComputeDesiredSseTopic(out string sseUrl);
        var existing = Volatile.Read(ref _socialSseHandle);
        if (existing != null && !existing.IsCancellationRequested
            && string.Equals(_subscribedSseKey, desiredKey, StringComparison.Ordinal))
        {
            return;
        }

        _subscribedSseKey = desiredKey;
        Interlocked.Exchange(ref _lastSocialEventId, 0L);
        var handle = new SocialSseHandle(HttpClientHub.CreateLinkedCts());
        var token = handle.Cts.Token;
        // 无论旧句柄是否已请求取消，均通过原子交接确保旧 CTS 必被单次取消释放、零跨代污染零泄漏
        var prev = Interlocked.Exchange(ref _socialSseHandle, handle);
        prev?.CancelAndDisposeOnce();

        _ = Task.Run(async () =>
        {
            int backoffMs = 1000;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    bool receivedHealthyTraffic = false;
                    try
                    {
                        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, sseUrl);
                        NameAuth.ApplyAuthHeaders(req);
                        long lastId = Interlocked.Read(ref _lastSocialEventId);
                        if (lastId > 0)
                        {
                            req.Headers.TryAddWithoutValidation("Last-Event-ID", lastId.ToString());
                        }

                        using var resp = await HttpClientHub.SseClient.SendAsync(
                            req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode)
                        {
                            if ((int)resp.StatusCode == 404)
                            {
                                backoffMs = Math.Min(15000, Math.Max(backoffMs * 2, 5000));
                            }
                            else
                            {
                                backoffMs = Math.Min(15000, backoffMs * 2);
                            }
                            await Task.Delay(backoffMs, token).ConfigureAwait(false);
                            continue;
                        }

                        backoffMs = 1000;
                        receivedHealthyTraffic = true;

                        using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                        // D4-P1-5：netstandard2.1 无 ReadLineAsync(ct)，移除同步阻塞的 reader.EndOfStream，
                        // 注册取消回调主动 Dispose(stream) 即时唤醒挂起的 ReadLineAsync，零线程挂死
                        using var cancelCloseReg = token.Register(() =>
                        {
                            try { stream.Dispose(); } catch { }
                        });
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        while (!token.IsCancellationRequested)
                        {
                            string line;
                            try
                            {
                                line = await reader.ReadLineAsync().ConfigureAwait(false);
                            }
                            catch (ObjectDisposedException) when (token.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (IOException) when (token.IsCancellationRequested)
                            {
                                break;
                            }

                            if (line == null) break;
                            if (line.StartsWith("id:", StringComparison.Ordinal))
                            {
                                if (long.TryParse(line.Substring(3).Trim(), out long evId) && evId > 0)
                                {
                                    long curMax = Interlocked.Read(ref _lastSocialEventId);
                                    if (evId > curMax) Interlocked.Exchange(ref _lastSocialEventId, evId);
                                }
                                continue;
                            }
                            if (string.IsNullOrEmpty(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
                            string payload = line.Substring(5).Trim();
                            if (!payload.StartsWith("{", StringComparison.Ordinal)) continue;

                            try
                            {
                                var jo = JObject.Parse(payload);
                                long id = (long?)(jo["id"] ?? jo["Id"]) ?? 0L;
                                string ch = (string)(jo["channelType"] ?? jo["channel"]) ?? "system";
                                string sender = (string)(jo["senderName"] ?? jo["sender"]) ?? "系统";
                                string content = (string)(jo["content"] ?? jo["message"]) ?? string.Empty;
                                if (string.IsNullOrWhiteSpace(content)) continue;

                                if (id > 0)
                                {
                                    long curMax = Interlocked.Read(ref _lastSocialEventId);
                                    if (id > curMax) Interlocked.Exchange(ref _lastSocialEventId, id);
                                }

                                BazaarLauncherPlugin.Host?.EventBus.Publish(new SocialChatReceivedModEvent(id, ch, sender, content));
                                string capturedTopic = desiredKey;
                                _pendingUpdates.Enqueue(() =>
                                {
                                    if (!string.Equals(_subscribedSseKey, capturedTopic, StringComparison.Ordinal))
                                        return;
                                    UpsertSocialMessageOnMain(id, ch, sender, content);
                                    if (_isOpen && _activeTab == UiTab.Social) RenderSocialChatLines();
                                });
                            }
                            catch { }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // 网络闪断或服务端重启：按指数退避重连
                    }

                    if (token.IsCancellationRequested) break;
                    if (!receivedHealthyTraffic)
                    {
                        backoffMs = Math.Min(15000, backoffMs * 2);
                    }
                    try
                    {
                        await Task.Delay(backoffMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
            finally
            {
                // D4-P1-5：按句柄实例级归属与幂等 CancelAndDisposeOnce 释放，彻底消除跨代竞态与句柄泄漏
                if (Interlocked.CompareExchange(ref _socialSseHandle, null, handle) == handle)
                {
                    _subscribedSseKey = string.Empty;
                }
                handle.CancelAndDisposeOnce();
            }
        });
    }

    private void RefreshSpectateAndReplaysAsync()
    {
        _ = Task.Run(async () =>
        {
            var specResp = await NameAuth.GetJsonAsync("/spectate").ConfigureAwait(false);
            var repResp = await NameAuth.GetJsonAsync("/api/replays/me?limit=10").ConfigureAwait(false);

            _pendingUpdates.Enqueue(() =>
            {
                if (_socialSpectateContainer != null)
                {
                    ClearChildren(_socialSpectateContainer);
                    if (specResp.Ok && !string.IsNullOrWhiteSpace(specResp.Body))
                    {
                        try
                        {
                            var arr = JArray.Parse(specResp.Body);
                            if (arr.Count == 0)
                            {
                                _ui.CreateText("Empty", _socialSpectateContainer, "当前暂无进行中的可观战锦标赛。", 12.5f, NativeUguiFactory.ColMuted);
                            }
                            else
                            {
                                int cnt = Math.Min(arr.Count, 6);
                                for (int i = 0; i < cnt; i++)
                                {
                                    if (arr[i] is not JObject so) continue;
                                    string code = (string)(so["WatchCode"] ?? so["Code"] ?? so["watchCode"]) ?? "-";
                                    string name = (string)(so["Name"] ?? so["name"]) ?? code;
                                    int pCount = (so["Players"] as JArray)?.Count ?? 0;

                                    var specRow = _ui.CreateHorizontalRow("SpecRow_" + i, _socialSpectateContainer, spacing: 6f);
                                    var specTxt = _ui.CreateText("Spec_" + i, specRow.transform,
                                        $"• [实时观战] {name}（观战码 {code}）· 活跃选手 {pCount} 人",
                                        12.5f, NativeUguiFactory.ColEmeraldText);
                                    NativeUguiFactory.SetLayout(specTxt.gameObject, flexW: 1f, minH: 24f);

                                    // P0-26：观战入口主动调用 /api/mods/active-bindings?watchCode=... 同步该场观战的自定义 Mod 视觉绑定
                                    string capturedCode = code;
                                    var syncSpecBtn = _ui.CreateButton("SyncSpec_" + i, specRow.transform, "同步观战Mod", 11.5f,
                                        NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColGold, () =>
                                        {
                                            SetStatus($"正在同步观战房 {capturedCode} 的自定义 Mod 贴图与文案...", false);
                                            _ = Task.Run(async () =>
                                            {
                                                await ModContentSyncClient.Instance.SyncActiveBindingsAsync(
                                                    watchCode: capturedCode,
                                                    ct: HttpClientHub.ShutdownToken).ConfigureAwait(false);
                                                _pendingUpdates.Enqueue(() =>
                                                {
                                                    SetStatus($"已同步观战房 {capturedCode} 的活跃 Mod 绑定", false);
                                                    ShowToastBanner($"已加载观战房 {capturedCode} Mod 视觉");
                                                });
                                            });
                                        }, padH: 8, padV: 2);
                                    NativeUguiFactory.SetLayout(syncSpecBtn.Root, minW: 96f, prefW: 102f, minH: 24f);
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        _ui.CreateText("Empty", _socialSpectateContainer, "观战列表暂不可用。", 12.5f, NativeUguiFactory.ColMuted);
                    }
                }

                if (_socialReplaysContainer != null)
                {
                    ClearChildren(_socialReplaysContainer);
                    if (repResp.Ok && !string.IsNullOrWhiteSpace(repResp.Body))
                    {
                        try
                        {
                            var tok = JToken.Parse(repResp.Body);
                            var arr = tok as JArray ?? tok["items"] as JArray ?? tok["replays"] as JArray;
                            if (arr == null || arr.Count == 0)
                            {
                                _ui.CreateText("Empty", _socialReplaysContainer, "暂无历史战报记录。", 12.5f, NativeUguiFactory.ColMuted);
                            }
                            else
                            {
                                string viewerAcc = NameAuth.CurrentAccountId;
                                int cnt = Math.Min(arr.Count, 8);
                                for (int i = 0; i < cnt; i++)
                                {
                                    if (arr[i] is not JObject ro) continue;
                                    string replayId = (string)(ro["id"] ?? ro["Id"] ?? ro["replayId"]) ?? string.Empty;
                                    string p1Acc = (string)(ro["p1AccountId"] ?? ro["P1AccountId"]) ?? string.Empty;
                                    string p2Acc = (string)(ro["p2AccountId"] ?? ro["P2AccountId"]) ?? string.Empty;
                                    string oppAcc = (string)(ro["opponentAccountId"] ?? ro["OpponentAccountId"]) ?? string.Empty;
                                    string p1Name = (string)(ro["p1Username"] ?? ro["P1Username"]) ?? string.Empty;
                                    string p2Name = (string)(ro["p2Username"] ?? ro["P2Username"] ?? ro["opponentName"] ?? ro["OpponentName"]) ?? "对手";
                                    string winAcc = (string)(ro["winnerAccountId"] ?? ro["WinnerAccountId"]) ?? string.Empty;
                                    string winSide = (string)(ro["winnerSide"] ?? ro["WinnerSide"] ?? ro["winner"]) ?? string.Empty;
                                    bool? isDraw = (bool?)(ro["isDraw"] ?? ro["IsDraw"]);
                                    int day = (int?)(ro["day"] ?? ro["Day"]) ?? 1;
                                    string mode = (string)(ro["modeId"] ?? ro["combatType"]) ?? "PVP";

                                    // D6-P1-12：按当前查看账号视角精确判定胜/负/平与对手昵称
                                    var outcome = ReplayOutcomeResolver.ResolvePerspective(
                                        viewerAcc, p1Acc, p2Acc, oppAcc, p1Name, p2Name, winAcc, winSide, isDraw);
                                    Color col = outcome.IsDraw
                                        ? NativeUguiFactory.ColGold
                                        : (outcome.IsWin ? NativeUguiFactory.ColEmeraldText : NativeUguiFactory.ColCream);

                                    var repRow = _ui.CreateHorizontalRow("RepRow_" + i, _socialReplaysContainer, spacing: 6f);
                                    var repTxt = _ui.CreateText("Rep_" + i, repRow.transform,
                                        $"• [第{day}天 {mode}] vs {outcome.OpponentName} · 结果: {outcome.LabelZh}",
                                        12.5f, col);
                                    NativeUguiFactory.SetLayout(repTxt.gameObject, flexW: 1f, minH: 24f);

                                    // P0-26：加载历史战报内的视觉绑定快照（兼容 visualBindings / visualBindingsJson）
                                    if (!string.IsNullOrEmpty(replayId))
                                    {
                                        string capturedId = replayId;
                                        var snapBtn = _ui.CreateButton("RepSnap_" + i, repRow.transform, "载入回放Mod", 11.5f,
                                            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
                                            {
                                                LoadReplayVisualBindingsByIdAsync(capturedId);
                                            }, padH: 8, padV: 2);
                                        NativeUguiFactory.SetLayout(snapBtn.Root, minW: 92f, prefW: 98f, minH: 24f);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            });
        });
    }

    private void LoadReplayVisualBindingsByIdAsync(string replayId)
    {
        if (string.IsNullOrWhiteSpace(replayId)) return;
        SetStatus($"正在载入历史战报 {replayId} 的自定义 Mod 快照...", false);
        _ = Task.Run(async () =>
        {
            var resp = await NameAuth.GetJsonAsync($"/api/replays/{Uri.EscapeDataString(replayId)}").ConfigureAwait(false);
            bool appliedSnap = false;
            if (resp.Ok && !string.IsNullOrWhiteSpace(resp.Body))
            {
                try
                {
                    var jo = JObject.Parse(resp.Body);
                    JToken snapTok = jo["visualBindings"] ?? jo["VisualBindings"] ?? jo["visualBindingsJson"] ?? jo["VisualBindingsJson"];
                    if (snapTok != null)
                    {
                        await ModContentSyncClient.Instance.ApplyReplayVisualBindingsSnapshotAsync(
                            snapTok, HttpClientHub.ShutdownToken).ConfigureAwait(false);
                        appliedSnap = true;
                    }
                }
                catch { }
            }
            _pendingUpdates.Enqueue(() =>
            {
                if (!resp.Ok)
                {
                    SetStatus("拉取战报详情失败：" + resp.Error, true);
                }
                else
                {
                    SetStatus(appliedSnap ? $"已载入战报 {replayId} 的自定义卡图快照" : $"战报 {replayId} 未包含额外自定义 Mod 绑定", false);
                }
            });
        });
    }

    private void RefreshLeaderboardAsync()
    {
        string key = _activeLeaderboardKey;
        _ = Task.Run(async () =>
        {
            string url = key == "daily:today"
                ? "/api/daily-challenge/today"
                : $"/api/leaderboards/{Uri.EscapeDataString(key)}?limit=15";
            var resp = await NameAuth.GetJsonAsync(url).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                if (_socialLeaderboardContainer == null) return;
                ClearChildren(_socialLeaderboardContainer);
                if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
                {
                    _ui.CreateText("Empty", _socialLeaderboardContainer, "该榜单暂无上榜记录。", 12.5f, NativeUguiFactory.ColMuted);
                    return;
                }
                try
                {
                    var tok = JToken.Parse(resp.Body);
                    var arr = tok as JArray ?? tok["items"] as JArray ?? tok["topLeaderboard"] as JArray ?? tok["entries"] as JArray ?? tok["leaderboard"] as JArray;
                    if (arr == null || arr.Count == 0)
                    {
                        _ui.CreateText("Empty", _socialLeaderboardContainer, "当前榜单虚位以待！", 12.5f, NativeUguiFactory.ColMuted);
                        return;
                    }
                    int cnt = Math.Min(arr.Count, 12);
                    for (int i = 0; i < cnt; i++)
                    {
                        if (arr[i] is not JObject eo) continue;
                        string uname = (string)(eo["username"] ?? eo["Username"]) ?? "-";
                        string hero = (string)(eo["hero"] ?? eo["Hero"]) ?? "-";
                        int score = (int?)(eo["score"] ?? eo["Score"]) ?? 0;
                        int metric = (int?)(eo["primaryMetric"] ?? eo["dayReached"] ?? eo["victories"]) ?? 0;
                        _ui.CreateText("LbRow_" + i, _socialLeaderboardContainer,
                            $"#{i + 1}  {uname}（{hero}）— 积分 {score} | 成绩 {metric}",
                            12.5f, i < 3 ? NativeUguiFactory.ColGold : NativeUguiFactory.ColCream);
                    }
                }
                catch { }
            });
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // 页签 5：性能监控与诊断（FPS / 1%Low / 内存 / Ping / 10项优化开关 / 熔断诊断 / 导出）
    // ═════════════════════════════════════════════════════════════════════════

    private void BuildPerformanceAndDiagnosticsTab(Transform parent)
    {
        _performanceTabRoot = _ui.CreateVerticalGroup("PerformanceTabRoot", parent, spacing: 10f);
        _perfToggleRefreshers.Clear();

        // 1. 实时遥测仪表盘
        var liveCard = _ui.CreateCard("PerfLiveCard", _performanceTabRoot.transform, NativeUguiFactory.ColAmberBg, NativeUguiFactory.ColGold, padH: 14, padV: 10, spacing: 6f);
        _ui.CreateText("Title", liveCard.transform, "实时性能遥测与延迟监控", 16f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _perfLiveHeaderText = _ui.CreateText("LiveHeader", liveCard.transform, "FPS -- | 1% Low -- | 帧时间 -- ms | 托管堆 -- MB | Ping -- ms", 14.5f, NativeUguiFactory.ColEmeraldText, FontStyles.Bold);
        _perfLiveDetailText = _ui.CreateText("LiveDetail", liveCard.transform, "正在采集...", 12.5f, NativeUguiFactory.ColCream);

        // 一键三档预设 + 动作按钮
        var presetRow = _ui.CreateHorizontalRow("PresetRow", liveCard.transform, spacing: 8f);
        CreatePerfProfileBtn(presetRow.transform, "原版默认", "Official");
        CreatePerfProfileBtn(presetRow.transform, "均衡流畅 (推荐)", "Balanced");
        CreatePerfProfileBtn(presetRow.transform, "极致低配省显存", "UltraLow");

        var btnPing = _ui.CreateButton("BtnPing", presetRow.transform, "测速 /api/ping", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                PerfOptimizerModule.Instance?.TriggerPingProbeBackground();
                SetStatus("正在探测自建服 /api/ping 延迟...", false);
            });
        NativeUguiFactory.SetLayout(btnPing.Root, minW: 118f, prefW: 126f, minH: 30f);

        var btnClean = _ui.CreateButton("BtnClean", presetRow.transform, "温和回收内存", 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColCream, () =>
            {
                PerfOptimizerModule.Instance?.TriggerManualGentleCleanup();
                SetStatus("已触发非战斗安全增量 GC 与未引用纹理回收", false);
            });
        NativeUguiFactory.SetLayout(btnClean.Root, minW: 110f, prefW: 118f, minH: 30f);

        var btnExport = _ui.CreateButton("BtnExportDiag", presetRow.transform, "一键导出诊断报告", 12.5f,
            NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, ExportDiagnosticReportToDisk, NativeUguiFactory.ColGold);
        NativeUguiFactory.SetLayout(btnExport.Root, minW: 136f, prefW: 146f, minH: 30f);

        // 2. 10 项运行时 Harmony 性能优化独立开关
        var optCard = _ui.CreateCard("PerfOptsCard", _performanceTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 5f);
        _ui.CreateText("Title", optCard.transform, "10 项运行时 Harmony 性能优化开关（全部实时生效、可安全降级）", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);

        AddPerfToggle(optCard.transform, "显示左上角实时性能角标 HUD（零射线开销）", () => PerfOptimizerModule.ShowPerfHudOverlay, v => PerfOptimizerModule.ShowPerfHudOverlay = v);
        AddPerfToggle(optCard.transform, "[A-1] 修复原版 PerformanceController -1 帧率恒负降频 Bug", () => PerfOptimizerModule.OptFixNegativeFpsBug, v => PerfOptimizerModule.OptFixNegativeFpsBug = v);
        AddPerfToggle(optCard.transform, "[A-2] 跳过高开销全屏弹窗高斯模糊（大幅降核显 GPU 占用）", () => PerfOptimizerModule.OptBlurDownsampleOrSkip, v => PerfOptimizerModule.OptBlurDownsampleOrSkip = v);
        AddPerfToggle(optCard.transform, "[A-3] 修复 CameraQualityController 切场景事件双倍订阅泄漏", () => PerfOptimizerModule.OptFixCameraLeak, v => PerfOptimizerModule.OptFixCameraLeak = v);
        AddPerfToggle(optCard.transform, "[A-4] 战斗高倍速读条 60/120+ FPS 丝滑线性插值优化", () => PerfOptimizerModule.OptCombatHighSpeedSmooth, v => PerfOptimizerModule.OptCombatHighSpeedSmooth = v);
        AddPerfToggle(optCard.transform, "[A-5] 战斗高并发 VFX / 飘字安全限流（防止后期连发流掉帧）", () => PerfOptimizerModule.OptVfxThrottle, v => PerfOptimizerModule.OptVfxThrottle = v);
        AddPerfToggle(optCard.transform, "[A-6] SoundManager 同音轨重复加载句柄防泄漏保护", () => PerfOptimizerModule.OptAudioHandleGuard, v => PerfOptimizerModule.OptAudioHandleGuard = v);
        AddPerfToggle(optCard.transform, "[A-7] AssetLoader.DoesAddressExist O(1) 缓存与切场景冷资产清理", () => PerfOptimizerModule.OptAssetLoaderCacheAndLru, v => PerfOptimizerModule.OptAssetLoaderCacheAndLru = v);
        AddPerfToggle(optCard.transform, "[A-8] MainMenuBaseView 主大厅导航节流（消除每帧 LINQ GC）", () => PerfOptimizerModule.OptMainMenuNavThrottle, v => PerfOptimizerModule.OptMainMenuNavThrottle = v);
        AddPerfToggle(optCard.transform, "[A-9] CardTooltipController 同帧布局脏重建合并节流", () => PerfOptimizerModule.OptTooltipLayoutThrottle, v => PerfOptimizerModule.OptTooltipLayoutThrottle = v);
        AddPerfToggle(optCard.transform, "[A-10] 战斗 1.5ms 增量 GC 护航 + 切大厅节流卸载 + 静音冗余日志", () => PerfOptimizerModule.OptSceneIncrementalGc, v =>
        {
            PerfOptimizerModule.OptSceneIncrementalGc = v;
            PerfOptimizerModule.OptMuteVerboseAppLogger = v;
        });
        AddPerfToggle(optCard.transform, "[A-11] 拖拽悬停去重与 DOTween 补间状态守卫（降低棋盘拖拽掉帧）", () => PerfOptimizerModule.OptDragHoverGuard, v => PerfOptimizerModule.OptDragHoverGuard = v);

        // 3. 模块运行状态与 Harmony 降级/熔断诊断列表
        var healthCard = _ui.CreateCard("PerfHealthCard", _performanceTabRoot.transform, NativeUguiFactory.ColCardBg, NativeUguiFactory.ColGoldDim, padH: 14, padV: 10, spacing: 5f);
        _ui.CreateText("Title", healthCard.transform, "BazaarModKit 全部模块健康与熔断诊断表", 15f, NativeUguiFactory.ColGold, FontStyles.Bold);
        _perfModulesHealthContainer = _ui.CreateVerticalGroup("PerfHealthList", healthCard.transform, spacing: 4f).transform;

        _performanceTabRoot.SetActive(false);
    }

    private void CreatePerfProfileBtn(Transform parent, string label, string profileId)
    {
        var btn = _ui.CreateButton("Prof_" + profileId, parent, label, 12.5f,
            NativeUguiFactory.ColBtnNormal, NativeUguiFactory.ColBtnHover, NativeUguiFactory.ColGold, () =>
            {
                PerfOptimizerModule.Instance?.ApplyProfile(profileId, persist: true);
                SetStatus($"已切换性能预设：{label}", false);
                RefreshPerformanceTabFull();
            }, NativeUguiFactory.ColGoldDim);
        NativeUguiFactory.SetLayout(btn.Root, minW: 116f, prefW: 126f, minH: 30f);
    }

    private void AddPerfToggle(Transform parent, string label, Func<bool> getter, Action<bool> setter)
    {
        var th = _ui.CreateToggle("PerfOpt_" + label.GetHashCode(), parent, label, getter(), val =>
        {
            setter(val);
            PerfOptimizerModule.Instance?.ApplyQualityTweaksNow();
            PerfOptimizerModule.Instance?.SaveConfigToStore();
        });
        _perfToggleRefreshers.Add(() => th.Toggle.SetIsOnWithoutNotify(getter()));
    }

    private void RefreshPerformanceTabFull()
    {
        for (int i = 0; i < _perfToggleRefreshers.Count; i++)
        {
            try { _perfToggleRefreshers[i](); } catch { }
        }
        RefreshPerformanceTabLiveMetrics();

        if (_perfModulesHealthContainer == null) return;
        ClearChildren(_perfModulesHealthContainer);
        var snaps = BazaarLauncherPlugin.Host?.GetHealthSnapshots() ?? Array.Empty<ModHealthSnapshot>();
        foreach (var s in snaps)
        {
            var captured = s;
            var row = _ui.CreateHorizontalRow("Health_" + captured.ModId, _perfModulesHealthContainer, spacing: 6f);
            string stateZh = FormatLifecycleStateZh(captured.State);
            string errPart = string.IsNullOrEmpty(captured.LastError) ? string.Empty : $" | 最近错误: {Truncate(captured.LastError, 48)}";
            Color col = captured.State == ModLifecycleState.Quarantined ? NativeUguiFactory.ColErrorText
                : captured.State == ModLifecycleState.Degraded ? NativeUguiFactory.ColGold
                : NativeUguiFactory.ColCream;

            var txt = _ui.CreateText("Txt", row.transform,
                $"• {captured.Name} [{stateZh}] · 补丁 {captured.PatchSucceeded}/{captured.PatchAttempted} (降级 {captured.PatchDegraded}) · 故障 {captured.FaultCount}{errPart}",
                12.5f, col);
            NativeUguiFactory.SetLayout(txt.gameObject, flexW: 1f, minH: 24f);

            if (captured.State == ModLifecycleState.Quarantined)
            {
                var resetBtn = _ui.CreateButton("Reset", row.transform, "解除熔断", 12f,
                    NativeUguiFactory.ColGoldBtn, NativeUguiFactory.ColGoldBtnHover, Color.white, () =>
                    {
                        BazaarLauncherPlugin.Host?.TryResetQuarantinedModule(captured.ModId);
                        RefreshPerformanceTabFull();
                    }, padH: 6, padV: 2);
                NativeUguiFactory.SetLayout(resetBtn.Root, minW: 78f, prefW: 82f, minH: 24f);
            }
        }
    }

    private void RefreshPerformanceTabLiveMetrics()
    {
        var perf = PerfOptimizerModule.Instance;
        if (perf == null) return;
        var snap = perf.GetLatestSnapshot();

        if (_perfLiveHeaderText != null)
        {
            string pingStr = snap.PingMs >= 0 ? $"{snap.PingMs} ms (±{snap.PingJitterMs} ms)" : "离线/未探测";
            _perfLiveHeaderText.text =
                $"FPS {snap.Fps:0.0}  |  1% Low {snap.OnePercentLowFps:0.0}  |  帧时间 {snap.FrameTimeMs:0.00} ms  |  托管堆 {snap.ManagedHeapMb:0.0} MB  |  Ping {pingStr}";
        }

        if (_perfLiveDetailText != null)
        {
            _perfLiveDetailText.text =
                $"当前档位: {snap.ActiveProfile} | 场景: {snap.SceneName} | 活跃 VFX: {snap.ActiveVfxCount} (累计限流 {snap.ThrottledVfxCount}) | 自定义卡图缓存: {snap.CachedCustomTextures} 张 ({snap.CachedCustomTextureVramMb:0.00} MB) | 云端拦截外联: {CloudGuard.BlockedExternalCount} 次";
        }
    }

    private void ExportDiagnosticReportToDisk()
    {
        try
        {
            string cfgDir = BepInEx.Paths.ConfigPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string outPath = Path.Combine(cfgDir, "bazaar_diag_report.txt");
            var sb = new StringBuilder(2048);
            sb.AppendLine("=== BazaarModKit 客户端诊断报告 ===");
            sb.AppendLine($"生成时间 (UTC): {DateTime.UtcNow:O}");
            sb.AppendLine($"服务器地址: {NameAuth.GetServerBaseUrl()}");
            sb.AppendLine($"玩家身份: {NameAuth.CurrentUsername} ({NameAuth.CurrentAccountId})");

            var perf = PerfOptimizerModule.Instance?.GetLatestSnapshot();
            if (perf != null)
            {
                sb.AppendLine($"性能摘要: FPS={perf.Fps}, 1%Low={perf.OnePercentLowFps}, FrameMs={perf.FrameTimeMs}, HeapMB={perf.ManagedHeapMb}, PingMs={perf.PingMs}, Profile={perf.ActiveProfile}");
                sb.AppendLine($"自定义贴图缓存: Count={perf.CachedCustomTextures}, VRAM={perf.CachedCustomTextureVramMb}MB");
            }
            sb.AppendLine($"云端防火墙统计: Allowed={CloudGuard.AllowedCount}, Blocked={CloudGuard.BlockedExternalCount}");
            sb.AppendLine($"四层汉化词典: Revision={LocalizationOverlayModule.Instance?.OverlayRevision}, Entries={LocalizationOverlayModule.Instance?.OverlayEntryCount}");
            sb.AppendLine();
            sb.AppendLine("--- 模块健康与 Harmony 补丁明细 ---");
            foreach (var m in BazaarLauncherPlugin.Host?.GetHealthSnapshots() ?? Array.Empty<ModHealthSnapshot>())
            {
                sb.AppendLine($"[{m.Category}] {m.ModId} ({m.Name}) | State={m.State} | Enabled={m.Enabled} | LoadMs={m.LoadDurationMs} | Patch={m.PatchSucceeded}/{m.PatchAttempted} (Degraded={m.PatchDegraded}) | Faults={m.FaultCount} | Err={m.LastError}");
                foreach (var note in m.DiagnosticNotes)
                {
                    sb.AppendLine($"    {note}");
                }
            }

            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
            SetStatus($"诊断报告已导出至：{outPath}", false);
            ShowToastBanner("诊断报告导出成功：BepInEx/config/bazaar_diag_report.txt");
        }
        catch (Exception ex)
        {
            SetStatus("导出诊断报告失败：" + ex.Message, true);
        }
    }

    private static string FormatLifecycleStateZh(ModLifecycleState state) => state switch
    {
        ModLifecycleState.Enabled => "运行正常",
        ModLifecycleState.Disabled => "已停用",
        ModLifecycleState.Degraded => "安全降级",
        ModLifecycleState.Quarantined => "已熔断隔离",
        ModLifecycleState.Loaded => "已加载",
        _ => "未加载",
    };
}

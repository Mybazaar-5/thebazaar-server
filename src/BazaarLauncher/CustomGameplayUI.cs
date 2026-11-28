using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace BazaarLauncher;

/// <summary>
/// 私服自定义玩法配置与锦标赛规则台（纯原生 Unity uGUI 事件驱动实现，彻底淘汰 IMGUI OnGUI）：
///
/// 功能概览：
///   1. 弹窗隐藏时 SetActive(false)，严格做到 0 每帧 GC 与 0 OnGUI CPU 占用；
///   2. 主菜单 / 英雄选择页与锦标赛大厅顶部嵌入双态规则状态胶囊按钮（官方默认 vs 自定义激活 + 开启项摘要）；
///   3. 面板顶部配备【总览状态灯】与【一键恢复官方默认（全部关闭）】按钮；局内右上角安全区域保留极简只读小徽章；
///   4. 战斗界面右上角提供轻量 1x/2x/3x 原生倍速切换芯片，配合 CombatPatch 确保不被每帧重置；
///   5. 锦标赛房主规则自动记忆至 BepInEx/config/bazaar_tournament_presets.json（含 3 个命名预设槽位），
///      房主开房/进大厅时自动加载 lastTournamentRules 并提交到 /lobbies/{id}/rules。
/// </summary>
internal sealed partial class CustomGameplayUI : MonoBehaviour
{
    internal static CustomGameplayUI Instance { get; private set; }

    private readonly ConcurrentQueue<Action> _pendingUpdates = new();
    private CustomRulesCatalog _catalog = new();
    private CustomRunRuleSet _normalRules = new();
    private CustomRunRuleSet _lobbyRules = new();
    private LobbyDetailSnapshot _currentLobby;

    private readonly RuleEditorBuffer _normalBuf = new();
    private readonly RuleEditorBuffer _lobbyBuf = new();
    private readonly Dictionary<string, string> _itemNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _skillNameMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoAppliedLobbyIds = new(StringComparer.OrdinalIgnoreCase);

    private bool _isOpen;
    private UiTab _activeTab = UiTab.Normal;

    private bool _catalogLoaded;
    private bool _initialFetchStarted;
    private bool _isBusy;
    private bool _lobbyPollInFlight;
    private bool _lobbyDirty;
    private float _nextLobbyPollTime;
    private float _nextHudSyncTime;
    private float _lastAutoDetectTry;
    private string _statusMessage = "就绪（点击顶部规则胶囊或按 F8 打开配置）";
    private bool _statusIsError;

    // 全模式（普通 / 排位 / 锦标赛）统一进局开场流派倾向一次性确认与严禁途中改倾向状态
    private bool _showPreRunPrompt;
    private readonly CustomRunRuleSet _openingPromptDraftRules = new();
    private string _activeRunId = "";
    private bool _activeRunAllowTendency = true;
    private bool _activeRunTendencyLocked;
    private ulong _activeRunLockedMask;
    private string _activeRunFetchedId = "";
    private bool _activeRunFetchInFlight;
    // P1-15：当前活跃对局的服务端权威规则快照（由 GET /custom-rules/me/active-run-tendency 下发），
    // 作为局内右上角规则 HUD 的唯一权威事实源，彻底隔离大厅可变 _normalRules
    private CustomRunRuleSet _activeRunRules;
    private bool _activeRunIsCustomRuleRun;
    private int _rulesFetchSeq;
    private int _activeRunFetchSeq;
    private int _lastSyncedAuthGeneration;
    private float _nextActiveRunRetryTime;
    private string _lastOpeningPromptedRunId = "";
    private float _nextViewportCheckTime;
    private bool _tendencySyncPending;
    private float _tendencySyncDueTime;
    private static bool _hooksApplied;
    private static BazaarLauncher.Core.SafeHarmonyPatcher _hostPatcher;
    private static BazaarLauncher.Core.SafeHarmonyPatcher _fallbackPatcher;
    private static int _lastEscConsumedFrame = -1;
    internal static int LastEscConsumedFrame => _lastEscConsumedFrame;
    private readonly List<IDisposable> _eventSubscriptions = new();
    private int _rulesVersion;
    private bool _hudDirty = true;
    private string _lastObservedAppState = "";
    private float _nextAppStatePollTime;
    private float _nextPerfTabRefreshTime;

    internal static bool IsPerformanceTabVisible
        => Instance != null && Instance._isOpen && Instance._activeTab == UiTab.Performance;

    // ── #16 锦标赛 PvP 等待状态与中文倒计时横幅缓存 ───────────────────────────
    private sealed class TourneyStatusSnapshot
    {
        public string Phase = "";
        public uint CurrentDay;
        public int TotalPlayers;
        public int ActivePlayers;
        public int ReadyPlayers;
        public DateTime? DeadlineAt;
        public bool PlayerReadyForPvp;
        public bool CombatSubmitted;
        public bool CanStartPvp = true;
        public bool WaitingForOtherPlayers;
        public bool WaitingForHumanDayStart;
        public string AssignedOpponentUsername = "";
    }

    private static volatile TourneyStatusSnapshot _latestTourneyStatus;
    private static volatile string _tourneyWaitErrorNotice = "";
    private static float _tourneyWaitErrorUntil;
    private static Type _cachedTssType;
    private static PropertyInfo _tssCurrentStatusProp;
    private static PropertyInfo _serverTimeProp;

    // ── 零 GC 热键检测（优先新 InputSystem，保留 Legacy Input 兜底）───────────
    private bool _inputChecked;
    private Func<object> _getKeyboardCurrent;
    private Func<object, object> _getF8Key;
    private Func<object, object> _getEscKey;
    private Func<object, bool> _getKeyWasPressed;
    private bool _legacyInputDisabled;

    internal static void EnsureInstalled(BazaarLauncher.Core.SafeHarmonyPatcher patcher = null)
    {
        _hostPatcher = patcher;
        if (Instance != null)
        {
            InstallHarmonyHooksOnce();
            return;
        }
        var go = new GameObject("BazaarCustomGameplayUI");
        DontDestroyOnLoad(go);
        Instance = go.AddComponent<CustomGameplayUI>();
        BazaarLauncherPlugin.Log?.LogInfo("CustomGameplayUI（事件驱动原生级界面，F8 兼容兜底）已加载");
    }

    internal static void Shutdown()
    {
        _hooksApplied = false;
        _hostPatcher = null;
        _lastEscConsumedFrame = -1;
        if (_fallbackPatcher != null)
        {
            try { _fallbackPatcher.UnpatchAll(); } catch { }
            _fallbackPatcher = null;
        }
        if (Instance != null)
        {
            var go = Instance.gameObject;
            Instance = null;
            if (go != null) Destroy(go);
        }
    }

    internal void MarkHudDirty() => _hudDirty = true;

    internal TMPro.TMP_FontAsset GetActiveUiFont() => _ui?.Font;

    private void Awake()
    {
        Instance = this;
        _normalRules.EnsureCollections();
        _lobbyRules.EnsureCollections();

        var lastTourney = TournamentPresetStore.GetLastTournamentRules();
        if (lastTourney != null)
        {
            _lobbyRules = lastTourney;
        }
        _normalBuf.SyncFromRules(_normalRules);
        _lobbyBuf.SyncFromRules(_lobbyRules);

        InstallHarmonyHooksOnce();
        SceneManager.activeSceneChanged += OnActiveSceneChanged;

        // P1-49：保存所有 EventBus 订阅句柄并在 OnDestroy() 中全量释放，彻底杜绝热重载销毁实例泄漏
        var bus = BazaarLauncherPlugin.Host?.EventBus;
        if (bus != null)
        {
            _eventSubscriptions.Add(bus.Subscribe<BazaarModSdk.RunStateChangedModEvent>(_ => MarkHudDirty()));
            _eventSubscriptions.Add(bus.Subscribe<BazaarModSdk.CombatLifecycleModEvent>(_ => MarkHudDirty()));
            _eventSubscriptions.Add(bus.Subscribe<BazaarModSdk.ActiveRulesUpdatedModEvent>(_ =>
            {
                MarkHudDirty();
                if (_isOpen) RefreshModalContentsIfOpen();
            }));
            _eventSubscriptions.Add(bus.Subscribe<BazaarModSdk.ModAssetsSyncedModEvent>(_ =>
            {
                _pendingUpdates.Enqueue(() =>
                {
                    if (_isOpen && _activeTab == UiTab.Workshop) RefreshWorkshopTabUi();
                });
            }));
            // P1-15：监听 NameAuth 登录/鉴权成功事件，立即重拉已认证账号的个人规则与活跃局权威规则，根治冷启动抢跑拿 guest 默认规则
            _eventSubscriptions.Add(bus.Subscribe<NameAuth.AuthSucceededEvent>(OnNameAuthSucceeded));
        }
    }

    private void Start()
    {
        EnsureHudBuilt();
        RefreshHudOverlays(force: true);
        StartInitialBackgroundFetch();
    }

    private void OnDestroy()
    {
        for (int i = 0; i < _eventSubscriptions.Count; i++)
        {
            try { _eventSubscriptions[i]?.Dispose(); } catch { }
        }
        _eventSubscriptions.Clear();

        StopSocialSse();
        if (Instance == this) Instance = null;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        if (_uiRootGo != null) Destroy(_uiRootGo);
        _ui.Dispose();
    }

    private void OnActiveSceneChanged(Scene prev, Scene next)
    {
        // P2-41：切场景时自动收起弹窗/开场提示防止遮挡场景过渡
        if (_isOpen)
        {
            ClosePanel();
        }
        if (_showPreRunPrompt)
        {
            DismissOpeningPromptOnly();
        }

        _lastPillKey = "";
        _lastInRunBadgeKey = "";
        _lastSpeedTenths = -1;
        _lastTourneyWaitKey = "";
        _lastToolboxKey = "";
        if (GameInspector.IsMainMenuSceneName(next.name))
        {
            _latestTourneyStatus = null;
            _tourneyWaitErrorNotice = "";
            _tourneyWaitErrorUntil = 0f;
            // P2-41 & D6-P1-08：返回主菜单清空跨局/跨房残留缓存、释放房间 Mod 与回放视觉快照并停用闲置 SSE 长连接
            _currentLobby = null;
            _lobbyDirty = false;
            _lastOpeningPromptedRunId = "";
            _autoAppliedLobbyIds.Clear();
            _activeRunLockedMask = 0UL;
            _activeRunFetchInFlight = false;
            BazaarLauncher.Core.ModContentSyncClient.Instance?.ClearSyncedRoomBundleMods();
            BazaarLauncher.Core.ModContentSyncClient.Instance?.ClearReplayVisualBindings();
            StopSocialSse();
        }
        _lastViewportScreenW = -1;
        _lastViewportScreenH = -1;
        _lastLetterBoxRect = new(-1f, -1f, -1f, -1f);
        _nextViewportCheckTime = 0f;
        _activeRunId = "";
        _activeRunFetchedId = "";
        _activeRunRules = null;
        _activeRunIsCustomRuleRun = false;
        _activeRunFetchSeq++;
        _activeRunFetchInFlight = false;
        _nextActiveRunRetryTime = 0f;
        _activeRunTendencyLocked = false;
        _activeRunAllowTendency = true;
        _ui.RefreshNativeAssetsIfNeeded(forceRescan: false);
        UpdateSafeViewportRootIfNeeded(force: true);
        RefreshHudOverlays(force: true);
        if (_isOpen)
        {
            RefreshModalContentsIfOpen();
        }
    }

    private void Update()
    {
        if (!_pendingUpdates.IsEmpty)
        {
            DrainPendingUpdates();
        }

        // P1-15：兜底感知在 Awake 订阅前或无 EventBus 环境下完成的鉴权升级，确保必重拉真实账号规则
        int curAuthGen = NameAuth.AuthGeneration;
        if (curAuthGen > _lastSyncedAuthGeneration)
        {
            HandleAuthReadySync(curAuthGen, NameAuth.CurrentUsername);
        }

        float now = Time.unscaledTime;
        if (Screen.width != _lastViewportScreenW || Screen.height != _lastViewportScreenH || now >= _nextViewportCheckTime)
        {
            _nextViewportCheckTime = now + 0.5f;
            UpdateSafeViewportRootIfNeeded(force: false);
        }

        if (_tendencySyncPending && now >= _tendencySyncDueTime && !_isBusy)
        {
            _tendencySyncPending = false;
            SavePersonalTendencyDraftAsync(_normalRules.SelectedArchetypeMask);
        }

        // 热键 F8 打开/收起，Esc 关闭弹窗（D4-P1-10：输入框聚焦时屏蔽 F8/热键，同帧 Esc 与 PopupManager.HandleEscape 互斥防穿透）
        bool inputFocused = IsAnyTmpInputFieldFocused();
        if (!inputFocused && WasKeyPressedThisFrame(isEscape: false) && !IsInActiveCombatPlayback())
        {
            ToggleOpen();
        }
        else if ((_isOpen || _showPreRunPrompt || SpIsPopupOpen()) && _lastEscConsumedFrame != Time.frameCount && WasKeyPressedThisFrame(isEscape: true))
        {
            if (inputFocused)
            {
                _lastEscConsumedFrame = Time.frameCount;
                try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
            }
            else
            {
                TryConsumeEscapeForCustomModal();
            }
        }

        // 事件驱动按需刷新（修复 P1-1 & 拖拽 Top 10）：将 AppState 反射轮询降频至 0.25s，避免每帧反射开销
        if (_hudDirty || now >= _nextAppStatePollTime)
        {
            _nextAppStatePollTime = now + 0.25f;
            string curAppState = GameInspector.GetAppStateName();
            if (!string.Equals(curAppState, _lastObservedAppState, StringComparison.Ordinal))
            {
                _lastObservedAppState = curAppState;
                _hudDirty = true;
            }
        }

        if (_hudDirty || now >= _nextHudSyncTime)
        {
            _hudDirty = false;
            _nextHudSyncTime = now + (IsInActiveCombatPlayback() ? 1.0f : 1.25f);
            RefreshHudOverlays(force: false);
            if (_isOpen && GameInspector.IsMainMenuSceneName(GameInspector.GetActiveSceneName()))
            {
                RefreshMainMenuToolboxUi();
            }
        }

        // 仅当玩家打开「性能与诊断」页签时每 0.5s 增量刷新性能仪表读数
        if (_isOpen && _activeTab == UiTab.Performance && now >= _nextPerfTabRefreshTime)
        {
            _nextPerfTabRefreshTime = now + 0.5f;
            RefreshPerformanceTabLiveMetrics();
        }

        TickToastBanner(now);

        if (_isOpen && !_catalogLoaded && !_isBusy && now - _lastAutoDetectTry > 4f)
        {
            _lastAutoDetectTry = now;
            StartInitialBackgroundFetch();
        }

        if (_isOpen && _activeTab == UiTab.Tournament && !_lobbyPollInFlight && now >= _nextLobbyPollTime)
        {
            _nextLobbyPollTime = now + 2.5f;
            RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false);
        }
    }

    internal void NotifyCombatSpeedChangedOnMain()
    {
        _lastSpeedTenths = -1;
        if (_hudBuilt)
        {
            RefreshHudOverlays(force: true);
        }
    }

    internal int GetActiveRuleCountForDriver()
    {
        try
        {
            bool inRun = GameInspector.IsActiveInRunState(
                GameInspector.GetActiveSceneName(),
                GameInspector.GetAppStateName());
            if (inRun)
            {
                bool isTourneyRun = IsActiveRunTournamentMode();
                return SummarizeInRunDisplayRules(isTourneyRun).ActiveCount;
            }

            bool isTourneyContext = _isOpen ? (_activeTab == UiTab.Tournament) : IsTournamentUiContext();
            var activeRules = isTourneyContext ? ResolveHudDisplayRules(true) : _normalRules;
            return SummarizeRules(activeRules, isTourneyContext).ActiveCount;
        }
        catch
        {
            return 0;
        }
    }

    // ── 对外与 GameDriver 控制接口（严格保持兼容） ─────────────────────────

    internal void DrainPendingUpdates()
    {
        int guard = 0;
        while (_pendingUpdates.TryDequeue(out var act) && guard++ < 64)
        {
            try { act(); }
            catch (Exception ex) { BazaarLauncherPlugin.Log?.LogWarning("CustomGameplayUI pending update: " + ex.Message); }
        }
    }

    internal void ToggleOpen()
    {
        if (_isOpen) ClosePanel();
        else OpenPanel();
    }

    internal void OpenPanel()
    {
        EnsureModalBuilt();
        _isOpen = true;
        _modalPanelRoot.SetActive(true);
        _lastToolboxKey = "";

        if (!_catalogLoaded) StartInitialBackgroundFetch();
        SpOnPanelOpened();
        if (_activeTab == UiTab.Tournament)
        {
            _nextLobbyPollTime = 0f;
            RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false);
        }

        RefreshModalContentsIfOpen();
        RefreshHudOverlays(force: true);
    }

    internal void ClosePanel()
    {
        SpClosePopup();
        _isOpen = false;
        if (_modalPanelRoot != null)
        {
            _modalPanelRoot.SetActive(false);
        }
        try
        {
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null
                && _modalCanvas != null && EventSystem.current.currentSelectedGameObject.transform.IsChildOf(_modalCanvas.transform))
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
        }
        catch { }
        RefreshHudOverlays(force: true);
        // P2-41：弹窗关闭且非活跃锦标赛局/大厅上下文时停止闲置 SSE 长连接
        SyncSocialSseForLobbyOrPanelState();
    }

    internal void SetOpenFromDriver(bool open, string tab)
    {
        DrainPendingUpdates();
        if (!string.IsNullOrWhiteSpace(tab))
        {
            _activeTab = tab.Trim().ToLowerInvariant() switch
            {
                "tournament" => UiTab.Tournament,
                "workshop" or "mods" or "mod" => UiTab.Workshop,
                "social" or "multiplayer" => UiTab.Social,
                "performance" or "perf" => UiTab.Performance,
                _ => UiTab.Normal,
            };
        }
        if (open) OpenPanelOnTab(_activeTab);
        else ClosePanel();
    }

    internal (bool menuRulePillVisible, bool inRunBadgeVisible, bool combatSpeedChipVisible) GetHudVisibilityForDriver()
    {
        bool pill = _menuRulePillBtn != null && _menuRulePillBtn.Root != null && _menuRulePillBtn.Root.activeInHierarchy;
        bool badge = _inRunBadgeGo != null && _inRunBadgeGo.activeInHierarchy;
        bool chip = _speedChipGo != null && _speedChipGo.activeInHierarchy;
        return (pill, badge, chip);
    }

    internal object GetDriverStateSnapshot()
    {
        DrainPendingUpdates();
        bool inCombat = IsInActiveCombatPlayback();
        string activeScene = GameInspector.GetActiveSceneName();
        bool inMainMenu = GameInspector.IsMainMenuSceneName(activeScene);
        bool inRun = !inMainMenu && GameInspector.IsActiveInRunState(activeScene, GameInspector.GetAppStateName());
        bool isTourneyContext = _isOpen ? (_activeTab == UiTab.Tournament) : IsTournamentUiContext();
        RuleActivationSummary sum;
        if (inRun && !_isOpen)
        {
            sum = SummarizeInRunDisplayRules(IsActiveRunTournamentMode());
        }
        else
        {
            var activeRules = isTourneyContext ? ResolveHudDisplayRules(true) : _normalRules;
            sum = SummarizeRules(activeRules, isTourneyContext);
        }
        var presetStore = TournamentPresetStore.LoadSnapshot();
        var hudVis = GetHudVisibilityForDriver();

        return new
        {
            ok = true,
            uiBackend = "ugui",
            open = _isOpen,
            tab = _activeTab switch
            {
                UiTab.Tournament => "tournament",
                UiTab.Workshop => "workshop",
                UiTab.Social => "social",
                UiTab.Performance => "performance",
                _ => "normal",
            },
            pillVisible = hudVis.menuRulePillVisible || (inMainMenu && !inCombat && !_isOpen && !_showPreRunPrompt),
            menuRulePillVisible = hudVis.menuRulePillVisible,
            inRunBadgeVisible = hudVis.inRunBadgeVisible,
            combatSpeedChipVisible = hudVis.combatSpeedChipVisible,
            effectiveCombatSpeed = CombatPatch.GetDisplayCombatSpeed(),
            manualCombatSpeed = CombatPatch.ManualUiSpeed,
            speedOverride = CombatPatch.SpeedOverride,
            inCombatPlayback = inCombat,
            preRunPromptVisible = _showPreRunPrompt,
            isCustomActive = sum.IsCustomActive,
            activeRuleCount = sum.ActiveCount,
            activeRuleSummary = sum.SummaryText,
            hasActiveRunRules = _activeRunRules != null,
            isCustomRuleRun = _activeRunIsCustomRuleRun,
            tournamentPresetsPath = TournamentPresetStore.FilePath,
            hasLastTournamentRules = presetStore.LastTournamentRules != null,
            presetNames = presetStore.Presets.Select(p => p.Name).ToList(),
            hero = DetectCurrentHero(),
            username = NameAuth.CurrentUsername,
            accountId = NameAuth.CurrentAccountId,
            hasToken = !string.IsNullOrEmpty(NameAuth.CurrentAccessToken),
            catalogLoaded = _catalogLoaded,
            archetypeCount = _catalog.Archetypes?.Count ?? 0,
            encounterCategoryCount = _catalog.EncounterCategories?.Count ?? 0,
            itemCount = _catalog.Items?.Count ?? 0,
            skillCount = _catalog.Skills?.Count ?? 0,
            isBusy = _isBusy,
            statusMessage = _statusMessage,
            statusIsError = _statusIsError,
            normalRules = _normalRules.Clone(),
            lobby = _currentLobby == null
                ? (object)new { inLobby = false }
                : new
                {
                    inLobby = true,
                    lobbyId = _currentLobby.LobbyId,
                    code = _currentLobby.Code,
                    name = _currentLobby.Name,
                    hostAccountId = _currentLobby.HostAccountId,
                    isHost = IsLocalPlayerLobbyHost(_currentLobby),
                    playerCap = _currentLobby.PlayerCap,
                    memberCount = _currentLobby.Members?.Count ?? 0,
                    botCount = _currentLobby.Members?.Count(m => m.IsAi) ?? 0,
                    rules = (_lobbyRules ?? _currentLobby.Rules)?.Clone(),
                },
        };
    }

    internal async Task<(bool ok, int statusCode, string error, CustomRunRuleSet appliedRules)> ApplyRulesFromDriverAsync(
        CustomRunRuleSet rules, string target)
    {
        if (rules == null)
        {
            return (false, 400, "规则配置数据不能为空", null);
        }
        var snapshot = rules.Clone();
        snapshot.NormalizeBounds();

        bool isTournament = string.Equals(target, "tournament", StringComparison.OrdinalIgnoreCase);
        if (isTournament)
        {
            string lobbyId = _currentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                return (false, 404, "当前未处于任何锦标赛房间中", null);
            }
            var resp = await NameAuth.PutJsonAsync($"/lobbies/{Uri.EscapeDataString(lobbyId)}/rules", snapshot).ConfigureAwait(false);
            if (resp.Ok)
            {
                var saved = TryDeserializeRules(resp.Body) ?? snapshot.Clone();
                TournamentPresetStore.SaveLastTournamentRules(saved);
                GameDriver.RunOnMain(() =>
                {
                    DrainPendingUpdates();
                    _autoAppliedLobbyIds.Add(lobbyId);
                    _lobbyRules = saved.Clone();
                    _lobbyBuf.SyncFromRules(_lobbyRules);
                    _lobbyDirty = false;
                    SetStatus("已通过调试接口广播并记忆锦标赛规则", isError: false);
                    RefreshModalContentsIfOpen();
                    RefreshHudOverlays(force: true);
                    return true;
                });
                return (true, resp.StatusCode, "", saved);
            }
            return (false, resp.StatusCode, resp.Error, null);
        }
        else
        {
            var resp = await NameAuth.PutJsonAsync("/custom-rules/me", snapshot).ConfigureAwait(false);
            if (resp.Ok)
            {
                var saved = TryDeserializeRules(resp.Body) ?? snapshot.Clone();
                GameDriver.RunOnMain(() =>
                {
                    DrainPendingUpdates();
                    _normalRules = saved.Clone();
                    _normalBuf.SyncFromRules(_normalRules);
                    Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                    SetStatus("已通过调试接口应用个人玩法规则", isError: false);
                    RefreshModalContentsIfOpen();
                    RefreshHudOverlays(force: true);
                    return true;
                });
                return (true, resp.StatusCode, "", saved);
            }
            return (false, resp.StatusCode, resp.Error, null);
        }
    }

    // ── 后台网络读写（严禁阻塞主线程） ──────────────────────────────────────

    /// <summary>P1-15：收到 NameAuth 登录/鉴权成功事件后，强制以真实账号身份重拉个人规则与活跃局权威规则。</summary>
    private void OnNameAuthSucceeded(NameAuth.AuthSucceededEvent evt)
    {
        HandleAuthReadySync(evt.Generation, evt.Username);
    }

    private void HandleAuthReadySync(int authGeneration, string username)
    {
        if (authGeneration <= _lastSyncedAuthGeneration) return;
        _lastSyncedAuthGeneration = authGeneration;
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[CustomGameplayUI] 鉴权就绪（user={username ?? string.Empty}, gen={authGeneration}），立即重同步个人规则与活跃局规则");

        // 1. 强制重拉 /custom-rules/me（覆盖登录前 Start() 抢跑可能拿到的 guest 默认规则，且不被旧 _isBusy 阻塞）
        StartInitialBackgroundFetch(forceRefreshAfterAuth: true);
        // 2. 立即同步拉取 /custom-rules/me/active-run-tendency（获取当前活跃局 SQLite 权威规则 _activeRunRules）
        ForceRefreshActiveRunRulesAfterAuth();
    }

    private void ForceRefreshActiveRunRulesAfterAuth()
    {
        string activeScene = GameInspector.GetActiveSceneName();
        bool inMainMenu = GameInspector.IsMainMenuSceneName(activeScene);
        bool inRun = !inMainMenu && GameInspector.IsActiveInRunState(activeScene, GameInspector.GetAppStateName());
        string runKey = inRun ? ReadCurrentRunWindowInfo(out _, out _, out _) : string.Empty;

        _activeRunFetchedId = inRun ? runKey : string.Empty;
        _nextActiveRunRetryTime = 0f;
        FetchActiveRunTendencyStateAsync(runKey, force: true);
    }

    private void StartInitialBackgroundFetch(bool forceRefreshAfterAuth = false)
    {
        if (!forceRefreshAfterAuth && _initialFetchStarted && _isBusy) return;
        _initialFetchStarted = true;
        _isBusy = true;
        int mySeq = ++_rulesFetchSeq;
        int capturedAuthGen = NameAuth.AuthGeneration;
        SetStatus("正在同步私服规则目录与当前配置...", isError: false);

        Task.Run(async () =>
        {
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var catalogTask = NameAuth.GetJsonAsync("/custom-rules/catalog");
                var rulesTask = NameAuth.GetJsonAsync("/custom-rules/me");
                await Task.WhenAll(catalogTask, rulesTask).ConfigureAwait(false);

                var catResp = catalogTask.Result;
                var meResp = rulesTask.Result;

                CustomRulesCatalog parsedCatalog = null;
                if (catResp.Ok && !string.IsNullOrWhiteSpace(catResp.Body))
                {
                    parsedCatalog = JsonConvert.DeserializeObject<CustomRulesCatalog>(catResp.Body);
                }

                CustomRunRuleSet parsedRules = null;
                if (meResp.Ok && !string.IsNullOrWhiteSpace(meResp.Body))
                {
                    parsedRules = TryDeserializeRules(meResp.Body);
                }

                _pendingUpdates.Enqueue(() =>
                {
                    // P1-15：丢弃已被更新一次（如鉴权成功后触发）的过期规则响应，防止登录前 guest 响应后到反向覆写
                    if (mySeq != _rulesFetchSeq) return;
                    _isBusy = false;
                    if (capturedAuthGen > _lastSyncedAuthGeneration)
                    {
                        _lastSyncedAuthGeneration = capturedAuthGen;
                    }
                    if (parsedCatalog != null)
                    {
                        ApplyCatalog(parsedCatalog);
                    }
                    if (parsedRules != null)
                    {
                        bool needMigrateV12_1 = BazaarLauncherPlugin.MigratedV12_1Defaults?.Value == false;
                        if (needMigrateV12_1)
                        {
                            bool changedDefaults = false;
                            if (!parsedRules.MonsterLootChooseOne)
                            {
                                parsedRules.MonsterLootChooseOne = true;
                                changedDefaults = true;
                            }
                            if (!parsedRules.PromptTendencyBeforeRun)
                            {
                                parsedRules.PromptTendencyBeforeRun = true;
                                changedDefaults = true;
                            }
                            parsedRules.RefreshPowerFlag();
                            BazaarLauncherPlugin.MigratedV12_1Defaults.Value = true;

                            TournamentPresetStore.MigrateDefaultsToV12_1();
                            _lobbyRules.MonsterLootChooseOne = true;
                            _lobbyRules.RefreshPowerFlag();
                            _lobbyBuf.SyncFromRules(_lobbyRules);
                            if (changedDefaults)
                            {
                                PersistMigratedDefaultRulesAsync(parsedRules.Clone());
                            }
                        }

                        _normalRules = parsedRules;
                        _normalRules.NormalizeBounds();
                        _normalBuf.SyncFromRules(_normalRules);
                        Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                    }
                    if (catResp.Ok && meResp.Ok)
                    {
                        SetStatus($"已同步：{_catalog.Archetypes.Count} 流派 / {_catalog.Items.Count} 物品 / {_catalog.Skills.Count} 技能", isError: false);
                    }
                    else
                    {
                        string err = !catResp.Ok ? catResp.Error : meResp.Error;
                        SetStatus($"规则同步部分失败（已使用标准默认）：{Truncate(err, 64)}", isError: true);
                    }
                    RefreshHudOverlays(force: true);
                    RefreshModalContentsIfOpen();
                });
            }
            catch (Exception ex)
            {
                _pendingUpdates.Enqueue(() =>
                {
                    if (mySeq != _rulesFetchSeq) return;
                    _isBusy = false;
                    SetStatus("同步异常：" + Truncate(ex.Message, 64), isError: true);
                    RefreshStatusBarUi();
                });
            }
        });
    }

    private void SaveNormalRulesAsync()
    {
        var snapshot = _normalRules.Clone();
        snapshot.NormalizeBounds();
        _isBusy = true;
        SetStatus("正在保存普通/排位玩法配置...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            var resp = await NameAuth.PutJsonAsync("/custom-rules/me", snapshot).ConfigureAwait(false);
            var saved = resp.Ok ? TryDeserializeRules(resp.Body) : null;
            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                if (resp.Ok)
                {
                    if (saved != null)
                    {
                        _normalRules = saved;
                        _normalRules.NormalizeBounds();
                        _normalBuf.SyncFromRules(_normalRules);
                    }
                    Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                    SetStatus("配置已保存并生效！", isError: false);
                }
                else
                {
                    SetStatus("保存失败：" + Truncate(resp.Error, 72), isError: true);
                }
                RefreshHudOverlays(force: true);
                RefreshModalContentsIfOpen();
            });
        });
    }

    private void ResetNormalRulesAsync()
    {
        _isBusy = true;
        SetStatus("正在恢复私服标准默认配置...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync("/custom-rules/me/reset", new { }).ConfigureAwait(false);
            var reset = resp.Ok ? TryDeserializeRules(resp.Body) : null;
            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                if (resp.Ok)
                {
                    _normalRules = reset ?? new CustomRunRuleSet();
                    _normalRules.NormalizeBounds();
                    _normalBuf.SyncFromRules(_normalRules);
                    SetStatus("已恢复私服标准默认配置（野怪自选默认生效，超模项已关闭）", isError: false);
                }
                else
                {
                    _normalRules.ResetToDefaults();
                    _normalBuf.SyncFromRules(_normalRules);
                    SetStatus("本地已重置；服务端重置失败：" + Truncate(resp.Error, 56), isError: true);
                }
                Modules.NativeMenuEmbedModule.Instance?.SyncSelectedQuickModeFromRules(_normalRules.SpecialModeId, _normalRules.PresetId);
                RefreshHudOverlays(force: true);
                RefreshModalContentsIfOpen();
            });
        });
    }

    private void RefreshTournamentLobbyAsync(bool forceOverwriteHostDraft, bool autoApplyLastIfNewHostLobby = false)
    {
        if (_lobbyPollInFlight) return;
        _lobbyPollInFlight = true;

        Task.Run(async () =>
        {
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var memResp = await NameAuth.GetJsonAsync("/membership").ConfigureAwait(false);
                if (!memResp.Ok || string.IsNullOrWhiteSpace(memResp.Body))
                {
                    _pendingUpdates.Enqueue(() =>
                    {
                        _lobbyPollInFlight = false;
                        _currentLobby = null;
                        AutoSyncCurrentLobbyModsIfNeeded(null);
                        SyncSocialSseForLobbyOrPanelState();
                        RefreshHudOverlays(force: true);
                        RefreshModalContentsIfOpen();
                    });
                    return;
                }

                var memObj = JObject.Parse(memResp.Body);
                string lobbyId = (string)(memObj["LobbyId"] ?? memObj["lobbyId"]);
                if (string.IsNullOrWhiteSpace(lobbyId))
                {
                    _pendingUpdates.Enqueue(() =>
                    {
                        _lobbyPollInFlight = false;
                        _currentLobby = null;
                        _lobbyDirty = false;
                        AutoSyncCurrentLobbyModsIfNeeded(null);
                        SyncSocialSseForLobbyOrPanelState();
                        RefreshHudOverlays(force: true);
                        RefreshModalContentsIfOpen();
                    });
                    return;
                }

                var lobbyResp = await NameAuth.GetJsonAsync("/lobbies/" + Uri.EscapeDataString(lobbyId)).ConfigureAwait(false);
                if (!lobbyResp.Ok || string.IsNullOrWhiteSpace(lobbyResp.Body))
                {
                    _pendingUpdates.Enqueue(() =>
                    {
                        _lobbyPollInFlight = false;
                        SetStatus("获取锦标赛房间失败：" + Truncate(lobbyResp.Error, 64), isError: true);
                        RefreshStatusBarUi();
                    });
                    return;
                }

                var detail = ParseLobbyDetail(lobbyResp.Body, lobbyId);
                var lobbyJo = JObject.Parse(lobbyResp.Body);
                string lobbyState = (string)(lobbyJo["state"] ?? lobbyJo["State"]) ?? string.Empty;
                bool hasStarted = (bool?)lobbyJo["HasStarted"]
                                  ?? (bool?)lobbyJo["hasStarted"]
                                  ?? string.Equals(lobbyState, "started", StringComparison.OrdinalIgnoreCase);

                _pendingUpdates.Enqueue(() =>
                {
                    _lobbyPollInFlight = false;
                    string prevId = _currentLobby?.LobbyId;
                    _currentLobby = detail;

                    bool switchedLobby = !string.Equals(prevId, detail.LobbyId, StringComparison.OrdinalIgnoreCase);
                    if (switchedLobby)
                    {
                        SyncSocialSseForLobbyOrPanelState();
                    }
                    bool isHost = IsLocalPlayerLobbyHost(detail);
                    SyncNativeLobbyDtoPlayerCap(detail.PlayerCap);

                    // #13 房主进入/创建新房间时，自动对齐并提交记忆预设（含 PlayerCap = 8 或 10，绝不回退 16 人）
                    if (isHost && !hasStarted && (switchedLobby || autoApplyLastIfNewHostLobby)
                        && _autoAppliedLobbyIds.Add(detail.LobbyId))
                    {
                        var lastSaved = TournamentPresetStore.GetLastTournamentRules();
                        var targetRules = lastSaved ?? (_lobbyRules != null ? _lobbyRules.Clone() : null);
                        if (targetRules != null)
                        {
                            targetRules.EnsureCollections();
                            if (targetRules.PlayerCap < 2 || targetRules.PlayerCap > 16)
                            {
                                targetRules.PlayerCap = 8;
                            }

                            // 若建房界面额外配置了原生禁用列表而本地记忆未包含，则保留并合并
                            if (targetRules.BannedEncounterIds.Count == 0 && detail.Rules?.BannedEncounterIds?.Count > 0)
                            {
                                targetRules.BannedEncounterIds.AddRange(detail.Rules.BannedEncounterIds);
                            }

                            bool capMismatch = detail.PlayerCap != targetRules.PlayerCap;
                            string serverJson = detail.Rules != null ? JsonConvert.SerializeObject(detail.Rules) : "";
                            string localJson = JsonConvert.SerializeObject(targetRules);
                            bool rulesMismatch = !string.Equals(serverJson, localJson, StringComparison.Ordinal);

                            if (lastSaved != null ? (capMismatch || rulesMismatch) : capMismatch)
                            {
                                _lobbyRules = targetRules.Clone();
                                _lobbyBuf.SyncFromRules(_lobbyRules);
                                _lobbyDirty = false;
                                PushAutoLoadedLobbyRulesAsync(detail.LobbyId, _lobbyRules);
                                RefreshHudOverlays(force: true);
                                RefreshModalContentsIfOpen();
                                return;
                            }
                        }
                    }

                    if (detail.Rules != null && (!isHost || !_lobbyDirty || switchedLobby || forceOverwriteHostDraft))
                    {
                        _lobbyRules = detail.Rules.Clone();
                        _lobbyBuf.SyncFromRules(_lobbyRules);
                        _lobbyDirty = false;
                    }
                    AutoSyncCurrentLobbyModsIfNeeded(detail);
                    RefreshHudOverlays(force: true);
                    RefreshModalContentsIfOpen();
                });
            }
            catch (Exception ex)
            {
                _pendingUpdates.Enqueue(() =>
                {
                    _lobbyPollInFlight = false;
                    SetStatus("刷新房间异常：" + Truncate(ex.Message, 64), isError: true);
                    RefreshStatusBarUi();
                });
            }
        });
    }

    private void PushAutoLoadedLobbyRulesAsync(string lobbyId, CustomRunRuleSet rules)
    {
        var snapshot = rules.Clone();
        snapshot.RefreshPowerFlag();
        SetStatus("正在自动同步上次记忆的锦标赛规则到当前房间...", isError: false);

        Task.Run(async () =>
        {
            var resp = await NameAuth.PutJsonAsync(
                $"/lobbies/{Uri.EscapeDataString(lobbyId)}/rules",
                snapshot).ConfigureAwait(false);

            var saved = resp.Ok ? TryDeserializeRules(resp.Body) : null;
            _pendingUpdates.Enqueue(() =>
            {
                if (resp.Ok)
                {
                    if (saved != null)
                    {
                        _lobbyRules = saved;
                        _lobbyBuf.SyncFromRules(_lobbyRules);
                        SyncNativeLobbyDtoPlayerCap(saved.PlayerCap);
                    }
                    _lobbyDirty = false;
                    SetStatus("已自动同步锦标赛人数与规则到全房！", isError: false);
                    RefreshTournamentLobbyAsync(forceOverwriteHostDraft: true);
                }
                else
                {
                    SetStatus("自动同步记忆规则失败：" + Truncate(resp.Error, 64), isError: true);
                    RefreshStatusBarUi();
                }
            });
        });
    }

    private void SendLobbyBotActionAsync(string action)
    {
        string lobbyId = _currentLobby?.LobbyId;
        if (string.IsNullOrWhiteSpace(lobbyId)) return;

        _isBusy = true;
        SetStatus($"正在执行机器人操作 ({action})...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            var resp = await NameAuth.PostJsonAsync(
                $"/lobbies/{Uri.EscapeDataString(lobbyId)}/bots",
                new { action }).ConfigureAwait(false);

            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                if (resp.Ok)
                {
                    SetStatus($"机器人操作成功：{action}", isError: false);
                    RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false);
                }
                else
                {
                    SetStatus("机器人操作失败：" + Truncate(resp.Error, 64), isError: true);
                }
                RefreshModalContentsIfOpen();
            });
        });
    }

    private void SaveLobbyRulesAsync()
    {
        string lobbyId = _currentLobby?.LobbyId;
        if (string.IsNullOrWhiteSpace(lobbyId)) return;

        var snapshot = _lobbyRules.Clone();
        snapshot.RefreshPowerFlag();
        TournamentPresetStore.SaveLastTournamentRules(snapshot);

        _isBusy = true;
        SetStatus("正在保存并广播锦标赛规则到全房...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            var resp = await NameAuth.PutJsonAsync(
                $"/lobbies/{Uri.EscapeDataString(lobbyId)}/rules",
                snapshot).ConfigureAwait(false);

            var saved = resp.Ok ? TryDeserializeRules(resp.Body) : null;
            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                if (resp.Ok)
                {
                    _autoAppliedLobbyIds.Add(lobbyId);
                    if (saved != null)
                    {
                        _lobbyRules = saved;
                        _lobbyBuf.SyncFromRules(_lobbyRules);
                        TournamentPresetStore.SaveLastTournamentRules(saved);
                    }
                    _lobbyDirty = false;
                    SetStatus("锦标赛规则已保存、广播至全房并记忆到本地！", isError: false);
                    RefreshTournamentLobbyAsync(forceOverwriteHostDraft: true);
                }
                else
                {
                    SetStatus("广播规则失败（本地记忆已保存）：" + Truncate(resp.Error, 56), isError: true);
                }
                RefreshHudOverlays(force: true);
                RefreshModalContentsIfOpen();
            });
        });
    }

    private void GrantFreeChestsFromToolboxAsync(int count)
    {
        _isBusy = true;
        SetStatus($"正在免费补充 {count} 个赛季宝箱并刷新库存...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            var res = await GameDriver.GrantChestsAndRefreshAsync(count).ConfigureAwait(false);
            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                _lastToolboxKey = "";
                if (res.ok)
                {
                    string cntText = res.chestCount >= 0 ? $"（当前未开宝箱：{res.chestCount} 个）" : "";
                    SetStatus($"已成功补充 {count} 个宝箱！{cntText}", isError: false);
                }
                else
                {
                    SetStatus("补充宝箱失败：" + Truncate(res.error, 64), isError: true);
                }
                RefreshModalContentsIfOpen();
            });
        });
    }

    private void ConcedeActiveRunFromToolboxAsync()
    {
        _isBusy = true;
        SetStatus("正在放弃当前未完成对局...", isError: false);
        RefreshModalContentsIfOpen();

        Task.Run(async () =>
        {
            try
            {
                object res = await GameDriver.DoConcedeAsync(new JObject { ["forceHttp"] = true }).ConfigureAwait(false);
                bool ok = true;
                string errMsg = null;
                if (res != null)
                {
                    var okProp = res.GetType().GetProperty("ok");
                    if (okProp != null && okProp.GetValue(res) is bool b) ok = b;
                    var errProp = res.GetType().GetProperty("error");
                    if (errProp != null) errMsg = errProp.GetValue(res)?.ToString();
                }

                _pendingUpdates.Enqueue(() =>
                {
                    _isBusy = false;
                    _lastToolboxKey = "";
                    if (!ok)
                    {
                        SetStatus("放弃对局失败：" + Truncate(string.IsNullOrWhiteSpace(errMsg) ? "服务端未返回成功" : errMsg, 64), isError: true);
                        RefreshModalContentsIfOpen();
                        return;
                    }

                    _activeRunId = "";
                    _activeRunFetchedId = "";
                    _activeRunRules = null;
                    _activeRunIsCustomRuleRun = false;
                    _activeRunFetchSeq++;
                    _activeRunFetchInFlight = false;
                    _nextActiveRunRetryTime = 0f;
                    _activeRunTendencyLocked = false;
                    _showPreRunPrompt = false;
                    CompatAuditPatch.RefreshActiveMainMenuUiFromData();
                    SetStatus("已结束当前对局，现在可以自由切换英雄与打开换装界面！", isError: false);
                    RefreshMainMenuToolboxUi();
                    RefreshModalContentsIfOpen();
                    RefreshHudOverlays(force: true);
                });
            }
            catch (Exception ex)
            {
                _pendingUpdates.Enqueue(() =>
                {
                    _isBusy = false;
                    SetStatus("放弃对局异常：" + Truncate(ex.Message, 64), isError: true);
                    RefreshModalContentsIfOpen();
                });
            }
        });
    }

    // ── 全模式统一的流派倾向热保存 / 开场状态检查 / 唯一一次锁定 ─────────

    private static void PersistMigratedDefaultRulesAsync(CustomRunRuleSet rules)
    {
        if (rules == null) return;
        Task.Run(async () =>
        {
            try { await NameAuth.PutJsonAsync("/custom-rules/me", rules).ConfigureAwait(false); }
            catch { }
        });
    }

    private void SchedulePersonalTendencySync()
    {
        _tendencySyncPending = true;
        _tendencySyncDueTime = Time.unscaledTime + 0.25f;
    }

    private void SavePersonalTendencyDraftAsync(ulong mask)
    {
        SetStatus("正在同步个人流派倾向预设（全模式共用，未锁开局可用）...", isError: false);
        // 在主线程先拍摄规则快照，避免后台线程在 404 回退分支访问/克隆 _normalRules 触发竞态
        var fallbackSnapshot = _normalRules.Clone();
        fallbackSnapshot.SelectedArchetypeMask = mask;

        Task.Run(async () =>
        {
            var resp = await NameAuth.PutJsonAsync("/custom-rules/me/tendency", new { selectedArchetypeMask = mask }).ConfigureAwait(false);
            if (!resp.Ok && resp.StatusCode == 404)
            {
                resp = await NameAuth.PutJsonAsync("/custom-rules/me", fallbackSnapshot).ConfigureAwait(false);
            }
            var saved = resp.Ok ? TryDeserializeRules(resp.Body) : null;
            _pendingUpdates.Enqueue(() =>
            {
                if (resp.Ok)
                {
                    if (saved != null)
                    {
                        _normalRules.SelectedArchetypeMask = saved.SelectedArchetypeMask;
                    }
                    SetStatus("个人流派倾向预设已同步（全模式共用）。", isError: false);
                }
                else
                {
                    SetStatus("倾向同步失败：" + Truncate(resp.Error, 56), isError: true);
                }
                RefreshHudOverlays(force: true);
                if (_isOpen) RefreshModalContentsIfOpen();
            });
        });
    }

    private void FetchActiveRunTendencyStateAsync(string observedRunKey, bool force = false)
    {
        if (!force && _activeRunFetchInFlight) return;
        _activeRunFetchInFlight = true;
        int mySeq = ++_activeRunFetchSeq;

        Task.Run(async () =>
        {
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var resp = await NameAuth.GetJsonAsync("/custom-rules/me/active-run-tendency").ConfigureAwait(false);
                if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
                {
                    _pendingUpdates.Enqueue(() =>
                    {
                        if (mySeq != _activeRunFetchSeq) return;
                        _activeRunFetchInFlight = false;
                        // 网络瞬态失败时释放 _activeRunFetchedId 并在短退避后允许重试，避免整局永久失步
                        _activeRunFetchedId = "";
                        _nextActiveRunRetryTime = Time.unscaledTime + 3.0f;
                    });
                    return;
                }

                var obj = JObject.Parse(resp.Body);
                bool hasRun = (bool?)obj["hasActiveRun"] ?? false;
                string srvRunId = (string)obj["runId"] ?? observedRunKey;
                bool allowSel = (bool?)obj["allowTendencySelection"] ?? true;
                bool locked = (bool?)obj["tendencyLocked"] ?? true;
                bool canLockNow = (bool?)obj["canLockNow"] ?? false;
                ulong srvMask = (ulong?)obj["selectedArchetypeMask"] ?? 0UL;
                uint day = (uint?)obj["day"] ?? 1u;
                uint hour = (uint?)obj["hour"] ?? 1u;
                string stateName = (string)obj["state"] ?? "";
                bool srvIsCustomRuleRun = (bool?)obj["isCustomRuleRun"] ?? false;
                // v4 追加字段：旧服务端没有这些字段时全是空，不会弹任何提示
                string srvSeedText = obj["seedText"]?.Type == JTokenType.String ? (string)obj["seedText"] : null;
                bool srvCustomSeeded = (bool?)obj["isCustomSeededRun"] ?? false;
                string srvPinnedLineup = obj["pinnedLineupName"]?.Type == JTokenType.String ? (string)obj["pinnedLineupName"] : null;
                string srvStartToast = obj["startToastZh"]?.Type == JTokenType.String ? (string)obj["startToastZh"] : null;

                // P1-15：严格对齐服务端契约，解析 GET /custom-rules/me/active-run-tendency 下发的权威 customRules
                CustomRunRuleSet parsedActiveRunRules = null;
                if (hasRun
                    && obj.TryGetValue("customRules", StringComparison.OrdinalIgnoreCase, out JToken activeRulesTok)
                    && activeRulesTok is JObject activeRulesObj)
                {
                    parsedActiveRunRules = TryDeserializeRules(activeRulesObj.ToString());
                    if (parsedActiveRunRules != null)
                    {
                        parsedActiveRunRules.NormalizeBounds();
                    }
                }

                _pendingUpdates.Enqueue(() =>
                {
                    if (mySeq != _activeRunFetchSeq) return;
                    _activeRunFetchInFlight = false;
                    if (!hasRun)
                    {
                        // 无活跃局时清空权威活跃局规则缓存，避免跨局残留
                        _activeRunRules = null;
                        _activeRunIsCustomRuleRun = false;
                        _activeRunId = "";
                        if (!string.IsNullOrEmpty(observedRunKey))
                        {
                            _activeRunFetchedId = "";
                            _nextActiveRunRetryTime = Time.unscaledTime + 2.0f;
                        }
                        RefreshHudOverlays(force: true);
                        return;
                    }

                    _activeRunId = srvRunId;
                    _activeRunAllowTendency = allowSel;
                    _activeRunLockedMask = srvMask;
                    _activeRunTendencyLocked = locked || !canLockNow;
                    _activeRunRules = parsedActiveRunRules;
                    _activeRunIsCustomRuleRun = srvIsCustomRuleRun;
                    if (_activeRunRules != null)
                    {
                        _activeRunRules.AllowTendencySelection = allowSel;
                        _activeRunRules.SelectedArchetypeMask = allowSel ? srvMask : 0UL;
                    }
                    SpOnActiveRunInfo(srvRunId, srvSeedText, srvCustomSeeded, srvPinnedLineup, srvStartToast);

                    if (allowSel)
                    {
                        _normalRules.SelectedArchetypeMask = srvMask;
                    }

                    bool stillOpening = !_activeRunTendencyLocked
                        && allowSel
                        && day == 1
                        && hour == 1
                        && string.Equals(stateName, "Choice", StringComparison.OrdinalIgnoreCase)
                        && !IsInActiveCombatPlayback();

                    bool promptTendency = _activeRunRules?.PromptTendencyBeforeRun ?? _normalRules.PromptTendencyBeforeRun;
                    if (stillOpening
                        && promptTendency
                        && CombatPatch.SpeedOverride <= 0.1f
                        && !string.Equals(_lastOpeningPromptedRunId, srvRunId, StringComparison.OrdinalIgnoreCase))
                    {
                        _lastOpeningPromptedRunId = srvRunId;
                        OpenOpeningTendencyPromptWindow(srvMask);
                    }

                    RefreshHudOverlays(force: true);
                    if (_isOpen) RefreshModalContentsIfOpen();
                });
            }
            catch
            {
                _pendingUpdates.Enqueue(() =>
                {
                    if (mySeq != _activeRunFetchSeq) return;
                    _activeRunFetchInFlight = false;
                    _activeRunFetchedId = "";
                    _nextActiveRunRetryTime = Time.unscaledTime + 3.0f;
                });
            }
        });
    }

    private void OpenOpeningTendencyPromptWindow(ulong initialMask)
    {
        EnsurePreRunPromptBuilt();
        _openingPromptDraftRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
        _openingPromptDraftRules.SelectedArchetypeMask = initialMask;
        _openingPromptDraftRules.PromptTendencyBeforeRun = _normalRules.PromptTendencyBeforeRun;
        _showPreRunPrompt = true;
        _preRunPromptRoot.SetActive(true);
        RefreshOpeningPromptVisuals();
        RefreshHudOverlays(force: true);
    }

    private void DismissOpeningPromptOnly()
    {
        _showPreRunPrompt = false;
        if (_preRunPromptRoot != null) _preRunPromptRoot.SetActive(false);
        RefreshHudOverlays(force: true);
        // P2-41：弹窗关闭且非活跃锦标赛局/大厅上下文时停止闲置 SSE 长连接
        SyncSocialSseForLobbyOrPanelState();
    }

    private void LockOpeningTendencyOnActiveRunAsync(ulong rawMask, bool closePromptAfter)
    {
        if (_isBusy) return;
        _isBusy = true;
        string runIdSnapshot = _activeRunId;
        SetStatus("正在向服务端锁定本局流派倾向...", isError: false);
        RefreshStatusBarUi();

        Task.Run(async () =>
        {
            var payload = new
            {
                selectedArchetypeMask = rawMask,
                runId = string.IsNullOrEmpty(runIdSnapshot) ? null : runIdSnapshot,
            };
            var resp = await NameAuth.PostJsonAsync("/custom-rules/me/lock-opening-tendency", payload).ConfigureAwait(false);

            JObject obj = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(resp.Body)) obj = JObject.Parse(resp.Body);
            }
            catch { }

            ulong srvMask = (ulong?)obj?["selectedArchetypeMask"] ?? rawMask;
            string errMsg = (string)obj?["error"] ?? resp.Error;

            _pendingUpdates.Enqueue(() =>
            {
                _isBusy = false;
                _activeRunTendencyLocked = true;
                if (resp.Ok)
                {
                    _activeRunLockedMask = srvMask;
                    _normalRules.SelectedArchetypeMask = srvMask;
                    if (_activeRunRules != null) _activeRunRules.SelectedArchetypeMask = srvMask;
                    int cnt = CountBits(srvMask);
                    SetStatus(cnt > 0
                        ? $"本局流派倾向已永久锁定（已选 {cnt} 项），对局途中不可再修改。"
                        : "已确认不选倾向并永久锁定本局，对局途中不可再修改。",
                        isError: false);
                }
                else
                {
                    if (obj != null && obj["selectedArchetypeMask"] != null)
                    {
                        _activeRunLockedMask = srvMask;
                        _normalRules.SelectedArchetypeMask = srvMask;
                        if (_activeRunRules != null) _activeRunRules.SelectedArchetypeMask = srvMask;
                    }
                    SetStatus("流派倾向已锁定，禁止途中修改：" + Truncate(errMsg, 56), isError: true);
                }

                if (closePromptAfter)
                {
                    DismissOpeningPromptOnly();
                }
                RefreshHudOverlays(force: true);
                if (_isOpen) RefreshModalContentsIfOpen();
            });
        });
    }

    private bool TryConsumeEscapeForCustomModal()
    {
        // 二级弹窗（分享码 / 收藏对手等）开着时，Esc 先只关它
        if (SpIsPopupOpen())
        {
            _lastEscConsumedFrame = Time.frameCount;
            SpClosePopup();
            return true;
        }
        if (_showPreRunPrompt)
        {
            _lastEscConsumedFrame = Time.frameCount;
            DismissOpeningPromptOnly();
            return true;
        }
        if (_isOpen)
        {
            _lastEscConsumedFrame = Time.frameCount;
            ClosePanel();
            return true;
        }
        return false;
    }

    // ── Harmony Hooks：原生锦标赛入厅自动加载记忆规则、Esc 互斥与精确等待提示 ──

    private static void InstallHarmonyHooksOnce()
    {
        if (_hooksApplied) return;
        _hooksApplied = true;
        try
        {
            // P0-6 / P1-46：统一走 SafeHarmonyPatcher.TryPatchMethod（优先复用 ModuleHost 托管实例，兜底创建可卸载实例），彻底消灭 RawHarmony.Patch
            var patcher = _hostPatcher
                ?? (_fallbackPatcher ??= new BazaarLauncher.Core.SafeHarmonyPatcher("local.thebazaar.bazaarlauncher.customuihooks"));

            // 监听锦标赛大厅进入：房主建房/入厅时自动加载并应用 lastTournamentRules
            var lobbyStateType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentLobbyState");
            var lobbyOnEnter = lobbyStateType == null ? null : AccessTools.Method(lobbyStateType, "OnEnter");
            if (lobbyOnEnter != null)
            {
                patcher.TryPatchMethod(
                    lobbyOnEnter,
                    postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnNativeTournamentLobbyEnteredPostfix)),
                    optional: true,
                    label: "CustomUI.TournamentLobbyState.OnEnter");
            }

            var smType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentPanelStateMachineController");
            var setLobby = smType == null ? null : AccessTools.Method(smType, "SetLobby");
            if (setLobby != null)
            {
                patcher.TryPatchMethod(
                    setLobby,
                    postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnTournamentPanelSetLobbyPostfix)),
                    optional: true,
                    label: "CustomUI.TournamentPanelStateMachineController.SetLobby");
            }

            // #13 拦截原生锦标赛建房流程，严格对齐记忆预设人数（8 / 10，绝不回落 16 / 1000）
            var hostViewType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentHostSettingsView");
            if (hostViewType != null)
            {
                var hostOnEnable = AccessTools.Method(hostViewType, "OnEnable");
                if (hostOnEnable != null)
                {
                    patcher.TryPatchMethod(
                        hostOnEnable,
                        postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnHostSettingsViewOnEnablePostfix)),
                        optional: true,
                        label: "CustomUI.TournamentHostSettingsView.OnEnable");
                }
                var getCap = AccessTools.Method(hostViewType, "GetRequestedPlayerCap");
                if (getCap != null)
                {
                    patcher.TryPatchMethod(
                        getCap,
                        postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnGetRequestedPlayerCapPostfix)),
                        optional: true,
                        label: "CustomUI.TournamentHostSettingsView.GetRequestedPlayerCap");
                }
            }

            var tourneyHttpType = AccessTools.TypeByName("Networking.TournamentHttpClient");
            var createLobby = tourneyHttpType == null ? null : AccessTools.Method(tourneyHttpType, "CreateLobby");
            if (createLobby != null)
            {
                patcher.TryPatchMethod(
                    createLobby,
                    prefix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnCreateTournamentLobbyPrefix)),
                    postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnCreateTournamentLobbyPostfix)),
                    optional: true,
                    label: "CustomUI.TournamentHttpClient.CreateLobby");
            }

            // #16 监听锦标赛实时状态与等待对手 PvP 错误包，提供清晰简体中文状态提示
            _cachedTssType = AccessTools.TypeByName("Networking.TournamentStatusService");
            if (_cachedTssType != null)
            {
                _tssCurrentStatusProp = _cachedTssType.GetProperty("CurrentStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var applyStatus = AccessTools.Method(_cachedTssType, "ApplyStatus");
                if (applyStatus != null)
                {
                    patcher.TryPatchMethod(
                        applyStatus,
                        postfix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnTournamentApplyStatusPostfix)),
                        optional: true,
                        label: "CustomUI.TournamentStatusService.ApplyStatus");
                }
            }

            var netMgrType = AccessTools.TypeByName("Networking.NetworkManager");
            var handleCmdErr = netMgrType == null ? null : AccessTools.Method(netMgrType, "HandleCommandErrorMessage");
            if (handleCmdErr != null)
            {
                patcher.TryPatchMethod(
                    handleCmdErr,
                    prefix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnNetworkCommandErrorPrefix)),
                    optional: true,
                    label: "CustomUI.NetworkManager.HandleCommandErrorMessage");
            }

            // P1-49 & D4-P1-10：同时拦截 PopupManager.HandleEscape 与 FightMenuDialog.OpenMenu，
            // 优先消费自定义弹窗并在同帧互斥防穿透原生菜单
            var popupMgrType = GameInspector.FindTypeSafe("TheBazaar.UI.PopupManager", "PopupManager");
            if (popupMgrType != null)
            {
                var handleEscBool = AccessTools.Method(popupMgrType, "HandleEscape", new[] { typeof(bool) })
                    ?? AccessTools.Method(popupMgrType, "HandleEscape", Type.EmptyTypes);
                if (handleEscBool != null)
                {
                    patcher.TryPatchMethod(
                        handleEscBool,
                        prefix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnPopupManagerHandleEscapePrefix)),
                        optional: true,
                        label: "CustomUI.PopupManager.HandleEscape");
                }
            }

            var fightMenuType = GameInspector.FindTypeSafe("FightMenuDialog", "TheBazaar.FightMenuDialog");
            var openMenuMethod = fightMenuType == null ? null : AccessTools.Method(fightMenuType, "OpenMenu", Type.EmptyTypes);
            if (openMenuMethod != null)
            {
                patcher.TryPatchMethod(
                    openMenuMethod,
                    prefix: AccessTools.Method(typeof(CustomGameplayUI), nameof(OnFightMenuOpenMenuPrefix)),
                    optional: true,
                    label: "CustomUI.FightMenuDialog.OpenMenu");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("InstallHarmonyHooksOnce: " + ex.Message);
        }
    }

    private static bool IsAnyTmpInputFieldFocused()
    {
        try
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go == null) return false;
            var tmpInput = go.GetComponent<TMPro.TMP_InputField>();
            if (tmpInput != null && tmpInput.isFocused) return true;
            var uguiInput = go.GetComponent<UnityEngine.UI.InputField>();
            if (uguiInput != null && uguiInput.isFocused) return true;
        }
        catch { }
        return false;
    }

    private static bool OnPopupManagerHandleEscapePrefix()
    {
        try
        {
            int frame = Time.frameCount;
            if (_lastEscConsumedFrame == frame)
                return false;

            if (IsAnyTmpInputFieldFocused())
            {
                _lastEscConsumedFrame = frame;
                try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
                return false;
            }

            var inst = Instance;
            if (inst != null && (inst._isOpen || inst._showPreRunPrompt || inst.SpIsPopupOpen()))
            {
                inst.TryConsumeEscapeForCustomModal();
                return false;
            }
        }
        catch { }
        return true;
    }

    private static bool OnFightMenuOpenMenuPrefix()
    {
        try
        {
            int frame = Time.frameCount;
            // 顺序 1：CustomGameplayUI.Update() 本帧已消费 Esc 关闭自定义弹窗
            if (_lastEscConsumedFrame == frame)
                return false;

            var inst = Instance;
            // 顺序 2：正在我们面板的输入框里打字时按 Esc，只取消输入，不弹原生暂停菜单
            if (inst != null && (inst._isOpen || inst._showPreRunPrompt || inst.SpIsPopupOpen()) && IsAnyTmpInputFieldFocused())
            {
                _lastEscConsumedFrame = frame;
                try { EventSystem.current?.SetSelectedGameObject(null); } catch { }
                return false;
            }

            // 顺序 3：PopupManager.HandleEscape 先于 CustomGameplayUI.Update() 执行，立即消费 Esc 关闭自定义弹窗并拦截原生暂停菜单
            if (inst != null && (inst._isOpen || inst._showPreRunPrompt || inst.SpIsPopupOpen()))
            {
                inst.TryConsumeEscapeForCustomModal();
                return false;
            }
        }
        catch { }
        return true;
    }

    private static int ResolvePreferredHostPlayerCap()
    {
        var lastSaved = TournamentPresetStore.GetLastTournamentRules();
        if (lastSaved != null && lastSaved.PlayerCap >= 2 && lastSaved.PlayerCap <= 16)
        {
            return lastSaved.PlayerCap;
        }
        var draft = Instance?._lobbyRules;
        if (draft != null && draft.PlayerCap >= 2 && draft.PlayerCap <= 16)
        {
            return draft.PlayerCap;
        }
        return 8;
    }

    private static void OnHostSettingsViewOnEnablePostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            var lastSaved = TournamentPresetStore.GetLastTournamentRules();
            int cap = ResolvePreferredHostPlayerCap();
            var fInput = __instance.GetType().GetField("_limitSizeInput", BindingFlags.NonPublic | BindingFlags.Instance);
            var inputObj = fInput?.GetValue(__instance);
            if (inputObj != null && !(inputObj is UnityEngine.Object iuo && iuo == null))
            {
                var textProp = inputObj.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                string curText = textProp?.GetValue(inputObj) as string;
                if (string.IsNullOrWhiteSpace(curText) || (lastSaved != null && string.Equals(curText.Trim(), "16", StringComparison.Ordinal)))
                {
                    textProp?.SetValue(inputObj, cap.ToString());
                }
            }
        }
        catch { }
    }

    private static void OnGetRequestedPlayerCapPostfix(object __instance, ref int __result)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            var lastSaved = TournamentPresetStore.GetLastTournamentRules();
            int preferred = ResolvePreferredHostPlayerCap();
            if (__result <= 0 || __result > 16 || (lastSaved != null && __result == 16 && preferred != 16))
            {
                __result = preferred;
            }
        }
        catch { }
    }

    private static void OnCreateTournamentLobbyPrefix(object request)
    {
        try
        {
            if (request == null) return;
            var capProp = request.GetType().GetProperty("PlayerCap", BindingFlags.Public | BindingFlags.Instance);
            if (capProp == null) return;
            int reqCap = capProp.GetValue(request) is int c ? c : 1000;
            int preferred = ResolvePreferredHostPlayerCap();
            var lastSaved = TournamentPresetStore.GetLastTournamentRules();

            // 原生未限制人数会传 1000；若已记忆预设（8 或 10）或未填写自定义上限，建房瞬间即直接按预设人数提交
            if (reqCap <= 0 || reqCap > 16 || (lastSaved != null && reqCap == 16 && preferred != 16))
            {
                capProp.SetValue(request, preferred);
            }
        }
        catch { }
    }

    private static void OnCreateTournamentLobbyPostfix(Task __result)
    {
        try
        {
            if (__result == null) return;
            __result.ContinueWith(_ =>
            {
                var inst = Instance;
                if (inst == null) return;
                inst._pendingUpdates.Enqueue(() =>
                {
                    inst._nextLobbyPollTime = 0f;
                    inst.RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false, autoApplyLastIfNewHostLobby: true);
                });
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void SyncNativeLobbyDtoPlayerCap(int playerCap)
    {
        try
        {
            if (playerCap < 2 || playerCap > 16) return;
            var smType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentPanelStateMachineController");
            if (smType == null) return;
            var sm = UnityEngine.Object.FindFirstObjectByType(smType);
            if (sm == null || (sm is UnityEngine.Object uo && uo == null)) return;

            var dto = GameInspector.GetMember(sm, "DTO");
            if (dto == null) return;
            var capProp = dto.GetType().GetProperty("PlayerCap", BindingFlags.Public | BindingFlags.Instance);
            if (capProp != null && capProp.GetValue(dto) is int curCap && curCap != playerCap)
            {
                capProp.SetValue(dto, playerCap);
            }
        }
        catch { }
    }

    private static void OnTournamentApplyStatusPostfix(object status)
    {
        try
        {
            if (status == null) return;
            _latestTourneyStatus = ReadTourneyStatusSnapshot(status);
            var inst = Instance;
            if (inst != null)
            {
                inst._pendingUpdates.Enqueue(() => inst.RefreshHudOverlays(force: false));
            }
        }
        catch { }
    }

    private static TourneyStatusSnapshot ReadTourneyStatusSnapshot(object dto)
    {
        if (dto == null) return null;
        return new TourneyStatusSnapshot
        {
            Phase = GameInspector.GetMember(dto, "Phase") as string ?? "",
            CurrentDay = GameInspector.GetMember(dto, "CurrentDay") is uint d ? d : 0u,
            TotalPlayers = GameInspector.GetMember(dto, "TotalPlayers") is int tp ? tp : 0,
            ActivePlayers = GameInspector.GetMember(dto, "ActivePlayers") is int ap ? ap : 0,
            ReadyPlayers = GameInspector.GetMember(dto, "ReadyPlayers") is int rp ? rp : 0,
            DeadlineAt = GameInspector.GetMember(dto, "DeadlineAt") as DateTime?,
            PlayerReadyForPvp = GameInspector.GetMember(dto, "PlayerReadyForPvp") is bool pr && pr,
            CombatSubmitted = GameInspector.GetMember(dto, "CombatSubmitted") is bool cs && cs,
            CanStartPvp = !(GameInspector.GetMember(dto, "CanStartPvp") is bool canPvp) || canPvp,
            WaitingForOtherPlayers = GameInspector.GetMember(dto, "WaitingForOtherPlayers") is bool wop && wop,
            WaitingForHumanDayStart = GameInspector.GetMember(dto, "WaitingForHumanDayStart") is bool whds && whds,
            AssignedOpponentUsername = GameInspector.GetMember(dto, "AssignedOpponentUsername") as string ?? "",
        };
    }

    private static bool OnNetworkCommandErrorPrefix(object message)
    {
        try
        {
            if (message == null) return true;
            string raw = GameInspector.GetMember(message, "Message") as string;
            string msgId = GameInspector.GetMember(message, "MessageId") as string;
            string errType = GameInspector.GetMember(message, "ErrorType")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(raw) && string.IsNullOrWhiteSpace(msgId)) return true;

            if (!TryMapTournamentWaitingErrorZh(raw, msgId, errType, out string zhNotice))
                return true;

            // 触发原生 Events.CombatBlocked 让状态机保持备战一致，并用右上角简体中文横幅代替英文弹窗阻塞
            BazaarLauncher.Core.GameEventsBridge.TryTrigger("CombatBlocked");

            _tourneyWaitErrorNotice = zhNotice;
            _tourneyWaitErrorUntil = Time.unscaledTime + 8f;
            var inst = Instance;
            if (inst != null)
            {
                inst._pendingUpdates.Enqueue(() => inst.RefreshHudOverlays(force: true));
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    private const string TournamentPvpWaitSyncCode = "TOURNAMENT_PVP_WAIT_SYNC";

    private static bool TryMapTournamentWaitingErrorZh(string raw, string messageId, string errorType, out string zhNotice)
    {
        zhNotice = "";
        string trimmed = (raw ?? "").Trim();
        string trimmedId = (messageId ?? "").Trim();

        // P1-49：彻底移除宽泛子串匹配（如 "等待" / "Hour 6" / "reach day" 等），仅匹配精确错误码或官方/自建服务端锚定屏障文案
        bool isExactWaitCode = string.Equals(trimmedId, TournamentPvpWaitSyncCode, StringComparison.Ordinal)
            || string.Equals(trimmed, TournamentPvpWaitSyncCode, StringComparison.Ordinal)
            || trimmed.StartsWith("[" + TournamentPvpWaitSyncCode + "]", StringComparison.Ordinal)
            || trimmed.StartsWith(TournamentPvpWaitSyncCode + ":", StringComparison.Ordinal);

        if (isExactWaitCode)
        {
            zhNotice = "已提交备战，正在等待其余选手就位后开启 PvP 对决...";
            return true;
        }

        if (string.Equals(trimmed, "Combat will begin shortly.", StringComparison.Ordinal)
            || string.Equals(trimmed, "Waiting for the remaining tournament players to finish PvP.", StringComparison.Ordinal))
        {
            zhNotice = "已提交备战，正在等待其余选手就位后开启 PvP 对决...";
            return true;
        }

        // 自建服务端锦标赛 Hour 6 屏障（RunEngine / TournamentEndpoints 明确下发的 NotAllowed 锚定前缀），且严格限定当前处于锦标赛对局内
        if (IsActiveRunTournamentMode()
            && string.Equals(errorType, "NotAllowed", StringComparison.Ordinal))
        {
            if (trimmed.StartsWith("需等待同房真人玩家到达第 ", StringComparison.Ordinal))
            {
                zhNotice = "已到达第 6 小时备战区，正在等待其余真人选手抵达第 6 小时...";
                return true;
            }
            if (trimmed.StartsWith("正在等待其他真人玩家完成第 ", StringComparison.Ordinal))
            {
                zhNotice = "已提交备战，正在等待其余选手就位后开启 PvP 对决...";
                return true;
            }
        }

        return false;
    }

    private static void OnNativeTournamentLobbyEnteredPostfix(object __instance)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            var sm = GameInspector.GetMember(__instance, "StateMachine");
            HandleNativeLobbyStateMachineUpdate(inst, sm);
        }
        catch { }
    }

    private static void OnTournamentPanelSetLobbyPostfix(object __instance, object lobby)
    {
        try
        {
            var inst = Instance;
            if (inst == null || __instance == null || (__instance is UnityEngine.Object uo && uo == null) || lobby == null) return;
            HandleNativeLobbyStateMachineUpdate(inst, __instance);
        }
        catch { }
    }

    private static void HandleNativeLobbyStateMachineUpdate(CustomGameplayUI inst, object sm)
    {
        try
        {
            if (inst == null || sm == null || (sm is UnityEngine.Object uo && uo == null)) return;
            var isHostM = sm.GetType().GetMethod("IsLocalPlayerHost", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            bool isHost = isHostM?.Invoke(sm, null) is bool hb && hb;
            var dto = GameInspector.GetMember(sm, "DTO");
            bool hasStarted = dto != null && GameInspector.GetMember(dto, "HasStarted") is bool hs && hs;

            // D6-P1-08：去除 && isHost 限制，房主与非房主通过原版 UI 进房时均主动刷新房间详情并同步房间 Mod 套件；
            // 仅房主且未开赛时自动提交上次记忆规则
            if (dto != null)
            {
                inst._nextLobbyPollTime = 0f;
                inst.RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false, autoApplyLastIfNewHostLobby: isHost && !hasStarted);
            }
            else if (inst._currentLobby != null)
            {
                inst._currentLobby = null;
                inst._lobbyDirty = false;
                BazaarLauncher.Core.ModContentSyncClient.Instance?.ClearSyncedRoomBundleMods();
            }
            inst.RefreshHudOverlays(force: true);
        }
        catch { }
    }

    // ── 工具方法与热键解析 ──────────────────────────────────────────────────

    private bool WasKeyPressedThisFrame(bool isEscape)
    {
        if (!_inputChecked)
        {
            _inputChecked = true;
            InitializeInputSystemDelegates();
        }

        if (_getKeyboardCurrent != null && _getKeyWasPressed != null)
        {
            try
            {
                var kb = _getKeyboardCurrent();
                if (kb != null)
                {
                    var keyCtrl = isEscape ? _getEscKey?.Invoke(kb) : _getF8Key?.Invoke(kb);
                    if (keyCtrl != null) return _getKeyWasPressed(keyCtrl);
                }
            }
            catch { }
        }

        if (!_legacyInputDisabled)
        {
            try
            {
                return Input.GetKeyDown(isEscape ? KeyCode.Escape : KeyCode.F8);
            }
            catch (InvalidOperationException)
            {
                _legacyInputDisabled = true;
            }
            catch { }
        }
        return false;
    }

    private void InitializeInputSystemDelegates()
    {
        try
        {
            var kbType = AccessTools.TypeByName("UnityEngine.InputSystem.Keyboard");
            var btnType = AccessTools.TypeByName("UnityEngine.InputSystem.Controls.ButtonControl");
            if (kbType == null || btnType == null) return;

            var curProp = kbType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
            var f8Prop = kbType.GetProperty("f8Key", BindingFlags.Public | BindingFlags.Instance);
            var escProp = kbType.GetProperty("escapeKey", BindingFlags.Public | BindingFlags.Instance);
            var pressedProp = btnType.GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);
            if (curProp == null || f8Prop == null || escProp == null || pressedProp == null) return;

            _getKeyboardCurrent = Expression.Lambda<Func<object>>(
                Expression.Convert(Expression.Property(null, curProp), typeof(object))).Compile();

            var pKb = Expression.Parameter(typeof(object), "kb");
            _getF8Key = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Property(Expression.Convert(pKb, kbType), f8Prop), typeof(object)), pKb).Compile();
            _getEscKey = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Property(Expression.Convert(pKb, kbType), escProp), typeof(object)), pKb).Compile();

            var pBtn = Expression.Parameter(typeof(object), "btn");
            _getKeyWasPressed = Expression.Lambda<Func<object, bool>>(
                Expression.Property(Expression.Convert(pBtn, btnType), pressedProp), pBtn).Compile();
        }
        catch { }
    }

    private void ApplyCatalog(CustomRulesCatalog cat)
    {
        _catalog = cat ?? new CustomRulesCatalog();
        _catalog.Archetypes ??= new List<ArchetypeCatalogEntry>();
        _catalog.EncounterCategories ??= new List<EncounterCategoryEntry>();
        _catalog.Items ??= new List<CatalogCardEntry>();
        _catalog.Skills ??= new List<CatalogCardEntry>();

        _itemNameMap.Clear();
        foreach (var it in _catalog.Items)
        {
            if (!string.IsNullOrEmpty(it.Id)) _itemNameMap[it.Id] = it.NameZh ?? it.Id;
        }
        _skillNameMap.Clear();
        foreach (var sk in _catalog.Skills)
        {
            if (!string.IsNullOrEmpty(sk.Id)) _skillNameMap[sk.Id] = sk.NameZh ?? sk.Id;
        }
        _catalogLoaded = true;
    }

    private static CustomRunRuleSet TryDeserializeRules(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var token = JToken.Parse(json);
            if (token is JObject obj)
            {
                // PUT /lobbies/{id}/rules 返回 CoordinatorLobbyDto { ..., Rules: CustomRunRuleSet }，需先解包内层 rules/Rules
                if (obj.TryGetValue("rules", StringComparison.OrdinalIgnoreCase, out var rulesToken))
                {
                    if (rulesToken is JObject innerObj)
                    {
                        token = innerObj;
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            if (token is not JObject targetObj)
            {
                return null;
            }

            var r = targetObj.ToObject<CustomRunRuleSet>();
            if (r == null) return null;
            r.EnsureCollections();
            r.RefreshPowerFlag();
            return r;
        }
        catch { return null; }
    }

    private static LobbyDetailSnapshot ParseLobbyDetail(string json, string fallbackId)
    {
        var obj = JObject.Parse(json);
        var lobby = new LobbyDetailSnapshot
        {
            LobbyId = (string)(obj["LobbyId"] ?? obj["lobbyId"]) ?? fallbackId,
            Code = (string)(obj["Code"] ?? obj["code"]) ?? "",
            Name = (string)(obj["Name"] ?? obj["name"]) ?? "锦标赛房间",
            HostAccountId = (string)(obj["HostAccountId"] ?? obj["hostAccountId"]) ?? "",
            PlayerCap = (int?)(obj["PlayerCap"] ?? obj["playerCap"] ?? obj["MaxPlayers"] ?? obj["maxPlayers"]) ?? 8,
        };

        if (obj["Members"] is JArray mArr || (obj["members"] is JArray mArr2 && (mArr = mArr2) != null))
        {
            foreach (var tok in mArr)
            {
                if (tok is not JObject mo) continue;
                string aid = (string)(mo["AccountId"] ?? mo["accountId"]) ?? "";
                bool isAi = (bool?)(mo["IsAi"] ?? mo["isAi"])
                            ?? aid.StartsWith("ai-", StringComparison.OrdinalIgnoreCase);
                lobby.Members.Add(new LobbyMemberSummary
                {
                    AccountId = aid,
                    Username = (string)(mo["Username"] ?? mo["username"]) ?? aid,
                    Hero = (string)(mo["Hero"] ?? mo["hero"]) ?? "",
                    State = (string)(mo["State"] ?? mo["state"]) ?? "",
                    IsAi = isAi,
                });
            }
        }

        if (obj["RequiredMods"] is JArray rmArr || (obj["requiredMods"] is JArray rmArr2 && (rmArr = rmArr2) != null))
        {
            foreach (var tok in rmArr)
            {
                if (tok is not JObject mo) continue;
                string mid = (string)(mo["ModId"] ?? mo["modId"]) ?? "";
                if (string.IsNullOrWhiteSpace(mid)) continue;
                lobby.RequiredMods.Add(new RequiredModEntry
                {
                    ModId = mid,
                    Name = (string)(mo["Name"] ?? mo["name"]) ?? mid,
                    Version = (string)(mo["Version"] ?? mo["version"]) ?? "-",
                    Author = (string)(mo["Author"] ?? mo["author"]) ?? "-",
                    CustomCardCount = (int?)(mo["CustomCardCount"] ?? mo["customCardCount"]) ?? 0,
                    CustomEventCount = (int?)(mo["CustomEventCount"] ?? mo["customEventCount"]) ?? 0,
                    AssetCount = (int?)(mo["AssetCount"] ?? mo["assetCount"]) ?? 0,
                    AvailableOnServer = (bool?)(mo["AvailableOnServer"] ?? mo["availableOnServer"]) ?? true,
                    DownloadUrl = (string)(mo["DownloadUrl"] ?? mo["downloadUrl"]) ?? "",
                });
            }
        }

        var rulesTok = obj["Rules"] ?? obj["rules"];
        if (rulesTok != null && rulesTok.Type == JTokenType.Object)
        {
            lobby.Rules = TryDeserializeRules(rulesTok.ToString());
        }
        return lobby;
    }

    private static bool IsLocalPlayerLobbyHost(LobbyDetailSnapshot lobby)
    {
        if (lobby == null) return false;
        string host = NameAuth.NormalizeAccountId(lobby.HostAccountId);
        string me = NameAuth.CurrentAccountId;
        return !string.IsNullOrEmpty(host) && !string.IsNullOrEmpty(me) && host == me;
    }

    private static List<CatalogCardEntry> FilterCatalog(
        List<CatalogCardEntry> source, string query, string heroFilter, int limit)
    {
        if (source == null || source.Count == 0) return new List<CatalogCardEntry>();
        string q = (query ?? "").Trim();
        var list = new List<CatalogCardEntry>(limit);
        foreach (var entry in source)
        {
            if (entry == null) continue;
            if (!string.IsNullOrEmpty(heroFilter)
                && !string.Equals(entry.Hero, heroFilter, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(entry.Hero, "Common", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (!string.IsNullOrEmpty(q))
            {
                bool hit = (!string.IsNullOrEmpty(entry.NameZh) && entry.NameZh.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                           || (!string.IsNullOrEmpty(entry.Id) && entry.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                           || (!string.IsNullOrEmpty(entry.Hero) && entry.Hero.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!hit) continue;
            }
            list.Add(entry);
            if (list.Count >= limit) break;
        }
        return list;
    }

    private string ResolveItemName(string id)
        => !string.IsNullOrEmpty(id) && _itemNameMap.TryGetValue(id, out var zh) ? zh : (id ?? "");

    private string ResolveSkillName(string id)
        => !string.IsNullOrEmpty(id) && _skillNameMap.TryGetValue(id, out var zh) ? zh : (id ?? "");

    private static string FindEnchantLabel(string key)
    {
        foreach (var e in Enchantments)
        {
            if (string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)) return e.LabelZh;
        }
        return key;
    }

    private static string FormatHeroZh(string hero)
        => !string.IsNullOrEmpty(hero) && HeroNameZh.TryGetValue(hero, out var zh) ? zh : (hero ?? "Vanessa");

    private static int CountBits(ulong mask)
    {
        int c = 0;
        while (mask != 0UL) { mask &= mask - 1UL; c++; }
        return c;
    }

    private static string Truncate(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxLen) return s ?? "";
        return s.Substring(0, maxLen) + "...";
    }

    private void SetStatus(string msg, bool isError)
    {
        _statusMessage = msg ?? "";
        _statusIsError = isError;
        if (BazaarLauncher.Core.MainThreadDispatcher.IsOnMainThread)
        {
            RefreshStatusBarUi();
        }
        else
        {
            _pendingUpdates.Enqueue(RefreshStatusBarUi);
        }
    }

    private static bool IsInActiveCombatPlayback()
    {
        string appState = GameInspector.GetAppStateName();
        return appState is "CombatState" or "PVPCombatState" or "ReplayState";
    }

    private static PropertyInfo _cachedDataSelectedPlayModeProp;
    private static PropertyInfo _cachedDataSelectedHeroProp;

    private static bool IsTournamentUiContext()
    {
        try
        {
            if (_cachedDataSelectedPlayModeProp == null)
            {
                var dataType = AccessTools.TypeByName("TheBazaar.Data");
                _cachedDataSelectedPlayModeProp = dataType?.GetProperty("SelectedPlayMode", BindingFlags.Public | BindingFlags.Static);
            }
            string playMode = _cachedDataSelectedPlayModeProp?.GetValue(null)?.ToString();
            if (string.Equals(playMode, "Tournament", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        catch { }
        return Instance != null && Instance._currentLobby != null;
    }

    private static bool IsActiveRunTournamentMode()
    {
        try
        {
            var run = GameInspector.GetDataRun();
            string playMode = GameInspector.GetMember(run, "PlayMode")?.ToString();
            return string.Equals(playMode, "Tournament", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private CustomRunRuleSet ResolveHudDisplayRules(bool tourneyContext)
    {
        if (!tourneyContext) return _normalRules;
        if (_currentLobby != null) return _lobbyRules ?? _currentLobby.Rules;
        return TournamentPresetStore.GetLastTournamentRules() ?? _lobbyRules;
    }

    private static string DetectCurrentHero()
    {
        try
        {
            var run = GameInspector.GetDataRun();
            var player = GameInspector.GetMember(run, "Player");
            string runHero = GameInspector.GetMember(player, "Hero")?.ToString();
            if (!string.IsNullOrWhiteSpace(runHero) && !string.Equals(runHero, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return runHero;
            }

            if (_cachedDataSelectedHeroProp == null)
            {
                var dataType = AccessTools.TypeByName("TheBazaar.Data");
                _cachedDataSelectedHeroProp = dataType?.GetProperty("SelectedHero", BindingFlags.Public | BindingFlags.Static);
            }
            string sel = _cachedDataSelectedHeroProp?.GetValue(null)?.ToString();
            if (!string.IsNullOrWhiteSpace(sel) && !string.Equals(sel, "Common", StringComparison.OrdinalIgnoreCase))
            {
                return sel;
            }
        }
        catch { }
        return "Vanessa";
    }
}

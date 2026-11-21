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
    private string _lastOpeningPromptedRunId = "";
    private float _nextViewportCheckTime;
    private bool _tendencySyncPending;
    private float _tendencySyncDueTime;
    private static bool _hooksApplied;
    private static BazaarLauncher.Core.SafeHarmonyPatcher _hostPatcher;
    private int _rulesVersion;
    private bool _hudDirty = true;
    private string _lastObservedAppState = "";
    private float _nextPerfTabRefreshTime;

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
    private static FieldInfo _eventsCombatBlockedField;
    private static MethodInfo _combatBlockedTriggerMethod;

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

        // 订阅事件总线实现事件驱动按需刷新（彻底取代 0.15s 高频轮询，修复 P1-1）
        BazaarLauncherPlugin.Host?.EventBus.Subscribe<BazaarModSdk.RunStateChangedModEvent>(_ => MarkHudDirty());
        BazaarLauncherPlugin.Host?.EventBus.Subscribe<BazaarModSdk.CombatLifecycleModEvent>(_ => MarkHudDirty());
        BazaarLauncherPlugin.Host?.EventBus.Subscribe<BazaarModSdk.ActiveRulesUpdatedModEvent>(_ =>
        {
            MarkHudDirty();
            if (_isOpen) RefreshModalContentsIfOpen();
        });
        BazaarLauncherPlugin.Host?.EventBus.Subscribe<BazaarModSdk.ModAssetsSyncedModEvent>(_ =>
        {
            _pendingUpdates.Enqueue(() =>
            {
                if (_isOpen && _activeTab == UiTab.Workshop) RefreshWorkshopTabUi();
            });
        });
    }

    private void Start()
    {
        EnsureHudBuilt();
        RefreshHudOverlays(force: true);
        StartInitialBackgroundFetch();
    }

    private void OnDestroy()
    {
        try { _socialSseCts?.Cancel(); _socialSseCts?.Dispose(); } catch { }
        _socialSseCts = null;
        if (Instance == this) Instance = null;
        SceneManager.activeSceneChanged -= OnActiveSceneChanged;
        if (_uiRootGo != null) Destroy(_uiRootGo);
        _ui.Dispose();
    }

    private void OnActiveSceneChanged(Scene prev, Scene next)
    {
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
        }
        _lastViewportScreenW = -1;
        _lastViewportScreenH = -1;
        _lastLetterBoxRect = new(-1f, -1f, -1f, -1f);
        _nextViewportCheckTime = 0f;
        _activeRunId = "";
        _activeRunFetchedId = "";
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

        // 热键 F8 打开/收起，Esc 关闭弹窗（零分配检查）
        if (WasKeyPressedThisFrame(isEscape: false) && !IsInActiveCombatPlayback())
        {
            ToggleOpen();
        }
        else if ((_isOpen || _showPreRunPrompt) && WasKeyPressedThisFrame(isEscape: true))
        {
            if (_showPreRunPrompt)
            {
                DismissOpeningPromptOnly();
            }
            else if (_isOpen)
            {
                ClosePanel();
            }
        }

        // 事件驱动按需刷新（修复 P1-1）：仅当脏标记置位、AppState 变更或低频保底 1.0s 时刷新，严禁战斗中 0.15s 高频刷新
        string curAppState = GameInspector.GetAppStateName();
        if (!string.Equals(curAppState, _lastObservedAppState, StringComparison.Ordinal))
        {
            _lastObservedAppState = curAppState;
            _hudDirty = true;
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
                var runRules = ResolveInRunDisplayRules(isTourneyRun);
                return SummarizeRules(runRules, isTourneyRun).ActiveCount;
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
        bool inMainMenu = GameInspector.IsMainMenuSceneName(GameInspector.GetActiveSceneName());
        bool isTourneyContext = _isOpen ? (_activeTab == UiTab.Tournament) : IsTournamentUiContext();
        var activeRules = isTourneyContext ? ResolveHudDisplayRules(true) : _normalRules;
        var sum = SummarizeRules(activeRules, isTourneyContext);
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
        rules.EnsureCollections();
        rules.RefreshPowerFlag();

        bool isTournament = string.Equals(target, "tournament", StringComparison.OrdinalIgnoreCase);
        if (isTournament)
        {
            string lobbyId = _currentLobby?.LobbyId;
            if (string.IsNullOrEmpty(lobbyId))
            {
                return (false, 404, "当前未处于任何锦标赛房间中", null);
            }
            var resp = await NameAuth.PutJsonAsync($"/lobbies/{Uri.EscapeDataString(lobbyId)}/rules", rules).ConfigureAwait(false);
            if (resp.Ok)
            {
                var saved = TryDeserializeRules(resp.Body) ?? rules.Clone();
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
            var resp = await NameAuth.PutJsonAsync("/custom-rules/me", rules).ConfigureAwait(false);
            if (resp.Ok)
            {
                var saved = TryDeserializeRules(resp.Body) ?? rules.Clone();
                GameDriver.RunOnMain(() =>
                {
                    DrainPendingUpdates();
                    _normalRules = saved.Clone();
                    _normalBuf.SyncFromRules(_normalRules);
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

    private void StartInitialBackgroundFetch()
    {
        if (_initialFetchStarted && _isBusy) return;
        _initialFetchStarted = true;
        _isBusy = true;
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
                    _isBusy = false;
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
                        _normalBuf.SyncFromRules(_normalRules);
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
        snapshot.RefreshPowerFlag();
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
                        _normalBuf.SyncFromRules(_normalRules);
                    }
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
                    _normalRules.EnsureCollections();
                    _normalRules.RefreshPowerFlag();
                    _normalBuf.SyncFromRules(_normalRules);
                    SetStatus("已恢复私服标准默认配置（野怪自选默认生效，超模项已关闭）", isError: false);
                }
                else
                {
                    _normalRules.ResetToStandardValues(preserveArchetypeAndLobbyFlags: false);
                    _normalBuf.SyncFromRules(_normalRules);
                    SetStatus("本地已重置；服务端重置失败：" + Truncate(resp.Error, 56), isError: true);
                }
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
                bool hasStarted = (bool?)JObject.Parse(lobbyResp.Body)["HasStarted"]
                                  ?? (bool?)JObject.Parse(lobbyResp.Body)["hasStarted"]
                                  ?? false;

                _pendingUpdates.Enqueue(() =>
                {
                    _lobbyPollInFlight = false;
                    string prevId = _currentLobby?.LobbyId;
                    _currentLobby = detail;

                    bool switchedLobby = !string.Equals(prevId, detail.LobbyId, StringComparison.OrdinalIgnoreCase);
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
                await GameDriver.DoConcedeAsync(new JObject { ["forceHttp"] = true }).ConfigureAwait(false);
                _pendingUpdates.Enqueue(() =>
                {
                    _isBusy = false;
                    _lastToolboxKey = "";
                    SetStatus("已结束当前对局，现在可以自由切换英雄与打开换装界面！", isError: false);
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
        RefreshStatusBarUi();

        Task.Run(async () =>
        {
            var resp = await NameAuth.PutJsonAsync("/custom-rules/me/tendency", new { selectedArchetypeMask = mask }).ConfigureAwait(false);
            if (!resp.Ok && resp.StatusCode == 404)
            {
                var fallback = _normalRules.Clone();
                fallback.SelectedArchetypeMask = mask;
                resp = await NameAuth.PutJsonAsync("/custom-rules/me", fallback).ConfigureAwait(false);
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

    private void FetchActiveRunTendencyStateAsync(string observedRunKey)
    {
        if (_activeRunFetchInFlight) return;
        _activeRunFetchInFlight = true;

        Task.Run(async () =>
        {
            try
            {
                await NameAuth.EnsureAccountIdentityAsync().ConfigureAwait(false);
                var resp = await NameAuth.GetJsonAsync("/custom-rules/me/active-run-tendency").ConfigureAwait(false);
                if (!resp.Ok || string.IsNullOrWhiteSpace(resp.Body))
                {
                    _pendingUpdates.Enqueue(() => { _activeRunFetchInFlight = false; });
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

                _pendingUpdates.Enqueue(() =>
                {
                    _activeRunFetchInFlight = false;
                    if (!hasRun) return;

                    _activeRunId = srvRunId;
                    _activeRunAllowTendency = allowSel;
                    _activeRunLockedMask = srvMask;
                    _activeRunTendencyLocked = locked || !canLockNow;

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

                    if (stillOpening
                        && _normalRules.PromptTendencyBeforeRun
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
                _pendingUpdates.Enqueue(() => { _activeRunFetchInFlight = false; });
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
    }

    private void LockOpeningTendencyOnActiveRunAsync(ulong rawMask, bool closePromptAfter)
    {
        if (_isBusy) return;
        _isBusy = true;
        SetStatus("正在向服务端锁定本局流派倾向...", isError: false);
        RefreshStatusBarUi();

        Task.Run(async () =>
        {
            var payload = new
            {
                selectedArchetypeMask = rawMask,
                runId = string.IsNullOrEmpty(_activeRunId) ? null : _activeRunId,
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

    // ── Harmony Hooks：原生锦标赛入厅自动加载记忆规则 & 同步提示 ──────────

    private static void InstallHarmonyHooksOnce()
    {
        if (_hooksApplied) return;
        _hooksApplied = true;
        try
        {
            // P0-6 修复：优先复用 ModuleHost 托管的 SafeHarmonyPatcher，确保禁用/卸载时 100% UnpatchAll 无僵尸补丁
            var h = _hostPatcher?.RawHarmony ?? new Harmony("local.thebazaar.bazaarlauncher.customuihooks");

            // 监听锦标赛大厅进入：房主建房/入厅时自动加载并应用 lastTournamentRules
            var lobbyStateType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentLobbyState");
            var lobbyOnEnter = lobbyStateType == null ? null : AccessTools.Method(lobbyStateType, "OnEnter");
            if (lobbyOnEnter != null)
            {
                h.Patch(lobbyOnEnter, postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnNativeTournamentLobbyEnteredPostfix)));
            }

            var smType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentPanelStateMachineController");
            var setLobby = smType == null ? null : AccessTools.Method(smType, "SetLobby");
            if (setLobby != null)
            {
                h.Patch(setLobby, postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnTournamentPanelSetLobbyPostfix)));
            }

            // #13 拦截原生锦标赛建房流程，严格对齐记忆预设人数（8 / 10，绝不回落 16 / 1000）
            var hostViewType = AccessTools.TypeByName("TheBazaar.UI.Tournaments.TournamentHostSettingsView");
            if (hostViewType != null)
            {
                var hostOnEnable = AccessTools.Method(hostViewType, "OnEnable");
                if (hostOnEnable != null)
                {
                    h.Patch(hostOnEnable, postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnHostSettingsViewOnEnablePostfix)));
                }
                var getCap = AccessTools.Method(hostViewType, "GetRequestedPlayerCap");
                if (getCap != null)
                {
                    h.Patch(getCap, postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnGetRequestedPlayerCapPostfix)));
                }
            }

            var tourneyHttpType = AccessTools.TypeByName("Networking.TournamentHttpClient");
            var createLobby = tourneyHttpType == null ? null : AccessTools.Method(tourneyHttpType, "CreateLobby");
            if (createLobby != null)
            {
                h.Patch(
                    createLobby,
                    prefix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnCreateTournamentLobbyPrefix)),
                    postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnCreateTournamentLobbyPostfix)));
            }

            // #16 监听锦标赛实时状态与等待对手 PvP 错误包，提供清晰简体中文状态提示
            _cachedTssType = AccessTools.TypeByName("Networking.TournamentStatusService");
            if (_cachedTssType != null)
            {
                _tssCurrentStatusProp = _cachedTssType.GetProperty("CurrentStatus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var applyStatus = AccessTools.Method(_cachedTssType, "ApplyStatus");
                if (applyStatus != null)
                {
                    h.Patch(applyStatus, postfix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnTournamentApplyStatusPostfix)));
                }
            }

            var netMgrType = AccessTools.TypeByName("Networking.NetworkManager");
            var handleCmdErr = netMgrType == null ? null : AccessTools.Method(netMgrType, "HandleCommandErrorMessage");
            if (handleCmdErr != null)
            {
                h.Patch(handleCmdErr, prefix: new HarmonyMethod(typeof(CustomGameplayUI), nameof(OnNetworkCommandErrorPrefix)));
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("InstallHarmonyHooksOnce: " + ex.Message);
        }
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
            if (string.IsNullOrWhiteSpace(raw)) return true;

            if (!TryMapTournamentWaitingErrorZh(raw, out string zhNotice))
                return true;

            // 触发原生 Events.CombatBlocked 让状态机保持备战一致，并用右上角简体中文横幅代替英文弹窗阻塞
            try
            {
                if (_eventsCombatBlockedField == null)
                {
                    var eventsType = AccessTools.TypeByName("TheBazaar.Events");
                    _eventsCombatBlockedField = eventsType?.GetField("CombatBlocked", BindingFlags.Public | BindingFlags.Static);
                }
                var evObj = _eventsCombatBlockedField?.GetValue(null);
                if (evObj != null)
                {
                    _combatBlockedTriggerMethod ??= evObj.GetType().GetMethod("Trigger", BindingFlags.Public | BindingFlags.Instance);
                    _combatBlockedTriggerMethod?.Invoke(evObj, null);
                }
            }
            catch { }

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

    private static bool TryMapTournamentWaitingErrorZh(string raw, out string zhNotice)
    {
        zhNotice = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;

        bool tourneyContext = IsActiveRunTournamentMode() || _latestTourneyStatus != null;
        bool looksLikeWait = raw.IndexOf("Combat will begin shortly", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("Waiting for other", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("Waiting for player", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("Hour 6", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("reach day", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("waiting for human", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("PvP", StringComparison.OrdinalIgnoreCase) >= 0 && raw.IndexOf("wait", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("等待", StringComparison.Ordinal) >= 0;

        if (!looksLikeWait) return false;
        if (!tourneyContext && !raw.Equals("Combat will begin shortly.", StringComparison.OrdinalIgnoreCase))
            return false;

        if (raw.IndexOf("Hour 6", StringComparison.OrdinalIgnoreCase) >= 0
            || raw.IndexOf("第6小时", StringComparison.Ordinal) >= 0
            || raw.IndexOf("第 6 小时", StringComparison.Ordinal) >= 0)
        {
            zhNotice = "已到达第 6 小时备战区，正在等待其余真人选手抵达第 6 小时...";
        }
        else if (raw.IndexOf("day", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            zhNotice = "正在等待其余锦标赛选手同步进入新的一天...";
        }
        else
        {
            zhNotice = "已提交备战，正在等待其余选手就位后开启 PvP 对决...";
        }
        return true;
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

            if (dto != null && isHost && !hasStarted)
            {
                inst._nextLobbyPollTime = 0f;
                inst.RefreshTournamentLobbyAsync(forceOverwriteHostDraft: false, autoApplyLastIfNewHostLobby: true);
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

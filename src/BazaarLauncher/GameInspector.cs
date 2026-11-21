using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using BazaarGameShared.Domain.Core.Types;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;

namespace BazaarLauncher;

/// <summary>
/// 读取 Unity UI 渲染控件与客户端内存中的实时对局状态，供实机双向核对（/inspect）。
/// 采用反射委托缓存 + 紧凑模式 + 实时性能遥测（perf），必须在 Unity 主线程调用。
/// </summary>
internal static class GameInspector
{
    private const BindingFlags AnyDeclared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    // ── 高速反射委托缓存 ──────────────────────────────────────────────────────
    private static readonly ConcurrentDictionary<(Type, string), Func<object, object>> _memberGetters = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object>> _templateMethodGetters = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object>> _enchantmentMethodGetters = new();
    private static readonly ConcurrentDictionary<Type, Func<object, bool>> _hasCardsPendingRemovalGetters = new();
    private static readonly ConcurrentDictionary<string, Type> _safeTypeCache = new(StringComparer.Ordinal);

    private static bool _handlesInitialized;
    private static Type _dataType;
    private static Type _appStateType;
    private static Type _cmdType;
    private static Type _boardManagerType;
    private static Type _sceneLoaderType;
    private static Type _mainMenuControllerType;
    private static Type _itemCardType;
    private static Type _socketEffectType;

    // 核心静态属性/字段预编译委托
    private static Func<object> _getDataRun;
    private static Func<object> _getDataCurrentState;
    private static Func<object> _getDataEntities;
    private static Func<object> _getDataSkillSockets;
    private static Func<object> _getDataLookup;
    private static Func<object> _getDataHasActiveRun;
    private static Func<object> _getDataPlayerHealthBar;
    private static Func<object> _getDataCardRemoval;
    private static Func<object> _getDataTransitionIn;
    private static Func<object> _getDataCurrentEncounterId;
    private static Func<object> _getDataCurrentEncounterController;
    private static Func<object> _getDataSimPvpOpponent;

    private static Func<object> _getAppCurrentState;
    private static Func<object> _getAppWaitingServer;
    private static Func<object> _getAppSimProcessing;
    private static Func<object> _getAppBlockInput;
    private static Func<object> _getAppGameSimHandler;
    private static Func<object> _getAppGameStateHandler;

    private static Func<object> _getBoardManagerSingleton;
    private static Func<object> _getSceneLoaderTransitioning;
    private static Func<object> _getCmdInstance;
    private static Func<object, object, object> _getCardControllerDelegate;

    // 缓存瞬态 Unity 单例引用（校验 UnityEngine.Object != null 后直接复用）
    private static object _cachedCmdObj;
    private static object _cachedMainMenuController;

    // ── 实时性能遥测（60 帧滑动窗口）──────────────────────────────────────────
    private const int PerfWindowSize = 60;
    private static readonly float[] _frameDeltas = new float[PerfWindowSize];
    private static int _frameIdx;
    private static int _frameCount;
    private static float _frameSum;
    private static double _lastMainThreadOpMs;

    private static void EnsureHandles()
    {
        if (_handlesInitialized) return;
        _handlesInitialized = true;

        _dataType = FindTypeSafe("TheBazaar.Data");
        _appStateType = FindTypeSafe("TheBazaar.AppState");
        _cmdType = FindTypeSafe("TheBazaar.Cmd");
        _boardManagerType = FindTypeSafe("BoardManager");
        _sceneLoaderType = FindTypeSafe("SceneLoader");
        _mainMenuControllerType = FindTypeSafe("TheBazaar.UI.Menu.MainMenuController");
        _itemCardType = FindTypeSafe("BazaarGameClient.Domain.Models.Cards.ItemCard");
        _socketEffectType = FindTypeSafe("BazaarGameClient.Domain.Models.Cards.SocketEffect");

        if (_dataType != null)
        {
            _getDataRun = CompileStaticGetter(_dataType, "Run");
            _getDataCurrentState = CompileStaticGetter(_dataType, "CurrentState");
            _getDataEntities = CompileStaticGetter(_dataType, "Entities");
            _getDataSkillSockets = CompileStaticGetter(_dataType, "SkillSockets");
            _getDataLookup = CompileStaticGetter(_dataType, "CardAndSkillLookup");
            _getDataHasActiveRun = CompileStaticGetter(_dataType, "HasActiveRun");
            _getDataPlayerHealthBar = CompileStaticGetter(_dataType, "PlayerHealthBar");
            _getDataCardRemoval = CompileStaticGetter(_dataType, "CardRemoval");
            _getDataTransitionIn = CompileStaticGetter(_dataType, "TransitionIn");
            _getDataCurrentEncounterId = CompileStaticGetter(_dataType, "CurrentEncounterId");
            _getDataCurrentEncounterController = CompileStaticGetter(_dataType, "CurrentEncounterController");
            _getDataSimPvpOpponent = CompileStaticGetter(_dataType, "SimPvpOpponent");
        }

        if (_appStateType != null)
        {
            _getAppCurrentState = CompileStaticGetter(_appStateType, "CurrentState");
            _getAppWaitingServer = CompileStaticGetter(_appStateType, "IsWaitingForServerResponse");
            _getAppSimProcessing = CompileStaticGetter(_appStateType, "SimHandlersProcessing");
            _getAppBlockInput = CompileStaticGetter(_appStateType, "BlockInput");
            _getAppGameSimHandler = CompileStaticGetter(_appStateType, "_gameSimHandler");
            _getAppGameStateHandler = CompileStaticGetter(_appStateType, "_gameStateHandler");
        }

        if (_boardManagerType != null)
        {
            _getBoardManagerSingleton = CompileStaticGetterIncludingBase(_boardManagerType, "Instance");
        }

        if (_sceneLoaderType != null)
        {
            _getSceneLoaderTransitioning = CompileStaticGetter(_sceneLoaderType, "IsTransitioning");
        }

        if (_cmdType != null)
        {
            var m = _cmdType.GetMethod("GetInstance", AnyStatic, null, Type.EmptyTypes, null);
            if (m != null)
            {
                try
                {
                    var call = Expression.Convert(Expression.Call(m), typeof(object));
                    _getCmdInstance = Expression.Lambda<Func<object>>(call).Compile();
                }
                catch
                {
                    _getCmdInstance = () => m.Invoke(null, null);
                }
            }
        }

        var lookupType = FindTypeSafe("TheBazaar.Tooltips.CardAndSkillLookup");
        var cardType = FindTypeSafe("BazaarGameClient.Domain.Models.Cards.Card");
        if (lookupType != null && cardType != null)
        {
            var mCtrl = lookupType.GetMethod("GetCardController", AnyInstance, null, new[] { cardType }, null);
            if (mCtrl != null)
            {
                try
                {
                    var pLookup = Expression.Parameter(typeof(object), "lookup");
                    var pCard = Expression.Parameter(typeof(object), "card");
                    var call = Expression.Call(
                        Expression.Convert(pLookup, lookupType),
                        mCtrl,
                        Expression.Convert(pCard, cardType));
                    _getCardControllerDelegate = Expression.Lambda<Func<object, object, object>>(
                        Expression.Convert(call, typeof(object)), pLookup, pCard).Compile();
                }
                catch
                {
                    _getCardControllerDelegate = (l, c) => mCtrl.Invoke(l, new[] { c });
                }
            }
        }
    }

    internal static void SampleFrame(float unscaledDeltaTime)
    {
        if (unscaledDeltaTime <= 0.0001f || unscaledDeltaTime >= 2.0f) return;

        if (_frameCount < PerfWindowSize)
        {
            _frameDeltas[_frameIdx] = unscaledDeltaTime;
            _frameSum += unscaledDeltaTime;
            _frameCount++;
        }
        else
        {
            _frameSum -= _frameDeltas[_frameIdx];
            _frameDeltas[_frameIdx] = unscaledDeltaTime;
            _frameSum += unscaledDeltaTime;
        }
        _frameIdx = (_frameIdx + 1) % PerfWindowSize;
    }

    internal static void RecordMainThreadOpMs(double ms)
    {
        _lastMainThreadOpMs = ms;
    }

    internal static object GetCurrentAppState()
    {
        EnsureHandles();
        try { return _getAppCurrentState?.Invoke(); }
        catch { return null; }
    }

    internal static string GetAppStateName()
    {
        try
        {
            var cur = GetCurrentAppState();
            return cur?.GetType().Name ?? "null";
        }
        catch { return "err"; }
    }

    internal static string GetActiveSceneName()
    {
        try { return SceneManager.GetActiveScene().name ?? ""; }
        catch { return ""; }
    }

    internal static bool IsMainMenuSceneName(string activeScene)
        => string.Equals(activeScene, "MainMenuScene", StringComparison.OrdinalIgnoreCase)
           || string.Equals(activeScene, "HeroSelectScene", StringComparison.OrdinalIgnoreCase);

    internal static bool IsActiveInRunState(string activeScene, string appStateName)
    {
        // 主大厅场景即使残留旧 Data.Run/Data.CurrentState，也必须判定为不在局内
        if (IsMainMenuSceneName(activeScene)) return false;
        if (appStateName is "null" or "EmptyState" or "err" &&
            !string.Equals(activeScene, "Gameplay", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    internal static object GetDataRun() { EnsureHandles(); return SafeInvoke(_getDataRun); }
    internal static object GetDataCurrentState() { EnsureHandles(); return SafeInvoke(_getDataCurrentState); }
    internal static IDictionary GetDataEntities() { EnsureHandles(); return SafeInvoke(_getDataEntities) as IDictionary; }
    internal static IDictionary GetDataSkillSockets() { EnsureHandles(); return SafeInvoke(_getDataSkillSockets) as IDictionary; }
    internal static object GetDataLookup() { EnsureHandles(); return SafeInvoke(_getDataLookup); }
    internal static bool ReadHasActiveRunFlag() { EnsureHandles(); return SafeInvoke(_getDataHasActiveRun) is bool b && b; }
    internal static object GetDataCurrentEncounterId() { EnsureHandles(); return SafeInvoke(_getDataCurrentEncounterId); }

    internal static bool IsCurrentEncounterPortraitVisible()
    {
        EnsureHandles();
        var ctrl = SafeInvoke(_getDataCurrentEncounterController);
        if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return false;
        return GetMember(ctrl, "IsCardVisible") is bool vis && vis;
    }

    internal static string GetPvpOpponentHero()
    {
        EnsureHandles();
        var opp = SafeInvoke(_getDataSimPvpOpponent);
        return opp == null ? null : GetMember(opp, "Hero")?.ToString();
    }

    internal static double GetFpsEstimate()
    {
        float avgDt = _frameCount > 0 ? (_frameSum / _frameCount) : Time.unscaledDeltaTime;
        double fps = avgDt > 0.0001f ? (1.0 / avgDt) : 0.0;
        return Math.Round(fps, 1);
    }

    internal static string ResolveCardTitlePublic(object card) => ResolveCardTitle(card);

    internal static object GetCmdCached()
    {
        EnsureHandles();
        if (_cachedCmdObj is UnityEngine.Object uo && uo != null) return _cachedCmdObj;
        _cachedCmdObj = SafeInvoke(_getCmdInstance);
        return _cachedCmdObj;
    }

    internal static object GetBoardManagerCached()
    {
        EnsureHandles();
        var bm = SafeInvoke(_getBoardManagerSingleton);
        return bm is UnityEngine.Object uo && uo == null ? null : bm;
    }

    internal static bool IsSceneTransitioning()
    {
        EnsureHandles();
        return SafeInvoke(_getSceneLoaderTransitioning) is bool b && b;
    }

    internal static bool IsMenuReady()
    {
        EnsureHandles();
        if (_mainMenuControllerType == null) return false;
        if (_cachedMainMenuController is not UnityEngine.Object uo || uo == null)
        {
            _cachedMainMenuController = UnityEngine.Object.FindFirstObjectByType(_mainMenuControllerType);
        }
        if (_cachedMainMenuController is not UnityEngine.Object uo2 || uo2 == null) return false;
        return GetMember(_cachedMainMenuController, "_isInitialized") is bool init && init;
    }

    internal static bool IsClientBusy(out string reason)
    {
        EnsureHandles();

        var cmd = GetCmdCached();
        if (cmd != null && GetMember(cmd, "IsProcessing") is bool cmdBusy && cmdBusy)
        {
            reason = "cmd";
            return true;
        }

        if (SafeInvoke(_getAppWaitingServer) is bool waitSrv && waitSrv)
        {
            reason = "waitingServer";
            return true;
        }

        if (SafeInvoke(_getAppSimProcessing) is bool simBusy && simBusy)
        {
            reason = "simHandler";
            return true;
        }

        var gsh = SafeInvoke(_getAppGameSimHandler);
        if (gsh != null && GetMember(gsh, "IsProcessing") is bool gsBusy && gsBusy)
        {
            reason = "gameSimHandler";
            return true;
        }

        var gst = SafeInvoke(_getAppGameStateHandler);
        if (gst != null && GetMember(gst, "IsProcessing") is bool gtBusy && gtBusy)
        {
            reason = "gameStateHandler";
            return true;
        }

        var bm = GetBoardManagerCached();
        if (bm != null)
        {
            if (GetMember(bm, "IsUpdatingBoard") is bool updBoard && updBoard)
            {
                reason = "updatingBoard";
                return true;
            }
            if (GetMember(bm, "IsUpdatingSkillBoard") is bool updSkill && updSkill)
            {
                reason = "updatingSkillBoard";
                return true;
            }
            if (GetMember(bm, "StorageMoving") is bool storMov && storMov)
            {
                reason = "storageMoving";
                return true;
            }
        }

        if (IsSceneTransitioning())
        {
            reason = "sceneTransitioning";
            return true;
        }

        reason = null;
        return false;
    }

    internal static Dictionary<string, object> GetPerfSnapshot()
    {
        float avgDt = _frameCount > 0 ? (_frameSum / _frameCount) : Time.unscaledDeltaTime;
        float maxDt = avgDt;
        for (int i = 0; i < _frameCount; i++)
        {
            if (_frameDeltas[i] > maxDt) maxDt = _frameDeltas[i];
        }

        double fps = avgDt > 0.0001f ? (1.0 / avgDt) : 0.0;
        double minFps = maxDt > 0.0001f ? (1.0 / maxDt) : 0.0;

        long monoUsed = 0;
        try { monoUsed = Profiler.GetMonoUsedSizeLong(); } catch { }
        if (monoUsed <= 0)
        {
            try { monoUsed = GC.GetTotalMemory(false); } catch { }
        }

        bool busy = IsClientBusy(out string busyReason);

        return new Dictionary<string, object>
        {
            ["fps"] = Math.Round(fps, 1),
            ["minFpsWindow"] = Math.Round(minFps, 1),
            ["avgFrameTimeMs"] = Math.Round(avgDt * 1000.0, 2),
            ["monoHeapMB"] = Math.Round(monoUsed / (1024.0 * 1024.0), 2),
            ["gcGen0"] = GC.CollectionCount(0),
            ["gcGen1"] = GC.CollectionCount(1),
            ["gcGen2"] = GC.CollectionCount(2),
            ["lastMainThreadOpMs"] = Math.Round(_lastMainThreadOpMs, 3),
            ["busy"] = busy,
            ["busyReason"] = busyReason,
            ["frameCount"] = Time.frameCount,
        };
    }

    internal static Dictionary<string, object> InspectAll() => InspectAll(compact: false, includeUi: true);

    internal static Dictionary<string, object> InspectAll(bool compact, bool includeUi)
    {
        long t0 = Stopwatch.GetTimestamp();
        EnsureHandles();

        string appStateName = GetAppStateName();
        string activeScene = GetActiveSceneName();
        bool sceneTransitioning = IsSceneTransitioning();
        bool inMainMenu = IsMainMenuSceneName(activeScene);

        var result = new Dictionary<string, object>
        {
            ["ok"] = true,
            ["inRun"] = false,
            ["appState"] = appStateName,
            ["isReplayState"] = appStateName == "ReplayState",
            ["combatSpeed"] = CombatPatch.SpeedOverride,
            ["hasActiveRunFlag"] = ReadHasActiveRunFlag(),
            ["activeScene"] = activeScene,
            ["isSceneTransitioning"] = sceneTransitioning,
            ["menuReady"] = inMainMenu && (appStateName == "null" || appStateName == "EmptyState") && IsMenuReady(),
        };

        if (!compact)
        {
            result["cloudGuard"] = CloudGuard.GetStats();
            result["compatGuards"] = CompatAuditPatch.PatchedCount;
            result["compatSuppressed"] = CompatAuditPatch.GetSuppressedStats();
            result["timestampUtc"] = DateTime.UtcNow.ToString("O");
        }

        try
        {
            bool canBeInRun = IsActiveInRunState(activeScene, appStateName);
            var run = canBeInRun ? SafeInvoke(_getDataRun) : null;
            var state = canBeInRun ? SafeInvoke(_getDataCurrentState) : null;

            if (!canBeInRun || run == null || state == null)
            {
                result["note"] = !canBeInRun ? "not in a run (menu/idle)" : "not in a run";
                result["hand"] = new List<object>();
                result["stash"] = new List<object>();
                result["skills"] = new List<object>();
                result["socketEffects"] = new List<object>();
                result["selection"] = new List<object>();
                result["opponentHand"] = new List<object>();
                result["opponentSkills"] = new List<object>();
                result["opponentSocketEffects"] = new List<object>();
                if (!compact) result["cards"] = new List<object>();
                FinalizeTiming(result, t0);
                return result;
            }

            result["inRun"] = true;
            result["runState"] = GetMember(state, "StateName")?.ToString();
            result["currentEncounterId"] = GetMember(state, "CurrentEncounterId")?.ToString();
            result["rerollCost"] = GetMember(state, "RerollCost");
            result["rerollsRemaining"] = GetMember(state, "RerollsRemaining");

            var selectionIds = ReadStringList(GetMember(state, "SelectionSet"));
            result["selectionSet"] = selectionIds;

            var selRules = GetMember(state, "SelectionContextRules");
            if (selRules != null)
            {
                result["selectionRules"] = new Dictionary<string, object>
                {
                    ["canSelectMultiple"] = GetMember(selRules, "CanSelectMultiple"),
                    ["selectionIsFree"] = GetMember(selRules, "SelectionIsFree"),
                    ["canExit"] = GetMember(selRules, "CanExit"),
                    ["maxSelections"] = GetMember(selRules, "MaxSelections"),
                    ["willAutoSellOnExit"] = GetMember(selRules, "WillAutoSellOnExit"),
                };
            }

            result["day"] = GetMember(run, "Day");
            result["hour"] = GetMember(run, "Hour");
            result["victories"] = GetMember(run, "Victories");
            result["losses"] = GetMember(run, "Losses");
            result["gameModeId"] = GetMember(run, "GameModeId")?.ToString();
            result["playMode"] = GetMember(run, "PlayMode")?.ToString();

            var player = GetMember(run, "Player");
            if (player != null)
            {
                var pMap = DescribePlayer(player, checkHealthBar: !compact);
                result["player"] = pMap;
                result["hero"] = pMap.GetValueOrDefault("hero");
                result["gold"] = pMap.GetValueOrDefault("gold");
                result["income"] = pMap.GetValueOrDefault("income");
                result["prestige"] = pMap.GetValueOrDefault("prestige");
                result["health"] = pMap.GetValueOrDefault("health");
                result["healthMax"] = pMap.GetValueOrDefault("healthMax");
                result["shield"] = pMap.GetValueOrDefault("shield");
                result["level"] = pMap.GetValueOrDefault("level");
                result["experience"] = pMap.GetValueOrDefault("experience");
                result["unlockedSlots"] = pMap.GetValueOrDefault("unlockedSlots");
            }

            var opponent = GetMember(run, "Opponent");
            if (opponent != null)
            {
                result["opponent"] = DescribePlayer(opponent, checkHealthBar: false);
            }

            var lookup = includeUi ? SafeInvoke(_getDataLookup) : null;
            var skillSockets = SafeInvoke(_getDataSkillSockets) as IDictionary;
            var entities = SafeInvoke(_getDataEntities) as IDictionary;

            Dictionary<string, int> selectionOrder = null;
            if (selectionIds.Count > 0)
            {
                selectionOrder = new Dictionary<string, int>(selectionIds.Count, StringComparer.Ordinal);
                for (int i = 0; i < selectionIds.Count; i++) selectionOrder[selectionIds[i]] = i;
            }

            var handList = new List<Dictionary<string, object>>(10);
            var stashList = new List<Dictionary<string, object>>(10);
            var skillList = new List<Dictionary<string, object>>(8);
            var socketEffectList = new List<Dictionary<string, object>>(4);
            var selectionList = new List<Dictionary<string, object>>(selectionIds.Count);
            var oppHandList = new List<Dictionary<string, object>>(10);
            var oppSkillList = new List<Dictionary<string, object>>(8);
            var oppSocketEffectList = new List<Dictionary<string, object>>(4);
            var allCards = compact ? null : new List<Dictionary<string, object>>(entities?.Count ?? 16);
            int totalCardCount = 0;

            if (entities != null)
            {
                foreach (DictionaryEntry kv in entities)
                {
                    var card = kv.Value;
                    if (card == null) continue;
                    totalCardCount++;

                    var desc = DescribeCardWithUi(card, lookup, skillSockets, compact, includeUi);
                    allCards?.Add(desc);

                    string iid = desc.GetValueOrDefault("instanceId") as string ?? "";
                    string owner = desc.GetValueOrDefault("owner") as string ?? "";
                    string section = desc.GetValueOrDefault("section") as string ?? "";
                    string cardType = desc.GetValueOrDefault("type") as string ?? "";

                    if (selectionOrder != null && selectionOrder.ContainsKey(iid))
                    {
                        selectionList.Add(desc);
                        continue;
                    }

                    bool isOpp = string.Equals(owner, "Opponent", StringComparison.OrdinalIgnoreCase);
                    if (IsSocketEffectCard(card, cardType))
                    {
                        if (isOpp) oppSocketEffectList.Add(desc);
                        else socketEffectList.Add(desc);
                    }
                    else if (string.Equals(cardType, "Skill", StringComparison.OrdinalIgnoreCase))
                    {
                        if (isOpp) oppSkillList.Add(desc);
                        else skillList.Add(desc);
                    }
                    else if (isOpp)
                    {
                        oppHandList.Add(desc);
                    }
                    else if (string.Equals(section, "Hand", StringComparison.OrdinalIgnoreCase))
                    {
                        handList.Add(desc);
                    }
                    else if (string.Equals(section, "Stash", StringComparison.OrdinalIgnoreCase))
                    {
                        stashList.Add(desc);
                    }
                }
            }

            if (handList.Count > 1) handList.Sort((a, b) => SocketIndex(a).CompareTo(SocketIndex(b)));
            if (stashList.Count > 1) stashList.Sort((a, b) => SocketIndex(a).CompareTo(SocketIndex(b)));
            if (socketEffectList.Count > 1) socketEffectList.Sort((a, b) => SocketIndex(a).CompareTo(SocketIndex(b)));
            if (oppHandList.Count > 1) oppHandList.Sort((a, b) => SocketIndex(a).CompareTo(SocketIndex(b)));
            if (oppSocketEffectList.Count > 1) oppSocketEffectList.Sort((a, b) => SocketIndex(a).CompareTo(SocketIndex(b)));
            if (selectionList.Count > 1 && selectionOrder != null)
            {
                selectionList.Sort((a, b) =>
                {
                    int ia = selectionOrder.TryGetValue(a["instanceId"] as string ?? "", out var va) ? va : int.MaxValue;
                    int ib = selectionOrder.TryGetValue(b["instanceId"] as string ?? "", out var vb) ? vb : int.MaxValue;
                    return ia.CompareTo(ib);
                });
            }

            result["hand"] = handList;
            result["stash"] = stashList;
            result["skills"] = skillList;
            result["socketEffects"] = socketEffectList;
            result["selection"] = selectionList;
            result["opponentHand"] = oppHandList;
            result["opponentSkills"] = oppSkillList;
            result["opponentSocketEffects"] = oppSocketEffectList;
            if (!compact) result["cards"] = allCards;
            result["cardCount"] = totalCardCount;

            string runStateStr = result.GetValueOrDefault("runState") as string ?? "";
            if (string.Equals(runStateStr, "Pedestal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(appStateName, "PedestalState", StringComparison.Ordinal))
            {
                var pedestalInfo = DescribePedestalState(result.GetValueOrDefault("currentEncounterId") as string);
                if (pedestalInfo != null)
                {
                    result["pedestal"] = pedestalInfo;
                }
            }

            var combatAudit = CombatPatch.GetLastCombatAuditSnapshot();
            if (combatAudit != null)
            {
                result["lastCombatAudit"] = combatAudit;
            }
        }
        catch (Exception ex)
        {
            result["ok"] = false;
            result["error"] = ex.GetType().Name + ": " + ex.Message;
        }

        FinalizeTiming(result, t0);
        return result;
    }

    private static void FinalizeTiming(Dictionary<string, object> result, long t0)
    {
        double elapsedMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _lastMainThreadOpMs = elapsedMs;
        result["inspectMs"] = Math.Round(elapsedMs, 3);
        result["perf"] = GetPerfSnapshot();
    }

    private static Dictionary<string, object> DescribePlayer(object player, bool checkHealthBar)
    {
        var p = new Dictionary<string, object>(16)
        {
            ["combatantId"] = GetMember(player, "CombatantId")?.ToString(),
            ["hero"] = GetMember(player, "Hero")?.ToString(),
        };

        if (GetMember(player, "Attributes") is IDictionary attrs)
        {
            var rawAttrs = new Dictionary<string, int>(attrs.Count);
            foreach (DictionaryEntry kv in attrs)
            {
                if (kv.Key != null && kv.Value is int iv)
                    rawAttrs[kv.Key.ToString()] = iv;
            }
            p["attributes"] = rawAttrs;
            p["health"] = rawAttrs.GetValueOrDefault("Health", 0);
            p["healthMax"] = rawAttrs.GetValueOrDefault("HealthMax", 0);
            p["shield"] = rawAttrs.GetValueOrDefault("Shield", 0);
            p["gold"] = rawAttrs.GetValueOrDefault("Gold", 0);
            p["income"] = rawAttrs.GetValueOrDefault("Income", 0);
            p["prestige"] = rawAttrs.GetValueOrDefault("Prestige", 0);
            p["level"] = rawAttrs.GetValueOrDefault("Level", 0);
            p["experience"] = rawAttrs.GetValueOrDefault("Experience", 0);
            p["burn"] = rawAttrs.GetValueOrDefault("Burn", 0);
            p["poison"] = rawAttrs.GetValueOrDefault("Poison", 0);
            p["regen"] = rawAttrs.GetValueOrDefault("Regen", 0);
            p["rage"] = rawAttrs.GetValueOrDefault("Rage", 0);
        }

        var hand = GetMember(player, "Hand");
        var container = GetMember(hand, "Container");
        var unlocked = GetMember(container, "UnlockedSockets");
        if (unlocked != null) p["unlockedSlots"] = unlocked;

        if (checkHealthBar)
        {
            var hpBar = SafeInvoke(_getDataPlayerHealthBar);
            if (hpBar != null) p["uiHealthBarPresent"] = true;
        }

        return p;
    }

    private static Dictionary<string, object> DescribeCardWithUi(
        object card, object lookup, IDictionary skillSockets, bool compact, bool includeUi)
    {
        string iid = GetMember(card, "InstanceId")?.ToString() ?? "";
        string tplId = GetMember(card, "TemplateId")?.ToString() ?? "";
        string cardType = GetMember(card, "Type")?.ToString() ?? "";
        string tier = GetMember(card, "Tier")?.ToString() ?? "";
        string size = GetMember(card, "Size")?.ToString() ?? "";
        var owner = GetMember(card, "Owner");

        var d = new Dictionary<string, object>(32)
        {
            ["instanceId"] = iid,
            ["templateId"] = tplId,
            ["name"] = ResolveCardTitle(card),
            ["type"] = cardType,
            ["tier"] = tier,
            ["size"] = size,
            ["owner"] = owner == null ? null : GetMember(owner, "CombatantId")?.ToString(),
            ["section"] = GetMember(card, "Section")?.ToString(),
            ["socket"] = GetMember(card, "LeftSocketId")?.ToString(),
            ["state"] = GetMember(card, "State")?.ToString(),
            ["enchantment"] = ResolveEnchantment(card),
        };

        if (skillSockets != null && skillSockets.Contains(card))
        {
            d["skillSocket"] = skillSockets[card]?.ToString();
        }

        var attrMap = new Dictionary<string, int>();
        if (GetMember(card, "Attributes") is IDictionary attrs)
        {
            foreach (DictionaryEntry kv in attrs)
            {
                if (kv.Key != null && kv.Value is int iv)
                    attrMap[kv.Key.ToString()] = iv;
            }
        }
        d["attributes"] = attrMap;

        // 高频核对属性提升到顶层
        int multicast = attrMap.GetValueOrDefault("Multicast", 0);
        int ammoMax = attrMap.GetValueOrDefault("AmmoMax", 0);
        d["damageAmount"] = attrMap.GetValueOrDefault("DamageAmount", 0);
        d["shieldApplyAmount"] = attrMap.GetValueOrDefault("ShieldApplyAmount", 0);
        d["burnApplyAmount"] = attrMap.GetValueOrDefault("BurnApplyAmount", 0);
        d["poisonApplyAmount"] = attrMap.GetValueOrDefault("PoisonApplyAmount", 0);
        d["healAmount"] = attrMap.GetValueOrDefault("HealAmount", 0);
        d["regenApplyAmount"] = attrMap.GetValueOrDefault("RegenApplyAmount", 0);
        d["lifesteal"] = attrMap.GetValueOrDefault("Lifesteal", 0);
        d["critChance"] = attrMap.GetValueOrDefault("CritChance", 0);
        d["multicast"] = multicast;
        d["cooldown"] = attrMap.GetValueOrDefault("Cooldown", 0);
        d["cooldownMax"] = attrMap.GetValueOrDefault("CooldownMax", 0);
        d["ammo"] = attrMap.GetValueOrDefault("Ammo", 0);
        d["ammoMax"] = ammoMax;
        d["buyPrice"] = attrMap.GetValueOrDefault("BuyPrice", 0);
        d["sellPrice"] = attrMap.GetValueOrDefault("SellPrice", 0);
        d["value"] = attrMap.GetValueOrDefault("Value", 0);
        d["counter"] = attrMap.GetValueOrDefault("Counter", 0);

        d["tags"] = ReadStringEnumerable(GetMember(card, "Tags"));
        d["hiddenTags"] = ReadStringEnumerable(GetMember(card, "HiddenTags"));

        if (includeUi)
        {
            d["ui"] = ReadCardUiState(card, lookup, compact, multicast, ammoMax);
        }

        return d;
    }

    private static Dictionary<string, object> ReadCardUiState(
        object card, object lookup, bool compact, int multicast, int ammoMax)
    {
        var ui = new Dictionary<string, object>(12)
        {
            ["hasController"] = false,
        };
        if (card == null || lookup == null) return ui;

        try
        {
            var ctrl = _getCardControllerDelegate != null
                ? _getCardControllerDelegate(lookup, card)
                : lookup.GetType().GetMethod("GetCardController", AnyInstance)?.Invoke(lookup, new[] { card });

            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null))
                return ui;

            ui["hasController"] = true;
            string ctrlTypeName = ctrl.GetType().Name;
            ui["controllerType"] = ctrlTypeName;
            ui["isCardVisible"] = GetMember(ctrl, "IsCardVisible");
            if (ctrl is Component comp && comp.gameObject != null)
            {
                ui["activeInHierarchy"] = comp.gameObject.activeInHierarchy;
            }

            if (ctrlTypeName == "ItemController")
            {
                ReadItemControllerUi(ctrl, ui, compact, multicast, ammoMax);
            }
            else if (ctrlTypeName == "SkillController")
            {
                var curTier = GetMember(ctrl, "_currentTier");
                if (curTier != null) ui["uiTier"] = curTier.ToString();
            }
        }
        catch (Exception ex)
        {
            ui["inspectError"] = ex.Message;
        }

        return ui;
    }

    private static void ReadItemControllerUi(object itemCtrl, Dictionary<string, object> ui, bool compact, int multicast, int ammoMax)
    {
        // 1. 品阶视觉框（ItemVisualsController.currentTier）
        var visuals = GetMember(itemCtrl, "visualsController");
        if (visuals != null)
        {
            var curTier = GetMember(visuals, "currentTier");
            if (curTier != null) ui["uiTier"] = curTier.ToString();
        }

        // 2. 飞行与多段文本（compact 模式仅在 multicast > 1 时读取 TMP 文本）
        bool isFlying = GetMember(itemCtrl, "_isFlying") is bool bf && bf;
        ui["isFlying"] = isFlying;
        if (!compact || multicast > 1)
        {
            var multiCast = GetMember(itemCtrl, "multiCastContainer");
            if (multiCast != null)
            {
                string mcText = ReadTextMeshContainerText(multiCast);
                if (!string.IsNullOrEmpty(mcText)) ui["uiMulticastText"] = mcText;
            }
        }

        // 3. 宝石数字文本（DamageAmount / ShieldApplyAmount / BurnApplyAmount / PoisonApplyAmount / HealAmount / RegenApplyAmount）
        var gemGroup = GetMember(itemCtrl, "GemController");
        if (gemGroup != null && GetMember(gemGroup, "visibleCardGems") is IDictionary gems)
        {
            Dictionary<string, object> gemMap = compact ? null : new Dictionary<string, object>(gems.Count);
            foreach (DictionaryEntry kv in gems)
            {
                string key = kv.Key?.ToString();
                var gemCtrl = kv.Value;
                if (string.IsNullOrEmpty(key) || gemCtrl == null) continue;

                var tmpObj = GetMember(gemCtrl, "value");
                string text = tmpObj != null ? (GetMember(tmpObj, "text")?.ToString() ?? "") : "";

                if (gemMap != null)
                {
                    var curVal = GetMember(gemCtrl, "currentValue");
                    bool isActive = gemCtrl is Component gc && gc.gameObject != null && gc.gameObject.activeInHierarchy;
                    gemMap[key] = new Dictionary<string, object>
                    {
                        ["text"] = text,
                        ["currentValue"] = curVal,
                        ["active"] = isActive,
                    };
                }

                switch (key)
                {
                    case "DamageAmount": ui["uiDamageText"] = text; break;
                    case "ShieldApplyAmount": ui["uiShieldText"] = text; break;
                    case "BurnApplyAmount": ui["uiBurnText"] = text; break;
                    case "PoisonApplyAmount": ui["uiPoisonText"] = text; break;
                    case "HealAmount": ui["uiHealText"] = text; break;
                    case "RegenApplyAmount": ui["uiRegenText"] = text; break;
                }
            }
            if (gemMap != null) ui["gems"] = gemMap;
        }

        // 4. 弹药 Pip 渲染状态（compact 模式仅在存在弹药上限时读取）
        if (!compact || ammoMax > 0)
        {
            var ammoCtrl = GetMember(itemCtrl, "ammoController");
            if (ammoCtrl != null && GetMember(ammoCtrl, "ammoPips") is Array pips)
            {
                int activePips = 0;
                foreach (var pip in pips)
                {
                    if (pip is Component pc && pc.gameObject != null && pc.gameObject.activeSelf)
                        activePips++;
                }
                ui["uiActiveAmmoPips"] = activePips;
            }
        }

        // 5. 状态特效标记（仅在激活或非 compact 时读取 TMP Timer 文本）
        var vfx = GetMember(itemCtrl, "cardVFXController");
        if (vfx != null)
        {
            bool frozen = (GetMember(vfx, "isFreeze") is bool f1 && f1) || (!compact && IsGameObjectActive(GetMember(vfx, "freezeFXGO")));
            bool hasted = (GetMember(vfx, "isHaste") is bool h1 && h1) || (!compact && IsGameObjectActive(GetMember(vfx, "hasteTimer")));
            bool slowed = (GetMember(vfx, "isSlow") is bool s1 && s1) || (!compact && IsGameObjectActive(GetMember(vfx, "slowTimer")));
            bool heated = (GetMember(vfx, "isHeated") is bool ht && ht) || (!compact && IsGameObjectActive(GetMember(vfx, "heatedFXGO")));
            bool chilled = (GetMember(vfx, "isChilled") is bool ch && ch) || (!compact && IsGameObjectActive(GetMember(vfx, "chilledFXGO")));
            bool disabled = GetMember(vfx, "isDisabled") is bool dis && dis;

            var fx = new Dictionary<string, object>(10)
            {
                ["frozen"] = frozen,
                ["hasted"] = hasted,
                ["slowed"] = slowed,
                ["heated"] = heated,
                ["chilled"] = chilled,
                ["disabled"] = disabled,
                ["flying"] = isFlying,
            };

            if (!compact || frozen)
            {
                string freezeTxt = ReadTmpText(GetMember(vfx, "freezeTimer"));
                if (!string.IsNullOrEmpty(freezeTxt)) fx["freezeTimerText"] = freezeTxt;
            }
            if (!compact || hasted)
            {
                string hasteTxt = ReadTmpText(GetMember(vfx, "hasteTimer"));
                if (!string.IsNullOrEmpty(hasteTxt)) fx["hasteTimerText"] = hasteTxt;
            }
            if (!compact || slowed)
            {
                string slowTxt = ReadTmpText(GetMember(vfx, "slowTimer"));
                if (!string.IsNullOrEmpty(slowTxt)) fx["slowTimerText"] = slowTxt;
            }

            ui["effects"] = fx;
        }
    }

    private static string ResolveCardTitle(object card)
    {
        try
        {
            var tpl = GetMember(card, "Template");
            if (tpl == null)
            {
                var fn = _templateMethodGetters.GetOrAdd(card.GetType(), t => BuildZeroArgMethod(t, "GetTemplate"));
                tpl = fn(card);
            }
            if (tpl == null) return "";

            var loc = GetMember(tpl, "Localization");
            var title = GetMember(loc, "Title");
            string text = GetMember(title, "Text") as string;
            if (!string.IsNullOrWhiteSpace(text)) return text;

            return GetMember(tpl, "InternalName")?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static string ResolveEnchantment(object card)
    {
        try
        {
            var ench = GetMember(card, "Enchantment");
            if (ench != null) return ench.ToString();

            var fn = _enchantmentMethodGetters.GetOrAdd(card.GetType(), t => BuildZeroArgMethod(t, "GetEnchantment"));
            return fn(card)?.ToString();
        }
        catch { return null; }
    }

    private static List<string> ReadStringList(object enObj)
    {
        var list = new List<string>();
        if (enObj is IEnumerable en)
        {
            foreach (var item in en)
            {
                if (item == null) continue;
                string s = item.ToString();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
        }
        return list;
    }

    private static List<string> ReadStringEnumerable(object enObj)
    {
        var list = new List<string>();
        if (enObj is IEnumerable en)
        {
            foreach (var item in en)
            {
                if (item != null) list.Add(item.ToString());
            }
        }
        return list;
    }

    private static string ReadTextMeshContainerText(object container)
    {
        // TextMeshObjectContainer 原生字段名为 text（TMP_Text）
        var tmp = GetMember(container, "text")
                  ?? GetMember(container, "textMesh")
                  ?? GetMember(container, "TextMesh")
                  ?? GetMember(container, "_text");
        return ReadTmpText(tmp);
    }

    private static string ReadTmpText(object tmpObj)
    {
        if (tmpObj == null) return null;
        try
        {
            if (tmpObj is Component c && c.gameObject != null && !c.gameObject.activeInHierarchy)
                return null;
            return GetMember(tmpObj, "text")?.ToString();
        }
        catch { return null; }
    }

    private static bool IsGameObjectActive(object obj)
    {
        if (obj is GameObject go) return go != null && go.activeInHierarchy;
        if (obj is Component c) return c != null && c.gameObject != null && c.gameObject.activeInHierarchy;
        return false;
    }

    private static int SocketIndex(Dictionary<string, object> card)
    {
        string sock = card.GetValueOrDefault("socket") as string ?? "";
        if (sock.StartsWith("Socket_") && int.TryParse(sock.Substring("Socket_".Length), out int idx))
            return idx;
        return 999;
    }

    internal static bool HasCardsPendingRemoval()
    {
        EnsureHandles();
        var removal = SafeInvoke(_getDataCardRemoval);
        if (removal == null) return false;
        var fn = _hasCardsPendingRemovalGetters.GetOrAdd(removal.GetType(), t =>
        {
            var m = t.GetMethod("HasCardsPendingRemoval", AnyInstance, null, Type.EmptyTypes, null);
            if (m == null) return _ => false;
            try
            {
                var p = Expression.Parameter(typeof(object), "o");
                var call = Expression.Call(Expression.Convert(p, t), m);
                return Expression.Lambda<Func<object, bool>>(call, p).Compile();
            }
            catch
            {
                return o => m.Invoke(o, null) is bool b && b;
            }
        });
        try { return fn(removal); } catch { return false; }
    }

    private static bool IsSocketEffectCard(object card, string cardType)
    {
        if (string.Equals(cardType, "SocketEffect", StringComparison.OrdinalIgnoreCase))
            return true;
        if (_socketEffectType != null && _socketEffectType.IsInstanceOfType(card))
            return true;
        return card != null && string.Equals(card.GetType().Name, "SocketEffect", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsItemCardInstance(object card)
    {
        if (card == null) return false;
        EnsureHandles();
        if (_itemCardType != null) return _itemCardType.IsInstanceOfType(card);
        return string.Equals(card.GetType().Name, "ItemCard", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> DescribePedestalState(string currentEncounterId)
    {
        try
        {
            var cur = GetCurrentAppState();
            if (cur == null || cur.GetType().Name != "PedestalState") return null;

            var pedInfo = new Dictionary<string, object>(6);
            if (!string.IsNullOrEmpty(currentEncounterId))
                pedInfo["encounterId"] = currentEncounterId;

            var tpl = cur.GetType().GetField("_pedestalTemplate", AnyInstance)?.GetValue(cur);
            string internalName = tpl != null ? (GetMember(tpl, "InternalName")?.ToString() ?? "") : "";
            string descText = "";
            if (tpl != null)
            {
                var loc = GetMember(tpl, "Localization");
                var desc = GetMember(loc, "Description");
                descText = GetMember(desc, "Text") as string ?? "";
            }

            var behavior = tpl != null ? GetMember(tpl, "Behavior") : null;
            string behName = behavior?.GetType().Name ?? "";
            var criteria = tpl != null ? GetMember(tpl, "SelectionCriteria") : null;

            List<string> allowedTiers = null;
            string targetEnchantment = null;
            bool hasEnchantCond = false;
            ExtractPedestalCriteriaHints(criteria, ref allowedTiers, ref targetEnchantment, ref hasEnchantCond);

            if (string.IsNullOrEmpty(targetEnchantment) && behavior != null)
            {
                targetEnchantment = GetMember(behavior, "Enchantment")?.ToString();
            }

            string kind = "unknown";
            if (behName.Contains("Transform", StringComparison.OrdinalIgnoreCase))
            {
                kind = "transform";
            }
            else if (behName.Contains("Enchant", StringComparison.OrdinalIgnoreCase) ||
                     hasEnchantCond ||
                     internalName.Contains("Enchant", StringComparison.OrdinalIgnoreCase) ||
                     descText.Contains("Enchant", StringComparison.OrdinalIgnoreCase))
            {
                kind = "enchant";
            }
            else if (behName.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                     allowedTiers != null ||
                     internalName.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) ||
                     descText.Contains("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                kind = "upgrade";
            }

            pedInfo["kind"] = kind;
            if (!string.IsNullOrEmpty(internalName)) pedInfo["name"] = internalName;
            if (allowedTiers != null && allowedTiers.Count > 0) pedInfo["allowedTiers"] = allowedTiers;
            if (!string.IsNullOrEmpty(targetEnchantment)) pedInfo["targetEnchantment"] = targetEnchantment;

            if (tpl != null && criteria != null)
            {
                var validateM = cur.GetType().GetMethod("ValidateCards", AnyInstance, null, Type.EmptyTypes, null);
                validateM?.Invoke(cur, null);
                var validCardsObj = cur.GetType().GetField("_validCards", AnyInstance)?.GetValue(cur) as IEnumerable;
                if (validCardsObj != null)
                {
                    var validIds = new List<string>();
                    foreach (var vc in validCardsObj)
                    {
                        if (vc == null) continue;
                        string iid = GetMember(vc, "InstanceId")?.ToString();
                        if (!string.IsNullOrEmpty(iid)) validIds.Add(iid);
                    }
                    pedInfo["validInstanceIds"] = validIds;
                }
            }

            return pedInfo;
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractPedestalCriteriaHints(
        object criteria, ref List<string> allowedTiers, ref string targetEnchantment, ref bool hasEnchantCond)
    {
        if (criteria == null) return;
        string cname = criteria.GetType().Name;
        if (cname == "TCardConditionalTier")
        {
            bool isNot = GetMember(criteria, "IsNot") is bool b && b;
            if (!isNot && GetMember(criteria, "Tiers") is IEnumerable tiers)
            {
                allowedTiers ??= new List<string>();
                foreach (var t in tiers)
                {
                    if (t != null) allowedTiers.Add(t.ToString());
                }
            }
            return;
        }

        if (cname is "TCardConditionalHasEnchantment" or "TCardConditionalEnchantmentEligible")
        {
            hasEnchantCond = true;
            if (string.IsNullOrEmpty(targetEnchantment) && cname == "TCardConditionalHasEnchantment")
            {
                targetEnchantment = GetMember(criteria, "Enchantment")?.ToString();
            }
            return;
        }

        if (GetMember(criteria, "Conditions") is IEnumerable children)
        {
            foreach (var child in children)
            {
                ExtractPedestalCriteriaHints(child, ref allowedTiers, ref targetEnchantment, ref hasEnchantCond);
            }
        }
    }

    internal static Type FindTypeSafe(params string[] fullNames)
    {
        if (fullNames == null || fullNames.Length == 0) return null;
        foreach (string name in fullNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (_safeTypeCache.TryGetValue(name, out var cached) && cached != null)
                return cached;

            var resolved = ResolveTypeAcrossAssemblies(name);
            if (resolved != null)
            {
                _safeTypeCache[name] = resolved;
                return resolved;
            }
        }
        return null;
    }

    private static Type ResolveTypeAcrossAssemblies(string fullName)
    {
        var direct = Type.GetType(fullName, throwOnError: false);
        if (direct != null) return direct;

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null) return t;
            }
            catch { }
        }
        return null;
    }

    // ── 高速委托编译工具 ──────────────────────────────────────────────────────

    internal static object GetMember(object obj, string name)
    {
        if (obj == null || string.IsNullOrEmpty(name)) return null;
        try
        {
            var getter = _memberGetters.GetOrAdd((obj.GetType(), name), BuildMemberGetter);
            return getter(obj);
        }
        catch { return null; }
    }

    private static Func<object, object> BuildMemberGetter((Type type, string name) key)
    {
        for (var cur = key.type; cur != null; cur = cur.BaseType)
        {
            var p = cur.GetProperty(key.name, AnyDeclared);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetMethod != null)
            {
                try
                {
                    var param = Expression.Parameter(typeof(object), "obj");
                    Expression access = p.GetMethod.IsStatic
                        ? Expression.Property(null, p)
                        : Expression.Property(Expression.Convert(param, cur), p);
                    return Expression.Lambda<Func<object, object>>(Expression.Convert(access, typeof(object)), param).Compile();
                }
                catch
                {
                    var prop = p;
                    return o => prop.GetValue(o);
                }
            }

            var f = cur.GetField(key.name, AnyDeclared);
            if (f != null)
            {
                try
                {
                    var param = Expression.Parameter(typeof(object), "obj");
                    Expression access = f.IsStatic
                        ? Expression.Field(null, f)
                        : Expression.Field(Expression.Convert(param, cur), f);
                    return Expression.Lambda<Func<object, object>>(Expression.Convert(access, typeof(object)), param).Compile();
                }
                catch
                {
                    var fld = f;
                    return o => fld.GetValue(o);
                }
            }
        }
        return static _ => null;
    }

    private static Func<object, object> BuildZeroArgMethod(Type type, string methodName)
    {
        var m = type.GetMethod(methodName, AnyInstance, null, Type.EmptyTypes, null);
        if (m == null || m.ReturnType == typeof(void)) return static _ => null;
        try
        {
            var param = Expression.Parameter(typeof(object), "obj");
            var call = Expression.Call(Expression.Convert(param, m.DeclaringType ?? type), m);
            return Expression.Lambda<Func<object, object>>(Expression.Convert(call, typeof(object)), param).Compile();
        }
        catch
        {
            return o => m.Invoke(o, null);
        }
    }

    private static Func<object> CompileStaticGetter(Type type, string memberName)
    {
        var p = type.GetProperty(memberName, AnyStatic);
        if (p != null && p.GetMethod != null)
        {
            try
            {
                var access = Expression.Convert(Expression.Property(null, p), typeof(object));
                return Expression.Lambda<Func<object>>(access).Compile();
            }
            catch { return () => p.GetValue(null); }
        }
        var f = type.GetField(memberName, AnyStatic);
        if (f != null)
        {
            try
            {
                var access = Expression.Convert(Expression.Field(null, f), typeof(object));
                return Expression.Lambda<Func<object>>(access).Compile();
            }
            catch { return () => f.GetValue(null); }
        }
        return static () => null;
    }

    private static Func<object> CompileStaticGetterIncludingBase(Type type, string memberName)
    {
        for (var cur = type; cur != null; cur = cur.BaseType)
        {
            var getter = CompileStaticGetter(cur, memberName);
            var p = cur.GetProperty(memberName, AnyStatic | BindingFlags.DeclaredOnly);
            var f = cur.GetField(memberName, AnyStatic | BindingFlags.DeclaredOnly);
            if (p != null || f != null) return getter;
        }
        return static () => null;
    }

    private static object SafeInvoke(Func<object> fn)
    {
        if (fn == null) return null;
        try { return fn(); } catch { return null; }
    }
}

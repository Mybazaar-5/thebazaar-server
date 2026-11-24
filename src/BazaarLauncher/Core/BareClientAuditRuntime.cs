using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Cards;
using BazaarGameShared.Domain.Core;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.GameSimEvents;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BazaarLauncher.Core;

/// <summary>
/// Wave 4C（任务 #12）：裸协议黑匣子 Unity 运行时只读观测层。
/// 铁律：
///   1. 仅在 StrictBareClientAuditMode == true 时激活并安装只读 Harmony 钩子与 0.5s 心跳组件；
///   2. 严格只读——绝不修改任何参数、返回值、容器槽位、翻牌状态或吞掉任何游戏异常；
///   3. 默认模式（StrictBareClientAuditMode == false）下完全不安装、零文件句柄、零主线程回调开销。
/// </summary>
internal static class BareClientAuditRuntime
{
    private static readonly object LifecycleLock = new();
    private static volatile BareClientAuditEngine _engine;
    private static bool _active;
    private static bool _logHooked;
    private static GameObject _watchdogGo;

    // 反射缓存（仅在严格审计开启时初始化，零热路径查找）
    private static Type _appStateType;
    private static PropertyInfo _appBlockInputProp;
    private static PropertyInfo _appWaitingServerProp;
    private static PropertyInfo _appSimProcessingProp;
    private static FieldInfo _cmdIsProcessingField;
    private static PropertyInfo _cmdIsProcessingProp;
    private static PropertyInfo _dataHoursInADayProp;
    private static PropertyInfo _dataTransitionInProp;
    private static PropertyInfo _dataNewDayCtrlProp;
    private static PropertyInfo _dataPlayerSpmProp;
    private static FieldInfo _tiWaitForNewDayField;
    private static PropertyInfo _newDayIsActiveProp;
    private static PropertyInfo _newDayHasPendingProp;
    private static PropertyInfo _spmIsUpdatingProp;
    private static FieldInfo _bmCardsBeingDealtField;
    private static FieldInfo _bmCardsBeingSpawnedField;
    private static FieldInfo _bmIsLoadingCollectiblesField;
    private static FieldInfo _bmIsRevealingField;
    private static PropertyInfo _bmIsRevealingProp;
    private static MethodInfo _bmHasCardsToRevealMethod;
    private static PropertyInfo _bmIsProcessingUpgradeProp;
    private static PropertyInfo _bmDialogOpenProp;
    private static FieldInfo _bmBoardTransitionField;
    private static PropertyInfo _btcIsTransitioningProp;

    private sealed class GameContextSnapshot
    {
        public string Scene = string.Empty;
        public string AppState = string.Empty;
        public string RunState = string.Empty;
        public int Day;
        public int Hour;
    }

    // 不可变上下文快照，任意线程读取零撕裂
    private static volatile GameContextSnapshot _ctx = new();
    private static MethodInfo _dataGetStaticMethod;
    private static MethodInfo _staticGetCardByIdMethod;

    internal static bool IsActive => _active && _engine != null && _engine.Enabled;
    internal static string ActiveLogPath => _engine?.JsonlFilePath ?? string.Empty;

    internal static string ResolveDefaultJsonlPath()
    {
        string root = null;
        try { root = BepInEx.Paths.GameRootPath; } catch { }
        if (string.IsNullOrWhiteSpace(root))
        {
            root = AppDomain.CurrentDomain.BaseDirectory;
        }
        return Path.Combine(root, "logs", "bare_client_audit.jsonl");
    }

    internal static void Start(SafeHarmonyPatcher patcher)
    {
        if (patcher == null) return;
        lock (LifecycleLock)
        {
            if (_active) return;

            string logPath = ResolveDefaultJsonlPath();
            _engine = new BareClientAuditEngine(
                enabled: true,
                jsonlFilePath: logPath,
                ringBufferCapacity: 48,
                dedupeWindowMs: 5000L,
                maxRecordsPer10Seconds: 40,
                deadlockThresholdMs: 10_000L);
            _active = true;

            InitReflectionHandles();
            InstallReadOnlyHooks(patcher);

            if (!_logHooked)
            {
                Application.logMessageReceived += OnUnityLogMessageReceived;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                _logHooked = true;
            }

            EnsureWatchdogInstalled();
            RefreshCachedGameContext();
            _engine.RecordTrace("state", "AuditStarted", "StrictBareClientAuditMode enabled -> " + logPath);

            BazaarLauncherPlugin.Log?.LogWarning(
                $"[BareClientAudit] 裸协议黑匣子观测模式已启用！已禁用 CompatAuditPatch/H-7 自愈干预，异常将异步写入: {logPath}");
        }
    }

    internal static void Shutdown()
    {
        BareClientAuditEngine toDispose;
        lock (LifecycleLock)
        {
            _active = false;
            toDispose = _engine;
            _engine = null;

            if (_logHooked)
            {
                try { Application.logMessageReceived -= OnUnityLogMessageReceived; } catch { }
                try { SceneManager.activeSceneChanged -= OnActiveSceneChanged; } catch { }
                _logHooked = false;
            }

            if (_watchdogGo != null)
            {
                try { UnityEngine.Object.Destroy(_watchdogGo); } catch { }
                _watchdogGo = null;
            }
        }

        if (toDispose != null)
        {
            try { toDispose.Flush(1500); } catch { }
            try { toDispose.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 供 CardTextureCache / CustomContentVisualModule 在卡图加载失败路径调用（默认模式 0 开销短路）。
    /// </summary>
    internal static void NotifyCardArtLoadFailure(string source, string assetPath, string reason, string cardId = null)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;

        try
        {
            var snap = _ctx;
            eng.RecordTrace("visual", "CardArtLoadFailure", $"{source}:{reason}:{cardId ?? assetPath}");
            eng.RecordCardArtLoadFailure(
                source,
                assetPath,
                reason,
                cardId,
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour);
        }
        catch { }
    }

    private static void InitReflectionHandles()
    {
        const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags anyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        _appStateType = GameInspector.FindTypeSafe("TheBazaar.AppState");
        if (_appStateType != null)
        {
            _appBlockInputProp = _appStateType.GetProperty("BlockInput", anyStatic);
            _appWaitingServerProp = _appStateType.GetProperty("IsWaitingForServerResponse", anyStatic);
            _appSimProcessingProp = _appStateType.GetProperty("SimHandlersProcessing", anyStatic);
        }

        var cmdType = GameInspector.FindTypeSafe("TheBazaar.Cmd");
        if (cmdType != null)
        {
            _cmdIsProcessingField = cmdType.GetField("_isProcessing", anyInst);
            _cmdIsProcessingProp = cmdType.GetProperty("IsProcessing", anyInst);
        }

        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
        if (dataType != null)
        {
            _dataHoursInADayProp = dataType.GetProperty("HoursInADay", anyStatic);
            _dataTransitionInProp = dataType.GetProperty("TransitionIn", anyStatic);
            _dataNewDayCtrlProp = dataType.GetProperty("NewDayTransitionController", anyStatic);
            _dataPlayerSpmProp = dataType.GetProperty("PlayerSkillPresentationManager", anyStatic);
            _dataGetStaticMethod = dataType.GetMethod("GetStatic", anyStatic, null, Type.EmptyTypes, null);
        }

        var tiType = GameInspector.FindTypeSafe("TheBazaar.SequenceFramework.VisualSequences.TransitionInComponent");
        if (tiType != null)
        {
            _tiWaitForNewDayField = tiType.GetField("_waitForNewDayTransition", anyInst);
        }

        var spmType = GameInspector.FindTypeSafe("TheBazaar.SkillPresentationManager", "SkillPresentationManager");
        if (spmType != null)
        {
            _spmIsUpdatingProp = spmType.GetProperty("IsUpdatingSkillBoard", anyInst);
        }

        var bmType = GameInspector.FindTypeSafe("BoardManager");
        if (bmType != null)
        {
            _bmCardsBeingDealtField = bmType.GetField("_cardsBeingDealt", anyInst);
            _bmCardsBeingSpawnedField = bmType.GetField("_cardsBeingSpawned", anyInst);
            _bmIsLoadingCollectiblesField = bmType.GetField("_isLoadingCollectibles", anyInst);
            _bmIsRevealingField = bmType.GetField("_isRevealing", anyInst);
            _bmIsRevealingProp = bmType.GetProperty("IsRevealing", anyInst);
            _bmHasCardsToRevealMethod = bmType.GetMethod("HasCardsToReveal", anyInst, null, Type.EmptyTypes, null);
            _bmIsProcessingUpgradeProp = bmType.GetProperty("IsProcessingUpgradeOrFuseOrEnchant", anyInst);
            _bmDialogOpenProp = bmType.GetProperty("DialogOpen", anyInst);
            _bmBoardTransitionField = bmType.GetField("_boardTransition", anyInst);
        }

        var btcType = GameInspector.FindTypeSafe("TheBazaar.BoardTransitionController", "BoardTransitionController");
        if (btcType != null)
        {
            _btcIsTransitioningProp = btcType.GetProperty("IsTransitioning", anyInst);
        }
    }

    private static void InstallReadOnlyHooks(SafeHarmonyPatcher patcher)
    {
        // 1. 客户端命令链路只读采集 + 购买前货架属性抓拍
        var cmdType = GameInspector.FindTypeSafe("TheBazaar.Cmd");
        if (cmdType != null)
        {
            var cmdPrefix = AccessTools.Method(typeof(BareClientAuditRuntime), nameof(CmdActionReadOnlyPrefix));
            string[] cmdMethods =
            {
                "SelectItem",
                "SendSelectItem",
                "SelectEncounter",
                "SendMoveItem",
                "SendSelectSkill",
                "SendReRollSelection",
                "SendExitCurrentState",
                "SendSellCard",
                "SendCommitToPedestal",
                "SendAbandonRun",
            };
            foreach (string name in cmdMethods)
            {
                var m = AccessTools.Method(cmdType, name);
                if (m != null)
                {
                    patcher.TryPatchMethod(m, prefix: cmdPrefix, optional: true, label: "BareAudit.Cmd." + name);
                }
            }
        }

        // 2. 服务端 GameSimHandler 只读观测：环形事件链、同包协议不变量、货架发牌快照与购入后属性比对
        var gshType = GameInspector.FindTypeSafe("TheBazaar.GameSimHandler");
        if (gshType != null)
        {
            var handleMsg = AccessTools.Method(gshType, "HandleMessage", new[] { typeof(NetMessageGameSim) });
            if (handleMsg != null)
            {
                patcher.TryPatchMethod(
                    handleMsg,
                    prefix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(GameSimHandleMessagePrefix)),
                    postfix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(GameSimHandleMessagePostfix)),
                    optional: true,
                    label: "BareAudit.GameSimHandler.HandleMessage");
            }

            var procDealt = AccessTools.Method(gshType, "ProcessCardDealtEvents");
            if (procDealt != null)
            {
                patcher.TryPatchMethod(
                    procDealt,
                    postfix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(GameSimProcessCardDealtPostfix)),
                    optional: true,
                    label: "BareAudit.GameSimHandler.ProcessCardDealtEvents");
            }
        }

        // 3. 服务端 CombatSimHandler 只读观测
        var cshType = GameInspector.FindTypeSafe("TheBazaar.CombatSimHandler");
        if (cshType != null)
        {
            var handleCombat = AccessTools.Method(cshType, "HandleMessage", new[] { typeof(NetMessageCombatSim) });
            if (handleCombat != null)
            {
                patcher.TryPatchMethod(
                    handleCombat,
                    prefix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(CombatSimHandleMessagePrefix)),
                    optional: true,
                    label: "BareAudit.CombatSimHandler.HandleMessage");
            }
        }

        // 4. BoardManager.PlayRevealAnimations 只读前置信号观测（即使关闭日志也能捕获 skipped 条件）
        var bmType = GameInspector.FindTypeSafe("BoardManager");
        if (bmType != null)
        {
            var playReveal = AccessTools.Method(bmType, "PlayRevealAnimations", Type.EmptyTypes);
            if (playReveal != null)
            {
                patcher.TryPatchMethod(
                    playReveal,
                    prefix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(BoardManagerPlayRevealReadOnlyPrefix)),
                    optional: true,
                    label: "BareAudit.BoardManager.PlayRevealAnimations");
            }
        }

        // 5. Data.ResetRunData 跨局清理（只清黑匣子自身缓存，不修改游戏状态）
        var dataType = GameInspector.FindTypeSafe("TheBazaar.Data");
        var resetRunData = dataType == null ? null : AccessTools.Method(dataType, "ResetRunData", Type.EmptyTypes);
        if (resetRunData != null)
        {
            patcher.TryPatchMethod(
                resetRunData,
                postfix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(DataResetRunDataPostfix)),
                optional: true,
                label: "BareAudit.Data.ResetRunData");
        }

        // 6. Networking.NetworkManager 命令服务端报错/失败只读观测（捕获 NetMessageError 与网络异常）
        var netMgrType = GameInspector.FindTypeSafe("Networking.NetworkManager");
        if (netMgrType != null)
        {
            var errMsgM = AccessTools.Method(netMgrType, "HandleCommandErrorMessage", new[] { typeof(NetMessageError) });
            if (errMsgM != null)
            {
                patcher.TryPatchMethod(
                    errMsgM,
                    prefix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(NetworkManagerCommandErrorPrefix)),
                    optional: true,
                    label: "BareAudit.NetworkManager.HandleCommandErrorMessage");
            }

            var failM = AccessTools.Method(netMgrType, "HandleCommandFailure", new[] { typeof(Exception) });
            if (failM != null)
            {
                patcher.TryPatchMethod(
                    failM,
                    prefix: AccessTools.Method(typeof(BareClientAuditRuntime), nameof(NetworkManagerCommandFailurePrefix)),
                    optional: true,
                    label: "BareAudit.NetworkManager.HandleCommandFailure");
            }
        }
    }

    private static void EnsureWatchdogInstalled()
    {
        try
        {
            if (_watchdogGo != null) return;
            _watchdogGo = new GameObject("BazaarLauncher.BareClientAuditWatchdog")
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(_watchdogGo);
            _watchdogGo.AddComponent<BareAuditWatchdogBehaviour>();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[BareClientAudit] 创建锁死观测组件失败: " + ex.Message);
        }
    }

    private static void OnActiveSceneChanged(Scene prev, Scene next)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;
        try
        {
            string nextName = next.name ?? string.Empty;
            var prevSnap = _ctx;
            _ctx = new GameContextSnapshot
            {
                Scene = nextName,
                AppState = prevSnap.AppState,
                RunState = prevSnap.RunState,
                Day = prevSnap.Day,
                Hour = prevSnap.Hour,
            };
            eng.RecordTrace("state", "SceneChanged", $"{prev.name} -> {nextName}");
            if (GameInspector.IsMainMenuSceneName(nextName))
            {
                eng.ResetRunScopedState();
            }
        }
        catch { }
    }

    private static void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;
        try
        {
            var snap = _ctx;
            eng.RecordUnityLog(
                type.ToString(),
                condition,
                stackTrace,
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour);
        }
        catch { }
    }

    private static void NetworkManagerCommandErrorPrefix(NetMessageError message)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || message == null) return;
        try
        {
            RefreshCachedGameContext();
            var snap = _ctx;
            string errSummary = $"NetMessageError(id={message.MessageId}, msg={message.Message})";
            eng.RecordTrace("server_msg", "NetMessageError", errSummary);
            eng.RecordProtocolSignal(
                BareAuditCategory.ProtocolAnomaly,
                "error",
                "net_message_error",
                errSummary,
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour,
                new Dictionary<string, object>
                {
                    ["messageId"] = message.MessageId ?? string.Empty,
                    ["errorMessage"] = message.Message ?? string.Empty,
                });
        }
        catch { }
    }

    private static void NetworkManagerCommandFailurePrefix(Exception error)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || error == null) return;
        try
        {
            RefreshCachedGameContext();
            var snap = _ctx;
            string errSummary = $"CommandFailure({error.GetType().Name}): {error.Message}";
            eng.RecordTrace("server_msg", "CommandFailure", errSummary);
            eng.RecordProtocolSignal(
                BareAuditCategory.ProtocolAnomaly,
                "error",
                "command_failure",
                errSummary,
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour,
                new Dictionary<string, object>
                {
                    ["exceptionType"] = error.GetType().FullName ?? error.GetType().Name,
                    ["exceptionMessage"] = error.Message ?? string.Empty,
                });
        }
        catch { }
    }

    private static void DataResetRunDataPostfix()
    {
        try
        {
            _engine?.RecordTrace("state", "ResetRunData", "run cleared");
            _engine?.ResetRunScopedState();
        }
        catch { }
    }

    // ── 只读 Harmony 钩子回调 ────────────────────────────────────────────────

    private static void CmdActionReadOnlyPrefix(MethodBase __originalMethod, object[] __args)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;

        try
        {
            RefreshCachedGameContext();
            string methodName = __originalMethod?.Name ?? "Cmd";
            string argSummary = FormatCmdArgsSummary(__args);
            eng.RecordTrace("client_cmd", methodName, argSummary);

            // 购买货架卡前立刻刷新该卡在货架展示的权威属性快照
            if ((methodName == "SelectItem" || methodName == "SendSelectItem") && __args != null && __args.Length > 0)
            {
                TryCaptureShelfCardFromArg(__args[0]);
            }
        }
        catch { }
    }

    private static void GameSimHandleMessagePrefix(NetMessageGameSim message, out List<string> __state)
    {
        __state = null;
        var eng = _engine;
        if (eng == null || !eng.Enabled || message?.Data == null) return;

        try
        {
            RefreshCachedGameContext();
            var snap = _ctx;
            var sim = message.Data;
            var events = sim.Events;
            string prevRunState = snap.RunState;
            string nextRunState = sim.CurrentState?.StateName.ToString() ?? prevRunState;

            var summarySb = new StringBuilder(128);
            summarySb.Append("msgId=").Append(message.MessageId ?? "").Append(" state=").Append(nextRunState);

            var batch = new BareAuditGameSimBatchInput
            {
                MessageId = message.MessageId ?? string.Empty,
                PreviousRunState = prevRunState,
                ResultingRunState = nextRunState,
                ResultingHour = snap.Hour,
                HoursInADay = GetHoursInADaySafe(),
                ExistingPlayerHandMask = ReadCurrentPlayerHandUnlockedMask(),
            };

            // 只读收集当前已有实体 ID
            var entities = GameInspector.GetDataEntities();
            if (entities != null)
            {
                foreach (DictionaryEntry kv in entities)
                {
                    if (kv.Key != null) batch.ExistingEntityIds.Add(kv.Key.ToString());
                }
            }

            if (sim.Cards != null)
            {
                foreach (string key in sim.Cards.Keys)
                {
                    if (!string.IsNullOrEmpty(key)) batch.SimUpdateCardKeys.Add(key);
                }
            }

            if (events != null && events.Count > 0)
            {
                summarySb.Append(" events=[");
                int shown = 0;
                for (int i = 0; i < events.Count; i++)
                {
                    var ev = events[i];
                    if (ev == null) continue;
                    if (shown < 8)
                    {
                        if (shown > 0) summarySb.Append(',');
                        summarySb.Append(ev.GetType().Name.Replace("GameSimEvent", ""));
                    }
                    shown++;

                    switch (ev)
                    {
                        case GameSimEventStateTransitioned st:
                            batch.ResultingRunState = st.ToState.ToString();
                            if (st.ToState != ERunState.Choice && st.ToState != ERunState.TournamentPvpChoice)
                            {
                                batch.HasLeftChoiceInBatch = true;
                            }
                            break;
                        case GameSimEventRunHourChanged hr:
                            batch.ResultingHour = (int)hr.NewHour;
                            break;
                        case GameSimEventCardDealt:
                            batch.HasCardDealt = true;
                            break;
                        case GameSimEventCardSpawned sp:
                            if (!string.IsNullOrEmpty(sp.InstanceId))
                                batch.SpawnedCardInstanceIds.Add(sp.InstanceId);
                            if (sp.CombatantId == ECombatantId.Player && sp.Section == EInventorySection.Hand && sp.Socket.HasValue)
                            {
                                int leftSock = (int)sp.Socket.Value;
                                int width = ResolveCardSlotWidthSafe(sp.TemplateId);
                                int endSock = Math.Min(10, leftSock + width);
                                for (int s = Math.Max(0, leftSock); s < endSock; s++)
                                {
                                    batch.SpawnedPlayerHandSockets.Add(s);
                                }
                            }
                            break;
                        case GameSimEventSocketsUnlocked su:
                            if (su.Section == EInventorySection.Hand)
                                batch.NewlyUnlockedPlayerHandMask |= su.UnlockedSocketsBitmask;
                            break;
                        case GameSimEventPlayerSkillEquipped eq:
                            if (!string.IsNullOrEmpty(eq.InstanceId))
                                batch.EquippedSkillInstanceIds.Add(eq.InstanceId);
                            break;
                        case GameSimEventCardTransformed tr:
                            if (tr.TransformedCards != null)
                            {
                                foreach (var tc in tr.TransformedCards)
                                {
                                    if (!string.IsNullOrEmpty(tc?.InstanceId))
                                        batch.TransformReferencedCardIds.Add(tc.InstanceId);
                                }
                            }
                            break;
                        case GameSimEventCardTransformReverted rev:
                            if (!string.IsNullOrEmpty(rev.OriginalCard?.InstanceId))
                                batch.TransformReferencedCardIds.Add(rev.OriginalCard.InstanceId);
                            break;
                        case GameSimEventCardMoved mv:
                            if (!string.IsNullOrEmpty(mv.InstanceId))
                                batch.MovedCardInstanceIds.Add(mv.InstanceId);
                            break;
                        case GameSimEventCardPurchased pur:
                            if (!string.IsNullOrEmpty(pur.InstanceId))
                            {
                                __state ??= new List<string>(2);
                                __state.Add(pur.InstanceId);
                            }
                            break;
                    }
                }
                if (shown > 8) summarySb.Append(",+").Append(shown - 8);
                summarySb.Append(']');
            }

            eng.RecordTrace("server_msg", "NetMessageGameSim", summarySb.ToString());
            eng.InspectGameSimBatch(batch, snap.Scene, snap.AppState, batch.ResultingRunState, snap.Day, batch.ResultingHour);
        }
        catch { }
    }

    private static void GameSimHandleMessagePostfix(List<string> __state, Task __result)
    {
        if (__state == null || __state.Count == 0) return;
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;

        try
        {
            if (__result == null || __result.IsCompleted)
            {
                VerifyPurchasedCardsOnMain(__state);
                return;
            }

            var captured = __state;
            __result.ContinueWith(_ =>
            {
                MainThreadDispatcher.Enqueue(() => VerifyPurchasedCardsOnMain(captured));
            }, TaskScheduler.Default);
        }
        catch { }
    }

    private static void GameSimProcessCardDealtPostfix(IEnumerable<GameSimEventCardDealt> events)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || events == null) return;

        try
        {
            var entities = GameInspector.GetDataEntities();
            if (entities == null) return;

            foreach (var ev in events)
            {
                if (ev == null || string.IsNullOrEmpty(ev.InstanceId)) continue;
                var iid = new InstanceId(ev.InstanceId);
                if (!entities.Contains(iid)) continue;
                var cardObj = entities[iid];
                CaptureShelfCardObject(cardObj, ev.InstanceId);
            }
        }
        catch { }
    }

    private static void CombatSimHandleMessagePrefix(NetMessageCombatSim message)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || message?.Data == null) return;

        try
        {
            RefreshCachedGameContext();
            int frames = message.Data.Frames?.Count ?? 0;
            eng.RecordTrace("server_msg", "NetMessageCombatSim", $"msgId={message.MessageId} winner={message.Data.Winner} frames={frames}");
        }
        catch { }
    }

    private static void BoardManagerPlayRevealReadOnlyPrefix(object __instance)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || __instance == null) return;

        try
        {
            bool dealt = _bmCardsBeingDealtField?.GetValue(__instance) is bool d && d;
            bool spawned = _bmCardsBeingSpawnedField?.GetValue(__instance) is bool s && s;
            bool collectibles = _bmIsLoadingCollectiblesField?.GetValue(__instance) is bool c && c;
            bool revealing = _bmIsRevealingField?.GetValue(__instance) is bool rf
                ? rf
                : (_bmIsRevealingProp?.GetValue(__instance) is bool rp && rp);

            if (!(dealt || spawned || collectibles || revealing)) return;

            bool hasToReveal = _bmHasCardsToRevealMethod?.Invoke(__instance, null) is bool hr && hr;
            if (!hasToReveal) return;

            RefreshCachedGameContext();
            var snap = _ctx;
            var ctx = new Dictionary<string, object>
            {
                ["dealt"] = dealt,
                ["spawned"] = spawned,
                ["collectibles"] = collectibles,
                ["revealing"] = revealing,
                ["hasCardsToReveal"] = true,
            };
            eng.RecordProtocolSignal(
                BareAuditCategory.ProtocolRevealSkipped,
                "critical",
                "play_reveal_animations_skipped_direct",
                $"PlayRevealAnimations skipped (dealt={dealt}, spawned={spawned}, collectibles={collectibles}, revealing={revealing})",
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour,
                ctx);
        }
        catch { }
    }

    // ── 货架购入属性快照与比对辅助 ────────────────────────────────────────────

    private static void TryCaptureShelfCardFromArg(object arg0)
    {
        if (arg0 == null) return;
        try
        {
            if (arg0 is InstanceId iid)
            {
                var entities = GameInspector.GetDataEntities();
                if (entities != null && entities.Contains(iid))
                {
                    CaptureShelfCardObject(entities[iid], iid.Value);
                }
                return;
            }

            object iidObj = GameInspector.GetMember(arg0, "InstanceId");
            string idStr = iidObj?.ToString();
            if (!string.IsNullOrEmpty(idStr))
            {
                CaptureShelfCardObject(arg0, idStr);
            }
        }
        catch { }
    }

    private static void CaptureShelfCardObject(object cardObj, string instanceId)
    {
        var eng = _engine;
        if (eng == null || cardObj == null || string.IsNullOrEmpty(instanceId)) return;

        // 仅记录待购的货架物品（排除已属于玩家或非 Item 类型如 Encounter）
        if (GameInspector.GetMember(cardObj, "Type") is ECardType ct && ct != ECardType.Item)
            return;
        if (GameInspector.GetMember(cardObj, "Owner") != null)
            return;

        var attrs = ReadCardAttributes(cardObj);
        if (attrs == null || attrs.Count == 0) return;

        string tplId = GameInspector.GetMember(cardObj, "TemplateId")?.ToString() ?? string.Empty;
        string title = GameInspector.ResolveCardTitlePublic(cardObj) ?? tplId;
        eng.CaptureShopItemBeforePurchase(instanceId, tplId, title, attrs);
    }

    private static void VerifyPurchasedCardsOnMain(List<string> purchasedIds)
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled || purchasedIds == null) return;

        try
        {
            RefreshCachedGameContext();
            var snap = _ctx;
            var entities = GameInspector.GetDataEntities();
            if (entities == null) return;

            for (int i = 0; i < purchasedIds.Count; i++)
            {
                string id = purchasedIds[i];
                if (string.IsNullOrEmpty(id)) continue;
                var iid = new InstanceId(id);
                if (!entities.Contains(iid)) continue;

                var cardObj = entities[iid];
                var afterAttrs = ReadCardAttributes(cardObj);
                if (afterAttrs == null) continue;

                eng.VerifyShopItemAfterPurchase(
                    id,
                    afterAttrs,
                    snap.Scene,
                    snap.AppState,
                    snap.RunState,
                    snap.Day,
                    snap.Hour);
            }
        }
        catch { }
    }

    private static Dictionary<string, int> ReadCardAttributes(object cardObj)
    {
        if (cardObj == null) return null;
        if (GameInspector.GetMember(cardObj, "Attributes") is not IDictionary rawDict)
            return null;

        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry kv in rawDict)
        {
            if (kv.Key == null || kv.Value == null) continue;
            try
            {
                result[kv.Key.ToString()] = Convert.ToInt32(kv.Value);
            }
            catch { }
        }
        return result;
    }

    // ── 主线程状态采样与 >10s 锁死巡检 ───────────────────────────────────────

    internal static void SampleDeadlockTickOnMain()
    {
        var eng = _engine;
        if (eng == null || !eng.Enabled) return;

        try
        {
            RefreshCachedGameContext();
            var snap = _ctx;
            bool isInRun = GameInspector.IsActiveInRunState(snap.Scene, snap.AppState);
            bool isCombatPlayback = snap.AppState is "CombatState" or "PVPCombatState"
                || snap.RunState is "Combat" or "PVPCombat";

            bool blockInput = !isCombatPlayback && (_appBlockInputProp?.GetValue(null) is bool bi && bi);
            bool waitingServer = _appWaitingServerProp?.GetValue(null) is bool ws && ws;
            bool simProc = _appSimProcessingProp?.GetValue(null) is bool sp && sp;

            bool cmdProc = false;
            var cmd = GameInspector.GetCmdCached();
            if (cmd != null)
            {
                cmdProc = _cmdIsProcessingField?.GetValue(cmd) is bool cf
                    ? cf
                    : (_cmdIsProcessingProp?.GetValue(cmd) is bool cp && cp);
            }

            bool isRevealing = false;
            bool hasCardsToReveal = false;
            bool isBoardTransitioning = false;
            bool isProcessingUpgrade = false;
            bool dialogOpen = false;

            var bm = GameInspector.GetBoardManagerCached();
            if (bm != null)
            {
                isRevealing = _bmIsRevealingField?.GetValue(bm) is bool rf
                    ? rf
                    : (_bmIsRevealingProp?.GetValue(bm) is bool rp && rp);
                hasCardsToReveal = !isCombatPlayback && (_bmHasCardsToRevealMethod?.Invoke(bm, null) is bool hr && hr);
                isProcessingUpgrade = _bmIsProcessingUpgradeProp?.GetValue(bm) is bool pu && pu;
                dialogOpen = !isCombatPlayback && (_bmDialogOpenProp?.GetValue(bm) is bool dlg && dlg);

                var btc = _bmBoardTransitionField?.GetValue(bm);
                if (btc != null && !(btc is UnityEngine.Object buo && buo == null))
                {
                    isBoardTransitioning = _btcIsTransitioningProp?.GetValue(btc) is bool bt && bt;
                }
            }

            bool isUpdatingSkillBoard = false;
            var playerSpm = _dataPlayerSpmProp?.GetValue(null);
            if (playerSpm != null && !(playerSpm is UnityEngine.Object suo && suo == null))
            {
                isUpdatingSkillBoard = _spmIsUpdatingProp?.GetValue(playerSpm) is bool us && us;
            }

            bool waitForNewDay = false;
            var ti = _dataTransitionInProp?.GetValue(null);
            if (ti != null && !(ti is UnityEngine.Object tuo && tuo == null) && _tiWaitForNewDayField != null)
            {
                waitForNewDay = _tiWaitForNewDayField.GetValue(ti) is bool wnd && wnd;
            }

            bool newDayActiveOrPending = IsNewDayTransitionActiveOrPending();
            bool hour6Lock = BareAuditGameSimInvariantChecker.IsHour6WaitNewDayRevealLock(
                snap.RunState,
                snap.Hour,
                GetHoursInADaySafe(),
                waitForNewDay,
                newDayActiveOrPending,
                hasCardsToReveal);

            eng.SampleDeadlockState(
                isInRun,
                snap.Scene,
                snap.AppState,
                snap.RunState,
                snap.Day,
                snap.Hour,
                blockInput,
                waitingServer,
                simProc,
                cmdProc,
                isRevealing,
                hasCardsToReveal,
                isBoardTransitioning,
                isProcessingUpgrade,
                dialogOpen,
                isUpdatingSkillBoard,
                hour6Lock);
        }
        catch { }
    }

    private static void RefreshCachedGameContext()
    {
        try
        {
            string scene = GameInspector.GetActiveSceneName();
            string appState = GameInspector.GetAppStateName();

            var dataState = GameInspector.GetDataCurrentState();
            string runState = GameInspector.GetMember(dataState, "StateName")?.ToString() ?? string.Empty;

            int day = 0;
            int hour = 0;
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                object dObj = GameInspector.GetMember(run, "Day");
                object hObj = GameInspector.GetMember(run, "Hour");
                day = dObj != null ? Convert.ToInt32(dObj) : 0;
                hour = hObj != null ? Convert.ToInt32(hObj) : 0;
            }

            _ctx = new GameContextSnapshot
            {
                Scene = scene ?? string.Empty,
                AppState = appState ?? string.Empty,
                RunState = runState,
                Day = day,
                Hour = hour,
            };
        }
        catch { }
    }

    private static int ResolveCardSlotWidthSafe(string templateId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(templateId) || !Guid.TryParse(templateId, out Guid tplGuid))
                return 1;
            var staticMgr = _dataGetStaticMethod?.Invoke(null, null);
            if (staticMgr == null) return 1;
            _staticGetCardByIdMethod ??= staticMgr.GetType().GetMethod(
                "GetCardById",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(Guid) },
                null);
            if (_staticGetCardByIdMethod?.Invoke(staticMgr, new object[] { tplGuid }) is ITCard tpl)
            {
                int sz = (int)tpl.Size;
                if (sz >= 1 && sz <= 3) return sz;
            }
        }
        catch { }
        return 1;
    }

    private static int GetHoursInADaySafe()
    {
        try
        {
            if (_dataHoursInADayProp?.GetValue(null) is int h && h > 0) return h;
        }
        catch { }
        return 6;
    }

    private static int ReadCurrentPlayerHandUnlockedMask()
    {
        try
        {
            var run = GameInspector.GetDataRun();
            var player = run != null ? GameInspector.GetMember(run, "Player") : null;
            var hand = player != null ? GameInspector.GetMember(player, "Hand") : null;
            var container = hand != null ? (GameInspector.GetMember(hand, "Container") ?? hand) : null;
            if (container is SocketedContainer sc)
            {
                return sc.UnlockedSockets;
            }
        }
        catch { }
        return 0;
    }

    private static bool IsNewDayTransitionActiveOrPending()
    {
        try
        {
            var ctrl = _dataNewDayCtrlProp?.GetValue(null);
            if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) return false;
            Type ct = ctrl.GetType();
            _newDayIsActiveProp ??= ct.GetProperty("IsActive", BindingFlags.Public | BindingFlags.Instance);
            _newDayHasPendingProp ??= ct.GetProperty("HasPendingDayChanged", BindingFlags.Public | BindingFlags.Instance);
            if (_newDayIsActiveProp?.GetValue(ctrl) is bool active && active) return true;
            if (_newDayHasPendingProp?.GetValue(ctrl) is bool pending && pending) return true;
        }
        catch { }
        return false;
    }

    private static string FormatCmdArgsSummary(object[] args)
    {
        if (args == null || args.Length == 0) return string.Empty;
        var sb = new StringBuilder(96);
        int limit = Math.Min(args.Length, 3);
        for (int i = 0; i < limit; i++)
        {
            if (i > 0) sb.Append(", ");
            var a = args[i];
            if (a == null)
            {
                sb.Append("null");
            }
            else if (a is ICollection col)
            {
                sb.Append("count=").Append(col.Count);
            }
            else
            {
                object iid = GameInspector.GetMember(a, "InstanceId");
                sb.Append(iid != null ? iid.ToString() : a.ToString());
            }
        }
        return sb.ToString();
    }

    private sealed class BareAuditWatchdogBehaviour : MonoBehaviour
    {
        private float _nextSampleAt;

        private void Update()
        {
            if (!IsActive) return;
            float now = Time.unscaledTime;
            if (now < _nextSampleAt) return;
            _nextSampleAt = now + 0.5f;
            SampleDeadlockTickOnMain();
        }
    }
}

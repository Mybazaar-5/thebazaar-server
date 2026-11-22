using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Domain.Runs;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using BazaarLauncher.Core;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// 战斗流程增强、首战加速修复、1x/2x/3x 手动倍速稳定控制、高倍速流畅化与战斗触发明细审计补丁：
///
///   1. CombatState.GoToNextState 安全切态兜底（优先恢复同场战斗 DespawnMessage，严禁对 PvP/战败/终局盲切 Loot）；
///   2. 首场战斗加速修复：Day 1 首战（Hour 1~3）只要用户启用首战加速或默认未显式关闭即触发 3x；
///   3. 稳定战斗倍速控制：支持 UI 倍速小芯片（1x / 2x / 3x）与 GameDriver.SpeedOverride，
///      在模拟阶段不会被每帧 `else SetSpeed(1f)` 覆盖重置，并在常规倍速（&lt; 4x）的最后 40 帧保留终结慢镜流畅演出；
///   4. EnforceMaxTimeScale 守卫：仅在自动化极速模式（SpeedOverride &gt;= 4x）屏蔽慢镜与 TimeScale 压低，
///      常规倍速不压低 TimeScale 且战斗退出/取消时保证安全恢复 1f；
///   5. 可选 AutoExitReplay 与 NetMessageCombatSim 全量触发明细审计。
/// </summary>
internal static class CombatPatch
{
    private static bool _applied;
    private static int _mainThreadId;
    private static SynchronizationContext _unitySyncContext;
    private static float _speedOverride;
    private static float _manualUiSpeed;
    private static float _baseFixedDeltaTime = -1f;
    private static volatile bool _replayReadyToExit;
    private static float _replayEnteredAt;
    private static Type _gameServiceManagerType;
    private static PropertyInfo _gameServiceManagerInstanceProp;
    private static FieldInfo _finalBlowCurrentStateField;
    private static object _finalBlowCompleteEnumVal;

    // 模拟生命周期状态（用于精准放行收尾 40 帧与 finally 归位）
    private static volatile bool _inSimulateLoop;
    private static volatile int _currentFramesLeft = -1;
    private static volatile bool _bypassSetSpeedPrefix;
    private static WeakReference<object> _activeCombatHandlerRef;
    private static WeakReference<object> _lastRunObjRef;
    private static MethodInfo _setSpeedMethod;
    private static PropertyInfo _speedMultiplierProp;
    private static MethodInfo _enforceMaxTimeScaleMethod;
    private static PropertyInfo _gamePausedProp;

    // P1-29：CombatState.GoToNextState 切态核验反射句柄与最近实况战斗胜负缓存
    private static Type _appStateType;
    private static Type _pvpCombatStateType;
    private static FieldInfo _cachedGameSimField;
    private static FieldInfo _cachedCombatSimField;
    private static MethodInfo _appStateSetStateByRunStateMethod;
    private static Type _networkManagerType;
    private static MethodInfo _nmGetInstanceMethod;
    private static PropertyInfo _nmLastCombatSequenceProp;
    private static FieldInfo _combatSeqDespawnField;
    private static FieldInfo _combatSeqCombatField;
    private static ECombatantId? _lastLiveCombatWinner;
    private static string _lastLiveCombatMessageId;
    private static bool _lastLiveCombatWasPvp;
    private static WeakReference<NetMessageCombatSim> _lastLiveCombatSimRef;
    // P2-15 (H-7)：GoToNextState 兜底命中低频诊断计数
    private static int _hackH7GoToNextStateFallbackCount;
    internal static int HackH7GoToNextStateFallbackCount => Volatile.Read(ref _hackH7GoToNextStateFallbackCount);

    private static int _combatSeq;
    private static readonly object _auditLock = new();
    private static Dictionary<string, object> _lastCombatAudit;
    private static readonly ConcurrentQueue<Dictionary<string, object>> _auditHistory = new();

    // V13.2 战斗读条丝滑优化：仅战斗模拟期启用的 1ms Windows 高精度定时器 + 60/120+ FPS 冷却条平滑插值
    private static bool _highResTimerActive;
    private static bool _processExitHooked;
    private static FieldInfo _cdAnimatedTransformField;
    private static MethodInfo _cdRendererSetCooldownMethod;
    private static MethodInfo _tooltipGetCurAttrMethod;
    private static MethodInfo _tooltipNextTierHasAttrMethod;
    private static MethodInfo _tooltipGetBaseFromTplMethod;
    private static GameObject _smoothTickerGo;

    private sealed class CooldownBarTrack
    {
        public Transform Transform;
        public float CurrentY;
        public float LastTargetY;
        public float VelocityY;
        public float LastSampleTime;
        public bool HasVelocity;
        public bool Active;
    }

    private static readonly Dictionary<int, CooldownBarTrack> _cdTracks = new(32);
    private static readonly List<int> _cdDeadKeys = new(8);

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint WinMmTimeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint WinMmTimeEndPeriod(uint uMilliseconds);

    private sealed class CooldownSmoothTicker : MonoBehaviour
    {
        private void Update()
        {
            TickSmoothCooldownBars();
        }

        private void OnDestroy()
        {
            ClearSmoothCooldownTracks();
        }
    }

    /// <summary>
    /// 调试/自动化战斗倍速覆盖（GameDriver 专用）。0 表示交由用户手动倍速或首战自动规则处理。
    /// </summary>
    internal static float SpeedOverride
    {
        get => _speedOverride;
        set
        {
            float clamped = Math.Max(0f, value);
            _speedOverride = clamped;
            // 线程安全（修复 P1-5）：严禁在 HTTP 线程直接写 Unity Time.fixedDeltaTime 或调 CombatSimHandler.SetSpeed
            BazaarLauncher.Core.MainThreadDispatcher.Enqueue(() =>
            {
                if (_inSimulateLoop)
                {
                    float target = _speedOverride > 0.1f ? _speedOverride : GetEffectiveTargetSpeed();
                    SyncFixedDeltaTimeForSpeed(target);
                    ApplySpeedToActiveHandler(target);
                }
                else
                {
                    SyncFixedDeltaTimeForSpeed(1f);
                }
                CustomGameplayUI.Instance?.NotifyCombatSpeedChangedOnMain();
            });
        }
    }

    /// <summary>
    /// 战斗界面右上角 UI 芯片手动选定的倍速（0 = 自动，1/2/3/5 = 玩家显式锁定）。
    /// </summary>
    internal static float ManualUiSpeed => _manualUiSpeed;

    /// <summary>
    /// 是否在战斗播完、进入 ReplayState 且战后数据更新完毕后自动退出回放界面。
    /// </summary>
    internal static bool AutoExitReplay { get; set; }

    internal static void SetManualUiSpeed(float speed)
    {
        _manualUiSpeed = speed <= 0.1f ? 0f : Mathf.Clamp(speed, 1f, 5f);
        SyncFixedDeltaTimeForSpeed(_inSimulateLoop ? GetEffectiveTargetSpeed() : 1f);

        if (_inSimulateLoop && (_speedOverride >= 4f || _manualUiSpeed >= 4f || _currentFramesLeft < 0 || _currentFramesLeft > 40))
        {
            float target = _speedOverride > 0.1f ? _speedOverride : GetEffectiveTargetSpeed();
            ApplySpeedToActiveHandler(target);
        }
        CustomGameplayUI.Instance?.NotifyCombatSpeedChangedOnMain();
    }

    internal static float GetDisplayCombatSpeed()
    {
        if (_speedOverride > 0.1f) return _speedOverride;
        if (_inSimulateLoop && _speedMultiplierProp != null)
        {
            try
            {
                object val = _speedMultiplierProp.GetValue(null);
                if (val is float f && f > 0.1f) return f;
            }
            catch { }
        }
        if (_manualUiSpeed > 0.1f) return _manualUiSpeed;
        if (IsFirstFightDay1() && IsFirstFightFastForwardEnabled()) return 3f;
        return 1f;
    }

    private static float GetEffectiveTargetSpeed()
    {
        if (_speedOverride > 0.1f) return _speedOverride;
        if (_manualUiSpeed > 0.1f) return _manualUiSpeed;
        if (IsFirstFightDay1() && IsFirstFightFastForwardEnabled()) return 3f;
        return 1f;
    }

    private static void ApplySpeedToActiveHandler(float speed)
    {
        try
        {
            object handler = null;
            if (_activeCombatHandlerRef == null || !_activeCombatHandlerRef.TryGetTarget(out handler) || handler == null)
            {
                var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
                handler = appStateType?.GetField("_combatSimHandler", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public)?.GetValue(null);
            }
            if (handler == null || _setSpeedMethod == null) return;

            _bypassSetSpeedPrefix = true;
            try
            {
                _setSpeedMethod.Invoke(handler, new object[] { speed, true });
            }
            finally
            {
                _bypassSetSpeedPrefix = false;
            }
        }
        catch { }
    }

    private static void SyncFixedDeltaTimeForSpeed(float speed)
    {
        try
        {
            if (_baseFixedDeltaTime <= 0f)
            {
                float cur = Time.fixedDeltaTime;
                _baseFixedDeltaTime = (cur > 0.001f && cur < 0.1f) ? cur : 0.02f;
            }
            // 倍速战斗期间按比例放大 fixedDeltaTime，防止 3x/5x 下 Unity 每帧重复跑 3~5 次 PhysX 物理步进拖爆 GPU/CPU
            float scale = speed > 1.05f ? Mathf.Clamp(speed, 1f, 8f) : 1f;
            float targetFixed = _baseFixedDeltaTime * scale;
            if (Mathf.Abs(Time.fixedDeltaTime - targetFixed) > 0.0005f)
            {
                Time.fixedDeltaTime = targetFixed;
            }
        }
        catch { }
    }

    private static void UpdateHighResTimer(bool enable)
    {
        try
        {
            if (enable)
            {
                if (!_highResTimerActive)
                {
                    if (WinMmTimeBeginPeriod(1) == 0)
                    {
                        _highResTimerActive = true;
                        if (!_processExitHooked)
                        {
                            _processExitHooked = true;
                            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
                            {
                                try
                                {
                                    if (_highResTimerActive)
                                    {
                                        _highResTimerActive = false;
                                        WinMmTimeEndPeriod(1);
                                    }
                                }
                                catch { }
                            };
                        }
                    }
                }
            }
            else
            {
                if (_highResTimerActive)
                {
                    _highResTimerActive = false;
                    WinMmTimeEndPeriod(1);
                }
                SyncFixedDeltaTimeForSpeed(1f);
            }
        }
        catch
        {
            if (!enable) SyncFixedDeltaTimeForSpeed(1f);
        }
    }

    internal static void ResetAppliedFlag()
    {
        _applied = false;
        _inSimulateLoop = false;
        _currentFramesLeft = -1;
        _lastLiveCombatWinner = null;
        _lastLiveCombatMessageId = null;
        _lastLiveCombatWasPvp = false;
        _lastLiveCombatSimRef = null;
        _activeCombatHandlerRef = null;
        _lastRunObjRef = null;
        UnregisterStaticCombatEndedListeners();

        // P1-45：热卸载时彻底收尾平滑读条缓存、WinMM 1ms 定时器、fixedDeltaTime、FinalBlow 状态机、TimeScale 与常驻 Ticker GameObject
        ClearSmoothCooldownTracks();
        UpdateHighResTimer(false);
        ResetFinalBlowStateToComplete();

        Action mainCleanup = () =>
        {
            try
            {
                ClearSmoothCooldownTracks();
                SyncFixedDeltaTimeForSpeed(1f);
                SafeRestoreNormalTimeScale();
                if (_smoothTickerGo != null)
                {
                    UnityEngine.Object.Destroy(_smoothTickerGo);
                    _smoothTickerGo = null;
                }
            }
            catch { }
        };

        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            mainCleanup();
        }
        else
        {
            MainThreadDispatcher.Enqueue(mainCleanup);
        }
    }

    internal static void Apply(SafeHarmonyPatcher patcher)
    {
        if (patcher == null || _applied) return;
        _applied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        PatchCombatStateGoNext(patcher);
        PatchCombatSimSpeed(patcher);
        PatchOptionsFastForwardSync(patcher);
        PatchFinalBlowSlowDown(patcher);
        PatchGameServiceManagerTimeScale(patcher);
        // P1-5 修复：不再对 CombatSimHandler.Simulate 重复打第 2 个补丁，条件审计已合入 SimulateSpeedPrefix
        PatchReplayAutoExit(patcher);
        PatchPostCombatVisualCleanup(patcher);
        PatchDisableTutorials(patcher);
        PatchSmoothCooldownBars(patcher);
        PatchPrecisionCooldownTooltip(patcher);
    }

    // ── 1. CombatState.GoToNextState 安全切态兜底（P1-29：移除盲切 Loot）───────
    private static void PatchCombatStateGoNext(SafeHarmonyPatcher patcher)
    {
        var combatState = AccessTools.TypeByName("TheBazaar.CombatState");
        var go = combatState == null ? null : AccessTools.Method(combatState, "GoToNextState");
        if (go == null) return;

        EnsureGoToNextStateReflectionHandles();
        if (patcher.TryPatchMethod(
            go,
            prefix: AccessTools.Method(typeof(CombatPatch), nameof(GoToNextStatePrefix)),
            optional: true,
            label: "CombatState.GoToNextState"))
        {
            BazaarLauncherPlugin.Log.LogInfo("Patched CombatState.GoToNextState (P1-29 authoritative-safe)");
        }
    }

    private static void EnsureGoToNextStateReflectionHandles()
    {
        _appStateType ??= AccessTools.TypeByName("TheBazaar.AppState");
        _pvpCombatStateType ??= AccessTools.TypeByName("TheBazaar.PVPCombatState");
        if (_appStateType != null)
        {
            _cachedGameSimField ??= _appStateType.GetField("_cachedGameSim", BindingFlags.NonPublic | BindingFlags.Static);
            _cachedCombatSimField ??= _appStateType.GetField("_cachedCombatSim", BindingFlags.NonPublic | BindingFlags.Static);
            _appStateSetStateByRunStateMethod ??= _appStateType.GetMethod(
                "SetState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(ERunState) },
                null);
        }

        _networkManagerType ??= AccessTools.TypeByName("Networking.NetworkManager");
        if (_networkManagerType != null)
        {
            _nmGetInstanceMethod ??= _networkManagerType.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
            _nmLastCombatSequenceProp ??= _networkManagerType.GetProperty("LastCombatSequence", BindingFlags.Public | BindingFlags.Instance);
        }
    }

    private static bool IsPvpCombatInstance(object combatStateInstance)
    {
        if (combatStateInstance != null)
        {
            if (_pvpCombatStateType != null && _pvpCombatStateType.IsInstanceOfType(combatStateInstance))
                return true;
            if (string.Equals(combatStateInstance.GetType().Name, "PVPCombatState", StringComparison.Ordinal))
                return true;
        }

        var curAppState = GameInspector.GetCurrentAppState();
        if (curAppState != null)
        {
            if (_pvpCombatStateType != null && _pvpCombatStateType.IsInstanceOfType(curAppState))
                return true;
            if (string.Equals(curAppState.GetType().Name, "PVPCombatState", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static ERunState? GetAuthoritativeDataRunState()
    {
        try
        {
            var dataState = GameInspector.GetDataCurrentState();
            if (dataState != null && GameInspector.GetMember(dataState, "StateName") is ERunState runState)
            {
                return runState;
            }
        }
        catch { }
        return null;
    }

    private static ECombatantId? ResolveCurrentCombatWinner(NetMessageCombatSim cachedCombatSim, bool isPvp)
    {
        if (cachedCombatSim?.Data != null)
        {
            return cachedCombatSim.Data.Winner;
        }

        if (_lastLiveCombatWinner.HasValue && _lastLiveCombatWasPvp == isPvp)
        {
            return _lastLiveCombatWinner.Value;
        }

        return null;
    }

    private static bool IsAuthoritativePostCombatNonLootState(ERunState state)
    {
        return state is ERunState.Choice
            or ERunState.EndRunVictory
            or ERunState.EndRunDefeat
            or ERunState.LevelUp
            or ERunState.TournamentPvpChoice
            or ERunState.TournamentWaitingRoom;
    }

    private static bool GoToNextStatePrefix(object __instance)
    {
        try
        {
            EnsureGoToNextStateReflectionHandles();
            var cachedGameSim = _cachedGameSimField?.GetValue(null);
            if (cachedGameSim != null) return true;

            var cachedCombatSim = _cachedCombatSimField?.GetValue(null) as NetMessageCombatSim;
            var nm = _nmGetInstanceMethod?.Invoke(null, null);
            var lastSeq = nm != null ? _nmLastCombatSequenceProp?.GetValue(nm) : null;
            if (lastSeq != null)
            {
                _combatSeqDespawnField ??= lastSeq.GetType().GetField("DespawnMessage", BindingFlags.Public | BindingFlags.Instance);
                _combatSeqCombatField ??= lastSeq.GetType().GetField("CombatMessage", BindingFlags.Public | BindingFlags.Instance);

                var despawn = _combatSeqDespawnField?.GetValue(lastSeq);
                var seqCombat = _combatSeqCombatField?.GetValue(lastSeq) as NetMessageCombatSim;

                // 仅当 LastCombatSequence 与当前这场战斗严格匹配（同引用或同非空 MessageId）时才回填 DespawnMessage，
                // 绝不在 MessageId 为空或缺乏匹配依据时放行，防止本场丢包时误复用上一场战斗残留的 DespawnMessage
                NetMessageCombatSim expectedCombatSim = cachedCombatSim;
                if (expectedCombatSim == null && _lastLiveCombatSimRef != null)
                {
                    _lastLiveCombatSimRef.TryGetTarget(out expectedCombatSim);
                }

                string expectedCombatMsgId = !string.IsNullOrEmpty(expectedCombatSim?.MessageId)
                    ? expectedCombatSim.MessageId
                    : _lastLiveCombatMessageId;

                bool sequenceMatchesCurrentCombat = false;
                if (seqCombat != null)
                {
                    if (expectedCombatSim != null && ReferenceEquals(seqCombat, expectedCombatSim))
                    {
                        sequenceMatchesCurrentCombat = true;
                    }
                    else if (!string.IsNullOrEmpty(expectedCombatMsgId)
                        && !string.IsNullOrEmpty(seqCombat.MessageId)
                        && string.Equals(seqCombat.MessageId, expectedCombatMsgId, StringComparison.Ordinal))
                    {
                        sequenceMatchesCurrentCombat = true;
                    }
                }

                if (despawn != null && sequenceMatchesCurrentCombat)
                {
                    _cachedGameSimField?.SetValue(null, despawn);
                    RecordHackH7Hit("RecoveredDespawnFromLastCombatSequence");
                    return true;
                }
            }

            bool isPvp = IsPvpCombatInstance(__instance);
            ECombatantId? winner = ResolveCurrentCombatWinner(cachedCombatSim, isPvp);
            ERunState? authoritativeState = GetAuthoritativeDataRunState();

            // P1-29：严禁无条件盲切 ERunState.Loot！
            // 仅当确认为 PvE（非 PVPCombatState）、玩家胜利、且服务端权威 CurrentState 明确为 Loot 时才允许兜底切入 Loot
            if (!isPvp && winner == ECombatantId.Player && authoritativeState == ERunState.Loot)
            {
                if (_appStateSetStateByRunStateMethod != null)
                {
                    RecordHackH7Hit("AuthoritativeFallback:Loot");
                    BazaarLauncherPlugin.Log.LogWarning(
                        "GoToNextState[P1-29]: _cachedGameSim 缺失，已核实 PvE 胜利且服务端权威状态为 Loot，执行权威兜底切态 -> Loot");
                    _ = _appStateSetStateByRunStateMethod.Invoke(null, new object[] { ERunState.Loot }) as Task;
                    return false;
                }
            }

            // 若服务端权威 CurrentState 已更新为明确的战后非战斗/非 Loot 状态（如 Choice / EndRunVictory / EndRunDefeat / LevelUp），按权威态切态
            if (authoritativeState.HasValue && IsAuthoritativePostCombatNonLootState(authoritativeState.Value))
            {
                if (_appStateSetStateByRunStateMethod != null)
                {
                    RecordHackH7Hit("AuthoritativeFallback:" + authoritativeState.Value);
                    BazaarLauncherPlugin.Log.LogWarning(
                        $"GoToNextState[P1-29]: _cachedGameSim 缺失，按服务端权威 CurrentState 切态 -> {authoritativeState.Value} (isPvp={isPvp}, winner={winner})");
                    _ = _appStateSetStateByRunStateMethod.Invoke(null, new object[] { authoritativeState.Value }) as Task;
                    return false;
                }
            }

            // 其余情况（PvP/终局/战败或延迟包尚未抵达）绝不伪造或盲切 LootState，交由原方法记录日志并安全等待后续服务端消息
            RecordHackH7Hit($"BlockedBlindLoot(isPvp={isPvp},winner={winner},auth={authoritativeState})");
            BazaarLauncherPlugin.Log.LogWarning(
                $"GoToNextState[P1-29]: 缺少 _cachedGameSim 与匹配 DespawnMessage，拒绝盲切 Loot，等待延迟包或状态同步 (isPvp={isPvp}, winner={winner}, authoritativeState={authoritativeState})");
            return true;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("GoToNextStatePrefix: " + ex.Message);
            return true;
        }
    }

    private static void RecordHackH7Hit(string detail)
    {
        int cnt = Interlocked.Increment(ref _hackH7GoToNextStateFallbackCount);
        if (cnt == 1 || cnt == 5 || cnt % 25 == 0)
        {
            BazaarLauncherPlugin.Log?.LogInfo($"[CompatDiag][H-7] GoToNextState fallback hit (count={cnt}, detail={detail})");
        }
    }

    // ── 2. CombatSimHandler 首战加速与倍速稳定控制 ──────────────────────────
    private static void PatchCombatSimSpeed(SafeHarmonyPatcher patcher)
    {
        try
        {
            var handlerType = AccessTools.TypeByName("TheBazaar.CombatSimHandler");
            if (handlerType == null)
            {
                BazaarLauncherPlugin.Log.LogWarning("CombatSimHandler not found");
                return;
            }

            _setSpeedMethod = AccessTools.Method(handlerType, "SetSpeed", new[] { typeof(float), typeof(bool) });
            _speedMultiplierProp = AccessTools.Property(handlerType, "SpeedMultiplier");

            if (_setSpeedMethod != null)
            {
                patcher.TryPatchMethod(
                    _setSpeedMethod,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(SetSpeedPrefix)),
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(SetSpeedPostfix)),
                    optional: true,
                    label: "CombatSimHandler.SetSpeed");
            }

            var shouldFf = AccessTools.Method(handlerType, "ShouldFastForward");
            if (shouldFf != null)
            {
                patcher.TryPatchMethod(
                    shouldFf,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(ShouldFastForwardPostfix)),
                    optional: true,
                    label: "CombatSimHandler.ShouldFastForward");
            }

            var simMethod = AccessTools.Method(handlerType, "Simulate", new[] { typeof(NetMessageCombatSim), typeof(CancellationTokenSource) });
            if (simMethod != null)
            {
                patcher.TryPatchMethod(
                    simMethod,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(SimulateSpeedPrefix)),
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(SimulateSpeedPostfix)),
                    optional: true,
                    label: "CombatSimHandler.Simulate");
            }

            BazaarLauncherPlugin.Log.LogInfo("Patched CombatSimHandler (first-fight 3x, 1x/2x/3x stable control, clean slowmo)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchCombatSimSpeed: " + ex.Message);
        }
    }

    private static void PostToMainThread(Action action)
    {
        if (action == null) return;
        if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
        {
            action();
            return;
        }
        if (_unitySyncContext != null)
        {
            _unitySyncContext.Post(_ => action(), null);
            return;
        }
        action();
    }

    private static void SimulateSpeedPrefix(object __instance, NetMessageCombatSim message)
    {
        try
        {
            bool isReplay = GameInspector.GetAppStateName() == "ReplayState";
            bool isPvp = GameInspector.GetAppStateName() == "PVPCombatState";
            if (!isReplay)
            {
                _lastLiveCombatWasPvp = isPvp;
                _lastLiveCombatMessageId = message?.MessageId;
                _lastLiveCombatWinner = message?.Data != null ? message.Data.Winner : null;
                _lastLiveCombatSimRef = message != null ? new WeakReference<NetMessageCombatSim>(message) : null;
            }
            BazaarLauncherPlugin.Host?.EventBus.Publish(new BazaarModSdk.CombatLifecycleModEvent(true, isPvp, isReplay));

            // P1-5 修复：生产环境默认关闭全帧审计以消除进战前数十毫秒停顿与堆分配；仅诊断/驱动启用时记录
            if (!isReplay && message?.Data != null && (BazaarLauncherPlugin.EnableCombatAudit?.Value == true || GameDriver.IsRunning))
            {
                RecordCombatAudit(message);
            }

            if (__instance != null && !(__instance is UnityEngine.Object uo && uo == null))
            {
                _activeCombatHandlerRef = new WeakReference<object>(__instance);
            }

            // 检测到开启了全新一局（Data.Run 实例变更且处于 Day 1 前期），清空上局残留的手动锁速，确保新局首战自动规则生效
            var run = GameInspector.GetDataRun();
            if (run != null)
            {
                object prevRun = null;
                if (_lastRunObjRef == null || !_lastRunObjRef.TryGetTarget(out prevRun) || !ReferenceEquals(prevRun, run))
                {
                    _lastRunObjRef = new WeakReference<object>(run);
                    if (IsFirstFightDay1())
                    {
                        _manualUiSpeed = 0f;
                    }
                }
            }

            _inSimulateLoop = true;
            _currentFramesLeft = message?.Data?.Frames?.Count ?? int.MaxValue;
            ClearSmoothCooldownTracks();
            EnsureSmoothTickerInstalled();
            UpdateHighResTimer(true);
            float targetSpeed = GetEffectiveTargetSpeed();
            SyncFixedDeltaTimeForSpeed(targetSpeed);
            CustomGameplayUI.Instance?.NotifyCombatSpeedChangedOnMain();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CombatPatch.SimulateSpeedPrefix: " + ex.Message);
        }
    }

    private static void SimulateSpeedPostfix(ref Task __result)
    {
        try
        {
            if (__result != null)
            {
                __result = WrapSimulateTask(__result);
            }
            else
            {
                CleanupSimulateState();
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CombatPatch.SimulateSpeedPostfix: " + ex.Message);
        }
    }

    private static async Task WrapSimulateTask(Task inner)
    {
        try
        {
            await inner;
        }
        finally
        {
            PostToMainThread(CleanupSimulateState);
        }
    }

    private static void CleanupSimulateState()
    {
        try
        {
            _inSimulateLoop = false;
            _currentFramesLeft = -1;
            ClearSmoothCooldownTracks();
            UpdateHighResTimer(false);

            // 若战斗被提前取消或慢镜未完全恢复，保证退出后不会卡在低 TimeScale、残留 FinalBlow 状态机或高 SpeedMultiplier
            if (_speedOverride >= 4f || _manualUiSpeed >= 4f)
            {
                ResetFinalBlowStateToComplete();
                SafeRestoreNormalTimeScale();
            }
            else if (Time.timeScale > 0.01f && Time.timeScale < 0.99f)
            {
                SafeRestoreNormalTimeScale();
            }

            if (_activeCombatHandlerRef != null && _activeCombatHandlerRef.TryGetTarget(out var h)
                && h != null && !(h is UnityEngine.Object huo && huo == null) && _setSpeedMethod != null)
            {
                _bypassSetSpeedPrefix = true;
                try { _setSpeedMethod.Invoke(h, new object[] { 1f, false }); }
                catch { }
                finally { _bypassSetSpeedPrefix = false; }
            }
            CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: false);
            CustomGameplayUI.Instance?.NotifyCombatSpeedChangedOnMain();
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CombatPatch.CleanupSimulateState: " + ex.Message);
        }
    }

    private static void ShouldFastForwardPostfix(ref bool __result)
    {
        try
        {
            if (_speedOverride > 0.1f)
            {
                __result = _speedOverride > 1.5f;
                return;
            }
            if (_manualUiSpeed > 0.1f)
            {
                __result = _manualUiSpeed > 1.5f;
                return;
            }
            if (!__result && IsFirstFightDay1() && IsFirstFightFastForwardEnabled())
            {
                __result = true;
            }
        }
        catch { }
    }

    private static void SetSpeedPrefix(object __instance, ref float speed, out float __state)
    {
        __state = 1f;
        try
        {
            if (_speedMultiplierProp?.GetValue(null) is float cur) __state = cur;

            if (__instance != null && !(__instance is UnityEngine.Object uo && uo == null))
            {
                _activeCombatHandlerRef = new WeakReference<object>(__instance);
            }

            if (_bypassSetSpeedPrefix || !_inSimulateLoop)
            {
                return;
            }

            // 极速模式（SpeedOverride >= 4x）或手动 5x 沙暴快进全程保持高倍速
            if (_speedOverride >= 4f)
            {
                speed = _speedOverride;
                return;
            }
            if (_manualUiSpeed >= 4f)
            {
                speed = _manualUiSpeed;
                return;
            }

            // 常规人类倍速（1x / 2x / 3x）：最后 40 帧放行降速到 1x，保留终结慢镜演出的流畅性
            if (_currentFramesLeft >= 0 && _currentFramesLeft <= 40)
            {
                if (speed > 1f) speed = 1f;
                return;
            }

            // 战斗主体播放阶段：优先遵循 GameDriver.SpeedOverride 或 UI 手动芯片选定速度，绝不被每帧 else SetSpeed(1f) 打回
            if (_speedOverride > 0.1f)
            {
                speed = _speedOverride;
            }
            else if (_manualUiSpeed > 0.1f)
            {
                speed = _manualUiSpeed;
            }
        }
        catch { }
    }

    private static void SetSpeedPostfix(float __state)
    {
        try
        {
            if (_speedMultiplierProp?.GetValue(null) is float cur && !Mathf.Approximately(cur, __state))
            {
                CustomGameplayUI.Instance?.NotifyCombatSpeedChangedOnMain();
            }
        }
        catch { }
    }

    private static bool IsFirstFightDay1()
    {
        try
        {
            var run = GameInspector.GetDataRun();
            if (run == null) return false;
            uint day = Convert.ToUInt32(GameInspector.GetMember(run, "Day") ?? 0u);
            uint hour = Convert.ToUInt32(GameInspector.GetMember(run, "Hour") ?? 0u);
            return day == 1 && hour <= 3;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFirstFightFastForwardEnabled()
    {
        try
        {
            bool prefVal = false;
            var prefsType = AccessTools.TypeByName("PlayerPreferences");
            var prefsData = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (prefsData != null)
            {
                var p = prefsData.GetType().GetProperty("FastForwardFirstFight", BindingFlags.Public | BindingFlags.Instance);
                if (p?.GetValue(prefsData) is bool b) prefVal = b;
            }

            if (prefVal) return true;
            if (BazaarLauncherPlugin.FastForwardUserExplicitlySet?.Value == true)
            {
                return BazaarLauncherPlugin.FastForwardFirstFight?.Value ?? false;
            }
            // 默认未显式关闭即开启首战 3x 加速
            return BazaarLauncherPlugin.FastForwardFirstFight?.Value ?? true;
        }
        catch
        {
            return true;
        }
    }

    // ── 3. OptionsDialogController 首战加速选项同步 ─────────────────────────
    private static void PatchOptionsFastForwardSync(SafeHarmonyPatcher patcher)
    {
        try
        {
            var optType = AccessTools.TypeByName("OptionsDialogController");
            if (optType == null) return;

            var awakeM = AccessTools.Method(optType, "Awake");
            if (awakeM != null)
            {
                patcher.TryPatchMethod(
                    awakeM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(OptionsAwakePostfix)),
                    optional: true,
                    label: "OptionsDialogController.Awake");
            }

            var changedM = AccessTools.Method(optType, "OnFastForwardFirstFightChanged", new[] { typeof(bool) });
            if (changedM != null)
            {
                patcher.TryPatchMethod(
                    changedM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(OptionsFastForwardChangedPostfix)),
                    optional: true,
                    label: "OptionsDialogController.OnFastForwardFirstFightChanged");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("PatchOptionsFastForwardSync: " + ex.Message);
        }
    }

    private static void OptionsAwakePostfix(object __instance)
    {
        try
        {
            if (__instance == null || (__instance is UnityEngine.Object uo && uo == null)) return;
            if (BazaarLauncherPlugin.FastForwardUserExplicitlySet?.Value == false
                && (BazaarLauncherPlugin.FastForwardFirstFight?.Value ?? true))
            {
                var prefsType = AccessTools.TypeByName("PlayerPreferences");
                var prefsData = prefsType?.GetProperty("Data", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                prefsData?.GetType().GetProperty("FastForwardFirstFight", BindingFlags.Public | BindingFlags.Instance)?.SetValue(prefsData, true);

                var toggleField = __instance.GetType().GetField("_fastForwardFirstFight", BindingFlags.NonPublic | BindingFlags.Instance);
                if (toggleField?.GetValue(__instance) is Toggle tog && tog != null)
                {
                    tog.SetIsOnWithoutNotify(true);
                }
            }
        }
        catch { }
    }

    private static void OptionsFastForwardChangedPostfix(bool value)
    {
        try
        {
            if (BazaarLauncherPlugin.FastForwardUserExplicitlySet != null)
            {
                BazaarLauncherPlugin.FastForwardUserExplicitlySet.Value = true;
            }
            if (BazaarLauncherPlugin.FastForwardFirstFight != null)
            {
                BazaarLauncherPlugin.FastForwardFirstFight.Value = value;
            }
        }
        catch { }
    }

    // ── 4. FinalBlowSlowDown 与 GameServiceManager.EnforceMaxTimeScale 守卫 ─
    private static void PatchFinalBlowSlowDown(SafeHarmonyPatcher patcher)
    {
        try
        {
            var fbType = AccessTools.TypeByName("TheBazaar.FinalBlowSlowDownController");
            if (fbType == null) return;

            _finalBlowCurrentStateField = fbType.GetField("_currentState", BindingFlags.NonPublic | BindingFlags.Static);
            if (_finalBlowCurrentStateField != null && _finalBlowCurrentStateField.FieldType.IsEnum)
            {
                try { _finalBlowCompleteEnumVal = Enum.Parse(_finalBlowCurrentStateField.FieldType, "Complete"); } catch { }
            }

            var processM = AccessTools.Method(fbType, "Process", new[] { typeof(int) });
            if (processM != null)
            {
                patcher.TryPatchMethod(
                    processM,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(FinalBlowProcessPrefix)),
                    optional: true,
                    label: "FinalBlowSlowDownController.Process");
            }

            var returnM = AccessTools.Method(fbType, "ReturnToNormalSpeed");
            if (returnM != null)
            {
                patcher.TryPatchMethod(
                    returnM,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(FinalBlowReturnPrefix)),
                    optional: true,
                    label: "FinalBlowSlowDownController.ReturnToNormalSpeed");
            }
            BazaarLauncherPlugin.Log.LogInfo("Patched FinalBlowSlowDownController (smooth 1x-3x slowmo preserved, >=4x fast skip + state reset)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchFinalBlowSlowDown: " + ex.Message);
        }
    }

    private static void ResetFinalBlowStateToComplete()
    {
        try
        {
            if (_finalBlowCurrentStateField != null && _finalBlowCompleteEnumVal != null)
            {
                _finalBlowCurrentStateField.SetValue(null, _finalBlowCompleteEnumVal);
            }
        }
        catch { }
    }

    private static void EnsureGameServiceManagerHandles()
    {
        if (_gameServiceManagerType == null)
        {
            _gameServiceManagerType = GameInspector.FindTypeSafe("GameServiceManager", "TheBazaar.GameServiceManager");
        }
        if (_gameServiceManagerType == null) return;

        _gameServiceManagerInstanceProp ??= _gameServiceManagerType.GetProperty(
            "Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
        _enforceMaxTimeScaleMethod ??= AccessTools.Method(_gameServiceManagerType, "EnforceMaxTimeScale", new[] { typeof(float) });
        _gamePausedProp ??= _gameServiceManagerType.GetProperty(
            "GamePaused", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    }

    private static void PatchGameServiceManagerTimeScale(SafeHarmonyPatcher patcher)
    {
        try
        {
            EnsureGameServiceManagerHandles();
            if (_enforceMaxTimeScaleMethod != null)
            {
                patcher.TryPatchMethod(
                    _enforceMaxTimeScaleMethod,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(EnforceMaxTimeScalePrefix)),
                    optional: true,
                    label: "GameServiceManager.EnforceMaxTimeScale");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("PatchGameServiceManagerTimeScale: " + ex.Message);
        }
    }

    private static void EnforceMaxTimeScalePrefix(ref float maximum)
    {
        try
        {
            // P1-45：自动化极速模式（SpeedOverride >= 4x）与玩家手动 5x 沙暴快进均禁止 HitStun / 慢镜压低全局 TimeScale
            if ((_speedOverride >= 4f || _manualUiSpeed >= 4f) && maximum < 1f)
            {
                maximum = 1f;
            }
        }
        catch { }
    }

    private static bool FinalBlowProcessPrefix(int framesLeft)
    {
        try
        {
            _currentFramesLeft = framesLeft;
            // 自动化极速模式（>= 4x）或玩家手动开启 5x 沙暴加速时跳过终局慢镜；1x/2x/3x 完整保留终结慢镜演出
            return _speedOverride < 4f && _manualUiSpeed < 4f;
        }
        catch { return true; }
    }

    private static bool FinalBlowReturnPrefix(ref Task __result)
    {
        try
        {
            _inSimulateLoop = false;
            _currentFramesLeft = -1;
            ClearSmoothCooldownTracks();
            UpdateHighResTimer(false);

            if (_speedOverride < 4f && _manualUiSpeed < 4f)
            {
                return true;
            }

            // P1-45：跳过 ReturnToNormalSpeed 时同步复位 FinalBlowSlowDownController._currentState = Complete，防止下局切回 1x-3x 错乱
            ResetFinalBlowStateToComplete();
            SafeRestoreNormalTimeScale();
            __result = Task.CompletedTask;
            return false;
        }
        catch
        {
            return true;
        }
    }

    private static void SafeRestoreNormalTimeScale()
    {
        try
        {
            EnsureGameServiceManagerHandles();
            var inst = _gameServiceManagerInstanceProp?.GetValue(null);
            if (inst == null || (inst is UnityEngine.Object uo && uo == null))
            {
                if (Time.timeScale > 0f && Time.timeScale < 1f)
                {
                    Time.timeScale = 1f;
                }
                return;
            }

            bool isPaused = _gamePausedProp?.GetValue(inst) is bool gp && gp;
            if (!isPaused)
            {
                Time.timeScale = 1f;
                _enforceMaxTimeScaleMethod?.Invoke(inst, new object[] { 1f });
            }
        }
        catch { }
    }

    // ── 5. ReplayState 自动退出回放机制 ──────────────────────────────────────
    private static void PatchReplayAutoExit(SafeHarmonyPatcher patcher)
    {
        try
        {
            var replayType = AccessTools.TypeByName("TheBazaar.ReplayState");
            if (replayType == null) return;

            var onEnter = AccessTools.Method(replayType, "OnEnter");
            if (onEnter != null)
            {
                patcher.TryPatchMethod(
                    onEnter,
                    prefix: AccessTools.Method(typeof(CombatPatch), nameof(ReplayOnEnterPrefix)),
                    optional: true,
                    label: "ReplayState.OnEnter");
            }

            var updVic = AccessTools.Method(replayType, "UpdateVictoriesAndPrestige");
            if (updVic != null)
            {
                patcher.TryPatchMethod(
                    updVic,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(ReplayPostCombatUpdatedPostfix)),
                    optional: true,
                    label: "ReplayState.UpdateVictoriesAndPrestige");
            }
            BazaarLauncherPlugin.Log.LogInfo("Patched ReplayState (safe autoExitReplay hook)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchReplayAutoExit: " + ex.Message);
        }
    }

    private static void ReplayOnEnterPrefix()
    {
        try
        {
            _replayReadyToExit = false;
            _replayEnteredAt = Time.realtimeSinceStartup;
        }
        catch { }
    }

    private static void ReplayPostCombatUpdatedPostfix()
    {
        try { _replayReadyToExit = true; }
        catch { }
    }

    /// <summary>由 GameDriver.Update 每帧调用：在战后数据更新且看板/动画空闲时自动退出 ReplayState。</summary>
    internal static void TryAutoExitReplayOnUpdate()
    {
        if (!AutoExitReplay || !_replayReadyToExit) return;
        if (Time.realtimeSinceStartup - _replayEnteredAt < 0.04f) return;

        var cur = GameInspector.GetCurrentAppState();
        if (cur == null || cur.GetType().Name != "ReplayState")
        {
            _replayReadyToExit = false;
            return;
        }

        if (GameInspector.GetMember(cur, "_exitRequested") is bool exitReq && exitReq)
        {
            _replayReadyToExit = false;
            return;
        }

        var dataState = GameInspector.GetDataCurrentState();
        string runState = GameInspector.GetMember(dataState, "StateName")?.ToString() ?? "";
        if (string.Equals(runState, "Choice", StringComparison.OrdinalIgnoreCase)
            || runState is "EndRunVictory" or "EndRunDefeat" or "Shutdown")
        {
            _replayReadyToExit = false;
            return;
        }

        if (GameInspector.GetMember(cur, "IsReplaying") is bool isRep && isRep) return;
        if (GameInspector.IsClientBusy(out _)) return;
        if (GameInspector.HasCardsPendingRemoval()) return;

        _replayReadyToExit = false;
        try
        {
            var bm = GameInspector.GetBoardManagerCached();
            if (bm != null)
            {
                var forceExit = bm.GetType().GetMethod("ForceExitReplayState", BindingFlags.Public | BindingFlags.Instance);
                if (forceExit != null)
                {
                    forceExit.Invoke(bm, null);
                    return;
                }
            }
            cur.GetType().GetMethod("Exit", BindingFlags.Public | BindingFlags.Instance)?.Invoke(cur, null);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("TryAutoExitReplayOnUpdate: " + ex.Message);
        }
    }

    // ── 6. 战斗触发明细捕获（直接读取 NetMessageCombatSim 全量帧事件）─────────
    // （由 SimulateSpeedPrefix 按需调用 RecordCombatAudit，避免对 CombatSimHandler.Simulate 重复打补丁）

    private static void RecordCombatAudit(NetMessageCombatSim message)
    {
        var sim = message.Data;
        var frames = sim.Frames;

        var run = GameInspector.GetDataRun();
        uint day = 0, hour = 0;
        try
        {
            if (run != null)
            {
                day = Convert.ToUInt32(GameInspector.GetMember(run, "Day") ?? 0u);
                hour = Convert.ToUInt32(GameInspector.GetMember(run, "Hour") ?? 0u);
            }
        }
        catch { }

        string appStateName = GameInspector.GetAppStateName();
        bool isPvp = appStateName == "PVPCombatState";
        int seq = Interlocked.Increment(ref _combatSeq);

        var cardTrackers = new Dictionary<string, CardTriggerTracker>(StringComparer.Ordinal);
        var entities = GameInspector.GetDataEntities();
        if (entities != null)
        {
            foreach (DictionaryEntry kv in entities)
            {
                var card = kv.Value;
                if (card == null) continue;
                var owner = GameInspector.GetMember(card, "Owner");
                string ownerId = owner == null ? null : GameInspector.GetMember(owner, "CombatantId")?.ToString();
                if (!string.Equals(ownerId, "Player", StringComparison.OrdinalIgnoreCase)) continue;

                string type = GameInspector.GetMember(card, "Type")?.ToString() ?? "";
                string section = GameInspector.GetMember(card, "Section")?.ToString() ?? "";
                if (!string.Equals(type, "Skill", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(section, "Hand", StringComparison.OrdinalIgnoreCase))
                    continue;

                string iid = GameInspector.GetMember(card, "InstanceId")?.ToString() ?? "";
                if (string.IsNullOrEmpty(iid)) continue;

                int cdMax = 0;
                if (GameInspector.GetMember(card, "Attributes") is IDictionary attrs)
                {
                    foreach (DictionaryEntry akv in attrs)
                    {
                        if (akv.Key?.ToString() == "CooldownMax" && akv.Value is int v)
                        {
                            cdMax = v;
                            break;
                        }
                    }
                }

                string name = "";
                try
                {
                    var tpl = GameInspector.GetMember(card, "Template");
                    var loc = GameInspector.GetMember(tpl, "Localization");
                    var title = GameInspector.GetMember(loc, "Title");
                    name = GameInspector.GetMember(title, "Text") as string ?? GameInspector.GetMember(tpl, "InternalName")?.ToString() ?? "";
                }
                catch { }

                cardTrackers[iid] = new CardTriggerTracker
                {
                    InstanceId = iid,
                    TemplateId = GameInspector.GetMember(card, "TemplateId")?.ToString() ?? "",
                    Name = name,
                    Type = type,
                    Tier = GameInspector.GetMember(card, "Tier")?.ToString() ?? "",
                    Section = section,
                    Socket = GameInspector.GetMember(card, "LeftSocketId")?.ToString() ?? "",
                    CooldownMax = cdMax,
                };
            }
        }

        var allSourceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int totalEffectEvents = 0;
        int totalAuraEvents = 0;

        if (frames != null)
        {
            foreach (var frame in frames)
            {
                if (frame?.Events == null) continue;
                foreach (var ev in frame.Events)
                {
                    if (ev is CombatSimEventEffectExecuted eff)
                    {
                        totalEffectEvents++;
                        if (eff.Source.HasValue)
                        {
                            string sid = eff.Source.Value.ToString();
                            allSourceCounts[sid] = allSourceCounts.TryGetValue(sid, out int c) ? c + 1 : 1;
                            if (cardTrackers.TryGetValue(sid, out var tr))
                            {
                                tr.TriggerCount++;
                                tr.Actions.Add(eff.ActionType.ToString());
                            }
                        }
                        if (eff.TriggerSource.HasValue)
                        {
                            string tsid = eff.TriggerSource.Value.ToString();
                            if (cardTrackers.TryGetValue(tsid, out var ttr))
                            {
                                ttr.TriggerSourceCount++;
                            }
                        }
                    }
                    else if (ev is CombatSimEventEffectAuraExecuted aura)
                    {
                        totalAuraEvents++;
                        if (aura.Source.HasValue)
                        {
                            string sid = aura.Source.Value.ToString();
                            if (cardTrackers.TryGetValue(sid, out var tr))
                            {
                                tr.AuraCount++;
                            }
                        }
                    }
                }
            }
        }

        var cardList = new List<Dictionary<string, object>>(cardTrackers.Count);
        foreach (var tr in cardTrackers.Values)
        {
            cardList.Add(new Dictionary<string, object>
            {
                ["instanceId"] = tr.InstanceId,
                ["templateId"] = tr.TemplateId,
                ["name"] = tr.Name,
                ["type"] = tr.Type,
                ["tier"] = tr.Tier,
                ["section"] = tr.Section,
                ["socket"] = tr.Socket,
                ["cooldownMax"] = tr.CooldownMax,
                ["triggerCount"] = tr.TriggerCount,
                ["triggerSourceCount"] = tr.TriggerSourceCount,
                ["auraCount"] = tr.AuraCount,
                ["actions"] = new List<string>(tr.Actions),
            });
        }

        var audit = new Dictionary<string, object>
        {
            ["combatSeq"] = seq,
            ["day"] = day,
            ["hour"] = hour,
            ["isPvp"] = isPvp,
            ["winner"] = sim.Winner.ToString(),
            ["frameCount"] = frames?.Count ?? 0,
            ["totalEffectEvents"] = totalEffectEvents,
            ["totalAuraEvents"] = totalAuraEvents,
            ["playerCards"] = cardList,
            ["timestampUtc"] = DateTime.UtcNow.ToString("O"),
        };

        lock (_auditLock)
        {
            _lastCombatAudit = audit;
        }
        _auditHistory.Enqueue(audit);
        while (_auditHistory.Count > 32) _auditHistory.TryDequeue(out _);
    }

    internal static Dictionary<string, object> GetLastCombatAuditSnapshot()
    {
        lock (_auditLock)
        {
            return _lastCombatAudit;
        }
    }

    internal static List<Dictionary<string, object>> GetCombatAuditsSince(int sinceSeq)
    {
        var list = new List<Dictionary<string, object>>();
        foreach (var a in _auditHistory)
        {
            if (a.TryGetValue("combatSeq", out var sv) && Convert.ToInt32(sv) > sinceSeq)
            {
                list.Add(a);
            }
        }
        return list;
    }

    private sealed class CardTriggerTracker
    {
        public string InstanceId;
        public string TemplateId;
        public string Name;
        public string Type;
        public string Tier;
        public string Section;
        public string Socket;
        public int CooldownMax;
        public int TriggerCount;
        public int TriggerSourceCount;
        public int AuraCount;
        public readonly HashSet<string> Actions = new(StringComparer.Ordinal);
    }

    // ── 7. 屏蔽首次战败/连胜副教程弹窗，防止 CombatSim 收尾被 GamePaused 阻塞 ─
    private static void PatchDisableTutorials(SafeHarmonyPatcher patcher)
    {
        try
        {
            var tutType = AccessTools.TypeByName("TheBazaar.SequenceFramework.TutorialSequenceSpawnerComponent");
            if (tutType == null) return;

            var prop = tutType.GetProperty("SkipSecondaryTutorials", BindingFlags.Public | BindingFlags.Static);
            var setMethod = prop?.GetSetMethod(nonPublic: true);
            if (setMethod != null)
            {
                setMethod.Invoke(null, new object[] { true });
            }
            else
            {
                tutType.GetField("<SkipSecondaryTutorials>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, true);
            }

            var prefix = AccessTools.Method(typeof(CombatPatch), nameof(SkipCompletedTaskPrefix));
            foreach (var name in new[] { "PreloadSecondaryTutorial", "PreloadHeroTutorial" })
            {
                var m = AccessTools.Method(tutType, name);
                if (m != null && typeof(Task).IsAssignableFrom(m.ReturnType))
                {
                    patcher.TryPatchMethod(
                        m,
                        prefix: prefix,
                        optional: true,
                        label: "TutorialSequenceSpawnerComponent." + name);
                }
            }
            BazaarLauncherPlugin.Log.LogInfo("Patched TutorialSequenceSpawnerComponent (secondary & hero tutorials skipped)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchDisableTutorials: " + ex.Message);
        }
    }

    private static bool SkipCompletedTaskPrefix(ref Task __result)
    {
        try
        {
            __result = Task.CompletedTask;
            return false;
        }
        catch
        {
            return true;
        }
    }

    // ── 8. 战后 Freeze / Flying 客户端残留视觉清除双保险（#6、#27） ──────────

    private static Type _itemControllerType;
    private static FieldInfo _itemIsFlyingField;
    private static FieldInfo _itemCanFlyHashField;
    private static MethodInfo _itemStopCardFlyMethod;
    private static FieldInfo _itemAnimatorField;
    private static PropertyInfo _itemAnimatorProp;
    private static PropertyInfo _animControllerProp;
    private static PropertyInfo _animIsInitializedProp;
    private static MethodInfo _animGetBoolIntMethod;
    private static MethodInfo _animSetBoolIntMethod;
    private static PropertyInfo _itemVfxProp;

    // 按运行时真实 VFX 控制器类型隔离缓存反射成员，杜绝异构/派生 VFX 控制器跨类型强转风险（P2 加固 + P2-40 补全 Heated/Chilled/Disabled/Tempo）
    private sealed class VfxControllerMemberBag
    {
        public FieldInfo IsFreeze;
        public FieldInfo FreezeFxGo;
        public FieldInfo FreezeStartGo;
        public FieldInfo FreezeIdleGo;
        public FieldInfo FreezeEndGo;
        public FieldInfo FreezeRemoveGo;
        public FieldInfo FreezeTimer;
        public FieldInfo DisableFreezeCo;
        public MethodInfo UpdateFreeze;
        public FieldInfo IsSlow;
        public MethodInfo UpdateSlow;
        public FieldInfo IsHaste;
        public MethodInfo UpdateHaste;
        // P2-40：补全 Heated / Chilled / Disabled / Tempo 四种战斗视觉特效反射成员（全部对应 ItemVFXController.cs 精确字段/方法名）
        public FieldInfo IsHeated;
        public FieldInfo HeatedFxGo;
        public MethodInfo UpdateHeated;
        public FieldInfo IsChilled;
        public FieldInfo ChilledFxGo;
        public MethodInfo UpdateChilled;
        public FieldInfo IsDisabled;
        public FieldInfo DestroyEffect;
        public FieldInfo DisableDisabledCo;
        public MethodInfo UpdateDisable;
        public FieldInfo ActiveTempoSpend;
        public FieldInfo ActiveTempoGain;
        public FieldInfo DeactivateTempoSpendCo;
        public FieldInfo DeactivateTempoGainCo;
        public MethodInfo StopTempoCoroutines;
        public MethodInfo TempoEffectSetActive;
    }

    private static readonly ConcurrentDictionary<Type, VfxControllerMemberBag> _vfxMembersByType = new();
    private static bool _combatEndedListenersAdded;

    private static void PatchPostCombatVisualCleanup(SafeHarmonyPatcher patcher)
    {
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                var onCombatEnded = AccessTools.Method(bmType, "OnCombatEnded");
                if (onCombatEnded != null)
                {
                    patcher.TryPatchMethod(
                        onCombatEnded,
                        postfix: AccessTools.Method(typeof(CombatPatch), nameof(PostCombatVisualCleanupPostfix)),
                        optional: true,
                        label: "BoardManager.OnCombatEnded");
                }

                var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
                if (onStateChanged != null)
                {
                    patcher.TryPatchMethod(
                        onStateChanged,
                        postfix: AccessTools.Method(typeof(CombatPatch), nameof(StateChangedVisualCleanupPostfix)),
                        optional: true,
                        label: "BoardManager.OnStateChanged.CombatVisualCleanup");
                }
            }

            var replayType = GameInspector.FindTypeSafe("TheBazaar.ReplayState");
            var replayOnExit = replayType == null ? null : AccessTools.Method(replayType, "OnExit");
            if (replayOnExit != null)
            {
                patcher.TryPatchMethod(
                    replayOnExit,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(ReplayOnExitVisualCleanupPostfix)),
                    optional: true,
                    label: "ReplayState.OnExit");
            }

            RegisterStaticCombatEndedListeners();
            BazaarLauncherPlugin.Log?.LogInfo("Patched post-combat Freeze/Flying visual cleanup guards (#6/#27)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("PatchPostCombatVisualCleanup: " + ex.Message);
        }
    }

    private static Action _registeredCombatEndCallback;

    private static void RegisterStaticCombatEndedListeners()
    {
        if (_combatEndedListenersAdded) return;
        try
        {
            var eventsType = GameInspector.FindTypeSafe("TheBazaar.Events");
            if (eventsType == null) return;

            _registeredCombatEndCallback = () =>
            {
                CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: false);
                BazaarLauncherPlugin.Host?.EventBus.Publish(new BazaarModSdk.CombatLifecycleModEvent(false, false, false));
            };
            var monoType = typeof(MonoBehaviour);
            foreach (var evtName in new[] { "CombatEnded", "ReplayEnded" })
            {
                var evtObj = eventsType.GetField(evtName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (evtObj == null) continue;
                var addM = evtObj.GetType().GetMethod(
                    "AddListener",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(Action), monoType },
                    null)
                    ?? evtObj.GetType().GetMethod(
                        "AddListener",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(Action) },
                        null);
                if (addM != null)
                {
                    var ps = addM.GetParameters();
                    if (ps.Length == 2) addM.Invoke(evtObj, new object[] { _registeredCombatEndCallback, null });
                    else if (ps.Length == 1) addM.Invoke(evtObj, new object[] { _registeredCombatEndCallback });
                }
            }
            _combatEndedListenersAdded = true;
        }
        catch { }
    }

    internal static void UnregisterStaticCombatEndedListeners()
    {
        if (!_combatEndedListenersAdded || _registeredCombatEndCallback == null) return;
        try
        {
            var eventsType = GameInspector.FindTypeSafe("TheBazaar.Events");
            if (eventsType != null)
            {
                foreach (var evtName in new[] { "CombatEnded", "ReplayEnded" })
                {
                    var evtObj = eventsType.GetField(evtName, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (evtObj == null) continue;
                    var remM = evtObj.GetType().GetMethod(
                        "RemoveListener",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new[] { typeof(Action) },
                        null);
                    remM?.Invoke(evtObj, new object[] { _registeredCombatEndCallback });
                }
            }
        }
        catch { }
        finally
        {
            _combatEndedListenersAdded = false;
            _registeredCombatEndCallback = null;
        }
    }

    private static void PostCombatVisualCleanupPostfix()
    {
        try { CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: false); }
        catch { }
    }

    /// <summary>
    /// ReplayState.OnExit 为 async Task：立即清理一次，并在异步退出真正完成后回到主线程二次兜底清理。
    /// </summary>
    private static void ReplayOnExitVisualCleanupPostfix(Task __result)
    {
        try { CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: false); }
        catch { }

        if (__result != null && !__result.IsCompleted)
        {
            __result.ContinueWith(_ =>
            {
                BazaarLauncher.Core.MainThreadDispatcher.Enqueue(() =>
                {
                    try { CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: true); }
                    catch { }
                });
            }, TaskScheduler.Default);
        }
    }

    private static void StateChangedVisualCleanupPostfix(object obj)
    {
        try
        {
            if (obj == null) return;
            var cur = GameInspector.GetMember(obj, "CurrentState");
            string curName = cur?.GetType().Name ?? "";
            if (curName is "ChoiceState" or "EncounterState" or "PedestalState" or "LootState" or "LevelUpState")
            {
                CleanupResidualFreezeAndFlyVisuals(onlyWhenOutOfCombat: true);
            }
        }
        catch { }
    }

    private static void CleanupResidualFreezeAndFlyVisuals(bool onlyWhenOutOfCombat)
    {
        try
        {
            if (onlyWhenOutOfCombat)
            {
                var curState = GameInspector.GetCurrentAppState();
                string stName = curState?.GetType().Name ?? "";
                if (stName is "CombatState" or "PVPCombatState")
                    return;
                if (stName == "ReplayState" && GameInspector.GetMember(curState, "IsReplaying") is bool rep && rep)
                    return;
            }

            var run = GameInspector.GetDataRun();
            var player = run != null ? GameInspector.GetMember(run, "Player") : null;
            var lookup = GameInspector.GetDataLookup();
            if (player == null || lookup == null) return;

            if (GameInspector.GetMember(lookup, "CardControllerDictionary") is not IDictionary dict || dict.Count == 0)
                return;

            _itemControllerType ??= GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            if (_itemControllerType == null) return;

            foreach (DictionaryEntry kv in dict)
            {
                var ctrl = kv.Value;
                if (ctrl == null || (ctrl is UnityEngine.Object uo && uo == null)) continue;
                if (!_itemControllerType.IsInstanceOfType(ctrl)) continue;

                var cardData = GameInspector.GetMember(ctrl, "CardData");
                if (cardData == null) continue;

                // 仅处理己方场上/可见 ItemController（防止误操作对手或未激活池对象）
                var owner = GameInspector.GetMember(cardData, "Owner");
                if (!ReferenceEquals(owner, player)) continue;

                bool visible = GameInspector.GetMember(ctrl, "IsCardVisible") is bool v && v;
                if (!visible && (ctrl is not Component comp || comp.gameObject == null || !comp.gameObject.activeInHierarchy))
                    continue;

                var attrs = GameInspector.GetMember(cardData, "Attributes") as IDictionary;
                int freezeVal = ReadCardAttrInt(attrs, ECardAttributeType.Freeze);
                int flyingVal = ReadCardAttrInt(attrs, ECardAttributeType.Flying);
                int slowVal = ReadCardAttrInt(attrs, ECardAttributeType.Slow);
                int hasteVal = ReadCardAttrInt(attrs, ECardAttributeType.Haste);
                int heatedVal = ReadCardAttrInt(attrs, ECardAttributeType.Heated);
                int chilledVal = ReadCardAttrInt(attrs, ECardAttributeType.Chilled);
                bool isCardStateDisabled = GameInspector.GetMember(cardData, "State") is ECardState st && st == ECardState.Disabled;

                if (freezeVal <= 0)
                {
                    ClearItemFreezeVfx(ctrl);
                }
                if (flyingVal <= 0)
                {
                    ClearItemFlyVisual(ctrl);
                }
                // P2-40：战后同步清扫 Heated / Chilled / Disabled / Tempo 四种残留战斗特效（仅当卡牌自身已非该状态）
                if (heatedVal <= 0)
                {
                    ClearItemHeatedVfx(ctrl);
                }
                if (chilledVal <= 0)
                {
                    ClearItemChilledVfx(ctrl);
                }
                if (!isCardStateDisabled)
                {
                    ClearItemDisabledVfx(ctrl);
                }
                ClearItemTempoVfx(ctrl);

                if (onlyWhenOutOfCombat)
                {
                    if (slowVal <= 0) ClearItemSlowVfx(ctrl);
                    if (hasteVal <= 0) ClearItemHasteVfx(ctrl);
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("CleanupResidualFreezeAndFlyVisuals: " + ex.Message);
        }
    }

    private static int ReadCardAttrInt(IDictionary attrs, ECardAttributeType attrType)
    {
        if (attrs == null) return 0;
        try
        {
            if (attrs.Contains(attrType) && attrs[attrType] is int iv)
                return iv;
        }
        catch { }
        return 0;
    }

    private static VfxControllerMemberBag GetVfxMembers(Type runtimeType)
    {
        return _vfxMembersByType.GetOrAdd(runtimeType, static vt =>
        {
            var tempoSpendField = vt.GetField("_activeTempoSpend", BindingFlags.NonPublic | BindingFlags.Instance);
            var tempoGainField = vt.GetField("_activeTempoGain", BindingFlags.NonPublic | BindingFlags.Instance);
            var tempoType = tempoSpendField?.FieldType ?? tempoGainField?.FieldType;
            return new VfxControllerMemberBag
            {
                IsFreeze = vt.GetField("isFreeze", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeFxGo = vt.GetField("freezeFXGO", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeStartGo = vt.GetField("freezeFXStateStartGO", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeIdleGo = vt.GetField("freezeFXStateIdleGO", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeEndGo = vt.GetField("freezeFXStateEndGO", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeRemoveGo = vt.GetField("freezeFXStateRemoveGO", BindingFlags.NonPublic | BindingFlags.Instance),
                FreezeTimer = vt.GetField("freezeTimer", BindingFlags.NonPublic | BindingFlags.Instance),
                DisableFreezeCo = vt.GetField("disableFreezeFX", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateFreeze = vt.GetMethod("UpdateFreezeVFX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                IsSlow = vt.GetField("isSlow", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateSlow = vt.GetMethod("UpdateSlow", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                IsHaste = vt.GetField("isHaste", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateHaste = vt.GetMethod("UpdateHaste", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance),
                IsHeated = vt.GetField("isHeated", BindingFlags.NonPublic | BindingFlags.Instance),
                HeatedFxGo = vt.GetField("heatedFXGO", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateHeated = vt.GetMethod("UpdateHeatedVFX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(float) }, null),
                IsChilled = vt.GetField("isChilled", BindingFlags.NonPublic | BindingFlags.Instance),
                ChilledFxGo = vt.GetField("chilledFXGO", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateChilled = vt.GetMethod("UpdateChilledVFX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(float) }, null),
                IsDisabled = vt.GetField("isDisabled", BindingFlags.NonPublic | BindingFlags.Instance),
                DestroyEffect = vt.GetField("destroyEffect", BindingFlags.NonPublic | BindingFlags.Instance),
                DisableDisabledCo = vt.GetField("disableDisabledFX", BindingFlags.NonPublic | BindingFlags.Instance),
                UpdateDisable = vt.GetMethod("UpdateDisableVFX", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null),
                ActiveTempoSpend = tempoSpendField,
                ActiveTempoGain = tempoGainField,
                DeactivateTempoSpendCo = vt.GetField("_deactivateTempoSpendCoroutine", BindingFlags.NonPublic | BindingFlags.Instance),
                DeactivateTempoGainCo = vt.GetField("_deactivateTempoGainCoroutine", BindingFlags.NonPublic | BindingFlags.Instance),
                StopTempoCoroutines = vt.GetMethod("StopTempoItemEffectCoroutines", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null),
                TempoEffectSetActive = tempoType?.GetMethod("SetActive", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(bool) }, null),
            };
        });
    }

    private static void ClearItemFreezeVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());

        bool isFreeze = bag.IsFreeze?.GetValue(vfx) is bool f && f;
        var freezeGo = bag.FreezeFxGo?.GetValue(vfx) as GameObject;
        var freezeIdleGo = bag.FreezeIdleGo?.GetValue(vfx) as GameObject;
        var freezeTimer = bag.FreezeTimer?.GetValue(vfx) as TMP_Text;

        bool hasResidual = isFreeze
            || (freezeGo != null && freezeGo.activeSelf)
            || (freezeIdleGo != null && freezeIdleGo.activeSelf)
            || (freezeTimer != null && freezeTimer.gameObject.activeSelf);

        if (!hasResidual) return;

        if (bag.DisableFreezeCo?.GetValue(vfx) is Coroutine co && co != null && vfx is MonoBehaviour mb && mb != null)
        {
            mb.StopCoroutine(co);
            bag.DisableFreezeCo.SetValue(vfx, null);
        }

        if (vfx is MonoBehaviour vmb && vmb != null && vmb.gameObject != null && vmb.gameObject.activeInHierarchy)
        {
            bag.UpdateFreeze?.Invoke(vfx, new object[] { 0f });
            if (bag.DisableFreezeCo?.GetValue(vfx) is Coroutine co2 && co2 != null)
            {
                vmb.StopCoroutine(co2);
                bag.DisableFreezeCo.SetValue(vfx, null);
            }
        }

        if (freezeGo != null) freezeGo.SetActive(false);
        if (bag.FreezeStartGo?.GetValue(vfx) is GameObject sGo && sGo != null) sGo.SetActive(false);
        if (freezeIdleGo != null) freezeIdleGo.SetActive(false);
        if (bag.FreezeEndGo?.GetValue(vfx) is GameObject eGo && eGo != null) eGo.SetActive(false);
        if (bag.FreezeRemoveGo?.GetValue(vfx) is GameObject rGo && rGo != null) rGo.SetActive(false);

        if (freezeTimer != null)
        {
            freezeTimer.text = string.Empty;
            freezeTimer.gameObject.SetActive(false);
        }
        bag.IsFreeze?.SetValue(vfx, false);
    }

    private static void ClearItemSlowVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        if (bag.IsSlow?.GetValue(vfx) is bool s && s)
        {
            bag.UpdateSlow?.Invoke(vfx, new object[] { 0f });
        }
    }

    private static void ClearItemHasteVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        if (bag.IsHaste?.GetValue(vfx) is bool h && h)
        {
            bag.UpdateHaste?.Invoke(vfx, new object[] { 0f });
        }
    }

    private static void ClearItemHeatedVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        bool isHeated = bag.IsHeated?.GetValue(vfx) is bool h && h;
        var heatedGo = bag.HeatedFxGo?.GetValue(vfx) as GameObject;
        if (!isHeated && (heatedGo == null || !heatedGo.activeSelf)) return;

        bag.UpdateHeated?.Invoke(vfx, new object[] { 0f });
        if (heatedGo != null && heatedGo.activeSelf) heatedGo.SetActive(false);
        bag.IsHeated?.SetValue(vfx, false);
    }

    private static void ClearItemChilledVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        bool isChilled = bag.IsChilled?.GetValue(vfx) is bool c && c;
        var chilledGo = bag.ChilledFxGo?.GetValue(vfx) as GameObject;
        if (!isChilled && (chilledGo == null || !chilledGo.activeSelf)) return;

        bag.UpdateChilled?.Invoke(vfx, new object[] { 0f });
        if (chilledGo != null && chilledGo.activeSelf) chilledGo.SetActive(false);
        bag.IsChilled?.SetValue(vfx, false);
    }

    private static void ClearItemDisabledVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        bool isDisabled = bag.IsDisabled?.GetValue(vfx) is bool d && d;
        var destroyFx = bag.DestroyEffect?.GetValue(vfx) as Component;
        bool destroyActive = destroyFx != null && destroyFx.gameObject != null && destroyFx.gameObject.activeSelf;
        if (!isDisabled && !destroyActive) return;

        if (bag.DisableDisabledCo?.GetValue(vfx) is Coroutine co && co != null && vfx is MonoBehaviour mb && mb != null)
        {
            mb.StopCoroutine(co);
            bag.DisableDisabledCo.SetValue(vfx, null);
        }
        if (destroyFx != null)
        {
            bag.UpdateDisable?.Invoke(vfx, new object[] { false });
            if (destroyFx.gameObject != null && destroyFx.gameObject.activeSelf)
            {
                destroyFx.gameObject.SetActive(false);
            }
        }
        bag.IsDisabled?.SetValue(vfx, false);
    }

    private static void ClearItemTempoVfx(object itemCtrl)
    {
        _itemVfxProp ??= _itemControllerType.GetProperty("CardVFXController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var vfx = _itemVfxProp?.GetValue(itemCtrl) ?? GameInspector.GetMember(itemCtrl, "cardVFXController");
        if (vfx == null || (vfx is UnityEngine.Object vuo && vuo == null)) return;

        var bag = GetVfxMembers(vfx.GetType());
        var spendCo = bag.DeactivateTempoSpendCo?.GetValue(vfx) as Coroutine;
        var gainCo = bag.DeactivateTempoGainCo?.GetValue(vfx) as Coroutine;
        var spendFx = bag.ActiveTempoSpend?.GetValue(vfx);
        var gainFx = bag.ActiveTempoGain?.GetValue(vfx);

        if (spendCo != null || gainCo != null)
        {
            bag.StopTempoCoroutines?.Invoke(vfx, null);
        }
        if (spendFx != null && bag.TempoEffectSetActive != null)
        {
            bag.TempoEffectSetActive.Invoke(spendFx, new object[] { false });
        }
        if (gainFx != null && bag.TempoEffectSetActive != null)
        {
            bag.TempoEffectSetActive.Invoke(gainFx, new object[] { false });
        }
    }

    private static void ClearItemFlyVisual(object itemCtrl)
    {
        _itemIsFlyingField ??= _itemControllerType.GetField("_isFlying", BindingFlags.NonPublic | BindingFlags.Instance);
        _itemStopCardFlyMethod ??= _itemControllerType.GetMethod("StopCardFly", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_itemIsFlyingField?.GetValue(itemCtrl) is bool isFly && isFly)
        {
            _itemStopCardFlyMethod?.Invoke(itemCtrl, null);
        }

        // 双重保险：若 Animator 布尔残留 true 但 _isFlying 为 false，仍强制拉回 false
        // 注意：CardController.Animator 为公有字段（非属性），优先按字段查找并保留属性兜底
        _itemAnimatorField ??= AccessTools.Field(_itemControllerType, "Animator");
        object animObj = _itemAnimatorField?.GetValue(itemCtrl);
        if (animObj == null)
        {
            _itemAnimatorProp ??= _itemControllerType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            animObj = _itemAnimatorProp?.GetValue(itemCtrl);
        }
        if (animObj != null && animObj is Behaviour animBhv && animBhv != null && animBhv.isActiveAndEnabled)
        {
            var animType = animObj.GetType();
            _animControllerProp ??= animType.GetProperty("runtimeAnimatorController", BindingFlags.Public | BindingFlags.Instance);
            _animIsInitializedProp ??= animType.GetProperty("isInitialized", BindingFlags.Public | BindingFlags.Instance);
            if (_animControllerProp?.GetValue(animObj) == null) return;
            if (_animIsInitializedProp?.GetValue(animObj) is bool init && !init) return;

            if (_itemCanFlyHashField == null)
            {
                _itemCanFlyHashField = _itemControllerType.GetField("_canFlyHash", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_itemCanFlyHashField?.GetValue(itemCtrl) is not int hash) return;

            _animGetBoolIntMethod ??= animType.GetMethod("GetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            _animSetBoolIntMethod ??= animType.GetMethod("SetBool", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(bool) }, null);
            try
            {
                if (_animGetBoolIntMethod?.Invoke(animObj, new object[] { hash }) is bool canFly && canFly)
                {
                    _animSetBoolIntMethod?.Invoke(animObj, new object[] { hash, false });
                }
            }
            catch { }
        }
    }

    // ── 8. 战斗读条 60/120+ FPS 丝滑线性插值（消除 50ms 离散步进视觉卡顿） ──

    private static void EnsureSmoothTickerInstalled()
    {
        try
        {
            if (_smoothTickerGo != null) return;
            _smoothTickerGo = new GameObject("BazaarCooldownSmoothTicker");
            UnityEngine.Object.DontDestroyOnLoad(_smoothTickerGo);
            _smoothTickerGo.AddComponent<CooldownSmoothTicker>();
        }
        catch { }
    }

    private static void ClearSmoothCooldownTracks()
    {
        _cdTracks.Clear();
        _cdDeadKeys.Clear();
    }

    private static void PatchSmoothCooldownBars(SafeHarmonyPatcher patcher)
    {
        try
        {
            var cdType = AccessTools.TypeByName("TheBazaar.Game.Cards.ItemVFXController+CooldownEffect");
            if (cdType == null) return;

            _cdAnimatedTransformField = AccessTools.Field(cdType, "animatedTransform");
            var updM = AccessTools.Method(cdType, "UpdatePosition", new[] { typeof(float), typeof(float) });
            var resetM = AccessTools.Method(cdType, "ResetPosition", Type.EmptyTypes);
            var showM = AccessTools.Method(cdType, "Show", new[] { typeof(bool) });
            var cleanM = AccessTools.Method(cdType, "Cleanup", Type.EmptyTypes);

            if (updM != null && _cdAnimatedTransformField != null)
            {
                patcher.TryPatchMethod(
                    updM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectUpdatePositionPostfix)),
                    optional: true,
                    label: "CooldownEffect.UpdatePosition");
            }
            if (resetM != null && _cdAnimatedTransformField != null)
            {
                patcher.TryPatchMethod(
                    resetM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectDropTrackPostfix)),
                    optional: true,
                    label: "CooldownEffect.ResetPosition");
            }
            if (showM != null && _cdAnimatedTransformField != null)
            {
                patcher.TryPatchMethod(
                    showM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectShowPostfix)),
                    optional: true,
                    label: "CooldownEffect.Show");
            }
            if (cleanM != null && _cdAnimatedTransformField != null)
            {
                patcher.TryPatchMethod(
                    cleanM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectDropTrackPostfix)),
                    optional: true,
                    label: "CooldownEffect.Cleanup");
            }
            BazaarLauncherPlugin.Log?.LogInfo("Patched ItemVFXController.CooldownEffect (60/120+ FPS smooth cooldown bar interpolation)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("PatchSmoothCooldownBars: " + ex.Message);
        }
    }

    private static void CooldownEffectUpdatePositionPostfix(object __instance, float newValue, float maxValue)
    {
        try
        {
            if (!_inSimulateLoop || __instance == null || _cdAnimatedTransformField == null || maxValue <= 0f)
                return;

            var tr = _cdAnimatedTransformField.GetValue(__instance) as Transform;
            if (tr == null) return;

            int key = tr.GetInstanceID();
            float targetY = Mathf.InverseLerp(0f, maxValue, maxValue - newValue) * 2f - 1f;
            float now = Time.unscaledTime;

            if (!_cdTracks.TryGetValue(key, out var track))
            {
                track = new CooldownBarTrack
                {
                    Transform = tr,
                    CurrentY = targetY,
                    LastTargetY = targetY,
                    VelocityY = 0f,
                    LastSampleTime = now,
                    HasVelocity = false,
                    Active = true,
                };
                _cdTracks[key] = track;
                return;
            }

            track.Transform = tr;
            track.Active = true;

            // 开火重置（newValue == maxValue 或倒退跳变）或大额瞬时充能跃变：直接瞬切不插值
            bool isReset = Mathf.Abs(newValue - maxValue) <= 0.5f || targetY < track.LastTargetY - 0.06f;
            bool isLargeChargeJump = targetY - track.LastTargetY > 0.35f;
            if (isReset || isLargeChargeJump)
            {
                track.CurrentY = targetY;
                track.LastTargetY = targetY;
                track.VelocityY = 0f;
                track.HasVelocity = false;
                track.LastSampleTime = now;
                return;
            }

            float dt = now - track.LastSampleTime;
            float deltaY = targetY - track.LastTargetY;

            // 冰冻或停滞（冷却未前进）：立即刹停，不再继续外推
            if (deltaY <= 0.0001f)
            {
                track.CurrentY = targetY;
                track.LastTargetY = targetY;
                track.VelocityY = 0f;
                track.HasVelocity = false;
                track.LastSampleTime = now;
                return;
            }

            if (dt >= 0.005f && dt <= 0.30f)
            {
                float sampleVel = deltaY / dt;
                track.VelocityY = track.HasVelocity
                    ? Mathf.Lerp(track.VelocityY, sampleVel, 0.55f)
                    : sampleVel;
                track.HasVelocity = true;
            }
            else if (dt > 0.30f)
            {
                track.VelocityY = 0f;
                track.HasVelocity = false;
                track.CurrentY = targetY;
            }

            track.LastTargetY = targetY;
            track.LastSampleTime = now;

            // 防止回拉抖动：若平滑外推位置与最新采样点误差在 1.5 帧步长内，保留平滑进度并由 Tick 温和收敛
            float err = targetY - track.CurrentY;
            if (Mathf.Abs(err) < 0.14f)
            {
                var lp = tr.localPosition;
                lp.y = track.CurrentY;
                tr.localPosition = lp;
            }
            else
            {
                track.CurrentY = targetY;
            }
        }
        catch { }
    }

    private static void CooldownEffectShowPostfix(object __instance, bool show)
    {
        if (!show) CooldownEffectDropTrackPostfix(__instance);
    }

    private static void CooldownEffectDropTrackPostfix(object __instance)
    {
        try
        {
            if (__instance == null || _cdAnimatedTransformField == null) return;
            var tr = _cdAnimatedTransformField.GetValue(__instance) as Transform;
            if (tr != null)
            {
                _cdTracks.Remove(tr.GetInstanceID());
            }
        }
        catch { }
    }

    private static bool IsGameCurrentlyPaused()
    {
        try
        {
            // P1-45：GameServiceManager 位于全局命名空间且继承 Singleton<GameServiceManager>，GamePaused 为实例属性，
            // 必须经 Singleton.Instance 读取，避免每帧 GetValue(null) 抛出 TargetException
            EnsureGameServiceManagerHandles();
            var inst = _gameServiceManagerInstanceProp?.GetValue(null);
            if (inst != null && !(inst is UnityEngine.Object uo && uo == null) && _gamePausedProp != null)
            {
                if (_gamePausedProp.GetValue(inst) is bool paused)
                    return paused;
            }
        }
        catch { }
        return Time.timeScale <= 0.001f;
    }

    private static void TickSmoothCooldownBars()
    {
        if (!BazaarLauncher.Modules.PerfOptimizerModule.OptCombatHighSpeedSmooth) return;
        if (!_inSimulateLoop || _cdTracks.Count == 0) return;
        if (IsGameCurrentlyPaused()) return;

        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f || dt > 0.1f) return;

        float now = Time.unscaledTime;
        _cdDeadKeys.Clear();

        foreach (var kv in _cdTracks)
        {
            var t = kv.Value;
            if (!t.Active || t.Transform == null)
            {
                _cdDeadKeys.Add(kv.Key);
                continue;
            }
            if (t.VelocityY <= 0.0001f) continue;

            // 若超过 120ms 未收到新的服务端帧（如终结慢镜停顿或冰冻），自动刹车避免冲过头
            float sinceSample = now - t.LastSampleTime;
            if (sinceSample > 0.12f) continue;

            float maxLeadY = Mathf.Min(1f, t.LastTargetY + t.VelocityY * 0.065f);
            // 带轻微相位校正（向 LastTargetY + VelocityY * sinceSample 收敛），既丝滑又零累计漂移
            float idealY = Mathf.Min(maxLeadY, t.LastTargetY + t.VelocityY * sinceSample);
            float stepY = t.CurrentY + t.VelocityY * dt;
            t.CurrentY = Mathf.Clamp(Mathf.Lerp(stepY, idealY, 0.35f), -1f, maxLeadY);

            var lp = t.Transform.localPosition;
            lp.y = t.CurrentY;
            t.Transform.localPosition = lp;
        }

        for (int i = 0; i < _cdDeadKeys.Count; i++)
        {
            _cdTracks.Remove(_cdDeadKeys[i]);
        }
    }

    // ── 9. 冷却时间 Tooltip 精准显示（亚十分位如 1980ms 显示 1.98，整 0.1s 保持原版 2.0）──

    private static void PatchPrecisionCooldownTooltip(SafeHarmonyPatcher patcher)
    {
        try
        {
            var rendererType = AccessTools.TypeByName("TheBazaar.UI.Tooltips.CooldownRenderer");
            var tooltipDataType = AccessTools.TypeByName("TheBazaar.Tooltips.CardTooltipData");
            if (rendererType == null || tooltipDataType == null) return;

            var renderM = AccessTools.Method(rendererType, "RenderFromTooltip");
            _cdRendererSetCooldownMethod = AccessTools.Method(rendererType, "SetCooldown", new[] { typeof(string), typeof(bool), typeof(string) });
            _tooltipGetCurAttrMethod = AccessTools.Method(tooltipDataType, "GetCurrentAttributeValue", new[] { typeof(ECardAttributeType) });
            _tooltipNextTierHasAttrMethod = AccessTools.Method(tooltipDataType, "NextTierHasAttribute", new[] { typeof(ECardAttributeType) });
            _tooltipGetBaseFromTplMethod = AccessTools.Method(tooltipDataType, "GetBaseValueFromCardTemplate",
                new[] { typeof(ECardAttributeType), typeof(ETier), typeof(float), typeof(ETier) });

            if (renderM != null && _cdRendererSetCooldownMethod != null && _tooltipGetCurAttrMethod != null)
            {
                if (patcher.TryPatchMethod(
                    renderM,
                    postfix: AccessTools.Method(typeof(CombatPatch), nameof(CooldownRendererRenderPostfix)),
                    optional: true,
                    label: "CooldownRenderer.RenderFromTooltip"))
                {
                    BazaarLauncherPlugin.Log?.LogInfo("Patched CooldownRenderer.RenderFromTooltip (sub-0.1s precision display, e.g. 1.98s)");
                }
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("PatchPrecisionCooldownTooltip: " + ex.Message);
        }
    }

    private static string FormatCooldownSecondsSmart(float seconds)
    {
        int ms = Mathf.RoundToInt(seconds * 1000f);
        // 整 0.1 秒（如 2000ms / 3500ms）保持官方 F1（2.0 / 3.5）；存在百分位缩减（如 1980ms）显示 F2（1.98）
        return (ms % 100) != 0 ? $"{seconds:F2}" : $"{seconds:F1}";
    }

    private static void CooldownRendererRenderPostfix(object __instance, object tooltipData, bool canFuse, ETier nextTier, ETier currentTier)
    {
        try
        {
            if (__instance == null || tooltipData == null || _tooltipGetCurAttrMethod == null || _cdRendererSetCooldownMethod == null)
                return;

            object curObj = _tooltipGetCurAttrMethod.Invoke(tooltipData, new object[] { ECardAttributeType.CooldownMax });
            if (curObj is not float curSec || curSec <= 0f) return;

            int curMs = Mathf.RoundToInt(curSec * 1000f);
            bool curNeedsF2 = (curMs % 100) != 0;

            bool fusedHasAttr = false;
            float fusedSec = 0f;
            bool fusedNeedsF2 = false;
            if (canFuse && _tooltipNextTierHasAttrMethod != null && _tooltipGetBaseFromTplMethod != null)
            {
                if (_tooltipNextTierHasAttrMethod.Invoke(tooltipData, new object[] { ECardAttributeType.CooldownMax }) is bool has && has)
                {
                    object fObj = _tooltipGetBaseFromTplMethod.Invoke(tooltipData, new object[] { ECardAttributeType.CooldownMax, nextTier, curSec, currentTier });
                    if (fObj is float fs && fs > 0f)
                    {
                        fusedHasAttr = true;
                        fusedSec = fs;
                        fusedNeedsF2 = (Mathf.RoundToInt(fs * 1000f) % 100) != 0;
                    }
                }
            }

            if (!curNeedsF2 && !fusedNeedsF2) return;

            string curText = FormatCooldownSecondsSmart(curSec);
            string fusedText = fusedHasAttr ? FormatCooldownSecondsSmart(fusedSec) : string.Empty;
            _cdRendererSetCooldownMethod.Invoke(__instance, new object[] { curText, canFuse && fusedHasAttr, fusedText });
        }
        catch { }
    }
}

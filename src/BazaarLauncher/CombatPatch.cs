using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Core.Types;
using BazaarGameShared.Infra.Messages;
using BazaarGameShared.Infra.Messages.CombatSimEvents;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace BazaarLauncher;

/// <summary>
/// 战斗流程增强、首战加速修复、1x/2x/3x 手动倍速稳定控制、高倍速流畅化与战斗触发明细审计补丁：
///
///   1. CombatState.GoToNextState 空值兜底（优先取 LastCombatSequence.DespawnMessage）；
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
        UnregisterStaticCombatEndedListeners();
    }

    internal static void Apply(BazaarLauncher.Core.SafeHarmonyPatcher patcher)
    {
        if (patcher == null) return;
        Apply(patcher.RawHarmony);
    }

    internal static void Apply(Harmony harmony)
    {
        if (_applied) return;
        _applied = true;
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _unitySyncContext ??= SynchronizationContext.Current;

        PatchCombatStateGoNext(harmony);
        PatchCombatSimSpeed(harmony);
        PatchOptionsFastForwardSync(harmony);
        PatchFinalBlowSlowDown(harmony);
        PatchGameServiceManagerTimeScale(harmony);
        // P1-5 修复：不再对 CombatSimHandler.Simulate 重复打第 2 个补丁，条件审计已合入 SimulateSpeedPrefix
        PatchReplayAutoExit(harmony);
        PatchPostCombatVisualCleanup(harmony);
        PatchDisableTutorials(harmony);
        PatchSmoothCooldownBars(harmony);
        PatchPrecisionCooldownTooltip(harmony);
    }

    // ── 1. CombatState.GoToNextState 空值兜底 ───────────────────────────────
    private static void PatchCombatStateGoNext(Harmony harmony)
    {
        var combatState = AccessTools.TypeByName("TheBazaar.CombatState");
        var go = combatState == null ? null : AccessTools.Method(combatState, "GoToNextState");
        if (go == null) return;

        harmony.Patch(go, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(CombatPatch), nameof(GoToNextStatePrefix))));
        BazaarLauncherPlugin.Log.LogInfo("Patched CombatState.GoToNextState (null-safe)");
    }

    private static bool GoToNextStatePrefix()
    {
        try
        {
            var appStateType = AccessTools.TypeByName("TheBazaar.AppState");
            var cachedGameSim = appStateType?
                .GetField("_cachedGameSim", BindingFlags.NonPublic | BindingFlags.Static)?
                .GetValue(null);

            if (cachedGameSim != null) return true;

            var nmType = AccessTools.TypeByName("Networking.NetworkManager");
            var getInstance = nmType?.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.Static);
            var nm = getInstance?.Invoke(null, null);
            var lastSeq = nm?.GetType().GetProperty("LastCombatSequence", BindingFlags.Public | BindingFlags.Instance)?.GetValue(nm);
            var despawn = lastSeq?.GetType().GetField("DespawnMessage", BindingFlags.Public | BindingFlags.Instance)?.GetValue(lastSeq);
            if (despawn != null)
            {
                appStateType?.GetField("_cachedGameSim", BindingFlags.NonPublic | BindingFlags.Static)?.SetValue(null, despawn);
                return true;
            }

            BazaarLauncherPlugin.Log.LogWarning("GoToNextState: no cached GameSim or DespawnMessage, forcing Loot");
            var erunState = AccessTools.TypeByName("BazaarGameShared.Domain.Runs.ERunState");
            var setState = appStateType?.GetMethod("SetState",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null, new[] { erunState }, null);

            if (setState != null)
            {
                var loot = Enum.Parse(erunState, "Loot");
                _ = setState.Invoke(null, new[] { loot }) as Task;
            }
            return false;
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("GoToNextStatePrefix: " + ex.Message);
            return true;
        }
    }

    // ── 2. CombatSimHandler 首战加速与倍速稳定控制 ──────────────────────────
    private static void PatchCombatSimSpeed(Harmony harmony)
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
                harmony.Patch(_setSpeedMethod,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CombatPatch), nameof(SetSpeedPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CombatPatch), nameof(SetSpeedPostfix))));
            }

            var shouldFf = AccessTools.Method(handlerType, "ShouldFastForward");
            if (shouldFf != null)
            {
                harmony.Patch(shouldFf, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(ShouldFastForwardPostfix))));
            }

            var simMethod = AccessTools.Method(handlerType, "Simulate", new[] { typeof(NetMessageCombatSim), typeof(CancellationTokenSource) });
            if (simMethod != null)
            {
                harmony.Patch(simMethod,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(CombatPatch), nameof(SimulateSpeedPrefix))),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(CombatPatch), nameof(SimulateSpeedPostfix))));
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

            // 若战斗被提前取消或慢镜未完全恢复，保证退出后不会卡在低 TimeScale 或残留高 SpeedMultiplier
            if (_speedOverride >= 4f || _manualUiSpeed >= 4f || (Time.timeScale > 0.01f && Time.timeScale < 0.99f))
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
    private static void PatchOptionsFastForwardSync(Harmony harmony)
    {
        try
        {
            var optType = AccessTools.TypeByName("OptionsDialogController");
            if (optType == null) return;

            var awakeM = AccessTools.Method(optType, "Awake");
            if (awakeM != null)
            {
                harmony.Patch(awakeM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(OptionsAwakePostfix))));
            }

            var changedM = AccessTools.Method(optType, "OnFastForwardFirstFightChanged", new[] { typeof(bool) });
            if (changedM != null)
            {
                harmony.Patch(changedM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(OptionsFastForwardChangedPostfix))));
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
    private static void PatchFinalBlowSlowDown(Harmony harmony)
    {
        try
        {
            var fbType = AccessTools.TypeByName("TheBazaar.FinalBlowSlowDownController");
            if (fbType == null) return;

            var processM = AccessTools.Method(fbType, "Process", new[] { typeof(int) });
            if (processM != null)
            {
                harmony.Patch(processM, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(FinalBlowProcessPrefix))));
            }

            var returnM = AccessTools.Method(fbType, "ReturnToNormalSpeed");
            if (returnM != null)
            {
                harmony.Patch(returnM, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(FinalBlowReturnPrefix))));
            }
            BazaarLauncherPlugin.Log.LogInfo("Patched FinalBlowSlowDownController (smooth 1x-3x slowmo preserved, >=4x fast skip)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchFinalBlowSlowDown: " + ex.Message);
        }
    }

    private static void PatchGameServiceManagerTimeScale(Harmony harmony)
    {
        try
        {
            var gsmType = _gameServiceManagerType ??= GameInspector.FindTypeSafe("GameServiceManager", "TheBazaar.GameServiceManager");
            if (gsmType == null) return;

            _enforceMaxTimeScaleMethod = AccessTools.Method(gsmType, "EnforceMaxTimeScale", new[] { typeof(float) });
            _gamePausedProp = AccessTools.Property(gsmType, "GamePaused");

            if (_enforceMaxTimeScaleMethod != null)
            {
                harmony.Patch(_enforceMaxTimeScaleMethod, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(EnforceMaxTimeScalePrefix))));
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
            // 自动化极速模式（SpeedOverride >= 4x）禁止 HitStun / 慢镜压低全局 TimeScale
            if (_speedOverride >= 4f && maximum < 1f)
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
            var gsmType = _gameServiceManagerType ??= GameInspector.FindTypeSafe("GameServiceManager", "TheBazaar.GameServiceManager");
            var inst = gsmType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)?.GetValue(null);
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
                (_enforceMaxTimeScaleMethod ?? gsmType.GetMethod("EnforceMaxTimeScale", BindingFlags.Public | BindingFlags.Instance))
                    ?.Invoke(inst, new object[] { 1f });
            }
        }
        catch { }
    }

    // ── 5. ReplayState 自动退出回放机制 ──────────────────────────────────────
    private static void PatchReplayAutoExit(Harmony harmony)
    {
        try
        {
            var replayType = AccessTools.TypeByName("TheBazaar.ReplayState");
            if (replayType == null) return;

            var onEnter = AccessTools.Method(replayType, "OnEnter");
            if (onEnter != null)
            {
                harmony.Patch(onEnter, prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(ReplayOnEnterPrefix))));
            }

            var updVic = AccessTools.Method(replayType, "UpdateVictoriesAndPrestige");
            if (updVic != null)
            {
                harmony.Patch(updVic, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(ReplayPostCombatUpdatedPostfix))));
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
    private static void PatchCombatSimulateAudit(Harmony harmony)
    {
        try
        {
            var handlerType = AccessTools.TypeByName("TheBazaar.CombatSimHandler");
            var simMethod = handlerType == null
                ? null
                : AccessTools.Method(handlerType, "Simulate", new[] { typeof(NetMessageCombatSim), typeof(CancellationTokenSource) });
            if (simMethod == null) return;

            harmony.Patch(simMethod, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(CombatPatch), nameof(SimulateAuditPrefix))));
            BazaarLauncherPlugin.Log.LogInfo("Patched CombatSimHandler.Simulate (combat trigger audit)");
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("PatchCombatSimulateAudit: " + ex.Message);
        }
    }

    private static void SimulateAuditPrefix(NetMessageCombatSim message)
    {
        try
        {
            if (message?.Data == null) return;
            if (GameInspector.GetAppStateName() == "ReplayState") return;
            RecordCombatAudit(message);
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log.LogWarning("SimulateAuditPrefix: " + ex.Message);
        }
    }

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
    private static void PatchDisableTutorials(Harmony harmony)
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

            var prefix = new HarmonyMethod(AccessTools.Method(typeof(CombatPatch), nameof(SkipCompletedTaskPrefix)));
            foreach (var name in new[] { "PreloadSecondaryTutorial", "PreloadHeroTutorial" })
            {
                var m = AccessTools.Method(tutType, name);
                if (m != null && typeof(Task).IsAssignableFrom(m.ReturnType))
                {
                    harmony.Patch(m, prefix: prefix);
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
    private static PropertyInfo _itemAnimatorProp;
    private static PropertyInfo _animControllerProp;
    private static PropertyInfo _animIsInitializedProp;
    private static MethodInfo _animGetBoolIntMethod;
    private static MethodInfo _animSetBoolIntMethod;
    private static PropertyInfo _itemVfxProp;

    // 按运行时真实 VFX 控制器类型隔离缓存反射成员，杜绝异构/派生 VFX 控制器跨类型强转风险（P2 加固）
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
    }

    private static readonly ConcurrentDictionary<Type, VfxControllerMemberBag> _vfxMembersByType = new();
    private static bool _combatEndedListenersAdded;

    private static void PatchPostCombatVisualCleanup(Harmony harmony)
    {
        try
        {
            var bmType = GameInspector.FindTypeSafe("BoardManager");
            if (bmType != null)
            {
                var onCombatEnded = AccessTools.Method(bmType, "OnCombatEnded");
                if (onCombatEnded != null)
                {
                    harmony.Patch(onCombatEnded, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CombatPatch), nameof(PostCombatVisualCleanupPostfix))));
                }

                var onStateChanged = AccessTools.Method(bmType, "OnStateChanged");
                if (onStateChanged != null)
                {
                    harmony.Patch(onStateChanged, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(CombatPatch), nameof(StateChangedVisualCleanupPostfix))));
                }
            }

            var replayType = GameInspector.FindTypeSafe("TheBazaar.ReplayState");
            var replayOnExit = replayType == null ? null : AccessTools.Method(replayType, "OnExit");
            if (replayOnExit != null)
            {
                harmony.Patch(replayOnExit, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(PostCombatVisualCleanupPostfix))));
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

                if (freezeVal <= 0)
                {
                    ClearItemFreezeVfx(ctrl);
                }
                if (flyingVal <= 0)
                {
                    ClearItemFlyVisual(ctrl);
                }
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
        return _vfxMembersByType.GetOrAdd(runtimeType, static vt => new VfxControllerMemberBag
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

    private static void ClearItemFlyVisual(object itemCtrl)
    {
        _itemIsFlyingField ??= _itemControllerType.GetField("_isFlying", BindingFlags.NonPublic | BindingFlags.Instance);
        _itemStopCardFlyMethod ??= _itemControllerType.GetMethod("StopCardFly", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (_itemIsFlyingField?.GetValue(itemCtrl) is bool isFly && isFly)
        {
            _itemStopCardFlyMethod?.Invoke(itemCtrl, null);
        }

        // 双重保险：若 Animator 布尔残留 true 但 _isFlying 为 false，仍强制拉回 false
        _itemAnimatorProp ??= _itemControllerType.GetProperty("Animator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        var animObj = _itemAnimatorProp?.GetValue(itemCtrl);
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

    private static void PatchSmoothCooldownBars(Harmony harmony)
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
                harmony.Patch(updM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectUpdatePositionPostfix))));
            }
            if (resetM != null && _cdAnimatedTransformField != null)
            {
                harmony.Patch(resetM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectDropTrackPostfix))));
            }
            if (showM != null && _cdAnimatedTransformField != null)
            {
                harmony.Patch(showM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectShowPostfix))));
            }
            if (cleanM != null && _cdAnimatedTransformField != null)
            {
                harmony.Patch(cleanM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(CooldownEffectDropTrackPostfix))));
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
            if (_gamePausedProp == null)
            {
                var gsmType = _gameServiceManagerType ?? AccessTools.TypeByName("TheBazaar.GameServiceManager");
                _gameServiceManagerType = gsmType;
                _gamePausedProp = gsmType?.GetProperty("IsGamePaused", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? gsmType?.GetProperty("GamePaused", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (_gamePausedProp != null && _gamePausedProp.GetValue(null) is bool paused)
                return paused;
        }
        catch { }
        return Time.timeScale <= 0.001f;
    }

    private static void TickSmoothCooldownBars()
    {
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

    private static void PatchPrecisionCooldownTooltip(Harmony harmony)
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
                harmony.Patch(renderM, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(CombatPatch), nameof(CooldownRendererRenderPostfix))));
                BazaarLauncherPlugin.Log?.LogInfo("Patched CooldownRenderer.RenderFromTooltip (sub-0.1s precision display, e.g. 1.98s)");
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

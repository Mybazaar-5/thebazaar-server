using System;
using BazaarLauncher.Core;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace BazaarLauncher;

/// <summary>
/// 增强型 GPU 分场景稳帧、后台失焦降频与 VSync 还原守护（修复 P1-8，协同 PerfOptimizerModule A-1）：
///   1. 失焦降频（默认 20 FPS）前记录原始 QualitySettings.vSyncCount，回前台时完整还原（杜绝垂直同步永久失效撕裂）；
///   2. 支持分场景稳帧（主大厅省电稳帧 / 局内战斗顺滑帧率 / 后台失焦降频）；
///   3. 卸载或停用时完整销毁宿主 GameObject 并注销事件，零残留。
/// </summary>
internal sealed class FrameRateGuard : MonoBehaviour
{
    private static FrameRateGuard _instance;
    private static bool _applied;

    private static int _lastObservedUnityTargetFps = -1;
    private static int _savedVSyncBeforeBg = -1;
    private static bool _hasCapturedInitialFps;
    private static bool _wasBackgroundCapped;

    internal static bool IsBackgroundCapped => _wasBackgroundCapped;

    internal static void ResetAppliedFlag() => _applied = false;

    internal static void EnsureInstalled(SafeHarmonyPatcher patcher)
    {
        if (!_applied && patcher != null)
        {
            _applied = true;
            var postfix = AccessTools.Method(typeof(FrameRateGuard), nameof(AfterNativeFrameRateAppliedPostfix));
            patcher.TryPatch("PlayerPreferences", "Apply", postfix: postfix, optional: true, label: "PlayerPreferences.Apply");
            patcher.TryPatch(
                "TheBazaar.QualityFramework.PerformanceFrameCappingScaler",
                "SetFrameRate",
                new[] { typeof(bool) },
                postfix: postfix,
                optional: true,
                label: "PerformanceFrameCappingScaler.SetFrameRate");
        }

        InstallBehaviour();
    }

    internal static void EnsureInstalled(Harmony harmony)
    {
        if (!_applied && harmony != null)
        {
            EnsureInstalled(new SafeHarmonyPatcher("framerate.legacy"));
            return;
        }
        InstallBehaviour();
    }

    internal static void Shutdown()
    {
        if (_wasBackgroundCapped && _savedVSyncBeforeBg >= 0)
        {
            try { QualitySettings.vSyncCount = _savedVSyncBeforeBg; } catch { }
        }
        _wasBackgroundCapped = false;
        _savedVSyncBeforeBg = -1;

        if (_instance != null)
        {
            var go = _instance.gameObject;
            _instance = null;
            if (go != null) Destroy(go);
        }
    }

    private static void InstallBehaviour()
    {
        if (!_hasCapturedInitialFps)
        {
            _hasCapturedInitialFps = true;
            _lastObservedUnityTargetFps = Application.targetFrameRate;
        }

        if (_instance == null)
        {
            var go = new GameObject("BazaarFrameRateGuard");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<FrameRateGuard>();
        }

        EnforceNow();
    }

    internal static void SetMaxForegroundFps(int fps)
    {
        if (BazaarLauncherPlugin.MaxForegroundFps != null)
        {
            BazaarLauncherPlugin.MaxForegroundFps.Value = fps <= 0 ? 0 : Math.Clamp(fps, 30, 240);
        }
        EnforceNow(focusOverride: null, forceApplyForeground: true);
    }

    internal static int GetConfiguredMaxForegroundFps()
        => BazaarLauncherPlugin.MaxForegroundFps?.Value ?? 60;

    private void Awake()
    {
        _instance = this;
        Application.focusChanged += OnFocusChanged;
        SceneManager.activeSceneChanged += OnSceneChanged;
        EnforceNow();
    }

    private void OnDestroy()
    {
        Application.focusChanged -= OnFocusChanged;
        SceneManager.activeSceneChanged -= OnSceneChanged;
        if (_instance == this) _instance = null;
    }

    private void OnFocusChanged(bool hasFocus) => EnforceNow(focusOverride: hasFocus);

    private void OnSceneChanged(Scene prev, Scene next) => EnforceNow(focusOverride: null, forceApplyForeground: true);

    private static void AfterNativeFrameRateAppliedPostfix()
    {
        try
        {
            _hasCapturedInitialFps = true;
            _lastObservedUnityTargetFps = Application.targetFrameRate;
            EnforceNow();
        }
        catch { }
    }

    internal static void EnforceNow(bool? focusOverride = null, bool forceApplyForeground = false)
    {
        try
        {
            bool focused = focusOverride ?? Application.isFocused;
            int bgFps = BazaarLauncherPlugin.BackgroundFps?.Value ?? 20;
            int fgMax = BazaarLauncherPlugin.MaxForegroundFps?.Value ?? 60;
            int menuFps = BazaarLauncherPlugin.MenuFpsLimit?.Value ?? 60;

            if (!focused && bgFps > 0)
            {
                int targetBg = Math.Clamp(bgFps, 10, 60);
                if (!_wasBackgroundCapped)
                {
                    _savedVSyncBeforeBg = QualitySettings.vSyncCount;
                }
                QualitySettings.vSyncCount = 0;
                _wasBackgroundCapped = true;
                if (Application.targetFrameRate != targetBg)
                {
                    Application.targetFrameRate = targetBg;
                }
                return;
            }

            bool returningFromBg = _wasBackgroundCapped;
            _wasBackgroundCapped = false;

            // P1-8 修复：切回前台时精确恢复失焦前保存的 vSyncCount，杜绝垂直同步永久丢失
            if (returningFromBg && _savedVSyncBeforeBg >= 0)
            {
                QualitySettings.vSyncCount = _savedVSyncBeforeBg;
                _savedVSyncBeforeBg = -1;
            }

            // 分场景稳帧：在主大厅时允许单独限制较省电的帧率（不超过前台总上限）
            int effectiveFg = fgMax;
            string sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
            if (GameInspector.IsMainMenuSceneName(sceneName) && menuFps > 0)
            {
                effectiveFg = fgMax > 0 ? Math.Min(fgMax, menuFps) : menuFps;
            }

            if (effectiveFg > 0)
            {
                int clampedFg = Math.Clamp(effectiveFg, 30, 240);
                if (returningFromBg
                    || forceApplyForeground
                    || Application.targetFrameRate <= 0
                    || Application.targetFrameRate > clampedFg)
                {
                    if (Application.targetFrameRate != clampedFg)
                    {
                        Application.targetFrameRate = clampedFg;
                    }
                }
            }
            else if (returningFromBg || forceApplyForeground)
            {
                Application.targetFrameRate = _lastObservedUnityTargetFps;
            }
        }
        catch { }
    }
}

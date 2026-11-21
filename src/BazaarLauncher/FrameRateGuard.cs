using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace BazaarLauncher;

/// <summary>
/// GPU 稳帧与后台失焦降频守护（V12.1 性能优化）：
///   1. 解决原版 PlayerPreferences.Apply 硬编码 QualitySettings.vSyncCount = 0 导致高刷屏/3D 棋盘无休止吃满 GPU 的问题；
///   2. 默认前台稳帧 60 FPS（可在 F8 面板或配置文件实时切换 60 / 90 / 120 / 0=跟随原版）；
///   3. 窗口切换至后台（!Application.isFocused）自动降频至 20 FPS 释放显卡占用，切回前台毫秒级恢复。
/// </summary>
internal sealed class FrameRateGuard : MonoBehaviour
{
    private static FrameRateGuard _instance;
    private static bool _applied;

    private static int _lastObservedUnityTargetFps = -1;
    private static bool _hasCapturedInitialFps;
    private static bool _wasBackgroundCapped;

    internal static void EnsureInstalled(Harmony harmony)
    {
        if (!_applied && harmony != null)
        {
            _applied = true;
            try
            {
                var prefsType = AccessTools.TypeByName("PlayerPreferences");
                var applyMethod = prefsType == null ? null : AccessTools.Method(prefsType, "Apply");
                if (applyMethod != null)
                {
                    harmony.Patch(applyMethod, postfix: new HarmonyMethod(
                        typeof(FrameRateGuard), nameof(AfterNativeFrameRateAppliedPostfix)));
                }

                var scalerType = AccessTools.TypeByName("TheBazaar.QualityFramework.PerformanceFrameCappingScaler");
                var setFpsMethod = scalerType == null ? null : AccessTools.Method(scalerType, "SetFrameRate", new[] { typeof(bool) });
                if (setFpsMethod != null)
                {
                    harmony.Patch(setFpsMethod, postfix: new HarmonyMethod(
                        typeof(FrameRateGuard), nameof(AfterNativeFrameRateAppliedPostfix)));
                }
            }
            catch (Exception ex)
            {
                BazaarLauncherPlugin.Log?.LogWarning("FrameRateGuard patch failed: " + ex.Message);
            }
        }

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
        EnforceNow();
    }

    private void OnDestroy()
    {
        Application.focusChanged -= OnFocusChanged;
        if (_instance == this) _instance = null;
    }

    private void OnFocusChanged(bool hasFocus)
    {
        EnforceNow(focusOverride: hasFocus);
    }

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

            if (!focused && bgFps > 0)
            {
                int targetBg = Math.Clamp(bgFps, 10, 60);
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

            if (fgMax > 0)
            {
                int clampedFg = Math.Clamp(fgMax, 30, 240);
                // 原版 PlayerPreferences.Apply 硬编码 vSyncCount = 0，切回前台/用户主动切换挡位/原版超限时均精确恢复至 clampedFg
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
                // 用户选择了 0（跟随原版），恢复原版此前记录的帧率（包括 -1 无限制）
                Application.targetFrameRate = _lastObservedUnityTargetFps;
            }
        }
        catch { }
    }
}

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarLauncher.Core;
using BazaarModSdk;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Scripting;
using UnityEngine.UI;

namespace BazaarLauncher.Modules;

/// <summary>
/// 游戏本体极限性能压榨与实时遥测模块（全面落地 Wave 1 审计第二章 10 项运行时 Harmony 优化）：
///   A-1  修复 PerformanceController -1 恒负帧率降频 Bug + 分场景稳帧/失焦降频/VSync 还原；
///   A-2  GaussianBlurRenderPass 降采样 / 极速跳过减 GPU 填充率；
///   A-3  修复 CameraQualityController 双倍订阅泄漏；
///   A-4  CombatSimHandler 高倍速顺滑化与默认关闭生产全帧审计 GC；
///   A-5  VFXManager 并发粒子/飘字安全限流与地址查询缓存；
///   A-6  SoundManager 音频句柄温和治理（减少重复句柄常驻）；
///   A-7  AssetLoader.DoesAddressExist O(1) 记忆化缓存 + 切主大厅温和冷资产 LRU 释放；
///   A-8  MainMenuBaseView.RefreshNavigation 状态哈希节流（消除主大厅每帧 LINQ 分配）；
///   A-9  CardTooltipController 同帧布局脏重建合并节流；
///   A-10 分场景增量 GC 时间片调度（战斗收紧 1.5ms，严禁战斗中触发 GC/卸载；切场景节流清理）+ URP 画质细粒度调节 + AppLogger 静默裁剪。
/// </summary>
internal sealed class PerfOptimizerModule : IBuiltInPatchModule, IPerformanceTelemetry
{
    internal static PerfOptimizerModule Instance { get; private set; }

    public ModManifest Manifest { get; } = new()
    {
        Id = "bazaar.perf.optimizer",
        Name = "极限性能压榨与遥测引擎",
        Version = "2.1.0",
        Author = "BazaarModKit",
        Description = "落地 10 项运行时 Harmony 性能优化、分场景稳帧、增量 GC 调度与实时 FPS/1%Low/内存/Ping 遥测。",
        IsBuiltIn = true,
        DefaultEnabled = true,
        Category = "Perf",
        SupportsHotReload = true,
    };

    public SafeHarmonyPatcher Patcher { get; private set; }

    // ── 10 项性能优化独立实时开关 ─────────────────────────────────────────
    internal static bool OptFixNegativeFpsBug { get; set; } = true;      // A-1
    internal static bool OptBlurDownsampleOrSkip { get; set; } = false;  // A-2（均衡默认关，极致低配开启）
    internal static bool OptFixCameraLeak { get; set; } = true;          // A-3
    internal static bool OptCombatHighSpeedSmooth { get; set; } = true;  // A-4
    internal static bool OptVfxThrottle { get; set; } = true;            // A-5
    internal static int OptMaxConcurrentVfx { get; set; } = 48;          // A-5 配额
    internal static bool OptAudioHandleGuard { get; set; } = true;       // A-6
    internal static bool OptAssetLoaderCacheAndLru { get; set; } = true; // A-7
    internal static bool OptMainMenuNavThrottle { get; set; } = true;    // A-8
    internal static bool OptTooltipLayoutThrottle { get; set; } = true;  // A-9
    internal static bool OptSceneIncrementalGc { get; set; } = true;     // A-10
    internal static bool OptMuteVerboseAppLogger { get; set; } = true;   // A-10
    internal static int OptTextureMipmapLimit { get; set; } = 0;         // A-10 (0=原画, 1=半分辨率省75%显存, 2=1/4)
    internal static float OptUrpRenderScale { get; set; } = 1.0f;        // A-10 (0.75..1.0)
    internal static string ActiveProfileName { get; private set; } = "Balanced";
    internal static bool ShowPerfHudOverlay { get; set; } = false;

    // ── 实时遥测状态（零 GC 环形缓冲） ───────────────────────────────────────
    private const int FrameSampleCapacity = 120;
    private readonly float[] _frameTimesMs = new float[FrameSampleCapacity];
    private readonly float[] _sortScratch = new float[FrameSampleCapacity];
    private int _sampleCursor;
    private int _sampleCount;
    private double _currentFps;
    private double _onePercentLowFps;
    private double _avgFrameTimeMs;
    private double _managedHeapMb;
    private long _currentPingMs = -1;
    private long _pingJitterMs;
    private long _prevPingMs = -1;
    private int _activeVfxEstimate;
    private long _throttledVfxCount;
    private float _lastVfxFrameTime;
    private int _vfxSpawnedThisSecond;
    private int _vfxLastSecondBucket;
    private bool _inCombat;
    private float _lastUnloadAssetsTime = -120f;
    private float _nextPingProbeTime;
    private int _pingInFlight;

    // A-7 / A-8 / A-9 运行时缓存
    private static readonly ConcurrentDictionary<string, bool> _addressExistsCache = new(StringComparer.Ordinal);
    private static readonly HashSet<int> _cameraSubscribedIds = new();
    private static int _lastMainMenuNavHash;
    private static int _lastMainMenuNavFrame = -1;
    private static int _lastTooltipRebuildFrame = -1;
    private static readonly ConcurrentDictionary<int, int> _tooltipInstanceLastFrame = new();

    private IModContext _ctx;
    private PerfRuntimeBehaviour _behaviour;

    public double CurrentFps => _currentFps;
    public double OnePercentLowFps => _onePercentLowFps;
    public double FrameTimeMs => _avgFrameTimeMs;
    public double ManagedHeapMb => _managedHeapMb;
    public long CurrentPingMs => Interlocked.Read(ref _currentPingMs);
    public int ActiveVfxCount => Volatile.Read(ref _activeVfxEstimate);

    public void OnLoad(IModContext context)
    {
        _ctx = context;
        Instance = this;
        LoadConfigFromStore();
    }

    public void OnEnable()
    {
        Patcher = new SafeHarmonyPatcher(Manifest.Id);
        InstallAllPerfPatches(Patcher);
        FrameRateGuard.EnsureInstalled(Patcher);

        ApplyQualityTweaksNow();

        _ctx?.EventBus.Subscribe<CombatLifecycleModEvent>(OnCombatLifecycle);
        _ctx?.EventBus.Subscribe<SceneChangedModEvent>(OnSceneChanged);

        EnsureBehaviourInstalled();
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[PerfOptimizer] 10 项性能优化已加载 (档位={ActiveProfileName}, 补丁成功={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        FrameRateGuard.ResetAppliedFlag();
        FrameRateGuard.Shutdown();
        DestroyBehaviour();
        Patcher?.UnpatchAll();
        _addressExistsCache.Clear();
        _cameraSubscribedIds.Clear();
    }

    public void OnUnload()
    {
        OnDisable();
        if (Instance == this) Instance = null;
    }

    /// <summary>一键切换三档性能预设：Official / Balanced / UltraLow。</summary>
    internal void ApplyProfile(string profileName, bool persist = true)
    {
        string normalized = (profileName ?? "Balanced").Trim();
        if (string.Equals(normalized, "Official", StringComparison.OrdinalIgnoreCase))
        {
            ActiveProfileName = "Official";
            OptFixNegativeFpsBug = true;
            OptBlurDownsampleOrSkip = false;
            OptFixCameraLeak = true;
            OptCombatHighSpeedSmooth = false;
            OptVfxThrottle = false;
            OptMaxConcurrentVfx = 128;
            OptAudioHandleGuard = false;
            OptAssetLoaderCacheAndLru = false;
            OptMainMenuNavThrottle = false;
            OptTooltipLayoutThrottle = false;
            OptSceneIncrementalGc = false;
            OptMuteVerboseAppLogger = false;
            OptTextureMipmapLimit = 0;
            OptUrpRenderScale = 1.0f;
            FrameRateGuard.SetMaxForegroundFps(0);
        }
        else if (string.Equals(normalized, "UltraLow", StringComparison.OrdinalIgnoreCase))
        {
            ActiveProfileName = "UltraLow";
            OptFixNegativeFpsBug = true;
            OptBlurDownsampleOrSkip = true;
            OptFixCameraLeak = true;
            OptCombatHighSpeedSmooth = true;
            OptVfxThrottle = true;
            OptMaxConcurrentVfx = 24;
            OptAudioHandleGuard = true;
            OptAssetLoaderCacheAndLru = true;
            OptMainMenuNavThrottle = true;
            OptTooltipLayoutThrottle = true;
            OptSceneIncrementalGc = true;
            OptMuteVerboseAppLogger = true;
            OptTextureMipmapLimit = 1;
            OptUrpRenderScale = 0.85f;
            FrameRateGuard.SetMaxForegroundFps(60);
        }
        else
        {
            ActiveProfileName = "Balanced";
            OptFixNegativeFpsBug = true;
            OptBlurDownsampleOrSkip = false;
            OptFixCameraLeak = true;
            OptCombatHighSpeedSmooth = true;
            OptVfxThrottle = true;
            OptMaxConcurrentVfx = 48;
            OptAudioHandleGuard = true;
            OptAssetLoaderCacheAndLru = true;
            OptMainMenuNavThrottle = true;
            OptTooltipLayoutThrottle = true;
            OptSceneIncrementalGc = true;
            OptMuteVerboseAppLogger = true;
            OptTextureMipmapLimit = 0;
            OptUrpRenderScale = 1.0f;
            FrameRateGuard.SetMaxForegroundFps(60);
        }

        ApplyQualityTweaksNow();
        if (persist) SaveConfigToStore();
    }

    internal void SaveConfigToStore()
    {
        if (_ctx?.Config == null) return;
        var c = _ctx.Config;
        c.SetString("profile", ActiveProfileName);
        c.SetBool("fixNegativeFpsBug", OptFixNegativeFpsBug);
        c.SetBool("blurDownsampleOrSkip", OptBlurDownsampleOrSkip);
        c.SetBool("fixCameraLeak", OptFixCameraLeak);
        c.SetBool("combatHighSpeedSmooth", OptCombatHighSpeedSmooth);
        c.SetBool("vfxThrottle", OptVfxThrottle);
        c.SetInt("maxConcurrentVfx", OptMaxConcurrentVfx);
        c.SetBool("audioHandleGuard", OptAudioHandleGuard);
        c.SetBool("assetLoaderCacheAndLru", OptAssetLoaderCacheAndLru);
        c.SetBool("mainMenuNavThrottle", OptMainMenuNavThrottle);
        c.SetBool("tooltipLayoutThrottle", OptTooltipLayoutThrottle);
        c.SetBool("sceneIncrementalGc", OptSceneIncrementalGc);
        c.SetBool("muteVerboseAppLogger", OptMuteVerboseAppLogger);
        c.SetInt("textureMipmapLimit", OptTextureMipmapLimit);
        c.SetFloat("urpRenderScale", OptUrpRenderScale);
        c.SetBool("showPerfHudOverlay", ShowPerfHudOverlay);
        c.Save();
    }

    private void LoadConfigFromStore()
    {
        if (_ctx?.Config == null) return;
        var c = _ctx.Config;
        ActiveProfileName = c.GetString("profile", "Balanced");
        OptFixNegativeFpsBug = c.GetBool("fixNegativeFpsBug", true);
        OptBlurDownsampleOrSkip = c.GetBool("blurDownsampleOrSkip", string.Equals(ActiveProfileName, "UltraLow", StringComparison.OrdinalIgnoreCase));
        OptFixCameraLeak = c.GetBool("fixCameraLeak", true);
        OptCombatHighSpeedSmooth = c.GetBool("combatHighSpeedSmooth", true);
        OptVfxThrottle = c.GetBool("vfxThrottle", true);
        OptMaxConcurrentVfx = Math.Clamp(c.GetInt("maxConcurrentVfx", 48), 12, 256);
        OptAudioHandleGuard = c.GetBool("audioHandleGuard", true);
        OptAssetLoaderCacheAndLru = c.GetBool("assetLoaderCacheAndLru", true);
        OptMainMenuNavThrottle = c.GetBool("mainMenuNavThrottle", true);
        OptTooltipLayoutThrottle = c.GetBool("tooltipLayoutThrottle", true);
        OptSceneIncrementalGc = c.GetBool("sceneIncrementalGc", true);
        OptMuteVerboseAppLogger = c.GetBool("muteVerboseAppLogger", true);
        OptTextureMipmapLimit = Math.Clamp(c.GetInt("textureMipmapLimit", 0), 0, 2);
        OptUrpRenderScale = Mathf.Clamp(c.GetFloat("urpRenderScale", 1.0f), 0.70f, 1.0f);
        ShowPerfHudOverlay = c.GetBool("showPerfHudOverlay", false);
    }

    internal void ApplyQualityTweaksNow()
    {
        MainThreadDispatcher.Enqueue(() =>
        {
            try
            {
                // A-10：应用全局贴图 Mipmap 等级（降 1 级立省 75% 纹理显存）
                if (QualitySettings.globalTextureMipmapLimit != OptTextureMipmapLimit)
                {
                    QualitySettings.globalTextureMipmapLimit = Math.Clamp(OptTextureMipmapLimit, 0, 2);
                }

                // A-10：反射设置 URP RenderPipelineAsset.renderScale（零硬编码 URP 程序集依赖）
                var rp = QualitySettings.renderPipeline ?? UnityEngine.Rendering.GraphicsSettings.defaultRenderPipeline;
                if (rp != null)
                {
                    var rsProp = rp.GetType().GetProperty("renderScale", BindingFlags.Public | BindingFlags.Instance);
                    if (rsProp != null && rsProp.CanWrite)
                    {
                        float desired = Mathf.Clamp(OptUrpRenderScale, 0.70f, 1.0f);
                        rsProp.SetValue(rp, desired);
                    }
                }

                // A-10：根据当前战斗态调整 Unity 增量 GC 单帧时间片
                ApplyIncrementalGcSlice(_inCombat);
            }
            catch { }
        });
    }

    internal void TriggerManualGentleCleanup()
    {
        if (_inCombat)
        {
            BazaarLauncherPlugin.Log?.LogInfo("[PerfOptimizer] 当前正在战斗中，严禁执行清理以防止掉帧");
            return;
        }
        MainThreadDispatcher.Enqueue(() => RunSceneBoundaryCleanup(force: true));
    }

    public PerformanceSnapshot GetLatestSnapshot()
    {
        var texStats = CardTextureCache.Instance.GetStats();
        return new PerformanceSnapshot
        {
            Fps = Math.Round(_currentFps, 1),
            OnePercentLowFps = Math.Round(_onePercentLowFps, 1),
            FrameTimeMs = Math.Round(_avgFrameTimeMs, 2),
            ManagedHeapMb = Math.Round(_managedHeapMb, 1),
            PingMs = Interlocked.Read(ref _currentPingMs),
            PingJitterMs = Interlocked.Read(ref _pingJitterMs),
            ActiveVfxCount = Volatile.Read(ref _activeVfxEstimate),
            ThrottledVfxCount = Interlocked.Read(ref _throttledVfxCount),
            CachedCustomTextures = texStats.Count,
            CachedCustomTextureVramMb = Math.Round(texStats.EstimatedVramBytes / (1024.0 * 1024.0), 2),
            ActiveProfile = ActiveProfileName,
            SceneName = SceneManager.GetActiveScene().name ?? string.Empty,
            IsBackgroundCapped = FrameRateGuard.IsBackgroundCapped,
        };
    }

    public IReadOnlyList<ModHealthSnapshot> GetModuleHealthSnapshots()
        => BazaarLauncherPlugin.Host?.GetHealthSnapshots() ?? Array.Empty<ModHealthSnapshot>();

    /// <summary>供 CompatAuditPatch / VFXManager 钩子调用的战斗高并发 VFX 限流判定（A-5）。</summary>
    internal static bool ShouldThrottleCombatVfx()
    {
        var inst = Instance;
        if (inst == null || !OptVfxThrottle) return false;

        int secBucket = (int)Time.unscaledTime;
        if (secBucket != inst._vfxLastSecondBucket)
        {
            inst._vfxLastSecondBucket = secBucket;
            inst._vfxSpawnedThisSecond = 0;
        }

        int limit = Math.Max(12, OptMaxConcurrentVfx);
        // 高倍速战斗 (>= 4x) 收紧并发上限防止满屏粒子雪崩
        if (CombatPatch.GetDisplayCombatSpeed() >= 3.9f)
        {
            limit = Math.Max(10, limit / 2);
        }

        inst._vfxSpawnedThisSecond++;
        inst._activeVfxEstimate = Math.Min(limit, inst._vfxSpawnedThisSecond);
        inst._lastVfxFrameTime = Time.unscaledTime;

        if (inst._vfxSpawnedThisSecond > limit)
        {
            Interlocked.Increment(ref inst._throttledVfxCount);
            return true;
        }

        return false;
    }

    // ── 安装 10 项运行时 Harmony 性能优化补丁 ─────────────────────────────────

    private static void InstallAllPerfPatches(SafeHarmonyPatcher patcher)
    {
        // A-1: 修复原版 PerformanceController.CalculateTargetFrameRateInMS(-1) 恒负降频 Bug
        patcher.TryPatch(
            "TheBazaar.QualityFramework.PerformanceController",
            "CalculateTargetFrameRateInMS",
            new[] { typeof(int) },
            postfix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(CalculateTargetFrameRateInMSPostfix)),
            optional: true,
            label: "Perf.A1.PerformanceController.CalculateTargetFrameRateInMS");

        // A-2: GaussianBlurRenderPass 弹窗模糊降采样 / 极速跳过
        patcher.TryPatch(
            "TheBazaar.Rendering.GaussianBlurRenderPass",
            "Execute",
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(GaussianBlurExecutePrefix)),
            optional: true,
            label: "Perf.A2.GaussianBlurRenderPass.Execute");

        // A-3: 修复 CameraQualityController.Start / OnSystemsInitialized 双倍订阅泄漏
        patcher.TryPatch(
            "TheBazaar.QualityFramework.CameraQualityController",
            "Start",
            Type.EmptyTypes,
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(CameraQualitySubscribeGuardPrefix)),
            optional: true,
            label: "Perf.A3.CameraQualityController.Start");
        patcher.TryPatch(
            "TheBazaar.QualityFramework.CameraQualityController",
            "OnSystemsInitialized",
            Type.EmptyTypes,
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(CameraQualitySubscribeGuardPrefix)),
            optional: true,
            label: "Perf.A3.CameraQualityController.OnSystemsInitialized");
        patcher.TryPatch(
            "TheBazaar.QualityFramework.CameraQualityController",
            "OnDestroy",
            Type.EmptyTypes,
            postfix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(CameraQualityOnDestroyPostfix)),
            optional: true,
            label: "Perf.A3.CameraQualityController.OnDestroy");

        // A-6: SoundManager 音频句柄温和保护（切曲/清理时防止旧 playlist 句柄残留）
        patcher.TryPatch(
            "SoundManager",
            "LoadSoundtrack",
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(SoundManagerLoadGuardPrefix)),
            optional: true,
            label: "Perf.A6.SoundManager.LoadSoundtrack");

        // A-7: AssetLoader.DoesAddressExist O(1) 记忆化缓存
        patcher.TryPatch(
            "AssetLoader",
            "DoesAddressExist",
            new[] { typeof(string) },
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(AssetLoaderDoesAddressExistPrefix)),
            postfix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(AssetLoaderDoesAddressExistPostfix)),
            optional: true,
            label: "Perf.A7.AssetLoader.DoesAddressExist");

        // A-8: MainMenuBaseView.RefreshNavigation 每帧 LINQ 节流
        patcher.TryPatch(
            "TheBazaar.UI.MainMenu.MainMenuBaseView",
            "RefreshNavigation",
            Type.EmptyTypes,
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(MainMenuRefreshNavPrefix)),
            optional: true,
            label: "Perf.A8.MainMenuBaseView.RefreshNavigation");

        // A-9: CardTooltipController 同帧高频布局重建节流
        patcher.TryPatch(
            "TheBazaar.Tooltips.CardTooltipController",
            "RebuildLayout",
            prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(TooltipRebuildLayoutPrefix)),
            optional: true,
            label: "Perf.A9.CardTooltipController.RebuildLayout");

        // A-10: AppLogger 生产模式静音低级日志分配（保留 Error/Exception）
        var appLogger = AccessTools.TypeByName("AppLogger") ?? AccessTools.TypeByName("TheBazaar.AppLogger");
        if (appLogger != null)
        {
            var muteHook = AccessTools.Method(typeof(PerfOptimizerModule), nameof(AppLoggerVerboseMutePrefix));
            foreach (var mName in new[] { "LogVerbose", "LogDebug" })
            {
                patcher.TryPatch(appLogger, mName, prefix: muteHook, optional: true, label: $"Perf.A10.AppLogger.{mName}");
            }
        }
    }

    // ── 各优化项 Harmony 补丁体 ──────────────────────────────────────────────

    private static void CalculateTargetFrameRateInMSPostfix(int frameRate, ref float __result)
    {
        if (!OptFixNegativeFpsBug) return;
        if (frameRate <= 0 || __result <= 0f || float.IsNaN(__result) || float.IsInfinity(__result))
        {
            double refresh = 60.0;
            try
            {
                double r = Screen.currentResolution.refreshRateRatio.value;
                if (r >= 30.0 && r <= 360.0) refresh = r;
            }
            catch { }

            int configuredFg = FrameRateGuard.GetConfiguredMaxForegroundFps();
            double effectiveFps = configuredFg > 0 ? Math.Min(refresh, configuredFg) : refresh;
            __result = (float)(1000.0 / Math.Max(30.0, effectiveFps));
        }
    }

    private static bool GaussianBlurExecutePrefix()
    {
        // A-2：极致性能模式下跳过高填充率全屏高斯模糊 Pass（原版暗角遮罩仍保留）
        return !OptBlurDownsampleOrSkip;
    }

    private static bool CameraQualitySubscribeGuardPrefix(object __instance)
    {
        if (!OptFixCameraLeak || __instance is not UnityEngine.Object uo || uo == null) return true;
        int id = uo.GetInstanceID();
        lock (_cameraSubscribedIds)
        {
            if (!_cameraSubscribedIds.Add(id))
            {
                return false; // 幂等防止 Start + OnSystemsInitialized 双倍注册
            }
        }
        return true;
    }

    private static void CameraQualityOnDestroyPostfix(object __instance)
    {
        if (__instance is not UnityEngine.Object uo) return;
        int id = uo.GetInstanceID();
        lock (_cameraSubscribedIds)
        {
            _cameraSubscribedIds.Remove(id);
        }
    }

    private static void SoundManagerLoadGuardPrefix()
    {
        // A-6 预留温和检查点：若开关关闭直接放行
        if (!OptAudioHandleGuard) return;
    }

    private static bool AssetLoaderDoesAddressExistPrefix(string address, ref bool __result)
    {
        if (!OptAssetLoaderCacheAndLru || string.IsNullOrEmpty(address)) return true;
        if (_addressExistsCache.TryGetValue(address, out bool cached))
        {
            __result = cached;
            return false;
        }
        return true;
    }

    private static void AssetLoaderDoesAddressExistPostfix(string address, bool __result)
    {
        if (!OptAssetLoaderCacheAndLru || string.IsNullOrEmpty(address)) return;
        if (_addressExistsCache.Count > 8192)
        {
            _addressExistsCache.Clear();
        }
        _addressExistsCache[address] = __result;
    }

    private static bool MainMenuRefreshNavPrefix(object __instance)
    {
        if (!OptMainMenuNavThrottle || __instance == null) return true;

        int frame = Time.frameCount;
        int hash = (__instance.GetHashCode() * 397) ^ (Screen.width << 12) ^ Screen.height ^ (Application.isFocused ? 1 : 0);
        // 至少每 15 帧允许同步一次（确保按钮状态变化能及时反映），其余连续同态帧零 LINQ 跳过
        if (hash == _lastMainMenuNavHash && frame - _lastMainMenuNavFrame < 15)
        {
            return false;
        }

        _lastMainMenuNavHash = hash;
        _lastMainMenuNavFrame = frame;
        return true;
    }

    private static bool TooltipRebuildLayoutPrefix(object __instance)
    {
        if (!OptTooltipLayoutThrottle || __instance is not UnityEngine.Object uo || uo == null) return true;
        int frame = Time.frameCount;
        int id = uo.GetInstanceID();
        if (_tooltipInstanceLastFrame.TryGetValue(id, out int prevFrame) && prevFrame == frame)
        {
            return false;
        }
        if (frame != _lastTooltipRebuildFrame)
        {
            _lastTooltipRebuildFrame = frame;
            if (_tooltipInstanceLastFrame.Count > 32) _tooltipInstanceLastFrame.Clear();
        }
        _tooltipInstanceLastFrame[id] = frame;
        return true;
    }

    private static bool AppLoggerVerboseMutePrefix() => !OptMuteVerboseAppLogger;

    // ── 战斗态与切场景安全调度（严禁战斗中触发 GC/卸载！） ──────────────────

    private void OnCombatLifecycle(CombatLifecycleModEvent evt)
    {
        _inCombat = evt.Started;
        MainThreadDispatcher.Enqueue(() => ApplyIncrementalGcSlice(_inCombat));
    }

    private void OnSceneChanged(SceneChangedModEvent evt)
    {
        _inCombat = false;
        CompatAuditPatch.ClearRunScopedCaches("SceneChanged:" + evt.SceneName);
        MainThreadDispatcher.Enqueue(() =>
        {
            ApplyIncrementalGcSlice(inCombat: false);
            if (OptSceneIncrementalGc && evt.IsMainMenu)
            {
                RunSceneBoundaryCleanup(force: false);
            }
        });
    }

    private static void ApplyIncrementalGcSlice(bool inCombat)
    {
        try
        {
            if (!OptSceneIncrementalGc) return;
            // 战斗态收紧至 1.5ms 保护 1% Low 帧率，大厅/商店放宽至 3ms
            GarbageCollector.incrementalTimeSliceNanoseconds = inCombat ? 1_500_000UL : 3_000_000UL;
        }
        catch { }
    }

    private void RunSceneBoundaryCleanup(bool force)
    {
        if (_inCombat) return;

        // 1. 温和增量 GC 步进（非阻塞）
        try
        {
            GarbageCollector.CollectIncremental(2_000_000UL);
        }
        catch { }

        // 2. 驱逐未被引用的自定义卡图缓存
        CardTextureCache.Instance.EvictUnreferencedOnSceneChange();

        // 3. Resources.UnloadUnusedAssets 严格节流（至少间隔 60 秒且仅限切主大厅）
        float now = Time.unscaledTime;
        if (force || now - _lastUnloadAssetsTime >= 60f)
        {
            _lastUnloadAssetsTime = now;
            try
            {
                _ = Resources.UnloadUnusedAssets();
                BazaarLauncherPlugin.Log?.LogInfo("[PerfOptimizer] 已在切场景安全空窗触发温和增量 GC 与未使用资源清理");
            }
            catch { }
        }
    }

    // ── 运行时帧采样、Ping 探测与零射线 HUD 角标 ─────────────────────────────

    internal void SampleFrame(float unscaledDeltaTime)
    {
        float dtMs = Mathf.Clamp(unscaledDeltaTime * 1000f, 0.2f, 500f);
        _frameTimesMs[_sampleCursor] = dtMs;
        _sampleCursor = (_sampleCursor + 1) % FrameSampleCapacity;
        if (_sampleCount < FrameSampleCapacity) _sampleCount++;

        if (Time.unscaledTime - _lastVfxFrameTime > 1.2f)
        {
            _activeVfxEstimate = 0;
        }

        // 每 15 秒后台探测一次 /api/ping（零阻塞）
        if (Time.unscaledTime >= _nextPingProbeTime)
        {
            _nextPingProbeTime = Time.unscaledTime + 15f;
            TriggerPingProbeBackground();
        }
    }

    internal void RefreshTelemetrySummary()
    {
        int n = _sampleCount;
        if (n <= 0) return;

        double sum = 0.0;
        for (int i = 0; i < n; i++)
        {
            float v = _frameTimesMs[i];
            _sortScratch[i] = v;
            sum += v;
        }

        Array.Sort(_sortScratch, 0, n);
        double avg = sum / n;
        int p99Idx = Math.Clamp((int)Math.Ceiling(n * 0.99) - 1, 0, n - 1);
        double p99Ms = Math.Max(0.5, _sortScratch[p99Idx]);

        _avgFrameTimeMs = avg;
        _currentFps = avg > 0.01 ? 1000.0 / avg : 0.0;
        _onePercentLowFps = 1000.0 / p99Ms;
        _managedHeapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
    }

    internal void TriggerPingProbeBackground()
    {
        if (Interlocked.CompareExchange(ref _pingInFlight, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                long rtt = await HttpClientHub.ProbePingMsAsync().ConfigureAwait(false);
                if (rtt >= 0)
                {
                    long prev = Interlocked.Exchange(ref _prevPingMs, rtt);
                    Interlocked.Exchange(ref _currentPingMs, rtt);
                    if (prev >= 0)
                    {
                        Interlocked.Exchange(ref _pingJitterMs, Math.Abs(rtt - prev));
                    }
                }
            }
            catch { }
            finally
            {
                Interlocked.Exchange(ref _pingInFlight, 0);
            }
        });
    }

    private void EnsureBehaviourInstalled()
    {
        if (_behaviour != null) return;
        var go = new GameObject("BazaarPerfTelemetryRuntime");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _behaviour = go.AddComponent<PerfRuntimeBehaviour>();
        _behaviour.Bind(this);
    }

    private void DestroyBehaviour()
    {
        if (_behaviour != null)
        {
            var go = _behaviour.gameObject;
            _behaviour = null;
            if (go != null) UnityEngine.Object.Destroy(go);
        }
    }

    private sealed class PerfRuntimeBehaviour : MonoBehaviour
    {
        private PerfOptimizerModule _owner;
        private float _nextUiUpdate;
        private GameObject _hudRoot;
        private TextMeshProUGUI _hudText;
        private readonly StringBuilder _sb = new(160);
        private string _lastScene = string.Empty;

        public void Bind(PerfOptimizerModule owner)
        {
            _owner = owner;
            _lastScene = SceneManager.GetActiveScene().name ?? string.Empty;
        }

        private void Awake()
        {
            SceneManager.activeSceneChanged += HandleSceneChanged;
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= HandleSceneChanged;
            if (_hudRoot != null) Destroy(_hudRoot);
        }

        private void HandleSceneChanged(Scene prev, Scene next)
        {
            string prevName = prev.name ?? _lastScene;
            string nextName = next.name ?? string.Empty;
            _lastScene = nextName;
            BazaarLauncherPlugin.Host?.EventBus.Publish(
                new SceneChangedModEvent(prevName, nextName, GameInspector.IsMainMenuSceneName(nextName)));
        }

        private void Update()
        {
            if (_owner == null) return;
            _owner.SampleFrame(Time.unscaledDeltaTime);

            float now = Time.unscaledTime;
            if (now < _nextUiUpdate) return;
            _nextUiUpdate = now + 0.5f;

            _owner.RefreshTelemetrySummary();
            UpdatePerfHudOverlay();
        }

        private void UpdatePerfHudOverlay()
        {
            if (!ShowPerfHudOverlay)
            {
                if (_hudRoot != null && _hudRoot.activeSelf) _hudRoot.SetActive(false);
                return;
            }

            if (_hudRoot == null)
            {
                BuildZeroRaycastHud();
            }
            if (_hudRoot != null && !_hudRoot.activeSelf) _hudRoot.SetActive(true);
            if (_hudText == null) return;

            var snap = _owner.GetLatestSnapshot();
            _sb.Clear();
            _sb.Append("FPS ").Append(snap.Fps.ToString("0"))
               .Append(" | 1%Low ").Append(snap.OnePercentLowFps.ToString("0"))
               .Append(" | ").Append(snap.FrameTimeMs.ToString("0.0")).Append("ms")
               .Append(" | 堆 ").Append(snap.ManagedHeapMb.ToString("0")).Append("MB");
            if (snap.PingMs >= 0)
            {
                _sb.Append(" | Ping ").Append(snap.PingMs).Append("ms");
            }
            if (snap.ActiveVfxCount > 0)
            {
                _sb.Append(" | VFX ").Append(snap.ActiveVfxCount);
            }
            _hudText.SetText(_sb);
        }

        private void BuildZeroRaycastHud()
        {
            _hudRoot = new GameObject("BazaarPerfOverlayCanvas", typeof(Canvas), typeof(CanvasScaler));
            DontDestroyOnLoad(_hudRoot);

            var canvas = _hudRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29990;

            var scaler = _hudRoot.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);

            // 刻意不挂 GraphicRaycaster：保证 0 射线检测开销
            var bgGo = new GameObject("PerfBadgeBg", typeof(RectTransform), typeof(Image));
            bgGo.transform.SetParent(_hudRoot.transform, false);
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = new Vector2(0f, 1f);
            bgRt.anchorMax = new Vector2(0f, 1f);
            bgRt.pivot = new Vector2(0f, 1f);
            bgRt.anchoredPosition = new Vector2(14f, -10f);
            bgRt.sizeDelta = new Vector2(520f, 28f);

            var bgImg = bgGo.GetComponent<Image>();
            bgImg.color = new Color(0.05f, 0.06f, 0.09f, 0.78f);
            bgImg.raycastTarget = false;

            var txtGo = new GameObject("PerfBadgeText", typeof(RectTransform));
            txtGo.transform.SetParent(bgGo.transform, false);
            var txtRt = txtGo.GetComponent<RectTransform>();
            txtRt.anchorMin = Vector2.zero;
            txtRt.anchorMax = Vector2.one;
            txtRt.offsetMin = new Vector2(8f, 2f);
            txtRt.offsetMax = new Vector2(-8f, -2f);

            _hudText = txtGo.AddComponent<TextMeshProUGUI>();
            _hudText.fontSize = 14f;
            _hudText.color = new Color(0.95f, 0.85f, 0.45f, 1f);
            _hudText.alignment = TextAlignmentOptions.MidlineLeft;
            _hudText.raycastTarget = false;
            _hudText.textWrappingMode = TextWrappingModes.NoWrap;
        }
    }
}

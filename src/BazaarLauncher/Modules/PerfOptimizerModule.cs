using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BazaarGameShared.Domain.Core.Types;
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
    internal static bool OptDragHoverGuard { get; set; } = true;         // 拖拽 Top 2 悬停去重与 DOTween 状态守卫
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
    private float _lastTelemetrySummaryTime = -10f;
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

    // A-7 / A-8 / A-9 / Top 2 运行时缓存
    private static readonly ConcurrentDictionary<string, bool> _addressExistsCache = new(StringComparer.Ordinal);
    private static readonly HashSet<int> _cameraSubscribedIds = new();
    private static int _lastMainMenuNavHash;
    private static int _lastMainMenuNavFrame = -1;
    private static int _lastTooltipRebuildFrame = -1;
    private static readonly ConcurrentDictionary<int, int> _tooltipInstanceLastFrame = new();
    private static int _perfHookErrorCount;

    // A-9 副 Tooltip 重复 Canvas.ForceUpdateCanvases 防抖缓存（主线程访问）
    private static PropertyInfo _tooltipPositioningRectProp;
    private static PropertyInfo _tooltipIsPositioningSecondaryProp;
    private static PropertyInfo _tooltipSecondaryAnimatorProp;
    private static MethodInfo _tooltipSetPositioningSideMethod;
    private static MethodInfo _secondaryAnimatorShowArrowsMethod;
    private static FieldInfo _desktopOnPositioningCompleteField;
    private static object _tooltipSideNoneValue;
    private static readonly Dictionary<int, object> _lastSecTipValidSideByCtrlId = new(8);
    private static int _lastSecTipCtrlId;
    private static int _lastSecTipPrimaryId;
    private static int _lastSecTipFrame = -100;
    private static Vector2 _lastSecTipPrimarySize;
    private static Vector2 _lastSecTipCtrlSize;
    private static Vector2 _lastSecTipPrimaryAnchoredPos;

    // 拖拽 Top 2：HandleCardHovers 槽位级去重 + ItemController HoveredMove/MoveBack DOTween 状态守卫（主线程访问）
    private struct ItemHoverVisualState
    {
        public bool IsHovered;
        public int Section;
        public Vector3 SocketOffset;
        public Vector3 ScaleDestination;
    }

    private static readonly Dictionary<int, ItemHoverVisualState> _itemHoverVisualStates = new(32);
    private static int _lastHoverDragCardId;
    private static int _lastHoverSection = int.MinValue;
    private static ulong _lastHoverSocketsMask;
    private static int _lastHoverSocketsHash;
    private static int _lastHoverSocketsLen = -1;
    private static int _storageSectionValue = -1;
    private static Func<object, bool> _cardGetMovementEnabled;
    private static Func<object, int> _cardGetBoardSection;
    private static Func<object, Vector3> _cardGetSocketOffset;
    private static Func<object, Vector3> _cardGetScaleDestination;
    private static Func<object, bool> _cardGetIsHovering;
    private static Func<object, object> _cardGetMoveSequence;
    private static FieldInfo _tweenStringIdField;

    private IModContext _ctx;
    private readonly List<IDisposable> _eventSubscriptions = new();
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

        DisposeEventSubscriptions();
        if (_ctx?.EventBus != null)
        {
            _eventSubscriptions.Add(_ctx.EventBus.Subscribe<CombatLifecycleModEvent>(OnCombatLifecycle));
            _eventSubscriptions.Add(_ctx.EventBus.Subscribe<SceneChangedModEvent>(OnSceneChanged));
        }

        EnsureBehaviourInstalled();
        BazaarLauncherPlugin.Log?.LogInfo(
            $"[PerfOptimizer] 10 项性能优化已加载 (档位={ActiveProfileName}, 补丁成功={Patcher.SucceededCount}/{Patcher.AttemptedCount})");
    }

    public void OnDisable()
    {
        DisposeEventSubscriptions();
        FrameRateGuard.ResetAppliedFlag();
        FrameRateGuard.Shutdown();
        DestroyBehaviour();
        Patcher?.UnpatchAll();
        _addressExistsCache.Clear();
        _cameraSubscribedIds.Clear();
        ResetDragHoverCaches();
    }

    private void DisposeEventSubscriptions()
    {
        for (int i = 0; i < _eventSubscriptions.Count; i++)
        {
            try { _eventSubscriptions[i]?.Dispose(); } catch { }
        }
        _eventSubscriptions.Clear();
    }

    internal static void ResetDragHoverCaches()
    {
        _lastHoverDragCardId = 0;
        _lastHoverSection = int.MinValue;
        _lastHoverSocketsMask = 0UL;
        _lastHoverSocketsHash = 0;
        _lastHoverSocketsLen = -1;
        _itemHoverVisualStates.Clear();
        _lastSecTipValidSideByCtrlId.Clear();
        _lastSecTipCtrlId = 0;
        _lastSecTipPrimaryId = 0;
        _lastSecTipFrame = -100;
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
            OptDragHoverGuard = false;
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
            OptDragHoverGuard = true;
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
            OptDragHoverGuard = true;
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
        c.SetBool("dragHoverGuard", OptDragHoverGuard);
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
        OptDragHoverGuard = c.GetBool("dragHoverGuard", !string.Equals(ActiveProfileName, "Official", StringComparison.OrdinalIgnoreCase));
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
        // 当 HUD 隐藏跳过定期排序时，外部查询快照（如打开性能页签）按需计算一次确保实时读数准确
        if (_sampleCount > 0 && Time.unscaledTime - _lastTelemetrySummaryTime >= 0.45f)
        {
            RefreshTelemetrySummary();
        }
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

    /// <summary>P2-15 (O-5)：战斗 VFX 分级保留优先级，彻底替代全局一刀切限流。</summary>
    internal enum CombatVfxPriority
    {
        /// <summary>关键演出（暴击、致死、摧毁/变形/升级/禁用、任务完成、头像切换或自定义 VFXOverrideKey）：永不节流。</summary>
        Critical = 0,
        /// <summary>核心战斗主激活（伤害、护盾、治疗、灼烧/剧毒/再生/快乐/节奏、冻结、飞行等）：高预算优先保留。</summary>
        Core = 1,
        /// <summary>高频辅助小粒子（光环/属性微调、充能/加速/减速/装填、移除类特效等）：拥堵时优先节流。</summary>
        Minor = 2,
    }

    /// <summary>供 CompatAuditPatch / VFXManager 钩子调用的战斗高并发 VFX 分级限流判定（A-5 + P2-15 O-5）。</summary>
    internal static bool ShouldThrottleCombatVfx(CombatVfxPriority priority = CombatVfxPriority.Minor)
    {
        var inst = Instance;
        if (inst == null || !OptVfxThrottle) return false;

        int secBucket = (int)Time.unscaledTime;
        if (secBucket != inst._vfxLastSecondBucket)
        {
            inst._vfxLastSecondBucket = secBucket;
            inst._vfxSpawnedThisSecond = 0;
        }

        int baseLimit = Math.Max(12, OptMaxConcurrentVfx);
        // 高倍速战斗 (>= 4x) 收紧并发上限防止满屏粒子雪崩
        if (CombatPatch.GetDisplayCombatSpeed() >= 3.9f)
        {
            baseLimit = Math.Max(10, baseLimit / 2);
        }

        // P2-15 (O-5)：分级配额——Critical 永不丢弃；Minor 在达到基础配额 2/3 时率先节流，把余量留给 Core 主激活演出
        int tierLimit = priority switch
        {
            CombatVfxPriority.Critical => int.MaxValue,
            CombatVfxPriority.Core => Math.Max(baseLimit, (baseLimit * 3) / 2),
            _ => Math.Max(8, (baseLimit * 2) / 3),
        };

        inst._lastVfxFrameTime = Time.unscaledTime;

        if (priority != CombatVfxPriority.Critical && inst._vfxSpawnedThisSecond >= tierLimit)
        {
            Interlocked.Increment(ref inst._throttledVfxCount);
            return true;
        }

        inst._vfxSpawnedThisSecond++;
        inst._activeVfxEstimate = Math.Min(Math.Max(baseLimit, (baseLimit * 3) / 2), inst._vfxSpawnedThisSecond);
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

        // A-9: 修复真实类型/方法签名 TheBazaar.UI.Tooltips.CardTooltipController.RebuildLayoutForArrows（清零告警 #2，使 Tooltip 降载生效）
        var cardTooltipType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.CardTooltipController");
        if (cardTooltipType != null)
        {
            _tooltipPositioningRectProp = cardTooltipType.GetProperty("PositioningRectTransform", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _tooltipIsPositioningSecondaryProp = cardTooltipType.GetProperty("IsPositioningSecondaryTooltip", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _tooltipSecondaryAnimatorProp = cardTooltipType.GetProperty("SecondaryTooltipAnimator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_tooltipSecondaryAnimatorProp != null)
            {
                _secondaryAnimatorShowArrowsMethod = _tooltipSecondaryAnimatorProp.PropertyType.GetMethod(
                    "ShowArrowsWithLayoutWait", BindingFlags.Public | BindingFlags.Instance);
            }

            _tooltipSetPositioningSideMethod = cardTooltipType.GetMethod(
                "SetPositioningSide", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (_tooltipSetPositioningSideMethod != null)
            {
                var sideParams = _tooltipSetPositioningSideMethod.GetParameters();
                if (sideParams.Length == 1 && sideParams[0].ParameterType.IsEnum)
                {
                    try { _tooltipSideNoneValue = Enum.Parse(sideParams[0].ParameterType, "None"); } catch { }
                    patcher.TryPatchMethod(
                        _tooltipSetPositioningSideMethod,
                        postfix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(CardTooltipSetPositioningSidePostfix)),
                        optional: true,
                        label: "Perf.A9.CardTooltipController.SetPositioningSide");
                }
            }

            patcher.TryPatch(
                cardTooltipType,
                "RebuildLayoutForArrows",
                Type.EmptyTypes,
                prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(TooltipRebuildLayoutPrefix)),
                optional: true,
                label: "Perf.A9.CardTooltipController.RebuildLayoutForArrows");
        }

        // A-9 补充：拦截 DesktopTooltipPositioningStrategy.SecondaryPositioningCoroutine 消除重复全 Canvas 重建
        var desktopTipPosType = GameInspector.FindTypeSafe("TheBazaar.UI.Tooltips.DesktopTooltipPositioningStrategy");
        if (desktopTipPosType != null && cardTooltipType != null)
        {
            _desktopOnPositioningCompleteField = desktopTipPosType.GetField(
                "OnPositioningComplete", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            patcher.TryPatch(
                desktopTipPosType,
                "SecondaryPositioningCoroutine",
                new[] { cardTooltipType, cardTooltipType },
                prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(SecondaryPositioningCoroutinePrefix)),
                optional: true,
                label: "Perf.A9.DesktopTooltipPositioningStrategy.SecondaryPositioningCoroutine");
        }

        // A-10: 单点拦截私有 TheBazaar.AppLogger.LogMessage，按 (int)logType == 0 (Debug) 零装箱静默（清零告警 #3/#4，完整保留 Warning/Error）
        var appLogger = GameInspector.FindTypeSafe("TheBazaar.AppLogger", "AppLogger");
        if (appLogger != null)
        {
            MethodInfo logMsgMethod = null;
            foreach (var m in appLogger.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == "LogMessage" && m.GetParameters().Length >= 3)
                {
                    logMsgMethod = m;
                    break;
                }
            }
            if (logMsgMethod != null)
            {
                Type logTypeEnum = logMsgMethod.GetParameters()[2].ParameterType;
                MethodInfo openPrefix = AccessTools.Method(typeof(PerfOptimizerModule), nameof(AppLoggerLogMessagePrefixGeneric));
                MethodInfo closedPrefix = openPrefix?.MakeGenericMethod(logTypeEnum);
                if (closedPrefix != null)
                {
                    patcher.TryPatchMethod(
                        logMsgMethod,
                        prefix: closedPrefix,
                        optional: true,
                        label: "Perf.A10.AppLogger.LogMessage");
                }
            }
        }

        // 拖拽 Top 2：BoardManager.HandleCardHovers 槽位级去重 + ItemController.HoveredMove/MoveBack DOTween 状态守卫
        InstallDragHoverPerfPatches(patcher);
    }

    private static void InstallDragHoverPerfPatches(SafeHarmonyPatcher patcher)
    {
        try
        {
            var cardCtrlType = GameInspector.FindTypeSafe("CardController", "TheBazaar.CardController");
            if (cardCtrlType != null)
            {
                var moveEnField = cardCtrlType.GetField("movementEnabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var sectionField = cardCtrlType.GetField("boardSection", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var offsetField = cardCtrlType.GetField("socketOffset", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var scaleDestField = cardCtrlType.GetField("scaleDestination", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var isHoveringProp = cardCtrlType.GetProperty("IsHovering", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var seqField = cardCtrlType.GetField("MoveSequence", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (moveEnField != null)
                {
                    _cardGetMovementEnabled = CompileInstanceFieldGetter<bool>(cardCtrlType, moveEnField);
                }
                if (sectionField != null)
                {
                    _cardGetBoardSection = CompileEnumFieldIntGetter(cardCtrlType, sectionField);
                    try
                    {
                        _storageSectionValue = Convert.ToInt32(Enum.Parse(sectionField.FieldType, "Storage"));
                    }
                    catch { _storageSectionValue = -1; }
                }
                if (offsetField != null)
                {
                    _cardGetSocketOffset = CompileInstanceFieldGetter<Vector3>(cardCtrlType, offsetField);
                }
                if (scaleDestField != null)
                {
                    _cardGetScaleDestination = CompileInstanceFieldGetter<Vector3>(cardCtrlType, scaleDestField);
                }
                if (isHoveringProp != null)
                {
                    _cardGetIsHovering = CompileInstancePropertyGetter<bool>(cardCtrlType, isHoveringProp);
                }
                if (seqField != null)
                {
                    _cardGetMoveSequence = CompileInstanceFieldGetter<object>(cardCtrlType, seqField);
                    _tweenStringIdField = seqField.FieldType.GetField("stringId", BindingFlags.Public | BindingFlags.Instance)
                        ?? seqField.FieldType.BaseType?.GetField("stringId", BindingFlags.Public | BindingFlags.Instance);
                }
            }

            var bmType = GameInspector.FindTypeSafe("BoardManager");
            var itemCtrlType = GameInspector.FindTypeSafe("ItemController", "TheBazaar.ItemController");
            if (bmType != null)
            {
                patcher.TryPatch(
                    bmType,
                    "HandleCardHovers",
                    prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(BoardHandleCardHoversPrefix)),
                    optional: true,
                    label: "Perf.DragTop2.BoardManager.HandleCardHovers");

                patcher.TryPatch(
                    bmType,
                    "ResetAllHovers",
                    postfix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(BoardResetAllHoversPostfix)),
                    optional: true,
                    label: "Perf.DragTop2.BoardManager.ResetAllHovers");
            }

            if (itemCtrlType != null)
            {
                patcher.TryPatch(
                    itemCtrlType,
                    "HoveredMove",
                    Type.EmptyTypes,
                    prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(ItemHoveredMovePrefix)),
                    optional: true,
                    label: "Perf.DragTop2.ItemController.HoveredMove");

                patcher.TryPatch(
                    itemCtrlType,
                    "MoveBack",
                    Type.EmptyTypes,
                    prefix: AccessTools.Method(typeof(PerfOptimizerModule), nameof(ItemMoveBackPrefix)),
                    optional: true,
                    label: "Perf.DragTop2.ItemController.MoveBack");
            }
        }
        catch (Exception ex)
        {
            BazaarLauncherPlugin.Log?.LogWarning("[PerfOptimizer] 安装拖拽悬停状态守卫异常: " + ex.Message);
        }
    }

    private static Func<object, T> CompileInstanceFieldGetter<T>(Type declaringType, FieldInfo field)
    {
        try
        {
            var p = Expression.Parameter(typeof(object), "inst");
            var access = Expression.Field(Expression.Convert(p, declaringType), field);
            Expression body = typeof(T) == field.FieldType ? access : Expression.Convert(access, typeof(T));
            return Expression.Lambda<Func<object, T>>(body, p).Compile();
        }
        catch
        {
            return obj => (T)field.GetValue(obj);
        }
    }

    private static Func<object, T> CompileInstancePropertyGetter<T>(Type declaringType, PropertyInfo prop)
    {
        try
        {
            var p = Expression.Parameter(typeof(object), "inst");
            var access = Expression.Property(Expression.Convert(p, declaringType), prop);
            Expression body = typeof(T) == prop.PropertyType ? access : Expression.Convert(access, typeof(T));
            return Expression.Lambda<Func<object, T>>(body, p).Compile();
        }
        catch
        {
            return obj => (T)prop.GetValue(obj);
        }
    }

    private static Func<object, int> CompileEnumFieldIntGetter(Type declaringType, FieldInfo field)
    {
        try
        {
            var p = Expression.Parameter(typeof(object), "inst");
            var access = Expression.Field(Expression.Convert(p, declaringType), field);
            var cast = Expression.Convert(access, typeof(int));
            return Expression.Lambda<Func<object, int>>(cast, p).Compile();
        }
        catch
        {
            return obj => Convert.ToInt32(field.GetValue(obj));
        }
    }

    // ── 各优化项 Harmony 补丁体（全员顶层 try/catch 防异常穿透）──────────────

    private static void LogPerfHookErrorThrottled(string hookName, Exception ex)
    {
        int cnt = Interlocked.Increment(ref _perfHookErrorCount);
        if (cnt <= 5 || cnt % 50 == 0)
        {
            BazaarLauncherPlugin.Log?.LogWarning($"[PerfOptimizer] {hookName} 异常已拦截 (count={cnt}): {ex.Message}");
        }
    }

    private static void CalculateTargetFrameRateInMSPostfix(int frameRate, ref float __result)
    {
        try
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
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(CalculateTargetFrameRateInMSPostfix), ex);
        }
    }

    private static bool GaussianBlurExecutePrefix()
    {
        try
        {
            // A-2：极致性能模式下跳过高填充率全屏高斯模糊 Pass（原版暗角遮罩仍保留）
            return !OptBlurDownsampleOrSkip;
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(GaussianBlurExecutePrefix), ex);
            return true;
        }
    }

    private static bool CameraQualitySubscribeGuardPrefix(object __instance)
    {
        try
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
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(CameraQualitySubscribeGuardPrefix), ex);
        }
        return true;
    }

    private static void CameraQualityOnDestroyPostfix(object __instance)
    {
        try
        {
            if (__instance is not UnityEngine.Object uo) return;
            int id = uo.GetInstanceID();
            lock (_cameraSubscribedIds)
            {
                _cameraSubscribedIds.Remove(id);
            }
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(CameraQualityOnDestroyPostfix), ex);
        }
    }

    private static FieldInfo _soundMgrPlayingField;
    private static PropertyInfo _soundMgrPlayingProp;
    private static FieldInfo _soundtrackPathField;
    private static PropertyInfo _soundtrackPathProp;
    private static string _lastLoadedSoundtrackPath;
    private static float _lastSoundtrackLoadUnscaledTime = -100f;

    /// <summary>
    /// A-6 真实音频句柄保护：原版 SoundManager.LoadSoundtrack 每次调用都会 Addressables.LoadAssetAsync 且不释放旧句柄；
    /// 当短时间内针对同一首正在播放的 SoundtrackPath 重复触发加载时直接跳过，避免重复泄漏异步句柄与重装 FMOD Bank。
    /// </summary>
    private static bool SoundManagerLoadGuardPrefix(object __instance, object __0)
    {
        try
        {
            if (!OptAudioHandleGuard || __instance == null || __0 == null) return true;

            Type soType = __0.GetType();
            _soundtrackPathProp ??= soType.GetProperty("SoundtrackPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _soundtrackPathField ??= soType.GetField("SoundtrackPath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            string reqPath = (_soundtrackPathProp?.GetValue(__0) ?? _soundtrackPathField?.GetValue(__0)) as string;
            if (string.IsNullOrWhiteSpace(reqPath)) return true;

            Type smType = __instance.GetType();
            _soundMgrPlayingProp ??= smType.GetProperty("PlayingSoundTrackSO", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _soundMgrPlayingField ??= smType.GetField("PlayingSoundTrackSO", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            object curPlaying = _soundMgrPlayingProp?.GetValue(__instance) ?? _soundMgrPlayingField?.GetValue(__instance);

            float now = Time.unscaledTime;
            if (curPlaying != null && !(curPlaying is UnityEngine.Object uo && uo == null))
            {
                string curPath = (_soundtrackPathProp?.GetValue(curPlaying) ?? _soundtrackPathField?.GetValue(curPlaying)) as string;
                if (string.Equals(curPath, reqPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_lastLoadedSoundtrackPath, reqPath, StringComparison.OrdinalIgnoreCase)
                    && (now - _lastSoundtrackLoadUnscaledTime) < 3.0f)
                {
                    return false;
                }
            }

            _lastLoadedSoundtrackPath = reqPath;
            _lastSoundtrackLoadUnscaledTime = now;
        }
        catch { }
        return true;
    }

    private static bool AssetLoaderDoesAddressExistPrefix(string address, ref bool __result)
    {
        try
        {
            if (!OptAssetLoaderCacheAndLru || string.IsNullOrEmpty(address)) return true;
            if (_addressExistsCache.TryGetValue(address, out bool cached))
            {
                __result = cached;
                return false;
            }
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(AssetLoaderDoesAddressExistPrefix), ex);
        }
        return true;
    }

    private static void AssetLoaderDoesAddressExistPostfix(string address, bool __result)
    {
        try
        {
            if (!OptAssetLoaderCacheAndLru || string.IsNullOrEmpty(address)) return;
            if (_addressExistsCache.Count > 8192)
            {
                _addressExistsCache.Clear();
            }
            _addressExistsCache[address] = __result;
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(AssetLoaderDoesAddressExistPostfix), ex);
        }
    }

    private static bool MainMenuRefreshNavPrefix(object __instance)
    {
        try
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
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(MainMenuRefreshNavPrefix), ex);
        }
        return true;
    }

    private static bool TooltipRebuildLayoutPrefix(object __instance)
    {
        try
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
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(TooltipRebuildLayoutPrefix), ex);
        }
        return true;
    }

    private static IEnumerator EmptyEnumerator()
    {
        yield break;
    }

    private static void CardTooltipSetPositioningSidePostfix(object __instance, object side)
    {
        try
        {
            if (__instance is not UnityEngine.Object uo || uo == null || side == null)
                return;
            if (_tooltipSideNoneValue != null && Equals(side, _tooltipSideNoneValue))
                return;
            if (_lastSecTipValidSideByCtrlId.Count > 32)
            {
                _lastSecTipValidSideByCtrlId.Clear();
            }
            _lastSecTipValidSideByCtrlId[uo.GetInstanceID()] = side;
        }
        catch { }
    }

    /// <summary>P1-47：节流跳过副 Tooltip 重建时，恢复 ResetPositioningSide() 清空的侧向并补发箭头动画与 OnPositioningComplete 回调。</summary>
    private static IEnumerator SkippedSecondaryPositioningEnumerator(object strategy, object controller, object primary, object cachedSide)
    {
        try
        {
            if (controller is Component ctrlComp && ctrlComp != null
                && primary is Component priComp && priComp != null
                && priComp.gameObject.activeInHierarchy)
            {
                _tooltipSetPositioningSideMethod?.Invoke(controller, new[] { cachedSide });

                var secAnim = _tooltipSecondaryAnimatorProp?.GetValue(primary);
                if (secAnim != null && _secondaryAnimatorShowArrowsMethod != null)
                {
                    _secondaryAnimatorShowArrowsMethod.Invoke(secAnim, new[] { cachedSide });
                }

                if (strategy != null && _desktopOnPositioningCompleteField?.GetValue(strategy) is Delegate onComplete)
                {
                    onComplete.DynamicInvoke(controller);
                }
            }
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(SkippedSecondaryPositioningEnumerator), ex);
        }
        yield break;
    }

    /// <summary>A-9 补充 + P1-47：短时间内对同一对主副升级 Tooltip 且尺寸与锚点未发生显著变化时，跳过重复的全局 Canvas.ForceUpdateCanvases，并补发 ShowArrowsWithLayoutWait 与 OnPositioningComplete。</summary>
    private static bool SecondaryPositioningCoroutinePrefix(object __instance, object controller, object primary, ref IEnumerator __result)
    {
        try
        {
            if (!OptTooltipLayoutThrottle || controller is not Component ctrlComp || primary is not Component priComp)
                return true;
            if (ctrlComp == null || priComp == null || _tooltipPositioningRectProp == null)
                return true;

            if (_tooltipIsPositioningSecondaryProp?.GetValue(controller) is bool busy && busy)
            {
                __result = EmptyEnumerator();
                return false;
            }

            var ctrlRect = _tooltipPositioningRectProp.GetValue(controller) as RectTransform;
            var priRect = _tooltipPositioningRectProp.GetValue(primary) as RectTransform;
            if (ctrlRect == null || priRect == null) return true;

            int frame = Time.frameCount;
            int ctrlId = ctrlComp.GetInstanceID();
            int priId = priComp.GetInstanceID();
            Vector2 priSize = priRect.rect.size;
            Vector2 ctrlSize = ctrlRect.rect.size;
            Vector2 priPos = priRect.anchoredPosition;

            if (ctrlId == _lastSecTipCtrlId && priId == _lastSecTipPrimaryId
                && frame - _lastSecTipFrame <= 2
                && ctrlComp.gameObject.activeInHierarchy
                && priSize.x > 1f && priSize.y > 1f && ctrlSize.x > 1f && ctrlSize.y > 1f
                && Mathf.Abs(priSize.x - _lastSecTipPrimarySize.x) < 0.5f
                && Mathf.Abs(priSize.y - _lastSecTipPrimarySize.y) < 0.5f
                && Mathf.Abs(ctrlSize.x - _lastSecTipCtrlSize.x) < 0.5f
                && Mathf.Abs(ctrlSize.y - _lastSecTipCtrlSize.y) < 0.5f
                && Mathf.Abs(priPos.x - _lastSecTipPrimaryAnchoredPos.x) < 0.5f
                && Mathf.Abs(priPos.y - _lastSecTipPrimaryAnchoredPos.y) < 0.5f
                && _tooltipSetPositioningSideMethod != null
                && _desktopOnPositioningCompleteField != null
                && _lastSecTipValidSideByCtrlId.TryGetValue(ctrlId, out var cachedSide)
                && cachedSide != null
                && (_tooltipSideNoneValue == null || !Equals(cachedSide, _tooltipSideNoneValue)))
            {
                __result = SkippedSecondaryPositioningEnumerator(__instance, controller, primary, cachedSide);
                return false;
            }

            _lastSecTipCtrlId = ctrlId;
            _lastSecTipPrimaryId = priId;
            _lastSecTipFrame = frame;
            _lastSecTipPrimarySize = priSize;
            _lastSecTipCtrlSize = ctrlSize;
            _lastSecTipPrimaryAnchoredPos = priPos;
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(SecondaryPositioningCoroutinePrefix), ex);
        }
        return true;
    }

    /// <summary>A-10：零装箱静默 TheBazaar.AppLogger.LogType.Debug (=0)，完整保留 Warning(1) / Error(2) 日志。</summary>
    private static bool AppLoggerLogMessagePrefixGeneric<TLogType>(TLogType logType)
    {
        try
        {
            if (OptMuteVerboseAppLogger && EqualityComparer<TLogType>.Default.Equals(logType, default))
            {
                return false;
            }
        }
        catch { }
        return true;
    }

    /// <summary>拖拽 Top 2：BoardManager.HandleCardHovers 槽位级脏标去重，同槽位微移时 0 遍历直接跳过。</summary>
    private static bool BoardHandleCardHoversPrefix(object draggingCard, EContainerSocketId[] sockets, object section)
    {
        try
        {
            if (!OptDragHoverGuard || draggingCard is not UnityEngine.Object dragUo || dragUo == null)
                return true;

            int dragId = dragUo.GetInstanceID();
            int secInt = section != null ? Convert.ToInt32(section) : -1;
            int len = sockets?.Length ?? 0;
            ulong mask = 0UL;
            int hash = 17;
            if (sockets != null)
            {
                for (int i = 0; i < len; i++)
                {
                    int v = (int)sockets[i];
                    if ((uint)v < 64u) mask |= 1UL << v;
                    hash = unchecked((hash * 31) + v);
                }
            }

            if (dragId == _lastHoverDragCardId
                && secInt == _lastHoverSection
                && len == _lastHoverSocketsLen
                && mask == _lastHoverSocketsMask
                && hash == _lastHoverSocketsHash)
            {
                return false;
            }

            _lastHoverDragCardId = dragId;
            _lastHoverSection = secInt;
            _lastHoverSocketsLen = len;
            _lastHoverSocketsMask = mask;
            _lastHoverSocketsHash = hash;
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(BoardHandleCardHoversPrefix), ex);
        }
        return true;
    }

    private static void BoardResetAllHoversPostfix()
    {
        try
        {
            ResetDragHoverCaches();
        }
        catch { }
    }

    /// <summary>拖拽 Top 2：ItemController.HoveredMove 状态守卫，防止重复 Kill(complete:true) 并重建同目标 DOTween 引发抖动与 GC。</summary>
    private static bool ItemHoveredMovePrefix(Component __instance)
    {
        try
        {
            if (!OptDragHoverGuard || __instance == null || _cardGetMovementEnabled == null || _cardGetBoardSection == null || _cardGetSocketOffset == null)
                return true;

            if (!_cardGetMovementEnabled(__instance)) return true;

            int id = __instance.GetInstanceID();
            int curSection = _cardGetBoardSection(__instance);
            Vector3 curOffset = _cardGetSocketOffset(__instance);
            object moveSeq = _cardGetMoveSequence?.Invoke(__instance);
            string seqId = moveSeq != null ? (_tweenStringIdField?.GetValue(moveSeq) as string) : null;

            if (_itemHoverVisualStates.TryGetValue(id, out var prev)
                && prev.IsHovered
                && prev.Section == curSection
                && (prev.SocketOffset - curOffset).sqrMagnitude < 1e-6f)
            {
                if (string.Equals(seqId, "HoveredMove", StringComparison.Ordinal))
                {
                    // 已经在向悬停偏置位置过渡，绝不重复强杀重建
                    return false;
                }
                if (moveSeq == null)
                {
                    Vector3 baseBackPos = curSection == _storageSectionValue
                        ? curOffset + new Vector3(0f, 0.08f, 0f)
                        : curOffset;
                    if ((__instance.transform.localPosition - baseBackPos).sqrMagnitude > 1e-5f)
                    {
                        // 已到位且仍处于悬停偏置态，跳过重建
                        return false;
                    }
                }
            }

            Vector3 curScaleDest = _cardGetScaleDestination != null ? _cardGetScaleDestination(__instance) : __instance.transform.localScale;
            if (_itemHoverVisualStates.Count > 128) _itemHoverVisualStates.Clear();
            _itemHoverVisualStates[id] = new ItemHoverVisualState
            {
                IsHovered = true,
                Section = curSection,
                SocketOffset = curOffset,
                ScaleDestination = curScaleDest,
            };
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(ItemHoveredMovePrefix), ex);
        }
        return true;
    }

    /// <summary>拖拽 Top 2 + P1-47：ItemController.MoveBack 状态守卫，严格核查位置、localScale == scaleDestination 与 !IsHovering 后再跳过，消除每帧全场 DOTween 重建同时绝不卡住缩放/悬停复位。</summary>
    private static bool ItemMoveBackPrefix(Component __instance)
    {
        try
        {
            if (!OptDragHoverGuard || __instance == null
                || _cardGetMovementEnabled == null
                || _cardGetBoardSection == null
                || _cardGetSocketOffset == null
                || _cardGetScaleDestination == null
                || _cardGetIsHovering == null)
                return true;

            if (!_cardGetMovementEnabled(__instance)) return true;

            int id = __instance.GetInstanceID();
            int curSection = _cardGetBoardSection(__instance);
            Vector3 curOffset = _cardGetSocketOffset(__instance);
            Vector3 scaleDest = _cardGetScaleDestination(__instance);
            bool isHovering = _cardGetIsHovering(__instance);
            object moveSeq = _cardGetMoveSequence?.Invoke(__instance);
            string seqId = moveSeq != null ? (_tweenStringIdField?.GetValue(moveSeq) as string) : null;

            Vector3 targetBackPos = curSection == _storageSectionValue
                ? curOffset + new Vector3(0f, 0.08f, 0f)
                : curOffset;

            // 1. 仅当无活跃序列、位置已在精确原位、缩放已等于 scaleDestination 且未处于 IsHovering 态时才跳过 MoveBack DOTween
            if (moveSeq == null
                && !isHovering
                && (__instance.transform.localPosition - targetBackPos).sqrMagnitude < 1e-5f
                && (__instance.transform.localScale - scaleDest).sqrMagnitude < 1e-5f)
            {
                if (_itemHoverVisualStates.Count > 128) _itemHoverVisualStates.Clear();
                _itemHoverVisualStates[id] = new ItemHoverVisualState
                {
                    IsHovered = false,
                    Section = curSection,
                    SocketOffset = curOffset,
                    ScaleDestination = scaleDest,
                };
                return false;
            }

            // 2. 若已处于 MoveBack 回位动画中且目标位置/目标缩放未变、且 IsHovering 已复位，则放行当前动画平滑播完
            if (_itemHoverVisualStates.TryGetValue(id, out var prev)
                && !prev.IsHovered
                && !isHovering
                && prev.Section == curSection
                && (prev.SocketOffset - curOffset).sqrMagnitude < 1e-6f
                && (prev.ScaleDestination - scaleDest).sqrMagnitude < 1e-6f
                && string.Equals(seqId, "MoveBack", StringComparison.Ordinal))
            {
                return false;
            }

            if (_itemHoverVisualStates.Count > 128) _itemHoverVisualStates.Clear();
            _itemHoverVisualStates[id] = new ItemHoverVisualState
            {
                IsHovered = false,
                Section = curSection,
                SocketOffset = curOffset,
                ScaleDestination = scaleDest,
            };
        }
        catch (Exception ex)
        {
            LogPerfHookErrorThrottled(nameof(ItemMoveBackPrefix), ex);
        }
        return true;
    }

    // ── 战斗态与切场景安全调度（严禁战斗中触发 GC/卸载！） ──────────────────

    private void OnCombatLifecycle(CombatLifecycleModEvent evt)
    {
        _inCombat = evt.Started;
        MainThreadDispatcher.Enqueue(() => ApplyIncrementalGcSlice(_inCombat));
    }

    private void OnSceneChanged(SceneChangedModEvent evt)
    {
        _inCombat = false;
        ResetDragHoverCaches();
        // P1-43：CompatAuditPatch 已自主监听 SceneManager.activeSceneChanged 与局生命周期，此处不再耦合触发 ClearRunScopedCaches
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
        _lastTelemetrySummaryTime = Time.unscaledTime;
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

            // 拖拽 Top 10：HUD 隐藏且性能页签未打开时跳过每 0.5s 的 Array.Sort 与 GC.GetTotalMemory
            if (!ShowPerfHudOverlay)
            {
                if (_hudRoot != null && _hudRoot.activeSelf) _hudRoot.SetActive(false);
                if (!CustomGameplayUI.IsPerformanceTabVisible)
                {
                    return;
                }
                _owner.RefreshTelemetrySummary();
                return;
            }

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
            var uiFont = CustomGameplayUI.Instance?.GetActiveUiFont();
            if (uiFont != null && _hudText.font != uiFont)
            {
                _hudText.font = uiFont;
            }

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
            var uiFont = CustomGameplayUI.Instance?.GetActiveUiFont();
            if (uiFont != null)
            {
                _hudText.font = uiFont;
            }
            _hudText.fontSize = 14f;
            _hudText.color = new Color(0.95f, 0.85f, 0.45f, 1f);
            _hudText.alignment = TextAlignmentOptions.MidlineLeft;
            _hudText.raycastTarget = false;
            _hudText.textWrappingMode = TextWrappingModes.NoWrap;
        }
    }
}

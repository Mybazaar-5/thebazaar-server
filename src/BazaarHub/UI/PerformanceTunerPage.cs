using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 页面 3：「本体性能调优」。
/// 提供三档可视化性能预设卡片、原版保护安全横幅与 5 组对齐规整的滑动开关/下拉配置项，
/// 100% 保留首次原版只读备份与逐字节还原纯净官方原版能力。
/// </summary>
public sealed class PerformanceTunerPage : UserControl
{
    private readonly GoldCardPanel _backupBannerCard;
    private readonly StatusPill _pillBackupState;
    private readonly Label _lblBackupStatus;
    private readonly Label _lblArgsPreview;

    private readonly PresetOptionCard _presetRecommended;
    private readonly PresetOptionCard _presetExtreme;
    private readonly PresetOptionCard _presetPure;

    // 1. boot.config
    private readonly GoldToggleSwitch _chkBootConfig;
    private readonly GoldToggleSwitch _chkNativeGfxJobs;
    private readonly GoldToggleSwitch _chkNativeMemAlloc;
    private readonly GoldComboBox _cmbGcSlice;
    private readonly GoldComboBox _cmbJobWorkers;

    // 2. Unity 启动参数
    private readonly GoldToggleChip _chkArgNoLog;
    private readonly GoldToggleChip _chkArgGcInc;
    private readonly GoldToggleChip _chkArgBorderless;
    private readonly GoldToggleChip _chkArgD3D11;
    private readonly GoldToggleChip _chkArgHighPrio;

    // 3. Addressables settings.json
    private readonly GoldToggleSwitch _chkAaSettings;

    // 4. BepInEx.cfg
    private readonly GoldToggleSwitch _chkBepLogging;

    // 5. BazaarLauncher 性能项
    private readonly GoldToggleSwitch _chkLauncherPerf;
    private readonly GoldComboBox _cmbForegroundFps;
    private readonly GoldComboBox _cmbBackgroundFps;
    private readonly GoldToggleSwitch _chkLazyCosmetics;
    private readonly GoldToggleSwitch _chkReduceVfx;

    private bool _suppressPresetRefresh;

    public event Action<PerformanceTuningOptions>? ApplyTuningRequested;
    public event Action? RestorePureOfficialRequested;

    public PerformanceTunerPage()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Padding = new Padding(24, 18, 24, 14);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(outer);
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));  // 标题 + 应用/还原操作
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));  // 原版保护安全横幅
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // 可滚动预设卡 + 5 项调优卡

        // ── 1. 顶部标题与核心操作 ────────────────────────────────────────
        var btnApplyCurrent = new GoldButton
        {
            Text = "应用当前配置",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Save,
            Size = new Size(134, 34),
            Margin = new Padding(0, 0, 8, 0)
        };
        btnApplyCurrent.Click += (_, _) => ApplyTuningRequested?.Invoke(CollectOptions());

        var btnRestore = new GoldButton
        {
            Text = "一键还原纯净官方原版",
            Variant = GoldButtonVariant.DangerRed,
            Icon = HubIconKind.Shield,
            Size = new Size(188, 34),
            Margin = new Padding(0)
        };
        btnRestore.Click += (_, _) => RestorePureOfficialRequested?.Invoke();

        var header = new SectionHeaderBar(
            "本体性能调优（安全可逆 · 原版零损坏）",
            @"任何修改前均自动在 <游戏目录>\_BazaarHubBackups\original\ 建立首次原版只读备份，随时逐字节 100% 还原。",
            btnApplyCurrent,
            btnRestore);
        outer.Controls.Add(header, 0, 0);

        // ── 2. 原版保护安全横幅 + 一键推荐应用 ────────────────────────────
        _backupBannerCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Highlighted = true,
            AccentBarColor = HubTheme.StatusBlue,
            Padding = new Padding(18, 10, 16, 10),
            Margin = new Padding(0, 0, 0, 12)
        };
        var bannerGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bannerGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174));

        _pillBackupState = new StatusPill { Height = 26, Margin = new Padding(0, 6, 12, 0) };
        _pillBackupState.SetState(PillTone.Blue, "原版保护就绪", showDot: false, icon: HubIconKind.Shield);

        _lblBackupStatus = new Label
        {
            Text = "等待检测游戏目录以校验原版基线备份状态...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        var btnRecommended = new GoldButton
        {
            Text = "一键应用推荐优化",
            Variant = GoldButtonVariant.PrimaryGold,
            Icon = HubIconKind.Sparkle,
            Dock = DockStyle.Fill,
            Margin = new Padding(10, 2, 0, 2)
        };
        btnRecommended.Click += (_, _) =>
        {
            PerformanceTuningOptions preset = PerformanceTuningOptions.CreateRecommended();
            LoadOptions(preset);
            ApplyTuningRequested?.Invoke(CollectOptions());
        };

        bannerGrid.Controls.Add(_pillBackupState, 0, 0);
        bannerGrid.Controls.Add(_lblBackupStatus, 1, 0);
        bannerGrid.Controls.Add(btnRecommended, 2, 0);
        _backupBannerCard.Controls.Add(bannerGrid);
        outer.Controls.Add(_backupBannerCard, 0, 1);

        // ── 3. 可滚动主体：三档预设卡片 + 5 组精密配置卡 ──────────────────
        var scrollHost = new DarkScrollPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 0, 8, 8)
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(stack);
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 3A. 三档性能预设卡片组
        var presetGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 104,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12)
        };
        presetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        presetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        presetGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        _presetRecommended = new PresetOptionCard(
            HubIconKind.Sparkle,
            "均衡流畅预设",
            "推荐",
            "启用 Native Gfx Jobs、2ms 增量 GC、冷启动加速、60 FPS 稳帧与藏品懒加载，兼顾画质与稳定。",
            HubTheme.Gold)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0)
        };
        _presetRecommended.Click += (_, _) => LoadOptions(PerformanceTuningOptions.CreateRecommended());

        _presetExtreme = new PresetOptionCard(
            HubIconKind.Tuning,
            "极致低配 / 核显减负",
            "强力降载",
            "开启无边框低延迟、DX11 渲染回退、高并发战斗特效降载与后台 15 FPS，大幅压降显卡与 CPU 压力。",
            HubTheme.StatusGreen)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0)
        };
        _presetExtreme.Click += (_, _) => LoadOptions(PerformanceTuningOptions.CreateExtremeLowSpec());

        _presetPure = new PresetOptionCard(
            HubIconKind.Official,
            "纯净官方默认",
            "零改动",
            "关闭全部自定义性能调优项与额外启动参数，保持客户端原生默认行为（实际还原请点右上角红色按钮）。",
            HubTheme.StatusBlue)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _presetPure.Click += (_, _) => LoadOptions(PerformanceTuningOptions.CreatePureOfficialDefault());

        presetGrid.Controls.Add(_presetRecommended, 0, 0);
        presetGrid.Controls.Add(_presetExtreme, 1, 0);
        presetGrid.Controls.Add(_presetPure, 2, 0);
        stack.Controls.Add(presetGrid);

        // ── 卡片 1：boot.config 底层引擎管线 ─────────────────────────────
        (GoldCardPanel cardBoot, TableLayoutPanel bootBody) = CreateSectionCard(
            "01 · TheBazaar_Data\\boot.config 底层引擎管线优化",
            "启用 Unity 6 原生多线程渲染录制、收紧增量 GC 单帧时间片并增大原生内存块，渲染线程耗时降低 10%–20%。");

        _chkBootConfig = new GoldToggleSwitch { Checked = true };
        _chkNativeGfxJobs = new GoldToggleSwitch { Checked = true };
        _chkNativeMemAlloc = new GoldToggleSwitch { Checked = true };

        _cmbGcSlice = new GoldComboBox { Width = 140, Height = 30 };
        _cmbGcSlice.Items.AddRange(new object[] { "2 ms（推荐）", "3 ms（原版）", "1 ms（极致）" });
        _cmbGcSlice.SelectedIndex = 0;

        _cmbJobWorkers = new GoldComboBox { Width = 140, Height = 30 };
        _cmbJobWorkers.Items.AddRange(new object[] { "4 线程", "5 线程", "6 线程", "7 线程", "8 线程" });
        _cmbJobWorkers.SelectedIndex = Math.Clamp(PerformanceTuningOptions.DefaultRecommendedJobWorkerCount() - 4, 0, 4);

        bootBody.Controls.Add(new SettingRowControl(
            "启用 boot.config 底层优化总开关",
            "自动完整保留原版前 6 行配置与 build-guid，仅追加安全调优参数。",
            _chkBootConfig, showDivider: true, emphasizeTitle: true));
        bootBody.Controls.Add(new SettingRowControl(
            "Native Gfx Jobs 多线程渲染录制",
            "提升多核 CPU 渲染提交效率；若极少数老旧核显出现花屏可单独关闭。",
            _chkNativeGfxJobs));
        bootBody.Controls.Add(new SettingRowControl(
            "增大原生主/临时内存分配块（16MB / 8MB）",
            "减少大场面战斗频繁向系统申请小块原生内存造成的微卡顿。",
            _chkNativeMemAlloc));
        bootBody.Controls.Add(new SettingRowControl(
            "增量 GC 单帧最大时间片",
            "限制单帧垃圾回收耗时上限，平滑帧时间曲线。",
            _cmbGcSlice));
        bootBody.Controls.Add(new SettingRowControl(
            "Unity Job Worker 工作线程数",
            $"当前主机检测到 {Environment.ProcessorCount} 逻辑核心，默认推荐 {PerformanceTuningOptions.DefaultRecommendedJobWorkerCount()} 线程。",
            _cmbJobWorkers, showDivider: false));

        stack.Controls.Add(cardBoot);

        // ── 卡片 2：Unity 启动命令行参数 ─────────────────────────────────
        (GoldCardPanel cardArgs, TableLayoutPanel argsBody) = CreateSectionCard(
            "02 · Unity 启动命令行参数组合",
            "-nolog 消除 Player.log 主线程同步刷盘卡顿，-gc-incremental 强制激活增量 GC，-high 提升进程调度优先级。");

        var argsChipsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 4, 0, 8)
        };
        argsChipsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        argsChipsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        argsChipsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        _chkArgNoLog = new GoldToggleChip { Text = "-nolog（禁写日志降卡顿）", Checked = true, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 8, 8) };
        _chkArgGcInc = new GoldToggleChip { Text = "-gc-incremental（增量 GC）", Checked = true, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 8, 8) };
        _chkArgHighPrio = new GoldToggleChip { Text = "-high（高优先级稳帧）", Checked = true, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 0, 8) };
        _chkArgBorderless = new GoldToggleChip { Text = "-window-mode borderless", Checked = false, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 8, 0) };
        _chkArgD3D11 = new GoldToggleChip { Text = "-force-d3d11（回退 DX11）", Checked = false, Dock = DockStyle.Fill, Height = 34, Margin = new Padding(0, 0, 8, 0) };

        argsChipsGrid.Controls.Add(_chkArgNoLog, 0, 0);
        argsChipsGrid.Controls.Add(_chkArgGcInc, 1, 0);
        argsChipsGrid.Controls.Add(_chkArgHighPrio, 2, 0);
        argsChipsGrid.Controls.Add(_chkArgBorderless, 0, 1);
        argsChipsGrid.Controls.Add(_chkArgD3D11, 1, 1);
        argsBody.Controls.Add(argsChipsGrid);

        var previewPanel = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            Height = 36,
            CornerRadius = 8,
            CardBackColor = HubTheme.BgInput,
            CardTopColor = HubTheme.BgInput,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 2, 0, 0)
        };
        _lblArgsPreview = new Label
        {
            Text = "启动参数预览：-high -nolog -gc-incremental",
            Font = HubTheme.FontMono,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        previewPanel.Controls.Add(_lblArgsPreview);
        argsBody.Controls.Add(previewPanel);
        stack.Controls.Add(cardArgs);

        // ── 卡片 3 & 4：冷启动加速与日志瘦身（并排双列更紧凑大气）────────
        var midSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12)
        };
        midSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        midSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        (GoldCardPanel cardAa, TableLayoutPanel aaBody) = CreateSectionCard(
            "03 · Addressables 冷启动加速",
            "关闭启动期远程 Catalog 超时探测与资源异常高频刷盘，冷启动进大厅缩短 1.5–4 秒。",
            bottomMargin: 0);
        cardAa.Margin = new Padding(0, 0, 6, 0);
        _chkAaSettings = new GoldToggleSwitch { Checked = true };
        aaBody.Controls.Add(new SettingRowControl(
            "优化 StreamingAssets\\aa\\settings.json",
            "m_DisableCatalogUpdateOnStart=true, m_LogResourceManagerExceptions=false",
            _chkAaSettings, showDivider: false, emphasizeTitle: true));

        (GoldCardPanel cardBep, TableLayoutPanel bepBody) = CreateSectionCard(
            "04 · BepInEx 日志链路瘦身",
            "关闭高开销控制台窗口与 Unity Debug.Log 反射转发，磁盘仅保留 Fatal/Error，显著降低 I/O。",
            bottomMargin: 0);
        cardBep.Margin = new Padding(6, 0, 0, 0);
        _chkBepLogging = new GoldToggleSwitch { Checked = true };
        bepBody.Controls.Add(new SettingRowControl(
            "优化 BepInEx\\config\\BepInEx.cfg",
            "生产游玩模式精简日志，排错时可随时一键还原完整日志。",
            _chkBepLogging, showDivider: false, emphasizeTitle: true));

        midSplit.Controls.Add(cardAa, 0, 0);
        midSplit.Controls.Add(cardBep, 1, 0);
        stack.Controls.Add(midSplit);

        // ── 卡片 5：BazaarLauncher 运行时性能配置 ────────────────────────
        (GoldCardPanel cardLauncher, TableLayoutPanel launcherBody) = CreateSectionCard(
            "05 · BazaarLauncher 游戏内运行时性能配置（[Performance]）",
            "前台稳帧省电、切后台自动降频释放 GPU、按需懒加载 396 套 3D 藏品及高倍速战斗特效降载。");

        _chkLauncherPerf = new GoldToggleSwitch { Checked = true };
        _cmbForegroundFps = new GoldComboBox { Width = 172, Height = 30 };
        _cmbForegroundFps.Items.AddRange(new object[] { "60 FPS（省电稳帧）", "90 FPS（高刷流畅）", "120 FPS（极速高刷）", "0（不限帧 / 随原版）" });
        _cmbForegroundFps.SelectedIndex = 0;

        _cmbBackgroundFps = new GoldComboBox { Width = 172, Height = 30 };
        _cmbBackgroundFps.Items.AddRange(new object[] { "20 FPS（推荐）", "15 FPS（极省电）", "30 FPS", "0（后台不降频）" });
        _cmbBackgroundFps.SelectedIndex = 0;

        _chkLazyCosmetics = new GoldToggleSwitch { Checked = true };
        _chkReduceVfx = new GoldToggleSwitch { Checked = false };

        launcherBody.Controls.Add(new SettingRowControl(
            "同步写入 local.thebazaar.bazaarlauncher.cfg 的 [Performance] 段",
            "开启后上述游戏内帧率与渲染策略将在启动时实时生效。",
            _chkLauncherPerf, showDivider: true, emphasizeTitle: true));
        launcherBody.Controls.Add(new SettingRowControl(
            "前台帧率上限（MaxForegroundFps）",
            "根据显示器刷新率与显卡散热需求合理限帧，减少满载啸叫。",
            _cmbForegroundFps));
        launcherBody.Controls.Add(new SettingRowControl(
            "后台失焦自动降频（BackgroundFps）",
            "切出游戏窗口时自动压低帧率，释放显卡与电量占用。",
            _cmbBackgroundFps));
        launcherBody.Controls.Add(new SettingRowControl(
            "藏品按需懒加载（LazyLoadCosmetics）",
            "跳过未穿戴 3D 棋盘/英雄皮肤的全量预加载，降低内存与进局耗时。",
            _chkLazyCosmetics));
        launcherBody.Controls.Add(new SettingRowControl(
            "高并发战斗特效降载（ReduceHighConcurrencyVfx）",
            "连发流派 / 高倍速大场面战斗中精简重叠粒子，大幅减少瞬间掉帧。",
            _chkReduceVfx, showDivider: false));

        stack.Controls.Add(cardLauncher);

        scrollHost.Controls.Add(stack);
        outer.Controls.Add(scrollHost, 0, 2);
        Controls.Add(outer);

        WireChangeEvents();
        UpdateArgsPreview();
        UpdatePresetSelectionHighlight();
    }

    private void WireChangeEvents()
    {
        foreach (GoldToggleChip chip in new[] { _chkArgNoLog, _chkArgGcInc, _chkArgHighPrio, _chkArgBorderless, _chkArgD3D11 })
        {
            chip.CheckedChanged += (_, _) =>
            {
                UpdateArgsPreview();
                UpdatePresetSelectionHighlight();
            };
        }

        foreach (GoldToggleSwitch sw in new[] { _chkBootConfig, _chkNativeGfxJobs, _chkNativeMemAlloc, _chkAaSettings, _chkBepLogging, _chkLauncherPerf, _chkLazyCosmetics, _chkReduceVfx })
        {
            sw.CheckedChanged += (_, _) => UpdatePresetSelectionHighlight();
        }

        foreach (GoldComboBox cmb in new[] { _cmbGcSlice, _cmbJobWorkers, _cmbForegroundFps, _cmbBackgroundFps })
        {
            cmb.SelectedIndexChanged += (_, _) => UpdatePresetSelectionHighlight();
        }
    }

    public void LoadOptions(PerformanceTuningOptions options)
    {
        _suppressPresetRefresh = true;
        try
        {
            _chkBootConfig.Checked = options.OptimizeBootConfig;
            _chkNativeGfxJobs.Checked = options.EnableNativeGfxJobs;
            _chkNativeMemAlloc.Checked = options.TuneNativeMemoryAllocators;
            _cmbGcSlice.SelectedIndex = options.GcMaxTimeSliceMs switch { 1 => 2, 3 => 1, _ => 0 };
            _cmbJobWorkers.SelectedIndex = Math.Clamp(options.JobWorkerCount - 4, 0, 4);

            _chkArgNoLog.Checked = options.ArgNoLog;
            _chkArgGcInc.Checked = options.ArgGcIncremental;
            _chkArgBorderless.Checked = options.ArgWindowModeBorderless;
            _chkArgD3D11.Checked = options.ArgForceD3D11;
            _chkArgHighPrio.Checked = options.ArgHighProcessPriority;

            _chkAaSettings.Checked = options.OptimizeAddressablesSettings;
            _chkBepLogging.Checked = options.OptimizeBepInExLogging;

            _chkLauncherPerf.Checked = options.ApplyLauncherPerfSettings;
            _cmbForegroundFps.SelectedIndex = options.MaxForegroundFps switch { 90 => 1, 120 => 2, 0 => 3, _ => 0 };
            _cmbBackgroundFps.SelectedIndex = options.BackgroundFps switch { 15 => 1, 30 => 2, 0 => 3, _ => 0 };
            _chkLazyCosmetics.Checked = options.LazyLoadCosmetics;
            _chkReduceVfx.Checked = options.ReduceHighConcurrencyVfx;
        }
        finally
        {
            _suppressPresetRefresh = false;
        }

        UpdateArgsPreview();
        UpdatePresetSelectionHighlight();
    }

    public PerformanceTuningOptions CollectOptions()
    {
        return new PerformanceTuningOptions
        {
            OptimizeBootConfig = _chkBootConfig.Checked,
            EnableNativeGfxJobs = _chkNativeGfxJobs.Checked,
            TuneNativeMemoryAllocators = _chkNativeMemAlloc.Checked,
            GcMaxTimeSliceMs = _cmbGcSlice.SelectedIndex switch { 1 => 3, 2 => 1, _ => 2 },
            JobWorkerCount = _cmbJobWorkers.SelectedIndex + 4,
            ArgNoLog = _chkArgNoLog.Checked,
            ArgGcIncremental = _chkArgGcInc.Checked,
            ArgWindowModeBorderless = _chkArgBorderless.Checked,
            ArgForceD3D11 = _chkArgD3D11.Checked,
            ArgHighProcessPriority = _chkArgHighPrio.Checked,
            OptimizeAddressablesSettings = _chkAaSettings.Checked,
            OptimizeBepInExLogging = _chkBepLogging.Checked,
            ApplyLauncherPerfSettings = _chkLauncherPerf.Checked,
            MaxForegroundFps = _cmbForegroundFps.SelectedIndex switch { 1 => 90, 2 => 120, 3 => 0, _ => 60 },
            BackgroundFps = _cmbBackgroundFps.SelectedIndex switch { 1 => 15, 2 => 30, 3 => 0, _ => 20 },
            LazyLoadCosmetics = _chkLazyCosmetics.Checked,
            ReduceHighConcurrencyVfx = _chkReduceVfx.Checked
        };
    }

    public void UpdateBackupBanner(string? gameDir, BackupManifest? manifest)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
        {
            _backupBannerCard.AccentBarColor = HubTheme.StatusYellow;
            _pillBackupState.SetState(PillTone.Amber, "等待定位目录", showDot: false, icon: HubIconKind.Warning);
            _lblBackupStatus.Text = "请先在「快速启动与选服」定位有效的游戏目录，再执行性能调优或一键还原。";
            _lblBackupStatus.ForeColor = HubTheme.StatusYellow;
            return;
        }

        int count = manifest?.Files.Count ?? 0;
        if (count == 0)
        {
            _backupBannerCard.AccentBarColor = HubTheme.StatusBlue;
            _pillBackupState.SetState(PillTone.Blue, "原版未改动", showDot: false, icon: HubIconKind.Shield);
            _lblBackupStatus.Text = @"尚未修改任何原版文件；首次应用优化将自动建立只读基线备份于 _BazaarHubBackups\original\。";
            _lblBackupStatus.ForeColor = HubTheme.TextSecondary;
        }
        else
        {
            _backupBannerCard.AccentBarColor = HubTheme.StatusGreen;
            _pillBackupState.SetState(PillTone.Green, $"已保护 {count} 份原版基线", showDot: false, icon: HubIconKind.Shield);
            _lblBackupStatus.Text = @"所有首次原版文件均已安全归档至 _BazaarHubBackups\original\，点击右上角红色按钮可随时逐字节还原。";
            _lblBackupStatus.ForeColor = HubTheme.StatusGreen;
        }
    }

    private void UpdateArgsPreview()
    {
        string preview = PerformanceTunerService.BuildFullLaunchArgsPreview(CollectOptions());
        _lblArgsPreview.Text = string.IsNullOrWhiteSpace(preview)
            ? "启动参数预览：（无附加参数，完全沿用游戏默认启动方式）"
            : $"启动参数预览：{preview}";
    }

    private void UpdatePresetSelectionHighlight()
    {
        if (_suppressPresetRefresh) return;
        PerformanceTuningOptions cur = CollectOptions();
        _presetRecommended.Selected = OptionsMatch(cur, PerformanceTuningOptions.CreateRecommended());
        _presetExtreme.Selected = OptionsMatch(cur, PerformanceTuningOptions.CreateExtremeLowSpec());
        _presetPure.Selected = OptionsMatch(cur, PerformanceTuningOptions.CreatePureOfficialDefault());
    }

    private static bool OptionsMatch(PerformanceTuningOptions a, PerformanceTuningOptions b)
    {
        return a.OptimizeBootConfig == b.OptimizeBootConfig
            && a.EnableNativeGfxJobs == b.EnableNativeGfxJobs
            && a.TuneNativeMemoryAllocators == b.TuneNativeMemoryAllocators
            && a.GcMaxTimeSliceMs == b.GcMaxTimeSliceMs
            && a.ArgNoLog == b.ArgNoLog
            && a.ArgGcIncremental == b.ArgGcIncremental
            && a.ArgWindowModeBorderless == b.ArgWindowModeBorderless
            && a.ArgForceD3D11 == b.ArgForceD3D11
            && a.ArgHighProcessPriority == b.ArgHighProcessPriority
            && a.OptimizeAddressablesSettings == b.OptimizeAddressablesSettings
            && a.OptimizeBepInExLogging == b.OptimizeBepInExLogging
            && a.ApplyLauncherPerfSettings == b.ApplyLauncherPerfSettings
            && a.MaxForegroundFps == b.MaxForegroundFps
            && a.BackgroundFps == b.BackgroundFps
            && a.LazyLoadCosmetics == b.LazyLoadCosmetics
            && a.ReduceHighConcurrencyVfx == b.ReduceHighConcurrencyVfx;
    }

    private static (GoldCardPanel Card, TableLayoutPanel Body) CreateSectionCard(
        string title, string benefit, int bottomMargin = 12)
    {
        var card = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, bottomMargin),
            Padding = new Padding(18, 14, 18, 12)
        };

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        body.Controls.Add(new Label
        {
            Text = title,
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 3)
        });

        body.Controls.Add(new Label
        {
            Text = benefit,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8)
        });

        card.Controls.Add(body);
        return (card, body);
    }
}

using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 标签页 3：「本体性能调优」。
/// 逐项开关 + 清晰中文收益说明 + 一键应用推荐优化 + 醒目「一键还原纯净官方原版」按钮。
/// </summary>
public sealed class PerformanceTunerPage : UserControl
{
    private readonly Label _lblBackupStatus;
    private readonly Label _lblArgsPreview;

    // 1. boot.config
    private readonly CheckBox _chkBootConfig;
    private readonly CheckBox _chkNativeGfxJobs;
    private readonly CheckBox _chkNativeMemAlloc;
    private readonly ComboBox _cmbGcSlice;
    private readonly ComboBox _cmbJobWorkers;

    // 2. Unity 启动参数
    private readonly CheckBox _chkArgNoLog;
    private readonly CheckBox _chkArgGcInc;
    private readonly CheckBox _chkArgBorderless;
    private readonly CheckBox _chkArgD3D11;
    private readonly CheckBox _chkArgHighPrio;

    // 3. Addressables settings.json
    private readonly CheckBox _chkAaSettings;

    // 4. BepInEx.cfg
    private readonly CheckBox _chkBepLogging;

    // 5. BazaarLauncher 性能项
    private readonly CheckBox _chkLauncherPerf;
    private readonly ComboBox _cmbForegroundFps;
    private readonly ComboBox _cmbBackgroundFps;
    private readonly CheckBox _chkLazyCosmetics;
    private readonly CheckBox _chkReduceVfx;

    public event Action<PerformanceTuningOptions>? ApplyTuningRequested;
    public event Action? RestorePureOfficialRequested;

    public PerformanceTunerPage()
    {
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        Padding = new Padding(22, 16, 22, 14);

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));   // 标题
        outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));   // 安全横幅 + 快捷按钮
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 可滚动 5 项调优卡片

        // 标题
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "本体性能调优（安全可逆 · 原版零损坏）",
            Font = HubTheme.FontPageTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Location = new Point(0, 0)
        });
        header.Controls.Add(new Label
        {
            Text = "任何修改前均自动在 <游戏目录>\\_BazaarHubBackups\\original\\ 建立首次原版只读备份，随时可逐字节 100% 还原。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 30)
        });
        outer.Controls.Add(header, 0, 0);

        // 安全状态与操作横幅
        var actionCard = new GoldCardPanel { Dock = DockStyle.Fill, Highlighted = true, Padding = new Padding(14, 10, 14, 10), Margin = new Padding(0, 0, 0, 10) };
        var actionLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, BackColor = Color.Transparent };
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        actionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        _lblBackupStatus = new Label
        {
            Text = "原版保护状态：等待检测游戏目录...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.StatusGreen,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        actionLayout.Controls.Add(_lblBackupStatus, 0, 0);

        var btnRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 4, 0, 0)
        };

        var btnRecommended = new GoldButton { Text = "一键应用推荐优化", Variant = GoldButtonVariant.PrimaryGold, Size = new Size(164, 34), Margin = new Padding(0, 0, 8, 0) };
        btnRecommended.Click += (_, _) =>
        {
            var preset = PerformanceTuningOptions.CreateRecommended();
            LoadOptions(preset);
            ApplyTuningRequested?.Invoke(CollectOptions());
        };

        var btnApplyCurrent = new GoldButton { Text = "应用当前勾选", Variant = GoldButtonVariant.OutlineGold, Size = new Size(126, 34), Margin = new Padding(0, 0, 8, 0) };
        btnApplyCurrent.Click += (_, _) => ApplyTuningRequested?.Invoke(CollectOptions());

        var btnExtreme = new GoldButton { Text = "载入极致低配预设", Variant = GoldButtonVariant.SubtleDark, Size = new Size(140, 34), Margin = new Padding(0, 0, 16, 0) };
        btnExtreme.Click += (_, _) => LoadOptions(PerformanceTuningOptions.CreateExtremeLowSpec());

        var btnRestore = new GoldButton { Text = "一键还原纯净官方原版", Variant = GoldButtonVariant.DangerRed, Size = new Size(184, 34), Margin = new Padding(0) };
        btnRestore.Click += (_, _) => RestorePureOfficialRequested?.Invoke();

        btnRow.Controls.AddRange(new Control[] { btnRecommended, btnApplyCurrent, btnExtreme, btnRestore });
        actionLayout.Controls.Add(btnRow, 0, 1);
        actionCard.Controls.Add(actionLayout);
        outer.Controls.Add(actionCard, 0, 1);

        // 可滚动 5 项调优区
        var scrollHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.Transparent };
        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 10, 12)
        };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── 卡片 1：boot.config ─────────────────────────────────────────
        var cardBoot = CreateTuningCard(
            "1. TheBazaar_Data\\boot.config 底层引擎管线优化",
            "收益：启用 Unity 6 原生多线程渲染录制（Native Gfx Jobs）、收紧增量 GC 单帧时间片并增大原生内存块，渲染线程耗时降低 10%–20%。");
        _chkBootConfig = new CheckBox { Text = "启用 boot.config 底层优化（自动保留原版 6 行与 build-guid）", Checked = true };
        HubTheme.StyleCheckBox(_chkBootConfig, bold: true);

        var bootSubRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(18, 6, 0, 0) };
        _chkNativeGfxJobs = new CheckBox { Text = "Native Gfx Jobs（老旧核显若花屏可取消勾选）", Checked = true, Margin = new Padding(0, 4, 18, 4) };
        _chkNativeMemAlloc = new CheckBox { Text = "增大主/临时原生内存分配块（16MB/8MB）", Checked = true, Margin = new Padding(0, 4, 18, 4) };
        HubTheme.StyleCheckBox(_chkNativeGfxJobs);
        HubTheme.StyleCheckBox(_chkNativeMemAlloc);

        _cmbGcSlice = new ComboBox { Width = 92 };
        HubTheme.StyleComboBox(_cmbGcSlice);
        _cmbGcSlice.Items.AddRange(new object[] { "2 ms (推荐)", "3 ms (原版)", "1 ms (极致)" });
        _cmbGcSlice.SelectedIndex = 0;

        _cmbJobWorkers = new ComboBox { Width = 78 };
        HubTheme.StyleComboBox(_cmbJobWorkers);
        _cmbJobWorkers.Items.AddRange(new object[] { "4 线程", "5 线程", "6 线程", "7 线程", "8 线程" });
        _cmbJobWorkers.SelectedIndex = Math.Clamp(PerformanceTuningOptions.DefaultRecommendedJobWorkerCount() - 4, 0, 4);

        bootSubRow.Controls.Add(_chkNativeGfxJobs);
        bootSubRow.Controls.Add(_chkNativeMemAlloc);
        bootSubRow.Controls.Add(CreateInlineLabel("GC 切片："));
        bootSubRow.Controls.Add(_cmbGcSlice);
        bootSubRow.Controls.Add(CreateInlineLabel("  Job 线程："));
        bootSubRow.Controls.Add(_cmbJobWorkers);
        AppendCardContent(cardBoot, _chkBootConfig, bootSubRow);
        cards.Controls.Add(cardBoot);

        // ── 卡片 2：Unity 启动参数 ───────────────────────────────────────
        var cardArgs = CreateTuningCard(
            "2. Unity 启动命令行参数组合",
            "收益：-nolog 消除 Player.log 主线程同步刷盘卡顿，-gc-incremental 确保增量 GC 激活，-high 提升系统进程调度优先级。");
        var argsFlow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        _chkArgNoLog = new CheckBox { Text = "-nolog（禁写 Player.log，降卡顿）", Checked = true, Margin = new Padding(0, 3, 18, 3) };
        _chkArgGcInc = new CheckBox { Text = "-gc-incremental（强制增量 GC）", Checked = true, Margin = new Padding(0, 3, 18, 3) };
        _chkArgHighPrio = new CheckBox { Text = "-high（高进程优先级稳帧）", Checked = true, Margin = new Padding(0, 3, 18, 3) };
        _chkArgBorderless = new CheckBox { Text = "-window-mode borderless（无边框全屏低延迟）", Checked = false, Margin = new Padding(0, 3, 18, 3) };
        _chkArgD3D11 = new CheckBox { Text = "-force-d3d11（DX12 异常卡顿显卡回退 DX11）", Checked = false, Margin = new Padding(0, 3, 0, 3) };
        foreach (CheckBox c in new[] { _chkArgNoLog, _chkArgGcInc, _chkArgHighPrio, _chkArgBorderless, _chkArgD3D11 })
        {
            HubTheme.StyleCheckBox(c);
            c.CheckedChanged += (_, _) => UpdateArgsPreview();
            argsFlow.Controls.Add(c);
        }

        _lblArgsPreview = new Label
        {
            Text = "当前启动参数预览：-high -nolog -gc-incremental",
            Font = HubTheme.FontMono,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 0)
        };
        AppendCardContent(cardArgs, argsFlow, _lblArgsPreview);
        cards.Controls.Add(cardArgs);

        // ── 卡片 3：Addressables settings.json ───────────────────────────
        var cardAa = CreateTuningCard(
            "3. StreamingAssets\\aa\\settings.json 冷启动加速",
            "收益：关闭启动期远程 Catalog 超时探测与资源异常高频刷盘，冷启动进大厅耗时缩短 1.5–4 秒（连官方服前一键还原即可）。");
        _chkAaSettings = new CheckBox { Text = "应用 Addressables 离线启动加速（m_DisableCatalogUpdateOnStart=true, m_LogResourceManagerExceptions=false）", Checked = true };
        HubTheme.StyleCheckBox(_chkAaSettings, bold: true);
        AppendCardContent(cardAa, _chkAaSettings);
        cards.Controls.Add(cardAa);

        // ── 卡片 4：BepInEx.cfg ──────────────────────────────────────────
        var cardBep = CreateTuningCard(
            "4. BepInEx\\config\\BepInEx.cfg 日志链路瘦身",
            "收益：关闭高开销控制台窗口、停止将每条 Unity Debug.Log 反射转发给 BepInEx，磁盘日志仅保留 Fatal/Error，大幅压降主线程 I/O。");
        _chkBepLogging = new CheckBox { Text = "启用 BepInEx 生产游玩模式日志瘦身（排错时可随时一键还原完整日志）", Checked = true };
        HubTheme.StyleCheckBox(_chkBepLogging, bold: true);
        AppendCardContent(cardBep, _chkBepLogging);
        cards.Controls.Add(cardBep);

        // ── 卡片 5：BazaarLauncher 性能项 ────────────────────────────────
        var cardLauncher = CreateTuningCard(
            "5. BazaarLauncher 游戏内运行时性能配置（[Performance]）",
            "收益：前台稳帧省电、切后台自动降频释放 GPU、跳过 396 套未穿戴 3D 藏品全量预加载，以及高倍速战斗特效降载。");
        _chkLauncherPerf = new CheckBox { Text = "同步写入 local.thebazaar.bazaarlauncher.cfg 的 [Performance] 优化配置", Checked = true };
        HubTheme.StyleCheckBox(_chkLauncherPerf, bold: true);

        var lpRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(18, 6, 0, 0) };
        _cmbForegroundFps = new ComboBox { Width = 122 };
        HubTheme.StyleComboBox(_cmbForegroundFps);
        _cmbForegroundFps.Items.AddRange(new object[] { "60 FPS (省电稳帧)", "90 FPS (高刷流畅)", "120 FPS (极速高刷)", "0 (不限帧/随原版)" });
        _cmbForegroundFps.SelectedIndex = 0;

        _cmbBackgroundFps = new ComboBox { Width = 112 };
        HubTheme.StyleComboBox(_cmbBackgroundFps);
        _cmbBackgroundFps.Items.AddRange(new object[] { "20 FPS (推荐)", "15 FPS (极省电)", "30 FPS", "0 (后台不降频)" });
        _cmbBackgroundFps.SelectedIndex = 0;

        _chkLazyCosmetics = new CheckBox { Text = "藏品按需懒加载", Checked = true, Margin = new Padding(14, 4, 14, 4) };
        _chkReduceVfx = new CheckBox { Text = "高并发战斗特效降载（连发/高倍速防掉帧）", Checked = false, Margin = new Padding(0, 4, 0, 4) };
        HubTheme.StyleCheckBox(_chkLazyCosmetics);
        HubTheme.StyleCheckBox(_chkReduceVfx);

        lpRow.Controls.Add(CreateInlineLabel("前台上限："));
        lpRow.Controls.Add(_cmbForegroundFps);
        lpRow.Controls.Add(CreateInlineLabel("   后台降频："));
        lpRow.Controls.Add(_cmbBackgroundFps);
        lpRow.Controls.Add(_chkLazyCosmetics);
        lpRow.Controls.Add(_chkReduceVfx);

        AppendCardContent(cardLauncher, _chkLauncherPerf, lpRow);
        cards.Controls.Add(cardLauncher);

        scrollHost.Controls.Add(cards);
        outer.Controls.Add(scrollHost, 0, 2);
        Controls.Add(outer);

        UpdateArgsPreview();
    }

    public void LoadOptions(PerformanceTuningOptions options)
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

        UpdateArgsPreview();
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
            _lblBackupStatus.Text = "原版保护状态：请先在「快速启动与选服」定位有效的游戏目录。";
            _lblBackupStatus.ForeColor = HubTheme.StatusYellow;
            return;
        }

        int count = manifest?.Files.Count ?? 0;
        if (count == 0)
        {
            _lblBackupStatus.Text = "原版保护状态：尚未改动任何游戏原版文件（首次应用优化时将自动安全备份至 _BazaarHubBackups\\original\\）。";
            _lblBackupStatus.ForeColor = HubTheme.StatusBlue;
        }
        else
        {
            _lblBackupStatus.Text = $"原版保护状态：已永久保护 {count} 份首次原版基线文件（_BazaarHubBackups\\original\\），随时可一键逐字节还原！";
            _lblBackupStatus.ForeColor = HubTheme.StatusGreen;
        }
    }

    private void UpdateArgsPreview()
    {
        string preview = PerformanceTunerService.BuildFullLaunchArgsPreview(CollectOptions());
        _lblArgsPreview.Text = string.IsNullOrWhiteSpace(preview)
            ? "当前启动参数预览：（无附加参数，完全沿用默认启动方式）"
            : $"当前启动参数预览：{preview}";
    }

    private static GoldCardPanel CreateTuningCard(string title, string benefit)
    {
        var card = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(14, 12, 14, 12)
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Name = "InnerStack"
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        stack.Controls.Add(new Label
        {
            Text = title,
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4)
        });
        stack.Controls.Add(new Label
        {
            Text = benefit,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(820, 0),
            Margin = new Padding(0, 0, 0, 8)
        });

        card.Controls.Add(stack);
        return card;
    }

    private static void AppendCardContent(GoldCardPanel card, params Control[] items)
    {
        if (card.Controls["InnerStack"] is TableLayoutPanel stack)
        {
            foreach (Control item in items)
            {
                stack.Controls.Add(item);
            }
        }
    }

    private static Label CreateInlineLabel(string text) => new()
    {
        Text = text,
        Font = HubTheme.FontBody,
        ForeColor = HubTheme.TextSecondary,
        AutoSize = true,
        Padding = new Padding(0, 5, 2, 0)
    };
}

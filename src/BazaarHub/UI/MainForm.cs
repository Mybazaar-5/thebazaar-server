using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 大巴扎自建服启动器主窗体。
/// 采用「暗黑黑曜石 × 暖鎏金」现代桌面架构：DWM 暗黑原生标题栏、精致左侧矢量导航栏、
/// 顶部全局状态条（含醒目 Steam PTR 测试服提示胶囊、实时节点状态与目录状态）以及无闪烁 4 页平滑内容舞台。
/// </summary>
public sealed class MainForm : Form
{
    private readonly HubPresetConfig _preset = HubPresetRegistry.Current;
    private readonly GameLocatorService _locator = new();
    private readonly LauncherConfigService _configService = new();
    private readonly ServerProbeService _probeService = new();
    private readonly UpdateSyncService _syncService = new();
    private readonly PerformanceTunerService _perfService = new();
    private readonly StandaloneServerHost _standaloneHost = new();
    private readonly DiagnosticsExportService _diagService = new();
    private readonly CommunityModClient _communityClient = new();
    private readonly ToolTip _toolTip = HubTheme.CreateDarkToolTip();

    private readonly NavSidebarButton _navLaunch;
    private readonly NavSidebarButton _navSync;
    private readonly NavSidebarButton _navPerf;
    private readonly NavSidebarButton _navDiag;
    private readonly NavSidebarButton _navWorkshop;

    private readonly Label _lblSidebarMode;
    private readonly Label _lblSidebarGame;

    private readonly StatusPill _topPtrPill;
    private readonly StatusPill _topServerPill;
    private readonly StatusPill _topGamePill;
    private readonly StatusPill _topVersionPill;

    private readonly Panel _contentHost;
    private readonly QuickLaunchPage _launchPage;
    private readonly UpdateSyncPage _syncPage;
    private readonly PerformanceTunerPage _perfPage;
    private readonly DiagnosticsPage _diagPage;

    private readonly Dictionary<ServerMode, ServerProbeResult> _probeResults = new();
    private readonly CancellationTokenSource _formCts = new();
    private HubPreferences _preferences = new();
    private GameInstallInfo? _gameInstall;
    private bool _isLaunching;
    private bool _isSyncing;
    private bool _isTogglingStandalone;
    private bool _communityModsLoadedOnce;
    private int _currentPageIndex = -1;

    private bool IsClosingOrDisposed => _formCts.IsCancellationRequested || IsDisposed || Disposing;

    public MainForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = $"大巴扎自建服启动器 · BazaarHub v{LauncherConstants.HubVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1060, 680);
        Size = new Size(1240, 780);
        BackColor = HubTheme.BgWindow;
        ForeColor = HubTheme.TextPrimary;
        Font = HubTheme.FontBody;
        DoubleBuffered = true;

        HandleCreated += (_, _) => HubTheme.ApplyDarkWindowChrome(this);

        var mainSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = HubTheme.BgWindow,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(mainSplit);
        mainSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236));
        mainSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── 1. 左侧黑曜石现代导航栏 ──────────────────────────────────────
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = HubTheme.BgSidebar,
            Padding = new Padding(0, 0, 1, 0)
        };
        HubTheme.EnableDoubleBuffer(sidebar);
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderHairline, 1f);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        var brandPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 86,
            BackColor = HubTheme.BgSidebar
        };
        brandPanel.Paint += PaintBrandHeader;

        var navContainer = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = HubTheme.BgSidebar,
            Padding = new Padding(0, 12, 0, 10),
            Margin = new Padding(0)
        };
        navContainer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        navContainer.Controls.Add(CreateNavGroupLabel("核心控制台"));

        _navLaunch = new NavSidebarButton { Text = "快速启动与选服", Icon = HubIconKind.Launch, Dock = DockStyle.Top, Height = 44, Margin = new Padding(0, 0, 0, 4) };
        _navSync = new NavSidebarButton { Text = "更新与 Mod 同步", Icon = HubIconKind.Sync, Dock = DockStyle.Top, Height = 44, Margin = new Padding(0, 0, 0, 4) };
        _navPerf = new NavSidebarButton { Text = "本体性能调优", Icon = HubIconKind.Tuning, Dock = DockStyle.Top, Height = 44, Margin = new Padding(0, 0, 0, 4) };
        _navDiag = new NavSidebarButton { Text = "自检与诊断日志", Icon = HubIconKind.Diagnostics, Dock = DockStyle.Top, Height = 44, Margin = new Padding(0, 0, 0, 14) };

        _navLaunch.Click += (_, _) => SelectPage(0);
        _navSync.Click += (_, _) => SelectPage(1);
        _navPerf.Click += (_, _) => SelectPage(2);
        _navDiag.Click += (_, _) => SelectPage(3);

        navContainer.Controls.Add(_navLaunch);
        navContainer.Controls.Add(_navSync);
        navContainer.Controls.Add(_navPerf);
        navContainer.Controls.Add(_navDiag);

        navContainer.Controls.Add(CreateNavGroupLabel("扩展生态"));

        _navWorkshop = new NavSidebarButton
        {
            Text = "创意工坊编辑器",
            Icon = HubIconKind.Workshop,
            BadgeText = "Web",
            Dock = DockStyle.Top,
            Height = 44,
            Margin = new Padding(0, 0, 0, 4)
        };
        _navWorkshop.Click += async (_, _) => await OpenWorkshopInBrowserAsync();
        navContainer.Controls.Add(_navWorkshop);

        // 侧边栏底部状态卡
        var footerWrap = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 114,
            BackColor = HubTheme.BgSidebar,
            Padding = new Padding(12, 8, 12, 12)
        };
        var footerCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 10,
            CardBackColor = ColorTranslator.FromHtml("#10141D"),
            CardTopColor = ColorTranslator.FromHtml("#141925"),
            Padding = new Padding(12, 10, 12, 10)
        };

        _lblSidebarMode = new Label
        {
            Text = $"模式：{FormatModeName(_preset.DefaultMode)}",
            Font = HubTheme.FontSmallBold,
            ForeColor = HubTheme.GoldLight,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Height = 20,
            AutoEllipsis = true
        };
        _lblSidebarGame = new Label
        {
            Text = "游戏：等待检测...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            BackColor = Color.Transparent,
            Dock = DockStyle.Top,
            Height = 20,
            AutoEllipsis = true
        };
        var lblVersionFooter = new Label
        {
            Text = $"BazaarHub v{LauncherConstants.HubVersion} · 原生零依赖",
            Font = HubTheme.FontCaption,
            ForeColor = HubTheme.TextMuted,
            BackColor = Color.Transparent,
            Dock = DockStyle.Bottom,
            Height = 16
        };
        footerCard.Controls.Add(lblVersionFooter);
        footerCard.Controls.Add(_lblSidebarGame);
        footerCard.Controls.Add(_lblSidebarMode);
        footerWrap.Controls.Add(footerCard);

        sidebar.Controls.Add(footerWrap);
        sidebar.Controls.Add(navContainer);
        sidebar.Controls.Add(brandPanel);

        // ── 2. 右侧主区域（顶部全局状态条 + 无闪烁内容舞台）───────────────
        var rightArea = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = HubTheme.BgWindow,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(rightArea);
        rightArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rightArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        rightArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 顶部全局状态条
        var topBar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = HubTheme.Surface0,
            Padding = new Padding(22, 0, 22, 0),
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(topBar);
        topBar.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderHairline, 1f);
            e.Graphics.DrawLine(pen, 0, topBar.Height - 1, topBar.Width, topBar.Height - 1);
        };

        var topBarGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        topBarGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topBarGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        // 左侧醒目 PTR 测试服分支提示胶囊（红线必保）
        var topLeftFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 12, 0, 0),
            Margin = new Padding(0)
        };

        _topPtrPill = new StatusPill
        {
            Height = 27,
            Interactive = true,
            Margin = new Padding(0)
        };
        _topPtrPill.SetState(
            PillTone.Amber,
            "需使用 Steam 官方测试服（PTR 分支）客户端 · 切换说明",
            showDot: false,
            icon: HubIconKind.Warning);
        _topPtrPill.Click += (_, _) => ShowPtrNoticeDialog();
        _toolTip.SetToolTip(
            _topPtrPill,
            "本项目必须配合 Steam 官方测试服（PTR 分支）客户端使用，暂不支持官方正式服最新版。\n在 Steam 库右键《The Bazaar》→ 属性 → 测试版（Betas）中切换至官方测试服（PTR）更新即可。");
        topLeftFlow.Controls.Add(_topPtrPill);

        // 右侧全局状态胶囊组（节点连通灯 + 游戏路径状态 + 版本号）
        var topRightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(8, 12, 0, 0),
            Margin = new Padding(0)
        };

        _topServerPill = new StatusPill { Height = 27, Interactive = true, Margin = new Padding(0, 0, 8, 0) };
        _topServerPill.SetState(PillTone.Neutral, "节点探测中", showDot: true);
        _topServerPill.Click += async (_, _) => await RefreshAllServerProbesAsync();
        _toolTip.SetToolTip(_topServerPill, "点击立即重新探测所有服务器节点延迟与在线状态");

        _topGamePill = new StatusPill { Height = 27, Interactive = true, Margin = new Padding(0, 0, 8, 0) };
        _topGamePill.SetState(PillTone.Neutral, "检测目录中", showDot: true);
        _topGamePill.Click += (_, _) => SelectPage(0);

        _topVersionPill = new StatusPill { Height = 27, Margin = new Padding(0) };
        _topVersionPill.SetState(PillTone.Gold, $"v{LauncherConstants.HubVersion}", showDot: false);

        topRightFlow.Controls.Add(_topServerPill);
        topRightFlow.Controls.Add(_topGamePill);
        topRightFlow.Controls.Add(_topVersionPill);

        topBarGrid.Controls.Add(topLeftFlow, 0, 0);
        topBarGrid.Controls.Add(topRightFlow, 1, 0);
        topBar.Controls.Add(topBarGrid);

        // 内容舞台（预挂载全部 4 页，通过 Visible 切换实现零闪烁）
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = HubTheme.BgWindow, Margin = new Padding(0) };
        HubTheme.EnableDoubleBuffer(_contentHost);

        _launchPage = new QuickLaunchPage { Dock = DockStyle.Fill, Visible = false };
        _syncPage = new UpdateSyncPage { Dock = DockStyle.Fill, Visible = false };
        _perfPage = new PerformanceTunerPage { Dock = DockStyle.Fill, Visible = false };
        _diagPage = new DiagnosticsPage { Dock = DockStyle.Fill, Visible = false };

        _contentHost.Controls.AddRange(new Control[] { _launchPage, _syncPage, _perfPage, _diagPage });

        rightArea.Controls.Add(topBar, 0, 0);
        rightArea.Controls.Add(_contentHost, 0, 1);

        WireEvents();

        mainSplit.Controls.Add(sidebar, 0, 0);
        mainSplit.Controls.Add(rightArea, 1, 0);
        Controls.Add(mainSplit);

        SelectPage(0);

        Shown += async (_, _) => await InitializeOnStartupAsync();
        FormClosing += OnMainFormClosing;
    }

    /// <summary>
    /// 切换到指定功能页（0=快速启动, 1=更新同步, 2=性能调优, 3=自检诊断）。
    /// </summary>
    public void SelectPage(int index)
    {
        int clamped = Math.Clamp(index, 0, 3);
        if (_currentPageIndex == clamped) return;
        _currentPageIndex = clamped;

        _navLaunch.Selected = clamped == 0;
        _navSync.Selected = clamped == 1;
        _navPerf.Selected = clamped == 2;
        _navDiag.Selected = clamped == 3;

        _contentHost.SuspendLayout();
        _launchPage.Visible = clamped == 0;
        _syncPage.Visible = clamped == 1;
        _perfPage.Visible = clamped == 2;
        _diagPage.Visible = clamped == 3;

        Control active = clamped switch
        {
            1 => _syncPage,
            2 => _perfPage,
            3 => _diagPage,
            _ => _launchPage
        };
        active.BringToFront();
        _contentHost.ResumeLayout(true);

        if (clamped == 1 && !_communityModsLoadedOnce && IsHandleCreated && Visible)
        {
            _communityModsLoadedOnce = true;
            _ = RefreshHubCommunityModsAsync();
        }
    }

    private void WireEvents()
    {
        _standaloneHost.LifecycleMessage += msg =>
        {
            if (IsClosingOrDisposed) return;
            AppendLog($"[托管] {msg}");
            if (IsHandleCreated && !IsClosingOrDisposed)
            {
                try { BeginInvoke(RefreshStandaloneStatusUi); } catch (ObjectDisposedException) { }
            }
        };

        // 页面 1：快速启动与选服
        _launchPage.AutoLocateRequested += () => LocateGame(forceFullScan: true);
        _launchPage.BrowseGameDirRequested += BrowseGameDirectory;
        _launchPage.OpenGameDirRequested += () => OpenDirectorySafely(_gameInstall?.GameDirectory);
        _launchPage.ServerModeChanged += mode =>
        {
            _preferences.SelectedMode = mode;
            UpdateGlobalStatusSummary();
            SavePreferencesQuietly();
        };
        _launchPage.ProbeRefreshRequested += async () => await RefreshAllServerProbesAsync();
        _launchPage.SavePlayerAndConfigRequested += () => SaveClientConfigFromUi(showFeedback: true);
        _launchPage.LaunchRequested += async () => await LaunchWorkflowAsync();
        _launchPage.OpenWorkshopRequested += async () => await OpenWorkshopInBrowserAsync();
        _launchPage.ToggleLocalServerRequested += async () => await ToggleStandaloneServerAsync();

        // 页面 2：更新与 Mod 同步
        _syncPage.SyncRequested += async applyDownloads => await RunManualSyncAsync(applyDownloads);
        _syncPage.OpenWorkshopRequested += async () => await OpenWorkshopInBrowserAsync();
        _syncPage.RefreshCommunityModsRequested += async () => await RefreshHubCommunityModsAsync();
        _syncPage.DownloadCommunityModRequested += async modId => await DownloadHubCommunityModAsync(modId);
        _syncPage.UploadLocalModRequested += async () => await UploadHubLocalModAsync();
        _syncPage.OpenModCacheRequested += () =>
        {
            if (_gameInstall?.IsValidGameInstall == true)
            {
                string cacheDir = Path.Combine(_gameInstall.GameDirectory, LauncherConstants.ModCacheRelativeDir);
                Directory.CreateDirectory(cacheDir);
                OpenDirectorySafely(cacheDir);
            }
            else
            {
                MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };

        // 页面 3：本体性能调优
        _perfPage.ApplyTuningRequested += async options => await ApplyPerformanceTuningAsync(options);
        _perfPage.RestorePureOfficialRequested += async () => await RestorePureOfficialAsync();

        // 页面 4：自检与诊断
        _diagPage.OpenGameDirRequested += () => OpenDirectorySafely(_gameInstall?.GameDirectory);
        _diagPage.OpenBepInExLogDirRequested += () =>
        {
            if (_gameInstall?.IsValidGameInstall == true)
            {
                OpenDirectorySafely(Path.Combine(_gameInstall.GameDirectory, "BepInEx"));
            }
        };
        _diagPage.OpenUnityLogDirRequested += () =>
        {
            string? dir = DiagnosticsExportService.LocateUnityPlayerLogDirectory();
            if (dir != null)
            {
                OpenDirectorySafely(dir);
            }
            else
            {
                AppendLog("[警告] 未在 AppData\\LocalLow 检测到 Unity Player.log 目录（开启 -nolog 或尚未运行过游戏时无此目录）。");
                MessageBox.Show(this, "未找到 Unity Player.log 目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        _diagPage.NetworkTestRequested += async () => await RefreshAllServerProbesAsync();
        _diagPage.ExportDiagnosticsRequested += async () => await ExportDiagnosticsZipAsync();
    }

    private void ShowPtrNoticeDialog()
    {
        MessageBox.Show(
            this,
            "客户端版本要求（必看）：\n\n" +
            "本自建服与启动器必须配合 Steam 官方测试服（PTR 分支）客户端使用，暂不支持官方正式服最新版。\n\n" +
            "切换步骤：\n" +
            "1. 打开 Steam 游戏库，右键《The Bazaar》；\n" +
            "2. 点击「属性」→ 左侧选择「测试版（Betas）」；\n" +
            "3. 在参与测试版下拉菜单中切换至官方测试服（PTR）并等待更新完成；\n" +
            "4. 回到本启动器确认游戏目录与节点后，一键启动即可。",
            "Steam 官方测试服（PTR 分支）说明",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private async Task InitializeOnStartupAsync()
    {
        AppendLog($"[信息] 大巴扎自建服启动器 BazaarHub v{LauncherConstants.HubVersion} 已就绪（需使用 Steam 官方测试服 PTR 分支客户端）。");
        _preferences = _configService.LoadHubPreferences();
        _perfPage.LoadOptions(_preferences.PerformanceOptions);

        LocateGame(forceFullScan: false);
        await RefreshAllServerProbesAsync();
    }

    private void LocateGame(bool forceFullScan)
    {
        string? preferred = forceFullScan ? null : _preferences.LastGameDirectory;
        GameInstallInfo? located = _locator.AutoLocate(preferred);
        SetGameInstall(located);
    }

    private void BrowseGameDirectory()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "请选择包含 TheBazaar.exe 的《The Bazaar》游戏安装目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (_gameInstall != null && Directory.Exists(_gameInstall.GameDirectory))
        {
            dlg.InitialDirectory = _gameInstall.GameDirectory;
        }

        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            GameInstallInfo info = _locator.InspectGameDirectory(dlg.SelectedPath, "手动选择");
            SetGameInstall(info);
            if (!info.IsValidGameInstall)
            {
                MessageBox.Show(
                    this,
                    "所选目录中未检测到 TheBazaar.exe 或 TheBazaar_Data\\Managed，请确认是否选择了正确的游戏根目录。",
                    "目录校验未通过",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }
    }

    private void SetGameInstall(GameInstallInfo? info)
    {
        _gameInstall = info;
        _launchPage.SetGameInstallInfo(info);

        if (info != null && info.IsValidGameInstall)
        {
            _preferences.LastGameDirectory = info.GameDirectory;
            SavePreferencesQuietly();

            LauncherConfigSnapshot snap = _configService.ReadSnapshot(info.GameDirectory);
            _launchPage.ApplySnapshotToInputs(snap, _preferences);
            BackupManifest manifest = _perfService.LoadBackupManifest(info.GameDirectory);
            _perfPage.UpdateBackupBanner(info.GameDirectory, manifest);

            AppendLog($"[成功] 已定位游戏目录：{info.GameDirectory}（{info.SourceHint}）");
        }
        else
        {
            _perfPage.UpdateBackupBanner(null, null);
            AppendLog("[警告] 未自动探测到《The Bazaar》安装目录，请手动选择目录。");
        }

        UpdateGlobalStatusSummary();
    }

    private async Task RefreshAllServerProbesAsync()
    {
        if (IsClosingOrDisposed) return;
        CancellationToken ct = _formCts.Token;
        _launchPage.SetProbingState(true);
        try
        {
            string customUrl = _launchPage.CustomServerUrl;
            Task<ServerProbeResult>? tPreset = _preset.HasPresetRemote
                ? _probeService.ProbeAsync(_preset.PresetRemoteUrl, ct)
                : null;
            Task<ServerProbeResult> tLocal = _probeService.ProbeAsync(LauncherConstants.LocalStandaloneUrl, ct);
            Task<ServerProbeResult> tCustom = string.IsNullOrWhiteSpace(customUrl)
                ? Task.FromResult(new ServerProbeResult { StatusMessage = "未填写自定义地址" })
                : _probeService.ProbeAsync(customUrl, ct);

            if (tPreset != null)
            {
                await Task.WhenAll(tPreset, tLocal, tCustom);
                if (IsClosingOrDisposed) return;
                _probeResults[ServerMode.PresetRemote] = tPreset.Result;
                _launchPage.UpdateServerProbeResult(ServerMode.PresetRemote, tPreset.Result);
            }
            else
            {
                await Task.WhenAll(tLocal, tCustom);
                if (IsClosingOrDisposed) return;
            }

            _probeResults[ServerMode.LocalStandalone] = tLocal.Result;
            _probeResults[ServerMode.CustomServer] = tCustom.Result;

            _launchPage.UpdateServerProbeResult(ServerMode.LocalStandalone, tLocal.Result);
            _launchPage.UpdateServerProbeResult(ServerMode.CustomServer, tCustom.Result);

            UpdateGlobalStatusSummary();
            if (tPreset != null)
            {
                AppendLog($"[测速] {_preset.PresetRemoteName}：{tPreset.Result.StatusMessage} ｜ 本地单机：{tLocal.Result.StatusMessage}");
            }
            else
            {
                AppendLog($"[测速] 本地单机：{tLocal.Result.StatusMessage} ｜ 自定义服：{tCustom.Result.StatusMessage}");
            }
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[警告] 服务器测速异常：{ex.Message}");
            }
        }
        finally
        {
            if (!IsClosingOrDisposed)
            {
                _launchPage.SetProbingState(false);
            }
        }
    }

    private async Task ToggleStandaloneServerAsync()
    {
        if (_isTogglingStandalone || IsClosingOrDisposed) return;
        _isTogglingStandalone = true;
        CancellationToken ct = _formCts.Token;
        try
        {
            if (_standaloneHost.IsManagedServerRunning)
            {
                _standaloneHost.StopManagedServer(AppendLog);
                _launchPage.SetLaunchBusy(false, "已停止本地托管单机服务端，存档已安全保存。", 0);
            }
            else
            {
                _launchPage.SetLaunchBusy(false, "正在拉起本地单机服务端并等待 /healthz 就绪...", 40);
                (bool ready, string msg) = await _standaloneHost.EnsureStartedAsync(8080, AppendLog, ct, _gameInstall?.GameDirectory);
                if (IsClosingOrDisposed) return;
                _launchPage.SetLaunchBusy(false, msg, ready ? 100 : 0);
                if (!ready)
                {
                    MessageBox.Show(this, msg, "本地单机服务端启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            RefreshStandaloneStatusUi();
            await RefreshAllServerProbesAsync();
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 切换本地单机服失败：{ex.Message}");
                MessageBox.Show(this, $"操作失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _isTogglingStandalone = false;
        }
    }

    private void RefreshStandaloneStatusUi()
    {
        if (IsClosingOrDisposed) return;
        _launchPage.SetStandaloneServerRunning(_standaloneHost.IsManagedServerRunning);
        UpdateGlobalStatusSummary();
    }

    private bool SaveClientConfigFromUi(bool showFeedback)
    {
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            if (showFeedback)
            {
                MessageBox.Show(this, "请先定位有效的《The Bazaar》游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return false;
        }

        try
        {
            ServerMode mode = _launchPage.SelectedMode;
            string customUrl = _launchPage.CustomServerUrl;
            string playerName = _launchPage.PlayerNameInput;

            LauncherConfigSnapshot snap = _configService.ApplyServerAndPlayerSettings(
                _gameInstall.GameDirectory,
                mode,
                customUrl,
                playerName,
                bypassSteam: _launchPage.BypassSteam,
                unlockEverything: _launchPage.UnlockEverything,
                acceptServerMods: _launchPage.AcceptServerMods);

            _preferences.SelectedMode = mode;
            if (!string.IsNullOrWhiteSpace(customUrl))
            {
                string normalizedCustom = LauncherConfigService.NormalizeServerUrl(customUrl);
                _preferences.CustomServerUrl = normalizedCustom;
                _preferences.SavedCustomServers ??= new List<string>();
                if (!_preferences.SavedCustomServers.Any(s => string.Equals(s, normalizedCustom, StringComparison.OrdinalIgnoreCase)))
                {
                    _preferences.SavedCustomServers.Insert(0, normalizedCustom);
                    if (_preferences.SavedCustomServers.Count > 12)
                    {
                        _preferences.SavedCustomServers.RemoveRange(12, _preferences.SavedCustomServers.Count - 12);
                    }
                }
                _launchPage.UpdateSavedServerDropdown(_preferences.SavedCustomServers, normalizedCustom);
            }
            _preferences.AutoSyncBeforeLaunch = _launchPage.AutoSyncBeforeLaunch;
            _preferences.PerformanceOptions = _perfPage.CollectOptions();
            SavePreferencesQuietly();

            UpdateGlobalStatusSummary();
            AppendLog($"[成功] 配置已保存：模式={FormatModeName(mode)}，地址={snap.ServerBase}，昵称={snap.EffectivePlayerName}，接收服务器 Mod={(snap.AcceptServerMods ? "开" : "关")}");

            if (showFeedback)
            {
                _launchPage.SetLaunchBusy(false, $"配置已保存（昵称：{snap.EffectivePlayerName}，模式：{FormatModeName(mode)}）。", 100);
            }

            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 保存配置失败：{ex.Message}");
            if (showFeedback)
            {
                MessageBox.Show(this, $"保存配置失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return false;
        }
    }

    private async Task LaunchWorkflowAsync()
    {
        if (_isLaunching) return;
        if (!SaveClientConfigFromUi(showFeedback: false))
        {
            MessageBox.Show(this, "请先在下方定位有效的《The Bazaar》游戏安装目录。", "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isLaunching = true;
        try
        {
            ServerMode mode = _launchPage.SelectedMode;
            string gameDir = _gameInstall!.GameDirectory;
            string serverBase = LauncherConfigService.ResolveServerBase(mode, _launchPage.CustomServerUrl);
            PerformanceTuningOptions perfOptions = _perfPage.CollectOptions();

            if (mode == ServerMode.LocalStandalone)
            {
                _launchPage.SetLaunchBusy(true, "正在拉起本地单机服务端并等待 /healthz 就绪...", 20);
                (bool ready, string msg) = await _standaloneHost.EnsureStartedAsync(8080, AppendLog, _formCts.Token, gameDir);
                if (IsClosingOrDisposed) return;
                RefreshStandaloneStatusUi();
                if (!ready)
                {
                    _launchPage.SetLaunchBusy(false, msg, 0);
                    MessageBox.Show(this, msg, "单机服务端启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            if (mode != ServerMode.PureOfficial && _launchPage.AutoSyncBeforeLaunch && !string.IsNullOrWhiteSpace(serverBase))
            {
                while (true)
                {
                    _launchPage.SetLaunchBusy(true, "正在连接服务端执行 SHA-256 增量更新检查...", 35);
                    var progress = new Progress<SyncProgress>(p =>
                    {
                        if (IsClosingOrDisposed) return;
                        int mapped = 35 + (int)(p.Percent * 0.5);
                        _launchPage.SetLaunchBusy(true, p.Message, Math.Clamp(mapped, 35, 88));
                    });

                    UpdateSyncReport syncReport = await _syncService.SyncAsync(
                        gameDir,
                        serverBase,
                        applyDownloads: true,
                        progress: progress,
                        log: AppendLog,
                        cancellationToken: _formCts.Token);
                    if (IsClosingOrDisposed) return;
                    _syncPage.RenderReport(syncReport);

                    LaunchGateAction gate = LaunchSyncGate.Evaluate(syncReport);
                    if (gate == LaunchGateAction.AllowLaunch)
                    {
                        break;
                    }

                    if (gate == LaunchGateAction.BlockRunningGame)
                    {
                        _launchPage.SetLaunchBusy(false, syncReport.SummaryMessage, 0);
                        MessageBox.Show(
                            this,
                            "检测到《The Bazaar》客户端（TheBazaar.exe）正在运行，无法安全更新核心插件。\n\n请先完全关闭游戏窗口后再重新点击启动。",
                            "核心更新已拦截（游戏运行中）",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                        return;
                    }

                    if (gate == LaunchGateAction.BlockCoreFailure)
                    {
                        _launchPage.SetLaunchBusy(false, "核心插件更新失败，已暂停启动等待确认。", 0);
                        string detail = syncReport.Errors.Count > 0
                            ? string.Join("\n• ", syncReport.Errors.Take(4))
                            : syncReport.SummaryMessage;
                        string rollbackNote = syncReport.RolledBackAtomicGroups.Count > 0
                            ? $"\n\n已自动原子回滚组件组：{string.Join(", ", syncReport.RolledBackAtomicGroups)}（本地版本保持完整一致）。"
                            : string.Empty;

                        var btnCancel = new TaskDialogButton("取消启动（推荐）");
                        var btnRetry = new TaskDialogButton("重新尝试同步");
                        var btnIgnore = new TaskDialogButton("忽略风险，使用当前本地版本启动");

                        var dlgPage = new TaskDialogPage
                        {
                            Caption = "核心插件更新失败 — 已阻断启动",
                            Heading = "核心插件同步未成功，继续启动可能导致客户端与服务端不兼容",
                            Text = $"失败原因：\n• {detail}{rollbackNote}",
                            Icon = TaskDialogIcon.Warning,
                            DefaultButton = btnCancel,
                            AllowCancel = true,
                            Buttons = { btnCancel, btnRetry, btnIgnore }
                        };

                        TaskDialogButton clicked = TaskDialog.ShowDialog(this, dlgPage);
                        if (clicked == btnRetry)
                        {
                            AppendLog("[同步] 用户选择重新尝试同步核心插件...");
                            continue;
                        }

                        if (clicked == btnIgnore)
                        {
                            AppendLog("[警告] 用户选择忽略核心插件同步失败风险，使用当前本地版本继续启动游戏。");
                            break;
                        }

                        AppendLog("[同步] 用户已取消本次启动。");
                        return;
                    }

                    if (gate == LaunchGateAction.WarnNonCoreFailure)
                    {
                        string detail = syncReport.Errors.Count > 0
                            ? string.Join("\n• ", syncReport.Errors.Take(4))
                            : syncReport.SummaryMessage;
                        DialogResult choice = MessageBox.Show(
                            this,
                            $"部分非核心资源（Mod 贴图或汉化词典）同步失败，核心插件未受影响：\n• {detail}\n\n是否仍继续启动游戏？",
                            "非核心资源同步警告",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2);
                        if (choice != DialogResult.Yes)
                        {
                            _launchPage.SetLaunchBusy(false, "已取消启动，可在「更新与 Mod 同步」页排查失败资源。", 0);
                            return;
                        }
                        break;
                    }
                }
            }

            _launchPage.SetLaunchBusy(true, "正在启动《The Bazaar》客户端...", 95);
            Process gameProc = _standaloneHost.LaunchGameAndWatch(
                gameDir,
                perfOptions,
                stopServerOnGameExit: mode == ServerMode.LocalStandalone,
                log: AppendLog);

            string launchOk = mode == ServerMode.LocalStandalone
                ? $"游戏已启动（PID={gameProc.Id}），本地单机服托管运行中，退出游戏后将自动停服。"
                : $"游戏已启动（PID={gameProc.Id}），祝你在大巴扎旗开得胜！";
            _launchPage.SetLaunchBusy(false, launchOk, 100);
            AppendLog($"[成功] {launchOk}");
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 启动游戏失败：{ex.Message}");
                _launchPage.SetLaunchBusy(false, $"启动失败：{ex.Message}", 0);
                MessageBox.Show(this, $"启动失败：{ex.Message}", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _isLaunching = false;
            RefreshStandaloneStatusUi();
        }
    }

    private async Task RunManualSyncAsync(bool applyDownloads)
    {
        if (_isSyncing || IsClosingOrDisposed) return;
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string serverBase = LauncherConfigService.ResolveServerBase(_launchPage.SelectedMode, _launchPage.CustomServerUrl);
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            string modeHint = _preset.HasPresetRemote
                ? $"请先选择{_preset.PresetRemoteName}、本地单机或自定义服务器再同步。"
                : "请先选择本地单机离线版或自定义服务器再同步。";
            MessageBox.Show(this, $"当前处于「纯净官方原版」模式，{modeHint}", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _isSyncing = true;
        _syncPage.SetBusy(true, "正在连接服务端获取最新清单...", 10);
        try
        {
            var progress = new Progress<SyncProgress>(p =>
            {
                if (!IsClosingOrDisposed) _syncPage.SetBusy(true, p.Message, p.Percent);
            });
            UpdateSyncReport report = await _syncService.SyncAsync(
                _gameInstall.GameDirectory,
                serverBase,
                applyDownloads: applyDownloads,
                progress: progress,
                log: AppendLog,
                cancellationToken: _formCts.Token);
            if (IsClosingOrDisposed) return;
            _syncPage.RenderReport(report);
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 同步异常：{ex.Message}");
                _syncPage.SetBusy(false, $"同步失败：{ex.Message}", 0);
            }
        }
        finally
        {
            _isSyncing = false;
            if (!IsClosingOrDisposed)
            {
                _syncPage.SetBusy(false, "同步流程已结束，可查看上方明细。", 100);
            }
        }
    }

    private string? ResolveServerBaseForCommunity(bool showWarningIfNone)
    {
        string serverBase = LauncherConfigService.ResolveServerBase(_launchPage.SelectedMode, _launchPage.CustomServerUrl);
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            if (showWarningIfNone)
            {
                MessageBox.Show(this, "当前处于「纯净官方原版」模式，请先切到线上服、本地服或自定义服务器。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return null;
        }
        return serverBase;
    }

    private async Task RefreshHubCommunityModsAsync()
    {
        if (IsClosingOrDisposed) return;
        string? serverBase = ResolveServerBaseForCommunity(showWarningIfNone: false);
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            _syncPage.RenderCommunityMods(Array.Empty<HubCommunityModItem>(), "当前为纯净原版模式，未连接社区 Mod 服务器。", false);
            return;
        }

        _syncPage.SetBusy(true, "正在拉取服务器和社区 Mod 列表...", 30);
        string finalStatus = "社区 Mod 列表已刷新。";
        bool finalOk = true;
        try
        {
            var (ok, mods, msg) = await _communityClient.FetchListAsync(serverBase, _launchPage.PlayerNameInput, _formCts.Token);
            if (IsClosingOrDisposed) return;
            finalStatus = msg;
            finalOk = ok;
            _syncPage.RenderCommunityMods(mods, msg, ok);
            AppendLog($"[社区 Mod] {msg}");
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                finalStatus = $"拉取失败：{ex.Message}";
                finalOk = false;
                AppendLog($"[错误] 拉取社区 Mod 列表失败：{ex.Message}");
                _syncPage.RenderCommunityMods(Array.Empty<HubCommunityModItem>(), finalStatus, false);
            }
        }
        finally
        {
            if (!IsClosingOrDisposed)
            {
                _syncPage.SetBusy(false, finalStatus, finalOk ? 100 : 0);
            }
        }
    }

    private async Task DownloadHubCommunityModAsync(string modId)
    {
        if (IsClosingOrDisposed) return;
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string? serverBase = ResolveServerBaseForCommunity(showWarningIfNone: true);
        if (string.IsNullOrWhiteSpace(serverBase)) return;

        _syncPage.SetBusy(true, $"正在下载 {modId} 到本地 BepInEx\\mods\\...", 40);
        try
        {
            var (ok, msg, _) = await _communityClient.DownloadToLocalAsync(serverBase, modId, _gameInstall.GameDirectory, _formCts.Token);
            if (IsClosingOrDisposed) return;
            AppendLog($"[社区 Mod] 下载 {modId}：{msg}");
            if (ok)
            {
                MessageBox.Show(this, $"{msg}\n\n以后开单机或离线也能直接用，也可以重新上传分享给别的服务器。", "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshHubCommunityModsAsync();
            }
            else
            {
                MessageBox.Show(this, msg, "下载失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 下载 {modId} 异常：{ex.Message}");
                MessageBox.Show(this, $"下载出错：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsClosingOrDisposed)
            {
                _syncPage.SetBusy(false, "操作已完成。", 100);
            }
        }
    }

    private async Task UploadHubLocalModAsync()
    {
        string? serverBase = ResolveServerBaseForCommunity(showWarningIfNone: true);
        if (string.IsNullOrWhiteSpace(serverBase)) return;

        using var ofd = new OpenFileDialog
        {
            Title = "选择要上传的 .bazaarmod.zip，或选择本地 Mod 文件夹里的 mod.json",
            Filter = "Mod 包或配置 (*.bazaarmod.zip;mod.json)|*.bazaarmod.zip;mod.json|ZIP 压缩包 (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (_gameInstall?.IsValidGameInstall == true)
        {
            string localModsDir = Path.Combine(_gameInstall.GameDirectory, "BepInEx", "mods");
            if (Directory.Exists(localModsDir))
            {
                ofd.InitialDirectory = localModsDir;
            }
        }

        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        string selected = ofd.FileName;
        string targetPath = string.Equals(Path.GetFileName(selected), "mod.json", StringComparison.OrdinalIgnoreCase)
            ? (Path.GetDirectoryName(selected) ?? selected)
            : selected;

        _syncPage.SetBusy(true, "正在上传并等待服务器做安全校验和模拟对战检验...", 35);
        try
        {
            string playerName = string.IsNullOrWhiteSpace(_launchPage.PlayerNameInput)
                ? Environment.UserName
                : _launchPage.PlayerNameInput;
            var (ok, msg) = await _communityClient.UploadModAsync(serverBase, targetPath, playerName, _formCts.Token);
            if (IsClosingOrDisposed) return;
            AppendLog($"[社区 Mod] 上传 {Path.GetFileName(targetPath)}：{msg}");
            if (ok)
            {
                MessageBox.Show(this, $"{msg}\n\n现在大家都能在列表里下载，锦标赛房主也可以勾选它开赛。", "上传成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await RefreshHubCommunityModsAsync();
            }
            else
            {
                MessageBox.Show(this, msg, "上传未通过", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 上传本地 Mod 异常：{ex.Message}");
                MessageBox.Show(this, $"上传出错：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsClosingOrDisposed)
            {
                _syncPage.SetBusy(false, "操作已完成。", 100);
            }
        }
    }

    private async Task ApplyPerformanceTuningAsync(PerformanceTuningOptions options)
    {
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            PerformanceOperationResult res = await _perfService.ApplyAsync(_gameInstall.GameDirectory, options, AppendLog);
            _preferences.PerformanceOptions = options;
            SavePreferencesQuietly();

            BackupManifest manifest = _perfService.LoadBackupManifest(_gameInstall.GameDirectory);
            _perfPage.UpdateBackupBanner(_gameInstall.GameDirectory, manifest);

            string detail = res.Summary + (res.AppliedItems.Count > 0
                ? "\n\n已生效项：\n• " + string.Join("\n• ", res.AppliedItems)
                : string.Empty);
            MessageBox.Show(this, detail, "性能调优已应用", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 应用性能调优失败：{ex.Message}");
            MessageBox.Show(this, $"应用性能调优失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task RestorePureOfficialAsync()
    {
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult confirm = MessageBox.Show(
            this,
            "将从首次原版只读备份（_BazaarHubBackups\\original\\）逐字节恢复所有被调优的原始文件（boot.config、aa\\settings.json、BepInEx.cfg 等），并将自建服总开关 Enabled 设为 false。\n\n确定立即还原纯净官方原版吗？",
            "确认一键还原纯净官方原版",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (confirm != DialogResult.Yes) return;

        try
        {
            PerformanceOperationResult res = await _perfService.RestorePureOfficialAsync(
                _gameInstall.GameDirectory,
                disableBazaarLauncher: true,
                log: AppendLog);

            PerformanceTuningOptions pureOpt = PerformanceTuningOptions.CreatePureOfficialDefault();
            _perfPage.LoadOptions(pureOpt);
            _preferences.PerformanceOptions = pureOpt;
            _preferences.SelectedMode = ServerMode.PureOfficial;
            SavePreferencesQuietly();

            _launchPage.SelectServerMode(ServerMode.PureOfficial, notify: false);
            _perfPage.UpdateBackupBanner(_gameInstall.GameDirectory, _perfService.LoadBackupManifest(_gameInstall.GameDirectory));
            UpdateGlobalStatusSummary();

            MessageBox.Show(this, res.Summary, "还原完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 还原纯净官方原版失败：{ex.Message}");
            MessageBox.Show(this, $"还原失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ExportDiagnosticsZipAsync()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "导出排错诊断包 (ZIP)",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"BazaarHub_Diagnostics_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        };

        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        _diagPage.SetExportBusy(true);
        try
        {
            var urls = new List<string>();
            if (_preset.HasPresetRemote)
            {
                urls.Add(_preset.PresetRemoteUrl);
            }
            urls.Add(LauncherConstants.LocalStandaloneUrl);
            if (!string.IsNullOrWhiteSpace(_launchPage.CustomServerUrl) &&
                !urls.Any(u => string.Equals(u, _launchPage.CustomServerUrl, StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(_launchPage.CustomServerUrl);
            }

            string path = await _diagService.ExportZipAsync(
                sfd.FileName,
                _gameInstall?.GameDirectory,
                urls,
                _diagPage.GetAllLogsSnapshot(),
                log: AppendLog,
                cancellationToken: _formCts.Token);
            if (IsClosingOrDisposed) return;

            MessageBox.Show(this, $"排错诊断包已成功导出至：\n{path}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 导出诊断包失败：{ex.Message}");
                MessageBox.Show(this, $"导出诊断包失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (!IsClosingOrDisposed)
            {
                _diagPage.SetExportBusy(false);
            }
        }
    }

    private void UpdateGlobalStatusSummary()
    {
        ServerMode mode = _launchPage.SelectedMode;
        string modeName = FormatModeName(mode);

        // 侧边栏底部摘要
        _lblSidebarMode.Text = $"模式：{modeName}";
        if (_gameInstall?.IsValidGameInstall == true)
        {
            _lblSidebarGame.Text = _gameInstall.IsBepInExReady ? "游戏：本体与插件就绪" : "游戏：本体就绪（缺插件）";
            _lblSidebarGame.ForeColor = _gameInstall.IsBepInExReady ? HubTheme.StatusGreen : HubTheme.StatusYellow;
            _topGamePill.SetState(
                _gameInstall.IsBepInExReady ? PillTone.Green : PillTone.Amber,
                _gameInstall.IsBepInExReady ? "游戏与插件就绪" : "缺 BepInEx 插件",
                showDot: true);
        }
        else
        {
            _lblSidebarGame.Text = "游戏：未定位目录";
            _lblSidebarGame.ForeColor = HubTheme.StatusYellow;
            _topGamePill.SetState(PillTone.Amber, "未定位游戏目录", showDot: true);
        }

        // 顶部服务端连通状态胶囊 & 诊断页自检卡同步
        PillTone srvTone;
        string srvTitle;
        string srvDetail;

        if (mode == ServerMode.PureOfficial)
        {
            srvTone = PillTone.Blue;
            srvTitle = "纯净官方原版";
            srvDetail = "Enabled = false · 直连官方 Steam";
        }
        else if (_probeResults.TryGetValue(mode, out ServerProbeResult? probe))
        {
            if (probe.IsOnline)
            {
                srvTone = probe.LatencyMs <= 120 ? PillTone.Green : (probe.LatencyMs <= 300 ? PillTone.Amber : PillTone.Red);
                srvTitle = $"{FormatShortModeName(mode)} {probe.LatencyMs}ms";
                srvDetail = probe.StatusMessage;
            }
            else if (mode == ServerMode.LocalStandalone)
            {
                srvTone = _standaloneHost.IsManagedServerRunning ? PillTone.Green : PillTone.Amber;
                srvTitle = _standaloneHost.IsManagedServerRunning ? "单机服运行中" : "单机服待拉起";
                srvDetail = _standaloneHost.IsManagedServerRunning ? "127.0.0.1:8080 托管就绪" : "点击启动时自动拉起本地服";
            }
            else
            {
                srvTone = PillTone.Red;
                srvTitle = $"{FormatShortModeName(mode)} 离线";
                srvDetail = probe.StatusMessage;
            }
        }
        else
        {
            srvTone = PillTone.Neutral;
            srvTitle = $"{FormatShortModeName(mode)} 探测中";
            srvDetail = "正在检测服务器连通状态...";
        }

        _topServerPill.SetState(srvTone, srvTitle, showDot: true);
        _diagPage.UpdateHealthCards(
            _gameInstall,
            srvTitle,
            srvDetail,
            srvTone,
            _standaloneHost.IsManagedServerRunning,
            _standaloneHost.ActiveStateDirectory);
    }

    private void SavePreferencesQuietly()
    {
        try
        {
            _configService.SaveHubPreferences(_preferences);
        }
        catch
        {
            // 忽略非关键偏好保存失败
        }
    }

    private void AppendLog(string message)
    {
        if (IsClosingOrDisposed) return;
        _diagPage.AppendLog(message);
    }

    private string FormatModeName(ServerMode mode) => mode switch
    {
        ServerMode.PresetRemote => _preset.HasPresetRemote ? _preset.PresetRemoteName : "本地单机离线版",
        ServerMode.LocalStandalone => "本地单机离线版",
        ServerMode.CustomServer => "自定义服务器",
        ServerMode.PureOfficial => "纯净官方原版",
        _ => "未知模式"
    };

    private string FormatShortModeName(ServerMode mode) => mode switch
    {
        ServerMode.PresetRemote => _preset.HasPresetRemote ? _preset.PresetRemoteShortName : "本地单机",
        ServerMode.LocalStandalone => "本地单机",
        ServerMode.CustomServer => "自定义服",
        ServerMode.PureOfficial => "官方原版",
        _ => "节点"
    };

    private static Label CreateNavGroupLabel(string text) => new()
    {
        Text = text,
        Font = HubTheme.FontCaption,
        ForeColor = HubTheme.TextMuted,
        AutoSize = true,
        Dock = DockStyle.Top,
        Padding = new Padding(22, 4, 0, 6),
        Margin = new Padding(0)
    };

    private static void OpenDirectorySafely(string? dirPath)
    {
        if (string.IsNullOrWhiteSpace(dirPath) || !Directory.Exists(dirPath)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = dirPath,
                UseShellExecute = true
            });
        }
        catch
        {
            // 忽略打开资源管理器失败
        }
    }

    private async Task OpenWorkshopInBrowserAsync()
    {
        try
        {
            ServerMode mode = _launchPage.SelectedMode;
            string serverBase = LauncherConfigService.ResolveServerBase(mode, _launchPage.CustomServerUrl);

            if (mode == ServerMode.PureOfficial || string.IsNullOrWhiteSpace(serverBase))
            {
                DialogResult choice = MessageBox.Show(
                    this,
                    "当前处于「纯净官方原版」模式。是否自动拉起本机单机服务器并打开本地零代码创意工坊（http://127.0.0.1:8080/workshop/）？",
                    "打开创意工坊编辑器",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (choice != DialogResult.Yes) return;
                mode = ServerMode.LocalStandalone;
                serverBase = LauncherConstants.LocalStandaloneUrl;
            }

            if (mode == ServerMode.LocalStandalone)
            {
                ServerProbeResult probe = await _probeService.ProbeAsync(LauncherConstants.LocalStandaloneUrl, _formCts.Token);
                if (IsClosingOrDisposed) return;
                if (!probe.IsOnline)
                {
                    AppendLog("[工坊] 正在自动拉起本地单机服务端以承载 /workshop/ 创意工坊编辑器...");
                    (bool ready, string msg) = await _standaloneHost.EnsureStartedAsync(8080, AppendLog, _formCts.Token, _gameInstall?.GameDirectory);
                    if (IsClosingOrDisposed) return;
                    RefreshStandaloneStatusUi();
                    if (!ready)
                    {
                        MessageBox.Show(this, $"无法启动本地单机服务端：\n{msg}", "创意工坊启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
            }

            string workshopUrl = $"{serverBase.TrimEnd('/')}/workshop/";
            AppendLog($"[工坊] 正在浏览器打开创意工坊编辑器：{workshopUrl}");
            Process.Start(new ProcessStartInfo(workshopUrl) { UseShellExecute = true });
        }
        catch (OperationCanceledException) when (IsClosingOrDisposed)
        {
        }
        catch (Exception ex)
        {
            if (!IsClosingOrDisposed)
            {
                AppendLog($"[错误] 打开创意工坊编辑器失败：{ex.Message}");
                MessageBox.Show(this, $"打开创意工坊编辑器失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    private void PaintBrandHeader(object? sender, PaintEventArgs e)
    {
        if (sender is not Control panel) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int padX = HubTheme.Scale(panel, 18);
        int embSz = HubTheme.Scale(panel, 40);
        int embY = (panel.Height - embSz) / 2;

        // 精致鎏金菱形奇幻集市星徽
        RectangleF emblem = new(padX, embY, embSz, embSz);
        using (var path = HubTheme.CreateRoundedRectPath(emblem, HubTheme.ScaleF(panel, 11)))
        using (var fill = new LinearGradientBrush(emblem, HubTheme.GoldBright, HubTheme.GoldDark, LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(fill, path);
            using var rim = new Pen(Color.FromArgb(120, 255, 250, 232), 1f);
            g.DrawPath(rim, path);
        }

        float cx = emblem.X + emblem.Width * 0.5f;
        float cy = emblem.Y + emblem.Height * 0.5f;
        float rOuter = embSz * 0.30f;
        PointF[] diamond =
        {
            new(cx, cy - rOuter),
            new(cx + rOuter * 0.85f, cy),
            new(cx, cy + rOuter),
            new(cx - rOuter * 0.85f, cy)
        };
        using (var innerBrush = new SolidBrush(HubTheme.Surface0))
        {
            g.FillPolygon(innerBrush, diamond);
        }
        float dotR = embSz * 0.10f;
        using (var dotBrush = new SolidBrush(HubTheme.GoldBright))
        {
            g.FillEllipse(dotBrush, cx - dotR, cy - dotR, dotR * 2f, dotR * 2f);
        }

        int textX = padX + embSz + HubTheme.Scale(panel, 11);
        int textW = Math.Max(40, panel.Width - textX - HubTheme.Scale(panel, 10));
        TextRenderer.DrawText(
            g,
            "BAZAAR HUB",
            HubTheme.FontBrand,
            new Rectangle(textX, embY + 1, textW, HubTheme.Scale(panel, 22)),
            HubTheme.GoldLight,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        TextRenderer.DrawText(
            g,
            "大巴扎自建服启动器",
            HubTheme.FontSmall,
            new Rectangle(textX, embY + HubTheme.Scale(panel, 22), textW, HubTheme.Scale(panel, 18)),
            HubTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        using var sep = new Pen(HubTheme.BorderHairline, 1f);
        g.DrawLine(sep, padX, panel.Height - 1, panel.Width - padX, panel.Height - 1);
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        // D7-P1-03：本地托管单机服运行中时弹出关窗确认，防止误关启动器导致正在进行的单机对局断线
        if (e.CloseReason == CloseReason.UserClosing && _standaloneHost.IsManagedServerRunning)
        {
            bool gameRunning = UpdateSyncService.IsGameProcessRunning();
            string warnBody = gameRunning
                ? "检测到《The Bazaar》游戏进程与本启动器托管的本地单机服务端均正在运行！\n\n现在关闭启动器将立即优雅停止本地单机服务端并导致当前对局断线（存档将自动落盘保留）。\n\n确定要停止单机服务端并关闭启动器吗？"
                : "本启动器托管的本地单机服务端仍在运行中。\n\n关闭启动器将优雅发送停止信号触发 SQLite WAL 刷盘保存存档并退出服务端。\n\n确定要停止服务端并关闭启动器吗？";

            DialogResult choice = MessageBox.Show(
                this,
                warnBody,
                "确认停止本地单机服务端并退出",
                MessageBoxButtons.YesNo,
                gameRunning ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (choice != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        try
        {
            _formCts.Cancel();
        }
        catch
        {
            // 忽略窗体关闭时的取消竞态异常
        }

        SavePreferencesQuietly();
        _toolTip.Dispose();
        _standaloneHost.Dispose();
        _probeService.Dispose();
        _syncService.Dispose();
        _communityClient.Dispose();
        _formCts.Dispose();
    }
}

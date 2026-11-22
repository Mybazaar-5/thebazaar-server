using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 大巴扎自建服启动器主窗体（深海蓝黑 + 鎏金主题、左侧导航栏 + 右侧 4 页平滑切换）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly GameLocatorService _locator = new();
    private readonly LauncherConfigService _configService = new();
    private readonly ServerProbeService _probeService = new();
    private readonly UpdateSyncService _syncService = new();
    private readonly PerformanceTunerService _perfService = new();
    private readonly StandaloneServerHost _standaloneHost = new();
    private readonly DiagnosticsExportService _diagService = new();
    private readonly CommunityModClient _communityClient = new();

    private readonly NavSidebarButton _navLaunch;
    private readonly NavSidebarButton _navSync;
    private readonly NavSidebarButton _navPerf;
    private readonly NavSidebarButton _navDiag;
    private readonly Label _lblSidebarMode;
    private readonly Label _lblSidebarGame;
    private readonly Panel _contentHost;

    private readonly QuickLaunchPage _launchPage;
    private readonly UpdateSyncPage _syncPage;
    private readonly PerformanceTunerPage _perfPage;
    private readonly DiagnosticsPage _diagPage;

    private HubPreferences _preferences = new();
    private GameInstallInfo? _gameInstall;
    private bool _isLaunching;
    private bool _isSyncing;
    private bool _communityModsLoadedOnce;

    public MainForm()
    {
        Text = $"大巴扎自建服启动器 · BazaarHub v{LauncherConstants.HubVersion}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1060, 680);
        Size = new Size(1180, 750);
        BackColor = HubTheme.BgWindow;
        ForeColor = HubTheme.TextPrimary;
        Font = HubTheme.FontBody;
        DoubleBuffered = true;

        var mainSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = HubTheme.BgWindow,
            Margin = new Padding(0)
        };
        mainSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 224));
        mainSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── 左侧深邃导航栏 ───────────────────────────────────────────────
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = HubTheme.BgSidebar,
            Padding = new Padding(0, 0, 1, 0)
        };
        sidebar.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderSubtle, 1f);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        // 顶部鎏金徽章区
        var brandPanel = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = HubTheme.BgSidebar };
        brandPanel.Paint += PaintBrandHeader;

        // 导航按钮区
        var navStack = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = HubTheme.BgSidebar,
            Padding = new Padding(6, 10, 6, 10)
        };

        _navLaunch = new NavSidebarButton { Text = "快速启动与选服", IconText = "⚡", Width = 208, Height = 46, Margin = new Padding(0, 0, 0, 6) };
        _navSync = new NavSidebarButton { Text = "更新与 Mod 同步", IconText = "◈", Width = 208, Height = 46, Margin = new Padding(0, 0, 0, 6) };
        _navPerf = new NavSidebarButton { Text = "本体性能调优", IconText = "⚙", Width = 208, Height = 46, Margin = new Padding(0, 0, 0, 6) };
        _navDiag = new NavSidebarButton { Text = "日志与排错", IconText = "☰", Width = 208, Height = 46, Margin = new Padding(0) };

        _navLaunch.Click += (_, _) => SwitchPage(0);
        _navSync.Click += (_, _) => SwitchPage(1);
        _navPerf.Click += (_, _) => SwitchPage(2);
        _navDiag.Click += (_, _) => SwitchPage(3);

        var btnSidebarWorkshop = new GoldButton
        {
            Text = "创意工坊编辑器 (Web)",
            Variant = GoldButtonVariant.OutlineGold,
            Width = 208,
            Height = 40,
            Margin = new Padding(0, 14, 0, 0)
        };
        btnSidebarWorkshop.Click += async (_, _) => await OpenWorkshopInBrowserAsync();

        navStack.Controls.AddRange(new Control[] { _navLaunch, _navSync, _navPerf, _navDiag, btnSidebarWorkshop });

        // 底部迷你状态面板
        var footerPanel = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = HubTheme.BgSidebar, Padding = new Padding(14, 10, 14, 12) };
        footerPanel.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderMuted, 1f);
            e.Graphics.DrawLine(pen, 14, 0, footerPanel.Width - 14, 0);
        };

        _lblSidebarMode = new Label
        {
            Text = "模式：远程自建服",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Top,
            Height = 20
        };
        _lblSidebarGame = new Label
        {
            Text = "游戏：等待检测...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Top,
            Height = 20
        };
        var lblVersion = new Label
        {
            Text = $"BazaarHub v{LauncherConstants.HubVersion} · 零依赖原生版",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Height = 18
        };
        footerPanel.Controls.Add(lblVersion);
        footerPanel.Controls.Add(_lblSidebarGame);
        footerPanel.Controls.Add(_lblSidebarMode);

        sidebar.Controls.Add(footerPanel);
        sidebar.Controls.Add(navStack);
        sidebar.Controls.Add(brandPanel);

        // ── 右侧页面宿主 ─────────────────────────────────────────────────
        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = HubTheme.BgWindow };

        _launchPage = new QuickLaunchPage();
        _syncPage = new UpdateSyncPage();
        _perfPage = new PerformanceTunerPage();
        _diagPage = new DiagnosticsPage();

        WireEvents();

        mainSplit.Controls.Add(sidebar, 0, 0);
        mainSplit.Controls.Add(_contentHost, 1, 0);
        Controls.Add(mainSplit);

        SwitchPage(0);

        Shown += async (_, _) => await InitializeOnStartupAsync();
        FormClosing += OnMainFormClosing;
    }

    private void WireEvents()
    {
        _standaloneHost.LifecycleMessage += msg => AppendLog($"[托管] {msg}");

        // 标签页 1 事件
        _launchPage.AutoLocateRequested += () => LocateGame(forceFullScan: true);
        _launchPage.BrowseGameDirRequested += BrowseGameDirectory;
        _launchPage.OpenGameDirRequested += () => OpenDirectorySafely(_gameInstall?.GameDirectory);
        _launchPage.ServerModeChanged += mode =>
        {
            _preferences.SelectedMode = mode;
            UpdateSidebarSummary();
            SavePreferencesQuietly();
        };
        _launchPage.ProbeRefreshRequested += async () => await RefreshAllServerProbesAsync();
        _launchPage.SavePlayerAndConfigRequested += () => SaveClientConfigFromUi(showFeedback: true);
        _launchPage.LaunchRequested += async () => await LaunchWorkflowAsync();
        _launchPage.OpenWorkshopRequested += async () => await OpenWorkshopInBrowserAsync();

        // 标签页 2 事件
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

        // 标签页 3 事件
        _perfPage.ApplyTuningRequested += async options => await ApplyPerformanceTuningAsync(options);
        _perfPage.RestorePureOfficialRequested += async () => await RestorePureOfficialAsync();

        // 标签页 4 事件
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

    private void SwitchPage(int index)
    {
        _navLaunch.Selected = index == 0;
        _navSync.Selected = index == 1;
        _navPerf.Selected = index == 2;
        _navDiag.Selected = index == 3;

        Control target = index switch
        {
            1 => _syncPage,
            2 => _perfPage,
            3 => _diagPage,
            _ => _launchPage
        };

        _contentHost.SuspendLayout();
        _contentHost.Controls.Clear();
        target.Dock = DockStyle.Fill;
        _contentHost.Controls.Add(target);
        _contentHost.ResumeLayout();

        if (index == 1 && !_communityModsLoadedOnce)
        {
            _communityModsLoadedOnce = true;
            _ = RefreshHubCommunityModsAsync();
        }
    }

    private async Task InitializeOnStartupAsync()
    {
        AppendLog($"[信息] 大巴扎自建服启动器 BazaarHub v{LauncherConstants.HubVersion} 已就绪。");
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

        UpdateSidebarSummary();
    }

    private async Task RefreshAllServerProbesAsync()
    {
        _launchPage.SetProbingState(true);
        try
        {
            string customUrl = _launchPage.CustomServerUrl;
            Task<ServerProbeResult> tCloud = _probeService.ProbeAsync(LauncherConstants.CloudTencentUrl);
            Task<ServerProbeResult> tLocal = _probeService.ProbeAsync(LauncherConstants.LocalStandaloneUrl);
            Task<ServerProbeResult> tCustom = string.IsNullOrWhiteSpace(customUrl)
                ? Task.FromResult(new ServerProbeResult { StatusMessage = "未填写自定义地址" })
                : _probeService.ProbeAsync(customUrl);

            await Task.WhenAll(tCloud, tLocal, tCustom);

            _launchPage.UpdateServerProbeResult(ServerMode.CloudTencent, tCloud.Result);
            _launchPage.UpdateServerProbeResult(ServerMode.LocalStandalone, tLocal.Result);
            _launchPage.UpdateServerProbeResult(ServerMode.CustomServer, tCustom.Result);

            AppendLog($"[测速] 远程自建服：{tCloud.Result.StatusMessage} ｜ 本地单机：{tLocal.Result.StatusMessage}");
        }
        catch (Exception ex)
        {
            AppendLog($"[警告] 服务器测速异常：{ex.Message}");
        }
        finally
        {
            _launchPage.SetProbingState(false);
        }
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
                        _preferences.SavedCustomServers.RemoveRange(12, _preferences.SavedCustomServers.Count - 12);
                }
                _launchPage.UpdateSavedServerDropdown(_preferences.SavedCustomServers, normalizedCustom);
            }
            _preferences.AutoSyncBeforeLaunch = _launchPage.AutoSyncBeforeLaunch;
            _preferences.PerformanceOptions = _perfPage.CollectOptions();
            SavePreferencesQuietly();

            UpdateSidebarSummary();
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
            MessageBox.Show(this, "请先在顶部定位有效的《The Bazaar》游戏安装目录。", "无法启动", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _isLaunching = true;
        try
        {
            ServerMode mode = _launchPage.SelectedMode;
            string gameDir = _gameInstall!.GameDirectory;
            string serverBase = LauncherConfigService.ResolveServerBase(mode, _launchPage.CustomServerUrl);
            PerformanceTuningOptions perfOptions = _perfPage.CollectOptions();

            // 1. 本地单机模式：先自动拉起本机 BazaarServer.exe 并等待 /healthz 就绪
            if (mode == ServerMode.LocalStandalone)
            {
                _launchPage.SetLaunchBusy(true, "正在拉起本地单机服务端并等待 /healthz 就绪...", 20);
                (bool ready, string msg) = await _standaloneHost.EnsureStartedAsync(8080, AppendLog);
                if (!ready)
                {
                    _launchPage.SetLaunchBusy(false, msg, 0);
                    MessageBox.Show(this, msg, "单机服务端启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            // 2. 启动前增量检查并同步插件 / 汉化 / Mod 资产（支持断网优雅降级）
            if (mode != ServerMode.PureOfficial && _launchPage.AutoSyncBeforeLaunch && !string.IsNullOrWhiteSpace(serverBase))
            {
                _launchPage.SetLaunchBusy(true, "正在连接服务端执行 SHA-256 增量更新检查...", 35);
                var progress = new Progress<SyncProgress>(p =>
                {
                    int mapped = 35 + (int)(p.Percent * 0.5);
                    _launchPage.SetLaunchBusy(true, p.Message, Math.Clamp(mapped, 35, 88));
                });

                UpdateSyncReport syncReport = await _syncService.SyncAsync(
                    gameDir,
                    serverBase,
                    applyDownloads: true,
                    progress: progress,
                    log: AppendLog);
                _syncPage.RenderReport(syncReport);
            }

            // 3. 拉起 TheBazaar.exe（附加性能调优 Unity 启动参数）
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
        catch (Exception ex)
        {
            AppendLog($"[错误] 启动游戏失败：{ex.Message}");
            _launchPage.SetLaunchBusy(false, $"启动失败：{ex.Message}", 0);
            MessageBox.Show(this, $"启动失败：{ex.Message}", "启动错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _isLaunching = false;
        }
    }

    private async Task RunManualSyncAsync(bool applyDownloads)
    {
        if (_isSyncing) return;
        if (_gameInstall == null || !_gameInstall.IsValidGameInstall)
        {
            MessageBox.Show(this, "请先在「快速启动与选服」定位有效的游戏目录。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string serverBase = LauncherConfigService.ResolveServerBase(_launchPage.SelectedMode, _launchPage.CustomServerUrl);
        if (string.IsNullOrWhiteSpace(serverBase))
        {
            MessageBox.Show(this, "当前处于「纯净官方原版」模式，请先选择远程自建服、本地单机或自定义服务器再同步。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _isSyncing = true;
        _syncPage.SetBusy(true, "正在连接服务端获取最新清单...", 10);
        try
        {
            var progress = new Progress<SyncProgress>(p => _syncPage.SetBusy(true, p.Message, p.Percent));
            UpdateSyncReport report = await _syncService.SyncAsync(
                _gameInstall.GameDirectory,
                serverBase,
                applyDownloads: applyDownloads,
                progress: progress,
                log: AppendLog);
            _syncPage.RenderReport(report);
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 同步异常：{ex.Message}");
            _syncPage.SetBusy(false, $"同步失败：{ex.Message}", 0);
        }
        finally
        {
            _isSyncing = false;
            _syncPage.SetBusy(false, "同步流程已结束，可查看上方明细。", 100);
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
            var (ok, mods, msg) = await _communityClient.FetchListAsync(serverBase, _launchPage.PlayerNameInput);
            finalStatus = msg;
            finalOk = ok;
            _syncPage.RenderCommunityMods(mods, msg, ok);
            AppendLog($"[社区 Mod] {msg}");
        }
        catch (Exception ex)
        {
            finalStatus = $"拉取失败：{ex.Message}";
            finalOk = false;
            AppendLog($"[错误] 拉取社区 Mod 列表失败：{ex.Message}");
            _syncPage.RenderCommunityMods(Array.Empty<HubCommunityModItem>(), finalStatus, false);
        }
        finally
        {
            _syncPage.SetBusy(false, finalStatus, finalOk ? 100 : 0);
        }
    }

    private async Task DownloadHubCommunityModAsync(string modId)
    {
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
            var (ok, msg, savedDir) = await _communityClient.DownloadToLocalAsync(serverBase, modId, _gameInstall.GameDirectory);
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
        catch (Exception ex)
        {
            AppendLog($"[错误] 下载 {modId} 异常：{ex.Message}");
            MessageBox.Show(this, $"下载出错：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncPage.SetBusy(false, "操作已完成。", 100);
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
                ofd.InitialDirectory = localModsDir;
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
            var (ok, msg) = await _communityClient.UploadModAsync(serverBase, targetPath, playerName);
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
        catch (Exception ex)
        {
            AppendLog($"[错误] 上传本地 Mod 异常：{ex.Message}");
            MessageBox.Show(this, $"上传出错：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _syncPage.SetBusy(false, "操作已完成。", 100);
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
            UpdateSidebarSummary();

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
            var urls = new List<string>
            {
                LauncherConstants.CloudTencentUrl,
                LauncherConstants.LocalStandaloneUrl
            };
            if (!string.IsNullOrWhiteSpace(_launchPage.CustomServerUrl))
            {
                urls.Add(_launchPage.CustomServerUrl);
            }

            string path = await _diagService.ExportZipAsync(
                sfd.FileName,
                _gameInstall?.GameDirectory,
                urls,
                _diagPage.GetAllLogsSnapshot(),
                log: AppendLog);

            MessageBox.Show(this, $"排错诊断包已成功导出至：\n{path}", "导出成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"[错误] 导出诊断包失败：{ex.Message}");
            MessageBox.Show(this, $"导出诊断包失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _diagPage.SetExportBusy(false);
        }
    }

    private void UpdateSidebarSummary()
    {
        _lblSidebarMode.Text = $"模式：{FormatModeName(_launchPage.SelectedMode)}";
        _lblSidebarGame.Text = _gameInstall?.IsValidGameInstall == true
            ? (_gameInstall.IsBepInExReady ? "游戏：本体与插件就绪" : "游戏：本体就绪（缺插件）")
            : "游戏：未定位目录";
        _lblSidebarGame.ForeColor = _gameInstall?.IsValidGameInstall == true ? HubTheme.StatusGreen : HubTheme.StatusYellow;
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

    private void AppendLog(string message) => _diagPage.AppendLog(message);

    private static string FormatModeName(ServerMode mode) => mode switch
    {
        ServerMode.CloudTencent => "远程自建服 (示例)",
        ServerMode.LocalStandalone => "本地单机离线版",
        ServerMode.CustomServer => "自定义服务器",
        ServerMode.PureOfficial => "纯净官方原版",
        _ => "未知模式"
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

    /// <summary>
    /// 一键在系统默认浏览器打开当前所选服务器的零代码 Web 创意工坊（/workshop/）；
    /// 若当前处于本地单机离线模式且本机 8080 服务尚未就绪，则自动拉起托管单机服后再打开。
    /// </summary>
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
                ServerProbeResult probe = await _probeService.ProbeAsync(LauncherConstants.LocalStandaloneUrl);
                if (!probe.IsOnline)
                {
                    AppendLog("[工坊] 正在自动拉起本地单机服务端以承载 /workshop/ 创意工坊编辑器...");
                    (bool ready, string msg) = await _standaloneHost.EnsureStartedAsync(8080, AppendLog);
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
        catch (Exception ex)
        {
            AppendLog($"[错误] 打开创意工坊编辑器失败：{ex.Message}");
            MessageBox.Show(this, $"打开创意工坊编辑器失败：{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void PaintBrandHeader(object? sender, PaintEventArgs e)
    {
        if (sender is not Control panel) return;
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // 绘制鎏金菱形徽章
        Rectangle emblem = new(18, 22, 42, 42);
        using (var path = HubTheme.CreateRoundedRectPath(emblem, 10))
        using (var fill = new LinearGradientBrush(emblem, HubTheme.GoldLight, HubTheme.GoldDark, LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(fill, path);
        }

        Point[] diamond =
        {
            new(39, 30),
            new(50, 43),
            new(39, 56),
            new(28, 43)
        };
        using (var innerBrush = new SolidBrush(HubTheme.BgSidebar))
        {
            g.FillPolygon(innerBrush, diamond);
        }
        using (var dotBrush = new SolidBrush(HubTheme.GoldLight))
        {
            g.FillEllipse(dotBrush, 35, 39, 8, 8);
        }

        TextRenderer.DrawText(g, "BAZAAR HUB", HubTheme.FontBrand, new Rectangle(70, 22, 144, 24), HubTheme.GoldLight, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "大巴扎自建服启动器", HubTheme.FontSmall, new Rectangle(70, 46, 144, 20), HubTheme.TextSecondary, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        using var sep = new Pen(HubTheme.BorderMuted, 1f);
        g.DrawLine(sep, 16, panel.Height - 1, panel.Width - 16, panel.Height - 1);
    }

    private void OnMainFormClosing(object? sender, FormClosingEventArgs e)
    {
        SavePreferencesQuietly();
        _standaloneHost.Dispose();
        _probeService.Dispose();
        _syncService.Dispose();
        _communityClient.Dispose();
    }
}

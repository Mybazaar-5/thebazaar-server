using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 标签页 1：「快速启动与选服」。
/// 包含游戏路径定位体检、4 种服务器卡片（含实时 /api/ping 延迟与 /healthz 在线绿灯）、
/// 玩家防撞号昵称管理与大号鎏金「同步更新并启动游戏」操作区。
/// </summary>
public sealed class QuickLaunchPage : UserControl
{
    private readonly TextBox _txtGameDir;
    private readonly Label _lblGameStatus;
    private readonly ServerModeCard _cardCloud;
    private readonly ServerModeCard _cardLocal;
    private readonly ServerModeCard _cardCustom;
    private readonly ServerModeCard _cardOfficial;
    private readonly Panel _customUrlRow;
    private readonly ComboBox _cmbSavedServers;
    private readonly TextBox _txtCustomUrl;
    private readonly TextBox _txtPlayerName;
    private readonly CheckBox _chkAutoSync;
    private readonly CheckBox _chkAcceptServerMods;
    private readonly CheckBox _chkBypassSteam;
    private readonly CheckBox _chkUnlockAll;
    private readonly GoldButton _btnLaunch;
    private readonly GoldButton _btnRefreshPing;
    private readonly GoldProgressBar _progressBar;
    private readonly Label _lblActionStatus;

    private ServerMode _selectedMode = ServerMode.CloudTencent;

    public event Action? AutoLocateRequested;
    public event Action? BrowseGameDirRequested;
    public event Action? OpenGameDirRequested;
    public event Action<ServerMode>? ServerModeChanged;
    public event Action? ProbeRefreshRequested;
    public event Action? SavePlayerAndConfigRequested;
    public event Action? LaunchRequested;
    public event Action? OpenWorkshopRequested;

    public QuickLaunchPage()
    {
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Padding = new Padding(22, 16, 22, 18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // 页面标题区
        var headerPanel = new Panel { Height = 52, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10) };
        var lblTitle = new Label
        {
            Text = "快速启动与选服",
            Font = HubTheme.FontPageTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Location = new Point(0, 0)
        };
        var lblDesc = new Label
        {
            Text = "一键自动定位游戏、切换云端服 / 单机离线版 / 纯净官方原版，并实时监测节点延迟。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 30)
        };
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblDesc);

        var btnOpenWorkshop = new GoldButton
        {
            Text = "打开创意工坊编辑器（Web）",
            Variant = GoldButtonVariant.OutlineGold,
            Size = new Size(204, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(headerPanel.Width - 206, 8)
        };
        btnOpenWorkshop.Click += (_, _) => OpenWorkshopRequested?.Invoke();
        headerPanel.Controls.Add(btnOpenWorkshop);
        root.Controls.Add(headerPanel);

        // 卡片 1：游戏路径与本体状态
        var gameCard = new GoldCardPanel { Dock = DockStyle.Fill, Height = 108, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(14, 12, 14, 12) };
        var gameLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.Transparent };
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

        gameLayout.Controls.Add(new Label
        {
            Text = "游戏安装目录（TheBazaar.exe）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var pathRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        pathRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));

        _txtGameDir = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Margin = new Padding(0, 4, 8, 0) };
        HubTheme.StyleTextBox(_txtGameDir);

        var btnAuto = new GoldButton { Text = "自动探测", Variant = GoldButtonVariant.OutlineGold, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 6, 1) };
        btnAuto.Click += (_, _) => AutoLocateRequested?.Invoke();

        var btnBrowse = new GoldButton { Text = "选择目录", Variant = GoldButtonVariant.SubtleDark, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 6, 1) };
        btnBrowse.Click += (_, _) => BrowseGameDirRequested?.Invoke();

        var btnOpen = new GoldButton { Text = "打开目录", Variant = GoldButtonVariant.SubtleDark, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
        btnOpen.Click += (_, _) => OpenGameDirRequested?.Invoke();

        pathRow.Controls.Add(_txtGameDir, 0, 0);
        pathRow.Controls.Add(btnAuto, 1, 0);
        pathRow.Controls.Add(btnBrowse, 2, 0);
        pathRow.Controls.Add(btnOpen, 3, 0);
        gameLayout.Controls.Add(pathRow, 0, 1);

        _lblGameStatus = new Label
        {
            Text = "正在检测本机游戏目录...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        gameLayout.Controls.Add(_lblGameStatus, 0, 2);
        gameCard.Controls.Add(gameLayout);
        root.Controls.Add(gameCard);

        // 卡片 2：服务器选择卡片组
        var serverCard = new GoldCardPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(14, 12, 14, 12) };
        var serverLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, BackColor = Color.Transparent };
        serverLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var srvHeader = new TableLayoutPanel { Dock = DockStyle.Fill, Height = 32, ColumnCount = 2, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 6) };
        srvHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srvHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        srvHeader.Controls.Add(new Label
        {
            Text = "服务器与运行模式（点击卡片切换）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _btnRefreshPing = new GoldButton { Text = "测速刷新", Variant = GoldButtonVariant.SubtleDark, Dock = DockStyle.Fill };
        _btnRefreshPing.Click += (_, _) => ProbeRefreshRequested?.Invoke();
        srvHeader.Controls.Add(_btnRefreshPing, 1, 0);
        serverLayout.Controls.Add(srvHeader);

        var cardsGrid = new TableLayoutPanel { Dock = DockStyle.Top, Height = 104, ColumnCount = 4, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 6) };
        for (int i = 0; i < 4; i++)
        {
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        _cardCloud = new ServerModeCard(ServerMode.CloudTencent, "远程自建服 (示例)", "请用「自定义服务器」填入你的自建服地址") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _cardLocal = new ServerModeCard(ServerMode.LocalStandalone, "本地单机离线版", "127.0.0.1:8080 · 自动托服") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _cardCustom = new ServerModeCard(ServerMode.CustomServer, "自定义服务器", "填入好友或自搭私服地址") { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _cardOfficial = new ServerModeCard(ServerMode.PureOfficial, "纯净官方原版", "Enabled = false · 直连官方") { Dock = DockStyle.Fill, Margin = new Padding(0) };
        _cardOfficial.SetProbeStatus(HubTheme.StatusBlue, "停用自建服补丁，走 Steam 原版");

        _cardCloud.Click += (_, _) => SelectServerMode(ServerMode.CloudTencent, notify: true);
        _cardLocal.Click += (_, _) => SelectServerMode(ServerMode.LocalStandalone, notify: true);
        _cardCustom.Click += (_, _) => SelectServerMode(ServerMode.CustomServer, notify: true);
        _cardOfficial.Click += (_, _) => SelectServerMode(ServerMode.PureOfficial, notify: true);

        cardsGrid.Controls.Add(_cardCloud, 0, 0);
        cardsGrid.Controls.Add(_cardLocal, 1, 0);
        cardsGrid.Controls.Add(_cardCustom, 2, 0);
        cardsGrid.Controls.Add(_cardOfficial, 3, 0);
        serverLayout.Controls.Add(cardsGrid);

        // 自定义服务器地址行（仅选自定义模式时高亮展开）
        _customUrlRow = new Panel { Dock = DockStyle.Top, Height = 36, Visible = false, Margin = new Padding(0, 4, 0, 0) };
        var customUrlLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent };
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        customUrlLayout.Controls.Add(new Label
        {
            Text = "自定义服地址：",
            Font = HubTheme.FontBodyBold,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _txtCustomUrl = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 8, 0), Text = "http://127.0.0.1:8080" };
        HubTheme.StyleTextBox(_txtCustomUrl);
        _txtCustomUrl.TextChanged += (_, _) => _cardCustom.CardSubtitle = string.IsNullOrWhiteSpace(_txtCustomUrl.Text) ? "请输入 http:// 地址" : _txtCustomUrl.Text.Trim();

        _cmbSavedServers = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = HubTheme.BgInput,
            ForeColor = HubTheme.TextPrimary,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 5, 8, 0)
        };
        _cmbSavedServers.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbSavedServers.SelectedItem is string picked && !string.IsNullOrWhiteSpace(picked))
                _txtCustomUrl.Text = picked;
        };

        var btnTestCustom = new GoldButton { Text = "测试连接", Variant = GoldButtonVariant.OutlineGold, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
        btnTestCustom.Click += (_, _) => ProbeRefreshRequested?.Invoke();

        customUrlLayout.ColumnCount = 4;
        customUrlLayout.ColumnStyles.Clear();
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        customUrlLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        customUrlLayout.Controls.Add(_txtCustomUrl, 1, 0);
        customUrlLayout.Controls.Add(_cmbSavedServers, 2, 0);
        customUrlLayout.Controls.Add(btnTestCustom, 3, 0);
        _customUrlRow.Controls.Add(customUrlLayout);
        serverLayout.Controls.Add(_customUrlRow);

        serverCard.Controls.Add(serverLayout);
        root.Controls.Add(serverCard);

        // 卡片 3：玩家身份与启动选项
        var playerCard = new GoldCardPanel { Dock = DockStyle.Fill, Height = 128, Margin = new Padding(0, 0, 0, 14), Padding = new Padding(14, 12, 14, 12) };
        var playerLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, BackColor = Color.Transparent };
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        playerLayout.Controls.Add(new Label
        {
            Text = "玩家账号昵称（名字即独立存档 · 自动写入无 BOM player-name.txt）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 146));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        _txtPlayerName = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 5, 8, 0) };
        HubTheme.StyleTextBox(_txtPlayerName);

        var btnGenName = new GoldButton { Text = "生成防撞号昵称", Variant = GoldButtonVariant.SubtleDark, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 6, 1) };
        btnGenName.Click += (_, _) => _txtPlayerName.Text = LauncherConfigService.GenerateCollisionSafePlayerName();

        var btnSaveCfg = new GoldButton { Text = "保存配置", Variant = GoldButtonVariant.OutlineGold, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 0, 1) };
        btnSaveCfg.Click += (_, _) => SavePlayerAndConfigRequested?.Invoke();

        nameRow.Controls.Add(_txtPlayerName, 0, 0);
        nameRow.Controls.Add(btnGenName, 1, 0);
        nameRow.Controls.Add(btnSaveCfg, 2, 0);
        playerLayout.Controls.Add(nameRow, 0, 1);

        var optsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 6, 0, 0)
        };
        _chkAutoSync = new CheckBox { Text = "启动前自动同步", Checked = true, Margin = new Padding(0, 0, 14, 0) };
        _chkAcceptServerMods = new CheckBox { Text = "允许接收服务器下发的 Mod", Checked = true, Margin = new Padding(0, 0, 14, 0) };
        _chkBypassSteam = new CheckBox { Text = "脱离 Steam 依赖", Checked = true, Margin = new Padding(0, 0, 14, 0) };
        _chkUnlockAll = new CheckBox { Text = "解锁全英雄与藏品", Checked = true, Margin = new Padding(0) };
        HubTheme.StyleCheckBox(_chkAutoSync);
        HubTheme.StyleCheckBox(_chkAcceptServerMods);
        HubTheme.StyleCheckBox(_chkBypassSteam);
        HubTheme.StyleCheckBox(_chkUnlockAll);
        optsFlow.Controls.AddRange(new Control[] { _chkAutoSync, _chkAcceptServerMods, _chkBypassSteam, _chkUnlockAll });
        playerLayout.Controls.Add(optsFlow, 0, 2);

        playerCard.Controls.Add(playerLayout);
        root.Controls.Add(playerCard);

        // 底部大号鎏金启动动作条
        var launchCard = new GoldCardPanel { Dock = DockStyle.Fill, Height = 104, Highlighted = true, Margin = new Padding(0), Padding = new Padding(16, 12, 16, 12) };
        var launchLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Color.Transparent };
        launchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        launchLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270));
        launchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        launchLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        _lblActionStatus = new Label
        {
            Text = "就绪：选择服务器与昵称后，点击右侧按钮即可同步并进入大巴扎。",
            Font = HubTheme.FontBody,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        launchLayout.Controls.Add(_lblActionStatus, 0, 0);

        _btnLaunch = new GoldButton
        {
            Text = "同步更新并启动游戏",
            Variant = GoldButtonVariant.PrimaryGold,
            Font = HubTheme.FontLargeAction,
            CornerRadius = 10,
            Dock = DockStyle.Fill,
            Margin = new Padding(12, 0, 0, 0)
        };
        _btnLaunch.Click += (_, _) => LaunchRequested?.Invoke();
        launchLayout.Controls.Add(_btnLaunch, 1, 0);
        launchLayout.SetRowSpan(_btnLaunch, 2);

        _progressBar = new GoldProgressBar { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 12, 6), Value = 0 };
        launchLayout.Controls.Add(_progressBar, 0, 1);

        launchCard.Controls.Add(launchLayout);
        root.Controls.Add(launchCard);

        Controls.Add(root);
        SelectServerMode(ServerMode.CloudTencent, notify: false);
    }

    public ServerMode SelectedMode => _selectedMode;
    public string CustomServerUrl => _txtCustomUrl.Text.Trim();
    public string PlayerNameInput => _txtPlayerName.Text.Trim();
    public bool AutoSyncBeforeLaunch => _chkAutoSync.Checked;
    public bool AcceptServerMods => _chkAcceptServerMods.Checked;
    public bool BypassSteam => _chkBypassSteam.Checked;
    public bool UnlockEverything => _chkUnlockAll.Checked;

    public void SetGameInstallInfo(GameInstallInfo? info)
    {
        if (info == null || string.IsNullOrWhiteSpace(info.GameDirectory))
        {
            _txtGameDir.Text = string.Empty;
            _lblGameStatus.Text = "未自动检测到《The Bazaar》安装目录，请点击右侧「选择目录」手动指定。";
            _lblGameStatus.ForeColor = HubTheme.StatusYellow;
            return;
        }

        _txtGameDir.Text = info.GameDirectory;
        _lblGameStatus.Text = $"{info.BuildSummaryText()}（来源：{info.SourceHint}）";
        _lblGameStatus.ForeColor = info.IsValidGameInstall
            ? (info.IsBepInExReady ? HubTheme.StatusGreen : HubTheme.StatusYellow)
            : HubTheme.StatusRed;
    }

    public void ApplySnapshotToInputs(LauncherConfigSnapshot snapshot, HubPreferences prefs)
    {
        _txtCustomUrl.Text = string.IsNullOrWhiteSpace(prefs.CustomServerUrl)
            ? LauncherConstants.LocalStandaloneUrl
            : prefs.CustomServerUrl;
        _cardCustom.CardSubtitle = _txtCustomUrl.Text;

        string name = string.IsNullOrWhiteSpace(snapshot.EffectivePlayerName)
            ? LauncherConfigService.GenerateCollisionSafePlayerName()
            : snapshot.EffectivePlayerName;
        _txtPlayerName.Text = name;

        _chkAutoSync.Checked = prefs.AutoSyncBeforeLaunch;
        _chkAcceptServerMods.Checked = snapshot.AcceptServerMods;
        _chkBypassSteam.Checked = snapshot.BypassSteam;
        _chkUnlockAll.Checked = snapshot.UnlockEverything;

        UpdateSavedServerDropdown(prefs.SavedCustomServers, _txtCustomUrl.Text);

        SelectServerMode(snapshot.InferServerMode(), notify: false);
    }

    public void UpdateSavedServerDropdown(IEnumerable<string>? servers, string? currentUrl)
    {
        _cmbSavedServers.Items.Clear();
        if (servers != null)
        {
            foreach (string s in servers)
            {
                if (!string.IsNullOrWhiteSpace(s) && !_cmbSavedServers.Items.Contains(s))
                    _cmbSavedServers.Items.Add(s);
            }
        }
        if (!string.IsNullOrWhiteSpace(currentUrl) && !_cmbSavedServers.Items.Contains(currentUrl))
            _cmbSavedServers.Items.Add(currentUrl);
    }

    public void SelectServerMode(ServerMode mode, bool notify)
    {
        _selectedMode = mode;
        _cardCloud.Selected = mode == ServerMode.CloudTencent;
        _cardLocal.Selected = mode == ServerMode.LocalStandalone;
        _cardCustom.Selected = mode == ServerMode.CustomServer;
        _cardOfficial.Selected = mode == ServerMode.PureOfficial;
        _customUrlRow.Visible = mode == ServerMode.CustomServer;

        _btnLaunch.Text = mode switch
        {
            ServerMode.PureOfficial => "以纯净官方原版启动游戏",
            ServerMode.LocalStandalone => "启动本地单机服并进入游戏",
            _ => "同步更新并启动游戏"
        };

        if (notify)
        {
            ServerModeChanged?.Invoke(mode);
        }
    }

    public void UpdateServerProbeResult(ServerMode mode, ServerProbeResult result)
    {
        ServerModeCard? card = mode switch
        {
            ServerMode.CloudTencent => _cardCloud,
            ServerMode.LocalStandalone => _cardLocal,
            ServerMode.CustomServer => _cardCustom,
            _ => null
        };

        if (card == null) return;

        if (!result.IsOnline)
        {
            if (mode == ServerMode.LocalStandalone)
            {
                card.SetProbeStatus(HubTheme.StatusYellow, "未运行（点击启动时自动拉起）");
            }
            else
            {
                card.SetProbeStatus(HubTheme.StatusRed, result.StatusMessage);
            }
            return;
        }

        Color lamp = result.LatencyMs <= 120
            ? HubTheme.StatusGreen
            : (result.LatencyMs <= 300 ? HubTheme.StatusYellow : HubTheme.StatusRed);
        card.SetProbeStatus(lamp, result.StatusMessage);
    }

    public void SetProbingState(bool probing)
    {
        _btnRefreshPing.Enabled = !probing;
        _btnRefreshPing.Text = probing ? "测速中..." : "测速刷新";
    }

    public void SetLaunchBusy(bool busy, string statusText, int progressPercent)
    {
        _btnLaunch.Enabled = !busy;
        _lblActionStatus.Text = statusText;
        _progressBar.Value = progressPercent;
    }
}

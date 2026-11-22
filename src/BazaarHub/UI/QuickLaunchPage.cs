using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 页面 1：「快速启动与选服」（核心启动台）。
/// 采用 Hero 启动主卡 + 4 节点实时测速卡 + 双列环境与身份配置卡结构，
/// 100% 保留原有游戏定位、选服、防撞号昵称、单机托管启停与增量同步启动能力。
/// </summary>
public sealed class QuickLaunchPage : UserControl
{
    private readonly StatusPill _pillHeroMode;
    private readonly StatusPill _pillHeroNet;
    private readonly Label _lblHeroTitle;
    private readonly Label _lblActionStatus;
    private readonly GoldProgressBar _progressBar;
    private readonly GoldButton _btnLaunch;
    private readonly GoldButton _btnLocalServerToggle;

    private readonly ServerModeCard _cardCloud;
    private readonly ServerModeCard _cardLocal;
    private readonly ServerModeCard _cardCustom;
    private readonly ServerModeCard _cardOfficial;
    private readonly GoldButton _btnRefreshPing;
    private readonly Panel _customUrlRow;
    private readonly GoldComboBox _cmbSavedServers;
    private readonly GoldTextBox _txtCustomUrl;

    private readonly StatusPill _pillGameBadge;
    private readonly GoldTextBox _txtGameDir;
    private readonly Label _lblGameStatus;

    private readonly GoldTextBox _txtPlayerName;
    private readonly GoldToggleChip _chkAutoSync;
    private readonly GoldToggleChip _chkAcceptServerMods;
    private readonly GoldToggleChip _chkBypassSteam;
    private readonly GoldToggleChip _chkUnlockAll;

    private readonly Dictionary<ServerMode, ServerProbeResult> _latestProbes = new();
    private ServerMode _selectedMode = ServerMode.CloudTencent;
    private bool _standaloneRunning;

    public event Action? AutoLocateRequested;
    public event Action? BrowseGameDirRequested;
    public event Action? OpenGameDirRequested;
    public event Action<ServerMode>? ServerModeChanged;
    public event Action? ProbeRefreshRequested;
    public event Action? SavePlayerAndConfigRequested;
    public event Action? LaunchRequested;
    public event Action? OpenWorkshopRequested;
    public event Action? ToggleLocalServerRequested;

    public QuickLaunchPage()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        var scroll = new DarkScrollPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 18, 24, 20)
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(root);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ── 1. Hero 核心启动卡 ──────────────────────────────────────────
        var heroCard = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            Highlighted = true,
            UseHeroGradient = true,
            AccentBarColor = HubTheme.Gold,
            Height = 148,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(22, 16, 20, 16)
        };

        var heroGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 296));

        // Hero 左侧信息栈
        var heroLeft = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 20, 0)
        };
        heroLeft.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heroLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        heroLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        heroLeft.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        heroLeft.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var pillsFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        _pillHeroMode = new StatusPill { Height = 24, Margin = new Padding(0, 1, 8, 0) };
        _pillHeroMode.SetState(PillTone.Gold, "远程自建服 (示例)", showDot: false, icon: HubIconKind.ServerCloud);

        _pillHeroNet = new StatusPill { Height = 24, Margin = new Padding(0, 1, 8, 0) };
        _pillHeroNet.SetState(PillTone.Neutral, "正在探测节点...", showDot: true);

        var pillPtrHint = new StatusPill { Height = 24, Margin = new Padding(0, 1, 0, 0) };
        pillPtrHint.SetState(PillTone.Amber, "PTR 测试服分支", showDot: false, icon: HubIconKind.Warning);

        pillsFlow.Controls.Add(_pillHeroMode);
        pillsFlow.Controls.Add(_pillHeroNet);
        pillsFlow.Controls.Add(pillPtrHint);
        heroLeft.Controls.Add(pillsFlow, 0, 0);

        _lblHeroTitle = new Label
        {
            Text = "准备进入大巴扎 · 远程自建服",
            Font = HubTheme.FontHeroTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        heroLeft.Controls.Add(_lblHeroTitle, 0, 1);

        _lblActionStatus = new Label
        {
            Text = "选择节点与昵称后点击右侧主按钮，自动完成增量同步并进入游戏。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0)
        };
        heroLeft.Controls.Add(_lblActionStatus, 0, 2);

        _progressBar = new GoldProgressBar
        {
            Dock = DockStyle.Fill,
            ShowPercentage = true,
            Value = 0,
            Margin = new Padding(0, 6, 0, 2)
        };
        heroLeft.Controls.Add(_progressBar, 0, 3);

        // Hero 右侧主操作 CTA 与辅助动作行
        var heroRight = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        heroRight.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        heroRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        heroRight.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _btnLaunch = new GoldButton
        {
            Text = "同步更新并启动游戏",
            Variant = GoldButtonVariant.PrimaryGold,
            Font = HubTheme.FontLargeAction,
            Icon = HubIconKind.Play,
            CornerRadius = 10,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 8)
        };
        _btnLaunch.Click += (_, _) => LaunchRequested?.Invoke();
        heroRight.Controls.Add(_btnLaunch, 0, 0);

        var subActions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 2)
        };
        subActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        subActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _btnLocalServerToggle = new GoldButton
        {
            Text = "预启单机服",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.ServerLocal,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 5, 0)
        };
        _btnLocalServerToggle.Click += (_, _) => ToggleLocalServerRequested?.Invoke();

        var btnWorkshop = new GoldButton
        {
            Text = "创意工坊 Web",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Workshop,
            Dock = DockStyle.Fill,
            Margin = new Padding(5, 0, 0, 0)
        };
        btnWorkshop.Click += (_, _) => OpenWorkshopRequested?.Invoke();

        subActions.Controls.Add(_btnLocalServerToggle, 0, 0);
        subActions.Controls.Add(btnWorkshop, 1, 0);
        heroRight.Controls.Add(subActions, 0, 1);

        heroGrid.Controls.Add(heroLeft, 0, 0);
        heroGrid.Controls.Add(heroRight, 1, 0);
        heroCard.Controls.Add(heroGrid);
        root.Controls.Add(heroCard);

        // ── 2. 服务器与运行模式选择卡 ────────────────────────────────────
        var serverCard = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 14),
            Padding = new Padding(18, 14, 18, 14)
        };

        var serverLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        serverLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var srvHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        srvHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        srvHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        srvHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var srvTitleFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Height = 34,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        srvTitleFlow.Controls.Add(new Label
        {
            Text = "服务器与运行模式",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 6, 10, 0)
        });
        srvTitleFlow.Controls.Add(new Label
        {
            Text = "点击卡片一键切换节点 · 自动实时探测延迟与健康状态",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextMuted,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0)
        });
        srvHeader.Controls.Add(srvTitleFlow, 0, 0);

        _btnRefreshPing = new GoldButton
        {
            Text = "测速刷新",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.Refresh,
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _btnRefreshPing.Click += (_, _) => ProbeRefreshRequested?.Invoke();
        srvHeader.Controls.Add(_btnRefreshPing, 1, 0);
        serverLayout.Controls.Add(srvHeader);

        var cardsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 114,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        for (int i = 0; i < 4; i++)
        {
            cardsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        _cardCloud = new ServerModeCard(ServerMode.CloudTencent, "远程自建服 (示例)", "请用「自定义服务器」填入你的自建服地址")
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0)
        };
        _cardLocal = new ServerModeCard(ServerMode.LocalStandalone, "本地单机离线版", "127.0.0.1:8080 · 自动托管")
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0)
        };
        _cardCustom = new ServerModeCard(ServerMode.CustomServer, "自定义服务器", "填入好友或自搭私服地址")
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 10, 0)
        };
        _cardOfficial = new ServerModeCard(ServerMode.PureOfficial, "纯净官方原版", "Enabled = false · 直连官方")
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        _cardOfficial.SetProbeStatus(HubTheme.StatusBlue, "停用自建服补丁，直连官方");

        _cardCloud.Click += (_, _) => SelectServerMode(ServerMode.CloudTencent, notify: true);
        _cardLocal.Click += (_, _) => SelectServerMode(ServerMode.LocalStandalone, notify: true);
        _cardCustom.Click += (_, _) => SelectServerMode(ServerMode.CustomServer, notify: true);
        _cardOfficial.Click += (_, _) => SelectServerMode(ServerMode.PureOfficial, notify: true);

        cardsGrid.Controls.Add(_cardCloud, 0, 0);
        cardsGrid.Controls.Add(_cardLocal, 1, 0);
        cardsGrid.Controls.Add(_cardCustom, 2, 0);
        cardsGrid.Controls.Add(_cardOfficial, 3, 0);
        serverLayout.Controls.Add(cardsGrid);

        // 自定义服务器地址折叠行（仅选中自定义服务器时展开）
        _customUrlRow = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            Visible = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 10, 0, 0),
            Margin = new Padding(0)
        };
        var customGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        customGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));

        customGrid.Controls.Add(new Label
        {
            Text = "自定义服地址",
            Font = HubTheme.FontBodyBold,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _txtCustomUrl = new GoldTextBox
        {
            Dock = DockStyle.Fill,
            Text = "http://127.0.0.1:8080",
            PlaceholderText = "例如：http://127.0.0.1:8080",
            Margin = new Padding(0, 1, 8, 1)
        };
        _txtCustomUrl.TextChanged += (_, _) =>
        {
            _cardCustom.CardSubtitle = string.IsNullOrWhiteSpace(_txtCustomUrl.Text)
                ? "请输入 http:// 服务器地址"
                : _txtCustomUrl.Text.Trim();
        };

        _cmbSavedServers = new GoldComboBox
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 8, 2)
        };
        _cmbSavedServers.SelectedIndexChanged += (_, _) =>
        {
            if (_cmbSavedServers.SelectedItem is string picked && !string.IsNullOrWhiteSpace(picked))
            {
                _txtCustomUrl.Text = picked;
            }
        };

        var btnTestCustom = new GoldButton
        {
            Text = "测试连接",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Refresh,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 1)
        };
        btnTestCustom.Click += (_, _) => ProbeRefreshRequested?.Invoke();

        customGrid.Controls.Add(_txtCustomUrl, 1, 0);
        customGrid.Controls.Add(_cmbSavedServers, 2, 0);
        customGrid.Controls.Add(btnTestCustom, 3, 0);
        _customUrlRow.Controls.Add(customGrid);
        serverLayout.Controls.Add(_customUrlRow);

        serverCard.Controls.Add(serverLayout);
        root.Controls.Add(serverCard);

        // ── 3. 下半区双列配置卡（游戏目录 | 玩家身份与启动项）────────────
        var bottomSplit = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 204,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        bottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        bottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));

        // 3A. 左卡：游戏安装目录与环境
        var gameCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 7, 0),
            Padding = new Padding(18, 14, 18, 14)
        };
        var gameLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        gameLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        gameLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

        var gameHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        gameHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        gameHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        gameHeader.Controls.Add(new Label
        {
            Text = "游戏安装目录",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _pillGameBadge = new StatusPill { Height = 22, Margin = new Padding(6, 2, 0, 0) };
        _pillGameBadge.SetState(PillTone.Neutral, "检测中", showDot: true);
        gameHeader.Controls.Add(_pillGameBadge, 1, 0);
        gameLayout.Controls.Add(gameHeader, 0, 0);

        _txtGameDir = new GoldTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            PlaceholderText = "未检测到 TheBazaar.exe，请探测或手动选择...",
            Margin = new Padding(0, 4, 0, 4)
        };
        gameLayout.Controls.Add(_txtGameDir, 0, 1);

        _lblGameStatus = new Label
        {
            Text = "正在自动扫描 Steam 库与本机目录...",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 4, 0, 4)
        };
        gameLayout.Controls.Add(_lblGameStatus, 0, 2);

        var gameBtnRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        gameBtnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        gameBtnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        gameBtnRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

        var btnAuto = new GoldButton
        {
            Text = "自动探测",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Search,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 6, 1)
        };
        btnAuto.Click += (_, _) => AutoLocateRequested?.Invoke();

        var btnBrowse = new GoldButton
        {
            Text = "选择目录",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.Folder,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 6, 1)
        };
        btnBrowse.Click += (_, _) => BrowseGameDirRequested?.Invoke();

        var btnOpen = new GoldButton
        {
            Text = "打开目录",
            Variant = GoldButtonVariant.SubtleDark,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 1)
        };
        btnOpen.Click += (_, _) => OpenGameDirRequested?.Invoke();

        gameBtnRow.Controls.Add(btnAuto, 0, 0);
        gameBtnRow.Controls.Add(btnBrowse, 1, 0);
        gameBtnRow.Controls.Add(btnOpen, 2, 0);
        gameLayout.Controls.Add(gameBtnRow, 0, 3);
        gameCard.Controls.Add(gameLayout);

        // 3B. 右卡：玩家身份与启动选项
        var playerCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(7, 0, 0, 0),
            Padding = new Padding(18, 14, 18, 14)
        };
        var playerLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        playerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        playerLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var playerHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        playerHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        playerHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        playerHeader.Controls.Add(new Label
        {
            Text = "玩家账号昵称",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 4, 8, 0)
        }, 0, 0);
        playerHeader.Controls.Add(new Label
        {
            Text = "名字即独立存档 · 写入 player-name.txt",
            Font = HubTheme.FontCaption,
            ForeColor = HubTheme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true
        }, 1, 0);
        playerLayout.Controls.Add(playerHeader, 0, 0);

        var nameRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 3, 0, 3)
        };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));

        _txtPlayerName = new GoldTextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "输入玩家昵称...",
            Margin = new Padding(0, 1, 6, 1)
        };

        var btnGenName = new GoldButton
        {
            Text = "防撞号昵称",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.Sparkle,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 6, 1)
        };
        btnGenName.Click += (_, _) => _txtPlayerName.Text = LauncherConfigService.GenerateCollisionSafePlayerName();

        var btnSaveCfg = new GoldButton
        {
            Text = "保存配置",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Save,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 0, 1)
        };
        btnSaveCfg.Click += (_, _) => SavePlayerAndConfigRequested?.Invoke();

        nameRow.Controls.Add(_txtPlayerName, 0, 0);
        nameRow.Controls.Add(btnGenName, 1, 0);
        nameRow.Controls.Add(btnSaveCfg, 2, 0);
        playerLayout.Controls.Add(nameRow, 0, 1);

        // 2×2 现代胶囊开关网格（零白框、零截断）
        var optsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 8, 0, 0)
        };
        optsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        optsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        optsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        _chkAutoSync = new GoldToggleChip { Text = "启动前自动增量同步", Checked = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 5, 4) };
        _chkAcceptServerMods = new GoldToggleChip { Text = "允许接收服务器 Mod", Checked = true, Dock = DockStyle.Fill, Margin = new Padding(5, 2, 0, 4) };
        _chkBypassSteam = new GoldToggleChip { Text = "脱离 Steam 启动依赖", Checked = true, Dock = DockStyle.Fill, Margin = new Padding(0, 4, 5, 2) };
        _chkUnlockAll = new GoldToggleChip { Text = "解锁全英雄与藏品", Checked = true, Dock = DockStyle.Fill, Margin = new Padding(5, 4, 0, 2) };

        optsGrid.Controls.Add(_chkAutoSync, 0, 0);
        optsGrid.Controls.Add(_chkAcceptServerMods, 1, 0);
        optsGrid.Controls.Add(_chkBypassSteam, 0, 1);
        optsGrid.Controls.Add(_chkUnlockAll, 1, 1);
        playerLayout.Controls.Add(optsGrid, 0, 2);

        playerCard.Controls.Add(playerLayout);

        bottomSplit.Controls.Add(gameCard, 0, 0);
        bottomSplit.Controls.Add(playerCard, 1, 0);
        root.Controls.Add(bottomSplit);

        scroll.Controls.Add(root);
        Controls.Add(scroll);

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
            _lblGameStatus.Text = "未自动检测到《The Bazaar》安装目录，请点击下方「选择目录」手动指定。";
            _lblGameStatus.ForeColor = HubTheme.StatusYellow;
            _pillGameBadge.SetState(PillTone.Amber, "未定位", showDot: true);
            return;
        }

        _txtGameDir.Text = info.GameDirectory;
        _lblGameStatus.Text = $"{info.BuildSummaryText()}（来源：{info.SourceHint}）";

        if (!info.IsValidGameInstall)
        {
            _lblGameStatus.ForeColor = HubTheme.StatusRed;
            _pillGameBadge.SetState(PillTone.Red, "目录无效", showDot: true);
        }
        else if (info.IsBepInExReady)
        {
            _lblGameStatus.ForeColor = HubTheme.StatusGreen;
            string ver = string.IsNullOrWhiteSpace(info.LauncherPluginVersion) ? "插件就绪" : $"v{info.LauncherPluginVersion}";
            _pillGameBadge.SetState(PillTone.Green, ver, showDot: true);
        }
        else
        {
            _lblGameStatus.ForeColor = HubTheme.StatusYellow;
            _pillGameBadge.SetState(PillTone.Amber, "缺少插件", showDot: true);
        }
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
                {
                    _cmbSavedServers.Items.Add(s);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(currentUrl) && !_cmbSavedServers.Items.Contains(currentUrl))
        {
            _cmbSavedServers.Items.Add(currentUrl);
        }
        if (_cmbSavedServers.Items.Count > 0 && _cmbSavedServers.SelectedIndex < 0)
        {
            _cmbSavedServers.SelectedIndex = 0;
        }
    }

    public void SelectServerMode(ServerMode mode, bool notify)
    {
        _selectedMode = mode;
        _cardCloud.Selected = mode == ServerMode.CloudTencent;
        _cardLocal.Selected = mode == ServerMode.LocalStandalone;
        _cardCustom.Selected = mode == ServerMode.CustomServer;
        _cardOfficial.Selected = mode == ServerMode.PureOfficial;
        _customUrlRow.Visible = mode == ServerMode.CustomServer;

        (string modeLabel, HubIconKind icon, string heroTitle, string launchText) = mode switch
        {
            ServerMode.LocalStandalone => (
                "本地单机离线版",
                HubIconKind.ServerLocal,
                "准备启动本地单机离线版",
                "启动本地单机服并进入游戏"),
            ServerMode.CustomServer => (
                "自定义服务器",
                HubIconKind.ServerCustom,
                "准备连接自定义自建服务器",
                "同步更新并启动游戏"),
            ServerMode.PureOfficial => (
                "纯净官方原版",
                HubIconKind.Official,
                "准备以纯净官方原版启动",
                "以纯净官方原版启动游戏"),
            _ => (
                "远程自建服 (示例)",
                HubIconKind.ServerCloud,
                "准备进入大巴扎 · 远程自建服",
                "同步更新并启动游戏")
        };

        _pillHeroMode.SetState(mode == ServerMode.PureOfficial ? PillTone.Blue : PillTone.Gold, modeLabel, showDot: false, icon: icon);
        _lblHeroTitle.Text = heroTitle;
        _btnLaunch.Text = launchText;

        RefreshHeroNetPill();

        if (notify)
        {
            ServerModeChanged?.Invoke(mode);
        }
    }

    public void UpdateServerProbeResult(ServerMode mode, ServerProbeResult result)
    {
        _latestProbes[mode] = result;

        ServerModeCard? card = mode switch
        {
            ServerMode.CloudTencent => _cardCloud,
            ServerMode.LocalStandalone => _cardLocal,
            ServerMode.CustomServer => _cardCustom,
            _ => null
        };

        if (card != null)
        {
            if (!result.IsOnline)
            {
                if (mode == ServerMode.LocalStandalone)
                {
                    card.SetProbeStatus(HubTheme.StatusYellow, "待机中（启动时自动拉起）");
                }
                else
                {
                    card.SetProbeStatus(HubTheme.StatusRed, result.StatusMessage);
                }
            }
            else
            {
                Color lamp = result.LatencyMs <= 120
                    ? HubTheme.StatusGreen
                    : (result.LatencyMs <= 300 ? HubTheme.StatusYellow : HubTheme.StatusRed);
                card.SetProbeStatus(lamp, result.StatusMessage);
            }
        }

        if (mode == _selectedMode)
        {
            RefreshHeroNetPill();
        }
    }

    public void SetStandaloneServerRunning(bool running)
    {
        _standaloneRunning = running;
        if (running)
        {
            _btnLocalServerToggle.Text = "停止单机服";
            _btnLocalServerToggle.Variant = GoldButtonVariant.DangerRed;
            _btnLocalServerToggle.Icon = HubIconKind.Stop;
        }
        else
        {
            _btnLocalServerToggle.Text = "预启单机服";
            _btnLocalServerToggle.Variant = GoldButtonVariant.SubtleDark;
            _btnLocalServerToggle.Icon = HubIconKind.ServerLocal;
        }
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

    private void RefreshHeroNetPill()
    {
        if (_selectedMode == ServerMode.PureOfficial)
        {
            _pillHeroNet.SetState(PillTone.Blue, "直连官方 Steam 客户端", showDot: true);
            return;
        }

        if (!_latestProbes.TryGetValue(_selectedMode, out ServerProbeResult? probe))
        {
            _pillHeroNet.SetState(PillTone.Neutral, "等待测速...", showDot: true);
            return;
        }

        if (!probe.IsOnline)
        {
            if (_selectedMode == ServerMode.LocalStandalone)
            {
                _pillHeroNet.SetState(
                    _standaloneRunning ? PillTone.Green : PillTone.Amber,
                    _standaloneRunning ? "托管单机服运行中" : "单机服未运行（自动拉起）",
                    showDot: true);
            }
            else
            {
                _pillHeroNet.SetState(PillTone.Red, "节点离线 / 超时", showDot: true);
            }
            return;
        }

        string onlineInfo = probe.OnlinePlayers.HasValue
            ? $"在线 {probe.LatencyMs}ms · {probe.OnlinePlayers.Value} 人"
            : $"节点在线 {probe.LatencyMs}ms";
        PillTone tone = probe.LatencyMs <= 120 ? PillTone.Green : (probe.LatencyMs <= 300 ? PillTone.Amber : PillTone.Red);
        _pillHeroNet.SetState(tone, onlineInfo, showDot: true);
    }
}

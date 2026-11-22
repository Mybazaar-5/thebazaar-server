using System.Drawing;
using BazaarHub.Models;

namespace BazaarHub.UI;

/// <summary>
/// 页面 4：「自检与诊断日志」。
/// 顶部展示 4 项实时环境体检状态卡，中部提供快捷测速/日志目录/复制清空操作栏，
/// 下方提供深色等宽终端日志视图与一键打包完整排错诊断 ZIP 能力。
/// </summary>
public sealed class DiagnosticsPage : UserControl
{
    private readonly StatusPill _pillCheckGame;
    private readonly Label _lblCheckGameDetail;
    private readonly StatusPill _pillCheckBep;
    private readonly Label _lblCheckBepDetail;
    private readonly StatusPill _pillCheckServer;
    private readonly Label _lblCheckServerDetail;
    private readonly StatusPill _pillCheckStandalone;
    private readonly Label _lblCheckStandaloneDetail;

    private readonly StatusPill _pillLogCount;
    private readonly RichTextBox _logBox;
    private readonly GoldButton _btnExportZip;
    private readonly List<string> _logBuffer = new();

    public event Action? OpenGameDirRequested;
    public event Action? OpenBepInExLogDirRequested;
    public event Action? OpenUnityLogDirRequested;
    public event Action? NetworkTestRequested;
    public event Action? ExportDiagnosticsRequested;

    public DiagnosticsPage()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        Padding = new Padding(24, 18, 24, 18);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(root);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));   // 标题 + 一键导出 ZIP
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));  // 4 张自检体检卡（充足高度防底部文字裁切）
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 自适应快捷工具栏（防截断）
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 深色等宽日志终端

        // ── 1. 顶部标题条与主操作 ────────────────────────────────────────
        _btnExportZip = new GoldButton
        {
            Text = "导出排错诊断包 (ZIP)",
            Variant = GoldButtonVariant.PrimaryGold,
            Icon = HubIconKind.Download,
            Size = new Size(188, 34),
            Margin = new Padding(0)
        };
        _btnExportZip.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();

        var header = new SectionHeaderBar(
            "自检与诊断日志",
            "实时监控游戏本体、插件、服务器连通与单机托管日志；遇到异常时一键打包完整排错诊断 ZIP。",
            _btnExportZip);
        root.Controls.Add(header, 0, 0);

        // ── 2. 4 项环境自检状态卡片 ──────────────────────────────────────
        var healthGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        for (int i = 0; i < 4; i++)
        {
            healthGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }

        (GoldCardPanel hc1, _pillCheckGame, _lblCheckGameDetail) = CreateHealthCard("游戏本体目录", "等待检测", "扫描 TheBazaar.exe 中...");
        hc1.Margin = new Padding(0, 0, 10, 0);

        (GoldCardPanel hc2, _pillCheckBep, _lblCheckBepDetail) = CreateHealthCard("BepInEx 与插件", "等待检测", "检查 winhttp 与插件版本...");
        hc2.Margin = new Padding(0, 0, 10, 0);

        (GoldCardPanel hc3, _pillCheckServer, _lblCheckServerDetail) = CreateHealthCard("当前目标服务端", "测速中", "正在探测 /api/ping 与 /healthz...");
        hc3.Margin = new Padding(0, 0, 10, 0);

        (GoldCardPanel hc4, _pillCheckStandalone, _lblCheckStandaloneDetail) = CreateHealthCard("本地单机托管服", "待机中", "未拉起本地托管进程");
        hc4.Margin = new Padding(0);

        healthGrid.Controls.Add(hc1, 0, 0);
        healthGrid.Controls.Add(hc2, 1, 0);
        healthGrid.Controls.Add(hc3, 2, 0);
        healthGrid.Controls.Add(hc4, 3, 0);
        root.Controls.Add(healthGrid, 0, 1);

        // ── 3. 自适应快捷操作栏（WrapContents = true 保证绝不截断）────────
        var barCard = new GoldCardPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 10)
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        var btnNetTest = new GoldButton { Text = "全节点网络测速", Variant = GoldButtonVariant.OutlineGold, Icon = HubIconKind.Refresh, Size = new Size(146, 32), Margin = new Padding(0, 2, 8, 2) };
        btnNetTest.Click += (_, _) => NetworkTestRequested?.Invoke();

        var btnOpenGame = new GoldButton { Text = "打开游戏目录", Variant = GoldButtonVariant.SubtleDark, Icon = HubIconKind.Folder, Size = new Size(134, 32), Margin = new Padding(0, 2, 8, 2) };
        btnOpenGame.Click += (_, _) => OpenGameDirRequested?.Invoke();

        var btnOpenBep = new GoldButton { Text = "BepInEx 日志", Variant = GoldButtonVariant.SubtleDark, Icon = HubIconKind.Folder, Size = new Size(136, 32), Margin = new Padding(0, 2, 8, 2) };
        btnOpenBep.Click += (_, _) => OpenBepInExLogDirRequested?.Invoke();

        var btnOpenUnity = new GoldButton { Text = "Player.log 目录", Variant = GoldButtonVariant.SubtleDark, Icon = HubIconKind.Folder, Size = new Size(146, 32), Margin = new Padding(0, 2, 8, 2) };
        btnOpenUnity.Click += (_, _) => OpenUnityLogDirRequested?.Invoke();

        var btnCopyLogs = new GoldButton { Text = "复制全部日志", Variant = GoldButtonVariant.SubtleDark, Icon = HubIconKind.Copy, Size = new Size(134, 32), Margin = new Padding(0, 2, 8, 2) };
        btnCopyLogs.Click += (_, _) => CopyLogsToClipboard();

        var btnClear = new GoldButton { Text = "清空视图", Variant = GoldButtonVariant.Ghost, Size = new Size(88, 32), Margin = new Padding(0, 2, 0, 2) };

        flow.Controls.AddRange(new Control[] { btnNetTest, btnOpenGame, btnOpenBep, btnOpenUnity, btnCopyLogs, btnClear });
        barCard.Controls.Add(flow);
        root.Controls.Add(barCard, 0, 2);

        // ── 4. 深色等宽日志终端卡片 ──────────────────────────────────────
        var logCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 14),
            Margin = new Padding(0)
        };

        var logHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        logHeader.Controls.Add(new Label
        {
            Text = "实时终端日志流（启动器 / 增量同步 / 本地单机服托管）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _pillLogCount = new StatusPill { Height = 22, Margin = new Padding(8, 3, 0, 0) };
        _pillLogCount.SetState(PillTone.Neutral, "0 条记录", showDot: false, icon: HubIconKind.Diagnostics);
        logHeader.Controls.Add(_pillLogCount, 1, 0);

        var terminalFrame = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            CornerRadius = 8,
            CardBackColor = HubTheme.BgInput,
            CardTopColor = HubTheme.BgInput,
            Padding = new Padding(10, 8, 6, 8),
            Margin = new Padding(0)
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = HubTheme.BgInput,
            ForeColor = HubTheme.TextPrimary,
            Font = HubTheme.FontMono,
            BorderStyle = BorderStyle.None,
            ReadOnly = true,
            WordWrap = true,
            HideSelection = false
        };
        HubTheme.ApplyDarkScrollBar(_logBox);

        btnClear.Click += (_, _) =>
        {
            _logBox.Clear();
            _pillLogCount.SetState(PillTone.Neutral, "视图已清空", showDot: false, icon: HubIconKind.Diagnostics);
        };

        terminalFrame.Controls.Add(_logBox);
        logCard.Controls.Add(terminalFrame);
        logCard.Controls.Add(logHeader);
        root.Controls.Add(logCard, 0, 3);

        Controls.Add(root);
    }

    public void UpdateHealthCards(
        GameInstallInfo? install,
        string serverStatusTitle,
        string serverStatusDetail,
        PillTone serverTone,
        bool standaloneRunning,
        string? standaloneDetail = null)
    {
        if (install == null || !install.IsValidGameInstall)
        {
            _pillCheckGame.SetState(PillTone.Amber, "未定位目录", showDot: true);
            _lblCheckGameDetail.Text = "请在首页自动探测或手动选择 TheBazaar.exe 所在目录";
            _pillCheckBep.SetState(PillTone.Neutral, "待检测", showDot: true);
            _lblCheckBepDetail.Text = "定位游戏目录后自动校验插件完整性";
        }
        else
        {
            _pillCheckGame.SetState(PillTone.Green, "本体有效", showDot: true);
            _lblCheckGameDetail.Text = install.GameDirectory;

            if (install.IsBepInExReady)
            {
                string ver = string.IsNullOrWhiteSpace(install.LauncherPluginVersion) ? "已就绪" : $"v{install.LauncherPluginVersion}";
                _pillCheckBep.SetState(PillTone.Green, ver, showDot: true);
                _lblCheckBepDetail.Text = "winhttp.dll + BepInEx + BazaarLauncher 均就绪";
            }
            else
            {
                _pillCheckBep.SetState(PillTone.Amber, "缺少组件", showDot: true);
                _lblCheckBepDetail.Text = "缺少 winhttp.dll、BepInEx 或 BazaarLauncher.dll";
            }
        }

        _pillCheckServer.SetState(serverTone, serverStatusTitle, showDot: true);
        _lblCheckServerDetail.Text = serverStatusDetail;

        if (standaloneRunning)
        {
            _pillCheckStandalone.SetState(PillTone.Green, "运行中", showDot: true);
            _lblCheckStandaloneDetail.Text = string.IsNullOrWhiteSpace(standaloneDetail)
                ? "本机 127.0.0.1:8080 托管运行中"
                : standaloneDetail;
        }
        else
        {
            _pillCheckStandalone.SetState(PillTone.Neutral, "未启动", showDot: true);
            _lblCheckStandaloneDetail.Text = "切至单机模式启动游戏时自动拉起";
        }
    }

    public IReadOnlyList<string> GetAllLogsSnapshot()
    {
        lock (_logBuffer)
        {
            return _logBuffer.ToList();
        }
    }

    public void SetExportBusy(bool busy)
    {
        _btnExportZip.Enabled = !busy;
        _btnExportZip.Text = busy ? "正在打包诊断 ZIP..." : "导出排错诊断包 (ZIP)";
    }

    public void AppendLog(string message)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        int totalCount;
        lock (_logBuffer)
        {
            _logBuffer.Add(stamped);
            if (_logBuffer.Count > 2000)
            {
                _logBuffer.RemoveAt(0);
            }
            totalCount = _logBuffer.Count;
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLogOnUiThread(stamped, totalCount));
        }
        else
        {
            AppendLogOnUiThread(stamped, totalCount);
        }
    }

    private void AppendLogOnUiThread(string line, int totalCount)
    {
        if (IsDisposed) return;

        Color color = HubTheme.TextPrimary;
        if (line.Contains("[错误]", StringComparison.Ordinal) || line.Contains("/ERR]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusRed;
        }
        else if (line.Contains("[警告]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusYellow;
        }
        else if (line.Contains("[成功]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusGreen;
        }
        else if (line.Contains("[同步]", StringComparison.Ordinal) || line.Contains("[性能调优]", StringComparison.Ordinal))
        {
            color = HubTheme.GoldLight;
        }
        else if (line.Contains("[单机服]", StringComparison.Ordinal) || line.Contains("[启动]", StringComparison.Ordinal) || line.Contains("[托管]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusBlue;
        }
        else if (line.Contains("[测速]", StringComparison.Ordinal) || line.Contains("[工坊]", StringComparison.Ordinal) || line.Contains("[社区 Mod]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusPurple;
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionColor = _logBox.ForeColor;
        _logBox.ScrollToCaret();

        _pillLogCount.SetState(PillTone.Neutral, $"{totalCount} 条记录", showDot: false, icon: HubIconKind.Diagnostics);
    }

    private void CopyLogsToClipboard()
    {
        string text = _logBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            Clipboard.SetText(text);
            AppendLog("[信息] 已将当前终端日志复制到剪贴板。");
        }
        catch
        {
            // 忽略剪贴板占用异常
        }
    }

    private static (GoldCardPanel Card, StatusPill Pill, Label Detail) CreateHealthCard(
        string title, string initialBadge, string initialDetail)
    {
        var card = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 10)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = title,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var pillWrap = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 0)
        };
        var pill = new StatusPill { Height = 24, Margin = new Padding(0) };
        pill.SetState(PillTone.Neutral, initialBadge, showDot: true);
        pillWrap.Controls.Add(pill);
        layout.Controls.Add(pillWrap, 0, 1);

        var detail = new Label
        {
            Text = initialDetail,
            Font = HubTheme.FontCaption,
            ForeColor = HubTheme.TextMuted,
            BackColor = Color.Transparent,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 2, 0, 0),
            AutoEllipsis = true
        };
        layout.Controls.Add(detail, 0, 2);

        card.Controls.Add(layout);
        return (card, pill, detail);
    }
}

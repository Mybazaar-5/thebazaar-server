using System.Drawing;

namespace BazaarHub.UI;

/// <summary>
/// 标签页 4：「日志与排错」。
/// 提供实时状态日志流、快捷打开游戏/BepInEx/Unity 日志目录、一键网络测速与一键导出排错诊断 ZIP 包。
/// </summary>
public sealed class DiagnosticsPage : UserControl
{
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
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        Padding = new Padding(22, 16, 22, 16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 标题
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "日志与一键排错诊断",
            Font = HubTheme.FontPageTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Location = new Point(0, 0)
        });
        header.Controls.Add(new Label
        {
            Text = "实时查看启动器、增量同步与本地单机服状态日志；遇到异常时可一键打包完整排错诊断 ZIP 分享排查。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 30)
        });
        root.Controls.Add(header, 0, 0);

        // 工具栏卡片
        var barCard = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10), Margin = new Padding(0, 0, 0, 10) };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent
        };

        _btnExportZip = new GoldButton { Text = "一键导出排错诊断包 (ZIP)", Variant = GoldButtonVariant.PrimaryGold, Size = new Size(198, 36), Margin = new Padding(0, 0, 10, 0) };
        _btnExportZip.Click += (_, _) => ExportDiagnosticsRequested?.Invoke();

        var btnNetTest = new GoldButton { Text = "全节点网络测速", Variant = GoldButtonVariant.OutlineGold, Size = new Size(124, 36), Margin = new Padding(0, 0, 8, 0) };
        btnNetTest.Click += (_, _) => NetworkTestRequested?.Invoke();

        var btnOpenGame = new GoldButton { Text = "打开游戏目录", Variant = GoldButtonVariant.SubtleDark, Size = new Size(108, 36), Margin = new Padding(0, 0, 8, 0) };
        btnOpenGame.Click += (_, _) => OpenGameDirRequested?.Invoke();

        var btnOpenBep = new GoldButton { Text = "BepInEx 日志", Variant = GoldButtonVariant.SubtleDark, Size = new Size(112, 36), Margin = new Padding(0, 0, 8, 0) };
        btnOpenBep.Click += (_, _) => OpenBepInExLogDirRequested?.Invoke();

        var btnOpenUnity = new GoldButton { Text = "Player.log 目录", Variant = GoldButtonVariant.SubtleDark, Size = new Size(118, 36), Margin = new Padding(0, 0, 8, 0) };
        btnOpenUnity.Click += (_, _) => OpenUnityLogDirRequested?.Invoke();

        var btnClear = new GoldButton { Text = "清空视图", Variant = GoldButtonVariant.SubtleDark, Size = new Size(82, 36), Margin = new Padding(0) };

        flow.Controls.AddRange(new Control[] { _btnExportZip, btnNetTest, btnOpenGame, btnOpenBep, btnOpenUnity, btnClear });
        barCard.Controls.Add(flow);
        root.Controls.Add(barCard, 0, 1);

        // 日志终端卡片
        var logCard = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0) };
        var logTitle = new Label
        {
            Text = "实时状态日志流",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Top,
            Height = 24
        };

        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = HubTheme.BgInput,
            ForeColor = HubTheme.TextPrimary,
            Font = HubTheme.FontMono,
            BorderStyle = BorderStyle.FixedSingle,
            ReadOnly = true,
            WordWrap = true,
            HideSelection = false
        };

        btnClear.Click += (_, _) => _logBox.Clear();

        logCard.Controls.Add(_logBox);
        logCard.Controls.Add(logTitle);
        root.Controls.Add(logCard, 0, 2);

        Controls.Add(root);
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
        _btnExportZip.Text = busy ? "正在打包诊断 ZIP..." : "一键导出排错诊断包 (ZIP)";
    }

    public void AppendLog(string message)
    {
        string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        lock (_logBuffer)
        {
            _logBuffer.Add(stamped);
            if (_logBuffer.Count > 2000)
            {
                _logBuffer.RemoveAt(0);
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLogOnUiThread(stamped));
        }
        else
        {
            AppendLogOnUiThread(stamped);
        }
    }

    private void AppendLogOnUiThread(string line)
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
        else if (line.Contains("[单机服]", StringComparison.Ordinal) || line.Contains("[启动]", StringComparison.Ordinal))
        {
            color = HubTheme.StatusBlue;
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line + Environment.NewLine);
        _logBox.SelectionColor = _logBox.ForeColor;
        _logBox.ScrollToCaret();
    }
}

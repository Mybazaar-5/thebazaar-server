using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 标签页 2：「更新与 Mod 同步」。
/// 展示服务端已发布插件版本、外挂中文词典版本、启用 Mod 列表与 PNG 资产数量，支持手动检查差异与增量同步。
/// </summary>
public sealed class UpdateSyncPage : UserControl
{
    private readonly Label _lblPluginVer;
    private readonly Label _lblL10nVer;
    private readonly Label _lblModSummary;
    private readonly Label _lblSyncSummary;
    private readonly GoldProgressBar _progressBar;
    private readonly GoldButton _btnSyncNow;
    private readonly GoldButton _btnCheckOnly;
    private readonly DataGridView _gridFiles;
    private readonly DataGridView _gridMods;

    public event Action<bool>? SyncRequested; // true = 下载同步, false = 仅检查
    public event Action? OpenModCacheRequested;

    public UpdateSyncPage()
    {
        BackColor = HubTheme.BgWindow;
        Dock = DockStyle.Fill;
        Padding = new Padding(22, 16, 22, 16);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));  // 标题
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));  // 三张概览卡片
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));  // 操作栏 + 进度条
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));   // 文件/插件/资产明细
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));   // Mod 清单

        // 标题
        var header = new Panel { Dock = DockStyle.Fill };
        header.Controls.Add(new Label
        {
            Text = "更新与 Mod 增量同步",
            Font = HubTheme.FontPageTitle,
            ForeColor = HubTheme.GoldLight,
            AutoSize = true,
            Location = new Point(0, 0)
        });
        header.Controls.Add(new Label
        {
            Text = "基于 SHA-256 增量哈希比对，先写 .tmp 严格校验再原子替换；断网时自动优雅降级使用本地缓存。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 30)
        });
        root.Controls.Add(header, 0, 0);

        // 三张统计概览卡
        var statsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 8) };
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        (GoldCardPanel c1, _lblPluginVer) = CreateMetricCard("服务端插件版本", "待检查", "BepInEx\\plugins\\BazaarLauncher.dll");
        c1.Margin = new Padding(0, 0, 10, 0);
        (GoldCardPanel c2, _lblL10nVer) = CreateMetricCard("外挂汉化词典版本", "待检查", "BepInEx\\config\\custom_zh_overlay.json");
        c2.Margin = new Padding(0, 0, 10, 0);
        (GoldCardPanel c3, _lblModSummary) = CreateMetricCard("启用 Mod 与 PNG 资产", "待检查", "BepInEx\\config\\BazaarModCache\\");
        c3.Margin = new Padding(0);

        statsGrid.Controls.Add(c1, 0, 0);
        statsGrid.Controls.Add(c2, 1, 0);
        statsGrid.Controls.Add(c3, 2, 0);
        root.Controls.Add(statsGrid, 0, 1);

        // 工具栏
        var toolbarCard = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 8), Margin = new Padding(0, 0, 0, 8) };
        var barLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, BackColor = Color.Transparent };
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));

        _btnSyncNow = new GoldButton { Text = "立即检查并同步", Variant = GoldButtonVariant.PrimaryGold, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _btnSyncNow.Click += (_, _) => SyncRequested?.Invoke(true);

        _btnCheckOnly = new GoldButton { Text = "仅检查差异", Variant = GoldButtonVariant.OutlineGold, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0) };
        _btnCheckOnly.Click += (_, _) => SyncRequested?.Invoke(false);

        var btnOpenCache = new GoldButton { Text = "打开 Mod 缓存", Variant = GoldButtonVariant.SubtleDark, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0) };
        btnOpenCache.Click += (_, _) => OpenModCacheRequested?.Invoke();

        _lblSyncSummary = new Label
        {
            Text = "点击「立即检查并同步」直连当前服务器获取最新清单。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new GoldProgressBar { Dock = DockStyle.Fill, Margin = new Padding(8, 14, 0, 14), Value = 0 };

        barLayout.Controls.Add(_btnSyncNow, 0, 0);
        barLayout.Controls.Add(_btnCheckOnly, 1, 0);
        barLayout.Controls.Add(btnOpenCache, 2, 0);
        barLayout.Controls.Add(_lblSyncSummary, 3, 0);
        barLayout.Controls.Add(_progressBar, 4, 0);
        toolbarCard.Controls.Add(barLayout);
        root.Controls.Add(toolbarCard, 0, 2);

        // 文件增量同步明细表
        var filesCard = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0, 0, 0, 8) };
        var filesHeader = new Label
        {
            Text = "同步文件明细（插件 / 外挂汉化词典 / Mod 贴图资产）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Top,
            Height = 24
        };
        _gridFiles = new DataGridView { Dock = DockStyle.Fill };
        HubTheme.StyleDataGridView(_gridFiles);
        _gridFiles.Columns.Add("Category", "类别");
        _gridFiles.Columns.Add("Name", "项目名称");
        _gridFiles.Columns.Add("Target", "本地落盘路径");
        _gridFiles.Columns.Add("Hash", "远端 SHA-256");
        _gridFiles.Columns.Add("Action", "状态");
        _gridFiles.Columns.Add("Detail", "详情");
        _gridFiles.Columns[0].FillWeight = 12;
        _gridFiles.Columns[1].FillWeight = 22;
        _gridFiles.Columns[2].FillWeight = 28;
        _gridFiles.Columns[3].FillWeight = 12;
        _gridFiles.Columns[4].FillWeight = 10;
        _gridFiles.Columns[5].FillWeight = 16;

        filesCard.Controls.Add(_gridFiles);
        filesCard.Controls.Add(filesHeader);
        root.Controls.Add(filesCard, 0, 3);

        // 服务器启用 Mod 列表
        var modsCard = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 12), Margin = new Padding(0) };
        var modsHeader = new Label
        {
            Text = "服务器启用 Mod 扩展包列表",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Top,
            Height = 24
        };
        _gridMods = new DataGridView { Dock = DockStyle.Fill };
        HubTheme.StyleDataGridView(_gridMods);
        _gridMods.Columns.Add("Id", "Mod ID");
        _gridMods.Columns.Add("Name", "中文名称");
        _gridMods.Columns.Add("Version", "版本");
        _gridMods.Columns.Add("Scope", "作用域");
        _gridMods.Columns.Add("Assets", "PNG 资产数");
        _gridMods.Columns.Add("Desc", "说明");
        _gridMods.Columns[0].FillWeight = 18;
        _gridMods.Columns[1].FillWeight = 22;
        _gridMods.Columns[2].FillWeight = 10;
        _gridMods.Columns[3].FillWeight = 15;
        _gridMods.Columns[4].FillWeight = 10;
        _gridMods.Columns[5].FillWeight = 25;

        modsCard.Controls.Add(_gridMods);
        modsCard.Controls.Add(modsHeader);
        root.Controls.Add(modsCard, 0, 4);

        Controls.Add(root);
    }

    public void SetBusy(bool busy, string message, int percent)
    {
        _btnSyncNow.Enabled = !busy;
        _btnCheckOnly.Enabled = !busy;
        _lblSyncSummary.Text = message;
        _lblSyncSummary.ForeColor = busy ? HubTheme.GoldLight : HubTheme.TextSecondary;
        _progressBar.Value = percent;
    }

    public void RenderReport(UpdateSyncReport report)
    {
        string pluginVer = !string.IsNullOrWhiteSpace(report.ClientManifest?.LauncherVersion)
            ? $"v{report.ClientManifest.LauncherVersion}"
            : (report.DegradedOffline ? "离线缓存" : "服务端未发布");
        _lblPluginVer.Text = pluginVer;

        string l10nVer = !string.IsNullOrWhiteSpace(report.LocalizationRevision)
            ? report.LocalizationRevision
            : (report.DegradedOffline ? "离线缓存" : "服务端未发布");
        _lblL10nVer.Text = l10nVer;

        int modCount = report.ModsManifest?.Mods.Count ?? 0;
        int assetCount = report.ModsManifest?.TotalAssetCount ?? 0;
        _lblModSummary.Text = report.ModsManifest != null
            ? $"{modCount} 个 Mod · {assetCount} 张资产"
            : (report.DegradedOffline ? "离线缓存" : "0 个 Mod");

        _lblSyncSummary.Text = report.SummaryMessage;
        _lblSyncSummary.ForeColor = report.DegradedOffline
            ? HubTheme.StatusYellow
            : (report.Success ? HubTheme.StatusGreen : HubTheme.StatusRed);
        _progressBar.Value = 100;

        _gridFiles.Rows.Clear();
        foreach (SyncItemStatus item in report.Items)
        {
            string shortSha = item.RemoteSha256.Length >= 10 ? item.RemoteSha256[..10] : item.RemoteSha256;
            int idx = _gridFiles.Rows.Add(
                item.Category,
                item.DisplayName,
                item.TargetRelativePath,
                shortSha,
                item.Action,
                item.Detail);

            Color actionColor = item.Action switch
            {
                "已更新" or "已是最新" => HubTheme.StatusGreen,
                "待更新" or "待下载" => HubTheme.GoldLight,
                "失败" => HubTheme.StatusRed,
                _ => HubTheme.TextPrimary
            };
            _gridFiles.Rows[idx].Cells[4].Style.ForeColor = actionColor;
        }

        _gridMods.Rows.Clear();
        if (report.ModsManifest != null)
        {
            foreach (RemoteModEntry mod in report.ModsManifest.Mods)
            {
                int modAssets = mod.Assets.Count > 0
                    ? mod.Assets.Count
                    : report.ModsManifest.Assets.Count(a => string.Equals(a.ModId, mod.Id, StringComparison.OrdinalIgnoreCase));

                _gridMods.Rows.Add(
                    mod.Id,
                    mod.Name,
                    mod.Version,
                    mod.Scope,
                    $"{modAssets} 张",
                    string.IsNullOrWhiteSpace(mod.Description) ? "-" : mod.Description);
            }
        }
    }

    private static (GoldCardPanel Card, Label ValueLabel) CreateMetricCard(string title, string initialValue, string hint)
    {
        var card = new GoldCardPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 10) };
        var lblTitle = new Label
        {
            Text = title,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Top,
            Height = 18
        };
        var lblValue = new Label
        {
            Text = initialValue,
            Font = HubTheme.FontMetric,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Top,
            Height = 28
        };
        var lblHint = new Label
        {
            Text = hint,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextMuted,
            Dock = DockStyle.Bottom,
            Height = 16
        };
        card.Controls.Add(lblHint);
        card.Controls.Add(lblValue);
        card.Controls.Add(lblTitle);
        return (card, lblValue);
    }
}

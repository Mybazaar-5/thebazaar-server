using System.Drawing;
using BazaarHub.Models;
using BazaarHub.Services;

namespace BazaarHub.UI;

/// <summary>
/// 页面 2：「更新与 Mod 增量同步」。
/// 提供版本对比概览卡、现代进度控制栏、文件增量同步明细表以及服务器/社区 Mod 浏览与下载/上传操作。
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
    private readonly Label _lblFilesEmpty;
    private readonly Label _lblModsEmpty;

    public event Action<bool>? SyncRequested; // true = 下载同步, false = 仅检查
    public event Action? OpenModCacheRequested;
    public event Action? OpenWorkshopRequested;
    public event Action? RefreshCommunityModsRequested;
    public event Action<string>? DownloadCommunityModRequested;
    public event Action? UploadLocalModRequested;

    public UpdateSyncPage()
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
            RowCount = 5,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        HubTheme.EnableDoubleBuffer(root);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));   // 统一顶部标题条
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));  // 三张版本对比卡（充裕高度防底部路径裁切）
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));   // 操作栏与进度条
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));    // 同步文件明细
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));    // 服务器与社区 Mod 列表

        // ── 1. 顶部标题条（零硬编码坐标）─────────────────────────────────
        var btnWorkshop = new GoldButton
        {
            Text = "创意工坊编辑器 (Web)",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Workshop,
            Size = new Size(182, 34),
            Margin = new Padding(0)
        };
        btnWorkshop.Click += (_, _) => OpenWorkshopRequested?.Invoke();

        var header = new SectionHeaderBar(
            "更新与 Mod 增量同步",
            "基于 SHA-256 哈希增量校验与 .tmp 原子落盘替换；断网时自动优雅降级使用本地缓存。",
            btnWorkshop);
        root.Controls.Add(header, 0, 0);

        // ── 2. 三张版本对比统计卡 ────────────────────────────────────────
        var statsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 10)
        };
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        statsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));

        (GoldCardPanel c1, _lblPluginVer) = CreateMetricCard(
            "服务端插件版本", "待检查", @"BepInEx\plugins\BazaarLauncher.dll", HubTheme.Gold);
        c1.Margin = new Padding(0, 0, 10, 0);

        (GoldCardPanel c2, _lblL10nVer) = CreateMetricCard(
            "外挂汉化词典版本", "待检查", @"BepInEx\config\custom_zh_overlay.json", HubTheme.StatusBlue);
        c2.Margin = new Padding(0, 0, 10, 0);

        (GoldCardPanel c3, _lblModSummary) = CreateMetricCard(
            "启用 Mod 与 PNG 资产", "待检查", @"BepInEx\config\BazaarModCache\", HubTheme.StatusGreen);
        c3.Margin = new Padding(0);

        statsGrid.Controls.Add(c1, 0, 0);
        statsGrid.Controls.Add(c2, 1, 0);
        statsGrid.Controls.Add(c3, 2, 0);
        root.Controls.Add(statsGrid, 0, 1);

        // ── 3. 同步操作与进度面板卡 ──────────────────────────────────────
        var toolbarCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 10)
        };
        var barLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        barLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));

        _btnSyncNow = new GoldButton
        {
            Text = "立即检查并同步",
            Variant = GoldButtonVariant.PrimaryGold,
            Icon = HubIconKind.Sync,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 8, 1)
        };
        _btnSyncNow.Click += (_, _) => SyncRequested?.Invoke(true);

        _btnCheckOnly = new GoldButton
        {
            Text = "仅检查差异",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Search,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 8, 1)
        };
        _btnCheckOnly.Click += (_, _) => SyncRequested?.Invoke(false);

        var btnOpenCache = new GoldButton
        {
            Text = "打开 Mod 缓存",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.Folder,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 12, 1)
        };
        btnOpenCache.Click += (_, _) => OpenModCacheRequested?.Invoke();

        _lblSyncSummary = new Label
        {
            Text = "点击「立即检查并同步」直连当前服务器比对最新清单。",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _progressBar = new GoldProgressBar
        {
            Dock = DockStyle.Fill,
            ShowPercentage = true,
            Value = 0,
            Margin = new Padding(8, 8, 0, 8)
        };

        barLayout.Controls.Add(_btnSyncNow, 0, 0);
        barLayout.Controls.Add(_btnCheckOnly, 1, 0);
        barLayout.Controls.Add(btnOpenCache, 2, 0);
        barLayout.Controls.Add(_lblSyncSummary, 3, 0);
        barLayout.Controls.Add(_progressBar, 4, 0);
        toolbarCard.Controls.Add(barLayout);
        root.Controls.Add(toolbarCard, 0, 2);

        // ── 4. 文件增量同步明细表 ────────────────────────────────────────
        var filesCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0, 0, 0, 10)
        };
        var filesTop = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 28,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        filesTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filesTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        filesTop.Controls.Add(new Label
        {
            Text = "同步文件明细（插件 / 外挂汉化词典 / Mod 贴图资产）",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var filesTableHost = new Panel { Dock = DockStyle.Fill, BackColor = HubTheme.BgInput, Padding = new Padding(1) };
        filesTableHost.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderHairline, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, filesTableHost.Width - 1, filesTableHost.Height - 1);
        };

        _gridFiles = new DataGridView { Dock = DockStyle.Fill };
        HubTheme.StyleDataGridView(_gridFiles);
        _gridFiles.Columns.Add("Category", "类别");
        _gridFiles.Columns.Add("Name", "项目名称");
        _gridFiles.Columns.Add("Target", "本地落盘路径");
        _gridFiles.Columns.Add("Hash", "远端 SHA-256");
        _gridFiles.Columns.Add("Action", "状态");
        _gridFiles.Columns.Add("Detail", "详情");
        _gridFiles.Columns[0].FillWeight = 11;
        _gridFiles.Columns[1].FillWeight = 22;
        _gridFiles.Columns[2].FillWeight = 28;
        _gridFiles.Columns[3].FillWeight = 13;
        _gridFiles.Columns[4].FillWeight = 10;
        _gridFiles.Columns[5].FillWeight = 16;

        _lblFilesEmpty = new Label
        {
            Text = "尚未执行同步检查 · 点击上方「立即检查并同步」获取文件清单",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextMuted,
            BackColor = HubTheme.BgInput,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        };

        filesTableHost.Controls.Add(_lblFilesEmpty);
        filesTableHost.Controls.Add(_gridFiles);
        _lblFilesEmpty.BringToFront();

        filesCard.Controls.Add(filesTableHost);
        filesCard.Controls.Add(filesTop);
        root.Controls.Add(filesCard, 0, 3);

        // ── 5. 服务器与社区 Mod 列表 ──────────────────────────────────────
        var modsCard = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14, 12, 14, 12),
            Margin = new Padding(0)
        };

        // 使用 TableLayoutPanel 彻底杜绝按钮负坐标重叠
        var modsTopBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 34,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };
        modsTopBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        modsTopBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        modsTopBar.Controls.Add(new Label
        {
            Text = "服务器与社区 Mod 列表",
            Font = HubTheme.FontSectionTitle,
            ForeColor = HubTheme.TextPrimary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var modsBtnFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = new Padding(8, 1, 0, 1)
        };

        var btnRefreshCommunity = new GoldButton
        {
            Text = "刷新列表",
            Variant = GoldButtonVariant.SubtleDark,
            Icon = HubIconKind.Refresh,
            Size = new Size(112, 30),
            Margin = new Padding(0, 0, 6, 0)
        };
        btnRefreshCommunity.Click += (_, _) => RefreshCommunityModsRequested?.Invoke();

        var btnDownloadMod = new GoldButton
        {
            Text = "下载选中 Mod",
            Variant = GoldButtonVariant.OutlineGold,
            Icon = HubIconKind.Download,
            Size = new Size(138, 30),
            Margin = new Padding(0, 0, 6, 0)
        };
        btnDownloadMod.Click += (_, _) =>
        {
            if (_gridMods is null) return;
            DataGridViewRow? row = _gridMods.SelectedRows.Count > 0 ? _gridMods.SelectedRows[0] : _gridMods.CurrentRow;
            string? modId = row?.Cells[0].Value?.ToString();
            if (string.IsNullOrWhiteSpace(modId))
            {
                MessageBox.Show(this, "请先在下方列表中点选一个要下载的 Mod。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DownloadCommunityModRequested?.Invoke(modId);
        };

        var btnUploadMod = new GoldButton
        {
            Text = "上传本地 Mod",
            Variant = GoldButtonVariant.PrimaryGold,
            Icon = HubIconKind.Upload,
            Size = new Size(138, 30),
            Margin = new Padding(0)
        };
        btnUploadMod.Click += (_, _) => UploadLocalModRequested?.Invoke();

        modsBtnFlow.Controls.Add(btnRefreshCommunity);
        modsBtnFlow.Controls.Add(btnDownloadMod);
        modsBtnFlow.Controls.Add(btnUploadMod);
        modsTopBar.Controls.Add(modsBtnFlow, 1, 0);

        var modsTableHost = new Panel { Dock = DockStyle.Fill, BackColor = HubTheme.BgInput, Padding = new Padding(1) };
        modsTableHost.Paint += (_, e) =>
        {
            using var pen = new Pen(HubTheme.BorderHairline, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, modsTableHost.Width - 1, modsTableHost.Height - 1);
        };

        _gridMods = new DataGridView { Dock = DockStyle.Fill };
        HubTheme.StyleDataGridView(_gridMods);
        _gridMods.Columns.Add("Id", "Mod ID");
        _gridMods.Columns.Add("Name", "名称");
        _gridMods.Columns.Add("Version", "版本");
        _gridMods.Columns.Add("Scope", "来源 / 作者");
        _gridMods.Columns.Add("Assets", "内容 / 下载");
        _gridMods.Columns.Add("Desc", "说明");
        _gridMods.Columns[0].FillWeight = 17;
        _gridMods.Columns[1].FillWeight = 20;
        _gridMods.Columns[2].FillWeight = 9;
        _gridMods.Columns[3].FillWeight = 17;
        _gridMods.Columns[4].FillWeight = 15;
        _gridMods.Columns[5].FillWeight = 22;

        _lblModsEmpty = new Label
        {
            Text = "暂无 Mod 记录 · 可点击右上角「刷新列表」拉取服务器与社区 Mod，或上传你的本地作品",
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextMuted,
            BackColor = HubTheme.BgInput,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Fill
        };

        modsTableHost.Controls.Add(_lblModsEmpty);
        modsTableHost.Controls.Add(_gridMods);
        _lblModsEmpty.BringToFront();

        modsCard.Controls.Add(modsTableHost);
        modsCard.Controls.Add(modsTopBar);
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
            _gridFiles.Rows[idx].Cells[4].Style.SelectionForeColor = actionColor;
            _gridFiles.Rows[idx].Cells[4].Style.Font = HubTheme.FontBodyBold;
        }
        _gridFiles.ClearSelection();
        _lblFilesEmpty.Visible = _gridFiles.Rows.Count == 0;

        if (report.ModsManifest != null && report.ModsManifest.Mods.Count > 0)
        {
            _gridMods.Rows.Clear();
            foreach (RemoteModEntry mod in report.ModsManifest.Mods)
            {
                int modAssets = mod.Assets.Count > 0
                    ? mod.Assets.Count
                    : report.ModsManifest.Assets.Count(a => string.Equals(a.ModId, mod.Id, StringComparison.OrdinalIgnoreCase));

                _gridMods.Rows.Add(
                    mod.Id,
                    mod.Name,
                    mod.Version,
                    $"全服启用 / {mod.Scope}",
                    $"{modAssets} 贴图",
                    string.IsNullOrWhiteSpace(mod.Description) ? "-" : mod.Description);
            }
            _lblModsEmpty.Visible = _gridMods.Rows.Count == 0;
        }
    }

    public void RenderCommunityMods(IReadOnlyList<HubCommunityModItem> mods, string statusMessage, bool ok)
    {
        _lblSyncSummary.Text = statusMessage;
        _lblSyncSummary.ForeColor = ok ? HubTheme.StatusGreen : HubTheme.StatusRed;
        _gridMods.Rows.Clear();
        foreach (HubCommunityModItem m in mods)
        {
            string tag = m.IsServerDefault ? "全服启用" : "社区";
            string author = string.IsNullOrWhiteSpace(m.UploaderName) ? m.Author : m.UploaderName;
            int idx = _gridMods.Rows.Add(
                m.ModId,
                m.Name,
                m.Version,
                $"{tag} · {author}",
                $"卡{m.CustomCardCount} / 图{m.AssetCount} / ↓{m.DownloadCount}",
                string.IsNullOrWhiteSpace(m.Description) ? "-" : m.Description);

            _gridMods.Rows[idx].Cells[3].Style.ForeColor = m.IsServerDefault ? HubTheme.GoldLight : HubTheme.StatusBlue;
            _gridMods.Rows[idx].Cells[3].Style.SelectionForeColor = m.IsServerDefault ? HubTheme.GoldLight : HubTheme.StatusBlue;
        }
        _gridMods.ClearSelection();
        _lblModsEmpty.Visible = _gridMods.Rows.Count == 0;
    }

    private static (GoldCardPanel Card, Label ValueLabel) CreateMetricCard(
        string title, string initialValue, string hint, Color accentColor)
    {
        var card = new GoldCardPanel
        {
            Dock = DockStyle.Fill,
            AccentBarColor = accentColor,
            Padding = new Padding(18, 10, 14, 10)
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
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

        var lblTitle = new Label
        {
            Text = title,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        var lblValue = new Label
        {
            Text = initialValue,
            Font = HubTheme.FontMetric,
            ForeColor = HubTheme.GoldLight,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        var lblHint = new Label
        {
            Text = hint,
            Font = HubTheme.FontMonoSmall,
            ForeColor = HubTheme.TextMuted,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true
        };

        layout.Controls.Add(lblTitle, 0, 0);
        layout.Controls.Add(lblValue, 0, 1);
        layout.Controls.Add(lblHint, 0, 2);
        card.Controls.Add(layout);
        return (card, lblValue);
    }
}

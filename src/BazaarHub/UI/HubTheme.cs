using System.Drawing;
using System.Drawing.Drawing2D;

namespace BazaarHub.UI;

/// <summary>
/// 大巴扎深海蓝黑 + 鎏金视觉主题定义。
/// 统一全界面色彩、微软雅黑字体阶梯与圆角矢量绘制辅助方法。
/// </summary>
public static class HubTheme
{
    // 核心背景色阶
    public static readonly Color BgWindow = ColorTranslator.FromHtml("#12161F");
    public static readonly Color BgSidebar = ColorTranslator.FromHtml("#0D1017");
    public static readonly Color BgCard = ColorTranslator.FromHtml("#191F2C");
    public static readonly Color BgCardHover = ColorTranslator.FromHtml("#1F2737");
    public static readonly Color BgCardSelected = ColorTranslator.FromHtml("#222836");
    public static readonly Color BgInput = ColorTranslator.FromHtml("#10141D");

    // 鎏金与边框色阶
    public static readonly Color Gold = ColorTranslator.FromHtml("#D4AF37");
    public static readonly Color GoldLight = ColorTranslator.FromHtml("#ECCE6B");
    public static readonly Color GoldDark = ColorTranslator.FromHtml("#A6831F");
    public static readonly Color GoldTintBg = Color.FromArgb(28, 212, 175, 55);
    public static readonly Color BorderSubtle = ColorTranslator.FromHtml("#2A3346");
    public static readonly Color BorderMuted = ColorTranslator.FromHtml("#202736");

    // 文字色阶
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#F2EDE0");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#9AA4B8");
    public static readonly Color TextMuted = ColorTranslator.FromHtml("#6E788C");
    public static readonly Color TextOnGold = ColorTranslator.FromHtml("#12161F");

    // 状态语义色
    public static readonly Color StatusGreen = ColorTranslator.FromHtml("#2ECC71");
    public static readonly Color StatusYellow = ColorTranslator.FromHtml("#F1C40F");
    public static readonly Color StatusRed = ColorTranslator.FromHtml("#E74C3C");
    public static readonly Color StatusBlue = ColorTranslator.FromHtml("#4DA6FF");
    public static readonly Color DangerBg = ColorTranslator.FromHtml("#2C181C");
    public static readonly Color DangerHoverBg = ColorTranslator.FromHtml("#3D1E24");

    // 微软雅黑字体阶梯
    private const string PrimaryFontFamily = "Microsoft YaHei UI";
    private const string MonoFontFamily = "Consolas";

    public static readonly Font FontBrand = new(PrimaryFontFamily, 14.5f, FontStyle.Bold);
    public static readonly Font FontPageTitle = new(PrimaryFontFamily, 15.5f, FontStyle.Bold);
    public static readonly Font FontSectionTitle = new(PrimaryFontFamily, 10.5f, FontStyle.Bold);
    public static readonly Font FontBody = new(PrimaryFontFamily, 9.25f, FontStyle.Regular);
    public static readonly Font FontBodyBold = new(PrimaryFontFamily, 9.25f, FontStyle.Bold);
    public static readonly Font FontSmall = new(PrimaryFontFamily, 8.5f, FontStyle.Regular);
    public static readonly Font FontLargeAction = new(PrimaryFontFamily, 11.5f, FontStyle.Bold);
    public static readonly Font FontNav = new(PrimaryFontFamily, 9.75f, FontStyle.Bold);
    public static readonly Font FontMetric = new(PrimaryFontFamily, 13.5f, FontStyle.Bold);
    public static readonly Font FontMono = new(MonoFontFamily, 9.25f, FontStyle.Regular);

    /// <summary>
    /// 创建圆角矩形矢量路径。
    /// </summary>
    public static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 统一配置深色输入框样式。
    /// </summary>
    public static void StyleTextBox(TextBox tb)
    {
        tb.BackColor = BgInput;
        tb.ForeColor = TextPrimary;
        tb.BorderStyle = BorderStyle.FixedSingle;
        tb.Font = FontBody;
    }

    /// <summary>
    /// 统一配置深色下拉框样式。
    /// </summary>
    public static void StyleComboBox(ComboBox cb)
    {
        cb.BackColor = BgInput;
        cb.ForeColor = TextPrimary;
        cb.FlatStyle = FlatStyle.Flat;
        cb.Font = FontBody;
        cb.DropDownStyle = ComboBoxStyle.DropDownList;
    }

    /// <summary>
    /// 统一配置深色复选框样式。
    /// </summary>
    public static void StyleCheckBox(CheckBox chk, bool bold = false)
    {
        chk.ForeColor = TextPrimary;
        chk.BackColor = Color.Transparent;
        chk.Font = bold ? FontBodyBold : FontBody;
        chk.AutoSize = true;
        chk.Cursor = Cursors.Hand;
    }

    /// <summary>
    /// 统一配置深色数据表格（DataGridView）视觉样式。
    /// </summary>
    public static void StyleDataGridView(DataGridView grid)
    {
        grid.BackgroundColor = BgInput;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = BorderMuted;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowTemplate.Height = 30;

        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeight = 32;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = BgSidebar,
            ForeColor = Gold,
            Font = FontBodyBold,
            SelectionBackColor = BgSidebar,
            SelectionForeColor = Gold,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 6, 0)
        };

        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = BgCard,
            ForeColor = TextPrimary,
            Font = FontBody,
            SelectionBackColor = Color.FromArgb(44, 56, 78),
            SelectionForeColor = TextPrimary,
            Padding = new Padding(6, 0, 6, 0)
        };

        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ColorTranslator.FromHtml("#151B26"),
            ForeColor = TextPrimary,
            Font = FontBody,
            SelectionBackColor = Color.FromArgb(44, 56, 78),
            SelectionForeColor = TextPrimary,
            Padding = new Padding(6, 0, 6, 0)
        };
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BazaarHub.UI;

/// <summary>
/// 大巴扎「暗黑黑曜石 × 暖鎏金」奇幻集市视觉主题体系。
/// 统一分层表面色板、语义状态色、8px 节奏间距、字体阶梯、DPI 缩放与 Win32 暗黑原生窗口外观。
/// </summary>
public static class HubTheme
{
    // ── 分层黑曜石表面色阶（Obsidian Surface Hierarchy）──────────────────
    public static readonly Color Surface0 = ColorTranslator.FromHtml("#090B10");       // 极深底槽：侧边栏 / 顶栏 / 终端凹槽
    public static readonly Color Surface1 = ColorTranslator.FromHtml("#0E121A");       // 主舞台背景
    public static readonly Color Surface2 = ColorTranslator.FromHtml("#151B26");       // 主卡片表面
    public static readonly Color Surface2Top = ColorTranslator.FromHtml("#18202D");    // 卡片顶部微亮渐变起点
    public static readonly Color Surface3 = ColorTranslator.FromHtml("#1E2636");       // 悬停 / 次级浮层 / 按钮底
    public static readonly Color SurfaceSelected = ColorTranslator.FromHtml("#212735"); // 选中卡片底色

    // 兼容语义别名
    public static readonly Color BgWindow = Surface1;
    public static readonly Color BgSidebar = Surface0;
    public static readonly Color BgCard = Surface2;
    public static readonly Color BgCardHover = Surface3;
    public static readonly Color BgCardSelected = SurfaceSelected;
    public static readonly Color BgInput = ColorTranslator.FromHtml("#0B0E15");

    // ── 暖鎏金强调体系（Antique & Champagne Gold）────────────────────────
    public static readonly Color Gold = ColorTranslator.FromHtml("#D8B25E");
    public static readonly Color GoldLight = ColorTranslator.FromHtml("#F2D995");
    public static readonly Color GoldBright = ColorTranslator.FromHtml("#FAE7B4");
    public static readonly Color GoldDark = ColorTranslator.FromHtml("#9E7A2D");
    public static readonly Color GoldDeep = ColorTranslator.FromHtml("#7A5B1C");
    public static readonly Color GoldTintBg = Color.FromArgb(26, 216, 178, 94);
    public static readonly Color GoldTintHover = Color.FromArgb(42, 216, 178, 94);
    public static readonly Color GoldBorderSoft = Color.FromArgb(92, 216, 178, 94);

    // ── 边框与微光线 ─────────────────────────────────────────────────────
    public static readonly Color BorderHairline = ColorTranslator.FromHtml("#1D2433");
    public static readonly Color BorderMuted = ColorTranslator.FromHtml("#222A3B");
    public static readonly Color BorderSubtle = ColorTranslator.FromHtml("#2B354A");
    public static readonly Color BorderHover = ColorTranslator.FromHtml("#3D4B66");
    public static readonly Color InnerHighlight = Color.FromArgb(16, 255, 248, 232);

    // ── 高对比文字色阶 ───────────────────────────────────────────────────
    public static readonly Color TextPrimary = ColorTranslator.FromHtml("#F4EFE6");
    public static readonly Color TextSecondary = ColorTranslator.FromHtml("#9CA7BC");
    public static readonly Color TextMuted = ColorTranslator.FromHtml("#68748B");
    public static readonly Color TextOnGold = ColorTranslator.FromHtml("#151109");

    // ── 语义状态色（主色 + 胶囊暗底色 + 描边色）──────────────────────────
    public static readonly Color StatusGreen = ColorTranslator.FromHtml("#34D399");
    public static readonly Color StatusGreenBg = ColorTranslator.FromHtml("#102922");
    public static readonly Color StatusGreenBorder = ColorTranslator.FromHtml("#1E5B47");

    public static readonly Color StatusYellow = ColorTranslator.FromHtml("#F5B942");
    public static readonly Color StatusAmberBg = ColorTranslator.FromHtml("#2B2110");
    public static readonly Color StatusAmberBorder = ColorTranslator.FromHtml("#694E1B");

    public static readonly Color StatusRed = ColorTranslator.FromHtml("#F06565");
    public static readonly Color StatusRedBg = ColorTranslator.FromHtml("#2D1519");
    public static readonly Color StatusRedBorder = ColorTranslator.FromHtml("#6E2832");

    public static readonly Color StatusBlue = ColorTranslator.FromHtml("#58A6FF");
    public static readonly Color StatusBlueBg = ColorTranslator.FromHtml("#122238");
    public static readonly Color StatusBlueBorder = ColorTranslator.FromHtml("#254C7A");

    public static readonly Color StatusPurple = ColorTranslator.FromHtml("#B388FF");

    public static readonly Color DangerBg = StatusRedBg;
    public static readonly Color DangerHoverBg = ColorTranslator.FromHtml("#3E1B21");

    // ── 8px 节奏间距常量 ─────────────────────────────────────────────────
    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 12;
    public const int SpaceLg = 16;
    public const int SpaceXl = 24;

    // ── 字体阶梯（自动择优 Cascadia Mono / Consolas）─────────────────────
    private const string PrimaryFontFamily = "Microsoft YaHei UI";
    private static readonly string MonoFontFamily = ResolveMonoFontFamily();

    public static readonly Font FontBrand = new(PrimaryFontFamily, 12.5f, FontStyle.Bold);
    public static readonly Font FontHeroTitle = new(PrimaryFontFamily, 15.5f, FontStyle.Bold);
    public static readonly Font FontPageTitle = new(PrimaryFontFamily, 14f, FontStyle.Bold);
    public static readonly Font FontSectionTitle = new(PrimaryFontFamily, 10.25f, FontStyle.Bold);
    public static readonly Font FontCardTitle = new(PrimaryFontFamily, 9.75f, FontStyle.Bold);
    public static readonly Font FontBody = new(PrimaryFontFamily, 9f, FontStyle.Regular);
    public static readonly Font FontBodyBold = new(PrimaryFontFamily, 9f, FontStyle.Bold);
    public static readonly Font FontSmall = new(PrimaryFontFamily, 8.25f, FontStyle.Regular);
    public static readonly Font FontSmallBold = new(PrimaryFontFamily, 8.25f, FontStyle.Bold);
    public static readonly Font FontCaption = new(PrimaryFontFamily, 7.75f, FontStyle.Regular);
    public static readonly Font FontLargeAction = new(PrimaryFontFamily, 11f, FontStyle.Bold);
    public static readonly Font FontNav = new(PrimaryFontFamily, 9.25f, FontStyle.Bold);
    public static readonly Font FontMetric = new(PrimaryFontFamily, 14f, FontStyle.Bold);
    public static readonly Font FontMono = new(MonoFontFamily, 9f, FontStyle.Regular);
    public static readonly Font FontMonoSmall = new(MonoFontFamily, 8.25f, FontStyle.Regular);

    // ── Win32 沉浸式暗黑窗口与滚动条互操作 ───────────────────────────────
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? pszSubAppName, string? pszSubIdList);

    private static string ResolveMonoFontFamily()
    {
        try
        {
            using var installed = new InstalledFontCollection();
            foreach (FontFamily family in installed.Families)
            {
                if (string.Equals(family.Name, "Cascadia Mono", StringComparison.OrdinalIgnoreCase))
                {
                    return "Cascadia Mono";
                }
            }
        }
        catch
        {
            // 回退系统等宽字体
        }

        return "Consolas";
    }

    /// <summary>
    /// 根据控件当前所在显示器的 DPI 计算缩放后的像素值（支持 100%/125%/150% 及多屏 PerMonitorV2）。
    /// </summary>
    public static int Scale(Control? control, int logicalPx)
    {
        float dpi = control != null && control.DeviceDpi > 0 ? control.DeviceDpi : 96f;
        return (int)Math.Round(logicalPx * (dpi / 96f));
    }

    public static float ScaleF(Control? control, float logicalPx)
    {
        float dpi = control != null && control.DeviceDpi > 0 ? control.DeviceDpi : 96f;
        return logicalPx * (dpi / 96f);
    }

    /// <summary>
    /// 为顶层窗体开启 Windows 10/11 沉浸式暗黑标题栏并匹配 Surface0 色值。
    /// </summary>
    public static void ApplyDarkWindowChrome(Form form)
    {
        if (!form.IsHandleCreated) return;
        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));

            int captionColorRef = ToColorRef(Surface0);
            _ = DwmSetWindowAttribute(form.Handle, DwmwaCaptionColor, ref captionColorRef, sizeof(int));

            int textColorRef = ToColorRef(TextPrimary);
            _ = DwmSetWindowAttribute(form.Handle, DwmwaTextColor, ref textColorRef, sizeof(int));

            int borderColorRef = ToColorRef(BorderSubtle);
            _ = DwmSetWindowAttribute(form.Handle, DwmwaBorderColor, ref borderColorRef, sizeof(int));
        }
        catch
        {
            // 旧版 Windows 静默忽略，不影响窗体正常运行
        }
    }

    /// <summary>
    /// 为支持滚动条的 Win32 控件启用原生暗色滚动条皮肤（DarkMode_Explorer）。
    /// </summary>
    public static void ApplyDarkScrollBar(Control control)
    {
        void ApplyCore()
        {
            if (control.IsDisposed || !control.IsHandleCreated) return;
            try
            {
                _ = SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch
            {
                // 静默忽略不支持的系统版本
            }
        }

        if (control.IsHandleCreated)
        {
            ApplyCore();
        }
        else
        {
            control.HandleCreated += (_, _) => ApplyCore();
        }
    }

    /// <summary>
    /// 通过双缓冲属性消除容器和表格在缩放/切换时的重绘闪烁。
    /// </summary>
    public static void EnableDoubleBuffer(Control control)
    {
        try
        {
            PropertyInfo? prop = typeof(Control).GetProperty(
                "DoubleBuffered",
                BindingFlags.Instance | BindingFlags.NonPublic);
            prop?.SetValue(control, true, null);
        }
        catch
        {
            // 忽略反射受限场景
        }
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    /// <summary>
    /// 创建平滑圆角矩形矢量路径。
    /// </summary>
    public static GraphicsPath CreateRoundedRectPath(Rectangle bounds, int radius)
    {
        return CreateRoundedRectPath((RectangleF)bounds, radius);
    }

    /// <summary>
    /// 创建高精度浮点圆角矩形矢量路径。
    /// </summary>
    public static GraphicsPath CreateRoundedRectPath(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0.5f || bounds.Width <= 0.5f || bounds.Height <= 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 统一配置深色无边框基础输入框（配合 GoldTextBox 容器使用）。
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
    /// 统一配置深色数据表格（DataGridView）视觉样式与暗黑滚动条。
    /// </summary>
    public static void StyleDataGridView(DataGridView grid)
    {
        EnableDoubleBuffer(grid);
        ApplyDarkScrollBar(grid);

        grid.BackgroundColor = BgInput;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = BorderHairline;
        grid.EnableHeadersVisualStyles = false;
        grid.RowHeadersVisible = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.MultiSelect = false;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.RowTemplate.Height = 34;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeight = 34;

        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface0,
            ForeColor = GoldLight,
            Font = FontSmallBold,
            SelectionBackColor = Surface0,
            SelectionForeColor = GoldLight,
            Alignment = DataGridViewContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0)
        };

        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ColorTranslator.FromHtml("#111620"),
            ForeColor = TextPrimary,
            Font = FontBody,
            SelectionBackColor = ColorTranslator.FromHtml("#263042"),
            SelectionForeColor = GoldBright,
            Padding = new Padding(10, 0, 10, 0)
        };

        grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ColorTranslator.FromHtml("#141A26"),
            ForeColor = TextPrimary,
            Font = FontBody,
            SelectionBackColor = ColorTranslator.FromHtml("#263042"),
            SelectionForeColor = GoldBright,
            Padding = new Padding(10, 0, 10, 0)
        };
    }

    /// <summary>
    /// 配置暗黑提示气泡工具（ToolTip）。
    /// </summary>
    public static ToolTip CreateDarkToolTip()
    {
        return new ToolTip
        {
            AutoPopDelay = 12000,
            InitialDelay = 300,
            ReshowDelay = 150,
            ShowAlways = true,
            BackColor = Surface3,
            ForeColor = TextPrimary
        };
    }
}

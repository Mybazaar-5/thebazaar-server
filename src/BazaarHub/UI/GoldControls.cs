using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using BazaarHub.Models;

namespace BazaarHub.UI;

/// <summary>
/// 矢量图标种类（全 GDI+ 路径抗锯齿绘制，不依赖系统 Emoji 或缺字符号）。
/// </summary>
public enum HubIconKind
{
    Launch,
    Sync,
    Tuning,
    Diagnostics,
    Workshop,
    ServerCloud,
    ServerLocal,
    ServerCustom,
    Official,
    Folder,
    Refresh,
    Warning,
    Shield,
    Play,
    Stop,
    Download,
    Upload,
    Check,
    Copy,
    Sparkle,
    Search,
    Save
}

/// <summary>
/// 纯 GDI+ 矢量图标渲染器（支持任意 DPI 无损缩放）。
/// </summary>
public static class HubVectorIcons
{
    public static void Draw(Graphics g, HubIconKind kind, RectangleF bounds, Color color, float strokeWidth = 1.6f)
    {
        if (bounds.Width <= 2f || bounds.Height <= 2f) return;

        GraphicsState state = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float x = bounds.X;
        float y = bounds.Y;
        float w = bounds.Width;
        float h = bounds.Height;
        float cx = x + w * 0.5f;
        float cy = y + h * 0.5f;

        using var pen = new Pen(color, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        using var brush = new SolidBrush(color);

        switch (kind)
        {
            case HubIconKind.Launch:
            {
                // 锐利前进启动箭头 + 尾焰星光
                PointF[] rocket =
                {
                    new(x + w * 0.84f, y + h * 0.16f),
                    new(x + w * 0.50f, y + h * 0.84f),
                    new(x + w * 0.42f, y + h * 0.56f),
                    new(x + w * 0.14f, y + h * 0.48f)
                };
                g.DrawPolygon(pen, rocket);
                g.DrawLine(pen, x + w * 0.42f, y + h * 0.56f, x + w * 0.82f, y + h * 0.18f);
                break;
            }

            case HubIconKind.Sync:
            case HubIconKind.Refresh:
            {
                RectangleF arcRect = new(x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f);
                g.DrawArc(pen, arcRect, 200, 140);
                g.DrawArc(pen, arcRect, 20, 140);
                // 上箭头
                g.DrawLine(pen, x + w * 0.72f, y + h * 0.18f, x + w * 0.86f, y + h * 0.28f);
                g.DrawLine(pen, x + w * 0.86f, y + h * 0.14f, x + w * 0.86f, y + h * 0.28f);
                // 下箭头
                g.DrawLine(pen, x + w * 0.28f, y + h * 0.82f, x + w * 0.14f, y + h * 0.72f);
                g.DrawLine(pen, x + w * 0.14f, y + h * 0.86f, x + w * 0.14f, y + h * 0.72f);
                break;
            }

            case HubIconKind.Tuning:
            {
                float r = Math.Max(2f, w * 0.12f);
                float[] ys = { y + h * 0.24f, y + h * 0.50f, y + h * 0.76f };
                float[] kx = { x + w * 0.36f, x + w * 0.68f, x + w * 0.44f };
                for (int i = 0; i < 3; i++)
                {
                    g.DrawLine(pen, x + w * 0.12f, ys[i], kx[i] - r, ys[i]);
                    g.DrawLine(pen, kx[i] + r, ys[i], x + w * 0.88f, ys[i]);
                    g.DrawEllipse(pen, kx[i] - r, ys[i] - r, r * 2f, r * 2f);
                }
                break;
            }

            case HubIconKind.Diagnostics:
            {
                using var frame = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.10f, y + h * 0.16f, w * 0.80f, h * 0.68f),
                    Math.Max(2f, w * 0.14f));
                g.DrawPath(pen, frame);
                g.DrawLine(pen, x + w * 0.26f, y + h * 0.40f, x + w * 0.40f, y + h * 0.50f);
                g.DrawLine(pen, x + w * 0.40f, y + h * 0.50f, x + w * 0.26f, y + h * 0.60f);
                g.DrawLine(pen, x + w * 0.48f, y + h * 0.60f, x + w * 0.72f, y + h * 0.60f);
                break;
            }

            case HubIconKind.Workshop:
            case HubIconKind.Sparkle:
            {
                // 四角奇幻星芒
                PointF[] star =
                {
                    new(cx, y + h * 0.08f),
                    new(cx + w * 0.14f, cy - h * 0.14f),
                    new(x + w * 0.92f, cy),
                    new(cx + w * 0.14f, cy + h * 0.14f),
                    new(cx, y + h * 0.92f),
                    new(cx - w * 0.14f, cy + h * 0.14f),
                    new(x + w * 0.08f, cy),
                    new(cx - w * 0.14f, cy - h * 0.14f)
                };
                g.DrawPolygon(pen, star);
                break;
            }

            case HubIconKind.ServerCloud:
            {
                using var path = new GraphicsPath();
                path.AddArc(x + w * 0.12f, y + h * 0.44f, w * 0.30f, h * 0.34f, 90, 180);
                path.AddArc(x + w * 0.28f, y + h * 0.22f, w * 0.42f, h * 0.42f, 180, 180);
                path.AddArc(x + w * 0.58f, y + h * 0.40f, w * 0.30f, h * 0.38f, 270, 180);
                path.CloseFigure();
                g.DrawPath(pen, path);
                break;
            }

            case HubIconKind.ServerLocal:
            {
                using var top = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.14f, y + h * 0.18f, w * 0.72f, h * 0.44f),
                    Math.Max(1.8f, w * 0.10f));
                g.DrawPath(pen, top);
                g.DrawLine(pen, cx, y + h * 0.62f, cx, y + h * 0.80f);
                g.DrawLine(pen, x + w * 0.30f, y + h * 0.80f, x + w * 0.70f, y + h * 0.80f);
                break;
            }

            case HubIconKind.ServerCustom:
            {
                RectangleF globe = new(x + w * 0.14f, y + h * 0.14f, w * 0.72f, h * 0.72f);
                g.DrawEllipse(pen, globe);
                g.DrawEllipse(pen, x + w * 0.34f, y + h * 0.14f, w * 0.32f, h * 0.72f);
                g.DrawLine(pen, x + w * 0.16f, cy, x + w * 0.84f, cy);
                break;
            }

            case HubIconKind.Official:
            case HubIconKind.Shield:
            {
                PointF[] shield =
                {
                    new(cx, y + h * 0.10f),
                    new(x + w * 0.84f, y + h * 0.24f),
                    new(x + w * 0.80f, y + h * 0.58f),
                    new(cx, y + h * 0.90f),
                    new(x + w * 0.20f, y + h * 0.58f),
                    new(x + w * 0.16f, y + h * 0.24f)
                };
                g.DrawPolygon(pen, shield);
                if (kind == HubIconKind.Official)
                {
                    g.DrawLine(pen, x + w * 0.34f, y + h * 0.50f, x + w * 0.46f, y + h * 0.62f);
                    g.DrawLine(pen, x + w * 0.46f, y + h * 0.62f, x + w * 0.68f, y + h * 0.38f);
                }
                break;
            }

            case HubIconKind.Folder:
            {
                PointF[] folder =
                {
                    new(x + w * 0.12f, y + h * 0.24f),
                    new(x + w * 0.42f, y + h * 0.24f),
                    new(x + w * 0.50f, y + h * 0.34f),
                    new(x + w * 0.88f, y + h * 0.34f),
                    new(x + w * 0.88f, y + h * 0.78f),
                    new(x + w * 0.12f, y + h * 0.78f)
                };
                g.DrawPolygon(pen, folder);
                break;
            }

            case HubIconKind.Warning:
            {
                PointF[] tri =
                {
                    new(cx, y + h * 0.12f),
                    new(x + w * 0.88f, y + h * 0.84f),
                    new(x + w * 0.12f, y + h * 0.84f)
                };
                g.DrawPolygon(pen, tri);
                g.DrawLine(pen, cx, y + h * 0.38f, cx, y + h * 0.60f);
                g.FillEllipse(brush, cx - 1.1f, y + h * 0.69f, 2.2f, 2.2f);
                break;
            }

            case HubIconKind.Play:
            {
                PointF[] tri =
                {
                    new(x + w * 0.28f, y + h * 0.18f),
                    new(x + w * 0.82f, cy),
                    new(x + w * 0.28f, y + h * 0.82f)
                };
                g.FillPolygon(brush, tri);
                break;
            }

            case HubIconKind.Stop:
            {
                using var sq = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.22f, y + h * 0.22f, w * 0.56f, h * 0.56f),
                    2f);
                g.FillPath(brush, sq);
                break;
            }

            case HubIconKind.Download:
            {
                g.DrawLine(pen, cx, y + h * 0.16f, cx, y + h * 0.66f);
                g.DrawLine(pen, x + w * 0.30f, y + h * 0.48f, cx, y + h * 0.68f);
                g.DrawLine(pen, x + w * 0.70f, y + h * 0.48f, cx, y + h * 0.68f);
                g.DrawLine(pen, x + w * 0.18f, y + h * 0.82f, x + w * 0.82f, y + h * 0.82f);
                break;
            }

            case HubIconKind.Upload:
            {
                g.DrawLine(pen, cx, y + h * 0.68f, cx, y + h * 0.18f);
                g.DrawLine(pen, x + w * 0.30f, y + h * 0.36f, cx, y + h * 0.16f);
                g.DrawLine(pen, x + w * 0.70f, y + h * 0.36f, cx, y + h * 0.16f);
                g.DrawLine(pen, x + w * 0.18f, y + h * 0.82f, x + w * 0.82f, y + h * 0.82f);
                break;
            }

            case HubIconKind.Check:
            {
                g.DrawLine(pen, x + w * 0.20f, y + h * 0.54f, x + w * 0.42f, y + h * 0.76f);
                g.DrawLine(pen, x + w * 0.42f, y + h * 0.76f, x + w * 0.82f, y + h * 0.26f);
                break;
            }

            case HubIconKind.Copy:
            {
                using var back = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.34f, y + h * 0.14f, w * 0.50f, h * 0.56f), 2f);
                using var front = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.16f, y + h * 0.30f, w * 0.50f, h * 0.56f), 2f);
                g.DrawPath(pen, back);
                g.DrawPath(pen, front);
                break;
            }

            case HubIconKind.Search:
            {
                g.DrawEllipse(pen, x + w * 0.16f, y + h * 0.16f, w * 0.50f, h * 0.50f);
                g.DrawLine(pen, x + w * 0.58f, y + h * 0.58f, x + w * 0.84f, y + h * 0.84f);
                break;
            }

            case HubIconKind.Save:
            {
                using var box = HubTheme.CreateRoundedRectPath(
                    new RectangleF(x + w * 0.16f, y + h * 0.16f, w * 0.68f, h * 0.68f), 2f);
                g.DrawPath(pen, box);
                g.DrawRectangle(pen, x + w * 0.32f, y + h * 0.16f, w * 0.36f, h * 0.24f);
                g.DrawLine(pen, x + w * 0.30f, y + h * 0.66f, x + w * 0.70f, y + h * 0.66f);
                break;
            }
        }

        g.Restore(state);
    }
}

/// <summary>
/// 按钮视觉变体。
/// </summary>
public enum GoldButtonVariant
{
    PrimaryGold,
    OutlineGold,
    SubtleDark,
    Ghost,
    DangerRed,
    SuccessGreen
}

/// <summary>
/// 现代黑曜石 × 暖鎏金自定义绘制按钮（支持矢量图标、双段渐变、顶光高光、悬停/按下/禁用与键盘交互）。
/// </summary>
public sealed class GoldButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private GoldButtonVariant _variant = GoldButtonVariant.OutlineGold;
    private int _cornerRadius = 8;
    private HubIconKind? _icon;

    public GoldButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = HubTheme.FontBodyBold;
        Cursor = Cursors.Hand;
        Size = new Size(136, 36);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GoldButtonVariant Variant
    {
        get => _variant;
        set { _variant = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(2, value); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HubIconKind? Icon
    {
        get => _icon;
        set { _icon = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new(1f, 1f, Math.Max(2, Width - 3), Math.Max(2, Height - 3));
        float r = HubTheme.ScaleF(this, _cornerRadius);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        Color textColor;
        Color borderColor;
        bool drawTopGloss = false;

        if (!Enabled)
        {
            using var disabledBrush = new SolidBrush(ColorTranslator.FromHtml("#1B2230"));
            g.FillPath(disabledBrush, path);
            borderColor = HubTheme.BorderSubtle;
            textColor = HubTheme.TextSecondary;
        }
        else
        {
            switch (_variant)
            {
                case GoldButtonVariant.PrimaryGold:
                {
                    Color top = _pressed
                        ? HubTheme.GoldDark
                        : (_hovered ? HubTheme.GoldBright : HubTheme.GoldLight);
                    Color bottom = _pressed
                        ? HubTheme.GoldDeep
                        : (_hovered ? HubTheme.Gold : HubTheme.GoldDark);
                    using var grad = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
                    g.FillPath(grad, path);
                    borderColor = _hovered ? HubTheme.GoldBright : HubTheme.GoldLight;
                    textColor = HubTheme.TextOnGold;
                    drawTopGloss = !_pressed;
                    break;
                }

                case GoldButtonVariant.DangerRed:
                {
                    Color bg = _pressed
                        ? ColorTranslator.FromHtml("#4A1E26")
                        : (_hovered ? HubTheme.DangerHoverBg : HubTheme.DangerBg);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = _hovered ? HubTheme.StatusRed : HubTheme.StatusRedBorder;
                    textColor = ColorTranslator.FromHtml("#FFB5B5");
                    break;
                }

                case GoldButtonVariant.SuccessGreen:
                {
                    Color bg = _pressed
                        ? ColorTranslator.FromHtml("#12392D")
                        : (_hovered ? ColorTranslator.FromHtml("#163D31") : HubTheme.StatusGreenBg);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = _hovered ? HubTheme.StatusGreen : HubTheme.StatusGreenBorder;
                    textColor = HubTheme.StatusGreen;
                    break;
                }

                case GoldButtonVariant.SubtleDark:
                {
                    Color top = _pressed
                        ? HubTheme.BgInput
                        : (_hovered ? ColorTranslator.FromHtml("#263044") : HubTheme.Surface3);
                    Color bottom = _pressed
                        ? HubTheme.BgInput
                        : (_hovered ? ColorTranslator.FromHtml("#1E2738") : ColorTranslator.FromHtml("#181F2D"));
                    using var grad = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
                    g.FillPath(grad, path);
                    borderColor = _hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle;
                    textColor = HubTheme.TextPrimary;
                    break;
                }

                case GoldButtonVariant.Ghost:
                {
                    Color bg = _pressed
                        ? Color.FromArgb(30, 255, 255, 255)
                        : (_hovered ? Color.FromArgb(18, 255, 255, 255) : Color.Transparent);
                    if (bg.A > 0)
                    {
                        using var brush = new SolidBrush(bg);
                        g.FillPath(brush, path);
                    }
                    borderColor = _hovered ? HubTheme.BorderSubtle : Color.Transparent;
                    textColor = _hovered ? HubTheme.TextPrimary : HubTheme.TextSecondary;
                    break;
                }

                default: // OutlineGold
                {
                    Color bg = _pressed
                        ? Color.FromArgb(46, 216, 178, 94)
                        : (_hovered ? HubTheme.GoldTintHover : HubTheme.GoldTintBg);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = _hovered ? HubTheme.GoldLight : HubTheme.GoldBorderSoft;
                    textColor = _hovered ? HubTheme.GoldBright : HubTheme.GoldLight;
                    break;
                }
            }
        }

        if (drawTopGloss && rect.Height > 12f)
        {
            using var glossPen = new Pen(Color.FromArgb(95, 255, 252, 238), 1f);
            g.DrawLine(glossPen, rect.X + r, rect.Y + 1.2f, rect.Right - r, rect.Y + 1.2f);
        }

        if (borderColor.A > 0)
        {
            using var pen = new Pen(borderColor, _variant == GoldButtonVariant.PrimaryGold ? 1.15f : 1.0f);
            g.DrawPath(pen, path);
        }

        if (Focused && Enabled)
        {
            RectangleF focusRect = RectangleF.Inflate(rect, -2f, -2f);
            using GraphicsPath fp = HubTheme.CreateRoundedRectPath(focusRect, Math.Max(1f, r - 1.5f));
            using var fpen = new Pen(Color.FromArgb(90, HubTheme.GoldBright), 1f);
            g.DrawPath(fpen, fp);
        }

        // 计算图标 + 文本水平居中排布（不向 MeasureText 传入 e.Graphics，避免 GetHdc 重置 GDI+ 裁剪态）
        int padX = HubTheme.Scale(this, 8);
        int textAreaWidth = Math.Max(10, Width - padX * 2);
        Size textSize = TextRenderer.MeasureText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            Font,
            new Size(1200, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        if (_icon.HasValue)
        {
            int iconSize = HubTheme.Scale(this, Height >= 46 ? 17 : 14);
            int gap = HubTheme.Scale(this, 6);
            int naturalTotalW = iconSize + gap + textSize.Width;
            int startX = naturalTotalW <= textAreaWidth
                ? (Width - naturalTotalW) / 2
                : padX;
            int iconY = (Height - iconSize) / 2;

            HubVectorIcons.Draw(
                g,
                _icon.Value,
                new RectangleF(startX, iconY, iconSize, iconSize),
                textColor,
                _variant == GoldButtonVariant.PrimaryGold ? 1.85f : 1.55f);

            int textX = startX + iconSize + gap;
            int availTextW = Math.Max(10, Width - textX - Math.Max(4, padX - 2));
            Rectangle textRect = new(textX, 0, availTextW, Height);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                textRect,
                textColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
        else
        {
            Rectangle textRect = new(padX, 0, textAreaWidth, Height);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                textRect,
                textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>
/// 现代深色微渐变圆角卡片容器（通过 OnPaintBackground 绘制，保证子标签透明无缝衔接无闪烁）。
/// </summary>
public sealed class GoldCardPanel : Panel
{
    private bool _highlighted;
    private bool _useHeroGradient;
    private int _cornerRadius = 12;
    private Color _cardBackColor = HubTheme.Surface2;
    private Color _cardTopColor = HubTheme.Surface2Top;
    private Color? _accentBarColor;

    public GoldCardPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        BackColor = HubTheme.Surface2;
        Padding = new Padding(16);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Highlighted
    {
        get => _highlighted;
        set { _highlighted = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UseHeroGradient
    {
        get => _useHeroGradient;
        set { _useHeroGradient = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(2, value); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CardBackColor
    {
        get => _cardBackColor;
        set { _cardBackColor = value; BackColor = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CardTopColor
    {
        get => _cardTopColor;
        set { _cardTopColor = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? AccentBarColor
    {
        get => _accentBarColor;
        set { _accentBarColor = value; Invalidate(); }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color outerBg = Parent?.BackColor ?? HubTheme.BgWindow;
        if (outerBg == Color.Transparent) outerBg = HubTheme.BgWindow;
        using (var outer = new SolidBrush(outerBg))
        {
            g.FillRectangle(outer, ClientRectangle);
        }

        if (Width < 6 || Height < 6) return;

        RectangleF rect = new(0.5f, 0.5f, Width - 2f, Height - 2f);
        float r = HubTheme.ScaleF(this, _cornerRadius);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        using (var grad = new LinearGradientBrush(rect, _cardTopColor, _cardBackColor, LinearGradientMode.Vertical))
        {
            g.FillPath(grad, path);
        }

        if (_useHeroGradient)
        {
            using var heroSheen = new LinearGradientBrush(
                rect,
                Color.FromArgb(26, 216, 178, 94),
                Color.FromArgb(0, 216, 178, 94),
                LinearGradientMode.ForwardDiagonal);
            g.FillPath(heroSheen, path);
        }
        else if (_highlighted)
        {
            using var topGlow = new LinearGradientBrush(
                new RectangleF(rect.X, rect.Y, rect.Width, Math.Min(64f, rect.Height)),
                Color.FromArgb(18, 216, 178, 94),
                Color.FromArgb(0, 216, 178, 94),
                LinearGradientMode.Vertical);
            g.FillPath(topGlow, path);
        }

        // 顶部柔和 1px 内高光（玻璃质感）
        using (var topEdge = new Pen(_highlighted ? Color.FromArgb(55, HubTheme.GoldLight) : HubTheme.InnerHighlight, 1f))
        {
            g.DrawLine(topEdge, rect.X + r, rect.Y + 1f, rect.Right - r, rect.Y + 1f);
        }

        // 可选左侧语义高亮竖条
        if (_accentBarColor.HasValue)
        {
            float barW = HubTheme.ScaleF(this, 3);
            float padY = HubTheme.ScaleF(this, 10);
            RectangleF barRect = new(rect.X + 1.5f, rect.Y + padY, barW, Math.Max(8f, rect.Height - padY * 2f));
            using GraphicsPath barPath = HubTheme.CreateRoundedRectPath(barRect, barW * 0.5f);
            using var barBrush = new SolidBrush(_accentBarColor.Value);
            g.FillPath(barBrush, barPath);
        }

        Color border = _highlighted ? HubTheme.GoldBorderSoft : HubTheme.BorderSubtle;
        using var pen = new Pen(border, _highlighted ? 1.25f : 1.0f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 暗黑双缓冲平滑滚动容器（自动启用 Win10/11 暗色原生滚动条）。
/// </summary>
public sealed class DarkScrollPanel : Panel
{
    public DarkScrollPanel()
    {
        DoubleBuffered = true;
        AutoScroll = true;
        BackColor = HubTheme.BgWindow;
        HubTheme.ApplyDarkScrollBar(this);
    }
}

/// <summary>
/// 状态胶囊语义色系。
/// </summary>
public enum PillTone
{
    Gold,
    Green,
    Amber,
    Red,
    Blue,
    Neutral
}

/// <summary>
/// 圆角胶囊状态徽章（支持呼吸感状态圆点或矢量图标、自动测量宽度防截断、支持点击交互）。
/// </summary>
public sealed class StatusPill : Control
{
    private PillTone _tone = PillTone.Neutral;
    private bool _showDot = true;
    private HubIconKind? _icon;
    private bool _interactive;
    private bool _hovered;
    private bool _autoWidth = true;

    public StatusPill()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = HubTheme.FontSmallBold;
        Height = 26;
        Width = 96;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PillTone Tone
    {
        get => _tone;
        set { _tone = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowDot
    {
        get => _showDot;
        set { _showDot = value; RecalcWidth(); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HubIconKind? Icon
    {
        get => _icon;
        set { _icon = value; RecalcWidth(); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Interactive
    {
        get => _interactive;
        set { _interactive = value; Cursor = value ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AutoWidth
    {
        get => _autoWidth;
        set { _autoWidth = value; RecalcWidth(); }
    }

    public void SetState(PillTone tone, string text, bool showDot = true, HubIconKind? icon = null)
    {
        _tone = tone;
        _showDot = showDot;
        _icon = icon;
        Text = text;
        RecalcWidth();
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        RecalcWidth();
        Invalidate();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        RecalcWidth();
    }

    private void RecalcWidth()
    {
        if (!_autoWidth) return;
        Size sz = TextRenderer.MeasureText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            Font,
            new Size(960, 40),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        int leading = _icon.HasValue ? HubTheme.Scale(this, 19) : (_showDot ? HubTheme.Scale(this, 14) : 0);
        int pad = HubTheme.Scale(this, 24);
        int targetW = Math.Max(HubTheme.Scale(this, 58), sz.Width + leading + pad);
        if (Width != targetW)
        {
            Width = targetW;
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (_interactive) { _hovered = true; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered) { _hovered = false; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        (Color bg, Color border, Color fg) = _tone switch
        {
            PillTone.Gold => (ColorTranslator.FromHtml("#262011"), HubTheme.GoldBorderSoft, HubTheme.GoldLight),
            PillTone.Green => (HubTheme.StatusGreenBg, HubTheme.StatusGreenBorder, HubTheme.StatusGreen),
            PillTone.Amber => (HubTheme.StatusAmberBg, HubTheme.StatusAmberBorder, HubTheme.StatusYellow),
            PillTone.Red => (HubTheme.StatusRedBg, HubTheme.StatusRedBorder, HubTheme.StatusRed),
            PillTone.Blue => (HubTheme.StatusBlueBg, HubTheme.StatusBlueBorder, HubTheme.StatusBlue),
            _ => (HubTheme.Surface3, HubTheme.BorderSubtle, HubTheme.TextSecondary)
        };

        RectangleF rect = new(0.5f, 0.5f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
        float r = rect.Height * 0.5f;
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        using (var fill = new SolidBrush(_hovered ? ControlPaint.Light(bg, 0.08f) : bg))
        {
            g.FillPath(fill, path);
        }
        using (var pen = new Pen(_hovered ? fg : border, 1f))
        {
            g.DrawPath(pen, path);
        }

        int padX = HubTheme.Scale(this, 10);
        int xCursor = padX;

        if (_icon.HasValue)
        {
            int iconSz = HubTheme.Scale(this, 12);
            HubVectorIcons.Draw(g, _icon.Value, new RectangleF(xCursor, (Height - iconSz) / 2f, iconSz, iconSz), fg, 1.5f);
            xCursor += iconSz + HubTheme.Scale(this, 5);
        }
        else if (_showDot)
        {
            float dot = HubTheme.ScaleF(this, 6.5f);
            float halo = dot + 4f;
            float cy = Height * 0.5f;
            using (var hb = new SolidBrush(Color.FromArgb(65, fg)))
            {
                g.FillEllipse(hb, xCursor - 2f, cy - halo * 0.5f, halo, halo);
            }
            using (var db = new SolidBrush(fg))
            {
                g.FillEllipse(db, xCursor, cy - dot * 0.5f, dot, dot);
            }
            xCursor += (int)Math.Ceiling(dot) + HubTheme.Scale(this, 6);
        }

        Rectangle textRect = new(xCursor, 0, Math.Max(10, Width - xCursor - HubTheme.Scale(this, 6)), Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
    }
}

/// <summary>
/// 圆角暗黑输入框（内置无边框 TextBox，彻底消除 WinForms 丑陋亮灰原边框，支持焦点鎏金高亮与占位提示）。
/// </summary>
public sealed class GoldTextBox : Control
{
    private readonly TextBox _inner;
    private bool _focused;
    private bool _hovered;

    public GoldTextBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = HubTheme.BgInput,
            ForeColor = HubTheme.TextPrimary,
            Font = HubTheme.FontBody
        };
        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _inner.TextChanged += (_, e) => OnTextChanged(e);
        _inner.MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        _inner.MouseLeave += (_, _) => { _hovered = false; Invalidate(); };

        Controls.Add(_inner);
        Size = new Size(240, 34);
        LayoutInner();
    }

    [AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set
        {
            string next = value ?? string.Empty;
            if (_inner.Text != next)
            {
                _inner.Text = next;
            }
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _inner.ReadOnly;
        set
        {
            _inner.ReadOnly = value;
            _inner.BackColor = value ? ColorTranslator.FromHtml("#0D1119") : HubTheme.BgInput;
            _inner.ForeColor = value ? HubTheme.TextSecondary : HubTheme.TextPrimary;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _inner.PlaceholderText;
        set => _inner.PlaceholderText = value;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_inner is null) return;
        _inner.Font = Font;
        LayoutInner();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _inner?.Focus();
    }

    private void LayoutInner()
    {
        if (_inner is null) return;
        int padX = HubTheme.Scale(this, 10);
        int innerH = _inner.PreferredHeight;
        int y = Math.Max(4, (Height - innerH) / 2);
        _inner.SetBounds(padX, y, Math.Max(20, Width - padX * 2), innerH);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        RectangleF rect = new(0.5f, 0.5f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
        float r = HubTheme.ScaleF(this, 8);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        Color fillBg = _inner.ReadOnly ? ColorTranslator.FromHtml("#0D1119") : HubTheme.BgInput;
        using (var bg = new SolidBrush(fillBg))
        {
            g.FillPath(bg, path);
        }

        Color border = _focused
            ? HubTheme.Gold
            : (_hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle);
        using var pen = new Pen(border, _focused ? 1.3f : 1.0f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 100% 纯自绘暗黑鎏金下拉选择框（完全脱离 Win32 原生 ComboBox 控件，彻底杜绝白边与白底下拉箭头）。
/// </summary>
public sealed class GoldComboBox : Control
{
    public sealed class ItemCollection
    {
        private readonly GoldComboBox _owner;
        private readonly List<object> _items = new();

        internal ItemCollection(GoldComboBox owner) => _owner = owner;

        public int Count => _items.Count;
        public object this[int index] => _items[index];
        public bool Contains(object item) => _items.Contains(item);

        public void Add(object item)
        {
            _items.Add(item);
            _owner.Invalidate();
        }

        public void AddRange(object[] items)
        {
            _items.AddRange(items);
            _owner.Invalidate();
        }

        public void Clear()
        {
            _items.Clear();
            _owner._selectedIndex = -1;
            _owner.Invalidate();
        }

        internal IReadOnlyList<object> Snapshot() => _items;
    }

    private sealed class DarkDropdownRenderer : ToolStripProfessionalRenderer
    {
        public DarkDropdownRenderer() : base(new DarkColorTable()) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Rectangle rect = new(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            bool hot = e.Item.Selected || e.Item.Pressed;
            Color fill = hot ? ColorTranslator.FromHtml("#263044") : HubTheme.Surface2;
            using var brush = new SolidBrush(fill);
            using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, 5);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
            if (hot)
            {
                using var pen = new Pen(HubTheme.GoldBorderSoft, 1f);
                e.Graphics.DrawPath(pen, path);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? HubTheme.GoldBright : HubTheme.TextPrimary;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(HubTheme.BorderSubtle, 1f);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => HubTheme.Surface2;
        public override Color ImageMarginGradientBegin => HubTheme.Surface2;
        public override Color ImageMarginGradientMiddle => HubTheme.Surface2;
        public override Color ImageMarginGradientEnd => HubTheme.Surface2;
        public override Color MenuBorder => HubTheme.BorderSubtle;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => HubTheme.Surface3;
    }

    private int _selectedIndex = -1;
    private bool _hovered;
    private bool _droppedDown;

    public event EventHandler? SelectedIndexChanged;

    public GoldComboBox()
    {
        Items = new ItemCollection(this);
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = HubTheme.FontBody;
        Cursor = Cursors.Hand;
        Size = new Size(148, 32);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ItemCollection Items { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int next = (value >= 0 && value < Items.Count) ? value : -1;
            if (_selectedIndex == next) return;
            _selectedIndex = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public object? SelectedItem =>
        (_selectedIndex >= 0 && _selectedIndex < Items.Count) ? Items[_selectedIndex] : null;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            ShowDropDownMenu();
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Enabled || Items.Count == 0) return;
        int delta = e.Delta > 0 ? -1 : 1;
        SelectedIndex = Math.Clamp(_selectedIndex + delta, 0, Items.Count - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled || Items.Count == 0) return;
        if (e.KeyCode == Keys.Down)
        {
            SelectedIndex = Math.Min(Items.Count - 1, _selectedIndex + 1);
        }
        else if (e.KeyCode == Keys.Up)
        {
            SelectedIndex = Math.Max(0, _selectedIndex - 1);
        }
        else if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            ShowDropDownMenu();
        }
    }

    private void ShowDropDownMenu()
    {
        if (Items.Count == 0) return;

        var menu = new ContextMenuStrip
        {
            Renderer = new DarkDropdownRenderer(),
            ShowImageMargin = false,
            BackColor = HubTheme.Surface2,
            ForeColor = HubTheme.TextPrimary,
            Font = Font,
            AutoSize = false,
            Padding = new Padding(2, 4, 2, 4)
        };

        int itemHeight = HubTheme.Scale(this, 28);
        IReadOnlyList<object> snap = Items.Snapshot();
        for (int i = 0; i < snap.Count; i++)
        {
            int idx = i;
            string label = snap[i]?.ToString() ?? string.Empty;
            var mi = new ToolStripMenuItem(label)
            {
                AutoSize = false,
                Width = Math.Max(Width - 4, HubTheme.Scale(this, 120)),
                Height = itemHeight,
                Font = idx == _selectedIndex ? HubTheme.FontBodyBold : Font,
                ForeColor = idx == _selectedIndex ? HubTheme.GoldLight : HubTheme.TextPrimary
            };
            mi.Click += (_, _) => SelectedIndex = idx;
            menu.Items.Add(mi);
        }

        menu.Size = new Size(Math.Max(Width, HubTheme.Scale(this, 124)), snap.Count * itemHeight + 10);
        menu.Closed += (_, _) =>
        {
            _droppedDown = false;
            Invalidate();
            menu.Dispose();
        };

        _droppedDown = true;
        Invalidate();
        menu.Show(this, new Point(0, Height + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new(0.5f, 0.5f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
        float r = HubTheme.ScaleF(this, 8);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        using (var fill = new SolidBrush(Enabled ? HubTheme.BgInput : ColorTranslator.FromHtml("#121620")))
        {
            g.FillPath(fill, path);
        }

        Color border = (Focused || _droppedDown)
            ? HubTheme.Gold
            : (_hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle);
        using (var pen = new Pen(border, (Focused || _droppedDown) ? 1.25f : 1.0f))
        {
            g.DrawPath(pen, path);
        }

        int arrowAreaW = HubTheme.Scale(this, 26);
        string display = SelectedItem?.ToString() ?? string.Empty;
        Rectangle textRect = new(HubTheme.Scale(this, 10), 0, Math.Max(10, Width - arrowAreaW - HubTheme.Scale(this, 10)), Height);
        TextRenderer.DrawText(
            g,
            display,
            Font,
            textRect,
            Enabled ? HubTheme.TextPrimary : HubTheme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        // 精致矢量下拉小箭头
        float cx = Width - arrowAreaW * 0.5f;
        float cy = Height * 0.5f;
        float aw = HubTheme.ScaleF(this, 4.2f);
        float ah = HubTheme.ScaleF(this, 2.4f);
        using var arrowPen = new Pen(Enabled ? HubTheme.GoldLight : HubTheme.TextMuted, 1.55f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLine(arrowPen, cx - aw, cy - ah, cx, cy + ah);
        g.DrawLine(arrowPen, cx, cy + ah, cx + aw, cy - ah);
    }
}

/// <summary>
/// 现代圆角滑动开关（彻底取代刺眼的 Win32 白色 CheckBox）。
/// </summary>
public sealed class GoldToggleSwitch : Control
{
    private bool _checked;
    private bool _hovered;

    public event EventHandler? CheckedChanged;

    public GoldToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(44, 24);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Enabled)
        {
            Checked = !Checked;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Space or Keys.Enter)
        {
            Checked = !Checked;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float trackH = Math.Min(Height - 2f, HubTheme.ScaleF(this, 22));
        float trackW = Math.Min(Width - 2f, HubTheme.ScaleF(this, 42));
        float tx = (Width - trackW) * 0.5f;
        float ty = (Height - trackH) * 0.5f;
        RectangleF trackRect = new(tx, ty, trackW, trackH);
        using GraphicsPath trackPath = HubTheme.CreateRoundedRectPath(trackRect, trackH * 0.5f);

        if (_checked)
        {
            using var grad = new LinearGradientBrush(
                trackRect,
                _hovered ? HubTheme.GoldBright : HubTheme.GoldLight,
                HubTheme.GoldDark,
                LinearGradientMode.Horizontal);
            g.FillPath(grad, trackPath);
        }
        else
        {
            using var offBg = new SolidBrush(_hovered ? HubTheme.Surface3 : HubTheme.BgInput);
            g.FillPath(offBg, trackPath);
            using var offBorder = new Pen(_hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle, 1.1f);
            g.DrawPath(offBorder, trackPath);
        }

        float pad = 3f;
        float knobD = trackH - pad * 2f;
        float knobX = _checked ? (trackRect.Right - pad - knobD) : (trackRect.X + pad);
        float knobY = trackRect.Y + pad;

        using var knobBrush = new SolidBrush(_checked ? HubTheme.TextOnGold : HubTheme.TextSecondary);
        g.FillEllipse(knobBrush, knobX, knobY, knobD, knobD);
    }
}

/// <summary>
/// 现代可点击胶囊复选芯片（用于多选项横向/网格排布，选中态暖金描边 + 矢量勾选）。
/// </summary>
public sealed class GoldToggleChip : Control
{
    private bool _checked;
    private bool _hovered;

    public event EventHandler? CheckedChanged;

    public GoldToggleChip()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Font = HubTheme.FontSmall;
        Cursor = Cursors.Hand;
        Size = new Size(168, 32);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Enabled) Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Enabled && e.KeyCode is Keys.Space or Keys.Enter) Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new(0.5f, 0.5f, Math.Max(2f, Width - 2f), Math.Max(2f, Height - 2f));
        float r = HubTheme.ScaleF(this, 8);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        Color fill = _checked
            ? (_hovered ? HubTheme.GoldTintHover : HubTheme.GoldTintBg)
            : (_hovered ? HubTheme.Surface3 : HubTheme.BgInput);
        Color border = _checked
            ? (_hovered ? HubTheme.GoldLight : HubTheme.GoldBorderSoft)
            : (_hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle);

        using (var fb = new SolidBrush(fill)) g.FillPath(fb, path);
        using (var bp = new Pen(border, 1f)) g.DrawPath(bp, path);

        // 左侧自绘微型圆角复选框
        int boxSz = HubTheme.Scale(this, 14);
        int boxX = HubTheme.Scale(this, 9);
        int boxY = (Height - boxSz) / 2;
        RectangleF boxRect = new(boxX, boxY, boxSz, boxSz);
        using GraphicsPath boxPath = HubTheme.CreateRoundedRectPath(boxRect, HubTheme.ScaleF(this, 4));

        if (_checked)
        {
            using var gb = new SolidBrush(HubTheme.Gold);
            g.FillPath(gb, boxPath);
            HubVectorIcons.Draw(g, HubIconKind.Check, RectangleF.Inflate(boxRect, -2f, -2f), HubTheme.TextOnGold, 1.8f);
        }
        else
        {
            using var ub = new Pen(HubTheme.TextMuted, 1.1f);
            g.DrawPath(ub, boxPath);
        }

        int textX = boxX + boxSz + HubTheme.Scale(this, 7);
        Rectangle textRect = new(textX, 0, Math.Max(10, Width - textX - HubTheme.Scale(this, 6)), Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            textRect,
            _checked ? HubTheme.GoldLight : HubTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>
/// 左侧现代导航项（纯矢量图标 + 左侧鎏金指示条 + 选中暖金柔光背板 + 可选右侧徽标）。
/// </summary>
public sealed class NavSidebarButton : Control
{
    private bool _selected;
    private bool _hovered;
    private HubIconKind _icon = HubIconKind.Launch;
    private string _badgeText = string.Empty;

    public NavSidebarButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        Font = HubTheme.FontNav;
        Cursor = Cursors.Hand;
        Size = new Size(212, 44);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public HubIconKind Icon
    {
        get => _icon;
        set { _icon = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string BadgeText
    {
        get => _badgeText;
        set { _badgeText = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(HubTheme.BgSidebar))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        int marginX = HubTheme.Scale(this, 10);
        int marginY = HubTheme.Scale(this, 2);
        RectangleF pill = new(marginX, marginY, Math.Max(4, Width - marginX * 2), Math.Max(4, Height - marginY * 2));
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(pill, HubTheme.ScaleF(this, 9));

        if (_selected)
        {
            using var selGrad = new LinearGradientBrush(
                pill,
                Color.FromArgb(40, 216, 178, 94),
                Color.FromArgb(14, 216, 178, 94),
                LinearGradientMode.Horizontal);
            g.FillPath(selGrad, path);
            using var selBorder = new Pen(HubTheme.GoldBorderSoft, 1f);
            g.DrawPath(selBorder, path);

            // 左侧圆角鎏金指示条
            float barW = HubTheme.ScaleF(this, 3.2f);
            float barPad = HubTheme.ScaleF(this, 9);
            RectangleF barRect = new(pill.X + 1.5f, pill.Y + barPad, barW, Math.Max(8f, pill.Height - barPad * 2f));
            using GraphicsPath barPath = HubTheme.CreateRoundedRectPath(barRect, barW * 0.5f);
            using var barBrush = new SolidBrush(HubTheme.GoldLight);
            g.FillPath(barBrush, barPath);
        }
        else if (_hovered)
        {
            using var hovFill = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
            g.FillPath(hovFill, path);
        }

        Color iconColor = _selected ? HubTheme.GoldLight : (_hovered ? HubTheme.TextPrimary : HubTheme.TextSecondary);
        Color textColor = _selected ? HubTheme.GoldBright : (_hovered ? HubTheme.TextPrimary : HubTheme.TextSecondary);

        int iconSz = HubTheme.Scale(this, 16);
        int iconX = marginX + HubTheme.Scale(this, 14);
        int iconY = (Height - iconSz) / 2;
        HubVectorIcons.Draw(g, _icon, new RectangleF(iconX, iconY, iconSz, iconSz), iconColor, 1.65f);

        int rightReserve = HubTheme.Scale(this, 14);
        if (!string.IsNullOrWhiteSpace(_badgeText))
        {
            int badgeW = HubTheme.Scale(this, 42);
            int badgeH = HubTheme.Scale(this, 18);
            RectangleF badgeRect = new(pill.Right - badgeW - HubTheme.Scale(this, 8), (Height - badgeH) * 0.5f, badgeW, badgeH);
            using GraphicsPath bp = HubTheme.CreateRoundedRectPath(badgeRect, badgeH * 0.5f);
            using var bf = new SolidBrush(HubTheme.GoldTintBg);
            g.FillPath(bf, bp);
            using var bpen = new Pen(HubTheme.GoldBorderSoft, 0.9f);
            g.DrawPath(bpen, bp);
            TextRenderer.DrawText(
                g,
                _badgeText,
                HubTheme.FontCaption,
                Rectangle.Round(badgeRect),
                HubTheme.GoldLight,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            rightReserve += badgeW + HubTheme.Scale(this, 6);
        }

        int labelX = iconX + iconSz + HubTheme.Scale(this, 11);
        Rectangle labelRect = new(labelX, 0, Math.Max(10, Width - labelX - rightReserve), Height);
        TextRenderer.DrawText(
            g,
            Text,
            Font,
            labelRect,
            textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>
/// 服务器模式选择卡片（全 DPI 动态排版 + 矢量模式图标 + 实时延迟状态灯）。
/// </summary>
public sealed class ServerModeCard : Control
{
    private bool _selected;
    private bool _hovered;
    private string _title;
    private string _subtitle;
    private string _statusText = "等待检测...";
    private Color _lampColor = HubTheme.TextMuted;
    private readonly HubIconKind _icon;

    public ServerModeCard(ServerMode mode, string title, string subtitle)
    {
        Mode = mode;
        _title = title;
        _subtitle = subtitle;
        _icon = mode switch
        {
            ServerMode.PresetRemote => HubIconKind.ServerCloud,
            ServerMode.LocalStandalone => HubIconKind.ServerLocal,
            ServerMode.CustomServer => HubIconKind.ServerCustom,
            _ => HubIconKind.Official
        };

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(216, 108);
    }

    public ServerMode Mode { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CardTitle
    {
        get => _title;
        set { _title = value; Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string CardSubtitle
    {
        get => _subtitle;
        set { _subtitle = value; Invalidate(); }
    }

    public void SetProbeStatus(Color lampColor, string statusText)
    {
        _lampColor = lampColor;
        _statusText = statusText;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new(1f, 1f, Math.Max(2f, Width - 3f), Math.Max(2f, Height - 3f));
        float r = HubTheme.ScaleF(this, 10);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        Color topColor = _selected
            ? ColorTranslator.FromHtml("#25231D")
            : (_hovered ? ColorTranslator.FromHtml("#1D2434") : ColorTranslator.FromHtml("#121722"));
        Color botColor = _selected
            ? ColorTranslator.FromHtml("#181B24")
            : (_hovered ? ColorTranslator.FromHtml("#171D2B") : ColorTranslator.FromHtml("#0F131C"));

        using (var grad = new LinearGradientBrush(rect, topColor, botColor, LinearGradientMode.Vertical))
        {
            g.FillPath(grad, path);
        }

        Color border = _selected
            ? HubTheme.Gold
            : (_hovered ? HubTheme.GoldDark : HubTheme.BorderSubtle);
        using (var pen = new Pen(border, _selected ? 1.55f : 1.0f))
        {
            g.DrawPath(pen, path);
        }

        int pad = HubTheme.Scale(this, 10);
        int topY = HubTheme.Scale(this, 11);

        // 顶部左侧图标
        int iconSz = HubTheme.Scale(this, 15);
        HubVectorIcons.Draw(
            g,
            _icon,
            new RectangleF(pad, topY + HubTheme.Scale(this, 2), iconSz, iconSz),
            _selected ? HubTheme.GoldLight : HubTheme.TextSecondary,
            1.6f);

        // 右上角“当前”金标
        int badgeReserve = 0;
        if (_selected)
        {
            int bw = HubTheme.Scale(this, 34);
            int bh = HubTheme.Scale(this, 18);
            RectangleF badge = new(Width - pad - bw, topY + 1, bw, bh);
            using GraphicsPath bp = HubTheme.CreateRoundedRectPath(badge, bh * 0.5f);
            using var bb = new SolidBrush(HubTheme.Gold);
            g.FillPath(bb, bp);
            TextRenderer.DrawText(
                g,
                "当前",
                HubTheme.FontCaption,
                Rectangle.Round(badge),
                HubTheme.TextOnGold,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            badgeReserve = bw + HubTheme.Scale(this, 5);
        }

        // 标题
        int titleX = pad + iconSz + HubTheme.Scale(this, 6);
        int titleH = HubTheme.Scale(this, 22);
        Rectangle titleRect = new(titleX, topY, Math.Max(10, Width - titleX - pad - badgeReserve), titleH);
        TextRenderer.DrawText(
            g,
            _title,
            HubTheme.FontCardTitle,
            titleRect,
            _selected ? HubTheme.GoldBright : HubTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

        // 副标题
        int subY = topY + titleH + HubTheme.Scale(this, 4);
        int subH = HubTheme.Scale(this, 20);
        Rectangle subRect = new(pad, subY, Math.Max(10, Width - pad * 2), subH);
        TextRenderer.DrawText(
            g,
            _subtitle,
            HubTheme.FontSmall,
            subRect,
            HubTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        // 底部分割微线 + 实时指示灯与文字
        int statusH = HubTheme.Scale(this, 20);
        int statusY = Height - pad - statusH + HubTheme.Scale(this, 2);
        using (var sep = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
        {
            g.DrawLine(sep, pad, statusY - HubTheme.Scale(this, 5), Width - pad, statusY - HubTheme.Scale(this, 5));
        }

        float dot = HubTheme.ScaleF(this, 7);
        float halo = dot + 5f;
        float lampCy = statusY + statusH * 0.5f;
        using (var hb = new SolidBrush(Color.FromArgb(65, _lampColor)))
        {
            g.FillEllipse(hb, pad - 1f, lampCy - halo * 0.5f, halo, halo);
        }
        using (var lb = new SolidBrush(_lampColor))
        {
            g.FillEllipse(lb, pad + 1.5f, lampCy - dot * 0.5f, dot, dot);
        }

        int stX = pad + (int)Math.Ceiling(dot) + HubTheme.Scale(this, 8);
        Rectangle statusRect = new(stX, statusY, Math.Max(10, Width - stX - pad), statusH);
        TextRenderer.DrawText(
            g,
            _statusText,
            HubTheme.FontSmall,
            statusRect,
            _lampColor == HubTheme.TextMuted ? HubTheme.TextSecondary : _lampColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }
}

/// <summary>
/// 性能预设分档卡片（支持标题、角标、多行说明与选中暖金态）。
/// </summary>
public sealed class PresetOptionCard : Control
{
    private bool _selected;
    private bool _hovered;
    private readonly HubIconKind _icon;
    private readonly string _title;
    private readonly string _badge;
    private readonly string _description;
    private readonly Color _accent;

    public PresetOptionCard(HubIconKind icon, string title, string badge, string description, Color accent)
    {
        _icon = icon;
        _title = title;
        _badge = badge;
        _description = description;
        _accent = accent;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Size = new Size(260, 96);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { _selected = value; Invalidate(); }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        RectangleF rect = new(1f, 1f, Math.Max(2f, Width - 3f), Math.Max(2f, Height - 3f));
        float r = HubTheme.ScaleF(this, 10);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, r);

        Color top = _selected
            ? ColorTranslator.FromHtml("#22252F")
            : (_hovered ? HubTheme.Surface3 : HubTheme.Surface2Top);
        Color bot = _selected
            ? ColorTranslator.FromHtml("#161A23")
            : (_hovered ? HubTheme.Surface2 : ColorTranslator.FromHtml("#121722"));

        using (var grad = new LinearGradientBrush(rect, top, bot, LinearGradientMode.Vertical))
        {
            g.FillPath(grad, path);
        }

        if (_selected)
        {
            using var tint = new SolidBrush(Color.FromArgb(20, _accent));
            g.FillPath(tint, path);
        }

        Color border = _selected ? _accent : (_hovered ? HubTheme.BorderHover : HubTheme.BorderSubtle);
        using (var pen = new Pen(border, _selected ? 1.5f : 1.0f))
        {
            g.DrawPath(pen, path);
        }

        int pad = HubTheme.Scale(this, 13);
        int topY = HubTheme.Scale(this, 11);
        int iconSz = HubTheme.Scale(this, 16);
        HubVectorIcons.Draw(g, _icon, new RectangleF(pad, topY + 2, iconSz, iconSz), _selected ? _accent : HubTheme.TextSecondary, 1.6f);

        int badgeW = HubTheme.Scale(this, 56);
        int badgeH = HubTheme.Scale(this, 18);
        RectangleF badgeRect = new(Width - pad - badgeW, topY + 1, badgeW, badgeH);
        using (var bp = HubTheme.CreateRoundedRectPath(badgeRect, badgeH * 0.5f))
        {
            using var bf = new SolidBrush(_selected ? _accent : Color.FromArgb(28, _accent));
            g.FillPath(bf, bp);
        }
        TextRenderer.DrawText(
            g,
            _badge,
            HubTheme.FontCaption,
            Rectangle.Round(badgeRect),
            _selected ? HubTheme.TextOnGold : _accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        int titleX = pad + iconSz + HubTheme.Scale(this, 7);
        int titleH = HubTheme.Scale(this, 22);
        Rectangle titleRect = new(titleX, topY, Math.Max(10, Width - titleX - pad - badgeW - 6), titleH);
        TextRenderer.DrawText(
            g,
            _title,
            HubTheme.FontCardTitle,
            titleRect,
            _selected ? HubTheme.TextPrimary : HubTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

        int descY = topY + titleH + HubTheme.Scale(this, 5);
        Rectangle descRect = new(pad, descY, Math.Max(10, Width - pad * 2), Math.Max(16, Height - descY - pad + 2));
        TextRenderer.DrawText(
            g,
            _description,
            HubTheme.FontSmall,
            descRect,
            HubTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 深色鎏金圆角进度条（平滑渐变 + 顶光泽 + 可选百分比数字）。
/// </summary>
public sealed class GoldProgressBar : Control
{
    private int _value;
    private bool _showPercentage;

    public GoldProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.SupportsTransparentBackColor,
            true);
        BackColor = Color.Transparent;
        Size = new Size(300, 14);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            _value = Math.Clamp(value, 0, 100);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowPercentage
    {
        get => _showPercentage;
        set { _showPercentage = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        int pctReserve = _showPercentage ? HubTheme.Scale(this, 40) : 0;
        int trackW = Math.Max(16, Width - pctReserve);
        int trackH = Math.Min(Height, HubTheme.Scale(this, 9));
        int trackY = (Height - trackH) / 2;

        RectangleF track = new(0.5f, trackY + 0.5f, trackW - 1f, trackH - 1f);
        float radius = track.Height * 0.5f;
        using GraphicsPath trackPath = HubTheme.CreateRoundedRectPath(track, radius);
        using (var bg = new SolidBrush(HubTheme.BgInput))
        {
            g.FillPath(bg, trackPath);
        }
        using (var border = new Pen(HubTheme.BorderSubtle, 1f))
        {
            g.DrawPath(border, trackPath);
        }

        if (_value > 0 && trackW > 8)
        {
            float fillW = Math.Max(trackH, (trackW - 2f) * (_value / 100f));
            RectangleF fillRect = new(1.2f, trackY + 1.2f, Math.Min(fillW, trackW - 2.4f), Math.Max(2f, trackH - 2.4f));
            using GraphicsPath fillPath = HubTheme.CreateRoundedRectPath(fillRect, fillRect.Height * 0.5f);
            using var grad = new LinearGradientBrush(fillRect, HubTheme.GoldDark, HubTheme.GoldBright, LinearGradientMode.Horizontal);
            g.FillPath(grad, fillPath);
        }

        if (_showPercentage)
        {
            Rectangle pctRect = new(trackW + HubTheme.Scale(this, 6), 0, Math.Max(10, pctReserve - HubTheme.Scale(this, 6)), Height);
            TextRenderer.DrawText(
                g,
                $"{_value}%",
                HubTheme.FontSmallBold,
                pctRect,
                _value > 0 ? HubTheme.GoldLight : HubTheme.TextMuted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>
/// 统一页面顶部标题栏（TableLayoutPanel 布局，彻底消灭硬编码坐标错位）。
/// </summary>
public sealed class SectionHeaderBar : Panel
{
    public SectionHeaderBar(string title, string subtitle, params Control[] rightActions)
    {
        Dock = DockStyle.Fill;
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 10);

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = rightActions.Length > 0 ? 2 : 1,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        if (rightActions.Length > 0)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        }

        var titleStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        titleStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleStack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        titleStack.Controls.Add(new Label
        {
            Text = title,
            Font = HubTheme.FontPageTitle,
            ForeColor = HubTheme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2)
        }, 0, 0);

        titleStack.Controls.Add(new Label
        {
            Text = subtitle,
            Font = HubTheme.FontSmall,
            ForeColor = HubTheme.TextSecondary,
            AutoSize = true,
            Margin = new Padding(0)
        }, 0, 1);

        grid.Controls.Add(titleStack, 0, 0);

        if (rightActions.Length > 0)
        {
            var actionsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                BackColor = Color.Transparent,
                Margin = new Padding(12, 6, 0, 0)
            };
            foreach (Control action in rightActions)
            {
                actionsFlow.Controls.Add(action);
            }
            grid.Controls.Add(actionsFlow, 1, 0);
        }

        Controls.Add(grid);
    }
}

/// <summary>
/// 统一设置项行组件（左侧标题+说明，右侧对齐开关或选择器，可选底部细分割线）。
/// </summary>
public sealed class SettingRowControl : Panel
{
    private readonly bool _showDivider;

    public SettingRowControl(string title, string description, Control trailingControl, bool showDivider = true, bool emphasizeTitle = false)
    {
        _showDivider = showDivider;
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Color.Transparent;
        Padding = new Padding(0, 7, 0, showDivider ? 9 : 4);
        Margin = new Padding(0);

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var leftStack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = string.IsNullOrWhiteSpace(description) ? 1 : 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 16, 0)
        };
        leftStack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        leftStack.Controls.Add(new Label
        {
            Text = title,
            Font = emphasizeTitle ? HubTheme.FontBodyBold : HubTheme.FontBody,
            ForeColor = emphasizeTitle ? HubTheme.GoldLight : HubTheme.TextPrimary,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 1, 0, 2)
        }, 0, 0);

        if (!string.IsNullOrWhiteSpace(description))
        {
            leftStack.Controls.Add(new Label
            {
                Text = description,
                Font = HubTheme.FontSmall,
                ForeColor = HubTheme.TextMuted,
                AutoSize = true,
                Dock = DockStyle.Top,
                Margin = new Padding(0)
            }, 0, 1);
        }

        trailingControl.Anchor = AnchorStyles.Right;
        trailingControl.Margin = new Padding(8, 2, 0, 0);

        row.Controls.Add(leftStack, 0, 0);
        row.Controls.Add(trailingControl, 1, 0);
        Controls.Add(row);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_showDivider)
        {
            using var pen = new Pen(HubTheme.BorderHairline, 1f);
            e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}

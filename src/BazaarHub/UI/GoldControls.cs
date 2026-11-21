using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using BazaarHub.Models;

namespace BazaarHub.UI;

/// <summary>
/// 按钮视觉变体。
/// </summary>
public enum GoldButtonVariant
{
    PrimaryGold,
    OutlineGold,
    SubtleDark,
    DangerRed
}

/// <summary>
/// 高颜值深色鎏金圆角按钮（双缓冲无闪烁、支持悬停/按下态与禁用态）。
/// </summary>
public sealed class GoldButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private GoldButtonVariant _variant = GoldButtonVariant.OutlineGold;
    private int _cornerRadius = 8;

    public GoldButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint |
            ControlStyles.Selectable,
            true);

        Font = HubTheme.FontBodyBold;
        Cursor = Cursors.Hand;
        Size = new Size(136, 36);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GoldButtonVariant Variant
    {
        get => _variant;
        set
        {
            _variant = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(2, value);
            Invalidate();
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

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Rectangle rect = new(1, 1, Width - 3, Height - 3);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, _cornerRadius);

        Color textColor;
        Color borderColor;

        if (!Enabled)
        {
            using var disabledBrush = new SolidBrush(ColorTranslator.FromHtml("#1B212E"));
            g.FillPath(disabledBrush, path);
            borderColor = HubTheme.BorderMuted;
            textColor = HubTheme.TextMuted;
        }
        else
        {
            switch (_variant)
            {
                case GoldButtonVariant.PrimaryGold:
                {
                    Color top = _pressed ? HubTheme.GoldDark : (_hovered ? HubTheme.GoldLight : HubTheme.Gold);
                    Color bottom = _pressed ? ColorTranslator.FromHtml("#8C6C15") : (_hovered ? HubTheme.Gold : HubTheme.GoldDark);
                    using var grad = new LinearGradientBrush(rect, top, bottom, LinearGradientMode.Vertical);
                    g.FillPath(grad, path);
                    borderColor = HubTheme.GoldLight;
                    textColor = HubTheme.TextOnGold;
                    break;
                }

                case GoldButtonVariant.DangerRed:
                {
                    Color bg = _pressed ? ColorTranslator.FromHtml("#4A2027") : (_hovered ? HubTheme.DangerHoverBg : HubTheme.DangerBg);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = HubTheme.StatusRed;
                    textColor = ColorTranslator.FromHtml("#FFB4AD");
                    break;
                }

                case GoldButtonVariant.SubtleDark:
                {
                    Color bg = _pressed ? HubTheme.BgInput : (_hovered ? HubTheme.BgCardHover : HubTheme.BgCard);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = _hovered ? HubTheme.Gold : HubTheme.BorderSubtle;
                    textColor = HubTheme.TextPrimary;
                    break;
                }

                default: // OutlineGold
                {
                    Color bg = _pressed
                        ? Color.FromArgb(45, 212, 175, 55)
                        : (_hovered ? Color.FromArgb(30, 212, 175, 55) : HubTheme.BgCard);
                    using var brush = new SolidBrush(bg);
                    g.FillPath(brush, path);
                    borderColor = _hovered ? HubTheme.GoldLight : HubTheme.Gold;
                    textColor = HubTheme.GoldLight;
                    break;
                }
            }
        }

        using (var pen = new Pen(borderColor, _variant == GoldButtonVariant.PrimaryGold ? 1.2f : 1.1f))
        {
            g.DrawPath(pen, path);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            rect,
            textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 深色圆角鎏金描边卡片容器（双缓冲）。
/// </summary>
public sealed class GoldCardPanel : Panel
{
    private bool _highlighted;
    private int _cornerRadius = 10;
    private Color _cardBackColor = HubTheme.BgCard;

    public GoldCardPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        BackColor = HubTheme.BgWindow;
        Padding = new Padding(16);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Highlighted
    {
        get => _highlighted;
        set
        {
            _highlighted = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int CornerRadius
    {
        get => _cornerRadius;
        set
        {
            _cornerRadius = Math.Max(2, value);
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color CardBackColor
    {
        get => _cardBackColor;
        set
        {
            _cardBackColor = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using (var outer = new SolidBrush(Parent?.BackColor ?? HubTheme.BgWindow))
        {
            g.FillRectangle(outer, ClientRectangle);
        }

        Rectangle rect = new(1, 1, Width - 3, Height - 3);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, _cornerRadius);
        using (var fill = new SolidBrush(_cardBackColor))
        {
            g.FillPath(fill, path);
        }

        Color border = _highlighted ? HubTheme.Gold : HubTheme.BorderSubtle;
        using var pen = new Pen(border, _highlighted ? 1.6f : 1.0f);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// 左侧导航栏高颜值切换按钮（选中时带左侧鎏金竖条与暖金底光）。
/// </summary>
public sealed class NavSidebarButton : Control
{
    private bool _selected;
    private bool _hovered;
    private string _iconText = "◆";

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
        Size = new Size(196, 44);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconText
    {
        get => _iconText;
        set
        {
            _iconText = value;
            Invalidate();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(HubTheme.BgSidebar))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        Rectangle pill = new(8, 2, Width - 16, Height - 4);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(pill, 8);

        if (_selected)
        {
            using var selFill = new SolidBrush(Color.FromArgb(32, 212, 175, 55));
            g.FillPath(selFill, path);
            using var selBorder = new Pen(Color.FromArgb(120, 212, 175, 55), 1f);
            g.DrawPath(selBorder, path);

            // 左侧鎏金指示条
            using var accentBar = new SolidBrush(HubTheme.Gold);
            g.FillRectangle(accentBar, 8, 9, 4, Height - 18);
        }
        else if (_hovered)
        {
            using var hovFill = new SolidBrush(Color.FromArgb(24, 255, 255, 255));
            g.FillPath(hovFill, path);
        }

        Color iconColor = _selected ? HubTheme.GoldLight : HubTheme.TextSecondary;
        Color textColor = _selected ? HubTheme.GoldLight : (_hovered ? HubTheme.TextPrimary : HubTheme.TextSecondary);

        Rectangle iconRect = new(20, 0, 26, Height);
        TextRenderer.DrawText(g, _iconText, Font, iconRect, iconColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        Rectangle labelRect = new(48, 0, Width - 58, Height);
        TextRenderer.DrawText(g, Text, Font, labelRect, textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 服务器模式选择卡片（内置实时绿/黄/红延迟灯与延迟文本）。
/// </summary>
public sealed class ServerModeCard : Control
{
    private bool _selected;
    private bool _hovered;
    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _statusText = "等待检测...";
    private Color _lampColor = HubTheme.TextMuted;

    public ServerModeCard(ServerMode mode, string title, string subtitle)
    {
        Mode = mode;
        _title = title;
        _subtitle = subtitle;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        Cursor = Cursors.Hand;
        Size = new Size(210, 96);
    }

    public ServerMode Mode { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Invalidate();
        }
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
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(Parent?.BackColor ?? HubTheme.BgCard))
        {
            g.FillRectangle(bg, ClientRectangle);
        }

        Rectangle rect = new(1, 1, Width - 3, Height - 3);
        using GraphicsPath path = HubTheme.CreateRoundedRectPath(rect, 10);

        Color fill = _selected
            ? HubTheme.BgCardSelected
            : (_hovered ? HubTheme.BgCardHover : ColorTranslator.FromHtml("#141924"));

        using (var fillBrush = new SolidBrush(fill))
        {
            g.FillPath(fillBrush, path);
        }

        if (_selected)
        {
            using var glow = new SolidBrush(Color.FromArgb(18, 212, 175, 55));
            g.FillPath(glow, path);
        }

        using (var pen = new Pen(_selected ? HubTheme.Gold : (_hovered ? HubTheme.GoldDark : HubTheme.BorderSubtle), _selected ? 1.8f : 1.0f))
        {
            g.DrawPath(pen, path);
        }

        // 右上角选中角标
        if (_selected)
        {
            Rectangle badge = new(Width - 52, 8, 42, 18);
            using GraphicsPath bp = HubTheme.CreateRoundedRectPath(badge, 6);
            using var bb = new SolidBrush(HubTheme.Gold);
            g.FillPath(bb, bp);
            TextRenderer.DrawText(g, "当前", HubTheme.FontSmall, badge, HubTheme.TextOnGold, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // 标题与副标题
        Rectangle titleRect = new(12, 10, Width - 66, 22);
        TextRenderer.DrawText(
            g,
            _title,
            HubTheme.FontSectionTitle,
            titleRect,
            _selected ? HubTheme.GoldLight : HubTheme.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        Rectangle subRect = new(12, 34, Width - 24, 20);
        TextRenderer.DrawText(
            g,
            _subtitle,
            HubTheme.FontSmall,
            subRect,
            HubTheme.TextSecondary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 底部状态指示灯 + 状态文字
        int lampY = Height - 26;
        using (var halo = new SolidBrush(Color.FromArgb(65, _lampColor)))
        {
            g.FillEllipse(halo, 11, lampY - 2, 14, 14);
        }
        using (var lamp = new SolidBrush(_lampColor))
        {
            g.FillEllipse(lamp, 13, lampY, 10, 10);
        }

        Rectangle statusRect = new(29, lampY - 3, Width - 38, 18);
        TextRenderer.DrawText(
            g,
            _statusText,
            HubTheme.FontSmall,
            statusRect,
            _lampColor == HubTheme.TextMuted ? HubTheme.TextSecondary : _lampColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 深色鎏金圆角进度条。
/// </summary>
public sealed class GoldProgressBar : Control
{
    private int _value;

    public GoldProgressBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);
        Size = new Size(300, 10);
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

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle track = new(0, 0, Width - 1, Height - 1);
        using GraphicsPath trackPath = HubTheme.CreateRoundedRectPath(track, Height / 2);
        using (var bg = new SolidBrush(HubTheme.BgInput))
        {
            g.FillPath(bg, trackPath);
        }
        using (var border = new Pen(HubTheme.BorderSubtle, 1f))
        {
            g.DrawPath(border, trackPath);
        }

        if (_value > 0 && Width > 6)
        {
            int fillWidth = Math.Max(Height, (int)((Width - 2) * (_value / 100.0)));
            Rectangle fillRect = new(1, 1, Math.Min(fillWidth, Width - 2), Height - 2);
            using GraphicsPath fillPath = HubTheme.CreateRoundedRectPath(fillRect, (Height - 2) / 2);
            using var grad = new LinearGradientBrush(fillRect, HubTheme.GoldLight, HubTheme.GoldDark, LinearGradientMode.Horizontal);
            g.FillPath(grad, fillPath);
        }
    }
}

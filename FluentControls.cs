using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>WinUI 风格按钮：32px 高、4px 圆角、支持图标与强调/危险语义。</summary>
[DesignerCategory("Code")]
internal sealed class FluentButton : Control
{
    private bool _hovered;
    private bool _pressed;

    public FluentButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        ForeColor = Fluent.TextPrimary;
        Font = Fluent.Font(TextRole.Body);
        Cursor = Cursors.Hand;
        Height = 32;
        Width = 88;
        Fluent.Changed += OnThemeChanged;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public SurfaceRole Role { get; set; } = SurfaceRole.Standard;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? IconName { get; set; }

    /// <summary>主按钮（Enter 触发）用强调色描边提示。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsDefault { get; set; }

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
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
        var g = e.Graphics;
        Fluent.AntiAlias(g);

        var state = !Enabled ? VisualState.Disabled
            : _pressed ? VisualState.Pressed
            : _hovered ? VisualState.Hover
            : VisualState.Rest;

        var scale = DeviceDpi / 96f;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Fluent.Button(g, rect, Text, Role, state, IconName, scale);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// WinUI 输入框：外框由本控件绘制（1px 描边 + 聚焦时 2px 强调下划线），
/// 内层是无边框的原生 TextBox，负责输入、选区和输入法。
/// </summary>
[DesignerCategory("Code")]
internal sealed class FluentTextBox : Control
{
    private readonly TextBox _inner;
    private bool _hovered;

    public FluentTextBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = Fluent.ControlFill;

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Fluent.ControlFill,
            ForeColor = Fluent.TextPrimary,
            Font = Fluent.Font(TextRole.Body),
        };

        _inner.GotFocus += (_, _) => Invalidate();
        _inner.LostFocus += (_, _) => Invalidate();

        Controls.Add(_inner);
        Height = 32;
        Fluent.Changed += OnThemeChanged;
    }

    /// <summary>输入内容（用 Value 而不是 Text，避免和 Control.Text 的事件语义纠缠）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string PlaceholderText
    {
        get => _inner.PlaceholderText;
        set => _inner.PlaceholderText = value;
    }

    public void FocusInput() => _inner.Focus();

    private void OnThemeChanged()
    {
        BackColor = Fluent.ControlFill;
        _inner.BackColor = Fluent.ControlFill;
        _inner.ForeColor = Fluent.TextPrimary;
        if (!IsDisposed)
        {
            Invalidate();
        }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var scale = DeviceDpi / 96f;
        var padX = (int)MathF.Round(11 * scale);
        var innerHeight = Math.Max(14, _inner.PreferredHeight);
        _inner.Bounds = new Rectangle(
            padX,
            (Height - innerHeight) / 2,
            Math.Max(0, Width - (2 * padX)),
            innerHeight);
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
        _inner.Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g);

        var scale = DeviceDpi / 96f;
        var focused = _inner.Focused;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);

        using var path = Fluent.Rounded(rect, Fluent.RControl);
        Fluent.Fill(g, path, Fluent.ControlFill);
        Fluent.Stroke(g, path, focused || _hovered ? Fluent.StrokeStrong : Fluent.StrokeColor, 1f);

        if (focused)
        {
            // 强调下划线：裁进圆角里，避免盖住两端的圆角。
            var state = g.Save();
            g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
            using var brush = new SolidBrush(Fluent.AccentFill);
            g.FillRectangle(brush, rect.X, rect.Bottom - (2 * scale), rect.Width, 2 * scale);
            g.Restore(state);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnThemeChanged;
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

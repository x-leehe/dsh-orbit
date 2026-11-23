using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 设置面板的开关行：左侧标签 + 说明（12 tertiary），右侧 WinUI 开关（40×20）。
/// 整行可点、可键盘聚焦（Tab + Space/Enter），不依赖灰色小方框。
/// </summary>
[DesignerCategory("Code")]
internal sealed class SettingsToggle : Control
{
    private bool _checked;
    private bool _hovered;
    private bool _focused;

    public SettingsToggle()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Height = 52;
        TabStop = true;
        Cursor = Cursors.Hand;
        Fluent.Changed += OnThemeChanged;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Label { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Description { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)MathF.Round(value * DpiScale);

    private RectangleF SwitchRect()
    {
        var width = 40f * DpiScale;
        var height = 20f * DpiScale;
        return new RectangleF(Width - width, S(4), width, height);
    }

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
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _focused = true;
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        _focused = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            Checked = !Checked;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);
        var scale = DpiScale;
        var textWidth = Math.Max(0f, Width - (48f * scale));

        Fluent.Text(
            g,
            Label,
            TextRole.Body,
            false,
            Fluent.TextPrimary,
            new RectangleF(0, 0, textWidth, 22f * scale),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            scale);

        if (!string.IsNullOrEmpty(Description))
        {
            Fluent.Text(
                g,
                Description,
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(0, 22f * scale, textWidth, 18f * scale),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }

        var track = SwitchRect();
        var radius = track.Height / 2f;
        using (var path = Fluent.Rounded(track, radius))
        {
            Fluent.Fill(g, path, _checked ? Fluent.AccentFill : Fluent.ControlFill);
            Fluent.Stroke(g, path, _checked ? Fluent.AccentFill : Fluent.StrokeStrong, 1f);
        }

        var knob = 14f * scale;
        var knobX = _checked ? track.Right - knob - (3f * scale) : track.Left + (3f * scale);
        using (var brush = new SolidBrush(_checked ? Fluent.OnAccent : Fluent.TextSecondary))
        {
            g.FillEllipse(brush, knobX, track.Top + ((track.Height - knob) / 2f), knob, knob);
        }

        if (_hovered || _focused)
        {
            using var path = Fluent.Rounded(RectangleF.Inflate(track, 3f * scale, 3f * scale), radius + (3f * scale));
            Fluent.Stroke(g, path, Fluent.WithAlpha(Fluent.AccentText, _focused ? 0.9f : 0.35f), MathF.Max(1f, scale));
        }
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

/// <summary>设置面板的分段控件（3–4 格）：复用 Fluent.Segmented 的视觉，加悬停/键盘。</summary>
[DesignerCategory("Code")]
internal sealed class SettingsSegmented : Control
{
    private int _selected;
    private int _hover = -1;

    public SettingsSegmented()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Height = 32;
        TabStop = true;
        Cursor = Cursors.Hand;
        Fluent.Changed += OnThemeChanged;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public IReadOnlyList<string> Labels { get; set; } = [];

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selected;
        set
        {
            var clamped = Labels.Count == 0 ? 0 : Math.Clamp(value, 0, Labels.Count - 1);
            if (_selected == clamped)
            {
                return;
            }

            _selected = clamped;
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectionChanged;

    private float DpiScale => DeviceDpi / 96f;

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            Invalidate();
        }
    }

    private int IndexAt(Point point)
    {
        if (Labels.Count == 0 || Width <= 0)
        {
            return -1;
        }

        var index = point.X * Labels.Count / Width;
        return Math.Clamp(index, 0, Labels.Count - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.Location);
        if (index != _hover)
        {
            _hover = index;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        SelectedIndex = IndexAt(e.Location);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left)
        {
            e.Handled = true;
            SelectedIndex--;
        }
        else if (e.KeyCode == Keys.Right)
        {
            e.Handled = true;
            SelectedIndex++;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);
        var scale = DpiScale;
        var focused = Focused;
        var hover = _hover;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Fluent.Segmented(g, rect, Labels, _selected, hover, scale);

        if (focused)
        {
            using var path = Fluent.Rounded(RectangleF.Inflate(rect, 2f * scale, 2f * scale), Fluent.RControl + (2f * scale));
            Fluent.Stroke(g, path, Fluent.WithAlpha(Fluent.AccentText, 0.8f), MathF.Max(1f, scale));
        }
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
/// 设置面板的数值滑杆（不透明度）：左侧标签、中间轨道、右侧百分比。
/// 拖动 / 点轨道 / 左右键都能改，步进 0.05。
/// </summary>
[DesignerCategory("Code")]
internal sealed class SettingsSlider : Control
{
    private double _value = 1.0;
    private bool _dragging;

    public SettingsSlider()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.Selectable |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Height = 40;
        TabStop = true;
        Cursor = Cursors.Hand;
        Fluent.Changed += OnThemeChanged;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Label { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Minimum { get; set; } = 0.3;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Maximum { get; set; } = 1.0;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Step { get; set; } = 0.05;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            var rounded = Math.Round(clamped / Step) * Step;
            rounded = Math.Clamp(rounded, Minimum, Maximum);
            if (Math.Abs(_value - rounded) < 0.0001)
            {
                return;
            }

            _value = rounded;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ValueChanged;

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)MathF.Round(value * DpiScale);

    private RectangleF TrackRect()
    {
        var left = 116f * DpiScale;
        var right = Width - (66f * DpiScale);
        var height = Math.Max(4f, 4f * DpiScale);
        return new RectangleF(left, (Height - height) / 2f, Math.Max(24f, right - left), height);
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            Invalidate();
        }
    }

    private void SetFromPoint(int x)
    {
        var track = TrackRect();
        var ratio = Math.Clamp((x - track.Left) / Math.Max(1f, track.Width), 0f, 1f);
        Value = Minimum + ((Maximum - Minimum) * ratio);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        Focus();
        _dragging = true;
        SetFromPoint(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetFromPoint(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Left:
                e.Handled = true;
                Value -= Step;
                break;
            case Keys.Right:
                e.Handled = true;
                Value += Step;
                break;
            case Keys.Home:
                e.Handled = true;
                Value = Minimum;
                break;
            case Keys.End:
                e.Handled = true;
                Value = Maximum;
                break;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);
        var scale = DpiScale;

        Fluent.Text(
            g,
            Label,
            TextRole.Body,
            false,
            Fluent.TextPrimary,
            new RectangleF(0, 0, 108f * scale, Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            scale);

        var track = TrackRect();
        var ratio = (float)((Value - Minimum) / Math.Max(0.0001, Maximum - Minimum));
        using (var path = Fluent.Rounded(track, track.Height / 2f))
        {
            Fluent.Fill(g, path, Fluent.ControlFill);
        }

        var filled = new RectangleF(track.X, track.Y, Math.Max(track.Height, track.Width * ratio), track.Height);
        using (var path = Fluent.Rounded(filled, track.Height / 2f))
        {
            Fluent.Fill(g, path, Fluent.AccentFill);
        }

        var thumb = 16f * scale;
        var thumbX = track.X + (track.Width * ratio) - (thumb / 2f);
        thumbX = Math.Clamp(thumbX, track.X - (thumb / 2f), track.Right - (thumb / 2f));
        using (var brush = new SolidBrush(Fluent.AccentFill))
        {
            g.FillEllipse(brush, thumbX, (Height - thumb) / 2f, thumb, thumb);
        }

        Fluent.Text(
            g,
            $"{Math.Round(Value * 100)}%",
            TextRole.Caption,
            false,
            Fluent.TextSecondary,
            new RectangleF(Width - (60f * scale), 0, 60f * scale, Height),
            StringAlignment.Far,
            StringTrimming.None,
            scale);

        if (Focused)
        {
            using var path = Fluent.Rounded(RectangleF.Inflate(track, 8f * scale, 10f * scale), Fluent.RControl);
            Fluent.Stroke(g, path, Fluent.WithAlpha(Fluent.AccentText, 0.7f), MathF.Max(1f, scale));
        }
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
/// 设置面板的文本字段：12 tertiary 标签 + 32px 输入框（可选「浏览…」按钮）。
/// 输入/选区/输入法交给内部的 <see cref="FluentTextBox"/>，这里只负责排版与标签。
/// </summary>
[DesignerCategory("Code")]
internal sealed class SettingsField : Control
{
    private readonly FluentTextBox _input = new();
    private readonly FluentButton _browse = new() { Text = "浏览…", Width = 76 };

    public SettingsField()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        BackColor = Color.Transparent;
        Height = 56;

        _browse.Visible = false;
        _browse.Click += (_, _) => BrowseRequested?.Invoke(this, EventArgs.Empty);

        Controls.Add(_input);
        Controls.Add(_browse);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Label { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _input.PlaceholderText;
        set => _input.PlaceholderText = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _input.Value;
        set => _input.Value = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Password
    {
        get => _input.Password;
        set => _input.Password = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowBrowse
    {
        get => _browse.Visible;
        set
        {
            if (_browse.Visible == value)
            {
                return;
            }

            _browse.Visible = value;
            LayoutChildren();
        }
    }

    public event EventHandler? BrowseRequested;

    public void FocusInput() => _input.FocusInput();

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)MathF.Round(value * DpiScale);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutChildren();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        var top = S(20);
        var buttonWidth = _browse.Visible ? S(76) : 0;
        var gap = _browse.Visible ? S(8) : 0;
        var inputWidth = Math.Max(0, Width - buttonWidth - gap);

        _input.Bounds = new Rectangle(0, top, inputWidth, S(32));
        if (_browse.Visible)
        {
            _browse.Bounds = new Rectangle(Width - buttonWidth, top, buttonWidth, S(32));
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);

        Fluent.Text(
            g,
            Label,
            TextRole.Caption,
            false,
            Fluent.TextTertiary,
            new RectangleF(0, 0, Width, S(18)),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            DpiScale);
    }
}


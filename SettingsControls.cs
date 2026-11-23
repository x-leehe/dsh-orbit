using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 设置面板的开关行（WinUI ToggleSwitch 形态）：左侧 14px 标签 + 12px 说明，右侧 40×20 开关。
/// 整行 64px、可键盘聚焦（Tab + Space/Enter）；滑块恒为近白，暗色主题下也是 WinUI 的观感。
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
        Height = 64;
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
        return new RectangleF(Width - width, (Height - height) / 2f, width, height);
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
            new RectangleF(0, 10f * scale, textWidth, 22f * scale),
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
                new RectangleF(0, 30f * scale, textWidth, 18f * scale),
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

            if (_hovered && !_checked)
            {
                var state = g.Save();
                g.SetClip(path, System.Drawing.Drawing2D.CombineMode.Intersect);
                Fluent.Fill(g, track, Fluent.HoverLayer);
                g.Restore(state);
            }
        }

        var knob = 14f * scale;
        var knobX = _checked ? track.Right - knob - (3f * scale) : track.Left + (3f * scale);
        using (var brush = new SolidBrush(_checked ? Fluent.SwitchKnob : Fluent.TextSecondary))
        {
            g.FillEllipse(brush, knobX, track.Top + ((track.Height - knob) / 2f), knob, knob);
        }

        if (_focused)
        {
            using var ring = Fluent.Rounded(RectangleF.Inflate(track, 2f * scale, 2f * scale), radius + (2f * scale));
            Fluent.Stroke(g, ring, Fluent.AccentText, MathF.Max(1f, 2f * scale));
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
/// 设置面板的分段选择行：左侧 14px 标签，右侧 32px 的 Fluent 分段控件（3 格 268×32）。
/// 视觉与托盘菜单里的分段控件同源（`Fluent.Segmented`），只是把标签挪到行首，像 WinUI 的 ComboBox 行。
/// </summary>
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
        Height = 64;
        TabStop = true;
        Cursor = Cursors.Hand;
        Fluent.Changed += OnThemeChanged;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Label { get; set; } = "";

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

    private RectangleF SegRect()
    {
        var scale = DpiScale;
        var width = (Labels.Count <= 2 ? 200f : 268f) * scale;
        width = Math.Min(width, Width * 0.8f);
        return new RectangleF(Width - width, (Height - (32f * scale)) / 2f, width, 32f * scale);
    }

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            Invalidate();
        }
    }

    private int IndexAt(Point point)
    {
        if (Labels.Count == 0)
        {
            return -1;
        }

        var rect = SegRect();
        if (!rect.Contains(point))
        {
            return -1;
        }

        var index = (int)((point.X - rect.X) * Labels.Count / Math.Max(1f, rect.Width));
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
        if (e.Button != MouseButtons.Left || IndexAt(e.Location) < 0)
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
        var rect = SegRect();

        var labelWidth = Math.Max(0f, rect.X - (12f * scale));
        Fluent.Text(
            g,
            Label,
            TextRole.Body,
            false,
            Fluent.TextPrimary,
            new RectangleF(0, 0, labelWidth, Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            scale);

        Fluent.Segmented(g, rect, Labels, _selected, _hover, scale);

        if (Focused)
        {
            // 分段控件本身已有选中格的强调描边；这里只补一圈很淡的焦点提示，避免"双边框"。
            using var ring = Fluent.Rounded(RectangleF.Inflate(rect, 1f * scale, 1f * scale), Fluent.RControl + (1f * scale));
            Fluent.Stroke(g, ring, Fluent.WithAlpha(Fluent.AccentText, 0.5f), MathF.Max(1f, scale));
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
/// 设置面板的滑杆行：左侧 14px 标签、中间 4px 轨道 + 20px 滑块、右侧 12px 百分比；整行 64px。
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
        Height = 64;
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

    private RectangleF TrackRect()
    {
        var left = 120f * DpiScale;
        var right = Width - (60f * DpiScale);
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

        var thumb = 20f * scale;
        var thumbX = Math.Clamp(
            track.X + (track.Width * ratio) - (thumb / 2f),
            track.X - (thumb / 2f),
            track.Right - (thumb / 2f));
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
            new RectangleF(Width - (56f * scale), 0, 56f * scale, Height),
            StringAlignment.Far,
            StringTrimming.None,
            scale);

        if (Focused)
        {
            using var ring = Fluent.Rounded(
                RectangleF.Inflate(track, 8f * scale, 12f * scale),
                Fluent.RControl);
            Fluent.Stroke(g, ring, Fluent.AccentText, MathF.Max(1f, 2f * scale));
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
/// 设置面板的文本字段行：12px 标签 + 32px 输入框（可选「浏览…」按钮），整行 76px。
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
        Height = 76;

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
        var top = S(26);
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
            new RectangleF(0, 2f * DpiScale, Width, S(18)),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            DpiScale);
    }
}

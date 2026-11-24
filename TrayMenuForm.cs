using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 托盘右键菜单：WinUI MenuFlyout 风格的浮出层（300 设计宽 + 四周 10px 阴影边距）。
/// 结构：头部（标志 / 标题 / 状态药丸）→ 主操作行 → 三组「标签 + 分段控件」→ 菜单行。
/// 所有颜色取自 <see cref="Fluent"/> 令牌，几何按 DeviceDpi/96 缩放。
/// </summary>
internal sealed class TrayMenuForm : Form
{
    // ---------------------------------------------------------------- 布局常量（96 DPI）

    private const int CardWidth = 300;
    private const int ShadowMargin = 10;
    private const int Pad = 12;
    private const int HeaderHeight = 32;
    private const int ButtonHeight = 32;
    private const int SegmentHeight = 32;
    private const int RowHeight = 36;
    private const int SectionHeight = 16;
    private const int Gap = 8;
    private const int ActionGap = 12;

    private static readonly MenuLayout Plan = BuildLayout();

    private readonly MenuState _state;
    private readonly Action<string> _command;
    private readonly LayeredSurface _layered = new();
    private readonly List<MenuHit> _hits = new();

    private Bitmap? _surface;
    private string? _hover;
    private string? _pressed;
    private float _scale = 1f;

    public TrayMenuForm(MenuState state, Action<string> command)
    {
        _state = state;
        _command = command;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        AutoScaleMode = AutoScaleMode.None;
        Text = "DSH 菜单";
        KeyPreview = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        SyncSize();
        Deactivate += (_, _) => Close();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                                  Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            return parameters;
        }
    }

    /// <summary>在光标附近弹出，自动避开屏幕边缘。</summary>
    public void ShowAtCursor()
    {
        _ = Handle;
        SyncSize();
        RenderSurface();

        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;

        var x = cursor.X - Width + 20;
        var y = cursor.Y - Height - 6;
        if (y < screen.Top)
        {
            y = cursor.Y - 20;
        }

        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - Width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - Height));

        Location = new Point(x, y);
        RenderSurface();
        Show();
        Activate();
        Focus();
    }

    // ---------------------------------------------------------------- 尺寸 / DPI

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        SyncSize();
        RenderSurface();
    }

    private int S(int value) => (int)MathF.Round(value * _scale);

    private float Sf(float value) => value * _scale;

    /// <summary>设计矩形 → 物理像素矩形（四边各自取整，避免半像素边缘）。</summary>
    private RectangleF Scale(RectangleF rect) => new(
        MathF.Round(rect.X * _scale),
        MathF.Round(rect.Y * _scale),
        MathF.Round(rect.Width * _scale),
        MathF.Round(rect.Height * _scale));

    /// <summary>1px 分隔线：宽度按 DPI 缩放，高度恒为 1 物理像素。</summary>
    private RectangleF Flat(RectangleF rect) => new(
        MathF.Round(rect.X * _scale),
        MathF.Round(rect.Y * _scale),
        MathF.Round(rect.Width * _scale),
        1f);

    private void SyncSize()
    {
        _scale = DeviceDpi / 96f;
        var width = S(CardWidth) + (2 * S(ShadowMargin));
        var height = S(Plan.CardHeight) + (2 * S(ShadowMargin));
        if (ClientSize.Width != width || ClientSize.Height != height)
        {
            ClientSize = new Size(width, height);
        }
    }

    // ---------------------------------------------------------------- 渲染

    private void RenderSurface()
    {
        SyncSize();
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        var margin = S(ShadowMargin);
        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            Fluent.AntiAlias(g);

            var state = g.Save();
            g.TranslateTransform(margin, margin);
            DrawCard(g);
            g.Restore(state);
        }

        // 绘制时平移了阴影边距，命中区域要补回来。
        for (var i = 0; i < _hits.Count; i++)
        {
            var hit = _hits[i];
            _hits[i] = hit with
            {
                Bounds = new RectangleF(
                    hit.Bounds.X + margin,
                    hit.Bounds.Y + margin,
                    hit.Bounds.Width,
                    hit.Bounds.Height),
            };
        }

        if (IsHandleCreated)
        {
            _layered.Push(Handle, _surface, Left, Top, 1f);
        }
    }

    private void DrawCard(Graphics g)
    {
        _hits.Clear();

        var card = new RectangleF(0, 0, S(CardWidth), S(Plan.CardHeight));
        Fluent.Card(g, card, Sf(Fluent.RCard), 2, Fluent.Flyout);

        DrawHeader(g, Plan.Header);
        DrawActions(g, Plan.Actions);

        Fluent.Divider(g, Flat(Plan.Divider1));

        Fluent.SectionLabel(g, Scale(Plan.LayoutLabel), "位置", _scale);
        DrawSegmented(
            g,
            Plan.LayoutSegments,
            [
                (MenuCommand.LayoutBottom, "置底"),
                (MenuCommand.LayoutTop, "置顶"),
                (MenuCommand.LayoutNormal, "普通"),
            ],
            _state.LayoutBottom ? 0 : _state.LayoutTop ? 1 : 2);

        Fluent.SectionLabel(g, Scale(Plan.AutoStartLabel), "开机启动", _scale);
        DrawSegmented(
            g,
            Plan.AutoStartSegments,
            [
                (MenuCommand.AutoStartDisabled, "停用"),
                (MenuCommand.AutoStartEnabled, "启用"),
                (MenuCommand.AutoStartFull, "全自动"),
            ],
            (int)_state.AutoStart);

        Fluent.Divider(g, Flat(Plan.Divider2));
        DrawRows(g, Plan.Rows);
    }

    // ---------------------------------------------------------------- 头部

    private void DrawHeader(Graphics g, RectangleF band)
    {
        var scale = _scale;
        var header = Scale(band);

        var markSize = 18f * scale;
        var mark = new RectangleF(
            header.X,
            header.Y + ((header.Height - markSize) / 2f),
            markSize,
            markSize);
        OrbitMark.Draw(g, mark, Fluent.AccentText, MathF.Max(1f, 1.6f * scale), withWhale: false);

        var (statusText, statusForeground, statusBackground) = StatusStyle();
        var statusFont = Fluent.Font(TextRole.Caption, true, scale);
        var statusTextWidth = Fluent.Measure(g, statusText, statusFont);
        var pillWidth = (32f * scale) + statusTextWidth;
        var pillHeight = 22f * scale;
        // OrbitMark 的圆弧有约 2px 内缩，左内边距视觉上约 14；药丸右缘取 14 才和它对称。
        var pillRight = Sf(CardWidth - 14f);
        var pill = new RectangleF(
            pillRight - pillWidth,
            header.Y + ((header.Height - pillHeight) / 2f),
            pillWidth,
            pillHeight);
        Fluent.Pill(g, pill, statusText, statusForeground, statusBackground, dot: true, scale);

        var titleX = mark.Right + (8f * scale);
        Fluent.Text(
            g,
            "DSH-Orbit",
            TextRole.Body,
            strong: true,
            Fluent.TextPrimary,
            new RectangleF(titleX, header.Y, MathF.Max(0f, pill.X - titleX - (8f * scale)), header.Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            scale);
    }

    private (string Text, Color Foreground, Color Background) StatusStyle()
    {
        if (_state.Starting)
        {
            var caution = Fluent.Caution;
            return ("启动中", caution, Fluent.Mix(Fluent.Flyout, caution, Fluent.IsDark ? 0.22f : 0.16f));
        }

        if (_state.Running)
        {
            var success = Fluent.Success;
            return ("运行中", success, Fluent.Mix(Fluent.Flyout, success, Fluent.IsDark ? 0.22f : 0.16f));
        }

        return ("已停止", Fluent.TextTertiary, Fluent.OverState(Fluent.Flyout, VisualState.Hover));
    }

    // ---------------------------------------------------------------- 操作按钮行

    private void DrawActions(Graphics g, RectangleF band)
    {
        var area = Scale(band);
        var gap = Sf(ActionGap);
        var buttonWidth = MathF.Round((area.Width - (2 * gap)) / 3f);

        var x = area.X;
        foreach (var button in ActionButtons())
        {
            var rect = new RectangleF(x, area.Y, buttonWidth, area.Height);
            DrawActionButton(g, rect, button);
            _hits.Add(new MenuHit(rect, button.Id, button.Enabled));
            x += buttonWidth + gap;
        }
    }

    /// <summary>
    /// 运行中：[打开][停止][重启]；停止/启动中：[启动][打开][重启(禁用)]。
    /// 启动中时首按钮显示"启动中"并禁用，打开降为 Standard。
    /// </summary>
    private IReadOnlyList<ActionButton> ActionButtons()
    {
        if (_state.Running)
        {
            return
            [
                new ActionButton(MenuCommand.Open, "打开", "external", SurfaceRole.Accent, _state.HasUrl),
                new ActionButton(MenuCommand.Stop, "停止", "square", SurfaceRole.Standard, true),
                new ActionButton(MenuCommand.Restart, "重启", "refresh", SurfaceRole.Standard, true),
            ];
        }

        return
        [
            new ActionButton(MenuCommand.Start, _state.Starting ? "启动中" : "启动", "play", SurfaceRole.Accent, !_state.Starting),
            new ActionButton(MenuCommand.Open, "打开", "external", SurfaceRole.Standard, _state.HasUrl),
            new ActionButton(MenuCommand.Restart, "重启", "refresh", SurfaceRole.Standard, false),
        ];
    }

    private void DrawActionButton(Graphics g, RectangleF rect, ActionButton button)
    {
        var scale = _scale;
        var state = !button.Enabled
            ? VisualState.Disabled
            : _pressed == button.Id
                ? VisualState.Pressed
                : _hover == button.Id
                    ? VisualState.Hover
                    : VisualState.Rest;

        using (var path = Fluent.Rounded(rect, Sf(Fluent.RControl)))
        {
            var fill = Fluent.FillOf(button.Role, state);
            if (fill != Color.Transparent)
            {
                Fluent.Fill(g, path, fill);
            }

            if (button.Role == SurfaceRole.Standard)
            {
                Fluent.Stroke(g, path, Fluent.Mix(Fluent.StrokeColor, Fluent.StrokeStrong, 0.5f), 1f);
            }
        }

        var foreground = Fluent.TextOf(button.Role, state);
        var font = Fluent.Font(TextRole.Body, false, scale);
        var iconSize = 16f * scale;
        var iconGap = 8f * scale;
        var textWidth = Fluent.Measure(g, button.Label, font);
        var total = iconSize + iconGap + textWidth;
        var startX = rect.X + MathF.Max(0f, (rect.Width - total) / 2f);
        var icon = new RectangleF(startX, rect.Y + ((rect.Height - iconSize) / 2f), iconSize, iconSize);

        Icons.Draw(g, button.Icon, icon, foreground, 2f, fill: button.Id == MenuCommand.Stop);
        Fluent.Text(
            g,
            button.Label,
            font,
            foreground,
            new RectangleF(icon.Right + iconGap, rect.Y, MathF.Max(0f, rect.Right - icon.Right - iconGap), rect.Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter);
    }

    // ---------------------------------------------------------------- 分段控件

    /// <summary>
    /// 三段分段控件（32px 高、4px 圆角、12px Caption 文字）。逐格自绘而不用 Fluent.Segmented：
    /// 后者浅色主题下选中态只混 14% 强调色，和深色版（18%）观感差距较大；
    /// 这里两主题统一用 24% 染底 + 70% 强调色描边，实测选中格/轨道对比度 1.34(浅) vs 1.38(深)。
    /// 悬停一律用 8%/10% 状态层，键盘焦点同样落在 hover 状态上。
    /// </summary>
    private void DrawSegmented(
        Graphics g,
        RectangleF band,
        IReadOnlyList<(string Id, string Label)> cells,
        int selected)
    {
        var scale = _scale;
        var rect = Scale(band);

        using (var track = Fluent.Rounded(rect, Sf(Fluent.RControl)))
        {
            Fluent.Fill(g, track, Fluent.Mix(Fluent.Flyout, Fluent.TextPrimary, Fluent.IsDark ? 0.06f : 0.045f));
            Fluent.Stroke(g, track, Fluent.StrokeColor, 1f);
        }

        var inset = Sf(2f);
        var segment = (rect.Width - (2 * inset)) / cells.Count;
        var activeFont = Fluent.Font(TextRole.Caption, true, scale);
        var normalFont = Fluent.Font(TextRole.Caption, false, scale);

        for (var i = 0; i < cells.Count; i++)
        {
            var cell = new RectangleF(
                rect.X + inset + (i * segment),
                rect.Y + inset,
                segment,
                rect.Height - (2 * inset));
            var active = i == selected;
            var hovered = _hover == cells[i].Id;

            using (var path = Fluent.Rounded(cell, Sf(Fluent.RControl - 1f)))
            {
                if (active)
                {
                    Fluent.Fill(g, path, Fluent.Mix(Fluent.ControlFill, Fluent.AccentFill, 0.24f));
                }

                if (hovered)
                {
                    Fluent.Fill(g, path, Fluent.HoverLayer);
                }

                if (active)
                {
                    Fluent.Stroke(g, path, Fluent.Mix(Fluent.StrokeStrong, Fluent.AccentFill, 0.7f), 1f);
                }
            }

            Fluent.Text(
                g,
                cells[i].Label,
                active ? activeFont : normalFont,
                active ? Fluent.TextPrimary : Fluent.TextSecondary,
                cell,
                StringAlignment.Center,
                StringTrimming.EllipsisCharacter);

            // 选中格本身已有强底色，悬停/键盘焦点再加一圈强调色描边才看得见。
            if (active && hovered)
            {
                using var focusPath = Fluent.Rounded(cell, Sf(Fluent.RControl - 1f));
                Fluent.Stroke(
                    g,
                    focusPath,
                    Fluent.WithAlpha(Fluent.AccentText, Fluent.IsDark ? 0.9f : 0.8f),
                    MathF.Max(1f, scale));
            }

            var hitWidth = rect.Width / cells.Count;
            _hits.Add(new MenuHit(
                new RectangleF(rect.X + (i * hitWidth), rect.Y, hitWidth, rect.Height),
                cells[i].Id,
                true));
        }
    }

    // ---------------------------------------------------------------- 菜单行

    private void DrawRows(Graphics g, RectangleF[] rows)
    {
        for (var i = 0; i < rows.Length; i++)
        {
            var rect = Scale(rows[i]);
            var row = RowAt(i);
            var state = !row.Enabled
                ? VisualState.Disabled
                : _pressed == row.Id
                    ? VisualState.Pressed
                    : _hover == row.Id
                        ? VisualState.Hover
                        : VisualState.Rest;

            DrawMenuRow(g, rect, row.Icon, row.Label, row.Trailing, state, row.Foreground);
            _hits.Add(new MenuHit(rect, row.Id, row.Enabled));
        }
    }

    /// <summary>
    /// MenuFlyout 菜单行：16px 图标 + 14px 文字 + 右侧 12px 状态文字。
    /// 几何与 Fluent.MenuRow 一致（右侧内边距 12、trailing 右对齐、label 截止到 trailing 左侧 8px），
    /// 额外做两件事：所有内边距按 DPI 缩放；trailing 用 TextSecondary（WinUI 的 accelerator/状态文字
    /// 本就是次要色，12px 三级文字在深色下太虚，会被误看成"半截字"）。
    /// </summary>
    private void DrawMenuRow(
        Graphics g,
        RectangleF rect,
        string? icon,
        string label,
        string? trailing,
        VisualState state,
        Color? foreground)
    {
        var scale = _scale;

        var backplate = new RectangleF(rect.X + 4, rect.Y + 1, rect.Width - 8, rect.Height - 2);
        if (state is VisualState.Hover or VisualState.Pressed)
        {
            using var path = Fluent.Rounded(backplate, Sf(Fluent.RControl));
            Fluent.Fill(g, path, state == VisualState.Pressed ? Fluent.PressLayer : Fluent.HoverLayer);
        }

        var contentLeft = rect.X + Sf(12f);
        var contentRight = rect.Right - Sf(12f);
        var bodyFont = Fluent.Font(TextRole.Body, false, scale);
        var captionFont = Fluent.Font(TextRole.Caption, false, scale);

        var labelRight = contentRight;
        if (!string.IsNullOrEmpty(trailing))
        {
            var trailingColor = state == VisualState.Disabled ? Fluent.TextDisabled : Fluent.TextSecondary;
            var trailingWidth = Fluent.Measure(g, trailing, captionFont);
            labelRight = contentRight - trailingWidth - Sf(8f);
            Fluent.Text(
                g,
                trailing,
                captionFont,
                trailingColor,
                new RectangleF(contentRight - trailingWidth, rect.Y, trailingWidth + Sf(4f), rect.Height),
                StringAlignment.Near);
        }

        var color = foreground ?? (state == VisualState.Disabled ? Fluent.TextDisabled : Fluent.TextPrimary);
        var labelLeft = contentLeft;
        if (icon is not null)
        {
            var iconSize = Sf(16f);
            Icons.Draw(
                g,
                icon,
                new RectangleF(contentLeft, rect.Y + ((rect.Height - iconSize) / 2f), iconSize, iconSize),
                color,
                2f);
            labelLeft = contentLeft + iconSize + Sf(12f);
        }

        Fluent.Text(
            g,
            label,
            bodyFont,
            color,
            new RectangleF(labelLeft, rect.Y, MathF.Max(0f, labelRight - labelLeft), rect.Height),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter);
    }

    private RowData RowAt(int index) => index switch
    {
        0 => new RowData(MenuCommand.Lock, "lock", "锁定穿透", _state.Locked ? "开" : "关", null, true),
        1 => new RowData(MenuCommand.Edge, "panel-left", "贴边隐藏", _state.EdgeHideEnabled ? "开" : "关", null, true),
        2 => new RowData(MenuCommand.Shell, "monitor", "外壳关闭时", _state.ShellHide ? "隐藏" : "关闭", null, true),
        3 => new RowData(MenuCommand.Eye, "eye", _state.Visible ? "隐藏挂件" : "显示挂件", null, null, true),
        4 => new RowData(
            MenuCommand.Copy,
            "copy",
            AuthLinks.HasAuthenticatedLink ? "复制登录地址" : "复制地址",
            null,
            null,
            _state.HasUrl),
        5 => new RowData(MenuCommand.Settings, "settings", "设置…", null, null, true),
        6 => new RowData(MenuCommand.Exit, "power", "退出", null, Fluent.Critical, true),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    // ---------------------------------------------------------------- 鼠标 / 键盘交互

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _pressed = HitTest(e.Location);
        RenderSurface();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        // 在窗口外松手收不到 MouseUp，回到窗口时把残留的按下态清掉。
        var releasedOutside = e.Button == MouseButtons.None && _pressed is not null;
        if (releasedOutside)
        {
            _pressed = null;
        }

        var hit = HitTest(e.Location);
        if (hit != _hover || releasedOutside)
        {
            _hover = hit;
            Cursor = hit is null ? Cursors.Default : Cursors.Hand;
            RenderSurface();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover is null)
        {
            return;
        }

        _hover = null;
        Cursor = Cursors.Default;
        RenderSurface();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var released = HitTest(e.Location);
        var clicked = released is not null && released == _pressed;
        _pressed = null;

        if (!clicked)
        {
            RenderSurface();
            return;
        }

        Close();
        _command(released!);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        switch (e.KeyCode)
        {
            case Keys.Escape:
                e.Handled = true;
                Close();
                break;

            case Keys.Up:
                e.Handled = true;
                e.SuppressKeyPress = true;
                MoveFocus(-1);
                break;

            case Keys.Down:
                e.Handled = true;
                e.SuppressKeyPress = true;
                MoveFocus(1);
                break;

            case Keys.Enter:
                e.Handled = true;
                e.SuppressKeyPress = true;
                ActivateFocused();
                break;
        }
    }

    /// <summary>键盘焦点在可点行之间循环移动，复用 hover 状态层表现焦点。</summary>
    private void MoveFocus(int delta)
    {
        var focusable = new List<string>();
        foreach (var hit in _hits)
        {
            if (hit.Enabled)
            {
                focusable.Add(hit.Id);
            }
        }

        if (focusable.Count == 0)
        {
            return;
        }

        int next;
        if (_hover is null)
        {
            next = delta > 0 ? 0 : focusable.Count - 1;
        }
        else
        {
            var current = focusable.IndexOf(_hover);
            next = current < 0
                ? (delta > 0 ? 0 : focusable.Count - 1)
                : ((current + delta) % focusable.Count + focusable.Count) % focusable.Count;
        }

        _hover = focusable[next];
        _pressed = null;
        Cursor = Cursors.Hand;
        RenderSurface();
    }

    private void ActivateFocused()
    {
        if (_hover is null)
        {
            return;
        }

        var enabled = false;
        foreach (var hit in _hits)
        {
            if (hit.Id == _hover && hit.Enabled)
            {
                enabled = true;
                break;
            }
        }

        if (!enabled)
        {
            return;
        }

        var command = _hover;
        Close();
        _command(command);
    }

    private string? HitTest(Point point)
    {
        var margin = S(ShadowMargin);
        var shifted = new PointF(point.X - margin, point.Y - margin);
        foreach (var hit in _hits)
        {
            if (hit.Enabled && hit.Bounds.Contains(shifted))
            {
                return hit.Id;
            }
        }

        return null;
    }

    protected override void OnPaint(PaintEventArgs e) => RenderSurface();

    /// <summary>供离屏预览取一帧。</summary>
    internal Bitmap? Snapshot()
    {
        RenderSurface();
        return _surface is null ? null : new Bitmap(_surface);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _surface?.Dispose();
            _layered.Dispose();
        }

        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- 布局 / 数据结构

    /// <summary>96 DPI 下的静态布局；卡高由行数推导，绘制时统一缩放。</summary>
    private static MenuLayout BuildLayout()
    {
        static RectangleF Box(int x, int y, int width, int height) => new(x, y, width, height);

        var width = CardWidth - (2 * Pad);
        var y = Pad;

        var header = Box(Pad, y, width, HeaderHeight);
        y += HeaderHeight + Gap;

        var actions = Box(Pad, y, width, ButtonHeight);
        y += ButtonHeight + Gap;

        var divider1 = Box(Pad, y, width, 1);
        y += 1 + Gap;

        var layoutLabel = Box(Pad, y, width, SectionHeight);
        y += SectionHeight + Gap;
        var layoutSegments = Box(Pad, y, width, SegmentHeight);
        y += SegmentHeight + Gap;

        var autoStartLabel = Box(Pad, y, width, SectionHeight);
        y += SectionHeight + Gap;
        var autoStartSegments = Box(Pad, y, width, SegmentHeight);
        y += SegmentHeight + Gap;

        var divider2 = Box(Pad, y, width, 1);
        y += 1 + Gap;

        // 菜单行贴齐卡片左右边：DrawMenuRow 自带 12px 内边距与 4px 悬停背板。
        var rows = new RectangleF[7];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = Box(0, y, CardWidth, RowHeight);
            y += RowHeight;
        }

        y += Gap;
        return new MenuLayout(
            header,
            actions,
            divider1,
            layoutLabel,
            layoutSegments,
            autoStartLabel,
            autoStartSegments,
            divider2,
            rows,
            y);
    }

    private sealed record MenuLayout(
        RectangleF Header,
        RectangleF Actions,
        RectangleF Divider1,
        RectangleF LayoutLabel,
        RectangleF LayoutSegments,
        RectangleF AutoStartLabel,
        RectangleF AutoStartSegments,
        RectangleF Divider2,
        RectangleF[] Rows,
        int CardHeight);

    private readonly record struct MenuHit(RectangleF Bounds, string Id, bool Enabled);

    private readonly record struct ActionButton(string Id, string Label, string Icon, SurfaceRole Role, bool Enabled);

    private readonly record struct RowData(
        string Id,
        string Icon,
        string Label,
        string? Trailing,
        Color? Foreground,
        bool Enabled);
}

/// <summary>弹出菜单时挂件的当前状态。</summary>
internal sealed record MenuState(
    bool Running,
    bool Starting,
    bool HasUrl,
    bool LayoutBottom,
    bool LayoutTop,
    bool Locked,
    bool EdgeHideEnabled,
    bool Visible,
    bool ShellHide,
    AutoStartMode AutoStart)
{
    public bool LayoutNormal => !LayoutBottom && !LayoutTop;
}

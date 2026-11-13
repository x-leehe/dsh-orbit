using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>托盘右键菜单：自绘卡片式弹层，和挂件同一套设计语言。</summary>
internal sealed class TrayMenuForm : Form
{
    private const int CardWidth = 252;
    private const int CardHeight = 436;
    private const int ShadowMargin = 10;
    private const int Pad = 12;
    private const int ActionHeight = 56;
    private const int SegmentHeight = 34;
    private const int ItemHeight = 32;

    private static readonly Color TextPrimary = Color.FromArgb(233, 238, 246);
    private static readonly Color TextDim = Color.FromArgb(150, 161, 178);
    private static readonly Color TextMuted = Color.FromArgb(122, 134, 155);
    private static readonly Color AccentGreen = Color.FromArgb(63, 208, 138);
    private static readonly Color AccentAmber = Color.FromArgb(240, 180, 41);
    private static readonly Color AccentRed = Color.FromArgb(240, 90, 90);
    private static readonly Color AccentBlue = Color.FromArgb(74, 140, 255);

    private readonly MenuState _state;
    private readonly Action<string> _command;
    private readonly LayeredSurface _layered = new();
    private readonly List<(Rectangle Bounds, string Id, bool Enabled)> _hits = new();
    private Bitmap? _surface;

    private string? _hover;

    private Font _fontLabel = null!;
    private Font _fontItem = null!;
    private Font _fontTile = null!;
    private Font _fontTiny = null!;

    public TrayMenuForm(MenuState state, Action<string> command)
    {
        _state = state;
        _command = command;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(12, 16, 23);
        AutoScaleMode = AutoScaleMode.None;
        Text = "DSH 菜单";
        KeyPreview = true;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        CreateFonts();
        ClientSize = new Size(CardWidth + (2 * ShadowMargin), CardHeight + (2 * ShadowMargin));

        Deactivate += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                Close();
            }
        };
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

    private void CreateFonts()
    {
        var body = Ui.PickFamily("HarmonyOS Sans SC", Ui.PickFamily("HarmonyOS Sans", "Segoe UI"));
        var medium = Ui.PickFamily("HarmonyOS Sans SC Medium", body);

        _fontLabel = new Font(body, 12, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontItem = new Font(medium, 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontTile = new Font(medium, 11.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        _fontTiny = new Font(body, 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>在光标附近弹出，自动避开屏幕边缘。</summary>
    public void ShowAtCursor()
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).WorkingArea;
        var width = Width;
        var height = Height;

        var x = cursor.X - width + 20;
        var y = cursor.Y - height - 6;
        if (y < screen.Top)
        {
            y = cursor.Y - 20;
        }

        x = Math.Clamp(x, screen.Left, Math.Max(screen.Left, screen.Right - width));
        y = Math.Clamp(y, screen.Top, Math.Max(screen.Top, screen.Bottom - height));

        Location = new Point(x, y);
        _ = Handle;
        RenderSurface();
        Show();
        Activate();
    }

    // ---------------------------------------------------------------- 绘制

    private void RenderSurface()
    {
        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            var state = g.Save();
            g.TranslateTransform(ShadowMargin, ShadowMargin);
            DrawCard(g);
            g.Restore(state);
        }

        if (IsHandleCreated)
        {
            _layered.Push(Handle, _surface, Left, Top, 1f);
        }
    }

    private void DrawCard(Graphics g)
    {
        _hits.Clear();

        var card = new RectangleF(0, 0, CardWidth, CardHeight);
        using (var path = Ui.Rounded(card, 22))
        {
            using var fill = new LinearGradientBrush(card, Color.FromArgb(28, 33, 45), Color.FromArgb(13, 16, 23), 90f);
            g.FillPath(fill, path);
        }

        Ui.Shadow(g, card, 22, 12, 5);

        // ---- 三个动作按钮 ----
        const int gap = 6;
        var tileWidth = (CardWidth - (2 * Pad) - (2 * gap)) / 3;
        var y = Pad;
        DrawAction(g, "open", "打开", Pad, y, tileWidth, AccentBlue, _state.HasUrl);
        DrawAction(g, "stop", "停止", Pad + tileWidth + gap, y, tileWidth, AccentRed, _state.Running);
        DrawAction(g, "restart", "重启", Pad + (2 * (tileWidth + gap)), y, tileWidth, AccentGreen,
            _state.Running || _state.HasUrl);

        // ---- 层级分段 ----
        var segmentY = Pad + ActionHeight + 8;
        DrawSegment(g, "bottom", "置底", Pad, segmentY, tileWidth, _state.LayoutBottom);
        DrawSegment(g, "top", "置顶", Pad + tileWidth + gap, segmentY, tileWidth, _state.LayoutTop);
        DrawSegment(g, "normal", "普通", Pad + (2 * (tileWidth + gap)), segmentY, tileWidth, _state.LayoutNormal);

        // ---- 分隔线 ----
        var lineY = segmentY + SegmentHeight + 10;
        using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f))
        {
            g.DrawLine(pen, Pad, lineY, CardWidth - Pad, lineY);
        }

        // ---- 开机启动：三选一 ----
        var autoLabelY = lineY + 8;
        Ui.Text(g, "开机启动", _fontTiny, Color.FromArgb(122, 134, 155),
            new RectangleF(Pad, autoLabelY, 120, 16));

        var autoY = autoLabelY + 18;
        var optionWidth = (CardWidth - (2 * Pad) - (2 * gap)) / 3;
        DrawAutoStart(g, "Disabled", "停用", Pad, autoY, optionWidth, _state.AutoStart == AutoStartMode.Disabled);
        DrawAutoStart(g, "Enabled", "启用", Pad + optionWidth + gap, autoY, optionWidth, _state.AutoStart == AutoStartMode.Enabled);
        DrawAutoStart(g, "FullAuto", "全自动", Pad + (2 * (optionWidth + gap)), autoY, optionWidth, _state.AutoStart == AutoStartMode.FullAuto);

        var autoLine = autoY + SegmentHeight + 10;
        using (var pen2 = new Pen(Color.FromArgb(28, 255, 255, 255), 1f))
        {
            g.DrawLine(pen2, Pad, autoLine, CardWidth - Pad, autoLine);
        }

        // ---- 列表项 ----
        var itemY = autoLine + 6;
        DrawItem(g, "lock", _state.Locked ? "解除锁定" : "锁定穿透",
            _state.Locked ? "已锁定" : null, ref itemY, _state.Locked ? AccentAmber : TextPrimary, true);
        DrawItem(g, "edge", "贴边隐藏", _state.EdgeHideEnabled ? "开" : "关", ref itemY, TextPrimary, true);
        DrawItem(g, "shell", "外壳关闭时", _state.ShellHide ? "隐藏" : "退出", ref itemY, TextPrimary, true);

        DrawItem(g, "eye", _state.Visible ? "隐藏挂件" : "显示挂件", null, ref itemY, TextPrimary, true);
        DrawItem(g, "copy", AuthLinks.HasAuthenticatedLink ? "复制登录地址" : "复制地址", null, ref itemY, TextPrimary, _state.HasUrl);
        DrawItem(g, "logs", "打开日志", null, ref itemY, TextPrimary, true);
        DrawItem(g, "exit", "退出", null, ref itemY, AccentRed, true);
    }

    private void DrawAction(Graphics g, string id, string label, int x, int y, int width, Color accent, bool enabled)
    {
        var rect = new Rectangle(x, y, width, ActionHeight);
        var hovered = enabled && _hover == id;

        using (var path = Ui.Rounded(rect, 14))
        {
            using var fill = new SolidBrush(Color.FromArgb(hovered ? 26 : 14, accent));
            g.FillPath(fill, path);
            using var border = new Pen(Color.FromArgb(hovered ? 90 : 40, accent), 1f);
            g.DrawPath(border, path);
        }

        var iconName = id switch
        {
            "open" => "external",
            "stop" => "square",
            "restart" => "refresh",
            _ => "square",
        };

        Icons.Draw(g, iconName,
            new RectangleF(rect.X + (width / 2f) - 9, rect.Y + 11, 18, 18),
            enabled ? accent : Color.FromArgb(70, 78, 92), 2f, fill: id == "stop");

        Ui.Text(g, label, _fontTile, enabled ? TextPrimary : Color.FromArgb(96, 106, 122),
            new RectangleF(rect.X, rect.Y + 33, width, 16), Ui.Center);

        if (enabled)
        {
            _hits.Add((rect, id, true));
        }
    }

    private void DrawSegment(Graphics g, string id, string label, int x, int y, int width, bool active)
    {
        var rect = new Rectangle(x, y, width, SegmentHeight);
        var hovered = _hover == id;

        using (var path = Ui.Rounded(rect, 11))
        {
            using var fill = new SolidBrush(active
                ? Color.FromArgb(38, AccentGreen)
                : Color.FromArgb(hovered ? 22 : 12, 255, 255, 255));
            g.FillPath(fill, path);

            using var border = new Pen(Color.FromArgb(active ? 110 : 26, active ? AccentGreen : Color.White), 1f);
            g.DrawPath(border, path);
        }

        var iconName = id switch
        {
            "bottom" => "panel-bottom",
            "top" => "pin",
            _ => "square",
        };

        Icons.Draw(g, iconName, new RectangleF(rect.X + 9, rect.Y + 10, 14, 14),
            active ? AccentGreen : TextMuted, 2f);

        Ui.Text(g, label, _fontLabel, active ? Color.FromArgb(236, 242, 250) : TextDim,
            new RectangleF(rect.X + 29, rect.Y, width - 33, SegmentHeight));

        _hits.Add((rect, "layout:" + id, true));
    }

    private void DrawAutoStart(Graphics g, string id, string label, int x, int y, int width, bool active)
    {
        var rect = new Rectangle(x, y, width, SegmentHeight);
        var hovered = _hover == "autostart:" + id;

        using (var path = Ui.Rounded(rect, 11))
        {
            using var fill = new SolidBrush(active
                ? Color.FromArgb(38, AccentGreen)
                : Color.FromArgb(hovered ? 22 : 12, 255, 255, 255));
            g.FillPath(fill, path);

            using var border = new Pen(Color.FromArgb(active ? 110 : 26, active ? AccentGreen : Color.White), 1f);
            g.DrawPath(border, path);
        }

        Ui.Text(g, label, _fontLabel, active ? Color.FromArgb(236, 242, 250) : TextDim,
            rect, Ui.Center);

        _hits.Add((rect, "autostart:" + id, true));
    }

    private void DrawItem(Graphics g, string id, string label, string? trailing, ref int y, Color color, bool enabled)
    {
        var rect = new Rectangle(Pad, y, CardWidth - (2 * Pad), ItemHeight);
        var hovered = enabled && _hover == id;

        if (hovered)
        {
            using var path = Ui.Rounded(rect, 9);
            using var fill = new SolidBrush(Color.FromArgb(20, 255, 255, 255));
            g.FillPath(fill, path);
        }

        var iconName = id switch
        {
            "lock" => "lock",
            "edge" => "panel-left",
            "shell" => "monitor",
            "autostart" => "zap",
            "eye" => "eye",
            "copy" => "copy",
            "logs" => "file-text",
            "exit" => "power",
            _ => "square",
        };

        Icons.Draw(g, iconName, new RectangleF(rect.X + 8, rect.Y + 9, 14, 14),
            enabled ? Color.FromArgb(150, 161, 178) : Color.FromArgb(70, 78, 92), 2f);

        Ui.Text(g, label, _fontItem, enabled ? color : Color.FromArgb(88, 98, 114),
            new RectangleF(rect.X + 30, rect.Y, rect.Width - 96, ItemHeight));

        if (trailing is not null)
        {
            Ui.Text(g, trailing, _fontTiny, Color.FromArgb(150, 161, 178),
                new RectangleF(rect.Right - 52, rect.Y, 44, ItemHeight), Ui.Right);
        }

        if (enabled)
        {
            _hits.Add((rect, id, true));
        }

        y += ItemHeight;
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Cursor = hit is null ? Cursors.Default : Cursors.Hand;
            RenderSurface();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = HitTest(e.Location);
        if (hit is null)
        {
            return;
        }

        Close();
        _command(hit);
    }

    private string? HitTest(Point point)
    {
        var shifted = new Point(point.X - ShadowMargin, point.Y - ShadowMargin);
        foreach (var (bounds, id, enabled) in _hits)
        {
            if (enabled && bounds.Contains(shifted))
            {
                return id;
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
            _fontLabel.Dispose();
            _fontItem.Dispose();
            _fontTile.Dispose();
            _fontTiny.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>弹出菜单时挂件的当前状态。</summary>
internal sealed record MenuState(
    bool Running,
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

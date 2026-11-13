using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>桌面挂件本体：不透明圆角卡片，逐像素透明窗口，锁定穿透时在光标处开「探照灯」。</summary>
internal sealed class WidgetForm : Form
{
    // 96 DPI 下的设计尺寸（卡片本体，不含阴影外扩）
    private const int CardWidth = 460;
    private const int CardHeight = 180;
    private const int Pad = 16;

    /// <summary>卡片四周留给阴影的透明外边距。</summary>
    private const int ShadowMargin = 16;

    /// <summary>卡片圆角半径（96 DPI 下的像素）。</summary>
    private const int CornerRadius = 30;

    private static readonly Color ButtonTop = Color.FromArgb(74, 140, 255);
    private static readonly Color ButtonBottom = Color.FromArgb(38, 92, 220);

    private static readonly StringFormat AlignLeft = CreateFormat(StringAlignment.Near, null);
    private static readonly StringFormat AlignRight = CreateFormat(StringAlignment.Far, null);
    private static readonly StringFormat AlignLeftEllipsis = CreateFormat(StringAlignment.Near, StringTrimming.EllipsisCharacter);

    private readonly WidgetConfig _config;
    private readonly ToolTip _tooltip = new();
    private StatusSnapshot _status = new();
    private readonly List<(Rectangle Bounds, string Id)> _hits = new();
    private readonly System.Windows.Forms.Timer _cursorTimer = new() { Interval = 40 };

    private Bitmap? _surface;
    private Point? _spotlight;

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _previousObject;
    private int _dibWidth;
    private int _dibHeight;

    private string? _hoverId;
    private string? _pressedId;
    private bool _dragging;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;

    // 贴边隐藏
    private readonly System.Windows.Forms.Timer _dockTimer = new() { Interval = 22 }; // ~45fps
    private DockSide _dockSide = DockSide.None;
    private bool _dockedHidden;
    private int _awayTicks;
    private bool _animating;
    private int _animFrom;
    private int _animTo;
    private int _animStep;

    private float _scale = 1f;
    private Font _fontBody = null!;
    private Font _fontLabel = null!;
    private Font _fontChip = null!;
    private Font _fontPill = null!;
    private Font _fontHeadline = null!;
    private Font _fontStatLabel = null!;
    private Font _fontStat = null!;
    private Font _fontMicro = null!;

    public event Action? StartStopRequested;
    public event Action? OpenRequested;
    public event Action? LockRequested;
    public event Action? Moved;
    public event Action? StatusChanged;

    public WidgetForm(WidgetConfig config)
    {
        _config = config;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(12, 16, 23);
        AutoScaleMode = AutoScaleMode.None;
        Text = "DSH-Orbit";

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw |
            ControlStyles.StandardClick |
            ControlStyles.StandardDoubleClick,
            true);

        CreateFonts();
        ApplyWindowSize();

        // 穿透时收不到鼠标消息，只能轮询光标位置来画「探照灯」。
        _cursorTimer.Tick += (_, _) => TrackSpotlight();
        _dockTimer.Tick += (_, _) => OnDockTick();

        Shown += (_, _) =>
        {
            ApplyZOrder();
            if (_config.ClickThrough)
            {
                ApplyClickThrough(true);
            }

            RenderSurface();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;

            // WinForms 给无边框窗体仍留着标题栏/可调边框位，会让窗口比客户区大一圈（16×39）；
            // 建窗之前就清掉，窗口尺寸才等于卡片 + 阴影边距。
            parameters.Style &= ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU |
                                  Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);

            // 逐像素透明：内容由 UpdateLayeredWindow 直接写入 ARGB 位图。
            parameters.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
            return parameters;
        }
    }

    // ---------------------------------------------------------------- 窗口行为

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowSize();
        RenderSurface();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        CreateFonts();
        ApplyWindowSize();
        RenderSurface();
    }

    /// <summary>窗口 = 卡片 + 四周阴影外边距。直接 SetWindowPos，绕开 WinForms 的边框折算。</summary>
    private void ApplyWindowSize()
    {
        var width = (int)MathF.Round((CardWidth + (2 * ShadowMargin)) * _scale);
        var height = (int)MathF.Round((CardHeight + (2 * ShadowMargin)) * _scale);

        if (IsHandleCreated)
        {
            _ = Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, width, height,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        else
        {
            Size = new Size(width, height);
        }
    }

    private void CreateFonts()
    {
        _scale = DeviceDpi / 96f;
        float Px(float v) => v * _scale;

        _fontBody?.Dispose();
        _fontLabel?.Dispose();
        _fontChip?.Dispose();
        _fontPill?.Dispose();
        _fontHeadline?.Dispose();
        _fontStatLabel?.Dispose();
        _fontStat?.Dispose();
        _fontMicro?.Dispose();

        var body = Ui.PickFamily("HarmonyOS Sans SC", Ui.PickFamily("HarmonyOS Sans", "Segoe UI"));
        var medium = Ui.PickFamily("HarmonyOS Sans SC Medium", body);
        var black = Ui.PickFamily("HarmonyOS Sans SC Black", medium);

        _fontBody = new Font(body, Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontLabel = new Font(body, Px(12.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontChip = new Font(body, Px(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontPill = new Font(medium, Px(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontHeadline = new Font(black, Px(28), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontStatLabel = new Font(body, Px(11), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontStat = new Font(medium, Px(15.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _fontMicro = new Font(body, Px(10.5f), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    public void SetStatus(StatusSnapshot status)
    {
        var changed = status.State != _status.State
            || status.Port != _status.Port
            || status.DisplayUrl != _status.DisplayUrl
            || status.PluginTotal != _status.PluginTotal
            || status.PluginDisabled != _status.PluginDisabled
            || status.PluginNames != _status.PluginNames
            || status.PluginScanning != _status.PluginScanning
            || status.Balance != _status.Balance
            || status.SpentToday != _status.SpentToday
            || status.OffPeak != _status.OffPeak
            || status.Hint != _status.Hint
            || status.HasAuthenticatedUrl != _status.HasAuthenticatedUrl;

        _status = status;

        // layered window 不依赖 WM_PAINT，状态一变就直接重画并推送。
        RenderSurface();

        if (changed)
        {
            UpdateTooltip();
            StatusChanged?.Invoke();
        }
    }

    public StatusSnapshot Status => _status;

    private void UpdateTooltip()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_status.Version))
        {
            parts.Add($"dsh {_status.Version}");
        }

        if (_status.Port is { } port)
        {
            parts.Add($"端口 {port}");
        }

        if (_status.PluginTotal is { } total)
        {
            var names = string.IsNullOrWhiteSpace(_status.PluginNames) ? "" : $"（{_status.PluginNames}）";
            parts.Add($"插件 {total}{names}");
        }

        if (_status.State == DshState.Running)
        {
            parts.Add($"内存 {FormatBytes(_status.MemoryBytes)}");
            if (_status.CpuPercent is { } cpu)
            {
                parts.Add($"CPU {cpu:0}%");
            }
        }

        parts.Add("双击卡片或点地址可打开 Web UI");
        _tooltip.SetToolTip(this, string.Join("  ·  ", parts));
    }

    private void ApplyZOrder()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        if (_config.AlwaysOnTop)
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else if (_config.AlwaysBottom)
        {
            Native.SetWindowPos(Handle, Native.HWND_BOTTOM, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    public void ApplyZOrderMode(WidgetConfig config) => ApplyZOrder();

    public void ApplyClickThrough(bool enabled)
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var style = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        style = enabled
            ? style | Native.WS_EX_TRANSPARENT
            : style & ~Native.WS_EX_TRANSPARENT;
        _ = Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, style);

        _config.ClickThrough = enabled;

        if (enabled)
        {
            _cursorTimer.Start();
        }
        else
        {
            _cursorTimer.Stop();
            _spotlight = null;
        }

        RenderSurface();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_WINDOWPOSCHANGING && _config.AlwaysBottom && !_config.AlwaysOnTop)
        {
            var pos = Marshal.PtrToStructure<Native.WINDOWPOS>(m.LParam);
            if ((pos.flags & Native.SWP_NOZORDER) == 0)
            {
                pos.hwndInsertAfter = Native.HWND_BOTTOM;
                Marshal.StructureToPtr(pos, m.LParam, false);
            }
        }

        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- 贴边隐藏

    public DockSide DockSide => _dockSide;

    public event Action? DockStateChanged;

    private int ShadowPixels => (int)MathF.Round(ShadowMargin * _scale);

    private Rectangle CardScreenRect
    {
        get
        {
            var origin = PointToScreen(Point.Empty);
            return new Rectangle(
                origin.X + ShadowPixels,
                origin.Y + ShadowPixels,
                ClientSize.Width - (2 * ShadowPixels),
                ClientSize.Height - (2 * ShadowPixels));
        }
    }

    /// <summary>按「卡片」而不是窗口来贴边（窗口带非客户区偏移），返回窗口 Left 的目标值。</summary>
    private int TargetLeft(Screen screen, DockSide side, bool hidden)
    {
        var card = CardScreenRect;
        // 右侧：收起时只留 6px（卡片左边贴住 右边缘-6），展开时右边离边缘 10px。
        // 左侧：镜像——收起时卡片右边贴住 左边缘+6。
        var targetCardLeft = side == DockSide.Right
            ? hidden ? screen.WorkingArea.Right - 8 : screen.WorkingArea.Right - 10 - card.Width
            : hidden ? screen.WorkingArea.Left + 8 - card.Width : screen.WorkingArea.Left + 10;

        return Left + (targetCardLeft - card.Left);
    }

    public void DockTo(DockSide side, bool hidden)
    {
        if (side == DockSide.None)
        {
            Undock();
            return;
        }

        _dockSide = side;
        _dockedHidden = hidden;
        _awayTicks = 0;
        _animating = false;

        var screen = Screen.FromControl(this);
        Left = TargetLeft(screen, side, hidden);
        _dockTimer.Start();
        DockStateChanged?.Invoke();
    }

    public void Undock()
    {
        if (_dockSide == DockSide.None)
        {
            return;
        }

        _dockSide = DockSide.None;
        _dockedHidden = false;
        _animating = false;
        _dockTimer.Stop();
        DockStateChanged?.Invoke();
    }

    private void OnDockTick()
    {
        if (_dragging)
        {
            return;
        }

        if (_animating)
        {
            _animStep++;
            var t = Math.Min(1f, _animStep / 11f);
            var eased = 1f - MathF.Pow(1f - t, 3);
            Left = (int)MathF.Round(_animFrom + ((_animTo - _animFrom) * eased));
            if (t >= 1f)
            {
                Left = _animTo;
                _animating = false;
            }

            return;
        }

        if (_dockSide == DockSide.None)
        {
            _dockTimer.Stop();
            return;
        }

        var screen = Screen.FromControl(this);
        if (_dockedHidden)
        {
            if (HotZone(screen).Contains(Cursor.Position))
            {
                _dockedHidden = false;
                _awayTicks = 0;
                StartAnimation(TargetLeft(screen, _dockSide, hidden: false));
            }

            return;
        }

        var padded = Rectangle.Inflate(CardScreenRect, 30, 30);
        if (padded.Contains(Cursor.Position))
        {
            _awayTicks = 0;
        }
        else if (++_awayTicks > 20)
        {
            _dockedHidden = true;
            StartAnimation(TargetLeft(screen, _dockSide, hidden: true));
        }
    }

    private Rectangle HotZone(Screen screen)
    {
        const int width = 14;
        var card = CardScreenRect;
        return _dockSide == DockSide.Right
            ? new Rectangle(screen.WorkingArea.Right - width, card.Top - 12, width, card.Height + 24)
            : new Rectangle(screen.WorkingArea.Left, card.Top - 12, width, card.Height + 24);
    }

    private void StartAnimation(int targetX)
    {
        _animFrom = Left;
        _animTo = targetX;
        _animStep = 0;
        _animating = true;
        if (!_dockTimer.Enabled)
        {
            _dockTimer.Start();
        }
    }

    /// <summary>松手时判断是否贴边：靠近左右边就吸附，否则解除。</summary>
    private void HandleDrop()
    {
        var screen = Screen.FromControl(this);
        var card = CardScreenRect;
        const int snap = 28;

        if (_config.EdgeHideEnabled && card.Right >= screen.WorkingArea.Right - snap)
        {
            DockTo(DockSide.Right, hidden: false);
            _awayTicks = 0;
            return;
        }

        if (_config.EdgeHideEnabled && card.Left <= screen.WorkingArea.Left + snap)
        {
            DockTo(DockSide.Left, hidden: false);
            _awayTicks = 0;
            return;
        }

        if (_dockSide != DockSide.None)
        {
            Undock();
        }

        Moved?.Invoke();
    }

    /// <summary>穿透时跟随光标：光标附近把卡片挖成渐变透明，露出下面的内容。</summary>
    private void TrackSpotlight()    {
        var point = PointToClient(Cursor.Position);
        Point? next = ClientRectangle.Contains(point) ? point : null;
        if (next == _spotlight)
        {
            return;
        }

        _spotlight = next;
        RenderSurface();
    }

    // ---------------------------------------------------------------- 鼠标交互

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _pressedId = HitTest(e.Location);
        _dragging = false;
        _animating = false;
        _dragCursorOrigin = Cursor.Position;
        _dragWindowOrigin = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (e.Button == MouseButtons.Left)
        {
            var cursor = Cursor.Position;
            var dx = cursor.X - _dragCursorOrigin.X;
            var dy = cursor.Y - _dragCursorOrigin.Y;

            if (!_dragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3))
            {
                _dragging = true;
                _hoverId = null;
            }

            if (_dragging)
            {
                Location = new Point(_dragWindowOrigin.X + dx, _dragWindowOrigin.Y + dy);
            }

            return;
        }

        var hit = HitTest(e.Location);
        if (hit != _hoverId)
        {
            _hoverId = hit;
            Cursor = hit is null ? Cursors.SizeAll : Cursors.Hand;
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

        var released = HitTest(e.Location);
        var clicked = !_dragging && released is not null && released == _pressedId;

        if (_dragging)
        {
            HandleDrop();
        }

        _dragging = false;
        _pressedId = null;

        if (!clicked)
        {
            return;
        }

        switch (released)
        {
            case "start":
                StartStopRequested?.Invoke();
                break;
            case "open":
                OpenRequested?.Invoke();
                break;
            case "lock":
                LockRequested?.Invoke();
                break;
        }
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        if (HitTest(PointToClient(Cursor.Position)) is null)
        {
            OpenRequested?.Invoke();
        }
    }

    private string? HitTest(Point point)
    {
        foreach (var (bounds, id) in _hits)
        {
            if (bounds.Contains(point))
            {
                return id;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- 渲染

    /// <summary>把卡片画进 ARGB 位图，再预乘 alpha 推到 layered window。</summary>
    private void RenderSurface()
    {
        if (!IsHandleCreated || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        var width = ClientSize.Width;
        var height = ClientSize.Height;
        if (_surface is null || _surface.Width != width || _surface.Height != height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        }

        var margin = (int)MathF.Round(ShadowMargin * _scale);
        var cardWidth = width - (2 * margin);
        var cardHeight = height - (2 * margin);

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            // 只能用灰度抗锯齿：ClearType 在透明面上会出彩边，而 AntiAliasGridFit 在小字号上会直接关掉抗锯齿。
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            var state = g.Save();
            g.TranslateTransform(margin, margin);
            DrawShadow(g, cardWidth, cardHeight);
            DrawCard(g, cardWidth, cardHeight);
            g.Restore(state);
        }

        // 绘制时平移了阴影外边距，命中区域要补回来。
        for (var i = 0; i < _hits.Count; i++)
        {
            var (bounds, id) = _hits[i];
            _hits[i] = (new Rectangle(bounds.X + margin, bounds.Y + margin, bounds.Width, bounds.Height), id);
        }

        if (_config.ClickThrough && _spotlight is { } spotlight)
        {
            ApplySpotlight(spotlight, width, height);
        }

        PushLayered(width, height);
    }

    /// <summary>卡片外面一圈柔和投影：多层同色低透明度圆角矩形堆出渐变。</summary>
    private void DrawShadow(Graphics g, int width, int height)
    {
        var blur = (int)MathF.Round(12 * _scale);
        if (blur <= 0)
        {
            return;
        }

        for (var i = blur; i >= 1; i--)
        {
            const int alpha = 5;
            var rect = new RectangleF(-i, -i, width + (2 * i), height + (2 * i));
            using var path = RoundedPath(rect, (CornerRadius * _scale) + i);
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0));
            g.FillPath(brush, path);
        }
    }

    protected override void OnPaint(PaintEventArgs e) => RenderSurface();

    /// <summary>供离屏测试取一帧（渲染到内存位图并复制）。</summary>
    internal Bitmap? Snapshot()
    {
        RenderSurface();
        return _surface is null ? null : new Bitmap(_surface);
    }

    private void PushLayered(int width, int height)
    {
        if (_surface is null)
        {
            return;
        }

        var screenDc = Native.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return;
        }

        try
        {
            EnsureDib(width, height);
            if (_dibBits == IntPtr.Zero)
            {
                return;
            }

            CopyPremultiplied(width, height);

            var destination = new Native.POINT { X = Left, Y = Top };
            var size = new Native.SIZE { CX = width, CY = height };
            var source = new Native.POINT { X = 0, Y = 0 };
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                // 不透明度已经乘进每个像素的 alpha，这里保持 255，避免二次衰减。
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };

            if (!Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, _memDc, ref source, 0, ref blend, Native.ULW_ALPHA)) { AppPaths.Log($"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()} size={width}x{height} dib={_dib} bits={_dibBits}"); }
        }
        finally
        {
            _ = Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void EnsureDib(int width, int height)
    {
        if (_dib != IntPtr.Zero && _dibWidth == width && _dibHeight == height)
        {
            return;
        }

        ReleaseDib();

        var screenDc = Native.GetDC(IntPtr.Zero);
        try
        {
            _memDc = Native.CreateCompatibleDC(screenDc);
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            };

            _dib = Native.CreateDIBSection(_memDc, ref header, 0, out _dibBits, IntPtr.Zero, 0);

            if (_dib == IntPtr.Zero) { AppPaths.Log($"CreateDIBSection failed: {Marshal.GetLastWin32Error()} dc={_memDc}"); }
            if (_dib != IntPtr.Zero)
            {
                _previousObject = Native.SelectObject(_memDc, _dib);
                _dibWidth = width;
                _dibHeight = height;
            }
        }
        finally
        {
            _ = Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void ReleaseDib()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_previousObject != IntPtr.Zero)
            {
                _ = Native.SelectObject(_memDc, _previousObject);
                _previousObject = IntPtr.Zero;
            }

            if (_dib != IntPtr.Zero)
            {
                _ = Native.DeleteObject(_dib);
                _dib = IntPtr.Zero;
            }

            _ = Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
        }

        _dibBits = IntPtr.Zero;
        _dibWidth = 0;
        _dibHeight = 0;
    }

    /// <summary>把 ARGB 位图拷进 DIB，同时乘整体不透明度并做 alpha 预乘。</summary>
    private unsafe void CopyPremultiplied(int width, int height)
    {
        if (_surface is null || _dibBits == IntPtr.Zero)
        {
            return;
        }

        var globalAlpha = Math.Clamp((int)MathF.Round((float)(_config.Opacity * 255)), 60, 255);
        var data = _surface.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var source = (byte*)data.Scan0;
            var target = (byte*)_dibBits;

            for (var y = 0; y < height; y++)
            {
                var sourceRow = source + (y * data.Stride);
                var targetRow = target + (y * width * 4);

                for (var x = 0; x < width * 4; x += 4)
                {
                    var alpha = sourceRow[x + 3] * globalAlpha / 255;
                    targetRow[x + 3] = (byte)alpha;
                    targetRow[x + 2] = (byte)(sourceRow[x + 2] * alpha / 255);
                    targetRow[x + 1] = (byte)(sourceRow[x + 1] * alpha / 255);
                    targetRow[x] = (byte)(sourceRow[x] * alpha / 255);
                }
            }
        }
        finally
        {
            _surface.UnlockBits(data);
        }
    }

    /// <summary>在光标处把 alpha 按平滑曲线压低：中心全透，向外渐变回不透明。</summary>
    private unsafe void ApplySpotlight(Point center, int width, int height)
    {
        if (_surface is null)
        {
            return;
        }

        var radius = 96f * _scale;
        var radiusSquared = radius * radius;
        if (radiusSquared <= 0)
        {
            return;
        }

        var data = _surface.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        try
        {
            var basePointer = (byte*)data.Scan0;
            var left = Math.Max(0, (int)(center.X - radius));
            var right = Math.Min(width - 1, (int)(center.X + radius));
            var top = Math.Max(0, (int)(center.Y - radius));
            var bottom = Math.Min(height - 1, (int)(center.Y + radius));

            for (var y = top; y <= bottom; y++)
            {
                var row = basePointer + (y * data.Stride);
                var dy = y - center.Y;

                for (var x = left; x <= right; x++)
                {
                    var dx = x - center.X;
                    var distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared >= radiusSquared)
                    {
                        continue;
                    }

                    var t = MathF.Sqrt(distanceSquared) / radius;
                    var factor = t * t * (3f - (2f * t)); // smoothstep
                    var pixel = row + (x * 4);
                    pixel[3] = (byte)(pixel[3] * factor);
                }
            }
        }
        finally
        {
            _surface.UnlockBits(data);
        }
    }

    // ---------------------------------------------------------------- 绘制

    private void DrawCard(Graphics g, int width, int height)
    {
        _scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * _scale);
        var pad = S(Pad);

        _hits.Clear();

        var card = new RectangleF(0, 0, width, height);
        using (var path = RoundedPath(card, S(CornerRadius)))
        {
            using var fill = new LinearGradientBrush(
                new RectangleF(0, 0, width, height),
                Color.FromArgb(26, 31, 42),
                Color.FromArgb(11, 14, 20),
                90f);
            g.FillPath(fill, path);

            var buttonSize = S(78);
            var buttonX = width - pad - buttonSize;
            var buttonY = (height - buttonSize) / 2;
            var center = new PointF(buttonX + (buttonSize / 2f), buttonY + (buttonSize / 2f));

            g.SetClip(path, CombineMode.Intersect);

            // 右侧装饰用的暗色圆斑
            var decorRect = new RectangleF(center.X - S(160), center.Y - S(132), S(320), S(264));
            using (var decor = new PathGradientBrush(BlobPath(decorRect, S(118))))
            {
                decor.CenterColor = Color.FromArgb(34, 0, 0, 0);
                decor.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                g.FillPath(decor, BlobPath(decorRect, S(118)));
            }

            // 按钮背后的柔光
            var glowRect = new RectangleF(center.X - S(126), center.Y - S(126), S(252), S(252));
            using (var glow = new PathGradientBrush(BlobPath(glowRect, S(100))))
            {
                glow.CenterColor = Color.FromArgb(64, ButtonTop);
                glow.SurroundColors = new[] { Color.FromArgb(0, ButtonTop) };
                g.FillPath(glow, BlobPath(glowRect, S(100)));
            }

            g.ResetClip();
        }

        DrawHeader(g, S, pad);
        DrawHeadline(g, S, pad, width);
        DrawChips(g, S, pad);
        DrawStats(g, S, pad, width);
        DrawActionButton(g, S, width, height, pad);
    }

    private void DrawHeader(Graphics g, Func<int, int> S, int pad)
    {
        var iconRect = new Rectangle(pad, S(15), S(22), S(22));
        using (var iconPath = RoundedPath(iconRect, S(7)))
        {
            using var iconBg = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
            g.FillPath(iconBg, iconPath);
        }
        Icons.Draw(g, "activity",
            new RectangleF(iconRect.X + S(3), iconRect.Y + S(3), iconRect.Width - S(6), iconRect.Height - S(6)),
            Color.FromArgb(220, _status.Accent),
            2f);

        var labelRect = new RectangleF(iconRect.Right + S(10), S(12), S(60), S(26));
        DrawText(g, "状态", _fontLabel, Color.FromArgb(150, 161, 178), labelRect);

        var labelWidth = MeasureText(g, "状态", _fontLabel);
        var pillX = labelRect.X + labelWidth + S(10);
        var pillText = _status.StateText;
        var textWidth = MeasureText(g, pillText, _fontPill);
        var pillRect = new Rectangle((int)pillX, S(15), (int)(textWidth + S(36)), S(22));

        using (var pillPath = RoundedPath(pillRect, S(11)))
        {
            using var pillBg = new SolidBrush(Color.FromArgb(_status.State == DshState.Running ? 40 : 26, _status.Accent));
            g.FillPath(pillBg, pillPath);
        }

        using (var dot = new SolidBrush(_status.Accent))
        {
            g.FillEllipse(dot, pillRect.X + S(11), pillRect.Y + (pillRect.Height / 2f) - S(3), S(6), S(6));
        }

        DrawText(g, pillText, _fontPill, _status.Accent,
            new RectangleF(pillRect.X + S(23), pillRect.Y, pillRect.Width - S(26), pillRect.Height));

        if (_config.ClickThrough)
        {
            var lockText = "已锁定";
            var lockWidth = MeasureText(g, lockText, _fontChip);
            var lockRect = new Rectangle(pillRect.Right + S(8), pillRect.Y, (int)(lockWidth + S(20)), pillRect.Height);
            using var lockPath = RoundedPath(lockRect, S(11));
            using var lockBg = new SolidBrush(Color.FromArgb(30, 240, 180, 41));
            g.FillPath(lockBg, lockPath);
            DrawText(g, lockText, _fontChip, Color.FromArgb(240, 180, 41), lockRect, StringAlignment.Center);
        }
    }

    private void DrawHeadline(Graphics g, Func<int, int> S, int pad, int width)
    {
        var text = _status.Port is { } port ? $"127.0.0.1:{port}" : "未运行";
        var color = _status.Port is not null ? Color.FromArgb(240, 244, 250) : Color.FromArgb(110, 122, 142);
        if (_hoverId == "open" && _status.Port is not null)
        {
            color = Color.FromArgb(124, 172, 255);
        }

        var rect = new RectangleF(pad, S(38), width - pad - S(124), S(36));
        DrawText(g, text, _fontHeadline, color, rect, StringAlignment.Near, AlignLeftEllipsis);

        var textWidth = MeasureText(g, text, _fontHeadline);
        var hitWidth = Math.Min(textWidth + S(8), width - pad - S(150));
        _hits.Add((new Rectangle(pad, S(38), (int)hitWidth, S(36)), "open"));
    }

    private void DrawChips(Graphics g, Func<int, int> S, int pad)
    {
        var x = (float)pad;
        var y = 84;

        x = DrawChip(g, S, x, y, $"profile {_status.Profile}", withDot: true);
        if (_status.PluginTotal is { } total)
        {
            x = DrawChip(g, S, x, y,
                _status.PluginDisabled is > 0 ? $"插件 {total} · 停 {_status.PluginDisabled}" : $"插件 {total}");
        }
        else if (_status.PluginScanning)
        {
            x = DrawChip(g, S, x, y, "插件 统计中");
        }

        if (_status.Balance is not null)
        {
            x = DrawChip(g, S, x, y, _status.OffPeak ? "闲时" : "标准价");
        }

        if (_status.State == DshState.Running && _status.MemoryBytes > 0)
        {
            var metrics = FormatBytes(_status.MemoryBytes);
            if (_status.CpuPercent is { } cpu)
            {
                metrics += $" · {cpu:0}%";
            }

            DrawText(g, metrics, _fontMicro, Color.FromArgb(110, 122, 142),
                new RectangleF(x + S(4), S(y), S(120), S(22)));
        }
    }

    private float DrawChip(Graphics g, Func<int, int> S, float x, int y, string text, bool withDot = false)
    {
        var textWidth = MeasureText(g, text, _fontChip);
        var width = textWidth + S(withDot ? 36 : 24);
        var rect = new Rectangle((int)x, S(y), (int)width, S(22));

        using (var path = RoundedPath(rect, S(11)))
        {
            using var bg = new SolidBrush(Color.FromArgb(18, 255, 255, 255));
            g.FillPath(bg, path);
        }

        if (withDot)
        {
            using var dot = new SolidBrush(Color.FromArgb(63, 208, 138));
            g.FillEllipse(dot, rect.X + S(11), rect.Y + S(8), S(6), S(6));
        }

        DrawText(g, text, _fontChip, Color.FromArgb(188, 197, 212),
            new RectangleF(rect.X + S(withDot ? 23 : 12), rect.Y, textWidth + S(4), rect.Height));

        return rect.Right + S(8);
    }

    private void DrawStats(Graphics g, Func<int, int> S, int pad, int width)
    {
        var columns = new (string Label, string Value, Color Color)[]
        {
            ("运行时长", _status.Uptime is { } up ? FormatUptime(up) : "—", Color.FromArgb(232, 238, 246)),
            ("余额", _status.Balance is { } b ? $"¥{b:0.00}" : "—",
                _status.Balance is not null ? Color.FromArgb(63, 208, 138) : Color.FromArgb(120, 132, 152)),
            ("今日已用", _status.SpentToday is { } s && s > 0 ? $"¥{s:0.0000}" : "¥0.0000",
                Color.FromArgb(188, 197, 212)),
        };

        var x = (float)pad;
        var available = width - pad - S(124);
        var step = Math.Max(S(104), available / columns.Length);

        foreach (var column in columns)
        {
            DrawText(g, column.Label, _fontStatLabel, Color.FromArgb(122, 134, 155),
                new RectangleF(x, S(120), step - S(8), S(16)));

            DrawText(g, column.Value, _fontStat, column.Color,
                new RectangleF(x, S(136), step - S(4), S(24)));

            x += step;
        }
    }

    private void DrawActionButton(Graphics g, Func<int, int> S, int width, int height, int pad)
    {
        var size = S(84);
        var rect = new Rectangle(width - pad - size, (height - size) / 2, size, size);
        var hovered = _hoverId == "start";

        var running = _status.State is DshState.Running or DshState.Starting;
        var top = hovered ? ControlPaint.Light(ButtonTop, 0.16f) : ButtonTop;
        var bottom = hovered ? ControlPaint.Light(ButtonBottom, 0.16f) : ButtonBottom;

        var glowRect = new RectangleF(rect.X - S(12), rect.Y - S(12), rect.Width + S(24), rect.Height + S(24));
        using (var glow = new PathGradientBrush(BlobPath(glowRect, glowRect.Width / 2f)))
        {
            glow.CenterColor = Color.FromArgb(58, top);
            glow.SurroundColors = new[] { Color.FromArgb(0, top) };
            g.FillPath(glow, BlobPath(glowRect, glowRect.Width / 2f));
        }

        using (var path = new GraphicsPath())
        {
            path.AddEllipse(rect);
            using var fill = new LinearGradientBrush(rect, top, bottom, 90f);
            g.FillPath(fill, path);

            using var inner = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f * _scale);
            g.DrawEllipse(inner, Rectangle.Inflate(rect, -S(1), -S(1)));
        }

        var glyphSize = S(30);
        Icons.Draw(
            g,
            running ? "pause" : "play",
            new RectangleF(
                rect.X + ((rect.Width - glyphSize) / 2f),
                rect.Y + ((rect.Height - glyphSize) / 2f),
                glyphSize,
                glyphSize),
            Color.FromArgb(245, 250, 255),
            2f);

        _hits.Add((rect, "start"));
    }

    // ---------------------------------------------------------------- 绘图工具

    private static StringFormat CreateFormat(StringAlignment alignment, StringTrimming? trimming)
    {
        var format = (StringFormat)StringFormat.GenericTypographic.Clone();
        format.Alignment = alignment;
        format.LineAlignment = StringAlignment.Center;
        format.FormatFlags = StringFormatFlags.NoWrap;
        if (trimming is { } value)
        {
            format.Trimming = value;
        }

        return format;
    }

    /// <summary>用 GDI+ 画文字：位图有 alpha 通道，GDI 的 TextRenderer 会留下锯齿。</summary>
    private static void DrawText(
        Graphics g,
        string text,
        Font font,
        Color color,
        RectangleF rect,
        StringAlignment alignment = StringAlignment.Near,
        StringFormat? format = null)
    {
        using var brush = new SolidBrush(color);
        var effective = format ?? (alignment == StringAlignment.Far ? AlignRight
            : alignment == StringAlignment.Center ? CreateFormat(StringAlignment.Center, null)
            : AlignLeft);
        g.DrawString(text, font, brush, rect, effective);
    }

    private static float MeasureText(Graphics g, string text, Font font) =>
        g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;

    private static GraphicsPath RoundedPath(Rectangle rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var arc = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        var arc = new RectangleF(rect.X, rect.Y, d, d);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - d;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - d;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>带轻微起伏的圆形轮廓，模仿参考卡片右侧的装饰圆斑。</summary>
    private static GraphicsPath BlobPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var center = new PointF(rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f));
        const int segments = 72;
        var points = new PointF[segments];

        for (var i = 0; i < segments; i++)
        {
            var angle = i * MathF.Tau / segments;
            var wobble = 1f + (0.035f * MathF.Sin(angle * 3f)) + (0.02f * MathF.Sin((angle * 5f) + 1.1f));
            var r = radius * wobble;
            points[i] = new PointF(center.X + (r * MathF.Cos(angle)), center.Y + (r * MathF.Sin(angle)));
        }

        path.AddPolygon(points);
        return path;
    }

    private static string FormatUptime(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours:00}:{span.Minutes:00}"
            : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "— MB";
        }

        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0} MB";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tooltip.Dispose();
            _cursorTimer.Dispose();
            _surface?.Dispose();
            ReleaseDib();
            _fontBody?.Dispose();
            _fontLabel?.Dispose();
            _fontChip?.Dispose();
            _fontPill?.Dispose();
            _fontHeadline?.Dispose();
            _fontStatLabel?.Dispose();
            _fontStat?.Dispose();
            _fontMicro?.Dispose();
        }

        base.Dispose(disposing);
    }
}

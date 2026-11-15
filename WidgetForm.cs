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
    private const int ShadowMargin = 26;

    /// <summary>卡片圆角半径（WinUI 顶层容器 8px）。</summary>
    private const int CornerRadius = 8;


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

    private void OnThemeChanged()
    {
        if (!IsDisposed)
        {
            RenderSurface();
        }
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
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var state = g.Save();
            g.TranslateTransform(margin, margin);
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

    /// <summary>主操作按钮的文案/图标/角色。</summary>
    private readonly record struct ActionSpec(string Label, string Icon, SurfaceRole Role);

    private void DrawCard(Graphics g, int width, int height)
    {
        _scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * _scale);
        var pad = S(Pad);
        _hits.Clear();

        Fluent.Card(g, new RectangleF(0, 0, width, height), S(CornerRadius), 3);

        var headerY = S(12);
        var buttonW = S(84);
        var buttonH = S(32);

        OrbitMark.Draw(g, new RectangleF(pad, headerY + S(8), S(16), S(16)), Fluent.AccentText, 2f);
        Fluent.Text(g, "DSH-Orbit", TextRole.Body, true, Fluent.TextPrimary,
            new RectangleF(pad + S(22), headerY, S(160), buttonH));

        var openRect = new RectangleF(width - pad - buttonW, headerY, buttonW, buttonH);
        var primaryRect = new RectangleF(openRect.X - S(8) - buttonW, headerY, buttonW, buttonH);
        var openRole = _status.State == DshState.Running ? SurfaceRole.Accent : SurfaceRole.Standard;
        DrawAction(g, primaryRect, PrimaryAction(), "start");
        DrawAction(g, openRect, new ActionSpec("打开", "external", openRole), "open");

        // 端口是整张卡片最重的一行（WinUI Title 28/36）
        var portTop = S(52);
        var portText = _status.Port is { } port ? $"127.0.0.1:{port}" : "未运行";
        var portColor = _status.Port is null
            ? Fluent.TextTertiary
            : _hoverId == "open" ? Fluent.AccentText : Fluent.TextPrimary;
        Fluent.Text(g, portText, TextRole.Title, true, portColor,
            new RectangleF(pad, portTop, width - (2 * pad) - S(184), Fluent.LineHeight(TextRole.Title)),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);

        var portHit = Math.Min(
            Fluent.Measure(g, portText, TextRole.Title, true, _scale) + S(8),
            width - pad - S(8));
        _hits.Add((new Rectangle(pad, portTop, (int)portHit, S(36)), "open"));

        // 状态药丸 + 元信息
        var metaY = S(92);
        var stateColor = StateColor();
        var pillRect = new RectangleF(pad, metaY, S(78), S(20));
        Fluent.Pill(g, pillRect, _status.StateText, stateColor,
            Fluent.Mix(Fluent.Layer, stateColor, 0.18f), true, _scale);

        var metaX = pillRect.Right + S(10);
        var metaRight = (float)(width - pad);
        if (_config.ClickThrough)
        {
            var lockRect = new RectangleF(metaRight - S(60), metaY, S(60), S(20));
            Fluent.Pill(g, lockRect, "已锁定", Fluent.Caution,
                Fluent.Mix(Fluent.Layer, Fluent.Caution, 0.16f), false, _scale);
            _hits.Add((Rectangle.Round(lockRect), "lock"));
            metaRight = lockRect.X - S(8);
        }

        Fluent.Text(g, MetaText(), TextRole.Caption, false, Fluent.TextSecondary,
            new RectangleF(metaX, metaY, Math.Max(0, metaRight - metaX), S(20)),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);

        Fluent.Divider(g, new RectangleF(pad, S(116), width - (2 * pad), 1));

        // 三列统计：标签 12、数值 14 semibold
        var labelY = S(124);
        var valueY = S(142);
        var columnWidth = (width - (2 * pad)) / 3f;
        var columns = Stats();
        for (var i = 0; i < columns.Length; i++)
        {
            var (label, value, color) = columns[i];
            var x = pad + (columnWidth * i);
            Fluent.Text(g, label, TextRole.Caption, false, Fluent.TextTertiary,
                new RectangleF(x, labelY, columnWidth - S(8), S(16)), StringAlignment.Near,
                StringTrimming.None, _scale);
            Fluent.Text(g, value, TextRole.Body, true, color,
                new RectangleF(x, valueY, columnWidth - S(8), S(20)), StringAlignment.Near,
                StringTrimming.EllipsisCharacter, _scale);
        }
    }

    private void DrawAction(Graphics g, RectangleF rect, ActionSpec spec, string id)
    {
        var state = spec.Label == "启动中"
            ? VisualState.Disabled
            : _pressedId == id ? VisualState.Pressed
            : _hoverId == id ? VisualState.Hover
            : VisualState.Rest;

        Fluent.Button(g, rect, spec.Label, spec.Role, state, spec.Icon, _scale);
        _hits.Add((Rectangle.Round(rect), id));
    }

    private ActionSpec PrimaryAction() => _status.State switch
    {
        DshState.Running => new ActionSpec("停止", "square", SurfaceRole.Standard),
        DshState.Starting => new ActionSpec("启动中", "pause", SurfaceRole.Standard),
        _ => new ActionSpec("启动", "play", SurfaceRole.Accent),
    };

    private Color StateColor() => _status.State switch
    {
        DshState.Running => Fluent.Success,
        DshState.Starting => Fluent.Caution,
        DshState.Error => Fluent.Critical,
        _ => Fluent.TextTertiary,
    };

    private string MetaText()
    {
        var parts = new List<string> { $"profile {_status.Profile}" };
        if (_status.PluginTotal is { } total)
        {
            parts.Add(_status.PluginDisabled is > 0
                ? $"插件 {total} · 停 {_status.PluginDisabled}"
                : $"插件 {total}");
        }
        else if (_status.PluginScanning)
        {
            parts.Add("插件 统计中");
        }

        if (_status.Balance is not null)
        {
            parts.Add(_status.OffPeak ? "闲时" : "标准价");
        }

        if (_status.State == DshState.Running && _status.MemoryBytes > 0)
        {
            var metrics = FormatBytes(_status.MemoryBytes);
            if (_status.CpuPercent is { } cpu)
            {
                metrics += $" · {cpu:0}%";
            }

            parts.Add(metrics);
        }

        return string.Join(" · ", parts);
    }

    private (string Label, string Value, Color Color)[] Stats() =>
    [
        ("运行时长", _status.Uptime is { } up ? FormatUptime(up) : "—",
            _status.Uptime is null ? Fluent.TextTertiary : Fluent.TextPrimary),
        ("余额", _status.Balance is { } balance ? $"¥{balance:0.00}" : "—",
            _status.Balance is null ? Fluent.TextTertiary : Fluent.Success),
        ("今日已用", _status.SpentToday is { } spent && spent > 0 ? $"¥{spent:0.0000}" : "¥0.0000",
            Fluent.TextSecondary),
    ];
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
        }

        base.Dispose(disposing);
    }
}

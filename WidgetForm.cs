using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>离屏截图时强制渲染的形态（只供截图，不触发动画）。</summary>
internal enum WidgetPose
{
    /// <summary>按当前动画状态渲染。</summary>
    Auto,

    /// <summary>强制渲染 44×44 贴边图标。</summary>
    Icon,

    /// <summary>强制渲染 320×228 浮层（内容全亮）。</summary>
    Flyout,

    /// <summary>强制渲染收起态：屏边一根 6×28 拉手。</summary>
    Handle,
}

/// <summary>
/// 桌面挂件本体：贴边一枚 44×44 图标（运行中匀速旋转），悬停 500ms 或单击后
/// 「接力变形」成 320×228 浮层。窗口恒为 372×280（卡片 + 四周 26px 阴影外边距），
/// 逐像素透明；锁定穿透时在光标处开「探照灯」。
///
/// 动效只有一条时间轴：一个 16ms 定时器同时驱动旋转角度与变形进度，
/// 没有动画、没有角度变化时不重绘（layered 窗口不靠 WM_PAINT，重绘就是一次全窗推送）。
/// </summary>
internal sealed class WidgetForm : Form
{
    // ---------------------------------------------------------------- 几何（96 DPI）

    /// <summary>浮层卡片宽。</summary>
    private const int CardW = 320;

    /// <summary>浮层卡片高。</summary>
    private const int CardH = 228;

    /// <summary>贴边图标边长。</summary>
    private const int IconSide = 44;

    /// <summary>让位时图标缩到的尺寸。</summary>
    private const int IconRetired = 28;

    /// <summary>卡片四周留给阴影的透明外边距（海拔 3 的阴影最远 25.6px）。</summary>
    private const int ShadowMargin = 26;

    /// <summary>卡片内边距。</summary>
    private const int Pad = 16;

    /// <summary>收起态留在屏内的宽度 = 拉手宽度（其余整张卡片在屏外）。</summary>
    private const int HandleW = 6;

    /// <summary>拉手高度（垂直居中对齐图标/浮层那条中心线）。</summary>
    private const int HandleH = 28;

    /// <summary>收起态热区：从屏幕边缘起 14px（6 可见 + 8 容错），高度 28 + 上下各 16。</summary>
    private const int HandleHotW = 14;
    private const int HandleHotPad = 16;

    // 浮层内部纵向排版（卡片局部坐标，走 4px 栅格）
    private const int HeadTop = 16;      // 头部 20
    private const int PortTop = 40;      // 端口 24
    private const int MetaTop = 68;      // 元信息 16
    private const int DividerTop = 92;   // 分隔线 1
    private const int StatsTop = 104;    // 2×2 第一行（标签与数值同一基线，行高 20）
    private const int StatsPitch = 40;   // 两行行距（基线差 40）
    private const int StatsRowH = 20;    // 每格一行：12 标签 + 14 数值同行
    private const int ButtonsTop = 180;  // 底部按钮 32，底边距 16
    private const int ButtonW = 76;
    private const int ButtonH = 32;
    private const int ButtonGap = 8;
    private const int ColumnGap = 24;
    private const int MarkSide = 20;

    // ---------------------------------------------------------------- 动效时长（ms）

    private const int RevealMs = 190;
    private const int RetireMs = 120;
    private const int GrowMs = 250;
    private const int CollapseMs = 167;
    private const int ReturnMs = 140;
    private const int HideMs = 190;

    /// <summary>图标悬停多久长成浮层。</summary>
    private const int HoverDelayMs = 500;

    /// <summary>光标离开多久开始收。</summary>
    private const int AwayDelayMs = 400;

    /// <summary>内容错峰淡入：140/180/220/260ms 起，490ms 全亮。</summary>
    private const int ContentDoneMs = 490;

    private const int SpinRunningMs = 8000;
    private const int SpinStartingMs = 2400;

    // ---------------------------------------------------------------- 状态

    /// <summary>变形时间轴上的站。</summary>
    private enum Phase
    {
        Hidden,
        Reveal,
        Icon,
        Retire,
        Grow,
        Flyout,
        Collapse,
        Return,
        Hide,
    }

    /// <summary>某一帧的绘制参数（全部由时间推导，便于截图时强制指定）。</summary>
    private readonly record struct Frame(
        float Grow,
        float Panel,
        float IconAlpha,
        float Retire,
        float Content,
        int ContentElapsed,
        float Angle,
        float Handle);

    private readonly WidgetConfig _config;
    private StatusSnapshot _status = new();
    private readonly List<(Rectangle Bounds, string Id)> _hits = new();
    private readonly List<(Rectangle Bounds, string Id)> _snapshotHits = new();

    /// <summary>唯一的驱动定时器（角度 + 变形进度 + 光标轮询）。</summary>
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };

    private Bitmap? _surface;
    private Point? _spotlight;

    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _dibBits;
    private IntPtr _previousObject;
    private int _dibWidth;
    private int _dibHeight;

    private float _scale = 1f;

    // 变形时间轴
    private Phase _phase = Phase.Icon;
    private long _phaseStart;
    private long _lastTick;
    private float _angle;

    // 贴边
    private DockSide _dockSide = DockSide.None;
    private bool _expandLeft = true;
    private float _dockOffset;
    private float _dockFrom;
    private float _dockTarget;
    private long _dockHoldUntil;

    // 光标
    private long _hoverStart;
    private long _awayStart;

    /// <summary>收起之后要先离开图标一次，才允许再次悬停展开（防止原地反复弹）。</summary>
    private bool _hoverArmed = true;

    /// <summary>光标是否压在收起态拉手的热区里（只影响光晕强度）。</summary>
    private bool _handleHover;

    private string? _hoverId;
    private string? _pressedId;
    private bool _dragging;
    private Point _dragCursorOrigin;
    private Point _dragWindowOrigin;

    public event Action? StartStopRequested;
    public event Action? OpenRequested;
    public event Action? LockRequested;
    public event Action? Moved;
    public event Action? StatusChanged;
    public event Action? DockStateChanged;

    public WidgetForm(WidgetConfig config)
    {
        _config = config;
        _scale = SystemDpi();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Fluent.Mica;
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

        _motion.Tick += (_, _) => OnMotionTick();

        Shown += (_, _) =>
        {
            // 未贴边时按窗口所在的半屏决定浮层往哪边展开（贴边由 DockTo 决定）。
            if (_dockSide == DockSide.None)
            {
                SyncExpandDirection();
                // 自由摆放 = 一张完整卡片常驻，启动即进入这个终态。
                EnterFreeForm();
            }

            ApplyZOrder();
            if (_config.ClickThrough)
            {
                ApplyClickThrough(true);
            }

            StartMotion();
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
        _scale = DeviceDpi / 96f;
        ApplyWindowSize();
        SnapDockOffsetToPhase();
        StartMotion();
        RenderSurface();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _scale = e.DeviceDpiNew / 96f;
        ApplyWindowSize();
        SnapDockOffsetToPhase();
        RenderSurface();
    }

    /// <summary>
    /// 换 DPI 之后，把「滑出偏移」按**当前 scale + 当前相位**重新推导，再摆位置。
    ///
    /// 必须重推的理由：`_dockOffset` 是**像素值**（`HiddenOffset()` 内部乘了 scale），
    /// 120%→96% 时旧值是 392，而新 scale 下的正确值是 314——继续用旧值会让窗口整体偏 78px，
    /// 卡片（连同拉手）直接滑出屏幕。同理，动画中途的中间值也不能留，直接落到该相位的静息位置。
    /// </summary>
    private void SnapDockOffsetToPhase()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        // 没贴边（含穿透态）：自由形态 = 完整卡片常驻。除了夹回工作区，
        // 相位也必须掰回 Flyout——否则上一轮遗留的 Hide 相位会让它画成拉手。
        if (_dockSide == DockSide.None)
        {
            EnterFreeForm();
            return;
        }

        switch (_phase)
        {
            // 收起途中/已收起：直接落到「收起完成」，拉手正好贴在屏边。
            case Phase.Hidden:
            case Phase.Hide:
                _dockOffset = HiddenOffset();
                _phase = Phase.Hidden;
                _phaseStart = Environment.TickCount64;
                break;

            // 滑出途中：直接落到「图标已就位」，不留半程状态。
            case Phase.Reveal:
                _dockOffset = 0f;
                _phase = Phase.Icon;
                _phaseStart = Environment.TickCount64;
                break;

            // Icon / Retire / Grow / Flyout / Collapse / Return：全是展开到位的位置。
            default:
                _dockOffset = 0f;
                break;
        }

        ApplyDockPosition(_dockOffset);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _motion.Stop();
        }
        else if (IsHandleCreated)
        {
            StartMotion();
        }
    }

    /// <summary>
    /// 建句柄之前 <see cref="Control.DeviceDpi"/> 还是 96，而 WidgetApp 在 Show 之前就要用
    /// 窗口尺寸算落点——先按系统 DPI 估算，句柄建好/收到 WM_DPICHANGED 后再校准。
    /// </summary>
    private static float SystemDpi()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX / 96f;
        }
        catch
        {
            return 1f;
        }
    }

    /// <summary>窗口 = 卡片 + 四周阴影外边距。直接 SetWindowPos，绕开 WinForms 的边框折算。</summary>
    private void ApplyWindowSize()
    {
        var size = SurfaceSize;
        if (IsHandleCreated)
        {
            _ = Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, size.Width, size.Height,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        else
        {
            Size = size;
        }
    }

    private Size SurfaceSize
    {
        get
        {
            var scale = _scale <= 0f ? 1f : _scale;
            return new Size(
                (int)MathF.Round((CardW + (2 * ShadowMargin)) * scale),
                (int)MathF.Round((CardH + (2 * ShadowMargin)) * scale));
        }
    }

    public void SetStatus(StatusSnapshot status)
    {
        var changed = status.State != _status.State
            || status.Port != _status.Port
            || status.DisplayUrl != _status.DisplayUrl
            || status.Profile != _status.Profile
            || status.Version != _status.Version
            || status.Uptime != _status.Uptime
            || status.PluginTotal != _status.PluginTotal
            || status.PluginDisabled != _status.PluginDisabled
            || status.PluginNames != _status.PluginNames
            || status.PluginScanning != _status.PluginScanning
            || status.Balance != _status.Balance
            || status.SpentToday != _status.SpentToday
            || status.OffPeak != _status.OffPeak
            || status.UntilPriceSwitch != _status.UntilPriceSwitch
            || status.ActiveSessions != _status.ActiveSessions
            || status.Hint != _status.Hint
            || status.HasAuthenticatedUrl != _status.HasAuthenticatedUrl;

        _status = status;

        // layered window 不依赖 WM_PAINT，状态一变就直接重画并推送；没变就不推。
        if (changed)
        {
            RenderSurface();
            StatusChanged?.Invoke();
        }
    }

    public StatusSnapshot Status => _status;


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

    /// <summary>
    /// 打开/关闭点击穿透。
    ///
    /// 语义（用户定死）：**穿透 = 完整卡片常驻**——不贴边、不收成图标、不显示拉手，
    /// 只有光标处的探照灯在动。所以打开穿透时先解除贴边，再进入自由形态（完整卡片 + 夹回工作区）。
    /// 关闭穿透时保持现状；若此时已不在贴边，自由形态本来就不会收起。
    /// </summary>
    public void ApplyClickThrough(bool enabled)
    {
        _config.ClickThrough = enabled;

        if (!IsHandleCreated)
        {
            return;
        }

        if (enabled)
        {
            if (_dockSide != DockSide.None)
            {
                AppPaths.Log("启用点击穿透：解除贴边（穿透与贴边隐藏互斥）");
                Undock();
            }

            EnterFreeForm();
        }
        else
        {
            _spotlight = null;
        }

        var style = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        style = enabled
            ? style | Native.WS_EX_TRANSPARENT
            : style & ~Native.WS_EX_TRANSPARENT;
        _ = Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, style);

        StartMotion();
        RenderSurface();
    }

    /// <summary>
    /// 自由形态（未贴边）：一张完整卡片常驻，永不收起。
    /// 用户定的规则：只有贴边才允许「拉手 → 图标 → 浮层」那套接力；拖出来就是普通挂件。
    /// </summary>
    private void EnterFreeForm()
    {
        ShowFullCardNow();
        ClampIntoWorkingArea();
    }

    /// <summary>
    /// 把形态直接钉成「浮层全出」的静止终态：不播 morph、不留任何计时器在手，
    /// 之后 AdvancePhase 没有可推进的相位，UpdateCursor 在自由形态/穿透下也早退，
    /// 所以不会再自动收起。
    /// </summary>
    private void ShowFullCardNow()
    {
        _phase = Phase.Flyout;
        _phaseStart = Environment.TickCount64;
        _dockOffset = 0f;
        _hoverStart = 0;
        _awayStart = 0;
        _hoverId = null;
        _pressedId = null;
        _handleHover = false;
    }

    /// <summary>
    /// 把整个窗口（含四周 26px 阴影边距）夹进当前工作区。
    /// 解除贴边的那一刻窗口可能大半在屏外（之前是收起态 / 滑出中），不夹回来就看不到整张卡片。
    /// </summary>
    private void ClampIntoWorkingArea()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var area = Screen.FromControl(this).WorkingArea;
        var x = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));

        if (x != Left || y != Top)
        {
            Location = new Point(x, y);
        }
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

    // ---------------------------------------------------------------- 几何

    private float W(float design) => design * _scale;

    private int S(float design) => (int)MathF.Round(design * _scale);

    private static float LerpF(float from, float to, float t) => from + ((to - from) * t);

    /// <summary>卡片本体在窗口里的矩形（含阴影外边距的平移）。</summary>
    private RectangleF CardFrame()
    {
        var margin = W(ShadowMargin);
        return new RectangleF(margin, margin, W(CardW), W(CardH));
    }

    /// <summary>44×44 图标槽位：贴边时贴住贴屏侧，垂直居中于卡片。</summary>
    private RectangleF IconSlotRect()
    {
        var card = CardFrame();
        var side = W(IconSide);
        var x = _expandLeft ? card.Right - side : card.Left;
        return new RectangleF(x, card.Y + ((card.Height - side) / 2f), side, side);
    }

    /// <summary>
    /// 变形矩形：锚点固定在贴屏侧边缘 + 垂直中心，宽高同时插值。
    /// 圆角全程 16，所以 morph 过程中任何一帧都读得懂「这张卡在长大」。
    /// </summary>
    private RectangleF PanelRect(float grow)
    {
        var card = CardFrame();
        var width = LerpF(W(IconSide), card.Width, grow);
        var height = LerpF(W(IconSide), card.Height, grow);
        var centerY = card.Y + (card.Height / 2f);
        var anchor = _expandLeft ? card.Right : card.Left;
        return new RectangleF(_expandLeft ? anchor - width : anchor, centerY - (height / 2f), width, height);
    }

    private Rectangle ToScreen(RectangleF rect) => new(
        Left + (int)MathF.Round(rect.X),
        Top + (int)MathF.Round(rect.Y),
        (int)MathF.Round(rect.Width),
        (int)MathF.Round(rect.Height));

    private Rectangle CardScreenRect() => ToScreen(PanelRect(1f));

    private Rectangle IconScreenRect() => ToScreen(IconSlotRect());

    // ---------------------------------------------------------------- 时间轴

    private void StartMotion()
    {
        if (!_motion.Enabled)
        {
            _lastTick = Environment.TickCount64;
            _motion.Start();
        }
    }

    private int SpinPeriodMs() => _status.State switch
    {
        DshState.Running => SpinRunningMs,
        DshState.Starting => SpinStartingMs,
        _ => 0,
    };

    private void OnMotionTick()
    {
        var now = Environment.TickCount64;
        var delta = Math.Max(0, now - _lastTick);
        _lastTick = now;

        var dirty = false;

        var spin = SpinPeriodMs();
        if (spin > 0)
        {
            _angle = (_angle + (delta * 360f / spin)) % 360f;

            // 收起态画面上只有一根静止的拉手，角度变化不需要重绘；
            // 角度照样累加，滑出来的那一刻图标不会跳相位。
            if (_phase != Phase.Hidden)
            {
                dirty = true;
            }
        }

        if (AdvancePhase(now))
        {
            dirty = true;
        }

        if (UpdateCursor(now))
        {
            dirty = true;
        }

        if (dirty)
        {
            RenderSurface();
        }
    }

    /// <summary>推进时间轴；返回 true 表示这一帧的画面变了（需要重绘）。</summary>
    private bool AdvancePhase(long now)
    {
        // 穿透期间形态被钉在「浮层全出」，任何相位推进都可能把它带回图标/拉手——直接短路。
        if (_config.ClickThrough)
        {
            return false;
        }

        // 自由形态没有"收起"这回事：相位只该是 Flyout，任何推进都直接跳过。
        if (_dockSide == DockSide.None)
        {
            return false;
        }

        var elapsed = now - _phaseStart;
        switch (_phase)
        {
            case Phase.Reveal:
                _dockOffset = _dockFrom * (1f - Fluent.EaseOut(elapsed / (float)RevealMs));
                ApplyDockPosition(_dockOffset);
                if (elapsed >= RevealMs)
                {
                    _dockOffset = 0f;
                    ApplyDockPosition(_dockOffset);
                    EnterPhase(Phase.Icon, now);
                }

                // 只有窗口在平移，位图没变。
                return false;

            case Phase.Hide:
                _dockOffset = _dockFrom + ((_dockTarget - _dockFrom) * Fluent.EaseOut(elapsed / (float)HideMs));
                ApplyDockPosition(_dockOffset);
                if (elapsed >= HideMs)
                {
                    _dockOffset = _dockTarget;
                    ApplyDockPosition(_dockOffset);
                    EnterPhase(Phase.Hidden, now);
                }

                return false;

            case Phase.Retire:
                if (elapsed >= RetireMs)
                {
                    EnterPhase(Phase.Grow, now);
                }

                return true;

            case Phase.Grow:
                if (elapsed >= GrowMs)
                {
                    EnterPhase(Phase.Flyout, now);
                }

                return true;

            case Phase.Collapse:
                if (elapsed >= CollapseMs)
                {
                    EnterPhase(Phase.Return, now);
                }

                return true;

            case Phase.Return:
                if (elapsed >= ReturnMs)
                {
                    EnterPhase(Phase.Icon, now);
                }

                return true;

            default:
                return false;
        }
    }

    private void EnterPhase(Phase phase, long now)
    {
        _phase = phase;
        _phaseStart = now;
        _hoverStart = 0;
        _awayStart = 0;
        if (phase is Phase.Retire or Phase.Collapse)
        {
            _hoverArmed = false;
        }

        if (phase == Phase.Icon)
        {
            _hoverId = null;
        }
    }

    private Frame CurrentFrame()
    {
        // 自由形态（未贴边）永远是完整卡片。这是兜底：即使某条路径漏了相位复位，
        // 也不会在拖出来的挂件上露出图标或拉手。
        if (_dockSide == DockSide.None && _phase != Phase.Flyout)
        {
            return new Frame(1f, 1f, 0f, 1f, 1f, ContentDoneMs, _angle, 0f);
        }

        var elapsed = Environment.TickCount64 - _phaseStart;
        switch (_phase)
        {
            // 收起态：卡片整张在屏外，只有屏边那根拉手留在屏内。
            case Phase.Hidden:
                return new Frame(0f, 0f, 0f, 0f, 0f, 0, _angle, 1f);

            // 滑出/滑回的途中：图标在滑、拉手一直贴在屏边（图层顺序上图标盖住它）。
            case Phase.Reveal:
            case Phase.Hide:
                return new Frame(0f, 0f, 1f, 0f, 0f, 0, _angle, 1f);

            case Phase.Icon:
                return new Frame(0f, 0f, 1f, 0f, 0f, 0, _angle, 0f);

            // 让位：图标 44→28 + 内移 8 + 淡出；同一时刻浮层在它背后慢慢显影，
            // 接力点（Retire 末尾 → Grow 开头）两边都是「44 的圆角底板」，不会闪一下。
            case Phase.Retire:
            {
                var p = Fluent.EaseOut(elapsed / (float)RetireMs);
                return new Frame(0f, 0.9f * p, 1f - p, p, 0f, 0, _angle, 0f);
            }

            case Phase.Grow:
            {
                var p = Fluent.Clamp01(elapsed / (float)GrowMs);
                var panel = 0.9f + (0.1f * Fluent.Clamp01(elapsed / 100f));
                return new Frame(Fluent.EaseEmphasized(p), panel, 0f, 1f, 1f, (int)elapsed, _angle, 0f);
            }

            case Phase.Flyout:
                return new Frame(1f, 1f, 0f, 1f, 1f, ContentDoneMs, _angle, 0f);

            case Phase.Collapse:
            {
                var p = Fluent.Clamp01(elapsed / (float)CollapseMs);
                var grow = 1f - Fluent.EaseOut(p);
                var content = Fluent.Clamp01(1f - (p * 2f));
                return new Frame(grow, 1f, 0f, 1f, content, ContentDoneMs, _angle, 0f);
            }

            // 图标再现：浮层停在槽位大小淡出，图标同时淡入——交叉溶解，不出现"空一帧"。
            case Phase.Return:
            {
                var p = Fluent.EaseOut(elapsed / (float)ReturnMs);
                return new Frame(0f, 1f - p, p, 1f - p, 0f, 0, _angle, 0f);
            }

            default:
                return new Frame(0f, 0f, 1f, 0f, 0f, 0, _angle, 0f);
        }
    }

    // ---------------------------------------------------------------- 贴边 / 穿透

    public DockSide DockSide => _dockSide;

    /// <summary>
    /// 贴边收起时窗口要平移的距离：整张卡片移出屏幕，只把拉手那 6px 留在屏内。
    /// 用取整后的拉手宽度，保证「留在屏内的像素」正好等于画出来的拉手宽度。
    /// </summary>
    private float HiddenOffset() => Math.Max(0f, W(CardW) - S(HandleW));

    public void DockTo(DockSide side, bool hidden)
    {
        if (side == DockSide.None)
        {
            Undock();
            return;
        }

        // 穿透态收不到 hover，贴边会变成"挂在屏幕外点不到的死块"，两个轮询器还会互抢光标。
        if (_config.ClickThrough)
        {
            AppPaths.Log("忽略贴边请求：当前是点击穿透态（贴边与穿透互斥）");
            return;
        }

        _dockSide = side;
        _expandLeft = side == DockSide.Right;
        _dockOffset = hidden ? HiddenOffset() : 0f;
        _hoverStart = 0;
        _awayStart = 0;
        _dockHoldUntil = 0;

        if (hidden)
        {
            _phase = Phase.Hidden;
            _phaseStart = Environment.TickCount64;
        }
        else if (_phase is Phase.Hidden or Phase.Reveal or Phase.Hide)
        {
            _phase = Phase.Icon;
            _phaseStart = Environment.TickCount64;
        }

        ApplyDockPosition(_dockOffset);
        StartMotion();
        DockStateChanged?.Invoke();
        RenderSurface();
    }

    /// <summary>
    /// 解除贴边 → 自由形态：一张完整卡片常驻，不再有拉手/图标/收起。
    /// 位置交给 EnterFreeForm 里的 ClampIntoWorkingArea：收起态时窗口大半在屏外，夹一下正好落回屏内；
    /// 而如果用户是「拖着那根拉手把它拖出来」的，就保留落点（这里不能再按 HiddenOffset 回退，
    /// 否则窗口会被二次挪动）。
    /// </summary>
    public void Undock()
    {
        if (_dockSide == DockSide.None)
        {
            return;
        }

        _dockSide = DockSide.None;
        _dockOffset = 0f;
        _hoverStart = 0;
        _awayStart = 0;
        _handleHover = false;

        EnterFreeForm();
        DockStateChanged?.Invoke();
        RenderSurface();
    }

    /// <summary>把窗口摆到贴边的水平位置。<paramref name="offset"/> 是滑出距离（像素）。</summary>
    private void ApplyDockPosition(float offset)
    {
        if (_dockSide == DockSide.None || !IsHandleCreated)
        {
            return;
        }

        var screen = Screen.FromControl(this).WorkingArea;
        var margin = W(ShadowMargin);
        var cardWidth = W(CardW);
        var target = _dockSide == DockSide.Right
            ? screen.Right - cardWidth - margin + offset
            : screen.Left - margin - offset;

        var left = (int)MathF.Round(target);
        if (Left != left)
        {
            Left = left;
        }
    }

    /// <summary>
    /// 从贴边隐藏状态滑出来，并在 <paramref name="holdMs"/> 内不自动收起（托盘单击用）。
    /// 自由形态（未贴边）没有"收起"这回事，直接忽略。
    /// </summary>
    public void RevealFromDock(int holdMs)
    {
        if (!IsHandleCreated || _dockSide == DockSide.None)
        {
            return;
        }

        _dockHoldUntil = Environment.TickCount64 + Math.Max(0, holdMs);
        StartMotion();

        _hoverStart = 0;
        _awayStart = 0;
        if (_phase is Phase.Hidden or Phase.Hide)
        {
            StartReveal();
        }
    }

    private void StartReveal()
    {
        _dockFrom = _dockOffset;
        EnterPhase(Phase.Reveal, Environment.TickCount64);
    }

    private void StartHide()
    {
        if (_dockSide == DockSide.None)
        {
            return;
        }

        _dockFrom = _dockOffset;
        _dockTarget = HiddenOffset();
        EnterPhase(Phase.Hide, Environment.TickCount64);
    }

    /// <summary>
    /// 收起态拉手在窗口里的矩形（6×28，垂直居中在卡片中心线上）。
    ///
    /// 关键：拉手是「贴在屏幕边缘」的，而屏幕边缘在窗口坐标里会随滑出距离移动——
    /// 窗口往右滑 offset，屏边在窗口里的 x 就左移 offset。所以这里用 _dockOffset 反推：
    /// 收起完成时它正好落在窗口仅剩的那 6px 可见条里；完全展开时它落在贴屏侧边缘、
    /// 被图标（图层在它之上）完全盖住，所以任何时候画它都不会露馅。
    /// </summary>
    private RectangleF HandleRect()
    {
        var card = CardFrame();
        var width = S(HandleW);
        var height = S(HandleH);
        var edge = _expandLeft ? card.Right - _dockOffset : card.Left + _dockOffset;
        var x = _expandLeft ? edge - width : edge;
        var y = card.Y + ((card.Height - height) / 2f);
        return new RectangleF(x, y, width, height);
    }

    /// <summary>收起态热区：以拉手为中心、屏幕边缘起 14px（6 可见 + 8 容错）、高 60。</summary>
    private Rectangle HotZone()
    {
        var screen = Screen.FromControl(this).WorkingArea;
        var handle = ToScreen(HandleRect());
        var width = S(HandleHotW);
        var pad = S(HandleHotPad);
        var height = S(HandleH) + (2 * pad);
        var top = handle.Top - pad;
        return _dockSide == DockSide.Right
            ? new Rectangle(screen.Right - width, top, width, height)
            : new Rectangle(screen.Left, top, width, height);
    }

    /// <summary>松手时判断是否贴边：靠近左右边就吸附，否则解除。</summary>
    private void HandleDrop()
    {
        var screen = Screen.FromControl(this).WorkingArea;
        var card = CurrentVisibleScreenRect();
        var snap = S(28);

        if (_config.EdgeHideEnabled && card.Right >= screen.Right - snap)
        {
            DockTo(DockSide.Right, hidden: false);
            return;
        }

        if (_config.EdgeHideEnabled && card.Left <= screen.Left + snap)
        {
            DockTo(DockSide.Left, hidden: false);
            return;
        }

        // 没吸附到边 → 自由形态：直接落到完整卡片（不再有图标/拉手），并从落点继续可拖。
        if (_dockSide != DockSide.None)
        {
            Undock();
            Moved?.Invoke();
            RenderSurface();
            return;
        }

        // 本来就没贴边：只记住"将来贴边/展开"的方向（自由形态不再为图标位置挪窗口）。
        _expandLeft = (Left + (Width / 2)) > (screen.Left + (screen.Width / 2));
        Moved?.Invoke();
        RenderSurface();
    }

    /// <summary>按窗口所在的半屏选展开方向（启动时用）。</summary>
    private void SyncExpandDirection()
    {
        var screen = Screen.FromControl(this).WorkingArea;
        _expandLeft = (Left + (Width / 2)) > (screen.Left + (screen.Width / 2));
    }

    private Rectangle CurrentVisibleScreenRect()
    {
        var frame = CurrentFrame();
        var rect = frame.Grow > 0.5f ? PanelRect(frame.Grow) : IconSlotRect();
        return ToScreen(rect);
    }

    /// <summary>穿透时跟随光标：光标附近把卡片挖成渐变透明，露出下面的内容。</summary>
    private bool TrackSpotlight()
    {
        var point = PointToClient(Cursor.Position);
        Point? next = ClientRectangle.Contains(point) ? point : null;
        if (next == _spotlight)
        {
            return false;
        }

        _spotlight = next;
        return true;
    }

    // ---------------------------------------------------------------- 光标 / 交互

    /// <summary>每帧轮询光标：热区、悬停计时、离开计时、命中高亮、探照灯。返回是否需要重绘。</summary>
    private bool UpdateCursor(long now)
    {
        if (_config.ClickThrough)
        {
            // 穿透态收不到 hover，只维护探照灯；形态保持图标。
            return TrackSpotlight();
        }

        var dirty = false;
        if (_spotlight is not null)
        {
            _spotlight = null;
            dirty = true;
        }

        if (_dragging)
        {
            _hoverStart = 0;
            _awayStart = 0;
            return dirty;
        }

        var held = now < _dockHoldUntil;
        if (held)
        {
            _hoverStart = 0;
            _awayStart = 0;
        }

        var point = PointToClient(Cursor.Position);

        var hit = _phase is Phase.Icon or Phase.Flyout ? HitTest(point) : null;
        if (hit != _hoverId)
        {
            _hoverId = hit;
            dirty = true;
        }

        // 收起态的拉手悬停：只提亮光晕，不参与命中表（它就在屏幕最边缘，改光标反而更烦）。
        var handleShown = _phase is Phase.Hidden or Phase.Reveal or Phase.Hide;
        var handleHover = handleShown && HotZone().Contains(Cursor.Position);
        if (handleHover != _handleHover)
        {
            _handleHover = handleHover;
            dirty = true;
        }

        var wanted = hit is not null ? Cursors.Hand : Cursors.Default;
        if (Cursor != wanted)
        {
            Cursor = wanted;
        }

        // 自由形态（未贴边）：一张完整卡片常驻——光标离开不做任何事，只剩上面的 hover 命中。
        // 收起/拉手/图标这一整套只属于贴边，所以这里一刀切掉所有相位推进。
        if (_dockSide == DockSide.None)
        {
            _hoverStart = 0;
            _awayStart = 0;
            return dirty;
        }

        switch (_phase)
        {
            case Phase.Hidden:
                if (handleHover)
                {
                    StartReveal();
                    dirty = true;
                }

                break;

            case Phase.Icon:
            {
                if (IconScreenRect().Contains(Cursor.Position))
                {
                    _awayStart = 0;
                    if (!_hoverArmed)
                    {
                        // 刚收起：光标还压在图标上，先离开一次再允许悬停展开，
                        // 否则会「收起 → 又长出来」地反复弹。
                        break;
                    }

                    if (_hoverStart == 0)
                    {
                        _hoverStart = now;
                    }
                    else if (now - _hoverStart >= HoverDelayMs)
                    {
                        StartGrow();
                        dirty = true;
                    }
                }
                else
                {
                    _hoverStart = 0;
                    _hoverArmed = true;
                    if (!held && _dockSide != DockSide.None)
                    {
                        if (_awayStart == 0)
                        {
                            _awayStart = now;
                        }
                        else if (now - _awayStart >= AwayDelayMs)
                        {
                            StartHide();
                        }
                    }
                }

                break;
            }

            case Phase.Flyout:
            {
                // 卡片外扩 8px 内都算"还在浮层上"，避免贴边时指针擦过就收。
                var zone = CardScreenRect();
                zone.Inflate(S(8), S(8));
                if (zone.Contains(Cursor.Position) || held)
                {
                    _awayStart = 0;
                }
                else if (_awayStart == 0)
                {
                    _awayStart = now;
                }
                else if (now - _awayStart >= AwayDelayMs)
                {
                    StartCollapse();
                    dirty = true;
                }

                break;
            }
        }

        return dirty;
    }

    /// <summary>图标 → 浮层：先让位（120ms），再长大（250ms）。只有贴边才有图标态。</summary>
    private void StartGrow()
    {
        if (_dockSide == DockSide.None || _phase != Phase.Icon)
        {
            return;
        }

        EnterPhase(Phase.Retire, Environment.TickCount64);
        _hoverId = null;
        RenderSurface();
    }

    /// <summary>浮层 → 图标：缩回槽位（167ms），图标再淡入（140ms）。只有贴边才收起。</summary>
    private void StartCollapse()
    {
        if (_dockSide == DockSide.None || _phase != Phase.Flyout)
        {
            return;
        }

        EnterPhase(Phase.Collapse, Environment.TickCount64);
        _hoverId = null;
        RenderSurface();
    }

    private string? HitTest(Point client)
    {
        foreach (var (bounds, id) in _hits)
        {
            if (bounds.Contains(client))
            {
                return id;
            }
        }

        return null;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _pressedId = HitTest(e.Location);
        _dragging = false;
        _dragCursorOrigin = Cursor.Position;
        _dragWindowOrigin = Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

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
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (e.Button == MouseButtons.Right)
        {
            // 右键上锁。贴边形态只有「图标」右键触发，避免在浮层按钮区误触；
            // 自由形态没有图标，所以整张卡片任意位置右键都算。
            // 上锁后收不到鼠标，解锁只能走托盘（有意为之）。
            if (_dockSide == DockSide.None || HitTest(e.Location) == "icon")
            {
                LockRequested?.Invoke();
            }

            return;
        }

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
            case "icon":
                StartGrow();
                break;
            case "start":
                StartStopRequested?.Invoke();
                break;
            case "port":
            case "open":
                OpenRequested?.Invoke();
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

    // ---------------------------------------------------------------- 渲染

    /// <summary>供离屏测试取一帧（整窗位图，含四周阴影外边距）。</summary>
    internal Bitmap? Snapshot()
    {
        RenderSurface();
        return _surface is null ? null : new Bitmap(_surface);
    }

    /// <summary>强制渲染指定形态，供离屏截图；不推进时间轴、不动命中表、不推送窗口。</summary>
    internal Bitmap? SnapshotPose(WidgetPose pose)
    {
        var frame = pose switch
        {
            WidgetPose.Icon => new Frame(0f, 0f, 1f, 0f, 0f, 0, _angle, 0f),
            WidgetPose.Flyout => new Frame(1f, 1f, 0f, 1f, 1f, ContentDoneMs, _angle, 0f),
            // 收起态：只有屏边那根拉手，图标/浮层都不画（它们在屏外）。
            WidgetPose.Handle => new Frame(0f, 0f, 0f, 0f, 0f, 0, _angle, 1f),
            _ => CurrentFrame(),
        };

        var size = SurfaceSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            DrawFrame(g, frame, _snapshotHits);
        }

        return bitmap;
    }

    /// <summary>把当前形态画进 ARGB 位图，再预乘 alpha 推到 layered window。</summary>
    private void RenderSurface()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        var size = SurfaceSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return;
        }

        if (_surface is null || _surface.Width != size.Width || _surface.Height != size.Height)
        {
            _surface?.Dispose();
            _surface = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        }

        using (var g = Graphics.FromImage(_surface))
        {
            g.Clear(Color.Transparent);
            DrawFrame(g, CurrentFrame(), _hits);
        }

        if (_config.ClickThrough && _spotlight is { } spotlight)
        {
            ApplySpotlight(spotlight, size.Width, size.Height);
        }

        PushLayered(size.Width, size.Height);
    }

    protected override void OnPaint(PaintEventArgs e) => RenderSurface();

    private void DrawFrame(Graphics g, in Frame frame, List<(Rectangle Bounds, string Id)> hits)
    {
        // 分层 / 透明表面一律灰度抗锯齿：ClearType 需要不透明底色，画在透明面上会出彩边。
        Fluent.AntiAlias(g, crisp: false);
        hits.Clear();

        // 拉手画在最底层：完全展开时它落在贴屏侧边缘，正好被图标整块盖住。
        if (frame.Handle > 0.001f)
        {
            DrawCollapsedHandle(g, frame.Handle);
        }

        if (frame.Panel > 0.001f)
        {
            DrawFlyout(g, frame, hits);
        }

        if (frame.IconAlpha > 0.001f)
        {
            DrawIcon(g, frame, hits);
        }
    }

    // ---------------------------------------------------------------- 绘制：收起态拉手

    /// <summary>
    /// 收起态留在屏边的那根 6×28 小把手：状态色实心 + 外侧一圈低透明度同色光晕。
    /// 它是用户「知道东西收在这儿」的唯一线索，所以颜色跟图标同源（状态色），
    /// 且不做尺寸动画——6px 一变宽就会跳。
    /// </summary>
    private void DrawCollapsedHandle(Graphics g, float alpha)
    {
        // 穿透态是桌面挂件形态、不贴边，也就不该有拉手。
        if (_dockSide == DockSide.None)
        {
            return;
        }

        var rect = HandleRect();
        var color = StateColor();

        // 光晕两层递减：近层 8%、外层 4%（悬停 14% / 7% 并各外扩 2px）。
        var near = _handleHover ? S(6) : S(4);
        var far = _handleHover ? S(10) : S(8);
        var nearAlpha = _handleHover ? 0.14f : 0.08f;
        var farAlpha = _handleHover ? 0.07f : 0.04f;
        DrawHandleGlow(g, rect, far, farAlpha, color, alpha);
        DrawHandleGlow(g, rect, near, nearAlpha, color, alpha);

        // 圆角：贴屏侧两角 0、内侧两角 RControl（4）。6px 宽上不要用药丸，否则会变半圆。
        var r = W(Fluent.RControl);
        var (tl, tr, br, bl) = _expandLeft ? (r, 0f, 0f, r) : (0f, r, r, 0f);
        using var path = CornerPath(rect, tl, tr, br, bl);
        Fluent.Fill(g, path, Tint(color, alpha));
    }

    private static void DrawHandleGlow(
        Graphics g, RectangleF handle, float spread, float glowAlpha, Color color, float alpha)
    {
        if (spread <= 0.1f || glowAlpha <= 0.001f)
        {
            return;
        }

        var rect = RectangleF.Inflate(handle, spread, spread);
        var radius = Math.Min(rect.Width, rect.Height) / 2f;
        using var path = Fluent.Rounded(rect, radius);
        Fluent.Fill(g, path, Tint(color, glowAlpha * alpha));
    }

    // ---------------------------------------------------------------- 绘制：浮层

    private void DrawFlyout(Graphics g, in Frame frame, List<(Rectangle Bounds, string Id)> hits)
    {
        var rect = PanelRect(frame.Grow);
        var radius = W(Fluent.RTile);
        var alpha = frame.Panel;

        // 圆角全程 16（morph 中也不变），阴影靠 26px 外边距落在窗口内。
        // 这里按 Fluent.Card 的同一套外观手绘：浮层要能和图标交叉溶解，
        // 而 Card 的描边不接受 alpha（淡入时会留下一条发亮的边）。
        if (alpha > 0.05f)
        {
            Fluent.Shadow(g, rect, radius, 3);
        }

        using (var path = CornerPath(rect, radius, radius, radius, radius))
        {
            Fluent.Fill(g, path, Tint(Fluent.Layer, alpha));
            Fluent.Stroke(g, path, Tint(Fluent.StrokeStrong, alpha), 1f);

            var state = g.Save();
            g.SetClip(path, CombineMode.Intersect);
            DrawFlyoutContent(g, CardFrame(), frame, frame.Grow >= 0.98f && alpha >= 0.99f, hits);
            g.Restore(state);
        }
    }

    private void DrawFlyoutContent(
        Graphics g,
        RectangleF card,
        in Frame frame,
        bool interactive,
        List<(Rectangle Bounds, string Id)> hits)
    {
        var pad = W(Pad);
        var left = card.X + pad;
        var width = card.Width - (2 * pad);
        var accent = StateColor();

        // 头部：20px 旋转标记 + 14 semibold 标题。标记是「从贴边小图标接力过来的同一个东西」，
        // 所以它不等 140ms 的错峰，跟着槽位长大就开始显影——否则 morph 前半段是一块空白板。
        var head = GroupAlpha(frame, 140);
        var markAlpha = frame.Content * Fluent.Clamp01(frame.ContentElapsed / 180f);
        var markCenter = new PointF(left + (W(MarkSide) / 2f), card.Y + W(HeadTop) + (W(MarkSide) / 2f));
        DrawMark(g, markCenter, W(MarkSide), accent, markAlpha, frame.Angle);
        Fluent.Text(g, "DSH-Orbit", TextRole.Body, true, Tint(Fluent.TextPrimary, head),
            new RectangleF(left + W(MarkSide) + W(8), card.Y + W(HeadTop), width - W(MarkSide) - W(8), W(20)));

        // 端口：整张浮层最重的一行（18 semibold），可点、悬停变强调色 + 下划线
        var portAlpha = GroupAlpha(frame, 180);
        var port = _status.Port;
        var hasPort = port is not null;
        var portText = port is { } value ? $"127.0.0.1:{value}" : "未运行";
        var portRect = new RectangleF(left, card.Y + W(PortTop), width, W(24));
        var portHover = interactive && hasPort && _hoverId == "port";
        var portColor = !hasPort
            ? Fluent.TextTertiary
            : portHover ? Fluent.AccentText : Fluent.TextPrimary;

        Fluent.Text(g, portText, TextRole.BodyLarge, true, Tint(portColor, portAlpha), portRect,
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);

        var portWidth = Math.Min(Fluent.Measure(g, portText, TextRole.BodyLarge, true, _scale), width);
        if (portHover)
        {
            Fluent.Fill(g,
                new RectangleF(left, portRect.Bottom - W(2), portWidth, Math.Max(1f, W(1))),
                Tint(Fluent.AccentText, portAlpha));
        }

        if (interactive && hasPort)
        {
            hits.Add((Rectangle.Round(new RectangleF(left, portRect.Y, portWidth + W(8), portRect.Height)), "port"));
        }

        // 元信息：hint 优先（启动失败 / 没装 dsh 时，否则这行信息就只剩托盘气泡了）
        var metaAlpha = GroupAlpha(frame, 220);
        var hint = _status.Hint;
        var hasHint = !string.IsNullOrWhiteSpace(hint);
        var metaText = hasHint && hint is not null ? hint : MetaText();
        var metaColor = hasHint ? Fluent.Caution : Fluent.TextTertiary;
        Fluent.Text(g, metaText, TextRole.Caption, false, Tint(metaColor, metaAlpha),
            new RectangleF(left, card.Y + W(MetaTop), width, W(16)),
            StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);

        // 分隔线
        Fluent.Fill(g, new RectangleF(left, card.Y + W(DividerTop), width, Math.Max(1f, W(1))),
            Tint(Fluent.DividerColor, metaAlpha));

        // 2×2 指标：标签 12 tertiary，数值 14 semibold 右对齐（GDI+ 开不了 tnum，右对齐达到同样的列对齐）
        var statsAlpha = GroupAlpha(frame, 260);
        var column = (width - W(ColumnGap)) / 2f;
        var row1 = card.Y + W(StatsTop);
        var row2 = row1 + W(StatsPitch);

        var peak = PeakCell();
        DrawMetric(g, left, row1, column, statsAlpha,
            "运行时长", _status.Uptime is { } up ? FormatUptime(up) : "—",
            _status.Uptime is null ? Fluent.TextTertiary : Fluent.TextPrimary);
        DrawMetric(g, left + column + W(ColumnGap), row1, column, statsAlpha,
            "余额", _status.Balance is { } balance ? $"¥{balance:0.00}" : "—",
            _status.Balance is null ? Fluent.TextTertiary : Fluent.TextPrimary);
        DrawMetric(g, left, row2, column, statsAlpha,
            "今日已用", _status.SpentToday is { } spent ? $"¥{spent:0.0000}" : "¥0.0000",
            Fluent.TextPrimary);
        DrawMetric(g, left + column + W(ColumnGap), row2, column, statsAlpha, "峰谷", peak.Text, peak.Color);

        // 底部按钮组：右对齐、各 76 宽、间距 8。
        // 穿透态下按钮点不到（鼠标直接落到桌面），所以整组不画、命中区也不注册；
        // 但槽位高度保持不变（32 高 + 底边距 16），卡片高度与 2×2 位置一点都不动。
        var buttonAlpha = GroupAlpha(frame, 260);
        var buttonY = card.Y + W(ButtonsTop);

        if (_config.ClickThrough)
        {
            Fluent.Text(g, "鼠标穿透 · 点击落到桌面", TextRole.Caption, false,
                Tint(Fluent.TextTertiary, buttonAlpha),
                new RectangleF(left, buttonY + W((ButtonH - 16) / 2f), width, W(16)),
                StringAlignment.Near, StringTrimming.EllipsisCharacter, _scale);
            return;
        }

        var openRect = new RectangleF(card.X + card.Width - pad - W(ButtonW), buttonY, W(ButtonW), W(ButtonH));
        var startRect = new RectangleF(openRect.X - W(ButtonGap) - W(ButtonW), buttonY, W(ButtonW), W(ButtonH));
        var primary = PrimaryAction();
        var openRole = _status.State == DshState.Running ? SurfaceRole.Accent : SurfaceRole.Standard;

        DrawActionButton(g, startRect, primary.Label, primary.Icon, primary.Role,
            ButtonState("start", primary.Disabled), buttonAlpha);
        DrawActionButton(g, openRect, "打开", "external", openRole,
            ButtonState("open", false), buttonAlpha);

        if (interactive)
        {
            hits.Add((Rectangle.Round(startRect), "start"));
            hits.Add((Rectangle.Round(openRect), "open"));
        }
    }

    /// <summary>
    /// 一格指标：标签 12 tertiary 在左、数值 14 semibold 右对齐到格子右缘，**同一行同一基线**。
    /// 四格数值都画在等高（20）矩形里，所以基线天然对齐；GDI+ 开不了 tnum，靠右对齐达到同样的列对齐。
    /// </summary>
    private void DrawMetric(
        Graphics g, float x, float y, float width, float alpha,
        string label, string value, Color valueColor)
    {
        var row = new RectangleF(x, y, width, W(StatsRowH));
        Fluent.Text(g, label, TextRole.Caption, false, Tint(Fluent.TextTertiary, alpha),
            row, StringAlignment.Near, StringTrimming.None, _scale);
        Fluent.Text(g, value, TextRole.Body, true, Tint(valueColor, alpha),
            row, StringAlignment.Far, StringTrimming.EllipsisCharacter, _scale);
    }

    /// <summary>
    /// 按钮自己画：分层窗口的错峰淡入需要把 alpha 乘进每个颜色，
    /// Fluent.Button 不接受 alpha，这里按同一套几何（32 高、4 圆角、16 图标）重画一遍。
    /// </summary>
    private void DrawActionButton(
        Graphics g, RectangleF rect, string label, string icon,
        SurfaceRole role, VisualState state, float alpha)
    {
        if (alpha <= 0.004f)
        {
            return;
        }

        var radius = W(Fluent.RControl);
        var fill = Fluent.FillOf(role, state);
        using (var path = Fluent.Rounded(rect, radius))
        {
            if (fill != Color.Transparent)
            {
                Fluent.Fill(g, path, Tint(fill, alpha));
            }

            if (role is SurfaceRole.Standard or SurfaceRole.Danger)
            {
                Fluent.Stroke(g, path, Tint(Fluent.StrokeStrong, alpha), 1f);
            }
        }

        var foreground = Tint(Fluent.TextOf(role, state), alpha);
        var font = Fluent.Font(TextRole.Body, false, _scale);
        var iconSize = W(16);
        var textWidth = Fluent.Measure(g, label, font);
        var total = iconSize + W(8) + textWidth;
        var startX = rect.X + ((rect.Width - total) / 2f);
        var iconRect = new RectangleF(startX, rect.Y + ((rect.Height - iconSize) / 2f), iconSize, iconSize);

        Icons.Draw(g, icon, iconRect, foreground, 2f);
        Fluent.Text(g, label, font, foreground,
            new RectangleF(iconRect.Right + W(8), rect.Y, textWidth + W(4), rect.Height));
    }

    private VisualState ButtonState(string id, bool disabled)
    {
        if (disabled)
        {
            return VisualState.Disabled;
        }

        return _pressedId == id
            ? VisualState.Pressed
            : _hoverId == id ? VisualState.Hover : VisualState.Rest;
    }

    private readonly record struct ActionSpec(string Label, string Icon, SurfaceRole Role, bool Disabled = false);

    private ActionSpec PrimaryAction() => _status.State switch
    {
        DshState.Running => new ActionSpec("停止", "square", SurfaceRole.Standard),
        DshState.Starting => new ActionSpec("启动中", "pause", SurfaceRole.Standard, Disabled: true),
        _ => new ActionSpec("启动", "play", SurfaceRole.Accent),
    };

    // ---------------------------------------------------------------- 绘制：图标

    private void DrawIcon(Graphics g, in Frame frame, List<(Rectangle Bounds, string Id)> hits)
    {
        var slot = IconSlotRect();
        var side = LerpF(W(IconSide), W(IconRetired), frame.Retire);
        var inset = W(8) * frame.Retire;
        var rect = new RectangleF(
            _expandLeft ? slot.Right - inset - side : slot.Left + inset,
            slot.Y + ((slot.Height - side) / 2f),
            side,
            side);

        var alpha = frame.IconAlpha;
        var hovered = _hoverId == "icon" && _phase == Phase.Icon;
        var fill = hovered ? Fluent.OverState(Fluent.Layer, VisualState.Hover) : Fluent.Layer;

        // 阴影不受 alpha 影响，只在图标基本不透明时画，避免让位时留下一团灰。
        if (alpha > 0.9f)
        {
            // 图标只有 44px，海拔 3 的 16 环阴影会糊成一圈光晕；抬升一级就够（浮层仍是 3）。
            Fluent.Shadow(g, rect, W(Fluent.RTile), 2);
        }

        var (tl, tr, br, bl) = IconCorners(rect);
        using (var path = CornerPath(rect, tl, tr, br, bl))
        {
            Fluent.Fill(g, path, Tint(fill, alpha));
            Fluent.Stroke(g, path, Tint(Fluent.StrokeStrong, alpha), 1f);
        }

        var markSize = W(MarkSide) * (side / Math.Max(1f, W(IconSide)));
        DrawMark(g, new PointF(rect.X + (rect.Width / 2f), rect.Y + (rect.Height / 2f)),
            markSize, StateColor(), alpha, frame.Angle);

        if (alpha > 0.5f)
        {
            hits.Add((Rectangle.Round(slot), "icon"));
        }
    }

    /// <summary>贴屏侧两角是方角（0），内侧两角 16；未贴边时四角都是 16。</summary>
    private (float TL, float TR, float BR, float BL) IconCorners(RectangleF rect)
    {
        var r = Math.Min(W(Fluent.RTile), Math.Min(rect.Width, rect.Height) / 2f);
        return _dockSide switch
        {
            DockSide.Right when _expandLeft => (r, 0f, 0f, r),
            DockSide.Left when !_expandLeft => (0f, r, r, 0f),
            _ => (r, r, r, r),
        };
    }

    private void DrawMark(Graphics g, PointF center, float size, Color color, float alpha, float angle)
    {
        if (alpha <= 0.004f || size <= 1f)
        {
            return;
        }

        var state = g.Save();
        g.TranslateTransform(center.X, center.Y);
        g.RotateTransform(angle);
        // 20px 下鲸鱼会把三段弧切出一个楔口（浅色下像渲染瑕疵），这里只留「三段 74° 弧 + 中心点」。
        OrbitMark.Draw(g, new RectangleF(-size / 2f, -size / 2f, size, size),
            Tint(color, alpha), Math.Max(1.2f, 2f * _scale), withWhale: false);
        g.Restore(state);
    }

    // ---------------------------------------------------------------- 绘制工具

    /// <summary>
    /// 逐角圆角路径（半径为 0 的角就是直角）。
    /// 约束是「每条边上两个圆角之和 ≤ 边长」，不是「半径 ≤ 半宽」——
    /// 像 6×28 的拉手只圆内侧两角时，4px 半径在 6px 宽上是合法的。
    /// </summary>
    private static GraphicsPath CornerPath(RectangleF rect, float tl, float tr, float br, float bl)
    {
        var limit = Math.Min(rect.Width, rect.Height);
        tl = Math.Clamp(tl, 0f, limit);
        tr = Math.Clamp(tr, 0f, limit);
        br = Math.Clamp(br, 0f, limit);
        bl = Math.Clamp(bl, 0f, limit);

        (tl, tr) = FitCornerPair(tl, tr, rect.Width);
        (tr, br) = FitCornerPair(tr, br, rect.Height);
        (br, bl) = FitCornerPair(br, bl, rect.Width);
        (bl, tl) = FitCornerPair(bl, tl, rect.Height);

        var path = new GraphicsPath();

        void Arc(float x, float y, float radius, float start)
        {
            if (radius > 0.05f)
            {
                path.AddArc(x, y, radius * 2, radius * 2, start, 90);
            }
        }

        Arc(rect.X, rect.Y, tl, 180);
        path.AddLine(rect.X + tl, rect.Y, rect.Right - tr, rect.Y);
        Arc(rect.Right - (tr * 2), rect.Y, tr, 270);
        path.AddLine(rect.Right, rect.Y + tr, rect.Right, rect.Bottom - br);
        Arc(rect.Right - (br * 2), rect.Bottom - (br * 2), br, 0);
        path.AddLine(rect.Right - br, rect.Bottom, rect.X + bl, rect.Bottom);
        Arc(rect.X, rect.Bottom - (bl * 2), bl, 90);
        path.AddLine(rect.X, rect.Bottom - bl, rect.X, rect.Y + tl);
        path.CloseFigure();
        return path;
    }

    /// <summary>一条边上的两个圆角之和超过边长时按比例缩回去（避免圆弧互相吃掉）。</summary>
    private static (float A, float B) FitCornerPair(float a, float b, float length)
    {
        var sum = a + b;
        if (length <= 0f)
        {
            return (0f, 0f);
        }

        if (sum <= length)
        {
            return (a, b);
        }

        var k = length / sum;
        return (a * k, b * k);
    }

    /// <summary>把 alpha 乘进颜色（保留令牌本身的 alpha）。</summary>
    private static Color Tint(Color color, float alpha)
    {
        if (alpha >= 0.999f)
        {
            return color;
        }

        return Fluent.WithAlpha(color, Fluent.Alpha(color) / 255f * Math.Clamp(alpha, 0f, 1f));
    }

    /// <summary>错峰淡入：第 <paramref name="startMs"/> 毫秒开始，490ms 全亮。</summary>
    private static float GroupAlpha(in Frame frame, int startMs)
    {
        var span = Math.Max(1f, ContentDoneMs - startMs);
        var progress = Math.Clamp((frame.ContentElapsed - startMs) / span, 0f, 1f);
        return Math.Clamp(frame.Content, 0f, 1f) * progress;
    }

    private Color StateColor() => _status.State switch
    {
        DshState.Running => Fluent.Success,
        DshState.Starting => Fluent.Caution,
        DshState.Error => Fluent.Critical,
        _ => Fluent.Caution,
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

        parts.Add($"会话 {(_status.ActiveSessions is { } sessions ? sessions.ToString() : "—")}");

        if (!string.IsNullOrWhiteSpace(_status.Version))
        {
            parts.Add($"dsh {_status.Version}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 峰谷倒计时：标准价「距闲时 h:mm」，闲时「闲时余 h:mm」。
    /// 两侧都压到 5 个字符宽，132px 的格子里标签 + 数值仍留 20px 余量，且与左侧「距闲时」对称；
    /// 「闲时 h:mm 后结束」会只剩 4px 余量（实测见 preview-stopped-*.png），「闲时 h:mm 结束」
    /// 又容易被读成钟点，所以改「余」。
    /// </summary>
    private (string Text, Color Color) PeakCell()
    {
        if (_status.UntilPriceSwitch is not { } span)
        {
            return ("—", Fluent.TextTertiary);
        }

        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var text = _status.OffPeak
            ? $"闲时余 {(int)span.TotalHours}:{span.Minutes:00}"
            : $"距闲时 {(int)span.TotalHours}:{span.Minutes:00}";
        return (text, _status.OffPeak ? Fluent.Success : Fluent.Caution);
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

    // ---------------------------------------------------------------- layered window

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

            if (!Native.UpdateLayeredWindow(Handle, screenDc, ref destination, ref size, _memDc, ref source, 0,
                    ref blend, Native.ULW_ALPHA))
            {
                AppPaths.Log(
                    $"UpdateLayeredWindow failed: {Marshal.GetLastWin32Error()} size={width}x{height} dib={_dib} bits={_dibBits}");
            }
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

            if (_dib == IntPtr.Zero)
            {
                AppPaths.Log($"CreateDIBSection failed: {Marshal.GetLastWin32Error()} dc={_memDc}");
            }

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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _motion.Stop();
            _motion.Dispose();
            _surface?.Dispose();
            ReleaseDib();
        }

        base.Dispose(disposing);
    }
}

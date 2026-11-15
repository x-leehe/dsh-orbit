using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshOrbit;

/// <summary>
/// Web 外壳：用 WebView2 内嵌 DSH Web UI，带简单的多标签页。
///
/// 外观按 docs/design-fluent.md §4.3 重塑为 WinUI 3（Fluent）TabView 式：
///   48px 标题栏  — 品牌 + 标签（上圆角 8、底部 2px 强调色指示条）+ 工具按钮 + 红绿灯
///   36px 工具条  — Mica 底 + Fluent.TextBox 地址栏
///   余下         — WebView2 内容区
///
/// 无边框自绘标题栏，拖动/八向缩放/最大化都自己处理（WM_NCCALCSIZE + WM_NCHITTEST）；
/// 关闭行为可配置为「隐藏（WebView2 留在后台）」或「直接关闭」。
/// </summary>
internal sealed class WebShell : Form
{
    // ---------------------------------------------------------------- 尺寸（96 DPI 逻辑像素，实际一律经 S() 缩放）

    private const int TitleBarHeight = 48;
    private const int ToolbarHeight = 36;
    private const int TabHeight = 32;
    private const int ControlSize = 32;
    private const int MinTabWidth = 84;
    private const int MaxTabWidth = 196;

    private const int ResizeGrip = 6;
    private const int MinWidth = 880;
    private const int MinHeight = 620;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 20H1+ / Win11）。</summary>
    private const int DarkTitleBar = 20;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE。</summary>
    private const int CornerPreference = 33;

    private const string BrandText = "DSH-Orbit";
    private const string AddressPlaceholder = "输入地址，回车访问";

    private readonly TitleBarPanel _header;
    private readonly EdgePanel _toolbar;
    private readonly TextBox _address;
    private readonly HeaderButton _collapse;
    private readonly TrafficButton _minimize;
    private readonly TrafficButton _maximize;
    private readonly TrafficButton _close;

    private readonly EdgePanel _webHost;
    private readonly List<Tab> _tabs = new();
    private readonly ShellState _shellState = ShellStore.Load();

    // 标签几何：每次布局/命中测试前由 LayoutTabs() 重算，避免和鼠标位置错一帧。
    private readonly List<RectangleF> _tabRects = new();
    private readonly List<RectangleF> _closeRects = new();

    // 测量文字用的离屏 Graphics（只量宽度，1×1 位图就够）。
    private readonly Bitmap _measureSurface;
    private readonly Graphics _measureGraphics;

    private Rectangle _plusRect = Rectangle.Empty;

    private bool _hasOverflow;
    private int _tabWidth;
    private bool _tabDragCandidate;
    private bool _tabDragging;
    private int _tabDragStartX;
    private int _tabDragScroll;
    private int _tabDragLastX;
    private long _tabDragLastTick;
    private float _tabGlideVelocity;
    private int _tabMaxScroll;
    private readonly System.Windows.Forms.Timer _tabGlideTimer = new() { Interval = 16 };
    private Rectangle _addressChrome = Rectangle.Empty;
    private Rectangle _refreshRect = Rectangle.Empty;
    private Rectangle _externalRect = Rectangle.Empty;
    private int _hoverFieldButton;
    private bool _addressCollapsed;
    private float _scale = 1f;
    private float _brandWidth;
    private int _brandTextX;
    private int _tabsStart;
    private int _tabsRegionRight;
    private int _tabsClipRight;
    private int _tabStripTop;
    private int _brandDividerX;

    private CoreWebView2Environment? _environment;
    private Tab? _active;
    private string _homeUrl;
    private bool _forcedClose;
    private bool _ready;
    private int _hoverTab = -1;
    private int _hoverClose = -1;
    private int _tabScroll;
    private bool _addressHover;
    private bool _addressEditing;
    private bool _loading;
    private float _spinAngle;
    private readonly System.Windows.Forms.Timer _spinTimer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _addressTimer = new() { Interval = 15 };
    private long _addressAnimStart;
    private int _addressAnimFrom;
    private int _addressAnimTo;
    private bool _addressAnimating;

    /// <summary>收起/展开的动画时长（ms）。</summary>
    private const int AddressAnimMs = 240;

    public WebShell(string url, ShellCloseMode closeMode)
    {
        _homeUrl = url;
        CloseMode = closeMode;
        _scale = DeviceDpi / 96f;

        _measureSurface = new Bitmap(1, 1);
        _measureGraphics = Graphics.FromImage(_measureSurface);

        Text = "DSH-Orbit Web";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(MinWidth, MinHeight);
        Size = new Size(1280, 860);
        BackColor = Fluent.Mica;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);

        // ---- 标题栏（48px）：品牌 + 标签 + 工具按钮 + 红绿灯 ----
        _header = new TitleBarPanel
        {
            BackColor = Fluent.Flyout,
            Interactive = HeaderInteractive,
        };
        _header.Paint += PaintHeader;
        _header.MouseDown += HeaderMouseDown;
        _header.MouseMove += HeaderMouseMove;
        _header.MouseLeave += HeaderMouseLeave;
        _header.MouseUp += HeaderMouseUp;

        _collapse = new HeaderButton("收起 / 展开地址栏", ToggleAddressBar) { Glyph = "chevron-up" };
        _close = new TrafficButton(TrafficButton.Dot.Close, "关闭", HideOrClose);
        _minimize = new TrafficButton(TrafficButton.Dot.Minimize, "最小化", () => BeginInvoke(MinimizeWindow));
        _maximize = new TrafficButton(TrafficButton.Dot.Maximize, "最大化", () => BeginInvoke(ToggleMaximize));

        _header.Controls.Add(_collapse);
        _header.Controls.Add(_minimize);
        _header.Controls.Add(_maximize);
        _header.Controls.Add(_close);

        _addressCollapsed = _shellState.AddressCollapsed;
        _collapse.Glyph = _addressCollapsed ? "chevron-down" : "chevron-up";

        // ---- 工具条（36px）：Mica 底 + Fluent.TextBox 地址栏（收起时为 0 高）----
        _toolbar = new EdgePanel { BackColor = Fluent.Mica };
        _toolbar.Paint += PaintToolbar;
        _toolbar.MouseEnter += (_, _) => UpdateAddressHover();
        _toolbar.MouseLeave += (_, _) => UpdateAddressHover();

        _address = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Fluent.ControlFill,
            ForeColor = Fluent.TextPrimary,
            Font = Fluent.Font(TextRole.Body, false, _scale),
            PlaceholderText = AddressPlaceholder,
            // 地址框只在 Ctrl+L / 点击时拿焦点：窗口显示时不要抢焦点、也别把地址整段选中。
            TabStop = false,
        };
        // 非编辑态由 PaintToolbar 自绘（要弱化 https:// 并画 favicon），编辑时才显示原生输入框。
        _address.Visible = false;
        _address.LostFocus += (_, _) => EndAddressEditing();

        _addressTimer.Tick += (_, _) => OnAddressAnimationTick();
        _tabGlideTimer.Tick += (_, _) => OnTabGlideTick();

        _spinTimer.Tick += (_, _) =>
        {
            _spinAngle = (_spinAngle + 30f) % 360f;
            _toolbar.Invalidate();
        };

        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                var target = _address.Text.Trim();
                EndAddressEditing();
                NavigateActive(target);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                EndAddressEditing();
                _toolbar.Focus();
            }
        };
        _address.GotFocus += (_, _) => _toolbar.Invalidate();
        _address.LostFocus += (_, _) => _toolbar.Invalidate();
        _address.MouseEnter += (_, _) => UpdateAddressHover();
        _address.MouseLeave += (_, _) => UpdateAddressHover();
        _toolbar.Controls.Add(_address);
        _toolbar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_refreshRect.Contains(e.Location))
            {
                if (_loading)
                {
                    ActiveTab?.Core?.Stop();
                }
                else
                {
                    ActiveTab?.Core?.Reload();
                }

                return;
            }

            if (_externalRect.Contains(e.Location))
            {
                OpenExternal();
                return;
            }

            // 点在输入框左右内边距上也要能开始编辑。
            if (_addressChrome.Contains(e.Location))
            {
                FocusAddress(selectAll: false);
            }
        };

        _toolbar.MouseMove += (_, e) =>
        {
            var hover = _refreshRect.Contains(e.Location) ? 1
                : _externalRect.Contains(e.Location) ? 2
                : 0;
            SetFieldButtonHover(hover);
        };

        _toolbar.MouseLeave += (_, _) => SetFieldButtonHover(0);

        // ---- 网页区域 ----
        _webHost = new EdgePanel { BackColor = Fluent.Mica };

        Controls.Add(_webHost);
        Controls.Add(_toolbar);
        Controls.Add(_header);

        Shown += async (_, _) =>
        {
            AppPaths.Log($"shell 显示：state={WindowState} zoomed={IsZoomed(Handle)} size={Size}");
            BringToFrontTemporarily();
            await CreateTabAsync(_homeUrl);
        };
        FormClosing += OnFormClosing;
        Deactivate += (_, _) => SetAddressHover(false);
        MouseWheel += OnShellMouseWheel;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.L)
            {
                FocusAddress(selectAll: true);
            }
            else if (e.Control && e.KeyCode == Keys.T)
            {
                CreateStartTab();
            }
            else if (e.Control && e.KeyCode == Keys.W)
            {
                CloseTab(ActiveTab);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                HideOrClose();
            }
        };

        _ready = true;
        Fluent.Changed += OnFluentChanged;
    }

    /// <summary>关闭窗口时的行为：隐藏（WebView2 留在后台）或直接关闭（进程退出）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ShellCloseMode CloseMode { get; set; }

    public string CurrentUrl => ActiveTab?.Core?.Source ?? _address.Text;

    public bool IsAlive => _active?.Core is not null;

    private Tab? ActiveTab => _active;

    /// <summary>本机是否装了 WebView2 运行时。</summary>
    public static bool IsRuntimeAvailable()
    {
        try
        {
            return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString());
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 标签页

    private void CreateStartTab()
    {
        var tab = new Tab { Url = "dsh://start", Title = "新标签页" };
        tab.Start = new StartPage(_shellState, _homeUrl);
        tab.Start.NavigateRequested += url => _ = NavigateTabAsync(tab, url);
        tab.Start.AddEngineRequested += () => AddEngine(tab);
        tab.Start.EngineChanged += _ => ShellStore.Save(_shellState);
        tab.Start.BackgroundChanged += _ => ShellStore.Save(_shellState);
        tab.Start.HistoryChanged += () => ShellStore.Save(_shellState);
        var grip = S(ResizeGrip);
        tab.Start.Bounds = new Rectangle(grip, grip,
            Math.Max(0, _webHost.ClientSize.Width - (2 * grip)),
            Math.Max(0, _webHost.ClientSize.Height - (2 * grip)));

        _tabs.Add(tab);
        _webHost.Controls.Add(tab.Start);
        Activate(tab);
    }

    /// <summary>在当前标签页里导航；还没有 WebView2 的就地创建（新标签页 → 网页）。</summary>
    private async Task NavigateTabAsync(Tab tab, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        if (tab.View is null)
        {
            await AttachViewAsync(tab, uri.ToString()).ConfigureAwait(true);
            return;
        }

        EnsureSessionCookie(tab, uri.ToString());
        tab.Url = uri.ToString();
        if (ReferenceEquals(tab, _active))
        {
            _address.Text = uri.ToString();
        }

        tab.Core?.Navigate(uri.ToString());
        RecordHistory(uri.ToString(), tab.Title);
    }

    private async Task AttachViewAsync(Tab tab, string target)
    {
        var view = new WebView2 { DefaultBackgroundColor = Fluent.Mica };
        tab.View = view;
        _webHost.Controls.Add(view);
        var grip = S(ResizeGrip);
        view.Bounds = new Rectangle(
            grip,
            grip,
            Math.Max(0, _webHost.ClientSize.Width - (2 * grip)),
            Math.Max(0, _webHost.ClientSize.Height - (2 * grip)));

        if (tab.Start is not null)
        {
            _webHost.Controls.Remove(tab.Start);
            tab.Start.Dispose();
            tab.Start = null;
        }

        Activate(tab);

        try
        {
            _environment ??= await CoreWebView2Environment.CreateAsync(null, EnsureDataFolder());
            await view.EnsureCoreWebView2Async(_environment);

            var core = view.CoreWebView2!;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsSwipeNavigationEnabled = false;
            ApplyWebViewTheme(core);

            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "DSH Web" : core.DocumentTitle;
                RefreshTabStrip();
                RecordHistory(tab.Url, tab.Title);
            };
            core.NavigationStarting += (_, e) =>
            {
                tab.Url = e.Uri;
                tab.Loading = true;
                if (ReferenceEquals(tab, _active))
                {
                    _loading = true;
                    _spinTimer.Start();
                    if (!_addressEditing)
                    {
                        _address.Text = e.Uri;
                    }

                    _toolbar.Invalidate();
                }
            };
            core.NavigationCompleted += (_, e) =>
            {
                tab.Loading = false;
                if (ReferenceEquals(tab, _active))
                {
                    _loading = false;
                    _spinTimer.Stop();
                    _spinAngle = 0f;
                    _toolbar.Invalidate();
                }

                if (e.IsSuccess)
                {
                    RecordHistory(tab.Url, tab.Title);
                }
            };
            core.FaviconChanged += (_, _) => _ = LoadFaviconAsync(tab);
            core.NewWindowRequested += (_, e) =>
            {
                // 站内 target=_blank 开成新标签页，不跳回浏览器。
                e.Handled = true;
                CreateStartTab();
                if (_active is not null)
                {
                    _ = NavigateTabAsync(_active, e.Uri);
                }
            };

            HookAccelerators(view);

            tab.Ready = true;
            EnsureSessionCookie(tab, target);
            tab.Url = target;
            core.Navigate(target);
            RecordHistory(target, tab.Title);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"WebView2 初始化失败：{ex.Message}");
            MessageBox.Show(
                $"WebView2 无法初始化：{ex.Message}\n\n可以改用默认浏览器打开。",
                "DSH Web",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            OpenExternal();
            Hide();
        }
    }

    /// <summary>原来「直接开网页」的入口，保留给初始标签页。</summary>
    private Task CreateTabAsync(string? url)
    {
        var target = string.IsNullOrWhiteSpace(url) ? _homeUrl : url;
        var tab = new Tab { Url = target, Title = "DSH Web" };
        _tabs.Add(tab);
        RefreshTabStrip();
        return AttachViewAsync(tab, target);
    }

    /// <summary>
    /// WebView2 吃掉键盘焦点时，窗体收不到 Ctrl+T/W/L。
    /// 通过 CoreWebView2Controller.AcceleratorKeyPressed 接管（WinForms 包装器没公开，用反射取控制器）。
    /// </summary>
    private void HookAccelerators(WebView2 view)
    {
        try
        {
            var field = typeof(WebView2).GetField(
                "_coreWebView2Controller",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field?.GetValue(view) is not CoreWebView2Controller controller)
            {
                return;
            }

            controller.AcceleratorKeyPressed += (_, e) =>
            {
                if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
                {
                    return;
                }

                if (!IsCtrlDown())
                {
                    return;
                }

                switch ((Keys)e.VirtualKey)
                {
                    case Keys.T:
                        e.Handled = true;
                        CreateStartTab();
                        break;
                    case Keys.W:
                        e.Handled = true;
                        CloseTab(ActiveTab);
                        break;
                    case Keys.L:
                        e.Handled = true;
                        _address.Focus();
                        _address.SelectAll();
                        break;
                }
            };
        }
        catch (Exception ex)
        {
            AppPaths.Log($"挂载加速键失败：{ex.Message}");
        }
    }

    private static bool IsCtrlDown() => (GetKeyState(VkControl) & 0x8000) != 0;

    private const int VkControl = 0x11;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    private void RecordHistory(string url, string title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        // DSH 自己（回环地址）不进访问记录，避免换端口后旧地址又冒出来。
        if (uri.IsLoopback)
        {
            return;
        }

        var existing = _shellState.History.FirstOrDefault(h =>
            string.Equals(h.Url, url, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            _shellState.History.Add(new HistoryEntry { Url = url, Title = title, VisitedAt = DateTime.Now });
        }
        else
        {
            existing.Title = title;
            existing.VisitedAt = DateTime.Now;
        }

        if (_shellState.History.Count > 120)
        {
            _shellState.History = _shellState.History
                .OrderByDescending(h => h.VisitedAt)
                .Take(120)
                .ToList();
        }

        ShellStore.Save(_shellState);
    }

    private void AddEngine(Tab tab)
    {
        using var dialog = new AddEngineDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _shellState.Engines.Add(new SearchEngine
        {
            Name = dialog.EngineName,
            Template = dialog.Template,
        });
        _shellState.ActiveEngine = dialog.EngineName;
        ShellStore.Save(_shellState);
        tab.Start?.RefreshContent();
    }

    private void Activate(Tab tab)
    {
        foreach (var other in _tabs)
        {
            var current = ReferenceEquals(other, tab);
            if (other.View is not null)
            {
                other.View.Visible = current;
            }

            if (other.Start is not null)
            {
                other.Start.Visible = current && other.View is null;
            }
        }

        _active = tab;
        EnsureActiveVisible();

        if (tab.View is not null)
        {
            tab.View.BringToFront();
        }
        else
        {
            tab.Start?.BringToFront();
        }

        _address.Text = tab.Url == "dsh://start" ? string.Empty : tab.Url;
        _loading = tab.Loading;
        if (_loading)
        {
            _spinTimer.Start();
        }
        else
        {
            _spinTimer.Stop();
        }

        _toolbar.Invalidate();
        RefreshTabStrip();
    }

    private void CloseTab(Tab? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);

        try
        {
            if (tab.View is not null)
            {
                _webHost.Controls.Remove(tab.View);
                tab.View?.Dispose();
            }

            if (tab.Start is not null)
            {
                _webHost.Controls.Remove(tab.Start);
                tab.Start.Dispose();
            }

            tab.Icon?.Dispose();
            tab.Icon = null;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"关闭标签页失败：{ex.Message}");
        }

        ResetTabHover();

        if (_tabs.Count == 0)
        {
            _active = null;
            RefreshTabStrip();
            HideOrClose();
            return;
        }

        if (ReferenceEquals(_active, tab))
        {
            Activate(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
        }

        RefreshTabStrip();
    }

    private void NavigateActive(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var tab = ActiveTab;
        if (tab?.Core is null)
        {
            _ = CreateTabAsync(uri.ToString());
            return;
        }

        EnsureSessionCookie(tab, uri.ToString());
        tab.Url = uri.ToString();
        _address.Text = uri.ToString();
        tab.Core!.Navigate(uri.ToString());
    }

    // ---------------------------------------------------------------- 布局

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        // 构造期间也会触发布局，此时子控件还没建好。
        if (!_ready || _header is null || _toolbar is null || _webHost is null)
        {
            return;
        }

        // 折叠动画自己逐帧控制高度，这里让开，否则每次改高度都会被重排打回终态。
        if (_addressAnimating)
        {
            return;
        }

        LayoutChildren(ClientSize.Width, ClientSize.Height);
    }

    /// <summary>按真实客户区重排；WM_NCCALCSIZE 之后 WinForms 缓存的 ClientSize 会是旧值，所以单独走 GetClientRect。</summary>
    private void ApplyBounds()
    {
        if (_addressAnimating)
        {
            return;
        }

        if (!IsHandleCreated || !GetClientRect(Handle, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width > 0 && height > 0)
        {
            LayoutChildren(width, height);
        }
    }

    /// <summary>DPI 缩放：96 逻辑像素 → 当前 DPI 的物理像素。</summary>
    private int S(int value) => (int)MathF.Round(value * _scale);

    private void LayoutChildren(int width, int height)
    {
        if (_header is null || _toolbar is null || _webHost is null || _close is null)
        {
            return;
        }

        _scale = DeviceDpi / 96f;
        var titleHeight = S(TitleBarHeight);
        var toolHeight = _addressCollapsed ? 0 : S(ToolbarHeight);
        var control = S(ControlSize);
        var margin = S(12);
        var rowY = S(8);

        _header.Bounds = new Rectangle(0, 0, width, titleHeight);
        _toolbar.Visible = !_addressCollapsed;
        _toolbar.Bounds = new Rectangle(0, titleHeight, width, toolHeight);
        _webHost.Bounds = new Rectangle(0, titleHeight + toolHeight, width,
            Math.Max(0, height - titleHeight - toolHeight));

        // 右侧：三颗红绿灯（最小化 → 最大化 → 关闭），命中区 32×32 紧挨着，整组右侧留 12px 内边距。
        var closeX = width - margin - control;
        var maxX = closeX - control;
        var minX = maxX - control;
        _close.Bounds = new Rectangle(closeX, rowY, control, control);
        _maximize.Bounds = new Rectangle(maxX, rowY, control, control);
        _minimize.Bounds = new Rectangle(minX, rowY, control, control);

        // 收起/展开地址栏的按钮固定住在红绿灯左边（收起后也看得见、点得到）。
        var collapseX = minX - S(12) - control;
        _collapse.Bounds = new Rectangle(collapseX, rowY, control, control);

        // 品牌（◈ + DSH-Orbit），1px 分隔线，标签紧接其后；+ 跟在标签末尾（放不下时钉在标签区最右）。
        _brandTextX = S(12) + S(18) + S(8);
        _brandWidth = Fluent.Measure(_measureGraphics, BrandText, Fluent.Font(TextRole.Body, true, _scale));
        var brandEnd = _brandTextX + (int)MathF.Ceiling(_brandWidth) + S(4);
        _brandDividerX = brandEnd + S(12);
        _tabsStart = brandEnd + S(24);
        _tabsRegionRight = collapseX - S(8);
        _tabStripTop = rowY;
        LayoutTabs();

        // 工具条：扁平地址行——没有输入框的外框/圆角，整行都是可点击区。
        _addressChrome = new Rectangle(0, 0, width, toolHeight);

        var fieldButton = S(26);
        var fieldPad = S(8);
        _externalRect = new Rectangle(
            width - fieldPad - fieldButton,
            (toolHeight - fieldButton) / 2,
            fieldButton,
            fieldButton);
        _refreshRect = new Rectangle(
            _externalRect.X - S(2) - fieldButton,
            _externalRect.Y,
            fieldButton,
            fieldButton);

        var preferred = _address.PreferredHeight;
        var textLeft = S(12);
        var textRight = _refreshRect.X - S(8);
        _address.Bounds = new Rectangle(
            textLeft,
            Math.Max(0, (toolHeight - preferred) / 2),
            Math.Max(S(40), textRight - textLeft),
            preferred);

        ResizeWebViews();
    }

    /// <summary>按标签标题算出每个标签的矩形；+ 跟在最后一个标签后面，放不下时钉在标签区最右。</summary>
    private void LayoutTabs()
    {
        _tabRects.Clear();
        _closeRects.Clear();

        var control = S(ControlSize);
        var gap = S(4);
        var min = (float)S(MinTabWidth);
        var max = (float)S(MaxTabWidth);

        // 标签区可用宽度 = 一直到工具按钮左边；+ 号本身不算在内。
        var available = Math.Max(0f, _tabsRegionRight - control - gap - _tabsStart);
        var count = Math.Max(1, _tabs.Count);

        // 一批标签均分宽度：数量多了先一起变窄，到最小值还放不下才开滚动。
        var width = Math.Clamp(available / count, min, max);
        var total = width * _tabs.Count;
        _hasOverflow = total > available + 0.5f;
        if (_hasOverflow)
        {
            width = min;
            total = width * _tabs.Count;
        }

        _tabWidth = (int)MathF.Round(width);
        var maxScroll = Math.Max(0f, total - available);
        _tabMaxScroll = (int)MathF.Round(maxScroll);
        _tabScroll = (int)Math.Clamp((float)_tabScroll, 0f, maxScroll);

        var tabsEnd = _tabsStart + total - _tabScroll;
        var plusX = (int)MathF.Round(Math.Min(_tabsRegionRight - control, tabsEnd + gap));
        plusX = Math.Max(_tabsStart, plusX);
        _plusRect = new Rectangle(plusX, _tabStripTop, control, control);
        _tabsClipRight = Math.Max(_tabsStart, plusX - gap);

        var x = (float)(_tabsStart - _tabScroll);
        var height = S(TabHeight);
        var closeSize = S(14);
        for (var i = 0; i < _tabs.Count; i++)
        {
            var rect = new RectangleF(x, _tabStripTop, width, height);
            _tabRects.Add(rect);
            _closeRects.Add(new RectangleF(
                rect.Right - S(10) - closeSize,
                rect.Y + ((rect.Height - closeSize) / 2f),
                closeSize,
                closeSize));
            x += width;
        }
    }

    /// <summary>把当前标签滚进可视区（切标签 / 新建 / 关闭后调用）。</summary>
    private void EnsureActiveVisible()
    {
        var index = _active is null ? -1 : _tabs.IndexOf(_active);
        if (index < 0 || index >= _tabRects.Count)
        {
            return;
        }

        var rect = _tabRects[index];
        var changed = false;
        if (rect.Left < _tabsStart)
        {
            _tabScroll -= (int)MathF.Ceiling(_tabsStart - rect.Left);
            changed = true;
        }
        else if (rect.Right > _tabsClipRight)
        {
            _tabScroll += (int)MathF.Ceiling(rect.Right - _tabsClipRight);
            changed = true;
        }

        if (changed)
        {
            _tabScroll = Math.Max(0, _tabScroll);
            LayoutTabs();
        }
    }

    private void ResizeWebViews()
    {
        var width = Math.Max(0, _webHost.ClientSize.Width - (2 * S(ResizeGrip)));
        var height = Math.Max(0, _webHost.ClientSize.Height - (2 * S(ResizeGrip)));
        foreach (var tab in _tabs)
        {
            if (tab.View is not null)
            {
                tab.View.Bounds = new Rectangle(S(ResizeGrip), S(ResizeGrip), width, height);
            }

            if (tab.Start is not null)
            {
                tab.Start.Bounds = new Rectangle(S(ResizeGrip), S(ResizeGrip), width, height);
            }
        }
    }

    private void RefreshTabStrip()
    {
        if (!_ready)
        {
            return;
        }

        LayoutTabs();
        _header.Invalidate();
    }

    /// <summary>边界阴影色：深色主题下要更重一点才看得出来。</summary>
    private static Color IsDarkShadow() =>
        Fluent.IsDark ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(56, 0, 0, 0);

    // ---------------------------------------------------------------- 标题栏绘制

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g);

        var width = _header.Width;
        var height = _header.Height;
        Fluent.Fill(g, new RectangleF(0, 0, width, height), Fluent.Flyout);

        // 品牌：◈ DSH-Orbit（14 semibold）
        var markSize = S(18);
        var markBox = new RectangleF(S(12), (height - markSize) / 2f, markSize, markSize);
        OrbitMark.Draw(g, markBox, Fluent.AccentFill, 2.2f * _scale);
        Fluent.Text(
            g,
            BrandText,
            Fluent.Font(TextRole.Body, true, _scale),
            Fluent.TextPrimary,
            new RectangleF(_brandTextX, 0, _brandWidth + S(2), height));

        // 品牌与标签之间的 1px 分组线（和设计稿里 "DSH-Orbit │[标签]" 一致）。
        var dividerHeight = S(20);
        Fluent.Divider(
            g,
            new RectangleF(_brandDividerX, (height - dividerHeight) / 2f, Math.Max(1, S(1)), dividerHeight));

        LayoutTabs();

        // 标签：激活 = Layer 底 + 底部 2px 强调色指示条；悬停 = 8% 状态层。
        var clip = g.Save();
        g.SetClip(new RectangleF(_tabsStart, 0, Math.Max(0, _tabsClipRight - _tabsStart), height));
        var tabFont = Fluent.Font(TextRole.Caption, false, _scale);
        for (var i = 0; i < _tabs.Count && i < _tabRects.Count; i++)
        {
            var tab = _tabs[i];
            var active = ReferenceEquals(tab, _active);
            var rect = _tabRects[i];

            using (var shape = Fluent.RoundedTop(rect, Fluent.RCard * _scale))
            {
                if (active)
                {
                    // Layer 和 Flyout 只差一阶，补一圈 1px 卡片描边（WinUI 选中标签也有）才立得住。
                    Fluent.Fill(g, shape, Fluent.Layer);
                    Fluent.Stroke(g, shape, Fluent.StrokeColor, Math.Max(1f, S(1)));
                }
                else if (i == _hoverTab)
                {
                    Fluent.Fill(g, shape, Fluent.OverState(Fluent.Flyout, VisualState.Hover));
                }
            }

            if (active)
            {
                Fluent.Fill(
                    g,
                    new RectangleF(rect.X, rect.Bottom - S(2), rect.Width, S(2)),
                    Fluent.AccentFill);
            }

            var showClose = active || i == _hoverTab;
            var textRight = showClose ? rect.Right - S(34) : rect.Right - S(10);
            Fluent.Text(
                g,
                tab.Title,
                tabFont,
                active ? Fluent.TextPrimary : Fluent.TextSecondary,
                new RectangleF(rect.X + S(12), rect.Y, Math.Max(0, textRight - rect.X - S(12)), rect.Height),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter);

            if (!showClose)
            {
                continue;
            }

            var closeRect = _closeRects[i];
            if (i == _hoverClose)
            {
                var backplate = new RectangleF(
                    closeRect.X - S(4),
                    closeRect.Y - S(4),
                    closeRect.Width + S(8),
                    closeRect.Height + S(8));
                using var hoverPath = Fluent.Rounded(backplate, Fluent.RControl);
                Fluent.Fill(g, hoverPath, Fluent.HoverLayer);
            }

            Icons.Draw(
                g,
                "x",
                closeRect,
                i == _hoverClose ? Fluent.TextPrimary : Fluent.TextSecondary,
                2f);
        }

        g.Restore(clip);

        // 溢出时左右边界各一道阴影（渐隐到标题栏底色 + 边缘压暗一点），
        // 暗示"这一侧还有标签"。不占标签空间，触屏靠滑动。
        if (_hasOverflow)
        {
            var fade = S(30);
            if (_tabScroll > 0)
            {
                var rect = new RectangleF(_tabsStart, 0, fade, height);
                using var brush = new LinearGradientBrush(
                    rect,
                    Fluent.Flyout,
                    Color.FromArgb(0, Fluent.Flyout),
                    LinearGradientMode.Horizontal);
                g.FillRectangle(brush, rect);

                var edge = new RectangleF(_tabsStart, 0, S(8), height);
                using var shade = new LinearGradientBrush(
                    edge,
                    IsDarkShadow(),
                    Color.FromArgb(0, 0, 0, 0),
                    LinearGradientMode.Horizontal);
                g.FillRectangle(shade, edge);
            }

            if (_tabScroll < _tabMaxScroll)
            {
                var rect = new RectangleF(_tabsClipRight - fade, 0, fade, height);
                using var brush = new LinearGradientBrush(
                    rect,
                    Color.FromArgb(0, Fluent.Flyout),
                    Fluent.Flyout,
                    LinearGradientMode.Horizontal);
                g.FillRectangle(brush, rect);

                var edge = new RectangleF(_tabsClipRight - S(8), 0, S(8), height);
                using var shade = new LinearGradientBrush(
                    edge,
                    Color.FromArgb(0, 0, 0, 0),
                    IsDarkShadow(),
                    LinearGradientMode.Horizontal);
                g.FillRectangle(shade, edge);
            }
        }

        // + 号：32×32 IconButton，跟在标签末尾（放不下时钉在标签区最右）。
        Fluent.IconButton(
            g,
            _plusRect,
            "plus",
            _hoverTab == -2 ? VisualState.Hover : VisualState.Rest,
            S(16),
            _hoverTab == -2 ? Fluent.TextPrimary : Fluent.TextSecondary);
    }

    // ---------------------------------------------------------------- 工具条绘制

    private void PaintToolbar(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g);

        var width = _toolbar.Width;
        var height = _toolbar.Height;
        Fluent.Fill(g, new RectangleF(0, 0, width, height), Fluent.Mica);
        Fluent.HairLine(g, 0, height - 1, width);

        // 扁平地址行：悬停/聚焦只改底色，不画外框和圆角。
        if (_address.Focused)
        {
            Fluent.Fill(g, new RectangleF(0, 0, width, height), Fluent.ControlFill);
            var underline = new RectangleF(S(12), height - (2 * _scale), Math.Max(0, width - S(24)), 2 * _scale);
            Fluent.Fill(g, underline, Fluent.AccentFill);
        }
        else if (_addressHover)
        {
            Fluent.Fill(g, new RectangleF(0, 0, width, height), Fluent.Mix(Fluent.Mica, Fluent.HoverLayer, 0.5f));
        }

        // 收起/展开过程中，内容跟着高度淡入淡出；纯几何收缩看起来会像匀速平移。
        var opacity = 1f;
        if (_addressAnimating)
        {
            var full = Math.Max(1f, S(ToolbarHeight));
            opacity = Math.Clamp(height / full, 0f, 1f);
        }

        // 非编辑态：自绘站点图标 + 弱化的 scheme + 地址；编辑态由原生输入框自己画。
        if (!_addressEditing && opacity > 0.02f)
        {
            DrawAddressDisplay(g, width, height, opacity);
        }

        var iconSize = 16f * _scale;
        var refreshState = _hoverFieldButton == 1 ? VisualState.Hover : VisualState.Rest;
        var externalState = _hoverFieldButton == 2 ? VisualState.Hover : VisualState.Rest;

        var iconAlpha = opacity;
        if (_loading)
        {
            if (_hoverFieldButton == 1)
            {
                using var plate = Fluent.Rounded(_refreshRect, Fluent.RControl);
                Fluent.Fill(g, plate, Fluent.HoverLayer);
            }

            DrawLoadingSpinner(g, _refreshRect, iconAlpha);
        }
        else
        {
            Fluent.IconButton(g, _refreshRect, "refresh", refreshState, iconSize,
                Fluent.WithAlpha(refreshState == VisualState.Hover ? Fluent.TextPrimary : Fluent.TextSecondary, iconAlpha));
        }

        Fluent.IconButton(g, _externalRect, "external", externalState, iconSize,
            Fluent.WithAlpha(externalState == VisualState.Hover ? Fluent.TextPrimary : Fluent.TextSecondary, iconAlpha));
    }

    /// <summary>地址行显示态：站点图标 + 三级文字的 scheme + 主文字的其余部分。</summary>
    private void DrawAddressDisplay(Graphics g, int width, int height, float opacity = 1f)
    {
        var scale = _scale;
        var iconSize = 16f * scale;
        var left = (float)S(12);
        var middle = height / 2f;

        var iconRect = new RectangleF(left, middle - (iconSize / 2f), iconSize, iconSize);
        var tab = ActiveTab;
        if (tab?.Icon is { } icon)
        {
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            if (opacity < 0.99f)
            {
                using var attributes = new System.Drawing.Imaging.ImageAttributes();
                var matrix = new System.Drawing.Imaging.ColorMatrix { Matrix33 = opacity };
                attributes.SetColorMatrix(matrix);
                var dest = Rectangle.Round(iconRect);
                g.DrawImage(icon, dest, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, attributes);
            }
            else
            {
                g.DrawImage(icon, iconRect);
            }

            g.Restore(state);
        }
        else
        {
            // 新标签页 / 没有图标：用中性的窗口图标（16px 下品牌标会糊成一团）
            Icons.Draw(g, "app-window", iconRect, Fluent.WithAlpha(Fluent.TextTertiary, opacity), 1.8f * scale);
        }

        var textLeft = left + iconSize + S(8);
        var textRight = _refreshRect.X - S(8);
        var textRect = new RectangleF(textLeft, 0, Math.Max(0, textRight - textLeft), height);
        var font = Fluent.Font(TextRole.Body, false, scale);

        var url = tab?.Url ?? string.Empty;
        if (string.IsNullOrEmpty(url) || url == "dsh://start")
        {
            Fluent.Text(g, AddressPlaceholder, font, Fluent.WithAlpha(Fluent.TextTertiary, opacity), textRect);
            return;
        }

        // 弱化 https://：只把 scheme 降一级颜色，其余保持主文字色。
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            Fluent.Text(g, url, font, Fluent.WithAlpha(Fluent.TextPrimary, opacity), textRect,
                StringAlignment.Near, StringTrimming.EllipsisCharacter);
            return;
        }

        var scheme = url[..(schemeEnd + 3)];
        var rest = url[(schemeEnd + 3)..];
        var schemeWidth = Fluent.Measure(g, scheme, font);
        Fluent.Text(g, scheme, font, Fluent.WithAlpha(Fluent.TextTertiary, opacity), textRect);
        if (rest.Length > 0)
        {
            var restRect = new RectangleF(
                textRect.X + schemeWidth,
                textRect.Y,
                Math.Max(0f, textRect.Width - schemeWidth),
                textRect.Height);
            Fluent.Text(g, rest, font, Fluent.WithAlpha(Fluent.TextSecondary, opacity), restRect,
                StringAlignment.Near, StringTrimming.EllipsisCharacter);
        }
    }

    /// <summary>加载中：刷新按钮的位置画一段旋转的弧。</summary>
    private void DrawLoadingSpinner(Graphics g, RectangleF rect, float opacity = 1f)
    {
        var state = g.Save();
        Fluent.AntiAlias(g);
        var inset = 6f * _scale;
        var circle = RectangleF.Inflate(rect, -inset, -inset);
        using var pen = new Pen(Fluent.WithAlpha(Fluent.AccentText, opacity), 2f * _scale)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };
        g.DrawArc(pen, circle, _spinAngle, 270f);
        g.Restore(state);
    }

    // ---------------------------------------------------------------- 标签交互

    /// <summary>按住标签条拖动 = 横向滚动（触屏滑动也走这条路径，Windows 会把触摸转成鼠标消息）。</summary>
    private void HeaderMouseDown(object? sender, MouseEventArgs e)
    {
        _tabDragCandidate = false;
        if (e.Button != MouseButtons.Left || !_hasOverflow)
        {
            return;
        }

        if (e.Y < _tabStripTop || e.Y > _tabStripTop + S(TabHeight) ||
            e.X < _tabsStart || e.X > _tabsClipRight)
        {
            return;
        }

        _tabDragCandidate = true;
        _tabDragging = false;
        _tabDragStartX = e.X;
        _tabDragLastX = e.X;
        _tabDragLastTick = Environment.TickCount64;
        _tabDragScroll = _tabScroll;
        _tabGlideVelocity = 0f;
        _tabGlideTimer.Stop();
    }

    private void OnTabGlideTick()
    {
        _tabGlideVelocity *= 0.90f;
        if (Math.Abs(_tabGlideVelocity) < 0.03f)
        {
            _tabGlideTimer.Stop();
            return;
        }

        _tabScroll = Math.Max(0, _tabScroll - (int)MathF.Round(_tabGlideVelocity * 16f));
        LayoutTabs();
        _header.Invalidate();
    }

    private void HeaderMouseMove(object? sender, MouseEventArgs e)
    {
        if (_tabDragCandidate && e.Button == MouseButtons.Left)
        {
            var dx = e.X - _tabDragStartX;
            if (!_tabDragging && Math.Abs(dx) >= S(6))
            {
                _tabDragging = true;
                _header.Cursor = Cursors.SizeWE;
            }

            if (_tabDragging)
            {
                var now = Environment.TickCount64;
                var elapsed = Math.Max(1, now - _tabDragLastTick);
                _tabGlideVelocity = (e.X - _tabDragLastX) / (float)elapsed;
                _tabDragLastX = e.X;
                _tabDragLastTick = now;

                _tabScroll = Math.Max(0, _tabDragScroll - dx);
                LayoutTabs();
                _header.Invalidate();
                return;
            }
        }

        var hit = TabHitTest(e.Location);
        var hoverTab = hit.IsPlus ? -2 : hit.Index;
        var hoverClose = hit.IsClose ? hit.Index : -1;
        if (hoverTab == _hoverTab && hoverClose == _hoverClose)
        {
            return;
        }

        _hoverTab = hoverTab;
        _hoverClose = hoverClose;
        _header.Cursor = (hit.IsPlus || hit.Index >= 0) ? Cursors.Hand : Cursors.Default;
        _header.Invalidate();
    }

    private void HeaderMouseLeave(object? sender, EventArgs e)
    {
        ResetTabHover();
        _header.Invalidate();
    }

    private void HeaderMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var wasDragging = _tabDragging;
        _tabDragCandidate = false;
        _tabDragging = false;

        if (wasDragging)
        {
            // 甩一把：松手后按速度继续滑一小段
            if (Math.Abs(_tabGlideVelocity) >= 0.05f)
            {
                _tabGlideTimer.Start();
            }

            _header.Cursor = Cursors.Hand;
            return;
        }


        var hit = TabHitTest(e.Location);
        if (hit.IsPlus)
        {
            ResetTabHover();
            CreateStartTab();
            return;
        }

        if (hit.Index < 0 || hit.Index >= _tabs.Count)
        {
            return;
        }

        var target = _tabs[hit.Index];
        ResetTabHover();
        if (hit.IsClose)
        {
            CloseTab(target);
            return;
        }

        Activate(target);
    }

    private void OnShellMouseWheel(object? sender, MouseEventArgs e)
    {
        // 标题栏上滚轮横向滚动标签（和旧实现一致）。
        if (PointToClient(Cursor.Position).Y >= S(TitleBarHeight))
        {
            return;
        }

        _tabScroll = Math.Max(0, _tabScroll - (e.Delta / 2));
        RefreshTabStrip();
    }

    private void ResetTabHover()
    {
        _hoverTab = -1;
        _hoverClose = -1;
    }

    /// <summary>标题栏里只有标签和 + 号要拦鼠标，其余空白留给窗体做 HTCAPTION 拖动。</summary>
    private bool HeaderInteractive(Point point)
    {
        LayoutTabs();

        if (_plusRect.Contains(point))
        {
            return true;
        }

        // 溢出时整条标签带都吃掉点击，否则空白处会变成拖动窗口，没法滑动标签。
        if (_hasOverflow &&
            point.Y >= _tabStripTop && point.Y <= _tabStripTop + S(TabHeight) &&
            point.X >= _tabsStart && point.X <= _tabsClipRight)
        {
            return true;
        }

        // 左右滚动出可视区的标签不算（否则品牌区会变成标签命中区）。
        if (point.X < _tabsStart || point.X > _tabsClipRight ||
            point.Y < _tabStripTop || point.Y > _tabStripTop + S(TabHeight))
        {
            return false;
        }

        foreach (var rect in _tabRects)
        {
            if (rect.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private (int Index, bool IsClose, bool IsPlus) TabHitTest(Point point)
    {
        LayoutTabs();

        if (_plusRect.Contains(point))
        {
            return (-1, false, true);
        }

        // 滚出可视区的标签不参与命中。
        if (point.X < _tabsStart || point.X > _tabsClipRight || point.Y < _tabStripTop ||
            point.Y > _tabStripTop + S(TabHeight))
        {
            return (-1, false, false);
        }

        for (var i = 0; i < _tabRects.Count; i++)
        {
            var visible = ReferenceEquals(_tabs[i], _active) || i == _hoverTab;
            if (!visible)
            {
                continue;
            }

            var close = RectangleF.Inflate(_closeRects[i], S(4), S(4));
            if (close.Contains(point))
            {
                return (i, true, false);
            }
        }

        for (var i = 0; i < _tabRects.Count; i++)
        {
            if (_tabRects[i].Contains(point))
            {
                return (i, false, false);
            }
        }

        return (-1, false, false);
    }

    // ---------------------------------------------------------------- 主题 / DWM

    private void OnFluentChanged()
    {
        if (IsDisposed || Disposing || !_ready)
        {
            return;
        }

        if (IsHandleCreated && InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(OnFluentChanged));
            }
            catch (InvalidOperationException)
            {
                // 窗口正在销毁，忽略。
            }

            return;
        }

        ApplyFluentChrome();
    }

    /// <summary>地址行是扁平的：默认和工具条同色，只有悬停/聚焦才提亮。</summary>
    private Color AddressFill => _addressEditing
        ? Fluent.ControlFill
        : _addressHover ? Fluent.Mix(Fluent.Mica, Fluent.HoverLayer, 0.5f) : Fluent.Mica;

    /// <summary>把 Fluent 令牌应用到窗口与所有子控件，然后整窗重画。</summary>
    private void ApplyFluentChrome()
    {
        _scale = DeviceDpi / 96f;

        BackColor = Fluent.Mica;
        _header.BackColor = Fluent.Flyout;
        _toolbar.BackColor = Fluent.Mica;
        _webHost.BackColor = Fluent.Mica;

        _address.BackColor = AddressFill;
        _address.ForeColor = Fluent.TextPrimary;
        _address.Font = Fluent.Font(TextRole.Body, false, _scale);

        _collapse.SyncTheme();
        _minimize.SyncTheme();
        _maximize.SyncTheme();
        _close.SyncTheme();

        foreach (var tab in _tabs)
        {
            if (tab.View is null)
            {
                continue;
            }

            try
            {
                // 页面底色跟主题走：深色 #202020 / 浅色 #F3F3F3（= Fluent.Mica）。
                tab.View.DefaultBackgroundColor = Fluent.Mica;
            }
            catch (Exception ex)
            {
                AppPaths.Log($"更新 WebView2 背景失败：{ex.Message}");
            }

            if (tab.Core is { } core)
            {
                ApplyWebViewTheme(core);
            }
        }

        ApplyWindowAttributes();

        if (IsHandleCreated)
        {
            ApplyBounds();
        }

        Invalidate(true);
    }

    /// <summary>让 WebView2 的 prefers-color-scheme 跟外壳主题一致（about:blank 的底色也靠它）。</summary>
    private static void ApplyWebViewTheme(CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredColorScheme = Fluent.IsDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"设置 WebView2 主题失败：{ex.Message}");
        }
    }

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE 跟随 Fluent.IsDark（主题变化时重设）；圆角保留。</summary>
    private void ApplyWindowAttributes()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            var dark = Fluent.IsDark ? 1 : 0;
            _ = DwmSetWindowAttribute(Handle, DarkTitleBar, ref dark, sizeof(int));
            var round = 2;
            _ = DwmSetWindowAttribute(Handle, CornerPreference, ref round, sizeof(int));
        }
        catch
        {
            // Windows 10 早期的 dwmapi 没有这些属性，忽略。
        }
    }

    /// <summary>收起 / 展开地址栏；收起后网页区域向上多占一条工具条的高度。</summary>
    private void ToggleAddressBar()
    {
        if (_addressAnimating)
        {
            // 动画中再点：直接落到目标态，避免来回抖
            _addressAnimating = false;
            _addressTimer.Stop();
        }

        _addressCollapsed = !_addressCollapsed;
        _collapse.Glyph = _addressCollapsed ? "chevron-down" : "chevron-up";
        _shellState.AddressCollapsed = _addressCollapsed;
        ShellStore.Save(_shellState);

        if (ClientRectangle.Height <= 0)
        {
            ApplyBounds();
            return;
        }

        _addressAnimFrom = _toolbar.Height;
        _addressAnimTo = _addressCollapsed ? 0 : S(ToolbarHeight);
        _addressAnimStart = Environment.TickCount64;
        _addressAnimating = true;
        _toolbar.Visible = true;   // 收起过程中还要画出来
        _addressTimer.Start();
    }

    /// <summary>
    /// 工具条高度 36 ↔ 0 的滑动动画。按时钟推进（不是按帧数），
    /// 曲线用五次方缓出：起步一冲、尾巴细长，观感才不像匀速。
    /// </summary>
    private void OnAddressAnimationTick()
    {
        var t = Math.Clamp(
            (Environment.TickCount64 - _addressAnimStart) / (float)AddressAnimMs,
            0f,
            1f);
        // 三次方缓出：起步明显但不是"一帧瞬移"，尾巴也收得住
        var eased = 1f - MathF.Pow(1f - t, 3f);
        var height = (int)MathF.Round(_addressAnimFrom + ((_addressAnimTo - _addressAnimFrom) * eased));

        var title = S(TitleBarHeight);
        var client = ClientRectangle.Height;

        _toolbar.Height = height;
        _toolbar.Invalidate();

        // 内容整体跟着上下滑：这里只挪位置、不逐帧改 WebView2 尺寸（否则每帧都在重排页面）。
        _webHost.Top = title + height;
        _webHost.Height = Math.Max(0, client - title);

        if (t >= 1f)
        {
            _addressAnimating = false;
            _addressTimer.Stop();
            ApplyBounds();   // 收尾：定稿高度 + 重新排 WebView2
        }
    }

    /// <summary>聚焦地址栏；收起状态下先展开。</summary>
    private void FocusAddress(bool selectAll)
    {
        if (_addressCollapsed)
        {
            // 键盘聚焦时瞬间展开：动画期间布局会让开，输入框位置会错一帧。
            _addressAnimating = false;
            _addressTimer.Stop();
            _addressCollapsed = false;
            _collapse.Glyph = "chevron-up";
            _shellState.AddressCollapsed = false;
            ShellStore.Save(_shellState);
            ApplyBounds();
        }

        _addressEditing = true;
        _address.Visible = true;
        _address.BackColor = AddressFill;
        _address.Focus();
        if (selectAll)
        {
            _address.SelectAll();
        }
        else
        {
            _address.SelectionStart = _address.TextLength;
        }

        _toolbar.Invalidate();
    }

    /// <summary>退出编辑：切回自绘显示（弱化 scheme + favicon）。</summary>
    private void EndAddressEditing()
    {
        if (!_addressEditing)
        {
            return;
        }

        _addressEditing = false;
        _address.Visible = false;
        _toolbar.Invalidate();
    }

    /// <summary>读取当前标签页的站点图标（WebView2 自带，不走网络）。</summary>
    private async Task LoadFaviconAsync(Tab tab)
    {
        var core = tab.Core;
        if (core is null)
        {
            return;
        }

        try
        {
            await using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            Image? icon = null;
            if (stream is not null)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                if (buffer.Length > 0)
                {
                    buffer.Position = 0;
                    using var raw = Image.FromStream(buffer);
                    icon = new Bitmap(raw, 32, 32);
                }
            }

            var old = tab.Icon;
            tab.Icon = icon;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取站点图标失败：{ex.Message}");
        }

        if (ReferenceEquals(tab, _active))
        {
            _toolbar.Invalidate();
        }
    }

    /// <summary>地址栏内按钮的悬停（0 无 / 1 刷新 / 2 在浏览器打开）。</summary>
    private void SetFieldButtonHover(int index)
    {
        if (_hoverFieldButton == index)
        {
            return;
        }

        _hoverFieldButton = index;
        _toolbar.Cursor = index == 0 ? Cursors.Default : Cursors.Hand;
        _toolbar.Invalidate();
    }

    private void UpdateAddressHover()
    {
        var point = _toolbar.PointToClient(Cursor.Position);
        SetAddressHover(_addressChrome.Contains(point));
    }

    private void SetAddressHover(bool hover)
    {
        if (_addressHover == hover)
        {
            return;
        }

        _addressHover = hover;
        _address.BackColor = AddressFill;
        _toolbar.Invalidate();
    }

    // ---------------------------------------------------------------- 拖动 / 缩放 / 最大化

    // ---------------------------------------------------------------- 导航与会话

    /// <summary>显示并按需导航到指定地址。</summary>
    public void ShowUrl(string url)
    {
        _homeUrl = string.IsNullOrWhiteSpace(url) ? _homeUrl : url;

        if (!Visible)
        {
            Show();
        }

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        Activate();
        _ = Native.SetForegroundWindow(Handle);

        if (_tabs.Count == 0)
        {
            _ = CreateTabAsync(_homeUrl);
            return;
        }

        if (!string.IsNullOrWhiteSpace(url) &&
            !string.Equals(CurrentUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            NavigateActive(url);
        }
    }

    private static string EnsureDataFolder()
    {
        var folder = Path.Combine(AppPaths.Root, "webview");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>本机回环地址先用本地 secret 签一个会话 cookie，省掉「没有 token 就 401」。</summary>
    private static void EnsureSessionCookie(Tab tab, string url)
    {
        var core = tab.Core;
        if (core is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback)
        {
            return;
        }

        try
        {
            if (!DshSession.TryCreateCookie(uri.Authority, TimeSpan.FromHours(12), out var name, out var value))
            {
                return;
            }

            var manager = core.CookieManager;
            manager.AddOrUpdateCookie(manager.CreateCookie(name, value, uri.Host, "/"));
        }
        catch (Exception ex)
        {
            AppPaths.Log($"写入会话 cookie 失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把窗口带到前台，但**绝不置顶**。
    /// 以前这里是「先 HWND_TOPMOST 再 NOTOPMOST」的 hack，一旦中途失败窗口就永久压在所有程序上面，
    /// 这跟桌面挂件的置顶语义是两回事。现在改用 SetForegroundWindow + 失败时闪任务栏。
    /// </summary>
    private void BringToFrontTemporarily()
    {
        try
        {
            _ = Native.SetForegroundWindow(Handle);

            // 前台权限被拒时，闪一下任务栏按钮提示，而不是把窗口钉在最上层。
            if (Native.GetForegroundWindow() != Handle)
            {
                var info = new Native.FLASHWINFO
                {
                    hwnd = Handle,
                    dwFlags = Native.FLASHW_ALL | Native.FLASHW_TIMERNOFG,
                    uCount = 3,
                    dwTimeout = 0,
                };
                info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.FLASHWINFO>();
                _ = Native.FlashWindowEx(ref info);
            }
        }
        catch
        {
            // 前台权限被拒绝时忽略。
        }
    }

    private void OpenExternal()
    {
        var url = CurrentUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        // 没有 launch token（外部实例/重启过）时，走本地中转先种 cookie 再跳转。
        if (Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.IsLoopback)
        {
            url = AuthLinks.For(url);
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log($"打开外部浏览器失败：{ex.Message}");
        }
    }

    /// <summary>用户点关闭时：按设置隐藏或真正关闭。</summary>
    public void HideOrClose()
    {
        if (CloseMode == ShellCloseMode.Hide)
        {
            Hide();
            return;
        }

        Close();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing && CloseMode == ShellCloseMode.Hide && !_forcedClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>真正关闭并释放所有 WebView2（不在后台常驻）。</summary>
    public void ForceClose()
    {
        _forcedClose = true;
        foreach (var tab in _tabs)
        {
            try
            {
                tab.View?.Dispose();
            }
            catch
            {
                // 释放失败不影响退出。
            }
        }

        _tabs.Clear();
        Close();
        Dispose();
    }

    // ---------------------------------------------------------------- 原生边框 / 命中测试

    private const int WmNcCalcSize = 0x0083;
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int FramePad = 8;
    private const int WmGetMinMaxInfo = 0x0024;
    private const int WmSize = 0x0005;

    private const int SwMaximize = 3;
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    /// <summary>走系统的 ShowWindow（最小化动画、最大化按工作区、还原都交给 Windows）。</summary>
    private void MinimizeWindow() => _ = ShowWindow(Handle, SwMinimize);

    private void ToggleMaximize() =>
        _ = ShowWindow(Handle, WindowState == FormWindowState.Maximized ? SwRestore : SwMaximize);

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcCalcSize:
            {
                if (m.WParam == IntPtr.Zero)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }

                // 结构里给的是「去掉边框后的客户区」：
                //  · 普通状态：向外扩一圈边框，客户区 = 整个窗口（等于没有标题栏）
                //  · 最大化：保持原样（系统给的就是工作区，不会盖住任务栏）
                var rect = Marshal.PtrToStructure<Rect>(m.LParam);
                if (IsZoomed(Handle))
                {
                    // 最大化时系统给的是「工作区 + 边框」，内缩一圈正好露出工作区。
                    rect.Left += FramePad;
                    rect.Top += FramePad;
                    rect.Right -= FramePad;
                    rect.Bottom -= FramePad;
                }

                Marshal.StructureToPtr(rect, m.LParam, false);
                ApplyBounds();
                m.Result = IntPtr.Zero;
                return;
            }

            case WmGetMinMaxInfo:
            {
                // 最大化后的窗口矩形 = 工作区 + 四周各一个边框厚度，这样 WM_NCCALCSIZE
                // 再把边框补回来后，客户区正好等于工作区（不盖住任务栏）。
                base.WndProc(ref m);
                var info = Marshal.PtrToStructure<MinMaxInfo>(m.LParam);
                var screen = Screen.FromHandle(Handle);
                var work = screen.WorkingArea;
                var bounds = screen.Bounds;
                info.ptMaxPosition.X = work.X - bounds.X - FramePad;
                info.ptMaxPosition.Y = work.Y - bounds.Y - FramePad;
                info.ptMaxSize.X = work.Width + (FramePad * 2);
                info.ptMaxSize.Y = work.Height + (FramePad * 2);
                Marshal.StructureToPtr(info, m.LParam, false);
                return;
            }

            case WmSize:
            {
                base.WndProc(ref m);
                ApplyBounds();
                return;
            }

            case WmNcHitTest:
            {
                base.WndProc(ref m);
                if ((int)m.Result != HtClient)
                {
                    return;
                }

                var raw = m.LParam.ToInt64();
                var screenPoint = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
                m.Result = HitTestClient(PointToClient(screenPoint));
                return;
            }
        }

        base.WndProc(ref m);
    }

    /// <summary>边缘 6px 给系统缩放，标题栏高度内给系统拖动（双击即最大化）。</summary>
    private IntPtr HitTestClient(Point point)
    {
        if (WindowState == FormWindowState.Normal)
        {
            var border = S(6);
            var left = point.X <= border;
            var right = point.X >= ClientSize.Width - border;
            var top = point.Y <= border;
            var bottom = point.Y >= ClientSize.Height - border;

            if (top && left) return HtTopLeft;
            if (top && right) return HtTopRight;
            if (bottom && left) return HtBottomLeft;
            if (bottom && right) return HtBottomRight;
            if (left) return HtLeft;
            if (right) return HtRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;
        }

        return point.Y < S(TitleBarHeight) ? HtCaption : HtClient;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point ptReserved;
        public Point ptMaxSize;
        public Point ptMaxPosition;
        public Point ptMinTrackSize;
        public Point ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyFluentChrome();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyFluentChrome();
        // 远程会话会临时把 DPI 改成 125%，这里必须按新的 DeviceDpi 重排一遍。
        ApplyBounds();
        _header.Invalidate();
        _toolbar.Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _maximize?.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnFluentChanged;
            _spinTimer.Dispose();
            _addressTimer.Dispose();
            _tabGlideTimer.Dispose();
            _collapse?.Dispose();
            _minimize?.Dispose();
            _maximize?.Dispose();
            _close?.Dispose();
            _toolbar?.Dispose();
            _header?.Dispose();
            _measureGraphics?.Dispose();
            _measureSurface?.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>一个标签页 = 一个 WebView2（都在 _webHost 里，只显示当前那个）。</summary>
    private sealed class Tab
    {
        public WebView2? View { get; set; }

        /// <summary>View 可能还没创建（新标签页），这里统一做一次空检查。</summary>
        public CoreWebView2? Core => View?.CoreWebView2;
        public StartPage? Start { get; set; }
        public string Url { get; set; } = "";
        public string Title { get; set; } = "新标签页";
        public bool Ready { get; set; }
        public bool Loading { get; set; }
        public Image? Icon { get; set; }
    }

    /// <summary>自绘面板基类：尺寸变化即整体重绘（ResizeRedraw），双缓冲避免闪烁。</summary>
    private class BufferedPanel : Panel
    {
        public BufferedPanel()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }
    }

    /// <summary>靠近窗口边缘时把命中让给父窗口（用于系统缩放），其余区域照常接收。</summary>
    private sealed class EdgePanel : BufferedPanel
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && Parent is { } parent)
            {
                var raw = m.LParam.ToInt64();
                var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
                var inForm = parent.PointToClient(screen);
                var size = parent.ClientSize;
                if (inForm.X <= 8 || inForm.Y <= 8 ||
                    inForm.X >= size.Width - 8 || inForm.Y >= size.Height - 8)
                {
                    m.Result = (IntPtr)(-1); // HTTRANSPARENT → 交给 form 做 HTLEFT/HTTOP/...
                    return;
                }
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 标题栏面板：只有标签 / + 号拦鼠标（HTCLIENT），其余空白一律 HTTRANSPARENT，
    /// 交给窗体返回 HTCAPTION —— 这样蓝色区域仍可拖动、双击最大化。
    /// </summary>
    private sealed class TitleBarPanel : BufferedPanel
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Func<Point, bool>? Interactive { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmNcHitTest && Interactive is { } interactive)
            {
                var raw = m.LParam.ToInt64();
                var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));
                if (!interactive(PointToClient(screen)))
                {
                    m.Result = (IntPtr)(-1); // HTTRANSPARENT
                    return;
                }
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>macOS 风格的红绿灯按钮：15px 彩色圆点，命中区 32×32，悬停 8% 状态层。</summary>
    private sealed class TrafficButton : Control
    {
        public enum Dot
        {
            Close,
            Minimize,
            Maximize,
        }

        private readonly Dot _dot;
        private readonly Action _action;
        private readonly ToolTip _toolTip;
        private bool _hover;

        public TrafficButton(Dot dot, string tooltip, Action action)
        {
            _dot = dot;
            _action = action;
            Size = new Size(32, 32);
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Fluent.Flyout;

            _toolTip = new ToolTip();
            _toolTip.SetToolTip(this, tooltip);
        }

        /// <summary>主题变了：底板颜色跟着标题栏走，然后重画。</summary>
        public void SyncTheme()
        {
            BackColor = Fluent.Flyout;
            Invalidate();
        }

        /// <summary>macOS 红绿灯三色（顺序：最小化 → 最大化 → 关闭）。</summary>
        private Color DotColor => _dot switch
        {
            Dot.Close => Fluent.TrafficClose,
            Dot.Minimize => Fluent.TrafficMinimize,
            _ => Fluent.TrafficMaximize,
        };

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _action();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Fluent.AntiAlias(g);

            var scale = DeviceDpi / 96f;
            var diameter = 15f * scale;
            var cx = Width / 2f;
            var cy = Height / 2f;
            var baseColor = DotColor;

            // 只有圆点，没有背板：悬停时圆点提亮、并浮出 × / − / + 符号（macOS 的做法）。
            using (var fill = new SolidBrush(_hover ? ControlPaint.Light(baseColor, 0.12f) : baseColor))
            {
                g.FillEllipse(fill, cx - (diameter / 2f), cy - (diameter / 2f), diameter, diameter);
            }

            if (!_hover)
            {
                return;
            }

            var ink = Fluent.Mix(baseColor, Color.Black, 0.52f);
            using var pen = new Pen(ink, 1.6f * scale)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            var arm = 3.6f * scale;
            switch (_dot)
            {
                case Dot.Close:
                    g.DrawLine(pen, cx - arm, cy - arm, cx + arm, cy + arm);
                    g.DrawLine(pen, cx + arm, cy - arm, cx - arm, cy + arm);
                    break;
                case Dot.Minimize:
                    g.DrawLine(pen, cx - (arm + 0.6f * scale), cy, cx + (arm + 0.6f * scale), cy);
                    break;
                default:
                    g.DrawLine(pen, cx - (arm + 0.6f * scale), cy, cx + (arm + 0.6f * scale), cy);
                    g.DrawLine(pen, cx, cy - (arm + 0.6f * scale), cx, cy + (arm + 0.6f * scale));
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>标题栏上的 32×32 图标按钮（Lucide 图标，悬停 8% 状态层）。</summary>
    private sealed class HeaderButton : Control
    {
        private readonly Action _action;
        private readonly ToolTip _toolTip;
        private bool _hover;

        public HeaderButton(string tooltip, Action action)
        {
            _action = action;
            Size = new Size(32, 32);
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Fluent.Flyout;
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(this, tooltip);
        }

        private string _glyph = "refresh";

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Glyph
        {
            get => _glyph;
            set
            {
                if (string.Equals(_glyph, value, StringComparison.Ordinal))
                {
                    return;
                }

                _glyph = value;
                Invalidate();
            }
        }

        /// <summary>主题变了：底板颜色跟着标题栏走，然后重画。</summary>
        public void SyncTheme()
        {
            BackColor = Fluent.Flyout;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                _action();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Fluent.AntiAlias(g);

            Fluent.IconButton(
                g,
                new RectangleF(0, 0, Width, Height),
                _glyph,
                _hover ? VisualState.Hover : VisualState.Rest,
                16f * (DeviceDpi / 96f),
                _hover ? Fluent.TextPrimary : Fluent.TextSecondary);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _toolTip.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

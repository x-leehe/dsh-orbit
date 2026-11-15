using System.Diagnostics;
using System.Drawing;
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
    private const int MinTabWidth = 132;
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
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateActive(_address.Text.Trim());
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
                ActiveTab?.Core?.Reload();
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
                if (ReferenceEquals(tab, _active))
                {
                    _address.Text = e.Uri;
                }
            };
            core.NavigationCompleted += (_, e) =>
            {
                if (e.IsSuccess)
                {
                    RecordHistory(tab.Url, tab.Title);
                }
            };
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
        if (tab.View is not null)
        {
            tab.View.BringToFront();
        }
        else
        {
            tab.Start?.BringToFront();
        }

        _address.Text = tab.Url == "dsh://start" ? string.Empty : tab.Url;
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

        LayoutChildren(ClientSize.Width, ClientSize.Height);
    }

    /// <summary>按真实客户区重排；WM_NCCALCSIZE 之后 WinForms 缓存的 ClientSize 会是旧值，所以单独走 GetClientRect。</summary>
    private void ApplyBounds()
    {
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

        // 工具条：地址栏左右各 12 内边距，TextField 32 高、上下各 2；
        // 刷新 / 在浏览器打开两个按钮挪进地址栏内部右侧，地址文字让出它们的宽度。
        _addressChrome = new Rectangle(S(12), S(2), Math.Max(S(80), width - (2 * S(12))), S(32));
        var fieldButton = S(28);
        var fieldPad = S(6);
        _externalRect = new Rectangle(
            _addressChrome.Right - fieldPad - fieldButton,
            _addressChrome.Y + ((_addressChrome.Height - fieldButton) / 2),
            fieldButton,
            fieldButton);
        _refreshRect = new Rectangle(
            _externalRect.X - S(2) - fieldButton,
            _externalRect.Y,
            fieldButton,
            fieldButton);

        var preferred = _address.PreferredHeight;
        var textLeft = _addressChrome.X + S(10);
        var textRight = _refreshRect.X - S(8);
        _address.Bounds = new Rectangle(
            textLeft,
            _addressChrome.Y + Math.Max(0, (_addressChrome.Height - preferred) / 2),
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
        var font = Fluent.Font(TextRole.Caption, false, _scale);
        var widths = new float[_tabs.Count];
        var total = 0f;
        for (var i = 0; i < _tabs.Count; i++)
        {
            var text = Fluent.Measure(_measureGraphics, _tabs[i].Title, font);
            var preferred = text + S(44);
            widths[i] = Math.Clamp(preferred, S(MinTabWidth), S(MaxTabWidth));
            total += widths[i];
        }

        // 标签区可用宽度 = 一直到工具按钮左边；+ 号本身不算在内。
        var available = Math.Max(0f, _tabsRegionRight - control - gap - _tabsStart);
        var maxScroll = Math.Max(0f, total - available);
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
            var rect = new RectangleF(x, _tabStripTop, widths[i], height);
            _tabRects.Add(rect);
            _closeRects.Add(new RectangleF(
                rect.Right - S(10) - closeSize,
                rect.Y + ((rect.Height - closeSize) / 2f),
                closeSize,
                closeSize));
            x += widths[i];
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

        Fluent.TextBox(
            g,
            new RectangleF(_addressChrome.X, _addressChrome.Y, _addressChrome.Width, _addressChrome.Height),
            null,
            null,
            _address.Focused,
            _addressHover,
            _scale);

        // 刷新 / 在浏览器打开：画在地址框内部右侧，悬停时叠 8% 状态层。
        var iconSize = 16f * _scale;
        var refreshState = _hoverFieldButton == 1 ? VisualState.Hover : VisualState.Rest;
        var externalState = _hoverFieldButton == 2 ? VisualState.Hover : VisualState.Rest;
        Fluent.IconButton(g, _refreshRect, "refresh", refreshState, iconSize,
            refreshState == VisualState.Hover ? Fluent.TextPrimary : Fluent.TextSecondary);
        Fluent.IconButton(g, _externalRect, "external", externalState, iconSize,
            externalState == VisualState.Hover ? Fluent.TextPrimary : Fluent.TextSecondary);
    }

    // ---------------------------------------------------------------- 标签交互

    private void HeaderMouseMove(object? sender, MouseEventArgs e)
    {
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

    private Color AddressFill => _addressHover ? Fluent.ControlFillHover : Fluent.ControlFill;

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
        _addressCollapsed = !_addressCollapsed;
        _collapse.Glyph = _addressCollapsed ? "chevron-down" : "chevron-up";
        _shellState.AddressCollapsed = _addressCollapsed;
        ShellStore.Save(_shellState);
        ApplyBounds();
        _header.Invalidate();
        _toolbar.Invalidate();
    }

    /// <summary>聚焦地址栏；收起状态下先展开。</summary>
    private void FocusAddress(bool selectAll)
    {
        if (_addressCollapsed)
        {
            ToggleAddressBar();
        }

        _address.Focus();
        if (selectAll)
        {
            _address.SelectAll();
        }
        else
        {
            _address.SelectionStart = _address.TextLength;
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

    /// <summary>先用 topmost 顶一下再恢复，确保第一次点击就能看到窗口。</summary>
    private void BringToFrontTemporarily()
    {
        try
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            _ = Native.SetForegroundWindow(Handle);
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
                ControlStyles.UserPaint,
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

        /// <summary>颜色语义与顺序保持 macOS：最小化 Caution、最大化 Success、关闭 Critical。</summary>
        private Color DotColor => _dot switch
        {
            Dot.Close => Fluent.Critical,
            Dot.Minimize => Fluent.Caution,
            _ => Fluent.Success,
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

            if (_hover)
            {
                using var backplate = Fluent.Rounded(new RectangleF(0, 0, Width, Height), Fluent.RCard);
                Fluent.Fill(g, backplate, Fluent.HoverLayer);
            }

            var diameter = 15f * (DeviceDpi / 96f);
            using var brush = new SolidBrush(DotColor);
            g.FillEllipse(
                brush,
                (Width / 2f) - (diameter / 2f),
                (Height / 2f) - (diameter / 2f),
                diameter,
                diameter);
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
                ControlStyles.UserPaint,
                true);
            BackColor = Fluent.Flyout;
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(this, tooltip);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Glyph { get; set; } = "refresh";

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
                Glyph,
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

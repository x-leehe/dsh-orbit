using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshStatusWidget;

/// <summary>
/// Web 外壳：用 WebView2 内嵌 DSH Web UI，带简单的多标签页。
///
/// 无边框自绘标题栏 + 标签条，拖动/缩放/最大化都自己处理；
/// 关闭行为可配置为「隐藏（WebView2 留在后台）」或「直接关闭」。
/// </summary>
internal sealed class WebShell : Form
{
    private const int HeaderHeight = 44;
    private const int TabStripHeight = 34;
    private const int ResizeBorder = 7;
    private const int DarkTitleBar = 20;
    private const int CornerPreference = 33;
    private const int MinWidth = 880;
    private const int MinHeight = 620;
    private const int MaxTabWidth = 196;
    private const int ResizeGrip = 6;
    private const int PlusWidth = 34;

    private static readonly Color HeaderBack = Color.FromArgb(20, 25, 34);
    private static readonly Color TabStripBack = Color.FromArgb(17, 22, 30);
    private static readonly Color ShellBack = Color.FromArgb(16, 20, 28);
    private static readonly Color AddressBack = Color.FromArgb(28, 34, 46);
    private static readonly Color TextPrimary = Color.FromArgb(226, 233, 244);
    private static readonly Color TextDim = Color.FromArgb(198, 208, 224);
    private static readonly Color TextMuted = Color.FromArgb(143, 160, 182);
    private static readonly Color Accent = Color.FromArgb(122, 170, 255);

    private readonly Panel _header;
    private readonly Panel _addressHost;
    private readonly TextBox _address;
    private readonly HeaderButton _refresh;
    private readonly HeaderButton _external;
    private readonly TrafficButton _minimize;
    private readonly TrafficButton _maximize;
    private readonly TrafficButton _close;

    private readonly Panel _tabStrip;
    private readonly Panel _webHost;
    private readonly List<Tab> _tabs = new();
    private readonly ShellState _shellState = ShellStore.Load();
    private readonly List<(Rectangle Bounds, int Index, bool IsClose)> _tabHits = new();
    private Rectangle _plusRect;

    private CoreWebView2Environment? _environment;
    private Tab? _active;
    private string _homeUrl;
    private bool _forcedClose;
    private int _hoverTab = -1;
    private int _tabScroll;



    public WebShell(string url, ShellCloseMode closeMode)
    {
        _homeUrl = url;
        CloseMode = closeMode;

        Text = "DSH-Orbit Web";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(MinWidth, MinHeight);
        Size = new Size(1280, 860);
        BackColor = ShellBack;
        FormBorderStyle = FormBorderStyle.Sizable;
        KeyPreview = true;
        DoubleBuffered = true;

        // ---- 标题栏 ----
        _header = new HitThroughPanel { BackColor = HeaderBack };
        _header.Paint += PaintHeader;


        _address = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = AddressBack,
            ForeColor = TextDim,
            Font = new Font("Segoe UI", 9.5f),
            Text = url,
        };
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateActive(_address.Text.Trim());
            }
        };

        _addressHost = new Panel { BackColor = AddressBack, Padding = new Padding(10, 7, 10, 7) };
        _addressHost.Controls.Add(_address);
        _addressHost.Paint += PaintAddressHost;
        _address.Dock = DockStyle.Fill;

        _refresh = new HeaderButton("刷新", () => ActiveTab?.View.CoreWebView2?.Reload()) { Glyph = "refresh" };
        _external = new HeaderButton("在浏览器打开", OpenExternal) { Glyph = "external" };
        _close = new TrafficButton(TrafficButton.Dot.Close, "关闭", HideOrClose);
        _minimize = new TrafficButton(TrafficButton.Dot.Minimize, "最小化", () => BeginInvoke(MinimizeWindow));
        _maximize = new TrafficButton(TrafficButton.Dot.Maximize, "最大化", () => BeginInvoke(ToggleMaximize));

        _header.Controls.Add(_addressHost);
        _header.Controls.Add(_refresh);
        _header.Controls.Add(_external);
        _header.Controls.Add(_minimize);
        _header.Controls.Add(_maximize);
        _header.Controls.Add(_close);

        // ---- 标签条 ----
        _tabStrip = new EdgePanel { BackColor = TabStripBack };
        _tabStrip.Paint += PaintTabs;
        _tabStrip.MouseMove += TabsMouseMove;
        _tabStrip.MouseLeave += (_, _) =>
        {
            _hoverTab = -1;
            _tabStrip.Invalidate();
        };
        _tabStrip.MouseUp += TabsMouseUp;
        MouseWheel += (_, e) =>
        {
            if (!_tabStrip.Bounds.Contains(PointToClient(Cursor.Position)))
            {
                return;
            }

            _tabScroll -= e.Delta / 2;
            _tabStrip.Invalidate();
        };

        // ---- 网页区域 ----
        _webHost = new EdgePanel { BackColor = ShellBack };

        Controls.Add(_webHost);
        Controls.Add(_tabStrip);
        Controls.Add(_header);

        Shown += async (_, _) =>
        {
            AppPaths.Log($"shell 显示：state={WindowState} zoomed={IsZoomed(Handle)} size={Size}");
            BringToFrontTemporarily();
            await CreateTabAsync(_homeUrl);
        };
        FormClosing += OnFormClosing;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.L)
            {
                _address.Focus();
                _address.SelectAll();
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
    }

    /// <summary>关闭窗口时的行为：隐藏（WebView2 留在后台）或直接关闭（进程退出）。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public ShellCloseMode CloseMode { get; set; }

    public string CurrentUrl => ActiveTab?.View.CoreWebView2?.Source ?? _address.Text;

    public bool IsAlive => _active?.View.CoreWebView2 is not null;

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
        tab.Start.Bounds = new Rectangle(ResizeGrip, ResizeGrip,
            Math.Max(0, _webHost.ClientSize.Width - (2 * ResizeGrip)),
            Math.Max(0, _webHost.ClientSize.Height - (2 * ResizeGrip)));

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

        tab.View.CoreWebView2?.Navigate(uri.ToString());
        RecordHistory(uri.ToString(), tab.Title);
    }

    private async Task AttachViewAsync(Tab tab, string target)
    {
        var view = new WebView2 { DefaultBackgroundColor = ShellBack };
        tab.View = view;
        _webHost.Controls.Add(view);
        view.Bounds = new Rectangle(
            ResizeGrip,
            ResizeGrip,
            Math.Max(0, _webHost.ClientSize.Width - (2 * ResizeGrip)),
            Math.Max(0, _webHost.ClientSize.Height - (2 * ResizeGrip)));

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

            core.DocumentTitleChanged += (_, _) =>
            {
                tab.Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "DSH Web" : core.DocumentTitle;
                _tabStrip.Invalidate();
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
        tab.Start?.Refresh();
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
        _tabStrip.Invalidate();
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
                tab.View.Dispose();
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

        if (_tabs.Count == 0)
        {
            _active = null;
            _tabStrip.Invalidate();
            HideOrClose();
            return;
        }

        if (ReferenceEquals(_active, tab))
        {
            Activate(_tabs[Math.Clamp(index, 0, _tabs.Count - 1)]);
        }

        _tabStrip.Invalidate();
    }

    private void NavigateActive(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        var tab = ActiveTab;
        if (tab?.View.CoreWebView2 is null)
        {
            _ = CreateTabAsync(uri.ToString());
            return;
        }

        EnsureSessionCookie(tab, uri.ToString());
        tab.Url = uri.ToString();
        _address.Text = uri.ToString();
        tab.View.CoreWebView2.Navigate(uri.ToString());
    }

    // ---------------------------------------------------------------- 布局

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        // 构造期间也会触发布局，此时子控件还没建好。
        if (_header is null || _tabStrip is null || _webHost is null || _addressHost is null || _close is null)
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


    private void LayoutChildren(int width, int height)
    {
        if (_header is null || _tabStrip is null || _webHost is null || _addressHost is null || _close is null)
        {
            return;
        }

        _header.Bounds = new Rectangle(0, 0, width, HeaderHeight);
        _tabStrip.Bounds = new Rectangle(0, HeaderHeight, width, TabStripHeight);
        _webHost.Bounds = new Rectangle(
            0,
            HeaderHeight + TabStripHeight,
            width,
            Math.Max(0, height - HeaderHeight - TabStripHeight));

        // 右侧：三颗红绿灯（关/最小化/最大化），命中区 32px 方便点。
        const int dotSize = 32;
        const int dotGap = 2;
        var groupLeft = width - 8 - ((dotSize * 3) + (dotGap * 2));
        // 保持原来的顺序：最小化 → 最大化 → 关闭（关闭仍在最右）。
        _minimize.Bounds = new Rectangle(groupLeft, 6, dotSize, dotSize);
        _maximize.Bounds = new Rectangle(groupLeft + dotSize + dotGap, 6, dotSize, dotSize);
        _close.Bounds = new Rectangle(groupLeft + (2 * (dotSize + dotGap)), 6, dotSize, dotSize);

        // 工具按钮（刷新 / 浏览器）放在红绿灯左边
        const int buttonWidth = 46;
        var toolsRight = groupLeft - 8;
        _external.Bounds = new Rectangle(toolsRight - buttonWidth, 8, buttonWidth - 2, HeaderHeight - 16);
        _refresh.Bounds = new Rectangle(toolsRight - (2 * buttonWidth), 8, buttonWidth - 2, HeaderHeight - 16);

        const int titleWidth = 112;
        var addressLeft = titleWidth;
        var addressRight = toolsRight - (2 * buttonWidth) - 6;
        _addressHost.Bounds = new Rectangle(
            addressLeft,
            9,
            Math.Max(160, addressRight - addressLeft - 10),
            HeaderHeight - 18);

        ResizeWebViews();

    }

    private void ResizeWebViews()
    {
        var width = Math.Max(0, _webHost.ClientSize.Width - (2 * ResizeGrip));
        var height = Math.Max(0, _webHost.ClientSize.Height - (2 * ResizeGrip));
        foreach (var tab in _tabs)
        {
            if (tab.View is not null)
            {
                tab.View.Bounds = new Rectangle(ResizeGrip, ResizeGrip, width, height);
            }

            if (tab.Start is not null)
            {
                tab.Start.Bounds = new Rectangle(ResizeGrip, ResizeGrip, width, height);
            }
        }
    }

    private void PaintHeader(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

        using (var pen = new Pen(Color.FromArgb(30, 255, 255, 255)))
        {
            g.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        }

        OrbitMark.Draw(g, new RectangleF(14, (_header.Height / 2f) - 9, 18, 18), Accent, 2.2f);

        using var font = new Font(
            Ui.PickFamily("HarmonyOS Sans SC Medium", Ui.PickFamily("HarmonyOS Sans", "Segoe UI")),
            12.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        Ui.Text(g, "DSH Web", font, TextPrimary, new RectangleF(38, 0, 84, _header.Height));
    }

    private void PaintAddressHost(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, _addressHost.Width - 1, _addressHost.Height - 1);
        using var path = Ui.Rounded(rect, 9);
        using var border = new Pen(Color.FromArgb(36, 255, 255, 255));
        g.DrawPath(border, path);
    }

    private void PaintTabs(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        _tabHits.Clear();

        using (var pen = new Pen(Color.FromArgb(26, 255, 255, 255)))
        {
            g.DrawLine(pen, 0, _tabStrip.Height - 1, _tabStrip.Width, _tabStrip.Height - 1);
        }

        using var titleFont = new Font(
            Ui.PickFamily("HarmonyOS Sans SC", Ui.PickFamily("HarmonyOS Sans", "Segoe UI")),
            11.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        // 标签多到放不下时横向滚动（滚轮），每页宽度固定，+ 号钉在右侧。
        var available = Math.Max(120, _tabStrip.Width - PlusWidth - 10);
        var tabWidth = MaxTabWidth;
        var total = _tabs.Count * tabWidth;
        var maxScroll = Math.Max(0, total - available);
        _tabScroll = Math.Clamp(_tabScroll, 0, maxScroll);

        var clipWidth = Math.Max(0, _tabStrip.Width - PlusWidth - 6);
        var clipState = g.Save();
        g.SetClip(new Rectangle(0, 0, clipWidth, _tabStrip.Height));
        var x = 6 - _tabScroll;

        for (var i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            var active = ReferenceEquals(tab, _active);
            var rect = new Rectangle(x, 3, (int)tabWidth - 4, TabStripHeight - 7);

            using (var path = Ui.Rounded(rect, 8))
            {
                if (active)
                {
                    // 只靠底色 + 更亮的文字区分激活态，不加下划线。
                    using var fill = new SolidBrush(Color.FromArgb(36, 43, 57));
                    g.FillPath(fill, path);
                }
                else if (_hoverTab == i)
                {
                    using var fill = new SolidBrush(Color.FromArgb(26, 32, 44));
                    g.FillPath(fill, path);
                }
            }

            var closeRect = new Rectangle(rect.Right - 22, rect.Y + ((rect.Height - 14) / 2), 14, 14);
            var showClose = active || _hoverTab == i;

            Ui.Text(g, tab.Title, titleFont, active ? TextPrimary : TextMuted,
                new RectangleF(rect.X + 10, rect.Y, rect.Width - (showClose ? 34 : 18), rect.Height),
                Ui.LeftEllipsis);

            if (showClose)
            {
                Icons.Draw(g, "x", closeRect, active ? Color.FromArgb(190, 202, 220) : TextMuted, 2f);
                _tabHits.Add((closeRect, i, true));
            }

            _tabHits.Add((rect, i, false));
            x += (int)tabWidth;
        }

        g.Restore(clipState);

        _plusRect = new Rectangle(_tabStrip.Width - PlusWidth + 2, 4, 26, TabStripHeight - 10);
        if (_hoverTab == -2)
        {
            using var plusPath = Ui.Rounded(_plusRect, 8);
            using var fill = new SolidBrush(Color.FromArgb(28, 34, 46));
            g.FillPath(fill, plusPath);
        }

        Icons.Draw(g, "plus", new RectangleF(_plusRect.X + 7, _plusRect.Y + 5, 12, 12), TextMuted, 2f);
    }

    // ---------------------------------------------------------------- 标签交互

    private void TabsMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = TabHitTest(e.Location);
        var index = hit.IsPlus ? -2 : hit.Index;
        if (index != _hoverTab)
        {
            _hoverTab = index;
            _tabStrip.Cursor = (hit.IsPlus || hit.Index >= 0) ? Cursors.Hand : Cursors.Default;
            _tabStrip.Invalidate();
        }
    }

    private void TabsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        var hit = TabHitTest(e.Location);
        if (hit.IsPlus)
        {
            CreateStartTab();
            return;
        }

        if (hit.Index < 0 || hit.Index >= _tabs.Count)
        {
            return;
        }

        if (hit.IsClose)
        {
            CloseTab(_tabs[hit.Index]);
            return;
        }

        Activate(_tabs[hit.Index]);
    }

    private (int Index, bool IsClose, bool IsPlus) TabHitTest(Point point)
    {
        if (_plusRect.Contains(point))
        {
            return (-1, false, true);
        }

        // 被 + 号区域裁掉的部分不参与命中。
        if (point.X > _tabStrip.Width - PlusWidth - 6)
        {
            return (-1, false, false);
        }

        foreach (var (bounds, index, isClose) in _tabHits)
        {
            if (isClose && bounds.Contains(point))
            {
                return (index, true, false);
            }
        }

        foreach (var (bounds, index, isClose) in _tabHits)
        {
            if (!isClose && bounds.Contains(point))
            {
                return (index, false, false);
            }
        }

        return (-1, false, false);
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
        var core = tab.View.CoreWebView2;
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
                tab.View.Dispose();
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
    private const int WmSysCommand = 0x0112;
    private const int ScMinimize = 0xF020;
    private const int ScMaximize = 0xF030;
    private const int ScRestore = 0xF120;
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
    private const int SmCxSizeFrame = 32;
    private const int SmCySizeFrame = 33;
    private const int SmCxPaddedBorder = 92;

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
            const int border = 6;
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

        return point.Y < HeaderHeight ? HtCaption : HtClient;
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
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, int wParam, IntPtr lParam);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var dark = 1;
            _ = DwmSetWindowAttribute(Handle, DarkTitleBar, ref dark, sizeof(int));
            var round = 2;
            _ = DwmSetWindowAttribute(Handle, CornerPreference, ref round, sizeof(int));
        }
        catch
        {
            // Windows 10 没有这些属性，忽略。
        }
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
            _refresh?.Dispose();
            _external?.Dispose();
            _minimize?.Dispose();
            _maximize?.Dispose();
            _close?.Dispose();
            _addressHost?.Dispose();
            _header?.Dispose();
            _tabStrip?.Dispose();
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>一个标签页 = 一个 WebView2（都在 _webHost 里，只显示当前那个）。</summary>
    private sealed class Tab
    {
        public WebView2? View { get; set; }
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
            if (m.Msg == 0x0084 && Parent is { } parent)
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

    /// <summary>标题栏面板：自身不接收鼠标命中，交给父窗口做 HTCAPTION。按钮/地址栏是它的子窗口，不受影响。</summary>
    private sealed class HitThroughPanel : BufferedPanel
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0084)
            {
                m.Result = (IntPtr)(-1); // HTTRANSPARENT
                return;
            }

            base.WndProc(ref m);
        }
    }

    /// <summary>macOS 风格的红绿灯按钮：彩色圆点 + 悬停符号，命中区比圆点大。</summary>
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
            BackColor = Color.Transparent;

            _toolTip = new ToolTip();
            _toolTip.SetToolTip(this, tooltip);
        }

        private Color BaseColor => _dot switch
        {
            Dot.Close => Color.FromArgb(255, 95, 87),
            Dot.Minimize => Color.FromArgb(254, 188, 46),
            _ => Color.FromArgb(40, 200, 64),
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
            g.SmoothingMode = SmoothingMode.AntiAlias;

            const float diameter = 15f;
            var cx = Width / 2f;
            var cy = Height / 2f;
            var circle = new RectangleF(cx - (diameter / 2f), cy - (diameter / 2f), diameter, diameter);

            using (var fill = new SolidBrush(_hover ? ControlPaint.Light(BaseColor, 0.12f) : BaseColor))
            {
                g.FillEllipse(fill, circle);
            }

            if (!_hover)
            {
                return;
            }

            var ink = _dot switch
            {
                Dot.Close => Color.FromArgb(120, 22, 18),
                Dot.Minimize => Color.FromArgb(96, 62, 0),
                _ => Color.FromArgb(10, 74, 22),
            };

            using var pen = new Pen(ink, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            switch (_dot)
            {
                case Dot.Close:
                    g.DrawLine(pen, cx - 3.5f, cy - 3.5f, cx + 3.5f, cy + 3.5f);
                    g.DrawLine(pen, cx + 3.5f, cy - 3.5f, cx - 3.5f, cy + 3.5f);
                    break;
                case Dot.Minimize:
                    g.DrawLine(pen, cx - 4f, cy, cx + 4f, cy);
                    break;
                default:
                    g.DrawLine(pen, cx - 4f, cy, cx + 4f, cy);
                    g.DrawLine(pen, cx, cy - 4f, cx, cy + 4f);
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

    /// <summary>标题栏上的图标按钮（Lucide 图标，自绘 hover）。</summary>
    private sealed class HeaderButton : Control
    {
        private readonly Action _action;
        private readonly ToolTip _toolTip;
        private bool _hover;

        public HeaderButton(string tooltip, Action action)
        {
            _action = action;
            Size = new Size(44, 28);
            TabStop = false;
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint |
                ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            _toolTip = new ToolTip();
            _toolTip.SetToolTip(this, tooltip);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Glyph { get; set; } = "min";

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Danger { get; init; }

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
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_hover)
            {
                using var path = Ui.Rounded(new Rectangle(0, 0, Width, Height), 7);
                using var fill = new SolidBrush(
                    Danger ? Color.FromArgb(46, 240, 90, 90) : Color.FromArgb(26, 255, 255, 255));
                g.FillPath(fill, path);
            }

            var color = Danger && _hover ? Color.FromArgb(255, 150, 150) : Color.FromArgb(168, 180, 198);
            var iconName = Glyph switch
            {
                "refresh" => "refresh",
                "external" => "external",
                "min" => "minus",
                "max" => "square",
                "restore" => "copy",
                "close" => "x",
                _ => "square",
            };

            Icons.Draw(g, iconName, new RectangleF((Width / 2f) - 8, (Height / 2f) - 8, 16, 16), color, 2f);
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

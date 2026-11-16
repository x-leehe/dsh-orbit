using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 离屏渲染真实界面，产出文档截图与设计自查图。
/// 用法：
///   RenderPreview.exe &lt;输出目录&gt;                 挂件 + 菜单 + 新标签页（浅/深）
///   RenderPreview.exe &lt;目录&gt; --only menu           只渲染某一面（widget/menu/start/dialog/shell/chrome/tokens）
///   RenderPreview.exe &lt;目录&gt; --shell &lt;url&gt;        截取真实外壳窗口
///   RenderPreview.exe &lt;目录&gt; --only chrome         只画标题栏 + 工具条（不建 WebView2，可像素级核对文字）
///   RenderPreview.exe &lt;目录&gt; --icons               图标对照表
/// </summary>
internal static class Program
{
    private static readonly AppTheme[] Themes = [AppTheme.Light, AppTheme.Dark];

    private static string ThemeName(AppTheme theme) => theme == AppTheme.Light ? "light" : "dark";

    [STAThread]
    private static void Main(string[] args)
    {
        var outputDir = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
            ? args[0]
            : ".";
        Directory.CreateDirectory(outputDir);
        ApplicationConfiguration.Initialize();

        var onlyIndex = Array.IndexOf(args, "--only");
        var only = onlyIndex >= 0 && onlyIndex + 1 < args.Length ? args[onlyIndex + 1] : null;

        if (args.Contains("--verify-tabs"))
        {
            VerifyTabs(int.TryParse(args.ElementAtOrDefault(Array.IndexOf(args, "--verify-tabs") + 1), out var n) ? n : 12);
            return;
        }

        if (args.Contains("--verify-toggle"))
        {
            VerifyToggle();
            return;
        }

        if (args.Contains("--icons"))
        {
            DrawIconSheet(Path.Combine(outputDir, "icons.png"));
            Console.WriteLine("saved icons.png");
            return;
        }

        var shellIndex = Array.IndexOf(args, "--shell");
        var shellUrl = shellIndex >= 0 && shellIndex + 1 < args.Length ? args[shellIndex + 1] : null;

        foreach (var theme in Themes)
        {
            Fluent.Init(theme);
            var suffix = ThemeName(theme);

            if (only is null or "tokens")
            {
                RenderTokens(Path.Combine(outputDir, $"tokens-{suffix}.png"));
            }

            if (only is null or "widget")
            {
                RenderWidget(outputDir, suffix);
            }

            if (only is null or "menu")
            {
                RenderMenu(outputDir, suffix);
            }

            if (only is null or "start")
            {
                RenderStartPage(outputDir, suffix);
            }

            if (only is null or "dialog")
            {
                RenderDialog(outputDir, suffix);
            }

            if (only is null or "chrome")
            {
                RenderChrome(outputDir, suffix);
            }

            if (only == "shell" || (only is null && shellUrl is not null))
            {
                var tabsArg = Array.IndexOf(args, "--tabs");
                var tabCount = tabsArg >= 0 && tabsArg + 1 < args.Length && int.TryParse(args[tabsArg + 1], out var n) ? n : 0;
                RenderShell(outputDir, suffix, shellUrl ?? "about:blank", args.Contains("--shell-start"), tabCount);
            }
        }

        Application.ExitThread();
    }

    /// <summary>
    /// 地址栏折叠的确定性验证：不开窗、不碰 WebView2，直接调内部方法看布局是否真的变了。
    /// </summary>
    private static void VerifyToggle()
    {
        Fluent.Init(AppTheme.Dark);
        var shell = new WebShell("", ShellCloseMode.Hide);
        var type = typeof(WebShell);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var toggle = type.GetMethod("ToggleAddressBar", Flags)!;
        var layout = type.GetMethod("LayoutChildren", Flags)!;
        var toolbar = (Control)type.GetField("_toolbar", Flags)!.GetValue(shell)!;
        var host = (Control)type.GetField("_webHost", Flags)!.GetValue(shell)!;
        var collapse = (Control)type.GetField("_collapse", Flags)!.GetValue(shell)!;

        // 账本：窗体不显示时 CreateControl 不建句柄，ApplyBounds 会早退，所以直接驱动布局。
        shell.ClientSize = new Size(1280, 860);
        layout.Invoke(shell, new object[] { 1280, 860 });

        var collapsedField = type.GetField("_addressCollapsed", Flags)!;

        var glyphProperty = collapse.GetType().GetProperty("Glyph")!;

        void Report(string label) => Console.WriteLine(
            $"{label,-10} glyph={glyphProperty.GetValue(collapse),-12} flag={collapsedField.GetValue(shell),-5} created={shell.IsHandleCreated,-5} " +
            $"toolbarVisible={toolbar.Visible,-5} toolbar={toolbar.Bounds} hostTop={host.Top,-3} hostH={host.Height,-3} " +
            $"collapseX={collapse.Left} client={shell.ClientSize}");

        Report("expanded");

        // 和真实点击同一条处理链：HeaderButton.OnMouseUp → _action() → ToggleAddressBar
        var onMouseUp = typeof(Control).GetMethod("OnMouseUp", Flags)!;
        void Click(Control target) => onMouseUp.Invoke(
            target, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0) });

        // 折叠动画：点一下按钮后不泵消息，手动逐帧驱动定时器的 tick，观察中间态
        var timer = (System.Windows.Forms.Timer)type.GetField("_addressTimer", Flags)!.GetValue(shell)!;
        var tick = type.GetMethod("OnAddressAnimationTick", Flags)!;

        var fromField = type.GetField("_addressAnimFrom", Flags)!;
        var toField = type.GetField("_addressAnimTo", Flags)!;
        var stepField = type.GetField("_addressAnimStep", Flags)!;

        string Drive(string label)
        {
            // 真实节拍：让 WinForms 定时器自己跑，按时间采样高度
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var frames = new List<string>();
            var last = -1;
            while (clock.ElapsedMilliseconds < 320)
            {
                Application.DoEvents();
                Thread.Sleep(5);
                if (toolbar.Height != last)
                {
                    last = toolbar.Height;
                    frames.Add($"{clock.ElapsedMilliseconds}ms:{last}");
                }
            }

            return $"{label}: {string.Join("  ", frames)}";
        }

        Click(collapse);
        Console.WriteLine(Drive("collapse"));
        Report("collapsed");

        Click(collapse);
        Console.WriteLine(Drive("expand"));
        Report("expanded2");
        shell.Dispose();
    }

    /// <summary>多标签布局自检：宽度收缩、溢出判定、滚动上限。</summary>
    private static void VerifyTabs(int count)
    {
        Fluent.Init(AppTheme.Dark);
        var shell = new WebShell("", ShellCloseMode.Hide);
        var type = typeof(WebShell);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        var create = type.GetMethod("CreateStartTab", Flags)!;
        for (var i = 1; i < count; i++)
        {
            create.Invoke(shell, null);
        }

        shell.ClientSize = new Size(1280, 860);
        type.GetMethod("LayoutChildren", Flags)!.Invoke(shell, new object[] { 1280, 860 });

        var tabs = (System.Collections.IList)type.GetField("_tabs", Flags)!.GetValue(shell)!;
        var rects = (System.Collections.IList)type.GetField("_tabRects", Flags)!.GetValue(shell)!;
        var overflow = type.GetField("_hasOverflow", Flags)!.GetValue(shell);
        var scroll = type.GetField("_tabScroll", Flags)!.GetValue(shell);
        var maxScroll = type.GetField("_tabMaxScroll", Flags)!.GetValue(shell);
        var clipRight = type.GetField("_tabsClipRight", Flags)!.GetValue(shell);
        var width = type.GetField("_tabWidth", Flags)!.GetValue(shell);

        Console.WriteLine($"tabs={tabs.Count} rects={rects.Count} width={width} overflow={overflow} scroll={scroll}/{maxScroll} clipRight={clipRight}");
        shell.Dispose();
    }

    // ---------------------------------------------------------------- 挂件

    private static void RenderWidget(string outputDir, string suffix)
    {
        var config = new WidgetConfig { AlwaysBottom = false, AlwaysOnTop = true, ClickThrough = false };
        using var form = new WidgetForm(config);
        form.Show();
        Application.DoEvents();

        // 按真实用法贴到右屏：图标贴住屏幕右缘（贴屏侧两角是方角），浮层向左长。
        form.DockTo(DockSide.Right, hidden: false);

        var running = new StatusSnapshot
        {
            State = DshState.Running,
            Profile = "web",
            Version = "0.25.4",
            Port = 3080,
            DisplayUrl = "http://127.0.0.1:3080/",
            Uptime = new TimeSpan(0, 2, 6, 22),
            MemoryBytes = 268L * 1024 * 1024,
            CpuPercent = 3,
            PluginTotal = 12,
            PluginDisabled = 2,
            PluginNames = "dsh-qqbot",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            OffPeak = false,
            UntilPriceSwitch = TimeSpan.FromMinutes(192),
            ActiveSessions = 3,
        };

        // 已停止：闲时中（倒计时走 Success 分支），会话数拿不到（显示「—」）。
        var stopped = new StatusSnapshot
        {
            State = DshState.Stopped,
            Profile = "web",
            Version = "0.25.4",
            PluginTotal = 12,
            PluginDisabled = 2,
            PluginNames = "dsh-qqbot",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            OffPeak = true,
            UntilPriceSwitch = TimeSpan.FromMinutes(102),
            ActiveSessions = null,
        };

        // 什么都没有：端口、会话、峰谷全部走降级分支，并带一行 hint。
        var missing = new StatusSnapshot
        {
            State = DshState.Stopped,
            Profile = "web",
            PluginTotal = null,
            Hint = "未找到 dsh：请先安装 dsh，或手动运行 dsh web",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            UntilPriceSwitch = null,
            ActiveSessions = null,
        };

        // 每个状态两种形态：浮层态（预览总览）与贴边图标态。
        foreach (var (name, snapshot) in new[]
                 {
                     ("running", running),
                     ("stopped", stopped),
                     ("missing", missing),
                 })
        {
            form.SetStatus(snapshot);
            Save(form, WidgetPose.Flyout, Path.Combine(outputDir, $"preview-{name}-{suffix}.png"));
            Save(form, WidgetPose.Icon, Path.Combine(outputDir, $"preview-icon-{name}-{suffix}.png"));
        }

        // 穿透态：完整卡片、底部两枚按钮换成一行说明（鼠标直接落到桌面）。
        form.SetStatus(running);
        form.ApplyClickThrough(true);
        Save(form, WidgetPose.Flyout, Path.Combine(outputDir, $"preview-clickthrough-{suffix}.png"));
        form.ApplyClickThrough(false);

        // 收起态：卡片滑出屏幕、只留屏边那根 6×28 拉手（要真收起，拉手才会落在窗口可见条里）。
        form.DockTo(DockSide.Right, hidden: true);
        foreach (var (name, snapshot) in new[] { ("running", running), ("stopped", stopped) })
        {
            form.SetStatus(snapshot);
            Save(form, WidgetPose.Handle, Path.Combine(outputDir, $"preview-handle-{name}-{suffix}.png"));
        }

        form.Close();
    }

    // ---------------------------------------------------------------- 菜单

    private static void RenderMenu(string outputDir, string suffix)
    {
        var live = new MenuState(
            Running: true,
            Starting: false,
            HasUrl: true,
            LayoutBottom: false,
            LayoutTop: true,
            Locked: false,
            EdgeHideEnabled: true,
            Visible: true,
            ShellHide: true,
            AutoStart: AutoStartMode.FullAuto);

        using (var menu = new TrayMenuForm(live, _ => { }))
        {
            using var bitmap = menu.Snapshot();
            bitmap?.Save(Path.Combine(outputDir, $"menu-{suffix}.png"), ImageFormat.Png);
            Console.WriteLine($"saved menu-{suffix}.png");
        }

        var stopped = new MenuState(
            Running: false,
            Starting: false,
            HasUrl: false,
            LayoutBottom: true,
            LayoutTop: false,
            Locked: false,
            EdgeHideEnabled: true,
            Visible: true,
            ShellHide: false,
            AutoStart: AutoStartMode.Disabled);

        using (var menu = new TrayMenuForm(stopped, _ => { }))
        {
            using var bitmap = menu.Snapshot();
            bitmap?.Save(Path.Combine(outputDir, $"menu-stopped-{suffix}.png"), ImageFormat.Png);
            Console.WriteLine($"saved menu-stopped-{suffix}.png");
        }
    }

    // ---------------------------------------------------------------- 新标签页

    private static void RenderStartPage(string outputDir, string suffix)
    {
        var state = ShellState.CreateDefault();
        state.History =
        [
            new HistoryEntry { Title = "DeepSeek 开放平台", Url = "https://platform.deepseek.com/usage", VisitedAt = DateTime.Now },
            new HistoryEntry { Title = "GitHub · x-leehe/dsh-orbit", Url = "https://github.com/x-leehe/dsh-orbit", VisitedAt = DateTime.Now.AddMinutes(-8) },
            new HistoryEntry { Title = "WinUI 3 文档", Url = "https://learn.microsoft.com/windows/apps/winui/winui3", VisitedAt = DateTime.Now.AddMinutes(-20) },
            new HistoryEntry { Title = "Material 3", Url = "https://m3.material.io/", VisitedAt = DateTime.Now.AddMinutes(-40) },
            new HistoryEntry { Title = "Lucide Icons", Url = "https://lucide.dev/icons/", VisitedAt = DateTime.Now.AddHours(-2) },
            new HistoryEntry { Title = "Bun 文档", Url = "https://bun.sh/docs", VisitedAt = DateTime.Now.AddHours(-5) },
            new HistoryEntry { Title = "Visual Studio 下载", Url = "https://visualstudio.microsoft.com/", VisitedAt = DateTime.Now.AddDays(-1) },
        ];

        using var host = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-6000, -6000),
            ClientSize = new Size(1240, 780),
            ShowInTaskbar = false,
        };

        var page = new StartPage(state, "http://127.0.0.1:3080/") { Dock = DockStyle.Fill };
        host.Controls.Add(page);
        host.Show();
        Pump(120);

        using var bitmap = new Bitmap(page.Width, page.Height);
        page.DrawToBitmap(bitmap, new Rectangle(0, 0, page.Width, page.Height));
        bitmap.Save(Path.Combine(outputDir, $"start-{suffix}.png"), ImageFormat.Png);
        Console.WriteLine($"saved start-{suffix}.png");

        host.Close();
        host.Dispose();
    }

    // ---------------------------------------------------------------- 对话框

    private static void RenderDialog(string outputDir, string suffix)
    {
        using var dialog = new AddEngineDialog
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-6000, -6000),
            ShowInTaskbar = false,
        };
        dialog.Show();
        Pump(120);

        using var bitmap = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(bitmap, new Rectangle(0, 0, dialog.Width, dialog.Height));
        bitmap.Save(Path.Combine(outputDir, $"dialog-{suffix}.png"), ImageFormat.Png);
        Console.WriteLine($"saved dialog-{suffix}.png");
        dialog.Close();
    }

    // ---------------------------------------------------------------- 外壳

    private static void RenderShell(string outputDir, string suffix, string url, bool startTab = false, int tabs = 0)
    {
        var shell = new WebShell(url, ShellCloseMode.Hide);
        shell.Show();

        if (tabs > 1)
        {
            var createTab = typeof(WebShell).GetMethod(
                "CreateStartTab",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            for (var i = 1; i < tabs; i++)
            {
                createTab?.Invoke(shell, null);
            }

            Pump(400);
        }
        shell.TopMost = true;
        shell.Activate();
        // WebView2 加载真实页面要一点时间，太短会截到 loading 态。
        Pump(startTab ? 900 : 2800);

        if (startTab)
        {
            // 新标签页没有公开入口，渲染器里反射调用一下（等价 Ctrl+T）。
            var create = typeof(WebShell).GetMethod(
                "CreateStartTab",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            create?.Invoke(shell, null);
            Pump(700);
        }

        using var bitmap = Capture(shell);

        var name = startTab ? $"shell-start-{suffix}.png" : $"shell-{suffix}.png";
        bitmap.Save(Path.Combine(outputDir, name), ImageFormat.Png);
        Console.WriteLine($"saved {name}");
        shell.ForceClose();
    }

    /// <summary>
    /// 只画标题栏 + 工具条：不建 WebView2，所以程序在跑的时候也能随时跑，
    /// 用来核对字号 / 文字渲染 / 标签间距（配合 4 倍放大图看笔画是否发虚）。
    /// </summary>
    private static void RenderChrome(string outputDir, string suffix)
    {
        var shell = new WebShell("", ShellCloseMode.Hide);
        var type = typeof(WebShell);
        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        // 构造函数里已有一个新标签页，再补一个，方便看激活 / 非激活两种文字颜色。
        var create = type.GetMethod("CreateStartTab", Flags)!;
        for (var i = 1; i < 3; i++)
        {
            create.Invoke(shell, null);
        }

        shell.ClientSize = new Size(1280, 860);
        // 不管用户当前是收起还是展开，这里固定渲染展开态：地址行文字也要一起核对。
        type.GetField("_addressCollapsed", Flags)!.SetValue(shell, false);
        type.GetMethod("LayoutChildren", Flags)!.Invoke(shell, new object[] { 1280, 860 });

        var header = (Control)type.GetField("_header", Flags)!.GetValue(shell)!;
        var toolbar = (Control)type.GetField("_toolbar", Flags)!.GetValue(shell)!;

        using var chrome = new Bitmap(header.Width, header.Height + toolbar.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(chrome))
        {
            type.GetMethod("PaintHeader", Flags)!.Invoke(
                shell,
                new object?[] { null, new PaintEventArgs(g, new Rectangle(0, 0, header.Width, header.Height)) });

            var state = g.Save();
            g.TranslateTransform(0, header.Height);
            g.SetClip(new Rectangle(0, 0, toolbar.Width, toolbar.Height));
            type.GetMethod("PaintToolbar", Flags)!.Invoke(
                shell,
                new object?[] { null, new PaintEventArgs(g, new Rectangle(0, 0, toolbar.Width, toolbar.Height)) });
            g.Restore(state);
        }

        chrome.Save(Path.Combine(outputDir, $"chrome-{suffix}.png"), ImageFormat.Png);
        Console.WriteLine($"saved chrome-{suffix}.png（标题栏 {header.Height}px + 工具条 {toolbar.Height}px）");

        var rects = (System.Collections.IList)type.GetField("_tabRects", Flags)!.GetValue(shell)!;
        if (rects.Count > 0 && rects[0] is RectangleF first)
        {
            var left = Math.Max(0, (int)first.X - 6);
            var width = Math.Min(chrome.Width - left, (int)(first.Width * 2) + 12);
            using var crop = chrome.Clone(new Rectangle(left, 0, Math.Max(1, width), chrome.Height), chrome.PixelFormat);
            using var zoom = new Bitmap(crop.Width * 4, crop.Height * 4, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(zoom))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(crop, new Rectangle(0, 0, zoom.Width, zoom.Height));
            }

            zoom.Save(Path.Combine(outputDir, $"chrome-{suffix}-zoom.png"), ImageFormat.Png);
            Console.WriteLine($"saved chrome-{suffix}-zoom.png");
        }

        shell.Dispose();
    }

    /// <summary>
    /// 抓窗口内容：优先 PrintWindow(PW_RENDERFULLCONTENT)，失败或被其他窗口遮挡时再退回屏幕拷贝。
    /// 直接用 CopyFromScreen 会把盖在上面的窗口一起拍进来。
    /// </summary>
    private static Bitmap Capture(Form form)
    {
        var bitmap = new Bitmap(form.Width, form.Height);
        var rendered = false;
        try
        {
            using var g = Graphics.FromImage(bitmap);
            var hdc = g.GetHdc();
            try
            {
                rendered = PrintWindow(form.Handle, hdc, 2);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        catch
        {
            rendered = false;
        }

        if (rendered && !IsBlank(bitmap))
        {
            return bitmap;
        }

        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(form.Left, form.Top, 0, 0, form.Size);
        }

        return bitmap;
    }

    private static bool IsBlank(Bitmap bitmap)
    {
        var first = bitmap.GetPixel(0, 0);
        for (var y = 0; y < bitmap.Height; y += 7)
        {
            for (var x = 0; x < bitmap.Width; x += 7)
            {
                if (bitmap.GetPixel(x, y) != first)
                {
                    return false;
                }
            }
        }

        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    // ---------------------------------------------------------------- 令牌自查表

    private static void RenderTokens(string path)
    {
        const int width = 900;
        const int height = 760;
        using var bitmap = new Bitmap(width, height);
        using var g = Graphics.FromImage(bitmap);
        Fluent.AntiAlias(g, crisp: true);
        g.Clear(Fluent.Mica);

        // 卡片 + 海拔
        Fluent.Card(g, new RectangleF(20, 20, 400, 210), Fluent.RCard, 3);
        Fluent.Card(g, new RectangleF(440, 20, 200, 100), Fluent.RCard, 1);
        Fluent.Card(g, new RectangleF(440, 130, 200, 100), Fluent.RCard, 2);
        Fluent.Card(g, new RectangleF(660, 20, 220, 210), Fluent.RCard, 0);

        // 版式阶梯
        var y = 44f;
        foreach (var (role, name) in new[]
                 {
                     (TextRole.Display, "Display 40/52"),
                     (TextRole.Title, "Title 28/36"),
                     (TextRole.Subtitle, "Subtitle 20/28"),
                     (TextRole.Body, "Body 14/20"),
                     (TextRole.Caption, "Caption 12/16"),
                 })
        {
            Fluent.Text(g, name, role, true, Fluent.TextPrimary, new RectangleF(36, y, 380, 34));
            y += role == TextRole.Display ? 56 : Fluent.LineHeight(role) + 2;
        }

        // 按钮
        Fluent.Button(g, new RectangleF(440, 250, 110, 32), "标准按钮", SurfaceRole.Standard);
        Fluent.Button(g, new RectangleF(560, 250, 110, 32), "强调按钮", SurfaceRole.Accent);
        Fluent.Button(g, new RectangleF(680, 250, 110, 32), "悬停", SurfaceRole.Standard, VisualState.Hover);
        Fluent.Button(g, new RectangleF(800, 250, 80, 32), "禁用", SurfaceRole.Standard, VisualState.Disabled);

        Fluent.Button(g, new RectangleF(440, 292, 130, 32), "带图标", SurfaceRole.Standard, VisualState.Rest, "refresh");
        Fluent.Button(g, new RectangleF(580, 292, 130, 32), "危险", SurfaceRole.Danger, VisualState.Hover, "trash-2");

        // 图标按钮
        Fluent.IconButton(g, new RectangleF(730, 292, 32, 32), "refresh");
        Fluent.IconButton(g, new RectangleF(770, 292, 32, 32), "external-link", VisualState.Hover);
        Fluent.IconButton(g, new RectangleF(810, 292, 32, 32), "x", VisualState.Pressed);

        // 药丸
        Fluent.Pill(g, new RectangleF(20, 260, 96, 24), "运行中", Fluent.Success, Fluent.Mix(Fluent.Layer, Fluent.Success, 0.18f), true);
        Fluent.Pill(g, new RectangleF(126, 260, 96, 24), "已停止", Fluent.TextTertiary, Fluent.Mix(Fluent.Layer, Fluent.TextTertiary, 0.12f), true);

        // 分段
        Fluent.Segmented(g, new RectangleF(20, 300, 300, 32), ["置底", "置顶", "普通"], 1, 2);
        Fluent.Segmented(g, new RectangleF(20, 344, 300, 32), ["跟随系统", "浅色", "深色"], 0);

        // 菜单行
        Fluent.MenuRow(g, new RectangleF(20, 392, 300, 36), "lock", "锁定穿透", "开");
        Fluent.MenuRow(g, new RectangleF(20, 428, 300, 36), "eye", "隐藏挂件", null, VisualState.Hover);
        Fluent.MenuRow(g, new RectangleF(20, 464, 300, 36), "power", "退出", null, VisualState.Rest, Fluent.Critical);

        // 输入框
        Fluent.TextBox(g, new RectangleF(440, 344, 400, 32), "127.0.0.1:3080", "搜索，或输入网址后回车", false, false);
        Fluent.TextBox(g, new RectangleF(440, 384, 400, 32), "", "聚焦状态", true, false);
        Fluent.TextBox(g, new RectangleF(440, 424, 400, 32), "", "悬停状态", false, true);

        // 磁贴
        Fluent.Tile(g, new RectangleF(440, 470, 180, 120));
        Fluent.Tile(g, new RectangleF(630, 470, 180, 120), VisualState.Hover);
        Fluent.Tile(g, new RectangleF(820, 470, 60, 60));

        // 分隔线
        Fluent.Divider(g, new RectangleF(20, 512, 300, 1));
        Fluent.SectionLabel(g, new RectangleF(20, 524, 300, 20), "分组标题");

        // 文本层级
        Fluent.Text(g, "TextPrimary 主要文字", TextRole.Body, false, Fluent.TextPrimary, new RectangleF(20, 552, 300, 20));
        Fluent.Text(g, "TextSecondary 次要文字", TextRole.Body, false, Fluent.TextSecondary, new RectangleF(20, 572, 300, 20));
        Fluent.Text(g, "TextTertiary 三级文字", TextRole.Body, false, Fluent.TextTertiary, new RectangleF(20, 592, 300, 20));
        Fluent.Text(g, "TextDisabled 禁用文字", TextRole.Body, false, Fluent.TextDisabled, new RectangleF(20, 612, 300, 20));

        Fluent.Text(g, $"主题 {(Fluent.IsDark ? "Dark" : "Light")} · 强调色 {Fluent.AccentFill.Name}",
            TextRole.Caption, true, Fluent.TextSecondary, new RectangleF(20, 660, 500, 20));
        Fluent.Text(g, $"{Fluent.BodyFamily} / {Fluent.StrongFamily} / {Fluent.DisplayStrongFamily}",
            TextRole.Caption, false, Fluent.TextTertiary, new RectangleF(20, 680, 700, 20));

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {Path.GetFileName(path)}");
    }

    // ---------------------------------------------------------------- 工具

    private static void Save(WidgetForm form, WidgetPose pose, string path)
    {
        using var bitmap = form.SnapshotPose(pose);
        if (bitmap is null)
        {
            // 别静默失败：null 说明没建句柄或客户区为 0，等于没出图。
            Console.Error.WriteLine($"挂件截图失败（{pose}）：{Path.GetFileName(path)}");
            return;
        }

        bitmap.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {Path.GetFileName(path)}（{bitmap.Width}×{bitmap.Height}）");
    }

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    /// <summary>把 LucideData 里的图标画成一张对照表，确认几何正确。</summary>
    private static void DrawIconSheet(string path)
    {
        var names = LucideData.Shapes.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        const int columns = 6;
        const int cell = 118;
        var rows = (int)Math.Ceiling(names.Length / (double)columns);
        using var bitmap = new Bitmap(columns * cell, rows * cell);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.FromArgb(16, 20, 28));
        Fluent.AntiAlias(g, crisp: true);

        using var font = new Font("Segoe UI", 10, FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(Color.FromArgb(198, 208, 224));

        for (var i = 0; i < names.Length; i++)
        {
            var col = i % columns;
            var row = i / columns;
            var box = new RectangleF((col * cell) + ((cell - 32) / 2f), (row * cell) + 24, 32, 32);
            Icons.Draw(g, names[i], box, Color.FromArgb(63, 208, 138), 2f);
            var label = names[i];
            var size = g.MeasureString(label, font);
            g.DrawString(label, font, brush, (col * cell) + ((cell - size.Width) / 2f), (row * cell) + 70);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}

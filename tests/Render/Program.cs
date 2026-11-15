using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 离屏渲染真实界面，产出文档截图与设计自查图。
/// 用法：
///   RenderPreview.exe &lt;输出目录&gt;                 挂件 + 菜单 + 新标签页（浅/深）
///   RenderPreview.exe &lt;目录&gt; --only menu           只渲染某一面（widget/menu/start/dialog/shell/tokens）
///   RenderPreview.exe &lt;目录&gt; --shell &lt;url&gt;        截取真实外壳窗口
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

            if (only == "shell" || (only is null && shellUrl is not null))
            {
                RenderShell(outputDir, suffix, shellUrl ?? "about:blank");
            }
        }

        Application.ExitThread();
    }

    // ---------------------------------------------------------------- 挂件

    private static void RenderWidget(string outputDir, string suffix)
    {
        var config = new WidgetConfig { AlwaysBottom = false, AlwaysOnTop = true, ClickThrough = false };
        using var form = new WidgetForm(config);
        form.Show();
        Application.DoEvents();

        var running = new StatusSnapshot
        {
            State = DshState.Running,
            Profile = "web",
            Version = "0.2.1-rc.5",
            Port = 3080,
            DisplayUrl = "http://127.0.0.1:3080/",
            Uptime = new TimeSpan(0, 2, 6, 22),
            MemoryBytes = 268L * 1024 * 1024,
            CpuPercent = 3,
            PluginTotal = 1,
            PluginDisabled = 0,
            PluginNames = "dsh-qqbot",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            OffPeak = false,
        };

        var stopped = new StatusSnapshot
        {
            State = DshState.Stopped,
            Profile = "web",
            Version = "0.2.1-rc.5",
            PluginTotal = 1,
            PluginDisabled = 0,
            PluginNames = "dsh-qqbot",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            OffPeak = true,
        };

        Save(form, running, Path.Combine(outputDir, $"preview-running-{suffix}.png"));
        Save(form, stopped, Path.Combine(outputDir, $"preview-stopped-{suffix}.png"));
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

    private static void RenderShell(string outputDir, string suffix, string url)
    {
        var shell = new WebShell(url, ShellCloseMode.Hide);
        shell.Show();
        shell.TopMost = true;
        shell.Activate();
        Pump(900);

        using var bitmap = Capture(shell);

        bitmap.Save(Path.Combine(outputDir, $"shell-{suffix}.png"), ImageFormat.Png);
        Console.WriteLine($"saved shell-{suffix}.png");
        shell.ForceClose();
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
        Fluent.AntiAlias(g);
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

    private static void Save(WidgetForm form, StatusSnapshot snapshot, string path)
    {
        form.SetStatus(snapshot);
        form.Refresh();
        Pump(30);
        using var bitmap = form.Snapshot();
        bitmap?.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {Path.GetFileName(path)}");
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
        Fluent.AntiAlias(g);

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

using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>用真实的 WidgetForm 绘制逻辑离屏渲染两种状态，产出文档截图。</summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outputDir = args.Length > 0 ? args[0] : ".";
        ApplicationConfiguration.Initialize();

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
            PluginDisabled = 0,    PluginNames = "dsh-qqbot",
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
            PluginDisabled = 0,    PluginNames = "dsh-qqbot",
            Balance = 24.45m,
            SpentToday = 0.6400m,
            OffPeak = true,
        };

        Save(form, running, Path.Combine(outputDir, "preview-running.png"));
        Save(form, stopped, Path.Combine(outputDir, "preview-stopped.png"));

        if (args.Contains("--icons"))
        {
            DrawIconSheet(Path.Combine(outputDir, "icons.png"));
            Console.WriteLine("saved icons.png");
            return;
        }

        var shellIndex = Array.IndexOf(args, "--shell");
        if (shellIndex >= 0 && shellIndex + 1 < args.Length)
        {
            var url = args[shellIndex + 1];
            var shell = new WebShell(url, ShellCloseMode.Hide);
            shell.Show();
            for (var i = 0; i < 700; i++)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            using var shot = new Bitmap(shell.Width, shell.Height);
            using (var g = Graphics.FromImage(shot))
            {
                g.CopyFromScreen(shell.Left, shell.Top, 0, 0, shell.Size);
            }

            shot.Save(Path.Combine(outputDir, "shell.png"), ImageFormat.Png);
            Console.WriteLine("saved shell.png");
            shell.ForceClose();
            return;
        }

        if (args.Contains("--show-menu"))
        {
            var liveState = new MenuState(true, false, true, false, true, false, true, true, true, AutoStartMode.FullAuto);
            using var live = new TrayMenuForm(liveState, _ => { });
            Cursor.Position = new Point(700, 500);
            live.ShowAtCursor();
            for (var i = 0; i < 40; i++)
            {
                Application.DoEvents();
                Thread.Sleep(15);
            }

            using var shot = new Bitmap(live.Width, live.Height);
            using (var g = Graphics.FromImage(shot))
            {
                g.CopyFromScreen(live.Left, live.Top, 0, 0, live.Size);
            }

            shot.Save(Path.Combine(outputDir, "menu-live.png"), ImageFormat.Png);
            Console.WriteLine("saved menu-live.png");
            live.Close();
            return;
        }

        var menuState = new MenuState(
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
        using (var menu = new TrayMenuForm(menuState, _ => { }))
        {
            using var bitmap = menu.Snapshot();
            bitmap?.Save(Path.Combine(outputDir, "menu.png"), ImageFormat.Png);
            Console.WriteLine("saved menu.png");
        }

        // 停止态：第一格应该是「启动」且可点
        var stoppedState = new MenuState(
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

        using (var menu = new TrayMenuForm(stoppedState, _ => { }))
        {
            using var bitmap = menu.Snapshot();
            bitmap?.Save(Path.Combine(outputDir, "menu-stopped.png"), ImageFormat.Png);
            Console.WriteLine("saved menu-stopped.png");
        }

        form.Close();
        Application.ExitThread();
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
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

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

    private static void Save(WidgetForm form, StatusSnapshot snapshot, string path)
    {
        form.SetStatus(snapshot);
        form.Refresh();
        Application.DoEvents();

        using var bitmap = form.Snapshot();
        bitmap?.Save(path, ImageFormat.Png);
        Console.WriteLine($"saved {path}");
    }
}

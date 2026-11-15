using System.Windows.Forms;

namespace DshOrbit;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 安装器写进「应用和功能」的卸载命令指向这里。
        if (HasFlag(args, "--uninstall"))
        {
            Uninstaller.Run(args);
            return;
        }

        var wantsShowWindow = HasFlag(args, "--show-window") || HasFlag(args, "--open-web");
        var wantsFullAuto = HasFlag(args, "--autostart-full");

        using var mutex = new Mutex(true, @"Local\DSH-Orbit.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已经有挂件在跑：按参数通知它，自己直接退出（不再弹对话框）。
            if (SingleInstance.TrySend(wantsShowWindow ? "show-window" : "show"))
            {
                return;
            }

            MessageBox.Show(
                "DSH-Orbit 已经在运行了。\n请在任务栏右下角的托盘区找它。",
                "DSH-Orbit",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Startup.Start();
        Startup.OpenWebOnStart = wantsShowWindow;
        Startup.AutoStartDsh = wantsFullAuto;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => AppPaths.Log($"未处理异常：{e.Exception}");

        var config = JsonIo.Load(AppPaths.ConfigFile, () => new WidgetConfig());
        ApplyOverrides(config, args);

        try
        {
            using var context = new WidgetApp(config);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"启动失败：{ex}");
            MessageBox.Show($"挂件启动失败：{ex.Message}\n\n日志：{AppPaths.LogFile}", "DSH-Orbit",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            IconFactory.ReleaseAll();
        }
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>调试/临时覆盖：--x 40 --y 60 --top --bottom --hidden --port 3080</summary>
    private static void ApplyOverrides(WidgetConfig config, string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--x" when i + 1 < args.Length && int.TryParse(args[i + 1], out var x):
                    config.X = x;
                    i++;
                    break;
                case "--y" when i + 1 < args.Length && int.TryParse(args[i + 1], out var y):
                    config.Y = y;
                    i++;
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], out var port):
                    config.PreferredPort = port;
                    i++;
                    break;
                case "--top":
                    config.AlwaysOnTop = true;
                    config.AlwaysBottom = false;
                    break;
                case "--bottom":
                    config.AlwaysBottom = true;
                    config.AlwaysOnTop = false;
                    break;
                case "--hidden":
                    config.Visible = false;
                    break;
            }
        }

        config.AlwaysOnTop = config.AlwaysOnTop && !config.AlwaysBottom;
    }
}

using System.Drawing.Imaging;
using System.Windows.Forms;

namespace DshOrbit.Setup;

/// <summary>
/// 入口：解析命令行 → 静默安装 / 单窗口安装 / 开发用截图。
/// 退出码：0 成功，1 未预期错误，2 预检或用户输入错误，3 用户取消了 UAC。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var cli = CliOptions.Parse(args);
        SetupLog.Init(cli.LogPath);

        if (cli.Errors.Count > 0)
        {
            foreach (var error in cli.Errors)
            {
                SetupLog.Error(error);
            }

            if (cli.Silent)
            {
                Environment.ExitCode = 2;
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(cli.ShotPath))
        {
            RunShot(cli);
            return;
        }

        if (cli.Silent)
        {
            Environment.ExitCode = RunSilent(cli);
            return;
        }

        ApplicationConfiguration.Initialize();
        Fluent.Init(AppTheme.System);

        try
        {
            Application.Run(new SetupForm(cli));
        }
        catch (Exception ex)
        {
            SetupLog.Error("安装程序异常退出", ex);
            MessageBox.Show(
                $"安装程序异常退出：{ex.Message}\n\n日志：{SetupLog.DefaultFile}",
                "DSH-Orbit 安装程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    // ---------------------------------------------------------------- 静默安装

    private static int RunSilent(CliOptions cli)
    {
        try
        {
            var options = InstallOptions.FromCli(cli);
            SetupLog.Info(
                $"静默安装：模式={options.Mode}，目录={options.TargetDir}，"
                + $"桌面快捷方式={options.DesktopShortcut}，开始菜单={options.StartMenuShortcut}");

            if (options.Mode == InstallMode.Machine && !InstallerSession.IsAdmin())
            {
                SetupLog.Info("当前不是管理员，请求提权后继续静默安装。");
                var code = InstallerSession.RelaunchElevated(options, wait: true);
                if (code is null)
                {
                    SetupLog.Error("用户取消了管理员授权（UAC），静默安装未完成。");
                    return 3;
                }

                SetupLog.Info($"提权安装结束，退出码 {code}。");
                return code.Value;
            }

            var result = new InstallEngine(options).Run();
            foreach (var warning in result.Warnings)
            {
                SetupLog.Warn(warning);
            }

            SetupLog.Info($"静默安装成功：{result.ExePath}（版本 {result.Version}）");
            return 0;
        }
        catch (SetupException ex)
        {
            SetupLog.Error($"静默安装失败：{ex.Message}", ex);
            return 2;
        }
        catch (Exception ex)
        {
            SetupLog.Error("静默安装失败（未预期错误）", ex);
            return 1;
        }
    }

    // ---------------------------------------------------------------- 截图（开发用）

    private static void RunShot(CliOptions cli)
    {
        ApplicationConfiguration.Initialize();
        Fluent.Init(cli.ShotTheme ?? AppTheme.System);

        try
        {
            using var form = new SetupForm(cli);
            form.ApplyShotState(cli.ShotState ?? "idle");
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-8000, -8000);
            form.ShowInTaskbar = false;
            form.Show();
            Pump(200);
            form.Refresh();

            // 窗口是无边框 + 自绘标题栏，DrawToBitmap 截出来的就是完整外观（含标题栏）。
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));

            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(cli.ShotPath!));
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            bitmap.Save(full, ImageFormat.Png);
            SetupLog.Info($"界面截图已保存：{full}");
            form.Close();
        }
        catch (Exception ex)
        {
            SetupLog.Error("界面截图失败", ex);
            Environment.ExitCode = 1;
        }
    }

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}

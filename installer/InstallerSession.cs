using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace DshOrbit.Setup;

/// <summary>需要和 Windows 会话打交道的杂事：判断管理员、提权、结束正在运行的主程序。</summary>
internal static class InstallerSession
{
    /// <summary>当前进程是否已提权（在管理员组 + 提升令牌）。</summary>
    public static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 用 runas 重启自己（清单是 asInvoker，所以提权必须靠这一下）。
    /// 返回 null 表示用户取消了 UAC；否则返回子进程退出码（wait=false 时固定 0）。
    /// </summary>
    public static int? RelaunchElevated(InstallOptions options, bool wait)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            SetupLog.Error("无法定位安装程序自身路径，无法请求管理员权限。");
            return null;
        }

        var arguments = CliOptions.BuildElevatedArguments(options);
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
        };

        try
        {
            using var child = Process.Start(startInfo);
            SetupLog.Info($"已请求管理员权限重启安装程序：{exe} {arguments}");
            if (child is null)
            {
                SetupLog.Error("无法启动提权后的安装程序（Process.Start 返回空）。");
                return null;
            }

            if (!wait)
            {
                return 0;
            }

            child.WaitForExit();
            SetupLog.Info($"提权后的安装程序已退出，退出码 {child.ExitCode}。");
            return child.ExitCode;
        }
        catch (Win32Exception ex)
        {
            // 用户点了 UAC 的「否」是 Win32Exception 1223。
            SetupLog.Warn($"提权被取消或失败（{ex.NativeErrorCode}）：{ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            SetupLog.Error("提权重启失败", ex);
            return null;
        }
    }

    /// <summary>目标 exe 是否被占用（运行中的 exe 映像不允许以读写方式独占打开）。</summary>
    public static bool IsFileLocked(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>当前是否有 DSH-Orbit 在运行（只判断进程名，不读路径，避免权限问题）。</summary>
    public static bool HasOrbitProcesses()
    {
        var processes = Process.GetProcessesByName("DSH-Orbit");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// 结束正在运行的 DSH-Orbit。传入 expectedPath 时只杀从该目录启动的实例；
    /// 传 null 表示「不管路径，按进程名全杀」（目标仍被占用时的兜底）。
    /// </summary>
    public static int KillOrbitProcesses(string? expectedPath)
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("DSH-Orbit"))
        {
            using (process)
            {
                try
                {
                    string? file = null;
                    try
                    {
                        file = process.MainModule?.FileName;
                    }
                    catch
                    {
                        // 不同位数 / 权限不足时读不到路径，按「未知」处理。
                    }

                    if (expectedPath is not null && file is not null
                        && !string.Equals(file, expectedPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SetupLog.Info($"结束正在运行的 DSH-Orbit：pid={process.Id}，路径={file ?? "(未知)"}");
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex)
                {
                    SetupLog.Warn($"无法结束进程 pid={process.Id}：{ex.Message}");
                }
            }
        }

        return killed;
    }
}

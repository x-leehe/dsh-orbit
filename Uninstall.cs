using System.Diagnostics;
using System.Drawing;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DshOrbit;

/// <summary>
/// 卸载：删快捷方式、注册表项（应用和功能 / RegisteredApplications）和程序文件。
/// 「应用和功能」里的 UninstallString 就指向 <c>DSH-Orbit.exe --uninstall</c>。
/// </summary>
internal static class Uninstaller
{
    private const string AppName = "DSH-Orbit";
    private const string UninstallSubKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppName;
    private const string CapabilitiesSubKey = @"Software\" + AppName + @"\Capabilities";
    private const string ParentSubKey = @"Software\" + AppName;

    private static string LogFile => Path.Combine(Path.GetTempPath(), "DSH-Orbit-Uninstall.log");

    public static void Run(string[] args)
    {
        var silent = Has(args, "/S") || Has(args, "--silent");
        var elevated = Has(args, "--elevated");
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        Log($"开始卸载 dir={dir} silent={silent} elevated={elevated}");

        // 装在 Program Files 或写在 HKLM 时，需要管理员权限才能删干净。
        if (!elevated && !IsElevated() && NeedsElevation(dir))
        {
            if (RelaunchElevated(silent))
            {
                return;
            }

            Log("提权被拒绝，继续以当前权限卸载（可能残留部分文件）");
        }

        // 默认保留用户数据：只有交互式勾选或显式 /REMOVEDATA 才删。
        var removeData = Has(args, "/REMOVEDATA") || Has(args, "--remove-data");
        if (!silent)
        {
            using var dialog = new UninstallDialog(dir);
            if (dialog.ShowDialog() != DialogResult.OK)
            {
                Log("用户取消");
                return;
            }

            removeData = dialog.RemoveUserData;
        }

        StopInstances();
        RemoveShortcuts();
        RemoveRegistry();
        if (removeData)
        {
            RemoveData(dir);
        }

        ScheduleSelfDelete(dir);
        Log("卸载完成");
    }

    private static bool Has(string[] args, string flag) =>
        args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

    private static bool IsElevated()
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

    /// <summary>Program Files 下的目录或 HKLM 里有卸载项，就认为需要管理员。</summary>
    private static bool NeedsElevation(string dir)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (dir.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using var key = Registry.LocalMachine.OpenSubKey(UninstallSubKey);
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool RelaunchElevated(bool silent)
    {
        try
        {
            var exe = Application.ExecutablePath;
            var arguments = silent ? "--uninstall --elevated /S" : "--uninstall --elevated";
            var info = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(info);
            return true;
        }
        catch (Exception ex)
        {
            Log($"提权失败：{ex.Message}");
            return false;
        }
    }

    private static void StopInstances()
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            try
            {
                if (process.Id != Environment.ProcessId)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    Log($"结束进程 {process.Id}");
                }
            }
            catch (Exception ex)
            {
                Log($"结束进程失败：{ex.Message}");
            }
        }
    }

    private static void RemoveShortcuts()
    {
        var desktops = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var desktop in desktops.Where(d => !string.IsNullOrEmpty(d)))
        {
            Delete(Path.Combine(desktop, AppName + ".lnk"));
        }

        var programFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };

        foreach (var programs in programFolders.Where(p => !string.IsNullOrEmpty(p)))
        {
            var folder = Path.Combine(programs, AppName);
            if (Directory.Exists(folder))
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    Log($"删除开始菜单目录 {folder}");
                }
                catch (Exception ex)
                {
                    Log($"删除开始菜单失败：{ex.Message}");
                }
            }
        }
    }

    private static void RemoveRegistry()
    {
        DeleteKey(Registry.LocalMachine, UninstallSubKey);
        DeleteKey(Registry.CurrentUser, UninstallSubKey);
        DeleteKey(Registry.LocalMachine, CapabilitiesSubKey);
        DeleteKey(Registry.CurrentUser, CapabilitiesSubKey);
        DeleteKey(Registry.LocalMachine, ParentSubKey);
        DeleteKey(Registry.CurrentUser, ParentSubKey);
        RemoveRegisteredApplication(Registry.LocalMachine);
        RemoveRegisteredApplication(Registry.CurrentUser);
    }

    private static void DeleteKey(RegistryKey root, string path)
    {
        try
        {
            // 先在只读句柄里看清楚，关掉句柄再删（句柄开着时 DeleteSubKeyTree 会失败）。
            bool exists;
            bool hasChildren;
            using (var key = root.OpenSubKey(path))
            {
                exists = key is not null;
                hasChildren = key is not null && (key.SubKeyCount > 0 || key.ValueCount > 0);
            }

            if (!exists)
            {
                return;
            }

            // 父键只在空的时候删，避免误伤同名前缀下的其他配置。
            if (string.Equals(path, ParentSubKey, StringComparison.OrdinalIgnoreCase) && hasChildren)
            {
                Log($"保留仍有内容的 {path}");
                return;
            }

            root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            Log($"删除注册表项 {root.Name}\\{path}");
        }
        catch (Exception ex)
        {
            Log($"删除注册表项失败 {path}：{ex.Message}");
        }
    }

    private static void RemoveRegisteredApplication(RegistryKey root)
    {
        try
        {
            using var key = root.OpenSubKey(@"Software\RegisteredApplications");
            if (key?.GetValue(AppName) is null)
            {
                return;
            }

            using var writable = root.OpenSubKey(@"Software\RegisteredApplications", writable: true);
            writable?.DeleteValue(AppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log($"清理 RegisteredApplications 失败：{ex.Message}");
        }
    }

    private static void RemoveData(string dir)
    {
        // 护栏：数据目录不能就是安装目录本身，也不能是它的祖先（防止写错路径把整个 exe 目录端了）。
        static bool Safe(string candidate, string installDir) =>
            !string.Equals(candidate.TrimEnd('\\'), installDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) &&
            !installDir.StartsWith(candidate.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        var portableData = Path.Combine(dir, "data");
        var paths = new List<string>();
        if (Directory.Exists(portableData))
        {
            paths.Add(portableData);
        }
        else
        {
            paths.Add(AppPaths.Root);
            paths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName));
            paths.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshStatusWidget"));
        }

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            if (!Safe(path, dir))
            {
                Log($"跳过不安全的数据目录 {path}");
                continue;
            }

            try
            {
                Directory.Delete(path, recursive: true);
                Log($"删除数据目录 {path}");
            }
            catch (Exception ex)
            {
                Log($"删除数据目录失败 {path}：{ex.Message}");
            }
        }
    }

    /// <summary>删掉正在运行的 exe 和目录：交给一个延迟的 cmd。</summary>
    private static void ScheduleSelfDelete(string dir)
    {
        try
        {
            var exe = Application.ExecutablePath;
            if (!string.Equals(Path.GetFileName(exe), AppName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(exe)?.TrimEnd('\\'), dir, StringComparison.OrdinalIgnoreCase))
            {
                Log($"跳过自删除（exe={exe} dir={dir}）");
                return;
            }

            var script = $"ping -n 3 127.0.0.1 >nul & del /f /q \"{exe}\" & rmdir /q \"{dir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Log($"安排自删除失败：{ex.Message}");
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Log($"删除 {path}");
            }
        }
        catch (Exception ex)
        {
            Log($"删除失败 {path}：{ex.Message}");
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响卸载。
        }
    }
}

/// <summary>卸载确认框：Fluent 卡片风格，可选是否连配置一起删。</summary>
internal sealed class UninstallDialog : Form
{
    private readonly CheckBox _removeData;

    public UninstallDialog(string installDir)
    {
        Text = "卸载 DSH-Orbit";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Fluent.Layer;
        ForeColor = Fluent.TextPrimary;
        DoubleBuffered = true;
        KeyPreview = true;

        var scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * scale);
        ClientSize = new Size(S(440), S(238));

        _removeData = new CheckBox
        {
            Text = "同时删除配置与访问历史（默认保留）",
            Checked = false,
            FlatStyle = FlatStyle.Flat,
            Font = Fluent.Font(TextRole.Body, false, scale),
            ForeColor = Fluent.TextSecondary,
            BackColor = Fluent.Layer,
            Bounds = new Rectangle(S(24), S(140), S(390), S(24)),
        };

        var cancel = new FluentButton
        {
            Text = "取消",
            Bounds = new Rectangle(S(232), S(180), S(88), S(32)),
            Font = Fluent.Font(TextRole.Body, false, scale),
        };

        var confirm = new FluentButton
        {
            Text = "卸载",
            Role = SurfaceRole.Danger,
            Bounds = new Rectangle(S(328), S(180), S(88), S(32)),
            Font = Fluent.Font(TextRole.Body, false, scale),
        };

        cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        confirm.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        Controls.Add(_removeData);
        Controls.Add(cancel);
        Controls.Add(confirm);

        _installDir = installDir;
    }

    private readonly string _installDir;

    public bool RemoveUserData => _removeData.Checked;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.EnableRoundedCorners(Handle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);

        var scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * scale);

        Fluent.Card(g, new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1),
            Fluent.RCard, 0, Fluent.Layer);

        Fluent.Text(g, "卸载 DSH-Orbit", TextRole.Subtitle, true, Fluent.TextPrimary,
            new RectangleF(S(24), S(20), S(360), S(28)), StringAlignment.Near, StringTrimming.None, scale);

        Fluent.Text(g, "将删除快捷方式、「应用和功能」条目，然后移除程序文件：",
            TextRole.Body, false, Fluent.TextSecondary,
            new RectangleF(S(24), S(56), S(392), S(20)), StringAlignment.Near, StringTrimming.None, scale);

        Fluent.Text(g, _installDir, TextRole.Caption, false, Fluent.TextTertiary,
            new RectangleF(S(24), S(78), S(392), S(16)), StringAlignment.Near, StringTrimming.EllipsisCharacter, scale);

        Fluent.Text(g, "配置目录：%LOCALAPPDATA%\\DSH-Orbit", TextRole.Caption, false, Fluent.TextTertiary,
            new RectangleF(S(24), S(112), S(392), S(16)), StringAlignment.Near, StringTrimming.None, scale);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT &&
            Cursor.Position.Y - Top < (int)(72 * DeviceDpi / 96f))
        {
            m.Result = HTCAPTION;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        else if (e.KeyCode == Keys.Enter)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}

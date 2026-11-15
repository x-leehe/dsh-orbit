using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DshOrbit.Setup;

/// <summary>
/// 快捷方式、注册表、便携标记。全部「写到系统里」的动作集中在这里，方便回滚与审查。
/// 快捷方式用 COM 的 WScript.Shell 创建（不引第三方包）。
/// </summary>
internal static class ShellIntegration
{
    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DSH-Orbit";
    private const string CapabilitiesKeyPath = @"Software\DSH-Orbit\Capabilities";
    private const string AppKeyPath = @"Software\DSH-Orbit";
    private const string RegisteredApplicationsPath = @"Software\RegisteredApplications";

    // ---------------------------------------------------------------- 路径

    public static string DesktopShortcutPath(InstallMode mode) =>
        Path.Combine(
            mode == InstallMode.Machine
                ? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
                : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AppInfo.ProductName + ".lnk");

    public static string StartMenuShortcutPath(InstallMode mode)
    {
        var root = mode == InstallMode.Machine
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            : Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);

        return Path.Combine(root, "Programs", AppInfo.ProductName, AppInfo.ProductName + ".lnk");
    }

    // ---------------------------------------------------------------- 快捷方式

    /// <summary>创建 .lnk；失败抛 <see cref="SetupException"/>（调用方按 warning 处理）。</summary>
    public static void CreateShortcut(string shortcutPath, string targetPath, string workingDirectory)
    {
        var directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new SetupException("系统里找不到 WScript.Shell（Windows Script Host 可能被禁用），无法创建快捷方式。");

        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new SetupException("无法创建 WScript.Shell 实例。");

            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: new object[] { shortcutPath })
                ?? throw new SetupException($"无法创建快捷方式：{shortcutPath}");

            var shortcutType = shortcut.GetType();
            SetProperty(shortcutType, shortcut, "TargetPath", targetPath);
            SetProperty(shortcutType, shortcut, "WorkingDirectory", workingDirectory);
            SetProperty(shortcutType, shortcut, "IconLocation", targetPath + ",0");
            SetProperty(shortcutType, shortcut, "Description", AppInfo.ProductName);

            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, binder: null, target: shortcut, args: null);
            SetupLog.Info($"已创建快捷方式：{shortcutPath} -> {targetPath}");
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SetupException($"创建快捷方式失败：{shortcutPath}\n{ex.Message}", ex);
        }
        finally
        {
            ReleaseCom(shortcut);
            ReleaseCom(shell);
        }
    }

    public static void DeleteShortcut(string shortcutPath)
    {
        try
        {
            if (File.Exists(shortcutPath))
            {
                File.Delete(shortcutPath);
                SetupLog.Info($"已删除快捷方式：{shortcutPath}");
            }

            // 开始菜单里我们自己建的目录，空了就顺手删掉。
            var directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrEmpty(directory)
                && string.Equals(Path.GetFileName(directory), AppInfo.ProductName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(directory)
                && Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"删除快捷方式失败（{shortcutPath}）：{ex.Message}");
        }
    }

    private static void SetProperty(Type type, object target, string name, string value) =>
        type.InvokeMember(name, BindingFlags.SetProperty, binder: null, target: target, args: new object[] { value });

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch
            {
                // 释放失败无伤大雅，进程马上退出。
            }
        }
    }

    // ---------------------------------------------------------------- 注册表

    /// <summary>写「应用和功能」与 RegisteredApplications / Capabilities。非便携模式才会调用。</summary>
    public static void WriteRegistry(InstallOptions options, string version, long sizeBytes)
    {
        var root = options.Mode == InstallMode.Machine ? Registry.LocalMachine : Registry.CurrentUser;
        var hive = options.Mode == InstallMode.Machine ? "HKLM" : "HKCU";

        try
        {
            using (var uninstall = root.CreateSubKey(UninstallKeyPath, writable: true)
                ?? throw new SetupException($"无法创建注册表项：{hive}\\{UninstallKeyPath}"))
            {
                uninstall.SetValue("DisplayName", AppInfo.ProductName, RegistryValueKind.String);
                uninstall.SetValue("DisplayVersion", version, RegistryValueKind.String);
                uninstall.SetValue("Publisher", AppInfo.Publisher, RegistryValueKind.String);
                uninstall.SetValue("DisplayIcon", Quote(options.ExePath), RegistryValueKind.String);
                uninstall.SetValue("InstallLocation", options.TargetDir, RegistryValueKind.String);
                uninstall.SetValue("EstimatedSize", (int)Math.Ceiling(sizeBytes / 1024.0), RegistryValueKind.DWord);
                uninstall.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
                uninstall.SetValue("URLInfoAbout", AppInfo.ProjectUrl, RegistryValueKind.String);
                uninstall.SetValue("UninstallString", Quote(options.ExePath) + " --uninstall", RegistryValueKind.String);
                uninstall.SetValue("QuietUninstallString", Quote(options.ExePath) + " --uninstall /S", RegistryValueKind.String);
                uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
                uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            using (var capabilities = root.CreateSubKey(CapabilitiesKeyPath, writable: true)
                ?? throw new SetupException($"无法创建注册表项：{hive}\\{CapabilitiesKeyPath}"))
            {
                capabilities.SetValue("ApplicationName", AppInfo.ProductName, RegistryValueKind.String);
                capabilities.SetValue("ApplicationDescription", AppInfo.Description, RegistryValueKind.String);
                capabilities.SetValue("ApplicationIcon", Quote(options.ExePath) + ",0", RegistryValueKind.String);
            }

            using (var registered = root.CreateSubKey(RegisteredApplicationsPath, writable: true)
                ?? throw new SetupException($"无法创建注册表项：{hive}\\{RegisteredApplicationsPath}"))
            {
                registered.SetValue(AppInfo.ProductName, AppKeyPath + @"\Capabilities", RegistryValueKind.String);
            }

            SetupLog.Info($"已写入注册表（{hive}）：{UninstallKeyPath}、{CapabilitiesKeyPath}、{RegisteredApplicationsPath}。");
        }
        catch (SetupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new SetupException($"写入注册表失败（{hive}）：{ex.Message}", ex);
        }
    }

    /// <summary>回滚 / 卸载用：删掉安装器写的三项注册表。</summary>
    public static void RemoveRegistry(InstallMode mode)
    {
        var root = mode == InstallMode.Machine ? Registry.LocalMachine : Registry.CurrentUser;

        try
        {
            root.DeleteSubKeyTree(UninstallKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"删除注册表 {UninstallKeyPath} 失败：{ex.Message}");
        }

        try
        {
            root.DeleteSubKeyTree(AppKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"删除注册表 {AppKeyPath} 失败：{ex.Message}");
        }

        try
        {
            using var registered = root.OpenSubKey(RegisteredApplicationsPath, writable: true);
            registered?.DeleteValue(AppInfo.ProductName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            SetupLog.Warn($"删除 RegisteredApplications 里的 DSH-Orbit 失败：{ex.Message}");
        }
    }

    /// <summary>写注册表前的可写性探测：先建一个临时项再删掉，失败时能给出人话错误。</summary>
    public static void ProbeRegistry(InstallMode mode)
    {
        var root = mode == InstallMode.Machine ? Registry.LocalMachine : Registry.CurrentUser;
        var hive = mode == InstallMode.Machine ? "HKLM" : "HKCU";
        const string probePath = @"Software\.DSH-Orbit-Setup-Probe";

        try
        {
            using (var probe = root.CreateSubKey(probePath, writable: true)
                ?? throw new SetupException($"无法创建注册表项：{hive}\\{probePath}"))
            {
                probe.SetValue("Probe", 1, RegistryValueKind.DWord);
            }

            root.DeleteSubKey(probePath, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            throw new SetupException(
                $"没有权限写入注册表（{hive}）。\n\n请改用「仅为我安装」或便携模式。", ex);
        }
    }

    // ---------------------------------------------------------------- 便携模式

    /// <summary>便携模式：只写 portable.txt + 空的 data\ 目录（主程序据此把数据放在 exe 旁）。</summary>
    public static void WritePortableMarker(InstallOptions options)
    {
        try
        {
            Directory.CreateDirectory(options.DataDir);
            var content = "便携模式：DSH-Orbit 的数据保存在本目录的 data 子目录里（删除本文件即恢复默认数据位置）。"
                          + Environment.NewLine;
            File.WriteAllText(options.PortableMarkerPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            SetupLog.Info($"已写入便携标记：{options.PortableMarkerPath}，数据目录：{options.DataDir}");
        }
        catch (Exception ex)
        {
            throw new SetupException($"无法写入便携模式标记文件：{options.PortableMarkerPath}\n{ex.Message}", ex);
        }
    }

    // ---------------------------------------------------------------- 工具

    private static string Quote(string path) => "\"" + path + "\"";
}

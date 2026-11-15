using System.Reflection;

namespace DshOrbit.Setup;

/// <summary>安装范围。</summary>
internal enum InstallMode
{
    /// <summary>仅为我安装：%LOCALAPPDATA%\Programs，不需要管理员。</summary>
    User,

    /// <summary>为所有用户安装：Program Files，需要管理员。</summary>
    Machine,

    /// <summary>便携模式：只解压，不写注册表、不建快捷方式。</summary>
    Portable,
}

/// <summary>产品级常量：注册表、快捷方式、日志都从这里取，避免散落字符串。</summary>
internal static class AppInfo
{
    public const string ProductName = "DSH-Orbit";
    public const string ExeName = "DSH-Orbit.exe";
    public const string Publisher = "x-leehe";
    public const string ProjectUrl = "https://github.com/x-leehe/dsh-orbit";
    public const string Description = "给 dsh web 用的桌面挂件与浏览器外壳";

    /// <summary>payload 的 FileVersion 取不到时用它（对应 DshOrbit.csproj 的 Version）。</summary>
    public const string FallbackVersion = "0.2.0";

    public const string PayloadResourceName = "payload.app";

    /// <summary>安装程序自己的版本，显示在界面右上角。</summary>
    public static string Version { get; } = FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

    public static string FormatVersion(Version? version) =>
        version is null
            ? FallbackVersion
            : version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}";
}

/// <summary>一次安装的全部输入。UI 与静默模式共用。</summary>
internal sealed class InstallOptions
{
    public required InstallMode Mode { get; init; }

    /// <summary>已规范化的绝对路径。</summary>
    public required string TargetDir { get; init; }

    public bool DesktopShortcut { get; init; }

    public bool StartMenuShortcut { get; init; }

    public bool Silent { get; init; }

    public string? LogPath { get; init; }

    public bool Elevated { get; init; }

    public bool Portable => Mode == InstallMode.Portable;

    public string ExePath => Path.Combine(TargetDir, AppInfo.ExeName);

    public string TempExePath => ExePath + ".new";

    public string DataDir => Path.Combine(TargetDir, "data");

    public string PortableMarkerPath => Path.Combine(TargetDir, "portable.txt");

    /// <summary>命令行静默模式的输入。</summary>
    public static InstallOptions FromCli(CliOptions cli)
    {
        var mode = cli.ModeExplicit
            ? cli.Mode
            : InstallerSession.IsAdmin() ? InstallMode.Machine : InstallMode.User;

        var dir = string.IsNullOrWhiteSpace(cli.TargetDir)
            ? DefaultDirectory(mode)
            : NormalizeDirectory(cli.TargetDir!);

        return new InstallOptions
        {
            Mode = mode,
            TargetDir = dir,
            DesktopShortcut = !cli.NoDesktop && mode != InstallMode.Portable,
            StartMenuShortcut = !cli.NoStart && mode != InstallMode.Portable,
            Silent = cli.Silent,
            LogPath = cli.LogPath,
            Elevated = cli.Elevated,
        };
    }

    /// <summary>各范围的默认目录（切换范围时界面用它自动填路径）。</summary>
    public static string DefaultDirectory(InstallMode mode)
    {
        switch (mode)
        {
            case InstallMode.Machine:
                // 老版本如果手工拷贝到 Program Files (x86)，默认沿用它，避免装出两份。
                var x86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), AppInfo.ProductName);
                return Directory.Exists(x86)
                    ? x86
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppInfo.ProductName);

            case InstallMode.Portable:
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "DSH-Orbit-Portable");

            default:
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs",
                    AppInfo.ProductName);
        }
    }

    /// <summary>
    /// 规范化用户输入的目录：展开环境变量、去引号、转绝对路径、去尾部分隔符。
    /// 不合法时抛 <see cref="SetupException"/>（消息可直接展示给用户）。
    /// </summary>
    public static string NormalizeDirectory(string path)
    {
        var value = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (value.Length == 0)
        {
            throw new SetupException("安装目录不能为空。");
        }

        if (!Path.IsPathRooted(value))
        {
            throw new SetupException($"安装目录必须是完整路径：\n{value}");
        }

        string full;
        try
        {
            full = Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            throw new SetupException($"安装目录不是合法的路径：\n{value}", ex);
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root is not null && string.Equals(trimmed, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupException("不能直接安装到磁盘根目录，请选择一个子目录。");
        }

        return trimmed;
    }
}

/// <summary>安装进度（百分数 + 状态文字），由引擎回调给界面。</summary>
internal readonly record struct InstallProgress(int Percent, string Status);

/// <summary>安装成功的结果。</summary>
internal sealed class InstallResult
{
    public required string Directory { get; init; }

    public required string ExePath { get; init; }

    public required string Version { get; init; }

    public bool Portable { get; init; }

    public List<string> Shortcuts { get; } = new();

    public List<string> Warnings { get; } = new();
}

/// <summary>可以直接展示给用户的安装错误（不打印堆栈，只给一句人话 + 建议）。</summary>
internal sealed class SetupException : Exception
{
    public SetupException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

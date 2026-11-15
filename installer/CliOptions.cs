namespace DshOrbit.Setup;

/// <summary>
/// 命令行参数（给 CI / 自动化用）：
/// <code>
///   /S                      静默安装（无界面、无对话框；失败写日志并返回非 0）
///   /D=&lt;dir&gt;               安装目录，必须是最后一个参数（允许空格，不需要引号）
///   /MODE=machine|user|portable
///   /NODESKTOP              不创建桌面快捷方式
///   /NOSTART                不创建开始菜单项
///   /LOG=&lt;file&gt;             额外日志文件（默认 %TEMP%\DSH-Orbit-Setup.log）
///
///   --elevated              内部开关：提权后的实例（不再重复提权）
///   --shot &lt;png&gt;            开发用：把界面渲染成 PNG
///   --shot-state idle|progress|portable|machine|done|error
///   --shot-theme system|light|dark
/// </code>
/// </summary>
internal sealed class CliOptions
{
    public bool Silent { get; private set; }

    public bool Elevated { get; private set; }

    public bool ModeExplicit { get; private set; }

    public InstallMode Mode { get; private set; } = InstallMode.User;

    public string? TargetDir { get; private set; }

    public bool NoDesktop { get; private set; }

    public bool NoStart { get; private set; }

    public string? LogPath { get; private set; }

    public string? ShotPath { get; private set; }

    public string? ShotState { get; private set; }

    public AppTheme? ShotTheme { get; private set; }

    /// <summary>解析失败的参数说明（静默模式据此返回非 0）。</summary>
    public List<string> Errors { get; } = new();

    public static CliOptions Parse(string[] args)
    {
        var cli = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var arg = raw.Trim();
            var lower = arg.ToLowerInvariant();

            // /D= 必须是最后一个参数：它后面的一切（含空格、不带引号）都算路径。
            if (lower.StartsWith("/d=", StringComparison.Ordinal))
            {
                var rest = arg[3..];
                if (i + 1 < args.Length)
                {
                    rest = rest + " " + string.Join(' ', args, i + 1, args.Length - i - 1);
                }

                cli.TargetDir = rest.Trim().Trim('"').Trim();
                if (cli.TargetDir.Length == 0)
                {
                    cli.Errors.Add("/D= 后面缺少安装目录。");
                }

                break;
            }

            if (lower.StartsWith("/mode=", StringComparison.Ordinal))
            {
                cli.ModeExplicit = true;
                var value = arg[6..].Trim().Trim('"');
                switch (value.ToLowerInvariant())
                {
                    case "user":
                        cli.Mode = InstallMode.User;
                        break;
                    case "machine":
                        cli.Mode = InstallMode.Machine;
                        break;
                    case "portable":
                        cli.Mode = InstallMode.Portable;
                        break;
                    default:
                        cli.Errors.Add($"无法识别的 /MODE={value}（可选 user / machine / portable）。");
                        break;
                }

                continue;
            }

            if (lower.StartsWith("/log=", StringComparison.Ordinal))
            {
                var value = arg[5..].Trim().Trim('"');
                if (value.Length > 0)
                {
                    cli.LogPath = value;
                }
                else
                {
                    cli.Errors.Add("/LOG= 后面缺少日志文件路径。");
                }

                continue;
            }

            switch (lower)
            {
                case "/s":
                    cli.Silent = true;
                    break;

                case "/nodesktop":
                    cli.NoDesktop = true;
                    break;

                case "/nostart":
                    cli.NoStart = true;
                    break;

                case "--elevated":
                    cli.Elevated = true;
                    break;

                case "--shot":
                    if (i + 1 < args.Length)
                    {
                        cli.ShotPath = args[++i];
                    }
                    else
                    {
                        cli.Errors.Add("--shot 后面缺少 PNG 输出路径。");
                    }

                    break;

                case "--shot-state":
                    if (i + 1 < args.Length)
                    {
                        cli.ShotState = args[++i].ToLowerInvariant();
                    }

                    break;

                case "--shot-theme":
                    if (i + 1 < args.Length)
                    {
                        cli.ShotTheme = args[++i].ToLowerInvariant() switch
                        {
                            "light" => AppTheme.Light,
                            "dark" => AppTheme.Dark,
                            _ => AppTheme.System,
                        };
                    }

                    break;
            }
        }

        return cli;
    }

    /// <summary>
    /// 提权实例的参数：原参数（/S、/LOG） + 界面上已经选好的范围、目录、快捷方式。
    /// /D= 仍然放在最后，保证「路径可以带空格」的规则一致。
    /// </summary>
    public static string BuildElevatedArguments(InstallOptions options)
    {
        var parts = new List<string> { "--elevated" };

        if (options.Silent)
        {
            parts.Add("/S");
        }

        parts.Add(options.Mode switch
        {
            InstallMode.Machine => "/MODE=machine",
            InstallMode.Portable => "/MODE=portable",
            _ => "/MODE=user",
        });

        if (!options.DesktopShortcut)
        {
            parts.Add("/NODESKTOP");
        }

        if (!options.StartMenuShortcut)
        {
            parts.Add("/NOSTART");
        }

        if (!string.IsNullOrEmpty(options.LogPath))
        {
            parts.Add("/LOG=" + Quote(options.LogPath!));
        }

        parts.Add("/D=" + Quote(options.TargetDir));
        return string.Join(' ', parts);
    }

    private static string Quote(string value) =>
        value.Contains(' ') && !value.StartsWith('"') ? "\"" + value + "\"" : value;
}

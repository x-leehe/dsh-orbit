using System.Drawing;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DshOrbit;

internal enum DshState
{
    Stopped,
    Starting,
    Running,
    Error,
}

/// <summary>一次界面刷新所需的全部状态。</summary>
internal sealed class StatusSnapshot
{
    public DshState State { get; init; } = DshState.Stopped;
    public string Profile { get; init; } = "web";
    public string Version { get; init; } = "";
    public int? Port { get; init; }
    public string DisplayUrl { get; init; } = "";
    public bool HasAuthenticatedUrl { get; init; }
    public TimeSpan? Uptime { get; init; }
    public long MemoryBytes { get; init; }
    public double? CpuPercent { get; init; }
    public int? PluginTotal { get; init; }
    public int? PluginDisabled { get; init; }
    public string PluginNames { get; init; } = "";
    public bool PluginScanning { get; init; }
    public decimal? Balance { get; init; }
    public decimal? SpentToday { get; init; }
    public bool OffPeak { get; init; }

    /// <summary>
    /// 距离下一次计价时段切换的时长：闲时中 = 闲时剩余，标准价 = 距闲时开始。
    /// 由本地时钟推导（北京 00:30–08:30 闲时），拿不到时为 null。
    /// </summary>
    public TimeSpan? UntilPriceSwitch { get; init; }

    /// <summary>
    /// 有 turn 在飞的会话数（近似值，来自 session_projcache 投影缓存）；
    /// 无法判定时为 null，UI 显示「—」。
    /// </summary>
    public int? ActiveSessions { get; init; }

    public string? Hint { get; init; }

    /// <summary>状态色：直接取 Fluent 令牌，跟随浅/深主题。</summary>
    public Color Accent => State switch
    {
        DshState.Running => Fluent.Success,
        DshState.Starting => Fluent.Caution,
        DshState.Error => Fluent.Critical,
        _ => Fluent.TextTertiary,
    };

    public string StateText => State switch
    {
        DshState.Running => "运行中",
        DshState.Starting => "启动中",
        DshState.Error => "异常",
        _ => "已停止",
    };
}

/// <summary>挂件配置，保存在 %LOCALAPPDATA%\DSH-Orbit\config.json。</summary>
internal sealed class WidgetConfig
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;

    /// <summary>贴到所有普通窗口之下（桌面挂件模式）。</summary>
    public bool AlwaysBottom { get; set; } = true;

    /// <summary>始终置顶（与 AlwaysBottom 互斥）。</summary>
    public bool AlwaysOnTop { get; set; }

    public double Opacity { get; set; } = 1.0;

    public string Profile { get; set; } = "web";

    /// <summary>优先附着的端口（默认 3080，即 DSH Web 的惯用端口）。</summary>
    public int PreferredPort { get; set; } = 3080;

    /// <summary>dsh 可执行文件路径；留空则自动从 PATH / ~\.bun\bin 查找。</summary>
    public string? DshExePath { get; set; }

    /// <summary>启动 dsh 时的工作目录；留空用用户主目录。</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>启动参数，默认与 `dsh web --no-open` 等价。</summary>
    public string LaunchArguments { get; set; } = "web --no-open";

    public bool ShowBalance { get; set; } = true;

    /// <summary>可选：直接指定 DeepSeek API Key；留空则读环境变量与 ~\.dsh\.credentials.yaml。</summary>
    public string? DeepSeekApiKey { get; set; }

    /// <summary>鼠标穿透（锁定）状态。</summary>
    public bool ClickThrough { get; set; }

    /// <summary>是否显示挂件窗口（托盘可再次唤出）。</summary>
    public bool Visible { get; set; } = true;

    /// <summary>贴边隐藏：拖到屏幕左右边缘时收起，鼠标移到边缘热区再滑出。</summary>
    public bool EdgeHideEnabled { get; set; } = true;

    /// <summary>前台有全屏窗口（含无边框全屏）时自动隐藏挂件。</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>当前吸附的边（None / Left / Right）。</summary>
    public DockSide DockSide { get; set; } = DockSide.None;

    /// <summary>开机启动方式：停用 / 启用（只起挂件）/ 全自动（挂件 + dsh web）。</summary>
    public AutoStartMode AutoStartMode { get; set; } = AutoStartMode.Disabled;

    /// <summary>外观主题：跟随系统 / 浅色 / 深色。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>点地址时用内置 WebView2 外壳打开（false 则用默认浏览器）。</summary>
    public bool UseWebShell { get; set; } = true;

    /// <summary>外壳窗口关闭时的行为：隐藏（WebView2 常驻）或真正关闭。</summary>
    public ShellCloseMode ShellClose { get; set; } = ShellCloseMode.Hide;

    public int StatusIntervalSeconds { get; set; } = 3;
    public int BalanceIntervalSeconds { get; set; } = 60;
    public int PluginIntervalSeconds { get; set; } = 300;
}

/// <summary>开机启动方式。</summary>
internal enum AutoStartMode
{
    Disabled,
    Enabled,
    FullAuto,
}

/// <summary>Web 外壳关闭时的行为。</summary>
internal enum ShellCloseMode
{
    Hide,
    Close,
}

/// <summary>挂件吸附在屏幕的哪一侧。</summary>
internal enum DockSide
{
    None,
    Left,
    Right,
}

/// <summary>当日用量记账（按余额下降累计）。</summary>
internal sealed class UsageRecord
{
    public string Date { get; set; } = "";
    public decimal Spent { get; set; }
    public decimal TopUp { get; set; }
    public decimal LastBalance { get; set; }
    public bool HasLastBalance { get; set; }
}

/// <summary>冷启动计时（从 Main 进入算起）。</summary>
internal static class Startup
{
    private static long _startTicks;

    /// <summary>启动后自动打开 Web 外壳（--show-window）。</summary>
    public static bool OpenWebOnStart { get; set; }

    /// <summary>启动后自动拉起 dsh web（开机自启「全自动」）。</summary>
    public static bool AutoStartDsh { get; set; }

    public static void Start() => _startTicks = Environment.TickCount64;

    public static long ElapsedMs => _startTicks == 0 ? 0 : Environment.TickCount64 - _startTicks;
}

internal static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    /// <summary>DSH-Orbit 的数据目录；首次升级时把旧的 DshStatusWidget 目录整体搬过来。</summary>
    private static string ResolveRoot()
    {
        // 便携模式（安装器写入 portable.txt 或 data 目录）：数据跟着 exe 走，不碰 %LOCALAPPDATA%。
        try
        {
            var exeDir = AppContext.BaseDirectory;
            if (File.Exists(Path.Combine(exeDir, "portable.txt")) ||
                Directory.Exists(Path.Combine(exeDir, "data")))
            {
                return Path.Combine(exeDir, "data");
            }
        }
        catch
        {
            // 取不到 exe 目录就退回常规路径。
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var target = Path.Combine(local, "DSH-Orbit");
        var legacy = Path.Combine(local, "DshStatusWidget");

        if (!Directory.Exists(target) && Directory.Exists(legacy))
        {
            try
            {
                Directory.Move(legacy, target);
            }
            catch
            {
                // 搬不动就沿用旧目录，至少不丢配置。
                return legacy;
            }
        }

        return target;
    }

    public static string ShellFile => Path.Combine(Root, "shell.json");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string UsageFile => Path.Combine(Root, "usage.json");
    public static string LogFile => Path.Combine(Root, "widget.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "logs"));
    }

    public static void Log(string message)
    {
        try
        {
            Ensure();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响挂件。
        }
    }
}

internal static class JsonIo
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static T Load<T>(string path, Func<T> fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                var text = File.ReadAllText(path);
                var value = JsonSerializer.Deserialize<T>(text, Options);
                if (value is not null)
                {
                    return value;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取 {path} 失败：{ex.Message}");
        }

        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            AppPaths.Ensure();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"写入 {path} 失败：{ex.Message}");
        }
    }
}

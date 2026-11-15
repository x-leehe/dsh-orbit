namespace DshOrbit.Setup;

/// <summary>
/// 安装日志：始终写 %TEMP%\DSH-Orbit-Setup.log；/LOG= 再额外写一份。
/// 静默安装失败时这里就是唯一的证据，所以连命令行、异常堆栈都记。
/// </summary>
internal static class SetupLog
{
    private static readonly object Gate = new();
    private static readonly List<string> Targets = new();
    private static bool _initialized;

    /// <summary>默认日志文件（/LOG 不改变它，只是额外再加一份）。</summary>
    public static string DefaultFile { get; } = Path.Combine(Path.GetTempPath(), "DSH-Orbit-Setup.log");

    public static string? ExtraFile { get; private set; }

    public static void Init(string? extraFile)
    {
        lock (Gate)
        {
            Targets.Clear();
            Targets.Add(DefaultFile);
            ExtraFile = null;

            if (!string.IsNullOrWhiteSpace(extraFile))
            {
                try
                {
                    var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(extraFile.Trim().Trim('"')));
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    Targets.Add(full);
                    ExtraFile = full;
                }
                catch (Exception ex)
                {
                    // 额外日志打不开不影响安装，只记进默认日志。
                    Append(DefaultFile, $"[{Now}] WARN  无法使用 /LOG 指定的日志文件：{ex.Message}");
                }
            }

            _initialized = true;
        }

        Info($"===== DSH-Orbit 安装程序 {AppInfo.Version} · {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        Info($"命令行：{Environment.CommandLine}");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception = null)
    {
        var text = message;
        if (exception is not null)
        {
            text += Environment.NewLine + exception;
        }

        var line = $"[{Now}] {level} {text}";
        lock (Gate)
        {
            if (!_initialized)
            {
                Targets.Add(DefaultFile);
                _initialized = true;
            }

            foreach (var path in Targets)
            {
                Append(path, line);
            }
        }
    }

    private static string Now => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

    private static void Append(string path, string line)
    {
        try
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // 日志写不进去也不能让安装失败（例如 %TEMP% 被策略禁止）。
        }
    }
}

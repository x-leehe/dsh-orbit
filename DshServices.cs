using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshOrbit;

/// <summary>发现到的一个 dsh web 实例（监听端口的真实拥有者是内部 node 进程）。</summary>
internal sealed record DshInstance(int Port, int ListenerPid, DateTime StartedAt);

/// <summary>扫描本机监听端口，找出真正在跑 dsh web 的实例。</summary>
internal sealed class DshDiscovery
{
    private static readonly HashSet<string> ServerProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "dsh", "bun",
    };

    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(2.5),
    };

    /// <summary>
    /// 扫描所有回环监听端口，按“3080 优先 → 配置端口 → 其余”的顺序探活。
    /// 命中返回实例；否则返回 null。
    /// </summary>
    public async Task<DshInstance?> FindAsync(int preferredPort, CancellationToken ct)
    {
        List<(int Port, int Pid)> rows;
        try
        {
            rows = Native.ListenLoopbackPorts();
        }
        catch
        {
            return null;
        }

        var candidates = new List<(int Port, int Pid)>();
        var seen = new HashSet<int>();
        foreach (var row in rows)
        {
            if (seen.Contains(row.Port) || !IsServerProcess(row.Pid))
            {
                continue;
            }

            seen.Add(row.Port);
            candidates.Add(row);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        candidates.Sort((a, b) => Rank(a.Port).CompareTo(Rank(b.Port)));

        foreach (var candidate in candidates.Take(10))
        {
            ct.ThrowIfCancellationRequested();
            if (await LooksLikeDshAsync(candidate.Port, ct).ConfigureAwait(false))
            {
                return new DshInstance(candidate.Port, candidate.Pid, StartTimeOf(candidate.Pid));
            }
        }

        return null;

        int Rank(int port) => port == 3080 ? 0 : port == preferredPort ? 1 : 2;
    }

    private static bool IsServerProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return ServerProcesses.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static DateTime StartTimeOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch
        {
            return DateTime.Now;
        }
    }

    /// <summary>dsh web 的首页要么 401「dsh web authentication required」，要么直接给出前端 HTML。</summary>
    private async Task<bool> LooksLikeDshAsync(int port, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            var code = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (code == 401)
            {
                return body.Contains("dsh web", StringComparison.OrdinalIgnoreCase);
            }

            if (code is >= 200 and < 400)
            {
                return body.Contains("dsh", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>拉起 / 结束 dsh web 子进程，并从 stdout 里抓到带 token 的访问地址。</summary>
internal sealed class DshProcessHost : IDisposable
{
    private Process? _process;
    private readonly StringBuilder _logTail = new();

    public string? AuthenticatedUrl { get; private set; }

    /// <summary>从 auth.json 里恢复的、仍然匹配当前实例的带 token 地址。</summary>
    public string? RememberedUrl { get; private set; }

    private static string AuthFile => Path.Combine(AppPaths.Root, "auth.json");

    /// <summary>启动时把带 token 的地址记下来，挂件重启后（同一实例）还能用。</summary>
    private static void RememberUrl(int port, int pid, string url)
    {
        try
        {
            File.WriteAllText(AuthFile, $"{{\"port\":{port},\"pid\":{pid},\"url\":\"{url.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"}}");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"保存登录地址失败：{ex.Message}");
        }
    }

    /// <summary>尝试用 auth.json 里的地址匹配当前实例（端口必须一致，pid 有就一起比）。</summary>
    public void RecallUrl(int port, int pid)
    {
        RememberedUrl = null;
        try
        {
            if (!File.Exists(AuthFile))
            {
                return;
            }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(AuthFile));
            var root = doc.RootElement;
            var savedPort = root.GetProperty("port").GetInt32();
            var savedPid = root.GetProperty("pid").GetInt32();
            var savedUrl = root.GetProperty("url").GetString();

            if (savedPort != port || string.IsNullOrWhiteSpace(savedUrl))
            {
                return;
            }

            if (savedPid != 0 && pid != 0 && savedPid != pid)
            {
                return;
            }

            RememberedUrl = savedUrl;
            AppPaths.Log($"沿用之前记录的登录地址（端口 {port}，pid {pid}）");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取登录地址失败：{ex.Message}");
        }
    }
    public int? LaunchedPort { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LastError { get; private set; }
    public string LogPath { get; private set; } = "";

    public event Action? Exited;

    public bool IsRunning
    {
        get
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    public string Tail(int maxChars = 800)
    {
        lock (_logTail)
        {
            var text = _logTail.ToString();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
    }

    public static string? LocateDshExe(WidgetConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DshExePath) && File.Exists(config.DshExePath))
        {
            return config.DshExePath;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(home, ".bun", "bin", "dsh.exe"),
            Path.Combine(home, "AppData", "Roaming", "npm", "dsh.cmd"),
            Path.Combine(home, "AppData", "Roaming", "npm", "dsh.exe"),
        };

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir.Trim(), "dsh.exe"));
            candidates.Add(Path.Combine(dir.Trim(), "dsh.cmd"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public bool Start(WidgetConfig config, out string? error)
    {
        error = null;
        if (IsRunning)
        {
            return true;
        }

        var exe = LocateDshExe(config);
        if (exe is null)
        {
            error = "找不到 dsh.exe（可在 config.json 里设置 DshExePath）";
            LastError = error;
            return false;
        }

        var workDir = config.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
        {
            workDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var logDir = Path.Combine(AppPaths.Root, "logs");
        Directory.CreateDirectory(logDir);
        LogPath = Path.Combine(logDir, $"dsh-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var startInfo = new ProcessStartInfo(exe)
        {
            Arguments = string.IsNullOrWhiteSpace(config.LaunchArguments) ? "web --no-open" : config.LaunchArguments,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            AuthenticatedUrl = null;
            LaunchedPort = null;
            StartedAt = DateTime.Now;
            lock (_logTail)
            {
                _logTail.Clear();
            }

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutput;
            _process.ErrorDataReceived += OnOutput;
            _process.Exited += OnExited;

            if (!_process.Start())
            {
                error = "dsh 进程启动失败";
                LastError = error;
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            AppPaths.Log($"启动 dsh web，pid={_process.Id}，日志 {LogPath}");
            return true;
        }
        catch (Exception ex)
        {
            error = $"启动 dsh 失败：{ex.Message}";
            LastError = error;
            AppPaths.Log(error);
            return false;
        }
    }

    private void OnOutput(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
        {
            return;
        }

        var line = e.Data;
        lock (_logTail)
        {
            _logTail.AppendLine(line);
            if (_logTail.Length > 16_000)
            {
                _logTail.Remove(0, _logTail.Length - 12_000);
            }
        }

        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
        }

        const string prefix = "dsh web: ";
        var index = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return;
        }

        var url = line[(index + prefix.Length)..].Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            AuthenticatedUrl = url;
            LaunchedPort = uri.Port;
            AppPaths.Log($"捕获访问地址：http://127.0.0.1:{uri.Port}/?token=***");
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        AppPaths.Log("dsh 进程已退出");
        AuthenticatedUrl = null;
        Exited?.Invoke();
    }

    public void Stop(int timeoutMs = 4000)
    {
        var process = _process;
        _process = null;

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= OnExited;
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(timeoutMs);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"结束 dsh 失败：{ex.Message}");
        }
        finally
        {
            process.Dispose();
            AuthenticatedUrl = null;
        }
    }

    /// <summary>结束时杀掉外部启动的实例（按监听端口的进程树）。</summary>
    public static bool KillPid(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"结束 pid={pid} 失败：{ex.Message}");
            return false;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>读取 dsh 版本号，以及「插件」页「已安装插件」那组卡片对应的插件包。</summary>
internal static class DshInfo
{
    /// <summary>随 DSH 发布的 profile 组合包，插件页面会把它们从「已安装插件」里过滤掉。</summary>
    private static readonly HashSet<string> BuiltinProfileBundles = new(StringComparer.OrdinalIgnoreCase)
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
        "@deepseek-ai/dsh-headless",
        "@deepseek-ai/dsh-sdk-app",
        "@deepseek-ai/dsh-acp-app",
        "@deepseek-ai/dsh-sdk-minimal",
    };

    public static async Task<string?> ReadVersionAsync(string exe, TimeSpan timeout, CancellationToken ct)
    {
        // 直接读 CLI 的 package.json，比拉起 node 进程快两个数量级（冷启动关键路径）。
        if (TryReadVersionFromDisk() is { } fromDisk)
        {
            return fromDisk;
        }

        var output = await RunAsync(exe, "-V", timeout, ct).ConfigureAwait(false);
        var line = output?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Any(char.IsDigit));
        return line;
    }

    /// <summary>从已安装的 @deepseek-ai/dsh/package.json 里读版本号。</summary>
    private static string? TryReadVersionFromDisk()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".bun", "install", "global", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(home, "AppData", "Roaming", "npm", "node_modules", "@deepseek-ai", "dsh", "package.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@deepseek-ai", "dsh", "package.json"),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                if (document.RootElement.TryGetProperty("version", out var version))
                {
                    return version.GetString();
                }
            }
            catch
            {
                // 读不到就退回进程探测。
            }
        }

        return null;
    }

    public static async Task<(int Total, int Disabled)?> CountPluginsAsync(
        string exe,
        string profile,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var output = await RunAsync(exe, $"--profile {profile} --dump-config", timeout, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var total = 0;
        var disabled = 0;
        var inEntry = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("- id:", StringComparison.Ordinal))
            {
                total++;
                inEntry = true;
                continue;
            }

            if (inEntry && line.TrimStart().StartsWith("disabled:", StringComparison.Ordinal))
            {
                var value = line.TrimStart()["disabled:".Length..].Trim();
                if (value is "true" or "True")
                {
                    disabled++;
                }
            }
        }

        return total == 0 ? null : (total, disabled);
    }

    /// <summary>
    /// 读取「插件」页「已安装插件」那组卡片：profile 清单 dependencies 里真正装进来的包，
    /// 排除随 DSH 发布的组合包（dsh-base / dsh-web-app 等，页面会过滤掉）。
    /// </summary>
    public static (int Total, int Disabled, IReadOnlyList<string> Names)? ReadInstalledPlugins(string profile)
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("DSH_HOME");
            if (string.IsNullOrWhiteSpace(home))
            {
                home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".dsh");
            }

            var manifest = Path.Combine(home, "profiles", profile, "package.json");
            if (!File.Exists(manifest))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            var root = document.RootElement;

            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("dsh", out var dsh) &&
                dsh.TryGetProperty("profile", out var profileNode) &&
                profileNode.TryGetProperty("bundles", out var bundles) &&
                bundles.ValueKind == JsonValueKind.Array)
            {
                foreach (var bundle in bundles.EnumerateArray())
                {
                    var name = bundle.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        selected.Add(name);
                    }
                }
            }

            var installed = new List<string>();
            var disabled = 0;
            if (root.TryGetProperty("dependencies", out var dependencies) &&
                dependencies.ValueKind == JsonValueKind.Object)
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    var name = dependency.Name;
                    if (string.IsNullOrWhiteSpace(name) || BuiltinProfileBundles.Contains(name))
                    {
                        continue;
                    }

                    installed.Add(name);
                    if (!selected.Contains(name))
                    {
                        disabled++;
                    }
                }
            }

            installed.Sort(StringComparer.OrdinalIgnoreCase);
            return (installed.Count, disabled, installed);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取 profile 清单失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<string?> RunAsync(string exe, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var startInfo = new ProcessStartInfo(exe)
            {
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return null;
            }

            var text = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return text;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"执行 {arguments} 失败：{ex.Message}");
            return null;
        }
    }
}

using System.Diagnostics;

namespace DshOrbit;

/// <summary>
/// dsh 进程的生命周期：发现（<see cref="DshDiscovery"/>）、拉起 / 停止（<see cref="DshProcessHost"/>）、
/// 端口探测、内存 / CPU 采样与版本读取。状态变化通过 <see cref="Changed"/> 通知宿主。
///
/// 约定：**所有成员都在 UI 线程调用**（`RefreshAsync` 的 await 也回到 UI 上下文），
/// 自身不持有任何 WinForms Timer；`WidgetApp` 管节奏，这里只管状态与动作。
/// </summary>
internal sealed class DshLifecycle : IDisposable
{
    private readonly Func<WidgetConfig> _config;
    private readonly CancellationToken _ct;
    private readonly DshDiscovery _discovery = new();
    private readonly DshProcessHost _host = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private DshInstance? _instance;
    private bool _online;
    private bool _starting;
    private bool _userStopped;
    private DateTime _suppressProbeUntil;
    private DateTime? _startedAt;
    private string? _hint;
    private string? _version;
    private long _lastMemory;
    private double? _lastCpu;
    private (DateTime At, TimeSpan Cpu)? _cpuSample;

    public DshLifecycle(Func<WidgetConfig> config, CancellationToken ct)
    {
        _config = config;
        _ct = ct;

        // 本机压根没有 dsh 时，先把话说明白（卡片上会显示这行 hint）。
        if (DshProcessHost.LocateDshExe(config()) is null)
        {
            _hint = "未找到 dsh：请先安装 dsh，或手动运行 dsh web";
        }

        _host.Exited += () =>
        {
            _starting = false;
            _startedAt = null;
            Changed?.Invoke();
            _ = RefreshAsync();
        };
    }

    /// <summary>状态有变化（探测结果 / 启停 / hint / 版本）时触发；宿主据此重画快照。</summary>
    public event Action? Changed;

    /// <summary>正在停止（防止连点「停止」又拉起新实例）。</summary>
    public bool Stopping { get; private set; }

    public DshInstance? Instance => _instance;

    public bool Online => _online;

    public bool HostRunning => _host.IsRunning;

    public bool Starting => _starting;

    public bool RunningOrOnline => _online || _host.IsRunning;

    public string? Hint => _hint;

    public string? Version => _version;

    public int? Port => _instance?.Port;

    public bool HasUrl => _instance is not null || _host.AuthenticatedUrl is not null;

    public string? AuthenticatedUrl => _host.AuthenticatedUrl;

    public string? RememberedUrl => _host.RememberedUrl;

    /// <summary>「打开 Web」实际要用的地址：优先带 token 的，其次回环地址。</summary>
    public string? CurrentUrl => _host.AuthenticatedUrl
        ?? (_instance is not null ? $"http://127.0.0.1:{_instance.Port}/" : null);

    public long MemoryBytes => _lastMemory;

    public double? CpuPercent => _lastCpu;

    public TimeSpan? Uptime => _instance is not null
        ? DateTime.Now - _instance.StartedAt
        : _host.IsRunning && _startedAt is { } started ? DateTime.Now - started : null;

    // ---------------------------------------------------------------- 探测

    /// <summary>端口探测（节流由调用方的定时器 / 显式调用决定；内部只防重入）。</summary>
    public async Task RefreshAsync()
    {
        if (DateTime.UtcNow < _suppressProbeUntil)
        {
            return;
        }

        if (!await _probeGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            if (_instance is not null)
            {
                var alive = Native.ListenLoopbackPorts()
                    .Any(row => row.Port == _instance.Port && row.Pid == _instance.ListenerPid);

                if (alive)
                {
                    _online = true;
                    _starting = false;
                    _hint = null;
                    Changed?.Invoke();
                    return;
                }

                AppPaths.Log($"端口 {_instance.Port} 已关闭，重新扫描");
                _instance = null;
                _online = false;
            }

            if (_userStopped)
            {
                // 用户明确点过「停止」：不再自动去找别的实例附着，
                // 否则看起来就像"停掉之后又自己开了一个"。
                _online = false;
                _starting = false;
                Changed?.Invoke();
                return;
            }

            var config = _config();
            var preferred = _host.LaunchedPort ?? config.PreferredPort;
            var found = await _discovery.FindAsync(preferred, _ct).ConfigureAwait(true);
            if (found is not null)
            {
                _instance = found;
                _online = true;
                _starting = false;
                _hint = null;
                AppPaths.Log(Stopping
                    ? $"dsh 又出现在端口 {found.Port}（pid {found.ListenerPid}）——不是挂件启动的，可能是外部守护进程重新拉起"
                    : $"附着到 dsh web：端口 {found.Port}，pid {found.ListenerPid}");
            }
            else
            {
                _online = false;
                if (_host.IsRunning)
                {
                    var elapsed = _startedAt is { } started ? DateTime.Now - started : TimeSpan.Zero;
                    if (elapsed < TimeSpan.FromSeconds(25))
                    {
                        _hint = "等待 dsh 监听端口…";
                    }
                    else
                    {
                        _hint = "启动超时，查看日志";
                        _starting = false;
                    }
                }
                else
                {
                    _hint = null;
                }
            }

            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppPaths.Log($"刷新探测失败：{ex.Message}");
        }
        finally
        {
            _probeGate.Release();
        }
    }

    /// <summary>心跳里刷新内存 / CPU（拿不到进程就清零，别留着上一次的数字）。</summary>
    public void UpdateMetrics()
    {
        _lastMemory = 0;
        _lastCpu = null;

        if (_instance is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(_instance.ListenerPid);
            _lastMemory = process.WorkingSet64;

            var now = DateTime.UtcNow;
            var cpu = process.TotalProcessorTime;
            if (_cpuSample is { } previous)
            {
                var wall = (now - previous.At).TotalSeconds;
                if (wall > 0.2)
                {
                    var used = (cpu - previous.Cpu).TotalSeconds;
                    _lastCpu = Math.Clamp(used / wall / Environment.ProcessorCount * 100.0, 0, 100);
                }
            }

            _cpuSample = (now, cpu);
        }
        catch
        {
            _cpuSample = null;
        }
    }

    /// <summary>读一次 `dsh --version`（失败不影响主流程）。</summary>
    public async Task LoadVersionAsync()
    {
        var exe = DshProcessHost.LocateDshExe(_config());
        if (exe is null)
        {
            return;
        }

        try
        {
            _version = await DshInfo
                .ReadVersionAsync(exe, TimeSpan.FromSeconds(15), _ct)
                .ConfigureAwait(true);
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---------------------------------------------------------------- 启停

    /// <summary>停止 → 启动 的翻转（托盘的启动/停止按钮）。</summary>
    public async Task ToggleAsync(Func<DshInstance, bool> confirmStop)
    {
        // 停止过程中再点一次，不能立刻又拉起一个新实例。
        if (Stopping)
        {
            return;
        }

        if (RunningOrOnline)
        {
            Stopping = true;
            try
            {
                await StopAsync(confirmStop).ConfigureAwait(true);
            }
            finally
            {
                Stopping = false;
            }
        }
        else
        {
            Start(out _);
        }
    }

    /// <summary>拉起 dsh web。返回 false 时 <paramref name="error"/> 给宿主弹气泡用。</summary>
    public bool Start(out string? error)
    {
        error = null;
        _userStopped = false;

        if (_host.IsRunning)
        {
            return true;
        }

        var config = _config();
        AppPaths.Log($"启动 dsh web（工作目录 {config.WorkingDirectory}，端口 {config.PreferredPort}）");

        if (!_host.Start(config, out error))
        {
            _hint = error;
            _starting = false;
            Changed?.Invoke();
            return false;
        }

        _starting = true;
        _startedAt = DateTime.Now;
        _hint = "等待 dsh 监听端口…";
        // 本机压根没有 dsh 时，先把话说明白（卡片上会显示这行 hint）。
        if (DshProcessHost.LocateDshExe(config) is null)
        {
            _hint = "未找到 dsh：请先安装 dsh，或手动运行 dsh web";
        }

        Changed?.Invoke();
        _ = RefreshAsync();
        return true;
    }

    /// <summary>
    /// 停止 dsh。挂件自己拉起的实例直接停；外部实例先用 <paramref name="confirmStop"/> 询问用户。
    /// 返回 false 表示用户取消了（状态保持原样）。
    /// </summary>
    public async Task<bool> StopAsync(Func<DshInstance, bool> confirmStop)
    {
        // 杀掉进程后端口探测会短暂看到"空窗"，把重连压后几秒，
        // 避免界面在"已停止"和"又在运行"之间来回跳。
        AppPaths.Log(_instance is null
            ? "用户请求停止 dsh（挂件自己启动的实例）"
            : $"用户请求停止 dsh（外部实例：端口 {_instance.Port}，pid {_instance.ListenerPid}）");

        _suppressProbeUntil = DateTime.UtcNow.AddSeconds(4);
        _hint = "正在停止 dsh…";
        Changed?.Invoke();

        if (_host.IsRunning)
        {
            _host.Stop();
        }
        else if (_instance is not null)
        {
            if (!confirmStop(_instance))
            {
                // 用户取消了：恢复原状，别把界面留在「已停止」。
                _userStopped = false;
                _hint = null;
                _suppressProbeUntil = DateTime.MinValue;
                Changed?.Invoke();
                return false;
            }

            _ = DshProcessHost.KillPid(_instance.ListenerPid);
        }

        _userStopped = true;
        _instance = null;
        _online = false;
        _starting = false;
        _hint = null;
        _cpuSample = null;
        Changed?.Invoke();
        await Task.Delay(600).ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        return true;
    }

    /// <summary>退出挂件时：只停挂件自己拉起的进程（外部实例由用户决定）。</summary>
    public void StopHost() => _host.Stop();

    public void Dispose()
    {
        _host.Dispose();
        _probeGate.Dispose();
    }
}

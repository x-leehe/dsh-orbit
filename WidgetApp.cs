using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>挂件应用：托盘、定时刷新、进程/余额/插件/外壳的协调者。</summary>
internal sealed class WidgetApp : ApplicationContext
{
    private readonly WidgetConfig _config;
    private LoopbackAuthServer? _authServer;
    private readonly WidgetForm _form;
    private readonly NotifyIcon _tray;
    private readonly DshDiscovery _discovery = new();
    private readonly DshProcessHost _host = new();
    private readonly BalanceService _balance = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _probeTimer = new();
    private readonly System.Windows.Forms.Timer _pluginTimer = new();
    private readonly System.Windows.Forms.Timer _balanceTimer = new();
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 600 };
    private readonly System.Windows.Forms.Timer _warmupTimer = new() { Interval = 1200 };

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _probeGate = new(1, 1);
    private readonly SemaphoreSlim _pluginGate = new(1, 1);
    private readonly SemaphoreSlim _balanceGate = new(1, 1);

    private TrayMenuForm? _trayMenu;
    private WebShell? _shell;
    private DshInstance? _instance;
    private bool _online;
    private bool _starting;
    private bool _stopping;
    private bool _userStopped;
    private DateTime _suppressProbeUntil;
    private DateTime? _startedAt;
    private string? _hint;
    private string? _version;
    private int? _pluginTotal;
    private int? _pluginDisabled;
    private string _pluginNames = "";
    private bool _pluginScanning;
    private long _lastMemory;
    private double? _lastCpu;
    private (DateTime At, TimeSpan Cpu)? _cpuSample;
    private bool _exitConfirmed;

    public WidgetApp(WidgetConfig config)
    {
        _config = config;
        AppPaths.Ensure();

        // 令牌层必须在任何窗口创建前就绪（配色、字体都从它取）。
        Fluent.Init(config.Theme);

        _form = new WidgetForm(config);
        _form.StartStopRequested += () => _ = ToggleDshAsync();
        _form.OpenRequested += OpenWebUi;
        _form.LockRequested += ToggleLock;
        _form.Moved += SavePosition;
        _form.DockStateChanged += SaveDock;
        _form.StatusChanged += OnStatusChanged;

        // 任何位移（拖动/程序改位置）后延迟写盘，保证下次启动回到原位。
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SavePosition();
        };
        _form.LocationChanged += (_, _) =>
        {
            if (_saveTimer.Enabled)
            {
                _saveTimer.Stop();
            }

            _saveTimer.Start();
        };

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "DSH-Orbit",
            Icon = IconFactory.Create(Fluent.TextTertiary),
        };
        _tray.DoubleClick += (_, _) => ShowWidget();
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowTrayMenu();
            }
        };

        Fluent.Changed += OnThemeChanged;

        _host.Exited += () =>
        {
            _starting = false;
            _startedAt = null;
            _ = BeginInvokeOnUi(() => _ = RefreshProbeAsync());
        };

        PlaceWindow();
        if (_config.Visible)
        {
            _form.Show();
            AppPaths.Log($"cold-start: 窗口可见 用时 {Startup.ElapsedMs}ms");
        }

        if (_config.EdgeHideEnabled && _config.DockSide != DockSide.None)
        {
            _form.DockTo(_config.DockSide, hidden: false);
        }

        _uiTimer.Interval = 1000;
        _uiTimer.Tick += (_, _) => Tick();
        _uiTimer.Start();

        _probeTimer.Interval = Math.Max(1, _config.StatusIntervalSeconds) * 1000;
        _probeTimer.Tick += (_, _) => _ = RefreshProbeAsync();
        _probeTimer.Start();

        _pluginTimer.Interval = Math.Max(30, _config.PluginIntervalSeconds) * 1000;
        _pluginTimer.Tick += (_, _) => _ = RefreshPluginsAsync();
        _pluginTimer.Start();

        _balanceTimer.Interval = Math.Max(20, _config.BalanceIntervalSeconds) * 1000;
        _balanceTimer.Tick += (_, _) => _ = RefreshBalanceAsync();
        _balanceTimer.Start();

        Tick();
        _ = RefreshProbeAsync();

        // 冷启动只保证「窗口先出来」：版本 / 插件 / 余额都等界面稳定后再取。
        _warmupTimer.Tick += (_, _) =>
        {
            _warmupTimer.Stop();
            _ = RefreshVersionAsync();
            _ = RefreshPluginsAsync();
            if (_config.ShowBalance)
            {
                _ = RefreshBalanceAsync();
            }
        };
        _warmupTimer.Start();

        AutoStart.MigrateIfNeeded();

        try
        {
            _authServer = new LoopbackAuthServer();
            AuthLinks.RelayPort = _authServer.Port;
            AppPaths.Log($"登录中转已启动：http://127.0.0.1:{_authServer.Port}/open?to=...");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"登录中转启动失败：{ex.Message}");
        }

        // 命令行通道：`DshStatus --show-window` 会走这里。
        SingleInstance.StartServer(OnCommandReceived, _cts.Token);

        if (Startup.AutoStartDsh && !(_online || _host.IsRunning))
        {
            StartDsh();
        }

        if (Startup.OpenWebOnStart)
        {
            _ = Task.Delay(400).ContinueWith(
                _ => BeginInvokeOnUi(() => _ = ShowWindowAsync()),
                TaskScheduler.Default);
        }
    }

    // ---------------------------------------------------------------- 定时刷新

    private void Tick()
    {
        // 系统主题/强调色变了就跟着变（每个心跳读一次注册表，开销可忽略）。
        Fluent.Refresh();

        UpdateMetrics();
        _form.SetStatus(BuildSnapshot());
        UpdateTray();
    }

    private async Task RefreshProbeAsync()
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
            var ct = _cts.Token;

            if (_instance is not null)
            {
                var alive = Native.ListenLoopbackPorts()
                    .Any(row => row.Port == _instance.Port && row.Pid == _instance.ListenerPid);

                if (alive)
                {
                    _online = true;
                    _starting = false;
                    _hint = null;
                    Tick();
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
                Tick();
                return;
            }

            var preferred = _host.LaunchedPort ?? _config.PreferredPort;
            var found = await _discovery.FindAsync(preferred, ct).ConfigureAwait(true);
            if (found is not null)
            {
                _instance = found;
                _online = true;
                _starting = false;
                _hint = null;
                AppPaths.Log(_stopping
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

            Tick();
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

    private void UpdateMetrics()
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

    private async Task RefreshPluginsAsync()
    {
        if (!await _pluginGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            // 与 Web UI「插件」页「已安装插件」同口径：profile 清单里真正装进来的包。
            var installed = DshInfo.ReadInstalledPlugins(_config.Profile);
            if (installed is { } value)
            {
                _pluginTotal = value.Total;
                _pluginDisabled = value.Disabled;
                _pluginNames = string.Join("、", value.Names.Select(ShortPackageName));
            }

            _pluginScanning = false;
            Tick();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _pluginScanning = false;
            AppPaths.Log($"插件统计失败：{ex.Message}");
        }
        finally
        {
            _pluginGate.Release();
        }
    }

    private async Task RefreshVersionAsync()
    {
        var exe = DshProcessHost.LocateDshExe(_config);
        if (exe is null)
        {
            return;
        }

        try
        {
            _version = await DshInfo
                .ReadVersionAsync(exe, TimeSpan.FromSeconds(15), _cts.Token)
                .ConfigureAwait(true);
            Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshBalanceAsync()
    {
        if (!_config.ShowBalance || !await _balanceGate.WaitAsync(0).ConfigureAwait(true))
        {
            return;
        }

        try
        {
            await _balance.RefreshAsync(_config, _cts.Token).ConfigureAwait(true);
            Tick();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _balanceGate.Release();
        }
    }

    // ---------------------------------------------------------------- 状态拼装

    private StatusSnapshot BuildSnapshot()
    {
        DshState state;
        if (_online)
        {
            state = DshState.Running;
        }
        else if (_host.IsRunning)
        {
            state = _starting ? DshState.Starting : DshState.Error;
        }
        else
        {
            state = DshState.Stopped;
        }

        var uptime = _instance is not null
            ? DateTime.Now - _instance.StartedAt
            : _host.IsRunning && _startedAt is { } started ? DateTime.Now - started : (TimeSpan?)null;

        return new StatusSnapshot
        {
            State = state,
            Profile = _config.Profile,
            Version = _version ?? "",
            Port = _instance?.Port,
            DisplayUrl = _instance is not null ? $"http://127.0.0.1:{_instance.Port}/" : "",
            HasAuthenticatedUrl = _host.AuthenticatedUrl is not null,
            Uptime = uptime,
            MemoryBytes = _lastMemory,
            CpuPercent = _lastCpu,
            PluginTotal = _pluginTotal,
            PluginDisabled = _pluginDisabled,
            PluginNames = _pluginNames,
            PluginScanning = _pluginScanning,
            Balance = _balance.Balance,
            SpentToday = _balance.SpentToday,
            OffPeak = BalanceService.IsOffPeak(DateTime.UtcNow),
            Hint = _hint,
        };
    }

    private void OnStatusChanged()
    {
        var snapshot = _form.Status;
        _tray.Icon = IconFactory.Create(snapshot.Accent);

        var text = snapshot.State switch
        {
            DshState.Running => $"DSH 运行中 · 端口 {snapshot.Port}",
            DshState.Starting => "DSH 启动中…",
            DshState.Error => "DSH 异常 · 查看日志",
            _ => "DSH 已停止",
        };

        if (snapshot.Balance is { } balance)
        {
            text += $" · ¥{balance:0.00}";
        }

        _tray.Text = text.Length > 63 ? text[..63] : text;
    }

    private void UpdateTray()
    {
        // 自绘菜单每次弹出时按当时状态重建，这里不用同步。
    }

    // ---------------------------------------------------------------- 操作

    private async Task ToggleDshAsync()
    {
        // 停止过程中再点一次，不能立刻又拉起一个新实例。
        if (_stopping)
        {
            return;
        }

        if (_online || _host.IsRunning)
        {
            _stopping = true;
            try
            {
                await StopDshAsync().ConfigureAwait(true);
            }
            finally
            {
                _stopping = false;
            }
        }
        else
        {
            StartDsh();
        }
    }

    private void StartDsh()
    {
        _userStopped = false;

        if (_host.IsRunning)
        {
            return;
        }

        AppPaths.Log($"启动 dsh web（工作目录 {_config.WorkingDirectory}，端口 {_config.PreferredPort}）");

        if (!_host.Start(_config, out var error))
        {
            _hint = error;
            _starting = false;
            Tick();
            _tray.ShowBalloonTip(4000, "无法启动 dsh", error ?? "未知错误", ToolTipIcon.Error);
            return;
        }

        _starting = true;
        _startedAt = DateTime.Now;
        _hint = "等待 dsh 监听端口…";
        Tick();
        _ = RefreshProbeAsync();
    }

    private async Task StopDshAsync()
    {
        // 杀掉进程后端口探测会短暂看到"空窗"，把重连压后几秒，
        // 避免界面在"已停止"和"又在运行"之间来回跳。
        AppPaths.Log(_instance is null
            ? "用户请求停止 dsh（挂件自己启动的实例）"
            : $"用户请求停止 dsh（外部实例：端口 {_instance.Port}，pid {_instance.ListenerPid}）");

        _suppressProbeUntil = DateTime.UtcNow.AddSeconds(4);
        _hint = "正在停止 dsh…";
        Tick();

        if (_host.IsRunning)
        {
            _host.Stop();
        }
        else if (_instance is not null)
        {
            var answer = MessageBox.Show(
                $"这会结束已经在运行的 dsh web（端口 {_instance.Port}，pid {_instance.ListenerPid}）。\n\n继续吗？",
                "停止 dsh",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                // 用户取消了：恢复原状，别把界面留在「已停止」。
                _userStopped = false;
                _hint = null;
                _suppressProbeUntil = DateTime.MinValue;
                Tick();
                return;
            }

            _ = DshProcessHost.KillPid(_instance.ListenerPid);
        }

        _userStopped = true;
        _instance = null;
        _online = false;
        _starting = false;
        _hint = null;
        _cpuSample = null;
        Tick();
        await Task.Delay(600).ConfigureAwait(true);
        await RefreshProbeAsync().ConfigureAwait(true);
    }

    /// <summary>管道命令（管道线程）→ UI 线程。</summary>
    private void OnCommandReceived(string command)
    {
        if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
        {
            _ = BeginInvokeOnUi(ShowWidget);
            return;
        }

        if (!string.Equals(command, "show-window", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = BeginInvokeOnUi(() => _ = ShowWindowAsync());
    }

    /// <summary>把挂件和 Web 外壳都摆到前台；dsh 没在跑就先拉起来。</summary>
    private async Task ShowWindowAsync()
    {
        ShowWidget();

        if (!(_online || _host.IsRunning))
        {
            StartDsh();
        }

        await ShowShellWhenReadyAsync().ConfigureAwait(true);
    }

    /// <summary>等 dsh 起来（最多 ~16 秒）再打开外壳。</summary>
    private async Task ShowShellWhenReadyAsync()
    {
        for (var i = 0; i < 40; i++)
        {
            if (_instance is not null || _host.AuthenticatedUrl is not null)
            {
                break;
            }

            await Task.Delay(400).ConfigureAwait(true);
        }

        ShowShell();
    }

    /// <summary>确保外壳可见（不切换、不隐藏）。</summary>
    private void ShowShell()
    {
        string? url = _host.AuthenticatedUrl;
        if (url is null && _instance is not null)
        {
            url = $"http://127.0.0.1:{_instance.Port}/";
        }

        if (url is null)
        {
            _tray.ShowBalloonTip(3000, "DSH 尚未就绪", "还没发现 dsh web，稍后可以点托盘「打开 Web UI」。", ToolTipIcon.Info);
            return;
        }

        if (_config.UseWebShell && WebShell.IsRuntimeAvailable())
        {
            EnsureShellVisible(url);
            return;
        }

        OpenInBrowser(url);
    }

    private void EnsureShellVisible(string url)
    {
        if (_shell is { IsDisposed: false } shell)
        {
            shell.CloseMode = _config.ShellClose;
            shell.ShowUrl(url);
            return;
        }

        var created = new WebShell(url, _config.ShellClose);
        created.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_shell, created))
            {
                _shell = null;
            }
        };

        _shell = created;
        created.Show();
    }

    private void OpenWebUi()
    {
        string? url = _host.AuthenticatedUrl;
        if (url is null && _instance is not null)
        {
            url = $"http://127.0.0.1:{_instance.Port}/";
        }

        if (url is null)
        {
            _tray.ShowBalloonTip(3000, "DSH 未运行", "先点「启动 dsh」，或手动在终端运行 dsh web。", ToolTipIcon.Info);
            return;
        }

        if (_config.UseWebShell && WebShell.IsRuntimeAvailable())
        {
            ToggleShell(url);
            return;
        }

        OpenInBrowser(url);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppPaths.Log($"打开浏览器失败：{ex.Message}");
        }
    }

    /// <summary>Web 外壳：没开就开；开着就按设置收起（隐藏）或关闭，再点一次等于切换。</summary>
    private void ToggleShell(string url)
    {
        if (_shell is { IsDisposed: false } shell)
        {
            if (shell.Visible)
            {
                shell.HideOrClose();
                if (shell.CloseMode == ShellCloseMode.Close)
                {
                    _shell = null;
                }

                return;
            }

            shell.CloseMode = _config.ShellClose;
            shell.ShowUrl(url);
            return;
        }

        var created = new WebShell(url, _config.ShellClose);
        created.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_shell, created))
            {
                _shell = null;
            }
        };

        _shell = created;
        created.Show();
    }

    private void ToggleLock()
    {
        var locked = !_config.ClickThrough;
        _config.ClickThrough = locked;
        _form.ApplyClickThrough(locked);
        JsonIo.Save(AppPaths.ConfigFile, _config);
        _form.Invalidate();
        Tick();

        if (locked)
        {
            _tray.ShowBalloonTip(2500, "挂件已锁定",
                "点击会穿透到下层窗口，光标附近会开一个「探照灯」。右键托盘图标可解锁。", ToolTipIcon.Info);
        }
    }

    private void ToggleEdgeHide()
    {
        _config.EdgeHideEnabled = !_config.EdgeHideEnabled;
        if (!_config.EdgeHideEnabled)
        {
            _form.Undock();
            _config.DockSide = DockSide.None;
            _config.X = _form.Left;
            _config.Y = _form.Top;
        }

        JsonIo.Save(AppPaths.ConfigFile, _config);
    }

    private void ShowWidget()
    {
        _config.Visible = true;
        JsonIo.Save(AppPaths.ConfigFile, _config);
        _form.Show();
        _form.Refresh();
    }

    private void HideWidget()
    {
        _config.Visible = false;
        JsonIo.Save(AppPaths.ConfigFile, _config);
        _form.Hide();
    }

    private void SavePosition()
    {
        if (_form.DockSide != DockSide.None)
        {
            return;
        }

        _config.X = _form.Left;
        _config.Y = _form.Top;
        JsonIo.Save(AppPaths.ConfigFile, _config);
    }

    private void SaveDock()
    {
        _config.DockSide = _form.DockSide;
        if (_form.DockSide == DockSide.None)
        {
            _config.X = _form.Left;
            _config.Y = _form.Top;
        }

        JsonIo.Save(AppPaths.ConfigFile, _config);
    }

    private void PlaceWindow()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var width = _form.Width;
        var height = _form.Height;

        var x = _config.X >= 0 ? _config.X : area.Right - width - 28;
        var y = _config.Y >= 0 ? _config.Y : area.Bottom - height - 28;

        var visible = Screen.AllScreens.Any(screen =>
            screen.WorkingArea.IntersectsWith(new Rectangle(x, y, width, height)));
        if (!visible)
        {
            x = area.Right - width - 28;
            y = area.Bottom - height - 28;
        }

        _form.Location = new Point(x, y);
    }

    // ---------------------------------------------------------------- 托盘菜单

    private void ShowTrayMenu()
    {
        _trayMenu?.Dispose();

        var running = _online || _host.IsRunning;
        var state = new MenuState(
            Running: running,
            Starting: _starting,
            HasUrl: _instance is not null || _host.AuthenticatedUrl is not null,
            LayoutBottom: _config.AlwaysBottom,
            LayoutTop: _config.AlwaysOnTop,
            Locked: _config.ClickThrough,
            EdgeHideEnabled: _config.EdgeHideEnabled,
            Visible: _config.Visible,
            ShellHide: _config.ShellClose == ShellCloseMode.Hide,
            AutoStart: AutoStart.GetMode());

        var menu = new TrayMenuForm(state, OnMenuCommand);
        _trayMenu = menu;
        menu.ShowAtCursor();
    }

    private void OnMenuCommand(string command)
    {
        switch (command)
        {
            case "open":
                OpenWebUi();
                break;
            case "start":
                StartDsh();
                break;
            case "stop":
                _ = StopDshAsync();
                break;
            case "restart":
                _ = RestartAsync();
                break;
            case "layout:bottom":
                SetZOrder(bottom: true, top: false);
                break;
            case "layout:top":
                SetZOrder(bottom: false, top: true);
                break;
            case "layout:normal":
                SetZOrder(bottom: false, top: false);
                break;
            case "lock":
                ToggleLock();
                break;
            case "edge":
                ToggleEdgeHide();
                break;
            case "shell":
                _config.ShellClose = _config.ShellClose == ShellCloseMode.Hide
                    ? ShellCloseMode.Close
                    : ShellCloseMode.Hide;
                if (_shell is { IsDisposed: false } shell)
                {
                    shell.CloseMode = _config.ShellClose;
                }

                JsonIo.Save(AppPaths.ConfigFile, _config);
                break;
            case "autostart:Disabled":
                AutoStart.SetMode(AutoStartMode.Disabled);
                break;
            case "autostart:Enabled":
                AutoStart.SetMode(AutoStartMode.Enabled);
                break;
            case "autostart:FullAuto":
                AutoStart.SetMode(AutoStartMode.FullAuto);
                break;
            case "theme:System":
                ApplyTheme(AppTheme.System);
                break;
            case "theme:Light":
                ApplyTheme(AppTheme.Light);
                break;
            case "theme:Dark":
                ApplyTheme(AppTheme.Dark);
                break;
            case "eye":
                if (_config.Visible)
                {
                    HideWidget();
                }
                else
                {
                    ShowWidget();
                }

                break;
            case "copy":
                CopyUrl();
                break;
            case "logs":
                OpenLogFolder();
                break;
            case "exit":
                RequestExit();
                break;
        }
    }

    private void ApplyTheme(AppTheme mode)
    {
        if (_config.Theme == mode)
        {
            return;
        }

        _config.Theme = mode;
        JsonIo.Save(AppPaths.ConfigFile, _config);
        Fluent.SetMode(mode); // 触发 Changed，由 OnThemeChanged 统一重绘
    }

    /// <summary>主题或强调色变化后，让所有窗口按新令牌重画。</summary>
    private void OnThemeChanged()
    {
        try
        {
            if (_form is { IsDisposed: false })
            {
                _form.Refresh();
            }

            if (_shell is { IsDisposed: false } shell)
            {
                shell.Invalidate(true);
                shell.Update();
            }

            _trayMenu?.Invalidate(true);

            if (_config.Visible)
            {
                _form.Refresh();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"主题重绘失败：{ex.Message}");
        }
    }

    private void SetZOrder(bool bottom, bool top)
    {
        _config.AlwaysBottom = bottom;
        _config.AlwaysOnTop = top;
        JsonIo.Save(AppPaths.ConfigFile, _config);
        _form.ApplyZOrderMode(_config);
    }

    private void CopyUrl()
    {
        if (_instance is null)
        {
            return;
        }

        try
        {
            AuthLinks.TokenUrl = _host.AuthenticatedUrl ?? _host.RememberedUrl;
            var plain = $"http://127.0.0.1:{_instance.Port}/";
            Clipboard.SetText(AuthLinks.For(plain));
        }
        catch
        {
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(AppPaths.Root, "logs"));
            Process.Start(new ProcessStartInfo(Path.Combine(AppPaths.Root, "logs")) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async Task RestartAsync()
    {
        await StopDshAsync().ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);
        StartDsh();
    }

    private void RequestExit()
    {
        if (_host.IsRunning)
        {
            var answer = MessageBox.Show(
                "由挂件启动的 dsh web 仍在运行。\n\n是：停止 dsh 并退出\n否：保留 dsh，只关挂件\n取消：不退出",
                "退出挂件",
                MessageBoxButtons.YesNoCancel,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button1);

            if (answer == DialogResult.Cancel)
            {
                return;
            }

            if (answer == DialogResult.Yes)
            {
                _host.Stop();
            }
        }

        _exitConfirmed = true;
        ExitThread();
    }

    private static string ShortPackageName(string name)
    {
        if (name.StartsWith("@", StringComparison.Ordinal))
        {
            var slash = name.IndexOf('/');
            return slash >= 0 && slash + 1 < name.Length ? name[(slash + 1)..] : name;
        }

        return name;
    }

    private Task BeginInvokeOnUi(Action action)
    {
        if (_form.IsHandleCreated)
        {
            _form.BeginInvoke(action);
        }

        return Task.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _exitConfirmed = true;
            _cts.Cancel();
            _uiTimer.Dispose();
            _probeTimer.Dispose();
            _pluginTimer.Dispose();
            _balanceTimer.Dispose();
            _saveTimer.Dispose();
            _warmupTimer.Dispose();
            _balance.Dispose();
            _host.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _shell?.ForceClose();
            _trayMenu?.Dispose();
            _form.Dispose();
        }

        base.Dispose(disposing);
    }

    public bool ExitConfirmed => _exitConfirmed;
}

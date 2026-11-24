using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>挂件应用：托盘、定时刷新、进程/余额/插件/外壳的协调者。</summary>
internal sealed class WidgetApp : ApplicationContext
{
    private readonly WidgetConfig _config;
    private readonly ConfigStore _store;
    private readonly DshLifecycle _dsh;
    private SettingsForm? _settingsForm;
    private LoopbackAuthServer? _authServer;
    private readonly WidgetForm _form;
    private readonly TrayIcon _tray;
    private readonly ShellController _shells;
    private readonly BalanceService _balance = new();
    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly System.Windows.Forms.Timer _probeTimer = new();
    private readonly System.Windows.Forms.Timer _pluginTimer = new();
    private readonly System.Windows.Forms.Timer _balanceTimer = new();
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 600 };
    private readonly System.Windows.Forms.Timer _warmupTimer = new() { Interval = 1200 };

    /// <summary>法定节假日年表：30 分钟看一次（<see cref="ChinaHolidays.EnsureAsync"/> 自带 20h/30min 节流）。</summary>
    private readonly System.Windows.Forms.Timer _holidayTimer = new() { Interval = 30 * 60 * 1000 };

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pluginGate = new(1, 1);
    private readonly SemaphoreSlim _balanceGate = new(1, 1);

    private TrayMenuForm? _trayMenu;
    private int? _pluginTotal;
    private int? _pluginDisabled;
    private string _pluginNames = "";
    private bool _pluginScanning;
    private bool _exitConfirmed;

    /// <summary>全屏自动隐藏的状态机（隐藏 / 压制标记都在里面）。</summary>
    private readonly FullscreenPolicy _fullscreen = new();

    /// <summary>会话数节流间隔：扫 40+ 个小 JSON 要几十毫秒，不能每个心跳都做。</summary>
    private static readonly TimeSpan SessionProbeInterval = TimeSpan.FromSeconds(15);

    /// <summary>最近一次会话数快照；null = 无法判定（UI 显示「—」），不要回退成 0。</summary>
    private int? _activeSessions;

    /// <summary>上次扫盘时刻；<see cref="DateTime.MinValue"/> 表示还没预热过，Tick 里不扫。</summary>
    private DateTime _lastSessionProbe = DateTime.MinValue;

    public WidgetApp(ConfigStore store)
    {
        _store = store;
        _config = store.Config;
        AppPaths.Ensure();

        // dsh 生命周期（发现 / 启停 / 探测 / 指标）全部交给 DshLifecycle；
        // 状态一变就重画一次快照。
        _dsh = new DshLifecycle(() => _config, _cts.Token);
        _dsh.Changed += Tick;

        // Web 外壳：单例窗口的打开 / 切换 / 关闭都在控制器里；拿不到外壳时回落到系统浏览器。
        _shells = new ShellController(() => _config, OpenInBrowser);

        // 老配置里「点击穿透」和「贴边隐藏」可能同时为真：互斥规则统一在 ConfigStore 里，
        // 归一化后立刻落盘，别把非法组合留在磁盘上。
        if (ConfigStore.Normalize(_config))
        {
            _store.Save();
        }

        // 令牌层必须在任何窗口创建前就绪（配色、字体都从它取）。
        Fluent.Init(_config.Theme);

        _form = new WidgetForm(_config);        _form.StartStopRequested += () => _ = ToggleDshAsync();
        _form.OpenRequested += OpenWebUi;
        _form.LockRequested += ToggleLock;   // 窗口侧由「右键图标」触发，别删这条订阅
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

        // 单击 = 显示挂件（贴边收起了就滑出来，停留 4 秒）；双击 = 打开 DSH Web；右键 = 菜单。
        _tray = new TrayIcon();
        _tray.LeftClick += RevealWidgetFromTray;
        _tray.RightClick += ShowTrayMenu;
        _tray.DoubleClick += OpenWebUi;

        Fluent.Changed += OnThemeChanged;

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
        _probeTimer.Tick += (_, _) => _ = _dsh.RefreshAsync();
        _probeTimer.Start();

        _pluginTimer.Interval = Math.Max(30, _config.PluginIntervalSeconds) * 1000;
        _pluginTimer.Tick += (_, _) => _ = RefreshPluginsAsync();
        _pluginTimer.Start();

        _balanceTimer.Interval = Math.Max(20, _config.BalanceIntervalSeconds) * 1000;
        _balanceTimer.Tick += (_, _) => _ = RefreshBalanceAsync();
        _balanceTimer.Start();

        // 本机压根没有 dsh 时，DshLifecycle 的构造已经写好 hint（卡片上会直接显示）。
        Tick();
        _ = _dsh.RefreshAsync();

        // 冷启动只保证「窗口先出来」：版本 / 插件 / 余额都等界面稳定后再取。
        _warmupTimer.Tick += (_, _) =>
        {
            _warmupTimer.Stop();
            // 会话数在这里取第一次（force 跳过节流），之后交给 1 秒心跳按 15s 节流刷新。
            RefreshActiveSessions(force: true);
            _ = _dsh.LoadVersionAsync();
            _ = RefreshPluginsAsync();
            if (_config.ShowBalance)
            {
                _ = RefreshBalanceAsync();
            }
        };
        _warmupTimer.Start();

        // 法定节假日年表：启动先后台拉一次，之后交给 30 分钟定时器（内部再节流成「成功 20h / 失败 30min」）。
        // 故意不挂在 RefreshBalanceAsync 里：ShowBalance=false 时那条会早退，但峰谷判定仍然要显示。
        _ = ChinaHolidays.EnsureAsync(_cts.Token);
        _holidayTimer.Tick += (_, _) => _ = ChinaHolidays.EnsureAsync(_cts.Token);
        _holidayTimer.Start();

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

        if (Startup.AutoStartDsh && !_dsh.RunningOrOnline)
        {
            StartDsh();
        }

        if (Startup.OpenWebOnStart)
        {
            _ = Task.Delay(400).ContinueWith(
                _ => BeginInvokeOnUi(() => _ = ShowWindowAsync()),
                TaskScheduler.Default);
        }

        if (Startup.OpenSettingsOnStart)
        {
            _ = Task.Delay(350).ContinueWith(
                _ => BeginInvokeOnUi(OpenSettings),
                TaskScheduler.Default);
        }
    }

    // ---------------------------------------------------------------- 定时刷新

    private void Tick()
    {
        // 系统主题/强调色变了就跟着变（每个心跳读一次注册表，开销可忽略）。
        Fluent.Refresh();

        UpdateFullscreenHidden();
        RefreshActiveSessions();
        _dsh.UpdateMetrics();
        _form.SetStatus(BuildSnapshot());
        UpdateTray();

        // 日志清理：24 小时内只跑一次，内部丢后台线程，失败只记日志。
        LogHousekeeping.RunIfDue();
    }

    /// <summary>
    /// 全屏自动隐藏：1 秒一次的前台探测（几次 user32 调用，不值得上线程/定时器）。
    /// 只改窗口可见性，绝不写 <c>_config.Visible</c>——那是「用户想不想看到挂件」的持久意愿，
    /// 自动隐藏不落盘；状态机在 <see cref="FullscreenPolicy"/> 里，这里只执行 Hide()/Show()。
    /// </summary>
    private void UpdateFullscreenHidden()
    {
        var step = _fullscreen.Update(
            _config.HideInFullscreen,
            FullscreenProbe.IsFullscreenForeground(),
            _config.Visible);

        switch (step)
        {
            case FullscreenPolicy.Step.Hide:
                AppPaths.Log("检测到全屏窗口：隐藏挂件");
                _form.Hide();
                break;

            case FullscreenPolicy.Step.Show:
                AppPaths.Log("退出全屏 / 关闭开关：恢复挂件");
                RestoreWidgetWindow();
                break;
        }
    }

    /// <summary>
    /// 把挂件放回屏幕，并重新应用「置底 / 置顶 / 普通」——重新 Show 之后窗口的 Z 序会被重置。
    /// 用户自己不想看到挂件（<c>_config.Visible == false</c>）时什么都不做。
    /// </summary>
    private void RestoreWidgetWindow()
    {
        if (!_config.Visible)
        {
            return;
        }

        _form.Show();
        _form.ApplyZOrderMode(_config);
    }

    /// <summary>
    /// 刷新会话数（15 秒节流）。冷启动不扫盘——第一次取值由预热定时器（1200ms）强制触发，
    /// 保证「窗口先出来」；取到 null 就保持 null，让 UI 显示「—」。
    /// </summary>
    private void RefreshActiveSessions(bool force = false)
    {
        if (!force && _lastSessionProbe == DateTime.MinValue)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastSessionProbe < SessionProbeInterval)
        {
            return;
        }

        _lastSessionProbe = now;
        _activeSessions = SessionProbe.CountActiveSessions();
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
        var now = DateTime.UtcNow;

        return StatusAggregator.Build(new StatusInputs(
            Online: _dsh.Online,
            HostRunning: _dsh.HostRunning,
            Starting: _dsh.Starting,
            Profile: _config.Profile,
            Version: _dsh.Version ?? "",
            Port: _dsh.Port,
            HasAuthenticatedUrl: _dsh.AuthenticatedUrl is not null,
            Uptime: _dsh.Uptime,
            MemoryBytes: _dsh.MemoryBytes,
            CpuPercent: _dsh.CpuPercent,
            PluginTotal: _pluginTotal,
            PluginDisabled: _pluginDisabled,
            PluginNames: _pluginNames,
            PluginScanning: _pluginScanning,
            ShowBalance: _config.ShowBalance,
            Balance: _balance.Balance,
            SpentToday: _balance.SpentToday,
            OffPeak: BalanceService.IsOffPeak(now),
            // 距下一次计价时段切换：闲时中 = 闲时剩余，高峰 = 距下次高峰开始。
            UntilPriceSwitch: BalanceService.UntilNextSwitch(now),
            // 近似会话数（15s 节流缓存）；取不到时是 null，UI 显示「—」。
            ActiveSessions: _activeSessions,
            Hint: _dsh.Hint));
    }

    private void OnStatusChanged() => _tray.Update(_form.Status);

    private void UpdateTray()
    {
        // 自绘菜单每次弹出时按当时状态重建，这里不用同步。
    }

    // ---------------------------------------------------------------- 操作

    private async Task ToggleDshAsync() =>
        await _dsh.ToggleAsync(ConfirmStopExternal).ConfigureAwait(true);

    private void StartDsh()
    {
        if (!_dsh.Start(out var error))
        {
            _tray.ShowBalloonTip(4000, "无法启动 dsh", error ?? "未知错误", ToolTipIcon.Error);
        }
    }

    /// <summary>停止外部实例前的确认：弹窗留在 UI 层，DshLifecycle 只收一个回调。</summary>
    private bool ConfirmStopExternal(DshInstance instance)
    {
        var answer = MessageBox.Show(
            $"这会结束已经在运行的 dsh web（端口 {instance.Port}，pid {instance.ListenerPid}）。\n\n继续吗？",
            "停止 dsh",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        return answer == DialogResult.Yes;
    }

    /// <summary>管道命令（管道线程）→ UI 线程。</summary>
    private void OnCommandReceived(string command)
    {
        if (string.Equals(command, "show", StringComparison.OrdinalIgnoreCase))
        {
            _ = BeginInvokeOnUi(ShowWidget);
            return;
        }

        if (string.Equals(command, "reveal", StringComparison.OrdinalIgnoreCase))
        {
            _ = BeginInvokeOnUi(RevealWidgetFromTray);
            return;
        }

        if (string.Equals(command, "settings", StringComparison.OrdinalIgnoreCase))
        {
            _ = BeginInvokeOnUi(OpenSettings);
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

        if (!_dsh.RunningOrOnline)
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
            if (_dsh.HasUrl)
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
        var url = _dsh.CurrentUrl;
        if (url is null)
        {
            _tray.ShowBalloonTip(3000, "DSH 尚未就绪", "还没发现 dsh web，稍后可以点托盘「打开 Web UI」。", ToolTipIcon.Info);
            return;
        }

        if (_config.UseWebShell && WebShell.IsRuntimeAvailable())
        {
            _shells.Show(url);
            return;
        }

        OpenInBrowser(url);
    }

    private void OpenWebUi()
    {
        var url = _dsh.CurrentUrl;
        if (url is null)
        {
            _tray.ShowBalloonTip(3000, "DSH 未运行", "先点「启动 dsh」，或手动在终端运行 dsh web。", ToolTipIcon.Info);
            return;
        }

        if (_config.UseWebShell && WebShell.IsRuntimeAvailable())
        {
            _shells.Toggle(url);
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

    private void ToggleLock()
    {
        var locked = !_config.ClickThrough;
        _config.ClickThrough = locked;

        // 贴边隐藏 ⇄ 点击穿透互斥（用户定死）：穿透 = 完整卡片常驻，不贴边、不收成图标/拉手。
        // 所以「开穿透」这一半要顺手把贴边隐藏关掉；反方向由 ToggleEdgeHide 负责。
        if (locked)
        {
            _config.EdgeHideEnabled = false;
            if (_form.DockSide != DockSide.None)
            {
                // Undock 会触发 DockStateChanged → SaveDock；最终仍以下面这一次 Save 为准。
                _form.Undock();
            }

            _config.DockSide = DockSide.None;
        }

        _form.ApplyClickThrough(locked);
        _store.Save();
        _form.Invalidate();
        Tick();

        if (locked)
        {
            _tray.ShowBalloonTip(2500, "挂件已锁定",
                "点击会穿透到下层窗口，挂件保持完整卡片，光标附近开一个探照灯；右键托盘图标可解锁。",
                ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// 设置 / 托盘共用的贴边开关。两个方向都要处理互斥：
    /// 开 → 先解除点击穿透（否则 DockTo 会被窗口侧的守卫拒绝），再贴到最近的左右边；
    /// 关 → 解除贴边并记住当前位置。
    /// 调用方传入的是「目标状态」，所以设置面板整表重放时也能正确执行副作用。
    /// </summary>
    private void SetEdgeHide(bool enabled)
    {
        if (!enabled)
        {
            var changed = _config.EdgeHideEnabled || _config.DockSide != DockSide.None;
            _config.EdgeHideEnabled = false;
            if (_config.DockSide != DockSide.None)
            {
                _form.Undock();
            }

            _config.DockSide = DockSide.None;
            _config.X = _form.Left;
            _config.Y = _form.Top;
            if (changed)
            {
                _store.Save();
            }

            return;
        }

        _config.EdgeHideEnabled = true;

        // 已经贴边了就别重复挪窗口（设置面板每次保存都会重放一遍目标状态）。
        if (_config.DockSide != DockSide.None && _form.DockSide != DockSide.None)
        {
            return;
        }

        // 互斥：先关穿透（ApplyClickThrough(false) 不动形态，完整卡片保持），
        // 不先关的话下面的 DockTo 会直接 return。
        if (_config.ClickThrough)
        {
            _config.ClickThrough = false;
            _form.ApplyClickThrough(false);
        }

        var area = Screen.FromControl(_form).WorkingArea;
        var center = _form.Left + (_form.Width / 2);
        var side = center > area.Left + (area.Width / 2) ? DockSide.Right : DockSide.Left;

        // DockTo 会触发 DockStateChanged → SaveDock，这一次写盘就带上了上面所有改动
        // （ClickThrough=false / EdgeHideEnabled=true / DockSide），不用再 Save 一遍。
        _form.DockTo(side, hidden: false);

        // 兜底：万一 DockTo 没生效（理论上不会），至少把开关本身落盘。
        if (_config.DockSide != side)
        {
            _config.DockSide = side;
            _store.Save();
        }
    }

    private void ToggleEdgeHide() => SetEdgeHide(!_config.EdgeHideEnabled);

    // ---------------------------------------------------------------- 设置面板

    /// <summary>打开设置面板（单例；已开着就激活）。</summary>
    private void OpenSettings()
    {
        if (_settingsForm is { IsDisposed: false } existing)
        {
            existing.Activate();
            return;
        }

        var form = new SettingsForm(
            SettingsModel.From(_config),
            AutoStart.GetMode(),
            _balance.KeySourceText,
            ApplySettings,
            OnMenuCommand,
            ClearLogs);

        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_settingsForm, form))
            {
                _settingsForm = null;
            }
        };

        _settingsForm = form;
        form.Show();
    }

    /// <summary>
    /// 设置面板保存后统一应用：先写配置，再按「改了什么」动窗口 / 定时器 / 开机自启。
    /// 路径、端口、Profile 只影响下一次拉起 dsh，这里不做重启（面板里有提示）。
    /// </summary>
    private void ApplySettings(SettingsModel model, AutoStartMode autoStart)
    {
        var before = new
        {
            Theme = _config.Theme,
            AlwaysBottom = _config.AlwaysBottom,
            AlwaysOnTop = _config.AlwaysOnTop,
            ClickThrough = _config.ClickThrough,
            Visible = _config.Visible,
            ShellClose = _config.ShellClose,
            Status = _config.StatusIntervalSeconds,
            Balance = _config.BalanceIntervalSeconds,
            Plugin = _config.PluginIntervalSeconds,
            ShowBalance = _config.ShowBalance,
        };

        model.ApplyTo(_config);
        _store.Save();

        if (_config.Theme != before.Theme)
        {
            Fluent.SetMode(_config.Theme);
        }

        if (_config.ClickThrough != before.ClickThrough)
        {
            _form.ApplyClickThrough(_config.ClickThrough);
        }

        SetEdgeHide(_config.EdgeHideEnabled);

        if (_config.AlwaysBottom != before.AlwaysBottom || _config.AlwaysOnTop != before.AlwaysOnTop)
        {
            _form.ApplyZOrderMode(_config);
        }

        if (_config.Visible != before.Visible)
        {
            if (_config.Visible)
            {
                ShowWidget();
            }
            else
            {
                HideWidget();
            }
        }

        if (_config.ShellClose != before.ShellClose)
        {
            _shells.SyncCloseMode();
        }

        if (_config.StatusIntervalSeconds != before.Status ||
            _config.BalanceIntervalSeconds != before.Balance ||
            _config.PluginIntervalSeconds != before.Plugin)
        {
            ApplyTimerIntervals();
        }

        if (autoStart != AutoStart.GetMode())
        {
            AutoStart.SetMode(autoStart);
        }

        if (_config.ShowBalance && !before.ShowBalance)
        {
            // 从「关」切回「开」：别等下一个余额周期，立刻拉一次。
            _ = RefreshBalanceAsync();
        }

        _form.Invalidate();
        _form.Refresh();
        AppPaths.Log(
            $"设置已保存：主题={_config.Theme}，层级={LayoutName()}，贴边={_config.EdgeHideEnabled}，"
            + $"全屏隐藏={_config.HideInFullscreen}，外壳关闭={_config.ShellClose}");
    }

    private string LayoutName() =>
        _config.AlwaysBottom ? "置底" : _config.AlwaysOnTop ? "置顶" : "普通";

    /// <summary>刷新间隔改了要重启计时器，否则新 Interval 不会立刻重排下一次 Tick。</summary>
    private void ApplyTimerIntervals()
    {
        _probeTimer.Interval = Math.Max(1, _config.StatusIntervalSeconds) * 1000;
        _pluginTimer.Interval = Math.Max(30, _config.PluginIntervalSeconds) * 1000;
        _balanceTimer.Interval = Math.Max(20, _config.BalanceIntervalSeconds) * 1000;

        foreach (var timer in new[] { _probeTimer, _pluginTimer, _balanceTimer })
        {
            if (timer.Enabled)
            {
                timer.Stop();
                timer.Start();
            }
        }
    }

    private void ShowWidget()
    {
        _config.Visible = true;
        _store.Save();

        // 全屏正在生效时手动唤出：记下压制，本次全屏内不再自动藏回去。
        _fullscreen.Wake(FullscreenProbe.IsFullscreenForeground());

        _form.Show();
        _form.ApplyZOrderMode(_config);
        _form.Refresh();
    }

    /// <summary>托盘左键：显示挂件；如果贴着屏幕边缘且已收起，就滑出来并保持几秒。</summary>
    private void RevealWidgetFromTray()
    {
        // 全屏自动隐藏期间唤出：先恢复显示 + 压制本次全屏的自动隐藏，再走原来的贴边滑出。
        if (_fullscreen.Hidden)
        {
            _fullscreen.Wake(FullscreenProbe.IsFullscreenForeground());
            RestoreWidgetWindow();
        }

        if (!_config.Visible)
        {
            ShowWidget();
            return;
        }

        _form.RevealFromDock(holdMs: 4000);
        _form.Refresh();
    }

    private void HideWidget()
    {
        // 用户明确隐藏了：不需要「退出全屏后恢复」再插一脚。
        _fullscreen.UserHid();
        _config.Visible = false;
        _store.Save();
        _form.Hide();
    }

    /// <summary>
    /// 托盘开关「全屏时隐藏」。翻转后立刻探测一次，让开关即时生效：
    /// 关掉时若正被全屏藏着要马上回来；打开时若正处在全屏里要马上藏起来。
    /// </summary>
    private void ToggleFullscreenHide()
    {
        _config.HideInFullscreen = !_config.HideInFullscreen;
        _store.Save();
        _fullscreen.ResetSuppression();
        AppPaths.Log(_config.HideInFullscreen ? "全屏时隐藏：开" : "全屏时隐藏：关");

        UpdateFullscreenHidden();
        _form.Invalidate();
    }

    private void SavePosition()
    {
        if (_form.DockSide != DockSide.None)
        {
            return;
        }

        _config.X = _form.Left;
        _config.Y = _form.Top;
        _store.Save();
    }

    private void SaveDock()
    {
        _config.DockSide = _form.DockSide;
        if (_form.DockSide == DockSide.None)
        {
            _config.X = _form.Left;
            _config.Y = _form.Top;
        }

        _store.Save();
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

        var running = _dsh.RunningOrOnline;
        var state = new MenuState(
            Running: running,
            Starting: _dsh.Starting,
            HasUrl: _dsh.HasUrl,
            LayoutBottom: _config.AlwaysBottom,
            LayoutTop: _config.AlwaysOnTop,
            Locked: _config.ClickThrough,
            EdgeHideEnabled: _config.EdgeHideEnabled,
            // 自动隐藏期间窗口是收着的，菜单行要显示「显示挂件」。
            Visible: _config.Visible && !_fullscreen.Hidden,
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
            case MenuCommand.Open:
                OpenWebUi();
                break;
            case MenuCommand.Start:
                StartDsh();
                break;
            case MenuCommand.Stop:
                _ = StopDshAsync();
                break;
            case MenuCommand.Restart:
                _ = RestartAsync();
                break;
            case MenuCommand.LayoutBottom:
                SetZOrder(bottom: true, top: false);
                break;
            case MenuCommand.LayoutTop:
                SetZOrder(bottom: false, top: true);
                break;
            case MenuCommand.LayoutNormal:
                SetZOrder(bottom: false, top: false);
                break;
            case MenuCommand.Lock:
                ToggleLock();
                break;
            case MenuCommand.Edge:
                ToggleEdgeHide();
                break;
            case MenuCommand.Fullscreen:
                ToggleFullscreenHide();
                break;
            case MenuCommand.Shell:
                _config.ShellClose = _config.ShellClose == ShellCloseMode.Hide
                    ? ShellCloseMode.Close
                    : ShellCloseMode.Hide;
                _shells.SyncCloseMode();

                _store.Save();
                break;
            case MenuCommand.AutoStartDisabled:
                AutoStart.SetMode(AutoStartMode.Disabled);
                break;
            case MenuCommand.AutoStartEnabled:
                AutoStart.SetMode(AutoStartMode.Enabled);
                break;
            case MenuCommand.AutoStartFull:
                AutoStart.SetMode(AutoStartMode.FullAuto);
                break;
            case MenuCommand.ThemeSystem:
                ApplyTheme(AppTheme.System);
                break;
            case MenuCommand.ThemeLight:
                ApplyTheme(AppTheme.Light);
                break;
            case MenuCommand.ThemeDark:
                ApplyTheme(AppTheme.Dark);
                break;
            case MenuCommand.Eye:
                // 菜单行文案与这里的判断必须同口径：全屏自动隐藏期间也算「不可见」，
                // 点了就是唤出（并压制本次全屏的自动隐藏），不能再走 HideWidget。
                if (_config.Visible && !_fullscreen.Hidden)
                {
                    HideWidget();
                }
                else
                {
                    ShowWidget();
                }

                break;
            case MenuCommand.Copy:
                CopyUrl();
                break;
            case MenuCommand.Logs:
                OpenLogFolder();
                break;
            case MenuCommand.Settings:
                OpenSettings();
                break;
            case MenuCommand.Exit:
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
        _store.Save();
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

            _shells.Redraw();

            _trayMenu?.Invalidate(true);
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
        _store.Save();
        _form.ApplyZOrderMode(_config);
    }

    /// <summary>设置面板「清理日志」：轮转 widget.log + 删旧 dsh 日志，结果用气泡提示。</summary>
    private void ClearLogs()
    {
        var (deleted, bytes) = LogHousekeeping.ClearAll();
        _tray.ShowBalloonTip(
            3000,
            "日志已清理",
            $"删除 {deleted} 个旧日志（{bytes / 1024.0:0.0} KB）；当前 widget.log 已轮转为 widget.1.log。",
            ToolTipIcon.Info);
    }

    private void CopyUrl()
    {
        if (_dsh.Port is not { } port)
        {
            return;
        }

        try
        {
            AuthLinks.TokenUrl = _dsh.AuthenticatedUrl ?? _dsh.RememberedUrl;
            var plain = $"http://127.0.0.1:{port}/";
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

    /// <summary>托盘「停止」/ 重启共用：停 dsh（外部实例先确认）。</summary>
    private Task StopDshAsync() => _dsh.StopAsync(ConfirmStopExternal);

    private async Task RestartAsync()
    {
        await StopDshAsync().ConfigureAwait(true);
        await Task.Delay(400).ConfigureAwait(true);
        StartDsh();
    }

    private void RequestExit()
    {
        if (_dsh.HostRunning)
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
                _dsh.StopHost();
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
            _holidayTimer.Dispose();
            _balance.Dispose();
            _dsh.Dispose();
            _tray.Dispose();
            _shells.Dispose();
            _trayMenu?.Dispose();
            _settingsForm?.Dispose();
            _form.Dispose();
        }

        base.Dispose(disposing);
    }

    public bool ExitConfirmed => _exitConfirmed;
}

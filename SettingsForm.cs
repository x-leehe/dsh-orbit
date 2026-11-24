using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 设置面板：WinUI Settings 风格的 640×640 不透明窗口 —— 左侧分区导航 + 右侧单页内容。
///
/// 职责边界：只做「编辑态 + 校验 + 发命令」。真正的应用（切主题、贴边、刷新间隔、开机自启）
/// 由 <see cref="WidgetApp"/> 的 ApplySettings 执行；诊断类动作（复制地址 / 打开日志）
/// 也通过 <see cref="MenuCommand"/> 回给 App，面板不直接碰业务对象。
/// </summary>
[DesignerCategory("Code")]
internal sealed class SettingsForm : Form
{
    // ---------------------------------------------------------------- 布局常量（96 DPI）

    private const int DesignWidth = 640;
    private const int DesignHeight = 640;
    private const int TitleHeight = 52;
    private const int NavWidth = 176;
    private const int ContentX = 200;
    private const int ContentWidth = 412;
    private const int FooterHeight = 64;

    // 分区只留标题：说明性文字（「点确定/应用后生效」之类）已按反馈去掉。
    private static readonly (string Title, string Description)[] SectionInfo =
    [
        ("外观", ""),
        ("挂件", ""),
        ("启动", ""),
        ("DSH", ""),
        ("数据", ""),
    ];

    private readonly SettingsModel _model;
    private readonly Action<SettingsModel, AutoStartMode> _onSave;
    private readonly Action<string>? _onCommand;
    private readonly Action? _onClearLogs;
    private readonly string _keySource;

    private readonly SettingsSegmented _theme = new();
    private readonly SettingsSlider _opacity = new();
    private readonly SettingsSegmented _layout = new();
    private readonly SettingsToggle _edge = new();
    private readonly SettingsToggle _clickThrough = new();
    private readonly SettingsToggle _hideFullscreen = new();
    private readonly SettingsToggle _visible = new();
    private readonly SettingsSegmented _autoStart = new();
    private readonly SettingsField _launchArgs = new();
    private readonly SettingsField _workDir = new();
    private readonly SettingsField _profile = new();
    private readonly SettingsField _port = new();
    private readonly SettingsField _dshPath = new();
    private readonly SettingsToggle _useShell = new();
    private readonly SettingsSegmented _shellClose = new();
    private readonly SettingsToggle _showBalance = new();
    private readonly SettingsField _apiKey = new();
    private readonly SettingsField _statusInterval = new();
    private readonly SettingsField _balanceInterval = new();
    private readonly SettingsField _pluginInterval = new();
    private readonly FluentButton _reset = new();
    private readonly FluentButton _savePreset = new();
    private readonly FluentButton _restorePreset = new();
    private readonly FluentButton _cancel = new();
    private readonly FluentButton _ok = new();
    private readonly FluentButton _apply = new();
    private readonly FluentButton _copy = new();
    private readonly FluentButton _logs = new();
    private readonly FluentButton _clearLogs = new();
    private readonly FluentButton _close = new();
    private readonly List<Control[]> _sectionControls = new();

    /// <summary>「已应用 / 已载入」的那份基线；当前编辑态与它实质相同就置灰「应用」。</summary>
    private SettingsModel _baseline = new();

    /// <summary>已保存的预设（没有就是 null）。内容区底部常驻显示它的时间，替代原来的页脚提示。</summary>
    private SettingsPreset? _savedPreset;

    private int _section;
    private int _navHover = -1;
    private string _error = "";

    public SettingsForm(
        SettingsModel model,
        AutoStartMode autoStart,
        string keySource,
        Action<SettingsModel, AutoStartMode> onSave,
        Action<string>? onCommand = null,
        Action? onClearLogs = null)
    {
        _model = model;
        _onSave = onSave;
        _onCommand = onCommand;
        _onClearLogs = onClearLogs;
        _keySource = keySource;

        Text = "DSH-Orbit 设置";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Fluent.Mica;
        DoubleBuffered = true;
        KeyPreview = true;

        BuildControls();
        BindModel(model, autoStart);
        _baseline = _model.Clone();
        _savedPreset = SettingsPresetStore.Load();
        ApplyWindowSize();
        LayoutControls();
        ApplySectionVisibility();
        HookDirtyTracking();
        UpdateDirty();

        Fluent.Changed += OnThemeChanged;
    }

    // ---------------------------------------------------------------- 控件装配

    private void BuildControls()
    {
        _theme.Label = "主题";
        _theme.Labels = ["跟随系统", "浅色", "深色"];

        _opacity.Label = "不透明度";
        _opacity.Minimum = SettingsModel.MinOpacity;
        _opacity.Maximum = SettingsModel.MaxOpacity;
        _opacity.Step = 0.05;

        _layout.Label = "位置";
        _layout.Labels = ["置底", "置顶", "普通"];

        _edge.Label = "贴边隐藏";
        _edge.Description = "吸附到屏幕左右边缘，鼠标移到边缘滑出";

        _clickThrough.Label = "点击穿透";
        _clickThrough.Description = "点击落到下层窗口；开启后自动关闭贴边隐藏";

        _hideFullscreen.Label = "全屏时隐藏";
        _hideFullscreen.Description = "前台有全屏窗口（含无边框全屏）时整窗收起";

        _visible.Label = "显示挂件";
        _visible.Description = "关掉后只能从托盘唤出";

        _autoStart.Label = "开机启动";
        _autoStart.Labels = ["停用", "启用", "全自动"];

        _launchArgs.Label = "启动参数";
        _launchArgs.Placeholder = "web --no-open";
        _launchArgs.InlineLabel = true;

        _workDir.Label = "工作目录";
        _workDir.Placeholder = @"留空用用户主目录";
        _workDir.InlineLabel = true;
        _workDir.ShowBrowse = true;
        _workDir.BrowseRequested += (_, _) => BrowseFolder(_workDir);

        _profile.Label = "Profile";
        _profile.Placeholder = "web";
        _profile.InlineLabel = true;

        _port.Label = "首选端口";
        _port.Placeholder = "3080";
        _port.InlineLabel = true;

        _dshPath.Label = "dsh 可执行文件";
        _dshPath.Placeholder = "留空从 PATH 查找";
        _dshPath.InlineLabel = true;
        _dshPath.ShowBrowse = true;
        _dshPath.BrowseRequested += (_, _) => BrowseFile(_dshPath);

        _useShell.Label = "用内置外壳打开";
        _useShell.Description = "关掉后点地址会用默认浏览器打开";

        _shellClose.Label = "外壳关闭时";
        _shellClose.Labels = ["隐藏", "关闭"];

        _showBalance.Label = "显示余额";
        _showBalance.Description = "浮层与托盘不显示余额和今日已用";

        _apiKey.Label = "DeepSeek API Key";
        _apiKey.Placeholder = "留空自动查找";
        _apiKey.InlineLabel = true;
        _apiKey.Password = true;

        _statusInterval.Label = "状态刷新（秒）";
        _balanceInterval.Label = "余额刷新（秒）";
        _pluginInterval.Label = "插件扫描（秒）";

        _reset.Text = "恢复默认";
        _reset.Role = SurfaceRole.Standard;
        _reset.Click += (_, _) => ResetToDefaults();

        _savePreset.Text = "保存预设";
        _savePreset.Role = SurfaceRole.Standard;
        _savePreset.Click += (_, _) => SavePreset();

        _restorePreset.Text = "恢复预设";
        _restorePreset.Role = SurfaceRole.Standard;
        _restorePreset.Enabled = SettingsPresetStore.Exists();
        _restorePreset.Click += (_, _) => RestorePreset();

        _ok.Text = "确定";
        _ok.Role = SurfaceRole.Accent;
        _ok.IsDefault = true;
        _ok.Click += (_, _) => Apply(close: true);

        _cancel.Text = "取消";
        _cancel.Role = SurfaceRole.Standard;
        _cancel.Click += (_, _) => Close();

        _apply.Text = "应用";
        _apply.Role = SurfaceRole.Standard;
        _apply.Click += (_, _) => Apply(close: false);

        _copy.Text = "复制地址";
        _copy.Role = SurfaceRole.Standard;
        _copy.Width = 96;
        _copy.Click += (_, _) => _onCommand?.Invoke(MenuCommand.Copy);

        _logs.Text = "打开日志";
        _logs.Role = SurfaceRole.Standard;
        _logs.Width = 96;
        _logs.Click += (_, _) => _onCommand?.Invoke(MenuCommand.Logs);

        _clearLogs.Text = "清理日志";
        _clearLogs.Role = SurfaceRole.Standard;
        _clearLogs.Width = 96;
        _clearLogs.Click += (_, _) => _onClearLogs?.Invoke();

        _close.Text = "";
        _close.IconName = "x";
        _close.Role = SurfaceRole.Subtle;
        _close.Width = 32;
        _close.Height = 32;
        _close.Click += (_, _) => Close();

        var widget = new Control[] { _layout, _edge, _clickThrough, _hideFullscreen, _visible };
        var launch = new Control[] { _autoStart, _launchArgs, _workDir };
        var dsh = new Control[] { _profile, _port, _dshPath, _useShell, _shellClose, _showBalance };
        var data = new Control[] { _apiKey, _statusInterval, _balanceInterval, _pluginInterval, _copy, _logs, _clearLogs };
        var appearance = new Control[] { _theme, _opacity };

        _sectionControls.AddRange([appearance, widget, launch, dsh, data]);

        foreach (var group in _sectionControls)
        {
            foreach (var control in group)
            {
                Controls.Add(control);
            }
        }

        Controls.Add(_reset);
        Controls.Add(_savePreset);
        Controls.Add(_restorePreset);
        Controls.Add(_ok);
        Controls.Add(_cancel);
        Controls.Add(_apply);
        Controls.Add(_close);
    }

    // ---------------------------------------------------------------- 数据绑定

    private void BindModel(SettingsModel model, AutoStartMode autoStart)
    {
        _theme.SelectedIndex = (int)model.Theme;
        _opacity.Value = model.Opacity;
        _layout.SelectedIndex = model.AlwaysBottom ? 0 : model.AlwaysOnTop ? 1 : 2;
        _edge.Checked = model.EdgeHideEnabled;
        _clickThrough.Checked = model.ClickThrough;
        _hideFullscreen.Checked = model.HideInFullscreen;
        _visible.Checked = model.Visible;
        _autoStart.SelectedIndex = (int)autoStart;
        _launchArgs.Value = model.LaunchArguments;
        _workDir.Value = model.WorkingDirectory;
        _profile.Value = model.Profile;
        _port.Value = model.PreferredPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _dshPath.Value = model.DshExePath;
        _useShell.Checked = model.UseWebShell;
        _shellClose.SelectedIndex = model.ShellClose == ShellCloseMode.Hide ? 0 : 1;
        _showBalance.Checked = model.ShowBalance;
        _apiKey.Value = model.DeepSeekApiKey;
        _statusInterval.Value = model.StatusIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _balanceInterval.Value = model.BalanceIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _pluginInterval.Value = model.PluginIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool TryCollect(out string error)
    {
        error = "";
        _model.Theme = (AppTheme)_theme.SelectedIndex;
        _model.Opacity = _opacity.Value;
        _model.AlwaysBottom = _layout.SelectedIndex == 0;
        _model.AlwaysOnTop = _layout.SelectedIndex == 1;
        _model.EdgeHideEnabled = _edge.Checked;
        _model.ClickThrough = _clickThrough.Checked;
        _model.HideInFullscreen = _hideFullscreen.Checked;
        _model.Visible = _visible.Checked;
        _model.LaunchArguments = _launchArgs.Value;
        _model.WorkingDirectory = _workDir.Value;
        _model.Profile = _profile.Value;
        _model.DshExePath = _dshPath.Value;
        _model.UseWebShell = _useShell.Checked;
        _model.ShellClose = _shellClose.SelectedIndex == 0 ? ShellCloseMode.Hide : ShellCloseMode.Close;
        _model.ShowBalance = _showBalance.Checked;
        _model.DeepSeekApiKey = _apiKey.Value;

        if (!int.TryParse(_port.Value.Trim(), out var port))
        {
            error = "首选端口需要是数字";
            return false;
        }

        _model.PreferredPort = port;

        if (!int.TryParse(_statusInterval.Value.Trim(), out var status) ||
            !int.TryParse(_balanceInterval.Value.Trim(), out var balance) ||
            !int.TryParse(_pluginInterval.Value.Trim(), out var plugin))
        {
            error = "刷新间隔需要是数字（秒）";
            return false;
        }

        _model.StatusIntervalSeconds = status;
        _model.BalanceIntervalSeconds = balance;
        _model.PluginIntervalSeconds = plugin;
        return true;
    }

    /// <summary>「确定」= 应用后关窗；「应用」= 应用但保持打开（工具栏 / 面板继续用）。</summary>
    private void Apply(bool close)
    {
        if (!TryCollect(out var error) || !_model.TryValidate(out error))
        {
            SetError(error);
            return;
        }

        _onSave(_model, (AutoStartMode)_autoStart.SelectedIndex);

        // 应用后基线前移：没再改东西「应用」立刻回到置灰。
        _baseline = _model.Clone();
        SetError("");
        UpdateDirty();

        if (close)
        {
            Close();
        }
    }

    /// <summary>
    /// 「保存预设」：把当前编辑态存到 `preset.json`（单槽位、覆盖式）。
    /// 不要求先「应用」，但会先校验，免得存进一份非法配置。
    /// </summary>
    private void SavePreset()
    {
        if (!TryCollect(out var error) || !_model.TryValidate(out error))
        {
            SetError(error);
            return;
        }

        if (SettingsPresetStore.Exists())
        {
            var answer = MessageBox.Show(
                this,
                "已经有保存过的预设了。要用当前设置覆盖它吗？",
                "保存预设",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        SettingsPresetStore.Save(_model);
        _savedPreset = SettingsPresetStore.Load();
        _restorePreset.Enabled = _savedPreset is not null;
        SetError("");
    }

    /// <summary>「恢复预设」：把预设载入面板，点「确定 / 应用」才真正生效。</summary>
    private void RestorePreset()
    {
        var preset = _savedPreset ?? SettingsPresetStore.Load();
        if (preset is null)
        {
            SetError("还没有保存过预设");
            return;
        }

        BindModel(preset.Settings, (AutoStartMode)_autoStart.SelectedIndex);
        SetError("");
        UpdateDirty();
    }

    private void ResetToDefaults()
    {
        var defaults = SettingsModel.Defaults();
        BindModel(defaults, (AutoStartMode)_autoStart.SelectedIndex);
        SetError("");
        UpdateDirty();
    }

    private void SetError(string error)
    {
        _error = error;
        Invalidate();
    }

    /// <summary>所有编辑控件的内容变化 → 重算「应用」按钮是否可用。</summary>
    private void HookDirtyTracking()
    {
        foreach (var group in _sectionControls)
        {
            foreach (var control in group)
            {
                switch (control)
                {
                    case SettingsToggle toggle:
                        toggle.CheckedChanged += (_, _) => UpdateDirty();
                        break;
                    case SettingsSegmented segmented:
                        segmented.SelectionChanged += (_, _) => UpdateDirty();
                        break;
                    case SettingsSlider slider:
                        slider.ValueChanged += (_, _) => UpdateDirty();
                        break;
                    case SettingsField field:
                        field.ValueChanged += (_, _) => UpdateDirty();
                        break;
                }
            }
        }
    }

    /// <summary>
    /// 「应用」只在实质改动时可用：收集当前编辑态与基线比较；
    /// 收集失败（例如端口不是数字）也算有改动，好让用户点进去看到校验错误。
    /// </summary>
    private void UpdateDirty()
    {
        _apply.Enabled = !TryCollect(out _) || !_model.SameAs(_baseline);
    }

    // ---------------------------------------------------------------- 浏览对话框

    private void BrowseFile(SettingsField field)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 dsh 可执行文件",
            Filter = "可执行文件|*.exe;*.cmd;*.bat|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            field.Value = dialog.FileName;
        }
    }

    private void BrowseFolder(SettingsField field)
    {
        using var dialog = new FolderBrowserDialog();
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            field.Value = dialog.SelectedPath;
        }
    }

    // ---------------------------------------------------------------- 布局

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)MathF.Round(value * DpiScale);

    private float Sf(float value) => value * DpiScale;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000; // CS_DROPSHADOW：无边框窗口的柔和投影
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.EnableRoundedCorners(Handle);

        // 注意：这里**不要**改 ClientSize。在 OnHandleCreated 里嵌套设置客户区，会让 WinForms
        // 随后用「先前算好的外框尺寸」再套一次，无边框窗口会凭空高出 39px（标题栏的高度）。
        LayoutControls();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);

        // WinForms 会按比例缩我们的 ClientSize；这里按新 DPI 重算一遍，避免累积误差。
        ApplyWindowSize();
        LayoutControls();
        Invalidate();
    }

    /// <summary>窗口尺寸 = 设计尺寸 × DPI。只在建句柄之前和 DPI 变化时调用。</summary>
    private void ApplyWindowSize()
    {
        var size = new Size(S(DesignWidth), S(DesignHeight));
        if (ClientSize != size)
        {
            ClientSize = size;
        }
    }

    private void LayoutControls()
    {
        // WinUI Settings 的卡片行：开关 / 分段 / 滑杆 / 行内字段 都是 64 高，行内边距 16。
        // 数据分区的三个间隔字段仍是「标签在上」的 76 高，因为它们三列并排。
        _theme.Bounds = InCard(116, 64);
        _opacity.Bounds = InCard(180, 64);

        _layout.Bounds = InCard(116, 64);
        _edge.Bounds = InCard(180, 64);
        _clickThrough.Bounds = InCard(244, 64);
        _hideFullscreen.Bounds = InCard(308, 64);
        _visible.Bounds = InCard(372, 64);

        _autoStart.Bounds = InCard(116, 64);
        _launchArgs.Bounds = InCard(180, 64);
        _workDir.Bounds = InCard(244, 64);

        _profile.Bounds = InCard(116, 64);
        _port.Bounds = InCard(180, 64);
        _dshPath.Bounds = InCard(244, 64);
        _useShell.Bounds = InCard(308, 64);
        _shellClose.Bounds = InCard(372, 64);
        _showBalance.Bounds = InCard(436, 64);

        _apiKey.Bounds = InCard(116, 64);

        var intervalWidth = (ContentWidth - 32 - 16) / 3;
        _statusInterval.Bounds = new Rectangle(S(ContentX + 16), S(206), S(intervalWidth), S(76));
        _balanceInterval.Bounds = new Rectangle(S(ContentX + 16 + intervalWidth + 8), S(206), S(intervalWidth), S(76));
        _pluginInterval.Bounds = new Rectangle(S(ContentX + 16 + ((intervalWidth + 8) * 2)), S(206), S(intervalWidth), S(76));
        _copy.Bounds = new Rectangle(S(ContentX + 16), S(294), S(96), S(32));
        _logs.Bounds = new Rectangle(S(ContentX + 16 + 104), S(294), S(96), S(32));
        _clearLogs.Bounds = new Rectangle(S(ContentX + 16 + 208), S(294), S(96), S(32));

        // 页脚
        var footerTop = DesignHeight - FooterHeight;
        var buttonY = footerTop + 20;
        // 左侧：恢复默认 / 保存预设 / 恢复预设
        _reset.Bounds = new Rectangle(S(24), S(buttonY), S(96), S(32));
        _savePreset.Bounds = new Rectangle(S(24 + 96 + 8), S(buttonY), S(96), S(32));
        _restorePreset.Bounds = new Rectangle(S(24 + ((96 + 8) * 2)), S(buttonY), S(96), S(32));

        // 右侧：确定 / 取消 / 应用（左→右，Windows 属性表口径；确定是默认按钮）
        var applyX = DesignWidth - 24 - 88;
        var cancelX = applyX - 8 - 88;
        var okX = cancelX - 8 - 88;
        _ok.Bounds = new Rectangle(S(okX), S(buttonY), S(88), S(32));
        _cancel.Bounds = new Rectangle(S(cancelX), S(buttonY), S(88), S(32));
        _apply.Bounds = new Rectangle(S(applyX), S(buttonY), S(88), S(32));

        // 标题栏关闭按钮
        _close.Bounds = new Rectangle(S(DesignWidth - 20 - 32), S(10), S(32), S(32));
    }

    /// <summary>卡片内的行：内容区左右各留 16 内边距。</summary>
    private Rectangle InCard(int y, int height) =>
        new(S(ContentX + 16), S(y), S(ContentWidth - 32), S(height));

    private void ApplySectionVisibility()
    {
        for (var i = 0; i < _sectionControls.Count; i++)
        {
            var visible = i == _section;
            foreach (var control in _sectionControls[i])
            {
                control.Visible = visible;
            }
        }

        Invalidate();
    }

    internal void SelectSection(int index)
    {
        if (index < 0 || index >= _sectionControls.Count || _section == index)
        {
            return;
        }

        _section = index;
        ApplySectionVisibility();
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g, crisp: true);
        var scale = DpiScale;

        using (var background = new SolidBrush(Fluent.Mica))
        {
            g.FillRectangle(background, ClientRectangle);
        }

        DrawTitle(g, scale);
        DrawNav(g, scale);
        DrawSectionHeader(g, scale);
        DrawFooter(g, scale);
    }

    private void DrawTitle(Graphics g, float scale)
    {
        // WinUI 标题栏只有一行 14px 应用名；页面自己的 28px 页头在 DrawSectionHeader 里。
        Fluent.Text(
            g,
            "设置",
            TextRole.Body,
            true,
            Fluent.TextPrimary,
            new RectangleF(S(24), S(16), S(DesignWidth - 120), S(22)),
            StringAlignment.Near,
            StringTrimming.None,
            scale);
    }

    private void DrawNav(Graphics g, float scale)
    {
        // 导航与内容之间一条竖分隔线
        Fluent.Fill(
            g,
            new RectangleF(S(NavWidth), S(TitleHeight + 12), MathF.Max(1f, scale), S(DesignHeight - TitleHeight - 12 - FooterHeight)),
            Fluent.DividerColor);

        for (var i = 0; i < SectionInfo.Length; i++)
        {
            var rect = NavRect(i);
            var selected = i == _section;
            var hovered = i == _navHover;

            // WinUI 的导航选中态是「轻微浅底 + 左侧 3px 强调条」，不是整块描边。
            if (selected)
            {
                using (var path = Fluent.Rounded(rect, Fluent.RControl))
                {
                    Fluent.Fill(g, path, Fluent.OverState(Fluent.Mica, VisualState.Hover));
                }

                var bar = new RectangleF(rect.X + (1f * scale), rect.Y + ((rect.Height - (16f * scale)) / 2f), 3f * scale, 16f * scale);
                using (var path = Fluent.Rounded(bar, 1.5f * scale))
                {
                    Fluent.Fill(g, path, Fluent.AccentText);
                }
            }
            else if (hovered)
            {
                using var path = Fluent.Rounded(rect, Fluent.RControl);
                Fluent.Fill(g, path, Fluent.HoverLayer);
            }

            Fluent.Text(
                g,
                SectionInfo[i].Title,
                TextRole.Body,
                false,
                selected ? Fluent.TextPrimary : Fluent.TextSecondary,
                new RectangleF(rect.X + (16f * scale), rect.Y, rect.Width - (24f * scale), rect.Height),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }

        // 预设状态放在导航列表下方的空白区（两行，内容区保持干净）；没有预设就不画。
        if (_savedPreset is { } preset)
        {
            var navTextLeft = 24f;
            var navTextWidth = NavWidth - navTextLeft - 12f;
            var statusTop = DesignHeight - FooterHeight - 52;

            Fluent.Text(
                g,
                "有已保存的预设，时间：",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(navTextLeft), S(statusTop), S(navTextWidth), S(16)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);

            Fluent.Text(
                g,
                $"{preset.SavedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(navTextLeft), S(statusTop + 16), S(navTextWidth), S(16)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    /// <summary>每个分区的卡片高度（设计像素）：16 内边距 + 各行高度。</summary>
    private static int CardHeight(int section) => section switch
    {
        0 => 160,   // 分段 64 + 滑杆 64
        1 => 352,   // 分段 64 + 4×开关 64
        2 => 224,   // 分段 64 + 2×行内字段 64
        3 => 416,   // 3×行内字段 64 + 开关 64 + 分段 64 + 开关 64
        4 => 244,   // 行内字段 64 + 来源 18 + 间隔 76 + 按钮 32
        _ => 160,
    };

    /// <summary>卡片内的行分隔线（设计 y）。</summary>
    private static int[] CardDividers(int section) => section switch
    {
        0 => [180],
        1 => [180, 244, 308, 372],
        2 => [180, 244],
        3 => [180, 244, 308, 372, 436],
        4 => [200, 288],
        _ => [],
    };

    private void DrawSectionHeader(Graphics g, float scale)
    {
        var info = SectionInfo[_section];

        // 页面页头：WinUI Settings 的 28px 大标题，位于标题栏之下、卡片之上。
        Fluent.Text(
            g,
            info.Title,
            TextRole.Title,
            true,
            Fluent.TextPrimary,
            new RectangleF(S(ContentX), S(48), S(ContentWidth), S(44)),
            StringAlignment.Near,
            StringTrimming.None,
            scale);

        if (!string.IsNullOrEmpty(info.Description))
        {
            Fluent.Text(
                g,
                info.Description,
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(ContentX), S(84), S(ContentWidth), S(20)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }

        // 设置分组 = 一张 WinUI 卡片（8 圆角、Layer 底、上亮下暗描边），行间用 inset 分隔线。
        Fluent.Card(
            g,
            new RectangleF(S(ContentX), S(100), S(ContentWidth), S(CardHeight(_section))),
            Sf(Fluent.RCard),
            0,
            Fluent.Layer);

        foreach (var y in CardDividers(_section))
        {
            Fluent.Divider(
                g,
                new RectangleF(S(ContentX + 16), S(y), S(ContentWidth - 32), MathF.Max(1f, scale)));
        }

        // 数据分区：API Key 的来源提示 + 延迟生效的约束
        if (_section == 4)
        {
            Fluent.Text(
                g,
                $"当前 Key 来源：{_keySource}",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(ContentX + 16), S(182), S(ContentWidth - 32), S(18)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);

            Fluent.Text(
                g,
                "路径、端口与 Profile 在下次启动生效。",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(ContentX + 16), S(360), S(ContentWidth - 32), S(18)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    private void DrawFooter(Graphics g, float scale)
    {
        var top = DesignHeight - FooterHeight;
        Fluent.Fill(
            g,
            new RectangleF(S(20), S(top), S(DesignWidth - 40), MathF.Max(1f, scale)),
            Fluent.DividerColor);

        if (!string.IsNullOrEmpty(_error))
        {
            // 只留校验错误；普通操作提示不再占用页脚（预设信息放在内容区底部常驻）。
            Fluent.Text(
                g,
                _error,
                TextRole.Caption,
                false,
                Fluent.Critical,
                new RectangleF(S(24), S(top + 2), S(DesignWidth - 48), S(16)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    private Rectangle NavRect(int index)
    {
        // WinUI 设置导航：40 高、4 间距、左侧 12 边距；从标题栏下方 56 开始。
        var top = 56 + (index * (40 + 4));
        return new Rectangle(S(12), S(top), S(NavWidth - 24), S(40));
    }

    // ---------------------------------------------------------------- 鼠标 / 键盘

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = -1;
        for (var i = 0; i < SectionInfo.Length; i++)
        {
            if (NavRect(i).Contains(e.Location))
            {
                hover = i;
                break;
            }
        }

        if (hover != _navHover)
        {
            _navHover = hover;
            Cursor = hover >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_navHover != -1)
        {
            _navHover = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        for (var i = 0; i < SectionInfo.Length; i++)
        {
            if (NavRect(i).Contains(e.Location))
            {
                SelectSection(i);
                return;
            }
        }

        // 标题栏拖动：把非客户区命中交给系统，和普通窗口一样拖动。
        if (e.Y < S(TitleHeight))
        {
            Native.ReleaseCapture();
            _ = Native.SendMessage(Handle, Native.WM_NCLBUTTONDOWN, Native.HTCAPTION, IntPtr.Zero);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Control && e.KeyCode == Keys.S)
        {
            e.Handled = true;
            Apply(close: true);
        }
    }

    private void OnThemeChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        BackColor = Fluent.Mica;
        Invalidate(true);
    }

    /// <summary>供离屏预览截一帧（真实表单 + 子控件）。</summary>
    internal Bitmap? Snapshot()
    {
        if (!IsHandleCreated)
        {
            _ = Handle;
        }

        CreateControl();
        var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnThemeChanged;
        }

        base.Dispose(disposing);
    }
}


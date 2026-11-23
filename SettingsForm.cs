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

    private static readonly (string Title, string Description)[] SectionInfo =
    [
        ("外观", "主题与挂件不透明度，保存后立即生效。"),
        ("挂件", "层级、贴边与全屏行为；贴边隐藏与点击穿透互斥。"),
        ("启动", "开机启动方式；启动参数与工作目录影响下一次拉起 dsh。"),
        ("DSH", "Profile、端口与本地 dsh；路径类改动下次启动生效。"),
        ("数据", "余额与刷新频率；API Key 留空时读环境变量或 ~\\.dsh\\.credentials.yaml。"),
    ];

    private readonly SettingsModel _model;
    private readonly Action<SettingsModel, AutoStartMode> _onSave;
    private readonly Action<string>? _onCommand;
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
    private readonly FluentButton _cancel = new();
    private readonly FluentButton _save = new();
    private readonly FluentButton _copy = new();
    private readonly FluentButton _logs = new();
    private readonly FluentButton _close = new();
    private readonly List<Control[]> _sectionControls = new();

    private int _section;
    private int _navHover = -1;
    private string _error = "";

    public SettingsForm(
        SettingsModel model,
        AutoStartMode autoStart,
        string keySource,
        Action<SettingsModel, AutoStartMode> onSave,
        Action<string>? onCommand = null)
    {
        _model = model;
        _onSave = onSave;
        _onCommand = onCommand;
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
        ApplyWindowSize();
        LayoutControls();
        ApplySectionVisibility();

        Fluent.Changed += OnThemeChanged;
    }

    // ---------------------------------------------------------------- 控件装配

    private void BuildControls()
    {
        _theme.Labels = ["跟随系统", "浅色", "深色"];

        _opacity.Label = "不透明度";
        _opacity.Minimum = SettingsModel.MinOpacity;
        _opacity.Maximum = SettingsModel.MaxOpacity;
        _opacity.Step = 0.05;

        _layout.Labels = ["置底", "置顶", "普通"];

        _edge.Label = "贴边隐藏";
        _edge.Description = "吸附到屏幕左右边缘，鼠标移到边缘滑出";

        _clickThrough.Label = "点击穿透";
        _clickThrough.Description = "点击落到下层窗口；开启后自动关闭贴边隐藏";

        _hideFullscreen.Label = "全屏时隐藏";
        _hideFullscreen.Description = "前台有全屏窗口（含无边框全屏）时整窗收起";

        _visible.Label = "显示挂件";
        _visible.Description = "关掉后只能从托盘唤出";

        _autoStart.Labels = ["停用", "启用", "全自动"];

        _launchArgs.Label = "启动参数";
        _launchArgs.Placeholder = "web --no-open";

        _workDir.Label = "工作目录（留空用用户主目录）";
        _workDir.Placeholder = @"C:\Users\me\work";
        _workDir.ShowBrowse = true;
        _workDir.BrowseRequested += (_, _) => BrowseFolder(_workDir);

        _profile.Label = "Profile";
        _profile.Placeholder = "web";

        _port.Label = "首选端口";
        _port.Placeholder = "3080";

        _dshPath.Label = "dsh 可执行文件（留空自动从 PATH 查找）";
        _dshPath.Placeholder = "dsh.exe";
        _dshPath.ShowBrowse = true;
        _dshPath.BrowseRequested += (_, _) => BrowseFile(_dshPath);

        _useShell.Label = "用内置外壳打开";
        _useShell.Description = "关掉后点地址会用默认浏览器打开";

        _shellClose.Labels = ["隐藏", "关闭"];

        _showBalance.Label = "显示余额";
        _showBalance.Description = "关闭后不请求余额接口，也不显示今日已用";

        _apiKey.Label = "DeepSeek API Key（留空自动查找）";
        _apiKey.Placeholder = "sk-…";
        _apiKey.Password = true;

        _statusInterval.Label = "状态刷新（秒）";
        _balanceInterval.Label = "余额刷新（秒）";
        _pluginInterval.Label = "插件扫描（秒）";

        _reset.Text = "恢复默认";
        _reset.Role = SurfaceRole.Standard;
        _reset.Click += (_, _) => ResetToDefaults();

        _cancel.Text = "取消";
        _cancel.Role = SurfaceRole.Standard;
        _cancel.Click += (_, _) => Close();

        _save.Text = "保存";
        _save.Role = SurfaceRole.Accent;
        _save.IsDefault = true;
        _save.Click += (_, _) => Save();

        _copy.Text = "复制地址";
        _copy.Role = SurfaceRole.Standard;
        _copy.Width = 96;
        _copy.Click += (_, _) => _onCommand?.Invoke(MenuCommand.Copy);

        _logs.Text = "打开日志";
        _logs.Role = SurfaceRole.Standard;
        _logs.Width = 96;
        _logs.Click += (_, _) => _onCommand?.Invoke(MenuCommand.Logs);

        _close.Text = "";
        _close.IconName = "x";
        _close.Role = SurfaceRole.Subtle;
        _close.Width = 32;
        _close.Height = 32;
        _close.Click += (_, _) => Close();

        var widget = new Control[] { _layout, _edge, _clickThrough, _hideFullscreen, _visible };
        var launch = new Control[] { _autoStart, _launchArgs, _workDir };
        var dsh = new Control[] { _profile, _port, _dshPath, _useShell, _shellClose, _showBalance };
        var data = new Control[] { _apiKey, _statusInterval, _balanceInterval, _pluginInterval, _copy, _logs };
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
        Controls.Add(_cancel);
        Controls.Add(_save);
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

    private void Save()
    {
        if (!TryCollect(out var error) || !_model.TryValidate(out error))
        {
            SetError(error);
            return;
        }

        _onSave(_model, (AutoStartMode)_autoStart.SelectedIndex);
        Close();
    }

    private void ResetToDefaults()
    {
        var defaults = SettingsModel.Defaults();
        BindModel(defaults, (AutoStartMode)_autoStart.SelectedIndex);
        SetError("");
    }

    private void SetError(string error)
    {
        _error = error;
        Invalidate();
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
        using var dialog = new FolderBrowserDialog { Description = "选择 dsh 的工作目录" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            field.Value = dialog.SelectedPath;
        }
    }

    // ---------------------------------------------------------------- 布局

    private float DpiScale => DeviceDpi / 96f;

    private int S(float value) => (int)MathF.Round(value * DpiScale);

    private Rectangle InContent(int y, int height) =>
        new(S(ContentX), S(y), S(ContentWidth), S(height));

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
        // 分区内容（同一套坐标，切分区时只改可见性）。
        // 每组顶部留 20px 给「主题 / 位置 / 开机启动 / 外壳关闭时」这类 12px 小标题。
        _theme.Bounds = InContent(152, 32);
        _opacity.Bounds = InContent(204, 40);

        _layout.Bounds = InContent(152, 32);
        _edge.Bounds = InContent(200, 52);
        _clickThrough.Bounds = InContent(256, 52);
        _hideFullscreen.Bounds = InContent(312, 52);
        _visible.Bounds = InContent(368, 52);

        _autoStart.Bounds = InContent(152, 32);
        _launchArgs.Bounds = InContent(204, 56);
        _workDir.Bounds = InContent(268, 56);

        _profile.Bounds = InContent(136, 56);
        _port.Bounds = InContent(200, 56);
        _dshPath.Bounds = InContent(264, 56);
        _useShell.Bounds = InContent(328, 52);
        _shellClose.Bounds = InContent(408, 32);
        _showBalance.Bounds = InContent(448, 52);

        _apiKey.Bounds = InContent(136, 56);
        var intervalWidth = (ContentWidth - 16) / 3;
        _statusInterval.Bounds = new Rectangle(S(ContentX), S(232), S(intervalWidth), S(56));
        _balanceInterval.Bounds = new Rectangle(S(ContentX + intervalWidth + 8), S(232), S(intervalWidth), S(56));
        _pluginInterval.Bounds = new Rectangle(S(ContentX + ((intervalWidth + 8) * 2)), S(232), S(intervalWidth), S(56));
        _copy.Bounds = new Rectangle(S(ContentX), S(304), S(96), S(32));
        _logs.Bounds = new Rectangle(S(ContentX + 104), S(304), S(96), S(32));

        // 页脚
        var footerTop = DesignHeight - FooterHeight;
        var buttonY = footerTop + 20;
        _reset.Bounds = new Rectangle(S(24), S(buttonY), S(104), S(32));
        _save.Bounds = new Rectangle(S(DesignWidth - 24 - 88), S(buttonY), S(88), S(32));
        _cancel.Bounds = new Rectangle(S(DesignWidth - 24 - 88 - 8 - 88), S(buttonY), S(88), S(32));

        // 标题栏关闭按钮
        _close.Bounds = new Rectangle(S(DesignWidth - 20 - 32), S(10), S(32), S(32));
    }

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
        Fluent.Text(
            g,
            "设置",
            TextRole.Subtitle,
            true,
            Fluent.TextPrimary,
            new RectangleF(S(20), S(10), S(DesignWidth - 120), S(TitleHeight - 20)),
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

            if (selected)
            {
                using var path = Fluent.Rounded(rect, Fluent.RControl);
                Fluent.Fill(g, path, Fluent.Mix(Fluent.ControlFill, Fluent.AccentFill, 0.24f));
                Fluent.Stroke(g, path, Fluent.Mix(Fluent.StrokeStrong, Fluent.AccentFill, 0.7f), 1f);
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
                selected,
                selected ? Fluent.TextPrimary : Fluent.TextSecondary,
                new RectangleF(rect.X + S(14), rect.Y, rect.Width - S(24), rect.Height),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    private void DrawSectionHeader(Graphics g, float scale)
    {
        var info = SectionInfo[_section];

        Fluent.Text(
            g,
            info.Title,
            TextRole.Subtitle,
            true,
            Fluent.TextPrimary,
            new RectangleF(S(ContentX), S(60), S(ContentWidth), S(30)),
            StringAlignment.Near,
            StringTrimming.None,
            scale);

        Fluent.Text(
            g,
            info.Description,
            TextRole.Caption,
            false,
            Fluent.TextTertiary,
            new RectangleF(S(ContentX), S(92), S(ContentWidth), S(32)),
            StringAlignment.Near,
            StringTrimming.EllipsisCharacter,
            scale);

        // 分段控件自己不带标签，这里补 12px 小标题（与文本字段的标签同一档）。
        switch (_section)
        {
            case 0:
                FieldLabel(g, "主题", 132, scale);
                break;
            case 1:
                FieldLabel(g, "位置", 132, scale);
                break;
            case 2:
                FieldLabel(g, "开机启动", 132, scale);
                break;
            case 3:
                FieldLabel(g, "外壳关闭时", 384, scale);
                break;
        }

        // 数据分区：API Key 的来源提示 + 三个间隔的说明
        if (_section == 4)
        {
            Fluent.Text(
                g,
                $"当前 Key 来源：{_keySource}",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(ContentX), S(196), S(ContentWidth), S(20)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);

            Fluent.Text(
                g,
                "间隔改动保存后立即重新计时；路径 / 端口 / Profile 下次启动生效。",
                TextRole.Caption,
                false,
                Fluent.TextTertiary,
                new RectangleF(S(ContentX), S(352), S(ContentWidth), S(20)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    private void FieldLabel(Graphics g, string text, int y, float scale)
    {
        Fluent.Text(
            g,
            text,
            TextRole.Caption,
            false,
            Fluent.TextTertiary,
            new RectangleF(S(ContentX), S(y), S(ContentWidth), S(16)),
            StringAlignment.Near,
            StringTrimming.None,
            scale);
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
            Fluent.Text(
                g,
                _error,
                TextRole.Caption,
                false,
                Fluent.Critical,
                new RectangleF(S(24), S(top - 28), S(DesignWidth - 48), S(20)),
                StringAlignment.Near,
                StringTrimming.EllipsisCharacter,
                scale);
        }
    }

    private Rectangle NavRect(int index)
    {
        var top = 72 + (index * (38 + 6));
        return new Rectangle(S(20), S(top), S(NavWidth - 36), S(38));
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
            Save();
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


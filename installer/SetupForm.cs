using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace DshOrbit.Setup;

/// <summary>
/// 单窗口安装界面：不走向导，一张脸完成「选路径 + 选选项 + 装」，装完原地切成完成态。
/// 窗口是无边框、自绘标题栏（和正文共用 Fluent 令牌），所以深浅色下标题栏不会和内容打架，
/// DrawToBitmap 截出来的图也和真实外观一致；同时仍给 DWM 设深色/圆角属性。
/// 除输入框与按钮用 FluentControls 外，收音机/复选框/进度条/标题栏按钮都是本工程自绘。
/// </summary>
[DesignerCategory("Code")]
internal sealed class SetupForm : Form
{
    private const int LogicalWidth = 640;
    private const int LogicalHeight = 440;
    private const int TitleBarHeight = 40;

    private enum Screen
    {
        Idle,
        Installing,
        Done,
    }

    private enum Hit
    {
        None,
        User,
        Machine,
        Portable,
        Desktop,
        Start,
        Run,
        Minimize,
        Close,
    }

    private readonly bool _autoStart;
    private readonly string? _logPath;
    private readonly FluentTextBox _path;
    private readonly FluentButton _browse;
    private readonly FluentButton _install;
    private readonly FluentButton _cancel;

    private InstallMode _mode;
    private bool _pathEdited;
    private bool _desktop = true;
    private bool _startMenu = true;
    private bool _runAfter = true;
    private Screen _screen = Screen.Idle;
    private Hit _hover = Hit.None;
    private Hit _captionHover = Hit.None;
    private int _progress;
    private string _status = "准备就绪";
    private bool _statusCritical;
    private string _installedDir = "";
    private string _installedVersion = AppInfo.Version;
    private bool _installedPortable;
    private bool _busy;
    private bool _updatingPath;
    private bool _elevating;

    private Rectangle _titleBar = Rectangle.Empty;
    private Rectangle _minButton = Rectangle.Empty;
    private Rectangle _closeButton = Rectangle.Empty;
    private Rectangle _logo = Rectangle.Empty;
    private Rectangle _title = Rectangle.Empty;
    private Rectangle _version = Rectangle.Empty;
    private Rectangle _pathLabel = Rectangle.Empty;
    private Rectangle[] _radioRows = Array.Empty<Rectangle>();
    private Rectangle _desktopBox = Rectangle.Empty;
    private Rectangle _startBox = Rectangle.Empty;
    private Rectangle _progressLabel = Rectangle.Empty;
    private Rectangle _bar = Rectangle.Empty;
    private Rectangle _statusRect = Rectangle.Empty;
    private Rectangle _doneIcon = Rectangle.Empty;
    private Rectangle _doneTitle = Rectangle.Empty;
    private Rectangle _doneInfo = Rectangle.Empty;
    private Rectangle _doneNote = Rectangle.Empty;
    private Rectangle _runBox = Rectangle.Empty;
    private int _dividerY;

    public SetupForm(CliOptions cli)
    {
        _autoStart = cli.Elevated;
        _logPath = cli.LogPath;
        _mode = cli.ModeExplicit
            ? cli.Mode
            : InstallerSession.IsAdmin() ? InstallMode.Machine : InstallMode.User;
        _pathEdited = !string.IsNullOrWhiteSpace(cli.TargetDir);
        _desktop = !cli.NoDesktop;
        _startMenu = !cli.NoStart;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw,
            true);

        AutoScaleMode = AutoScaleMode.None;
        Text = "DSH-Orbit 安装程序";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Fluent.Mica;
        ForeColor = Fluent.TextPrimary;
        KeyPreview = true;
        DoubleBuffered = true;

        try
        {
            Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? Application.ExecutablePath);
        }
        catch
        {
            // 没有图标也能装，忽略。
        }

        _path = new FluentTextBox();
        _browse = new FluentButton { Text = "浏览…" };
        _install = new FluentButton { Text = "安装", Role = SurfaceRole.Accent, IsDefault = true };
        _cancel = new FluentButton { Text = "取消" };

        Controls.AddRange(new Control[] { _path, _browse, _install, _cancel });

        // FluentTextBox 不暴露内部 TextBox 的 TextChanged，这里挂一下，用来判断
        // 「用户手动改过路径」——改过之后切换安装范围就不再自动覆盖路径。
        var inner = _path.Controls.OfType<TextBox>().FirstOrDefault();
        if (inner is not null)
        {
            inner.TextChanged += (_, _) =>
            {
                if (!_updatingPath)
                {
                    _pathEdited = true;
                }
            };
        }

        _browse.Click += OnBrowse;
        _install.Click += (_, _) =>
        {
            if (_screen == Screen.Done)
            {
                Finish();
            }
            else
            {
                StartInstall();
            }
        };
        _cancel.Click += (_, _) => RequestClose();

        _path.Value = string.IsNullOrWhiteSpace(cli.TargetDir)
            ? InstallOptions.DefaultDirectory(_mode)
            : cli.TargetDir!;

        Fluent.Changed += OnFluentChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Fluent.Changed -= OnFluentChanged;
        }

        base.Dispose(disposing);
    }

    /// <summary>无边框窗口给系统投影（有边框的方案会自带）。</summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
            return cp;
        }
    }

    // ---------------------------------------------------------------- 尺寸 / 布局

    private float Pixels => DeviceDpi / 96f;

    private int P(float value) => (int)Math.Round(value * Pixels);

    // 自绘标题栏以外，仍按 Fluent 主题设置 DWM 属性：Win11 圆角 + 沉浸式深色
    // （深色属性对无边框窗口没有可见效果，但主题切换时保持设置一致，也方便将来换回系统标题栏）。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            var dark = Fluent.IsDark ? 1 : 0;
            var result = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
            if (result != 0)
            {
                // Win10 1909 之前用 19 这个属性号。
                result = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
            }

            var round = DwmcpRound;
            DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref round, sizeof(int));
            SetupLog.Info($"窗口外观：深色模式={Fluent.IsDark}，DWM 返回 {result}。");
        }
        catch
        {
            // Windows 10 上个别 DWM 属性不存在：不影响安装流程。
        }
    }

    private void OnFluentChanged()
    {
        if (IsDisposed)
        {
            return;
        }

        ApplyWindowChrome();
        if (IsHandleCreated)
        {
            Invalidate();
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyLogicalSize();
        LayoutUi();
        if (_autoStart)
        {
            BeginInvoke(new Action(StartInstall));
        }
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        ApplyLogicalSize();
        LayoutUi();
        Invalidate();
    }

    private void ApplyLogicalSize()
    {
        var target = new Size(P(LogicalWidth), P(LogicalHeight));
        if (ClientSize != target)
        {
            ClientSize = target;
        }
    }

    private void LayoutUi()
    {
        var pad = P(24);
        var right = ClientSize.Width - pad;
        var buttonHeight = P(32);

        // 自绘标题栏：logo + 产品名在左，版本号在窗口按钮左边，右边是最小化/关闭。
        _titleBar = new Rectangle(0, 0, ClientSize.Width, P(TitleBarHeight));
        _dividerY = P(TitleBarHeight);

        var captionSize = P(30);
        _closeButton = new Rectangle(ClientSize.Width - P(10) - captionSize, P(5), captionSize, captionSize);
        _minButton = new Rectangle(_closeButton.X - P(4) - captionSize, P(5), captionSize, captionSize);
        _logo = new Rectangle(P(16), P(10), P(20), P(20));
        _title = new Rectangle(P(44), P(4), P(240), P(32));
        _version = new Rectangle(_minButton.X - P(8) - P(300), P(10), P(300), P(20));

        _pathLabel = new Rectangle(pad, P(56), P(240), P(20));

        var browseWidth = P(92);
        var pathWidth = right - pad - browseWidth - P(12);
        _path.SetBounds(pad, P(80), pathWidth, buttonHeight);
        _browse.SetBounds(pad + pathWidth + P(12), P(80), browseWidth, buttonHeight);
        ScalePathFont();

        _radioRows = new[]
        {
            new Rectangle(P(16), P(128), ClientSize.Width - P(32), P(28)),
            new Rectangle(P(16), P(160), ClientSize.Width - P(32), P(28)),
            new Rectangle(P(16), P(192), ClientSize.Width - P(32), P(28)),
        };

        var checksY = P(232);
        _desktopBox = new Rectangle(pad, checksY, P(250), P(24));
        _startBox = new Rectangle(pad + P(260), checksY, P(300), P(24));

        _progressLabel = new Rectangle(pad, P(312), P(240), P(20));
        _bar = new Rectangle(pad, P(340), P(360), P(8));
        _statusRect = new Rectangle(pad + P(372), P(330), right - pad - P(372), P(24));

        var buttonWidth = P(96);
        var buttonY = ClientSize.Height - P(24) - buttonHeight;
        _cancel.SetBounds(right - buttonWidth, buttonY, buttonWidth, buttonHeight);
        _install.SetBounds(right - buttonWidth - P(8) - buttonWidth, buttonY, buttonWidth, buttonHeight);

        _doneIcon = new Rectangle(pad, P(96), P(40), P(40));
        _doneTitle = new Rectangle(pad + P(52), P(92), P(500), P(48));
        _doneInfo = new Rectangle(pad, P(156), right - pad, P(20));
        _doneNote = new Rectangle(pad, P(180), right - pad, P(20));
        _runBox = new Rectangle(pad, P(220), P(360), P(24));

        LayoutButtons();
    }

    /// <summary>FluentTextBox 内部字号是 96 DPI 定死的，高 DPI 下要手动跟着缩。</summary>
    private void ScalePathFont()
    {
        try
        {
            var field = typeof(FluentTextBox).GetField("_inner", BindingFlags.NonPublic | BindingFlags.Instance);
            if (field?.GetValue(_path) is TextBox inner)
            {
                inner.Font = Fluent.Font(TextRole.Body, strong: false, scale: Pixels);
                _path.PerformLayout();
            }
        }
        catch
        {
            // 反射失败就保持默认字号（只会影响 DPI 缩放下的输入框文字）。
        }
    }

    private void LayoutButtons()
    {
        _install.Text = _screen switch
        {
            Screen.Done => "完成",
            Screen.Installing => "安装中…",
            _ => "安装",
        };
        _install.Role = _screen == Screen.Installing ? SurfaceRole.Standard : SurfaceRole.Accent;
        _cancel.Visible = _screen != Screen.Done;
        _path.Visible = _screen != Screen.Done;
        _browse.Visible = _screen != Screen.Done;
        _install.Enabled = !_busy;
        _cancel.Enabled = !_busy;
        _path.Enabled = !_busy;
        _browse.Enabled = !_busy;
        Invalidate();
    }

    // ---------------------------------------------------------------- 窗口拖动

    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtCaption = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // 无边框窗口：标题栏区域交给系统当标题栏（拖动、双击不最大化），
        // 窗口按钮保持 HTCLIENT，点击才会进 OnMouseClick。
        if (m.Msg == WmNcHitTest && m.Result.ToInt64() == HtClient)
        {
            var packed = m.LParam.ToInt64();
            var screenPoint = new Point(unchecked((short)(packed & 0xFFFF)), unchecked((short)((packed >> 16) & 0xFFFF)));
            var client = PointToClient(screenPoint);
            if (_titleBar.Contains(client) && !_closeButton.Contains(client) && !_minButton.Contains(client))
            {
                m.Result = (IntPtr)HtCaption;
            }
        }
    }

    // ---------------------------------------------------------------- 绘制

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 全部在 OnPaint 里自绘，避免闪烁。
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g);
        Fluent.Fill(g, ClientRectangle, Fluent.Mica);

        DrawTitleBar(g);

        if (_screen == Screen.Done)
        {
            DrawDone(g);
        }
        else
        {
            DrawSetup(g);
        }
    }

    private void DrawTitleBar(Graphics g)
    {
        OrbitMark.Draw(g, _logo, Fluent.AccentText, 2f);
        Fluent.Text(g, "DSH-Orbit", TextRole.Body, strong: true, Fluent.TextPrimary, _title);
        Fluent.Text(g, $"安装程序 · 版本 {AppInfo.Version}", TextRole.Caption, strong: false,
            Fluent.TextTertiary, _version, StringAlignment.Far);

        DrawCaptionButton(g, _minButton, "minus", _captionHover == Hit.Minimize, P(14));
        DrawCaptionButton(g, _closeButton, "x", _captionHover == Hit.Close, P(14));

        Fluent.Divider(g, new RectangleF(0, _dividerY, ClientSize.Width, 1));
    }

    private void DrawCaptionButton(Graphics g, Rectangle button, string icon, bool hovered, int iconSize)
    {
        if (hovered)
        {
            using var back = Fluent.Rounded(button, Fluent.RControl);
            Fluent.Fill(g, back, Fluent.HoverLayer);
        }

        var box = new RectangleF(
            button.X + ((button.Width - iconSize) / 2f),
            button.Y + ((button.Height - iconSize) / 2f),
            iconSize,
            iconSize);
        Icons.Draw(g, icon, box, hovered ? Fluent.TextPrimary : Fluent.TextSecondary, thickness: 1.8f);
    }

    private void DrawSetup(Graphics g)
    {
        Fluent.SectionLabel(g, _pathLabel, "安装到：", Pixels);

        DrawRadio(g, 0, Hit.User, _mode == InstallMode.User,
            "仅为我安装", "（%LOCALAPPDATA%\\Programs，不需要管理员）");
        DrawRadio(g, 1, Hit.Machine, _mode == InstallMode.Machine,
            "为所有用户安装", "（Program Files，需要管理员）");
        DrawRadio(g, 2, Hit.Portable, _mode == InstallMode.Portable,
            "便携模式", "（绿色版：只解压，不写注册表、不建快捷方式）");

        var shortcutsEnabled = _mode != InstallMode.Portable && !_busy;
        var shortcutsChecked = _mode != InstallMode.Portable;
        DrawCheck(g, _desktopBox, shortcutsChecked && _desktop, shortcutsEnabled,
            _hover == Hit.Desktop, "创建桌面快捷方式");
        DrawCheck(g, _startBox, shortcutsChecked && _startMenu, shortcutsEnabled,
            _hover == Hit.Start, "创建开始菜单项");

        Fluent.Divider(g, new RectangleF(P(24), P(272), ClientSize.Width - P(48), 1));
        Fluent.SectionLabel(g, _progressLabel, "安装进度", Pixels);

        DrawProgress(g);

        Fluent.Text(g, _status, TextRole.Caption, strong: false,
            _statusCritical ? Fluent.Critical : Fluent.TextSecondary,
            _statusRect, StringAlignment.Near, StringTrimming.EllipsisCharacter);
    }

    private void DrawRadio(Graphics g, int index, Hit hit, bool selected, string label, string hint)
    {
        var row = _radioRows[index];
        var enabled = !_busy;
        var hovered = _hover == hit && enabled;

        if (hovered)
        {
            using var back = Fluent.Rounded(row, Fluent.RControl);
            Fluent.Fill(g, back, Fluent.HoverLayer);
        }

        var radius = Pixels * 8f;
        var centerX = row.X + (Pixels * 9f);
        var centerY = row.Y + (row.Height / 2f);
        var ringColor = !enabled
            ? Fluent.TextDisabled
            : selected ? Fluent.AccentFill : Fluent.TextTertiary;

        using (var pen = new Pen(ringColor, Math.Max(1f, Pixels * 1.4f)))
        {
            g.DrawEllipse(pen, centerX - radius, centerY - radius, radius * 2, radius * 2);
        }

        if (selected)
        {
            var dot = Pixels * 4f;
            using var brush = new SolidBrush(enabled ? Fluent.AccentFill : Fluent.TextDisabled);
            g.FillEllipse(brush, centerX - dot, centerY - dot, dot * 2, dot * 2);
        }

        var textColor = enabled ? Fluent.TextPrimary : Fluent.TextDisabled;
        var textX = row.X + (Pixels * 26f);
        var labelWidth = Fluent.Measure(g, label, TextRole.Body, strong: false, scale: Pixels) + (Pixels * 2f);
        Fluent.Text(g, label, TextRole.Body, strong: false, textColor,
            new RectangleF(textX, row.Y, labelWidth, row.Height));

        Fluent.Text(g, hint, TextRole.Caption, strong: false,
            enabled ? Fluent.TextTertiary : Fluent.TextDisabled,
            new RectangleF(textX + labelWidth, row.Y, row.Right - textX - labelWidth, row.Height),
            StringAlignment.Near, StringTrimming.EllipsisCharacter);
    }

    private void DrawCheck(Graphics g, Rectangle box, bool checkedState, bool enabled, bool hovered, string label)
    {
        if (hovered && enabled)
        {
            using var back = Fluent.Rounded(
                new Rectangle(box.X - P(6), box.Y, box.Width + P(6), box.Height), Fluent.RControl);
            Fluent.Fill(g, back, Fluent.HoverLayer);
        }

        var size = P(16);
        var glyph = new RectangleF(box.X, box.Y + ((box.Height - size) / 2f), size, size);
        using var path = Fluent.Rounded(glyph, Fluent.RControl);

        if (checkedState)
        {
            Fluent.Fill(g, path, enabled ? Fluent.AccentFill : Fluent.Mix(Fluent.Mica, Fluent.AccentFill, 0.35f));
            Fluent.Stroke(g, path, enabled ? Fluent.AccentFill : Fluent.TextDisabled, 1f);
            Icons.Draw(g, "check", RectangleF.Inflate(glyph, -Pixels * 2.5f, -Pixels * 2.5f),
                enabled ? Fluent.OnAccent : Fluent.TextDisabled, thickness: 3f);
        }
        else
        {
            Fluent.Fill(g, path, Fluent.ControlFill);
            Fluent.Stroke(g, path, enabled ? Fluent.StrokeStrong : Fluent.StrokeColor, 1f);
        }

        Fluent.Text(g, label, TextRole.Body, strong: false,
            enabled ? Fluent.TextPrimary : Fluent.TextDisabled,
            new RectangleF(glyph.Right + (Pixels * 10f), box.Y, box.Width - size - (Pixels * 10f), box.Height));
    }

    private void DrawProgress(Graphics g)
    {
        using var track = Fluent.Rounded(_bar, _bar.Height / 2f);
        Fluent.Fill(g, track, Fluent.WithAlpha(Fluent.TextPrimary, 0.10f));

        var ratio = Math.Clamp(_progress / 100f, 0f, 1f);
        if (ratio > 0f)
        {
            var fillWidth = Math.Max(_bar.Height, _bar.Width * ratio);
            var state = g.Save();
            g.SetClip(track, CombineMode.Intersect);
            using var fill = Fluent.Rounded(
                new RectangleF(_bar.X, _bar.Y, fillWidth, _bar.Height), _bar.Height / 2f);
            Fluent.Fill(g, fill, Fluent.AccentFill);
            g.Restore(state);
        }
    }

    private void DrawDone(Graphics g)
    {
        Icons.Draw(g, "check", _doneIcon, Fluent.Success, thickness: 3f);
        Fluent.Text(g, "安装完成", TextRole.Title, strong: true, Fluent.TextPrimary, _doneTitle);
        Fluent.Text(g, $"已安装到 {_installedDir}（版本 {_installedVersion}）", TextRole.Body, strong: false,
            Fluent.TextSecondary, _doneInfo, StringAlignment.Near, StringTrimming.EllipsisCharacter);

        var note = _installedPortable
            ? "便携模式：数据保存在程序旁的 data 目录，没有写注册表、没有建快捷方式。"
            : "已登记到「应用和功能」，可以从那里卸载。";
        Fluent.Text(g, note, TextRole.Caption, strong: false, Fluent.TextTertiary,
            _doneNote, StringAlignment.Near, StringTrimming.EllipsisCharacter);

        DrawCheck(g, _runBox, _runAfter, enabled: !_busy, hovered: _hover == Hit.Run, "立即运行 DSH-Orbit");
    }

    // ---------------------------------------------------------------- 交互

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var caption = HitTestCaption(e.Location);
        if (caption != _captionHover)
        {
            _captionHover = caption;
            Invalidate();
        }

        var hit = HitTest(e.Location);
        if (hit != _hover)
        {
            _hover = hit;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover != Hit.None || _captionHover != Hit.None)
        {
            _hover = Hit.None;
            _captionHover = Hit.None;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        if (_closeButton.Contains(e.Location))
        {
            RequestClose();
            return;
        }

        if (_minButton.Contains(e.Location))
        {
            WindowState = FormWindowState.Minimized;
            return;
        }

        if (_busy || _elevating)
        {
            return;
        }

        switch (HitTest(e.Location))
        {
            case Hit.User:
                SelectMode(InstallMode.User);
                break;
            case Hit.Machine:
                SelectMode(InstallMode.Machine);
                break;
            case Hit.Portable:
                SelectMode(InstallMode.Portable);
                break;
            case Hit.Desktop:
                if (_mode != InstallMode.Portable)
                {
                    _desktop = !_desktop;
                    Invalidate();
                }

                break;
            case Hit.Start:
                if (_mode != InstallMode.Portable)
                {
                    _startMenu = !_startMenu;
                    Invalidate();
                }

                break;
            case Hit.Run:
                if (_screen == Screen.Done)
                {
                    _runAfter = !_runAfter;
                    Invalidate();
                }

                break;
        }
    }

    private Hit HitTestCaption(Point point)
    {
        if (_closeButton.Contains(point))
        {
            return Hit.Close;
        }

        if (_minButton.Contains(point))
        {
            return Hit.Minimize;
        }

        return Hit.None;
    }

    private Hit HitTest(Point point)
    {
        if (_screen == Screen.Done)
        {
            return _runBox.Contains(point) ? Hit.Run : Hit.None;
        }

        if (_busy || _radioRows.Length != 3)
        {
            return Hit.None;
        }

        if (_radioRows[0].Contains(point))
        {
            return Hit.User;
        }

        if (_radioRows[1].Contains(point))
        {
            return Hit.Machine;
        }

        if (_radioRows[2].Contains(point))
        {
            return Hit.Portable;
        }

        if (_mode != InstallMode.Portable)
        {
            if (_desktopBox.Contains(point))
            {
                return Hit.Desktop;
            }

            if (_startBox.Contains(point))
            {
                return Hit.Start;
            }
        }

        return Hit.None;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            RequestClose();
            return true;
        }

        if (keyData == Keys.Enter)
        {
            if (_screen == Screen.Done)
            {
                Finish();
            }
            else if (_screen == Screen.Idle && !_busy && !_elevating)
            {
                StartInstall();
            }

            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>关闭窗口：安装进行中不允许直接关掉（避免留下半装状态）。</summary>
    private void RequestClose()
    {
        if (_busy)
        {
            return;
        }

        Close();
    }

    private void SelectMode(InstallMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;

        // 用户手动改过路径就不再覆盖；没改过则跟着范围走。
        if (!_pathEdited)
        {
            SetSuggestedPath();
        }

        Invalidate();
    }

    private void SetSuggestedPath()
    {
        _updatingPath = true;
        try
        {
            _path.Value = InstallOptions.DefaultDirectory(_mode);
        }
        finally
        {
            _updatingPath = false;
        }

        _pathEdited = false;
    }

    private void OnBrowse(object? sender, EventArgs e)
    {
        if (_busy)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择 DSH-Orbit 的安装目录",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(_path.Value) ? _path.Value : InstallOptions.DefaultDirectory(_mode),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _updatingPath = true;
        try
        {
            _path.Value = dialog.SelectedPath;
        }
        finally
        {
            _updatingPath = false;
        }

        _pathEdited = true;
        Invalidate();
    }

    // ---------------------------------------------------------------- 安装流程

    private void StartInstall()
    {
        if (_busy || _elevating || _screen != Screen.Idle)
        {
            return;
        }

        string dir;
        try
        {
            dir = InstallOptions.NormalizeDirectory(_path.Value);
        }
        catch (SetupException ex)
        {
            ShowError(ex.Message);
            return;
        }

        var options = new InstallOptions
        {
            Mode = _mode,
            TargetDir = dir,
            DesktopShortcut = _mode != InstallMode.Portable && _desktop,
            StartMenuShortcut = _mode != InstallMode.Portable && _startMenu,
            Silent = false,
            LogPath = _logPath,
            Elevated = _autoStart,
        };

        // 为所有用户安装但当前不是管理员：按清单 asInvoker 的约定，重启自己请求提权。
        if (_mode == InstallMode.Machine && !InstallerSession.IsAdmin())
        {
            _elevating = true;
            _busy = true;
            LayoutButtons();

            var exitCode = InstallerSession.RelaunchElevated(options, wait: false);
            if (exitCode is null)
            {
                _elevating = false;
                _busy = false;
                SetStatus("已取消管理员授权，未安装。", critical: true);
                LayoutButtons();
                return;
            }

            SetupLog.Info("已交给提权后的安装程序继续安装，本窗口退出。");
            Close();
            return;
        }

        RunInstall(options);
    }

    private void RunInstall(InstallOptions options)
    {
        _screen = Screen.Installing;
        _busy = true;
        _progress = 0;
        _statusCritical = false;
        SetStatus("正在准备安装…");
        LayoutButtons();

        var progress = new Progress<InstallProgress>(p =>
        {
            _progress = Math.Clamp(p.Percent, 0, 100);
            SetStatus(p.Status);
            Invalidate();
        });

        var engine = new InstallEngine(options, ((IProgress<InstallProgress>)progress).Report, ConfirmCloseRunningApp);
        SetupLog.Info($"界面开始安装：{options.TargetDir}");

        Task.Run(() =>
        {
            try
            {
                var result = engine.Run();
                BeginInvoke(new Action(() => OnInstallDone(result)));
            }
            catch (Exception ex)
            {
                BeginInvoke(new Action(() => OnInstallFailed(ex)));
            }
        });
    }

    private bool ConfirmCloseRunningApp(string message)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return false;
        }

        try
        {
            return (bool)Invoke(new Func<bool>(() =>
                MessageBox.Show(
                    this,
                    message,
                    "DSH-Orbit 安装程序",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button1) == DialogResult.Yes));
        }
        catch
        {
            return false;
        }
    }

    private void OnInstallDone(InstallResult result)
    {
        _busy = false;
        _screen = Screen.Done;
        _progress = 100;
        _statusCritical = false;
        SetStatus("安装完成");
        _installedDir = result.Directory;
        _installedVersion = result.Version;
        _installedPortable = result.Portable;
        _runAfter = true;
        _hover = Hit.None;
        LayoutButtons();
        _install.Focus();
    }

    private void OnInstallFailed(Exception ex)
    {
        _busy = false;
        _screen = Screen.Idle;
        _progress = 0;
        SetStatus("安装失败", critical: true);
        LayoutButtons();

        var message = ex is SetupException ? ex.Message : $"安装失败：{ex.Message}";
        SetupLog.Error("界面安装失败", ex);
        try
        {
            MessageBox.Show(
                this,
                message + $"\n\n详细信息已写入日志：\n{SetupLog.DefaultFile}",
                "DSH-Orbit 安装程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 窗口已关闭时忽略。
        }
    }

    private void Finish()
    {
        if (_runAfter && _installedDir.Length > 0)
        {
            var exe = Path.Combine(_installedDir, AppInfo.ExeName);
            try
            {
                if (File.Exists(exe))
                {
                    Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                    SetupLog.Info($"安装完成后启动：{exe}");
                }
            }
            catch (Exception ex)
            {
                SetupLog.Warn($"启动 DSH-Orbit 失败：{ex.Message}");
            }
        }

        Close();
    }

    private void ShowError(string message)
    {
        SetupLog.Error($"界面提示：{message}");
        _statusCritical = true;
        SetStatus(message.Replace('\n', ' '), critical: true);
        MessageBox.Show(this, message, "DSH-Orbit 安装程序", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private void SetStatus(string text, bool critical = false)
    {
        _status = text;
        _statusCritical = critical;
        Invalidate();
    }

    // ---------------------------------------------------------------- 截图（开发用）

    /// <summary>--shot-state：把窗口摆成某个状态再截屏，用于自查排版。</summary>
    internal void ApplyShotState(string state)
    {
        switch (state)
        {
            case "progress":
                _mode = InstallMode.User;
                _pathEdited = false;
                SetSuggestedPath();
                _screen = Screen.Installing;
                _busy = true;
                _progress = 62;
                SetStatus("正在复制文件…");
                LayoutButtons();
                break;

            case "portable":
                _mode = InstallMode.Portable;
                _pathEdited = false;
                SetSuggestedPath();
                break;

            case "machine":
                _mode = InstallMode.Machine;
                _pathEdited = false;
                SetSuggestedPath();
                break;

            case "done":
                _screen = Screen.Done;
                _installedDir = InstallOptions.DefaultDirectory(InstallMode.User);
                _installedVersion = "0.3.0";
                _progress = 100;
                SetStatus("安装完成");
                LayoutButtons();
                break;

            case "error":
                SetStatus("安装失败：没有权限写入安装目录", critical: true);
                break;
        }
    }
}

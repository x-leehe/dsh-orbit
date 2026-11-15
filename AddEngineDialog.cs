using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 「添加搜索引擎」对话框：WinUI ContentDialog 形态——
/// 8px 圆角、24px 内边距、Subtitle 标题、底部右对齐的两个按钮。
/// </summary>
internal sealed class AddEngineDialog : Form
{
    private readonly FluentTextBox _name;
    private readonly FluentTextBox _template;
    private readonly FluentButton _ok;
    private readonly FluentButton _cancel;
    private string? _error;

    public AddEngineDialog()
    {
        Text = "添加搜索引擎";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Fluent.Layer;
        ForeColor = Fluent.TextPrimary;
        DoubleBuffered = true;
        KeyPreview = true;

        _name = new FluentTextBox { PlaceholderText = "例如：必应" };
        _template = new FluentTextBox
        {
            Value = "https://www.bing.com/search?q={q}",
            PlaceholderText = "https://example.com/search?q={q}",
        };

        _ok = new FluentButton
        {
            Text = "添加",
            Role = SurfaceRole.Accent,
            IsDefault = true,
        };

        _cancel = new FluentButton { Text = "取消" };

        _ok.Click += (_, _) => TryAccept();
        _cancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.Add(_name);
        Controls.Add(_template);
        Controls.Add(_ok);
        Controls.Add(_cancel);

        LayoutChildren();

        Shown += (_, _) => _name.FocusInput();

        // 校验不过就不关闭对话框（ShowDialog 的调用方只看 DialogResult）。
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK || Validate(out var error))
            {
                return;
            }

            e.Cancel = true;
            DialogResult = DialogResult.None;
            _error = error;
            Invalidate();
        };
    }

    public string EngineName => _name.Value.Trim();

    public string Template
    {
        get
        {
            var template = _template.Value.Trim();
            if (!template.Contains("{q}", StringComparison.OrdinalIgnoreCase))
            {
                template += template.Contains('?') ? "&q={q}" : "?q={q}";
            }

            return template;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.EnableRoundedCorners(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        var scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * scale);

        const int pad = 24;
        const int width = 440;
        const int height = 248;

        ClientSize = new Size(S(width), S(height));

        var fieldWidth = S(width - (2 * pad));
        _name.Bounds = new Rectangle(S(pad), S(82), fieldWidth, S(32));
        _template.Bounds = new Rectangle(S(pad), S(144), fieldWidth, S(32));

        var buttonWidth = S(88);
        var buttonHeight = S(32);
        _ok.Bounds = new Rectangle(S(width - pad) - buttonWidth, S(200), buttonWidth, buttonHeight);
        _cancel.Bounds = new Rectangle(
            _ok.Left - S(8) - buttonWidth, S(200), buttonWidth, buttonHeight);

        _ok.Font = Fluent.Font(TextRole.Body, false, scale);
        _cancel.Font = Fluent.Font(TextRole.Body, false, scale);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Fluent.AntiAlias(g);

        var scale = DeviceDpi / 96f;
        int S(int v) => (int)MathF.Round(v * scale);
        const int pad = 24;

        Fluent.Card(g, new RectangleF(0.5f, 0.5f, ClientSize.Width - 1, ClientSize.Height - 1),
            Fluent.RCard, 0, Fluent.Layer);

        Fluent.Text(g, "添加搜索引擎", TextRole.Subtitle, true, Fluent.TextPrimary,
            new RectangleF(S(pad), S(20), S(320), Fluent.LineHeight(TextRole.Subtitle)), StringAlignment.Near,
            StringTrimming.None, scale);

        Fluent.Text(g, "名称", TextRole.Caption, false, Fluent.TextSecondary,
            new RectangleF(S(pad), S(62), S(200), S(16)), StringAlignment.Near, StringTrimming.None, scale);

        Fluent.Text(g, "搜索地址（用 {q} 代表关键词）", TextRole.Caption, false, Fluent.TextSecondary,
            new RectangleF(S(pad), S(124), S(320), S(16)), StringAlignment.Near, StringTrimming.None, scale);

        if (!string.IsNullOrEmpty(_error))
        {
            Fluent.Text(g, _error, TextRole.Caption, false, Fluent.Critical,
                new RectangleF(S(pad), S(180), S(392), S(16)), StringAlignment.Near,
                StringTrimming.EllipsisCharacter, scale);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            TryAccept();
            e.Handled = true;
        }
    }

    // 让标题空白处可以拖动对话框（无边框窗口没有系统标题栏）。
    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        if (m.Msg == WM_NCHITTEST && m.Result == HTCLIENT && Cursor.Position.Y - Top < (int)(56 * DeviceDpi / 96f))
        {
            m.Result = HTCAPTION;
        }
    }

    private void TryAccept()
    {
        if (!Validate(out var error))
        {
            _error = error;
            Invalidate();
            return;
        }

        _error = null;
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool Validate(out string? error)
    {
        error = null;
        if (_name.Value.Trim().Length == 0)
        {
            error = "名称不能为空";
            return false;
        }

        var template = Template;
        if (!Uri.TryCreate(template.Replace("{q}", "test"), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "搜索地址不是有效的 http(s) 链接";
            return false;
        }

        return true;
    }
}

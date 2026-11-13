using System.Drawing;
using System.Windows.Forms;

namespace DshStatusWidget;

/// <summary>「添加搜索引擎」小对话框：深色、和外壳同一套配色。</summary>
internal sealed class AddEngineDialog : Form
{
    private static readonly Color Back = Color.FromArgb(18, 23, 32);
    private static readonly Color FieldBack = Color.FromArgb(28, 35, 47);
    private static readonly Color TextPrimary = Color.FromArgb(232, 238, 246);
    private static readonly Color TextMuted = Color.FromArgb(143, 160, 182);

    private readonly TextBox _name;
    private readonly TextBox _template;
    private readonly Label _hint;

    public AddEngineDialog()
    {
        Text = "添加搜索引擎";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Back;
        ForeColor = TextPrimary;
        ClientSize = new Size(420, 214);
        Font = new Font("Segoe UI", 9.5f);

        var titleFont = new Font(Ui.PickFamily("HarmonyOS Sans SC Medium", "Segoe UI"), 12f, FontStyle.Regular, GraphicsUnit.Pixel);

        Controls.Add(new Label
        {
            Text = "添加搜索引擎",
            Font = titleFont,
            ForeColor = TextPrimary,
            AutoSize = true,
            Location = new Point(20, 16),
        });

        Controls.Add(new Label
        {
            Text = "名称",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(20, 50),
        });

        _name = new TextBox
        {
            Location = new Point(20, 70),
            Width = 380,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = FieldBack,
            ForeColor = TextPrimary,
        };

        Controls.Add(_name);
        Controls.Add(new Label
        {
            Text = "搜索地址（用 {q} 代表关键词）",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(20, 100),
        });

        _template = new TextBox
        {
            Location = new Point(20, 120),
            Width = 380,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = FieldBack,
            ForeColor = TextPrimary,
            Text = "https://www.bing.com/search?q={q}",
        };

        Controls.Add(_template);

        _hint = new Label
        {
            ForeColor = Color.FromArgb(240, 120, 120),
            AutoSize = false,
            Location = new Point(20, 148),
            Size = new Size(380, 20),
            Text = "",
        };

        Controls.Add(_hint);

        var ok = new Button
        {
            Text = "添加",
            DialogResult = DialogResult.OK,
            Location = new Point(238, 172),
            Size = new Size(78, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(46, 60, 82),
            ForeColor = TextPrimary,
        };
        ok.FlatAppearance.BorderColor = Color.FromArgb(70, 86, 110);

        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(322, 172),
            Size = new Size(78, 28),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(30, 37, 49),
            ForeColor = TextMuted,
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(52, 62, 78);

        Controls.Add(ok);
        Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        // 用 FormClosing 拦截，校验不过就不关闭对话框。
        FormClosing += (_, e) =>
        {
            if (DialogResult != DialogResult.OK)
            {
                return;
            }

            if (!Validate(out var error))
            {
                e.Cancel = true;
                DialogResult = DialogResult.None;
                _hint.Text = error;
            }
        };
    }

    public string EngineName => _name.Text.Trim();

    public string Template
    {
        get
        {
            var template = _template.Text.Trim();
            if (!template.Contains("{q}", StringComparison.OrdinalIgnoreCase))
            {
                template += template.Contains('?') ? "&q={q}" : "?q={q}";
            }

            return template;
        }
    }

    private bool Validate(out string? error)
    {
        error = null;
        if (_name.Text.Trim().Length == 0)
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

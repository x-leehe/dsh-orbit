using System.Drawing;
using System.Windows.Forms;

namespace DshOrbit;

/// <summary>
/// 托盘图标：NotifyIcon + 状态色图标 + tooltip + 气泡。只负责「呈现」，
/// 点击语义交给宿主（左键唤出、双击打开 Web、右键菜单）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon()
    {
        _icon = new NotifyIcon
        {
            Visible = true,
            Text = "DSH-Orbit",
            Icon = IconFactory.Create(Fluent.TextTertiary),
        };

        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                RightClick?.Invoke();
            }
            else if (e.Button == MouseButtons.Left)
            {
                LeftClick?.Invoke();
            }
        };

        _icon.DoubleClick += (_, _) => DoubleClick?.Invoke();
    }

    public event Action? LeftClick;

    public event Action? DoubleClick;

    public event Action? RightClick;

    /// <summary>按快照更新图标颜色与悬停文字（与旧实现同一套文案）。</summary>
    public void Update(StatusSnapshot snapshot)
    {
        _icon.Icon = IconFactory.Create(snapshot.Accent);

        var text = snapshot.State switch
        {
            DshState.Running => $"DSH 运行中 · 端口 {snapshot.Port}",
            DshState.Starting => "DSH 启动中…",
            DshState.Error => "DSH 异常 · 查看日志",
            _ => "DSH 已停止",
        };

        if (snapshot.Balance is { } balance)
        {
            text += $" · {Money.Cny(balance)}";
        }

        // NotifyIcon.Text 上限 63 字符，超了会抛。
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloonTip(int timeout, string title, string text, ToolTipIcon icon) =>
        _icon.ShowBalloonTip(timeout, title, text, icon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

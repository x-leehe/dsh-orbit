namespace DshOrbit;

/// <summary>
/// Web 外壳的控制器：WebShell 单例的创建 / 显示 / 切换 / 关闭 / 主题重绘。
/// 「用外壳还是浏览器」由宿主决定，这里只在拿不到外壳时把 URL 交给
/// <c>openInBrowser</c> 回调。
/// </summary>
internal sealed class ShellController : IDisposable
{
    private readonly Func<WidgetConfig> _config;
    private readonly Action<string> _openInBrowser;
    private WebShell? _shell;

    public ShellController(Func<WidgetConfig> config, Action<string> openInBrowser)
    {
        _config = config;
        _openInBrowser = openInBrowser;
    }

    public bool HasWindow => _shell is { IsDisposed: false };

    /// <summary>没开就开；已隐藏就显示；可见时按 <see cref="WidgetConfig.ShellClose"/> 收起或关闭。</summary>
    public void Toggle(string url)
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

            shell.CloseMode = _config().ShellClose;
            shell.ShowUrl(url);
            return;
        }

        Create(url);
    }

    /// <summary>确保外壳可见（不切换、不隐藏）。</summary>
    public void Show(string url)
    {
        if (_shell is { IsDisposed: false } shell)
        {
            shell.CloseMode = _config().ShellClose;
            shell.ShowUrl(url);
            return;
        }

        Create(url);
    }

    /// <summary>设置面板改了「外壳关闭时」：同步到已存在的窗口。</summary>
    public void SyncCloseMode()
    {
        if (_shell is { IsDisposed: false } shell)
        {
            shell.CloseMode = _config().ShellClose;
        }
    }

    /// <summary>主题 / 强调色变化后重画外壳。</summary>
    public void Redraw()
    {
        if (_shell is { IsDisposed: false } shell)
        {
            shell.Invalidate(true);
            shell.Update();
        }
    }

    public void ForceClose() => _shell?.ForceClose();

    public void Dispose() => ForceClose();

    private void Create(string url)
    {
        var created = new WebShell(url, _config().ShellClose);
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
}

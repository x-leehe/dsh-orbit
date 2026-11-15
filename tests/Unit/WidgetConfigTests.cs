using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// WidgetConfig 的默认值即「首次启动时的行为契约」。
/// 只 new 对象，不触碰 AppPaths / 配置文件（避免读写 %LOCALAPPDATA%）。
/// </summary>
public class WidgetConfigTests
{
    [Fact]
    public void NewConfig_HasExpectedStartupDefaults()
    {
        var config = new WidgetConfig();
        Assert.Equal(3080, config.PreferredPort);
        Assert.Equal("web --no-open", config.LaunchArguments);
        Assert.Equal(AppTheme.System, config.Theme);
        Assert.Equal(AutoStartMode.Disabled, config.AutoStartMode);
        Assert.Equal("web", config.Profile);
        Assert.True(config.ShowBalance);
    }

    [Fact]
    public void NewConfig_HasExpectedWindowDefaults()
    {
        var config = new WidgetConfig();
        Assert.Equal(-1, config.X);
        Assert.Equal(-1, config.Y);
        Assert.True(config.AlwaysBottom);
        Assert.False(config.AlwaysOnTop);
        Assert.Equal(1.0, config.Opacity);
        Assert.False(config.ClickThrough);
        Assert.True(config.Visible);
        Assert.True(config.EdgeHideEnabled);
        Assert.Equal(DockSide.None, config.DockSide);
        Assert.True(config.UseWebShell);
        Assert.Equal(ShellCloseMode.Hide, config.ShellClose);
    }

    [Fact]
    public void NewConfig_HasExpectedPathAndIntervalDefaults()
    {
        var config = new WidgetConfig();
        Assert.Null(config.DshExePath);
        Assert.Null(config.WorkingDirectory);
        Assert.Null(config.DeepSeekApiKey);
        Assert.Equal(3, config.StatusIntervalSeconds);
        Assert.Equal(60, config.BalanceIntervalSeconds);
        Assert.Equal(300, config.PluginIntervalSeconds);
    }
}

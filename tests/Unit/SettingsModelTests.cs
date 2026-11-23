using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// SettingsModel：设置面板的编辑态。重点是三件事——
/// 默认值与 WidgetConfig 一致、From/ApplyTo 往返不丢字段、保存前校验能挡住非法输入。
/// 面板本身不测（自绘窗口），逻辑全部落在这里。
/// </summary>
public class SettingsModelTests
{
    [Fact]
    public void Defaults_MatchWidgetConfigDefaults()
    {
        var model = SettingsModel.Defaults();
        var config = new WidgetConfig();

        Assert.Equal(config.Theme, model.Theme);
        Assert.Equal(config.Opacity, model.Opacity);
        Assert.Equal(config.AlwaysBottom, model.AlwaysBottom);
        Assert.Equal(config.EdgeHideEnabled, model.EdgeHideEnabled);
        Assert.Equal(config.HideInFullscreen, model.HideInFullscreen);
        Assert.Equal(config.ShellClose, model.ShellClose);
        Assert.Equal(config.UseWebShell, model.UseWebShell);
        Assert.Equal(config.ShowBalance, model.ShowBalance);
        Assert.Equal(config.Profile, model.Profile);
        Assert.Equal(config.PreferredPort, model.PreferredPort);
        Assert.Equal(config.LaunchArguments, model.LaunchArguments);
        Assert.Equal(config.StatusIntervalSeconds, model.StatusIntervalSeconds);
        Assert.Equal(config.BalanceIntervalSeconds, model.BalanceIntervalSeconds);
        Assert.Equal(config.PluginIntervalSeconds, model.PluginIntervalSeconds);
    }

    [Fact]
    public void FromThenApplyTo_DoesNotChangeUnrelatedFields()
    {
        var config = new WidgetConfig
        {
            X = 42,
            Y = 24,
            DockSide = DockSide.Right,
            Theme = AppTheme.Light,
            DeepSeekApiKey = "sk-test",
        };

        var model = SettingsModel.From(config);
        model.ApplyTo(config);

        Assert.Equal(42, config.X);
        Assert.Equal(24, config.Y);
        Assert.Equal(DockSide.Right, config.DockSide);
        Assert.Equal(AppTheme.Light, config.Theme);
        Assert.Equal("sk-test", config.DeepSeekApiKey);
    }

    [Fact]
    public void ApplyTo_WritesEveryEditableField()
    {
        var config = new WidgetConfig();
        var model = new SettingsModel
        {
            Theme = AppTheme.Dark,
            Opacity = 0.5,
            AlwaysBottom = false,
            AlwaysOnTop = true,
            EdgeHideEnabled = false,
            ClickThrough = true,
            HideInFullscreen = false,
            Visible = false,
            ShellClose = ShellCloseMode.Close,
            UseWebShell = false,
            ShowBalance = false,
            Profile = "work",
            PreferredPort = 4000,
            DshExePath = @"C:\tools\dsh.exe",
            WorkingDirectory = @"C:\work",
            LaunchArguments = "web --port 4000",
            StatusIntervalSeconds = 5,
            BalanceIntervalSeconds = 120,
            PluginIntervalSeconds = 600,
            DeepSeekApiKey = "sk-abc",
        };

        model.ApplyTo(config);

        Assert.Equal(AppTheme.Dark, config.Theme);
        Assert.Equal(0.5, config.Opacity, 3);
        Assert.False(config.AlwaysBottom);
        Assert.True(config.AlwaysOnTop);
        Assert.True(config.ClickThrough);
        Assert.False(config.EdgeHideEnabled);       // 穿透优先
        Assert.Equal(DockSide.None, config.DockSide);
        Assert.False(config.HideInFullscreen);
        Assert.False(config.Visible);
        Assert.Equal(ShellCloseMode.Close, config.ShellClose);
        Assert.False(config.UseWebShell);
        Assert.False(config.ShowBalance);
        Assert.Equal("work", config.Profile);
        Assert.Equal(4000, config.PreferredPort);
        Assert.Equal(@"C:\tools\dsh.exe", config.DshExePath);
        Assert.Equal(@"C:\work", config.WorkingDirectory);
        Assert.Equal("web --port 4000", config.LaunchArguments);
        Assert.Equal(5, config.StatusIntervalSeconds);
        Assert.Equal(120, config.BalanceIntervalSeconds);
        Assert.Equal(600, config.PluginIntervalSeconds);
        Assert.Equal("sk-abc", config.DeepSeekApiKey);
    }

    [Fact]
    public void ApplyTo_NormalizesEmptyTextAndLayoutCombination()
    {
        var config = new WidgetConfig();
        var model = new SettingsModel
        {
            AlwaysBottom = true,
            AlwaysOnTop = true,      // 非法组合：置底优先
            Profile = "   ",
            LaunchArguments = "  ",
            DshExePath = "  ",
            WorkingDirectory = " ",
            DeepSeekApiKey = " ",
            Opacity = 0.1,           // 越界 → 夹到下限
        };

        model.ApplyTo(config);

        Assert.True(config.AlwaysBottom);
        Assert.False(config.AlwaysOnTop);
        Assert.Equal("web", config.Profile);
        Assert.Equal("web --no-open", config.LaunchArguments);
        Assert.Null(config.DshExePath);
        Assert.Null(config.WorkingDirectory);
        Assert.Null(config.DeepSeekApiKey);
        Assert.Equal(SettingsModel.MinOpacity, config.Opacity, 3);
    }

    [Fact]
    public void HasChanges_DetectsEditsOnly()
    {
        var config = new WidgetConfig();
        var model = SettingsModel.From(config);

        Assert.False(model.HasChanges(config));

        model.Theme = AppTheme.Dark;
        Assert.True(model.HasChanges(config));

        model.Theme = config.Theme;
        model.Profile = config.Profile + "  ";   // 空白差异不算改动
        Assert.False(model.HasChanges(config));

        model.Profile = "work";
        Assert.True(model.HasChanges(config));
    }

    [Theory]
    [InlineData(0.29, false)]
    [InlineData(0.3, true)]
    [InlineData(1.0, true)]
    [InlineData(1.01, false)]
    public void Validate_OpacityRange(double opacity, bool expected)
    {
        var model = SettingsModel.Defaults();
        model.Opacity = opacity;

        Assert.Equal(expected, model.TryValidate(out _));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    public void Validate_PortRange(int port, bool expected)
    {
        var model = SettingsModel.Defaults();
        model.PreferredPort = port;

        Assert.Equal(expected, model.TryValidate(out _));
    }

    [Fact]
    public void Validate_Intervals()
    {
        var model = SettingsModel.Defaults();
        model.StatusIntervalSeconds = SettingsModel.MinIntervalSeconds;
        Assert.True(model.TryValidate(out _));

        model.StatusIntervalSeconds = 0;
        Assert.False(model.TryValidate(out _));

        model.StatusIntervalSeconds = SettingsModel.MaxIntervalSeconds + 1;
        Assert.False(model.TryValidate(out _));
    }

    [Fact]
    public void Validate_TextFields()
    {
        var model = SettingsModel.Defaults();

        model.Profile = " ";
        Assert.False(model.TryValidate(out var error));
        Assert.Contains("Profile", error);

        model.Profile = "web";
        model.LaunchArguments = " ";
        Assert.False(model.TryValidate(out _));

        model.LaunchArguments = "web --no-open";
        Assert.True(model.TryValidate(out _));
    }

    [Fact]
    public void Validate_DshPath_OnlyChecksWhenItLooksLikeAPath()
    {
        var model = SettingsModel.Defaults();

        model.DshExePath = "dsh";     // 交给 PATH 查找，不查存在性
        Assert.True(model.TryValidate(out _));

        model.DshExePath = @"C:\not-exists\dsh.exe";
        Assert.False(model.TryValidate(out var error));
        Assert.Contains("dsh 路径不存在", error);
    }

    [Fact]
    public void Validate_WorkingDirectory_MustExist()
    {
        var model = SettingsModel.Defaults();
        model.WorkingDirectory = Path.Combine(Path.GetTempPath(), $"dsh-missing-{Guid.NewGuid():N}");
        Assert.False(model.TryValidate(out var error));
        Assert.Contains("工作目录不存在", error);

        model.WorkingDirectory = Path.GetTempPath();
        Assert.True(model.TryValidate(out _));
    }
}

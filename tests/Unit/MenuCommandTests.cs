using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// MenuCommand：托盘菜单与设置面板共用的命令 ID。
/// 只保证两件事——非空、不重复；动作映射本身由 WidgetApp 的 switch 覆盖。
/// </summary>
public class MenuCommandTests
{
    [Fact]
    public void All_AreNonEmptyAndUnique()
    {
        Assert.NotEmpty(MenuCommand.All);
        Assert.All(MenuCommand.All, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(MenuCommand.All.Length, MenuCommand.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CoreCommands_KeepTheirHistoricalIds()
    {
        // 这些字符串可能被调试脚本 / 历史配置引用，不能悄悄改名。
        Assert.Equal("open", MenuCommand.Open);
        Assert.Equal("layout:bottom", MenuCommand.LayoutBottom);
        Assert.Equal("autostart:FullAuto", MenuCommand.AutoStartFull);
        Assert.Equal("theme:System", MenuCommand.ThemeSystem);
        Assert.Equal("fullscreen", MenuCommand.Fullscreen);
    }
}

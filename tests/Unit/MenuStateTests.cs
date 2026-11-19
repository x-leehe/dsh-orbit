using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// MenuState 是 TrayMenuForm.cs 里的纯数据 record（不实例化窗体），
/// LayoutNormal 只在「既没有贴底、也没有置顶」时为 true。
/// </summary>
public class MenuStateTests
{
    private static MenuState State(bool layoutBottom, bool layoutTop) => new(
        Running: false,
        Starting: false,
        HasUrl: false,
        LayoutBottom: layoutBottom,
        LayoutTop: layoutTop,
        Locked: false,
        EdgeHideEnabled: false,
        Visible: true,
        ShellHide: false,
        AutoStart: AutoStartMode.Disabled);

    [Theory]
    [InlineData(false, false, true)]  // 普通模式
    [InlineData(true, false, false)]  // 贴底
    [InlineData(false, true, false)]  // 置顶
    [InlineData(true, true, false)]   // 两个标志同时为真的非法组合也不允许「普通」
    public void LayoutNormal_IsTrueOnlyWhenNeitherBottomNorTop(bool bottom, bool top, bool expected)
    {
        var state = State(bottom, top);
        Assert.Equal(expected, state.LayoutNormal);
        Assert.Equal(bottom, state.LayoutBottom);
        Assert.Equal(top, state.LayoutTop);
    }

    [Fact]
    public void WithExpression_ChangesOnlyRequestedLayoutFlag()
    {
        var normal = State(false, false);
        var bottom = normal with { LayoutBottom = true };

        Assert.True(normal.LayoutNormal);
        Assert.False(bottom.LayoutNormal);
        Assert.True(bottom.LayoutBottom);
        Assert.False(bottom.LayoutTop);
        Assert.Equal(normal.Running, bottom.Running);
    }

    [Fact]
    public void OtherRecordFields_ArePreserved()
    {
        var state = new MenuState(
            Running: true,
            Starting: false,
            HasUrl: true,
            LayoutBottom: false,
            LayoutTop: false,
            Locked: true,
            EdgeHideEnabled: false,
            Visible: false,
            ShellHide: true,
            AutoStart: AutoStartMode.FullAuto);

        Assert.True(state.Running);
        Assert.False(state.Starting);
        Assert.True(state.HasUrl);
        Assert.True(state.Locked);
        Assert.False(state.EdgeHideEnabled);
        Assert.False(state.Visible);
        Assert.True(state.ShellHide);
        Assert.Equal(AutoStartMode.FullAuto, state.AutoStart);
        Assert.True(state.LayoutNormal);
    }

    /// <summary>「全屏时隐藏」默认开：既有调用点不传这个参数时行为不变。</summary>
    [Fact]
    public void HideInFullscreen_DefaultsToTrue()
    {
        var state = State(false, false);

        Assert.True(state.HideInFullscreen);
    }

    [Fact]
    public void WithExpression_ChangesOnlyHideInFullscreen()
    {
        var open = new MenuState(
            Running: true,
            Starting: false,
            HasUrl: true,
            LayoutBottom: true,
            LayoutTop: false,
            Locked: true,
            EdgeHideEnabled: true,
            Visible: true,
            ShellHide: true,
            AutoStart: AutoStartMode.Enabled);

        var closed = open with { HideInFullscreen = false };

        Assert.True(open.HideInFullscreen);
        Assert.False(closed.HideInFullscreen);
        Assert.Equal(open.Running, closed.Running);
        Assert.Equal(open.HasUrl, closed.HasUrl);
        Assert.Equal(open.LayoutBottom, closed.LayoutBottom);
        Assert.Equal(open.LayoutTop, closed.LayoutTop);
        Assert.Equal(open.Locked, closed.Locked);
        Assert.Equal(open.EdgeHideEnabled, closed.EdgeHideEnabled);
        Assert.Equal(open.Visible, closed.Visible);
        Assert.Equal(open.ShellHide, closed.ShellHide);
        Assert.Equal(open.AutoStart, closed.AutoStart);
    }
}

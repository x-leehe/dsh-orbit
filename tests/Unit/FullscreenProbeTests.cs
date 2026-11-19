using System.Drawing;
using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// FullscreenProbe.CoversMonitor 的纯几何判定。
/// 全屏口径是「窗口矩形完整盖住显示器矩形（四边容差 2px）」，不是「和显示器有交集」：
/// 少一条边就不是全屏，多出来的部分（溢出到显示器外）不影响判定。
/// 2px 容差是为了吸收 DWM 扩展边框、DPI 取整和 Win10 隐形边框的抖动
/// ——容差再大就会把贴着屏幕边缘的普通窗口误判成全屏。
/// </summary>
public class FullscreenProbeTests
{
    /// <summary>主显示器 1920×1080，左上角在 (0,0)。</summary>
    private static readonly Rectangle Primary = new(0, 0, 1920, 1080);

    [Fact]
    public void ExactMatch_CoversMonitor()
    {
        Assert.True(FullscreenProbe.CoversMonitor(Primary, Primary));
    }

    [Theory]
    [InlineData(0, true)]   // 完全一致
    [InlineData(1, true)]   // 四边各内缩 1px：DPI 取整的常见误差
    [InlineData(2, true)]   // 内缩 2px 仍在容差内，仍算盖满
    [InlineData(3, false)]  // 内缩 3px 已经漏出显示器边缘，不算全屏
    public void InsetFromAllEdges_UsesTwoPixelTolerance(int inset, bool expected)
    {
        var window = Rectangle.Inflate(Primary, -inset, -inset);
        Assert.Equal(expected, FullscreenProbe.CoversMonitor(window, Primary));
    }

    [Fact]
    public void WindowLargerThanMonitor_CoversMonitor()
    {
        // 溢出：部分全屏游戏 / 无边框全屏会画到显示器外一点，这仍然是全屏。
        var window = Rectangle.Inflate(Primary, 64, 64);
        Assert.True(FullscreenProbe.CoversMonitor(window, Primary));
    }

    [Fact]
    public void DisjointWindow_DoesNotCover()
    {
        // 同一块屏大小、但整体挪到右边：完全没有交集。
        var window = new Rectangle(1920, 0, 1920, 1080);
        Assert.False(FullscreenProbe.CoversMonitor(window, Primary));
    }

    [Fact]
    public void CornerOverlapOnly_DoesNotCover()
    {
        // 只有右下角压到显示器：有交集 ≠ 全屏。
        var window = new Rectangle(1900, 1060, 800, 600);
        Assert.False(FullscreenProbe.CoversMonitor(window, Primary));
    }

    [Fact]
    public void WindowMatchingOneAxisOnly_DoesNotCover()
    {
        // 宽度正好盖满、高度只有一半：露出的下半屏说明它不是全屏。
        var window = new Rectangle(0, 0, 1920, 540);
        Assert.False(FullscreenProbe.CoversMonitor(window, Primary));
    }

    [Theory]
    [InlineData(0, 0)]     // GetMonitorInfo 失败时我们给的就是空矩形
    [InlineData(0, 1080)]  // 宽为 0：退化的单线
    [InlineData(1920, 0)]  // 高为 0
    [InlineData(-1, -1)]   // 负宽高同样视为无效
    public void InvalidMonitor_DoesNotCover(int width, int height)
    {
        var monitor = new Rectangle(0, 0, width, height);
        Assert.False(FullscreenProbe.CoversMonitor(Primary, monitor));
    }

    [Fact]
    public void EmptyWindow_DoesNotCover()
    {
        // 取不到窗口矩形（Rectangle.Empty）以及 0×0 的窗口都不能算全屏。
        Assert.False(FullscreenProbe.CoversMonitor(Rectangle.Empty, Primary));
        Assert.False(FullscreenProbe.CoversMonitor(new Rectangle(0, 0, 0, 0), Primary));
    }

    [Fact]
    public void SecondMonitor_UsesItsOwnCoordinates()
    {
        // 多显示器：第二块屏从 x = 1920 开始（2560×1440）。
        var second = new Rectangle(1920, 0, 2560, 1440);

        // 正好铺满第二块屏 → 全屏。
        Assert.True(FullscreenProbe.CoversMonitor(second, second));

        // 尺寸一样但放在第一屏起点 → 对第二块屏来说左半截都在屏外，不是全屏。
        var onPrimary = new Rectangle(0, 0, 2560, 1440);
        Assert.False(FullscreenProbe.CoversMonitor(onPrimary, second));
    }

    [Fact]
    public void MonitorWithNegativeOrigin_IsMatchedByExactCoordinates()
    {
        // 显示器可以在主屏左侧（左侧屏的 x 是负的），判定必须用绝对坐标而不是「从 0 开始」。
        var left = new Rectangle(-1920, 0, 1920, 1080);
        var window = new Rectangle(-1920, 0, 1920, 1080);
        Assert.True(FullscreenProbe.CoversMonitor(window, left));

        // 同样的尺寸挪回主屏起点，对左侧屏就不再覆盖。
        Assert.False(FullscreenProbe.CoversMonitor(new Rectangle(0, 0, 1920, 1080), left));
    }

    [Fact]
    public void WindowSpanningBothMonitors_CountsOnEach()
    {
        // 已知取舍：横跨两块屏的窗口对每块屏都算「盖满」，因为判定只看几何覆盖。
        // 真实场景里很少出现（无边框全屏一般只盯一块屏），且 IsZoomed 已挡掉普通跨屏最大化。
        var spanning = new Rectangle(0, 0, 4480, 1440);
        var second = new Rectangle(1920, 0, 2560, 1440);
        Assert.True(FullscreenProbe.CoversMonitor(spanning, second));
    }
}

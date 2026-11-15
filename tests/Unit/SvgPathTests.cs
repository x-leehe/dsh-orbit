using System.Drawing;
using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// SvgPath.Parse 是纯函数（string → GraphicsPath），这里只依赖 GDI+ 路径对象，
/// 不需要窗口/句柄，适合做确定性单测。
/// 断言约定：绝大多数用例用 Theory 参数化，失败时 xUnit 会把输入 d 打进测试名，便于直接定位。
/// </summary>
public class SvgPathTests
{
    /// <summary>取路径最后一个落点；空路径直接失败。</summary>
    private static PointF LastPoint(string d)
    {
        using var path = SvgPath.Parse(d);
        Assert.True(path.PointCount > 0, $"\"{d}\" 应解析出至少一个非空路径点");
        return path.PathPoints[^1];
    }

    // ---------------------------------------------------------------- 绝对命令

    [Theory]
    [InlineData("M0 0 L10 20", 10f, 20f)]
    [InlineData("M0 0 H10", 10f, 0f)]
    [InlineData("M0 0 V10", 0f, 10f)]
    [InlineData("M1 2 C3 4 5 6 7 8", 7f, 8f)]
    [InlineData("M1 2 S3 4 5 6", 5f, 6f)]
    [InlineData("M1 2 Q3 4 5 6", 5f, 6f)]
    [InlineData("M1 2 T5 6", 5f, 6f)]
    [InlineData("M10 10 A5 5 0 0 1 20 20", 20f, 20f)]
    public void AbsoluteCommands_EndAtExpectedPoint(string d, float expectedX, float expectedY)
    {
        var last = LastPoint(d);
        Assert.Equal(expectedX, last.X);
        Assert.Equal(expectedY, last.Y);
    }

    // ---------------------------------------------------------------- 相对命令

    [Theory]
    [InlineData("m10 20 l5 5", 15f, 25f)]
    [InlineData("m10 20 h5", 15f, 20f)]
    [InlineData("m10 20 v5", 10f, 25f)]
    [InlineData("m1 2 c3 4 5 6 7 8", 8f, 10f)]
    [InlineData("m1 2 s3 4 5 6", 6f, 8f)]
    [InlineData("m1 2 q3 4 5 6", 6f, 8f)]
    [InlineData("m1 2 t5 6", 6f, 8f)]
    [InlineData("m10 10 a5 5 0 0 1 10 10", 20f, 20f)]
    public void RelativeCommands_EndAtExpectedPoint(string d, float expectedX, float expectedY)
    {
        var last = LastPoint(d);
        Assert.Equal(expectedX, last.X);
        Assert.Equal(expectedY, last.Y);
    }

    // ---------------------------------------------------------------- 隐式重复命令

    [Theory]
    [InlineData("M0 0 10 0 10 10", 10f, 10f)]      // M 之后的数字对隐式按 L 处理
    [InlineData("m0 0 10 0 10 10", 20f, 10f)]      // m 之后隐式按 l 处理
    [InlineData("M0 0 H10 20", 20f, 0f)]           // H 连续给两个 x
    [InlineData("M0 0 V10 20", 0f, 20f)]           // V 连续给两个 y
    [InlineData("M0 0 C0 1 2 3 4 5 6 7 8 9 10 11", 10f, 11f)] // C 连续两段
    public void ImplicitRepeatedCommand_ContinuesPreviousCommand(string d, float expectedX, float expectedY)
    {
        var last = LastPoint(d);
        Assert.Equal(expectedX, last.X);
        Assert.Equal(expectedY, last.Y);
    }

    // ---------------------------------------------------------------- 包围盒 / 多子路径 / Z

    [Theory]
    [InlineData("M0 0 H10 V10 H0 Z")]
    [InlineData("m0 0 h10 v10 h-10 z")]
    [InlineData("M0 0 L10 0 L10 10 L0 10 Z")]
    [InlineData("M0 0 10 0 10 10 0 10 Z")]
    public void RectangleVariants_HaveTenByTenBounds(string d)
    {
        using var path = SvgPath.Parse(d);
        var bounds = path.GetBounds();
        Assert.Equal(new RectangleF(0, 0, 10, 10), bounds);
    }

    [Fact]
    public void RectanglePath_HasFourPointsAndClosesSubpath()
    {
        using var path = SvgPath.Parse("M0 0 H10 V10 H0 Z");
        Assert.Equal(4, path.PointCount);
        Assert.Equal(new PointF(0, 0), path.PathPoints[0]);
        Assert.Equal(new PointF(10, 0), path.PathPoints[1]);
        Assert.Equal(new PointF(10, 10), path.PathPoints[2]);
        Assert.Equal(new PointF(0, 10), path.PathPoints[3]);
        Assert.NotEqual(0, path.PathTypes[^1] & 0x80); // 末点带 CloseSubpath 标志
    }

    [Fact]
    public void MultipleSubpaths_StaySeparateFigures()
    {
        using var path = SvgPath.Parse("M0 0 H10 Z M20 20 H30 Z");
        Assert.Equal(4, path.PointCount);
        Assert.Equal(new PointF(0, 0), path.PathPoints[0]);
        Assert.Equal(new PointF(10, 0), path.PathPoints[1]);
        Assert.Equal(new PointF(20, 20), path.PathPoints[2]);
        Assert.Equal(new PointF(30, 20), path.PathPoints[3]);
        Assert.Equal(new RectangleF(0, 0, 30, 20), path.GetBounds());
        Assert.Equal(2, path.PathTypes.Count(t => (t & 0x80) != 0));
    }

    [Fact]
    public void CloseCommand_ResetsCurrentPointToSubpathStart()
    {
        // Z 之后 current 回到子路径起点 (10,10)，相对命令 l5 5 因此落在 (15,15)。
        using var path = SvgPath.Parse("M10 10 L20 10 Z l5 5");
        Assert.Equal(4, path.PointCount);
        Assert.Equal(new PointF(15, 15), path.PathPoints[^1]);
    }

    // ---------------------------------------------------------------- 圆弧（含紧凑旗标）

    [Fact]
    public void Arc_CompactFlagsMatchSpacedForm()
    {
        using var compact = SvgPath.Parse("M0 0 A5 5 0 0110 10");
        using var spaced = SvgPath.Parse("M0 0 A5 5 0 0 1 10 10");
        Assert.Equal(spaced.PointCount, compact.PointCount);
        Assert.Equal(spaced.GetBounds(), compact.GetBounds());
        Assert.Equal(new PointF(0, 0), compact.PathPoints[0]);
        Assert.Equal(new PointF(10, 10), compact.PathPoints[^1]);
    }

    [Fact]
    public void Arc_CompactDoubleZeroFlagsMatchSpacedForm()
    {
        using var compact = SvgPath.Parse("M0 0 A5 5 0 00 10 10");
        using var spaced = SvgPath.Parse("M0 0 A5 5 0 0 0 10 10");
        Assert.Equal(spaced.PointCount, compact.PointCount);
        Assert.Equal(spaced.GetBounds(), compact.GetBounds());
        Assert.Equal(new PointF(10, 10), compact.PathPoints[^1]);
    }

    [Fact]
    public void Arc_SweepFlagChangesGeometry()
    {
        using var sweep = SvgPath.Parse("M0 0 A5 5 0 0 1 10 10");
        using var noSweep = SvgPath.Parse("M0 0 A5 5 0 0 0 10 10");
        Assert.Equal(new PointF(10, 10), sweep.PathPoints[^1]);
        Assert.Equal(new PointF(10, 10), noSweep.PathPoints[^1]);
        Assert.NotEqual(sweep.GetBounds(), noSweep.GetBounds());
    }

    [Fact]
    public void Arc_LargeArcFlagChangesGeometry()
    {
        using var small = SvgPath.Parse("M0 0 A20 20 0 0 1 10 0");
        using var large = SvgPath.Parse("M0 0 A20 20 0 1 1 10 0");
        Assert.Equal(new PointF(10, 0), small.PathPoints[^1]);
        Assert.Equal(new PointF(10, 0), large.PathPoints[^1]);
        Assert.NotEqual(small.GetBounds(), large.GetBounds());
    }

    [Fact]
    public void Arc_WithZeroRadiusFallsBackToLine()
    {
        using var path = SvgPath.Parse("M0 0 A0 0 0 0 1 10 10");
        Assert.Equal(2, path.PointCount);
        Assert.Equal(new PointF(10, 10), path.PathPoints[^1]);
    }

    // ---------------------------------------------------------------- 数字切分

    [Fact]
    public void Tokenizer_HandlesAdjacentNegativeAndDecimalNumbers()
    {
        using var path = SvgPath.Parse("M10-20L5.5 7.5");
        Assert.Equal(2, path.PointCount);
        Assert.Equal(new PointF(10, -20), path.PathPoints[0]);
        Assert.Equal(new PointF(5.5f, 7.5f), path.PathPoints[1]);
    }

    // ---------------------------------------------------------------- 空 / 非法输入

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   \r\n\t ")]
    [InlineData("M")]
    [InlineData("Z")]
    [InlineData("xyz")]
    [InlineData("!!!")]
    [InlineData("@#$%")]
    [InlineData("--")]
    public void EmptyOrInvalidData_ReturnsEmptyPathWithoutThrowing(string d)
    {
        using var path = SvgPath.Parse(d);
        Assert.Equal(0, path.PointCount);
    }
}

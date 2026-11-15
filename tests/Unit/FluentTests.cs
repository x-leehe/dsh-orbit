using System.Drawing;
using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// Fluent 的纯函数部分：颜色插值 / alpha 钳制 / 缓动曲线 / 状态层 / 圆角路径。
/// 不初始化主题（Fluent.Init/Refresh 会读注册表），使用静态默认值（深色基准）。
/// 期望值均为手算结果，失败时能直接看出偏差。
/// </summary>
public class FluentTests
{
    private static readonly Color Lower = Color.FromArgb(0, 10, 20, 30);
    private static readonly Color Upper = Color.FromArgb(255, 30, 40, 50);

    // ---------------------------------------------------------------- Mix

    [Fact]
    public void Mix_ReturnsExactEndpoints()
    {
        Assert.Equal(Lower.ToArgb(), Fluent.Mix(Lower, Upper, 0f).ToArgb());
        Assert.Equal(Upper.ToArgb(), Fluent.Mix(Lower, Upper, 1f).ToArgb());
    }

    [Fact]
    public void Mix_Midpoint_InterpolatesRgbAndAlpha()
    {
        // alpha (0+255)/2=127.5 → Math.Round 银行家舍入 → 128；RGB 取平均。
        var mixed = Fluent.Mix(Lower, Upper, 0.5f);
        Assert.Equal(128, mixed.A);
        Assert.Equal(20, mixed.R);
        Assert.Equal(30, mixed.G);
        Assert.Equal(40, mixed.B);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(-0.25f)]
    [InlineData(1.25f)]
    [InlineData(2f)]
    public void Mix_ClampsOutsideUnitRange(float t)
    {
        var expected = t < 0f ? Lower : Upper;
        Assert.Equal(expected.ToArgb(), Fluent.Mix(Lower, Upper, t).ToArgb());
    }

    // ---------------------------------------------------------------- WithAlpha

    [Theory]
    [InlineData(-2f, 0)]
    [InlineData(0f, 0)]
    [InlineData(0.5f, 128)]
    [InlineData(1f, 255)]
    [InlineData(9f, 255)]
    public void WithAlpha_ClampsAndKeepsRgb(float alpha, int expectedAlpha)
    {
        var source = Color.FromArgb(255, 12, 34, 56);
        var result = Fluent.WithAlpha(source, alpha);
        Assert.Equal(expectedAlpha, result.A);
        Assert.Equal(12, result.R);
        Assert.Equal(34, result.G);
        Assert.Equal(56, result.B);
    }

    // ---------------------------------------------------------------- 缓动曲线

    [Fact]
    public void EaseOut_EndpointsAreZeroAndOne()
    {
        Assert.Equal(0f, Fluent.EaseOut(0f));
        Assert.Equal(1f, Fluent.EaseOut(1f));
        Assert.Equal(0f, Fluent.EaseOut(-5f)); // 入参先被钳制
        Assert.Equal(1f, Fluent.EaseOut(5f));
    }

    [Fact]
    public void EaseOut_IsMonotonicallyNonDecreasing()
    {
        var previous = -1f;
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100f;
            var value = Fluent.EaseOut(t);
            Assert.True(value >= previous - 1e-6f, $"EaseOut 在 t={t} 处回退：{value} < {previous}");
            Assert.InRange(value, 0f, 1f);
            previous = value;
        }
    }

    [Fact]
    public void EaseEmphasized_EndpointsAreZeroAndOne()
    {
        Assert.Equal(0f, Fluent.EaseEmphasized(0f));
        Assert.Equal(1f, Fluent.EaseEmphasized(1f));
        Assert.Equal(0f, Fluent.EaseEmphasized(-5f));
        Assert.Equal(1f, Fluent.EaseEmphasized(5f));
    }

    [Fact]
    public void EaseEmphasized_IsMonotonicallyNonDecreasing()
    {
        var previous = -1f;
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100f;
            var value = Fluent.EaseEmphasized(t);
            Assert.True(value >= previous - 1e-6f, $"EaseEmphasized 在 t={t} 处回退：{value} < {previous}");
            Assert.InRange(value, 0f, 1f);
            previous = value;
        }
    }

    // ---------------------------------------------------------------- OverState 状态层

    [Fact]
    public void OverState_RestAndDisabled_ReturnBaseColorUnchanged()
    {
        var baseColor = Color.FromArgb(255, 100, 100, 100);
        Assert.Equal(baseColor.ToArgb(), Fluent.OverState(baseColor, VisualState.Rest).ToArgb());
        Assert.Equal(baseColor.ToArgb(), Fluent.OverState(baseColor, VisualState.Disabled).ToArgb());
    }

    [Fact]
    public void OverState_HoverAndPressed_ApplyVisibleStateLayer()
    {
        var baseColor = Color.FromArgb(255, 100, 100, 100);
        var hover = Fluent.OverState(baseColor, VisualState.Hover);
        var pressed = Fluent.OverState(baseColor, VisualState.Pressed);

        Assert.NotEqual(baseColor.ToArgb(), hover.ToArgb());
        Assert.NotEqual(baseColor.ToArgb(), pressed.ToArgb());
        Assert.NotEqual(hover.ToArgb(), pressed.ToArgb());

        // 状态层是半透明白色遮罩：不透明底色叠上去后 alpha 降低、RGB 变亮。
        Assert.True(hover.A < baseColor.A, $"Hover 叠加后半透明，A 应小于 {baseColor.A}，实际 {hover.A}");
        Assert.True(pressed.A < baseColor.A, $"Pressed 叠加后半透明，A 应小于 {baseColor.A}，实际 {pressed.A}");
        Assert.True(hover.R > baseColor.R, $"Hover 应提亮，R 应大于 {baseColor.R}，实际 {hover.R}");
        Assert.True(pressed.R > baseColor.R, $"Pressed 应提亮，R 应大于 {baseColor.R}，实际 {pressed.R}");
    }

    // ---------------------------------------------------------------- Rounded 路径

    [Theory]
    [InlineData(-5f)]
    [InlineData(0f)]
    [InlineData(4f)]
    [InlineData(8f)]
    [InlineData(16f)]
    [InlineData(1000f)]
    public void Rounded_StaysWithinGivenRectangle(float radius)
    {
        var rect = new RectangleF(10, 20, 100, 40);
        using var path = Fluent.Rounded(rect, radius);
        Assert.True(path.PointCount > 0, $"Rounded(radius={radius}) 不应返回空路径");

        var bounds = path.GetBounds();
        const float eps = 0.01f;
        Assert.True(bounds.Left >= rect.Left - eps, $"Rounded(radius={radius}) 左边越界：{bounds.Left} < {rect.Left}");
        Assert.True(bounds.Top >= rect.Top - eps, $"Rounded(radius={radius}) 上边越界：{bounds.Top} < {rect.Top}");
        Assert.True(bounds.Right <= rect.Right + eps, $"Rounded(radius={radius}) 右边越界：{bounds.Right} > {rect.Right}");
        Assert.True(bounds.Bottom <= rect.Bottom + eps, $"Rounded(radius={radius}) 下边越界：{bounds.Bottom} > {rect.Bottom}");
        Assert.True(Math.Abs(bounds.Width - rect.Width) <= eps, $"Rounded(radius={radius}) 宽度应保持 {rect.Width}，实际 {bounds.Width}");
        Assert.True(Math.Abs(bounds.Height - rect.Height) <= eps, $"Rounded(radius={radius}) 高度应保持 {rect.Height}，实际 {bounds.Height}");
    }
}

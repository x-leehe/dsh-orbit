using System.Globalization;
using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// Money.Cny 的格式口径：
/// 余额与「今日已用」共用；DeepSeek 官方价格是「每百万 tokens 几元」，单次请求花费远小于 1 分，
/// 但 /user/balance 的余额只到分（今日已用由余额差累计，步长就是 0.01），
/// 所以 ≥ 1 分用两位小数，不足 1 分才展开到最多 8 位，避免把真实用量显示成 ¥0.00。
/// </summary>
public class MoneyTests
{
    [Theory]
    [InlineData("7.62", "¥7.62")]
    [InlineData("7.6200", "¥7.62")]     // 尾零不能露出来（截图里的 ¥7.6200 就是这个问题）
    [InlineData("26.36", "¥26.36")]
    [InlineData("0.64", "¥0.64")]
    [InlineData("12.5", "¥12.50")]      // 两位小数补零
    [InlineData("0.01", "¥0.01")]
    [InlineData("0", "¥0.00")]
    [InlineData("0.0000", "¥0.00")]
    [InlineData("0.0032", "¥0.0032")]   // 不足 1 分：不四舍五入成 0
    [InlineData("0.006", "¥0.006")]
    [InlineData("0.00000002", "¥0.00000002")]  // 单 token 级花费也不吞掉
    public void Cny_UsesTwoDecimalsUnlessSubCent(string amount, string expected)
    {
        var value = decimal.Parse(amount, CultureInfo.InvariantCulture);

        Assert.Equal(expected, Money.Cny(value));
    }

    [Fact]
    public void Cny_NegativeBalance_KeepsSign()
    {
        // 余额一般不会为负，但接口真返回负数时不能把符号吃掉。
        Assert.Equal("¥-1.20", Money.Cny(-1.2m));
        Assert.Equal("¥-0.0032", Money.Cny(-0.0032m));
    }
}

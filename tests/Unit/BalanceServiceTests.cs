using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// BalanceService.IsOffPeak：DeepSeek 闲时窗口是北京时间 00:30–08:30（左闭右开）。
/// 用 UTC 输入测边界，避免依赖机器本地时区；北京时间全年 UTC+8，无夏令时。
/// </summary>
public class BalanceServiceTests
{
    private static DateTime Utc(int hour, int minute) =>
        new(2026, 1, 15, hour, minute, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(16, 29, false)] // 北京 00:29 —— 窗口开始前 1 分钟
    [InlineData(16, 30, true)]  // 北京 00:30 —— 窗口开始（闭）
    [InlineData(0, 29, true)]   // 北京 08:29 —— 窗口结束前 1 分钟
    [InlineData(0, 30, false)]  // 北京 08:30 —— 窗口结束（开）
    [InlineData(20, 0, true)]   // 北京 04:00 —— 窗口正中间
    [InlineData(4, 0, false)]   // 北京 12:00 —— 白天
    [InlineData(12, 0, false)]  // 北京 20:00 —— 晚上
    [InlineData(15, 0, false)]  // 北京 23:00 —— 窗口前夜
    [InlineData(17, 0, true)]   // 北京 01:00 —— 窗口内
    [InlineData(23, 59, true)]  // 北京 07:59 —— 窗口末段
    public void IsOffPeak_MatchesBeijingWindow(int hour, int minute, bool expected)
    {
        var actual = BalanceService.IsOffPeak(Utc(hour, minute));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsOffPeak_OnlyUsesHourAndMinute()
    {
        // 16:30:59 UTC = 北京 00:30:59，仍算闲时（秒不参与判断）。
        var withSeconds = new DateTime(2026, 11, 15, 16, 30, 59, DateTimeKind.Utc);
        Assert.True(BalanceService.IsOffPeak(withSeconds));

        // 00:30:00 UTC = 北京 08:30:00，已离开闲时。
        var windowEnd = new DateTime(2026, 11, 15, 0, 30, 0, DateTimeKind.Utc);
        Assert.False(BalanceService.IsOffPeak(windowEnd));
    }
}

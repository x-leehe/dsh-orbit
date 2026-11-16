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

    /// <summary>UTC 带秒的时间点（北京 = UTC+8，无夏令时）。</summary>
    private static DateTime UtcAt(int hour, int minute, int second) =>
        new(2026, 1, 15, hour, minute, second, DateTimeKind.Utc);

    [Theory]
    // 北京 00:29:59 —— 标准价，距闲时开始 1 秒。
    [InlineData(16, 29, 59, 0, 0, 1)]
    // 北京 00:30:00 —— 闲时开始，距闲时结束 8 小时。
    [InlineData(16, 30, 0, 8, 0, 0)]
    // 北京 08:29:59 —— 闲时最后 1 秒。
    [InlineData(0, 29, 59, 0, 0, 1)]
    // 北京 08:30:00 —— 标准价开始，距次日闲时 16 小时。
    [InlineData(0, 30, 0, 16, 0, 0)]
    // 北京 23:00 —— 跨午夜：等 1 小时 30 分到次日 00:30。
    [InlineData(15, 0, 0, 1, 30, 0)]
    // 北京 00:00:00 —— 刚过午夜，还差 30 分到闲时。
    [InlineData(16, 0, 0, 0, 30, 0)]
    // 北京 04:00 —— 闲时中点，剩 4h30m。
    [InlineData(20, 0, 0, 4, 30, 0)]
    // 北京 12:00 —— 标准价中点，剩 12h30m 到次日 00:30。
    [InlineData(4, 0, 0, 12, 30, 0)]
    public void UntilNextSwitch_MatchesBeijingPeriods(
        int utcHour, int utcMinute, int utcSecond,
        int spanHours, int spanMinutes, int spanSeconds)
    {
        var actual = BalanceService.UntilNextSwitch(UtcAt(utcHour, utcMinute, utcSecond));
        Assert.Equal(new TimeSpan(spanHours, spanMinutes, spanSeconds), actual);
    }

    [Theory]
    [InlineData(16, 29, 59)] // 北京 00:29:59 —— 紧贴闭区间起点
    [InlineData(16, 30, 0)]  // 北京 00:30:00 —— 窗口起点
    [InlineData(0, 29, 59)]  // 北京 08:29:59 —— 紧贴开区间终点
    [InlineData(0, 30, 0)]   // 北京 08:30:00 —— 刚离开窗口
    [InlineData(15, 0, 0)]   // 北京 23:00 —— 跨午夜
    [InlineData(16, 0, 0)]   // 北京 00:00:00 —— 午夜整点
    [InlineData(9, 17, 42)]  // 北京 17:17:42 —— 任意标准价时刻
    public void UntilNextSwitch_AgreesWithIsOffPeak(int utcHour, int utcMinute, int utcSecond)
    {
        var now = UtcAt(utcHour, utcMinute, utcSecond);
        var span = BalanceService.UntilNextSwitch(now);

        // 恒为非负且小于一天（闲时 8h / 标准价 16h 都不越界）。
        Assert.InRange(span, TimeSpan.Zero, TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));

        // 同一个时刻两者不能自相矛盾：闲时的倒计时必须在 8h 内，标准价必须在 16h 内。
        var limit = BalanceService.IsOffPeak(now) ? TimeSpan.FromHours(8) : TimeSpan.FromHours(16);
        Assert.InRange(span, TimeSpan.FromTicks(1), limit);

        // 倒计时归零的那一刻，计价时段必须真的翻面。
        Assert.NotEqual(BalanceService.IsOffPeak(now), BalanceService.IsOffPeak(now + span));
    }

    [Fact]
    public void IsOffPeak_UsesSharedBoundaryConstants()
    {
        // 边界由常量唯一来源定义，窗口是左闭右开。
        Assert.Equal(30, BalanceService.OffPeakStartMinute);
        Assert.Equal(8 * 60 + 30, BalanceService.OffPeakEndMinute);
        Assert.True(BalanceService.IsOffPeak(UtcAt(16, 30, 0)));
        Assert.False(BalanceService.IsOffPeak(UtcAt(0, 30, 0)));
    }
}

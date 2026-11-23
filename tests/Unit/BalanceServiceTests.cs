using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// BalanceService 峰谷判定：DeepSeek 官方 2026 规则（北京时间 周一至周五 09:00–12:00、14:00–18:00 为高峰，
/// 左闭右开；周末与法定节假日全天闲时）。
///
/// 所有用例都注入节假日判定（<see cref="NoHoliday"/> / <see cref="IsHoliday"/>），既不读网络也不读磁盘；
/// 北京时间全年 UTC+8 且无夏令时，所以「北京 local → UTC」直接减 8 小时。
/// </summary>
public class BalanceServiceTests
{
    /// <summary>没有法定节假日的工作周。</summary>
    private static bool NoHoliday(DateOnly _) => false;

    /// <summary>2026 年国庆前两天的替身年表（2026-10-01 是周四）。</summary>
    private static readonly HashSet<DateOnly> Holidays = [new(2026, 10, 1), new(2026, 10, 2)];

    private static bool IsHoliday(DateOnly date) => Holidays.Contains(date);

    /// <summary>北京时间 → UTC（北京 = UTC+8，无夏令时）。</summary>
    private static DateTime Utc(int month, int day, int hour, int minute, int second = 0) =>
        DateTime.SpecifyKind(
            new DateTime(2026, month, day, hour, minute, second, DateTimeKind.Unspecified).AddHours(-8),
            DateTimeKind.Utc);

    // ---------------------------------------------------------------- IsOffPeak

    [Theory]
    [InlineData(0, 0, true)]    // 北京 00:00 —— 深夜闲时
    [InlineData(8, 59, true)]   // 上午高峰开始前 1 分钟
    [InlineData(9, 0, false)]   // 上午高峰开始（闭）
    [InlineData(11, 59, false)] // 上午高峰最后一分钟
    [InlineData(12, 0, true)]   // 午间闲时开始（开）
    [InlineData(13, 59, true)]  // 午间闲时最后一分钟
    [InlineData(14, 0, false)]  // 下午高峰开始（闭）
    [InlineData(17, 59, false)] // 下午高峰最后一分钟
    [InlineData(18, 0, true)]   // 晚间闲时开始（开）
    [InlineData(23, 59, true)]  // 深夜闲时
    public void IsOffPeak_WorkdayBoundaries(int hour, int minute, bool expected)
    {
        // 2026-11-23 是周一：没有任何法定节假日。
        Assert.Equal(expected, BalanceService.IsOffPeak(Utc(11, 23, hour, minute), NoHoliday));
    }

    [Fact]
    public void IsOffPeak_IgnoresSeconds()
    {
        // 秒不参与判断：09:00:59 仍在上午高峰里，17:59:59 仍在下午高峰里。
        Assert.False(BalanceService.IsOffPeak(Utc(11, 23, 9, 0, 59), NoHoliday));
        Assert.False(BalanceService.IsOffPeak(Utc(11, 23, 17, 59, 59), NoHoliday));

        // 12:00:00 整点已经翻面成闲时。
        Assert.True(BalanceService.IsOffPeak(Utc(11, 23, 12, 0, 0), NoHoliday));
    }

    [Theory]
    [InlineData(11, 28, 10, 0)] // 周六 2026-11-28 上午
    [InlineData(11, 28, 15, 0)] // 周六下午
    [InlineData(11, 29, 10, 0)] // 周日 2026-11-29 上午
    [InlineData(11, 29, 15, 0)] // 周日下午
    public void IsOffPeak_WeekendIsAlwaysOffPeak(int month, int day, int hour, int minute)
    {
        Assert.True(BalanceService.IsOffPeak(Utc(month, day, hour, minute), NoHoliday));
    }

    [Theory]
    [InlineData(9, 0)]   // 本来是上午高峰的起点
    [InlineData(10, 0)]
    [InlineData(15, 0)]  // 本来是下午高峰中间
    [InlineData(17, 59)] // 本来是下午高峰的最后一分钟
    public void IsOffPeak_HolidayIsAlwaysOffPeak(int hour, int minute)
    {
        // 2026-10-01 是周四：注入「放假」后同一时刻从高峰翻成闲时。
        Assert.True(BalanceService.IsOffPeak(Utc(10, 1, hour, minute), IsHoliday));
        Assert.False(BalanceService.IsOffPeak(Utc(10, 1, hour, minute), NoHoliday));
    }

    [Fact]
    public void IsOffPeak_HolidayOnWeekendStaysOffPeak()
    {
        // 节假日判定落在周末也必须闲时（两个条件不能互相抵消）。
        Assert.True(BalanceService.IsOffPeak(Utc(10, 3, 10, 0), IsHoliday)); // 2026-10-03 是周六
    }

    // ---------------------------------------------------------------- UntilNextSwitch

    [Theory]
    // 周一 2026-11-23 08:59 —— 高峰前 1 分钟。
    [InlineData(11, 23, 8, 59, 0, 1)]
    // 周一 10:00 —— 上午高峰中，到 12:00。
    [InlineData(11, 23, 10, 0, 2, 0)]
    // 周一 12:30 —— 午间闲时，到 14:00。
    [InlineData(11, 23, 12, 30, 1, 30)]
    // 周一 15:00 —— 下午高峰中，到 18:00。
    [InlineData(11, 23, 15, 0, 3, 0)]
    // 周一 18:00 —— 晚间闲时，到周二 09:00。
    [InlineData(11, 23, 18, 0, 15, 0)]
    // 周一 19:00 —— 晚间闲时，到周二 09:00。
    [InlineData(11, 23, 19, 0, 14, 0)]
    // 周五 2026-11-27 19:00 —— 跨周末，到周一 09:00 = 62 小时。
    [InlineData(11, 27, 19, 0, 62, 0)]
    // 周六 2026-11-28 10:00 —— 到周一 09:00 = 47 小时。
    [InlineData(11, 28, 10, 0, 47, 0)]
    // 周日 2026-11-29 15:00 —— 到周一 09:00 = 18 小时。
    [InlineData(11, 29, 15, 0, 18, 0)]
    public void UntilNextSwitch_MatchesWorkdayPeriods(
        int month, int day, int hour, int minute, int spanHours, int spanMinutes)
    {
        var actual = BalanceService.UntilNextSwitch(Utc(month, day, hour, minute), NoHoliday);
        Assert.Equal(new TimeSpan(spanHours, spanMinutes, 0), actual);
    }

    [Theory]
    // 节假日 2026-10-01（周四）10:00 —— 整个长假闲时，到 10-05 周一 09:00 = 95 小时。
    [InlineData(10, 1, 10, 0, 95, 0)]
    // 节假日 2026-10-01 12:00 —— 到 10-05 周一 09:00 = 93 小时。
    [InlineData(10, 1, 12, 0, 93, 0)]
    // 节假日 2026-10-02（周五）19:00 —— 周末连休后 10-05 周一 09:00 = 62 小时。
    [InlineData(10, 2, 19, 0, 62, 0)]
    public void UntilNextSwitch_SkipsHolidays(
        int month, int day, int hour, int minute, int spanHours, int spanMinutes)
    {
        var actual = BalanceService.UntilNextSwitch(Utc(month, day, hour, minute), IsHoliday);
        Assert.Equal(new TimeSpan(spanHours, spanMinutes, 0), actual);
    }

    [Theory]
    [InlineData(11, 23, 0, 0)]   // 深夜闲时
    [InlineData(11, 23, 8, 59)]  // 高峰前 1 分钟
    [InlineData(11, 23, 9, 0)]   // 上午高峰起点
    [InlineData(11, 23, 12, 0)]  // 午间闲时起点
    [InlineData(11, 23, 13, 59)] // 午间闲时终点前
    [InlineData(11, 23, 14, 0)]  // 下午高峰起点
    [InlineData(11, 23, 18, 0)]  // 晚间闲时起点
    [InlineData(11, 23, 23, 59)] // 深夜闲时
    [InlineData(11, 27, 17, 30)] // 周五下午高峰里
    [InlineData(11, 28, 10, 0)]  // 周六
    [InlineData(11, 29, 23, 59)] // 周日深夜
    [InlineData(10, 1, 10, 0)]   // 节假日
    [InlineData(10, 2, 12, 0)]   // 节假日午间
    public void UntilNextSwitch_AgreesWithIsOffPeak(int month, int day, int hour, int minute)
    {
        var now = Utc(month, day, hour, minute);
        var span = BalanceService.UntilNextSwitch(now, IsHoliday);

        // 恒为正且小于防御上限（400 天）。
        Assert.InRange(span, TimeSpan.FromTicks(1), TimeSpan.FromDays(400));

        // 倒计时归零的那一刻，计价时段必须真的翻面。
        Assert.NotEqual(
            BalanceService.IsOffPeak(now, IsHoliday),
            BalanceService.IsOffPeak(now + span, IsHoliday));
    }

    [Fact]
    public void UntilNextSwitch_NeverNegative()
    {
        // 一天里的每个整点都不能返回负数（跨周末 / 跨长假时数值可以远大于 24 小时）。
        for (var hour = 0; hour < 24; hour++)
        {
            var span = BalanceService.UntilNextSwitch(Utc(11, 27, hour, 0), IsHoliday);
            Assert.InRange(span, TimeSpan.Zero, TimeSpan.FromDays(400));
        }
    }

    // ---------------------------------------------------------------- 常量与公开 API

    [Fact]
    public void PeakWindow_ConstantsMatchOfficialRule()
    {
        // 边界只有这一份来源，且是左闭右开。
        Assert.Equal(9 * 60, BalanceService.PeakMorningStart);
        Assert.Equal(12 * 60, BalanceService.PeakMorningEnd);
        Assert.Equal(14 * 60, BalanceService.PeakAfternoonStart);
        Assert.Equal(18 * 60, BalanceService.PeakAfternoonEnd);

        Assert.False(BalanceService.IsOffPeak(Utc(11, 23, 9, 0), NoHoliday));
        Assert.True(BalanceService.IsOffPeak(Utc(11, 23, 12, 0), NoHoliday));
        Assert.False(BalanceService.IsOffPeak(Utc(11, 23, 14, 0), NoHoliday));
        Assert.True(BalanceService.IsOffPeak(Utc(11, 23, 18, 0), NoHoliday));
    }

    [Fact]
    public void PublicOverload_UsesHolidayCalendarWithoutThrowing()
    {
        // 公开重载接的是 ChinaHolidays（未加载/无数据时按星期判定，绝不能抛异常）。
        // 周六必闲时与年表数据无关；跨周末的倒计时只会因为撞上更多假日而变长，不可能少于 47 小时。
        Assert.True(BalanceService.IsOffPeak(Utc(11, 28, 10, 0)));
        Assert.True(BalanceService.UntilNextSwitch(Utc(11, 28, 10, 0)) >= TimeSpan.FromHours(47));
    }
}

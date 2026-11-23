using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// ChinaHolidays 的两个解析器：纯函数、纯离线（内联 JSON），不调用 EnsureAsync、不访问网络。
/// 两个数据源都会返回「补班日」条目，判定时只能收放假的那一侧。
/// </summary>
public class ChinaHolidaysTests
{
    /// <summary>timor.tech 年度接口的裁剪样本：1 个放假、1 个补班、1 个跨年条目。</summary>
    private const string TimorJson = """
        {
          "code": 0,
          "holiday": {
            "01-01": { "holiday": true,  "name": "元旦",       "wage": 3, "date": "2026-01-01" },
            "01-04": { "holiday": false, "name": "元旦后补班", "wage": 1, "date": "2026-01-04" },
            "12-31": { "holiday": true,  "name": "跨年条目",   "wage": 3, "date": "2025-12-31" }
          }
        }
        """;

    /// <summary>holiday-cn 年表的裁剪样本：1 个放假、1 个补班、1 个跨年条目。</summary>
    private const string HolidayCnJson = """
        {
          "year": 2026,
          "papers": ["国务院办公厅关于 2026 年部分节假日安排的通知"],
          "days": [
            { "name": "元旦",       "date": "2026-01-01", "isOffDay": true },
            { "name": "元旦后补班", "date": "2026-01-04", "isOffDay": false },
            { "name": "跨年条目",   "date": "2027-01-01", "isOffDay": true }
          ]
        }
        """;

    [Fact]
    public void TryParseTimor_OnlyTakesOffDaysInTargetYear()
    {
        Assert.True(ChinaHolidays.TryParseTimor(TimorJson, 2026, out var days));

        // 补班（holiday:false）与跨年（2025-12-31）都被过滤掉。
        Assert.Equal(new DateOnly(2026, 1, 1), Assert.Single(days));
    }

    [Fact]
    public void TryParseTimor_CrossYearOnly_ReturnsFalse()
    {
        // 目标年一条都没有（只有邻年数据）等同于解析失败。
        Assert.False(ChinaHolidays.TryParseTimor(TimorJson, 2027, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void TryParseTimor_RejectsNonZeroCode()
    {
        const string json = """{"code":1,"holiday":{"01-01":{"holiday":true,"date":"2026-01-01"}}}""";

        Assert.False(ChinaHolidays.TryParseTimor(json, 2026, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void TryParseTimor_RejectsEmptyHolidayObject()
    {
        const string json = """{"code":0,"holiday":{}}""";

        Assert.False(ChinaHolidays.TryParseTimor(json, 2026, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void TryParseTimor_RejectsGarbage()
    {
        Assert.False(ChinaHolidays.TryParseTimor("这不是 JSON", 2026, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void TryParseHolidayCn_OnlyTakesOffDaysInTargetYear()
    {
        Assert.True(ChinaHolidays.TryParseHolidayCn(HolidayCnJson, 2026, out var days));

        // 补班（isOffDay:false）与跨年（2027-01-01）都被过滤掉。
        Assert.Equal(new DateOnly(2026, 1, 1), Assert.Single(days));
    }

    [Fact]
    public void TryParseHolidayCn_RejectsMissingDays()
    {
        const string json = """{"year":2026,"papers":[]}""";

        Assert.False(ChinaHolidays.TryParseHolidayCn(json, 2026, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void TryParseHolidayCn_RejectsGarbage()
    {
        Assert.False(ChinaHolidays.TryParseHolidayCn("<html>404 Not Found</html>", 2026, out var days));
        Assert.Empty(days);
    }

    [Fact]
    public void IsHoliday_UnknownYear_ReturnsFalse()
    {
        // 1980 年既不会来自缓存也不会来自网络：未命中即 false，且不能抛异常。
        // 这一句同时覆盖「首次调用懒加载缓存文件」的路径（纯本地，不碰网络）。
        Assert.False(ChinaHolidays.IsHoliday(new DateOnly(1980, 10, 1)));
    }
}

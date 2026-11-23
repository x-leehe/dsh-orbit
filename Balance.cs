using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshOrbit;

/// <summary>DeepSeek 余额 + 按余额下降累计的“今日已用”。</summary>
internal sealed class BalanceService : IDisposable
{
    private static readonly Regex KeyPattern = new(
        @"DEEPSEEK_API_KEY\s*:\s*[""']?(sk-[A-Za-z0-9_\-]+)",
        RegexOptions.Compiled);

    private static readonly TimeZoneInfo Beijing = ResolveBeijing();

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private UsageRecord _usage;
    private string? _key;
    private string? _keySource;

    public BalanceService()
    {
        _usage = JsonIo.Load(AppPaths.UsageFile, () => new UsageRecord());
    }

    public decimal? Balance { get; private set; }
    public decimal? SpentToday { get; private set; }
    public decimal? TopUpToday { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastSuccess { get; private set; }
    public string? KeySource => _keySource;
    public bool HasKey => !string.IsNullOrWhiteSpace(_key);

    // ---------------------------------------------------------------- 峰谷
    // DeepSeek 官方计价（2026-08-17 起生效，来源：api-docs.deepseek.com/zh-cn/quick_start/pricing）：
    //   高峰时段 = 北京时间 周一至周五（不含中国法定节假日）09:00–12:00、14:00–18:00（左闭右开：
    //   09:00 算高峰、12:00 算闲时、14:00 算高峰、18:00 算闲时）；
    //   其余一切时间都是闲时：周末、法定节假日全天、工作日 12:00–14:00 与 18:00–次日 09:00；
    //   闲时价 = 高峰价的一半（挂件只显示「是否闲时」和「距下次切换」，不涉及具体金额）。
    // 法定节假日年表由 ChinaHolidays 运行期拉取（timor.tech → holiday-cn → 本地缓存 → 仅按星期）。

    /// <summary>上午高峰起点：北京时间 09:00（当日第 540 分钟，闭区间起点）。</summary>
    public const int PeakMorningStart = 9 * 60;

    /// <summary>上午高峰终点：北京时间 12:00（当日第 720 分钟，开区间终点）。</summary>
    public const int PeakMorningEnd = 12 * 60;

    /// <summary>下午高峰起点：北京时间 14:00（当日第 840 分钟，闭区间起点）。</summary>
    public const int PeakAfternoonStart = 14 * 60;

    /// <summary>下午高峰终点：北京时间 18:00（当日第 1080 分钟，开区间终点）。</summary>
    public const int PeakAfternoonEnd = 18 * 60;

    /// <summary>向后找下一次高峰开始的防御上限（正常情况下最多扫到下一个工作日）。</summary>
    private const int MaxScanDays = 400;

    /// <summary>当前是否闲时（官方 2026 规则：工作日 9–12 / 14–18 之外，以及周末、法定节假日都是闲时）。</summary>
    public static bool IsOffPeak(DateTime utcNow) => IsOffPeak(utcNow, ChinaHolidays.IsHoliday);

    /// <summary>可注入节假日判定的重载（单测用）。</summary>
    internal static bool IsOffPeak(DateTime utcNow, Func<DateOnly, bool> isHoliday)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Beijing);
        if (!IsPeakDay(DateOnly.FromDateTime(local), isHoliday))
        {
            return true;
        }

        var minutes = local.Hour * 60 + local.Minute;
        var inMorning = minutes >= PeakMorningStart && minutes < PeakMorningEnd;
        var inAfternoon = minutes >= PeakAfternoonStart && minutes < PeakAfternoonEnd;
        return !(inMorning || inAfternoon);
    }

    /// <summary>距离下一次计价时段切换的时长：高峰 → 到本段结束；闲时 → 到下一次高峰开始。恒为非负。</summary>
    public static TimeSpan UntilNextSwitch(DateTime utcNow) => UntilNextSwitch(utcNow, ChinaHolidays.IsHoliday);

    /// <summary>可注入节假日判定的重载（单测用）。</summary>
    internal static TimeSpan UntilNextSwitch(DateTime utcNow, Func<DateOnly, bool> isHoliday)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Beijing);
        var day = DateOnly.FromDateTime(local);
        var minutes = local.Hour * 60 + local.Minute;
        var peakDay = IsPeakDay(day, isHoliday);

        DateTime target;
        if (peakDay && minutes >= PeakMorningStart && minutes < PeakMorningEnd)
        {
            // 上午高峰中：等到 12:00 进入午间闲时。
            target = local.Date.AddMinutes(PeakMorningEnd);
        }
        else if (peakDay && minutes >= PeakAfternoonStart && minutes < PeakAfternoonEnd)
        {
            // 下午高峰中：等到 18:00 进入晚间闲时。
            target = local.Date.AddMinutes(PeakAfternoonEnd);
        }
        else
        {
            target = NextPeakStart(local, day, minutes, isHoliday);
        }

        var span = target - local;
        // 目标时刻按构造必然晚于当前时刻，这里只做防御性归一化，保证调用方永远拿不到负数。
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    /// <summary>
    /// 下一次高峰开始的时刻：从今天开始逐日向后扫（最多 <see cref="MaxScanDays"/> 天防御死循环），
    /// 跳过周末与法定节假日；当天若是高峰日，还要看是不是还没到 09:00（等到 09:00）
    /// 或正处在 12:00–14:00 的午间闲时（等到 14:00），否则一律算到下一个高峰日的 09:00。
    /// </summary>
    private static DateTime NextPeakStart(
        DateTime local, DateOnly day, int minutes, Func<DateOnly, bool> isHoliday)
    {
        for (var offset = 0; offset < MaxScanDays; offset++)
        {
            var candidate = day.AddDays(offset);
            if (!IsPeakDay(candidate, isHoliday))
            {
                continue;
            }

            var dayStart = local.Date.AddDays(offset);
            if (offset > 0)
            {
                return dayStart.AddMinutes(PeakMorningStart);
            }

            // 今天（高峰日）但不在两段高峰里：09:00 之前等 09:00，午休等 14:00，18:00 之后已无高峰。
            if (minutes < PeakMorningStart)
            {
                return dayStart.AddMinutes(PeakMorningStart);
            }

            if (minutes >= PeakMorningEnd && minutes < PeakAfternoonStart)
            {
                return dayStart.AddMinutes(PeakAfternoonStart);
            }
        }

        // 400 天内没有任何高峰日（只可能来自注入的极端节假日判定）：返回 0 而不是负数。
        return local;
    }

    /// <summary>高峰日 = 周一至周五且不是法定节假日。</summary>
    private static bool IsPeakDay(DateOnly date, Func<DateOnly, bool> isHoliday) =>
        date.DayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday && !isHoliday(date);

    public async Task RefreshAsync(WidgetConfig config, CancellationToken ct)
    {
        if (!config.ShowBalance)
        {
            return;
        }

        _key ??= ResolveKey(config);
        if (_key is null)
        {
            LastError = "未找到 DEEPSEEK_API_KEY";
            return;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Add("Authorization", $"Bearer {_key}");
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LastError = $"HTTP {(int)response.StatusCode}";
                return;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!TryParseBalance(json, out var balance))
            {
                LastError = "余额响应无法解析";
                return;
            }

            var now = DateTime.UtcNow;
            var today = TimeZoneInfo.ConvertTimeFromUtc(now, Beijing).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (!string.Equals(_usage.Date, today, StringComparison.Ordinal))
            {
                _usage = new UsageRecord { Date = today };
            }

            if (_usage.HasLastBalance)
            {
                var delta = _usage.LastBalance - balance;
                if (delta > 0)
                {
                    _usage.Spent += delta;
                }
                else if (delta < 0)
                {
                    _usage.TopUp += -delta;
                }
            }

            _usage.LastBalance = balance;
            _usage.HasLastBalance = true;
            _usage.Date = today;

            Balance = balance;
            SpentToday = _usage.Spent;
            TopUpToday = _usage.TopUp;
            LastSuccess = DateTime.Now;
            LastError = null;

            JsonIo.Save(AppPaths.UsageFile, _usage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static bool TryParseBalance(string json, out decimal balance)
    {
        balance = 0;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("balance_infos", out var infos) ||
                infos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            JsonElement? chosen = null;
            foreach (var info in infos.EnumerateArray())
            {
                var currency = info.TryGetProperty("currency", out var c) ? c.GetString() : null;
                if (string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = info;
                    break;
                }

                chosen ??= info;
            }

            if (chosen is null ||
                !chosen.Value.TryGetProperty("total_balance", out var total))
            {
                return false;
            }

            var text = total.ValueKind == JsonValueKind.Number
                ? total.GetRawText()
                : total.GetString();

            return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out balance);
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveKey(WidgetConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.DeepSeekApiKey))
        {
            _keySource = "config";
            return config.DeepSeekApiKey.Trim();
        }

        var fromEnv = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            _keySource = "env";
            return fromEnv.Trim();
        }

        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var credentials = Path.Combine(home, ".dsh", ".credentials.yaml");
            if (File.Exists(credentials))
            {
                var match = KeyPattern.Match(File.ReadAllText(credentials));
                if (match.Success)
                {
                    _keySource = "credentials.yaml";
                    return match.Groups[1].Value;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取凭据失败：{ex.Message}");
        }

        return null;
    }

    private static TimeZoneInfo ResolveBeijing()
    {
        foreach (var id in new[] { "China Standard Time", "Asia/Shanghai" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");
    }

    public void Dispose() => _http.Dispose();
}

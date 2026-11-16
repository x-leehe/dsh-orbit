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

    /// <summary>闲时窗口起点：北京时间 00:30（当日第 30 分钟，闭区间起点）。</summary>
    public const int OffPeakStartMinute = 30;

    /// <summary>闲时窗口终点：北京时间 08:30（当日第 510 分钟，开区间终点）。</summary>
    public const int OffPeakEndMinute = 8 * 60 + 30;

    /// <summary>北京时间 00:30–08:30 为 DeepSeek 的优惠时段（左闭右开）。</summary>
    public static bool IsOffPeak(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Beijing);
        var minutes = local.Hour * 60 + local.Minute;
        return minutes >= OffPeakStartMinute && minutes < OffPeakEndMinute;
    }

    /// <summary>
    /// 距离下一次计价时段切换的时长（按北京时间算）：
    /// 闲时中 → 到当日 08:30；标准价且已过 00:30 → 到次日 00:30；标准价且未到 00:30 → 到当日 00:30。
    /// 与 <see cref="IsOffPeak"/> 同一套边界：返回值恒为非负且小于 24 小时
    /// （闲时最长 8h、标准价最长 16h，两段不对称）。
    /// </summary>
    public static TimeSpan UntilNextSwitch(DateTime utcNow)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(utcNow, Beijing);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var startToday = midnight.AddMinutes(OffPeakStartMinute);
        var endToday = midnight.AddMinutes(OffPeakEndMinute);

        DateTime target;
        if (local < startToday)
        {
            // 北京 00:00–00:30：标准价，等到今日 00:30 进入闲时。
            target = startToday;
        }
        else if (local < endToday)
        {
            // 北京 00:30–08:30：闲时中，等到今日 08:30 回到标准价。
            target = endToday;
        }
        else
        {
            // 北京 08:30–24:00：标准价，等到次日 00:30。
            target = startToday.AddDays(1);
        }

        var span = target - local;
        // 目标时刻按构造必然晚于当前时刻（最多 16h），这里只做防御性归一化，
        // 保证调用方永远不会拿到负数。
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

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

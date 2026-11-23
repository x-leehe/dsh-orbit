using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DshOrbit;

/// <summary>
/// 中国法定节假日年表：运行期拉取 + 本地缓存 + 降级，供「峰谷」判定用。
///
/// 官方计价规则把「周一至周五（不含法定节假日）9:00–12:00、14:00–18:00」算高峰，
/// 所以挂件必须知道哪几天是法定节假日。年表变化很慢（每年国务院发一次通知），
/// 但程序要能在新版通知发布后自动跟上，因此设计成：
///
/// ① 判定热路径（<see cref="IsHoliday"/>）只做内存字典查找（每秒会被 UI 快照调用）；
/// ② 年表由 <see cref="EnsureAsync"/> 在启动与长周期定时器里后台刷新（不阻塞 UI、不抛异常）；
/// ③ 拉取逐级降级：timor.tech → jsDelivr / raw 的 holiday-cn → 本地缓存 holidays.json → 仅按星期；
/// ④ 任何失败都只记日志：拿不到年表时 <see cref="IsHoliday"/> 一律返回 false（近似为「没有节假日」）。
/// </summary>
internal static class ChinaHolidays
{
    /// <summary>刷新成功后的最短间隔：年表 20 小时内不重复拉。</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(20);

    /// <summary>拉取失败后的冷却时间：30 分钟内不再试。</summary>
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(30);

    /// <summary>HTTP 超时：接口都在国内可直连，8 秒足够，超时就降级。</summary>
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(8);

    /// <summary>进程级 HttpClient 单例（挂件只有一个进程，别每次拉建一个）。</summary>
    private static readonly HttpClient Http = CreateHttp();

    /// <summary>防重入：拿不到锁的那次调用直接返回，不排队。</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>「还没加载完」时本次调用用的空表（只读，不发布）。</summary>
    private static readonly Dictionary<int, HashSet<DateOnly>> Empty = new();

    /// <summary>
    /// 年表本体：年份 → 该年放假日期集合。<see cref="IsHoliday"/> 只读这个引用，
    /// 刷新时整表替换（发布出去的表不再修改），所以热路径没有锁、没有文件 IO。
    /// </summary>
    private static Dictionary<int, HashSet<DateOnly>>? _years;

    /// <summary>缓存文件的懒加载只做一次：0 = 未开始，1 = 已开始/已完成。</summary>
    private static int _cacheLoadStarted;

    /// <summary>上次尝试拉取的时刻（成功或失败都算），UTC ticks；0 = 从未尝试。</summary>
    private static long _lastAttemptTicks;

    /// <summary>上次成功拉取的时刻，UTC ticks；0 = 从未成功。</summary>
    private static long _lastSuccessTicks;

    /// <summary>「仅按星期判定」的日志只写一次，免得 30 分钟刷一条。</summary>
    private static int _weekdayOnlyLogged;

    /// <summary>「沿用本地缓存」的日志只写一次。</summary>
    private static int _cacheFallbackLogged;

    /// <summary>某天是否是法定节假日（含调休放假的整天）。“补班日”不算。</summary>
    public static bool IsHoliday(DateOnly date)
    {
        // 热路径：只读引用 + 字典查找。第一次调用会懒加载一次缓存文件，之后不再碰磁盘。
        var years = Volatile.Read(ref _years) ?? LoadCacheOnce();
        return years.TryGetValue(date.Year, out var days) && days.Contains(date);
    }

    /// <summary>启动/定时调用：缺年表或超过 20 小时没刷新时，后台拉一次（不抛异常、不阻塞 UI）。</summary>
    public static async Task EnsureAsync(CancellationToken ct)
    {
        try
        {
            // 已有一次刷新在跑：本次直接放弃，不排队。
            if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"节假年表刷新跳过：{ex.Message}");
            return;
        }

        try
        {
            await RefreshIfNeededAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 网络/文件/解析的问题一律吞掉：年表坏了也不能把心跳或启动搞崩。
            AppPaths.Log($"节假年表刷新异常：{ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    // ---------------------------------------------------------------- 刷新

    private static async Task RefreshIfNeededAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, BeijingNow());
        var years = LocalYears(local);

        // 复制一份再改：已发布出去的表不能被就地修改。
        var data = new Dictionary<int, HashSet<DateOnly>>(Cache());
        var lastSuccess = ReadTime(ref _lastSuccessTicks);
        var lastAttempt = ReadTime(ref _lastAttemptTicks);

        var stale = lastSuccess is null || now - lastSuccess.Value >= RefreshInterval;
        var missing = new List<int>();
        foreach (var year in years)
        {
            if (!data.ContainsKey(year))
            {
                missing.Add(year);
            }
        }

        if (!stale && missing.Count == 0)
        {
            // 年表齐全且 20 小时内刚刷新过：什么都不做。
            return;
        }

        // 上一次尝试是失败的（没有比它更晚的成功）→ 30 分钟内不重试。
        var lastAttemptFailed = lastAttempt is not null &&
            (lastSuccess is null || lastSuccess.Value < lastAttempt.Value);
        if (lastAttemptFailed && now - lastAttempt!.Value < RetryDelay)
        {
            LogFallback(data);
            return;
        }

        Interlocked.Exchange(ref _lastAttemptTicks, now.Ticks);

        var errors = new List<string>();
        var fetched = 0;
        foreach (var year in years)
        {
            if (!stale && data.ContainsKey(year))
            {
                continue;
            }

            var outcome = await FetchYearAsync(year, ct).ConfigureAwait(false);
            if (outcome.Days is { } days)
            {
                data[year] = days;
                fetched++;
                AppPaths.Log($"节假年表已更新：{year}（{days.Count} 天，来源 {outcome.Source}）");
            }
            else if (outcome.Error is { } error)
            {
                errors.Add(error);
            }
        }

        if (fetched > 0)
        {
            Interlocked.Exchange(ref _lastSuccessTicks, now.Ticks);
            Volatile.Write(ref _years, data);
            WriteCache(data, now);
            return;
        }

        if (errors.Count > 0)
        {
            AppPaths.Log($"节假年表拉取失败（{string.Join("；", errors)}）");
        }

        LogFallback(data);
    }

    /// <summary>降级日志：有多年的本地缓存就记一条「沿用缓存」，完全没有就记一条「仅按星期」。</summary>
    private static void LogFallback(Dictionary<int, HashSet<DateOnly>> data)
    {
        if (data.Count > 0)
        {
            if (Interlocked.Exchange(ref _cacheFallbackLogged, 1) == 0)
            {
                AppPaths.Log("节假年表降级：沿用本地缓存的年表");
            }

            return;
        }

        if (Interlocked.Exchange(ref _weekdayOnlyLogged, 1) == 0)
        {
            AppPaths.Log("节假日年表不可用：仅按星期判定");
        }
    }

    /// <summary>
    /// 取某一年的年表：timor.tech → holiday-cn（jsDelivr，失败再 raw）。
    /// 任一成功即返回；全失败时 <c>Days</c> 为 null，<c>Error</c> 里带两边的失败原因。
    /// </summary>
    private static async Task<(HashSet<DateOnly>? Days, string Source, string? Error)> FetchYearAsync(
        int year, CancellationToken ct)
    {
        var yearText = year.ToString(CultureInfo.InvariantCulture);
        var errors = new List<string>();

        // ① timor.tech：国内、免 Key，补班日为 holiday:false。
        try
        {
            var json = await GetAsync($"https://timor.tech/api/holiday/year/{yearText}", ct).ConfigureAwait(false);
            if (TryParseTimor(json, year, out var days))
            {
                return (days, "timor", null);
            }

            errors.Add("timor：响应无法解析");
        }
        catch (Exception ex)
        {
            errors.Add($"timor：{Describe(ex)}");
        }

        // ② holiday-cn：GitHub 镜像，jsDelivr 优先、raw.githubusercontent 兜底。
        var mirrors = new[]
        {
            $"https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/{yearText}.json",
            $"https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/{yearText}.json",
        };
        foreach (var url in mirrors)
        {
            try
            {
                var json = await GetAsync(url, ct).ConfigureAwait(false);
                if (TryParseHolidayCn(json, year, out var days))
                {
                    return (days, "holiday-cn", null);
                }

                errors.Add("holiday-cn：响应无法解析");
            }
            catch (Exception ex)
            {
                errors.Add($"holiday-cn：{Describe(ex)}");
            }
        }

        return (null, "", errors.Count > 0 ? string.Join("；", errors) : "未知原因");
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "超时",
        HttpRequestException http => $"请求失败（{http.StatusCode?.ToString() ?? "无状态码"}）",
        _ => ex.Message,
    };

    // ---------------------------------------------------------------- 解析（纯函数，单测直接调）

    /// <summary>
    /// 解析 timor.tech 的年度接口：
    /// <c>{"code":0,"holiday":{"01-01":{"holiday":true,"date":"2026-01-01"}, ...}}</c>。
    /// 只收 <c>holiday == true</c>（补班日为 false），并过滤掉不属于 <paramref name="year"/> 的跨年条目。
    /// </summary>
    internal static bool TryParseTimor(string json, int year, out HashSet<DateOnly> days)
    {
        days = new HashSet<DateOnly>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("code", out var code) ||
                code.ValueKind != JsonValueKind.Number ||
                !code.TryGetInt32(out var codeValue) ||
                codeValue != 0)
            {
                return false;
            }

            if (!root.TryGetProperty("holiday", out var holiday) || holiday.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var found = new HashSet<DateOnly>();
            foreach (var entry in holiday.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object ||
                    !entry.Value.TryGetProperty("holiday", out var flag) ||
                    !IsTrue(flag) ||
                    !entry.Value.TryGetProperty("date", out var dateProperty) ||
                    dateProperty.ValueKind != JsonValueKind.String ||
                    !TryParseDate(dateProperty.GetString(), out var day) ||
                    day.Year != year)
                {
                    continue;
                }

                found.Add(day);
            }

            if (found.Count == 0)
            {
                return false;
            }

            days = found;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析 holiday-cn 的年表：<c>{"year":2026,"days":[{"date":"2026-01-01","isOffDay":true}, ...]}</c>。
    /// 只收 <c>isOffDay == true</c>（补班日为 false），跨年条目同样过滤。
    /// </summary>
    internal static bool TryParseHolidayCn(string json, int year, out HashSet<DateOnly> days)
    {
        days = new HashSet<DateOnly>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("days", out var array) ||
                array.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var found = new HashSet<DateOnly>();
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("isOffDay", out var flag) ||
                    !IsTrue(flag) ||
                    !item.TryGetProperty("date", out var dateProperty) ||
                    dateProperty.ValueKind != JsonValueKind.String ||
                    !TryParseDate(dateProperty.GetString(), out var day) ||
                    day.Year != year)
                {
                    continue;
                }

                found.Add(day);
            }

            if (found.Count == 0)
            {
                return false;
            }

            days = found;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>JSON 里的布尔：兼容 <c>true</c> / <c>"true"</c> / <c>1</c> 三种写法。</summary>
    private static bool IsTrue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => bool.TryParse(element.GetString(), out var value) && value,
        JsonValueKind.Number => element.TryGetInt32(out var number) && number != 0,
        _ => false,
    };

    private static bool TryParseDate(string? text, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    // ---------------------------------------------------------------- 缓存文件

    /// <summary>缓存文件格式：<c>{ "FetchedUtc": "...", "Years": { "2026": ["2026-01-01", ...] } }</c>。</summary>
    private sealed class CacheFile
    {
        public string? FetchedUtc { get; set; }
        public Dictionary<string, List<string>> Years { get; set; } = new();
    }

    /// <summary>当前生效的年表引用（必要时懒加载缓存文件一次）。</summary>
    private static Dictionary<int, HashSet<DateOnly>> Cache()
    {
        return Volatile.Read(ref _years) ?? LoadCacheOnce();
    }

    /// <summary>
    /// 懒加载缓存文件，只做一次。并发调用里抢不到的那一侧返回空表（本次按「无年表」处理），
    /// 下一次调用就能读到发布好的引用——判定路径绝不能等 IO。
    /// </summary>
    private static Dictionary<int, HashSet<DateOnly>> LoadCacheOnce()
    {
        if (Interlocked.Exchange(ref _cacheLoadStarted, 1) == 1)
        {
            return Empty;
        }

        var loaded = ReadCacheFile();
        Volatile.Write(ref _years, loaded);
        return loaded;
    }

    private static Dictionary<int, HashSet<DateOnly>> ReadCacheFile()
    {
        var years = new Dictionary<int, HashSet<DateOnly>>();
        try
        {
            var cache = JsonIo.Load(AppPaths.HolidaysFile, () => new CacheFile());

            // JsonIo 已经吞掉了「读不到 / 反序列化失败」；这里再兜一层结构异常（例如 Years 为 null）。
            if (cache.Years is not null)
            {
                foreach (var pair in cache.Years)
                {
                    if (!int.TryParse(pair.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
                        pair.Value is null)
                    {
                        continue;
                    }

                    var days = new HashSet<DateOnly>();
                    foreach (var text in pair.Value)
                    {
                        if (TryParseDate(text, out var day) && day.Year == year)
                        {
                            days.Add(day);
                        }
                    }

                    if (days.Count > 0)
                    {
                        years[year] = days;
                    }
                }
            }

            // 缓存里的时间戳也算「上次成功」：这样 20 小时内不会为了同一份数据反复拉。
            if (DateTime.TryParse(
                    cache.FetchedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                    out var fetched))
            {
                Interlocked.Exchange(ref _lastSuccessTicks, fetched.ToUniversalTime().Ticks);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"节假年表缓存解析失败：{ex.Message}");
        }

        return years;
    }

    /// <summary>写缓存：失败只记日志（<see cref="JsonIo.Save"/> 已经吞掉异常），不影响判定。</summary>
    private static void WriteCache(Dictionary<int, HashSet<DateOnly>> years, DateTime fetchedUtc)
    {
        var payload = new CacheFile
        {
            FetchedUtc = fetchedUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Years = new Dictionary<string, List<string>>(),
        };

        foreach (var pair in years)
        {
            payload.Years[pair.Key.ToString(CultureInfo.InvariantCulture)] = pair.Value
                .OrderBy(day => day)
                .Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();
        }

        JsonIo.Save(AppPaths.HolidaysFile, payload);
    }

    // ---------------------------------------------------------------- 时间工具

    /// <summary>需要的年份：当前北京年份；12 月顺带把次年一起预取（元旦通知一般 11 月就出了）。</summary>
    private static List<int> LocalYears(DateTime local)
    {
        var years = new List<int> { local.Year };
        if (local.Month == 12)
        {
            years.Add(local.Year + 1);
        }

        return years;
    }

    /// <summary>北京时间：与 <see cref="BalanceService"/> 同一套解析（本文件保持自足，不反向依赖）。</summary>
    private static TimeZoneInfo BeijingNow()
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

    private static DateTime? ReadTime(ref long ticks)
    {
        var value = Interlocked.Read(ref ticks);
        return value == 0 ? null : new DateTime(value, DateTimeKind.Utc);
    }

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = HttpTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DSH-Orbit/0.2");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }
}

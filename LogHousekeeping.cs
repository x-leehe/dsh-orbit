namespace DshOrbit;

/// <summary>
/// 日志清理：只动挂件自己的数据目录。
///
/// 背景：`widget.log` 是纯追加、无上限；`logs\dsh-*.log` 每次拉起 dsh 一个，
/// 里面还带着明文 token（12 小时有效）。空间不是问题（总共 0.1 MB），
/// 但文件数只增不减、异常循环时 widget.log 可能暴涨，所以做两件事：
/// ① 自动保留：dsh 日志留最近若干份 / 若干天，widget.log 超 2 MB 轮转一份备份；
/// ② 手动清理：设置面板的「清理日志」——轮转当前日志并删掉旧 dsh 日志。
///
/// 正在写的文件删不掉时直接跳过（不递归强删），也永远不删最新的一份。
/// </summary>
internal static class LogHousekeeping
{
    /// <summary>保留最近多少个 dsh 运行日志（无论多旧）。</summary>
    public const int KeepDshLogs = 20;

    /// <summary>超出保留数量后，只删除早于这么多天的。</summary>
    public const int KeepDays = 14;

    /// <summary>dsh 日志总量兜底（超出时从最旧的开始删，但不动最新 3 份）。</summary>
    public const long MaxLogBytes = 20L * 1024 * 1024;

    /// <summary>widget.log 超过这个大小就轮转。</summary>
    public const long MaxWidgetLogBytes = 2L * 1024 * 1024;

    /// <summary>一次清理要处理的文件快照（只读，纯数据，便于单测）。</summary>
    internal sealed record Entry(string Path, DateTime LastWrite, long Length);

    private static long _lastRunTicks;
    private static int _running;

    /// <summary>启动 / 每日调用：24 小时内只跑一次，后台线程，失败只记日志。</summary>
    public static void RunIfDue()
    {
        var now = DateTime.UtcNow;
        var last = new DateTime(Interlocked.Read(ref _lastRunTicks), DateTimeKind.Utc);
        if (now - last < TimeSpan.FromHours(24))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _lastRunTicks, now.Ticks);
        _ = Task.Run(() =>
        {
            try
            {
                Run();
            }
            catch (Exception ex)
            {
                AppPaths.Log($"日志清理失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        });
    }

    /// <summary>按保留策略执行一次（轮转 widget.log + 删过期 dsh 日志）。</summary>
    public static void Run()
    {
        RotateWidgetLog(force: false);

        var files = ReadEntries();
        if (files.Count == 0)
        {
            return;
        }

        var deleted = 0;
        foreach (var entry in SelectExpired(files, DateTime.Now))
        {
            if (TryDelete(entry.Path))
            {
                deleted++;
            }
        }

        if (deleted > 0)
        {
            AppPaths.Log($"日志自动清理：删除 {deleted} 个过期 dsh 日志（保留最近 {KeepDshLogs} 份 / {KeepDays} 天）");
        }
    }

    /// <summary>
    /// 手动「清理日志」：轮转 widget.log（留一份 backup），并删除除最新一个以外的 dsh 日志。
    /// 返回删除数量与释放字节数；不会抛异常（删不掉的跳过）。
    /// </summary>
    public static (int Deleted, long Bytes) ClearAll()
    {
        RotateWidgetLog(force: true);

        var files = ReadEntries();
        var deleted = 0;
        var bytes = 0L;
        if (files.Count > 0)
        {
            foreach (var entry in files.OrderByDescending(f => f.LastWrite).Skip(1))
            {
                if (TryDelete(entry.Path))
                {
                    deleted++;
                    bytes += entry.Length;
                }
            }
        }

        AppPaths.Log($"日志已清理：{deleted} 个 dsh 日志（{bytes / 1024.0:0.0} KB），widget.log 已轮转");
        return (deleted, bytes);
    }

    /// <summary>
    /// 纯选择：哪些 dsh 日志该删。
    /// 规则：按修改时间倒序，前 <see cref="KeepDshLogs"/> 份一律保留；其余只删超过 <see cref="KeepDays"/> 天的；
    /// 再做总量兜底：仍超过 <see cref="MaxLogBytes"/> 就从最旧的删（最新 3 份不动）。
    /// </summary>
    internal static IReadOnlyList<Entry> SelectExpired(IReadOnlyList<Entry> files, DateTime now)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var ordered = files
            .OrderByDescending(f => f.LastWrite)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var expired = ordered
            .Skip(KeepDshLogs)
            .Where(f => now - f.LastWrite > TimeSpan.FromDays(KeepDays))
            .ToList();

        var total = ordered.Sum(f => f.Length);
        for (var i = ordered.Count - 1; i >= 3 && total > MaxLogBytes; i--)
        {
            var entry = ordered[i];
            if (expired.Contains(entry))
            {
                continue;
            }

            expired.Add(entry);
            total -= entry.Length;
        }

        return expired;
    }

    private static List<Entry> ReadEntries()
    {
        var dir = AppPaths.LogDirectory;
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<Entry>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(dir, "dsh-*.log"))
            {
                try
                {
                    var info = new FileInfo(path);
                    result.Add(new Entry(path, info.LastWriteTime, info.Length));
                }
                catch
                {
                    // 读不到（被占用 / 刚被删）就跳过，下一轮再说。
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"枚举日志目录失败：{ex.Message}");
        }

        return result;
    }

    private static void RotateWidgetLog(bool force)
    {
        try
        {
            var path = AppPaths.LogFile;
            if (!File.Exists(path))
            {
                return;
            }

            if (!force && new FileInfo(path).Length < MaxWidgetLogBytes)
            {
                return;
            }

            File.Move(path, AppPaths.LogBackupFile, overwrite: true);
        }
        catch (Exception ex)
        {
            // 移动失败（文件被占用 / 磁盘权限）不影响主流程，下个周期再试。
            AppPaths.Log($"widget.log 轮转失败：{ex.Message}");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            // 被正在运行的 dsh 占用时会失败：跳过，绝不强删。
            return false;
        }
    }
}

using System.Text.Json;

namespace DshOrbit;

/// <summary>
/// 会话数探针（近似）：dsh 的权威来源是 <c>/api</c> RPC 的 <c>session.control</c> baseline，
/// 首版改用磁盘上的投影缓存做低成本近似——扫
/// <c>%USERPROFILE%\.dsh\storages\session_projcache\sessions\*.json</c>，
/// 统计 <c>record.rows.turnBoundary.val.openTurnStartSeq != null</c> 的会话数
/// （即「有 turn 在飞」）。只读这一个目录，不碰 dsh 本身。
/// </summary>
internal static class SessionProbe
{
    /// <summary>异常最多记一次，避免每次扫描都刷日志。</summary>
    private static int _logged;

    /// <summary>默认扫描目录：<c>%USERPROFILE%\.dsh\storages\session_projcache\sessions</c>。</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh",
        "storages",
        "session_projcache",
        "sessions");

    /// <summary>用默认目录统计「有 turn 在飞」的会话数；无法判定时返回 null。</summary>
    public static int? CountActiveSessions() => CountActiveSessions(DefaultRoot);

    /// <summary>
    /// 统计 <paramref name="root"/> 下「有 turn 在飞」的会话数。
    /// 返回 null 表示**无法判定**（目录不存在 / 无读权限 / IO 失败 / 全部文件都解析失败），
    /// 调用方据此显示「—」，不要当成 0。目录存在但没有文件则返回 0（确实没有会话）。
    /// </summary>
    internal static int? CountActiveSessions(string root)
    {
        string[] files;
        try
        {
            // 目录不存在会抛 DirectoryNotFoundException（IOException 的子类），统一走 null。
            files = Directory.GetFiles(root, "*.json");
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            LogOnce($"会话投影缓存无法读取：{root}（{ex.GetType().Name}: {ex.Message}）");
            return null;
        }

        if (files.Length == 0)
        {
            return 0;
        }

        var active = 0;
        var parsed = 0;
        var failed = 0;

        foreach (var file in files)
        {
            // 单个文件坏了就跳过，不影响整体；只有全坏才算「无法判定」。
            if (!TryReadOpenTurn(file, out var openTurn))
            {
                failed++;
                continue;
            }

            parsed++;
            if (openTurn)
            {
                active++;
            }
        }

        if (parsed == 0)
        {
            LogOnce($"会话投影缓存全部解析失败：{root}（{failed} 个文件）");
            return null;
        }

        return active;
    }

    /// <summary>
    /// 读取单个投影缓存文件，判断是否有 turn 在飞。
    /// 返回 false = 文件读不出/不是合法 JSON（调用方计入「解析失败」）；
    /// 返回 true 时 <paramref name="openTurn"/> 表示 <c>openTurnStartSeq</c> 是否非 null。
    /// 结构里没有这块投影（例如刚建会话还没写 turn）按「没有在飞 turn」处理。
    /// </summary>
    private static bool TryReadOpenTurn(string file, out bool openTurn)
    {
        openTurn = false;

        try
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream);

            // 只顺着 record.rows.turnBoundary.val.openTurnStartSeq 走一遍，不给整棵树建模型。
            if (!document.RootElement.TryGetProperty("record", out var record) ||
                !record.TryGetProperty("rows", out var rows) ||
                !rows.TryGetProperty("turnBoundary", out var turnBoundary) ||
                !turnBoundary.TryGetProperty("val", out var val) ||
                !val.TryGetProperty("openTurnStartSeq", out var openTurnStartSeq))
            {
                return true;
            }

            openTurn = openTurnStartSeq.ValueKind != JsonValueKind.Null;
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    /// <summary>预期内的失败：读盘失败、JSON 不合法、权限不足。其它异常照旧往上抛。</summary>
    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    /// <summary>只记第一条（磁盘常年读不到时不刷屏）。</summary>
    private static void LogOnce(string message)
    {
        if (Interlocked.Exchange(ref _logged, 1) != 0)
        {
            return;
        }

        AppPaths.Log(message);
    }
}

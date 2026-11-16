using System.Text.Json;
using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// SessionProbe：扫 session_projcache 的投影缓存，统计 openTurnStartSeq != null 的会话数。
/// 全部用临时目录构造假数据，不依赖本机 ~/.dsh。
/// </summary>
public class SessionProbeTests
{
    /// <summary>造一个临时扫描目录；测试结束整棵删掉。</summary>
    private sealed class TempRoot : IDisposable
    {
        public TempRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-orbit-sessions-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        /// <summary>写一个投影缓存文件，返回其路径。</summary>
        public string Write(string name, string json)
        {
            var file = System.IO.Path.Combine(Path, name);
            File.WriteAllText(file, json);
            return file;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // 临时目录删不掉不影响测试结论。
            }
        }
    }

    /// <summary>写一个最小可解析的投影缓存：record.rows.turnBoundary.val.openTurnStartSeq。</summary>
    private static string Projection(string openTurnStartSeq)
    {
        var seq = openTurnStartSeq;
        return $$"""
                 {
                   "version": 1,
                   "record": {
                     "identity": { "id": "test" },
                     "rows": {
                       "turnBoundary": {
                         "ver": 2,
                         "seq": 628,
                         "val": {
                           "openTurnStartSeq": {{seq}},
                           "lastStepStartSeq": 624,
                           "lastTurn": 1
                         }
                       }
                     }
                   }
                 }
                 """;
    }

    [Fact]
    public void CountActiveSessions_CountsOnlyOpenTurns_AndSkipsBrokenFiles()
    {
        using var root = new TempRoot();
        root.Write("active.json", Projection("12"));   // 有 turn 在飞
        root.Write("idle.json", Projection("null"));   // turn 已收尾
        root.Write("broken.json", "{ this is not json "); // 坏 JSON：跳过，不整体失败

        Assert.Equal(1, SessionProbe.CountActiveSessions(root.Path));
    }

    [Fact]
    public void CountActiveSessions_MissingDirectoryReturnsNull()
    {
        var missing = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dsh-orbit-missing-" + Guid.NewGuid().ToString("N"));

        // 目录不存在 = 无法判定，必须是 null 而不是 0。
        Assert.Null(SessionProbe.CountActiveSessions(missing));
    }

    [Fact]
    public void CountActiveSessions_EmptyDirectoryReturnsZero()
    {
        using var root = new TempRoot();

        // 目录在、但没有会话缓存 —— 确实没有会话，可以确定是 0。
        Assert.Equal(0, SessionProbe.CountActiveSessions(root.Path));
    }

    [Fact]
    public void CountActiveSessions_AllFilesBrokenReturnsNull()
    {
        using var root = new TempRoot();
        root.Write("a.json", "{ broken");
        root.Write("b.json", "not json at all");

        // 全部解析失败 = 无法判定，不能假装 0。
        Assert.Null(SessionProbe.CountActiveSessions(root.Path));
    }

    [Fact]
    public void CountActiveSessions_NonJsonFilesAreIgnored()
    {
        using var root = new TempRoot();
        root.Write("active.json", Projection("5"));
        root.Write("notes.txt", "不是 json，也不该被扫到");

        Assert.Equal(1, SessionProbe.CountActiveSessions(root.Path));
    }

    [Fact]
    public void CountActiveSessions_NullSequenceIsNotActive()
    {
        using var root = new TempRoot();
        root.Write("a.json", Projection("null"));
        root.Write("b.json", Projection("null"));

        Assert.Equal(0, SessionProbe.CountActiveSessions(root.Path));
    }

    [Fact]
    public void DefaultRoot_PointsAtDshProjectionCache()
    {
        // 默认路径必须落在 %USERPROFILE%\.dsh\storages\session_projcache\sessions。
        var expected = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "storages", "session_projcache", "sessions");

        Assert.Equal(expected, SessionProbe.DefaultRoot);
    }

    [Fact]
    public void Projection_IsValidJsonShape()
    {
        // 守住假数据本身：结构必须能按 openTurnStartSeq 读出来（防止测试假数据悄悄漂移）。
        using var document = JsonDocument.Parse(Projection("7"));
        var val = document.RootElement
            .GetProperty("record")
            .GetProperty("rows")
            .GetProperty("turnBoundary")
            .GetProperty("val");

        Assert.Equal(7, val.GetProperty("openTurnStartSeq").GetInt32());
    }
}

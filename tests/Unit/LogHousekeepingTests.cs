using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// LogHousekeeping.SelectExpired：保留策略的纯选择逻辑。
/// 核心约定：最新 N 份永远保留；超出的只删过期（&gt;14 天）的；总量兜底从最旧的删，最新 3 份不动。
/// </summary>
public class LogHousekeepingTests
{
    private static LogHousekeeping.Entry Entry(string name, int daysAgo, long length = 1024)
    {
        var path = $@"C:\logs\{name}";
        return new LogHousekeeping.Entry(path, DateTime.Now.AddDays(-daysAgo), length);
    }

    [Fact]
    public void EmptyList_NothingToDelete()
    {
        Assert.Empty(LogHousekeeping.SelectExpired([], DateTime.Now));
    }

    [Fact]
    public void KeepsNewestEvenIfAllAreOld()
    {
        var files = Enumerable.Range(0, 25)
            .Select(i => Entry($"dsh-{i:00}.log", daysAgo: 30 + i))
            .ToList();

        var expired = LogHousekeeping.SelectExpired(files, DateTime.Now);

        Assert.Equal(5, expired.Count);
        // 删的必须是最旧的 5 个
        var kept = files.Except(expired).ToList();
        Assert.Equal(20, kept.Count);
        Assert.DoesNotContain(kept, k => k.LastWrite < files.OrderByDescending(f => f.LastWrite).ElementAt(19).LastWrite);
    }

    [Fact]
    public void RecentFilesBeyondCount_AreKept()
    {
        // 24 份都是 1 天内的，只有最旧的一份是 30 天前 → 超过 20 份且过期，删 1 个
        var files = Enumerable.Range(0, 23)
            .Select(i => Entry($"dsh-new-{i:00}.log", daysAgo: 1, length: i + 1))
            .Append(Entry("dsh-old.log", daysAgo: 30))
            .ToList();

        var expired = LogHousekeeping.SelectExpired(files, DateTime.Now);

        Assert.Single(expired);
        Assert.Equal(@"C:\logs\dsh-old.log", expired[0].Path);
    }

    [Fact]
    public void TotalSizeCap_DeletesOldestButKeepsNewestThree()
    {
        // 30 份 × 1 MB = 30 MB，全部很新（不触发天数规则）→ 总量兜底删到 20 MB
        var files = Enumerable.Range(0, 30)
            .Select(i => Entry($"dsh-{i:00}.log", daysAgo: i / 10, length: 1024 * 1024))
            .ToList();

        var expired = LogHousekeeping.SelectExpired(files, DateTime.Now);

        var kept = files.Except(expired).ToList();
        Assert.True(kept.Sum(f => f.Length) <= LogHousekeeping.MaxLogBytes);
        Assert.True(kept.Count >= 3);
        // 最新的 3 份一定还在
        var newestThree = files.OrderByDescending(f => f.LastWrite).Take(3).ToList();
        Assert.All(newestThree, n => Assert.Contains(n, kept));
    }

    [Fact]
    public void SmallRecentSet_DeletesNothing()
    {
        var files = Enumerable.Range(0, 5)
            .Select(i => Entry($"dsh-{i}.log", daysAgo: i))
            .ToList();

        Assert.Empty(LogHousekeeping.SelectExpired(files, DateTime.Now));
    }
}

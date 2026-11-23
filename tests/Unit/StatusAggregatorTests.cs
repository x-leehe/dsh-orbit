using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// StatusAggregator：输入（探测 / 进程 / 余额 / 插件）→ StatusSnapshot 的纯映射。
/// 重点是状态机四态，以及 DisplayUrl 只由真实端口生成。
/// </summary>
public class StatusAggregatorTests
{
    private static StatusInputs Inputs(
        bool online = false,
        bool hostRunning = false,
        bool starting = false,
        int? port = null) => new(
        Online: online,
        HostRunning: hostRunning,
        Starting: starting,
        Profile: "web",
        Version: "0.2.2",
        Port: port,
        HasAuthenticatedUrl: false,
        Uptime: TimeSpan.FromMinutes(5),
        MemoryBytes: 123_456,
        CpuPercent: 1.5,
        PluginTotal: 9,
        PluginDisabled: 2,
        PluginNames: "a、b",
        PluginScanning: false,
        Balance: 26.36m,
        SpentToday: 8.61m,
        OffPeak: true,
        UntilPriceSwitch: TimeSpan.FromMinutes(12),
        ActiveSessions: 3,
        Hint: null);

    [Fact]
    public void Online_MapsToRunning()
    {
        var snapshot = StatusAggregator.Build(Inputs(online: true, hostRunning: true, starting: true, port: 3080));

        Assert.Equal(DshState.Running, snapshot.State);
    }

    [Fact]
    public void HostRunningAndStarting_MapsToStarting()
    {
        var snapshot = StatusAggregator.Build(Inputs(hostRunning: true, starting: true));

        Assert.Equal(DshState.Starting, snapshot.State);
    }

    [Fact]
    public void HostRunningButStalled_MapsToError()
    {
        var snapshot = StatusAggregator.Build(Inputs(hostRunning: true, starting: false));

        Assert.Equal(DshState.Error, snapshot.State);
    }

    [Fact]
    public void NoHostNoOnline_MapsToStopped()
    {
        var snapshot = StatusAggregator.Build(Inputs());

        Assert.Equal(DshState.Stopped, snapshot.State);
    }

    [Fact]
    public void DisplayUrl_OnlyWhenPortKnown()
    {
        Assert.Equal("http://127.0.0.1:3080/", StatusAggregator.Build(Inputs(online: true, port: 3080)).DisplayUrl);
        Assert.Equal("", StatusAggregator.Build(Inputs()).DisplayUrl);
    }

    [Fact]
    public void PassesEveryValueThrough()
    {
        var snapshot = StatusAggregator.Build(Inputs(online: true, port: 3080));

        Assert.Equal("web", snapshot.Profile);
        Assert.Equal("0.2.2", snapshot.Version);
        Assert.Equal(3080, snapshot.Port);
        Assert.Equal(TimeSpan.FromMinutes(5), snapshot.Uptime);
        Assert.Equal(123_456, snapshot.MemoryBytes);
        Assert.Equal(1.5, snapshot.CpuPercent);
        Assert.Equal(9, snapshot.PluginTotal);
        Assert.Equal(2, snapshot.PluginDisabled);
        Assert.Equal("a、b", snapshot.PluginNames);
        Assert.False(snapshot.PluginScanning);
        Assert.Equal(26.36m, snapshot.Balance);
        Assert.Equal(8.61m, snapshot.SpentToday);
        Assert.True(snapshot.OffPeak);
        Assert.Equal(TimeSpan.FromMinutes(12), snapshot.UntilPriceSwitch);
        Assert.Equal(3, snapshot.ActiveSessions);
        Assert.Null(snapshot.Hint);
    }

    [Fact]
    public void HintIsCarriedThrough()
    {
        var snapshot = StatusAggregator.Build(Inputs() with { Hint = "启动超时，查看日志" });

        Assert.Equal("启动超时，查看日志", snapshot.Hint);
    }
}

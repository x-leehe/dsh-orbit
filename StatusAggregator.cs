namespace DshOrbit;

/// <summary>拼一次状态快照所需的全部输入（都是 WidgetApp 已经算好的值）。</summary>
internal readonly record struct StatusInputs(
    bool Online,
    bool HostRunning,
    bool Starting,
    string Profile,
    string Version,
    int? Port,
    bool HasAuthenticatedUrl,
    TimeSpan? Uptime,
    long MemoryBytes,
    double? CpuPercent,
    int? PluginTotal,
    int? PluginDisabled,
    string PluginNames,
    bool PluginScanning,
    decimal? Balance,
    decimal? SpentToday,
    bool OffPeak,
    TimeSpan? UntilPriceSwitch,
    int? ActiveSessions,
    string? Hint);

/// <summary>
/// 状态拼装（纯函数）：把「进程是否在跑 / 探测结果 / 余额 / 插件」映射成
/// <see cref="StatusSnapshot"/>。不读系统时间、不碰 UI，方便单测。
/// </summary>
internal static class StatusAggregator
{
    public static StatusSnapshot Build(StatusInputs input)
    {
        var state = input.Online
            ? DshState.Running
            : input.HostRunning
                ? (input.Starting ? DshState.Starting : DshState.Error)
                : DshState.Stopped;

        return new StatusSnapshot
        {
            State = state,
            Profile = input.Profile,
            Version = input.Version,
            Port = input.Port,
            DisplayUrl = input.Port is { } port ? $"http://127.0.0.1:{port}/" : "",
            HasAuthenticatedUrl = input.HasAuthenticatedUrl,
            Uptime = input.Uptime,
            MemoryBytes = input.MemoryBytes,
            CpuPercent = input.CpuPercent,
            PluginTotal = input.PluginTotal,
            PluginDisabled = input.PluginDisabled,
            PluginNames = input.PluginNames,
            PluginScanning = input.PluginScanning,
            Balance = input.Balance,
            SpentToday = input.SpentToday,
            OffPeak = input.OffPeak,
            UntilPriceSwitch = input.UntilPriceSwitch,
            ActiveSessions = input.ActiveSessions,
            Hint = input.Hint,
        };
    }
}

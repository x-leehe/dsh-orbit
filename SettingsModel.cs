namespace DshOrbit;

/// <summary>
/// 设置面板的编辑态：从 <see cref="WidgetConfig"/> 取一份可编辑副本，校验后写回。
///
/// 只覆盖「用户可以在面板里改」的字段：X/Y 与 DockSide 由界面行为自动维护，
/// AutoStartMode 存在注册表里（由 <see cref="AutoStart"/> 管），都不进这里。
/// </summary>
internal sealed class SettingsModel
{
    public const double MinOpacity = 0.3;
    public const double MaxOpacity = 1.0;
    public const int MinIntervalSeconds = 1;
    public const int MaxIntervalSeconds = 24 * 60 * 60;

    public AppTheme Theme { get; set; } = AppTheme.System;
    public double Opacity { get; set; } = 1.0;
    public bool AlwaysBottom { get; set; } = true;
    public bool AlwaysOnTop { get; set; }
    public bool EdgeHideEnabled { get; set; } = true;
    public bool ClickThrough { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public bool Visible { get; set; } = true;
    public ShellCloseMode ShellClose { get; set; } = ShellCloseMode.Hide;
    public bool UseWebShell { get; set; } = true;
    public bool ShowBalance { get; set; } = true;
    public string Profile { get; set; } = "web";
    public int PreferredPort { get; set; } = 3080;
    public string DshExePath { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string LaunchArguments { get; set; } = "web --no-open";
    public int StatusIntervalSeconds { get; set; } = 3;
    public int BalanceIntervalSeconds { get; set; } = 60;
    public int PluginIntervalSeconds { get; set; } = 300;
    public string DeepSeekApiKey { get; set; } = "";

    /// <summary>面板默认值（与 <see cref="WidgetConfig"/> 的初始值一致）。</summary>
    public static SettingsModel Defaults() => new();

    /// <summary>浅拷贝（字段都是值类型 / 不可变字符串）。用于记录「已应用」的基线。</summary>
    public SettingsModel Clone() => (SettingsModel)MemberwiseClone();

    /// <summary>
    /// 是否与另一份编辑态「实质相同」。文本按 Trim 比较（路径末尾空格不算改动），
    /// 不透明度按 0.0001 容差；用于「应用」按钮置灰。
    /// </summary>
    public bool SameAs(SettingsModel other) =>
        Theme == other.Theme
        && Math.Abs(Opacity - other.Opacity) < 0.0001
        && AlwaysBottom == other.AlwaysBottom
        && AlwaysOnTop == other.AlwaysOnTop
        && EdgeHideEnabled == other.EdgeHideEnabled
        && ClickThrough == other.ClickThrough
        && HideInFullscreen == other.HideInFullscreen
        && Visible == other.Visible
        && ShellClose == other.ShellClose
        && UseWebShell == other.UseWebShell
        && ShowBalance == other.ShowBalance
        && PreferredPort == other.PreferredPort
        && StatusIntervalSeconds == other.StatusIntervalSeconds
        && BalanceIntervalSeconds == other.BalanceIntervalSeconds
        && PluginIntervalSeconds == other.PluginIntervalSeconds
        && SameText(Profile, other.Profile)
        && SameText(DshExePath, other.DshExePath)
        && SameText(WorkingDirectory, other.WorkingDirectory)
        && SameText(LaunchArguments, other.LaunchArguments)
        && SameText(DeepSeekApiKey, other.DeepSeekApiKey);

    private static bool SameText(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    public static SettingsModel From(WidgetConfig config) => new()
    {
        Theme = config.Theme,
        Opacity = config.Opacity,
        AlwaysBottom = config.AlwaysBottom,
        AlwaysOnTop = config.AlwaysOnTop,
        EdgeHideEnabled = config.EdgeHideEnabled,
        ClickThrough = config.ClickThrough,
        HideInFullscreen = config.HideInFullscreen,
        Visible = config.Visible,
        ShellClose = config.ShellClose,
        UseWebShell = config.UseWebShell,
        ShowBalance = config.ShowBalance,
        Profile = config.Profile,
        PreferredPort = config.PreferredPort,
        DshExePath = config.DshExePath ?? "",
        WorkingDirectory = config.WorkingDirectory ?? "",
        LaunchArguments = config.LaunchArguments,
        StatusIntervalSeconds = config.StatusIntervalSeconds,
        BalanceIntervalSeconds = config.BalanceIntervalSeconds,
        PluginIntervalSeconds = config.PluginIntervalSeconds,
        DeepSeekApiKey = config.DeepSeekApiKey ?? "",
    };

    /// <summary>
    /// 写回配置。会顺手收掉非法组合（穿透优先于贴边、层级二选一），
    /// 其余字段原样写入；存盘由调用方通过 <see cref="ConfigStore.Save"/> 负责。
    /// </summary>
    public void ApplyTo(WidgetConfig config)
    {
        var top = AlwaysOnTop && !AlwaysBottom;

        config.Theme = Theme;
        config.Opacity = Math.Clamp(Opacity, MinOpacity, MaxOpacity);
        config.AlwaysBottom = AlwaysBottom;
        config.AlwaysOnTop = top;
        config.ClickThrough = ClickThrough;
        config.EdgeHideEnabled = ClickThrough ? false : EdgeHideEnabled;
        if (ClickThrough)
        {
            config.DockSide = DockSide.None;
        }

        config.HideInFullscreen = HideInFullscreen;
        config.Visible = Visible;
        config.ShellClose = ShellClose;
        config.UseWebShell = UseWebShell;
        config.ShowBalance = ShowBalance;
        config.Profile = NonEmpty(Profile, "web");
        config.PreferredPort = PreferredPort;
        config.DshExePath = Optional(DshExePath);
        config.WorkingDirectory = Optional(WorkingDirectory);
        config.LaunchArguments = NonEmpty(LaunchArguments, "web --no-open");
        config.StatusIntervalSeconds = StatusIntervalSeconds;
        config.BalanceIntervalSeconds = BalanceIntervalSeconds;
        config.PluginIntervalSeconds = PluginIntervalSeconds;
        config.DeepSeekApiKey = Optional(DeepSeekApiKey);
    }

    /// <summary>与现有配置相比是否有改动（面板拿来决定"保存"按钮是否可用）。</summary>
    public bool HasChanges(WidgetConfig config)
    {
        var current = From(config);
        return Theme != current.Theme
            || Math.Abs(Opacity - current.Opacity) > 0.0001
            || AlwaysBottom != current.AlwaysBottom
            || AlwaysOnTop != current.AlwaysOnTop
            || EdgeHideEnabled != current.EdgeHideEnabled
            || ClickThrough != current.ClickThrough
            || HideInFullscreen != current.HideInFullscreen
            || Visible != current.Visible
            || ShellClose != current.ShellClose
            || UseWebShell != current.UseWebShell
            || ShowBalance != current.ShowBalance
            || Clean(Profile) != Clean(current.Profile)
            || PreferredPort != current.PreferredPort
            || Clean(DshExePath) != Clean(current.DshExePath)
            || Clean(WorkingDirectory) != Clean(current.WorkingDirectory)
            || Clean(LaunchArguments) != Clean(current.LaunchArguments)
            || StatusIntervalSeconds != current.StatusIntervalSeconds
            || BalanceIntervalSeconds != current.BalanceIntervalSeconds
            || PluginIntervalSeconds != current.PluginIntervalSeconds
            || Clean(DeepSeekApiKey) != Clean(current.DeepSeekApiKey);
    }

    /// <summary>保存前校验。返回 false 时 <paramref name="error"/> 是可以直接显示给用户的中文提示。</summary>
    public bool TryValidate(out string error)
    {
        if (Opacity < MinOpacity || Opacity > MaxOpacity)
        {
            error = $"不透明度需在 {MinOpacity:0.0}–{MaxOpacity:0.0} 之间";
            return false;
        }

        if (PreferredPort is < 1 or > 65535)
        {
            error = "首选端口需在 1–65535 之间";
            return false;
        }

        if (!InRange(StatusIntervalSeconds) ||
            !InRange(BalanceIntervalSeconds) ||
            !InRange(PluginIntervalSeconds))
        {
            error = $"刷新间隔需在 {MinIntervalSeconds}–{MaxIntervalSeconds} 秒之间";
            return false;
        }

        if (Clean(Profile).Length == 0)
        {
            error = "Profile 不能为空";
            return false;
        }

        if (Clean(LaunchArguments).Length == 0)
        {
            error = "启动参数不能为空（默认 web --no-open）";
            return false;
        }

        var exe = Clean(DshExePath);
        if (LooksLikePath(exe) && !File.Exists(exe))
        {
            error = $"dsh 路径不存在：{exe}";
            return false;
        }

        var dir = Clean(WorkingDirectory);
        if (dir.Length > 0 && !Directory.Exists(dir))
        {
            error = $"工作目录不存在：{dir}";
            return false;
        }

        error = "";
        return true;
    }

    private static bool InRange(int seconds) => seconds is >= MinIntervalSeconds and <= MaxIntervalSeconds;

    /// <summary>只对「看起来是路径」的输入查存在性：允许直接写 `dsh` 让程序去 PATH 里找。</summary>
    private static bool LooksLikePath(string text) =>
        text.Length > 0 && (text.Contains('\\') || text.Contains('/') || text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static string Clean(string? text) => (text ?? "").Trim();

    private static string NonEmpty(string? text, string fallback) => Clean(text) is { Length: > 0 } value ? value : fallback;

    private static string? Optional(string? text) => Clean(text) is { Length: > 0 } value ? value : null;
}

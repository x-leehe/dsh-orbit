namespace DshOrbit;

/// <summary>
/// 托盘菜单与设置面板共用的命令 ID：视图只发命令，真正的动作统一在 <see cref="WidgetApp"/> 里执行。
/// 取值保持既有字符串不变（调试脚本 / 历史配置里可能已经用过），新增命令只追加。
/// </summary>
internal static class MenuCommand
{
    public const string Open = "open";
    public const string Start = "start";
    public const string Stop = "stop";
    public const string Restart = "restart";

    public const string LayoutBottom = "layout:bottom";
    public const string LayoutTop = "layout:top";
    public const string LayoutNormal = "layout:normal";

    public const string AutoStartDisabled = "autostart:Disabled";
    public const string AutoStartEnabled = "autostart:Enabled";
    public const string AutoStartFull = "autostart:FullAuto";

    public const string ThemeSystem = "theme:System";
    public const string ThemeLight = "theme:Light";
    public const string ThemeDark = "theme:Dark";

    public const string Lock = "lock";
    public const string Edge = "edge";
    public const string Fullscreen = "fullscreen";
    public const string Shell = "shell";
    public const string Eye = "eye";
    public const string Copy = "copy";
    public const string Logs = "logs";
    public const string Settings = "settings";
    public const string Exit = "exit";

    /// <summary>全部命令，供单测查重 / 检查覆盖用。</summary>
    public static readonly string[] All =
    [
        Open, Start, Stop, Restart,
        LayoutBottom, LayoutTop, LayoutNormal,
        AutoStartDisabled, AutoStartEnabled, AutoStartFull,
        ThemeSystem, ThemeLight, ThemeDark,
        Lock, Edge, Fullscreen, Shell, Eye, Copy, Logs, Settings, Exit,
    ];
}

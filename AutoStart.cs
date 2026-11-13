using Microsoft.Win32;

namespace DshOrbit;

/// <summary>
/// 开机自启：写 HKCU 的 Run 键。
/// 三种状态——停用（无值）、启用（只起挂件）、全自动（挂件 + 自动拉起 dsh web）。
/// </summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DSH-Orbit";
    private const string LegacyValueName = "DshStatusWidget";
    private const string FullAutoArgument = "--autostart-full";

    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DSH-Orbit.exe");

    public static AutoStartMode GetMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(ValueName) is not string value || value.Length == 0)
            {
                return AutoStartMode.Disabled;
            }

            return value.Contains(FullAutoArgument, StringComparison.OrdinalIgnoreCase)
                ? AutoStartMode.FullAuto
                : AutoStartMode.Enabled;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"读取开机自启失败：{ex.Message}");
            return AutoStartMode.Disabled;
        }
    }

    /// <summary>设置开机自启，返回最终生效的状态。</summary>
    public static AutoStartMode SetMode(AutoStartMode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
            {
                return GetMode();
            }

            switch (mode)
            {
                case AutoStartMode.Disabled:
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    break;
                case AutoStartMode.Enabled:
                    key.SetValue(ValueName, $"\"{ExecutablePath}\"");
                    break;
                case AutoStartMode.FullAuto:
                    key.SetValue(ValueName, $"\"{ExecutablePath}\" {FullAutoArgument}");
                    break;
            }

            return GetMode();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"写入开机自启失败：{ex.Message}");
            return GetMode();
        }
    }

    /// <summary>如果注册表里的路径指向旧位置，按当前 exe 重写一次。</summary>
    public static void MigrateIfNeeded()
    {
        // 旧版本的 Run 键名 DshStatusWidget → DSH-Orbit
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null &&
                key.GetValue(ValueName) is null &&
                key.GetValue(LegacyValueName) is string legacyValue)
            {
                key.SetValue(ValueName, legacyValue);
                key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
                AppPaths.Log("开机自启键已迁移到 DSH-Orbit");
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"迁移开机自启键失败：{ex.Message}");
        }

        var mode = GetMode();
        if (mode == AutoStartMode.Disabled)
        {
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key?.GetValue(ValueName) is not string value)
            {
                return;
            }

            if (!value.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                SetMode(mode);
                AppPaths.Log($"开机自启路径已迁移到 {ExecutablePath}");
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"检查开机自启路径失败：{ex.Message}");
        }
    }

    /// <summary>停用 → 启用 → 全自动 → 停用。</summary>
    public static AutoStartMode Next(AutoStartMode mode) => mode switch
    {
        AutoStartMode.Disabled => AutoStartMode.Enabled,
        AutoStartMode.Enabled => AutoStartMode.FullAuto,
        _ => AutoStartMode.Disabled,
    };

    public static string Label(AutoStartMode mode) => mode switch
    {
        AutoStartMode.Enabled => "启用",
        AutoStartMode.FullAuto => "全自动",
        _ => "停用",
    };
}

namespace DshOrbit;

/// <summary>
/// 设置面板「保存预设」存下来的那份快照。只有一个槽位，覆盖式；
/// 存的只是编辑态（<see cref="SettingsModel"/>），开机启动这类操作系统状态不存。
/// </summary>
internal sealed class SettingsPreset
{
    public DateTimeOffset SavedAt { get; set; }

    public SettingsModel Settings { get; set; } = new();
}

/// <summary>预设的读写：默认放数据目录的 `preset.json`，路径可注入便于单测。</summary>
internal static class SettingsPresetStore
{
    public static string DefaultPath => Path.Combine(AppPaths.Root, "preset.json");

    public static SettingsPreset? Load(string? path = null) =>
        JsonIo.Load<SettingsPreset?>(path ?? DefaultPath, () => null);

    public static bool Exists(string? path = null) => File.Exists(path ?? DefaultPath);

    public static void Save(SettingsModel settings, string? path = null) =>
        JsonIo.Save(path ?? DefaultPath, new SettingsPreset
        {
            SavedAt = DateTimeOffset.Now,
            Settings = settings,
        });
}

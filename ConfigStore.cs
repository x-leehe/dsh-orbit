namespace DshOrbit;

/// <summary>
/// config.json 的唯一入口：读取 + 归一化 + 原子写。
///
/// 归一化把「互斥 / 非法组合」收成唯一状态，避免各处重复判断：
/// 点击穿透优先于贴边隐藏（穿透收不到 hover，贴边会变成点不到的死块）；
/// 层级三态里「置底 + 置顶」同时为真时以置底为准。
/// </summary>
internal sealed class ConfigStore
{
    /// <summary>当前配置结构版本；以后加迁移时递增。</summary>
    public const int CurrentSchema = 1;

    private readonly string _path;

    public ConfigStore(WidgetConfig config, string? path = null)
    {
        Config = config;
        _path = path ?? AppPaths.ConfigFile;
    }

    public WidgetConfig Config { get; }

    /// <summary>读盘（失败回退默认值），并把旧配置升级到 <see cref="CurrentSchema"/>。</summary>
    public static ConfigStore Load(string? path = null)
    {
        var file = path ?? AppPaths.ConfigFile;
        var config = JsonIo.Load(file, () => new WidgetConfig());
        var store = new ConfigStore(config, file);
        if (Normalize(config))
        {
            AppPaths.Log("配置归一化：已修正互斥 / 非法组合，稍后落盘");
        }

        return store;
    }

    public void Save() => JsonIo.Save(_path, Config);

    /// <summary>互斥 / 非法组合归一化。返回是否改动（调用方据此决定要不要写盘）。</summary>
    internal static bool Normalize(WidgetConfig config)
    {
        var changed = false;

        if (config.ClickThrough && (config.EdgeHideEnabled || config.DockSide != DockSide.None))
        {
            AppPaths.Log("配置归一化：点击穿透与贴边隐藏互斥，关闭贴边隐藏");
            config.EdgeHideEnabled = false;
            config.DockSide = DockSide.None;
            changed = true;
        }

        // 层级三态：两个都为真时置底优先（与 Program.ApplyOverrides 的口径一致）。
        if (config.AlwaysBottom && config.AlwaysOnTop)
        {
            config.AlwaysOnTop = false;
            changed = true;
        }

        if (config.SchemaVersion != CurrentSchema)
        {
            config.SchemaVersion = CurrentSchema;
            changed = true;
        }

        return changed;
    }
}

using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// ConfigStore：配置归一化（互斥 / 非法组合）、旧配置读取、原子写回。
/// 归一化规则：点击穿透优先于贴边隐藏；层级「置底 + 置顶」同时为真时以置底为准；结构版本升到当前版本。
/// </summary>
public class ConfigStoreTests
{
    [Fact]
    public void Normalize_CleanConfig_ReturnsFalse()
    {
        var config = new WidgetConfig { AlwaysBottom = true, AlwaysOnTop = false, ClickThrough = false };

        Assert.False(ConfigStore.Normalize(config));
        Assert.Equal(ConfigStore.CurrentSchema, config.SchemaVersion);
    }

    [Fact]
    public void Normalize_ClickThroughWinsOverEdgeHide()
    {
        var config = new WidgetConfig
        {
            ClickThrough = true,
            EdgeHideEnabled = true,
            DockSide = DockSide.Right,
        };

        Assert.True(ConfigStore.Normalize(config));
        Assert.False(config.EdgeHideEnabled);
        Assert.Equal(DockSide.None, config.DockSide);
    }

    [Fact]
    public void Normalize_BottomWinsOverTop()
    {
        var config = new WidgetConfig { AlwaysBottom = true, AlwaysOnTop = true };

        Assert.True(ConfigStore.Normalize(config));
        Assert.True(config.AlwaysBottom);
        Assert.False(config.AlwaysOnTop);
    }

    [Fact]
    public void Normalize_UpgradesSchemaVersion()
    {
        var config = new WidgetConfig { SchemaVersion = 0 };

        Assert.True(ConfigStore.Normalize(config));
        Assert.Equal(ConfigStore.CurrentSchema, config.SchemaVersion);
    }

    [Fact]
    public void Load_MissingFile_UsesDefaults()
    {
        var path = TempFile();

        try
        {
            var store = ConfigStore.Load(path);

            Assert.True(store.Config.AlwaysBottom);
            Assert.True(store.Config.HideInFullscreen);
            Assert.Equal(3080, store.Config.PreferredPort);
            Assert.Equal(ConfigStore.CurrentSchema, store.Config.SchemaVersion);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_LegacyJson_KeepsKnownFieldsAndDefaultsTheRest()
    {
        var path = TempFile();
        File.WriteAllText(path, """{ "X": 10, "Y": 20, "HideInFullscreen": false, "UnknownField": 7 }""");

        try
        {
            var config = ConfigStore.Load(path).Config;

            Assert.Equal(10, config.X);
            Assert.Equal(20, config.Y);
            Assert.False(config.HideInFullscreen);
            Assert.Equal(1.0, config.Opacity);           // 旧配置没有这一项 → 默认值
            Assert.Equal(3080, config.PreferredPort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BrokenJson_FallsBackToDefaults()
    {
        var path = TempFile();
        File.WriteAllText(path, "{ this is not json");

        try
        {
            var config = ConfigStore.Load(path).Config;

            Assert.True(config.AlwaysBottom);
            Assert.Equal(3080, config.PreferredPort);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var path = TempFile();
        var config = new WidgetConfig
        {
            X = 111,
            Y = 222,
            Theme = AppTheme.Dark,
            Opacity = 0.85,
            ClickThrough = true,
            HideInFullscreen = false,
            DockSide = DockSide.None,
            EdgeHideEnabled = false,
        };

        try
        {
            new ConfigStore(config, path).Save();
            var loaded = ConfigStore.Load(path).Config;

            Assert.Equal(111, loaded.X);
            Assert.Equal(222, loaded.Y);
            Assert.Equal(AppTheme.Dark, loaded.Theme);
            Assert.Equal(0.85, loaded.Opacity, 3);
            Assert.True(loaded.ClickThrough);
            Assert.False(loaded.HideInFullscreen);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"dsh-config-{Guid.NewGuid():N}.json");
}

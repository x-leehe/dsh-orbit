using Xunit;

namespace DshOrbit.Tests;

/// <summary>
/// SettingsPresetStore：预设的存取（单槽位、覆盖式）。
/// 只验证 round-trip 与缺失文件的行为；路径可注入，不碰真实数据目录。
/// </summary>
public class SettingsPresetStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dsh-preset-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(SettingsPresetStore.Load(_path));
        Assert.False(SettingsPresetStore.Exists(_path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsSettings()
    {
        var settings = SettingsModel.Defaults();
        settings.Theme = AppTheme.Dark;
        settings.Opacity = 0.85;
        settings.AlwaysBottom = false;
        settings.AlwaysOnTop = true;
        settings.Profile = "preset-profile";
        settings.PreferredPort = 3999;
        settings.StatusIntervalSeconds = 7;
        settings.ShowBalance = false;

        SettingsPresetStore.Save(settings, _path);

        Assert.True(SettingsPresetStore.Exists(_path));
        var preset = SettingsPresetStore.Load(_path);
        Assert.NotNull(preset);
        Assert.NotEqual(default, preset.SavedAt);
        Assert.Equal(AppTheme.Dark, preset.Settings.Theme);
        Assert.Equal(0.85, preset.Settings.Opacity);
        Assert.False(preset.Settings.AlwaysBottom);
        Assert.True(preset.Settings.AlwaysOnTop);
        Assert.Equal("preset-profile", preset.Settings.Profile);
        Assert.Equal(3999, preset.Settings.PreferredPort);
        Assert.Equal(7, preset.Settings.StatusIntervalSeconds);
        Assert.False(preset.Settings.ShowBalance);
    }

    [Fact]
    public void Save_OverwritesPreviousPreset()
    {
        var first = SettingsModel.Defaults();
        first.Profile = "first";
        SettingsPresetStore.Save(first, _path);

        var second = SettingsModel.Defaults();
        second.Profile = "second";
        SettingsPresetStore.Save(second, _path);

        Assert.Equal("second", SettingsPresetStore.Load(_path)?.Settings.Profile);
    }
}

using HyperLight.ColorProcessing;
using HyperLight.Config;
using Xunit;

namespace HyperLight.Tests;

/// <summary>设置文件：默认值、读写、范围检查、格式错误、原子保存。</summary>
public sealed class ConfigTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "HyperLightTests", Guid.NewGuid().ToString("N"));
    private readonly string path;

    public ConfigTests()
    {
        Directory.CreateDirectory(folder);
        path = Path.Combine(folder, "hyperlight.json");
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    [Fact]
    public void MissingFileCreatesDefaults()
    {
        AppConfig config = AppConfig.Load(path, out List<string> notes);
        Assert.Single(notes);
        Assert.True(File.Exists(path));
        Assert.Equal(ColorMode.Wallpaper, config.ColorMode);
        Assert.Equal(64, config.CaseLedCount);
        Assert.Equal(64, config.RadiatorLedCount);
        Assert.True(config.GpuEnabled);
        Assert.True(config.SleepLightsOff);

        string text = File.ReadAllText(path);
        Assert.Contains("\"caseColorSource\": \"lower\"", text);
        Assert.Contains("\"freeMicColor\": \"FF69B4\"", text);
        Assert.Contains("//", text);
    }

    [Fact]
    public void SaveThenLoadRoundTrips()
    {
        AppConfig changed = new AppConfig() with
        {
            ColorMode = ColorMode.Free,
            CaseEffect = LightEffect.FlowUpDown,
            OnboardColorSource = ColorSource.Right,
            FreeGpuColor = new Rgb(1, 2, 3),
            GpuColorBoost = 77,
        };
        Assert.True(AppConfig.TrySave(changed, path, out _));
        Assert.False(File.Exists(path + ".tmp"));

        AppConfig loaded = AppConfig.Load(path, out List<string> notes);
        Assert.Empty(notes);
        Assert.Equal(changed, loaded);
    }

    [Fact]
    public void OutOfRangeValueFallsBackToDefault()
    {
        File.WriteAllText(path, "{ \"caseLedCount\": 300, \"twoColorSpread\": -1 }");
        AppConfig config = AppConfig.Load(path, out List<string> notes);
        Assert.Equal(2, notes.Count);
        Assert.Equal(64, config.CaseLedCount);
        Assert.Equal(40, config.TwoColorSpread);
    }

    [Fact]
    public void OldFileWithoutNewKeysUsesDefaults()
    {
        File.WriteAllText(path, "{ \"zoneLayout\": \"whole\" }");
        AppConfig config = AppConfig.Load(path, out List<string> notes);
        Assert.Empty(notes);
        Assert.Equal(ZoneLayout.Whole, config.ZoneLayout);
        Assert.True(config.CaseEnabled);
    }

    [Theory]
    [InlineData("{ \"caseColorSource\": \"middle\" }")]
    [InlineData("{ \"freeMicColor\": \"pink\" }")]
    [InlineData("{ \"freeMicColor\": 123 }")]
    [InlineData("{ not json")]
    public void BrokenFileFallsBackToDefaultsWithNote(string content)
    {
        File.WriteAllText(path, content);
        AppConfig config = AppConfig.Load(path, out List<string> notes);
        Assert.Single(notes);
        Assert.Equal(new AppConfig(), config);
    }

    [Fact]
    public void ColorAcceptsHashAndLowercase()
    {
        File.WriteAllText(path, "{ \"freeMicColor\": \"#00ff00\" }");
        AppConfig config = AppConfig.Load(path, out List<string> notes);
        Assert.Empty(notes);
        Assert.Equal(new Rgb(0, 255, 0), config.FreeMicColor);
    }
}

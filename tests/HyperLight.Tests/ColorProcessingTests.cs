using HyperLight.ColorProcessing;
using Xunit;

namespace HyperLight.Tests;

/// <summary>取色（五块区域）、增强颜色、加大差异、LED 颜色加强、灯效图案。</summary>
public class ColorProcessingTests
{
    /// <summary>做一张四块颜色的图：左上红、右上绿、左下蓝、右下白（BGRA 格式）。</summary>
    private static byte[] FourQuarterImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool top = y < height / 2;
                bool left = x < width / 2;
                (byte r, byte g, byte b) = (top, left) switch
                {
                    (true, true) => ((byte)255, (byte)0, (byte)0),
                    (true, false) => ((byte)0, (byte)255, (byte)0),
                    (false, true) => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)255, (byte)255, (byte)255),
                };
                int i = ((y * width) + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        }

        return pixels;
    }

    [Fact]
    public void AveragerComputesFiveRegions()
    {
        WallpaperColors colors = ColorAverager.AverageRegions(FourQuarterImage(64, 48), 64, 48);
        Assert.Equal(new Rgb(128, 128, 0), colors.Upper);
        Assert.Equal(new Rgb(128, 128, 255), colors.Lower);
        Assert.Equal(new Rgb(128, 0, 128), colors.Left);
        Assert.Equal(new Rgb(128, 255, 128), colors.Right);
        Assert.Equal(new Rgb(128, 128, 128), colors.Whole);
        Assert.Equal(colors.Whole, colors.Get(ColorSource.Average));
    }

    [Fact]
    public void AveragerHandlesTinyImages()
    {
        WallpaperColors single = ColorAverager.AverageRegions(new byte[] { 1, 2, 3, 0 }, 1, 1);
        Assert.Equal(new Rgb(3, 2, 1), single.Upper);
        Assert.Equal(new Rgb(3, 2, 1), single.Right);
    }

    [Fact]
    public void AveragerRejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => ColorAverager.AverageRegions(new byte[3], 1, 1));
    }

    [Fact]
    public void LayoutMapsToRegions()
    {
        Assert.Equal((ColorSource.Upper, ColorSource.Lower), ZoneLayoutRegions.Of(ZoneLayout.TopBottom));
        Assert.Equal((ColorSource.Left, ColorSource.Right), ZoneLayoutRegions.Of(ZoneLayout.LeftRight));
        Assert.Equal((ColorSource.Average, ColorSource.Average), ZoneLayoutRegions.Of(ZoneLayout.Whole));
    }

    [Fact]
    public void BoostSaturationKeepsGrayAndBrightness()
    {
        Assert.Equal(new Rgb(128, 128, 128), ColorEnhancer.BoostSaturation(new Rgb(128, 128, 128), 1.5));
        Rgb boosted = ColorEnhancer.BoostSaturation(new Rgb(200, 150, 150), 1.5);
        Assert.Equal(200, boosted.R);
        Assert.True(boosted.G < 150);
    }

    [Fact]
    public void SeparateLeavesDifferentColorsAlone()
    {
        Rgb red = new(255, 0, 0);
        Rgb blue = new(0, 0, 255);
        Assert.Equal((red, blue), ColorEnhancer.Separate(red, blue, 100));
        Assert.Equal((red, red), ColorEnhancer.Separate(red, red, 0));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(70)]
    [InlineData(100)]
    public void SeparatePullsSimilarColorsApart(int strength)
    {
        (Rgb a, Rgb b) = ColorEnhancer.Separate(new Rgb(240, 170, 190), new Rgb(235, 175, 195), strength);
        Assert.True(ColorChangeFilter.Difference(a, b) >= (int)Math.Round(strength * 1.2));
    }

    [Fact]
    public void SeparateTurnsGrayIntoLightAndDark()
    {
        Rgb gray = new(128, 128, 128);
        (Rgb a, Rgb b) = ColorEnhancer.Separate(gray, gray, 40);
        Assert.Equal(gray, a);
        Assert.True(b.R == b.G && b.G == b.B && b.R < 128);
    }

    [Fact]
    public void StrengthenForLedMakesPaleColorsDeeperAndKeepsBrightness()
    {
        Assert.Equal(new Rgb(0xF9, 0x4E, 0x52), ColorEnhancer.StrengthenForLed(new Rgb(0xF9, 0x93, 0x96), 60));
        Assert.Equal(new Rgb(128, 128, 128), ColorEnhancer.StrengthenForLed(new Rgb(128, 128, 128), 100));
        Assert.Equal(new Rgb(0, 0, 0), ColorEnhancer.StrengthenForLed(new Rgb(0, 0, 0), 100));
        Assert.Equal(new Rgb(0xF9, 0x93, 0x96), ColorEnhancer.StrengthenForLed(new Rgb(0xF9, 0x93, 0x96), 0));
    }

    [Fact]
    public void ColorAlgorithmsNeverThrowOnRandomInput()
    {
        var random = new Random(1);
        for (int i = 0; i < 3000; i++)
        {
            var a = new Rgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            var b = new Rgb((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            int strength = random.Next(101);
            ColorEnhancer.Separate(a, b, strength);
            ColorEnhancer.StrengthenForLed(a, strength);
        }
    }

    [Fact]
    public void FlowPatternRepeatsAndRotates()
    {
        Rgb a = new(255, 0, 0);
        Rgb b = new(0, 0, 255);
        Rgb[] start = LightEffectRules.BuildFlow(a, b, 16, 8, 0);
        Assert.Equal(a, start[0]);
        Assert.Equal(b, start[4]);
        Assert.Equal(a, start[8]);
        Assert.Equal(new Rgb(128, 0, 128), start[2]);

        Rgb[] half = LightEffectRules.BuildFlow(a, b, 8, 8, 0.5);
        Assert.Equal(b, half[0]);
        Assert.Equal(LightEffectRules.BuildFlow(a, b, 8, 8, 0.125)[0], LightEffectRules.BuildFlow(a, b, 8, 8, 1.125)[0]);
    }

    [Fact]
    public void SplitPatternIsHalfAndHalf()
    {
        Rgb a = new(255, 0, 0);
        Rgb b = new(0, 0, 255);
        Rgb[] leds = LightEffectRules.BuildSplit(a, b, 16, 8);
        Assert.Equal(new[] { a, a, a, a, b, b, b, b, a, a, a, a, b, b, b, b }, leds);
    }

    [Fact]
    public void EffectRegionsAndAnimation()
    {
        Assert.Null(LightEffectRules.TwoColorRegionsOf(LightEffect.Solid));
        Assert.Equal((ColorSource.Left, ColorSource.Right), LightEffectRules.TwoColorRegionsOf(LightEffect.SplitLeftRight));
        Assert.Equal((ColorSource.Upper, ColorSource.Lower), LightEffectRules.TwoColorRegionsOf(LightEffect.FlowUpDown));
        Assert.True(LightEffectRules.IsAnimated(LightEffect.FlowLeftRight));
        Assert.False(LightEffectRules.IsAnimated(LightEffect.SplitUpDown));
    }
}

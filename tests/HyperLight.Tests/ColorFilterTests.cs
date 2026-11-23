using HyperLight.ColorProcessing;
using Xunit;

namespace HyperLight.Tests;

/// <summary>颜色变化过滤：门槛、确认次数、渐变。</summary>
public class ColorFilterTests
{
    private static readonly Rgb dark = new(10, 10, 10);
    private static readonly Rgb red = new(200, 0, 0);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void FirstColorIsAdoptedImmediately()
    {
        var filter = new ColorChangeFilter(12, Ms(400), 2);
        Assert.True(filter.Offer(dark, Ms(0)));
        Assert.Equal(dark, filter.Current(Ms(0)));
    }

    [Fact]
    public void ZeroThresholdStillConfirmsSameColor()
    {
        // 门槛为 0 时，同一个新颜色连续出现两次要能被采用（曾经的 bug：0 < 0 不成立，永远确认不了）
        var filter = new ColorChangeFilter(0, TimeSpan.Zero, 2);
        filter.Offer(dark, Ms(0));
        Assert.False(filter.Offer(red, Ms(1)));
        Assert.True(filter.Offer(red, Ms(2)));
        Assert.Equal(red, filter.Target);
        Assert.False(filter.Offer(red, Ms(3)));
    }

    [Fact]
    public void ZeroThresholdCountsAnyDifference()
    {
        var filter = new ColorChangeFilter(0, TimeSpan.Zero, 1);
        filter.Offer(dark, Ms(0));
        Assert.False(filter.Offer(dark, Ms(1)));
        Assert.True(filter.Offer(new Rgb(11, 10, 10), Ms(2)));
    }

    [Fact]
    public void SmallChangeBelowThresholdIsIgnored()
    {
        var filter = new ColorChangeFilter(12, TimeSpan.Zero, 1);
        filter.Offer(dark, Ms(0));
        Assert.False(filter.Offer(new Rgb(21, 10, 10), Ms(1)));
        Assert.Equal(dark, filter.Target);
    }

    [Fact]
    public void MaximumThresholdNeverChanges()
    {
        var filter = new ColorChangeFilter(255, TimeSpan.Zero, 1);
        filter.Offer(dark, Ms(0));
        Assert.False(filter.Offer(new Rgb(255, 255, 255), Ms(1)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void NewColorNeedsExactlyConfirmCaptures(int confirm)
    {
        var filter = new ColorChangeFilter(12, TimeSpan.Zero, confirm);
        filter.Offer(dark, Ms(0));
        for (int i = 1; i < confirm; i++)
        {
            Assert.False(filter.Offer(red, Ms(i)));
        }

        Assert.True(filter.Offer(red, Ms(confirm)));
    }

    [Fact]
    public void FlashOfTransitionFrameIsIgnored()
    {
        // 换壁纸时一闪而过的过渡画面：出现一次就回到原来的颜色，不应该切换
        var filter = new ColorChangeFilter(12, TimeSpan.Zero, 2);
        filter.Offer(dark, Ms(0));
        Assert.False(filter.Offer(new Rgb(128, 128, 128), Ms(1)));
        Assert.False(filter.Offer(dark, Ms(2)));
        Assert.False(filter.Offer(red, Ms(3)));
        Assert.Equal(dark, filter.Target);
    }

    [Fact]
    public void FadeGoesFromOldToNewColor()
    {
        var filter = new ColorChangeFilter(12, Ms(400), 1);
        filter.Offer(new Rgb(0, 0, 0), Ms(0));
        filter.Offer(new Rgb(200, 100, 0), Ms(1000));
        Assert.Equal(new Rgb(0, 0, 0), filter.Current(Ms(1000)));
        Assert.Equal(new Rgb(100, 50, 0), filter.Current(Ms(1200)));
        Assert.Equal(new Rgb(200, 100, 0), filter.Current(Ms(1400)));
        Assert.Equal(new Rgb(200, 100, 0), filter.Current(Ms(5000)));
    }

    [Fact]
    public void ClearStopsSending()
    {
        var filter = new ColorChangeFilter(12, TimeSpan.Zero, 1);
        filter.Offer(dark, Ms(0));
        filter.Clear();
        Assert.Null(filter.Current(Ms(1)));
        Assert.Null(filter.Target);
    }
}

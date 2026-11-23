namespace HyperLight.ColorProcessing;

/// <summary>
/// 灯效：机箱灯、冷排灯的灯怎么亮。
/// 以后加新灯效，只改这个文件（名字、用哪两块颜色、怎么排）。
/// </summary>
internal enum LightEffect
{
    /// <summary>单色：整个灯区一个颜色（按取色来源）。</summary>
    Solid,

    /// <summary>左右流动：左半区和右半区两个颜色连成渐变，在每个风扇上转圈。</summary>
    FlowLeftRight,

    /// <summary>上下流动：上半区和下半区两个颜色连成渐变，在每个风扇上转圈。</summary>
    FlowUpDown,
}

/// <summary>灯效的中文名字、用到的颜色，以及流动图案的算法。</summary>
internal static class LightEffectRules
{
    /// <summary>所有灯效，按菜单里的顺序。</summary>
    public static readonly LightEffect[] All = { LightEffect.Solid, LightEffect.FlowLeftRight, LightEffect.FlowUpDown };

    /// <summary>中文名字。</summary>
    public static string TextOf(LightEffect effect) => effect switch
    {
        LightEffect.FlowLeftRight => "左右流动",
        LightEffect.FlowUpDown => "上下流动",
        _ => "单色",
    };

    /// <summary>流动灯效用哪两块区域的颜色。单色返回 null（单色用取色来源）。</summary>
    public static (ColorSource First, ColorSource Second)? FlowRegionsOf(LightEffect effect) => effect switch
    {
        LightEffect.FlowLeftRight => (ColorSource.Left, ColorSource.Right),
        LightEffect.FlowUpDown => (ColorSource.Upper, ColorSource.Lower),
        _ => null,
    };

    /// <summary>
    /// 生成流动图案：两个颜色连成"第一个 → 第二个 → 第一个"的渐变，每 <paramref name="periodLeds"/> 颗灯重复一次，
    /// 随着 <paramref name="cyclePosition"/> 往前转。
    /// 集线板会把同一份数据复制给每个风扇，所以每个风扇都从第 1 颗灯开始显示同样的图案；
    /// 周期取风扇的灯珠数（8 颗），每个风扇正好转一整圈。
    /// </summary>
    /// <param name="first">第一个颜色。</param>
    /// <param name="second">第二个颜色。</param>
    /// <param name="ledCount">一共要生成多少颗灯的颜色。</param>
    /// <param name="periodLeds">图案每几颗灯重复一次（至少 2）。</param>
    /// <param name="cyclePosition">转到哪里了：0～1 是一整圈，超过 1 会自动接着转。</param>
    public static Rgb[] BuildFlow(Rgb first, Rgb second, int ledCount, int periodLeds, double cyclePosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ledCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(periodLeds, 2);

        double shift = cyclePosition - Math.Floor(cyclePosition);
        var leds = new Rgb[ledCount];
        for (int i = 0; i < ledCount; i++)
        {
            // 这颗灯在图案里的位置（0～1），再加上转动的偏移
            double phase = ((double)(i % periodLeds) / periodLeds) + shift;
            phase -= Math.Floor(phase);

            // 0 → 0.5 从第一个颜色渐变到第二个，0.5 → 1 再渐变回来，首尾相接不会有断层
            double weight = phase < 0.5 ? phase * 2 : (1 - phase) * 2;
            leds[i] = Mix(first, second, weight);
        }

        return leds;
    }

    /// <summary>按比例混合两个颜色：weight 为 0 是 a，为 1 是 b。</summary>
    private static Rgb Mix(Rgb a, Rgb b, double weight) => new(
        (byte)Math.Round(a.R + ((b.R - a.R) * weight)),
        (byte)Math.Round(a.G + ((b.G - a.G) * weight)),
        (byte)Math.Round(a.B + ((b.B - a.B) * weight)));
}

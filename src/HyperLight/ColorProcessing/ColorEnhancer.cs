namespace HyperLight.ColorProcessing;

/// <summary>
/// 增强颜色：提高饱和度，模仿 Wallpaper Engine 的「增强 LED 颜色」。
/// Wallpaper Engine 的官方文档说明，这个选项的作用就是提高饱和度。
/// 平均色通常偏灰（各种颜色混在一起），提高饱和度后更接近键盘上看到的颜色。
/// </summary>
internal static class ColorEnhancer
{
    /// <summary>
    /// 把饱和度乘以 factor（1 表示不变）。色调和亮度保持不变；饱和度最多到 100%，不会变色。
    /// </summary>
    /// <remarks>
    /// 做法：最亮的那个分量（决定亮度）不动，其他分量离它的距离按同一个倍数拉大。
    /// 所有分量用同一个倍数，所以色调不变；倍数会被限制在"最暗的分量刚好降到 0"为止，所以饱和度不会超过 100%。
    /// </remarks>
    public static Rgb BoostSaturation(Rgb color, double factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1.0);

        int max = Math.Max(color.R, Math.Max(color.G, color.B));
        int min = Math.Min(color.R, Math.Min(color.G, color.B));
        if (max == min)
        {
            // 纯灰色（包括黑和白）没有色调，饱和度怎么提高都还是它自己
            return color;
        }

        double effective = Math.Min(factor, (double)max / (max - min));
        return new Rgb(Stretch(color.R, max, effective), Stretch(color.G, max, effective), Stretch(color.B, max, effective));
    }

    private static byte Stretch(byte channel, int max, double factor) =>
        (byte)Math.Clamp(Math.Round(max - ((max - channel) * factor)), 0, 255);
}

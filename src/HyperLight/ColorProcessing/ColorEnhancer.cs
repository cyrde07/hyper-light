namespace HyperLight.ColorProcessing;

/// <summary>
/// 颜色处理：
///   提高饱和度（<see cref="BoostSaturation"/>），模仿 Wallpaper Engine 的「增强 LED 颜色」。
///   Wallpaper Engine 的官方文档说明，这个选项的作用就是提高饱和度。
///   平均色通常偏灰（各种颜色混在一起），提高饱和度后更接近键盘上看到的颜色。
///   加大差异（<see cref="Separate"/>）：双色、流动灯效里两个颜色太像时，把它们拉开一点，让图案看得出来。
///   LED 颜色加强（<see cref="StrengthenForLed"/>）：让淡颜色在 LED 上不发白（给主板灯用）。
/// </summary>
internal static class ColorEnhancer
{
    /// <summary>饱和度低于这个值（0～1）就当作灰色：色调没有意义，只能靠亮度拉开。</summary>
    private const double graySaturation = 0.15;

    /// <summary>加大差异时，变暗的那个颜色最暗只到原来亮度的这么多，免得变成黑色。</summary>
    private const double minBrightnessFactor = 0.2;

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

    /// <summary>
    /// 加大差异：两个颜色差别不够时把它们拉开，差别够了就原样返回。不撞色，拉开后还是同一个色系。
    /// </summary>
    /// <param name="first">第一个颜色。</param>
    /// <param name="second">第二个颜色。</param>
    /// <param name="strength">强度 0～100。0 表示不处理；越大，要求的最小差别越大、色调拉得越开。</param>
    /// <remarks>
    /// 两步：
    ///   1. 有颜色的（不是灰色）：两个颜色在色环上往两边挪，色调至少相差 strength × 0.8 度（强度 100 时 80 度）。
    ///      其中一个是灰色时，它借用另一个的饱和度，这样挪色调才看得出来。
    ///   2. 还不够（或者都是灰色）：把较暗的那个再调暗，直到差别达到 strength × 1.2（强度 100 时 120，满分 255），
    ///      最暗到原来亮度的 20%。灰色壁纸就会变成"一亮一暗"。
    /// "差别"的算法和颜色变化门槛一样：红、绿、蓝三项里相差最大的那一项。
    /// </remarks>
    public static (Rgb First, Rgb Second) Separate(Rgb first, Rgb second, int strength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 100);

        int minDifference = (int)Math.Round(strength * 1.2);
        if (strength == 0 || ColorChangeFilter.Difference(first, second) >= minDifference)
        {
            return (first, second);
        }

        // 第 1 步：拉开色调
        (double h1, double s1, double v1) = ToHsv(first);
        (double h2, double s2, double v2) = ToHsv(second);
        double maxSaturation = Math.Max(s1, s2);
        if (maxSaturation >= graySaturation)
        {
            double targetGap = strength * 0.8;
            double gap = HueGap(h1, h2);
            if (Math.Abs(gap) < targetGap)
            {
                double middle = h1 + (gap / 2);
                double direction = gap >= 0 ? 1 : -1;
                h1 = middle - (direction * targetGap / 2);
                h2 = middle + (direction * targetGap / 2);
            }

            s1 = Math.Max(s1, s1 < graySaturation ? maxSaturation : 0);
            s2 = Math.Max(s2, s2 < graySaturation ? maxSaturation : 0);
            first = FromHsv(h1, s1, v1);
            second = FromHsv(h2, s2, v2);
        }

        // 第 2 步：还不够，就把较暗的那个调暗
        if (ColorChangeFilter.Difference(first, second) < minDifference)
        {
            bool darkenSecond = Brightness(second) <= Brightness(first);
            Rgb original = darkenSecond ? second : first;
            Rgb other = darkenSecond ? first : second;
            Rgb darker = original;
            for (double factor = 0.95; factor >= minBrightnessFactor; factor -= 0.05)
            {
                darker = Scale(original, factor);
                if (ColorChangeFilter.Difference(darker, other) >= minDifference)
                {
                    break;
                }
            }

            (first, second) = darkenSecond ? (first, darker) : (darker, second);
        }

        return (first, second);
    }

    /// <summary>
    /// LED 颜色加强：淡颜色在 LED 上看起来会发白（LED 的中等亮度看起来比屏幕亮得多），
    /// 这里把颜色按伽马曲线压一下（较暗的分量压得更多），再把最亮的分量恢复成原来的亮度。
    /// 结果是亮度不变、颜色更浓：浅粉 F99396 会变成 F94E52（强度 60 时）。
    /// 灰色（红绿蓝相等）没有颜色可以加强，保持不变。
    /// </summary>
    /// <param name="color">原来的颜色。</param>
    /// <param name="strength">强度 0～100。0 表示不处理；伽马值从 1（不变）到 3（强度 100）。</param>
    public static Rgb StrengthenForLed(Rgb color, int strength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 100);

        int max = Brightness(color);
        if (strength == 0 || max == 0)
        {
            return color;
        }

        double gamma = 1 + (strength / 50.0);
        double Curve(byte channel) => Math.Pow(channel / 255.0, gamma);
        double r = Curve(color.R);
        double g = Curve(color.G);
        double b = Curve(color.B);

        // 把最亮的分量恢复成原来的亮度：只改颜色浓淡，不让灯变暗
        double scale = (max / 255.0) / Math.Max(r, Math.Max(g, b));
        return new Rgb(ToByte(r * scale), ToByte(g * scale), ToByte(b * scale));
    }

    private static byte Stretch(byte channel, int max, double factor) =>
        (byte)Math.Clamp(Math.Round(max - ((max - channel) * factor)), 0, 255);

    /// <summary>亮度：三个分量里最大的那个（0～255）。</summary>
    private static int Brightness(Rgb color) => Math.Max(color.R, Math.Max(color.G, color.B));

    /// <summary>三个分量按同一个比例缩小：色调、饱和度不变，只变暗。</summary>
    private static Rgb Scale(Rgb color, double factor) => new(
        (byte)Math.Round(color.R * factor),
        (byte)Math.Round(color.G * factor),
        (byte)Math.Round(color.B * factor));

    /// <summary>从色调 a 走到色调 b 最近的方向和距离（度）：-180～180，正数表示顺时针。</summary>
    private static double HueGap(double a, double b)
    {
        double gap = (b - a) % 360;
        if (gap > 180)
        {
            gap -= 360;
        }
        else if (gap <= -180)
        {
            gap += 360;
        }

        return gap;
    }

    /// <summary>RGB 换成 色调（0～360 度）、饱和度（0～1）、亮度（0～1）。</summary>
    private static (double Hue, double Saturation, double Value) ToHsv(Rgb color)
    {
        double r = color.R / 255.0;
        double g = color.G / 255.0;
        double b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }
        }

        double saturation = max == 0 ? 0 : delta / max;
        return (NormalizeHue(hue), saturation, max);
    }

    /// <summary>色调、饱和度、亮度换回 RGB。色调可以是任意度数（会自动换到 0～360）。</summary>
    private static Rgb FromHsv(double hue, double saturation, double value)
    {
        hue = NormalizeHue(hue);
        double chroma = value * saturation;
        double x = chroma * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double m = value - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };

        return new Rgb(ToByte(r + m), ToByte(g + m), ToByte(b + m));
    }

    private static double NormalizeHue(double hue)
    {
        double normalized = hue % 360;
        return normalized < 0 ? normalized + 360 : normalized;
    }

    private static byte ToByte(double channel) => (byte)Math.Clamp(Math.Round(channel * 255), 0, 255);
}

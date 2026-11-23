namespace HyperLight.ColorProcessing;

/// <summary>
/// 取色来源：一个只能显示单色的灯区（机箱灯、冷排灯）用哪个颜色。
/// 颜色来自取色方式算出来的上、下两个颜色（<see cref="ZoneColors"/>）。
/// 以后加新的取色来源，只改这个文件。
/// </summary>
internal enum ColorSource
{
    /// <summary>上半区：用上灯区的颜色。</summary>
    Upper,

    /// <summary>下半区：用下灯区的颜色。</summary>
    Lower,

    /// <summary>上下平均：上、下两个颜色的平均。</summary>
    Average,
}

/// <summary>取色来源的取色规则和中文名字。托盘菜单、命令行显示都用这里，说法只写一处。</summary>
internal static class ColorSourceRules
{
    /// <summary>所有取色来源，按菜单里的顺序。</summary>
    public static readonly ColorSource[] All = { ColorSource.Upper, ColorSource.Lower, ColorSource.Average };

    /// <summary>按取色来源，从上下两个颜色里取出一个。</summary>
    public static Rgb Pick(ZoneColors colors, ColorSource source) => source switch
    {
        ColorSource.Upper => colors.Upper,
        ColorSource.Lower => colors.Lower,
        _ => new Rgb(
            (byte)((colors.Upper.R + colors.Lower.R + 1) / 2),
            (byte)((colors.Upper.G + colors.Lower.G + 1) / 2),
            (byte)((colors.Upper.B + colors.Lower.B + 1) / 2)),
    };

    /// <summary>中文名字。</summary>
    public static string TextOf(ColorSource source) => source switch
    {
        ColorSource.Upper => "上半区",
        ColorSource.Lower => "下半区",
        _ => "上下平均",
    };
}

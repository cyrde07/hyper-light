namespace HyperLight.ColorProcessing;

/// <summary>
/// 取色来源：壁纸的哪一块区域。机箱灯、冷排灯用它选颜色；麦克风的取色方式、流动灯效也是由这几块组合出来的。
/// 颜色来自每次截图算出来的 <see cref="WallpaperColors"/>。以后加新的区域，改这个文件和 <see cref="WallpaperColors"/>。
/// </summary>
internal enum ColorSource
{
    /// <summary>上半区：壁纸上半部分。</summary>
    Upper,

    /// <summary>下半区：壁纸下半部分。</summary>
    Lower,

    /// <summary>
    /// 整张：整张壁纸的平均色。
    /// 设置文件里写作 "average"（旧版叫"上下平均"，结果和整张一样），保留这个写法，旧的设置文件不用改。
    /// </summary>
    Average,

    /// <summary>左半区：壁纸左半部分。</summary>
    Left,

    /// <summary>右半区：壁纸右半部分。</summary>
    Right,
}

/// <summary>取色来源的中文名字和菜单顺序。托盘菜单、命令行显示都用这里，说法只写一处。</summary>
internal static class ColorSourceRules
{
    /// <summary>所有取色来源，按菜单里的顺序。</summary>
    public static readonly ColorSource[] All =
    {
        ColorSource.Upper, ColorSource.Lower, ColorSource.Left, ColorSource.Right, ColorSource.Average,
    };

    /// <summary>中文名字。</summary>
    public static string TextOf(ColorSource source) => source switch
    {
        ColorSource.Upper => "上半区",
        ColorSource.Lower => "下半区",
        ColorSource.Left => "左半区",
        ColorSource.Right => "右半区",
        _ => "整张",
    };
}

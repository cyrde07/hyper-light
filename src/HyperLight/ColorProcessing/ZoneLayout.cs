namespace HyperLight.ColorProcessing;

/// <summary>
/// 取色方式：壁纸的哪一部分对应麦克风的哪个灯区。
/// 麦克风硬件只有上、下两个灯区（输出改不了），这里决定的是从壁纸的哪里取颜色（输入）。
/// </summary>
internal enum ZoneLayout
{
    /// <summary>整张壁纸：两个灯区都用整张壁纸的平均色。</summary>
    Whole,

    /// <summary>上下：上灯区取壁纸上半部分，下灯区取下半部分。</summary>
    TopBottom,

    /// <summary>左右：上灯区取壁纸左半部分，下灯区取右半部分。很多壁纸左右颜色差别比上下大。</summary>
    LeftRight,
}

/// <summary>取色方式用壁纸的哪两块区域。对应关系只写在这里。</summary>
internal static class ZoneLayoutRegions
{
    /// <summary>麦克风上灯区、下灯区分别取哪块区域的颜色。</summary>
    public static (ColorSource Upper, ColorSource Lower) Of(ZoneLayout layout) => layout switch
    {
        ZoneLayout.TopBottom => (ColorSource.Upper, ColorSource.Lower),
        ZoneLayout.LeftRight => (ColorSource.Left, ColorSource.Right),
        _ => (ColorSource.Average, ColorSource.Average),
    };
}

/// <summary>取色方式的中文名字。托盘菜单和命令行显示都用这里，说法只写一处。</summary>
internal static class ZoneLayoutText
{
    public static string Of(ZoneLayout layout) => layout switch
    {
        ZoneLayout.TopBottom => "上下（上半张 → 上灯区，下半张 → 下灯区）",
        ZoneLayout.LeftRight => "左右（左半张 → 上灯区，右半张 → 下灯区）",
        _ => "整张壁纸（两个灯区同色）",
    };
}

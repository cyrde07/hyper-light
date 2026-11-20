namespace HyperLight.ColorProcessing;

/// <summary>麦克风上下两个灯区的颜色。</summary>
internal readonly record struct ZoneColors(Rgb Upper, Rgb Lower)
{
    /// <summary>两个灯区用同一个颜色。</summary>
    public static ZoneColors Same(Rgb color) => new(color, color);

    /// <summary>显示成 "上 24386C / 下 1A2B3C" 这种格式。</summary>
    public override string ToString() => $"上 {Upper} / 下 {Lower}";
}

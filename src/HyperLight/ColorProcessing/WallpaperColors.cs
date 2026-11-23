namespace HyperLight.ColorProcessing;

/// <summary>
/// 一次截图算出来的壁纸各区域平均色：上半、下半、左半、右半、整张。
/// 麦克风按取色方式从里面取两个，机箱灯和冷排灯按各自的取色来源取一个。
/// </summary>
internal readonly record struct WallpaperColors(Rgb Upper, Rgb Lower, Rgb Left, Rgb Right, Rgb Whole)
{
    /// <summary>取出某个区域的颜色。区域和字段的对应关系只写在这里。</summary>
    public Rgb Get(ColorSource region) => region switch
    {
        ColorSource.Upper => Upper,
        ColorSource.Lower => Lower,
        ColorSource.Left => Left,
        ColorSource.Right => Right,
        _ => Whole,
    };

    /// <summary>对每个区域的颜色做同一种处理（比如增强饱和度）。</summary>
    public WallpaperColors Map(Func<Rgb, Rgb> change) =>
        new(change(Upper), change(Lower), change(Left), change(Right), change(Whole));
}

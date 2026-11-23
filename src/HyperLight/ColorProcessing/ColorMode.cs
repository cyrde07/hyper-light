namespace HyperLight.ColorProcessing;

/// <summary>颜色模式：灯的颜色从哪里来。</summary>
internal enum ColorMode
{
    /// <summary>跟壁纸：截壁纸算颜色（默认）。</summary>
    Wallpaper,

    /// <summary>自由选色：每个设备用自己选好的固定颜色，不截图。</summary>
    Free,
}

/// <summary>颜色模式的中文名字和菜单顺序。</summary>
internal static class ColorModeRules
{
    /// <summary>所有模式，按菜单里的顺序。</summary>
    public static readonly ColorMode[] All = { ColorMode.Wallpaper, ColorMode.Free };

    /// <summary>中文名字。</summary>
    public static string TextOf(ColorMode mode) => mode == ColorMode.Free ? "自由选色" : "跟壁纸";
}

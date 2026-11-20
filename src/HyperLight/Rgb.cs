using System.Globalization;

namespace HyperLight;

/// <summary>
/// 一个颜色：红（R）、绿（G）、蓝（B）三个分量，每个 0～255。
/// 截屏取色、调色、发给麦克风都用这个类型。
/// </summary>
internal readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>解析 "FF0000" 或 "#FF0000" 这种 6 位十六进制写法。格式不对时返回 false。</summary>
    public static bool TryParseHex(string? text, out Rgb color)
    {
        color = default;
        if (text is null)
        {
            return false;
        }

        string hex = text.Trim();
        if (hex.StartsWith('#'))
        {
            hex = hex[1..];
        }

        if (hex.Length != 6
            || !int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value))
        {
            return false;
        }

        color = new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>显示成 "FF0000" 这种格式。</summary>
    public override string ToString() => $"{R:X2}{G:X2}{B:X2}";
}

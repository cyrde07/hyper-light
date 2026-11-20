using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HyperLight.ColorProcessing;

namespace HyperLight.Tray;

/// <summary>
/// 画托盘图标：一个小圆点，上半部分是上灯区的颜色，下半部分是下灯区的颜色。
/// 暂停时是灰色实心圆；还没有颜色（没找到壁纸等）时是空心圆。
/// 不需要另外的图标文件，图标是程序运行时画出来的。
/// </summary>
internal static class TrayIconPainter
{
    /// <summary>画布大小（像素）。Windows 会按屏幕缩放自动缩到托盘需要的尺寸。</summary>
    private const int size = 32;

    private static readonly Color pausedColor = Color.FromArgb(128, 128, 128);
    private static readonly Color outerRing = Color.FromArgb(220, 30, 30, 30);
    private static readonly Color innerRing = Color.FromArgb(220, 255, 255, 255);

    /// <summary>
    /// 画一个新图标。用完必须交给 <see cref="Destroy"/> 释放（图标占用系统资源，不释放会越积越多）。
    /// </summary>
    public static Icon Paint(ZoneColors? colors, bool paused)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            var circle = new Rectangle(3, 3, size - 7, size - 7);

            if (paused)
            {
                using var brush = new SolidBrush(pausedColor);
                graphics.FillEllipse(brush, circle);
            }
            else if (colors is ZoneColors zones)
            {
                using var upper = new SolidBrush(ToColor(zones.Upper));
                using var lower = new SolidBrush(ToColor(zones.Lower));
                graphics.FillPie(upper, circle, 180, 180); // 从左边顺时针画到右边，经过上方：上半圆
                graphics.FillPie(lower, circle, 0, 180);   // 从右边顺时针画到左边，经过下方：下半圆
            }

            // 外圈深色、内圈白色：深色和浅色的任务栏上都看得见
            using var outer = new Pen(outerRing, 2.5f);
            using var inner = new Pen(innerRing, 1f);
            graphics.DrawEllipse(outer, circle);
            graphics.DrawEllipse(inner, Rectangle.Inflate(circle, -2, -2));
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>释放 <see cref="Paint"/> 画出来的图标。</summary>
    public static void Destroy(Icon icon)
    {
        IntPtr handle = icon.Handle;
        icon.Dispose();
        DestroyIcon(handle);
    }

    private static Color ToColor(Rgb rgb) => Color.FromArgb(rgb.R, rgb.G, rgb.B);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

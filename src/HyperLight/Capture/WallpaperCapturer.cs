using System.Runtime.InteropServices;
using static HyperLight.Capture.NativeMethods;

namespace HyperLight.Capture;

/// <summary>
/// 截壁纸：用 PrintWindow（完整内容模式）把壁纸的宿主窗口画进一块内存。
/// 这块内存反复使用，尺寸变了才重建，避免每次截图都新分配 30 多 MB。
/// 截图不存盘，也不保留旧图：下一次截图直接覆盖这块内存。
/// 只能在一个线程里使用（不是线程安全的）。
/// </summary>
/// <remarks>
/// 方法来自 probe 实测：PrintWindow + PW_RENDERFULLCONTENT 截父窗口能截到壁纸，
/// 即使壁纸被最大化的窗口完全挡住。
/// 注意：PrintWindow 会等目标窗口响应，万一对方卡住，调用方也会被卡住，
/// 所以截图要放在单独的线程里，不能和给麦克风发送颜色放在一起。
/// </remarks>
internal sealed class WallpaperCapturer : IDisposable
{
    private const int bytesPerPixel = 4;

    private IntPtr memoryDc;
    private IntPtr bitmap;
    private IntPtr previousBitmap;
    private IntPtr pixels;
    private bool disposed;

    /// <summary>最近一次截图的宽度（像素）。</summary>
    public int Width { get; private set; }

    /// <summary>最近一次截图的高度（像素）。</summary>
    public int Height { get; private set; }

    /// <summary>
    /// 最近一次截图的像素：每个像素 4 字节（B、G、R、未使用），从上到下逐行排列，每行 Width × 4 字节。
    /// 只在下一次截图之前有效。
    /// </summary>
    public ReadOnlySpan<byte> Pixels
    {
        get
        {
            if (pixels == IntPtr.Zero)
            {
                return ReadOnlySpan<byte>.Empty;
            }

            unsafe
            {
                // 像素内存由 Windows 管理（CreateDIBSection 分配），这里只是直接读它，不复制
                return new ReadOnlySpan<byte>((void*)pixels, checked(Width * Height * bytesPerPixel));
            }
        }
    }

    /// <summary>截一次壁纸。成功返回 true；失败（窗口没了、系统资源不够等）返回 false。</summary>
    public bool TryCapture(WallpaperTarget target)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!EnsureBuffer(target.Width, target.Height))
        {
            return false;
        }

        return PrintWindow(target.CaptureWindow, memoryDc, PW_RENDERFULLCONTENT);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ReleaseBuffer();
    }

    /// <summary>准备好指定尺寸的像素内存。尺寸没变就直接用旧的。</summary>
    private bool EnsureBuffer(int width, int height)
    {
        if (pixels != IntPtr.Zero && width == Width && height == Height)
        {
            return true;
        }

        ReleaseBuffer();

        memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (memoryDc == IntPtr.Zero)
        {
            return false;
        }

        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // 负数表示从上到下排列
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        bitmap = CreateDIBSection(memoryDc, ref header, DIB_RGB_COLORS, out pixels, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
        {
            ReleaseBuffer();
            return false;
        }

        previousBitmap = SelectObject(memoryDc, bitmap);
        Width = width;
        Height = height;
        return true;
    }

    private void ReleaseBuffer()
    {
        if (memoryDc != IntPtr.Zero && previousBitmap != IntPtr.Zero)
        {
            SelectObject(memoryDc, previousBitmap);
        }

        if (bitmap != IntPtr.Zero)
        {
            DeleteObject(bitmap);
        }

        if (memoryDc != IntPtr.Zero)
        {
            DeleteDC(memoryDc);
        }

        memoryDc = IntPtr.Zero;
        bitmap = IntPtr.Zero;
        previousBitmap = IntPtr.Zero;
        pixels = IntPtr.Zero;
        Width = 0;
        Height = 0;
    }
}

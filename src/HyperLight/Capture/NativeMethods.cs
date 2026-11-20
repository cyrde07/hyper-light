using System.Runtime.InteropServices;
using System.Text;

namespace HyperLight.Capture;

/// <summary>
/// 截窗口要用到的 Windows 系统函数（Win32 API）声明。
/// 这里只放声明，不放逻辑。名字和参数与 Windows 官方文档一致，方便对照查资料。
/// </summary>
internal static class NativeMethods
{
    /// <summary>PrintWindow 选项：连显卡画的内容一起截（Windows 8.1 起支持）。</summary>
    public const uint PW_RENDERFULLCONTENT = 0x00000002;

    /// <summary>BitBlt 选项：直接复制像素。</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>BitBlt 选项：连半透明窗口一起截。</summary>
    public const uint CAPTUREBLT = 0x40000000;

    /// <summary>GetSystemMetrics：主显示器宽度（像素）。</summary>
    public const int SM_CXSCREEN = 0;

    /// <summary>GetSystemMetrics：主显示器高度（像素）。</summary>
    public const int SM_CYSCREEN = 1;

    /// <summary>GetAncestor：取父窗口。</summary>
    public const uint GA_PARENT = 1;

    /// <summary>GetAncestor：取最顶层的祖先窗口。</summary>
    public const uint GA_ROOT = 2;

    /// <summary>
    /// 告诉 Windows 本程序自己处理高分辨率缩放（Per-Monitor V2）。
    /// 不设的话，4K 屏开了 150% 缩放时，Windows 会把窗口大小"缩水"后报给我们，截图也会被缩放。
    /// </summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;

        public readonly int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(IntPtr hdc, int x, int y, int cx, int cy, IntPtr hdcSrc, int x1, int y1, uint rop);
}

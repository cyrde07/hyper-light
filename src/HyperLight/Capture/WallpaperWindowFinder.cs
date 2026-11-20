using System.Diagnostics;
using System.Text;
using static HyperLight.Capture.NativeMethods;

namespace HyperLight.Capture;

/// <summary>要截的壁纸：Wallpaper Engine 画壁纸的窗口，以及实际拿来截图的窗口和它的尺寸。</summary>
internal readonly record struct WallpaperTarget(IntPtr WallpaperWindow, IntPtr CaptureWindow, int Width, int Height);

/// <summary>
/// 找 Wallpaper Engine 的壁纸窗口。"哪个窗口是壁纸"的判断规则全项目只写在这里（probe 测试也用它）。
/// </summary>
/// <remarks>
/// 规则来自 probe 实测（Windows 11，2026-11）：
///   1. 壁纸窗口挂在桌面最底层的宿主窗口（Progman / WorkerW）下面；
///   2. 属于 Wallpaper Engine 的进程（wallpaper32 / wallpaper64 / webwallpaper32 等）；
///   3. 可见，并且至少有半个屏幕大。
/// 截图要截它的父窗口：直接截壁纸窗口本身是全黑，截父窗口（WorkerW）才能截到画面。
/// 副作用是会带上桌面图标，实测对平均色的影响很小（每个分量相差 1～5，满分 255）。
/// 调用前程序必须已经设置了高分辨率缩放感知（Program 启动时做），否则 4K 缩放下尺寸不准。
/// </remarks>
internal static class WallpaperWindowFinder
{
    /// <summary>Wallpaper Engine 画壁纸的进程名开头。</summary>
    private static readonly string[] wallpaperProcessPrefixes = { "wallpaper", "webwallpaper" };

    /// <summary>桌面最底层的宿主窗口类名。</summary>
    private static readonly string[] desktopHostClasses = { "Progman", "WorkerW" };

    /// <summary>是不是 Wallpaper Engine 画壁纸的进程。</summary>
    public static bool IsWallpaperProcessName(string processName) =>
        wallpaperProcessPrefixes.Any(prefix => processName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>是不是桌面最底层的宿主窗口类名。</summary>
    public static bool IsDesktopHostClass(string className) =>
        desktopHostClasses.Contains(className, StringComparer.OrdinalIgnoreCase);

    /// <summary>一个窗口看起来是不是壁纸：属于 Wallpaper Engine、可见、至少半个屏幕大。</summary>
    public static bool LooksLikeWallpaper(string processName, bool visible, int width, int height, int screenWidth, int screenHeight) =>
        visible
        && IsWallpaperProcessName(processName)
        && width >= screenWidth / 2
        && height >= screenHeight / 2;

    /// <summary>找到当前的壁纸窗口。找不到（比如 Wallpaper Engine 没开）返回 null。</summary>
    public static WallpaperTarget? Find()
    {
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);
        foreach (IntPtr host in FindTopLevelDesktopHosts())
        {
            foreach (IntPtr child in FindDescendants(host))
            {
                if (TryMakeTarget(child, screenWidth, screenHeight) is WallpaperTarget target)
                {
                    return target;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 之前找到的壁纸窗口现在还能不能用：窗口还在、还可见、尺寸没变。
    /// 换壁纸、换分辨率时 Wallpaper Engine 可能会重建窗口，这时要重新找。
    /// </summary>
    public static bool IsStillValid(WallpaperTarget target) =>
        IsWindow(target.WallpaperWindow)
        && IsWindow(target.CaptureWindow)
        && IsWindowVisible(target.WallpaperWindow)
        && GetWindowRect(target.CaptureWindow, out RECT rect)
        && rect.Width == target.Width
        && rect.Height == target.Height;

    private static WallpaperTarget? TryMakeTarget(IntPtr window, int screenWidth, int screenHeight)
    {
        if (!GetWindowRect(window, out RECT rect))
        {
            return null;
        }

        string? processName = ProcessNameOf(window);
        if (processName is null
            || !LooksLikeWallpaper(processName, IsWindowVisible(window), rect.Width, rect.Height, screenWidth, screenHeight))
        {
            return null;
        }

        // 截父窗口；万一壁纸窗口没有父窗口（不太可能），就截它自己
        IntPtr parent = GetAncestor(window, GA_PARENT);
        IntPtr captureWindow = parent == IntPtr.Zero || parent == GetDesktopWindow() ? window : parent;
        if (!GetWindowRect(captureWindow, out RECT captureRect) || captureRect.Width <= 0 || captureRect.Height <= 0)
        {
            return null;
        }

        return new WallpaperTarget(window, captureWindow, captureRect.Width, captureRect.Height);
    }

    /// <summary>窗口所属进程的名字。进程已经退出时返回 null。</summary>
    private static string? ProcessNameOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process process = Process.GetProcessById(unchecked((int)processId));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static List<IntPtr> FindTopLevelDesktopHosts()
    {
        var hosts = new List<IntPtr>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            if (IsDesktopHostClass(ClassNameOf(hwnd)))
            {
                hosts.Add(hwnd);
            }

            return true;
        };

        EnumWindows(collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return hosts;
    }

    private static List<IntPtr> FindDescendants(IntPtr parent)
    {
        var children = new List<IntPtr>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            children.Add(hwnd);
            return true;
        };

        EnumChildWindows(parent, collect, IntPtr.Zero);
        GC.KeepAlive(collect);
        return children;
    }

    private static string ClassNameOf(IntPtr window)
    {
        var className = new StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString();
    }
}

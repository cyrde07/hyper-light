using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using static HyperLight.Capture.NativeMethods;

namespace HyperLight.Capture;

/// <summary>
/// 方案 A 的可行性测试：找到 Wallpaper Engine 画壁纸的窗口，用几种方法截图，
/// 判断"壁纸被别的窗口挡住时，还能不能截到壁纸本身"。
/// 只读：不碰 Wallpaper Engine 的文件，不往它的进程里注入任何东西，不改它的设置。
/// </summary>
/// <remarks>
/// 这是诊断代码：任何一步失败都只把原因写进报告，不能影响后面的测试，
/// 所以有几处统一接住所有异常（catch Exception），这是有意为之。
/// </remarks>
internal static class WallpaperWindowProbe
{
    /// <summary>存图和比较用的缩略图宽度（像素）。4K 原图一张 30 多 MB，没必要存原图。</summary>
    private const int thumbnailWidth = 480;

    /// <summary>两次截图之间隔多久，用来判断截到的画面是在动，还是一张不变的旧图。</summary>
    private static readonly TimeSpan secondShotDelay = TimeSpan.FromSeconds(1);

    /// <summary>两张图平均每个颜色分量相差不到这个值（满分 255），就算"几乎一样"。</summary>
    private const double sameImageThreshold = 3.0;

    /// <summary>整张图最亮的颜色分量都不到这个值，就算"全黑"（说明没截到东西）。</summary>
    private const int blackThreshold = 8;

    /// <summary>一种截图方法。</summary>
    private sealed record CaptureMethod(string Key, string Description, Func<WindowInfo, Bitmap> Capture);

    private static readonly CaptureMethod[] captureMethods =
    {
        new("PrintWindow-full", "PrintWindow + 完整内容（连显卡画的一起）",
            window => CaptureWithPrintWindow(window, PW_RENDERFULLCONTENT)),
        new("PrintWindow", "PrintWindow 普通模式", window => CaptureWithPrintWindow(window, 0)),
        new("BitBlt", "BitBlt 从窗口复制像素", CaptureWindowWithBitBlt),
    };

    /// <summary>运行全部测试，把截图存进 outputFolder，返回报告文字。本方法不会抛出异常。</summary>
    public static string Run(string outputFolder)
    {
        var report = new StringBuilder();
        try
        {
            RunCore(outputFolder, report);
        }
        catch (Exception ex)
        {
            report.AppendLine();
            report.AppendLine($"测试中途出现意外错误：{ex}");
        }

        return report.ToString();
    }

    private static void RunCore(string outputFolder, StringBuilder report)
    {
        // 高分辨率缩放感知由 Program 启动时统一设置，这里拿到的就是真实像素
        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        report.AppendLine("HyperLight 方案 A 测试：截 Wallpaper Engine 的壁纸窗口");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"系统：{Environment.OSVersion.VersionString}");
        report.AppendLine($"主显示器：{screenWidth}x{screenHeight}");
        report.AppendLine($"截图保存在：{outputFolder}");
        report.AppendLine();

        // ── 1. Wallpaper Engine 的进程 ──
        Dictionary<int, string> processNames = ReadProcessNames();
        var wallpaperPids = processNames
            .Where(pair => WallpaperWindowFinder.IsWallpaperProcessName(pair.Value))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        report.AppendLine("==================== Wallpaper Engine 进程 ====================");
        if (wallpaperPids.Count == 0)
        {
            report.AppendLine("没有找到（名字以 wallpaper 开头的进程）。请先打开 Wallpaper Engine，确认壁纸在显示，再运行一次。");
            return;
        }

        foreach ((int pid, string name) in wallpaperPids.OrderBy(pair => pair.Value))
        {
            report.AppendLine($"  {name}    进程号 {pid}");
        }

        report.AppendLine();

        // ── 2. 桌面结构和 Wallpaper Engine 的窗口 ──
        List<WindowInfo> windows = EnumerateAllWindows(processNames);
        IntPtr desktop = GetDesktopWindow();
        List<WindowInfo> desktopHosts = windows
            .Where(w => w.Parent == desktop && WallpaperWindowFinder.IsDesktopHostClass(w.ClassName))
            .ToList();
        List<WindowInfo> wallpaperWindows = windows.Where(w => wallpaperPids.ContainsKey(w.ProcessId)).ToList();

        report.AppendLine("==================== 桌面最底层的窗口（按层级） ====================");
        foreach (WindowInfo host in desktopHosts)
        {
            AppendWindowTree(report, windows, host, depth: 0);
        }

        report.AppendLine();
        report.AppendLine("==================== Wallpaper Engine 的窗口 ====================");
        if (wallpaperWindows.Count == 0)
        {
            report.AppendLine("没有找到。Wallpaper Engine 可能处于暂停或停止状态，请确认壁纸在动，再运行一次。");
            return;
        }

        foreach (WindowInfo window in wallpaperWindows)
        {
            string parentNote = window.Parent == desktop ? "顶层窗口" : $"父窗口 0x{window.Parent.ToInt64():X}";
            report.AppendLine($"  {window.Describe()}    {parentNote}");
        }

        // 同步程序（sync）实际会截哪个窗口：和下面的测试用的是同一套判断规则
        WallpaperTarget? syncTarget = WallpaperWindowFinder.Find();
        report.AppendLine(syncTarget is WallpaperTarget syncFound
            ? $"同步程序会截的窗口：0x{syncFound.CaptureWindow.ToInt64():X}（{syncFound.Width}x{syncFound.Height}），壁纸窗口 0x{syncFound.WallpaperWindow.ToInt64():X}"
            : "同步程序会截的窗口：没找到");
        report.AppendLine();

        // ── 3. 选出要截的窗口：大小像壁纸的 Wallpaper Engine 窗口，再加上它的父窗口和最顶层祖先窗口 ──
        // 子窗口的画面有时是画在上层窗口上的，所以上层窗口也截一次。
        // Windows 11 24H2 起桌面结构变了：壁纸宿主窗口 WorkerW 挂在 Progman 下面，所以父窗口和顶层祖先可能不是同一个。
        List<WindowInfo> wallpaperSized = wallpaperWindows
            .Where(w => WallpaperWindowFinder.LooksLikeWallpaper(w.ProcessName, w.Visible, w.Rect.Width, w.Rect.Height, screenWidth, screenHeight))
            .ToList();
        var targets = new List<WindowInfo>(wallpaperSized);
        foreach (WindowInfo window in wallpaperSized)
        {
            foreach (IntPtr ancestor in new[] { window.Parent, GetAncestor(window.Handle, GA_ROOT) })
            {
                WindowInfo? found = windows.FirstOrDefault(w => w.Handle == ancestor);
                if (ancestor != desktop && found is not null && targets.All(t => t.Handle != ancestor))
                {
                    targets.Add(found);
                }
            }
        }

        report.AppendLine("==================== 截图测试 ====================");
        if (targets.Count == 0)
        {
            report.AppendLine("没有找到大小像壁纸的窗口（至少半个屏幕大、并且可见），没法测试。");
            return;
        }

        for (int i = 0; i < targets.Count; i++)
        {
            report.AppendLine($"  目标 t{i + 1}：{targets[i].Describe()}");
        }

        report.AppendLine();

        // ── 4. 截两轮，中间隔 1 秒 ──
        List<Shot> roundA = CaptureRound(targets, screenWidth, screenHeight);
        Thread.Sleep(secondShotDelay);
        List<Shot> roundB = CaptureRound(targets, screenWidth, screenHeight);

        try
        {
            SaveThumbnails(roundA, "a", outputFolder, report);
            SaveThumbnails(roundB, "b", outputFolder, report);
            AppendAnalysis(roundA, roundB, report);
        }
        finally
        {
            foreach (Shot shot in roundA.Concat(roundB))
            {
                shot.Thumbnail?.Dispose();
            }
        }
    }

    // ───────────────────────── 分析 ─────────────────────────

    private static void AppendAnalysis(List<Shot> roundA, List<Shot> roundB, StringBuilder report)
    {
        Shot screenA = roundA[0];
        PixelData? screenPixels = screenA.Thumbnail is null ? null : PixelData.Read(screenA.Thumbnail);

        var promising = new List<string>();
        for (int i = 0; i < roundA.Count; i++)
        {
            Shot a = roundA[i];
            Shot b = roundB[i];
            report.AppendLine($"[{a.Key}] {a.Label}");

            if (a.Thumbnail is null || b.Thumbnail is null)
            {
                report.AppendLine($"    截图失败：{a.Error ?? b.Error}");
                continue;
            }

            PixelData pixelsA = PixelData.Read(a.Thumbnail);
            PixelData pixelsB = PixelData.Read(b.Thumbnail);
            bool isBlack = pixelsA.MaxChannel < blackThreshold;
            double change = PixelData.MeanDifference(pixelsA, pixelsB);

            report.AppendLine($"    平均色：{pixelsA.Average}    {(isBlack ? "全黑（没截到东西）" : "不是全黑")}");
            report.AppendLine($"    1 秒内画面变化：{FormatDifference(change)}（{(change < sameImageThreshold ? "没变，可能是静态壁纸，也可能是旧画面" : "在动")}）");

            if (i == 0)
            {
                report.AppendLine("    （这张是整个屏幕的截图，用来对照）");
                continue;
            }

            if (screenPixels is null)
            {
                report.AppendLine("    屏幕截图失败，没法对照");
                continue;
            }

            double vsScreen = PixelData.MeanDifference(pixelsA, screenPixels);
            bool sameAsScreen = vsScreen < sameImageThreshold;
            report.AppendLine($"    和屏幕截图相比：差 {FormatDifference(vsScreen)}（{(sameAsScreen ? "几乎一样，截到的是挡在前面的窗口，不行" : "不一样")}）");

            if (!isBlack && !sameAsScreen)
            {
                promising.Add($"{a.Key}（图片 {a.Key}-a.png）");
            }
        }

        report.AppendLine();
        report.AppendLine("==================== 自动判断 ====================");
        if (promising.Count == 0)
        {
            report.AppendLine("这一轮的方法都不行：截到的要么是全黑，要么和屏幕截图一样。");
            report.AppendLine("请把这份报告发给 Claude。还可以再试一种截图方法（Windows.Graphics.Capture），不行的话再在方案 B、C 里选。");
            return;
        }

        report.AppendLine("这些组合截到的东西既不是全黑、也和屏幕不一样，可能截到了壁纸：");
        foreach (string item in promising)
        {
            report.AppendLine($"  {item}");
        }

        report.AppendLine("请打开截图文件夹，看这些图片是不是你的壁纸（而不是红色网页或黑色），并告诉 Claude。");
    }

    private static string FormatDifference(double difference) =>
        double.IsNaN(difference) ? "尺寸不同，没法比" : difference.ToString("0.0");

    // ───────────────────────── 截图 ─────────────────────────

    /// <summary>一张截图（已缩成缩略图）。截图失败时 Thumbnail 为 null，原因在 Error。</summary>
    private sealed record Shot(string Key, string Label, Bitmap? Thumbnail, string? Error);

    /// <summary>截一轮：第 1 张是整个主显示器，后面是每个目标窗口 × 每种方法。顺序固定，两轮之间可以一一对比。</summary>
    private static List<Shot> CaptureRound(List<WindowInfo> targets, int screenWidth, int screenHeight)
    {
        var shots = new List<Shot>
        {
            TakeShot("screen", "整个屏幕（对照用）", () => CaptureScreen(screenWidth, screenHeight)),
        };

        for (int i = 0; i < targets.Count; i++)
        {
            WindowInfo target = targets[i];
            foreach (CaptureMethod method in captureMethods)
            {
                shots.Add(TakeShot($"t{i + 1}-{method.Key}", $"目标 t{i + 1}（{target.ClassName}）用 {method.Description}", () => method.Capture(target)));
            }
        }

        return shots;
    }

    private static Shot TakeShot(string key, string label, Func<Bitmap> capture)
    {
        try
        {
            using Bitmap full = capture();
            return new Shot(key, label, MakeThumbnail(full), null);
        }
        catch (Exception ex)
        {
            return new Shot(key, label, null, $"{ex.GetType().Name}：{ex.Message}");
        }
    }

    private static Bitmap CaptureWithPrintWindow(WindowInfo window, uint flags)
    {
        var bitmap = new Bitmap(window.Rect.Width, window.Rect.Height, PixelFormat.Format32bppRgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr hdc = graphics.GetHdc();
            try
            {
                if (!PrintWindow(window.Handle, hdc, flags))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "PrintWindow 返回失败");
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap CaptureWindowWithBitBlt(WindowInfo window)
    {
        IntPtr windowDc = GetWindowDC(window.Handle);
        if (windowDc == IntPtr.Zero)
        {
            throw new Win32Exception("GetWindowDC 返回失败");
        }

        try
        {
            return CopyFromDc(windowDc, 0, 0, window.Rect.Width, window.Rect.Height);
        }
        finally
        {
            ReleaseDC(window.Handle, windowDc);
        }
    }

    private static Bitmap CaptureScreen(int width, int height)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new Win32Exception("GetDC 返回失败");
        }

        try
        {
            return CopyFromDc(screenDc, 0, 0, width, height);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Bitmap CopyFromDc(IntPtr sourceDc, int x, int y, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(bitmap);
            IntPtr hdc = graphics.GetHdc();
            try
            {
                if (!BitBlt(hdc, 0, 0, width, height, sourceDc, x, y, SRCCOPY | CAPTUREBLT))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt 返回失败");
                }
            }
            finally
            {
                graphics.ReleaseHdc(hdc);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static Bitmap MakeThumbnail(Bitmap source)
    {
        int height = Math.Max(1, (int)Math.Round((double)source.Height * thumbnailWidth / source.Width));
        var thumbnail = new Bitmap(thumbnailWidth, height, PixelFormat.Format24bppRgb);
        try
        {
            using Graphics graphics = Graphics.FromImage(thumbnail);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(source, 0, 0, thumbnailWidth, height);
            return thumbnail;
        }
        catch
        {
            thumbnail.Dispose();
            throw;
        }
    }

    private static void SaveThumbnails(List<Shot> shots, string round, string folder, StringBuilder report)
    {
        foreach (Shot shot in shots)
        {
            if (shot.Thumbnail is null)
            {
                continue;
            }

            string path = Path.Combine(folder, $"{shot.Key}-{round}.png");
            try
            {
                shot.Thumbnail.Save(path, ImageFormat.Png);
            }
            catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
            {
                report.AppendLine($"  保存图片失败 {path}：{ex.Message}");
            }
        }
    }

    // ───────────────────────── 像素统计 ─────────────────────────

    /// <summary>一张缩略图的像素（每个像素 3 字节，顺序是 B、G、R）。</summary>
    private sealed class PixelData
    {
        private readonly byte[] bytes;

        private PixelData(int width, int height, byte[] bytes)
        {
            Width = width;
            Height = height;
            this.bytes = bytes;

            long b = 0, g = 0, r = 0;
            int max = 0;
            for (int i = 0; i < bytes.Length; i += 3)
            {
                b += bytes[i];
                g += bytes[i + 1];
                r += bytes[i + 2];
                max = Math.Max(max, Math.Max(bytes[i], Math.Max(bytes[i + 1], bytes[i + 2])));
            }

            int count = Math.Max(1, bytes.Length / 3);
            Average = new Rgb((byte)(r / count), (byte)(g / count), (byte)(b / count));
            MaxChannel = max;
        }

        public int Width { get; }

        public int Height { get; }

        public Rgb Average { get; }

        public int MaxChannel { get; }

        /// <summary>读取 24 位缩略图的像素（去掉每行末尾的对齐填充）。</summary>
        public static PixelData Read(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int rowBytes = bitmap.Width * 3;
                byte[] bytes = new byte[rowBytes * bitmap.Height];
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + (y * data.Stride), bytes, y * rowBytes, rowBytes);
                }

                return new PixelData(bitmap.Width, bitmap.Height, bytes);
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>两张图平均每个颜色分量相差多少（0～255）。尺寸不同时返回 NaN。</summary>
        public static double MeanDifference(PixelData first, PixelData second)
        {
            if (first.Width != second.Width || first.Height != second.Height)
            {
                return double.NaN;
            }

            long total = 0;
            for (int i = 0; i < first.bytes.Length; i++)
            {
                total += Math.Abs(first.bytes[i] - second.bytes[i]);
            }

            return (double)total / Math.Max(1, first.bytes.Length);
        }
    }

    // ───────────────────────── 窗口和进程 ─────────────────────────

    /// <summary>一个窗口的信息。</summary>
    private sealed record WindowInfo(
        IntPtr Handle,
        IntPtr Parent,
        string ClassName,
        string Title,
        RECT Rect,
        bool Visible,
        int ProcessId,
        string ProcessName)
    {
        public string Describe() =>
            $"0x{Handle.ToInt64():X}  类名={ClassName}  标题={(Title.Length == 0 ? "（无）" : Title)}"
            + $"  大小={Rect.Width}x{Rect.Height}  位置=({Rect.Left},{Rect.Top})  {(Visible ? "可见" : "隐藏")}  进程={ProcessName}";
    }

    /// <summary>按层级打印一个窗口和它下面的所有子窗口。层级太深时停止，防止报告过长。</summary>
    private static void AppendWindowTree(StringBuilder report, List<WindowInfo> windows, WindowInfo window, int depth)
    {
        const int maxDepth = 4;
        string indent = new(' ', 2 + (depth * 4));
        report.AppendLine($"{indent}{(depth == 0 ? "" : "└ ")}{window.Describe()}");
        if (depth >= maxDepth)
        {
            return;
        }

        foreach (WindowInfo child in windows.Where(w => w.Parent == window.Handle))
        {
            AppendWindowTree(report, windows, child, depth + 1);
        }
    }

    /// <summary>列出所有窗口：顶层窗口，以及它们下面的所有子窗口。</summary>
    private static List<WindowInfo> EnumerateAllWindows(IReadOnlyDictionary<int, string> processNames)
    {
        var handles = new List<IntPtr>();
        EnumWindowsProc collect = (hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        };

        EnumWindows(collect, IntPtr.Zero);
        IntPtr[] topLevel = handles.ToArray();
        foreach (IntPtr top in topLevel)
        {
            EnumChildWindows(top, collect, IntPtr.Zero);
        }

        // 回调函数在系统调用期间必须一直存活，防止被垃圾回收
        GC.KeepAlive(collect);

        return handles.Distinct().Select(handle => ReadWindow(handle, processNames)).ToList();
    }

    private static WindowInfo ReadWindow(IntPtr handle, IReadOnlyDictionary<int, string> processNames)
    {
        var className = new StringBuilder(256);
        GetClassName(handle, className, className.Capacity);

        var title = new StringBuilder(256);
        GetWindowText(handle, title, title.Capacity);

        GetWindowRect(handle, out RECT rect);
        GetWindowThreadProcessId(handle, out uint pid);
        int processId = unchecked((int)pid);

        return new WindowInfo(
            Handle: handle,
            Parent: GetAncestor(handle, GA_PARENT),
            ClassName: className.ToString(),
            Title: title.ToString(),
            Rect: rect,
            Visible: IsWindowVisible(handle),
            ProcessId: processId,
            ProcessName: processNames.TryGetValue(processId, out string? name) ? name : "（未知）");
    }

    /// <summary>读取所有进程的"进程号 → 进程名"。</summary>
    private static Dictionary<int, string> ReadProcessNames()
    {
        var names = new Dictionary<int, string>();
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    names[process.Id] = process.ProcessName;
                }
                catch (InvalidOperationException)
                {
                    // 进程正好在这时退出了，跳过
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return names;
    }
}

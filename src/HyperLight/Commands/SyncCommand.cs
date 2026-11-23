using System.Diagnostics;
using System.Runtime.InteropServices;
using HyperLight.ColorProcessing;
using HyperLight.Config;

namespace HyperLight.Commands;

/// <summary>
/// sync 命令（M2，诊断用）：截 Wallpaper Engine 的壁纸取色，持续发给麦克风，直到按 Ctrl+C。
/// 和托盘程序用的是同一个同步引擎，区别只是把状态显示在命令行窗口里，方便排查问题。
/// 窗口里会显示状态变化（找到壁纸、颜色变了、麦克风断开等），
/// 并且每隔几秒显示一次当前颜色（带色块）、截图耗时、发送帧率、CPU 和内存。
/// </summary>
internal static class SyncCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  sync   开始同步：截 Wallpaper Engine 的壁纸取色，持续发给麦克风（按 Ctrl+C 停止）",
        "         设置在 exe 旁边的 hyperlight.json（第一次运行时自动生成，带中文说明）",
    };

    /// <summary>多久看一次引擎状态（状态变了就马上显示）。</summary>
    private static readonly TimeSpan pollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>多久显示一次完整的状态行。</summary>
    private static readonly TimeSpan statusInterval = TimeSpan.FromSeconds(5);

    /// <summary>执行 sync 命令。</summary>
    public static int Run()
    {
        using MicOwnerLock? owner = MicOwnerLock.TryAcquire();
        if (owner is null)
        {
            Console.WriteLine(MicOwnerLock.BusyMessage);
            return 3;
        }

        bool colorBlocks = TryEnableColorOutput();
        AppConfig config = AppConfig.Load(AppConfig.DefaultPath, out List<string> notes);
        PrintStart(config, notes);

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            // 不让系统直接杀掉程序：先停止同步、关闭设备，再正常退出
            e.Cancel = true;
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 程序正好在退出，忽略
            }
        };

        var clock = Stopwatch.StartNew();
        Console.CancelKeyPress += onCtrlC;
        try
        {
            using var engine = new SyncEngine(config);
            engine.Start();

            var meter = new ResourceMeter();
            // 第一次比较时让两个状态都显示出来
            SyncStatus previous = new() { WallpaperState = "", MicState = "", BoardState = "" };
            TimeSpan nextStatus = statusInterval;
            long framesAtLastStatus = 0;
            TimeSpan lastStatusTime = TimeSpan.Zero;

            while (!cancel.Token.WaitHandle.WaitOne(pollInterval))
            {
                SyncStatus current = engine.Status;
                PrintChanges(previous, current, config, clock, colorBlocks);
                previous = current;

                if (clock.Elapsed >= nextStatus)
                {
                    double seconds = (clock.Elapsed - lastStatusTime).TotalSeconds;
                    double sendFps = seconds > 0 ? (current.FramesSent - framesAtLastStatus) / seconds : 0;
                    PrintStatusLine(current, sendFps, meter.Sample(), clock, colorBlocks);
                    framesAtLastStatus = current.FramesSent;
                    lastStatusTime = clock.Elapsed;
                    nextStatus = clock.Elapsed + statusInterval;
                }
            }

            // 离开 using 时引擎停止：各个线程收尾，麦克风停止接收后会马上回到自带灯效，主板灯保持最后的颜色
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
        }

        Console.WriteLine();
        PrintLine(clock, "已停止同步。麦克风会马上回到它自带的灯效，机箱灯和冷排灯保持最后的颜色。");
        return 0;
    }

    // ───────────────────────── 显示 ─────────────────────────

    private static void PrintStart(AppConfig config, List<string> notes)
    {
        Console.WriteLine("HyperLight 壁纸同步（诊断模式）");
        Console.WriteLine(
            $"截图：每 {config.CaptureIntervalMs} 毫秒一次    发送：每 {config.SendIntervalMs} 毫秒一次    "
            + $"变化门槛：{config.ChangeThreshold}    渐变：{config.FadeMs} 毫秒");
        Console.WriteLine(
            $"增强颜色：{(config.BoostColors ? $"开（{config.SaturationBoost} 倍）" : "关")}    "
            + $"取色方式：{ZoneLayoutText.Of(config.ZoneLayout)}");
        Console.WriteLine(
            $"麦克风：{(config.MicEnabled ? "开" : "关")}    "
            + $"机箱灯：{SyncEngine.DescribeBoardZone(config.CaseEnabled, config.CaseColorSource, config.CaseEffect)}    "
            + $"冷排灯：{SyncEngine.DescribeBoardZone(config.RadiatorEnabled, config.RadiatorColorSource, config.RadiatorEffect)}");
        Console.WriteLine($"配置文件：{AppConfig.DefaultPath}");
        foreach (string note in notes)
        {
            Console.WriteLine($"注意：{note}");
        }

        Console.WriteLine("按 Ctrl+C 停止。");
        Console.WriteLine();
    }

    /// <summary>状态有变化时，每种变化打印一行。</summary>
    private static void PrintChanges(SyncStatus previous, SyncStatus current, AppConfig config, Stopwatch clock, bool colorBlocks)
    {
        if (current.WallpaperState != previous.WallpaperState)
        {
            PrintLine(clock, current.WallpaperState);
        }

        if (current.ColorChanges != previous.ColorChanges && current.Target is ZoneColors newColors)
        {
            string message = previous.Target is ZoneColors oldColors
                ? $"壁纸颜色变了：{ZoneSwatch(oldColors, colorBlocks)} → {ZoneSwatch(newColors, colorBlocks)}（{config.FadeMs} 毫秒渐变）"
                : $"开始同步颜色：{ZoneSwatch(newColors, colorBlocks)}";
            PrintLine(clock, message);
        }

        if (current.MicState != previous.MicState)
        {
            PrintLine(clock, current.MicState);
        }

        if (current.BoardState != previous.BoardState)
        {
            PrintLine(clock, $"主板：{current.BoardState}");
        }

        if (current.OpenRgbRunning != previous.OpenRgbRunning)
        {
            PrintLine(clock, current.OpenRgbRunning
                ? "警告：检测到 OpenRGB 正在运行。它会和本程序同时控制主板的灯，灯可能乱变，请完全退出 OpenRGB。"
                : "OpenRGB 已退出。");
        }

        if (current.NgenuityRunning != previous.NgenuityRunning)
        {
            PrintLine(clock, current.NgenuityRunning
                ? "警告：检测到 NGENUITY 正在运行。两个程序会同时发数据，灯可能乱闪，请从托盘区退出 NGENUITY。"
                : "NGENUITY 已退出。");
        }

        if (current.LastError is string error && error != previous.LastError)
        {
            PrintLine(clock, error);
        }
    }

    private static void PrintStatusLine(SyncStatus status, double sendFps, (double CpuPercent, double MemoryMb) usage, Stopwatch clock, bool colorBlocks)
    {
        string mic = status.Target is ZoneColors target ? ZoneSwatch(target, colorBlocks) : "—（自带灯效）";
        PrintLine(clock,
            $"麦克风 {mic} | 截图 {status.LastCaptureMs:0.0} 毫秒 | 发送 {sendFps:0.0} 帧/秒"
            + $" | CPU {usage.CpuPercent:0.0}% | 内存 {usage.MemoryMb:0} MB");
    }

    /// <summary>两个灯区的颜色显示成"上 色块 十六进制  下 色块 十六进制"。</summary>
    private static string ZoneSwatch(ZoneColors colors, bool colorBlocks) =>
        $"上 {Swatch(colors.Upper, colorBlocks)}  下 {Swatch(colors.Lower, colorBlocks)}";

    /// <summary>颜色显示成"色块 + 十六进制"。窗口不支持彩色时只显示十六进制。</summary>
    private static string Swatch(Rgb color, bool colorBlocks) =>
        colorBlocks ? $"\u001b[48;2;{color.R};{color.G};{color.B}m    \u001b[0m {color}" : color.ToString();

    private static void PrintLine(Stopwatch clock, string message) =>
        Console.WriteLine($"[{clock.Elapsed:mm\\:ss}] {message}");

    // ───────────────────────── CPU 和内存 ─────────────────────────

    /// <summary>算本程序的 CPU 占用（和任务管理器一样，按所有核心合计）和内存占用。</summary>
    private sealed class ResourceMeter
    {
        private readonly Stopwatch wall = Stopwatch.StartNew();
        private TimeSpan lastCpu = TimeSpan.Zero;
        private TimeSpan lastWall = TimeSpan.Zero;

        public (double CpuPercent, double MemoryMb) Sample()
        {
            using Process self = Process.GetCurrentProcess();
            TimeSpan cpu = self.TotalProcessorTime;
            long memoryBytes = self.WorkingSet64;
            TimeSpan now = wall.Elapsed;

            double wallMs = Math.Max(1, (now - lastWall).TotalMilliseconds);
            double cpuPercent = (cpu - lastCpu).TotalMilliseconds / wallMs / Environment.ProcessorCount * 100;
            lastCpu = cpu;
            lastWall = now;
            return (cpuPercent, memoryBytes / 1024.0 / 1024.0);
        }
    }

    // ───────────────────────── 让窗口支持彩色色块 ─────────────────────────

    private const int stdOutputHandle = -11;
    private const uint enableVirtualTerminalProcessing = 0x0004;

    /// <summary>
    /// 打开控制台的彩色输出（Windows 叫"虚拟终端处理"），用来显示颜色色块。
    /// Windows Terminal 默认就支持；老式命令行窗口需要打开。打不开就只显示颜色的十六进制。
    /// </summary>
    private static bool TryEnableColorOutput()
    {
        if (Console.IsOutputRedirected)
        {
            return false;
        }

        try
        {
            IntPtr handle = GetStdHandle(stdOutputHandle);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1) || !GetConsoleMode(handle, out uint mode))
            {
                return false;
            }

            return (mode & enableVirtualTerminalProcessing) != 0
                || SetConsoleMode(handle, mode | enableVirtualTerminalProcessing);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 不是 Windows（只有云端测试时会这样）：这些终端本来就支持彩色
            return !OperatingSystem.IsWindows();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}

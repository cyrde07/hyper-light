using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using HyperLight.Capture;
using HyperLight.Commands;
using HyperLight.Device;
using HyperLight.Tray;

namespace HyperLight;

/// <summary>
/// 程序入口。
/// 不带参数运行（双击 exe、开机自启）：启动托盘程序，这是平时用的方式。
/// 带命令运行：诊断工具，要在 .bat 或命令行里用。
///   list（M0 设备诊断）、color（M1 固定颜色测试）、probe（方案 A 测试）、sync（壁纸同步，命令行版）、
///   board（M5 主板灯光诊断）、gpu（显卡灯光诊断）。
/// </summary>
internal static class Program
{
    /// <summary>AttachConsole 的参数：连上启动本程序的那个进程的命令行窗口。</summary>
    private const int attachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        TryEnableDpiAwareness();

        bool startedByAutoStart = args.Length == 1 && args[0] == AutoStart.Argument;
        if (args.Length == 0 || startedByAutoStart)
        {
            return TrayApp.Run(startedByAutoStart);
        }

        AttachToParentConsole();
        TryUseUtf8Console();
        string command = args[0].Trim().ToLowerInvariant();

        try
        {
            return command switch
            {
                "list" => RunList(),
                "color" => ColorCommand.Run(args[1..]),
                "probe" => ProbeCommand.Run(),
                "sync" => SyncCommand.Run(),
                "board" => BoardCommand.Run(args[1..]),
                "gpu" => GpuCommand.Run(args[1..]),
                "help" or "-h" or "--help" or "/?" => PrintUsage(),
                _ => PrintUnknownCommand(command),
            };
        }
        catch (Exception ex)
        {
            // 最后一道防线：任何没预料到的异常都打印出来，不让程序直接崩溃退出
            Console.WriteLine();
            Console.WriteLine("程序遇到意外错误，请把下面的内容发给 Claude：");
            Console.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>list 命令：生成诊断报告，显示在窗口里，并在 exe 旁边存一份文本文件，方便发给别人看。</summary>
    private static int RunList()
    {
        string report = HidDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();
        ReportFile.SaveNextToExe("hid-list", report);
        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("用法：");
        Console.WriteLine("  HyperLight.exe          不带命令：启动托盘程序（平时就这样用，双击 exe 也是这样）");
        Console.WriteLine("  HyperLight.exe <命令>   诊断工具，请在 .bat 文件里运行：");
        Console.WriteLine("  list   列出所有 HID 设备，找出 QuadCast S，并检查 NGENUITY 是否在运行");
        foreach (string line in ColorCommand.UsageLines.Concat(ProbeCommand.UsageLines).Concat(SyncCommand.UsageLines).Concat(BoardCommand.UsageLines).Concat(GpuCommand.UsageLines))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine("  help   显示这段说明");
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.WriteLine($"不认识的命令：{command}");
        Console.WriteLine();
        PrintUsage();
        return 2;
    }

    /// <summary>
    /// 本程序是窗口程序（双击时不会弹出黑色命令行窗口），所以带命令运行时，
    /// 要先连上启动它的那个命令行窗口（比如 .bat 的窗口），文字才显示得出来。连不上就新开一个命令行窗口。
    /// </summary>
    private static void AttachToParentConsole()
    {
        try
        {
            if (!AttachConsole(attachParentProcess))
            {
                AllocConsole();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 不是 Windows（只有云端测试时会这样）：本来就在命令行里运行，不需要连接
        }
    }

    /// <summary>让窗口正确显示中文。失败也没关系（比如没有控制台窗口），忽略即可。</summary>
    private static void TryUseUtf8Console()
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (Exception ex) when (ex is IOException or SecurityException)
        {
            // 设置失败只会影响中文显示，不影响功能
        }
    }

    /// <summary>
    /// 让 Windows 按真实像素报告窗口大小（4K 屏开了缩放也一样）。截壁纸要用到，必须在读取任何窗口信息之前设置。
    /// 设置失败只影响截壁纸的尺寸判断，不影响其他功能。
    /// </summary>
    private static void TryEnableDpiAwareness()
    {
        try
        {
            NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 不是 Windows（只有云端测试时会这样），或者系统太旧不支持
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}

using System.Security;
using System.Text;
using HyperLight.Device;

namespace HyperLight;

/// <summary>
/// 程序入口：按命令行参数分发命令。
/// M0 只有 list 命令。不带参数运行（比如双击 exe）时默认执行 list，结束后等你按回车，防止窗口一闪就关。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        TryUseUtf8Console();

        bool startedWithoutArgs = args.Length == 0;
        string command = startedWithoutArgs ? "list" : args[0].Trim().ToLowerInvariant();

        int exitCode;
        try
        {
            exitCode = command switch
            {
                "list" => RunList(),
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
            exitCode = 1;
        }

        if (startedWithoutArgs)
        {
            WaitForEnter();
        }

        return exitCode;
    }

    /// <summary>list 命令：生成诊断报告，显示在窗口里，并在 exe 旁边存一份文本文件，方便发给别人看。</summary>
    private static int RunList()
    {
        string report = HidDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();

        string fileName = $"hid-list-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        try
        {
            // UTF-8 带 BOM：Windows 记事本打开中文不会乱码
            File.WriteAllText(path, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"报告已保存：{path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"报告保存失败（{ex.Message}），请直接复制窗口里的内容。");
        }

        return 0;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("用法：HyperLight.exe <命令>");
        Console.WriteLine("  list   列出所有 HID 设备，找出 QuadCast S，并检查 NGENUITY 是否在运行（不带命令时默认执行这个）");
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

    /// <summary>双击运行时，等用户按回车再关窗口。输入被重定向（比如脚本调用）时不等。</summary>
    private static void WaitForEnter()
    {
        if (Console.IsInputRedirected)
        {
            return;
        }

        Console.WriteLine();
        Console.Write("按回车键关闭窗口…");
        Console.ReadLine();
    }
}

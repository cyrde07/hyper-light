using HyperLight.Device;

namespace HyperLight.Commands;

/// <summary>
/// board 命令（M5，主板灯光）：诊断技嘉主板的灯光芯片。
/// 现在只有探测（M5-0）：读取芯片信息，不改任何灯光。
/// </summary>
internal static class BoardCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  board  主板灯光诊断：读取技嘉主板灯光芯片的信息（不改灯光），报告也存到 exe 旁边",
    };

    /// <summary>执行 board 命令。</summary>
    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.WriteLine($"board 命令现在不需要参数（收到了：{string.Join(" ", args)}）。");
            return 2;
        }

        string report = MotherboardLightDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();
        ReportFile.SaveNextToExe("board-info", report);
        return 0;
    }
}

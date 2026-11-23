using HyperLight.Device;

namespace HyperLight.Commands;

/// <summary>
/// gpu 命令（显卡灯）：诊断华硕 ROG STRIX RTX 4090 白色版的灯光芯片。
/// 现在只有探测（G0）：读取芯片信息，不改任何灯光。
/// </summary>
internal static class GpuCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  gpu    显卡灯光诊断：找到显卡、认灯光芯片、读芯片型号和灯珠数（不改灯光），报告也存到 exe 旁边",
    };

    /// <summary>执行 gpu 命令。args 是 "gpu" 后面的参数。</summary>
    public static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.WriteLine($"gpu 命令现在不需要参数（收到了：{string.Join(" ", args)}）。");
            return 2;
        }

        string report = GpuLightDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();
        ReportFile.SaveNextToExe("gpu-info", report);
        return 0;
    }
}

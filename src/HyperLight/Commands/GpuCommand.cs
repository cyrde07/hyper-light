using HyperLight.Device;

namespace HyperLight.Commands;

/// <summary>
/// gpu 命令（显卡灯）：诊断和测试华硕 ROG STRIX RTX 4090 白色版的灯光芯片。
///   gpu               读取芯片信息（G0，不改灯光）
///   gpu color 颜色    打开直接控制模式，所有灯设成这个颜色（G1）
///   gpu release       关闭直接控制模式，显卡回到自己的灯效（G1）
/// </summary>
internal static class GpuCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  gpu    显卡灯光诊断：找到显卡、认灯光芯片、读芯片型号和灯珠数（不改灯光），报告也存到 exe 旁边",
        "  gpu color <颜色>   显卡灯设成固定颜色（打开直接控制模式）。例子：gpu color FF0000",
        "  gpu release        关闭直接控制模式，显卡回到它自己的灯效",
    };

    /// <summary>执行 gpu 命令。args 是 "gpu" 后面的参数。</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return RunInfo();
        }

        // 下面的子命令都会写显卡：托盘程序在运行时不允许，免得两边同时控制
        using MicOwnerLock? owner = MicOwnerLock.TryAcquire();
        if (owner is null)
        {
            Console.WriteLine(MicOwnerLock.BusyMessage);
            return 3;
        }

        string sub = args[0].Trim().ToLowerInvariant();
        return sub switch
        {
            "color" when args.Length == 2 && Rgb.TryParseHex(args[1], out Rgb color) => RunColor(color),
            "release" when args.Length == 1 => RunRelease(),
            _ => PrintUsage($"看不懂的参数：{string.Join(" ", args)}"),
        };
    }

    private static int RunColor(Rgb color)
    {
        GpuLight? light = Connect();
        if (light is null)
        {
            return 1;
        }

        Console.WriteLine($"显卡灯（{light.LedCount} 颗灯）设成 {color}。");
        if (!light.TrySetDirectMode(true) || !light.TrySetColor(color))
        {
            Console.WriteLine($"发送失败：{light.LastError ?? "原因未知"}");
            return 1;
        }

        Console.WriteLine("已发送。现在程序已经停止发送，请看：");
        Console.WriteLine("  1. 显卡灯有没有变成这个颜色？（红色有没有变成别的颜色）");
        Console.WriteLine("  2. 等 10 秒左右，颜色能不能一直保持？");
        Console.WriteLine("看完按回车退出。想让显卡回到自己的灯效，运行 gpu release。");
        Console.ReadLine();
        return 0;
    }

    private static int RunRelease()
    {
        GpuLight? light = Connect();
        if (light is null)
        {
            return 1;
        }

        if (!light.TrySetDirectMode(false))
        {
            Console.WriteLine($"发送失败：{light.LastError ?? "原因未知"}");
            return 1;
        }

        Console.WriteLine("已关闭直接控制模式。显卡应该回到它自己的灯效（之前是呼吸灯）。看完按回车退出。");
        Console.ReadLine();
        return 0;
    }

    /// <summary>连接显卡灯。失败时显示原因并返回 null。</summary>
    private static GpuLight? Connect()
    {
        GpuLight? light = GpuLight.TryConnect(out string? error);
        if (light is null)
        {
            Console.WriteLine($"连不上显卡灯：{error}");
            Console.WriteLine("请先完全退出 Armoury Crate、GPU Tweak，再运行一次。");
        }

        return light;
    }

    private static int PrintUsage(string error)
    {
        Console.WriteLine(error);
        Console.WriteLine();
        foreach (string line in UsageLines)
        {
            Console.WriteLine(line);
        }

        return 2;
    }

    private static int RunInfo()
    {
        string report = GpuLightDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();
        ReportFile.SaveNextToExe("gpu-info", report);
        return 0;
    }
}

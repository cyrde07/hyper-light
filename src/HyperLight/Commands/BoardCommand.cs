using System.Diagnostics;
using HyperLight.ColorProcessing;
using HyperLight.Config;
using HyperLight.Device;

namespace HyperLight.Commands;

/// <summary>
/// board 命令（M5，主板灯光）：诊断和测试技嘉主板的灯光芯片。
///   board                        读取芯片信息（M5-0，不改灯光）
///   board color 机箱色 [冷排色]   设成固定颜色（M5-1）
///   board count                  把灯分段上色，用来数灯珠（M5-1）
///   board range 起 止             冷排只点亮第"起"到第"止"颗灯（白色），用来找灯珠一共有多少颗
///   board flow 颜色1 颜色2         机箱灯和冷排灯用两个颜色流动 10 秒（和托盘程序同一套流动算法）
///   board onboard 颜色 [下方颜色]   主板自己的灯（I/O 挡板上、下）设成固定颜色
/// color 和 count 发一次就停，然后等用户按回车：用来观察程序停发后，灯能不能保持颜色。
/// </summary>
internal static class BoardCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  board  主板灯光诊断：读取技嘉主板灯光芯片的信息（不改灯光），报告也存到 exe 旁边",
        "  board color <机箱颜色> [冷排颜色] [--apply]",
        "         把机箱灯和冷排灯设成固定颜色。只写一个颜色时两边相同。例子：board color FF0000 0000FF",
        "  board count [--apply]",
        "         把灯每 4 颗一段，按 红 绿 蓝 黄 青 紫 循环上色，拍照用来数灯珠",
        "  board range <从第几颗> <到第几颗>",
        "         冷排只点亮这一段灯（白色），其他全黑；机箱灯不变。例子：board range 1 64",
        "  board flow <颜色1> <颜色2>",
        "         机箱灯和冷排灯用两个颜色流动 10 秒，测试流动灯效。例子：board flow FF0000 0000FF",
        "  board onboard <颜色> [下方颜色]",
        "         主板自己的灯设成固定颜色。只写一个颜色时上下相同；写两个时第一个给 I/O 挡板上方、第二个给下方",
        "         --apply  发完颜色后再发一个\"生效\"指令（测试需不需要它）",
    };

    private const string applyOption = "--apply";

    /// <summary>数灯珠时每段几颗灯。</summary>
    private const int countBandSize = 4;

    /// <summary>数灯珠时循环使用的颜色和名字。</summary>
    private static readonly (Rgb Color, string Name)[] countColors =
    {
        (new Rgb(255, 0, 0), "红"),
        (new Rgb(0, 255, 0), "绿"),
        (new Rgb(0, 0, 255), "蓝"),
        (new Rgb(255, 255, 0), "黄"),
        (new Rgb(0, 255, 255), "青"),
        (new Rgb(255, 0, 255), "紫"),
    };

    /// <summary>执行 board 命令。args 是 "board" 后面的参数。</summary>
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            return RunInfo();
        }

        string sub = args[0].Trim().ToLowerInvariant();
        string[] rest = args[1..];
        bool apply = rest.Contains(applyOption, StringComparer.OrdinalIgnoreCase);
        string[] values = rest.Where(arg => !arg.Equals(applyOption, StringComparison.OrdinalIgnoreCase)).ToArray();

        // 下面的子命令都会写主板：托盘程序在运行时不允许，免得两边同时控制
        using MicOwnerLock? owner = MicOwnerLock.TryAcquire();
        if (owner is null)
        {
            Console.WriteLine(MicOwnerLock.BusyMessage);
            return 3;
        }

        return sub switch
        {
            "color" => RunColor(values, apply),
            "count" when values.Length == 0 => RunCount(apply),
            "range" => RunRange(values),
            "flow" => RunFlow(values),
            "onboard" => RunOnboard(values),
            _ => PrintUsage($"看不懂的参数：{string.Join(" ", args)}"),
        };
    }

    private static int RunInfo()
    {
        string report = MotherboardLightDiagnostics.BuildReport();
        Console.Write(report);
        Console.WriteLine();
        ReportFile.SaveNextToExe("board-info", report);
        return 0;
    }

    private static int RunColor(string[] values, bool apply)
    {
        if (values.Length is < 1 or > 2
            || !Rgb.TryParseHex(values[0], out Rgb caseColor)
            || !TryParseOptional(values, 1, caseColor, out Rgb radiatorColor))
        {
            return PrintUsage("颜色要写成 FF0000 这种 6 位十六进制，最多两个（机箱、冷排）。");
        }

        Console.WriteLine($"机箱灯设成 {caseColor}，冷排灯设成 {radiatorColor}。");
        Rgb[] caseLeds = Enumerable.Repeat(caseColor, MotherboardLightProtocol.MaxLedsPerZone).ToArray();
        Rgb[] radiatorLeds = Enumerable.Repeat(radiatorColor, MotherboardLightProtocol.MaxLedsPerZone).ToArray();
        return SendOnceAndWait(caseLeds, radiatorLeds, apply);
    }

    private static int RunCount(bool apply)
    {
        Rgb[] leds = new Rgb[MotherboardLightProtocol.MaxLedsPerZone];
        for (int i = 0; i < leds.Length; i++)
        {
            leds[i] = countColors[i / countBandSize % countColors.Length].Color;
        }

        string order = string.Join(" → ", countColors.Select(entry => entry.Name));
        Console.WriteLine($"数灯珠：每 {countBandSize} 颗一段，按 {order} 循环（机箱和冷排相同）。");
        Console.WriteLine("请给每个风扇和冷排侧面灯条拍张清楚的照片，发给 Claude。");
        return SendOnceAndWait(leds, leds, apply);
    }

    private static int RunRange(string[] values)
    {
        int max = MotherboardLightProtocol.MaxLedsPerZone;
        if (values.Length != 2
            || !int.TryParse(values[0], out int first)
            || !int.TryParse(values[1], out int last)
            || first < 1 || last > max || first > last)
        {
            return PrintUsage($"range 后面要写两个数：从第几颗到第几颗（1～{max}，前一个不能比后一个大）。");
        }

        // 用户说的"第 1 颗"是列表里的第 0 个
        Rgb white = new(255, 255, 255);
        Rgb[] leds = Enumerable.Range(0, max)
            .Select(index => index >= first - 1 && index <= last - 1 ? white : default)
            .ToArray();

        Console.WriteLine($"冷排只点亮第 {first}～{last} 颗灯（白色），其他全黑。机箱灯不变。");
        Console.WriteLine("请看亮的是冷排的哪一部分（哪个风扇的哪几块、侧面灯条有没有亮）。");
        return SendOnceAndWait(null, leds, apply: false);
    }

    private static int RunOnboard(string[] values)
    {
        if (values.Length is < 1 or > 2
            || !Rgb.TryParseHex(values[0], out Rgb top)
            || !TryParseOptional(values, 1, top, out Rgb bottom))
        {
            return PrintUsage("onboard 后面要写一个或两个颜色，比如 board onboard FF0000。");
        }

        Console.WriteLine($"主板灯：I/O 挡板上方设成 {top}，下方设成 {bottom}。机箱灯和冷排灯不变。");
        Console.WriteLine("请看主板上哪里亮了、颜色对不对，风扇有没有被影响。");

        using var light = new MotherboardLight();
        var lights = new[] { (MotherboardOnboardLight.IoCoverTop, top), (MotherboardOnboardLight.IoCoverBottom, bottom) };
        if (light.SendOnboard(lights) != SendResult.Sent)
        {
            Console.WriteLine($"发送失败：{light.LastError ?? "原因未知"}");
            Console.WriteLine("请先退出托盘里的 HyperLight 和 OpenRGB，再运行一次。");
            return 1;
        }

        Console.WriteLine("已发送。看完按回车退出。");
        Console.ReadLine();
        return 0;
    }

    /// <summary>流动测试一共多久。</summary>
    private static readonly TimeSpan flowTestDuration = TimeSpan.FromSeconds(10);

    /// <summary>流动测试每帧间隔，和托盘程序的流动帧率一样（每秒约 20 帧）。</summary>
    private static readonly TimeSpan flowTestFrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>流动测试每个灯区发多少颗灯：风扇最多 16 颗，冷排能收到数据的不到 64 颗，64 足够。</summary>
    private const int flowTestLeds = 64;

    private static int RunFlow(string[] values)
    {
        if (values.Length != 2 || !Rgb.TryParseHex(values[0], out Rgb first) || !Rgb.TryParseHex(values[1], out Rgb second))
        {
            return PrintUsage("flow 后面要写两个颜色，比如 board flow FF0000 0000FF。");
        }

        // 流动速度和图案周期用设置的默认值，和托盘程序默认的样子一致
        var defaults = new AppConfig();
        Console.WriteLine($"机箱灯和冷排灯用 {first} 和 {second} 流动 {flowTestDuration.TotalSeconds:0} 秒"
            + $"（{defaults.FlowCycleMs} 毫秒转一圈，每 {defaults.FlowPeriodLeds} 颗灯重复一次）。");
        Console.WriteLine("请看风扇上两种颜色有没有在转圈。");

        using var light = new MotherboardLight();
        var clock = Stopwatch.StartNew();
        TimeSpan sendTime = TimeSpan.Zero;
        int frames = 0;
        while (clock.Elapsed < flowTestDuration)
        {
            TimeSpan frameStart = clock.Elapsed;
            double cyclePosition = frameStart.TotalMilliseconds / defaults.FlowCycleMs;
            Rgb[] leds = LightEffectRules.BuildFlow(first, second, flowTestLeds, defaults.FlowPeriodLeds, cyclePosition);
            foreach (MotherboardLightZone zone in Enum.GetValues<MotherboardLightZone>())
            {
                if (light.SendZone(zone, leds) != SendResult.Sent)
                {
                    Console.WriteLine($"发送失败：{light.LastError ?? "原因未知"}");
                    Console.WriteLine("请先退出托盘里的 HyperLight 和 OpenRGB，再运行一次。");
                    return 1;
                }
            }

            sendTime += clock.Elapsed - frameStart;
            frames++;
            TimeSpan wait = frameStart + flowTestFrameInterval - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                Thread.Sleep(wait);
            }
        }

        Console.WriteLine($"结束。一共发了 {frames} 帧，每帧发送平均花了 {sendTime.TotalMilliseconds / Math.Max(frames, 1):0.0} 毫秒。");
        Console.WriteLine("（灯会停在最后一帧的样子。按回车退出。）");
        Console.ReadLine();
        return 0;
    }

    /// <summary>
    /// 发一次颜色，然后等用户按回车再退出（期间不再发送）。
    /// <paramref name="caseLeds"/> 为 null 时不改机箱灯。
    /// </summary>
    private static int SendOnceAndWait(Rgb[]? caseLeds, Rgb[] radiatorLeds, bool apply)
    {
        using var light = new MotherboardLight();

        if (caseLeds is not null && light.SendZone(MotherboardLightZone.Case, caseLeds) != SendResult.Sent)
        {
            Console.WriteLine($"发送失败：{light.LastError ?? "原因未知"}");
            Console.WriteLine("请先完全退出 OpenRGB，再运行一次。");
            return 1;
        }

        SendResult radiatorResult = light.SendZone(MotherboardLightZone.Radiator, radiatorLeds);
        if (radiatorResult != SendResult.Sent)
        {
            Console.WriteLine($"冷排发送失败：{light.LastError ?? "原因未知"}");
            Console.WriteLine("请先完全退出 OpenRGB，再运行一次。");
            return 1;
        }

        if (apply && light.SendApply() != SendResult.Sent)
        {
            Console.WriteLine($"\"生效\"指令发送失败：{light.LastError ?? "原因未知"}");
            return 1;
        }

        Console.WriteLine(apply ? "已发送（包括\"生效\"指令）。" : "已发送（没有发\"生效\"指令）。");
        Console.WriteLine();
        Console.WriteLine("现在程序已经停止发送。请看：");
        Console.WriteLine("  1. 灯有没有变成要的颜色？");
        Console.WriteLine("  2. 等 10 秒左右，颜色能不能一直保持？");
        Console.WriteLine("看完按回车退出。");
        Console.ReadLine();
        return 0;
    }

    /// <summary>读可选的第 index 个颜色；没写就用 <paramref name="fallback"/>。写了但格式不对返回 false。</summary>
    private static bool TryParseOptional(string[] values, int index, Rgb fallback, out Rgb color)
    {
        if (values.Length <= index)
        {
            color = fallback;
            return true;
        }

        return Rgb.TryParseHex(values[index], out color);
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
}

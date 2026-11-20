using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HyperLight.Device;

namespace HyperLight;

/// <summary>
/// color 命令（M1 测试用）：不停地给麦克风发一个固定颜色，直到到达指定秒数或按 Ctrl+C。
/// 用法：HyperLight.exe color &lt;上灯区颜色&gt; [下灯区颜色] [--interval 毫秒] [--seconds 秒数]
/// </summary>
internal static class ColorCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  color <上灯区颜色> [下灯区颜色] [--interval 毫秒] [--seconds 秒数]",
        "         不停地给麦克风发固定颜色。颜色写成 FF0000 这种 6 位十六进制；只写一个颜色时上下灯区相同",
        $"         --interval  每帧间隔，{minIntervalMs}～{maxIntervalMs} 毫秒，默认 {QuadcastProtocol.ReferenceFrameIntervalMs}（参考项目的值）",
        "         --seconds   发多少秒后自动停止；不写就一直发，按 Ctrl+C 停止",
        "         例子：HyperLight.exe color FF0000 0000FF --seconds 15",
    };

    private const int minIntervalMs = 10;
    private const int maxIntervalMs = 1000;
    private const int maxSeconds = 24 * 60 * 60;

    /// <summary>每隔多久在窗口里报告一次实际帧率。</summary>
    private static readonly TimeSpan statusInterval = TimeSpan.FromSeconds(5);

    /// <summary>每隔多久检查一次 NGENUITY 有没有在运行（查进程列表有开销，不必每帧都查）。</summary>
    private static readonly TimeSpan ngenuityCheckInterval = TimeSpan.FromSeconds(3);

    private sealed record Options(Rgb Upper, Rgb Lower, int IntervalMs, int? Seconds);

    /// <summary>执行 color 命令。args 是 "color" 后面的参数。</summary>
    public static int Run(string[] args)
    {
        if (!TryParse(args, out Options? options, out string error))
        {
            Console.WriteLine(error);
            Console.WriteLine();
            foreach (string line in UsageLines)
            {
                Console.WriteLine(line);
            }

            return 2;
        }

        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCtrlC = (_, e) =>
        {
            // 不让系统直接杀掉程序：先停止循环、关闭设备，再正常退出
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

        Console.CancelKeyPress += onCtrlC;
        try
        {
            SendLoop(options, cancel.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCtrlC;
        }

        return 0;
    }

    // ───────────────────────── 参数解析 ─────────────────────────

    private static bool TryParse(string[] args, [NotNullWhen(true)] out Options? options, out string error)
    {
        options = null;
        var colors = new List<Rgb>();
        int intervalMs = QuadcastProtocol.ReferenceFrameIntervalMs;
        int? seconds = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--interval", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadNumber(args, ref i, minIntervalMs, maxIntervalMs, out intervalMs))
                {
                    error = $"--interval 后面要跟 {minIntervalMs}～{maxIntervalMs} 之间的整数（毫秒）";
                    return false;
                }
            }
            else if (arg.Equals("--seconds", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryReadNumber(args, ref i, 1, maxSeconds, out int value))
                {
                    error = $"--seconds 后面要跟 1～{maxSeconds} 之间的整数（秒）";
                    return false;
                }

                seconds = value;
            }
            else if (Rgb.TryParseHex(arg, out Rgb color))
            {
                if (colors.Count == 2)
                {
                    error = "最多只能写两个颜色（上灯区、下灯区）";
                    return false;
                }

                colors.Add(color);
            }
            else
            {
                error = $"看不懂的参数：{arg}（颜色要写成 FF0000 这种 6 位十六进制）";
                return false;
            }
        }

        if (colors.Count == 0)
        {
            error = "请写上颜色，比如：HyperLight.exe color FF0000";
            return false;
        }

        Rgb upper = colors[0];
        Rgb lower = colors.Count == 2 ? colors[1] : upper;
        options = new Options(upper, lower, intervalMs, seconds);
        error = "";
        return true;
    }

    /// <summary>读取选项后面跟着的整数，并检查范围。成功时 index 会跳过这个数。</summary>
    private static bool TryReadNumber(string[] args, ref int index, int min, int max, out int value)
    {
        value = 0;
        if (index + 1 >= args.Length)
        {
            return false;
        }

        index++;
        return int.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= min
            && value <= max;
    }

    // ───────────────────────── 发送循环 ─────────────────────────

    private static void SendLoop(Options options, CancellationToken cancel)
    {
        PrintStart(options);

        var clock = Stopwatch.StartNew();
        TimeSpan? duration = options.Seconds is int s ? TimeSpan.FromSeconds(s) : null;

        string? lastMessage = null;
        bool? ngenuityWasRunning = null;
        TimeSpan nextNgenuityCheck = TimeSpan.Zero;

        var total = new FrameStats();
        var window = new FrameStats();
        TimeSpan windowStart = TimeSpan.Zero;

        long nextFrameMs = 0;

        using (var mic = new QuadcastMic())
        {
            while (!cancel.IsCancellationRequested && (duration is null || clock.Elapsed < duration))
            {
                // 1. 每隔几秒检查一次 NGENUITY，状态变化时提示
                if (clock.Elapsed >= nextNgenuityCheck)
                {
                    nextNgenuityCheck = clock.Elapsed + ngenuityCheckInterval;
                    bool running = NgenuityProcess.IsRunning();
                    if (running != ngenuityWasRunning)
                    {
                        PrintLine(clock, running
                            ? "警告：检测到 NGENUITY 正在运行。两个程序会同时发数据，灯可能乱闪，请从托盘区退出 NGENUITY。"
                            : "NGENUITY 没有在运行。");
                        ngenuityWasRunning = running;
                    }
                }

                // 2. 发一帧，并记录这一帧花了多久
                TimeSpan sendStart = clock.Elapsed;
                SendResult result = mic.SendFrame(options.Upper, options.Lower);
                TimeSpan sendTime = clock.Elapsed - sendStart;
                total.Add(result, sendTime);
                window.Add(result, sendTime);

                // 3. 连接状态变化时提示（同样的消息不重复刷屏）
                string message = result switch
                {
                    SendResult.Sent => $"已连接麦克风，正在发送颜色。设备：{mic.DevicePath}",
                    SendResult.NotConnected => $"等待麦克风：{mic.LastError}（每秒自动重试）",
                    _ => $"麦克风断开：{mic.LastError}（会自动重连）",
                };
                if (message != lastMessage)
                {
                    PrintLine(clock, message);
                    lastMessage = message;
                }

                // 4. 每隔几秒报告一次实际帧率
                if (clock.Elapsed - windowStart >= statusInterval)
                {
                    PrintLine(clock, $"最近 {statusInterval.TotalSeconds:0} 秒：{window.Describe(clock.Elapsed - windowStart)}");
                    window = new FrameStats();
                    windowStart = clock.Elapsed;
                }

                // 5. 等到下一帧的时间
                nextFrameMs += options.IntervalMs;
                long waitMs = nextFrameMs - clock.ElapsedMilliseconds;
                if (waitMs > 0)
                {
                    cancel.WaitHandle.WaitOne((int)waitMs);
                }
                else
                {
                    // 落后了（比如发送很慢）：不补发，从现在重新计时
                    nextFrameMs = clock.ElapsedMilliseconds;
                }
            }
        }

        string reason = cancel.IsCancellationRequested ? "按了 Ctrl+C" : $"已到 {options.Seconds} 秒";
        Console.WriteLine();
        PrintLine(clock, $"已停止发送（{reason}）。");
        Console.WriteLine($"总计：{total.Describe(clock.Elapsed)}");
        Console.WriteLine();
        if (total.Sent == 0)
        {
            Console.WriteLine("这次一帧都没有发出去，麦克风不会有变化。请检查 USB 线，或者先运行 HyperLight.exe list 看看能不能找到麦克风。");
            return;
        }

        Console.WriteLine("请观察麦克风的灯接下来怎么变化：");
        Console.WriteLine("  保持刚才的颜色？回到原来的灯效？还是熄灭？大概过了几秒才变？把结果告诉 Claude。");
    }

    private static void PrintStart(Options options)
    {
        double targetFps = 1000.0 / options.IntervalMs;
        Console.WriteLine("HyperLight 固定颜色测试（M1）");
        Console.WriteLine($"上灯区：{options.Upper}    下灯区：{options.Lower}");
        Console.WriteLine($"发送间隔：{options.IntervalMs} 毫秒（目标每秒 {targetFps:0.0} 帧）");
        Console.WriteLine(options.Seconds is int s
            ? $"持续时间：{s} 秒后自动停止（也可以按 Ctrl+C 提前停止）"
            : "持续时间：一直发送，按 Ctrl+C 停止");
        Console.WriteLine();
    }

    private static void PrintLine(Stopwatch clock, string message) =>
        Console.WriteLine($"[{clock.Elapsed:mm\\:ss}] {message}");

    /// <summary>一段时间内的发送统计。</summary>
    private sealed class FrameStats
    {
        private int failed;
        private TimeSpan sendTimeTotal;

        /// <summary>成功发送的帧数。</summary>
        public int Sent { get; private set; }

        public void Add(SendResult result, TimeSpan sendTime)
        {
            if (result == SendResult.Sent)
            {
                Sent++;
                sendTimeTotal += sendTime;
            }
            else if (result == SendResult.Failed)
            {
                failed++;
            }
        }

        public string Describe(TimeSpan elapsed)
        {
            double fps = elapsed.TotalSeconds > 0 ? Sent / elapsed.TotalSeconds : 0;
            string avgSend = Sent > 0 ? $"{sendTimeTotal.TotalMilliseconds / Sent:0.0} 毫秒" : "—";
            return $"成功 {Sent} 帧（每秒 {fps:0.0} 帧），失败 {failed} 次，每帧发送平均耗时 {avgSend}";
        }
    }
}

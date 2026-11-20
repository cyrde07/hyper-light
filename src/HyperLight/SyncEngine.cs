using System.Diagnostics;
using HyperLight.Capture;
using HyperLight.ColorProcessing;
using HyperLight.Config;
using HyperLight.Device;

namespace HyperLight;

/// <summary>同步引擎的状态快照，给界面显示用。快照本身不会变，状态变了就换一个新的快照。</summary>
internal sealed record SyncStatus
{
    /// <summary>壁纸这一侧的状态（找到了 / 找不到）。</summary>
    public string WallpaperState { get; init; } = "正在寻找 Wallpaper Engine 的壁纸窗口…";

    /// <summary>最近一次截到的平均色。</summary>
    public Rgb? CapturedColor { get; init; }

    /// <summary>经过变化过滤后的目标颜色（麦克风最终显示的颜色）。null 表示没有颜色，不发送。</summary>
    public Rgb? TargetColor { get; init; }

    /// <summary>目标颜色一共变了几次。</summary>
    public int ColorChanges { get; init; }

    /// <summary>最近一次截图 + 算颜色花了多少毫秒。</summary>
    public double LastCaptureMs { get; init; }

    /// <summary>麦克风这一侧的状态（已连接 / 等待 / 断开）。</summary>
    public string MicState { get; init; } = "等待第一次截图…";

    /// <summary>一共成功发送了多少帧。</summary>
    public long FramesSent { get; init; }

    /// <summary>NGENUITY 是否在运行。</summary>
    public bool NgenuityRunning { get; init; }

    /// <summary>最近一次意外错误。恢复正常后清空。</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// 同步引擎：截壁纸 → 算平均色 → 变化过滤 → 持续发给麦克风。
/// 两个线程：
///   截图线程：每隔 CaptureIntervalMs 截一次壁纸，算平均色，交给 <see cref="ColorChangeFilter"/>。
///   发送线程：每隔 SendIntervalMs 把"此刻应该显示的颜色"发给麦克风。
///             麦克风一停止接收就会恢复自带灯效，所以这个线程不能被截图卡住，两件事必须分开。
/// 找不到壁纸超过 WallpaperLostGraceMs，就停止发送，让麦克风回到自带灯效。
/// M4 的托盘程序也直接使用它。
/// </summary>
internal sealed class SyncEngine : IDisposable
{
    private static readonly TimeSpan ngenuityCheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>停止时最多等两个线程收尾多久。</summary>
    private static readonly TimeSpan stopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppConfig config;
    private readonly ColorChangeFilter filter;
    private readonly Stopwatch clock = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly object statusGate = new();

    private SyncStatus status = new();
    private Thread? captureThread;
    private Thread? sendThread;
    private bool disposed;

    public SyncEngine(AppConfig config)
    {
        this.config = config;
        filter = new ColorChangeFilter(config.ChangeThreshold, TimeSpan.FromMilliseconds(config.FadeMs));
    }

    /// <summary>当前状态快照。任何线程都可以读。</summary>
    public SyncStatus Status
    {
        get
        {
            lock (statusGate)
            {
                return status;
            }
        }
    }

    /// <summary>启动两个线程。只能调用一次。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (captureThread is not null)
        {
            throw new InvalidOperationException("同步引擎已经启动过了");
        }

        clock.Start();
        captureThread = StartBackgroundThread(CaptureLoop, "HyperLight 截图线程");
        sendThread = StartBackgroundThread(SendLoop, "HyperLight 发送线程");
    }

    /// <summary>停止同步。两个线程会各自关闭麦克风连接、释放截图内存。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stopping.Cancel();

        // 万一截图线程被卡住（目标窗口不响应），最多等几秒就不等了：
        // 它是后台线程，程序退出时会被系统回收。这种情况下不能释放 stopping，线程还在用它。
        bool captureStopped = captureThread?.Join(stopTimeout) ?? true;
        bool sendStopped = sendThread?.Join(stopTimeout) ?? true;
        if (captureStopped && sendStopped)
        {
            stopping.Dispose();
        }
    }

    // ───────────────────────── 截图线程 ─────────────────────────

    private void CaptureLoop()
    {
        var interval = TimeSpan.FromMilliseconds(config.CaptureIntervalMs);
        var lostGrace = TimeSpan.FromMilliseconds(config.WallpaperLostGraceMs);
        using var capturer = new WallpaperCapturer();
        WallpaperTarget? target = null;
        TimeSpan lastSuccess = TimeSpan.Zero;
        TimeSpan nextNgenuityCheck = TimeSpan.Zero;

        while (!stopping.IsCancellationRequested)
        {
            TimeSpan roundStart = clock.Elapsed;
            try
            {
                if (roundStart >= nextNgenuityCheck)
                {
                    nextNgenuityCheck = roundStart + ngenuityCheckInterval;
                    bool running = NgenuityProcess.IsRunning();
                    UpdateStatus(s => s with { NgenuityRunning = running });
                }

                // 之前找到的窗口不能用了（换壁纸、换分辨率等），就重新找
                if (target is not WallpaperTarget known || !WallpaperWindowFinder.IsStillValid(known))
                {
                    target = WallpaperWindowFinder.Find();
                }

                TimeSpan captureStart = clock.Elapsed;
                if (target is WallpaperTarget found && capturer.TryCapture(found))
                {
                    Rgb average = ColorAverager.Average(capturer.Pixels, capturer.Width, capturer.Height);
                    bool changed = filter.Offer(average, clock.Elapsed);
                    double captureMs = (clock.Elapsed - captureStart).TotalMilliseconds;
                    lastSuccess = clock.Elapsed;
                    UpdateStatus(s => s with
                    {
                        WallpaperState = "已找到壁纸，正在同步",
                        CapturedColor = average,
                        TargetColor = filter.Target,
                        ColorChanges = s.ColorChanges + (changed ? 1 : 0),
                        LastCaptureMs = captureMs,
                        LastError = null,
                    });
                }
                else
                {
                    target = null;
                    HandleWallpaperMissing(lastSuccess, lostGrace);
                }
            }
            catch (Exception ex)
            {
                // 线程最外层：任何意外都不能让程序崩溃。记下原因，下一轮重新找窗口再试
                target = null;
                UpdateStatus(s => s with { LastError = $"截图出错：{ex.GetType().Name}：{ex.Message}" });
            }

            WaitUntil(roundStart + interval);
        }
    }

    /// <summary>找不到壁纸或截图失败：宽限期内继续发上一次的颜色，超过宽限期就停止发送。</summary>
    private void HandleWallpaperMissing(TimeSpan lastSuccess, TimeSpan lostGrace)
    {
        if (clock.Elapsed - lastSuccess >= lostGrace)
        {
            filter.Clear();
        }

        string state = filter.Target is null
            ? "找不到 Wallpaper Engine 的壁纸窗口（没开？），麦克风显示自带灯效"
            : "暂时找不到壁纸窗口，继续发送上一次的颜色";
        UpdateStatus(s => s with { WallpaperState = state, TargetColor = filter.Target });
    }

    // ───────────────────────── 发送线程 ─────────────────────────

    private void SendLoop()
    {
        int intervalMs = config.SendIntervalMs;
        using var mic = new QuadcastMic();
        long nextFrameMs = clock.ElapsedMilliseconds;

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (filter.Current(clock.Elapsed) is Rgb color)
                {
                    // M2 上下两个灯区用同一个颜色；分区取色是 M3 的工作
                    SendResult result = mic.SendFrame(color, color);
                    string micState = result switch
                    {
                        SendResult.Sent => "麦克风已连接，正在发送颜色",
                        SendResult.NotConnected => $"等待麦克风：{mic.LastError}（每秒自动重试）",
                        _ => $"麦克风断开：{mic.LastError}（会自动重连）",
                    };
                    long sent = result == SendResult.Sent ? 1 : 0;
                    UpdateStatus(s => s with { MicState = micState, FramesSent = s.FramesSent + sent });
                }
                else
                {
                    UpdateStatus(s => s with { MicState = "没有颜色可发，麦克风显示自带灯效" });
                }
            }
            catch (Exception ex)
            {
                // 线程最外层：任何意外都不能让程序崩溃。记下原因，下一帧继续
                UpdateStatus(s => s with { LastError = $"发送出错：{ex.GetType().Name}：{ex.Message}" });
            }

            nextFrameMs += intervalMs;
            long waitMs = nextFrameMs - clock.ElapsedMilliseconds;
            if (waitMs > 0)
            {
                stopping.Token.WaitHandle.WaitOne((int)waitMs);
            }
            else
            {
                // 落后了（比如发送很慢）：不补发，从现在重新计时
                nextFrameMs = clock.ElapsedMilliseconds;
            }
        }
    }

    // ───────────────────────── 小工具 ─────────────────────────

    private void UpdateStatus(Func<SyncStatus, SyncStatus> change)
    {
        lock (statusGate)
        {
            status = change(status);
        }
    }

    private void WaitUntil(TimeSpan due)
    {
        TimeSpan remaining = due - clock.Elapsed;
        if (remaining > TimeSpan.Zero)
        {
            stopping.Token.WaitHandle.WaitOne(remaining);
        }
    }

    private static Thread StartBackgroundThread(Action body, string name)
    {
        var thread = new Thread(() => body()) { IsBackground = true, Name = name };
        thread.Start();
        return thread;
    }
}

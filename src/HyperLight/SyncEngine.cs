using System.Diagnostics;
using HyperLight.Capture;
using HyperLight.ColorProcessing;
using HyperLight.Config;
using HyperLight.Device;

namespace HyperLight;

/// <summary>同步引擎的状态快照，给界面显示用。快照本身不会变，状态变了就换一个新的快照。</summary>
internal sealed record SyncStatus
{
    /// <summary>壁纸这一侧的状态（找到了 / 找不到 / 已暂停）。</summary>
    public string WallpaperState { get; init; } = "正在寻找 Wallpaper Engine 的壁纸窗口…";

    /// <summary>最近一次截到的两个灯区的颜色（已经过增强）。</summary>
    public ZoneColors? Captured { get; init; }

    /// <summary>经过变化过滤后的目标颜色（麦克风最终显示的颜色）。null 表示没有颜色，不发送。</summary>
    public ZoneColors? Target { get; init; }

    /// <summary>目标颜色一共变了几次。</summary>
    public int ColorChanges { get; init; }

    /// <summary>最近一次截图 + 算颜色花了多少毫秒。</summary>
    public double LastCaptureMs { get; init; }

    /// <summary>麦克风这一侧的状态（已连接 / 等待 / 断开 / 已暂停）。</summary>
    public string MicState { get; init; } = "等待第一次截图…";

    /// <summary>最近一次发送是否成功（麦克风已连上）。</summary>
    public bool MicConnected { get; init; }

    /// <summary>一共成功发送了多少帧。</summary>
    public long FramesSent { get; init; }

    /// <summary>NGENUITY 是否在运行。</summary>
    public bool NgenuityRunning { get; init; }

    /// <summary>是否已暂停。</summary>
    public bool Paused { get; init; }

    /// <summary>最近一次意外错误。恢复正常后清空。</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// 同步引擎：截壁纸 → 算两个灯区的平均色 → 增强颜色 → 变化过滤 → 持续发给麦克风。
/// 两个线程：
///   截图线程：每隔 CaptureIntervalMs 截一次壁纸，算颜色，交给两个 <see cref="ColorChangeFilter"/>（上、下灯区各一个）。
///   发送线程：每隔 SendIntervalMs 把"此刻应该显示的颜色"发给麦克风。
///             麦克风一停止接收就会恢复自带灯效，所以这个线程不能被截图卡住，两件事必须分开。
/// 找不到壁纸超过 WallpaperLostGraceMs，或者暂停时，停止发送，让麦克风回到自带灯效。
/// sync 命令和托盘程序都使用它。
/// </summary>
internal sealed class SyncEngine : IDisposable
{
    private static readonly TimeSpan ngenuityCheckInterval = TimeSpan.FromSeconds(3);

    /// <summary>停止时最多等两个线程收尾多久。</summary>
    private static readonly TimeSpan stopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppConfig config;
    private readonly ColorChangeFilter upperFilter;
    private readonly ColorChangeFilter lowerFilter;
    private readonly Stopwatch clock = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly object statusGate = new();

    private SyncStatus status = new();
    private Thread? captureThread;
    private Thread? sendThread;
    private volatile bool paused;
    private bool disposed;

    public SyncEngine(AppConfig config)
    {
        this.config = config;
        var fade = TimeSpan.FromMilliseconds(config.FadeMs);
        upperFilter = new ColorChangeFilter(config.ChangeThreshold, fade);
        lowerFilter = new ColorChangeFilter(config.ChangeThreshold, fade);
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

    /// <summary>
    /// 暂停 / 恢复。暂停时不截图、不发送，麦克风马上回到自带灯效；恢复后接着同步。任何线程都可以设置。
    /// </summary>
    public bool Paused
    {
        get => paused;
        set
        {
            paused = value;
            UpdateStatus(s => s with { Paused = value });
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

                if (paused)
                {
                    // 暂停时不截图；把"上次成功"挪到现在，免得恢复的瞬间被当成"很久没找到壁纸"
                    lastSuccess = clock.Elapsed;
                    UpdateStatus(s => s with { WallpaperState = "已暂停" });
                }
                else
                {
                    target = CaptureOnce(capturer, target, ref lastSuccess, lostGrace);
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

    /// <summary>截一次壁纸并更新颜色。返回这次用的壁纸窗口（截图失败时返回 null，下一轮重新找）。</summary>
    private WallpaperTarget? CaptureOnce(WallpaperCapturer capturer, WallpaperTarget? target, ref TimeSpan lastSuccess, TimeSpan lostGrace)
    {
        // 之前找到的窗口不能用了（换壁纸、换分辨率等），就重新找
        if (target is not WallpaperTarget known || !WallpaperWindowFinder.IsStillValid(known))
        {
            target = WallpaperWindowFinder.Find();
        }

        TimeSpan captureStart = clock.Elapsed;
        if (target is not WallpaperTarget found || !capturer.TryCapture(found))
        {
            HandleWallpaperMissing(lastSuccess, lostGrace);
            return null;
        }

        ZoneColors colors = Enhance(ColorAverager.AverageZones(capturer.Pixels, capturer.Width, capturer.Height, config.SplitZones));
        TimeSpan now = clock.Elapsed;
        bool upperChanged = upperFilter.Offer(colors.Upper, now);
        bool lowerChanged = lowerFilter.Offer(colors.Lower, now);
        double captureMs = (clock.Elapsed - captureStart).TotalMilliseconds;
        lastSuccess = now;

        ZoneColors? targetColors = CurrentTarget();
        UpdateStatus(s => s with
        {
            WallpaperState = "已找到壁纸，正在同步",
            Captured = colors,
            Target = targetColors,
            ColorChanges = s.ColorChanges + (upperChanged || lowerChanged ? 1 : 0),
            LastCaptureMs = captureMs,
            LastError = null,
        });
        return found;
    }

    /// <summary>增强颜色打开时，两个灯区都提高饱和度。</summary>
    private ZoneColors Enhance(ZoneColors colors) =>
        config.BoostColors
            ? new ZoneColors(
                ColorEnhancer.BoostSaturation(colors.Upper, config.SaturationBoost),
                ColorEnhancer.BoostSaturation(colors.Lower, config.SaturationBoost))
            : colors;

    /// <summary>找不到壁纸或截图失败：宽限期内继续发上一次的颜色，超过宽限期就停止发送。</summary>
    private void HandleWallpaperMissing(TimeSpan lastSuccess, TimeSpan lostGrace)
    {
        if (clock.Elapsed - lastSuccess >= lostGrace)
        {
            upperFilter.Clear();
            lowerFilter.Clear();
        }

        ZoneColors? targetColors = CurrentTarget();
        string state = targetColors is null
            ? "找不到 Wallpaper Engine 的壁纸窗口（没开？），麦克风显示自带灯效"
            : "暂时找不到壁纸窗口，继续发送上一次的颜色";
        UpdateStatus(s => s with { WallpaperState = state, Target = targetColors });
    }

    /// <summary>两个灯区的目标颜色。任何一个没有颜色就返回 null（两个过滤器总是一起设置、一起清空）。</summary>
    private ZoneColors? CurrentTarget() =>
        upperFilter.Target is Rgb upper && lowerFilter.Target is Rgb lower ? new ZoneColors(upper, lower) : null;

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
                SendOnce(mic);
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

    private void SendOnce(QuadcastMic mic)
    {
        if (paused)
        {
            UpdateStatus(s => s with { MicState = "已暂停，麦克风显示自带灯效" });
            return;
        }

        TimeSpan now = clock.Elapsed;
        if (upperFilter.Current(now) is not Rgb upper || lowerFilter.Current(now) is not Rgb lower)
        {
            UpdateStatus(s => s with { MicState = "没有颜色可发，麦克风显示自带灯效" });
            return;
        }

        SendResult result = mic.SendFrame(upper, lower);
        string micState = result switch
        {
            SendResult.Sent => "麦克风已连接，正在发送颜色",
            SendResult.NotConnected => $"等待麦克风：{mic.LastError}（每秒自动重试）",
            _ => $"麦克风断开：{mic.LastError}（会自动重连）",
        };
        bool connected = result == SendResult.Sent;
        UpdateStatus(s => s with { MicState = micState, MicConnected = connected, FramesSent = s.FramesSent + (connected ? 1 : 0) });
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

using System.Diagnostics;
using HyperLight.Capture;
using HyperLight.ColorProcessing;
using HyperLight.Config;
using HyperLight.Device;
using HyperLight.Diagnostics;

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

    /// <summary>主板灯（机箱灯、冷排灯）这一侧的状态。</summary>
    public string BoardState { get; init; } = "等待第一次截图…";

    /// <summary>最近一次给主板发送是否成功（主板灯光芯片已连上）。</summary>
    public bool BoardConnected { get; init; }

    /// <summary>OpenRGB 是否在运行。</summary>
    public bool OpenRgbRunning { get; init; }

    /// <summary>是否已暂停。</summary>
    public bool Paused { get; init; }

    /// <summary>最近一次意外错误。恢复正常后清空。</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// 同步引擎：截壁纸 → 算两个灯区的平均色 → 增强颜色 → 变化过滤 → 发给麦克风和主板灯。
/// 三个线程：
///   截图线程：每隔 CaptureIntervalMs 截一次壁纸，算出上、下、左、右、整张五块区域的颜色，
///             每块区域交给自己的 <see cref="ColorChangeFilter"/>。麦克风按取色方式用其中两块，主板灯按取色来源或灯效用。
///   发送线程：每隔 SendIntervalMs 把"此刻应该显示的颜色"发给麦克风。
///             麦克风一停止接收就会恢复自带灯效，所以这个线程不能被截图卡住，两件事必须分开。
///   主板线程：单色灯效只在颜色变了时才发给主板（M5-1 实测主板停止接收后会保持颜色，不用一直发）；
///             流动灯效要一直发（每秒约 20 次），图案才会转。
///             主板每次要发很多个包（比麦克风慢），单独一个线程，不会拖慢麦克风。
/// 找不到壁纸超过 WallpaperLostGraceMs，或者暂停时，麦克风停止发送、回到自带灯效；主板灯保持最后的颜色。
/// sync 命令和托盘程序都使用它。
/// </summary>
internal sealed class SyncEngine : IDisposable
{
    /// <summary>多久查一次 NGENUITY 和 OpenRGB。查进程列表会产生不少临时数据，没必要太频繁。</summary>
    private static readonly TimeSpan rivalCheckInterval = TimeSpan.FromSeconds(10);

    /// <summary>截图或发送耽误超过这个时间，就记进卡顿记录。</summary>
    private static readonly TimeSpan stallThreshold = TimeSpan.FromMilliseconds(300);

    /// <summary>颜色变化超过这个值（0～255），就当作"突变"记下来（多半是换了壁纸）。</summary>
    private const int bigColorJump = 60;

    /// <summary>流动灯效多久发一帧（每秒约 20 帧，转起来够流畅，USB 负担也不大）。</summary>
    private static readonly TimeSpan flowFrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>停止时最多等每个线程收尾多久。</summary>
    private static readonly TimeSpan stopTimeout = TimeSpan.FromSeconds(5);

    private readonly AppConfig config;
    /// <summary>每块区域一个颜色过滤器。截图线程一起设置、一起清空。</summary>
    private readonly Dictionary<ColorSource, ColorChangeFilter> filters = new();

    /// <summary>麦克风上、下灯区分别用哪块区域（由取色方式决定）。</summary>
    private readonly (ColorSource Upper, ColorSource Lower) micRegions;
    private readonly Stopwatch clock = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly object statusGate = new();

    private SyncStatus status = new();
    private Thread? captureThread;
    private Thread? sendThread;
    private Thread? boardThread;
    private volatile bool paused;
    private bool disposed;

    // 以下只给卡顿记录用：记住上次记下的状态，同样的情况不重复写
    private IntPtr loggedWallpaperWindow;   // 只在截图线程里用
    private bool loggedWallpaperMissing;    // 只在截图线程里用
    private bool micConnectedBefore;        // 只在发送线程里用
    private bool boardConnectedBefore;      // 只在主板线程里用

    public SyncEngine(AppConfig config)
    {
        this.config = config;
        var fade = TimeSpan.FromMilliseconds(config.FadeMs);
        foreach (ColorSource region in Enum.GetValues<ColorSource>())
        {
            filters[region] = new ColorChangeFilter(config.ChangeThreshold, fade, config.ConfirmCaptures);
        }

        micRegions = ZoneLayoutRegions.Of(config.ZoneLayout);
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
    /// 暂停 / 恢复。暂停时不截图、不发送：麦克风马上回到自带灯效，主板灯保持当前颜色；恢复后接着同步。任何线程都可以设置。
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

    /// <summary>启动三个线程。只能调用一次。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (captureThread is not null)
        {
            throw new InvalidOperationException("同步引擎已经启动过了");
        }

        clock.Start();
        StallLog.Write("启动", $"开始同步（截图间隔 {config.CaptureIntervalMs} 毫秒，取色方式 {ZoneLayoutText.Of(config.ZoneLayout)}，{DescribeDevices()}）");
        captureThread = StartBackgroundThread(CaptureLoop, "HyperLight 截图线程");
        sendThread = StartBackgroundThread(SendLoop, "HyperLight 发送线程");
        boardThread = StartBackgroundThread(BoardLoop, "HyperLight 主板线程");
    }

    /// <summary>停止同步。各个线程会各自关闭麦克风和主板的连接、释放截图内存。主板灯保持最后的颜色。</summary>
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
        bool boardStopped = boardThread?.Join(stopTimeout) ?? true;
        if (captureStopped && sendStopped && boardStopped)
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
        TimeSpan nextRivalCheck = TimeSpan.Zero;

        while (!stopping.IsCancellationRequested)
        {
            TimeSpan roundStart = clock.Elapsed;
            try
            {
                if (paused)
                {
                    // 暂停时什么都不做（不截图、不查 NGENUITY 和 OpenRGB）；
                    // 把"上次成功"挪到现在，免得恢复的瞬间被当成"很久没找到壁纸"
                    lastSuccess = clock.Elapsed;
                    UpdateStatus(s => s with { WallpaperState = "已暂停" });
                }
                else
                {
                    if (roundStart >= nextRivalCheck)
                    {
                        nextRivalCheck = roundStart + rivalCheckInterval;
                        bool ngenuity = config.MicEnabled && NgenuityProcess.IsRunning();
                        bool openRgb = OpenRgbProcess.IsRunning();
                        UpdateStatus(s => s with { NgenuityRunning = ngenuity, OpenRgbRunning = openRgb });
                    }

                    TimeSpan captureRoundStart = clock.Elapsed;
                    target = CaptureOnce(capturer, target, ref lastSuccess, lostGrace);
                    TimeSpan took = clock.Elapsed - captureRoundStart;
                    if (took > stallThreshold)
                    {
                        StallLog.Write("截图", $"这次截图很慢：{took.TotalMilliseconds:0} 毫秒");
                    }
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
            LogWallpaperWindow(target);
        }

        TimeSpan captureStart = clock.Elapsed;
        WallpaperColors colors;
        try
        {
            if (target is not WallpaperTarget found || !capturer.TryCapture(found))
            {
                if (target is not null && !loggedWallpaperMissing)
                {
                    StallLog.Write("壁纸", "找到了壁纸窗口，但截图失败");
                    loggedWallpaperMissing = true;
                }

                HandleWallpaperMissing(lastSuccess, lostGrace);
                return null;
            }

            colors = Enhance(ColorAverager.AverageRegions(capturer.Pixels, capturer.Width, capturer.Height));
            target = found;
        }
        finally
        {
            // 读完像素马上释放 30 多 MB 的截图内存，两次截图之间不占着
            capturer.ReleaseBuffer();
        }

        TimeSpan now = clock.Elapsed;
        ZoneColors? before = CurrentTarget();
        var micColors = new ZoneColors(colors.Get(micRegions.Upper), colors.Get(micRegions.Lower));

        // 每块区域都要交给自己的过滤器（不能因为前面的变了就跳过后面的）
        bool micChanged = false;
        foreach ((ColorSource region, ColorChangeFilter filter) in filters)
        {
            bool changed = filter.Offer(colors.Get(region), now);
            if (region == micRegions.Upper || region == micRegions.Lower)
            {
                micChanged |= changed;
            }
        }

        if (before is ZoneColors old && ZoneDifference(old, micColors) >= bigColorJump)
        {
            StallLog.Write("颜色", micChanged
                ? $"颜色突变：{old} → {micColors}（已切换；多半是换了壁纸）"
                : $"颜色突变：{old} → {micColors}（先不切换，等下次截图确认；一闪而过的过渡画面会被忽略）");
        }
        double captureMs = (clock.Elapsed - captureStart).TotalMilliseconds;
        lastSuccess = now;

        ZoneColors? targetColors = CurrentTarget();
        UpdateStatus(s => s with
        {
            WallpaperState = "已找到壁纸，正在同步",
            Captured = micColors,
            Target = targetColors,
            ColorChanges = s.ColorChanges + (micChanged ? 1 : 0),
            LastCaptureMs = captureMs,
            LastError = null,
        });
        return target;
    }

    /// <summary>
    /// 卡顿记录：壁纸窗口找不到了，或者换成了另一个窗口（Wallpaper Engine 换壁纸、重新加载时会这样）。
    /// 同样的情况只记一次。
    /// </summary>
    private void LogWallpaperWindow(WallpaperTarget? target)
    {
        if (target is not WallpaperTarget found)
        {
            if (!loggedWallpaperMissing)
            {
                StallLog.Write("壁纸", "找不到壁纸窗口（Wallpaper Engine 没开、正在换壁纸或正在重新加载）");
                loggedWallpaperMissing = true;
            }

            return;
        }

        if (found.WallpaperWindow != loggedWallpaperWindow || loggedWallpaperMissing)
        {
            StallLog.Write("壁纸", loggedWallpaperWindow == IntPtr.Zero
                ? $"找到壁纸窗口 0x{found.WallpaperWindow.ToInt64():X}"
                : $"壁纸窗口变了：0x{loggedWallpaperWindow.ToInt64():X} → 0x{found.WallpaperWindow.ToInt64():X}（Wallpaper Engine 可能换了壁纸或重新加载）");
            loggedWallpaperWindow = found.WallpaperWindow;
            loggedWallpaperMissing = false;
        }
    }

    /// <summary>两组灯区颜色的差别：上、下灯区里差得多的那个。</summary>
    private static int ZoneDifference(ZoneColors a, ZoneColors b) =>
        Math.Max(ColorChangeFilter.Difference(a.Upper, b.Upper), ColorChangeFilter.Difference(a.Lower, b.Lower));

    /// <summary>增强颜色打开时，每块区域都提高饱和度。</summary>
    private WallpaperColors Enhance(WallpaperColors colors) =>
        config.BoostColors
            ? colors.Map(color => ColorEnhancer.BoostSaturation(color, config.SaturationBoost))
            : colors;

    /// <summary>找不到壁纸或截图失败：宽限期内继续发上一次的颜色，超过宽限期就停止发送。</summary>
    private void HandleWallpaperMissing(TimeSpan lastSuccess, TimeSpan lostGrace)
    {
        if (clock.Elapsed - lastSuccess >= lostGrace)
        {
            foreach (ColorChangeFilter filter in filters.Values)
            {
                filter.Clear();
            }
        }

        ZoneColors? targetColors = CurrentTarget();
        string state = targetColors is null
            ? "找不到 Wallpaper Engine 的壁纸窗口（没开？），麦克风显示自带灯效"
            : "暂时找不到壁纸窗口，继续发送上一次的颜色";
        UpdateStatus(s => s with { WallpaperState = state, Target = targetColors });
    }

    /// <summary>麦克风两个灯区的目标颜色。任何一个没有颜色就返回 null（过滤器总是一起设置、一起清空）。</summary>
    private ZoneColors? CurrentTarget() =>
        filters[micRegions.Upper].Target is Rgb upper && filters[micRegions.Lower].Target is Rgb lower
            ? new ZoneColors(upper, lower)
            : null;

    /// <summary>某块区域此刻应该显示的颜色（渐变中的颜色）。还没有颜色时返回 null。</summary>
    private Rgb? CurrentOf(ColorSource region, TimeSpan now) => filters[region].Current(now);

    // ───────────────────────── 发送线程 ─────────────────────────

    private void SendLoop()
    {
        int intervalMs = config.SendIntervalMs;
        using var mic = new QuadcastMic();
        long nextFrameMs = clock.ElapsedMilliseconds;
        TimeSpan allowedGap = TimeSpan.FromMilliseconds(intervalMs) + stallThreshold;
        TimeSpan lastRound = clock.Elapsed;
        TimeSpan lastSendTook = TimeSpan.Zero;

        while (!stopping.IsCancellationRequested)
        {
            // 卡顿记录：两帧之间隔得太久，说明发送线程被卡住了（整台电脑卡，或者 USB 卡住）
            TimeSpan roundStart = clock.Elapsed;
            TimeSpan gap = roundStart - lastRound;
            if (gap > allowedGap)
            {
                // 分清楚是卡在给麦克风发数据上（USB 慢），还是整个程序被卡住（电脑整体卡了一下）
                string where = lastSendTook.TotalMilliseconds >= gap.TotalMilliseconds * 0.7
                    ? "主要卡在给麦克风发数据上（USB 那边慢）"
                    : "发数据本身不慢，是整个程序被卡住了（多半是电脑整体卡了一下）";
                StallLog.Write(
                    "发送",
                    $"发送中断了 {gap.TotalMilliseconds:0} 毫秒，其中给麦克风发数据花了 {lastSendTook.TotalMilliseconds:0} 毫秒：{where}。这段时间麦克风会回到自带灯效");
            }

            lastRound = roundStart;
            TimeSpan sendStart = clock.Elapsed;

            try
            {
                SendOnce(mic);
            }
            catch (Exception ex)
            {
                // 线程最外层：任何意外都不能让程序崩溃。记下原因，下一帧继续
                UpdateStatus(s => s with { LastError = $"发送出错：{ex.GetType().Name}：{ex.Message}" });
            }

            lastSendTook = clock.Elapsed - sendStart;
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
        if (!config.MicEnabled)
        {
            UpdateStatus(s => s with { MicState = "麦克风同步已关闭，显示自带灯效", MicConnected = false });
            return;
        }

        if (paused)
        {
            UpdateStatus(s => s with { MicState = "已暂停，麦克风显示自带灯效" });
            return;
        }

        TimeSpan now = clock.Elapsed;
        if (CurrentOf(micRegions.Upper, now) is not Rgb upper || CurrentOf(micRegions.Lower, now) is not Rgb lower)
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
        if (result == SendResult.Failed)
        {
            StallLog.Write("麦克风", $"麦克风断开：{mic.LastError}");
        }
        else if (connected && !micConnectedBefore)
        {
            StallLog.Write("麦克风", "麦克风已连上");
        }

        if (result != SendResult.NotConnected)
        {
            micConnectedBefore = connected;
        }

        UpdateStatus(s => s with { MicState = micState, MicConnected = connected, FramesSent = s.FramesSent + (connected ? 1 : 0) });
    }

    // ───────────────────────── 主板线程 ─────────────────────────

    /// <summary>一个要同步的主板灯区，以及它的取色来源、灯效和灯珠数。</summary>
    private readonly record struct BoardZoneSetting(MotherboardLightZone Zone, ColorSource Source, LightEffect Effect, int LedCount);

    /// <summary>按配置列出打开了同步的主板灯区。配置和灯区的对应关系只写在这里。</summary>
    private List<BoardZoneSetting> EnabledBoardZones()
    {
        var zones = new List<BoardZoneSetting>();
        if (config.CaseEnabled)
        {
            zones.Add(new BoardZoneSetting(MotherboardLightZone.Case, config.CaseColorSource, config.CaseEffect, config.CaseLedCount));
        }

        if (config.RadiatorEnabled)
        {
            zones.Add(new BoardZoneSetting(MotherboardLightZone.Radiator, config.RadiatorColorSource, config.RadiatorEffect, config.RadiatorLedCount));
        }

        return zones;
    }

    private void BoardLoop()
    {
        List<BoardZoneSetting> zones = EnabledBoardZones();
        if (zones.Count == 0)
        {
            // 两个灯区都关了：不连主板，灯保持原样
            UpdateStatus(s => s with { BoardState = "机箱灯和冷排灯都没有打开同步" });
            return;
        }

        // 有流动灯效时要一直发，按流动的帧率走；全是单色时只是看看颜色有没有变，按发送间隔走
        bool anyFlow = zones.Any(zone => zone.Effect != LightEffect.Solid);
        TimeSpan interval = anyFlow ? flowFrameInterval : TimeSpan.FromMilliseconds(config.SendIntervalMs);
        using var board = new MotherboardLight();

        // 单色灯区最后一次成功发出的颜色。颜色没变就不发；连接出问题时清空，重新连上后全部重发
        var lastSent = new Dictionary<MotherboardLightZone, Rgb>();

        while (!stopping.IsCancellationRequested)
        {
            TimeSpan roundStart = clock.Elapsed;
            try
            {
                SendBoardOnce(board, zones, lastSent);
            }
            catch (Exception ex)
            {
                // 线程最外层：任何意外都不能让程序崩溃。记下原因，下一轮全部重发
                lastSent.Clear();
                UpdateStatus(s => s with { LastError = $"主板发送出错：{ex.GetType().Name}：{ex.Message}" });
            }

            WaitUntil(roundStart + interval);
        }
    }

    private void SendBoardOnce(MotherboardLight board, List<BoardZoneSetting> zones, Dictionary<MotherboardLightZone, Rgb> lastSent)
    {
        if (paused)
        {
            UpdateStatus(s => s with { BoardState = "已暂停，机箱灯和冷排灯保持当前颜色" });
            return;
        }

        TimeSpan now = clock.Elapsed;
        if (CurrentOf(ColorSource.Average, now) is null)
        {
            UpdateStatus(s => s with { BoardState = "没有颜色可发，机箱灯和冷排灯保持当前颜色" });
            return;
        }

        SendResult result = SendResult.Sent;
        foreach (BoardZoneSetting setting in zones)
        {
            Rgb[]? leds = BuildBoardLeds(setting, now, lastSent);
            if (leds is null)
            {
                continue;
            }

            TimeSpan sendStart = clock.Elapsed;
            result = board.SendZone(setting.Zone, leds);
            TimeSpan took = clock.Elapsed - sendStart;
            if (took > stallThreshold)
            {
                StallLog.Write("主板", $"给主板发颜色很慢：{took.TotalMilliseconds:0} 毫秒");
            }

            if (result != SendResult.Sent)
            {
                // 连接出问题：全部作废，重新连上后每个灯区都重发
                lastSent.Clear();
                break;
            }

            if (setting.Effect == LightEffect.Solid)
            {
                lastSent[setting.Zone] = leds[0];
            }
        }

        string boardState = result switch
        {
            SendResult.Sent => "主板已连接，机箱灯和冷排灯正在同步",
            SendResult.NotConnected => $"等待主板灯光芯片：{board.LastError}（每秒自动重试）",
            _ => $"主板断开：{board.LastError}（会自动重连）",
        };
        bool connected = result == SendResult.Sent;
        if (result == SendResult.Failed)
        {
            StallLog.Write("主板", $"主板断开：{board.LastError}");
        }
        else if (connected && !boardConnectedBefore)
        {
            StallLog.Write("主板", "主板灯光芯片已连上");
        }

        if (result != SendResult.NotConnected)
        {
            boardConnectedBefore = connected;
        }

        UpdateStatus(s => s with { BoardState = boardState, BoardConnected = connected });
    }

    /// <summary>
    /// 生成一个灯区这一帧要发的所有灯的颜色。不需要发时返回 null：
    /// 单色而且颜色和上次发的一样；或者要用的区域暂时还没有颜色（截图线程正好在设置过滤器）。
    /// </summary>
    private Rgb[]? BuildBoardLeds(BoardZoneSetting setting, TimeSpan now, Dictionary<MotherboardLightZone, Rgb> lastSent)
    {
        if (LightEffectRules.FlowRegionsOf(setting.Effect) is (ColorSource firstRegion, ColorSource secondRegion))
        {
            if (CurrentOf(firstRegion, now) is not Rgb first || CurrentOf(secondRegion, now) is not Rgb second)
            {
                return null;
            }

            double cyclePosition = now.TotalMilliseconds / config.FlowCycleMs;
            return LightEffectRules.BuildFlow(first, second, setting.LedCount, config.FlowPeriodLeds, cyclePosition);
        }

        if (CurrentOf(setting.Source, now) is not Rgb color
            || (lastSent.TryGetValue(setting.Zone, out Rgb sent) && sent == color))
        {
            return null;
        }

        return Enumerable.Repeat(color, setting.LedCount).ToArray();
    }

    // ───────────────────────── 小工具 ─────────────────────────

    /// <summary>启动日志里的设备设置，比如"麦克风 开，机箱灯 开（下半区），冷排灯 关"。</summary>
    private string DescribeDevices()
    {
        static string OnOff(bool enabled) => enabled ? "开" : "关";
        return $"麦克风 {OnOff(config.MicEnabled)}，"
            + $"机箱灯 {DescribeBoardZone(config.CaseEnabled, config.CaseColorSource, config.CaseEffect)}，"
            + $"冷排灯 {DescribeBoardZone(config.RadiatorEnabled, config.RadiatorColorSource, config.RadiatorEffect)}";
    }

    /// <summary>
    /// 一个主板灯区的设置说明，比如"开（单色，下半区）""开（左右流动）""关"。日志、命令行显示共用。
    /// </summary>
    public static string DescribeBoardZone(bool enabled, ColorSource source, LightEffect effect)
    {
        if (!enabled)
        {
            return "关";
        }

        return effect == LightEffect.Solid
            ? $"开（{LightEffectRules.TextOf(effect)}，{ColorSourceRules.TextOf(source)}）"
            : $"开（{LightEffectRules.TextOf(effect)}）";
    }

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

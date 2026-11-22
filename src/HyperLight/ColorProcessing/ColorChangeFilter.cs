namespace HyperLight.ColorProcessing;

/// <summary>
/// 颜色变化过滤：新颜色和当前目标颜色差别小于门槛，就当没变；
/// 差别够大的新颜色，要连续出现几次（确认次数）才采用，免得换壁纸时一闪而过的过渡画面也传到灯上；
/// 采用以后，在一段时间内渐变过去，不硬切，免得闪眼睛。
/// 截图线程调用 <see cref="Offer"/>，发送线程调用 <see cref="Current"/>，两边可以同时调用（内部加锁）。
/// 时间由调用方传入（同一个计时器的读数），这样这个类不依赖系统时钟，方便测试。
/// </summary>
internal sealed class ColorChangeFilter
{
    private readonly object gate = new();
    private readonly int threshold;
    private readonly TimeSpan fadeDuration;
    private readonly int confirmCaptures;

    private bool hasColor;
    private Rgb pending;
    private int pendingCount;
    private Rgb fadeFrom;
    private Rgb target;
    private TimeSpan fadeStart;

    /// <param name="threshold">变化门槛（0～255）：红、绿、蓝任一项相差达到这个值才算变了。</param>
    /// <param name="fadeDuration">渐变时间。0 表示立刻切换。</param>
    /// <param name="confirmCaptures">确认次数：差别够大的新颜色要连续出现几次才采用。1 表示出现就采用。</param>
    public ColorChangeFilter(int threshold, TimeSpan fadeDuration, int confirmCaptures = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(threshold);
        ArgumentOutOfRangeException.ThrowIfLessThan(fadeDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(confirmCaptures, 1);
        this.threshold = threshold;
        this.fadeDuration = fadeDuration;
        this.confirmCaptures = confirmCaptures;
    }

    /// <summary>当前的目标颜色（渐变的终点）。还没有颜色时为 null。</summary>
    public Rgb? Target
    {
        get
        {
            lock (gate)
            {
                return hasColor ? target : null;
            }
        }
    }

    /// <summary>
    /// 报告新截到的颜色。返回 true 表示目标颜色真的变了。
    /// 第一次报告时直接显示，不渐变。之后差别够大的新颜色，要连续出现 confirmCaptures 次（每次都和上一次差不多）才采用；
    /// 中途变回原来的颜色，就当什么都没发生。采用时从"此刻正在显示的颜色"渐变过去，所以渐变到一半又变了也不会跳。
    /// </summary>
    public bool Offer(Rgb captured, TimeSpan now)
    {
        lock (gate)
        {
            if (!hasColor)
            {
                hasColor = true;
                fadeFrom = captured;
                target = captured;
                fadeStart = now;
                pendingCount = 0;
                return true;
            }

            if (Difference(captured, target) < threshold)
            {
                // 和当前颜色差不多：之前等待确认的新颜色作废（比如过渡画面闪了一下又回来了）
                pendingCount = 0;
                return false;
            }

            // 差别够大：和上一次等待确认的颜色差不多就累计，否则从这次重新算
            if (pendingCount > 0 && Difference(captured, pending) < threshold)
            {
                pendingCount++;
            }
            else
            {
                pending = captured;
                pendingCount = 1;
            }

            if (pendingCount < confirmCaptures)
            {
                return false;
            }

            pendingCount = 0;
            fadeFrom = CurrentLocked(now);
            target = captured;
            fadeStart = now;
            return true;
        }
    }

    /// <summary>此刻应该显示的颜色。还没有颜色（或已清空）时返回 null，表示不要发送。</summary>
    public Rgb? Current(TimeSpan now)
    {
        lock (gate)
        {
            return hasColor ? CurrentLocked(now) : null;
        }
    }

    /// <summary>清空颜色（比如找不到壁纸了）。之后 <see cref="Current"/> 返回 null，直到下一次 <see cref="Offer"/>。</summary>
    public void Clear()
    {
        lock (gate)
        {
            hasColor = false;
            pendingCount = 0;
        }
    }

    /// <summary>两个颜色的差别：红、绿、蓝三项里相差最大的那一项（0～255）。</summary>
    public static int Difference(Rgb a, Rgb b) =>
        Math.Max(Math.Abs(a.R - b.R), Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B)));

    private Rgb CurrentLocked(TimeSpan now)
    {
        if (fadeDuration == TimeSpan.Zero)
        {
            return target;
        }

        double progress = (now - fadeStart) / fadeDuration;
        if (progress >= 1)
        {
            return target;
        }

        if (progress <= 0)
        {
            return fadeFrom;
        }

        return new Rgb(
            Lerp(fadeFrom.R, target.R, progress),
            Lerp(fadeFrom.G, target.G, progress),
            Lerp(fadeFrom.B, target.B, progress));
    }

    private static byte Lerp(byte from, byte to, double progress) =>
        (byte)Math.Round(from + ((to - from) * progress));
}

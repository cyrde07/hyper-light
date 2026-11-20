namespace HyperLight;

/// <summary>
/// 同一时间只允许一个 HyperLight 往麦克风发颜色。托盘程序、sync 命令、color 命令共用这把锁：
/// 两个程序同时发，灯会乱闪；开机自启的托盘程序在运行时，再双击 exe 也不会多开一个。
/// </summary>
/// <remarks>
/// 用 Windows 的"具名互斥锁"（Mutex）实现，程序退出（包括崩溃）时系统会自动释放。
/// 注意：互斥锁在哪个线程拿到，就必须在哪个线程释放（Dispose）。
/// </remarks>
internal sealed class MicOwnerLock : IDisposable
{
    /// <summary>锁的名字。Local 表示只在当前登录的用户里有效。</summary>
    private const string mutexName = @"Local\HyperLight.MicOwner";

    /// <summary>命令行版本拿不到锁时显示的提示。</summary>
    public const string BusyMessage =
        "另一个 HyperLight 正在控制麦克风（多半是右下角托盘里的那个）。请先在托盘图标上右键 → 退出，再运行这个命令。";

    private readonly Mutex mutex;
    private bool disposed;

    private MicOwnerLock(Mutex mutex)
    {
        this.mutex = mutex;
    }

    /// <summary>尝试拿到锁，不等待。别的 HyperLight 正在控制麦克风时返回 null。</summary>
    public static MicOwnerLock? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        try
        {
            if (mutex.WaitOne(TimeSpan.Zero))
            {
                return new MicOwnerLock(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个拿着锁的程序没释放就退出了（比如被强制结束）：系统已经把锁交给我们
            return new MicOwnerLock(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mutex.ReleaseMutex();
        mutex.Dispose();
    }
}

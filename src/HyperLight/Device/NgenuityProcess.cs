using System.ComponentModel;
using System.Diagnostics;

namespace HyperLight.Device;

/// <summary>
/// 检查 HyperX NGENUITY（麦克风的官方控制软件）有没有在运行。
/// 它和本工具会同时往麦克风发灯光数据、互相打架，所以运行本工具时必须关掉它。
/// 进程名规则全项目只写在这里（唯一真相来源）。
/// </summary>
/// <remarks>
/// M0 实测（NGENUITY 5.38）一共三个进程：NGenuity2、NGenuity2Helper、NGenuity2GeneralHelper。
/// 用"名字以 NGenuity 开头"来判断，三个都能认出来；以后版本号变了（比如 NGenuity3）也不用改。
/// 注意：M0 实测 NGENUITY 开着时麦克风照样"能打开"，所以不能靠"打不开"来判断它在不在，只能查进程。
/// </remarks>
internal static class NgenuityProcess
{
    private const string namePrefix = "NGenuity";

    /// <summary>找到的一个 NGENUITY 进程。读不到路径时，Path 是说明文字。</summary>
    public readonly record struct Info(string Name, int Id, string Path);

    /// <summary>
    /// NGENUITY 是否在运行。
    /// 读进程列表失败时返回 false：宁可漏报一次提示，也不让程序崩溃。
    /// </summary>
    public static bool IsRunning()
    {
        try
        {
            return Scan(readPath: false).Count > 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>列出所有在运行的 NGENUITY 进程，包括程序路径（诊断用）。</summary>
    /// <exception cref="Win32Exception">读取进程列表失败。</exception>
    /// <exception cref="InvalidOperationException">读取进程列表失败。</exception>
    public static IReadOnlyList<Info> FindRunning() => Scan(readPath: true);

    private static List<Info> Scan(bool readPath)
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            var found = new List<Info>();
            foreach (Process process in processes)
            {
                string? name = TryGetName(process);
                if (name is null || !name.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string path = readPath ? TryGetPath(process) : "";
                found.Add(new Info(name, process.Id, path));
            }

            found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return found;
        }
        finally
        {
            // Process 对象占用系统句柄，用完必须释放
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>读进程名。进程刚好在这时退出了，会读不到，返回 null。</summary>
    private static string? TryGetName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>读程序路径。没有权限或进程已退出时返回说明文字。</summary>
    private static string TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "（空）";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return $"（读不到：{ex.GetType().Name}）";
        }
    }
}

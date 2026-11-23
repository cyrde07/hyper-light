using System.ComponentModel;
using System.Diagnostics;

namespace HyperLight.Device;

/// <summary>
/// 检查 OpenRGB 有没有在运行。
/// 它也能控制主板灯光芯片（还有麦克风），和本工具同时运行会互相抢，所以本工具控制主板灯时要关掉它。
/// 进程名规则全项目只写在这里（唯一真相来源）。
/// </summary>
internal static class OpenRgbProcess
{
    /// <summary>OpenRGB 的进程名（不含 .exe）。</summary>
    private const string processName = "OpenRGB";

    /// <summary>
    /// OpenRGB 是否在运行。
    /// 读进程列表失败时返回 false：宁可漏报一次提示，也不让程序崩溃。
    /// </summary>
    public static bool IsRunning()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        // Process 对象占用系统句柄，用完必须释放
        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}

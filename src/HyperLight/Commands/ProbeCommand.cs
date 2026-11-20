using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HyperLight.Capture;

namespace HyperLight.Commands;

/// <summary>
/// probe 命令（方案 A 测试）：找到 Wallpaper Engine 画壁纸的窗口并截图，
/// 截图和报告存到 exe 旁边的 probe-日期-时间 文件夹，结束后自动打开这个文件夹。
/// </summary>
internal static class ProbeCommand
{
    /// <summary>命令说明，由 Program 统一显示。</summary>
    public static readonly string[] UsageLines =
    {
        "  probe  方案 A 测试：找 Wallpaper Engine 画壁纸的窗口并截图，结果存到 exe 旁边的 probe-日期-时间 文件夹",
    };

    /// <summary>执行 probe 命令。</summary>
    public static int Run()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}");
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"创建结果文件夹失败（{ex.Message}）。请把 exe 放到桌面或下载文件夹这类普通位置再运行。");
            return 1;
        }

        Console.WriteLine("正在测试，大约需要几秒……");
        Console.WriteLine();

        string report = WallpaperWindowProbe.Run(folder);
        Console.Write(report);
        Console.WriteLine();

        string reportPath = Path.Combine(folder, "probe-report.txt");
        try
        {
            // UTF-8 带 BOM：Windows 记事本打开中文不会乱码
            File.WriteAllText(reportPath, report, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"报告已保存：{reportPath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"报告保存失败（{ex.Message}），请直接复制窗口里的内容。");
        }

        OpenFolder(folder);
        return 0;
    }

    /// <summary>用资源管理器打开结果文件夹。打不开也不影响结果，只提示一下。</summary>
    private static void OpenFolder(string folder)
    {
        try
        {
            using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"自动打开文件夹失败（{ex.Message}），请手动打开：{folder}");
        }
    }
}

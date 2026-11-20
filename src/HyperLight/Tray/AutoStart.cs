using Microsoft.Win32;

namespace HyperLight.Tray;

/// <summary>
/// 开机自启：在当前用户的"启动项"里登记本程序（注册表 HKCU\...\Run），只影响当前用户，不需要管理员权限。
/// 只有用户在托盘菜单里勾选时才会写入，程序不会自己打开。
/// </summary>
/// <remarks>
/// 读写注册表失败时会抛出 UnauthorizedAccessException、System.Security.SecurityException 或 IOException，
/// 由调用方（托盘菜单）接住并提示。
/// 另外，Windows 的任务管理器 →「启动应用」里也能单独禁用某个启动项；那里禁用了，这里仍会显示为已勾选。
/// </remarks>
internal static class AutoStart
{
    /// <summary>开机自启时带的参数：表示是开机自动启动的，不弹"已在托盘运行"的提示。</summary>
    public const string Argument = "--autostart";

    private const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string valueName = "HyperLight";

    /// <summary>当前有没有登记开机自启。</summary>
    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return key?.GetValue(valueName) is string;
    }

    /// <summary>登记开机自启，指向现在这个 exe 的位置。</summary>
    public static void Enable()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(runKeyPath);
        key.SetValue(valueName, BuildCommand());
    }

    /// <summary>取消开机自启。本来就没有登记时什么也不做。</summary>
    public static void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// 已经登记了开机自启、但 exe 换了位置（比如解压了新版本）时，把启动项改成现在的位置，免得开机时找不到程序。
    /// </summary>
    public static void RefreshPathIfEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        if (key?.GetValue(valueName) is string current && current != BuildCommand())
        {
            Enable();
        }
    }

    private static string BuildCommand()
    {
        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("读不到本程序的位置，没法设置开机自启");
        return $"\"{exePath}\" {Argument}";
    }
}

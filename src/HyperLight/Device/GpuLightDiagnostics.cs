using System.Diagnostics;
using System.Text;

namespace HyperLight.Device;

/// <summary>
/// 显卡灯光诊断（G0）：列出 NVIDIA 显卡、核对编号、做认芯片检查、读芯片型号和灯珠数。不改任何灯光。
/// </summary>
/// <remarks>
/// 这是诊断代码：任何一步失败，都只把原因写进报告，不能影响后面的内容。
/// 所以这里会统一接住所有异常（catch Exception），这是有意为之，和 <see cref="HidDiagnostics"/> 一样。
/// </remarks>
internal static class GpuLightDiagnostics
{
    /// <summary>华硕的灯光软件，开着会和本程序抢显卡灯（只用来提醒）。</summary>
    private static readonly string[] asusLightingProcesses = { "ArmouryCrate", "GPUTweakIII", "GPUTweakII", "LightingService" };

    /// <summary>生成完整的诊断报告文本。本方法不会抛出异常。</summary>
    public static string BuildReport()
    {
        var report = new StringBuilder();
        report.AppendLine("HyperLight 显卡灯光诊断（G0，只读取信息，不改灯光）");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();
        AppendAsusSoftwareCheck(report);
        report.AppendLine();

        try
        {
            AppendGpus(report);
        }
        catch (Exception ex)
        {
            report.AppendLine($"诊断时出现意外错误：{ex.GetType().Name}：{ex.Message}");
        }

        return report.ToString();
    }

    private static void AppendAsusSoftwareCheck(StringBuilder report)
    {
        report.AppendLine("============== 华硕灯光软件检查 ==============");
        var running = new List<string>();
        foreach (string name in asusLightingProcesses)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(name);
                if (processes.Length > 0)
                {
                    running.Add(name);
                }

                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
            catch (Exception)
            {
                // 读不到某个进程不影响诊断
            }
        }

        report.AppendLine(running.Count == 0
            ? "没有发现 Armoury Crate、GPU Tweak 在运行。"
            : $"正在运行：{string.Join("、", running)}。它们可能会和本程序抢显卡灯，读到的信息可能不准。");
    }

    private static void AppendGpus(StringBuilder report)
    {
        report.AppendLine("================= NVIDIA 显卡 =================");
        NvApi? api = NvApi.TryLoad(out string? loadError);
        if (api is null)
        {
            report.AppendLine(loadError);
            return;
        }

        IReadOnlyList<IntPtr> gpus = api.EnumPhysicalGpus(out int status);
        if (status != NvApi.Ok)
        {
            report.AppendLine($"列出显卡失败（状态码 {status}）。");
            return;
        }

        report.AppendLine($"找到 {gpus.Count} 张 NVIDIA 显卡。");
        for (int i = 0; i < gpus.Count; i++)
        {
            int idStatus = api.GetPciIdentifiers(gpus[i], out NvApi.PciIds ids);
            string idText = idStatus == NvApi.Ok ? ids.ToString() : $"读不到编号（状态码 {idStatus}）";
            bool supported = idStatus == NvApi.Ok && GpuLightProtocol.IsSupportedCard(ids);
            report.AppendLine($"  显卡 {i}：{idText}{(supported ? "    ← 支持的显卡（华硕 ROG STRIX RTX 4090 白色版）" : "")}");
        }

        report.AppendLine();
        GpuLight? light = GpuLight.TryFind(api, out string? findError);
        if (light is null)
        {
            report.AppendLine(findError);
            report.AppendLine("请把这份报告发给 Claude：如果你的显卡是 ROG STRIX 4090 白色版但没被认出来，可能是板卡编号没收录。");
            return;
        }

        AppendChip(report, light);
    }

    private static void AppendChip(StringBuilder report, GpuLight light)
    {
        report.AppendLine($"============ 灯光芯片（端口 {GpuLightProtocol.I2cPort}，地址 0x{GpuLightProtocol.ChipAddress:X2}） ============");

        bool passed = light.TryCheckSignature(out byte?[] values);
        string valueText = string.Join(" ", values.Select(value => value is byte b ? b.ToString("X2") : "??"));
        report.AppendLine($"认芯片检查（寄存器 0xA0～0xAF 应该是 00～0F）：{valueText}");
        if (!passed)
        {
            report.AppendLine("没通过：不是预期的 ENE 灯光芯片，或者读不到。为了安全，不再继续读这颗芯片。请把报告发给 Claude。");
            return;
        }

        report.AppendLine("通过：是 ENE 灯光芯片。");

        byte[]? nameBytes = light.TryReadRegisters(GpuLightProtocol.DeviceNameRegister, GpuLightProtocol.DeviceNameLength);
        string deviceName = nameBytes is null ? "" : Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');
        report.AppendLine(nameBytes is null
            ? "芯片型号：读不到"
            : $"芯片型号：{deviceName}（原始：{Convert.ToHexString(nameBytes)}）");

        byte[]? config = light.TryReadRegisters(GpuLightProtocol.ConfigTableRegister, GpuLightProtocol.ConfigTableLength);
        if (config is null)
        {
            report.AppendLine("配置表：读不到");
        }
        else
        {
            report.AppendLine($"配置表：{Convert.ToHexString(config)}");
            int? ledCount = GpuLightProtocol.LedCountOf(deviceName, config);
            report.AppendLine(ledCount is int count
                ? $"灯珠数（按 OpenRGB 的规则）：{count}"
                : "灯珠数：这个芯片型号 OpenRGB 没收录，看不出来");
        }

        report.AppendLine(light.TryReadRegister(GpuLightProtocol.DirectModeRegister, out byte direct)
            ? $"直接控制模式：{(direct == 0 ? "关（显卡在显示自己的灯效）" : $"开（0x{direct:X2}）")}"
            : "直接控制模式：读不到");
        report.AppendLine(light.TryReadRegister(GpuLightProtocol.EffectModeRegister, out byte mode)
            ? $"显卡自己的灯效种类：0x{mode:X2}（0 关、1 静态、2 呼吸、3 闪烁、4 颜色循环、5 彩虹……）"
            : "显卡自己的灯效种类：读不到");
    }
}

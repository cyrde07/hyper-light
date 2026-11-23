using System.Diagnostics;
using System.Text;
using HidSharp;

namespace HyperLight.Device;

/// <summary>
/// 主板灯光芯片诊断（M5-0）：找到技嘉主板的灯光芯片，读取它的信息。
/// 只发一个"读取信息"的请求，不改任何灯光。
/// </summary>
/// <remarks>
/// 这是诊断代码：任何一步失败，都只把原因写进报告，不能影响后面的内容。
/// 所以这里会统一接住所有异常（catch Exception），这是有意为之，和 <see cref="HidDiagnostics"/> 一样。
/// </remarks>
internal static class MotherboardLightDiagnostics
{
    /// <summary>生成完整的诊断报告文本。本方法不会抛出异常。</summary>
    public static string BuildReport()
    {
        var report = new StringBuilder();
        report.AppendLine("HyperLight 主板灯光诊断（M5-0，只读取信息，不改灯光）");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine();
        AppendOpenRgbCheck(report);
        report.AppendLine();
        AppendNodes(report);
        return report.ToString();
    }

    // ───────────────────────── 报告各部分 ─────────────────────────

    private static void AppendOpenRgbCheck(StringBuilder report)
    {
        report.AppendLine("================= OpenRGB 进程检查 =================");
        try
        {
            Process[] processes = Process.GetProcessesByName("OpenRGB");
            int count = processes.Length;
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            report.AppendLine(count == 0
                ? "没有在运行。"
                : $"正在运行（{count} 个进程）。它可能同时在和芯片通信，读到的信息可能不准。请完全退出 OpenRGB 后再运行一次。");
        }
        catch (Exception ex)
        {
            report.AppendLine($"读取进程列表失败：{Describe(ex)}");
        }
    }

    private static void AppendNodes(StringBuilder report)
    {
        report.AppendLine($"============ 灯光芯片节点（{MotherboardLightProtocol.It5702}） ============");

        List<HidDevice> nodes;
        try
        {
            nodes = DeviceList.Local.GetHidDevices()
                .Where(device => new UsbId(device.VendorID, device.ProductID) == MotherboardLightProtocol.It5702)
                .ToList();
        }
        catch (Exception ex)
        {
            report.AppendLine($"读取设备列表失败：{Describe(ex)}");
            return;
        }

        if (nodes.Count == 0)
        {
            report.AppendLine("没有找到。这块主板的灯光芯片应该是这个编号，请把报告发给 Claude。");
            return;
        }

        report.AppendLine($"找到 {nodes.Count} 个节点。");
        report.AppendLine();

        int controlNodes = 0;
        foreach (HidDevice node in nodes)
        {
            int? featureLength = SafeInt(node.GetMaxFeatureReportLength);
            IReadOnlyCollection<uint> usages = ReadUsages(node, out string usageText);
            bool isControl = MotherboardLightProtocol.IsControlNode(MotherboardLightProtocol.It5702, featureLength, usages);

            report.AppendLine(isControl ? "★ 灯光控制节点（本工具用这个）" : "  其他节点（不用）");
            report.AppendLine($"    用途：{usageText}");
            report.AppendLine($"    Feature 长度：{featureLength?.ToString() ?? "读不到"}");
            report.AppendLine($"    路径：{SafeText(() => node.DevicePath)}");

            if (isControl)
            {
                controlNodes++;
                AppendInfo(report, node);
            }

            report.AppendLine();
        }

        if (controlNodes == 0)
        {
            report.AppendLine("没有找到灯光控制节点（Usage Page 0xFF89 / Usage 0xCC，Feature 长度 64）。请把报告发给 Claude。");
        }
    }

    /// <summary>向控制节点发"读取信息"请求，读回芯片信息并解读。</summary>
    private static void AppendInfo(StringBuilder report, HidDevice node)
    {
        byte[] reply = MotherboardLightProtocol.NewReport();
        try
        {
            using HidStream stream = node.Open();
            stream.SetFeature(MotherboardLightProtocol.BuildInfoRequest());
            stream.GetFeature(reply);
        }
        catch (Exception ex)
        {
            report.AppendLine($"    读取信息失败：{Describe(ex)}");
            return;
        }

        report.AppendLine($"    原始回复：{Convert.ToHexString(reply)}");

        MotherboardLightInfo? info = MotherboardLightProtocol.ParseInfo(reply);
        if (info is null)
        {
            report.AppendLine("    回复的格式不对（报告编号不是 0xCC），看不懂。请把报告发给 Claude。");
            return;
        }

        report.AppendLine("    ---- 解读 ----");
        report.AppendLine($"    产品名：{(info.ProductName.Length == 0 ? "（空）" : info.ProductName)}");
        report.AppendLine($"    固件版本：{info.FirmwareVersion}");
        report.AppendLine($"    芯片编号：0x{info.ChipId:X8}");
        report.AppendLine($"    产品代码 0x{info.Product:X2}，设备序号 {info.DeviceNumber}，灯带检测 0x{info.StripDetect:X2}，功能标志 0x{info.SupportFlags:X2}");
        report.AppendLine($"    灯数字节：{Convert.ToHexString(info.LedCountCodes)}");
        report.AppendLine($"    通道 0x58（主板印 ARGB_V2_2，接冷排）：      灯数上限 {LedLimitText(info.LedCountCodes[0] & 0x0F)}，颜色顺序 {ColorOrderText(info.StripCalibration0)}");
        report.AppendLine($"    通道 0x59（主板印 ARGB_V2_1，接机箱集线板）：灯数上限 {LedLimitText(info.LedCountCodes[0] >> 4)}，颜色顺序 {ColorOrderText(info.StripCalibration1)}");
    }

    // ───────────────────────── 小工具 ─────────────────────────

    /// <summary>读取节点的所有 Usage。读不到时返回空集合，原因写进 <paramref name="text"/>。</summary>
    private static IReadOnlyCollection<uint> ReadUsages(HidDevice device, out string text)
    {
        try
        {
            uint[] usages = device.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .ToArray();
            text = usages.Length == 0
                ? "（空）"
                : string.Join("、", usages.Select(usage => $"Usage Page 0x{usage >> 16:X4} / Usage 0x{usage & 0xFFFF:X4}"));
            return usages;
        }
        catch (Exception ex)
        {
            text = $"（读不到：{ex.GetType().Name}）";
            return Array.Empty<uint>();
        }
    }

    private static string LedLimitText(int code) =>
        MotherboardLightProtocol.LedCountFromCode(code) is int limit ? $"{limit}（代码 {code}）" : $"不认识（代码 {code}）";

    private static string ColorOrderText(uint calibration) =>
        $"{MotherboardLightProtocol.DescribeColorOrder(calibration) ?? "不认识"}（0x{calibration:X8}）";

    private static string Describe(Exception ex) => $"{ex.GetType().Name}：{ex.Message}";

    /// <summary>安全读取一段文字：读失败或为空时返回说明文字，不抛异常。</summary>
    private static string SafeText(Func<string?> read)
    {
        try
        {
            string? text = read();
            return string.IsNullOrWhiteSpace(text) ? "（空）" : text.Trim();
        }
        catch (Exception ex)
        {
            return $"（读不到：{ex.GetType().Name}）";
        }
    }

    /// <summary>安全读取一个整数：读失败时返回 null，不抛异常。</summary>
    private static int? SafeInt(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}

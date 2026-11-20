using System.Diagnostics;
using System.Text;
using HidSharp;

namespace HyperLight.Device;

/// <summary>
/// M0 设备诊断：列出电脑上所有 HID 设备，标出 QuadCast S 候选，并检查 NGENUITY 是否在运行。
/// 不向任何设备发送数据。唯一的"动作"是对候选设备做打开测试：打开后马上关闭，对麦克风没有影响。
/// </summary>
/// <remarks>
/// 这是诊断代码：任何一个设备、任何一个字段读取失败，都只把原因写进报告，
/// 不能影响后面的内容。所以这里会统一接住所有异常（catch Exception），这是有意为之。
/// </remarks>
internal static class HidDiagnostics
{
    /// <summary>
    /// 搜索 NGENUITY 进程用的关键字（不区分大小写）。
    /// M0 先模糊搜索；M1 再按这次查到的真实进程名精确检测。
    /// </summary>
    private static readonly string[] ngenuityKeywords = { "ngenuity", "hyperx" };

    /// <summary>
    /// QuadCast S 协议的包是 64 字节，Windows 发送时前面要多加 1 字节报告编号，
    /// 所以真正用来控制灯光的那个设备节点，Feature 报告长度应该是 65。
    /// </summary>
    private const int expectedFeatureLength = 65;

    /// <summary>生成完整的诊断报告文本。本方法不会抛出异常。</summary>
    public static string BuildReport()
    {
        var report = new StringBuilder();
        AppendHeader(report);
        report.AppendLine();
        AppendHidDevices(report);
        report.AppendLine();
        AppendNgenuityProcesses(report);
        return report.ToString();
    }

    // ───────────────────────── 报告各部分 ─────────────────────────

    private static void AppendHeader(StringBuilder report)
    {
        string bits = Environment.Is64BitOperatingSystem ? "64" : "32";
        report.AppendLine("HyperLight 设备诊断（M0）");
        report.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"系统：{Environment.OSVersion.VersionString}（{bits} 位）");
    }

    private static void AppendHidDevices(StringBuilder report)
    {
        report.AppendLine("==================== HID 设备 ====================");

        List<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex)
        {
            report.AppendLine($"读取设备列表失败：{Describe(ex)}");
            return;
        }

        // 逐个读取信息；某个设备读失败，只记下原因，继续读下一个
        var infos = new List<HidDeviceInfo>();
        var failures = new List<string>();
        foreach (HidDevice device in devices)
        {
            try
            {
                infos.Add(ReadInfo(device));
            }
            catch (Exception ex)
            {
                failures.Add($"{SafeText(() => device.DevicePath)}：{Describe(ex)}");
            }
        }

        List<HidDeviceInfo> candidates = infos
            .Where(info => info.Match != MicMatch.None)
            .OrderBy(info => DisplayOrder(info.Match))
            .ThenBy(info => info.Id.VendorId)
            .ThenBy(info => info.Id.ProductId)
            .ThenBy(info => info.DevicePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<HidDeviceInfo> others = infos
            .Where(info => info.Match == MicMatch.None)
            .OrderBy(info => info.Id.VendorId)
            .ThenBy(info => info.Id.ProductId)
            .ToList();

        int quadcastCount = candidates.Count(info => info.Match == MicMatch.QuadcastS);
        report.AppendLine($"共 {devices.Count} 个，其中 QuadCast S 候选 {quadcastCount} 个");
        report.AppendLine("标记：★ 已知的 QuadCast S    ○ 其他 HyperX 麦克风（不是目标）    △ 厂商编号对得上，但产品编号不在表里");
        report.AppendLine();

        if (quadcastCount == 0)
        {
            AppendNotFoundAdvice(report);
        }

        foreach (HidDeviceInfo info in candidates)
        {
            AppendCandidate(report, info);
        }

        report.AppendLine("---------------- 其他设备（无关，只列出来备查） ----------------");
        foreach (HidDeviceInfo info in others)
        {
            report.AppendLine($"  {info.Id}  {info.ProductName}（Feature {LengthText(info.FeatureLength)}）");
        }

        if (failures.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"---------------- 读取失败的设备（{failures.Count} 个） ----------------");
            foreach (string failure in failures)
            {
                report.AppendLine($"  {failure}");
            }
        }
    }

    private static void AppendNotFoundAdvice(StringBuilder report)
    {
        report.AppendLine("没有找到 QuadCast S 候选。请检查：");
        report.AppendLine("  1. 麦克风的 USB 线插好了吗？麦克风的灯亮吗？");
        report.AppendLine("  2. 换一个 USB 口（最好直接插主板后面的口，不用集线器），再运行一次");
        report.AppendLine("  3. 下面带 △ 的设备如果名字像麦克风，可能是没收录的新批次，把报告发给 Claude");
        report.AppendLine();
    }

    private static void AppendCandidate(StringBuilder report, HidDeviceInfo info)
    {
        string featureHint = info.Match == MicMatch.QuadcastS && info.FeatureLength == expectedFeatureLength
            ? "    ← 正好是 65，M1 很可能就用这个节点"
            : "";

        report.AppendLine($"{MatchLabel(info)}  {info.Id}  {info.ProductName}");
        report.AppendLine($"    厂商：{info.Manufacturer}    固件版本：{info.ReleaseNumber}");
        report.AppendLine($"    用途：{info.Usage}    使用报告编号：{info.ReportsUseId}");
        report.AppendLine(
            $"    报告长度：Input {LengthText(info.InputLength)} / Output {LengthText(info.OutputLength)}"
            + $" / Feature {LengthText(info.FeatureLength)}{featureHint}");
        report.AppendLine($"    打开测试：{info.OpenTest}");
        report.AppendLine($"    路径：{info.DevicePath}");
        report.AppendLine();
    }

    private static void AppendNgenuityProcesses(StringBuilder report)
    {
        report.AppendLine("================= NGENUITY 进程检查 =================");
        report.AppendLine($"（搜索名字里带 {string.Join(" 或 ", ngenuityKeywords)} 的进程，不区分大小写）");

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            report.AppendLine($"读取进程列表失败：{Describe(ex)}");
            return;
        }

        try
        {
            var lines = new List<string>();
            foreach (Process process in processes)
            {
                string name = SafeText(() => process.ProcessName);
                bool isMatch = ngenuityKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (!isMatch)
                {
                    continue;
                }

                string id = SafeText(() => process.Id.ToString());
                string path = SafeText(() => process.MainModule?.FileName);
                lines.Add($"  {name}    进程号 {id}    路径：{path}");
            }

            if (lines.Count == 0)
            {
                report.AppendLine("没有找到。");
                return;
            }

            report.AppendLine($"找到 {lines.Count} 个：");
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                report.AppendLine(line);
            }
            report.AppendLine("提示：从 M1 开始，运行本工具前必须完全退出 NGENUITY（两个程序会抢麦克风）。");
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

    // ───────────────────────── 读取单个设备 ─────────────────────────

    /// <summary>一个 HID 设备节点的诊断信息。文字字段都已经处理成可以直接显示的内容。</summary>
    private sealed record HidDeviceInfo(
        UsbId Id,
        MicMatch Match,
        string? OtherMicName,
        string ProductName,
        string Manufacturer,
        string ReleaseNumber,
        string Usage,
        string ReportsUseId,
        int? InputLength,
        int? OutputLength,
        int? FeatureLength,
        string OpenTest,
        string DevicePath);

    private static HidDeviceInfo ReadInfo(HidDevice device)
    {
        var id = new UsbId(device.VendorID, device.ProductID);
        MicMatch match = QuadcastDeviceIds.Classify(id, out string? otherMicName);

        // 详细信息只对候选设备读取：用途、打开测试对无关设备没有意义
        bool isCandidate = match != MicMatch.None;
        (string usage, string reportsUseId) = isCandidate ? ReadUsage(device) : ("", "");

        return new HidDeviceInfo(
            Id: id,
            Match: match,
            OtherMicName: otherMicName,
            ProductName: SafeText(device.GetProductName),
            Manufacturer: SafeText(device.GetManufacturer),
            ReleaseNumber: SafeText(() => $"0x{device.ReleaseNumberBcd:X4}"),
            Usage: usage,
            ReportsUseId: reportsUseId,
            InputLength: SafeInt(device.GetMaxInputReportLength),
            OutputLength: SafeInt(device.GetMaxOutputReportLength),
            FeatureLength: SafeInt(device.GetMaxFeatureReportLength),
            OpenTest: isCandidate ? TestOpen(device) : "",
            DevicePath: SafeText(() => device.DevicePath));
    }

    /// <summary>
    /// 读取设备的"用途"（Usage Page / Usage，HID 设备自报的类别）。
    /// 厂商自定义的设备，Usage Page 一般在 0xFF00 以上。
    /// </summary>
    private static (string Usage, string ReportsUseId) ReadUsage(HidDevice device)
    {
        try
        {
            var descriptor = device.GetReportDescriptor();
            uint usage = descriptor.DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .FirstOrDefault();

            string usageText = usage == 0
                ? "（空）"
                : $"Usage Page 0x{usage >> 16:X4} / Usage 0x{usage & 0xFFFF:X4}";
            string reportsUseId = descriptor.ReportsUseID ? "是" : "否";
            return (usageText, reportsUseId);
        }
        catch (Exception ex)
        {
            string unreadable = $"（读不到：{ex.GetType().Name}）";
            return (unreadable, unreadable);
        }
    }

    /// <summary>
    /// 打开测试：只打开一下马上关闭，不发送任何数据。
    /// 用来判断设备有没有被别的程序（比如 NGENUITY）独占。
    /// </summary>
    private static string TestOpen(HidDevice device)
    {
        try
        {
            if (device.TryOpen(new OpenConfiguration(), out DeviceStream stream, out Exception error))
            {
                stream.Dispose();
                return "能打开";
            }

            return error is null ? "打不开（原因未知）" : $"打不开（{Describe(error)}）";
        }
        catch (Exception ex)
        {
            return $"打不开（{Describe(ex)}）";
        }
    }

    // ───────────────────────── 小工具 ─────────────────────────

    /// <summary>候选设备的排序：QuadCast S 排最前，其次其他 HyperX 麦克风，最后厂商相同的设备。</summary>
    private static int DisplayOrder(MicMatch match) => match switch
    {
        MicMatch.QuadcastS => 0,
        MicMatch.OtherHyperXMic => 1,
        MicMatch.SameVendor => 2,
        _ => 3,
    };

    private static string MatchLabel(HidDeviceInfo info) => info.Match switch
    {
        MicMatch.QuadcastS => "★ [QuadCast S 候选]",
        MicMatch.OtherHyperXMic => $"○ [{info.OtherMicName}，不是目标]",
        MicMatch.SameVendor => "△ [厂商编号相同]",
        _ => "",
    };

    private static string LengthText(int? length) => length?.ToString() ?? "读不到";

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

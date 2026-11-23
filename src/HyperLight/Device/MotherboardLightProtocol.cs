namespace HyperLight.Device;

/// <summary>
/// 技嘉主板灯光芯片的协议（ITE IT5702，技嘉叫它 RGB Fusion 2.0）：设备编号、数据包格式、信息包的解读。
/// 全项目只有这里写这块芯片的编号和字节格式（唯一真相来源）。
/// 格式来源：开源项目 OpenRGB 的 GigabyteRGBFusion2USBController。只参考格式这种事实，没有使用它的代码。
/// </summary>
/// <remarks>
/// 通信方式：HID Feature 报告，每个包 64 字节，第 1 个字节固定是报告编号 0xCC（已经算在 64 字节里）。
/// 和麦克风不同，这块芯片的报告编号不是 0，所以包的长度就是 64，不用再加 1。
/// 灯光控制节点的标识：Usage Page 0xFF89，Usage 0xCC。
/// 主板上两个可控的 ARGB 接口，在协议里各有一个通道（M5-1 才会用到）：
///   通道 0x58：主板上印 ARGB_V2_1（OpenRGB 里叫 ARGB_V2_3）—— Luca 的机箱集线板接在这里
///   通道 0x59：主板上印 ARGB_V2_2 —— Luca 的冷排接在这里
/// </remarks>
internal static class MotherboardLightProtocol
{
    /// <summary>ITE 的厂商编号。</summary>
    public const int VendorIte = 0x048D;

    /// <summary>IT5702 灯光芯片。M0 的设备列表里，Luca 的主板有两个这个编号的节点（Feature 17 和 Feature 64）。</summary>
    public static readonly UsbId It5702 = new(VendorIte, 0x5702);

    /// <summary>灯光控制节点的 Usage：高 16 位是 Usage Page 0xFF89，低 16 位是 Usage 0xCC。</summary>
    public const uint ControlUsage = 0xFF8900CC;

    /// <summary>每个包的第 1 个字节：报告编号。</summary>
    public const byte ReportId = 0xCC;

    /// <summary>每个 Feature 报告的总长度（含报告编号）。</summary>
    public const int FeatureReportLength = 64;

    /// <summary>"读取芯片信息"命令。只是查询，不会改变灯光。</summary>
    private const byte infoCommand = 0x60;

    // ───────── 信息包里各字段的位置（从报告编号那个字节开始数） ─────────
    private const int productIndex = 1;
    private const int deviceNumberIndex = 2;
    private const int stripDetectIndex = 3;
    private const int firmwareIndex = 4;
    private const int ledCountIndex = 8;      // 8、9、10 三个字节，每个字节的低 4 位和高 4 位各管一个接口
    private const int supportFlagsIndex = 11;
    private const int productNameIndex = 12;
    private const int productNameLength = 28;
    private const int stripCalibration0Index = 44;  // 通道 0x58 的颜色顺序
    private const int stripCalibration1Index = 48;  // 通道 0x59 的颜色顺序
    private const int chipIdIndex = 56;

    /// <summary>判断一个 HID 节点是不是灯光控制节点。这条规则全项目只写在这里。</summary>
    public static bool IsControlNode(UsbId id, int? featureLength, IReadOnlyCollection<uint> usages) =>
        id == It5702 && featureLength == FeatureReportLength && usages.Contains(ControlUsage);

    /// <summary>生成"读取芯片信息"的请求包。</summary>
    public static byte[] BuildInfoRequest()
    {
        byte[] report = NewReport();
        report[1] = infoCommand;
        return report;
    }

    /// <summary>生成用来接收回复的空包。Windows 要求第 1 个字节先填好报告编号。</summary>
    public static byte[] NewReport()
    {
        byte[] report = new byte[FeatureReportLength];
        report[0] = ReportId;
        return report;
    }

    /// <summary>解读芯片回复的信息包。长度不对或报告编号不对时返回 null。</summary>
    public static MotherboardLightInfo? ParseInfo(byte[] reply)
    {
        if (reply.Length < FeatureReportLength || reply[0] != ReportId)
        {
            return null;
        }

        return new MotherboardLightInfo(
            Product: reply[productIndex],
            DeviceNumber: reply[deviceNumberIndex],
            StripDetect: reply[stripDetectIndex],
            FirmwareVersion: $"{reply[firmwareIndex]}.{reply[firmwareIndex + 1]}.{reply[firmwareIndex + 2]}.{reply[firmwareIndex + 3]}",
            LedCountCodes: reply[ledCountIndex..(ledCountIndex + 3)],
            SupportFlags: reply[supportFlagsIndex],
            ProductName: ReadText(reply, productNameIndex, productNameLength),
            StripCalibration0: BitConverter.ToUInt32(reply, stripCalibration0Index),
            StripCalibration1: BitConverter.ToUInt32(reply, stripCalibration1Index),
            ChipId: BitConverter.ToUInt32(reply, chipIdIndex));
    }

    /// <summary>
    /// 把灯数代码换成灯数上限。代码来自信息包的灯数字节：0=32、1=64、2=256、3=512、4=1024。
    /// 不认识的代码返回 null。
    /// </summary>
    public static int? LedCountFromCode(int code) => code switch
    {
        0 => 32,
        1 => 64,
        2 => 256,
        3 => 512,
        4 => 1024,
        _ => null,
    };

    /// <summary>
    /// 把颜色顺序设置换成文字，比如 "GRB"。
    /// 设置是 4 个字节：第 1 个字节是蓝色排在第几位，第 2 个是绿色，第 3 个是红色（0~2），第 4 个必须是 0。
    /// 例如 0x00010002：蓝色在第 2 位、绿色在第 0 位、红色在第 1 位，也就是 GRB。0 表示这个接口关闭。
    /// 内容不合法时返回 null。
    /// </summary>
    public static string? DescribeColorOrder(uint calibration)
    {
        if (calibration == 0)
        {
            return "关闭";
        }

        int blue = (int)(calibration & 0xFF);
        int green = (int)((calibration >> 8) & 0xFF);
        int red = (int)((calibration >> 16) & 0xFF);
        int reserved = (int)(calibration >> 24);
        if (reserved != 0 || blue > 2 || green > 2 || red > 2 || blue == green || blue == red || green == red)
        {
            return null;
        }

        char[] order = new char[3];
        order[blue] = 'B';
        order[green] = 'G';
        order[red] = 'R';
        return new string(order);
    }

    /// <summary>读一段以 0 结尾的文字（芯片里存的产品名）。</summary>
    private static string ReadText(byte[] data, int start, int length)
    {
        int end = start;
        while (end < start + length && data[end] != 0)
        {
            end++;
        }

        return System.Text.Encoding.ASCII.GetString(data, start, end - start).Trim();
    }
}

/// <summary>主板灯光芯片回复的信息。字段含义见 <see cref="MotherboardLightProtocol"/>。</summary>
/// <param name="Product">产品代码（OpenRGB 没有使用这个字段，只显示出来备查）。</param>
/// <param name="DeviceNumber">设备序号。</param>
/// <param name="StripDetect">灯带检测标志。</param>
/// <param name="FirmwareVersion">固件版本，比如 "8.0.33.1"。</param>
/// <param name="LedCountCodes">3 个灯数字节，每个字节的低 4 位、高 4 位各对应一个接口。</param>
/// <param name="SupportFlags">功能标志。</param>
/// <param name="ProductName">芯片里存的产品名。</param>
/// <param name="StripCalibration0">通道 0x58 的颜色顺序设置。</param>
/// <param name="StripCalibration1">通道 0x59 的颜色顺序设置。</param>
/// <param name="ChipId">芯片编号。</param>
internal sealed record MotherboardLightInfo(
    byte Product,
    byte DeviceNumber,
    byte StripDetect,
    string FirmwareVersion,
    byte[] LedCountCodes,
    byte SupportFlags,
    string ProductName,
    uint StripCalibration0,
    uint StripCalibration1,
    uint ChipId);

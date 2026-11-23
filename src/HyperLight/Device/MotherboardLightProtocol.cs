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
/// 主板上两个可控的 ARGB 接口，在协议里各有一个通道：
///   通道 0x58：主板上印 ARGB_V2_1（OpenRGB 里叫 ARGB_V2_3）—— Luca 的机箱集线板接在这里
///   通道 0x59：主板上印 ARGB_V2_2 —— Luca 的冷排接在这里
/// 发颜色的步骤：先发"关掉自带灯效"（0x32），再按通道发颜色包（每包最多 19 颗灯）。
/// 不使用任何"保存到芯片"的指令（比如 0x5E、0x33），所以断电重启后主板会恢复原样。
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

    /// <summary>每个通道最多能控制多少颗灯（OpenRGB 对这块芯片的上限，M5-0 实测两个通道的上限也是 256）。</summary>
    public const int MaxLedsPerZone = 256;

    /// <summary>
    /// 颜色顺序读不到或不合法时用的默认值：GRB（ARGB 灯最常见的顺序）。
    /// M5-0 实测 Luca 的主板两个通道都是这个值。
    /// </summary>
    public const uint DefaultColorOrder = 0x00010002;

    /// <summary>"读取芯片信息"命令。只是查询，不会改变灯光。</summary>
    private const byte infoCommand = 0x60;

    /// <summary>"自带灯效开关"命令：后面跟一个字节，某一位是 1 表示关掉那个通道的自带灯效。</summary>
    private const byte builtinEffectCommand = 0x32;

    /// <summary>"生效"命令（0x28 FF 00）。M5-1 测试用：确认发完颜色后需不需要它。</summary>
    private const byte applyCommand = 0x28;

    /// <summary>每个颜色包最多装多少颗灯：64 字节里，前 5 字节是包头，后面 19×3 = 57 字节是颜色。</summary>
    private const int ledsPerPacket = 19;

    /// <summary>颜色包里第一颗灯的颜色从第几个字节开始。</summary>
    private const int colorDataIndex = 5;

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

    /// <summary>每个灯区在协议里的通道编号。灯区和接口的对应关系全项目只写在这里。</summary>
    public static byte ChannelOf(MotherboardLightZone zone) => zone switch
    {
        MotherboardLightZone.Case => 0x58,
        MotherboardLightZone.Radiator => 0x59,
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "未知的灯区"),
    };

    /// <summary>"关掉自带灯效"时，每个灯区对应的那一位。</summary>
    private static byte BuiltinEffectBitOf(MotherboardLightZone zone) => zone switch
    {
        MotherboardLightZone.Case => 0x01,
        MotherboardLightZone.Radiator => 0x02,
        _ => throw new ArgumentOutOfRangeException(nameof(zone), zone, "未知的灯区"),
    };

    /// <summary>生成"关掉所有灯区的自带灯效"的请求包。关掉后，芯片才会显示我们发的颜色。</summary>
    public static byte[] BuildDisableBuiltinEffectsRequest()
    {
        byte mask = 0;
        foreach (MotherboardLightZone zone in Enum.GetValues<MotherboardLightZone>())
        {
            mask |= BuiltinEffectBitOf(zone);
        }

        byte[] report = NewReport();
        report[1] = builtinEffectCommand;
        report[2] = mask;
        return report;
    }

    /// <summary>生成"生效"请求包（M5-1 测试用）。</summary>
    public static byte[] BuildApplyRequest()
    {
        byte[] report = NewReport();
        report[1] = applyCommand;
        report[2] = 0xFF;
        return report;
    }

    /// <summary>
    /// 把一个灯区所有灯的颜色拼成颜色包。灯多时会拆成好几个包，每包最多 19 颗。
    /// <paramref name="colorOrder"/> 是这个通道的颜色顺序设置（从芯片信息里读出来的），不合法时按 GRB 处理。
    /// </summary>
    public static List<byte[]> BuildColorReports(MotherboardLightZone zone, IReadOnlyList<Rgb> leds, uint colorOrder)
    {
        if (leds.Count > MaxLedsPerZone)
        {
            throw new ArgumentException($"一个灯区最多 {MaxLedsPerZone} 颗灯，收到了 {leds.Count} 颗", nameof(leds));
        }

        (int red, int green, int blue) = ColorIndicesOf(colorOrder) ?? ColorIndicesOf(DefaultColorOrder)!.Value;
        byte channel = ChannelOf(zone);
        var reports = new List<byte[]>();

        for (int first = 0; first < leds.Count; first += ledsPerPacket)
        {
            int count = Math.Min(ledsPerPacket, leds.Count - first);
            int byteOffset = first * 3;

            byte[] report = NewReport();
            report[1] = channel;
            report[2] = (byte)(byteOffset & 0xFF);   // 这一包从第几个字节开始（低位在前）
            report[3] = (byte)(byteOffset >> 8);
            report[4] = (byte)(count * 3);            // 这一包有多少字节的颜色

            for (int i = 0; i < count; i++)
            {
                Rgb color = leds[first + i];
                int slot = colorDataIndex + i * 3;
                report[slot + red] = color.R;
                report[slot + green] = color.G;
                report[slot + blue] = color.B;
            }

            reports.Add(report);
        }

        return reports;
    }

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

        if (ColorIndicesOf(calibration) is not (int red, int green, int blue))
        {
            return null;
        }

        char[] order = new char[3];
        order[red] = 'R';
        order[green] = 'G';
        order[blue] = 'B';
        return new string(order);
    }

    /// <summary>
    /// 从颜色顺序设置里读出红、绿、蓝各排在第几位（0~2）。设置不合法（包括 0，表示接口关闭）时返回 null。
    /// 这条解读规则全项目只写在这里。
    /// </summary>
    private static (int Red, int Green, int Blue)? ColorIndicesOf(uint calibration)
    {
        int blue = (int)(calibration & 0xFF);
        int green = (int)((calibration >> 8) & 0xFF);
        int red = (int)((calibration >> 16) & 0xFF);
        int reserved = (int)(calibration >> 24);
        if (reserved != 0 || blue > 2 || green > 2 || red > 2 || blue == green || blue == red || green == red)
        {
            return null;
        }

        return (red, green, blue);
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

/// <summary>主板上可以分开控制的灯区。</summary>
internal enum MotherboardLightZone
{
    /// <summary>机箱灯：接在主板 ARGB_V2_1 上的机箱集线板（5 个风扇和灯条）。</summary>
    Case,

    /// <summary>冷排灯：接在主板 ARGB_V2_2 上的水冷。</summary>
    Radiator,
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

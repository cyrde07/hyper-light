namespace HyperLight.Device;

/// <summary>
/// QuadCast S 灯光协议：把颜色拼成要发给麦克风的数据包。
/// 全项目只有这里写字节格式（唯一真相来源）。
/// 格式来源：开源项目 QuadcastRGB 的 modules/devio.c 和 rgbmodes.c。只参考格式这种事实，没有使用它的代码。
/// </summary>
/// <remarks>
/// 每一帧发两个包，都是 HID Feature 报告（发到 USB 接口 0，报告编号 0）：
///   1. 头包：  04 F2 00 00 00 00 00 00 01 00 …（第 9 个字节的 01 表示后面跟 1 个颜色包）
///   2. 颜色包：81 R G B 81 R G B 00 …（前 4 个字节管上灯区，后 4 个字节管下灯区）
/// 麦克风要持续收到数据才会保持颜色，所以调用方要不停地发。
/// M1 实测：停止发送后，麦克风会立刻恢复自带灯效；每秒 30 帧稳定不闪，每帧发送约 11.6 毫秒。
/// Windows 发送 Feature 报告时，第 1 个字节必须是报告编号（这里是 0），
/// 所以每个包实际是 1 + 64 = 65 字节。
/// </remarks>
internal static class QuadcastProtocol
{
    /// <summary>协议里每个包的数据长度（字节）。</summary>
    public const int PacketSize = 64;

    /// <summary>
    /// Windows 上一个 Feature 报告的总长度：1 字节报告编号 + 64 字节数据。
    /// 麦克风的灯光控制节点，Feature 长度必须等于它（M0 实测：03F0:028C 的接口 0 正好是 65）。
    /// </summary>
    public const int FeatureReportLength = PacketSize + 1;

    /// <summary>参考项目每帧之间等待的时间（毫秒），大约每秒 18 帧。</summary>
    public const int ReferenceFrameIntervalMs = 55;

    private const byte reportId = 0x00;
    private const byte headerCode = 0x04;
    private const byte displayCode = 0xF2;
    private const byte colorPacketCount = 0x01;
    private const byte rgbCode = 0x81;

    // 以下位置都是"数据部分"里的位置，不算最前面那 1 字节报告编号
    private const int headerPacketCountIndex = 8;
    private const int upperZoneIndex = 0;
    private const int lowerZoneIndex = 4;

    /// <summary>生成头包（65 字节，含报告编号）。内容固定不变。</summary>
    public static byte[] BuildHeaderReport()
    {
        byte[] report = NewReport();
        report[1 + 0] = headerCode;
        report[1 + 1] = displayCode;
        report[1 + headerPacketCountIndex] = colorPacketCount;
        return report;
    }

    /// <summary>生成颜色包（65 字节，含报告编号）。</summary>
    public static byte[] BuildColorReport(Rgb upper, Rgb lower)
    {
        byte[] report = NewReport();
        WriteZone(report, 1 + upperZoneIndex, upper);
        WriteZone(report, 1 + lowerZoneIndex, lower);
        return report;
    }

    private static byte[] NewReport()
    {
        byte[] report = new byte[FeatureReportLength];
        report[0] = reportId;
        return report;
    }

    private static void WriteZone(byte[] report, int index, Rgb color)
    {
        report[index] = rgbCode;
        report[index + 1] = color.R;
        report[index + 2] = color.G;
        report[index + 3] = color.B;
    }
}

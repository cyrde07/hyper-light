namespace HyperLight.Device;

/// <summary>
/// 显卡灯光芯片的协议（华硕 ROG STRIX RTX 4090 白色版上的 ENE 灯光芯片）：认哪张卡、芯片在哪、寄存器、颜色格式。
/// 全项目只有这里写这些编号和格式（唯一真相来源）。
/// 来源：开源项目 OpenRGB 的 ENESMBusController 和 pci_ids。只参考这类事实，没有使用它的代码。
/// </summary>
/// <remarks>
/// 芯片挂在显卡 I2C 端口 1、地址 0x67。
/// 芯片的寄存器编号是 2 字节（比如 0x8020），访问方法：
///   读：先往寄存器 0x00 写 2 字节"位置"（高字节在前），再读寄存器 0x81。
///   写：先往寄存器 0x00 写"位置"，再往寄存器 0x01 写 1 字节（G1 才会用到）。
/// "写位置"只是告诉芯片接下来读写哪里，不改任何设置。
/// 绝不写"保存设置"（往 0x80A0 写 0xAA），所以断电重启后显卡恢复原样。
/// </remarks>
internal static class GpuLightProtocol
{
    /// <summary>NVIDIA 的厂商编号。</summary>
    public const ushort VendorNvidia = 0x10DE;

    /// <summary>RTX 4090 的芯片编号。</summary>
    public const ushort DeviceRtx4090 = 0x2684;

    /// <summary>华硕的板卡厂商编号。</summary>
    public const ushort SubVendorAsus = 0x1043;

    /// <summary>
    /// 支持的板卡型号（华硕 ROG STRIX RTX 4090 白色版的几个批次，OpenRGB 的 pci_ids 里有登记）。
    /// 只有对得上的显卡才会去碰它的灯光芯片。
    /// </summary>
    private static readonly ushort[] supportedSubDevices =
    {
        0x88C3, // ROG STRIX RTX 4090 O24G GAMING WHITE（白色 OC）
        0x88F1, // 同上，另一批次
        0x88C4, // ROG STRIX RTX 4090 24G GAMING WHITE（白色非 OC）
        0x88F2, // 同上，另一批次
    };

    /// <summary>灯光芯片所在的 I2C 端口。</summary>
    public const byte I2cPort = 1;

    /// <summary>灯光芯片的 I2C 地址。</summary>
    public const byte ChipAddress = 0x67;

    /// <summary>认芯片检查：寄存器 0xA0～0xAF 读出来必须正好是 0～15（ENE 芯片的特征）。</summary>
    public const byte SignatureFirstRegister = 0xA0;

    /// <summary>认芯片检查要读的寄存器个数。</summary>
    public const int SignatureLength = 16;

    /// <summary>"写位置"用的寄存器。</summary>
    public const byte AddressRegister = 0x00;

    /// <summary>"读一个字节"用的寄存器。</summary>
    public const byte ReadRegister = 0x81;

    /// <summary>芯片型号文字（16 字节）。</summary>
    public const ushort DeviceNameRegister = 0x1000;

    /// <summary>芯片型号文字的长度。</summary>
    public const int DeviceNameLength = 16;

    /// <summary>配置表（64 字节），里面有灯珠数。</summary>
    public const ushort ConfigTableRegister = 0x1C00;

    /// <summary>配置表的长度。</summary>
    public const int ConfigTableLength = 64;

    /// <summary>"直接控制模式"开关：1 是开，0 是关（回到显卡自己的灯效）。</summary>
    public const ushort DirectModeRegister = 0x8020;

    /// <summary>显卡自己的灯效种类（0 关、1 静态、2 呼吸……）。</summary>
    public const ushort EffectModeRegister = 0x8021;

    /// <summary>
    /// 这些型号的芯片，灯珠数在配置表第 0x03 个字节；其他型号在第 0x02 个字节（OpenRGB 的规则）。
    /// AUMA0-E6K5-1111 是 OpenRGB 记录的 ROG STRIX 4090 的芯片。
    /// </summary>
    private static readonly string[] ledCountAtOffset3 =
    {
        "AUMA0-E6K5-0107", "AUMA0-E6K5-1111", "AUMA0-E6K5-1107", "AUMA0-E6K5-0008",
        "AUMA0-E6K5-1113", "AUMA0-E6K5-1114", "AUMA0-E6K5-1110",
    };

    /// <summary>是不是支持的显卡（RTX 4090 + 华硕 ROG STRIX 白色版）。这条规则全项目只写在这里。</summary>
    public static bool IsSupportedCard(NvApi.PciIds ids) =>
        ids.Vendor == VendorNvidia
        && ids.Device == DeviceRtx4090
        && ids.SubVendor == SubVendorAsus
        && supportedSubDevices.Contains(ids.SubDevice);

    /// <summary>认芯片检查：寄存器 0xA0～0xAF 读出来的 16 个值，是不是正好是 0～15。</summary>
    public static bool IsEneSignature(ReadOnlySpan<byte> values)
    {
        if (values.Length != SignatureLength)
        {
            return false;
        }

        for (int i = 0; i < SignatureLength; i++)
        {
            if (values[i] != i)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"写位置"要发的 2 个字节：寄存器编号的高字节在前（0x8020 → 80 20）。</summary>
    public static byte[] EncodeRegisterAddress(ushort register) => new[] { (byte)(register >> 8), (byte)(register & 0xFF) };

    /// <summary>
    /// 按 OpenRGB 的规则从配置表读灯珠数：列表里的型号看第 0x03 个字节，其他 AUMA0 型号看第 0x02 个字节。
    /// OpenRGB 只认它收录过的型号，这里对没收录的 AUMA0 型号也给一个参考值（不一定准，G1 测试时确认）。
    /// 不是 AUMA0 型号返回 null。
    /// </summary>
    public static int? LedCountOf(string deviceName, ReadOnlySpan<byte> configTable)
    {
        if (configTable.Length < ConfigTableLength || !deviceName.StartsWith("AUMA0-", StringComparison.Ordinal))
        {
            return null;
        }

        int offset = ledCountAtOffset3.Contains(deviceName) ? 0x03 : 0x02;
        return configTable[offset];
    }
}

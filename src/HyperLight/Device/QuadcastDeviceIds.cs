namespace HyperLight.Device;

/// <summary>一个设备和 HyperX 麦克风的匹配程度。</summary>
internal enum MicMatch
{
    /// <summary>无关设备。</summary>
    None,

    /// <summary>已知的 QuadCast S 编号，本工具的目标。</summary>
    QuadcastS,

    /// <summary>
    /// 已知的其他 HyperX 麦克风设备：DuoCast、QuadCast 2 等协议不同的型号，
    /// 以及 QuadCast S 自己的音频部分（不管灯光）。都不是目标，标出来是为了防止认错。
    /// </summary>
    OtherHyperXMic,

    /// <summary>厂商编号对得上（金士顿或 HP），但产品编号不在表里。可能是没收录的新批次，也可能是 HP 的其他产品。</summary>
    SameVendor,
}

/// <summary>
/// HyperX 麦克风的设备编号表。
/// 全项目只有这里写设备编号，其他地方一律来这里查（唯一真相来源）。
/// 编号来源：开源项目 QuadcastRGB 的 modules/devio.c。只参考编号这种事实，没有使用它的代码。
/// </summary>
internal static class QuadcastDeviceIds
{
    /// <summary>金士顿的厂商编号。HyperX 被 HP 收购之前的产品用这个。</summary>
    public const int VendorKingston = 0x0951;

    /// <summary>HP 的厂商编号。HP 收购 HyperX 之后的新批次用这个。</summary>
    public const int VendorHp = 0x03F0;

    /// <summary>
    /// QuadCast S 可能用的编号。
    /// HP 的四个编号在源码里没有逐个注明型号，按源码的排列推测是 QuadCast S 的不同批次。
    /// M0 实测：Luca 的麦克风是 03F0:028C（固件版本 0x6103）。
    /// </summary>
    private static readonly UsbId[] quadcastSIds =
    {
        new(VendorKingston, 0x171F),
        new(VendorHp, 0x0F8B),
        new(VendorHp, 0x028C),
        new(VendorHp, 0x048C),
        new(VendorHp, 0x068C),
    };

    /// <summary>其他 HyperX 麦克风设备的编号和名字。</summary>
    private static readonly Dictionary<UsbId, string> otherMicNames = new()
    {
        // M0 实测：和 03F0:028C 同时出现，名字也叫 QuadCast S，但没有 Feature 65 的节点，推测是负责声音的芯片
        [new(VendorHp, 0x0294)] = "QuadCast S 的音频部分（不管灯光）",

        // 以下三个是 QuadcastRGB 源码注释里写明了型号的
        [new(VendorHp, 0x098C)] = "DuoCast",
        [new(VendorHp, 0x09AF)] = "QuadCast 2",
        [new(VendorHp, 0x02B5)] = "QuadCast 2S",
    };

    /// <summary>是不是已知的 QuadCast S 编号。</summary>
    public static bool IsQuadcastS(UsbId id) => quadcastSIds.Contains(id);

    /// <summary>
    /// 判断一个设备编号和 HyperX 麦克风的匹配程度。
    /// 只有返回 <see cref="MicMatch.OtherHyperXMic"/> 时，<paramref name="otherMicName"/> 才有值。
    /// </summary>
    public static MicMatch Classify(UsbId id, out string? otherMicName)
    {
        otherMicName = null;

        if (IsQuadcastS(id))
        {
            return MicMatch.QuadcastS;
        }

        if (otherMicNames.TryGetValue(id, out string? name))
        {
            otherMicName = name;
            return MicMatch.OtherHyperXMic;
        }

        if (id.VendorId is VendorKingston or VendorHp)
        {
            return MicMatch.SameVendor;
        }

        return MicMatch.None;
    }
}

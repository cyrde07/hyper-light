namespace HyperLight.Device;

/// <summary>
/// 显卡灯光芯片的连接：找到支持的显卡、做认芯片检查、读写芯片寄存器。
/// G0 只有读：认芯片检查、读芯片型号和配置表。发颜色在 G1 加。
/// 同一时间只能有一个线程使用它（不是线程安全的）。
/// </summary>
internal sealed class GpuLight
{
    private readonly NvApi api;
    private readonly IntPtr gpu;

    private GpuLight(NvApi api, IntPtr gpu, NvApi.PciIds ids)
    {
        this.api = api;
        this.gpu = gpu;
        Ids = ids;
    }

    /// <summary>这张显卡的 PCI 编号。</summary>
    public NvApi.PciIds Ids { get; }

    /// <summary>
    /// 在所有 NVIDIA 显卡里找支持的那张（编号对得上才算）。找不到返回 null，原因放在 <paramref name="error"/>。
    /// 只核对编号，不碰灯光芯片；碰芯片之前还要过 <see cref="TryCheckSignature"/>。
    /// </summary>
    public static GpuLight? TryFind(NvApi api, out string? error)
    {
        IReadOnlyList<IntPtr> gpus = api.EnumPhysicalGpus(out int status);
        if (status != NvApi.Ok)
        {
            error = $"列出 NVIDIA 显卡失败（状态码 {status}）";
            return null;
        }

        foreach (IntPtr gpu in gpus)
        {
            if (api.GetPciIdentifiers(gpu, out NvApi.PciIds ids) == NvApi.Ok && GpuLightProtocol.IsSupportedCard(ids))
            {
                error = null;
                return new GpuLight(api, gpu, ids);
            }
        }

        error = "没有找到支持的显卡（只支持华硕 ROG STRIX RTX 4090 白色版）";
        return null;
    }

    /// <summary>
    /// 认芯片检查：读寄存器 0xA0～0xAF，必须正好是 0～15。读到的值放在 <paramref name="values"/>（读失败的位置是 null）。
    /// 这一步只读，不写任何东西。通过了才能读写芯片的其他寄存器。
    /// </summary>
    public bool TryCheckSignature(out byte?[] values)
    {
        values = new byte?[GpuLightProtocol.SignatureLength];
        var read = new byte[GpuLightProtocol.SignatureLength];
        bool allRead = true;
        for (int i = 0; i < GpuLightProtocol.SignatureLength; i++)
        {
            if (TryReadByteData((byte)(GpuLightProtocol.SignatureFirstRegister + i), out byte value))
            {
                values[i] = value;
                read[i] = value;
            }
            else
            {
                allRead = false;
            }
        }

        return allRead && GpuLightProtocol.IsEneSignature(read);
    }

    /// <summary>
    /// 读芯片的一段寄存器（比如芯片型号、配置表）。任何一个字节读失败就返回 null。
    /// 调用前必须先过认芯片检查。
    /// </summary>
    public byte[]? TryReadRegisters(ushort firstRegister, int length)
    {
        var result = new byte[length];
        for (int i = 0; i < length; i++)
        {
            if (!TryReadRegister((ushort)(firstRegister + i), out result[i]))
            {
                return null;
            }
        }

        return result;
    }

    /// <summary>读芯片的一个寄存器：先"写位置"，再读一个字节。调用前必须先过认芯片检查。</summary>
    public bool TryReadRegister(ushort register, out byte value)
    {
        value = 0;
        int status = api.I2cWrite(
            gpu, GpuLightProtocol.I2cPort, GpuLightProtocol.ChipAddress,
            GpuLightProtocol.AddressRegister, GpuLightProtocol.EncodeRegisterAddress(register));
        return status == NvApi.Ok && TryReadByteData(GpuLightProtocol.ReadRegister, out value);
    }

    /// <summary>读一个 1 字节寄存器（普通的 SMBus 读，不写任何东西）。</summary>
    private bool TryReadByteData(byte register, out byte value)
    {
        Span<byte> buffer = stackalloc byte[1];
        int status = api.I2cRead(gpu, GpuLightProtocol.I2cPort, GpuLightProtocol.ChipAddress, register, buffer);
        value = buffer[0];
        return status == NvApi.Ok;
    }
}

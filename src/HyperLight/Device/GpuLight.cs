namespace HyperLight.Device;

/// <summary>
/// 显卡灯光芯片的连接：找到支持的显卡、做认芯片检查、读写芯片寄存器、发颜色。
/// 安全规则：认芯片检查没通过之前，任何写入都会被拒绝；绝不写"保存设置"。
/// 同一时间只能有一个线程使用它（不是线程安全的）。
/// </summary>
internal sealed class GpuLight
{
    private readonly NvApi api;
    private readonly IntPtr gpu;

    /// <summary>认芯片检查通过了没有。没通过时拒绝一切写入。</summary>
    private bool chipVerified;

    private GpuLight(NvApi api, IntPtr gpu, NvApi.PciIds ids)
    {
        this.api = api;
        this.gpu = gpu;
        Ids = ids;
    }

    /// <summary>这张显卡的 PCI 编号。</summary>
    public NvApi.PciIds Ids { get; }

    /// <summary>灯珠数（<see cref="TryConnect"/> 成功后才有）。</summary>
    public int LedCount { get; private set; }

    /// <summary>最近一次失败的原因，给界面显示用。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 连接显卡灯：载入 NVIDIA 驱动 → 找支持的显卡 → 认芯片检查 → 读芯片型号和灯珠数。
    /// 全部通过才返回连接，否则返回 null，原因放在 <paramref name="error"/>。
    /// </summary>
    public static GpuLight? TryConnect(out string? error)
    {
        NvApi? api = NvApi.TryLoad(out error);
        if (api is null)
        {
            return null;
        }

        GpuLight? light = TryFind(api, out error);
        if (light is null)
        {
            return null;
        }

        if (!light.TryCheckSignature(out _))
        {
            error = "显卡上的灯光芯片没通过检查（不是预期的芯片，或者读不到），为了安全不控制它";
            return null;
        }

        byte[]? nameBytes = light.TryReadRegisters(GpuLightProtocol.DeviceNameRegister, GpuLightProtocol.DeviceNameLength);
        byte[]? config = light.TryReadRegisters(GpuLightProtocol.ConfigTableRegister, GpuLightProtocol.ConfigTableLength);
        if (nameBytes is null || config is null)
        {
            error = "读不到显卡灯光芯片的型号或配置";
            return null;
        }

        string deviceName = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0', ' ');
        if (GpuLightProtocol.LedCountOf(deviceName, config) is not int ledCount || ledCount < 1 || ledCount > GpuLightProtocol.MaxLeds)
        {
            error = $"显卡灯光芯片（{deviceName}）报告的灯珠数不在 1～{GpuLightProtocol.MaxLeds} 之间，为了安全不控制它";
            return null;
        }

        light.LedCount = ledCount;
        error = null;
        return light;
    }

    /// <summary>
    /// 打开或关闭直接控制模式，然后"生效"。打开后芯片显示我们写的颜色；关闭后显卡回到自己的灯效。
    /// </summary>
    public bool TrySetDirectMode(bool enabled) =>
        TryWriteRegister(GpuLightProtocol.DirectModeRegister, enabled ? (byte)1 : (byte)0)
        && TryWriteRegister(GpuLightProtocol.ApplyRegister, GpuLightProtocol.ApplyValue);

    /// <summary>
    /// 检查显卡灯是不是还是我们设的样子：直接控制模式还开着，第 1 颗灯的颜色还是 <paramref name="expected"/>。
    /// 只读，不写任何东西（读之前的"写位置"不改设置）。读失败时返回 null（连接可能断了）。
    /// 用来发现别的程序（比如 Armoury Crate 的后台服务）把显卡灯改掉了。
    /// </summary>
    public bool? TryCheckStillOurs(Rgb expected)
    {
        if (!TryReadRegister(GpuLightProtocol.DirectModeRegister, out byte direct))
        {
            return null;
        }

        Span<byte> expectedBytes = stackalloc byte[3];
        GpuLightProtocol.WriteColor(expectedBytes, expected);
        for (int i = 0; i < 3; i++)
        {
            if (!TryReadRegister((ushort)(GpuLightProtocol.DirectColorsRegister + i), out byte actual))
            {
                return null;
            }

            if (actual != expectedBytes[i])
            {
                return false;
            }
        }

        return direct != 0;
    }

    /// <summary>把所有灯设成同一个颜色（要先打开直接控制模式）。</summary>
    public bool TrySetColor(Rgb color)
    {
        if (LedCount < 1)
        {
            LastError = "还没有连接显卡灯（灯珠数未知）";
            return false;
        }

        Span<byte> block = stackalloc byte[GpuLightProtocol.LedsPerBlock * 3];
        for (int first = 0; first < LedCount; first += GpuLightProtocol.LedsPerBlock)
        {
            int count = Math.Min(GpuLightProtocol.LedsPerBlock, LedCount - first);
            for (int i = 0; i < count; i++)
            {
                GpuLightProtocol.WriteColor(block.Slice(i * 3, 3), color);
            }

            ushort register = (ushort)(GpuLightProtocol.DirectColorsRegister + (first * 3));
            if (!TryWriteBlock(register, block[..(count * 3)]))
            {
                return false;
            }
        }

        return true;
    }

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

        chipVerified = allRead && GpuLightProtocol.IsEneSignature(read);
        return chipVerified;
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

    /// <summary>读芯片的一个寄存器：先"写位置"，再读一个字节。认芯片检查没通过时拒绝。</summary>
    public bool TryReadRegister(ushort register, out byte value)
    {
        value = 0;
        return EnsureVerified() && TryWriteAddress(register) && TryReadByteData(GpuLightProtocol.ReadRegister, out value);
    }

    /// <summary>写芯片的一个寄存器：先"写位置"，再写一个字节。认芯片检查没通过时拒绝。</summary>
    private bool TryWriteRegister(ushort register, byte value)
    {
        if (!EnsureVerified() || !TryWriteAddress(register))
        {
            return false;
        }

        ReadOnlySpan<byte> data = stackalloc byte[] { value };
        return Check(api.I2cWrite(gpu, GpuLightProtocol.I2cPort, GpuLightProtocol.ChipAddress, GpuLightProtocol.WriteRegister, data), $"写寄存器 0x{register:X4}");
    }

    /// <summary>
    /// 从一个寄存器开始连续写好几个字节：先"写位置"，再"块写入"（第 1 个字节是长度）。
    /// 块写入失败时，改成一个字节一个字节地写（OpenRGB 也是这样退一步）。认芯片检查没通过时拒绝。
    /// </summary>
    private bool TryWriteBlock(ushort register, ReadOnlySpan<byte> data)
    {
        if (!EnsureVerified() || !TryWriteAddress(register))
        {
            return false;
        }

        Span<byte> block = stackalloc byte[data.Length + 1];
        block[0] = (byte)data.Length;
        data.CopyTo(block[1..]);
        if (api.I2cWrite(gpu, GpuLightProtocol.I2cPort, GpuLightProtocol.ChipAddress, GpuLightProtocol.BlockWriteRegister, block) == NvApi.Ok)
        {
            return true;
        }

        for (int i = 0; i < data.Length; i++)
        {
            if (!TryWriteRegister((ushort)(register + i), data[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"写位置"：告诉芯片接下来读写哪个寄存器。</summary>
    private bool TryWriteAddress(ushort register) =>
        Check(
            api.I2cWrite(gpu, GpuLightProtocol.I2cPort, GpuLightProtocol.ChipAddress,
                GpuLightProtocol.AddressRegister, GpuLightProtocol.EncodeRegisterAddress(register)),
            $"指定位置 0x{register:X4}");

    private bool EnsureVerified()
    {
        if (!chipVerified)
        {
            LastError = "灯光芯片还没通过检查，拒绝写入";
        }

        return chipVerified;
    }

    /// <summary>检查状态码：失败时记下原因。</summary>
    private bool Check(int status, string action)
    {
        if (status == NvApi.Ok)
        {
            return true;
        }

        LastError = $"{action}失败（状态码 {status}）";
        return false;
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

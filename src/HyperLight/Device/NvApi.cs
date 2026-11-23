using System.Runtime.InteropServices;

namespace HyperLight.Device;

/// <summary>
/// NVIDIA 显卡驱动自带的 nvapi64.dll：找显卡、读显卡编号、读写显卡上的 I2C 小线路（显卡灯光芯片挂在上面）。
/// 这些函数 NVIDIA 没有公开，要用"函数编号"向驱动查询（nvapi_QueryInterface）。
/// 函数编号和数据结构来源：开源项目 OpenRGB 的 dependencies/NVFC/nvapi 和 i2c_smbus_nvapi。只参考这类事实，没有使用它的代码。
/// </summary>
/// <remarks>
/// 不需要管理员权限，也不用另外装驱动。驱动没装或太旧时，<see cref="TryLoad"/> 返回 null 并说明原因。
/// 所有函数返回 NVIDIA 的状态码：0 表示成功，其他是错误码。
/// 同一时间只能有一个线程使用它（不是线程安全的）。
/// </remarks>
internal sealed unsafe class NvApi
{
    /// <summary>NVIDIA 状态码：成功。</summary>
    public const int Ok = 0;

    /// <summary>驱动最多返回多少张显卡。</summary>
    private const int maxPhysicalGpus = 64;

    // 函数编号（向驱动查询函数地址用）
    private const uint initializeId = 0x0150E828;
    private const uint enumPhysicalGpusId = 0xE5AC921F;
    private const uint getPciIdentifiersId = 0x2DDFB66E;
    private const uint i2cWriteExId = 0x283AC65A;
    private const uint i2cReadExId = 0x4D7B0709;

    /// <summary>I2C 速度：0xFFFF 表示用默认值（和 OpenRGB 一样）。</summary>
    private const uint defaultI2cSpeed = 0xFFFF;

    private readonly delegate* unmanaged[Cdecl]<int> initialize;
    private readonly delegate* unmanaged[Cdecl]<IntPtr*, uint*, int> enumPhysicalGpus;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, uint*, int> getPciIdentifiers;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, I2cInfoV3*, uint*, int> i2cWriteEx;
    private readonly delegate* unmanaged[Cdecl]<IntPtr, I2cInfoV3*, uint*, int> i2cReadEx;

    private NvApi(IntPtr initialize, IntPtr enumPhysicalGpus, IntPtr getPciIdentifiers, IntPtr i2cWriteEx, IntPtr i2cReadEx)
    {
        this.initialize = (delegate* unmanaged[Cdecl]<int>)initialize;
        this.enumPhysicalGpus = (delegate* unmanaged[Cdecl]<IntPtr*, uint*, int>)enumPhysicalGpus;
        this.getPciIdentifiers = (delegate* unmanaged[Cdecl]<IntPtr, uint*, uint*, uint*, uint*, int>)getPciIdentifiers;
        this.i2cWriteEx = (delegate* unmanaged[Cdecl]<IntPtr, I2cInfoV3*, uint*, int>)i2cWriteEx;
        this.i2cReadEx = (delegate* unmanaged[Cdecl]<IntPtr, I2cInfoV3*, uint*, int>)i2cReadEx;
    }

    /// <summary>显卡的 PCI 编号：芯片厂商、芯片型号、板卡厂商（子系统厂商）、板卡型号（子系统型号）。</summary>
    public readonly record struct PciIds(ushort Vendor, ushort Device, ushort SubVendor, ushort SubDevice)
    {
        public override string ToString() => $"{Vendor:X4}:{Device:X4}（板卡 {SubVendor:X4}:{SubDevice:X4}）";
    }

    /// <summary>
    /// 载入 nvapi64.dll，查到要用的几个函数，并初始化。失败时返回 null，原因放在 <paramref name="error"/>。
    /// </summary>
    public static NvApi? TryLoad(out string? error)
    {
        try
        {
            IntPtr init = QueryInterface(initializeId);
            IntPtr enumGpus = QueryInterface(enumPhysicalGpusId);
            IntPtr pciIds = QueryInterface(getPciIdentifiersId);
            IntPtr write = QueryInterface(i2cWriteExId);
            IntPtr read = QueryInterface(i2cReadExId);
            if (init == IntPtr.Zero || enumGpus == IntPtr.Zero || pciIds == IntPtr.Zero || write == IntPtr.Zero || read == IntPtr.Zero)
            {
                error = "NVIDIA 驱动里找不到需要的函数（驱动版本可能不支持）";
                return null;
            }

            var api = new NvApi(init, enumGpus, pciIds, write, read);
            int status = api.initialize();
            if (status != Ok)
            {
                error = $"NVIDIA 驱动初始化失败（状态码 {status}）";
                return null;
            }

            error = null;
            return api;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = $"找不到 NVIDIA 驱动的 nvapi64.dll（{ex.GetType().Name}）。没装 NVIDIA 显卡驱动时会这样";
            return null;
        }
    }

    /// <summary>列出所有 NVIDIA 显卡。失败时返回空列表，状态码放在 <paramref name="status"/>。</summary>
    public IReadOnlyList<IntPtr> EnumPhysicalGpus(out int status)
    {
        IntPtr* handles = stackalloc IntPtr[maxPhysicalGpus];
        uint count = 0;
        status = enumPhysicalGpus(handles, &count);
        if (status != Ok)
        {
            return Array.Empty<IntPtr>();
        }

        var gpus = new List<IntPtr>();
        for (int i = 0; i < Math.Min(count, (uint)maxPhysicalGpus); i++)
        {
            gpus.Add(handles[i]);
        }

        return gpus;
    }

    /// <summary>读一张显卡的 PCI 编号。成功返回 0。</summary>
    public int GetPciIdentifiers(IntPtr gpu, out PciIds ids)
    {
        uint deviceId = 0, subSystemId = 0, revisionId = 0, extDeviceId = 0;
        int status = getPciIdentifiers(gpu, &deviceId, &subSystemId, &revisionId, &extDeviceId);

        // 高 16 位是型号，低 16 位是厂商（OpenRGB 的拆法）
        ids = new PciIds(
            Vendor: (ushort)(deviceId & 0xFFFF),
            Device: (ushort)(deviceId >> 16),
            SubVendor: (ushort)(subSystemId & 0xFFFF),
            SubDevice: (ushort)(subSystemId >> 16));
        return status;
    }

    /// <summary>
    /// 从 I2C 设备读数据。<paramref name="register"/> 是要读的寄存器（1 字节），null 表示不带寄存器直接读。成功返回 0。
    /// </summary>
    /// <param name="gpu">显卡。</param>
    /// <param name="port">显卡上的 I2C 端口（灯光芯片在端口 1）。</param>
    /// <param name="address">I2C 设备地址（7 位，比如 0x67）。</param>
    /// <param name="register">寄存器，null 表示不带。</param>
    /// <param name="data">读到的数据放这里，长度就是要读的字节数。</param>
    public int I2cRead(IntPtr gpu, byte port, byte address, byte? register, Span<byte> data) =>
        Transfer(i2cReadEx, gpu, port, address, register, data);

    /// <summary>
    /// 向 I2C 设备写数据。<paramref name="register"/> 是要写的寄存器（1 字节），null 表示不带寄存器直接写。成功返回 0。
    /// 参数同 <see cref="I2cRead"/>。
    /// </summary>
    public int I2cWrite(IntPtr gpu, byte port, byte address, byte? register, ReadOnlySpan<byte> data)
    {
        // 驱动的函数要的是可写的指针，复制一份再传，调用方的数据不会被改
        Span<byte> copy = stackalloc byte[data.Length];
        data.CopyTo(copy);
        return Transfer(i2cWriteEx, gpu, port, address, register, copy);
    }

    private static int Transfer(
        delegate* unmanaged[Cdecl]<IntPtr, I2cInfoV3*, uint*, int> function,
        IntPtr gpu, byte port, byte address, byte? register, Span<byte> data)
    {
        byte registerByte = register ?? 0;
        uint unknown = 0;
        fixed (byte* dataPointer = data)
        {
            var info = new I2cInfoV3
            {
                Version = (3u << 16) | (uint)sizeof(I2cInfoV3),
                IsDdcPort = 0,
                DeviceAddress = (byte)(address << 1),   // 驱动要的是左移一位的地址（0x67 → 0xCE）
                RegisterAddress = register is null ? null : &registerByte,
                RegisterAddressSize = register is null ? 0u : 1u,
                Data = dataPointer,
                Size = (uint)data.Length,
                Speed = defaultI2cSpeed,
                SpeedKhz = 0,
                PortId = port,
                IsPortIdSet = 1,
            };
            return function(gpu, &info, &unknown);
        }
    }

    /// <summary>
    /// 驱动的 I2C 传输参数（NV_I2C_INFO_V3）。64 位程序里一共 0x40 字节，按普通对齐排列。
    /// 字段顺序必须和驱动的定义完全一样。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct I2cInfoV3
    {
        public uint Version;
        public uint DisplayMask;
        public byte IsDdcPort;
        public byte DeviceAddress;
        public byte* RegisterAddress;
        public uint RegisterAddressSize;
        public byte* Data;
        public uint Size;
        public uint Speed;
        public uint SpeedKhz;
        public byte PortId;
        public uint IsPortIdSet;
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);
}

namespace HyperLight.Device;

/// <summary>
/// USB 设备编号：VID（Vendor ID，厂商编号）+ PID（Product ID，产品编号）。
/// 显示格式和设备管理器、QuadcastRGB 一致，例如 "0951:171F"。
/// </summary>
internal readonly record struct UsbId(int VendorId, int ProductId)
{
    public override string ToString() => $"{VendorId:X4}:{ProductId:X4}";
}

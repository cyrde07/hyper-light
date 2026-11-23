using System.ComponentModel;
using HidSharp;

namespace HyperLight.Device;

/// <summary>
/// 技嘉主板灯光芯片的连接：找到灯光控制节点、打开、读取颜色顺序、关掉自带灯效、发颜色、断开后自动重连。
/// 只管"发一次"，不管"多久发一次"，节奏由调用方决定。
/// 同一时间只能有一个线程使用它（不是线程安全的）。
/// </summary>
/// <remarks>
/// 每次连上芯片时会做两件事：
///   1. 读取芯片信息，拿到每个通道的颜色顺序（Luca 的主板两个通道都是 GRB）
///   2. 关掉自带灯效，不然芯片不显示我们发的颜色
/// 不会往芯片里保存任何东西，断电重启后主板恢复原样。
/// HidSharp 的异常行为和 <see cref="QuadcastMic"/> 一样：发送失败抛 IOException，连接已关闭抛 ObjectDisposedException，
/// 打开失败抛 IOException 或 UnauthorizedAccessException。
/// </remarks>
internal sealed class MotherboardLight : IDisposable
{
    /// <summary>没连上时，两次尝试连接之间至少隔多久。</summary>
    private static readonly TimeSpan reconnectInterval = TimeSpan.FromSeconds(1);

    private HidStream? stream;
    private DateTime nextConnectAttemptUtc = DateTime.MinValue;
    private bool disposed;

    /// <summary>当前连接的芯片信息。没连上时为 null。</summary>
    public MotherboardLightInfo? Info { get; private set; }

    /// <summary>最近一次连接失败或发送失败的原因，给界面显示用。连接成功后清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前有没有连上芯片。</summary>
    public bool IsConnected => stream is not null;

    /// <summary>
    /// 发一个灯区所有灯的颜色（第 1 颗灯是列表里的第 1 个颜色，依此类推）。没连上时会先尝试连接（受重连间隔限制）。
    /// 设备方面的问题不会抛出异常，结果看返回值，原因看 <see cref="LastError"/>。
    /// 灯数超过 <see cref="MotherboardLightProtocol.MaxLedsPerZone"/> 属于调用方的错误，会抛出 ArgumentException。
    /// </summary>
    public SendResult SendZone(MotherboardLightZone zone, IReadOnlyList<Rgb> leds)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        HidStream? current = stream ?? TryConnect();
        if (current is null || Info is null)
        {
            return SendResult.NotConnected;
        }

        uint colorOrder = MotherboardLightProtocol.ColorOrderOf(Info, zone);
        List<byte[]> reports = MotherboardLightProtocol.BuildColorReports(zone, leds, colorOrder);
        return Send(current, reports);
    }

    /// <summary>发"生效"指令（M5-1 测试用，确认需不需要它）。</summary>
    public SendResult SendApply()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        HidStream? current = stream ?? TryConnect();
        if (current is null)
        {
            return SendResult.NotConnected;
        }

        return Send(current, new[] { MotherboardLightProtocol.BuildApplyRequest() });
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CloseStream();
    }

    private SendResult Send(HidStream current, IEnumerable<byte[]> reports)
    {
        try
        {
            foreach (byte[] report in reports)
            {
                current.SetFeature(report);
            }

            return SendResult.Sent;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = $"发送失败：{ex.Message}";
            CloseStream();
            return SendResult.Failed;
        }
    }

    /// <summary>尝试连接芯片。成功返回打开的连接；失败返回 null，原因写进 <see cref="LastError"/>。</summary>
    private HidStream? TryConnect()
    {
        DateTime now = DateTime.UtcNow;
        if (now < nextConnectAttemptUtc)
        {
            return null;
        }

        nextConnectAttemptUtc = now + reconnectInterval;

        HidDevice? device = FindControlNode();
        if (device is null)
        {
            return null;
        }

        HidStream opened;
        try
        {
            opened = device.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"找到了主板灯光芯片，但打不开：{ex.Message}";
            return null;
        }

        try
        {
            // 1. 读芯片信息（拿颜色顺序）
            opened.SetFeature(MotherboardLightProtocol.BuildInfoRequest());
            byte[] reply = MotherboardLightProtocol.NewReport();
            opened.GetFeature(reply);
            MotherboardLightInfo? info = MotherboardLightProtocol.ParseInfo(reply);
            if (info is null)
            {
                LastError = "主板灯光芯片回复的信息格式不对";
                DisposeQuietly(opened);
                return null;
            }

            // 2. 关掉自带灯效
            opened.SetFeature(MotherboardLightProtocol.BuildDisableBuiltinEffectsRequest());

            Info = info;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            LastError = $"连接主板灯光芯片失败：{ex.Message}";
            DisposeQuietly(opened);
            return null;
        }

        stream = opened;
        LastError = null;
        return stream;
    }

    /// <summary>在所有 HID 设备里找灯光控制节点。找不到返回 null，原因写进 <see cref="LastError"/>。</summary>
    private HidDevice? FindControlNode()
    {
        List<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            LastError = $"读取设备列表失败：{ex.Message}";
            return null;
        }

        foreach (HidDevice device in devices)
        {
            var id = new UsbId(device.VendorID, device.ProductID);

            // 先按编号筛选，避免去读无关设备的报告信息
            if (id == MotherboardLightProtocol.It5702
                && MotherboardLightProtocol.IsControlNode(id, TryGetFeatureLength(device), TryGetUsages(device)))
            {
                return device;
            }
        }

        LastError = "没有找到主板灯光芯片";
        return null;
    }

    /// <summary>读取 Feature 报告长度。读不到时返回 null。</summary>
    private static int? TryGetFeatureLength(HidDevice device)
    {
        try
        {
            return device.GetMaxFeatureReportLength();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    /// <summary>读取节点的所有 Usage。读不到时返回空集合（这个节点就不会被当成控制节点）。</summary>
    private static IReadOnlyCollection<uint> TryGetUsages(HidDevice device)
    {
        try
        {
            return device.GetReportDescriptor().DeviceItems
                .SelectMany(item => item.Usages.GetAllValues())
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or NotSupportedException)
        {
            return Array.Empty<uint>();
        }
    }

    private void CloseStream()
    {
        HidStream? old = stream;
        stream = null;
        Info = null;
        if (old is not null)
        {
            DisposeQuietly(old);
        }
    }

    private static void DisposeQuietly(HidStream target)
    {
        try
        {
            target.Dispose();
        }
        catch (IOException)
        {
            // 设备已经断开时，关闭也可能出错。连接反正不要了，忽略即可
        }
    }
}

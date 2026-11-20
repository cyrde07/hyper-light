using System.ComponentModel;
using HidSharp;

namespace HyperLight.Device;

/// <summary>发送一帧颜色的结果。</summary>
internal enum SendResult
{
    /// <summary>发送成功。</summary>
    Sent,

    /// <summary>当前没有连上麦克风（没找到或打不开）。之后每次发送都会按间隔自动重试连接。</summary>
    NotConnected,

    /// <summary>发送失败（比如麦克风刚被拔掉）。连接已关闭，之后会自动重连。</summary>
    Failed,
}

/// <summary>
/// QuadCast S 的灯光连接：找到灯光控制节点、打开、发颜色、断开后自动重连。
/// 只管"发一帧"，不管"多久发一次"：麦克风要持续收到数据才会保持颜色，
/// 所以调用方要按自己的节奏不停地调用 <see cref="SendFrame"/>。
/// 同一时间只能有一个线程使用它（不是线程安全的）。
/// </summary>
/// <remarks>
/// 已核实的 HidSharp 行为（2.6.4，Windows）：
/// 发送失败抛 IOException；连接已关闭时再发送抛 ObjectDisposedException；
/// 打开失败抛 IOException 或 UnauthorizedAccessException。
/// 发送是同步的、没有超时设置：万一设备卡住，调用方会被卡住。
/// M2 加截屏后，要把发送放到单独的线程，不能让它卡住截屏。
/// </remarks>
internal sealed class QuadcastMic : IDisposable
{
    /// <summary>没连上时，两次尝试连接之间至少隔多久。避免麦克风拔掉时每一帧都去搜一遍设备。</summary>
    private static readonly TimeSpan reconnectInterval = TimeSpan.FromSeconds(1);

    /// <summary>头包内容固定，只生成一次。</summary>
    private readonly byte[] headerReport = QuadcastProtocol.BuildHeaderReport();

    private HidStream? stream;
    private DateTime nextConnectAttemptUtc = DateTime.MinValue;
    private bool disposed;

    /// <summary>当前连接的设备路径。没连上时为 null。</summary>
    public string? DevicePath { get; private set; }

    /// <summary>最近一次连接失败或发送失败的原因，给界面显示用。连接成功后清空。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 判断一个 HID 节点是不是麦克风的灯光控制节点：设备编号是已知的 QuadCast S，并且 Feature 长度等于 65。
    /// M0 实测同一个麦克风有 3 个节点，只有接口 0 那个满足条件。这条规则全项目只写在这里。
    /// </summary>
    public static bool IsControlNode(UsbId id, int? featureLength) =>
        QuadcastDeviceIds.IsQuadcastS(id) && featureLength == QuadcastProtocol.FeatureReportLength;

    /// <summary>
    /// 发一帧颜色（头包 + 颜色包）。没连上时会先尝试连接（受重连间隔限制）。
    /// 设备方面的问题（没插、被拔、打不开）不会抛出异常，结果看返回值，原因看 <see cref="LastError"/>。
    /// </summary>
    public SendResult SendFrame(Rgb upper, Rgb lower)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        HidStream? current = stream ?? TryConnect();
        if (current is null)
        {
            return SendResult.NotConnected;
        }

        try
        {
            current.SetFeature(headerReport);
            current.SetFeature(QuadcastProtocol.BuildColorReport(upper, lower));
            return SendResult.Sent;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // IOException：发送失败，多半是麦克风被拔掉；ObjectDisposedException：连接已经被关闭
            LastError = $"发送失败：{ex.Message}";
            CloseStream();
            return SendResult.Failed;
        }
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

    /// <summary>尝试连接麦克风。成功返回打开的连接；失败返回 null，原因写进 <see cref="LastError"/>。</summary>
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

        try
        {
            stream = device.Open();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"找到了麦克风，但打不开：{ex.Message}";
            return null;
        }

        DevicePath = device.DevicePath;
        LastError = null;
        return stream;
    }

    /// <summary>
    /// 在所有 HID 设备里找灯光控制节点。找不到返回 null，原因写进 <see cref="LastError"/>。
    /// 电脑上插了多个 QuadCast S 时，只用找到的第一个。
    /// </summary>
    private HidDevice? FindControlNode()
    {
        List<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            // Windows 设备枚举的底层调用出错时可能抛出
            LastError = $"读取设备列表失败：{ex.Message}";
            return null;
        }

        foreach (HidDevice device in devices)
        {
            var id = new UsbId(device.VendorID, device.ProductID);

            // 先按编号筛选，避免去读无关设备的报告长度
            if (QuadcastDeviceIds.IsQuadcastS(id) && IsControlNode(id, TryGetFeatureLength(device)))
            {
                return device;
            }
        }

        LastError = "没有找到麦克风（请检查 USB 线有没有插好）";
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

    private void CloseStream()
    {
        HidStream? old = stream;
        stream = null;
        DevicePath = null;
        if (old is null)
        {
            return;
        }

        try
        {
            old.Dispose();
        }
        catch (IOException)
        {
            // 麦克风已经被拔掉时，关闭也可能出错。连接反正不要了，忽略即可
        }
    }
}

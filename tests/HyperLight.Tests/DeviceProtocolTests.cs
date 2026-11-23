using System.Reflection;
using System.Runtime.InteropServices;
using HyperLight.Device;
using Xunit;

namespace HyperLight.Tests;

/// <summary>设备协议：麦克风、主板灯光芯片、显卡灯光芯片的数据格式和安全检查。</summary>
public class DeviceProtocolTests
{
    // ───────── 麦克风 ─────────

    [Fact]
    public void MicPacketsHaveExpectedLayout()
    {
        byte[] header = QuadcastProtocol.BuildHeaderReport();
        Assert.Equal(65, header.Length);
        Assert.Equal(new byte[] { 0x00, 0x04, 0xF2 }, header[..3]);
        Assert.Equal(0x01, header[9]);

        byte[] color = QuadcastProtocol.BuildColorReport(new Rgb(1, 2, 3), new Rgb(4, 5, 6));
        Assert.Equal(new byte[] { 0x00, 0x81, 1, 2, 3, 0x81, 4, 5, 6 }, color[..9]);
    }

    [Fact]
    public void MicIdsAreRecognized()
    {
        Assert.True(QuadcastDeviceIds.IsQuadcastS(new UsbId(0x03F0, 0x028C)));
        Assert.Equal(MicMatch.OtherHyperXMic, QuadcastDeviceIds.Classify(new UsbId(0x03F0, 0x0294), out _));
    }

    // ───────── 主板灯光芯片 ─────────

    [Fact]
    public void BoardZonesMapToMeasuredChannels()
    {
        // M5-1 实测：机箱灯是 0x59，冷排灯是 0x58
        Assert.Equal(0x59, MotherboardLightProtocol.ChannelOf(MotherboardLightZone.Case));
        Assert.Equal(0x58, MotherboardLightProtocol.ChannelOf(MotherboardLightZone.Radiator));
    }

    [Fact]
    public void BoardColorReportsSplitInto19LedPackets()
    {
        var leds = Enumerable.Range(0, 40).Select(i => new Rgb((byte)i, (byte)(100 + i), 200)).ToList();
        List<byte[]> reports = MotherboardLightProtocol.BuildColorReports(MotherboardLightZone.Case, leds, 0x00010002);
        Assert.Equal(3, reports.Count);
        Assert.Equal(new byte[] { 0xCC, 0x59, 0, 0, 57 }, reports[0][..5]);
        Assert.Equal(new byte[] { 57, 0, 57 }, reports[1][2..5]);
        Assert.Equal(new byte[] { 114, 0, 6 }, reports[2][2..5]);

        // GRB 顺序：第 1 颗灯是 绿、红、蓝
        Assert.Equal(new byte[] { 100, 0, 200 }, reports[0][5..8]);
        Assert.All(reports, report => Assert.Equal(64, report.Length));
    }

    [Fact]
    public void BoardRejectsMoreThanMaxLeds()
    {
        var tooMany = Enumerable.Repeat(new Rgb(1, 2, 3), MotherboardLightProtocol.MaxLedsPerZone + 1).ToList();
        Assert.Throws<ArgumentException>(() => MotherboardLightProtocol.BuildColorReports(MotherboardLightZone.Case, tooMany, 0));
    }

    [Fact]
    public void BoardInfoParsesAndColorOrderDecodes()
    {
        byte[] reply = MotherboardLightProtocol.NewReport();
        reply[4] = 8;
        reply[6] = 33;
        reply[7] = 1;
        byte[] name = "IT5701-GIGABYTE V8.0.33.1"u8.ToArray();
        name.CopyTo(reply, 12);
        BitConverter.GetBytes(0x00010002u).CopyTo(reply, 44);

        MotherboardLightInfo? info = MotherboardLightProtocol.ParseInfo(reply);
        Assert.NotNull(info);
        Assert.Equal("8.0.33.1", info.FirmwareVersion);
        Assert.Equal("IT5701-GIGABYTE V8.0.33.1", info.ProductName);
        Assert.Equal("GRB", MotherboardLightProtocol.DescribeColorOrder(info.StripCalibration0));
        Assert.Equal("RGB", MotherboardLightProtocol.DescribeColorOrder(0x00000102));
        Assert.Null(MotherboardLightProtocol.DescribeColorOrder(0x01010101));

        reply[0] = 0;
        Assert.Null(MotherboardLightProtocol.ParseInfo(reply));
    }

    [Fact]
    public void BoardOnboardPacketsUseEffectFormat()
    {
        byte[] top = MotherboardLightProtocol.BuildOnboardStaticReport(MotherboardOnboardLight.IoCoverTop, new Rgb(0x11, 0x22, 0x33));
        Assert.Equal(new byte[] { 0xCC, 0x20, 0x01, 0, 0, 0 }, top[..6]);
        Assert.Equal(0x01, top[11]);
        Assert.Equal(0xFF, top[12]);
        Assert.Equal(new byte[] { 0x33, 0x22, 0x11, 0 }, top[14..18]);

        byte[] apply = MotherboardLightProtocol.BuildApplyOnboardRequest(
            new[] { MotherboardOnboardLight.IoCoverTop, MotherboardOnboardLight.IoCoverBottom });
        Assert.Equal(new byte[] { 0xCC, 0x28, 0x09, 0, 0, 0 }, apply[..6]);
    }

    [Fact]
    public void BoardDisablesBuiltinEffectsForBothChannels()
    {
        byte[] request = MotherboardLightProtocol.BuildDisableBuiltinEffectsRequest();
        Assert.Equal(new byte[] { 0xCC, 0x32, 0x03 }, request[..3]);
    }

    // ───────── 显卡灯光芯片 ─────────

    [Fact]
    public void GpuOnlySupportsKnownCards()
    {
        Assert.True(GpuLightProtocol.IsSupportedCard(new NvApi.PciIds(0x10DE, 0x2684, 0x1043, 0x88F1)));
        Assert.False(GpuLightProtocol.IsSupportedCard(new NvApi.PciIds(0x10DE, 0x2684, 0x1043, 0x1234)));
        Assert.False(GpuLightProtocol.IsSupportedCard(new NvApi.PciIds(0x10DE, 0x2704, 0x1043, 0x88F1)));
    }

    [Fact]
    public void GpuSignatureMustBeZeroToFifteen()
    {
        Assert.True(GpuLightProtocol.IsEneSignature(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));
        Assert.False(GpuLightProtocol.IsEneSignature(new byte[16]));
        Assert.False(GpuLightProtocol.IsEneSignature(new byte[15]));
    }

    [Fact]
    public void GpuOnlyControlsWhitelistedChips()
    {
        var config = new byte[64];
        config[2] = 5;
        config[3] = 30;
        Assert.Equal(30, GpuLightProtocol.LedCountOf("AUMA0-E6K5-1110", config));
        Assert.Null(GpuLightProtocol.LedCountOf("AUMA0-E6K5-0104", config));
        Assert.Null(GpuLightProtocol.LedCountOf("XYZ", config));
    }

    [Fact]
    public void GpuColorOrderIsRedBlueGreen()
    {
        var bytes = new byte[3];
        GpuLightProtocol.WriteColor(bytes, new Rgb(0x11, 0x22, 0x33));
        Assert.Equal(new byte[] { 0x11, 0x33, 0x22 }, bytes);
        Assert.Equal(new byte[] { 0x80, 0x20 }, GpuLightProtocol.EncodeRegisterAddress(0x8020));
    }

    [Fact]
    public void GpuWritesStayInsideDirectColorArea()
    {
        // 最多 32 颗灯，写到 0x815F 为止，不会碰到 0x8160 开始的灯效颜色区
        int lastByte = GpuLightProtocol.DirectColorsRegister + (GpuLightProtocol.MaxLeds * 3) - 1;
        Assert.True(lastByte < 0x8160);
        Assert.Equal(0x01, GpuLightProtocol.ApplyValue);
    }

    [Fact]
    public void NvApiI2cStructMatchesDriverLayout()
    {
        // 和 NVIDIA 驱动交换的数据结构：64 位下一共 0x40 字节，每个字段的位置必须对得上
        Type type = typeof(NvApi).GetNestedType("I2cInfoV3", BindingFlags.NonPublic)!;
        Assert.Equal(0x40, Marshal.SizeOf(type));
        var expected = new Dictionary<string, int>
        {
            ["Version"] = 0,
            ["DisplayMask"] = 4,
            ["IsDdcPort"] = 8,
            ["DeviceAddress"] = 9,
            ["RegisterAddress"] = 16,
            ["RegisterAddressSize"] = 24,
            ["Data"] = 32,
            ["Size"] = 40,
            ["Speed"] = 44,
            ["SpeedKhz"] = 48,
            ["PortId"] = 52,
            ["IsPortIdSet"] = 56,
        };
        foreach ((string field, int offset) in expected)
        {
            Assert.Equal(offset, (int)Marshal.OffsetOf(type, field));
        }
    }
}

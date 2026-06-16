using Xunit;
using App.Core;

namespace App.Core.Tests;

/// <summary>
/// Modbus RTU CRC16 (poly 0xA001, init 0xFFFF) 单元测试。
/// </summary>
public class Crc16Tests
{
    [Theory]
    [InlineData(new byte[] { 0x01 }, 0x807E)]
    [InlineData(new byte[] { 0x01, 0x04 }, 0xE301)]
    [InlineData(new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A }, 0xCDC5)]
    public void Compute_ReturnsExpectedCrc_ForKnownVectors(byte[] data, ushort expected)
    {
        var crc = Crc16.Compute(data);
        Assert.Equal(expected, crc);
    }

    [Fact]
    public void Write_WritesLittleEndianCrc_ToDestination()
    {
        // CRC of [0x01] is 0x807E → 小端 [0x7E, 0x80]
        Span<byte> dest = stackalloc byte[2];
        Crc16.Write(new byte[] { 0x01 }, dest);

        Assert.Equal(0x7E, dest[0]);
        Assert.Equal(0x80, dest[1]);
    }

    [Fact]
    public void Verify_ReturnsTrue_ForCorrectFrame()
    {
        // 完整 RTU 帧：01 03 00 00 00 0A + CRC(C5 CD)
        var frame = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCD };
        Assert.True(Crc16.Verify(frame));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForCorruptedFrame()
    {
        // 翻转最后一个 CRC 字节
        var frame = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A, 0xC5, 0xCE };
        Assert.False(Crc16.Verify(frame));
    }

    [Fact]
    public void Verify_ReturnsFalse_ForTooShortFrame()
    {
        Assert.False(Crc16.Verify(new byte[] { 0x01 }));
        Assert.False(Crc16.Verify([]));
    }

    [Fact]
    public void Compute_IsStable_AgainstSpanAndArray()
    {
        var data = new byte[] { 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A };
        var asArray = Crc16.Compute(data);
        var asSpan = Crc16.Compute((ReadOnlySpan<byte>)data.AsSpan());

        Assert.Equal(asArray, asSpan);
    }
}

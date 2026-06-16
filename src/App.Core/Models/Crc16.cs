namespace App.Core;

/// <summary>
/// Modbus RTU CRC16（ polynomial 0xA001 ）零分配工具。
/// </summary>
public static class Crc16
{
    /// <summary>
    /// 计算给定缓冲区的 CRC16。
    /// </summary>
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                if ((crc & 0x0001) != 0)
                {
                    crc >>= 1;
                    crc ^= 0xA001;
                }
                else
                {
                    crc >>= 1;
                }
            }
        }
        return crc;
    }

    /// <summary>
    /// 计算 CRC16 并写入小端字节序到目标缓冲区。
    /// </summary>
    public static void Write(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        var crc = Compute(data);
        destination[0] = (byte)(crc & 0xFF);
        destination[1] = (byte)(crc >> 8);
    }

    /// <summary>
    /// 验证固定长帧是否 CRC 匹配。
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return false;
        }

        var payload = frame.Slice(0, frame.Length - 2);
        Span<byte> expected = stackalloc byte[2];
        Write(payload, expected);
        return frame[^2] == expected[0] && frame[^1] == expected[1];
    }
}

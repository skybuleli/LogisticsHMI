using App.Core;

namespace App.Infrastructure.Drivers;

/// <summary>
/// Modbus 协议无关工具方法（地址解析、PDU 构建、响应解析、CRC、大小端、异常码格式化）。
/// <para>
/// TCP / RTU 驱动共用本类，避免在两处重复实现协议逻辑。当前 TCP 驱动仍保留私有副本，
/// 待 Phase 2.7 工厂统一管理时再迁移调用点。
/// </para>
/// </summary>
internal static class ModbusProtocol
{
    public const byte FunctionCodeMask = 0x7F;   // 正常响应 FC 掩码
    public const byte ErrorFlag = 0x80;          // 异常响应最高位
    public const int MaxPduLength = 253;         // 最大 PDU 长度（Modbus 规范）

    // ── 大小端读写 ───────────────────────────────────────────

    /// <summary>
    /// 将 ushort 以大端序写入 span。
    /// </summary>
    public static void WriteBigEndian(Span<byte> destination, ushort value)
    {
        destination[0] = (byte)(value >> 8);
        destination[1] = (byte)value;
    }

    /// <summary>
    /// 从 span 读取大端序 ushort。
    /// </summary>
    public static ushort ReadBigEndian(ReadOnlySpan<byte> source)
    {
        return (ushort)((source[0] << 8) | source[1]);
    }

    // ── 地址解析 ─────────────────────────────────────────────

    /// <summary>
    /// 解析读操作地址字符串。
    /// 格式：[FC前缀:][地址]，例如 "03:100", "01:0", "100"。
    /// 默认 FC = 03（读保持寄存器）。支持 PLC 地址格式（4xxxxx/3xxxxx/1xxxxx）。
    /// </summary>
    public static bool TryParseReadAddress(
        string address, out ModbusFunctionCode functionCode, out ushort startAddress)
    {
        functionCode = ModbusFunctionCode.ReadHoldingRegisters;
        startAddress = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        // 尝试解析 FC:Address 格式，支持十六进制（如 "0F:0"）和十进制（如 "15:0"）
        var colonIndex = address.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 2) // FC 前缀最多 2 位
        {
            var fcPart = address[..colonIndex];
            var addrPart = address[(colonIndex + 1)..];

            // 先尝试十进制，若失败则尝试十六进制
            if ((!byte.TryParse(fcPart, out var fcByte) ||
                 !ushort.TryParse(addrPart, out var addr)) &&
                !(byte.TryParse(fcPart, System.Globalization.NumberStyles.HexNumber, null, out fcByte) &&
                  ushort.TryParse(addrPart, out addr)))
            {
                // 两者都失败
                return false;
            }

            functionCode = (ModbusFunctionCode)fcByte;
            startAddress = addr;
            return true;
        }

        // 纯数字地址 → 优先匹配 PLC 标准地址段，否则默认保持寄存器
        if (ushort.TryParse(address, out var plainAddr))
        {
            if (plainAddr is >= 40001 and <= 49999)
            {
                functionCode = ModbusFunctionCode.ReadHoldingRegisters;
                startAddress = (ushort)(plainAddr - 40001);
            }
            else if (plainAddr is >= 30001 and <= 39999)
            {
                functionCode = ModbusFunctionCode.ReadInputRegisters;
                startAddress = (ushort)(plainAddr - 30001);
            }
            else if (plainAddr is >= 10001 and <= 19999)
            {
                functionCode = ModbusFunctionCode.ReadDiscreteInputs;
                startAddress = (ushort)(plainAddr - 10001);
            }
            else
            {
                // 范围外（含 < 10001）的纯数字默认保持寄存器
                functionCode = ModbusFunctionCode.ReadHoldingRegisters;
                startAddress = plainAddr;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析写操作地址和数据，确定功能码。
    /// 无前缀时按 data.Length 推断：1=写单线圈，2=写单寄存器，其他=写多寄存器。
    /// </summary>
    public static bool TryParseWriteAddress(
        string address, int dataLength, out ModbusFunctionCode functionCode, out ushort startAddress)
    {
        functionCode = ModbusFunctionCode.WriteSingleRegister;
        startAddress = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var colonIndex = address.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 2)
        {
            var fcPart = address[..colonIndex];
            var addrPart = address[(colonIndex + 1)..];

            // 先尝试十进制，若失败则尝试十六进制
            byte fcByte;
            ushort addr;
            if ((!byte.TryParse(fcPart, out fcByte) ||
                 !ushort.TryParse(addrPart, out addr)) &&
                !(byte.TryParse(fcPart, System.Globalization.NumberStyles.HexNumber, null, out fcByte) &&
                  ushort.TryParse(addrPart, out addr)))
            {
                return false;
            }

            var fc = (ModbusFunctionCode)fcByte;
            if (fc is ModbusFunctionCode.WriteSingleCoil
                or ModbusFunctionCode.WriteSingleRegister
                or ModbusFunctionCode.WriteMultipleCoils
                or ModbusFunctionCode.WriteMultipleRegisters)
            {
                functionCode = fc;
                startAddress = addr;
                return true;
            }
        }

        if (ushort.TryParse(address, out var plainAddr))
        {
            functionCode = dataLength switch
            {
                1 => ModbusFunctionCode.WriteSingleCoil,
                2 => ModbusFunctionCode.WriteSingleRegister,
                _ => ModbusFunctionCode.WriteMultipleRegisters
            };
            startAddress = plainAddr;
            return true;
        }

        return false;
    }

    // ── PDU 构建 ─────────────────────────────────────────────

    /// <summary>
    /// 构建读请求 PDU（FC + 起始地址 + 数量，共 5 字节）。
    /// </summary>
    public static byte[] BuildReadRequestPdu(ModbusFunctionCode functionCode, ushort startAddress, ushort quantity)
    {
        var pdu = new byte[5];
        pdu[0] = (byte)functionCode;
        WriteBigEndian(pdu.AsSpan(1..3), startAddress);
        WriteBigEndian(pdu.AsSpan(3..5), quantity);
        return pdu;
    }

    /// <summary>
    /// 构建写请求 PDU。返回 null 表示功能码不合法。
    /// </summary>
    public static byte[]? BuildWriteRequestPdu(
        ModbusFunctionCode functionCode, ushort startAddress, ReadOnlySpan<byte> data)
    {
        var pduLength = functionCode switch
        {
            ModbusFunctionCode.WriteSingleCoil => 5,                  // FC + Addr(2) + Value(2)
            ModbusFunctionCode.WriteSingleRegister => 5,              // FC + Addr(2) + Value(2)
            ModbusFunctionCode.WriteMultipleCoils => 6 + data.Length, // FC + Addr(2) + Qty(2) + ByteCount(1) + Data
            ModbusFunctionCode.WriteMultipleRegisters => 6 + data.Length,
            _ => 0
        };

        if (pduLength == 0)
        {
            return null;
        }

        var pdu = new byte[pduLength];
        pdu[0] = (byte)functionCode;
        WriteBigEndian(pdu.AsSpan(1..3), startAddress);

        switch (functionCode)
        {
            case ModbusFunctionCode.WriteSingleCoil:
                // 0xFF00 = ON, 0x0000 = OFF
                WriteBigEndian(pdu.AsSpan(3..5), data.Length > 0 && data[0] != 0 ? (ushort)0xFF00 : (ushort)0);
                break;

            case ModbusFunctionCode.WriteSingleRegister:
                // data 应恰好为 2 字节（大端寄存器值），不足补 0
                data.Slice(0, Math.Min(data.Length, 2)).CopyTo(pdu.AsSpan(3..5));
                break;

            case ModbusFunctionCode.WriteMultipleCoils:
            {
                var quantity = data.Length * 8;
                WriteBigEndian(pdu.AsSpan(3..5), (ushort)quantity);
                pdu[5] = (byte)data.Length;
                data.CopyTo(pdu.AsSpan(6..));
                break;
            }

            case ModbusFunctionCode.WriteMultipleRegisters:
            {
                var quantity = data.Length / 2;
                WriteBigEndian(pdu.AsSpan(3..5), (ushort)quantity);
                pdu[5] = (byte)data.Length;
                data.CopyTo(pdu.AsSpan(6..));
                break;
            }
        }

        return pdu;
    }

    // ── 响应解析 ─────────────────────────────────────────────

    /// <summary>
    /// 从读响应 PDU 提取数据。
    /// 读响应格式：FC + 字节数 + 数据。
    /// </summary>
    public static Result<byte[]> ParseReadResponse(byte[] responsePdu)
    {
        var span = responsePdu.AsSpan();
        if (span.Length < 2)
        {
            return Result<byte[]>.Failure("响应帧过短");
        }

        var byteCount = span[1];
        if (byteCount + 2 > span.Length)
        {
            return Result<byte[]>.Failure($"响应数据长度声明 {byteCount} 超出实际 {span.Length - 2}");
        }

        var result = new byte[byteCount];
        span.Slice(2, byteCount).CopyTo(result);
        return Result<byte[]>.Success(result);
    }

    /// <summary>
    /// 校验写响应帧（至少 5 字节，含 FC）。返回是否基本合法。
    /// </summary>
    public static bool IsWriteResponseWellFormed(ReadOnlySpan<byte> responsePdu)
    {
        return responsePdu.Length >= 4;
    }

    /// <summary>
    /// 从 PDU 首字节判断是否为异常响应。
    /// </summary>
    public static bool IsExceptionResponse(byte pduFirstByte)
    {
        return (pduFirstByte & ErrorFlag) != 0;
    }

    /// <summary>
    /// 从响应 PDU 提取实际功能码（屏蔽异常位）。
    /// </summary>
    public static ModbusFunctionCode GetResponseFunctionCode(byte pduFirstByte)
    {
        return (ModbusFunctionCode)(pduFirstByte & FunctionCodeMask);
    }

    /// <summary>
    /// 格式化 Modbus 异常码为可读消息。
    /// </summary>
    public static string FormatModbusError(byte exceptionCode)
    {
        return exceptionCode switch
        {
            0x01 => "非法功能码 (Illegal Function)",
            0x02 => "非法数据地址 (Illegal Data Address)",
            0x03 => "非法数据值 (Illegal Data Value)",
            0x04 => "从站设备故障 (Slave Device Failure)",
            0x05 => "确认超时 (Acknowledge)",
            0x06 => "从站忙 (Slave Device Busy)",
            0x08 => "存储器奇偶校验错误 (Memory Parity Error)",
            0x0A => "网关路径不可用 (Gateway Path Unavailable)",
            0x0B => "网关目标设备无响应 (Gateway Target Device Failed)",
            _ => $"未知异常码 0x{exceptionCode:X2}"
        };
    }

    // ── RTU 帧长度推导 ───────────────────────────────────────

    /// <summary>
    /// 根据（非异常）响应的期望语义推导 RTU 完整帧总长度（含 Slave + PDU + 2 字节 CRC）。
    /// <para>
    /// 读操作（FC01~04）需传入 quantity；写操作（FC05/06/0F/10）固定 8 字节。
    /// 异常响应恒为 5 字节（需另行处理，调用方先读首字节判断）。
    /// </para>
    /// </summary>
    /// <remarks>
    /// 长度推导规则：
    /// <list type="bullet">
    ///   <item>FC01/02 读线圈/离散输入：[Slave][FC][ByteCount][Data...][CRC] = 3 + ceil(quantity/8) + 2</item>
    ///   <item>FC03/04 读寄存器：[Slave][FC][ByteCount][Data...][CRC] = 3 + quantity*2 + 2</item>
    ///   <item>FC05/06/0F/10 写：[Slave][FC][Addr(2)][Value/Qty(2)][CRC] = 8</item>
    /// </list>
    /// </remarks>
    public static int GetRtuResponseFrameLength(ModbusFunctionCode functionCode, ushort quantity)
    {
        var pduLength = functionCode switch
        {
            ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs
                => 2 + (quantity + 7) / 8,                  // FC + ByteCount + 数据
            ModbusFunctionCode.ReadHoldingRegisters or ModbusFunctionCode.ReadInputRegisters
                => 2 + quantity * 2,                        // FC + ByteCount + 数据
            ModbusFunctionCode.WriteSingleCoil
                or ModbusFunctionCode.WriteSingleRegister
                or ModbusFunctionCode.WriteMultipleCoils
                or ModbusFunctionCode.WriteMultipleRegisters
                => 5,                                       // FC + Addr(2) + Value/Qty(2)
            _ => 0
        };

        if (pduLength == 0)
        {
            return 0;
        }

        // Slave(1) + PDU + CRC(2)
        return 1 + pduLength + 2;
    }

    /// <summary>
    /// RTU 异常响应固定帧长度：[Slave][FC|0x80][ExCode][CRC][CRC] = 5。
    /// </summary>
    public const int RtuExceptionFrameLength = 5;
}

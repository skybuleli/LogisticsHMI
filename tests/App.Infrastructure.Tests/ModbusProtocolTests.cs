using Xunit;
using App.Core;
using App.Infrastructure.Drivers;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="ModbusProtocol"/> 协议工具类单元测试。
/// 覆盖地址解析、PDU 构建、响应解析、RTU 帧长度推导、大小端、异常码格式化。
/// </summary>
public class ModbusProtocolTests
{
    // ── 大小端 ───────────────────────────────────────────────

    [Theory]
    [InlineData((ushort)0x1234, new byte[] { 0x12, 0x34 })]
    [InlineData((ushort)0xFF00, new byte[] { 0xFF, 0x00 })]
    [InlineData((ushort)0x00AB, new byte[] { 0x00, 0xAB })]
    public void WriteBigEndian_WritesMostSignificantByteFirst(ushort value, byte[] expected)
    {
        Span<byte> buf = stackalloc byte[2];
        ModbusProtocol.WriteBigEndian(buf, value);
        Assert.Equal(expected, buf.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 0x12, 0x34 }, (ushort)0x1234)]
    [InlineData(new byte[] { 0xFF, 0x00 }, (ushort)0xFF00)]
    public void ReadBigEndian_ReadsMostSignificantByteFirst(byte[] data, ushort expected)
    {
        Assert.Equal(expected, ModbusProtocol.ReadBigEndian(data));
    }

    // ── 地址解析（读）────────────────────────────────────────

    [Fact]
    public void TryParseReadAddress_PlainNumber_DefaultsToHoldingRegister()
    {
        var ok = ModbusProtocol.TryParseReadAddress("100", out var fc, out var addr);

        Assert.True(ok);
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, fc);
        Assert.Equal((ushort)100, addr);
    }

    [Fact]
    public void TryParseReadAddress_FcPrefix_ParsesExplicitFunctionCode()
    {
        var ok = ModbusProtocol.TryParseReadAddress("01:5", out var fc, out var addr);

        Assert.True(ok);
        Assert.Equal(ModbusFunctionCode.ReadCoils, fc);
        Assert.Equal((ushort)5, addr);
    }

    [Theory]
    // 4xxxxx → 保持寄存器（PLC 地址 40001 对应协议地址 0）
    [InlineData("40001", ModbusFunctionCode.ReadHoldingRegisters, (ushort)0)]
    [InlineData("40010", ModbusFunctionCode.ReadHoldingRegisters, (ushort)9)]
    // 3xxxxx → 输入寄存器（30001 → 0）
    [InlineData("30001", ModbusFunctionCode.ReadInputRegisters, (ushort)0)]
    // 1xxxxx → 离散输入（10001 → 0）
    [InlineData("10001", ModbusFunctionCode.ReadDiscreteInputs, (ushort)0)]
    public void TryParseReadAddress_PlcAddressFormat_InfersFunctionCodeAndOffset(
        string input, ModbusFunctionCode expectedFc, ushort expectedAddr)
    {
        var ok = ModbusProtocol.TryParseReadAddress(input, out var fc, out var addr);

        Assert.True(ok);
        Assert.Equal(expectedFc, fc);
        Assert.Equal(expectedAddr, addr);
    }

    [Fact]
    public void TryParseReadAddress_EmptyString_ReturnsFalse()
    {
        Assert.False(ModbusProtocol.TryParseReadAddress("", out _, out _));
        Assert.False(ModbusProtocol.TryParseReadAddress("   ", out _, out _));
    }

    // ── 地址解析（写）────────────────────────────────────────

    [Theory]
    // data.Length=1 → 写单线圈
    [InlineData("0", 1, ModbusFunctionCode.WriteSingleCoil)]
    // data.Length=2 → 写单寄存器
    [InlineData("100", 2, ModbusFunctionCode.WriteSingleRegister)]
    // data.Length=4 → 写多寄存器
    [InlineData("100", 4, ModbusFunctionCode.WriteMultipleRegisters)]
    public void TryParseWriteAddress_InfersFunctionCode_FromDataLength(
        string address, int dataLength, ModbusFunctionCode expectedFc)
    {
        var ok = ModbusProtocol.TryParseWriteAddress(address, dataLength, out var fc, out _);

        Assert.True(ok);
        Assert.Equal(expectedFc, fc);
    }

    [Fact]
    public void TryParseWriteAddress_Fc0F_ParsesWriteMultipleCoils()
    {
        var ok = ModbusProtocol.TryParseWriteAddress("0F:0", 2, out var fc, out var addr);

        Assert.True(ok);
        Assert.Equal(ModbusFunctionCode.WriteMultipleCoils, fc);
        Assert.Equal((ushort)0, addr);
    }

    // ── PDU 构建（读）────────────────────────────────────────

    [Fact]
    public void BuildReadRequestPdu_ReadHoldingRegisters_ProducesCorrectFrame()
    {
        // FC03, addr=100 (0x0064), qty=10 (0x000A)
        var pdu = ModbusProtocol.BuildReadRequestPdu(
            ModbusFunctionCode.ReadHoldingRegisters, 100, 10);

        Assert.Equal(new byte[] { 0x03, 0x00, 0x64, 0x00, 0x0A }, pdu);
    }

    [Fact]
    public void BuildReadRequestPdu_AlwaysReturns5Bytes()
    {
        var pdu = ModbusProtocol.BuildReadRequestPdu(ModbusFunctionCode.ReadCoils, 0, 1);
        Assert.Equal(5, pdu.Length);
    }

    // ── PDU 构建（写）────────────────────────────────────────

    [Fact]
    public void BuildWriteRequestPdu_WriteSingleRegister_ProducesCorrectFrame()
    {
        // FC06, addr=100 (0x0064), value=0x1234
        var pdu = ModbusProtocol.BuildWriteRequestPdu(
            ModbusFunctionCode.WriteSingleRegister, 100, new byte[] { 0x12, 0x34 });

        Assert.Equal(new byte[] { 0x06, 0x00, 0x64, 0x12, 0x34 }, pdu);
    }

    [Fact]
    public void BuildWriteRequestPdu_WriteSingleCoil_EncodesFF00ForOn()
    {
        var pdu = ModbusProtocol.BuildWriteRequestPdu(
            ModbusFunctionCode.WriteSingleCoil, 5, new byte[] { 0x01 });

        // 0xFF00 = ON
        Assert.Equal(new byte[] { 0x05, 0x00, 0x05, 0xFF, 0x00 }, pdu);
    }

    [Fact]
    public void BuildWriteRequestPdu_WriteSingleCoil_Encodes0000ForOff()
    {
        var pdu = ModbusProtocol.BuildWriteRequestPdu(
            ModbusFunctionCode.WriteSingleCoil, 5, new byte[] { 0x00 });

        Assert.Equal(new byte[] { 0x05, 0x00, 0x05, 0x00, 0x00 }, pdu);
    }

    [Fact]
    public void BuildWriteRequestPdu_WriteMultipleRegisters_ProducesCorrectFrame()
    {
        // FC10, addr=100, qty=2, byteCount=4, data=0x1111 0x2222
        var pdu = ModbusProtocol.BuildWriteRequestPdu(
            ModbusFunctionCode.WriteMultipleRegisters, 100,
            new byte[] { 0x11, 0x11, 0x22, 0x22 });

        Assert.Equal(
            new byte[] { 0x10, 0x00, 0x64, 0x00, 0x02, 0x04, 0x11, 0x11, 0x22, 0x22 },
            pdu);
    }

    [Fact]
    public void BuildWriteRequestPdu_ReadFunctionCode_ReturnsNull()
    {
        var pdu = ModbusProtocol.BuildWriteRequestPdu(
            ModbusFunctionCode.ReadHoldingRegisters, 0, new byte[] { 0x00, 0x00 });
        Assert.Null(pdu);
    }

    // ── 响应解析 ─────────────────────────────────────────────

    [Fact]
    public void ParseReadResponse_ExtractsData_PastByteCount()
    {
        // FC03 响应 PDU：FC + 字节数(2) + 数据(0x1234 0x5678)
        var responsePdu = new byte[] { 0x03, 0x04, 0x12, 0x34, 0x56, 0x78 };

        var result = ModbusProtocol.ParseReadResponse(responsePdu);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x12, 0x34, 0x56, 0x78 }, result.Value);
    }

    [Fact]
    public void ParseReadResponse_TooShort_ReturnsFailure()
    {
        var result = ModbusProtocol.ParseReadResponse(new byte[] { 0x03 });

        Assert.False(result.IsSuccess);
        Assert.Contains("过短", result.Error);
    }

    [Fact]
    public void ParseReadResponse_DeclaredLengthExceedsActual_ReturnsFailure()
    {
        // 声明 10 字节但实际只有 2 字节
        var result = ModbusProtocol.ParseReadResponse(new byte[] { 0x03, 0x0A, 0x12, 0x34 });

        Assert.False(result.IsSuccess);
    }

    // ── 异常响应识别 ─────────────────────────────────────────

    [Theory]
    [InlineData((byte)0x83, true)]   // 0x03 | 0x80 → 异常
    [InlineData((byte)0x03, false)]  // 正常
    [InlineData((byte)0x86, true)]   // 0x06 | 0x80 → 异常
    public void IsExceptionResponse_DetectsErrorFlag(byte firstByte, bool expected)
    {
        Assert.Equal(expected, ModbusProtocol.IsExceptionResponse(firstByte));
    }

    [Fact]
    public void GetResponseFunctionCode_StripsExceptionFlag()
    {
        Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters,
            ModbusProtocol.GetResponseFunctionCode(0x83));
        Assert.Equal(ModbusFunctionCode.WriteSingleRegister,
            ModbusProtocol.GetResponseFunctionCode(0x86));
    }

    [Theory]
    [InlineData((byte)0x01, "非法功能码")]
    [InlineData((byte)0x02, "非法数据地址")]
    [InlineData((byte)0x03, "非法数据值")]
    [InlineData((byte)0x06, "从站忙")]
    [InlineData((byte)0xFF, "未知异常码")]
    public void FormatModbusError_ReturnsReadableMessage(byte code, string expectedFragment)
    {
        var msg = ModbusProtocol.FormatModbusError(code);
        Assert.Contains(expectedFragment, msg);
    }

    // ── RTU 帧长度推导 ───────────────────────────────────────

    [Theory]
    // FC03/04 读寄存器：1(Slave) + 2(FC+ByteCount) + qty*2 + 2(CRC)
    [InlineData(ModbusFunctionCode.ReadHoldingRegisters, (ushort)2, 9)]   // 1+2+4+2 = 9
    [InlineData(ModbusFunctionCode.ReadInputRegisters, (ushort)10, 25)]   // 1+2+20+2 = 25
    // FC01/02 读位：1 + 2 + ceil(qty/8) + 2
    [InlineData(ModbusFunctionCode.ReadCoils, (ushort)8, 6)]     // 1+2+1+2 = 6
    [InlineData(ModbusFunctionCode.ReadCoils, (ushort)9, 7)]     // 1+2+2+2 = 7（9位需2字节）
    [InlineData(ModbusFunctionCode.ReadDiscreteInputs, (ushort)1, 6)]  // 1+2+1+2 = 6
    // FC05/06/0F/10 写：固定 8 字节
    [InlineData(ModbusFunctionCode.WriteSingleCoil, (ushort)0, 8)]
    [InlineData(ModbusFunctionCode.WriteSingleRegister, (ushort)0, 8)]
    [InlineData(ModbusFunctionCode.WriteMultipleCoils, (ushort)0, 8)]
    [InlineData(ModbusFunctionCode.WriteMultipleRegisters, (ushort)0, 8)]
    public void GetRtuResponseFrameLength_ReturnsExpectedSize(
        ModbusFunctionCode fc, ushort quantity, int expected)
    {
        Assert.Equal(expected, ModbusProtocol.GetRtuResponseFrameLength(fc, quantity));
    }

    [Fact]
    public void GetRtuResponseFrameLength_ReadFunctionCodeWithUnknownFc_ReturnsZero()
    {
        // 非 Modbus 标准功能码（0x00）应返回 0 表示无法推导
        Assert.Equal(0, ModbusProtocol.GetRtuResponseFrameLength((ModbusFunctionCode)0x00, 1));
    }

    [Fact]
    public void RtuExceptionFrameLength_IsFive()
    {
        // 异常帧：[Slave][FC|0x80][ExCode][CRC][CRC]
        Assert.Equal(5, ModbusProtocol.RtuExceptionFrameLength);
    }
}

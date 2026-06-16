using Xunit;
using App.Core;
using App.Infrastructure.Drivers;
using S7.Net;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="S7Driver"/> 单元测试。
/// 覆盖地址解析、配置映射等不依赖网络连接的逻辑。
/// 完整 PLC 通信集成测试需在真实硬件或模拟器环境（P7.5 现场联调 / P2.10 模拟设备）进行。
/// </summary>
public class S7DriverTests
{
    // ── 地址解析：DataBlock ──────────────────────────────────

    [Theory]
    [InlineData("DB1.0", 1, 0)]
    [InlineData("DB100.24", 100, 24)]
    [InlineData("DB1.255", 1, 255)]
    public void TryParseAddress_DbBlock_ParsesCorrectly(string address, int expectedDb, int expectedStart)
    {
        var ok = S7Driver.TryParseAddress(address, out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.DataBlock, dataType);
        Assert.Equal(expectedDb, db);
        Assert.Equal(expectedStart, startByte);
    }

    [Fact]
    public void TryParseAddress_DbWithoutOffset_DefaultsToZero()
    {
        var ok = S7Driver.TryParseAddress("DB1", out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.DataBlock, dataType);
        Assert.Equal(1, db);
        Assert.Equal(0, startByte);
    }

    [Fact]
    public void TryParseAddress_DbLargeNumber_ParsesCorrectly()
    {
        var ok = S7Driver.TryParseAddress("DB9999.65535", out _, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(9999, db);
        Assert.Equal(65535, startByte);
    }

    [Fact]
    public void TryParseAddress_DbWithBitSuffix_IgnoresBitPosition()
    {
        // DB1.0.7 → 字节 0，忽略位偏移 7
        var ok = S7Driver.TryParseAddress("DB1.0.7", out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.DataBlock, dataType);
        Assert.Equal(1, db);
        Assert.Equal(0, startByte);
    }

    // ── 地址解析：输入映像区 I ───────────────────────────────

    [Theory]
    [InlineData("I0", 0)]
    [InlineData("I10", 10)]
    [InlineData("I255", 255)]
    public void TryParseAddress_InputArea_ParsesCorrectly(string address, int expectedStart)
    {
        var ok = S7Driver.TryParseAddress(address, out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.Input, dataType);
        Assert.Equal(0, db); // DB 编号不适用
        Assert.Equal(expectedStart, startByte);
    }

    [Fact]
    public void TryParseAddress_InputAreaLowerCase_ParsesCorrectly()
    {
        var ok = S7Driver.TryParseAddress("i0", out var dataType, out _, out _);

        Assert.True(ok);
        Assert.Equal(DataType.Input, dataType);
    }

    // ── 地址解析：输出映像区 Q ───────────────────────────────

    [Theory]
    [InlineData("Q0", 0)]
    [InlineData("Q4", 4)]
    [InlineData("Q127", 127)]
    public void TryParseAddress_OutputArea_ParsesCorrectly(string address, int expectedStart)
    {
        var ok = S7Driver.TryParseAddress(address, out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.Output, dataType);
        Assert.Equal(0, db);
        Assert.Equal(expectedStart, startByte);
    }

    [Fact]
    public void TryParseAddress_OutputAreaLowerCase_ParsesCorrectly()
    {
        var ok = S7Driver.TryParseAddress("q0", out var dataType, out _, out _);

        Assert.True(ok);
        Assert.Equal(DataType.Output, dataType);
    }

    // ── 地址解析：存储区 M ───────────────────────────────────

    [Theory]
    [InlineData("M0", 0)]
    [InlineData("M100", 100)]
    [InlineData("M65535", 65535)]
    public void TryParseAddress_MemoryArea_ParsesCorrectly(string address, int expectedStart)
    {
        var ok = S7Driver.TryParseAddress(address, out var dataType, out var db, out var startByte);

        Assert.True(ok);
        Assert.Equal(DataType.Memory, dataType);
        Assert.Equal(0, db);
        Assert.Equal(expectedStart, startByte);
    }

    [Fact]
    public void TryParseAddress_MemoryAreaLowerCase_ParsesCorrectly()
    {
        var ok = S7Driver.TryParseAddress("m0", out var dataType, out _, out _);

        Assert.True(ok);
        Assert.Equal(DataType.Memory, dataType);
    }

    // ── 地址解析：错误场景 ───────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("INVALID")]
    [InlineData("DB")]
    [InlineData("DB.")]
    [InlineData("DB.abc")]
    [InlineData("ABC123")]
    public void TryParseAddress_InvalidInput_ReturnsFalse(string address)
    {
        var ok = S7Driver.TryParseAddress(address, out _, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryParseAddress_NullInput_ReturnsFalse()
    {
        // 方法签名接受 string?，IsNullOrWhiteSpace(null) 返回 true
        Assert.False(S7Driver.TryParseAddress(null, out _, out _, out _));
    }

    // ── 地址解析：负边界 ─────────────────────────────────────

    [Fact]
    public void TryParseAddress_NegativeOffsetInDb_ReturnsFalse()
    {
        var ok = S7Driver.TryParseAddress("DB1.-1", out _, out _, out _);
        Assert.False(ok);
    }

    // ── S7DriverConfig CpuType ───────────────────────────────

    private static S7DriverConfig CreateDefaultConfig() => new()
    {
        DeviceId = "test-s7-device"
    };

    [Fact]
    public void S7DriverConfig_DefaultCpuType_IsS71200()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(S7CpuType.S71200, config.CpuType);
    }

    [Fact]
    public void S7DriverConfig_DefaultIp_Is19216801()
    {
        var config = CreateDefaultConfig();
        Assert.Equal("192.168.0.1", config.IpAddress);
    }

    [Fact]
    public void S7DriverConfig_DefaultRack_Is0()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(0, config.Rack);
    }

    [Fact]
    public void S7DriverConfig_DefaultSlot_Is2()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(2, config.Slot);
    }

    // ── S7CpuType 常量 ──────────────────────────────────────

    [Fact]
    public void S7CpuType_HasAllExpectedConstants()
    {
        Assert.Equal("S7200", S7CpuType.S7200);
        Assert.Equal("S7300", S7CpuType.S7300);
        Assert.Equal("S7400", S7CpuType.S7400);
        Assert.Equal("S71200", S7CpuType.S71200);
        Assert.Equal("S71500", S7CpuType.S71500);
        Assert.Equal("Logo0BA8", S7CpuType.Logo0BA8);
        Assert.Equal("S7200Smart", S7CpuType.S7200Smart);
    }

    // ── 驱动名称 ─────────────────────────────────────────────

    // DriverName 测试需要完整的 mock 环境（ILogger 等），
    // 由集成测试覆盖。地址解析的正确性已由上方各测试用例覆盖。

    // ── 地址解析：区分大小写 ────────────────────────────────

    [Fact]
    public void TryParseAddress_Ie_IsInvalid()
    {
        // "IE" 不是合法前缀（"I" 后面必须是数字）
        var ok = S7Driver.TryParseAddress("IE", out _, out _, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryParseAddress_db1_0_Lowercase_ReturnsFalse()
    {
        // "db1.0" → 小写 db 不是合法格式（要求大写 "DB"）
        var ok = S7Driver.TryParseAddress("db1.0", out _, out _, out _);
        Assert.False(ok);
    }
}

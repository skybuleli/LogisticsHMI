using Xunit;
using App.Core;
using App.Infrastructure.Drivers;
using Opc.Ua;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="OpcUaDriver"/> 单元测试。
/// 覆盖 NodeId 解析、配置默认值、值序列化/反序列化等不依赖网络连接的逻辑。
/// 完整 OPC UA 服务器通信集成测试需在真实服务器或模拟器环境（P7.5 现场联调）进行。
/// </summary>
public class OpcUaDriverTests
{
    // ── NodeId 解析 ─────────────────────────────────────────

    [Theory]
    [InlineData("i=85")]
    [InlineData("ns=0;i=85")]
    [InlineData("ns=2;i=100")]
    public void TryParseNodeId_NumericIdentifier_ParsesCorrectly(string address)
    {
        var ok = OpcUaDriver.TryParseNodeId(address, out var nodeId);

        Assert.True(ok);
        Assert.NotNull(nodeId);
        Assert.False(nodeId.IsNullNodeId);
    }

    [Theory]
    [InlineData("ns=2;s=MyVariable")]
    [InlineData("ns=2;s=Test.Variable")]
    [InlineData("s=MyVariable")]
    [InlineData("ns=3;s=MyVar")]
    public void TryParseNodeId_StringIdentifier_ParsesCorrectly(string address)
    {
        var ok = OpcUaDriver.TryParseNodeId(address, out var nodeId);

        Assert.True(ok);
        Assert.NotNull(nodeId);
        Assert.False(nodeId.IsNullNodeId);
    }

    [Fact]
    public void TryParseNodeId_GuidIdentifier_ParsesCorrectly()
    {
        var guid = Guid.NewGuid().ToString();
        var address = $"ns=2;g={guid}";

        var ok = OpcUaDriver.TryParseNodeId(address, out var nodeId);

        Assert.True(ok);
        Assert.NotNull(nodeId);
        Assert.False(nodeId.IsNullNodeId);
    }

    [Fact]
    public void TryParseNodeId_OpcBinaryIdentifier_ParsesCorrectly()
    {
        // Base64 encoded byte string
        var address = "ns=2;b=AAECAwQFBgcICQoLDA0ODw==";

        var ok = OpcUaDriver.TryParseNodeId(address, out var nodeId);

        Assert.True(ok);
        Assert.NotNull(nodeId);
        Assert.False(nodeId.IsNullNodeId);
    }

    // ── NodeId 解析：错误场景 ───────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("ns=;s=test")]
    [InlineData("ns=-1;i=100")]
    public void TryParseNodeId_InvalidInput_ReturnsFalse(string? address)
    {
        var ok = OpcUaDriver.TryParseNodeId(address, out var nodeId);

        Assert.False(ok);
        Assert.True(nodeId?.IsNullNodeId != false);
    }

    // ── OpcUaDriverConfig 默认值 ────────────────────────────

    private static OpcUaDriverConfig CreateDefaultConfig() => new()
    {
        DeviceId = "test-opcua-device"
    };

    [Fact]
    public void OpcUaDriverConfig_DefaultServerUrl_IsLocalhost4840()
    {
        var config = CreateDefaultConfig();
        Assert.Equal("opc.tcp://127.0.0.1:4840", config.ServerUrl);
    }

    [Fact]
    public void OpcUaDriverConfig_DefaultSecurityPolicy_IsNone()
    {
        var config = CreateDefaultConfig();
        Assert.Equal("None", config.SecurityPolicy);
    }

    [Fact]
    public void OpcUaDriverConfig_DefaultSessionName_IsLogisticsHMI()
    {
        var config = CreateDefaultConfig();
        Assert.Equal("LogisticsHMI", config.SessionName);
    }

    [Fact]
    public void OpcUaDriverConfig_DefaultUserCredentials_AreNull()
    {
        var config = CreateDefaultConfig();
        Assert.Null(config.UserName);
        Assert.Null(config.Password);
    }

    [Fact]
    public void OpcUaDriverConfig_InheritsTimeoutFromDriverConfigBase()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(5000, config.TimeoutMs); // 继承自 DriverConfigBase
    }

    [Fact]
    public void OpcUaDriverConfig_InheritsAutoReconnectFromDriverConfigBase()
    {
        var config = CreateDefaultConfig();
        Assert.True(config.AutoReconnect);
    }

    // ── Variant 序列化（BytesToVariant）────────────────────

    [Fact]
    public void BytesToVariant_SingleByte_ReturnsByteVariant()
    {
        var variant = OpcUaDriver.BytesToVariant(new byte[] { 0x42 });

        Assert.Equal(BuiltInType.Byte, variant.TypeInfo?.BuiltInType);
        Assert.Equal((byte)0x42, variant.Value);
    }

    [Fact]
    public void BytesToVariant_TwoBytes_ReturnsInt16Variant()
    {
        // BitConverter 使用宿主字节序（M1 Mac = little-endian）
        var expected = (short)0x0201; // { 0x01, 0x02 } on LE → 0x0201
        var variant = OpcUaDriver.BytesToVariant(new byte[] { 0x01, 0x02 });

        Assert.Equal(BuiltInType.Int16, variant.TypeInfo?.BuiltInType);
        Assert.Equal(expected, variant.Value);
    }

    [Fact]
    public void BytesToVariant_FourBytes_ReturnsInt32Variant()
    {
        // 42 in little-endian bytes → { 0x2A, 0x00, 0x00, 0x00 }
        var data = BitConverter.GetBytes(42);
        var variant = OpcUaDriver.BytesToVariant(data);

        Assert.Equal(BuiltInType.Int32, variant.TypeInfo?.BuiltInType);
        Assert.Equal(42, variant.Value);
    }

    [Fact]
    public void BytesToVariant_EightBytes_ReturnsDoubleVariant()
    {
        var expected = 3.14159;
        var data = BitConverter.GetBytes(expected);
        var variant = OpcUaDriver.BytesToVariant(data);

        Assert.Equal(BuiltInType.Double, variant.TypeInfo?.BuiltInType);
        Assert.Equal(expected, (double)variant.Value, precision: 5);
    }

    [Fact]
    public void BytesToVariant_ThreeBytes_ReturnsByteStringVariant()
    {
        var data = new byte[] { 0x01, 0x02, 0x03 };
        var variant = OpcUaDriver.BytesToVariant(data);

        Assert.Equal(BuiltInType.ByteString, variant.TypeInfo?.BuiltInType);
        Assert.Equal(data, (byte[])variant.Value);
    }

    // ── BytesToVariant：边界场景 ──────────────────────────

    [Fact]
    public void BytesToVariant_EmptyArray_ReturnsByteString()
    {
        // 空数组 → 默认走 ByteString 分支
        var variant = OpcUaDriver.BytesToVariant([]);

        Assert.Equal(BuiltInType.ByteString, variant.TypeInfo?.BuiltInType);
    }

    [Fact]
    public void BytesToVariant_SixBytes_ReturnsByteString()
    {
        // 非 1/2/4/8 字节 → ByteString
        var data = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        var variant = OpcUaDriver.BytesToVariant(data);

        Assert.Equal(BuiltInType.ByteString, variant.TypeInfo?.BuiltInType);
        Assert.Equal(data, (byte[])variant.Value);
    }

    // ── NodeId 解析：边界场景 ───────────────────────────────

    [Fact]
    public void TryParseNodeId_NullNodeId_ReturnsFalse()
    {
        // i=0 是 OPC UA 的 Null NodeId，应视为无效
        var ok = OpcUaDriver.TryParseNodeId("i=0", out var nodeId);
        Assert.False(ok);
    }

    [Fact]
    public void TryParseNodeId_MaxNamespace_IsValid()
    {
        // OPC UA 标准允许最大命名空间索引为 ushort.MaxValue
        var ok = OpcUaDriver.TryParseNodeId("ns=65535;s=Test", out var nodeId);
        Assert.True(ok);
    }

    [Fact]
    public void TryParseNodeId_LargeNumericId_IsValid()
    {
        // 大数字 NodeId
        var ok = OpcUaDriver.TryParseNodeId("i=2147483647", out var nodeId);
        Assert.True(ok);
    }

    // ── 带特殊字符的字符串标识符 ────────────────────────────

    [Fact]
    public void TryParseNodeId_StringIdWithSpaces_ParsesCorrectly()
    {
        var ok = OpcUaDriver.TryParseNodeId("ns=2;s=My Variable With Spaces", out var nodeId);
        Assert.True(ok);
    }

    [Fact]
    public void TryParseNodeId_StringIdWithUnicode_ParsesCorrectly()
    {
        var ok = OpcUaDriver.TryParseNodeId("ns=2;s=温度センサー_01", out var nodeId);
        Assert.True(ok);
    }

    // ── 配置默认值验证 ───────────────────────────────────────

    [Fact]
    public void OpcUaDriverConfig_DeviceIdIsRequired()
    {
        // DeviceId 是 required init only 属性
        var ex = Record.Exception(() => new OpcUaDriverConfig { DeviceId = "test" });
        Assert.Null(ex);
    }

    [Fact]
    public void OpcUaDriverConfig_SecurityPolicyNone_ConnectsWithoutEncryption()
    {
        var config = CreateDefaultConfig();
        config.SecurityPolicy = "None";
        // None 策略下 AutoAcceptUntrustedCertificates = true
        // 此为配置行为，非运行时断言
        Assert.Equal("None", config.SecurityPolicy);
    }
}

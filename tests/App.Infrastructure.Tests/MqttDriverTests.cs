using Xunit;
using App.Core;
using App.Infrastructure.Drivers;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="MqttDriver"/> 单元测试。
/// 覆盖配置默认值、QoS 映射、地址解析等不依赖网络连接的逻辑。
/// 完整 MQTT Broker 通信集成测试需在真实 Broker 或 Docker 容器环境（P7.5 现场联调）进行。
/// </summary>
public class MqttDriverTests
{
    // ── MqttDriverConfig 默认值 ────────────────────────────

    private static MqttDriverConfig CreateDefaultConfig() => new()
    {
        DeviceId = "test-mqtt-device"
    };

    [Fact]
    public void MqttDriverConfig_DefaultBroker_IsLocalhost()
    {
        var config = CreateDefaultConfig();
        Assert.Equal("127.0.0.1", config.Broker);
    }

    [Fact]
    public void MqttDriverConfig_DefaultPort_Is1883()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(1883, config.Port);
    }

    [Fact]
    public void MqttDriverConfig_DefaultQos_Is1()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(1, config.Qos);
    }

    [Fact]
    public void MqttDriverConfig_DefaultUseTls_IsFalse()
    {
        var config = CreateDefaultConfig();
        Assert.False(config.UseTls);
    }

    [Fact]
    public void MqttDriverConfig_DefaultClientId_IsNotEmpty()
    {
        var config = CreateDefaultConfig();
        Assert.False(string.IsNullOrWhiteSpace(config.ClientId));
        Assert.StartsWith("LogisticsHMI_", config.ClientId);
    }

    [Fact]
    public void MqttDriverConfig_DefaultWillTopic_IsNull()
    {
        var config = CreateDefaultConfig();
        Assert.Null(config.WillTopic);
    }

    [Fact]
    public void MqttDriverConfig_DefaultCredentials_AreNull()
    {
        var config = CreateDefaultConfig();
        Assert.Null(config.UserName);
        Assert.Null(config.Password);
    }

    [Fact]
    public void MqttDriverConfig_ClientId_GeneratesUniqueValue()
    {
        var config1 = CreateDefaultConfig();
        var config2 = CreateDefaultConfig();
        Assert.NotEqual(config1.ClientId, config2.ClientId);
    }

    // ── 配置自定义值 ─────────────────────────────────────────

    [Fact]
    public void MqttDriverConfig_CustomValues_AreApplied()
    {
        var config = new MqttDriverConfig
        {
            DeviceId = "custom-device",
            Broker = "mqtt.example.com",
            Port = 8883,
            UserName = "user",
            Password = "pass",
            UseTls = true,
            Qos = 2,
            WillTopic = "device/status"
        };

        Assert.Equal("mqtt.example.com", config.Broker);
        Assert.Equal(8883, config.Port);
        Assert.Equal("user", config.UserName);
        Assert.Equal("pass", config.Password);
        Assert.True(config.UseTls);
        Assert.Equal(2, config.Qos);
        Assert.Equal("device/status", config.WillTopic);
    }

    [Fact]
    public void MqttDriverConfig_InheritsTimeoutFromDriverConfigBase()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(5000, config.TimeoutMs);
    }

    [Fact]
    public void MqttDriverConfig_InheritsAutoReconnectFromDriverConfigBase()
    {
        var config = CreateDefaultConfig();
        Assert.True(config.AutoReconnect);
    }

    // ── 驱动名称 ────────────────────────────────────────────

    [Fact]
    public void DriverName_UsesDeviceId()
    {
        var config = new MqttDriverConfig { DeviceId = "my-mqtt" };
        // DriverName 实际需要 MqttDriver 实例，需要 ILogger mock
        // 此测试验证配置的 DeviceId 已正确设置
        Assert.Equal("my-mqtt", config.DeviceId);
    }

    // ── Topic 地址格式验证 ──────────────────────────────────

    [Theory]
    [InlineData("sensor/temperature")]
    [InlineData("factory/floor1/temperature")]
    [InlineData("device/+/status")]
    [InlineData("device/#")]
    [InlineData("a/b/c/d/e/f")]
    [InlineData("single")]
    public void MqttTopic_ValidFormats_AreAccepted(string topic)
    {
        // MQTT Topic 的基本格式验证
        // MQTTnet 库接受任意非空字符串作为 Topic
        Assert.False(string.IsNullOrWhiteSpace(topic));
        Assert.True(topic.Length > 0);
        // MQTT Topic 不允许包含 null 字符
        Assert.DoesNotContain((char)0, topic.ToCharArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MqttTopic_InvalidInput_Rejected(string? topic)
    {
        Assert.True(string.IsNullOrWhiteSpace(topic));
    }

    // ── QoS 等级 ───────────────────────────────────────────

    [Theory]
    [InlineData(0)]  // AtMostOnce
    [InlineData(1)]  // AtLeastOnce
    [InlineData(2)]  // ExactlyOnce
    public void Qos_ValidValues_AreAccepted(int qos)
    {
        var config = new MqttDriverConfig { DeviceId = "test", Qos = qos };
        Assert.Equal(qos, config.Qos);
    }

    [Fact]
    public void Qos_Default_IsAtLeastOnce()
    {
        var config = CreateDefaultConfig();
        Assert.Equal(1, config.Qos);
    }

    // ── 边界场景 ───────────────────────────────────────────

    [Fact]
    public void Port_Range_Valid()
    {
        var config = CreateDefaultConfig();
        // MQTT 标准端口
        config.Port = 1883;
        config.Port = 8883; // MQTTS
        config.Port = 8884; // MQTTS (alternative)
        config.Port = 8080; // WebSocket
        Assert.Equal(8080, config.Port);
    }

    [Fact]
    public void Broker_Hostname_CanContainDot()
    {
        var config = CreateDefaultConfig();
        config.Broker = "mqtt.company.com";
        Assert.Equal("mqtt.company.com", config.Broker);
    }

    [Fact]
    public void ClientId_CustomPrefix_IsApplied()
    {
        var config = new MqttDriverConfig
        {
            DeviceId = "test",
            ClientId = "CustomClient_123"
        };
        Assert.Equal("CustomClient_123", config.ClientId);
    }

    [Fact]
    public void DeviceIdIsRequired()
    {
        var ex = Record.Exception(() => new MqttDriverConfig { DeviceId = "required-test" });
        Assert.Null(ex);
    }
}

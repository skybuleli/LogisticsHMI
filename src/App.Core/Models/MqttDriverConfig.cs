namespace App.Core;

/// <summary>
/// MQTT 驱动配置。
/// </summary>
public sealed class MqttDriverConfig : DriverConfigBase
{
    /// <summary>
    /// 服务器地址。
    /// </summary>
    public string Broker { get; set; } = "127.0.0.1";

    /// <summary>
    /// 端口。
    /// </summary>
    public int Port { get; set; } = 1883;

    /// <summary>
    /// 用户名。
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 密码。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 客户端 ID。
    /// </summary>
    public string ClientId { get; set; } = $"LogisticsHMI_{Guid.NewGuid():N}";

    /// <summary>
    /// Will Topic。
    /// </summary>
    public string? WillTopic { get; set; }

    /// <summary>
    /// 是否启用 TLS。
    /// </summary>
    public bool UseTls { get; set; }

    /// <summary>
    /// QoS 等级。
    /// </summary>
    public int Qos { get; set; } = 1;
}

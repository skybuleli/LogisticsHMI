namespace App.Core;

/// <summary>
/// OPC UA 驱动配置。
/// </summary>
public sealed class OpcUaDriverConfig : DriverConfigBase
{
    /// <summary>
    /// 服务器 URL。
    /// </summary>
    public string ServerUrl { get; set; } = "opc.tcp://127.0.0.1:4840";

    /// <summary>
    /// 用户名。
    /// </summary>
    public string? UserName { get; set; }

    /// <summary>
    /// 密码。
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// 安全策略（None/Sign/Encrypt）。
    /// </summary>
    public string SecurityPolicy { get; set; } = "None";

    /// <summary>
    /// 会话名称。
    /// </summary>
    public string SessionName { get; set; } = "LogisticsHMI";
}

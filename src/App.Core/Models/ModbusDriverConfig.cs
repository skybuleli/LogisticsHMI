namespace App.Core;

/// <summary>
/// Modbus 驱动配置。
/// </summary>
public sealed class ModbusDriverConfig : DriverConfigBase
{
    /// <summary>
    /// 从机地址（1~247）。
    /// </summary>
    public byte SlaveAddress { get; set; } = 1;

    /// <summary>
    /// 服务器主机。
    /// </summary>
    public string Host { get; set; } = "127.0.0.1";

    /// <summary>
    /// TCP 端口。
    /// </summary>
    public int Port { get; set; } = 502;

    /// <summary>
    /// 如果为 true，TCP 连接后替换第一个请求的单元标识符为 <see cref="SlaveAddress"/>。
    /// </summary>
    public bool UseUnitId { get; set; } = true;
}

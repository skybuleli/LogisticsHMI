namespace App.Core;

/// <summary>
/// Siemens S7 驱动配置。
/// </summary>
public sealed class S7DriverConfig : DriverConfigBase
{
    /// <summary>
    /// PLC IP 地址。
    /// </summary>
    public string IpAddress { get; set; } = "192.168.0.1";

    /// <summary>
    /// Rack 号。
    /// </summary>
    public int Rack { get; set; } = 0;

    /// <summary>
    /// Slot 号。
    /// </summary>
    public int Slot { get; set; } = 2;

    /// <summary>
    /// TSAP 源（本地）。
    /// </summary>
    public ushort? LocalTsap { get; set; }

    /// <summary>
    /// TSAP 目标（PLC）。
    /// </summary>
    public ushort? RemoteTsap { get; set; }
}

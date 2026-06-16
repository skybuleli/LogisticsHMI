namespace App.Core;

/// <summary>
/// Siemens PLC CPU 类型（与 S7netplus 的 CpuType 枚举值对齐，
/// 但用字符串避免在 Core 层引入 S7netplus 依赖）。
/// </summary>
public static class S7CpuType
{
    public const string S7200 = "S7200";
    public const string S7300 = "S7300";
    public const string S7400 = "S7400";
    public const string S71200 = "S71200";
    public const string S71500 = "S71500";
    public const string Logo0BA8 = "Logo0BA8";
    public const string S7200Smart = "S7200Smart";
}

/// <summary>
/// Siemens S7 驱动配置。
/// </summary>
public sealed class S7DriverConfig : DriverConfigBase
{
    /// <summary>
    /// PLC CPU 类型。
    /// </summary>
    public string CpuType { get; set; } = S7CpuType.S71200;

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

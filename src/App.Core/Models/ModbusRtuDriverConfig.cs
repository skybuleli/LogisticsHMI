namespace App.Core;

/// <summary>
/// Modbus RTU 串口校验位（协议无关，避免在领域层依赖 <c>System.IO.Ports</c>）。
/// </summary>
public enum SerialParity
{
    /// <summary>无校验。</summary>
    None = 0,

    /// <summary>奇校验。</summary>
    Odd = 1,

    /// <summary>偶校验。</summary>
    Even = 2,

    /// <summary>标记校验（校验位恒为 1）。</summary>
    Mark = 3,

    /// <summary>空格校验（校验位恒为 0）。</summary>
    Space = 4
}

/// <summary>
/// Modbus RTU 串口停止位。
/// </summary>
public enum SerialStopBits
{
    /// <summary>无停止位（非法，仅占位）。</summary>
    None = 0,

    /// <summary>1 个停止位（最常用）。</summary>
    One = 1,

    /// <summary>1.5 个停止位。</summary>
    OnePointFive = 3,

    /// <summary>2 个停止位。</summary>
    Two = 2
}

/// <summary>
/// Modbus RTU 驱动配置（基于串口 RS-485 / RS-232）。
/// </summary>
public sealed class ModbusRtuDriverConfig : DriverConfigBase
{
    /// <summary>
    /// 从机地址（1~247）。
    /// </summary>
    public byte SlaveAddress { get; set; } = 1;

    /// <summary>
    /// 串口名称（Windows: "COM3"; Linux: "/dev/ttyUSB0"）。
    /// </summary>
    public string PortName { get; set; } = "COM1";

    /// <summary>
    /// 波特率（常见 9600 / 19200 / 38400 / 115200）。
    /// </summary>
    public int BaudRate { get; set; } = 9600;

    /// <summary>
    /// 数据位（7 或 8）。
    /// </summary>
    public int DataBits { get; set; } = 8;

    /// <summary>
    /// 校验位。
    /// </summary>
    public SerialParity Parity { get; set; } = SerialParity.None;

    /// <summary>
    /// 停止位。
    /// </summary>
    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

    /// <summary>
    /// 帧间静默间隔（毫秒）。
    /// <para>
    /// RTU 规范要求两帧之间至少 3.5 个字符时间的静默。低于此值时主站不能发送下一帧。
    /// 设为 <c>0</c> 或负数时按波特率自动计算（9600 以上 ≈ 1.75ms；≤19200 按 3.5 字符）。
    /// </para>
    /// </summary>
    public int InterFrameDelayMs { get; set; } = 0;

    /// <summary>
    /// 串口同步读取的超时（毫秒），仅作为兜底（异步路径另有 CancellationToken）。
    /// 设为 <c>0</c> 或负数时使用 <see cref="DriverConfigBase.TimeoutMs"/>。
    /// </summary>
    public int ReadTimeoutMs { get; set; } = 0;

    /// <summary>
    /// 计算实际生效的帧间间隔（毫秒）。
    /// 显式配置优先；否则按 Modbus RTU 3.5 字符规则推导。
    /// </summary>
    public int GetEffectiveInterFrameDelayMs()
    {
        if (InterFrameDelayMs > 0)
        {
            return InterFrameDelayMs;
        }

        // RTU 规范：波特率 > 19200 时固定 1.75ms；否则按 3.5 字符时间计算。
        // 每字符 = 11 位（1 起始 + 8 数据 + 1 校验 + 1 停止）。
        if (BaudRate > 19200)
        {
            return 2;
        }

        var charTimeMs = 11.0 * 1000.0 / BaudRate;
        return (int)Math.Ceiling(3.5 * charTimeMs);
    }
}

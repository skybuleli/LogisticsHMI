using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Drivers;

/// <summary>
/// 虚拟设备驱动。
/// 以纯内存方式模拟设备地址空间，支持三种行为 profile 的自动化仿真。
/// </summary>
public sealed class VirtualDeviceDriver : IDeviceDriver
{
    private readonly VirtualDeviceDriverConfig _config;
    private readonly ILogger<VirtualDeviceDriver> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Action<byte[]>>> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    private ConnectionState _state = ConnectionState.Disconnected;
    private Timer? _behaviorTimer;
    private int _behaviorTick; // seconds since behavior start
    private bool _disposed;

    public VirtualDeviceDriver(VirtualDeviceDriverConfig config, ILogger<VirtualDeviceDriver> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string DriverName => $"VirtualDevice-{_config.DeviceId}";

    public DriverConfigBase Config => _config;

    public ConnectionState State
    {
        get => _state;
        private set
        {
            var oldState = _state;
            _state = value;
            if (oldState != value)
            {
                ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(oldState, value));
            }
        }
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public ValueTask<Result> ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (State == ConnectionState.Connected)
        {
            return ValueTask.FromResult(Result.Success());
        }

        State = ConnectionState.Connecting;
        State = ConnectionState.Connected;

        _logger.LogInformation("{Driver} 已连接，模拟类型 {Profile}", DriverName, _config.DeviceProfile);

        // 初始化设备默认状态并启动行为仿真
        InitDeviceState();
        StartBehavior();

        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result> DisconnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (State == ConnectionState.Disconnected)
        {
            return ValueTask.FromResult(Result.Success());
        }

        State = ConnectionState.Disconnecting;
        StopBehavior();
        State = ConnectionState.Disconnected;

        _logger.LogInformation("{Driver} 已断开", DriverName);
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (State != ConnectionState.Connected)
        {
            return ValueTask.FromResult(Result<byte[]>.Failure("设备未连接"));
        }

        if (!TryNormalizeAddress(address, out var normalized))
        {
            return ValueTask.FromResult(Result<byte[]>.Failure($"无效地址格式: {address}"));
        }

        var data = _memory.TryGetValue(normalized, out var existing)
            ? CopyOrPad(existing, length)
            : new byte[length];

        return ValueTask.FromResult(Result<byte[]>.Success(data));
    }

    public ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(data);

        if (State != ConnectionState.Connected)
        {
            return ValueTask.FromResult(Result.Failure("设备未连接"));
        }

        if (!TryNormalizeAddress(address, out var normalized))
        {
            return ValueTask.FromResult(Result.Failure($"无效地址格式: {address}"));
        }

        var snapshot = data.ToArray();
        _memory[normalized] = snapshot;
        NotifySubscribers(normalized, snapshot);

        // Stacker profile: 写入指令时触发移动
        if (_config.DeviceProfile == "Stacker" && normalized == "R:2")
        {
            OnStackerCommand();
        }

        _logger.LogDebug("{Driver} 写入地址 {Address} ({Length} bytes)", DriverName, normalized, snapshot.Length);
        return ValueTask.FromResult(Result.Success());
    }

    public ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(callback);

        if (!TryNormalizeAddress(address, out var normalized))
        {
            throw new ArgumentException($"无效地址格式: {address}", nameof(address));
        }

        var subscriptionId = Guid.NewGuid();
        var callbacks = _subscriptions.GetOrAdd(normalized, _ => new ConcurrentDictionary<Guid, Action<byte[]>>());
        callbacks[subscriptionId] = callback;

        return ValueTask.FromResult<IDisposable>(new Subscription(() =>
        {
            if (_subscriptions.TryGetValue(normalized, out var handlers))
            {
                handlers.TryRemove(subscriptionId, out _);
                if (handlers.IsEmpty)
                {
                    _subscriptions.TryRemove(normalized, out _);
                }
            }
        }));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        StopBehavior();
        _memory.Clear();
        _subscriptions.Clear();
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    // ── 地址规范化 ──────────────────────────────────────────────

    internal static bool TryNormalizeAddress(string? address, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var trimmed = address.Trim();
        if (!trimmed.Contains(':'))
        {
            normalized = $"R:{trimmed}";
            return int.TryParse(trimmed, out _);
        }

        var parts = trimmed.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out _))
        {
            return false;
        }

        var prefix = parts[0].ToUpperInvariant();
        if (prefix is not ("R" or "REG" or "REGISTER" or "C" or "COIL" or "DI" or "AI"))
        {
            return false;
        }

        normalized = $"{prefix}:{parts[1]}";
        return true;
    }

    // ── 行为仿真引擎 ─────────────────────────────────────────────

    /// <summary>
    /// 根据设备 profile 初始化内存状态。
    /// </summary>
    private void InitDeviceState()
    {
        switch (_config.DeviceProfile)
        {
            case "Conveyor":
                WriteReg("R:0", 1);          // 状态: 运行中
                WriteReg("R:1", 1200);        // 速度: 1200 rpm
                WriteReg("R:10", 0);          // 累计计数
                WriteReg("R:11", 1000);       // 运行时长(秒)
                WriteMemory("DI:0", [0x01]);   // 电机运行指示
                WriteMemory("AI:0", BitConverter.GetBytes(65_000)); // 温度 ~65°C * 1000
                break;

            case "Stacker":
                WriteReg("R:0", 0);           // 当前位置: 0mm
                WriteReg("R:1", 0);           // 目标位置: 0mm
                WriteReg("R:2", 0);           // 控制指令: 空闲
                WriteReg("R:3", 200);         // 速度: 200 mm/s
                WriteReg("R:4", 0);           // 状态: 空闲
                WriteReg("R:10", 5000);       // 最大行程: 5000mm
                WriteMemory("DI:0", [0x00]);   // 运行中指示
                break;

            case "Sensor":
                WriteMemory("DI:0", [0x00]);   // 物体检测
                WriteMemory("DI:1", [0x00]);   // 错误标志
                WriteReg("R:0", 0);           // 检测计数
                WriteReg("R:1", 10);          // 频率: 10 Hz
                WriteMemory("AI:0", BitConverter.GetBytes(5000)); // 模拟量 5000
                break;

            default: // Generic — 留空，用户自行写入
                break;
        }
    }

    /// <summary>
    /// 启动行为仿真定时器（每秒 tick 一次）。
    /// </summary>
    private void StartBehavior()
    {
        if (_disposed) return;
        _behaviorTick = 0;
        _behaviorTimer = new Timer(
            _ => TickBehavior(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// 停止行为仿真定时器。
    /// </summary>
    private void StopBehavior()
    {
        _behaviorTimer?.Dispose();
        _behaviorTimer = null;
    }

    /// <summary>
    /// 每秒执行一次的行为更新。
    /// </summary>
    private void TickBehavior()
    {
        if (_disposed) return;
        _behaviorTick++;

        try
        {
            switch (_config.DeviceProfile)
            {
                case "Conveyor":
                    TickConveyor();
                    break;
                case "Stacker":
                    TickStacker();
                    break;
                case "Sensor":
                    TickSensor();
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Driver} 行为仿真 tick 异常", DriverName);
        }
    }

    // ── Conveyor 输送线 ──────────────────────────────────────

    /// <summary>
    /// 输送线行为：
    /// - 每 5秒 产出一个物件（R:10++)
    /// - 每 30秒 模拟一次故障（R:0=2），5秒后自动恢复
    /// - 速度 R:1 在 ±50 范围内正弦波动
    /// - AI:0 温度波动
    /// </summary>
    private void TickConveyor()
    {
        var tick = _behaviorTick;

        // 读取当前状态
        var status = ReadReg("R:0");

        if (status == 2) // 故障中
        {
            if (tick % 35 >= 30 && tick % 35 < 35) // 在第30-34秒故障
            {
                // 仍在故障
                WriteMemory("DI:0", [0x00]);
                WriteMemory("AI:0", Int16ToBytes((short)(65_000 + (tick % 5) * 500))); // 温度波动
                return;
            }
            // 恢复
            WriteReg("R:0", 1);
            WriteMemory("DI:0", [0x01]);
            _logger.LogInformation("{Driver} 输送线故障恢复", DriverName);
            return;
        }

        // 正常运行
        // 第30-34秒触发故障
        if (tick > 0 && tick % 35 >= 30 && tick % 35 < 35)
        {
            WriteReg("R:0", 2);
            WriteMemory("DI:0", [0x00]);
            _logger.LogInformation("{Driver} 输送线进入故障", DriverName);
            return;
        }

        // 每 5 秒产出一个物件
        if (tick % 5 == 0)
        {
            var count = ReadReg("R:10");
            WriteReg("R:10", count + 1);
            // 同时翻转 DI 输入模拟检测信号
            var detected = ReadMemory("DI:1")?[0] == 0x01 ? (byte)0x00 : (byte)0x01;
            WriteMemory("DI:1", new[] { detected });
        }

        // 速度正弦波动: 1200 ± 50，周期约 20秒
        var speedBase = 1200;
        var speedDelta = (int)(Math.Sin(tick * Math.PI / 10) * 50);
        WriteReg("R:1", speedBase + speedDelta);

        // 温度缓慢上升+波动
        var tempBase = 65_000;
        var tempDelta = (int)(Math.Sin(tick * Math.PI / 30) * 3_000) + (tick % 60) * 100;
        WriteMemory("AI:0", Int16ToBytes((short)(tempBase + tempDelta)));

        // 运行时长更新
        WriteReg("R:11", ReadReg("R:11") + 1);
    }

    // ── Stacker 堆垛机 ───────────────────────────────────────

    /// <summary>
    /// 堆垛机行为：
    /// - 收到控制指令 R:2 = 1 时，向目标位置 R:1 移动
    /// - 收到 R:2 = 2 时，返回原点 (0)
    /// - 当前位置 R:0 每 tick 向目标靠近 speed mm/s (R:3)
    /// - 到达后设置 R:4 = 2 (已定位)
    /// </summary>
    private void TickStacker()
    {
        var currentPos = ReadReg("R:0");
        var targetPos = ReadReg("R:1");
        var cmd = ReadReg("R:2");
        var speed = ReadReg("R:3");
        if (speed <= 0) speed = 200;

        if (cmd == 0) // 空闲
        {
            // 位置不动，状态保持
            return;
        }

        // 正在移动
        if (currentPos != targetPos)
        {
            WriteReg("R:4", 1); // 移动中
            WriteMemory("DI:0", [0x01]);

            var distance = Math.Abs(targetPos - currentPos);
            var step = Math.Min(speed, distance);
            var newPos = currentPos < targetPos ? currentPos + step : currentPos - step;

            WriteReg("R:0", newPos);
            _logger.LogTrace("{Driver} 堆垛机移动: {Current} -> {Target}", DriverName, newPos, targetPos);

            if (newPos == targetPos)
            {
                WriteReg("R:4", 2); // 已定位
                WriteReg("R:2", 0); // 指令复位
                WriteMemory("DI:0", [0x00]);
                _logger.LogInformation("{Driver} 堆垛机到达目标位置 {Target}mm", DriverName, targetPos);
            }
        }
    }

    /// <summary>
    /// 堆垛机指令处理（WriteAsync 触发）。
    /// 从 R:2 读取完整指令值：1=移动到目标，2=回原点。
    /// </summary>
    private void OnStackerCommand()
    {
        var cmd = ReadReg("R:2");
        if (cmd == 2) // 回原点
        {
            WriteReg("R:1", 0);
            _logger.LogInformation("{Driver} 堆垛机收到回原点指令", DriverName);
        }
        // cmd == 1 由目标位置 R:1 驱动，TickStacker 中处理移动过程
    }

    // ── Sensor 传感器 ────────────────────────────────────────

    /// <summary>
    /// 传感器行为：
    /// - DI:0 每 500ms 翻转（模拟物体经过）
    /// - AI:0 模拟量正弦波动
    /// - R:0 检测计数累积
    /// </summary>
    private void TickSensor()
    {
        var tick = _behaviorTick;

        // DI:0 每 1 秒翻转一次（偶数秒有物体，奇数秒无）
        var detected = tick % 2 == 0;
        WriteMemory("DI:0", detected ? [0x01] : [0x00]);
        WriteMemory("DI:1", [0x00]); // 正常模式

        // 物体经过时计数
        if (detected)
        {
            var count = ReadReg("R:0");
            WriteReg("R:0", count + 1);
        }

        // AI:0 正弦模拟量: 5000 ± 3000, 周期约 60秒
        var aiVal = (int)(5000 + Math.Sin(tick * Math.PI / 30) * 3000);
        WriteMemory("AI:0", Int16ToBytes((short)aiVal));

        // DI:1 每 30 秒模拟一次错误脉冲
        WriteMemory("DI:1", tick % 30 == 0 && tick > 0 ? [0x01] : [0x00]);
    }

    // ── 内存读写辅助 ─────────────────────────────────────────

    /// <summary>
    /// 写入内存并通知订阅者。
    /// </summary>
    private void WriteMemory(string normalized, byte[] data)
    {
        _memory[normalized] = data;
        NotifySubscribers(normalized, data);
    }

    /// <summary>
    /// 读取内存值，不存在返回全零。
    /// </summary>
    private byte[] ReadMemory(string normalized)
    {
        return _memory.TryGetValue(normalized, out var data) ? data : [];
    }

    /// <summary>
    /// 写入 4 字节寄存器（大端 int32）。
    /// </summary>
    private void WriteReg(string normalized, int value)
    {
        var bytes = new byte[4];
        bytes[0] = (byte)((value >> 24) & 0xFF);
        bytes[1] = (byte)((value >> 16) & 0xFF);
        bytes[2] = (byte)((value >> 8) & 0xFF);
        bytes[3] = (byte)(value & 0xFF);
        WriteMemory(normalized, bytes);
    }

    /// <summary>
    /// 读取 4 字节大端寄存器值，不存在返回 0。
    /// </summary>
    private int ReadReg(string normalized)
    {
        if (_memory.TryGetValue(normalized, out var data) && data.Length >= 4)
        {
            return (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        }
        return 0;
    }

    private static byte[] CopyOrPad(byte[] source, ushort length)
    {
        if (source.Length == length)
        {
            return source.ToArray();
        }

        var buffer = new byte[length];
        Array.Copy(source, buffer, Math.Min(source.Length, buffer.Length));
        return buffer;
    }

    private static byte[] Int16ToBytes(short value)
    {
        return BitConverter.GetBytes(value);
    }

    private void NotifySubscribers(string address, byte[] data)
    {
        if (!_subscriptions.TryGetValue(address, out var handlers))
        {
            return;
        }

        foreach (var handler in handlers.Values)
        {
            handler(data.ToArray());
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public Subscription(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _dispose();
            }
        }
    }
}

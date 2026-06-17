using App.Core;
using App.Infrastructure.Drivers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace App.Infrastructure.Tests;

public class VirtualDeviceDriverTests
{
    private static VirtualDeviceDriver CreateDriver(string profile = "Generic")
    {
        var config = new VirtualDeviceDriverConfig
        {
            DeviceId = "virtual-1",
            DeviceProfile = profile
        };

        return new VirtualDeviceDriver(config, LoggerFactory.Create(_ => { }).CreateLogger<VirtualDeviceDriver>());
    }

    // ═══════════════════════════════════════════════════════════
    // 地址解析
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("100", "R:100")]
    [InlineData("R:100", "R:100")]
    [InlineData("reg:10", "REG:10")]
    [InlineData("coil:5", "COIL:5")]
    [InlineData("AI:3", "AI:3")]
    public void TryNormalizeAddress_ValidInput_ReturnsNormalizedValue(string input, string expected)
    {
        var ok = VirtualDeviceDriver.TryNormalizeAddress(input, out var normalized);

        Assert.True(ok);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("R:abc")]
    [InlineData("BAD:1")]
    public void TryNormalizeAddress_InvalidInput_ReturnsFalse(string input)
    {
        Assert.False(VirtualDeviceDriver.TryNormalizeAddress(input, out _));
    }

    // ═══════════════════════════════════════════════════════════
    // 基本操作
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task ConnectAsync_ChangesStateToConnected()
    {
        await using var driver = CreateDriver();

        var result = await driver.ConnectAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(ConnectionState.Connected, driver.State);
    }

    [Fact]
    public async Task ReadAsync_UninitializedAddress_ReturnsZeroFilledBuffer()
    {
        await using var driver = CreateDriver();
        await driver.ConnectAsync();

        var result = await driver.ReadAsync("100", 4);

        Assert.True(result.IsSuccess);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], result.Value);
    }

    [Fact]
    public async Task WriteThenRead_ReturnsSameData()
    {
        await using var driver = CreateDriver("Stacker");
        await driver.ConnectAsync();

        var writeResult = await driver.WriteAsync("R:100", [0x01, 0x02, 0x03, 0x04]);
        var readResult = await driver.ReadAsync("R:100", 4);

        Assert.True(writeResult.IsSuccess);
        Assert.True(readResult.IsSuccess);
        Assert.Equal([0x01, 0x02, 0x03, 0x04], readResult.Value);
    }

    [Fact]
    public async Task SubscribeAsync_WriteTriggersCallback()
    {
        await using var driver = CreateDriver("Sensor");
        await driver.ConnectAsync();

        byte[]? received = null;
        using var subscription = await driver.SubscribeAsync("C:1", data => received = data);

        await driver.WriteAsync("C:1", [0xFF]);

        Assert.Equal([0xFF], received);
    }

    [Fact]
    public void Factory_CreateVirtualDevice_ReturnsVirtualDeviceDriver()
    {
        var factory = new DeviceDriverFactory(LoggerFactory.Create(_ => { }));
        var config = new DeviceConfigEntry
        {
            DeviceId = "virtual-1",
            DriverType = "VirtualDevice",
            DisplayName = "虚拟输送线"
        };

        var driver = factory.Create(config);

        Assert.IsType<VirtualDeviceDriver>(driver);
        Assert.Equal("VirtualDevice-virtual-1", driver.DriverName);
        Assert.IsType<VirtualDeviceDriverConfig>(driver.Config);
    }

    // ═══════════════════════════════════════════════════════════
    // 行为仿真 — Conveyor
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Conveyor_Connect_InitializesRegisters()
    {
        await using var driver = CreateDriver("Conveyor");
        await driver.ConnectAsync();

        var status = await driver.ReadAsync("0", 4);
        var speed = await driver.ReadAsync("1", 4);

        Assert.True(status.IsSuccess);
        Assert.True(speed.IsSuccess);

        // R:0 = 1 (运行中)
        Assert.Equal(1, ReadInt32(status.Value));
        // R:1 = 1200 (速度 rpm)
        Assert.Equal(1200, ReadInt32(speed.Value));
    }

    [Fact]
    public async Task Conveyor_AfterFewSeconds_CountIncreases()
    {
        await using var driver = CreateDriver("Conveyor");
        await driver.ConnectAsync();

        // 等 6 秒让输送线产出至少 1 个物件
        await Task.Delay(6200);

        var count = await driver.ReadAsync("10", 4);

        Assert.True(count.IsSuccess);
        // 每 5 秒产出 1 个，6 秒内至少 1 个
        Assert.True(ReadInt32(count.Value) >= 1, $"期望计数 >= 1，实际 {ReadInt32(count.Value)}");
    }

    [Fact]
    public async Task Conveyor_SpeedFluctuates()
    {
        await using var driver = CreateDriver("Conveyor");
        await driver.ConnectAsync();

        // 等一小段时间
        await Task.Delay(1500);

        var speed = await driver.ReadAsync("1", 4);
        Assert.True(speed.IsSuccess);

        var speedVal = ReadInt32(speed.Value);
        // 速度应在 1150~1250 范围内波动（1200 ± 50）
        Assert.InRange(speedVal, 1140, 1260);
    }

    // ═══════════════════════════════════════════════════════════
    // 行为仿真 — Stacker
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Stacker_Connect_InitializesRegisters()
    {
        await using var driver = CreateDriver("Stacker");
        await driver.ConnectAsync();

        var pos = await driver.ReadAsync("0", 4);
        var cmd = await driver.ReadAsync("2", 4);
        var status = await driver.ReadAsync("4", 4);

        Assert.Equal(0, ReadInt32(pos.Value));    // 当前位置 = 0
        Assert.Equal(0, ReadInt32(cmd.Value));    // 指令 = 空闲
        Assert.Equal(0, ReadInt32(status.Value)); // 状态 = 空闲
    }

    [Fact]
    public async Task Stacker_SetTargetThenCommand_MovesTowardTarget()
    {
        await using var driver = CreateDriver("Stacker");
        await driver.ConnectAsync();

        // 设置目标位置 = 1000mm（4 字节大端）
        await driver.WriteAsync("1", Int32ToBytes(1000));
        // 发送移动指令（4 字节大端，值=1）
        await driver.WriteAsync("2", Int32ToBytes(1));

        // 等 4 秒让堆垛机移动（200 mm/s × 4 tick ≈ 600mm）
        await Task.Delay(4500);

        var currentPos = await driver.ReadAsync("0", 4);
        var posVal = ReadInt32(currentPos.Value);
        Assert.True(posVal > 0, $"期望位置 > 0，实际 {posVal}");
        // 200 mm/s × 至少 3 个有效 tick = 600mm
        Assert.True(posVal >= 400, $"期望位置 >= 400，实际 {posVal}");
    }

    [Fact]
    public async Task Stacker_ReturnHome_CommandMovesToZero()
    {
        await using var driver = CreateDriver("Stacker");
        await driver.ConnectAsync();

        // 先设目标并移动一段
        await driver.WriteAsync("1", Int32ToBytes(500));
        await driver.WriteAsync("2", Int32ToBytes(1));
        await Task.Delay(3000); // 移动约 600mm

        // 发送回原点指令
        await driver.WriteAsync("2", Int32ToBytes(2));
        await Task.Delay(3000); // 等待移动回原点

        var pos = await driver.ReadAsync("0", 4);
        // 应该回到 0 或在接近 0
        Assert.InRange(ReadInt32(pos.Value), 0, 10);
    }

    // ═══════════════════════════════════════════════════════════
    // 行为仿真 — Sensor
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Sensor_Connect_InitializesRegisters()
    {
        await using var driver = CreateDriver("Sensor");
        await driver.ConnectAsync();

        var count = await driver.ReadAsync("0", 4);
        var freq = await driver.ReadAsync("1", 4);

        Assert.Equal(0, ReadInt32(count.Value));   // 初始计数 = 0
        Assert.Equal(10, ReadInt32(freq.Value));   // 频率 = 10 Hz
    }

    [Fact]
    public async Task Sensor_AfterFewSeconds_DiTogglesAndCountGrows()
    {
        await using var driver = CreateDriver("Sensor");
        await driver.ConnectAsync();

        // 等 3 秒让传感器产生几个检测事件
        await Task.Delay(3200);

        var count = await driver.ReadAsync("0", 4);
        Assert.True(ReadInt32(count.Value) >= 1, $"期望计数 >= 1，实际 {ReadInt32(count.Value)}");
    }

    // ═══════════════════════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task Disconnect_StopsBehavior()
    {
        await using var driver = CreateDriver("Conveyor");
        await driver.ConnectAsync();

        // 先运行一会儿
        await Task.Delay(2000);

        await driver.DisconnectAsync();
        var countBeforeDisconnect = (await driver.ReadAsync("10", 4)).Value;

        // 等 3 秒后再读，计数不应变化
        await Task.Delay(3000);
        var countAfter = (await driver.ReadAsync("10", 4)).Value;

        // 断开后行为停止，计数应不变
        Assert.Equal(ReadInt32(countBeforeDisconnect), ReadInt32(countAfter));
    }

    [Fact]
    public async Task Dispose_StopsTimer_NoError()
    {
        var driver = CreateDriver("Conveyor");
        await driver.ConnectAsync();

        await driver.DisposeAsync(); // 不应抛异常
        await driver.DisposeAsync(); // 二次释放也不应抛
    }

    // ═══════════════════════════════════════════════════════════
    // 辅助方法
    // ═══════════════════════════════════════════════════════════

    private static int ReadInt32(byte[]? data)
    {
        if (data == null || data.Length < 4) return 0;
        return (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
    }

    private static byte[] Int32ToBytes(int value)
    {
        return
        [
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        ];
    }
}

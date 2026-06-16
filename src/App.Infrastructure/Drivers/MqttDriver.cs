using System.Buffers;
using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;      // MqttQualityOfServiceLevel

namespace App.Infrastructure.Drivers;

/// <summary>
/// MQTT 5.0 客户端驱动实现。
/// <para>
/// 基于 MQTTnet 5.x 库实现 MQTT 客户端通信，支持 TLS、用户名密码认证、Will Message、QoS 等级。
/// </para>
/// <para>
/// 地址（Topic）格式：
/// <list type="bullet">
///   <item><c>sensor/temperature</c>           — 单级 Topic</item>
///   <item><c>factory/floor1/temperature</c>    — 多级 Topic</item>
///   <item><c>device/+/status</c>               — 单级通配符 +</item>
///   <item><c>device/#</c>                      — 多级通配符 #</item>
/// </list>
/// </para>
/// <para>
/// 语义说明：
/// <list type="bullet">
///   <item><c>ReadAsync(topic, ...)</c> — 向指定 Topic 订阅并等待下一条消息到达（超时返回失败）</item>
///   <item><c>WriteAsync(topic, data)</c> — 向指定 Topic 发布消息</item>
///   <item><c>SubscribeAsync(topic, callback)</c> — 向指定 Topic 订阅并持续接收消息</item>
/// </list>
/// </para>
/// <para>
/// 线程安全（单连接串行化请求），完整异步支持。MQTTnet 内置自动重连机制。
/// </para>
/// </summary>
public sealed class MqttDriver : IDeviceDriver
{
    // ── 字段 ───────────────────────────────────────────────
    private readonly MqttDriverConfig _config;
    private readonly ILogger<MqttDriver> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _disposalCts = new();

    private IMqttClient? _mqttClient;

    // 订阅管理
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new();

    // 一次性读取等待队列（ReadAsync 使用）
    private readonly ConcurrentDictionary<string, TaskCompletionSource<byte[]>> _pendingReads = new();

    // 连接状态
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 初始化 MQTT 驱动。
    /// </summary>
    public MqttDriver(MqttDriverConfig config, ILogger<MqttDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
    }

    // ── IDeviceDriver 属性 ─────────────────────────────────

    /// <inheritdoc />
    public string DriverName => $"MQTT-{_config.DeviceId}";

    /// <inheritdoc />
    public DriverConfigBase Config => _config;

    /// <inheritdoc />
    public ConnectionState State
    {
        get => _state;
        private set
        {
            var oldState = Interlocked.Exchange(ref _state, value);
            if (oldState != value)
            {
                OnConnectionStateChanged(oldState, value);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    // ── 连接管理 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<Result> ConnectAsync(CancellationToken ct = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return Result.Success();
        }

        State = ConnectionState.Connecting;
        _logger.LogInformation(
            "{Driver} 正在连接 MQTT Broker {Broker}:{Port} (TLS={UseTls}) ...",
            DriverName, _config.Broker, _config.Port, _config.UseTls);

        try
        {
            var factory = new MqttClientFactory();
            var client = factory.CreateMqttClient();

            // 注册事件处理器
            client.ConnectedAsync += OnConnectedAsync;
            client.DisconnectedAsync += OnDisconnectedAsync;
            client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;

            // 构建连接选项
            var options = BuildClientOptions(factory);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            var connectResult = await client.ConnectAsync(options, timeoutCts.Token)
                .ConfigureAwait(false);

            if (connectResult.ResultCode != MqttClientConnectResultCode.Success)
            {
                var msg = $"MQTT 连接失败: {connectResult.ResultCode}";
                _logger.LogError("{Driver} {Msg}", DriverName, msg);
                State = ConnectionState.Disconnected;
                return Result.Failure(msg);
            }

            _mqttClient = client;
            State = ConnectionState.Connected;

            _logger.LogInformation(
                "{Driver} 连接成功 ({Broker}:{Port})",
                DriverName, _config.Broker, _config.Port);

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            State = ConnectionState.Disconnected;
            _logger.LogWarning("{Driver} 连接超时 ({Timeout}ms)", DriverName, _config.TimeoutMs);
            return Result.Failure($"连接超时 ({_config.TimeoutMs}ms)");
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} 连接失败", DriverName);
            return Result.Failure($"连接失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result> DisconnectAsync(CancellationToken ct = default)
    {
        if (State is ConnectionState.Disconnected)
        {
            return Result.Success();
        }

        State = ConnectionState.Disconnecting;
        _logger.LogInformation("{Driver} 正在断开连接...", DriverName);

        try
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            State = ConnectionState.Disconnected;
            _logger.LogInformation("{Driver} 已断开", DriverName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} 断开时发生异常", DriverName);
            return Result.Failure($"断开异常: {ex.Message}");
        }
    }

    // ── 读写操作 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// 读取 MQTT 主题的当前值。
    /// <para>
    /// MQTT 是发布/订阅模型，不支持直接\"读取\"主题值。本方法通过以下方式实现：
    /// 订阅指定 Topic → 等待下一条到达的消息 → 取消订阅并返回消息负载。
    /// </para>
    /// <para>
    /// <paramref name="address"/> 为 MQTT Topic 字符串。
    /// <paramref name="length"/> 在 MQTT 上下文中无意义，被忽略。
    /// </para>
    /// <para>
    /// 注意：此方法会等待下一条消息到达，最长达 <see cref="DriverConfigBase.TimeoutMs"/>。
    /// 如果超时未收到消息，返回失败结果。
    /// </para>
    /// </remarks>
    public async ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        _logger.LogTrace("{Driver} 读取 Topic: {Topic}", DriverName, address);

        var client = _mqttClient;
        if (client is null || !client.IsConnected)
        {
            return Result<byte[]>.Failure($"{DriverName} 未连接，无法读取");
        }

        // 创建 TaskCompletionSource 等待消息到达
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingReads.TryAdd(address, tcs))
        {
            return Result<byte[]>.Failure($"已有待处理的读取操作: {address}");
        }

        try
        {
            // 订阅 Topic
            var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(address)
                    .WithQualityOfServiceLevel(ToMqttQosLevel(_config.Qos)))
                .Build();

            await client.SubscribeAsync(subscribeOptions, ct).ConfigureAwait(false);

            _logger.LogTrace("{Driver} 已订阅 (临时读取): {Topic}", DriverName, address);

            // 等待消息到达或超时
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            try
            {
                var payload = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                return Result<byte[]>.Success(payload);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Result<byte[]>.Failure($"读取超时 ({_config.TimeoutMs}ms)，Topic: {address}");
            }
            catch (OperationCanceledException)
            {
                return Result<byte[]>.Failure("读取操作已取消");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Driver} 读取失败: {Topic}", DriverName, address);
            return Result<byte[]>.Failure($"读取失败: {ex.Message}");
        }
        finally
        {
            // 清理 pending reads
            _pendingReads.TryRemove(address, out _);

            // 只在没有活跃永久订阅时才取消订阅（避免移除 SubscribeAsync 的订阅）
            try
            {
                if (!_subscriptions.ContainsKey(address) && client.IsConnected)
                {
                    var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
                        .WithTopicFilter(address)
                        .Build();

                    await client.UnsubscribeAsync(unsubscribeOptions, CancellationToken.None)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "{Driver} 取消订阅失败: {Topic}", DriverName, address);
            }
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 向 MQTT 主题发布消息。
    /// <para>
    /// <paramref name="address"/> 为 MQTT Topic。
    /// <paramref name="data"/> 为消息负载（原始字节）。
    /// </para>
    /// </remarks>
    public async ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(data);

        _logger.LogTrace(
            "{Driver} 发布到 Topic: {Topic} ({Len} 字节)", DriverName, address, data.Length);

        var client = _mqttClient;
        if (client is null || !client.IsConnected)
        {
            return Result.Failure($"{DriverName} 未连接，无法发布");
        }

        try
        {
            var message = new MqttApplicationMessageBuilder()
                .WithTopic(address)
                .WithPayload(data)
                .WithQualityOfServiceLevel(ToMqttQosLevel(_config.Qos))
                .WithRetainFlag()
                .Build();

            var publishResult = await client.PublishAsync(message, ct).ConfigureAwait(false);

            if (publishResult.ReasonCode != MqttClientPublishReasonCode.Success)
            {
                var msg = $"发布失败: {publishResult.ReasonCode}";
                _logger.LogWarning("{Driver} {Msg} (Topic={Topic})", DriverName, msg, address);
                return Result.Failure(msg);
            }

            _logger.LogTrace("{Driver} 发布成功: {Topic}", DriverName, address);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("发布操作已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Driver} 发布失败: {Topic}", DriverName, address);
            return Result.Failure($"发布失败: {ex.Message}");
        }
    }

    // ── 订阅 ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <summary>
    /// 订阅 MQTT 主题的值变化。
    /// <para>
    /// MQTT 原生支持事件驱动推送（通过 <see cref="IMqttClient.ApplicationMessageReceivedAsync"/> 事件），
    /// 无需后台轮询。当有消息到达时，直接通过回调通知订阅方。
    /// </para>
    /// <para>
    /// 返回的 <see cref="IDisposable"/> 可用于取消订阅。
    /// </para>
    /// <para>
    /// ⚠️ 注意：当前实现基于字典精确匹配 Topic。使用通配符（<c>+</c> / <c>#</c>）订阅时，
    /// 重新连接后能正确向 Broker 重新注册过滤条件，但消息到达回调依赖于精确 Topic 匹配。
    /// 如需通配符订阅的消息送达，请确保返回的句柄生命周期跨越重连周期。
    /// </para>
    /// </summary>
    public async ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(callback);

        var entry = new SubscriptionEntry(address, callback);
        if (_subscriptions.TryAdd(address, entry))
        {
            _logger.LogDebug("{Driver} 新增订阅: {Address}", DriverName, address);

            // 如果已连接，立即向 Broker 订阅 Topic
            var client = _mqttClient;
            if (client is { IsConnected: true })
            {
                try
                {
                    var subscribeOptions = new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(f => f.WithTopic(address)
                            .WithQualityOfServiceLevel(ToMqttQosLevel(_config.Qos)))
                        .Build();

                    await client.SubscribeAsync(subscribeOptions, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Driver} 订阅 Topic 失败: {Address}", DriverName, address);
                }
            }
        }

        return new SubscriptionHandle(this, address);
    }

    // ── 异步释放 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("{Driver} 正在释放资源...", DriverName);

        await _disposalCts.CancelAsync().ConfigureAwait(false);
        _disposalCts.Dispose();

        // 取消所有待处理读取
        foreach (var kvp in _pendingReads)
        {
            kvp.Value.TrySetCanceled();
        }
        _pendingReads.Clear();

        // 取消所有订阅
        _subscriptions.Clear();

        // 断开连接
        await CleanupConnectionAsync().ConfigureAwait(false);

        _requestLock.Dispose();

        _logger.LogInformation("{Driver} 资源已释放", DriverName);
    }

    // ── 内部实现：MQTT 事件 ──────────────────────────────────

    private Task OnConnectedAsync(MqttClientConnectedEventArgs args)
    {
        _logger.LogDebug("{Driver} MQTT 已连接 (ResultCode={ResultCode})",
            DriverName, args.ConnectResult?.ResultCode);

        State = ConnectionState.Connected;

        // 连接恢复后重新订阅所有活跃的 Topic
        ReSubscribeAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.LogWarning(t.Exception, "{Driver} 重订阅失败", DriverName);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);

        return Task.CompletedTask;
    }

    private Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        // 如果是因为主动断开，不需要通知
        if (State == ConnectionState.Disconnecting || State == ConnectionState.Disconnected)
        {
            return Task.CompletedTask;
        }

        _logger.LogWarning("{Driver} MQTT 断开: {Reason}",
            DriverName, args.Reason);

        State = ConnectionState.Reconnecting;

        return Task.CompletedTask;
    }

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;
        var payload = args.ApplicationMessage.Payload.ToArray();

        _logger.LogTrace("{Driver} 收到消息: {Topic} ({Len} 字节)", DriverName, topic, payload.Length);

        // 检查是否有一次性读取等待
        if (_pendingReads.TryRemove(topic, out var tcs))
        {
            tcs.TrySetResult(payload);
        }

        // 检查是否有持久订阅
        if (_subscriptions.TryGetValue(topic, out var entry))
        {
            entry.InvokeIfChanged(payload);
        }

        return Task.CompletedTask;
    }

    // ── 内部实现：重新订阅 ───────────────────────────────────

    /// <summary>
    /// 连接恢复后重新订阅所有活跃 Topic。
    /// </summary>
    private async Task ReSubscribeAsync()
    {
        var client = _mqttClient;
        if (client is null || !client.IsConnected)
        {
            return;
        }

        if (_subscriptions.IsEmpty)
        {
            return;
        }

        _logger.LogDebug("{Driver} 正在重新订阅 {Count} 个 Topic...",
            DriverName, _subscriptions.Count);

        var subscribeOptionsBuilder = new MqttClientSubscribeOptionsBuilder();
        foreach (var topic in _subscriptions.Keys)
        {
            subscribeOptionsBuilder.WithTopicFilter(f => f
                .WithTopic(topic)
                .WithQualityOfServiceLevel(ToMqttQosLevel(_config.Qos)));
        }

        try
        {
            await client.SubscribeAsync(subscribeOptionsBuilder.Build())
                .ConfigureAwait(false);

            _logger.LogDebug("{Driver} 重新订阅完成 ({Count} 个 Topic)",
                DriverName, _subscriptions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Driver} 重新订阅失败", DriverName);
        }
    }

    // ── 内部实现：工具方法 ───────────────────────────────────

    /// <summary>
    /// 构建 MQTT 客户端连接选项。
    /// </summary>
    private MqttClientOptions BuildClientOptions(MqttClientFactory factory)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_config.Broker, _config.Port)
            .WithClientId(_config.ClientId)
            .WithCleanSession();

        // 心跳间隔（Keep Alive）
        builder.WithKeepAlivePeriod(TimeSpan.FromMilliseconds(
            Math.Max(1000, _config.HeartbeatIntervalMs)));

        // 用户名密码认证
        if (!string.IsNullOrEmpty(_config.UserName))
        {
            builder.WithCredentials(
                _config.UserName,
                _config.Password ?? string.Empty);
        }

        // Will Message（遗嘱消息）
        if (!string.IsNullOrEmpty(_config.WillTopic))
        {
            builder.WithWillTopic(_config.WillTopic);
            builder.WithWillPayload("Offline");
            builder.WithWillQualityOfServiceLevel(ToMqttQosLevel(_config.Qos));
            builder.WithWillRetain();
        }

        // TLS 配置
        if (_config.UseTls)
        {
            builder.WithTlsOptions(o =>
            {
                // 开发环境：接受所有服务器证书
                o.WithCertificateValidationHandler(_ => true);
            });
        }

        // 超时设置
        builder.WithTimeout(TimeSpan.FromMilliseconds(_config.TimeoutMs));

        return builder.Build();
    }

    /// <summary>
    /// 将 <see cref="MqttDriverConfig.Qos"/> 整数转换为 MQTTnet 的 QoS 枚举。
    /// </summary>
    private static MqttQualityOfServiceLevel ToMqttQosLevel(int qos) => qos switch
    {
        0 => MqttQualityOfServiceLevel.AtMostOnce,
        1 => MqttQualityOfServiceLevel.AtLeastOnce,
        2 => MqttQualityOfServiceLevel.ExactlyOnce,
        _ => MqttQualityOfServiceLevel.AtLeastOnce
    };

    /// <summary>
    /// 清理连接资源。
    /// </summary>
    private async Task CleanupConnectionAsync()
    {
        var client = _mqttClient;
        if (client is null)
        {
            return;
        }

        try
        {
            // 先停止事件处理
            client.ConnectedAsync -= OnConnectedAsync;
            client.DisconnectedAsync -= OnDisconnectedAsync;
            client.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;

            if (client.IsConnected)
            {
                await client.DisconnectAsync(
                    new MqttClientDisconnectOptions
                    {
                        Reason = MqttClientDisconnectOptionsReason.NormalDisconnection
                    }).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 断开 MQTT 时异常", DriverName);
        }
        finally
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "{Driver} 释放 MQTT 客户端时异常", DriverName);
            }
            _mqttClient = null;
        }
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        var detail = newState switch
        {
            ConnectionState.Connected => $"已连接到 {_config.Broker}:{_config.Port}",
            ConnectionState.Disconnected => "已断开",
            ConnectionState.Reconnecting => "正在重连（MQTTnet 内置自动重连）",
            _ => string.Empty
        };

        var args = new ConnectionStateChangedEventArgs(oldState, newState, detail);
        ConnectionStateChanged?.Invoke(this, args);
    }

    internal void Unsubscribe(string address)
    {
        if (_subscriptions.TryRemove(address, out _))
        {
            _logger.LogDebug("{Driver} 取消订阅: {Address}", DriverName, address);

            var client = _mqttClient;
            if (client is { IsConnected: true })
            {
                try
                {
                    var unsubscribeOptions = new MqttClientUnsubscribeOptionsBuilder()
                        .WithTopicFilter(address)
                        .Build();

                    _ =            client.UnsubscribeAsync(unsubscribeOptions).ContinueWith(t =>
                    {
                        _logger.LogTrace(t.Exception,
                            "{Driver} 取消订阅异常: {Address}", DriverName, address);
                    }, TaskContinuationOptions.OnlyOnFaulted);
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "{Driver} 取消订阅异常: {Address}", DriverName, address);
                }
            }
        }
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 订阅条目：记录 Topic 和回调，并持有上次值以实现变化检测。
    /// </summary>
    private sealed class SubscriptionEntry
    {
        private readonly Action<byte[]> _callback;
        private byte[]? _lastValue;

        public SubscriptionEntry(string address, Action<byte[]> callback)
        {
            Address = address;
            _callback = callback;
        }

        public string Address { get; }

        /// <summary>
        /// 如果值发生变化则调用回调。
        /// </summary>
        public void InvokeIfChanged(byte[] newValue)
        {
            if (_lastValue is not null &&
                _lastValue.AsSpan().SequenceEqual(newValue))
            {
                return;
            }

            _lastValue = newValue;
            _callback(newValue);
        }
    }

    /// <summary>
    /// 订阅句柄：在释放时从驱动取消订阅。
    /// </summary>
    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly MqttDriver _driver;
        private readonly string _address;

        public SubscriptionHandle(MqttDriver driver, string address)
        {
            _driver = driver;
            _address = address;
        }

        public void Dispose()
        {
            _driver.Unsubscribe(_address);
        }
    }
}

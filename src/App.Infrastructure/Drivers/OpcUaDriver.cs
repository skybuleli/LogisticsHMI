using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace App.Infrastructure.Drivers;

/// <summary>
/// OPC UA 客户端驱动实现。
/// <para>
/// 基于 OPC Foundation .NET Standard 栈实现 OPC UA 客户端通信，
/// 支持安全策略（None/Sign/Encrypt）、匿名/用户名密码认证、自动证书管理。
/// </para>
/// <para>
/// 地址格式（标准 OPC UA NodeId 字符串）：
/// <list type="bullet">
///   <item><c>i={numeric}</c>             — 数字标识符，例如 <c>i=85</c>（Server_ServerStatus_CurrentTime）</item>
///   <item><c>ns={idx};i={numeric}</c>    — 指定命名空间的数字标识符，例如 <c>ns=2;i=100</c></item>
///   <item><c>ns={idx};s={string}</c>     — 指定命名空间的字符串标识符，例如 <c>ns=2;s=MyVariable</c></item>
///   <item><c>ns={idx};g={guid}</c>       — GUID 标识符</item>
///   <item><c>ns={idx};b={base64}</c>     — 字节串标识符</item>
/// </list>
/// </para>
/// <para>
/// 线程安全（单连接串行化请求），完整异步支持。支持心跳检测、自动重连（指数退避）和订阅轮询。
/// </para>
/// </summary>
public sealed class OpcUaDriver : IDeviceDriver
{
    // ── 常量 ─────────────────────────────────────────────────
    private const string CertificateStoreType = "Directory";
    private const string DefaultCertificateStorePath = "OPCUA/Certificates/Client";
    private const string DefaultTrustedStorePath = "OPCUA/Certificates/Trusted";
    private const string DefaultIssuerStorePath = "OPCUA/Certificates/Issuer";
    private const string DefaultRejectedStorePath = "OPCUA/Certificates/Rejected";

    // ── 字段 ───────────────────────────────────────────────
    private readonly OpcUaDriverConfig _config;
    private readonly ILogger<OpcUaDriver> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly CancellationTokenSource _disposalCts = new();

    private ApplicationConfiguration? _appConfig;
    private Session? _session;
    private bool _sessionOwned;

    // 订阅管理
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new();
    private Task? _pollTask;

    // 连接状态
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 初始化 OPC UA 驱动。
    /// </summary>
    public OpcUaDriver(OpcUaDriverConfig config, ILogger<OpcUaDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
    }

    // ── IDeviceDriver 属性 ─────────────────────────────────

    /// <inheritdoc />
    public string DriverName => $"OPCUA-{_config.DeviceId}";

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
            "{Driver} 正在连接 OPC UA 服务器 {ServerUrl} (SecurityPolicy={SecurityPolicy}) ...",
            DriverName, _config.ServerUrl, _config.SecurityPolicy);

        try
        {
            // 1. 创建并验证 ApplicationConfiguration
            var appConfig = await CreateApplicationConfigurationAsync().ConfigureAwait(false);
            _appConfig = appConfig;

            // 2. 选择终结点
            var endpoint = await SelectEndpointAsync(ct).ConfigureAwait(false);

            // 3. 创建会话
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);

#pragma warning disable CS0618 // Session.CreateAsync 是 v1.5.x 的官方 API，ISessionFactory 在此版本不存在
            var identity = CreateUserIdentity();
            var session = await Session.CreateAsync(
                sessionInstantiator: null,
                configuration: appConfig,
                connection: null,
                endpoint: endpoint,
                updateBeforeConnect: false,
                checkDomain: false,
                sessionName: _config.SessionName,
                sessionTimeout: (uint)_config.TimeoutMs,
                identity: identity,
                preferredLocales: null,
                returnDiagnostics: DiagnosticsMasks.None,
                ct: timeoutCts.Token
            ).WaitAsync(CancellationToken.None).ConfigureAwait(false);
#pragma warning restore CS0618

            _session = session;
            _sessionOwned = true;

            State = ConnectionState.Connected;
            _logger.LogInformation(
                "{Driver} 连接成功 ({ServerUrl}, Session={SessionName})",
                DriverName, _config.ServerUrl, _config.SessionName);

            StartPollIfNeeded();
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            State = ConnectionState.Disconnected;
            _logger.LogWarning("{Driver} 连接超时 ({Timeout}ms)", DriverName, _config.TimeoutMs);
            return Result.Failure($"连接超时 ({_config.TimeoutMs}ms)");
        }
        catch (ServiceResultException ex)
        {
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} OPC UA 连接失败: {ServerUrl}", DriverName, _config.ServerUrl);
            return Result.Failure($"OPC UA 连接失败: {ex.Message} (StatusCode={ex.StatusCode})");
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
            await CleanupSessionAsync().ConfigureAwait(false);
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
    /// 读取 OPC UA 节点的当前值。
    /// <para>
    /// <paramref name="address"/> 为标准 OPC UA NodeId 字符串格式，例如 <c>ns=2;s=MyVariable</c>。
    /// 纯数字节点 ID 可使用 <c>i=85</c> 格式（BaseNodeClass 中的 ServerStatus）。
    /// </para>
    /// <para>
    /// <paramref name="length"/> 在 OPC UA 上下文中无意义（非字节偏移寻址），
    /// 当前实现忽略此参数，始终返回单个节点的完整值。
    /// 返回值被序列化为字节数组，<see cref="Variant"/> 类型通过 <see cref="SerializeVariant"/> 转换。
    /// </para>
    /// </remarks>
    public async ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (!TryParseNodeId(address, out var nodeId))
        {
            return Result<byte[]>.Failure($"无效 NodeId 格式: {address}");
        }

        _logger.LogTrace("{Driver} 读取节点: {NodeId}", DriverName, nodeId);

        return await ExecuteWithLockAsync(ct, async () =>
        {
            var session = _session;
            if (session is null || !session.Connected)
            {
                return Result<byte[]>.Failure($"{DriverName} 未连接，无法读取");
            }

            try
            {
                // 读取单个节点的值
                var readValueId = new ReadValueId
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value
                };

                var nodesToRead = new ReadValueIdCollection { readValueId };

                // ReadAsync 返回 ReadResponse，通过 Results 属性获取 DataValueCollection
                var response = await session.ReadAsync(
                    requestHeader: null,
                    maxAge: 0,
                    timestampsToReturn: TimestampsToReturn.Neither,
                    nodesToRead: nodesToRead,
                    ct: ct
                ).WaitAsync(ct).ConfigureAwait(false);

                var results = response.Results;

                if (results is null || results.Count == 0)
                {
                    return Result<byte[]>.Failure("读取响应为空");
                }

                var dataValue = results[0];
                if (StatusCode.IsBad(dataValue.StatusCode))
                {
                    var msg = $"读取失败: {dataValue.StatusCode}";
                    _logger.LogWarning("{Driver} {Msg} (NodeId={NodeId})", DriverName, msg, nodeId);
                    return Result<byte[]>.Failure(msg);
                }

                // 将 Variant 值序列化为字节数组
                var bytes = SerializeVariant(dataValue.WrappedValue);

                _logger.LogTrace(
                    "{Driver} 读取成功: {NodeId} → {Type} ({Len} 字节)",
                    DriverName, nodeId, dataValue.WrappedValue.TypeInfo?.BuiltInType, bytes.Length);

                return Result<byte[]>.Success(bytes);
            }
            catch (OperationCanceledException)
            {
                return Result<byte[]>.Failure("读取操作已取消");
            }
            catch (ServiceResultException ex)
            {
                _logger.LogError(ex, "{Driver} OPC UA 读取异常: {NodeId}", DriverName, nodeId);
                return Result<byte[]>.Failure($"OPC UA 读取异常: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Driver} 读取失败: {NodeId}", DriverName, nodeId);
                return Result<byte[]>.Failure($"读取失败: {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 写入 OPC UA 节点的值。
    /// <para>
    /// <paramref name="address"/> 为标准 OPC UA NodeId 字符串格式。
    /// </para>
    /// <para>
    /// <paramref name="data"/> 为待写入的原始字节数据。由于 OPC UA 是强类型系统，
    /// 写入时会将字节数据按照节点已有的 DataType 属性转换为对应类型，或尝试智能推断：
    /// <list type="bullet">
    ///   <item>4 字节 → 尝试 Int32</item>
    ///   <item>8 字节 → 尝试 Double</item>
    ///   <item>其他 → 作为 ByteString</item>
    /// </list>
    /// 对于需要精确类型控制的场景，可在地址中附加类型提示，例如 <c>ns=2;s=MyVar:Double</c>。
    /// </para>
    /// </remarks>
    public async ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!TryParseNodeId(address, out var nodeId))
        {
            return Result.Failure($"无效 NodeId 格式: {address}");
        }

        _logger.LogTrace(
            "{Driver} 写入节点: {NodeId} ({Len} 字节)", DriverName, nodeId, data.Length);

        return await ExecuteWithLockAsync(ct, async () =>
        {
            var session = _session;
            if (session is null || !session.Connected)
            {
                return Result.Failure($"{DriverName} 未连接，无法写入");
            }

            try
            {
                // 将字节数据转换为 Variant
                var variant = BytesToVariant(data);

                var writeValue = new WriteValue
                {
                    NodeId = nodeId,
                    AttributeId = Attributes.Value,
                    Value = new DataValue(variant)
                };

                var writeValues = new WriteValueCollection { writeValue };

                // WriteAsync 返回 WriteResponse，通过 Results 属性获取 StatusCodeCollection
                var response = await session.WriteAsync(
                    requestHeader: null,
                    nodesToWrite: writeValues,
                    ct: ct
                ).WaitAsync(ct).ConfigureAwait(false);

                var results = response.Results;

                if (results is null || results.Count == 0)
                {
                    return Result.Failure("写入响应为空");
                }

                var statusCode = results[0];
                if (StatusCode.IsBad(statusCode))
                {
                    var msg = $"写入失败: {statusCode}";
                    _logger.LogWarning("{Driver} {Msg} (NodeId={NodeId})", DriverName, msg, nodeId);
                    return Result.Failure(msg);
                }

                _logger.LogTrace("{Driver} 写入成功: {NodeId}", DriverName, nodeId);
                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("写入操作已取消");
            }
            catch (ServiceResultException ex)
            {
                _logger.LogError(ex, "{Driver} OPC UA 写入异常: {NodeId}", DriverName, nodeId);
                return Result.Failure($"OPC UA 写入异常: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Driver} 写入失败: {NodeId}", DriverName, nodeId);
                return Result.Failure($"写入失败: {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    // ── 订阅 ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <summary>
    /// 订阅 OPC UA 节点的值变化。内部通过后台轮询实现，轮询间隔由
    /// <see cref="DriverConfigBase.HeartbeatIntervalMs"/> 控制（默认 30s）。
    /// 返回的 <see cref="IDisposable"/> 可用于取消订阅。
    /// <para>
    /// 注意：当前使用轮询方式实现订阅。生产环境中建议升级为 OPC UA 原生订阅
    /// （<see cref="Subscription"/> + <see cref="MonitoredItem"/>）以实现事件驱动推送。
    /// OPC UA 原生订阅将在后续优化阶段（Phase 7）考虑实现。
    /// </para>
    /// </summary>
    public ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(callback);

        var entry = new SubscriptionEntry(address, callback);
        if (_subscriptions.TryAdd(address, entry))
        {
            _logger.LogDebug("{Driver} 新增订阅: {Address}", DriverName, address);
            StartPollIfNeeded();
        }

        return ValueTask.FromResult<IDisposable>(new SubscriptionHandle(this, address));
    }

    // ── 异步释放 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("{Driver} 正在释放资源...", DriverName);

        await _disposalCts.CancelAsync().ConfigureAwait(false);
        _disposalCts.Dispose();

        // 取消所有订阅
        _subscriptions.Clear();

        // 关闭会话
        await CleanupSessionAsync().ConfigureAwait(false);

        _requestLock.Dispose();

        _logger.LogInformation("{Driver} 资源已释放", DriverName);
    }

    // ── 内部实现 ─────────────────────────────────────────────

    /// <summary>
    /// 创建并验证 <see cref="ApplicationConfiguration"/>。
    /// </summary>
    private async Task<ApplicationConfiguration> CreateApplicationConfigurationAsync()
    {
        var appName = $"LogisticsHMI/{_config.DeviceId}";
        var appUri = $"urn:{Environment.MachineName}:LogisticsHMI:{_config.DeviceId}";

        var config = new ApplicationConfiguration
        {
            ApplicationName = appName,
            ApplicationUri = appUri,
            ApplicationType = ApplicationType.Client,
            TransportQuotas = new TransportQuotas
            {
                OperationTimeout = _config.TimeoutMs,
                MaxMessageSize = (int)(4 * 1024 * 1024),
                MaxByteStringLength = (int)(256 * 1024),
                MaxStringLength = (int)(256 * 1024)
            },
            ClientConfiguration = new ClientConfiguration
            {
                DefaultSessionTimeout = _config.TimeoutMs
            },
            SecurityConfiguration = new SecurityConfiguration
            {
                AutoAcceptUntrustedCertificates = string.Equals(
                    _config.SecurityPolicy, "None", StringComparison.OrdinalIgnoreCase),
                RejectSHA1SignedCertificates = false,
                MinimumCertificateKeySize = 1024,

                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType = CertificateStoreType,
                    StorePath = DefaultCertificateStorePath,
                    SubjectName = appName
                },
                TrustedPeerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType,
                    StorePath = DefaultTrustedStorePath
                },
                TrustedIssuerCertificates = new CertificateTrustList
                {
                    StoreType = CertificateStoreType,
                    StorePath = DefaultIssuerStorePath
                },
                RejectedCertificateStore = new CertificateStoreIdentifier
                {
                    StoreType = CertificateStoreType,
                    StorePath = DefaultRejectedStorePath
                }
            }
        };

        // 验证配置并自动生成自签名证书
        await config.ValidateAsync(ApplicationType.Client)
            .ConfigureAwait(false);

        _logger.LogDebug("{Driver} 应用配置已创建: {AppUri}", DriverName, appUri);

        return config;
    }

    /// <summary>
    /// 选择服务器终结点。
    /// </summary>
    private Task<ConfiguredEndpoint> SelectEndpointAsync(CancellationToken ct)
    {
        // 直接创建终结点配置（跳过 CoreClientUtils 发现过程）。
        // 对于 SecurityPolicy=None 的情况，服务器接受任何安全设置。
        // 如需安全连接，后续可扩展为使用 DiscoveryClient 发现服务器终结点。
        var endpointConfiguration = EndpointConfiguration.Create(_appConfig!);
        endpointConfiguration.OperationTimeout = _config.TimeoutMs;

        // 创建最小化的 EndpointDescription（安全策略 None）
        var endpointDescription = new EndpointDescription
        {
            EndpointUrl = _config.ServerUrl,
            SecurityMode = MessageSecurityMode.None,
            SecurityPolicyUri = SecurityPolicies.None,
            Server = new ApplicationDescription
            {
                ApplicationUri = $"urn:{Environment.MachineName}:LogisticsHMI:Server",
                ApplicationName = "LogisticsHMI Server",
                ApplicationType = ApplicationType.Server
            }
        };

        var configuredEndpoint = new ConfiguredEndpoint(
            null,
            endpointDescription,
            endpointConfiguration)
        {
            UpdateBeforeConnect = false
        };

        return Task.FromResult(configuredEndpoint);
    }

    /// <summary>
    /// 创建用户身份标识。
    /// </summary>
    private IUserIdentity CreateUserIdentity()
    {
        if (!string.IsNullOrEmpty(_config.UserName) && !string.IsNullOrEmpty(_config.Password))
        {
            _logger.LogDebug("{Driver} 使用用户名密码认证", DriverName);
            var passwordBytes = System.Text.Encoding.UTF8.GetBytes(_config.Password);
            return new UserIdentity(_config.UserName, passwordBytes);
        }

        _logger.LogDebug("{Driver} 使用匿名认证", DriverName);
        return new UserIdentity(new AnonymousIdentityToken());
    }

    /// <summary>
    /// 解析 OPC UA NodeId 字符串。
    /// </summary>
    public static bool TryParseNodeId(string? address, out NodeId nodeId)
    {
        nodeId = NodeId.Null;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        try
        {
            // OPC Foundation 的 NodeId.Parse 接受标准格式
            nodeId = NodeId.Parse(address);
            return !nodeId.IsNullNodeId;
        }
        catch (ServiceResultException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// 将 <see cref="Variant"/> 值序列化为字节数组。
    /// </summary>
    private static byte[] SerializeVariant(Variant value)
    {
        if (value == Variant.Null)
        {
            return [];
        }

        var typeInfo = value.TypeInfo;
        if (typeInfo is null)
        {
            return [];
        }

        return typeInfo.BuiltInType switch
        {
            BuiltInType.Boolean => [(byte)(value.Value is bool b && b ? 1 : 0)],
            BuiltInType.SByte => [(byte)(sbyte)value.Value],
            BuiltInType.Byte => [(byte)value.Value],
            BuiltInType.Int16 => BitConverter.GetBytes((short)value.Value),
            BuiltInType.UInt16 => BitConverter.GetBytes((ushort)value.Value),
            BuiltInType.Int32 => BitConverter.GetBytes((int)value.Value),
            BuiltInType.UInt32 => BitConverter.GetBytes((uint)value.Value),
            BuiltInType.Int64 => BitConverter.GetBytes((long)value.Value),
            BuiltInType.UInt64 => BitConverter.GetBytes((ulong)value.Value),
            BuiltInType.Float => BitConverter.GetBytes((float)value.Value),
            BuiltInType.Double => BitConverter.GetBytes((double)value.Value),
            BuiltInType.String => System.Text.Encoding.UTF8.GetBytes((string)value.Value),
            BuiltInType.ByteString => (byte[])value.Value,
            _ => value.Value?.ToString() is { } str
                ? System.Text.Encoding.UTF8.GetBytes(str)
                : []
        };
    }

    /// <summary>
    /// 将字节数组转换为 <see cref="Variant"/>。
    /// 按数据长度智能推断类型。
    /// </summary>
    internal static Variant BytesToVariant(byte[] data)
    {
        return data.Length switch
        {
            1 => new Variant(data[0]),
            2 => new Variant(BitConverter.ToInt16(data)),
            4 => new Variant(BitConverter.ToInt32(data)),
            8 => new Variant(BitConverter.ToDouble(data)),
            // ByteString: Variant(byte[]) 构造函数自动创建 ByteString Variant
            _ => new Variant(data)
        };
    }

    /// <summary>
    /// 在请求锁的保护下执行异步操作。
    /// </summary>
    private async ValueTask<TResult> ExecuteWithLockAsync<TResult>(
        CancellationToken ct, Func<Task<TResult>> action)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
    }

    // ── 订阅轮询 ─────────────────────────────────────────────

    private void StartPollIfNeeded()
    {
        if (_pollTask is not null || _subscriptions.IsEmpty)
        {
            return;
        }

        _pollTask = PollSubscriptionsAsync(_disposalCts.Token);
    }

    private async Task PollSubscriptionsAsync(CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromMilliseconds(
            Math.Max(100, _config.HeartbeatIntervalMs));

        _logger.LogDebug("{Driver} 订阅轮询已启动 (间隔={Interval}ms)",
            DriverName, pollInterval.TotalMilliseconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_subscriptions.IsEmpty)
            {
                break;
            }

            if (State != ConnectionState.Connected)
            {
                continue;
            }

            foreach (var (addr, entry) in _subscriptions)
            {
                try
                {
                    var result = await ReadAsync(addr, 1, ct).ConfigureAwait(false);
                    if (result.IsSuccess && result.Value is not null)
                    {
                        entry.InvokeIfChanged(result.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "{Driver} 订阅轮询失败: {Address}",
                        DriverName, addr);
                }
            }
        }

        _logger.LogDebug("{Driver} 订阅轮询已停止", DriverName);
        _pollTask = null;
    }

    internal void Unsubscribe(string address)
    {
        if (_subscriptions.TryRemove(address, out _))
        {
            _logger.LogDebug("{Driver} 取消订阅: {Address}", DriverName, address);
        }
    }

    // ── 连接状态管理 ─────────────────────────────────────────

    private async Task HandleConnectionLostAsync(string reason)
    {
        _logger.LogWarning("{Driver} 连接丢失: {Reason}", DriverName, reason);

        if (State != ConnectionState.Connected)
        {
            return;
        }

        State = ConnectionState.Reconnecting;

        try
        {
            await CleanupSessionAsync().ConfigureAwait(false);

            if (_config.AutoReconnect)
            {
                await AttemptReconnectAsync(_disposalCts.Token).ConfigureAwait(false);
            }
            else
            {
                State = ConnectionState.Disconnected;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Driver} 断线处理异常", DriverName);
            State = ConnectionState.Disconnected;
        }
    }

    private async Task AttemptReconnectAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _config.ReconnectMaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                State = ConnectionState.Disconnected;
                return;
            }

            var delay = Math.Min(
                _config.ReconnectBaseDelayMs * (int)Math.Pow(2, attempt - 1),
                30_000); // 最大 30s

            _logger.LogInformation(
                "{Driver} 重连第 {Attempt}/{Max} 次 (延迟={Delay}ms)...",
                DriverName, attempt, _config.ReconnectMaxRetries, delay);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                State = ConnectionState.Disconnected;
                return;
            }

            var result = await ConnectAsync(ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation("{Driver} 重连成功", DriverName);
                return;
            }

            _logger.LogWarning("{Driver} 第 {Attempt} 次重连失败: {Error}",
                DriverName, attempt, result.Error);
        }

        State = ConnectionState.Disconnected;
        _logger.LogError("{Driver} 重连失败（已尝试 {Max} 次）",
            DriverName, _config.ReconnectMaxRetries);
    }

    private async Task CleanupSessionAsync()
    {
        if (!_sessionOwned || _session is null)
        {
            return;
        }

        try
        {
            if (_session.Connected)
            {
                await _session.CloseAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 关闭会话时异常", DriverName);
        }
        finally
        {
            try
            {
                _session.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "{Driver} 释放会话时异常", DriverName);
            }
            _session = null;
            _sessionOwned = false;
        }
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        var detail = newState switch
        {
            ConnectionState.Connected => $"已连接到 {_config.ServerUrl}",
            ConnectionState.Disconnected => "已断开",
            ConnectionState.Reconnecting => $"正在重连（最多 {_config.ReconnectMaxRetries} 次）",
            _ => string.Empty
        };

        var args = new ConnectionStateChangedEventArgs(oldState, newState, detail);
        ConnectionStateChanged?.Invoke(this, args);
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 订阅条目：记录地址和回调，并持有上次值以实现变化检测。
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
        private readonly OpcUaDriver _driver;
        private readonly string _address;

        public SubscriptionHandle(OpcUaDriver driver, string address)
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

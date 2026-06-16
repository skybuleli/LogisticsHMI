# Logistics HMI — 物流上位机系统

> **WCS (Warehouse Control System)** — 物流仓储行业设备调度与监控上位机。
>
> 接收 WMS 下发的任务指令，调度底层自动化设备（堆垛机、AGV、输送线、分拣机），提供实时监控、报警管理和数据分析。

---

## 架构总览

```
WMS / ERP
    │  REST API / MQ
    ▼
┌─────────────────────────────────────────────────┐
│              Logistics HMI (本系统)               │
│                                                   │
│  ┌──────────┐  ┌──────────┐  ┌────────────────┐ │
│  │ 通信层    │  │ 数据平台 │  │ 核心业务       │ │
│  │ Modbus   │  │ Postgres │  │ 任务调度       │ │
│  │ S7       │  │ Redis    │  │ 设备状态机     │ │
│  │ OPC UA   │  │ TDengine │  │ 报警管理       │ │
│  │ MQTT     │  │ SQLite   │  │ 权限审计       │ │
│  └──────────┘  └──────────┘  └────────────────┘ │
│                                                   │
│  ┌────────────────────────────────────────────┐  │
│  │  UI (Avalonia Desktop)                     │  │
│  │  仪表盘 · 实时监控 · 配置 · 诊断            │  │
│  └────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
    │
    ▼
PLC / 自动化设备 (堆垛机 · AGV · 输送线 · 传感器)
```

**四层架构：**

| 层 | 项目 | 职责 |
|---|------|------|
| 领域层 | `App.Core` | 实体、值对象、接口定义、枚举、领域事件 |
| 基础设施 | `App.Infrastructure` | 通信驱动、数据持久化、缓存、消息队列 |
| 表示层 | `App.UI` | Avalonia 桌面 UI、MVVM 视图模型 |
| 启动层 | `App.Host` | DI 容器组合根、配置加载、进程生命周期 |

---

## 技术栈

| 分类 | 技术 | 版本 |
|------|------|------|
| 运行时 | .NET | 9.0 (开发) / NativeAOT (发布) |
| 桌面 UI | Avalonia | 12.0.4 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| DI | Microsoft.Extensions.DependencyInjection | 10.0 |
| 日志 | Serilog (文件 + 控制台) | 4.3.1 |
| 2D 图形 | SkiaSharp | 4.x |
| 图表 | LiveChartsCore | 2.x |

### 工业通信协议

| 协议 | 库 | 状态 |
|------|----|------|
| Modbus TCP | 裸 TCP 实现 (功能码 01-10) | ✅ 完成 |
| Modbus RTU | System.IO.Ports + 自研 CRC16 | ✅ 完成 |
| Siemens S7 | S7netplus | ✅ 完成 |
| OPC UA | OPCFoundation.NetStandard | ✅ 完成 |
| MQTT 5.0 | MQTTnet | ✅ 完成 |

### 数据存储 (Phase 3+)

| 数据库 | 用途 |
|--------|------|
| PostgreSQL | 业务数据主库 (任务/配置/审计) |
| Redis | 实时状态缓存 + 分布式锁 |
| TDengine | 时序数据 (设备运行指标) |
| SQLite | 离线缓冲 + 断点续传 |

---

## 项目结构

```
LogisticsHMI/
├── src/
│   ├── App.Core/               # 领域模型与接口
│   │   ├── Models/             #   DriverConfig, Result, CRC16, 消息模型
│   │   ├── Enums/              #   ConnectionState, DeviceHealth, TaskStatus
│   │   ├── Interfaces/         #   IDeviceDriver, IDeviceConnectionPool, IHeartbeatService
│   │   └── Events/             #   ConnectionStateChangedEventArgs
│   ├── App.Infrastructure/     # 基础设施实现
│   │   ├── Drivers/            #   ModbusTcp, ModbusRtu, S7, OpcUa, Mqtt
│   │   ├── Logging/            #   Serilog 三路 Sink 配置
│   │   ├── ConnectionPoolManager.cs
│   │   ├── HeartbeatService.cs
│   │   └── ConfigurationService.cs
│   ├── App.UI/                 # Avalonia 桌面 UI
│   │   ├── Views/              #   ShellView, HomeView, MonitorView, SettingsView
│   │   ├── ViewModels/         #   对应 ViewModel + StatusBarViewModel
│   │   ├── Controls/           #   NotificationBadge, PageContainer, StatusBar
│   │   ├── Services/           #   NavigationService, DialogService, ThemeService
│   │   ├── Styles/             #   工业蓝 / 暗黑 / 高对比度 三套主题
│   │   └── Converters/         #   GreaterThanZeroConverter, StringNotEmptyConverter
│   └── App.Host/               # 启动入口
│       ├── Program.cs          #   SingleInstanceGuard → DI → Avalonia
│       ├── ServiceCollectionExtensions.cs
│       └── appsettings.json
├── tests/
│   ├── App.Core.Tests/         # 领域层单元测试 (18 项)
│   └── App.Infrastructure.Tests/ # 基础设施测试 (199 项)
├── docs/
│   ├── 实施计划.md              # 64 项任务拆分
│   ├── PROGRESS.md             # 任务进度追踪
│   └── AGENTS.md               # AI 代理上下文指南
└── README.md                   # ← 你在这里
```

---

## 快速开始

### 前置条件

- .NET 9.0 SDK
- 推荐 IDE：JetBrains Rider / Visual Studio 2022+

### 构建与运行

```bash
git clone https://github.com/skybuleli/LogisticsHMI.git
cd LogisticsHMI

# 还原依赖
dotnet restore

# 开发模式运行 (JIT)
dotnet run --project src/App.Host

# NativeAOT 发布
dotnet publish src/App.Host -c Release -r win-x64 -p:PublishAot=true
```

### 运行测试

```bash
# 全量测试
dotnet test

# 按协议过滤
dotnet test --filter "FullyQualifiedName~ModbusProtocol"
dotnet test --filter "FullyQualifiedName~HeartbeatService"
dotnet test --filter "FullyQualifiedName~S7Driver"
```

---

## 配置

配置文件 `src/App.Host/appsettings.json`：

```json
{
  "ConnectionPool": {
    "MaxConcurrentConnections": 10,
    "HealthCheckIntervalMs": 30000
  },
  "Heartbeat": {
    "IntervalMs": 10000,
    "MissedThreshold": 3
  },
  "Devices": [
    {
      "DeviceId": "modbus-plc-1",
      "DriverType": "ModbusTCP",
      "Host": "192.168.1.100",
      "Port": 502,
      "SlaveAddress": 1
    }
  ]
}
```

支持的 `DriverType`：`ModbusTCP`、`ModbusRTU`、`S7`、`OPCUA`、`MQTT`。

---

## 开发进度

| Phase | 内容 | 完成率 |
|-------|------|--------|
| P0 | 项目规划 | 100% |
| P1 | 基础框架 (MVVM/DI/导航/主题/日志/配置) | 100% |
| **P2** | **通信层 (驱动/连接池/心跳)** | **72.7%** |
| P3 | 数据平台 | 0% |
| P4 | 核心业务 (任务调度/报警/报表) | 0% |
| P5 | 系统集成 (API/WebSocket/WMS 对接) | 0% |
| P6 | 增强功能 (3D 孪生/ONNX) | 按需 |
| P7 | 测试发布 (贯穿全程) | 进行中 |

> 详细进度 → [`docs/PROGRESS.md`](docs/PROGRESS.md)
> 实施计划 → [`docs/实施计划.md`](docs/实施计划.md)

---

## 架构要点

### 驱动设计

所有通信驱动实现统一的 `IDeviceDriver` 接口：

```csharp
ValueTask<Result> ConnectAsync(CancellationToken ct);
ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct);
ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct);
ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct);
```

通过 `IDeviceDriverFactory` 按 `DriverType` 字符串路由到具体驱动，由 `IDeviceConnectionPool` 统一管理生命周期。

### 心跳服务

`IHeartbeatService` 提供三级事件：

- **HeartbeatTick** — 每周期全局通知（含所有设备状态快照）
- **HeartbeatMissed** — 设备连续 N 次心跳丢失时触发（阈值可配）
- **HeartbeatRecovered** — 丢失设备重新在线时触发

所有事件按设备粒度隔离，互不影响。

### 错误处理

函数式 `Result` / `Result<T>` 贯穿所有驱动操作，避免异常流。上层通过 `ViewModelBase.ExecuteBusyAsync` 统一处理异常边界。

---

## 关键架构决策

| ADR | 决策 | 理由 |
|-----|------|------|
| 001 | 四层单体架构 | 上位机垂直集成度高，微服务得不偿失 |
| 002 | Wolverine 而非 MediatR | MIT 许可永久免费，内置消息总线+重试 |
| 003 | SkiaSharp 2D 绘制 | v4 新增可变字体/Metal/Vulkan GPU 后端 |
| 004 | TDengine 时序存储 | 写入性能和压缩率 10-20x领先 |
| 006 | NativeAOT 发布 | 冷启动 <2s，内存降 30-50% |

---

## License

MIT

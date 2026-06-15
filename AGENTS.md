# AGENTS.md — 物流上位机系统 (Logistics HMI)

> 本文件为 AI 编码代理（Claude、Cursor、Copilot 等）提供项目上下文。每次开始新对话或新任务时，AI 应首先阅读本文件。

---

## 1. 项目概述

**项目名称：** 物流上位机系统 (Logistics WCS/HMI)

**定位：** 面向物流仓储行业的工业上位机（WCS 控制层），负责接收 WMS 下发的任务指令，调度和管理底层自动化设备（堆垛机、AGV、输送线、分拣机等），提供实时监控、报警管理和数据分析。

**目标平台：** Windows（主）/ Linux（辅）/ macOS（开发环境）

**架构：** 三层架构 — 管理层 (WMS/ERP) → **控制层 (本系统)** → 执行层 (PLC/设备)

---

## 2. 技术栈

### 运行时与语言
| 组件 | 版本 | 说明 |
|------|------|------|
| .NET SDK | 9.0 (开发) / 10.0 LTS (目标发布) | 预留 .NET 10 升级路径 |
| C# | 13 | 使用最新语言特性 |
| 编译模式 | NativeAOT | 生产发布使用 NativeAOT 编译为本机代码 |

### UI 框架
| 组件 | 版本 | 说明 |
|------|------|------|
| Avalonia UI | 12.0.4+ | 跨平台桌面 UI（Windows/Linux/macOS）|
| CommunityToolkit.Mvvm | 8.x | 微软官方 MVVM 源码生成器 |
| SkiaSharp | 4.x | 2D 图形引擎（设备绘制、自定义控件）|
| LiveChartsCore | 2.x | 实时数据图表 |
| **Impeller** | 实验性 | Google Flutter 合作，尚未生产可用 |

### 工业通信
| 协议 | .NET 库 | 优先级 |
|------|---------|--------|
| Modbus TCP/RTU | FluentModbus / NModbus4 | 🔴 必备 |
| Siemens S7 | S7netplus | 🔴 必备 |
| OPC UA | OPCFoundation.NetStandard | 🔴 必备 |
| MQTT 5.0 | MQTTnet | 🟠 强烈推荐 |
| EtherNet/IP | libplctag.NET | 🟡 按需 |

### 数据存储
| 数据库 | 用途 | 阶段 |
|--------|------|------|
| PostgreSQL | 业务数据主库（用户/任务/配置/审计） | Phase 3 |
| Redis | 实时状态缓存 + 分布式锁 | Phase 3 |
| TDengine | 时序数据（设备运行数据） | Phase 3 |
| SQLite | 离线缓冲 + 断点续传 | Phase 3 |

### 消息与事件
| 组件 | 用途 | 许可 |
|------|------|------|
| Wolverine | 进程内 Mediator + 消息总线（替代商业收费的 MediatR） | MIT |
| RabbitMQ | 跨进程事件总线 | - |
| SignalR | WebSocket 实时推送 | - |

### 测试与运维
| 组件 | 用途 |
|------|------|
| xUnit + Moq + AutoFixture | 单元测试 |
| TestContainers | 集成测试（数据库/Redis/RabbitMQ 容器） |
| BenchmarkDotNet | 性能基准测试 |
| Serilog | 结构化日志 |
| Docker + Docker Compose | 容器化部署 |
| GitHub Actions | CI/CD |

---

## 3. 项目结构

```
LogisticsHMI/
├── LogisticsHMI.sln
├── src/
│   ├── App.Host/                  # 启动入口、DI 容器配置、appsettings.json
│   ├── App.Core/                  # 领域模型（Task、Device、Alarm 实体、值对象、枚举）
│   │   ├── Models/
│   │   ├── Enums/
│   │   ├── Events/                # 领域事件定义
│   │   └── Interfaces/            # 仓储接口、领域服务接口
│   ├── App.Infrastructure/        # 基础设施层
│   │   ├── Communication/         # 设备通信驱动（Modbus/S7/OPC UA/MQTT）
│   │   │   ├── Abstraction/       # IDeviceDriver 接口、DriverConfig 基类
│   │   │   └── Drivers/           # 各协议具体实现
│   │   ├── Data/                  # EF Core DbContext、仓储实现、Migration
│   │   ├── Caching/              # Redis 缓存服务
│   │   ├── Messaging/            # Wolverine / RabbitMQ 集成
│   │   └── Logging/              # Serilog 配置和扩展
│   └── App.UI/                   # Avalonia UI 层
│       ├── Views/                 # XAML 视图文件
│       ├── ViewModels/            # ViewModel（与 View 一一对应）
│       ├── Controls/             # 自定义控件（设备状态指示器、路径画布等）
│       ├── Converters/           # XAML 值转换器
│       ├── Styles/               # 主题和样式（工业蓝/暗黑/高对比度）
│       └── Services/             # UI 服务（导航、对话框、主题切换）
├── tests/
│   ├── App.Core.Tests/           # 领域层单元测试
│   ├── App.Infrastructure.Tests/ # 基础设施层测试（含通信协议测试）
│   └── App.Integration.Tests/   # 端到端集成测试
├── docs/
│   ├── AGENTS.md                 # 本文件
│   ├── 实施计划.md               # 详细任务拆分与执行计划
│   ├── PROGRESS.md               # 任务进度追踪
│   ├── 技术方案.md               # 完整技术方案文档（建设中）
│   ├── API.md                    # REST API 文档（建设中）
│   └── 数据库设计.md             # ER 图和表结构（建设中）
├── docker/
│   ├── Dockerfile                # 上位机容器镜像（建设中）
│   └── docker-compose.yml       # 完整环境编排（建设中）
└── .github/
    └── workflows/
        ├── ci.yml                # Push → Build → Test（建设中）
        └── release.yml           # Release → Build → Publish → Docker Push（建设中）
```

---

## 4. 开发规范

### 4.1 命名约定

- **项目/命名空间：** `App.Host`、`App.Core`、`App.Infrastructure`、`App.UI`
- **接口：** `I` 前缀 — `IDeviceDriver`、`ITaskRepository`、`IAlarmChannel`
- **抽象基类：** `Base` 后缀 — `ViewModelBase`、`DriverConfigBase`
- **枚举：** 单数命名 — `TaskStatus`、`AlarmLevel`、`DeviceHealth`
- **私有字段：** `_camelCase` — `_deviceManager`、`_logger`
- **异步方法：** `Async` 后缀 — `ConnectAsync()`、`ReadRegisterAsync()`

### 4.2 代码风格

- **文件范围命名空间：** `namespace App.Core.Models;`（C# 10+）
- **可空引用类型：** 全局启用 `<Nullable>enable</Nullable>`
- **隐式 using：** 启用 `<ImplicitUsings>enable</ImplicitUsings>`
- **首选** `Span<T>` / `Memory<T>` 用于协议解析中的缓冲区操作，避免分配
- **首选** `ValueTask` 替代 `Task` 用于可能同步完成的高频异步方法
- **使用** `System.Threading.Channels` 用于生产者-消费者数据管道
- **避免** `#region` 块，使用 partial class 或提取方法替代

### 4.3 MVVM 规范

- 每个 View 对应一个 ViewModel，命名遵循 `XxxView` → `XxxViewModel`
- ViewModel 继承 `ViewModelBase`（提供 IsBusy、错误处理、导航支持）
- 使用 `[ObservableProperty]` 和 `[RelayCommand]` 源码生成器（CommunityToolkit.Mvvm）
- **不在 ViewModel 中直接操作 UI 元素**，使用绑定和转换器
- ViewModel 通过构造函数注入依赖服务

### 4.4 通信层规范

- 所有设备驱动实现 `IDeviceDriver` 接口
- 驱动构造函数接收 `DriverConfig` 配置对象
- 驱动通过 `ConnectionStateChanged` 事件通知上层连接状态变化
- 驱动内部实现心跳检测和自动重连，上层无需关心
- 使用 `Span<T>` 进行协议帧零分配解析
- 数据读取优先查本地缓存（`DeviceDataCache`），减少 PLC 轮询压力

### 4.5 异常处理

- **领域层：** 抛出特定领域异常（`TaskNotFoundException`、`DeviceOfflineException` 等）
- **基础设施层：** 捕获通信异常并包装为 `CommunicationException`
- **UI 层：** 在 ViewModelBase 中全局捕获，通过 `ErrorMessages` 属性展示
- **不吞掉异常**：所有异常必须记录到 Serilog 日志
- **关键操作**（设备控制指令下发）异常必须触发报警

---

## 5. 开发阶段与当前进度

### 总体时间线（23 周 ≈ 6 个月）

| Phase | 内容 | 工期 | 累计 | 里程碑 |
|-------|------|------|------|--------|
| **1** | 基础框架搭建 (9 项任务) | 2~3 w | 3 w | 项目可运行 |
| **2** | 通信层开发 (11 项任务) | 3~4 w | 7 w | Modbus/S7/MQTT 驱动可用 |
| **3** | 数据平台 (7 项任务) | 3~4 w | 11 w | 数据管道流转 |
| **4** | 核心业务 (18 项任务) | 6~8 w | 19 w | 🎯 **MVP** 完整出入库流程 |
| **5** | 系统集成 (5 项任务) | 3~4 w | 23 w | API + Web 远程可用 |
| **6** | 增强功能 (5 项任务) | 按需 | — | 多语言/3D/AI |
| **7** | 测试发布 (9 项任务) | 贯穿 | 23+ w | 🎯 **v1.0** 可交付 |

### 当前状态

- [x] **规划完成**：docs/实施计划.md — 64 项任务拆分、依赖关系、风险评估
- [ ] **Phase 1**：基础框架搭建 (9 项任务，待开始) — 下一步
- [ ] **Phase 2**：通信层开发 (11 项任务，待开始)
- [ ] **Phase 3**：数据平台 (7 项任务，待开始)
- [ ] **Phase 4**：核心业务 (18 项任务，待开始) — 🎯 **MVP 里程碑**
- [ ] **Phase 5**：系统集成 (5 项任务，待开始)
- [ ] **Phase 6**：增强功能 (5 项任务，待开始)
- [ ] **Phase 7**：测试发布 (9 项任务，待开始)

### 已完成工作

| 日期 | 工作内容 |
|------|----------|
| 2026-06-15 | 完成项目规划：docs/实施计划.md 含 64 项任务依赖关系与执行策略 |
| 2026-06-15 | 建立进度追踪：docs/PROGRESS.md 含全部任务状态表 |
| 2026-06-15 | 更新本文件：AGENTS.md 进度状态与目录引用 |

---

## 6. 关键架构决策（ADR）

### ADR-001：分层架构而非微服务

**决策：** 采用经典四层架构（表示层/应用层/领域层/基础设施层），不拆分为微服务。
**理由：** 上位机是垂直集成度高的单体桌面应用，团队规模不大，微服务带来的运维复杂度远超收益。Phase 5 时通过 REST API 和消息队列暴露能力给外部系统。

### ADR-002：使用 Wolverine 而非 MediatR

**决策：** 进程内事件驱动使用 Wolverine（MIT 许可），不使用 MediatR（已商业收费）。
**理由：** Wolverine 不仅提供 Mediator 能力，还内置消息总线、重试、计划任务，更适合工业场景的可靠性要求。且 MIT 许可永久免费。

### ADR-003：SkiaSharp 4.x 作为 2D 绘制核心

**决策：** 所有 2D 自定义绘制（设备图标、路径动画、库区热力图）使用 SkiaSharp 4.x，不引入外部绘图工具。
**理由：** v4 新增可变字体、CPAL 色彩字体、SKPathBuilder 不可变路径、Metal/Vulkan GPU 后端，一套 NuGet 包覆盖全部 2D 需求。

### ADR-004：时序数据选 TDengine 为主

**决策：** 设备运行数据主存 TDengine，备选 InfluxDB。
**理由：** TDengine 写入性能和压缩率（10~20x）全面领先，且内置流处理和 AI 代理。起步阶段可用 PostgreSQL 存储少量时序数据，数据量大后平滑切换。

### ADR-005：Impeller 暂不入生产

**决策：** 当前使用 Skia 渲染后端，Impeller 等待正式发布后再评估。
**理由：** Impeller 目前为实验阶段。Avalonia 12 基于 Skia 已实现 1867% FPS 提升，当前性能足够。

### ADR-006：NativeAOT 为发布目标

**决策：** 生产发布使用 NativeAOT 编译，开发阶段使用 JIT。
**理由：** NativeAOT 实现冷启动 <2s、内存占用降低 30~50%、单文件发布 <80MB，适合工控机环境。但开发时调试不便，所以开发期使用常规 JIT。

---

## 7. 核心接口定义

### IDeviceDriver（通信驱动统一接口）

```csharp
public interface IDeviceDriver : IAsyncDisposable
{
    string DriverName { get; }
    DriverConfig Config { get; }
    ConnectionState State { get; }
    event EventHandler<ConnectionStateChangedEventArgs> ConnectionStateChanged;

    Task<Result> ConnectAsync(CancellationToken ct = default);
    Task<Result> DisconnectAsync(CancellationToken ct = default);
    Task<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default);
    Task<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default);
    Task<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default);
}
```

### ITaskRepository（任务仓储接口）

```csharp
public interface ITaskRepository
{
    Task<TaskEntity?> GetByIdAsync(Guid taskId, CancellationToken ct = default);
    Task<IReadOnlyList<TaskEntity>> GetByStatusAsync(TaskStatus status, CancellationToken ct = default);
    Task<IReadOnlyList<TaskEntity>> GetPendingAsync(int maxCount, CancellationToken ct = default);
    Task AddAsync(TaskEntity task, CancellationToken ct = default);
    Task UpdateAsync(TaskEntity task, CancellationToken ct = default);
    Task<IReadOnlyList<TaskEntity>> QueryAsync(TaskQuerySpec spec, CancellationToken ct = default);
}
```

---

## 8. 关键枚举

```csharp
// 任务状态
public enum TaskStatus
{
    Pending,      // 待分配
    Assigned,     // 已分配，等待执行
    Running,      // 执行中
    Paused,       // 暂停
    Completed,    // 已完成
    Failed,       // 失败
    Cancelled     // 已取消
}

// 报警级别
public enum AlarmLevel
{
    Info = 1,     // 提示
    Warning = 2,  // 警告
    Critical = 3, // 严重
    Emergency = 4 // 紧急
}

// 设备健康状态
public enum DeviceHealth
{
    Online,       // 在线正常
    Idle,         // 在线空闲
    Busy,         // 运行中
    Warning,      // 预警
    Fault,        // 故障
    Maintenance,  // 维护中
    Offline       // 离线
}
```

---

## 9. 常用命令

```bash
# 还原依赖
dotnet restore

# 构建
dotnet build

# 运行（开发模式，JIT）
dotnet run --project src/App.Host

# 运行测试
dotnet test

# 运行特定测试
dotnet test --filter "FullyQualifiedName~ModbusDriver"

# 性能基准测试
dotnet run --project tests/App.Benchmarks -c Release

# 添加 EF Core Migration
dotnet ef migrations add MigrationName -p src/App.Infrastructure -s src/App.Host

# 更新数据库
dotnet ef database update -p src/App.Infrastructure -s src/App.Host

# NativeAOT 发布 (Windows x64)
dotnet publish src/App.Host -c Release -r win-x64 -p:PublishAot=true -p:TrimMode=full

# NativeAOT 发布 (Linux x64)
dotnet publish src/App.Host -c Release -r linux-x64 -p:PublishAot=true -p:TrimMode=full

# Docker Compose 启动完整环境
docker compose -f docker/docker-compose.yml up -d

# 查看 Serilog 日志
tail -f logs/logistics-hmi-*.log
```

---

## 10. 给 AI 代理的工作指南

### 开始新任务时

1. **首先阅读本文件**，了解项目背景和当前进度
2. **确定任务属于哪个 Phase**，检查该 Phase 的前置依赖是否已就绪
3. **确认技术选型**：不要引入本文件未列出的新库，除非明确必要并讨论
4. **遵循命名和代码风格约定**（见第 4 节）

### 编写代码时

- **通信协议代码**：优先使用 `Span<T>` 零分配，关注协议帧格式的正确性
- **UI 代码**：遵循 MVVM 模式，View 只做绑定，逻辑在 ViewModel
- **数据访问**：使用 EF Core + Specification 模式，所有操作为 async
- **日志**：关键路径加 `_logger.LogDebug/Information/Warning/Error`
- **异常**：按 4.5 节的规范处理

### 交付任务时

- 如果产出的是代码：确保编译通过，相关单元测试通过
- 如果产出的是文档：更新到 `docs/` 目录，在本文件中更新 Phase 进度
- 在提交信息中引用任务编号（如 `feat(4.3): 实现任务拆分与编排器`）

---

## 11. 项目文档索引

| 文档 | 位置 | 用途 |
|------|------|------|
| 技术方案蓝皮书 | `docs/物流上位机系统_前沿务实方案.html` | 完整技术方案、选型对比、架构决策 |
| 详细实施计划 | `docs/实施计划.md` | 64 项任务拆分、依赖关系图、风险清单 |
| 任务进度追踪 | `docs/PROGRESS.md` | 各任务完成状态、完成时间、证据文件 |
| 本文件 | `AGENTS.md` | 项目上下文的快速参考（技术栈、规范、ADR） |

---

### 每会话启动流程（SOP）

每次 Agent 或开发者开始新会话时：

1. 读取 `AGENTS.md` — 了解项目背景、规范、当前进度
2. 读取 `docs/PROGRESS.md` — 查看哪些任务已完成、当前正在做什么
3. 读取 `docs/实施计划.md` — 获取当前任务的详细说明和前置依赖
4. 如果涉及编辑代码，先阅读相邻已完成的代码文件保持风格一致
5. 执行任务后：`dotnet build` → `dotnet test` → 更新 PROGRESS.md → 记录会话

---

*本文件基于《物流上位机系统 · 前沿务实技术方案 v4.0》生成。完整方案详见 `docs/物流上位机系统_前沿务实方案.html`。详细任务计划详见 `docs/实施计划.md`。*

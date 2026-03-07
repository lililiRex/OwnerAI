# OwnerAI

> 中文 | [English](#english)

OwnerAI 是一个面向 Windows 桌面环境的、可观测、可扩展、可自我进化的 AI Agent 宿主。

OwnerAI is an observable, extensible, self-evolving AI agent host designed for the Windows desktop.

---

## 中文

### 1. 项目简介

OwnerAI 旨在成为用户个人电脑上的“硅基生产力”和“硅基劳动力”基础设施。

它不仅是一个聊天界面，更是一个完整的 AI 宿主平台，具备：

- 多模型接入与智能路由
- 原生工具调用与函数编排
- 长短期记忆与任务缓存
- 后台任务调度与自主进化
- 插件、钩子、通信通道扩展能力
- 本地优先、安全可控、可审计的执行环境
- Windows 桌面原生交互体验

它可以作为：

- 个人 AI 助手
- 本地 AI 工作台
- 企业内部智能工作流宿主
- 设备通信与自动化控制中枢
- 自我演化型 Agent 实验平台

---

### 2. 核心价值

#### 2.1 面向真实工作，而不只是对话

OwnerAI 通过 `function calling` 调用本地工具，能真实读取文件、操作目录、搜索网页、下载资源、调用系统命令、管理任务、连接 TCP/UDP/串口设备，并将结果回馈模型继续推理。

#### 2.2 面向多模型协作，而不绑定单一模型

系统内建多模型注册、分类、工作槽位分配、故障转移与度量记录能力，可同时使用本地 Ollama 模型和 OpenAI 兼容接口模型。

#### 2.3 面向长期运行，而不是一次性脚本

系统具备：

- SQLite 持久化
- 事件总线
- 审计日志
- 健康检查
- 模型性能度量
- 技能生命周期管理
- 进化状态机与验收机制

#### 2.4 面向二次开发，而不是封闭应用

OwnerAI 提供清晰的接口层：

- `IOwnerAITool`
- `IPlugin`
- `IHook`
- `IChannelAdapter`
- `IEventBus`

开发者可以将它作为宿主平台，扩展自己的工具、插件、消息通道和业务逻辑。

---

### 3. 当前能力总览

#### 3.1 技术栈

- `.NET 10`
- `C# 14`
- `WinUI 3` (`Microsoft.WindowsAppSDK 2.0 preview`)
- `Microsoft.Extensions.AI`
- `SQLite`
- `Serilog`
- `CommunityToolkit.Mvvm`

#### 3.2 项目结构

当前解决方案包含多个分层项目：

- `src/OwnerAI.Shared`：共享模型、抽象、事件、基础类型
- `src/OwnerAI.Configuration`：配置模型与验证
- `src/OwnerAI.Agent`：Agent 执行、上下文、记忆、进化、调度、编排、Provider 路由
- `src/OwnerAI.Agent.Tools`：原生工具集
- `src/OwnerAI.Gateway`：网关、管道、中间件、会话、通道、健康检查
- `src/OwnerAI.Security`：审批、审计、密钥、沙箱与安全控制
- `src/OwnerAI.Host.Desktop`：WinUI 3 桌面宿主
- `src/OwnerAI.Host.Cli`：CLI 宿主
- `tests/*`：测试项目

#### 3.3 模型能力

- 支持 14 类模型能力分类：
  - `LLM`
  - `Vision`
  - `Multimodal`
  - `Science`
  - `Coding`
  - `Reasoning`
  - `ImageGen`
  - `Audio`
  - `Translation`
  - `Writing`
  - `DataAnalysis`
  - `Embedding`
  - `ImageToVideo`
  - `TextToVideo`
- 支持 8 个工作槽位：
  - `ChatDefault`
  - `ChatFast`
  - `CodeTask`
  - `DeepReasoning`
  - `EvolutionPlanning`
  - `EvolutionExecution`
  - `EvolutionVerification`
  - `VisionAssist`
- 支持本地模型与远程模型混合部署
- 支持自动故障转移与流式探活
- 支持调用度量记录（延迟、成功率、token）

#### 3.4 Agent 能力

- 多轮 ReAct 推理循环
- 流式响应
- 工具调用编排
- 多模型委派
- 上下文预算控制
- 历史消息注入
- 用户画像注入
- 任务缓存三态命中
- 长期记忆检索
- 多模态附件处理

#### 3.5 记忆与缓存能力

- TiMem 五层记忆：
  - L1 对话碎片
  - L2 会话摘要
  - L3 日报摘要
  - L4 周报摘要
  - L5 用户画像
- SQLite + FTS5 全文检索
- 字符 n-gram 相似度精排
- 任务缓存：
  - `ExactHit`
  - `ReferenceHit`
  - `Miss`

#### 3.6 自我进化能力

- 缺口发现、记录与跟踪
- 三阶段进化流程：
  - Planning
  - Execution
  - Verification / Acceptance
- Docker 沙箱验证
- 技能生命周期自动管理：
  - `trial`
  - `stable`
  - `deprecated`
- 进化数据持久化与失败恢复

#### 3.7 安全能力

- 路径校验
- 命令校验
- 风险分级工具安全模型
- 审批服务：CLI / Desktop 双实现
- 审计日志
- DPAPI 密钥加密存储
- 高风险操作审批

#### 3.8 网关与通信能力

- 消息网关
- 会话管理
- 事件总线
- 中间件管道
- 健康检查
- 原生通信工具：
  - TCP
  - UDP
  - Serial Port
- 通道适配器：
  - `TcpChannelAdapter`
  - `UdpChannelAdapter`
  - `SerialChannelAdapter`

#### 3.9 桌面端能力

- 聊天页面
- 任务页面
- 技能页面
- 度量页面
- 设置页面
- 托盘图标
- 流式消息渲染
- 工具调用可视化
- 进化状态可视化
- 模型度量面板

---

### 4. 原生工具集

当前系统内置多个原生工具，覆盖文件、系统、网络、下载、文档、调度、进化与通信场景。

主要包括：

- 文件系统：
  - `read_file`
  - `write_file`
  - `list_directory`
  - `search_files`
- 系统与进程：
  - `run_command`
  - `open_app`
  - `process_list`
  - `system_info`
- Web：
  - `web_search`
  - `web_fetch`
- 下载：
  - `download_file`
  - `download_video`
- 文档：
  - `document_tool`
- 剪贴板：
  - `clipboard`
- 模型协作：
  - `delegate_to_model`
- 调度：
  - `schedule_task`
- 自我进化：
  - `self_evolve`
- 外部技能桥接：
  - `openclaw_skill`
- 通信：
  - `tcp_communicate`
  - `udp_communicate`
  - `serial_communicate`

> 实际注册工具以运行时 DI 注册结果为准。

---

### 5. 架构概览

#### 5.1 总体流程

```text
User / Device / Channel
        ↓
   GatewayEngine
        ↓
 Gateway Pipeline
        ↓
   AgentExecutor
        ↓
ToolOrchestrator / ProviderFailover / Memory / Cache
        ↓
 Tool / Plugin / Hook / Channel / EventBus
```

#### 5.2 关键模块

##### `GatewayEngine`

统一消息入口，负责：

- 创建或获取会话
- 构建 `MessageContext`
- 发布消息事件
- 执行网关中间件管道
- 生成回复载荷

##### `AgentExecutor`

Agent 的核心执行器，负责：

- 构建系统提示词
- 注入记忆、历史、模型团队信息
- 执行多轮 ReAct 推理
- 处理流式响应
- 触发工具调用
- 记录缓存与度量

##### `ToolOrchestrator`

工具注册和执行中心，负责：

- 枚举所有 `IOwnerAITool`
- 转换为 AI 可调用函数
- 生成参数 Schema
- 执行工具并回传结果
- 分发工具钩子事件

##### `ProviderRegistry` / `ProviderFailover`

负责模型注册、工作槽路由、优先级与故障转移。

##### `SchedulerService`

负责后台任务调度和进化任务协作，保证长期自治运行。

##### `HookManager`

负责钩子事件分发，支持按优先级执行和取消链路。

##### `PluginLoader`

负责插件生命周期管理：

- Initialize
- Start
- Stop
- Dispose

---

### 6. 扩展接口

#### 6.1 自定义工具：`IOwnerAITool`

适用于：

- 新增本地能力
- 封装第三方服务
- 实现设备控制
- 实现业务自动化

工具由特性描述元数据，并由宿主自动注册到编排系统。

示意：

```csharp
[Tool("hello_tool", "返回一个问候消息")]
public sealed class HelloTool : IOwnerAITool
{
    public Task<string> ExecuteAsync(string arguments, CancellationToken ct)
    {
        return Task.FromResult("hello from OwnerAI");
    }
}
```

> 具体接口签名请以 `src/OwnerAI.Shared/Abstractions/IOwnerAITool.cs` 为准。

工具元数据支持：

- 名称
- 描述
- 安全级别
- 所需权限
- 是否需要沙箱
- 超时时间

#### 6.2 自定义插件：`IPlugin`

适用于：

- 集成外部服务
- 添加后台能力
- 注入宿主服务依赖
- 管理独立数据目录

核心接口：

```csharp
public interface IPlugin : IAsyncDisposable
{
    Task InitializeAsync(PluginContext context, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

插件上下文可获得：

- `PluginManifest`
- 插件数据目录
- 宿主 `IServiceProvider`

#### 6.3 自定义钩子：`IHook`

适用于：

- 审计
- 监控
- 自定义安全策略
- 工具前后处理
- 对话前后处理

核心接口：

```csharp
public interface IHook
{
    string EventName { get; }
    int Priority { get; }
    ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken ct);
}
```

已定义事件包括：

- `agent:before-start`
- `agent:after-reply`
- `tool:before-call`
- `tool:after-call`
- `message:received`
- `message:sending`
- `session:created`
- `session:ended`
- `config:changed`
- `plugin:loaded`
- `memory:consolidated`

#### 6.4 自定义通道：`IChannelAdapter`

适用于：

- 接入 IM / 企业消息平台
- 接入硬件总线
- 接入机器人、设备、边缘节点
- 做被动式消息入口

核心能力包括：

- 启动通道
- 发送出站消息
- 提供入站消息流
- 暴露健康状态与能力标记

当前已有：

- TCP
- UDP
- Serial Port

#### 6.5 事件系统：`IEventBus`

适用于：

- UI 状态同步
- 后台状态广播
- 调度事件通知
- 演化状态更新
- 可观测性集成

核心接口：

```csharp
public interface IEventBus
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IOwnerAIEvent;
}
```

---

### 7. 二次开发指南

#### 7.1 新增一个工具

1. 在 `src/OwnerAI.Agent.Tools` 下选择合适目录新增工具类
2. 使用 `ToolAttribute` 描述名称、用途和安全级别
3. 实现 `IOwnerAITool`
4. 在 `src/OwnerAI.Agent.Tools/ServiceCollectionExtensions.cs` 中注册
5. 如需函数调用参数支持，确保可被 `ToolOrchestrator` 正确转换
6. 为新工具补充测试

建议：

- 保持单一职责
- 明确参数与返回结构
- 对危险行为设置合理安全级别
- 尽量返回结构化结果，而不是杂乱字符串

#### 7.2 新增一个插件

1. 定义插件清单 `PluginManifest`
2. 实现 `IPlugin`
3. 在初始化中读取配置、创建状态
4. 在启动阶段连接外部资源
5. 在停止阶段释放资源
6. 使用宿主服务完成日志、事件、配置、缓存接入

#### 7.3 新增一个钩子

1. 实现 `IHook`
2. 指定监听事件名
3. 指定优先级
4. 在执行逻辑中读取和修改上下文属性
5. 返回 `HookResult.Ok()` 或 `HookResult.Cancel()`

适合做：

- 敏感工具拦截
- 统一日志
- 自动标记会话
- 性能测量
- 自定义安全策略

#### 7.4 新增一个消息通道

1. 实现 `IChannelAdapter`
2. 定义能力标记 `ChannelCapabilities`
3. 实现入站消息枚举和出站消息发送
4. 接入 `GatewayEngine`
5. 添加必要健康检查和日志

典型场景：

- 企业微信 / 钉钉 / Slack / Telegram
- PLC / 串口设备 / 网关设备
- 局域网 agent-to-agent 协作

#### 7.5 扩展模型路由

可以从以下方向扩展：

- 新增 Provider 类型
- 新增模型能力分类
- 新增工作槽位
- 基于度量实现更复杂路由策略
- 为不同任务配置不同 fallback 链路

#### 7.6 扩展桌面 UI

桌面端采用 WinUI 3 + MVVM，可扩展：

- 新页面
- 新卡片组件
- 新状态面板
- 新日志查看器
- 新的设置项与引导流程

---

### 8. 自我进化机制

OwnerAI 并不是只会执行固定工具的 Agent。

它具备自我进化能力，可以：

- 记录能力缺口
- 规划实现步骤
- 执行实现任务
- 进行验证与验收
- 产出新的技能能力
- 自动管理技能生命周期

当前进化工作流强调明确阶段划分：

1. **Planning**：为发现的能力缺口生成实施计划
2. **Execution**：按步骤执行实现任务
3. **Verification / Acceptance**：验证结果并形成最终技能

这使得进化过程更可观测、更可恢复、更容易被人类审计与接管。

---

### 9. 安全与治理

OwnerAI 设计为默认安全、显式审批、可追踪。

#### 9.1 工具安全等级

- `ReadOnly`
- `Low`
- `Medium`
- `High`
- `Critical`

#### 9.2 安全机制

- 危险命令校验
- 路径访问校验
- 文件写入限制
- 审批服务
- 审计日志
- DPAPI 密钥保护
- Docker 沙箱验证

#### 9.3 审批模式

- `AutoApprove`
- `HighRiskOnly`
- `AlwaysAsk`

桌面模式下通过 `ContentDialog` 进行审批，CLI 模式下通过控制台交互审批。

---

### 10. 可观测性

系统具备较完整的可观测能力：

- 结构化日志（Serilog）
- 模型调用度量
- 审计日志
- 事件总线
- 调度状态事件
- 进化状态事件
- 健康检查：
  - SQLite
  - LLM endpoint
  - Docker
  - Disk space

---

### 11. 运行与开发

#### 11.1 环境要求

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2026 / 最新 .NET 开发环境
- 建议安装 Ollama 以获得本地模型能力
- 如需文档或视频能力，可安装相关本地软件或工具链

#### 11.2 推荐本地模型

当前已知可用的本地模型包括：

- `GLM-4.6v-Flash-9B`
- `deepseek-r1:8B`
- `deepseek-r1:14B`

#### 11.3 构建

```powershell
dotnet build
```

#### 11.4 运行桌面宿主

请将桌面宿主项目设为启动项目后运行，或使用：

```powershell
dotnet run --project .\src\OwnerAI.Host.Desktop\OwnerAI.Host.Desktop.csproj
```

#### 11.5 运行测试

```powershell
dotnet test
```

---

### 12. 配置说明

系统通过集中配置控制：

- 模型 Provider
- Agent 人设
- 安全策略
- 记忆参数
- 插件目录
- UI 行为
- 工作槽位模型绑定

关键配置模型位于：

- `src/OwnerAI.Configuration/OwnerAIConfig.cs`

建议阅读：

- `docs/OwnerAI-Capabilities.md`

---

### 13. 推荐阅读顺序

如果你想快速理解本项目，建议按以下顺序阅读：

1. `README.md`
2. `docs/OwnerAI-Capabilities.md`
3. `src/OwnerAI.Configuration/OwnerAIConfig.cs`
4. `src/OwnerAI.Agent/AgentExecutor.cs`
5. `src/OwnerAI.Agent/Orchestration/ToolOrchestrator.cs`
6. `src/OwnerAI.Agent/Providers/ProviderRegistry.cs`
7. `src/OwnerAI.Agent/Providers/ProviderFailover.cs`
8. `src/OwnerAI.Agent/Scheduler/SchedulerService.cs`
9. `src/OwnerAI.Agent/Evolution/SelfEvolveTool.cs`
10. `src/OwnerAI.Gateway/GatewayEngine.cs`

---

### 14. 适用场景

OwnerAI 特别适合以下方向：

- 本地 AI 助手
- 个人知识工作站
- 自动化办公
- 开发辅助与代码生成
- 多模型协作实验
- 可审计 Agent 平台
- 设备接入与控制
- AI + 工控 / IoT / 边缘节点融合
- 自我进化智能体研究

---

### 15. 开源共建邀请

如果你也相信：

- AI 不应只是云端 API 的被动调用者
- AI 应成为真实世界中可执行、可审计、可协作的生产力系统
- 人类与机器的协作，值得拥有更开放、更可靠、更尊重用户主权的基础设施

那么，欢迎你加入 OwnerAI。

无论你是：

- .NET / Windows 开发者
- 大模型应用开发者
- 工具链工程师
- 自动化与 RPA 开发者
- 机器人 / IoT / 串口 / 工控工程师
- 产品设计师
- 测试工程师
- 文档贡献者

都欢迎你通过 Issue、Discussion、PR、文档、测试、架构建议、插件贡献，一起完善这个项目。

我们诚挚邀请全世界的开发者，一起为“硅基生产力”和“硅基劳动力”的未来添砖加瓦。

让 AI 不止会说，更会做；
让 Agent 不止能跑，更能长期演化；
让软件不止服务人，也能与人并肩工作。

---

### 16. 许可证与说明

本仓库建议在开源发布时补充正式许可证文件（如 `MIT` 或 `Apache-2.0`）。

如果你准备将 OwnerAI 面向全球开源，建议同时补充：

- `LICENSE`
- `CONTRIBUTING.md`
- `CODE_OF_CONDUCT.md`
- `SECURITY.md`
- GitHub Actions CI
- 演示截图或 GIF

---

## English

### 1. Overview

OwnerAI is a Windows-native, observable, extensible, self-evolving AI agent host.

It is designed to become infrastructure for both **silicon-based productivity** and **silicon-based labor** on personal computers and controlled environments.

OwnerAI is not just a chat UI. It is a complete host platform for AI agents with:

- multi-model routing and failover
- native tool execution and function orchestration
- long-term memory and task caching
- background scheduling and autonomous evolution
- plugin, hook, and channel extensibility
- local-first, safe, auditable execution
- native desktop experience on Windows

It can serve as:

- a personal AI assistant
- a local AI workstation
- an enterprise internal agent host
- a device communication and automation hub
- a research platform for self-evolving agents

---

### 2. Core Value

#### 2.1 Built for real work, not chat only

OwnerAI uses `function calling` to operate real tools on the local machine. It can read files, inspect directories, search the web, download resources, execute system commands, manage scheduled tasks, and communicate with TCP/UDP/serial devices.

#### 2.2 Built for model collaboration, not a single-provider lock-in

The system provides provider registration, model categorization, work-slot assignment, failover, and metric collection. It supports both local Ollama models and OpenAI-compatible endpoints.

#### 2.3 Built for long-running operation, not one-off scripts

The platform includes:

- SQLite persistence
- event bus
- audit logging
- health checks
- model metrics
- skill lifecycle management
- evolution state and acceptance flow

#### 2.4 Built for secondary development, not a closed application

OwnerAI exposes clear extensibility interfaces:

- `IOwnerAITool`
- `IPlugin`
- `IHook`
- `IChannelAdapter`
- `IEventBus`

Developers can use OwnerAI as a host platform and extend it with custom tools, plugins, channels, and business logic.

---

### 3. Feature Matrix

#### 3.1 Tech Stack

- `.NET 10`
- `C# 14`
- `WinUI 3` (`Microsoft.WindowsAppSDK 2.0 preview`)
- `Microsoft.Extensions.AI`
- `SQLite`
- `Serilog`
- `CommunityToolkit.Mvvm`

#### 3.2 Solution Layout

- `src/OwnerAI.Shared`: shared models, abstractions, events, base types
- `src/OwnerAI.Configuration`: configuration models and validation
- `src/OwnerAI.Agent`: agent execution, context, memory, evolution, scheduling, orchestration, provider routing
- `src/OwnerAI.Agent.Tools`: built-in tools
- `src/OwnerAI.Gateway`: gateway, middleware, sessions, channels, health checks
- `src/OwnerAI.Security`: approval, audit, secrets, sandboxing, safety controls
- `src/OwnerAI.Host.Desktop`: WinUI 3 desktop host
- `src/OwnerAI.Host.Cli`: CLI host
- `tests/*`: test projects

#### 3.3 Model Capabilities

- 14 model categories, including:
  - `LLM`
  - `Vision`
  - `Multimodal`
  - `Science`
  - `Coding`
  - `Reasoning`
  - `ImageGen`
  - `Audio`
  - `Translation`
  - `Writing`
  - `DataAnalysis`
  - `Embedding`
  - `ImageToVideo`
  - `TextToVideo`
- 8 work slots:
  - `ChatDefault`
  - `ChatFast`
  - `CodeTask`
  - `DeepReasoning`
  - `EvolutionPlanning`
  - `EvolutionExecution`
  - `EvolutionVerification`
  - `VisionAssist`
- local + remote model mix
- automatic failover with probe-first streaming
- metric recording for latency, success rate, and tokens

#### 3.4 Agent Features

- multi-round ReAct loop
- streaming responses
- tool orchestration
- delegated model collaboration
- context budgeting
- history injection
- user profile injection
- three-state task cache hits
- long-term memory retrieval
- multimodal attachment handling

#### 3.5 Memory and Cache

- TiMem five-layer memory:
  - L1 fragment
  - L2 session summary
  - L3 daily summary
  - L4 weekly summary
  - L5 user profile
- SQLite + FTS5 full-text retrieval
- character n-gram reranking
- task cache states:
  - `ExactHit`
  - `ReferenceHit`
  - `Miss`

#### 3.6 Self-Evolution

- gap discovery, reporting, and tracking
- three-stage evolution workflow:
  - Planning
  - Execution
  - Verification / Acceptance
- Docker-based sandbox verification
- automatic skill lifecycle management:
  - `trial`
  - `stable`
  - `deprecated`
- persistent evolution state and recovery

#### 3.7 Security

- path validation
- command validation
- risk-based tool security model
- approval service for CLI and desktop
- audit logging
- DPAPI secret protection
- approval for high-risk operations

#### 3.8 Gateway and Communication

- message gateway
- session management
- event bus
- middleware pipeline
- health checks
- native communication tools:
  - TCP
  - UDP
  - Serial Port
- channel adapters:
  - `TcpChannelAdapter`
  - `UdpChannelAdapter`
  - `SerialChannelAdapter`

#### 3.9 Desktop Host Features

- chat page
- scheduler page
- skills page
- metrics page
- settings page
- tray icon
- streaming message rendering
- tool call visualization
- evolution status visualization
- model metrics dashboard

---

### 4. Built-in Tooling

OwnerAI ships with built-in tools across file access, system control, web retrieval, downloads, documents, scheduling, self-evolution, and communication.

Examples include:

- File system:
  - `read_file`
  - `write_file`
  - `list_directory`
  - `search_files`
- System and process:
  - `run_command`
  - `open_app`
  - `process_list`
  - `system_info`
- Web:
  - `web_search`
  - `web_fetch`
- Download:
  - `download_file`
  - `download_video`
- Document:
  - `document_tool`
- Clipboard:
  - `clipboard`
- Model collaboration:
  - `delegate_to_model`
- Scheduling:
  - `schedule_task`
- Self evolution:
  - `self_evolve`
- External skill bridge:
  - `openclaw_skill`
- Communication:
  - `tcp_communicate`
  - `udp_communicate`
  - `serial_communicate`

> The authoritative source is the runtime DI registration.

---

### 5. Architecture

#### 5.1 High-Level Flow

```text
User / Device / Channel
        ↓
   GatewayEngine
        ↓
 Gateway Pipeline
        ↓
   AgentExecutor
        ↓
ToolOrchestrator / ProviderFailover / Memory / Cache
        ↓
 Tool / Plugin / Hook / Channel / EventBus
```

#### 5.2 Key Components

##### `GatewayEngine`

The unified entry point for messages. It is responsible for:

- creating or resolving sessions
- building `MessageContext`
- publishing message events
- executing the gateway middleware pipeline
- returning the final reply payload

##### `AgentExecutor`

The core execution engine of the agent. It is responsible for:

- building system prompts
- injecting memory, history, and model-team context
- executing the multi-round ReAct loop
- handling streaming output
- triggering tool calls
- recording cache and metrics

##### `ToolOrchestrator`

The central tool registration and execution component. It is responsible for:

- enumerating registered `IOwnerAITool` implementations
- converting them into AI-callable functions
- building parameter schemas
- executing tools and returning results
- dispatching tool hook events

##### `ProviderRegistry` / `ProviderFailover`

Responsible for provider registration, work-slot routing, prioritization, and automatic failover.

##### `SchedulerService`

Runs background scheduled jobs and coordinates evolution-related workflows.

##### `HookManager`

Dispatches hook events with priority ordering and cancellation support.

##### `PluginLoader`

Manages plugin lifecycle:

- Initialize
- Start
- Stop
- Dispose

---

### 6. Extensibility Interfaces

#### 6.1 Custom Tools: `IOwnerAITool`

Use this to:

- add local capabilities
- wrap third-party services
- control hardware or systems
- implement business automation

Typical workflow:

1. create a tool class under `src/OwnerAI.Agent.Tools`
2. describe it with `ToolAttribute`
3. implement `IOwnerAITool`
4. register it in `ServiceCollectionExtensions`
5. add tests

Tool metadata supports:

- name
- description
- security level
- required permissions
- sandbox requirement
- timeout

#### 6.2 Custom Plugins: `IPlugin`

Use this to:

- integrate background services
- connect external systems
- reuse host services
- manage independent plugin data folders

Core contract:

```csharp
public interface IPlugin : IAsyncDisposable
{
    Task InitializeAsync(PluginContext context, CancellationToken ct);
    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}
```

#### 6.3 Custom Hooks: `IHook`

Use this to:

- audit actions
- add monitoring
- implement custom safety policy
- pre/post-process tool calls
- pre/post-process agent execution

Core contract:

```csharp
public interface IHook
{
    string EventName { get; }
    int Priority { get; }
    ValueTask<HookResult> ExecuteAsync(HookContext context, CancellationToken ct);
}
```

Defined events include:

- `agent:before-start`
- `agent:after-reply`
- `tool:before-call`
- `tool:after-call`
- `message:received`
- `message:sending`
- `session:created`
- `session:ended`
- `config:changed`
- `plugin:loaded`
- `memory:consolidated`

#### 6.4 Custom Channels: `IChannelAdapter`

Use this to:

- connect chat or enterprise messaging platforms
- connect industrial buses or device networks
- connect robots, gateways, or edge nodes
- create passive inbound entry points for messages

Current implementations include:

- TCP
- UDP
- Serial Port

#### 6.5 Event System: `IEventBus`

Use this to:

- synchronize UI state
- broadcast background state changes
- publish scheduler notifications
- publish evolution status changes
- integrate observability and telemetry flows

Core contract:

```csharp
public interface IEventBus
{
    ValueTask PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IAsyncEnumerable<TEvent> SubscribeAsync<TEvent>(CancellationToken ct = default)
        where TEvent : IOwnerAIEvent;

    IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, ValueTask> handler)
        where TEvent : IOwnerAIEvent;
}
```

---

### 7. Secondary Development Guide

#### 7.1 Add a new tool

1. create a tool class in `src/OwnerAI.Agent.Tools`
2. describe it with `ToolAttribute`
3. implement `IOwnerAITool`
4. register it in `src/OwnerAI.Agent.Tools/ServiceCollectionExtensions.cs`
5. ensure it can be exposed through the orchestration layer
6. add tests

Recommendations:

- keep a single responsibility
- design clear parameters and outputs
- assign an appropriate security level
- prefer structured results over loose text

#### 7.2 Add a plugin

1. define a `PluginManifest`
2. implement `IPlugin`
3. initialize state and config
4. connect external resources in `StartAsync`
5. release resources in `StopAsync`
6. use host services for logging, events, config, and storage

#### 7.3 Add a hook

1. implement `IHook`
2. set the target event name
3. set priority
4. read or modify hook context properties
5. return `HookResult.Ok()` or `HookResult.Cancel()`

#### 7.4 Add a channel

1. implement `IChannelAdapter`
2. define `ChannelCapabilities`
3. implement inbound message streaming and outbound sending
4. connect it to `GatewayEngine`
5. add health checks and logging

Typical scenarios:

- Slack / Telegram / Teams / enterprise IM
- PLC / serial devices / field gateways
- LAN-based agent-to-agent collaboration

#### 7.5 Extend model routing

Possible directions:

- add new provider types
- add model categories
- add work slots
- implement metric-driven routing strategies
- define different fallback chains per task class

#### 7.6 Extend the desktop UI

The desktop app uses WinUI 3 + MVVM and can be extended with:

- new pages
- new card components
- new status panels
- log viewers
- onboarding flows
- advanced configuration panels

---

### 8. Self-Evolution Workflow

OwnerAI is not limited to fixed tools.

It can:

- report capability gaps
- generate implementation plans
- execute implementation steps
- verify and accept results
- produce new skills
- manage skill lifecycle automatically

The current evolution workflow emphasizes explicit stages:

1. **Planning**: generate implementation plans for detected skill gaps
2. **Execution**: execute plan steps
3. **Verification / Acceptance**: verify the result and form the resulting skill

This makes evolution more observable, more recoverable, and easier for humans to audit.

---

### 9. Safety and Governance

OwnerAI is designed to be safe by default, approval-aware, and auditable.

#### 9.1 Tool Security Levels

- `ReadOnly`
- `Low`
- `Medium`
- `High`
- `Critical`

#### 9.2 Safety Mechanisms

- dangerous command validation
- path validation
- file write restrictions
- approval service
- audit logging
- DPAPI secret protection
- Docker sandbox verification

#### 9.3 Approval Modes

- `AutoApprove`
- `HighRiskOnly`
- `AlwaysAsk`

Desktop mode uses `ContentDialog` for approval. CLI mode uses console-based approval.

---

### 10. Observability

OwnerAI includes a strong observability foundation:

- structured logging with Serilog
- model-call metrics
- audit logs
- event bus
- scheduler status events
- evolution status events
- health checks for:
  - SQLite
  - LLM endpoint
  - Docker
  - disk space

---

### 11. Build and Run

#### 11.1 Requirements

- Windows 10/11
- .NET 10 SDK
- Visual Studio 2026 or a recent .NET development environment
- Ollama recommended for local model hosting

#### 11.2 Recommended Local Models

Known available local models include:

- `GLM-4.6v-Flash-9B`
- `deepseek-r1:8B`
- `deepseek-r1:14B`

#### 11.3 Build

```powershell
dotnet build
```

#### 11.4 Run the desktop host

```powershell
dotnet run --project .\src\OwnerAI.Host.Desktop\OwnerAI.Host.Desktop.csproj
```

#### 11.5 Run tests

```powershell
dotnet test
```

---

### 12. Configuration

The system uses centralized configuration for:

- model providers
- agent personas
- safety policies
- memory parameters
- plugin directories
- UI behavior
- model work-slot assignments

Key configuration model:

- `src/OwnerAI.Configuration/OwnerAIConfig.cs`

Recommended document:

- `docs/OwnerAI-Capabilities.md`

---

### 13. Suggested Reading Order

If you want to understand the project quickly, read in this order:

1. `README.md`
2. `docs/OwnerAI-Capabilities.md`
3. `src/OwnerAI.Configuration/OwnerAIConfig.cs`
4. `src/OwnerAI.Agent/AgentExecutor.cs`
5. `src/OwnerAI.Agent/Orchestration/ToolOrchestrator.cs`
6. `src/OwnerAI.Agent/Providers/ProviderRegistry.cs`
7. `src/OwnerAI.Agent/Providers/ProviderFailover.cs`
8. `src/OwnerAI.Agent/Scheduler/SchedulerService.cs`
9. `src/OwnerAI.Agent/Evolution/SelfEvolveTool.cs`
10. `src/OwnerAI.Gateway/GatewayEngine.cs`

---

### 14. Use Cases

OwnerAI is especially suitable for:

- local AI assistants
- personal knowledge workstations
- office automation
- coding assistance and code generation
- multi-model orchestration experiments
- auditable agent platforms
- device integration and control
- AI + industrial / IoT / edge fusion
- self-evolving agent research

---

### 15. Open Source Invitation

If you also believe that:

- AI should not remain a passive consumer of cloud APIs only
- AI should become an executable, auditable, collaborative productivity system in the real world
- human-machine collaboration deserves more open, reliable, and user-sovereign infrastructure

then you are warmly invited to join OwnerAI.

Whether you are a:

- .NET / Windows developer
- LLM application developer
- tooling engineer
- automation or RPA developer
- robotics / IoT / serial / industrial engineer
- product designer
- tester
- technical writer

we welcome your Issues, Discussions, Pull Requests, tests, documentation, plugin contributions, and architectural ideas.

We sincerely invite developers from all over the world to help build the future of **silicon-based productivity** and **silicon-based labor** together.

Let AI not only speak, but also act.
Let agents not only run, but also evolve over time.
Let software not only serve people, but also work alongside them.

---

### 16. License and Repository Readiness

For public open-source release, it is strongly recommended to add:

- `LICENSE` (`MIT` or `Apache-2.0`)
- `CONTRIBUTING.md`
- `CODE_OF_CONDUCT.md`
- `SECURITY.md`
- GitHub Actions CI
- screenshots or demo GIFs

---

## Star History of Ideas

If this project resonates with you, please consider starring it, testing it, extending it, or building on top of it.

Every issue, every PR, every plugin, every test, every document page, and every thoughtful review helps move the ecosystem forward.

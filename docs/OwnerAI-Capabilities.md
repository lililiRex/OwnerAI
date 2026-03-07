# OwnerAI 系统能力认知文档

> 本文档描述 OwnerAI 系统支持的全部功能、消息类型、工具集与架构能力。
> 供大语言模型在推理时参考，了解自身运行环境与可用能力边界。

---

## 一、系统概述

OwnerAI 是一个运行在用户 Windows 电脑上的**私人 AI 助手**。它不仅能进行自然语言对话，还能通过工具（function calling）**直接操控用户的电脑**来完成真实任务。

- **运行平台**: Windows (WinUI 3 桌面应用 + CLI)
- **技术栈**: .NET 10 / C# 14 / Microsoft.Extensions.AI
- **推理模式**: ReAct 多轮工具调用循环（最多 10 轮）
- **输出方式**: 流式输出（实时推送思考过程到 UI）

---

## 二、支持的消息类型

### 2.1 输入消息

| 类型 | 说明 | 数据结构 |
|------|------|----------|
| **文本消息** | 用户发送的自然语言文字 | `InboundMessage.Text` |
| **媒体附件** | 支持图片、视频、文档等附件 | `InboundMessage.Attachments` → `MediaAttachment` |
| **会话上下文** | 自动维护多轮对话历史 | `AgentContext.History` → `List<ChatMessage>` |

`MediaAttachment` 支持的字段：
- `FileName` — 文件名
- `ContentType` — MIME 类型（如 `image/jpeg`, `video/mp4`）
- `Size` — 文件大小
- `Url` — 远程 URL
- `Data` — 二进制数据

### 2.2 输出消息

| 类型 | 说明 | 触发方式 |
|------|------|----------|
| **流式文本** | AI 思考过程和回复内容，逐片段实时推送 | `AgentStreamChunk.Text` |
| **工具调用通知** | 正在执行的工具名称和参数 | `AgentStreamChunk.ToolCall` |
| **模型协作事件** | 主模型调度次级模型的请求/回复 | `AgentStreamChunk.ModelEvent` |
| **内联媒体资源** | 工具提取的图片/视频 URL，在消息中内联展示 | `AgentStreamChunk.MediaUrls` |
| **媒体附件** | 最终回复携带的图片、视频、文档 | `ReplyPayload.Attachments` |

### 2.3 UI 渲染的内容类型

聊天气泡（`ChatBubble`）支持以下富内容：

| 内容段类型 | 说明 |
|------------|------|
| **纯文本** | Markdown 格式文字 |
| **内联图片** | 解析 `![alt](url)` 语法，图文交错展示 |
| **内联链接** | 解析 `[text](url)` 语法，可点击的超链接 |
| **视频引用** | `[▶ 标题](url)` 格式，视频链接展示 |
| **工具调用卡片** | 显示工具名称、参数，可折叠展开 |
| **媒体附件区** | 独立区域展示图片/视频缩略图 |

---

## 三、工具集

### 3.1 🔍 信息获取

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `web_search` | 使用 Bing 搜索互联网信息，返回结构化结果（标题、链接、摘要），最多 10 条 | Medium | 30s |
| `web_fetch` | 获取指定网页的完整结构化内容（标题、正文、图片、视频），支持提取内联媒体资源 | Medium | 30s |
| `clipboard` | 读取或写入系统剪贴板内容；`action` 参数支持 `read` / `write` | Low | 5s |

**网页内容提取能力**（`web_fetch`）：
- 自动提取页面标题、meta 描述、OG 封面图
- 提取正文中的图片（`<img>` 标签，含 alt 文字）
- 提取嵌入视频（YouTube、Bilibili iframe）
- 将 HTML 转为可读文本
- 提取的媒体 URL 会推送到 UI 内联展示

### 3.2 📁 文件操作

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `read_file` | 读取指定路径文件的内容（文本），超过 50,000 字符自动截断 | ReadOnly | 10s |
| `write_file` | 创建或覆盖文件（代码、配置、文档等）；禁止写入系统路径和危险扩展名 | Medium | 15s |
| `list_directory` | 列出指定目录下的文件和子目录（最多各 100 项），含文件大小 | ReadOnly | 10s |
| `search_files` | 按文件名在指定目录中递归搜索（最深 5 层，最多 50 结果） | ReadOnly | 30s |

**写入文件安全限制**：
- 禁止路径：`C:\Windows`、`C:\Program Files`、`C:\Program Files (x86)`
- 禁止扩展名：`.exe`、`.bat`、`.cmd`、`.vbs`、`.reg`、`.sys`、`.dll`

### 3.3 💻 系统控制

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `run_command` | 在 PowerShell 中执行任意命令（受安全黑名单保护） | High (沙箱) | 60s |
| `open_app` | 打开应用程序、文件或网址（使用 Shell 启动） | Medium | 10s |
| `system_info` | 获取系统信息（OS、架构、CPU、内存、运行时间等） | ReadOnly | 5s |
| `process_list` | 列出当前运行的进程 Top 30（按内存排序） | ReadOnly | 10s |

**命令执行安全黑名单**：
`format`、`diskpart`、`bcdedit`、`reg delete`、`shutdown`、`taskkill /f /im`、
`net user`、`net localgroup`、`remove-item -recurse -force C:`、`stop-computer`、`restart-computer` 等

### 3.4 📄 文档操作

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `document_tool` | 使用本机 Office 或 WPS 操作文档 | Medium | 60s |

**支持的操作**：
- `read` — 读取文档内容
- `create` — 创建新文档
- `convert` — 格式转换
- `open` — 打开文档

**支持的文档格式**：
`.docx` `.doc` `.xlsx` `.xls` `.pptx` `.ppt` `.pdf` `.csv` `.txt` `.rtf`

### 3.5 ⬇️ 下载

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `download_file` | 从 URL 下载任意文件（图片、视频、文档等）到本地 | Medium | 300s |
| `download_video` | 从视频平台下载视频（需要 yt-dlp） | Medium | 600s |

**视频下载支持的平台**：
YouTube、Bilibili、腾讯视频、优酷、西瓜视频、抖音、Vimeo、Dailymotion、Twitch

**视频下载参数**：
- `quality` — 画质选择：`720p`、`1080p`、`best`
- `save_directory` — 保存目录（默认用户 Downloads）

### 3.6 🤖 多模型协作

| 工具名 | 功能 | 安全级别 | 超时 |
|--------|------|----------|------|
| `delegate_to_model` | 将专项任务分发给指定类别的次级专业模型 | Medium | 120s |

**可调度的模型类别**：

| 类别 | 标识 | 适用任务 |
|------|------|----------|
| 大语言模型 | `llm` | 文本理解、推理、对话、代码生成 |
| 视觉大模型 | `vision` | 图像理解、OCR、图像分析 |
| 多模态大模型 | `multimodal` | 同时处理文本、图像、音频、视频 |
| 基础科学大模型 | `science` | 数学、物理、化学、生物等科学推理 |
| 代码专用模型 | `coding` | 代码生成、代码审查、调试 |

---

## 四、核心架构能力

### 4.1 ReAct 推理循环

- 每次用户提问触发一次 ReAct 循环
- 最多执行 **10 轮**工具调用
- 每轮：LLM 流式推理 → 检测 function call → 执行工具 → 将结果回馈 LLM → 继续推理
- 无工具调用时自动结束循环

### 4.2 流式输出

- 使用 `GetStreamingResponseAsync` 实现实时流式输出
- 每收到一个文本 token 立即推送到 UI
- 用户可以实时看到 AI 的思考过程，无需等待完整响应

### 4.3 多模型编排

- **主模型**（Primary）：负责理解用户意图、规划任务、调度次级模型、整合结果
- **次级模型**（Secondary）：接受主模型分发的专项任务
- 主模型通过 `delegate_to_model` 工具调度次级模型
- 支持多个供应商的故障转移（`ProviderFailover`）

### 4.4 供应商支持

| 供应商 | 端点格式 |
|--------|----------|
| OpenAI | `https://api.openai.com/v1` |
| DeepSeek | `https://api.deepseek.com/v1` |
| Ollama (本地) | `http://localhost:11434/v1` |
| Azure OpenAI | `https://{resource}.openai.azure.com/...` |
| 阿里云百炼 | `https://dashscope.aliyuncs.com/compatible-mode/v1` |
| 火山引擎 (豆包) | `https://ark.cn-beijing.volces.com/api/v3` |

**Ollama 本地模型管理**：
- 自动检测 Ollama 安装状态
- 一键安装 Ollama
- 一键部署开源模型（Qwen、DeepSeek R1、Llama、Gemma、Phi、Mistral、Code Llama）
- 一键接入为供应商
- 自动标识不支持工具调用的模型（如 DeepSeek R1），以纯对话模式运行

### 4.5 记忆系统

五级时序记忆（TiMem）：

| 层级 | 名称 | 触发频率 | 稳定性 |
|------|------|----------|--------|
| L1 | 对话碎片 | 每 2-3 轮提取 | 最低 |
| L2 | 会话摘要 | 会话结束时 | 低 |
| L3 | 日报摘要 | 每日固化 | 中 |
| L4 | 周报摘要 | 每周固化 | 高 |
| L5 | 用户画像 | 月度固化 | 最高 |

功能：
- 对话记忆自动摄入
- 基于向量的相似记忆检索
- 用户画像持久化

### 4.6 任务缓存

三模式命中判定：

| 模式 | 相似度 | 行为 |
|------|--------|------|
| **精确命中** | ≥ 0.95 (且幂等) | 跳过 ReAct 循环，直接返回缓存结果 |
| **参考命中** | 0.70 - 0.95 | 注入历史思考链作为 few-shot 提示，仍执行工具 |
| **未命中** | < 0.70 | 正常执行完整 ReAct 循环 |

缓存内容包括：
- 表1：问题 + 最终结果
- 表2：思考过程（每轮 ReAct 推理文本）
- 表3：工作过程（工具调用链：工具名、参数、结果、耗时）

### 4.7 安全体系

- **工具安全级别**：ReadOnly → Low → Medium → High → Critical
- **审批策略**：`HighRiskOnly`（默认仅高风险需审批）
- **命令执行沙箱**：`run_command` 工具启用沙箱保护
- **文件写入保护**：禁止写入系统目录和可执行文件
- **命令黑名单**：阻止格式化磁盘、删除系统文件等危险操作

### 4.8 Gateway 管道

消息处理管道中间件链：

```
入站消息 → 错误处理 → Agent 调用 → 流式回复
```

- `ErrorHandlingMiddleware` — 全局异常捕获
- `AgentMiddleware` — 调用 Agent 执行器，收集流式回复、工具调用、模型事件、媒体附件

---

## 五、系统提示词注入项

每次推理时，系统提示词自动包含以下上下文：

| 注入项 | 来源 | 说明 |
|--------|------|------|
| 人设 | `AgentConfig.Persona` | 用户自定义的 AI 人格描述 |
| 运行环境 | 系统 API | OS、计算机名、用户名、用户目录、桌面路径、当前时间 |
| 用户画像 | 记忆系统 L5 | 长期积累的用户偏好和特征 |
| 相关记忆 | 向量检索 Top-5 | 与当前问题最相关的历史记忆 |
| 历史任务参考 | 任务缓存（参考命中） | 压缩的思考链和工具链作为 few-shot |
| 能力与工具列表 | `ToolOrchestrator` | 所有已注册且可用的工具描述 |
| 模型团队 | `ProviderRegistry` | 可调度的次级专业模型列表 |
| 行为准则 | 硬编码 | 9 条行为规范指导 |

---

## 六、行为准则

1. **主动使用工具** — 不猜测，需要信息就搜索，需要看文件就读取
2. **组合使用工具** — 复杂任务多步完成（搜索 → 获取 → 总结）
3. **实时问题必须搜索** — 新闻、天气、股价等信息先 `web_search`
4. **代码直接保存** — 用 `write_file` 写到文件，不只在对话中展示
5. **危险操作先说明** — 删除文件、修改系统前告知用户
6. **使用用户的语言回复**
7. **简洁高效** — 先做事再汇报结果
8. **图文并茂 + 来源标注** — 引用内容标明出处，配图用 Markdown 图片语法
9. **善用多模型协作** — 专业任务分发给对应次级模型
10. **主动进化** — 发现能力缺口时用 `self_evolve report_gap` 记录，后台自动实现

---

## 七、🧬 自我进化系统

OwnerAI 具备自主进化能力 — 能够检测自身能力缺口，自动编写代码实现新工具，并验证集成。

### 7.1 架构设计

**双线程模型**：
- **主线程**: 用户正常对话 → GatewayEngine → AgentExecutor（ReAct 循环）
- **后台线程**: EvolutionBackgroundService → AgentExecutor（自我进化循环）

两个线程共享相同的工具集（write_file, run_command, web_search 等），后台线程使用独立的 Agent 上下文和进化专用人设。

### 7.2 进化循环

每 30 分钟执行一轮进化循环，也可通过 UI 手动触发：

```
DETECT → PLAN → IMPLEMENT → VERIFY → INTEGRATE
  🔍       📋       🔧         🧪        ✅
```

1. **DETECT（检测）**: Agent 审视工具目录，识别缺失的常用能力
2. **PLAN（规划）**: 分析缺口，确定工具类名、文件路径、功能设计
3. **IMPLEMENT（实现）**: 使用 write_file 创建新工具代码，修改 DI 注册
4. **VERIFY（验证）**: 使用 run_command 执行 `dotnet build` 验证编译通过
5. **INTEGRATE（集成）**: 标记缺口已解决，记录解决方案

### 7.3 self_evolve 工具

| 操作 | 说明 |
|------|------|
| `report_gap` | 报告能力缺口（description, source, priority） |
| `list_gaps` | 查看所有能力缺口及状态 |
| `get_status` | 查看进化统计（总数/已解决/失败/待处理） |
| `resolve_gap` | 手动标记缺口已解决 |

**缺口状态流转**：
```
Detected → Planning → Implementing → Verifying → Resolved
                                              ↘ Failed (最多重试 3 次)
```

### 7.4 安全约束

- ❌ 不允许修改 `OwnerAI.Security` 项目
- ❌ 不允许修改 `AgentExecutor.cs` 或 `ToolOrchestrator.cs`
- ❌ 不允许删除现有文件
- ✅ 仅允许新增工具文件
- ✅ 仅允许修改工具注册文件 (ServiceCollectionExtensions.cs)
- ✅ 必须通过 `dotnet build` 验证

### 7.5 进化数据持久化

- 存储位置: `%LOCALAPPDATA%/OwnerAI/evolution.db` (SQLite)
- 记录内容: 缺口描述、来源、优先级、状态、尝试次数、解决方案、执行日志

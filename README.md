# OwnerAI
一款开箱即用的本地AI助手，.Net 10框架，AI设计，AI编码

## 功能概览

OwnerAI 是一个可接入各大模型平台（阿里云百炼、OpenAI 等 OpenAI 兼容接口）的本地 AI 助手，具备以下内置工具能力：

### 🔍 信息获取（3 个）
| 工具名 | 参数 | 功能 |
|--------|------|------|
| `web_search` | `query` | 使用 Bing 搜索，返回标题/链接/摘要 |
| `web_fetch` | `Url` | 获取网页结构化内容（标题、正文、图片、视频） |
| `clipboard` | `Action`, `Content` | 读写系统剪贴板 |

### 📁 文件操作（4 个）
| 工具名 | 参数 | 功能 |
|--------|------|------|
| `read_file` | `path` | 读取文件内容 |
| `write_file` | `path`, `Content` | 创建/覆盖文件 |
| `list_directory` | `path` | 列出目录下的文件和子目录 |
| `search_files` | `query`, `directory` | 按名称搜索文件 |

### 💻 系统控制（4 个）
| 工具名 | 参数 | 功能 |
|--------|------|------|
| `run_command` | `command`, `working_directory` | PowerShell/Shell 执行命令（有安全黑名单） |
| `open_app` | `target`, `arguments` | 打开应用/文件/网址 |
| `system_info` | （无） | 获取 CPU/内存/磁盘等系统信息 |
| `process_list` | （无） | 列出当前运行的进程 |

### ⬇️ 下载（2 个）
| 工具名 | 参数 | 功能 |
|--------|------|------|
| `download_file` | `Url`, `save_path` | 从 URL 下载图片/文档/任意文件 |
| `download_video` | `Url`, `save_directory`, `quality` | YouTube/Bilibili 等视频下载（依赖 yt-dlp） |

### 🤖 多模型协作（1 个）
| 工具名 | 参数 | 功能 |
|--------|------|------|
| `delegate_to_model` | `Category`, `task`, `system_instruction` | 分发任务给次级专业模型（vision/coding/science/multimodal） |

## 快速开始

### 1. 环境要求
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- （可选）[yt-dlp](https://github.com/yt-dlp/yt-dlp) — 用于视频下载功能

### 2. 配置 API Key

编辑 `src/OwnerAI/appsettings.json`，填入你的大模型 API Key：

```json
{
  "LLM": {
    "BaseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
    "ApiKey": "你的API Key",
    "Model": "qwen-max",
    "MaxTokens": 4096
  }
}
```

也可以通过环境变量设置（推荐，避免泄露）：
```bash
set OWNERAI_LLM__ApiKey=你的API Key        # Windows
export OWNERAI_LLM__ApiKey=你的API Key     # Linux/macOS
```

### 3. 运行

```bash
cd src/OwnerAI
dotnet run
```

或构建后运行：
```bash
dotnet build -c Release
./src/OwnerAI/bin/Release/net10.0/OwnerAI
```

## 支持的模型平台

任何兼容 OpenAI Chat Completions API（支持 Function Calling/Tool Use）的平台均可使用，包括：

- **阿里云百炼**（已测试）：`https://dashscope.aliyuncs.com/compatible-mode/v1`
- **OpenAI**：`https://api.openai.com/v1`
- **其他兼容平台**：修改 `appsettings.json` 中的 `BaseUrl` 和 `Model` 即可

## 内置命令

| 命令 | 说明 |
|------|------|
| `/clear` | 清空对话历史 |
| `/tools` | 列出所有可用工具 |
| `/help` | 显示帮助信息 |
| `/exit` | 退出程序 |
| `Ctrl+C` | 中断当前请求 |

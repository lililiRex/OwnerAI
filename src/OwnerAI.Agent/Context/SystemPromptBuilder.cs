using OwnerAI.Agent.Providers;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Context;

/// <summary>
/// 系统提示词构建器
/// </summary>
public sealed class SystemPromptBuilder
{
    private string _persona = "你是一个高效、专业的个人 AI 助手。";
    private MemoryEntry? _userProfile;
    private IReadOnlyList<MemorySearchResult>? _memories;
    private IReadOnlyList<string>? _toolDescriptions;
    private IReadOnlyList<ModelTeamMember>? _modelTeam;
    private IReadOnlySet<string>? _enabledTools;
    private DateTimeOffset? _dateTime;
    private TaskCacheSearchResult? _taskReference;

    public SystemPromptBuilder WithPersona(string persona)
    {
        _persona = persona;
        return this;
    }

    public SystemPromptBuilder WithUserProfile(MemoryEntry? profile)
    {
        _userProfile = profile;
        return this;
    }

    public SystemPromptBuilder WithRetrievedMemories(IReadOnlyList<MemorySearchResult>? memories)
    {
        _memories = memories;
        return this;
    }

    public SystemPromptBuilder WithTools(IReadOnlyList<string>? toolDescriptions)
    {
        _toolDescriptions = toolDescriptions;
        return this;
    }

    public SystemPromptBuilder WithDateTime(DateTimeOffset dateTime)
    {
        _dateTime = dateTime;
        return this;
    }

    public SystemPromptBuilder WithModelTeam(IReadOnlyList<ModelTeamMember>? team)
    {
        _modelTeam = team;
        return this;
    }

    public SystemPromptBuilder WithEnabledTools(IReadOnlySet<string>? enabledTools)
    {
        _enabledTools = enabledTools;
        return this;
    }

    /// <summary>
    /// 注入参考命中的历史任务 — 压缩思考链作为 few-shot 提示
    /// </summary>
    public SystemPromptBuilder WithTaskReference(TaskCacheSearchResult? taskReference)
    {
        _taskReference = taskReference;
        return this;
    }

    public string Build()
    {
        var builder = new System.Text.StringBuilder();

        // Persona
        builder.AppendLine(_persona);
        builder.AppendLine();

        // 身份与能力总览
        builder.AppendLine("## 你是谁");
        builder.AppendLine("你是 OwnerAI — 运行在用户 Windows 电脑上的私人 AI 助手。");
        builder.AppendLine("你不仅能对话，还能**直接操控用户的电脑**来完成任务。");
        builder.AppendLine("你通过 function calling 调用工具，工具会在用户电脑上真实执行并返回结果。");
        builder.AppendLine();

        // 运行环境
        builder.AppendLine("## 运行环境");
        builder.Append("- 操作系统: ").AppendLine(System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        builder.Append("- 计算机名: ").AppendLine(Environment.MachineName);
        builder.Append("- 当前用户: ").AppendLine(Environment.UserName);
        builder.Append("- 用户目录: ").AppendLine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        builder.Append("- 桌面路径: ").AppendLine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        if (_dateTime.HasValue)
        {
            builder.Append("- 当前时间: ").AppendLine(_dateTime.Value.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture));
        }
        builder.AppendLine();

        // 用户画像
        if (_userProfile is not null)
        {
            builder.AppendLine("## 用户画像");
            builder.AppendLine(_userProfile.Content);
            builder.AppendLine();
        }

        // 相关记忆
        if (_memories is { Count: > 0 })
        {
            builder.AppendLine("## 相关记忆 (检索结果)");
            foreach (var memory in _memories)
            {
                builder.Append("- [").Append(memory.Entry.Level).Append("] ").AppendLine(memory.Entry.Content);
            }
            builder.AppendLine();
        }

        // 历史任务参考 (参考命中) — 压缩思考链作为 few-shot
        if (_taskReference is { HitMode: TaskCacheHitMode.ReferenceHit })
        {
            builder.AppendLine("## 历史相似任务参考 (few-shot)");
            builder.Append("以下是你之前处理过的相似任务（相似度: ")
                   .Append(_taskReference.Score.ToString("F2"))
                   .AppendLine("），仅供参考，当前任务参数可能不同，请根据实际情况调整。");
            builder.AppendLine();

            builder.Append("**历史问题**: ").AppendLine(_taskReference.Entry.Query);
            builder.Append("**历史结果**: ").AppendLine(_taskReference.Entry.Result.Length > 200
                ? string.Concat(_taskReference.Entry.Result.AsSpan(0, 200), "...")
                : _taskReference.Entry.Result);

            // 压缩思考链为 1-2 句概要
            if (_taskReference.Entry.ThinkingSteps is { Count: > 0 })
            {
                builder.Append("**思考路径**: ");
                var steps = _taskReference.Entry.ThinkingSteps;
                // 取第一步和最后一步作为概要
                var firstStep = Truncate(steps[0].Reasoning, 100);
                if (steps.Count > 1)
                {
                    var lastStep = Truncate(steps[^1].Reasoning, 100);
                    builder.Append(firstStep).Append(" → ... → ").AppendLine(lastStep);
                }
                else
                {
                    builder.AppendLine(firstStep);
                }
            }

            // 工具调用链概要
            if (_taskReference.Entry.WorkSteps is { Count: > 0 })
            {
                builder.Append("**工具链**: ");
                builder.AppendJoin(" → ", _taskReference.Entry.WorkSteps.Select(w => w.ToolName));
                builder.AppendLine();
            }

            builder.AppendLine();
        }

        // 能力与工具 — 按类别详细描述（仅输出已启用的工具）
        builder.AppendLine("## 你的能力（通过工具实现）");
        builder.AppendLine();

        // 🔍 信息获取
        if (HasAnyEnabled("web_search", "web_fetch", "clipboard"))
        {
            builder.AppendLine("### 🔍 信息获取");
            if (IsToolEnabled("web_search"))
                builder.AppendLine("- **web_search**: 使用 Bing 搜索互联网信息。需要查询实时新闻、技术资料、任何你不确定的知识时，**必须主动搜索**。");
            if (IsToolEnabled("web_fetch"))
                builder.AppendLine("- **web_fetch**: 获取指定网页的完整内容（标题、正文、图片、视频）。拿到搜索结果后，用此工具深入阅读。");
            if (IsToolEnabled("clipboard"))
                builder.AppendLine("- **clipboard**: 读取用户剪贴板内容。用户说「看看我复制的内容」时使用。");
            builder.AppendLine();
        }

        // 📁 文件操作
        if (HasAnyEnabled("read_file", "write_file", "list_directory", "search_files"))
        {
            builder.AppendLine("### 📁 文件操作");
            if (IsToolEnabled("read_file"))
                builder.AppendLine("- **read_file**: 读取文件内容。可以读代码、配置、日志等。");
            if (IsToolEnabled("write_file"))
                builder.AppendLine("- **write_file**: 创建或覆盖文件。可以写代码、生成报告、保存配置。");
            if (IsToolEnabled("list_directory"))
                builder.AppendLine("- **list_directory**: 列出目录内容。了解文件夹结构。");
            if (IsToolEnabled("search_files"))
                builder.AppendLine("- **search_files**: 按名称搜索文件。");
            builder.AppendLine();
        }

        // 💻 系统控制
        if (HasAnyEnabled("run_command", "open_app", "system_info", "process_list"))
        {
            builder.AppendLine("### 💻 系统控制");
            if (IsToolEnabled("run_command"))
                builder.AppendLine("- **run_command**: 在 PowerShell 中执行**任意命令**。这是最强大的工具 — 可以运行程序、编译代码、管理服务、操作 Git 等一切命令行能做的事。");
            if (IsToolEnabled("open_app"))
                builder.AppendLine("- **open_app**: 打开应用程序、文件或网址（例如用 VS Code 打开项目，用浏览器打开链接）。");
            if (IsToolEnabled("system_info"))
                builder.AppendLine("- **system_info**: 获取系统信息（CPU、内存、磁盘等）。");
            if (IsToolEnabled("process_list"))
                builder.AppendLine("- **process_list**: 查看正在运行的进程。");
            builder.AppendLine();
        }

        // ⬇️ 下载
        if (HasAnyEnabled("download_file", "download_video"))
        {
            builder.AppendLine("### ⬇️ 下载");
            if (IsToolEnabled("download_file"))
                builder.AppendLine("- **download_file**: 从 URL 下载图片、文档等任意文件到本地。");
            if (IsToolEnabled("download_video"))
                builder.AppendLine("- **download_video**: 从 YouTube、Bilibili 等平台下载视频（需要 yt-dlp）。支持选择画质和仅提取音频。");
            builder.AppendLine();
        }

        // 🤖 多模型协作
        if (IsToolEnabled("delegate_to_model"))
        {
            builder.AppendLine("### 🤖 多模型协作");
            builder.AppendLine("- **delegate_to_model**: 将专项任务分发给其他类别的 AI 模型。你是主模型，负责理解用户意图、规划任务、调度专业模型、整合结果。");
            builder.AppendLine("  可选类别: llm(对话), coding(代码), reasoning(推理), vision(图像识别), imagegen(文生图), i2v(图生视频), t2v(文生视频), audio(语音), translation(翻译), writing(写作), dataanalysis(数据分析), embedding(嵌入), science(科学), multimodal(多模态)");
            builder.AppendLine("  参数: category(必填), task(必填), system_instruction(可选), image_urls(可选，字符串数组，指定图片文件路径或URL)");
            builder.AppendLine("  **重要**: 当用户发送了图片/视频附件时，系统会自动将附件转发给 vision/multimodal/i2v 等需要媒体的次级模型，无需手动指定 image_urls。");

            // 动态输出当前配置的模型团队
            if (_modelTeam is { Count: > 0 })
            {
                var secondaryModels = _modelTeam.Where(m => m.Role == ModelRole.Secondary).ToList();
                if (secondaryModels.Count > 0)
                {
                    builder.AppendLine();
                    builder.AppendLine("  **当前可调度的次级专业模型：**");
                    foreach (var m in secondaryModels)
                    {
                        var catLabels = m.Categories.Select(c => c switch
                        {
                            ModelCategory.LLM => "🗣️ 语言",
                            ModelCategory.Vision => "👁️ 视觉/图像识别",
                            ModelCategory.Coding => "💻 代码",
                            ModelCategory.Reasoning => "🧠 推理",
                            ModelCategory.ImageGen => "🎨 文生图",
                            ModelCategory.ImageToVideo => "🎬 图生视频",
                            ModelCategory.TextToVideo => "📹 文生视频",
                            ModelCategory.Audio => "🔊 语音",
                            ModelCategory.Translation => "🌐 翻译",
                            ModelCategory.Writing => "✍️ 写作",
                            ModelCategory.DataAnalysis => "📊 数据分析",
                            ModelCategory.Embedding => "📐 嵌入",
                            ModelCategory.Multimodal => "🎭 多模态",
                            ModelCategory.Science => "🔬 科学",
                            _ => c.ToString(),
                        });
                        var categoriesStr = string.Join("+", m.Categories.Select(c => c.ToString().ToLowerInvariant()));
                        var labelStr = string.Join("+", catLabels);
                        builder.Append("  - `").Append(categoriesStr)
                               .Append("` → ").Append(labelStr).Append(" (").Append(m.Name).AppendLine(")");
                    }
                    builder.AppendLine("  - 用法: 分析需求 → 选择合适类别 → delegate_to_model 分发 → 整合结果回复用户");
                }
                else
                {
                    builder.AppendLine("  - 当前未配置次级模型。如需多模型协作，请在设置中添加「📎 次级模型」。");
                }
            }
            else
            {
                builder.AppendLine("  - 当前未配置模型团队。所有任务由你独立完成。");
            }
            builder.AppendLine();
        }

        // 🧬 自我进化
        if (IsToolEnabled("self_evolve"))
        {
            builder.AppendLine("### 🧬 自我进化");
            builder.AppendLine("- **self_evolve**: 自我进化工具。当你发现无法完成某项任务、或识别到系统缺少某种能力时，主动调用此工具记录能力缺口。");
            builder.AppendLine("  可用操作:");
            builder.AppendLine("  - `report_gap`: 报告能力缺口（需提供 description, 可选 source/priority）");
            builder.AppendLine("  - `list_gaps`: 查看当前所有能力缺口及状态");
            builder.AppendLine("  - `get_status`: 查看自我进化整体统计");
            builder.AppendLine("  - `resolve_gap`: 手动标记某个缺口已解决");
            builder.AppendLine("  **自动进化**: 后台调度器会定期执行进化任务 — 自动拾取缺口 → 规划方案 → 编写代码 → 编译验证 → 集成。你只需报告缺口，系统会自主实现。");
            builder.AppendLine();
        }

        // ⏰ 计划任务
        if (IsToolEnabled("schedule_task"))
        {
            builder.AppendLine("### ⏰ 计划任务");
            builder.AppendLine("- **schedule_task**: 计划任务工具。用户说「每隔 X 分钟做一件事」「过一小时帮我做」「定时执行」「每天几点做」等场景时使用。");
            builder.AppendLine("  可用操作:");
            builder.AppendLine("  - `create`: 创建任务（需提供 name, message; 可选 type=once/recurring/cron, delay_minutes, interval_minutes, cron_expression, priority, description）");
            builder.AppendLine("  - `list`: 查看任务列表（可选 status/source 过滤）");
            builder.AppendLine("  - `cancel`: 取消任务（需提供 task_id）");
            builder.AppendLine("  - `pause`: 暂停循环任务（需提供 task_id）");
            builder.AppendLine("  - `resume`: 恢复已暂停的任务（需提供 task_id）");
            builder.AppendLine("  - `status`: 查看调度器整体状态");
            builder.AppendLine("  - `history`: 查看任务执行历史（可选 task_id 过滤, limit 限制数量）");
            builder.AppendLine("  **Cron 定时**: type=\"cron\" 时需提供 cron_expression，格式为 5 段: \"分 时 日 月 周\"。");
            builder.AppendLine("  示例: \"0 9 * * *\"(每天9:00), \"*/30 * * * *\"(每30分钟), \"0 8 * * 1-5\"(工作日8:00)");
            builder.AppendLine("  **调度规则**: 后台任务只在用户空闲时执行（2 分钟无对话），LLM 互斥锁保证不会影响用户对话体验。");
            builder.AppendLine();
        }

        // 🧩 外部技能 (OpenClaw)
        if (IsToolEnabled("openclaw_skill"))
        {
            builder.AppendLine("### 🧩 外部技能 (OpenClaw)");
            builder.AppendLine("- **openclaw_skill**: 管理和执行 OpenClaw 格式的外部技能。OpenClaw 技能是社区共享的知识和脚本包。");
            builder.AppendLine("  可用操作:");
            builder.AppendLine("  - `list_skills`: 列出所有已安装的 OpenClaw 技能");
            builder.AppendLine("  - `read_skill`: 读取技能完整指南（需提供 skill_name），用于获取技能的使用方法和最佳实践");
            builder.AppendLine("  - `log_learning`: 记录学习日志到 .learnings/ 目录（需提供 type=learning/error/feature_request, content）");
            builder.AppendLine("  - `run_script`: 执行技能附带的脚本（需提供 skill_name, script，可选 arguments）");
            builder.AppendLine("  **自我改进**: 当你遇到非显而易见的问题、被用户纠正、或发现更优方案时，使用 log_learning 记录。这些记录会在后续会话中被检索和利用。");
            builder.AppendLine();
        }

        // 工具名列表（供 function calling 参考）
        if (_toolDescriptions is { Count: > 0 })
        {
            builder.AppendLine("### 已注册工具清单");
            foreach (var tool in _toolDescriptions)
            {
                builder.Append("- ").AppendLine(tool);
            }
            builder.AppendLine();
        }

        // 行为准则
        builder.AppendLine("## 行为准则");
        builder.AppendLine("1. **主动使用工具**：不要猜测或编造信息。需要查资料就搜索，需要看文件就读取，需要执行就运行命令。");
        builder.AppendLine("2. **组合使用工具**：复杂任务可以多步完成。例如：搜索 → 获取网页 → 总结；或：列目录 → 读文件 → 修改 → 写回。");
        builder.AppendLine("3. **遇到实时问题必须搜索**：任何关于新闻、天气、股价、赛事等实时信息，必须先用 web_search 搜索。");
        builder.AppendLine("4. **编写代码时直接保存**：用户要你写代码，用 write_file 保存到文件，而不是只在对话中展示。");
        builder.AppendLine("5. **危险操作先说明**：删除文件、修改系统配置等操作前，先告知用户影响范围。");
        builder.AppendLine("6. **回复使用用户的语言**。");
        builder.AppendLine("7. **简洁高效**：先做事再汇报结果，不要长篇大论地描述计划。");
        builder.AppendLine("8. **图文并茂 + 来源标注**：在回答包含网页内容或新闻时，必须遵循以下格式：");
        builder.AppendLine("   - 首先，**必须**输出你总结或提取的**文字正文**，为你找到的信息提供详尽的说明。");
        builder.AppendLine("   - 每次引用内容前，**单独首行**标明出处，格式为 `[来源: 网站名](URL)`");
        builder.AppendLine("   - 若网页中有配图，你需要挑选最相关的部分，使用 `![描述](URL)` 嵌入到你刚刚输出的对应**文字段落之后**");
        builder.AppendLine("   - 提及视频时使用 `[▶ 视频标题](URL)` 格式单独一行引用");
        builder.AppendLine("   - **切忌只输出一堆 URL 或图片链接**，必须有结构化的文字解读，使得信息对用户一目了然！");

        // 仅在有次级模型时提示多模型协作
        if (_modelTeam is { Count: > 0 } && _modelTeam.Any(m => m.Role == ModelRole.Secondary))
        {
            builder.AppendLine("9. **善用多模型协作**：遇到代码生成、复杂推理、图片识别、文生图、图生视频、文生视频、翻译、写作、数据分析等专业任务时，用 delegate_to_model 分发给对应类别的专业模型，而不是自己勉强做。");
        }

        builder.AppendLine("10. **主动进化**：当你发现自己缺少某种能力时（如找不到合适的工具），用 self_evolve report_gap 记录缺口。后台进化线程会自动实现新工具。");

        return builder.ToString();
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    /// <summary>指定工具是否已启用（未设置 _enabledTools 时视为全部启用）</summary>
    private bool IsToolEnabled(string toolName)
        => _enabledTools is null || _enabledTools.Contains(toolName);

    /// <summary>指定工具名中是否有任一已启用</summary>
    private bool HasAnyEnabled(params string[] toolNames)
        => _enabledTools is null || toolNames.Any(_enabledTools.Contains);
}

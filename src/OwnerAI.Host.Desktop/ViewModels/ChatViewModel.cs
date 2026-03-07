using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.UI.Dispatching;
using OwnerAI.Gateway;
using OwnerAI.Gateway.Sessions;
using OwnerAI.Host.Desktop.Models;
using OwnerAI.Configuration;
using OwnerAI.Shared;
using OwnerAI.Shared.Abstractions;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OwnerAI.Host.Desktop.ViewModels;

/// <summary>
/// 聊天页面 ViewModel — 消息发送、流式接收、工具调用展示、附件管理
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly GatewayEngine _gateway;
    private readonly ILogger<ChatViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly string _chatAgentName;
    private readonly string _evolutionAgentName;
    private CancellationTokenSource? _currentCts;

    /// <summary>后台任务活跃气泡 — 按 TaskId 跟踪，用于合并 Progress 到同一气泡</summary>
    private readonly Dictionary<string, ChatBubble> _activeTaskBubbles = [];

    public ObservableCollection<ChatBubble> Messages { get; } = [];

    /// <summary>通知视图滚动到底部</summary>
    public event Action? ScrollToBottomRequested;

    /// <summary>待发送的附件列表</summary>
    public ObservableCollection<AttachmentDisplay> PendingAttachments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string InputText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProcessing { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool HasPendingAttachments { get; set; }

    // ── 自我进化状态 ──

    [ObservableProperty]
    public partial bool IsEvolutionActive { get; set; }

    [ObservableProperty]
    public partial string EvolutionPhase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EvolutionSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowEvolutionIndicator { get; set; }

    public string ChatAgentDisplayName => _chatAgentName;
    public string EvolutionAgentDisplayName => _evolutionAgentName;

    private IDisposable? _evolutionSubscription;
    private IDisposable? _backgroundTaskChatSubscription;

    public ChatViewModel(
        GatewayEngine gateway,
        ConversationHistory conversationHistory,
        IOptions<OwnerAIConfig> ownerAiOptions,
        ILogger<ChatViewModel> logger)
    {
        _gateway = gateway;
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _chatAgentName = ownerAiOptions.Value.ChatAgent.DisplayName;
        _evolutionAgentName = ownerAiOptions.Value.EvolutionAgent.DisplayName;
        PendingAttachments.CollectionChanged += (_, _) =>
            HasPendingAttachments = PendingAttachments.Count > 0;

        // 恢复上次的最近聊天记录
        LoadRecentHistory(conversationHistory);

        // 订阅进化状态事件
        SubscribeEvolutionEvents();

        // 订阅后台任务聊天汇报事件
        SubscribeBackgroundTaskChatEvents();
    }

    /// <summary>
    /// 从 SQLite 加载最近 10 条聊天记录并显示为历史气泡
    /// </summary>
    private void LoadRecentHistory(ConversationHistory conversationHistory)
    {
        try
        {
            var recent = conversationHistory.GetRecentMessages(count: 10);
            if (recent.Count == 0) return;

            foreach (var (role, content, createdAt) in recent)
            {
                var chatRole = role switch
                {
                    "user" => ChatRole.User,
                    "assistant" => ChatRole.Assistant,
                    _ => ChatRole.Assistant,
                };

                var bubble = new ChatBubble
                {
                    Id = Ulid.NewUlid().ToString(),
                    Role = chatRole,
                    Text = content,
                    Timestamp = createdAt,
                    ModelName = chatRole == ChatRole.Assistant ? "OwnerAI" : null,
                    ModelGlyph = chatRole == ChatRole.Assistant ? "\uE99A" : "\uE77B",
                };
                Messages.Add(bubble);
            }

            _logger.LogInformation("[Chat] 已恢复 {Count} 条历史聊天记录", recent.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] 恢复历史聊天记录失败");
        }
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(InputText) && !IsProcessing;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        InputText = string.Empty;

        // 收集待发送附件
        var attachments = PendingAttachments.Count > 0
            ? PendingAttachments.ToList()
            : null;
        PendingAttachments.Clear();

        // 添加用户消息气泡
        var userBubble = new ChatBubble
        {
            Id = Ulid.NewUlid().ToString(),
            Role = ChatRole.User,
            Text = text,
        };

        // 附加用户附件到气泡
        if (attachments is not null)
        {
            foreach (var att in attachments)
                userBubble.Attachments.Add(att);
        }

        Messages.Add(userBubble);

        // 添加 AI 回复占位气泡
        var assistantBubble = new ChatBubble
        {
            Id = Ulid.NewUlid().ToString(),
            Role = ChatRole.Assistant,
            ModelName = _chatAgentName,
            ModelGlyph = "\uE99A",
            IsStreaming = true,
        };
        Messages.Add(assistantBubble);

        IsProcessing = true;
        StatusText = "思考中...";

        _currentCts?.Cancel();
        _currentCts = new CancellationTokenSource();

        // 跟踪当前次级模型气泡（用于流式填充回复）
        ChatBubble? currentSecondaryBubble = null;
        // 工具调用独立气泡
        ChatBubble? toolCallsBubble = null;

        try
        {
            // 将附件信息附加到消息文本中供 Agent 识别
            var messageText = text;
            List<MediaAttachment>? mediaAttachments = null;

            if (attachments is { Count: > 0 })
            {
                mediaAttachments = attachments.Select(a => new MediaAttachment
                {
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    Url = a.FilePath,
                    Size = a.Size,
                }).ToList();

                var fileList = string.Join(", ", attachments.Select(a => a.FileName));
                messageText = $"{text}\n\n[用户附加了以下文件: {fileList}]";
            }

            var message = new InboundMessage
            {
                Id = userBubble.Id,
                ChannelId = "desktop",
                SenderId = "owner",
                SenderName = Environment.UserName,
                Text = messageText,
                Attachments = mediaAttachments,
            };

            var streamedText = new StringBuilder();

            var reply = await _gateway.ProcessMessageAsync(
                message,
                onStreamChunk: chunk =>
                {
                    streamedText.Append(chunk);
                    _dispatcher.TryEnqueue(() =>
                    {
                        assistantBubble.Text = streamedText.ToString();
                        StatusText = "回复中...";
                        ScrollToBottomRequested?.Invoke();
                    });
                },
                onModelEvent: evt =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (evt.IsRequest)
                        {
                            // 分发指令气泡: [@模型名, 任务]
                            var delegationBubble = new ChatBubble
                            {
                                Id = Ulid.NewUlid().ToString(),
                                Role = ChatRole.Delegation,
                                ModelName = "📤 分发指令",
                                ModelGlyph = "\uE724",
                                Text = $"[@{evt.ModelName}, {Truncate(evt.Task, 200)}]",
                            };
                            Messages.Add(delegationBubble);
                            StatusText = $"正在调度 {evt.ModelName} ...";
                        }
                        else
                        {
                            // 次级模型回复气泡
                            var glyph = GetCategoryGlyph(evt.Category);
                            currentSecondaryBubble = new ChatBubble
                            {
                                Id = Ulid.NewUlid().ToString(),
                                Role = ChatRole.SecondaryModel,
                                ModelName = evt.ModelName,
                                ModelGlyph = glyph,
                                Text = evt.Response ?? "(无回复)",
                            };
                            Messages.Add(currentSecondaryBubble);
                            StatusText = "回复中...";
                        }
                    });
                },
                onToolCall: call =>
                    {
                        _dispatcher.TryEnqueue(() =>
                        {
                            if (toolCallsBubble is null)
                            {
                                toolCallsBubble = new ChatBubble
                                {
                                    Id = Ulid.NewUlid().ToString(),
                                    Role = ChatRole.Assistant,
                                    ModelName = $"{_chatAgentName} · 工具调用",
                                    ModelGlyph = "\uE90F",
                                    IsStreaming = true,
                                };
                                var idx = Messages.IndexOf(assistantBubble);
                                Messages.Insert(idx, toolCallsBubble);
                            }
                            toolCallsBubble.ToolCalls.Add(new ToolCallDisplay
                            {
                                ToolName = call.ToolName,
                                Parameters = call.Parameters,
                                Result = call.Result,
                                Success = call.Success,
                                Duration = call.Duration,
                                IsRunning = true,
                            });
                            StatusText = $"正在执行工具: {call.ToolName} ...";
                        });
                    },
                ct: _currentCts.Token);

            if (reply is not null)
            {
                if (reply.IsError)
                {
                    var detail = FormatProviderError(new InvalidOperationException(reply.Text));
                    assistantBubble.Text = $"❌ {detail}";
                    ErrorMessage = detail;
                    HasError = true;
                    StatusText = "调用失败";
                }
                else
                {
                    // 确保流式文本已同步应用（TryEnqueue 可能尚未执行）
                    if (streamedText.Length > 0)
                    {
                        assistantBubble.Text = streamedText.ToString();
                    }
                    else if (reply.Text is { Length: > 0 })
                    {
                        assistantBubble.Text = reply.Text;
                    }

                    if (reply.ToolCalls is { Count: > 0 })
                    {
                        if (toolCallsBubble is null)
                        {
                            toolCallsBubble = new ChatBubble
                            {
                                Id = Ulid.NewUlid().ToString(),
                                Role = ChatRole.Assistant,
                                ModelName = "工具调用",
                                ModelGlyph = "\uE90F",
                            };
                            var idx = Messages.IndexOf(assistantBubble);
                            Messages.Insert(idx, toolCallsBubble);
                        }

                        for (int i = 0; i < reply.ToolCalls.Count; i++)
                        {
                            var call = reply.ToolCalls[i];
                            if (i < toolCallsBubble.ToolCalls.Count)
                            {
                                var display = toolCallsBubble.ToolCalls[i];
                                display.Result = call.Result;
                                display.Success = call.Success;
                                display.Duration = call.Duration;
                                if (!string.IsNullOrEmpty(call.Parameters))
                                    display.Parameters = call.Parameters;
                                display.IsRunning = false;
                            }
                            else
                            {
                                toolCallsBubble.ToolCalls.Add(new ToolCallDisplay
                                {
                                    ToolName = call.ToolName,
                                    Parameters = call.Parameters,
                                    Result = call.Result,
                                    Success = call.Success,
                                    Duration = call.Duration,
                                    IsRunning = false,
                                });
                            }
                        }
                    }

                    // 处理回复中的媒体附件
                    if (reply.Attachments is { Count: > 0 })
                    {
                        ChatBubble? attachmentsBubble = null;

                        foreach (var att in reply.Attachments)
                        {
                            var kind = ClassifyAttachment(att.ContentType);

                            if (kind is AttachmentKind.Image or AttachmentKind.Video)
                            {
                                // 图片/视频 → 合并到 ContentSegments 中与文字交错显示
                                assistantBubble.ExtractedMedia.Add((
                                    att.Url ?? "",
                                    att.FileName,
                                    kind));
                            }
                            else
                            {
                                // 文档/其他 → 独立附件气泡展示
                                if (attachmentsBubble is null)
                                {
                                    attachmentsBubble = new ChatBubble
                                    {
                                        Id = Ulid.NewUlid().ToString(),
                                        Role = ChatRole.Assistant,
                                        ModelName = $"{_chatAgentName} · 附件",
                                        ModelGlyph = "\uE8A5",
                                    };
                                    Messages.Add(attachmentsBubble);
                                }
                                attachmentsBubble.Attachments.Add(new AttachmentDisplay
                                {
                                    FileName = att.FileName,
                                    FilePath = att.Url ?? "",
                                    ContentType = att.ContentType,
                                    Size = att.Size,
                                    Kind = kind,
                                });
                            }
                        }
                    }

                    // 工具调用已独立显示时，若文本气泡为空则移除
                    if (toolCallsBubble is not null && string.IsNullOrEmpty(assistantBubble.Text))
                    {
                        Messages.Remove(assistantBubble);
                    }
                }
            }
            else
            {
                if (streamedText.Length > 0)
                {
                    assistantBubble.Text = streamedText.ToString();
                }
                else if (toolCallsBubble is not null)
                {
                    Messages.Remove(assistantBubble);
                }
                else
                {
                    assistantBubble.Text = "（无回复）";
                }
            }
        }
        catch (OperationCanceledException)
        {
            assistantBubble.Text = "（已取消）";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending message");
            var detail = ExtractErrorDetail(ex);
            assistantBubble.Text = $"❌ {detail}";
            ErrorMessage = detail;
            HasError = true;
            StatusText = "调用失败";
        }
        finally
        {
            if (toolCallsBubble is not null) toolCallsBubble.IsStreaming = false;
            assistantBubble.IsStreaming = false;
            IsProcessing = false;
            if (!HasError) StatusText = "就绪";
        }
    }

    /// <summary>
    /// 通过文件选择器添加附件
    /// </summary>
    [RelayCommand]
    private async Task PickAttachmentAsync()
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");

            // 获取窗口句柄以初始化 picker
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files is null) return;

            foreach (var file in files)
            {
                var props = await file.GetBasicPropertiesAsync();
                var kind = ClassifyAttachment(file.ContentType);

                PendingAttachments.Add(new AttachmentDisplay
                {
                    FileName = file.Name,
                    FilePath = file.Path,
                    ContentType = file.ContentType,
                    Size = (long)props.Size,
                    Kind = kind,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to pick attachment");
        }
    }

    /// <summary>
    /// 移除待发送附件
    /// </summary>
    [RelayCommand]
    private void RemoveAttachment(AttachmentDisplay? attachment)
    {
        if (attachment is not null)
            PendingAttachments.Remove(attachment);
    }

    [RelayCommand]
    private void Cancel()
    {
        _currentCts?.Cancel();
        IsProcessing = false;
        StatusText = "已取消";
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        PendingAttachments.Clear();
        StatusText = "对话已清空";
    }

    [RelayCommand]
    private void DismissError()
    {
        HasError = false;
        ErrorMessage = string.Empty;
        StatusText = "就绪";
    }

    /// <summary>
    /// 根据 MIME 类型分类附件种类
    /// </summary>
    internal static AttachmentKind ClassifyAttachment(string contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return AttachmentKind.Other;

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return AttachmentKind.Image;

        if (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return AttachmentKind.Video;

        // 文档类型
        return contentType.ToLowerInvariant() switch
        {
            "application/pdf" or
            "application/msword" or
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" or
            "application/vnd.ms-excel" or
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" or
            "application/vnd.ms-powerpoint" or
            "application/vnd.openxmlformats-officedocument.presentationml.presentation" or
            "text/plain" or "text/csv" => AttachmentKind.Document,
            _ => AttachmentKind.Other,
        };
    }

    /// <summary>
    /// 获取模型类别对应的图标字形
    /// </summary>
    private static string GetCategoryGlyph(string category) => category.ToLowerInvariant() switch
    {
        "vision" or "visual" or "image" => "\uE890",
        "coding" or "code" or "coder" => "\uE943",
        "science" or "math" or "reasoning" => "\uE9D9",
        "multimodal" or "multi" => "\uE8B8",
        _ => "\uE99A",
    };

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");

    /// <summary>
    /// 从嵌套异常中提取最有用的错误描述
    /// </summary>
    private static string ExtractErrorDetail(Exception ex)
    {
        // AggregateException → 展开内部
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            var inner = agg.InnerExceptions[0];
            return FormatProviderError(inner);
        }

        return FormatProviderError(ex);
    }

    private static string FormatProviderError(Exception ex)
    {
        var msg = ex.Message;

        // ClientResultException: "HTTP 401 (Unauthorized: AuthenticationError)\n\nThe API key..."
        if (msg.Contains("401", StringComparison.Ordinal))
            return $"API Key 认证失败 — 请在设置中检查 API Key 是否正确。\n{msg}";

        if (msg.Contains("429", StringComparison.Ordinal))
            return $"请求频率超限 (429) — 请稍后重试或更换供应商。\n{msg}";

        if (msg.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            ex is TaskCanceledException)
            return "请求超时 — 请检查网络连接或 API 端点是否可达。";

        if (msg.Contains("未配置", StringComparison.Ordinal) ||
            msg.Contains("No provider", StringComparison.OrdinalIgnoreCase))
            return "未配置 LLM 供应商 — 请打开设置页，添加供应商配置后保存并重启。";

        return msg;
    }

    /// <summary>
    /// 手动触发一轮自我进化
    /// </summary>
    [RelayCommand]
    private void TriggerEvolution()
    {
        try
        {
            var scheduler = App.Services.GetService<OwnerAI.Agent.Scheduler.SchedulerService>();
            if (scheduler is not null)
            {
                scheduler.TriggerNow();
                StatusText = "🧬 已触发自我进化...";
                return;
            }
            StatusText = "调度服务未就绪";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] Failed to trigger evolution");
        }
    }

    /// <summary>
    /// 订阅进化状态事件 — 通过事件总线跨线程接收
    /// </summary>
    private void SubscribeEvolutionEvents()
    {
        try
        {
            var eventBus = App.Services.GetService<IEventBus>();
            if (eventBus is null) return;

            _evolutionSubscription = eventBus.Subscribe<EvolutionStatusEvent>(
                (evt, _) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        IsEvolutionActive = evt.IsActive;
                        EvolutionPhase = evt.Phase;
                        ShowEvolutionIndicator = evt.IsActive || !string.IsNullOrEmpty(evt.Phase);

                        if (evt.Stats is not null)
                        {
                            EvolutionSummary = $"缺口: {evt.Stats.TotalGaps} | 已解决: {evt.Stats.Resolved} | 失败: {evt.Stats.Failed}";
                        }
                    });
                    return ValueTask.CompletedTask;
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Chat] Failed to subscribe evolution events (non-critical)");
        }
    }

    /// <summary>
    /// 订阅后台任务聊天汇报事件 — 将进化/定时任务的触发、过程、结果显示在聊天窗口。
    /// 同一任务的 Start → Progress → Completed/Failed 合并为单条气泡，避免刷屏。
    /// </summary>
    private void SubscribeBackgroundTaskChatEvents()
    {
        try
        {
            var eventBus = App.Services.GetService<IEventBus>();
            if (eventBus is null) return;

            _backgroundTaskChatSubscription = eventBus.Subscribe<BackgroundTaskChatEvent>(
                (evt, _) =>
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        var sourceLabel = evt.Source == "evolution" ? _evolutionAgentName : _chatAgentName;
                        var glyph = evt.Source == "evolution" ? "\uE945" : "\uE916";
                        var key = evt.TaskId ?? evt.EventId;
                        var (taskStage, cleanMessage) = ParseBackgroundTaskMessage(evt.Message);

                        switch (evt.Phase)
                        {
                            case BackgroundTaskPhase.Start:
                            {
                                var bubble = new ChatBubble
                                {
                                    Id = Ulid.NewUlid().ToString(),
                                    Role = ChatRole.System,
                                    Text = $"▶ {cleanMessage}",
                                    ModelName = sourceLabel,
                                    ModelGlyph = glyph,
                                    TaskStage = taskStage,
                                    BackgroundGapId = evt.GapId,
                                    IsStreaming = true,
                                };
                                _activeTaskBubbles[key] = bubble;
                                Messages.Add(bubble);
                                break;
                            }

                            case BackgroundTaskPhase.Progress:
                            {
                                if (_activeTaskBubbles.TryGetValue(key, out var existing))
                                {
                                    // 追加进度行到同一气泡
                                    existing.Text = $"{existing.Text}\n🔄 {cleanMessage}";
                                }
                                else
                                {
                                    // 无对应 Start（可能漏掉），创建独立气泡
                                    var bubble = new ChatBubble
                                    {
                                        Id = Ulid.NewUlid().ToString(),
                                        Role = ChatRole.System,
                                        Text = $"🔄 {cleanMessage}",
                                        ModelName = sourceLabel,
                                        ModelGlyph = glyph,
                                        TaskStage = taskStage,
                                        BackgroundGapId = evt.GapId,
                                        IsStreaming = true,
                                    };
                                    _activeTaskBubbles[key] = bubble;
                                    Messages.Add(bubble);
                                }
                                break;
                            }

                            case BackgroundTaskPhase.Completed:
                            case BackgroundTaskPhase.Failed:
                            {
                                var icon = evt.Phase == BackgroundTaskPhase.Completed ? "✅" : "❌";
                                if (_activeTaskBubbles.TryGetValue(key, out var existing))
                                {
                                    existing.Text = $"{existing.Text}\n{icon} {cleanMessage}";
                                    existing.IsStreaming = false;
                                    _activeTaskBubbles.Remove(key);
                                }
                                else
                                {
                                    var bubble = new ChatBubble
                                    {
                                        Id = Ulid.NewUlid().ToString(),
                                        Role = ChatRole.System,
                                        Text = $"{icon} {cleanMessage}",
                                        ModelName = sourceLabel,
                                        ModelGlyph = glyph,
                                        TaskStage = taskStage,
                                        BackgroundGapId = evt.GapId,
                                    };
                                    Messages.Add(bubble);
                                }
                                break;
                            }

                            default:
                            {
                                var bubble = new ChatBubble
                                {
                                    Id = Ulid.NewUlid().ToString(),
                                    Role = ChatRole.System,
                                    Text = $"ℹ️ {cleanMessage}",
                                    ModelName = sourceLabel,
                                    ModelGlyph = glyph,
                                    TaskStage = taskStage,
                                    BackgroundGapId = evt.GapId,
                                };
                                Messages.Add(bubble);
                                break;
                            }
                        }

                        ScrollToBottomRequested?.Invoke();
                    });
                    return ValueTask.CompletedTask;
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Chat] Failed to subscribe background task chat events (non-critical)");
        }
    }

    private static (BackgroundTaskStage Stage, string CleanMessage) ParseBackgroundTaskMessage(string message)
    {
        if (message.StartsWith("[🧭 规划] ", StringComparison.Ordinal))
            return (BackgroundTaskStage.Planning, message[8..]);

        if (message.StartsWith("[🛠 执行] ", StringComparison.Ordinal))
            return (BackgroundTaskStage.Execution, message[8..]);

        if (message.StartsWith("[🧪 验收] ", StringComparison.Ordinal))
            return (BackgroundTaskStage.Verification, message[8..]);

        return (BackgroundTaskStage.None, message);
    }

    public void Dispose()
    {
        _evolutionSubscription?.Dispose();
        _backgroundTaskChatSubscription?.Dispose();
        _currentCts?.Cancel();
        _currentCts?.Dispose();
    }
}

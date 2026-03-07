using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace OwnerAI.Host.Desktop.Models;

/// <summary>
/// 聊天消息气泡 UI 模型
/// </summary>
public sealed partial class ChatBubble : ObservableObject
{
    private static readonly SolidColorBrush s_planningStageBrush = new(ColorHelper.FromArgb(255, 245, 158, 11));
    private static readonly SolidColorBrush s_executionStageBrush = new(ColorHelper.FromArgb(255, 59, 130, 246));
    private static readonly SolidColorBrush s_verificationStageBrush = new(ColorHelper.FromArgb(255, 139, 92, 246));
    private static readonly SolidColorBrush s_inactiveStageBrush = new(ColorHelper.FromArgb(255, 75, 85, 99));

    public required string Id { get; init; }
    public required ChatRole Role { get; init; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    [ObservableProperty]
    public partial bool IsStreaming { get; set; }

    /// <summary>模型名称 — 显示在气泡头部</summary>
    public string? ModelName { get; init; }

    /// <summary>模型图标字形</summary>
    public string ModelGlyph { get; init; } = "\uE99A";

    /// <summary>是否显示模型标识头部</summary>
    public bool ShowModelHeader => Role != ChatRole.User && !string.IsNullOrEmpty(ModelName);

    private BackgroundTaskStage _taskStage;
    public BackgroundTaskStage TaskStage
    {
        get => _taskStage;
        set
        {
            if (SetProperty(ref _taskStage, value))
            {
                OnPropertyChanged(nameof(BackgroundTaskStageVisibility));
                OnPropertyChanged(nameof(TaskStageLabel));
                OnPropertyChanged(nameof(TaskStageBrush));
                OnPropertyChanged(nameof(PlanningStageBrush));
                OnPropertyChanged(nameof(ExecutionStageBrush));
                OnPropertyChanged(nameof(VerificationStageBrush));
            }
        }
    }

    public Visibility BackgroundTaskStageVisibility =>
        TaskStage == BackgroundTaskStage.None ? Visibility.Collapsed : Visibility.Visible;

    public string TaskStageLabel => TaskStage switch
    {
        BackgroundTaskStage.Planning => "🧭 规划阶段",
        BackgroundTaskStage.Execution => "🛠 执行阶段",
        BackgroundTaskStage.Verification => "🧪 验收阶段",
        _ => string.Empty,
    };

    public Brush TaskStageBrush => TaskStage switch
    {
        BackgroundTaskStage.Planning => s_planningStageBrush,
        BackgroundTaskStage.Execution => s_executionStageBrush,
        BackgroundTaskStage.Verification => s_verificationStageBrush,
        _ => s_inactiveStageBrush,
    };

    public Brush PlanningStageBrush => TaskStage == BackgroundTaskStage.Planning ? s_planningStageBrush : s_inactiveStageBrush;
    public Brush ExecutionStageBrush => TaskStage == BackgroundTaskStage.Execution ? s_executionStageBrush : s_inactiveStageBrush;
    public Brush VerificationStageBrush => TaskStage == BackgroundTaskStage.Verification ? s_verificationStageBrush : s_inactiveStageBrush;

    public global::Windows.UI.Text.FontWeight PlanningStageWeight => TaskStage == BackgroundTaskStage.Planning ? FontWeights.SemiBold : FontWeights.Normal;
    public global::Windows.UI.Text.FontWeight ExecutionStageWeight => TaskStage == BackgroundTaskStage.Execution ? FontWeights.SemiBold : FontWeights.Normal;
    public global::Windows.UI.Text.FontWeight VerificationStageWeight => TaskStage == BackgroundTaskStage.Verification ? FontWeights.SemiBold : FontWeights.Normal;

    private string? _backgroundGapId;
    public string? BackgroundGapId
    {
        get => _backgroundGapId;
        set
        {
            if (SetProperty(ref _backgroundGapId, value))
            {
                OnPropertyChanged(nameof(BackgroundGapIdVisibility));
                OnPropertyChanged(nameof(BackgroundGapIdText));
            }
        }
    }

    public Visibility BackgroundGapIdVisibility => string.IsNullOrWhiteSpace(BackgroundGapId)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string BackgroundGapIdText => string.IsNullOrWhiteSpace(BackgroundGapId)
        ? string.Empty
        : $"GAP {BackgroundGapId}";

    public ObservableCollection<ToolCallDisplay> ToolCalls { get; }

    [ObservableProperty]
    public partial bool HasToolCalls { get; set; }

    public ChatBubble()
    {
        ToolCalls = new ObservableCollection<ToolCallDisplay>();
        ToolCalls.CollectionChanged += (_, _) => HasToolCalls = ToolCalls.Count > 0;
    }

    /// <summary>附件列表 — 图片、视频、文档等</summary>
    public ObservableCollection<AttachmentDisplay> Attachments { get; } = [];

    /// <summary>是否有可显示的媒体附件</summary>
    public bool HasAttachments => Attachments.Count > 0;

    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role is ChatRole.Assistant or ChatRole.SecondaryModel or ChatRole.Delegation;

    // ─── 混合内容（文本 + 内联图片）───

    /// <summary>解析后的混合内容片段</summary>
    public ObservableCollection<ContentSegment> ContentSegments { get; } = [];

    /// <summary>是否显示富文本内容（含内联图片），替代纯文本</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlainText))]
    public partial bool ShowRichContent { get; set; }

    /// <summary>是否显示纯文本（流式输出中或无内联图片时）</summary>
    public bool ShowPlainText => !ShowRichContent;

    /// <summary>
    /// 流式输出结束后，解析文本中的 Markdown 图片语法 ![alt](url)，
    /// 将文字与图片交错显示，使每条新闻的配图与内容对应
    /// </summary>
    partial void OnIsStreamingChanged(bool value)
    {
        if (!value && Role != ChatRole.User)
        {
            ParseContentSegments();
        }
    }

    /// <summary>工具提取的媒体资源 — 不直接显示，由 ParseContentSegments 合并到 ContentSegments</summary>
    internal List<(string Url, string Name, AttachmentKind Kind)> ExtractedMedia { get; } = [];

    /// <summary>匹配 Markdown 图片 ![alt](url) 和链接 [text](url)</summary>
    private static readonly Regex s_markdownRegex = new(
        @"(!?)\[([^\]]*)\]\((https?://[^)\s]+)\)",
        RegexOptions.Compiled);

    private void ParseContentSegments()
    {
        var text = Text ?? "";
        var matches = s_markdownRegex.Matches(text);
        var hasMediaAttachments = ExtractedMedia.Count > 0;

        if (matches.Count == 0 && !hasMediaAttachments) return;

        var inlineUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (matches.Count > 0)
        {
            var lastIndex = 0;
            foreach (Match match in matches)
            {
                // 标记前的文字段
                if (match.Index > lastIndex)
                {
                    var textBefore = text[lastIndex..match.Index].Trim();
                    if (textBefore.Length > 0)
                        ContentSegments.Add(new ContentSegment
                        {
                            Kind = ContentSegmentKind.Text,
                            Content = textBefore,
                        });
                }

                var isImage = match.Groups[1].Value == "!";
                var alt = match.Groups[2].Value;
                var url = match.Groups[3].Value;
                inlineUrls.Add(url);

                if (isImage)
                {
                    ContentSegments.Add(new ContentSegment
                    {
                        Kind = ContentSegmentKind.Image,
                        Content = url,
                    });
                }
                else
                {
                    // 若显示文本包含视频符号，将其作为视频链接展示
                    var kind = alt.Contains('▶') || alt.Contains("视频") 
                        ? ContentSegmentKind.Video 
                        : ContentSegmentKind.Link;

                    ContentSegments.Add(new ContentSegment
                    {
                        Kind = kind,
                        Content = url,
                        DisplayText = alt.Trim(),
                    });
                }

                lastIndex = match.Index + match.Length;
            }

            // 剩余文字
            if (lastIndex < text.Length)
            {
                var remaining = text[lastIndex..].Trim();
                if (remaining.Length > 0)
                    ContentSegments.Add(new ContentSegment
                    {
                        Kind = ContentSegmentKind.Text,
                        Content = remaining,
                    });
            }
        }
        else if (!string.IsNullOrWhiteSpace(text))
        {
            // 无 Markdown 标记但有媒体附件 — 将全部文本作为一个段
            ContentSegments.Add(new ContentSegment
            {
                Kind = ContentSegmentKind.Text,
                Content = text,
            });
        }

        // 合并工具提取的媒体 — 去重后追加到 ContentSegments
        foreach (var media in ExtractedMedia)
        {
            if (inlineUrls.Contains(media.Url)) continue;

            if (media.Kind == AttachmentKind.Image)
            {
                ContentSegments.Add(new ContentSegment
                {
                    Kind = ContentSegmentKind.Image,
                    Content = media.Url,
                });
            }
            else if (media.Kind == AttachmentKind.Video)
            {
                ContentSegments.Add(new ContentSegment
                {
                    Kind = ContentSegmentKind.Video,
                    Content = media.Url,
                    DisplayText = media.Name,
                });
            }
        }

        ShowRichContent = ContentSegments.Count > 0;
    }
}

public enum ChatRole
{
    User,
    Assistant,
    System,
    /// <summary>分发指令 — 主模型向次级模型发送的任务</summary>
    Delegation,
    /// <summary>次级模型回复</summary>
    SecondaryModel,
}

public enum BackgroundTaskStage
{
    None = 0,
    Planning = 1,
    Execution = 2,
    Verification = 3,
}

/// <summary>
/// 工具调用展示模型
/// </summary>
public sealed partial class ToolCallDisplay : ObservableObject
{
    public ToolCallDisplay()
    {
        IsRunning = true;
    }

    public required string ToolName { get; init; }

    [ObservableProperty]
    public partial string? Parameters { get; set; }

    [ObservableProperty]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial bool Success { get; set; }

    [ObservableProperty]
    public partial TimeSpan Duration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    public partial bool IsRunning { get; set; }

    /// <summary>反向逻辑，供 XAML 绑定可见性</summary>
    public bool IsFinished => !IsRunning;
}

/// <summary>
/// 附件展示模型
/// </summary>
public sealed partial class AttachmentDisplay : ObservableObject
{
    /// <summary>文件名</summary>
    public required string FileName { get; init; }

    /// <summary>本地文件路径或远程 URL</summary>
    public required string FilePath { get; init; }

    /// <summary>MIME 类型</summary>
    public required string ContentType { get; init; }

    /// <summary>文件大小 (字节)</summary>
    public long Size { get; init; }

    /// <summary>媒体类型分类</summary>
    public AttachmentKind Kind { get; init; }

    /// <summary>仅图片类型返回路径 — 防止 MediaPlayerElement 对图片 URL 触发原生解码崩溃</summary>
    public string? ImagePath => Kind == AttachmentKind.Image ? FilePath : null;

    /// <summary>仅视频类型返回路径 — 防止 BitmapImage 对视频 URL 进行不必要的加载</summary>
    public string? VideoPath => Kind == AttachmentKind.Video ? FilePath : null;
}

/// <summary>
/// 附件种类
/// </summary>
public enum AttachmentKind
{
    /// <summary>图片 — 内联显示</summary>
    Image,
    /// <summary>视频 — 内联播放器</summary>
    Video,
    /// <summary>文档 — 显示图标和文件名</summary>
    Document,
    /// <summary>其他文件</summary>
    Other,
}

/// <summary>
/// 混合内容片段类型
/// </summary>
public enum ContentSegmentKind
{
    Text,
    Image,
    Video,
    Link,
}

/// <summary>
/// 文字或内联图片片段 — 用于将 Markdown 图片语法渲染到对应文字位置
/// </summary>
public sealed class ContentSegment
{
    public required ContentSegmentKind Kind { get; init; }
    public required string Content { get; init; }

    /// <summary>链接/视频的显示文本</summary>
    public string? DisplayText { get; init; }

    /// <summary>仅图片类型返回 URL，供 XAML 绑定</summary>
    public string? ImageUrl => Kind == ContentSegmentKind.Image ? Content : null;

    /// <summary>链接/视频导航 URI</summary>
    public Uri? NavigateUri
    {
        get
        {
            if (Kind is not (ContentSegmentKind.Link or ContentSegmentKind.Video))
                return null;
            try { return new Uri(Content); }
            catch { return null; }
        }
    }

    public bool IsText => Kind == ContentSegmentKind.Text;
    public bool IsImage => Kind == ContentSegmentKind.Image;
    public bool IsVideo => Kind == ContentSegmentKind.Video;
    public bool IsLink => Kind == ContentSegmentKind.Link;
}

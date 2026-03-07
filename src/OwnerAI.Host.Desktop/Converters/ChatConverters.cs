using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OwnerAI.Host.Desktop.Models;
using Windows.Media.Core;

namespace OwnerAI.Host.Desktop.Converters;

/// <summary>
/// 字符串路径/URL → ImageSource — 安全创建 BitmapImage，支持本地路径和 HTTP URL
/// WinUI 的 XAML 内置类型转换无法处理 HTTP URL → ImageSource，必须显式创建 BitmapImage
/// </summary>
public sealed class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                return new BitmapImage(new Uri(path));
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (IsUser) → HorizontalAlignment (Right/Left)
/// </summary>
public sealed class BoolToAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// ChatRole → Brush — 不同角色使用不同背景色
/// </summary>
public sealed class ChatRoleToBrushConverter : IValueConverter
{
    public Brush? UserBrush { get; set; }
    public Brush? AssistantBrush { get; set; }
    public Brush? DelegationBrush { get; set; }
    public Brush? SecondaryBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ChatRole.User => UserBrush,
            ChatRole.Delegation => DelegationBrush ?? AssistantBrush,
            ChatRole.SecondaryModel => SecondaryBrush ?? AssistantBrush,
            _ => AssistantBrush,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (IsUser) → Brush (用户紫色/助手深灰) — 保留向后兼容
/// </summary>
public sealed class BoolToBubbleBrushConverter : IValueConverter
{
    public Brush? UserBrush { get; set; }
    public Brush? AssistantBrush { get; set; }

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        return value is true ? UserBrush : AssistantBrush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// AttachmentKind → Visibility — 仅当附件类型匹配 ConverterParameter 时可见
/// </summary>
public sealed class AttachmentKindToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not AttachmentKind kind || parameter is not string expected)
            return Visibility.Collapsed;

        var match = expected switch
        {
            "Image" => kind == AttachmentKind.Image,
            "Video" => kind == AttachmentKind.Video,
            "Document" => kind is AttachmentKind.Document or AttachmentKind.Other,
            _ => false,
        };

        return match ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// 文件路径字符串 → MediaSource（用于 MediaPlayerElement 绑定）
/// 仅对可直接播放的视频 URL 创建 MediaSource，跳过嵌入式播放器页面（YouTube、Bilibili 等）
/// </summary>
public sealed class StringToMediaSourceConverter : IValueConverter
{
    /// <summary>已知的视频嵌入域名 — 这些是 HTML 页面，MediaPlayerElement 无法播放</summary>
    private static readonly string[] s_embedDomains =
    [
        "youtube.com", "youtu.be", "bilibili.com", "player.bilibili.com",
        "v.qq.com", "player.youku.com", "vimeo.com", "dailymotion.com",
        "douyin.com", "ixigua.com",
    ];

    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return null;

        try
        {
            var uri = new Uri(path);

            // 跳过已知的视频嵌入页面 — 它们是 HTML 页面，不是可播放的视频流
            if (uri.Scheme is "http" or "https")
            {
                var host = uri.Host;
                foreach (var domain in s_embedDomains)
                {
                    if (host.Contains(domain, StringComparison.OrdinalIgnoreCase))
                        return null;
                }
            }

            return MediaSource.CreateFromUri(uri);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

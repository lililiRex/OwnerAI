using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Host.Desktop.Converters;

/// <summary>
/// ScheduledTaskStatus → FontIcon Glyph
/// </summary>
public sealed class TaskStatusToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ScheduledTaskStatus.Pending   => "\uE823",  // ⏳
            ScheduledTaskStatus.Dispatching => "\uE8A7", // 发射/派发
            ScheduledTaskStatus.WaitingForLlm => "\uE895", // ⏱ / 等待资源
            ScheduledTaskStatus.RetryWaiting => "\uE72B",  // ↻
            ScheduledTaskStatus.Running   => "\uEA81",  // ▶ 播放
            ScheduledTaskStatus.Completed => "\uE73E",  // ✅
            ScheduledTaskStatus.Failed    => "\uEA39",  // ❌
            ScheduledTaskStatus.Blocked   => "\uEA3B",  // 阻塞
            ScheduledTaskStatus.Cancelled => "\uE711",  // 🚫
            ScheduledTaskStatus.Paused    => "\uE769",  // ⏸
            _ => "\uE823",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// DateTimeOffset → 短时间文本
/// </summary>
public sealed class DateTimeOffsetToShortTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is DateTimeOffset time
            ? time.ToLocalTime().ToString("MM-dd HH:mm")
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// TimeSpan → 执行耗时文本
/// </summary>
public sealed class DurationToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is TimeSpan duration
            ? $"耗时 {duration.TotalSeconds:F1}s"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (执行成功) → 边框高亮色
/// </summary>
public sealed class ExecutionSuccessToBorderBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_success = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush s_failure = new(ColorHelper.FromArgb(255, 239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? s_success : s_failure;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (执行成功) → 文本标签
/// </summary>
public sealed class ExecutionSuccessToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "成功" : "失败";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (执行成功) → Glyph
/// </summary>
public sealed class ExecutionSuccessToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? "\uE73E" : "\uEA39";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (执行成功) → 前景色
/// </summary>
public sealed class ExecutionSuccessToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_success = new(ColorHelper.FromArgb(255, 34, 197, 94));
    private static readonly SolidColorBrush s_failure = new(ColorHelper.FromArgb(255, 239, 68, 68));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? s_success : s_failure;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// string? → Visibility（非空可见）
/// </summary>
public sealed class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is string text && !string.IsNullOrWhiteSpace(text)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// DateTimeOffset? → 下次执行文本
/// </summary>
public sealed class NextRunAtToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is DateTimeOffset nextRun
            ? $"下次执行：{nextRun:yyyy-MM-dd HH:mm}"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// DateTimeOffset? → Visibility（有值可见）
/// </summary>
public sealed class NullableDateTimeToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is DateTimeOffset ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// ScheduledTaskStatus → SolidColorBrush 前景色
/// </summary>
public sealed class TaskStatusToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_pending   = new(ColorHelper.FromArgb(255, 156, 163, 175)); // gray-400
    private static readonly SolidColorBrush s_running   = new(ColorHelper.FromArgb(255, 99, 102, 241));  // indigo (OwnerAIPrimary)
    private static readonly SolidColorBrush s_waitingLlm = new(ColorHelper.FromArgb(255, 59, 130, 246)); // blue-500
    private static readonly SolidColorBrush s_retryWaiting = new(ColorHelper.FromArgb(255, 245, 158, 11)); // amber-500
    private static readonly SolidColorBrush s_completed = new(ColorHelper.FromArgb(255, 34, 197, 94));   // green-500
    private static readonly SolidColorBrush s_failed    = new(ColorHelper.FromArgb(255, 239, 68, 68));   // red-500
    private static readonly SolidColorBrush s_cancelled = new(ColorHelper.FromArgb(255, 107, 114, 128)); // gray-500
    private static readonly SolidColorBrush s_paused    = new(ColorHelper.FromArgb(255, 245, 158, 11));  // amber-500

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ScheduledTaskStatus.Pending   => s_pending,
            ScheduledTaskStatus.Dispatching => s_running,
            ScheduledTaskStatus.WaitingForLlm => s_waitingLlm,
            ScheduledTaskStatus.RetryWaiting => s_retryWaiting,
            ScheduledTaskStatus.Running   => s_running,
            ScheduledTaskStatus.Completed => s_completed,
            ScheduledTaskStatus.Failed    => s_failed,
            ScheduledTaskStatus.Blocked   => s_failed,
            ScheduledTaskStatus.Cancelled => s_cancelled,
            ScheduledTaskStatus.Paused    => s_paused,
            _ => s_pending,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// ScheduledTaskStatus → Visibility（通过 ConverterParameter 指定按钮类型）
/// Parameter: "Pause" | "Resume" | "Cancel"
/// </summary>
public sealed class TaskStatusToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not ScheduledTaskStatus status || parameter is not string button)
            return Visibility.Collapsed;

        var visible = button switch
        {
            // 暂停：仅 Pending / Running 可暂停
            "Pause" => status is ScheduledTaskStatus.Pending or ScheduledTaskStatus.Dispatching or ScheduledTaskStatus.WaitingForLlm or ScheduledTaskStatus.RetryWaiting or ScheduledTaskStatus.Running,
            // 恢复：仅 Paused 可恢复
            "Resume" => status is ScheduledTaskStatus.Paused,
            // 取消：非终态（Pending / Running / Paused）可取消
            "Cancel" => status is ScheduledTaskStatus.Pending or ScheduledTaskStatus.Dispatching or ScheduledTaskStatus.WaitingForLlm or ScheduledTaskStatus.RetryWaiting or ScheduledTaskStatus.Running or ScheduledTaskStatus.Blocked or ScheduledTaskStatus.Paused,
            _ => true,
        };

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// ScheduledTaskStatus → 状态标签文本
/// </summary>
public sealed class TaskStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ScheduledTaskStatus.Pending   => "已入队",
            ScheduledTaskStatus.Dispatching => "派发中",
            ScheduledTaskStatus.WaitingForLlm => "等待 LLM",
            ScheduledTaskStatus.RetryWaiting => "等待重试",
            ScheduledTaskStatus.Running   => "运行中",
            ScheduledTaskStatus.Completed => "已完成",
            ScheduledTaskStatus.Failed    => "已失败",
            ScheduledTaskStatus.Blocked   => "已阻塞",
            ScheduledTaskStatus.Cancelled => "已取消",
            ScheduledTaskStatus.Paused    => "已暂停",
            _ => "未知",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// ScheduledTaskStatus → 状态标签背景 Brush
/// </summary>
public sealed class TaskStatusToBadgeBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_pending   = new(ColorHelper.FromArgb(255, 107, 114, 128)); // gray
    private static readonly SolidColorBrush s_running   = new(ColorHelper.FromArgb(255, 99, 102, 241));  // indigo
    private static readonly SolidColorBrush s_waitingLlm = new(ColorHelper.FromArgb(255, 59, 130, 246)); // blue
    private static readonly SolidColorBrush s_retryWaiting = new(ColorHelper.FromArgb(255, 245, 158, 11)); // amber
    private static readonly SolidColorBrush s_completed = new(ColorHelper.FromArgb(255, 34, 197, 94));   // green
    private static readonly SolidColorBrush s_failed    = new(ColorHelper.FromArgb(255, 239, 68, 68));   // red
    private static readonly SolidColorBrush s_cancelled = new(ColorHelper.FromArgb(255, 107, 114, 128)); // gray
    private static readonly SolidColorBrush s_paused    = new(ColorHelper.FromArgb(255, 245, 158, 11));  // amber

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value switch
        {
            ScheduledTaskStatus.Pending   => s_pending,
            ScheduledTaskStatus.Dispatching => s_running,
            ScheduledTaskStatus.WaitingForLlm => s_waitingLlm,
            ScheduledTaskStatus.RetryWaiting => s_retryWaiting,
            ScheduledTaskStatus.Running   => s_running,
            ScheduledTaskStatus.Completed => s_completed,
            ScheduledTaskStatus.Failed    => s_failed,
            ScheduledTaskStatus.Blocked   => s_failed,
            ScheduledTaskStatus.Cancelled => s_cancelled,
            ScheduledTaskStatus.Paused    => s_paused,
            _ => s_pending,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// 颜色 Hex 字符串 → SolidColorBrush（用于技能卡片安全等级标签）
/// </summary>
public sealed class StringToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is string hex && hex.Length == 7 && hex[0] == '#')
        {
            var r = System.Convert.ToByte(hex.Substring(1, 2), 16);
            var g = System.Convert.ToByte(hex.Substring(3, 2), 16);
            var b = System.Convert.ToByte(hex.Substring(5, 2), 16);
            return new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
        }
        return new SolidColorBrush(ColorHelper.FromArgb(255, 156, 163, 175));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (IsEnabled) → Opacity (1.0 / 0.45)
/// </summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? 1.0 : 0.45;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// int → Visibility (0=Collapsed, >0=Visible)
/// </summary>
public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool (IsDuplicate) → BorderBrush (Red / Transparent)
/// </summary>
public sealed class BoolToDuplicateBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush s_red = new(ColorHelper.FromArgb(255, 239, 68, 68));
    private static readonly SolidColorBrush s_transparent = new(Microsoft.UI.Colors.Transparent);

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? s_red : s_transparent;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>
/// bool → Visibility (true=Visible, false=Collapsed)
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

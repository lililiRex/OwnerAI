namespace OwnerAI.Shared.Extensions;

/// <summary>
/// 字符串扩展方法
/// </summary>
public static class StringExtensions
{
    /// <summary>截断字符串到指定长度</summary>
    public static string Truncate(this string value, int maxLength, string suffix = "...")
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return string.Concat(value.AsSpan(0, maxLength - suffix.Length), suffix);
    }

    /// <summary>判断字符串是否不为空</summary>
    public static bool IsNotNullOrEmpty(this string? value)
        => !string.IsNullOrEmpty(value);

    /// <summary>判断字符串是否不为空白</summary>
    public static bool IsNotNullOrWhiteSpace(this string? value)
        => !string.IsNullOrWhiteSpace(value);
}

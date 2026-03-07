using System.Text.RegularExpressions;

namespace OwnerAI.Security.Sandbox;

/// <summary>
/// 路径安全校验 — 阻止访问敏感路径
/// </summary>
public static partial class PathValidator
{
    private static readonly string[] BlockedPaths =
    [
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
    ];

    private static readonly string[] BlockedExtensions =
    [
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".reg", ".sys", ".dll",
    ];

    /// <summary>
    /// 校验路径是否安全
    /// </summary>
    public static PathValidationResult Validate(string path, bool allowWrite = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return PathValidationResult.Invalid("路径不能为空");

        var fullPath = Path.GetFullPath(path);

        // 阻止路径遍历
        if (fullPath.Contains("..", StringComparison.Ordinal))
            return PathValidationResult.Invalid("路径不能包含 '..'");

        // 检查阻止路径
        foreach (var blocked in BlockedPaths)
        {
            if (fullPath.StartsWith(blocked, StringComparison.OrdinalIgnoreCase))
                return PathValidationResult.Invalid($"禁止访问系统路径: {blocked}");
        }

        // 写入模式下检查扩展名
        if (allowWrite)
        {
            var ext = Path.GetExtension(fullPath);
            if (BlockedExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                return PathValidationResult.Invalid($"禁止写入可执行文件: {ext}");
        }

        return PathValidationResult.Safe(fullPath);
    }
}

public sealed record PathValidationResult
{
    public bool IsSafe { get; init; }
    public string? NormalizedPath { get; init; }
    public string? Reason { get; init; }

    public static PathValidationResult Safe(string path)
        => new() { IsSafe = true, NormalizedPath = path };

    public static PathValidationResult Invalid(string reason)
        => new() { IsSafe = false, Reason = reason };
}

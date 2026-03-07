namespace OwnerAI.Security.Sandbox;

/// <summary>
/// 命令安全校验 — 阻止危险命令
/// </summary>
public static class CommandValidator
{
    private static readonly string[] BlockedCommands =
    [
        "format", "diskpart", "bcdedit", "reg delete", "rd /s",
        "rmdir /s", "del /f /s /q C:", "shutdown", "taskkill /f /im",
        "net user", "net localgroup", "powershell -encodedcommand",
    ];

    private static readonly string[] BlockedPatterns =
    [
        "rm -rf /", ":(){ :|:& };:", "mkfs", "> /dev/sda",
    ];

    /// <summary>
    /// 校验命令是否安全
    /// </summary>
    public static CommandValidationResult Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return CommandValidationResult.Invalid("命令不能为空");

        var lower = command.ToLowerInvariant();

        foreach (var blocked in BlockedCommands)
        {
            if (lower.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                return CommandValidationResult.Blocked($"命令包含危险操作: {blocked}");
        }

        foreach (var pattern in BlockedPatterns)
        {
            if (lower.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return CommandValidationResult.Blocked($"命令包含危险模式: {pattern}");
        }

        return CommandValidationResult.Safe();
    }
}

public sealed record CommandValidationResult
{
    public bool IsSafe { get; init; }
    public bool IsBlocked { get; init; }
    public string? Reason { get; init; }

    public static CommandValidationResult Safe()
        => new() { IsSafe = true };

    public static CommandValidationResult Invalid(string reason)
        => new() { IsSafe = false, Reason = reason };

    public static CommandValidationResult Blocked(string reason)
        => new() { IsSafe = false, IsBlocked = true, Reason = reason };
}

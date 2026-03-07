namespace OwnerAI.Agent.Tools.Shell;

/// <summary>
/// PowerShell 路径发现 — 优先使用 pwsh (7+)，回退到 powershell (5.1)
/// </summary>
internal static class ShellDetector
{
    private static string? _cachedPath;

    /// <summary>
    /// 获取可用的 PowerShell 可执行文件路径
    /// </summary>
    public static string GetPowerShellPath()
    {
        if (_cachedPath is not null)
            return _cachedPath;

        // 优先 pwsh (PowerShell 7+)
        var pwshPaths = new[]
        {
            "pwsh",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        };

        foreach (var path in pwshPaths)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-NoProfile -NonInteractive -Command \"exit 0\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = System.Diagnostics.Process.Start(psi);
                if (process is not null)
                {
                    process.WaitForExit(3000);
                    if (process.ExitCode == 0)
                    {
                        _cachedPath = path;
                        return path;
                    }
                }
            }
            catch
            {
                // pwsh 不可用，继续尝试
            }
        }

        // 回退到 Windows PowerShell 5.1
        _cachedPath = "powershell";
        return _cachedPath;
    }
}

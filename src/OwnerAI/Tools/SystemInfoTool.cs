using System.Diagnostics;
using System.Runtime.InteropServices;
using OwnerAI.Models;

namespace OwnerAI.Tools;

public class SystemInfoTool : IToolHandler
{
    public string Name => "system_info";

    public ToolDefinition GetDefinition() => new()
    {
        Function = new()
        {
            Name = Name,
            Description = "获取当前系统的 CPU、内存、磁盘等硬件和操作系统信息",
            Parameters = new()
            {
                Properties = new()
            }
        }
    };

    public async Task<string> ExecuteAsync(Dictionary<string, object?> parameters)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 系统信息");
        sb.AppendLine();

        // OS Info
        sb.AppendLine($"**操作系统：** {RuntimeInformation.OSDescription}");
        sb.AppendLine($"**OS 架构：** {RuntimeInformation.OSArchitecture}");
        sb.AppendLine($"**进程架构：** {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($"**.NET 运行时：** {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"**机器名：** {Environment.MachineName}");
        sb.AppendLine($"**用户名：** {Environment.UserName}");
        sb.AppendLine($"**系统启动时间：** {DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64):yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**当前时间：** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        // CPU Info
        sb.AppendLine($"**CPU 核心数：** {Environment.ProcessorCount} 核");
        sb.AppendLine();

        // Memory Info
        var memInfo = GetMemoryInfo();
        sb.AppendLine(memInfo);

        // Disk Info
        sb.AppendLine("**磁盘信息：**");
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
        {
            var total = drive.TotalSize / (1024.0 * 1024 * 1024);
            var free = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var used = total - free;
            sb.AppendLine($"  - {drive.Name} ({drive.DriveType}): 总 {total:F1}GB, 已用 {used:F1}GB, 可用 {free:F1}GB");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sb.AppendLine();
            var cpuUsage = await GetWindowsCpuUsageAsync();
            sb.AppendLine(cpuUsage);
        }

        return sb.ToString();
    }

    private static string GetMemoryInfo()
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var memStatus = new MEMORYSTATUSEX();
                memStatus.dwLength = (uint)Marshal.SizeOf(memStatus);
                if (GlobalMemoryStatusEx(ref memStatus))
                {
                    var totalMB = memStatus.ullTotalPhys / 1024.0 / 1024;
                    var freeMB = memStatus.ullAvailPhys / 1024.0 / 1024;
                    var usedMB = totalMB - freeMB;
                    sb.AppendLine($"**内存：** 总 {totalMB / 1024:F1}GB, 已用 {usedMB / 1024:F1}GB, 可用 {freeMB / 1024:F1}GB（使用率 {memStatus.dwMemoryLoad}%）");
                }
            }
            else if (File.Exists("/proc/meminfo"))
            {
                var lines = File.ReadAllLines("/proc/meminfo");
                var memDict = new Dictionary<string, long>();
                foreach (var line in lines)
                {
                    var parts = line.Split(':', 2, StringSplitOptions.TrimEntries);
                    if (parts.Length == 2 && long.TryParse(parts[1].Replace("kB", "").Trim(), out var val))
                        memDict[parts[0]] = val;
                }
                if (memDict.TryGetValue("MemTotal", out var total) &&
                    memDict.TryGetValue("MemAvailable", out var avail))
                {
                    var usedKB = total - avail;
                    sb.AppendLine($"**内存：** 总 {total / 1024.0 / 1024:F1}GB, 已用 {usedKB / 1024.0 / 1024:F1}GB, 可用 {avail / 1024.0 / 1024:F1}GB");
                }
            }
        }
        catch
        {
            sb.AppendLine("**内存：** 无法获取");
        }
        return sb.ToString();
    }

    private static async Task<string> GetWindowsCpuUsageAsync()
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (double.TryParse(output.Trim(), out var usage))
                return $"**CPU 使用率：** {usage:F1}%";
        }
        catch { }
        return "";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}

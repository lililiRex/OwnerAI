using Microsoft.Extensions.DependencyInjection;
using OwnerAI.Agent.Tools.Clipboard;
using OwnerAI.Agent.Tools.Communication;
using OwnerAI.Agent.Tools.Download;
using OwnerAI.Agent.Tools.FileSystem;
using OwnerAI.Agent.Tools.ProcessTools;
using OwnerAI.Agent.Tools.Shell;
using OwnerAI.Agent.Tools.SystemTools;
using OwnerAI.Agent.Tools.Office;
using OwnerAI.Agent.Tools.Web;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tools;

/// <summary>
/// 内置工具注册扩展
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOwnerAITools(this IServiceCollection services)
    {
        // 文件系统工具
        services.AddSingleton<IOwnerAITool, ReadFileTool>();
        services.AddSingleton<IOwnerAITool, WriteFileTool>();
        services.AddSingleton<IOwnerAITool, ListDirectoryTool>();
        services.AddSingleton<IOwnerAITool, SearchFilesTool>();

        // 系统工具
        services.AddSingleton<IOwnerAITool, SystemInfoTool>();

        // Shell 命令
        services.AddSingleton<IOwnerAITool, RunCommandTool>();

        // 进程管理
        services.AddSingleton<IOwnerAITool, ProcessListTool>();
        services.AddSingleton<IOwnerAITool, OpenAppTool>();

        // 网页工具
        services.AddSingleton<IOwnerAITool, WebFetchTool>();
        services.AddSingleton<IOwnerAITool, WebSearchTool>();

        // 剪贴板
        services.AddSingleton<IOwnerAITool, ClipboardTool>();

        // 下载工具
        services.AddSingleton<IOwnerAITool, DownloadFileTool>();
        services.AddSingleton<IOwnerAITool, DownloadVideoTool>();

        // 文档工具 (Office / WPS)
        services.AddSingleton<IOwnerAITool, DocumentTool>();

        // 通信工具 (TCP/UDP/串口)
        services.AddSingleton<IOwnerAITool, TcpCommunicationTool>();
        services.AddSingleton<IOwnerAITool, UdpCommunicationTool>();
        services.AddSingleton<IOwnerAITool, SerialPortTool>();

        return services;
    }
}

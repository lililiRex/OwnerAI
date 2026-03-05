using Microsoft.Extensions.Configuration;
using OwnerAI.Models;
using OwnerAI.Services;
using OwnerAI.Tools;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ─── Load Configuration ───────────────────────────────────────────────────────
var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
    .AddEnvironmentVariables("OWNERAI_")
    .Build();

var settings = new AppSettings();
config.Bind(settings);

if (string.IsNullOrWhiteSpace(settings.LLM.ApiKey) || settings.LLM.ApiKey == "your-api-key-here")
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("⚠️  警告：未配置 API Key，请在 appsettings.json 中填写 LLM.ApiKey");
    Console.WriteLine("       或设置环境变量 OWNERAI_LLM__ApiKey=your-api-key");
    Console.ResetColor();
    Console.WriteLine();
}

// ─── Build Services ───────────────────────────────────────────────────────────
var httpClient = new HttpClient();
httpClient.Timeout = TimeSpan.FromMinutes(5);

var llmService = new LLMService(httpClient, settings.LLM);

var toolRegistry = new ToolRegistry();
toolRegistry.Register(new WebSearchTool(httpClient, settings.Bing.ApiKey, settings.Bing.Endpoint));
toolRegistry.Register(new WebFetchTool(httpClient));
toolRegistry.Register(new ClipboardTool());
toolRegistry.Register(new ReadFileTool());
toolRegistry.Register(new WriteFileTool());
toolRegistry.Register(new ListDirectoryTool());
toolRegistry.Register(new SearchFilesTool());
toolRegistry.Register(new RunCommandTool());
toolRegistry.Register(new OpenAppTool());
toolRegistry.Register(new SystemInfoTool());
toolRegistry.Register(new ProcessListTool());
toolRegistry.Register(new DownloadFileTool(httpClient));
toolRegistry.Register(new DownloadVideoTool());
toolRegistry.Register(new DelegateToModelTool(httpClient, settings.SubModels));

var chatService = new ChatService(llmService, toolRegistry);

chatService.SetSystemPrompt(
    "你是 OwnerAI，一个功能强大的本地 AI 助手。" +
    "你拥有丰富的工具能力，包括：网页搜索、网页内容获取、文件读写、目录操作、" +
    "系统命令执行、应用程序启动、系统信息查询、进程管理、文件下载、视频下载，" +
    "以及将专业任务委托给专业子模型处理。" +
    "请主动使用这些工具来完成用户的请求，并以清晰、友好的中文回复。");

// ─── Print Welcome Banner ─────────────────────────────────────────────────────
PrintBanner(toolRegistry);

// ─── Main Chat Loop ───────────────────────────────────────────────────────────
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

while (!cts.IsCancellationRequested)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("\n你：");
    Console.ResetColor();

    string? input;
    try
    {
        input = Console.ReadLine();
    }
    catch (OperationCanceledException)
    {
        break;
    }

    if (input == null || cts.IsCancellationRequested) break;
    input = input.Trim();

    if (string.IsNullOrEmpty(input)) continue;

    // Built-in commands
    if (input.StartsWith("/"))
    {
        HandleCommand(input, chatService);
        continue;
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.Write("\nOwnerAI：");
    Console.ResetColor();
    Console.WriteLine();

    try
    {
        var response = await chatService.SendAsync(
            input,
            onToolCall: msg =>
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {msg}");
                Console.ResetColor();
            },
            cancellationToken: cts.Token);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(response);
        Console.ResetColor();
    }
    catch (OperationCanceledException)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("\n（已中断）");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"❌ 错误：{ex.Message}");
        Console.ResetColor();
    }
}

Console.WriteLine("\n再见！");

// ─── Helper Methods ───────────────────────────────────────────────────────────
static void PrintBanner(ToolRegistry toolRegistry)
{
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine("╔════════════════════════════════════════════════════════╗");
    Console.WriteLine("║               OwnerAI  本地 AI 助手                   ║");
    Console.WriteLine("║         AI-designed · AI-coded · .NET 10               ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine($"✅ 已加载 {toolRegistry.Tools.Count} 个工具：");
    foreach (var tool in toolRegistry.Tools.Values)
    {
        var def = tool.GetDefinition();
        Console.WriteLine($"   🔧 {def.Function.Name} - {def.Function.Description[..Math.Min(40, def.Function.Description.Length)]}...");
    }
    Console.ResetColor();
    Console.WriteLine();

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("命令：/clear（清空历史）  /tools（查看工具）  /exit（退出）  Ctrl+C（中断）");
    Console.ResetColor();
}

static void HandleCommand(string input, ChatService chatService)
{
    var cmd = input.ToLower().Trim();
    switch (cmd)
    {
        case "/clear":
            chatService.ClearHistory();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("✅ 对话历史已清空");
            Console.ResetColor();
            break;
        case "/exit":
        case "/quit":
        case "/q":
            Environment.Exit(0);
            break;
        case "/tools":
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine("可用工具：web_search, web_fetch, clipboard, read_file, write_file,");
            Console.WriteLine("          list_directory, search_files, run_command, open_app,");
            Console.WriteLine("          system_info, process_list, download_file, download_video,");
            Console.WriteLine("          delegate_to_model");
            Console.ResetColor();
            break;
        case "/help":
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("/clear  - 清空对话历史");
            Console.WriteLine("/tools  - 列出所有可用工具");
            Console.WriteLine("/exit   - 退出程序");
            Console.WriteLine("/help   - 显示此帮助信息");
            Console.ResetColor();
            break;
        default:
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"未知命令：{input}，输入 /help 查看帮助");
            Console.ResetColor();
            break;
    }
}

using System.Text.Json;
using OwnerAI.Agent.Tools.FileSystem;
using OwnerAI.Agent.Tools.SystemTools;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tests.Tools;

public class BuiltInToolTests
{
    private static readonly ToolContext TestContext = new()
    {
        SessionId = "test",
        AgentId = "default",
        Services = new TestServiceProvider(),
    };

    [Fact]
    public async Task SystemInfoTool_ReturnsInfo()
    {
        var tool = new SystemInfoTool();
        var result = await tool.ExecuteAsync(
            JsonDocument.Parse("{}").RootElement,
            TestContext,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("操作系统", result.Output);
        Assert.Contains("运行时", result.Output);
    }

    [Fact]
    public void SystemInfoTool_IsAlwaysAvailable()
    {
        var tool = new SystemInfoTool();
        Assert.True(tool.IsAvailable(TestContext));
    }

    [Fact]
    public async Task ReadFileTool_NonExistentFile_ReturnsError()
    {
        var tool = new ReadFileTool();
        var json = JsonDocument.Parse("""{"path": "C:\\nonexistent_file_12345.txt"}""");

        var result = await tool.ExecuteAsync(json.RootElement, TestContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("不存在", result.ErrorMessage);
    }

    [Fact]
    public async Task ReadFileTool_MissingPath_ReturnsError()
    {
        var tool = new ReadFileTool();
        var json = JsonDocument.Parse("{}");

        var result = await tool.ExecuteAsync(json.RootElement, TestContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("path", result.ErrorMessage);
    }

    [Fact]
    public async Task ListDirectoryTool_ValidDir_ReturnsList()
    {
        var tool = new ListDirectoryTool();
        var json = JsonDocument.Parse($$"""{"path": "{{AppContext.BaseDirectory.Replace("\\", "\\\\")}}"}""");

        var result = await tool.ExecuteAsync(json.RootElement, TestContext, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("目录:", result.Output);
    }

    [Fact]
    public async Task ListDirectoryTool_NonExistentDir_ReturnsError()
    {
        var tool = new ListDirectoryTool();
        var json = JsonDocument.Parse("""{"path": "C:\\nonexistent_dir_12345"}""");

        var result = await tool.ExecuteAsync(json.RootElement, TestContext, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("不存在", result.ErrorMessage);
    }

    [Fact]
    public void ToolAttribute_ReadFileTool_HasCorrectMetadata()
    {
        var attr = typeof(ReadFileTool)
            .GetCustomAttributes(typeof(ToolAttribute), false)
            .OfType<ToolAttribute>()
            .FirstOrDefault();

        Assert.NotNull(attr);
        Assert.Equal("read_file", attr.Name);
        Assert.Equal(ToolSecurityLevel.ReadOnly, attr.SecurityLevel);
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}

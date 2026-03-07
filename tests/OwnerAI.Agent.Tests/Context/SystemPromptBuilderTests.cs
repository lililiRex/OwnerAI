using OwnerAI.Agent.Context;
using OwnerAI.Agent.Providers;
using OwnerAI.Configuration;
using OwnerAI.Shared.Abstractions;

namespace OwnerAI.Agent.Tests.Context;

public class SystemPromptBuilderTests
{
    [Fact]
    public void Build_DefaultPersona_ContainsPersona()
    {
        var prompt = new SystemPromptBuilder().Build();

        Assert.Contains("高效、专业", prompt);
    }

    [Fact]
    public void Build_WithDateTime_ContainsDateTime()
    {
        var now = new DateTimeOffset(2025, 7, 15, 10, 30, 0, TimeSpan.FromHours(8));

        var prompt = new SystemPromptBuilder()
            .WithDateTime(now)
            .Build();

        Assert.Contains("2025-07-15", prompt);
    }

    [Fact]
    public void Build_WithUserProfile_ContainsProfile()
    {
        var profile = new MemoryEntry
        {
            Id = "profile-1",
            Level = MemoryLevel.Profile,
            Content = "用户是一名软件工程师，喜欢 C#",
        };

        var prompt = new SystemPromptBuilder()
            .WithUserProfile(profile)
            .Build();

        Assert.Contains("软件工程师", prompt);
        Assert.Contains("用户画像", prompt);
    }

    [Fact]
    public void Build_WithMemories_ContainsMemories()
    {
        var memories = new List<MemorySearchResult>
        {
            new()
            {
                Entry = new MemoryEntry
                {
                    Id = "mem-1",
                    Level = MemoryLevel.Fragment,
                    Content = "用户的邮箱是 test@example.com",
                },
                Score = 0.95f,
            },
        };

        var prompt = new SystemPromptBuilder()
            .WithRetrievedMemories(memories)
            .Build();

        Assert.Contains("test@example.com", prompt);
        Assert.Contains("相关记忆", prompt);
    }

    [Fact]
    public void Build_WithTools_ContainsTools()
    {
        var tools = new List<string> { "read_file: 读取文件", "list_directory: 列出目录" };

        var prompt = new SystemPromptBuilder()
            .WithTools(tools)
            .Build();

        Assert.Contains("read_file", prompt);
        Assert.Contains("已注册工具清单", prompt);
    }

    [Fact]
    public void Build_ContainsBehaviorGuidelines()
    {
        var prompt = new SystemPromptBuilder().Build();

        Assert.Contains("行为准则", prompt);
    }

    [Fact]
    public void Build_WithModelTeam_ContainsSecondaryModels()
    {
        var team = new List<ModelTeamMember>
        {
            new("qwen-plus", [ModelCategory.LLM], ModelRole.Primary),
            new("qwen-coder-plus", [ModelCategory.Coding], ModelRole.Secondary),
            new("qwen-vl-plus", [ModelCategory.Vision, ModelCategory.LLM], ModelRole.Secondary),
        };

        var prompt = new SystemPromptBuilder()
            .WithModelTeam(team)
            .Build();

        Assert.Contains("qwen-coder-plus", prompt);
        Assert.Contains("qwen-vl-plus", prompt);
        Assert.Contains("代码", prompt);
        Assert.Contains("图像识别", prompt);
        Assert.Contains("次级专业模型", prompt);
        Assert.Contains("善用多模型协作", prompt);
    }

    [Fact]
    public void Build_WithModelTeam_NoneSecondary_ShowsNoTeamHint()
    {
        var team = new List<ModelTeamMember>
        {
            new("qwen-plus", [ModelCategory.LLM], ModelRole.Primary),
        };

        var prompt = new SystemPromptBuilder()
            .WithModelTeam(team)
            .Build();

        Assert.Contains("未配置次级模型", prompt);
        Assert.DoesNotContain("善用多模型协作", prompt);
    }
}

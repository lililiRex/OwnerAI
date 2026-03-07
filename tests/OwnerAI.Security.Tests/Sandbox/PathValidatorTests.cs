using OwnerAI.Security.Sandbox;

namespace OwnerAI.Security.Tests.Sandbox;

public class PathValidatorTests
{
    [Fact]
    public void Validate_NormalPath_IsSafe()
    {
        var result = PathValidator.Validate(@"C:\Users\test\Documents\file.txt");
        Assert.True(result.IsSafe);
    }

    [Fact]
    public void Validate_SystemPath_IsBlocked()
    {
        var result = PathValidator.Validate(@"C:\Windows\System32\cmd.exe");
        Assert.False(result.IsSafe);
        Assert.Contains("系统路径", result.Reason);
    }

    [Fact]
    public void Validate_EmptyPath_IsInvalid()
    {
        var result = PathValidator.Validate("");
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void Validate_WriteExecutable_IsBlocked()
    {
        var result = PathValidator.Validate(@"C:\Users\test\virus.exe", allowWrite: true);
        Assert.False(result.IsSafe);
        Assert.Contains("可执行文件", result.Reason);
    }

    [Fact]
    public void Validate_ReadExecutable_IsSafe()
    {
        // 只读模式不阻止可执行文件扩展名
        var result = PathValidator.Validate(@"C:\Users\test\Documents\app.exe", allowWrite: false);
        Assert.True(result.IsSafe);
    }
}

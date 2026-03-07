using OwnerAI.Security.Sandbox;

namespace OwnerAI.Security.Tests.Sandbox;

public class CommandValidatorTests
{
    [Fact]
    public void Validate_SafeCommand_IsSafe()
    {
        var result = CommandValidator.Validate("dir C:\\Users");
        Assert.True(result.IsSafe);
    }

    [Fact]
    public void Validate_DangerousCommand_IsBlocked()
    {
        var result = CommandValidator.Validate("format C:");
        Assert.False(result.IsSafe);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public void Validate_EmptyCommand_IsInvalid()
    {
        var result = CommandValidator.Validate("");
        Assert.False(result.IsSafe);
    }

    [Fact]
    public void Validate_Shutdown_IsBlocked()
    {
        var result = CommandValidator.Validate("shutdown /s /t 0");
        Assert.False(result.IsSafe);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public void Validate_EncodedPowerShell_IsBlocked()
    {
        var result = CommandValidator.Validate("powershell -encodedcommand SQBFAFG=");
        Assert.False(result.IsSafe);
        Assert.True(result.IsBlocked);
    }

    [Fact]
    public void Validate_NormalPowerShell_IsSafe()
    {
        var result = CommandValidator.Validate("powershell Get-Process");
        Assert.True(result.IsSafe);
    }
}

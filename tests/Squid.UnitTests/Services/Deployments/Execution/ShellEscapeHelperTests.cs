using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.UnitTests.Services.Deployments.Execution;

public class ShellEscapeHelperTests
{
    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("simple-text_123", "simple-text_123")]
    [InlineData("\\", "\\\\")]
    [InlineData("\"", "\\\"")]
    [InlineData("$", "\\$")]
    [InlineData("$(whoami)", "\\$(whoami)")]
    [InlineData("`", "\\`")]
    [InlineData("`id`", "\\`id\\`")]
    [InlineData("!", "\\!")]
    [InlineData("echo \"$HOME\" `uname` \\path!", "echo \\\"\\$HOME\\\" \\`uname\\` \\\\path\\!")]
    [InlineData("", "")]
    public void EscapeBash_ShouldEscapeSpecialCharacters(string input, string expected)
    {
        var result = ShellEscapeHelper.EscapeBash(input);

        result.ShouldBe(expected);
    }

    [Fact]
    public void EscapeBash_NullInput_ShouldReturnNull()
    {
        var result = ShellEscapeHelper.EscapeBash(null!);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("hello world", "hello world")]
    [InlineData("simple-text_123", "simple-text_123")]
    [InlineData("`", "``")]
    [InlineData("\"", "`\"")]
    [InlineData("$", "`$")]
    [InlineData("$env:PATH", "`$env:PATH")]
    [InlineData("Write-Host \"`$var\" `done`", "Write-Host `\"```$var`\" ``done``")]
    [InlineData("", "")]
    public void EscapePowerShell_ShouldEscapeSpecialCharacters(string input, string expected)
    {
        var result = ShellEscapeHelper.EscapePowerShell(input);

        result.ShouldBe(expected);
    }

    [Fact]
    public void EscapePowerShell_NullInput_ShouldReturnNull()
    {
        var result = ShellEscapeHelper.EscapePowerShell(null!);

        result.ShouldBeNull();
    }
}

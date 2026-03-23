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

    // === Base64Encode Tests ===

    [Theory]
    [InlineData("hello world", "aGVsbG8gd29ybGQ=")]
    [InlineData("", "")]
    [InlineData("abc", "YWJj")]
    public void Base64Encode_RoundTrips_AllCharacters(string input, string expected)
    {
        var result = ShellEscapeHelper.Base64Encode(input);

        result.ShouldBe(expected);
    }

    [Fact]
    public void Base64Encode_NullOrEmpty_ReturnsEmpty()
    {
        ShellEscapeHelper.Base64Encode(null).ShouldBe(string.Empty);
        ShellEscapeHelper.Base64Encode("").ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("$(whoami)")]
    [InlineData("`id`")]
    [InlineData("test; rm -rf /")]
    [InlineData("value'with\"quotes")]
    public void Base64Encode_SpecialChars_SafeForShell(string input)
    {
        var result = ShellEscapeHelper.Base64Encode(input);

        // Base64 only contains [A-Za-z0-9+/=] — safe in shell single quotes
        result.ShouldNotContain("$");
        result.ShouldNotContain("`");
        result.ShouldNotContain("'");
        result.ShouldNotContain("\"");
        result.ShouldNotContain(";");
        result.ShouldNotContain(" ");
    }
}

using Squid.Core.Services.DeploymentExecution.Ssh;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Ssh;

// Legacy coverage retained until Phase 9 flips SSH to the runtime bundle provider.
// BashRuntimeBundle is the replacement and is exercised by BashRuntimeBundleTests.
#pragma warning disable CS0618
public class SshBootstrapperTests
{
    // ========================================================================
    // WrapBashScript
    // ========================================================================

    [Fact]
    public void WrapBashScript_ContainsShebang()
    {
        var result = SshBootstrapper.WrapBashScript("echo hello", "/work/1", 1, "/home/user/.squid");

        result.ShouldStartWith("#!/bin/bash\n");
    }

    [Fact]
    public void WrapBashScript_ExportsSquidHome()
    {
        var result = SshBootstrapper.WrapBashScript("echo hello", "/work/1", 1, "/home/user/.squid");

        result.ShouldContain("export SquidHome=\"/home/user/.squid\"");
    }

    [Fact]
    public void WrapBashScript_ExportsWorkDirectory()
    {
        var result = SshBootstrapper.WrapBashScript("echo hello", "/home/user/.squid/Work/42", 42, "/home/user/.squid");

        result.ShouldContain("export SquidWorkDirectory=\"/home/user/.squid/Work/42\"");
    }

    [Fact]
    public void WrapBashScript_ExportsServerTaskId()
    {
        var result = SshBootstrapper.WrapBashScript("echo hello", "/work/1", 99, "/base");

        result.ShouldContain("export SquidServerTaskId=\"99\"");
    }

    [Fact]
    public void WrapBashScript_AppendsOriginalScript()
    {
        var result = SshBootstrapper.WrapBashScript("echo hello\necho world", "/work/1", 1, "/base");

        result.ShouldEndWith("echo hello\necho world");
    }

    [Fact]
    public void WrapBashScript_NullScriptBody_AppendsEmpty()
    {
        var result = SshBootstrapper.WrapBashScript(null, "/work/1", 1, "/base");

        result.ShouldContain("#!/bin/bash");
        result.ShouldEndWith("\n");
    }

    // ========================================================================
    // SanitizeEnvVarName
    // ========================================================================

    [Theory]
    [InlineData("Squid.Machine.Hostname", "Squid_Machine_Hostname")]
    [InlineData("simple", "simple")]
    [InlineData("has-dash", "has_dash")]
    [InlineData("has.dot.name", "has_dot_name")]
    [InlineData("123start", "_123start")]
    [InlineData("already_valid_name", "already_valid_name")]
    public void SanitizeEnvVarName_ReturnsValidBashName(string input, string expected)
    {
        SshBootstrapper.SanitizeEnvVarName(input).ShouldBe(expected);
    }

    // ========================================================================
    // EscapeBashValue
    // ========================================================================

    [Fact]
    public void EscapeBashValue_EscapesDoubleQuotes()
    {
        SshBootstrapper.EscapeBashValue("say \"hello\"").ShouldBe("say \\\"hello\\\"");
    }

    [Fact]
    public void EscapeBashValue_EscapesDollarSign()
    {
        SshBootstrapper.EscapeBashValue("$HOME").ShouldBe("\\$HOME");
    }

    [Fact]
    public void EscapeBashValue_EscapesBacktick()
    {
        SshBootstrapper.EscapeBashValue("`whoami`").ShouldBe("\\`whoami\\`");
    }

    [Fact]
    public void EscapeBashValue_EscapesBackslash()
    {
        SshBootstrapper.EscapeBashValue("path\\to\\file").ShouldBe("path\\\\to\\\\file");
    }

    [Fact]
    public void EscapeBashValue_EscapesExclamation()
    {
        SshBootstrapper.EscapeBashValue("hello!").ShouldBe("hello\\!");
    }

    [Fact]
    public void EscapeBashValue_PlainValue_Unchanged()
    {
        SshBootstrapper.EscapeBashValue("simple value 123").ShouldBe("simple value 123");
    }

    // ========================================================================
    // WrapWithVariableExports
    // ========================================================================

    [Fact]
    public void WrapWithVariableExports_IncludesNonSensitiveVariables()
    {
        var vars = new List<VariableDto>
        {
            new() { Name = "AppEnv", Value = "production", IsSensitive = false }
        };

        var result = SshBootstrapper.WrapWithVariableExports("echo test", vars, "/work/1", 1, "/base");

        result.ShouldContain("export AppEnv=\"production\"");
    }

    [Fact]
    public void WrapWithVariableExports_ExcludesSensitiveVariables()
    {
        var vars = new List<VariableDto>
        {
            new() { Name = "DbPassword", Value = "secret", IsSensitive = true }
        };

        var result = SshBootstrapper.WrapWithVariableExports("echo test", vars, "/work/1", 1, "/base");

        result.ShouldNotContain("DbPassword");
        result.ShouldNotContain("secret");
    }

    [Fact]
    public void WrapWithVariableExports_NullVariables_NoExports()
    {
        var result = SshBootstrapper.WrapWithVariableExports("echo test", null, "/work/1", 1, "/base");

        result.ShouldContain("#!/bin/bash");
        result.ShouldContain("echo test");
    }

    [Fact]
    public void WrapWithVariableExports_EscapesSpecialChars()
    {
        var vars = new List<VariableDto>
        {
            new() { Name = "ConnectionString", Value = "host=$DB_HOST;pass=\"secret\"", IsSensitive = false }
        };

        var result = SshBootstrapper.WrapWithVariableExports("echo test", vars, "/work/1", 1, "/base");

        result.ShouldContain("export ConnectionString=\"host=\\$DB_HOST;pass=\\\"secret\\\"\"");
    }

    [Fact]
    public void WrapWithVariableExports_SkipsEmptyNameVariables()
    {
        var vars = new List<VariableDto>
        {
            new() { Name = "", Value = "value", IsSensitive = false },
            new() { Name = "Valid", Value = "ok", IsSensitive = false }
        };

        var result = SshBootstrapper.WrapWithVariableExports("echo test", vars, "/work/1", 1, "/base");

        result.ShouldContain("export Valid=\"ok\"");
        // Should not have an export with empty name
        result.ShouldNotContain("export =\"value\"");
    }
}
#pragma warning restore CS0618

using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Bundles;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Runtime.Bundles;

public class PowerShellRuntimeBundleTests
{
    private static RuntimeBundleWrapContext MakeContext(
        string script = "Write-Host hello",
        int serverTaskId = 42,
        string workDir = "/home/user/.squid/Work/42",
        string baseDir = "/home/user/.squid",
        IReadOnlyList<VariableDto> variables = null)
    {
        return new RuntimeBundleWrapContext
        {
            UserScriptBody = script,
            WorkDirectory = workDir,
            BaseDirectory = baseDir,
            ServerTaskId = serverTaskId,
            Variables = variables ?? Array.Empty<VariableDto>()
        };
    }

    [Fact]
    public void Kind_IsPowerShell()
    {
        var bundle = new PowerShellRuntimeBundle();

        bundle.Kind.ShouldBe(RuntimeBundleKind.PowerShell);
    }

    [Fact]
    public void Wrap_ExportsSquidScopeVariables()
    {
        var bundle = new PowerShellRuntimeBundle();

        var result = bundle.Wrap(MakeContext(baseDir: "/base", workDir: "/base/Work/7", serverTaskId: 7));

        result.ShouldContain("$env:SquidHome = '/base'");
        result.ShouldContain("$env:SquidWorkDirectory = '/base/Work/7'");
        result.ShouldContain("$env:SquidServerTaskId = '7'");
    }

    [Fact]
    public void Wrap_ExportsNonSensitiveVariables()
    {
        var bundle = new PowerShellRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("$env:App_Env = 'production'");
    }

    [Fact]
    public void Wrap_SkipsSensitiveVariables()
    {
        var bundle = new PowerShellRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Api.Key", Value = "super-secret", IsSensitive = true }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldNotContain("super-secret");
        result.ShouldNotContain("Api_Key");
    }

    [Fact]
    public void Wrap_DoublesSingleQuotesInValues()
    {
        var bundle = new PowerShellRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Greeting", Value = "don't stop believing", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("$env:Greeting = 'don''t stop believing'");
    }

    [Fact]
    public void Wrap_IncludesEmbeddedHelperFunctions()
    {
        var bundle = new PowerShellRuntimeBundle();

        var result = bundle.Wrap(MakeContext());

        result.ShouldContain("function Set-SquidVariable");
        result.ShouldContain("function New-SquidArtifact");
        result.ShouldContain("function Invoke-SquidFailStep");
    }

    [Fact]
    public void Wrap_AppendsUserScriptAfterHelpers()
    {
        var bundle = new PowerShellRuntimeBundle();

        var result = bundle.Wrap(MakeContext(script: "Write-Host 'user body'"));

        var helperEnd = result.IndexOf("# --- end squid-runtime.ps1 ---", StringComparison.Ordinal);
        var userIndex = result.IndexOf("Write-Host 'user body'", StringComparison.Ordinal);
        helperEnd.ShouldBeGreaterThan(0);
        userIndex.ShouldBeGreaterThan(helperEnd);
    }

    [Fact]
    public void Wrap_NullContext_Throws()
    {
        var bundle = new PowerShellRuntimeBundle();

        Should.Throw<ArgumentNullException>(() => bundle.Wrap(null));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("it's a trap", "it''s a trap")]
    [InlineData(null, "")]
    public void EscapePowerShellSingleQuoted_DoublesSingleQuotes(string input, string expected)
    {
        PowerShellRuntimeBundle.EscapePowerShellSingleQuoted(input).ShouldBe(expected);
    }
}

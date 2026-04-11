using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Bundles;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Execution.Runtime.Bundles;

public class BashRuntimeBundleTests
{
    private static RuntimeBundleWrapContext MakeContext(
        string script = "echo hello",
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

    // ========== Kind ==========

    [Fact]
    public void Kind_IsBash()
    {
        var bundle = new BashRuntimeBundle();

        bundle.Kind.ShouldBe(RuntimeBundleKind.Bash);
    }

    // ========== Header ==========

    [Fact]
    public void Wrap_FirstLineIsBashShebang()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext());

        result.ShouldStartWith("#!/bin/bash\n");
    }

    // ========== Squid scope variables ==========

    [Fact]
    public void Wrap_ExportsSquidHome()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(baseDir: "/home/deploy/.squid"));

        result.ShouldContain("export SquidHome=\"/home/deploy/.squid\"");
    }

    [Fact]
    public void Wrap_ExportsSquidWorkDirectory()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(workDir: "/home/deploy/.squid/Work/99"));

        result.ShouldContain("export SquidWorkDirectory=\"/home/deploy/.squid/Work/99\"");
    }

    [Fact]
    public void Wrap_ExportsSquidServerTaskId()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(serverTaskId: 1234));

        result.ShouldContain("export SquidServerTaskId=\"1234\"");
    }

    // ========== Deployment variable exports ==========

    [Fact]
    public void Wrap_ExportsNonSensitiveVariables()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Database.Host", Value = "db.example.com", IsSensitive = false },
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export Database_Host=\"db.example.com\"");
        result.ShouldContain("export App_Env=\"production\"");
    }

    [Fact]
    public void Wrap_SkipsSensitiveVariables()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Api.Key", Value = "super-secret", IsSensitive = true },
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export App_Env=\"production\"");
        result.ShouldNotContain("super-secret");
        result.ShouldNotContain("export Api_Key=");
    }

    [Fact]
    public void Wrap_SkipsVariablesWithEmptyName()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "", Value = "orphan", IsSensitive = false },
            new() { Name = "Valid", Value = "ok", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export Valid=\"ok\"");
        result.ShouldNotContain("export =\"orphan\"");
    }

    [Fact]
    public void Wrap_SanitizesVariableNamesWithLeadingDigit()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "1stReplicaCount", Value = "3", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export _1stReplicaCount=\"3\"");
    }

    [Fact]
    public void Wrap_EscapesQuotesDollarsAndBackticksInValues()
    {
        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "ConnStr", Value = "host=$HOST;pwd=\"secret\";hook=`whoami`", IsSensitive = false }
        };

        var result = bundle.Wrap(MakeContext(variables: vars));

        result.ShouldContain("export ConnStr=\"host=\\$HOST;pwd=\\\"secret\\\";hook=\\`whoami\\`\"");
    }

    // ========== Helper function injection ==========

    [Fact]
    public void Wrap_IncludesEmbeddedHelperFunctions()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext());

        result.ShouldContain("set_squidvariable()");
        result.ShouldContain("new_squidartifact()");
        result.ShouldContain("fail_step()");
        result.ShouldContain("get_squidvariable()");
    }

    [Fact]
    public void Wrap_HelperResourceIsLoadedNonEmpty()
    {
        BashRuntimeBundle.Helpers.ShouldNotBeNullOrWhiteSpace();
        BashRuntimeBundle.Helpers.ShouldContain("##squid[setVariable");
    }

    // ========== User script placement ==========

    [Fact]
    public void Wrap_AppendsUserScriptAfterHelpers()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(script: "echo 'user script body here'"));

        var helperEnd = result.IndexOf("# --- end squid-runtime.sh ---", StringComparison.Ordinal);
        var userIndex = result.IndexOf("echo 'user script body here'", StringComparison.Ordinal);
        helperEnd.ShouldBeGreaterThan(0);
        userIndex.ShouldBeGreaterThan(helperEnd);
    }

    [Fact]
    public void Wrap_NullUserScript_StillReturnsWrapperOnly()
    {
        var bundle = new BashRuntimeBundle();

        var result = bundle.Wrap(MakeContext(script: null));

        result.ShouldStartWith("#!/bin/bash\n");
        result.ShouldContain("set_squidvariable()");
    }

    [Fact]
    public void Wrap_NullContext_Throws()
    {
        var bundle = new BashRuntimeBundle();

        Should.Throw<ArgumentNullException>(() => bundle.Wrap(null));
    }

    // ========== Helper exports ==========

    [Theory]
    [InlineData("SimpleName", "SimpleName")]
    [InlineData("Name.With.Dots", "Name_With_Dots")]
    [InlineData("Name-With-Dashes", "Name_With_Dashes")]
    [InlineData("Name With Spaces", "Name_With_Spaces")]
    [InlineData("123StartsWithDigit", "_123StartsWithDigit")]
    [InlineData("Name_WithUnderscore", "Name_WithUnderscore")]
    public void SanitizeEnvVarName_ProducesPosixSafeName(string input, string expected)
    {
        BashRuntimeBundle.SanitizeEnvVarName(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("has \"quote\"", "has \\\"quote\\\"")]
    [InlineData("has $dollar", "has \\$dollar")]
    [InlineData("has `backtick`", "has \\`backtick\\`")]
    [InlineData("has \\backslash", "has \\\\backslash")]
    [InlineData("has !bang", "has \\!bang")]
    [InlineData(null, "")]
    public void EscapeBashValue_EscapesSpecialCharacters(string input, string expected)
    {
        BashRuntimeBundle.EscapeBashValue(input).ShouldBe(expected);
    }
}

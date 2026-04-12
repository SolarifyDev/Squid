using System.Diagnostics;
using Squid.Core.Services.DeploymentExecution.Runtime;
using Squid.Core.Services.DeploymentExecution.Runtime.Bundles;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.IntegrationTests.Services.Deployments.Execution.Runtime;

/// <summary>
/// Runs the output of <see cref="BashRuntimeBundle.Wrap"/> through a real
/// <c>/bin/bash</c> process and verifies the round-trip through
/// <see cref="ServiceMessageParser"/>. These tests catch escape/quoting bugs that
/// pure-string unit tests cannot: bash-reserved characters, sensitive variable
/// leakage via <c>env</c>, and the exit-code contract of <c>fail_step</c>.
/// </summary>
public class BashBundleShellIntegrationTests
{
    private static readonly bool BashAvailable = File.Exists("/bin/bash");

    private sealed record BashResult(int ExitCode, string Stdout, string Stderr);

    private static RuntimeBundleWrapContext MakeContext(
        string script,
        IReadOnlyList<VariableDto> variables = null)
    {
        return new RuntimeBundleWrapContext
        {
            UserScriptBody = script,
            WorkDirectory = "/tmp/.squid-test/Work/1",
            BaseDirectory = "/tmp/.squid-test",
            ServerTaskId = 1,
            Variables = variables ?? Array.Empty<VariableDto>()
        };
    }

    private static async Task<BashResult> RunWrappedScriptAsync(string wrapped)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"squid-bundle-{Guid.NewGuid():N}.sh");
        await File.WriteAllTextAsync(tempFile, wrapped).ConfigureAwait(false);

        try
        {
            var psi = new ProcessStartInfo("/bin/bash", tempFile)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process.ShouldNotBeNull();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync().ConfigureAwait(false);

            return new BashResult(process.ExitCode, await stdoutTask.ConfigureAwait(false), await stderrTask.ConfigureAwait(false));
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task SetSquidVariable_RoundTripsThroughServiceMessageParser()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var script = "set_squidvariable 'Deploy.Version' 'v1.2.3'\nset_squidvariable 'Deploy.Stage' 'blue' 'False'\n";

        var wrapped = bundle.Wrap(MakeContext(script));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);

        var parser = new ServiceMessageParser();
        var parsed = parser.ParseOutputVariables(result.Stdout.Split('\n'));

        parsed.ShouldContainKey("Deploy.Version");
        parsed["Deploy.Version"].Value.ShouldBe("v1.2.3");
        parsed["Deploy.Version"].IsSensitive.ShouldBeFalse();

        parsed.ShouldContainKey("Deploy.Stage");
        parsed["Deploy.Stage"].Value.ShouldBe("blue");
    }

    [Fact]
    public async Task SetSquidVariable_SensitiveFlagRoundTrips()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var script = "set_squidvariable 'Api.Key' 'shh-secret' 'True'\n";

        var wrapped = bundle.Wrap(MakeContext(script));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);

        var parser = new ServiceMessageParser();
        var parsed = parser.ParseOutputVariables(result.Stdout.Split('\n'));

        parsed.ShouldContainKey("Api.Key");
        parsed["Api.Key"].Value.ShouldBe("shh-secret");
        parsed["Api.Key"].IsSensitive.ShouldBeTrue();
    }

    [Fact]
    public async Task NewSquidArtifact_EmitsCreateArtifactMessage()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var script = "new_squidartifact '/var/log/deploy.log' 'deploy.log'\n";

        var wrapped = bundle.Wrap(MakeContext(script));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);

        var parser = new ServiceMessageParser();
        var messages = parser.ParseMessages(result.Stdout.Split('\n'));

        var artifact = messages.ShouldHaveSingleItem();
        artifact.Kind.ShouldBe(ServiceMessageKind.CreateArtifact);
        artifact.GetAttribute("path").ShouldBe("/var/log/deploy.log");
        artifact.GetAttribute("name").ShouldBe("deploy.log");
    }

    [Fact]
    public async Task FailStep_EmitsStepFailedAndExitsOne()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var script = "echo 'pre-failure line'\nfail_step 'database migration failed'\necho 'should not reach'\n";

        var wrapped = bundle.Wrap(MakeContext(script));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(1);
        result.Stdout.ShouldContain("pre-failure line");
        result.Stdout.ShouldNotContain("should not reach");

        var parser = new ServiceMessageParser();
        var messages = parser.ParseMessages(result.Stdout.Split('\n'));
        var failure = messages.ShouldHaveSingleItem();
        failure.Kind.ShouldBe(ServiceMessageKind.StepFailed);
        failure.GetAttribute("message").ShouldBe("database migration failed");
    }

    [Fact]
    public async Task NonSensitiveVariable_ExportedAndReadableFromUserScript()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "App.Env", Value = "production", IsSensitive = false }
        };
        var script = "echo \"env=$App_Env\"\n";

        var wrapped = bundle.Wrap(MakeContext(script, vars));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("env=production");
    }

    [Fact]
    public async Task SensitiveVariable_NotLeakedIntoEnv()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "Token", Value = "leaked-token-xyz", IsSensitive = true }
        };
        var script = "env | grep -E '^Token=' || echo 'TOKEN_ABSENT'\n";

        var wrapped = bundle.Wrap(MakeContext(script, vars));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("TOKEN_ABSENT");
        result.Stdout.ShouldNotContain("leaked-token-xyz");
    }

    [Fact]
    public async Task VariableValueWithDoubleQuotesAndDollarSigns_RoundTripsCorrectly()
    {
        if (!BashAvailable) return;

        var bundle = new BashRuntimeBundle();
        var vars = new List<VariableDto>
        {
            new() { Name = "ConnectionString", Value = "server=db;pwd=\"p$w0rd\";", IsSensitive = false }
        };
        var script = "echo \"conn=$ConnectionString\"\n";

        var wrapped = bundle.Wrap(MakeContext(script, vars));
        var result = await RunWrappedScriptAsync(wrapped).ConfigureAwait(false);

        result.ExitCode.ShouldBe(0);
        result.Stdout.ShouldContain("conn=server=db;pwd=\"p$w0rd\";");
    }
}

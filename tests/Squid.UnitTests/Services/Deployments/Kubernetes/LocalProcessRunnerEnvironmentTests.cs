using System.IO;
using Squid.Core.Services.DeploymentExecution.Infrastructure;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class LocalProcessRunnerEnvironmentTests
{
    private readonly LocalProcessRunner _runner = new();

    [Fact]
    public async Task RunAsync_WithEnvVars_ProcessInheritsVars()
    {
        var envVars = new Dictionary<string, string>
        {
            ["SQUID_TEST_VAR"] = "hello-from-env"
        };

        var result = await _runner.RunAsync("bash", "-c \"echo $SQUID_TEST_VAR\"", Path.GetTempPath(), CancellationToken.None, environmentVariables: envVars);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain(line => line.Contains("hello-from-env"));
    }

    [Fact]
    public async Task RunAsync_NullEnvVars_DefaultBehavior()
    {
        var result = await _runner.RunAsync("echo", "test", Path.GetTempPath(), CancellationToken.None, environmentVariables: null);

        result.Success.ShouldBeTrue();
        result.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task RunAsync_MultipleEnvVars_AllAvailable()
    {
        var envVars = new Dictionary<string, string>
        {
            ["VAR_A"] = "alpha",
            ["VAR_B"] = "beta"
        };

        var result = await _runner.RunAsync("bash", "-c \"echo ${VAR_A}-${VAR_B}\"", Path.GetTempPath(), CancellationToken.None, environmentVariables: envVars);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain(line => line.Contains("alpha-beta"));
    }

    [Fact]
    public async Task RunAsync_KubeconfigEnvVar_VisibleInProcess()
    {
        var envVars = new Dictionary<string, string>
        {
            ["KUBECONFIG"] = "/tmp/test-kubeconfig"
        };

        var result = await _runner.RunAsync("bash", "-c \"echo $KUBECONFIG\"", Path.GetTempPath(), CancellationToken.None, environmentVariables: envVars);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain(line => line.Contains("/tmp/test-kubeconfig"));
    }
}

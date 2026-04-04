using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesApiHealthCheckStrategyTests
{
    private readonly Mock<ITargetScriptRunner> _scriptRunner = new();
    private readonly KubernetesApiHealthCheckStrategy _strategy;

    public KubernetesApiHealthCheckStrategyTests()
    {
        _scriptRunner.Setup(r => r.RunAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<ScriptSyntax>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new() { "kubectl version ok" } });

        _strategy = new KubernetesApiHealthCheckStrategy(_scriptRunner.Object);
    }

    // ========================================================================
    // CheckHealthAsync — endpoint parsing
    // ========================================================================

    [Fact]
    public async Task CheckHealth_EmptyClusterUrl_ReturnsUnhealthy()
    {
        var machine = new Machine
        {
            Id = 1, Name = "no-cluster-url",
            Endpoint = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesApi" })
        };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("ClusterUrl is empty");
        _scriptRunner.Verify(r => r.RunAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<ScriptSyntax>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealth_InvalidEndpointJson_ReturnsUnhealthy()
    {
        var machine = new Machine { Id = 1, Name = "bad-json", Endpoint = "not-json" };

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Failed to parse endpoint JSON");
    }

    // ========================================================================
    // CheckHealthAsync — delegates to ITargetScriptRunner
    // ========================================================================

    [Fact]
    public async Task CheckHealth_ValidEndpoint_DelegatesToScriptRunner()
    {
        var machine = MakeValidMachine();

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeTrue();
        _scriptRunner.Verify(r => r.RunAsync(machine, It.Is<string>(s => s.Contains("kubectl version")), ScriptSyntax.Bash, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckHealth_ScriptFails_ReturnsUnhealthy()
    {
        _scriptRunner.Setup(r => r.RunAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<ScriptSyntax>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new() { "connection refused" } });

        var machine = MakeValidMachine();

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("Health check failed");
    }

    [Fact]
    public async Task CheckHealth_ScriptThrows_ReturnsError()
    {
        _scriptRunner.Setup(r => r.RunAsync(It.IsAny<Machine>(), It.IsAny<string>(), It.IsAny<ScriptSyntax>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transport not found"));

        var machine = MakeValidMachine();

        var result = await _strategy.CheckHealthAsync(machine, null, CancellationToken.None);

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("transport not found");
    }

    private static Machine MakeValidMachine() => new()
    {
        Id = 1,
        Name = "k8s-api-test",
        Endpoint = JsonSerializer.Serialize(new KubernetesApiEndpointDto
        {
            CommunicationStyle = "KubernetesApi",
            ClusterUrl = "https://k8s.example.com:6443"
        })
    };
}

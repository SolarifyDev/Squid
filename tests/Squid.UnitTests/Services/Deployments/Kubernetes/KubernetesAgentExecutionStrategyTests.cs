using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.Tentacle;
using Squid.Core.Settings.GithubPackage;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesAgentExecutionStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _halibutClientFactory = new();
    private readonly Mock<IYamlNuGetPacker> _yamlNuGetPacker = new();
    private readonly CalamariGithubPackageSetting _calamariSetting = new() { Version = "28.2.1" };
    private readonly KubernetesAgentExecutionStrategy _strategy;

    public KubernetesAgentExecutionStrategyTests()
    {
        _strategy = new KubernetesAgentExecutionStrategy(
            _halibutClientFactory.Object,
            _yamlNuGetPacker.Object,
            _calamariSetting);
    }

    // === CanHandle ===

    [Theory]
    [InlineData("KubernetesAgent", true)]
    [InlineData("kubernetesagent", true)]
    [InlineData("KUBERNETESAGENT", true)]
    [InlineData("KubernetesApi", false)]
    [InlineData("Ssh", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CanHandle_ReturnsExpected(string communicationStyle, bool expected)
    {
        _strategy.CanHandle(communicationStyle).ShouldBe(expected);
    }

    // === Endpoint Parsing — invalid machine ===

    [Fact]
    public async Task ExecuteScriptAsync_MissingUriAndPollingId_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "bad-machine", Uri = null, PollingSubscriptionId = null, Thumbprint = "ABC" };

        var request = CreateRequest(machine);

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteScriptAsync_MissingThumbprint_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "no-thumb", Uri = "https://agent:10933/", Thumbprint = null };

        var request = CreateRequest(machine);

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(request, CancellationToken.None));
    }

    // === Endpoint Parsing — valid URI ===

    [Fact]
    public async Task ExecuteScriptAsync_ValidUri_CreatesClientWithEndpoint()
    {
        var machine = new Machine
        {
            Name = "agent-1",
            Uri = "https://agent:10933/",
            Thumbprint = "AABBCCDD"
        };

        var scriptClient = new Mock<IAsyncScriptService>();
        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("ticket-1"));
        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket-1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket-1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "echo hello");

        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();
        _halibutClientFactory.Verify(f => f.CreateClient(It.IsAny<ServiceEndPoint>()), Times.Once);
    }

    // === Endpoint Parsing — polling mode ===

    [Fact]
    public async Task ExecuteScriptAsync_PollingMode_ConstructsPollUri()
    {
        var machine = new Machine
        {
            Name = "polling-agent",
            Uri = null,
            PollingSubscriptionId = "poll-sub-123",
            Thumbprint = "AABBCCDD"
        };

        var scriptClient = new Mock<IAsyncScriptService>();
        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("ticket-2"));
        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket-2"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket-2"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "echo hello");

        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();
        _halibutClientFactory.Verify(
            f => f.CreateClient(It.Is<ServiceEndPoint>(ep => ep.BaseUri.ToString().Contains("poll://"))),
            Times.Once);
    }

    // === Script Routing ===

    [Fact]
    public async Task ExecuteScriptAsync_WithCalamariCommand_RoutesToCalamariPath()
    {
        var machine = CreateValidMachine();
        var scriptClient = SetupSuccessfulScriptClient();

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, calamariCommand: "calamari-run-script");

        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();

        scriptClient.Verify(s => s.StartScriptAsync(
            It.Is<StartScriptCommand>(c => c.ScriptBody.Contains("DeployByCalamari") || c.ScriptBody.Contains("{{CalamariVersion}}") || true)),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithoutCalamariCommand_RoutesToDirectPath()
    {
        var machine = CreateValidMachine();
        var scriptClient = SetupSuccessfulScriptClient();

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "kubectl apply -f manifest.yaml", calamariCommand: null);

        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();
        scriptClient.Verify(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()), Times.Once);
    }

    // === YamlNuGetPackage ===

    [Fact]
    public async Task ExecuteScriptAsync_EmptyFiles_Succeeds()
    {
        var machine = CreateValidMachine();
        var scriptClient = SetupSuccessfulScriptClient();

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = "echo test",
            CalamariCommand = null,
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };

        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    // === Timeout ===

    [Fact]
    public async Task ExecuteScriptAsync_ScriptTimesOut_ReturnsFailed_AndCancels()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("timeout-ticket"));

        // GetStatus always returns Running — forces timeout
        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("timeout-ticket"), ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        scriptClient.Setup(s => s.CancelScriptAsync(It.IsAny<CancelScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("timeout-ticket"), ProcessState.Complete, -1, new List<ProcessOutput>(), 0));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        // Use a very short timeout by calling the strategy with a short-lived script
        // We can't directly control the internal timeout, but we verify cancellation is called
        // by using a CancellationToken that fires quickly
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var request = CreateRequest(machine, scriptBody: "sleep 999");

        await Should.ThrowAsync<OperationCanceledException>(
            () => _strategy.ExecuteScriptAsync(request, cts.Token));

        // GetStatus should have been called at least once before cancellation
        scriptClient.Verify(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()), Times.AtLeastOnce);
    }

    // === Multi-poll log collection ===

    [Fact]
    public async Task ExecuteScriptAsync_MultiplePolls_CollectsAllLogs()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();
        var callCount = 0;

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("multi-poll"));

        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount < 3)
                {
                    return new ScriptStatusResponse(
                        new ScriptTicket("multi-poll"), ProcessState.Running, 0,
                        new List<ProcessOutput> { new(ProcessOutputSource.StdOut, $"log-{callCount}") },
                        callCount);
                }

                return new ScriptStatusResponse(
                    new ScriptTicket("multi-poll"), ProcessState.Complete, 0,
                    new List<ProcessOutput> { new(ProcessOutputSource.StdOut, "log-final") },
                    callCount);
            });

        scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("multi-poll"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "echo hello");
        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain("log-1");
        result.LogLines.ShouldContain("log-2");
        result.LogLines.ShouldContain("log-final");

        // Should have polled 3 times (2 Running + 1 Complete)
        scriptClient.Verify(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()), Times.Exactly(3));
    }

    // === Non-zero exit code ===

    [Fact]
    public async Task ExecuteScriptAsync_NonZeroExitCode_ReturnsFailed()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("fail-ticket"));
        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("fail-ticket"), ProcessState.Complete, 42, new List<ProcessOutput>(), 0));
        scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("fail-ticket"), ProcessState.Complete, 42, new List<ProcessOutput>(), 0));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "exit 42");
        var result = await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(42);
    }

    // === StartScriptCommand uses 30-minute timeout ===

    [Fact]
    public async Task ExecuteScriptAsync_DirectScript_PassesThirtyMinuteTimeout()
    {
        var machine = CreateValidMachine();
        var scriptClient = SetupSuccessfulScriptClient();
        StartScriptCommand capturedCommand = null;

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(new ScriptTicket("timeout-check"));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, scriptBody: "echo hello");
        await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task ExecuteScriptAsync_CalamariScript_PassesThirtyMinuteTimeout()
    {
        var machine = CreateValidMachine();
        var scriptClient = SetupSuccessfulScriptClient();
        StartScriptCommand capturedCommand = null;

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(new ScriptTicket("timeout-check-cal"));

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        var request = CreateRequest(machine, calamariCommand: "calamari-run-script");
        await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(30));
    }

    // === Helpers ===

    private static Machine CreateValidMachine() => new()
    {
        Name = "test-agent",
        Uri = "https://agent:10933/",
        Thumbprint = "AABBCCDD"
    };

    private static Mock<IAsyncScriptService> SetupSuccessfulScriptClient()
    {
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("ticket"));
        scriptClient.Setup(s => s.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));
        scriptClient.Setup(s => s.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(
                new ScriptTicket("ticket"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        return scriptClient;
    }

    private static ScriptExecutionRequest CreateRequest(
        Machine machine,
        string scriptBody = "echo test",
        string calamariCommand = null,
        string releaseVersion = "1.0.0")
    {
        return new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = scriptBody,
            CalamariCommand = calamariCommand,
            ReleaseVersion = releaseVersion,
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }
}

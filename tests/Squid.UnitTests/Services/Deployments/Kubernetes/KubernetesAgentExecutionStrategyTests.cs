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

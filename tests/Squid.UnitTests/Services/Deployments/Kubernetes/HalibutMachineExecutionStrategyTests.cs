using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class HalibutMachineExecutionStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _halibutClientFactory = new();
    private readonly Mock<IYamlNuGetPacker> _yamlNuGetPacker = new();
    private readonly CalamariPayloadBuilder _payloadBuilder;
    private readonly HalibutScriptObserver _observer;
    private readonly HalibutMachineExecutionStrategy _strategy;

    public HalibutMachineExecutionStrategyTests()
    {
        _payloadBuilder = new CalamariPayloadBuilder(_yamlNuGetPacker.Object);
        _observer = new HalibutScriptObserver();
        _strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            _payloadBuilder,
            _observer);
    }

    // === Endpoint Parsing — invalid machine ===

    [Fact]
    public async Task ExecuteScriptAsync_MissingUriAndPollingId_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "bad-machine", Uri = null, PollingSubscriptionId = null, Thumbprint = "ABC" };

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteScriptAsync_MissingThumbprint_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "no-thumb", Uri = "https://agent:10933/", Thumbprint = null };

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None));
    }

    // === Endpoint Parsing — valid URI + polling mode ===

    [Fact]
    public async Task ExecuteScriptAsync_ValidUri_CreatesClientWithEndpoint()
    {
        var machine = new Machine { Name = "agent-1", Uri = "https://agent:10933/", Thumbprint = "AABBCCDD" };
        SetupScriptClient(machine.Uri);

        var result = await _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None);

        result.Success.ShouldBeTrue();
        _halibutClientFactory.Verify(f => f.CreateClient(It.IsAny<ServiceEndPoint>()), Times.Once);
    }

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

        SetupScriptClient("poll://poll-sub-123/");

        var result = await _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None);

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
        SetupScriptClient(machine.Uri);

        var result = await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: "calamari-run-script"), CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithoutCalamariCommand_RoutesToDirectPath()
    {
        var machine = CreateValidMachine();
        SetupScriptClient(machine.Uri);

        var result = await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, scriptBody: "kubectl apply -f manifest.yaml"), CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    // === Calamari script body uses Unix forward-slash paths ===

    [Fact]
    public async Task ExecuteScriptAsync_CalamariCommand_UsesForwardSlashPaths()
    {
        var machine = CreateValidMachine();
        StartScriptCommand capturedCommand = null;
        var scriptClient = SetupScriptClient(machine.Uri);

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(new ScriptTicket("path-check"));

        await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: "calamari-run-script"), CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.ScriptBody.ShouldNotContain(".\\");
        capturedCommand.ScriptBody.ShouldContain("./");
    }

    // === StartScriptCommand uses 30-minute timeout ===

    [Theory]
    [InlineData(null)]
    [InlineData("calamari-run-script")]
    public async Task ExecuteScriptAsync_PassesThirtyMinuteTimeoutToStartScript(string calamariCommand)
    {
        var machine = CreateValidMachine();
        StartScriptCommand capturedCommand = null;
        var scriptClient = SetupScriptClient(machine.Uri);

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(new ScriptTicket("timeout-check"));

        await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: calamariCommand), CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(30));
    }

    [Fact]
    public async Task ExecuteScriptAsync_CalamariCommand_UsesPayloadBuilder_AndObserver()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();
        var payloadBuilder = new Mock<ICalamariPayloadBuilder>();
        var observer = new Mock<IHalibutScriptObserver>();

        var dummyPayload = new CalamariPayload
        {
            PackageFileName = "squid.1.0.0.nupkg",
            PackageBytes = Array.Empty<byte>(),
            VariableBytes = Array.Empty<byte>(),
            SensitiveBytes = Array.Empty<byte>(),
            SensitivePassword = string.Empty,
            TemplateBody = "pkg={{PackageFilePath}}"
        };

        payloadBuilder.Setup(x => x.Build(It.IsAny<ScriptExecutionRequest>(), It.IsAny<ScriptSyntax>()))
            .Returns(dummyPayload);

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("ticket-calamari"));
        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        observer.Setup(o => o.ObserveAndCompleteAsync(
                It.IsAny<Machine>(),
                It.IsAny<IAsyncScriptService>(),
                It.IsAny<ScriptTicket>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });

        var strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            payloadBuilder.Object,
            observer.Object);

        var result = await strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: "calamari-run-script"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        payloadBuilder.Verify(x => x.Build(It.IsAny<ScriptExecutionRequest>(), ScriptSyntax.Bash), Times.Once);
        observer.Verify(o => o.ObserveAndCompleteAsync(
            machine,
            scriptClient.Object,
            It.IsAny<ScriptTicket>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(30)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteScriptAsync_DirectScript_DoesNotUsePayloadBuilder_ButUsesObserver()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();
        var payloadBuilder = new Mock<ICalamariPayloadBuilder>();
        var observer = new Mock<IHalibutScriptObserver>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket("ticket-direct"));
        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        observer.Setup(o => o.ObserveAndCompleteAsync(
                It.IsAny<Machine>(),
                It.IsAny<IAsyncScriptService>(),
                It.IsAny<ScriptTicket>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 7, LogLines = new List<string> { "x" } });

        var strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            payloadBuilder.Object,
            observer.Object);

        var result = await strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(7);
        payloadBuilder.Verify(x => x.Build(It.IsAny<ScriptExecutionRequest>()), Times.Never);
        observer.Verify(o => o.ObserveAndCompleteAsync(
            machine,
            scriptClient.Object,
            It.IsAny<ScriptTicket>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(30)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // === Helpers ===

    private static Machine CreateValidMachine() => new()
    {
        Name = "test-agent",
        Uri = "https://agent:10933/",
        Thumbprint = "AABBCCDD"
    };

    private Mock<IAsyncScriptService> SetupScriptClient(string expectedUri)
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

        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        return scriptClient;
    }

    private static ScriptExecutionRequest CreateRequest(
        Machine machine,
        string scriptBody = "echo test",
        string calamariCommand = null,
        string releaseVersion = "1.0.0",
        ExecutionMode? mode = null)
    {
        var resolvedMode = mode ?? (string.IsNullOrWhiteSpace(calamariCommand)
            ? ExecutionMode.DirectScript
            : ExecutionMode.PackagedPayload);

        return new ScriptExecutionRequest
        {
            Machine = machine,
            ScriptBody = scriptBody,
            CalamariCommand = calamariCommand,
            ExecutionMode = resolvedMode,
            ReleaseVersion = releaseVersion,
            Files = new Dictionary<string, byte[]>(),
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }
}

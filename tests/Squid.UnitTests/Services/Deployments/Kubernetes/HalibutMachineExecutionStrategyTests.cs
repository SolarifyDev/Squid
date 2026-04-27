using System;
using System.Collections.Generic;
using Halibut;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Settings.Halibut;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

// Shares the static HalibutPollingWorkAdmission counters with
// HalibutPollingWorkAdmissionTests — must run in the same xUnit collection so
// they serialize. Otherwise concurrent ResetForTests() in one test class can
// wipe pre-saturated state from the integration test in this class.
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class HalibutMachineExecutionStrategyTests
{
    private readonly Mock<IHalibutClientFactory> _halibutClientFactory = new();
    private readonly Mock<IYamlNuGetPacker> _yamlNuGetPacker = new();
    private readonly CalamariPayloadBuilder _payloadBuilder;
    private readonly HalibutScriptObserver _observer;
    private readonly HalibutSetting _halibutSetting = new();
    private readonly HalibutMachineExecutionStrategy _strategy;

    public HalibutMachineExecutionStrategyTests()
    {
        _payloadBuilder = new CalamariPayloadBuilder(_yamlNuGetPacker.Object);
        _observer = new HalibutScriptObserver();
        _strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            _payloadBuilder,
            _observer,
            _halibutSetting);
    }

    // === Endpoint Parsing — invalid machine ===

    [Fact]
    public async Task ExecuteScriptAsync_MissingSubscriptionIdAndThumbprint_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "bad-machine", Endpoint = """{"CommunicationStyle":"KubernetesAgent"}""" };

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteScriptAsync_MissingThumbprint_ThrowsDeploymentEndpointException()
    {
        var machine = new Machine { Name = "no-thumb", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123"}""" };

        await Should.ThrowAsync<DeploymentEndpointException>(
            () => _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None));
    }

    // === Endpoint Parsing — valid URI + polling mode ===

    [Fact]
    public async Task ExecuteScriptAsync_ValidEndpoint_CreatesClientWithEndpoint()
    {
        var machine = new Machine { Name = "agent-1", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-123","Thumbprint":"AABBCCDD"}""" };
        SetupScriptClient("poll://sub-123/");

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
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"poll-sub-123","Thumbprint":"AABBCCDD"}"""
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
        SetupScriptClient("poll://sub-test/");

        var result = await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: "calamari-run-script"), CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteScriptAsync_WithoutCalamariCommand_RoutesToDirectPath()
    {
        var machine = CreateValidMachine();
        SetupScriptClient("poll://sub-test/");

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
        var scriptClient = SetupScriptClient("poll://sub-test/");

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(NewStartResponse("path-check"));

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
        var scriptClient = SetupScriptClient("poll://sub-test/");

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(NewStartResponse("timeout-check"));

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
            .ReturnsAsync(NewStartResponse("ticket-calamari"));
        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        observer.Setup(o => o.ObserveAndCompleteAsync(
                It.IsAny<Machine>(),
                It.IsAny<IAsyncScriptService>(),
                It.IsAny<ScriptTicket>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<SensitiveValueMasker>(),
                It.IsAny<ScriptStatusResponse>(),
                It.IsAny<global::Halibut.ServiceEndPoint>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string>() });

        var strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            payloadBuilder.Object,
            observer.Object,
            _halibutSetting);

        var result = await strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: "calamari-run-script"),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
        payloadBuilder.Verify(x => x.Build(It.IsAny<ScriptExecutionRequest>(), ScriptSyntax.PowerShell), Times.Once);
        observer.Verify(o => o.ObserveAndCompleteAsync(
            machine,
            scriptClient.Object,
            It.IsAny<ScriptTicket>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(30)),
            It.IsAny<CancellationToken>(),
            It.IsAny<SensitiveValueMasker>(),
            It.IsAny<ScriptStatusResponse>(),
            It.IsAny<global::Halibut.ServiceEndPoint>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteScriptAsync_DirectScript_DoesNotUsePayloadBuilder_ButUsesObserver()
    {
        var machine = CreateValidMachine();
        var scriptClient = new Mock<IAsyncScriptService>();
        var payloadBuilder = new Mock<ICalamariPayloadBuilder>();
        var observer = new Mock<IHalibutScriptObserver>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(NewStartResponse("ticket-direct"));
        _halibutClientFactory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>()))
            .Returns(scriptClient.Object);

        observer.Setup(o => o.ObserveAndCompleteAsync(
                It.IsAny<Machine>(),
                It.IsAny<IAsyncScriptService>(),
                It.IsAny<ScriptTicket>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<SensitiveValueMasker>(),
                It.IsAny<ScriptStatusResponse>(),
                It.IsAny<global::Halibut.ServiceEndPoint>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 7, LogLines = new List<string> { "x" } });

        var strategy = new HalibutMachineExecutionStrategy(
            _halibutClientFactory.Object,
            payloadBuilder.Object,
            observer.Object,
            _halibutSetting);

        var result = await strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.ExitCode.ShouldBe(7);
        payloadBuilder.Verify(x => x.Build(It.IsAny<ScriptExecutionRequest>()), Times.Never);
        observer.Verify(o => o.ObserveAndCompleteAsync(
            machine,
            scriptClient.Object,
            It.IsAny<ScriptTicket>(),
            It.Is<TimeSpan>(t => t == TimeSpan.FromMinutes(30)),
            It.IsAny<CancellationToken>(),
            It.IsAny<SensitiveValueMasker>(),
            It.IsAny<ScriptStatusResponse>(),
            It.IsAny<global::Halibut.ServiceEndPoint>()), Times.Once);
    }

    // === Request Timeout Override ===

    [Fact]
    public async Task ExecuteScriptAsync_WithRequestTimeout_UsesRequestTimeoutOverDefault()
    {
        var machine = CreateValidMachine();
        StartScriptCommand capturedCommand = null;
        var scriptClient = SetupScriptClient("poll://sub-test/");

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(NewStartResponse("timeout-override"));

        var request = CreateRequest(machine);
        request.Timeout = TimeSpan.FromMinutes(10);

        await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.ScriptIsolationMutexTimeout.ShouldBe(TimeSpan.FromMinutes(10));
    }

    // === Ticket ID Generation ===

    [Theory]
    [InlineData(null)]
    [InlineData("calamari-run-script")]
    public async Task ExecuteScriptAsync_GeneratesTicketId_PassedToCommand(string calamariCommand)
    {
        var machine = CreateValidMachine();
        StartScriptCommand capturedCommand = null;
        var scriptClient = SetupScriptClient("poll://sub-test/");

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => capturedCommand = cmd)
            .ReturnsAsync(NewStartResponse("ticket-id-check"));

        await _strategy.ExecuteScriptAsync(
            CreateRequest(machine, calamariCommand: calamariCommand), CancellationToken.None);

        capturedCommand.ShouldNotBeNull();
        capturedCommand.TaskId.ShouldNotBeNullOrEmpty();
        capturedCommand.TaskId.Length.ShouldBe(32); // Guid without hyphens
    }

    // === Ticket ID — fresh-per-attempt (ARCH.7) ===
    //
    // Pre-Phase-6 these tests pinned `GenerateTicketId` as a pure function of
    // the (taskId, step, action, machineId) tuple — same input, same output.
    // ARCH.7 deliberately broke that property: ticket is now Guid-per-attempt
    // so retries don't trap on agent-side state from the previous attempt.
    // The stable-derivation half (used as the IsolationMutexName so concurrent
    // dispatches still serialise on the agent) lives in `GenerateMutexName`,
    // which IS pinned by these tests instead.

    [Fact]
    public void GenerateMutexName_SameInputs_ProducesSameResult()
    {
        // The mutex-name MUST stay deterministic — that's what makes
        // concurrent same-action dispatches serialise on the agent.
        var n1 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Deploy", "RunScript", 42);
        var n2 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Deploy", "RunScript", 42);

        n1.ShouldBe(n2);
    }

    [Fact]
    public void GenerateMutexName_DifferentMachineId_ProducesDifferentResult()
    {
        var n1 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Deploy", "RunScript", 42);
        var n2 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Deploy", "RunScript", 43);

        n1.ShouldNotBe(n2);
    }

    [Fact]
    public void GenerateMutexName_DifferentStepName_ProducesDifferentResult()
    {
        var n1 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Deploy", "RunScript", 42);
        var n2 = HalibutMachineExecutionStrategy.GenerateMutexName(1, "Rollback", "RunScript", 42);

        n1.ShouldNotBe(n2);
    }

    // === Helpers ===

    private static ScriptStatusResponse NewStartResponse(string ticketId)
        => new(new ScriptTicket(ticketId), ProcessState.Running, 0, new List<ProcessOutput>(), 0);

    private static Machine CreateValidMachine() => new()
    {
        Name = "test-agent",
        Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-test","Thumbprint":"AABBCCDD"}"""
    };

    private Mock<IAsyncScriptService> SetupScriptClient(string expectedUri)
    {
        var scriptClient = new Mock<IAsyncScriptService>();

        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(NewStartResponse("ticket"));
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

    // ── P1-Phase9b.1 admission gate — wired into ExecuteScriptAsync ──────────

    [Fact]
    public async Task ExecuteScriptAsync_AdmissionGateAtCap_RejectsWithStructuredException()
    {
        // Saturate machine 1234's admission slots up to default cap (100), then
        // dispatch one more — must be rejected with PollingWorkAdmissionExceededException.
        // This is the integration-level pin that Phase-9b.1's gate is wired into
        // the strategy's ExecuteScriptAsync entry point. Pre-fix, the gate was
        // a separate utility but the strategy never called it; tests at the
        // utility level alone wouldn't have caught a wiring regression.
        try
        {
            HalibutPollingWorkAdmission.ResetForTests();
            var machine = new Machine
            {
                Id = 1234,
                Name = "saturated-machine",
                Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-1234","Thumbprint":"THUMB-1234"}"""
            };

            // Pre-saturate: 100 admits (the default cap)
            for (var i = 0; i < HalibutPollingWorkAdmission.DefaultMaxPendingWorkPerAgent; i++)
                HalibutPollingWorkAdmission.TryAdmit(machine.Id, HalibutPollingWorkAdmission.DefaultMaxPendingWorkPerAgent, out _);

            // 101st dispatch attempt — strategy must reject before touching Halibut
            var ex = await Should.ThrowAsync<PollingWorkAdmissionExceededException>(
                () => _strategy.ExecuteScriptAsync(CreateRequest(machine), CancellationToken.None));

            ex.MachineId.ShouldBe(machine.Id);
            ex.MaxPending.ShouldBe(HalibutPollingWorkAdmission.DefaultMaxPendingWorkPerAgent);

            // Halibut client factory MUST NOT have been called — the gate cuts before dispatch.
            _halibutClientFactory.Verify(f => f.CreateClient(It.IsAny<ServiceEndPoint>()), Times.Never,
                failMessage: "Admission gate must short-circuit BEFORE Halibut client creation, " +
                             "otherwise the queue-bounded invariant is broken.");
        }
        finally
        {
            HalibutPollingWorkAdmission.ResetForTests();
        }
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
            Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
        };
    }
}

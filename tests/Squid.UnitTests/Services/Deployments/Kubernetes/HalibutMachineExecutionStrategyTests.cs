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
                It.IsAny<global::Halibut.ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
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
            It.IsAny<global::Halibut.ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()), Times.Once);
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
                It.IsAny<global::Halibut.ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()))
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
            It.IsAny<global::Halibut.ServiceEndPoint>(), It.IsAny<ScriptOutputSink>()), Times.Once);
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
        // The ScriptTicket carries the Guid-per-attempt id; TaskId carries the
        // server task id (correlation), NOT the ticket.
        capturedCommand.ScriptTicket.TaskId.Length.ShouldBe(32); // Guid 'N' form
        capturedCommand.ScriptTicket.TaskId.ShouldMatch("^[a-f0-9]{32}$");
    }

    [Theory]
    [InlineData(null)]                   // direct-script dispatch site
    [InlineData("calamari-run-script")]  // packaged / Calamari dispatch site
    public async Task ExecuteScriptAsync_PassesMachineScopedMutexNameAsIsolationMutexName_NotTaskId(string calamariCommand)
    {
        // Regression pin for the ctor-arg-position bug: the generated isolation
        // mutex name MUST reach the wire as IsolationMutexName (arg 5) — the field
        // the agent uses to serialise FullIsolation scripts. Before the fix it was
        // misassigned to TaskId (arg 7), leaving IsolationMutexName null, which
        // silently falls back to the global "default" mutex. No prior test captured
        // this wire field, so the bug survived.
        //
        // Covers BOTH dispatch sites (direct + Calamari) since the bug existed in
        // both. Uses DISTINCT machine.Id (42) and ServerTaskId (777) so the
        // assertions prove WHICH field carries WHICH value — with a single shared 0
        // the test couldn't distinguish ForMachine(machine.Id) from
        // ForMachine(serverTaskId), nor TaskId=machineId from TaskId=serverTaskId.
        var machine = new Machine
        {
            Id = 42,
            Name = "test-agent",
            Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-test","Thumbprint":"AABBCCDD"}"""
        };
        StartScriptCommand captured = null;
        var scriptClient = SetupScriptClient("poll://sub-test/");
        scriptClient.Setup(s => s.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .Callback<StartScriptCommand>(cmd => captured = cmd)
            .ReturnsAsync(NewStartResponse("ticket"));

        var request = CreateRequest(machine, calamariCommand: calamariCommand);
        request.ServerTaskId = 777;

        await _strategy.ExecuteScriptAsync(request, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Isolation.ShouldBe(ScriptIsolationLevel.FullIsolation);
        captured.IsolationMutexName.ShouldBe(ScriptIsolationMutexNames.ForMachine(42),
            customMessage: "IsolationMutexName (arg 5) must be ForMachine(machine.Id=42) — keyed on machine id, " +
                           "reaching the wire — not null (the global 'default' fallback) and not serverTaskId");
        captured.IsolationMutexName.ShouldBe("squid/machine/42");
        captured.TaskId.ShouldBe("777",
            customMessage: "TaskId (arg 7) must carry the server task id (777), not the machine id or the mutex name");
    }

    // Isolation mutex name is now machine-scoped: the strategy sends
    // ScriptIsolationMutexNames.ForMachine(machineId) on IsolationMutexName so
    // EVERY FullIsolation script on a machine serialises behind one writer lock
    // (the earlier per-action SHA keyed by serverTaskId would have let two
    // deployments to the same machine run concurrently — the opposite of
    // FullIsolation's purpose). The strategy's use of it is pinned by
    // ExecuteScriptAsync_PassesMachineScopedMutexNameAsIsolationMutexName_NotTaskId
    // above; the name's own contract is pinned by ScriptIsolationMutexNamesTests.

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

    // ──  admission gate — wired into ExecuteScriptAsync ──────────

    [Fact]
    public async Task ExecuteScriptAsync_AdmissionGateAtCap_RejectsWithStructuredException()
    {
        // Saturate machine 1234's admission slots up to default cap (100), then
        // dispatch one more — must be rejected with PollingWorkAdmissionExceededException.
        // This is the integration-level pin that 's gate is wired into
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

public class MachineHealthCheckServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IMachinePolicyDataProvider> _policyDataProvider = new();
    private readonly Mock<ITransportRegistry> _transportRegistry = new();
    private readonly Mock<IExecutionStrategy> _strategy = new();
    private readonly Mock<IDeploymentTransport> _transport = new();
    private readonly MachineHealthCheckService _service;

    public MachineHealthCheckServiceTests()
    {
        _transport.Setup(t => t.Strategy).Returns(_strategy.Object);
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(_transport.Object);
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } });
        _service = new MachineHealthCheckService(_machineDataProvider.Object, _policyDataProvider.Object, _transportRegistry.Object);
    }

    // ========================================================================
    // Phase 1.1: Per-Policy Health Check Interval
    // ========================================================================

    [Fact]
    public async Task RunHealthCheckForAll_NeverChecked_ExecutesHealthCheck()
    {
        var machine = CreateActiveMachine(healthLastChecked: null);
        SetupMachineList(machine);

        await _service.RunHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunHealthCheckForAll_CheckedRecentlyWithDefaultInterval_Skips()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-30));
        SetupMachineList(machine);

        await _service.RunHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHealthCheckForAll_CheckedOverAnHourAgoWithDefaultInterval_Executes()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-61));
        SetupMachineList(machine);

        await _service.RunHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(300, -4, false)]   // 5min interval, checked 4min ago → skip
    [InlineData(300, -6, true)]    // 5min interval, checked 6min ago → execute
    [InlineData(7200, -100, false)] // 2hr interval, checked 100min ago → skip
    [InlineData(7200, -121, true)]  // 2hr interval, checked 121min ago → execute
    public async Task RunHealthCheckForAll_CustomPolicyInterval_RespectsInterval(int intervalSeconds, int minutesAgo, bool shouldExecute)
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(minutesAgo), machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicy(1, intervalSeconds);

        await _service.RunHealthCheckForAllAsync();

        var expected = shouldExecute ? Times.Once() : Times.Never();
        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), expected);
    }

    [Fact]
    public void IsHealthCheckDue_Expired_ReturnsTrue()
    {
        MachineHealthCheckService.IsHealthCheckDue(DateTime.UtcNow.AddHours(-2), 3600).ShouldBeTrue();
    }

    [Fact]
    public void IsHealthCheckDue_NotExpired_ReturnsFalse()
    {
        MachineHealthCheckService.IsHealthCheckDue(DateTime.UtcNow.AddMinutes(-30), 3600).ShouldBeFalse();
    }

    // ========================================================================
    // Phase 1.2: OnlyConnectivity Health Check Mode
    // ========================================================================

    [Fact]
    public async Task RunHealthCheck_OnlyConnectivity_ExecutesMinimalScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithRunType(1, "KubernetesApi", "OnlyConnectivity");

        await _service.RunHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("exit 0");
        capturedRequest.ScriptBody.ShouldNotContain("kubectl");
    }

    [Fact]
    public async Task RunHealthCheck_OnlyConnectivity_Success_RecordsHealthy()
    {
        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithRunType(1, "KubernetesApi", "OnlyConnectivity");

        await _service.RunHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        machine.HealthDetailJson.ShouldBe("Connectivity OK");
    }

    [Fact]
    public async Task RunHealthCheck_OnlyConnectivity_Failure_RecordsUnavailable()
    {
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithRunType(1, "KubernetesApi", "OnlyConnectivity");

        await _service.RunHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    [Fact]
    public async Task RunHealthCheck_OnlyConnectivity_CaseInsensitive()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithRunType(1, "KubernetesApi", "onlyconnectivity"); // lowercase

        await _service.RunHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("exit 0");
        capturedRequest.ScriptBody.ShouldNotContain("kubectl");
    }

    // ========================================================================
    // Phase 1.3: Per-CommunicationStyle Default Health Scripts
    // ========================================================================

    [Theory]
    [InlineData(CommunicationStyle.KubernetesApi, "kubectl cluster-info")]
    [InlineData(CommunicationStyle.KubernetesAgent, "kubectl get pods")]
    public void GetDefaultHealthCheckScript_ReturnsStyleSpecificScript(CommunicationStyle style, string expectedContent)
    {
        var script = MachineHealthCheckService.GetDefaultHealthCheckScript(style);

        script.ShouldContain(expectedContent);
    }

    [Fact]
    public void GetDefaultHealthCheckScript_UnknownStyle_ReturnsFallback()
    {
        var script = MachineHealthCheckService.GetDefaultHealthCheckScript(CommunicationStyle.Unknown);

        script.ShouldContain("Uptime");
        script.ShouldNotContain("kubectl");
    }

    [Fact]
    public async Task RunHealthCheck_InheritFromDefault_UsesStyleSpecificScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithRunType(1, "KubernetesApi", "InheritFromDefault");

        await _service.RunHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public async Task RunHealthCheck_CustomInlineScript_UsesCustomScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);
        SetupPolicyWithCustomScript(1, "KubernetesApi", "Inline", "echo custom-check");

        await _service.RunHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldBe("echo custom-check");
    }

    [Fact]
    public async Task RunHealthCheck_NoPolicy_UsesDefaultScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.RunHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("kubectl cluster-info");
    }

    // ========================================================================
    // ResolveScriptPolicy — pure static logic
    // ========================================================================

    [Fact]
    public void ResolveScriptPolicy_NullPolicy_ReturnsNull()
    {
        MachineHealthCheckService.ResolveScriptPolicy(null, CommunicationStyle.KubernetesApi).ShouldBeNull();
    }

    [Fact]
    public void ResolveScriptPolicy_NoMatchingStyle_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["KubernetesAgent"] = new() { RunType = "Inline", ScriptBody = "echo test" }
            }
        };

        MachineHealthCheckService.ResolveScriptPolicy(policy, CommunicationStyle.KubernetesApi).ShouldBeNull();
    }

    [Fact]
    public void ResolveScriptPolicy_MatchingStyle_ReturnsPolicy()
    {
        var expected = new MachineScriptPolicyDto { RunType = "Inline", ScriptBody = "echo test" };
        var policy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["KubernetesApi"] = expected
            }
        };

        var result = MachineHealthCheckService.ResolveScriptPolicy(policy, CommunicationStyle.KubernetesApi);

        result.ShouldBe(expected);
    }

    // ========================================================================
    // Edge cases & robustness
    // ========================================================================

    [Fact]
    public async Task RunHealthCheck_DisabledMachine_Skips()
    {
        var machine = new Machine { Id = 1, Name = "disabled", IsDisabled = true };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        await _service.RunHealthCheckAsync(1);

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHealthCheck_MachineNotFound_Throws()
    {
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Machine)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _service.RunHealthCheckAsync(999));
    }

    [Fact]
    public async Task RunHealthCheck_NoTransport_RecordsUnavailable()
    {
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns((IDeploymentTransport)null);

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.RunHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    [Fact]
    public async Task RunHealthCheck_ScriptFails_RecordsUnhealthy()
    {
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "error" } });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.RunHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthCheck_ScriptSucceeds_RecordsHealthy()
    {
        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.RunHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        machine.HealthLastChecked.ShouldNotBeNull();
    }

    [Fact]
    public async Task RunHealthCheckForAll_ExceptionOnOneMachine_ContinuesOthers()
    {
        var machine1 = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        var machine2 = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        machine1.Id = 1;
        machine1.Name = "machine1";
        machine2.Id = 2;
        machine2.Name = "machine2";

        SetupMachineList(machine1, machine2);

        var callCount = 0;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1) throw new TimeoutException("connection timeout");
                return new ScriptExecutionResult { Success = true, ExitCode = 0 };
            });

        await _service.RunHealthCheckForAllAsync();

        machine1.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine2.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static Machine CreateActiveMachine(DateTime? healthLastChecked = null, int? machinePolicyId = null)
    {
        return new Machine
        {
            Id = 1,
            Name = "test-machine",
            IsDisabled = false,
            HealthLastChecked = healthLastChecked,
            MachinePolicyId = machinePolicyId,
            Endpoint = """{"communicationStyle":"KubernetesApi"}"""
        };
    }

    private static Machine CreateActiveMachineWithEndpoint(CommunicationStyle style, int? machinePolicyId = null)
    {
        return new Machine
        {
            Id = 1,
            Name = "test-machine",
            IsDisabled = false,
            MachinePolicyId = machinePolicyId,
            Endpoint = $$$"""{"communicationStyle":"{{{style}}}"}"""
        };
    }

    private void SetupMachineById(Machine machine)
    {
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(machine.Id, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
    }

    private void SetupMachineList(params Machine[] machines)
    {
        _machineDataProvider.Setup(p => p.GetMachinePagingAsync(It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((machines.Length, machines.ToList()));
    }

    private void SetupPolicy(int policyId, int intervalSeconds)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto { HealthCheckIntervalSeconds = intervalSeconds };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }

    private void SetupPolicyWithRunType(int policyId, string styleKey, string runType)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                [styleKey] = new() { RunType = runType }
            }
        };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }

    private void SetupPolicyWithCustomScript(int policyId, string styleKey, string runType, string scriptBody)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                [styleKey] = new() { RunType = runType, ScriptBody = scriptBody }
            }
        };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }
}

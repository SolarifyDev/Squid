using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Machine;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Machines;

public class MachineHealthCheckServiceTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly Mock<IMachinePolicyDataProvider> _policyDataProvider = new();
    private readonly Mock<ITransportRegistry> _transportRegistry = new();
    private readonly Mock<IExecutionStrategy> _strategy = new();
    private readonly Mock<IDeploymentTransport> _transport = new();
    private readonly Mock<IHealthCheckStrategy> _healthChecker = new();
    private readonly Mock<IEndpointVariableContributor> _variableContributor = new();
    private readonly Mock<IEndpointContextBuilder> _endpointContextBuilder = new();
    private readonly MachineHealthCheckService _service;

    public MachineHealthCheckServiceTests()
    {
        _transport.Setup(t => t.Strategy).Returns(_strategy.Object);
        _transport.Setup(t => t.HealthChecker).Returns(_healthChecker.Object);
        _transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.KubernetesApi);
        _transport.Setup(t => t.Variables).Returns(_variableContributor.Object);
        _healthChecker.Setup(h => h.DefaultHealthCheckScript).Returns("#!/bin/bash\nkubectl cluster-info 2>&1");
        _healthChecker.Setup(h => h.ScriptSyntax).Returns(ScriptSyntax.Bash);
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(_transport.Object);
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0, LogLines = new List<string> { "ok" } });
        _endpointContextBuilder.Setup(b => b.BuildAsync(It.IsAny<string>(), It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string endpoint, IEndpointVariableContributor _, CancellationToken _) => new EndpointContext { EndpointJson = endpoint });
        _service = new MachineHealthCheckService(_machineDataProvider.Object, _policyDataProvider.Object, _transportRegistry.Object, _endpointContextBuilder.Object);
    }

    // ========================================================================
    // Manual trigger — ManualHealthCheckAsync (no policy, always runs)
    // ========================================================================

    [Fact]
    public async Task RunHealthCheck_UsesTransportDefaultScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public async Task RunHealthCheck_NullHealthChecker_UsesFallbackScript()
    {
        _transport.Setup(t => t.HealthChecker).Returns((IHealthCheckStrategy)null);

        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("Uptime");
        capturedRequest.ScriptBody.ShouldNotContain("kubectl");
    }

    [Fact]
    public async Task RunHealthCheck_IgnoresPolicy_AlwaysExecutes()
    {
        // Machine has policy, but manual trigger should not load it
        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi, machinePolicyId: 1);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        _policyDataProvider.Verify(p => p.GetByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunHealthCheck_ScriptSucceeds_RecordsHealthy()
    {
        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
        machine.HealthLastChecked.ShouldNotBeNull();
    }

    [Fact]
    public async Task RunHealthCheck_ScriptFails_RecordsUnhealthy()
    {
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult { Success = false, ExitCode = 1, LogLines = new List<string> { "error" } });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unhealthy);
    }

    [Fact]
    public async Task RunHealthCheck_DisabledMachine_Skips()
    {
        var machine = new Machine { Id = 1, Name = "disabled", IsDisabled = true };
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);

        await _service.ManualHealthCheckAsync(1);

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHealthCheck_MachineNotFound_Throws()
    {
        _machineDataProvider.Setup(p => p.GetMachinesByIdAsync(999, It.IsAny<CancellationToken>())).ReturnsAsync((Machine)null);

        await Should.ThrowAsync<InvalidOperationException>(() => _service.ManualHealthCheckAsync(999));
    }

    [Fact]
    public async Task RunHealthCheck_NoTransport_RecordsUnavailable()
    {
        _transportRegistry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns((IDeploymentTransport)null);

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    // ========================================================================
    // Recurring job — AutoHealthCheckForAllAsync (policy-driven)
    // ========================================================================

    [Fact]
    public async Task RunHealthCheckForAll_NeverChecked_ExecutesHealthCheck()
    {
        var machine = CreateActiveMachine(healthLastChecked: null);
        SetupMachineList(machine);

        await _service.AutoHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunHealthCheckForAll_CheckedRecentlyWithDefaultInterval_Skips()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-30));
        SetupMachineList(machine);

        await _service.AutoHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHealthCheckForAll_CheckedOverAnHourAgoWithDefaultInterval_Executes()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-61));
        SetupMachineList(machine);

        await _service.AutoHealthCheckForAllAsync();

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

        await _service.AutoHealthCheckForAllAsync();

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
    // ShouldRunHealthCheck — schedule type enum
    // ========================================================================

    [Fact]
    public void ShouldRunHealthCheck_NullPolicy_DefaultsToInterval()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-61));

        MachineHealthCheckService.ShouldRunHealthCheck(machine, null).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRunHealthCheck_NullPolicy_NeverChecked_ReturnsTrue()
    {
        var machine = CreateActiveMachine(healthLastChecked: null);

        MachineHealthCheckService.ShouldRunHealthCheck(machine, null).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRunHealthCheck_NullPolicy_RecentlyChecked_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-30));

        MachineHealthCheckService.ShouldRunHealthCheck(machine, null).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRunHealthCheck_ExplicitInterval_RespectsIntervalSeconds()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-31));
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Interval,
            HealthCheckIntervalSeconds = 1800 // 30 minutes
        };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRunHealthCheck_ExplicitInterval_NotDue_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddMinutes(-10));
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Interval,
            HealthCheckIntervalSeconds = 1800
        };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRunHealthCheck_ScheduleTypeNever_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: null);
        var policy = new MachineHealthCheckPolicyDto { HealthCheckScheduleType = HealthCheckScheduleType.Never };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRunHealthCheck_ScheduleTypeNever_EvenWhenOverdue_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddDays(-30));
        var policy = new MachineHealthCheckPolicyDto { HealthCheckScheduleType = HealthCheckScheduleType.Never };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRunHealthCheck_ScheduleTypeCron_Due_ReturnsTrue()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddHours(-2));
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Cron,
            HealthCheckCronExpression = "* * * * *" // every minute
        };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeTrue();
    }

    [Fact]
    public void ShouldRunHealthCheck_ScheduleTypeCron_NotDue_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow);
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Cron,
            HealthCheckCronExpression = "0 0 1 1 *" // once a year
        };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeFalse();
    }

    [Fact]
    public void ShouldRunHealthCheck_ScheduleTypeCron_NullExpression_ReturnsFalse()
    {
        var machine = CreateActiveMachine(healthLastChecked: DateTime.UtcNow.AddHours(-2));
        var policy = new MachineHealthCheckPolicyDto
        {
            HealthCheckScheduleType = HealthCheckScheduleType.Cron,
            HealthCheckCronExpression = null
        };

        MachineHealthCheckService.ShouldRunHealthCheck(machine, policy).ShouldBeFalse();
    }

    // ========================================================================
    // IsCronDue — edge cases
    // ========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void IsCronDue_EmptyOrWhitespace_ReturnsFalse(string cronExpr)
    {
        MachineHealthCheckService.IsCronDue(DateTime.UtcNow.AddHours(-1), cronExpr).ShouldBeFalse();
    }

    [Fact]
    public void IsCronDue_InvalidCronExpression_ReturnsFalse()
    {
        MachineHealthCheckService.IsCronDue(DateTime.UtcNow.AddHours(-1), "not a valid cron").ShouldBeFalse();
    }

    [Fact]
    public void IsCronDue_LastCheckedInFuture_ReturnsFalse()
    {
        MachineHealthCheckService.IsCronDue(DateTime.UtcNow.AddDays(1), "* * * * *").ShouldBeFalse();
    }

    // ========================================================================
    // Recurring job — OnlyConnectivity mode (policy-driven, top-level enum)
    // ========================================================================

    [Fact]
    public async Task RunHealthCheckForAll_OnlyConnectivity_DelegatesToHealthChecker()
    {
        _healthChecker.Setup(h => h.CheckConnectivityAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(true, "Connected"));

        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithHealthCheckType(1, PolicyHealthCheckType.OnlyConnectivity);

        await _service.AutoHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _healthChecker.Verify(h => h.CheckConnectivityAsync(machine, It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>()), Times.Once);
        machine.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
    }

    [Fact]
    public async Task RunHealthCheckForAll_OnlyConnectivity_Unhealthy_RecordsUnavailable()
    {
        _healthChecker.Setup(h => h.CheckConnectivityAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HealthCheckResult(false, "ClusterUrl is empty"));

        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithHealthCheckType(1, PolicyHealthCheckType.OnlyConnectivity);

        await _service.AutoHealthCheckForAllAsync();

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine.HealthDetail.ShouldContain("ClusterUrl is empty");
    }

    [Fact]
    public async Task RunHealthCheckForAll_OnlyConnectivity_NullHealthChecker_RecordsUnavailable()
    {
        _transport.Setup(t => t.HealthChecker).Returns((IHealthCheckStrategy)null);

        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithHealthCheckType(1, PolicyHealthCheckType.OnlyConnectivity);

        await _service.AutoHealthCheckForAllAsync();

        machine.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
    }

    [Fact]
    public async Task RunHealthCheckForAll_HealthCheckTypeRunScript_ExecutesScript()
    {
        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithHealthCheckType(1, PolicyHealthCheckType.RunScript);

        await _service.AutoHealthCheckForAllAsync();

        _strategy.Verify(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ========================================================================
    // Recurring job — Script resolution (policy-driven)
    // ========================================================================

    [Fact]
    public async Task RunHealthCheckForAll_InheritFromDefault_UsesTransportDefaultScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithRunType(1, ScriptSyntax.Bash.ToString(), ScriptPolicyRunType.InheritFromDefault);

        await _service.AutoHealthCheckForAllAsync();

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public async Task RunHealthCheckForAll_CustomInlineScript_UsesCustomScript()
    {
        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachine(machinePolicyId: 1);
        SetupMachineList(machine);
        SetupPolicyWithCustomScript(1, ScriptSyntax.Bash.ToString(), ScriptPolicyRunType.CustomScript, "echo custom-check");

        await _service.AutoHealthCheckForAllAsync();

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldBe("echo custom-check");
    }

    // ========================================================================
    // ResolveScriptPolicy — pure static logic (keyed by ScriptSyntax)
    // ========================================================================

    [Fact]
    public void ResolveScriptPolicy_NullPolicy_ReturnsNull()
    {
        MachineHealthCheckService.ResolveScriptPolicy(null, ScriptSyntax.Bash).ShouldBeNull();
    }

    [Fact]
    public void ResolveScriptPolicy_NoMatchingSyntax_ReturnsNull()
    {
        var policy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["PowerShell"] = new() { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo test" }
            }
        };

        MachineHealthCheckService.ResolveScriptPolicy(policy, ScriptSyntax.Bash).ShouldBeNull();
    }

    [Fact]
    public void ResolveScriptPolicy_ByScriptSyntax_Bash()
    {
        var expected = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo test" };
        var policy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["Bash"] = expected
            }
        };

        var result = MachineHealthCheckService.ResolveScriptPolicy(policy, ScriptSyntax.Bash);

        result.ShouldBe(expected);
    }

    [Fact]
    public void ResolveScriptPolicy_ByScriptSyntax_PowerShell()
    {
        var expected = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "Write-Host test" };
        var policy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                ["PowerShell"] = expected
            }
        };

        var result = MachineHealthCheckService.ResolveScriptPolicy(policy, ScriptSyntax.PowerShell);

        result.ShouldBe(expected);
    }

    // ========================================================================
    // ResolveScriptBody — enum-based
    // ========================================================================

    [Fact]
    public void ResolveScriptBody_CustomScript_ReturnsCustomBody()
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo custom" };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, null, _transport.Object);

        result.ShouldBe("echo custom");
    }

    [Fact]
    public void ResolveScriptBody_InheritFromDefault_ReturnsTransportDefault()
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.InheritFromDefault };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, null, _transport.Object);

        result.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public void ResolveScriptBody_NullScriptPolicy_ReturnsTransportDefault()
    {
        var result = MachineHealthCheckService.ResolveScriptBody(null, null, _transport.Object);

        result.ShouldContain("kubectl cluster-info");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveScriptBody_CustomScriptButEmptyBody_FallsBackToDefault(string emptyBody)
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = emptyBody };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, null, _transport.Object);

        result.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public void ResolveScriptBody_NullScriptPolicy_NullHealthChecker_ReturnsFallback()
    {
        _transport.Setup(t => t.HealthChecker).Returns((IHealthCheckStrategy)null);

        var result = MachineHealthCheckService.ResolveScriptBody(null, null, _transport.Object);

        result.ShouldContain("Uptime");
    }

    // ========================================================================
    // ResolveScriptBody — InheritFromDefault cross-policy inheritance
    // ========================================================================

    [Fact]
    public void ResolveScriptBody_InheritFromDefault_DefaultPolicyHasCustomScript_UsesDefaultPolicyScript()
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.InheritFromDefault };
        var defaultScript = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo default-policy-custom" };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, defaultScript, _transport.Object);

        result.ShouldBe("echo default-policy-custom");
    }

    [Fact]
    public void ResolveScriptBody_NullScriptPolicy_DefaultPolicyHasCustomScript_UsesDefaultPolicyScript()
    {
        var defaultScript = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo default-policy-custom" };

        var result = MachineHealthCheckService.ResolveScriptBody(null, defaultScript, _transport.Object);

        result.ShouldBe("echo default-policy-custom");
    }

    [Fact]
    public void ResolveScriptBody_InheritFromDefault_DefaultPolicyAlsoInherits_FallsToTransportDefault()
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.InheritFromDefault };
        var defaultScript = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.InheritFromDefault };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, defaultScript, _transport.Object);

        result.ShouldContain("kubectl cluster-info");
    }

    [Fact]
    public void ResolveScriptBody_CustomScript_IgnoresDefaultPolicy()
    {
        var scriptPolicy = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo machine-custom" };
        var defaultScript = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "echo default-policy-custom" };

        var result = MachineHealthCheckService.ResolveScriptBody(scriptPolicy, defaultScript, _transport.Object);

        result.ShouldBe("echo machine-custom");
    }

    // ========================================================================
    // Recurring job — error handling
    // ========================================================================

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

        await _service.AutoHealthCheckForAllAsync();

        machine1.HealthStatus.ShouldBe(MachineHealthStatus.Unavailable);
        machine2.HealthStatus.ShouldBe(MachineHealthStatus.Healthy);
    }

    // ========================================================================
    // Credential loading + script wrapping
    // ========================================================================

    [Fact]
    public async Task ManualHealthCheck_CallsEndpointContextBuilder()
    {
        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        _endpointContextBuilder.Verify(b => b.BuildAsync(machine.Endpoint, It.IsAny<IEndpointVariableContributor>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ManualHealthCheck_WrapsScriptViaTransportWrapper()
    {
        var scriptWrapper = new Mock<IScriptContextWrapper>();
        scriptWrapper.Setup(w => w.WrapScript(It.IsAny<string>(), It.IsAny<ScriptContext>()))
            .Returns((string s, ScriptContext _) => $"# context setup\n{s}");
        _transport.Setup(t => t.ScriptWrapper).Returns(scriptWrapper.Object);

        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldStartWith("# context setup");
        capturedRequest.EndpointContext.ShouldNotBeNull();
        scriptWrapper.Verify(w => w.WrapScript(It.IsAny<string>(), It.IsAny<ScriptContext>()), Times.Once);
    }

    [Fact]
    public async Task ManualHealthCheck_NullWrapper_ScriptUnchanged()
    {
        _transport.Setup(t => t.ScriptWrapper).Returns((IScriptContextWrapper)null);

        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachineWithEndpoint(CommunicationStyle.KubernetesApi);
        SetupMachineById(machine);

        await _service.ManualHealthCheckAsync(machine.Id);

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldContain("kubectl cluster-info");
        capturedRequest.EndpointContext.ShouldNotBeNull();
    }

    [Fact]
    public async Task AutoHealthCheck_WrapsScript()
    {
        var scriptWrapper = new Mock<IScriptContextWrapper>();
        scriptWrapper.Setup(w => w.WrapScript(It.IsAny<string>(), It.IsAny<ScriptContext>()))
            .Returns((string s, ScriptContext _) => $"# wrapped\n{s}");
        _transport.Setup(t => t.ScriptWrapper).Returns(scriptWrapper.Object);

        ScriptExecutionRequest capturedRequest = null;
        _strategy.Setup(s => s.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<ScriptExecutionRequest, CancellationToken>((r, _) => capturedRequest = r)
            .ReturnsAsync(new ScriptExecutionResult { Success = true, ExitCode = 0 });

        var machine = CreateActiveMachine(healthLastChecked: null);
        SetupMachineList(machine);

        await _service.AutoHealthCheckForAllAsync();

        capturedRequest.ShouldNotBeNull();
        capturedRequest.ScriptBody.ShouldStartWith("# wrapped");
        scriptWrapper.Verify(w => w.WrapScript(It.IsAny<string>(), It.IsAny<ScriptContext>()), Times.Once);
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

    private void SetupPolicyWithHealthCheckType(int policyId, PolicyHealthCheckType healthCheckType)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto { HealthCheckType = healthCheckType };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }

    private void SetupPolicyWithRunType(int policyId, string syntaxKey, ScriptPolicyRunType runType)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                [syntaxKey] = new() { RunType = runType }
            }
        };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }

    private void SetupPolicyWithCustomScript(int policyId, string syntaxKey, ScriptPolicyRunType runType, string scriptBody)
    {
        var healthPolicy = new MachineHealthCheckPolicyDto
        {
            ScriptPolicies = new Dictionary<string, MachineScriptPolicyDto>
            {
                [syntaxKey] = new() { RunType = runType, ScriptBody = scriptBody }
            }
        };
        var policyJson = JsonSerializer.Serialize(healthPolicy, JsonOptions);

        _policyDataProvider.Setup(p => p.GetByIdAsync(policyId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MachinePolicy { Id = policyId, MachineHealthCheckPolicy = policyJson });
    }
}

using Autofac;
using Halibut.Diagnostics;
using Microsoft.Extensions.Configuration;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

public class ProbeTimeoutPolicyTests
{
    private static readonly HalibutTimeoutsAndLimits Defaults = HalibutTimeoutsAndLimits.RecommendedValues();

    private static MachineHealthCheckPolicyDto SshScriptPolicy() => new()
    {
        HealthCheckType = PolicyHealthCheckType.RunScript,
        ScriptPolicies = new() { ["Bash"] = new MachineScriptPolicyDto { RunType = ScriptPolicyRunType.CustomScript, ScriptBody = "sleep 100" } }
    };

    [Theory]
    [InlineData(CommunicationStyle.TentaclePolling, 150)]
    [InlineData(CommunicationStyle.KubernetesAgent, 150)]
    [InlineData(CommunicationStyle.TentacleListening, 95)]
    [InlineData(CommunicationStyle.KubernetesApi, 75)]
    [InlineData(CommunicationStyle.Ssh, 75)]
    [InlineData(CommunicationStyle.OpenClaw, 75)]
    public void Unconfigured_DefaultCoversTheTransportsOwnTimeout(CommunicationStyle style, int expectedSeconds)
    {
        ProbeTimeoutPolicy.For(new MachineHealthCheckSetting(), style, null, Defaults).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void Unconfigured_PollingDefault_FollowsTheEffectivePollingQueueTimeout()
    {
        var limits = HalibutTimeoutsAndLimits.RecommendedValues();
        limits.PollingRequestQueueTimeout = TimeSpan.FromSeconds(300);

        ProbeTimeoutPolicy.For(new MachineHealthCheckSetting(), CommunicationStyle.TentaclePolling, null, limits).ShouldBe(TimeSpan.FromSeconds(330));
    }

    [Fact]
    public void Unconfigured_SshWithCustomScript_CoversTheScriptTimeout_ButPlainSshDoesNot()
    {
        ProbeTimeoutPolicy.For(new MachineHealthCheckSetting(), CommunicationStyle.Ssh, SshScriptPolicy(), Defaults).ShouldBeGreaterThan(TimeSpan.FromMinutes(5));
        ProbeTimeoutPolicy.For(new MachineHealthCheckSetting(), CommunicationStyle.Ssh, new MachineHealthCheckPolicyDto { HealthCheckType = PolicyHealthCheckType.RunScript }, Defaults).ShouldBe(TimeSpan.FromSeconds(75));
    }

    [Theory]
    [InlineData(CommunicationStyle.TentaclePolling)]
    [InlineData(CommunicationStyle.TentacleListening)]
    [InlineData(CommunicationStyle.Ssh)]
    public void Configured_IsHonouredExactly_ForEveryTransport(CommunicationStyle style)
    {
        var setting = new MachineHealthCheckSetting { ProbeTimeoutSeconds = 20 };

        ProbeTimeoutPolicy.For(setting, style, SshScriptPolicy(), Defaults).ShouldBe(TimeSpan.FromSeconds(20));
        ProbeTimeoutPolicy.Longest(setting, Defaults).ShouldBe(TimeSpan.FromSeconds(20));
        ProbeTimeoutPolicy.ManualWait(setting, Defaults).ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void Longest_CoversEveryTransport_AndManualWaitExcludesTheSshScript()
    {
        var setting = new MachineHealthCheckSetting();

        ProbeTimeoutPolicy.Longest(setting, Defaults).ShouldBeGreaterThanOrEqualTo(ProbeTimeoutPolicy.For(setting, CommunicationStyle.Ssh, SshScriptPolicy(), Defaults));
        ProbeTimeoutPolicy.ManualWait(setting, Defaults).ShouldBe(TimeSpan.FromSeconds(150));
    }

    [Theory]
    [InlineData("0", false)]
    [InlineData("-5", false)]
    [InlineData("40", true)]
    public void Setting_IsProbeTimeoutConfigured_OnlyForPositiveValues(string raw, bool configured)
    {
        var setting = new MachineHealthCheckSetting(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["MachineHealthCheck:ProbeTimeoutSeconds"] = raw }).Build());

        setting.IsProbeTimeoutConfigured.ShouldBe(configured);
        setting.ProbeTimeout.ShouldBe(configured ? TimeSpan.FromSeconds(40) : TimeSpan.FromSeconds(75));
    }

    [Fact]
    public void Setting_Unset_IsNotConfigured_WithBaseDefault75()
    {
        var setting = new MachineHealthCheckSetting(new ConfigurationBuilder().Build());

        setting.IsProbeTimeoutConfigured.ShouldBeFalse();
        setting.ProbeTimeout.ShouldBe(TimeSpan.FromSeconds(75));
    }

    // ── coordinator: the real error survives a timeout ──

    [Fact]
    public async Task Timeout_ProbeThatReportsItsOwnErrorAfterTheDeadline_KeepsThatErrorInTheDetail()
    {
        var coordinator = new MachineHealthCheckCoordinator(HealthCheckTestHost.PassthroughRedis().Object, new ContainerBuilder().Build(), new HealthCheckInProcessLocks(), new MachineHealthCheckSetting());

        var result = await coordinator.RunProbeAsync(async ct => { await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }); return new HealthCheckResult(false, "SSH health check error: connection refused"); }, CancellationToken.None, TimeSpan.FromMilliseconds(150));

        result.Healthy.ShouldBeFalse();
        result.Detail.ShouldContain("timed out after");
        result.Detail.ShouldContain("connection refused");
    }

    [Fact]
    public async Task Timeout_HungProbeWithNoError_StillReportsTimeout()
    {
        var coordinator = new MachineHealthCheckCoordinator(HealthCheckTestHost.PassthroughRedis().Object, new ContainerBuilder().Build(), new HealthCheckInProcessLocks(), new MachineHealthCheckSetting());

        var result = await coordinator.RunProbeAsync(ct => new TaskCompletionSource<HealthCheckResult>().Task, CancellationToken.None, TimeSpan.FromMilliseconds(150));

        result.Detail.ShouldContain("timed out after");
    }

    // ── seam: the service asks for the transport-aware timeout and passes it to the probe ──

    [Fact]
    public async Task ManualCheck_UsesTheTransportAwareTimeout_NotTheBaseDefault()
    {
        var machine = new Machine { Id = 1, Name = "agent", Endpoint = """{"communicationStyle":"TentaclePolling"}""" };
        var machines = new Mock<IMachineDataProvider>();
        machines.Setup(p => p.GetMachinesByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(machine);
        var checker = new Mock<IHealthCheckStrategy>();
        checker.Setup(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>())).ReturnsAsync(new HealthCheckResult(true, "ok"));
        var transport = new Mock<IDeploymentTransport>();
        transport.Setup(t => t.HealthChecker).Returns(checker.Object);
        transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.TentaclePolling);
        var registry = new Mock<ITransportRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport.Object);
        var spy = new List<TimeSpan>();

        using var host = new HealthCheckTestHost(machines.Object, Mock.Of<IMachinePolicyDataProvider>(), registry.Object, HealthCheckTestHost.PassthroughRedis().Object, new MachineHealthCheckSetting(), null, spy);

        await host.Service.ManualHealthCheckAsync(1);

        spy.ShouldBe(new[] { TimeSpan.FromSeconds(150) });
    }
}

using Squid.Core.Services.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Pins the machine-policy connectivity-behaviour predicate that decides whether an
/// unreachable agent is tolerated during a health check. Default / null /
/// ExpectedToBeOnline = not tolerated (today's behaviour); only the explicit
/// MayBeOfflineAndCanBeSkipped opts in.
/// </summary>
public class MachineConnectivityEvaluatorTests
{
    [Fact]
    public void NullPolicy_DoesNotAllowOffline()
        => MachineConnectivityEvaluator.AllowsOffline(null).ShouldBeFalse();

    [Fact]
    public void ExpectedToBeOnline_DoesNotAllowOffline()
        => MachineConnectivityEvaluator.AllowsOffline(new MachineConnectivityPolicyDto
        {
            MachineConnectivityBehavior = MachineConnectivityBehavior.ExpectedToBeOnline
        }).ShouldBeFalse();

    [Fact]
    public void MayBeOfflineAndCanBeSkipped_AllowsOffline()
        => MachineConnectivityEvaluator.AllowsOffline(new MachineConnectivityPolicyDto
        {
            MachineConnectivityBehavior = MachineConnectivityBehavior.MayBeOfflineAndCanBeSkipped
        }).ShouldBeTrue();

    [Fact]
    public void DefaultConstructedPolicy_DoesNotAllowOffline()
        // A freshly-constructed DTO defaults to ExpectedToBeOnline (enum 0) — the
        // non-breaking default that mirrors today's "offline = failure" behaviour.
        => MachineConnectivityEvaluator.AllowsOffline(new MachineConnectivityPolicyDto()).ShouldBeFalse();
}

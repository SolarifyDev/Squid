using Squid.Core.Services.Machines.Cleanup;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines.Cleanup;

/// <summary>
/// Exhaustive matrix for the machine-cleanup eligibility predicate — the single
/// source of truth for "may this unavailable target be auto-deleted by its policy".
/// Every guard is pinned independently so a future edit that drops one fails loudly.
/// </summary>
public class MachineCleanupEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);

    private static MachineCleanupPolicyDto Policy(DeleteMachinesBehavior behavior, int afterSeconds = 86400) => new()
    {
        DeleteMachinesBehavior = behavior,
        DeleteMachinesAfterSeconds = afterSeconds
    };

    [Fact]
    public void NullPolicy_NotEligible()
        => MachineCleanupEvaluator.IsEligible(null, MachineHealthStatus.Unavailable, Now.AddDays(-30), Now).ShouldBeFalse();

    [Fact]
    public void DoNotDelete_EvenWhenLongUnavailable_NotEligible()
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DoNotDelete), MachineHealthStatus.Unavailable, Now.AddDays(-30), Now)
            .ShouldBeFalse();

    [Theory]
    [InlineData(MachineHealthStatus.Healthy)]
    [InlineData(MachineHealthStatus.Unknown)]
    [InlineData(MachineHealthStatus.HasWarnings)]
    [InlineData(MachineHealthStatus.Unhealthy)]
    public void NotUnavailable_NotEligible(MachineHealthStatus status)
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines), status, Now.AddDays(-30), Now)
            .ShouldBeFalse();

    [Fact]
    public void UnknownGoBadInstant_NotEligible()
        // We refuse to delete a target whose continuous-downtime duration we can't prove.
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines), MachineHealthStatus.Unavailable, unavailableSince: null, Now)
            .ShouldBeFalse();

    [Fact]
    public void WithinGracePeriod_NotEligible()
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400), MachineHealthStatus.Unavailable, Now.AddHours(-1), Now)
            .ShouldBeFalse();

    [Fact]
    public void ExactlyAtGraceBoundary_Eligible()
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 3600), MachineHealthStatus.Unavailable, Now.AddSeconds(-3600), Now)
            .ShouldBeTrue();

    [Fact]
    public void PastGracePeriod_Eligible()
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds: 86400), MachineHealthStatus.Unavailable, Now.AddDays(-7), Now)
            .ShouldBeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveGrace_NotEligible(int afterSeconds)
        // Misconfigured grace period must never collapse into "delete immediately".
        => MachineCleanupEvaluator.IsEligible(Policy(DeleteMachinesBehavior.DeleteUnavailableMachines, afterSeconds), MachineHealthStatus.Unavailable, Now.AddDays(-30), Now)
            .ShouldBeFalse();
}

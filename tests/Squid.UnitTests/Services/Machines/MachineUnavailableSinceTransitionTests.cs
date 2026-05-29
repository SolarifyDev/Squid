using Squid.Core.Services.Machines;
using Squid.Message.Enums;

namespace Squid.UnitTests.Services.Machines;

/// <summary>
/// Pins the UnavailableSince transition (drives the cleanup grace period): stamp on
/// entry to Unavailable, preserve while it stays Unavailable, clear on any recovery.
/// </summary>
public class MachineUnavailableSinceTransitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = Now.AddDays(-3);

    [Theory]
    [InlineData(MachineHealthStatus.Unknown)]
    [InlineData(MachineHealthStatus.Healthy)]
    [InlineData(MachineHealthStatus.HasWarnings)]
    [InlineData(MachineHealthStatus.Unhealthy)]
    public void EnteringUnavailable_StampsNow(MachineHealthStatus previous)
        => MachineHealthCheckService.ResolveUnavailableSince(previous, previousUnavailableSince: null, MachineHealthStatus.Unavailable, Now)
            .ShouldBe(Now);

    [Fact]
    public void StayingUnavailable_PreservesOriginalInstant()
        => MachineHealthCheckService.ResolveUnavailableSince(MachineHealthStatus.Unavailable, Earlier, MachineHealthStatus.Unavailable, Now)
            .ShouldBe(Earlier);

    [Fact]
    public void StayingUnavailable_WithNullOrigin_StaysNull()
        // A machine that was already Unavailable before the column existed keeps a null
        // origin (and so stays cleanup-ineligible) until it recovers and goes bad again.
        => MachineHealthCheckService.ResolveUnavailableSince(MachineHealthStatus.Unavailable, previousUnavailableSince: null, MachineHealthStatus.Unavailable, Now)
            .ShouldBeNull();

    [Theory]
    [InlineData(MachineHealthStatus.Healthy)]
    [InlineData(MachineHealthStatus.Unknown)]
    [InlineData(MachineHealthStatus.HasWarnings)]
    [InlineData(MachineHealthStatus.Unhealthy)]
    public void Recovering_ClearsInstant(MachineHealthStatus next)
        => MachineHealthCheckService.ResolveUnavailableSince(MachineHealthStatus.Unavailable, Earlier, next, Now)
            .ShouldBeNull();
}

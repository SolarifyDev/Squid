using System.Linq;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.Machines.Upgrade;
using Squid.Core.Settings.Halibut;

namespace Squid.UnitTests.Services.Machines.Upgrade;

/// <summary>
/// Pinning guard for A4 (server-side heartbeat cancellation during upgrade).
/// The infrastructure (<see cref="HalibutScriptObserver"/>'s liveness probe
/// loop + <c>AgentUnreachableException</c>) predates the Phase 3 upgrade
/// roadmap — A4's value is "verify this is actually wired into the upgrade
/// path AND the defaults are tight enough that operators don't wait 30 min
/// for a dead agent to be detected."
///
/// <para>If a future refactor disconnects the probe from the upgrade
/// observer — for example, by adding a parameterless ctor path that
/// skips the probe — these tests catch it before a real agent hang
/// stretches the Redis-lock recovery window past the designed 10s.</para>
/// </summary>
public sealed class UpgradeLivenessCancellationTests
{
    [Fact]
    public void LinuxTentacleUpgradeStrategy_UsesHalibutScriptObserverInterface_NotFallbackInstantiation()
    {
        // If the strategy ever switched to `new HalibutScriptObserver()`
        // (the parameterless ctor path), it would get the default
        // settings AND a null liveness probe — meaning agent-hang
        // detection would silently stop working. Pin via reflection that
        // the ONLY dependency on the observer is the interface, not a
        // concrete ctor.
        var ctor = typeof(LinuxTentacleUpgradeStrategy).GetConstructors().Single();

        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToArray();

        paramTypes.ShouldContain(typeof(IHalibutScriptObserver),
            customMessage: "LinuxTentacleUpgradeStrategy MUST inject IHalibutScriptObserver (DI-wired with liveness probe), " +
                           "NOT the concrete HalibutScriptObserver via a parameterless ctor.");

        paramTypes.ShouldNotContain(typeof(HalibutScriptObserver),
            customMessage: "Must not directly depend on the concrete type — that would bypass DI and lose the liveness probe wiring.");
    }

    [Fact]
    public void LivenessSettings_FailureThreshold_IsTight_ForFastAgentUnreachableDetection()
    {
        // Default must give us sub-30-second detection of an unresponsive
        // agent during upgrade. Too high (say 10) → 50s+ before
        // AgentUnreachableException → Redis lock held longer than A2's
        // 10-min staleness threshold considers reasonable.
        var defaults = new LivenessSettings();

        defaults.FailureThreshold.ShouldBeLessThanOrEqualTo(5,
            customMessage: $"FailureThreshold ({defaults.FailureThreshold}) too high — >5 consecutive failures at " +
                           $"{defaults.ProbeIntervalSeconds}s interval = >25s before we cancel. An operator clicking " +
                           "Upgrade on an already-dead agent would wait unreasonably long before getting a failure response.");

        defaults.FailureThreshold.ShouldBeGreaterThanOrEqualTo(2,
            customMessage: $"FailureThreshold ({defaults.FailureThreshold}) too low — a single transient probe failure " +
                           "would falsely cancel a healthy upgrade. 2+ consecutive = absorbs ordinary network hiccups.");
    }

    [Fact]
    public void LivenessSettings_ProbeIntervalSeconds_IsTight_ButNotHammering()
    {
        var defaults = new LivenessSettings();

        defaults.ProbeIntervalSeconds.ShouldBeLessThanOrEqualTo(10,
            customMessage: "ProbeIntervalSeconds > 10 makes detection too slow (see FailureThreshold analysis).");

        defaults.ProbeIntervalSeconds.ShouldBeGreaterThanOrEqualTo(2,
            customMessage: "ProbeIntervalSeconds < 2 hammers the agent with capabilities probes — noisy on slow/cross-border links.");
    }

    [Fact]
    public void LivenessSettings_ProbeTimeout_StaysUnderProbeInterval()
    {
        // If ProbeTimeout >= ProbeInterval, two consecutive probes
        // overlap — wastes Halibut connections and can mask a slow
        // agent as "still alive" because one probe hasn't timed out
        // before the next fires.
        var defaults = new LivenessSettings();

        defaults.ProbeTimeoutSeconds.ShouldBeLessThan(defaults.ProbeIntervalSeconds,
            customMessage: $"ProbeTimeoutSeconds ({defaults.ProbeTimeoutSeconds}) must be < ProbeIntervalSeconds " +
                           $"({defaults.ProbeIntervalSeconds}) — otherwise probes overlap and slow agents appear alive.");
    }

    [Fact]
    public void AgentUnreachableException_DetectionWindow_StaysUnderA2StalenessThreshold()
    {
        // The cancellation chain: probe every X seconds, fail N times
        // in a row, throw AgentUnreachableException. Total detection
        // window ≈ X * N. This window MUST stay comfortably under A2's
        // StalenessThreshold (10 min) so the heartbeat path catches
        // dead agents BEFORE the A2 reconciler's slower safety net
        // kicks in. Otherwise operators experience the longer wait.
        var defaults = new LivenessSettings();
        var detectionWindowSeconds = defaults.ProbeIntervalSeconds * defaults.FailureThreshold;

        detectionWindowSeconds.ShouldBeLessThanOrEqualTo(60,
            customMessage: $"Heartbeat detection window ({detectionWindowSeconds}s) should stay under 60s — " +
                           "A4's value is fast cancellation vs A2's 10-min reconciliation floor.");
    }
}

using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// P1-Phase12.E.3 — registered counterpart to
/// <see cref="LinuxTentacleUpgradeStrategy"/> that claims Windows agents
/// behind the same Halibut wire-protocol communication styles
/// (<c>TentaclePolling</c> / <c>TentacleListening</c>).
///
/// <para><b>Phase 12.E.3 ships routing only.</b> The actual dispatch (load
/// the embedded <c>upgrade-windows-tentacle.ps1</c> template, concatenate
/// every <see cref="IWindowsUpgradeMethod"/>'s rendered snippet into the
/// <c>{{INSTALL_METHODS}}</c> placeholder, substitute the remaining
/// placeholders, send the resulting script over Halibut, observe the
/// outcome) is Phase 12.E.4's deliverable. Until E.4 lands, this strategy
/// returns a clear <see cref="MachineUpgradeStatus.NotSupported"/> with a
/// remediation hint that points the operator at the planned roadmap step.
/// Without this stub, a click on "Upgrade" against a Windows tentacle would
/// surface as a confusing "no strategy registered for style 'TentaclePolling'"
/// error — the operator wouldn't know whether Windows is unsupported in
/// principle or just unfinished. This stub draws the line clearly: Windows
/// tentacles are recognised, dispatch arrives in Phase 12.E.4.</para>
///
/// <para><b>Why ship a registered stub instead of doing nothing in E.3:</b>
/// the Phase-12.E.3 architectural change widens
/// <see cref="IMachineUpgradeStrategy.CanHandle"/> to take
/// <see cref="MachineRuntimeCapabilities"/> so the resolver can route by
/// OS. With the widening in place but NO Windows strategy registered, the
/// "exactly one owner" invariant in
/// <c>MachineUpgradeService.ResolveStrategy</c> would correctly reject the
/// Linux strategy for Windows machines (because Linux now skips Windows),
/// but no other strategy would claim them either → operators see "no
/// strategy registered for style". The stub makes the migration story
/// honest: "we routed correctly, we just haven't shipped the actual
/// upgrade yet, here's where to look on the roadmap."</para>
/// </summary>
public sealed class WindowsTentacleUpgradeStrategy : IMachineUpgradeStrategy
{
    public bool CanHandle(string communicationStyle, MachineRuntimeCapabilities capabilities)
    {
        var matchesStyle = communicationStyle == nameof(CommunicationStyle.TentaclePolling)
                        || communicationStyle == nameof(CommunicationStyle.TentacleListening);

        if (!matchesStyle) return false;

        return IsWindowsAgent(capabilities);
    }

    public Task<MachineUpgradeOutcome> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        // Phase 12.E.3 placeholder: routing wired, dispatch deferred to E.4.
        // Operator sees a clear roadmap-aligned message instead of a generic
        // "not registered" error. AgentVersionMayHaveChanged = false because
        // the stub did nothing — the runtime cache is still valid and the
        // server doesn't need to invalidate it.
        return Task.FromResult(new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.NotSupported,
            Detail =
                "Windows tentacle in-UI upgrade is planned for Phase 12.E.4 of the self-upgrade roadmap. " +
                "Phase 12.E.3 (this release) wires the OS-aware strategy resolver — Windows agents now " +
                "route to a dedicated strategy instead of falling through to the Linux dispatch — but " +
                "the Halibut script send + PowerShell template render + Phase B detach mechanism still " +
                "ship in 12.E.4. Until then, run install-tentacle.ps1 -Version " + (targetVersion ?? "<target>") +
                " manually on the agent host to upgrade in place.",
            AgentVersionMayHaveChanged = false
        });
    }

    private static bool IsWindowsAgent(MachineRuntimeCapabilities capabilities)
    {
        if (capabilities == null) return false;

        return capabilities.Os.Equals("Windows", StringComparison.OrdinalIgnoreCase);
    }
}

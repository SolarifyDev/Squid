using Squid.Core.DependencyInjection;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Hardening;

namespace Squid.Core.Services.Machines.Cleanup;

public sealed record MachineCleanupOutcome(EnforcementMode Mode, int Scanned, int Eligible, int Deleted);

public interface IMachineCleanupService : IScopedDependency
{
    Task<MachineCleanupOutcome> EnforceCleanupAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Enforces the machine-policy "Clean up — delete unavailable deployment targets
/// after N" behaviour. Runs on a schedule via <c>MachineCleanupRecurringJob</c>.
///
/// <para>Double-opt-in for this destructive operation: a machine is eligible only
/// when its policy sets <see cref="DeleteMachinesBehavior.DeleteUnavailableMachines"/>
/// AND it has been continuously unavailable for the configured grace period
/// (<see cref="MachineCleanupEvaluator"/>). Whether an eligible machine is merely
/// logged or actually deleted is then gated by <see cref="MachineCleanupEnforcement"/>
/// (default <c>warn</c> = dry-run). Deletion goes through the same
/// <see cref="IMachineService.DeleteMachinesAsync"/> path the operator-facing delete
/// uses, so Halibut trust is reconfigured exactly as for a manual removal.</para>
/// </summary>
public sealed class MachineCleanupService(
    IMachineDataProvider machineDataProvider,
    IMachinePolicyDataProvider policyDataProvider,
    IMachineService machineService) : IMachineCleanupService
{
    public async Task<MachineCleanupOutcome> EnforceCleanupAsync(CancellationToken cancellationToken = default)
    {
        var mode = MachineCleanupEnforcement.ResolveMode();

        if (mode == EnforcementMode.Off)
        {
            Log.Debug("[MachineCleanup] Disabled via {EnvVar}=off — skipping sweep.", MachineCleanupEnforcement.EnvVar);
            return new MachineCleanupOutcome(mode, 0, 0, 0);
        }

        var now = DateTimeOffset.UtcNow;

        var (_, machines) = await machineDataProvider.GetMachinesAllSpacesPagingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var eligible = await ResolveEligibleAsync(machines, now, cancellationToken).ConfigureAwait(false);

        if (eligible.Count == 0)
            return new MachineCleanupOutcome(mode, machines.Count, 0, 0);

        if (mode == EnforcementMode.Warn)
        {
            WarnWouldDelete(eligible);
            return new MachineCleanupOutcome(mode, machines.Count, eligible.Count, 0);
        }

        var deleted = await DeleteAsync(eligible, cancellationToken).ConfigureAwait(false);

        return new MachineCleanupOutcome(mode, machines.Count, eligible.Count, deleted);
    }

    private async Task<List<Machine>> ResolveEligibleAsync(List<Machine> machines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var eligible = new List<Machine>();

        foreach (var machine in machines)
        {
            // Cheap pre-filter before the per-machine policy load.
            if (machine.HealthStatus != MachineHealthStatus.Unavailable || machine.MachinePolicyId == null)
                continue;

            var policy = await LoadCleanupPolicyAsync(machine.MachinePolicyId.Value, cancellationToken).ConfigureAwait(false);

            if (MachineCleanupEvaluator.IsEligible(policy, machine.HealthStatus, machine.UnavailableSince, now))
                eligible.Add(machine);
        }

        return eligible;
    }

    private void WarnWouldDelete(List<Machine> eligible)
    {
        foreach (var machine in eligible)
            Log.Warning(
                "[MachineCleanup] WOULD delete unavailable target {MachineName} (id {MachineId}, unavailable since {Since:o}) per its machine policy. " +
                "Set {EnvVar}=strict to actually delete, or =off to silence.",
                machine.Name, machine.Id, machine.UnavailableSince, MachineCleanupEnforcement.EnvVar);
    }

    private async Task<int> DeleteAsync(List<Machine> eligible, CancellationToken cancellationToken)
    {
        var ids = eligible.Select(m => m.Id).ToList();

        try
        {
            await machineService.DeleteMachinesAsync(new DeleteMachinesCommand { Ids = ids }, cancellationToken).ConfigureAwait(false);

            foreach (var machine in eligible)
                Log.Information(
                    "[MachineCleanup] Deleted unavailable target {MachineName} (id {MachineId}, unavailable since {Since:o}) per its machine policy.",
                    machine.Name, machine.Id, machine.UnavailableSince);

            return ids.Count;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[MachineCleanup] Failed to delete {Count} unavailable target(s) — leaving them in place.", ids.Count);
            return 0;
        }
    }

    private async Task<Message.Models.Deployments.Machine.MachineCleanupPolicyDto> LoadCleanupPolicyAsync(int policyId, CancellationToken cancellationToken)
    {
        var policy = await policyDataProvider.GetByIdAsync(policyId, cancellationToken).ConfigureAwait(false);

        return policy == null ? null : MachinePolicyService.ToDto(policy).MachineCleanupPolicy;
    }
}

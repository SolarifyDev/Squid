using Squid.Core.Services.Deployments.Machine;

namespace Squid.Core.Services.DeploymentExecution;

/// <summary>
/// Selects deployment target machines using Squid's role/tenant filtering pipeline:
///   1. Get candidate pool (specific machine or auto-select by environment)
///   2. Filter by environment (validate machine belongs to target environment)
///   3. Filter disabled (exclude IsDisabled machines)
///   4. (Future) Filter by health status
///   5. (Future) Apply exclusion list
/// Per-step role filtering is provided as a static utility for the executor.
/// </summary>
public class DeploymentTargetFinder : IDeploymentTargetFinder
{
    private readonly IMachineDataProvider _machineDataProvider;

    public DeploymentTargetFinder(IMachineDataProvider machineDataProvider)
    {
        _machineDataProvider = machineDataProvider;
    }

    public async Task<List<Persistence.Entities.Deployments.Machine>> FindTargetsAsync(
        Persistence.Entities.Deployments.Deployment deployment,
        CancellationToken cancellationToken)
    {
        if (deployment == null) throw new ArgumentNullException(nameof(deployment));

        var candidates = await GetCandidatePoolAsync(deployment, cancellationToken).ConfigureAwait(false);
        candidates = FilterByEnvironment(candidates, deployment.EnvironmentId);
        candidates = FilterDisabled(candidates);

        Log.Information("Found {Count} target machines for deployment {DeploymentId}",
            candidates.Count, deployment.Id);

        return candidates;
    }

    private async Task<List<Persistence.Entities.Deployments.Machine>> GetCandidatePoolAsync(
        Persistence.Entities.Deployments.Deployment deployment,
        CancellationToken ct)
    {
        if (deployment.MachineId > 0)
        {
            var machine = await _machineDataProvider.GetMachinesByIdAsync(deployment.MachineId, ct).ConfigureAwait(false);
            return machine != null
                ? new List<Persistence.Entities.Deployments.Machine> { machine }
                : new List<Persistence.Entities.Deployments.Machine>();
        }

        var environmentIds = new HashSet<int> { deployment.EnvironmentId };
        return await _machineDataProvider.GetMachinesByFilterAsync(environmentIds, new HashSet<string>(), ct).ConfigureAwait(false);
    }

    private static List<Persistence.Entities.Deployments.Machine> FilterByEnvironment(
        List<Persistence.Entities.Deployments.Machine> candidates,
        int environmentId)
    {
        return candidates
            .Where(m => ParseIds(m.EnvironmentIds).Contains(environmentId))
            .ToList();
    }

    private static List<Persistence.Entities.Deployments.Machine> FilterDisabled(
        List<Persistence.Entities.Deployments.Machine> candidates)
    {
        return candidates
            .Where(m => !m.IsDisabled)
            .ToList();
    }

    // === Static utilities for per-step filtering (used by executor) ===

    public static HashSet<int> ParseIds(string ids)
    {
        if (string.IsNullOrEmpty(ids)) return new HashSet<int>();

        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var id) ? id : 0)
            .Where(id => id > 0)
            .ToHashSet();
    }

    public static HashSet<string> ParseRoles(string roles)
    {
        if (string.IsNullOrEmpty(roles)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Filters machines by target roles using OR logic (deployment-step pattern:
    /// a machine matches if it has ANY of the target roles).
    /// Empty or null targetRoles returns all machines (no filtering).
    /// </summary>
    public static List<Persistence.Entities.Deployments.Machine> FilterByRoles(
        List<Persistence.Entities.Deployments.Machine> candidates,
        HashSet<string> targetRoles)
    {
        if (targetRoles == null || targetRoles.Count == 0)
            return candidates;

        return candidates
            .Where(m => ParseRoles(m.Roles).Overlaps(targetRoles))
            .ToList();
    }

    /// <summary>
    /// Collects all target roles from all steps (global pre-filtering).
    /// Used to narrow down machines before the per-target execution loop,
    /// avoiding wasted LoadAccount/ContributeEndpointVariables/ExtractCalamari
    /// on machines that won't execute any step.
    /// Returns empty set if no steps define target roles (meaning all machines needed).
    /// </summary>
    public static HashSet<string> CollectAllTargetRoles(
        List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> steps)
    {
        if (steps == null || steps.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var allRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasStepWithoutRoles = false;

        foreach (var step in steps)
        {
            if (step.IsDisabled)
                continue;

            var rolesProp = step.Properties?
                .FirstOrDefault(p => p.PropertyName == DeploymentVariables.Action.TargetRoles);

            if (rolesProp == null || string.IsNullOrEmpty(rolesProp.PropertyValue))
            {
                hasStepWithoutRoles = true;
            }
            else
            {
                var stepRoles = ParseRoles(rolesProp.PropertyValue);
                allRoles.UnionWith(stepRoles);
            }
        }

        // If any enabled step has no target roles, ALL machines are needed
        if (hasStepWithoutRoles)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return allRoles;
    }
}

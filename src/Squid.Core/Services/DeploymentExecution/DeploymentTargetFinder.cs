using System.Text.Json;
using Squid.Core.Services.Machines;
using Squid.Message.Models.Deployments.Deployment;

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

    public async Task<List<Persistence.Entities.Deployments.Machine>> FindTargetsAsync(Persistence.Entities.Deployments.Deployment deployment, CancellationToken cancellationToken)
    {
        if (deployment == null) throw new ArgumentNullException(nameof(deployment));

        var selection = ParseTargetSelection(deployment.Json);
        var candidates = await GetCandidatePoolAsync(deployment, cancellationToken).ConfigureAwait(false);
        
        candidates = FilterByEnvironment(candidates, deployment.EnvironmentId);
        candidates = FilterDisabled(candidates);
        candidates = ApplyMachineSelection(candidates, selection);

        Log.Information("Found {Count} target machines for deployment {DeploymentId}", candidates.Count, deployment.Id);

        return candidates;
    }

    private async Task<List<Persistence.Entities.Deployments.Machine>> GetCandidatePoolAsync(Persistence.Entities.Deployments.Deployment deployment, CancellationToken ct)
    {
        if (deployment.MachineId > 0)
        {
            var machine = await _machineDataProvider.GetMachinesByIdAsync(deployment.MachineId, ct).ConfigureAwait(false);
            
            return machine != null ? [machine] : [];
        }

        var environmentIds = new HashSet<int> { deployment.EnvironmentId };
        
        return await _machineDataProvider.GetMachinesByFilterAsync(environmentIds, new HashSet<string>(), ct).ConfigureAwait(false);
    }

    private static List<Persistence.Entities.Deployments.Machine> FilterByEnvironment(List<Persistence.Entities.Deployments.Machine> candidates, int environmentId)
    {
        return candidates.Where(m => ParseIds(m.EnvironmentIds).Contains(environmentId)).ToList();
    }

    private static List<Persistence.Entities.Deployments.Machine> FilterDisabled(List<Persistence.Entities.Deployments.Machine> candidates)
    {
        return candidates.Where(m => !m.IsDisabled).ToList();
    }

    public static List<Persistence.Entities.Deployments.Machine> ApplyMachineSelection(List<Persistence.Entities.Deployments.Machine> candidates, DeploymentMachineSelection selection)
    {
        if (selection == null || !selection.HasConstraints)
            return candidates;

        var filtered = candidates;

        if (selection.SpecificMachineIds.Count > 0)
            filtered = filtered.Where(m => selection.SpecificMachineIds.Contains(m.Id)).ToList();

        if (selection.ExcludedMachineIds.Count > 0)
            filtered = filtered.Where(m => !selection.ExcludedMachineIds.Contains(m.Id)).ToList();

        return filtered;
    }

    public static DeploymentMachineSelection ParseTargetSelection(string deploymentJson)
    {
        if (string.IsNullOrWhiteSpace(deploymentJson))
            return DeploymentMachineSelection.Empty;

        try
        {
            var payload = JsonSerializer.Deserialize<DeploymentRequestPayload>(deploymentJson);
            
            if (payload == null) return DeploymentMachineSelection.Empty;

            return new DeploymentMachineSelection
            {
                SpecificMachineIds = NormalizeMachineIds(payload.SpecificMachineIds),
                ExcludedMachineIds = NormalizeMachineIds(payload.ExcludedMachineIds)
            };
        }
        catch
        {
            return DeploymentMachineSelection.Empty;
        }
    }

    private static HashSet<int> NormalizeMachineIds(IEnumerable<int> machineIds)
    {
        if (machineIds == null)
            return new HashSet<int>();

        return machineIds.Where(id => id > 0).ToHashSet();
    }

    // === Static utilities for per-step filtering (used by executor) ===

    public static HashSet<int> ParseIds(string json)
    {
        if (string.IsNullOrEmpty(json)) return new HashSet<int>();

        return JsonSerializer.Deserialize<List<int>>(json)?.ToHashSet() ?? new HashSet<int>();
    }

    public static HashSet<string> ParseRoles(string json)
    {
        if (string.IsNullOrEmpty(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static HashSet<string> ParseCsvRoles(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string SerializeIds(IEnumerable<int> ids)
    {
        return JsonSerializer.Serialize(ids.ToList());
    }

    public static string SerializeRoles(IEnumerable<string> roles)
    {
        return JsonSerializer.Serialize(roles.ToList());
    }

    /// <summary>
    /// Filters machines by target roles using OR logic (deployment-step pattern:
    /// a machine matches if it has ANY of the target roles).
    /// Empty or null targetRoles returns all machines (no filtering).
    /// </summary>
    public static List<Persistence.Entities.Deployments.Machine> FilterByRoles(List<Persistence.Entities.Deployments.Machine> candidates, HashSet<string> targetRoles)
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
    public static HashSet<string> CollectAllTargetRoles(List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> steps)
    {
        if (steps == null || steps.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hasStepWithoutRoles = false;
        var allRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (step.IsDisabled) continue;

            var rolesProp = step.Properties?.FirstOrDefault(p => p.PropertyName == DeploymentVariables.Action.TargetRoles);

            if (rolesProp == null || string.IsNullOrEmpty(rolesProp.PropertyValue))
            {
                hasStepWithoutRoles = true;
            }
            else
            {
                var stepRoles = ParseCsvRoles(rolesProp.PropertyValue);
                
                allRoles.UnionWith(stepRoles);
            }
        }

        // If any enabled step has no target roles, ALL machines are needed
        if (hasStepWithoutRoles)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return allRoles;
    }
    
    public sealed class DeploymentMachineSelection
    {
        public static readonly DeploymentMachineSelection Empty = new();

        public HashSet<int> SpecificMachineIds { get; init; } = new();

        public HashSet<int> ExcludedMachineIds { get; init; } = new();

        public bool HasConstraints => SpecificMachineIds.Count > 0 || ExcludedMachineIds.Count > 0;
    }
}

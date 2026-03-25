using System.Text.Json;
using Squid.Core.Services.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

/// <summary>
/// Selects deployment target machines using Squid's role/tenant filtering pipeline:
///   1. Get candidate pool (specific machine or auto-select by environment)
///   2. Filter by environment (validate machine belongs to target environment)
///   3. Filter disabled (exclude IsDisabled machines)
///   4. Filter by health status (exclude Unhealthy/Unavailable)
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

    public static (List<Persistence.Entities.Deployments.Machine> Healthy, List<Persistence.Entities.Deployments.Machine> Excluded) FilterByHealthStatus(List<Persistence.Entities.Deployments.Machine> candidates)
    {
        var healthy = new List<Persistence.Entities.Deployments.Machine>();
        var excluded = new List<Persistence.Entities.Deployments.Machine>();

        foreach (var m in candidates)
        {
            if (m.HealthStatus is MachineHealthStatus.Unhealthy or MachineHealthStatus.Unavailable)
                excluded.Add(m);
            else
                healthy.Add(m);
        }

        return (healthy, excluded);
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
        var payload = ParseRequestPayload(deploymentJson);

        return new DeploymentMachineSelection
        {
            SpecificMachineIds = NormalizePositiveIds(payload.SpecificMachineIds),
            ExcludedMachineIds = NormalizePositiveIds(payload.ExcludedMachineIds)
        };
    }

    public static DeploymentRequestPayload ParseRequestPayload(string deploymentJson)
    {
        if (string.IsNullOrWhiteSpace(deploymentJson))
            return new DeploymentRequestPayload();

        try
        {
            return JsonSerializer.Deserialize<DeploymentRequestPayload>(deploymentJson) ?? new DeploymentRequestPayload();
        }
        catch
        {
            return new DeploymentRequestPayload();
        }
    }

    private static HashSet<int> NormalizePositiveIds(IEnumerable<int> machineIds)
    {
        if (machineIds == null)
            return new HashSet<int>();

        return machineIds.Where(id => id > 0).ToHashSet();
    }

    // === Static utilities for per-step filtering (used by executor) ===

    public static HashSet<int> ParseIds(string json)
    {
        if (string.IsNullOrEmpty(json)) return new HashSet<int>();

        try
        {
            return JsonSerializer.Deserialize<List<int>>(json)?.ToHashSet() ?? new HashSet<int>();
        }
        catch (JsonException)
        {
            var result = new HashSet<int>();

            foreach (var segment in json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(segment, out var id))
                    result.Add(id);
            }

            return result;
        }
    }

    public static HashSet<string> ParseRoles(string json)
    {
        if (string.IsNullOrEmpty(json)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json)?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return json.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
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
    /// When scopeResolver is provided, steps whose enabled actions are ALL StepLevel
    /// (e.g. Manual Intervention) are excluded from role collection — they don't
    /// iterate targets and should not prevent pre-filtering.
    /// </summary>
    public static HashSet<string> CollectAllTargetRoles(List<Squid.Message.Models.Deployments.Process.DeploymentStepDto> steps, Func<Squid.Message.Models.Deployments.Process.DeploymentActionDto, Handlers.ExecutionScope> scopeResolver = null)
    {
        if (steps == null || steps.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var hasStepWithoutRoles = false;
        var allRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in steps)
        {
            if (step.IsDisabled) continue;
            if (scopeResolver != null && IsStepLevelOnly(step, scopeResolver)) continue;

            var rolesProp = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.TargetRoles);

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

    private static bool IsStepLevelOnly(Squid.Message.Models.Deployments.Process.DeploymentStepDto step, Func<Squid.Message.Models.Deployments.Process.DeploymentActionDto, Handlers.ExecutionScope> scopeResolver)
    {
        var enabledActions = step.Actions?.Where(a => !a.IsDisabled).ToList();

        if (enabledActions == null || enabledActions.Count == 0) return false;

        return enabledActions.All(a => scopeResolver(a) == Handlers.ExecutionScope.StepLevel);
    }
    
    public sealed class DeploymentMachineSelection
    {
        public static readonly DeploymentMachineSelection Empty = new();

        public HashSet<int> SpecificMachineIds { get; init; } = new();

        public HashSet<int> ExcludedMachineIds { get; init; } = new();

        public bool HasConstraints => SpecificMachineIds.Count > 0 || ExcludedMachineIds.Count > 0;
    }
}

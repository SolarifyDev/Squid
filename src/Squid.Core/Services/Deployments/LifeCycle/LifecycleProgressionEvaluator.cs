using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface ILifecycleProgressionEvaluator : IScopedDependency
{
    Task<PhaseProgressionResult> EvaluateProgressionAsync(int lifecycleId, int projectId, CancellationToken cancellationToken);
}

public class PhaseProgressionResult
{
    public int CurrentPhaseIndex { get; set; }

    public List<int> AllowedEnvironmentIds { get; set; } = new();

    public List<int> AutoDeployEnvironmentIds { get; set; } = new();

    public List<PhaseStatus> Phases { get; set; } = new();
}

public class PhaseStatus
{
    public int PhaseId { get; set; }

    public string PhaseName { get; set; }

    public int SortOrder { get; set; }

    public bool IsComplete { get; set; }

    public bool IsOptional { get; set; }

    public List<int> AutomaticEnvironmentIds { get; set; } = new();

    public List<int> OptionalEnvironmentIds { get; set; } = new();

    public List<int> DeployedEnvironmentIds { get; set; } = new();
}

public class LifecycleProgressionEvaluator(
    ILifeCycleDataProvider lifeCycleDataProvider,
    IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
    IRepository repository) : ILifecycleProgressionEvaluator
{
    public async Task<PhaseProgressionResult> EvaluateProgressionAsync(
        int lifecycleId, int projectId, CancellationToken cancellationToken)
    {
        var phases = await lifeCycleDataProvider.GetPhasesByLifecycleIdAsync(lifecycleId, cancellationToken).ConfigureAwait(false);

        var phaseIds = phases.Select(p => p.Id).ToList();
        var phaseEnvironments = await lifeCycleDataProvider.GetPhaseEnvironmentsByPhaseIdsAsync(phaseIds, cancellationToken).ConfigureAwait(false);

        var deployedEnvironmentIds = await GetDeployedEnvironmentIdsAsync(projectId, cancellationToken).ConfigureAwait(false);

        return EvaluatePhases(phases, phaseEnvironments, deployedEnvironmentIds);
    }

    public static PhaseProgressionResult EvaluatePhases(
        List<LifecyclePhase> phases,
        List<LifecyclePhaseEnvironment> phaseEnvironments,
        HashSet<int> deployedEnvironmentIds)
    {
        var result = new PhaseProgressionResult();
        var envsByPhase = phaseEnvironments.GroupBy(pe => pe.PhaseId).ToDictionary(g => g.Key, g => g.ToList());
        var allPriorEnvironmentIds = new HashSet<int>();
        var lastCompletedPhaseIndex = -1;

        for (var i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            var envs = envsByPhase.GetValueOrDefault(phase.Id) ?? new List<LifecyclePhaseEnvironment>();

            var automaticIds = envs
                .Where(e => e.TargetType == LifecyclePhaseEnvironmentTargetType.Automatic)
                .Select(e => e.EnvironmentId).ToList();

            var optionalIds = envs
                .Where(e => e.TargetType == LifecyclePhaseEnvironmentTargetType.Optional)
                .Select(e => e.EnvironmentId).ToList();

            var allPhaseEnvIds = automaticIds.Concat(optionalIds).ToList();

            // Empty phase inherits all prior environments
            if (allPhaseEnvIds.Count == 0)
                allPhaseEnvIds = allPriorEnvironmentIds.ToList();

            var deployedInPhase = allPhaseEnvIds.Where(deployedEnvironmentIds.Contains).ToList();
            var isComplete = EvaluatePhaseCompletion(phase, automaticIds, optionalIds, allPhaseEnvIds, deployedInPhase);

            var status = new PhaseStatus
            {
                PhaseId = phase.Id,
                PhaseName = phase.Name,
                SortOrder = phase.SortOrder,
                IsComplete = isComplete,
                IsOptional = phase.IsOptionalPhase,
                AutomaticEnvironmentIds = automaticIds,
                OptionalEnvironmentIds = optionalIds,
                DeployedEnvironmentIds = deployedInPhase
            };

            result.Phases.Add(status);

            if (isComplete)
                lastCompletedPhaseIndex = i;

            allPriorEnvironmentIds.UnionWith(allPhaseEnvIds);
        }

        result.CurrentPhaseIndex = lastCompletedPhaseIndex + 1;

        BuildAllowedEnvironmentIds(result, phases, envsByPhase, lastCompletedPhaseIndex);
        BuildAutoDeployEnvironmentIds(result, phases, envsByPhase, lastCompletedPhaseIndex);

        return result;
    }

    private static bool EvaluatePhaseCompletion(
        LifecyclePhase phase,
        List<int> automaticIds,
        List<int> optionalIds,
        List<int> allPhaseEnvIds,
        List<int> deployedInPhase)
    {
        // Optional phases are always considered complete (skippable)
        if (phase.IsOptionalPhase) return true;

        if (allPhaseEnvIds.Count == 0) return true;

        // MinimumEnvironmentsBeforePromotion > 0 → at least N targets deployed
        if (phase.MinimumEnvironmentsBeforePromotion > 0)
            return deployedInPhase.Count >= phase.MinimumEnvironmentsBeforePromotion;

        // MinimumEnvironmentsBeforePromotion == 0 → all automatic targets must have deployments
        var requiredIds = automaticIds.Count > 0 ? automaticIds : allPhaseEnvIds;

        return requiredIds.All(id => deployedInPhase.Contains(id));
    }

    private static void BuildAllowedEnvironmentIds(
        PhaseProgressionResult result,
        List<LifecyclePhase> phases,
        Dictionary<int, List<LifecyclePhaseEnvironment>> envsByPhase,
        int lastCompletedPhaseIndex)
    {
        var allowed = new HashSet<int>();

        // All environments from completed phases are allowed
        for (var i = 0; i <= lastCompletedPhaseIndex && i < phases.Count; i++)
        {
            AddPhaseEnvironmentIds(allowed, phases[i].Id, envsByPhase);
        }

        // Current incomplete phase environments are also allowed
        var currentIndex = lastCompletedPhaseIndex + 1;
        if (currentIndex < phases.Count)
        {
            AddPhaseEnvironmentIds(allowed, phases[currentIndex].Id, envsByPhase);
        }

        result.AllowedEnvironmentIds = allowed.ToList();
    }

    private static void BuildAutoDeployEnvironmentIds(
        PhaseProgressionResult result,
        List<LifecyclePhase> phases,
        Dictionary<int, List<LifecyclePhaseEnvironment>> envsByPhase,
        int lastCompletedPhaseIndex)
    {
        // Auto-deploy targets of the next phase after last completed
        var nextPhaseIndex = lastCompletedPhaseIndex + 1;
        if (nextPhaseIndex >= phases.Count) return;

        var nextPhase = phases[nextPhaseIndex];
        var envs = envsByPhase.GetValueOrDefault(nextPhase.Id);
        if (envs == null) return;

        result.AutoDeployEnvironmentIds = envs
            .Where(e => e.TargetType == LifecyclePhaseEnvironmentTargetType.Automatic)
            .Select(e => e.EnvironmentId).ToList();
    }

    private static void AddPhaseEnvironmentIds(HashSet<int> set, int phaseId, Dictionary<int, List<LifecyclePhaseEnvironment>> envsByPhase)
    {
        var envs = envsByPhase.GetValueOrDefault(phaseId);
        if (envs == null) return;

        foreach (var env in envs)
        {
            set.Add(env.EnvironmentId);
        }
    }

    private async Task<HashSet<int>> GetDeployedEnvironmentIdsAsync(int projectId, CancellationToken cancellationToken)
    {
        // Get successful deployment completions, then join with Deployment to get EnvironmentIds
        var completions = await deploymentCompletionDataProvider
            .GetLatestSuccessfulCompletionsAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (completions.Count == 0) return new HashSet<int>();

        var deploymentIds = completions.Select(c => c.DeploymentId).Distinct().ToList();

        var deployments = await repository.QueryNoTracking<Deployment>(d => deploymentIds.Contains(d.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return deployments.Select(d => d.EnvironmentId).ToHashSet();
    }
}

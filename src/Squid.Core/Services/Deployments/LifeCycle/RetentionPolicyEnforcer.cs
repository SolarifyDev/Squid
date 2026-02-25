using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface IRetentionPolicyEnforcer : IScopedDependency
{
    Task<int> EnforceRetentionForAllProjectsAsync(CancellationToken cancellationToken);

    Task EnforceRetentionForProjectAsync(int projectId, CancellationToken cancellationToken);
}

public class RetentionPolicyEnforcer(
    IProjectDataProvider projectDataProvider,
    ILifeCycleDataProvider lifeCycleDataProvider,
    IReleaseDataProvider releaseDataProvider,
    IDeploymentCompletionDataProvider deploymentCompletionDataProvider,
    IRepository repository) : IRetentionPolicyEnforcer
{
    public async Task<int> EnforceRetentionForAllProjectsAsync(CancellationToken cancellationToken)
    {
        var projectIds = await repository.QueryNoTracking<Persistence.Entities.Deployments.Project>()
            .Where(p => !p.IsDisabled)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var projectId in projectIds)
        {
            try
            {
                await EnforceRetentionForProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Retention enforcement failed for project {ProjectId}", projectId);
            }
        }

        return projectIds.Count;
    }

    public async Task EnforceRetentionForProjectAsync(int projectId, CancellationToken cancellationToken)
    {
        var project = await projectDataProvider.GetProjectByIdAsync(projectId, cancellationToken).ConfigureAwait(false);
        
        if (project == null) return;

        var lifecycle = await lifeCycleDataProvider.GetLifecycleByIdAsync(project.LifecycleId, cancellationToken).ConfigureAwait(false);
        
        if (lifecycle == null) return;

        var phases = await lifeCycleDataProvider.GetPhasesByLifecycleIdAsync(lifecycle.Id, cancellationToken).ConfigureAwait(false);
        var phaseIds = phases.Select(p => p.Id).ToList();
        var phaseEnvironments = await lifeCycleDataProvider.GetPhaseEnvironmentsByPhaseIdsAsync(phaseIds, cancellationToken).ConfigureAwait(false);

        var currentlyDeployedReleaseIds = await GetCurrentlyDeployedReleaseIdsAsync(projectId, cancellationToken).ConfigureAwait(false);

        var envsByPhase = phaseEnvironments.GroupBy(pe => pe.PhaseId).ToDictionary(g => g.Key, g => g.Select(e => e.EnvironmentId).ToList());

        foreach (var phase in phases)
        {
            var effectiveKeepForever = phase.ReleaseRetentionKeepForever ?? lifecycle.ReleaseRetentionKeepForever;
            
            if (effectiveKeepForever) continue;

            var effectiveUnit = phase.ReleaseRetentionUnit ?? lifecycle.ReleaseRetentionUnit;
            var effectiveQuantity = phase.ReleaseRetentionQuantity ?? lifecycle.ReleaseRetentionQuantity;

            var environmentIds = envsByPhase.GetValueOrDefault(phase.Id) ?? new List<int>();

            await EnforceForEnvironmentsAsync(
                projectId, environmentIds, effectiveUnit, effectiveQuantity,
                currentlyDeployedReleaseIds, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnforceForEnvironmentsAsync(int projectId, List<int> environmentIds, RetentionPolicyUnit unit, int quantity, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        foreach (var environmentId in environmentIds)
        {
            var deployments = await repository
                .QueryNoTracking<Deployment>(d => d.ProjectId == projectId && d.EnvironmentId == environmentId)
                .OrderByDescending(d => d.Created)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var deploymentsToDelete = GetDeploymentsExceedingRetention(deployments, unit, quantity, currentlyDeployedReleaseIds);

            if (deploymentsToDelete.Count == 0) continue;

            Log.Information("Retention: deleting {Count} deployments for project {ProjectId} environment {EnvironmentId}", deploymentsToDelete.Count, projectId, environmentId);
        }
    }

    public static List<Deployment> GetDeploymentsExceedingRetention(List<Deployment> deployments, RetentionPolicyUnit unit, int quantity, HashSet<int> currentlyDeployedReleaseIds)
    {
        var result = new List<Deployment>();

        if (unit == RetentionPolicyUnit.Days || unit == RetentionPolicyUnit.Weeks ||
            unit == RetentionPolicyUnit.Months || unit == RetentionPolicyUnit.Years)
        {
            var cutoff = CalculateCutoff(unit, quantity);

            foreach (var deployment in deployments)
            {
                if (currentlyDeployedReleaseIds.Contains(deployment.ReleaseId)) continue;
                
                if (deployment.Created >= cutoff) continue;

                result.Add(deployment);
            }
        }

        return result;
    }

    private static DateTimeOffset CalculateCutoff(RetentionPolicyUnit unit, int quantity)
    {
        var now = DateTimeOffset.UtcNow;

        return unit switch
        {
            RetentionPolicyUnit.Days => now.AddDays(-quantity),
            RetentionPolicyUnit.Weeks => now.AddDays(-quantity * 7),
            RetentionPolicyUnit.Months => now.AddMonths(-quantity),
            RetentionPolicyUnit.Years => now.AddYears(-quantity),
            _ => now
        };
    }

    private async Task<HashSet<int>> GetCurrentlyDeployedReleaseIdsAsync(int projectId, CancellationToken cancellationToken)
    {
        var completions = await deploymentCompletionDataProvider
            .GetLatestSuccessfulCompletionsAsync(projectId, cancellationToken).ConfigureAwait(false);

        if (completions.Count == 0) return new HashSet<int>();

        var deploymentIds = completions.Select(c => c.DeploymentId).Distinct().ToList();

        var releaseIds = await releaseDataProvider
            .GetReleaseIdsByDeploymentIdsAsync(deploymentIds, cancellationToken).ConfigureAwait(false);

        return releaseIds.ToHashSet();
    }
}

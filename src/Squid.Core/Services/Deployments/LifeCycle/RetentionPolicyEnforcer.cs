using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Enums.Deployments;
using TaskState = Squid.Core.Services.Deployments.ServerTask.TaskState;

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
    IDeploymentDataProvider deploymentDataProvider,
    IRepository repository) : IRetentionPolicyEnforcer
{
    private static readonly string[] NonTerminalTaskStates = { TaskState.Pending, TaskState.Executing, TaskState.Cancelling, TaskState.Paused };

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

        await PruneExpiredReleasesAsync(projectId, lifecycle.ReleaseRetentionKeepForever, lifecycle.ReleaseRetentionUnit, lifecycle.ReleaseRetentionQuantity, currentlyDeployedReleaseIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnforceForEnvironmentsAsync(int projectId, List<int> environmentIds, RetentionPolicyUnit unit, int quantity, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        foreach (var environmentId in environmentIds)
        {
            var deployments = await repository
                .QueryNoTracking<Deployment>(d => d.ProjectId == projectId && d.EnvironmentId == environmentId)
                .OrderByDescending(d => d.CreatedDate)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            var deploymentsToDelete = GetDeploymentsExceedingRetention(deployments, unit, quantity, currentlyDeployedReleaseIds);

            if (deploymentsToDelete.Count == 0) continue;

            Log.Information("Retention: deleting {Count} deployments for project {ProjectId} environment {EnvironmentId}", deploymentsToDelete.Count, projectId, environmentId);

            var ids = deploymentsToDelete.Select(d => d.Id).ToList();
            var taskIds = deploymentsToDelete.Where(d => d.TaskId.HasValue).Select(d => d.TaskId!.Value).Distinct().ToList();

            // Delete the deployment LAST. It is the retention anchor: if any child delete
            // below crashes mid-way, the surviving deployment is re-selected next run and the
            // whole cleanup retries idempotently, so nothing is left permanently orphaned.
            await DeleteServerTaskDataAsync(taskIds, cancellationToken).ConfigureAwait(false);
            await deploymentCompletionDataProvider.DeleteByDeploymentIdsAsync(ids, cancellationToken).ConfigureAwait(false);
            await deploymentDataProvider.DeleteDeploymentsAsync(ids, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DeleteServerTaskDataAsync(List<int> taskIds, CancellationToken cancellationToken)
    {
        if (taskIds.Count == 0) return;

        await repository.ExecuteDeleteAsync<ServerTaskLog>(l => taskIds.Contains(l.ServerTaskId), cancellationToken).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<DeploymentInterruption>(i => taskIds.Contains(i.ServerTaskId), cancellationToken).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<DeploymentExecutionCheckpoint>(c => taskIds.Contains(c.ServerTaskId), cancellationToken).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<Persistence.Entities.Deployments.ActivityLog>(a => taskIds.Contains(a.ServerTaskId), cancellationToken).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<Persistence.Entities.Deployments.ServerTask>(t => taskIds.Contains(t.Id), cancellationToken).ConfigureAwait(false);
    }

    private async Task PruneExpiredReleasesAsync(int projectId, bool keepForever, RetentionPolicyUnit unit, int quantity, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        if (keepForever) return;

        if (unit == RetentionPolicyUnit.Items)
            await PruneReleasesByCountAsync(projectId, quantity, currentlyDeployedReleaseIds, cancellationToken).ConfigureAwait(false);
        else
            await PruneReleasesByAgeAsync(projectId, CalculateCutoff(unit, quantity), currentlyDeployedReleaseIds, cancellationToken).ConfigureAwait(false);
    }

    // Age-based pruning is conservative: it only removes releases that were NEVER deployed and are
    // past the window. A release that has ever had a deployment is kept (its deployment history is
    // untouched). Currently-deployed releases are always preserved.
    private async Task PruneReleasesByAgeAsync(int projectId, DateTimeOffset cutoff, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        var releases = await repository.QueryNoTracking<Persistence.Entities.Deployments.Release>(r => r.ProjectId == projectId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var releaseIdsWithDeployments = (await repository.QueryNoTracking<Deployment>(d => d.ProjectId == projectId)
            .Select(d => d.ReleaseId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var releasesToDelete = GetReleasesExceedingRetention(releases, cutoff, currentlyDeployedReleaseIds, releaseIdsWithDeployments);

        if (releasesToDelete.Count == 0) return;

        Log.Information("Retention: deleting {Count} releases for project {ProjectId}", releasesToDelete.Count, projectId);

        var releaseIds = releasesToDelete.Select(r => r.Id).ToList();
        var processSnapshotIds = releasesToDelete.Select(r => r.ProjectDeploymentProcessSnapshotId).Where(id => id != 0).Distinct().ToList();
        var variableSnapshotIds = releasesToDelete.Select(r => r.ProjectVariableSetSnapshotId).Where(id => id != 0).Distinct().ToList();

        await DeleteReleasesWithPackagesAsync(releaseIds, cancellationToken).ConfigureAwait(false);

        await DeleteOrphanedSnapshotsAsync(processSnapshotIds, variableSnapshotIds, cancellationToken).ConfigureAwait(false);
    }

    // Count-based pruning keeps the newest N releases per channel and cascade-deletes the rest —
    // including each pruned release's deployments + task data + completions + packages, then
    // ref-count-GCs its snapshots. Currently-deployed releases and releases with an in-progress
    // deployment are always preserved, never counting against (or consuming) the keep window.
    private async Task PruneReleasesByCountAsync(int projectId, int keepCount, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        if (keepCount <= 0) return;

        var releases = await repository.QueryNoTracking<Persistence.Entities.Deployments.Release>(r => r.ProjectId == projectId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var preservedReleaseIds = await GetPreservedReleaseIdsAsync(projectId, currentlyDeployedReleaseIds, cancellationToken).ConfigureAwait(false);

        var releasesToDelete = GetReleasesExceedingCount(releases, keepCount, preservedReleaseIds);

        if (releasesToDelete.Count == 0) return;

        Log.Information("Retention: deleting {Count} releases (keep newest {KeepCount} per channel) for project {ProjectId}", releasesToDelete.Count, keepCount, projectId);

        await CascadeDeleteReleasesAsync(projectId, releasesToDelete, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HashSet<int>> GetPreservedReleaseIdsAsync(int projectId, HashSet<int> currentlyDeployedReleaseIds, CancellationToken cancellationToken)
    {
        var activeReleaseIds = await GetReleaseIdsWithActiveDeploymentAsync(projectId, cancellationToken).ConfigureAwait(false);

        var preserved = new HashSet<int>(currentlyDeployedReleaseIds);
        preserved.UnionWith(activeReleaseIds);

        return preserved;
    }

    // A release with a deployment whose task is still non-terminal (queued/executing/pausing) is
    // mid-flight — pruning it would yank the release out from under a running deployment. Preserve.
    private async Task<HashSet<int>> GetReleaseIdsWithActiveDeploymentAsync(int projectId, CancellationToken cancellationToken)
    {
        var activeTaskIds = repository.QueryNoTracking<Persistence.Entities.Deployments.ServerTask>(t => NonTerminalTaskStates.Contains(t.State))
            .Select(t => t.Id);

        var releaseIds = await repository.QueryNoTracking<Deployment>(d => d.ProjectId == projectId && d.TaskId != null && activeTaskIds.Contains(d.TaskId.Value))
            .Select(d => d.ReleaseId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);

        return releaseIds.ToHashSet();
    }

    private async Task CascadeDeleteReleasesAsync(int projectId, List<Persistence.Entities.Deployments.Release> releasesToDelete, CancellationToken cancellationToken)
    {
        var releaseIds = releasesToDelete.Select(r => r.Id).ToList();

        // Snapshot the deployments at read time and delete them by their captured Ids only. A
        // deployment created concurrently (new Id) is never in this list, so an in-flight deploy of
        // a beyond-window release is never killed — the guarded release delete below then skips it.
        var deployments = await repository.QueryNoTracking<Deployment>(d => d.ProjectId == projectId && releaseIds.Contains(d.ReleaseId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await DeleteDeploymentsCascadeAsync(deployments, cancellationToken).ConfigureAwait(false);

        await DeleteReleasesWithPackagesAsync(releaseIds, cancellationToken).ConfigureAwait(false);

        var processSnapshotIds = CollectProcessSnapshotIds(releasesToDelete, deployments);
        var variableSnapshotIds = CollectVariableSnapshotIds(releasesToDelete, deployments);

        await DeleteOrphanedSnapshotsAsync(processSnapshotIds, variableSnapshotIds, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteDeploymentsCascadeAsync(List<Deployment> deployments, CancellationToken cancellationToken)
    {
        if (deployments.Count == 0) return;

        var ids = deployments.Select(d => d.Id).ToList();
        var taskIds = deployments.Where(d => d.TaskId.HasValue).Select(d => d.TaskId!.Value).Distinct().ToList();

        // Delete the deployment LAST (it is the retention anchor): if a child delete crashes
        // mid-way, the surviving deployment is re-selected next run and the cleanup retries
        // idempotently, so nothing is left permanently orphaned.
        await DeleteServerTaskDataAsync(taskIds, cancellationToken).ConfigureAwait(false);
        await deploymentCompletionDataProvider.DeleteByDeploymentIdsAsync(ids, cancellationToken).ConfigureAwait(false);
        await deploymentDataProvider.DeleteDeploymentsAsync(ids, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteReleasesWithPackagesAsync(List<int> releaseIds, CancellationToken cancellationToken)
    {
        // Atomic anti-race guard: re-check at DELETE time that no deployment references the
        // release. A deployment created between the in-memory read above and these deletes
        // (e.g. someone deploys a beyond-window release just as retention runs) must protect its
        // release. The subquery re-evaluates per DELETE, so such a release is excluded from BOTH
        // deletes — never leaving a release without its packages, nor a deployment whose release
        // was pruned. (Its snapshots are likewise kept: the surviving release and the new
        // deployment both still reference them, so DeleteOrphanedSnapshotsAsync skips them.)
        var releaseIdsReferencedByDeployment = repository.QueryNoTracking<Deployment>().Select(d => d.ReleaseId);

        // Children before parent: delete package selections, then the release (the anchor) last,
        // so a mid-sequence crash leaves the release for the next run to retry idempotently.
        await repository.ExecuteDeleteAsync<ReleaseSelectedPackage>(p => releaseIds.Contains(p.ReleaseId) && !releaseIdsReferencedByDeployment.Contains(p.ReleaseId), cancellationToken).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<Persistence.Entities.Deployments.Release>(r => releaseIds.Contains(r.Id) && !releaseIdsReferencedByDeployment.Contains(r.Id), cancellationToken).ConfigureAwait(false);
    }

    private static List<int> CollectProcessSnapshotIds(List<Persistence.Entities.Deployments.Release> releases, List<Deployment> deployments)
    {
        var fromReleases = releases.Select(r => r.ProjectDeploymentProcessSnapshotId);
        var fromDeployments = deployments.Where(d => d.ProcessSnapshotId.HasValue).Select(d => d.ProcessSnapshotId!.Value);

        return fromReleases.Concat(fromDeployments).Where(id => id != 0).Distinct().ToList();
    }

    private static List<int> CollectVariableSnapshotIds(List<Persistence.Entities.Deployments.Release> releases, List<Deployment> deployments)
    {
        var fromReleases = releases.Select(r => r.ProjectVariableSetSnapshotId);
        var fromDeployments = deployments.Where(d => d.VariableSetSnapshotId.HasValue).Select(d => d.VariableSetSnapshotId!.Value);

        return fromReleases.Concat(fromDeployments).Where(id => id != 0).Distinct().ToList();
    }

    public static List<Persistence.Entities.Deployments.Release> GetReleasesExceedingRetention(List<Persistence.Entities.Deployments.Release> releases, DateTimeOffset cutoff, HashSet<int> currentlyDeployedReleaseIds, HashSet<int> releaseIdsWithDeployments)
    {
        var result = new List<Persistence.Entities.Deployments.Release>();

        foreach (var release in releases)
        {
            if (currentlyDeployedReleaseIds.Contains(release.Id)) continue;

            if (releaseIdsWithDeployments.Contains(release.Id)) continue;

            if (release.CreatedDate >= cutoff) continue;

            result.Add(release);
        }

        return result;
    }

    // Keep the newest keepCount releases per channel; everything older is eligible unless preserved
    // (currently deployed or mid-flight). Ordering is CreatedDate desc, then Id desc as a stable
    // tiebreaker so two releases created in the same instant rank deterministically.
    public static List<Persistence.Entities.Deployments.Release> GetReleasesExceedingCount(List<Persistence.Entities.Deployments.Release> releases, int keepCount, HashSet<int> preservedReleaseIds)
    {
        var result = new List<Persistence.Entities.Deployments.Release>();

        foreach (var channelGroup in releases.GroupBy(r => r.ChannelId))
        {
            var ordered = channelGroup.OrderByDescending(r => r.CreatedDate).ThenByDescending(r => r.Id).ToList();

            foreach (var release in ordered.Skip(keepCount))
            {
                if (preservedReleaseIds.Contains(release.Id)) continue;

                result.Add(release);
            }
        }

        return result;
    }

    private async Task DeleteOrphanedSnapshotsAsync(List<int> processSnapshotIds, List<int> variableSnapshotIds, CancellationToken cancellationToken)
    {
        if (processSnapshotIds.Count > 0)
        {
            var referencedByRelease = await repository.QueryNoTracking<Persistence.Entities.Deployments.Release>(r => processSnapshotIds.Contains(r.ProjectDeploymentProcessSnapshotId))
                .Select(r => r.ProjectDeploymentProcessSnapshotId).ToListAsync(cancellationToken).ConfigureAwait(false);

            var referencedByDeployment = await repository.QueryNoTracking<Deployment>(d => d.ProcessSnapshotId != null && processSnapshotIds.Contains(d.ProcessSnapshotId.Value))
                .Select(d => d.ProcessSnapshotId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);

            var stillReferenced = referencedByRelease.Concat(referencedByDeployment).ToHashSet();
            var orphaned = processSnapshotIds.Where(id => !stillReferenced.Contains(id)).ToList();

            if (orphaned.Count > 0)
                await repository.ExecuteDeleteAsync<DeploymentProcessSnapshot>(s => orphaned.Contains(s.Id), cancellationToken).ConfigureAwait(false);
        }

        if (variableSnapshotIds.Count > 0)
        {
            var referencedByRelease = await repository.QueryNoTracking<Persistence.Entities.Deployments.Release>(r => variableSnapshotIds.Contains(r.ProjectVariableSetSnapshotId))
                .Select(r => r.ProjectVariableSetSnapshotId).ToListAsync(cancellationToken).ConfigureAwait(false);

            var referencedByDeployment = await repository.QueryNoTracking<Deployment>(d => d.VariableSetSnapshotId != null && variableSnapshotIds.Contains(d.VariableSetSnapshotId.Value))
                .Select(d => d.VariableSetSnapshotId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false);

            var stillReferenced = referencedByRelease.Concat(referencedByDeployment).ToHashSet();
            var orphaned = variableSnapshotIds.Where(id => !stillReferenced.Contains(id)).ToList();

            if (orphaned.Count > 0)
                await repository.ExecuteDeleteAsync<VariableSetSnapshot>(s => orphaned.Contains(s.Id), cancellationToken).ConfigureAwait(false);
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
                
                if (deployment.CreatedDate >= cutoff) continue;

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

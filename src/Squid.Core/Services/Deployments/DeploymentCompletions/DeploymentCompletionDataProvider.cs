using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Rollback;
using Squid.Core.Services.Deployments.ServerTask;
// 'Release' alone binds to the sibling Services.Deployments.Release namespace
// from this file's scope, so alias the entity explicitly for the journal join.
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;

namespace Squid.Core.Services.Deployments.DeploymentCompletions;

public interface IDeploymentCompletionDataProvider : IScopedDependency
{
    Task AddDeploymentCompletionAsync(DeploymentCompletion completion, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<DeploymentCompletion>> GetDeploymentCompletionsByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken = default);

    Task<List<DeploymentCompletion>> GetLatestSuccessfulCompletionsAsync(int? projectId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// PR-12 — successful deployments of releases to a single environment,
    /// newest-first, as the rollback journal. Joins the completion journal to
    /// <see cref="Deployment"/> (for project/environment/release) and
    /// <see cref="Release"/> (for the version string). Ordered by
    /// <see cref="DeploymentCompletion.CompletedTime"/> descending so
    /// <see cref="RollbackTargetSelector"/> can pick the prior distinct release.
    /// </summary>
    Task<List<RollbackReleaseHistoryEntry>> GetSuccessfulReleaseHistoryAsync(int projectId, int environmentId, CancellationToken cancellationToken = default);

    Task DeleteByDeploymentIdsAsync(List<int> deploymentIds, CancellationToken cancellationToken = default);
}

public class DeploymentCompletionDataProvider(IRepository repository, IUnitOfWork unitOfWork) : IDeploymentCompletionDataProvider
{
    public async Task AddDeploymentCompletionAsync(DeploymentCompletion completion, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(completion, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentCompletion>> GetDeploymentCompletionsByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken = default)
    {
        return await repository.QueryNoTracking<DeploymentCompletion>(dc => dc.DeploymentId == deploymentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentCompletion>> GetLatestSuccessfulCompletionsAsync(int? projectId = null, CancellationToken cancellationToken = default)
    {
        var query = repository.QueryNoTracking<DeploymentCompletion>(dc => dc.State == TaskState.Success);

        if (projectId.HasValue)
        {
            // 需要通过Deployment表关联来过滤ProjectId
            query = from completion in query
                    join deployment in repository.QueryNoTracking<Deployment>()
                        on completion.DeploymentId equals deployment.Id
                    where deployment.ProjectId == projectId.Value
                    select completion;
        }

        // 获取每个环境的最新成功部署
        var latestCompletions = await query
            .GroupBy(dc => new { dc.DeploymentId })
            .Select(g => g.OrderByDescending(dc => dc.CompletedTime).First())
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return latestCompletions;
    }

    public async Task<List<RollbackReleaseHistoryEntry>> GetSuccessfulReleaseHistoryAsync(int projectId, int environmentId, CancellationToken cancellationToken = default)
    {
        var query = from completion in repository.QueryNoTracking<DeploymentCompletion>(dc => dc.State == TaskState.Success)
                    join deployment in repository.QueryNoTracking<Deployment>()
                        on completion.DeploymentId equals deployment.Id
                    join release in repository.QueryNoTracking<ReleaseEntity>()
                        on deployment.ReleaseId equals release.Id
                    where deployment.ProjectId == projectId && deployment.EnvironmentId == environmentId
                    orderby completion.CompletedTime descending
                    select new RollbackReleaseHistoryEntry(release.Id, release.Version, deployment.Id, completion.CompletedTime);

        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteByDeploymentIdsAsync(List<int> deploymentIds, CancellationToken cancellationToken = default)
    {
        if (deploymentIds == null || deploymentIds.Count == 0) return;

        var completions = await repository.Query<DeploymentCompletion>(dc => deploymentIds.Contains(dc.DeploymentId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await repository.DeleteAllAsync(completions, cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

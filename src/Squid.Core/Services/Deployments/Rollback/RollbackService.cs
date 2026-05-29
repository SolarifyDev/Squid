using Squid.Core.Persistence.Db;
using Squid.Core.Services.Deployments.DeploymentCompletions;

namespace Squid.Core.Services.Deployments.Rollback;

/// <summary>
/// PR-12 — resolves the rollback target for an environment: the release that
/// was successfully running before the current one. Owns the data access (via
/// <see cref="IDeploymentCompletionDataProvider"/>) and delegates the
/// selection rule to <see cref="RollbackTargetSelector"/>. Re-deploying that
/// release (PR-13) is the rollback action.
/// </summary>
public interface IRollbackService : IScopedDependency
{
    /// <summary>
    /// The release to roll back to for <paramref name="projectId"/> in
    /// <paramref name="environmentId"/>, or <see langword="null"/> when there
    /// is no prior distinct release (never deployed, or only one release ever
    /// ran there).
    /// </summary>
    Task<RollbackReleaseHistoryEntry?> GetRollbackTargetAsync(int projectId, int environmentId, CancellationToken cancellationToken = default);
}

public class RollbackService(IDeploymentCompletionDataProvider deploymentCompletionDataProvider) : IRollbackService
{
    public async Task<RollbackReleaseHistoryEntry?> GetRollbackTargetAsync(int projectId, int environmentId, CancellationToken cancellationToken = default)
    {
        var history = await deploymentCompletionDataProvider
            .GetSuccessfulReleaseHistoryAsync(projectId, environmentId, cancellationToken).ConfigureAwait(false);

        return RollbackTargetSelector.SelectPreviousDistinctRelease(history);
    }
}

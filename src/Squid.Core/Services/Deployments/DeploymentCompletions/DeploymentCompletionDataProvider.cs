using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.DeploymentCompletions;

public interface IDeploymentCompletionDataProvider : IScopedDependency
{
    Task AddDeploymentCompletionAsync(DeploymentCompletion completion, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<DeploymentCompletion>> GetDeploymentCompletionsByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken = default);

    Task<List<DeploymentCompletion>> GetLatestSuccessfulCompletionsAsync(int? projectId = null, CancellationToken cancellationToken = default);
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
        var query = repository.QueryNoTracking<DeploymentCompletion>(dc => dc.State == "Success");

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
}

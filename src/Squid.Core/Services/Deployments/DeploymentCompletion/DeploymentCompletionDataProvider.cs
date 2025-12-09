namespace Squid.Core.Services.Deployments.DeploymentCompletion;

public interface IDeploymentCompletionDataProvider : IScopedDependency
{
    Task AddDeploymentCompletionAsync(Message.Domain.Deployments.DeploymentCompletion completion, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.DeploymentCompletion>> GetDeploymentCompletionsByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken = default);

    Task<List<Message.Domain.Deployments.DeploymentCompletion>> GetLatestSuccessfulCompletionsAsync(int? projectId = null, CancellationToken cancellationToken = default);
}

public class DeploymentCompletionDataProvider : IDeploymentCompletionDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public DeploymentCompletionDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddDeploymentCompletionAsync(Message.Domain.Deployments.DeploymentCompletion completion, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(completion, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<List<Message.Domain.Deployments.DeploymentCompletion>> GetDeploymentCompletionsByDeploymentIdAsync(int deploymentId, CancellationToken cancellationToken = default)
    {
        return await _repository.QueryNoTracking<Message.Domain.Deployments.DeploymentCompletion>(dc => dc.DeploymentId == deploymentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<Message.Domain.Deployments.DeploymentCompletion>> GetLatestSuccessfulCompletionsAsync(int? projectId = null, CancellationToken cancellationToken = default)
    {
        var query = _repository.QueryNoTracking<Message.Domain.Deployments.DeploymentCompletion>(dc => dc.State == "Success");

        if (projectId.HasValue)
        {
            // 需要通过Deployment表关联来过滤ProjectId
            query = from completion in query
                    join deployment in _repository.QueryNoTracking<Message.Domain.Deployments.Deployment>()
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

using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Checkpoints;

public interface IDeploymentCheckpointService : IScopedDependency
{
    Task SaveAsync(DeploymentExecutionCheckpoint checkpoint, CancellationToken ct = default);

    Task<DeploymentExecutionCheckpoint> LoadAsync(int serverTaskId, CancellationToken ct = default);

    Task DeleteAsync(int serverTaskId, CancellationToken ct = default);
}

public class DeploymentCheckpointService(IRepository repository, IUnitOfWork unitOfWork) : IDeploymentCheckpointService
{
    public async Task SaveAsync(DeploymentExecutionCheckpoint checkpoint, CancellationToken ct = default)
    {
        var rowsAffected = await repository.ExecuteUpdateAsync<DeploymentExecutionCheckpoint>(
            c => c.ServerTaskId == checkpoint.ServerTaskId,
            s => s.SetProperty(c => c.LastCompletedBatchIndex, checkpoint.LastCompletedBatchIndex)
                  .SetProperty(c => c.FailureEncountered, checkpoint.FailureEncountered)
                  .SetProperty(c => c.OutputVariablesJson, checkpoint.OutputVariablesJson)
                  .SetProperty(c => c.CreatedAt, checkpoint.CreatedAt),
            ct).ConfigureAwait(false);

        if (rowsAffected > 0) return;

        await repository.InsertAsync(checkpoint, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<DeploymentExecutionCheckpoint> LoadAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await repository.QueryNoTracking<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int serverTaskId, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId, ct).ConfigureAwait(false);
    }
}

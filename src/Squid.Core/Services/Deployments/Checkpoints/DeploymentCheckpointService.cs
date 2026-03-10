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
        var existing = await repository.FirstOrDefaultAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == checkpoint.ServerTaskId, ct).ConfigureAwait(false);

        if (existing != null)
        {
            existing.LastCompletedBatchIndex = checkpoint.LastCompletedBatchIndex;
            existing.FailureEncountered = checkpoint.FailureEncountered;
            existing.OutputVariablesJson = checkpoint.OutputVariablesJson;
            existing.CreatedAt = checkpoint.CreatedAt;

            await repository.UpdateAsync(existing, ct).ConfigureAwait(false);
        }
        else
        {
            await repository.InsertAsync(checkpoint, ct).ConfigureAwait(false);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<DeploymentExecutionCheckpoint> LoadAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await repository.FirstOrDefaultAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int serverTaskId, CancellationToken ct = default)
    {
        var existing = await repository.FirstOrDefaultAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == serverTaskId, ct).ConfigureAwait(false);

        if (existing == null) return;

        await repository.DeleteAsync(existing, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

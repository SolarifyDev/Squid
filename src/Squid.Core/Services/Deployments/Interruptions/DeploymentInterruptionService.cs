using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.Deployments;

namespace Squid.Core.Services.Deployments.Interruptions;

public interface IDeploymentInterruptionService : IScopedDependency
{
    Task<DeploymentInterruption> CreateInterruptionAsync(int serverTaskId, int deploymentId, int stepDisplayOrder, string stepName, string actionName, string machineName, string errorMessage, int spaceId, CancellationToken ct = default);

    Task ResolveInterruptionAsync(int interruptionId, GuidedFailureAction action, CancellationToken ct = default);

    Task<GuidedFailureAction> WaitForResolutionAsync(int interruptionId, CancellationToken ct);

    Task<DeploymentInterruption> GetInterruptionByIdAsync(int interruptionId, CancellationToken ct = default);

    Task<List<DeploymentInterruption>> GetPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default);
}

public class DeploymentInterruptionService(IRepository repository, IUnitOfWork unitOfWork) : IDeploymentInterruptionService
{
    private const int PollIntervalMs = 5000;

    public async Task<DeploymentInterruption> CreateInterruptionAsync(int serverTaskId, int deploymentId, int stepDisplayOrder, string stepName, string actionName, string machineName, string errorMessage, int spaceId, CancellationToken ct = default)
    {
        var interruption = new DeploymentInterruption
        {
            ServerTaskId = serverTaskId,
            DeploymentId = deploymentId,
            StepDisplayOrder = stepDisplayOrder,
            StepName = stepName,
            ActionName = actionName,
            MachineName = machineName,
            ErrorMessage = errorMessage,
            CreatedAt = DateTimeOffset.UtcNow,
            SpaceId = spaceId
        };

        await repository.InsertAsync(interruption, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        return interruption;
    }

    public async Task ResolveInterruptionAsync(int interruptionId, GuidedFailureAction action, CancellationToken ct = default)
    {
        var interruption = await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);

        if (interruption == null)
            throw new InvalidOperationException($"DeploymentInterruption {interruptionId} not found");

        interruption.Resolution = action.ToString();
        interruption.ResolvedAt = DateTimeOffset.UtcNow;

        await repository.UpdateAsync(interruption, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<GuidedFailureAction> WaitForResolutionAsync(int interruptionId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interruption = await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);

            if (interruption?.Resolution != null && Enum.TryParse<GuidedFailureAction>(interruption.Resolution, true, out var action))
                return action;

            await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return GuidedFailureAction.Abort;
    }

    public async Task<DeploymentInterruption> GetInterruptionByIdAsync(int interruptionId, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<DeploymentInterruption>(interruptionId, ct).ConfigureAwait(false);
    }

    public async Task<List<DeploymentInterruption>> GetPendingInterruptionsAsync(int serverTaskId, CancellationToken ct = default)
    {
        return await repository.ToListAsync<DeploymentInterruption>(i => i.ServerTaskId == serverTaskId && i.Resolution == null, ct).ConfigureAwait(false);
    }
}

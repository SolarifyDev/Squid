using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Process.Action;

public interface IDeploymentActionDataProvider : IScopedDependency
{
    Task AddDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default);

    Task AddDeploymentActionsAsync(List<DeploymentAction> actions, bool forceSave = true, CancellationToken cancellationToken = default);

    Task UpdateDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<DeploymentAction> GetDeploymentActionByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<List<DeploymentAction>> GetDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default);

    Task<List<DeploymentAction>> GetDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default);

    Task DeleteDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default);

    Task DeleteDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default);
}

public class DeploymentActionDataProvider(
    IRepository repository,
    IUnitOfWork unitOfWork,
    IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
    IActionEnvironmentDataProvider actionEnvironmentDataProvider,
    IActionChannelDataProvider actionChannelDataProvider,
    IActionMachineRoleDataProvider actionMachineRoleDataProvider) : IDeploymentActionDataProvider
{
    public async Task AddDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddDeploymentActionsAsync(List<DeploymentAction> actions, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAllAsync(actions, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.UpdateAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await actionChannelDataProvider.DeleteActionChannelsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        
        await repository.DeleteAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DeploymentAction> GetDeploymentActionByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await repository.Query<DeploymentAction>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAction>> GetDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        return await repository.Query<DeploymentAction>()
            .Where(a => a.StepId == stepId)
            .OrderBy(a => a.ActionOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAction>> GetDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        return await repository.Query<DeploymentAction>()
            .Where(a => stepIds.Contains(a.StepId))
            .OrderBy(a => a.StepId)
            .ThenBy(a => a.ActionOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        var actions = await GetDeploymentActionsByStepIdAsync(stepId, cancellationToken).ConfigureAwait(false);

        var actionIds = actions.Select(a => a.Id).ToList();

        await actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionChannelDataProvider.DeleteActionChannelsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await repository.DeleteAllAsync(actions, cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        var actions = await repository.Query<DeploymentAction>()
            .Where(a => stepIds.Contains(a.StepId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var actionIds = actions.Select(a => a.Id).ToList();

        await actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionChannelDataProvider.DeleteActionChannelsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await repository.DeleteAllAsync(actions, cancellationToken).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

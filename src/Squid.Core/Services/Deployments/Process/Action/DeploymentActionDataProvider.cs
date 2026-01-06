using Squid.Core.Persistence.Data.Domain.Deployments;

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

public class DeploymentActionDataProvider : IDeploymentActionDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IDeploymentActionPropertyDataProvider _actionPropertyDataProvider;
    private readonly IActionEnvironmentDataProvider _actionEnvironmentDataProvider;
    private readonly IActionChannelDataProvider _actionChannelDataProvider;
    private readonly IActionMachineRoleDataProvider _actionMachineRoleDataProvider;

    public DeploymentActionDataProvider(IRepository repository, IUnitOfWork unitOfWork,
        IDeploymentActionPropertyDataProvider actionPropertyDataProvider,
        IActionEnvironmentDataProvider actionEnvironmentDataProvider,
        IActionChannelDataProvider actionChannelDataProvider,
        IActionMachineRoleDataProvider actionMachineRoleDataProvider)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
        _actionPropertyDataProvider = actionPropertyDataProvider;
        _actionEnvironmentDataProvider = actionEnvironmentDataProvider;
        _actionChannelDataProvider = actionChannelDataProvider;
        _actionMachineRoleDataProvider = actionMachineRoleDataProvider;
    }

    public async Task AddDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task AddDeploymentActionsAsync(List<DeploymentAction> actions, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(actions, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UpdateDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _repository.UpdateAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteDeploymentActionAsync(DeploymentAction action, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await _actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await _actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await _actionChannelDataProvider.DeleteActionChannelsByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        await _actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdAsync(action.Id, cancellationToken).ConfigureAwait(false);
        
        await _repository.DeleteAsync(action, cancellationToken).ConfigureAwait(false);

        if (forceSave)
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<DeploymentAction> GetDeploymentActionByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentAction>(x => x.Id == id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAction>> GetDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentAction>()
            .Where(a => a.StepId == stepId)
            .OrderBy(a => a.ActionOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<DeploymentAction>> GetDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<DeploymentAction>()
            .Where(a => stepIds.Contains(a.StepId))
            .OrderBy(a => a.StepId)
            .ThenBy(a => a.ActionOrder)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionsByStepIdAsync(int stepId, CancellationToken cancellationToken = default)
    {
        var actions = await GetDeploymentActionsByStepIdAsync(stepId, cancellationToken).ConfigureAwait(false);

        var actionIds = actions.Select(a => a.Id).ToList();

        await _actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionChannelDataProvider.DeleteActionChannelsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(actions, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteDeploymentActionsByStepIdsAsync(List<int> stepIds, CancellationToken cancellationToken = default)
    {
        var actions = await _repository.Query<DeploymentAction>()
            .Where(a => stepIds.Contains(a.StepId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var actionIds = actions.Select(a => a.Id).ToList();

        await _actionPropertyDataProvider.DeleteDeploymentActionPropertiesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionEnvironmentDataProvider.DeleteActionEnvironmentsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionChannelDataProvider.DeleteActionChannelsByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _actionMachineRoleDataProvider.DeleteActionMachineRolesByActionIdsAsync(actionIds, cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(actions, cancellationToken).ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

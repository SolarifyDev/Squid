namespace Squid.Core.Services.Deployments.Process;

public interface IActionMachineRoleDataProvider : IScopedDependency
{
    Task AddActionMachineRolesAsync(List<ActionMachineRole> machineRoles, CancellationToken cancellationToken = default);

    Task UpdateActionMachineRolesAsync(int actionId, List<ActionMachineRole> machineRoles, CancellationToken cancellationToken = default);

    Task DeleteActionMachineRolesByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task DeleteActionMachineRolesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);

    Task<List<ActionMachineRole>> GetActionMachineRolesByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task<List<ActionMachineRole>> GetActionMachineRolesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);
}

public class ActionMachineRoleDataProvider : IActionMachineRoleDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActionMachineRoleDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddActionMachineRolesAsync(List<ActionMachineRole> machineRoles, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(machineRoles, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateActionMachineRolesAsync(int actionId, List<ActionMachineRole> machineRoles, CancellationToken cancellationToken = default)
    {
        await DeleteActionMachineRolesByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddActionMachineRolesAsync(machineRoles, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionMachineRolesByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        var machineRoles = await _repository.Query<ActionMachineRole>()
            .Where(m => m.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(machineRoles, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionMachineRolesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        var machineRoles = await _repository.Query<ActionMachineRole>()
            .Where(m => actionIds.Contains(m.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(machineRoles, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionMachineRole>> GetActionMachineRolesByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionMachineRole>()
            .Where(m => m.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionMachineRole>> GetActionMachineRolesByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionMachineRole>()
            .Where(m => actionIds.Contains(m.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

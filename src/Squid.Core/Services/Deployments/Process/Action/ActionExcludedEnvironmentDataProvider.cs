using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Process.Action;

public interface IActionExcludedEnvironmentDataProvider : IScopedDependency
{
    Task AddActionExcludedEnvironmentsAsync(List<ActionExcludedEnvironment> environments, CancellationToken cancellationToken = default);

    Task UpdateActionExcludedEnvironmentsAsync(int actionId, List<ActionExcludedEnvironment> environments, CancellationToken cancellationToken = default);

    Task DeleteActionExcludedEnvironmentsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task DeleteActionExcludedEnvironmentsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);

    Task<List<ActionExcludedEnvironment>> GetActionExcludedEnvironmentsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task<List<ActionExcludedEnvironment>> GetActionExcludedEnvironmentsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);
}

public class ActionExcludedEnvironmentDataProvider : IActionExcludedEnvironmentDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActionExcludedEnvironmentDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddActionExcludedEnvironmentsAsync(List<ActionExcludedEnvironment> environments, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateActionExcludedEnvironmentsAsync(int actionId, List<ActionExcludedEnvironment> environments, CancellationToken cancellationToken = default)
    {
        await DeleteActionExcludedEnvironmentsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddActionExcludedEnvironmentsAsync(environments, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionExcludedEnvironmentsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        var environments = await _repository.Query<ActionExcludedEnvironment>()
            .Where(e => e.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionExcludedEnvironmentsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        var environments = await _repository.Query<ActionExcludedEnvironment>()
            .Where(e => actionIds.Contains(e.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionExcludedEnvironment>> GetActionExcludedEnvironmentsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionExcludedEnvironment>()
            .Where(e => e.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionExcludedEnvironment>> GetActionExcludedEnvironmentsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionExcludedEnvironment>()
            .Where(e => actionIds.Contains(e.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

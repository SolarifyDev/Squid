using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Process;

public interface IActionEnvironmentDataProvider : IScopedDependency
{
    Task AddActionEnvironmentsAsync(List<ActionEnvironment> environments, CancellationToken cancellationToken = default);

    Task UpdateActionEnvironmentsAsync(Guid actionId, List<ActionEnvironment> environments, CancellationToken cancellationToken = default);

    Task DeleteActionEnvironmentsByActionIdAsync(Guid actionId, CancellationToken cancellationToken = default);

    Task DeleteActionEnvironmentsByActionIdsAsync(List<Guid> actionIds, CancellationToken cancellationToken = default);

    Task<List<ActionEnvironment>> GetActionEnvironmentsByActionIdAsync(Guid actionId, CancellationToken cancellationToken = default);
}

public class ActionEnvironmentDataProvider : IActionEnvironmentDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActionEnvironmentDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddActionEnvironmentsAsync(List<ActionEnvironment> environments, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateActionEnvironmentsAsync(Guid actionId, List<ActionEnvironment> environments, CancellationToken cancellationToken = default)
    {
        await DeleteActionEnvironmentsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddActionEnvironmentsAsync(environments, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionEnvironmentsByActionIdAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        var environments = await _repository.Query<ActionEnvironment>()
            .Where(e => e.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionEnvironmentsByActionIdsAsync(List<Guid> actionIds, CancellationToken cancellationToken = default)
    {
        var environments = await _repository.Query<ActionEnvironment>()
            .Where(e => actionIds.Contains(e.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(environments, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionEnvironment>> GetActionEnvironmentsByActionIdAsync(Guid actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionEnvironment>()
            .Where(e => e.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

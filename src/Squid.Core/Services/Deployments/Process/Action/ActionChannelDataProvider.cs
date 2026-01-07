using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.Process.Action;

public interface IActionChannelDataProvider : IScopedDependency
{
    Task AddActionChannelsAsync(List<ActionChannel> channels, CancellationToken cancellationToken = default);

    Task UpdateActionChannelsAsync(int actionId, List<ActionChannel> channels, CancellationToken cancellationToken = default);

    Task DeleteActionChannelsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task DeleteActionChannelsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);

    Task<List<ActionChannel>> GetActionChannelsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task<List<ActionChannel>> GetActionChannelsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);
}

public class ActionChannelDataProvider : IActionChannelDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActionChannelDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddActionChannelsAsync(List<ActionChannel> channels, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(channels, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateActionChannelsAsync(int actionId, List<ActionChannel> channels, CancellationToken cancellationToken = default)
    {
        await DeleteActionChannelsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddActionChannelsAsync(channels, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionChannelsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        var channels = await _repository.Query<ActionChannel>()
            .Where(c => c.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(channels, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionChannelsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        var channels = await _repository.Query<ActionChannel>()
            .Where(c => actionIds.Contains(c.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(channels, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionChannel>> GetActionChannelsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionChannel>()
            .Where(c => c.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionChannel>> GetActionChannelsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionChannel>()
            .Where(c => actionIds.Contains(c.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

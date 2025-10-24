using Squid.Core.DependencyInjection;
using Squid.Core.Persistence;
using Squid.Message.Domain.Deployments;

namespace Squid.Core.Services.Deployments.Process;

public interface IActionTenantTagDataProvider : IScopedDependency
{
    Task AddActionTenantTagsAsync(List<ActionTenantTag> tenantTags, CancellationToken cancellationToken = default);

    Task UpdateActionTenantTagsAsync(int actionId, List<ActionTenantTag> tenantTags, CancellationToken cancellationToken = default);

    Task DeleteActionTenantTagsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);

    Task DeleteActionTenantTagsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default);

    Task<List<ActionTenantTag>> GetActionTenantTagsByActionIdAsync(int actionId, CancellationToken cancellationToken = default);
}

public class ActionTenantTagDataProvider : IActionTenantTagDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public ActionTenantTagDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddActionTenantTagsAsync(List<ActionTenantTag> tenantTags, CancellationToken cancellationToken = default)
    {
        await _repository.InsertAllAsync(tenantTags, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateActionTenantTagsAsync(int actionId, List<ActionTenantTag> tenantTags, CancellationToken cancellationToken = default)
    {
        await DeleteActionTenantTagsByActionIdAsync(actionId, cancellationToken).ConfigureAwait(false);
        await AddActionTenantTagsAsync(tenantTags, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionTenantTagsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        var tenantTags = await _repository.Query<ActionTenantTag>()
            .Where(t => t.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(tenantTags, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteActionTenantTagsByActionIdsAsync(List<int> actionIds, CancellationToken cancellationToken = default)
    {
        var tenantTags = await _repository.Query<ActionTenantTag>()
            .Where(t => actionIds.Contains(t.ActionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        await _repository.DeleteAllAsync(tenantTags, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<ActionTenantTag>> GetActionTenantTagsByActionIdAsync(int actionId, CancellationToken cancellationToken = default)
    {
        return await _repository.Query<ActionTenantTag>()
            .Where(t => t.ActionId == actionId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

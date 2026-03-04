using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.TargetTags;

public interface ITargetTagDataProvider : IScopedDependency
{
    Task AddAsync(TargetTag tag, bool forceSave = true, CancellationToken cancellationToken = default);

    Task DeleteAsync(List<TargetTag> tags, bool forceSave = true, CancellationToken cancellationToken = default);

    Task<List<TargetTag>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<List<TargetTag>> GetByIdsAsync(List<int> ids, CancellationToken cancellationToken = default);

    Task<TargetTag> GetByNameAsync(string name, CancellationToken cancellationToken = default);
}

public class TargetTagDataProvider(IUnitOfWork unitOfWork, IRepository repository) : ITargetTagDataProvider
{
    public async Task AddAsync(TargetTag tag, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.InsertAsync(tag, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(List<TargetTag> tags, bool forceSave = true, CancellationToken cancellationToken = default)
    {
        await repository.DeleteAllAsync(tags, cancellationToken).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<List<TargetTag>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await repository.Query<TargetTag>()
            .OrderBy(t => t.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<List<TargetTag>> GetByIdsAsync(List<int> ids, CancellationToken cancellationToken = default)
    {
        return await repository.Query<TargetTag>(t => ids.Contains(t.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TargetTag> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        return await repository.Query<TargetTag>(t => t.Name == name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}

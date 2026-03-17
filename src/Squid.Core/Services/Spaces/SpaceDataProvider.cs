using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Spaces;

public interface ISpaceDataProvider : IScopedDependency
{
    Task<Space> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<Space>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Space space, bool forceSave = true, CancellationToken ct = default);
    Task UpdateAsync(Space space, bool forceSave = true, CancellationToken ct = default);
    Task DeleteAsync(Space space, bool forceSave = true, CancellationToken ct = default);
}

public class SpaceDataProvider(IUnitOfWork unitOfWork, IRepository repository) : ISpaceDataProvider
{
    public async Task<Space> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<Space>(id, ct).ConfigureAwait(false);
    }

    public async Task<List<Space>> GetAllAsync(CancellationToken ct = default)
    {
        return await repository.Query<Space>().ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(Space space, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(space, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Space space, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.UpdateAsync(space, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Space space, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.DeleteAsync(space, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

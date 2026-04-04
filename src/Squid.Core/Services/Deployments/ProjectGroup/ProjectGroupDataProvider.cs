using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.ProjectGroup;

public interface IProjectGroupDataProvider : IScopedDependency
{
    Task<Persistence.Entities.Deployments.ProjectGroup> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Persistence.Entities.Deployments.ProjectGroup> GetDefaultAsync(CancellationToken ct = default);

    Task AddAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default);

    Task UpdateAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default);

    Task DeleteAsync(List<Persistence.Entities.Deployments.ProjectGroup> projectGroups, bool forceSave = true, CancellationToken ct = default);

    Task<List<Persistence.Entities.Deployments.ProjectGroup>> GetProjectGroupsAsync(List<int> ids, CancellationToken ct = default);

    Task<(int count, List<Persistence.Entities.Deployments.ProjectGroup>)> GetProjectGroupPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken ct = default);
}

public class ProjectGroupDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IProjectGroupDataProvider
{
    public async Task<Persistence.Entities.Deployments.ProjectGroup> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<Persistence.Entities.Deployments.ProjectGroup>(id, ct).ConfigureAwait(false);
    }

    public async Task<Persistence.Entities.Deployments.ProjectGroup> GetDefaultAsync(CancellationToken ct = default)
    {
        return await repository.Query<Persistence.Entities.Deployments.ProjectGroup>()
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(projectGroup, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.UpdateAsync(projectGroup, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(List<Persistence.Entities.Deployments.ProjectGroup> projectGroups, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.DeleteAllAsync(projectGroups, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Persistence.Entities.Deployments.ProjectGroup>> GetProjectGroupsAsync(List<int> ids, CancellationToken ct = default)
    {
        return await repository.Query<Persistence.Entities.Deployments.ProjectGroup>()
            .Where(pg => ids.Contains(pg.Id))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int count, List<Persistence.Entities.Deployments.ProjectGroup>)> GetProjectGroupPagingAsync(int? spaceId = null, int? pageIndex = null, int? pageSize = null, CancellationToken ct = default)
    {
        var query = repository.Query<Persistence.Entities.Deployments.ProjectGroup>();

        if (spaceId.HasValue)
            query = query.Where(pg => pg.SpaceId == spaceId.Value);

        var count = await query.CountAsync(ct).ConfigureAwait(false);

        if (pageIndex.HasValue && pageSize.HasValue)
            query = query.Skip((pageIndex.Value - 1) * pageSize.Value).Take(pageSize.Value);

        return (count, await query.ToListAsync(ct).ConfigureAwait(false));
    }
}

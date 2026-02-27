using Squid.Core.Persistence.Db;

namespace Squid.Core.Services.Deployments.ProjectGroup;

public interface IProjectGroupDataProvider : IScopedDependency
{
    Task<Persistence.Entities.Deployments.ProjectGroup> GetByIdAsync(int id, CancellationToken ct = default);

    Task<Persistence.Entities.Deployments.ProjectGroup> GetDefaultAsync(CancellationToken ct = default);

    Task AddAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default);

    Task UpdateAsync(Persistence.Entities.Deployments.ProjectGroup projectGroup, bool forceSave = true, CancellationToken ct = default);
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
}

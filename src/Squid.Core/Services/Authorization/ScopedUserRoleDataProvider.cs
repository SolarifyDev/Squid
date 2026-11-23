using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Services.Authorization;

public interface IScopedUserRoleDataProvider : IScopedDependency
{
    Task<List<ScopedUserRole>> GetByTeamIdsAsync(List<int> teamIds, CancellationToken ct = default);
    Task<ScopedUserRole> GetByTeamAndIdAsync(int teamId, int scopedUserRoleId, CancellationToken ct = default);
    Task<List<int>> GetProjectScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task<List<int>> GetEnvironmentScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task<List<int>> GetProjectGroupScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task AddAsync(ScopedUserRole scopedRole, bool forceSave = true, CancellationToken ct = default);
    Task DeleteAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task SetProjectScopeAsync(int scopedUserRoleId, List<int> projectIds, CancellationToken ct = default);
    Task SetEnvironmentScopeAsync(int scopedUserRoleId, List<int> environmentIds, CancellationToken ct = default);
    Task SetProjectGroupScopeAsync(int scopedUserRoleId, List<int> projectGroupIds, CancellationToken ct = default);
    Task ReplaceScopesAsync(
        int scopedUserRoleId,
        IReadOnlyCollection<int> projectIds,
        IReadOnlyCollection<int> environmentIds,
        IReadOnlyCollection<int> projectGroupIds,
        CancellationToken ct = default);
}

public class ScopedUserRoleDataProvider(IUnitOfWork unitOfWork, IRepository repository) : IScopedUserRoleDataProvider
{
    public async Task<List<ScopedUserRole>> GetByTeamIdsAsync(List<int> teamIds, CancellationToken ct = default)
    {
        return await repository.Query<ScopedUserRole>(x => teamIds.Contains(x.TeamId)).ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<ScopedUserRole> GetByTeamAndIdAsync(int teamId, int scopedUserRoleId, CancellationToken ct = default)
    {
        return repository.Query<ScopedUserRole>(x => x.TeamId == teamId && x.Id == scopedUserRoleId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<int>> GetProjectScopeAsync(int scopedUserRoleId, CancellationToken ct = default)
    {
        return await repository.Query<ScopedUserRoleProject>(x => x.ScopedUserRoleId == scopedUserRoleId)
            .Select(x => x.ProjectId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetEnvironmentScopeAsync(int scopedUserRoleId, CancellationToken ct = default)
    {
        return await repository.Query<ScopedUserRoleEnvironment>(x => x.ScopedUserRoleId == scopedUserRoleId)
            .Select(x => x.EnvironmentId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetProjectGroupScopeAsync(int scopedUserRoleId, CancellationToken ct = default)
    {
        return await repository.Query<ScopedUserRoleProjectGroup>(x => x.ScopedUserRoleId == scopedUserRoleId)
            .Select(x => x.ProjectGroupId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(ScopedUserRole scopedRole, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(scopedRole, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(int scopedUserRoleId, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRole>(x => x.Id == scopedUserRoleId, ct).ConfigureAwait(false);
    }

    public async Task SetProjectScopeAsync(int scopedUserRoleId, List<int> projectIds, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRoleProject>(x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);

        var entities = projectIds.Select(id => new ScopedUserRoleProject { ScopedUserRoleId = scopedUserRoleId, ProjectId = id }).ToList();

        if (entities.Count > 0)
            await repository.InsertAllAsync(entities, ct).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetEnvironmentScopeAsync(int scopedUserRoleId, List<int> environmentIds, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);

        var entities = environmentIds.Select(id => new ScopedUserRoleEnvironment { ScopedUserRoleId = scopedUserRoleId, EnvironmentId = id }).ToList();

        if (entities.Count > 0)
            await repository.InsertAllAsync(entities, ct).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task SetProjectGroupScopeAsync(int scopedUserRoleId, List<int> projectGroupIds, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);

        var entities = projectGroupIds.Select(id => new ScopedUserRoleProjectGroup { ScopedUserRoleId = scopedUserRoleId, ProjectGroupId = id }).ToList();

        if (entities.Count > 0)
            await repository.InsertAllAsync(entities, ct).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task ReplaceScopesAsync(
        int scopedUserRoleId,
        IReadOnlyCollection<int> projectIds,
        IReadOnlyCollection<int> environmentIds,
        IReadOnlyCollection<int> projectGroupIds,
        CancellationToken ct = default)
    {
        await using var transaction = repository.Database.CurrentTransaction == null
            ? await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        await repository.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM scoped_user_role WHERE id = {scopedUserRoleId} FOR UPDATE", ct).ConfigureAwait(false);

        await repository.ExecuteDeleteAsync<ScopedUserRoleProject>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);

        await InsertScopesAsync(scopedUserRoleId, projectIds, environmentIds, projectGroupIds, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        if (transaction != null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task InsertScopesAsync(
        int scopedUserRoleId,
        IReadOnlyCollection<int> projectIds,
        IReadOnlyCollection<int> environmentIds,
        IReadOnlyCollection<int> projectGroupIds,
        CancellationToken ct)
    {
        var projects = projectIds
            .Select(id => new ScopedUserRoleProject { ScopedUserRoleId = scopedUserRoleId, ProjectId = id })
            .ToList();
        var environments = environmentIds
            .Select(id => new ScopedUserRoleEnvironment { ScopedUserRoleId = scopedUserRoleId, EnvironmentId = id })
            .ToList();
        var projectGroups = projectGroupIds
            .Select(id => new ScopedUserRoleProjectGroup { ScopedUserRoleId = scopedUserRoleId, ProjectGroupId = id })
            .ToList();

        if (projects.Count > 0)
            await repository.InsertAllAsync(projects, ct).ConfigureAwait(false);
        if (environments.Count > 0)
            await repository.InsertAllAsync(environments, ct).ConfigureAwait(false);
        if (projectGroups.Count > 0)
            await repository.InsertAllAsync(projectGroups, ct).ConfigureAwait(false);
    }

}

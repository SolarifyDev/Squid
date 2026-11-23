using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Models.Authorization;

namespace Squid.Core.Services.Authorization;

public interface IScopedUserRoleDataProvider : IScopedDependency
{
    Task<List<ScopedUserRole>> GetByTeamIdsAsync(List<int> teamIds, CancellationToken ct = default);
    Task<List<int>> GetProjectScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task<List<int>> GetEnvironmentScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task<List<int>> GetProjectGroupScopeAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task AddAsync(ScopedUserRole scopedRole, bool forceSave = true, CancellationToken ct = default);
    Task DeleteAsync(int scopedUserRoleId, CancellationToken ct = default);
    Task SetProjectScopeAsync(int scopedUserRoleId, List<int> projectIds, CancellationToken ct = default);
    Task SetEnvironmentScopeAsync(int scopedUserRoleId, List<int> environmentIds, CancellationToken ct = default);
    Task SetProjectGroupScopeAsync(int scopedUserRoleId, List<int> projectGroupIds, CancellationToken ct = default);
    Task<ScopedUserRoleDto> ReplaceScopesAsync(
        int teamId,
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

    public async Task<ScopedUserRoleDto> ReplaceScopesAsync(
        int teamId,
        int scopedUserRoleId,
        IReadOnlyCollection<int> projectIds,
        IReadOnlyCollection<int> environmentIds,
        IReadOnlyCollection<int> projectGroupIds,
        CancellationToken ct = default)
    {
        await using var transaction = repository.Database.CurrentTransaction == null
            ? await repository.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

        var role = await LockScopedRoleAsync(teamId, scopedUserRoleId, ct).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"Scoped user role {scopedUserRoleId} not found");

        var result = new ScopedUserRoleDto
        {
            Id = role.Id,
            TeamId = role.TeamId,
            UserRoleId = role.UserRoleId,
            UserRoleName = role.UserRoleName,
            SpaceId = role.SpaceId,
            ProjectIds = projectIds.ToList(),
            EnvironmentIds = environmentIds.ToList(),
            ProjectGroupIds = projectGroupIds.ToList()
        };

        await DeleteScopesAsync(role.Id, ct).ConfigureAwait(false);
        await InsertScopesAsync(
            role.Id, result.ProjectIds, result.EnvironmentIds, result.ProjectGroupIds, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);

        if (transaction != null)
            await transaction.CommitAsync(ct).ConfigureAwait(false);

        return result;
    }

    private async Task<LockedScopedRole> LockScopedRoleAsync(int teamId, int scopedUserRoleId, CancellationToken ct)
    {
        var rows = await repository.Database.SqlQuery<LockedScopedRole>($"""
            SELECT scoped.id, scoped.team_id, scoped.user_role_id, scoped.space_id, role.name AS user_role_name
            FROM scoped_user_role AS scoped
            JOIN user_role AS role ON role.id = scoped.user_role_id
            WHERE scoped.team_id = {teamId} AND scoped.id = {scopedUserRoleId}
            FOR UPDATE OF scoped
            """).ToListAsync(ct).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    private async Task DeleteScopesAsync(int scopedUserRoleId, CancellationToken ct)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRoleProject>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<ScopedUserRoleEnvironment>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);
        await repository.ExecuteDeleteAsync<ScopedUserRoleProjectGroup>(
            x => x.ScopedUserRoleId == scopedUserRoleId, ct).ConfigureAwait(false);
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

    private sealed class LockedScopedRole
    {
        public int Id { get; init; }
        public int TeamId { get; init; }
        public int UserRoleId { get; init; }
        public int? SpaceId { get; init; }
        public string UserRoleName { get; init; }
    }
}

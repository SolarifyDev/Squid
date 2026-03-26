using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;

namespace Squid.Core.Services.Teams;

public interface ITeamDataProvider : IScopedDependency
{
    Task<Team> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<Team>> GetByIdsAsync(List<int> ids, CancellationToken ct = default);
    Task<List<Team>> GetAllBySpaceAsync(int spaceId, CancellationToken ct = default);
    Task AddAsync(Team team, bool forceSave = true, CancellationToken ct = default);
    Task UpdateAsync(Team team, bool forceSave = true, CancellationToken ct = default);
    Task DeleteAsync(Team team, bool forceSave = true, CancellationToken ct = default);
    Task<List<int>> GetTeamIdsByUserIdAsync(int userId, CancellationToken ct = default);
    Task<bool> IsUserInAnyTeamAsync(int userId, List<int> teamIds, CancellationToken ct = default);
    Task AddMemberAsync(TeamMember member, bool forceSave = true, CancellationToken ct = default);
    Task RemoveMemberAsync(TeamMember member, bool forceSave = true, CancellationToken ct = default);
    Task<List<TeamMember>> GetMembersByTeamIdAsync(int teamId, CancellationToken ct = default);
    Task DeleteMembersByTeamIdAsync(int teamId, CancellationToken ct = default);
}

public class TeamDataProvider(IUnitOfWork unitOfWork, IRepository repository) : ITeamDataProvider
{
    public async Task<Team> GetByIdAsync(int id, CancellationToken ct = default)
    {
        return await repository.GetByIdAsync<Team>(id, ct).ConfigureAwait(false);
    }

    public async Task<List<Team>> GetByIdsAsync(List<int> ids, CancellationToken ct = default)
    {
        return await repository.Query<Team>(x => ids.Contains(x.Id)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<Team>> GetAllBySpaceAsync(int spaceId, CancellationToken ct = default)
    {
        return await repository.Query<Team>(x => x.SpaceId == spaceId || x.SpaceId == 0).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task AddAsync(Team team, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(team, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Team team, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.UpdateAsync(team, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Team team, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.DeleteAsync(team, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<int>> GetTeamIdsByUserIdAsync(int userId, CancellationToken ct = default)
    {
        return await repository.Query<TeamMember>(x => x.UserId == userId).Select(x => x.TeamId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> IsUserInAnyTeamAsync(int userId, List<int> teamIds, CancellationToken ct = default)
    {
        return await repository.Query<TeamMember>(x => x.UserId == userId && teamIds.Contains(x.TeamId)).AnyAsync(ct).ConfigureAwait(false);
    }

    public async Task AddMemberAsync(TeamMember member, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.InsertAsync(member, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveMemberAsync(TeamMember member, bool forceSave = true, CancellationToken ct = default)
    {
        await repository.DeleteAsync(member, ct).ConfigureAwait(false);

        if (forceSave) await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<List<TeamMember>> GetMembersByTeamIdAsync(int teamId, CancellationToken ct = default)
    {
        return await repository.Query<TeamMember>(x => x.TeamId == teamId).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteMembersByTeamIdAsync(int teamId, CancellationToken ct = default)
    {
        await repository.ExecuteDeleteAsync<TeamMember>(x => x.TeamId == teamId, ct).ConfigureAwait(false);
    }
}

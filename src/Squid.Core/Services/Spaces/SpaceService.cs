using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Spaces;
using Squid.Message.Models.Spaces;

namespace Squid.Core.Services.Spaces;

public interface ISpaceService : IScopedDependency
{
    Task<SpaceDto> CreateAsync(CreateSpaceCommand command, CancellationToken ct = default);
    Task<SpaceDto> UpdateAsync(UpdateSpaceCommand command, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<SpaceDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<SpaceDto>> GetAllAsync(CancellationToken ct = default);
    Task<SpaceManagersDto> GetManagersAsync(int spaceId, CancellationToken ct = default);
}

public class SpaceService(IMapper mapper, ISpaceDataProvider spaceDataProvider, IRepository repository, ITeamDataProvider teamDataProvider, IScopedUserRoleDataProvider scopedUserRoleDataProvider, IUserRoleDataProvider userRoleDataProvider) : ISpaceService
{
    private const string SpaceOwnerRoleName = "Space Owner";

    public async Task<SpaceDto> CreateAsync(CreateSpaceCommand command, CancellationToken ct = default)
    {
        var space = mapper.Map<Persistence.Entities.Deployments.Space>(command);

        await spaceDataProvider.AddAsync(space, ct: ct).ConfigureAwait(false);
        await AssignOwnerTeamsAsync(space.Id, command.OwnerTeamIds, ct).ConfigureAwait(false);
        await SyncOwnerUsersAsync(space, command.OwnerUserIds, ct).ConfigureAwait(false);

        return mapper.Map<SpaceDto>(space);
    }

    public async Task<SpaceDto> UpdateAsync(UpdateSpaceCommand command, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(command.Id, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {command.Id} not found");

        mapper.Map(command, space);

        await spaceDataProvider.UpdateAsync(space, ct: ct).ConfigureAwait(false);
        await SyncOwnerTeamsAsync(space.Id, command.OwnerTeamIds, ct).ConfigureAwait(false);
        await SyncOwnerUsersAsync(space, command.OwnerUserIds, ct).ConfigureAwait(false);

        return mapper.Map<SpaceDto>(space);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {id} not found");

        if (space.IsDefault)
            throw new InvalidOperationException("Cannot delete the default space");

        await CleanupSpaceManagersAsync(space, ct).ConfigureAwait(false);
        await spaceDataProvider.DeleteAsync(space, ct: ct).ConfigureAwait(false);
    }

    public async Task<SpaceDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {id} not found");

        return mapper.Map<SpaceDto>(space);
    }

    public async Task<List<SpaceDto>> GetAllAsync(CancellationToken ct = default)
    {
        var spaces = await spaceDataProvider.GetAllAsync(ct).ConfigureAwait(false);

        return mapper.Map<List<SpaceDto>>(spaces);
    }

    public async Task<SpaceManagersDto> GetManagersAsync(int spaceId, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(spaceId, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {spaceId} not found");

        var spaceOwnerRoleId = await ResolveSpaceOwnerRoleIdAsync(ct).ConfigureAwait(false);

        var teams = await repository.QueryNoTracking<ScopedUserRole>()
            .Where(sr => sr.SpaceId == spaceId && sr.UserRoleId == spaceOwnerRoleId)
            .Join(repository.QueryNoTracking<Team>(), sr => sr.TeamId, t => t.Id, (sr, t) => new SpaceManagerTeamDto { TeamId = t.Id, TeamName = t.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new SpaceManagersDto();

        if (space.OwnerTeamId != null)
        {
            teams.RemoveAll(t => t.TeamId == space.OwnerTeamId.Value);

            var members = await teamDataProvider.GetMembersByTeamIdAsync(space.OwnerTeamId.Value, ct).ConfigureAwait(false);
            var userIds = members.Select(m => m.UserId).ToList();

            if (userIds.Count > 0)
            {
                result.Users = await repository.QueryNoTracking<UserAccount>(u => userIds.Contains(u.Id))
                    .Select(u => new SpaceManagerUserDto { UserId = u.Id, UserName = u.UserName, DisplayName = u.DisplayName })
                    .ToListAsync(ct).ConfigureAwait(false);
            }
        }

        result.Teams = teams;

        return result;
    }

    private async Task<int> ResolveSpaceOwnerRoleIdAsync(CancellationToken ct)
    {
        var role = await userRoleDataProvider.GetByNameAsync(SpaceOwnerRoleName, ct).ConfigureAwait(false);

        if (role == null)
            throw new InvalidOperationException($"Built-in role '{SpaceOwnerRoleName}' not found");

        return role.Id;
    }

    private async Task AssignOwnerTeamsAsync(int spaceId, List<int> teamIds, CancellationToken ct)
    {
        if (teamIds.Count == 0) return;

        var spaceOwnerRoleId = await ResolveSpaceOwnerRoleIdAsync(ct).ConfigureAwait(false);

        foreach (var teamId in teamIds)
        {
            var scopedRole = new ScopedUserRole { TeamId = teamId, UserRoleId = spaceOwnerRoleId, SpaceId = spaceId };

            await scopedUserRoleDataProvider.AddAsync(scopedRole, ct: ct).ConfigureAwait(false);
        }
    }

    private async Task SyncOwnerTeamsAsync(int spaceId, List<int> newTeamIds, CancellationToken ct)
    {
        var spaceOwnerRoleId = await ResolveSpaceOwnerRoleIdAsync(ct).ConfigureAwait(false);

        var space = await spaceDataProvider.GetByIdAsync(spaceId, ct).ConfigureAwait(false);
        var autoTeamId = space?.OwnerTeamId;

        var currentScopedRoles = await repository.QueryNoTracking<ScopedUserRole>()
            .Where(sr => sr.SpaceId == spaceId && sr.UserRoleId == spaceOwnerRoleId)
            .ToListAsync(ct).ConfigureAwait(false);

        if (autoTeamId != null)
            currentScopedRoles.RemoveAll(sr => sr.TeamId == autoTeamId.Value);

        var currentTeamIds = currentScopedRoles.Select(sr => sr.TeamId).ToHashSet();
        var newTeamIdSet = newTeamIds.ToHashSet();

        var toAdd = newTeamIdSet.Except(currentTeamIds).ToList();
        var toRemove = currentScopedRoles.Where(sr => !newTeamIdSet.Contains(sr.TeamId)).ToList();

        foreach (var scopedRole in toRemove)
        {
            await scopedUserRoleDataProvider.DeleteAsync(scopedRole.Id, ct).ConfigureAwait(false);
        }

        foreach (var teamId in toAdd)
        {
            var scopedRole = new ScopedUserRole { TeamId = teamId, UserRoleId = spaceOwnerRoleId, SpaceId = spaceId };

            await scopedUserRoleDataProvider.AddAsync(scopedRole, ct: ct).ConfigureAwait(false);
        }
    }

    private async Task SyncOwnerUsersAsync(Persistence.Entities.Deployments.Space space, List<int> newUserIds, CancellationToken ct)
    {
        if (newUserIds.Count == 0 && space.OwnerTeamId == null) return;

        if (newUserIds.Count == 0 && space.OwnerTeamId != null)
        {
            await DeleteAutoTeamAsync(space, ct).ConfigureAwait(false);
            return;
        }

        if (space.OwnerTeamId == null)
        {
            await CreateAutoTeamAsync(space, ct).ConfigureAwait(false);
        }

        var currentMembers = await teamDataProvider.GetMembersByTeamIdAsync(space.OwnerTeamId!.Value, ct).ConfigureAwait(false) ?? new();
        var currentUserIds = currentMembers.Select(m => m.UserId).ToHashSet();
        var newUserIdSet = newUserIds.ToHashSet();

        var toAdd = newUserIdSet.Except(currentUserIds).ToList();
        var toRemove = currentMembers.Where(m => !newUserIdSet.Contains(m.UserId)).ToList();

        foreach (var member in toRemove)
        {
            await teamDataProvider.RemoveMemberAsync(member, ct: ct).ConfigureAwait(false);
        }

        foreach (var userId in toAdd)
        {
            var member = new TeamMember { TeamId = space.OwnerTeamId!.Value, UserId = userId };

            await teamDataProvider.AddMemberAsync(member, ct: ct).ConfigureAwait(false);
        }
    }

    private async Task CreateAutoTeamAsync(Persistence.Entities.Deployments.Space space, CancellationToken ct)
    {
        var team = new Team { Name = $"Space Owners ({space.Name})", SpaceId = 0, IsBuiltIn = true };

        await teamDataProvider.AddAsync(team, ct: ct).ConfigureAwait(false);

        var spaceOwnerRoleId = await ResolveSpaceOwnerRoleIdAsync(ct).ConfigureAwait(false);
        var scopedRole = new ScopedUserRole { TeamId = team.Id, UserRoleId = spaceOwnerRoleId, SpaceId = space.Id };

        await scopedUserRoleDataProvider.AddAsync(scopedRole, ct: ct).ConfigureAwait(false);

        space.OwnerTeamId = team.Id;

        await spaceDataProvider.UpdateAsync(space, ct: ct).ConfigureAwait(false);
    }

    private async Task DeleteAutoTeamAsync(Persistence.Entities.Deployments.Space space, CancellationToken ct)
    {
        var autoTeamId = space.OwnerTeamId!.Value;

        var members = await teamDataProvider.GetMembersByTeamIdAsync(autoTeamId, ct).ConfigureAwait(false);

        foreach (var member in members)
        {
            await teamDataProvider.RemoveMemberAsync(member, ct: ct).ConfigureAwait(false);
        }

        var scopedRoles = await repository.QueryNoTracking<ScopedUserRole>()
            .Where(sr => sr.TeamId == autoTeamId)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var scopedRole in scopedRoles)
        {
            await scopedUserRoleDataProvider.DeleteAsync(scopedRole.Id, ct).ConfigureAwait(false);
        }

        var team = await teamDataProvider.GetByIdAsync(autoTeamId, ct).ConfigureAwait(false);

        if (team != null)
            await teamDataProvider.DeleteAsync(team, ct: ct).ConfigureAwait(false);

        space.OwnerTeamId = null;

        await spaceDataProvider.UpdateAsync(space, ct: ct).ConfigureAwait(false);
    }

    private async Task CleanupSpaceManagersAsync(Persistence.Entities.Deployments.Space space, CancellationToken ct)
    {
        await repository.ExecuteDeleteAsync<ScopedUserRole>(x => x.SpaceId == space.Id, ct).ConfigureAwait(false);

        if (space.OwnerTeamId != null)
        {
            var autoTeamId = space.OwnerTeamId.Value;

            var members = await teamDataProvider.GetMembersByTeamIdAsync(autoTeamId, ct).ConfigureAwait(false);

            foreach (var member in members)
            {
                await teamDataProvider.RemoveMemberAsync(member, ct: ct).ConfigureAwait(false);
            }

            var team = await teamDataProvider.GetByIdAsync(autoTeamId, ct).ConfigureAwait(false);

            if (team != null)
                await teamDataProvider.DeleteAsync(team, ct: ct).ConfigureAwait(false);
        }
    }
}

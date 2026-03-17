using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Commands.Teams;
using Squid.Message.Models.Teams;

namespace Squid.Core.Services.Teams;

public interface ITeamService : IScopedDependency
{
    Task<TeamDto> CreateAsync(CreateTeamCommand command, CancellationToken ct = default);
    Task<TeamDto> UpdateAsync(UpdateTeamCommand command, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<TeamDto> GetByIdAsync(int id, CancellationToken ct = default);
    Task<List<TeamDto>> GetAllBySpaceAsync(int spaceId, CancellationToken ct = default);
    Task AddMemberAsync(int teamId, int userId, CancellationToken ct = default);
    Task RemoveMemberAsync(int teamId, int userId, CancellationToken ct = default);
    Task<List<TeamMemberDto>> GetMembersAsync(int teamId, CancellationToken ct = default);
}

public class TeamService(IMapper mapper, ITeamDataProvider teamDataProvider, IRepository repository) : ITeamService
{
    public async Task<TeamDto> CreateAsync(CreateTeamCommand command, CancellationToken ct = default)
    {
        var team = mapper.Map<Team>(command);

        await teamDataProvider.AddAsync(team, ct: ct).ConfigureAwait(false);

        return mapper.Map<TeamDto>(team);
    }

    public async Task<TeamDto> UpdateAsync(UpdateTeamCommand command, CancellationToken ct = default)
    {
        var team = await teamDataProvider.GetByIdAsync(command.Id, ct).ConfigureAwait(false);

        if (team == null)
            throw new InvalidOperationException($"Team {command.Id} not found");

        if (team.IsBuiltIn)
            throw new InvalidOperationException("Cannot modify a built-in team");

        mapper.Map(command, team);

        await teamDataProvider.UpdateAsync(team, ct: ct).ConfigureAwait(false);

        return mapper.Map<TeamDto>(team);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var team = await teamDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (team == null)
            throw new InvalidOperationException($"Team {id} not found");

        if (team.IsBuiltIn)
            throw new InvalidOperationException("Cannot delete a built-in team");

        await teamDataProvider.DeleteAsync(team, ct: ct).ConfigureAwait(false);
    }

    public async Task<TeamDto> GetByIdAsync(int id, CancellationToken ct = default)
    {
        var team = await teamDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (team == null)
            throw new InvalidOperationException($"Team {id} not found");

        return mapper.Map<TeamDto>(team);
    }

    public async Task<List<TeamDto>> GetAllBySpaceAsync(int spaceId, CancellationToken ct = default)
    {
        var teams = await teamDataProvider.GetAllBySpaceAsync(spaceId, ct).ConfigureAwait(false);

        return mapper.Map<List<TeamDto>>(teams);
    }

    public async Task AddMemberAsync(int teamId, int userId, CancellationToken ct = default)
    {
        await teamDataProvider.AddMemberAsync(new TeamMember { TeamId = teamId, UserId = userId }, ct: ct).ConfigureAwait(false);
    }

    public async Task RemoveMemberAsync(int teamId, int userId, CancellationToken ct = default)
    {
        await teamDataProvider.RemoveMemberAsync(new TeamMember { TeamId = teamId, UserId = userId }, ct: ct).ConfigureAwait(false);
    }

    public async Task<List<TeamMemberDto>> GetMembersAsync(int teamId, CancellationToken ct = default)
    {
        var members = await repository.QueryNoTracking<TeamMember>(m => m.TeamId == teamId)
            .Join(repository.QueryNoTracking<UserAccount>(), m => m.UserId, u => u.Id, (m, u) => new TeamMemberDto
            {
                TeamId = m.TeamId,
                UserId = m.UserId,
                UserName = u.UserName,
                DisplayName = u.DisplayName
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return members;
    }
}

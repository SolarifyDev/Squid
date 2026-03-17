using AutoMapper;
using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Commands.Teams;
using Squid.Message.Models.Teams;

namespace Squid.Core.Services.Teams;

public interface ITeamService : IScopedDependency
{
    Task<TeamDto> CreateAsync(CreateTeamCommand command, CancellationToken ct = default);
    Task<TeamDto> UpdateAsync(UpdateTeamCommand command, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<List<TeamDto>> GetAllBySpaceAsync(int spaceId, CancellationToken ct = default);
    Task AddMemberAsync(int teamId, int userId, CancellationToken ct = default);
    Task RemoveMemberAsync(int teamId, int userId, CancellationToken ct = default);
    Task<List<TeamMemberDto>> GetMembersAsync(int teamId, CancellationToken ct = default);
}

public class TeamService(IMapper mapper, ITeamDataProvider teamDataProvider) : ITeamService
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

        mapper.Map(command, team);

        await teamDataProvider.UpdateAsync(team, ct: ct).ConfigureAwait(false);

        return mapper.Map<TeamDto>(team);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var team = await teamDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (team == null)
            throw new InvalidOperationException($"Team {id} not found");

        await teamDataProvider.DeleteAsync(team, ct: ct).ConfigureAwait(false);
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
        var members = await teamDataProvider.GetMembersByTeamIdAsync(teamId, ct).ConfigureAwait(false);

        return mapper.Map<List<TeamMemberDto>>(members);
    }
}

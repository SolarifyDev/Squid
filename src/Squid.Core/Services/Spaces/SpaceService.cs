using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
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
    Task<List<SpaceManagerTeamDto>> GetManagerTeamsAsync(int spaceId, CancellationToken ct = default);
}

public class SpaceService(IMapper mapper, ISpaceDataProvider spaceDataProvider, IRepository repository) : ISpaceService
{
    private const string SpaceOwnerRoleName = "Space Owner";

    public async Task<SpaceDto> CreateAsync(CreateSpaceCommand command, CancellationToken ct = default)
    {
        var space = mapper.Map<Persistence.Entities.Deployments.Space>(command);

        await spaceDataProvider.AddAsync(space, ct: ct).ConfigureAwait(false);

        return mapper.Map<SpaceDto>(space);
    }

    public async Task<SpaceDto> UpdateAsync(UpdateSpaceCommand command, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(command.Id, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {command.Id} not found");

        mapper.Map(command, space);

        await spaceDataProvider.UpdateAsync(space, ct: ct).ConfigureAwait(false);

        return mapper.Map<SpaceDto>(space);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var space = await spaceDataProvider.GetByIdAsync(id, ct).ConfigureAwait(false);

        if (space == null)
            throw new InvalidOperationException($"Space {id} not found");

        if (space.IsDefault)
            throw new InvalidOperationException("Cannot delete the default space");

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

    public async Task<List<SpaceManagerTeamDto>> GetManagerTeamsAsync(int spaceId, CancellationToken ct = default)
    {
        var managers = await repository.QueryNoTracking<ScopedUserRole>()
            .Where(sr => sr.SpaceId == spaceId)
            .Join(repository.QueryNoTracking<UserRole>(), sr => sr.UserRoleId, ur => ur.Id, (sr, ur) => new { sr, ur })
            .Where(x => x.ur.Name == SpaceOwnerRoleName)
            .Join(repository.QueryNoTracking<Team>(), x => x.sr.TeamId, t => t.Id, (x, t) => new SpaceManagerTeamDto
            {
                TeamId = t.Id,
                TeamName = t.Name
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return managers;
    }
}

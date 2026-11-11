using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportDestinationDataProvider : IScopedDependency
{
    Task<IReadOnlyList<OctopusImportDestinationResource>> GetResourcesAsync(
        int destinationSpaceId,
        CancellationToken cancellationToken = default);
}

public class OctopusImportDestinationDataProvider(IRepository repository) : IOctopusImportDestinationDataProvider
{
    public async Task<IReadOnlyList<OctopusImportDestinationResource>> GetResourcesAsync(
        int destinationSpaceId,
        CancellationToken cancellationToken = default)
    {
        var resources = new List<OctopusImportDestinationResource>();

        resources.AddRange(await repository.QueryNoTracking<Project>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Project, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<ProjectGroup>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.ProjectGroup, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<DeploymentEnvironment>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Environment, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<Lifecycle>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Lifecycle, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<ExternalFeed>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Feed, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<LibraryVariableSet>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.LibraryVariableSet, x.Name, null, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await LoadTeamsAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<Machine>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Machine, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<DeploymentAccount>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Account, x.Name, x.Slug, x.LastModifiedDate))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        return resources
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Id)
            .ToList();
    }

    // Teams carry their built-in flag, current member count and member fingerprint: reusing a team grants approval rights to its members.
    private async Task<List<OctopusImportDestinationResource>> LoadTeamsAsync(int destinationSpaceId, CancellationToken ct)
    {
        var teams = await repository.QueryNoTracking<Team>(x => x.SpaceId == destinationSpaceId || x.SpaceId == 0).ToListAsync(ct).ConfigureAwait(false);

        var membersByTeam = await LoadMembersByTeamAsync(teams.Select(t => t.Id).ToList(), ct).ConfigureAwait(false);

        return teams.Select(x => ToTeamResource(x, membersByTeam[x.Id].ToList())).ToList();
    }

    private async Task<ILookup<int, int>> LoadMembersByTeamAsync(List<int> teamIds, CancellationToken ct)
    {
        var members = await repository.QueryNoTracking<TeamMember>(m => teamIds.Contains(m.TeamId)).Select(m => new { m.TeamId, m.UserId }).ToListAsync(ct).ConfigureAwait(false);

        return members.ToLookup(m => m.TeamId, m => m.UserId);
    }

    private static OctopusImportDestinationResource ToTeamResource(Team team, List<int> memberUserIds)
        => new(team.Id, team.SpaceId, OctopusResourceKind.Team, team.Name, null, team.LastModifiedDate)
        {
            IsBuiltIn = team.IsBuiltIn,
            MemberCount = memberUserIds.Count,
            MemberFingerprint = OctopusImportTeamMembership.Fingerprint(memberUserIds)
        };
}

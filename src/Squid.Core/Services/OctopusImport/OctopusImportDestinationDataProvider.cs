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

    // Teams carry their built-in flag and current member count: reusing a team grants approval rights to its members.
    private async Task<List<OctopusImportDestinationResource>> LoadTeamsAsync(int destinationSpaceId, CancellationToken ct)
    {
        var teams = await repository.QueryNoTracking<Team>(x => x.SpaceId == destinationSpaceId || x.SpaceId == 0).ToListAsync(ct).ConfigureAwait(false);
        var teamIds = teams.Select(t => t.Id).ToList();

        var memberCounts = await repository.QueryNoTracking<TeamMember>(m => teamIds.Contains(m.TeamId))
            .GroupBy(m => m.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct)
            .ConfigureAwait(false);

        return teams
            .Select(x => new OctopusImportDestinationResource(x.Id, x.SpaceId, OctopusResourceKind.Team, x.Name, null, x.LastModifiedDate) { IsBuiltIn = x.IsBuiltIn, MemberCount = memberCounts.GetValueOrDefault(x.Id) })
            .ToList();
    }
}

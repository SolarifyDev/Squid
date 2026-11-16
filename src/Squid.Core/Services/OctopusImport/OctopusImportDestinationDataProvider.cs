using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportDestinationDataProvider : IScopedDependency
{
    Task<IReadOnlyList<OctopusImportDestinationResource>> GetResourcesAsync(
        int destinationSpaceId,
        CancellationToken cancellationToken = default);

    /// <summary>The release identities that exist under the given destination projects.</summary>
    Task<IReadOnlyList<OctopusImportReleaseKey>> GetReleaseKeysAsync(int destinationSpaceId, IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken = default);

    /// <summary>Squid's current release notes and task scalars of the given rows in the space (what an Update would rewrite), for the preview's old → new.</summary>
    Task<OctopusImportStoredHistory> GetStoredHistoryAsync(int destinationSpaceId, IReadOnlyCollection<int> releaseIds, IReadOnlyCollection<int> taskIds, CancellationToken cancellationToken = default);

    /// <summary>The names of the variables the given variable sets of the space hold now (never values).</summary>
    Task<IReadOnlyList<OctopusImportDestinationVariable>> GetVariableNamesAsync(int destinationSpaceId, IReadOnlyCollection<int> variableSetIds, CancellationToken cancellationToken = default);
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
                x.Id, x.SpaceId, OctopusResourceKind.Project, x.Name, x.Slug, x.LastModifiedDate) { VariableSetId = x.VariableSetId })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false));

        resources.AddRange(await repository.QueryNoTracking<Channel>(x => x.SpaceId == destinationSpaceId)
            .Select(x => new OctopusImportDestinationResource(
                x.Id, x.SpaceId, OctopusResourceKind.Channel, x.Name, x.Slug, x.LastModifiedDate) { ProjectId = x.ProjectId })
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
                x.Id, x.SpaceId, OctopusResourceKind.LibraryVariableSet, x.Name, null, x.LastModifiedDate) { VariableSetId = x.VariableSetId })
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

    public async Task<IReadOnlyList<OctopusImportReleaseKey>> GetReleaseKeysAsync(int destinationSpaceId, IReadOnlyCollection<int> projectIds, CancellationToken cancellationToken = default)
    {
        if (projectIds == null || projectIds.Count == 0) return [];

        return await repository.QueryNoTracking<Release>(x => x.SpaceId == destinationSpaceId && projectIds.Contains(x.ProjectId)).Select(x => new OctopusImportReleaseKey(x.ProjectId, x.ChannelId, x.Version)).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<OctopusImportStoredHistory> GetStoredHistoryAsync(int destinationSpaceId, IReadOnlyCollection<int> releaseIds, IReadOnlyCollection<int> taskIds, CancellationToken cancellationToken = default)
    {
        var releaseNotes = await LoadReleaseNotesAsync(destinationSpaceId, releaseIds, cancellationToken).ConfigureAwait(false);
        var tasks = await LoadStoredTasksAsync(destinationSpaceId, taskIds, cancellationToken).ConfigureAwait(false);

        return new OctopusImportStoredHistory(releaseNotes, tasks);
    }

    public async Task<IReadOnlyList<OctopusImportDestinationVariable>> GetVariableNamesAsync(int destinationSpaceId, IReadOnlyCollection<int> variableSetIds, CancellationToken cancellationToken = default)
    {
        if (variableSetIds == null || variableSetIds.Count == 0) return [];

        var sets = repository.QueryNoTracking<VariableSet>(s => s.SpaceId == destinationSpaceId && variableSetIds.Contains(s.Id)).Select(s => s.Id);

        return await repository.QueryNoTracking<Variable>(v => sets.Contains(v.VariableSetId)).Select(v => new OctopusImportDestinationVariable(v.VariableSetId, v.Name)).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, string>> LoadReleaseNotesAsync(int destinationSpaceId, IReadOnlyCollection<int> releaseIds, CancellationToken ct)
    {
        if (releaseIds == null || releaseIds.Count == 0) return [];

        return await repository.QueryNoTracking<Release>(x => x.SpaceId == destinationSpaceId && releaseIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.ReleaseNote, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<int, OctopusImportStoredTask>> LoadStoredTasksAsync(int destinationSpaceId, IReadOnlyCollection<int> taskIds, CancellationToken ct)
    {
        if (taskIds == null || taskIds.Count == 0) return [];

        var tasks = await repository.QueryNoTracking<ServerTask>(x => x.SpaceId == destinationSpaceId && taskIds.Contains(x.Id)).Select(x => new OctopusImportStoredTask(x.Id, x.State, x.QueueTime, x.StartTime, x.CompletedTime, x.ErrorMessage, x.HasWarningsOrErrors)).ToListAsync(ct).ConfigureAwait(false);

        return tasks.ToDictionary(x => x.Id);
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

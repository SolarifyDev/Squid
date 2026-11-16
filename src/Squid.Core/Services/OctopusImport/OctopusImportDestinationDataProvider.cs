using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.Deployments.Variable;
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

    /// <summary>
    /// The names of the rows of one child kind the given owners of the space hold now (never values): the steps of processes, the actions
    /// of steps, the phases of lifecycles or the variables of variable sets.
    /// </summary>
    Task<IReadOnlyList<OctopusImportDestinationChild>> GetChildNamesAsync(int destinationSpaceId, OctopusResourceKind kind, IReadOnlyCollection<int> ownerIds, CancellationToken cancellationToken = default);

    /// <summary>The releases and deployments of the space whose owner rows were deleted in Squid (deletes do not cascade).</summary>
    Task<OctopusImportOrphanedHistory> GetOrphanedHistoryAsync(int destinationSpaceId, CancellationToken cancellationToken = default);
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

    public async Task<IReadOnlyList<OctopusImportDestinationChild>> GetChildNamesAsync(int destinationSpaceId, OctopusResourceKind kind, IReadOnlyCollection<int> ownerIds, CancellationToken cancellationToken = default)
    {
        if (ownerIds == null || ownerIds.Count == 0) return [];

        var names = await ChildNames(destinationSpaceId, kind, ownerIds).ToListAsync(cancellationToken).ConfigureAwait(false);
        var scopes = kind == OctopusResourceKind.Variable ? await VariableScopeTokensAsync(names.Select(n => n.Id).ToList(), cancellationToken).ConfigureAwait(false) : null;

        return names.Select(n => new OctopusImportDestinationChild(kind.ToString(), n.OwnerId, n.Name) { Id = n.Id, Scopes = scopes == null ? null : scopes.GetValueOrDefault(n.Id) ?? [] }).ToList();
    }

    // Each variable's scope as the canonical tokens its VariableIdentity is built from (an unscoped variable has none).
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> VariableScopeTokensAsync(IReadOnlyCollection<int> variableIds, CancellationToken cancellationToken)
    {
        var scopes = await repository.QueryNoTracking<VariableScope>(s => variableIds.Contains(s.VariableId)).Select(s => new { s.VariableId, s.ScopeType, s.ScopeValue }).ToListAsync(cancellationToken).ConfigureAwait(false);

        return scopes.GroupBy(s => s.VariableId).ToDictionary(g => g.Key, g => VariableIdentity.Canonical(g.Where(s => !string.IsNullOrWhiteSpace(s.ScopeValue)).Select(s => VariableIdentity.ScopeToken(s.ScopeType, s.ScopeValue))));
    }

    // Each owner is read through its space, so an id of another space never answers.
    private IQueryable<OwnedName> ChildNames(int spaceId, OctopusResourceKind kind, IReadOnlyCollection<int> ownerIds)
        => kind switch
        {
            OctopusResourceKind.DeploymentStep => StepNames(spaceId, ownerIds),
            OctopusResourceKind.DeploymentAction => ActionNames(spaceId, ownerIds),
            OctopusResourceKind.LifecyclePhase => PhaseNames(spaceId, ownerIds),
            OctopusResourceKind.Variable => VariableNames(spaceId, ownerIds),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only steps, actions, lifecycle phases and variables are read by owner.")
        };

    private IQueryable<OwnedName> StepNames(int spaceId, IReadOnlyCollection<int> processIds)
    {
        var processes = repository.QueryNoTracking<DeploymentProcess>(p => p.SpaceId == spaceId && processIds.Contains(p.Id)).Select(p => p.Id);

        return repository.QueryNoTracking<DeploymentStep>(s => processes.Contains(s.ProcessId)).Select(s => new OwnedName(s.Id, s.ProcessId, s.Name));
    }

    private IQueryable<OwnedName> ActionNames(int spaceId, IReadOnlyCollection<int> stepIds)
    {
        var processes = repository.QueryNoTracking<DeploymentProcess>(p => p.SpaceId == spaceId).Select(p => p.Id);
        var steps = repository.QueryNoTracking<DeploymentStep>(s => stepIds.Contains(s.Id) && processes.Contains(s.ProcessId)).Select(s => s.Id);

        return repository.QueryNoTracking<DeploymentAction>(a => steps.Contains(a.StepId)).Select(a => new OwnedName(a.Id, a.StepId, a.Name));
    }

    private IQueryable<OwnedName> PhaseNames(int spaceId, IReadOnlyCollection<int> lifecycleIds)
    {
        var lifecycles = repository.QueryNoTracking<Lifecycle>(l => l.SpaceId == spaceId && lifecycleIds.Contains(l.Id)).Select(l => l.Id);

        return repository.QueryNoTracking<LifecyclePhase>(p => lifecycles.Contains(p.LifecycleId)).Select(p => new OwnedName(p.Id, p.LifecycleId, p.Name));
    }

    private IQueryable<OwnedName> VariableNames(int spaceId, IReadOnlyCollection<int> variableSetIds)
    {
        var sets = repository.QueryNoTracking<VariableSet>(s => s.SpaceId == spaceId && variableSetIds.Contains(s.Id)).Select(s => s.Id);

        return repository.QueryNoTracking<Variable>(v => sets.Contains(v.VariableSetId)).Select(v => new OwnedName(v.Id, v.VariableSetId, v.Name));
    }

    private sealed record OwnedName(int Id, int OwnerId, string Name);

    public async Task<OctopusImportOrphanedHistory> GetOrphanedHistoryAsync(int destinationSpaceId, CancellationToken cancellationToken = default)
    {
        var releaseIds = await LoadOrphanedReleaseIdsAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false);
        var deploymentIds = await LoadOrphanedDeploymentIdsAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false);

        return new OctopusImportOrphanedHistory(releaseIds.ToHashSet(), deploymentIds.ToHashSet());
    }

    private async Task<List<int>> LoadOrphanedReleaseIdsAsync(int destinationSpaceId, CancellationToken ct)
    {
        var projects = repository.QueryNoTracking<Project>();
        var channels = repository.QueryNoTracking<Channel>();

        return await repository.QueryNoTracking<Release>(r => r.SpaceId == destinationSpaceId).Where(r => !projects.Any(p => p.Id == r.ProjectId) || !channels.Any(c => c.Id == r.ChannelId)).Select(r => r.Id).ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<int>> LoadOrphanedDeploymentIdsAsync(int destinationSpaceId, CancellationToken ct)
    {
        var projects = repository.QueryNoTracking<Project>();
        var channels = repository.QueryNoTracking<Channel>();
        var releases = repository.QueryNoTracking<Release>();
        var environments = repository.QueryNoTracking<DeploymentEnvironment>();

        return await repository.QueryNoTracking<Deployment>(d => d.SpaceId == destinationSpaceId).Where(d => !projects.Any(p => p.Id == d.ProjectId) || !channels.Any(c => c.Id == d.ChannelId) || !releases.Any(r => r.Id == d.ReleaseId) || !environments.Any(e => e.Id == d.EnvironmentId)).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
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

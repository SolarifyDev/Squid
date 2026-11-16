using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportConflictDiscoveryService : IScopedDependency
{
    Task<OctopusImportConflictDiscoveryResult> DiscoverAsync(
        int destinationSpaceId,
        OctopusResourceGraph graph,
        CancellationToken cancellationToken = default);
}

public class OctopusImportConflictDiscoveryService(
    IOctopusImportDestinationDataProvider destinationDataProvider,
    IOctopusImportSourceMapDataProvider sourceMapDataProvider = null,
    Lazy<IVariableService> variableService = null,
    Lazy<IExternalFeedService> externalFeedService = null,
    IOctopusImportSessionDataProvider sessionDataProvider = null)
    : IOctopusImportConflictDiscoveryService
{
    // Lazy: both services build the variable encryption service, whose constructor throws under an empty master key. Previewing an
    // import with no missing secrets must not need a key, so they are built only when a missing secret slot is actually read.

    public async Task<OctopusImportConflictDiscoveryResult> DiscoverAsync(
        int destinationSpaceId,
        OctopusResourceGraph graph,
        CancellationToken cancellationToken = default)
    {
        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        ArgumentNullException.ThrowIfNull(graph);

        var destinationResources = await destinationDataProvider.GetResourcesAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false);
        var prior = await LoadPriorMappingsAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false);
        var conflicts = FindConflicts(destinationSpaceId, graph, destinationResources, prior);
        var releaseKeys = await LoadExistingReleaseKeysAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var secretSlots = await LoadMissingSecretSlotsAsync(prior, cancellationToken).ConfigureAwait(false);
        var origin = await LoadRecordedOriginAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var sourceOrigin = await LoadSourceOriginAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var creatorOrigins = await LoadCreatorOriginsAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var storedHistory = await LoadStoredHistoryAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var destinationVariables = await LoadVariablesWithoutRowsAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);
        var orphanedHistory = await LoadOrphanedHistoryAsync(destinationSpaceId, prior, cancellationToken).ConfigureAwait(false);

        return new OctopusImportConflictDiscoveryResult(conflicts) { DestinationVariables = destinationVariables, PriorMappings = prior, ExistingReleaseKeys = releaseKeys, HistoryInstallationId = graph.InstallationId, ExportSourceId = graph.ExportSourceId, SecretSlots = secretSlots, FillDestinations = OctopusImportFillDestinations.From(destinationResources, prior), InstallationOrigin = origin, SourceOrigin = sourceOrigin, CreatorOrigins = creatorOrigins, Destinations = destinationResources, StoredHistory = storedHistory, OrphanedHistory = orphanedHistory };
    }

    // Only the rows an Update would rewrite are read: imported history whose notes or task changed in Octopus.
    private async Task<OctopusImportStoredHistory> LoadStoredHistoryAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        var releaseIds = OctopusImportPreviewChanges.ReleaseIdsToCompare(graph.Resources, prior);
        var taskIds = OctopusImportPreviewChanges.TaskIdsToCompare(graph.Resources, prior);

        if (releaseIds.Count == 0 && taskIds.Count == 0) return OctopusImportStoredHistory.Empty;

        return await destinationDataProvider.GetStoredHistoryAsync(destinationSpaceId, releaseIds, taskIds, ct).ConfigureAwait(false) ?? OctopusImportStoredHistory.Empty;
    }

    // Only a space an earlier import wrote history into can hold imported history a Squid delete orphaned.
    private async Task<OctopusImportOrphanedHistory> LoadOrphanedHistoryAsync(int destinationSpaceId, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        if (!prior.Mappings.Keys.Any(IsHistoryRow)) return OctopusImportOrphanedHistory.Empty;

        return await destinationDataProvider.GetOrphanedHistoryAsync(destinationSpaceId, ct).ConfigureAwait(false) ?? OctopusImportOrphanedHistory.Empty;
    }

    private static bool IsHistoryRow((string Type, string Id) key)
        => key.Type == OctopusImportPriorMappings.Normalize(nameof(OctopusResourceKind.Release)) || key.Type == OctopusImportPriorMappings.Normalize(nameof(OctopusResourceKind.Deployment));

    // Only the imported sets with an incoming variable that has no live row are read (by name): on an unchanged repeat, nothing.
    private async Task<IReadOnlyList<OctopusImportDestinationVariable>> LoadVariablesWithoutRowsAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        var setIds = graph.Resources
            .Where(n => n.Kind == OctopusResourceKind.Variable && !prior.TryGet(nameof(OctopusResourceKind.Variable), n.SourceId, out _))
            .Select(n => prior.TryGet(nameof(OctopusResourceKind.VariableSet), OctopusImportPriorMappingPolicy.OwnerSourceId(n), out var set) ? set.DestinationId : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

        if (setIds.Count == 0) return [];

        return await destinationDataProvider.GetVariableNamesAsync(destinationSpaceId, setIds, ct).ConfigureAwait(false) ?? [];
    }

    // A source not proven by the space's installation id shows where the space came from: read from the newest import session that
    // recorded that installation.
    private async Task<OctopusImportInstallationOrigin> LoadRecordedOriginAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prior.InstallationId) || SameInstallation(graph.InstallationId, prior.InstallationId)) return null;

        return await LoadOriginAsync(destinationSpaceId, prior.InstallationId, ct).ConfigureAwait(false);
    }

    // An Octopus server read into a space that recorded another (or no) installation shows the server it reads: its own session.
    private async Task<OctopusImportInstallationOrigin> LoadSourceOriginAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(graph.InstallationId) || prior.Mappings.Count == 0 || SameInstallation(graph.InstallationId, prior.InstallationId)) return null;

        return await LoadOriginAsync(destinationSpaceId, graph.InstallationId, ct).ConfigureAwait(false);
    }

    // Rows a third origin created, which the source would link to, refuse the import: the installations that created them (those a
    // refusal names) show the server they were read from.
    private async Task<IReadOnlyDictionary<string, OctopusImportInstallationOrigin>> LoadCreatorOriginsAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        var creators = OctopusImportFillEvidences.OtherOriginCreators(graph.InstallationId, graph.ExportSourceId, graph.Resources, prior).Take(OctopusImportOriginMessages.NamedCreatorLimit);
        var origins = new Dictionary<string, OctopusImportInstallationOrigin>(StringComparer.Ordinal);

        foreach (var installationId in creators.Select(c => c.InstallationId).Where(id => id != null))
            if (await LoadOriginAsync(destinationSpaceId, installationId, ct).ConfigureAwait(false) is { } origin) origins[OctopusImportPriorMappings.Normalize(installationId)] = origin;

        return origins;
    }

    private async Task<OctopusImportInstallationOrigin> LoadOriginAsync(int destinationSpaceId, string installationId, CancellationToken ct)
    {
        if (sessionDataProvider == null) return null;

        var fetch = await sessionDataProvider.GetLatestHistoryFetchAsync(destinationSpaceId, installationId, ct).ConfigureAwait(false);

        return fetch == null ? null : new OctopusImportInstallationOrigin(fetch.InstallationId, fetch.ServerHost, fetch.OctopusSpaceId, fetch.OctopusVersion);
    }

    private static bool SameInstallation(string incoming, string recorded)
        => !string.IsNullOrWhiteSpace(incoming) && OctopusImportPriorMappings.Normalize(incoming) == OctopusImportPriorMappings.Normalize(recorded);

    // Only the Squid rows a live Missing secret row points at: whether each secret is still empty decides fill / kept / settled.
    private async Task<OctopusImportSecretSlots> LoadMissingSecretSlotsAsync(OctopusImportPriorMappings prior, CancellationToken ct)
    {
        var variableSetIds = OctopusImportSecretFills.MissingDestinations(prior, OctopusImportSourceMapQueries.VariableSecretSourceType);
        var feedIds = OctopusImportSecretFills.MissingDestinations(prior, OctopusImportSourceMapQueries.FeedSecretSourceType);

        var variables = variableSetIds.Count == 0 || variableService == null ? [] : await variableService.Value.GetSensitiveVariableSlotsAsync(variableSetIds, ct).ConfigureAwait(false);
        var feeds = feedIds.Count == 0 || externalFeedService == null ? [] : await externalFeedService.Value.GetCredentialSlotsAsync(feedIds, ct).ConfigureAwait(false);

        return new OctopusImportSecretSlots(variables, feeds);
    }

    private static List<OctopusImportResourceConflict> FindConflicts(int destinationSpaceId, OctopusResourceGraph graph, IReadOnlyList<OctopusImportDestinationResource> destinationResources, OctopusImportPriorMappings prior)
    {
        var destinationsByKind = destinationResources
            .GroupBy(x => x.Kind)
            .ToDictionary(x => x.Key, x => x.OrderBy(resource => resource.Id).ToList());

        return graph.Resources
            .Where(x => !x.IsHistorical && IsDiscoverable(x.Kind))
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(source => BuildConflict(source, ScopedToOwner(source, destinationsByKind, prior), destinationSpaceId))
            .Where(conflict => conflict != null)
            .ToList();
    }

    private async Task<OctopusImportPriorMappings> LoadPriorMappingsAsync(int destinationSpaceId, CancellationToken ct)
    {
        if (sourceMapDataProvider == null) return OctopusImportPriorMappings.Empty;

        return await sourceMapDataProvider.LoadAsync(destinationSpaceId, ct).ConfigureAwait(false);
    }

    // Only a project imported before can already hold a release with the incoming version, so the
    // release identities are loaded for prior-mapped projects only.
    private async Task<IReadOnlySet<OctopusImportReleaseKey>> LoadExistingReleaseKeysAsync(int destinationSpaceId, OctopusResourceGraph graph, OctopusImportPriorMappings prior, CancellationToken ct)
    {
        var projectIds = PriorMappedReleaseProjectIds(graph, prior);

        if (projectIds.Count == 0) return new HashSet<OctopusImportReleaseKey>();

        var keys = await destinationDataProvider.GetReleaseKeysAsync(destinationSpaceId, projectIds, ct).ConfigureAwait(false);

        return keys.ToHashSet();
    }

    private static List<int> PriorMappedReleaseProjectIds(OctopusResourceGraph graph, OctopusImportPriorMappings prior)
        => graph.Resources
            .Where(r => r.Kind == OctopusResourceKind.Release)
            .Select(r => r.GetSource<OctopusReleaseDto>()?.ProjectId)
            .Select(projectId => prior.TryGet(OctopusResourceKind.Project.ToString(), projectId, out var mapping) ? mapping.DestinationId : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();

    // A channel's name is unique only within its project, and only a project imported before can already hold one: its channels are
    // matched against the Squid channels of the project the source map records (none for a project this import creates) that no other
    // Octopus channel is linked to.
    private static IReadOnlyDictionary<OctopusResourceKind, List<OctopusImportDestinationResource>> ScopedToOwner(OctopusResourceNode source, IReadOnlyDictionary<OctopusResourceKind, List<OctopusImportDestinationResource>> destinationsByKind, OctopusImportPriorMappings prior)
    {
        if (source.Kind != OctopusResourceKind.Channel) return destinationsByKind;

        var projectId = prior.TryGet(nameof(OctopusResourceKind.Project), source.GetSource<OctopusChannelDto>()?.ProjectId ?? source.OwnerProjectId, out var project) ? project.DestinationId : (int?)null;
        var channels = destinationsByKind.GetValueOrDefault(OctopusResourceKind.Channel, []).Where(c => projectId != null && c.ProjectId == projectId && !IsLinkedToAnotherChannel(c.Id, source.SourceId, prior)).ToList();

        return new Dictionary<OctopusResourceKind, List<OctopusImportDestinationResource>> { [OctopusResourceKind.Channel] = channels };
    }

    // A Squid channel the source map already links to another Octopus channel is that channel's: never a second Octopus channel's too.
    private static bool IsLinkedToAnotherChannel(int destinationId, string sourceId, OctopusImportPriorMappings prior)
    {
        var (channelType, channelId) = OctopusImportPriorMappings.Key(nameof(OctopusResourceKind.Channel), sourceId);

        return prior.Mappings.Any(m => m.Key.Type == channelType && m.Key.Id != channelId && m.Value.DestinationId == destinationId);
    }

    private static OctopusImportResourceConflict BuildConflict(OctopusResourceNode source, IReadOnlyDictionary<OctopusResourceKind, List<OctopusImportDestinationResource>> destinationsByKind, int destinationSpaceId)
    {
        if (!destinationsByKind.TryGetValue(source.Kind, out var destinations))
            return null;

        var matches = IsBuiltInTeamWithEquivalent(source, out var squidTeamName)
            ? MatchSquidBuiltInTeam(squidTeamName, destinations)
            : PreferDestinationSpace(MatchByIdentity(source, destinations), destinationSpaceId);

        return matches.Count == 0
            ? null
            : new OctopusImportResourceConflict(source, matches);
    }

    private static List<OctopusImportDestinationMatch> MatchByIdentity(OctopusResourceNode source, IEnumerable<OctopusImportDestinationResource> destinations)
    {
        var sourceSlug = source.GetSource<OctopusDocumentDto>()?.Slug;

        return destinations
            .Select(destination => BuildMatch(source.Name, sourceSlug, destination))
            .Where(match => match != null)
            .ToList();
    }

    private static bool IsBuiltInTeamWithEquivalent(OctopusResourceNode source, out string squidTeamName)
    {
        squidTeamName = null;

        return source.Kind == OctopusResourceKind.Team && OctopusBuiltInTeams.TryGetSquidEquivalent(source.SourceId, out squidTeamName);
    }

    // A built-in Octopus team (recognised by its fixed id, whatever its display name) maps ONLY onto the
    // system-wide Squid built-in team of the equivalent name, never onto a same-named custom team.
    private static List<OctopusImportDestinationMatch> MatchSquidBuiltInTeam(string squidTeamName, IEnumerable<OctopusImportDestinationResource> destinations)
        => destinations
            .Where(destination => destination.IsBuiltIn && destination.SpaceId == 0 && IdentityEquals(squidTeamName, destination.Name))
            .Select(destination => new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))
            .ToList();

    // Only teams can live at the system scope (SpaceId 0). When the destination space has its own resource
    // with the same identity, the closest scope wins deterministically; the system-scope resource is reused
    // only when the destination space has none. (Other kinds are space-only, so this is a no-op for them.)
    private static List<OctopusImportDestinationMatch> PreferDestinationSpace(List<OctopusImportDestinationMatch> matches, int destinationSpaceId)
    {
        var inDestinationSpace = matches.Where(match => match.Destination.SpaceId == destinationSpaceId).ToList();

        return inDestinationSpace.Count > 0 ? inDestinationSpace : matches;
    }

    private static OctopusImportDestinationMatch BuildMatch(
        string sourceName,
        string sourceSlug,
        OctopusImportDestinationResource destination)
    {
        var nameMatches = IdentityEquals(sourceName, destination.Name);
        var slugMatches = IdentityEquals(sourceSlug, destination.Slug);

        if (!nameMatches && !slugMatches)
            return null;

        var matchKind = (nameMatches, slugMatches) switch
        {
            (true, true) => OctopusImportIdentityMatchKind.NameAndSlug,
            (true, false) => OctopusImportIdentityMatchKind.Name,
            _ => OctopusImportIdentityMatchKind.Slug
        };

        return new OctopusImportDestinationMatch(destination, matchKind);
    }

    private static bool IdentityEquals(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDiscoverable(OctopusResourceKind kind)
        => kind is OctopusResourceKind.Project
            or OctopusResourceKind.Channel
            or OctopusResourceKind.ProjectGroup
            or OctopusResourceKind.Environment
            or OctopusResourceKind.Lifecycle
            or OctopusResourceKind.Feed
            or OctopusResourceKind.Team
            or OctopusResourceKind.LibraryVariableSet
            or OctopusResourceKind.Machine
            or OctopusResourceKind.Account;
}

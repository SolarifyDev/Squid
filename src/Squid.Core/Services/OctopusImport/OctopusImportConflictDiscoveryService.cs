using Squid.Core.Services.OctopusImport.Octopus;
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
    IOctopusImportSourceMapDataProvider sourceMapDataProvider = null)
    : IOctopusImportConflictDiscoveryService
{
    public async Task<OctopusImportConflictDiscoveryResult> DiscoverAsync(
        int destinationSpaceId,
        OctopusResourceGraph graph,
        CancellationToken cancellationToken = default)
    {
        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space id must be positive.");

        ArgumentNullException.ThrowIfNull(graph);

        var conflicts = await FindConflictsAsync(destinationSpaceId, graph, cancellationToken).ConfigureAwait(false);
        var prior = await LoadPriorMappingsAsync(destinationSpaceId, cancellationToken).ConfigureAwait(false);
        var releaseKeys = await LoadExistingReleaseKeysAsync(destinationSpaceId, graph, prior, cancellationToken).ConfigureAwait(false);

        return new OctopusImportConflictDiscoveryResult(conflicts) { PriorMappings = prior, ExistingReleaseKeys = releaseKeys, HistoryInstallationId = graph.InstallationId };
    }

    private async Task<List<OctopusImportResourceConflict>> FindConflictsAsync(int destinationSpaceId, OctopusResourceGraph graph, CancellationToken ct)
    {
        var destinationResources = await destinationDataProvider.GetResourcesAsync(destinationSpaceId, ct).ConfigureAwait(false);

        var destinationsByKind = destinationResources
            .GroupBy(x => x.Kind)
            .ToDictionary(x => x.Key, x => x.OrderBy(resource => resource.Id).ToList());

        return graph.Resources
            .Where(x => !x.IsHistorical && IsDiscoverable(x.Kind))
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(source => BuildConflict(source, destinationsByKind, destinationSpaceId))
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
            or OctopusResourceKind.ProjectGroup
            or OctopusResourceKind.Environment
            or OctopusResourceKind.Lifecycle
            or OctopusResourceKind.Feed
            or OctopusResourceKind.Team
            or OctopusResourceKind.LibraryVariableSet
            or OctopusResourceKind.Machine
            or OctopusResourceKind.Account;
}

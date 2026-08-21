using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportConflictDiscoveryService : IScopedDependency
{
    Task<OctopusImportConflictDiscoveryResult> DiscoverAsync(
        int destinationSpaceId,
        OctopusResourceGraph graph,
        CancellationToken cancellationToken = default);
}

public class OctopusImportConflictDiscoveryService(
    IOctopusImportDestinationDataProvider destinationDataProvider)
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

        var destinationResources = await destinationDataProvider
            .GetResourcesAsync(destinationSpaceId, cancellationToken)
            .ConfigureAwait(false);

        var destinationsByKind = destinationResources
            .GroupBy(x => x.Kind)
            .ToDictionary(x => x.Key, x => x.OrderBy(resource => resource.Id).ToList());

        var conflicts = graph.Resources
            .Where(x => !x.IsHistorical && IsDiscoverable(x.Kind))
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.SourceId, StringComparer.OrdinalIgnoreCase)
            .Select(source => BuildConflict(source, destinationsByKind))
            .Where(conflict => conflict != null)
            .ToList();

        return new OctopusImportConflictDiscoveryResult(conflicts);
    }

    private static OctopusImportResourceConflict BuildConflict(
        OctopusResourceNode source,
        IReadOnlyDictionary<OctopusResourceKind, List<OctopusImportDestinationResource>> destinationsByKind)
    {
        if (!destinationsByKind.TryGetValue(source.Kind, out var destinations))
            return null;

        var sourceSlug = source.GetSource<OctopusDocumentDto>()?.Slug;
        var matches = destinations
            .Select(destination => BuildMatch(source.Name, sourceSlug, destination))
            .Where(match => match != null)
            .ToList();

        return matches.Count == 0
            ? null
            : new OctopusImportResourceConflict(source, matches);
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
            or OctopusResourceKind.Machine
            or OctopusResourceKind.Account;
}

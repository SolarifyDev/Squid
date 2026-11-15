using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// Names the Squid row a still-missing secret lives in the way a user finds it — the owning project or library variable set (by its
/// variable set), or the feed — so preview says exactly where a fill lands, not just a variable set id.
/// </summary>
public static class OctopusImportFillDestinations
{
    /// <summary>The destinations of every live <c>Missing</c> row, from the space's current projects, library variable sets and feeds.</summary>
    public static IReadOnlyList<OctopusImportFillDestinationDto> From(IEnumerable<OctopusImportDestinationResource> resources, OctopusImportPriorMappings prior)
    {
        var variableSetIds = OctopusImportSecretFills.MissingDestinations(prior, OctopusImportSourceMapQueries.VariableSecretSourceType).ToHashSet();
        var feedIds = OctopusImportSecretFills.MissingDestinations(prior, OctopusImportSourceMapQueries.FeedSecretSourceType).ToHashSet();

        return (resources ?? []).Select(r => Destination(r, variableSetIds, feedIds)).Where(d => d != null).ToList();
    }

    /// <summary>Where the candidate's secret lives in Squid; null when its row is gone (the message then names the bare id).</summary>
    public static OctopusImportFillDestinationDto Of(OctopusImportSecretFillCandidate candidate, IEnumerable<OctopusImportFillDestinationDto> destinations)
    {
        if (candidate == null) return null;

        return candidate.Kind == OctopusResourceKind.Feed
            ? (destinations ?? []).FirstOrDefault(d => d.OwnerKind == OctopusImportFillDestinationDto.FeedOwner && d.OwnerId == candidate.DestinationId)
            : (destinations ?? []).FirstOrDefault(d => d.OwnerKind != OctopusImportFillDestinationDto.FeedOwner && d.VariableSetId == candidate.DestinationId);
    }

    private static OctopusImportFillDestinationDto Destination(OctopusImportDestinationResource resource, IReadOnlySet<int> variableSetIds, IReadOnlySet<int> feedIds)
        => resource.Kind switch
        {
            OctopusResourceKind.Project when resource.VariableSetId is { } id && variableSetIds.Contains(id) => Owned(OctopusImportFillDestinationDto.ProjectOwner, resource),
            OctopusResourceKind.LibraryVariableSet when resource.VariableSetId is { } id && variableSetIds.Contains(id) => Owned(OctopusImportFillDestinationDto.LibraryVariableSetOwner, resource),
            OctopusResourceKind.Feed when feedIds.Contains(resource.Id) => new OctopusImportFillDestinationDto { OwnerKind = OctopusImportFillDestinationDto.FeedOwner, OwnerId = resource.Id, OwnerName = resource.Name },
            _ => null
        };

    private static OctopusImportFillDestinationDto Owned(string ownerKind, OctopusImportDestinationResource resource)
        => new() { OwnerKind = ownerKind, OwnerId = resource.Id, OwnerName = resource.Name, VariableSetId = resource.VariableSetId };
}

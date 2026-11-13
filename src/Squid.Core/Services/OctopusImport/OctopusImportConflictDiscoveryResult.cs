using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public enum OctopusImportIdentityMatchKind
{
    Name,
    Slug,
    NameAndSlug
}

public sealed record OctopusImportDestinationMatch(
    OctopusImportDestinationResource Destination,
    OctopusImportIdentityMatchKind MatchKind);

public sealed record OctopusImportResourceConflict(
    OctopusResourceNode Source,
    IReadOnlyList<OctopusImportDestinationMatch> Matches);

public sealed record OctopusImportConflictDiscoveryResult(
    IReadOnlyList<OctopusImportResourceConflict> Conflicts)
{
    public bool HasConflicts => Conflicts.Count > 0;

    /// <summary>Live source-map rows recorded by earlier confirmations into this space.</summary>
    public OctopusImportPriorMappings PriorMappings { get; init; } = OctopusImportPriorMappings.Empty;

    /// <summary>(project, channel, version) of the releases that already exist under prior-mapped projects.</summary>
    public IReadOnlySet<OctopusImportReleaseKey> ExistingReleaseKeys { get; init; } = new HashSet<OctopusImportReleaseKey>();

    /// <summary>The Octopus installation the incoming history came from; null for file-only imports.</summary>
    public string HistoryInstallationId { get; init; }
}

/// <summary>Squid's release identity: a version is unique per project channel.</summary>
public sealed record OctopusImportReleaseKey(int ProjectId, int ChannelId, string Version);

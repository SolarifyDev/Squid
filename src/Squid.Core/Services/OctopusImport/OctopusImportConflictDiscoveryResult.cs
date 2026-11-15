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

    /// <summary>The incoming export file's fingerprint (its id suffix); null for API bundles and unsuffixed files.</summary>
    public string ExportSourceId { get; init; }

    /// <summary>
    /// The variables (by name) Squid's imported variable sets hold now, read only for sets with incoming variables that have no live row:
    /// a variable's row goes stale whenever its set is saved in Squid, so this tells "still in Squid" from "not in Squid".
    /// </summary>
    public IReadOnlyList<OctopusImportDestinationVariable> DestinationVariables { get; init; } = [];

    /// <summary>Squid's current state of every secret an earlier import left missing (never values).</summary>
    public OctopusImportSecretSlots SecretSlots { get; init; } = OctopusImportSecretSlots.Empty;

    /// <summary>The Squid projects, library variable sets and feeds those missing secrets live in, named for preview.</summary>
    public IReadOnlyList<OctopusImportFillDestinationDto> FillDestinations { get; init; } = [];

    /// <summary>The server, space and version of the API import that recorded the space's installation (for a pairing or a refusal); null when unknown or proven.</summary>
    public OctopusImportInstallationOrigin InstallationOrigin { get; init; }

    /// <summary>The server, space and version the incoming source was read from (an Octopus server read into a space of another origin); null otherwise.</summary>
    public OctopusImportInstallationOrigin SourceOrigin { get; init; }

    /// <summary>The Squid resources of the destination space discovery matched against (projects name the destination of each project's changes).</summary>
    public IReadOnlyList<OctopusImportDestinationResource> Destinations { get; init; } = [];

    /// <summary>Squid's current values of the history rows this import would update (old side of the preview's old → new).</summary>
    public OctopusImportStoredHistory StoredHistory { get; init; } = OctopusImportStoredHistory.Empty;
}

/// <summary>Squid's release identity: a version is unique per project channel.</summary>
public sealed record OctopusImportReleaseKey(int ProjectId, int ChannelId, string Version);

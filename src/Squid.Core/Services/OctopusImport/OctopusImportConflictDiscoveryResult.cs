using Squid.Core.Services.OctopusImport.Octopus;

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
}

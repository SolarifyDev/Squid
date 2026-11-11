using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportDestinationResource(
    int Id,
    int SpaceId,
    OctopusResourceKind Kind,
    string Name,
    string Slug,
    DateTimeOffset LastModifiedDate)
{
    /// <summary>True for Squid built-in rows (only teams have them); built-in Octopus teams map onto these by identity, not by name.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Current member count of a destination team (null for every other kind). Reusing a team grants approval to these members.</summary>
    public int? MemberCount { get; init; }
}

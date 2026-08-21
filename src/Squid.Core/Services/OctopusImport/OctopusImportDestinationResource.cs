using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportDestinationResource(
    int Id,
    int SpaceId,
    OctopusResourceKind Kind,
    string Name,
    string Slug,
    DateTimeOffset LastModifiedDate);

using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ExternalFeed;

[RequiresPermission(Permission.FeedView)]
public class GetPackageNotesRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<PackageNotesQuery> Packages { get; set; } = [];
}

public record PackageNotesQuery
{
    public int FeedId { get; init; }
    public string PackageId { get; init; }
    public string Version { get; init; }
}

public class GetPackageNotesResponse : SquidResponse<GetPackageNotesResponseData>
{
}

public class GetPackageNotesResponseData
{
    public List<PackageNotesItem> Packages { get; set; } = [];
}

public class PackageNotesItem
{
    public int FeedId { get; set; }
    public string PackageId { get; set; }
    public string Version { get; set; }
    public bool Succeeded { get; set; }
    public string Notes { get; set; }
    public string FailureReason { get; set; }
    public DateTimeOffset? Published { get; set; }
}

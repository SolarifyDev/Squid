using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process;

[RequiresPermission(Permission.ProcessView)]
public class GetPackageReferencesRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ProjectId { get; set; }
    public int? ChannelId { get; set; }
}

public class GetPackageReferencesResponse : SquidResponse<GetPackageReferencesResponseData>
{
}

public class GetPackageReferencesResponseData
{
    public List<PackageReferenceItem> PackageReferences { get; set; } = new();
}

public class PackageReferenceItem
{
    public string ActionName { get; set; }
    public string PackageReferenceName { get; set; }
    public string PackageId { get; set; }
    public int FeedId { get; set; }
    public string FeedName { get; set; }
    public string LastReleaseVersion { get; set; }
}

using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Response;

namespace Squid.Message.Requests.BuiltInPackages;

[RequiresPermission(Permission.FeedView)]
public class ListBuiltInPackageVersionsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string PackageId { get; set; }
}

public class ListBuiltInPackageVersionsResponse : SquidResponse<ListBuiltInPackageVersionsResponseData>
{
}

public class ListBuiltInPackageVersionsResponseData
{
    /// <summary>Newest first.</summary>
    public List<BuiltInPackageVersionDto> Versions { get; set; }
}

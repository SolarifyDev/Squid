using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Response;

namespace Squid.Message.Requests.BuiltInPackages;

[RequiresPermission(Permission.FeedView)]
public class GetBuiltInPackageVersionRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string PackageId { get; set; }

    public string Version { get; set; }
}

public class GetBuiltInPackageVersionResponse : SquidResponse<BuiltInPackageVersionDto>
{
}

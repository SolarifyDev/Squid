using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.PackageVersioning;
using Squid.Message.Response;

namespace Squid.Message.Requests.PackageVersioning;

[RequiresPermission(Permission.FeedView)]
public class GetPackageVersionRuleRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int FeedId { get; set; }

    public string PackageId { get; set; }
}

public class GetPackageVersionRuleResponse : SquidResponse<PackageVersionRuleDto>
{
}

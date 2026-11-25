using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.PackageVersioning;
using Squid.Message.Response;

namespace Squid.Message.Requests.PackageVersioning;

[RequiresPermission(Permission.FeedView)]
public class ListPackageVersionRulesRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int FeedId { get; set; }
}

public class ListPackageVersionRulesResponse : SquidResponse<ListPackageVersionRulesResponseData>
{
}

public class ListPackageVersionRulesResponseData
{
    public List<PackageVersionRuleDto> Rules { get; set; }
}

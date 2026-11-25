using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.BuiltInPackages;
using Squid.Message.Response;

namespace Squid.Message.Requests.BuiltInPackages;

[RequiresPermission(Permission.FeedView)]
public class ListBuiltInPackagesRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    /// <summary>Only packages whose id contains this text (ignoring case).</summary>
    public string Filter { get; set; }

    public int Take { get; set; } = 30;
}

public class ListBuiltInPackagesResponse : SquidResponse<ListBuiltInPackagesResponseData>
{
}

public class ListBuiltInPackagesResponseData
{
    public int FeedId { get; set; }

    public List<BuiltInPackageDto> Packages { get; set; }
}

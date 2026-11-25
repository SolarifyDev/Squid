using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.PackageVersioning;
using Squid.Message.Response;

namespace Squid.Message.Commands.PackageVersioning;

[RequiresPermission(Permission.FeedEdit)]
public class SetPackageVersionRuleCommand : ICommand, ISpaceScoped
{
    public int SpaceId { get; set; }
    int? ISpaceScoped.SpaceId => SpaceId;

    public int FeedId { get; set; }

    public string PackageId { get; set; }

    public string VersionTemplate { get; set; }
}

public class SetPackageVersionRuleResponse : SquidResponse<PackageVersionRuleDto>
{
}

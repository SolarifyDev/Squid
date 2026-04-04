using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Release;

[RequiresPermission(Permission.ReleaseView)]
public class GetReleaseDetailRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ReleaseId { get; set; }
}

public class GetReleaseDetailResponse : SquidResponse<ReleaseDetailDto>
{
}

public class ReleaseDetailDto
{
    public int Id { get; set; }
    public string Version { get; set; }
    public int ProjectId { get; set; }
    public int ChannelId { get; set; }
    public int SpaceId { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public List<SelectedPackageDto> SelectedPackages { get; set; }
}

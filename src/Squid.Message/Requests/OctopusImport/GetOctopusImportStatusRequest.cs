using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Requests.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class GetOctopusImportStatusRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }
}

public class GetOctopusImportStatusResponse : SquidResponse<GetOctopusImportStatusResponseData>
{
}

public class GetOctopusImportStatusResponseData
{
    public OctopusImportSessionDto Session { get; set; }
}

using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Requests.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class GetOctopusImportPreviewRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }
}

public class GetOctopusImportPreviewResponse : SquidResponse<GetOctopusImportPreviewResponseData>
{
}

public class GetOctopusImportPreviewResponseData
{
    public OctopusImportSessionDto Session { get; set; }

    public OctopusImportPreviewPlanDto PreviewPlan { get; set; }
}

using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Requests.OctopusImport;

/// <summary>The current user's import sessions in the space that can still be continued, newest first.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class ListOctopusImportSessionsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }
}

public class ListOctopusImportSessionsResponse : SquidResponse<List<OctopusImportResumableSessionDto>>
{
}

using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Requests.OctopusImport;

/// <summary>The Octopus projects a session's source holds, with the session's current selection.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class GetOctopusImportProjectsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }
}

public class GetOctopusImportProjectsResponse : SquidResponse<OctopusImportProjectListDto>
{
}

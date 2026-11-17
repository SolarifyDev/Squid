using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Requests.OctopusImport;

/// <summary>Lists an Octopus space's projects and project groups (no per-project request), so the user can choose what to import before anything is read. Creates no session.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class ListOctopusServerProjectsRequest : IRequest, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string ServerUrl { get; set; }

    public string ApiKey { get; set; }

    /// <summary>The Octopus space whose projects are listed; the server's default space when empty.</summary>
    public string OctopusSpaceId { get; set; }
}

public class ListOctopusServerProjectsResponse : SquidResponse<OctopusImportProjectListDto>
{
}

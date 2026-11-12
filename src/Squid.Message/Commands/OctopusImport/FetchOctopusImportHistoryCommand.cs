using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>Fetches the next chunk of deployment history from an Octopus server into an Uploaded session.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class FetchOctopusImportHistoryCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }

    public string ServerUrl { get; set; }

    public string ApiKey { get; set; }

    public string OctopusSpaceId { get; set; }
}

public class FetchOctopusImportHistoryResponse : SquidResponse<OctopusHistoryFetchProgressDto>
{
}

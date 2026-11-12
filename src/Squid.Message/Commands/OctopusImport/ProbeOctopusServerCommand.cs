using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>Checks that a URL and API key reach an Octopus server and lists its spaces. Creates no session.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class ProbeOctopusServerCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string ServerUrl { get; set; }

    public string ApiKey { get; set; }
}

public class ProbeOctopusServerResponse : SquidResponse<OctopusServerProbeDto>
{
}

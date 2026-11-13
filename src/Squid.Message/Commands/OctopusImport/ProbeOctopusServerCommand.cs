using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>Checks that a URL and API key reach an Octopus server, lists its spaces and reports whether the key can read a whole space. Creates no session.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class ProbeOctopusServerCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string ServerUrl { get; set; }

    public string ApiKey { get; set; }

    /// <summary>The Octopus space whose permissions the probe reports; the server's default space when empty.</summary>
    public string OctopusSpaceId { get; set; }
}

public class ProbeOctopusServerResponse : SquidResponse<OctopusServerProbeDto>
{
}

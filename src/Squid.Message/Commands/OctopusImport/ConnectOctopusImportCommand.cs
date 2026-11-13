using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>Creates an import session whose only source is an Octopus server's REST API.</summary>
[RequiresPermission(Permission.ProjectCreate)]
public class ConnectOctopusImportCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    /// <summary>
    /// The Octopus server and key the session will read from. When given, the key is checked before the session exists: a
    /// key that cannot read the whole space is refused and no session is created. When omitted, the first fetch checks it.
    /// </summary>
    public string ServerUrl { get; set; }

    public string ApiKey { get; set; }

    public string OctopusSpaceId { get; set; }
}

public class ConnectOctopusImportResponse : SquidResponse<ConnectOctopusImportResponseData>
{
}

public class ConnectOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }
}

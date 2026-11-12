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
}

public class ConnectOctopusImportResponse : SquidResponse<ConnectOctopusImportResponseData>
{
}

public class ConnectOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }
}

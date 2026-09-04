using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class ConfirmOctopusImportCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }
}

public class ConfirmOctopusImportResponse : SquidResponse<ConfirmOctopusImportResponseData>
{
}

public class ConfirmOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }
}

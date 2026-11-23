using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

[RequiresPermission(Permission.RunbookEdit)]
public class CopyRunbookCommand : ICommand, ISpaceScoped
{
    public int SourceRunbookId { get; set; }

    public int ProjectId { get; set; }

    public int? SpaceId { get; set; }

    public string Name { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class CopyRunbookResponse : SquidResponse<RunbookDto>
{
}

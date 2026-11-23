using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Runbook;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

[RequiresPermission(Permission.RunbookPublish)]
public class PublishRunbookCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public int? SpaceId { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class PublishRunbookResponse : SquidResponse<RunbookPublishedSnapshotDto>
{
}

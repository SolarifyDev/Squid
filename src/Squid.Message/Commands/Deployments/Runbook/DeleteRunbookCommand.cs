using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Runbook;

public class DeleteRunbookCommand : ICommand, ISpaceScoped
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int SpaceId { get; set; }

    int? ISpaceScoped.SpaceId => SpaceId;
}

public class DeleteRunbookResponse : SquidResponse<DeleteRunbookResponseData>
{
}

public class DeleteRunbookResponseData
{
    public int Id { get; set; }
}

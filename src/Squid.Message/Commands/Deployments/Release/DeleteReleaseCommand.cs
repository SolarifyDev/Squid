using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Release;

public class DeleteReleaseCommand : ICommand
{
    public int ReleaseId { get; set; }
}

public class DeleteReleaseResponse : SquidResponse
{
}

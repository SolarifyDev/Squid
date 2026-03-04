using Squid.Message.Models.Deployments.Release;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Release;

public class UpdateReleaseCommand : ICommand
{
    public int Id { get; set; }
    public UpdateReleaseModel Release { get; set; }
}

public class UpdateReleaseResponse : SquidResponse<ReleaseDto>
{
}
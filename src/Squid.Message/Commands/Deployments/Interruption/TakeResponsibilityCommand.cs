using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Interruption;

public class TakeResponsibilityCommand : ICommand
{
    public int InterruptionId { get; set; }
    public string UserId { get; set; }
}

public class TakeResponsibilityResponse : SquidResponse
{
}

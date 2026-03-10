using Squid.Message.Enums.Deployments;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Interruption;

public class ResolveInterruptionCommand : ICommand
{
    public int InterruptionId { get; set; }

    public GuidedFailureAction Action { get; set; }
}

public class ResolveInterruptionResponse : SquidResponse
{
}

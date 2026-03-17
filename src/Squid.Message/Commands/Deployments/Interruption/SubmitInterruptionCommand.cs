using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Interruption;

[RequiresPermission(Permission.InterruptionSubmit)]
public class SubmitInterruptionCommand : ICommand
{
    public int InterruptionId { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();
}

public class SubmitInterruptionResponse : SquidResponse
{
}

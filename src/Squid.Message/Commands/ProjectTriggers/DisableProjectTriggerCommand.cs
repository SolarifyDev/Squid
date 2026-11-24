using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Commands.ProjectTriggers;

public sealed class DisableProjectTriggerCommand : ProjectTriggerRevisionCommand
{
}

public sealed class DisableProjectTriggerResponse : SquidResponse<ProjectTriggerChangeDto>
{
}

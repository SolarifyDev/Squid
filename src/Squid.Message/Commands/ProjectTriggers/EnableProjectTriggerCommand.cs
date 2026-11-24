using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Commands.ProjectTriggers;

public sealed class EnableProjectTriggerCommand : ProjectTriggerRevisionCommand
{
}

public sealed class EnableProjectTriggerResponse : SquidResponse<ProjectTriggerChangeDto>
{
}

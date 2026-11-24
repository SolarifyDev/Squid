using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Commands.ProjectTriggers;

public sealed class DeleteProjectTriggerCommand : ProjectTriggerRevisionCommand
{
}

public sealed class DeleteProjectTriggerResponse : SquidResponse<ProjectTriggerChangeDto>
{
}

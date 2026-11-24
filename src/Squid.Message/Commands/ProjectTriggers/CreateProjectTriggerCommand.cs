using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Commands.ProjectTriggers;

public sealed class CreateProjectTriggerCommand : ProjectTriggerWriteCommand
{
    public bool IsDisabled { get; set; }
}

public sealed class CreateProjectTriggerResponse : SquidResponse<ProjectTriggerChangeDto>
{
}

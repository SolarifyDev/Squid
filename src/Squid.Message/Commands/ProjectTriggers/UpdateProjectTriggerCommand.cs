using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Commands.ProjectTriggers;

public sealed class UpdateProjectTriggerCommand : ProjectTriggerWriteCommand
{
    public int TriggerId { get; set; }

    public int ExpectedRevision { get; set; }
}

public sealed class UpdateProjectTriggerResponse : SquidResponse<ProjectTriggerChangeDto>
{
}

using Squid.Message.Models.ProjectTriggers;

namespace Squid.Message.Commands.ProjectTriggers;

public abstract class ProjectTriggerRevisionCommand : ProjectTriggerProjectScope, ICommand
{
    public int TriggerId { get; set; }

    public int ExpectedRevision { get; set; }
}

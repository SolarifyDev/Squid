using System.Text.Json;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Message.Commands.ProjectTriggers;

// All permissions are checked against the persisted project in the service. SpaceId can be omitted.
public abstract class ProjectTriggerWriteCommand : ProjectTriggerProjectScope, ICommand
{
    public string Name { get; set; }

    public string Description { get; set; }

    public string TriggerType { get; set; }

    public string ActionType { get; set; }

    public JsonElement Configuration { get; set; }
}

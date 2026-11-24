using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Requests.ProjectTriggers;

public sealed class GetProjectTriggerRequest : ProjectTriggerProjectScope, IRequest
{
    public int TriggerId { get; set; }
}

public sealed class GetProjectTriggerResponse : SquidResponse<ProjectTriggerDto>
{
}

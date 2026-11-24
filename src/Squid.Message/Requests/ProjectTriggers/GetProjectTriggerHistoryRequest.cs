using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Requests.ProjectTriggers;

public sealed class GetProjectTriggerHistoryRequest : ProjectTriggerPageRequest
{
    public int TriggerId { get; set; }
}

public sealed class GetProjectTriggerHistoryResponse : SquidResponse<ProjectTriggerPage<ProjectTriggerRunDto>>
{
}

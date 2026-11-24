using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Requests.ProjectTriggers;

public sealed class GetProjectTriggersRequest : ProjectTriggerPageRequest
{
}

public sealed class GetProjectTriggersResponse : SquidResponse<ProjectTriggerPage<ProjectTriggerSummaryDto>>
{
}

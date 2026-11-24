using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Response;

namespace Squid.Message.Requests.ProjectTriggers;

public sealed class GetProjectTriggerTypesRequest : ProjectTriggerProjectScope, IRequest
{
}

public sealed class GetProjectTriggerTypesResponse : SquidResponse<IReadOnlyList<ProjectTriggerCapability>>
{
}

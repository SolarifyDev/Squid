using Squid.Message.Models.ProjectTriggers;

namespace Squid.Message.Requests.ProjectTriggers;

public abstract class ProjectTriggerPageRequest : ProjectTriggerProjectScope, IPaginatedRequest
{
    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;
}

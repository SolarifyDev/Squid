using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ServerTask;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ServerTask;

[RequiresPermission(Permission.TaskView)]
public class GetServerTaskListRequest : IPaginatedRequest
{
    public int ProjectId { get; set; }

    public string State { get; set; }

    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 30;
}

public class GetServerTaskListResponse : SquidResponse<GetServerTaskListResponseData>;

public class GetServerTaskListResponseData
{
    public int TotalCount { get; set; }

    public List<ServerTaskSummaryDto> Items { get; set; } = [];
}

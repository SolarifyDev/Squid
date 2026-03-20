using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ServerTask;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ServerTask;

[RequiresPermission(Permission.TaskView)]
public class GetServerTaskRequest : IRequest
{
    public int TaskId { get; set; }
}

public class GetServerTaskResponse : SquidResponse<ServerTaskSummaryDto>;

[RequiresPermission(Permission.TaskView)]
public class GetServerTaskDetailsRequest : IRequest
{
    public int TaskId { get; set; }

    public bool? Verbose { get; set; }

    public int? Tail { get; set; }
}

public class GetServerTaskDetailsResponse : SquidResponse<ServerTaskDetailsDto>;

[RequiresPermission(Permission.TaskView)]
public class GetServerTaskLogsRequest : IRequest
{
    public int TaskId { get; set; }

    public long? AfterSequenceNumber { get; set; }

    public int? Take { get; set; }
}

public class GetServerTaskLogsResponse : SquidResponse<ServerTaskLogPageDto>;

[RequiresPermission(Permission.TaskView)]
public class GetServerTaskNodeLogsRequest : IRequest
{
    public int TaskId { get; set; }

    public long NodeId { get; set; }

    public long? AfterSequenceNumber { get; set; }

    public int? Take { get; set; }
}

public class GetServerTaskNodeLogsResponse : SquidResponse<ServerTaskLogPageDto>;

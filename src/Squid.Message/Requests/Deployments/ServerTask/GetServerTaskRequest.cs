using Squid.Message.Models.Deployments.ServerTask;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.ServerTask;

public class GetServerTaskRequest : IRequest
{
    public int TaskId { get; set; }
}

public class GetServerTaskResponse : SquidResponse<ServerTaskSummaryDto>;

public class GetServerTaskDetailsRequest : IRequest
{
    public int TaskId { get; set; }

    public bool? Verbose { get; set; }

    public int? Tail { get; set; }
}

public class GetServerTaskDetailsResponse : SquidResponse<ServerTaskDetailsDto>;

public class GetServerTaskLogsRequest : IRequest
{
    public int TaskId { get; set; }

    public long? AfterSequenceNumber { get; set; }

    public int? Take { get; set; }
}

public class GetServerTaskLogsResponse : SquidResponse<ServerTaskLogPageDto>;

public class GetServerTaskNodeLogsRequest : IRequest
{
    public int TaskId { get; set; }

    public long NodeId { get; set; }

    public long? AfterSequenceNumber { get; set; }

    public int? Take { get; set; }
}

public class GetServerTaskNodeLogsResponse : SquidResponse<ServerTaskLogPageDto>;

using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ServerTask;

/// <summary>
/// System-triggered sweep: recovers every Executing/Cancelling task whose owning process stopped renewing its lease (it died).
/// Not exposed via a controller.
/// </summary>
public class ReapStaleServerTasksCommand : ICommand
{
}

public class ReapStaleServerTasksResponse : SquidResponse<ReapStaleServerTasksResponseData>
{
}

public class ReapStaleServerTasksResponseData
{
    public int Recovered { get; set; }
}

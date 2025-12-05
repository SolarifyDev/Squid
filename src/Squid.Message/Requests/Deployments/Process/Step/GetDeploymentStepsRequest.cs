using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process.Step;

public class GetDeploymentStepsRequest : IPaginatedRequest
{
    public int ProcessId { get; set; }

    public int PageIndex { get; set; }

    public int PageSize { get; set; }

    public string Keyword { get; set; }
}

public class GetDeploymentStepsResponse : SquidResponse<GetDeploymentStepsResponseData>
{
}

public class GetDeploymentStepsResponseData
{
    public int Count { get; set; }

    public List<DeploymentStepDto> Steps { get; set; }
}


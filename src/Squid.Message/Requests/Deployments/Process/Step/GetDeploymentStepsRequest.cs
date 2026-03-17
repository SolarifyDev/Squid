using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Process.Step;

[RequiresPermission(Permission.ProcessView)]
public class GetDeploymentStepsRequest : IPaginatedRequest
{
    public int ProcessId { get; set; }

    public int PageIndex { get; set; } = 1;

    public int PageSize { get; set; } = 20;

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


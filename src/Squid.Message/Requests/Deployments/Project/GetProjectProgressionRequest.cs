using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

public class GetProjectProgressionRequest : IRequest
{
    public int ProjectId { get; set; }
}

public class GetProjectProgressionResponse : SquidResponse<ProjectProgressionDto>
{
}

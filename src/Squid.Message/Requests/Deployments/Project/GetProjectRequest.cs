using Squid.Message.Models.Deployments.Project;
using Squid.Message.Response;

namespace Squid.Message.Requests.Deployments.Project;

public class GetProjectRequest : IRequest
{
    public int Id { get; set; }
}

public class GetProjectResponse : SquidResponse<ProjectDto>
{
}


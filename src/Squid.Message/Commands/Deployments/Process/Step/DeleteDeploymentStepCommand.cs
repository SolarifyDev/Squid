using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Process.Step;

public class DeleteDeploymentStepCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public List<int> Ids { get; set; } = new List<int>();
    public int? ProjectId { get; set; }
    public int? RunbookId { get; set; }
}

public class DeleteDeploymentStepResponse : SquidResponse<DeleteDeploymentStepResponseData>
{
}

public class DeleteDeploymentStepResponseData
{
    public List<int> FailIds { get; set; } = new List<int>();
}

using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Deployment;

public class CreateDeploymentCommand : ICommand
{
    public int ReleaseId { get; set; }

    public int EnvironmentId { get; set; }

    public string Name { get; set; }

    public int DeployedBy { get; set; }

    public string Comments { get; set; }

    public bool ForcePackageDownload { get; set; }

    public bool UseGuidedFailure { get; set; }

    public Dictionary<string, string> FormValues { get; set; } = new();

    public List<string> SpecificMachineIds { get; set; } = new();

    public List<string> ExcludedMachineIds { get; set; } = new();
}

public class CreateDeploymentResponse : SquidResponse<CreateDeploymentResponseData>
{
}

public class CreateDeploymentResponseData
{
    public DeploymentDto Deployment { get; set; }

    public int TaskId { get; set; }
}

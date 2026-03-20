using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Deployment;

[RequiresPermission(Permission.DeploymentCreate)]
public class CreateDeploymentCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }
    public int ReleaseId { get; set; }

    public int EnvironmentId { get; set; }

    public string Name { get; set; }

    public string Comments { get; set; }

    public bool ForcePackageDownload { get; set; }

    public bool ForcePackageRedeployment { get; set; }

    public bool UseGuidedFailure { get; set; }

    public DateTimeOffset? QueueTime { get; set; }

    public DateTimeOffset? QueueTimeExpiry { get; set; }

    public Dictionary<string, string> FormValues { get; set; } = new();

    public List<string> SpecificMachineIds { get; set; } = new();

    public List<string> ExcludedMachineIds { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> SkipActionIds { get; set; } = new();
}

public class CreateDeploymentResponse : SquidResponse<CreateDeploymentResponseData>
{
}

public class CreateDeploymentResponseData
{
    public DeploymentDto Deployment { get; set; }

    public int TaskId { get; set; }
}

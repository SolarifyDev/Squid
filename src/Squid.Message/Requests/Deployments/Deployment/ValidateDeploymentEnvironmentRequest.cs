using System.Text.Json.Serialization;

namespace Squid.Message.Requests.Deployments.Deployment;

public class ValidateDeploymentEnvironmentRequest : IRequest
{
    public int ReleaseId { get; set; }

    public int EnvironmentId { get; set; }

    public DateTimeOffset? QueueTime { get; set; }

    public DateTimeOffset? QueueTimeExpiry { get; set; }

    public List<string> SpecificMachineIds { get; set; } = new();

    public List<string> ExcludedMachineIds { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> SkipActionIds { get; set; } = new();
}

public class ValidateDeploymentEnvironmentResponse : Models.Deployments.Deployment.DeploymentEnvironmentValidationResult, IResponse;

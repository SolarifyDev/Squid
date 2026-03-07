using System.Text.Json.Serialization;

namespace Squid.Message.Models.Deployments.Deployment;

public class DeploymentRequestPayload
{
    public string Comments { get; set; }

    public bool ForcePackageDownload { get; set; }

    public bool ForcePackageRedeployment { get; set; }

    public bool UseGuidedFailure { get; set; }

    public DateTimeOffset? QueueTime { get; set; }

    public DateTimeOffset? QueueTimeExpiry { get; set; }

    public Dictionary<string, string> FormValues { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> SpecificMachineIds { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> ExcludedMachineIds { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> SkipActionIds { get; set; } = new();
}

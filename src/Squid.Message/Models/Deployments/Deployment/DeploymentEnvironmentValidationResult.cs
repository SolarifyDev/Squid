namespace Squid.Message.Models.Deployments.Deployment;

public class DeploymentEnvironmentValidationResult
{
    public bool IsValid { get; set; }

    public List<string> Reasons { get; set; } = new();

    public int AvailableMachineCount { get; set; }

    public int? LifecycleId { get; set; }

    public List<int> AllowedEnvironmentIds { get; set; } = new();

    public string Message => IsValid
        ? "Environment validation passed."
        : string.Join("; ", Reasons);
}

namespace Squid.Core.Services.Deployments.Validation;

public sealed class DeploymentValidationContext
{
    public int ReleaseId { get; init; }

    public int EnvironmentId { get; init; }

    public int? ChannelId { get; init; }

    public DateTimeOffset? QueueTime { get; init; }

    public DateTimeOffset? QueueTimeExpiry { get; init; }

    public HashSet<int> SpecificMachineIds { get; init; } = new();

    public HashSet<int> ExcludedMachineIds { get; init; } = new();

    public HashSet<int> SkipActionIds { get; init; } = new();
}

using System.Text.Json;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.Core.Services.ProjectTriggers.Actions;

public sealed record SuccessfulTriggerDeployment(int DeploymentId, int ReleaseId, string Version, int ChannelId,
    DateTimeOffset CompletedTime, int ProcessSnapshotId, int VariableSetSnapshotId, string RequestJson);

public interface ITriggerDeploymentHistory : IScopedDependency
{
    /// <summary>Find the environment's latest successful deployment, excluding partial target-trigger catch-ups. Returns null for empty or out-of-scope history.</summary>
    Task<SuccessfulTriggerDeployment> FindLatestSuccessfulAsync(
        int spaceId,
        int projectId,
        int environmentId,
        int? channelId = null,
        CancellationToken ct = default);
}

public sealed class TriggerDeploymentHistory : ITriggerDeploymentHistory
{
    private readonly ITriggerActionDataProvider _dataProvider;

    public TriggerDeploymentHistory(ITriggerActionDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    public Task<SuccessfulTriggerDeployment> FindLatestSuccessfulAsync(
        int spaceId, int projectId, int environmentId, int? channelId = null, CancellationToken ct = default)
    {
        return FindSuccessfulAsync(spaceId, projectId, environmentId, channelId, null, ct);
    }

    internal async Task<SuccessfulTriggerDeployment> FindSuccessfulAsync(
        int spaceId, int projectId, int environmentId, int? channelId, int? deploymentId, CancellationToken ct)
    {
        var candidates = _dataProvider.ReadSuccessfulDeploymentsAsync(
            spaceId, projectId, environmentId, channelId, deploymentId);

        // Older requests are stored as text. Stop streaming at the first complete deployment.
        await foreach (var candidate in candidates.WithCancellation(ct).ConfigureAwait(false))
        {
            if (CoversEnvironment(candidate.RequestJson)) return candidate;
        }

        return null;
    }

    private static bool CoversEnvironment(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            var request = JsonSerializer.Deserialize<DeploymentRequestPayload>(json, SquidJsonDefaults.CaseInsensitive);

            return request != null && !(request.SpecificMachineIds?.Count > 0)
                && !(request.ExcludedMachineIds?.Count > 0);
        }
        catch (JsonException)
        {
            // A malformed legacy selector cannot establish an environment-wide baseline.
            return false;
        }
    }
}

using System.Text.Json;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Release;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.Core.Services.Deployments.Validation.Rules;

public sealed class FeedAvailabilityValidationRule : IDeploymentValidationRule
{
    private readonly IReleaseDataProvider _releaseDataProvider;
    private readonly IDeploymentSnapshotService _deploymentSnapshotService;
    private readonly IExternalFeedDataProvider _externalFeedDataProvider;

    public FeedAvailabilityValidationRule(
        IReleaseDataProvider releaseDataProvider,
        IDeploymentSnapshotService deploymentSnapshotService,
        IExternalFeedDataProvider externalFeedDataProvider)
    {
        _releaseDataProvider = releaseDataProvider;
        _deploymentSnapshotService = deploymentSnapshotService;
        _externalFeedDataProvider = externalFeedDataProvider;
    }

    public int Order => 500;

    public bool Supports(DeploymentValidationStage stage) =>
        stage == DeploymentValidationStage.Precheck || stage == DeploymentValidationStage.Create;

    public async Task EvaluateAsync(DeploymentValidationContext context, DeploymentValidationReport report, CancellationToken cancellationToken = default)
    {
        var release = await _releaseDataProvider
            .GetReleaseByIdAsync(context.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null || release.ProjectDeploymentProcessSnapshotId <= 0)
            return;

        var processSnapshot = await _deploymentSnapshotService
            .LoadProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, cancellationToken).ConfigureAwait(false);

        var steps = ProcessSnapshotStepConverter.Convert(processSnapshot);
        var runnableActions = steps
            .Where(step => !step.IsDisabled)
            .SelectMany(step => step.Actions)
            .Where(action => StepEligibilityEvaluator.ShouldExecuteAction(action, context.EnvironmentId, release.ChannelId))
            .Where(action => !context.SkipActionIds.Contains(action.Id))
            .ToList();

        if (runnableActions.Count == 0)
            return;

        var requiredFeedIds = CollectFeedIds(runnableActions);

        if (requiredFeedIds.Count == 0)
            return;

        var feeds = await _externalFeedDataProvider
            .GetExternalFeedsByIdsAsync(requiredFeedIds.ToList(), cancellationToken).ConfigureAwait(false);

        var existingFeedIds = feeds.Select(f => f.Id).ToHashSet();
        var missingFeedIds = requiredFeedIds
            .Where(id => !existingFeedIds.Contains(id))
            .OrderBy(id => id)
            .ToList();

        if (missingFeedIds.Count == 0)
            return;

        report.AddBlockingIssue(DeploymentValidationIssueCode.FeedNotFound, $"Referenced feeds do not exist: {string.Join(", ", missingFeedIds)}.");
    }

    private static HashSet<int> CollectFeedIds(IEnumerable<Squid.Message.Models.Deployments.Process.DeploymentActionDto> actions)
    {
        var feedIds = new HashSet<int>();

        foreach (var action in actions)
        {
            var containersProp = action.Properties
                .FirstOrDefault(p => string.Equals(p.PropertyName, KubernetesProperties.Containers, StringComparison.Ordinal));

            if (containersProp == null || string.IsNullOrWhiteSpace(containersProp.PropertyValue))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(containersProp.PropertyValue);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var container in doc.RootElement.EnumerateArray())
                {
                    if (!container.TryGetProperty(KubernetesContainerPayloadProperties.FeedId, out var feedProp))
                        continue;

                    if (TryGetFeedId(feedProp, out var feedId) && feedId > 0)
                        feedIds.Add(feedId);
                }
            }
            catch
            {
                // Ignore malformed container payloads here; action execution will surface detailed parse errors.
            }
        }

        return feedIds;
    }

    private static bool TryGetFeedId(JsonElement value, out int feedId)
    {
        feedId = 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out feedId),
            JsonValueKind.String => int.TryParse(value.GetString(), out feedId),
            _ => false
        };
    }
}

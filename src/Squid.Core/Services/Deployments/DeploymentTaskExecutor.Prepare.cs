using System.Text.Json;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task LoadTaskAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await _serverTaskDataProvider.GetServerTaskByIdAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null)
            throw new InvalidOperationException($"ServerTask {serverTaskId} not found");

        task.State = "Running";
        task.StartTime = DateTimeOffset.UtcNow;
        await _serverTaskDataProvider.UpdateServerTaskStateAsync(task.Id, "Running", cancellationToken: ct).ConfigureAwait(false);

        _ctx.Task = task;

        Log.Information("Start processing task {TaskId}", serverTaskId);
    }

    private async Task LoadDeploymentAsync(CancellationToken ct)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(_ctx.Task.Id, ct).ConfigureAwait(false);

        if (deployment == null)
            throw new InvalidOperationException($"No deployment found for task {_ctx.Task.Id}");

        _ctx.Deployment = deployment;

        var release = await _releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, ct).ConfigureAwait(false);
        _ctx.Release = release;
    }

    private async Task GeneratePlanAsync(CancellationToken ct)
    {
        Log.Information("Generating deployment plan for deployment {DeploymentId}", _ctx.Deployment.Id);

        _ctx.Plan = await _planService.GeneratePlanAsync(_ctx.Deployment.Id, ct).ConfigureAwait(false);
    }

    private async Task ResolveVariablesAsync(CancellationToken ct)
    {
        Log.Information("Resolving variables for deployment {DeploymentId}", _ctx.Deployment.Id);

        _ctx.Variables = await _variableResolver.ResolveVariablesAsync(_ctx.Deployment.Id, ct).ConfigureAwait(false);
    }

    private async Task FindTargetsAsync(CancellationToken ct)
    {
        Log.Information("Finding targets for deployment {DeploymentId}", _ctx.Deployment.Id);

        _ctx.Targets = await _targetFinder.FindTargetsAsync(_ctx.Deployment, ct).ConfigureAwait(false);

        if (_ctx.Targets.Count == 0)
            throw new InvalidOperationException($"No target machines found for deployment {_ctx.Deployment.Id}");

        Log.Information("Found {Count} target machines for deployment {DeploymentId}",
            _ctx.Targets.Count, _ctx.Deployment.Id);
    }

    private async Task LoadAccountAsync(CancellationToken ct)
    {
        _ctx.EndpointJson = _ctx.Target.Endpoint;
        _ctx.CommunicationStyle = ParseCommunicationStyle(_ctx.EndpointJson);

        _resolvedContributor = _variableContributors.FirstOrDefault(
            c => c.CanHandle(_ctx.CommunicationStyle));

        if (_resolvedContributor != null)
        {
            var accountId = _resolvedContributor.ParseAccountId(_ctx.EndpointJson);
            if (accountId.HasValue)
                _ctx.Account = await _deploymentAccountDataProvider
                    .GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task ContributeEndpointVariablesAsync(CancellationToken ct)
    {
        if (_resolvedContributor != null)
        {
            var endpointVars = _resolvedContributor.ContributeVariables(_ctx.EndpointJson, _ctx.Account);
            _ctx.Variables.AddRange(endpointVars);
        }

        await InsertContainerImageVariableAsync(ct).ConfigureAwait(false);
    }

    private static string ParseCommunicationStyle(string endpointJson)
    {
        if (string.IsNullOrEmpty(endpointJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(endpointJson);
            if (doc.RootElement.TryGetProperty("CommunicationStyle", out var prop))
                return prop.GetString();
            if (doc.RootElement.TryGetProperty("communicationStyle", out var prop2))
                return prop2.GetString();
            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task InsertContainerImageVariableAsync(CancellationToken ct)
    {
        try
        {
            var containerImage = await BuildContainerImageAsync(_ctx.Plan, _ctx.Release, ct).ConfigureAwait(false);
            _ctx.Variables.Add(MakeVariable("ContainerImage", containerImage));
        }
        catch
        {
            _ctx.Variables.Add(MakeVariable("ContainerImage", _ctx.Release?.Version ?? string.Empty));
        }
    }

    private void ConvertSnapshotToSteps()
    {
        _ctx.Steps = ConvertProcessSnapshotToSteps(_ctx.Plan.ProcessSnapshot);
    }

    private static VariableDto MakeVariable(string name, string value) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = VariableType.String,
        IsSensitive = false,
        LastModifiedOn = DateTimeOffset.UtcNow,
        LastModifiedBy = "System"
    };

    private async Task<string> BuildContainerImageAsync(DeploymentPlanDto plan, Persistence.Entities.Deployments.Release release, CancellationToken ct)
    {
        var firstAction = plan.ProcessSnapshot?.Data.StepSnapshots?
            .SelectMany(s => s.ActionSnapshots)
            .FirstOrDefault(a => a.FeedId.HasValue && !string.IsNullOrEmpty(a.PackageId));

        if (firstAction == null) return release.Version;

        var feed = await _externalFeedDataProvider.GetFeedByIdAsync(firstAction.FeedId.Value, ct).ConfigureAwait(false);

        if (feed == null) return release.Version;

        var uri = new Uri(feed.FeedUri ?? string.Empty);

        return $"{uri.Host}/{firstAction.PackageId ?? string.Empty}:{release.Version ?? string.Empty}";
    }

    private List<DeploymentStepDto> ConvertProcessSnapshotToSteps(DeploymentProcessSnapshotDto processSnapshot)
    {
        var steps = new List<DeploymentStepDto>();

        foreach (var stepSnap in processSnapshot.Data.StepSnapshots.OrderBy(p => p.StepOrder))
        {
            var step = new DeploymentStepDto
            {
                Id = stepSnap.Id,
                ProcessId = processSnapshot.Id,
                StepOrder = stepSnap.StepOrder,
                Name = stepSnap.Name,
                StepType = stepSnap.StepType,
                Condition = stepSnap.Condition,
                StartTrigger = "",
                PackageRequirement = "",
                IsDisabled = false,
                IsRequired = true,
                CreatedAt = stepSnap.CreatedAt,
                Properties = stepSnap.Properties.Select(
                    kvp =>
                        new DeploymentStepPropertyDto
                        {
                            StepId = stepSnap.Id, PropertyName = kvp.Key, PropertyValue = kvp.Value
                        }).ToList(),
                Actions = stepSnap.ActionSnapshots.Select(
                    action =>
                        new DeploymentActionDto
                        {
                            Id = action.Id,
                            StepId = stepSnap.Id,
                            ActionOrder = action.ActionOrder,
                            Name = action.Name,
                            ActionType = action.ActionType,
                            WorkerPoolId = action.WorkerPoolId,
                            IsDisabled = action.IsDisabled,
                            IsRequired = action.IsRequired,
                            CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
                            CreatedAt = action.CreatedAt,
                            Properties = action.Properties.Select(
                                kvp =>
                                    new DeploymentActionPropertyDto
                                    {
                                        ActionId = action.Id, PropertyName = kvp.Key, PropertyValue = kvp.Value
                                    }).ToList()
                        }).ToList()
            };

            steps.Add(step);
        }

        return steps;
    }
}

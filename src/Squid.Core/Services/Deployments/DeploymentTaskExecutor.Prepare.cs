using System.Text.Json;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Exceptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task LoadDeploymentDataAsync(int serverTaskId, CancellationToken ct)
    {
        await LoadTaskAsync(serverTaskId, ct).ConfigureAwait(false);
        await LoadDeploymentAsync(ct).ConfigureAwait(false);
        await LoadOrSnapshotAsync(ct).ConfigureAwait(false);
        await ResolveVariablesAsync(ct).ConfigureAwait(false);
        await FindTargetsAsync(ct).ConfigureAwait(false);
        ConvertSnapshotToSteps();
        PreFilterTargetsByRoles();
    }

    private async Task LoadTargetDataAsync(CancellationToken ct)
    {
        await LoadAccountAsync(ct).ConfigureAwait(false);
        await ContributeEndpointVariablesAsync(ct).ConfigureAwait(false);
    }

    private async Task LoadTaskAsync(int serverTaskId, CancellationToken ct)
    {
        var task = await _serverTaskDataProvider.GetServerTaskByIdAsync(serverTaskId, ct).ConfigureAwait(false);

        if (task == null)
            throw new DeploymentEntityNotFoundException("ServerTask", serverTaskId);

        task.State = TaskState.Executing;
        task.StartTime = DateTimeOffset.UtcNow;
        await _serverTaskDataProvider.TransitionStateAsync(task.Id, TaskState.Pending, TaskState.Executing, ct).ConfigureAwait(false);

        _ctx.Task = task;

        Log.Information("Start processing task {TaskId}", serverTaskId);
    }

    private async Task LoadDeploymentAsync(CancellationToken ct)
    {
        var deployment = await _deploymentDataProvider.GetDeploymentByTaskIdAsync(_ctx.Task.Id, ct).ConfigureAwait(false);

        if (deployment == null)
            throw new DeploymentEntityNotFoundException("Deployment", $"task:{_ctx.Task.Id}");

        _ctx.Deployment = deployment;

        var release = await _releaseDataProvider.GetReleaseByIdAsync(deployment.ReleaseId, ct).ConfigureAwait(false);
        _ctx.Release = release;
    }

    private async Task LoadOrSnapshotAsync(CancellationToken ct)
    {
        Log.Information("Loading process snapshot for deployment {DeploymentId}", _ctx.Deployment.Id);

        if (_ctx.Deployment.ProcessSnapshotId.HasValue)
        {
            _ctx.ProcessSnapshot = await _snapshotService.LoadProcessSnapshotAsync(_ctx.Deployment.ProcessSnapshotId.Value, ct).ConfigureAwait(false);
            
            return;
        }

        _ctx.ProcessSnapshot = await _snapshotService.SnapshotProcessFromReleaseAsync(_ctx.Release, ct).ConfigureAwait(false);

        _ctx.Deployment.ProcessSnapshotId = _ctx.ProcessSnapshot.Id;
        
        await _deploymentDataProvider.UpdateDeploymentAsync(_ctx.Deployment, cancellationToken: ct).ConfigureAwait(false);
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
            throw new DeploymentTargetException($"No target machines found for deployment {_ctx.Deployment.Id}", _ctx.Deployment.Id);

        Log.Information("Found {Count} target machines for deployment {DeploymentId}", _ctx.Targets.Count, _ctx.Deployment.Id);
    }

    private async Task LoadAccountAsync(CancellationToken ct)
    {
        _ctx.EndpointJson = _ctx.Target.Endpoint;
        _ctx.CommunicationStyle = ParseCommunicationStyle(_ctx.EndpointJson);

        _resolvedContributor = _variableContributors.FirstOrDefault(c => c.CanHandle(_ctx.CommunicationStyle));

        if (_resolvedContributor != null)
        {
            var accountId = _resolvedContributor.ParseAccountId(_ctx.EndpointJson);
            
            if (accountId.HasValue)
                _ctx.Account = await _deploymentAccountDataProvider.GetAccountByIdAsync(accountId.Value, ct).ConfigureAwait(false);
        }
    }

    private async Task ContributeEndpointVariablesAsync(CancellationToken ct)
    {
        if (_resolvedContributor != null)
        {
            var endpointVars = _resolvedContributor.ContributeVariables(_ctx.EndpointJson, _ctx.Account);
            
            _ctx.Variables.AddRange(endpointVars);

            var additionalVars = await _resolvedContributor
                .ContributeAdditionalVariablesAsync(_ctx.ProcessSnapshot, _ctx.Release, ct).ConfigureAwait(false);
            
            _ctx.Variables.AddRange(additionalVars);
        }
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

    private void PreFilterTargetsByRoles()
    {
        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(_ctx.Steps);

        if (allRoles.Count == 0)
            return;

        var before = _ctx.Targets.Count;
        
        _ctx.Targets = DeploymentTargetFinder.FilterByRoles(_ctx.Targets, allRoles);

        if (_ctx.Targets.Count < before)
            Log.Information("Pre-filtered targets by roles: {Before} → {After} (roles: {Roles})", before, _ctx.Targets.Count, string.Join(", ", allRoles));

        if (_ctx.Targets.Count == 0)
            throw new DeploymentTargetException($"No target machines match the required roles [{string.Join(", ", allRoles)}] for deployment {_ctx.Deployment.Id}", _ctx.Deployment.Id);
    }

    private void ConvertSnapshotToSteps()
    {
        _ctx.Steps = ConvertProcessSnapshotToSteps(_ctx.ProcessSnapshot);
    }

    public static List<DeploymentStepDto> ConvertProcessSnapshotToSteps(DeploymentProcessSnapshotDto processSnapshot)
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
                StartTrigger = stepSnap.StartTrigger ?? "",
                PackageRequirement = "",
                IsDisabled = stepSnap.IsDisabled,
                IsRequired = stepSnap.IsRequired,
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
                                    }).ToList(),
                            Environments = action.Environments ?? new List<int>(),
                            ExcludedEnvironments = action.ExcludedEnvironments ?? new List<int>(),
                            Channels = action.Channels ?? new List<int>()
                        }).ToList()
            };

            steps.Add(step);
        }

        return steps;
    }
}

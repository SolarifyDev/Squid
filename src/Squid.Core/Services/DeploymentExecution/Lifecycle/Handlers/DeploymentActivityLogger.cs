using System.Collections.Concurrent;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Lifecycle.Handlers;

public sealed class DeploymentActivityLogger : DeploymentLifecycleHandlerBase
{
    private long? _taskNodeId;
    private readonly IDeploymentLogWriter _logWriter;
    private readonly ConcurrentDictionary<int, long?> _stepNodes = new();
    private readonly ConcurrentDictionary<(int StepDisplayOrder, string MachineName, int ActionSortOrder), long?> _actionNodes = new();
    private SensitiveValueMasker _masker;

    public DeploymentActivityLogger(IDeploymentLogWriter logWriter)
    {
        _logWriter = logWriter;
    }

    // === Deployment ===

    protected override async Task OnDeploymentStartingAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        InitializeSensitiveMasker();

        var projectName = string.IsNullOrWhiteSpace(Ctx.Project?.Name) ? $"Project {Ctx.Deployment?.ProjectId}" : Ctx.Project.Name;
        var releaseVersion = string.IsNullOrWhiteSpace(Ctx.Release?.Version) ? "Unknown" : Ctx.Release.Version;
        var environmentName = string.IsNullOrWhiteSpace(Ctx.Environment?.Name) ? $"Environment {Ctx.Deployment?.EnvironmentId}" : Ctx.Environment.Name;

        var nodeName = $"Deploy {projectName} release {releaseVersion} to {environmentName}";
        var node = await CreateActivityNodeAsync(null, nodeName, DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, ct).ConfigureAwait(false);
        _taskNodeId = node?.Id;

        await LogInfoAsync($"Deploying {projectName} release {releaseVersion} to {environmentName}", "System", ct).ConfigureAwait(false);
    }

    protected override async Task OnDeploymentResumingAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        InitializeSensitiveMasker();

        try
        {
            var nodes = await _logWriter.GetTreeByTaskIdAsync(Ctx.ServerTaskId, ct).ConfigureAwait(false);

            RestoreTaskNode(nodes);
            RestoreStepNodes(nodes);

            if (_taskNodeId != null)
                await UpdateActivityNodeStatusAsync(_taskNodeId, DeploymentActivityLogNodeStatus.Running, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to locate existing task node on resume for task {TaskId}", Ctx.ServerTaskId);
        }

        if (_taskNodeId == null)
        {
            Log.Warning("No existing task node found on resume for task {TaskId}, creating fallback", Ctx.ServerTaskId);
            var node = await CreateActivityNodeAsync(null, "Resumed deployment", DeploymentActivityLogNodeType.Task, DeploymentActivityLogNodeStatus.Running, 0, ct).ConfigureAwait(false);
            _taskNodeId = node?.Id;
        }

        await LogInfoAsync("Resuming deployment after interruption", "System", ct).ConfigureAwait(false);
    }

    private void RestoreTaskNode(List<ActivityLog> nodes)
    {
        var taskNode = nodes?.FirstOrDefault(n => n.NodeType == DeploymentActivityLogNodeType.Task);
        if (taskNode != null) _taskNodeId = taskNode.Id;
    }

    private void RestoreStepNodes(List<ActivityLog> nodes)
    {
        if (nodes == null) return;

        foreach (var stepNode in nodes.Where(n => n.NodeType == DeploymentActivityLogNodeType.Step))
            _stepNodes.TryAdd(stepNode.SortOrder, stepNode.Id);
    }

    protected override async Task OnDeploymentSucceededAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        await LogInfoAsync("Deployment completed successfully", "System", ct).ConfigureAwait(false);
        await FlushLogWriterAsync(ct).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(_taskNodeId, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    protected override async Task OnDeploymentFailedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        await UpdateActivityNodeStatusAsync(_taskNodeId, DeploymentActivityLogNodeStatus.Failed, ct).ConfigureAwait(false);
        await LogErrorAsync(ctx.Exception?.Message ?? ctx.Error ?? "Unknown error", "System", ct).ConfigureAwait(false);
        await FlushLogWriterAsync(ct).ConfigureAwait(false);
    }

    // === Target Preparation ===

    protected override Task OnTargetsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var names = string.Join(", ", ctx.Targets.Select(t => t.Name));

        return LogInfoAsync($"Found {ctx.Targets.Count} targets: {names}", "System", ct);
    }

    protected override Task OnUnhealthyTargetsExcludedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var names = string.Join(", ", ctx.Targets.Select(t => $"{t.Name} ({t.HealthStatus})"));

        return LogWarningAsync($"Excluded {ctx.Targets.Count} unhealthy target(s) from deployment: {names}", "System", ct);
    }

    protected override Task OnTargetPreparingAsync(DeploymentEventContext ctx, CancellationToken ct)
        => LogInfoAsync($"Preparing target: {ctx.MachineName} ({ctx.CommunicationStyle})", "System", ct);

    protected override Task OnTargetTransportMissingAsync(DeploymentEventContext ctx, CancellationToken ct)
        => LogWarningAsync($"No transport resolved for target {ctx.MachineName} with style {ctx.CommunicationStyle}", "System", ct);

    protected override async Task OnMachineConstraintsResolvedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var selection = DeploymentTargetFinder.ParseTargetSelection(Ctx.Deployment?.Json);

        if (!selection.HasConstraints) return;

        var machineNameById = Ctx.AllTargets.ToDictionary(m => m.Id, m => m.Name);

        if (selection.SpecificMachineIds.Count > 0)
        {
            var names = string.Join(", ", selection.SpecificMachineIds.Select(id => machineNameById.GetValueOrDefault(id, $"#{id}")).OrderBy(n => n));
            await LogInfoAsync($"Deploying only to these specifically included machines: {names}", "System", ct).ConfigureAwait(false);

            var disabledMachines = Ctx.AllTargets.Where(m => selection.SpecificMachineIds.Contains(m.Id) && m.IsDisabled).ToList();

            if (disabledMachines.Count > 0)
            {
                var disabledNames = string.Join(", ", disabledMachines.Select(m => m.Name));
                await LogWarningAsync($"The following specifically included machine{(disabledMachines.Count > 1 ? "s are" : " is")} disabled and will not receive the deployment: {disabledNames}", "System", ct).ConfigureAwait(false);
            }
        }

        if (selection.ExcludedMachineIds.Count > 0)
        {
            var names = string.Join(", ", selection.ExcludedMachineIds.Select(id => machineNameById.GetValueOrDefault(id, $"#{id}")).OrderBy(n => n));
            await LogInfoAsync($"These machines were specifically excluded from the deployment: {names}", "System", ct).ConfigureAwait(false);
        }
    }

    // === Packages ===

    protected override async Task OnPackagesAcquiringAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var node = await CreateActivityNodeAsync(_taskNodeId, "Acquire packages", DeploymentActivityLogNodeType.Phase, DeploymentActivityLogNodeStatus.Running, 0, ct).ConfigureAwait(false);
        var nodeId = node?.Id;

        await LogInfoAsync("Acquiring packages", "System", ct, nodeId).ConfigureAwait(false);

        var packages = ctx.SelectedPackages;

        if (packages?.Count > 0)
        {
            foreach (var pkg in packages)
                await LogInfoAsync($"Package {pkg.ActionName} version {pkg.Version}", "System", ct, nodeId).ConfigureAwait(false);

            await LogInfoAsync("All packages have been acquired", "System", ct, nodeId).ConfigureAwait(false);
        }
        else
        {
            await LogInfoAsync("No packages to acquire", "System", ct, nodeId).ConfigureAwait(false);
        }

        await UpdateActivityNodeStatusAsync(nodeId, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    protected override async Task OnPackagesReleasedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var releaseSortOrder = (Ctx.Steps?.Max(s => s.StepOrder) ?? 0) + 1;
        var node = await CreateActivityNodeAsync(_taskNodeId, "Release packages", DeploymentActivityLogNodeType.Phase, DeploymentActivityLogNodeStatus.Running, releaseSortOrder, ct).ConfigureAwait(false);
        var nodeId = node?.Id;

        await LogInfoAsync("There are no packages to be released.", "System", ct, nodeId).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(nodeId, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    // === Steps ===

    protected override async Task OnStepStartingAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        if (_stepNodes.TryGetValue(ctx.StepDisplayOrder, out var existingId) && existingId != null)
        {
            await UpdateActivityNodeStatusAsync(existingId, DeploymentActivityLogNodeStatus.Running, ct).ConfigureAwait(false);
            return;
        }

        var stepActivityName = BuildStepActivityName(ctx.StepName, ctx.StepDisplayOrder);
        var node = await CreateActivityNodeAsync(_taskNodeId, stepActivityName, DeploymentActivityLogNodeType.Step, DeploymentActivityLogNodeStatus.Running, ctx.StepDisplayOrder, ct).ConfigureAwait(false);
        _stepNodes[ctx.StepDisplayOrder] = node?.Id;
    }

    protected override async Task OnStepNoMatchingTargetsAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);
        var rolesText = ctx.Roles?.Count > 0 ? string.Join(", ", ctx.Roles) : "unknown";
        var plural = ctx.Roles?.Count > 1;

        await LogWarningAsync($"Skipping this step as no machines were found in the role{(plural ? "s" : "")}: {rolesText}", "System", ct, stepNodeId).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(stepNodeId, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    protected override Task OnStepSkippedOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync(ctx.StepEligibility?.Message ?? $"Step skipped on {ctx.MachineName}", ctx.MachineName, ct, stepNodeId);
    }

    protected override Task OnStepConditionMetAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync(ctx.Message, "System", ct, stepNodeId);
    }

    protected override Task OnStepExecutingOnTargetAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync($"Executing step \"{ctx.StepName}\" on {ctx.MachineName}", ctx.MachineName, ct, stepNodeId);
    }

    protected override async Task OnStepCompletedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        if (ctx.Skipped)
        {
            await LogInfoAsync($"Step \"{ctx.StepName}\" was skipped", "System", ct, stepNodeId).ConfigureAwait(false);
            await UpdateActivityNodeStatusAsync(stepNodeId, DeploymentActivityLogNodeStatus.Skipped, ct).ConfigureAwait(false);
            return;
        }

        if (!ctx.Failed)
            await LogInfoAsync($"Step \"{ctx.StepName}\" completed successfully", "System", ct, stepNodeId).ConfigureAwait(false);

        await UpdateActivityNodeStatusAsync(stepNodeId, ctx.Failed ? DeploymentActivityLogNodeStatus.Failed : DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    // === Health Check ===

    protected override Task OnHealthCheckStartingAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync("Running health check", "System", ct, stepNodeId);
    }

    protected override Task OnHealthCheckTargetResultAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        if (ctx.HealthCheckHealthy == true)
            return LogInfoAsync($"Health check passed for {ctx.MachineName}: {ctx.HealthCheckDetail}", ctx.MachineName, ct, stepNodeId);

        return LogWarningAsync($"Health check failed for {ctx.MachineName}: {ctx.HealthCheckDetail}", ctx.MachineName, ct, stepNodeId);
    }

    protected override Task OnHealthCheckCompletedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);
        var healthy = ctx.HealthCheckHealthyCount;
        var unhealthy = ctx.HealthCheckUnhealthyCount;

        if (unhealthy == 0)
            return LogInfoAsync($"Health check completed: all {healthy} target(s) healthy", "System", ct, stepNodeId);

        return LogWarningAsync($"Health check completed: {healthy} healthy, {unhealthy} unhealthy", "System", ct, stepNodeId);
    }

    // === Actions (pre-execution) ===

    protected override Task OnActionManuallyExcludedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync($"Action \"{ctx.ActionName}\" was manually excluded from this deployment", "System", ct, stepNodeId);
    }

    protected override Task OnActionSkippedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogWarningAsync(ctx.ActionEligibility?.Message ?? $"Action \"{ctx.ActionName}\" skipped", "System", ct, stepNodeId);
    }

    protected override Task OnActionNoHandlerAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogWarningAsync($"No handler found for action type \"{ctx.ActionType}\", skipping", "System", ct, stepNodeId);
    }

    protected override Task OnActionRunningAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);
        var suffix = string.IsNullOrWhiteSpace(ctx.MachineName) ? "" : $" on {ctx.MachineName}";

        return LogInfoAsync($"Running action \"{ctx.ActionName}\"{suffix}", "System", ct, stepNodeId);
    }

    // === Actions (execution) ===

    protected override async Task OnActionExecutingAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);
        var actionActivityName = BuildActionActivityName(ctx.MachineName);
        var node = await CreateActivityNodeAsync(stepNodeId, actionActivityName, DeploymentActivityLogNodeType.Action, DeploymentActivityLogNodeStatus.Running, ctx.ActionSortOrder, ct).ConfigureAwait(false);
        _actionNodes[(ctx.StepDisplayOrder, ctx.MachineName, ctx.ActionSortOrder)] = node?.Id;
    }

    protected override async Task OnActionSucceededAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var actionNodeId = LookupActionNode(ctx.StepDisplayOrder, ctx.MachineName, ctx.ActionSortOrder);

        await LogInfoAsync($"Successfully finished \"{ctx.ActionName}\" on {ctx.MachineName} (exit code {ctx.ExitCode})", ctx.MachineName, ct, actionNodeId).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(actionNodeId, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
    }

    protected override async Task OnActionFailedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var actionNodeId = LookupActionNode(ctx.StepDisplayOrder, ctx.MachineName, ctx.ActionSortOrder);

        await LogErrorAsync(ctx.Error, ctx.MachineName, ct, actionNodeId).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(actionNodeId, DeploymentActivityLogNodeStatus.Failed, ct).ConfigureAwait(false);
    }

    // === Script Output ===

    protected override Task OnScriptOutputReceivedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var actionNodeId = LookupActionNode(ctx.StepDisplayOrder, ctx.MachineName, ctx.ActionSortOrder);

        return PersistScriptOutputAsync(ctx.ScriptResult, ctx.MachineName, actionNodeId, ct);
    }

    // === Guided Failure ===

    protected override Task OnGuidedFailurePromptAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogWarningAsync($"Guided failure: action \"{ctx.ActionName}\" failed on {ctx.MachineName}. Waiting for manual intervention — {ctx.Error}", "System", ct, stepNodeId);
    }

    protected override Task OnGuidedFailureResolvedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync($"Guided failure resolved: {ctx.GuidedFailureResolution}", "System", ct, stepNodeId);
    }

    // === Manual Intervention ===

    protected override Task OnManualInterventionPromptAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync($"Manual intervention required for action \"{ctx.ActionName}\". Waiting for user response", "System", ct, stepNodeId);
    }

    protected override Task OnManualInterventionResolvedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        var stepNodeId = LookupStepNode(ctx.StepDisplayOrder);

        return LogInfoAsync($"Manual intervention resolved: {ctx.GuidedFailureResolution}", "System", ct, stepNodeId);
    }

    // === Cancellation / Pause ===

    protected override async Task OnDeploymentCancelledAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        await LogWarningAsync("Deployment was cancelled", "System", ct).ConfigureAwait(false);
        await FlushLogWriterAsync(ct).ConfigureAwait(false);
        await UpdateActivityNodeStatusAsync(_taskNodeId, DeploymentActivityLogNodeStatus.Failed, ct).ConfigureAwait(false);
    }

    protected override async Task OnDeploymentPausedAsync(DeploymentEventContext ctx, CancellationToken ct)
    {
        await LogInfoAsync("Deployment paused — waiting for interruption to be resolved", "System", ct).ConfigureAwait(false);
    }

    // === Node Lookup ===

    private long? LookupStepNode(int stepDisplayOrder)
        => _stepNodes.GetValueOrDefault(stepDisplayOrder);

    private long? LookupActionNode(int stepDisplayOrder, string machine, int sortOrder)
        => _actionNodes.GetValueOrDefault((stepDisplayOrder, machine, sortOrder));

    // === Formatting ===

    private static string BuildStepActivityName(string stepName, int stepSortOrder)
    {
        var name = stepName?.Trim();

        if (string.IsNullOrWhiteSpace(name))
            return $"Step {stepSortOrder}";

        if (name.StartsWith("Step ", StringComparison.OrdinalIgnoreCase))
            return name;

        return $"Step {stepSortOrder}: {name}";
    }

    private static string BuildActionActivityName(string machineName)
    {
        if (!string.IsNullOrWhiteSpace(machineName))
            return $"Executing on {machineName}";

        return "Executing";
    }

    // === Sensitive Value Masking ===

    private void InitializeSensitiveMasker()
    {
        var sensitiveValues = CollectSensitiveValues();
        _masker = new SensitiveValueMasker(sensitiveValues);

        if (_masker.ValueCount > 0)
            Log.Debug("Initialized sensitive value masker with {Count} values for task {TaskId}", _masker.ValueCount, Ctx.ServerTaskId);
    }

    private IEnumerable<string> CollectSensitiveValues()
    {
        if (Ctx.Variables != null)
        {
            foreach (var v in Ctx.Variables)
            {
                if (v.IsSensitive) yield return v.Value;
            }
        }

        if (Ctx.AllTargetsContext == null) yield break;

        foreach (var tc in Ctx.AllTargetsContext)
        {
            if (tc.EndpointVariables == null) continue;

            foreach (var v in tc.EndpointVariables)
            {
                if (v.IsSensitive) yield return v.Value;
            }
        }
    }

    private string MaskSensitiveValues(string text)
        => _masker?.Mask(text) ?? text;

    // === Persistence Helpers ===

    private async Task FlushLogWriterAsync(CancellationToken ct)
    {
        try
        {
            await _logWriter.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to flush log writer for task {TaskId}", Ctx.ServerTaskId);
        }
    }

    private Task LogInfoAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Info, message, source, nodeId ?? _taskNodeId, ct);

    private Task LogWarningAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Warning, message, source, nodeId ?? _taskNodeId, ct);

    private Task LogErrorAsync(string message, string source, CancellationToken ct, long? nodeId = null)
        => PersistTaskLogAsync(ServerTaskLogCategory.Error, message, source, nodeId ?? _taskNodeId, ct);

    private async Task<ActivityLog> CreateActivityNodeAsync(long? parentId, string name, DeploymentActivityLogNodeType nodeType, DeploymentActivityLogNodeStatus status, int sortOrder, CancellationToken ct)
    {
        try
        {
            return await _logWriter.AddActivityNodeAsync(Ctx.ServerTaskId, parentId, name, nodeType, status, sortOrder, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to create activity log node: {Name}", name);
            return null;
        }
    }

    private async Task PersistTaskLogAsync(ServerTaskLogCategory category, string message, string source, long? activityNodeId, CancellationToken ct)
    {
        try
        {
            var seq = Ctx.NextLogSequence();
            var maskedMessage = MaskSensitiveValues(message);
            await _logWriter.AddLogAsync(Ctx.ServerTaskId, seq, category, maskedMessage, source, activityNodeId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist task log entry");
        }
    }

    private async Task PersistScriptOutputAsync(ScriptExecutionResult execResult, string source, long? activityNodeId, CancellationToken ct)
    {
        if (execResult?.LogLines == null || execResult.LogLines.Count == 0) return;

        var stderrSet = execResult.StderrLines?.Count > 0 ? new HashSet<string>(execResult.StderrLines, StringComparer.Ordinal) : null;

        try
        {
            var entries = execResult.LogLines.Select(line => new ServerTaskLogWriteEntry
            {
                Category = stderrSet != null && stderrSet.Contains(line) ? ServerTaskLogCategory.Error : ServerTaskLogCategory.Info,
                MessageText = MaskSensitiveValues(line),
                Source = source,
                OccurredAt = DateTimeOffset.UtcNow,
                SequenceNumber = Ctx.NextLogSequence(),
                ActivityNodeId = activityNodeId
            }).ToList();

            await _logWriter.AddLogsAsync(Ctx.ServerTaskId, entries, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist script output for task {TaskId}", Ctx.ServerTaskId);
        }
    }

    private async Task UpdateActivityNodeStatusAsync(long? nodeId, DeploymentActivityLogNodeStatus status, CancellationToken ct)
    {
        if (nodeId == null) return;

        try
        {
            await _logWriter.UpdateActivityNodeStatusAsync(nodeId.Value, status, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to update activity node status for node {NodeId}", nodeId);
        }
    }
}

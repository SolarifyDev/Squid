using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Message.Constants;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public sealed class HealthCheckActionHandler(IDeploymentLifecycle lifecycle, IDeploymentTargetFinder targetFinder) : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.HealthCheck;

    public ExecutionScope ExecutionScope => ExecutionScope.StepLevel;

    /// <summary>
    /// Produces a <see cref="HealthCheckIntent"/> with a stable semantic name
    /// (<c>health-check</c>). The <c>Squid.Action.HealthCheck.*</c> properties are
    /// mapped onto semantic intent fields via the same <see cref="ParseSettings"/> helper
    /// that powers <see cref="ExecuteStepLevelAsync"/>.
    /// </summary>
    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var settings = ParseSettings(ctx.Action);

        var intent = new HealthCheckIntent
        {
            Name = "health-check",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            CheckType = settings.CheckType,
            ErrorHandling = settings.ErrorHandling,
            IncludeNewTargets = settings.IncludeNewTargets
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }

    public async Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct)
    {
        var deployCtx = ctx.DeploymentContext;
        if (deployCtx == null) return;

        var targets = deployCtx.AllTargetsContext;
        if (targets == null || targets.Count == 0) return;

        var settings = ParseSettings(ctx.Action);

        await lifecycle.EmitAsync(new HealthCheckStartingEvent(new DeploymentEventContext
        {
            StepDisplayOrder = ctx.StepDisplayOrder, StepName = ctx.Step.Name, ActionName = ctx.Action.Name
        }), ct).ConfigureAwait(false);

        var (healthyCount, unhealthyCount) = await RunHealthChecksAsync(targets, settings, ctx.StepDisplayOrder, ct).ConfigureAwait(false);

        if (unhealthyCount > 0 && settings.ErrorHandling == HealthCheckErrorHandling.FailDeployment)
            throw new DeploymentTargetException($"Health check failed: {unhealthyCount} target(s) unavailable");

        if (settings.IncludeNewTargets)
            await IncludeNewTargetsAsync(deployCtx, ctx.Step, ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new HealthCheckCompletedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = ctx.StepDisplayOrder, StepName = ctx.Step.Name,
            HealthCheckHealthyCount = healthyCount, HealthCheckUnhealthyCount = unhealthyCount
        }), ct).ConfigureAwait(false);
    }

    private async Task<(int Healthy, int Unhealthy)> RunHealthChecksAsync(List<DeploymentTargetContext> targets, HealthCheckSettings settings, int stepDisplayOrder, CancellationToken ct)
    {
        var healthyCount = 0;
        var unhealthyCount = 0;

        // Phase-6.3: route through TargetParallelExecutor so the process-wide
        // global parallelism cap applies. Pre-fix this was a raw uncapped
        // Task.WhenAll → 100-target health check spawned 100 simultaneous
        // Halibut connections + 100 cluster TLS handshakes. maxParallelism=0
        // (= as-many-as-items) preserves the existing per-step behaviour;
        // only the cross-process cap is new.
        var results = await TargetParallelExecutor.ExecuteAsync(
            targets, maxParallelism: 0,
            (tc, taskCt) => CheckTargetAsync(tc, settings, taskCt), ct).ConfigureAwait(false);

        foreach (var (tc, result) in targets.Zip(results))
        {
            await lifecycle.EmitAsync(new HealthCheckTargetResultEvent(new DeploymentEventContext
            {
                StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name,
                HealthCheckHealthy = result.Healthy, HealthCheckDetail = result.Detail
            }), ct).ConfigureAwait(false);

            if (result.Healthy)
            {
                healthyCount++;
                continue;
            }

            unhealthyCount++;
            tc.Exclude($"Health check failed: {result.Detail}");
            Log.Warning("[Deploy] Health check failed for {MachineName}: {Detail}", tc.Machine.Name, result.Detail);
        }

        return (healthyCount, unhealthyCount);
    }

    private static async Task<HealthCheckResult> CheckTargetAsync(DeploymentTargetContext tc, HealthCheckSettings settings, CancellationToken ct)
    {
        var healthChecker = tc.Transport?.HealthChecker;

        if (healthChecker == null)
            return new HealthCheckResult(true, "No health checker configured, treating as healthy");

        try
        {
            return await healthChecker.CheckHealthAsync(tc.Machine, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Deploy] Health check threw for {MachineName}", tc.Machine.Name);
            return new HealthCheckResult(false, $"Health check exception: {ex.Message}");
        }
    }

    private async Task IncludeNewTargetsAsync(DeploymentTaskContext deployCtx, DeploymentStepDto step, CancellationToken ct)
    {
        var freshTargets = await targetFinder.FindTargetsAsync(deployCtx.Deployment, ct).ConfigureAwait(false);
        var existingIds = new HashSet<int>(deployCtx.AllTargets.Select(m => m.Id));
        var newMachines = freshTargets.Where(m => !existingIds.Contains(m.Id)).ToList();

        if (newMachines.Count == 0) return;

        Log.Information("[Deploy] Including {Count} new deployment target(s) discovered during health check", newMachines.Count);

        deployCtx.AllTargets.AddRange(newMachines);

        // Note: new targets need PrepareAllTargetsAsync to set up transport — this only adds
        // the Machine entities. Full transport preparation happens if the pipeline supports it.
        // For now, log the discovery so operators know new targets were found.
    }

    internal static HealthCheckSettings ParseSettings(DeploymentActionDto action)
    {
        var typeStr = action.GetProperty("Squid.Action.HealthCheck.Type");
        var errorStr = action.GetProperty("Squid.Action.HealthCheck.ErrorHandling");
        var includeStr = action.GetProperty("Squid.Action.HealthCheck.IncludeMachinesInDeployment");

        var checkType = string.Equals(typeStr, "ConnectionTest", StringComparison.OrdinalIgnoreCase)
            ? HealthCheckType.ConnectionTest
            : HealthCheckType.FullHealthCheck;

        var errorHandling = string.Equals(errorStr, "TreatExceptionsAsWarnings", StringComparison.OrdinalIgnoreCase)
            ? HealthCheckErrorHandling.SkipUnavailable
            : HealthCheckErrorHandling.FailDeployment;

        var includeNew = string.Equals(includeStr, "IncludeCheckedMachines", StringComparison.OrdinalIgnoreCase);

        return new HealthCheckSettings(checkType, errorHandling, includeNew);
    }
}

public record HealthCheckSettings(HealthCheckType CheckType, HealthCheckErrorHandling ErrorHandling, bool IncludeNewTargets);

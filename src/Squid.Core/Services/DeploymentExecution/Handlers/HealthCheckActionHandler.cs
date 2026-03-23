using Squid.Core.Extensions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
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

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ExecutionMode = ExecutionMode.ManualIntervention,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip
        });
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

        var results = await Task.WhenAll(targets.Select(tc => CheckTargetAsync(tc, settings, ct))).ConfigureAwait(false);

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
            Log.Warning("Health check failed for {MachineName}: {Detail}", tc.Machine.Name, result.Detail);
        }

        return (healthyCount, unhealthyCount);
    }

    private static async Task<HealthCheckResult> CheckTargetAsync(DeploymentTargetContext tc, HealthCheckSettings settings, CancellationToken ct)
    {
        if (settings.CheckType == HealthCheckType.FullHealthCheck)
            return await RunFullHealthCheckAsync(tc, ct).ConfigureAwait(false);

        return await RunConnectivityCheckAsync(tc, ct).ConfigureAwait(false);
    }

    private static async Task<HealthCheckResult> RunConnectivityCheckAsync(DeploymentTargetContext tc, CancellationToken ct)
    {
        var healthChecker = tc.Transport?.HealthChecker;

        if (healthChecker == null)
            return new HealthCheckResult(true, "No health checker configured, treating as healthy");

        try
        {
            return await healthChecker.CheckConnectivityAsync(tc.Machine, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Health check threw for {MachineName}", tc.Machine.Name);
            return new HealthCheckResult(false, $"Health check exception: {ex.Message}");
        }
    }

    private static async Task<HealthCheckResult> RunFullHealthCheckAsync(DeploymentTargetContext tc, CancellationToken ct)
    {
        var healthChecker = tc.Transport?.HealthChecker;
        var strategy = tc.Transport?.Strategy;

        if (healthChecker == null || strategy == null)
            return await RunConnectivityCheckAsync(tc, ct).ConfigureAwait(false);

        try
        {
            var script = healthChecker.DefaultHealthCheckScript;

            if (string.IsNullOrEmpty(script))
                return await healthChecker.CheckConnectivityAsync(tc.Machine, null, ct).ConfigureAwait(false);

            var request = new ScriptExecutionRequest
            {
                ScriptBody = script,
                Syntax = ScriptSyntax.Bash,
                ExecutionMode = ExecutionMode.DirectScript,
                Machine = tc.Machine,
                Files = new Dictionary<string, byte[]>(),
                Variables = new List<VariableDto>()
            };

            var result = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

            return result.Success
                ? new HealthCheckResult(true, $"Full health check passed (exit code {result.ExitCode})")
                : new HealthCheckResult(false, $"Full health check failed (exit code {result.ExitCode}): {result.BuildErrorSummary()}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Full health check threw for {MachineName}", tc.Machine.Name);
            return new HealthCheckResult(false, $"Full health check exception: {ex.Message}");
        }
    }

    private async Task IncludeNewTargetsAsync(DeploymentTaskContext deployCtx, DeploymentStepDto step, CancellationToken ct)
    {
        var freshTargets = await targetFinder.FindTargetsAsync(deployCtx.Deployment, ct).ConfigureAwait(false);
        var existingIds = new HashSet<int>(deployCtx.AllTargets.Select(m => m.Id));
        var newMachines = freshTargets.Where(m => !existingIds.Contains(m.Id)).ToList();

        if (newMachines.Count == 0) return;

        Log.Information("Including {Count} new deployment target(s) discovered during health check", newMachines.Count);

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

public enum HealthCheckType
{
    FullHealthCheck,
    ConnectionTest
}

public enum HealthCheckErrorHandling
{
    FailDeployment,
    SkipUnavailable
}

public record HealthCheckSettings(HealthCheckType CheckType, HealthCheckErrorHandling ErrorHandling, bool IncludeNewTargets);

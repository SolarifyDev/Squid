using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class PrepareDeploymentPhase(
    IDeploymentSnapshotService snapshotService,
    IDeploymentVariableResolver variableResolver,
    IDeploymentTargetFinder targetFinder,
    IDeploymentDataProvider deploymentDataProvider) : IDeploymentPipelinePhase
{
    public int Order => 300;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await LoadOrSnapshotAsync(ctx, ct).ConfigureAwait(false);
        await ResolveVariablesAsync(ctx, ct).ConfigureAwait(false);
        await FindTargetsAsync(ctx, ct).ConfigureAwait(false);

        ConvertSnapshotToSteps(ctx);
        PreFilterTargetsByRoles(ctx);
    }

    private async Task LoadOrSnapshotAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("Loading process snapshot for deployment {DeploymentId}", ctx.Deployment.Id);

        if (ctx.Deployment.ProcessSnapshotId.HasValue)
        {
            ctx.ProcessSnapshot = await snapshotService.LoadProcessSnapshotAsync(ctx.Deployment.ProcessSnapshotId.Value, ct).ConfigureAwait(false);

            return;
        }

        ctx.ProcessSnapshot = await snapshotService.SnapshotProcessFromReleaseAsync(ctx.Release, ct).ConfigureAwait(false);

        ctx.Deployment.ProcessSnapshotId = ctx.ProcessSnapshot.Id;

        await deploymentDataProvider.UpdateDeploymentAsync(ctx.Deployment, cancellationToken: ct).ConfigureAwait(false);
    }

    private async Task ResolveVariablesAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("Resolving variables for deployment {DeploymentId}", ctx.Deployment.Id);

        ctx.Variables = await variableResolver.ResolveVariablesAsync(ctx.Deployment.Id, ct).ConfigureAwait(false);

        ctx.Variables.Add(new VariableDto { Name = DeploymentVariableNames.DeploymentId, Value = ctx.Deployment.Id.ToString() });
    }

    private async Task FindTargetsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        Log.Information("Finding targets for deployment {DeploymentId}", ctx.Deployment.Id);

        ctx.AllTargets = await targetFinder.FindTargetsAsync(ctx.Deployment, ct).ConfigureAwait(false);

        var (healthy, excludedByHealth) = DeploymentTargetFinder.FilterByHealthStatus(ctx.AllTargets);

        if (excludedByHealth.Count > 0)
        {
            ctx.AllTargets = healthy;
            ctx.ExcludedByHealthTargets = excludedByHealth;
        }

        if (ctx.AllTargets.Count == 0) throw new DeploymentTargetException($"No target machines found for deployment {ctx.Deployment.Id}", ctx.Deployment.Id);

        Log.Information("Found {Count} target machines for deployment {DeploymentId}", ctx.AllTargets.Count, ctx.Deployment.Id);
    }

    private static void ConvertSnapshotToSteps(DeploymentTaskContext ctx)
    {
        ctx.Steps = ProcessSnapshotStepConverter.Convert(ctx.ProcessSnapshot);
    }

    private static void PreFilterTargetsByRoles(DeploymentTaskContext ctx)
    {
        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(ctx.Steps);

        if (allRoles.Count == 0)
            return;

        var before = ctx.AllTargets.Count;

        ctx.AllTargets = DeploymentTargetFinder.FilterByRoles(ctx.AllTargets, allRoles);

        if (ctx.AllTargets.Count < before)
            Log.Information("Pre-filtered targets by roles: {Before} -> {After} (roles: {Roles})", before, ctx.AllTargets.Count, string.Join(", ", allRoles));

        if (ctx.AllTargets.Count == 0)
            throw new DeploymentTargetException($"No target machines match the required roles [{string.Join(", ", allRoles)}] for deployment {ctx.Deployment.Id}", ctx.Deployment.Id);
    }
}

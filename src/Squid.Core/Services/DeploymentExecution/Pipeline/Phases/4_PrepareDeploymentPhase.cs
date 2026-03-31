using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class PrepareDeploymentPhase(
    IDeploymentSnapshotService snapshotService,
    IDeploymentVariableResolver variableResolver,
    IDeploymentTargetFinder targetFinder,
    IDeploymentDataProvider deploymentDataProvider,
    IActionHandlerRegistry actionHandlerRegistry) : IDeploymentPipelinePhase
{
    public int Order => 300;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await LoadOrSnapshotAsync(ctx, ct).ConfigureAwait(false);
        await ResolveVariablesAsync(ctx, ct).ConfigureAwait(false);
        ValidatePromptedVariables(ctx);
        MergePromptedVariables(ctx);

        ConvertSnapshotToSteps(ctx);
        InjectPackageAcquisitionSteps(ctx);
        DetectServerOnlyDeployment(ctx);

        if (!ctx.IsServerOnlyDeployment)
        {
            await FindTargetsAsync(ctx, ct).ConfigureAwait(false);
            PreFilterTargetsByRoles(ctx);
        }
    }

    private async Task LoadOrSnapshotAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
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
        ctx.Variables = await variableResolver.ResolveVariablesAsync(ctx.Deployment.Id, ct).ConfigureAwait(false);

        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Deployment.Id, Value = $"Deployments-{ctx.Deployment.Id}" });

        if (ctx.RestoredOutputVariables.Count > 0)
            ctx.Variables.AddRange(ctx.RestoredOutputVariables);
    }

    private static void ValidatePromptedVariables(DeploymentTaskContext ctx)
    {
        var formValues = ctx.Deployment?.DeploymentRequestPayload?.FormValues;
        var errors = PromptedVariableMerger.ValidateRequiredPrompts(ctx.Variables, formValues);

        if (errors.Count > 0)
            throw new DeploymentValidationException(string.Join("; ", errors));
    }

    private static void MergePromptedVariables(DeploymentTaskContext ctx)
    {
        var formValues = ctx.Deployment?.DeploymentRequestPayload?.FormValues;

        PromptedVariableMerger.MergePromptedValues(ctx.Variables, formValues);
    }

    private void DetectServerOnlyDeployment(DeploymentTaskContext ctx)
    {
        ctx.IsServerOnlyDeployment = RunOnServerEvaluator.IsEntireDeploymentServerOnly(ctx.Steps, actionHandlerRegistry.ResolveScope);

        if (ctx.IsServerOnlyDeployment)
            Log.Information("[Deploy] All steps are RunOnServer — no target machines required for deployment {DeploymentId}", ctx.Deployment.Id);
    }

    private async Task FindTargetsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        ctx.AllTargets = await targetFinder.FindTargetsAsync(ctx.Deployment, ct).ConfigureAwait(false);

        var (healthy, excludedByHealth) = DeploymentTargetFinder.FilterByHealthStatus(ctx.AllTargets);

        if (excludedByHealth.Count > 0)
        {
            ctx.AllTargets = healthy;
            ctx.ExcludedByHealthTargets = excludedByHealth;
        }

        if (ctx.AllTargets.Count == 0) throw new DeploymentTargetException($"No target machines found for deployment {ctx.Deployment.Id}", ctx.Deployment.Id);

        Log.Information("[Deploy] Found {Count} target machines for deployment {DeploymentId}", ctx.AllTargets.Count, ctx.Deployment.Id);
    }

    private static void ConvertSnapshotToSteps(DeploymentTaskContext ctx)
    {
        ctx.Steps = ProcessSnapshotStepConverter.Convert(ctx.ProcessSnapshot);
    }

    private static void InjectPackageAcquisitionSteps(DeploymentTaskContext ctx)
    {
        ctx.Steps = PackageAcquisitionInjector.InjectAcquisitionSteps(ctx.Steps, ctx.SelectedPackages);
    }

    private void PreFilterTargetsByRoles(DeploymentTaskContext ctx)
    {
        var allRoles = DeploymentTargetFinder.CollectAllTargetRoles(ctx.Steps, actionHandlerRegistry.ResolveScope);

        if (allRoles.Count == 0)
            return;

        var before = ctx.AllTargets.Count;

        ctx.AllTargets = DeploymentTargetFinder.FilterByRoles(ctx.AllTargets, allRoles);

        if (ctx.ExcludedByHealthTargets is { Count: > 0 })
            ctx.ExcludedByHealthTargets = DeploymentTargetFinder.FilterByRoles(ctx.ExcludedByHealthTargets, allRoles);

        if (ctx.AllTargets.Count < before)
            Log.Information("[Deploy] Pre-filtered targets by roles: {Before} -> {After} (roles: {Roles})", before, ctx.AllTargets.Count, string.Join(", ", allRoles));

        if (ctx.AllTargets.Count == 0)
            throw new DeploymentTargetException($"No target machines match the required roles [{string.Join(", ", allRoles)}] for deployment {ctx.Deployment.Id}", ctx.Deployment.Id);
    }
}

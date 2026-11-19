using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Constants;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class PrepareDeploymentPhase(
    IDeploymentSnapshotService snapshotService,
    IDeploymentVariableResolver variableResolver,
    IDeploymentTargetFinder targetFinder,
    IDeploymentDataProvider deploymentDataProvider,
    IActionHandlerRegistry actionHandlerRegistry,
    IRepository repository = null,
    IImportedSecretPreflight importedSecrets = null) : IDeploymentPipelinePhase
{
    public int Order => 300;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        await LoadOrSnapshotAsync(ctx, ct).ConfigureAwait(false);
        await ResolveVariablesAsync(ctx, ct).ConfigureAwait(false);
        ValidatePromptedVariables(ctx);
        MergePromptedVariables(ctx);
        await EnsureImportedSecretsPresentAsync(ctx, ct).ConfigureAwait(false);

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

        await AddDeploymentCreatorVariablesAsync(ctx, ct).ConfigureAwait(false);

        // Built-in deployment context variables required by package install path planning
        // and variable substitution. These are not stored in project variable sets.
        if (ctx.Project != null)
        {
            ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Project.Id, Value = ctx.Project.Id.ToString() });
            ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Project.Name, Value = ctx.Project.Name ?? string.Empty });
        }

        if (ctx.Environment != null)
        {
            ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Environment.Id, Value = ctx.Environment.Id.ToString() });
            ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Environment.Name, Value = ctx.Environment.Name ?? string.Empty });
        }

        if (ctx.Release != null)
        {
            ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Release.Number, Value = ctx.Release.Version ?? string.Empty });
        }

        // The captured-set re-seed for these restored variables happens in ExecuteStepsPhase,
        // which owns that accumulator and has the encryption service needed to protect them.
        if (ctx.RestoredOutputVariables.Count > 0)
            ctx.Variables.AddRange(ctx.RestoredOutputVariables);
    }

    // Who triggered the deployment (Octopus' Octopus.Deployment.CreatedBy.*). Always defined — a script that
    // reads an undefined name gets the literal #{…} token — and always appended after the resolved variables,
    // so precedence against a user-defined variable of the same name never depends on whether the user row
    // still exists. No actor → all empty; deleted user or no repository → the id with empty names.
    private async Task AddDeploymentCreatorVariablesAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        var userId = ctx.Deployment.DeployedBy > 0 ? ctx.Deployment.DeployedBy : ctx.Deployment.CreatedBy;
        var user = await FindDeploymentCreatorAsync(userId, ct).ConfigureAwait(false);
        var userName = user?.UserName ?? string.Empty;

        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Deployment.CreatedById, Value = userId > 0 ? userId.ToString() : string.Empty });
        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Deployment.CreatedByUsername, Value = userName });
        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Deployment.CreatedByDisplayName, Value = string.IsNullOrWhiteSpace(user?.DisplayName) ? userName : user.DisplayName });
    }

    private async Task<UserAccount> FindDeploymentCreatorAsync(int userId, CancellationToken ct)
    {
        if (repository == null || userId <= 0) return null;

        return await repository.QueryNoTracking<UserAccount>(u => u.Id == userId).Select(u => new UserAccount { Id = u.Id, UserName = u.UserName, DisplayName = u.DisplayName }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
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

    // D16: a sensitive value the Octopus API withheld and nothing has supplied since fails the deployment before any step runs,
    // naming it — instead of every script silently reading an empty secret. Runs after prompted values are merged (a prompt answers it).
    private async Task EnsureImportedSecretsPresentAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        if (importedSecrets == null) return;

        var missing = await importedSecrets.FindMissingAsync(ctx.Deployment.SpaceId, ctx.Variables, DeploymentScope(ctx), ct).ConfigureAwait(false);

        if (missing.Count > 0)
            throw new ImportedSecretMissingException(ctx.Deployment.Id, missing);
    }

    private static VariableScopeContext DeploymentScope(DeploymentTaskContext ctx)
        => new() { EnvironmentId = ctx.Environment?.Id, EnvironmentName = ctx.Environment?.Name, ChannelId = ctx.Channel?.Id, ChannelName = ctx.Channel?.Name };

    private void DetectServerOnlyDeployment(DeploymentTaskContext ctx)
    {
        ctx.IsServerOnlyDeployment = RunOnServerEvaluator.IsEntireDeploymentServerOnly(ctx.Steps, actionHandlerRegistry.ResolveScope);

        if (ctx.IsServerOnlyDeployment)
            Log.Information("[Deploy] All steps are RunOnServer — no target machines required for deployment {DeploymentId}", ctx.Deployment.Id);
    }

    private async Task FindTargetsAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        ctx.AllTargets = await targetFinder.FindTargetsAsync(ctx.Deployment, ct).ConfigureAwait(false);

        // Scope the transient-target policy to the targets the deployment would
        // actually use (matching at least one step role). An unavailable target in
        // the environment that no step targets must NOT fail the deployment — it is
        // not a deployment target for this release. Same role set PreFilterTargetsByRoles uses.
        var requiredRoles = DeploymentTargetFinder.CollectAllTargetRoles(ctx.Steps, actionHandlerRegistry.ResolveScope);

        ApplyTransientTargetPolicy(ctx, requiredRoles);

        if (ctx.AllTargets.Count == 0) throw new DeploymentTargetException($"No target machines found for deployment {ctx.Deployment.Id}", ctx.Deployment.Id);

        Log.Information("[Deploy] Found {Count} target machines for deployment {DeploymentId}", ctx.AllTargets.Count, ctx.Deployment.Id);
    }

    // Honour the project's "Transient Deployment Targets" setting, scoped to the
    // deployment's role-matched targets (see requiredRoles). For an unconfigured
    // project the defaults are FailDeployment (an unavailable target aborts the
    // deployment up front rather than being silently skipped) + Exclude (unhealthy
    // targets are dropped). Explicit SkipAndContinue is the opt-out that restores the
    // lenient skip behaviour for unavailable targets.
    internal static void ApplyTransientTargetPolicy(DeploymentTaskContext ctx, HashSet<string> requiredRoles)
    {
        var result = TransientDeploymentTargetEvaluator.ApplyProjectPolicy(ctx.AllTargets, requiredRoles, ctx.Project?.DeploymentSettingsJson);

        if (result.FailedUnavailable.Count > 0)
        {
            var names = string.Join(", ", result.FailedUnavailable.Select(m => m.Name));
            throw new DeploymentTargetException(
                $"Deployment {ctx.Deployment.Id} cannot start: {result.FailedUnavailable.Count} deployment target(s) are unavailable and the project's Transient Deployment Targets setting is 'Fail deployment': {names}",
                ctx.Deployment.Id);
        }

        ctx.AllTargets = result.Kept;

        if (result.Skipped.Count > 0)
            ctx.ExcludedByHealthTargets = result.Skipped;
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

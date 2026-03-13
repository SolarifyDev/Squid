using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Validation;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.Core.Services.Deployments.Deployments;

public partial class DeploymentService
{
    public async Task<DeploymentPreviewResult> PreviewDeploymentAsync(DeploymentRequestPayload deploymentRequestPayload, CancellationToken cancellationToken = default)
    {
        return await PreviewInternalAsync(deploymentRequestPayload, DeploymentValidationStage.Precheck, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DeploymentPreviewResult> PreviewInternalAsync(DeploymentRequestPayload deploymentRequestPayload, DeploymentValidationStage stage, CancellationToken cancellationToken)
    {
        var normalizedPayload = NormalizeRequestPayload(deploymentRequestPayload);
        var context = BuildValidationContext(normalizedPayload);
        var result = new DeploymentPreviewResult();
        var blockingReasons = new List<string>();

        Log.Information("Building deployment preview for release {ReleaseId} and environment {EnvironmentId}", context.ReleaseId, context.EnvironmentId);

        var release = await _releaseDataProvider.GetReleaseByIdAsync(context.ReleaseId, cancellationToken).ConfigureAwait(false);

        if (release == null)
        {
            blockingReasons.Add($"Release {context.ReleaseId} not found.");
            return FinalizePreview(result, blockingReasons);
        }

        var environment = await _environmentDataProvider
            .GetEnvironmentByIdAsync(context.EnvironmentId, cancellationToken).ConfigureAwait(false);

        if (environment == null)
        {
            blockingReasons.Add($"Environment {context.EnvironmentId} not found.");
            return FinalizePreview(result, blockingReasons);
        }

        var machines = await _machineDataProvider
            .GetMachinesByFilterAsync([context.EnvironmentId], [], cancellationToken).ConfigureAwait(false);

        var selectedMachines = ApplyMachineSelection(machines, context.SpecificMachineIds, context.ExcludedMachineIds);
        
        result.AvailableMachineCount = selectedMachines.Count;
        result.CandidateTargets = selectedMachines.OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase).Select(ToPreviewTarget).ToList();

        if (selectedMachines.Count == 0)
        {
            var message = context.SpecificMachineIds.Count > 0 || context.ExcludedMachineIds.Count > 0
                ? $"No available machines found after applying machine selection constraints in environment {context.EnvironmentId}."
                : $"No available machines found in environment {context.EnvironmentId}.";
            blockingReasons.Add(message);
        }

        await PopulateLifecycleValidationAsync(result, release, context, blockingReasons, cancellationToken).ConfigureAwait(false);

        var ruleReport = await _deploymentValidationOrchestrator.ValidateAsync(stage, context, cancellationToken).ConfigureAwait(false);

        blockingReasons.AddRange(ruleReport.Issues.Where(issue => issue.IsBlocking).Select(issue => issue.Message));

        try
        {
            result.Steps = await BuildStepPreviewAsync(release, context, selectedMachines, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to build step preview for release {ReleaseId}", release.Id);
            blockingReasons.Add($"Deployment process preview failed: {ex.Message}");
        }

        if (HasNoMatchingTargetsForApplicableSteps(result.Steps))
        {
            blockingReasons.Add("No target machines match the required roles for any runnable step.");
        }

        return FinalizePreview(result, blockingReasons);
    }

    private static DeploymentPreviewResult FinalizePreview(DeploymentPreviewResult result, List<string> blockingReasons)
    {
        result.BlockingReasons = blockingReasons.Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.CanDeploy = result.BlockingReasons.Count == 0;

        return result;
    }

    private async Task PopulateLifecycleValidationAsync(DeploymentPreviewResult result, Persistence.Entities.Deployments.Release release, DeploymentValidationContext context, List<string> blockingReasons, CancellationToken cancellationToken)
    {
        try
        {
            var lifecycle = await _lifecycleResolver
                .ResolveLifecycleAsync(release.ProjectId, release.ChannelId, cancellationToken).ConfigureAwait(false);

            result.LifecycleId = lifecycle.Id;

            var progression = await _progressionEvaluator
                .EvaluateProgressionAsync(lifecycle.Id, release.ProjectId, cancellationToken).ConfigureAwait(false);

            result.AllowedEnvironmentIds = progression.AllowedEnvironmentIds;

            if (progression.AllowedEnvironmentIds.Contains(context.EnvironmentId))
                return;

            var allowedText = progression.AllowedEnvironmentIds.Count == 0
                ? "<none>"
                : string.Join(", ", progression.AllowedEnvironmentIds);

            blockingReasons.Add(
                $"Environment {context.EnvironmentId} is not allowed by lifecycle {lifecycle.Id} progression. Allowed: {allowedText}.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Lifecycle validation failed for release {ReleaseId} and environment {EnvironmentId}", context.ReleaseId, context.EnvironmentId);
            blockingReasons.Add($"Lifecycle validation failed: {ex.Message}");
        }
    }

    private async Task<List<DeploymentPreviewStepResult>> BuildStepPreviewAsync(Persistence.Entities.Deployments.Release release, DeploymentValidationContext context, List<Persistence.Entities.Deployments.Machine> selectedMachines, CancellationToken cancellationToken)
    {
        if (release.ProjectDeploymentProcessSnapshotId <= 0)
            throw new InvalidOperationException($"Release {release.Id} has no deployment process snapshot.");
        
        var processSnapshot = await _deploymentSnapshotService
            .LoadProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, cancellationToken).ConfigureAwait(false);

        var steps = ProcessSnapshotStepConverter.Convert(processSnapshot).OrderBy(step => step.StepOrder).ToList();

        return steps.Select(step => BuildStepPreview(step, release.ChannelId, context, selectedMachines)).ToList();
    }

    private static DeploymentPreviewStepResult BuildStepPreview(Squid.Message.Models.Deployments.Process.DeploymentStepDto step, int releaseChannelId, DeploymentValidationContext context, List<Persistence.Entities.Deployments.Machine> selectedMachines)
    {
        var result = new DeploymentPreviewStepResult
        {
            StepId = step.Id,
            StepOrder = step.StepOrder,
            StepName = step.Name,
            IsDisabled = step.IsDisabled
        };

        if (step.IsDisabled)
        {
            result.Reason = "Step is disabled.";
            return result;
        }

        var runnableActions = step.Actions
            .Where(action => StepEligibilityEvaluator.ShouldExecuteAction(action, context.EnvironmentId, releaseChannelId))
            .Where(action => !context.SkipActionIds.Contains(action.Id))
            .OrderBy(action => action.ActionOrder)
            .ToList();

        result.RunnableActionIds = runnableActions.Select(action => action.Id).ToList();

        if (runnableActions.Count == 0)
        {
            result.Reason = "No runnable actions for the selected environment/channel after skip filters.";
            return result;
        }

        result.IsApplicable = true;
        result.RequiredRoles = ExtractRequiredRoles(step);

        var matchedMachines = result.RequiredRoles.Count == 0
            ? selectedMachines
            : DeploymentTargetFinder.FilterByRoles(selectedMachines, result.RequiredRoles.ToHashSet(StringComparer.OrdinalIgnoreCase));

        result.MatchedTargets = matchedMachines
            .OrderBy(machine => machine.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToPreviewTarget)
            .ToList();

        if (result.MatchedTargets.Count > 0)
            return result;

        result.Reason = result.RequiredRoles.Count == 0
            ? "No available machines remain after machine selection constraints."
            : $"No machines match required roles: {string.Join(", ", result.RequiredRoles)}.";

        return result;
    }

    private static List<string> ExtractRequiredRoles(Squid.Message.Models.Deployments.Process.DeploymentStepDto step)
    {
        var stepRolesProperty = step.Properties?
            .FirstOrDefault(property => property.PropertyName == DeploymentVariables.Action.TargetRoles);

        if (stepRolesProperty == null || string.IsNullOrWhiteSpace(stepRolesProperty.PropertyValue))
            return [];

        return DeploymentTargetFinder.ParseCsvRoles(stepRolesProperty.PropertyValue)
            .OrderBy(role => role, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasNoMatchingTargetsForApplicableSteps(List<DeploymentPreviewStepResult> steps)
    {
        var applicableSteps = steps
            .Where(step => step.IsApplicable)
            .ToList();

        return applicableSteps.Count > 0 && applicableSteps.All(step => step.MatchedTargets.Count == 0);
    }

    private static DeploymentPreviewTargetResult ToPreviewTarget(Persistence.Entities.Deployments.Machine machine)
    {
        return new DeploymentPreviewTargetResult
        {
            MachineId = machine.Id,
            MachineName = machine.Name,
            Roles = DeploymentTargetFinder.ParseRoles(machine.Roles).OrderBy(role => role, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static DeploymentRequestPayload NormalizeRequestPayload(DeploymentRequestPayload payload)
    {
        payload ??= new DeploymentRequestPayload();

        var queueTime = NormalizeUtc(payload.QueueTime);
        var queueTimeExpiry = NormalizeUtc(payload.QueueTimeExpiry);

        return new DeploymentRequestPayload
        {
            ReleaseId = payload.ReleaseId,
            EnvironmentId = payload.EnvironmentId,
            Name = payload.Name,
            Comments = payload.Comments,
            ForcePackageDownload = payload.ForcePackageDownload,
            ForcePackageRedeployment = payload.ForcePackageRedeployment,
            UseGuidedFailure = payload.UseGuidedFailure,
            QueueTime = queueTime,
            QueueTimeExpiry = queueTimeExpiry,
            FormValues = payload.FormValues ?? new Dictionary<string, string>(),
            SpecificMachineIds = NormalizePositiveIds(payload.SpecificMachineIds).OrderBy(id => id).ToList(),
            ExcludedMachineIds = NormalizePositiveIds(payload.ExcludedMachineIds).OrderBy(id => id).ToList(),
            SkipActionIds = NormalizePositiveIds(payload.SkipActionIds).OrderBy(id => id).ToList()
        };
    }

    private static DeploymentValidationContext BuildValidationContext(DeploymentRequestPayload payload)
    {
        return new DeploymentValidationContext
        {
            ReleaseId = payload.ReleaseId,
            EnvironmentId = payload.EnvironmentId,
            QueueTime = payload.QueueTime,
            QueueTimeExpiry = payload.QueueTimeExpiry,
            SpecificMachineIds = NormalizePositiveIds(payload.SpecificMachineIds),
            ExcludedMachineIds = NormalizePositiveIds(payload.ExcludedMachineIds),
            SkipActionIds = NormalizePositiveIds(payload.SkipActionIds)
        };
    }

    private static List<Persistence.Entities.Deployments.Machine> ApplyMachineSelection(List<Persistence.Entities.Deployments.Machine> machines, HashSet<int> specificMachineIds, HashSet<int> excludedMachineIds)
    {
        if (machines.Count == 0)
            return machines;

        var selected = machines;

        if (specificMachineIds.Count > 0)
            selected = selected.Where(machine => specificMachineIds.Contains(machine.Id)).ToList();

        if (excludedMachineIds.Count > 0)
            selected = selected.Where(machine => !excludedMachineIds.Contains(machine.Id)).ToList();

        return selected;
    }
}

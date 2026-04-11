using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Validation;
using Squid.Message.Models.Deployments.Deployment;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

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

        await PopulateLifecycleValidationAsync(result, release, context, blockingReasons, cancellationToken).ConfigureAwait(false);

        var ruleReport = await _deploymentValidationOrchestrator.ValidateAsync(stage, context, cancellationToken).ConfigureAwait(false);

        blockingReasons.AddRange(ruleReport.Issues.Where(issue => issue.IsBlocking).Select(issue => issue.Message));

        await PopulateStepsAndBlockersFromPlanAsync(result, release, context, selectedMachines, blockingReasons, cancellationToken).ConfigureAwait(false);

        return FinalizePreview(result, blockingReasons);
    }

    // ---------- planner adapter ------------------------------------------

    private async Task PopulateStepsAndBlockersFromPlanAsync(
        DeploymentPreviewResult result,
        Persistence.Entities.Deployments.Release release,
        DeploymentValidationContext context,
        List<Persistence.Entities.Deployments.Machine> selectedMachines,
        List<string> blockingReasons,
        CancellationToken cancellationToken)
    {
        DeploymentPlan plan;

        try
        {
            plan = await BuildPlanAsync(release, context, selectedMachines, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to build deployment plan for release {ReleaseId}", release.Id);
            blockingReasons.Add($"Deployment process preview failed: {ex.Message}");
            return;
        }

        result.Steps = plan.Steps.Select(MapPlannedStep).ToList();
        result.CandidateTargets = plan.CandidateTargets.Select(MapPlannedTarget).ToList();
        result.AvailableMachineCount = result.CandidateTargets.Count;

        blockingReasons.AddRange(plan.BlockingReasons.Select(reason => reason.Message));
    }

    private async Task<DeploymentPlan> BuildPlanAsync(
        Persistence.Entities.Deployments.Release release,
        DeploymentValidationContext context,
        List<Persistence.Entities.Deployments.Machine> selectedMachines,
        CancellationToken cancellationToken)
    {
        if (release.ProjectDeploymentProcessSnapshotId <= 0)
            throw new InvalidOperationException($"Release {release.Id} has no deployment process snapshot.");

        var processSnapshot = await _deploymentSnapshotService
            .LoadProcessSnapshotAsync(release.ProjectDeploymentProcessSnapshotId, cancellationToken).ConfigureAwait(false);

        var steps = ProcessSnapshotStepConverter.Convert(processSnapshot);
        var targetContexts = BuildPreviewTargetContexts(selectedMachines);

        var request = new DeploymentPlanRequest
        {
            Mode = PlanMode.Preview,
            ReleaseId = release.Id,
            EnvironmentId = context.EnvironmentId,
            ChannelId = release.ChannelId,
            DeploymentProcessSnapshotId = processSnapshot.Id,
            Steps = steps,
            Variables = Array.Empty<VariableDto>(),
            TargetContexts = targetContexts,
            SkipActionIds = context.SkipActionIds
        };

        return await _deploymentPlanner.PlanAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private List<DeploymentTargetContext> BuildPreviewTargetContexts(List<Persistence.Entities.Deployments.Machine> machines)
    {
        return machines.Select(BuildPreviewTargetContext).ToList();
    }

    private DeploymentTargetContext BuildPreviewTargetContext(Persistence.Entities.Deployments.Machine machine)
    {
        var style = CommunicationStyleParser.Parse(machine.Endpoint);

        return new DeploymentTargetContext
        {
            Machine = machine,
            CommunicationStyle = style,
            Transport = _transportRegistry.Resolve(style),
            EndpointContext = new EndpointContext { EndpointJson = machine.Endpoint ?? string.Empty }
        };
    }

    // ---------- plan -> preview mapping ----------------------------------

    internal static DeploymentPreviewStepResult MapPlannedStep(PlannedStep step)
    {
        var result = new DeploymentPreviewStepResult
        {
            StepId = step.StepId,
            StepOrder = step.StepOrder,
            StepName = step.StepName,
            IsDisabled = step.Status == PlannedStepStatus.Disabled,
            RequiredRoles = step.RequiredRoles.ToList(),
            RunnableActionIds = step.Actions.Select(a => a.ActionId).ToList()
        };

        ApplyStatusToPreviewStep(step, result);

        return result;
    }

    private static void ApplyStatusToPreviewStep(PlannedStep step, DeploymentPreviewStepResult result)
    {
        switch (step.Status)
        {
            case PlannedStepStatus.Applicable:
                result.IsApplicable = true;
                result.MatchedTargets = step.MatchedTargets.Select(MapPlannedTarget).ToList();
                return;

            case PlannedStepStatus.StepLevelOnly:
                result.IsApplicable = true;
                result.IsStepLevelOnly = true;
                return;

            case PlannedStepStatus.RunOnServer:
                result.IsApplicable = true;
                result.IsRunOnServer = true;
                return;

            case PlannedStepStatus.NoMatchingTargets:
                result.IsApplicable = true;
                result.Reason = step.StatusMessage;
                return;

            case PlannedStepStatus.Disabled:
                result.Reason = step.StatusMessage;
                return;

            case PlannedStepStatus.NoRunnableActions:
            case PlannedStepStatus.ConditionNotMet:
            default:
                result.Reason = step.StatusMessage;
                return;
        }
    }

    private static DeploymentPreviewTargetResult MapPlannedTarget(PlannedTarget target) => new()
    {
        MachineId = target.MachineId,
        MachineName = target.MachineName,
        Roles = target.Roles.ToList()
    };

    // ---------- finalize / lifecycle / selection -------------------------

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
                .EvaluateProgressionForReleaseAsync(lifecycle.Id, release.Id, cancellationToken).ConfigureAwait(false);

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

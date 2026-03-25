using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

internal enum ActionDirective
{
    Execute,
    Skip,
    Abort
}

public sealed partial class ExecuteStepsPhase
{
    private async Task<ActionDirective> ResolveResumeDirectiveAsync(DeploymentStepDto step, ActionExecutionResult actionResult, DeploymentTargetContext tc, int stepDisplayOrder, CancellationToken ct)
    {
        if (!_ctx.IsResume || !_ctx.UseGuidedFailure) return ActionDirective.Execute;

        var outcome = await ResolveGuidedFailureOnResumeAsync(step, actionResult, tc, stepDisplayOrder, ct).ConfigureAwait(false);

        return outcome switch
        {
            InterruptionOutcome.Skip => ActionDirective.Skip,
            InterruptionOutcome.Abort => ActionDirective.Abort,
            InterruptionOutcome.ExcludeMachine => ApplyExcludeAndSkip(tc),
            _ => ActionDirective.Execute
        };
    }

    private async Task<InterruptionOutcome?> ResolveGuidedFailureOnResumeAsync(DeploymentStepDto step, ActionExecutionResult actionResult, DeploymentTargetContext tc, int stepDisplayOrder, CancellationToken ct)
    {
        var existing = await interruptionService.FindResolvedInterruptionAsync(_ctx.ServerTaskId, step.Name, actionResult.ActionName, tc.Machine.Name, ct).ConfigureAwait(false);

        if (existing == null) return null;

        var outcome = Enum.TryParse<InterruptionOutcome>(existing.Resolution, true, out var parsed) ? parsed : (InterruptionOutcome?)null;

        if (outcome.HasValue)
        {
            await lifecycle.EmitAsync(new GuidedFailureResolvedEvent(new DeploymentEventContext
            {
                StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
                GuidedFailureResolution = outcome.Value.ToString(),
                InterruptionType = InterruptionType.GuidedFailure
            }), ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task HandleGuidedFailureAsync(DeploymentStepDto step, ActionExecutionResult actionResult, DeploymentTargetContext tc, Exception ex, int stepDisplayOrder, int actionSortOrder, CancellationToken ct)
    {
        var form = InterruptionFormBuilder.BuildGuidedFailureForm(step.Name, actionResult.ActionName, tc.Machine.Name, ex.Message);

        await lifecycle.EmitAsync(new GuidedFailurePromptEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            ActionName = actionResult.ActionName, MachineName = tc.Machine.Name,
            ActionSortOrder = actionSortOrder, Error = ex.Message,
            InterruptionType = InterruptionType.GuidedFailure
        }), ct).ConfigureAwait(false);

        await interruptionService.CreateInterruptionAsync(new CreateInterruptionRequest
        {
            ServerTaskId = _ctx.ServerTaskId, DeploymentId = _ctx.Deployment.Id,
            InterruptionType = InterruptionType.GuidedFailure, StepDisplayOrder = stepDisplayOrder,
            StepName = step.Name, ActionName = actionResult.ActionName,
            MachineName = tc.Machine.Name, ErrorMessage = ex.Message,
            Form = form, SpaceId = _ctx.Deployment.SpaceId
        }, ct).ConfigureAwait(false);

        // Save checkpoint with the last *completed* batch index, not the current (failing) one.
        // On resume, the failing batch replays so the resolved interruption can be applied.
        await PersistCheckpointAsync(_currentBatchIndex - 1, ct).ConfigureAwait(false);

        await serverTaskService.TransitionStateAsync(_ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, ct).ConfigureAwait(false);

        throw new DeploymentSuspendedException(_ctx.ServerTaskId);
    }

    private static ActionDirective ApplyExcludeAndSkip(DeploymentTargetContext tc)
    {
        tc.Exclude("Excluded via guided failure");
        Log.Information("[Deploy] Machine {MachineName} excluded from remaining deployment steps via guided failure", tc.Machine.Name);
        return ActionDirective.Skip;
    }
}

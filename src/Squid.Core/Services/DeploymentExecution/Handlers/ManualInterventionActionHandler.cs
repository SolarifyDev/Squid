using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Constants;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public sealed class ManualInterventionActionHandler(
    IDeploymentInterruptionService interruptionService,
    IServerTaskService serverTaskService,
    IDeploymentLifecycle lifecycle) : IActionHandler
{
    public string ActionType => SpecialVariables.ActionTypes.Manual;

    public ExecutionScope ExecutionScope => ExecutionScope.StepLevel;

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var instructions = ReadInstructions(ctx.Action);

        return Task.FromResult(new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ExecutionMode = ExecutionMode.ManualIntervention,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            ManualInterventionInstructions = instructions
        });
    }

    Task<ExecutionIntent> IActionHandler.DescribeIntentAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var intent = new ManualInterventionIntent
        {
            Name = "manual-intervention",
            StepName = ctx.Step?.Name ?? string.Empty,
            ActionName = ctx.Action?.Name ?? string.Empty,
            Instructions = ReadInstructions(ctx.Action),
            ResponsibleTeamIds = ReadResponsibleTeamIds(ctx.Action)
        };

        return Task.FromResult<ExecutionIntent>(intent);
    }

    private static string ReadInstructions(DeploymentActionDto action)
        => action?.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Action.ManualInstructions)?.PropertyValue ?? string.Empty;

    private static string ReadResponsibleTeamIds(DeploymentActionDto action)
        => action?.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds)?.PropertyValue;

    public async Task ExecuteStepLevelAsync(StepActionContext ctx, CancellationToken ct)
    {
        var existingOutcome = await ResolveExistingInterruptionAsync(ctx, ct).ConfigureAwait(false);

        if (existingOutcome == InterruptionOutcome.Abort)
            throw new DeploymentAbortedException($"Manual intervention aborted for step \"{ctx.Step.Name}\"");

        if (existingOutcome == InterruptionOutcome.Proceed) return;

        await SuspendForInterruptionAsync(ctx, ct).ConfigureAwait(false);
    }

    private async Task<InterruptionOutcome?> ResolveExistingInterruptionAsync(StepActionContext ctx, CancellationToken ct)
    {
        var existing = await interruptionService.FindResolvedInterruptionAsync(ctx.ServerTaskId, ctx.Step.Name, ctx.Action.Name, null, ct).ConfigureAwait(false);

        if (existing == null) return null;

        var outcome = Enum.TryParse<InterruptionOutcome>(existing.Resolution, true, out var parsed) ? parsed : (InterruptionOutcome?)null;

        if (outcome.HasValue)
        {
            await lifecycle.EmitAsync(new ManualInterventionResolvedEvent(new DeploymentEventContext
            {
                StepDisplayOrder = ctx.StepDisplayOrder, StepName = ctx.Step.Name,
                ActionName = ctx.Action.Name, GuidedFailureResolution = outcome.Value.ToString()
            }), ct).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task SuspendForInterruptionAsync(StepActionContext ctx, CancellationToken ct)
    {
        var instructions = ReadInstructions(ctx.Action);
        var responsibleTeamIds = ReadResponsibleTeamIds(ctx.Action);
        var form = InterruptionFormBuilder.BuildManualInterventionForm(instructions);

        await lifecycle.EmitAsync(new ManualInterventionPromptEvent(new DeploymentEventContext
        {
            StepDisplayOrder = ctx.StepDisplayOrder, StepName = ctx.Step.Name,
            ActionName = ctx.Action.Name, InterruptionType = InterruptionType.ManualIntervention
        }), ct).ConfigureAwait(false);

        await interruptionService.CreateInterruptionAsync(new CreateInterruptionRequest
        {
            ServerTaskId = ctx.ServerTaskId, DeploymentId = ctx.DeploymentId,
            InterruptionType = InterruptionType.ManualIntervention, StepDisplayOrder = ctx.StepDisplayOrder,
            StepName = ctx.Step.Name, ActionName = ctx.Action.Name,
            Form = form, SpaceId = ctx.SpaceId,
            ResponsibleTeamIds = responsibleTeamIds
        }, ct).ConfigureAwait(false);

        await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, ct).ConfigureAwait(false);

        throw new DeploymentSuspendedException(ctx.ServerTaskId);
    }
}

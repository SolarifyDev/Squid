using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public sealed class ManualInterventionActionHandler(
    IDeploymentInterruptionService interruptionService,
    IServerTaskService serverTaskService,
    IDeploymentLifecycle lifecycle) : IActionHandler
{
    public DeploymentActionType ActionType => DeploymentActionType.ManualIntervention;

    public ExecutionScope ExecutionScope => ExecutionScope.StepLevel;

    public bool CanHandle(DeploymentActionDto action) => DeploymentActionTypeParser.Is(action?.ActionType, ActionType);

    public Task<ActionExecutionResult> PrepareAsync(ActionExecutionContext ctx, CancellationToken ct)
    {
        var instructions = ctx.Action.Properties?.FirstOrDefault(p => p.PropertyName == "Squid.Action.Manual.Instructions")?.PropertyValue ?? "";

        return Task.FromResult(new ActionExecutionResult
        {
            ActionName = ctx.Action.Name,
            ExecutionMode = ExecutionMode.ManualIntervention,
            ContextPreparationPolicy = ContextPreparationPolicy.Skip,
            ManualInterventionInstructions = instructions
        });
    }

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
        var existing = await interruptionService.FindResolvedInterruptionAsync(ctx.ServerTaskId, ctx.Step.Name, ctx.Action.Name, ct).ConfigureAwait(false);

        if (existing == null) return null;

        return Enum.TryParse<InterruptionOutcome>(existing.Resolution, true, out var outcome) ? outcome : null;
    }

    private async Task SuspendForInterruptionAsync(StepActionContext ctx, CancellationToken ct)
    {
        var instructions = ctx.Action.Properties?.FirstOrDefault(p => p.PropertyName == "Squid.Action.Manual.Instructions")?.PropertyValue ?? "";
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
            Form = form, SpaceId = ctx.SpaceId
        }, ct).ConfigureAwait(false);

        await serverTaskService.TransitionStateAsync(ctx.ServerTaskId, TaskState.Executing, TaskState.Paused, ct).ConfigureAwait(false);

        throw new DeploymentSuspendedException(ctx.ServerTaskId);
    }
}

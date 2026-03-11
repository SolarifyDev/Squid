using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Handlers;

public sealed class ManualInterventionActionHandler : IActionHandler
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
}

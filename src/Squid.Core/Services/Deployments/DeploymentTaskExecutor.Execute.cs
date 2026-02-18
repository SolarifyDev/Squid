using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.Deployments;

public partial class DeploymentTaskExecutor
{
    private async Task PrepareAndExecuteStepsAsync(CancellationToken ct)
    {
        var targetRoles = DeploymentTargetFinder.ParseRoles(_ctx.Target.Roles);
        var failureEncountered = false;
        var variableDictionary = VariableDictionaryFactory.Create(_ctx.Variables);
        var stepSortOrder = 0;

        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);

        foreach (var batch in batches)
        {
            if (batch.Count == 1)
            {
                var step = batch[0];
                var result = await ExecuteStepAsync(step, variableDictionary, targetRoles, failureEncountered,
                        ++stepSortOrder, ct)
                    .ConfigureAwait(false);
                failureEncountered = MergeStepResult(result, failureEncountered);
            }
            else
            {
                var tasks = batch.Select(step =>
                    ExecuteStepAsync(step, variableDictionary, targetRoles, failureEncountered,
                        ++stepSortOrder, ct)).ToList();
                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var result in results)
                    failureEncountered = MergeStepResult(result, failureEncountered);
            }

            variableDictionary = VariableDictionaryFactory.Create(_ctx.Variables);
        }
    }

    private async Task<StepExecutionResult> ExecuteStepAsync(
        DeploymentStepDto step,
        VariableDictionary variableDictionary,
        HashSet<string> targetRoles,
        bool failureEncountered,
        int stepSortOrder,
        CancellationToken ct)
    {
        var result = new StepExecutionResult();

        if (!ShouldExecuteStep(step, targetRoles, previousStepSucceeded: !failureEncountered))
        {
            Log.Information("Skipping step {StepName} (disabled, condition, or role mismatch)", step.Name);
            return result;
        }

        var stepActivityNode = await CreateActivityNodeAsync(
            _ctx.Task.Id, _ctx.TaskActivityNode?.Id, step.Name, "Step", "Running", stepSortOrder, ct)
            .ConfigureAwait(false);

        var stepResults = await PrepareStepActionsAsync(step, variableDictionary, ct).ConfigureAwait(false);
        await ExecuteActionResultsAsync(stepResults, step, stepActivityNode, result, ct).ConfigureAwait(false);

        if (stepActivityNode != null)
            await _activityLogDataProvider.UpdateNodeStatusAsync(
                stepActivityNode.Id, result.Failed ? "Failed" : "Success", DateTimeOffset.UtcNow, ct: ct)
                .ConfigureAwait(false);

        return result;
    }

    private async Task<List<ActionExecutionResult>> PrepareStepActionsAsync(
        DeploymentStepDto step,
        VariableDictionary variableDictionary,
        CancellationToken ct)
    {
        var stepResults = new List<ActionExecutionResult>();

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            if (!ShouldExecuteAction(action, _ctx.Deployment.EnvironmentId, _ctx.Deployment.ChannelId))
            {
                Log.Information("Skipping action {ActionName} (disabled, environment, or channel mismatch)", action.Name);
                continue;
            }

            var handler = _actionHandlerRegistry.Resolve(action);

            if (handler == null)
            {
                Log.Warning("No handler found for action {ActionType}, skipping", action.ActionType);
                continue;
            }

            var expandedAction = VariableExpander.ExpandActionProperties(action, variableDictionary);

            var context = new ActionExecutionContext
            {
                Step = step,
                Action = expandedAction,
                Variables = _ctx.Variables,
                ReleaseVersion = _ctx.Release?.Version
            };

            var prepared = await handler.PrepareAsync(context, ct).ConfigureAwait(false);

            if (prepared != null)
            {
                if (prepared.ScriptBody != null)
                    prepared.ScriptBody = VariableExpander.ExpandString(prepared.ScriptBody, variableDictionary);

                if (prepared.CalamariCommand == null)
                    WrapScriptIfApplicable(prepared);

                stepResults.Add(prepared);
            }
        }

        return stepResults;
    }

    private void WrapScriptIfApplicable(ActionExecutionResult prepared)
    {
        var wrapper = _scriptWrappers.FirstOrDefault(w => w.CanWrap(_ctx.CommunicationStyle));

        if (wrapper != null)
            prepared.ScriptBody = wrapper.WrapScript(
                prepared.ScriptBody, _ctx.EndpointJson, _ctx.Account,
                prepared.Syntax, _ctx.Variables);
    }

    private async Task ExecuteActionResultsAsync(
        List<ActionExecutionResult> stepResults,
        DeploymentStepDto step,
        Persistence.Entities.Deployments.ActivityLog stepActivityNode,
        StepExecutionResult result,
        CancellationToken ct)
    {
        var actionSortOrder = 0;

        foreach (var actionResult in stepResults)
        {
            var actionActivityNode = await CreateActivityNodeAsync(
                _ctx.Task.Id, stepActivityNode?.Id, actionResult.CalamariCommand ?? "Direct Script",
                "Action", "Running", ++actionSortOrder, ct).ConfigureAwait(false);

            try
            {
                if (actionResult.CalamariCommand != null)
                    await ExecuteCalamariActionAsync(actionResult, ct).ConfigureAwait(false);
                else
                    await ExecuteDirectScriptAsync(actionResult, ct).ConfigureAwait(false);

                result.ActionResults.Add(actionResult);

                if (actionActivityNode != null)
                    await _activityLogDataProvider.UpdateNodeStatusAsync(
                        actionActivityNode.Id, "Success", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

                CollectOutputVariables(result, step.Name, actionResult);
            }
            catch (Exception ex)
            {
                result.Failed = true;
                Log.Error(ex, "Action failed in step {StepName}: {Error}", step.Name, ex.Message);

                if (actionActivityNode != null)
                    await _activityLogDataProvider.UpdateNodeStatusAsync(
                        actionActivityNode.Id, "Failed", DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

                if (step.IsRequired)
                    throw;
            }
        }
    }

    private static void CollectOutputVariables(
        StepExecutionResult result, string stepName, ActionExecutionResult actionResult)
    {
        foreach (var kv in actionResult.OutputVariables)
        {
            var qualifiedName = DeploymentVariables.Action.OutputVariable(stepName, kv.Key);
            result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = kv.Value });
            result.OutputVariables.Add(new VariableDto { Name = kv.Key, Value = kv.Value });
        }
    }

    private bool MergeStepResult(StepExecutionResult result, bool failureEncountered)
    {
        _ctx.ActionResults.AddRange(result.ActionResults);

        if (result.OutputVariables.Count > 0)
        {
            _ctx.Variables.AddRange(result.OutputVariables);
            foreach (var v in result.OutputVariables)
                Log.Information("Output variable captured: {Name} = {Value}", v.Name, v.Value);
        }

        return failureEncountered || result.Failed;
    }

    private class StepExecutionResult
    {
        public List<ActionExecutionResult> ActionResults { get; } = new();
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }
    }
}

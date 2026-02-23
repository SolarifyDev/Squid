using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Pipeline;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution;

public partial class DeploymentTaskExecutor
{
    private async Task ExecuteDeploymentStepsAsync(CancellationToken ct)
    {
        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);
        var stepSortOrder = 0;

        foreach (var batch in batches)
        {
            if (batch.Count == 1)
            {
                await ExecuteStepAcrossTargetsAsync(batch[0], ++stepSortOrder, ct).ConfigureAwait(false);
            }
            else
            {
                var tasks = batch.Select(step =>
                    ExecuteStepAcrossTargetsAsync(step, ++stepSortOrder, ct)).ToList();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteStepAcrossTargetsAsync(
        DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        var matchingTargets = FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);

        foreach (var tc in matchingTargets)
        {
            _ctx.CurrentDeployTargetContext = tc;
            var targetRoles = DeploymentTargetFinder.ParseRoles(tc.Machine.Roles);
            var effectiveVariables = BuildEffectiveVariables(_ctx.Variables, tc);

            if (!ShouldExecuteStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, effectiveVariables))
            {
                Log.Information("Skipping step {StepName} on target {TargetName}", step.Name, tc.Machine.Name);
                continue;
            }

            var variableDictionary = VariableDictionaryFactory.Create(effectiveVariables);

            var stepActivityNode = await CreateActivityNodeAsync(
                _ctx.Task.Id, _ctx.TaskActivityNode?.Id, step.Name, "Step", "Running",
                stepSortOrder, ct).ConfigureAwait(false);

            var stepResults = await PrepareStepActionsAsync(step, variableDictionary, effectiveVariables, ct).ConfigureAwait(false);
            var result = new StepExecutionResult();
            await ExecuteActionResultsAsync(stepResults, step, stepActivityNode, result, ct).ConfigureAwait(false);

            if (stepActivityNode != null)
                await _activityLogDataProvider.UpdateNodeStatusAsync(
                    stepActivityNode.Id, result.Failed ? "Failed" : "Success",
                    DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

            MergeStepResult(result);
        }
    }

    public static List<DeploymentTargetContext> FindMatchingTargetsForStep(
        DeploymentStepDto step, List<DeploymentTargetContext> allTargets)
        => TargetStepMatcher.FindMatchingTargetsForStep(step, allTargets);

    public static List<VariableDto> BuildEffectiveVariables(
        List<VariableDto> baseVariables, DeploymentTargetContext target)
        => EffectiveVariableBuilder.BuildEffectiveVariables(baseVariables, target);

    private List<VariableDto> BuildActionVariables(List<VariableDto> effectiveVariables, DeploymentActionDto action)
        => EffectiveVariableBuilder.BuildActionVariables(effectiveVariables, action, _ctx.SelectedPackages);

    private async Task<List<ActionExecutionResult>> PrepareStepActionsAsync(
        DeploymentStepDto step,
        VariableDictionary variableDictionary,
        List<VariableDto> effectiveVariables,
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

            var actionVariables = BuildActionVariables(effectiveVariables, action);
            var variableDictionaryForAction = VariableDictionaryFactory.Create(actionVariables);
            var expandedAction = VariableExpander.ExpandActionProperties(action, variableDictionaryForAction);

            var context = new ActionExecutionContext
            {
                Step = step,
                Action = expandedAction,
                Variables = actionVariables,
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
        var tc = _ctx.CurrentDeployTargetContext;
        var wrapper = _scriptWrappers.FirstOrDefault(w => w.CanWrap(tc.CommunicationStyle));

        if (wrapper == null) return;

        var effectiveVariables = BuildEffectiveVariables(_ctx.Variables, tc);

        prepared.ScriptBody = wrapper.WrapScript(
            prepared.ScriptBody, tc.EndpointJson, tc.Account,
            prepared.Syntax, effectiveVariables);
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
                var strategy = _ctx.CurrentDeployTargetContext.ResolvedStrategy;

                if (strategy == null)
                    throw new DeploymentTargetException(
                        $"No execution strategy for {_ctx.CurrentDeployTargetContext.CommunicationStyle}");

                var request = BuildScriptExecutionRequest(actionResult);
                var execResult = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

                CaptureOutputVariables(actionResult, execResult.LogLines);

                if (!execResult.Success)
                    throw new DeploymentScriptException("Script execution failed", _ctx.Deployment.Id);

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

    private ScriptExecutionRequest BuildScriptExecutionRequest(ActionExecutionResult actionResult)
    {
        var effectiveVariables = BuildEffectiveVariables(_ctx.Variables, _ctx.CurrentDeployTargetContext);

        return new ScriptExecutionRequest
        {
            ScriptBody = actionResult.ScriptBody,
            Files = actionResult.Files,
            CalamariCommand = actionResult.CalamariCommand,
            Variables = effectiveVariables,
            Machine = _ctx.CurrentDeployTargetContext.Machine,
            ReleaseVersion = _ctx.Release?.Version,
            CalamariPackageBytes = _ctx.CalamariPackageBytes
        };
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

    private void MergeStepResult(StepExecutionResult result)
    {
        _ctx.CurrentDeployTargetContext.ActionResults.AddRange(result.ActionResults);

        if (result.OutputVariables.Count > 0)
        {
            _ctx.Variables.AddRange(result.OutputVariables);
            foreach (var v in result.OutputVariables)
                Log.Information("Output variable captured: {Name} = {Value}", v.Name, v.Value);
        }

        _ctx.FailureEncountered = _ctx.FailureEncountered || result.Failed;
    }

    private class StepExecutionResult
    {
        public List<ActionExecutionResult> ActionResults { get; } = new();
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }
    }
}

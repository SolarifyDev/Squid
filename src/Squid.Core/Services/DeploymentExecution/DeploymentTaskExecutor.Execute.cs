using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Enums;
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
            var batchResults = batch.Count == 1
                ? [await ExecuteStepAcrossTargetsAsync(batch[0], ++stepSortOrder, ct).ConfigureAwait(false)]
                : await Task.WhenAll(batch.Select(step =>
                    ExecuteStepAcrossTargetsAsync(step, ++stepSortOrder, ct))).ConfigureAwait(false);

            ApplyBatchResults(batchResults);
        }
    }

    private async Task<StepExecutionResult> ExecuteStepAcrossTargetsAsync(
        DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        var stepResult = new StepExecutionResult();
        var matchingTargets = FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);

        foreach (var tc in matchingTargets)
        {
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

            var actionResults = await PrepareStepActionsAsync(
                step, variableDictionary, effectiveVariables, tc, ct).ConfigureAwait(false);

            var result = new StepExecutionResult();
            await ExecuteActionResultsAsync(
                actionResults, step, stepActivityNode, result, tc, effectiveVariables, ct).ConfigureAwait(false);

            if (stepActivityNode != null)
                await _activityLogDataProvider.UpdateNodeStatusAsync(
                    stepActivityNode.Id, result.Failed ? "Failed" : "Success",
                    DateTimeOffset.UtcNow, ct: ct).ConfigureAwait(false);

            stepResult.Absorb(result);
        }

        return stepResult;
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
        DeploymentTargetContext tc,
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
                    WrapScriptIfApplicable(prepared, tc, effectiveVariables);

                stepResults.Add(prepared);
            }
        }

        return stepResults;
    }

    private static void WrapScriptIfApplicable(
        ActionExecutionResult prepared, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
    {
        var wrapper = tc.Transport?.ScriptWrapper;

        if (wrapper == null) return;

        prepared.ScriptBody = wrapper.WrapScript(
            prepared.ScriptBody, tc.EndpointJson, tc.Account,
            prepared.Syntax, effectiveVariables);
    }

    private async Task ExecuteActionResultsAsync(
        List<ActionExecutionResult> stepResults,
        DeploymentStepDto step,
        Persistence.Entities.Deployments.ActivityLog stepActivityNode,
        StepExecutionResult result,
        DeploymentTargetContext tc,
        List<VariableDto> effectiveVariables,
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
                var strategy = tc.Transport?.Strategy;

                if (strategy == null)
                    throw new DeploymentTargetException(
                        $"No execution strategy for {tc.CommunicationStyle}");

                var request = BuildScriptExecutionRequest(actionResult, tc, effectiveVariables);
                var execResult = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

                CaptureOutputVariables(actionResult, execResult.LogLines);

                if (!execResult.Success)
                    throw new DeploymentScriptException("Script execution failed", _ctx.Deployment.Id);

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

    private ScriptExecutionRequest BuildScriptExecutionRequest(
        ActionExecutionResult actionResult, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
    {
        return new ScriptExecutionRequest
        {
            ScriptBody = actionResult.ScriptBody,
            Files = actionResult.Files,
            CalamariCommand = actionResult.CalamariCommand,
            Variables = effectiveVariables,
            Machine = tc.Machine,
            ReleaseVersion = _ctx.Release?.Version,
            CalamariPackageBytes = _ctx.CalamariPackageBytes
        };
    }

    private static void CaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);

        foreach (var kv in outputVars)
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;
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

    private void ApplyBatchResults(IEnumerable<StepExecutionResult> results)
    {
        foreach (var result in results)
        {
            if (result.OutputVariables.Count > 0)
            {
                _ctx.Variables.AddRange(result.OutputVariables);
                foreach (var v in result.OutputVariables)
                    Log.Information("Output variable captured: {Name} = {Value}", v.Name, v.Value);
            }

            _ctx.FailureEncountered |= result.Failed;
        }
    }

    private class StepExecutionResult
    {
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }

        public void Absorb(StepExecutionResult other)
        {
            OutputVariables.AddRange(other.OutputVariables);
            Failed |= other.Failed;
        }
    }
}

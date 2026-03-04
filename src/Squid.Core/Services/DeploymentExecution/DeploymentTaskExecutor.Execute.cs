using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.VariableSubstitution;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;
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
            var scopeContext = new VariableScopeContext
            {
                EnvironmentId = _ctx.Deployment.EnvironmentId,
                MachineId = tc.Machine.Id,
                Roles = targetRoles,
                ChannelId = _ctx.Deployment.ChannelId
            };
            var effectiveVariables = BuildEffectiveVariables(_ctx.Variables, tc, scopeContext);

            if (!ShouldExecuteStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, effectiveVariables))
            {
                Log.Information("Skipping step {StepName} on target {TargetName}", step.Name, tc.Machine.Name);
                continue;
            }

            var variableDictionary = VariableDictionaryFactory.Create(effectiveVariables);

            var stepActivityNode = await CreateActivityNodeAsync(
                _ctx.Task.Id, _ctx.TaskActivityNode?.Id, step.Name, DeploymentActivityLogNodeType.Step, DeploymentActivityLogNodeStatus.Running,
                stepSortOrder, ct).ConfigureAwait(false);

            var actionResults = await PrepareStepActionsAsync(
                step, variableDictionary, effectiveVariables, tc, ct).ConfigureAwait(false);

            var result = new StepExecutionResult();
            await ExecuteActionResultsAsync(
                actionResults, step, stepActivityNode, result, tc, effectiveVariables, ct).ConfigureAwait(false);

            await UpdateActivityNodeStatusAsync(
                stepActivityNode,
                result.Failed ? DeploymentActivityLogNodeStatus.Failed : DeploymentActivityLogNodeStatus.Success,
                ct).ConfigureAwait(false);

            stepResult.Absorb(result);
        }

        return stepResult;
    }

    public static List<DeploymentTargetContext> FindMatchingTargetsForStep(
        DeploymentStepDto step, List<DeploymentTargetContext> allTargets)
        => TargetStepMatcher.FindMatchingTargetsForStep(step, allTargets);

    public static List<VariableDto> BuildEffectiveVariables(
        List<VariableDto> baseVariables, DeploymentTargetContext target, VariableScopeContext scopeContext)
        => EffectiveVariableBuilder.BuildEffectiveVariables(baseVariables, target, scopeContext);

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
                prepared.ActionProperties = BuildActionPropertyDictionary(expandedAction);

                var executionMode = prepared.ResolveExecutionMode();
                var contextPreparationPolicy = ResolveContextPreparationPolicy(prepared, tc);

                if (prepared.ScriptBody != null)
                    prepared.ScriptBody = VariableExpander.ExpandString(prepared.ScriptBody, variableDictionary);

                // Direct script can be wrapped here. Packaged payloads are wrapped later after payload template paths are resolved.
                if (executionMode == ExecutionMode.DirectScript
                    && contextPreparationPolicy == ContextPreparationPolicy.Apply)
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

        var scriptContext = new ScriptContext
        {
            Endpoint = tc.EndpointContext,
            Syntax = prepared.Syntax,
            Variables = effectiveVariables,
            ActionProperties = prepared.ActionProperties
        };

        prepared.ScriptBody = wrapper.WrapScript(prepared.ScriptBody, scriptContext);
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
                DeploymentActivityLogNodeType.Action, DeploymentActivityLogNodeStatus.Running, ++actionSortOrder, ct).ConfigureAwait(false);

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

                await UpdateActivityNodeStatusAsync(actionActivityNode, DeploymentActivityLogNodeStatus.Success, ct)
                    .ConfigureAwait(false);

                CollectOutputVariables(result, step.Name, actionResult);
            }
            catch (Exception ex)
            {
                result.Failed = true;
                Log.Error(ex, "Action failed in step {StepName}: {Error}", step.Name, ex.Message);

                await UpdateActivityNodeStatusAsync(actionActivityNode, DeploymentActivityLogNodeStatus.Failed, ct)
                    .ConfigureAwait(false);

                if (step.IsRequired)
                    throw;
            }
        }
    }

    private ScriptExecutionRequest BuildScriptExecutionRequest(
        ActionExecutionResult actionResult, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
    {
        var resolvedMode = actionResult.ResolveExecutionMode();
        var resolvedContextPreparationPolicy = ResolveContextPreparationPolicy(actionResult, tc);

        return new ScriptExecutionRequest
        {
            ScriptBody = actionResult.ScriptBody,
            Files = actionResult.Files,
            CalamariCommand = actionResult.CalamariCommand,
            ExecutionMode = resolvedMode,
            ContextPreparationPolicy = resolvedContextPreparationPolicy,
            ExecutionLocation = tc.Transport?.ExecutionLocation ?? ExecutionLocation.Unspecified,
            ExecutionBackend = tc.Transport?.ExecutionBackend ?? ExecutionBackend.Unspecified,
            PayloadKind = actionResult.PayloadKind,
            RunnerKind = actionResult.RunnerKind,
            Syntax = actionResult.Syntax,
            ActionProperties = actionResult.ActionProperties,
            EndpointContext = tc.EndpointContext,
            Variables = effectiveVariables,
            Machine = tc.Machine,
            ReleaseVersion = _ctx.Release?.Version
        };
    }

    private static Dictionary<string, string> BuildActionPropertyDictionary(DeploymentActionDto action)
    {
        if (action.Properties == null || action.Properties.Count == 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, string>(action.Properties.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var prop in action.Properties)
            dict[prop.PropertyName] = prop.PropertyValue;

        return dict;
    }

    private static ContextPreparationPolicy ResolveContextPreparationPolicy(
        ActionExecutionResult actionResult, DeploymentTargetContext tc)
    {
        if (actionResult.ContextPreparationPolicy != ContextPreparationPolicy.Unspecified)
            return actionResult.ContextPreparationPolicy;

        var mode = actionResult.ResolveExecutionMode();

        if (mode == ExecutionMode.PackagedPayload && (tc.Transport?.RequiresContextPreparationForPackagedPayload ?? false))
            return ContextPreparationPolicy.Apply;

        return actionResult.ResolveContextPreparationPolicy();
    }

    private static void CaptureOutputVariables(ActionExecutionResult actionResult, List<string> logLines)
    {
        var outputVars = ServiceMessageParser.ParseOutputVariables(logLines);

        foreach (var kv in outputVars)
        {
            actionResult.OutputVariables[kv.Key] = kv.Value.Value;

            if (kv.Value.IsSensitive)
                actionResult.SensitiveOutputVariableNames.Add(kv.Key);
        }
    }

    private static void CollectOutputVariables(
        StepExecutionResult result, string stepName, ActionExecutionResult actionResult)
    {
        foreach (var kv in actionResult.OutputVariables)
        {
            var isSensitive = actionResult.SensitiveOutputVariableNames.Contains(kv.Key);
            var qualifiedName = DeploymentVariables.Action.OutputVariable(stepName, kv.Key);

            result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = kv.Value, IsSensitive = isSensitive });
            result.OutputVariables.Add(new VariableDto { Name = kv.Key, Value = kv.Value, IsSensitive = isSensitive });
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
                {
                    var displayValue = v.IsSensitive ? "********" : v.Value;
                    Log.Information("Output variable captured: {Name} = {Value}", v.Name, displayValue);
                }
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

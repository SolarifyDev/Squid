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
        var stepSortOrderByStep = new Dictionary<DeploymentStepDto, int>();
        var displayOrder = 0;

        foreach (var step in orderedSteps)
            stepSortOrderByStep[step] = ++displayOrder;

        foreach (var batch in batches)
        {
            var batchEntries = batch.Select(step => (Step: step, SortOrder: stepSortOrderByStep[step])).ToList();
            var batchResults = batch.Count == 1
                ? [await ExecuteStepAcrossTargetsAsync(batchEntries[0].Step, batchEntries[0].SortOrder, ct).ConfigureAwait(false)]
                : await Task.WhenAll(batchEntries.Select(entry =>
                    ExecuteStepAcrossTargetsAsync(entry.Step, entry.SortOrder, ct))).ConfigureAwait(false);

            ApplyBatchResults(batchResults);
        }
    }

    private async Task<StepExecutionResult> ExecuteStepAcrossTargetsAsync(
        DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        var stepResult = new StepExecutionResult();
        var stepActivityName = BuildStepActivityName(step, stepSortOrder);
        var stepActivityNode = await CreateActivityNodeAsync(_ctx.TaskActivityNode?.Id, stepActivityName, DeploymentActivityLogNodeType.Step, DeploymentActivityLogNodeStatus.Running, stepSortOrder, ct).ConfigureAwait(false);
        var stepNodeId = stepActivityNode?.Id;

        var matchingTargets = FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);

        if (matchingTargets.Count == 0)
        {
            var stepRoles = ExtractStepRoles(step);
            var rolesText = stepRoles.Count > 0 ? string.Join(", ", stepRoles) : "unknown";

            await LogWarningAsync($"Skipping this step as no machines were found in the role{(stepRoles.Count > 1 ? "s" : "")}: {rolesText}", "System", ct, stepNodeId).ConfigureAwait(false);
            await UpdateActivityNodeStatusAsync(stepActivityNode, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);
            return stepResult;
        }

        foreach (var tc in matchingTargets)
        {
            var targetRoles = DeploymentTargetFinder.ParseRoles(tc.Machine.Roles);
            var scopeContext = new VariableScopeContext
            {
                EnvironmentId = _ctx.Deployment.EnvironmentId,
                EnvironmentName = _ctx.Environment?.Name,
                MachineId = tc.Machine.Id,
                MachineName = tc.Machine.Name,
                Roles = targetRoles,
                ChannelId = _ctx.Deployment.ChannelId
            };
            var effectiveVariables = BuildEffectiveVariables(_ctx.Variables, tc, scopeContext);

            var eligibility = EvaluateStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, effectiveVariables);

            if (!eligibility.ShouldExecute)
            {
                Log.Information("Skipping step {StepName} on target {TargetName}: {Reason}", step.Name, tc.Machine.Name, eligibility.SkipReason);

                await LogInfoAsync(eligibility.Message, tc.Machine.Name, ct, stepNodeId).ConfigureAwait(false);

                continue;
            }

            if (eligibility.Message != null)
                await LogInfoAsync(eligibility.Message, "System", ct, stepNodeId).ConfigureAwait(false);

            await LogInfoAsync($"Executing step \"{step.Name}\" on {tc.Machine.Name}", tc.Machine.Name, ct, stepNodeId).ConfigureAwait(false);

            var variableDictionary = VariableDictionaryFactory.Create(effectiveVariables);

            var actionResults = await PrepareStepActionsAsync(
                step, variableDictionary, effectiveVariables, tc, stepNodeId, ct).ConfigureAwait(false);

            var result = new StepExecutionResult();
            await ExecuteActionResultsAsync(
                actionResults, step, stepActivityNode, result, tc, effectiveVariables, ct).ConfigureAwait(false);

            stepResult.Absorb(result);
        }

        if (!stepResult.Failed)
            await LogInfoAsync($"Step \"{step.Name}\" completed successfully", "System", ct, stepNodeId).ConfigureAwait(false);

        await UpdateActivityNodeStatusAsync(
            stepActivityNode,
            stepResult.Failed ? DeploymentActivityLogNodeStatus.Failed : DeploymentActivityLogNodeStatus.Success,
            ct).ConfigureAwait(false);

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
        long? stepActivityNodeId,
        CancellationToken ct)
    {
        var stepResults = new List<ActionExecutionResult>();

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            if (_ctx.Deployment?.DeploymentRequestPayload?.SkipActionIds?.Contains(action.Id) == true)
            {
                Log.Information("Skipping action {ActionName} ({ActionId}) due to SkipActionIds selection", action.Name, action.Id);

                await LogInfoAsync($"Action \"{action.Name}\" was manually excluded from this deployment", "System", ct, stepActivityNodeId).ConfigureAwait(false);

                continue;
            }

            var actionEligibility = EvaluateAction(action, _ctx.Deployment.EnvironmentId, _ctx.Deployment.ChannelId);

            if (!actionEligibility.ShouldExecute)
            {
                Log.Information("Skipping action {ActionName}: {Reason}", action.Name, actionEligibility.SkipReason);

                await LogWarningAsync(actionEligibility.Message, "System", ct, stepActivityNodeId).ConfigureAwait(false);

                continue;
            }

            var handler = _actionHandlerRegistry.Resolve(action);

            if (handler == null)
            {
                Log.Warning("No handler found for action {ActionType}, skipping", action.ActionType);

                await LogWarningAsync($"No handler found for action type \"{action.ActionType}\", skipping", "System", ct, stepActivityNodeId).ConfigureAwait(false);

                continue;
            }

            await LogInfoAsync($"Running action \"{action.Name}\"", "System", ct, stepActivityNodeId).ConfigureAwait(false);

            var actionVariables = BuildActionVariables(effectiveVariables, action);
            var variableDictionaryForAction = VariableDictionaryFactory.Create(actionVariables);
            var expandedAction = VariableExpander.ExpandActionProperties(action, variableDictionaryForAction);

            var context = new ActionExecutionContext
            {
                Step = step,
                Action = expandedAction,
                Variables = actionVariables,
                ReleaseVersion = _ctx.Release?.Version,
                SelectedPackages = _ctx.SelectedPackages?
                    .Select(sp => new Message.Models.Deployments.Release.SelectedPackageDto
                    {
                        ActionName = sp.ActionName,
                        PackageReferenceName = sp.PackageReferenceName,
                        Version = sp.Version
                    }).ToList() ?? new()
            };

            var prepared = await handler.PrepareAsync(context, ct).ConfigureAwait(false);

            if (prepared != null)
            {
                prepared.ActionName = action.Name;
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
            var actionActivityName = BuildActionActivityName(tc, actionResult);

            var actionActivityNode = await CreateActivityNodeAsync(stepActivityNode?.Id, actionActivityName, DeploymentActivityLogNodeType.Action, DeploymentActivityLogNodeStatus.Running, ++actionSortOrder, ct).ConfigureAwait(false);

            try
            {
                var strategy = tc.Transport?.Strategy;

                if (strategy == null)
                    throw new DeploymentTargetException(
                        $"No execution strategy for {tc.CommunicationStyle}");

                var request = BuildScriptExecutionRequest(actionResult, tc, effectiveVariables);
                var execResult = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

                CaptureOutputVariables(actionResult, execResult.LogLines);

                await PersistScriptOutputAsync(execResult, tc.Machine.Name, actionActivityNode?.Id, ct).ConfigureAwait(false);

                if (!execResult.Success)
                    throw new DeploymentScriptException(execResult.BuildErrorSummary(), _ctx.Deployment.Id);

                await LogInfoAsync($"Successfully finished \"{actionResult.ActionName}\" on {tc.Machine.Name} (exit code {execResult.ExitCode})", tc.Machine.Name, ct, actionActivityNode?.Id).ConfigureAwait(false);

                await UpdateActivityNodeStatusAsync(actionActivityNode, DeploymentActivityLogNodeStatus.Success, ct).ConfigureAwait(false);

                CollectOutputVariables(result, step.Name, actionResult);
            }
            catch (Exception ex)
            {
                result.Failed = true;
                
                Log.Error(ex, "Action failed in step {StepName}: {Error}", step.Name, ex.Message);

                await LogErrorAsync(ex.Message, tc.Machine.Name, ct, actionActivityNode?.Id).ConfigureAwait(false);

                await UpdateActivityNodeStatusAsync(actionActivityNode, DeploymentActivityLogNodeStatus.Failed, ct).ConfigureAwait(false);

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
            ReleaseVersion = _ctx.Release?.Version,
            ContextWrapper = resolvedContextPreparationPolicy == ContextPreparationPolicy.Apply ? tc.Transport?.ScriptWrapper : null
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

    private static HashSet<string> ExtractStepRoles(DeploymentStepDto step)
    {
        var rolesProp = step.Properties?.FirstOrDefault(p => p.PropertyName == DeploymentVariables.Action.TargetRoles);

        if (rolesProp == null || string.IsNullOrEmpty(rolesProp.PropertyValue))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return DeploymentTargetFinder.ParseCsvRoles(rolesProp.PropertyValue);
    }

    private static string BuildStepActivityName(DeploymentStepDto step, int stepSortOrder)
    {
        var stepName = step?.Name?.Trim();

        if (string.IsNullOrWhiteSpace(stepName))
            return $"Step {stepSortOrder}";

        if (stepName.StartsWith("Step ", StringComparison.OrdinalIgnoreCase))
            return stepName;

        return $"Step {stepSortOrder}: {stepName}";
    }

    private static string BuildActionActivityName(DeploymentTargetContext tc, ActionExecutionResult actionResult)
    {
        var machineName = tc?.Machine?.Name;

        if (!string.IsNullOrWhiteSpace(machineName))
            return $"Executing on {machineName}";

        return string.IsNullOrWhiteSpace(actionResult?.CalamariCommand)
            ? "Executing"
            : actionResult.CalamariCommand;
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

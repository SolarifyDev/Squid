using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.VariableSubstitution;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed class ExecuteStepsPhase(IActionHandlerRegistry actionHandlerRegistry, IDeploymentLifecycle lifecycle, IDeploymentInterruptionService interruptionService, IServerTaskService serverTaskService, IDeploymentCheckpointService checkpointService) : IDeploymentPipelinePhase
{
    public int Order => 500;

    private DeploymentTaskContext _ctx;

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        await ExecuteDeploymentStepsAsync(ct).ConfigureAwait(false);
    }

    private async Task ExecuteDeploymentStepsAsync(CancellationToken ct)
    {
        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);
        var stepSortOrderByStep = new Dictionary<DeploymentStepDto, int>();
        var displayOrder = 0;

        foreach (var step in orderedSteps)
            stepSortOrderByStep[step] = ++displayOrder;

        var batchIndex = 0;

        foreach (var batch in batches)
        {
            if (_ctx.ResumeFromBatchIndex.HasValue && batchIndex <= _ctx.ResumeFromBatchIndex.Value)
            {
                Log.Information("Skipping batch {BatchIndex} (already completed in previous run)", batchIndex);
                batchIndex++;
                continue;
            }

            var batchEntries = batch.Select(step => (Step: step, SortOrder: stepSortOrderByStep[step])).ToList();
            var batchResults = batch.Count == 1
                ? [await ExecuteStepAcrossTargetsAsync(batchEntries[0].Step, batchEntries[0].SortOrder, ct).ConfigureAwait(false)]
                : await Task.WhenAll(batchEntries.Select(entry =>
                    ExecuteStepAcrossTargetsAsync(entry.Step, entry.SortOrder, ct))).ConfigureAwait(false);

            ApplyBatchResults(batchResults);

            await PersistCheckpointAsync(batchIndex, ct).ConfigureAwait(false);

            batchIndex++;
        }
    }

    private async Task PersistCheckpointAsync(int batchIndex, CancellationToken ct)
    {
        try
        {
            var outputVariablesJson = SerializeOutputVariables(_ctx.Variables);

            await checkpointService.SaveAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = _ctx.ServerTaskId,
                DeploymentId = _ctx.Deployment.Id,
                LastCompletedBatchIndex = batchIndex,
                FailureEncountered = _ctx.FailureEncountered,
                OutputVariablesJson = outputVariablesJson,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to persist checkpoint at batch {BatchIndex}, continuing", batchIndex);
        }
    }

    private static string SerializeOutputVariables(List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0) return null;

        var outputVars = variables.Where(v => v.Name.StartsWith("Squid.Action.", StringComparison.OrdinalIgnoreCase)).ToList();

        if (outputVars.Count == 0) return null;

        return System.Text.Json.JsonSerializer.Serialize(outputVars);
    }

    private async Task<StepExecutionResult> ExecuteStepAcrossTargetsAsync(DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        var stepResult = new StepExecutionResult();

        await lifecycle.EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = step.Name, StepDisplayOrder = stepSortOrder }), ct).ConfigureAwait(false);

        var matchingTargets = TargetStepMatcher.FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);

        if (matchingTargets.Count == 0)
        {
            await lifecycle.EmitAsync(new StepNoMatchingTargetsEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Roles = ExtractStepRoles(step) }), ct).ConfigureAwait(false);
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
            var effectiveVariables = EffectiveVariableBuilder.BuildEffectiveVariables(_ctx.Variables, tc, scopeContext);

            var eligibility = StepEligibilityEvaluator.EvaluateStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, effectiveVariables);

            if (!eligibility.ShouldExecute)
            {
                Log.Information("Skipping step {StepName} on target {TargetName}: {Reason}", step.Name, tc.Machine.Name, eligibility.SkipReason);

                await lifecycle.EmitAsync(new StepSkippedOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, StepEligibility = eligibility, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

                continue;
            }

            if (eligibility.Message != null)
                await lifecycle.EmitAsync(new StepConditionMetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Message = eligibility.Message }), ct).ConfigureAwait(false);

            await lifecycle.EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

            var variableDictionary = VariableDictionaryFactory.Create(effectiveVariables);

            var actionResults = await PrepareStepActionsAsync(step, variableDictionary, effectiveVariables, tc, stepSortOrder, ct).ConfigureAwait(false);

            var result = new StepExecutionResult();
            await ExecuteActionResultsAsync(actionResults, step, stepSortOrder, result, tc, effectiveVariables, ct).ConfigureAwait(false);

            stepResult.Absorb(result);
        }

        await lifecycle.EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, Failed = stepResult.Failed }), ct).ConfigureAwait(false);

        return stepResult;
    }

    private async Task<List<ActionExecutionResult>> PrepareStepActionsAsync(
        DeploymentStepDto step,
        VariableDictionary variableDictionary,
        List<VariableDto> effectiveVariables,
        DeploymentTargetContext tc,
        int stepDisplayOrder,
        CancellationToken ct)
    {
        var stepResults = new List<ActionExecutionResult>();

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            if (_ctx.Deployment?.DeploymentRequestPayload?.SkipActionIds?.Contains(action.Id) == true)
            {
                Log.Information("Skipping action {ActionName} ({ActionId}) due to SkipActionIds selection", action.Name, action.Id);

                await lifecycle.EmitAsync(new ActionManuallyExcludedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name }), ct).ConfigureAwait(false);

                continue;
            }

            var actionEligibility = StepEligibilityEvaluator.EvaluateAction(action, _ctx.Deployment.EnvironmentId, _ctx.Deployment.ChannelId);

            if (!actionEligibility.ShouldExecute)
            {
                Log.Information("Skipping action {ActionName}: {Reason}", action.Name, actionEligibility.SkipReason);

                await lifecycle.EmitAsync(new ActionSkippedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name, ActionEligibility = actionEligibility }), ct).ConfigureAwait(false);

                continue;
            }

            var handler = actionHandlerRegistry.Resolve(action);

            if (handler == null)
            {
                Log.Warning("No handler found for action {ActionType}, skipping", action.ActionType);

                await lifecycle.EmitAsync(new ActionNoHandlerEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionType = action.ActionType }), ct).ConfigureAwait(false);

                continue;
            }

            await lifecycle.EmitAsync(new ActionRunningEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name }), ct).ConfigureAwait(false);

            var actionVariables = EffectiveVariableBuilder.BuildActionVariables(effectiveVariables, action, _ctx.SelectedPackages);
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

    private static void WrapScriptIfApplicable(ActionExecutionResult prepared, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
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
        int stepDisplayOrder,
        StepExecutionResult result,
        DeploymentTargetContext tc,
        List<VariableDto> effectiveVariables,
        CancellationToken ct)
    {
        const int maxRetries = 10;
        var actionSortOrder = 0;

        foreach (var actionResult in stepResults)
        {
            ++actionSortOrder;
            var retry = true;
            var retryCount = 0;

            while (retry)
            {
                retry = false;

                await lifecycle.EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ActionName = actionResult.ActionName }), ct).ConfigureAwait(false);

                if (actionResult.ExecutionMode == ExecutionMode.ManualIntervention)
                {
                    var interventionOutcome = await HandleManualInterventionAsync(step, actionResult, stepDisplayOrder, actionSortOrder, ct).ConfigureAwait(false);

                    if (interventionOutcome == InterruptionOutcome.Proceed)
                    {
                        CollectManualInterventionOutputVariables(result, step.Name, actionResult);
                        continue;
                    }

                    throw new DeploymentAbortedException($"Manual intervention aborted for step \"{step.Name}\"");
                }

                try
                {
                    var strategy = tc.Transport?.Strategy;

                    if (strategy == null)
                        throw new DeploymentTargetException($"No execution strategy for {tc.CommunicationStyle}");

                    var request = BuildScriptExecutionRequest(actionResult, tc, effectiveVariables);
                    var execResult = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

                    CaptureOutputVariables(actionResult, execResult.LogLines);

                    await lifecycle.EmitAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ScriptResult = execResult }), ct).ConfigureAwait(false);

                    if (!execResult.Success)
                        throw new DeploymentScriptException(execResult.BuildErrorSummary(), _ctx.Deployment.Id);

                    await lifecycle.EmitAsync(new ActionSucceededEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ActionName = actionResult.ActionName, ExitCode = execResult.ExitCode }), ct).ConfigureAwait(false);

                    CollectOutputVariables(result, step.Name, actionResult);
                }
                catch (Exception ex)
                {
                    result.Failed = true;

                    Log.Error(ex, "Action failed in step {StepName}: {Error}", step.Name, ex.Message);

                    await lifecycle.EmitAsync(new ActionFailedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, Error = ex.Message }), ct).ConfigureAwait(false);

                    if (step.IsRequired && _ctx.UseGuidedFailure)
                    {
                        var outcome = await HandleGuidedFailureAsync(step, actionResult, tc, ex, stepDisplayOrder, actionSortOrder, ct).ConfigureAwait(false);

                        if (outcome == InterruptionOutcome.Retry && retryCount < maxRetries)
                        {
                            result.Failed = false;
                            retry = true;
                            retryCount++;
                            continue;
                        }

                        if (outcome == InterruptionOutcome.Skip)
                        {
                            result.Failed = false;
                            break;
                        }
                    }

                    if (step.IsRequired)
                        throw;
                }
            }
        }
    }

    private async Task<InterruptionOutcome> HandleGuidedFailureAsync(DeploymentStepDto step, ActionExecutionResult actionResult, DeploymentTargetContext tc, Exception ex, int stepDisplayOrder, int actionSortOrder, CancellationToken ct)
    {
        var form = InterruptionFormBuilder.BuildGuidedFailureForm(step.Name, actionResult.ActionName, tc.Machine.Name, ex.Message);

        await lifecycle.EmitAsync(new GuidedFailurePromptEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            ActionName = actionResult.ActionName, MachineName = tc.Machine.Name,
            ActionSortOrder = actionSortOrder, Error = ex.Message,
            InterruptionType = InterruptionType.GuidedFailure
        }), ct).ConfigureAwait(false);

        var interruption = await interruptionService.CreateInterruptionAsync(new CreateInterruptionRequest
        {
            ServerTaskId = _ctx.ServerTaskId, DeploymentId = _ctx.Deployment.Id,
            InterruptionType = InterruptionType.GuidedFailure, StepDisplayOrder = stepDisplayOrder,
            StepName = step.Name, ActionName = actionResult.ActionName,
            MachineName = tc.Machine.Name, ErrorMessage = ex.Message,
            Form = form, SpaceId = _ctx.Deployment.SpaceId
        }, ct).ConfigureAwait(false);

        var outcome = await interruptionService.WaitForInterruptionAsync(interruption.Id, ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new GuidedFailureResolvedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            GuidedFailureResolution = outcome.ToString(),
            InterruptionType = InterruptionType.GuidedFailure
        }), ct).ConfigureAwait(false);

        return outcome;
    }

    private async Task<InterruptionOutcome> HandleManualInterventionAsync(DeploymentStepDto step, ActionExecutionResult actionResult, int stepDisplayOrder, int actionSortOrder, CancellationToken ct)
    {
        var form = InterruptionFormBuilder.BuildManualInterventionForm(actionResult.ManualInterventionInstructions);

        await lifecycle.EmitAsync(new GuidedFailurePromptEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            ActionName = actionResult.ActionName, InterruptionType = InterruptionType.ManualIntervention
        }), ct).ConfigureAwait(false);

        var interruption = await interruptionService.CreateInterruptionAsync(new CreateInterruptionRequest
        {
            ServerTaskId = _ctx.ServerTaskId, DeploymentId = _ctx.Deployment.Id,
            InterruptionType = InterruptionType.ManualIntervention, StepDisplayOrder = stepDisplayOrder,
            StepName = step.Name, ActionName = actionResult.ActionName,
            Form = form, SpaceId = _ctx.Deployment.SpaceId
        }, ct).ConfigureAwait(false);

        var outcome = await interruptionService.WaitForInterruptionAsync(interruption.Id, ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new GuidedFailureResolvedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            GuidedFailureResolution = outcome.ToString(),
            InterruptionType = InterruptionType.ManualIntervention
        }), ct).ConfigureAwait(false);

        return outcome;
    }

    private static void CollectManualInterventionOutputVariables(StepExecutionResult result, string stepName, ActionExecutionResult actionResult)
    {
        var responsibleUser = actionResult.OutputVariables.GetValueOrDefault("ManualIntervention.ResponsibleUserId");
        var notes = actionResult.OutputVariables.GetValueOrDefault("ManualIntervention.Notes");

        if (responsibleUser != null)
        {
            var qualifiedName = DeploymentVariables.Action.OutputVariable(stepName, "ManualIntervention.ResponsibleUserId");
            result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = responsibleUser });
        }

        if (notes != null)
        {
            var qualifiedName = DeploymentVariables.Action.OutputVariable(stepName, "ManualIntervention.Notes");
            result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = notes });
        }
    }

    private ScriptExecutionRequest BuildScriptExecutionRequest(ActionExecutionResult actionResult, DeploymentTargetContext tc, List<VariableDto> effectiveVariables)
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

    private static ContextPreparationPolicy ResolveContextPreparationPolicy(ActionExecutionResult actionResult, DeploymentTargetContext tc)
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

    private static void CollectOutputVariables(StepExecutionResult result, string stepName, ActionExecutionResult actionResult)
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

using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Interruption;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase
{
    private async Task<StepExecutionResult> ExecuteStepAcrossTargetsAsync(DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        var stepResult = new StepExecutionResult();

        var (eligibleActions, skippedActions) = FilterEligibleActions(step);

        if (eligibleActions.Count == 0 && step.Actions.Count > 0)
            return stepResult;

        await lifecycle.EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = step.Name, StepDisplayOrder = stepSortOrder }), ct).ConfigureAwait(false);

        await EmitSkippedActionEventsAsync(skippedActions, stepSortOrder, ct).ConfigureAwait(false);

        await ExecuteStepLevelActionsAsync(step, eligibleActions, stepSortOrder, ct).ConfigureAwait(false);

        if (HasTargetLevelActions(eligibleActions))
        {
            var matchingTargets = TargetStepMatcher.FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);

            if (matchingTargets.Count == 0)
            {
                await lifecycle.EmitAsync(new StepNoMatchingTargetsEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Roles = ExtractStepRoles(step) }), ct).ConfigureAwait(false);
                return stepResult;
            }

            foreach (var tc in matchingTargets)
            {
                var targetRoles = DeploymentTargetFinder.ParseRoles(tc.Machine.Roles);
                var baseScopeContext = BuildTargetScopeContext(tc, targetRoles);
                var stepEffectiveVars = EffectiveVariableBuilder.BuildEffectiveVariables(_ctx.Variables, tc, baseScopeContext);

                var eligibility = StepEligibilityEvaluator.EvaluateStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, stepEffectiveVars);

                if (!eligibility.ShouldExecute)
                {
                    Log.Information("Skipping step {StepName} on target {TargetName}: {Reason}", step.Name, tc.Machine.Name, eligibility.SkipReason);

                    await lifecycle.EmitAsync(new StepSkippedOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, StepEligibility = eligibility, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

                    continue;
                }

                if (eligibility.Message != null)
                    await lifecycle.EmitAsync(new StepConditionMetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Message = eligibility.Message }), ct).ConfigureAwait(false);

                await lifecycle.EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

                var actionResults = await PrepareStepActionsAsync(step, eligibleActions, baseScopeContext, tc, stepSortOrder, ct).ConfigureAwait(false);

                var result = new StepExecutionResult();
                await ExecuteActionResultsAsync(actionResults, step, stepSortOrder, result, tc, ct).ConfigureAwait(false);

                stepResult.Absorb(result);
            }
        }

        await lifecycle.EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, Failed = stepResult.Failed }), ct).ConfigureAwait(false);

        return stepResult;
    }

    private VariableScopeContext BuildTargetScopeContext(DeploymentTargetContext tc, HashSet<string> targetRoles)
    {
        return new VariableScopeContext
        {
            EnvironmentId = _ctx.Deployment.EnvironmentId,
            EnvironmentName = _ctx.Environment?.Name,
            MachineId = tc.Machine.Id,
            MachineName = tc.Machine.Name,
            Roles = targetRoles,
            ChannelId = _ctx.Deployment.ChannelId,
            ChannelName = _ctx.Channel?.Name,
            ProcessId = _ctx.ProcessSnapshot?.OriginalProcessId,
        };
    }

    private async Task ExecuteActionResultsAsync(
        List<PreparedAction> stepResults,
        DeploymentStepDto step,
        int stepDisplayOrder,
        StepExecutionResult result,
        DeploymentTargetContext tc,
        CancellationToken ct)
    {
        const int maxRetries = 10;
        var actionSortOrder = 0;

        foreach (var prepared in stepResults)
        {
            var actionResult = prepared.Result;
            var effectiveVariables = prepared.EffectiveVariables;

            ++actionSortOrder;
            var retry = true;
            var retryCount = 0;

            while (retry)
            {
                retry = false;

                await lifecycle.EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ActionName = actionResult.ActionName }), ct).ConfigureAwait(false);

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

    private async Task ExecuteStepLevelActionsAsync(DeploymentStepDto step, List<DeploymentActionDto> eligibleActions, int stepDisplayOrder, CancellationToken ct)
    {
        var actionSortOrder = 0;

        foreach (var action in eligibleActions)
        {
            actionSortOrder++;

            if (actionHandlerRegistry.ResolveScope(action) != ExecutionScope.StepLevel) continue;

            var handler = actionHandlerRegistry.Resolve(action);
            if (handler == null) continue;

            var ctx = new StepActionContext
            {
                ServerTaskId = _ctx.ServerTaskId, DeploymentId = _ctx.Deployment.Id, SpaceId = _ctx.Deployment.SpaceId,
                Step = step, Action = action, Variables = _ctx.Variables, ReleaseVersion = _ctx.Release?.Version,
                StepDisplayOrder = stepDisplayOrder, ActionSortOrder = actionSortOrder
            };

            await handler.ExecuteStepLevelAsync(ctx, ct).ConfigureAwait(false);
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

        await PersistCheckpointAsync(_currentBatchIndex, ct).ConfigureAwait(false);

        var outcome = await interruptionService.WaitForInterruptionAsync(interruption.Id, ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new GuidedFailureResolvedEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepDisplayOrder, StepName = step.Name,
            GuidedFailureResolution = outcome.ToString(),
            InterruptionType = InterruptionType.GuidedFailure
        }), ct).ConfigureAwait(false);

        return outcome;
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

    private bool HasTargetLevelActions(List<DeploymentActionDto> eligibleActions)
    {
        if (eligibleActions.Count == 0) return true;

        return eligibleActions.Any(a => actionHandlerRegistry.ResolveScope(a) == ExecutionScope.TargetLevel);
    }

    private (List<DeploymentActionDto> Eligible, List<(DeploymentActionDto Action, ActionEligibilityResult Eligibility)> Skipped) FilterEligibleActions(DeploymentStepDto step)
    {
        var evalCtx = BuildActionEvaluationContext();
        var eligible = new List<DeploymentActionDto>();
        var skipped = new List<(DeploymentActionDto, ActionEligibilityResult)>();

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            var eligibility = StepEligibilityEvaluator.EvaluateAction(action, evalCtx);

            if (!eligibility.ShouldExecute) { skipped.Add((action, eligibility)); continue; }

            eligible.Add(action);
        }

        return (eligible, skipped);
    }

    private ActionEvaluationContext BuildActionEvaluationContext()
    {
        return new ActionEvaluationContext(
            _ctx.Deployment.EnvironmentId,
            _ctx.Deployment.ChannelId,
            _ctx.Deployment?.DeploymentRequestPayload?.SkipActionIds?.ToHashSet());
    }

    private async Task EmitSkippedActionEventsAsync(List<(DeploymentActionDto Action, ActionEligibilityResult Eligibility)> skipped, int stepDisplayOrder, CancellationToken ct)
    {
        foreach (var (action, eligibility) in skipped)
        {
            Log.Information("Skipping action {ActionName}: {Reason}", action.Name, eligibility.SkipReason);

            if (eligibility.SkipReason == ActionSkipReason.ManuallySkipped)
                await lifecycle.EmitAsync(new ActionManuallyExcludedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name }), ct).ConfigureAwait(false);
            else
                await lifecycle.EmitAsync(new ActionSkippedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name, ActionEligibility = eligibility }), ct).ConfigureAwait(false);
        }
    }
}

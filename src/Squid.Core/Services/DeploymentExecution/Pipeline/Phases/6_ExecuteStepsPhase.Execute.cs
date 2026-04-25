using Squid.Core.Observability;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Planning;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Message.Hardening;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase
{
    private async Task<StepExecutionResult> ExecuteStepAcrossTargetsAsync(DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        using var stepSpan = DeploymentTracing.StartStep(step.Name, stepSortOrder);
        var stepResult = new StepExecutionResult();

        if (IsSyntheticStep(step))
            return await ExecuteSyntheticStepAsync(step, stepSortOrder, ct).ConfigureAwait(false);

        var planned = LookupPlannedStep(step);

        if (planned != null && IsSkippedByPlanner(planned.Status))
            return stepResult;

        var (eligibleActions, skippedActions) = planned != null
            ? SplitActionsUsingPlan(step, planned)
            : FilterEligibleActions(step);

        if (eligibleActions.Count == 0 && step.Actions.Count > 0)
            return stepResult;

        await lifecycle.EmitAsync(new StepStartingEvent(new DeploymentEventContext { StepName = step.Name, StepDisplayOrder = stepSortOrder, StepType = step.StepType }), ct).ConfigureAwait(false);

        await EmitSkippedActionEventsAsync(skippedActions, stepSortOrder, ct).ConfigureAwait(false);

        var stepLevelExecuted = await ExecuteStepLevelActionsAsync(step, eligibleActions, stepSortOrder, ct).ConfigureAwait(false);

        if (stepLevelExecuted)
            stepResult.Executed = true;

        if (HasTargetLevelActions(eligibleActions))
        {
            if (IsRunOnServer(step, planned))
            {
                var serverResult = await ExecuteStepOnServerAsync(step, eligibleActions, stepSortOrder, ct).ConfigureAwait(false);
                stepResult.Absorb(serverResult);
            }
            else
            {
                var matchingTargets = ResolveMatchingTargets(step, planned);
                matchingTargets = FilterAlreadyCompletedTargets(matchingTargets);

                if (matchingTargets.Count == 0)
                {
                    await lifecycle.EmitAsync(new StepNoMatchingTargetsEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Roles = ExtractStepRoles(step) }), ct).ConfigureAwait(false);
                    return stepResult;
                }

                var maxParallelism = TargetParallelExecutor.ParseMaxParallelism(step);

                using var failFastCts = step.IsRequired ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
                var effectiveCt = failFastCts?.Token ?? ct;

                var targetResults = await TargetParallelExecutor.ExecuteAsync(
                    matchingTargets, maxParallelism,
                    async (tc, targetCt) =>
                    {
                        StepExecutionResult targetResult = null;
                        try
                        {
                            targetResult = await ExecuteSingleTargetAsync(step, eligibleActions, tc, stepSortOrder, targetCt).ConfigureAwait(false);
                            return targetResult;
                        }
                        catch (Exception ex) when (step.IsRequired)
                        {
                            // P1-A.4 (Phase-7): classify the catch instead of
                            // unconditionally marking failed. Peer-aborted
                            // targets (OCE from a peer's failFastCts.Cancel)
                            // must NOT be checkpointed terminal — resume should
                            // retry them. Real exceptions / user-cancel still
                            // cascade through MarkTargetCompleted +
                            // failFastCts.Cancel as before.
                            var classification = TargetCatchClassifier.Classify(
                                ex,
                                failFastCancelled: failFastCts?.IsCancellationRequested ?? false,
                                parentCtCancelled: ct.IsCancellationRequested);

                            if (classification.MarkFailed)
                                MarkTargetCompleted(_currentBatchIndex, tc.Machine.Id, failed: true);
                            if (classification.TriggerFailFast)
                                failFastCts?.Cancel();
                            throw;
                        }
                        finally
                        {
                            if (targetResult != null)
                                MarkTargetCompleted(_currentBatchIndex, tc.Machine.Id, failed: targetResult.Failed);
                        }
                    }, effectiveCt).ConfigureAwait(false);

                foreach (var result in targetResults)
                    stepResult.Absorb(result);
            }
        }

        var skipped = !stepResult.Executed && !stepResult.Failed;

        await lifecycle.EmitAsync(new StepCompletedEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, Failed = stepResult.Failed, Skipped = skipped }), ct).ConfigureAwait(false);

        return stepResult;
    }

    private PlannedStep LookupPlannedStep(DeploymentStepDto step)
    {
        return _ctx.Plan?.Steps.FirstOrDefault(p => p.StepId == step.Id);
    }

    private static bool IsSkippedByPlanner(PlannedStepStatus status)
    {
        return status is PlannedStepStatus.Disabled or PlannedStepStatus.NoRunnableActions;
    }

    private static bool IsRunOnServer(DeploymentStepDto step, PlannedStep planned)
    {
        if (planned != null)
            return planned.Status == PlannedStepStatus.RunOnServer;

        return RunOnServerEvaluator.IsRunOnServer(step);
    }

    private (List<DeploymentActionDto> Eligible, List<(DeploymentActionDto Action, ActionEligibilityResult Eligibility)> Skipped) SplitActionsUsingPlan(DeploymentStepDto step, PlannedStep planned)
    {
        var plannedActionIds = planned.Actions.Select(a => a.ActionId).ToHashSet();
        var evalCtx = BuildActionEvaluationContext();
        var eligible = new List<DeploymentActionDto>();
        var skipped = new List<(DeploymentActionDto, ActionEligibilityResult)>();

        foreach (var action in step.Actions.OrderBy(p => p.ActionOrder))
        {
            if (plannedActionIds.Contains(action.Id))
            {
                eligible.Add(action);
                continue;
            }

            var eligibility = StepEligibilityEvaluator.EvaluateAction(action, evalCtx);
            skipped.Add((action, eligibility));
        }

        return (eligible, skipped);
    }

    private List<DeploymentTargetContext> ResolveMatchingTargets(DeploymentStepDto step, PlannedStep planned)
    {
        if (planned == null)
        {
            var legacy = TargetStepMatcher.FindMatchingTargetsForStep(step, _ctx.AllTargetsContext);
            return legacy.Where(tc => !tc.IsExcluded).ToList();
        }

        var plannedMachineIds = planned.MatchedTargets.Select(t => t.MachineId).ToHashSet();

        return _ctx.AllTargetsContext
            .Where(tc => plannedMachineIds.Contains(tc.Machine.Id) && !tc.IsExcluded)
            .ToList();
    }

    private List<DeploymentTargetContext> FilterAlreadyCompletedTargets(List<DeploymentTargetContext> targets)
    {
        if (!_batchStates.TryGetValue(_currentBatchIndex, out var state)) return targets;

        var filtered = targets.Where(tc => !state.IsTerminalFor(tc.Machine.Id)).ToList();

        if (filtered.Count < targets.Count)
        {
            var skippedNames = targets
                .Where(tc => state.IsTerminalFor(tc.Machine.Id))
                .Select(tc => tc.Machine.Name);
            Log.Information("[Deploy] Skipping {Count} machine(s) already terminal in prior run of batch {Batch}: {Names}",
                targets.Count - filtered.Count, _currentBatchIndex, string.Join(", ", skippedNames));
        }

        return filtered;
    }

    private async Task<StepExecutionResult> ExecuteSingleTargetAsync(DeploymentStepDto step, List<DeploymentActionDto> eligibleActions, DeploymentTargetContext tc, int stepSortOrder, CancellationToken ct)
    {
        // Target-level span wraps the entire per-machine execution. If a transport
        // exception escapes ExecuteActionResultsAsync, RecordException marks the
        // span as Error; otherwise the span auto-completes at dispose with OK.
        using var targetSpan = DeploymentTracing.StartTargetExecution(
            step.Name,
            eligibleActions.FirstOrDefault()?.Name ?? "-",
            tc.Machine?.Id ?? 0,
            tc.Machine?.Name ?? "unknown",
            tc.CommunicationStyle.ToString());

        var targetRoles = DeploymentTargetFinder.ParseRoles(tc.Machine.Roles);
        var baseScopeContext = BuildTargetScopeContext(tc, targetRoles);
        var stepEffectiveVars = EffectiveVariableBuilder.BuildEffectiveVariables(_ctx.Variables, tc, baseScopeContext);

        var eligibility = StepEligibilityEvaluator.EvaluateStep(step, targetRoles, previousStepSucceeded: !_ctx.FailureEncountered, stepEffectiveVars);

        if (!eligibility.ShouldExecute)
        {
            await lifecycle.EmitAsync(new StepSkippedOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, StepEligibility = eligibility, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

            return new StepExecutionResult();
        }

        if (eligibility.Message != null)
            await lifecycle.EmitAsync(new StepConditionMetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Message = eligibility.Message }), ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new StepExecutingOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, MachineName = tc.Machine.Name }), ct).ConfigureAwait(false);

        var actionResults = await PrepareStepActionsAsync(step, eligibleActions, baseScopeContext, tc, stepSortOrder, ct).ConfigureAwait(false);
        var stepTimeout = StepTimeoutParser.ParseTimeout(step);

        var result = new StepExecutionResult { Executed = true };
        try
        {
            await ExecuteActionResultsAsync(actionResults, step, stepSortOrder, result, tc, stepTimeout, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            targetSpan?.RecordException(ex);
            throw;
        }

        // Record final outcome on the target span so OTel backends can slice
        // success/failure rates by machine and communication style.
        if (result.Failed)
            targetSpan?.SetStatus(System.Diagnostics.ActivityStatusCode.Error);

        return result;
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

    private async Task<StepExecutionResult> ExecuteStepOnServerAsync(DeploymentStepDto step, List<DeploymentActionDto> eligibleActions, int stepSortOrder, CancellationToken ct)
    {
        var serverTc = CreateServerTargetContext();
        var scopeContext = BuildServerScopeContext();
        var effectiveVars = EffectiveVariableBuilder.BuildEffectiveVariables(_ctx.Variables, serverTc, scopeContext);

        var eligibility = StepEligibilityEvaluator.EvaluateStep(step, targetRoles: null, previousStepSucceeded: !_ctx.FailureEncountered, effectiveVars);

        if (!eligibility.ShouldExecute)
        {
            await lifecycle.EmitAsync(new StepSkippedOnTargetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name, StepEligibility = eligibility, MachineName = serverTc.Machine.Name }), ct).ConfigureAwait(false);

            return new StepExecutionResult();
        }

        if (eligibility.Message != null)
            await lifecycle.EmitAsync(new StepConditionMetEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, Message = eligibility.Message }), ct).ConfigureAwait(false);

        await lifecycle.EmitAsync(new RunOnServerExecutingEvent(new DeploymentEventContext { StepDisplayOrder = stepSortOrder, StepName = step.Name }), ct).ConfigureAwait(false);

        var actions = await PrepareStepActionsAsync(step, eligibleActions, scopeContext, serverTc, stepSortOrder, ct).ConfigureAwait(false);
        var timeout = StepTimeoutParser.ParseTimeout(step);

        var result = new StepExecutionResult { Executed = true };
        await ExecuteActionResultsAsync(actions, step, stepSortOrder, result, serverTc, timeout, ct).ConfigureAwait(false);

        return result;
    }

    private DeploymentTargetContext CreateServerTargetContext()
    {
        var transport = transportRegistry.Resolve(CommunicationStyle.None);

        return new DeploymentTargetContext
        {
            Machine = new Machine { Id = 0, Name = "Squid Server" },
            CommunicationStyle = CommunicationStyle.None,
            Transport = transport,
            EndpointContext = new EndpointContext()
        };
    }

    private VariableScopeContext BuildServerScopeContext()
    {
        return new VariableScopeContext
        {
            EnvironmentId = _ctx.Deployment.EnvironmentId,
            EnvironmentName = _ctx.Environment?.Name,
            MachineId = 0,
            MachineName = "Squid Server",
            Roles = null,
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
        TimeSpan? stepTimeout,
        CancellationToken ct)
    {
        var actionSortOrder = 0;

        foreach (var prepared in stepResults)
        {
            ++actionSortOrder;

            var actionName = prepared.Context.Action.Name;

            var directive = await ResolveResumeDirectiveAsync(step, actionName, tc, stepDisplayOrder, ct).ConfigureAwait(false);

            if (directive == ActionDirective.Abort)
                throw new DeploymentAbortedException($"Guided failure aborted for step \"{step.Name}\" action \"{actionName}\"");

            if (directive == ActionDirective.Skip)
            {
                result.Failed = false;
                continue;
            }

            await ExecuteSingleActionAsync(prepared, step, stepDisplayOrder, actionSortOrder, result, tc, stepTimeout, ct).ConfigureAwait(false);
        }
    }

    private async Task ExecuteSingleActionAsync(PreparedAction prepared, DeploymentStepDto step, int stepDisplayOrder, int actionSortOrder, StepExecutionResult result, DeploymentTargetContext tc, TimeSpan? stepTimeout, CancellationToken ct)
    {
        var actionName = prepared.Context.Action.Name;
        var effectiveVariables = prepared.EffectiveVariables;

        await lifecycle.EmitAsync(new ActionExecutingEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ActionName = actionName }), ct).ConfigureAwait(false);

        try
        {
            var strategy = tc.Transport?.Strategy;

            if (strategy == null)
                throw new DeploymentTargetException($"No execution strategy for {tc.CommunicationStyle}");

            var request = await DescribeExpandAndRenderAsync(prepared, tc, step, effectiveVariables, stepTimeout, stepDisplayOrder, ct).ConfigureAwait(false);

            var execResult = await strategy.ExecuteScriptAsync(request, ct).ConfigureAwait(false);

            // P1-B.7: cross-reference output-variable values against the
            // sensitive values that flowed INTO this script, so an agent that
            // wrongly emits sensitive='False' (compromised script or bug) can
            // be caught by the three-mode guard.
            var knownSensitiveValues = ExtractSensitiveValues(effectiveVariables);
            var outputCapture = CaptureOutputVariables(execResult.LogLines, knownSensitiveValues);

            await lifecycle.EmitAsync(new ScriptOutputReceivedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ScriptResult = execResult }), ct).ConfigureAwait(false);

            if (!execResult.Success)
                throw new DeploymentScriptException(execResult.BuildErrorSummary(), _ctx.Deployment.Id);

            await lifecycle.EmitAsync(new ActionSucceededEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, ActionName = actionName, ExitCode = execResult.ExitCode }), ct).ConfigureAwait(false);

            var collectedNames = CollectOutputVariables(result, step.Name, tc.Machine?.Name, outputCapture);

            if (collectedNames.Count > 0)
                await lifecycle.EmitAsync(new OutputVariablesCapturedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine?.Name, ActionSortOrder = actionSortOrder, OutputVariableNames = collectedNames }), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result.Failed = true;

            Log.Error(ex, "[Deploy] Action failed in step {StepName}", step.Name);

            await lifecycle.EmitAsync(new ActionFailedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, MachineName = tc.Machine.Name, ActionSortOrder = actionSortOrder, Error = ex.Message }), ct).ConfigureAwait(false);

            if (step.IsRequired && _ctx.UseGuidedFailure)
            {
                await HandleGuidedFailureAsync(step, actionName, tc, ex, stepDisplayOrder, actionSortOrder, ct).ConfigureAwait(false);
                // unreachable — HandleGuidedFailureAsync always throws DeploymentSuspendedException
            }

            if (step.IsRequired)
                throw;
        }
    }

    private async Task<Squid.Core.Services.DeploymentExecution.Script.ScriptExecutionRequest> DescribeExpandAndRenderAsync(
        PreparedAction prepared,
        DeploymentTargetContext tc,
        DeploymentStepDto step,
        List<VariableDto> effectiveVariables,
        TimeSpan? stepTimeout,
        int stepDisplayOrder,
        CancellationToken ct)
    {
        var actionName = prepared.Context.Action.Name;

        var intent = await prepared.Handler.DescribeIntentAsync(prepared.Context, ct).ConfigureAwait(false);

        intent = IntentVariableExpander.Expand(intent, prepared.VariableDictionary);

        var (expandedIntent, warnings) = IntentStructuredConfigReplacer.ReplaceIfEnabled(intent, prepared.Context.Action, prepared.VariableDictionary);

        await EmitPreparationWarningsAsync(warnings, stepDisplayOrder, actionName, tc.Machine.Name, ct).ConfigureAwait(false);

        var packageReferences = BuildPackageReferences(actionName);

        var renderContext = new IntentRenderContext
        {
            Target = tc,
            Step = step,
            EffectiveVariables = effectiveVariables,
            ServerTaskId = _ctx.ServerTaskId,
            ReleaseVersion = _ctx.Release?.Version,
            StepTimeout = stepTimeout,
            PackageReferences = packageReferences,
            TargetNamespace = effectiveVariables.FirstOrDefault(v => v.Name == SpecialVariables.Kubernetes.Namespace)?.Value
        };

        var renderer = intentRendererRegistry.Resolve(tc.CommunicationStyle, expandedIntent);

        return await renderer.RenderAsync(expandedIntent, renderContext, ct).ConfigureAwait(false);
    }

    private async Task<bool> ExecuteStepLevelActionsAsync(DeploymentStepDto step, List<DeploymentActionDto> eligibleActions, int stepDisplayOrder, CancellationToken ct)
    {
        var executed = false;
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
                StepDisplayOrder = stepDisplayOrder, ActionSortOrder = actionSortOrder,
                DeploymentContext = _ctx
            };

            await handler.ExecuteStepLevelAsync(ctx, ct).ConfigureAwait(false);
            executed = true;
        }

        return executed;
    }

    private sealed class OutputVariableCapture
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SensitiveNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private OutputVariableCapture CaptureOutputVariables(List<string> logLines, IReadOnlyCollection<string> knownSensitiveValues)
    {
        var capture = new OutputVariableCapture();
        var outputVars = serviceMessageParser.ParseOutputVariables(logLines);

        // P1-B.7: read the enforcement mode ONCE per capture pass — env-var
        // reads are cheap but doing it inside the loop would still be wasteful
        // for high-volume output-var emits.
        var leakGuardMode = EnforcementModeReader.Read(SensitiveValueLeakGuard.EnforcementEnvVar);

        foreach (var kv in outputVars)
        {
            capture.Variables[kv.Key] = kv.Value.Value;

            var isSensitive = SensitiveValueLeakGuard.ShouldForceSensitive(
                kv.Key,
                kv.Value.Value,
                kv.Value.IsSensitive,
                knownSensitiveValues,
                leakGuardMode);

            if (isSensitive)
                capture.SensitiveNames.Add(kv.Key);
        }

        return capture;
    }

    /// <summary>
    /// Pulls the values of every <see cref="VariableDto.IsSensitive"/>=true
    /// variable that was passed to the script into a HashSet for O(1)
    /// match-checking by <see cref="SensitiveValueLeakGuard"/>. Empty values
    /// and short values are filtered upstream by the guard's length floor —
    /// keeping the set small here is just a minor optimisation.
    /// </summary>
    private static HashSet<string> ExtractSensitiveValues(IReadOnlyList<VariableDto> effectiveVariables)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (effectiveVariables == null) return set;

        foreach (var v in effectiveVariables)
        {
            if (v == null) continue;
            if (!v.IsSensitive) continue;
            if (string.IsNullOrEmpty(v.Value)) continue;
            set.Add(v.Value);
        }
        return set;
    }

    private static readonly string[] ReservedPrefixes = { "Squid.", "System." };

    private static bool IsReservedName(string name)
    {
        return ReservedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> CollectOutputVariables(StepExecutionResult result, string stepName, string machineName, OutputVariableCapture capture)
    {
        var collectedNames = new List<string>();

        foreach (var kv in capture.Variables)
        {
            var isSensitive = capture.SensitiveNames.Contains(kv.Key);
            var qualifiedName = SpecialVariables.Output.Variable(stepName, kv.Key);

            result.OutputVariables.Add(new VariableDto { Name = qualifiedName, Value = kv.Value, IsSensitive = isSensitive });
            collectedNames.Add(qualifiedName);

            if (!string.IsNullOrEmpty(machineName))
            {
                var machineQualifiedName = SpecialVariables.Output.MachineVariable(stepName, machineName, kv.Key);
                result.OutputVariables.Add(new VariableDto { Name = machineQualifiedName, Value = kv.Value, IsSensitive = isSensitive });
                collectedNames.Add(machineQualifiedName);
            }

            if (!IsReservedName(kv.Key))
                result.OutputVariables.Add(new VariableDto { Name = kv.Key, Value = kv.Value, IsSensitive = isSensitive });
        }

        return collectedNames;
    }

    private async Task<StepExecutionResult> ExecuteSyntheticStepAsync(DeploymentStepDto step, int stepSortOrder, CancellationToken ct)
    {
        switch (step.StepType)
        {
            case "AcquirePackages":
                await AcquirePackagesAsync(stepSortOrder, ct).ConfigureAwait(false);
                break;

            default:
                Log.Warning("[Deploy] Unknown synthetic step type {StepType}, skipping", step.StepType);
                break;
        }

        return new StepExecutionResult { Executed = true };
    }

    private async Task AcquirePackagesAsync(int stepSortOrder, CancellationToken ct)
    {
        var packages = _ctx.SelectedPackages ?? [];
        if (packages.Count == 0)
        {
            Log.Information("[Deploy] No packages selected for acquisition");
            return;
        }

        // P0: collect only valid FeedIds — prevents unnecessary DB calls and null reference on ToDictionary
        var feedIds = packages.Where(p => p.FeedId > 0).Select(p => p.FeedId).Distinct().ToList();
        var feeds = feedIds.Count > 0
            ? await externalFeedDataProvider.GetExternalFeedsByIdsAsync(feedIds, ct).ConfigureAwait(false)
            : new List<ExternalFeed>();
        var feedById = feeds.ToDictionary(f => f.Id);

        var totalSize = 0L;
        var packageCount = packages.Count;

        await lifecycle.EmitAsync(new PackagesAcquiringEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepSortOrder,
            SelectedPackages = packages,
            PackageCount = packageCount,
            PackageTotalSizeBytes = 0,
            Packages = new DeploymentPackageContext(
                SelectedPackages: packages,
                PackageId: string.Empty,
                PackageVersion: string.Empty,
                PackageFeedId: 0,
                PackageSizeBytes: 0,
                PackageHash: string.Empty,
                PackageLocalPath: string.Empty,
                PackageIndex: 0,
                PackageCount: packageCount,
                PackageTotalSizeBytes: 0,
                PackageError: string.Empty)
        }), ct).ConfigureAwait(false);

        for (var i = 0; i < packages.Count; i++)
        {
            var pkg = packages[i];

            // P0: validate FeedId is positive — database enforces NOT NULL, service layer enforces semantic validity
            if (pkg.FeedId <= 0)
            {
                Log.Error("[Deploy] Package {PackageId} v{Version} has invalid FeedId {FeedId}", pkg.PackageReferenceName, pkg.Version, pkg.FeedId);
                await lifecycle.EmitAsync(new PackageDownloadFailedEvent(new DeploymentEventContext
                {
                    StepDisplayOrder = stepSortOrder,
                    PackageIndex = i,
                    PackageCount = packageCount,
                    PackageId = pkg.PackageReferenceName,
                    PackageVersion = pkg.Version,
                    PackageFeedId = pkg.FeedId,
                    PackageError = $"Invalid FeedId: {pkg.FeedId}. FeedId must be a positive integer.",
                    Packages = new DeploymentPackageContext(
                        SelectedPackages: packages,
                        PackageId: pkg.PackageReferenceName,
                        PackageVersion: pkg.Version,
                        PackageFeedId: pkg.FeedId,
                        PackageSizeBytes: 0,
                        PackageHash: string.Empty,
                        PackageLocalPath: string.Empty,
                        PackageIndex: i,
                        PackageCount: packageCount,
                        PackageTotalSizeBytes: totalSize,
                        PackageError: $"Invalid FeedId: {pkg.FeedId}. FeedId must be a positive integer.")
                }), ct).ConfigureAwait(false);
                continue;
            }

            if (!feedById.TryGetValue(pkg.FeedId, out var feed))
            {
                Log.Error("[Deploy] Feed {FeedId} not found for package {PackageId} v{Version}", pkg.FeedId, pkg.PackageReferenceName, pkg.Version);
                await lifecycle.EmitAsync(new PackageDownloadFailedEvent(new DeploymentEventContext
                {
                    StepDisplayOrder = stepSortOrder,
                    PackageIndex = i,
                    PackageCount = packageCount,
                    PackageId = pkg.PackageReferenceName,
                    PackageVersion = pkg.Version,
                    PackageFeedId = pkg.FeedId,
                    PackageError = $"Feed {pkg.FeedId} not found",
                    Packages = new DeploymentPackageContext(
                        SelectedPackages: packages,
                        PackageId: pkg.PackageReferenceName,
                        PackageVersion: pkg.Version,
                        PackageFeedId: pkg.FeedId,
                        PackageSizeBytes: 0,
                        PackageHash: string.Empty,
                        PackageLocalPath: string.Empty,
                        PackageIndex: i,
                        PackageCount: packageCount,
                        PackageTotalSizeBytes: totalSize,
                        PackageError: $"Feed {pkg.FeedId} not found")
                }), ct).ConfigureAwait(false);
                continue;
            }

            var baseCtx = new DeploymentEventContext
            {
                StepDisplayOrder = stepSortOrder,
                PackageIndex = i,
                PackageCount = packageCount,
                PackageId = pkg.PackageReferenceName,
                PackageVersion = pkg.Version,
                PackageFeedId = pkg.FeedId,
                Packages = new DeploymentPackageContext(
                    SelectedPackages: packages,
                    PackageId: pkg.PackageReferenceName,
                    PackageVersion: pkg.Version,
                    PackageFeedId: pkg.FeedId,
                    PackageSizeBytes: 0,
                    PackageHash: string.Empty,
                    PackageLocalPath: string.Empty,
                    PackageIndex: i,
                    PackageCount: packageCount,
                    PackageTotalSizeBytes: totalSize,
                    PackageError: string.Empty)
            };

            await lifecycle.EmitAsync(new PackageDownloadingEvent(baseCtx), ct).ConfigureAwait(false);

            try
            {
                var result = await packageAcquisitionService.AcquireAsync(feed, pkg.PackageReferenceName, pkg.Version, _ctx.Deployment.Id, ct).ConfigureAwait(false);

                _ctx.AcquiredPackages[pkg.PackageReferenceName] = result;
                totalSize += result.SizeBytes;

                var downloadedCtx = new DeploymentEventContext
                {
                    StepDisplayOrder = baseCtx.StepDisplayOrder,
                    PackageIndex = baseCtx.PackageIndex,
                    PackageCount = baseCtx.PackageCount,
                    PackageId = baseCtx.PackageId,
                    PackageVersion = baseCtx.PackageVersion,
                    PackageFeedId = baseCtx.PackageFeedId,
                    PackageSizeBytes = result.SizeBytes,
                    PackageHash = result.Hash,
                    PackageLocalPath = result.LocalPath,
                    Packages = new DeploymentPackageContext(
                        SelectedPackages: packages,
                        PackageId: baseCtx.PackageId,
                        PackageVersion: baseCtx.PackageVersion,
                        PackageFeedId: baseCtx.PackageFeedId,
                        PackageSizeBytes: result.SizeBytes,
                        PackageHash: result.Hash,
                        PackageLocalPath: result.LocalPath,
                        PackageIndex: baseCtx.PackageIndex,
                        PackageCount: baseCtx.PackageCount,
                        PackageTotalSizeBytes: totalSize,
                        PackageError: string.Empty)
                };
                await lifecycle.EmitAsync(new PackageDownloadedEvent(downloadedCtx), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Deploy] Failed to acquire package {PackageId} v{Version}", pkg.PackageReferenceName, pkg.Version);
                var failedCtx = new DeploymentEventContext
                {
                    StepDisplayOrder = baseCtx.StepDisplayOrder,
                    PackageIndex = baseCtx.PackageIndex,
                    PackageCount = baseCtx.PackageCount,
                    PackageId = baseCtx.PackageId,
                    PackageVersion = baseCtx.PackageVersion,
                    PackageFeedId = baseCtx.PackageFeedId,
                    PackageError = ex.Message,
                    Packages = new DeploymentPackageContext(
                        SelectedPackages: packages,
                        PackageId: baseCtx.PackageId,
                        PackageVersion: baseCtx.PackageVersion,
                        PackageFeedId: baseCtx.PackageFeedId,
                        PackageSizeBytes: 0,
                        PackageHash: string.Empty,
                        PackageLocalPath: string.Empty,
                        PackageIndex: baseCtx.PackageIndex,
                        PackageCount: baseCtx.PackageCount,
                        PackageTotalSizeBytes: totalSize,
                        PackageError: ex.Message)
                };
                await lifecycle.EmitAsync(new PackageDownloadFailedEvent(failedCtx), ct).ConfigureAwait(false);
            }
        }

        await lifecycle.EmitAsync(new PackagesAcquiredEvent(new DeploymentEventContext
        {
            StepDisplayOrder = stepSortOrder,
            SelectedPackages = packages,
            PackageCount = packageCount,
            PackageTotalSizeBytes = totalSize,
            Packages = new DeploymentPackageContext(
                SelectedPackages: packages,
                PackageId: string.Empty,
                PackageVersion: string.Empty,
                PackageFeedId: 0,
                PackageSizeBytes: 0,
                PackageHash: string.Empty,
                PackageLocalPath: string.Empty,
                PackageIndex: 0,
                PackageCount: packageCount,
                PackageTotalSizeBytes: totalSize,
                PackageError: string.Empty)
        }), ct).ConfigureAwait(false);
    }

    private static bool IsSyntheticStep(DeploymentStepDto step)
        => step.StepType is "AcquirePackages";

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
            if (eligibility.SkipReason == ActionSkipReason.ManuallySkipped)
                await lifecycle.EmitAsync(new ActionManuallyExcludedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name }), ct).ConfigureAwait(false);
            else
                await lifecycle.EmitAsync(new ActionSkippedEvent(new DeploymentEventContext { StepDisplayOrder = stepDisplayOrder, ActionName = action.Name, ActionEligibility = eligibility }), ct).ConfigureAwait(false);
        }
    }
}

using Squid.Core.Observability;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Rendering;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Core.Services.DeploymentExecution.Transport;

namespace Squid.Core.Services.DeploymentExecution.Pipeline.Phases;

public sealed partial class ExecuteStepsPhase(
    IActionHandlerRegistry actionHandlerRegistry,
    IDeploymentLifecycle lifecycle,
    IDeploymentInterruptionService interruptionService,
    IDeploymentCheckpointService checkpointService,
    IServerTaskService serverTaskService,
    ITransportRegistry transportRegistry,
    IExternalFeedDataProvider externalFeedDataProvider,
    IPackageAcquisitionService packageAcquisitionService,
    IServiceMessageParser serviceMessageParser,
    IIntentRendererRegistry intentRendererRegistry) : IDeploymentPipelinePhase
{
    public int Order => 500;

    private DeploymentTaskContext _ctx;
    private int _currentBatchIndex;

    // P0-A.3 (2026-04-24 audit): parallel target executors write per-target completion
    // via MarkTargetCompleted → GetOrCreateBatchState. Plain Dictionary races — non-atomic
    // TryGetValue-then-Add loses mutations when two threads both see "not present".
    // ConcurrentDictionary.GetOrAdd gives us atomic one-instance-per-key semantics.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState> _batchStates = new();

    public async Task ExecuteAsync(DeploymentTaskContext ctx, CancellationToken ct)
    {
        _ctx = ctx;
        foreach (var (batch, state) in ctx.ResumeBatchStates)
            _batchStates[batch] = state;

        // Root span for the entire deployment execution. Child spans (batch,
        // step, target) attach automatically via System.Diagnostics.Activity's
        // async-local parenting. If no OTel listener is registered, this is a
        // cheap null — no allocation, no overhead.
        using var deploymentSpan = DeploymentTracing.StartDeployment(
            _ctx.ServerTaskId,
            _ctx.Deployment?.Id ?? 0,
            _ctx.Release?.Version ?? "unknown");

        try
        {
            await ExecuteDeploymentStepsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            deploymentSpan?.RecordException(ex);
            throw;
        }
    }

    private async Task ExecuteDeploymentStepsAsync(CancellationToken ct)
    {
        var orderedSteps = _ctx.Steps.OrderBy(p => p.StepOrder).ToList();
        var batches = StepBatcher.BatchSteps(orderedSteps);
        var stepSortOrderByStep = orderedSteps.ToDictionary(step => step, step => step.StepOrder);

        _currentBatchIndex = 0;

        foreach (var batch in batches)
        {
            if (_ctx.ResumeFromBatchIndex.HasValue && _currentBatchIndex <= _ctx.ResumeFromBatchIndex.Value)
            {
                Log.Information("[Deploy] Skipping batch {BatchIndex} (already completed in previous run)", _currentBatchIndex);
                _currentBatchIndex++;
                continue;
            }

            using var batchSpan = DeploymentTracing.StartBatch(_currentBatchIndex);

            var batchEntries = batch.Select(step => (Step: step, SortOrder: stepSortOrderByStep[step])).ToList();
            var batchResults = batch.Count == 1
                ? [await ExecuteStepAcrossTargetsAsync(batchEntries[0].Step, batchEntries[0].SortOrder, ct).ConfigureAwait(false)]
                : await Task.WhenAll(batchEntries.Select(entry =>
                    ExecuteStepAcrossTargetsAsync(entry.Step, entry.SortOrder, ct))).ConfigureAwait(false);

            ApplyBatchResults(batchResults);

            await PersistCheckpointAsync(_currentBatchIndex, ct).ConfigureAwait(false);

            _currentBatchIndex++;
        }
    }

    /// <summary>
    /// Maximum attempts for <see cref="PersistCheckpointAsync"/> before
    /// escalating to <c>Log.Error</c> and proceeding without checkpoint
    /// persistence. 3 attempts with exponential backoff (200ms / 600ms
    /// / 1800ms) covers transient DB hiccups (connection blip, brief
    /// lock contention) without delaying steady-state deploys noticeably.
    /// </summary>
    internal const int CheckpointPersistMaxAttempts = 3;

    /// <summary>
    /// Initial delay for the checkpoint-persist retry. Doubles on each
    /// subsequent attempt; cumulative wait under the cap is ~2.6 s.
    /// </summary>
    internal static readonly TimeSpan CheckpointPersistInitialDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// P0-5: persist the checkpoint with retry + exponential backoff.
    ///
    /// <para><b>Pre-fix</b>: a single DB write attempt; on failure the
    /// catch logged at <c>Warning</c> level and continued. A transient
    /// DB blip (connection drop, brief deadlock with concurrent writers)
    /// silently lost the checkpoint — resume after a server restart
    /// would replay batches the agent had already completed, doubling
    /// up side effects. The <c>Warning</c> level meant operators
    /// running production at <c>Error</c> threshold never saw the loss.</para>
    ///
    /// <para><b>Post-fix</b>: up to <see cref="CheckpointPersistMaxAttempts"/>
    /// attempts with exponential backoff. If ALL attempts fail, escalate
    /// to <c>Log.Error</c> so the partial-resume hazard surfaces in
    /// alerting that's typically configured at Error threshold. We still
    /// continue execution — the deploy itself isn't broken, only the
    /// crash-recovery story is degraded for this single batch.</para>
    ///
    /// <para><b>Why retry-all-exceptions</b>: the failure modes here are
    /// dominated by transient DB issues (connection drop, lock wait
    /// timeout). Permanent failures (constraint violation, disk full)
    /// are rare from this schema; a 3-attempt retry costs ≤ 2.6 s in
    /// the unlikely permanent case but recovers the much-more-common
    /// transient case for free.</para>
    /// </summary>
    private async Task PersistCheckpointAsync(int batchIndex, CancellationToken ct)
    {
        var outputVariablesJson = SerializeOutputVariables(_ctx.Variables);
        var batchStatesJson = SerializeBatchStates();

        var checkpoint = new DeploymentExecutionCheckpoint
        {
            ServerTaskId = _ctx.ServerTaskId,
            DeploymentId = _ctx.Deployment.Id,
            LastCompletedBatchIndex = batchIndex,
            FailureEncountered = _ctx.FailureEncountered,
            OutputVariablesJson = outputVariablesJson,
            BatchStatesJson = batchStatesJson,
            InFlightScriptsJson = "{}"
        };

        var delay = CheckpointPersistInitialDelay;
        Exception lastException = null;

        for (var attempt = 1; attempt <= CheckpointPersistMaxAttempts; attempt++)
        {
            try
            {
                await checkpointService.SaveAsync(checkpoint, ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancellation is not a checkpoint-persist failure. Surface
                // it to the caller's CT path, which is already handled by
                // the pipeline runner's cancel-vs-failure precedence.
                throw;
            }
            catch (Exception ex) when (attempt < CheckpointPersistMaxAttempts)
            {
                lastException = ex;
                Log.Warning(ex,
                    "[Deploy] Failed to persist checkpoint at batch {BatchIndex}, " +
                    "attempt {Attempt}/{MaxAttempts}, retrying in {Delay}ms",
                    batchIndex, attempt, CheckpointPersistMaxAttempts, delay.TotalMilliseconds);

                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }

                delay = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * 3);
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        // All retries exhausted — escalate to Error (not Warning) so the
        // failure surfaces in alerting configured at Error threshold.
        // Continue execution; the deploy is still progressing, only the
        // resume story for this batch is degraded.
        Log.Error(lastException,
            "[Deploy] Failed to persist checkpoint at batch {BatchIndex} after {MaxAttempts} attempts. " +
            "Deploy will continue, but a server restart at this point will replay batches that " +
            "completed in the current run — manual reconciliation may be needed.",
            batchIndex, CheckpointPersistMaxAttempts);
    }

    private string SerializeBatchStates()
    {
        if (_batchStates.Count == 0) return "{}";

        var keyed = _batchStates.ToDictionary(kv => kv.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), kv => kv.Value);
        return System.Text.Json.JsonSerializer.Serialize(keyed);
    }

    internal Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState GetOrCreateBatchState(int batchIndex)
    {
        // Atomic under concurrent callers — exactly one state instance per batchIndex
        // survives the race.
        return _batchStates.GetOrAdd(batchIndex, _ => new Squid.Core.Services.Deployments.Checkpoints.BatchCheckpointState());
    }

    internal void MarkTargetCompleted(int batchIndex, int machineId, bool failed)
    {
        var state = GetOrCreateBatchState(batchIndex);
        // AddCompleted / AddFailed serialise on the state's internal lock so parallel
        // writers don't corrupt the underlying HashSet.
        if (failed) state.AddFailed(machineId);
        else state.AddCompleted(machineId);
    }

    private static string SerializeOutputVariables(List<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0) return null;

        var outputVars = variables.Where(v => v.Name.StartsWith("Squid.Action.", StringComparison.OrdinalIgnoreCase)).ToList();

        if (outputVars.Count == 0) return null;

        return System.Text.Json.JsonSerializer.Serialize(outputVars);
    }

    private void ApplyBatchResults(IEnumerable<StepExecutionResult> results)
    {
        // Phase-6.5: route the per-target output-variable list through the
        // collision-aware merger instead of a blind AddRange. Default mode
        // (Warn) keeps the legacy add-all behaviour and only adds an
        // operator-visible warning; Strict drops colliding incoming writes
        // (first-writer-wins). Pinned by OutputVariableMergerTests.
        var collisionMode = Squid.Message.Hardening.EnforcementModeReader.Read(
            Squid.Core.Services.DeploymentExecution.Variables.OutputVariableMerger.EnforcementEnvVar);

        foreach (var result in results)
        {
            if (result.OutputVariables.Count > 0)
            {
                var (mergedVariables, _) = Squid.Core.Services.DeploymentExecution.Variables.OutputVariableMerger.Merge(
                    _ctx.Variables, result.OutputVariables, collisionMode);
                _ctx.Variables = mergedVariables;

                Log.Information("[Deploy] Captured {Count} output variables from batch {BatchIndex}", result.OutputVariables.Count, _currentBatchIndex);

                foreach (var v in result.OutputVariables)
                    Log.Debug("[Deploy]   {Name} = {Value}", v.Name, v.IsSensitive ? "***" : v.Value?[..Math.Min(v.Value.Length, 100)]);
            }

            _ctx.FailureEncountered |= result.Failed;
        }
    }

    private class StepExecutionResult
    {
        private readonly object _lock = new();
        public List<VariableDto> OutputVariables { get; } = new();
        public bool Failed { get; set; }
        public bool Executed { get; set; }

        public void Absorb(StepExecutionResult other)
        {
            lock (_lock)
            {
                OutputVariables.AddRange(other.OutputVariables);
                Failed |= other.Failed;
                Executed |= other.Executed;
            }
        }
    }
}

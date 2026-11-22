namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Per-target completion state for a single step batch. Persisted as JSON on
/// <see cref="Persistence.Entities.Deployments.DeploymentExecutionCheckpoint.BatchStatesJson"/>.
///
/// <para>The executor writes one entry per batch the moment any target within that
/// batch reaches a terminal state. On resume, the executor reads it to decide
/// which (step, machine) pairs still need to run.</para>
///
/// <para><b>Keyed by (step, machine)</b>: a batch may hold several steps (consecutive
/// <c>StartWithPrevious</c> steps run in parallel) and two of them may target the same
/// machine. Recording completion per machine only meant step A finishing on machine M
/// made step B on M look finished too, so after a pause B's retry was silently skipped.
/// <see cref="StepStates"/> is the source of truth; <see cref="IsTerminalFor(int,int)"/>
/// is the single place that decides "has step X finished on machine M".</para>
///
/// <para><b>Persisted format</b>: <see cref="CompletedMachineIds"/> / <see cref="FailedMachineIds"/>
/// are the pre-step-keyed fields. New writes keep them populated as the union across steps so
/// a server that predates <see cref="StepStates"/> still reads a usable (machine-level)
/// checkpoint. A state that has machine ids but no <see cref="StepStates"/> was written by that
/// older server and is <see cref="IsLegacy"/>: its machine-level entries mean "terminal for every
/// step of the batch", which is exactly what that server meant by them.
/// <see cref="ExpandLegacy"/> converts it to step-keyed entries so later writes in the same
/// batch cannot be confused with the legacy meaning.</para>
///
/// <para>P0-A.3 (2026-04-24 audit): the inner collections are mutated from parallel target
/// executors. Concurrent <c>HashSet.Add</c> corrupts the bucket array, losing entries at best and
/// throwing at worst. Public <c>Add*</c> methods go through a private lock so writes serialise.
/// Property setters stay public for <c>System.Text.Json</c> deserialisation (resume).</para>
/// </summary>
public sealed class BatchCheckpointState
{
    // Sync root for every mutation / terminal-check. Private so callers can't
    // accidentally hold it across a long call.
    private readonly object _gate = new();

    public HashSet<int> CompletedMachineIds { get; set; } = new();
    public HashSet<int> FailedMachineIds { get; set; } = new();

    /// <summary>Per-step outcome keyed by the stable snapshot step id (never the display name).</summary>
    public Dictionary<int, StepCheckpointState> StepStates { get; set; } = new();

    /// <summary>
    /// True when this state was written before step-keying existed: machine-level entries only.
    /// </summary>
    public bool IsLegacy
    {
        get
        {
            lock (_gate)
                return StepStates.Count == 0 && (CompletedMachineIds.Count > 0 || FailedMachineIds.Count > 0);
        }
    }

    /// <summary>
    /// Record that <paramref name="stepId"/> completed on <paramref name="machineId"/>. Safe to
    /// call from parallel target executors — writes serialise on the instance lock.
    /// </summary>
    public void AddCompleted(int stepId, int machineId)
    {
        lock (_gate)
        {
            GetOrAddStep(stepId).CompletedMachineIds.Add(machineId);
            CompletedMachineIds.Add(machineId);
        }
    }

    /// <summary>
    /// Record that <paramref name="stepId"/> failed on <paramref name="machineId"/>. See
    /// <see cref="AddCompleted(int,int)"/> for the concurrency contract.
    /// </summary>
    public void AddFailed(int stepId, int machineId)
    {
        lock (_gate)
        {
            GetOrAddStep(stepId).FailedMachineIds.Add(machineId);
            FailedMachineIds.Add(machineId);
        }
    }

    /// <summary>
    /// Machine-level add (applies to every step of the batch). Retained for callers that predate
    /// step-keying; the executor uses <see cref="AddCompleted(int,int)"/>.
    /// </summary>
    public void AddCompleted(int machineId)
    {
        lock (_gate) CompletedMachineIds.Add(machineId);
    }

    /// <summary>Machine-level counterpart of <see cref="AddCompleted(int)"/> for failures.</summary>
    public void AddFailed(int machineId)
    {
        lock (_gate) FailedMachineIds.Add(machineId);
    }

    /// <summary>
    /// The one decision "has step <paramref name="stepId"/> already reached a terminal state on
    /// <paramref name="machineId"/>?". A legacy state answers at machine level.
    /// </summary>
    public bool IsTerminalFor(int stepId, int machineId)
    {
        // Lock during read too — concurrent with a writer, an unlocked Contains
        // on HashSet can walk a corrupt bucket and throw InvalidOperationException.
        lock (_gate)
        {
            if (StepStates.TryGetValue(stepId, out var step) && step.IsTerminalFor(machineId)) return true;

            return StepStates.Count == 0 && IsMachineLevelTerminal(machineId);
        }
    }

    /// <summary>Machine-level check (any step). Retained for callers that predate step-keying.</summary>
    public bool IsTerminalFor(int machineId)
    {
        lock (_gate)
            return IsMachineLevelTerminal(machineId);
    }

    /// <summary>
    /// Convert a <see cref="IsLegacy"/> state to step-keyed entries: every machine-level entry
    /// becomes an entry for each of <paramref name="stepIds"/> (the steps of this batch). No-op
    /// for a state that is already step-keyed or empty.
    /// </summary>
    public void ExpandLegacy(IEnumerable<int> stepIds)
    {
        lock (_gate)
        {
            if (StepStates.Count > 0 || (CompletedMachineIds.Count == 0 && FailedMachineIds.Count == 0)) return;

            foreach (var stepId in stepIds)
            {
                var step = GetOrAddStep(stepId);
                step.CompletedMachineIds.UnionWith(CompletedMachineIds);
                step.FailedMachineIds.UnionWith(FailedMachineIds);
            }
        }
    }

    private bool IsMachineLevelTerminal(int machineId) => CompletedMachineIds.Contains(machineId) || FailedMachineIds.Contains(machineId);

    private StepCheckpointState GetOrAddStep(int stepId)
    {
        if (!StepStates.TryGetValue(stepId, out var step))
            StepStates[stepId] = step = new StepCheckpointState();

        return step;
    }
}

/// <summary>Machines on which one step completed / failed. Only mutated under <see cref="BatchCheckpointState"/>'s lock.</summary>
public sealed class StepCheckpointState
{
    public HashSet<int> CompletedMachineIds { get; set; } = new();
    public HashSet<int> FailedMachineIds { get; set; } = new();

    public bool IsTerminalFor(int machineId) => CompletedMachineIds.Contains(machineId) || FailedMachineIds.Contains(machineId);
}

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// P0-A.3 regression guard (2026-04-24 audit). Pre-fix, two data structures that
/// sit directly under parallel write pressure were not thread-safe:
///
/// <list type="bullet">
///   <item><see cref="BatchCheckpointState.CompletedMachineIds"/> / <c>FailedMachineIds</c>
///         are <see cref="System.Collections.Generic.HashSet{Int32}"/>. Concurrent
///         <c>Add</c> calls from parallel targets in the same batch corrupt the internal
///         bucket array — losing entries at best, throwing at worst. A lost-entry here
///         means resume replays a target that was already complete or never replays one
///         that failed; both produce wrong deploys.</item>
///   <item><c>ExecuteStepsPhase._batchStates</c> is a plain <c>Dictionary&lt;int,
///         BatchCheckpointState&gt;</c>. <c>GetOrCreateBatchState</c> does non-atomic
///         "TryGetValue then Add" — two parallel targets hitting a never-seen batch
///         index both allocate their own state and both overwrite each other, losing
///         any mutations that had already landed.</item>
/// </list>
///
/// <para>Fix: <c>BatchCheckpointState</c> gains thread-safe <c>AddCompleted</c> /
/// <c>AddFailed</c> methods guarded by a private lock; <c>_batchStates</c> becomes
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> with <c>GetOrAdd</c>. These tests
/// pin both under heavy parallel contention — 1000 writers for the state, 1000 calls
/// on <c>GetOrCreateBatchState</c> for the phase — and fail loudly if the fix is
/// removed.</para>
/// </summary>
public sealed class BatchCheckpointStateThreadSafetyTests
{
    [Fact]
    public void AddCompleted_Parallel_NoEntriesLost()
    {
        const int writers = 1000;
        var state = new BatchCheckpointState();

        Parallel.For(0, writers, i => state.AddCompleted(i));

        state.CompletedMachineIds.Count.ShouldBe(writers,
            customMessage:
                $"concurrent HashSet<int>.Add corrupts the bucket array and loses entries. " +
                $"Expected {writers} ids, got {state.CompletedMachineIds.Count} — the lost " +
                "ones mean resume replays terminal targets or skips still-pending ones.");

        Enumerable.Range(0, writers).All(id => state.CompletedMachineIds.Contains(id)).ShouldBeTrue(
            customMessage: "every machineId written in parallel must be recoverable afterwards");
    }

    [Fact]
    public void AddFailed_Parallel_NoEntriesLost()
    {
        const int writers = 1000;
        var state = new BatchCheckpointState();

        Parallel.For(0, writers, i => state.AddFailed(i));

        state.FailedMachineIds.Count.ShouldBe(writers,
            customMessage: "same contract as CompletedMachineIds — failed-ids must survive parallel writes");
    }

    [Fact]
    public void AddCompleted_And_AddFailed_Interleaved_NoEntriesLost()
    {
        // Mixed workload — completed and failed ids written from the same parallel
        // section. Pre-fix, the two HashSets are independent but sit inside the same
        // object, so contention on a shared state instance still corrupts both sets.
        const int writers = 500;
        var state = new BatchCheckpointState();

        Parallel.For(0, writers, i =>
        {
            if (i % 2 == 0)
                state.AddCompleted(i);
            else
                state.AddFailed(i);
        });

        state.CompletedMachineIds.Count.ShouldBe(writers / 2);
        state.FailedMachineIds.Count.ShouldBe(writers / 2);
    }

    [Fact]
    public void IsTerminalFor_DuringConcurrentWrites_NeverThrows()
    {
        // Reads happen during resume from a background thread; writes happen from
        // parallel target executors. A concurrent read on a HashSet mid-Add throws
        // InvalidOperationException. The fix's lock-protected read path must not
        // throw, even if it sees a stale value occasionally.
        const int writers = 500;
        var state = new BatchCheckpointState();

        using var cts = new CancellationTokenSource();
        var reader = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
                state.IsTerminalFor(writers - 1);
        });

        Parallel.For(0, writers, i => state.AddCompleted(i));

        cts.Cancel();

        // Pre-fix, IsTerminalFor could throw mid-write. Just letting the reader finish
        // cleanly is the success condition.
        Should.NotThrow(() => reader.GetAwaiter().GetResult(),
            customMessage: "IsTerminalFor must tolerate concurrent AddCompleted without throwing");
    }

    [Fact]
    public void ExecuteStepsPhaseGetOrCreateBatchState_SameIndex_AlwaysSameInstance()
    {
        // Pre-fix, _batchStates is a non-concurrent Dictionary. Concurrent
        // GetOrCreateBatchState(5) calls can each go "not present → allocate new
        // state → assign _batchStates[5]". The LAST writer wins; whatever the first
        // writer already mutated on its state gets silently dropped.
        //
        // Fix: ConcurrentDictionary.GetOrAdd guarantees one instance per key.
        var phase = CreatePhaseUnderTest();
        var instances = new ConcurrentBag<BatchCheckpointState>();

        Parallel.For(0, 200, _ => instances.Add(phase.GetOrCreateBatchState(42)));

        var distinct = instances.Distinct().ToList();
        distinct.Count.ShouldBe(1,
            customMessage:
                "concurrent GetOrCreateBatchState calls for the same batchIndex must return the " +
                "same instance. Multiple instances = lost-writes when the dictionary overwrites " +
                $"a partially-mutated state. Got {distinct.Count} distinct instances.");
    }

    [Fact]
    public void ExecuteStepsPhaseMarkTargetCompleted_Parallel_SameBatchIndex_AllMachineIdsPreserved()
    {
        // Full integration test: multiple parallel threads mark targets completed
        // against the same batchIndex. The combined state must contain every
        // machineId. Pre-fix, both the batchStates dictionary AND the inner HashSet
        // corrupt under this workload.
        const int machines = 500;
        var phase = CreatePhaseUnderTest();

        Parallel.For(0, machines, i => phase.MarkTargetCompleted(7, i, failed: false));

        var state = phase.GetOrCreateBatchState(7);
        state.CompletedMachineIds.Count.ShouldBe(machines,
            customMessage: $"concurrent MarkTargetCompleted writers lost entries — expected {machines}");
    }

    /// <summary>
    /// Build an <see cref="ExecuteStepsPhase"/> with nulls for every injected service.
    /// These tests only exercise <c>GetOrCreateBatchState</c> / <c>MarkTargetCompleted</c>
    /// which don't touch any of the injected services, so the null dependencies are safe.
    /// </summary>
    private static ExecuteStepsPhase CreatePhaseUnderTest()
    {
        return new ExecuteStepsPhase(
            actionHandlerRegistry: null!,
            lifecycle: null!,
            interruptionService: null!,
            checkpointService: null!,
            serverTaskService: null!,
            transportRegistry: null!,
            externalFeedDataProvider: null!,
            packageAcquisitionService: null!,
            serviceMessageParser: null!,
            intentRendererRegistry: null!);
    }
}

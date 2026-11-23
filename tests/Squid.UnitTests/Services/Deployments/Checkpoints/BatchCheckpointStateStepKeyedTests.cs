using System.Text.Json;
using System.Linq;
using Squid.Core.Services.Deployments.Checkpoints;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Completion is recorded per (step, machine), not per machine: two steps of one parallel batch can target
/// the same machine and finish differently. Also pins how a checkpoint written before step-keying (machine
/// ids only) is read after upgrade.
/// </summary>
public class BatchCheckpointStateStepKeyedTests
{
    [Fact]
    public void StepCompletedOnMachine_DoesNotMakeSiblingStepTerminalOnSameMachine()
    {
        var state = new BatchCheckpointState();

        state.AddCompleted(stepId: 1, machineId: 100);

        state.IsTerminalFor(1, 100).ShouldBeTrue();
        state.IsTerminalFor(2, 100).ShouldBeFalse("step 2 never finished on machine 100 — step 1 finishing there must not mark it done");
        state.IsTerminalFor(1, 101).ShouldBeFalse();
    }

    [Fact]
    public void StepFailedOnMachine_IsTerminalForThatStepOnly()
    {
        var state = new BatchCheckpointState();

        state.AddFailed(stepId: 2, machineId: 100);

        state.IsTerminalFor(2, 100).ShouldBeTrue();
        state.IsTerminalFor(1, 100).ShouldBeFalse();
    }

    [Fact]
    public void StepKeyedAdds_AlsoPopulateMachineLevelUnion_ForServersThatPredateStepKeying()
    {
        var state = new BatchCheckpointState();

        state.AddCompleted(1, 100);
        state.AddFailed(2, 101);

        state.CompletedMachineIds.ShouldBe(new HashSet<int> { 100 });
        state.FailedMachineIds.ShouldBe(new HashSet<int> { 101 });
        state.IsLegacy.ShouldBeFalse();
    }

    [Fact]
    public void NewFormat_RoundTrips_PerStepOutcomes()
    {
        var state = new BatchCheckpointState();
        state.AddCompleted(1, 100);
        state.AddFailed(2, 100);

        var restored = JsonSerializer.Deserialize<BatchCheckpointState>(JsonSerializer.Serialize(state));

        restored.IsLegacy.ShouldBeFalse();
        restored.IsTerminalFor(1, 100).ShouldBeTrue();
        restored.IsTerminalFor(2, 100).ShouldBeTrue();
        restored.IsTerminalFor(3, 100).ShouldBeFalse();
        restored.StepStates[1].CompletedMachineIds.ShouldBe(new HashSet<int> { 100 });
        restored.StepStates[2].FailedMachineIds.ShouldBe(new HashSet<int> { 100 });
    }

    [Fact]
    public void LegacyJson_HasNoStepStates_AndMeansTerminalForEveryStep()
    {
        var legacy = JsonSerializer.Deserialize<BatchCheckpointState>("""{"CompletedMachineIds":[100],"FailedMachineIds":[101]}""");

        legacy.IsLegacy.ShouldBeTrue();
        legacy.StepStates.ShouldBeEmpty();
        legacy.IsTerminalFor(1, 100).ShouldBeTrue("a legacy machine-level entry applies to every step of the batch");
        legacy.IsTerminalFor(2, 100).ShouldBeTrue();
        legacy.IsTerminalFor(2, 101).ShouldBeTrue();
        legacy.IsTerminalFor(2, 102).ShouldBeFalse();
    }

    [Fact]
    public void ExpandLegacy_ConvertsMachineEntriesToPerStep_SoLaterStepKeyedWritesStayDistinct()
    {
        var state = JsonSerializer.Deserialize<BatchCheckpointState>("""{"CompletedMachineIds":[100],"FailedMachineIds":[101]}""");

        state.ExpandLegacy(new[] { 1, 2 });
        state.AddCompleted(3, 102);

        state.IsLegacy.ShouldBeFalse();
        state.IsTerminalFor(1, 100).ShouldBeTrue();
        state.IsTerminalFor(2, 101).ShouldBeTrue();
        state.IsTerminalFor(3, 102).ShouldBeTrue();
        state.IsTerminalFor(3, 100).ShouldBeFalse("step 3 was not part of the legacy batch's steps; a legacy entry must not leak onto it");
        state.CompletedMachineIds.ShouldBe(new HashSet<int> { 100, 102 }, ignoreOrder: true);
    }

    [Fact]
    public void ExpandLegacy_OnStepKeyedOrEmptyState_IsNoOp()
    {
        var keyed = new BatchCheckpointState();
        keyed.AddCompleted(1, 100);
        var empty = new BatchCheckpointState();

        keyed.ExpandLegacy(new[] { 1, 2 });
        empty.ExpandLegacy(new[] { 1, 2 });

        keyed.IsTerminalFor(2, 100).ShouldBeFalse();
        empty.StepStates.ShouldBeEmpty();
    }

    [Fact]
    public void ConcurrentStepKeyedWrites_AllPreserved_AndReadsNeverThrow()
    {
        const int machines = 2000;
        var state = new BatchCheckpointState();

        Parallel.For(0, machines, i =>
        {
            state.AddCompleted(i % 3, i);
            state.IsTerminalFor(i % 3, i);
        });

        state.CompletedMachineIds.Count.ShouldBe(machines);
        state.StepStates.Values.Sum(s => s.CompletedMachineIds.Count).ShouldBe(machines);
    }
}

using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.IntegrationTests.Base;

namespace Squid.IntegrationTests.Services.Deployments.Checkpoints;

/// <summary>
/// Resume-by-ticket — integration tests for <see cref="InFlightScriptStore"/>
/// against a real Postgres checkpoint row. Covers record/clear/lookup keyed by
/// <see cref="DispatchSlot"/>, the dispatch-scoped independence (two parallel
/// steps on ONE machine must not collide), the clobber-fix (a batch-boundary save
/// must NOT wipe in-flight tickets), the ensure-row helper, the no-row fail-safe,
/// and concurrent record under the per-task lock.
/// </summary>
public class InFlightScriptStoreTests : TestBase
{
    public InFlightScriptStoreTests() : base("InFlightScriptStore", "squid_it_inflight_script")
    {
    }

    private static DispatchSlot Slot(int machineId, string step = "Step1", string action = "Action1")
        => new(machineId, step, action);

    [Fact]
    public async Task EnsureExists_ThenRecordAndClear_RoundTrips()
    {
        const int taskId = 700001;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a")).ConfigureAwait(false);
        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe("ticket-a");

        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, Slot(11))).ConfigureAwait(false);
        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task Record_MultipleMachines_AreIndependent()
    {
        const int taskId = 700002;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a")).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(22), "ticket-b")).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, Slot(11))).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBeNull();
        (await GetTicketAsync(taskId, Slot(22)).ConfigureAwait(false)).ShouldBe("ticket-b",
            customMessage: "Clearing one machine MUST NOT drop another machine's in-flight ticket.");
    }

    [Fact]
    public async Task Record_SameMachineDifferentDispatches_AreIndependent()
    {
        // The headline fix: two parallel StartWithPrevious steps targeting ONE machine
        // each record their own slot. The probe for step B must NOT find step A's
        // ticket (the machine-only key this replaced would have — making step B
        // re-attach to step A and silently skip its own script).
        const int taskId = 700008;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11, "StepA", "ActionA"), "ticket-a")).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11, "StepB", "ActionB")).ConfigureAwait(false)).ShouldBeNull(
            customMessage: "Step B's reattach probe must not match Step A's slot on the same machine — that cross-reattach is the bug this fixes.");

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11, "StepB", "ActionB"), "ticket-b")).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11, "StepA", "ActionA")).ConfigureAwait(false)).ShouldBe("ticket-a");
        (await GetTicketAsync(taskId, Slot(11, "StepB", "ActionB")).ConfigureAwait(false)).ShouldBe("ticket-b");

        // Clearing one slot leaves the sibling slot on the same machine intact.
        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, Slot(11, "StepA", "ActionA"))).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11, "StepA", "ActionA")).ConfigureAwait(false)).ShouldBeNull();
        (await GetTicketAsync(taskId, Slot(11, "StepB", "ActionB")).ConfigureAwait(false)).ShouldBe("ticket-b",
            customMessage: "Clearing one dispatch slot MUST NOT drop a sibling slot on the same machine.");
    }

    [Fact]
    public async Task BatchCheckpointSave_DoesNotClobberInFlightTickets()
    {
        // The clobber-fix: DeploymentCheckpointService.SaveAsync (batch boundary)
        // must no longer overwrite InFlightScriptsJson, or every batch save would
        // wipe tickets dispatched mid-batch and defeat resume-by-ticket.
        const int taskId = 700003;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a")).ConfigureAwait(false);

        // Simulate a batch-boundary checkpoint save (note its InFlightScriptsJson="{}").
        await Run<IDeploymentCheckpointService>(svc => svc.SaveAsync(new DeploymentExecutionCheckpoint
        {
            ServerTaskId = taskId,
            DeploymentId = 1,
            LastCompletedBatchIndex = 0,
            FailureEncountered = false,
            OutputVariablesJson = "[]",
            BatchStatesJson = "{}",
            InFlightScriptsJson = "{}"
        })).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe("ticket-a",
            customMessage: "A batch-boundary save MUST preserve in-flight tickets — InFlightScriptsJson is owned by the store.");

        // And the batch save still updated its own column.
        var row = await LoadAsync(taskId).ConfigureAwait(false);
        row.LastCompletedBatchIndex.ShouldBe(0);
    }

    [Fact]
    public async Task EnsureExists_IsIdempotent_AndDoesNotClobber()
    {
        const int taskId = 700004;
        await EnsureRowAsync(taskId).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a")).ConfigureAwait(false);

        // A second EnsureExists (e.g. a resume re-entering the phase) must be a
        // no-op — never reset the row or wipe the recorded ticket.
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBe("ticket-a");
    }

    [Fact]
    public async Task Record_WithNoCheckpointRow_IsNoOp()
    {
        // Fail-safe: no checkpoint row yet → recording is skipped (resume just
        // re-dispatches). Must not throw or create a row.
        const int taskId = 700005;

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11), "ticket-a")).ConfigureAwait(false);

        (await LoadAsync(taskId).ConfigureAwait(false)).ShouldBeNull();
        (await GetTicketAsync(taskId, Slot(11)).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentRecord_ForDistinctMachines_AllPersist()
    {
        // Parallel batch: many targets record at once. The per-task lock must
        // serialise the read-modify-write so no ticket is lost.
        const int taskId = 700006;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        var machineIds = Enumerable.Range(1, 25).ToList();

        await Task.WhenAll(machineIds.Select(id =>
            Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(id), $"ticket-{id}")))).ConfigureAwait(false);

        foreach (var id in machineIds)
            (await GetTicketAsync(taskId, Slot(id)).ConfigureAwait(false)).ShouldBe($"ticket-{id}",
                customMessage: $"Concurrent record lost machine {id}'s ticket — the per-task RMW lock is not serialising writes.");
    }

    [Fact]
    public async Task ConcurrentRecord_SameMachineDistinctSteps_AllPersist()
    {
        // The production parallel-batch shape: several steps in ONE batch each
        // dispatch to the SAME machine concurrently. Every (machine, step, action)
        // slot must survive the contended read-modify-write — none lost, none
        // overwritten by a sibling.
        const int taskId = 700009;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        var steps = Enumerable.Range(1, 25).ToList();

        await Task.WhenAll(steps.Select(i =>
            Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, Slot(11, $"Step{i}", $"Action{i}"), $"ticket-{i}")))).ConfigureAwait(false);

        foreach (var i in steps)
            (await GetTicketAsync(taskId, Slot(11, $"Step{i}", $"Action{i}")).ConfigureAwait(false)).ShouldBe($"ticket-{i}",
                customMessage: $"Concurrent same-machine dispatch lost step {i}'s slot — the per-task RMW lock or the slot key is not isolating sibling dispatches.");
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_OnOneSharedContext_DoNotThrow()
    {
        // Reproduces the production race the other tests here cannot: each of them
        // uses a FRESH Run scope per call, so concurrent ops never share a DbContext.
        // In production a parallel batch's targets all run on the ONE scoped
        // DbContext the Hangfire worker owns for the task — each target records its
        // ticket AND probes for a re-attach ticket. Before TryGetTicketAsync took
        // the per-task stripe, an ungated read raced a concurrent read/RMW on that
        // shared context and EF threw "a second operation was started on this
        // context instance". This resolves ONE store and fires the ops on it.
        const int taskId = 700007;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Should.NotThrowAsync(() => Run<IInFlightScriptStore>(async store =>
        {
            var ops = new List<Task>();

            foreach (var id in Enumerable.Range(1, 24))
            {
                ops.Add(store.RecordDispatchedAsync(taskId, Slot(id), $"ticket-{id}"));
                ops.Add(store.TryGetTicketAsync(taskId, Slot(id)));
            }

            await Task.WhenAll(ops).ConfigureAwait(false);
        })).ConfigureAwait(false);

        // Serialised RMW also means no write was lost in the contention.
        foreach (var id in Enumerable.Range(1, 24))
            (await GetTicketAsync(taskId, Slot(id)).ConfigureAwait(false)).ShouldBe($"ticket-{id}",
                customMessage: $"Concurrent read+write on one context lost machine {id}'s ticket.");
    }

    private Task EnsureRowAsync(int taskId)
        => Run<IDeploymentCheckpointService>(svc => svc.EnsureExistsAsync(taskId, deploymentId: 1));

    private Task<string> GetTicketAsync(int taskId, DispatchSlot slot)
        => Run<IInFlightScriptStore, string>(s => s.TryGetTicketAsync(taskId, slot));

    private Task<DeploymentExecutionCheckpoint> LoadAsync(int taskId)
        => Run<IDeploymentCheckpointService, DeploymentExecutionCheckpoint>(svc => svc.LoadAsync(taskId));
}

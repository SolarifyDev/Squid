using System.Linq;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.IntegrationTests.Base;

namespace Squid.IntegrationTests.Services.Deployments.Checkpoints;

/// <summary>
/// Resume-by-ticket — integration tests for <see cref="InFlightScriptStore"/>
/// against a real Postgres checkpoint row. Covers record/clear/lookup, the
/// clobber-fix (a batch-boundary save must NOT wipe in-flight tickets), the
/// ensure-row helper, the no-row fail-safe, and concurrent record under the
/// per-task lock.
/// </summary>
public class InFlightScriptStoreTests : TestBase
{
    public InFlightScriptStoreTests() : base("InFlightScriptStore", "squid_it_inflight_script")
    {
    }

    [Fact]
    public async Task EnsureExists_ThenRecordAndClear_RoundTrips()
    {
        const int taskId = 700001;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, machineId: 11, scriptTicket: "ticket-a")).ConfigureAwait(false);
        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBe("ticket-a");

        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, machineId: 11)).ConfigureAwait(false);
        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBeNull();
    }

    [Fact]
    public async Task Record_MultipleMachines_AreIndependent()
    {
        const int taskId = 700002;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, 11, "ticket-a")).ConfigureAwait(false);
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, 22, "ticket-b")).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.ClearAsync(taskId, 11)).ConfigureAwait(false);

        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBeNull();
        (await GetTicketAsync(taskId, 22).ConfigureAwait(false)).ShouldBe("ticket-b",
            customMessage: "Clearing one machine MUST NOT drop another machine's in-flight ticket.");
    }

    [Fact]
    public async Task BatchCheckpointSave_DoesNotClobberInFlightTickets()
    {
        // The clobber-fix: DeploymentCheckpointService.SaveAsync (batch boundary)
        // must no longer overwrite InFlightScriptsJson, or every batch save would
        // wipe tickets dispatched mid-batch and defeat resume-by-ticket.
        const int taskId = 700003;
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, 11, "ticket-a")).ConfigureAwait(false);

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

        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBe("ticket-a",
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
        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, 11, "ticket-a")).ConfigureAwait(false);

        // A second EnsureExists (e.g. a resume re-entering the phase) must be a
        // no-op — never reset the row or wipe the recorded ticket.
        await EnsureRowAsync(taskId).ConfigureAwait(false);

        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBe("ticket-a");
    }

    [Fact]
    public async Task Record_WithNoCheckpointRow_IsNoOp()
    {
        // Fail-safe: no checkpoint row yet → recording is skipped (resume just
        // re-dispatches). Must not throw or create a row.
        const int taskId = 700005;

        await Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, 11, "ticket-a")).ConfigureAwait(false);

        (await LoadAsync(taskId).ConfigureAwait(false)).ShouldBeNull();
        (await GetTicketAsync(taskId, 11).ConfigureAwait(false)).ShouldBeNull();
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
            Run<IInFlightScriptStore>(s => s.RecordDispatchedAsync(taskId, id, $"ticket-{id}")))).ConfigureAwait(false);

        foreach (var id in machineIds)
            (await GetTicketAsync(taskId, id).ConfigureAwait(false)).ShouldBe($"ticket-{id}",
                customMessage: $"Concurrent record lost machine {id}'s ticket — the per-task RMW lock is not serialising writes.");
    }

    private Task EnsureRowAsync(int taskId)
        => Run<IDeploymentCheckpointService>(svc => svc.EnsureExistsAsync(taskId, deploymentId: 1));

    private Task<string> GetTicketAsync(int taskId, int machineId)
        => Run<IInFlightScriptStore, string>(s => s.TryGetTicketAsync(taskId, machineId));

    private Task<DeploymentExecutionCheckpoint> LoadAsync(int taskId)
        => Run<IDeploymentCheckpointService, DeploymentExecutionCheckpoint>(svc => svc.LoadAsync(taskId));
}
